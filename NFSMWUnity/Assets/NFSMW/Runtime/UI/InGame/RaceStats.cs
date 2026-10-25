using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// Timer::PrintToString (Misc/Timer.cpp:239-433, English): packed time = (int)(seconds·4000 + 0.5); "HH:MM:SS",
    /// "MM:SS.hh", "SS.hh" or "0.hh", the leading field without a leading zero (IntToString2 puts a space). flags: 1 negative
    /// ('-' in the first column), 2 plus sign, 4 show unused digits, 8 keep the last digit when shifting for a sign,
    /// 16 drop the hundredths. An unset timer (0) prints the zero branch.
    /// </summary>
    public static class FETimer
    {
        public const int Negative = 1, PlusSign = 2, ShowUnused = 4, KeepLast = 8, NoMs = 16;

        public static string Print(float seconds, int flags = 0)
        {
            int packed = (int)(seconds * 4000f + 0.5f);
            if (packed < 0) { packed = -packed; flags |= Negative; }
            int whole = packed / 4000, frac = packed % 4000;
            int h = whole / 3600, m = whole % 3600 / 60, s = whole % 60, hh = (frac >> 2) / 10;
            if (packed == 0 || packed == 0x7FFFFFFF) h = m = s = hh = 0;
            char[] str;
            int lead;
            if (h != 0) { str = "00:00:00".ToCharArray(); Put(str, 0, h, false); Put(str, 3, m, true); Put(str, 6, s, true); lead = h; }
            else if (m != 0) { str = "00:00.00".ToCharArray(); Put(str, 0, m, false); Put(str, 3, s, true); Put(str, 6, hh, true); lead = m; }
            else if (s != 0) { str = "00.00".ToCharArray(); Put(str, 0, s, false); Put(str, 3, hh, true); lead = s; }
            else { str = "0.00".ToCharArray(); Put(str, 2, hh, true); lead = 0; }
            string r = new string(str);
            bool sign = (flags & (Negative | PlusSign)) != 0;
            if (sign && (lead > 9 || (h == 0 && m == 0 && s == 0)))
            {
                string t = (flags & KeepLast) != 0 ? r : r.Substring(0, r.Length - 1);
                r = r[0] + t;
            }
            if (h == 0 && (flags & NoMs) != 0) r = r.Substring(0, r.Length - 3);
            if ((flags & Negative) != 0) r = "-" + r.Substring(1);
            else if ((flags & PlusSign) != 0) r = "+" + r.Substring(1);
            return r;
        }

        static void Put(char[] dst, int at, int value, bool leadZero)
        {
            for (int n = 0; n < 2; n++)
            {
                int digit = value % 10;
                dst[at + 1 - n] = !leadZero && digit == 0 && value < 10 ? ' ' : (char)('0' + digit);
                value /= 10;
            }
        }
    }

    /// <summary>
    /// The GRacerInfo stat fields the results screens read that RaceManager does not keep (gameplay_core.md §0, §1.2):
    /// mTopSpeed, distance driven and mTotalUpdateTime (CalcAverageSpeed), mZeroToSixtyTime (at 26.8218 m/s),
    /// mQuarterMileTime (at 402.335 m driven), mFinishingSpeed (FinishRace), perfect shifts and traffic cars hit.
    /// GRacerInfo::Update (0x5E92E0) accumulates them each sim tick while the racer is still racing; here the HUD ticks
    /// them each frame [ours: frame rate instead of the 120 Hz sim]. Traffic hits come from the racer's car colliding with
    /// a traffic vehicle [ours: the exe counts them from collision messages that are not traced].
    /// </summary>
    public sealed class RacerStats
    {
        public float TopSpeed, Distance, UpdateTime, ZeroToSixty, QuarterMile, FinishingSpeed, PoundsNos;
        public int PerfectShifts, TrafficHits;
        internal int ShiftsSeen = -1;
        internal bool FinishSeen;
        internal float LastNos = -1;
        /// <summary>AreStatsReady [?]: the racer has been updated at least once.</summary>
        public bool Ready => UpdateTime > 0;
        /// <summary>CalcAverageSpeed: distance / update time (m/s).</summary>
        public float AverageSpeed => UpdateTime > 0 ? Distance / UpdateTime : 0;
    }

    public static class RaceStatsTracker
    {
        static readonly ConditionalWeakTable<RaceManager.Entrant, RacerStats> Stats = new ConditionalWeakTable<RaceManager.Entrant, RacerStats>();

        public static RacerStats For(RaceManager.Entrant e) => e == null ? new RacerStats() : Stats.GetValue(e, _ => new RacerStats());

        /// <summary>Called every frame by the single-player HUD while a race is active.</summary>
        public static void Tick(RaceManager race, float dt)
        {
            if (race == null || race.Intro || race.Countdown || dt <= 0) return;
            foreach (var e in race.Entrants)
            {
                var car = e.Car;
                if (!car) continue;
                var st = For(e);
                if (car.gameObject.GetComponent<TrafficHitCounter>() == null) car.gameObject.AddComponent<TrafficHitCounter>().Entrant = e;
                if (e.Finished || e.Out || e.Totalled || e.EngineBlown || e.Busted)
                {
                    if (e.Finished && !st.FinishSeen) { st.FinishSeen = true; st.FinishingSpeed = car.SpeedMps; }
                    continue;
                }
                float v = car.SpeedMps;
                st.TopSpeed = Mathf.Max(st.TopSpeed, v);
                st.Distance += v * dt;
                st.UpdateTime += dt;
                if (st.ZeroToSixty <= 0 && v >= 26.8218f) st.ZeroToSixty = st.UpdateTime;
                if (st.QuarterMile <= 0 && st.Distance >= 402.335f) st.QuarterMile = st.UpdateTime;
                if (st.ShiftsSeen < 0) st.ShiftsSeen = car.ShiftCount;
                if (car.ShiftCount != st.ShiftsSeen)
                {
                    st.ShiftsSeen = car.ShiftCount;
                    if (car.LastShift == PlayerCar.ShiftStatus.Perfect) st.PerfectShifts++;
                }
                // mPoundsNOSUsed [?]: the share of the bottle burned, scaled to the PC NOS capacity in pounds is not read
                if (st.LastNos >= 0 && car.Nos < st.LastNos) st.PoundsNos += (st.LastNos - car.Nos) * 10f;
                st.LastNos = car.Nos;
            }
        }
    }

    /// <summary>[ours] Counts a racer's hits on traffic cars (GRacerInfo::GetTrafficCarsHit).</summary>
    public sealed class TrafficHitCounter : MonoBehaviour
    {
        public RaceManager.Entrant Entrant;

        void OnCollisionEnter(Collision c)
        {
            if (Entrant == null || RaceManager.Active == null) return;
            var other = c.rigidbody ? c.rigidbody.gameObject : c.gameObject;
            var pc = other.GetComponentInParent<PlayerCar>();
            bool traffic = (pc != null && pc.Traffic) || other.GetComponentInParent<Game.TrafficPhysics.TrafficVehicle>() != null;
            if (traffic) RaceStatsTracker.For(Entrant).TrafficHits++;
        }
    }
}
