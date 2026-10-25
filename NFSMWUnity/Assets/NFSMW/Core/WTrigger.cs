using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// WTrigger (World/Common/WTrigger.cpp, PC 0x766810..0x784EB9): a physical trigger volume the rigid bodies are tested
    /// against every sim frame (WTriggerManager::Update 0x781C90 from SimSystem::OnTask 0x6F0EB0). Unity-free; the frame
    /// is y-up (the port's Unity frame: x = game X, y = game Z, z = game Y) — the exe keeps its own sim frame
    /// (x = −gameY, y = gameZ, z = gameX; the GTrigger ctor 0x601790 stores fPosRadius = (−Position.y, Position.z,
    /// Position.x)), which is only an axis permutation.
    ///
    /// Layout (CARP::Trigger, 0x40 bytes): fMatRow0Width (right.xyz, w = width), fFlags (+0x10: bit 0 enabled, bits 4-7
    /// the shape, the rest below), fHeight, fEvents, fType / fIterStamp, fMatRow2Length (forward.xyz, w = length),
    /// fPosRadius (position, w = bounding radius).
    /// </summary>
    public sealed class WTrigger
    {
        /// <summary>fShape (bits 4-7 of the flags dword): 1 box (OBB), 2 sphere, 3 cylinder (xz distance and a height band).</summary>
        public enum ShapeKind { None = 0, Box = 1, Sphere = 2, Cylinder = 3 }

        // fFlags on PC (the GameCube header's bits sit one byte lower: GC 0x2 → PC 0x200, 0x800 → 0x80000, 0x8000 →
        // 0x800000, 0x40000 → 0x4000000): CheckCollideRB 0x7669F0 tests 0x80000, SubmitForFire 0x7817B0 tests 0x800000 /
        // 0x4000000, GTrigger::Enable 0x5E2C90 sets 0x100, the ctor sets `OneShot` as 0x200.
        public const uint FlagEnabled = 0x1;
        /// <summary>In the world's trigger list (GTrigger::Enable(true) / AddActivationReference 0x5E8540).</summary>
        public const uint FlagActivated = 0x100;
        /// <summary>`OneShot` (0xCE4261AC): disabled after it fires (FireEvents: GC `fFlags & 2` → Disable).</summary>
        public const uint FlagOneShot = 0x200;
        /// <summary>Set by the GTrigger ctor on every trigger (0x4810D01): shifted down 8 these are the RBVehicle trigger
        /// bits 4 (the player's car) and 8 (other vehicles) that ProcessRB tests (see <see cref="Activates"/>).</summary>
        public const uint FlagGameplay = 0x400 | 0x800;
        /// <summary>The old stand-in for "cars activate it" (see <see cref="Activates"/>); in the exe bit 16 of fFlags is
        /// body bit 0x100 after the shift, which no rigid body returns.</summary>
        public const uint FlagActivateCars = 0x10000;
        /// <summary>Silencable (GC 0x400): ignored while the manager's silencables are off.</summary>
        public const uint FlagSilencable = 0x40000;
        /// <summary>Directional (GC 0x800): fires only for a body moving along the trigger's forward (TestDirection 0x766960).</summary>
        public const uint FlagDirectional = 0x80000;
        /// <summary>Tilted box (GC 0x1000, UpdateBox: the matrix' up is not world up): MakeMatrix builds the up axis from the rows.</summary>
        public const uint FlagTilted = 0x100000;
        /// <summary>Simple bodies (traffic, cops) fire it only when owned by a player (GC 0x80, ProcessSRB) [?: PC bit].</summary>
        public const uint FlagPlayersOnlySRB = 0x8000;
        /// <summary>The entering body is kept on the fire-on-exit list; it fires again (stimulus 2) when it leaves (GC 0x8000).</summary>
        public const uint FlagFireOnExit = 0x800000;
        /// <summary>Fires at once on entering too (GC 0x40000); with neither of the two it fires at once as well.</summary>
        public const uint FlagFireOnEnter = 0x4000000;
        /// <summary>GTrigger ctor 0x601790: `fFlags = (fFlags &amp; 0xF0) | 0x4810D01` — every gameplay trigger.</summary>
        public const uint GameplayFlags = 0x4810D01;

        public Vec3 Right = new Vec3(1, 0, 0);     // fMatRow0Width.xyz
        public Vec3 Forward = new Vec3(0, 0, 1);   // fMatRow2Length.xyz (the heading)
        public Vec3 Position;                      // fPosRadius.xyz
        /// <summary>fPosRadius.w: the bounding radius used by the broad test.</summary>
        public float Radius;
        public float Width, Length, Height;        // fMatRow0Width.w, fMatRow2Length.w, fHeight (full extents)
        public uint Flags;
        public ShapeKind Shape;
        public ushort IterStamp;
        /// <summary>The owner that receives FireEvents (the GTrigger).</summary>
        public object Owner;
        public string Name;

        public bool Enabled => (Flags & FlagEnabled) != 0;
        public bool IsEnabled(bool allowSilencables) => Enabled && ((Flags & FlagSilencable) == 0 || allowSilencables);

        // IRigidBody::GetTriggerFlags bits (PC: RigidBody 0x699F60 → 0x40 unless attached to the world (status bit 1) or
        // asleep (vfunc +0x34); RBVehicle 0x69AFD0 → | 0x20, | 4 for the player's car else | 8, | 0x20000 for a COP driver;
        // SimpleRigidBody: 0x40 (0x50 explosion), | 4 player, human | 0x10000 (| 8 not the player))
        public const uint BodyRigid = 0x40, BodyVehicle = 0x20, BodyPlayer = 0x4, BodyAi = 0x8, BodyCop = 0x20000;

        /// <summary>RBVehicle::GetTriggerFlags (0x69AFD0): 0 while attached to the world or asleep, else 0x64 for the player's
        /// car, 0x68 for any other vehicle, | 0x20000 for a cop.</summary>
        public static uint VehicleTriggerFlags(bool player, bool cop, bool inactive)
        {
            if (inactive) return 0;
            uint f = BodyRigid | BodyVehicle | (player ? BodyPlayer : BodyAi);
            return cop ? f | BodyCop : f;
        }

        /// <summary>
        /// ProcessRB / ProcessSRB's activation test (0x781961-0x781979): `(fFlags &gt;&gt; 8) &amp; body.GetTriggerFlags()`.
        /// The gameplay triggers' 0x4810D01 gives 0x4810D: bit 2 (player) and bit 3 (other vehicles), so every car fires
        /// them (FlagGameplay 0xC00 is that pair). A body passing the old stand-in <see cref="FlagActivateCars"/> keeps the
        /// direct `fFlags &amp; 0x10000` test [ours: compatibility].
        /// </summary>
        public bool Activates(uint bodyFlags) =>
            bodyFlags == FlagActivateCars ? (Flags & FlagActivateCars) != 0 : ((Flags >> 8) & bodyFlags) != 0;
        public void Enable() => Flags |= FlagEnabled;
        public void Disable() => Flags &= ~FlagEnabled;

        /// <summary>WTrigger(mat, dimensions (half extents), events, flags): shape 1, height = 2·dims.y, width = 2·dims.x, length = 2·dims.z, radius = |dims|.</summary>
        public static WTrigger Box(Vec3 position, Vec3 forward, Vec3 halfExtents, uint flags)
        {
            var t = new WTrigger { Position = position, Flags = flags, Shape = ShapeKind.Box };
            t.SetHeading(forward);
            t.Height = halfExtents.Y + halfExtents.Y;
            t.Width = halfExtents.X + halfExtents.X;
            t.Length = halfExtents.Z + halfExtents.Z;
            t.Radius = V.Length(halfExtents);
            return t;
        }

        /// <summary>WTrigger(mat, radius, height, events, flags) (stripped on GC; the PC GTrigger ctor's radius branch): shape 3.</summary>
        public static WTrigger Cylinder(Vec3 position, Vec3 forward, float radius, float height, uint flags)
        {
            var t = new WTrigger { Position = position, Flags = flags, Shape = ShapeKind.Cylinder, Radius = radius, Height = height };
            t.SetHeading(forward);
            t.Width = t.Length = radius + radius;
            return t;
        }

        /// <summary>
        /// GTrigger::GTrigger (0x601790) — a gameplay record's trigger: the heading is `Rotation` (degrees about up);
        /// `Dimensions` present → box: width = Dimensions.y, length (along the heading) = Dimensions.x, height = 2·Dimensions.z
        /// (so the record's z is a half height), bounding radius = √((w/2)² + (l/2)²); else `Radius` present → cylinder
        /// (shape 3) of that radius, width = length = 2r, height = 4r; else a plane: `Width` across the heading, 1 m long,
        /// 100 m high (50 each way). Flags = 0x4810D01, plus OneShot. `toFrame` maps a game-space vector (X east, Y north,
        /// Z up) into the caller's frame (the Unity frame: (x, z, y)); the heading is mapped the same way.
        /// </summary>
        public static WTrigger FromGameplayRecord(AttribCollection c, Func<Vec3, Vec3> toFrame)
        {
            if (c == null || !c.Has("Position")) return null;
            var pos = toFrame(c.Vector3("Position"));
            float rot = c.Float("Rotation") * (float)Math.PI / 180f;
            var fwd = V.Normalize(toFrame(new Vec3((float)Math.Cos(rot), (float)Math.Sin(rot), 0f)));
            uint flags = GameplayFlags;
            if (c.Has("OneShot") && c.Bool("OneShot")) flags |= FlagOneShot;
            WTrigger t;
            if (c.Has("Dimensions"))
            {
                var d = c.Vector3("Dimensions");
                t = new WTrigger { Position = pos, Flags = flags, Shape = ShapeKind.Box, Width = d.Y, Length = d.X, Height = d.Z + d.Z };
                t.SetHeading(fwd);
                t.Radius = (float)Math.Sqrt(d.Y * 0.5f * (d.Y * 0.5f) + d.X * 0.5f * (d.X * 0.5f));
            }
            else if (c.Has("Radius"))
            {
                float r = c.Float("Radius");
                t = Cylinder(pos, fwd, r, 4f * r, flags);
            }
            else
            {
                float w = c.Float("Width");
                t = new WTrigger { Position = pos, Flags = flags, Shape = ShapeKind.Box, Width = w, Length = 1f, Height = 100f };
                t.SetHeading(fwd);
                t.Radius = (float)Math.Sqrt(w * 0.5f * (w * 0.5f) + 0.25f);
            }
            t.Name = c.Name;
            return t;
        }

        void SetHeading(Vec3 forward)
        {
            Forward = V.Normalize(forward);
            // the box matrix is a yaw about up (bRotateY of the identity by −Rotation): right = forward × up
            Right = V.Normalize(V.Cross(Forward, new Vec3(0, 1, 0)));
            if (V.LengthSq(Right) < 1e-6f) Right = new Vec3(1, 0, 0);
        }

        /// <summary>TestDirection (0x766960): the body moves along the trigger's forward (dot ≥ 0).</summary>
        public bool TestDirection(Vec3 v) => V.Dot(Forward, v) >= 0f;

        /// <summary>MakeMatrix's up axis: world up, or (tilted) right × forward orthogonalised from the rows.</summary>
        public Vec3 Up => (Flags & FlagTilted) != 0 ? V.Normalize(V.Cross(Forward, Right)) : new Vec3(0, 1, 0);

        /// <summary>UpdatePos (0x783180): moved (the grid re-registration is the manager's).</summary>
        public void UpdatePos(Vec3 p) => Position = p;

        /// <summary>
        /// UpdateBox (WTrigger.cpp:105-140; inlined on PC, no symbol): the trigger becomes a box on this matrix — shape 1,
        /// type 0, tilted flag (0x100000 on PC, GC 0x1000) while up.y &lt; 0.999, height 2·dim.y, width 2·dim.x, length
        /// 2·dim.z, bounding radius |dim|; flags and owner kept.
        /// </summary>
        public void UpdateBox(Vec3 right, Vec3 up, Vec3 forward, Vec3 position, Vec3 halfDims)
        {
            Right = right;
            Forward = forward;
            Position = position;
            Shape = ShapeKind.Box;
            if (up.Y < 0.999f) Flags |= FlagTilted; else Flags &= ~FlagTilted;
            Height = halfDims.Y + halfDims.Y;
            Width = halfDims.X + halfDims.X;
            Length = halfDims.Z + halfDims.Z;
            Radius = V.Length(halfDims);
        }
    }

    /// <summary>What WTriggerManager needs of an IRigidBody (ProcessRB / ProcessSRB / CheckCollide*).</summary>
    public struct WTriggerBody
    {
        public object Simable;          // the owner (ISimable handle); null = none (fires at once, no exit record)
        public Vec3 Position, Velocity;
        /// <summary>Half extents (IRigidBody::GetDimension) and the body axes (GetMatrix4) for the OBB test of box triggers.</summary>
        public Vec3 HalfExtents, Right, Up, Forward;
        public float Radius;            // IRigidBody::GetRadius (bounding sphere)
        /// <summary>IRigidBody::IsSimple: traffic and cops (ProcessSRB: sphere vs box, radius ≥ 1.5 m).</summary>
        public bool Simple;
        public bool OwnedByPlayer;
        /// <summary>IRigidBody::GetTriggerFlags: which triggers this body activates (0 = none).</summary>
        public uint TriggerFlags;
        public float Speed => V.Length(Velocity);
    }

    /// <summary>
    /// WTriggerManager (0x781C90 Update, 0x781840 ProcessRB, 0x781A00 ProcessSRB, 0x7669F0 / 0x766CB0 CheckCollide,
    /// 0x7817B0 SubmitForFire): once per sim frame every rigid body is tested against the triggers near it (here: every
    /// registered trigger; the exe walks the WGrid), a hit is fired with stimulus 1 (inside) — at once, or queued on the
    /// fire-on-exit list — and the list's bodies that no longer collide fire with stimulus 2 (left). Fire callbacks go to
    /// <see cref="Fire"/> (WTrigger::FireEvents 0x7668A0: the event list, gEventDynamicData.fTriggerStimulus).
    /// </summary>
    public sealed class WTriggerManager
    {
        /// <summary>FireOnExitRec {trigger, simable} (a std::set; no duplicates).</summary>
        readonly List<(WTrigger trig, object simable)> _fireOnExit = new List<(WTrigger, object)>();
        readonly List<WTrigger> _triggers = new List<WTrigger>();
        bool _silencablesEnabled = true;
        ushort _iterCount;
        /// <summary>GetCurrentStimulus: 1 while bodies are processed, 2 while the fire-on-exit list is.</summary>
        public int CurrentStimulus { get; private set; }
        /// <summary>FireEvents(trigger, simable, stimulus): the GTrigger's NotifySimableTrigger.</summary>
        public Action<WTrigger, object, int> Fire;

        public IReadOnlyList<WTrigger> Triggers => _triggers;

        /// <summary>WCollisionAssets::AddTrigger (0x783B60) — GTrigger::Enable(true).</summary>
        public void AddTrigger(WTrigger t)
        {
            if (t == null || _triggers.Contains(t)) return;
            _triggers.Add(t);
            t.Flags |= WTrigger.FlagActivated;
        }

        /// <summary>WCollisionAssets::RemoveTrigger (0x785430) + DeleteRefs (0x781BF0) — GTrigger::Enable(false) / ~WTrigger.</summary>
        public void RemoveTrigger(WTrigger t)
        {
            if (t == null) return;
            _triggers.Remove(t);
            t.Flags &= ~WTrigger.FlagActivated;
            DeleteRefs(t);
        }

        public void DeleteRefs(WTrigger t) => _fireOnExit.RemoveAll(r => r.trig == t);
        /// <summary>Restart / ClearAllFireOnExit (0x77CE50).</summary>
        public void ClearAllFireOnExit() => _fireOnExit.Clear();
        public void Restart() { _fireOnExit.Clear(); _silencablesEnabled = true; }
        public void EnableSilencables() => _silencablesEnabled = true;
        public void DisableSilencables() => _silencablesEnabled = false;
        /// <summary>Forget one simable's exit records (a trigger reset / a car that is gone) [ours].</summary>
        public void ForgetSimable(object simable) => _fireOnExit.RemoveAll(r => r.simable == simable);
        public bool IsOnExitList(WTrigger t, object simable) => _fireOnExit.Exists(r => r.trig == t && r.simable == simable);

        /// <summary>Update(dT): ProcessSRB / ProcessRB for every body (stimulus 1), then the fire-on-exit list (stimulus 2).</summary>
        public void Update(float dt, IList<WTriggerBody> bodies)
        {
            CurrentStimulus = 1;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i].Simple) ProcessSRB(bodies[i], dt);
                else ProcessRB(bodies[i], dt);
            }
            CurrentStimulus = 2;
            for (int i = 0; i < _fireOnExit.Count;)
            {
                var rec = _fireOnExit[i];
                int b = IndexOfSimable(bodies, rec.simable);
                if (b >= 0)
                {
                    bool still = bodies[b].Simple ? CheckCollideSRB(bodies[b], rec.trig, dt) : CheckCollideRB(bodies[b], rec.trig, dt);
                    if (still) { i++; continue; }
                    rec.trig.FireEventsFrom(this, rec.simable);
                }
                // a simable that is gone (ISimable::FindInstance null) is dropped without firing
                _fireOnExit.RemoveAt(i);
            }
            CurrentStimulus = 0;
        }

        static int IndexOfSimable(IList<WTriggerBody> bodies, object simable)
        {
            for (int i = 0; i < bodies.Count; i++) if (ReferenceEquals(bodies[i].Simable, simable)) return i;
            return -1;
        }

        /// <summary>SubmitForFire (0x7817B0): 0x800000 → the exit list (or fire now when the simable is gone); 0x4000000 → fire now; neither → fire now.</summary>
        public void SubmitForFire(WTrigger trig, object simable)
        {
            if ((trig.Flags & WTrigger.FlagFireOnExit) != 0)
            {
                if (simable != null)
                {
                    if (!IsOnExitList(trig, simable)) _fireOnExit.Add((trig, simable));
                }
                else trig.FireEventsFrom(this, simable);
            }
            if ((trig.Flags & WTrigger.FlagFireOnEnter) != 0) trig.FireEventsFrom(this, simable);
            if ((trig.Flags & (WTrigger.FlagFireOnEnter | WTrigger.FlagFireOnExit)) == 0) trig.FireEventsFrom(this, simable);
        }

        /// <summary>ProcessRB (0x781840): every enabled trigger near the body that its trigger flags activate and that it collides with.</summary>
        public void ProcessRB(in WTriggerBody body, float dt)
        {
            _iterCount++;
            uint activate = body.TriggerFlags;
            if (activate == 0) return;
            float reach = body.Radius + body.Speed * dt;
            for (int i = 0; i < _triggers.Count; i++)
            {
                var trig = _triggers[i];
                if (trig.IterStamp == _iterCount) continue;
                trig.IterStamp = _iterCount;
                if (!NearGrid(body.Position, reach, trig)) continue;
                if (trig.IsEnabled(_silencablesEnabled) && trig.Activates(activate) && CheckCollideRB(body, trig, dt))
                    SubmitForFire(trig, body.Simable);
            }
        }

        /// <summary>ProcessSRB (0x781A00): simple bodies, radius at least 1.5 m, players-only triggers skipped for others.</summary>
        public void ProcessSRB(in WTriggerBody body, float dt)
        {
            _iterCount++;
            uint activate = body.TriggerFlags;
            if (activate == 0) return;
            float reach = Math.Max(1.5f, body.Radius) + body.Speed * dt;
            for (int i = 0; i < _triggers.Count; i++)
            {
                var trig = _triggers[i];
                if (trig.IterStamp == _iterCount) continue;
                trig.IterStamp = _iterCount;
                if (!NearGrid(body.Position, reach, trig)) continue;
                if (!trig.IsEnabled(_silencablesEnabled) || !trig.Activates(activate)) continue;
                if (!body.OwnedByPlayer && (trig.Flags & WTrigger.FlagPlayersOnlySRB) != 0) continue;
                if (CheckCollideSRB(body, trig, dt)) SubmitForFire(trig, body.Simable);
            }
        }

        /// <summary>The WGrid::FindNodes stand-in: a trigger whose bounding circle is anywhere near the body (xz, generous) [ours].</summary>
        static bool NearGrid(Vec3 p, float reach, WTrigger t)
        {
            float r = reach + t.Radius + 50f;
            return V.DistanceSqXZ(p, t.Position) <= r * r;
        }

        /// <summary>
        /// CheckCollideRB (0x7669F0): the body a frame ahead (p + v·dT) against the trigger: sphere → centre distance within
        /// (r + |v|·dT + trigger radius); else the xz distance within that, then a cylinder's height band
        /// (p.y ± r against centre.y ± h/2) or a box's OBB intersection; directional triggers also need TestDirection(v).
        /// </summary>
        public bool CheckCollideRB(in WTriggerBody body, WTrigger trig, float dt)
        {
            float rb = body.Radius;
            float rads = rb + body.Speed * dt + trig.Radius;
            float radsSq = rads * rads;
            var dp = V.Scale(body.Velocity, dt);
            var rPos = V.Add(body.Position, dp);
            var cp = trig.Position;
            if (trig.Shape == WTrigger.ShapeKind.Sphere)
            {
                if (V.DistanceSq(cp, rPos) <= radsSq)
                    return (trig.Flags & WTrigger.FlagDirectional) == 0 || trig.TestDirection(body.Velocity);
                return false;
            }
            if (V.DistanceSqXZ(cp, rPos) > radsSq) return false;
            if ((trig.Flags & WTrigger.FlagDirectional) != 0 && !trig.TestDirection(body.Velocity)) return false;
            if (trig.Shape == WTrigger.ShapeKind.Cylinder)
                return rPos.Y + rb >= trig.Position.Y - trig.Height * 0.5f && rPos.Y - rb < trig.Position.Y + trig.Height * 0.5f;
            if (trig.Shape == WTrigger.ShapeKind.Box)
            {
                // Dynamics::Collision::Geometry(BOX) of the car (its matrix, dims, dP) against the trigger's box (MakeMatrix)
                var carRight = V.LengthSq(body.Right) > 0 ? body.Right : new Vec3(1, 0, 0);
                var carUp = V.LengthSq(body.Up) > 0 ? body.Up : new Vec3(0, 1, 0);
                var carFwd = V.LengthSq(body.Forward) > 0 ? body.Forward : new Vec3(0, 0, 1);
                var carHalf = body.HalfExtents;
                if (V.LengthSq(carHalf) < 1e-6f) carHalf = new Vec3(rb * 0.5f, rb * 0.35f, rb);
                // the moving box: its extent along the velocity grows by |dP| (the swept geometry) [?: FindIntersection's sweep]
                var trigHalf = new Vec3(trig.Width * 0.5f, trig.Height * 0.5f, trig.Length * 0.5f);
                return ObbIntersect(rPos, carRight, carUp, carFwd, carHalf, dp, trig.Position, trig.Right, trig.Up, trig.Forward, trigHalf);
            }
            return false;
        }

        /// <summary>CheckCollideSRB (0x766CB0): simple bodies — box: a sphere of the body's radius against the OBB; sphere / cylinder as above (strict &lt;).</summary>
        public bool CheckCollideSRB(in WTriggerBody body, WTrigger trig, float dt)
        {
            float sr = body.Radius;
            var dv = V.Scale(body.Velocity, dt);
            var rPos = V.Add(body.Position, dv);
            var cp = trig.Position;
            float rads = sr + body.Speed * dt + trig.Radius;
            float radsSq = rads * rads;
            switch (trig.Shape)
            {
                case WTrigger.ShapeKind.Box:
                    if ((trig.Flags & WTrigger.FlagDirectional) != 0 && !trig.TestDirection(body.Velocity)) return false;
                    return SphereObbIntersect(rPos, sr, dv, trig.Position, trig.Right, trig.Up, trig.Forward, new Vec3(trig.Width * 0.5f, trig.Height * 0.5f, trig.Length * 0.5f));
                case WTrigger.ShapeKind.Sphere:
                    if (V.DistanceSq(cp, rPos) < radsSq)
                        return (trig.Flags & WTrigger.FlagDirectional) == 0 || trig.TestDirection(body.Velocity);
                    return false;
                case WTrigger.ShapeKind.Cylinder:
                    if (V.DistanceSqXZ(cp, rPos) < radsSq)
                    {
                        if ((trig.Flags & WTrigger.FlagDirectional) != 0 && !trig.TestDirection(body.Velocity)) return false;
                        return rPos.Y + sr >= trig.Position.Y - trig.Height * 0.5f && rPos.Y - sr < trig.Position.Y + trig.Height * 0.5f;
                    }
                    return false;
            }
            return false;
        }

        /// <summary>GetIntersectingTriggers (0x784C50): the triggers whose bounding circle (xz) overlaps (pt, radius).</summary>
        public void GetIntersectingTriggers(Vec3 pt, float radius, List<WTrigger> into)
        {
            _iterCount++;
            foreach (var t in _triggers)
            {
                if (t.IterStamp == _iterCount) continue;
                t.IterStamp = _iterCount;
                float r = radius + t.Radius;
                if (V.DistanceSqXZ(pt, t.Position) < r * r) into.Add(t);
            }
        }

        // ---- Dynamics::Collision::Geometry::FindIntersection (0x48C100) stand-ins: separating axes [ours: the exe's routine
        // is the physics module's; a box against a box on the 15 SAT axes, with the car's motion added to its extents] -----

        static bool ObbIntersect(Vec3 pa, Vec3 ar, Vec3 au, Vec3 af, Vec3 ha, Vec3 sweep, Vec3 pb, Vec3 br, Vec3 bu, Vec3 bf, Vec3 hb)
        {
            var axesA = new[] { ar, au, af };
            var axesB = new[] { br, bu, bf };
            var d = V.Sub(pb, pa);
            float ProjA(Vec3 axis) => Math.Abs(V.Dot(ar, axis)) * ha.X + Math.Abs(V.Dot(au, axis)) * ha.Y + Math.Abs(V.Dot(af, axis)) * ha.Z + Math.Abs(V.Dot(sweep, axis)) * 0.5f;
            float ProjB(Vec3 axis) => Math.Abs(V.Dot(br, axis)) * hb.X + Math.Abs(V.Dot(bu, axis)) * hb.Y + Math.Abs(V.Dot(bf, axis)) * hb.Z;
            bool Separated(Vec3 axis)
            {
                if (V.LengthSq(axis) < 1e-8f) return false;
                axis = V.Normalize(axis);
                return Math.Abs(V.Dot(d, axis)) > ProjA(axis) + ProjB(axis);
            }
            for (int i = 0; i < 3; i++) if (Separated(axesA[i]) || Separated(axesB[i])) return false;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (Separated(V.Cross(axesA[i], axesB[j]))) return false;
            return true;
        }

        static bool SphereObbIntersect(Vec3 c, float r, Vec3 sweep, Vec3 pb, Vec3 br, Vec3 bu, Vec3 bf, Vec3 hb)
        {
            var d = V.Sub(c, pb);
            float lx = V.Dot(d, br), ly = V.Dot(d, bu), lz = V.Dot(d, bf);
            // the swept sphere: the box grows by the motion along each axis
            float ex = hb.X + Math.Abs(V.Dot(sweep, br)) * 0.5f, ey = hb.Y + Math.Abs(V.Dot(sweep, bu)) * 0.5f, ez = hb.Z + Math.Abs(V.Dot(sweep, bf)) * 0.5f;
            float qx = Clamp(lx, -ex, ex), qy = Clamp(ly, -ey, ey), qz = Clamp(lz, -ez, ez);
            float dx = lx - qx, dy = ly - qy, dz = lz - qz;
            return dx * dx + dy * dy + dz * dz <= r * r;
        }

        static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
    }

    /// <summary>WTrigger::FireEvents (0x7668A0) as a manager callback: the owner is told with the current stimulus; OneShot disables.</summary>
    public static class WTriggerFire
    {
        public static void FireEventsFrom(this WTrigger trig, WTriggerManager mgr, object simable)
        {
            mgr.Fire?.Invoke(trig, simable, mgr.CurrentStimulus);
            if ((trig.Flags & WTrigger.FlagOneShot) != 0) trig.Disable();
        }
    }

    /// <summary>Small Vec3 maths for the Unity-free trigger code.</summary>
    public static class V
    {
        public static Vec3 Add(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 Sub(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 Scale(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static float LengthSq(Vec3 a) => Dot(a, a);
        public static float Length(Vec3 a) => (float)Math.Sqrt(Dot(a, a));
        public static Vec3 Normalize(Vec3 a) { float l = Length(a); return l > 1e-9f ? Scale(a, 1f / l) : a; }
        public static float DistanceSq(Vec3 a, Vec3 b) => LengthSq(Sub(a, b));
        public static float DistanceSqXZ(Vec3 a, Vec3 b) { float x = a.X - b.X, z = a.Z - b.Z; return x * x + z * z; }
    }
}
