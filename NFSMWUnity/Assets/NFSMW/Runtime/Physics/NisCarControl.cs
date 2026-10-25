using System;
using NfsMw.Core;
using NfsMw.Dynamics;
using NfsMw.Game.TrafficPhysics;
using Unity.Collections;
using UnityEngine;
using System.Collections.Generic;

namespace NfsMw.Game
{
    /// <summary>
    /// The NIS car controller: SuspensionSpline (Physics/Behaviors/SuspensionSpline.cpp; speed.exe ctor 0x6B30C0,
    /// OnTaskSimulate 0x6B34A0, DoWheelForces 0x6AE710, DoSteering 0x691860, NISCarTweaks 0x690EA0, SetNISPosition
    /// 0x691110, MatchSpeed 0x691610, Reset 0x6916F0) and EngineSpline (EngineSpline.cpp; ctor 0x6B3EF0, OnTaskSimulate
    /// 0x6A1AC0, AutoShift 0x6A1740) with the CAnimScene side (AnimScene.cpp:666-700 AnimatedCars_SetMainAndWheels).
    /// <para>
    /// While a vehicle animates, PVehicle::LookupBehaviorSignature (0x67F0D0) swaps its SUSPENSION for "SuspensionSpline",
    /// ENGINE for "EngineSpline" and INPUT for "InputNIS" (not for CHOPPER), RESET for none. The car stays a physical body:
    /// each step SetNISPosition pins x/z, the x/z velocity and the yaw (rate) to the animation track and keeps pitch/roll
    /// within ±mConstraint/2 (60° → ±30°); the vertical, pitch and roll come from gravity and the spring/damper/sway forces
    /// of DoWheelForces (only the body-up part of the force and the pitch/roll part of the torque are applied, so the
    /// body squats, dives and leans with the track's accelerations). The track's own height is ignored (CAnimScene puts
    /// it 5 m over GetWorldHeightAtPointRigorous); the first call (and the first after ENISReattach, 0x447770 clears
    /// HaveLastCarPosition) places the car on the ground (PVehicle::SetVehicleOnGround 0x685810), zero speed,
    /// RestoreState. EngineSpline only turns the wheel speed into rpm/gear for the audio (no torque).
    /// </para>
    /// <para>
    /// INISCarControl / INISCarEngine come from the NIS events (event ctors, speed.exe): ENISBurnout 0x639190 SetBurnout,
    /// ENISBrakelock 0x638FB0 SetBrakeLock, ENISSteering 0x63A3F0 SetSteering(deg/360 turns, weight), ENISCarPitch
    /// 0x6394D0 / ENISCarRoll / ENISCarShake 0x639890 SetAnim*, ENISNeutralRev 0x639F20 SetNeutralRev, ENISNitro 0x63A140
    /// SetNitro, ENISAeroDynamics 0x62DA50 gNIS_AeroDynamics, ENISCarDamageReset 0x639350 IDamageable::ResetDamage;
    /// ENISDetach dtor 0x639C70 = IVehicle::SetAnimating(false) (fSpeed is not read on PC), ENISReattach dtor 0x63A320 =
    /// SetAnimating(true) + reset the last pose (+ ResetDamage when fRepairDamage). Each one only reaches a car that
    /// animates (QueryInterface INISCarControl fails on the normal behaviours).
    /// </para>
    /// <para>
    /// The PlayerCar frame [ours]: its origin is on the road at rest (PlayerCar.RestLift = 0), so the wheel arms sit at the
    /// wheel visuals' x/z and RIDE_HEIGHT − static sag above the origin (the exe: the collision box bottom, which rides at
    /// that height). The PlayerCar's centre of mass stands in for Chassis::SetCOG(0, 0). The PlayerCar is disabled while
    /// the car animates and its read-outs are fed through SetExternalSimState.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class NisCarControl : MonoBehaviour
    {
        /// <summary>gNIS_AeroDynamics (0x937800): set by ENISAeroDynamics, DoAerodynamics(state, 0, x, ...) when &gt; 0.</summary>
        public static float AeroDynamics;

        const float IN = 0.0254f, LBIN2NM = 175.1268f;
        /// <summary>mMaxSteering 0.125 turn (ctor 0x6B30C0).</summary>
        const float MaxSteering = 45f * Mathf.Deg2Rad;

        public PlayerCar Car { get; private set; }
        /// <summary>IVehicle::IsAnimating: the controller drives the car (false between ENISDetach and ENISReattach).</summary>
        public bool Animating { get; private set; }
        /// <summary>The NIS channel ("Car1", "Cop2").</summary>
        public string Channel { get; private set; }

        Rigidbody _rb;
        CarSpec _c;
        Func<float, (Vector3 pos, Quaternion rot)> _path;
        NisPlayer _player;
        BoxCollider _box;
        Vector3 _dim = new Vector3(0.9f, 0.7f, 2.3f);

        // ---- SuspensionSpline state ----
        sealed class Tire
        {
            public bool Locked;
            public float Burnout, Radius, AV, Slip, RoadSpeed, SlipAngle, LateralSpeed, Load;

            public void BeginFrame() => Load = 0f;

            public void UpdateFree()
            {
                Slip = 0f;
                SlipAngle = 0f;
                if (Locked) AV = 0f;
            }

            /// <summary>Tire::UpdateLoaded: the wheel turns at road speed (+ burnout), locked = 0.</summary>
            public void UpdateLoaded(float latVel, float fwdVel, float load)
            {
                Load = load;
                RoadSpeed = fwdVel;
                LateralSpeed = latVel;
                SlipAngle = Mathf.Atan2(latVel, Mathf.Abs(fwdVel));
                AV = Locked ? 0f : (fwdVel + Burnout) / Radius;
                Slip = AV * Radius - fwdVel;
            }

            public void MatchSpeed(float speed)
            {
                Locked = false;
                Slip = 0f;
                RoadSpeed = speed;
                SlipAngle = 0f;
                LateralSpeed = 0f;
                AV = speed / Radius;
            }
        }

        readonly Tire[] _t = new Tire[4];
        readonly Wheel[] _w = new Wheel[4];
        readonly float[] _sag = new float[2];
        int _onGround;
        float _steering, _nisSteering, _nisSteeringWeight, _timeInAir, _burnout, _constraint = 60f;
        bool _lockFront, _lockRear;
        bool _haveLast;
        Vector3 _nisPos;
        float _nisYaw;
        // NISCarTweaks
        float _pitchLength, _pitch, _pitchDelta, _pitchTime;
        float _rollLength, _roll, _rollDelta, _rollTime;
        float _shakeLength, _shakeRate, _shakeRamp, _shake, _shakeDelta, _shakeTime;

        // ---- EngineSpline state ----
        int _gear = Chassis.GFirst;
        float _shiftTimer, _throttle, _engineW, _rpm, _neutralRevRpm, _neutralRevSpeed;
        bool _neutralRev, _nitro, _engineBraking;
        readonly float[] _shiftUp = new float[10], _shiftDown = new float[10];

        // ---- what the controller took over, restored on hand-back ----
        bool _carWasEnabled, _usedDevice, _wasKinematic, _gravity, _ending;
        Transform _bodyT;
        Quaternion _bodyRot0;
        Vector3[] _wheelPos0;
        Quaternion[] _wheelRot0;
        Transform[] _spinT;
        float[] _spinDeg;
        float _sceneT, _lastSceneT, _clockFrame, _clockFixed;
        float _renderSteer, _maxTireSteer = 90f * Mathf.Deg2Rad, _steerSpeed = 180f * Mathf.Deg2Rad;
        bool _clockSet;

        /// <summary>
        /// NISActivity::AddCar + SetAnimating(true): the car follows the scene track (scene time → world pose) through
        /// SuspensionSpline from now on. Null when the car has no physics to drive.
        /// </summary>
        public static NisCarControl Begin(PlayerCar car, NisPlayer player, Func<float, (Vector3, Quaternion)> path, string channel)
        {
            if (!car || car.Spec == null || path == null) return null;
            var rb = car.GetComponent<Rigidbody>();
            if (!rb || car.WheelVisuals == null || car.WheelVisuals.Length < 4) return null;
            var c = car.GetComponent<NisCarControl>() ?? car.gameObject.AddComponent<NisCarControl>();
            c.Init(car, rb, player, path, channel);
            return c;
        }

        void Init(PlayerCar car, Rigidbody rb, NisPlayer player, Func<float, (Vector3, Quaternion)> path, string channel)
        {
            Car = car;
            _rb = rb;
            _c = car.Spec;
            _player = player;
            _path = path;
            Channel = channel;
            _box = GetComponent<BoxCollider>();
            if (_box) _dim = _box.size * 0.5f;
            _bodyT = transform.Find("Body");
            if (_bodyT) _bodyRot0 = _bodyT.localRotation;
            _wheelPos0 = new Vector3[4];
            _wheelRot0 = new Quaternion[4];
            _spinT = new Transform[4];
            _spinDeg = new float[4];
            for (int i = 0; i < 4; i++)
            {
                var wv = car.WheelVisuals[i];
                _t[i] = new Tire { Radius = Mathf.Max(i < 2 ? _c.TireRadiusFront : _c.TireRadiusRear, 0.1f) };
                _w[i] = new Wheel();
                if (!wv) continue;
                _wheelPos0[i] = wv.localPosition;
                _wheelRot0[i] = wv.localRotation;
                _spinT[i] = wv.Find("Spin");
            }
            // static sag k·x·(1 + p·x) = corner weight (as PlayerCar.Setup), capped at the travel
            for (int a = 0; a < 2; a++)
            {
                bool front = a == 0;
                float load = _c.Mass * 9.81f * 0.5f * (front ? _c.FrontWeightBias : 100f - _c.FrontWeightBias) * 0.01f;
                float k = Mathf.Max(1000f, (front ? _c.SpringFront : _c.SpringRear) * LBIN2NM), p = front ? _c.SpringProgFront : _c.SpringProgRear;
                float x = p > 1e-4f ? (-k + Mathf.Sqrt(k * k + 4f * p * k * load)) / (2f * p * k) : load / k;
                _sag[a] = Mathf.Clamp(x, 0f, Travel(a));
            }
            // the arms: track at the wheel visuals, RIDE_HEIGHT − sag over the origin (see the class notes) [ours]
            for (int i = 0; i < 4; i++)
            {
                int a = i >> 1;
                var wv = car.WheelVisuals[i];
                float x = wv ? wv.localPosition.x : (i & 1) == 0 ? -0.8f : 0.8f;
                float z = wv ? wv.localPosition.z : a == 0 ? _c.FrontAxle : _c.FrontAxle - _c.WheelBase;
                _w[i].LocalArm = new Vector3(x, Ride(a) - _sag[a], z);
            }
            CalcShiftPoints();
            // CarRenderConn::UpdateSteering (0x7459F0): ecar MaxTireSteer / SteerSpeed (default 90° / 180 °/s)
            var ecar = GameData.Attributes?.Get("ecar", (_c.Name ?? "").ToLowerInvariant());
            if (ecar != null)
            {
                _maxTireSteer = ecar.Float("MaxTireSteer", 0, 90f) * Mathf.Deg2Rad;
                _steerSpeed = ecar.Float("SteerSpeed", 0, 180f) * Mathf.Deg2Rad;
            }
            _carWasEnabled = car.enabled;
            _usedDevice = car.UseDeviceInput;
            _wasKinematic = rb.isKinematic;
            _gravity = rb.useGravity;
            SetAnimating(true);
        }

        float Ride(int axle) => (axle == 0 ? _c.RideHeightFront : _c.RideHeightRear) * IN;
        float Travel(int axle) => Mathf.Max(0.01f, (axle == 0 ? _c.TravelFront : _c.TravelRear) * IN);

        // ---- IVehicle::SetAnimating (PVehicle 0x686410: the behaviours reload) ----

        void SetAnimating(bool on)
        {
            if (Animating == on) return;
            Animating = on;
            SetImmobile(on);
            if (on)
            {
                Car.enabled = false;
                Car.UseDeviceInput = false;
                _rb.isKinematic = false;
                _rb.useGravity = true;
                _haveLast = false;   // AnimatedCars: HaveLastCarPosition 0 → the next SetNISPosition is the initial one
                Construct();
                Reset();
                EngineReset();
                RestoreState();
            }
            else HandBack();
        }

        /// <summary>
        /// PVehicle::SetAnimating(true) reloads the behaviours (0x686410), so SuspensionSpline and EngineSpline come back
        /// at their constructor values (0x6B30C0 / 0x6B3EF0): constraint 60°, no pitch/roll/shake pulse, steering 0, tyres
        /// cleared; gear FIRST, shift timer 0, rpm 0.
        /// </summary>
        void Construct()
        {
            _constraint = 60f;
            _pitchLength = _pitch = _pitchDelta = _pitchTime = 0f;
            _rollLength = _roll = _rollDelta = _rollTime = 0f;
            _shakeLength = _shakeRate = _shakeRamp = _shake = _shakeDelta = _shakeTime = 0f;
            _steering = _nisSteering = _nisSteeringWeight = 0f;
            _timeInAir = 0f;
            foreach (var t in _t) { t.Locked = false; t.Burnout = 0f; t.AV = t.Slip = t.RoadSpeed = t.SlipAngle = t.LateralSpeed = t.Load = 0f; }
            _gear = Chassis.GFirst;
            _shiftTimer = 0f;
            _rpm = 0f;
            _engineW = 0f;
            _throttle = 0f;
            _cogZ = float.NaN;
        }

        /// <summary>The car's own behaviours take over again (SetAnimating(false)) [ours: PlayerCar re-enabled].</summary>
        void HandBack()
        {
            if (!Car) return;
            ApplyBodyTweak(true);
            for (int i = 0; i < 4; i++)
            {
                var wv = Car.WheelVisuals[i];
                if (wv) { wv.localPosition = _wheelPos0[i]; wv.localRotation = _wheelRot0[i]; }
            }
            // [ours] between ENISDetach and the end of the NIS the car coasts on its own physics with no input (the
            // player's pad is not read until the NIS is over)
            Car.UseDeviceInput = _ending && _usedDevice;
            Car.Input = default;
            // a blown or totaled car would stay on the brakes under the racer chassis (Chassis::ComputeState) [ours]
            if (Car.Destroyed || Car.EngineBlown) Car.ResetDamage();
            Car.enabled = _carWasEnabled;
        }

        /// <summary>The NIS ends (NISActivity dtor: SetAnimating(false) for every car).</summary>
        public void End()
        {
            _ending = true;
            if (Animating) SetAnimating(false);
            else if (Car) Car.UseDeviceInput = _usedDevice;
            if (_rb) { _rb.isKinematic = _wasKinematic; _rb.useGravity = _gravity; }
            Destroy(this);
        }

        // ---- INISCarControl ----

        public void SetBurnout(float speed) => _burnout = speed;
        public void SetBrakeLock(bool front, bool rear) { _lockFront = front; _lockRear = rear; }
        public void SetConstraintAngle(float degrees) => _constraint = Mathf.Clamp(degrees, 0f, 80f);

        /// <summary>SetSteering(angle, weight): ENISSteering passes fDegrees × 1/360 (0x63A476); stored in radians here.</summary>
        public void SetSteering(float radians, float weight)
        {
            _nisSteering = Mathf.Clamp(radians, -MaxSteering, MaxSteering);
            _nisSteeringWeight = Mathf.Clamp01(weight);
        }

        /// <summary>AngleToAIAngle(bATan(half, dip)): binary angle / 32768, i.e. atan(dip / half) / π.</summary>
        static float DipAngle(float half, float dip)
        {
            float a = Mathf.Atan2(Mathf.Abs(dip), Mathf.Max(half, 1e-4f)) / Mathf.PI;
            return dip < 0f ? -a : a;
        }

        /// <summary>SetAnimPitch: GetWheelBase gives the box half extents; the lever is half of that.</summary>
        public void SetAnimPitch(float dip, float time)
        {
            _pitchLength = time; _pitchTime = 0f; _pitch = 0f;
            _pitchDelta = DipAngle(_dim.z * 0.5f, dip);
        }

        public void SetAnimRoll(float dip, float time)
        {
            _rollLength = time; _rollTime = 0f; _roll = 0f;
            _rollDelta = DipAngle(_dim.x * 0.5f, dip);
        }

        public void SetAnimShake(float dip, float cycleRate, float cycleRamp, float time)
        {
            _shakeLength = time; _shakeTime = 0f; _shake = 0f;
            _shakeRate = cycleRate; _shakeRamp = cycleRamp;
            _shakeDelta = DipAngle(_dim.x * 0.5f, dip);
        }

        public float AnimPitch => _pitch;
        public float AnimRoll => _roll;
        public float AnimShake => _shake;

        /// <summary>SuspensionSpline::RestoreState (+ EngineSpline::RestoreState through INISCarEngine).</summary>
        public void RestoreState()
        {
            if (Car) Car.Input = default;   // IInput::ClearInput
            _burnout = 0f;
            _lockFront = _lockRear = false;
            _constraint = 60f;
            _nisSteering = 0f;
            _nisSteeringWeight = 0f;
            _neutralRev = false;
            _neutralRevRpm = 0f;
            _neutralRevSpeed = 0f;
            _nitro = false;
            _rpm = _c.IdleRpm;
            _engineW = _rpm * 0.10471976f;
            _throttle = 0f;
        }

        // ---- INISCarEngine ----

        public void SetNeutralRev(bool on, float throttle, float speed)
        {
            _neutralRev = on;
            _neutralRevRpm = _c.IdleRpm + (_c.RedLine - _c.IdleRpm) * throttle;
            _neutralRevSpeed = speed;
        }

        public void SetNitro(bool on) => _nitro = on;
        public bool Nitro => _nitro;

        /// <summary>ENISDetach (0x639C70): SetAnimating(false).</summary>
        public void Detach() => SetAnimating(false);

        /// <summary>
        /// CAnimScene::JumpToEnd → AnimatedCars_SetTime(total) (AnimScene.cpp:713-724): HaveLastCarPosition cleared, the
        /// track evaluated at the end and an initial SetNISPosition there (SetVehicleOnGround at the final pose, zero speed).
        /// </summary>
        public void JumpToEnd(float sceneTime)
        {
            if (!Animating || !Car || !_rb) return;
            _sceneT = _lastSceneT = sceneTime;
            _haveLast = false;
            var (pos, rot) = _path(sceneTime);
            if (WorldQueries.HeightRigorous(pos, out float ground, out _)) pos.y = ground + 5f;
            SetNISPosition(pos, rot, true, 0f);
        }

        /// <summary>ENISReattach (0x63A320): SetAnimating(true), the next pose is an initial one, fRepairDamage → ResetDamage.</summary>
        public void Reattach(bool repair)
        {
            SetAnimating(true);
            _haveLast = false;
            if (repair && Car) Car.ResetDamage();
        }

        // ---- the NIS events (one-line hook from NisEventRunner.Handle) ----

        /// <summary>
        /// Handles the INISCarControl / INISCarEngine events of a NIS channel; true when the event is one of them (even if
        /// the channel's car does not animate, as the exe's QueryInterface simply fails then).
        /// </summary>
        public static bool HandleEvent(NisPlayer player, string channel, SeqEvent e)
        {
            switch (e.Name)
            {
                case "ENISAeroDynamics": AeroDynamics = e.F(4); return true;
                case "ENISDetach": case "ENISReattach": case "ENISBurnout": case "ENISNitro": case "ENISBrakelock":
                case "ENISNeutralRev": case "ENISSteering": case "ENISCarDamageReset":
                case "ENISCarPitch": case "ENISCarRoll": case "ENISCarShake":
                    break;
                default: return false;
            }
            var car = player && channel != null ? player.CarOf(channel) : null;
            var c = car ? car.GetComponent<NisCarControl>() : null;
            if (e.Name == "ENISCarDamageReset") { if (car) car.ResetDamage(); return true; }
            if (!c) return true;
            switch (e.Name)
            {
                case "ENISDetach": if (c.Animating) c.Detach(); return true;
                case "ENISReattach": c.Reattach(e.I(4) != 0); return true;
            }
            if (!c.Animating) return true;
            switch (e.Name)
            {
                case "ENISBurnout": c.SetBurnout(e.F(4)); break;
                case "ENISNitro": c.SetNitro(e.U(4) != 0); break;
                case "ENISBrakelock": c.SetBrakeLock(e.U(4) != 0, e.U(8) != 0); break;
                case "ENISNeutralRev": if (e.U(4) != 0) c.SetNeutralRev(true, e.F(8), e.F(12)); else c.SetNeutralRev(false, 0f, 0f); break;
                case "ENISSteering": c.SetSteering(e.F(4) * Mathf.Deg2Rad, e.F(8)); break;
                case "ENISCarPitch": c.SetAnimPitch(e.F(4), e.F(8)); break;
                case "ENISCarRoll": c.SetAnimRoll(e.F(4), e.F(8)); break;
                case "ENISCarShake": c.SetAnimShake(e.F(4), e.F(8), e.F(12), e.F(16)); break;
            }
            return true;
        }

        // ---- the scene clock: CAnimScene::UpdateTime runs at the sim rate; the NisPlayer clock advances per frame ----

        void LateUpdate()
        {
            // after NisPlayer.LateUpdate (execution order 100): the scene time this frame and the fixed time it belongs to
            if (!_player) return;
            _clockFrame = _player.SceneTime;
            _clockFixed = Time.fixedTime;
            _clockSet = true;
            if (Animating) UpdateVisuals();
        }

        float SceneClock()
        {
            if (!_player) return _sceneT;
            if (!_clockSet) return _player.SceneTime;
            // the fixed steps run between frames: extrapolate the frame's scene time at the scene rate [ours]
            float ahead = Mathf.Clamp(Time.fixedTime - _clockFixed, 0f, 0.1f) * _player.ClockRate;
            return Mathf.Max(_lastSceneT, _clockFrame + ahead);
        }

        void FixedUpdate()
        {
            if (!Animating || !Car || !_rb || _rb.isKinematic) return;
            float dT = Time.fixedDeltaTime;
            RigidBodyFrame();
            _sceneT = SceneClock();
            float animDT = _haveLast ? _sceneT - _lastSceneT : 0f;
            _lastSceneT = _sceneT;
            var (pos, rot) = _path(_sceneT);
            // CAnimScene::AnimatedCars_SetMainAndWheels (AnimScene.cpp:672-684): the bone matrix × the scene frame, then
            // y = GetWorldHeightAtPointRigorous(track point) + 5 m when a face is found (else the track's own height)
            bool haveGround = WorldQueries.HeightRigorous(pos, out float ground, out _);
            if (haveGround) pos.y = ground + 5f;
            SetNISPosition(pos, rot, !_haveLast, animDT, haveGround);
            if (!Animating) return;
            // behaviour order ENGINE → SUSPENSION (PVehicle mechanics): the suspension's IInput gas (1 burnout / 0.5) is the
            // tick's final value, over EngineSpline's SetControlGas(mThrottle)
            EngineSimulate(dT);
            OnTaskSimulate(dT);
        }

        /// <summary>
        /// The exe's rigid body for the NIS car, which has no PlayerCar.Step while it animates: RigidBody::OnBeginFrame
        /// gravity GRAVITY·m (−9.813) [ours: the difference to Unity's], DoIntegration's ±300 / ±30 component bounds.
        /// </summary>
        void RigidBodyFrame()
        {
            _rb.AddForce(Vector3.up * ((_c.Body != null ? _c.Body.Gravity : -9.813f) - Physics.gravity.y) * _rb.mass);
            var lv = _rb.linearVelocity;
            var av = _rb.angularVelocity;
            var blv = new Vector3(Mathf.Clamp(lv.x, -300f, 300f), Mathf.Clamp(lv.y, -300f, 300f), Mathf.Clamp(lv.z, -300f, 300f));
            var bav = new Vector3(Mathf.Clamp(av.x, -30f, 30f), Mathf.Clamp(av.y, -30f, 30f), Mathf.Clamp(av.z, -30f, 30f));
            if (blv != lv) _rb.linearVelocity = blv;
            if (bav != av) _rb.angularVelocity = bav;
        }

        // ---- SuspensionSpline::SetNISPosition (0x691110; decomp SuspensionSpline.cpp:621-702) ----

        static float Yaw(Quaternion r)
        {
            var f = r * Vector3.forward;
            return Mathf.Atan2(f.x, f.z);
        }

        void SetNISPosition(Vector3 pos, Quaternion rot, bool initial, float dT, bool haveGround = true)
        {
            NISCarTweaks(dT);
            if (initial)
            {
                // [ours] no world collision under the track point yet (streaming): SetVehicleOnGround would fail; hold the car
                // at the track point and retry next step instead of teleporting it every step
                if (!haveGround) { HoldWithoutGround(pos, rot); return; }
                // SuspensionSpline.cpp:626-634: PVehicle::SetVehicleOnGround(position, forward) (0x685810: the ground face
                // under the track point, searched from dimension.y above it, a WWorldPos per wheel, the body turned to the
                // plane through the contacts, behaviours reset, SetSpeed(0)); it fails (and the scene retries next update,
                // HaveLastCarPosition −1) with no collision under it
                var fwd = rot * Vector3.forward;
                if (!Car.SetVehicleOnGround(pos, fwd)) { HoldWithoutGround(pos, rot); return; }
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                Physics.SyncTransforms();
                Reset();            // SuspensionSpline::Reset (+ MatchSpeed(0) through SetSpeed(0))
                EngineReset();      // EngineSpline::Reset
                EngineMatchSpeed(0f);
                _nisPos = pos;
                _nisYaw = Yaw(rot);
                _haveLast = true;
                RestoreState();
                return;
            }
            float yaw = Yaw(rot);
            Vector3 linXZ = Vector3.zero;
            float angY = 0f;
            if (dT > 1e-6f)
            {
                linXZ = (pos - _nisPos) / dT;
                // [ours] the exe subtracts the two Atan2r headings without wrapping (a one-step spike at ±180°)
                angY = Mathf.DeltaAngle(_nisYaw * Mathf.Rad2Deg, yaw * Mathf.Rad2Deg) * Mathf.Deg2Rad / dT;
            }
            _nisPos = pos;
            _nisYaw = yaw;
            var bp = _rb.position;
            bp.x = pos.x;
            bp.z = pos.z;
            _rb.position = bp;
            var v = _rb.linearVelocity;
            v.x = linXZ.x;
            v.z = linXZ.z;
            _rb.linearVelocity = v;
            var av = _rb.angularVelocity;
            av.y = angY;
            _rb.angularVelocity = av;
            // pitch/roll (SuspensionSpline.cpp:673): Crossxyz(body up, mNISPosition.v1 = the TRACK's up); asin of its world
            // x / z, each held within ±constraint/2
            var m = _rb.rotation;
            var cross = Vector3.Cross(m * Vector3.up, rot * Vector3.up);
            float pitch = Mathf.Asin(Mathf.Clamp(cross.x, -1f, 1f)), roll = Mathf.Asin(Mathf.Clamp(cross.z, -1f, 1f));
            float max = _constraint * 0.5f * Mathf.Deg2Rad;
            float np = Mathf.Clamp(pitch, -max, max), nr = Mathf.Clamp(roll, -max, max);
            // [?] MultZRot / MultXRot by the excess: taken about the world axes the cross was measured on
            if (nr != roll) m = Quaternion.AngleAxis((roll - nr) * Mathf.Rad2Deg, Vector3.forward) * m;
            if (np != pitch) m = Quaternion.AngleAxis((pitch - np) * Mathf.Rad2Deg, Vector3.right) * m;
            // the heading: the NIS forward in the body frame, turned about the body's own up
            var localF = Quaternion.Inverse(m) * (rot * Vector3.forward);
            float delta = Mathf.Atan2(localF.x, localF.z);
            m = m * Quaternion.AngleAxis(delta * Mathf.Rad2Deg, Vector3.up);
            _rb.rotation = m;
        }

        /// <summary>[ours] no world collision under the track yet (streaming): keep the car at the track point, still.</summary>
        void HoldWithoutGround(Vector3 pos, Quaternion rot)
        {
            _rb.position = pos;
            _rb.rotation = Quaternion.Euler(0f, Yaw(rot) * Mathf.Rad2Deg, 0f);
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        // ---- SuspensionSpline::NISCarTweaks (0x690EA0) ----

        void NISCarTweaks(float dT)
        {
            Pulse(ref _pitchLength, ref _pitchTime, _pitchDelta, ref _pitch, dT);
            Pulse(ref _rollLength, ref _rollTime, _rollDelta, ref _roll, dT);
            if (_shakeLength != 0f && _shakeRate != 0f)
            {
                _shakeTime += dT;
                if (_shakeTime < _shakeLength)
                {
                    float inv = 1f / _shakeRate;
                    float total = (int)(inv * _shakeLength), count = (int)(_shakeTime * inv), rampCycles = (int)(inv * _shakeRamp + 0.99f);
                    float ratio = (_shakeTime - count * _shakeRate) * inv;
                    float ramp = 1f;
                    if (rampCycles > 0f)
                    {
                        if (count < rampCycles) ramp = count / rampCycles;
                        else if (total - rampCycles < count) ramp = (total - count) / rampCycles;
                    }
                    // bCos of the binary angle ratio·65536 (0x6910AD..0x6910CE)
                    _shake = Mathf.Cos(ratio * 2f * Mathf.PI) * _shakeDelta * ramp;
                }
                else { _shake = 0f; _shakeDelta = 0f; _shakeRamp = 0f; _shakeLength = 0f; }
            }
        }

        /// <summary>The pitch/roll pulse: rises to delta/2 at half time, back to 0 at the end, then cleared.</summary>
        static void Pulse(ref float length, ref float time, float delta, ref float output, float dT)
        {
            if (length == 0f) return;
            time += dT;
            if (time < length)
            {
                float half = length * 0.5f;
                float pos = time <= half ? time * 0.5f : half - time * 0.5f;
                output = pos / half * delta;
            }
            else { output = 0f; length = 0f; }
        }

        // ---- SuspensionSpline::Reset (0x6916F0) / MatchSpeed (0x691610) ----

        Chassis.State State(float dT)
        {
            var s = new Chassis.State
            {
                Time = dT,
                Rotation = _rb.rotation,
                Position = _rb.position,
                LinearVel = _rb.linearVelocity,
                AngularVel = _rb.angularVelocity,
                Dimension = _dim,
                Mass = _rb.mass,
                MaxReach = Mathf.Max(Ride(0), Ride(1)) + Mathf.Max(Travel(0), Travel(1)) + Mathf.Max(_t[0].Radius, _t[2].Radius),
            };
            s.Right = s.Rotation * Vector3.right;
            s.Up = s.Rotation * Vector3.up;
            s.Forward = s.Rotation * Vector3.forward;
            s.LocalVel = s.ToLocal(s.LinearVel);
            s.LocalAngularVel = s.ToLocal(s.AngularVel);
            s.Speed = s.LinearVel.magnitude;
            s.Cog = transform.InverseTransformPoint(_rb.worldCenterOfMass);
            s.WorldCog = s.ToWorld(s.Cog);
            return s;
        }

        void Reset()
        {
            var s = State(Time.fixedDeltaTime);
            int n = 0;
            for (int i = 0; i < 4; i++)
            {
                var w = _w[i];
                w.Compression = 0f;
                w.AirTime = 0f;
                if (w.UpdatePosition(s, s.WorldCog, _t[i].Radius, _dim.y * 2f))
                {
                    float up = Mathf.Clamp01(Vector3.Dot(w.Normal, s.Up));
                    float c = Mathf.Max(w.Penetration + Ride(i >> 1) * up, 0f);
                    w.SetCompression(c);
                    if (c > 0f) n++;
                }
            }
            _onGround = n;
            _timeInAir = 0f;
        }

        public void MatchSpeed(float speed)
        {
            foreach (var t in _t) t.MatchSpeed(speed);
        }

        // ---- Chassis::SetCOG(0, 0) (Chassis.cpp:266-282), every SuspensionSpline tick ----

        float _cogZ = float.NaN;

        /// <summary>
        /// The CG's z: rear axle + wheelbase × FRONT_WEIGHT_BIAS/100, 0.5 when no wheel is on the ground; the y is the
        /// PlayerCar's own (ROLL_CENTER − RIDE_HEIGHT above the hubs) [ours: the frames agree, see the class notes].
        /// </summary>
        void UpdateCog()
        {
            float bias = _onGround == 0 ? 0.5f : _c.FrontWeightBias * 0.01f;
            float zf = _w[0].LocalArm.z, zr = _w[2].LocalArm.z;
            float z = zr + (zf - zr) * bias;
            if (!float.IsNaN(_cogZ) && Mathf.Abs(z - _cogZ) < 1e-4f) return;
            _cogZ = z;
            var t = _rb.inertiaTensor;
            var com = _rb.centerOfMass;
            _rb.centerOfMass = new Vector3(0f, com.y, z);
            _rb.inertiaTensor = t;
            _rb.inertiaTensorRotation = Quaternion.identity;
        }

        // ---- SuspensionSpline::OnTaskSimulate (0x6B34A0) ----

        void OnTaskSimulate(float dT)
        {
            UpdateCog();
            var s = State(dT);
            float split = _c.TorqueSplit;
            for (int i = 0; i < 4; i++)
            {
                _t[i].BeginFrame();
                _t[i].Locked = i > 1 ? _lockRear : _lockFront;
                // the burnout spins the driven wheels (TORQUE_SPLIT 0 = rear drive in the sim's convention)
                if (i <= 1 ? split != 0f : split != 1f) _t[i].Burnout = _burnout;
            }
            // IInput (SuspensionSpline.cpp:481-485): gas 1 with a burnout else 0.5 (over EngineSpline's), brake 1 with both
            // locks, e-brake with the rear lock only; the nitrous button is EngineSpline's (SetNitro)
            var input = Car.Input;
            input.Throttle = _burnout != 0f ? 1f : 0.5f;
            input.Brake = 0f;
            input.Handbrake = false;
            if (_lockFront && _lockRear) input.Brake = 1f;
            else if (_lockRear) input.Handbrake = true;
            input.Nitrous = _nitro;
            Car.Input = input;
            DoWheelForces(s);
            _timeInAir = _onGround != 0 ? 0f : _timeInAir + dT;
            if (AeroDynamics > 0f) DoAerodynamics(s, AeroDynamics);
        }

        /// <summary>DoSteering (0x691860): the wheels follow the body's slip (steer into it), blended with ENISSteering.</summary>
        void DoSteering(in Chassis.State s, out Vector3 dir)
        {
            float steering = 0f;
            if (s.Speed > 1f)
            {
                steering = Mathf.Atan2(s.LocalVel.x, Mathf.Abs(s.LocalVel.z));
                if (s.LocalVel.z < 0f) steering = -steering;
            }
            steering *= 1f - Mathf.Abs(s.LocalVel.z * 0.02f) * 0.8f;
            _steering = (1f - _nisSteeringWeight) * steering + _nisSteeringWeight * _nisSteering;
            _steering = Mathf.Clamp(_steering, -MaxSteering, MaxSteering);
            dir = s.ToWorld(new Vector3(Mathf.Sin(_steering), 0f, Mathf.Cos(_steering)));
        }

        /// <summary>DoWheelForces (0x6AE710; decomp :770-975).</summary>
        void DoWheelForces(in Chassis.State s)
        {
            float dT = s.Time;
            DoSteering(s, out var steer);
            int onGround = 0;
            float maxDelta = 0f;
            var vUp = s.Up;
            float mass = s.Mass;
            float sw0 = (_w[0].Compression - _w[1].Compression) * _c.SwayFront * LBIN2NM;
            float sw1 = (_w[2].Compression - _w[3].Compression) * _c.SwayRear * LBIN2NM;
            var cg = s.Position + s.WorldCog;
            var totalForce = Vector3.zero;
            var totalTorque = Vector3.zero;
            bool resolve = false;
            for (int i = 0; i < 4; i++)
            {
                int axle = i >> 1;
                var w = _w[i];
                var t = _t[i];
                w.UpdatePosition(s, s.WorldCog, t.Radius, s.Dimension.y * 2f);
                var ground = w.Normal;
                var fwd = i < 2 ? steer : s.Forward;
                var lat = Vector3.Cross(ground, fwd).normalized;
                float upness = Mathf.Clamp01(Vector3.Dot(ground, vUp));
                float ride = Ride(axle), travel = Travel(axle);
                float comp = ride * upness + w.Penetration;
                if (w.Compression == 0f) maxDelta = Mathf.Max(maxDelta, comp - travel);
                comp = Mathf.Max(comp, 0f);
                if (comp > travel) { maxDelta = Mathf.Max(maxDelta, comp - travel); comp = travel; }
                if (comp > 0f && upness > Chassis.EnableRollStopsThreshold && dT > 0f)
                {
                    onGround++;
                    float rise = (comp - w.Compression) / dT;
                    float valving = (axle == 0 ? _c.ValvingFront : _c.ValvingRear) * IN;
                    float digression = 1f - (axle == 0 ? _c.DigressionFront : _c.DigressionRear);
                    if (valving > 1e-6f && digression < 1f && Mathf.Abs(rise) > valving)
                    {
                        float dg = valving * Mathf.Pow(Mathf.Abs(rise) / valving, digression);
                        rise = rise > 0f ? dg : -dg;
                    }
                    float k = (axle == 0 ? _c.SpringFront : _c.SpringRear) * LBIN2NM;
                    float spring = comp * k * (comp * (axle == 0 ? _c.SpringProgFront : _c.SpringProgRear) + 1f);
                    float damp = rise > 0f ? rise * (axle == 0 ? _c.ShockFront : _c.ShockRear) * LBIN2NM
                                           : rise * (axle == 0 ? _c.ShockExtFront : _c.ShockExtRear) * LBIN2NM;
                    if (damp > _c.ShockBlowout * 9.81f * mass) damp = 0f;
                    float sway = i == 0 ? sw0 : i == 1 ? -sw0 : i == 2 ? sw1 : -sw1;
                    float springForce = Mathf.Max(damp + spring + sway, 0f);
                    var vertical = vUp * springForce;
                    var c = Vector3.Cross(Vector3.Cross(fwd, ground), fwd);
                    float d2 = Mathf.Max(Vector3.Dot(c, ground) * 4f - 3f, 0.3f);
                    float load = d2 * springForce;
                    float xs = Vector3.Dot(w.Velocity, lat), zs = Vector3.Dot(w.Velocity, fwd);
                    var acc = new Vector3((xs - t.LateralSpeed) / dT, 0f, (zs - t.RoadSpeed) / dT);
                    t.UpdateLoaded(xs, zs, load);
                    var lf = acc * (mass * 0.25f);
                    var lateralForce = lat * -lf.x + fwd * lf.z;
                    float lforce = lf.magnitude, sgrip = (axle == 0 ? _c.StaticGripFront : _c.StaticGripRear) * load;
                    if (lforce > sgrip) lateralForce *= (axle == 0 ? _c.DynamicGripFront : _c.DynamicGripRear) * load / lforce;
                    var force = lateralForce + vertical;
                    var p = w.LocalArm;
                    p.y += w.Compression - ride;
                    p = s.Position + s.ToWorld(p);
                    w.Position = p;
                    w.Force = force;
                    totalTorque += Vector3.Cross(p - cg, force);
                    totalForce += vertical;
                    resolve = true;
                }
                else
                {
                    w.Force = Vector3.zero;
                    t.UpdateFree();
                }
                if (comp == 0f) w.AirTime += dT; else w.AirTime = 0f;
                w.SetCompression(comp);
            }
            if (resolve)
            {
                // only the body-up force and the pitch/roll torque reach the body
                var lt = s.ToLocal(totalTorque);
                var lfz = s.ToLocal(totalForce);
                lt.y = 0f;
                lfz.x = 0f;
                lfz.z = 0f;
                _rb.AddForce(s.ToWorld(lfz));
                _rb.AddTorque(s.ToWorld(lt));
            }
            if (maxDelta > 0f)
            {
                // SuspensionSpline.cpp:965-972: every wheel lifted with the body (wheel.SetY(y + maxDelta)), then ModifyYPos
                for (int i = 0; i < 4; i++) { var wp = _w[i].Position; wp.y += maxDelta; _w[i].Position = wp; }
                _rb.position += Vector3.up * maxDelta;   // RigidBody::ModifyYPos
            }
            _onGround = onGround;
        }

        /// <summary>
        /// Chassis::DoAerodynamics(state, 0 drag, gNIS_AeroDynamics, front z, rear z, tunings null) (Chassis.cpp:402-440):
        /// downforce = aero% · upness · forwardness · Physics::Info::AerodynamicDownforce(speed) = speed·2·AERO_COEFFICIENT·1000
        /// (linear in speed, PhysicsInfo.cpp:10-12), ×0.8 airborne, at AERO_CG between the axles. [?] The exe reads the
        /// untuned AERO_COEFFICIENT (tunings null); CarSpec's has the owned car's AERO tuning folded in.
        /// </summary>
        void DoAerodynamics(in Chassis.State s, float aeroPct)
        {
            float upness = _onGround >= 2 ? 1f : Mathf.Max(s.Up.y, 0f);
            var dir = s.Speed > 0.0001f ? s.LinearVel / s.Speed : s.Forward;
            float fwd = Mathf.Max(0.4f, Mathf.Pow(Mathf.Max(Vector3.Dot(dir, s.Forward), 0f), 0.5f));
            float down = aeroPct * upness * fwd * PhysicsInfo.AerodynamicDownforce(_c, s.Speed);
            if (_onGround == 0) down *= 0.8f;
            if (down <= 0f) return;
            var center = s.Cog;
            if (_onGround != 0)
            {
                float zf = _w[0].LocalArm.z, zr = _w[2].LocalArm.z;
                center.z = (zf - zr) * (_c.AeroCg * 0.01f) + zr;
            }
            _rb.AddForceAtPosition(s.ToWorld(new Vector3(0f, -down, 0f)), s.Position + s.ToWorld(center));
        }

        // ---- EngineSpline (0x6A1AC0; decomp EngineSpline.cpp:478-543) ----

        float Ratio(int g) => _c.GearRatios != null && g >= 0 && g < _c.GearRatios.Length ? _c.GearRatios[g] : 0f;
        int NumGears => _c.GearRatios != null ? _c.GearRatios.Length : 0;

        /// <summary>Physics::Info::ShiftPoints without induction (as EngineTraffic).</summary>
        void CalcShiftPoints()
        {
            Array.Clear(_shiftUp, 0, _shiftUp.Length);
            Array.Clear(_shiftDown, 0, _shiftDown.Length);
            int top = NumGears - 1;
            if (NumGears > _shiftUp.Length) return;
            for (int j = Chassis.GFirst; j < top; j++)
            {
                float g1 = Ratio(j), g2 = Ratio(j + 1);
                float max = (_c.RedLine + _c.IdleRpm) * 0.5f;
                bool done = !(max < _c.RedLine);
                while (!done)
                {
                    float cur = _c.TorqueAt(max);
                    float next = Mathf.Abs(g1) > 1e-5f ? _c.TorqueAt(g2 / g1 * max) * g2 / g1 : 0f;
                    if (next > cur) break;
                    max += 50f;
                    done = !(max < _c.RedLine);
                }
                _shiftUp[j] = done ? _c.RedLine - 100f : max;
                _shiftDown[j + 1] = Mathf.Abs(g1) > 1e-5f ? g2 / g1 * _shiftUp[j] : 0f;
            }
            if (top >= 0) _shiftUp[top] = _c.RedLine;
        }

        /// <summary>EngineSpline::Reset (EngineSpline.cpp:273-281): ω 0, gear FIRST, timer 0, throttle 0, the shift points.</summary>
        void EngineReset()
        {
            _engineW = 0f;
            _gear = Chassis.GFirst;
            _shiftTimer = 0f;
            _throttle = 0f;
            CalcShiftPoints();
        }

        /// <summary>
        /// EngineSpline::MatchSpeed (EngineSpline.cpp:299-344), reached through PVehicle::SetSpeed: r̄ = mean wheel radius;
        /// speed 0 → timer 0, gear FIRST, ω = idle; speed &lt; 0 → timer 0, REVERSE; else the highest gear whose top speed
        /// r̄·ωred/ratio is below the speed; ω = ωidle + (speed/r̄)·ratio·FINAL·(ωred − ωidle)/ωred.
        /// </summary>
        void EngineMatchSpeed(float speed)
        {
            float r = 0f;
            foreach (var t in _t) r += t.Radius;
            r *= 0.25f;
            if (r <= 0f) return;
            float dw = speed / r, maxW = _c.RedLine * 0.10471976f, minW = _c.IdleRpm * 0.10471976f;
            if (speed == 0f) { _shiftTimer = 0f; _gear = Chassis.GFirst; _engineW = minW; _rpm = _c.IdleRpm; return; }
            if (speed < 0f) { _shiftTimer = 0f; _gear = Chassis.GReverse; }
            else
            {
                _gear = Chassis.GFirst;
                for (int g = Chassis.GFirst; g < NumGears; g++)
                {
                    float ratio = Ratio(g) * _c.FinalGear;
                    if (ratio > 0f && r * maxW / ratio < speed) _gear = g;
                }
            }
            _engineW = minW + dw * Ratio(_gear) * _c.FinalGear * (maxW - minW) / maxW;
        }

        bool Shift(int gear)
        {
            if (gear < NumGears && gear != _gear && gear >= Chassis.GReverse)
            {
                float delay = _c.ShiftSpeed * Ratio(gear);
                _shiftTimer = gear < _gear ? delay * 0.25f : delay;
                _gear = gear;
                return true;
            }
            return false;
        }

        void AutoShift()
        {
            if (_gear == Chassis.GReverse || _shiftTimer > 0f) return;
            float rpm = _engineW * 9.549297f;
            if (_gear == Chassis.GNeutral) { _gear = Chassis.GFirst; return; }
            float up = _gear < _shiftUp.Length ? _shiftUp[_gear] : _c.RedLine;
            float down = _gear < _shiftDown.Length ? _shiftDown[_gear] : 0f;
            float lowerUp = 0f, lowerRatio = Ratio(_gear - 1);
            if (_gear != Chassis.GFirst && lowerRatio > 0f) lowerUp = _shiftUp[_gear - 1] * Ratio(_gear) / lowerRatio - 200f;
            if (_gear <= Chassis.GFirst) down = _throttle < 0.2f ? _c.IdleRpm + 100f : 0f;
            if (rpm >= up && _gear < NumGears - 1)
            {
                bool traction = true;
                for (int i = 0; i < 4; i++) traction &= _w[i].IsOnGround && _t[i].Slip < 4f;
                if (traction) Shift(_gear + 1);
            }
            else if (rpm <= down && _gear > Chassis.GNeutral && rpm <= lowerUp) Shift(_gear - 1);
        }

        void EngineSimulate(float dT)
        {
            float idle = _c.IdleRpm, red = _c.RedLine;
            if (_neutralRev)
            {
                float diff = red - idle;
                if (_neutralRevSpeed <= 1e-6f) _rpm = _neutralRevRpm;
                else if (_rpm < _neutralRevRpm) _rpm = Mathf.Min(_neutralRevRpm, _rpm + _neutralRevSpeed * diff * dT);
                else _rpm = Mathf.Max(_neutralRevRpm, _rpm - _neutralRevSpeed * diff * dT);
                _gear = Chassis.GNeutral;
                _engineW = _rpm * 0.10471976f;
                _throttle = Chassis.Ramp(_rpm, idle, red);
            }
            else
            {
                // GetDifferentialAngularVelocity: the driven axle(s)
                float into = 0f;
                bool rear = _c.TorqueSplit < 1f, front = _c.TorqueSplit > 0f;
                if (rear) into = (_t[2].AV + _t[3].AV) * 0.5f;
                if (front) { into += (_t[0].AV + _t[1].AV) * 0.5f; if (rear) into *= 0.5f; }
                if (into < -Mathf.PI && _gear != Chassis.GReverse) Shift(Chassis.GReverse);
                else if (into > Mathf.PI && _gear <= Chassis.GNeutral) Shift(Chassis.GFirst);
                else AutoShift();
                _shiftTimer = _shiftTimer <= 0f ? 0f : Mathf.Max(0f, _shiftTimer - dT);
                float maxW = red * 0.10471976f, minW = idle * 0.10471976f;
                float inertia = (_gear != Chassis.GNeutral ? 1f : 0.35f) * (_c.FlywheelMass * 0.025f + 0.25f);
                float total = Ratio(_gear) * _c.FinalGear;
                float av = minW + Mathf.Abs(into) * total * ((maxW - minW) / maxW);
                _engineBraking = av < _engineW;
                _engineW = Mathf.Clamp(av, minW, maxW);
                _rpm = SmoothRpm(_shiftTimer > 0f, _gear, dT, _rpm, _engineW * 9.549297f, inertia);
                _throttle = Mathf.Clamp01((_engineW - minW) / (maxW - minW));
            }
            var input = Car.Input;
            input.Throttle = _throttle;   // mIInput->SetControlGas(mThrottle) (SuspensionSpline overwrites it the same tick)
            input.Nitrous = _nitro;
            Car.Input = input;
        }

        /// <summary>Engine_SmoothRPM (EngineRacer.cpp:888; SmoothRPMDecel {2.5, 15} at 0x8AAE0C).</summary>
        static float SmoothRpm(bool shifting, int gear, float dT, float oldRpm, float newRpm, float inertia)
        {
            bool fast = shifting && gear > Chassis.GFirst || gear == Chassis.GNeutral;
            float maxDecel = -(fast ? 15f : 2.5f) * 1000f / inertia;
            float rpm = newRpm;
            if (dT > 0f && (rpm - oldRpm) / dT < maxDecel) rpm = Mathf.Max(maxDecel * dT + oldRpm, rpm);
            return rpm * 0.55f + oldRpm * 0.45f;
        }

        // ---- the read-outs and the visuals (DrawCar: wheels, compressions, the animated pitch/roll/shake) ----

        void UpdateVisuals()
        {
            float skid = 0f;
            for (int i = 0; i < 4; i++)
                if (_w[i].IsOnGround) skid = Mathf.Max(skid, Mathf.Abs(_t[i].Slip) > 2f ? 1f : 0f);
            Car.SetExternalSimState(_rpm, _gear - 1, _onGround, skid);
            // the ISuspension read-outs (SuspensionSpline.h:191-255): GetWheelSlip = mSlip (ω·r − v, the burnout speed),
            // GetWheelSlipAngle, GetWheelLoad = mLoad, GetWheelAngularVelocity = mAV, IsWheelOnGround = compression > 0,
            // GetWheelRoadSurface — effects (tyre smoke on ENISBurnout) and WheelAudio read them through the PlayerCar
            for (int i = 0; i < 4; i++)
            {
                var w = _w[i];
                var t = _t[i];
                Car.SetExternalWheelState(i, w.IsOnGround, t.Slip, t.SlipAngle, t.RoadSpeed, t.Load, t.AV, w.Surface);
            }
            Car.SetExternalNos(_nitro, 0f);   // EngineSpline: IsNOSEngaged = mNitro, mNOSCapacity 0
            // CarRenderConn::UpdateSteering (0x7459F0): the drawn front-wheel angle follows the sim's at SteerSpeed per
            // second, clamped to ±MaxTireSteer (0 beyond 100 m is the renderer's LOD rule, not applied here)
            float step = _steerSpeed * Time.deltaTime;
            if (_renderSteer < _steering - step) _renderSteer += step;
            else if (_renderSteer > _steering + step) _renderSteer -= step;
            else _renderSteer = _steering;
            _renderSteer = Mathf.Clamp(_renderSteer, -_maxTireSteer, _maxTireSteer);
            for (int i = 0; i < 4; i++)
            {
                var wv = Car.WheelVisuals[i];
                if (!wv) continue;
                int a = i >> 1;
                wv.localPosition = _wheelPos0[i] + Vector3.up * (_w[i].Compression - _sag[a]);
                wv.localRotation = Quaternion.Euler(0f, i < 2 ? _renderSteer * Mathf.Rad2Deg : 0f, 0f) * _wheelRot0[i];
                _spinDeg[i] = Mathf.Repeat(_spinDeg[i] + _t[i].AV * Mathf.Rad2Deg * Time.deltaTime, 360f);
                if (_spinT[i]) _spinT[i].localRotation = Quaternion.Euler(_spinDeg[i], 0f, 0f);
            }
            ApplyBodyTweak(false);
        }

        /// <summary>
        /// DrawCar::OnService (0x6B731F) packs GetAnimPitch/Roll/Shake (pkt +0x94/+0x98/+0x9C); CarRenderConn::
        /// UpdateEngineAnimation (0x746208..) copies pitch into the body's pitch angle, roll (when non-zero) over the
        /// engine-torque roll and shake into the shake angle, all summed as radians in UpdateRenderMatrix (0x747050).
        /// Applied to the "Body" child [?: axes and signs].
        /// </summary>
        void ApplyBodyTweak(bool clear)
        {
            if (!_bodyT) return;
            if (clear) { _bodyT.localRotation = _bodyRot0; return; }
            _bodyT.localRotation = Quaternion.Euler(-_pitch * Mathf.Rad2Deg, 0f, (_roll + _shake) * Mathf.Rad2Deg) * _bodyRot0;
        }

        void OnDestroy()
        {
            SetImmobile(false);
            if (Animating) { Animating = false; HandBack(); }
        }

        // ---- RBVehicle::ModifyCollision(RigidBody) (decomp RBVehicle.cpp:171-178): an animating vehicle is immobile ----

        // The body instance ids of the cars that animate (read on the physics thread under the lock).
        static readonly HashSet<int> Immobile = new HashSet<int>();
        static readonly object ImmobileLock = new object();
        int _immobileId;
        Collider[] _modCols;
        bool[] _modWas;

        /// <summary>
        /// RBVehicle::ModifyCollision against another rigid body: `if (GetVehicle()->IsAnimating()) myMoment.MakeImmobile(true)`.
        /// A NIS car is an infinite mass to every other body (the pursuit cop it is placed next to is pushed away, never
        /// the NIS car); two animating cars are both immobile, so their contact does nothing. World contacts (the
        /// SimSurface overload) are left alone. Without this the solver could only push the NIS car up, since
        /// SetNISPosition pins its x/z every step: it climbed onto the car it overlapped (a busted-scene cop on top of a
        /// pursuit cop). Runs after CarContacts' handler (re-subscribed last), which sets the car-vs-car mass scales.
        /// </summary>
        void SetImmobile(bool on)
        {
            if (!_rb) return;
            if (on)
            {
                _immobileId = _rb.GetInstanceID();
                _modCols = GetComponentsInChildren<Collider>(true);
                _modWas = new bool[_modCols.Length];
                for (int i = 0; i < _modCols.Length; i++)
                {
                    _modWas[i] = _modCols[i].hasModifiableContacts;
                    if (!_modCols[i].isTrigger) _modCols[i].hasModifiableContacts = true;
                }
                Physics.ContactModifyEvent -= MakeImmobile;
                Physics.ContactModifyEventCCD -= MakeImmobile;
                Physics.ContactModifyEvent += MakeImmobile;
                Physics.ContactModifyEventCCD += MakeImmobile;
                lock (ImmobileLock) Immobile.Add(_immobileId);
            }
            else if (_immobileId != 0)
            {
                lock (ImmobileLock) Immobile.Remove(_immobileId);
                _immobileId = 0;
                if (_modCols != null)
                    for (int i = 0; i < _modCols.Length; i++)
                        if (_modCols[i]) _modCols[i].hasModifiableContacts = _modWas[i];
                _modCols = null;
            }
        }

        static void MakeImmobile(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            lock (ImmobileLock)
            {
                if (Immobile.Count == 0) return;
                for (int p = 0; p < pairs.Length; p++)
                {
                    var pair = pairs[p];
                    int ba = pair.bodyInstanceID, bb = pair.otherBodyInstanceID;
                    if (ba == 0 || bb == 0) continue;   // a world contact
                    bool a = Immobile.Contains(ba), b = Immobile.Contains(bb);
                    if (!a && !b) continue;
                    if (a && b)
                    {
                        for (int i = 0; i < pair.contactCount; i++) pair.IgnoreContact(i);
                    }
                    else
                    {
                        var mp = pair.massProperties;
                        if (a) { mp.inverseMassScale = 0f; mp.inverseInertiaScale = 0f; }
                        else { mp.otherInverseMassScale = 0f; mp.otherInverseInertiaScale = 0f; }
                        pair.massProperties = mp;
                    }
                    pairs[p] = pair;
                }
            }
        }
    }
}
