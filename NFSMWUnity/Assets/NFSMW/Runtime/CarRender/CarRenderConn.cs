using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.CarRender
{
    /// <summary>
    /// CarRenderConn::Update (World/CarRenderConn.cpp, exe 0x7564C0 from OnFetch 0x756660): the per-frame render-side
    /// state the sim does not own — the rate-limited render steering (UpdateSteering 0x7459F0), the flat-tyre sink and
    /// wobble, the wheel hop (UpdateTires 0x746750), the road noise (UpdateRoadNoise 0x745C70 / AddRoadNoise 0x7390B0),
    /// the body roll / squat / dive (UpdateBodyAnimation 0x746440), the engine torque roll, vibration and shift pitch
    /// (UpdateEngineAnimation 0x745F20) and the render matrix they build (UpdateRenderMatrix 0x747050: render = Rx(roll)·
    /// Ry(pitch)·body, lifted by ride = hop.z + noise.z + flat.z + INCH2METERS(ecar RideHeight), then the wheel-well clamp).
    /// Attributes: the car's `ecar` collection (0xA5B543B7, parents racers → default). Distances: TestVisibility(d) =
    /// NIS running || view anchor || distance to the camera ≤ d (renderModifier [0x8F2260] taken as 1 [?: the cop
    /// "cherry" count that lowers it is not read]).
    /// Port: runs in LateUpdate after PlayerCar.Update placed the wheel pivots (sim steer, roll) and applies the
    /// animation to the Body transform as an offset from its base pose (PlayerCar.Setup's RIDE_HEIGHT − sag placement).
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class CarRenderConn : MonoBehaviour
    {
        struct BodyMotion { public float DegPerG, MaxGs, DegPerSec; }   // CarBodyMotion (MWAttribUserTypes.h:72): DegPerG, MaxGs, DegPerSec
        struct RoadNoise { public float Frequency, Amplitude, MinSpeed, MaxSpeed; }

        // ecar
        BodyMotion _roll, _squat, _dive;
        float _shiftAngle, _shiftSpeed, _engineRev, _engineRevAngle, _engineRevSpeed, _vibFreq, _vibMin, _vibMax;
        float _roadNoise = 1f, _extraPitch, _rideHeightIn, _wheelWellIn, _hopScale = 1f, _steerSpeed = 180f, _maxTireSteer = 90f;
        float _camberFront, _camberRear;
        int _spokes;
        float _renderMotion = 1f;    // chassis RENDER_MOTION (Chassis::GetRenderMotion): the packet's ExtraBodyPitch / Roll

        // state (CarRenderConn members)
        readonly float[] _steering = new float[2];        // mSteering (rad): FL, FR
        float _animTime;                                   // mAnimTime (wraps at 10 s)
        Vector2 _extraBodyAngle;                           // mExtraBodyAngle (x roll, y pitch, rad)
        Vector3 _roadNoiseV, _flatTireAngle, _wheelHop;    // mRoadNoise, mFlatTireAngle, mWheelHop (x roll, y pitch, z lift)
        float _enginePower, _engineTorqueAngle, _engineVibrationAngle, _shiftPitchAngle, _shifting, _enginePitchAngle;
        int _lastGear = int.MinValue;
        Vector3 _lastVel, _vel, _acc;

        PlayerCar _car;
        Rigidbody _rb;
        CarRenderInfo _info;
        Transform _body;
        Transform[] _wheels, _spins, _tires;
        readonly WheelRenderOffset[] _offsets = new WheelRenderOffset[4];
        readonly Quaternion[] _tireBase = new Quaternion[4];
        readonly bool[] _tireBaseSet = new bool[4];
        // the sim's wheel pose (PlayerCar.Update writes Euler(0, steer, 0) and the hub position every frame; a car whose sim
        // does not run keeps ours): re-read only when the pivot changed under us
        readonly Quaternion[] _appliedWheelRot = new Quaternion[4];
        readonly Vector3[] _appliedWheelPos = new Vector3[4], _hub = new Vector3[4];
        readonly float[] _simSteerDeg = new float[4];
        readonly bool[] _wheelSeen = new bool[4];
        Vector3 _basePos, _appliedPos;
        Quaternion _baseRot = Quaternion.identity, _appliedRot = Quaternion.identity;
        bool _baseSet;
        static readonly Dictionary<uint, RoadNoise> NoiseCache = new Dictionary<uint, RoadNoise>();

        // tweaks (CarRenderConn.cpp statics)
        const float MaxDistForSteeringAnim = 100f, MaxDistForRoadNoiseAnim = 30f, MaxDistanceForEngineAnim = 30f;
        const float MaxDistanceForBodyAnim = 80f, MaxDistanceForWheelHopAnim = 30f, MaxDistanceForVehicleEffects = 80f, MaxDistanceForRenderBodyMotion = 80f;
        const float EngineAnimNoiseReduction = 0.005f, EngineAnimShiftMinAcc = 0.1f, EngineAnimShiftMaxAcc = 0.5f;
        const float EngineAnimShiftGearScale = 0.95f, EngineAnimShiftDownScale = 0.25f, EngineAnimShiftMinSpeed = 10f;
        const float BodyAnimNoiseReduction = 0.2f;
        const float WheelHopMinBurnout = 1f, WheelHopMaxBurnout = 5f, WheelHopMinBrake = 5f, WheelHopMaxBrake = 20f;
        const float WheelHopFrequency = 24f, WheelHopAngle = 0.2f, WheelHopTireAngle = 0.3f, WheelHopRollAngle = 0.15f;
        const float TireBlowOffset = -0.12f;
        /// <summary>Tweak_UseWheelHop / Tweak_AllowRenderBodyMotion / Tweak_DisableRoadNoise (NIS sets the last).</summary>
        public static bool UseWheelHop = true, AllowRenderBodyMotion = true, DisableRoadNoise;
        /// <summary>Wobble of a blown wheel: eRotateX(0x4FA) = 7.0° in the wheel's pre-roll frame (CarRenderInfo::Render; the GC 16° is unused).</summary>
        const float WobbleDeg = 7.0f;
        static readonly RoadNoise BlowOutNoise = new RoadNoise { Frequency = 4f, Amplitude = 1f, MinSpeed = 0f, MaxSpeed = 10f };
        const float Deg = Mathf.Deg2Rad, Inch = 0.0254f;

        /// <summary>mMaxWheelRenderDeltaAngle in degrees: 360 / (2·spokes) − 1 when the rim has a spoke count (OnLoaded
        /// 0x750B20: the rim part's SPOKE count, else |ecar WheelSpokeCount|), the ctor's 17° otherwise. PlayerCar.Update
        /// clamps the drawn roll per frame with it.</summary>
        public float MaxWheelRenderDeltaDeg { get; private set; } = 17f;

        public static CarRenderConn Attach(GameObject root, string car, CarRenderInfo info, Transform body, Transform[] wheels, int rimSpokes = 0)
        {
            var c = root.GetComponent<CarRenderConn>();
            if (!c) c = root.AddComponent<CarRenderConn>();
            c._info = info;
            c._body = body;
            c._wheels = wheels;
            c._spins = new Transform[4];
            c._tires = new Transform[4];
            for (int i = 0; i < 4 && wheels != null; i++)
            {
                if (!wheels[i]) continue;
                c._spins[i] = wheels[i].Find("Spin");
                c._offsets[i] = wheels[i].GetComponent<WheelRenderOffset>();
                if (c._spins[i]) c._tires[i] = c._spins[i].Find("Tire");
            }
            c.ReadAttributes(car);
            int spokes = rimSpokes != 0 ? rimSpokes : Mathf.Abs(c._spokes);
            if (spokes != 0) c.MaxWheelRenderDeltaDeg = 360f / (2f * spokes) - 1f;
            return c;
        }

        void ReadAttributes(string car)
        {
            var db = GameData.Attributes;
            if (db == null) return;
            var e = db.Get("ecar", (car ?? "").ToLowerInvariant()) ?? db.Get("ecar", "racers") ?? db.Get("ecar", "default");
            if (e != null)
            {
                _roll = Motion(e, "BodyRoll"); _squat = Motion(e, "BodySquat"); _dive = Motion(e, "BodyDive");
                _shiftAngle = e.Float("ShiftAngle"); _shiftSpeed = e.Float("ShiftSpeed");
                _engineRev = e.Float("EngineRev"); _engineRevAngle = e.Float("EngineRevAngle"); _engineRevSpeed = e.Float("EngineRevSpeed");
                _vibFreq = e.Float("EngineVibrationFreq"); _vibMin = e.Float("EngineVibrationMin"); _vibMax = e.Float("EngineVibrationMax");
                _roadNoise = e.Float("RoadNoise", 0, 1f); _extraPitch = e.Float("ExtraPitch");
                _rideHeightIn = e.Float("RideHeight"); _wheelWellIn = e.Float("WheelWell"); _hopScale = e.Float("WheelHopScale", 0, 1f);
                _steerSpeed = e.Float("SteerSpeed", 0, 180f); _maxTireSteer = e.Float("MaxTireSteer", 0, 90f);
                _camberFront = e.Float("CamberFront"); _camberRear = e.Float("CamberRear");
                if (e.Has("WheelSpokeCount")) _spokes = (sbyte)e.Source.Data[At(e, "WheelSpokeCount")];   // Int8
            }
            // Chassis::GetRenderMotion: the chassis part's RENDER_MOTION [?: the stock chassis collection named as the car]
            var ch = db.Get("chassis", (car ?? "").ToLowerInvariant());
            if (ch != null && ch.Has("RENDER_MOTION")) _renderMotion = ch.Float("RENDER_MOTION", 0, 1f);
        }

        static int At(AttribCollection c, string field) => c.TryGet(field, out int at, out _, out _) ? at : 0;

        static BodyMotion Motion(AttribCollection e, string f)
        {
            var v = e.Floats(f);
            return v.Length >= 3 ? new BodyMotion { DegPerG = v[0], MaxGs = v[1], DegPerSec = v[2] } : default;
        }

        /// <summary>simsurface RenderNoise {Frequency, Amplitude, MinSpeed, MaxSpeed} by collection key (RoadNoiseRecord).</summary>
        static RoadNoise NoiseOf(uint key)
        {
            if (NoiseCache.TryGetValue(key, out var n)) return n;
            var db = GameData.Attributes;
            if (db != null && db.Collections.TryGetValue((AttribDatabase.Hash("simsurface"), key), out var c))
            {
                var v = c.Floats("RenderNoise");
                if (v.Length >= 4) n = new RoadNoise { Frequency = v[0], Amplitude = v[1], MinSpeed = v[2], MaxSpeed = v[3] };
            }
            NoiseCache[key] = n;
            return n;
        }

        static float Ramp(float x, float lo, float hi) => hi <= lo ? (x >= hi ? 1f : 0f) : Mathf.Clamp01((x - lo) / (hi - lo));

        bool IsViewAnchor(Camera cam)
        {
            if (!cam) return false;
            if (_chaseCam != cam) { _chaseCam = cam; _chase = cam.GetComponent<ChaseCamera>(); }
            return _chase != null && _chase.Target && _chase.Target.IsChildOf(transform);
        }

        Camera _chaseCam;
        ChaseCamera _chase;
        float _distanceToView;
        bool _isAnchor, _nis;

        bool TestVisibility(float distance) => _nis || _isAnchor || _distanceToView <= distance;

        void ReadWheelPoses()
        {
            for (int i = 0; i < 4; i++)
            {
                if (_wheels == null || !_wheels[i]) continue;
                var t = _wheels[i];
                if (!_wheelSeen[i] || Quaternion.Angle(t.localRotation, _appliedWheelRot[i]) > 1e-3f)
                {
                    float y = t.localEulerAngles.y;
                    if (y > 180f) y -= 360f;
                    _simSteerDeg[i] = y;
                }
                if (!_wheelSeen[i] || (t.localPosition - _appliedWheelPos[i]).sqrMagnitude > 1e-10f) _hub[i] = t.localPosition;
                _wheelSeen[i] = true;
            }
        }

        void LateUpdate()
        {
            if (!_car) _car = GetComponent<PlayerCar>();
            if (!_rb) _rb = GetComponent<Rigidbody>();
            if (!_body || !_car) return;
            float dT = Time.deltaTime;
            if (dT <= 0f) return;
            ReadWheelPoses();
            var cam = Camera.main;
            _distanceToView = cam ? Vector3.Distance(cam.transform.position, transform.position) : 999999f;
            _isAnchor = IsViewAnchor(cam);
            _nis = Game.NisPlayer.Active != null;

            // BuildRenderMatrix: velocity, acceleration (world space) for the animations
            _vel = _rb ? _rb.linearVelocity : Vector3.zero;
            _acc = (_vel - _lastVel) / dT;
            _lastVel = _vel;
            float carspeed = _vel.magnitude;

            _animTime += dT;
            if (_animTime >= 10f) _animTime -= 10f;

            // E_UPSHIFT / E_DOWNSHIFT (OnEvent 0x756xxx): mShifting ±1 [?: forward gears only]
            int gear = _car.Gear;
            if (_lastGear != int.MinValue && gear != _lastGear && gear >= 1 && _lastGear >= 1) _shifting = gear > _lastGear ? 1f : -1f;
            _lastGear = gear;

            UpdateSteering(dT);
            UpdateTires(dT, carspeed);
            UpdateRoadNoise(carspeed);
            UpdateBodyAnimation(dT);
            UpdateEngineAnimation(dT);
            UpdateRenderMatrix();
        }

        // ---- UpdateSteering 0x7459F0 ------------------------------------------------------------------------------------

        void UpdateSteering(float dT)
        {
            if (!TestVisibility(MaxDistForSteeringAnim)) { _steering[0] = _steering[1] = 0f; }
            else
            {
                float maxsteer = _maxTireSteer * Deg, delta = _steerSpeed * Deg * dT;
                for (int i = 0; i < 2; i++)
                {
                    if (_wheels == null || !_wheels[i]) continue;
                    // the sim's steer: PlayerCar.Update put Euler(0, steer, 0) on the pivot this frame
                    float y = _simSteerDeg[i];
                    float target = y * Deg;
                    if (_steering[i] < target - delta) _steering[i] += delta;
                    else if (_steering[i] > target + delta) _steering[i] -= delta;
                    else _steering[i] = target;
                    _steering[i] = Mathf.Clamp(_steering[i], -maxsteer, maxsteer);
                }
            }
            // camber (CarRenderInfo::Render, body LOD <= B): eRotateX(bDegToAng(ecar Camber × 7°)) on the wheel matrix,
            // FL +, FR −, RR −, RL + (a tilt about the car's forward axis; Unity: about +Z with the opposite sign)
            bool camber = _info == null || _info.BodyLod <= 1;
            for (int i = 0; i < 4; i++)
            {
                if (_wheels == null || !_wheels[i]) continue;
                float steerDeg = i < 2 ? _steering[i] / Deg : 0f;
                float c = camber ? (i < 2 ? _camberFront : _camberRear) * 7f : 0f;
                bool left = (i & 1) == 0;
                var rot = Quaternion.AngleAxis(left ? -c : c, Vector3.forward) * Quaternion.Euler(0f, steerDeg, 0f);
                _wheels[i].localRotation = rot;
                _appliedWheelRot[i] = rot;
            }
        }

        // ---- UpdateTires 0x746750 (the parts PlayerCar does not draw: flat tyres, wobble, wheel hop) ---------------------

        void UpdateTires(float dT, float carspeed)
        {
            float hopRoll = 0f, hopPitch = 0f, tireHop = 0f;
            bool hopWheels = false, flatten = false;
            _flatTireAngle = Vector3.zero;
            _wheelHop = Vector3.zero;
            float extraBodyPitch = _renderMotion;
            if (TestVisibility(MaxDistanceForWheelHopAnim))
            {
                flatten = true;
                if (UseWheelHop && extraBodyPitch > 0f && _hopScale > 0f)
                {
                    float h2 = _hopScale * _hopScale;
                    hopRoll = h2 * extraBodyPitch * WheelHopRollAngle * Deg * Mathf.Abs(Mathf.Sin(_animTime + WheelHopFrequency * Mathf.PI * 0.5f));
                    hopPitch = h2 * extraBodyPitch * WheelHopAngle * Deg * Mathf.Abs(Mathf.Sin(_animTime * (WheelHopFrequency * 2f * Mathf.PI)));
                    tireHop = h2 * extraBodyPitch * WheelHopTireAngle * Deg * Mathf.Abs(Mathf.Sin((_animTime + 0.5f) * (WheelHopFrequency * 2f * Mathf.PI)));
                    hopWheels = true;
                }
            }
            bool candofx = TestVisibility(MaxDistanceForVehicleEffects);
            int blown = _car.BlownTires;
            float bodyY = _body.localPosition.y;
            for (int i = 0; i < 4; i++)
            {
                if (_wheels == null || !_wheels[i]) continue;
                _car.WheelState(i, out bool onground, out float slip, out float radius, out _);
                bool isFlat = (blown & (1 << i)) != 0;
                // tyre position in the render frame (game x forward, y left): Unity local (x right, z forward); the exe's
                // mTirePositions are the ecar TireOffsets, i.e. the drawn wheel (WheelRenderOffset adds ecar - sim)
                var hub = _hub[i];
                var drawnOff = _offsets != null && _offsets[i] ? _offsets[i].Offset : Vector3.zero;
                float tx = hub.z + drawnOff.z, ty = -(hub.x + drawnOff.x);
                float compression = 0f;
                if (flatten && isFlat)
                {
                    compression += TireBlowOffset;
                    // PC 0x746BF8: VU0_Atan2 (0x5CB5D0) returns (short)bATan · 2^-16, a fraction of a turn, times −π
                    // ([0x89120C]) / +π ([0x8B2B98]) = ∓0.5 × the angle in radians: the GC decomp's 0.5 exactly
                    float xAngle = Mathf.Atan2(-TireBlowOffset, Mathf.Abs(ty)) * -0.5f;
                    float yAngle = Mathf.Atan2(-TireBlowOffset, Mathf.Abs(tx)) * 0.5f;
                    if (ty < 0f) xAngle = -xAngle;
                    if (tx < 0f) yAngle = -yAngle;
                    _flatTireAngle.x += xAngle;
                    _flatTireAngle.y += yAngle;
                    _flatTireAngle.z += TireBlowOffset * 0.25f;
                }
                if (i > 1 && onground && hopWheels)
                {
                    float scale = slip > 0f ? Ramp(slip, WheelHopMinBurnout, WheelHopMaxBurnout) : Ramp(-slip, WheelHopMinBrake, WheelHopMaxBrake);
                    float frontX = _wheels[0] ? _wheels[0].localPosition.z : tx;
                    _wheelHop.y = hopPitch * scale;
                    _wheelHop.z = Mathf.Max(_wheelHop.z, frontX * Mathf.Sin(hopPitch * scale));
                    _wheelHop.x = hopRoll * scale;
                    compression += Mathf.Sin(tireHop * scale) * (frontX - tx);
                }
                var wp = hub + Vector3.up * compression;
                _wheels[i].localPosition = wp;
                _appliedWheelPos[i] = wp;
                // SetWheelWobble within 80 m: a blown wheel's axis is tilted 7° in its own pre-roll frame (precesses as it rolls)
                var tire = _tires[i];
                if (tire)
                {
                    if (!_tireBaseSet[i]) { _tireBase[i] = tire.localRotation; _tireBaseSet[i] = true; }
                    bool wobble = candofx && isFlat;
                    tire.localRotation = wobble ? Quaternion.AngleAxis(WobbleDeg, Vector3.forward) * _tireBase[i] : _tireBase[i];
                }
            }
        }

        // ---- UpdateRoadNoise 0x745C70 / AddRoadNoise 0x7390B0 ----------------------------------------------------------

        void AddRoadNoise(float speed, int tires, in RoadNoise noise)
        {
            if (noise.Frequency * noise.Amplitude * noise.MaxSpeed <= 0f) return;
            float intensity = Ramp(speed, noise.MinSpeed, noise.MaxSpeed);
            float frequency = noise.Frequency * intensity;
            float amplitude = _roadNoise * noise.Amplitude * Deg * intensity;
            const int front = 3, rear = 12, right = 10, left = 5;   // tyre bits FL 1, FR 2, RL 4, RR 8 (the decomp's 6/9 take FL FR RR RL order)
            int doFront = tires & front, doRear = tires & rear, doLeft = tires & left, doRight = tires & right;
            bool doRoll = (doFront | doRear) != 0;
            float noisePitch = 0f, noiseRoll = 0f;
            if (doRoll)
            {
                noisePitch = amplitude * (Mathf.Sin(_animTime * (frequency * 2f * Mathf.PI)) * 0.5f);
                if (doFront == 0) noisePitch = Mathf.Abs(noisePitch);
                if (doRear == 0) noisePitch = -Mathf.Abs(noisePitch);
                noiseRoll = amplitude * Mathf.Sin((_animTime + 0.33f) * (frequency * 2f * Mathf.PI));
                if (doRight == 0) noiseRoll = Mathf.Abs(noiseRoll);
                if (doLeft == 0) noiseRoll = -Mathf.Abs(noiseRoll);
            }
            _roadNoiseV.y += noisePitch;
            _roadNoiseV.x += noiseRoll;
        }

        void UpdateRoadNoise(float carspeed)
        {
            _roadNoiseV = Vector3.zero;
            if (!TestVisibility(MaxDistForRoadNoiseAnim) || DisableRoadNoise) return;
            RoadNoise roughness = default;
            int ground = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!_car.WheelState(i, out bool contact, out _, out _, out uint surface) || !contact) continue;
                ground |= 1 << i;
                var n = NoiseOf(surface);
                if (n.Amplitude * n.Frequency > roughness.Amplitude * roughness.Frequency) roughness = n;
            }
            AddRoadNoise(carspeed, ground, roughness);
            AddRoadNoise(carspeed, ground & _car.BlownTires, BlowOutNoise);
            float siny = Mathf.Sin(_roadNoiseV.y), sinx = Mathf.Sin(_roadNoiseV.x);
            float x0 = _wheels != null && _wheels[0] ? _wheels[0].localPosition.z : 0f, x3 = _wheels != null && _wheels[3] ? _wheels[3].localPosition.z : 0f;
            float y0 = _wheels != null && _wheels[0] ? -_wheels[0].localPosition.x : 0f, y1 = _wheels != null && _wheels[1] ? -_wheels[1].localPosition.x : 0f;
            _roadNoiseV.z = Mathf.Max(x0 * siny, 0f);
            _roadNoiseV.z = Mathf.Max(x3 * siny, _roadNoiseV.z);
            _roadNoiseV.z = Mathf.Max(y0 * sinx, _roadNoiseV.z);
            _roadNoiseV.z = Mathf.Max(y1 * sinx, _roadNoiseV.z);
        }

        // ---- UpdateBodyAnimation 0x746440 -------------------------------------------------------------------------------

        void UpdateBodyAnimation(float dT)
        {
            if (!TestVisibility(MaxDistanceForBodyAnim)) { _extraBodyAngle = Vector2.zero; return; }
            // accelerations along the render matrix's v1 (left) and v0 (forward), in g
            float leftAccel = Vector3.Dot(_acc, -transform.right) / 9.8f;
            float fwdAccel = Vector3.Dot(_acc, transform.forward) / 9.8f;
            var pitchControl = fwdAccel < 0f ? _dive : _squat;
            var rollControl = _roll;
            float maxPitch = _renderMotion * pitchControl.DegPerG * Deg, ratePitch = pitchControl.DegPerSec * Deg * dT;
            float maxRoll = _renderMotion * rollControl.DegPerG * Deg, rateRoll = rollControl.DegPerSec * Deg * dT;
            if (Mathf.Abs(leftAccel) < BodyAnimNoiseReduction) leftAccel = 0f;
            if (Mathf.Abs(fwdAccel) < BodyAnimNoiseReduction) fwdAccel = 0f;
            float destX = (maxRoll + maxRoll) * (Ramp(leftAccel, -rollControl.MaxGs, rollControl.MaxGs) - 0.5f);
            float destY = (maxPitch + maxPitch) * (Ramp(-fwdAccel / 9.8f, -pitchControl.MaxGs, pitchControl.MaxGs) - 0.5f);   // (the exe divides by g twice)
            if (_vel.magnitude < 1f) { destX = 0f; destY = 0f; }
            if (destX > _extraBodyAngle.x) _extraBodyAngle.x = Mathf.Min(_extraBodyAngle.x + rateRoll, destX);
            else if (destX < _extraBodyAngle.x) _extraBodyAngle.x = Mathf.Max(_extraBodyAngle.x - rateRoll, destX);
            if (destY > _extraBodyAngle.y) _extraBodyAngle.y = Mathf.Min(_extraBodyAngle.y + ratePitch, destY);
            else if (destY < _extraBodyAngle.y) _extraBodyAngle.y = Mathf.Max(_extraBodyAngle.y - ratePitch, destY);
        }

        // ---- UpdateEngineAnimation 0x745F20 -----------------------------------------------------------------------------

        void UpdateEngineAnimation(float dT)
        {
            if (!TestVisibility(MaxDistanceForEngineAnim))
            {
                _enginePower = _engineVibrationAngle = _engineTorqueAngle = _shiftPitchAngle = _shifting = _enginePitchAngle = 0f;
                return;
            }
            if (_shifting != 0f)
            {
                float carSpeed = _vel.magnitude, shiftSpeed = _shiftSpeed * Deg, maxPitch = _shiftAngle * Deg;
                int gear = _car.Gear - 1;   // mGear − G_FIRST
                if (shiftSpeed > 0f && maxPitch > 0f && gear >= 0 && carSpeed > EngineAnimShiftMinSpeed)
                {
                    float fwdAccel = Vector3.Dot(_acc, transform.forward) / 9.8f;
                    float accelRatio = Ramp(Mathf.Abs(fwdAccel), EngineAnimShiftMinAcc, EngineAnimShiftMaxAcc);
                    float gearRatio = Mathf.Pow(EngineAnimShiftGearScale, gear);
                    // UMath::Sina(|shifting|·0.5): the angle is a fraction of a turn
                    _shiftPitchAngle = Mathf.Sin(Mathf.Abs(_shifting) * 0.5f * 2f * Mathf.PI) * maxPitch * gearRatio * accelRatio;
                    if (_shifting > 0f) _shifting = Mathf.Max(_shifting - dT * shiftSpeed / maxPitch, 0f);
                    else if (_shifting < 0f) { _shiftPitchAngle *= EngineAnimShiftDownScale; _shifting = Mathf.Min(_shifting + dT * shiftSpeed / maxPitch, 0f); }
                }
                else { _shifting = 0f; _shiftPitchAngle = 0f; }
            }
            else _shiftPitchAngle = 0f;

            // DrawPerformanceCar::OnService (DrawCar.cpp:337): EnginePower / EngineSpeed only while the gear is reverse /
            // neutral or staging (GetGear() < 2 || IsStaging), else the packet keeps 0: speed = Ramp(rpm, min, MAX_RPM),
            // power = Ramp(GetHorsePower, GetMinHorsePower, GetMaxHorsePower) with EngineRacer::GetHorsePower =
            // NM2HP(GetEngineTorque(rpm) · throttle, rpm), GetMinHorsePower = FTLB2HP(Torque(IDLE) · IDLE) and mMaxHP =
            // MaxInductedPower (0x66BFA0) [? the engine torque's induction / NOS boost is not visible here: stock torque]
            float enginePowerIn = 0f, engineSpeedIn = 0f;
            if (_car.Gear < 1 || _car.Staging)
            {
                var s = _car.Spec;
                if (s != null)
                {
                    engineSpeedIn = Ramp(_car.Rpm, s.IdleRpm, s.MaxRpm);
                    const float Hp = 1f / 5252.113f;   // ft·lb · rpm → hp (FTLB2HP; NM2HP of the same torque in Nm agrees)
                    float hp = s.TorqueAt(_car.Rpm) * Mathf.Clamp01(_car.Input.Throttle) * _car.Rpm * Hp;
                    float minHp = s.TorqueAt(s.IdleRpm) * s.IdleRpm * Hp;
                    enginePowerIn = Ramp(hp, minHp, Dynamics.PhysicsInfo.MaxInductedPower(s));
                }
            }
            float delta = enginePowerIn - _enginePower;
            if (Mathf.Abs(delta) < EngineAnimNoiseReduction) delta = 0f;
            float revAccel = delta / dT;
            _enginePower = Mathf.Clamp01(_enginePower + delta);
            _enginePitchAngle = 0f;   // mAnimatedCarPitch (ENISCarPitch): Runtime/Nis drives its own tweak [?]
            float maxRev = enginePowerIn * engineSpeedIn * _engineRevAngle * Deg;
            float revSpeed = _engineRevSpeed * Deg;
            float desired = maxRev * Ramp(revAccel * _engineRev, 0f, 0.2f);
            if (_engineTorqueAngle < desired) _engineTorqueAngle = Mathf.Min(_engineTorqueAngle + revSpeed * dT, desired);
            else _engineTorqueAngle = Mathf.Max(_engineTorqueAngle - revSpeed * dT, desired);
            _engineTorqueAngle = Mathf.Clamp(_engineTorqueAngle, 0f, maxRev);
            _engineVibrationAngle = engineSpeedIn * Mathf.Sin(_animTime * 2f * Mathf.PI * _vibFreq) * (_vibMin * Deg + _vibMax * Deg * engineSpeedIn);
        }

        // ---- UpdateRenderMatrix 0x747050 -------------------------------------------------------------------------------

        void UpdateRenderMatrix()
        {
            // the base pose: PlayerCar.Setup's Body placement; re-captured when someone moved the body under us
            if (!_baseSet || (_body.localPosition - _appliedPos).sqrMagnitude > 1e-8f || Quaternion.Angle(_body.localRotation, _appliedRot) > 1e-3f)
            {
                _basePos = _body.localPosition;
                _baseRot = _body.localRotation;
                _baseSet = true;
            }
            float roll = 0f, pitch = 0f, ride = 0f;
            if (AllowRenderBodyMotion && TestVisibility(MaxDistanceForRenderBodyMotion))
            {
                roll = _wheelHop.x + _extraBodyAngle.x + _flatTireAngle.x + _roadNoiseV.y + _engineTorqueAngle + _engineVibrationAngle;   // (the exe adds mRoadNoise.y to both)
                pitch = _wheelHop.y + _extraBodyAngle.y + _flatTireAngle.y + _roadNoiseV.y + _enginePitchAngle + _shiftPitchAngle + _extraPitch * Deg;
                ride = _wheelHop.z + _roadNoiseV.z + _flatTireAngle.z + _rideHeightIn * Inch;
                // the wheel-well clamp: the highest tyre top over INCH2METERS(WheelWell) raises the body by the excess (the
                // tyres keep their place)
                float wheelWell = _wheelWellIn * Inch;
                if (wheelWell > 0f && _wheels != null)
                {
                    float top = float.MinValue;
                    for (int i = 0; i < 4; i++)
                    {
                        if (!_wheels[i]) continue;
                        _car.WheelState(i, out _, out _, out float radius, out _);
                        top = Mathf.Max(top, _wheels[i].localPosition.y - (_basePos.y + ride) + radius);
                    }
                    if (top > wheelWell) ride += top - wheelWell;
                }
            }
            // game: eRotateX(roll) about the forward axis (right-handed, Y left) then eRotateY(pitch) about the left axis;
            // Unity: roll about +Z with the opposite sign, pitch about +X (nose down for +pitch in both)
            var rot = _baseRot * Quaternion.AngleAxis(pitch * Mathf.Rad2Deg, Vector3.right) * Quaternion.AngleAxis(-roll * Mathf.Rad2Deg, Vector3.forward);
            var pos = _basePos + _baseRot * (Vector3.up * ride);
            _body.localRotation = rot;
            _body.localPosition = pos;
            _appliedRot = rot;
            _appliedPos = pos;
        }
    }
}
