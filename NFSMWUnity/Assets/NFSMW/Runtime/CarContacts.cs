using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Dynamics;
using Unity.Collections;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// Car contact response after RigidBody (Physics/Behaviors/RigidBody.cpp; speed.exe 0x670B90-0x6BB7EC),
    /// Dynamics::Collision::Moment::React (plane 0x488830, two bodies 0x488F70) and RBVehicle::ModifyCollision
    /// (SimSurface 0x68BD60, RigidBody 0x69ABF0; RBCop = RBVehicle, its vtable 0x8AD008 jumps to both), applied through
    /// PhysX contact modification (off the main thread, so it reads only the records registered here). The car's
    /// rigidbodyspecs (RigidBodySpecs) give, per collision kind:
    /// <list type="bullet">
    /// <item><b>ground</b> (ResolveGroundCollision :721-770): friction GROUND_FRICTION {us, min(uk, us)}, bounce
    /// |n_body ⊙ GROUND_ELASTICITY|, inertia × GROUND_MOMENT_SCALE.</item>
    /// <item><b>wall</b> (ResolveWorldCollision :1227-1270): WALL_FRICTION, |n_body ⊙ WALL_ELASTICITY|, inertia ×
    /// WORLD_MOMENT_SCALE. RBVehicle: the impulse acts at the body origin's height (Moment CG y = 0) for every vehicle;
    /// for the player the bounce is 0 and the yaw inertia × 1 + 8·ramp(closing speed, 0..5)·ramp(nose dot, −1..1).</item>
    /// <item><b>object</b> (ResolveObjectCollision :1032-1164): friction √(us_A·us_B), min(that, √(uk_A·uk_B)), bounce
    /// |n_body ⊙ OBJ_ELASTICITY| each, inertia × OBJ_MOMENT_SCALE.xyz, mass × OBJ_MOMENT_SCALE.w when &gt; 0. Player vs
    /// a car with both on their wheels: CG 0.125 under the contact. Non-player vs a racer/human: the aivehicle's
    /// collisionreactions record (see below). Game breaker collision mass 2 × MASS. NO_CAR_EFFECT prop or an animating
    /// car (NIS): immobile.</item>
    /// </list>
    /// <b>Two elasticities</b> (React(Moment&amp;) 0x488F70, decompiled this round): the impulse that stops the closing
    /// speed is found with the pair's reduced mass (32 fractional steps), then each body's velocity change is scaled by
    /// (1 + its own elasticity) — they are never combined. PhysX takes one restitution per contact, so each body's
    /// inverse mass and inertia scales get × (1 + e_i) and the contact restitution is the inverse-mass weighted mean
    /// (e_A/M_A + e_B/M_B)/(1/M_A + 1/M_B): with that, PhysX's impulse gives each body exactly J(1 + e_i)/M_i.
    /// <b>Per-axis inertia</b>: React uses inertia ⊙ moment scale per axis; PhysX has one scalar per body and pair. The
    /// scalar is now chosen so the contact's angular effective mass equals the per-axis one (same impulse magnitude), and
    /// <see cref="CorrectAngular"/> puts the per-axis remainder of the angular velocity change on afterwards (for every
    /// registered car, traffic and cops included).
    /// <b>Prims</b>: a car's collision is its CollisionGeometry primitives (CollisionBounds); a PrimVsWorld-only primitive
    /// ignores bodies, a PrimVsObjects-only one ignores the world. <b>Exclusion</b>: cops (mask 0x80) and traffic (0x40)
    /// pass through scenery-group walls (0xC0, WorldCollision.ExclusionOf).
    /// </summary>
    public static class CarContacts
    {
        public enum Kind { Ground, Wall, Object }

        /// <summary>CollisionReactionRecord (MWAttribUserTypes.h:146-155).</summary>
        public struct Reaction
        {
            public float Elasticity, RollHeight, WeightBias, MassScale, StunSpeed, StunTime;
        }

        sealed class Record
        {
            public volatile bool Player, Grounded, Breaker, Animating;
            /// <summary>RBVehicle::CanCollideWithGround (RBVehicle.cpp:229-237): every wheel down, and an object hit this step.</summary>
            public volatile bool AllWheels, ObjectHit;
            public RigidBodySpecs Body;
            public Vector3 LocalCom;
            /// <summary>The body origin in car space (CollisionGeometry root pivot, or the box centre): Moment CG y = 0
            /// against walls, ChooseReaction's "position".</summary>
            public Vector3 Origin;
            public PlayerCar Car;
            public Rigidbody Rb;
            // main-thread snapshot (pre-step)
            public Vector3 Velocity, Forward;
            public float Mass = 1500f;
            public Vector3 Inertia = Vector3.one * 1000f;
            /// <summary>DriverClass: 0 human, 1 traffic, 2 cop, 3 racer (Sim/SimTypes.h:218-226).</summary>
            public volatile int DriverClass;
            public uint Mask;
            /// <summary>mPlayerReactions (REARSIDE, FRONTSIDE, FRONT, REAR; the layout order) or null (IsValid false).</summary>
            public Reaction[] Reactions;
            public AttribCollection AiVehicle;
            public uint Goal;
            public int ClassFrame = -1000;
            // RBVehicle invulnerability (0x6B7CB0 Set, 0x6B7CE0 DoPenetration, OnTaskSimulate 0x68BC80)
            public volatile int Invulnerable;
            public float InvulnerableTimer, LastPenetration;
            public volatile bool Penetrated;
        }

        sealed class Entry
        {
            public Record R;
            public bool VsWorld = true, VsObjects = true, WallsOnly, NoGround, GroundOnly;
            /// <summary>Primitive::ONESIDED: every sphere (RigidBody::AddCollisionSphere ORs 0x10), so every barrier is
            /// one-sided for it (DoBarrierCollision :1399).</summary>
            public bool OneSided;
        }

        static readonly Dictionary<int, Entry> ByCollider = new Dictionary<int, Entry>();
        static readonly Dictionary<PlayerCar, Record> ByCar = new Dictionary<PlayerCar, Record>();
        static readonly HashSet<int> DynamicBodies = new HashSet<int>();
        static readonly HashSet<int> NoCarEffectBodies = new HashSet<int>();
        /// <summary>Per (collider, other collider): the inverse inertia scale PhysX got for that body this step.</summary>
        static readonly Dictionary<long, float> Applied = new Dictionary<long, float>();
        static bool _hooked;
        const float PlayerCogModifier = -0.125f;               // Tweak_PlayerCogModifire (RBVehicle.cpp:16)
        const float HeadOnDampEnd = 5f, HeadOnDamp = 8f;      // Tweak_HeadOnDampStart 0 / End 5 / Damp 8 / Dot −1 (0x68BD60)
        /// <summary>
        /// [?] A world contact whose normal is within ~45° of vertical is "ground" (the game's ground collision tests the
        /// box corners against the road face under them, its walls are the barrier list; Unity has one contact stream).
        /// </summary>
        public const float GroundNormalY = 0.7071f;
        /// <summary>LocalPlayer Tweak_GameBreakerCollisionMass.</summary>
        public const float GameBreakerCollisionMass = 2f;
        static readonly uint GoalRacer = AttribDatabase.Hash("AIGoalRacer");

        public static void Register(PlayerCar car, Collider col, Rigidbody rb, CarSpec spec)
        {
            if (!col || spec == null || !car) return;
            Hook();
            col.hasModifiableContacts = true;
            var box = col as BoxCollider;
            var r = new Record
            {
                Player = !car.AiControlled && !car.Traffic,
                Body = spec.Body ?? new RigidBodySpecs(),
                LocalCom = rb ? rb.centerOfMass : Vector3.zero,
                Origin = box ? box.center : (rb ? rb.centerOfMass : Vector3.zero),
                Car = car,
                Rb = rb,
                Mass = rb ? rb.mass : spec.Mass,
                Inertia = rb ? rb.inertiaTensor : Vector3.one * 1000f,
            };
            LoadReactions(r, spec.Name);
            lock (ByCollider)
            {
                if (ByCar.TryGetValue(car, out var old))
                {
                    var stale = new List<int>();
                    foreach (var kv in ByCollider) if (kv.Value.R == old) stale.Add(kv.Key);
                    foreach (var k in stale) ByCollider.Remove(k);
                }
                ByCar[car] = r;
                ByCollider[col.GetInstanceID()] = new Entry { R = r };
            }
            ResolveClass(r);
        }

        /// <summary>
        /// The car's CollisionGeometry primitives (CollisionBounds.BuildVehicle): each collider joins the car's record
        /// with its PrimVsWorld / PrimVsObjects flags; origin = the body origin (root pivot) in car space.
        /// </summary>
        public static void RegisterPrims(PlayerCar car, List<CollisionBounds.Prim> prims, Vector3 origin)
        {
            if (!car) return;
            lock (ByCollider)
            {
                if (!ByCar.TryGetValue(car, out var r)) return;
                r.Origin = origin;
                foreach (var p in prims)
                {
                    if (!p.Collider) continue;
                    p.Collider.hasModifiableContacts = true;
                    ByCollider[p.Collider.GetInstanceID()] = new Entry
                    {
                        R = r, VsWorld = p.VsWorld, VsObjects = p.VsObjects, WallsOnly = p.WallsOnly, NoGround = p.NoGround, GroundOnly = p.GroundOnly,
                        OneSided = p.Collider is SphereCollider && !p.GroundOnly,
                    };
                }
            }
        }

        public static void Unregister(Collider col)
        {
            if (!col) return;
            lock (ByCollider)
            {
                if (!ByCollider.TryGetValue(col.GetInstanceID(), out var e)) return;
                var stale = new List<int>();
                foreach (var kv in ByCollider) if (kv.Value.R == e.R) stale.Add(kv.Key);
                foreach (var k in stale) ByCollider.Remove(k);
                if (e.R.Car) ByCar.Remove(e.R.Car);
            }
        }

        static void Hook()
        {
            if (_hooked) return;
            Physics.ContactModifyEvent += Modify;
            Physics.ContactModifyEventCCD += Modify;
            _hooked = true;
        }

        /// <summary>Main-thread refresh of the flags the contact callback reads (player/AI role, wheels on the ground).</summary>
        public static void Refresh(Collider col, bool player, bool grounded)
        {
            if (!col || !TryGet(col.GetInstanceID(), out var e)) return;
            var r = e.R;
            r.Player = player;
            r.Grounded = grounded;
            Snapshot(r);
        }

        /// <summary>Refresh with the game breaker flag and the pre-step velocity and heading (head-on rule).</summary>
        public static void Refresh(Collider col, bool player, bool grounded, bool breaker, Vector3 velocity, Vector3 forward)
        {
            if (!col || !TryGet(col.GetInstanceID(), out var e)) return;
            var r = e.R;
            r.Player = player;
            r.Grounded = grounded;
            r.Breaker = breaker;
            r.Velocity = velocity;
            r.Forward = forward;
            Snapshot(r);
        }

        /// <summary>
        /// RBVehicle::CanCollideWithGround's inputs: NumWheels == NumWheelsOnGround (all four down) and
        /// HasHadObjectCollision() this step. A non-player with both (all down, no object hit) skips body-vs-ground.
        /// </summary>
        public static void RefreshGround(Collider col, bool allWheelsDown, bool hadObjectCollision)
        {
            if (!col || !TryGet(col.GetInstanceID(), out var e)) return;
            e.R.AllWheels = allWheelsDown;
            e.R.ObjectHit = hadObjectCollision;
        }

        static void Snapshot(Record r)
        {
            if (r.Rb)
            {
                r.Mass = r.Rb.mass;
                r.Inertia = r.Rb.inertiaTensor;
                r.LocalCom = r.Rb.centerOfMass;
                if (r.Velocity == Vector3.zero) { r.Velocity = r.Rb.linearVelocity; r.Forward = r.Rb.transform.forward; }
            }
            if (Time.frameCount - r.ClassFrame > 30) ResolveClass(r);
        }

        /// <summary>IVehicle::GetDriverClass from the car's components (main thread), the collision mask with it.</summary>
        static void ResolveClass(Record r)
        {
            r.ClassFrame = Time.frameCount;
            var car = r.Car;
            if (!car) return;
            int cls = car.Traffic ? 1 : car.GetComponent<Game.Police.CopCar>() ? 2 : car.AiControlled ? 3 : 0;
            if (cls != r.DriverClass || r.Mask == 0 && cls != 0 && cls != 3)
            {
                r.DriverClass = cls;
                r.Mask = WorldQueries.MaskFor(cls);
                if (r.Goal == 0 && cls == 3) SelectReaction(r, GoalRacer);   // AIVehicleRacecar runs AIGoalRacer [?: until SetGoal says otherwise]
            }
            var nis = car.GetComponent<Game.NisCarControl>();
            r.Animating = nis && nis.Animating;
        }

        static bool TryGet(int id, out Entry e)
        {
            lock (ByCollider) return ByCollider.TryGetValue(id, out e);
        }

        // ---- collisionreactions (AIVehicle::SetGoal 0x422480; RBVehicle::SetPlayerReactions 0x6B7D70) ------------

        /// <summary>
        /// AIVehicle::SetGoal (0x422480) for traffic, cops and racers: the aivehicle's PlayerCollisions record whose Goal is
        /// this goal's hash gives the collisionreactions, else PlayerCollisionsDefault. E.g. cops: StaticRoadBlock →
        /// "roadblock" (mass ×2.6 head-on), Pit → 30EA49FC (front e +0.4, mass ×4); racers: AIGoalRacer → 71B9AF3D (rear
        /// corners mass ×2). [hook for the AI/police code: call on every goal change]
        /// </summary>
        public static void SetGoal(PlayerCar car, string goal)
        {
            if (!car) return;
            Record r;
            lock (ByCollider) if (!ByCar.TryGetValue(car, out r)) return;
            SelectReaction(r, string.IsNullOrEmpty(goal) ? 0u : AttribDatabase.Hash(goal));
        }

        static void LoadReactions(Record r, string pvehicle)
        {
            var db = GameData.Attributes;
            var pv = db != null && pvehicle != null ? db.Get("pvehicle", pvehicle.ToLowerInvariant()) : null;
            if (pv == null) return;
            foreach (var key in pv.Refs("aivehicle"))
                if (db.Collections.TryGetValue(key, out var ai)) { r.AiVehicle = ai; break; }
            SelectReaction(r, 0);
        }

        static void SelectReaction(Record r, uint goal)
        {
            r.Goal = goal;
            var ai = r.AiVehicle;
            var db = GameData.Attributes;
            if (ai == null || db == null) { r.Reactions = null; return; }
            (uint, uint) target = default;
            bool found = false;
            if (goal != 0 && ai.TryGet("PlayerCollisions", out int at, out int n, out _))
                for (int i = 0; i < n; i++)
                    if (Chunks.U32(ai.Source.Data, at + i * 16) == goal)
                    {
                        target = (Chunks.U32(ai.Source.Data, at + i * 16 + 4), Chunks.U32(ai.Source.Data, at + i * 16 + 8));
                        found = true;
                        break;
                    }
            if (!found && ai.TryGet("PlayerCollisionsDefault", out int dat, out _, out _))
            {
                target = (Chunks.U32(ai.Source.Data, dat), Chunks.U32(ai.Source.Data, dat + 4));
                found = true;
            }
            if (!found || !db.Collections.TryGetValue(target, out var cr)) { r.Reactions = null; return; }
            string[] fields = { "REARSIDE_REACTION", "FRONTSIDE_REACTION", "FRONT_REACTION", "REAR_REACTION" };
            var rs = new Reaction[4];
            for (int i = 0; i < 4; i++)
            {
                if (!cr.TryGet(fields[i], out int o, out _, out _)) continue;
                var d = cr.Source.Data;
                rs[i] = new Reaction
                {
                    Elasticity = Chunks.F32(d, o), RollHeight = Chunks.F32(d, o + 4), WeightBias = Chunks.F32(d, o + 8),
                    MassScale = Chunks.F32(d, o + 12), StunSpeed = Chunks.F32(d, o + 16), StunTime = Chunks.F32(d, o + 20),
                };
            }
            r.Reactions = rs;
        }

        /// <summary>RBVehicle::ChooseReaction (0x68BEF0): front/rear half by (point − origin)·fwd, side when |n·fwd| &lt; 0.707.</summary>
        static Reaction ChooseReaction(Record r, Vector3 point, Vector3 normal, Vector3 bodyPos, Quaternion rot)
        {
            var fwd = rot * Vector3.forward;
            var origin = bodyPos + rot * r.Origin;
            bool front = Vector3.Dot(point - origin, fwd) > 0f;
            bool side = Mathf.Abs(Vector3.Dot(normal, fwd)) < 0.707f;
            return r.Reactions[front ? (side ? 1 : 2) : (side ? 0 : 3)];
        }

        /// <summary>The reaction a non-player body uses against this other body (null when none applies; traffic's are
        /// TrafficContacts', so traffic is left out here).</summary>
        static bool ReactsTo(Record me, Record other) =>
            me.Reactions != null && !me.Player && me.DriverClass != 1 && other != null && (other.DriverClass == 0 || other.DriverClass == 3);

        // ---- the contact callback ------------------------------------------------------------------------------

        static void Modify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            lock (Applied) if (Applied.Count > 4096) Applied.Clear();
            for (int p = 0; p < pairs.Length; p++)
            {
                var pair = pairs[p];
                TryGet(pair.colliderInstanceID, out var ea);
                TryGet(pair.otherColliderInstanceID, out var eb);
                if (ea == null && eb == null) continue;
                var a = ea?.R;
                var b = eb?.R;
                if (a != null && a == b) continue;
                int otherBody = a != null ? pair.otherBodyInstanceID : pair.bodyInstanceID;
                bool objects = a != null && b != null;
                // a loose body (knocked prop) counts as an object; fixed scenery and smackables still standing are walls
                if (!objects) objects = otherBody != 0 && IsDynamic(otherBody);

                // primitive filters (PrimVsWorld / PrimVsObjects) and the world exclusion mask
                bool ignore = (ea != null && !(objects ? ea.VsObjects : ea.VsWorld)) || (eb != null && !(objects ? eb.VsObjects : eb.VsWorld));
                if (!ignore && !objects)
                {
                    var rec = a ?? b;
                    int world = a != null ? pair.otherColliderInstanceID : pair.colliderInstanceID;
                    if ((rec.Mask & WorldCollision.ExclusionOf(world)) != 0) ignore = true;
                }
                if (ignore)
                {
                    for (int i = 0; i < pair.contactCount; i++) pair.IgnoreContact(i);
                    pairs[p] = pair;
                    continue;
                }
                // RBVehicle::DoPenetration: an invulnerable vehicle (just reset or switched back to the full sim) passes
                // through other bodies; ResolveObjectCollision is skipped for the pair (RigidBody.cpp:1038-1050)
                if (objects && (a != null && a.Invulnerable != 0 || b != null && b.Invulnerable != 0))
                {
                    if (a != null && a.Invulnerable != 0) a.Penetrated = true;
                    if (b != null && b.Invulnerable != 0) b.Penetrated = true;
                    for (int i = 0; i < pair.contactCount; i++) pair.IgnoreContact(i);
                    pairs[p] = pair;
                    continue;
                }
                bool noCarEffect = !(a != null && b != null) && otherBody != 0 && IsNoCarEffect(otherBody);

                // friction
                float objUs = 0, objUk = 0;
                if (objects)
                {
                    var ra = (a ?? b).Body;
                    var rb = (b ?? a).Body;
                    objUs = Mathf.Sqrt(ra.ObjFrictionStatic * rb.ObjFrictionStatic);
                    objUk = Mathf.Min(objUs, Mathf.Sqrt(ra.ObjFrictionDynamic * rb.ObjFrictionDynamic));
                }

                // reactions (each non-player body against a racer/human) from the first contact
                var n0 = pair.contactCount > 0 ? pair.GetNormal(0) : Vector3.up;
                var p0 = pair.contactCount > 0 ? pair.GetPoint(0) : pair.position;
                Reaction reactA = default, reactB = default;
                bool hasA = objects && a != null && ReactsTo(a, b), hasB = objects && b != null && ReactsTo(b, a);
                if (hasA) reactA = ChooseReaction(a, p0, n0, pair.position, pair.rotation);
                if (hasB) reactB = ChooseReaction(b, p0, n0, pair.otherPosition, pair.otherRotation);

                // per body: mass and inertia multipliers, elasticity of the first contact, the per-axis scalar
                var kind0 = objects ? Kind.Object : KindOfWorld(WorldCollision.KindOf(a != null ? pair.otherColliderInstanceID : pair.colliderInstanceID), n0);
                var mp = pair.massProperties;
                float eA0 = 0, eB0 = 0, mA = 1, mB = 1;
                if (a != null)
                {
                    BodyScales(a, kind0, objects, noCarEffect, hasA, reactA, p0, n0, pair.position, pair.rotation, out float im, out float ii, out mA, out eA0);
                    mp.inverseMassScale = im;
                    mp.inverseInertiaScale = ii;
                }
                if (b != null)
                {
                    BodyScales(b, kind0, objects, noCarEffect, hasB, reactB, p0, n0, pair.otherPosition, pair.otherRotation, out float im, out float ii, out mB, out eB0);
                    mp.otherInverseMassScale = im;
                    mp.otherInverseInertiaScale = ii;
                }
                bool twoBodies = a != null && b != null;
                if (twoBodies)
                {
                    // React(Moment&) 0x488F70: each body's change × (1 + its own e) (see the class notes)
                    mp.inverseMassScale *= 1f + eA0;
                    mp.inverseInertiaScale *= 1f + eA0;
                    mp.otherInverseMassScale *= 1f + eB0;
                    mp.otherInverseInertiaScale *= 1f + eB0;
                }
                pair.massProperties = mp;
                lock (Applied)
                {
                    if (a != null) Applied[PairKey(pair.colliderInstanceID, pair.otherColliderInstanceID)] = mp.inverseInertiaScale;
                    if (b != null) Applied[PairKey(pair.otherColliderInstanceID, pair.colliderInstanceID)] = mp.otherInverseInertiaScale;
                }

                for (int i = 0; i < pair.contactCount; i++)
                {
                    var n = pair.GetNormal(i);
                    var kind = Kind.Object;
                    bool obb = false;
                    if (!objects)
                    {
                        // a walls-only primitive (the corner/mirror spheres, which hang below the road in the exe's own data)
                        // meets only the barrier list (DoBarrierCollision), never the ground mesh or props
                        bool wallsOnly = ea != null && ea.WallsOnly || eb != null && eb.WallsOnly;
                        int otherCol = a != null ? pair.otherColliderInstanceID : pair.colliderInstanceID;
                        int worldKind = WorldCollision.KindOf(otherCol);
                        kind = KindOfWorld(worldKind, n);
                        if (wallsOnly && worldKind != 2 && worldKind != 3) { pair.IgnoreContact(i); continue; }
                        // the road strips meet only the ground primitives (MeshVsGround cloud points, VSGROUND boxes:
                        // DoInstanceCollision2d :790-932); a cloud point meets nothing else
                        bool noGround = ea != null && ea.NoGround || eb != null && eb.NoGround;
                        bool groundOnly = ea != null && ea.GroundOnly || eb != null && eb.GroundOnly;
                        if (noGround && worldKind == 1 || groundOnly && worldKind != 1) { pair.IgnoreContact(i); continue; }
                        // WCollisionMgr::Collide's side rule: behind a one-sided barrier (or any barrier, for a sphere)
                        // the body passes through
                        if (worldKind == 2)
                        {
                            var recW = a ?? b;
                            var comW = a != null ? pair.position + pair.rotation * recW.LocalCom : pair.otherPosition + pair.otherRotation * recW.LocalCom;
                            var pt = pair.GetPoint(i);
                            var nT = Vector3.Dot(n, comW - pt) < 0f ? -n : n;
                            bool oneSided = ea != null && ea.OneSided || eb != null && eb.OneSided;
                            if (!WorldCollision.BarrierContactCounts(otherCol, pt, nT, oneSided)) { pair.IgnoreContact(i); continue; }
                        }
                        // RBVehicle::CanCollideWithGround (RBVehicle.cpp:229-237): a non-player with every wheel down and
                        // no object collision this step skips body-vs-ground
                        var rec = a ?? b;
                        if (kind == Kind.Ground && !rec.Player && rec.AllWheels && !rec.ObjectHit) { pair.IgnoreContact(i); continue; }
                        if (kind == Kind.Wall && worldKind == 3) obb = true;
                    }
                    float us, uk;
                    if (kind == Kind.Object) { us = objUs; uk = objUk; }
                    else
                    {
                        var body = (a ?? b).Body;
                        if (kind == Kind.Ground) { us = body.GroundFrictionStatic; uk = Mathf.Min(body.GroundFrictionDynamic, us); }
                        // a world OBB (TrackOBB) is a 1e6 kg box: ResolveWorldOBBCollision (RigidBody.cpp:1168) takes OBJ_FRICTION
                        else if (obb) { us = body.ObjFrictionStatic; uk = Mathf.Min(body.ObjFrictionDynamic, us); }
                        else { us = body.WallFrictionStatic; uk = Mathf.Min(body.WallFrictionDynamic, us); }
                    }
                    float ea_ = a != null ? ContactElasticity(a, kind, Quaternion.Inverse(pair.rotation) * n, hasA, reactA) : 0f;
                    float eb_ = b != null ? ContactElasticity(b, kind, Quaternion.Inverse(pair.otherRotation) * n, hasB, reactB) : 0f;
                    float e;
                    if (twoBodies)
                    {
                        float wa = 1f / Mathf.Max(1e-3f, mA), wb = 1f / Mathf.Max(1e-3f, mB);
                        e = (ea_ * wa + eb_ * wb) / (wa + wb);
                    }
                    else e = Mathf.Max(ea_, eb_);   // one car vs the world or a loose prop [?: the prop's own e is not known here]
                    pair.SetBounciness(i, Mathf.Clamp01(e));
                    pair.SetStaticFriction(i, us);
                    pair.SetDynamicFriction(i, uk);

                    if (kind == Kind.Object)
                    {
                        // player vs car, both on their wheels: the CG sits 0.125 under the contact height (no roll)
                        if (a != null && b != null && a.Grounded && b.Grounded)
                        {
                            var rec = a.Player ? a : b.Player ? b : null;
                            if (rec != null)
                            {
                                var pos = a.Player ? pair.position : pair.otherPosition;
                                var rot = a.Player ? pair.rotation : pair.otherRotation;
                                var pt = pair.GetPoint(i);
                                float cgY = (pos + rot * rec.LocalCom).y;
                                pair.SetPoint(i, new Vector3(pt.x, cgY - PlayerCogModifier, pt.z));
                            }
                        }
                    }
                    else if (kind == Kind.Wall)
                    {
                        // RBVehicle::ModifyCollision(SimSurface): Moment CG y = 0 (the body origin), for every vehicle
                        var rec = a ?? b;
                        var rot = a != null ? pair.rotation : pair.otherRotation;
                        pair.SetPoint(i, pair.GetPoint(i) + rot * Vector3.up * (rec.LocalCom.y - rec.Origin.y));
                    }
                }
                pairs[p] = pair;
            }
        }

        static long PairKey(int own, int other) => ((long)own << 32) ^ (uint)other;

        /// <summary>World contact kind from its normal (either sign).</summary>
        public static Kind Classify(Vector3 normal) => Mathf.Abs(normal.y) >= GroundNormalY ? Kind.Ground : Kind.Wall;

        /// <summary>
        /// The kind of a contact with a world collider: the road strips are ground (ResolveGroundCollision, whatever the
        /// face's slope), barriers and track OBBs are walls (DoBarrierCollision / DoObbCollision); any other static
        /// collider (props still standing, non-pack scenery) by its normal [?].
        /// </summary>
        public static Kind KindOfWorld(int worldKind, Vector3 normal) =>
            worldKind == 1 ? Kind.Ground : worldKind == 2 || worldKind == 3 ? Kind.Wall : Classify(normal);

        /// <summary>
        /// One body's PhysX scales for a pair: inverse mass 1/(mass multiplier), inverse inertia = the scalar that gives
        /// the first contact the exe's per-axis angular effective mass, the effective mass M (for the restitution mean)
        /// and its elasticity on the first contact.
        /// </summary>
        static void BodyScales(Record r, Kind kind, bool objects, bool noCarEffect, bool react, Reaction reaction, Vector3 point, Vector3 normal,
            Vector3 bodyPos, Quaternion rot, out float invMass, out float invInertia, out float effMass, out float e)
        {
            e = 0f;
            effMass = Mathf.Max(1f, r.Mass);
            if (objects && (noCarEffect || r.Animating)) { invMass = 0f; invInertia = 0f; effMass = 1e9f; return; }   // MakeImmobile (0x69ABF0)
            MassAndInertia(r, kind, react, reaction, out float m, out float t);
            invMass = 1f / m;
            effMass = Mathf.Max(1f, r.Mass) * m;
            // the normal out of the other surface toward this body (PhysX's sign depends on the pair order)
            var com = bodyPos + rot * r.LocalCom;
            var nIn = Vector3.Dot(normal, com - point) < 0f ? -normal : normal;
            var s = MomentScale(r, kind, nIn, rot);
            invInertia = AxisScalar(r, s, t, point, normal, bodyPos, rot);
            e = ContactElasticity(r, kind, Quaternion.Inverse(rot) * normal, react, reaction);
        }

        /// <summary>
        /// The scalar k with k·Σ c_k²/I_k = Σ c_k²/(I_k·S_k·T), c = r × n in body axes: PhysX's angular effective mass for
        /// this contact equals React's per-axis one. A central hit (c ≈ 0) falls back to 1/(median S · T).
        /// </summary>
        static float AxisScalar(Record r, Vector3 s, float t, Vector3 point, Vector3 normal, Vector3 bodyPos, Quaternion rot)
        {
            var inv = Quaternion.Inverse(rot);
            var com = bodyPos + rot * r.LocalCom;
            var c = Vector3.Cross(inv * (point - com), inv * normal);
            var I = r.Inertia;
            if (I.x <= 0f || I.y <= 0f || I.z <= 0f) return 1f / (Median(s) * t);
            float unit = c.x * c.x / I.x + c.y * c.y / I.y + c.z * c.z / I.z;
            if (unit < 1e-9f) return 1f / (Median(s) * t);
            float exe = c.x * c.x / (I.x * s.x * t) + c.y * c.y / (I.y * s.y * t) + c.z * c.z / (I.z * s.z * t);
            return exe / unit;
        }

        /// <summary>The moment scale of a contact kind, with the player's head-on yaw factor on walls (0x68BD60). The
        /// normal points out of the wall toward the car: closing speed −n·v, nose dot −n·fwd.</summary>
        static Vector3 MomentScale(Record r, Kind kind, Vector3 n, Quaternion rot)
        {
            Vector3 s = kind == Kind.Object ? r.Body.ObjMomentScale : kind == Kind.Ground ? r.Body.GroundMomentScale : r.Body.WorldMomentScale;
            if (kind == Kind.Wall && r.Player)
            {
                var fwd = rot * Vector3.forward;
                float noseDot = -Vector3.Dot(n, fwd);
                float closing = Mathf.Clamp01(-Vector3.Dot(n, r.Velocity) / HeadOnDampEnd);
                float damp = Mathf.Clamp01((noseDot + 1f) * 0.5f);
                s.y *= closing * damp * HeadOnDamp + 1f;
            }
            return s;
        }

        /// <summary>
        /// Mass multiplier and the extra inertia multiplier T (beyond the moment scale) for a kind: OBJ_MOMENT_SCALE.w,
        /// the reaction's MassScale (mass and inertia × it, 0x69ABF0), then the game breaker's collision mass
        /// (inertia × collisionMass / momentMass, mass = collisionMass; RBVehicle.cpp:212-216).
        /// </summary>
        static void MassAndInertia(Record r, Kind kind, bool react, Reaction reaction, out float massMul, out float inertiaMul)
        {
            massMul = 1f;
            inertiaMul = 1f;
            if (kind != Kind.Object) return;
            if (r.Body.ObjMassScale > 0f) massMul = r.Body.ObjMassScale;          // RigidBody.cpp:957-959
            if (react && reaction.MassScale > 0f) { massMul *= reaction.MassScale; inertiaMul *= reaction.MassScale; }
            if (r.Breaker)
            {
                inertiaMul *= GameBreakerCollisionMass / massMul;
                massMul = GameBreakerCollisionMass;
            }
        }

        /// <summary>The scalar PhysX gets when the contact gives no lever arm: the median axis scale (WORLD_MOMENT_SCALE has
        /// 1e6 on z for cars, cops and traffic, which a mean would spread over every axis). [ours]</summary>
        static float Median(Vector3 s) => Mathf.Max(Mathf.Min(s.x, s.y), Mathf.Min(Mathf.Max(s.x, s.y), s.z));

        static float ContactElasticity(Record r, Kind kind, Vector3 localNormal, bool react, Reaction reaction)
        {
            float e;
            switch (kind)
            {
                case Kind.Ground: e = RigidBodySpecs.Elasticity(localNormal, r.Body.GroundElasticity); break;
                case Kind.Wall: e = r.Player ? 0f : RigidBodySpecs.Elasticity(localNormal, r.Body.WallElasticity); break;   // head-on: player 0
                default: e = RigidBodySpecs.Elasticity(localNormal, r.Body.ObjElasticity); break;
            }
            if (react) e = Mathf.Clamp01(e + reaction.Elasticity);   // + Tweak_HeadOnElasticity 0
            return e;
        }

        static readonly List<ContactPoint> Contacts = new List<ContactPoint>(16);

        /// <summary>
        /// Main thread, from the car's OnCollisionEnter/Stay (also for traffic and cops, whose PlayerCar is disabled): the
        /// per-axis part of React. With the reported contact impulses J_i at p_i, the exe's angular change is
        /// (1 + e)·(I ⊙ S ⊙ T)⁻¹ Σ (p_i − cg_i) × J_i, with cg shifted by a collisionreactions record (RollHeight up,
        /// WeightBias forward); PhysX applied k·I⁻¹ Σ (p_i − com) × J_i. The difference goes on the angular velocity.
        /// </summary>
        public static void CorrectAngular(Rigidbody rb, Collider own, Collision c)
        {
            if (!rb || !own || rb.isKinematic || c == null || c.contactCount == 0) return;
            if (!TryGet(own.GetInstanceID(), out var me)) return;
            var r = me.R;
            Record other = null;
            bool objects = false, noCarEffect = false;
            var oc = c.collider;
            if (oc && TryGet(oc.GetInstanceID(), out var oe)) { objects = true; other = oe.R; }
            else if (c.rigidbody && IsDynamic(c.rigidbody.GetInstanceID())) { objects = true; noCarEffect = IsNoCarEffect(c.rigidbody.GetInstanceID()); }
            if (noCarEffect || objects && r.Animating) return;
            var inertia = rb.inertiaTensor;
            if (inertia.x <= 0 || inertia.y <= 0 || inertia.z <= 0) return;
            float k;
            lock (Applied)
            {
                // the scale PhysX applied for this body against that collider (any of the car's prims)
                if (!oc || !Applied.TryGetValue(PairKey(own.GetInstanceID(), oc.GetInstanceID()), out k)) k = -1f;
            }
            var rot = rb.rotation * rb.inertiaTensorRotation;
            var inv = Quaternion.Inverse(rot);
            var com = rb.worldCenterOfMass;
            bool react = objects && ReactsTo(r, other);
            Vector3 dw = Vector3.zero;
            c.GetContacts(Contacts);
            foreach (var cp in Contacts)
            {
                var j = cp.impulse;
                if (j.sqrMagnitude < 1e-8f) continue;
                // orient: the normal out of the other surface toward this body, the impulse pushing this body away
                var n = cp.normal;
                if (Vector3.Dot(n, com - cp.point) < 0) n = -n;
                if (Vector3.Dot(j, n) < 0) j = -j;
                var kind = objects ? Kind.Object : KindOfWorld(oc ? WorldCollision.KindOf(oc.GetInstanceID()) : 0, n);
                Reaction reaction = react ? ChooseReaction(r, cp.point, n, rb.position, rb.rotation) : default;
                MassAndInertia(r, kind, react, reaction, out _, out float t);
                var s = MomentScale(r, kind, n, rb.rotation);
                float e = objects && other != null ? ContactElasticity(r, kind, Quaternion.Inverse(rb.rotation) * n, react, reaction) : 0f;
                float kk = k >= 0f ? k : (1f + e) * AxisScalar(r, s, t, cp.point, n, rb.position, rb.rotation);
                var Lpx = inv * Vector3.Cross(cp.point - com, j);
                var Lexe = Lpx;
                if (react) Lexe -= Vector3.Cross(inv * (rb.rotation * new Vector3(0f, reaction.RollHeight, reaction.WeightBias)), inv * j);
                float g = 1f + e;
                dw += new Vector3(
                    g * Lexe.x / (inertia.x * s.x * t) - kk * Lpx.x / inertia.x,
                    g * Lexe.y / (inertia.y * s.y * t) - kk * Lpx.y / inertia.y,
                    g * Lexe.z / (inertia.z * s.z * t) - kk * Lpx.z / inertia.z);
            }
            if (dw.sqrMagnitude > 0) rb.angularVelocity += rot * dw;
        }

        static bool IsDynamic(int bodyId)
        {
            lock (DynamicBodies) return DynamicBodies.Contains(bodyId);
        }

        static bool IsNoCarEffect(int bodyId)
        {
            lock (NoCarEffectBodies) return NoCarEffectBodies.Contains(bodyId);
        }

        /// <summary>Loose props (knocked smackables) are objects to the cars, not walls.</summary>
        public static void SetDynamic(Rigidbody rb, bool on)
        {
            if (!rb) return;
            lock (DynamicBodies) { if (on) DynamicBodies.Add(rb.GetInstanceID()); else DynamicBodies.Remove(rb.GetInstanceID()); }
        }

        /// <summary>
        /// smackable NO_CAR_EFFECT on a loose prop: the car is immobile in that contact (RBVehicle.cpp:204-210). [hook for
        /// the Smackable owner: call with the prop's attribute when it detaches]
        /// </summary>
        public static void SetNoCarEffect(Rigidbody rb, bool on)
        {
            if (!rb) return;
            lock (NoCarEffectBodies) { if (on) NoCarEffectBodies.Add(rb.GetInstanceID()); else NoCarEffectBodies.Remove(rb.GetInstanceID()); }
        }

        /// <summary>eInvulnerablitiy (Interfaces/Simables/IRBVehicle.h:8-14).</summary>
        public const int InvulnerableNone = 0, InvulnerableManualReset = 1, InvulnerableReset = 2, InvulnerableControlSwitch = 3, InvulnerablePhysicsSwitch = 4;

        /// <summary>RBVehicle::SetInvulnerability(state, time) (0x6B7CB0).</summary>
        public static void SetInvulnerability(PlayerCar car, int state, float time)
        {
            if (!car) return;
            lock (ByCollider)
            {
                if (!ByCar.TryGetValue(car, out var r)) return;
                r.Invulnerable = state;
                r.InvulnerableTimer = time;
                r.Penetrated = false;
            }
        }

        /// <summary>RBVehicle::GetInvulnerability: DrawCar flashes the car while it is FROM_RESET / FROM_MANUAL_RESET.</summary>
        public static int GetInvulnerability(PlayerCar car)
        {
            if (!car) return 0;
            lock (ByCollider) return ByCar.TryGetValue(car, out var r) ? r.Invulnerable : 0;
        }

        /// <summary>
        /// RBVehicle::OnTaskSimulate (RBVehicle.cpp:91-98), main thread once per sim step: the timer only runs once a
        /// second has passed since the last penetrated contact; at 0 the state clears.
        /// </summary>
        public static void TickInvulnerability(PlayerCar car, float dt)
        {
            float simTime = Time.fixedTime;
            if (!car) return;
            Record r;
            lock (ByCollider) if (!ByCar.TryGetValue(car, out r)) return;
            if (r.Invulnerable == 0) return;
            if (r.Penetrated) { r.Penetrated = false; r.LastPenetration = simTime; }
            if (simTime > r.LastPenetration + 1f && (r.InvulnerableTimer -= dt) <= 0f)
            {
                r.InvulnerableTimer = 0f;
                r.Invulnerable = 0;
            }
        }

        /// <summary>IVehicle::GetDriverClass as the contact code sees it (0 human, 1 traffic, 2 cop, 3 racer; −1 unknown).</summary>
        public static int DriverClassOf(PlayerCar car)
        {
            if (!car) return -1;
            lock (ByCollider) return ByCar.TryGetValue(car, out var r) ? r.DriverClass : -1;
        }

        /// <summary>RigidBody mCollisionMask of a registered car (0 when unknown).</summary>
        public static uint CollisionMaskOf(PlayerCar car)
        {
            if (!car) return 0;
            lock (ByCollider) return ByCar.TryGetValue(car, out var r) ? r.Mask : 0u;
        }
    }
}
