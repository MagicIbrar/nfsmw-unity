using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// Physics/Behaviors/SuspensionTrailer.cpp (speed.exe Construct 0x6B6BF0, Tire::UpdateLoaded 0x6906AD): the semi
    /// trailer's chassis. No steering, no drive. Brakes BRAKES·1.3558·4, e-brake EBRAKE·1.3558·10 on all four wheels;
    /// cornering force load·GRIP_SCALE·TrailerCorneringForce(|slip angle|) with the PC table {0, .4, .8, 1.2, 1.6,
    /// 1.75, 1.65} over 0..12° (Table at .data 0x8F8FC4: min 0, max 1/30 rev, scale 180; the GameCube source says 1/3),
    /// rear axle ×1.5 (×0.75 on the e-brake), capped by STATIC_GRIP and scaled to DYNAMIC_GRIP when over it.
    /// While unhitched (Articulation not joined) its inputs are zeroed and the CG moves to the box centre.
    /// </summary>
    public sealed class SuspensionTrailer : Chassis
    {
        sealed class Tire
        {
            public readonly float Radius;
            public float Brake, EBrake, AV, Load, LateralForce, LongitudeForce, AppliedTorque, Slip, RoadSpeed, LateralSpeed;
            public bool Slipping;
            readonly int _axle;
            readonly CarSpec _c;
            static readonly float[] Cornering = { 0f, 0.4f, 0.8f, 1.2f, 1.6f, 1.75f, 1.65f };
            const float CorneringMaxRad = 12f * Mathf.Deg2Rad;

            public Tire(float radius, int index, CarSpec c)
            {
                Radius = Mathf.Max(radius, 0.1f);
                _axle = index >> 1;
                _c = c;
            }

            public void BeginFrame() { AppliedTorque = 0f; LateralForce = 0f; LongitudeForce = 0f; }

            public void UpdateFree()
            {
                Load = 0f;
                Slip = 0f;
                Slipping = false;
                if (EBrake > 0f || Brake > 0f) AV = 0f;
                LateralForce = 0f;
                LongitudeForce = 0f;
            }

            public void UpdateLoaded(float latVel, float fwdVel, float load)
            {
                float brakeSpec = (_axle == 0 ? _c.BrakeFront : _c.BrakeRear) * FTLB2NM * 4f;
                float ebrakeSpec = _c.EBrake * FTLB2NM * 10f;
                float dyn = _axle == 0 ? _c.DynamicGripFront : _c.DynamicGripRear;
                float stat = _axle == 0 ? _c.StaticGripFront : _c.StaticGripRear;
                float grip = _axle == 0 ? _c.GripScaleFront : _c.GripScaleRear;
                RoadSpeed = fwdVel;
                LateralSpeed = latVel;
                Load = Mathf.Max(load, 0f);
                float bt = Brake * brakeSpec, ebt = EBrake * ebrakeSpec;
                if (fwdVel > 0f) bt = -bt;
                AppliedTorque += bt;
                if (fwdVel > 0f) ebt = -ebt;
                AppliedTorque += ebt;
                float slip = Mathf.Atan2(latVel, Mathf.Abs(fwdVel));
                LongitudeForce = AppliedTorque / Radius;
                Slip = AV * Radius - fwdVel;
                LateralForce = Load * grip * Table(Cornering, Mathf.Abs(slip) / CorneringMaxRad);
                if (latVel > 0f) LateralForce = -LateralForce;
                if (EBrake > 0.5f)
                {
                    if (_axle == 1) LateralForce *= 0.75f;
                    AV = 0f;
                    Slipping = true;
                }
                else
                {
                    if (_axle == 1) LateralForce *= 1.5f;
                    AV = fwdVel / Radius;
                }
                float len = Mathf.Sqrt(LateralForce * LateralForce + LongitudeForce * LongitudeForce);
                if (len > Load * stat && len > 0.001f)
                {
                    float ratio = Load * dyn / len;
                    LateralForce *= ratio;
                    LongitudeForce *= ratio;
                }
                if (Mathf.Abs(fwdVel) > 1f) LongitudeForce -= Mathf.Sin(slip) * LateralForce * 0.5f;
                else LateralForce *= Mathf.Min(Mathf.Abs(latVel), 1f);
                if (EBrake > 0f)
                {
                    AV = 0f;
                    Slip = -fwdVel;
                }
            }
        }

        readonly Tire[] _t = new Tire[4];

        public SuspensionTrailer(TrafficVehicle owner) : base(owner)
        {
            for (int i = 0; i < 4; i++) _t[i] = new Tire(i < 2 ? C.TireRadiusFront : C.TireRadiusRear, i, C);
            CreateArms();
        }

        public override float WheelRadius(int i) => _t[i].Radius;
        public override float WheelAngularVelocity(int i) => _t[i].AV;
        public override float MaxSteeringDeg => 0f;
        public override float WheelLoad(int i) => _t[i].Load;
        public override float WheelSlip(int i) => _t[i].Slip;
        public override float WheelSkid(int i) => _t[i].LateralSpeed;

        public override void MatchSpeed(float speed)
        {
            for (int i = 0; i < 4; i++) _t[i].AV = speed / _t[i].Radius;
        }

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
            }
            NumWheelsOnGround = n;
        }

        /// <summary>SuspensionTrailer::ComputeState: unhitched, no inputs and the CG at the box centre; hitched, at ROLL_CENTER, z 0.</summary>
        protected override void ComputeState(float dT, out State s)
        {
            base.ComputeState(dT, out s);
            if (!Owner.Joined)
            {
                s.EBrake = 0f;
                s.Brake = 0f;
                s.Gas = 0f;
                s.Cog = Vector3.zero;
            }
            else
            {
                float ride = Mathf.Max(RideHeight(0), RideHeight(2));
                s.Cog = new Vector3(0f, C.RollCenter * IN - (s.Dimension.y + ride), 0f);
            }
            Owner.SetCenterOfGravity(s.Cog);
            s.WorldCog = s.ToWorld(s.Cog);
        }

        public override void Simulate(float dT)
        {
            ComputeState(dT, out var s);
            for (int i = 0; i < 4; i++) _t[i].BeginFrame();
            Owner.ResolveForce(-(s.Speed * C.DragCoefficient) * s.LinearVel);
            DoWheelForces(s);
        }

        void DoWheelForces(in State s)
        {
            float dT = s.Time;
            for (int i = 0; i < 4; i++) { _t[i].Brake = s.Brake; _t[i].EBrake = s.EBrake; }
            int onGround = 0;
            float maxDelta = 0f;
            float sw0 = (Wheels[0].Compression - Wheels[1].Compression) * C.SwayFront * LBIN2NM;
            float sw1 = (Wheels[2].Compression - Wheels[3].Compression) * C.SwayRear * LBIN2NM;
            bool resolve = false;
            for (int i = 0; i < 4; i++)
            {
                int axle = i >> 1;
                var w = Wheels[i];
                var t = _t[i];
                w.UpdatePosition(s, s.WorldCog, t.Radius, s.Dimension.y * 2f);
                var ground = w.Normal;
                var lat = Vector3.Cross(ground, s.Forward).normalized;
                float upness = Mathf.Clamp01(Vector3.Dot(s.Up, ground));
                float travel = (axle == 0 ? C.TravelFront : C.TravelRear) * IN;
                float comp = RideHeight(i) * upness + w.Penetration;
                if (w.Compression == 0f) maxDelta = Mathf.Max(maxDelta, comp - travel);
                comp = Mathf.Max(comp, 0f);
                if (comp > travel) { maxDelta = Mathf.Max(maxDelta, comp - travel); comp = travel; }
                if (comp > 0f && upness > EnableRollStopsThreshold)
                {
                    onGround++;
                    float k = (axle == 0 ? C.SpringFront : C.SpringRear) * LBIN2NM;
                    float rise = (comp - w.Compression) / dT;
                    float spring = comp * k * (comp * (axle == 0 ? C.SpringProgFront : C.SpringProgRear) + 1f);
                    float damp = rise * (axle == 0 ? C.ShockFront : C.ShockRear) * LBIN2NM;
                    if (damp > C.ShockBlowout * 9.81f * s.Mass) damp = 0f;
                    float sway = i == 0 ? sw0 : i == 1 ? -sw0 : i == 2 ? sw1 : -sw1;
                    float springForce = Mathf.Max(damp + spring + sway, 0f);
                    float load = Mathf.Max(upness * 4f - 3f, 0.3f) * springForce;
                    float xs = Vector3.Dot(w.Velocity, lat), zs = Vector3.Dot(w.Velocity, s.Forward);
                    t.UpdateLoaded(xs, zs, load);
                    float maxTraction = Mathf.Abs(xs / dT * (0.25f * s.Mass));
                    float traction = Mathf.Clamp(t.LateralForce, -maxTraction, maxTraction);
                    w.Force = lat * traction + Vector3.Cross(lat, ground).normalized * t.LongitudeForce + s.Up * springForce;
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
                    Owner.Resolve(w.Force, Vector3.Cross(p - cg, w.Force));
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
