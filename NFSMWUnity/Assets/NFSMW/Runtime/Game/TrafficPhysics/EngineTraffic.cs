using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// Physics/Behaviors/EngineTraffic.cpp (speed.exe Construct 0x6B6EB0): the traffic engine and automatic gearbox.
    /// The engine speed chases throttle × RED_LINE through a clutch tied to the driven wheels (no clutch slip model),
    /// torque from the TORQUE curve (ft-lb → N·m ×1.3558), engine braking from ENGINE_BRAKING, the TORQUE_CONVERTER
    /// multiplier below peak torque, a shift delay of SHIFT_SPEED × ratio (¼ for downshifts) during which no torque
    /// flows, and Physics::Info::ShiftPoints without induction. No nitrous, no boost. Gear ids are the sim's
    /// (0 reverse, 1 neutral, 2 first), which index GEAR_RATIO / GEAR_EFFICIENCY directly.
    /// </summary>
    public sealed class EngineTraffic
    {
        readonly CarSpec _c;
        readonly Chassis _susp;
        readonly float[] _up = new float[10], _down = new float[10];
        float _shiftTimer, _throttle, _w, _clutchW, _peakTorqueRpm;

        public int Gear { get; private set; } = Chassis.GFirst;
        public float DriveTorque { get; private set; }
        public float Rpm { get; private set; }
        public bool EngineBraking { get; private set; }
        public bool IsShifting => _shiftTimer > 0f;
        public bool IsReversing => Gear == Chassis.GReverse;
        int NumGears => _c.GearRatios.Length;
        public int TopGear => NumGears - 1;

        public EngineTraffic(CarSpec spec, Chassis suspension)
        {
            _c = spec;
            _susp = suspension;
            Reset();
        }

        float Ratio(int g) => g >= 0 && g < _c.GearRatios.Length ? _c.GearRatios[g] : 0f;
        float Efficiency(int g) => g >= 0 && g < _c.GearEfficiency.Length ? _c.GearEfficiency[g] : 1f;
        static float Rpm2Rps(float rpm) => rpm * 0.10471976f;
        static float Rps2Rpm(float w) => w * 9.549297f;
        /// <summary>GetTorquePoint (0x693410): Physics::Info::Torque × 1.3558.</summary>
        float TorqueNm(float rpm) => Dynamics.PhysicsInfo.Torque(_c, rpm) * Chassis.FTLB2NM;

        /// <summary>EngineTraffic::Reset.</summary>
        public void Reset()
        {
            DriveTorque = 0f;
            _w = Rpm2Rps(_c.IdleRpm);
            Rpm = _c.IdleRpm;
            _clutchW = 0f;
            _shiftTimer = 0f;
            Gear = Chassis.GFirst;
            _throttle = 0f;
            CalcShiftPoints();
        }

        /// <summary>
        /// EngineTraffic::CalcShiftPoints (0x693430): Physics::Info::ShiftPoints (0x673400, arrays of 10, the induction
        /// collection null; Dynamics.PhysicsInfo.ShiftPoints is the same boost-free search) and MaxInductedTorque (0x66C180:
        /// the largest TORQUE sample, its rpm clamped IDLE..RED_LINE) for the peak-torque rpm the torque converter ramps to
        /// (was a 50-rpm scan [?]). MaxInductedPower (mMaxHP) only feeds GetMaxHorsePower, which nothing in the port reads.
        /// </summary>
        void CalcShiftPoints()
        {
            if (!Dynamics.PhysicsInfo.ShiftPoints(_c, _up, _down))
            {
                System.Array.Clear(_up, 0, _up.Length);
                System.Array.Clear(_down, 0, _down.Length);
            }
            Dynamics.PhysicsInfo.MaxInductedTorque(_c, out _peakTorqueRpm);
        }

        /// <summary>EngineTraffic::MatchSpeed: the gear and engine speed for a car already rolling at `speed`.</summary>
        public void MatchSpeed(float speed)
        {
            float radius = (_c.TireRadiusFront + _c.TireRadiusRear) * 0.5f;
            if (radius <= 0f) return;
            float diffW = speed / radius, maxW = Rpm2Rps(_c.RedLine), minW = Rpm2Rps(_c.IdleRpm);
            if (speed == 0f)
            {
                _shiftTimer = 0f;
                Gear = Chassis.GFirst;
                _w = minW;
                _clutchW = _w;
                return;
            }
            if (speed < 0f)
            {
                _shiftTimer = 0f;
                Gear = Chassis.GReverse;
            }
            else
            {
                Gear = Chassis.GFirst;
                for (int g = Chassis.GFirst; g < NumGears; g++)
                {
                    float gr = Ratio(g) * _c.FinalGear;
                    if (gr > 0f && radius * maxW / gr < speed) Gear = g;
                }
            }
            float total = Ratio(Gear) * _c.FinalGear;
            _w = minW + diffW * total * ((maxW - minW) / maxW);
            _clutchW = _w;
            Rpm = Rps2Rpm(_w);
        }

        float BrakingTorque(float engineTorque, float rpm)
        {
            var eb = _c.EngineBraking;
            if (eb != null && eb.Length > 1) return -engineTorque * _c.EngineBrakingAt(rpm);
            return -engineTorque * (eb != null && eb.Length > 0 ? eb[0] : 0f);
        }

        /// <summary>EngineTraffic::Shift: only to a real gear; a downshift takes a quarter of the shift delay.</summary>
        public bool Shift(int gear)
        {
            if (gear < NumGears && gear != Gear && gear >= Chassis.GReverse)
            {
                float delay = _c.ShiftSpeed * Ratio(gear);
                _shiftTimer = gear < Gear ? delay * 0.25f : delay;
                Gear = gear;
                return true;
            }
            return false;
        }

        /// <summary>EngineTraffic::AutoShift.</summary>
        void AutoShift()
        {
            if (Gear == Chassis.GReverse || _shiftTimer > 0f) return;
            float rpm = Rps2Rpm(_w);
            if (Gear == Chassis.GNeutral) { Gear = Chassis.GFirst; return; }
            float upRpm = Gear < _up.Length ? _up[Gear] : _c.RedLine;
            float downRpm = Gear < _down.Length ? _down[Gear] : 0f;
            float lowerUp = 0f;
            float lowerRatio = Ratio(Gear - 1);
            if (Gear != Chassis.GFirst && lowerRatio > 0f)
            {
                lowerUp = _up[Gear - 1] * Ratio(Gear) / lowerRatio;
                lowerUp -= 200f;
            }
            if (Gear <= Chassis.GFirst) downRpm = _throttle < 0.2f ? _c.IdleRpm + 100f : 0f;
            if (rpm >= upRpm && Gear < TopGear) Shift(Gear + 1);
            else if (rpm <= downRpm && Gear > Chassis.GNeutral && rpm <= lowerUp) Shift(Gear - 1);
        }

        /// <summary>EngineTraffic::GetDifferentialAngularVelocity: the driven axle's mean wheel speed, never against the gear.</summary>
        float DifferentialW()
        {
            float into = 0f;
            bool rev = Gear == Chassis.GReverse;
            if (_c.TorqueSplit > 0f)
            {
                float w = (_susp.WheelAngularVelocity(0) + _susp.WheelAngularVelocity(1)) * 0.5f;
                into = rev ? Mathf.Min(w, 0f) : Mathf.Max(w, 0f);
            }
            if (_c.TorqueSplit < 1f)
            {
                float w = (_susp.WheelAngularVelocity(2) + _susp.WheelAngularVelocity(3)) * 0.5f;
                into = rev ? Mathf.Min(w, into) : Mathf.Max(w, into);
            }
            return into;
        }

        /// <summary>EngineTraffic::OnTaskSimulate (EngineTraffic.cpp:428-534; checked against the PC code 0x6A3480: idle offset
        /// min_w = IDLE in the clutch map, collision disengage up.y &lt; 0.3 (0x894B58), the neutral rev ×4·dT, the final
        /// clamp IDLE..RED_LINE and DriveTorque = efficiency·ratio·total·clutch are all as the source).</summary>
        public void Simulate(float dT, float gas, Vector3 up)
        {
            _throttle = gas;
            if (Gear > Chassis.GReverse) AutoShift();
            if (_shiftTimer > 0f) { _shiftTimer -= dT; if (_shiftTimer <= 0f) _shiftTimer = 0f; }
            float maxW = Rpm2Rps(_c.RedLine), minW = Rpm2Rps(_c.IdleRpm);
            float targetW = maxW * _throttle;
            float inertia = Dynamics.PhysicsInfo.EngineInertia(_c, true);   // Physics::Info::EngineInertia(loaded) 0x66BB00
            float diffW = DifferentialW();
            float clutch = _shiftTimer > 0f ? 0f : 1f;
            float total = 0f;
            float dir = Gear == Chassis.GReverse ? -1f : 1f;
            float ratio = Ratio(Gear) * _c.FinalGear * dir;
            if (_c.TorqueConverter > 0f)
            {
                float rpm = Rps2Rpm(_w);
                ratio *= 1f + _c.TorqueConverter * clutch * _throttle * (1f - Chassis.Ramp(rpm, _c.IdleRpm, _peakTorqueRpm));
            }
            bool disengage = up.y < 0.3f;
            EngineBraking = false;
            if (Gear == Chassis.GNeutral || disengage || ratio == 0f)
            {
                float rpm = Rps2Rpm(_w);
                float et = TorqueNm(rpm);
                float neutral = (et * _throttle + BrakingTorque(et, rpm) * (1f - _throttle)) / inertia;
                _w += neutral * 4f * dT;
                _w = neutral > 0f ? Mathf.Min(_w, targetW) : Mathf.Max(_w, targetW);
                DriveTorque = 0f;
                _clutchW = minW;
                clutch = 0f;
            }
            else
            {
                float rpm = Rps2Rpm(_w);
                float drive = TorqueNm(rpm);
                float braking = BrakingTorque(drive, rpm);
                if (diffW * ratio < 0f) braking = -braking;
                if (diffW == 0f) braking = 0f;
                float transW = diffW * ratio * ((maxW - minW) / maxW) + minW;
                _clutchW = transW;
                total += drive * _throttle + braking * (1f - _throttle);
                if (transW > maxW) total = braking * (transW / maxW - 1f) + braking * (1f - _throttle);
                if (transW > targetW)
                {
                    // PC 0x6A3732-0x6A375F as the GC source: Abs is called on the pushed 0 (not on d), so the step is
                    // min(0/inertia·dT, d) — the engine never rises toward the clutch here [C]
                    float d = transW - _w;
                    _w += Mathf.Min(0f, d);
                    EngineBraking = true;
                }
                else if (transW == targetW) total = 0f;
                else
                {
                    float dW = dT * total / inertia;
                    float delta = _w - transW;
                    if (delta > dW) _w -= dW * 4f;
                    else if (delta < -dW) _w += dW;
                    else _w = transW;
                    _w = Mathf.Min(_w, targetW);
                }
            }
            _w = Mathf.Clamp(_w, minW, maxW);
            DriveTorque = total * ratio * Efficiency(Gear) * clutch;
            Rpm = Rps2Rpm(_w);
        }
    }
}
