using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// An AI opponent in a race: AIVehicleRacecar with AIActionRace and AIActionGetUnstuck (specs/ai_racer.md §4-7).
    /// It thinks at 60 Hz and drives every 120 Hz tick.
    /// <list type="bullet">
    /// <item>A RoadNav runs 30–100 m ahead along the race route and leaves a cookie trail. The car aims at the funnel
    /// apex of that trail, which is the racing line.</item>
    /// <item>Corner speed comes from tyre grip plus a share of downforce, through the trail curvature. An
    /// acceleration-limited speed limit then rises at the modelled rate and drops instantly when needed.</item>
    /// <item>Throttle uses an incremental PID; steering uses an adaptive PID on the body heading error.</item>
    /// <item>Nitrous is gated by bottle, time and skill. Catch-up shifts skill rather than top speed.</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class AIRacer : MonoBehaviour
    {
        public PlayerCar Car;
        public RaceManager Race;
        public RaceManager.Entrant Me;
        /// <summary>ComputeSkill base: QR {0.15, 0.4, 1.0}[difficulty]; career SkillLevel/100.</summary>
        public float BaseSkill = 0.4f;
        /// <summary>MinimumAIPerformance (career; 0 in quick race): how far the AI's car may exceed the player's.</summary>
        public float MinPerformance;
        public bool CatchUpOn = true;
        public bool Drag;
        /// <summary>IPerpetrator::GetHeat (AIPerpVehicle::mHeat): 1 at construction (AIVehicle.cpp:1959, SetHeat(1)); a cop
        /// pursuit of this racer raises it (<see cref="Police.RacerPursuit"/>).</summary>
        public float Heat { get; set; } = 1f;
        /// <summary>IPerpetrator::GetLastTrafficHitTime (AIPerpVehicle::OnCausedCollision, AIVehicle.cpp:2483-2490): the time of
        /// the last intentional causal hit on a traffic car (-1 = never); patrol cops read it (CheckForPursuit, within 1 s).</summary>
        public float LastTrafficHitTime { get; private set; } = -1f;
        /// <summary>GRacerInfo::ForceStop (the racer was busted by the police, AIPursuit.cpp:1965-1975): the car stops for good.</summary>
        public bool Stopped { get; private set; }
        public void ForceStop() { Stopped = true; if (Car) Car.Input = new DriveInput { Brake = 1, Handbrake = true }; }

        public RoadNav Nav { get; private set; }
        public Vector3 DriveTarget { get; private set; }
        public float DriveSpeed { get; private set; }
        public float SpeedLimit => _speedLimit;
        /// <summary>Logs each racer's nav state once a second (tests).</summary>
        public static bool Log;
        float _logTimer;

        // potentials (ComputePotentials)
        float _startGrip = 1.5f, _endGrip = 2f, _topSpeed = 60f, _nosCapability, _usableNos, _bottleTime, _accelMult = 1f, _topMult = 1f;
        float[] _accel = new float[10];
        float _speedLimit, _potentialSpeed, _lastSpeed, _thinkAcc, _nosTimer;
        bool _nosOn, _needStop;
        // glue (catch-up)
        float _glueSkill, _glueOutput, _glueTimer;
        readonly PidError _glue = new PidError(10, 5, 1f);
        // steering / throttle controllers
        float _tb;
        readonly PidError _speedErr = new PidError(4, 4, 30f);
        readonly PidError _headingErr = new PidError(5, 5, 30f);
        readonly AdaptivePid _steerPid = new AdaptivePid();
        // unstuck
        float _stuckTimer, _reverseOverride;
        Vector3 _stuckAt;
        Vector3 _lastTarget;
        bool _haveTarget;

        static readonly float[] HeadingModelX = { 0, 3, 6, 9, 12, 15, 18, 21, 24, 27 };
        static readonly float[] HeadingModelY = { 0, -1, -2.05f, -3.5f, -4.94f, -6.14f, -7.35f, -8.55f, -9.4f, -10f };
        static readonly float[] LowP = { 0.328f, 0.22f, 0.148f, 0.115f, 0.09f, 0.074f, 0.057f, 0.049f, 0.043f, 0.04f };
        static readonly float[] LowI = { 0.21f, 0.244f, 0.267f, 0.29f, 0.305f, 0.321f, 0.328f, 0.336f, 0.341f, 0.344f };
        static readonly float[] LowD = { 0, 0.0758f, 0.0758f, 0.0738f, 0.0604f, 0.047f, 0.047f, 0.0403f, 0.0403f, 0.0403f };

        float Skill => Mathf.Clamp01(BaseSkill + _glueSkill);
        float CatchupCheat => Mathf.Clamp01(BaseSkill + _glueSkill - 1f) * 0.5f;

        // ---- the vehicle model (specs/ai_racer.md §11, traffic_physics.md §11) ----

        /// <summary>
        /// The exe's model for an AI racer: PVehicle::LookupBehaviorSignature (0x67F0D0) gives a CAR with driver RACER (3)
        /// SuspensionSimple and the pvehicle's EngineRacer (EngineDragster in drag style), so opponents run
        /// <see cref="TrafficPhysics.TrafficVehicle"/> like the cops (CopCar.AttachChassis), not the player's
        /// SuspensionRacer sim. Off: the old PlayerCar racer model [ours: the fall-back switch].
        /// </summary>
        public static bool UseSimpleChassis = true;
        /// <summary>The opponent's SuspensionSimple + EngineRacer, or null on the PlayerCar model.</summary>
        public TrafficPhysics.TrafficVehicle Sim { get; private set; }
        const int DriverRacer = 3;   // DriverClass DRIVER_RACER (Sim/SimTypes.h:218)
        // AIVehicle reverse state (AIVehicle.h:348-350): mSteeringBehind, mReversingSpeed
        bool _steeringBehind, _reversingSpeed;

        void Awake()
        {
            if (!Car) Car = GetComponent<PlayerCar>();
            AttachChassis();
        }

        /// <summary>
        /// Builds the racer's behaviours on its car (PVehicle::LoadBehaviors with DRIVER_RACER). The PlayerCar keeps input,
        /// audio, damage and lookups but stops simulating; TrafficVehicle feeds its rpm, gear and wheels [ours: the bridge].
        /// Called from Awake (RaceManager.SpawnOpponent adds the component to the spawned car); false = the racer model stays.
        /// </summary>
        public bool AttachChassis()
        {
            if (Sim) return true;
            if (!UseSimpleChassis || !Car || Car.Spec == null || string.IsNullOrEmpty(Car.Spec.Name)) return false;
            var boot = GameBootstrap.Instance;
            if (boot && (Car == boot.Player || Car == boot.Player2)) return false;   // a human's car keeps its own model
            try
            {
                var specs = TrafficPhysics.VehicleSpecs.ForRacer(Car.Spec.Name, Car.Spec, Car.Dragster);
                if (specs == null) return false;
                Sim = TrafficPhysics.TrafficVehicle.Attach(Car.gameObject, specs, Car, Car.WheelVisuals);
                if (!Sim) return false;
                Sim.DriverClass = DriverRacer;
                Sim.ResetPhysics();
                Sim.MatchSpeed(Vector3.Dot(Body.linearVelocity, Car.transform.forward));
                return true;
            }
            catch (System.Exception e) { Debug.LogWarning($"NFSMW racer {name}: chassis: {e.Message}"); Sim = null; return false; }
        }

        TrafficPhysics.EngineRacer Engine => Sim ? Sim.Racer : null;
        /// <summary>The nitrous bottle 0..1 (EngineRacer's tank on the simple model).</summary>
        float Bottle => Engine != null ? Engine.NosCapacity : Car.Nos;

        void OnEnable()
        {
            if (!Car) Car = GetComponent<PlayerCar>();
            if (Car) { Car.AiControlled = true; Car.UseDeviceInput = false; }
            BeginAction();
        }

        void OnDisable()
        {
            if (Car) Car.Input = new DriveInput { Brake = 1 };
            // [ours] the race left the car: back to the simulation where there is ground, else it stays where it is
            if (_emulated && Car && TrafficCar.GroundBelow(Body.position + Vector3.up * 3f, 60f, out _)) { _emulated = false; Body.isKinematic = false; Car.SetPhysicsMode(PlayerCar.PhysicsModes.Simulated); }
        }

        /// <summary>AIActionRace::BeginAction + StartRace: a fresh nav at the car, potentials, the nav pre-advanced 60 m.</summary>
        void BeginAction()
        {
            if (!Car || Car.Spec == null) return;
            Nav = new RoadNav { RaceFilter = true, CookieTrail = true, HalfWidth = HalfWidth() };
            if (Drag) { Nav.Selectable = RoadGraph.DragSelectable; }
            Nav.AllowedShortcuts = Shortcuts();
            if (!Nav.InitAtPoint(Car.transform.position, Car.transform.forward, false, 1f))
            {
                Nav.RaceFilter = false;
                Nav.InitAtPoint(Car.transform.position, Car.transform.forward, false, 1f);
            }
            ComputePotentials();
            _speedLimit = Mathf.Max(0, Vector3.Dot(Body.linearVelocity, Car.transform.forward));
            _lastSpeed = _speedLimit;
            _tb = 0;
            _haveTarget = false;
            if (Nav.Valid) Nav.IncNavPosition(Drag ? 30f : 60f, 60f);
        }

        HashSet<int> _shortcuts;

        /// <summary>
        /// MakeShortcutDecision (WRoadNetwork.cpp:3100): each race shortcut is taken with chance
        /// min + shortcutSkill·(max − min), rolled once (shortcutSkill = clamp(base + glue output)).
        /// </summary>
        HashSet<int> Shortcuts()
        {
            if (_shortcuts != null) return _shortcuts;
            _shortcuts = new HashSet<int>();
            if (Race == null || Race.Event == null) return _shortcuts;
            float sk = Mathf.Clamp01(BaseSkill + _glueOutput);
            for (int i = 0; i < Race.Event.Shortcuts.Count; i++)
            {
                var (_, min, max) = Race.Event.Shortcuts[i];
                if (Random.value < min + sk * (max - min)) _shortcuts.Add(i);
            }
            return _shortcuts;
        }

        Rigidbody _body;
        Rigidbody Body => _body ? _body : _body = Car.GetComponent<Rigidbody>();

        float HalfWidth()
        {
            var box = Car.GetComponent<BoxCollider>();
            return box ? box.size.x * 0.5f : 1f;
        }

        // ---- potentials (Race.cpp:408, performance matching) ----

        void ComputePotentials()
        {
            var s = Car.Spec;
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            var ps = player && player != Car ? player.Spec : s;
            // career: raised towards MinimumAIPerformance; quick race: never better than the weaker car (b = 0).
            // The player's 0..1 performance rating is not ported, so it counts as 0 here [?].
            float b = Mathf.Clamp01(MinPerformance);
            float myGrip = Mathf.Min(s.StaticGripFront, s.StaticGripRear), pGrip = Mathf.Min(ps.StaticGripFront, ps.StaticGripRear);
            var myTable = AccelTable(s, out float myTop);
            var pTable = AccelTable(ps, out float pTop);
            _startGrip = Mathf.Lerp(Mathf.Min(myGrip, pGrip), myGrip, b);
            _topSpeed = Mathf.Lerp(Mathf.Min(myTop, pTop), myTop, b);
            float g = 9.81f;
            float down = _topSpeed * 2f * s.AeroCoefficient * 1000f;
            _endGrip = _startGrip * (g + down / Mathf.Max(1f, s.Mass)) / g;
            for (int i = 0; i < 10; i++) _accel[i] = Mathf.Lerp(Mathf.Min(myTable[i], pTable[i]), myTable[i], b);
            // nitrous
            float myBoost = Mathf.Max(0, s.NosTorqueBoost), pBoost = Mathf.Max(0, ps.NosTorqueBoost);
            float myCap = Mathf.Max(0, s.NosCapacity), pCap = Mathf.Max(0, ps.NosCapacity);
            _nosCapability = Mathf.Lerp(Mathf.Min(myBoost, pBoost), myBoost, b);
            _usableNos = myCap > 0 && myBoost > 0 ? Mathf.Min(myCap, pCap) / myCap * (_nosCapability / myBoost) : 0;
            _bottleTime = myCap;
            var ai = GameData.Attributes?.Get("aivehicle", "racers");
            _accelMult = ai != null && ai.Has("AccelerationMultiplier") ? ai.Float("AccelerationMultiplier", 0, 1f) : 1f;
            _topMult = ai != null && ai.Has("TopSpeedMultiplier") ? ai.Float("TopSpeedMultiplier", 0, 1f) : 1f;
        }

        /// <summary>
        /// Physics::Info::ComputeAccelerationTable (speed.exe 0x673810, decompiled from the exe). For each forward
        /// gear: the speed at the redline, v = RED_LINE/(ratio·final)·r·π/30, and the acceleration at peak torque
        /// there, a = (peakTorque·ratio·final·efficiency/r − DRAG·v²)/MASS. Where a gear's acceleration goes negative the
        /// zero crossing is interpolated from the previous gear. Top speed is the largest such v. The 10-entry table
        /// samples that piecewise-linear graph over 0..top (clamped, so the first point covers low speeds).
        /// </summary>
        static float[] AccelTable(CarSpec s, out float top)
        {
            const float LBFT = 1.3558f;
            float r = Mathf.Max(0.2f, s.RimRadius);
            float peak = 0;
            for (float rpm = s.IdleRpm; rpm <= s.RedLine; rpm += 50f) peak = Mathf.Max(peak, s.TorqueAt(rpm));
            peak *= LBFT;
            var xs = new List<float>();
            var ys = new List<float>();
            top = 0;
            float prevV = 0, prevA = 0;
            for (int g = 2; g < s.GearRatios.Length; g++)
            {
                float ratio = s.GearRatios[g] * s.FinalGear;
                if (ratio <= 0) break;
                float eff = g < s.GearEfficiency.Length ? s.GearEfficiency[g] : 1f;
                float force = peak * ratio * eff / r;
                float v = s.RedLine / ratio * r * 0.10471976f;
                float a = (force - v * s.DragCoefficient * v) / Mathf.Max(1f, s.Mass);
                if (a <= 0)
                {
                    if (prevA <= 0) break;
                    v = (v - prevV) * (1f - prevA / (prevA - a)) + prevV;
                    a = 0;
                }
                top = Mathf.Max(top, v);
                xs.Add(v);
                ys.Add(a);
                prevV = v;
                prevA = a;
                if (a <= 0) break;
            }
            var t = new float[10];
            if (xs.Count == 0 || top <= 0) { top = s.TopSpeed; return t; }
            for (int i = 0; i < 10; i++)
            {
                float v = top * i / 9f;
                float a = ys[0];
                if (v >= xs[xs.Count - 1]) a = ys[ys.Count - 1];
                else
                    for (int k = 1; k < xs.Count; k++)
                        if (v <= xs[k]) { a = v <= xs[k - 1] ? ys[k - 1] : Mathf.Lerp(ys[k - 1], ys[k], (v - xs[k - 1]) / (xs[k] - xs[k - 1])); break; }
                t[i] = Mathf.Max(0, a);
            }
            return t;
        }

        float TableValue(float[] tab, float x, float max)
        {
            float f = Mathf.Clamp01(x / Mathf.Max(1e-3f, max)) * (tab.Length - 1);
            int i = Mathf.Min((int)f, tab.Length - 2);
            return Mathf.Lerp(tab[i], tab[i + 1], f - i);
        }

        // ---- speed (Race.cpp:475-635) ----

        /// <summary>GetSpeedLimit: v²|κ| = g(f0 + f1·v).</summary>
        static float SpeedLimitFor(float k, float f0, float f1, float top)
        {
            k = Mathf.Abs(k);
            const float g = 9.8f;
            if (k < 1e-5f) return top;
            float n = g * f1 + Mathf.Sqrt(g * f1 * g * f1 + 4f * g * f0 * k);
            return Mathf.Min(top, n / (2f * k));
        }

        float PotentialSpeed(float k, float skill)
        {
            if (_topSpeed <= 0 || Nav.DeadEnd) return 0;
            if (Drag) return _topSpeed * _topMult * Mathf.Lerp(0.85f, 1f, skill);
            float scale = Mathf.Lerp(0.36f, 0.9f, skill);
            float f0 = _startGrip, f1 = (Mathf.Lerp(_startGrip, _endGrip, scale) - _startGrip) / _topSpeed;
            return SpeedLimitFor(k, f0, f1, _topSpeed) * _topMult * Mathf.Lerp(0.85f, 1f, skill);
        }

        float PotentialAcceleration(float v, float skill, bool nos)
        {
            float a = TableValue(_accel, Mathf.Abs(v), _topSpeed) * _accelMult * (Drag ? Mathf.Lerp(0.82f, 1f, skill) : Mathf.Lerp(0.65f, 1f, skill));
            if (nos) a *= 1f + _nosCapability;
            float cheat = CatchupCheat;   // AiCatchupAcceleration {1, 1.33, 1.66, 2} over 0..1
            a *= 1f + cheat;
            a -= 9.81f * Car.transform.forward.y;
            return Mathf.Max(0, a);
        }

        // ---- the think (60 Hz) and the drive (every tick) ----

        void FixedUpdate()
        {
            if (!Car || Car.Spec == null || Nav == null) return;
            if (Stopped) { Car.Input = new DriveInput { Brake = 1, Handbrake = true }; return; }
            _thinkAcc += 0.5f;
            if (_thinkAcc >= 1f)
            {
                _thinkAcc -= 1f;
                Think(Time.fixedDeltaTime * 2f);
            }
            Drive(Time.fixedDeltaTime);
        }

        void Think(float dt)
        {
            SimplePhysics(dt);
            var pos = Car.transform.position;
            // emulated: IRigidBody::SetLinearVelocity(dir · driveSpeed) (a kinematic body here keeps no velocity)
            var vel = _emulated ? Car.transform.forward * _emulatedSpeed : Body.linearVelocity;
            float speed = Vector3.Dot(vel, Car.transform.forward);
            if (!Nav.Valid)
            {
                Nav.InitAtPoint(pos, Car.transform.forward, false, 1f);
                if (!Nav.Valid) { DriveSpeed = 0; DriveTarget = pos + Car.transform.forward * 10f; return; }
            }
            Glue(dt);
            float skill = Skill;
            // ICheater::GetCatchupCheat (AIVehicleRacecar): EngineRacer's NOS drain / recharge and torque boost read it
            if (Engine != null) Engine.CatchupCheat = CatchupCheat;

            // route: replan to the race target when it moves (FindPath), else follow InRace segments in race direction
            var target = Race ? Race.TargetFor(Me) : (Vector3?)null;
            _needStop = target == null;
            if (target.HasValue && (!_haveTarget || (target.Value - _lastTarget).sqrMagnitude > 25f))
            {
                _lastTarget = target.Value;
                _haveTarget = true;
                Nav.FindPath(target.Value);
            }

            // look-ahead L = clamp(30 + 0.7·speedLimit, 30, 100); advance the nav so it sits L ahead of the car
            float L = Mathf.Clamp(30f + 0.7f * _speedLimit, 30f, 100f);
            var N = Nav.Position - pos; N.y = 0;
            var F = Nav.Forward; F.y = 0;
            float a = Vector3.Dot(F, F), bq = 2f * Vector3.Dot(N, F), c = Vector3.Dot(N, N) - L * L;
            float disc = bq * bq - 4f * a * c;
            if (disc > 0 && a > 1e-6f)
            {
                float d = (-bq + Mathf.Sqrt(disc)) / (2f * a);
                if (d > 0) Nav.IncNavPosition(d, L);
            }
            var aim = Nav.UpdateOccludedPosition(pos, HalfWidth());

            // CheckOffPath: more than 2 m outside the corridor and a better nav at the car -> start again there
            if (Nav.OutOfBounds > 2f && Time.frameCount % 4 == 0)
            {
                var fresh = new RoadNav { RaceFilter = Nav.RaceFilter, CookieTrail = true, HalfWidth = Nav.HalfWidth, Selectable = Nav.Selectable };
                if (fresh.InitAtPoint(pos, Car.transform.forward, false, 1f))
                {
                    fresh.UpdateOccludedPosition(pos, HalfWidth());
                    if (fresh.OutOfBounds < Nav.OutOfBounds)
                    {
                        Nav = fresh;
                        _haveTarget = false;
                        Nav.IncNavPosition(L, L);
                        aim = Nav.UpdateOccludedPosition(pos, HalfWidth());
                    }
                }
            }

            float k = Nav.CookieTrailCurvature(pos, vel);
            _potentialSpeed = PotentialSpeed(k, skill);

            // the acceleration-limited speed limit (Race.cpp:965-997)
            float potAcc = PotentialAcceleration(Mathf.Max(speed, _speedLimit), skill, _nosOn);
            float actAcc = (speed - _lastSpeed) / Mathf.Max(1e-4f, dt);
            _lastSpeed = speed;
            if (actAcc < 0 && speed < _speedLimit && _speedLimit < _potentialSpeed)
                _speedLimit += Mathf.Min(potAcc + actAcc, 0) * dt;
            if (_speedLimit < _potentialSpeed)
            {
                float t = _speedLimit / Mathf.Max(1e-3f, _potentialSpeed);
                float e = Mathf.Lerp(1.5f, 2f, skill);
                _speedLimit += Mathf.Clamp(potAcc - actAcc * Mathf.Pow(Mathf.Max(0, t), e), 0, potAcc) * dt;
            }
            _speedLimit = Mathf.Clamp(_speedLimit, 0, _potentialSpeed);

            DriveTarget = aim;
            DriveSpeed = _needStop ? 0 : _speedLimit;
            if (Log && (_logTimer += dt) >= 1f)
            {
                _logTimer = 0;
                Debug.Log($"NFSMW racer {name}: seg {Nav.Seg}{(Nav.Fwd ? ">" : "<")} t {Nav.T:0.00} off {Nav.LaneOffset:0.0} [{Nav.Left:0.0},{Nav.Right:0.0}] path {(Nav.Path != null ? Nav.Path.Count : -1)} dead {Nav.DeadEnd} occl {Nav.RoadOcclusion} oob {Nav.OutOfBounds:0.0} k {k:0.0000} pot {_potentialSpeed:0.0} lim {_speedLimit:0.0} v {speed:0.0} car {pos} nav {Nav.Position} aim {aim} target {target}");
            }
            Nitrous(speed, potAcc, actAcc, skill, dt);
            Unstuck(pos, dt);
        }

        // ---- off-world simple physics (AIVehicleRacecar::Update / ShouldDoSimplePhysics, Racecar.cpp:542-575;
        //      AIVehicle::UpdateSimplePhysics / EnableSimplePhysics / DisableSimplePhysics, AIVehicle.cpp:1214-1364) ----

        static readonly float[] OffWorldAccel = { 0.5f, 1f }, OffWorldSpeed = { 0.75f, 1f };   // Tweak_OffWorldAccel / Speed
        bool _emulated, _offWorld;
        float _groundTimer, _emulatedSpeed;
        /// <summary>PHYSICS_MODE_EMULATED: the car has no world under it and is moved along its route by the AI.</summary>
        public bool SimplePhysicsActive => _emulated;

        /// <summary>
        /// IVehicle::IsOffWorld [?: no world collision ground under the car, checked twice a second as TrafficCar does];
        /// ShouldDoSimplePhysics: off the world and not staging, animating (NIS) or the player. Entering: EnableSimplePhysics
        /// = SetPhysicsMode(EMULATED) (the body is moved, kinematic here); leaving: DisableSimplePhysics (AIVehicle.cpp:1337-1360)
        /// = SetPhysicsMode(SIMULATED), SetVehicleOnGround keeping the speed and SetInvulnerability(INVULNERABLE_FROM_PHYSICS_SWITCH,
        /// 1 s), which PlayerCar.SetPhysicsMode applies (CarContacts passes the car through other cars for that second).
        /// </summary>
        void SimplePhysics(float dt)
        {
            var pos = Body.position;
            if ((_groundTimer -= dt) <= 0f)
            {
                _groundTimer = 0.5f;
                _offWorld = !TrafficCar.GroundBelow(pos + Vector3.up * 3f, 60f, out _) || pos.y < -200f;
            }
            bool want = _offWorld && !Car.Staging && !NisPlayer.Active && !(Race && Race.Countdown);
            if (want && !_emulated)
            {
                _emulated = true;
                _emulatedSpeed = Body.linearVelocity.magnitude;
                Body.isKinematic = true;
                Car.SetPhysicsMode(PlayerCar.PhysicsModes.Emulated);   // EnableSimplePhysics (AIVehicle.cpp:1329)
            }
            else if (!want && _emulated)
            {
                _emulated = false;
                Body.isKinematic = false;
                Car.SetPhysicsMode(PlayerCar.PhysicsModes.Simulated);   // DisableSimplePhysics: back to SIMULATED + 1 s INVULNERABLE_FROM_PHYSICS_SWITCH
                var fwd = RoadGraph.FlatDir(Car.transform.forward);
                float y = TrafficCar.GroundBelow(pos + Vector3.up * 3f, 60f, out float gy) ? gy : pos.y;
                Car.transform.SetPositionAndRotation(new Vector3(pos.x, y + 0.3f, pos.z), Quaternion.LookRotation(fwd, Vector3.up));
                Body.position = Car.transform.position;
                Body.rotation = Car.transform.rotation;
                Body.linearVelocity = fwd * _emulatedSpeed;
                Body.angularVelocity = Vector3.zero;
                if (Sim) { Sim.ResetPhysics(); Sim.MatchSpeed(_emulatedSpeed); }
            }
            if (_emulated) UpdateSimplePhysics(dt);
        }

        /// <summary>UpdateSimplePhysics: towards the drive target 1 m up; the speed falls at 30 m/s² to the drive speed or
        /// rises by the accel table × lerp(0.5, 1, skill), capped at the top speed × lerp(0.75, 1, skill) (× −0.5 in
        /// reverse); upright off the world; height = the current cookie centre + 1 + ride height (+ dimension.y: the body
        /// origin here is the box bottom).</summary>
        void UpdateSimplePhysics(float dT)
        {
            var position = Body.position;
            var dest = DriveTarget + Vector3.up;
            var dir = dest - position;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Car.transform.forward;
            float skill = Skill, current = _emulatedSpeed, drive = current;
            if (current > DriveSpeed) drive = Mathf.Max(drive - dT * 30f, DriveSpeed);
            else drive = Mathf.Min(drive + TableValue(_accel, current, _topSpeed) * dT * Mathf.Lerp(OffWorldAccel[0], OffWorldAccel[1], skill), DriveSpeed);
            float top = _topSpeed * Mathf.Lerp(OffWorldSpeed[0], OffWorldSpeed[1], skill);
            drive = Mathf.Min(top, Mathf.Max(0f, drive));
            _emulatedSpeed = drive;
            bool reversing = Engine != null ? Engine.IsReversing : Car.Gear < 0;
            if (reversing) drive *= -0.5f;
            var np = position + dir * (drive * dT);
            // elevation: the nav's current cookie (the one nearest the car [?]) + 1, plus the largest ride height
            float elevation = dest.y;
            if (Nav != null && Nav.CookieCount > 0)
            {
                float bd = float.MaxValue;
                for (int i = 0; i < Nav.CookieCount; i++)
                {
                    var c = Nav.CookieAt(i);
                    float d = (c.Centre - position).sqrMagnitude;
                    if (d < bd) { bd = d; elevation = c.Centre.y + 1f; }
                }
            }
            float ride = 0f;
            if (Sim) for (int i = 0; i < 4; i++) ride = Mathf.Max(ride, Sim.Chassis.RideHeight(i));
            np.y = elevation + ride;
            var flat = RoadGraph.FlatDir(dir);
            Body.MovePosition(np);
            Body.MoveRotation(Quaternion.LookRotation(dir.y * dir.y < 0.99f ? dir : flat, Vector3.up));
        }

        /// <summary>GetPotentialNOS and the on/off hysteresis (Race.cpp:673, 1032-1069).</summary>
        void Nitrous(float v, float potAcc, float actAcc, float skill, float dt)
        {
            _nosTimer += _nosOn ? dt : -dt;
            float potNos = 0;
            if (v >= 10f && v < _speedLimit && _nosCapability > 0 && _usableNos > 0)
            {
                float useable = _usableNos * Mathf.Lerp(0.33f, 1f, skill);
                float need = _nosOn ? 1f - useable : useable * Mathf.Lerp(0.5f, 0.3f, skill);
                float bottle = Bottle;
                potNos = bottle > need ? (bottle - need) * _bottleTime : 0;
            }
            bool can = !_needStop && potNos > 0 && !(Race && Race.Countdown);
            float kk = Mathf.Lerp(0.25f, 1f, skill);
            float speedGap = kk * (_potentialSpeed - v), accelGap = kk * (potAcc - actAcc);
            if (!_nosOn)
            {
                if (can && _nosTimer < -Mathf.Lerp(20f, 4f, skill) && !Nav.Occluded && speedGap > 15f && accelGap > 0.5f * potAcc)
                { _nosOn = true; _nosTimer = 0; }
            }
            else if (!can || (_nosTimer > Mathf.Lerp(2f, 4f, skill) && (speedGap < 5f || accelGap < 0.15f * potAcc)))
            { _nosOn = false; _nosTimer = 0; }
        }

        /// <summary>AIActionGetUnstuck: gas ≥ 0.5 for 3 s within 3 m ⇒ 2 s reverse override, then a fresh nav.</summary>
        void Unstuck(Vector3 pos, float dt)
        {
            if (_reverseOverride > 0)
            {
                _reverseOverride -= dt;
                if (_reverseOverride <= 0)
                {
                    // UpdateReverseOverride (AIVehicle.cpp:1023): out of reverse into first
                    _steeringBehind = false;
                    if (Engine != null && Engine.IsReversing) Engine.Shift(TrafficPhysics.Chassis.GFirst);
                    BeginAction();
                    _stuckTimer = 0;
                }
                return;
            }
            if (Car.Input.Throttle >= 0.5f)
            {
                if (_stuckTimer <= 0) _stuckAt = pos;
                _stuckTimer += dt;
                if (_stuckTimer > 3f)
                {
                    if ((pos - _stuckAt).sqrMagnitude < 9f)
                    {
                        // SetReverseOverride (AIVehicle.cpp:1012): the opposite of the current gear
                        _reverseOverride = 2f;
                        if (Engine != null) Engine.Shift(Engine.IsReversing ? TrafficPhysics.Chassis.GFirst : TrafficPhysics.Chassis.GReverse);
                    }
                    _stuckTimer = 0;
                }
            }
            else _stuckTimer = 0;
        }

        void Drive(float dt)
        {
            var input = new DriveInput();
            var fwd = Car.transform.forward;
            var pos = Car.transform.position;
            float speed = Vector3.Dot(Body.linearVelocity, fwd);
            var dir = RoadGraph.FlatDir(DriveTarget - pos);
            var f = RoadGraph.FlatDir(fwd);
            // OnSteering divides by ISuspension::GetMaxSteering: SuspensionSimple's 45..60° (it applies STEERING itself);
            // the racer model's AI steering is STEERING·45°·input
            float maxSteer = Sim ? Mathf.Max(5f, Sim.MaxSteeringDeg) : Mathf.Max(5f, Car.Spec.Steering * 45f);
            if (Engine != null) { DriveSimple(input, fwd, pos, speed, dir, f, maxSteer, dt); return; }
            if (_reverseOverride > 0)
            {
                // reverse override: AIVehicle::OnSteering with the car backing up at 15 m/s target
                float st = Mathf.Asin(Mathf.Clamp(-RoadGraph.Cross2(dir, f), -1f, 1f)) / (maxSteer * Mathf.Deg2Rad);
                input.Steer = Mathf.Clamp(-st, -1f, 1f);
                input.Brake = 1f;   // brake at a stop selects reverse; the car's DoAutoReverse handles the gear
                if (speed < -4f) input.Brake = 0.3f;
                Car.Input = input;
                return;
            }
            // OnGasBrake (Racecar.cpp:197): incremental PID on speed - driveSpeed
            float e = speed - DriveSpeed;
            _speedErr.Record(e, dt);
            if (Car.Gear < 0) _tb = 1f;   // reverse gear outside the override: OnReverse shifts back to 1st (gas brakes the backing car)
            else if (DriveSpeed < 0.5f) _tb = -1f;
            else if (speed < -1f) _tb = -1f;
            else _tb += -0.4f * e - 0.01f * Mathf.Clamp(_speedErr.Integral, -5f, 5f) - 0.1f * Mathf.Clamp(_speedErr.Derivative, -10f, 10f);
            _tb = Mathf.Clamp(_tb, -1f, 1f);
            input.Throttle = Mathf.Max(_tb, 0);
            input.Brake = Mathf.Max(-_tb, 0);
            input.Nitrous = _nosOn;
            // OnSteering (Racecar.cpp:290): adaptive PID on the body heading error, scaled by the car's max steer
            if (!(DriveSpeed == 0 && Body.linearVelocity.magnitude < 1f))
            {
                float err = Mathf.Asin(Mathf.Clamp(-RoadGraph.Cross2(dir, f), -1f, 1f));   // + = target to the left
                _headingErr.Record(err, dt);
                float P = err, I = Mathf.Clamp(_headingErr.Integral, -0.5f, 0.5f), D = Mathf.Clamp(_headingErr.Derivative, -10f, 10f);
                float v = Mathf.Abs(speed);
                if (v < 10f) _steerPid.Force(TableValue(LowP, v, 160f), TableValue(LowI, v, 160f), TableValue(LowD, v, 160f));
                else
                {
                    float model = Graph(HeadingModelX, HeadingModelY, Mathf.Abs(err) * Mathf.Rad2Deg);
                    float actual = (err * D >= 0 ? Mathf.Abs(D) : -Mathf.Abs(D)) * Mathf.Rad2Deg;
                    _steerPid.Update(model, actual, P, I, D, dt);
                }
                float outv = _steerPid.P * P + _steerPid.I * I + _steerPid.D * D;
                input.Steer = Mathf.Clamp(-outv / (maxSteer * Mathf.Deg2Rad), -1f, 1f);   // Unity steer + = right
            }
            Car.Input = input;
        }

        /// <summary>
        /// The per-tick drive on EngineRacer (AIVehicle::OnDriving = OnReverse, OnSteering, OnGasBrake). In the reverse
        /// override (GetUnstuck): AIVehicle::OnSteering (AIVehicle.cpp:841) and AIVehiclePid::OnGasBrake at drive speed 15;
        /// otherwise AIVehicle::OnReverse (flag 4, AIVehicle.cpp:805) and the PIDs of §5.5 / 5.6.
        /// </summary>
        void DriveSimple(DriveInput input, Vector3 fwd, Vector3 pos, float speed, Vector3 dir, Vector3 f, float maxSteer, float dt)
        {
            var eng = Engine;
            bool overrideOn = _reverseOverride > 0;
            float driveSpeed = overrideOn ? 15f : DriveSpeed;
            if (!overrideOn)
            {
                // OnReverse: from a forward gear only below 15 m/s; facing the target → first, facing away (< −0.707) → reverse
                _reversingSpeed = false;
                if (eng.IsReversing || speed < 15f)
                {
                    _reversingSpeed = true;
                    float facing = Vector3.Dot(fwd, (DriveTarget - pos).normalized);
                    if (eng.IsReversing && facing > 0f) eng.Shift(TrafficPhysics.Chassis.GFirst);
                    else if (!eng.IsReversing && facing < -0.707f) eng.Shift(TrafficPhysics.Chassis.GReverse);
                }
            }
            bool reversing = eng.IsReversing;
            var v = Body.linearVelocity;
            float planar = new Vector2(v.x, v.z).magnitude;
            if (overrideOn)
            {
                // AIVehicle::OnSteering: asin(cross(fwd, dir).y) / max steering, negated in reverse; a target behind (< −0.2)
                // in a forward gear is full lock and mSteeringBehind
                _steeringBehind = false;
                if (!(driveSpeed == 0f && planar < 1f))
                {
                    float st = Mathf.Asin(Mathf.Clamp(-RoadGraph.Cross2(dir, f), -1f, 1f)) / (maxSteer * Mathf.Deg2Rad);
                    if (reversing) st = -st;
                    else if (Vector3.Dot(dir, f) < -0.2f) { st = st < 0f ? -1f : 1f; _steeringBehind = true; }
                    input.Steer = Mathf.Clamp(-st, -1f, 1f);   // Unity steer + = right
                }
            }
            else if (!(DriveSpeed == 0 && planar < 1f))
                input.Steer = PidSteer(dir, f, speed, maxSteer, reversing, dt);
            // AIVehiclePid::OnGasBrake (Racecar.cpp:197)
            if (!_reversingSpeed && _steeringBehind) { input.Throttle = 1f; input.Handbrake = true; }
            else
            {
                float e = speed - driveSpeed;
                _speedErr.Record(e, dt);
                if (driveSpeed < 0.5f) _tb = -1f;
                else if (reversing) _tb = speed > 1f ? -1f : 1f;
                else if (speed < -1f) _tb = -1f;
                else _tb += -0.4f * e - 0.01f * Mathf.Clamp(_speedErr.Integral, -5f, 5f) - 0.1f * Mathf.Clamp(_speedErr.Derivative, -10f, 10f);
                _tb = Mathf.Clamp(_tb, -1f, 1f);
                input.Throttle = Mathf.Max(_tb, 0);
                input.Brake = Mathf.Max(-_tb, 0);
            }
            input.Nitrous = _nosOn && !overrideOn;
            Car.Input = input;
        }

        /// <summary>AIVehiclePid::OnSteering's adaptive PID (Racecar.cpp:290); in reverse gear: speed &lt; 0 → full lock the
        /// other way, else 0.</summary>
        float PidSteer(Vector3 dir, Vector3 f, float speed, float maxSteer, bool reversing, float dt)
        {
            float err = Mathf.Asin(Mathf.Clamp(-RoadGraph.Cross2(dir, f), -1f, 1f));   // + = target to the left
            _headingErr.Record(err, dt);
            float P = err, I = Mathf.Clamp(_headingErr.Integral, -0.5f, 0.5f), D = Mathf.Clamp(_headingErr.Derivative, -10f, 10f);
            float v = Mathf.Abs(speed);
            if (v < 10f) _steerPid.Force(TableValue(LowP, v, 160f), TableValue(LowI, v, 160f), TableValue(LowD, v, 160f));
            else
            {
                float model = Graph(HeadingModelX, HeadingModelY, Mathf.Abs(err) * Mathf.Rad2Deg);
                float actual = (err * D >= 0 ? Mathf.Abs(D) : -Mathf.Abs(D)) * Mathf.Rad2Deg;
                _steerPid.Update(model, actual, P, I, D, dt);
            }
            float steer = (_steerPid.P * P + _steerPid.I * I + _steerPid.D * D) / (maxSteer * Mathf.Deg2Rad);   // + = left
            if (reversing) steer = speed < 0f ? (steer < 0f ? 1f : -1f) : 0f;
            return Mathf.Clamp(-steer, -1f, 1f);   // Unity steer + = right
        }

        void OnCollisionEnter(Collision c)
        {
            if (c.contactCount == 0) return;   // contact modification can leave a collision with no contacts
            // AIPerpVehicle::OnCausedCollision (AIVehicle.cpp:2416-2545) on a traffic car: a direct hit closing faster than
            // Tweak_MinAICausalityHitSpeed (4 m/s) is causal; intentional when this car closes along the normal faster than
            // the other does [?: post-contact velocities here, the exe reads the collision's pre-contact objA/objB velocities]
            var traffic = c.rigidbody ? c.rigidbody.GetComponent<TrafficCar>() : null;
            if (traffic && c.relativeVelocity.magnitude > 4f)
            {
                var n = c.GetContact(0).normal;   // points from the other body toward this one
                if (Vector3.Dot(Body.linearVelocity, -n) > Vector3.Dot(c.rigidbody.linearVelocity, n)) LastTrafficHitTime = Time.time;
            }
            if (Log && c.relativeVelocity.magnitude > 5f)
                Debug.Log($"NFSMW racer {name}: hit {c.collider.name} ({c.collider.GetType().Name}, parent {(c.collider.transform.parent ? c.collider.transform.parent.name : "-")}) at {c.GetContact(0).point} rel {c.relativeVelocity.magnitude:0.0}");
        }

        static float Graph(float[] xs, float[] ys, float x)
        {
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) return Mathf.Lerp(ys[i - 1], ys[i], (x - xs[i - 1]) / (xs[i] - xs[i - 1]));
            return ys[ys.Length - 1];
        }

        // ---- skill and catch-up (AIVehicle.cpp:1976-2305; races.md §5.3) ----

        static readonly float[] SpreadEasy = { 1000, 900, 750, 600, 800 }, SpreadHard = { 300, 275, 250, 150, 250 };
        static readonly float[] SkillEasy = { .75f, .75f, .75f, .5f, .25f }, SkillHard = { .75f, .75f, .75f, .75f, .25f };

        void Glue(float dt)
        {
            if (!CatchUpOn || Race == null || Me == null || Race.Countdown || Drag) { _glueSkill = _glueOutput = 0; return; }
            _glueTimer += dt;
            if (_glueTimer < 1f) return;
            _glueTimer = 0;
            var player = Race.PlayerEntrant;
            float total = Mathf.Max(1f, Race.RouteLength);
            float myPct = 100f * Me.Progress / total, pPct = player != null ? 100f * player.Progress / total : myPct;
            float pTop = GameBootstrap.Instance && GameBootstrap.Instance.Player ? GameBootstrap.Instance.Player.Spec.TopSpeed : _topSpeed;
            float perf = Mathf.Clamp01(pTop / 90f);   // the player's top-speed rating [?: ratings not ported]
            float e = total / 100f * (pPct - myPct) * (1f + 0.5f * (1f - perf));
            _glue.Record(e, 1f);
            // ComputeCatchUpSkill (FUN_005ff990) with the built-in curves
            float s = Race.Event.Difficulty < 34 ? 0f : Race.Event.Difficulty <= 66 ? 0.5f : 1f;
            float t = Mathf.Clamp(myPct, 0, 100);
            float spread = Mathf.Max(100f, Mathf.Lerp(Curve(SpreadEasy, t), Curve(SpreadHard, t), s));
            float skill = Mathf.Lerp(Curve(SkillEasy, t), Curve(SkillHard, t), s);
            float u = Mathf.Clamp(2f / spread * _glue.Error + 0.01f * _glue.Derivative + 5e-5f * _glue.Integral, -1f, 1f);
            _glueOutput = u;
            float gs = u * skill;
            bool trap = Race.SpeedTrapRace;
            if (gs > 0) gs *= trap ? 0.5f : TableValue(new[] { 0.33f, 0.66f, 1f }, BaseSkill, 1f);
            else gs *= trap ? 0.5f : TableValue(new[] { 1f, 1f, 0.66f }, BaseSkill, 1f);
            _glueSkill = gs;
        }

        static float Curve(float[] c, float pct)
        {
            float f = Mathf.Clamp(pct, 0, 100) / 25f;
            int i = Mathf.Min((int)f, c.Length - 2);
            return Mathf.Lerp(c[i], c[i + 1], f - i);
        }

        void OnDrawGizmosSelected()
        {
            if (Nav == null || !Nav.Valid) return;
            Gizmos.color = Color.yellow;
            for (int i = 0; i < Nav.CookieCount; i++) { var c = Nav.CookieAt(i); Gizmos.DrawLine(c.Left, c.Right); }
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(DriveTarget, 0.6f);
        }
    }

    /// <summary>PidError (Misc/Table.hpp:250): error, ring-averaged derivative and integral.</summary>
    public sealed class PidError
    {
        readonly float[] _int, _der, _dt;
        int _ni, _nd, _ci, _cd, _cdt;
        readonly float _freq;
        float _prev;
        bool _has;
        public float Error { get; private set; }

        public PidError(int nI, int nD, float freq)
        {
            _int = new float[nI]; _dt = new float[nI]; _der = new float[nD]; _freq = freq;
        }

        public void Record(float e, float dt)
        {
            float de = _has ? e - _prev : 0;
            float iv = dt * (_prev + de * 0.5f);
            _int[_ci] = iv; _ci = (_ci + 1) % _int.Length; _ni = Mathf.Min(_ni + 1, _int.Length);
            _dt[_cdt] = dt; _cdt = (_cdt + 1) % _dt.Length;
            _der[_cd] = dt > 0 ? de / dt : 0; _cd = (_cd + 1) % _der.Length; _nd = Mathf.Min(_nd + 1, _der.Length);
            _prev = e;
            _has = true;
            Error = e;
        }

        public float Derivative
        {
            get { if (_nd == 0) return 0; float s = 0; for (int i = 0; i < _nd; i++) s += _der[i]; return s / _nd; }
        }

        public float Integral
        {
            get
            {
                if (_ni == 0) return 0;
                float si = 0, sd = 0;
                for (int i = 0; i < _ni; i++) { si += _int[i]; sd += _dt[i]; }
                return sd > 0 ? si * _ni / (_freq * sd) : 0;
            }
        }
    }

    /// <summary>
    /// AdaptivePIDControllerComplicated (AdaptivePIDController.cpp): MIT-rule tuning of one coefficient per 0.1 s slice
    /// (P, I, D in turn) towards the heading-closing-rate model, clamped to P [0.4, 1], I [0.01, 0.1], D [0.1, 0.4].
    /// </summary>
    public sealed class AdaptivePid
    {
        public float P, I, D;
        /// <summary>Coefficient limits (racers; the drag-human set is P [0.6, 1.2], I [0.05, 0.5], D [0.02, 0.6]).</summary>
        public float MinP = 0.4f, MaxP = 1f, MinI = 0.01f, MaxI = 0.1f, MinD = 0.1f, MaxD = 0.4f;

        static readonly float[] ModelX = { 0, 3, 6, 9, 12, 15, 18, 21, 24, 27 };
        static readonly float[] ModelY = { 0, -1, -2.05f, -3.5f, -4.94f, -6.14f, -7.35f, -8.55f, -9.4f, -10f };

        /// <summary>HeadingErrorModelGraph: |heading error| (deg) → desired closing rate (deg/s).</summary>
        public static float HeadingModel(float deg)
        {
            if (deg <= ModelX[0]) return ModelY[0];
            for (int i = 1; i < ModelX.Length; i++)
                if (deg <= ModelX[i]) return Mathf.Lerp(ModelY[i - 1], ModelY[i], (deg - ModelX[i - 1]) / (ModelX[i] - ModelX[i - 1]));
            return ModelY[ModelY.Length - 1];
        }
        readonly float[] _modelErr = new float[5], _coefDer = new float[5];
        int _m, _c;
        float _prevModelErr, _prevCoef, _time;
        const float Gain = 1e-5f, Threshold = 0.01f;

        public void Force(float p, float i, float d) { P = p; I = i; D = d; }

        public void Update(float model, float actual, float pTerm, float iTerm, float dTerm, float dt)
        {
            _time += dt;
            float modelError = actual - model;
            int which = (int)Mathf.Floor(_time / 0.1f) % 3;
            float term = which == 0 ? pTerm : which == 1 ? iTerm : dTerm;
            float coef = which == 0 ? P : which == 1 ? I : D;
            _modelErr[_m] = dt > 0 ? (modelError - _prevModelErr) / dt : 0; _m = (_m + 1) % 5;
            _coefDer[_c] = dt > 0 ? (coef - _prevCoef) / dt : 0; _c = (_c + 1) % 5;
            _prevModelErr = modelError;
            _prevCoef = coef;
            if (Mathf.Abs(term) < Threshold) return;
            float am = 0, ac = 0;
            for (int k = 0; k < 5; k++) { am += _modelErr[k]; ac += _coefDer[k]; }
            am /= 5; ac /= 5;
            float sens = Mathf.Abs(ac) > 1e-6f ? Mathf.Clamp(am / ac, -1000f, 1000f) : Mathf.Abs(am) < 0.001f ? 0 : Mathf.Sign(am);
            coef += dt * (-Gain * modelError * sens);
            switch (which)
            {
                case 0: P = Mathf.Clamp(coef, MinP, MaxP); break;
                case 1: I = Mathf.Clamp(coef, MinI, MaxI); break;
                default: D = Mathf.Clamp(coef, MinD, MaxD); break;
            }
        }
    }
}
