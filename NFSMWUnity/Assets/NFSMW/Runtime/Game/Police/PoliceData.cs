using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// One heat row of <c>pursuitlevels</c> (attributes.bin heat_01..heat_06; specs/pursuit.md §2, values [D]).
    /// Distances are metres, times seconds, speeds as the field name says.
    /// </summary>
    public sealed class HeatRow
    {
        public (string type, int count, int chance)[] Cops;
        public float BustSpeedKmh, EvadeTimeout, HiddenZoneTimeMultiplier, FrontLOS, RearLOS, HeliLOS;
        public int BountyPer10s;
        public float DestroyCopBonusTime, TimeBetweenFirstFourSpawn, TimeBetweenCopSpawn;
        public int NumPatrolCars, FullEngagementCopCount, NumCopsToTriggerBackup;
        public float FullEngagementRadius, BackupCallTimer;
        public float RoadblockProbability, RoadblockSpikeChance, SearchModeRoadblockChance, SearchModeRoadblockRadius, SearchModeHeliSpawnChance;
        public float HeliFuelTime, TimeBetweenHeliActive;
        public float BoxinDuration, BoxinTightness, RollingBlockDuration, RollingBlockTightness;
        public float CollapseSpeedKmh, CollapseAggression, CollapseInnerRadius, CollapseOuterRadius;
        public int MaxCopsCollapsing;
        public float SpeedReactionTime, StaggerFormationTime;
        /// <summary>CopFormations records {FormationType, duration s, weight}.</summary>
        public (int type, float duration, float weight)[] Formations;
        /// <summary>911 (SoundAI::SyncPlayers, SoundAI.cpp:1620-1661): cost to state of property hit, or traffic smashes
        /// hit, outside a pursuit before dispatch reports a 911 call.</summary>
        public float CTSFor911;
        public int NumCiviHitsFor911;
        /// <summary>Seconds a 911 call stays active (Force911State → Set911CallTime).</summary>
        public float Lifetime911;
        /// <summary>The cop lockout in free roam after an evade / restart (LockoutCops, AICopManager.cpp:2245-2258).</summary>
        public float TimeInactiveFor911;
        /// <summary>Never read by the game (the hiding latch is a fixed 0.05 s, AIVehicle.cpp:2189-2196) [C].</summary>
        public float TimeToHideInZone;
        /// <summary>roadblockhelichance (0 on every PC row: no heli roadblocks).</summary>
        public float RoadblockHeliChance;
        /// <summary>TimePerHeatLevel x ScaleEscalationPerBucket(bin) = seconds per heat level (AIPursuit::OnTask,
        /// AIPursuit.cpp:1676-1699; the career bin 0..14, 14 outside the career / in a challenge race). The heat rows carry
        /// TimePerHeatLevel 1 and the seconds in the 15 bucket scales (attribute 80DEB840); the race rows 600..2000 s.</summary>
        public float TimePerHeatLevel = 1f;
        public float[] ScaleEscalationPerBucket = { 120, 120, 120, 120, 120, 120, 120, 120, 120, 120, 120, 180, 240, 360, 600 };
        /// <summary>Seconds per heat level for a career bin (TimePerHeatLevel x ScaleEscalationPerBucket[min(bin, 14)]).</summary>
        public float SecondsPerHeat(int bin) => Mathf.Max(0.001f, TimePerHeatLevel * ScaleEscalationPerBucket[Mathf.Clamp(bin, 0, ScaleEscalationPerBucket.Length - 1)]);
        /// <summary>MeterDeadZoneBustedDistance / MeterDeadZoneEvadeDist (the pursuit meter, AIPursuit.cpp:2114-2140).</summary>
        public float MeterDeadZoneBustedDistance = 35f, MeterDeadZoneEvadeDist = 50f;
        /// <summary>The attributes.bin collection this row came from (heat_NN for the built-in heat rows).</summary>
        public string Source;

        /// <summary>
        /// A pursuitlevels row read from attributes.bin (Generated/AttribSys/Classes/pursuitlevels.h accessor names; the
        /// `cops` CopSpawnRecord is {RefSpec aivehicle (12), int count, int chance}, CopFormations {int type, float
        /// duration, float frequency}). Fields a row leaves unset keep the fallback's values (the class defaults live in
        /// `default`, which every row inherits from; harness `aiprobe racetables` lists the rows).
        /// </summary>
        public static HeatRow FromAttributes(AttribDatabase db, AttribCollection c, HeatRow fallback)
        {
            var f = fallback ?? PoliceData.HeatFor(1);
            var r = new HeatRow
            {
                Source = c.Name ?? c.Key.ToString("X8"),
                Cops = f.Cops, Formations = f.Formations,
                BustSpeedKmh = c.Float("BustSpeed", 0, f.BustSpeedKmh), EvadeTimeout = c.Float("evadetimeout", 0, f.EvadeTimeout),
                HiddenZoneTimeMultiplier = c.Float("HiddenZoneTimeMultiplier", 0, f.HiddenZoneTimeMultiplier),
                FrontLOS = c.Float("frontLOSdistance", 0, f.FrontLOS), RearLOS = c.Float("rearLOSdistance", 0, f.RearLOS), HeliLOS = c.Float("heliLOSdistance", 0, f.HeliLOS),
                BountyPer10s = c.Int("RepPointsPerMinute", 0, f.BountyPer10s), DestroyCopBonusTime = c.Float("DestroyCopBonusTime", 0, f.DestroyCopBonusTime),
                TimeBetweenFirstFourSpawn = c.Float("TimeBetweenFirstFourSpawn", 0, f.TimeBetweenFirstFourSpawn), TimeBetweenCopSpawn = c.Float("TimeBetweenCopSpawn", 0, f.TimeBetweenCopSpawn),
                NumPatrolCars = c.Int("NumPatrolCars", 0, f.NumPatrolCars), FullEngagementCopCount = c.Int("FullEngagementCopCount", 0, f.FullEngagementCopCount),
                NumCopsToTriggerBackup = c.Int("NumCopsToTriggerBackup", 0, f.NumCopsToTriggerBackup), FullEngagementRadius = c.Float("FullEngagementRadius", 0, f.FullEngagementRadius),
                BackupCallTimer = c.Float("BackupCallTimer", 0, f.BackupCallTimer), RoadblockProbability = c.Float("roadblockprobability", 0, f.RoadblockProbability),
                RoadblockSpikeChance = c.Float("roadblockspikechance", 0, f.RoadblockSpikeChance), SearchModeRoadblockChance = c.Float("SearchModeRoadblockChance", 0, f.SearchModeRoadblockChance),
                SearchModeRoadblockRadius = c.Float("SearchModeRoadblockRadius", 0, f.SearchModeRoadblockRadius), SearchModeHeliSpawnChance = c.Float("SearchModeHeliSpawnChance", 0, f.SearchModeHeliSpawnChance),
                HeliFuelTime = c.Float("HeliFuelTime", 0, f.HeliFuelTime), TimeBetweenHeliActive = c.Float("TimeBetweenHeliActive", 0, f.TimeBetweenHeliActive),
                BoxinDuration = c.Float("BoxinDuration", 0, f.BoxinDuration), BoxinTightness = c.Float("BoxinTightness", 0, f.BoxinTightness),
                RollingBlockDuration = c.Float("RollingBlockDuration", 0, f.RollingBlockDuration), RollingBlockTightness = c.Float("RollingBlockTightness", 0, f.RollingBlockTightness),
                CollapseSpeedKmh = c.Float("CollapseSpeed", 0, f.CollapseSpeedKmh), CollapseAggression = c.Float("CollapseAggression", 0, f.CollapseAggression),
                CollapseInnerRadius = c.Has("CollapseInnerRadius") ? c.Int("CollapseInnerRadius") : f.CollapseInnerRadius,   // Int32 in the class
                CollapseOuterRadius = c.Float("CollapseOuterRadius", 0, f.CollapseOuterRadius), MaxCopsCollapsing = c.Int("MaxCopsCollapsing", 0, f.MaxCopsCollapsing),
                SpeedReactionTime = c.Float("SpeedReactionTime", 0, f.SpeedReactionTime), StaggerFormationTime = c.Float("StaggerFormationTime", 0, f.StaggerFormationTime),
                CTSFor911 = c.Has("CTSFor911") ? c.Int("CTSFor911") : f.CTSFor911, NumCiviHitsFor911 = c.TryGet("NumCiviHitsFor911", out int at8, out _, out _) ? c.Source.Data[at8] : f.NumCiviHitsFor911,
                Lifetime911 = c.Float("Lifetime911", 0, f.Lifetime911), TimeInactiveFor911 = c.Float("TimeInactiveFor911", 0, f.TimeInactiveFor911),
                TimeToHideInZone = c.Float("TimeToHideInZone", 0, f.TimeToHideInZone), RoadblockHeliChance = c.Float("roadblockhelichance", 0, f.RoadblockHeliChance),
                TimePerHeatLevel = c.Float("TimePerHeatLevel", 0, f.TimePerHeatLevel),
                MeterDeadZoneBustedDistance = c.Float("MeterDeadZoneBustedDistance", 0, f.MeterDeadZoneBustedDistance), MeterDeadZoneEvadeDist = c.Float("MeterDeadZoneEvadeDist", 0, f.MeterDeadZoneEvadeDist),
            };
            var scales = c.Floats("ScaleEscalationPerBucket");
            if (scales.Length >= 15) r.ScaleEscalationPerBucket = scales;
            if (c.TryGet("cops", out int at, out int count, out var def) && count > 0)
            {
                int stride = def.Size >= 20 ? def.Size : 20;
                var list = new List<(string, int, int)>();
                var d = c.Source.Data;
                for (int i = 0; i < count; i++)
                {
                    int b = at + i * stride;
                    uint cls = Chunks.U32(d, b), key = Chunks.U32(d, b + 4);
                    string name = db.Collections.TryGetValue((cls, key), out var ai) ? ai.Name : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add((name, Chunks.I32(d, b + 12), Chunks.I32(d, b + 16)));
                }
                if (list.Count > 0) r.Cops = list.ToArray();
            }
            if (c.TryGet("CopFormations", out int fat, out int fcount, out var fdef) && fcount > 0)
            {
                int stride = fdef.Size >= 12 ? fdef.Size : 12;
                var d = c.Source.Data;
                var list = new List<(int, float, float)>();
                for (int i = 0; i < fcount; i++)
                {
                    int b = fat + i * stride;
                    list.Add((Chunks.I32(d, b), Chunks.F32(d, b + 4), Chunks.F32(d, b + 8)));
                }
                r.Formations = list.ToArray();
            }
            return r;
        }
    }

    /// <summary>One row of <c>pursuitsupport</c> (support01..support06; specs/pursuit.md §3, ai_cops.md §0, 2.10).</summary>
    public sealed class SupportRow
    {
        public float MinimumSupportDelay;
        /// <summary>AirSupport {AirSupportStrategy, chance, duration}: only SKID_HIT (2) is read by the game.</summary>
        public (int strategy, int chance, float duration)[] Air;
        /// <summary>HeavySupport {HeavySupportStrategy 1 E_BRAKE 2 COORD_E_BRAKE 3 RAM 4 HEAVY_ROADBLOCK, chance, duration, ChanceBigSUV}.</summary>
        public (int strategy, int chance, float duration, int chanceBigSuv)[] Heavy;
        /// <summary>LeaderSupport {5 CROSS_FOLLOW 6 CROSS_BRAKE 7 CROSS_PLUS_V_BLOCK, chance, duration, PriorityChance, PriorityTime}.</summary>
        public (int strategy, int chance, float duration, int priorityChance, float priorityTime)[] Leader;
    }

    /// <summary>aivehicle numbers for one cop type (specs/pursuit.md §6, [D]); the model folder is our mapping.</summary>
    public sealed class CopType
    {
        public string Name, Model, Display;
        public float TopSpeedMultiplier, AccelerationMultiplier, MaxAiSpeedKmh;
        public int Bounty;
        /// <summary>aivehicle.RepPointsForDestroying[10] by pursuit level (AIPursuit::IncNumCopsDestroyed reads index
        /// mCurrentPursuitLevel); flat per type in the PC data except copsport (100000 at level 10; `aiprobe racetables`).</summary>
        public int[] RepPoints;
        public int BountyAt(int level) => RepPoints != null && RepPoints.Length > 0 ? RepPoints[Mathf.Clamp(level, 0, RepPoints.Length - 1)] : Bounty;
        /// <summary>aivehicle.TETHER_WEIGHT (percent; AIActionPursuitOffRoad.cpp:427): 0 in every cop aivehicle of the PC data
        /// (harness `aiprobe cops`), so the offset follows the road (roadoff) only; read from attributes.bin.</summary>
        public float TetherWeight = 0f;
        internal bool Loaded;
        public bool IsHeli => Name == "copheli";
        /// <summary>Spawned as a special unit (AIPursuit::OnAttached counts these as support).</summary>
        public bool IsSpecial => Name == "copcross" || Name == "copsuv" || Name == "copsuvl";
    }

    public enum ElementType { None, Car, Barrier, Spikes }

    public struct RoadblockElement
    {
        public ElementType Type;
        public float X, Z, AngleRev;
        public RoadblockElement(ElementType t, float x, float z, float a) { Type = t; X = x; Z = z; AngleRev = a; }
    }

    public sealed class RoadblockSetup
    {
        public float MinWidth;
        public int Cars;
        public RoadblockElement[] Elements;
    }

    public static class PoliceData
    {
        // FormationType (AI/aireflectedtypes.h:16)
        public const int PIT = 1, BOX_IN = 2, ROLLING_BLOCK = 3, FOLLOW = 4, HELI_PURSUIT = 5, HERD = 6, STAGGER_FOLLOW = 7;

        public static float Kph(float k) => k / 3.6f;
        public static float Mph(float m) => m * 0.44704f;

        /// <summary>pursuitlevels/default: SearchModeCityMPH / SearchModeHwyMPH are read from it (AIActionTraffic.cpp:184-196).</summary>
        public const float SearchModeCityMph = 50f, SearchModeHwyMph = 71f;

        public static readonly HeatRow[] Heat =
        {
            Row(new[] { ("copmidsize", 4, 100) }, 9, 20, 4, 151, 151, 251, 100, 40, 10, 1, 5, 2, 180, 0, 0, 0, 1000, 0, 10, 0, 4, .5f, 4, .5f, 58, .4f, 6, 15, 4, 1.75f, 7,
                new[] { (3, 40f, 100f) }),
            Row(new[] { ("copghost", 5, 100) }, 12, 45, 5, 151, 151, 251, 500, 30, 10, 1, 10, 2, 120, 10, 0, 3, 1000, 0, 10, 0, 5, .6f, 5, .6f, 68, .5f, 5, 15, 5, 1.5f, 6,
                new[] { (2, 40f, 100f) }),
            Row(new[] { ("copgto", 7, 100) }, 19, 75, 6, 151, 151, 251, 1000, 20, 5, 1, 15, 2, 120, 25, 0, 5, 2000, 0, 10, 120, 6, .8f, 6, .8f, 78, .6f, 4, 8, 6, .75f, 5,
                new[] { (2, 40f, 25f), (1, 40f, 60f), (3, 40f, 20f), (6, 40f, 40f) }),
            Row(new[] { ("copgtoghost", 8, 100), ("copheli", 1, 50) }, 20, 90, 7, 151, 151, 251, 5000, 15, 5, 1, 20, 2, 120, 30, 70, 7, 5000, 2, 60, 180, 7, .9f, 7, .9f, 88, .7f, 3, 5, 7, .5f, 4,
                new[] { (1, 40f, 50f), (2, 40f, 50f), (6, 40f, 30f), (3, 40f, 50f) }),
            Row(new[] { ("copsporthench", 10, 50), ("copheli", 1, 60) }, 25, 120, 8, 151, 151, 251, 25000, 10, 2, 1, 25, 2, 90, 40, 80, 10, 10000, 4, 90, 180, 8, 1, 8, 1, 96, .8f, 3, 5, 8, .1f, 3,
                new[] { (3, 40f, 50f), (2, 40f, 50f), (1, 40f, 50f), (6, 40f, 50f) }),
            Row(new[] { ("copsportghost", 8, 80), ("copheli", 1, 50), ("copsuvpatrol", 2, 80) }, 25, 120, 8, 151, 151, 251, 30000, 10, 5, 1, 30, 2, 90, 50, 85, 12, 10000, 5, 180, 180, 10, 1, 10, 1, 120, .8f, 3, 7, 8, .1f, 5,
                new[] { (3, 40f, 60f), (2, 40f, 50f), (6, 40f, 50f) }),
        };

        static HeatRow Row((string, int, int)[] cops, float bust, float evade, float hidden, float front, float rear, float heli, int bounty,
            float firstFour, float spawnGap, int patrol, int fullCount, int backupAt, float backupTimer, float rb, float spikes, float searchRb,
            float searchRbRadius, float searchHeli, float heliFuel, float heliGap, float boxDur, float boxTight, float rollDur, float rollTight,
            float collapseKmh, float collapseAggr, float inner, float outer, int maxCollapse, float reaction, float stagger, (int, float, float)[] formations) =>
            new HeatRow
            {
                Cops = cops, BustSpeedKmh = bust, EvadeTimeout = evade, HiddenZoneTimeMultiplier = hidden, FrontLOS = front, RearLOS = rear, HeliLOS = heli,
                BountyPer10s = bounty, DestroyCopBonusTime = 10f, TimeBetweenFirstFourSpawn = firstFour, TimeBetweenCopSpawn = spawnGap,
                NumPatrolCars = patrol, FullEngagementCopCount = fullCount, FullEngagementRadius = 50f, NumCopsToTriggerBackup = backupAt,
                BackupCallTimer = backupTimer, RoadblockProbability = rb, RoadblockSpikeChance = spikes, SearchModeRoadblockChance = searchRb,
                SearchModeRoadblockRadius = searchRbRadius, SearchModeHeliSpawnChance = searchHeli, HeliFuelTime = heliFuel,
                TimeBetweenHeliActive = heliGap, BoxinDuration = boxDur, BoxinTightness = boxTight, RollingBlockDuration = rollDur,
                RollingBlockTightness = rollTight, CollapseSpeedKmh = collapseKmh, CollapseAggression = collapseAggr, CollapseInnerRadius = inner,
                CollapseOuterRadius = outer, MaxCopsCollapsing = maxCollapse, SpeedReactionTime = reaction, StaggerFormationTime = stagger,
                Formations = formations,
            };

        public static readonly SupportRow[] Support =
        {
            new SupportRow { MinimumSupportDelay = 60, Air = new[] { (1, 0, 60f) }, Heavy = new[] { (3, 0, 0f, 0) }, Leader = new[] { (5, 0, 0f, 0, 0f) } },
            new SupportRow { MinimumSupportDelay = 60, Air = new[] { (1, 0, 60f) }, Heavy = new[] { (3, 0, 20f, 0) }, Leader = new (int, int, float, int, float)[0] },
            new SupportRow { MinimumSupportDelay = 60, Air = new[] { (1, 0, 0f) }, Heavy = new[] { (4, 5, 40f, 0), (3, 10, 20f, 0) }, Leader = new (int, int, float, int, float)[0] },
            new SupportRow { MinimumSupportDelay = 45, Air = new[] { (1, 50, 60f) }, Heavy = new[] { (3, 25, 30f, 100), (4, 25, 20f, 100) }, Leader = new (int, int, float, int, float)[0] },
            new SupportRow { MinimumSupportDelay = 35, Air = new[] { (2, 60, 90f) }, Heavy = new[] { (3, 25, 30f, 100), (4, 40, 20f, 100) }, Leader = new[] { (5, 5, 2000f, 10, 120f) } },
            new SupportRow { MinimumSupportDelay = 10, Air = new[] { (2, 100, 180f) }, Heavy = new[] { (3, 20, 30f, 100) }, Leader = new[] { (5, 5, 2000f, 100, 60f) } },
        };

        // pursuitlevels heat_01..06 [D] (pursuit.md §2): CTSFor911, NumCiviHitsFor911, Lifetime911, TimeInactiveFor911, TimeToHideInZone
        static readonly float[] Cts911 = { 400, 600, 800, 1000, 1500, 1750 }, Life911 = { 45, 45, 45, 45, 45, 180 }, Hide = { 1, 1, 1, 1, 1, 3 };
        static readonly int[] Civi911 = { 3, 4, 5, 6, 7, 5 };

        // pursuitlevels heat_01..06 attribute 80DEB840[15] (ScaleEscalationPerBucket): seconds per heat level by career
        // progress (rivals beaten 0..14), with TimePerHeatLevel 1 on the heat rows [D]
        static readonly float[][] SecondsPerHeat =
        {
            new float[] { 120,120,120,120,120,120,120,120,120,120,120,180,240,360,600 },
            new float[] { 120,120,120,120,120,120,120,180,240,240,360,480,600,600,600 },
            new float[] { 120,120,120,120,120,180,240,300,600,600,600,600,600,600,600 },
            new float[] { 120,240,360,480,600,600,600,600,600,600,600,600,600,600,600 },
            new float[] { 600,600,600,600,600,600,600,600,600,600,600,600,600,600,600 },
            new float[] { 600,600,600,600,600,600,600,600,600,600,600,600,600,600,600 },
        };

        static PoliceData()
        {
            for (int i = 0; i < Heat.Length; i++)
            {
                var r = Heat[i];
                r.Source = "heat_" + (i + 1).ToString("00");
                r.TimePerHeatLevel = 1f;
                r.ScaleEscalationPerBucket = SecondsPerHeat[i];
                r.CTSFor911 = Cts911[i];
                r.NumCiviHitsFor911 = Civi911[i];
                r.Lifetime911 = Life911[i];
                r.TimeInactiveFor911 = 120f;
                r.TimeToHideInZone = Hide[i];
                r.RoadblockHeliChance = 0f;
            }
        }

        public static HeatRow HeatFor(int heat) => Heat[Mathf.Clamp(heat, 1, Heat.Length) - 1];
        public static SupportRow SupportFor(int heat) => Support[Mathf.Clamp(heat, 1, Support.Length) - 1];

        static HeatRow[] _raceRows;

        /// <summary>
        /// pursuitescalation/default `racetable` (10 pursuitlevels rows): the heat rows of a pursuit during a race, i.e. in
        /// play mode Racing when the race is not a pursuit race (AIPerpVehicle::SetHeat, AIVehicle.cpp:2257-2297, PC
        /// `race_01..10`). Read from attributes.bin; falls back to the heat rows when absent.
        /// </summary>
        public static HeatRow[] RaceRows
        {
            get
            {
                if (_raceRows != null) return _raceRows;
                var rows = new List<HeatRow>();
                try
                {
                    var db = GameData.Attributes;
                    var esc = db?.Get("pursuitescalation", "default");
                    if (esc != null)
                    {
                        var keys = esc.Keys("racetable");
                        uint cls = AttribDatabase.Hash("pursuitlevels");
                        for (int i = 0; i < keys.Length; i++)
                        {
                            if (!db.Collections.TryGetValue((cls, keys[i]), out var c)) break;
                            var row = HeatRow.FromAttributes(db, c, HeatFor(i + 1));
                            row.Source = "race_" + (i + 1).ToString("00");
                            rows.Add(row);
                        }
                    }
                }
                catch (System.Exception e) { Debug.LogWarning("NFSMW police: race tables: " + e.Message); }
                _raceRows = rows.Count > 0 ? rows.ToArray() : Heat;
                return _raceRows;
            }
        }

        /// <summary>The row for a heat level: the race table while racing (not in a pursuit race), else the heat table.</summary>
        public static HeatRow RowFor(int heat, bool racing)
        {
            var rows = racing ? RaceRows : Heat;
            return rows[Mathf.Clamp(heat, 1, rows.Length) - 1];
        }

        static readonly Dictionary<string, CopType> _types = new Dictionary<string, CopType>
        {
            ["copmidsize"] = T("copmidsize", "COPMIDSIZE", "Civic Cruiser", 1.6f, 1.05f, 280, 250),
            ["copghost"] = T("copghost", "COPGHOST", "Civic Undercover Cruiser", 1.15f, 1.09f, 310, 500),
            ["copgto"] = T("copgto", "COPGTO", "State Pursuit Vehicle", 1.2f, 1.1f, 325, 2500),
            ["copgtoghost"] = T("copgtoghost", "COPGTOGHOST", "State Undercover", 1.25f, 1.15f, 350, 5000),
            ["copsporthench"] = T("copsporthench", "COPSPORTHENCH", "Federal Pursuit Vehicle", 1.3f, 1.2f, 400, 20000),
            ["copsportghost"] = T("copsportghost", "COPSPORTGHOST", "Federal Undercover", 1.5f, 1.25f, 400, 25000),
            ["copsport"] = T("copsport", "COPSPORT", "Federal Pursuit Vehicle", 2f, 1.5f, 400, 20000),
            // copsuvpatrol and copcross have no CARS folder: the SUV and Cross's Corvette C6.R models stand in [?]
            ["copsuvpatrol"] = T("copsuvpatrol", "COPSUV", "SUV Patrol", 0.9f, 1.05f, 250, 25000),
            ["copsuv"] = T("copsuv", "COPSUV", "SUV Heavy", 0.5f, 1f, 50, 15000),
            ["copsuvl"] = T("copsuvl", "COPSUVL", "SUV Light", 0.5f, 1f, 50, 10000),
            ["copcross"] = T("copcross", "CORVETTEC6R", "Cross", 2f, 1.5f, 400, 100000),
            ["copheli"] = T("copheli", "COPHELI", "Helicopter", 1f, 1f, 400, 0),
        };

        static CopType T(string n, string model, string display, float top, float acc, float max, int bounty) =>
            new CopType { Name = n, Model = model, Display = display, TopSpeedMultiplier = top, AccelerationMultiplier = acc, MaxAiSpeedKmh = max, Bounty = bounty };

        /// <summary>The cop type by aivehicle name; the multipliers come from attributes.bin when present.</summary>
        public static CopType Type(string name)
        {
            if (!_types.TryGetValue(name, out var t)) t = _types["copmidsize"];
            if (!t.Loaded)
            {
                t.Loaded = true;
                var a = GameData.Attributes?.Get("aivehicle", t.Name);
                if (a != null)
                {
                    if (a.Has("TopSpeedMultiplier")) t.TopSpeedMultiplier = a.Float("TopSpeedMultiplier", 0, t.TopSpeedMultiplier);
                    if (a.Has("AccelerationMultiplier")) t.AccelerationMultiplier = a.Float("AccelerationMultiplier", 0, t.AccelerationMultiplier);
                    if (a.Has("MAXIMUM_AI_SPEED")) t.MaxAiSpeedKmh = a.Float("MAXIMUM_AI_SPEED", 0, t.MaxAiSpeedKmh);
                    if (a.Has("TETHER_WEIGHT")) t.TetherWeight = a.Float("TETHER_WEIGHT", 0, t.TetherWeight);
                    if (a.TryGet("RepPointsForDestroying", out int at, out int n, out _) && n > 0)
                    {
                        t.RepPoints = new int[n];
                        for (int i = 0; i < n; i++) t.RepPoints[i] = Chunks.I32(a.Source.Data, at + i * 4);
                    }
                }
            }
            return t;
        }

        // ---- roadblock setups (AI/Common/AIRoadBlockSetups.cpp; ai_cops.md §3.3) ----

        static RoadblockElement C(float x, float z, float a) => new RoadblockElement(ElementType.Car, x, z, a);
        static RoadblockElement B(float x, float z, float a) => new RoadblockElement(ElementType.Barrier, x, z, a);
        static RoadblockElement S(float x, float z, float a) => new RoadblockElement(ElementType.Spikes, x, z, a);
        static RoadblockSetup R(float w, int cars, params RoadblockElement[] e) => new RoadblockSetup { MinWidth = w, Cars = cars, Elements = e };

        public static readonly RoadblockSetup[] Roadblocks =
        {
            R(8.0f, 2, C(-3, 0, .75f), C(2, -.35f, .27f)),
            R(12.0f, 3, C(-5.2f, -2, .875f), C(0, -2, .12f), C(4.5f, -1, .87f), B(-2.9f, .2f, .48f)),
            R(5.5f, 2, C(-2, 0, .26f), C(2, 5, .48f), B(1.5f, 1.7f, .61f)),
            R(6.5f, 2, C(-2, 0, .75f), C(3, 4, .25f), B(1.5f, 1.7f, .61f)),
            R(8.5f, 2, C(-.9f, 0, .72f), C(4, 0, .32f), B(-4.8f, -.8f, .54f)),
            R(15.0f, 4, C(-5, 0, .71f), C(1, 0, .29f), C(5, 6, .24f), C(8, 2, .5f), B(4.1f, -1.4f, .57f), B(6.1f, -1.4f, .43f)),
            R(13.0f, 3, C(-4, 1, .26f), C(-.9f, -4.2f, .75f), C(5, -4, .26f), B(-3.3f, -2.2f, .4f), B(-5, -4, .46f)),
            R(11.4f, 3, C(-4.4f, -1.2f, .65f), C(0, 2.4f, .24f), C(4.4f, -1.2f, .35f), B(0, -2.7f, .49f), B(2.1f, 0, .35f), B(-2.1f, 0, .65f)),
            R(12.1f, 3, C(-4.5f, 0, .75f), C(1, 0, .25f), C(4, 4.5f, .72f), B(4.3f, -1.2f, .48f), B(4.9f, .3f, .04f)),
            R(10.0f, 4, C(-4.5f, 0, .27f), C(-.3f, -4, .75f), C(4.2f, -4, .24f), C(4, 0, .26f), B(-4.9f, -5, .51f), B(-4.2f, -3.3f, .53f)),
            R(14.0f, 4, C(-4.5f, 0, .25f), C(1, 0, .75f), C(0, 5.4f, .25f), C(5.3f, 5.4f, .22f), B(-2, 2.2f, .37f), B(5.4f, -.2f, .53f)),
            R(12.0f, 4, C(-4.3f, 0, .74f), C(-5, 4, .75f), C(.2f, 4, .27f), C(4, 0, .25f), B(-.7f, -1, .355f), B(.7f, -1, .635f)),
            R(22.0f, 4, C(-9, 0, .74f), C(-3, 0, .25f), C(3, 0, .75f), C(9, 0, .25f), B(0, -3, .5f)),
            R(15.0f, 3, C(-5.9f, 0, .74f), C(0, 0, .25f), C(5.9f, 0, .75f), B(3, -2, .51f)),
            R(28.0f, 5, C(-12, 0, .74f), C(-6, 0, .25f), C(0, 0, .75f), C(6, 0, .75f), C(12, 0, .25f), B(-3, -3.4f, .5f)),
        };

        public static readonly RoadblockSetup[] SpikeRoadblocks =
        {
            R(8.0f, 1, C(-3, 0, .75f), S(2, -.35f, .52f)),
            R(15.0f, 3, S(-5, 0, .51f), C(1, 0, .29f), C(5, 6, .24f), C(8, 2, .5f), B(4.1f, -1.4f, .57f), B(6.1f, -1.4f, .43f)),
            R(15.0f, 2, C(-4, 1, .26f), C(-.9f, -4.2f, .75f), S(5, -4, .52f), B(-3.3f, -2.2f, .4f), B(-5, -4, .46f)),
            R(14.1f, 2, S(-4.8f, 0, .5f), C(1, 0, .25f), C(4, 4.5f, .72f), B(4.3f, -1.2f, .48f), B(4.9f, .3f, .04f)),
            R(12.0f, 3, C(-4, 0, .27f), S(-.3f, -4.4f, .5f), C(4.8f, -4, .24f), C(4, 0, .26f), B(-4.9f, -5, .51f), B(-4.2f, -3.3f, .53f)),
            R(13.0f, 3, S(-4, 0, .51f), C(-5, 4, .75f), C(.2f, 4, .27f), C(4, 0, .25f), B(-.7f, -1, .355f), B(.7f, -1, .635f)),
            R(21.0f, 2, S(-9, 0, .48f), C(-3, 0, .25f), C(3, 0, .75f), S(9, 0, .52f), B(0, -3, .5f)),
            R(15.0f, 2, S(-5.2f, 0, .51f), C(0, 0, .25f), C(5.9f, 0, .75f), B(3, -2, .51f)),
            R(28.0f, 4, C(-12, 0, .74f), C(-6, 0, .25f), S(-1, 0, .51f), C(5, 0, .75f), C(11, 0, .25f)),
        };

        /// <summary>
        /// PickRoadblockSetup (AICopManager.cpp:1079): the setup with the smallest positive width − MinWidth that the
        /// gathered cars can fill.
        /// </summary>
        public static RoadblockSetup PickRoadblock(float width, int cars, bool spikes)
        {
            RoadblockSetup best = null;
            float bestScore = float.MaxValue;
            foreach (var r in spikes ? SpikeRoadblocks : Roadblocks)
            {
                float score = width - r.MinWidth;
                if (score > 0 && cars >= r.Cars && score < bestScore) { best = r; bestScore = score; }
            }
            return best;
        }

        /// <summary>UMath::RotateInXZ: rotate in the XZ plane by a fraction of a turn (same form in Unity axes).</summary>
        public static Vector3 RotateInXZ(float rev, Vector3 v)
        {
            float a = rev * 2f * Mathf.PI, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector3(v.x * c - v.z * s, v.y, v.x * s + v.z * c);
        }
    }
}
