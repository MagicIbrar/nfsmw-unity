using UnityEngine;

namespace NfsMw.Dynamics
{
    /// <summary>
    /// Physics::Info (Physics/PhysicsInfo.cpp; speed.exe 0x66BAE0-0x68A7E0) on a <see cref="CarSpec"/> (the pvehicle's
    /// parts at their upgrade levels). Every function is the exe's; offsets are the engine/transmission/tires layouts
    /// (engine +0x54 MAX_RPM, +0x58 RED_LINE, +0x5C IDLE; transmission +0x80 FINAL_GEAR; chassis +0x70
    /// DRAG_COEFFICIENT). Tunings are not passed by any caller ported here (null in the exe calls).
    /// </summary>
    public static class PhysicsInfo
    {
        public const float RpmToRad = 0.10471976f, RadToRpm = 9.549296f, LbFtToNm = 1.3558f, Mph = 0.44703f;

        /// <summary>UMath::Ramp: clamp01((x − a)/(b − a)), 0 when b − a ≤ 1e-6.</summary>
        public static float Ramp(float x, float a, float b) => b - a <= 1e-6f ? 0f : Mathf.Clamp01((x - a) / (b - a));

        /// <summary>Torque (0x672FE0): lb-ft, TORQUE spans IDLE..MAX_RPM, rpm clamped to IDLE..RED_LINE.</summary>
        public static float Torque(CarSpec s, float rpm) => s.TorqueAt(rpm);

        /// <summary>
        /// MaxTorque (0x66C3A0): the largest TORQUE point (first one on ties) and its rpm IDLE + i·(MAX_RPM − IDLE)/(n − 1),
        /// clamped to IDLE..RED_LINE.
        /// </summary>
        public static float MaxTorque(CarSpec s, out float atRpm)
        {
            var t = s.Torque;
            int n = t != null ? t.Length : 0;
            if (n == 0) { atRpm = 0; return 0; }
            float best = 0;
            int bi = 0;
            for (int i = 0; i < n; i++) if (best < t[i]) { best = t[i]; bi = i; }
            atRpm = n > 1 ? (s.MaxRpm - s.IdleRpm) * ((float)bi / (n - 1)) + s.IdleRpm : s.IdleRpm;
            atRpm = Mathf.Max(Mathf.Min(atRpm, s.RedLine), s.IdleRpm);
            return best;
        }

        /// <summary>
        /// MaxInductedTorque (0x66C180): per TORQUE point, TORQUE[i]·(1 + InductionBoost(rpm_i, spool 1)); the peak (lb-ft)
        /// and its rpm clamped to IDLE..RED_LINE. EngineRacer keeps it ×1.3558 as its peak torque (0x691ED0 → 0x66C2D0).
        /// </summary>
        public static float MaxInductedTorque(CarSpec s, out float atRpm)
        {
            var t = s.Torque;
            int n = t != null ? t.Length : 0;
            atRpm = s.IdleRpm;
            if (n <= 1) return 0;
            float best = 0, rpm = s.IdleRpm, step = (s.MaxRpm - s.IdleRpm) / (n - 1);
            for (int i = 0; i < n; i++, rpm += step)
            {
                float v = (InductionBoost(s, rpm, 1f, out _) + 1f) * t[i];
                if (best < v) { best = v; atRpm = rpm; }
            }
            atRpm = Mathf.Max(Mathf.Min(atRpm, s.RedLine), s.IdleRpm);
            return best;
        }

        /// <summary>InductionType: none, turbo (SPOOL &gt; 0) or supercharger.</summary>
        public static int InductionType(CarSpec s) => s.HighBoost > 0 || s.LowBoost > 0 ? (s.Spool > 0 ? 1 : 2) : 0;

        /// <summary>InductionRPM (0x66BC60): SPOOL·(RED_LINE − IDLE) + IDLE (tunings already folded into the spec).</summary>
        public static float InductionRpm(CarSpec s) => s.Spool * (s.RedLine - s.IdleRpm) + s.IdleRpm;

        /// <summary>
        /// InductionBoost (0x66BCC0)(engine, induction, rpm, spool, tunings, psi): spool clamped 0..1; at or above the
        /// spool rpm lerp(LOW_BOOST, HIGH_BOOST, ramp(rpm, spoolRpm, RED_LINE)), below it ramp(rpm, IDLE, spoolRpm)·VACUUM
        /// when VACUUM &lt; 0; × spool. psi as the gauge reads it.
        /// </summary>
        public static float InductionBoost(CarSpec s, float rpm, float spool, out float psi)
        {
            psi = 0;
            spool = Mathf.Clamp01(spool);
            float hi = s.HighBoost, lo = s.LowBoost, boost = 0;
            if (hi > 0 || lo > 0)
            {
                float spoolRpm = InductionRpm(s);
                if (rpm < spoolRpm)
                {
                    if (s.Vacuum < 0)
                    {
                        float r = Ramp(rpm, s.IdleRpm, spoolRpm);
                        boost = r * s.Vacuum;
                        psi = -(Ramp(-boost, 0, Mathf.Max(lo, hi)) * s.Psi * r);
                    }
                }
                else
                {
                    float r = Ramp(rpm, spoolRpm, s.RedLine);
                    boost = r * hi + (1f - r) * lo;
                    psi = Ramp(boost, 0, Mathf.Max(lo, hi)) * s.Psi * spool;
                }
            }
            return spool * boost;
        }

        /// <summary>
        /// MaxInductedPower (0x66BFA0): the largest TORQUE[i]·(1 + InductionBoost(rpm_i, spool 1))·rpm_i / 5252 (hp,
        /// 0.00019040366) over the torque points IDLE + i·(MAX_RPM − IDLE)/(n − 1); 0 with fewer than 2 points.
        /// </summary>
        public static float MaxInductedPower(CarSpec s)
        {
            var t = s.Torque;
            int n = t != null ? t.Length : 0;
            if (n <= 1) return 0f;
            float best = 0f, rpm = s.IdleRpm, step = (s.MaxRpm - s.IdleRpm) / (n - 1);
            for (int i = 0; i < n; i++, rpm += step)
            {
                float hp = (InductionBoost(s, rpm, 1f, out _) + 1f) * t[i] * rpm * 0.00019040366f;
                if (best < hp) best = hp;
            }
            return best;
        }

        /// <summary>Redline (0x66C560): the engine's RED_LINE (+0x58).</summary>
        public static float Redline(CarSpec s) => s.RedLine;

        /// <summary>
        /// Physics::Upgrades::MatchPeformance (0x67C310), which PVehicle::Construct (0x689820) runs when the VehicleParams
        /// carry a Performance to match: t_top/t_handling/t_accel = Ramp(target, stock, maximum) of the car's ratings
        /// (GetStockPerformance 0x679660 / GetMaximumPerformance 0x6796C0, Ramp 0x401AE0 = 0 for a degenerate range); per
        /// Upgrades::Type: tires and chassis (t_handling + t_top)/2, brakes t_handling, transmission and engine
        /// (t_top + t_accel)/2, induction and nos t_accel; clamped ≤ 1 (skipped below 0 and for types without packages);
        /// level = Ceil(N·t) (UMath::Ceil 0x5C8150) and the part is the interpolation at t itself (0x678730). False when the
        /// car has no ratings. GRacerInfo::CreateVehicle (0x5E8E60) creates every race opponent with Performance {1, 1, 1}
        /// and flags 9 (0x5E8F9E-0x5E8FD1), so an AI racer's parts are all at the top level (t = 1, as the ratings are
        /// normalised to ≤ 1). The returned car's levels are Ceil(N·t); CarSpec lerps by L/N, the same for t = 0 or 1.
        /// [hook for RaceManager.SpawnOpponent: the opponent's OwnedCar]
        /// </summary>
        public static bool MatchPerformance(string car, float topSpeed, float handling, float acceleration, out Game.OwnedCar match, float[] fractions = null)
        {
            match = new Game.OwnedCar { Car = car };
            if (!Game.Gameplay.PerformanceRatings.Stock(car, out var lo) || !Game.Gameplay.PerformanceRatings.Maximum(car, out var hi)) return false;
            float top = Ramp(topSpeed, lo.TopSpeed, hi.TopSpeed), hand = Ramp(handling, lo.Handling, hi.Handling), acc = Ramp(acceleration, lo.Acceleration, hi.Acceleration);
            for (int type = 0; type < 7; type++)
            {
                float t;
                switch (type)
                {
                    case 0: case 2: t = (hand + top) * 0.5f; break;
                    case 1: t = hand; break;
                    case 3: case 4: t = (top + acc) * 0.5f; break;
                    default: t = acc; break;   // 5 induction, 6 nos
                }
                t = Mathf.Min(t, 1f);
                if (fractions != null && type < fractions.Length) fractions[type] = t;
                int n = Game.CarParts.NumPackages(car, type);
                if (t <= 0f || n <= 0) continue;
                Game.CarParts.SetPerfLevel(match, type, Mathf.CeilToInt(n * t));
            }
            return true;
        }

        /// <summary>HasNos (0x66BB80): the nos part's TORQUE_BOOST (+4) and NOS_CAPACITY (+0x10) both &gt; 0.</summary>
        public static bool HasNos(CarSpec s) => s.NosTorqueBoost > 0f && s.NosCapacity > 0f;

        /// <summary>HasRunflatTires (0x66BC00): always false on PC (`xor al, al; ret`).</summary>
        public static bool HasRunflatTires(CarSpec s) => false;

        /// <summary>Forward gear count (0x66C8A0): Num_GEAR_RATIO − 2 (R and N), 0 when there are none.</summary>
        public static int NumGears(CarSpec s) => s.GearRatios != null && s.GearRatios.Length > 2 ? s.GearRatios.Length - 2 : 0;

        /// <summary>WheelDiameter (0x66BF20): RIM·0.0254 + SECTION·0.002·ASPECT·0.01 (CarSpec keeps the radius).</summary>
        public static float WheelDiameter(CarSpec s, bool front) => 2f * (front ? s.TireRadiusFront : s.TireRadiusRear);

        /// <summary>EngineInertia (0x66BB00): (FLYWHEEL_MASS·0.025 + 0.25)·(loaded ? 1 : 0.35).</summary>
        public static float EngineInertia(CarSpec s, bool loaded) => (loaded ? 1f : 0.35f) * (s.FlywheelMass * 0.025f + 0.25f);

        /// <summary>AerodynamicDownforce (0x66BAE0): speed·2·AERO_COEFFICIENT·1000.</summary>
        public static float AerodynamicDownforce(CarSpec s, float speed) => speed * 2f * s.AeroCoefficient * 1000f;

        /// <summary>
        /// ShiftPoints (0x673400) into arrays indexed by gear (2 = 1st). For each gear j from 1st to top−1: the first rpm
        /// from (RED_LINE + IDLE)/2 in 50-rpm steps (while &lt; RED_LINE) where the next gear makes more wheel torque,
        /// T(r)(1 + b) &lt; T(r·ρ)(1 + b)·ρ, else RED_LINE − 100; down[j + 1] = ρ·up[j]; up[top] = RED_LINE.
        /// The exe passes (rpm 1.0, spool r) to InductionBoost here (swapped arguments), so b is 0: boost never moves
        /// the shift points.
        /// </summary>
        public static bool ShiftPoints(CarSpec s, float[] up, float[] down)
        {
            int numpts = up.Length;
            for (int i = 0; i < numpts; i++) { up[i] = 0; down[i] = 0; }
            var g = s.GearRatios;
            int count = g != null ? g.Length : 0;
            if (numpts < count) return false;
            float red = s.RedLine;
            int top = count - 1;
            for (int j = 2; j < top; j++)
            {
                float g1 = g[j], g2 = g[j + 1];
                bool found = false;
                for (float r = (red + s.IdleRpm) * 0.5f; r < red; r += 50f)
                {
                    float cur = Torque(s, r) * (InductionBoost(s, 1f, r, out _) + 1f);
                    float next = 0;
                    if (Mathf.Abs(g1) > 1e-5f)
                    {
                        float r2 = 1f / g1 * g2 * r;
                        next = Torque(s, r2) * (InductionBoost(s, 1f, r2, out _) + 1f) * (1f / g1) * g2;
                    }
                    if (cur < next) { up[j] = r; found = true; break; }
                }
                if (!found) up[j] = red - 100f;
                down[j + 1] = Mathf.Abs(g1) <= 1e-5f ? 0f : g2 / g1 * up[j];
            }
            if (top >= 0) up[top] = red;
            return true;
        }

        /// <summary>
        /// Speedometer: wheel speed at an rpm in a gear (idle-offset map), capped by SPEED_LIMITER.
        /// </summary>
        public static float Speedometer(CarSpec s, float rpm, int gearIndex)
        {
            float speed = 0;
            float ratio = Mathf.Abs(s.GearRatios[Mathf.Clamp(gearIndex, 0, s.GearRatios.Length - 1)] * s.FinalGear);
            float range = s.RedLine - s.IdleRpm;
            if (ratio > 0 && range > 0)
            {
                float rAvg = (s.TireRadiusFront + s.TireRadiusRear) * 0.5f;
                float clutchRpm = (rpm - s.IdleRpm) / ratio / range * s.RedLine;
                speed = clutchRpm * RpmToRad * rAvg;
            }
            float lim = s.SpeedLimiter * Mph;
            return lim > 0 ? Mathf.Min(speed, lim) : speed;
        }

        /// <summary>One PerfStats::Fetch result: 0-100 mph time (s), top speed (m/s), raw handling.</summary>
        public struct PerfStats
        {
            public float ZeroToHundred, TopSpeed, Handling;
        }

        /// <summary>
        /// PerfStats::Fetch (0x678CE0): a straight run at full throttle and full spool, no tunings. Wheel radius = the
        /// rear WheelDiameter/2; launch rpm = MaxTorque's rpm in 1st; per step the rpm is the idle-offset map
        /// ((v/r)·ratio·(ωred − ωidle)/ωred + ωidle) clamped to IDLE..RED_LINE, force = Torque·1.3558·(1 + boost)·ratio/r
        /// (× (TORQUE_BOOST + 1) while t &lt; NOS_CAPACITY and v &gt; 5), drag = |DRAG_COEFFICIENT·v²|; v += (F − D)/m·dt with
        /// dt 0.125 s (1 s when a table is filled, and after 100 mph); shift at the shift points; top speed = the limiter,
        /// or v when top gear stops accelerating or hits RED_LINE; to 120 s. `table` (optional) receives (v, accel) per
        /// step (ComputeAccelerationTable's caller). Handling = lerp(HandlingRating[0], [1], Σ w_h·GetPercent / Σ w_h)
        /// (PerformanceWeights 0x8F8928 Handling column, the caller's upgrade fractions).
        /// </summary>
        public static bool Fetch(CarSpec s, float handlingStock, float handlingMax, float[] upgradePercent, float[] handlingWeights,
                                 out PerfStats perf, System.Collections.Generic.List<Vector2> table = null)
        {
            perf = default;
            float r = WheelDiameter(s, false) * 0.5f;
            float finalGear = s.FinalGear, red = s.RedLine, idle = s.IdleRpm;
            float idleRad = idle * RpmToRad;
            float limiter = s.SpeedLimiter * Mph;
            var up = new float[12];
            var down = new float[12];
            if (!ShiftPoints(s, up, down) || r <= 0 || finalGear <= 0 || s.Mass <= 0) return false;
            MaxTorque(s, out float launch);
            float time = 0, v = 0, dt = table != null ? 1f : 0.125f;
            int g = 0, last = NumGears(s) - 1;
            if (last < 0) return false;
            float fc = (red * RpmToRad - idleRad) / (red * RpmToRad);
            float invMass = 1f / s.Mass;
            float rpm = idle;
            do
            {
                int gi = g + 2;
                float ratio = finalGear * (gi < s.GearRatios.Length ? s.GearRatios[gi] : 0f);
                rpm = ((1f / r) * v * ratio * fc + idleRad) * RadToRpm;
                if (red <= rpm) rpm = red;
                if (rpm < idle) rpm = idle;
                if (g == 0 && rpm <= launch) rpm = launch;
                float torque = Torque(s, rpm) * LbFtToNm * (InductionBoost(s, rpm, 1f, out _) + 1f) * ratio;
                if (time < s.NosCapacity && v > 5f) torque *= s.NosTorqueBoost + 1f;
                float accel = invMass * torque * (1f / r);
                table?.Add(new Vector2(v, accel));
                float drag = Mathf.Abs(s.DragCoefficient * v * v) * invMass;
                v = accel * dt + v - drag * dt;
                if (v >= 44.703f && perf.ZeroToHundred <= 0f) { perf.ZeroToHundred = time; dt = 1f; }
                if (limiter <= 0f || v < limiter)
                {
                    if (g == last && (accel < drag || red <= rpm) && perf.TopSpeed <= 0f) perf.TopSpeed = v;
                }
                else perf.TopSpeed = limiter;
                time += dt;
                if (perf.TopSpeed > 0f && perf.ZeroToHundred > 0f) break;
                if (up[gi] <= rpm && ++g >= last) g = last;
            } while (time < 120f);
            if (g == last || perf.TopSpeed <= 0f) perf.TopSpeed = v;
            float sw = 0, sp = 0;
            if (upgradePercent != null && handlingWeights != null)
                for (int i = 0; i < handlingWeights.Length && i < upgradePercent.Length; i++) { sw += handlingWeights[i]; sp += upgradePercent[i] * handlingWeights[i]; }
            float frac = sw > 1e-6f ? sp / sw : 0f;
            perf.Handling = (handlingMax - handlingStock) * frac + handlingStock;
            return perf.TopSpeed > 0f && perf.ZeroToHundred > 0f;
        }
    }
}
