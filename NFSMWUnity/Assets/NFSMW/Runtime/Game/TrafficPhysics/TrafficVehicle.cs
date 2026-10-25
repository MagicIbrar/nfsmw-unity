using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// A vehicle run on the behaviours its pvehicle names (specs/traffic_physics.md §2) instead of the racer model:
    /// the PVehicle task order RIGIDBODY → INPUT → ENGINE → SUSPENSION → DAMAGE at the fixed 120 Hz step, with
    /// RigidBody's Resolve/ResolveForce/ModifyYPos/Damp mapped onto the Unity Rigidbody (forces summed per step,
    /// applied at the centre of gravity). The body is the collision geometry root: dimension = its half extents, the
    /// inertia Box(mass, 2·dim) × TENSOR_SCALE (Util_GenerateCarTensor), the centre of gravity from Chassis::SetCOG.
    /// A traffic car keeps its PlayerCar for the rest of the game (input, audio, lights, other systems' lookups) but
    /// that component is disabled; the engine and wheel state are copied back into it each step [ours].
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class TrafficVehicle : MonoBehaviour
    {
        public VehicleSpecs Specs { get; private set; }
        public Rigidbody Rb { get; private set; }
        public Chassis Chassis { get; private set; }
        public EngineTraffic Engine { get; private set; }
        /// <summary>EngineRacer (the cops' BEHAVIOR_MECHANIC_ENGINE): null for traffic.</summary>
        public EngineRacer Racer { get; private set; }
        /// <summary>ITransmission::GetGear of whichever engine runs (sim ids: 0 R, 1 N, 2 first).</summary>
        public int EngineGear => Engine != null ? Engine.Gear : Racer != null ? Racer.Gear : Chassis.GNeutral;
        /// <summary>ITransmission::GetDriveTorque (0 without an engine: trailers).</summary>
        public float DriveTorque => Engine != null ? Engine.DriveTorque : Racer != null ? Racer.DriveTorque : 0f;
        public bool HasEngine => Engine != null || Racer != null;
        /// <summary>IEngine::GetNOSBoost (Chassis::ComputeState state.nos_boost, Chassis.cpp:328).</summary>
        public float NosBoost => Racer != null ? Racer.NosBoost : 1f;
        /// <summary>ICheater::GetCatchupCheat (the AI racer's, AIRacer sets it on EngineRacer); null without an ICheater
        /// (cops: AIVehiclePursuit has none; traffic).</summary>
        public float? CatchupCheat => Racer?.CatchupCheat;
        /// <summary>IVehicle::IsDestroyed for the chassis: DamageVehicle, or the PlayerCar's damage/engine state (cops:
        /// DamageCopCar runs on the CopCar/PlayerCar side) [ours: the bridge].</summary>
        public bool IsDestroyed => OwnerDestroyed || (Damage != null ? Damage.Destroyed : Car && (Car.Destroyed || Car.EngineBlown));
        /// <summary>Set by the owner's own damage behaviour (CopCar: DamageCopCar destroyed).</summary>
        public bool OwnerDestroyed;
        /// <summary>ICause::FindInstance(model causality) == the player's vehicle, for DamageCopCar::CheckUpright.
        /// [hook for the police code: e.g. `() => Police.LastPlayerContact(cop) ...`; null = never the player's]</summary>
        public System.Func<bool> CausedByPlayer;
        public VehicleDamage Damage { get; private set; }
        /// <summary>The PlayerCar this vehicle drives for (null for a trailer).</summary>
        public PlayerCar Car { get; private set; }
        public Transform[] WheelVisuals;

        // controls (IInput): from the PlayerCar's Input for a car, set by the tractor for a trailer
        public float Gas, Brake, EBrake, Steer;
        /// <summary>IInput fNOS (InputControls) — the nitrous button, read by EngineRacer.</summary>
        public bool Nitrous;
        public bool Staging;
        /// <summary>IVehicle::GetDriverStyle / GetDriverClass (Sim/SimTypes.h:218-226: DRIVER_TRAFFIC = 1; was 2, the cop
        /// class; the cops and racers set theirs on attach).</summary>
        public int DriverStyle, DriverClass = 1;
        /// <summary>A trailer: hitched to its tractor (Dynamics::Articulation::IsJoined).</summary>
        public bool Joined;
        /// <summary>RigidBody::HasHadCollision this step (DoSleep keeps a car awake after a hit).</summary>
        public bool HadCollision { get; private set; }
        /// <summary>RigidBody::IsInGroundContact (leversInContact != 0): the body touched the road last step.</summary>
        public bool GroundContact { get; private set; }

        public Vector3 PendingForce { get; private set; }
        public Vector3 PendingTorque { get; private set; }
        Vector3 _cog, _lastPos;
        bool _cogSet, _havePos;
        Vector3[] _hub;
        Transform[] _spin;
        float[] _spinDeg;
        BoxCollider _box;
        bool _hit, _groundHit;

        /// <summary>Centre of gravity in the body frame.</summary>
        public Vector3 CenterOfGravity => _cog;
        /// <summary>The body origin in world space (the collision root's centre).</summary>
        public Vector3 BodyOrigin => Rb.position + Rb.rotation * Specs.Pivot;
        public float MaxSteeringDeg => Chassis != null ? Chassis.MaxSteeringDeg : 45f;
        /// <summary>IVehicle::InShock (DamageVehicle shock timer running).</summary>
        public bool InShock => Damage != null && Damage.InShock;
        public float ForwardSpeed => Rb ? Vector3.Dot(Rb.linearVelocity, transform.forward) : 0f;
        /// <summary>The sim's gear as the PlayerCar counts it: -1 reverse, 0 neutral, 1.. forward.</summary>
        public int PlayerGear => HasEngine ? EngineGear - 1 : 0;

        /// <summary>Builds the behaviours the pvehicle names; false when it has none we run (the racer model stays).</summary>
        public static TrafficVehicle Attach(GameObject go, VehicleSpecs specs, PlayerCar car, Transform[] wheels)
        {
            if (specs == null) return null;
            var tv = go.GetComponent<TrafficVehicle>() ?? go.AddComponent<TrafficVehicle>();
            if (!tv.Init(specs, car, wheels)) { Destroy(tv); return null; }
            return tv;
        }

        bool Init(VehicleSpecs specs, PlayerCar car, Transform[] wheels)
        {
            Specs = specs;
            Car = car;
            Rb = GetComponent<Rigidbody>();
            WheelVisuals = wheels;
            switch (specs.Suspension)
            {
                case "SuspensionTraffic": Chassis = new SuspensionTraffic(this); break;
                case "SuspensionTrailer": Chassis = new SuspensionTrailer(this); break;
                case "SuspensionSimple": Chassis = new SuspensionSimple(this); break;
                default: return false;
            }
            if (specs.Engine == "EngineTraffic") Engine = new EngineTraffic(specs.Spec, Chassis);
            else if (specs.Engine == "EngineRacer") Racer = new EngineRacer(this, specs.Spec, Chassis);
            // DamageCopCar derives from DamageVehicle (DamageCopCar.cpp): the same zones and hit points from the cop's
            // damagespecs, plus its smackable rule (OnHit) and flip check (CopCar.FlipCheck)
            if (specs.Damage == "DamageVehicle" || specs.Damage == "DamageCopCar") Damage = new VehicleDamage(specs);
            if (Time.fixedDeltaTime > 1f / PlayerCar.SimRate + 1e-5f) Time.fixedDeltaTime = 1f / PlayerCar.SimRate;
            Rb.mass = specs.Spec.Mass;
            // RigidBody::DoIntegration (RigidBody.cpp:657-709) has no damping: DRAG / DRAG_ANGULAR / NATURAL_ANGULAR_DAMPING
            // are 0 for every car (physics.md §15.1); the velocity components are bounded ±300 / ±30 (Bounds below)
            Rb.linearDamping = 0f;
            Rb.angularDamping = specs.Spec.Body != null ? specs.Spec.Body.NaturalAngularDamping : 0f;
            Rb.interpolation = RigidbodyInterpolation.Interpolate;
            Rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Rb.maxAngularVelocity = AngularBound * 1.7320508f;
            // Util_GenerateCarTensor: Box(mass, 2·dim) × TENSOR_SCALE
            var d = specs.Dimension * 2f;
            float m12 = specs.Spec.Mass / 12f;
            Rb.inertiaTensor = Vector3.Scale(new Vector3(m12 * (d.y * d.y + d.z * d.z), m12 * (d.x * d.x + d.z * d.z), m12 * (d.x * d.x + d.y * d.y)), specs.Spec.TensorScale);
            Rb.inertiaTensorRotation = Quaternion.identity;
            _box = GetComponent<BoxCollider>();
            if (car)
            {
                // the racer sim stops; collision callbacks (smackables) still reach the disabled PlayerCar
                car.enabled = false;
                if (specs.Reset != "ResetCar") { var reset = GetComponent<CarReset>(); if (reset) Destroy(reset); }
            }
            // The model origin is the collision box's bottom (every root pivot.y = half height) and the body rides on its
            // springs at RIDE_HEIGHT − sag above the road. CarBuilder lifts the body by the ride height for a root sitting on
            // the road; with the sim carrying the ride height that lift is undone, the collider with it [ours].
            var bodyT = transform.Find("Body");
            if (bodyT && Mathf.Abs(bodyT.localPosition.y) > 1e-4f)
            {
                float lift = bodyT.localPosition.y;
                bodyT.localPosition = new Vector3(bodyT.localPosition.x, 0f, bodyT.localPosition.z);
                if (car && car.HasCollisionGeometry) car.PlaceCollisionAtOrigin(0f);   // the collision geometry at the body origin
                else if (_box) _box.center -= new Vector3(0, lift, 0);
            }
            if (wheels != null)
            {
                _hub = new Vector3[4];
                _spin = new Transform[4];
                _spinDeg = new float[4];
                for (int i = 0; i < 4 && i < wheels.Length; i++)
                {
                    if (!wheels[i]) continue;
                    // the hub over its arm: track/2 across as built, the axle at the arm's z (FRONT_AXLE is measured from the
                    // body origin, the collision box centre, not the model origin) [?: CarBuilder uses the model origin]
                    _hub[i] = new Vector3(wheels[i].localPosition.x, wheels[i].localPosition.y, specs.Pivot.z + Chassis.Wheels[i].LocalArm.z);
                    _spin[i] = wheels[i].Find("Spin");
                }
            }
            _cogSet = false;
            Chassis.Reset();
            return true;
        }

        // ---- RigidBody (Physics/Behaviors/RigidBody.cpp) ----

        public void SetCenterOfGravity(Vector3 cog)
        {
            if (_cogSet && (cog - _cog).sqrMagnitude < 1e-8f) return;
            _cog = cog;
            _cogSet = true;
            var t = Rb.inertiaTensor;
            Rb.centerOfMass = Specs.Pivot + cog;
            Rb.inertiaTensor = t;
            Rb.inertiaTensorRotation = Quaternion.identity;
            TrafficContacts.UpdateCog(this);
        }

        public void Resolve(Vector3 force, Vector3 torque)
        {
            PendingForce += force;
            PendingTorque += torque;
        }

        public void ResolveForce(Vector3 force) => PendingForce += force;

        public void ResolveForceAt(Vector3 force, Vector3 point)
        {
            PendingForce += force;
            PendingTorque += Vector3.Cross(point - Rb.worldCenterOfMass, force);
        }

        /// <summary>RigidBody::ModifyYPos: lift the body straight up (a wheel past full bump).</summary>
        public void ModifyYPos(float dy)
        {
            if (Rb.isKinematic) return;
            Rb.position += Vector3.up * dy;
        }

        /// <summary>RigidBody::Damp (RigidBody.cpp:1555-1562): linear and angular velocity, force and torque × (1 − amount).</summary>
        public void Damp(float amount)
        {
            float k = Mathf.Clamp01(1f - amount);
            Rb.linearVelocity *= k;
            Rb.angularVelocity *= k;
            PendingForce *= k;
            PendingTorque *= k;
        }

        // ---- RigidBody::DoIntegration bounds, RBVehicle::OnBeginFrame staging, ShouldSleep (RigidBody.cpp, RBVehicle.cpp) ----

        const float LinearBound = 300f, AngularBound = 30f;   // UMath::Bound per component (RigidBody.cpp:682-688)
        /// <summary>RigidBody::SLEEP_VELOCITY (rigidbodyspecs, 1 for every car).</summary>
        const float SleepVelocity = 1f;
        Vector3 _stageHold;
        bool _stageHeld, _hitObject;
        int _contactPoints;
        /// <summary>RBVehicle::mDeadOnWheels: seconds a destroyed non-player has sat on more than half its wheels.</summary>
        public float DeadOnWheels { get; private set; }
        /// <summary>RigidBody state 1 (SLEEPING): no integration, no gravity, no collisions until something hits it.</summary>
        public bool Sleeping { get; private set; }
        /// <summary>RigidBody::HasHadObjectCollision this step.</summary>
        public bool HadObjectCollision => _hitObject;

        /// <summary>
        /// The post-integration rules of the exe's rigid body, run on the previous step's result at the start of this one:
        /// <list type="bullet">
        /// <item>DoIntegration: each velocity component bounded to ±300 m/s / ±30 rad/s (:682-688).</item>
        /// <item>RBVehicle::OnBeginFrame (:66-87): staging (IsStaging, modelling, not animating) → the local x/z velocity and
        /// the yaw rate are zeroed and the x/z position of the previous tick is kept (re-taken every tick).</item>
        /// <item>RBVehicle::OnTaskSimulate (:99-104) + ShouldSleep (:112-120, RigidBody :1475): mDeadOnWheels counts while a
        /// destroyed non-player sits on more than numWheels/2 wheels; it sleeps after 2 s of that, or (no wheel down) when
        /// |v| + |ω|·radius &lt; SLEEP_VELOCITY with more than 2 contact points; never while animating or after an object hit.
        /// Asleep, DoIntegration clears the velocities and nothing collides with it until a Resolve wakes it.</item>
        /// </list>
        /// </summary>
        void RigidBodyFrame(float dT)
        {
            var lv = Rb.linearVelocity;
            var av = Rb.angularVelocity;
            var blv = new Vector3(Mathf.Clamp(lv.x, -LinearBound, LinearBound), Mathf.Clamp(lv.y, -LinearBound, LinearBound), Mathf.Clamp(lv.z, -LinearBound, LinearBound));
            var bav = new Vector3(Mathf.Clamp(av.x, -AngularBound, AngularBound), Mathf.Clamp(av.y, -AngularBound, AngularBound), Mathf.Clamp(av.z, -AngularBound, AngularBound));
            if (blv != lv) Rb.linearVelocity = blv;
            if (bav != av) Rb.angularVelocity = bav;
            bool animating = Car && Car.GetComponent<NisCarControl>() is NisCarControl n && n.Animating;
            if (Staging && !animating)
            {
                var p = Rb.position;
                if (_stageHeld)
                {
                    var rot = Rb.rotation;
                    var lvl = Quaternion.Inverse(rot) * Rb.linearVelocity;
                    var avl = Quaternion.Inverse(rot) * Rb.angularVelocity;
                    lvl.x = 0f;
                    lvl.z = 0f;
                    avl.y = 0f;
                    Rb.linearVelocity = rot * lvl;
                    Rb.angularVelocity = rot * avl;
                    p = new Vector3(_stageHold.x, p.y, _stageHold.z);
                    Rb.position = p;
                }
                _stageHold = p;
                _stageHeld = true;
            }
            else _stageHeld = false;

            // mDeadOnWheels and ShouldSleep
            bool player = Car && !Car.AiControlled && !Car.Traffic;
            if (Chassis != null && IsDestroyed && !player && Chassis.NumWheelsOnGround > 2) DeadOnWheels += dT;
            else DeadOnWheels = 0f;
            bool shouldSleep = false;
            if (!animating && !_hitObject)
            {
                if (Chassis != null && Chassis.NumWheelsOnGround >= 1) shouldSleep = DeadOnWheels >= 2f;
                else shouldSleep = Rb.linearVelocity.magnitude + Rb.angularVelocity.magnitude * Specs.Dimension.magnitude < SleepVelocity && _contactPoints > 2;
            }
            if (shouldSleep)
            {
                Sleeping = true;
                Rb.linearVelocity = Vector3.zero;
                Rb.angularVelocity = Vector3.zero;
                Rb.Sleep();
            }
            else if (Sleeping) { Sleeping = false; Rb.WakeUp(); }
        }

        /// <summary>A car placed at a spot (PlaceObject + Behavior::Reset): the wheels find the road again.</summary>
        public void ResetPhysics()
        {
            Engine?.Reset();
            Racer?.Reset();
            Damage?.Reset();
            _cogSet = false;
            Chassis.Reset();
        }

        public void MatchSpeed(float speed)
        {
            Engine?.MatchSpeed(speed);
            Racer?.MatchSpeed(speed);
            Chassis.MatchSpeed(speed);
        }

        public void ResetDamage() => Damage?.ResetDamage();

        readonly Core.WWorldPos _placePos = new Core.WWorldPos(0.05f);

        /// <summary>
        /// IVehicle::SetVehicleOnGround (PVehicle 0x685810) for a vehicle on these behaviours: orientation from the flattened
        /// initialVec (Util_GenerateMatrix), velocities zeroed, GetWorldHeightAtPointRigorous at resetPos + dimension.y;
        /// none → left at resetPos, false. Found: each wheel arm gets its own face (a WWorldPos with tolerance 0.05 at the
        /// arm's x/z), implying a body-origin height face + RIDE_HEIGHT − GuessCompression(m·g) + dimension.y
        /// (Chassis::GuessCompression: m·g/4 / SPRING_STIFFNESS); the body is turned to the plane through the axle and side
        /// mid-points and put at their centre; a wheel with no face → y = height + dimension.y, level. Then PlaceObject,
        /// the behaviours reset and SetSpeed(0). resetPos is the body origin (the collision root's centre). [?: the exact
        /// per-wheel term of 0x685A..0x685B7B, read as above]
        /// </summary>
        public bool SetVehicleOnGround(Vector3 resetPos, Vector3 initialVec)
        {
            var fwd = new Vector3(initialVec.x, 0f, initialVec.z);
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;
            var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            var dim = Specs.Dimension;
            bool found = Dynamics.WorldQueries.HeightRigorous(resetPos + Vector3.up * dim.y, out float h, out _);
            var origin = resetPos;
            if (found)
            {
                origin.y = h + dim.y;
                if (Chassis != null && Dynamics.WorldQueries.InstanceCount > 0)
                {
                    var p = new Vector3[4];
                    bool all = true;
                    var w = Dynamics.WorldQueries.Sim;
                    for (int i = 0; i < 4 && all; i++)
                    {
                        var arm = Chassis.Wheels[i].LocalArm;
                        var at = new Vector3(resetPos.x, resetPos.y + dim.y, resetPos.z) + rot * new Vector3(arm.x, 0f, arm.z);
                        _placePos.Invalidate();
                        _placePos.FindClosestFace(w, Dynamics.WorldQueries.ToSim(at), false);
                        if (!_placePos.OnValidFace) { all = false; break; }
                        float face = _placePos.HeightAtPoint(Dynamics.WorldQueries.ToSim(at));
                        float k = (i < 2 ? Specs.Spec.SpringFront : Specs.Spec.SpringRear) * Chassis.LBIN2NM;
                        float guess = k > 0f ? Specs.Spec.Mass * 9.81f * 0.25f / k : 0f;
                        p[i] = new Vector3(at.x, face + Chassis.RideHeight(i) - guess + dim.y, at.z);
                    }
                    if (all)
                    {
                        var along = (p[0] + p[1]) * 0.5f - (p[2] + p[3]) * 0.5f;
                        var across = (p[1] + p[3]) * 0.5f - (p[0] + p[2]) * 0.5f;
                        var up = Vector3.Cross(along, across).normalized;
                        if (up.y > 0.3f)
                        {
                            var f2 = Vector3.ProjectOnPlane(fwd, up).normalized;
                            if (f2.sqrMagnitude > 0.5f) rot = Quaternion.LookRotation(f2, up);
                        }
                        var centre = (p[0] + p[1] + p[2] + p[3]) * 0.25f;
                        var armMean = Vector3.zero;
                        for (int i = 0; i < 4; i++) armMean += new Vector3(Chassis.Wheels[i].LocalArm.x, 0f, Chassis.Wheels[i].LocalArm.z);
                        origin = centre - rot * (armMean * 0.25f);
                    }
                }
            }
            var pos = origin - rot * Specs.Pivot;
            transform.SetPositionAndRotation(pos, rot);
            Rb.position = pos;
            Rb.rotation = rot;
            if (!Rb.isKinematic)
            {
                Rb.linearVelocity = Vector3.zero;
                Rb.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
            _lastPos = pos;
            _havePos = true;
            _stageHeld = false;
            ResetPhysics();
            MatchSpeed(0f);
            return found;
        }

        float _uprightTask;
        readonly Core.WWorldPos _bodyWPos = new Core.WWorldPos(0.025f);

        /// <summary>DamageCopCar::OnTask (the 0.1 s "Physics" task): CheckUpright unless destroyed, animating or in an NIS.
        /// The ground normal is the body's WWorldPos (RBComplex::GetGroundNormal).</summary>
        void CopUprightTask(float dT)
        {
            _uprightTask += dT;
            if (_uprightTask < 0.1f) return;
            float step = _uprightTask;
            _uprightTask = 0f;
            bool animating = Car && Car.GetComponent<NisCarControl>() is NisCarControl n && n.Animating;
            if (Damage.Destroyed || animating || NisPlayer.Active) return;
            var gn = Vector3.up;
            if (Dynamics.WorldQueries.InstanceCount > 0 && _bodyWPos.Update(Dynamics.WorldQueries.Sim, Dynamics.WorldQueries.ToSim(BodyOrigin), out var sn, out _, true, true))
                gn = Dynamics.WorldQueries.ToUnity(sn);
            bool modeling = !Rb.isKinematic && Rb.detectCollisions;
            Damage.CheckUpright(step, modeling, Chassis.NumWheelsOnGround == 4, Rb.rotation * Vector3.up, gn, CausedByPlayer != null && CausedByPlayer());
        }

        static float Sq(float x) => x * x;

        void FixedUpdate()
        {
            if (Chassis == null || Rb == null || Rb.isKinematic) { PendingForce = PendingTorque = Vector3.zero; return; }
            // PVehicle leaving SIMULATED (0x6865D0) pauses SUSPENSION, ENGINE, RIGIDBODY, DAMAGE (...); SuspensionSimple's
            // OnTaskSimulate also returns while !IsModeling — EMULATED / INACTIVE run no behaviour here
            if (Car && Car.PhysicsMode != PlayerCar.PhysicsModes.Simulated) { PendingForce = PendingTorque = Vector3.zero; return; }
            float dT = Time.fixedDeltaTime;
            // [ours] a body moved by more than its speed allows since the last step was placed (PlayerCar.Teleport: CarReset,
            // the rolling-start release, RestoreStartPositions): PlaceObject + Behavior::Reset, then MatchSpeed
            var at = Rb.position;
            if (_havePos && (at - _lastPos).sqrMagnitude > Sq(Rb.linearVelocity.magnitude * dT * 2f + 1.5f))
            {
                ResetPhysics();
                MatchSpeed(ForwardSpeed);
                _stageHeld = false;   // PlaceObject: the staging hold is re-taken at the new spot
            }
            _lastPos = at;
            _havePos = true;
            RigidBodyFrame(dT);
            if (Car)
            {
                var input = Car.Input;
                Gas = Mathf.Clamp01(input.Throttle);
                Brake = Mathf.Clamp01(input.Brake);
                EBrake = input.Handbrake ? 1f : 0f;
                Steer = Mathf.Clamp(input.Steer, -1f, 1f);
                Nitrous = input.Nitrous;
                Staging = Car.Staging;
                // IVehicle::GetDriverStyle: STYLE_DRAG for the race's drag cars (RaceManager sets PlayerCar.Dragster)
                DriverStyle = Car.Dragster ? Chassis.StyleDrag : 0;
            }
            HadCollision = _hit;
            GroundContact = _groundHit;
            _groundHit = false;
            bool hadObject = _hitObject;
            int contactPoints = _contactPoints;
            _hit = false;
            _hitObject = false;
            _contactPoints = 0;
            PendingForce = Vector3.zero;
            PendingTorque = Vector3.zero;
            if (Sleeping)
            {
                // asleep: no behaviour forces reach the body (OnBeginFrame adds no gravity, DoIntegration clears v/ω)
                Rb.linearVelocity = Vector3.zero;
                Rb.angularVelocity = Vector3.zero;
                if (Car) Bridge();
                return;
            }
            // RigidBody::OnBeginFrame: gravity is rigidbodyspecs GRAVITY·mass (−9.813 for cars) [ours: Unity's own gravity
            // stays on, this adds the difference]
            Rb.AddForce(Vector3.up * ((Specs.Spec.Body != null ? Specs.Spec.Body.Gravity : -9.813f) - Physics.gravity.y) * Rb.mass);
            bool destroyed = IsDestroyed;
            Engine?.Simulate(dT, destroyed ? 0f : Gas, Rb.rotation * Vector3.up);
            if (Racer != null)
            {
                // EngineRacer::DoThrottle reads the raw IInput gas; Chassis::ComputeState's destroyed rule zeroes it for the
                // chassis only, so a wrecked cop's engine is fed 0 here like EngineTraffic's [ours]
                float gas = Gas;
                if (destroyed) Gas = 0f;
                Racer.Simulate(dT);
                Gas = gas;
            }
            Chassis.Simulate(dT);
            Damage?.Simulate(dT);
            if (Damage != null && Specs.Damage == "DamageCopCar") CopUprightTask(dT);
            Rb.AddForce(PendingForce);
            Rb.AddTorque(PendingTorque);
            // the pre-step velocity and heading feed CarContacts' head-on rule (as PlayerCar does)
            if (_box)
            {
                CarContacts.Refresh(_box, false, Chassis.NumWheelsOnGround > 2, false, Rb.linearVelocity, transform.forward);
                CarContacts.RefreshGround(_box, Chassis.NumWheelsOnGround == 4, hadObject);
            }
        }

        void Update()
        {
            if (Chassis == null) return;
            if (Car) Bridge();
            if (WheelVisuals == null || _hub == null) return;
            for (int i = 0; i < 4 && i < WheelVisuals.Length; i++)
            {
                var w = WheelVisuals[i];
                if (!w) continue;
                // the hub rides on the compression: contact point (arm + up·(compression − ride)) + radius
                float y = Specs.Pivot.y + Chassis.Wheels[i].LocalArm.y + Chassis.Compression(i) - Chassis.RideHeight(i) + Chassis.WheelRadius(i);
                w.localPosition = new Vector3(_hub[i].x, y, _hub[i].z);
                w.localRotation = Quaternion.Euler(0, Chassis.WheelSteerDeg(i), 0);
                _spinDeg[i] = Mathf.Repeat(_spinDeg[i] + Chassis.WheelAngularVelocity(i) * Mathf.Rad2Deg * Time.deltaTime, 360f);
                if (_spin[i]) _spin[i].localRotation = Quaternion.Euler(_spinDeg[i], 0, 0);
            }
        }

        void OnDestroy()
        {
            if (_box) TrafficContacts.Unregister(_box);
            else TrafficContacts.Unregister(GetComponent<BoxCollider>());
        }

        void OnCollisionEnter(Collision c) => OnHit(c, true);
        void OnCollisionStay(Collision c)
        {
            _hit = true;
            _contactPoints += c.contactCount;
            if (c.rigidbody != null) _hitObject = true;
            else if (c.contactCount > 0 && WorldCollision.IsGround(c.collider)) _groundHit = true;
        }

        void OnHit(Collision c, bool enter)
        {
            _hit = true;
            if (c.rigidbody != null) _hitObject = true;   // HAS_HAD_OBJECT_COLLISION (RigidBody.cpp:1110)
            else if (c.contactCount > 0 && WorldCollision.IsGround(c.collider)) _groundHit = true;
            _contactPoints += c.contactCount;
            if (c.contactCount == 0) return;   // contact modification can leave a collision with no contacts
            var cp = c.GetContact(0);
            // the contact normal points from the other body into this one's side: this body is pushed along +normal
            var n = cp.normal;
            if (c.rigidbody == null && Chassis is SuspensionSimple ss) ss.OnWorldCollision(n, cp.point, c.relativeVelocity);
            if (Damage == null || !enter) return;
            var local = Quaternion.Inverse(Rb.rotation);
            var arm = local * (cp.point - BodyOrigin);
            // smackable NO_CAR_EFFECT (DamageVehicle.cpp:158-163): a loose prop with it does not count as an impact
            var sm = c.collider ? c.collider.GetComponentInParent<Smackable>() : null;
            bool noCarEffect = sm && sm.NoCarEffect && c.rigidbody != null;
            // DamageCopCar::OnImpact: a loose smackable damages a cop only when immobile or over 2000 kg
            // (Tweak_MinMassSmackableForCopDamage)
            if (sm && Specs.Damage == "DamageCopCar" && c.rigidbody != null && !c.rigidbody.isKinematic && c.rigidbody.mass <= 2000f) return;
            Damage.OnCollision(c.relativeVelocity.magnitude, c.impulse.magnitude, Rb.mass, arm, local * n, c.rigidbody != null, noCarEffect, cp.point);
        }

        // ---- the disabled PlayerCar's read-outs (engine audio, lights, skids), once a frame ----

        void Bridge()
        {
            int n = Chassis.NumWheelsOnGround;
            float rpm = Engine != null ? Engine.Rpm : Racer != null ? Racer.Rpm : Car.Spec != null ? Car.Spec.IdleRpm : 800f;
            float skid = 0f;
            if (Rb.linearVelocity.sqrMagnitude > 9f)
                for (int i = 0; i < 4; i++)
                    if (Chassis.Wheels[i].IsOnGround) skid = Mathf.Max(skid, Mathf.Abs(Mathf.Sin(Chassis.WheelSlipAngle(i))) * 1.5f);
            Car.SetExternalSimState(rpm, PlayerGear, n, skid);
            // per-wheel read-outs and nitrous for effects and audio (PlayerCar.SetExternalWheelState / SetExternalNos)
            for (int i = 0; i < 4; i++)
            {
                var w = Chassis.Wheels[i];
                float omega = Chassis.WheelAngularVelocity(i), slip = Chassis.WheelSlip(i);
                Car.SetExternalWheelState(i, w.IsOnGround, slip, Chassis.WheelSlipAngle(i), omega * Chassis.WheelRadius(i) - slip, Chassis.WheelLoad(i), omega, w.Surface);
            }
            Car.SetExternalNos(Racer != null && Racer.NosEngaged, Racer != null ? Racer.NosCapacity : 0f);
            Car.TickPVehicle(Time.deltaTime);   // PVehicle::OnTaskSimulate part: screen times, off world, invulnerability
        }
    }
}
