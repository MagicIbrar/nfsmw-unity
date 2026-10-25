using System;
using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NfsMw.Controls
{
    /// <summary>sEnvelopePoint / tEnvelope (Misc/Rumble.cpp): a piecewise-linear envelope over time; 0 outside [first, last) (GetValue).</summary>
    public sealed class Envelope
    {
        public readonly (float t, float v)[] Points;
        public readonly string Name;
        public Envelope(string name, params (float t, float v)[] points) { Name = name; Points = points; }
        public float Duration => Points[Points.Length - 1].t;
        public bool OutOfRange(float t) => t < Points[0].t || t >= Points[Points.Length - 1].t;

        int GetIndex(float t)
        {
            int i = 0;
            for (; i < Points.Length; i++) if (t < Points[i].t) break;
            return i > 0 ? i - 1 : i;
        }

        float GetSlope(int i)
        {
            if (i < 0 || i > Points.Length - 2) return 0f;
            return (Points[i + 1].v - Points[i].v) / (Points[i + 1].t - Points[i].t);
        }

        public float GetValue(float t)
        {
            if (OutOfRange(t)) return 0f;
            int i = GetIndex(t);
            return Points[i].v + GetSlope(i) * (t - Points[i].t);
        }
    }

    /// <summary>
    /// The rumble patterns of Rumble.cpp (values 0..255 are the pad motor strength) and the intensity tables
    /// {in min, in max, amplitude min, amplitude max} that scale them (fXIntensityCoeffs). ApplyCameraShake / MaybeCameraShake /
    /// ForceCameraShake (PC 0x64C420 / 0x64C5F0 / tShaker) are the camera shaker half of the same file.
    /// </summary>
    public static class RumblePatterns
    {
        public static readonly Envelope SmallRumble = new Envelope("SmallRumble", (0f, 255f), (0.05f, 200f), (0.07f, 80f), (0.8f, 0f));
        public static readonly Envelope BigRumble = new Envelope("BigRumble", (0f, 255f), (0.15f, 255f), (0.2f, 120f), (1.2f, 0f));
        public static readonly Envelope ShockSway = new Envelope("ShockSway", (0f, 55f), (0.05f, 15f), (0.1f, 0f));
        public static readonly Envelope RoadNoise0 = new Envelope("RoadNoise0", (0f, 80f), (0.05f, 15f), (0.1f, 0f));
        public static readonly Envelope RoadNoise1 = new Envelope("RoadNoise1", (0f, 80f), (0.05f, 15f), (0.1f, 0f));
        public static readonly Envelope Binary = new Envelope("Binary", (0f, 255f), (0.15f, 255f));
        public static readonly Envelope SingleRoadBump = new Envelope("SingleRoadBump", (0f, 140f), (0.07f, 0f));
        public static readonly Envelope DoubleRoadBump = new Envelope("DoubleRoadBump", (0f, 255f), (0.05f, 0f), (0.07f, 255f), (0.15f, 0f));
        public static readonly Envelope ShakeAmplitude = new Envelope("ShakeAmplitude", (0f, 1f), (0.1f, 0.7f), (0.3f, 0.5f), (0.5f, 0.4f), (1f, 0f));
        public static readonly Envelope GearGrind = new Envelope("GearGrind", (0f, 255f), (0.075f, 20f), (0.2f, 0f));
        public static readonly Envelope EngineHeat = new Envelope("EngineHeat", (0f, 45f), (0.25f, 35f));
        public static readonly Envelope EngineRev = new Envelope("EngineRev", (0f, 45f), (0.01f, 1f));
        public static readonly Envelope NOS = new Envelope("NOS", (0f, 70f), (1f, 1f));
        public static readonly Envelope Drift = new Envelope("Drift", (0f, 45f), (0.01f, 0f));
        public static readonly Envelope Burnout = new Envelope("Burnout", (0f, 55f), (0.01f, 15f), (0.05f, 0f));

        // fXIntensityCoeffs: {input low, input high, amplitude at low, amplitude at high} (as fShakeIntensityCoeffs is used
        // by MaybeCameraShake: ratio = clamp((x − lo) / (hi − lo)), amplitude = lerp) [?: the 4-entry tables' use is stripped]
        public static readonly float[] ShockSwayCoeffs = { 9f, 40f, 0.95f, 1.5f };
        public static readonly float[] DriftCoeffs = { 5f, 15f, 2f, 2.5f };
        public static readonly float[] RoadNoiseCoeffs = { 0.001f, 0.025f, 1.1f, 1.5f, 25f, 85f };
        public static readonly float[] ShockBottomCoeffs = { 1f, 3f, 1f, 2f };
        public static readonly float[] ImpactCoeffs = { 2f, 11f, 0.3f, 2f };
        public static readonly float[] BlownTiresCoeffs = { 2f, 11f, 0.3f, 2f };
        public static readonly float[] RoadBumpCoeffs = { 0f, 1f, 0.3f, 2f };
        public static readonly float[] GearGrindCoeffs = { 0f, 1f, 0.3f, 3f };
        public static readonly float[] EngineHeatCoeffs = { 0f, 1f, 1.5f, 3f };
        public static readonly float[] EngineRevCoeffs = { 0.3f, 0.9f, 1.2f, 2.75f };
        public static readonly float[] NOSCoeffs = { 0f, 1f, 1f, 1.9f };
        public static readonly float[] BurnoutCoeffs = { 1f, 10f, 1.15f, 1.9f };
        /// <summary>fShakeIntensityCoeffs: |accel·bias| 100..600 → magnitude 0.1..0.4, duration 0.3..0.7 s.</summary>
        public static readonly float[] ShakeIntensityCoeffs = { 100f, 600f, 0.1f, 0.4f, 0.3f, 0.7f };
        public const float ShakeDuration = 0.6f, ShakeFrequency = 11.74265f;
        public static readonly Vector3 ShakeRotation = new Vector3(-1200f, 2000f, 0f), ShakeAccelBias = new Vector3(0.7f, 0.7f, 1f);

        /// <summary>The intensity mapping: below the input low → no pattern (−1); else the amplitude between the two amplitude entries.</summary>
        public static float Amplitude(float[] coeffs, float x)
        {
            if (x < coeffs[0]) return -1f;
            float r = coeffs[1] > coeffs[0] ? Mathf.Clamp01((x - coeffs[0]) / (coeffs[1] - coeffs[0])) : 1f;
            return coeffs[2] + r * (coeffs[3] - coeffs[2]);
        }
    }

    /// <summary>
    /// tRumbler (Rumble.cpp, 0x30 bytes: 3 envelope slots with time, amplitude and priority, the current and previous value;
    /// StartRumble / Update / GetEnvelopeValue are stripped on GC and absent on PC — [?] reconstructed: a new rumble takes a
    /// free slot, else the slot of lowest priority not above its own; the value is the largest amplitude·envelope(t) of the
    /// running slots, clamped to 0..255; a slot ends when its envelope runs out).
    /// </summary>
    public sealed class Rumbler
    {
        public const int MaxEnvelopes = 3;
        readonly Envelope[] _env = new Envelope[MaxEnvelopes];
        readonly float[] _time = new float[MaxEnvelopes], _amp = new float[MaxEnvelopes];
        readonly sbyte[] _prio = new sbyte[MaxEnvelopes];
        public int Value { get; private set; }
        public int OldValue { get; private set; }
        public bool NewValue => Value != OldValue;
        public bool IsRumbling => Value > 0;

        public bool StartRumble(Envelope e, float amplitude, sbyte priority)
        {
            if (e == null || amplitude <= 0f) return false;
            int slot = -1;
            for (int i = 0; i < MaxEnvelopes; i++) if (_env[i] == null) { slot = i; break; }
            if (slot < 0)
            {
                int low = 0;
                for (int i = 1; i < MaxEnvelopes; i++) if (_prio[i] < _prio[low]) low = i;
                if (_prio[low] > priority) return false;
                slot = low;
            }
            _env[slot] = e; _time[slot] = 0f; _amp[slot] = amplitude; _prio[slot] = priority;
            return true;
        }

        /// <summary>Is this envelope already running (so a continuous source does not restart it every frame)?</summary>
        public bool IsRunning(Envelope e) { for (int i = 0; i < MaxEnvelopes; i++) if (_env[i] == e) return true; return false; }

        public bool Update(float dt)
        {
            OldValue = Value;
            float v = 0f;
            for (int i = 0; i < MaxEnvelopes; i++)
            {
                if (_env[i] == null) continue;
                _time[i] += dt;
                if (_env[i].OutOfRange(_time[i])) { _env[i] = null; continue; }
                v = Mathf.Max(v, _env[i].GetValue(_time[i]) * _amp[i]);
            }
            Value = Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);
            return NewValue;
        }

        public void Stop() { for (int i = 0; i < MaxEnvelopes; i++) _env[i] = null; Value = 0; }
    }

    /// <summary>
    /// tShaker (Rumble.cpp): a camera shake vector with the ShakeAmplitude envelope over a duration, flipped at fFrequency
    /// (GetValue: the amplitude only on the odd half-cycles of t·f). CameraShakers[2] (one per player), UpdateCameraShakers
    /// (0x64C2D0, Camera::UpdateAll), ResetCameraShakers (0x64C370, World_Init and ERestartRace), MaybeCameraShake (0x64C5F0,
    /// CDActionDrive::Update with the car's acceleration), ApplyCameraShake (0x64C420, the camera movers: a translation and a
    /// rotation of the view matrix from the shake vector) and ForceCameraShake (fixed 0.6 s, 11.74 Hz).
    /// </summary>
    public sealed class CameraShaker
    {
        Vector3 _shake;
        Envelope _amplitude;
        float _time, _duration = 1f, _frequency = RumblePatterns.ShakeFrequency;
        public static readonly CameraShaker[] Shakers = { new CameraShaker(), new CameraShaker() };

        public void Reset() { _shake = Vector3.zero; _amplitude = null; _time = 0f; _duration = 1f; _frequency = RumblePatterns.ShakeFrequency; }
        public float GetAmplitude() => _amplitude != null ? _amplitude.GetValue(_time) : 0f;
        public bool IsShaking => _amplitude != null;

        public void StartShaking(Vector3 v, float duration, float frequency)
        {
            _shake = -v;
            _amplitude = RumblePatterns.ShakeAmplitude;
            _time = 0f;
            _duration = duration;
            _frequency = frequency;
        }

        public void Update(float dt)
        {
            if (_amplitude == null) return;
            _time += dt / _duration;
            if (_amplitude.OutOfRange(_time)) _amplitude = null;
        }

        /// <summary>tShaker::GetValue: the shake vector × amplitude on the odd half-cycles ((int)(t·f) ^ 1) &amp; 1.</summary>
        public Vector3 GetValue()
        {
            int n = (int)(_time * _frequency);
            bool on = ((n ^ 1) & 1) != 0;
            float a = on && _amplitude != null ? _amplitude.GetValue(_time) : 0f;
            return _shake * a;
        }

        public static void UpdateCameraShakers(float dt) { foreach (var s in Shakers) s.Update(dt); }
        public static void ResetCameraShakers() { foreach (var s in Shakers) s.Reset(); }
        public static Vector3 GetShake(int player) => Shakers[Mathf.Clamp(player, 0, 1)].GetValue();
        public static void ForceCameraShake(int player, Vector3 shake) => Shakers[Mathf.Clamp(player, 0, 1)].StartShaking(shake, RumblePatterns.ShakeDuration, RumblePatterns.ShakeFrequency);

        /// <summary>MaybeCameraShake(player, accel): |bias·accel| in 100..600 → magnitude 0.1..0.4 and duration 0.3..0.7 s along the (biased) acceleration, if stronger than the current shake.</summary>
        public static void MaybeCameraShake(int player, Vector3 accel)
        {
            var c = RumblePatterns.ShakeIntensityCoeffs;
            var a = Vector3.Scale(RumblePatterns.ShakeAccelBias, accel);
            float ratio = a.magnitude;
            if (ratio < c[0]) return;
            if (ratio > c[1]) ratio = c[1];
            ratio = (ratio - c[0]) / (c[1] - c[0]);
            float magnitude = ratio * (c[3] - c[2]) + c[2];
            var s = Shakers[Mathf.Clamp(player, 0, 1)];
            if (magnitude > s.GetAmplitude())
            {
                float duration = ratio * (c[5] - c[4]) + c[4];
                s.StartShaking(a.normalized * magnitude, duration, RumplePatternsFrequency);
            }
        }

        const float RumplePatternsFrequency = RumblePatterns.ShakeFrequency;

        /// <summary>
        /// ApplyCameraShake(viewID, matrix): nothing below full sim speed (Sim::GetSpeed &lt; 1: the speedbreaker); view 1 / 3 →
        /// player 0, view 2 → player 1. The shake vector (in the camera's frame) becomes a translation, then rotations
        /// X by −1200·(z + y), Y by 2000·x, Z by 0 (bAngle units: 65536 per turn) multiplied into the view matrix.
        /// Returns the local offset and the Euler rotation (degrees) for the camera code to apply [hook for ChaseCamera].
        /// </summary>
        public static bool ApplyCameraShake(int viewId, out Vector3 offset, out Vector3 eulerDeg)
        {
            offset = Vector3.zero; eulerDeg = Vector3.zero;
            if (PlayerCar.WorldTimeScale < 1f) return false;
            int player;
            switch (viewId) { case 1: case 3: player = 0; break; case 2: player = 1; break; default: return false; }
            var v = GetShake(player);
            if (v == Vector3.zero) return false;
            offset = v;
            const float turn = 360f / 65536f;
            eulerDeg = new Vector3(RumblePatterns.ShakeRotation.x * (v.z + v.y) * turn, RumblePatterns.ShakeRotation.y * v.x * turn, RumblePatterns.ShakeRotation.z * (v.x + v.y) * turn);
            return true;
        }
    }

    /// <summary>
    /// The pad rumble of the local player (LocalPlayer::DoFFB 0x6EE490 feeds the IFeedback device from the vehicle each frame
    /// — engine rpm ratio, wheel slip / load, impacts, NOS, the handbrake; the device's own force maths is the DirectInput
    /// wheel driver's and is not reproduced [?]). Here the Rumble.cpp patterns are started from the car's state and sent to
    /// UnityEngine.InputSystem.Gamepad.SetMotorSpeeds (value / 255 on both motors [ours]) while the player's FE Rumble
    /// option is on (FEPlayerSettings.Rumble) and the game is not paused; the camera shakers are updated here too
    /// (Camera::UpdateAll) and MaybeCameraShake runs on the player's acceleration (CDActionDrive::Update).
    /// </summary>
    public sealed class RumbleDriver : MonoBehaviour
    {
        public static RumbleDriver Instance { get; private set; }
        public readonly Rumbler Player1 = new Rumbler(), Player2 = new Rumbler();
        Vector3 _lastVel, _lastVel2;
        bool _hadVel, _hadVel2;
        int _lastGear, _lastShiftCount;
        bool _wasGrounded = true;
        float _bumpTimer;
        bool _motorsOn;
        public bool Enabled = true;

        void Awake() => Instance = this;

        public static RumbleDriver Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("NFSMW Rumble") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            return go.AddComponent<RumbleDriver>();
        }

        void Update()
        {
            float dt = Time.deltaTime;
            CameraShaker.UpdateCameraShakers(dt);
            var boot = GameBootstrap.Instance;
            var car = boot ? boot.Player : null;
            bool paused = Time.timeScale <= 0f || GameFlow.SimulationPaused;
            bool option = UI.FEDb.Data?.Player1?.Rumble ?? true;
            if (car && !paused && Enabled)
            {
                Feed(car, Player1, dt, ref _lastVel, ref _hadVel, 0);
                if (boot.Player2) Feed(boot.Player2, Player2, dt, ref _lastVel2, ref _hadVel2, 1);
            }
            else { Player1.Stop(); Player2.Stop(); }
            Player1.Update(dt);
            Player2.Update(dt);
            if (car) ForceFeedback.DoFFB(car);   // LocalPlayer::OnTask → DoFFB (no device on this port: returns at once)
            var pad = Gamepad.current;
            if (pad == null) return;
            float v = option && !paused ? Player1.Value / 255f : 0f;
            if (v > 0f || _motorsOn)
            {
                try { pad.SetMotorSpeeds(v, v); } catch (Exception) { }
                _motorsOn = v > 0f;
            }
        }

        void OnDisable() { try { Gamepad.current?.SetMotorSpeeds(0f, 0f); } catch (Exception) { } }

        /// <summary>The car's state → patterns (DoFFB's inputs; the thresholds are the Rumble.cpp intensity tables).</summary>
        void Feed(PlayerCar car, Rumbler r, float dt, ref Vector3 lastVel, ref bool hadVel, int player)
        {
            var rb = car.GetComponent<Rigidbody>();
            var vel = rb ? rb.linearVelocity : Vector3.zero;
            if (hadVel && dt > 0f)
            {
                var dv = vel - lastVel;
                var accel = dv / dt;
                // CDActionDrive::Update → MaybeCameraShake(player, acceleration in the car's frame)
                CameraShaker.MaybeCameraShake(player, car.transform.InverseTransformDirection(accel));
                // an impact: the speed lost in one frame (m/s) [?: RBSmackable's impact intensity]
                float hit = Vector3.Dot(dv, -vel.normalized);
                if (vel.sqrMagnitude < 0.01f) hit = dv.magnitude;
                float a = RumblePatterns.Amplitude(RumblePatterns.ImpactCoeffs, hit);
                if (a > 0f) r.StartRumble(hit > 6f ? RumblePatterns.BigRumble : RumblePatterns.SmallRumble, a, 3);
                // landing after a jump: the shocks bottom out (vertical speed lost)
                if (car.Grounded && !_wasGrounded)
                {
                    float bottom = RumblePatterns.Amplitude(RumblePatterns.ShockBottomCoeffs, Mathf.Abs(dv.y));
                    if (bottom > 0f) r.StartRumble(RumblePatterns.ShockSway, bottom, 2);
                }
            }
            _wasGrounded = car.Grounded;
            lastVel = vel; hadVel = true;

            float speed = car.SpeedMps;
            // NOS: the envelope while the nitrous fires, scaled by the bottle level
            if (car.NosFiring && !r.IsRunning(RumblePatterns.NOS)) r.StartRumble(RumblePatterns.NOS, RumblePatterns.Amplitude(RumblePatterns.NOSCoeffs, Mathf.Clamp01(car.Nos)), 1);
            // gear grind: a missed shift (EngineRacer's shift status)
            if (car.ShiftCount != _lastShiftCount)
            {
                _lastShiftCount = car.ShiftCount;
                if (car.LastShift == PlayerCar.ShiftStatus.Missed) r.StartRumble(RumblePatterns.GearGrind, RumblePatterns.Amplitude(RumblePatterns.GearGrindCoeffs, 1f), 2);
            }
            _lastGear = car.Gear;
            // engine rev at the limiter (over-rev) and engine heat (the sabotaged / overheating engine)
            float rpmRatio = car.Spec != null && car.Spec.RedLine > 0 ? car.Rpm / car.Spec.RedLine : 0f;
            if (car.OverRev > 0f && rpmRatio > 0.9f && !r.IsRunning(RumblePatterns.EngineRev)) r.StartRumble(RumblePatterns.EngineRev, RumblePatterns.Amplitude(RumblePatterns.EngineRevCoeffs, Mathf.Clamp01(rpmRatio)), 1);
            if (car.EngineHeat > 0.5f && !r.IsRunning(RumblePatterns.EngineHeat)) r.StartRumble(RumblePatterns.EngineHeat, RumblePatterns.Amplitude(RumblePatterns.EngineHeatCoeffs, Mathf.Clamp01(car.EngineHeat)), 1);
            // drift (the drift state's slip angle, degrees) and burnout (wheel spin at low speed)
            float alpha = 0f, slip = 0f;
            bool anyContact = false;
            for (int i = 0; i < 4; i++)
            {
                var w = car.WheelAudio(i);
                if (!w.contact) continue;
                anyContact = true;
                alpha = Mathf.Max(alpha, Mathf.Abs(w.alphaDeg));
                slip = Mathf.Max(slip, Mathf.Abs(w.slip));
            }
            if (anyContact && car.Drift > 0f) { float d = RumblePatterns.Amplitude(RumblePatterns.DriftCoeffs, alpha); if (d > 0f && !r.IsRunning(RumblePatterns.Drift)) r.StartRumble(RumblePatterns.Drift, d, 1); }
            if (anyContact && speed < 8f) { float b = RumblePatterns.Amplitude(RumblePatterns.BurnoutCoeffs, slip); if (b > 0f && !r.IsRunning(RumblePatterns.Burnout)) r.StartRumble(RumblePatterns.Burnout, b, 1); }
            // blown tires: a bump per wheel turn (the flat's thump)
            if (car.BlownTires > 0 && speed > 2f)
            {
                _bumpTimer -= dt;
                if (_bumpTimer <= 0f)
                {
                    _bumpTimer = Mathf.Clamp(2f * Mathf.PI * 0.33f / speed, 0.08f, 1f);
                    r.StartRumble(RumblePatterns.SingleRoadBump, RumblePatterns.Amplitude(RumblePatterns.BlownTiresCoeffs, Mathf.Clamp(speed * 0.2f, 2f, 11f)), 2);
                }
            }
        }
    }

    /// <summary>
    /// The PC force-feedback device a LocalPlayer feeds (LocalPlayer +0x58, an IFeedback with ~14 virtuals; the DirectInput
    /// steering-wheel driver behind DAT_0092C678). The slot order is DoFFB's; the names are [?] from the arguments.
    /// </summary>
    public interface IFfbDevice
    {
        /// <summary>vtable +4: stop / pause all effects (CanDoFFB false, PauseFFB 0x6E8B20).</summary>
        void Pause();
        /// <summary>+8 then +0x10: a new frame of channel values begins.</summary>
        void BeginFrame();
        /// <summary>+0x20(front, surface, ITransmission): the wheel's surface for its axle.</summary>
        void SetSurface(bool front, uint surface);
        /// <summary>+0x1C(front, {|slip|, |skid|}, ISuspension).</summary>
        void SetWheel(bool front, float slip, float skid);
        /// <summary>+0x18(ITransmission, surface, vehicle speed).</summary>
        void SetRoad(uint surface, float speed);
        /// <summary>+0x24(rpm ratio, 0, the engine's info +0x14).</summary>
        void SetEngine(float rpmRatio);
        /// <summary>+0x28(ITransmission +0x2C): the gear.</summary>
        void SetGear(int gear);
        /// <summary>+0x2C(IEngine +0x20(IEngine +0x1C)): only when the engine has nitrous (IEngine +0x2C).</summary>
        void SetNos(float nos);
        /// <summary>+0x30(blown or sabotaged): only for a car with IEngineDamage.</summary>
        void SetEngineDamaged(bool damaged);
        /// <summary>+0x34(IVehicle +0x10 and speed &gt; 0.1): a car without IEngineDamage.</summary>
        void SetMoving(bool moving);
        /// <summary>+0x14: send the frame.</summary>
        void Commit();
    }

    /// <summary>
    /// LocalPlayer::CanDoFFB (0x6EE400) / DoFFB (0x6EE490, from LocalPlayer::OnTask 0x6F22C6). Unity's Input System has no
    /// force-feedback wheel API (no DirectInput effects), so <see cref="Device"/> stays null on this port and DoFFB returns at
    /// its first test (LocalPlayer +0x58 == null) exactly as the exe does without a wheel — the pad rumble is RumbleDriver's.
    /// A platform plug-in can set Device to drive a wheel with the exe's channels.
    /// </summary>
    public static class ForceFeedback
    {
        /// <summary>The device (LocalPlayer +0x58); null: no FFB wheel.</summary>
        public static IFfbDevice Device;
        /// <summary>DAT_008F86E0: force feedback enabled (1 by default).</summary>
        public static bool Enabled = true;

        /// <summary>
        /// CanDoFFB: the enabled flag, a device (0x92C678), no NIS running (the INIS of the device's list, vtable +8), the
        /// player's simable present and not "animating" (its IAnimatable +0xC), and IVehicle +0xBC (staged / on the grid) false.
        /// </summary>
        public static bool CanDoFFB(PlayerCar car) => Enabled && Device != null && !NisPlayer.Active && car && !car.AiControlled;

        /// <summary>
        /// DoFFB: without a device nothing; when it cannot run the device pauses; else per wheel i (ISuspension count): the
        /// surface — airborne (ISuspension +0x24 false) kNull (0x988E80), a punctured tyre (ISpikeable +4(i)) the 0xD929E923
        /// surface, else the wheel's (+0x44) — |slip| (+0x30) and |skid| (+0x38) with front = i &lt; 2 into +0x20 / +0x1C, and +0x18
        /// with the speed (IVehicle +0x98); then the rpm ratio clamp((rpm − idle) / (redline − idle)) (0 when the range is not
        /// positive), the gear, the nitrous when fitted, and the engine damage (IEngineDamage: blown +4 or sabotaged +0x10) or,
        /// without one, "moving" (IVehicle +0x10 and speed &gt; 0.1); Commit.
        /// </summary>
        public static void DoFFB(PlayerCar car)
        {
            var dev = Device;
            if (dev == null) return;
            if (!CanDoFFB(car)) { dev.Pause(); return; }
            dev.BeginFrame();
            for (int i = 0; i < 4; i++)
            {
                if (!car.WheelState(i, out bool contact, out float slip, out _, out uint surface)) continue;
                var w = car.WheelAudio(i);
                bool front = i < 2;
                uint s = !contact ? 0u : car.BlownTires > 0 && (car.BlownTires & (1 << i)) != 0 ? 0xD929E923u : surface;
                dev.SetSurface(front, s);
                dev.SetWheel(front, Mathf.Abs(slip), Mathf.Abs(w.alphaDeg));
                dev.SetRoad(s, car.SpeedMps);
            }
            var spec = car.Spec;
            float ratio = 0f;
            if (spec != null && spec.RedLine - spec.IdleRpm > 0f) ratio = Mathf.Clamp01((car.Rpm - spec.IdleRpm) / (spec.RedLine - spec.IdleRpm));
            dev.SetEngine(ratio);
            dev.SetGear(car.Gear);
            dev.SetNos(car.Nos);
            dev.SetEngineDamaged(car.EngineBlown || car.Sabotaged);
            dev.Commit();
        }
    }
}
