using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// Physics::Info::Performance (specs/gameplay_core.md §11): the three 0..1 ratings {TopSpeed, Handling, Acceleration}
    /// behind Game_FindPerformanceCandidates (0x61D8E0), the FE rating bars and AIActionRace's MinimumAIPerformance.
    /// <list type="bullet">
    /// <item>Physics::Info::Init (0x683EB0): every pvehicle with performance ratings (HasPerformanceRatings 0x66C9A0:
    /// HandlingRating[0] &lt; HandlingRating[1] and [1] &gt; 0) gets a raw estimate stock and with every package at max
    /// (Upgrades::SetMaximum 0x67F950) from PerfStats::Fetch (0x678CE0); each raw value is then normalised over all the
    /// estimates (sub_6794E0 min / max): TopSpeed = ramp(top speed), Handling = ramp(handling), Acceleration =
    /// 1 − ramp(0-100 mph time).</item>
    /// <item>PerfStats::Fetch: a straight-line run at full throttle from the peak-torque rpm in 1st, dt 0.125 s until
    /// 100 mph (44.703 m/s, the time is the raw acceleration), then 1 s steps until 120 s: rpm = (v/r·ratio·(1 −
    /// idle/red) + idle) (rad/s), clamped to idle..redline; force = torque(rpm)·1.3558·(1 + boost)·ratio / r (NOS
    /// TORQUE_BOOST while the run is shorter than NOS_CAPACITY seconds and v &gt; 5); drag = DRAG_COEFFICIENT·v²; shifts at
    /// the shift points; top speed = the limiter, or v once top gear can accelerate no more. Raw handling =
    /// lerp(HandlingRating[0], [1], Σ Handling weight·upgrade % / Σ weight).</item>
    /// <item>EstimatePerformance (0x67C570): a car's installed rating = lerp(stock, max, Σ weight·GetPercent / Σ weight)
    /// per axis with PerformanceWeights[7] (0x8F8928), Junkman parts raising max, stock and the fraction.</item>
    /// </list>
    /// The raw run is Dynamics.PhysicsInfo.Fetch (0x678CE0, ported from the exe) on the port's CarSpec at those levels.
    /// </summary>
    public static class PerformanceRatings
    {
        public struct Perf
        {
            public float TopSpeed, Handling, Acceleration;
            public Perf(float t, float h, float a) { TopSpeed = t; Handling = h; Acceleration = a; }
            /// <summary>Performance::Maximize.</summary>
            public Perf Max(Perf o) => new Perf(Mathf.Max(TopSpeed, o.TopSpeed), Mathf.Max(Handling, o.Handling), Mathf.Max(Acceleration, o.Acceleration));
            public bool AllAtMost(Perf o) => TopSpeed <= o.TopSpeed && Handling <= o.Handling && Acceleration <= o.Acceleration;
            public override string ToString() => $"top {TopSpeed:0.00} handling {Handling:0.00} accel {Acceleration:0.00}";
        }

        /// <summary>PerformanceWeights[7] (0x8F8928) by Physics::Upgrades::Type (tires, brakes, chassis, transmission,
        /// engine, induction, nos): {TopSpeed, Handling, Acceleration}.</summary>
        public static readonly Perf[] Weights =
        {
            new Perf(0.25f, 1.5f, 0.25f), new Perf(0f, 0.5f, 0f), new Perf(0.25f, 1f, 0.2f), new Perf(1f, 0f, 0.75f),
            new Perf(0.5f, 0f, 1f), new Perf(0.25f, 0f, 1.25f), new Perf(0.25f, 0f, 1.5f),
        };

        sealed class Entry
        {
            public string Car;
            public Vector3 RawStock, RawMax;   // (0-100 time, top speed, handling)
            public Perf Stock, Max;
        }

        static List<Entry> _list;
        static Dictionary<string, Entry> _byCar;

        static AttribDatabase Db => GameSession.Career?.Db ?? GameData.Attributes;

        /// <summary>Physics::Info::HasPerformanceRatings (0x66C9A0).</summary>
        public static bool HasRatings(AttribCollection pv)
        {
            if (pv == null || !pv.Has("HandlingRating")) return false;
            float a = pv.Float("HandlingRating", 0), b = pv.Float("HandlingRating", 1);
            return a < b && b > 0f;
        }

        /// <summary>GetStockPerformance (0x679660); false when the car has no ratings.</summary>
        public static bool Stock(string car, out Perf p)
        {
            Ensure();
            p = default;
            if (car == null || !_byCar.TryGetValue(car.ToLowerInvariant(), out var e)) return false;
            p = e.Stock;
            return true;
        }

        /// <summary>GetMaximumPerformance (0x6796C0).</summary>
        public static bool Maximum(string car, out Perf p)
        {
            Ensure();
            p = default;
            if (car == null || !_byCar.TryGetValue(car.ToLowerInvariant(), out var e)) return false;
            p = e.Max;
            return true;
        }

        /// <summary>
        /// EstimatePerformance (0x67C570) for an owned car: per axis the weighted upgrade fraction, with the Junkman parts'
        /// share j raising max' = max + (1 − max)·j, stock' = stock + (max' − stock)·j·0.33 and the fraction ·(1 + 0.33·j)
        /// (≤ 1); result = stock' + (max' − stock')·fraction.
        /// </summary>
        public static bool Estimate(OwnedCar car, out Perf p)
        {
            p = default;
            if (car == null || !Stock(car.Car, out var st) || !Maximum(car.Car, out var mx)) return false;
            float sumT = 0, sumH = 0, sumA = 0, junkT = 0, junkH = 0, junkA = 0, pt = 0, ph = 0, pa = 0;
            for (int i = 0; i < ShopRules.PerfCount; i++)
            {
                float pct = Percent(car, i);
                var w = Weights[i];
                sumT += w.TopSpeed; sumH += w.Handling; sumA += w.Acceleration;
                if ((car.Junkman & (1 << i)) != 0) { junkT += w.TopSpeed; junkH += w.Handling; junkA += w.Acceleration; }
                pt += pct * w.TopSpeed; ph += pct * w.Handling; pa += pct * w.Acceleration;
            }
            if (sumH > 1e-6f) ph /= sumH;
            if (sumA > 1e-6f) pa /= sumA;
            if (sumT > 1e-6f) pt /= sumT;
            Axis(ref st.Handling, ref mx.Handling, ref ph, sumH, junkH);
            Axis(ref st.Acceleration, ref mx.Acceleration, ref pa, sumA, junkA);
            Axis(ref st.TopSpeed, ref mx.TopSpeed, ref pt, sumT, junkT);
            p = new Perf((mx.TopSpeed - st.TopSpeed) * pt + st.TopSpeed, (mx.Handling - st.Handling) * ph + st.Handling,
                (mx.Acceleration - st.Acceleration) * pa + st.Acceleration);
            return true;
        }

        static void Axis(ref float stock, ref float max, ref float frac, float sum, float junk)
        {
            if (sum <= 1e-6f) return;
            float j = junk / sum;
            max = (1f - max) * j + max;
            stock = (max - stock) * j * 0.33f + stock;
            frac = Mathf.Min(1f, (j * 0.33f + 1f) * frac);
        }

        /// <summary>Physics::Upgrades::GetPercent (0x677F80): 0 with no packages, 1 at the top level, else level / max.</summary>
        public static float Percent(OwnedCar car, int type)
        {
            int max = CarParts.NumPackages(car.Car, type);
            if (max <= 0) return 0f;
            int lvl = CarParts.PerfLevel(car, type);
            return lvl >= max ? 1f : Mathf.Clamp01((float)lvl / max);
        }

        /// <summary>
        /// Game_FindPerformanceCandidates (0x61D8E0 → sub_684610): perf' = perf + 0.2 per axis; every rated car whose stock
        /// rating is ≤ perf' on all three axes (and maximum ≥ 0) and whose pvehicle has RandomOpponent; shuffled
        /// (bRandom); up to `max`.
        /// </summary>
        public static List<string> Candidates(Perf perf, int max)
        {
            Ensure();
            var top = new Perf(perf.TopSpeed + 0.2f, perf.Handling + 0.2f, perf.Acceleration + 0.2f);
            var list = new List<string>();
            var db = Db;
            foreach (var e in _list)
            {
                if (!e.Stock.AllAtMost(top)) continue;
                var pv = db?.Get("pvehicle", e.Car);
                if (pv == null || !pv.Has("RandomOpponent") || !pv.Bool("RandomOpponent")) continue;
                list.Add(e.Car);
            }
            for (int i = 0; i < list.Count; i++)
            {
                int j = Random.Range(0, list.Count);
                (list[i], list[j]) = (list[j], list[i]);
            }
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        /// <summary>The table is built (Physics::Info::Init) and has entries.</summary>
        public static int Count { get { Ensure(); return _list.Count; } }

        static void Ensure()
        {
            if (_list != null) return;
            _list = new List<Entry>();
            _byCar = new Dictionary<string, Entry>();
            var db = Db;
            if (db == null) return;
            try
            {
                foreach (var pv in db.OfClass("pvehicle"))
                {
                    if (pv.Name == null || !HasRatings(pv)) continue;
                    var stockCar = new OwnedCar { Car = pv.Name };
                    var maxCar = new OwnedCar { Car = pv.Name };
                    for (int t = 0; t < ShopRules.PerfCount; t++) CarParts.SetPerfLevel(maxCar, t, ShopRules.NumPackages(db, pv.Name, t));
                    if (!Fetch(db, pv, stockCar, out var rs) || !Fetch(db, pv, maxCar, out var rm)) continue;
                    var e = new Entry { Car = pv.Name.ToLowerInvariant(), RawStock = rs, RawMax = rm };
                    _list.Add(e);
                    _byCar[e.Car] = e;
                }
                if (_list.Count == 0) return;
                // sub_6794E0(-1 / +1): the minimum and maximum of each raw value over every estimate (stock and max)
                var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var e in _list) { lo = Vector3.Min(lo, Vector3.Min(e.RawStock, e.RawMax)); hi = Vector3.Max(hi, Vector3.Max(e.RawStock, e.RawMax)); }
                foreach (var e in _list) { e.Stock = Normalise(e.RawStock, lo, hi); e.Max = Normalise(e.RawMax, lo, hi); }
            }
            catch (System.Exception ex) { Debug.LogWarning("NFSMW performance ratings: " + ex.Message); }
        }

        static Perf Normalise(Vector3 raw, Vector3 lo, Vector3 hi) =>
            new Perf(RaceRules.Ramp(raw.y, lo.y, hi.y), RaceRules.Ramp(raw.z, lo.z, hi.z), 1f - RaceRules.Ramp(raw.x, lo.x, hi.x));

        /// <summary>
        /// PerfStats::Fetch (0x678CE0) on the port's CarSpec at the given levels: (0-100 mph time, top speed, raw
        /// handling). The run itself is Dynamics.PhysicsInfo.Fetch (the exe's function with its MaxTorque launch rpm,
        /// 0x673400 shift points and 0x66BCC0 full-spool boost); raw handling uses the HandlingRating pair and the
        /// Handling column of PerformanceWeights with Physics::Upgrades::GetPercent.
        /// </summary>
        static bool Fetch(AttribDatabase db, AttribCollection pv, OwnedCar levels, out Vector3 raw)
        {
            raw = default;
            var s = CarSpec.FromAttributes(db, pv.Name, levels);
            var pct = new float[Weights.Length];
            var wh = new float[Weights.Length];
            for (int i = 0; i < Weights.Length; i++) { wh[i] = Weights[i].Handling; pct[i] = i < ShopRules.PerfCount ? Percent(levels, i) : 0f; }
            if (!Dynamics.PhysicsInfo.Fetch(s, pv.Float("HandlingRating", 0), pv.Float("HandlingRating", 1), pct, wh, out var perf)) return false;
            raw = new Vector3(perf.ZeroToHundred, perf.TopSpeed, perf.Handling);
            return true;
        }
    }
}
