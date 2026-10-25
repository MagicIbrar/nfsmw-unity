using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// Physics/Behaviors/SuspensionTraffic.cpp (speed.exe Construct 0x6B6AB0, ctor 0x6B20F0, vtable 0x8ABC00; Tire::
    /// UpdateLoaded 0x68F7A5, DoSimpleAero 0x69EE20, DoDriveForces 0x69EE90): the traffic chassis.
    /// <list type="bullet">
    /// <item>Springs (progressive) + one-way shocks + sway bars on four raycast arms; load scaled by how square the
    /// wheel sits on the road (max(4·d² − 3, 0.3)); wheels stay on the road speed (no spin dynamics).</item>
    /// <item>Tires: lateral −2·v_lat·load·GRIP_SCALE (÷ skid speed above 1 m/s), longitudinal = wheel torque / radius;
    /// brakes BRAKES·1.3558·BrakingTorque (.data 0x8F8A90 = 4), e-brake EBRAKE·1.3558·EBrakingTorque (0x8F8A94 = 10)
    /// on the rear, locking them above half input.</item>
    /// <item>Steering: input × 45°, both front wheels alike (no Ackermann). Drive torque front = (1 − TORQUE_SPLIT),
    /// rear = TORQUE_SPLIT (the reverse of the racer's convention, as the exe has it).</item>
    /// <item>Drag −speed·DRAG_COEFFICIENT·v; wheel torques with their lever's world-y halved (TrafficRollAdjust 0.5).</item>
    /// </list>
    /// </summary>
    public sealed class SuspensionTraffic : Chassis
    {
        /// <summary>Tire (SuspensionTraffic.cpp:21-110, 225-332).</summary>
        sealed class Tire
        {
            public readonly float Radius;
            public float Brake, EBrake, AV, Load, LateralForce, LongitudeForce, AppliedTorque, Slip, RoadSpeed, SlipAngle, LateralSpeed;
            public bool Slipping;
            readonly int _axle;
            readonly CarSpec _c;

            public Tire(float radius, int index, CarSpec c)
            {
                Radius = Mathf.Max(radius, 0.1f);
                _axle = index >> 1;
                _c = c;
            }

            float GripScale => _axle == 0 ? _c.GripScaleFront : _c.GripScaleRear;
            float BrakeSpec => _axle == 0 ? _c.BrakeFront : _c.BrakeRear;

            public void BeginFrame() { AppliedTorque = 0f; LateralForce = 0f; LongitudeForce = 0f; }
            public void Stop() { AV = 0f; Slip = 0f; RoadSpeed = 0f; SlipAngle = 0f; }

            public void UpdateFree()
            {
                Slipping = false;
                Load = 0f;
                Slip = 0f;
                SlipAngle = 0f;
                if (EBrake > 0f || Brake > 0f) AV = 0f;
                LateralForce = 0f;
                LongitudeForce = 0f;
            }

            public void UpdateLoaded(float latVel, float fwdVel, float load)
            {
                if (Load <= 0f) AV = fwdVel / Radius;
                RoadSpeed = fwdVel;
                LateralSpeed = latVel;
                Load = Mathf.Max(load, 0f);
                if (Brake > 0f)
                {
                    float bt = Brake * (BrakeSpec * FTLB2NM * BrakingTorque);
                    if (AV > 0f) bt = -bt;
                    AppliedTorque += bt;
                }
                if (EBrake > 0f)
                {
                    float ebt = EBrake * (_c.EBrake * FTLB2NM * EBrakingTorque);
                    if (AV > 0f) ebt = -ebt;
                    AppliedTorque += ebt;
                }
                SlipAngle = Mathf.Atan2(latVel, Mathf.Abs(fwdVel));
                float slipSpeed = AV * Radius - fwdVel;
                Slip = EBrake > 0f && Mathf.Abs(fwdVel) > 1f ? slipSpeed : 0f;
                float skid = Mathf.Sqrt(slipSpeed * slipSpeed + latVel * latVel);
                if (EBrake > 0.5f && skid > 1f)
                {
                    Slipping = true;
                    LongitudeForce = -fwdVel * 2f * Load * GripScale / skid;
                }
                else LongitudeForce = AppliedTorque / Radius;
                LateralForce = -latVel * 2f * Load * GripScale;
                if (skid > 1f) LateralForce /= skid;
                if (Mathf.Abs(fwdVel) > 1f) LongitudeForce -= Mathf.Sin(SlipAngle) * LateralForce * 0.5f;
                else LateralForce *= Mathf.Min(Mathf.Abs(latVel), 1f);
                AV = (1f - EBrake) * fwdVel / Radius;
            }
        }

        /// <summary>BrakingTorque / EBrakingTorque (speed.exe .data 0x8F8A90 / 0x8F8A94).</summary>
        public static float BrakingTorque = 4f, EBrakingTorque = 10f;
        /// <summary>TrafficRollAdjust (SuspensionTraffic.cpp:523).</summary>
        public const float TrafficRollAdjust = 0.5f;

        readonly Tire[] _t = new Tire[4];
        float _lastSteerDeg;
        const float MaxSteering = 45f;

        public SuspensionTraffic(TrafficVehicle owner) : base(owner)
        {
            for (int i = 0; i < 4; i++) _t[i] = new Tire(i < 2 ? C.TireRadiusFront : C.TireRadiusRear, i, C);
            CreateArms();
        }

        public override float WheelRadius(int i) => _t[i].Radius;
        public override float WheelAngularVelocity(int i) => _t[i].AV;
        public override float WheelSteerDeg(int i) => i < 2 ? _lastSteerDeg : 0f;
        public override float MaxSteeringDeg => MaxSteering;
        public override float WheelLoad(int i) => _t[i].Load;
        public override float WheelSlip(int i) => _t[i].Slip;
        public override float WheelSkid(int i) => _t[i].LateralSpeed;
        public override float WheelSlipAngle(int i) => _t[i].SlipAngle;
        public bool WheelTraction(int i) => _t[i].EBrake <= 0f;

        public override void MatchSpeed(float speed)
        {
            for (int i = 0; i < 4; i++) _t[i].AV = speed / _t[i].Radius;
        }

        /// <summary>SuspensionTraffic::Reset: the wheels find the road and take their compression from it.</summary>
        public override void Reset()
        {
            ComputeState(Time.fixedDeltaTime, out var s);
            int n = 0;
            for (int i = 0; i < 4; i++)
            {
                var w = Wheels[i];
                if (w.UpdatePosition(s, s.WorldCog, _t[i].Radius, S.Dimension.y * 2f))
                {
                    float c = w.Penetration + RideHeight(i);
                    w.SetCompression(c < 0f ? 0f : c);
                    if (w.Compression > 0f) n++;
                }
                else w.SetCompression(0f);
                _t[i].Stop();
            }
            NumWheelsOnGround = n;
        }

        /// <summary>SuspensionTraffic::OnTaskSimulate.</summary>
        public override void Simulate(float dT)
        {
            SetCOG(0f, 0f);
            ComputeState(dT, out var s);
            for (int i = 0; i < 4; i++) _t[i].BeginFrame();
            // DoSimpleAero
            Owner.ResolveForce(-(s.Speed * C.DragCoefficient) * s.LinearVel);
            DoDriveForces(s);
            DoWheelForces(s);
            if (DoSleep(s) == SleepState.All)
                for (int i = 0; i < 4; i++) _t[i].Stop();
        }

        void DoDriveForces(in State s)
        {
            var eng = Owner.Engine;
            if (eng == null) return;
            float torque = eng.DriveTorque;
            if (torque == 0f) return;
            float split = C.TorqueSplit;
            for (int i = 0; i < 4; i++)
                if (Wheels[i].IsOnGround)
                    _t[i].AppliedTorque += (i < 2 ? torque * (1f - split) : torque * split) * 0.5f;
        }

        void DoWheelForces(in State s)
        {
            float dT = s.Time;
            // DoHP2Steering + DoSteering: one direction for both front wheels
            _lastSteerDeg = s.Steer * MaxSteering;
            float a = _lastSteerDeg * Mathf.Deg2Rad;
            var steerDir = s.ToWorld(new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)));
            for (int i = 0; i < 4; i++)
            {
                _t[i].Brake = s.Brake;
                if (i > 1) _t[i].EBrake = s.EBrake;
            }
            int onGround = 0;
            float maxDelta = 0f;
            var cog = s.WorldCog;
            float massLimit = 0.25f * s.Mass;
            float sw0 = (Wheels[0].Compression - Wheels[1].Compression) * C.SwayFront * LBIN2NM;
            float sw1 = (Wheels[2].Compression - Wheels[3].Compression) * C.SwayRear * LBIN2NM;
            bool resolve = false;
            for (int i = 0; i < 4; i++)
            {
                int axle = i >> 1;
                var w = Wheels[i];
                var t = _t[i];
                w.UpdatePosition(s, cog, t.Radius, s.Dimension.y * 2f);
                var ground = w.Normal;
                var fwd = i < 2 ? steerDir : s.Forward;
                var lat = Vector3.Cross(ground, fwd).normalized;
                float upness = Mathf.Clamp01(Vector3.Dot(ground, s.Up));
                float ride = RideHeight(i), travel = (axle == 0 ? C.TravelFront : C.TravelRear) * IN;
                float comp = ride * upness + w.Penetration;
                if (w.Compression == 0f) maxDelta = Mathf.Max(maxDelta, comp - travel);
                comp = Mathf.Max(comp, 0f);
                if (comp > travel) { maxDelta = Mathf.Max(maxDelta, comp - travel); comp = travel; }
                if (comp > 0f && upness > EnableRollStopsThreshold)
                {
                    onGround++;
                    float rise = (comp - w.Compression) / dT;
                    float k = (axle == 0 ? C.SpringFront : C.SpringRear) * LBIN2NM;
                    float prog = axle == 0 ? C.SpringProgFront : C.SpringProgRear;
                    float spring = comp * k * (comp * prog + 1f);
                    float damp = rise * (axle == 0 ? C.ShockFront : C.ShockRear) * LBIN2NM;
                    if (damp > C.ShockBlowout * 9.81f * s.Mass) damp = 0f;
                    float sway = i == 0 ? sw0 : i == 1 ? -sw0 : i == 2 ? sw1 : -sw1;
                    float springForce = Mathf.Max(damp + spring + sway, 0f);
                    var c = Vector3.Cross(Vector3.Cross(fwd, ground), fwd);
                    float d2 = Vector3.Dot(c, ground);
                    float load = Mathf.Max(d2 * 4f - 3f, 0.3f) * springForce;
                    float xs = Vector3.Dot(w.Velocity, lat), zs = Vector3.Dot(w.Velocity, fwd);
                    t.UpdateLoaded(xs, zs, load);
                    float maxTraction = Mathf.Abs(xs / dT * massLimit);
                    float traction = Mathf.Clamp(t.LateralForce, -maxTraction, maxTraction);
                    var drive = Vector3.Cross(lat, ground).normalized * t.LongitudeForce;
                    w.Force = lat * traction + drive + s.Up * springForce;
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
                var cg = s.Position + s.WorldCog;
                for (int i = 0; i < 4; i++)
                {
                    var w = Wheels[i];
                    var p = w.LocalArm;
                    p.y += w.Compression - RideHeight(i);
                    p = s.Position + s.ToWorld(p);
                    w.Position = p;
                    var r = p - cg;
                    r.y *= TrafficRollAdjust;
                    Owner.Resolve(w.Force, Vector3.Cross(r, w.Force));
                }
            }
            if (maxDelta > 0f)
            {
                for (int i = 0; i < 4; i++) Wheels[i].Position.y += maxDelta;
                Owner.ModifyYPos(maxDelta);
            }
            NumWheelsOnGround = onGround;
        }
    }
}
