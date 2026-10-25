using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// SndAITrigger (EAXSound/EAXAIUtils.cpp; PC Update @0x4B1040): a running average of a value that starts triggering
    /// when it passes the auto-trigger level, or stays past the threshold for the sustain time, and stops once it has been
    /// back under the threshold for the trigger length. fSign flips the comparisons (right steering, deceleration).
    /// </summary>
    public sealed class SndAITrigger
    {
        public float Threshold, AutoTrigger, Sustain, TriggerLength, Sign = 1f;
        public readonly RunningAverage Avg;
        public bool Triggering { get; private set; }
        float _curSustain = -1f, _curLength = -1f;
        public float Value { get; private set; } = -1f;

        public SndAITrigger(float avgSize, float threshold, float force, float sustain, float length, float sign)
        {
            Avg = new RunningAverage((int)avgSize);
            Avg.Flush(0f);
            Threshold = threshold; AutoTrigger = force; Sustain = sustain; TriggerLength = length; Sign = sign;
            _curSustain = sustain;
        }

        public void Update(float v, float t)
        {
            Avg.Record(v);
            Avg.Recalculate();
            Value = Avg.Value;
            if (!Triggering)
            {
                if (Sign * Value > Sign * AutoTrigger) { Begin(); return; }
                if (Sign * Value > Sign * Threshold)
                {
                    _curSustain -= t;
                    if (_curSustain < 0f) Begin();
                    return;
                }
                _curSustain = Sustain;
                return;
            }
            if (Sign * Value > Sign * Threshold) _curLength = TriggerLength;
            else
            {
                _curLength -= t;
                if (_curLength < 0f) { Triggering = false; _curSustain = Sustain; }
            }
        }

        void Begin() { Triggering = true; _curLength = TriggerLength; }
    }

    /// <summary>
    /// SndAIStateManager (EAXAIUtils.cpp; PC Update @0x4C4380, UpdateState @0x4BA7B0): classifies an AI racer's driving
    /// from its steering (degrees: ±1 threshold, ±3 forced, average 15), speed change per audio frame (accel: average 3,
    /// threshold 0, forced 1 m/s, sustain 0.4 s; decel: -0.1 / -1, sustain 0.1 s) and throttle into PRERACE, IDLE (under
    /// 5 mph), ACCEL, DECEL and CORNER_LEFT/RIGHT (decelerating while steering), each state held at least
    /// MIN_StateSustainTime {1,1,1,1,0.5,0.5,0.5} s.
    /// </summary>
    public sealed class SndAIStateManager
    {
        public const int Unknown = 0, PreRace = 1, Idle = 2, Accel = 3, Decel = 4, CornerLeft = 5, CornerRight = 6;
        static readonly float[] MinSustain = { 1f, 1f, 1f, 1f, 0.5f, 0.5f, 0.5f };
        public readonly SndAITrigger SteerLeft = new SndAITrigger(15, 1f, 3f, 0.5f, 0.5f, 1f);
        public readonly SndAITrigger SteerRight = new SndAITrigger(15, -1f, -3f, 0.5f, 0.5f, -1f);
        public readonly SndAITrigger AccelMonitor = new SndAITrigger(3, 0f, 1f, 0.4f, 0.5f, 1f);
        public readonly SndAITrigger DecelMonitor = new SndAITrigger(3, -0.1f, -1f, 0.1f, 0.5f, -1f);
        public readonly SndAITrigger ThrottleMonitor = new SndAITrigger(2, 80f, 30f, 0.2f, 0.5f, -1f);
        public int State { get; private set; } = Unknown;
        public int Prev { get; private set; } = Unknown;
        float _tLastSwitch;

        /// <param name="steerDeg">steering angle in degrees (left positive) [?: the port reads the AI's steering input]</param>
        /// <param name="speedDelta">GetVelocityMagnitude() - |old velocity|, m/s per audio frame</param>
        public void Update(float steerDeg, float speedDelta, float throttle, float mph, float time, float t)
        {
            if (steerDeg > 180f) steerDeg -= 360f;
            SteerLeft.Update(steerDeg, t);
            SteerRight.Update(steerDeg, t);
            AccelMonitor.Update(speedDelta, t);
            DecelMonitor.Update(speedDelta, t);
            ThrottleMonitor.Update(throttle, t);
            UpdateState(mph, time);
        }

        void Switch(int s, float time) { _tLastSwitch = time; Prev = State; State = s; }

        void UpdateState(float mph, float time)
        {
            bool idle = mph < 5f, accel = AccelMonitor.Triggering, decel = DecelMonitor.Triggering;
            bool left = decel && SteerLeft.Triggering, right = decel && SteerRight.Triggering;
            bool held = _tLastSwitch > time - MinSustain[State];
            switch (State)
            {
                case Unknown: Switch(Accel, time); break;   // PossibleState[PRERACE] is never set
                case PreRace: Switch(Accel, time); break;
                case Idle: if (accel) Switch(Accel, time); break;
                case Accel:
                    if (held) break;
                    if (left) Switch(CornerLeft, time);
                    else if (right) Switch(CornerRight, time);
                    else if (decel) Switch(Decel, time);
                    else if (idle) Switch(Idle, time);
                    break;
                case Decel:
                    if (held) break;
                    if (left) Switch(CornerLeft, time);
                    else if (right) Switch(CornerRight, time);
                    else if (accel && !decel) Switch(Accel, time);
                    break;
                case CornerLeft:
                case CornerRight:
                    if (held) break;
                    if (accel) { if (!decel) Switch(Accel, time); else Switch(Decel, time); }
                    else if (decel) Switch(Decel, time);
                    break;
            }
        }
    }

    /// <summary>
    /// SFXCTL_AIPhysics (sfxctl/SFXCTL_Physics.cpp; PC UpdateParams @0x4C8B40): an AI racer's audio rpm is not the
    /// physics rpm but a model driven by <see cref="SndAIStateManager"/>:
    /// <list type="bullet">
    /// <item>GenDeltaRPM (@0x4B5040): 100/3 x FINE[gear] x COARSE x dt (normal 3.02 {40,40,40.5,21,12,6.5,3.6,2,1},
    /// x2.5 cruising; drift / short-track tables exist but MW has neither race flag set [?]); below 4th gear an LFO
    /// (15 rpm, 2/3 Hz) and x SteadyVelocityFactor = clamp(|accel avg| x 5.5556, 0.1, 1).</item>
    /// <item>UpdateAccel: accelerating = ACCEL state, or nitrous; held while shifting.</item>
    /// <item>UpdateRPM (@0x4BE120): accelerating → toward 9000 (accel avg under 1 and under 5 mph) else 10000; coasting
    /// under 3 mph → 1000 by 50, else 4000; while down-shifting 8000; up-shifting GetShiftDownRPM (3000 on PC) / max rpm
    /// x 10000 + 100 + a random 0..700 offset.</item>
    /// <item>UpdateGear (@0x4B5290, run twice): reverse → 1st; SuggestGear (@0x4B51E0) = clamp((int)(speed m/s x 0.0976),
    /// 1st, top gear) (theoretical top speed is 100 on PC); over 9300 rpm shift up to clamp(suggested, 2nd, 6th), under the
    /// per-gear down-shift rpm {6000,6000,5700,6200,6650,7000,7300,7400,7500} shift down (never 1st above 10 mph).
    /// LastGear is set one off so SFXCTL_Shifting sees the change.</item>
    /// </list>
    /// </summary>
    public sealed class AiPhysicsModel
    {
        static readonly float[] Fine = { 40f, 40f, 40.5f, 21f, 12f, 6.5f, 3.6f, 2f, 1f };
        static readonly float[] DownShiftRpm = { 6000f, 6000f, 5700f, 6200f, 6650f, 7000f, 7300f, 7400f, 7500f };
        public readonly SndAIStateManager States = new SndAIStateManager();
        public float Rpm = 1000f, Trq, DeltaRpm, Steady, TargetOffset;
        public int Gear = 2, LastGear = 2;
        public bool Accelerating;
        float _lfoOffset, _prevSpeed;
        ushort _lfoAngle;

        public void Step(PlayerCar car, CarSoundModel m, float time, float t)
        {
            float speed = car.SpeedMps, mph = speed * 2.23699f;
            // EAXAIUtils.cpp:175: GetSteering() (a bAngle) x 360/65536, wrapped above 180. SoundCar.cpp:120 feeds it
            // bRadToAng(fSteering): the -1..1 steering input read as radians
            ushort ang = (ushort)(int)(Mathf.Clamp(car.Input.Steer, -1f, 1f) * 10430.378f);
            float steer = ang * 0.005493248f;
            if (steer > 180f) steer -= 360f;
            States.Update(steer, speed - _prevSpeed, Accelerating ? 100f : 0f, mph, time, t);
            _prevSpeed = speed;
            DeltaRpm = GenDeltaRpm(t);
            // UpdateAccel
            Steady = Mathf.Clamp(Mathf.Abs(States.AccelMonitor.Avg.Value) * 5.5555553f, 0.1f, 1f);
            if (m.ShiftState == 0) Accelerating = car.NosFiring || States.State == SndAIStateManager.Accel;
            // UpdateRPM
            if (m.ShiftState == 0)
            {
                if (Accelerating)
                    Rpm = States.AccelMonitor.Avg.Value < 1f && mph < 5f ? SndCurves.Smooth(Rpm, 9000f, DeltaRpm) : SndCurves.Smooth(Rpm, 10000f, DeltaRpm);
                else Rpm = mph < 3f ? SndCurves.Smooth(Rpm, 1000f, 50f) : SndCurves.Smooth(Rpm, 4000f, DeltaRpm);
            }
            else if (m.ShiftState >= 4) Rpm = 8000f;
            else
            {
                float max = car.Spec != null ? car.Spec.MaxRpm : 0f;
                if (max != 0f) Rpm = 3000f / max * 10000f + 100f + TargetOffset;
            }
            // UpdateTorque
            Trq = SndCurves.Smooth(Trq, Accelerating ? 100f : 0f, 50f);
            LastGear = Gear;
            UpdateGear(car, mph);
            LastGear = Gear;
            UpdateGear(car, mph);
        }

        float GenDeltaRpm(float dt)
        {
            float scale = Fine[Mathf.Clamp(Gear, 0, 8)] * 3.02f;
            if (!Accelerating) scale *= 2.5f;
            if (Gear <= 4)
            {
                _lfoAngle = (ushort)((_lfoAngle + (int)(dt * 43689.996f)) % 0xFFFF);
                _lfoOffset = Mathf.Sin(_lfoAngle / 65536f * Mathf.PI * 2f) * 15f;
                scale *= Steady;
            }
            return 100f / 3f * scale * dt;
        }

        static int TopGear(PlayerCar car) => car.Spec != null ? car.Spec.ForwardGears + 1 : 7;

        int Suggest(PlayerCar car) => Mathf.Min(Mathf.Max((int)(car.SpeedMps * 0.01f * 9.76f), 2), TopGear(car));

        void UpdateGear(PlayerCar car, float mph)
        {
            if (car.Gear < 0) { Gear = 2; return; }
            int sugg = Suggest(car);
            int diff = Gear - sugg;
            if (diff >= 2 || diff <= -2) { Gear = sugg; LastGear = Gear; }
            if (Rpm > 9300f)
            {
                Gear = Mathf.Clamp(Suggest(car), 3, 7);
                TargetOffset = Random.Range(0f, 700f);
                LastGear = Gear - 1;
                return;
            }
            if (Rpm >= DownShiftRpm[Mathf.Clamp(Gear, 0, 8)] || Gear <= 2) return;
            Gear = Suggest(car);
            if (Gear == 2 && mph > 10f) Gear = 3;
            LastGear = Gear + 1;
        }
    }
}
