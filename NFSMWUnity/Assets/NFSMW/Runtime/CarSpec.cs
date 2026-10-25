using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// A car's handling data from attributes.bin (specs/physics.md §2): the pvehicle's part references (tires, brakes,
    /// chassis, transmission, engine, induction, nos) with the installed upgrade level of each part interpolated between
    /// its references (2 refs: stock → top at L/N; 3 refs: none at L = 0, else first → top), plus pvehicle.MASS.
    /// AxlePair fields keep [front, rear].
    /// </summary>
    public sealed class CarSpec
    {
        public string Name;
        // chassis
        public float TrackFront = 1.6f, TrackRear = 1.6f, WheelBase = 2.6f, FrontAxle = 1.3f;
        public float RideHeightFront = 7f, RideHeightRear = 7f;            // inches
        public float TravelFront = 7f, TravelRear = 7f;                    // inches
        public float SpringFront = 400f, SpringRear = 400f;                // lb/in
        public float SpringProgFront, SpringProgRear;
        public float ShockFront = 40f, ShockRear = 40f, ShockExtFront = 40f, ShockExtRear = 40f;   // lb/in per in/s
        public float ValvingFront = 20f, ValvingRear = 20f, DigressionFront = 1f, DigressionRear = 1f;
        public float SwayFront, SwayRear, RollCenter = 10f, ShockBlowout = 6f, AeroCg = 50f;
        public float FrontWeightBias = 50f, DragCoefficient = 0.3f, AeroCoefficient = 0.2f;
        // tires
        public float TireRadiusFront = 0.33f, TireRadiusRear = 0.33f;
        public float StaticGripFront = 1.8f, StaticGripRear = 1.8f, DynamicGripFront = 1.6f, DynamicGripRear = 1.6f;
        public float GripScaleFront = 1f, GripScaleRear = 1f, Steering = 1f, YawSpeed = 1f;
        public float[] YawControl = { 0, 0, 0, 0 };
        // engine
        public float[] Torque = { 150, 200, 250, 280, 290, 280, 260, 240, 220 };
        public float[] EngineBraking = { 0.7f, 0.8f, 0.9f };
        public float MaxRpm = 8000, RedLine = 7500, IdleRpm = 800, FlywheelMass = 10f;
        public float SpeedLimiter, SpeedLimiterFade;
        // induction (turbo when Spool > 0, supercharger otherwise; none when both boosts are 0)
        public float LowBoost, HighBoost, Spool, SpoolUp = 1, SpoolDown = 1, Vacuum, Psi;
        // transmission
        public float[] GearRatios = { 3.5f, 0, 3.4f, 2.1f, 1.5f, 1.2f, 1.0f, 0.85f };
        public float[] GearEfficiency = { 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        public float[] Differential = { 0, 0, 0 };
        public float FinalGear = 3.6f, TorqueSplit = 0f, ShiftSpeed = 0.25f, TorqueConverter, ClutchSlip;
        /// <summary>transmission OPTIMAL_SHIFT: the drag perfect-shift band is this x the next gear's ratio (rpm).</summary>
        public float OptimalShift = 500f;
        // brakes (lb-ft)
        public float BrakeFront = 400, BrakeRear = 400, EBrake = 800, BrakeLockFront = 1, BrakeLockRear = 1;
        // nitrous
        public float NosCapacity, NosTorqueBoost, NosFlowRate = 1f;
        public float NosRechargeMin = 50f, NosRechargeMax = 30f, NosRechargeMinSpeed = 50f, NosRechargeMaxSpeed = 100f, NosDisengage = 2f;
        public float Mass = 1450f;
        public Vector3 TensorScale = new Vector3(1, 2, 1);
        // rigidbodyspecs (pvehicle ref, "cars" by default): friction {static, dynamic} and elasticity per body axis
        // (sim x right, y up, z forward, the same as Unity car space)
        public float WallFrictionStatic = 0.2f, WallFrictionDynamic = 0.1f, ObjFrictionStatic = 0.55f, ObjFrictionDynamic = 0.35f;
        public Vector3 WallElasticity = new Vector3(0, 0, 0.15f), ObjElasticity = new Vector3(0, 0, 0.05f);

        public int ForwardGears
        {
            get
            {
                int n = 0;
                for (int i = 2; i < GearRatios.Length; i++) if (GearRatios[i] > 0) n++;
                return n;
            }
        }

        public float RimRadius => (TireRadiusFront + TireRadiusRear) * 0.5f;

        /// <summary>Top speed (m/s) the gearing allows: top gear at the red line (YAW_CONTROL's Vtop), or the limiter.</summary>
        public float TopSpeed
        {
            get
            {
                float top = GearRatios[Mathf.Clamp(ForwardGears + 1, 2, GearRatios.Length - 1)];
                float v = top > 0 && FinalGear > 0 ? RedLine * 0.10471976f * RimRadius / (top * FinalGear) : 80f;
                return SpeedLimiter > 0 ? Mathf.Min(v, SpeedLimiter * 0.44703f) : v;
            }
        }

        /// <summary>Engine torque (lb-ft, 0x672FE0): TORQUE spans IDLE..MAX_RPM in equal steps, rpm clamped to IDLE..RED_LINE.</summary>
        public float TorqueAt(float rpm) => Torque != null && Torque.Length <= 1 ? 0f : Table(Torque, rpm);   // Physics::Info::Torque: 0 with fewer than 2 points (PhysicsInfo.cpp:124-138)

        /// <summary>Closed-throttle braking as a fraction of torque (ENGINE_BRAKING over IDLE..MAX_RPM).</summary>
        public float EngineBrakingAt(float rpm) => Mathf.Clamp01(Table(EngineBraking, rpm));

        float Table(float[] t, float rpm)
        {
            if (t == null || t.Length == 0) return 0;
            if (t.Length == 1) return t[0];
            float r = Mathf.Clamp(rpm, IdleRpm, RedLine);
            float x = Mathf.Clamp01((r - IdleRpm) / Mathf.Max(1f, MaxRpm - IdleRpm)) * (t.Length - 1);
            int i = Mathf.Min((int)x, t.Length - 2);
            return Mathf.Lerp(t[i], t[i + 1], x - i);
        }

        /// <summary>One part type at an upgrade level: the referenced collections and the blend between them.</summary>
        sealed class Part
        {
            public AttribCollection A, B;
            public float T;
            /// <summary>Junkman multipliers by field hash (junkman/default.&lt;type&gt;_package, 0x678150): applied after the lerp.</summary>
            public Dictionary<uint, float> Junk;

            float J(string f) => Junk != null && Junk.TryGetValue(AttribDatabase.Hash(f), out var k) ? k : 1f;

            public bool Has(string f) => (A != null && A.Has(f)) || (B != null && B.Has(f));

            public float F(string f, int i, float fb)
            {
                if (A == null) return fb;
                float a = A.Float(f, i, fb);
                return (B == null || T <= 0 ? a : Mathf.Lerp(a, B.Float(f, i, a), T)) * J(f);
            }

            public (float, float) P(string f, (float, float) fb)
            {
                if (A == null) return fb;
                var a = A.Pair(f, fb);
                float j = J(f);
                if (B == null || T <= 0) return (a.Item1 * j, a.Item2 * j);
                var b = B.Pair(f, a);
                return (Mathf.Lerp(a.Item1, b.Item1, T) * j, Mathf.Lerp(a.Item2, b.Item2, T) * j);
            }

            public float[] Fs(string f, float[] fb)
            {
                if (A == null || !A.Has(f)) return fb;
                var a = A.Floats(f);
                float j = J(f);
                var b = B == null || T <= 0 || !B.Has(f) ? null : B.Floats(f);
                var r = new float[a.Length];
                for (int i = 0; i < a.Length; i++) r[i] = (b == null ? a[i] : Mathf.Lerp(a[i], i < b.Length ? b[i] : a[i], T)) * j;
                return r;
            }
        }

        /// <summary>
        /// The part of type `part` at the installed level L (0..pvehicle `*_upgrades` N, the shop's package level):
        /// refs [stock] / [stock, top] / [none, first, top] lerped by L/N (0x678730, 0x677CE0); a Junkman part
        /// multiplies the junkman/default.&lt;part&gt;_package fields on top (0x678150, physics.md §2).
        /// </summary>
        static Part PartFor(AttribDatabase db, AttribCollection pv, string part, string car, int stage, bool junkman = false)
        {
            var p = PartAt(db, pv, part, car, stage);
            if (junkman)
            {
                p.Junk = new Dictionary<uint, float>();
                int type = Array.IndexOf(ShopRules.PerfTypes, part);
                if (type >= 0)
                    foreach (var m in ShopRules.JunkmanMods(db, type)) p.Junk[m.field] = (p.Junk.TryGetValue(m.field, out var k) ? k : 1f) * m.scale;
            }
            return p;
        }

        static Part PartAt(AttribDatabase db, AttribCollection pv, string part, string car, int stage)
        {
            var p = new Part();
            var refs = pv != null ? pv.Refs(part) : Array.Empty<(uint, uint)>();
            var cols = new List<AttribCollection>();
            foreach (var r in refs) if (db.Collections.TryGetValue(r, out var c)) cols.Add(c);
            if (cols.Count == 0) { p.A = db.Get(part, car); return p; }
            int n = pv.Has(part + "_upgrades") ? pv.Int(part + "_upgrades") : 0;
            int level = n > 0 ? Mathf.Clamp(stage, 0, n) : 0;
            float t = n > 0 ? (float)level / n : 0;
            if (cols.Count >= 3)
            {
                if (level == 0) p.A = cols[0];
                else { p.A = cols[1]; p.B = cols[2]; p.T = t; }
            }
            else { p.A = cols[0]; p.B = cols.Count > 1 ? cols[1] : null; p.T = t; }
            return p;
        }

        /// <summary>The whole rigidbodyspecs collection (RigidBody behaviour: collision response, gravity, integration
        /// bounds; Runtime/Physics/RigidBodySpecs.cs).</summary>
        public Dynamics.RigidBodySpecs Body = new Dynamics.RigidBodySpecs();

        /// <summary>pvehicle's rigidbodyspecs RefSpec (class, collection) to the collision friction and elasticity.</summary>
        static void ReadRigidBody(AttribDatabase db, AttribCollection pv, CarSpec s)
        {
            var rb = Dynamics.RigidBodySpecs.ForVehicle(db, pv?.Name);
            s.Body = rb;
            s.WallFrictionStatic = rb.WallFrictionStatic;
            s.WallFrictionDynamic = rb.WallFrictionDynamic;
            s.ObjFrictionStatic = rb.ObjFrictionStatic;
            s.ObjFrictionDynamic = rb.ObjFrictionDynamic;
            s.WallElasticity = rb.WallElasticity;
            s.ObjElasticity = rb.ObjElasticity;
        }

        public static CarSpec FromAttributes(AttribDatabase db, string car, Game.OwnedCar levels = null)
        {
            var s = new CarSpec { Name = car };
            var pv = db.Get("pvehicle", car);
            if (pv != null)
            {
                if (pv.Has("MASS")) s.Mass = pv.Float("MASS", 0, s.Mass);
                if (pv.Has("TENSOR_SCALE")) { var v = pv.Floats("TENSOR_SCALE"); if (v.Length >= 3 && v[1] > 0) s.TensorScale = new Vector3(v[0], v[1], v[2]); }
            }
            ReadRigidBody(db, pv, s);
            var ch = PartFor(db, pv, "chassis", car, levels?.Suspension ?? 0, Junk(levels, ShopRules.Chassis));
            (s.TrackFront, s.TrackRear) = ch.P("TRACK_WIDTH", (s.TrackFront, s.TrackRear));
            s.WheelBase = ch.F("WHEEL_BASE", 0, s.WheelBase);
            s.FrontAxle = ch.F("FRONT_AXLE", 0, s.FrontAxle);
            (s.RideHeightFront, s.RideHeightRear) = ch.P("RIDE_HEIGHT", (s.RideHeightFront, s.RideHeightRear));
            (s.TravelFront, s.TravelRear) = ch.P("TRAVEL", (s.TravelFront, s.TravelRear));
            (s.SpringFront, s.SpringRear) = ch.P("SPRING_STIFFNESS", (s.SpringFront, s.SpringRear));
            (s.SpringProgFront, s.SpringProgRear) = ch.P("SPRING_PROGRESSION", (0f, 0f));
            (s.ShockFront, s.ShockRear) = ch.P("SHOCK_STIFFNESS", (s.ShockFront, s.ShockRear));
            (s.ShockExtFront, s.ShockExtRear) = ch.P("SHOCK_EXT_STIFFNESS", (s.ShockFront, s.ShockRear));
            (s.ValvingFront, s.ValvingRear) = ch.P("SHOCK_VALVING", (s.ValvingFront, s.ValvingRear));
            (s.DigressionFront, s.DigressionRear) = ch.P("SHOCK_DIGRESSION", (s.DigressionFront, s.DigressionRear));
            (s.SwayFront, s.SwayRear) = ch.P("SWAYBAR_STIFFNESS", (0f, 0f));
            s.RollCenter = ch.F("ROLL_CENTER", 0, s.RollCenter);
            s.ShockBlowout = ch.F("SHOCK_BLOWOUT", 0, s.ShockBlowout);
            s.AeroCg = ch.F("AERO_CG", 0, s.AeroCg);
            s.FrontWeightBias = ch.F("FRONT_WEIGHT_BIAS", 0, s.FrontWeightBias);
            s.DragCoefficient = ch.F("DRAG_COEFFICIENT", 0, s.DragCoefficient);
            s.AeroCoefficient = ch.F("AERO_COEFFICIENT", 0, s.AeroCoefficient);

            var ti = PartFor(db, pv, "tires", car, levels?.Tires ?? 0, Junk(levels, ShopRules.Tires));
            var (wf, wr) = ti.P("SECTION_WIDTH", (255f, 255f));
            var (af, ar) = ti.P("ASPECT_RATIO", (35f, 35f));
            var (rf, rr) = ti.P("RIM_SIZE", (18f, 18f));
            s.TireRadiusFront = rf * 0.0254f * 0.5f + wf * 0.001f * af * 0.01f;
            s.TireRadiusRear = rr * 0.0254f * 0.5f + wr * 0.001f * ar * 0.01f;
            (s.StaticGripFront, s.StaticGripRear) = ti.P("STATIC_GRIP", (s.StaticGripFront, s.StaticGripRear));
            (s.DynamicGripFront, s.DynamicGripRear) = ti.P("DYNAMIC_GRIP", (s.DynamicGripFront, s.DynamicGripRear));
            (s.GripScaleFront, s.GripScaleRear) = ti.P("GRIP_SCALE", (1f, 1f));
            s.Steering = ti.F("STEERING", 0, s.Steering);
            s.YawSpeed = ti.F("YAW_SPEED", 0, s.YawSpeed);
            var yc = ti.Fs("YAW_CONTROL", s.YawControl);
            if (yc.Length > 0) s.YawControl = yc;

            var en = PartFor(db, pv, "engine", car, levels?.Engine ?? 0, Junk(levels, ShopRules.Engine));
            var t = en.Fs("TORQUE", null);
            if (t != null && t.Length > 1) s.Torque = t;
            var eb = en.Fs("ENGINE_BRAKING", null);
            if (eb != null && eb.Length > 0) s.EngineBraking = eb;
            s.MaxRpm = en.F("MAX_RPM", 0, s.MaxRpm);
            s.RedLine = en.F("RED_LINE", 0, s.RedLine);
            s.IdleRpm = en.F("IDLE", 0, s.IdleRpm);
            s.FlywheelMass = en.F("FLYWHEEL_MASS", 0, s.FlywheelMass);
            s.SpeedLimiter = en.F("SPEED_LIMITER", 0, 0);
            s.SpeedLimiterFade = en.F("SPEED_LIMITER", 1, 0);

            var ind = PartFor(db, pv, "induction", car, levels?.Induction ?? 0, Junk(levels, ShopRules.Induction));
            s.LowBoost = ind.F("LOW_BOOST", 0, 0); s.HighBoost = ind.F("HIGH_BOOST", 0, 0);
            s.Spool = ind.F("SPOOL", 0, 0); s.SpoolUp = ind.F("SPOOL_TIME_UP", 0, 1); s.SpoolDown = ind.F("SPOOL_TIME_DOWN", 0, 1);
            s.Vacuum = ind.F("VACUUM", 0, 0); s.Psi = ind.F("PSI", 0, 0);

            var tr = PartFor(db, pv, "transmission", car, levels?.Transmission ?? 0, Junk(levels, ShopRules.Transmission));
            var g = tr.Fs("GEAR_RATIO", null);
            if (g != null && g.Length > 2) s.GearRatios = g;
            var ge = tr.Fs("GEAR_EFFICIENCY", null);
            if (ge != null && ge.Length > 0) s.GearEfficiency = ge;
            var df = tr.Fs("DIFFERENTIAL", null);
            if (df != null && df.Length >= 3) s.Differential = df;
            s.FinalGear = tr.F("FINAL_GEAR", 0, s.FinalGear);
            s.TorqueSplit = tr.F("TORQUE_SPLIT", 0, s.TorqueSplit);
            s.ShiftSpeed = tr.F("SHIFT_SPEED", 0, s.ShiftSpeed);
            s.OptimalShift = tr.F("OPTIMAL_SHIFT", 0, s.OptimalShift);
            s.TorqueConverter = tr.F("TORQUE_CONVERTER", 0, 0);
            s.ClutchSlip = tr.F("CLUTCH_SLIP", 0, 0);

            var br = PartFor(db, pv, "brakes", car, levels?.Brakes ?? 0, Junk(levels, ShopRules.Brakes));
            (s.BrakeFront, s.BrakeRear) = br.P("BRAKES", (s.BrakeFront, s.BrakeRear));
            (s.BrakeLockFront, s.BrakeLockRear) = br.P("BRAKE_LOCK", (1f, 1f));
            s.EBrake = br.F("EBRAKE", 0, s.EBrake);

            var nos = PartFor(db, pv, "nos", car, levels?.Nitrous ?? (pv == null ? 0 : 1), Junk(levels, ShopRules.Nos));
            if (pv == null) nos = new Part { A = db.Get("nos", "1EFBD807") };
            s.NosCapacity = nos.F("NOS_CAPACITY", 0, 0);
            s.NosTorqueBoost = nos.F("TORQUE_BOOST", 0, 0);
            s.NosFlowRate = nos.F("FLOW_RATE", 0, 1);
            s.NosRechargeMin = nos.F("RECHARGE_MIN", 0, s.NosRechargeMin);
            s.NosRechargeMax = nos.F("RECHARGE_MAX", 0, s.NosRechargeMax);
            s.NosRechargeMinSpeed = nos.F("RECHARGE_MIN_SPEED", 0, s.NosRechargeMinSpeed);
            s.NosRechargeMaxSpeed = nos.F("RECHARGE_MAX_SPEED", 0, s.NosRechargeMaxSpeed);
            s.NosDisengage = nos.F("NOS_DISENGAGE", 0, s.NosDisengage);
            if (levels != null) ApplyTunings(s, levels);
            return s;
        }

        static bool Junk(Game.OwnedCar c, int type) => c != null && (c.Junkman & (1 << type)) != 0;

        /// <summary>Physics::Tunings paths (PhysicsTunings.h): the 7 slider values of a tuning preset.</summary>
        public const int TuneSteering = 0, TuneHandling = 1, TuneBrakes = 2, TuneRideHeight = 3, TuneAero = 4, TuneNos = 5, TuneInduction = 6;

        /// <summary>The active tuning preset's 7 values (FECustomizationRecord.Tunings[ActiveTuning]).</summary>
        public static float[] ActiveTunings(Game.OwnedCar c)
        {
            var t = new float[7];
            if (c?.Tunings == null) return t;
            int b = Mathf.Clamp(c.ActiveTuning, 0, 2) * 7;
            for (int i = 0; i < 7 && b + i < c.Tunings.Length; i++) t[i] = c.Tunings[b + i];
            return t;
        }

        /// <summary>Tuning values of the active preset (physics.md §2 "Tunings"; t in LowerLimit..UpperLimit: TuningLimits at speed.exe 0x8A9350 are −1..+1 for all 7 paths).</summary>
        public float[] Tunings = new float[7];
        /// <summary>HANDLING tuning: lateral tire force ×(1+0.2t), longitudinal ×(1−0.2t) (for the tire model to apply).</summary>
        public float TuneLateral = 1f, TuneLongitudinal = 1f;
        /// <summary>BRAKES tuning: front brake input ×(1+0.5t), rear ×(1−0.5t) (already folded into BrakeFront/BrakeRear).</summary>
        public float TuneBrakeFront = 1f, TuneBrakeRear = 1f;

        /// <summary>
        /// Folds the active tuning preset into the spec where the effect is a plain scale (physics.md §2 "Tunings"):
        /// STEERING max angle ×(1+0.2t), BRAKES bias (front ×(1+0.5t), rear ×(1−0.5t) on the brake torques),
        /// RIDEHEIGHT +t inches, AERO drag and downforce ×(1+0.25t), NOS capacity ×(1−0.25t) and boost ×(1+0.25t),
        /// INDUCTION boost lo ×(1−0.25t), hi ×(1+0.25t) and the spool point moved by 0.25t (§3.3). HANDLING's
        /// lateral/longitudinal split is kept in TuneLateral/TuneLongitudinal for the tire model.
        /// </summary>
        static void ApplyTunings(CarSpec s, Game.OwnedCar c)
        {
            var t = ActiveTunings(c);
            s.Tunings = t;
            s.Steering *= 1f + 0.2f * t[TuneSteering];
            s.TuneLateral = 1f + 0.2f * t[TuneHandling];
            s.TuneLongitudinal = 1f - 0.2f * t[TuneHandling];
            s.TuneBrakeFront = 1f + 0.5f * t[TuneBrakes];
            s.TuneBrakeRear = 1f - 0.5f * t[TuneBrakes];
            s.BrakeFront *= s.TuneBrakeFront;
            s.BrakeRear *= s.TuneBrakeRear;
            s.RideHeightFront += t[TuneRideHeight];
            s.RideHeightRear += t[TuneRideHeight];
            s.DragCoefficient *= 1f + 0.25f * t[TuneAero];
            s.AeroCoefficient *= 1f + 0.25f * t[TuneAero];
            s.NosCapacity *= 1f - 0.25f * t[TuneNos];
            s.NosTorqueBoost *= 1f + 0.25f * t[TuneNos];
            float ti = t[TuneInduction];
            s.LowBoost *= 1f - 0.25f * ti;
            s.HighBoost *= 1f + 0.25f * ti;
            if (s.Spool > 0) s.Spool += 0.25f * ti * (ti >= 0 ? 1f - s.Spool : s.Spool);
        }
    }
}
