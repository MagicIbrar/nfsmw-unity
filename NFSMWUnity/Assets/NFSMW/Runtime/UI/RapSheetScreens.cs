using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>One pursuit on record (TopEvadedPursuitDetail, Database/RaceDB.hpp:177).</summary>
    [Serializable]
    public sealed class PursuitRecord
    {
        public string Id, Car;
        public float Length;
        public int Bounty, CopsInvolved, CopsDamaged, CopsDestroyed, Roadblocks, Spikes, Cts, Infractions, Helicopters;
    }

    /// <summary>[legacy] the port's old per-car record (kept for old saves; the rap sheet now reads FEImpound's FECareerRecord).</summary>
    [Serializable]
    public sealed class CarRecord
    {
        public string Car;
        public int Bounty, Fines, Unserved, Evaded, Busted;
        public bool Impounded;
    }

    /// <summary>
    /// HighScoresDatabase's pursuit part (Database/RaceDB.cpp:46-339, PC CommitPursuitInfo 0x580170, CalcPursuitRank 0x5A3C30,
    /// GetCareerCST 0x5A3F10, GeneratePursuitID 0x56E840): CareerPursuitDetails (lifetime sums, PD_* 0..7), BestPursuitRankings
    /// (single-pursuit bests with the car, PD_* 0..9), CostToStateDetails, the five top evaded pursuits by bounty and the
    /// previously pursued car. Only an evaded pursuit is committed (CommitPursuitInfo skips everything but the previous car
    /// when IPursuit::IsPerpBusted). Fed by <see cref="Watch"/> (HUD frame) and the Infractions screen (busted).
    /// The per-car police record (bounty, infractions, evaded / busted, impound) is FEImpound's FECareerRecord (VehicleDB.cpp),
    /// read through <see cref="RapStable"/>.
    /// </summary>
    [Serializable]
    public sealed class RapSheetRecord
    {
        /// <summary>GInfractionManager types (kInfraction_* bit order) and their RapSheetUS labels; fine = infractions/&lt;type&gt;.amount
        /// in the attributes (the column is only a fallback).</summary>
        public static readonly (uint label, string name, int fine)[] Types =
        {
            // fine column = attributes infractions/<key>.amount (harness fe9data infr): 150, 350, 1000, 350, 300, 100, 300, 75
            (0x676B575C, "SPEEDING", 150), (0x81705AC5, "EXCESSIVE SPEEDING", 350), (0x0E9D1CB6, "RECKLESS DRIVING", 1000),
            (0x1536B1FA, "RAMMING A POLICE VEHICLE", 350), (0xAAF89AB3, "HIT AND RUN", 300), (0x706C0F0D, "DAMAGE TO PROPERTY", 100),
            (0xAD524B30, "RESISTING ARREST", 300), (0xF9748B0B, "DRIVING OFF ROADWAY", 75),
        };

        /// <summary>uiRapSheetCTS rows (RAP_CTS_* in screen order, GetCareerCST): label and value per unit (property damage: summed value).</summary>
        public static readonly (uint label, string name, int unit)[] CtsRows =
        {
            (0x3682A8CF, "DAMAGE TO PROPERTY", 1), (0x6DE4810A, "INSURANCE CLAIMS", 500), (0x89A9C941, "PATROL VEHICLES DEPLOYED", 250),
            (0x443B615F, "SPECIAL UNITS DEPLOYED", 450), (0xD3AA88DA, "DAMAGED POLICE VEHICLES", 250), (0xBDB16FEA, "IMMOBILIZED POLICE VEHICLES", 5000),
            (0xD320C6C3, "ROADBLOCKS DEPLOYED", 500), (0xA83862AF, "SPIKE STRIPS DEPLOYED", 250), (0x80E9CCB2, "HELICOPTERS DEPLOYED", 2000),
        };

        public const int PD_Length = 0, PD_CopsInvolved = 1, PD_CopsDamaged = 2, PD_CopsDestroyed = 3, PD_Spikes = 4, PD_Roadblocks = 5,
            PD_Helicopters = 6, PD_CostToState = 7, PD_Infractions = 8, PD_Bounty = 9;

        public string CaseFile;
        /// <summary>CareerPursuitDetails.Value[0..7] (length in Timer ticks, sec × 4000).</summary>
        public int[] Career = new int[8];
        /// <summary>BestPursuitRankings[0..9]: value (length in ticks) and car key.</summary>
        public int[] BestValue = new int[10];
        public string[] BestCar = new string[10];
        /// <summary>CostToStateDetails: properties damaged (count, value), traffic hit, cop cars / support vehicles / roadblocks /
        /// spike strips / heli spike strips deployed.</summary>
        public int CtsProperties, CtsPropertyValue, CtsTraffic, CtsCopCars, CtsSupport, CtsRoadblocks, CtsSpikes, CtsHeliSpikes;
        public List<PursuitRecord> TopPursuits = new List<PursuitRecord>();
        /// <summary>PreviouslyPursuedCarFEKey (the VD "RECENT VEHICLE").</summary>
        public string RecentCar;
        /// <summary>CareerSettings HasBeenBustedOnce (uiInfractions.cpp:19).</summary>
        public bool BustedBefore;
        /// <summary>CareerSettings flag 0x10 (SetHasRapSheet: uiRepSheetRivalFlow stage 6 at bin 13, Lua Game.SetHasRapSheet 0x605670).</summary>
        public bool HasRapSheet;
        public bool Migrated;

        // ---- legacy fields of the port's first rap sheet (read once by Migrate) ----
        public int[] Unserved = new int[8], Total = new int[8];
        public int FinesDue, FinesPaid, CarsImpounded, CopsDamaged, CopsDestroyed;
        public int[] CtsQty = new int[9];
        public int CtsTotal;
        public List<CarRecord> Cars = new List<CarRecord>();
        public PursuitRecord Best = new PursuitRecord(), Lifetime = new PursuitRecord();

        /// <summary>Port infraction names (PursuitSystem) → kInfraction_* index (GInfractionManager.h:12).</summary>
        public static int TypeOf(string infraction) => infraction switch
        {
            "Speeding" => 0, "Racing" or "Excessive Speeding" => 1, "Reckless Driving" => 2, "Ramming a Police Vehicle" => 3,
            "Hit and Run" => 4, "Damage to Property" => 5, "Resisting Arrest" => 6, "Driving Off Roadway" => 7, _ => -1,
        };

        /// <summary>A pursuit's infractions as kInfraction_* bits (the exe's GInfractionManager::GetInfractions).</summary>
        public static uint BitsOf(PursuitStats s)
        {
            uint bits = 0;
            if (s != null) foreach (var n in s.Infractions) { int t = TypeOf(n); if (t >= 0) bits |= 1u << t; }
            return bits;
        }

        /// <summary>The old record's numbers carried into the exe layout once.</summary>
        public void Migrate()
        {
            if (Migrated) return;
            Migrated = true;
            if (Career == null || Career.Length < 8) Career = new int[8];
            if (BestValue == null || BestValue.Length < 10) BestValue = new int[10];
            if (BestCar == null || BestCar.Length < 10) BestCar = new string[10];
            if (Lifetime != null && Lifetime.Length > 0 && Career.All(v => v == 0))
            {
                Career[PD_Length] = Ticks(Lifetime.Length); Career[PD_CopsInvolved] = Lifetime.CopsInvolved;
                Career[PD_CopsDamaged] = Lifetime.CopsDamaged; Career[PD_CopsDestroyed] = Lifetime.CopsDestroyed;
                Career[PD_Spikes] = Lifetime.Spikes; Career[PD_Roadblocks] = Lifetime.Roadblocks; Career[PD_CostToState] = CtsTotal;
            }
            if (Best != null && Best.Length > 0 && BestValue.All(v => v == 0))
            {
                BestValue[PD_Length] = Ticks(Best.Length); BestValue[PD_CopsInvolved] = Best.CopsInvolved;
                BestValue[PD_CopsDamaged] = Best.CopsDamaged; BestValue[PD_CopsDestroyed] = Best.CopsDestroyed;
                BestValue[PD_Spikes] = Best.Spikes; BestValue[PD_Roadblocks] = Best.Roadblocks; BestValue[PD_CostToState] = Best.Cts;
                BestValue[PD_Infractions] = Best.Infractions; BestValue[PD_Bounty] = Best.Bounty;
            }
            if (CtsQty != null && CtsQty.Length >= 9 && CtsCopCars == 0)
            {
                CtsPropertyValue = CtsQty[0]; CtsCopCars = CtsQty[2]; CtsRoadblocks = CtsQty[6]; CtsSpikes = CtsQty[7];
            }
        }

        /// <summary>Timer(float seconds) packing (Misc/Timer.hpp:111): (int)(sec × 4000 + 0.5).</summary>
        public static int Ticks(float sec) => (int)(sec * 4000f + 0.5f);
        public static float Seconds(int ticks) => ticks / 4000f;

        /// <summary>GeneratePursuitID (PC 0x56E840): "MW-" then 11 alternating digit / letter characters written into a 12-byte
        /// buffer whose last byte is then zeroed — 8 survive (D L D L D L D L).</summary>
        static string NewId()
        {
            var sb = new System.Text.StringBuilder("MW-");
            for (int i = 0; i < 8; i++) sb.Append((i & 1) != 0 ? (char)('A' + UnityEngine.Random.Range(0, 26)) : (char)('0' + UnityEngine.Random.Range(0, 10)));
            return sb.ToString();
        }

        /// <summary>
        /// HighScoresDatabase::CommitPursuitInfo (RaceDB.cpp:46): previous car always; evaded only → CostToStateDetails,
        /// CareerPursuitDetails (+= this pursuit), BestPursuitRankings (strictly greater, with the car), and the top-five list
        /// by bounty (a new entry shifts the rest down, GeneratePursuitID). The CostToStateDetails come from PursuitStats' AIPursuit
        /// counters (heli spike strips stay 0: the port's helicopter drops none).
        /// </summary>
        public void Commit(PursuitStats s, bool busted, string car)
        {
            Migrate();
            RecentCar = car;
            if (busted || s == null) return;
            int infractions = s.Infractions.Count, bounty = Mathf.RoundToInt(s.Bounty), cts = s.CostToState;
            // RaceDB.cpp:55-62: the pursuit's own deployed / hit / property counters (PursuitStats, gameplay owner's hook)
            CtsCopCars += s.CopCarsDeployed; CtsSupport += s.SupportVehiclesDeployed; CtsTraffic += s.TrafficCarsHit; CtsRoadblocks += s.RoadblocksDeployed;
            CtsSpikes += s.SpikeStripsDeployed; CtsHeliSpikes += s.HeliSpikeStripsDeployed; CtsProperties += s.PropertiesDamaged; CtsPropertyValue += s.PropertyDamageValue;
            int length = Ticks(s.Length);
            Career[PD_Length] += length;
            Career[PD_CopsInvolved] += s.CopsInvolved;
            Career[PD_CopsDamaged] += s.CopsDamaged;
            Career[PD_CopsDestroyed] += s.CopsDestroyed;
            Career[PD_Spikes] += s.SpikesDodged;
            Career[PD_Roadblocks] += s.RoadblocksDodged;
            Career[PD_Helicopters] += s.HeliSpawns;   // PD_HELICOPTERS_INVOLVED += GetNumHeliSpawns (RaceDB.cpp:70)
            Career[PD_CostToState] += cts;
            void Best(int type, int v) { if (v > BestValue[type]) { BestValue[type] = v; BestCar[type] = car; } }
            Best(PD_Length, length); Best(PD_CopsInvolved, s.CopsInvolved); Best(PD_CopsDamaged, s.CopsDamaged);
            Best(PD_CopsDestroyed, s.CopsDestroyed); Best(PD_Spikes, s.SpikesDodged); Best(PD_Roadblocks, s.RoadblocksDodged);
            Best(PD_Infractions, infractions); Best(PD_CostToState, cts); Best(PD_Bounty, bounty);
            int pos = 5;
            for (int i = 0; i < 5; i++) if (bounty > (i < TopPursuits.Count ? TopPursuits[i].Bounty : 0)) { pos = i; break; }
            if (pos == 5) return;
            TopPursuits.Insert(Mathf.Min(pos, TopPursuits.Count), new PursuitRecord
            {
                Id = NewId(), Car = car, Length = s.Length, Bounty = bounty, CopsInvolved = s.CopsInvolved, CopsDamaged = s.CopsDamaged,
                CopsDestroyed = s.CopsDestroyed, Roadblocks = s.RoadblocksDodged, Spikes = s.SpikesDodged, Cts = cts, Infractions = infractions,
            });
            if (TopPursuits.Count > 5) TopPursuits.RemoveRange(5, TopPursuits.Count - 5);
        }

        /// <summary>CareerPursuitScores::GetValue (RaceDB.cpp:349): infractions and bounty come from the stable.</summary>
        public int CareerScore(int type)
        {
            Migrate();
            if (type == PD_Infractions) return RapStable.TotalNumInfractions(true) + RapStable.TotalNumInfractions(false);
            if (type == PD_Bounty) return RapStable.TotalBounty();
            return type >= 0 && type < 8 ? Career[type] : 0;
        }

        /// <summary>GetCareerCST (PC 0x5A3F10) in uiRapSheetCTS row order: (quantity, value).</summary>
        public (int qty, int value) CareerCst(int row)
        {
            Migrate();
            switch (row)
            {
                case 0: return (CtsProperties, CtsPropertyValue);
                case 1: return (CtsTraffic, CtsTraffic * 500);
                case 2: return (CtsCopCars, CtsCopCars * 250);
                case 3: return (CtsSupport, CtsSupport * 450);
                case 4: { int q = CareerScore(PD_CopsDamaged); return (q, q * 250); }
                case 5: { int q = CareerScore(PD_CopsDestroyed); return (q, q * 5000); }
                case 6: return (CtsRoadblocks, CtsRoadblocks * 500);
                case 7: return (CtsSpikes, CtsSpikes * 250);
                default: { int q = CareerScore(PD_Helicopters); return (q, q * 2000); }
            }
        }

        static readonly string[] RankKeys = { "pursuit_length", "cops_involved", "cops_damaged", "cops_destroyed", "tire_spikes_dodged", "roadblocks_dodged", "helis_involved", "cost_to_state", "total_infractions", "bounty" };

        /// <summary>The frontend VLT record of a ranking (CalcPursuitRank's keys; single pursuit = "&lt;key&gt;_in_pursuit").</summary>
        public static AttribCollection RankRecord(int type, bool career) =>
            type < 0 || type >= RankKeys.Length ? null : GameData.Attributes?.Get("frontend", career ? RankKeys[type] : RankKeys[type] + "_in_pursuit");

        /// <summary>RapSheetRanks (0xF9A7D5F7): 15 floats; NameId (0x2C3C7FEB): 15 bytes (rival bin of each rank).</summary>
        public static float[] Ranks(AttribCollection c)
        {
            if (c == null || !c.TryGetKey(0xF9A7D5F7, out int at, out int count, out _)) return null;
            var r = new float[count];
            for (int i = 0; i < count; i++) r[i] = BitConverter.ToSingle(c.Source.Data, at + i * 4);
            return r;
        }

        public static int NameId(AttribCollection c, int i) =>
            c != null && c.TryGetKey(0x2C3C7FEB, out int at, out int count, out _) && i >= 0 && i < Math.Max(count, 15) ? c.Source.Data[at + i] : 0;

        /// <summary>
        /// CalcPursuitRank (PC 0x5A3C30): 16 (unranked) unless the record has exactly 15 ranks; rank = first i with player ≥
        /// rank[i] (pursuit length: ticks ≥ (int)(rank × 4000 + 0.5); else player ≥ (int)rank) + 1. Player = career score or the
        /// single-pursuit best.
        /// </summary>
        public int CalcPursuitRank(int type, bool career)
        {
            Migrate();
            var ranks = Ranks(RankRecord(type, career));
            if (ranks == null || ranks.Length != 15) return 16;
            int player = career ? CareerScore(type) : BestValue[type];
            for (int i = 0; i < 15; i++)
            {
                int threshold = type == PD_Length ? (int)(ranks[i] * 4000f + 0.5f) : (int)ranks[i];
                if (player >= threshold) return i + 1;
            }
            return 16;
        }

        // ---- watching pursuits ----------------------------------------------------------------------------------

        static PursuitStats _stats;

        /// <summary>Called every HUD frame: an evaded career pursuit is committed when it ends (UserProfile::CommitPursuitInfo via
        /// AICopManager::CommitPursuitDetails 0x411240); a busted one by the Infractions screen.</summary>
        public static void Watch()
        {
            var police = Systems.Police;
            if (police == null) return;
            if (police.InPursuit)
            {
                if (police.Challenge == null) _stats = police.Stats;   // CommitPursuitDetails is skipped in Challenge Series races
                return;
            }
            if (_stats == null) return;
            var s = _stats;
            _stats = null;
            var p = GameSession.Profile;
            if (!s.Evaded || p == null || GameSession.Mode != GameMode.Career) return;
            (p.RapSheet ??= new RapSheetRecord()).Commit(s, false, p.Car?.Car);
            p.Save();
        }

        /// <summary>A busted pursuit is put on file by the Infractions screen; drop the watcher's copy.</summary>
        public static void Forget() => _stats = null;
    }

    /// <summary>
    /// FEPlayerCarDB's stable totals (Database/VehicleDB.cpp:275-447) over the career cars' FECareerRecords (FEImpound): the sums
    /// run over every career car (ForAllCareerRecordsSum, filter region-all | career). [?] Sold cars' history
    /// (BackupSoldCarHistory) is not kept by FEImpound; GetTotalBounty is the profile's bounty (the port keeps one bounty that
    /// also holds race / milestone bounty; the records only get the pursuits').
    /// </summary>
    public static class RapStable
    {
        static IEnumerable<(OwnedCar car, FECareerCarRecord rec)> Records()
        {
            var p = GameSession.Profile;
            if (p == null) yield break;
            foreach (var c in p.Cars)
            {
                var r = FEImpound.Record(c, false);
                if (r != null) yield return (c, r);
            }
        }

        public static int TotalBounty() => GameSession.Profile?.Bounty ?? 0;
        // the stable totals are FEImpound's (VehicleDB.cpp port, with the sold-car history BackupSoldCarHistory 0x580FD0 keeps)
        public static int TotalEvadedPursuits() => FEImpound.TotalEvadedPursuits();
        public static int TotalBustedPursuits() => FEImpound.TotalBustedPursuits();
        public static int NumImpoundedCars() => FEImpound.NumImpoundedCars();
        public static int TotalFines(bool unserved) => FEImpound.TotalFines(unserved);
        public static int NumInfraction(int type, bool unserved) => FEImpound.NumInfraction(type, unserved);
        public static int TotalNumInfractions(bool unserved) => FEImpound.TotalNumInfractions(unserved);
        public static int NumInfractionsOnCar(OwnedCar car, bool unserved) => FEImpound.NumInfractionsOnCar(car, unserved);
        /// <summary>GetNumCareerCarsWithARecord: every career car has a FECareerRecord in the exe.</summary>
        public static int NumCareerCarsWithARecord() => GameSession.Profile?.Cars.Count ?? 0;

        /// <summary>GetPreferedCarName (PC 0x599C50): the career car with the most evaded + busted pursuits (strictly more).</summary>
        public static string PreferredCar()
        {
            int best = 0; string car = null;
            foreach (var (c, r) in Records())
            {
                int t = r.NumBustedPursuits + r.NumEvadedPursuits;
                if (t > best) { best = t; car = c.Car; }
            }
            return car;
        }
    }

    /// <summary>The rap-sheet packages' shared pieces: CASE FILE / NAME header lines, GetLocalizedString formats.</summary>
    public abstract class RapSheetPage : FEMenuScreen
    {
        // the rap sheet is only reached from the safehouse crib: the garage renders behind it (GarageMainScreen)
        public override bool ShowCar => true;
        public override string Platform => "CAREER_SAFEHOUSE.BIN";
        protected static Profile P => GameSession.Profile;
        protected static RapSheetRecord R => P == null ? new RapSheetRecord() : P.RapSheet ??= new RapSheetRecord();
        protected static string ProfileName => P?.Name ?? "";

        /// <summary>FEPrintf(pkg, obj, GetLocalizedString(label), args).</summary>
        protected void Line(uint obj, uint label, params object[] args) => Printf(obj, K.Format(Lang(label), args));

        /// <summary>FEPrintf(pkg, 0x1232703A, "CASE FILE: %s", CaseFileName).</summary>
        protected void CaseFileLine() => Line(0x1232703A, 0xE21D083C, K.CaseFile());

        /// <summary>A package switch to another rap-sheet page (QueuePackageSwitch).</summary>
        protected void SwitchTo(FEScreen next) => FE.Defer(() => { if (FE.Top == this) FE.Replace(next); });

        /// <summary>
        /// The PC button screens' mouse case (uiRapSheetMain 0x52C82C): a click (0x7EABCA56) on the button under the highlight
        /// (+0x128, remembered from 0xABC08912) queues BUTTON_PRESSED from it, a click elsewhere makes it the current button.
        /// [?] the engine's current button stands in for the remembered highlight.
        /// </summary>
        protected void MouseClick(FEView.Node obj)
        {
            if (obj == null) return;
            if (View.CurrentButton != obj) View.SetCurrentButton(obj);
            else View.QueueMessage(FEHash.ButtonPressed, obj, FEView.ToBroadcast);
        }

        /// <summary>The car's language name (GetFECarNameHashFromFEKey → GetLocalizedString).</summary>
        protected static string CarName(string car) => string.IsNullOrEmpty(car) ? "" : CarNames.Display(car).ToUpperInvariant();
    }

    /// <summary>
    /// RapSheetLogin.fng (Arg 0) / RapSheetLogin2.fng (Arg 2) — uiRapSheetLogin (PC ctor 0x546510, NotificationMessage 0x52C670):
    /// Login2 prints 0x03CC94D6 = "&gt; %s" (profile name). Accept or a mouse click (0x7EABCA56) stops the login sound
    /// (UISND_RAPSHEET_LOGIN / LOGIN2) and marks the step done; back sets return-to-menu. EXIT_COMPLETE → MainMenu_Sub (clear
    /// RAP_SHEET mode) after back, RapSheetMain after Login2 / an accept, else RapSheetLogin2 (Arg 2). The career crib's Rap
    /// Sheet option opens Login (uiCareerMain.cpp; fe_safehouse.md §4).
    /// </summary>
    public sealed class RapSheetLoginScreen : RapSheetPage
    {
        int _screen;
        bool _returnToMainMenu;
        public RapSheetLoginScreen(int screen = 0) { _screen = screen; }
        public override string Package => _screen == 2 ? "RapSheetLogin2" : "RapSheetLogin";

        protected override void Setup()
        {
            FEDb.SetGameMode(FEGameMode.RapSheet);
            if (_screen == 2) Printf(0x03CC94D6, "> " + ProfileName);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case 0x7EABCA56:
                case FEHash.PadAccept:
                    if (_screen == 0) NfsMw.Audio.FeSound.Stop((int)NfsMw.Audio.FeSoundId.RapsheetLogin);
                    else if (_screen == 2) NfsMw.Audio.FeSound.Stop((int)NfsMw.Audio.FeSoundId.RapsheetLogin2);
                    _screen = 3;
                    break;
                case FEHash.PadBack:
                    _returnToMainMenu = true;
                    break;
                case FEHash.ExitComplete:
                    if (_returnToMainMenu) { FEDb.ClearGameMode(FEGameMode.RapSheet); FE.Defer(() => { if (FE.Top == this) FE.Pop(); }); }
                    else if (_screen >= 2 && _screen < 4) SwitchTo(new RapSheetScreen());
                    else SwitchTo(new RapSheetLoginScreen(2));
                    break;
            }
        }
    }

    /// <summary>
    /// RapSheetMain.fng — uiRapSheetMain (UIWidgetMenu; PC ctor 0x5465C0, NotificationMessage 0x52C790, RefreshHeader 0x52CA00).
    /// Header: CASE FILE, 0xE3DA78E7 NAME, E8 BOUNTY (GetTotalBounty), E9 COST TO STATE (career PD_COST_TO_STATE), EA CARS
    /// IMPOUNDED, EB FINES DUE (unserved), EC PURSUITS EVADED, ED BUSTED, EE CARS MONITORED. INIT_COMPLETE focuses BUTTON_&lt;last&gt;
    /// (PC "BUTTON_%d"; the decomp's "BL_%d" is the GC build). PC mouse: highlight 0xABC08912 remembers the object, a click
    /// 0x7EABCA56 on it presses (queues BUTTON_PRESSED) else focuses it; 0x08B84A5C takes the current button. EXIT_COMPLETE:
    /// BUTTON_1..6 → RS, US, CTS, TEP, Rankings, VD (last button 1..6), anything else → MainMenu_Sub, clear RAP_SHEET mode.
    /// </summary>
    public sealed class RapSheetScreen : RapSheetPage
    {
        public override string Package => "RapSheetMain";
        uint _pressed;

        protected override void Setup()
        {
            FEDb.SetGameMode(FEGameMode.RapSheet);
            var r = R;
            CaseFileLine();
            Line(0xE3DA78E7, 0x6031106E, ProfileName);
            Line(0xE3DA78E8, 0x364E4525, RapStable.TotalBounty());
            Line(0xE3DA78E9, 0xA355FEDD, r.CareerScore(RapSheetRecord.PD_CostToState));
            Line(0xE3DA78EA, 0xB1E58DB1, RapStable.NumImpoundedCars());
            Line(0xE3DA78EB, 0x79FB7D16, RapStable.TotalFines(true));
            Line(0xE3DA78EC, 0x463B461B, RapStable.TotalEvadedPursuits());
            Line(0xE3DA78ED, 0xC5094459, RapStable.TotalBustedPursuits());
            Line(0xE3DA78EE, 0x6DEE0C7A, RapStable.NumCareerCarsWithARecord());
            FocusLast();
        }

        void FocusLast()
        {
            int b = FEDb.LastButton(Package);
            if (b == 0) b = 1;
            View.SetCurrentButton(HashF("BUTTON_" + b));
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.InitComplete: FocusLast(); break;
                case FEHash.ButtonPressed: _pressed = obj?.Obj.Hash ?? 0; break;
                case 0x08B84A5C: _pressed = View.CurrentButton?.Obj.Hash ?? 0; break;
                case 0x7EABCA56: MouseClick(obj); break;
                case FEHash.ExitComplete:
                {
                    int n = _pressed - 0xCDA0A66Bu < 6 ? (int)(_pressed - 0xCDA0A66Bu) + 1 : 0;   // BUTTON_1..6
                    FEDb.SetLastButton(Package, n == 0 ? 1 : n);
                    switch (n)
                    {
                        case 1: SwitchTo(new RapSheetSummaryScreen()); break;
                        case 2: SwitchTo(new RapSheetInfractionsScreen()); break;
                        case 3: SwitchTo(new RapSheetCostScreen()); break;
                        case 4: SwitchTo(new RapSheetPursuitsScreen()); break;
                        case 5: SwitchTo(new RapSheetRankingsScreen()); break;
                        case 6: SwitchTo(new RapSheetVehiclesScreen()); break;
                        default:
                            FEDb.ClearGameMode(FEGameMode.RapSheet);
                            FE.Defer(() => { if (FE.Top == this) FE.Pop(); });   // MainMenu_Sub (the crib under it)
                            break;
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// RapSheetRS.fng — uiRapSheetRS (PC ctor 0x546650, RefreshHeader 0x52CBC0): NAME, BOUNTY, COST TO STATE, CARS IMPOUNDED,
    /// UNSERVED / SERVED infractions, TIMES EVADED / BUSTED, FINES DUE / PAID, police vehicles damaged / immobilized (career
    /// scores). 0x90211462 = RAP_SUMMARY_%.2d (PC string 0x89D414; the decomp's RAPSHEET_WARNING_%d does not exist) of frontend
    /// "rapsheet_summary_strings" (0x89D428): level = first i with unserved + served ≤ WarningLevel[i] ([0,100,400,800,1000]),
    /// none → the count, 0 → 1. EXIT_COMPLETE → RapSheetMain.
    /// </summary>
    public sealed class RapSheetSummaryScreen : RapSheetPage
    {
        public override string Package => "RapSheetRS";

        protected override void Setup()
        {
            var r = R;
            CaseFileLine();
            Line(0xB259EEA7, 0x6031106E, ProfileName);
            Line(0xB259EEA8, 0x364E4525, RapStable.TotalBounty());
            Line(0xB259EEA9, 0xA355FEDD, r.CareerScore(RapSheetRecord.PD_CostToState));
            Line(0xB259EEAA, 0xB1E58DB1, RapStable.NumImpoundedCars());
            Line(0x6EB5AC50, 0x091CB790, RapStable.TotalNumInfractions(true));
            Line(0x6EB5AC51, 0x1903C44D, RapStable.TotalNumInfractions(false));
            Line(0xD919049F, 0x3598476F, RapStable.TotalEvadedPursuits());
            Line(0xD91904A0, 0x2E90D7ED, RapStable.TotalBustedPursuits());
            Line(0xD01E18C5, 0x82A67697, RapStable.TotalFines(true));
            Line(0xD01E18C6, 0xD77B89B7, RapStable.TotalFines(false));
            Line(0xD7E5D0CC, 0x50EC3763, r.CareerScore(RapSheetRecord.PD_CopsDamaged));
            Line(0xD7E5D0CD, 0xE8DB4BF3, r.CareerScore(RapSheetRecord.PD_CopsDestroyed));
            var summary = GameData.Attributes?.Get("frontend", "rapsheet_summary_strings");
            if (summary != null && summary.TryGetKey(0xB54D4A41, out int at, out int count, out _))
            {
                int level = count, val = RapStable.TotalNumInfractions(true) + RapStable.TotalNumInfractions(false);
                for (int i = 0; i < count; i++)
                    if ((uint)val <= BitConverter.ToUInt32(summary.Source.Data, at + i * 4)) { level = i; break; }
                if (level == 0) level = 1;
                SetLanguageHash(0x90211462, HashF($"RAP_SUMMARY_{level:00}"));
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg == FEHash.ExitComplete) SwitchTo(new RapSheetScreen());
        }
    }

    /// <summary>
    /// RapSheetUS.fng — uiRapSheetUS (ArrayScrollerMenu 1×8; PC ctor 0x5466D0, Setup 0x52CF40, RefreshHeader 0x52D420): rows
    /// INFRACTION_TYPE_n (label), UNSERVED_QTY_n "%$d" (this view's count), TOTAL_QTY_n "%$d" (both) per kInfraction_* type;
    /// 0x9D974DF3 = "UNSERVED" 0xC225D554 / "SERVED" 0x6A1151D1. Header: NAME, 0x1FFFB989 "UNSERVED INFRACTIONS: %$d" 0xBDFE114C /
    /// "SERVED INFRACTIONS: %$d" 0xAD0B7F09, 0x1FFFB98A BOUNTY, 0x1FFFB98B 0x1FF24DD3 / "FINES PAID: %$d" 0x1E424873,
    /// 0x2ECAFA80 / 0xBBE88932 0x8422B22A / "Unserved Infractions" 0x3177BB0D (the other view's name). PAD_BUTTON5 (key 1)
    /// toggles the view; EXIT_COMPLETE → RapSheetMain.
    /// </summary>
    public sealed class RapSheetInfractionsScreen : RapSheetPage
    {
        public override string Package => "RapSheetUS";
        bool _unserved = true;
        ArrayScroller _list;

        protected override void Setup()
        {
            _list = new ArrayScroller(View, 1, 8);
            for (int i = 0; i < 8; i++) _list.AddSlot(Obj(HashF("INFRACTION_TYPE_" + (i + 1))));
            Fill();
        }

        void Fill()
        {
            _list.ClearData();
            for (int t = 0; t < 8; t++) _list.AddDatum(new ArrayScroller.Datum { Tag = t });
            SetLanguageHash(0x9D974DF3, _unserved ? 0xC225D554u : 0x6A1151D1u);
            RefreshHeader();
        }

        void RefreshHeader()
        {
            CaseFileLine();
            Line(0x1FFFB988, 0x6031106E, ProfileName);
            Line(0x1FFFB989, _unserved ? 0xBDFE114Cu : 0xAD0B7F09u, RapStable.TotalNumInfractions(_unserved));
            Line(0x1FFFB98A, 0x364E4525, RapStable.TotalBounty());
            Line(0x1FFFB98B, _unserved ? 0x1FF24DD3u : 0x1E424873u, RapStable.TotalFines(_unserved));
            Line(0x2ECAFA80, _unserved ? 0x8422B22Au : 0x3177BB0Du, RapStable.TotalNumInfractions(_unserved));
            Line(0xBBE88932, _unserved ? 0x8422B22Au : 0x3177BB0Du, RapStable.TotalNumInfractions(_unserved));
            _list.RefreshHeader();
            for (int k = 0; k < _list.NumSlots; k++)
            {
                var d = _list.DatumAt(_list.Start + k);
                if (d == null) continue;
                int t = (int)d.Tag, num = RapStable.NumInfraction(t, _unserved), total = num + RapStable.NumInfraction(t, !_unserved);
                SetLanguageHash(HashF("INFRACTION_TYPE_" + (k + 1)), RapSheetRecord.Types[t].label);
                Printf(HashF("UNSERVED_QTY_" + (k + 1)), num.Grouped());
                Printf(HashF("TOTAL_QTY_" + (k + 1)), total.Grouped());
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.PadUp: _list.ScrollVer(false); RefreshHeader(); break;
                case FEHash.PadDown: _list.ScrollVer(true); RefreshHeader(); break;
                case FEHash.PadButton5: _unserved = !_unserved; Fill(); break;
                case FEHash.ExitComplete: SwitchTo(new RapSheetScreen()); break;
            }
        }
    }

    /// <summary>
    /// RapSheetCTS.fng — uiRapSheetCTS (ArrayScrollerMenu 1×9; PC ctor 0x52DA20, Setup 0x51E6E0, RefreshHeader 0x52DD80):
    /// TXT_QUANTITY_n "%$d", TXT_CATEGORY_n (label), TXT_CASH_n "%$d" from GetCareerCST in the RAP_CTS row order; header NAME,
    /// 0xE3DA78E8 BOUNTY, 0x358672CC COST TO STATE (career score). EXIT_COMPLETE → RapSheetMain.
    /// </summary>
    public sealed class RapSheetCostScreen : RapSheetPage
    {
        public override string Package => "RapSheetCTS";

        protected override void Setup()
        {
            var r = R;
            var list = new ArrayScroller(View, 1, 9);
            for (int i = 0; i < 9; i++) list.AddSlot(Obj(HashF("TXT_QUANTITY_" + (i + 1))));
            for (int i = 0; i < 9; i++) list.AddDatum(new ArrayScroller.Datum { Tag = i });
            CaseFileLine();
            Line(0xE3DA78E7, 0x6031106E, ProfileName);
            Line(0xE3DA78E8, 0x364E4525, RapStable.TotalBounty());
            Line(0x358672CC, 0xA355FEDD, r.CareerScore(RapSheetRecord.PD_CostToState));
            list.RefreshHeader();
            for (int i = 0; i < 9; i++)
            {
                var (q, v) = r.CareerCst(i);
                Printf(HashF("TXT_QUANTITY_" + (i + 1)), q.Grouped());
                SetLanguageHash(HashF("TXT_CATEGORY_" + (i + 1)), RapSheetRecord.CtsRows[i].label);
                Printf(HashF("TXT_CASH_" + (i + 1)), v.Grouped());
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg == FEHash.ExitComplete) SwitchTo(new RapSheetScreen());
        }
    }

    /// <summary>
    /// RapSheetTEP.fng — uiRapSheetTEP (UIWidgetMenu; PC ctor 0x546FC0, Setup 0x52E0C0, NotificationMessage 0x52DE40): the five
    /// top evaded pursuits: CAR_USED_n "CAR USED: %s" (0x69EAB50F), BOUNTY_n "BOUNTY: %$d" (0x060C058A), PURSUIT_ID_n "ID: %s"
    /// (0x41474FB1), PURSUIT_LENGTH_n "LENGTH: %s" (0x36175146, Timer::PrintToString flags 0); an empty slot disables BUTTON_n and
    /// reads "&lt; CLEAN RECORD &gt;" (0xE3274304); none → 0xEB5E7757 / 0x73DCB662 hidden. UP on BUTTON_1 focuses the last pursuit,
    /// DOWN on the last focuses BUTTON_1; accept (with pursuits) queues 0x587C018B; INIT_COMPLETE focuses BUTTON_&lt;last&gt;.
    /// EXIT_COMPLETE: BUTTON_1..5 → RapSheetPD (Arg n−1, last = n), else RapSheetMain (last 1).
    /// </summary>
    public sealed class RapSheetPursuitsScreen : RapSheetPage
    {
        public override string Package => "RapSheetTEP";
        int _num;
        uint _pressed;

        protected override void Setup()
        {
            var r = R;
            CaseFileLine();
            Line(0xE3DA78E7, 0x6031106E, ProfileName);
            Line(0xE3DA78E8, 0x364E4525, RapStable.TotalBounty());
            _num = 0;
            for (int i = 0; i < 5; i++)
            {
                int n = i + 1;
                var pr = i < r.TopPursuits.Count ? r.TopPursuits[i] : null;
                if (pr != null && RapSheetRecord.Ticks(pr.Length) != 0)
                {
                    Line(HashF("CAR_USED_" + n), 0x69EAB50F, CarName(pr.Car));
                    Line(HashF("BOUNTY_" + n), 0x060C058A, pr.Bounty);
                    Line(HashF("PURSUIT_ID_" + n), 0x41474FB1, pr.Id);
                    Line(HashF("PURSUIT_LENGTH_" + n), 0x36175146, GameHud.PrintTime(pr.Length, 0));
                    _num++;
                }
                else
                {
                    View.DisableButton(Obj(HashF("BUTTON_" + n)));
                    Line(HashF("CAR_USED_" + n), 0xE3274304);
                    Printf(HashF("BOUNTY_" + n), "");
                    Printf(HashF("PURSUIT_ID_" + n), "");
                    Printf(HashF("PURSUIT_LENGTH_" + n), "");
                }
            }
            if (_num == 0) { SetInvisible(0xEB5E7757); SetInvisible(0x73DCB662); }
            else FocusLast();
        }

        void FocusLast()
        {
            int b = FEDb.LastButton(Package);
            if (b == 0) b = 1;
            View.SetCurrentButton(HashF("BUTTON_" + b));
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            var cur = View.CurrentButton;
            switch (msg)
            {
                case FEHash.ButtonPressed: _pressed = obj?.Obj.Hash ?? 0; break;
                case 0x08B84A5C: _pressed = cur?.Obj.Hash ?? 0; break;
                case 0x7EABCA56: MouseClick(obj); break;
                case FEHash.PadAccept:
                    if (_num == 0) return;
                    View.PackageMessage(0x587C018B);
                    break;
                case FEHash.PadUp:
                    if (cur != null && cur.Obj.Hash == 0xCDA0A66B) View.SetCurrentButton(HashF("BUTTON_" + _num));
                    break;
                case FEHash.PadDown:
                    if (cur != null && cur.Obj.Hash == HashF("BUTTON_" + _num)) View.SetCurrentButton(0xCDA0A66B);
                    break;
                case FEHash.InitComplete:
                    if (_num != 0) FocusLast();
                    break;
                case FEHash.ExitComplete:
                {
                    int index = _pressed - 0xCDA0A66Bu < 5 ? (int)(_pressed - 0xCDA0A66Bu) : -1;
                    if (index >= 0) { FEDb.SetLastButton(Package, index + 1); SwitchTo(new RapSheetPursuitScreen(index)); }
                    else { FEDb.SetLastButton(Package, 1); SwitchTo(new RapSheetScreen()); }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// RapSheetPD.fng — uiRapSheetPD (PC ctor 0x547050, Setup 0x52E450): CASE FILE, 0xEB406FEC NAME, 0xD91934E7 "PURSUIT ID: %s"
    /// (0xA656CD29); 0x9068C46D length (Timer::PrintToString 0), 6E cops involved, 6F damaged, 70 destroyed, 71 roadblocks
    /// dodged, 72 spike strips dodged, 73 cost to state, 74 infractions, 75 helicopters, 0x9D81523D bounty (all "%$d").
    /// EXIT_COMPLETE → RapSheetTEP.
    /// </summary>
    public sealed class RapSheetPursuitScreen : RapSheetPage
    {
        public override string Package => "RapSheetPD";
        readonly int _index;
        readonly PursuitRecord _p;
        public RapSheetPursuitScreen(int index) { _index = index; }
        public RapSheetPursuitScreen(PursuitRecord p) { _p = p; _index = -1; }

        protected override void Setup()
        {
            var p = _p ?? (_index >= 0 && _index < R.TopPursuits.Count ? R.TopPursuits[_index] : new PursuitRecord());
            CaseFileLine();
            Line(0xEB406FEC, 0x6031106E, ProfileName);
            Line(0xD91934E7, 0xA656CD29, p.Id ?? "");
            Printf(0x9068C46D, GameHud.PrintTime(p.Length, 0));
            int[] v = { p.CopsInvolved, p.CopsDamaged, p.CopsDestroyed, p.Roadblocks, p.Spikes, p.Cts, p.Infractions, p.Helicopters };
            for (int i = 0; i < v.Length; i++) Printf(0x9068C46Eu + (uint)i, v[i].Grouped());
            Printf(0x9D81523D, p.Bounty.Grouped());
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg == FEHash.ExitComplete) SwitchTo(new RapSheetPursuitsScreen());
        }
    }

    /// <summary>
    /// RapSheetRankings.fng — uiRapSheetRankings (PC ctor 0x550DC0, Setup 0x5470E0, PrintRanking 0x52E6C0, NotificationMessage
    /// 0x550E50): 0x7711109B..A3 + 0x5933242B = rank "%$d" (or "UNRANKED" 0xF3799455 for 16) of pursuit length, cops involved,
    /// damaged, destroyed, roadblocks, spike strips, cost to state, infractions, helicopters, bounty (buttons BUTTON_1..9,
    /// 0x81B573FB); 0x001E4FDA = "LIFETIME PURSUIT" / "SINGLE PURSUIT", 0x0DD2F4FB / 0x9AE9B5CD = the other view. The view is the
    /// static career_view (shared with the detail page); PAD_BUTTON5 toggles it. INIT_COMPLETE focuses the row whose type equals
    /// the last button (FEngSetLastButton stores the type — the decomp's noted quirk). EXIT_COMPLETE: a row → RankingsDetail
    /// (Arg = type), else RapSheetMain (last 0). PC mouse handling as RapSheetMain.
    /// </summary>
    public sealed class RapSheetRankingsScreen : RapSheetPage
    {
        public override string Package => "RapSheetRankings";
        /// <summary>uiRapSheetRankings::career_view (static, false at boot).</summary>
        public static bool CareerView;
        uint _pressed, _initButton;

        static readonly (uint rank, uint button, int type)[] Rows =
        {
            (0x7711109B, 0xCDA0A66B, RapSheetRecord.PD_Length), (0x7711109C, 0xCDA0A66C, RapSheetRecord.PD_CopsInvolved),
            (0x7711109D, 0xCDA0A66D, RapSheetRecord.PD_CopsDamaged), (0x7711109E, 0xCDA0A66E, RapSheetRecord.PD_CopsDestroyed),
            (0x7711109F, 0xCDA0A66F, RapSheetRecord.PD_Roadblocks), (0x771110A0, 0xCDA0A670, RapSheetRecord.PD_Spikes),
            (0x771110A1, 0xCDA0A671, RapSheetRecord.PD_CostToState), (0x771110A2, 0xCDA0A672, RapSheetRecord.PD_Infractions),
            (0x771110A3, 0xCDA0A673, RapSheetRecord.PD_Helicopters), (0x5933242B, 0x81B573FB, RapSheetRecord.PD_Bounty),
        };

        protected override void Setup()
        {
            foreach (var (rank, button, type) in Rows)
            {
                int r = R.CalcPursuitRank(type, CareerView);
                Printf(rank, r != 16 ? r.Grouped() : Lang(0xF3799455));
                if (type == FEDb.LastButton(Package)) _initButton = button;
            }
            CaseFileLine();
            Line(0xEB406FEC, 0x6031106E, ProfileName);
            SetLanguageHash(0x001E4FDA, CareerView ? 0x96DDF504u : 0x56E940F4u);
            SetLanguageHash(0x0DD2F4FB, CareerView ? 0x554BBDB5u : 0xA88B3FC5u);
            SetLanguageHash(0x9AE9B5CD, CareerView ? 0x554BBDB5u : 0xA88B3FC5u);
            if (_initButton != 0) View.SetCurrentButton(_initButton);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.ButtonPressed: _pressed = obj?.Obj.Hash ?? 0; break;
                case 0x08B84A5C: _pressed = View.CurrentButton?.Obj.Hash ?? 0; break;
                case 0x7EABCA56: MouseClick(obj); break;
                case FEHash.PadButton5: CareerView = !CareerView; Setup(); break;
                case FEHash.InitComplete: if (_initButton != 0) View.SetCurrentButton(_initButton); break;
                case FEHash.ExitComplete:
                {
                    int type = -1;
                    foreach (var row in Rows) if (row.button == _pressed) type = row.type;
                    if (type >= 0)
                    {
                        RapSheetRankingsDetailScreen.CareerView = CareerView;
                        FEDb.SetLastButton(Package, type);
                        SwitchTo(new RapSheetRankingsDetailScreen(type));
                    }
                    else { FEDb.SetLastButton(Package, 0); SwitchTo(new RapSheetScreen()); }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// RapSheetRankingsDetail.fng — uiRapSheetRankingsDetail (ArrayScrollerMenu 1×10; PC ctor 0x5471B0, Setup 0x52E750,
    /// RefreshHeader 0x52ED00, UpdateHighlight 0x51EE90; slots 0x51ECA0 / timer 0x51ED90). The frontend record's 15 ranks with
    /// BLACKLIST_RIVAL_%.2d_AKA of NameId[i] (the car _CAR only in the single-pursuit view), the player inserted at his rank
    /// (16 rows when unranked; name = profile, car = his best pursuit's in the single view). Slots: RANK_n "%$d" (unranked:
    /// "--" 0x0FC1BF40 / timer slot "--"), PLAYER_NAME_n, VEHICLE_n, TIME_n "%$.0f" (pursuit length: Timer(seconds) flags 16).
    /// SetInitialPosition(0) then ScrollDown while player rank − 10 + 4 &gt; 0; the player's row on screen → package POS%d, else
    /// 0x58B123F7. 0x8224E17C = category ("PURSUIT LENGTH" 0xD70811D1 …). PAD_BUTTON5 toggles the view; EXIT_COMPLETE →
    /// RapSheetRankings (carrying the view).
    /// </summary>
    public sealed class RapSheetRankingsDetailScreen : RapSheetPage
    {
        public override string Package => "RapSheetRankingsDetail";
        public static bool CareerView;
        readonly int _type;
        int _playerRank = 16;
        ArrayScroller _list;

        sealed class Row { public int Rank; public bool Player; public uint Name, Car; public string PlayerCar; public float Value; }

        static readonly uint[] Category = { 0xD70811D1, 0xC6113FCF, 0x2A1815D9, 0x189EAF7B, 0xDCD6B9BA, 0x9EF589BE, 0x39A1413C, 0xE34B2E6F, 0xB3F963F8, 0x48B4B99C };

        public RapSheetRankingsDetailScreen(int type) { _type = type; }

        protected override void Setup()
        {
            _list = new ArrayScroller(View, 1, 10);
            for (int i = 0; i < 10; i++) _list.AddSlot(Obj(HashF("RANK_" + (i + 1))));
            Fill();
        }

        void Fill()
        {
            _list.ClearData();
            var r = R;
            _playerRank = r.CalcPursuitRank(_type, CareerView);
            var rec = RapSheetRecord.RankRecord(_type, CareerView);
            var ranks = RapSheetRecord.Ranks(rec);
            if (ranks != null && ranks.Length == 15)
            {
                int offset = 0, shown = _playerRank == 16 ? 16 : 15;
                for (int i = 0; i < shown; i++)
                {
                    if (i == _playerRank - 1)
                    {
                        int v = CareerView ? r.CareerScore(_type) : r.BestValue[_type];
                        _list.AddDatum(new ArrayScroller.Datum
                        {
                            Tag = new Row
                            {
                                Rank = _playerRank, Player = true, PlayerCar = CareerView ? null : r.BestCar[_type],
                                Value = _type == RapSheetRecord.PD_Length ? RapSheetRecord.Seconds(v) : v,
                            },
                        });
                        offset--;
                    }
                    else
                    {
                        int id = RapSheetRecord.NameId(rec, i + offset);
                        _list.AddDatum(new ArrayScroller.Datum
                        {
                            Tag = new Row
                            {
                                Rank = i + 1, Name = HashF($"BLACKLIST_RIVAL_{id:00}_AKA"),
                                Car = CareerView ? 0u : HashF($"BLACKLIST_RIVAL_{id:00}_CAR"), Value = ranks[i + offset],
                            },
                        });
                    }
                }
                for (int d = _playerRank - 10 + 4; d > 0; d--) _list.ScrollVer(true);
            }
            SetLanguageHash(0x8224E17C, _type >= 0 && _type < Category.Length ? Category[_type] : 0u);
            UpdateHighlight();
            RefreshHeader();
        }

        void RefreshHeader()
        {
            CaseFileLine();
            SetLanguageHash(0x001E4FDA, CareerView ? 0x96DDF504u : 0x56E940F4u);
            SetLanguageHash(0x0DD2F4FB, CareerView ? 0x554BBDB5u : 0xA88B3FC5u);
            SetLanguageHash(0x9AE9B5CD, CareerView ? 0x554BBDB5u : 0xA88B3FC5u);
            _list.RefreshHeader();
            bool timer = _type == RapSheetRecord.PD_Length;
            for (int k = 0; k < _list.NumSlots; k++)
            {
                var d = _list.DatumAt(_list.Start + k);
                if (d == null) continue;
                var row = (Row)d.Tag;
                int n = k + 1;
                uint rank = HashF("RANK_" + n), name = HashF("PLAYER_NAME_" + n), car = HashF("VEHICLE_" + n), val = HashF("TIME_" + n);
                if (timer)
                {
                    Printf(rank, row.Rank != 16 ? row.Rank.Grouped() : "--");
                    Printf(val, GameHud.PrintTime(row.Value, 16));
                }
                else
                {
                    Printf(val, Localize.Fixed(row.Value, 0, true));
                    if (row.Rank != 16) Printf(rank, row.Rank.Grouped()); else SetLanguageHash(rank, 0x0FC1BF40);
                }
                if (row.Car != 0) SetLanguageHash(car, row.Car);
                else Printf(car, row.PlayerCar != null ? CarName(row.PlayerCar) : "");
                if (!row.Player) SetLanguageHash(name, row.Name); else Printf(name, ProfileName);
            }
        }

        /// <summary>UpdateHighlight (0x51EE90).</summary>
        void UpdateHighlight()
        {
            int pos = _playerRank - _list.Start;
            View.PackageMessage(pos > 0 && pos <= _list.NumSlots ? HashF("POS" + pos) : 0x58B123F7);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.PadUp: _list.ScrollVer(false); RefreshHeader(); UpdateHighlight(); break;
                case FEHash.PadDown: _list.ScrollVer(true); RefreshHeader(); UpdateHighlight(); break;
                case FEHash.InitComplete: UpdateHighlight(); break;
                case FEHash.PadButton5: CareerView = !CareerView; Fill(); break;
                case FEHash.ExitComplete:
                    RapSheetRankingsScreen.CareerView = CareerView;
                    SwitchTo(new RapSheetRankingsScreen());
                    break;
            }
        }
    }

    /// <summary>
    /// RapSheetVD.fng — uiRapSheetVD (ArrayScrollerMenu 1×2; PC ctor 0x546A50, Setup 0x52D640, RefreshHeader 0x52D8F0): per
    /// career car (filter 0xF0002) with its record: CAR_n (name), BOUNTY_n 0xDFC1111B, FINES_n 0x092D727E (unserved fine value),
    /// UNSERVED_n 0x71DAD325, TO_DRIVE_n "&lt; VEHICLE IMPOUNDED &gt;" 0x35E4E01F / "&lt; CONFIRMED TO DRIVE &gt;" 0x2089554C (busted &gt; 0) /
    /// "&lt; SUSPECTED TO DRIVE &gt;" 0xD3EFE2E5, EVADED_n 0x49F29E04, BUSTED_n 0x42EB2E82; unused slots "&lt; no record &gt;" 0x73AF0386
    /// with blank lines. Header NAME, BOUNTY, 0x1FFFB98A "PREFERRED VEHICLE: %s" (GetPreferedCarName, else &lt; no record &gt;),
    /// 0x1FFFB98B "RECENT VEHICLE: %s" (previously pursued car). EXIT_COMPLETE → RapSheetMain.
    /// </summary>
    public sealed class RapSheetVehiclesScreen : RapSheetPage
    {
        public override string Package => "RapSheetVD";
        ArrayScroller _list;

        protected override void Setup()
        {
            _list = new ArrayScroller(View, 1, 2);
            for (int i = 0; i < 2; i++) _list.AddSlot(Obj(HashF("CAR_" + (i + 1))));
            var p = P;
            int num = 0;
            if (p != null)
                foreach (var c in p.Cars)
                {
                    if (FEImpound.Record(c, false) == null) continue;
                    _list.AddDatum(new ArrayScroller.Datum { Tag = c });
                    num++;
                }
            for (int i = num; i < 2; i++)
            {
                int n = i + 1;
                Line(HashF("CAR_" + n), 0x73AF0386);
                foreach (var f in new[] { "BOUNTY_", "FINES_", "UNSERVED_", "TO_DRIVE_", "EVADED_", "BUSTED_" }) Printf(HashF(f + n), "");
            }
            RefreshHeader();
        }

        void RefreshHeader()
        {
            CaseFileLine();
            Line(0x1FFFB988, 0x6031106E, ProfileName);
            Line(0x1FFFB989, 0x364E4525, RapStable.TotalBounty());
            string pref = RapStable.PreferredCar(), recent = R.RecentCar;
            Line(0x1FFFB98A, 0xFBAF89DF, pref != null ? CarName(pref) : Lang(0x73AF0386));
            Line(0x1FFFB98B, 0x074A86E1, !string.IsNullOrEmpty(recent) ? CarName(recent) : Lang(0x73AF0386));
            _list.RefreshHeader();
            for (int k = 0; k < _list.NumSlots; k++)
            {
                var d = _list.DatumAt(_list.Start + k);
                if (d == null) continue;
                var car = (OwnedCar)d.Tag;
                var rec = FEImpound.Record(car, false);
                int n = k + 1;
                uint status = rec.IsImpounded ? 0x35E4E01Fu : rec.NumBustedPursuits != 0 ? 0x2089554Cu : 0xD3EFE2E5u;
                Printf(HashF("CAR_" + n), CarName(car.Car));
                Line(HashF("BOUNTY_" + n), 0xDFC1111B, rec.Bounty);
                Line(HashF("FINES_" + n), 0x092D727E, FEImpound.FineValue(car, true));
                Line(HashF("UNSERVED_" + n), 0x71DAD325, RapStable.NumInfractionsOnCar(car, true));
                Printf(HashF("TO_DRIVE_" + n), Lang(status));
                Line(HashF("EVADED_" + n), 0x49F29E04, rec.NumEvadedPursuits);
                Line(HashF("BUSTED_" + n), 0x42EB2E82, rec.NumBustedPursuits);
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.PadUp: _list.ScrollVer(false); RefreshHeader(); break;
                case FEHash.PadDown: _list.ScrollVer(true); RefreshHeader(); break;
                case FEHash.ExitComplete: SwitchTo(new RapSheetScreen()); break;
            }
        }
    }

    /// <summary>
    /// Infractions.fng — PostPursuitInfractionsScreen (career/uiInfractions.cpp; PC Create 0x595E20). Ctor (:17): first bust ever
    /// (CareerSettings HasBeenBustedOnce) → 4 Get Out of Jail markers when none are owned, the help dialog later; the BUSTED stamp
    /// 0x2347122A hidden, its texture by language (English 0xB419EC5F) shown once loaded. 0xBD66334A "%$d" this pursuit's fine
    /// (FEInfractionsData of GInfractionManager's infractions), 0xB967F64D "%d" its count, 0xA4C79522 the car's other unserved
    /// fines (record unserved value − this), 0x5344F2A6 their count, 0x5B875870 / 0xEA8AECD9 "%d" markers; no marker → GREY on
    /// 0xF9363F30, 0x6B6973C1, 0x39F11E5C and Button1 0xB8A7C6CC disabled, else 0x39F11E5C NORMAL; 0x854AF1F4 "%$d" amount to pay
    /// (the car's unserved fine value), 0x1930B057 "%$d" cash. INIT_COMPLETE: first bust → focus 0xB8A7C6CC + one-button dialog
    /// 0x9C14B5F1, else focus 0xB8A7C6CD. BUTTON_PRESSED: Button2 0xB8A7C6CD pays cash (SpendCash, impound NotifyBusted → strike
    /// limit, ServeAllIncractions, heat 1); Button1 uses a marker (count redrawn, GREY at 0) and WaiveIncractions(this pursuit's).
    /// Strike limit → BecomeImpounded(strike limit) + dialog 0x78F0E298; cash path without enough cash → BecomeImpounded
    /// (insufficient funds) + 0x1ECFFA6E; their OK (0x34DC1BEC) → Car_Select.fng Arg 0x200; otherwise Car_Select Arg 0 (marker) /
    /// 0x100 (cash). Port: Car_Select = the career CarSelectScreen over the safehouse crib (GameFlow.JumpToSafeHouse) [?: the
    /// 0x100 / 0x200 args — the busted manager's strike animation — are not passed]. The pursuit also goes on the rap sheet.
    /// </summary>
    public sealed class InfractionsScreen : FEMenuScreen
    {
        public override string Package => "Infractions";
        const uint Button1 = 0xB8A7C6CC, Button2 = 0xB8A7C6CD, MsgOk = 0x34DC1BEC;
        readonly PursuitStats _stats;
        readonly int _heat;
        bool _firstTimeBusted, _strikeLimitReached, _done;
        int _amountToPay, _amountPlayerHas;
        uint _bits;

        public InfractionsScreen(PursuitStats stats, int heat) { _stats = stats; _heat = heat; }

        static int Markers => ShopMarkers.Count(ShopRules.M_JailFree);

        /// <summary>CalcBustedTexture (uiInfractions.cpp:104): BUSTED_&lt;language&gt; stamp hashes.</summary>
        static uint BustedTexture() => Localize.CurrentLanguage switch
        {
            Language.French => 0xB419F122, Language.German => 0xB419F3C3, Language.Italian => 0xB419FE23, Language.Spanish => 0xB41A2829,
            Language.Dutch => 0xB419E912, Language.Swedish => 0xB41A2914, Language.Danish => 0xB419E678, Language.Polish => 0xB41A1B50,
            Language.Finnish => 0xB419F002, Language.Korean => 0xB41A0611, Language.Chinese => 0xB419E319, Language.Japanese => 0xB41A0000,
            Language.Thai => 0xB41A2B62, _ => 0xB419EC5F,
        };

        /// <summary>FEInfractionsData::GetFineValue of a kInfraction_* bit set (one of each).</summary>
        static int FineOf(uint bits)
        {
            // attributes `infractions` keys in kInfraction bit order (FEImpound.InfractionKeys: speeding, racing, reckless_driving,
            // assault, hit_and_run, damage, resisting_arrest, off_road)
            var counts = new int[8];
            for (int i = 0; i < 8; i++) if ((bits & (1u << i)) != 0) counts[i] = 1;
            return FEImpound.FineValue(counts);
        }

        static int Count(uint bits) { int n = 0; for (; bits != 0; bits &= bits - 1) n++; return n; }

        protected override void Setup()
        {
            var p = GameSession.Profile;
            var r = p.RapSheet ??= new RapSheetRecord();
            RapSheetRecord.Forget();
            if (!r.BustedBefore)
            {
                if (Markers < 1) for (int i = 0; i < 4; i++) ShopMarkers.Add(ShopRules.M_JailFree);
                _firstTimeBusted = true;
            }
            r.BustedBefore = true;
            r.Commit(_stats, true, p.Car?.Car);   // UserProfile::CommitPursuitInfo (busted: the previous car only)
            var car = p.Car;
            SetInvisible(0x2347122A);
            SetTextureHash(0x2347122A, BustedTexture());
            SetVisible(0x2347122A);   // NotifyBustedTextureLoaded: the port's textures are resident
            _bits = RapSheetRecord.BitsOf(_stats);
            int thisCost = FineOf(_bits), thisNum = Count(_bits);
            Printf(0xBD66334A, thisCost.Grouped());
            Printf(0xB967F64D, thisNum.ToString());
            int unservedValue = FEImpound.FineValue(car, true);
            Printf(0xA4C79522, (unservedValue - thisCost).Grouped());
            Printf(0x5344F2A6, (RapStable.NumInfractionsOnCar(car, true) - thisNum).ToString());
            Printf(0x5B875870, Markers.ToString());
            Printf(0xEA8AECD9, Markers.ToString());
            if (Markers == 0)
            {
                SetScript(0xF9363F30, FEHash.Grey);
                SetScript(0x6B6973C1, FEHash.Grey);
                SetScript(0x39F11E5C, FEHash.Grey);
                View.DisableButton(Obj(Button1));
            }
            else SetScript(0x39F11E5C, K.Normal);
            _amountToPay = unservedValue;
            Printf(0x854AF1F4, _amountToPay.Grouped());
            _amountPlayerHas = p.Cash;
            Printf(0x1930B057, _amountPlayerHas.Grouped());
            p.Save();
        }

        /// <summary>
        /// [ours] CareerStable.OnPursuitBusted (Runtime/Game/Gameplay, the gameplay owner's) already ran FEImpound.NotifyBusted /
        /// BecomeImpounded(strike limit) as a stand-in for this screen; in the exe the strike belongs to the pay-cash branch only
        /// (a marker bust costs no strike). The stand-in strike is taken back here so the screen applies it 1:1. Remove when the
        /// gameplay owner drops those calls (hook noted in the report).
        /// </summary>
        static void UndoStandInStrike(OwnedCar car)
        {
            var rec = FEImpound.Record(car, false);
            if (rec == null) return;
            if (rec.ImpoundedState == FECareerCarRecord.ImpoundStrikeLimit) rec.ImpoundedState = FECareerCarRecord.ImpoundNone;
            if (rec.TimesBusted > 0) rec.TimesBusted--;
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.InitComplete:
                    if (_firstTimeBusted)
                    {
                        View.SetCurrentButton(Button1);
                        DialogInterface.ShowOneButton(this, "", DialogTitle.Alert, FEHash.LabelOk, DialogInterface.MsgCancelled, DialogInterface.MsgCancelled, true, Lang(0x9C14B5F1));
                    }
                    else View.SetCurrentButton(Button2);
                    break;
                case FEHash.ButtonPressed:
                {
                    if (_done || obj == null) break;
                    var p = GameSession.Profile;
                    var car = p.Car;
                    bool cash = obj.Obj.Hash == Button2, marker = obj.Obj.Hash == Button1;
                    if (!cash && !marker) break;
                    _done = true;
                    if (cash)
                    {
                        p.Cash = Mathf.Max(0, p.Cash - _amountToPay);   // CareerSettings::SpendCash
                        _strikeLimitReached = FEImpound.NotifyBusted(car);
                        FEImpound.ServeAllInfractions(car);
                        if (car != null) CarParts.SetHeat(car, 1f);
                    }
                    else
                    {
                        ShopMarkers.UseJailFree();
                        int n = Markers;
                        Printf(0x5B875870, n.ToString());
                        Printf(0xEA8AECD9, n.ToString());
                        if (n <= 0) { SetScript(0x6B6973C1, FEHash.Grey); SetScript(0x39F11E5C, FEHash.Grey); }
                        var rec = FEImpound.Record(car, false);   // FECareerRecord::WaiveIncractions(this pursuit's)
                        if (rec != null) for (int i = 0; i < 8; i++) if ((_bits & (1u << i)) != 0 && rec.Unserved[i] > 0) rec.Unserved[i]--;
                    }
                    bool notEnoughCash = !marker && _amountToPay > _amountPlayerHas;
                    uint message = 0;
                    int reason = FECareerCarRecord.ImpoundNone;
                    if (_strikeLimitReached) { message = 0x78F0E298; reason = FECareerCarRecord.ImpoundStrikeLimit; }
                    else if (notEnoughCash) { message = 0x1ECFFA6E; reason = FECareerCarRecord.ImpoundInsufficientFunds; }
                    p.Save();
                    if (message != 0)
                    {
                        FEImpound.BecomeImpounded(car, reason);
                        p.Save();
                        DialogInterface.ShowOneButton(this, "", DialogTitle.Alert, FEHash.LabelOk, MsgOk, MsgOk, false, Lang(message));
                    }
                    else ToCarSelect();
                    break;
                }
                case MsgOk:
                    ToCarSelect();
                    break;
            }
        }

        /// <summary>QueuePackageSwitch("Car_Select.fng", …): the career car select in the safehouse.</summary>
        void ToCarSelect()
        {
            FE.Defer(() =>
            {
                if (GameSession.Mode != GameMode.Career) { FE.CloseAll(); return; }
                GameFlow.JumpToSafeHouse();
                FrontEnd.Create().Push(new CarSelectScreen(CarSelectScreen.Mode.Career));
            });
        }
    }
}
