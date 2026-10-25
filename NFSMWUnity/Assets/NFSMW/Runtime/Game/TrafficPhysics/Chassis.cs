using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// Physics/Wheel.h + Common/Wheel.cpp: one wheel's arm on the body, its world position and point velocity, and the
    /// ground under it. mNormal.w is the ground's height over the arm point along the face normal (positive when the
    /// arm is below the road), which the suspensions turn into compression = RIDE_HEIGHT·upness + w.
    /// </summary>
    public sealed class Wheel
    {
        public Vector3 LocalArm, WorldArm, Position, Velocity, Force;
        public Vector3 Normal = Vector3.up;
        /// <summary>mNormal.w.</summary>
        public float Penetration;
        public float Compression, AirTime;
        public WorldCollision.Surface Surface = WorldCollision.Surface.Default;
        public bool IsOnGround => Compression > 0f;
        static readonly RaycastHit[] Hits = new RaycastHit[16];

        public void SetCompression(float c) => Compression = Mathf.Max(c, 0f);

        /// <summary>The wheel's WWorldPos (Wheel ctor: Wheel(flags) → mWorldPos), in sim axes over the loaded packs.</summary>
        readonly Core.WWorldPos _wpos = new Core.WWorldPos(0.025f);

        /// <summary>
        /// Wheel::UpdatePosition (Common/Wheel.cpp; PC 0x66A450 range): the arm and its velocity (v + ω × (arm − cog)),
        /// then WWorldPos::SetTolerance(min(max(−vy·dT, 0) + radius + |v_xz|·dT, height/2)) and WWorldPos::Update(position,
        /// normal, usecache = IsOnGround, keep_valid = true): the road face found from `tolerance` above the arm point
        /// downward over the exe's own collision packs (Core.WWorldPos / Dynamics.WorldQueries, the same faces the Unity
        /// ground colliders are built from); normal.w = the signed distance to the face plane (positive below it), the
        /// surface its simsurface (SimSurface::kNull with no face). With no pack loaded at all (WorldQueries empty) the old
        /// world-down raycast stands in [ours].
        /// </summary>
        public bool UpdatePosition(in Chassis.State s, Vector3 worldCog, float radius, float vehicleHeight)
        {
            WorldArm = s.Rotation * LocalArm;
            Position = s.Position + WorldArm;
            Velocity = s.LinearVel + Vector3.Cross(s.AngularVel, WorldArm - worldCog);
            float tolerance = Mathf.Min(Mathf.Max(-Velocity.y * s.Time, 0f) + radius + new Vector2(Velocity.x, Velocity.z).magnitude * s.Time, vehicleHeight * 0.5f);
            if (Dynamics.WorldQueries.InstanceCount > 0)
            {
                _wpos.YOffset = tolerance;
                var w = Dynamics.WorldQueries.Sim;
                var pos = Dynamics.WorldQueries.ToSim(Position);
                bool ok = _wpos.Update(w, pos, out var n, out float planeDist, IsOnGround, true);
                Normal = Dynamics.WorldQueries.ToUnity(n);
                if (!ok)
                {
                    Penetration = -1f;   // WWorldPos::Update leaves w = -1 without a face
                    Surface = WorldCollision.Surface.Null;
                    return false;
                }
                Penetration = planeDist;
                Surface = _wpos.SurfaceNull ? WorldCollision.Surface.Null : WorldCollision.SurfaceOf(_wpos.Face.SurfaceKey);
                return true;
            }
            // [ours] no collision packs registered: search from half the vehicle height above the arm to its full reach below
            float above = vehicleHeight * 0.5f + tolerance;
            float below = s.MaxReach + tolerance;
            int hits = Physics.RaycastNonAlloc(Position + Vector3.up * above, Vector3.down, Hits, above + below, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            int bi = -1;
            for (int i = 0; i < hits; i++)
            {
                var h = Hits[i];
                if (h.distance <= 0f || h.normal.y <= 0f || !WorldCollision.IsGround(h.collider)) continue;
                if (h.distance < best) { best = h.distance; bi = i; }
            }
            if (bi < 0)
            {
                Normal = Vector3.up;
                Penetration = -below - 1f;
                Surface = WorldCollision.Surface.Default;
                return false;
            }
            Normal = Hits[bi].normal;
            Penetration = Vector3.Dot(Hits[bi].point - Position, Normal);
            Surface = WorldCollision.SurfaceAt(Hits[bi]);
            return true;
        }
    }

    /// <summary>
    /// Physics/Behaviors/Chassis.cpp: the shared base of the suspension behaviours. State (ComputeState :277), SetCOG
    /// (:264), DoSleep (:139), ComputeAckerman (:209), the grip/traction speed tables and DoAerodynamics /
    /// DoJumpStabilizer for SuspensionSimple. Vectors are Unity world space; the body frame is the vehicle's local frame
    /// moved to the collision-geometry root (x right, y up, z forward, as the sim's). Angles are radians.
    /// </summary>
    public abstract class Chassis
    {
        public struct State
        {
            public Quaternion Rotation;
            public Vector3 Right, Up, Forward;
            /// <summary>Body origin (RigidBody position: the collision root's centre).</summary>
            public Vector3 Position;
            public Vector3 LocalVel, LinearVel, AngularVel, LocalAngularVel;
            /// <summary>Centre of gravity in the body frame, and rotated into world axes (relative to Position).</summary>
            public Vector3 Cog, WorldCog;
            public Vector3 Dimension, Inertia;
            public float Gas, Brake, EBrake, Steer;
            public float GroundEffect, Mass, Speed, SlipAngle, Time;
            public int Flags, Gear;
            public float NosBoost, ShiftBoost;
            public int DriverStyle, DriverClass;
            /// <summary>[ours] how far below the arm the ground search reaches (ride height + travel + radius).</summary>
            public float MaxReach;
            public const int IsStaging = 1, IsDestroyed = 2;

            public Vector3 ToLocal(Vector3 v) => Quaternion.Inverse(Rotation) * v;
            public Vector3 ToWorld(Vector3 v) => Rotation * v;
        }

        public enum SleepState { None = 0, All = 1, Lateral = 2 }

        public const float IN = 0.0254f, LBIN2NM = 175.1268f, FTLB2NM = 1.3558f, MPH2MPS = 0.44704f;
        /// <summary>VehicleSystem::ENABLE_ROLL_STOPS_THRESHOLD (speed.exe .data 0x8F8760 = 0.2).</summary>
        public static float EnableRollStopsThreshold = 0.2f;
        public const int GReverse = 0, GNeutral = 1, GFirst = 2;
        public const int StyleDrag = 1;

        protected readonly TrafficVehicle Owner;
        protected readonly VehicleSpecs S;
        protected readonly CarSpec C;
        public readonly Wheel[] Wheels = new Wheel[4];
        public int NumWheelsOnGround { get; protected set; }

        protected Chassis(TrafficVehicle owner)
        {
            Owner = owner;
            S = owner.Specs;
            C = S.Spec;
            for (int i = 0; i < 4; i++) Wheels[i] = new Wheel();
        }

        public abstract void Simulate(float dT);
        public abstract void Reset();
        public abstract void MatchSpeed(float speed);
        public abstract float WheelRadius(int i);
        public abstract float WheelAngularVelocity(int i);
        public virtual void SetWheelAngularVelocity(int i, float w) { }
        public virtual float WheelSteerDeg(int i) => 0f;
        public virtual float MaxSteeringDeg => 45f;
        public virtual float WheelLoad(int i) => 0f;
        public virtual float WheelSlip(int i) => 0f;
        public virtual float WheelSkid(int i) => 0f;
        public virtual float WheelSlipAngle(int i) => 0f;
        public float Compression(int i) => Wheels[i].Compression;
        public float RideHeight(int i) => (i < 2 ? C.RideHeightFront : C.RideHeightRear) * IN;

        /// <summary>The four wheel arms (SuspensionTraffic.cpp:365-381): track − tire width apart, at the body's bottom.</summary>
        protected void CreateArms()
        {
            float wf = C.TrackFront - S.SectionWidthFront * 0.001f, wr = C.TrackRear - S.SectionWidthRear * 0.001f;
            float y = -S.Dimension.y, zf = C.FrontAxle, zr = C.FrontAxle - C.WheelBase;
            Wheels[0].LocalArm = new Vector3(-wf * 0.5f, y, zf);
            Wheels[1].LocalArm = new Vector3(wf * 0.5f, y, zf);
            Wheels[2].LocalArm = new Vector3(-wr * 0.5f, y, zr);
            Wheels[3].LocalArm = new Vector3(wr * 0.5f, y, zr);
        }

        /// <summary>Chassis::SetCOG: FRONT_WEIGHT_BIAS between the axles (half when airborne), ROLL_CENTER under the box top.</summary>
        protected void SetCOG(float extraBias, float extraRide)
        {
            float frontZ = C.FrontAxle, rearZ = frontZ - C.WheelBase;
            float fwbias = (C.FrontWeightBias + extraBias) * 0.01f;
            if (NumWheelsOnGround == 0) fwbias = 0.5f;
            float cgZ = (frontZ - rearZ) * fwbias + rearZ;
            float cgY = C.RollCenter * IN - (S.Dimension.y + Mathf.Max((C.RideHeightFront + extraRide) * IN, (C.RideHeightRear + extraRide) * IN));
            Owner.SetCenterOfGravity(new Vector3(0, cgY, cgZ));
        }

        /// <summary>Chassis::ComputeState.</summary>
        protected virtual void ComputeState(float dT, out State s)
        {
            var rb = Owner.Rb;
            s = default;
            s.Time = dT;
            s.Rotation = rb.rotation;
            s.Right = s.Rotation * Vector3.right;
            s.Up = s.Rotation * Vector3.up;
            s.Forward = s.Rotation * Vector3.forward;
            s.Position = Owner.BodyOrigin;
            s.Dimension = S.Dimension;
            s.Inertia = rb.inertiaTensor;
            s.LinearVel = rb.linearVelocity;
            s.LocalVel = s.ToLocal(s.LinearVel);
            s.AngularVel = rb.angularVelocity;
            s.LocalAngularVel = s.ToLocal(s.AngularVel);
            s.Speed = s.LinearVel.magnitude;
            s.SlipAngle = s.LocalVel.z < 1f ? 0f : Mathf.Atan2(s.LocalVel.x, s.LocalVel.z);
            s.Gas = Mathf.Clamp01(Owner.Gas);
            s.Brake = Mathf.Clamp01(Owner.Brake);
            s.EBrake = Owner.EBrake;
            s.Steer = Mathf.Clamp(Owner.Steer, -1f, 1f);
            s.Cog = Owner.CenterOfGravity;
            s.GroundEffect = NumWheelsOnGround * 0.25f;
            s.Mass = rb.mass;
            s.DriverStyle = Owner.DriverStyle;
            s.DriverClass = Owner.DriverClass;
            if (Owner.Staging) s.Flags |= State.IsStaging;
            s.NosBoost = Owner.NosBoost;
            s.ShiftBoost = 1f;
            s.Gear = Owner.EngineGear;
            bool destroyed = Owner.IsDestroyed;
            if (destroyed)
            {
                s.Brake = 1f;
                s.Gas = 0f;
                s.EBrake = 1f;
                s.Flags |= State.IsDestroyed;
            }
            s.WorldCog = s.ToWorld(s.Cog);
            s.MaxReach = Mathf.Max(RideHeight(0), RideHeight(2)) + Mathf.Max(C.TravelFront, C.TravelRear) * IN + Mathf.Max(C.TireRadiusFront, C.TireRadiusRear);
        }

        // ---- Chassis::ComputeMaxSlip / ComputeLateralGripScale / ComputeTractionScale (Chassis.cpp:84-137) ----

        static readonly float[] TractionVsSpeed = { 0.909f, 1.045f, 1.09f, 1.09f, 1.09f, 1.09f, 1.09f, 1.045f, 1f, 1f };
        static readonly float[] GripVsSpeed = { 0.833f, 0.958f, 1.008f, 1.0167f, 1.033f, 1.033f, 1.033f, 1.0167f, 1f, 1f };

        protected static float ComputeMaxSlip(in State s) => s.Gear == GReverse ? 71f : Ramp(s.Speed, 10f, 71f) + 0.5f;

        protected static float ComputeLateralGripScale(in State s)
        {
            if (s.DriverStyle == StyleDrag) return 3f;
            return Table(GripVsSpeed, Ramp(s.Speed, 0f, 85f * MPH2MPS)) * 1.2f;
        }

        protected static float ComputeTractionScale(in State s)
        {
            float r = s.DriverStyle == StyleDrag ? 1.1f : Table(TractionVsSpeed, Ramp(s.Speed, 0f, 85f * MPH2MPS)) * 1.1f;
            return s.Gear == GReverse ? 2f : r;
        }

        /// <summary>Chassis::DoSleep: parked on the brakes it damps to rest; slow and coasting, the sideways motion dies.</summary>
        protected SleepState DoSleep(in State s)
        {
            if ((s.Flags & State.IsStaging) != 0) return SleepState.None;
            if (s.Speed < 0.5f && NumWheelsOnGround == 4 && s.Brake + s.EBrake > 0f && s.Gas == 0f
                && s.AngularVel.magnitude < 0.25f && !Owner.HadCollision)
            {
                Owner.Damp(s.Speed < 1e-6f ? 1f : 1f - s.Speed);
                for (int i = 0; i < 4; i++) SetWheelAngularVelocity(i, 0f);
                return SleepState.All;
            }
            if (s.Speed < 1f && s.AngularVel.magnitude < 0.25f && s.Gas <= 0f)
            {
                var v = s.LocalVel;
                var w = s.LocalAngularVel;
                var f = s.ToLocal(Owner.PendingForce);
                var t = s.ToLocal(Owner.PendingTorque);
                v.x *= s.Speed;
                w.y *= s.Speed;
                // only the sideways force and the yaw torque are cancelled
                var fc = new Vector3(-f.x * (1f - s.Speed), 0, 0);
                var tc = new Vector3(0, -t.y * (1f - s.Speed), 0);
                Owner.Resolve(s.ToWorld(fc), s.ToWorld(tc));
                Owner.Rb.linearVelocity = s.ToWorld(v);
                Owner.Rb.angularVelocity = s.ToWorld(w);
                return SleepState.Lateral;
            }
            return SleepState.None;
        }

        /// <summary>Chassis::ComputeAckerman: the inside wheel turns more; returns (left, right) wheel directions and angles.</summary>
        protected void ComputeAckerman(float steering, in State s, out Vector3 left, out float leftAngle, out Vector3 right, out float rightAngle)
        {
            bool goingRight = true;
            float wheelbase = C.WheelBase, track = C.TrackFront;
            float inside = steering;
            if (inside > Mathf.PI) inside -= 2f * Mathf.PI;
            if (inside < 0f) { goingRight = false; inside = -inside; }
            float outside = wheelbase * inside / (track * inside + wheelbase);
            float s0, s1;
            if (goingRight) { s0 = inside; s1 = outside; }
            else { s1 = -inside; s0 = -outside; }
            right = s.ToWorld(new Vector3(Mathf.Sin(s0), 0, Mathf.Cos(s0)));
            rightAngle = s0;
            left = s.ToWorld(new Vector3(Mathf.Sin(s1), 0, Mathf.Cos(s1)));
            leftAngle = s1;
        }

        /// <summary>Chassis::DoAerodynamics (:330): drag ×2 off the throttle at a lowered centre, downforce at AERO_CG.</summary>
        protected void DoAerodynamics(in State s, float dragPct, float aeroPct, float aeroFrontZ, float aeroRearZ)
        {
            if (dragPct > 0f)
            {
                float drag = s.Speed * dragPct * C.DragCoefficient;
                drag += drag * (2f - 1f) * (1f - s.Gas);
                var center = s.Cog;
                if (s.GroundEffect >= 0.5f) center.y += -0.1f * (1f - s.Gas);
                Owner.ResolveForceAt(-drag * s.LinearVel, s.Position + s.ToWorld(center));
            }
            if (aeroPct > 0f)
            {
                float upness = s.GroundEffect >= 0.5f ? 1f : Mathf.Max(s.Up.y, 0f);
                var dir = s.Speed > 0.0001f ? s.LinearVel / s.Speed : s.Forward;
                // forwardness = max(AeroDropOffMin 0.4, max(dir·fwd, 0)^AeroDropOff 0.5)
                float fwd = Mathf.Max(0.4f, Mathf.Pow(Mathf.Max(Vector3.Dot(dir, s.Forward), 0f), 0.5f));
                // Physics::Info::AerodynamicDownforce (0x66BAE0): speed·2·AERO_COEFFICIENT·1000 — linear (was speed²·AERO)
                float down = aeroPct * upness * fwd * Dynamics.PhysicsInfo.AerodynamicDownforce(C, s.Speed);
                if (s.GroundEffect == 0f) down *= 0.8f;
                if (down > 0f)
                {
                    var center = s.Cog;
                    if (s.GroundEffect != 0f) center.z = (aeroFrontZ - aeroRearZ) * (C.AeroCg * 0.01f) + aeroRearZ;
                    Owner.ResolveForceAt(s.ToWorld(new Vector3(0, -down, 0)), s.Position + s.ToWorld(center));
                }
            }
        }

        float _jumpTime, _jumpAltitude;
        readonly Core.WWorldPos _bodyPos = new Core.WWorldPos(0.025f);
        static readonly Vector2[] JumpStabilization = { new Vector2(0, 0), new Vector2(0.4f, 0.15f), new Vector2(2f, 5f) };

        /// <summary>Chassis::DoJumpStabilizer (:420): extra landing gravity and pitch/roll damping in the air.</summary>
        protected void DoJumpStabilizer(in State s)
        {
            bool resolve = false;
            var df = Vector3.zero;
            var dt = Vector3.zero;
            // RBComplex::GetGroundNormal: the body's WWorldPos updated each tick at the body position (RigidBody.cpp:477,
            // 1315: usecache, keep_valid), w = the signed plane distance, so altitude = −w (0 / up without a face yet)
            var gn = Vector3.up;
            float altitude = 0f;
            if (Dynamics.WorldQueries.InstanceCount > 0)
            {
                if (_bodyPos.Update(Dynamics.WorldQueries.Sim, Dynamics.WorldQueries.ToSim(s.Position), out var n, out float w, true, true))
                {
                    gn = Dynamics.WorldQueries.ToUnity(n);
                    altitude = -w;
                }
            }
            else if (Physics.Raycast(s.Position, Vector3.down, out var hit, 50f, ~0, QueryTriggerInteraction.Ignore) && WorldCollision.IsGround(hit.collider))
            {
                gn = hit.normal;   // [ours] no collision packs registered
                altitude = hit.distance;
            }
            float groundDot = Vector3.Dot(s.Up, gn);
            if (NumWheelsOnGround == 0)
            {
                _jumpTime += s.Time;
                _jumpAltitude = Mathf.Max(_jumpAltitude, altitude);
                float accel = 3f;
                if (_jumpTime > 1f && groundDot > 0.96f && _jumpAltitude > 2f && s.LinearVel.y < 0f && altitude < 6f)
                    accel += (1f - Ramp(altitude, 0f, 6f)) * 10f * Ramp(s.Speed, 0f, 20f);
                df += new Vector3(0, -s.Mass * accel, 0);
                resolve = true;
            }
            else
            {
                _jumpTime = 0f;
                _jumpAltitude = 0f;
            }
            // passive stabiliser: fewer than two wheels down, upright, and the body not touching the road (IsInGroundContact:
            // leversInContact != 0; was "no wheel down")
            if (NumWheelsOnGround < 2 && s.Up.y > 0.8f && !Owner.GroundContact)
            {
                float damping = Ramp(s.Speed, 0f, 5f) * Graph(JumpStabilization, s.LocalAngularVel.magnitude);
                var m = Vector3.Scale(s.LocalAngularVel, s.Inertia) * -damping;
                m.y = 0f;
                dt += s.ToWorld(m);
                resolve = true;
            }
            if (NumWheelsOnGround == 0 && gn.y > 0.9f && s.Up.y > 0.1f && groundDot > 0.8f && altitude > 1e-6f)
            {
                float altRamp = 1f - Ramp(altitude, 0f, 15f), spRamp = Ramp(s.Speed, 0f, 5f);
                var flatFwd = Vector3.Cross(s.Right, gn).normalized;
                float dot = Vector3.Dot(flatFwd, s.Forward);
                if (dot < 0.99f)
                {
                    var axis = Vector3.Cross(flatFwd, s.Forward).normalized;
                    dt += axis * (s.Mass * spRamp * altRamp * 40f * (dot - 1f));
                    resolve = true;
                }
                var flatRight = Vector3.Cross(gn, s.Forward).normalized;
                dot = Vector3.Dot(flatRight, s.Right);
                if (dot < 0.99f)
                {
                    var axis = Vector3.Cross(flatRight, s.Right).normalized;
                    dt += axis * (s.Mass * spRamp * 20f * altRamp * (dot - 1f));
                    resolve = true;
                }
            }
            if (resolve) Owner.Resolve(df, dt);
        }

        public static float Ramp(float a, float min, float max)
        {
            float r = max - min;
            return r > 1e-6f ? Mathf.Clamp01((a - min) / r) : 0f;
        }

        /// <summary>Misc/Table: equally spaced samples over [0, 1], linear, clamped.</summary>
        public static float Table(float[] t, float x)
        {
            float p = Mathf.Clamp01(x) * (t.Length - 1);
            int i = Mathf.Min((int)p, t.Length - 2);
            return Mathf.Lerp(t[i], t[i + 1], p - i);
        }

        /// <summary>Misc/Graph: piecewise linear over (x, y) points, clamped at the ends.</summary>
        public static float Graph(Vector2[] g, float x)
        {
            if (x <= g[0].x) return g[0].y;
            for (int i = 1; i < g.Length; i++)
                if (x <= g[i].x) return Mathf.Lerp(g[i - 1].y, g[i].y, (x - g[i - 1].x) / Mathf.Max(1e-6f, g[i].x - g[i - 1].x));
            return g[g.Length - 1].y;
        }
    }
}
