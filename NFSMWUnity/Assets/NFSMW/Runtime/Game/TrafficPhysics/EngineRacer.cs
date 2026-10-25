using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// Physics/Behaviors/EngineRacer.cpp:1-1330 (speed.exe ctor 0x6B37B0, Reset 0x6A0760, OnTaskSimulate 0x6AF740,
    /// DoNos 0x692930, DoInduction 0x692C50, DoECU 0x692840, DoThrottle 0x692EB0, DoShifting 0x6AF670, AutoShift
    /// 0x6AF490, FindShiftPotential 0x691F90, OnGearChange 0x6920D0, DoGearChange 0x6A0AA0, GetBrakingTorque 0x6A0910,
    /// GetEngineTorque 0x691D40, GetDifferentialAngularVelocity 0x692140, SetDifferentialAngularVelocity 0x692380,
    /// GetDriveWheelSlippage 0x6922C0, LimitFreeWheels 0x692690, Engine_SmoothRPM 0x6927A0, Clutch::Engage 0x6919C0,
    /// Clutch::Update 0x691A10, GuessGear 0x6A0800, GuessRPM 0x691DA0, MatchSpeed 0x6A0890, CalcShiftPoints 0x691ED0):
    /// the racer engine and gearbox the cop pvehicles name. The engine spins on its own inertia, coupled to the driven
    /// axle by a three-state clutch (ClutchStiffness 20 / ClutchLimiter 300 while engaging, a stiff "response" coupling
    /// when engaged), the TORQUE curve × (1 + induction boost) × NOS boost, ENGINE_BRAKING off the throttle, the
    /// TORQUE_CONVERTER below peak torque, the CLUTCH_SLIP launch play in 1st/reverse, the automatic box with coast
    /// downshifts, a turbo/supercharger spool, the NOS tank, and the red-line limiter that clamps the engine and the
    /// driven wheels. Physics::Info helpers (PhysicsInfo.cpp: Torque 0x672FE0, ShiftPoints 0x673400, InductionBoost
    /// 0x66BCC0, InductionRPM 0x66BC60, EngineInertia 0x66BB00, NosBoost 0x66BC10, NosCapacity 0x66BC40, Speedometer
    /// 0x66C610, WheelDiameter 0x66BEE0, MaxInductedTorque 0x66C2D0 → 0x66C180) are private here with Physics::Tunings
    /// absent (AI cars). Gear ids are the sim's (0 reverse, 1 neutral, 2 first..), indexing GEAR_RATIO directly.
    /// Units as the exe: torque N·m (ft-lb × 1.3558), angular speeds rad/s, rpm, speeds m/s.
    /// Not ported: sabotage/blow events (prologue only; exe oscillation is ω·(1 + 0.5·sin(2π·t)), not the decomp's
    /// 12 Hz), EPlayerShift events, horsepower read-outs, the player's EngineDragster parts (perfect shifts, heat, overrev); the AI's EngineDragster
    /// rules are <see cref="Dragster"/>.
    /// </summary>
    public sealed class EngineRacer
    {
        // ShiftPotential / ShiftStatus (PhysicsTypes.h), Clutch::State, DriverClass
        public const int PotentialNone = 0, PotentialDown = 1, PotentialUp = 2;
        public const int StatusNone = 0, StatusNormal = 1;
        const int ClutchEngaged = 0, ClutchEngaging = 1, ClutchDisengaged = 2;
        const int DriverRemote = 6;
        const int InductionNone = 0, InductionTurbo = 1, InductionSuper = 2;

        // speed.exe constants (checked against .data / .rdata)
        const float RPM2RPS = 0.10471976f;          // 0x8A9398
        const float RPS2RPM = 9.549297f;            // 0x8A9394
        const float MPS2MPH = 2.23694f;             // 0x8967E0
        const float MPH2MPS = 0.44704f;             // 0x890958
        const float Epsilon = 1e-6f;                // _Epsilon 0x8AABE0
        /// <summary>ClutchStiffness (.data 0x8F8D9C = 20).</summary>
        public static float ClutchStiffness = 20f;
        /// <summary>ClutchLimiter (.data 0x8F8DC8 = 300).</summary>
        public static float ClutchLimiter = 300f;
        /// <summary>SmoothRPMDecel (0x8AAE0C = {2.5, 15}).</summary>
        static readonly float[] SmoothRPMDecel = { 2.5f, 15f };
        const float Tweak_CoastingPercent = 0.65f;      // 0x8AAE04
        const float Tweak_IdleClutchRPM = 800f;         // 0x8AAE24 (×3 = 2400 for reverse, 0x8ABE70)
        const float Tweak_ClutchEngageTime = 0.25f;     // immediate in 0x6AF99E
        const float Tweak_1stGearClutchEngageTime = 0.05f; // immediate in 0x6AF9F2
        const float Tweak_SeizeRPM = 2000f;             // 0x8AAC54 (shared constant)
        const float Tweak_MinSpeedForNosMPH = 10f;      // 0x8AAC84; half of it 0x8AAC18 = 5
        const float Tweak_MaxPSIChangePerSec = 20f;     // 0x8AAD94
        const float Tweak_SportShiftDecay = 2f;         // 0x8AAC9C
        const float Tweak_CheaterTorqueBoost = 0.5f;    // 0x8933D4 in 0x6B0016
        /// <summary>Tweak_InfiniteNOS (0x937804, 0 in the exe).</summary>
        public static bool Tweak_InfiniteNOS;
        /// <summary>ClutchPlayTable (0x8F8FD8 → 0x8F8DA0, 5 points).</summary>
        static readonly Vector2[] ClutchPlayTable =
        {
            new Vector2(-10f, 1f), new Vector2(-7.5f, 0.96f), new Vector2(-3.5f, 0.925f), new Vector2(-0.3f, 0.875f), new Vector2(-0.05f, 0f),
        };

        readonly TrafficVehicle _owner;
        readonly CarSpec _c;
        readonly Chassis _susp;
        readonly float[] _shiftUpRpm = new float[10], _shiftDownRpm = new float[10];
        /// <summary>Physics::Info::WheelDiameter per axle as the exe computes it (0x66BEE0: rim·0.0254 + aspect·0.01·width·0.001,
        /// one sidewall, unlike the decomp's two).</summary>
        readonly float _wheelDiameterFront, _wheelDiameterRear;

        float _driveTorque, _gearShiftTimer, _throttle, _spool, _psi, _inductionBoost;
        float _angularVelocity, _angularAcceleration, _transmissionVelocity;
        float _nosCapacity = 1f, _nosBoost = 1f, _nosEngaged, _clutchRpmDiff, _sportShifting, _rpm;
        float _peakTorque, _peakTorqueRpm;
        int _gear = Chassis.GNeutral, _shiftStatus, _shiftPotential;
        bool _engineBraking;
        // Clutch (EngineRacer.cpp:40-105)
        int _clutchState;
        float _clutchTime, _clutchEngageTime;

        /// <summary>IInput::IsAutomaticShift: PInput (AI) returns true [ours: settable].</summary>
        public bool AutomaticShift = true;
        /// <summary>ICheater::GetCatchupCheat, or null when the owner has no ICheater (cops: AIVehiclePursuit has none).</summary>
        public float? CatchupCheat;
        /// <summary>IVehicle::GetPerfectLaunch [ours: 0 for AI; TrafficVehicle has no perfect launch].</summary>
        public float PerfectLaunch;
        /// <summary>IEngineDamage::IsBlown (cuts the throttle and the NOS).</summary>
        public bool Blown;
        /// <summary>
        /// EngineDragster (EngineRacer.cpp:1355-1645): what LookupBehaviorSignature 0x67F2E7 builds for a CAR in drag style.
        /// For a non-player owner (Tweak_AllowAIPerfectShift false) it is EngineRacer except: OnGearChange refuses reverse
        /// (bUseReverseInDrag false) and, while staging, anything but first or neutral; UseRevLimiter only while staging
        /// (MAX_RPM otherwise); GetEngineTorque × GetShiftBoost (1 without a perfect shift) and the heat penalty (heat only
        /// builds for DRIVER_HUMAN) leave the torque as is. The overrev read-out is not kept.
        /// </summary>
        public bool Dragster => _owner.DriverStyle == Chassis.StyleDrag;

        public int Gear => _gear;
        public float DriveTorque => _driveTorque;
        public float Rpm => _rpm;
        public float NosBoost => _nosBoost;
        public float NosCapacity => _nosCapacity;
        public bool NosEngaged => _nosEngaged >= 1f;
        public bool IsShifting => _gearShiftTimer > 0f;
        public bool IsReversing => _gear == Chassis.GReverse;
        public bool EngineBraking => _engineBraking;
        public int ShiftPotential => _shiftPotential;
        public int ShiftStatus => _shiftStatus;
        public int NumGearRatios => _c.GearRatios != null ? _c.GearRatios.Length : 0;
        public int TopGear => NumGearRatios - 1;
        public float Throttle => _throttle;
        public float AngularVelocity => _angularVelocity;
        public float InductionPsi => _psi;
        public float InductionSpool => _spool;
        public float PeakTorque => _peakTorque;
        public float PeakTorqueRpm => _peakTorqueRpm;
        public bool HasNos => _c.NosCapacity > 0f && _c.NosTorqueBoost > 0f;
        /// <summary>ITransmission::GetSpeedometer (m/s).</summary>
        public float Speedometer => CalcSpeedometer(_transmissionVelocity * RPS2RPM, _gear);

        public EngineRacer(TrafficVehicle owner, CarSpec spec, Chassis suspension)
        {
            _owner = owner;
            _c = spec;
            _susp = suspension;
            if (!ReadWheelDiameters(owner, out _wheelDiameterFront, out _wheelDiameterRear))
            {
                // [?] no tires collection: the CarSpec radius (rim/2 + full sidewall) doubled
                _wheelDiameterFront = spec.TireRadiusFront * 2f;
                _wheelDiameterRear = spec.TireRadiusRear * 2f;
            }
            Reset();
            _nosCapacity = _c.NosCapacity > 0f ? 1f : 0f;   // ctor 0x6B3AED
            CalcShiftPoints();
        }

        // ---------------------------------------------------------------- attribute access (bounds as Attrib: out of range → 0)

        float Ratio(int g) => _c.GearRatios != null && g >= 0 && g < _c.GearRatios.Length ? _c.GearRatios[g] : 0f;
        float Efficiency(int g) => _c.GearEfficiency != null && g >= 0 && g < _c.GearEfficiency.Length ? _c.GearEfficiency[g] : 0f;
        bool RearWheelDrive => _c.TorqueSplit < 1f;
        bool FrontWheelDrive => _c.TorqueSplit > 0f;

        /// <summary>GetRatioChange 0x691B30.</summary>
        float RatioChange(int from, int to)
        {
            float r1 = Ratio(from), r2 = Ratio(to);
            return r1 > 0f && r2 > Epsilon ? r1 / r2 : 0f;
        }

        /// <summary>GetShiftDelay 0x691BD0: SHIFT_SPEED × GEAR_RATIO(gear).</summary>
        float ShiftDelay(int g) => _c.ShiftSpeed * Ratio(g);

        /// <summary>tires of the pvehicle's first ref [ours: CarSpec keeps only the full radius; read like VehicleSpecs does].</summary>
        static bool ReadWheelDiameters(TrafficVehicle owner, out float front, out float rear)
        {
            front = rear = 0f;
            var db = GameData.Attributes;
            string name = owner != null && owner.Specs != null ? owner.Specs.Vehicle : null;
            if (db == null || string.IsNullOrEmpty(name)) return false;
            var pv = db.Get("pvehicle", name);
            if (pv == null) return false;
            foreach (var r in pv.Refs("tires"))
            {
                if (!db.Collections.TryGetValue(r, out var ti)) continue;
                var ar = ti.Pair("ASPECT_RATIO", (35f, 35f));
                var rim = ti.Pair("RIM_SIZE", (18f, 18f));
                var w = ti.Pair("SECTION_WIDTH", (255f, 255f));
                front = ar.front * 0.01f * w.front * 0.001f + rim.front * 0.0254f;
                rear = ar.rear * 0.01f * w.rear * 0.001f + rim.rear * 0.0254f;
                return front > 0f && rear > 0f;
            }
            return false;
        }

        // ---------------------------------------------------------------- Physics::Info

        /// <summary>Physics::Info::Torque 0x672FE0 (ft-lb): TORQUE over IDLE..MAX_RPM, rpm clamped to IDLE..RED_LINE.</summary>
        float TorqueFtLb(float rpm) => Interpolate(_c.Torque, rpm, true);

        /// <summary>The engine tables' lookup (Torque 0x672FE0 / GetBrakingTorque 0x6A0910): index truncated, next index capped.</summary>
        float Interpolate(float[] t, float rpm, bool zeroIfSingle)
        {
            int n = t != null ? t.Length : 0;
            if (n <= 1) return n == 1 && !zeroIfSingle ? t[0] : 0f;
            float r = Mathf.Max(Mathf.Min(rpm, _c.RedLine), _c.IdleRpm);
            float pos = (r - _c.IdleRpm) / (_c.MaxRpm - _c.IdleRpm) * (n - 1);
            int index = (int)pos;
            if (index < 0) index = 0;
            float ratio = pos - index;
            float a = index < n ? t[index] : 0f;
            float b = t[Mathf.Min(index + 1, n - 1)];
            return (b - a) * ratio + a;
        }

        /// <summary>Physics::Info::EngineInertia 0x66BB00.</summary>
        float EngineInertia(bool loaded) => (loaded ? 1f : 0.35f) * (_c.FlywheelMass * 0.025f + 0.25f);

        /// <summary>Physics::Info::InductionType 0x66BB30.</summary>
        int InductionType() => _c.HighBoost > 0f || _c.LowBoost > 0f ? (_c.Spool > 0f ? InductionTurbo : InductionSuper) : InductionNone;

        /// <summary>Physics::Info::InductionRPM 0x66BC60 (no tunings).</summary>
        float InductionRpm() => _c.Spool * (_c.RedLine - _c.IdleRpm) + _c.IdleRpm;

        /// <summary>Physics::Info::InductionBoost 0x66BCC0 (no tunings): boost ratio × spool, and the gauge psi.</summary>
        float InductionBoost(float rpm, float spool, out float psi)
        {
            psi = 0f;
            spool = spool >= 1f ? 1f : (0f <= spool ? spool : 0f);
            float rpmMin = _c.IdleRpm, rpmMax = _c.RedLine;
            float spoolRpm = InductionRpm();
            float high = _c.HighBoost, low = _c.LowBoost, drag = _c.Vacuum;
            float boost = 0f;
            if (high > 0f || low > 0f)
            {
                float maxBoost = high > low ? high : low;
                if (rpm >= spoolRpm)
                {
                    float ratio = Chassis.Ramp(rpm, spoolRpm, rpmMax);
                    boost = (1f - ratio) * low + ratio * high;
                    psi = Chassis.Ramp(boost, 0f, maxBoost) * _c.Psi * spool;
                }
                else if (drag < 0f)
                {
                    float dragRatio = Chassis.Ramp(rpm, rpmMin, spoolRpm);
                    boost = dragRatio * drag;
                    psi = -(Chassis.Ramp(-boost, 0f, maxBoost) * _c.Psi * dragRatio);
                }
            }
            return boost * spool;
        }

        /// <summary>
        /// Physics::Info::ShiftPoints 0x673400: from the mid rpm up in 50 rpm steps until the next gear's torque at the
        /// wheel beats this one's (red line − 100 otherwise); the next gear's downshift is the same road speed. The
        /// induction term is called with rpm and spool swapped (rpm 1, so it is always 0), as in the exe.
        /// </summary>
        void ShiftPoints()
        {
            System.Array.Clear(_shiftUpRpm, 0, _shiftUpRpm.Length);
            System.Array.Clear(_shiftDownRpm, 0, _shiftDownRpm.Length);
            int num = NumGearRatios;
            if (_shiftUpRpm.Length < num) return;
            float redline = _c.RedLine;
            int top = num - 1;
            for (int j = Chassis.GFirst; j < top; j++)
            {
                float g1 = Ratio(j), g2 = Ratio(j + 1);
                float max = (redline + _c.IdleRpm) * 0.5f;
                bool found = false;
                if (max < redline)
                {
                    while (true)
                    {
                        float current = TorqueFtLb(max) * (InductionBoost(1f, max, out _) + 1f);
                        float up = 0f;
                        if (Mathf.Abs(g1) > 1e-5f)
                        {
                            float inv = 1f / g1;
                            float next = inv * g2 * max;
                            up = TorqueFtLb(next) * (InductionBoost(1f, next, out _) + 1f) * inv * g2;
                        }
                        if (up > current) { found = true; break; }
                        max += 50f;
                        if (!(max < redline)) break;
                    }
                }
                _shiftUpRpm[j] = found ? max : redline - 100f;
                _shiftDownRpm[j + 1] = Mathf.Abs(g1) > 1e-5f ? g2 / g1 * _shiftUpRpm[j] : 0f;
            }
            if (top >= 0) _shiftUpRpm[top] = _c.RedLine;
        }

        /// <summary>Physics::Info::MaxInductedTorque 0x66C180 (via the pvehicle 0x66C2D0): the largest TORQUE point
        /// × full-spool boost (ft-lb) and its rpm, clamped to IDLE..RED_LINE.</summary>
        float MaxInductedTorque(out float atRpm)
        {
            var t = _c.Torque;
            int n = t != null ? t.Length : 0;
            atRpm = _c.IdleRpm;
            if (n <= 1) return 0f;
            float best = 0f, rpm = _c.IdleRpm;
            float step = (_c.MaxRpm - _c.IdleRpm) / (n - 1);
            for (int i = 0; i < n; i++)
            {
                float v = (InductionBoost(rpm, 1f, out _) + 1f) * t[i];
                if (v > best) { best = v; atRpm = rpm; }
                rpm += step;
            }
            atRpm = Mathf.Max(Mathf.Min(atRpm, _c.RedLine), _c.IdleRpm);
            return best;
        }

        /// <summary>Physics::Info::Speedometer 0x66C610 (no tunings): road speed at `rpm` in `gear`, capped at SPEED_LIMITER[0].</summary>
        float CalcSpeedometer(float rpm, int gear)
        {
            float speed = 0f;
            float gearRatio = Mathf.Abs(Ratio(gear) * _c.FinalGear);
            float powerRange = _c.RedLine - _c.IdleRpm;
            if (gearRatio > 0f && powerRange > 0f)
            {
                float avgRadius = (_wheelDiameterRear * 0.5f + _wheelDiameterFront * 0.5f) * 0.5f;
                speed = (rpm - _c.IdleRpm) / gearRatio / powerRange * _c.RedLine * avgRadius * RPM2RPS;
            }
            float limiter = _c.SpeedLimiter * MPH2MPS;
            if (limiter > 0f) speed = speed >= limiter ? limiter : speed;
            return speed;
        }

        // ---------------------------------------------------------------- Clutch (EngineRacer.cpp:40-105, 0x6919C0 / 0x691A10)

        void ClutchDisengage()
        {
            if (_clutchState == ClutchEngaged) _clutchState = ClutchDisengaged;
        }

        void ClutchEngage(float time)
        {
            if (_clutchState != ClutchDisengaged) return;
            _clutchState = time > 0f ? ClutchEngaging : ClutchEngaged;
            _clutchTime = time;
            _clutchEngageTime = time;
        }

        float ClutchUpdate(float dT)
        {
            if (_clutchTime > 0f)
            {
                _clutchTime -= dT;
                if (_clutchTime <= 0f && _clutchState == ClutchEngaging) _clutchState = ClutchEngaged;
            }
            switch (_clutchState)
            {
                case ClutchDisengaged: return 0.25f;
                case ClutchEngaging: return 1f - Chassis.Ramp(_clutchTime, 0f, _clutchEngageTime) * 0.75f;
                default: return 1f;
            }
        }

        // ---------------------------------------------------------------- reset / match speed

        /// <summary>EngineRacer::Reset 0x6A0760 (first gear, idle, clutch engaged; the NOS tank is kept).</summary>
        public void Reset()
        {
            _driveTorque = 0f;
            _angularVelocity = _c.IdleRpm * RPM2RPS;
            _angularAcceleration = 0f;
            _rpm = _c.IdleRpm;
            _transmissionVelocity = 0f;
            _clutchState = ClutchEngaged;
            _clutchTime = 0f;
            _gearShiftTimer = 0f;
            _spool = 0f;
            _psi = 0f;
            _inductionBoost = 0f;
            _shiftStatus = StatusNone;
            _shiftPotential = PotentialNone;
            _gear = Chassis.GFirst;
            _nosEngaged = 0f;
            _clutchRpmDiff = 0f;
            _throttle = 0f;
            _nosBoost = 1f;
            _sportShifting = 0f;
            CalcShiftPoints();
        }

        /// <summary>EngineRacer::CalcShiftPoints 0x691ED0 (MaxInductedPower is a read-out only, not ported).</summary>
        void CalcShiftPoints()
        {
            ShiftPoints();
            _peakTorque = MaxInductedTorque(out _peakTorqueRpm) * Chassis.FTLB2NM;
        }

        /// <summary>EngineRacer::MatchSpeed 0x6A0890: the gear and engine speed for a car already rolling at `speed` (m/s).</summary>
        public void MatchSpeed(float speed)
        {
            Reset();
            _angularAcceleration = 0f;
            _sportShifting = 0f;
            _gearShiftTimer = 0f;
            _gear = GuessGear(speed);
            _rpm = GuessRpm(speed, _gear);
            _angularVelocity = _rpm * RPM2RPS;
            _transmissionVelocity = _angularVelocity;
            ClutchEngage(0f);
        }

        /// <summary>GuessGear 0x6A0800.</summary>
        int GuessGear(float speed)
        {
            if (speed < 0f) return Chassis.GReverse;
            int result = Chassis.GFirst;
            for (int i = Chassis.GFirst; i < TopGear; i++)
            {
                float shiftAt = GetShiftPoint(i, i + 1);
                float thisRpm = GuessRpm(speed, i);
                if (thisRpm <= shiftAt) break;
                result = i + 1;
            }
            return result;
        }

        /// <summary>GuessRPM 0x691DA0.</summary>
        float GuessRpm(float speed, int gear)
        {
            float avgRadius = (_wheelDiameterRear * 0.5f + _wheelDiameterFront * 0.5f) * 0.5f;
            if (!(avgRadius > 0f)) return _c.IdleRpm;
            float differentialW = Mathf.Abs(speed) / avgRadius;
            float maxW = _c.RedLine * RPM2RPS, minW = _c.IdleRpm * RPM2RPS;
            float av = (_c.FinalGear * Ratio(gear) * differentialW * ((maxW - minW) / maxW) + minW) * RPS2RPM;
            return Mathf.Max(Mathf.Min(av, _c.RedLine), _c.IdleRpm);
        }

        /// <summary>ITransmission::GetShiftPoint (EngineRacer.cpp:1323).</summary>
        public float GetShiftPoint(int fromGear, int toGear)
        {
            if (fromGear <= Chassis.GReverse || toGear <= Chassis.GNeutral) return 0f;
            if (fromGear >= _shiftUpRpm.Length) return 0f;   // [ours] the exe's arrays hold 10
            if (toGear > fromGear) return _shiftUpRpm[fromGear];
            if (toGear < fromGear) return _shiftDownRpm[fromGear];
            return 0f;
        }

        // ---------------------------------------------------------------- gear changes

        /// <summary>ITransmission::Shift → DoGearChange(gear, false).</summary>
        public bool Shift(int gear) => DoGearChange(gear, false);

        /// <summary>ITiptronic::SportShift (EngineRacer.cpp:689): a manual shift that holds off the auto box for 1.25 s.</summary>
        public bool SportShift(int gear)
        {
            if (gear == _gear || gear <= Chassis.GNeutral || IsShifting) return false;
            if (gear > _gear && _shiftPotential == PotentialDown) return false;
            if (gear < _gear && _shiftPotential == PotentialUp) return false;
            if (!DoGearChange(gear, false)) return false;
            _sportShifting = 1.25f;
            return true;
        }

        /// <summary>DoGearChange 0x6A0AA0 (no EPlayerShift: not a player).</summary>
        bool DoGearChange(int gear, bool automatic)
        {
            if (gear > TopGear || gear < Chassis.GReverse) return false;
            int status = OnGearChange(gear);
            if (status == StatusNone) return false;
            _shiftStatus = status;
            _shiftPotential = PotentialNone;
            return true;
        }

        /// <summary>OnGearChange 0x6920D0: the shift delay (a quarter for a downshift) and the clutch out.</summary>
        int OnGearChange(int gear)
        {
            if (gear >= NumGearRatios || gear == _gear || gear < Chassis.GReverse) return StatusNone;
            // EngineDragster::OnGearChange (EngineRacer.cpp:1516): no reverse; on the line only first or neutral
            if (Dragster && (gear == Chassis.GReverse || (_owner.Staging && gear != Chassis.GFirst && gear != Chassis.GNeutral))) return StatusNone;
            _gearShiftTimer = gear < _gear ? ShiftDelay(gear) * 0.25f : ShiftDelay(gear);
            _gear = gear;
            ClutchDisengage();
            return StatusNormal;
        }

        /// <summary>AutoShift 0x6AF490: neutral → first; down as far as the coast points allow; up only with all four
        /// wheels down and slipping under 4 m/s.</summary>
        void AutoShift()
        {
            if (_gear == Chassis.GReverse || _gearShiftTimer > 0f || _owner.Staging || _sportShifting > 0f) return;
            if (_gear == Chassis.GNeutral)
            {
                _gear = Chassis.GFirst;
                return;
            }
            switch (_shiftPotential)
            {
                case PotentialDown:
                {
                    int next = _gear - 1;
                    if (next > Chassis.GFirst)
                    {
                        float currentRpm = _transmissionVelocity * RPS2RPM;
                        float rpm = RatioChange(next, _gear) * currentRpm;
                        while (FindShiftPotential(next, rpm) == PotentialDown)
                        {
                            next--;
                            rpm = RatioChange(next, _gear) * currentRpm;
                            if (!(next > Chassis.GFirst)) break;
                        }
                    }
                    DoGearChange(next, true);
                    break;
                }
                case PotentialUp:
                case 4: // SHIFT_POTENTIAL_PERFECT
                case 5: // SHIFT_POTENTIAL_MISS (the jump table at 0x6AF5E0 skips 3 = GOOD)
                {
                    bool traction = true;
                    for (int i = 0; i < 4; i++)
                        traction &= _susp.Wheels[i].IsOnGround && _susp.WheelSlip(i) < 4f;
                    if (traction) DoGearChange(_gear + 1, true);
                    break;
                }
            }
        }

        /// <summary>FindShiftPotential 0x691F90: off the throttle the downshift point drops toward 65 % of the way
        /// from idle, never above the lower gear's upshift point − 200 rpm.</summary>
        int FindShiftPotential(int gear, float rpm)
        {
            if (gear <= Chassis.GNeutral || gear >= _shiftUpRpm.Length) return PotentialNone;   // [ours] upper bound
            float shiftUp = _shiftUpRpm[gear];
            float shiftDown = _shiftDownRpm[gear];
            float lowerRatio = Ratio(gear - 1);
            if (gear != Chassis.GFirst && lowerRatio > 0f)
            {
                float lowerUp = _shiftUpRpm[gear - 1] / lowerRatio * Ratio(gear) - 200f;
                float offThrottle = (shiftDown - _c.IdleRpm) * Tweak_CoastingPercent + _c.IdleRpm;
                shiftDown = (shiftDown - offThrottle) * _throttle + offThrottle;
                shiftDown = shiftDown < lowerUp ? shiftDown : lowerUp;
            }
            if (rpm >= shiftUp && gear < TopGear) return PotentialUp;
            if (rpm <= shiftDown && gear > Chassis.GFirst) return PotentialDown;
            return PotentialNone;
        }

        // ---------------------------------------------------------------- driveline

        /// <summary>GetDifferentialAngularVelocity 0x692140: the driven axles' mean wheel speed (the faster axle; when
        /// `locked`, never against the gear).</summary>
        float DifferentialAngularVelocity(bool locked)
        {
            float into = 0f;
            bool reverse = _gear == Chassis.GReverse;
            if (FrontWheelDrive)
            {
                float w = (_susp.WheelAngularVelocity(1) + _susp.WheelAngularVelocity(0)) * 0.5f;
                if (!locked) { if (Mathf.Abs(w) > 0f) into = w; }
                else into = reverse ? Mathf.Min(w, 0f) : Mathf.Max(w, 0f);
            }
            if (RearWheelDrive)
            {
                float w = (_susp.WheelAngularVelocity(3) + _susp.WheelAngularVelocity(2)) * 0.5f;
                if (!locked) { if (Mathf.Abs(w) > Mathf.Abs(into)) into = w; }
                else into = reverse ? (w < into ? w : into) : (w > into ? w : into);
            }
            return into;
        }

        /// <summary>GetDriveWheelSlippage 0x6922C0.</summary>
        float DriveWheelSlippage()
        {
            float r = 0f;
            int n = 0;
            if (RearWheelDrive) { n += 2; r = _susp.WheelSlip(3) + _susp.WheelSlip(2); }
            if (FrontWheelDrive) { n += 2; r = _susp.WheelSlip(1) + _susp.WheelSlip(0) + r; }
            return r / n;
        }

        /// <summary>SetDifferentialAngularVelocity 0x692380: moves the driven wheels by the same Δω; under 40 mph (or with
        /// both wheels of the axle in the air) the axle is locked to its mean first.</summary>
        void SetDifferentialAngularVelocity(float w)
        {
            float diff = w - DifferentialAngularVelocity(false);
            float speedMph = AbsoluteSpeed * MPS2MPH;
            bool lockDiff = speedMph < 40f;
            if (RearWheelDrive)
            {
                if (!_susp.Wheels[2].IsOnGround && !_susp.Wheels[3].IsOnGround) lockDiff = true;
                float w1 = _susp.WheelAngularVelocity(2), w2 = _susp.WheelAngularVelocity(3);
                if (lockDiff) w1 = w2 = (w2 + w1) * 0.5f;
                _susp.SetWheelAngularVelocity(2, w1 + diff);
                _susp.SetWheelAngularVelocity(3, w2 + diff);
            }
            lockDiff = speedMph < 40f;
            if (FrontWheelDrive)
            {
                if (!_susp.Wheels[0].IsOnGround && !_susp.Wheels[1].IsOnGround) lockDiff = true;
                float w1 = _susp.WheelAngularVelocity(0), w2 = _susp.WheelAngularVelocity(1);
                if (lockDiff) w1 = w2 = (w2 + w1) * 0.5f;
                _susp.SetWheelAngularVelocity(0, w1 + diff);
                _susp.SetWheelAngularVelocity(1, w2 + diff);
            }
        }

        /// <summary>LimitFreeWheels 0x692690: an airborne driven wheel may not spin past the red line (nor backwards).</summary>
        void LimitFreeWheels(float w)
        {
            for (int i = 0; i < _susp.Wheels.Length; i++)
            {
                if (_susp.Wheels[i].IsOnGround) continue;
                if (i < 2 ? !FrontWheelDrive : !RearWheelDrive) continue;
                float ww = _susp.WheelAngularVelocity(i);
                if (ww * w < 0f) ww = 0f;
                else if (ww > 0f) ww = ww >= w ? w : ww;
                else if (ww < 0f) ww = ww <= w ? w : ww;
                _susp.SetWheelAngularVelocity(i, ww);
            }
        }

        float AbsoluteSpeed => _owner.Rb != null ? _owner.Rb.linearVelocity.magnitude : 0f;

        // ---------------------------------------------------------------- engine

        /// <summary>GetEngineTorque 0x691D40 (N·m): TORQUE × 1.3558 × (1 + induction), × NOS unless REMOTE.</summary>
        float EngineTorque(float rpm)
        {
            float result = TorqueFtLb(rpm) * Chassis.FTLB2NM;
            result = (_inductionBoost + 1f) * result;
            if (_owner.DriverClass != DriverRemote) result *= _nosBoost;
            return result;
        }

        /// <summary>GetBrakingTorque 0x6A0910: −torque × ENGINE_BRAKING(rpm) clamped 0..1 (one point: unclamped).</summary>
        float BrakingTorque(float engineTorque, float rpm)
        {
            var eb = _c.EngineBraking;
            int n = eb != null ? eb.Length : 0;
            if (n > 1) return -(Mathf.Clamp01(Interpolate(eb, rpm, false)) * engineTorque);
            return -(engineTorque * (n == 1 ? eb[0] : 0f));
        }

        /// <summary>Engine_SmoothRPM 0x6927A0: the shown rpm falls no faster than 2.5 (15 while shifting above first
        /// or in neutral) ×1000/inertia rpm/s, then 55/45 blended with the last value.</summary>
        static float SmoothRpm(bool isShifting, int gear, float dT, float oldRpm, float newRpm, float engineInertia)
        {
            bool fast = (isShifting && gear > Chassis.GFirst) || gear == Chassis.GNeutral;
            float maxDecel = -SmoothRPMDecel[fast ? 1 : 0] / engineInertia * 1000f;
            float rpm = newRpm;
            if (dT > 0f && (rpm - oldRpm) / dT < maxDecel)
            {
                float r = maxDecel * dT + oldRpm;
                rpm = r < rpm ? rpm : r;
            }
            return rpm * 0.55f + oldRpm * 0.45f;
        }

        /// <summary>DoThrottle 0x692EB0: IInput fGas, 0 when blown.</summary>
        float DoThrottle() => Blown ? 0f : _owner.Gas;

        /// <summary>
        /// DoECU 0x692840: past SPEED_LIMITER[0] the throttle fades over SPEED_LIMITER[1] mph. The speedometer it reads is
        /// itself capped at SPEED_LIMITER[0] (0x66C736), so in the exe this never cuts; kept as written.
        /// </summary>
        void DoECU()
        {
            if (_gear <= Chassis.GNeutral) return;
            float limiter = _c.SpeedLimiter * MPH2MPS;
            if (!(limiter > 0f)) return;
            float cutoff = _c.SpeedLimiterFade * MPH2MPS;
            if (!(cutoff > 0f)) return;
            float speedometer = Speedometer;
            if (speedometer > limiter)
                _throttle *= 1f - Mathf.Clamp((speedometer - limiter) / cutoff, 0f, 1f);
        }

        /// <summary>DoNos 0x692930: drains the tank while engaged (≥ 10 mph to start, ≥ 5 to keep), waits NOS_DISENGAGE
        /// after, then recharges at RECHARGE_MIN..MAX seconds per tank by speed. Returns the torque scale.</summary>
        float DoNos(float dT, bool engaged)
        {
            if (!HasNos) return 1f;
            float speedMph = AbsoluteSpeed * MPS2MPH;
            float rechargeRate = 0f;
            // IPlayer::CanRechargeNOS: no player on an AI car
            float minSpeed = _c.NosRechargeMinSpeed;
            if (!(speedMph < minSpeed) && _gear >= Chassis.GFirst)
            {
                float t = Chassis.Ramp(speedMph, minSpeed, _c.NosRechargeMaxSpeed);
                rechargeRate = (_c.NosRechargeMax - _c.NosRechargeMin) * t + _c.NosRechargeMin;
            }
            if (_gear < Chassis.GFirst || !(_throttle > 0f) || Blown) engaged = false;
            if ((speedMph < Tweak_MinSpeedForNosMPH && !NosEngaged) || (speedMph < Tweak_MinSpeedForNosMPH * 0.5f && NosEngaged))
                engaged = false;
            float discharge = _c.NosCapacity;   // Physics::Info::NosCapacity, no tunings
            float torqueScale = 1f;
            if (discharge > 0f)
            {
                float disengage = _c.NosDisengage;
                if (engaged && _nosCapacity > 0f)
                {
                    float d = dT / discharge;
                    if (Tweak_InfiniteNOS || _owner.DriverClass == DriverRemote) d = 0f;
                    if (CatchupCheat.HasValue) d = (CatchupCheat.Value * -0.5f + 1f) * d;
                    _nosCapacity -= d;
                    torqueScale = _c.NosTorqueBoost + 1f;   // Physics::Info::NosBoost, no tunings
                    _nosEngaged = 1f;
                    _nosCapacity = _nosCapacity > 0f ? _nosCapacity : 0f;
                }
                else if (_nosEngaged > 0f && disengage > 0f)
                {
                    _nosEngaged -= dT / disengage;
                    _nosEngaged = _nosEngaged > 0f ? _nosEngaged : 0f;
                }
                else
                {
                    if (_nosCapacity < 1f && rechargeRate > 0f)
                    {
                        float r = dT / rechargeRate;
                        if (CatchupCheat.HasValue) r = (CatchupCheat.Value + 1f) * r;
                        _nosCapacity = Mathf.Min(r + _nosCapacity, 1f);
                    }
                    _nosEngaged = 0f;
                }
            }
            else
            {
                _nosCapacity = 0f;
                _nosEngaged = 0f;
            }
            return torqueScale;
        }

        /// <summary>DoInduction 0x692C50: spool chases Ramp(throttle, 0, 0.5) (0 while shifting, and for a turbo below
        /// its spool rpm) at SPOOL_TIME_UP / _DOWN; the psi gauge follows at 20 psi/s.</summary>
        void DoInduction(float dT)
        {
            int type = InductionType();
            if (type == InductionNone)
            {
                _spool = 0f;
                _inductionBoost = 0f;
                _psi = 0f;
                return;
            }
            float desired = Chassis.Ramp(_throttle, 0f, 0.5f);
            float rpm = _angularVelocity * RPS2RPM;
            if (IsShifting) desired = 0f;
            if (type == InductionTurbo && !(InductionRpm() <= rpm)) desired = 0f;
            if (_spool > desired)
            {
                float t = _c.SpoolDown;
                if (t > Epsilon) { _spool -= dT / t; _spool = _spool > desired ? _spool : desired; }
                else _spool = desired;
            }
            else if (_spool < desired)
            {
                float t = _c.SpoolUp;
                if (t > Epsilon) { _spool += dT / t; _spool = _spool < desired ? _spool : desired; }
                else _spool = desired;
            }
            _spool = _spool >= 1f ? 1f : (0f <= _spool ? _spool : 0f);
            _inductionBoost = InductionBoost(rpm, _spool, out float targetPsi);
            if (_psi > targetPsi)
            {
                float p = _psi - dT * Tweak_MaxPSIChangePerSec;
                _psi = p > targetPsi ? p : targetPsi;
            }
            else if (_psi < targetPsi)
            {
                float p = dT * Tweak_MaxPSIChangePerSec + _psi;
                _psi = p < targetPsi ? p : targetPsi;
            }
        }

        /// <summary>DoShifting 0x6AF670.</summary>
        void DoShifting(float dT)
        {
            if (AutomaticShift) AutoShift();
            if (_gearShiftTimer > 0f)
            {
                _gearShiftTimer -= dT;
                if (_gearShiftTimer <= 0f) _gearShiftTimer = 0f;
            }
            if (_sportShifting > 0f && _shiftPotential != PotentialNone)
            {
                float s = _sportShifting - (Tweak_SportShiftDecay - _owner.Gas) * dT;
                _sportShifting = s > 0f ? s : 0f;
            }
        }

        // ---------------------------------------------------------------- the step

        /// <summary>EngineRacer::OnTaskSimulate 0x6AF740 (EngineRacer.cpp:1093-1320). Runs before the suspension.</summary>
        public void Simulate(float dT)
        {
            if (_susp == null || _susp.Wheels.Length != 4 || !(dT > 0f)) return;
            bool isStaging = _owner.Staging;
            _throttle = DoThrottle();
            _nosBoost = DoNos(dT, _owner.Nitrous);
            DoECU();
            DoInduction(dT);
            DoShifting(dT);

            // UseRevLimiter 0x6A05E0 = true: RED_LINE; EngineDragster::UseRevLimiter = IsStaging, else MAX_RPM (EngineRacer.cpp:1111)
            float maxW = (Dragster && !isStaging ? Mathf.Max(_c.MaxRpm, _c.RedLine) : _c.RedLine) * RPM2RPS;
            float minW = _c.IdleRpm * RPM2RPS;
            float engineInertia = EngineInertia(_gear != Chassis.GNeutral);
            float axleW = DifferentialAngularVelocity(false);
            float differentialW = DifferentialAngularVelocity(true);
            int numOnGround = _susp.NumWheelsOnGround;
            float wheelRatio = Mathf.Max(0.25f, numOnGround * 0.25f);
            float clutchRatio = ClutchUpdate(dT);
            float gearDirection = _gear == Chassis.GReverse ? -1f : 1f;
            float totalGearRatio = Ratio(_gear) * _c.FinalGear * gearDirection;
            float rpm = _angularVelocity * RPS2RPM;

            float torqueConverter = _c.TorqueConverter;
            if (torqueConverter > 0f)
            {
                float converter = (1f - Chassis.Ramp(rpm, _c.IdleRpm, _peakTorqueRpm)) * torqueConverter * _throttle;
                if (IsShifting) converter *= clutchRatio;
                totalGearRatio = (converter + 1f) * totalGearRatio;
            }
            if (totalGearRatio == 0f && _gear != Chassis.GNeutral) return;

            // the clutch: 1st/reverse slip it off idle, neutral opens it, the other gears close it over 0.25 s
            if (_gear == Chassis.GReverse || _gear == Chassis.GFirst)
            {
                float idleLimit = _gear == Chassis.GReverse ? Tweak_IdleClutchRPM * 3f : Tweak_IdleClutchRPM;
                float idleW = idleLimit * RPM2RPS + minW;
                if (_angularVelocity > idleW || _transmissionVelocity > idleW || _throttle >= 0.1f) ClutchEngage(Tweak_1stGearClutchEngageTime);
                else ClutchDisengage();
            }
            else if (_gear == Chassis.GNeutral) ClutchDisengage();
            else ClutchEngage(Tweak_ClutchEngageTime);

            float engineTorque = EngineTorque(rpm);
            float brakingTorque = BrakingTorque(engineTorque, rpm);
            if (!isStaging && PerfectLaunch > 0f && _throttle > 0f)
            {
                _throttle = 1f;
                engineTorque = _peakTorque * _nosBoost;
                brakingTorque = 0f;
            }

            float oldClutchW = _transmissionVelocity;
            _transmissionVelocity = (maxW - minW) / maxW * totalGearRatio * differentialW + minW;
            float transAcceleration = (_transmissionVelocity - oldClutchW) / dT;
            // over-run: the wheels drive the engine, engine braking pushes back the other way
            if (_gear != Chassis.GNeutral && _clutchState == ClutchEngaged && gearDirection * brakingTorque * axleW > 0f)
                brakingTorque = -brakingTorque;

            float totalEngineTorque = (1f - _throttle) * brakingTorque + engineTorque * _throttle;
            float driveTorque = 0f, roadTorque = 0f;
            _engineBraking = totalEngineTorque < 0f;

            if (_gear != Chassis.GNeutral)
            {
                switch (_clutchState)
                {
                    case ClutchEngaged:
                        _clutchRpmDiff = 0f;
                        driveTorque = totalEngineTorque;
                        roadTorque = -(totalEngineTorque * wheelRatio);
                        break;
                    case ClutchEngaging:
                    {
                        float diff = _angularVelocity - _transmissionVelocity;
                        if (diff > ClutchLimiter) diff = ClutchLimiter;
                        else if (diff < -ClutchLimiter) diff = -ClutchLimiter;
                        float stiffness = ClutchStiffness;
                        float rpmDiff = (_transmissionVelocity - _angularVelocity) * RPS2RPM;
                        if (_clutchRpmDiff != 0f && rpmDiff * _clutchRpmDiff < 0f && Mathf.Abs(rpmDiff) > Tweak_SeizeRPM && Mathf.Abs(_clutchRpmDiff) > Tweak_SeizeRPM)
                            stiffness *= 0.5f;
                        _clutchRpmDiff = rpmDiff;
                        float clutching = diff * stiffness * clutchRatio;
                        driveTorque = clutching;
                        roadTorque = -(clutching * wheelRatio);
                        break;
                    }
                    case ClutchDisengaged:
                        _clutchRpmDiff = 0f;
                        break;
                }

                if (_clutchState == ClutchEngaged)
                {
                    // the engine and the wheels respond together: split the acceleration mismatch over both inertias
                    float response = 1f / engineInertia;
                    float diff = (roadTorque + totalEngineTorque) * response - transAcceleration;
                    float torqueSplit = 1f - _c.TorqueSplit;
                    if (FrontWheelDrive)
                    {
                        float g = Mathf.Abs(totalGearRatio);
                        response = (1f - torqueSplit) * g * g * 0.05f + response;
                    }
                    if (RearWheelDrive)
                    {
                        float g = Mathf.Abs(totalGearRatio);
                        response = g * g * torqueSplit * 0.05f + response;
                    }
                    float response1 = diff / response;
                    driveTorque = Mathf.Min(response1, 0f) + driveTorque;
                    roadTorque = roadTorque - response1 * wheelRatio;
                }

                if (_clutchState == ClutchEngaged && numOnGround > 0)
                {
                    float slip = DriveWheelSlippage();
                    float delta = _transmissionVelocity - _angularVelocity;
                    if (_throttle > 0.2f && gearDirection * slip > 0.1f && _gear <= Chassis.GFirst && delta < 0f)
                    {
                        // launch wheelspin: the driven wheels are pulled up to the engine
                        _transmissionVelocity = _angularVelocity;
                        SetDifferentialAngularVelocity(_angularVelocity / totalGearRatio);
                    }
                    else
                    {
                        float maxTorque = Mathf.Abs(totalEngineTorque);
                        roadTorque = Mathf.Clamp(delta * ClutchStiffness, -maxTorque, maxTorque) + roadTorque;
                    }
                }

                if (_gear == Chassis.GFirst || _gear == Chassis.GReverse)
                {
                    float torqueDiff = roadTorque + totalEngineTorque;
                    if (roadTorque < totalEngineTorque && torqueDiff < 0f)
                    {
                        float play = Chassis.Graph(ClutchPlayTable, torqueDiff * 1000f);
                        roadTorque = play * _c.ClutchSlip * torqueDiff + roadTorque;
                    }
                }
            }

            if (_gear != Chassis.GNeutral && _throttle > 0f && _gear <= Chassis.GFirst && _clutchState == ClutchEngaged && roadTorque * totalEngineTorque < 0f)
            {
                float powerRatio = 1f - Chassis.Ramp(rpm, _c.IdleRpm, _peakTorqueRpm) * _throttle;
                float clutchSlip = _c.ClutchSlip * _throttle * powerRatio;
                float allowed = 1f - clutchSlip;
                roadTorque = allowed * allowed * roadTorque;
            }

            _angularAcceleration = (roadTorque + totalEngineTorque) / engineInertia;
            _angularVelocity = Mathf.Clamp(_angularAcceleration * dT + _angularVelocity, minW, maxW);

            if (totalGearRatio != 0f) LimitFreeWheels(maxW / totalGearRatio);
            if (_transmissionVelocity > maxW && totalGearRatio != 0f)
            {
                // rev limiter on the driven wheels
                if (driveTorque * totalGearRatio > 0f) driveTorque = 0f;
                _transmissionVelocity = maxW;
                SetDifferentialAngularVelocity(maxW / totalGearRatio);
            }
            if (driveTorque > 0f && CatchupCheat.HasValue) driveTorque *= CatchupCheat.Value * Tweak_CheaterTorqueBoost + 1f;

            _driveTorque = Efficiency(_gear) * driveTorque * totalGearRatio;
            _rpm = SmoothRpm(IsShifting || _clutchState == ClutchDisengaged, _gear, dT, _rpm, _angularVelocity * RPS2RPM, engineInertia);
            _shiftPotential = _clutchState != ClutchEngaged || _owner.Staging
                ? PotentialNone
                : FindShiftPotential(_gear, _transmissionVelocity * RPS2RPM);
        }
    }
}
