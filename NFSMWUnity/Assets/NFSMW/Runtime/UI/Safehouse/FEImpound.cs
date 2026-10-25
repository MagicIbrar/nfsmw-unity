using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// The career cars' FECareerRecord / FEImpoundData (Database/VehicleDB.cpp:147-206, 1216-1434) over
    /// FEDb.Data.CareerCars, keyed by the car's index in Profile.Cars (MaxBusted stays on OwnedCar.MaxBusted).
    /// Callers: Runtime/Game/Gameplay/CareerStable.cs (race won, pursuit evaded / busted) and the career Car_Select:
    /// <list type="bullet">
    /// <item>busted: <see cref="NotifyBusted"/> (TimesBusted + 1; true when the strike limit is reached →
    /// <see cref="BecomeImpounded"/>(strike limit)), <see cref="CommitPursuitCarData"/>(car, infractions, bounty, evaded = false);</item>
    /// <item>evaded: <see cref="CommitPursuitCarData"/>(…, evaded = true), <see cref="AdjustStableImpound_EvadePursuit"/>,
    /// <see cref="AdjustStableHeat_EvadePursuit"/>;</item>
    /// <item>event won: <see cref="AdjustStableImpound_EventWin"/> (a released car sends SMS 0x78), <see cref="AdjustStableHeat_EventWin"/>;</item>
    /// <item>milestone: <see cref="AdjustStableHeat_MilestoneComplete"/>; a sold / deleted car: <see cref="OnCarRemoved"/>.</item>
    /// </list>
    /// </summary>
    public static class FEImpound
    {
        /// <summary>g_fImpoundPercentageOfOriginalCost (speed.exe 0x8F4310).</summary>
        public const float ImpoundPercentageOfOriginalCost = 0.25f;

        static List<FECareerCarRecord> Records => FEDb.Data.CareerCars ??= new List<FECareerCarRecord>();

        static int IndexOf(OwnedCar car) => car == null || GameSession.Profile == null ? -1 : GameSession.Profile.Cars.IndexOf(car);

        /// <summary>GetCareerRecordByHandle: the car's record, created on demand (FECareerRecord::Default: everything 0).</summary>
        public static FECareerCarRecord Record(OwnedCar car, bool create = true)
        {
            int i = IndexOf(car);
            if (i < 0) return null;
            var r = Records.FirstOrDefault(x => x.CarIndex == i);
            if (r == null && create) { r = new FECareerCarRecord { CarIndex = i }; Records.Add(r); }
            return r;
        }

        public static bool IsImpounded(OwnedCar car) => Record(car, false)?.IsImpounded ?? false;
        public static bool IsReleasable(OwnedCar car) => Record(car, false)?.IsReleasable ?? false;
        public static int TimesBusted(OwnedCar car) => Record(car, false)?.TimesBusted ?? 0;

        /// <summary>FECarRecord::GetReleaseFromImpoundCost (VDB:900): Cost × g_fImpoundPercentageOfOriginalCost.</summary>
        public static int ReleaseCost(OwnedCar car)
        {
            var lot = GameSession.Career?.CarLot.FirstOrDefault(l => l.Car == car?.Car);
            return (int)((lot?.Cost ?? 0) * ImpoundPercentageOfOriginalCost);
        }

        /// <summary>FEImpoundData::BecomeImpounded (VDB:1225): state = reason, TimesBusted = MaxBusted, 5 days before release.</summary>
        public static void BecomeImpounded(OwnedCar car, int reason)
        {
            var r = Record(car);
            if (r == null) return;
            r.ImpoundedState = reason;
            r.TimesBusted = car.MaxBusted;
            r.DaysBeforeRelease = 5;
        }

        /// <summary>NotifyPlayerPaidToRelease / NotifyPlayerUsedMarkerToRelease (VDB:1231-1239) + SetVehicleHeat(0) (QRCS:80-85).</summary>
        public static void Release(OwnedCar car)
        {
            var r = Record(car);
            if (r == null) return;
            r.TimesBusted = 0; r.ImpoundedState = FECareerCarRecord.ImpoundNone; r.DaysBeforeRelease = 0;
            CarParts.SetHeat(car, 0f);
        }

        /// <summary>NotifyWin (VDB:1241): an impounded car counts a day; at 0 it becomes releasable (true once).</summary>
        public static bool NotifyWin(OwnedCar car)
        {
            var r = Record(car, false);
            if (r == null || !r.IsImpounded) return false;
            if (r.DaysBeforeRelease > 0) r.DaysBeforeRelease--;
            if (r.DaysBeforeRelease == 0 && r.ImpoundedState != FECareerCarRecord.ImpoundReleased) { r.ImpoundedState = FECareerCarRecord.ImpoundReleased; return true; }
            return false;
        }

        /// <summary>NotifyBusted (VDB:1254): EvadeCount 0, TimesBusted + 1; true when TimesBusted ≥ MaxBusted.</summary>
        public static bool NotifyBusted(OwnedCar car)
        {
            var r = Record(car);
            if (r == null) return false;
            r.EvadeCount = 0;
            r.TimesBusted++;
            return r.TimesBusted >= car.MaxBusted;
        }

        /// <summary>NotifyEvade (VDB:1260): three evades in a row take a strike back; then NotifyWin.</summary>
        public static bool NotifyEvade(OwnedCar car)
        {
            var r = Record(car);
            if (r == null) return false;
            if (!r.IsImpounded)
            {
                r.EvadeCount++;
                if (r.EvadeCount > 2) { r.TimesBusted--; r.EvadeCount = 0; }
                if (r.TimesBusted < 0) r.TimesBusted = 0;
            }
            return NotifyWin(car);
        }

        /// <summary>CanAddMaxBusted / AddMaxBusted (VDB:1275-1287) with g_MaximumMaximumTimesBusted.</summary>
        public static bool CanAddMaxBusted(OwnedCar car) => car != null && car.MaxBusted < ShopMarkers.MaximumTimesBusted && !IsImpounded(car);
        public static void AddMaxBusted(OwnedCar car)
        {
            if (car == null) return;
            car.MaxBusted = Mathf.Min(car.MaxBusted + 1, ShopMarkers.MaximumTimesBusted);
        }

        /// <summary>FECareerRecord::CommitPursuitCarData (VDB:1392): unserved infractions added; evaded → bounty + evaded count, else busted count.</summary>
        public static void CommitPursuitCarData(OwnedCar car, uint infractions, int bounty, bool evaded)
        {
            var r = Record(car);
            if (r == null) return;
            for (int i = 0; i < 8; i++) if ((infractions & (1u << i)) != 0) r.Unserved[i]++;
            if (evaded) { r.Bounty += bounty; r.NumEvadedPursuits++; }
            else r.NumBustedPursuits++;
        }

        /// <summary>ServeAllIncractions (VDB:1430): paying the fine moves the unserved infractions to served.</summary>
        public static void ServeAllInfractions(OwnedCar car)
        {
            var r = Record(car, false);
            if (r == null) return;
            for (int i = 0; i < 8; i++) { r.Served[i] += r.Unserved[i]; r.Unserved[i] = 0; }
        }

        /// <summary>
        /// The infractions collections FEInfractionsData::GetFineValue (VDB:1176-1214) reads, in GInfractionManager bit order
        /// (Speeding 1, Racing 2, Reckless 4, Assault 8, HitAndRun 16, Damage 32, Resist 64, OffRoad 128): attributes.bin has
        /// exactly these eight (amounts 150 / 350 / 1000 / 350 / 300 / 100 / 300 / 75; harness `fe9data`). The port used
        /// excessive_speeding / ramming / damage_to_property / driving_off_roadway before, which do not exist (fined 0).
        /// </summary>
        public static readonly string[] InfractionKeys = { "speeding", "racing", "reckless_driving", "assault", "hit_and_run", "damage", "resisting_arrest", "off_road" };

        /// <summary>FEInfractionsData::GetFineValue (VDB:1176): 0 without infractions, else Σ count × infractions/&lt;key&gt;.amount
        /// (a missing collection adds nothing).</summary>
        public static int FineValue(int[] counts)
        {
            if (counts == null || counts.Sum() == 0) return 0;
            int fines = 0;
            var db = GameData.Attributes;
            for (int i = 0; i < 8 && i < counts.Length; i++)
            {
                var c = db?.Get("infractions", InfractionKeys[i]);
                if (c != null) fines += counts[i] * c.Int("amount");
            }
            return fines;
        }

        public static int FineValue(OwnedCar car, bool unserved = true)
        {
            var r = Record(car, false);
            return r == null ? 0 : FineValue(unserved ? r.Unserved : r.Served);
        }

        // ---- FEPlayerCarDB stable totals (VDB:274-412): ForAllCareerRecordsSum over the career cars' records, plus the
        //      sold-car history where the exe keeps one (bounty, evaded, busted, infraction counts) ----

        static IEnumerable<FECareerCarRecord> CareerRecords()
        {
            var p = GameSession.Profile;
            if (p == null) yield break;
            for (int i = 0; i < p.Cars.Count; i++)
            {
                var r = Records.FirstOrDefault(x => x.CarIndex == i);
                if (r != null) yield return r;
            }
        }

        static FEDatabaseData Db => FEDb.Data;

        /// <summary>GetTotalBounty (VDB:320): Σ record bounty + SoldHistoryBounty.</summary>
        public static int TotalBounty() => CareerRecords().Sum(r => r.Bounty) + Db.SoldHistoryBounty;
        /// <summary>GetTotalEvadedPursuits (0x590550).</summary>
        public static int TotalEvadedPursuits() => CareerRecords().Sum(r => r.NumEvadedPursuits) + Db.SoldHistoryNumEvadedPursuits;
        /// <summary>GetTotalBustedPursuits (0x5905A0).</summary>
        public static int TotalBustedPursuits() => CareerRecords().Sum(r => r.NumBustedPursuits) + Db.SoldHistoryNumBustedPursuits;
        /// <summary>GetNumImpoundedCars (0x5905F0): records whose impound data IsImpounded (no history).</summary>
        public static int NumImpoundedCars() => CareerRecords().Count(r => r.IsImpounded);
        /// <summary>GetTotalFines(unserved) (0x590630): Σ the records' fine values (no history).</summary>
        public static int TotalFines(bool unserved) => CareerRecords().Sum(r => FineValue(unserved ? r.Unserved : r.Served));
        /// <summary>GetNumCareerCarsWithARecord (0x5906B0): every career car gets a career record in the exe (CreateNewCareerCar
        /// 0x599D20), so this is the career car count.</summary>
        public static int NumCareerCarsWithARecord() => GameSession.Profile?.Cars.Count ?? 0;

        /// <summary>GetNumInfraction(type bit index 0..7, unserved) (0x5903B0): Σ records + the sold history's count.</summary>
        public static int NumInfraction(int type, bool unserved)
        {
            if (type < 0 || type >= 8) return 0;
            var h = unserved ? Db.SoldHistoryUnserved : Db.SoldHistoryServed;
            return CareerRecords().Sum(r => (unserved ? r.Unserved : r.Served)[type]) + (h != null && type < h.Length ? h[type] : 0);
        }

        /// <summary>GetTotalNumInfractions(unserved) (0x590430): Σ FEInfractionsData::NumInfractions + the history's.</summary>
        public static int TotalNumInfractions(bool unserved)
        {
            var h = unserved ? Db.SoldHistoryUnserved : Db.SoldHistoryServed;
            return CareerRecords().Sum(r => (unserved ? r.Unserved : r.Served).Sum()) + (h?.Sum() ?? 0);
        }

        /// <summary>GetNumInfractionsOnCar(handle, unserved) (0x580EA0).</summary>
        public static int NumInfractionsOnCar(OwnedCar car, bool unserved)
        {
            var r = Record(car, false);
            return r == null ? 0 : (unserved ? r.Unserved : r.Served).Sum();
        }

        /// <summary>GetPreferedCarName (0x599C50): the career car with the most busted + evaded pursuits (strictly more: ties keep
        /// the first) [ours: the car, the exe returns its name hash]; null when none has any.</summary>
        public static OwnedCar PreferedCar()
        {
            var p = GameSession.Profile;
            if (p == null) return null;
            int best = 0;
            OwnedCar car = null;
            for (int i = 0; i < p.Cars.Count; i++)
            {
                var r = Records.FirstOrDefault(x => x.CarIndex == i);
                if (r == null) continue;
                int times = r.NumBustedPursuits + r.NumEvadedPursuits;
                if (times > best) { best = times; car = p.Cars[i]; }
            }
            return car;
        }

        /// <summary>BackupSoldCarHistory (0x580FD0, VDB:414): a sold car's bounty, evaded / busted counts and both infraction
        /// sets go to the stable's SoldHistory* (DeleteCar with was_sold = true: the career Car_Select sell, QRCS:478).</summary>
        public static void BackupSoldCarHistory(int index)
        {
            var r = Records.FirstOrDefault(x => x.CarIndex == index);
            if (r == null) return;
            var d = Db;
            d.SoldHistoryBounty += r.Bounty;
            d.SoldHistoryNumEvadedPursuits += r.NumEvadedPursuits;
            d.SoldHistoryNumBustedPursuits += r.NumBustedPursuits;
            if (d.SoldHistoryUnserved == null || d.SoldHistoryUnserved.Length < 8) d.SoldHistoryUnserved = new int[8];
            if (d.SoldHistoryServed == null || d.SoldHistoryServed.Length < 8) d.SoldHistoryServed = new int[8];
            for (int i = 0; i < 8; i++) { d.SoldHistoryUnserved[i] += r.Unserved[i]; d.SoldHistoryServed[i] += r.Served[i]; }
        }

        /// <summary>FEPlayerCarDB::GetNumAvailableCareerCars (VDB:471): career cars that are not impounded.</summary>
        public static int NumAvailableCareerCars()
        {
            var p = GameSession.Profile;
            return p == null ? 0 : p.Cars.Count(c => !IsImpounded(c));
        }

        /// <summary>QRCarSelectBustedManager::CalcGameOver (QRCS:278): no available car and no Impound Release marker.</summary>
        public static bool CalcGameOver() => NumAvailableCareerCars() < 1 && !ShopMarkers.Has(ShopRules.M_ImpoundRelease);

        /// <summary>DeleteCareerCar(handle, was_sold) (0x590D20 → DeleteCar 0x581370): a sold car's history is backed up first,
        /// then its record goes and later indices shift down.</summary>
        public static void OnCarRemoved(int index, bool wasSold = false)
        {
            if (wasSold) BackupSoldCarHistory(index);
            Records.RemoveAll(r => r.CarIndex == index);
            foreach (var r in Records) if (r.CarIndex > index) r.CarIndex--;
        }

        static float Adjust(string field)
        {
            var c = GameData.Attributes?.Get("pursuitlevels", "default");
            return c != null && c.Has(field) ? c.Float(field) : 1f;
        }

        /// <summary>AdjustStableHeat_EventWin / _MilestoneComplete / _EvadePursuit (VDB:147-178): every career car's heat × pursuitlevels/default.*HeatAdjust.</summary>
        public static void AdjustStableHeat_EventWin() => AdjustStableHeat(Adjust("EventWinHeatAdjust"));
        public static void AdjustStableHeat_MilestoneComplete() => AdjustStableHeat(Adjust("MilestoneCompleteHeatAdjust"));
        public static void AdjustStableHeat_EvadePursuit() => AdjustStableHeat(Adjust("EvadeSuccessHeatAdjust"));

        static void AdjustStableHeat(float k)
        {
            var p = GameSession.Profile;
            if (p == null) return;
            foreach (var c in p.Cars) CarParts.SetHeat(c, CarParts.HeatOf(c) * k);
        }

        /// <summary>AdjustStableImpound_EventWin (VDB:180): a car becoming releasable sends SMS 0x78 (120) [hook: the SMS store].</summary>
        public static void AdjustStableImpound_EventWin()
        {
            var p = GameSession.Profile;
            if (p == null) return;
            foreach (var c in p.Cars) if (NotifyWin(c) && !p.Inbox.Contains(120)) p.Inbox.Add(120);
        }

        public static void AdjustStableImpound_EvadePursuit()
        {
            var p = GameSession.Profile;
            if (p == null) return;
            foreach (var c in p.Cars) NotifyEvade(c);
        }
    }
}
