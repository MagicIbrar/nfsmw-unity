using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NfsMw
{
    /// <summary>Driver inputs, from the keyboard/gamepad or set by code (AI, tests).</summary>
    public struct DriveInput
    {
        public float Throttle, Brake, Steer;
        public bool Handbrake, Nitrous, Reset;
        /// <summary>Manual gear changes (one shot; used by the drag races' manual gearbox).</summary>
        public bool ShiftUp, ShiftDown;
        /// <summary>Speedbreaker button press (one shot; the car clears it once handled).</summary>
        public bool SpeedBreaker;

        /// <summary>
        /// slot 0: every device (single player; X or the right shoulder toggles the speedbreaker). Split screen: slot 1 =
        /// W A S D, Space handbrake, Left Shift nitrous, R reset and the first gamepad when two are connected; slot 2 =
        /// arrows, Right Ctrl handbrake, Right Shift nitrous, Backspace reset and the last gamepad.
        /// </summary>
        public static DriveInput FromDevices(int slot = 0)
        {
            if (slot != 0) return FromSlot(slot);
            var i = new DriveInput();
            var k = Keyboard.current;
            if (k != null)
            {
                if (k.wKey.isPressed || k.upArrowKey.isPressed) i.Throttle = 1;
                if (k.sKey.isPressed || k.downArrowKey.isPressed) i.Brake = 1;
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) i.Steer -= 1;
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) i.Steer += 1;
                i.Handbrake = k.spaceKey.isPressed;
                i.Nitrous = k.nKey.isPressed || k.leftCtrlKey.isPressed;
                i.Reset = k.rKey.wasPressedThisFrame;
                i.SpeedBreaker = k.xKey.wasPressedThisFrame;
                i.ShiftUp = k.eKey.wasPressedThisFrame;
                i.ShiftDown = k.qKey.wasPressedThisFrame;
            }
            var g = Gamepad.current;
            if (g != null)
            {
                i.Throttle = Mathf.Max(i.Throttle, g.rightTrigger.ReadValue());
                i.Brake = Mathf.Max(i.Brake, g.leftTrigger.ReadValue());
                float sx = g.leftStick.ReadValue().x;
                if (Mathf.Abs(sx) > Mathf.Abs(i.Steer)) i.Steer = sx;
                i.Handbrake |= g.buttonSouth.isPressed;
                i.Nitrous |= g.buttonEast.isPressed;
                i.Reset |= g.selectButton.wasPressedThisFrame;
                i.SpeedBreaker |= g.rightShoulder.wasPressedThisFrame;
                i.ShiftUp |= g.buttonNorth.wasPressedThisFrame;
                i.ShiftDown |= g.buttonWest.wasPressedThisFrame;
            }
            return i;
        }

        static DriveInput FromSlot(int slot)
        {
            var i = new DriveInput();
            var k = Keyboard.current;
            if (k != null)
            {
                bool p1 = slot == 1;
                if (p1 ? k.wKey.isPressed : k.upArrowKey.isPressed) i.Throttle = 1;
                if (p1 ? k.sKey.isPressed : k.downArrowKey.isPressed) i.Brake = 1;
                if (p1 ? k.aKey.isPressed : k.leftArrowKey.isPressed) i.Steer -= 1;
                if (p1 ? k.dKey.isPressed : k.rightArrowKey.isPressed) i.Steer += 1;
                i.Handbrake = p1 ? k.spaceKey.isPressed : k.rightCtrlKey.isPressed;
                i.Nitrous = p1 ? k.leftShiftKey.isPressed : k.rightShiftKey.isPressed;
                i.Reset = p1 ? k.rKey.wasPressedThisFrame : k.backspaceKey.wasPressedThisFrame;
            }
            var pads = Gamepad.all;
            Gamepad g = pads.Count >= 2 ? pads[slot == 1 ? 0 : pads.Count - 1] : slot == 2 && pads.Count == 1 ? pads[0] : null;
            if (g != null)
            {
                i.Throttle = Mathf.Max(i.Throttle, g.rightTrigger.ReadValue());
                i.Brake = Mathf.Max(i.Brake, g.leftTrigger.ReadValue());
                float sx = g.leftStick.ReadValue().x;
                if (Mathf.Abs(sx) > Mathf.Abs(i.Steer)) i.Steer = sx;
                i.Handbrake |= g.buttonSouth.isPressed;
                i.Nitrous |= g.buttonEast.isPressed;
                i.Reset |= g.selectButton.wasPressedThisFrame;
            }
            return i;
        }
    }

    /// <summary>
    /// The racer simulation of speed.exe (EngineRacer + SuspensionRacer, specs/physics.md) on raycast wheels at 120 Hz:
    /// torque curve with boost and nitrous, clutch and computed shift points, limited-slip differentials, progressive
    /// springs with digressive dampers and sway bars, the load-sensitive lateral tire tables inside a friction circle with
    /// wheel-spin dynamics, speed-scaled steering tables with Ackermann, YAW_CONTROL, the drift state, aero, air control
    /// and the speedbreaker (world time ×0.25 and its handling blend).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed partial class PlayerCar : MonoBehaviour
    {
        public CarSpec Spec;
        public Transform[] WheelVisuals = new Transform[4];
        public bool UseDeviceInput = true;
        /// <summary>Which controls drive it (DriveInput.FromDevices).</summary>
        public int InputSlot;
        public List<Renderer> BrakeLights;
        public DriveInput Input;
        /// <summary>0..1: spike-strip damage (blown tires: grip ×0.3, rim drag) until the pursuit ends.</summary>
        public float TireDamage;
        /// <summary>Wheels whose tire is BLOWN (bit i = wheel i, FL FR RL RR): spike strips (Police.TireState).</summary>
        public int BlownTires;
        /// <summary>A traffic car (no drift state).</summary>
        public bool Traffic;
        /// <summary>Driven by an AIDriver: DoAISteering (STEERING·45°·input, no speed tables) and no auto-brake.</summary>
        public bool AiControlled;
        /// <summary>Engine output factor (Game.SabotageEngine sets it to zero: the engine is blown).</summary>
        public float PowerScale = 1f;

        // ---- drag racing (EngineDragster, EngineRacer.cpp:1339-1645; DamageDragster.cpp) ----
        public enum ShiftPotential { None, Down, Up, Good, Perfect, Miss }
        public enum ShiftStatus { None, Normal, Good, Perfect, Missed }
        /// <summary>A drag race car: no reverse, and the player's car gets the manual gearbox and shift ratings.</summary>
        public bool Dragster;
        /// <summary>On the start line before GO: held on the brakes, clutch open, rev limiter at the redline (IsStaging).</summary>
        public bool Staging;
        /// <summary>The player's drag gearbox: manual, rated shifts, over-rev heat (the AI keeps EngineRacer's).</summary>
        public bool ManualGears => Dragster && !AiControlled;
        public ShiftPotential Potential { get; private set; }
        /// <summary>The last rated gear change and a counter the HUD watches for new ones.</summary>
        public ShiftStatus LastShift { get; private set; }
        public int ShiftCount { get; private set; }
        public float EngineHeat { get; private set; }
        public float OverRev { get; private set; }
        public bool EngineBlown { get; private set; }
        /// <summary>A drag car hit something at over 100 mph and lost over 40 mph (DamageDragster::CheckTotaling).</summary>
        public bool Totaled { get; private set; }
        /// <summary>Perfect launch strength, 1 at GO, fading over 4.5 s.</summary>
        public float PerfectLaunch { get; private set; }
        float _potentialBonus, _perfectShiftTime, _shiftBoost, _launchTimer;
        int _pendingShift;
        const float PerfectShiftSplit = 3f, PerfectShiftBoost = 0.75f, GoodShiftRangeMult = 1.5f;
        const float EngineHeatUpRedLine = 0.45f, EngineHeatCoolDown = -0.1f, EngineHeatPenaltyThreshold = 0.6f, EngineHeatTorquePenalty = 0.75f;
        const float PerfectLaunchTime = 4.5f, DragKillSpeedMph = 100f, DragKillSpeedChangeMph = 40f;
        /// <summary>Speedbreaker charge 0..1 (the HUD bar).</summary>
        public float SpeedBreaker = 1f;

        public int Gear { get; private set; } = 1; // -1 reverse, 0 neutral, 1.. forward
        public float Rpm { get; private set; }
        public float SpeedMps => _rb ? _rb.linearVelocity.magnitude : 0;
        public float ForwardSpeed => _rb ? Vector3.Dot(_rb.linearVelocity, transform.forward) : 0;
        public float Nos { get; private set; } = 1;
        /// <summary>Induction boost for gauges (psi).</summary>
        public float BoostPsi { get; private set; }
        public bool SpeedBreakerOn { get; private set; }
        /// <summary>Nitrous is burning this tick.</summary>
        public bool NosFiring => _nosOn;
        /// <summary>0..1: drift state (§10).</summary>
        public float Drift => _drift;
        /// <summary>Tire noise level: sideways scrub, wheel spin and locked wheels (0 = rolling).</summary>
        public float SkidAmount { get; private set; }
        public bool Grounded { get; private set; }

        /// <summary>One wheel's state for effects and sound (0 FL, 1 FR, 2 RL, 3 RR): contact, longitudinal slip, tyre
        /// radius and the simsurface key under it.</summary>
        public bool WheelState(int i, out bool contact, out float slip, out float radius, out uint surface)
        {
            contact = false; slip = 0; radius = 0.33f; surface = 0;
            if (_w == null || i < 0 || i >= _w.Length || _w[i] == null) return false;
            var w = _w[i];
            contact = w.Contact;
            slip = w.SlipX;
            radius = w.Radius;
            surface = w.Surface.Key;
            return true;
        }
        /// <summary>Wheels touching the ground this tick (0..4).</summary>
        public int WheelsOnGround { get; private set; }

        /// <summary>[ours] Feeds the read-outs (engine audio, lights, skids) from another sim while this component is
        /// disabled: traffic runs SuspensionTraffic/EngineTraffic (TrafficPhysics/TrafficVehicle.cs).</summary>
        public void SetExternalSimState(float rpm, int gear, int wheelsOnGround, float skid)
        {
            Rpm = rpm;
            Gear = gear;
            WheelsOnGround = wheelsOnGround;
            Grounded = wheelsOnGround > 0;
            SkidAmount = skid;
        }
        /// <summary>Height of the car origin above the road at rest (the wheels' design pose puts the road at local y = 0).</summary>
        public float RestLift => 0f;

        // ---- units and fixed tables (speed.exe .data) -----------------------------------------------------------
        const float IN = 0.0254f, LBFT = 1.3558f, LBIN = 175.1268f, RPM2RAD = 0.10471976f, RAD2RPM = 9.549297f;
        const float WheelInertia = 10f, RollingResistance = 1f;
        public const float SimRate = 120f;

        // lateral force tables T0..T6 over load x = 0,2..10 (0x68E090), one per 2° of slip angle
        static readonly float[][] LatTables =
        {
            new float[] { 0, 0, 0, 0, 0, 0 },
            new float[] { 0, 1.2f, 2.3f, 3.0f, 3.0f, 2.8f },
            new float[] { 0, 1.7f, 3.2f, 4.3f, 5.1f, 5.2f },
            new float[] { 0, 1.8f, 3.5f, 4.9f, 5.8f, 6.1f },
            new float[] { 0, 1.83f, 3.6f, 5.0f, 5.96f, 6.4f },
            new float[] { 0, 1.86f, 3.7f, 5.1f, 6.13f, 6.7f },
            new float[] { 0, 1.9f, 3.8f, 5.2f, 6.3f, 7.1f },
        };
        static readonly float[] LatScaleTable = { 0.833f, 0.958f, 1.008f, 1.0167f, 1.033f, 1.033f, 1.033f, 1.0167f, 1f, 1f };   // 0x8f8e08 over V 0..38 m/s
        static readonly float[] GripTable = { 0.909f, 1.045f, 1.09f, 1.09f, 1.09f, 1.09f, 1.09f, 1.045f, 1f, 1f };               // 0x8f8df4
        static readonly float[] SteerMaxTable = { 40f, 20f, 10f, 5.5f, 4.5f, 3.25f, 2.9f, 2.9f, 2.9f, 2.9f };                   // 0x8f8f30 over vz 0..160 m/s
        static readonly float[] SteerGasTable = { 1f, 1f, 1f, 0.56f, 0.5f, 0.35f, 0.3f, 0.3f, 0.3f, 0.3f };                     // 0x8f8f6c
        static readonly float[] SteerRateTable = { 1f, 1.05f, 1.1f, 1.5f, 2.2f, 3.1f };                                          // over |Δin|/dt 0..10
        static readonly float[] SteerInputTable = { 1f, 1.05f, 1.1f, 1.2f, 1.3f, 1.4f };                                         // over |InputAverage| 0..1
        static readonly float[] DriftGripTable = { 1.1f, 0.95f, 0.87f, 0.77f, 0.67f, 0.6f, 0.51f, 0.43f, 0.37f, 0.34f };      // 0x8f8fb0 over 0..1
        static readonly Vector2[] DriftYawGraph =                                                                                // 0x938210
        {
            new Vector2(0, 0), new Vector2(0.2618f, 0.1f), new Vector2(0.5236f, 0.45f), new Vector2(0.7854f, 0.85f),
            new Vector2(1.0472f, 0.95f), new Vector2(1.5533f, 1.15f), new Vector2(1.5708f, 0),
        };

        sealed class Wheel
        {
            public int Index;
            public bool Front, Left;
            public Vector3 Droop;               // local hub position at full extension
            public float Radius, Travel, Comp, PrevComp, Load;
            public bool Contact, Locked;
            public float Omega, SpinDeg, Steer;
            public float Traction = 1, TauPrev, PrevVx, Drive;
            public float SlipX, AlphaDeg, Vx;
            public Vector3 Normal;
            /// <summary>The simsurface under the wheel (WorldCollision.SurfaceAt; default off the world ground).</summary>
            public WorldCollision.Surface Surface = WorldCollision.Surface.Default;
            /// <summary>The wheel's ground is a world face (its WWorldPos surface is not SimSurface::kNull): PVehicle's
            /// off-world test counts the wheels without one (0x680840).</summary>
            public bool OnWorld;
        }

        Rigidbody _rb;
        readonly Wheel[] _w = new Wheel[4];
        readonly Transform[] _spin = new Transform[4];
        static readonly RaycastHit[] Hits = new RaycastHit[12];
        Vector3 _resetPos;
        Quaternion _resetRot;

        // engine / driveline
        float _we, _ie, _shiftTimer, _clutchTimer, _clutchTotal, _spool, _nosDisengage, _peakRpm, _peakTorqueNm;
        // rigid body (RigidBody.cpp / RBVehicle.cpp)
        const float LinearBound = 300f, AngularBound = 30f;   // DoIntegration's per-component UMath::Bound
        Vector3 _stageHold;
        bool _stageHeld;
        int _clutch;                         // 0 locked, 1 engaging, 2 open
        float[] _up, _down;
        // chassis and assists
        float _steerDeg, _steerInPrev, _steerRate, _hb = 1f, _drift, _sb, _airTime, _rearAlpha;
        int _driftState, _lastContacts = 4;
        float _lastDrive, _nosMult = 1f, _lastMaxSteer = 45f;
        bool _nosOn;
        // InputAverage (0.55 s) of the steering input and InputSpeedCoeffAverage (0.15 s) of its speed coefficient
        readonly Queue<(float t, float v)> _steerAvg = new Queue<(float, float)>(), _steerSpeedAvg = new Queue<(float, float)>();
        float _simTime, _steerAvgSum, _steerSpeedSum;

        public void Setup(CarSpec spec, Transform[] wheelVisuals)
        {
            Spec = spec;
            WheelVisuals = wheelVisuals;
            if (Time.fixedDeltaTime > 1f / SimRate + 1e-5f) Time.fixedDeltaTime = 1f / SimRate;
            _rb = GetComponent<Rigidbody>();
            _rb.mass = spec.Mass;
            _rb.linearDamping = 0f;
            // RigidBody::DoIntegration (0x670B90 range, RigidBody.cpp:657-709): no damping (NATURAL_ANGULAR_DAMPING 0 for
            // every car spec), each angular component bounded to ±30 rad/s (done in Step; PhysX only caps the magnitude)
            _rb.angularDamping = spec.Body != null ? spec.Body.NaturalAngularDamping : 0f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.maxAngularVelocity = AngularBound * 1.7320508f;

            // CG (0x68D800): FRONT_WEIGHT_BIAS between the axles, height from ROLL_CENTER over the ride height
            float zf = spec.FrontAxle, zr = spec.FrontAxle - spec.WheelBase;
            float hubY = (wheelVisuals[0].localPosition.y + wheelVisuals[2].localPosition.y) * 0.5f;
            float cgY = Mathf.Clamp(hubY + (spec.RollCenter - Mathf.Max(spec.RideHeightFront, spec.RideHeightRear)) * IN, 0.2f, 0.6f);
            _rb.centerOfMass = new Vector3(0, cgY, zr + (zf - zr) * spec.FrontWeightBias * 0.01f);
            // inertia: the collision box ×TENSOR_SCALE (1, 2, 1: twice the yaw inertia)
            var box = GetComponent<BoxCollider>();
            var sz = box ? box.size : new Vector3(1.8f, 1.3f, 4.4f);
            // the car's own collision (Bounds.cpp / CollisionGeometry, GLOBALB): its primitives instead of the render
            // bounds, and the RigidBody dimension (root half extents) for Util_GenerateCarTensor (0x6E8F50)
            var bodyModel = transform.Find("Body");
            var geometry = Dynamics.CollisionBounds.UseCollisionGeometry ? Dynamics.CollisionBounds.Vehicle(spec.Name) : null;
            HasCollisionGeometry = false;
            if (geometry != null && box && bodyModel && Dynamics.CollisionBounds.BuildVehicle(geometry, box, bodyModel, _prims, out var halfDims, out _geomPivot, out _primaryOffset, out _primHolder))
            {
                sz = 2f * halfDims;
                HasCollisionGeometry = true;
            }
            float m12 = spec.Mass / 12f;
            _rb.inertiaTensor = Vector3.Scale(new Vector3(m12 * (sz.y * sz.y + sz.z * sz.z), m12 * (sz.x * sz.x + sz.z * sz.z), m12 * (sz.x * sz.x + sz.y * sz.y)), spec.TensorScale);
            _rb.inertiaTensorRotation = Quaternion.identity;
            _box = box;
            CarContacts.Register(this, box, _rb, spec);
            if (!GetComponent<Game.CarReset>()) gameObject.AddComponent<Game.CarReset>();

            float sag = 0f;
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                var w = new Wheel { Index = i, Front = front, Left = (i & 1) == 0 };
                w.Radius = front ? spec.TireRadiusFront : spec.TireRadiusRear;
                w.Travel = Mathf.Max(0.06f, (front ? spec.TravelFront : spec.TravelRear) * IN);
                // static compression: k·x·(1 + p·x) = corner weight
                float load = spec.Mass * 9.81f * 0.5f * (front ? spec.FrontWeightBias : 100f - spec.FrontWeightBias) * 0.01f;
                float k = Mathf.Max(1000f, (front ? spec.SpringFront : spec.SpringRear) * LBIN), p = front ? spec.SpringProgFront : spec.SpringProgRear;
                float xeq = p > 1e-4f ? (-k + Mathf.Sqrt(k * k + 4f * p * k * load)) / (2f * p * k) : load / k;
                xeq = Mathf.Min(xeq, w.Travel * 0.8f);
                w.Droop = wheelVisuals[i].localPosition - Vector3.up * xeq;
                w.Comp = w.PrevComp = xeq;
                _w[i] = w;
                _spin[i] = wheelVisuals[i].Find("Spin");
                sag += xeq * 0.25f;
            }
            // The exe's body origin (the collision box bottom: Bounds root pivot.y = half height) rides at RIDE_HEIGHT minus
            // the static sag above the road. This sim's design pose has the road at root y = 0, so the collision goes there,
            // whatever height CarBuilder drew the body at (it lifts it by RIDE_HEIGHT x 0.01, inches read as cm, 4-10 cm
            // too low; the lower body box then sat 6 cm over the road, touched it under squat and dragged the car with the
            // wheels unloaded: physics.md section 20)
            float rideY = (spec.RideHeightFront + spec.RideHeightRear) * 0.5f * IN - sag;
            // the exe draws the body at the same height (DrawCar: RIDE_HEIGHT in inches minus the static sag); re-place the
            // drawn Body before the collision, since the prim holder hangs under it
            if (bodyModel) bodyModel.localPosition = new Vector3(bodyModel.localPosition.x, rideY, bodyModel.localPosition.z);
            if (HasCollisionGeometry) PlaceCollisionAtOrigin(rideY);
            _ie = Dynamics.PhysicsInfo.EngineInertia(spec, true);
            _we = spec.IdleRpm * RPM2RAD;
            Rpm = spec.IdleRpm;
            _clutch = 2;
            ShiftPoints();
            _resetPos = transform.position;
            _resetRot = transform.rotation;
        }

        public void SetResetPoint(Vector3 pos, Quaternion rot)
        {
            _resetPos = pos;
            _resetRot = rot;
        }

        /// <summary>Manual reset: ResetCar's recorded spots (snapped to the road) when it has one, else the reset point.</summary>
        public void ResetCar()
        {
            var auto = GetComponent<Game.CarReset>();
            if (!(auto && auto.ResetVehicle(true))) Teleport(_resetPos + Vector3.up * 0.5f, _resetRot);
            Gear = 1;
        }

        // ---- damage glue (IEngineDamage on EngineRacer, IDamageable on DamageRacer/DamageDragster) ---------------------

        const float EngineDamageFrequency = 12f, EngineDamageAmplitude = 0.5f;   // EngineRacer.cpp:1089-1090
        float _sabotageAt;
        CarRender.CarDamage _damage;
        int _damageFrame = -1;

        /// <summary>The DamageVehicle state of this car (CarDamage, the car-render port), when it has one.</summary>
        CarRender.CarDamage DamageState
        {
            get
            {
                if (!_damage && _damageFrame != Time.frameCount) { _damageFrame = Time.frameCount; _damage = GetComponent<CarRender.CarDamage>(); }
                return _damage;
            }
        }

        /// <summary>
        /// IVehicle::IsDestroyed: DamageVehicle's total ≥ 1 (EVehicleDestroyed) or a drag car totaled. Racers' damagespecs
        /// ("racers") have HIT_POINTS 0 and HitPointScale 0 on every zone, so hits never destroy a racer; only
        /// Destroy() does (DamageDragster::CheckTotaling, Game.TotalRacer).
        /// </summary>
        public bool Destroyed => Totaled || (DamageState && DamageState.Destroyed);

        /// <summary>IEngineDamage::IsSabotaged: a breakdown is counting down.</summary>
        public bool Sabotaged => _sabotageAt > 0;

        /// <summary>Game.SabotageEngine without a delay [ours: the Story flow's call]: the engine blows now.</summary>
        public void Sabotage() { Blow(); PowerScale = 0f; }

        /// <summary>
        /// EngineRacer::Sabotage (EngineRacer.cpp:467-471): after `time` seconds the engine blows; until then its speed
        /// shakes (see Engine). Ignored when one is already running or the engine is blown.
        /// </summary>
        public void Sabotage(float time)
        {
            if (_sabotageAt <= 0 && time > 1e-6f && !EngineBlown) _sabotageAt = _simTime + time;
        }

        /// <summary>EngineRacer::Blow (EngineRacer.cpp:473-481): blown (no throttle, Chassis holds the brakes; EEngineBlown).</summary>
        public bool Blow()
        {
            if (EngineBlown) return false;
            EngineBlown = true;
            _sabotageAt = 0;
            return true;
        }

        /// <summary>
        /// DamageVehicle::Destroy (DamageVehicle.cpp:281-292): every zone to 6 and the total to 1 (CarDamage), and this
        /// car counts as destroyed.
        /// </summary>
        public void Destroy()
        {
            Totaled = true;
            PowerScale = 0f;
            if (DamageState) DamageState.Destroy();
        }

        /// <summary>
        /// DamageRacer/DamageVehicle::ResetDamage (DamageVehicle.cpp:294-317, DamageRacer.cpp:110-114): visual damage and
        /// hit points cleared (CarDamage), the engine repaired (EngineRacer::Repair: sabotage and blown cleared).
        /// </summary>
        public void ResetDamage()
        {
            if (DamageState) DamageState.ResetDamage();
            _sabotageAt = 0;
            EngineBlown = false;
            EngineHeat = 0;
            Totaled = false;
            PowerScale = 1f;
        }

        /// <summary>Moves the body and the rigidbody together (an interpolated body would otherwise snap back).</summary>
        public void Teleport(Vector3 pos, Quaternion rot)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(pos, rot);
            _rb.position = pos;
            _rb.rotation = rot;
            Physics.SyncTransforms();
            foreach (var w in _w)
                if (w != null) { w.Omega = 0; w.Traction = 1; w.TauPrev = 0; w.PrevVx = 0; w.PrevComp = w.Comp; w.Locked = false; }
            _drift = 0;
            _driftState = 0;
            _stageHeld = false;
        }

        /// <summary>Read-only wheel state for the audio port (SFXCTL_Wheel): slip speed m/s, slip angle, contact speed, load N, contact, simsurface key.</summary>
        public (float slip, float alphaDeg, float vx, float load, bool contact, uint surface, bool front, bool left) WheelAudio(int i) { var w = i >= 0 && i < 4 ? _w[i] : null; return w == null ? default : (w.SlipX, w.AlphaDeg, w.Vx, w.Load, w.Contact, w.Surface.Key, w.Front, w.Left); }

        /// <summary>Per-wheel debug line for the editor drive test.</summary>
        public string DebugWheels()
        {
            var sb = new System.Text.StringBuilder($"clutch {_clutch} drive {_lastDrive:0}Nm drift {_drift:0.00} | ");
            foreach (var w in _w)
                if (w != null)
                    sb.Append($"{w.Omega * RAD2RPM:0}rpm/s{w.SlipX:0.0}/a{w.AlphaDeg:0.0}/t{w.Traction:0.00}/{w.Drive:0}Nm/{w.Load:0}N{(w.Locked ? "L" : "")}{(w.Contact ? "" : "(air)")} ");
            return sb.ToString();
        }

        CarRender.CarRenderConn _renderConn;

        void Update()
        {
            if (!_renderConn) _renderConn = GetComponent<CarRender.CarRenderConn>();
            if (UseDeviceInput) Input = DriveInput.FromDevices(InputSlot);
            if (Input.Reset) ResetCar();
            if (Input.SpeedBreaker) { Input.SpeedBreaker = false; ToggleSpeedBreaker(); }
            if (Input.ShiftUp) { Input.ShiftUp = false; _pendingShift = 1; }
            if (Input.ShiftDown) { Input.ShiftDown = false; _pendingShift = -1; }
            for (int i = 0; i < 4; i++)
            {
                var w = _w[i];
                if (w == null) continue;
                WheelVisuals[i].localPosition = w.Droop + Vector3.up * Mathf.Min(w.Comp, w.Travel);
                WheelVisuals[i].localRotation = Quaternion.Euler(0, w.Steer, 0);
                // CarRenderConn::UpdateTires: the drawn roll steps at most mMaxWheelRenderDeltaAngle a frame (17°, or
                // 360/(2 spokes) - 1 with a rim spoke count: CarRender/CarRenderConn.cs)
                float maxDelta = _renderConn ? _renderConn.MaxWheelRenderDeltaDeg : 17f;
                w.SpinDeg = Mathf.Repeat(w.SpinDeg + Mathf.Clamp(w.Omega * Mathf.Rad2Deg * Time.deltaTime, -maxDelta, maxDelta), 360f);
                if (_spin[i]) _spin[i].localRotation = Quaternion.Euler(w.SpinDeg, 0, 0);
            }
        }

        void FixedUpdate() => Step(Time.fixedDeltaTime);

        BoxCollider _box;
        Vector3 _preVel;

        void OnCollisionEnter(Collision c)
        {
            _hadCollision = true;   // RigidBody::HasHadCollision (status 4 | 8) this step
            if (c.rigidbody != null) _hadObjectCollision = true;
            var sm = c.collider ? c.collider.GetComponent<Smackable>() : null;
            if (sm) sm.Hit(this, -c.relativeVelocity, _rb);
            MomentScale(c);
            // DamageDragster::CheckTotaling (DamageDragster.cpp:21-46): not a ground hit, not while animating; over
            // 100 mph before and over 40 mph lost → Destroy
            if (Dragster && !Destroyed && enabled && c.contactCount > 0 && CarContacts.Classify(c.GetContact(0).normal) != CarContacts.Kind.Ground)
            {
                float before = _preVel.magnitude, after = _rb.linearVelocity.magnitude;
                if (before > DragKillSpeedMph * 0.44704f && before - after > DragKillSpeedChangeMph * 0.44704f) Destroy();
            }
        }

        void OnCollisionStay(Collision c)
        {
            _hadCollision = true;
            if (c.rigidbody != null) _hadObjectCollision = true;
            MomentScale(c);
        }

        /// <summary>
        /// RigidBody/RBVehicle collision response (CarContacts): the per-axis moment scale (WORLD/GROUND/OBJ_MOMENT_SCALE,
        /// the player's head-on yaw factor, the game breaker's collision mass) applied from this step's contact impulses.
        /// Collision messages also reach a disabled component (traffic runs its own sim), hence the enabled test.
        /// </summary>
        void MomentScale(Collision c)
        {
            // also for a disabled racer sim (traffic and cops on TrafficVehicle): the per-axis moment scale is RigidBody's,
            // which every vehicle has [the angular remainder of React, CarContacts]
            if (Spec == null || !_rb || PhysicsMode != PhysicsModes.Simulated) return;
            CarContacts.CorrectAngular(_rb, OwnCollider(c), c);
        }

        readonly List<Dynamics.CollisionBounds.Prim> _prims = new List<Dynamics.CollisionBounds.Prim>();
        Vector3 _geomPivot, _primaryOffset;
        Transform _primHolder;

        /// <summary>The car's colliders come from its CollisionGeometry (physics.md section 20), not the render bounds.</summary>
        public bool HasCollisionGeometry { get; private set; }

        /// <summary>
        /// Puts the collision geometry's body origin (the Bounds root pivot's base) at root-space height originY: the racer
        /// sim uses RIDE_HEIGHT minus sag (its road is at root y = 0), TrafficVehicle 0 (its rigidbody is the body origin).
        /// </summary>
        public void PlaceCollisionAtOrigin(float originY)
        {
            if (!HasCollisionGeometry) return;
            var body = transform.Find("Body");
            Dynamics.CollisionBounds.PlaceAtOrigin(_box, _primaryOffset, _primHolder, body, originY);
            CarContacts.RegisterPrims(this, _prims, new Vector3(0f, originY, 0f) + _geomPivot);
        }

        /// <summary>Which of this car's colliders the collision is about (the prims share one record).</summary>
        Collider OwnCollider(Collision c)
        {
            if (c.contactCount > 0)
            {
                var own = c.GetContact(0).thisCollider;
                if (own && own.attachedRigidbody == _rb) return own;
            }
            return _box;
        }

        void OnDestroy()
        {
            CarContacts.Unregister(_box);
            if (_breakerOwner == this) { _breakerOwner = null; WorldTimeScale = 1f; }
        }

        // ---- speedbreaker (0x6EDD60 / 0x6EDC90; world time 0x6F4D10) -------------------------------------------

        /// <summary>World time scale the speedbreaker asks for (GameBootstrap applies it).</summary>
        public static float WorldTimeScale = 1f;
        static PlayerCar _breakerOwner;

        /// <summary>The button: on at 30 mph or more with charge left, a second press turns it off.</summary>
        public void ToggleSpeedBreaker()
        {
            if (SpeedBreakerOn) { SpeedBreakerOn = false; return; }
            if (Spec == null || InputSlot != 0 || SpeedMps * 2.23699f < 30f || SpeedBreaker <= 0) return;
            if (Game.RaceManager.Active != null && Game.RaceManager.Active.Countdown) return;
            SpeedBreakerOn = true;
            _breakerOwner = this;
        }

        /// <summary>Ramps the world time toward 0.25 while a speedbreaker runs (2/s in, 0.5/s out). Real seconds.</summary>
        public static void TickTimeScale(float realDt)
        {
            bool on = _breakerOwner && _breakerOwner.SpeedBreakerOn;
            WorldTimeScale = on ? Mathf.MoveTowards(WorldTimeScale, 0.25f, 2f * realDt) : Mathf.MoveTowards(WorldTimeScale, 1f, 0.5f * realDt);
        }

        /// <summary>Ends any speedbreaker at once (race start/end, world unload).</summary>
        public static void ResetTime()
        {
            if (_breakerOwner) _breakerOwner.SpeedBreakerOn = false;
            _breakerOwner = null;
            WorldTimeScale = 1f;
        }

        // ---- tick ---------------------------------------------------------------------------------------------------

        /// <summary>One sim tick (called by FixedUpdate, or by tests stepping physics by hand).</summary>
        public void Step(float dt)
        {
            if (Spec == null || _rb == null || dt <= 0) return;
            // PVehicle::OnTaskSimulate (0x686060): screen timers and the off-world test; EMULATED runs no behaviour
            TickPVehicle(dt);
            if (PhysicsMode != PhysicsModes.Simulated) return;
            float realDt = dt / Mathf.Max(0.05f, Time.timeScale);
            if (!_rb.isKinematic) RigidBodyFrame();
            var up = transform.up;
            var v = _rb.linearVelocity;
            float V = v.magnitude, mph = V * 2.23699f;
            var vl = transform.InverseTransformDirection(v);
            float vz = vl.z;
            float beta = vz >= 1f ? Mathf.Atan2(vl.x, vl.z) : 0f;          // body slip angle (rad), + = sliding right
            float yawRate = Vector3.Dot(_rb.angularVelocity, up);

            // input layer: brake held at a stop selects reverse (not while the handbrake holds the car on the grid)
            float gas = Mathf.Clamp01(Input.Throttle), brake = Mathf.Clamp01(Input.Brake), steerIn = Mathf.Clamp(Input.Steer, -1f, 1f);
            float hand = Input.Handbrake ? 1f : 0f;
            // InputPlayer::DoAutoReverse: reverse below 2.5 m/s on the brake alone (not while the handbrake holds the
            // car on a grid), back to 1st above -5 m/s once the brake is released or gas is pressed
            float rawGas = gas, rawBrake = brake;
            if (Staging || ManualGears) { }   // the start line and the drag gearbox take no automatic gear changes
            else if (Gear >= 0 && vz < 2.5f && brake > 0 && gas == 0 && hand < 0.5f) SetGear(-1);
            else if (Gear < 0 && vz > -5f && (brake == 0 || gas > 0)) SetGear(1);
            else if (Gear == 0 && gas > 0.1f) SetGear(1);
            if (_pendingShift != 0) { if (ManualGears) ManualShift(Gear + _pendingShift); _pendingShift = 0; }
            // staging (the countdown): the car is held on its brakes with the clutch open, the throttle revs the engine
            if (Staging) { brake = 1f; hand = 0f; rawBrake = 1f; }
            if (Gear < 0) { float t = gas; gas = brake; brake = t; }
            // DoAutoBraking: coasting slowly brakes the car to a stop (players only)
            if (!AiControlled && rawGas == 0 && rawBrake == 0)
            {
                if (Gear >= 0) { if (vz < 8f) brake = 1f - 0.75f * Ratio01(vz, 0, 8f); }
                else brake = 1f - 0.75f * Ratio01(-vz, 0, 10f);
            }
            if (hand > 0) brake = 0;
            // Chassis state (Chassis.cpp:343-347): a blown engine or a destroyed car holds full brake and e-brake, no gas
            if (EngineBlown || Destroyed) { brake = 1f; gas = 0f; hand = 1f; }

            // speedbreaker charge and handling blend (§12)
            // LocalPlayer::DoGameBreaker / CanDoGameBreaker (LocalPlayer.cpp:148-227): off under 30 mph and while staging
            if (SpeedBreakerOn && (mph < 30f || Staging)) SpeedBreakerOn = false;
            if (SpeedBreakerOn)
            {
                SpeedBreaker -= 0.1f * realDt;
                if (SpeedBreaker <= 0) { SpeedBreaker = 0; SpeedBreakerOn = false; }
                _sb = 1f;
            }
            else
            {
                if (mph > 100f) SpeedBreaker += dt / 25f;   // Tweak_GameBreakerRechargeSpeed 100 (strictly above), 25 s
                _sb = Mathf.Max(0, _sb - dt / 3f);
            }
            SpeedBreaker = Mathf.Clamp01(SpeedBreaker);

            // ---- suspension raycasts ----
            int contacts = 0;
            for (int i = 0; i < 4; i++) if (Probe(_w[i], up)) contacts++;
            Grounded = contacts > 0;
            WheelsOnGround = contacts;
            CarContacts.Refresh(_box, !AiControlled && !Traffic, contacts > 2, SpeedBreakerOn, _rb.linearVelocity, transform.forward);
            CarContacts.RefreshGround(_box, contacts == 4, _hadObjectCollision);   // RBVehicle::CanCollideWithGround inputs
            _hadObjectCollision = false;
            _preVel = _rb.linearVelocity;
            float gfrac = Mathf.Max(0.25f, contacts / 4f);
            // landing halves the pitch and roll rates
            if (_lastContacts == 0 && contacts > 0 && !_rb.isKinematic)
            {
                var lav = transform.InverseTransformDirection(_rb.angularVelocity);
                lav.y *= 0.5f;
                lav.z *= 0.5f;
                _rb.angularVelocity = transform.TransformDirection(lav);
            }
            _lastContacts = contacts;

            // ---- engine, clutch, gearbox → driveshaft torque (§3) ----
            float tDrive = Engine(dt, gas, mph, gfrac);
            _lastDrive = tDrive;

            // ---- differential (§5) ----
            float split = Mathf.Clamp01(Spec.TorqueSplit), sf = split, sr = 1f - split;
            if (Spec.Differential.Length > 2 && Spec.Differential[2] > 0)
                Diff(Mathf.Abs(_w[0].Omega + _w[1].Omega), Mathf.Abs(_w[2].Omega + _w[3].Omega), _w[0].Contact || _w[1].Contact, _w[2].Contact || _w[3].Contact, split, Spec.Differential[2], out sf, out sr);
            for (int a = 0; a < 2; a++)
            {
                var l = _w[a * 2];
                var r = _w[a * 2 + 1];
                float ta = tDrive * (a == 0 ? sf : sr) * (1f - 0.55f * TireDamage);
                float lockF = Spec.Differential.Length > a ? Spec.Differential[a] : 0;
                Diff(Mathf.Abs(l.Omega), Mathf.Abs(r.Omega), l.Contact, r.Contact, 0.5f, lockF, out float oLeft, out float oRight);
                l.Drive = ta * oLeft;
                r.Drive = ta * oRight;
            }

            // ---- steering (§10) ----
            Steering(dt, vz, gas, brake, hand, steerIn, beta);

            // ---- grip parameters (sim 0x6B1DD0) ----
            float x01 = Mathf.Clamp01(V / 37.997f);
            float latScale = 1.2f * Table(LatScaleTable, x01);
            float grip = Gear < 0 ? 2f : 1.1f * Table(GripTable, x01);
            grip *= Mathf.Lerp(1f, 0.3f, TireDamage);
            float slipLimit = Gear < 0 ? 71f : Ratio01(V, 10f, 71f) + 0.5f;
            float scrub = Mathf.Lerp(0.15f, 1f, _sb);   // mDragReduction
            // handbrake blend and the YAW_CONTROL rear stabiliser
            _hb = hand < 0.5f ? Mathf.Min(1f, _hb + dt) : Mathf.Max(0.5f, _hb - 20f * dt);
            float ebrakeIn = hand;
            if (hand > 0.2f && Mathf.Abs(beta) > 0.3f && mph < 80f) ebrakeIn += 0.5f;
            float yc = Table(Spec.YawControl, Mathf.Min(1f, V / Mathf.Max(10f, Spec.TopSpeed)));
            float ycm = ebrakeIn > 0.5f && Mathf.Abs(beta) < 20f * Mathf.Deg2Rad ? 1f : 1f + Mathf.Min(0.35f, V / 30f * Mathf.Abs(beta) * yc);
            float rearGrip = grip * (1f + (ycm - 1f) * (1f - _drift) * _hb);

            // drift state: rear static grip, the stabilising torque, speedbreaker charge
            float rearStatic = DriftState(dt, V, gas, hand, steerIn, beta, yawRate, up, contacts);

            // burnout: gas + brake at a stop spins the driven wheels, only the other axle brakes
            bool burnout = gas > 0.8f && brake > 0.5f && mph < 10f && Gear > 0;

            // ---- tires (§8) ----
            Vector3 com = _rb.worldCenterOfMass, force = Vector3.zero, torque = Vector3.zero;
            float skid = 0, rearAlpha = 0;
            for (int i = 0; i < 4; i++)
            {
                var w = _w[i];
                bool driven = w.Front ? split > 0.01f : split < 0.99f;
                float bIn = burnout && driven ? Mathf.Abs(vz) * 2.23699f * 0.05f : brake;
                float ebIn = w.Front ? 0f : ebrakeIn;
                bool blown = (BlownTires & (1 << i)) != 0;   // BLOWN (SuspensionRacer.cpp:1665): brake 1, no e-brake, traction ×0.3
                if (blown) { bIn = 1f; ebIn = 0f; }
                if (!w.Contact) { FreeWheel(w, dt, bIn, ebIn); continue; }
                float staticMul = w.Front ? 1f : rearStatic;
                float latBoost = w.Front ? 1f + 0.75f * Mathf.Abs(steerIn) * _sb : 1f;
                float g = (w.Front ? grip : rearGrip) * _nosMult * latBoost * (1f + 0.25f * PerfectLaunch) * (blown ? 0.3f : 1f);   // launch: traction +0.25
                var hub = transform.TransformPoint(w.Droop + Vector3.up * Mathf.Min(w.Comp, w.Travel));
                var f = Tire(w, dt, hub, up, V, g, staticMul, latBoost, latScale, slipLimit, scrub, bIn, ebIn);
                // suspension load along the body's up, tire forces in the ground plane, at the hub
                var total = up * w.Load + f;
                force += total;
                torque += Vector3.Cross(hub - com, total);
                float s = Mathf.Max(Mathf.Abs(Mathf.Sin(w.AlphaDeg * Mathf.Deg2Rad)) * 1.5f, (1f - w.Traction) * 1.5f, w.Locked && Mathf.Abs(w.Vx) > 2f ? 1f : 0f);
                if (V > 3f) skid = Mathf.Max(skid, s);
                if (!w.Front) rearAlpha = Mathf.Max(rearAlpha, Mathf.Abs(w.AlphaDeg));
            }
            SkidAmount = skid;
            _rearAlpha = rearAlpha;
            // the yaw part of the wheel torque is scaled by YAW_SPEED (×1.6 more for traffic)
            float ys = Spec.YawSpeed;
            torque += Vector3.Dot(torque, up) * (ys - 1f) * up;
            if (_rb.isKinematic) return;
            bool hadCollision = _hadCollision;
            _hadCollision = false;
            // Chassis::DoSleep (0x69D120; Chassis.cpp:150-169): parked on the brakes, not staging, no collision this step →
            // RigidBody::Damp(speed < ε ? 1 : 1 − speed): v, ω, force and torque × speed (0 at rest), every wheel ω = 0
            bool sleep = !Staging && V < 0.5f && contacts == 4 && brake + hand > 0 && gas == 0 && _rb.angularVelocity.magnitude < 0.25f && !hadCollision;
            float damp = sleep ? (V < 1e-6f ? 0f : V) : 1f;
            // RigidBody::OnBeginFrame: gravity is rigidbodyspecs GRAVITY·mass (−9.813 for cars) [ours: Unity's own
            // gravity stays on, this adds the difference]
            var gravity = Vector3.up * ((Spec.Body != null ? Spec.Body.Gravity : -9.813f) - Physics.gravity.y) * _rb.mass;
            _rb.AddForce(gravity * damp);
            _rb.AddForce(force * damp);
            _rb.AddTorque(torque * damp);
            if (sleep)
            {
                _rb.linearVelocity *= damp;
                _rb.angularVelocity *= damp;
                foreach (var w in _w) { w.Omega = 0f; w.SlipX = 0f; }
                return;
            }
            Aero(V, v, gas, up, contacts);
            AirControl(dt, contacts);
            if (TireDamage > 0) _rb.AddForce(-v * _rb.mass * 0.25f * TireDamage);   // running on rims
        }

        bool _hadCollision, _hadObjectCollision;

        // ---- rigid body (RigidBody.cpp / RBVehicle.cpp) -----------------------------------------------------------

        /// <summary>
        /// The parts of RigidBody::DoIntegration / RBVehicle::OnBeginFrame that PhysX does not do:
        /// <list type="bullet">
        /// <item>each velocity component bounded: linear ±300 m/s, angular ±30 rad/s (RigidBody.cpp:682-688);</item>
        /// <item>staging (IsStaging, modelled, not animating; RBVehicle.cpp:66-87): the local sideways and forward speed
        /// and the yaw rate are zeroed and the car keeps its x/z position (only the suspension moves). [ours] The held
        /// spot is re-taken when the car is moved more than 1 m (a teleport onto the grid).</item>
        /// </list>
        /// </summary>
        void RigidBodyFrame()
        {
            var lv = _rb.linearVelocity;
            var av = _rb.angularVelocity;
            var blv = new Vector3(Mathf.Clamp(lv.x, -LinearBound, LinearBound), Mathf.Clamp(lv.y, -LinearBound, LinearBound), Mathf.Clamp(lv.z, -LinearBound, LinearBound));
            var bav = new Vector3(Mathf.Clamp(av.x, -AngularBound, AngularBound), Mathf.Clamp(av.y, -AngularBound, AngularBound), Mathf.Clamp(av.z, -AngularBound, AngularBound));
            if (blv != lv) _rb.linearVelocity = blv;
            if (bav != av) _rb.angularVelocity = bav;
            if (!Staging) { _stageHeld = false; return; }
            var p = _rb.position;
            var tp = transform.position;   // a teleport may still be pending in the transform
            if (!_stageHeld || new Vector2(tp.x - _stageHold.x, tp.z - _stageHold.z).sqrMagnitude > 1f) { _stageHold = tp; _stageHeld = true; return; }
            var rot = _rb.rotation;
            var lvl = Quaternion.Inverse(rot) * _rb.linearVelocity;
            var avl = Quaternion.Inverse(rot) * _rb.angularVelocity;
            lvl.x = 0;
            lvl.z = 0;
            avl.y = 0;
            _rb.linearVelocity = rot * lvl;
            _rb.angularVelocity = rot * avl;
            _rb.position = new Vector3(_stageHold.x, p.y, _stageHold.z);
        }

        // ---- suspension (§7) ------------------------------------------------------------------------------------

        /// <summary>Raycasts one wheel from above full bump down to below full droop; Comp = compression (m).</summary>
        bool Probe(Wheel w, Vector3 up)
        {
            const float extra = 0.3f;
            float len = w.Travel + extra + w.Radius;
            var top = transform.TransformPoint(w.Droop + Vector3.up * (w.Travel + extra));
            int n = Physics.RaycastNonAlloc(top, -up, Hits, len, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 normal = up;
            int bestHit = -1;
            for (int h = 0; h < n; h++)
            {
                var hit = Hits[h];
                // the road, props and other cars (a car pushed onto another must still find grip); never itself
                if (hit.rigidbody == _rb || hit.distance <= 0) continue;
                if (hit.rigidbody != null && !hit.rigidbody.GetComponent<PlayerCar>()) continue;   // knocked props, debris
                if (hit.distance < best) { best = hit.distance; normal = hit.normal; bestHit = h; }
            }
            // the ground face's simsurface becomes the wheel's (WWorldPos.cpp:211); no face → SimSurface::kNull
            w.Surface = bestHit >= 0 ? WorldCollision.SurfaceAt(Hits[bestHit]) : WorldCollision.Surface.Null;
            w.OnWorld = bestHit >= 0 && WorldCollision.IsGround(Hits[bestHit].collider);
            w.PrevComp = w.Contact ? w.Comp : 0;
            float upAlign = Mathf.Clamp01(Vector3.Dot(normal, up));
            if (best == float.MaxValue || upAlign <= 0.2f) { w.Contact = false; w.Comp = 0; w.Load = 0; return false; }
            w.Contact = true;
            w.Normal = normal;
            w.Comp = Mathf.Max(0, len - best);
            return true;
        }

        /// <summary>Spring, damper, sway bar and bump stop (0x6AA020).</summary>
        float SpringLoad(Wheel w, float dt)
        {
            var s = Spec;
            bool f = w.Front;
            var o = _w[w.Index ^ 1];
            float x = Mathf.Min(w.Comp, w.Travel), bump = Mathf.Max(0, w.Comp - w.Travel);
            float vel = (w.Comp - w.PrevComp) / dt;
            float k = (f ? s.SpringFront : s.SpringRear) * LBIN;
            float fs = k * x * (1f + (f ? s.SpringProgFront : s.SpringProgRear) * x);
            float valv = (f ? s.ValvingFront : s.ValvingRear) * IN, dig = f ? s.DigressionFront : s.DigressionRear;
            float vd = vel;
            if (valv > 1e-4f && Mathf.Abs(vel) > valv && dig < 1f) vd = Mathf.Sign(vel) * valv * Mathf.Pow(Mathf.Abs(vel) / valv, 1f - dig);
            float fd = (vel > 0 ? (f ? s.ShockFront : s.ShockRear) : (f ? s.ShockExtFront : s.ShockExtRear)) * LBIN * vd;
            if (fd > s.Mass * s.ShockBlowout * 9.81f) fd = 0;
            // +(xL − xR) on the left, −(xL − xR) on the right: both are k·(own − other)
            float sway = (f ? s.SwayFront : s.SwayRear) * LBIN * (x - (o.Contact ? Mathf.Min(o.Comp, o.Travel) : 0));
            // bump stop past full travel: stiff and damped so the body never sinks into the road
            float fb = bump > 0 ? bump * k * 25f + Mathf.Max(0, vel) * s.Mass * 2f : 0;
            // (capped: a wheel starting inside a wall must not fire the car into the air)
            return Mathf.Clamp(fs + fd + sway + fb, 0, s.Mass * 9.81f * 3f);
        }

        // ---- engine (§3) ----------------------------------------------------------------------------------------

        float GearRatio(int gear)
        {
            var g = Spec.GearRatios;
            if (gear < 0) return g.Length > 0 ? g[0] : 3.5f;
            if (gear == 0) return 0;
            int idx = gear + 1;
            return idx < g.Length ? g[idx] : g[g.Length - 1];
        }

        bool Driven(Wheel w) => w.Front ? Spec.TorqueSplit > 0.01f : Spec.TorqueSplit < 0.99f;

        /// <summary>A gear change opens a locked clutch; it re-engages on the next ticks.</summary>
        void SetGear(int g)
        {
            if (g == Gear) return;
            Gear = g;
            if (_clutch == 0) _clutch = 2;
        }

        /// <summary>Induction boost fraction at rpm with full spool (§3.3).</summary>
        float BoostAt(float rpm)
        {
            var s = Spec;
            if (s.HighBoost <= 0 && s.LowBoost <= 0) return 0;
            float rpmSpool = s.IdleRpm + (s.RedLine - s.IdleRpm) * s.Spool;
            if (rpm < rpmSpool) return s.Vacuum < 0 ? Ratio01(rpm, s.IdleRpm, rpmSpool) * s.Vacuum : 0;
            return Mathf.Lerp(s.LowBoost, s.HighBoost, Ratio01(rpm, rpmSpool, s.RedLine));
        }

        /// <summary>Shift points (0x673400): up where the next gear makes more wheel torque, down at up·ρ.</summary>
        /// <summary>
        /// EngineRacer reset (0x691ED0): Physics::Info::ShiftPoints (0x673400, 10 points) and the peak torque
        /// (MaxInductedTorque 0x66C180 via 0x66C2D0: TORQUE points × full-spool boost, ×1.3558 → Nm) and its rpm.
        /// _up/_down are indexed by the port's gear number (1 = 1st = the exe's index 2).
        /// </summary>
        void ShiftPoints()
        {
            var s = Spec;
            int top = Mathf.Max(1, s.ForwardGears);
            _up = new float[top + 2];
            _down = new float[top + 2];
            int n = Mathf.Max(10, s.GearRatios.Length);
            float[] up = new float[n], down = new float[n];
            Dynamics.PhysicsInfo.ShiftPoints(s, up, down);
            for (int k = 1; k <= top; k++)
            {
                _up[k] = k + 1 < n ? up[k + 1] : s.RedLine;
                _down[k] = k + 1 < n ? down[k + 1] : 0f;
            }
            _up[top] = s.RedLine;
            _peakTorqueNm = Dynamics.PhysicsInfo.MaxInductedTorque(s, out _peakRpm) * LBFT;
        }

        float Engine(float dt, float gas, float mph, float gfrac)
        {
            var s = Spec;
            // the drag engine's rev limiter works only while staging; racing, it over-revs to MAX_RPM (EngineDragster::UseRevLimiter)
            float wIdle = s.IdleRpm * RPM2RAD, wMax = (ManualGears && !Staging ? Mathf.Max(s.MaxRpm, s.RedLine) : s.RedLine) * RPM2RAD;
            // speed limiter (0x692840)
            if (Gear > 0 && s.SpeedLimiter > 0 && s.SpeedLimiterFade > 0 && mph > s.SpeedLimiter)
                gas *= 1f - Mathf.Clamp01((mph - s.SpeedLimiter) / s.SpeedLimiterFade);
            if (PowerScale <= 0) gas = 0;
            if (EngineBlown) gas = 0;   // EngineRacer: a blown engine takes no throttle and no nitrous (EngineRacer.cpp:948, 1047)

            float ratio = GearRatio(Gear) * s.FinalGear * (Gear < 0 ? -1f : 1f);
            if (s.TorqueConverter > 0 && Gear != 0)
                ratio *= 1f + (1f - Ratio01(Rpm, s.IdleRpm, _peakRpm)) * s.TorqueConverter * gas;

            // driven wheel speed → driveline speed (idle-offset map: ωw = ωmax/ratio gives ωc = ωmax)
            float ww = 0;
            int nd = 0;
            foreach (var w in _w) if (Driven(w)) { ww += w.Omega; nd++; }
            ww = nd > 0 ? ww / nd : 0;
            float wc = Gear == 0 ? _we : wIdle + ratio * ww * (1f - wIdle / wMax);
            float rpm = _we * RAD2RPM;

            // induction spool and boost
            float boost = 0;
            if (s.HighBoost > 0 || s.LowBoost > 0)
            {
                float rpmSpool = s.IdleRpm + (s.RedLine - s.IdleRpm) * s.Spool;
                float target = Ratio01(gas, 0, 0.5f);
                if (_shiftTimer > 0 || (s.Spool > 0 && rpm < rpmSpool)) target = 0;
                float rate = target > _spool ? (s.SpoolUp > 1e-6f ? dt / s.SpoolUp : 1) : (s.SpoolDown > 1e-6f ? dt / s.SpoolDown : 1);
                _spool = Mathf.Clamp01(Mathf.MoveTowards(_spool, target, rate));
                boost = _spool * BoostAt(rpm);
                BoostPsi = Mathf.MoveTowards(BoostPsi, Ratio01(boost, 0, Mathf.Max(s.LowBoost, s.HighBoost)) * s.Psi * _spool, 20f * dt);
            }

            // nitrous (0x692930)
            float nosMult = 1f;
            if (s.NosCapacity <= 0) { Nos = 0; _nosOn = false; }
            else
            {
                bool fire = Input.Nitrous && Gear > 0 && gas > 0 && PowerScale > 0 && mph >= (_nosOn ? 5f : 10f);
                _nosOn = fire && Nos > 0;
                if (fire && Nos > 0)
                {
                    Nos = Mathf.Max(0, Nos - dt / s.NosCapacity);
                    _nosDisengage = 1f;
                    nosMult = 1f + s.NosTorqueBoost;
                }
                else if (_nosDisengage > 0 && s.NosDisengage > 0) _nosDisengage -= dt / s.NosDisengage;
                else if (!SpeedBreakerOn && mph >= s.NosRechargeMinSpeed && Gear > 0)
                {
                    float t = Mathf.Lerp(s.NosRechargeMin, s.NosRechargeMax, Ratio01(mph, s.NosRechargeMinSpeed, s.NosRechargeMaxSpeed));
                    Nos = Mathf.Min(1f, Nos + dt / Mathf.Max(1f, t));
                }
            }

            _nosMult = nosMult;
            float tq = s.TorqueAt(rpm) * LBFT;
            float te = tq * (1f + boost) * nosMult * PowerScale;
            if (ManualGears) te *= DragTorqueScale();
            if (_we >= wMax - 0.5f) te = 0;                       // rev limiter
            float eb = -s.EngineBrakingAt(rpm) * tq;
            // perfect launch: full throttle at peak torque, no engine braking (EngineRacer.cpp:1160)
            if (PerfectLaunch > 0 && gas > 0 && !Staging && PowerScale > 0)
            {
                gas = 1f;
                te = _peakTorqueNm * nosMult;   // mPeakTorque (0x691ED0): the full-spool peak, Nm
                eb = 0;
            }
            float tnet = gas * te + (1f - gas) * eb;

            // clutch: 1st/R engage over 0.05 s once revs or throttle rise (open at idle), other gears over 0.25 s
            if (Gear == 0 || Staging) _clutch = 2;
            else if (Gear == 1 || Gear == -1)
            {
                float thr = wIdle + (Gear < 0 ? 2400f : 800f) * RPM2RAD;
                if (_we > thr || wc > thr || gas >= 0.1f) { if (_clutch == 2) Engage(0.05f); }
                else _clutch = 2;
            }
            else if (_clutch == 2) Engage(0.25f);

            float tc;
            if (_clutch == 0)
            {
                // locked: closed throttle drags against the wheels' rotation, fading out at a crawl
                float dirW = Mathf.Abs(ww) > 1e-3f ? Mathf.Sign(ww) : 0;
                float drag = (1f - gas) * Mathf.Abs(eb) * Mathf.Clamp01(Mathf.Abs(ww) / 3f);
                tc = gas * te - drag * dirW * Mathf.Sign(ratio);
                if (wc >= wMax && tc * Mathf.Sign(ratio) * dirW > 0) tc = 0;
                _we = Mathf.Clamp(wc, wIdle, wMax);
            }
            else if (_clutch == 1)
            {
                _clutchTimer -= dt;
                float factor = 1f - 0.75f * Mathf.Clamp01(_clutchTimer / _clutchTotal);
                float slip = Mathf.Clamp(_we - wc, -300f, 300f);
                tc = 20f * slip * factor;
                // never more than syncs the engine to the driveline this tick
                float cap = Mathf.Abs(slip) * _ie / dt + Mathf.Abs(tnet);
                tc = Mathf.Clamp(tc, -cap, cap);
                _we = Mathf.Clamp(_we + dt * (tnet - tc * gfrac) / _ie, wIdle, wMax);
                if (_clutchTimer <= 0) _clutch = 0;
            }
            else
            {
                tc = 0;
                _we = Mathf.Clamp(_we + dt * tnet / (Gear == 0 ? _ie * 0.35f : _ie), wIdle, wMax);
            }
            // IEngineDamage::Sabotage (EngineRacer.cpp:1270-1280): the engine speed shakes by
            // ω += 0.5·sin(12 turns/s · time left)·ω until the timer runs out and the engine blows
            if (_sabotageAt > 0)
            {
                float left = _sabotageAt - _simTime;
                if (left <= 0) { _sabotageAt = 0; Blow(); }
                else _we = Mathf.Clamp(_we + EngineDamageAmplitude * Mathf.Sin(left * EngineDamageFrequency * 2f * Mathf.PI) * _we, wIdle, wMax);
            }
            Rpm = _we * RAD2RPM;

            // wheel-side rev limiter on spinning driven wheels
            if (Gear != 0 && Mathf.Abs(ratio) > 0.01f)
            {
                float wl = wMax / Mathf.Abs(ratio) * 1.02f;
                foreach (var w in _w) if (Driven(w)) w.Omega = Mathf.Clamp(w.Omega, -wl, wl);
            }

            if (ManualGears) { _shiftTimer -= dt; DragUpdate(dt); }
            else AutoShift(dt, gas, wc * RAD2RPM);
            float eff = Gear > 0 && Gear + 1 < s.GearEfficiency.Length ? s.GearEfficiency[Gear + 1] : 1f;
            return eff * tc * ratio;
        }

        void Engage(float time)
        {
            _clutch = 1;
            _clutchTimer = _clutchTotal = time;
        }

        /// <summary>
        /// EngineDragster::OnGearChange: no reverse, 1st or neutral on the line. An upshift taken with a Perfect
        /// potential gives the torque boost (0.75 x bonus, fading over 3 s); a Good or a Missed one is rated only.
        /// </summary>
        void ManualShift(int gear)
        {
            var s = Spec;
            int top = Mathf.Max(1, s.ForwardGears);
            if (gear < 0 || gear > top || gear == Gear || _shiftTimer > 0 || EngineBlown) return;
            if (Staging && gear > 1) return;
            int old = Gear;
            var potential = Potential;
            _perfectShiftTime = 0;
            _shiftBoost = 0;
            SetGear(gear);
            _shiftTimer = s.ShiftSpeed * GearRatio(gear) * (gear < old ? 0.25f : 1f);
            var status = ShiftStatus.Normal;
            if (gear > old && old > 0)
            {
                if (potential == ShiftPotential.Perfect)
                {
                    _perfectShiftTime = PerfectShiftSplit;
                    _shiftBoost = _potentialBonus * PerfectShiftBoost;
                    status = ShiftStatus.Perfect;
                }
                else if (potential == ShiftPotential.Miss) status = ShiftStatus.Missed;
                else if (potential == ShiftPotential.Good) status = ShiftStatus.Good;
            }
            Potential = ShiftPotential.None;
            LastShift = status;
            ShiftCount++;
        }

        /// <summary>EngineDragster::OnTaskSimulate: boost fade, over-rev, engine heat, launch fade, shift potential.</summary>
        void DragUpdate(float dt)
        {
            var s = Spec;
            if (_shiftBoost > 0 && _shiftTimer <= 0)
            {
                _perfectShiftTime -= dt;
                if (_perfectShiftTime <= 0) { _perfectShiftTime = 0; _shiftBoost = 0; }
            }
            OverRev = Rpm > s.RedLine && s.MaxRpm > s.RedLine ? Mathf.Clamp01((Rpm - s.RedLine) / (s.MaxRpm - s.RedLine)) : 0;
            // ComputeEngineHeat: over the redline it heats 0.45/s, otherwise cools 0.1/s; 1 blows the engine
            if (!Staging && !SpeedBreakerOn && !EngineBlown && (Gear != 1 || _shiftBoost <= 0))
            {
                EngineHeat = Mathf.Max(0, EngineHeat + (OverRev > 0 ? EngineHeatUpRedLine : EngineHeatCoolDown) * dt);
                if (EngineHeat >= 1f) { EngineBlown = true; EngineHeat = 1f; PowerScale = 0; }
            }
            if (_launchTimer > 0)
            {
                _launchTimer -= dt;
                PerfectLaunch = Mathf.Max(0, _launchTimer / PerfectLaunchTime);
            }
            Potential = FindDragPotential();
        }

        /// <summary>EngineDragster::UpdateShiftPotential and CalcPotentialShiftBonus.</summary>
        ShiftPotential FindDragPotential()
        {
            var s = Spec;
            int g = Gear, top = Mathf.Max(1, s.ForwardGears);
            float rpm = Rpm;
            if (g > 1 && g < _down.Length && rpm <= _down[g]) return ShiftPotential.Down;
            if (Staging) return ShiftPotential.None;
            bool traction = true;
            if (g == 1) foreach (var w in _w) traction &= w.Contact && Mathf.Abs(w.SlipX) < 4f;
            _potentialBonus = 0;
            if (g < top && g > 0 && traction)
            {
                float range = GearRatio(g + 1) * s.OptimalShift;
                float perfect = _up[g] - range;
                if (rpm >= s.RedLine) _potentialBonus = -1f;
                else if (rpm >= perfect) _potentialBonus = Mathf.Lerp(1f, 0.5f, Ratio01(rpm, perfect, s.RedLine));
                if (_potentialBonus > 0) return ShiftPotential.Perfect;
                if (_potentialBonus < 0) return ShiftPotential.Miss;
                if (rpm > _up[g] - range * GoodShiftRangeMult) return ShiftPotential.Good;
            }
            return ShiftPotential.None;
        }

        /// <summary>GetShiftBoost (lerp 1..2 over the boost left) and the engine-heat torque penalty above 1st.</summary>
        float DragTorqueScale()
        {
            float k = 1f;
            if (_shiftTimer <= 0 && _shiftBoost > 0) k *= Mathf.Lerp(1f, 2f, _shiftBoost / PerfectShiftSplit * _perfectShiftTime);
            if (EngineHeat > 0 && Gear > 1) k *= 1f - Mathf.Clamp01(EngineHeat / EngineHeatPenaltyThreshold) * EngineHeatTorquePenalty;
            return k;
        }

        /// <summary>
        /// EngineRacer::GetPerfectLaunchRange: in 1st, a band a quarter of (redline - idle) wide above peak torque,
        /// kept 500 rpm under the redline.
        /// </summary>
        public (float lo, float hi) PerfectLaunchWindow()
        {
            var s = Spec;
            float range = (s.RedLine - s.IdleRpm) * 0.25f;
            float lo = Mathf.Min(_peakRpm + range, s.RedLine - 500f) - range;
            return (lo, lo + range);
        }

        public bool InPerfectLaunchRange
        {
            get
            {
                if (!ManualGears || Gear != 1) return false;
                var (lo, hi) = PerfectLaunchWindow();
                return Rpm >= lo && Rpm <= hi;
            }
        }

        /// <summary>
        /// GO: the car leaves staging. A player drag car inside the launch window gets the perfect launch (4.5 s,
        /// speed.exe 0x6700E0). How the game measures "inside" is not decompiled; here it is the window above.
        /// </summary>
        public bool Launch()
        {
            bool perfect = InPerfectLaunchRange && Staging;
            Staging = false;
            if (perfect) { PerfectLaunch = 1f; _launchTimer = PerfectLaunchTime; }
            return perfect;
        }

        /// <summary>Automatic gearbox (0x6920D0 / 0x6AF490).</summary>
        void AutoShift(float dt, float gas, float rpmC)
        {
            var s = Spec;
            _shiftTimer -= dt;
            if (Gear <= 0 || _clutch != 0 || _shiftTimer > 0) return;
            int top = Mathf.Max(1, s.ForwardGears);
            if (Gear < top && rpmC >= _up[Gear])
            {
                // no upshift while a driven wheel spins or is off the ground
                foreach (var w in _w)
                    if (!w.Contact || Mathf.Abs(w.SlipX) >= 4f) return;
                SetGear(Gear + 1);
                _shiftTimer = s.ShiftSpeed * GearRatio(Gear);
                return;
            }
            if (Gear > 1)
            {
                float d = _down[Gear];
                float lo = s.IdleRpm + (d - s.IdleRpm) * 0.65f;
                float thr = Mathf.Min(Mathf.Lerp(lo, d, gas), _up[Gear - 1] / GearRatio(Gear - 1) * GearRatio(Gear) - 200f);
                if (rpmC < thr)
                {
                    SetGear(Gear - 1);
                    _shiftTimer = s.ShiftSpeed * GearRatio(Gear) * 0.25f;
                }
            }
        }

        /// <summary>Limited-slip split (0x68F0D0): more torque to the slower side as `lockF` rises.</summary>
        static void Diff(float a, float b, bool ga, bool gb, float bias, float lockF, out float oa, out float ob)
        {
            if (lockF > 0 && ga && gb)
            {
                float s = Mathf.Abs(b * bias + (1f - bias) * a);
                if (s > 1e-3f)
                {
                    oa = Mathf.Clamp01((1f - lockF) * bias + lockF * Mathf.Abs(b * bias) / s);
                    ob = Mathf.Clamp01((1f - lockF) * (1f - bias) + lockF * Mathf.Abs((1f - bias) * a) / s);
                    return;
                }
            }
            oa = bias;
            ob = 1f - bias;
        }

        // ---- steering (§10) -------------------------------------------------------------------------------------

        void Steering(float dt, float vz, float gas, float brake, float hand, float steerIn, float beta)
        {
            _simTime += dt;
            float steering = Spec.Steering, a;
            if (AiControlled)
                a = Mathf.Clamp(steering * 45f * steerIn, -45f, 45f);   // DoAISteering
            else
            {
                float sp = Mathf.Clamp(vz, 0f, 160f) / 160f;
                float q = Mathf.Abs(Average(_steerAvg, ref _steerAvgSum, 0.55f));
                // CalculateMaxSteering: speed range, opened up off the throttle / on the brakes, widened by a held turn
                float max = Table(SteerMaxTable, sp);
                float tb = 1f - (gas + 1f - (brake + hand) * 0.5f) * 0.5f;
                max *= 1.45f * tb * Table(SteerGasTable, sp) + 1f;
                max *= Table(SteerInputTable, q);
                float yawLeft = _w[2].AlphaDeg, yawRight = _w[3].AlphaDeg;
                if (steerIn > 0 && yawRight > 0) max = Mathf.Min(Mathf.Max(max, yawRight), 45f);
                else if (steerIn < 0 && yawLeft < 0) max = Mathf.Min(Mathf.Max(max, -yawLeft), 45f);
                else if (q >= 0.5f) max = Mathf.Max(max, _lastMaxSteer);   // hard turn: the lock never closes while held
                max = Mathf.Min(max, 45f);
                _lastMaxSteer = max;
                float target = Mathf.Clamp(steerIn * max * steering, -45f, 45f);
                // CalculateSteeringSpeed: 180°/s by speed, by how fast the input moves (0.15 s average), by the held turn
                float din = Mathf.Abs(steerIn - _steerInPrev) / dt;
                Record(_steerSpeedAvg, ref _steerSpeedSum, Table(SteerRateTable, Mathf.Clamp01(din / 10f)), 0.15f);
                float rate = 180f * Table(SteerGasTable, sp) * Average(_steerSpeedAvg, ref _steerSpeedSum, 0.15f) * Table(SteerInputTable, q) * steering;
                _steerDeg = Mathf.Clamp(target, _steerDeg - rate * dt, _steerDeg + rate * dt);
                a = _steerDeg;
                Record(_steerAvg, ref _steerAvgSum, steerIn, 0.55f);
            }
            _steerInPrev = steerIn;
            if (_sb > 0) a = Mathf.Lerp(a, steerIn * 60f, _sb);
            // Ackermann: the inside wheel turns a, the outside WB·a/(|a|·TRACK + WB)
            float ar = a * Mathf.Deg2Rad;
            float outer = Spec.WheelBase * ar / (Mathf.Abs(ar) * Spec.TrackFront + Spec.WheelBase) * Mathf.Rad2Deg;
            bool right = a > 0;
            _w[0].Steer = right ? outer : a;
            _w[1].Steer = right ? a : outer;
            _w[2].Steer = _w[3].Steer = 0;
        }

        /// <summary>Time-windowed average sampled each tick (the game's AverageWindow at 60 Hz).</summary>
        void Record(Queue<(float t, float v)> q, ref float sum, float v, float window)
        {
            q.Enqueue((_simTime, v));
            sum += v;
            while (q.Count > 0 && q.Peek().t < _simTime - window) sum -= q.Dequeue().v;
        }

        static float Average(Queue<(float t, float v)> q, ref float sum, float window) => q.Count > 0 ? sum / q.Count : 0f;

        // ---- drift (0x6A9760) -------------------------------------------------------------------------------------

        float DriftState(float dt, float V, float gas, float hand, float steerIn, float beta, float yawRate, Vector3 up, int contacts)
        {
            if (Traffic) { _drift = 0; _driftState = 0; return 1f; }
            const float twelve = 0.033333335f;   // 12° in turns
            float betaTurns = beta / (2f * Mathf.PI);
            _drift += (_driftState == 1 || _driftState == 2 ? 8f : -2f) * dt;
            if (_drift >= 1f) { _drift = 1f; _driftState = 2; }
            else if (_drift <= 0f) { _drift = 0f; _driftState = 0; }
            if (_driftState < 2)
            {
                if (V > 13.4109f && (hand > 0.5f || Mathf.Abs(betaTurns) > twelve)) _driftState = 1;
            }
            else if (steerIn * betaTurns * gas > twelve && V > 13.4109f) _driftState = 2;
            else if (Mathf.Abs(betaTurns) * gas <= twelve) _driftState = 3;
            else _driftState = 1;
            if (_drift <= 0) return 1f;

            float b = Mathf.Abs(beta);
            if (_sb <= 0 && V > 15.64605f && b > 0.52359f) SpeedBreaker = Mathf.Min(1f, SpeedBreaker + _drift * dt * 0.5f);
            // stabilising yaw torque while the car rotates against the slide
            if (yawRate * beta < 0 && contacts > 1 && !_rb.isKinematic)
                _rb.AddTorque(up * (-4f * _rb.inertiaTensor.y * _drift * Graph(DriftYawGraph, b) * yawRate));
            float into = steerIn * beta > 0 ? Mathf.Abs(steerIn) : 0;
            return Table(DriftGripTable, (into * 4f + (Mathf.Abs(yawRate) + b) * 0.5f) * _drift);
        }

        // ---- tire (§8) ------------------------------------------------------------------------------------------

        Vector3 Tire(Wheel w, float dt, Vector3 hub, Vector3 up, float V, float grip, float staticMul, float latBoost, float latScale,
                     float slipLimit, float scrub, float bIn, float ebIn)
        {
            var s = Spec;
            bool f = w.Front;
            float spring = SpringLoad(w, dt);
            float upAlign = Mathf.Clamp01(Vector3.Dot(w.Normal, up));
            float N = spring * Mathf.Max(0.3f, 4f * upAlign - 3f);
            w.Load = spring;

            var n = w.Normal;
            var fwd = Quaternion.AngleAxis(w.Steer, up) * transform.forward;
            fwd = (fwd - n * Vector3.Dot(fwd, n)).normalized;
            var side = Vector3.Cross(n, fwd);
            var pv = _rb.GetPointVelocity(hub - up * w.Radius);
            float vx = Vector3.Dot(pv, fwd), vy = Vector3.Dot(pv, side);
            float r = w.Radius;
            float ax = (vx - w.PrevVx) / dt;
            w.PrevVx = vx;
            w.Vx = vx;

            float Tb = (f ? s.BrakeFront : s.BrakeRear) * LBFT * 4f * bIn;
            float Teb = f ? 0f : s.EBrake * LBFT * 10f * ebIn;
            float drive = w.Drive, brakeT;
            if (Mathf.Abs(vx) >= 1f) brakeT = -(Mathf.Abs(w.Omega) > 1e-3f ? Mathf.Sign(w.Omega) : Mathf.Sign(vx)) * (Tb + Teb);
            else
            {
                // low-speed hold: the brakes resist creeping in proportion to speed
                drive -= Mathf.Min(1f, ebIn) * drive;
                brakeT = -(bIn + ebIn) * vx * N / r;
            }
            float alpha = Mathf.Atan2(vy, Mathf.Abs(vx));
            float alphaDeg = alpha * Mathf.Rad2Deg;
            float lowSpeed = !w.Front && !w.Locked && w.Omega >= 0 ? 0.85f + 0.15f * Ratio01(V, 13.41f, 22.35f) : 1f;
            float muD = (f ? s.DynamicGripFront : s.DynamicGripRear) * grip * lowSpeed;

            // lock test (0x68E220): BRAKE_LOCK only scales the brakes here
            float tLock = 1.2f * (Teb + Tb * (f ? s.BrakeLockFront : s.BrakeLockRear));
            float vmag = Mathf.Sqrt(vx * vx + vy * vy);
            w.Locked = tLock > 1f && r * muD * N * Mathf.Abs(vx) / Mathf.Max(0.01f, vmag) + 100f * Mathf.Abs(w.Omega) < tLock;
            if (w.Locked) w.Omega = 0;
            float slipX = w.Omega * r - vx;
            float sMag = Mathf.Sqrt(slipX * slipX + vy * vy);

            float T = drive + brakeT, fx, fy;
            bool boosted = false;
            // (a wheel that has just regained near-rolling speed grips again rather than sliding on noise)
            if ((w.Traction < 1f && sMag > 0.25f) || w.Locked)
            {
                // sliding: dynamic friction along the slip direction
                float k = sMag > 1e-3f ? muD * N / sMag : 0;
                if (V < 0.447f && muD > 0.1f) k /= muD;
                fx = k * slipX;
                fy = -k * vy;
                if (T * fx > 0 && Mathf.Abs(fx) > Mathf.Abs(T / r)) fx = T / r;
            }
            else
            {
                fx = T / r;
                fy = -Mathf.Sign(vy) * Lat(Mathf.Abs(alphaDeg), N, f ? s.GripScaleFront : s.GripScaleRear, latScale);
                boosted = T * vx > 0;
            }
            fy *= latBoost;
            // HANDLING tuning (§2): lateral ×(1 + 0.2t), longitudinal ×(1 − 0.2t), before the friction circle
            fy *= s.TuneLateral;
            fx *= s.TuneLongitudinal;

            // friction circle
            float fmax = staticMul * grip * (f ? s.StaticGripFront : s.StaticGripRear) * N * lowSpeed;
            float fxTest = boosted ? fx * 1.5f : fx;
            float mag = Mathf.Sqrt(fxTest * fxTest + fy * fy);
            float traction = 1f, slipLim = slipLimit;
            if (mag > fmax && mag > 1e-3f)
            {
                traction = fmax / mag;
                fx = fxTest * traction;
                fy *= traction;
                slipLim = traction * traction * slipLimit;
            }
            if (Mathf.Abs(slipX) > slipLim && slipLim > 0) traction *= slipLim / Mathf.Abs(slipX);
            // surface friction (SuspensionRacer.cpp:787-789): the tyre forces scale by the simsurface's grips
            fy *= w.Surface.LateralGrip;
            fx *= w.Surface.DriveGrip;
            // cornering drag
            float gs = Mathf.Max(0.1f, f ? s.GripScaleFront : s.GripScaleRear);
            if (vx > 1f) fx -= Mathf.Sin(alpha) * fy * scrub / gs;
            else fy *= Mathf.Min(Mathf.Abs(vy), 1f);

            // wheel spin: while sliding the wheel has its own dynamics, gripping it rolls with the road
            if (!w.Locked)
            {
                if (traction < 1f)
                {
                    float tau = 0.5f * (T - fx * r + w.TauPrev);
                    w.TauPrev = tau;
                    // rolling resistance × the surface's ROLLING_RESISTANCE (SuspensionRacer.cpp:805)
                    float wdot = (tau - 2f * RollingResistance * w.Surface.RollingResistance * w.Omega) / WheelInertia - traction * slipX / (dt * r);
                    wdot = Mathf.Lerp(wdot, ax / r, traction);
                    w.Omega += dt * wdot;
                }
                else { w.Omega = (vx + ax * dt) / r; w.TauPrev = 0; }   // rolls with the road, its acceleration included
            }
            w.Traction = traction;
            w.SlipX = w.Omega * r - vx;
            w.AlphaDeg = alphaDeg;

            // no force may reverse the contact's motion within one tick
            float quarter = s.Mass * 0.25f;
            fy = Mathf.Clamp(fy, -quarter * Mathf.Abs(vy) / dt, quarter * Mathf.Abs(vy) / dt);
            if (fx * vx < 0 && Mathf.Abs(w.Drive) < 1f) fx = Mathf.Clamp(fx, -quarter * Mathf.Abs(vx) / dt, quarter * Mathf.Abs(vx) / dt);
            return side * fy + fwd * fx;
        }

        /// <summary>A wheel in the air (0x68E370): drive and brakes act on its spin only.</summary>
        void FreeWheel(Wheel w, float dt, float bIn, float ebIn)
        {
            var s = Spec;
            float Tb = (w.Front ? s.BrakeFront : s.BrakeRear) * LBFT * 4f * bIn;
            float Teb = w.Front ? 0f : s.EBrake * LBFT * 10f * ebIn;
            float brakeT = -Mathf.Sign(w.Omega) * Mathf.Min(Tb + Teb, Mathf.Abs(w.Omega) * WheelInertia / dt);
            w.Omega += dt * (w.Drive + brakeT) / WheelInertia;
            w.Locked = 1.2f * (Teb + Tb * (w.Front ? s.BrakeLockFront : s.BrakeLockRear)) > 1f + 100f * Mathf.Abs(w.Omega);
            if (w.Locked) w.Omega = 0;
            w.Traction = 1;
            w.TauPrev = 0;
            w.SlipX = 0;
            w.AlphaDeg = 0;
            w.Load = 0;
        }

        /// <summary>Lateral force Lat(α°, N) (0x68E090).</summary>
        static float Lat(float alphaDeg, float N, float gripScale, float latScale)
        {
            float x = N * 0.001f * 0.8f;
            int i = Mathf.FloorToInt(alphaDeg / 2f);
            float v = i >= 6 ? LatAt(6, x) : Mathf.Lerp(LatAt(i, x), LatAt(i + 1, x), alphaDeg / 2f - i);
            return v * gripScale * 2f * latScale * 2500f;
        }

        static float LatAt(int t, float x)
        {
            var tab = LatTables[Mathf.Clamp(t, 0, 6)];
            float p = Mathf.Clamp(x, 0f, 10f) / 2f;
            int i = Mathf.Min((int)p, tab.Length - 2);
            return Mathf.Lerp(tab[i], tab[i + 1], p - i);
        }

        // ---- aero and air (§6, §10) -----------------------------------------------------------------------------

        void Aero(float V, Vector3 v, float gas, Vector3 up, int contacts)
        {
            var s = Spec;
            // drag doubles off the throttle
            _rb.AddForce(-v * V * s.DragCoefficient * (2f - gas) * (1f - 0.75f * _sb));
            if (V < 0.1f) return;
            float fwdDot = Mathf.Max(0, Vector3.Dot(transform.forward, v / V));
            float upY = contacts >= 2 ? 1f : Mathf.Max(0, up.y) * (contacts == 0 ? 0.8f : 1f);
            float down = s.AeroCoefficient * 2000f * V * Mathf.Max(0.4f, Mathf.Sqrt(fwdDot)) * upY;
            float zf = s.FrontAxle, zr = s.FrontAxle - s.WheelBase;
            var at = transform.TransformPoint(new Vector3(0, _rb.centerOfMass.y, zr + (zf - zr) * s.AeroCg * 0.01f));
            _rb.AddForceAtPosition(-up * down, at);
            // speedbreaker: an extra 2 g of down force fading with the blend
            if (_sb > 0) _rb.AddForce(-up * 2f * 9.81f * _rb.mass * _sb);
        }

        /// <summary>Air control (0x6A8F40): extra fall force and pitch/roll levelling while airborne.</summary>
        void AirControl(float dt, int contacts)
        {
            if (contacts > 0) { _airTime = 0; return; }
            _airTime += dt;
            _rb.AddForce(Vector3.down * 3f * _rb.mass);
            if (_airTime < 0.1f) return;
            var err = Vector3.Cross(transform.up, Vector3.up);
            var av = _rb.angularVelocity;
            var right = transform.right;
            var fwd = transform.forward;
            float pitch = Vector3.Dot(err, right) * 4f - Vector3.Dot(av, right) * 1.5f;
            float roll = Vector3.Dot(err, fwd) * 2f - Vector3.Dot(av, fwd) * 1f;
            _rb.AddTorque(right * pitch + fwd * roll, ForceMode.Acceleration);
        }

        // ---- helpers ------------------------------------------------------------------------------------------

        static float Ratio01(float x, float a, float b) => b - a <= 1e-6f ? 0f : Mathf.Clamp01((x - a) / (b - a));

        /// <summary>Equally spaced samples over 0..1, linear, clamped (0x6533B0).</summary>
        static float Table(float[] t, float x01)
        {
            if (t == null || t.Length == 0) return 1f;
            if (t.Length == 1) return t[0];
            float p = Mathf.Clamp01(x01) * (t.Length - 1);
            int i = Mathf.Min((int)p, t.Length - 2);
            return Mathf.Lerp(t[i], t[i + 1], p - i);
        }

        /// <summary>(x, y) points, linear, clamped (0x46F7C0).</summary>
        static float Graph(Vector2[] g, float x)
        {
            if (x <= g[0].x) return g[0].y;
            for (int i = 1; i < g.Length; i++)
                if (x <= g[i].x) return Mathf.Lerp(g[i - 1].y, g[i].y, (x - g[i - 1].x) / Mathf.Max(1e-6f, g[i].x - g[i - 1].x));
            return g[g.Length - 1].y;
        }
    }
}
