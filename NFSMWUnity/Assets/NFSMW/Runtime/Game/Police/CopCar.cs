using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>AIGoal names a cop car can have (AI/Common/AIGoal.cpp:116-366).</summary>
    public enum CopGoal { None, Patrol, Pursuit, Ram, Pit, PullOver, HeadOnRam, StaticRoadBlock, StopShort, FleePursuit }

    /// <summary>
    /// A cop car's brain: AIVehicleCopCar / AIVehiclePursuit with the goal → action system (specs/ai_cops.md §1-2, §6.6,
    /// §7). It thinks at the pursuit rate (0.125 of the 60 Hz sim = 7.5 Hz, staggered by 0.125 per car,
    /// AIVehiclePursuit.cpp:13-33) and drives every physics tick toward the drive target at the drive speed.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class CopCar : MonoBehaviour
    {
        enum Act { None, Traffic, Race, PursuitOffRoad, Ram, HeadOnRam, StaticRoadBlock, StopShort, TooDamaged, GetUnstuck, Airborne }

        public PlayerCar Car;
        public CopType Type;
        public PursuitSystem Police;

        // ---- IPursuitAI ----
        public CopGoal Goal { get; private set; } = CopGoal.None;
        public bool InPursuit { get; private set; }
        public bool InFormation, InPosition, DamagedByPerp;
        public bool WithinEngagementRadius { get; private set; }
        public Vector3 PursuitOffset, InPositionOffset;
        public CopGoal InPositionGoal = CopGoal.None, SupportGoal = CopGoal.None;
        public bool IsSupport => SupportGoal != CopGoal.None;
        /// <summary>mTimeSinceTargetSeen: −0.25 on a sighting, +dt per think (99 = never).</summary>
        public float TimeSinceSeen { get; private set; } = 99f;
        public CopRoadblock Roadblock;
        public bool InPursuitList;
        /// <summary>IVehicleAI::GetPursuit: the pursuit this car is in (the player's PursuitSystem.PlayerPursuit or a
        /// RacerPursuit), null when patrolling, fleeing or in a roadblock.</summary>
        public CopPursuit Pursuit { get; private set; }
        PlayerCar _lastTarget;
        /// <summary>AITarget: the pursuit's perp; a cop that left a pursuit keeps its last target (the flee action aims
        /// away from it); otherwise the local player.</summary>
        public PlayerCar Target => Pursuit != null && Pursuit.Target ? Pursuit.Target : (_lastTarget ? _lastTarget : Police.Player);
        /// <summary>The pursuit state the actions read; outside a pursuit the player's (the global pursuit level).</summary>
        CopPursuit View => Pursuit ?? Police.PlayerPursuit;

        // ---- damage and spawn bookkeeping ----
        public bool Destroyed { get; private set; }
        public float HitPoints;
        public bool DestroyedByPlayer;
        public float SpawnTime, OffscreenTime;
        public bool SawFlag, SirenOn = true;
        /// <summary>The light bar and the siren source (PursuitSystem.Lights).</summary>
        public Light RedLight, BlueLight;
        public AudioSource SirenSource;
        public bool DrivableToTarget { get; private set; }

        // ---- drive state ----
        public Vector3 DriveTarget { get; private set; }
        public float DriveSpeed { get; private set; }
        public RoadNav Nav { get; private set; }
        readonly CopDriver _driver = new CopDriver();
        DriveInput? _override;
        bool _nos, _allowReverse;
        Act _act;
        float _thinkTimer, _visTimer;
        static float _stagger;
        Rigidbody _body;
        Potentials _pot;

        // action state
        PerformanceLimiter _limiter;
        readonly SpeedDelay _delay = new SpeedDelay();
        float _speedLimit, _lastSpeed, _findTimer, _nosTimer, _nosDelay;
        Vector3 _lastFind;
        bool _playerNosLast, _flee, _dontSeekAhead, _raceNos;
        float _stuckTimer, _unstuckTimer;
        Vector3 _stuckAt;
        bool _bornThisFrame = true;
        // AIActionAirborne (mIsAirborne, mAirborneTimer: the timer is never reset, AIActionAirborne.cpp:67-75)
        bool _isAirborne;
        float _airborneTimer;
        // AIVehicleCopCar::mPerpHiddenFromMe
        bool _perpHiddenFromMe;

        const float ThinkRate = 0.125f * 60f;   // per second
        public const float FrontLOS = 150f, RearLOS = 50f;   // kCopFront/RearLOSDistance (patrol)

        public Rigidbody Body => _body ? _body : _body = GetComponent<Rigidbody>();
        Vector3 Pos => transform.position;
        Vector3 Vel => Body.linearVelocity;
        public float Speed => Body ? Body.linearVelocity.magnitude : 0;

        void Awake()
        {
            _thinkTimer = -_stagger / ThinkRate;
            _stagger += 0.125f;
            if (_stagger >= 1f) _stagger = 0;
        }

        public void Init(PlayerCar car, CopType type, PursuitSystem police)
        {
            Car = car;
            Type = type;
            Police = police;
            car.AiControlled = true;
            car.UseDeviceInput = false;
            _pot = new Potentials(car.Spec);
            SpawnTime = Time.time;
            AttachChassis(type);
        }

        /// <summary>The cop's own behaviours (specs/traffic_physics.md §9): RBCop + SuspensionSimple + EngineRacer.</summary>
        public TrafficPhysics.TrafficVehicle Sim { get; private set; }

        /// <summary>
        /// PVehicle::LoadBehaviors for DRIVER_COP: the cop pvehicles name BEHAVIOR_MECHANIC_SUSPENSION = SuspensionSimple and
        /// ENGINE = EngineRacer (all 14), and PVehicle::LookupBehaviorSignature (0x67F0D0) keeps that for DRIVER_COP (its
        /// SuspensionSimple override only adds CAR-class RACER/NONE/REMOTE drivers). The PlayerCar stays for input, audio and
        /// lookups but stops simulating (TrafficVehicle bridges its read-outs) [ours: the bridge].
        /// </summary>
        void AttachChassis(CopType type)
        {
            if (!UseSuspensionSimple || type == null || type.IsHeli) return;
            try
            {
                var specs = TrafficPhysics.VehicleSpecs.Get(type.Name);
                if (specs == null || specs.Suspension != "SuspensionSimple" || specs.Engine != "EngineRacer") return;
                Sim = TrafficPhysics.TrafficVehicle.Attach(gameObject, specs, Car, Car.WheelVisuals);
                if (Sim == null) return;
                Sim.DriverClass = 2;   // DRIVER_COP (SimTypes.h:218)
                // DamageCopCar::CheckUpright's instant destroy: ICause::FindInstance(model causality) == the player's vehicle — taken as
                // the player's car touching this cop in the last second [?: the exe reads the model's causality]
                Sim.CausedByPlayer = () => Police != null && Time.time - Police.LastPlayerContact(this) < 1f;
                Sim.ResetPhysics();
                Sim.MatchSpeed(Vector3.Dot(Body.linearVelocity, transform.forward));
            }
            catch (System.Exception e) { Debug.LogWarning($"NFSMW police: {type.Name} chassis: {e.Message}"); Sim = null; }
        }

        /// <summary>Off: cops keep the racer model (PlayerCar) [ours: a switch for comparisons].</summary>
        public static bool UseSuspensionSimple = true;

        float HalfWidth()
        {
            var box = GetComponent<BoxCollider>();
            return box ? box.size.x * 0.5f : 1f;
        }

        // ---- goals ----

        public void SetGoal(CopGoal g)
        {
            if (Goal == g) return;
            Goal = g;
            _act = Act.None;
        }

        /// <summary>AIVehiclePursuit::StartPursuit (AIVehiclePursuit.cpp:92-110): the target is acquired (the pursuit's), in
        /// pursuit with AIGoalPursuit.</summary>
        public void StartPursuit(CopPursuit pursuit)
        {
            Pursuit = pursuit ?? Police.PlayerPursuit;
            if (Pursuit.Target) _lastTarget = Pursuit.Target;
            InPursuit = true;
            SetGoal(CopGoal.Pursuit);
        }

        /// <summary>StartPursuit on the local player's pursuit.</summary>
        public void StartPursuit() => StartPursuit(Police.PlayerPursuit);

        /// <summary>AIVehiclePursuit::EndPursuit: out of the pursuit (the target stays acquired for the flee).</summary>
        public void EndPursuit()
        {
            InPursuit = false;
            InFormation = InPosition = false;
            Pursuit = null;
        }

        public void DoInPositionGoal() { if (InPositionGoal != CopGoal.None) SetGoal(InPositionGoal); }
        public bool StartSupportGoal() { if (!IsSupport) return false; SetGoal(SupportGoal); return true; }
        public void StartFlee() => SetGoal(CopGoal.FleePursuit);
        public void SetWithinEngagementRadius() => WithinEngagementRadius = true;
        /// <summary>IPursuitAI::ZeroTimeSinceTargetSeen (SpawnPursuitCar, AICopManager.cpp:825).</summary>
        public void ZeroTimeSinceTargetSeen() => TimeSinceSeen = 0f;

        /// <summary>DamageVehicle destroyed (EVehicleDestroyed): TooDamaged takes over, lights off.</summary>
        public void MarkDestroyed(bool byPlayer)
        {
            if (Destroyed) return;
            Destroyed = true;
            DestroyedByPlayer = byPlayer;
            if (Sim) Sim.OwnerDestroyed = true;   // Chassis::ComputeState: a destroyed vehicle brakes fully
            SirenOn = false;
            _act = Act.None;
        }

        // ---- the think ----

        void FixedUpdate()
        {
            if (!Car || Car.Spec == null || Police == null) return;
            float dt = Time.fixedDeltaTime;
            // DamageCopCar (TrafficVehicle.Damage: the cop's damagespecs) past 1 → EVehicleDestroyed; the player gets the
            // takedown when he touched it in the last 3 s [?: the exe reads the collision's cause]
            if (!Destroyed && Sim && Sim.Damage != null && Sim.Damage.Destroyed) Police.CopDestroyed(this, Time.time - Police.LastPlayerContact(this) < 3f);
            _thinkTimer += dt;
            if (_thinkTimer >= 1f / ThinkRate)
            {
                float tdt = _thinkTimer;
                _thinkTimer = 0;
                Think(tdt);
            }
            if (_override.HasValue) { Car.Input = _override.Value; return; }
            Car.Input = _driver.Drive(Car, Body, DriveTarget, DriveSpeed, _nos, _unstuckTimer > 0, _allowReverse, dt);
        }

        void Think(float dt)
        {
            var target = Target;
            // AIVehiclePursuit::Update: the LOS test every 0.25 s while in pursuit
            if (!InPursuit || !target)
            {
                TimeSinceSeen = 0.25f;
                _visTimer = 0.25f;
            }
            else
            {
                _visTimer += dt;
                TimeSinceSeen += dt;
                if (_visTimer >= 0.25f)
                {
                    _visTimer -= 0.25f;
                    var row = View.Row;
                    if (CanSeeTarget(target, row.FrontLOS, row.RearLOS, true)) TimeSinceSeen = -0.25f;
                }
            }
            if (target) DrivableToTarget = !WorldCollision(Pos, target.transform.position);
            if (!InPursuit && Goal == CopGoal.Patrol && !Destroyed) WatchForPerps();
            if (Nav == null) NewNav(RoadGraph.Lane.Cop, false);   // the drive-to nav exists from the spawn
            ChooseAction(dt);
            _override = null;
            _nos = false;
            _allowReverse = true;
            switch (_act)
            {
                case Act.Traffic: Traffic(dt); break;
                case Act.Race: Race(dt); break;
                case Act.PursuitOffRoad: PursuitOffRoad(dt); break;
                case Act.Ram: Ram(dt); break;
                case Act.HeadOnRam: HeadOnRam(dt); break;
                case Act.StaticRoadBlock: _override = new DriveInput { Brake = 1f }; break;
                case Act.StopShort: _override = new DriveInput { Brake = 1f, Handbrake = true }; break;
                case Act.TooDamaged: _override = new DriveInput { Brake = Speed > 2.5f ? 0.25f : 0f }; break;
                case Act.GetUnstuck: DriveTarget = Pos - transform.forward * 10f; DriveSpeed = 15f; break;
                case Act.Airborne:
                    // AIActionAirborne::Update: every control off while in the air
                    _override = new DriveInput();
                    _isAirborne = Car.WheelsOnGround == 0;
                    break;
                default: DriveTarget = Pos + transform.forward * 5f; DriveSpeed = 0; break;
            }
            if (System.Array.IndexOf(Actions(), Act.GetUnstuck) < 0) _unstuckTimer = _stuckTimer = 0;
            else if (_act != Act.GetUnstuck) WatchStuck(dt);
            else if ((_unstuckTimer -= dt) <= 0) { _unstuckTimer = 0; _act = Act.None; }
            _bornThisFrame = false;
        }

        /// <summary>
        /// AIGoal::ChooseAction (AIGoal.cpp:33): an attemptable action other than the current one replaces it when the
        /// current one is finished or its score is not higher; the last such action in the goal's list wins.
        /// </summary>
        void ChooseAction(float dt)
        {
            bool done = _act == Act.None || Finished(_act);
            float score = _act == Act.None ? 0f : Score(_act);
            Act pick = Act.None;
            foreach (var a in Actions())
            {
                if (a == _act || !CanAttempt(a, dt)) continue;
                if (done || Score(a) >= score) { pick = a; done = false; score = Score(a); }
            }
            if (pick == Act.None) return;
            _act = pick;
            Begin(pick);
        }

        /// <summary>The goal's action list in AddAction order (AIGoal.cpp:116-366; PC ctors 0x43B740-0x43BF70 checked:
        /// every pursuit-side goal ends with AIActionAirborne, and AIGoalPullOver::Construct 0x43BBB0 builds the
        /// AIGoalRam list).</summary>
        Act[] Actions() => Goal switch
        {
            CopGoal.Patrol => ActPatrol,
            CopGoal.Pursuit => ActPursuit,
            CopGoal.Ram or CopGoal.Pit or CopGoal.PullOver => ActRam,
            CopGoal.HeadOnRam => ActHeadOnRam,
            CopGoal.StaticRoadBlock => ActRoadBlock,
            CopGoal.StopShort => ActStopShort,
            CopGoal.FleePursuit => ActFlee,
            _ => ActNone,
        };

        static readonly Act[] ActPatrol = { Act.Traffic, Act.TooDamaged };
        static readonly Act[] ActPursuit = { Act.PursuitOffRoad, Act.Race, Act.Traffic, Act.TooDamaged, Act.GetUnstuck, Act.Airborne };
        static readonly Act[] ActRam = { Act.Ram, Act.PursuitOffRoad, Act.Race, Act.TooDamaged, Act.GetUnstuck, Act.Airborne };
        static readonly Act[] ActHeadOnRam = { Act.HeadOnRam, Act.Race, Act.TooDamaged, Act.GetUnstuck, Act.Airborne };
        static readonly Act[] ActRoadBlock = { Act.StaticRoadBlock };
        static readonly Act[] ActStopShort = { Act.StopShort, Act.TooDamaged, Act.GetUnstuck, Act.Airborne };
        static readonly Act[] ActFlee = { Act.Race, Act.TooDamaged, Act.GetUnstuck, Act.Airborne };
        static readonly Act[] ActNone = new Act[0];

        static float Score(Act a) => a switch
        {
            Act.GetUnstuck or Act.TooDamaged or Act.StaticRoadBlock => 1f,
            Act.Ram or Act.HeadOnRam or Act.Traffic => 0.1f,
            Act.PursuitOffRoad => 0.01f,
            _ => 0f,
        };

        bool CanAttempt(Act a, float dt)
        {
            var target = Target;
            switch (a)
            {
                case Act.TooDamaged: return Destroyed;
                case Act.GetUnstuck: return _unstuckTimer > 0;
                case Act.StaticRoadBlock:
                case Act.StopShort: return true;
                case Act.Traffic: return Pursuit != null ? !Pursuit.PerpInSight : true;
                case Act.Race: return target;
                case Act.PursuitOffRoad: return target && PursuitOffRoadOk(target);
                case Act.Ram:
                case Act.HeadOnRam: return target && DrivableToTarget;
                case Act.Airborne: return AirborneCanBeAttempted(dt);
                default: return false;
            }
        }

        /// <summary>AIActionAirborne::CanBeAttempted (PC 0x407080): once the previous check saw no wheel down, the
        /// timer adds dT and the action starts above 1.5 s; the timer is cumulative over the car's life (never reset),
        /// so after one long jump any later airborne think qualifies at once. Body "modeling" is taken as true.</summary>
        bool AirborneCanBeAttempted(float dt)
        {
            if (_isAirborne)
            {
                _airborneTimer += dt;
                if (_airborneTimer > 1.5f) return true;
            }
            _isAirborne = Car.WheelsOnGround == 0;
            return false;
        }

        bool Finished(Act a)
        {
            switch (a)
            {
                case Act.Traffic: return Pursuit != null && Pursuit.PerpInSight;
                case Act.PursuitOffRoad: return !Target || !PursuitOffRoadOk(Target);
                case Act.Ram: return !Target || !DrivableToTarget;
                case Act.HeadOnRam: return !Target;
                case Act.GetUnstuck: return _unstuckTimer <= 0;
                case Act.TooDamaged: return !Destroyed;
                case Act.Airborne: return !_isAirborne;
                default: return false;
            }
        }

        /// <summary>AIActionPursuitOffRoad::CanBeAttempted (:190-215).</summary>
        bool PursuitOffRoadOk(PlayerCar target)
        {
            var roads = View.Roads;
            float slack = roads.Current.Valid ? Vector3.Distance(target.transform.position, roads.Current.Position) : 0;
            if (Vector3.Distance(Pos, target.transform.position) > 60f + slack) return false;
            // UMath::Distance(target velocity, my velocity): the relative velocity, not the speed difference
            var tb = target.GetComponent<Rigidbody>();
            if (tb && (tb.linearVelocity - Body.linearVelocity).magnitude > PoliceData.Kph(140f)) return false;
            return DrivableToTarget && Nav != null && Nav.Valid;
        }

        void Begin(Act a)
        {
            _override = null;
            switch (a)
            {
                case Act.Traffic:
                    NewNav(RoadGraph.Lane.Traffic, true);
                    if (Nav.Valid) { KeepRight(); Nav.SetPath(Nav.Wander(12)); }
                    break;
                case Act.Race:
                    _flee = Goal == CopGoal.FleePursuit;
                    _dontSeekAhead = SupportGoal == CopGoal.HeadOnRam;
                    NewNav(RoadGraph.Lane.Racing, false);
                    _speedLimit = Mathf.Max(0, Speed);
                    _lastSpeed = Speed;
                    _findTimer = 0;
                    _nosTimer = 0;
                    _raceNos = false;
                    break;
                case Act.PursuitOffRoad:
                case Act.Ram:
                case Act.HeadOnRam:
                    // a cop that gets the action in its first sim frame starts at 60 mph
                    if (_bornThisFrame) Body.linearVelocity = transform.forward * PoliceData.Mph(60f);
                    _limiter.Init(Speed);
                    if (a == Act.PursuitOffRoad)
                    {
                        NewNav(RoadGraph.Lane.Cop, false);   // ResetDriveToNav(SELECT_CURRENT_LANE)
                        _delay.Reset(Target ? Target.SpeedMps : 0);
                    }
                    else if (Nav == null || !Nav.Valid) NewNav(RoadGraph.Lane.Cop, false);
                    break;
                case Act.GetUnstuck:
                    break;
            }
        }

        void NewNav(RoadGraph.Lane lane, bool traffic)
        {
            Nav = new RoadNav { CookieTrail = true, DecisionFilter = false, TrafficFilter = traffic, HalfWidth = HalfWidth() };
            Nav.SetLaneType(lane);
            if (!Nav.InitAtPoint(Pos, transform.forward, false, 1f) && traffic)
            {
                Nav.TrafficFilter = false;
                Nav.InitAtPoint(Pos, transform.forward, false, 1f);
            }
        }

        /// <summary>Traffic lanes in the travel direction are right of the centre line (right-hand traffic).</summary>
        void KeepRight()
        {
            var zones = RoadGraph.ZonesAt(Nav.Segment, Nav.Fwd);
            if (zones == null) return;
            float sign = Nav.Fwd ? 1f : -1f, best = float.MaxValue, pick = Nav.LaneOffset;
            foreach (var z in zones)
            {
                if (z.Type != (int)RoadGraph.Lane.Traffic) continue;
                float o = z.Offset * sign;
                if (o <= 0) continue;
                float d = Mathf.Abs(o - Nav.LaneOffset);
                if (d < best) { best = d; pick = o; }
            }
            Nav.ChangeLanes(pick);
        }

        /// <summary>Moves the nav so it sits <paramref name="L"/> ahead of the car (the racer's look-ahead solve).</summary>
        void AdvanceNav(float L)
        {
            var N = Nav.Position - Pos; N.y = 0;
            var F = Nav.Forward; F.y = 0;
            float a = Vector3.Dot(F, F), b = 2f * Vector3.Dot(N, F), c = Vector3.Dot(N, N) - L * L;
            float disc = b * b - 4f * a * c;
            if (disc > 0 && a > 1e-6f)
            {
                float d = (-b + Mathf.Sqrt(disc)) / (2f * a);
                if (d > 0) Nav.IncNavPosition(Mathf.Min(d, 200f), L);
            }
        }

        // ---- AIActionTraffic (patrol and search mode) ----

        void Traffic(float dt)
        {
            if (Nav == null || !Nav.Valid) { NewNav(RoadGraph.Lane.Traffic, true); if (!Nav.Valid) { DriveSpeed = 0; return; } }
            if (Nav.PathDone) Nav.SetPath(Nav.Wander(12));
            float v = Speed;
            float look = Mathf.Lerp(10f, 30f, Mathf.Clamp01(v / 25f)) + v * dt + HalfWidth() * 2f;
            AdvanceNav(look);
            if (Nav.DeadEnd) { Nav.Reverse(); Nav.SetPath(Nav.Wander(12)); }
            var aim = Nav.UpdateOccludedPosition(Pos, HalfWidth());
            bool highway = RoadGraph.TrafficLanes(Nav.Segment) >= 4;
            // AIActionTraffic.cpp:89-90 (patrol 35/55 mph); search mode uses the default row's SearchModeCity/HwyMPH
            float posted = InPursuitList ? PoliceData.Mph(highway ? PoliceData.SearchModeHwyMph : PoliceData.SearchModeCityMph)
                                         : PoliceData.Mph(highway ? 55f : 35f);
            float k = Nav.CookieTrailCurvature(Pos, Vel);
            DriveTarget = aim;
            DriveSpeed = RoadGraph.SpeedLimitForCurvature(1.6f, k, posted);   // cops use friction 1.6 (traffic 0.6)
        }

        // ---- AIActionRace, pursuit and flee branches (AIActionRace.cpp:503-576, 853-1069) ----

        void Race(float dt)
        {
            var target = Target;
            if (Nav == null || !Nav.Valid) { NewNav(RoadGraph.Lane.Racing, false); if (!Nav.Valid) { DriveSpeed = 0; return; } }
            var tp = target.transform.position;
            // FindPath whenever the path finder is idle; ours is synchronous, so it re-plans every 0.5 s [I]
            _findTimer -= dt;
            if (_findTimer <= 0)
            {
                _findTimer = 0.5f;
                Vector3 find;
                if (_flee)
                {
                    var fv = target.GetComponent<Rigidbody>().linearVelocity;
                    var fwd = fv.magnitude > 1e-5f ? fv.normalized : target.transform.forward;
                    var off = tp - Pos;
                    if (off.magnitude > 50f) fwd = off.normalized;
                    find = tp - fwd * 500f;
                    if (Vector3.Distance(_lastFind, find) < 60f && Vector3.Distance(tp, _lastFind) > 450f) find = _lastFind;
                }
                else find = _dontSeekAhead ? tp : View.SeekAhead;
                _lastFind = find;
                Nav.FindPath(find);
            }
            float L = Mathf.Lerp(30f, 100f, Mathf.Clamp01(_speedLimit / 100f));   // AiNavLookAheadTable
            AdvanceNav(L);
            var aim = Nav.UpdateOccludedPosition(Pos, HalfWidth());
            float k = Nav.CookieTrailCurvature(Pos, Vel);
            float potential = RacePotentialSpeed(k, target);
            float speed = Speed;
            float jerkAcc = View.IsJerk ? 1.5f : 1f;
            float potAcc = Mathf.Max(0, _pot.Acceleration(Mathf.Max(speed, _speedLimit)) * Type.AccelerationMultiplier * jerkAcc - 9.81f * transform.forward.y);
            float actAcc = (speed - _lastSpeed) / Mathf.Max(1e-4f, dt);
            _lastSpeed = speed;
            if (actAcc < 0 && _speedLimit < potential && _speedLimit > speed) _speedLimit += Mathf.Min(potAcc + actAcc, 0) * dt;
            if (_speedLimit < potential)
            {
                float t = Mathf.Clamp01(_speedLimit / Mathf.Max(1e-3f, potential));
                _speedLimit += Mathf.Clamp(potAcc - actAcc * Mathf.Pow(t, 2f), 0, potAcc) * dt;   // skill 1: exponent 2
            }
            _speedLimit = Mathf.Clamp(_speedLimit, 0, potential);
            var driveTarget = aim;
            float driveSpeed = _speedLimit;
            // turn around: above 40 km/h with the target behind, brake toward the road ahead
            if (speed > PoliceData.Kph(40f))
            {
                var steer = driveTarget - Pos;
                if (Vector3.Dot(Vel, steer) < speed * -0.3f * steer.magnitude)
                {
                    driveTarget = Nav.Centre;
                    driveSpeed = 0;
                }
            }
            DriveTarget = driveTarget;
            DriveSpeed = driveSpeed;
            // NOS (:1032-1069) with the racer gaps at skill 1
            _nosTimer += _raceNos ? dt : -dt;
            float speedGap = potential - speed, accelGap = potAcc - actAcc;
            if (!_raceNos) { if (_nosTimer < -4f && !Nav.Occluded && speedGap > 15f && accelGap > 0.5f * potAcc) { _raceNos = true; _nosTimer = 0; } }
            else if (_nosTimer > 4f && (speedGap < 5f || accelGap < 0.15f * potAcc)) { _raceNos = false; _nosTimer = 0; }
            _nos = _raceNos;
        }

        float RacePotentialSpeed(float k, PlayerCar target)
        {
            if (Nav.DeadEnd) return 0;
            float distant = PoliceData.Kph(Type.MaxAiSpeedKmh) * (View.IsJerk ? 1.1f : 1f);
            var tp = target.transform.position;
            var seekDir = RoadGraph.FlatDir(_lastFind - tp);
            var steerDir = RoadGraph.FlatDir(Nav.Position - Pos);
            var roads = View.Roads;
            var targetSteer = roads.Future.Valid ? RoadGraph.FlatDir(roads.Future.Position - tp) : steerDir;
            var off = Pos - tp;
            float s = Vector3.Dot(off, seekDir), vT = target.SpeedMps;
            float fwdNear = Mathf.Clamp(vT - (s > 0 ? PoliceData.Kph(100f) : PoliceData.Kph(200f)) * 0.01f * s, PoliceData.Kph(10f), distant);
            float revNear = Mathf.Clamp(s > 0 ? -vT + s * PoliceData.Kph(50f) * 0.01f : distant, PoliceData.Kph(40f), distant);
            float dirScale = Mathf.Clamp01(Vector3.Dot(transform.forward, seekDir) + 0.5f);
            float near = dirScale * (fwdNear - revNear) + revNear;
            float lat = (off - seekDir * s).magnitude * 2.5f;
            float sAdj = s > 0 ? s * 0.5f : s;
            float app = Mathf.Sqrt(sAdj * sAdj + lat * lat);
            float nearW = Mathf.Clamp01(1f - (app - 150f) / 150f) * Mathf.Clamp01(Mathf.Abs(Vector3.Dot(steerDir, targetSteer)) + 0.2f);
            float maxCop = Mathf.Clamp(nearW * near + (1f - nearW) * distant, 0, distant);
            float curve = _pot.CornerSpeed(k);
            float result = Mathf.Min(maxCop, curve) * Type.TopSpeedMultiplier * (View.IsJerk ? 1.2f : 1f);
            return Mathf.Min(maxCop, result);
        }

        // ---- AIActionPursuitOffRoad (formation slot driving) ----

        void PursuitOffRoad(float dt)
        {
            var target = Target;
            var tBody = target.GetComponent<Rigidbody>();
            var tp = target.transform.position;
            var tv = tBody ? tBody.linearVelocity : Vector3.zero;
            _delay.Add(target.SpeedMps, dt);
            bool playerNos = target.NosFiring;
            if (playerNos && !_playerNosLast) _nosDelay = 1.5f;   // kNOSDelay
            _playerNosLast = playerNos;

            var avoid = AvoidWalls();
            var seek = PursuitSeek(target, tp, tv);
            float seekSpeed = seek.magnitude;
            // the limiter: the target's top speed and acceleration × this type's multipliers
            float speedMult = Type.TopSpeedMultiplier, accelMult = Type.AccelerationMultiplier, maxSpeed = PoliceData.Kph(Type.MaxAiSpeedKmh);
            if (View.IsJerk) { speedMult *= 1.2f; accelMult *= 1.5f; maxSpeed *= 1.1f; }
            var pp = View.TargetPotentials;
            maxSpeed = Mathf.Min(maxSpeed, pp.TopSpeed * speedMult);
            float maxAccel = pp.Acceleration(Speed) * accelMult - 9.81f * transform.forward.y;
            if (playerNos) maxAccel *= 0.2f;
            _limiter.Update(Speed, maxSpeed, maxAccel, dt);
            maxSpeed = _limiter.Limit;
            if (seekSpeed > maxSpeed && seekSpeed > 1e-4f) seek *= maxSpeed / seekSpeed;

            var separation = Steer.Separation(Car, Body, Police.Avoidables, 2.7f, 5.3f);
            var steer = avoid + separation + RoadAffinity();
            steer.y = 0;
            seek.y = 0;
            float sl = seek.magnitude;
            if (sl > 1e-4f)
            {
                float sds = Vector3.Dot(steer, seek), counter = -sds / sl;
                if (counter > 0.0001f && steer.sqrMagnitude > 1e-6f)
                {
                    float w = Mathf.Clamp01((PoliceData.Kph(35f) - counter) / PoliceData.Kph(25f));
                    var along = steer * (sds / steer.sqrMagnitude);
                    seek = along * w + (seek - along);
                }
            }
            steer += seek;
            CurvatureCap(ref steer);
            DriveSpeed = steer.magnitude;
            DriveTarget = Pos + steer;
            DoNos();
        }

        /// <summary>UpdateSeek (:328-508): the formation slot on the target's road, delayed speed, tether, lane snap.</summary>
        Vector3 PursuitSeek(PlayerCar target, Vector3 tp, Vector3 tv)
        {
            var roads = View.Roads;
            Vector3 centre, future, dir;
            if (roads.Valid)
            {
                centre = roads.Current.Position;
                future = roads.Future.Position;
                dir = future - centre;
            }
            else
            {
                centre = tp;
                dir = tv.magnitude < 1f ? target.transform.forward : tv.normalized;
                future = tp + tv;
            }
            dir.y = 0;
            dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : RoadGraph.FlatDir(target.transform.forward);
            var side = RoadGraph.RightOf(dir);
            var off = PursuitOffset;
            var seekPos = centre + dir * off.z + side * off.x;
            var seekFut = future + dir * off.z + side * off.x;
            float vd = _delay.Sample(View.Row.SpeedReactionTime);
            var roadoff = seekFut - seekPos;
            float roadlen = roadoff.magnitude;
            if (roadlen > 1e-5f) { roadoff *= vd / roadlen; roadlen = vd; }
            var simpleoff = tv.magnitude > 1e-5f ? tv * (vd / tv.magnitude) : tv;
            var seekoff = seekPos - Pos;
            float seeklen = seekoff.magnitude;
            if (seeklen > 1e-5f)
            {
                float seekdir = Vector3.Dot(seekoff, dir) / seeklen;
                float rscale = Mathf.Min(1f, Mathf.Max(PoliceData.Kph(20f), vd * 0.2f) / seeklen);
                float slide = Mathf.Clamp(seekdir * 10f, -1f, 1f) * 0.5f + 0.5f;
                float scale = slide * 1.8f + (1f - slide) * rscale;
                if (roadlen >= 1e-5f)
                {
                    var para = roadoff * (Vector3.Dot(seekoff, roadoff) / (roadlen * roadlen));
                    seekoff = para * scale + (seekoff - para);
                }
            }
            float tether = Type.TetherWeight * 0.01f;
            var blended = Vector3.Lerp(roadoff, simpleoff, tether) + seekoff + Pos;

            // lane snap on the drive nav
            bool reset = true;
            if (Nav != null && Nav.Valid)
            {
                Nav.SetLaneType(View.Heat >= 3 ? RoadGraph.Lane.CopReckless : RoadGraph.Lane.Cop);
                var nd = RoadGraph.FlatDir(Nav.Forward);
                var endoff = blended - Nav.Position;
                float inc = Vector3.Dot(endoff, nd);
                if (inc >= -5f)
                {
                    int guard = 0;
                    while (inc > 5f && guard++ < 60 && !Nav.DeadEnd)
                    {
                        Nav.IncNavPosition(5f, 0f);
                        nd = RoadGraph.FlatDir(Nav.Forward);
                        endoff = blended - Nav.Position;
                        inc = Vector3.Dot(endoff, nd);
                    }
                    inc = Mathf.Max(0.1f, inc);
                    float lane = Vector3.Dot(endoff, RoadGraph.RightOf(nd)) + Nav.LaneOffset;
                    float snap = Nav.SnapOffset(lane);
                    if (Mathf.Abs(snap - lane) < 4f)
                    {
                        Nav.ChangeLanes(snap);
                        reset = false;
                        Nav.IncNavPosition(inc, 0f);
                        var occ = Nav.UpdateOccludedPosition(Pos, HalfWidth()) - Pos;
                        float ol = occ.magnitude;
                        if (ol > 1e-5f) occ *= Vector3.Distance(blended, Pos) / ol;
                        blended = occ + Pos;
                    }
                }
            }
            if (reset)
            {
                NewNav(RoadGraph.Lane.Cop, false);
                if (Nav.Valid) Nav.SnapToSelectableLane();
            }
            return blended - Pos;
        }

        /// <summary>UpdateAvoidWalls (:294-326): a ray along v; facing a wall, push off its normal by dot²·10 km/h / t².</summary>
        Vector3 AvoidWalls()
        {
            var v = Vel;
            float speed = v.magnitude;
            if (speed < 2f) return Vector3.zero;
            float len = Mathf.Min(80f, speed + 10f);
            if (!WorldRay(Pos + Vector3.up * 0.5f, v / speed, len, out var hit)) return Vector3.zero;
            float t = hit.distance / speed, dot = Vector3.Dot(v, hit.normal) / speed;
            if (dot >= 0) return Vector3.zero;
            return hit.normal * (dot * dot * PoliceData.Kph(10f) / Mathf.Max(1e-3f, t * t));
        }

        /// <summary>
        /// UpdateRoadAffinity (:255-287): from the current cookie's left and right corridor edges, a push of
        /// 0.5/(d+1)² + 2·(v_side/(d+1))² toward the road, each capped at 100 km/h.
        /// </summary>
        Vector3 RoadAffinity()
        {
            if (Nav == null || !Nav.Valid || Nav.CookieCount == 0) return Vector3.zero;
            var cookie = Nav.CurrentCookie;
            var side = RoadGraph.RightOf(RoadGraph.FlatDir(cookie.Forward));
            var p = Pos;
            float llen = Vector3.Dot(p - cookie.Left, side) + 1f, rlen = -Vector3.Dot(p - cookie.Right, side) + 1f;
            float sidev = Vector3.Dot(Vel, side);
            float lden = Mathf.Max(0.1f, llen), rden = Mathf.Max(0.1f, rlen);
            float linv = Mathf.Max(0, -sidev) / lden, rinv = Mathf.Max(0, sidev) / rden;
            float cap = PoliceData.Kph(100f);
            float l = Mathf.Min(cap, 0.5f / (lden * lden) + 2f * linv * linv);
            float r = Mathf.Min(cap, 0.5f / (rden * rden) + 2f * rinv * rinv);
            return side * (l - r);
        }

        /// <summary>The curvature cap (:595-610): |steer| ≤ max(40 km/h, 450 km/h − curv·950 km/h).</summary>
        void CurvatureCap(ref Vector3 steer)
        {
            var v = Vel;
            float lv = v.magnitude, ls = steer.magnitude;
            if (lv <= 0.1f || ls <= 0.1f) return;
            float dot = Vector3.Dot(v, steer) / (lv * ls);
            float curv = (1f - dot) * lv / ls;
            float limit = Mathf.Max(PoliceData.Kph(40f), PoliceData.Kph(450f) - curv * PoliceData.Kph(950f));
            if (limit < ls) steer *= limit / ls;
        }

        /// <summary>AIVehicle::DoNOS (AIVehicle.cpp:593) with the 1.5 s wait after the player's NOS comes on.</summary>
        void DoNos()
        {
            if (_nosDelay > 0) { _nosDelay -= 1f / ThinkRate; _nos = false; return; }
            bool was = Car.NosFiring;
            float my = Speed, want = DriveSpeed;
            _nos = false;
            if (want > PoliceData.Kph(was ? 90f : 120f) && my > PoliceData.Kph(was ? 50f : 80f) && want - my > PoliceData.Kph(was ? 10f : 60f))
            {
                var off = DriveTarget - Pos;
                _nos = Vector3.Dot(off, Vel) > off.magnitude * (was ? 0.8f : 0.95f) * my;
            }
        }

        // ---- AIActionRam (Ram / Pit / PullOver) ----

        void Ram(float dt)
        {
            var target = Target;
            var tBody = target.GetComponent<Rigidbody>();
            var tp = target.transform.position;
            var tv = tBody ? tBody.linearVelocity : Vector3.zero;
            bool pullOver = Goal == CopGoal.PullOver;
            var seekPos = RamSeekPosition(target, tp, tv, pullOver);
            // UpdateSeek: +70 % of the seek offset's part along the target velocity, then AISteer::Pursuit 1 s ahead
            float tvl = tv.magnitude;
            var newseek = seekPos;
            if (tvl > 0.01f)
            {
                var so = seekPos - Pos;
                float blah = Vector3.Dot(so, tv) / tvl * 0.7f;
                so += tv * (blah / tvl);
                newseek = Pos + so;
            }
            var seek = newseek + tv - Pos;
            seek.y = 0;
            var steer = seek;
            float aggression = 0;
            if (pullOver)
            {
                aggression = View.IsJerk ? 1f : View.Row.CollapseAggression;
                float st = (1f - aggression) * 4f;
                var sep = Steer.Separation(Car, Body, Police.Avoidables, st, st + 1.5f);
                float sl = steer.magnitude;
                if (sl > 1e-4f && sep.sqrMagnitude > 1e-6f)
                {
                    float sds = Vector3.Dot(steer, sep), counter = -sds / sl;
                    if (counter > 0.0001f)
                    {
                        float w = Mathf.Clamp01((PoliceData.Kph(55f) - counter) / PoliceData.Kph(55f));
                        var along = sep * (sds / sep.sqrMagnitude);
                        steer = along * w + (steer - along);
                    }
                }
                steer += sep;
            }
            float desired = steer.magnitude;
            float speedMult = Type.TopSpeedMultiplier, accelMult = Type.AccelerationMultiplier, maxSpeed = PoliceData.Kph(Type.MaxAiSpeedKmh);
            if (View.IsJerk) { speedMult *= 1.2f; accelMult *= 1.5f; maxSpeed *= 1.1f; }
            var pp = View.TargetPotentials;
            maxSpeed = Mathf.Min(maxSpeed, pp.TopSpeed * speedMult);
            _limiter.Update(Speed, maxSpeed, pp.Acceleration(Speed) * accelMult, dt);
            desired = Mathf.Min(desired, _limiter.Limit);
            // parked in its ring slot: full brake and handbrake
            if (pullOver && seek.magnitude < PoliceData.Kph(0.5f) + (1f - aggression) * PoliceData.Kph(0.2f))
            {
                _override = new DriveInput { Brake = 1f, Handbrake = true };
                return;
            }
            DriveSpeed = desired;
            DriveTarget = Pos + steer;
        }

        /// <summary>GetSeekPosition (AIActionRam.cpp:120-173): the in-position slot in the target frame; pull-over goes round the target.</summary>
        Vector3 RamSeekPosition(PlayerCar target, Vector3 tp, Vector3 tv, bool avoid)
        {
            var tf = target.SpeedMps < PoliceData.Kph(5f) ? RoadGraph.FlatDir(target.transform.forward) : RoadGraph.FlatDir(tv);
            var ts = RoadGraph.RightOf(tf);
            var off = InPositionOffset;
            var meToTarget = tp - Pos; meToTarget.y = 0;
            var targetToSeek = tf * off.z + ts * off.x;
            var meToSeek = meToTarget + targetToSeek;
            float tsr = targetToSeek.magnitude, mtr = meToTarget.magnitude, msr = meToSeek.magnitude;
            float avoidRadius = Mathf.Min(tsr, mtr) - 0.2f;
            if (!avoid || avoidRadius <= 0.1f || !SegmentHitsCircle(new Vector2(-meToTarget.x, -meToTarget.z), new Vector2(targetToSeek.x, targetToSeek.z), avoidRadius))
                return tp + targetToSeek;
            var tangent = new Vector3(-meToTarget.z, 0, meToTarget.x);
            tangent *= tsr * 2.1f / Mathf.Max(1e-4f, tangent.magnitude);
            var d1 = meToTarget + tangent;
            var d2 = meToTarget - tangent;
            d1 *= msr / Mathf.Max(1e-4f, d1.magnitude);
            d2 *= msr / Mathf.Max(1e-4f, d2.magnitude);
            if (Vector3.Distance(d1, meToSeek) > Vector3.Distance(d2, meToSeek)) d1 = d2;
            return d1 + Pos;
        }

        /// <summary>WWorldMath::IntersectCircle: does the segment a→b pass within r of the origin.</summary>
        static bool SegmentHitsCircle(Vector2 a, Vector2 b, float r)
        {
            var d = b - a;
            float len2 = d.sqrMagnitude;
            float t = len2 > 1e-6f ? Mathf.Clamp01(-Vector2.Dot(a, d) / len2) : 0;
            return (a + d * t).sqrMagnitude < r * r;
        }

        // ---- AIActionHeadOnRam (Rhino) ----

        void HeadOnRam(float dt)
        {
            var target = Target;
            var tBody = target.GetComponent<Rigidbody>();
            var aim = Steer.Intercept(Pos, Speed, target.transform.position, tBody ? tBody.linearVelocity : Vector3.zero);
            DriveTarget = aim;
            DriveSpeed = PoliceData.Kph(100f);
        }

        // ---- GetUnstuck (AIActionGetUnstuck.cpp:44-95) ----

        void WatchStuck(float dt)
        {
            if (Destroyed || _override.HasValue || DriveSpeed < 0.5f) { _stuckTimer = 0; return; }
            if (Car.Input.Throttle >= 0.5f)
            {
                if (_stuckTimer <= 0) _stuckAt = Pos;
                _stuckTimer += dt;
                if (_stuckTimer > 3f)
                {
                    if ((Pos - _stuckAt).sqrMagnitude < 9f) _unstuckTimer = 2f;
                    _stuckTimer = 0;
                }
            }
            else _stuckTimer = 0;
        }

        // ---- noticing perps (AIVehicleCopCar::WatchForPerps / CheckForPursuit, :170-227) ----

        /// <summary>
        /// WatchForPerps (:170-190): every think out of a pursuit, the human players first; then, while
        /// ICopMgr::CanPursueRacers (the race has opponents and no racer is pursued yet), the AI racers (DRIVER_RACER).
        /// </summary>
        void WatchForPerps()
        {
            if (!Police.CanStartPursuit) return;
            var player = Police.Player;
            if (player && CheckForPursuit(player, Police.InPursuit, Police.Active911, Time.time - Police.LastTrafficHitTime < 1f, Police.PerpHeat)) return;
            if (!Police.CanPursueRacers) return;
            var race = RaceManager.Active;
            if (!race) return;
            foreach (var e in race.Entrants)
            {
                if (e.Human || e.Remote || !e.AI || !e.Car || !e.InRace) continue;
                if (CheckForPursuit(e.Car, Police.PursuitOf(e.Car) != null, false, Time.time - e.AI.LastTrafficHitTime < 1f, (int)e.AI.Heat)) return;
            }
        }

        /// <summary>
        /// CheckForPursuit (:192-227): the perp must be in sight (patrol LOS 150 / 50 m) and either already pursued, under
        /// an active 911 call, have hit traffic within 1 s, be at heat 4+, or drive 65 mph or more; then the target is
        /// acquired and the manager adds this car to the perp's pursuit (or creates it) on its next tick.
        /// </summary>
        bool CheckForPursuit(PlayerCar target, bool alreadyPursuit, bool active911, bool hitTraffic, int heat)
        {
            if (!CanSeeTarget(target, FrontLOS, RearLOS, false)) return false;
            if (!alreadyPursuit && !active911 && !hitTraffic && heat <= 3 && target.SpeedMps < PoliceData.Mph(65f)) return false;
            _lastTarget = target;
            Police.PursuitRequest(this, target);
            return true;
        }

        /// <summary>
        /// CanSeeTarget (AIVehicleCopCar.cpp:95-160): within the front distance, or the rear one when the target is
        /// behind (dot ≤ −0.3); not while the busted meter is held off; a ray from +0.5 m to +0.5 m must be clear.
        /// </summary>
        public bool CanSeeTarget(PlayerCar target, float front, float rear, bool inPursuit)
        {
            // a perp hidden from cars stays unseen by a cop that lost it while it was hidden (mPerpHiddenFromMe)
            bool hidden = target == View.Target && View.PerpHiddenFromCars;
            if (hidden && _perpHiddenFromMe) return false;
            _perpHiddenFromMe = false;
            var tp = target.transform.position;
            var to = tp - Pos;
            float dist = to.magnitude;
            bool outOfSight = dist >= front || (dist >= rear && Vector3.Dot(transform.forward, to / Mathf.Max(1e-4f, dist)) <= -0.3f);
            if (inPursuit && !View.PursuitMeterCanShowBusted) outOfSight = true;
            if (!outOfSight && WorldRay(Pos + Vector3.up * 0.5f, to / Mathf.Max(1e-4f, dist), dist, out _)) outOfSight = true;
            if (outOfSight)
            {
                if (hidden) _perpHiddenFromMe = true;
                return false;
            }
            return true;
        }

        /// <summary>AIVehicle::WorldCollision (:1053): beyond 200 m, or a world hit short of the destination.</summary>
        public static bool WorldCollision(Vector3 from, Vector3 to)
        {
            if ((from - to).sqrMagnitude > 40000f) return true;
            var a = from + Vector3.up * 0.5f;
            var d = to + Vector3.up * 0.5f - a;
            float len = d.magnitude;
            if (len < 1e-3f) return false;
            if (!WorldRay(a, d / len, len, out var hit)) return false;
            return (hit.point - (to + Vector3.up * 0.5f)).sqrMagnitude > 0.5f;
        }

        static readonly RaycastHit[] Hits = new RaycastHit[16];

        /// <summary>A ray against the static world (cars and other rigid bodies ignored).</summary>
        public static bool WorldRay(Vector3 from, Vector3 dir, float len, out RaycastHit first)
        {
            first = default;
            int n = Physics.RaycastNonAlloc(from, dir, Hits, len, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.rigidbody != null) continue;
                if (h.distance < best) { best = h.distance; first = h; }
            }
            return best < float.MaxValue;
        }

        // ---- damage (DamageVehicle / DamageCopCar) ----

        // DamageCopCar's flip rule (CheckUpright: 6 s not upright, at once when the player caused it) is VehicleDamage.CheckUpright,
        // run by TrafficVehicle's 0.1 s task with Sim.CausedByPlayer (the earlier FlipCheck duplicate is gone).

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(DriveTarget, 0.6f);
            if (Nav != null && Nav.Valid) { Gizmos.color = Color.cyan; Gizmos.DrawSphere(Nav.Position, 0.4f); }
        }
    }
}
