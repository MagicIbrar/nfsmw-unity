using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// A breakable world prop: one SmokeableSpawner record (chunk 0x34027) over its static instance, run as speed.exe runs
    /// it (specs/traffic_physics.md §10; addresses below). The prop is static scenery with a box (SmackableTrigger 0x667E70:
    /// the bounds half extents, centred on the instance) until an awake body touches it; ESpawnSmackable (dtor 0x63B790)
    /// then creates the Smackable sim object (Construct 0x6895A0, ctor 0x688660) with an RBSmackable body (SimpleRigidBody
    /// for SimplePhysics), and the static instance is hidden while the prop draws dynamically (OnBeginDraw 0x677830).
    /// <list type="bullet">
    /// <item>Body: half = max(bounds half, 0.025); I = MASS·((2hy)²+(2hz)², (2hx)²+(2hz)², (2hx)²+(2hy)²)/6 × MOMENT
    /// (each component &gt; 0). Its rigidbodyspecs (the smackable's RefSpec; "smackable" for 156 of 181) give GRAVITY
    /// (−13 … −40), DoDrag, ShouldSleep and the collision switches (see Integrate, ModifyContacts); a simplified body
    /// (SimpleRigidBody, flags 0x20B) is undamped and collisionless.</item>
    /// <item>Made only when the spawner's Collision key names a track collection (sub_681540; 126 spawners have none).
    /// The SmackableTrigger (ctor 0x667E70: WTrigger box, flags (0x40143 | extra 0) &lt;&lt; 8 | 0x10) fires for any body
    /// whose GetTriggerFlags shares a bit with 0x40143, i.e. every awake, unattached rigid body (0x40); a simplified prop
    /// (CAN_HIT_TRIGGER clear) fires none, and props (0x40 / +4) never match the gameplay triggers' 0x4810D.</item>
    /// <item>DETACH_FORCE (OnBehaviorChange 0x684DF0, RigidBody::AttachedToWorld 0x697020, Moment::React 0x488F70): a virgin
    /// prop with DETACH_FORCE ≠ 0 is attached to the world (immobile) and breaks loose when the other body's mass × the
    /// normal closing speed exceeds DETACH_FORCE (then WORLD_DETACHED, RigidBody::Detach 0x697050); &lt; 0 never breaks from
    /// contacts, only from forces (explosions); 0 or not virgin: a free body from the first touch.</item>
    /// <item>Rest logic (10 Hz task, RestLogic 0x670A30): at rest for three ticks a prop that never broke loose goes back to
    /// static scenery (RestoreScene 0x6778F0); a loose one on the ground freezes where it lies (PlaceTrigger, non-virgin)
    /// and keeps drawing there; DROPOUT {time, sink speed} sinks it away (0x66A0B0); off the world for 1 s it is released
    /// (0x66A170).</item>
    /// <item>KILL_OFF_SCREEN (model 0x66AF60): from the first smack, that many seconds unseen releases the model (the prop is
    /// gone). Gone props stay gone until their section is reloaded more than 180 s after it was unloaded, never while
    /// actively racing (section OnLoad 0x6828D0). RESPAWN_TIME is not read by the exe.</item>
    /// <item>Limits (manager 0x685000): more than 20 RBSmackables → the highest priority one (CAN_SIMPLIFY·(1 in view, 2
    /// not) + distance to the camera) / radius is simplified into a collisionless falling body (Simplify 0x684C60),
    /// as AUTO_SIMPLIFY does after that many seconds.</item>
    /// <item>Event sequencer (pursuit breakers, specs/gameplay_core.md §12): IListener::OnCollision 0x6772C0 → Impact
    /// 0x669E30 → SendImpacts 0x669D70: closing speed ≥ 1 m/s; ground hits to GROUND_COLLISION (severity v·0.1), world hits
    /// to WORLD_COLLISION and object hits to OBJECT_COLLISION (+ PLAYER_COLLISION for the player, VEHICLE_COLLISION for a
    /// vehicle) with severity v·0.022369863 (mph/100); IMPACT_1..IMPACT_(⌊6·clamp01(sev)⌋+1) to that system only. The
    /// event context position is the last impact point. OnExplosion 0x677100 (ExplosionEffect &gt; 0): pushed to the
    /// expansion speed × ExplosionEffect and EXPLOSION. no_trigger / start_sequencer start the sequencer at restore.</item>
    /// </list>
    /// [ours] The Unity collider stands in for both the trigger and the attached body; the cars' own contacts are resolved
    /// by PhysX against the fixed box, so a break hands the car back its normal speed minus the inelastic exchange.
    /// </summary>
    public sealed class Smackable : MonoBehaviour
    {
        public const uint ClassKey = 0xCE70D7DB;

        public string Kind;
        public float Mass = 100f, DetachForce, KillOffScreen = 8f, ExplosionEffect = 1f, AutoSimplify, DropoutTime, DropoutSpeed;
        public Vector3 Moment = new Vector3(1.5f, 1.5f, 0f);
        public int CostToState, CanSimplify = 50;
        /// <summary>NO_CAR_EFFECT: a car hitting the loose prop keeps its speed (RBVehicle::ModifyCollision 0x69AE0E); a car
        /// takes no damage from it (DamageVehicle.cpp:158-163).</summary>
        public bool NoCarEffect;
        public bool SimplePhysics, AllowOffWorld, NoTrigger;
        /// <summary>stringhash32 of the `EventSequencer` engine name (0 = none) and `start_sequencer`.</summary>
        public uint SequencerHash;
        public bool StartSequencer;

        /// <summary>The Smackable sim object exists (spawned by a touch), and its body is still attached to the world.</summary>
        public bool Spawned => _spawned;
        public bool AttachedToWorld => _attached;
        /// <summary>Released by KILL_OFF_SCREEN / off-world / dropout: gone until the section reloads.</summary>
        public bool Gone => _gone;

        bool _spawned, _attached, _virgin = true, _gone, _smacked, _simple, _dropping, _hadObjectHit;
        float _age, _restTimer = 0.125f, _dropTimer, _offWorldTimer, _offScreen, _task, _priority;
        Vector3 _homePos, _lastImpact;
        bool _haveImpact;
        Quaternion _homeRot;
        Rigidbody _rb;
        BoxCollider _box;
        Renderer _renderer;
        EventSeqEngine _seq;
        bool _byPlayer;
        Rigidbody _lastBody;
        float _lastBodyTime = -1f;
        Vector3Int _key;
        Specs _specs = Specs.Default;
        Vector3 _half;
        float _radius;
        int _contactPoints, _contactPointsPrev;

        /// <summary>Smackables with an event sequencer, for EProcessAreaStimulus / ESpawnExplosion lookups.</summary>
        static readonly List<Smackable> Sequenced = new List<Smackable>();
        /// <summary>Every live prop (for explosions) and the complex (RBSmackable) bodies for the 20 limit.</summary>
        static readonly List<Smackable> All = new List<Smackable>();
        static readonly List<Smackable> Complex = new List<Smackable>();
        /// <summary>gRBSmackableCount limit (Construct 0x6895A0: ≥ 20 simplifies one first).</summary>
        public const int MaxComplex = 20;
        /// <summary>TheSmokeableSections: props hidden at section unload, with the unload time (section OnUnload 0x67BF80).</summary>
        static readonly Dictionary<Vector3Int, float> Smashed = new Dictionary<Vector3Int, float>();
        const float SectionRespawnTime = 180f;

        /// <summary>An EMomentStrm fired by a prop's sequence (moment name, position, caused by the player) [ours: hook for the
        /// camera / audio moments].</summary>
        public static event System.Action<string, Vector3, bool> MomentFired;

        /// <summary>Turns a streamed prop into a smackable using the spawner's attribute collection.</summary>
        public static Smackable Make(GameObject go, Mesh mesh, AttribCollection attrs, uint collisionKey = 0)
        {
            // sub_681540: the smackable is made only when the spawner's Collision key names a track collection with
            // non-zero half dims (6 keys / 126 of 13,484 spawners have none: those props stay plain models). Until the
            // track pack is read the prop is made anyway [ours].
            if (collisionKey != 0 && Dynamics.CollisionBounds.TrackLoaded && !Dynamics.CollisionBounds.SceneryBox(collisionKey, out _, out _)) return null;
            var s = go.AddComponent<Smackable>();
            s._specs = Specs.For(attrs);
            if (attrs != null)
            {
                s.Kind = attrs.Name;
                s.Mass = attrs.Float("MASS", 0, 100f);
                s.DetachForce = attrs.Float("DETACH_FORCE", 0, 0f);
                if (attrs.Has("MOMENT")) s.Moment = new Vector3(attrs.Float("MOMENT", 0, 0f), attrs.Float("MOMENT", 1, 0f), attrs.Float("MOMENT", 2, 0f));
                s.KillOffScreen = attrs.Float("KILL_OFF_SCREEN", 0, 0f);
                s.CostToState = attrs.Has("COST_TO_STATE") ? attrs.Int("COST_TO_STATE") : 0;
                s.NoCarEffect = attrs.Has("NO_CAR_EFFECT") && attrs.Bool("NO_CAR_EFFECT");
                s.ExplosionEffect = attrs.Float("ExplosionEffect", 0, 0f);
                s.AutoSimplify = attrs.Float("AUTO_SIMPLIFY", 0, 0f);
                s.CanSimplify = attrs.Has("CAN_SIMPLIFY") ? attrs.Int("CAN_SIMPLIFY") : 0;
                s.SimplePhysics = attrs.Has("SimplePhysics") && attrs.Bool("SimplePhysics");
                s.AllowOffWorld = attrs.Has("ALLOW_OFF_WORLD") && attrs.Bool("ALLOW_OFF_WORLD");
                s.NoTrigger = attrs.Has("no_trigger") && attrs.Bool("no_trigger");
                // DROPOUT {duration, sink speed}: used only when both are > 0 (sub_669C00)
                float dd = attrs.Float("DROPOUT", 0, 0f), ds = attrs.Float("DROPOUT", 1, 0f);
                if (dd > 0f && ds > 0f) { s.DropoutTime = dd; s.DropoutSpeed = ds; }
                // EventSequencer: Attrib::StringKey {u64 hash64, u32 hash32, char*}; the engine is found by hash32
                if (attrs.TryGet("EventSequencer", out int at, out _, out _) && at + 12 <= attrs.Source.Data.Length)
                    s.SequencerHash = Chunks.U32(attrs.Source.Data, at + 8);
                s.StartSequencer = attrs.Has("start_sequencer") && attrs.Bool("start_sequencer");
            }
            s._box = go.AddComponent<BoxCollider>();
            var b = mesh.bounds;
            s._box.center = b.center;
            s._box.size = Vector3.Max(b.size, new Vector3(0.2f, 0.2f, 0.2f));
            // the spawner's collision geometry (Bounds.cpp, physics.md §22) when it has one; the render bounds otherwise
            if (collisionKey != 0 && Dynamics.CollisionBounds.SceneryBox(collisionKey, out var cc, out var cs)) { s._box.center = cc; s._box.size = cs; }
            s._box.sharedMaterial = s._specs.Material;
            s._box.hasModifiableContacts = true;
            s._renderer = go.GetComponent<Renderer>();
            s._homePos = go.transform.localPosition;
            s._homeRot = go.transform.localRotation;
            s._key = Vector3Int.RoundToInt(go.transform.position * 2f);
            All.Add(s);
            if (s.SequencerHash != 0 && WorldSequences.Find(s.SequencerHash) != null) Sequenced.Add(s);
            else s.SequencerHash = 0;
            // the section's hidden bits: smashed before the section unloaded, and not yet respawned (OnLoad 0x6828D0)
            if (Smashed.TryGetValue(s._key, out float at2))
            {
                if (!ActivelyRacing && Time.time > at2 + SectionRespawnTime) Smashed.Remove(s._key);
                else { s.HideStatic(); return s; }
            }
            s.RestoreScene();
            return s;
        }

        /// <summary>GRaceStatus mActivelyRacing [?: a race past its countdown].</summary>
        static bool ActivelyRacing => Game.RaceManager.Active != null && !Game.RaceManager.Active.Countdown;

        void OnDestroy()
        {
            Sequenced.Remove(this);
            All.Remove(this);
            Complex.Remove(this);
            if (_rb) { CarContacts.SetDynamic(_rb, false); CarContacts.SetNoCarEffect(_rb, false); }
            if (_box) SetContactRules(_box, 0);
            // section OnUnload: the bit is set when the static instance is hidden (the prop was smacked or released)
            if ((_smacked || _gone) && !ApplicationQuitting) Smashed[_key] = Time.time;
        }

        /// <summary>World_RestoreProps (0x74D320) / ResetPropTimers: every section's hidden bits cleared (a new world).</summary>
        public static void RestoreAll() => Smashed.Clear();

        static bool ApplicationQuitting;
        void OnApplicationQuit() => ApplicationQuitting = true;

        // ---- scenery model side (SceneryModel 0x67BC20) ----

        /// <summary>RestoreScene (0x6778F0): back in place as static scenery with a virgin trigger; no_trigger props keep the
        /// trigger off and start their sequencer, start_sequencer ones start it too.</summary>
        void RestoreScene()
        {
            KillSim();
            transform.localPosition = _homePos;
            transform.localRotation = _homeRot;
            if (_renderer) _renderer.enabled = true;
            if (_box) _box.enabled = true;
            _virgin = true;
            _smacked = false;
            _offScreen = 0f;
            _seq = null;
            _byPlayer = false;
            _haveImpact = false;
            if (NoTrigger || StartSequencer) EnsureSequencer();
        }

        void HideStatic()
        {
            _gone = true;
            if (_renderer) _renderer.enabled = false;
            if (_box) _box.enabled = false;
        }

        /// <summary>IModel::ReleaseModel (0x66B630): the prop is gone (the static instance stays hidden).</summary>
        void ReleaseModel()
        {
            KillSim();
            HideStatic();
        }

        // ---- the sim object ----

        /// <summary>
        /// ESpawnSmackable → Smackable::Construct: refused at the limits (more than 20 complex bodies and none could be
        /// simplified) or MASS ≤ 1e-6; the prop then stays static and the trigger re-arms.
        /// </summary>
        bool Spawn()
        {
            if (_spawned) return true;
            if (_gone || Mass <= 1e-6f) return false;
            bool simple = SimplePhysics;
            if (!simple && Complex.Count >= MaxComplex && !SimplifyOne()) return false;
            _spawned = true;
            _simple = simple;
            _age = 0f;
            _restTimer = 0.125f;
            _dropping = false;
            _offWorldTimer = 0f;
            // OnBeginDraw: the static instance is hidden (the same mesh draws dynamically here), KILL_OFF_SCREEN starts
            _smacked = true;
            _offScreen = KillOffScreen;
            EnsureSequencer();
            // OnBehaviorChange: a virgin prop with DETACH_FORCE ≠ 0 is attached to the world
            _attached = !simple && _virgin && DetachForce != 0f;
            if (!_attached) MakeBody();
            if (!simple) Complex.Add(this);
            return true;
        }

        /// <summary>The loose body: RBSmackable (or SimpleRigidBody: no collisions, damped, owner gravity).</summary>
        void MakeBody()
        {
            if (_rb) return;
            // [ours] KillSim's Destroy(_rb) only takes effect at the end of the frame: a re-spawn in the same frame gets
            // null from AddComponent (the old body is still attached), so the body is made on the next physics step
            _bodyPending = false;
            _rb = gameObject.AddComponent<Rigidbody>();
            if (!_rb) { _bodyPending = true; return; }
            _rb.mass = Mass;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = _simple ? CollisionDetectionMode.Discrete : CollisionDetectionMode.ContinuousDynamic;
            var half = Vector3.Max(Vector3.Scale(_box ? _box.size : Vector3.one, transform.lossyScale) * 0.5f, new Vector3(0.025f, 0.025f, 0.025f));
            _half = half;
            _radius = half.magnitude;
            // RigidBody::DoIntegration has no damping: only DoDrag (DRAG / DRAG_ANGULAR of the rigidbodyspecs, below)
            _rb.linearDamping = 0f;
            _rb.angularDamping = _specs.NaturalAngularDamping;
            float w = 2f * half.x, h = 2f * half.y, l = 2f * half.z;
            var inertia = new Vector3(h * h + l * l, w * w + l * l, w * w + h * h) * (Mass / 6f);
            if (Moment.x > 0f) inertia.x *= Moment.x;
            if (Moment.y > 0f) inertia.y *= Moment.y;
            if (Moment.z > 0f) inertia.z *= Moment.z;
            _rb.inertiaTensor = inertia;
            _rb.inertiaTensorRotation = Quaternion.identity;
            if (_simple && _box) _box.enabled = false;   // ModifyFlags(0, 0x20B): no SRB/RB collisions, no triggers
            CarContacts.SetDynamic(_rb, true);
            CarContacts.SetNoCarEffect(_rb, NoCarEffect);
            if (_box) SetContactRules(_box, _simple ? 0 : _specs.ContactFlags);
        }

        /// <summary>RigidBody::Detach (0x697050): an attached body comes loose; WORLD_DETACHED to the sequencer.</summary>
        void Detach()
        {
            if (!_attached) { MakeBody(); return; }
            _attached = false;
            MakeBody();
            if (SequencerHash != 0) Stimulus(SeqStimuli.WorldDetached);
        }

        /// <summary>Smackable::Kill (0x67B650) without releasing the model: the body goes, the prop stays where it is.</summary>
        void KillSim()
        {
            Complex.Remove(this);
            if (_rb)
            {
                CarContacts.SetDynamic(_rb, false);
                CarContacts.SetNoCarEffect(_rb, false);
                if (_box) SetContactRules(_box, 0);
                Destroy(_rb);
                _rb = null;
            }
            if (_box && !_gone) _box.enabled = true;
            _spawned = false;
            _attached = false;
            _simple = false;
            _dropping = false;
            _asleep = false;
        }

        // ---- contacts: the trigger, Moment::React's break test and the impact stimuli ----

        /// <summary>A car hit it with this closing velocity (car velocity minus prop velocity) before the contact.</summary>
        public void Hit(PlayerCar car, Vector3 carVelocity, Rigidbody carBody)
        {
            if (!carBody) return;
            var toProp = (transform.TransformPoint(_box ? _box.center : Vector3.zero) - carBody.worldCenterOfMass);
            Contact(carBody, carVelocity, carBody.worldCenterOfMass, toProp.sqrMagnitude > 1e-6f ? toProp.normalized : carBody.transform.forward, false);
        }

        void OnCollisionEnter(Collision c)
        {
            var p = c.contactCount > 0 ? c.GetContact(0) : default;
            if (c.rigidbody) { Contact(c.rigidbody, c.relativeVelocity, p.point, p.normal, false); return; }
            // the loose body against the world: ground (face up) or world (walls)
            if (!_spawned || c.contactCount == 0) return;
            bool ground = Mathf.Abs(p.normal.y) > 0.7f;
            _contactPoints += c.contactCount;
            Impact(c.relativeVelocity.magnitude, ground ? 3 : 2, null, p.point);
        }

        void OnCollisionStay(Collision c)
        {
            if (c.rigidbody) _hadObjectHit = true;
            _contactPoints += c.contactCount;   // RigidBody::GetNumContactPoints (this step's contact points)
        }

        void Contact(Rigidbody other, Vector3 relVel, Vector3 point, Vector3 normal, bool fromExplosion)
        {
            if (_gone || !other || other == _rb) return;
            if (other == _lastBody && Mathf.Abs(Time.fixedTime - _lastBodyTime) < 1e-4f) return;   // PlayerCar.Hit and the prop's own callback
            _lastBody = other;
            _lastBodyTime = Time.fixedTime;
            _hadObjectHit = true;
            var center = transform.TransformPoint(_box ? _box.center : Vector3.zero);
            // the other body's velocity into the prop (the contact's sign varies with who receives it)
            if (Vector3.Dot(relVel, center - other.worldCenterOfMass) < 0f) relVel = -relVel;
            if (Vector3.Dot(normal, center - other.worldCenterOfMass) < 0f) normal = -normal;
            var car = other.GetComponent<PlayerCar>();
            if (car && !car.AiControlled && !car.Traffic) _byPlayer = true;
            if (!_spawned)
            {
                // SmackableTrigger: any awake rigid body fires it (WTriggerManager::ProcessRB 0x781840); no_trigger props stay scenery
                if (NoTrigger || other.isKinematic || other.IsSleeping()) return;
                if (!Spawn()) return;
            }
            bool vehicle = car || other.GetComponent<Game.TrafficPhysics.TrafficVehicle>();
            Impact(relVel.magnitude, 1, other, point, car && !car.AiControlled && !car.Traffic, vehicle);
            float vn = Mathf.Max(0f, Vector3.Dot(relVel, normal));
            if (_attached)
            {
                // Moment::React (0x488F70): one side immobile → the other's mass × the normal closing speed
                if (!(DetachForce > 0f && other.mass * vn > DetachForce)) return;
                Detach();
                ResolveBreak(other, relVel, normal);
            }
            else if (!_rb)
            {
                MakeBody();
                ResolveBreak(other, relVel, normal);
            }
            if (car && !car.AiControlled && !car.Traffic && Game.Systems.Police != null && Game.Systems.Police.InPursuit && CostToState > 0 && !_costed)
            {
                _costed = true;
                Game.Systems.Police.Stats.CostToState += CostToState;
            }
        }

        bool _costed;

        /// <summary>
        /// The break resolved as a free collision this step [ours: PhysX already stopped the car against the fixed box]:
        /// along the normal both leave at the momentum-sharing speed; a NO_CAR_EFFECT prop leaves the car its speed.
        /// </summary>
        void ResolveBreak(Rigidbody other, Vector3 relVel, Vector3 n)
        {
            if (!_rb || !other || other.isKinematic) return;
            float vn = Mathf.Max(0f, Vector3.Dot(relVel, n));
            float common = vn * other.mass / (other.mass + Mass);
            _rb.linearVelocity = n * common + (relVel - n * vn) * 0.5f;   // [?] half the sliding speed carried along
            float back = NoCarEffect ? vn : common;
            float now = Vector3.Dot(other.linearVelocity, n);
            if (now < back) other.linearVelocity += n * (back - now);
        }

        /// <summary>Smackable::Impact (0x669E30) → SendImpacts (0x669D70). type: 1 object, 2 world, 3 ground.</summary>
        void Impact(float mag, int type, Rigidbody other, Vector3 point, bool player = false, bool vehicle = false)
        {
            if (SequencerHash == 0 || mag < 1f) return;
            _lastImpact = point;
            _haveImpact = true;
            switch (type)
            {
                case 3: SendImpacts(SeqStimuli.GroundCollision, mag * 0.1f); break;
                case 2: SendImpacts(SeqStimuli.WorldCollision, mag * 0.022369863f); break;
                default:
                    if (!other) return;
                    float sev = mag * 0.022369863f;
                    SendImpacts(SeqStimuli.ObjectCollision, sev);
                    if (player) SendImpacts(SeqStimuli.PlayerCollision, sev);
                    if (vehicle) SendImpacts(SeqStimuli.VehicleCollision, sev);
                    break;
            }
        }

        void SendImpacts(uint system, float severity)
        {
            var seq = EnsureSequencer();
            if (seq == null) return;
            int n = (int)(Mathf.Clamp01(severity) * 6f);
            float t = Time.time;
            foreach (var s in seq.Systems)
                if (s.Id == system)
                    for (int i = 0; i <= n && i < SeqStimuli.Impact.Length; i++) s.ProcessStimulus(SeqStimuli.Impact[i], t, EventSeqEngine.QueueMode.Allow);
        }

        /// <summary>
        /// IExplodeable::OnExplosion (0x677100) for a prop in an explosion: the explosion's trigger sphere spawns it first,
        /// then (ExplosionEffect &gt; 0) it is pushed to speed × ExplosionEffect along the blast (ResolveForce detaches
        /// even a DETACH_FORCE &lt; 0 prop) and gets EXPLOSION.
        /// </summary>
        public void OnExplosion(Vector3 center, float speed, bool byPlayer)
        {
            if (_gone) return;
            if (!_spawned && !Spawn()) return;
            if (ExplosionEffect <= 0f) return;
            _byPlayer |= byPlayer;
            if (_attached) Detach();
            else MakeBody();
            var dir = transform.TransformPoint(_box ? _box.center : Vector3.zero) - center;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.up;
            float s = speed * ExplosionEffect;
            if (_rb && !_rb.isKinematic)
            {
                float vd = Vector3.Dot(_rb.linearVelocity, dir);
                if (vd < s) _rb.linearVelocity += dir * (s - vd);
            }
            if (SequencerHash != 0) Stimulus(SeqStimuli.Explosion);
        }

        /// <summary>EventSequencer::System::ProcessStimulus on this prop's engine (QUEUE_ALLOW); `system` 0 = every system.</summary>
        public void Stimulus(uint stimulus, uint system = 0)
        {
            var seq = EnsureSequencer();
            if (seq == null) return;
            float t = Time.time;
            if (system == 0) seq.ProcessStimulus(stimulus, t, EventSeqEngine.QueueMode.Allow);
            else foreach (var s in seq.Systems) if (s.Id == system) s.ProcessStimulus(stimulus, t, EventSeqEngine.QueueMode.Allow);
        }

        EventSeqEngine EnsureSequencer()
        {
            if (_seq != null || SequencerHash == 0) return _seq;
            var data = WorldSequences.Find(SequencerHash);
            if (data == null) { SequencerHash = 0; return null; }
            _seq = new EventSeqEngine(data, Time.time) { OnEvent = OnSeqEvent };
            return _seq;
        }

        /// <summary>EventSequencer::IContext position (0x669D40): the last impact point, else the prop.</summary>
        Vector3 ContextPosition => _haveImpact ? _lastImpact : transform.position;

        void OnSeqEvent(uint system, SeqEvent ev)
        {
            var pos = ContextPosition;
            if (ev.Id == SeqStimuli.EBreakerStopCops)
            {
                // EBreakerStopCops {fEventRadius, fDuration} at the prop -> MBreakerStopCops (AICopManager::ApplyBreakerZones)
                Game.Systems.Police?.BreakerStopCops(pos, ev.F(4), ev.F(8));
                Game.Gameplay.GameIcons.HidePursuitBreakerIcon(pos);   // EBreakerStopCops handler sub_620DF0 → HidePursuitBreakerIcon(pos, 20)
            }
            else if (ev.Id == SeqStimuli.EMomentStrm)
            {
                string moment = ev.S(4);
                try { MomentFired?.Invoke(moment, pos, _byPlayer); } catch (System.Exception e) { Debug.LogException(e); }
                if (moment == "expl_gas_station") Game.Systems.Police?.NotifyGasStationExplosion(pos, _byPlayer);
            }
            else if (ev.Id == SeqStimuli.ETriggerMomentNIS)
            {
                // ETriggerMomentNIS {fSceneName}: the breaker's "Moment" NIS (an ICE camera at the nearest moment marker);
                // NisPlayer.PlayMoment applies the exe's gates (Jump Cams, pursuit, cop near) and returns null otherwise
                Game.NisPlayer.PlayMoment(ev.S(4));
            }
            else if (ev.Id == SeqStimuli.EProcessAreaStimulus)
            {
                // {fSystemName, fStimulus, fQueueMode, fRadius}: every sequenced prop in range with that system
                uint sys = ev.U(4), stim = ev.U(8);
                float r = ev.F(16);
                foreach (var s in Sequenced.ToArray())
                    if (s && s != this && (s.transform.position - pos).sqrMagnitude <= r * r && s.HasSystem(sys))
                    {
                        s._byPlayer |= _byPlayer;
                        s.Stimulus(stim, sys);
                    }
            }
            else if (ev.Id == SeqStimuli.ESpawnExplosion)
            {
                // {fPosition (context), fSpeed, fRadius, fStartRadius, fEffectThis, fDoesDamage, fIncludePlayers,
                // fExcludeVehicles, fExcludeSmackables} [?: the game expands it from fStartRadius at fSpeed; applied at once]
                float speed = ev.F(8), r = ev.F(12);
                bool self = ev.U(20) != 0, damage = ev.U(24) != 0, players = ev.U(28) != 0, noVehicles = ev.U(32) != 0, noSmack = ev.U(36) != 0;
                CameraExplosions.Add(pos, speed, r, ev.F(16));   // [hook: camera agent] the IExplosion list CDActionDrive shakes the view from
                if (!noSmack)
                    foreach (var s in All.ToArray())
                        if (s && (s != this || self) && (s.transform.position - pos).sqrMagnitude <= r * r) s.OnExplosion(pos, speed, _byPlayer);
                if (!noVehicles && damage)
                    foreach (var car in FindObjectsByType<PlayerCar>(FindObjectsSortMode.None))
                    {
                        if (!car || (car.transform.position - pos).sqrMagnitude > r * r) continue;
                        if (!players && !car.AiControlled && !car.Traffic) continue;
                        // the vehicle's explosion handler: EXPLOSION + EXPLOSION_DAMAGE to its sequencer (wrecks a cop)
                        CarRender.CarFragments.Explosion(car.gameObject);
                    }
            }
        }

        bool HasSystem(uint id)
        {
            var data = WorldSequences.Find(SequencerHash);
            if (data == null) return false;
            foreach (var s in data.SystemIds) if (s == id) return true;
            return false;
        }

        // ---- per-step and 10 Hz logic ----

        bool _bodyPending;

        void FixedUpdate()
        {
            if (!_spawned) return;
            if (_bodyPending && !_attached) MakeBody();
            float dT = Time.fixedDeltaTime;
            // OnTaskSimulate (0x684F10): age, AUTO_SIMPLIFY, dropout sink
            _age += dT;
            if (!_simple && AutoSimplify > 0f && _age > AutoSimplify) Simplify();
            if (_dropping)
            {
                _dropTimer -= dT;
                transform.position += Vector3.down * (dT * DropoutSpeed);   // RigidBody::ModifyYPos(−dT·DROPOUT[1])
                if (_rb) _rb.position = transform.position;
                if (_dropTimer <= 0f) { _dropTimer = 0f; ReleaseModel(); }
                return;
            }
            _contactPointsPrev = _contactPoints;
            _contactPoints = 0;
            if (_rb && !_rb.isKinematic) Integrate();
            _task += dT;
            if (_task >= 0.1f)
            {
                float t = _task;
                _task = 0f;
                OnTask(t);
            }
        }

        /// <summary>Smackable::OnTask (0x67B6F0, 10 Hz): RestLogic, OffWorld, UpdatePriority.</summary>
        void OnTask(float dT)
        {
            RestLogic(dT);
            if (!_spawned) return;
            OffWorld(dT);
            UpdatePriority();
        }

        bool IsAtRest()
        {
            if (_dropping) return false;
            if (_attached) return !_hadObjectHit;   // an attached body sleeps right away
            if (!_rb) return false;
            if (_simple) return false;              // not modeling: never at rest in the RB sense [?]
            return (_asleep || _rb.IsSleeping()) && !_hadObjectHit;
        }

        /// <summary>RestLogic (0x670A30).</summary>
        void RestLogic(float dT)
        {
            bool rest = IsAtRest();
            _hadObjectHit = false;
            if (!rest) { _restTimer = 0.125f; return; }
            if (_restTimer > 0f) { _restTimer -= dT; return; }
            // StartDropout (0x669F50): loose with a DROPOUT → sink away
            if (DropoutTime > 0f && !_attached && _rb)
            {
                _dropping = true;
                _dropTimer = DropoutTime;
                _rb.isKinematic = true;
                if (_box) _box.enabled = false;
                return;
            }
            if (CanRestore())
            {
                if (_virgin && _attached)
                {
                    // never broke loose: back to plain scenery
                    RestoreScene();
                    return;
                }
                // ITriggerableModel::PlaceTrigger (0x6729B0): frozen where it lies with a non-virgin trigger, still drawn
                KillSim();
                _virgin = false;
                return;
            }
            ReleaseModel();
        }

        /// <summary>CanRestore (0x66A070): sleeping on a world face [ours: a ground ray].</summary>
        bool CanRestore()
        {
            var p = transform.TransformPoint(_box ? _box.center : Vector3.zero);
            return GroundUnder(p, 6f, out _);
        }

        static readonly RaycastHit[] Hits = new RaycastHit[8];

        static bool GroundUnder(Vector3 p, float reach, out float y)
        {
            y = float.MinValue;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 2f, Vector3.down, Hits, reach + 2f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            for (int i = 0; i < n; i++)
                if (!Hits[i].rigidbody && WorldCollision.IsGround(Hits[i].collider) && Hits[i].point.y > y) { y = Hits[i].point.y; found = true; }
            return found;
        }

        /// <summary>OffWorld (0x66A170): off a face or below the ground for over 1 s → released.</summary>
        void OffWorld(float dT)
        {
            if (AllowOffWorld) return;
            var p = transform.TransformPoint(_box ? _box.center : Vector3.zero);
            float radius = _box ? Vector3.Scale(_box.size, transform.lossyScale).magnitude * 0.5f : 0.5f;
            bool on = GroundUnder(p, 200f, out float gy) && p.y + radius >= gy;
            if (on) { _offWorldTimer = 0f; return; }
            _offWorldTimer += dT;
            if (_offWorldTimer > 1f) ReleaseModel();
        }

        /// <summary>UpdatePriority (0x66A200): (CAN_SIMPLIFY · (1 in view, 2 not) + distance to the camera) / radius.</summary>
        void UpdatePriority()
        {
            if (!_spawned || _simple) { _priority = -1f; return; }
            var cam = Camera.main;
            float dist = cam ? Vector3.Distance(cam.transform.position, transform.position) : 0f;
            bool inView = _renderer && _renderer.isVisible;
            float radius = _box ? Mathf.Max(0.01f, Vector3.Scale(_box.size, transform.lossyScale).magnitude * 0.5f) : 1f;
            _priority = (CanSimplify * (inView ? 1f : 2f) + dist) / radius;
        }

        /// <summary>SimplifyOne (0x684DB0): the highest priority complex body that can simplify.</summary>
        static bool SimplifyOne()
        {
            Complex.RemoveAll(s => !s);
            Complex.Sort((a, b) => b._priority.CompareTo(a._priority));
            foreach (var s in Complex.ToArray())
                if (s.Simplify()) return true;
            return false;
        }

        /// <summary>Simplify (0x684C60): a collisionless SimpleRigidBody with the same motion and owner gravity.</summary>
        bool Simplify()
        {
            if (!_spawned || _simple) return false;
            Complex.Remove(this);
            _simple = true;
            _attached = false;
            MakeBody();
            if (_box) _box.enabled = false;
            if (_rb) _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            return true;
        }

        void Update()
        {
            if (_seq != null && _seq.AnySystemInAction()) _seq.Update(Time.time);
            // KILL_OFF_SCREEN on the model (0x66AF60): counts down while unseen, reset while seen; ≤ 0 = never
            if (_smacked && !_gone && KillOffScreen > 0f && _offScreen > 0f)
            {
                if (_renderer && _renderer.isVisible) _offScreen = KillOffScreen;
                else
                {
                    _offScreen -= Time.deltaTime;
                    if (_offScreen <= 0f) ReleaseModel();
                }
            }
        }

        // ---- the body's rigidbodyspecs (RigidBody / SimpleRigidBody rules the prop runs on) ----

        bool _asleep;
        const float AirDensity = 1.225f;   // Drag(vel, C, A, 1.225) (RigidBody.cpp:527-534)

        /// <summary>
        /// Per sim step on a loose body:
        /// <list type="bullet">
        /// <item>Gravity (RigidBody::OnBeginFrame :516-522; Smackable::OnTaskSimulate 0x684F10 for a SimpleRigidBody, which
        /// gets ResolveForce((0, GRAVITY·mass, 0)) from its owner): the rigidbodyspecs GRAVITY, e.g. −13 for "smackable",
        /// −36 watertower, −40 sailboat; Unity's gravity is replaced by it.</item>
        /// <item>RigidBody::DoDrag (0x6972D0, :538-570), RBSmackable only: in body axes F = −½·1.225·|v|·(v ⊙ 4·dim² ⊙ DRAG)
        /// and τ = −½·1.225·|ω|·(ω ⊙ cs ⊙ DRAG_ANGULAR), cs = 4·dim_i·√(dim_j² + dim_k²), dim = half extents; each only when its
        /// coefficients are non-zero (status 0x20 / 0x800 from the ctor :228-238).</item>
        /// <item>RBSmackable::ShouldSleep (0x66A310 → RigidBody::ShouldSleep :1475): |v| + |ω|·radius &lt; SLEEP_VELOCITY (2) with
        /// more than 2 contact points → asleep (state 1: no integration until a collision wakes it; Rigidbody.Sleep here).</item>
        /// <item>SimpleRigidBody (the simplified prop, ModifyFlags(0, 0x20B) at 0x684EE7): flags 1|2 no SRB/RB collisions,
        /// 8 position integration, 0x200 orientation; flag 4 (ApplyFriction v×0.99, ω×0.98) is NOT set, so it is undamped
        /// (was damped here) and has no drag; CAN_HIT_TRIGGER (0x100) is clear, so it fires no trigger.</item>
        /// </list>
        /// </summary>
        void Integrate()
        {
            // asleep: nothing is integrated until a contact wakes the body (an AddForce would wake it)
            if (_asleep) { if (_rb.IsSleeping()) return; _asleep = false; }
            float g = _specs.Gravity;
            if (_rb.useGravity) _rb.AddForce(new Vector3(0f, g - Physics.gravity.y, 0f), ForceMode.Acceleration);
            else _rb.AddForce(new Vector3(0f, g, 0f), ForceMode.Acceleration);
            if (_simple) return;
            var rot = _rb.rotation;
            var inv = Quaternion.Inverse(rot);
            if (_specs.HasDrag)
            {
                var v = inv * _rb.linearVelocity;
                var area = 4f * Vector3.Scale(_half, _half);
                var f = Vector3.Scale(Vector3.Scale(v, area), _specs.Drag) * (-0.5f * v.magnitude * AirDensity);
                _rb.AddForce(rot * f, ForceMode.Force);
            }
            if (_specs.HasAngularDrag)
            {
                var w = inv * _rb.angularVelocity;
                var h = _half;
                var cs = new Vector3(h.x * 4f * Mathf.Sqrt(h.y * h.y + h.z * h.z), h.y * 4f * Mathf.Sqrt(h.x * h.x + h.z * h.z), h.z * 4f * Mathf.Sqrt(h.x * h.x + h.y * h.y));
                var t = Vector3.Scale(Vector3.Scale(w, cs), _specs.DragAngular) * (-0.5f * w.magnitude * AirDensity);
                _rb.AddTorque(rot * t, ForceMode.Force);
            }
            // ShouldSleep on the last step's contacts
            float sum = _rb.linearVelocity.magnitude + _rb.angularVelocity.magnitude * _radius;
            bool sleep = sum < _specs.SleepVelocity && _contactPointsPrev > 2;
            if (sleep && !_asleep) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; _rb.Sleep(); }
            _asleep = sleep;
        }

        /// <summary>Attrib::Gen::rigidbodyspecs of a smackable (its `rigidbodyspecs` RefSpec, "default" when absent: 12
        /// collections, 156 of 181 smackables on "smackable"; harness `smack9`, `physcore rb`).</summary>
        public sealed class Specs
        {
            public static readonly Specs Default = new Specs();
            public string Name = "default";
            public float Gravity = -13f, SleepVelocity = 2f, NaturalAngularDamping;
            public Vector3 Drag, DragAngular;
            public float GroundStatic = 0.35f, GroundDynamic = 0.28f;
            public bool ImmobileObjects, NoWorld, NoGround, NoObjects;
            public bool HasDrag => Drag.sqrMagnitude > 0f;
            public bool HasAngularDrag => DragAngular.sqrMagnitude > 0f;
            public int ContactFlags => (ImmobileObjects ? RuleImmobile : 0) | (NoWorld ? RuleNoWorld : 0) | (NoGround ? RuleNoGround : 0) | (NoObjects ? RuleNoObjects : 0);
            PhysicsMaterial _mat;

            /// <summary>
            /// The prop's own friction against the road: GROUND_FRICTION {us, min(uk, us)} (ResolveGroundCollision :724-727),
            /// combined by minimum so the world's default material does not raise it [ours: PhysX takes one material per
            /// collider, so walls use the ground pair too (WALL_FRICTION 0.3/0.2 vs 0.3/0.15)]. Cars set their own per contact.
            /// </summary>
            public PhysicsMaterial Material
            {
                get
                {
                    if (_mat) return _mat;
                    _mat = new PhysicsMaterial("smackable " + Name)
                    {
                        staticFriction = GroundStatic, dynamicFriction = Mathf.Min(GroundDynamic, GroundStatic), bounciness = 0f,
                        frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Minimum,
                    };
                    return _mat;
                }
            }

            static readonly Dictionary<uint, Specs> Cache = new Dictionary<uint, Specs>();

            public static Specs For(AttribCollection smackable)
            {
                var db = GameData.Attributes;
                if (db == null) return Default;
                uint cls = Dynamics.RigidBodySpecs.ClassKey, key = AttribDatabase.Hash("default");
                for (var x = smackable; x != null; x = x.Parent)
                    if (x.TryGet("rigidbodyspecs", out int at, out _, out _) && at + 8 <= x.Source.Data.Length)
                    {
                        uint k = Chunks.U32(x.Source.Data, at + 4);
                        if (db.Collections.ContainsKey((cls, k))) key = k;
                        break;
                    }
                lock (Cache)
                {
                    if (Cache.TryGetValue(key, out var c)) return c;
                    c = new Specs();
                    if (db.Collections.TryGetValue((cls, key), out var rb))
                    {
                        Vector3 V(string f) => rb.TryGet(f, out int o, out _, out _)
                            ? new Vector3(Chunks.F32(rb.Source.Data, o), Chunks.F32(rb.Source.Data, o + 4), Chunks.F32(rb.Source.Data, o + 8)) : Vector3.zero;
                        c.Name = rb.Name ?? key.ToString("X8");
                        c.Gravity = rb.Float("GRAVITY", 0, c.Gravity);
                        c.SleepVelocity = rb.Float("SLEEP_VELOCITY", 0, c.SleepVelocity);
                        c.NaturalAngularDamping = rb.Float("NATURAL_ANGULAR_DAMPING", 0, 0f);
                        c.Drag = V("DRAG");
                        c.DragAngular = V("DRAG_ANGULAR");
                        c.GroundStatic = rb.Float("GROUND_FRICTION", 0, c.GroundStatic);
                        c.GroundDynamic = rb.Float("GROUND_FRICTION", 1, c.GroundDynamic);
                        c.ImmobileObjects = rb.Bool("IMMOBILE_OBJECT_COLLISIONS");
                        c.NoWorld = rb.Bool("NO_WORLD_COLLISIONS");
                        c.NoGround = rb.Bool("NO_GROUND_COLLISIONS");
                        c.NoObjects = rb.Bool("NO_OBJ_COLLISIONS");
                    }
                    Cache[key] = c;
                    return c;
                }
            }
        }

        // ---- contact rules PhysX cannot express (its own ContactModifyEvent; CarContacts handles the car side) ----

        const int RuleImmobile = 1, RuleNoWorld = 2, RuleNoGround = 4, RuleNoObjects = 8;
        static readonly Dictionary<int, int> Rules = new Dictionary<int, int>();
        static bool _rulesHooked;

        static void SetContactRules(Collider c, int rules)
        {
            lock (Rules)
            {
                if (rules == 0) Rules.Remove(c.GetInstanceID());
                else Rules[c.GetInstanceID()] = rules;
            }
            if (rules != 0 && !_rulesHooked)
            {
                Physics.ContactModifyEvent += ModifyContacts;
                Physics.ContactModifyEventCCD += ModifyContacts;
                _rulesHooked = true;
            }
        }

        /// <summary>
        /// The rigidbodyspecs collision switches of a loose RBSmackable: NO_WORLD_COLLISIONS (CanCollideWithWorld false:
        /// barriers and OBBs ignored; foundation, radiotowersupport, 493A8A87), NO_GROUND_COLLISIONS (CanCollideWithGround
        /// false), NO_OBJ_COLLISIONS (CanCollideWithObjects :1505-1513 false), and IMMOBILE_OBJECT_COLLISIONS
        /// (RigidBody::ModifyCollision(RigidBody) :947-952: the prop's Moment is immobile against any body that is not
        /// immobile itself; watertower, radiotower, sailboat, foundation): its inverse mass and inertia go to 0 for that pair.
        /// World colliders are told apart by WorldCollision.KindOf (1 road strips, 2 barriers, 3 OBBs). Any thread.
        /// </summary>
        static void ModifyContacts(PhysicsScene scene, Unity.Collections.NativeArray<ModifiableContactPair> pairs)
        {
            for (int p = 0; p < pairs.Length; p++)
            {
                var pair = pairs[p];
                int ra, rb;
                lock (Rules)
                {
                    if (Rules.Count == 0) return;
                    Rules.TryGetValue(pair.colliderInstanceID, out ra);
                    Rules.TryGetValue(pair.otherColliderInstanceID, out rb);
                }
                if (ra == 0 && rb == 0) continue;
                int rules = ra != 0 ? ra : rb;
                bool mineIsA = ra != 0;
                int otherCol = mineIsA ? pair.otherColliderInstanceID : pair.colliderInstanceID;
                int otherBody = mineIsA ? pair.otherBodyInstanceID : pair.bodyInstanceID;
                int kind = WorldCollision.KindOf(otherCol);
                bool ignore = (rules & RuleNoWorld) != 0 && (kind == 2 || kind == 3)
                    || (rules & RuleNoGround) != 0 && kind == 1
                    || (rules & RuleNoObjects) != 0 && otherBody != 0 && kind == 0;
                if (ignore)
                {
                    for (int i = 0; i < pair.contactCount; i++) pair.IgnoreContact(i);
                    pairs[p] = pair;
                    continue;
                }
                if ((rules & RuleImmobile) != 0 && otherBody != 0 && kind == 0)
                {
                    // the other side immobile too (both rules set) → leave the pair to PhysX
                    int otherRules = mineIsA ? rb : ra;
                    if ((otherRules & RuleImmobile) != 0) continue;
                    var mp = pair.massProperties;
                    if (mineIsA) { mp.inverseMassScale = 0f; mp.inverseInertiaScale = 0f; }
                    else { mp.otherInverseMassScale = 0f; mp.otherInverseInertiaScale = 0f; }
                    pair.massProperties = mp;
                    pairs[p] = pair;
                }
            }
        }

        /// <summary>Stimulus, system and event ids (stringhash32 of the names; checked against speed.exe and the L2RA packs).</summary>
        public static class SeqStimuli
        {
            /// <summary>IMPACT_1..IMPACT_7 (table 0x905D28).</summary>
            public static readonly uint[] Impact =
            {
                AttribDatabase.Hash("IMPACT_1"), AttribDatabase.Hash("IMPACT_2"), AttribDatabase.Hash("IMPACT_3"),
                AttribDatabase.Hash("IMPACT_4"), AttribDatabase.Hash("IMPACT_5"), AttribDatabase.Hash("IMPACT_6"),
                AttribDatabase.Hash("IMPACT_7"),
            };
            public static readonly uint WorldDetached = AttribDatabase.Hash("WORLD_DETACHED"),   // 0xC91B90D9
                Explosion = AttribDatabase.Hash("EXPLOSION"),                                       // 0xAB556D39
                JointDetached = AttribDatabase.Hash("JOINT_DETACHED"),                              // 0xE16B3A09
                EBreakerStopCops = AttribDatabase.Hash("EBreakerStopCops"),
                EMomentStrm = AttribDatabase.Hash("EMomentStrm"),
                ETriggerMomentNIS = AttribDatabase.Hash("ETriggerMomentNIS"),
                EProcessAreaStimulus = AttribDatabase.Hash("EProcessAreaStimulus"),
                ESpawnExplosion = AttribDatabase.Hash("ESpawnExplosion");
            /// <summary>The collision systems Smackable::Impact sends to.</summary>
            public const uint GroundCollision = 0x2BF74E61, WorldCollision = 0x7EBE81C0, ObjectCollision = 0xD59062C8,
                PlayerCollision = 0x2F698829, VehicleCollision = 0x80B88C1D;
        }

        /// <summary>
        /// The world event sequencer engines (TRACKS\L2RA.BUN, 75 chunks 0x3B811: bench, gasstationroof_seq, watertower_seq,
        /// radiotower_seq, CopCar, TrailerB, ...), by stringhash32 of the engine name. Loaded on first use.
        /// </summary>
        public static class WorldSequences
        {
            static Dictionary<uint, EventSeqEngineData> _engines;

            public static int Count => Engines.Count;

            public static EventSeqEngineData Find(uint hash) => hash != 0 && Engines.TryGetValue(hash, out var e) ? e : null;

            static Dictionary<uint, EventSeqEngineData> Engines
            {
                get
                {
                    if (_engines != null) return _engines;
                    _engines = new Dictionary<uint, EventSeqEngineData>();
                    try
                    {
                        if (GameData.Root == null) return _engines;
                        string path = Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN");
                        if (!File.Exists(path)) return _engines;
                        Load(GameFiles.ReadBundle(path), _engines);
                    }
                    catch (System.Exception e) { Debug.LogWarning("NFSMW world event sequences: " + e.Message); }
                    return _engines;
                }
            }

            /// <summary>Every chunk 0x3B811 (LoaderEventSequence) in the bundle; a chunk counts when its header size fits and
            /// it holds a 'CARP' pack.</summary>
            public static void Load(byte[] d, Dictionary<uint, EventSeqEngineData> into)
            {
                for (int i = 0; i + 8 <= d.Length; i++)
                {
                    if (d[i] != 0x11 || d[i + 1] != 0xB8 || d[i + 2] != 0x03 || d[i + 3] != 0x00) continue;
                    int size = Chunks.I32(d, i + 4);
                    if (size <= 16 || i + 8 + size > d.Length) continue;
                    EventSeqPack pack;
                    try { pack = EventSeqPack.Read(d, i + 8, i + 8 + size); } catch { continue; }
                    if (pack == null) continue;
                    foreach (var e in pack.Engines)
                        if (!string.IsNullOrEmpty(e.Name)) into[AttribDatabase.Hash(e.Name)] = e;
                    i += 7 + size;
                }
            }
        }
    }
}
