using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// Physics/Behaviors/SuspensionSimple.cpp (speed.exe Construct 0x6B6B50, OnTaskSimulate 0x6B2870, DoWheelForces
    /// 0x6AC480, DoDriveForces 0x69FC00, Tire::UpdateLoaded 0x69F020, OnCollision 0x69F9C0, DoSteering 0x69FB20): the
    /// chassis the pvehicle data gives every cop car (BEHAVIOR_MECHANIC_SUSPENSION = SuspensionSimple on all 14 cop
    /// pvehicles; no traffic car uses it; PVehicle::LookupBehaviorSignature 0x67F0D0 also gives it to CAR-class
    /// RACER/NONE/REMOTE drivers). The cops run it through TrafficVehicle with EngineRacer (CopCar.AttachChassis). Differences from SuspensionTraffic: Ackermann steering of 45..60° × STEERING, wheel-spin
    /// dynamics with slip, the 12° lateral ramp × 1.54 × GRIP_SCALE, locked-brake skids, the friction circle
    /// STATIC_GRIP × (1 + 2.5·brake), surface grip on the rear, the rear-traction oversteer correction, aero, the jump
    /// stabiliser and the wall steer.
    /// </summary>
    public sealed class SuspensionSimple : Chassis
    {
        sealed class Tire
        {
            public readonly float Radius;
            readonly int _index, _axle;
            readonly CarSpec _c;
            public float Brake, EBrake, AV, Load, LateralForce, LongitudeForce, AppliedTorque, TractionBoost = 1f, Slip, LateralSpeed, RoadSpeed, SlipAngle, Traction = 1f, LastTorque, AngularAcc, MaxSlip = 0.5f, GripBoost = 1f;
            public bool BrakeLocked, AllowSlip;
            public WorldCollision.Surface Surface = WorldCollision.Surface.Default;
            /// <summary>WheelMomentOfInertia (SuspensionRacer.cpp:627; PC .data 0x8F8AB0 = 10.0).</summary>
            const float WheelMomentOfInertia = 10f;

            public Tire(float radius, int index, CarSpec c)
            {
                Radius = Mathf.Max(radius, 0.1f);
                _index = index;
                _axle = index >> 1;
                _c = c;
            }

            float GripScale => _axle == 0 ? _c.GripScaleFront : _c.GripScaleRear;
            float Dyn => _axle == 0 ? _c.DynamicGripFront : _c.DynamicGripRear;
            float Stat => _axle == 0 ? _c.StaticGripFront : _c.StaticGripRear;

            public void BeginFrame(float maxSlip, float gripScale, float tractionBoost)
            {
                MaxSlip = maxSlip; AppliedTorque = 0f; LateralForce = 0f; LongitudeForce = 0f;
                TractionBoost = tractionBoost; GripBoost = gripScale; AllowSlip = false;
            }

            public void Stop() { AV = 0f; Slip = 0f; RoadSpeed = 0f; SlipAngle = 0f; }

            public void ApplyTorque(float t) { if (!BrakeLocked) AppliedTorque += t; }

            public void UpdateFree(float dT)
            {
                Traction = 0f; Load = 0f; Slip = 0f; SlipAngle = 0f;
                BrakeLocked = EBrake > 0f && _axle == 1;
                if (BrakeLocked) AV = 0f;
                else AV += AppliedTorque / WheelMomentOfInertia * dT;
                LateralForce = 0f; LongitudeForce = 0f; LastTorque = 0f; AngularAcc = 0f;
            }

            public float UpdateLoaded(float latVel, float fwdVel, float bodySpeed, float load, float dT, float dragReduction)
            {
                if (Load <= 0f && !BrakeLocked) AV = fwdVel / Radius;
                float fwdAcc = (fwdVel - RoadSpeed) / dT;
                RoadSpeed = fwdVel;
                LateralSpeed = latVel;
                Load = Mathf.Max(load, 0f);
                Traction = 1f;
                BrakeLocked = EBrake > 0f && _axle == 1;
                bool braking = Brake + EBrake > 0f;
                if (braking)
                {
                    if (Mathf.Abs(fwdVel) < 1f) AppliedTorque += (-Brake * load * fwdVel) / Radius + (-EBrake * load * fwdVel) / Radius;
                    else
                    {
                        float total = Brake * (_axle == 0 ? _c.BrakeFront : _c.BrakeRear) * FTLB2NM * 10f + EBrake * _c.EBrake * FTLB2NM * 10f;
                        AppliedTorque += AV <= 0f ? total : -total;
                    }
                }
                SlipAngle = Mathf.Atan2(latVel, Mathf.Abs(fwdVel));
                Slip = BrakeLocked || AllowSlip ? AV * Radius - fwdVel : 0f;
                bool slipping = false;
                float slipRatio = 1f, absSlip = Mathf.Abs(Slip);
                if (AllowSlip && absSlip > MaxSlip)
                {
                    slipRatio = MaxSlip / absSlip;
                    Traction *= slipRatio;
                    slipping = true;
                }
                float skid = Mathf.Sqrt(Slip * Slip + latVel * latVel);
                float range = Ramp(Mathf.Abs(SlipAngle), 0f, 12f * Mathf.Deg2Rad);
                LateralForce = GripBoost * range * 1.54f * Load * GripScale * TractionBoost;
                if (SlipAngle > 0f) LateralForce = -LateralForce;
                if (BrakeLocked && skid > 1f)
                {
                    LateralForce = -latVel * Load * TractionBoost * Dyn * 1.5f / skid;
                    LongitudeForce = Slip * Load * TractionBoost * Dyn / (skid * 1.5f);
                }
                else if (slipping)
                {
                    float pilot = Ramp(bodySpeed, 30f * MPH2MPS, 50f * MPH2MPS) * (1f - 0.85f) + 0.85f;
                    float dynamic = Dyn * TractionBoost * Load * pilot / skid * Slip;
                    float ground = AppliedTorque / Radius;
                    // UMath::Limit: the dynamic friction, no larger than the ground torque's force
                    LongitudeForce = Mathf.Abs(dynamic) > Mathf.Abs(ground) ? ground : dynamic;
                }
                else LongitudeForce = AppliedTorque / Radius;
                if (AllowSlip && !slipping && !BrakeLocked)
                    LongitudeForce += (AngularAcc * Radius - fwdAcc) * 10f / Radius;
                float sq = LongitudeForce * LongitudeForce + LateralForce * LateralForce;
                float max = Load * Stat * TractionBoost * (Brake * 2.5f + 1f);
                if (sq > max * max && sq > 1e-6f)
                {
                    float ratio = max / Mathf.Sqrt(sq);
                    Traction *= ratio; LateralForce *= ratio; LongitudeForce *= ratio;
                    slipRatio *= ratio * ratio;
                }
                if (Mathf.Abs(fwdVel) > 1f) LongitudeForce -= Mathf.Sin(SlipAngle) * LateralForce * dragReduction / GripScale;
                else LateralForce *= Mathf.Min(Mathf.Abs(latVel), 1f);
                if (!braking && _index >= 2)
                {
                    LongitudeForce *= Surface.DriveGrip;
                    LateralForce *= Surface.LateralGrip;
                }
                if (BrakeLocked) { AngularAcc = 0f; AV = 0f; }
                else if (AllowSlip && slipRatio < 1f)
                {
                    float torque = (AppliedTorque - LongitudeForce * Radius + LastTorque) * 0.5f;
                    LastTorque = torque;
                    float eff = torque - AV * 1.5f * Surface.RollingResistance;
                    AngularAcc = Mathf.Lerp(eff * 0.1f - slipRatio * Slip / (Radius * dT), fwdAcc / Radius, slipRatio);
                    AV += AngularAcc * dT;
                }
                else AV = RoadSpeed / Radius;
                return LateralForce;
            }
        }

        readonly Tire[] _t = new Tire[4];
        readonly float[] _wheelSteer = new float[2];
        float _maxSteering = 45f, _againstWall, _timeInAir, _sleepTime;

        public SuspensionSimple(TrafficVehicle owner) : base(owner)
        {
            for (int i = 0; i < 4; i++) _t[i] = new Tire(i < 2 ? C.TireRadiusFront : C.TireRadiusRear, i, C);
            CreateArms();
        }

        public override float WheelRadius(int i) => _t[i].Radius;
        public override float WheelAngularVelocity(int i) => _t[i].AV;
        public override void SetWheelAngularVelocity(int i, float w) => _t[i].AV = w;
        public override float WheelSteerDeg(int i) => i < 2 ? _wheelSteer[i] * Mathf.Rad2Deg : 0f;
        public override float MaxSteeringDeg => _maxSteering;
        public override float WheelLoad(int i) => _t[i].Load;
        public override float WheelSlip(int i) => _t[i].Slip;
        public override float WheelSkid(int i) => _t[i].LateralSpeed;
        public override float WheelSlipAngle(int i) => _t[i].SlipAngle;

        public override void MatchSpeed(float speed)
        {
            foreach (var t in _t)
            {
                t.AV = speed / t.Radius; t.RoadSpeed = speed; t.Traction = 1f; t.BrakeLocked = false;
                t.Slip = 0f; t.AngularAcc = 0f; t.LastTorque = 0f; t.Brake = 0f; t.EBrake = 0f;
            }
        }

        public override void Reset()
        {
            ComputeState(Time.fixedDeltaTime, out var s);
            int n = 0;
            for (int i = 0; i < 4; i++)
            {
                var w = Wheels[i];
                var t = _t[i];
                t.AV = 0f; t.Load = 0f; t.Brake = 0f; t.EBrake = 0f; t.Slip = 0f; t.Traction = 1f; t.BrakeLocked = false; t.MaxSlip = 0.5f;
                if (w.UpdatePosition(s, s.WorldCog, t.Radius, S.Dimension.y * 2f))
                {
                    float c = w.Penetration + RideHeight(i);
                    w.SetCompression(c <= 1e-6f ? 0f : c);
                    if (w.Compression > 0f) n++;
                }
                else w.SetCompression(0f);
            }
            NumWheelsOnGround = n;
            _againstWall = 0f;
            _timeInAir = 0f;
            _sleepTime = 0f;
        }

        /// <summary>SuspensionSimple::OnCollision: a slow nose-first world hit near the front sets the wall steer side.</summary>
        public void OnWorldCollision(Vector3 normal, Vector3 point, Vector3 closingVel)
        {
            if (Mathf.Abs(normal.y) >= 0.1f || _againstWall != 0f || closingVel.magnitude >= 7.5f) return;
            var rb = Owner.Rb;
            var fwd = rb.rotation * Vector3.forward;
            var right = rb.rotation * Vector3.right;
            if (Vector3.Dot(normal, fwd) > 0f) return;
            if (Vector3.Dot(point - Owner.BodyOrigin, fwd) <= S.Dimension.z * 0.75f) return;
            float dot = Vector3.Dot(normal, right);
            _againstWall = (dot > 0f ? 1f : -1f) - dot;
        }

        public override void Simulate(float dT)
        {
            float extraRide = NumWheelsOnGround > 1 ? 2f : NumWheelsOnGround == 0 ? -10f : 0f;
            SetCOG(0f, extraRide);
            ComputeState(dT, out var s);
            if (_sleepTime > 3f && DoSleep(s) == SleepState.All) return;
            float maxSlip = ComputeMaxSlip(s), grip = ComputeLateralGripScale(s), traction = ComputeTractionScale(s);
            foreach (var t in _t) t.BeginFrame(maxSlip, grip, traction);
            float over = Mathf.Abs(s.SlipAngle - s.AngularVel.y * 0.25f);
            float gf = Ramp(over, 0f, 35f * Mathf.Deg2Rad) * Ramp(s.Speed, 0f, 20f) * 0.15f;
            if (s.DriverClass == 6) gf = 0f;
            _t[2].TractionBoost = gf + 1f;
            _t[3].TractionBoost = gf + 1f;
            // ICheater::GetCatchupCheat (OnTaskSimulate :682-690): drag × lerp(1, 0, cheat), downforce × lerp(1, 1.5, cheat);
            // a drag race keeps at least 1.5 downforce
            float aero = 1f, drag = 1f;
            if (Owner.CatchupCheat is float cheat)
            {
                drag = Mathf.Lerp(drag, 0f, cheat);
                aero = Mathf.Lerp(aero, 1.5f, cheat);
            }
            if (s.DriverStyle == StyleDrag) aero = Mathf.Max(aero, 1.5f);
            DoAerodynamics(s, drag, aero, Wheels[0].LocalArm.z, Wheels[2].LocalArm.z);
            DoDriveForces(s);
            DoWheelForces(s);
            _againstWall = 0f;
            _timeInAir = NumWheelsOnGround != 0 ? 0f : _timeInAir + dT;
            if ((s.Flags & 2) == 0) DoJumpStabilizer(s);
            if (DoSleep(s) == SleepState.All)
            {
                foreach (var t in _t) t.Stop();
                _sleepTime += dT;
            }
            else _sleepTime = 0f;
        }

        void DoDriveForces(in State s)
        {
            if (!Owner.HasEngine) return;
            float torque = Owner.DriveTorque;
            if (Mathf.Abs(torque) <= 1e-6f) return;
            float split = C.TorqueSplit;
            bool staging = (s.Flags & State.IsStaging) != 0;
            bool slipTires = staging || (s.Gear < 4 && s.Gas > 0.5f);
            float boost = s.NosBoost * s.ShiftBoost * (staging ? 0.25f : 1f);
            float front = torque * split, rear = torque * (1f - split);
            for (int i = 0; i < 4; i++)
            {
                float t = i < 2 ? front : rear;
                if (t == 0f || !Wheels[i].IsOnGround) continue;
                _t[i].ApplyTorque(t * 0.5f);
                _t[i].AllowSlip = slipTires;
                _t[i].TractionBoost *= boost;
            }
        }

        void DoWheelForces(in State s)
        {
            float dT = s.Time;
            // DoSteering: 45..60° by brake / lift, × STEERING, Ackermann; then the wall steer
            _maxSteering = Mathf.Lerp(45f, 60f, Mathf.Max(Mathf.Max(s.EBrake, s.Brake), 1f - s.Gas));
            float steer = s.Steer * _maxSteering;
            if (s.DriverStyle != StyleDrag) steer *= C.Steering;
            ComputeAckerman(steer * Mathf.Deg2Rad, s, out var steerL, out _wheelSteer[0], out var steerR, out _wheelSteer[1]);
            if (_againstWall != 0f && NumWheelsOnGround > 2 && s.Gas > 0f)
            {
                float dW = s.Steer * s.Gas * 0.125f;
                if (_againstWall * dW >= 0f)
                    Owner.Rb.angularVelocity = s.AngularVel + s.ToWorld(new Vector3(0, dW * Mathf.Abs(_againstWall), 0));
            }
            for (int i = 2; i < 4; i++) _t[i].EBrake = s.EBrake;
            for (int i = 0; i < 4; i++) _t[i].Brake = s.Brake;
            float dragReduction = 0.5f;
            // the AI's turning drag: lerp(Tweak_AITurningDragReductionMin 0.23, 0.5, catch-up cheat) for a RACER not drag racing
            if (s.DriverClass == 3 && s.DriverStyle != StyleDrag) dragReduction = Mathf.Lerp(0.23f, 0.5f, Owner.CatchupCheat ?? 0f);
            int onGround = 0;
            float maxDelta = 0f;
            float sw0 = (Wheels[0].Compression - Wheels[1].Compression) * C.SwayFront * LBIN2NM;
            float sw1 = (Wheels[2].Compression - Wheels[3].Compression) * C.SwayRear * LBIN2NM;
            var dirs = new[] { steerL, steerR, s.Forward, s.Forward };
            bool resolve = false;
            for (int i = 0; i < 4; i++)
            {
                int axle = i >> 1;
                var w = Wheels[i];
                var t = _t[i];
                w.UpdatePosition(s, s.WorldCog, t.Radius, s.Dimension.y * 2f);
                t.Surface = w.Surface;
                var ground = w.Normal;
                var fwd = dirs[i];
                var lat = Vector3.Cross(ground, fwd).normalized;
                float upness = Mathf.Clamp01(Vector3.Dot(ground, s.Up));
                float travel = (axle == 0 ? C.TravelFront : C.TravelRear) * IN;
                float comp = RideHeight(i) * upness + w.Penetration;
                if (w.Compression == 0f) maxDelta = Mathf.Max(maxDelta, comp - travel);
                comp = Mathf.Max(comp, 0f);
                if (comp > travel) { maxDelta = Mathf.Max(maxDelta, comp - travel); comp = travel; }
                if (comp > 1e-6f && upness > EnableRollStopsThreshold)
                {
                    onGround++;
                    float rise = (comp - w.Compression) / dT;
                    float valving = (axle == 0 ? C.ValvingFront : C.ValvingRear) * IN;
                    float digression = 1f - (axle == 0 ? C.DigressionFront : C.DigressionRear);
                    if (valving > 1e-6f && digression < 1f && Mathf.Abs(rise) > valving)
                    {
                        float dg = valving * Mathf.Pow(Mathf.Abs(rise) / valving, digression);
                        rise = rise > 0f ? dg : -dg;
                    }
                    float k = (axle == 0 ? C.SpringFront : C.SpringRear) * LBIN2NM;
                    float spring = comp * k * (comp * (axle == 0 ? C.SpringProgFront : C.SpringProgRear) + 1f);
                    float damp = rise > 0f ? rise * (axle == 0 ? C.ShockFront : C.ShockRear) * LBIN2NM : rise * (axle == 0 ? C.ShockExtFront : C.ShockExtRear) * LBIN2NM;
                    if (damp > C.ShockBlowout * 9.81f * s.Mass) damp = 0f;
                    float sway = i == 0 ? sw0 : i == 1 ? -sw0 : i == 2 ? sw1 : -sw1;
                    float load = Mathf.Max(damp + spring + sway, 0f);
                    float xs = Vector3.Dot(w.Velocity, lat), zs = Vector3.Dot(w.Velocity, fwd);
                    float tf = t.UpdateLoaded(xs, zs, s.LocalVel.z, load, dT, dragReduction);
                    float maxTraction = Mathf.Abs(xs / dT * (0.25f * s.Mass));
                    w.Force = lat * Mathf.Clamp(tf, -maxTraction, maxTraction) + Vector3.Cross(lat, ground).normalized * t.LongitudeForce + s.Up * load;
                    resolve = true;
                }
                else
                {
                    w.Force = Vector3.zero;
                    t.UpdateFree(dT);
                }
                if (comp == 0f) w.AirTime += dT; else w.AirTime = 0f;
                w.SetCompression(comp);
            }
            if (resolve)
            {
                var tf = Vector3.zero;
                var tt = Vector3.zero;
                var cg = s.Position + s.WorldCog;
                for (int i = 0; i < 4; i++)
                {
                    var w = Wheels[i];
                    var p = w.LocalArm;
                    p.y += w.Compression - RideHeight(i);
                    p = s.Position + s.ToWorld(p);
                    w.Position = p;
                    tf += w.Force;
                    tt += Vector3.Cross(p - cg, w.Force);
                }
                // YAW_SPEED scales the yaw part of the wheel torque
                float yaw = Vector3.Dot(s.Up, tt);
                tt += s.Up * (yaw * C.YawSpeed - yaw);
                Owner.Resolve(tf, tt);
            }
            if (maxDelta > 0f)
            {
                for (int i = 0; i < 4; i++) Wheels[i].Position.y += maxDelta;
                Owner.ModifyYPos(maxDelta);
            }
            if (onGround != 0 && NumWheelsOnGround == 0)
            {
                // landing halves the yaw rate
                var lav = s.LocalAngularVel;
                lav.y *= 0.5f;
                Owner.Rb.angularVelocity = s.ToWorld(lav);
            }
            NumWheelsOnGround = onGround;
        }
    }
}
