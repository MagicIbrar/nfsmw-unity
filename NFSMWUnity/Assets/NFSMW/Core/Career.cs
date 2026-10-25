using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NfsMw.Core
{
    public enum RaceType { Circuit, Sprint, Knockout, Tollbooth, SpeedTrap, Drag, Pursuit, Other }

    public sealed class RacePoint
    {
        public string Name;
        public Vec3 Position;     // game space
        public float Rotation;    // heading, degrees
        public float Radius, Width;
        public int TimeBonus;     // tollbooth time_bonus_checkpoint: seconds added
        /// <summary>boxtrigger Dimensions (depth along the heading, width, height); zero when the record has none.</summary>
        public Vec3 Dims;
        /// <summary>speedtrap ThreshholdSpeed (km/h: 88 on race traps) and Bounty (open-world traps).</summary>
        public float Threshold;
        public int Bounty;
        /// <summary>resetplayertrigger ResetsPlayer (the `wrongway` plane behind the grid).</summary>
        public bool ResetsPlayer;
    }

    public sealed class RaceEvent
    {
        public uint Key;
        public string Name, EventId, Region, IntroNis, OutroNis, IntroMovie, OutroMovie, Icon;
        public RaceType Type;
        public int Laps, TrafficLevel, Difficulty, BinIndex;
        public float Cash, TimeLimit, Length;
        /// <summary>Speedtrap OvertimePenaltyPerSec: points lost per second by racers still out after the first finish.</summary>
        public float OvertimePenalty = 10f;
        public int Reputation;
        public bool CopsInRace, BossRace, Reversed, Knockout;
        public RacePoint Start, Finish;
        public readonly List<RacePoint> Checkpoints = new List<RacePoint>();
        public readonly List<RacePoint> SpeedTraps = new List<RacePoint>();
        /// <summary>`shortcut` markers: where the road is, and the AI's ShortcutMinChance / ShortcutMaxChance (0..1).</summary>
        public readonly List<(RacePoint point, float min, float max)> Shortcuts = new List<(RacePoint, float, float)>();
        public readonly List<uint> Opponents = new List<uint>();
        public readonly List<string> Barriers = new List<string>();
        // Challenge Series / pursuit events
        public bool ChallengeSeries, InitiallyUnlocked;
        public string PlayerCar;                      // preset ride (PlayerCarType), e.g. CS_CAR_01
        public string MilestoneType;                  // pursuit challenge stat (MilestoneName)
        public float Threshold, InitialSpeed;         // ThreshholdValue, InitialPlayerSpeed (mph)
        public int ForceHeat, BustedLives;
        public readonly List<RacePoint> CopSpawns = new List<RacePoint>();
        public readonly List<uint> UnlockRaces = new List<uint>();
        // story / scripted events (prologue): rolling start part way along the route, the next event to run after this
        // one (PostRaceActivity), a time card before it (IntroMessageID -> DDAY_TIMELAPSE_n), named trigger planes
        public bool RollingStart, DDayRace, NisShell;
        public float StartPercent;
        /// <summary>
        /// GRaceParameters accessors used by GRaceStatus (specs/gameplay_core.md §1-2): StartTime (the race clock's start
        /// value, 86 s on 16.1.0), KnockoutsPerLap, IsLoopingRace (circuits), CheckpointsVisible, RankPlayersByPoints,
        /// NoPostRaceScreen, IsEpicPursuitRace.
        /// </summary>
        public float StartTime;
        /// <summary>Gameplay "TOD" (0x9DFF3C3D): the time of day the race sets when it starts (0x5FD210 / 0x600AB0);
        /// -1 leaves the clock running. The D-Day intro and 8 other races use 1.0 (dusk), 16_1_1 uses 0.5.</summary>
        public float TimeOfDay = -1f;
        public int KnockoutsPerLap;
        public bool Looping, CheckpointsVisible = true, RankByPoints, NoPostRaceScreen, EpicPursuit;
        public uint PostRaceActivity;
        public int IntroMessageId = -1;
        /// <summary>
        /// GRaceParameters accessors and the GRaceIndexData fields (GenerateIndex 0x600CD0; specs/gameplay_core.md §9):
        /// GetRaceType 0x5FAA20 maps EventIconType through the table at 0x8F5CD8 (<see cref="RaceIndex"/>; -1 = none),
        /// GetRegion 0x5FAAB0 maps Region (college 0, coastal 1, city 2), GetCopDensity 0x5FD240 / GetDifficulty
        /// 0x5FAC10 bucket CopDensity / DifficultyLevel at 33 / 66.
        /// </summary>
        public int GRaceType = -1, RegionIndex = -1, CopDensityValue;
        public bool NeverInQuickRace, InitiallyUnlockedQuickRace, InitiallyUnlockedOnline, InitiallyUnlockedChallenge;
        public bool CollectorsEdition, ScriptedCopsInRace, RankByDistance, UseWorldHeatInRace, PursuitRace;
        public float RivalBestTime, MaxHeatLevel, MaxRaceHeatLevel;
        public string TrafficPattern, PhotoFinishCamera, PhotoFinishTexture;
        /// <summary>The race's engage trigger in its bin (bin_race_engage RaceTriggers: Position, Radius 25, InitiallyUnlocked);
        /// null for races reached only through a chain (later boss races) or not in a bin.</summary>
        public RacePoint Engage;
        public bool EngageInitiallyUnlocked;
        /// <summary>GetDifficulty (0x5FAC10): DifficultyLevel &lt; 34 easy (0), ≤ 66 medium (1), else hard (2).</summary>
        public int DifficultyTier => Difficulty < 34 ? 0 : Difficulty <= 66 ? 1 : 2;
        /// <summary>GetCopDensity (0x5FD240): CopDensity 0 → light (1) when CopsInRace else off (0); ≤ 33 light; ≤ 66 medium (2); else heavy (3).</summary>
        public int CopDensityTier => CopDensityValue == 0 ? (CopsInRace ? 1 : 0) : CopDensityValue <= 33 ? 1 : CopDensityValue <= 66 ? 2 : 3;
        /// <summary>GetIsSunsetRace / GetIsMiddayRace (index flags 0x2000 / 0x20000): TOD &gt; 0.8, or 0 ≤ TOD &lt; 0.8.</summary>
        public bool IsSunsetRace => TimeOfDay > 0.8f;
        public bool IsMiddayRace => TimeOfDay >= 0f && TimeOfDay < 0.8f;
        public readonly Dictionary<string, RacePoint> Triggers = new Dictionary<string, RacePoint>();
        public string DisplayName => EventId != null ? $"{TypeName} {EventId}" : Name;
        public string TypeName => Type switch
        {
            RaceType.Circuit => "Circuit", RaceType.Sprint => "Sprint", RaceType.Knockout => "Lap Knockout",
            RaceType.Tollbooth => "Tollbooth", RaceType.SpeedTrap => "Speedtrap", RaceType.Drag => "Drag", RaceType.Pursuit => "Pursuit", _ => "Race",
        };
    }

    /// <summary>
    /// A Blacklist challenge (gameplay.bin milestones/bin_NN/challenge_NN_type): MilestoneName is the stat, GoalEasy /
    /// GoalHard the target (pursuit_evasion_time is the one where smaller is better), Bounty the reward. All are
    /// pursuit stats and only count when the pursuit is evaded.
    /// </summary>
    public sealed class Milestone
    {
        public string Name, Type;
        public int Bin, Bounty;
        public float GoalEasy, GoalHard;
        public float Goal => GoalEasy;
        public bool BiggerIsBetter => Type != "pursuit_evasion_time";
        public bool Met(float value) => BiggerIsBetter ? value >= Goal : value > 0 && value <= Goal;

        public string Title => Type switch
        {
            "pursuit_length" => "Pursuit Length",
            "bounty_in_pursuit" => "Bounty in a Pursuit",
            "cost_to_state_in_pursuit" => "Cost to State",
            "cops_damaged" => "Cops Damaged",
            "total_infractions" => "Infractions",
            "roadblocks_dodged" => "Roadblocks Dodged",
            "tire_spikes_dodged" => "Spike Strips Dodged",
            "pursuit_evasion_time" => "Escape Within",
            _ => Type,
        };

        /// <summary>A stat value formatted for this milestone (times as m:ss, money with $).</summary>
        public string Format(float v) => Type switch
        {
            "pursuit_length" or "pursuit_evasion_time" => $"{(int)(v / 60)}:{(int)v % 60:00}",
            "bounty_in_pursuit" => ((int)v).ToString("N0"),
            "cost_to_state_in_pursuit" => "$" + ((int)v).ToString("N0"),
            _ => ((int)v).ToString(),
        };
    }

    public sealed class Racer
    {
        public uint Key;
        public string Name, PresetRide, CarType;
        public bool IsBoss;
        /// <summary>character SkillLevel (0..100, -1 = none) and the unnamed float B1ECE070, which AIActionRace's
        /// ComputePotentials reads as MinimumAIPerformance (specs/ai_racer.md §4.2).</summary>
        public int SkillLevel = -1;
        public float MinimumAIPerformance;
        /// <summary>RacerName (a language label; GRacerInfo::ChooseRacerName 0x5DD2A0) and the ForceStartPosition marker.</summary>
        public string RacerName;
        public RacePoint ForceStart;
    }

    /// <summary>
    /// An open-world speed trap (gameplay.bin speedtraps/bin_NN/speedtrapK; GSpeedTrap): passing it faster than
    /// ThreshholdSpeed (km/h) completes it once, pays its Bounty and counts as one of the bin's challenges
    /// (GRaceBin::RefreshProgress 0x6010E0).
    /// </summary>
    public sealed class WorldSpeedTrap
    {
        public string Name;
        public int Bin, Bounty;
        public float Threshold;
        public RacePoint Point;
    }

    public sealed class BlacklistBin
    {
        public int Index;                 // 15 (first rival) .. 1 (Razor); 16 = prologue
        public Racer Boss;
        public int RequiredBounty, RequiredRacesWon, RequiredChallenges;
        public int EntrySms, RivalSms;     // EntryCellCallID / SMSRivalChallenge (stategraph race_bin)
        /// <summary>GRaceBin::GetBaseOpenWorldHeat / GetMaxOpenWorldHeat / GetScaleOpenWorldHeat (0x5DCEE0 / 0x5DCF00 / 0x5DCF20): the
        /// pursuit heat range of the career while this bin is current (race_bin: 0 / 1 / 1; bins 15 1 / 2, 14-13 max 2, 12-9 max 3,
        /// 8-5 max 4, 4-1 max 5, 16 1 / 1).</summary>
        public float BaseOpenWorldHeat, MaxOpenWorldHeat = 1f, ScaleOpenWorldHeat = 1f;
        public readonly List<RaceEvent> WorldRaces = new List<RaceEvent>();
        public readonly List<RaceEvent> BossRaces = new List<RaceEvent>();
        public readonly List<string> Barriers = new List<string>();
        public readonly List<Milestone> Milestones = new List<Milestone>();
        public readonly List<WorldSpeedTrap> SpeedTraps = new List<WorldSpeedTrap>();
        /// <summary>race_bin CellChallengeRace / SMSCellChallenge (rival_locked NotifyChallengePassed: when the challenge that
        /// completes RequiredChallenges is passed the race is unlocked; only race_bin_15 sets it, to race_bin_01/1_2_2_sprint).</summary>
        public uint CellChallengeRace;
        public int SmsCellChallenge = -1;
        /// <summary>The bin's engage triggers (child `engage` RaceTriggers): unlock_races unlocks the targets of those with
        /// InitiallyUnlocked (every world race; the first boss race is unlocked at rival_unlocked).</summary>
        public readonly List<(RacePoint point, uint target, bool unlocked)> EngageTriggers = new List<(RacePoint, uint, bool)>();
    }

    /// <summary>
    /// GRaceDatabase / GRaceParameters constants and enums (specs/gameplay_core.md §9).
    /// </summary>
    public static class RaceIndex
    {
        /// <summary>GRace::Type.</summary>
        public const int P2P = 0, Circuit = 1, Drag = 2, Knockout = 3, Tollbooth = 4, SpeedTrap = 5, Checkpoint = 6, CashGrab = 7,
            Challenge = 8, JumpToSpeedTrap = 9, JumpToMilestone = 10;

        /// <summary>GetRaceType's table at 0x8F5CD8: EventIconType string → GRace::Type.</summary>
        public static int TypeOf(string eventIconType)
        {
            switch (eventIconType)
            {
                case "circuit": return Circuit;
                case "p2p": return P2P;
                case "drag": return Drag;
                case "knockout": return Knockout;
                case "tollbooth": return Tollbooth;
                case "speedtrap": return SpeedTrap;
                case "cashgrab": return CashGrab;
                case "checkpointrace": return Checkpoint;
                case "challenge": return Challenge;
                case "speedtrapjump": return JumpToSpeedTrap;
                case "milestonejump": return JumpToMilestone;
                default: return -1;
            }
        }

        /// <summary>GetRegion's table at 0x8F5D30.</summary>
        public static int RegionOf(string region) => region == "college" ? 0 : region == "coastal" ? 1 : region == "city" ? 2 : -1;

        /// <summary>sDDayRaces (0x8A37E4, 8-byte strings): GetNextDDayRace (0x600C30) returns the first not completed in career.
        /// DDay start "16.1.0", end "16.2.1"; final boss "1.2.3", final epic chase "1.8.1", Burger King "19.8.31".</summary>
        public static readonly string[] DDayRaces = { "16.1.0", "16.2.2", "16.2.3", "16.1.1", "16.2.1" };
        public const string DDayStart = "16.1.0", DDayEnd = "16.2.1", FinalBoss = "1.2.3", FinalEpicChase = "1.8.1", BurgerKing = "19.8.31";

        /// <summary>GRaceDatabase::ScoreFlags (GRaceSaveInfo.mFlags).</summary>
        public const int CompletedQuickRace = 1, CompletedCareer = 2, UnlockedQuickRace = 4, UnlockedCareer = 8, UnlockedOnline = 16;

        /// <summary>GRace::Context.</summary>
        public const int ContextQuickRace = 0, ContextOnline = 1, ContextCareer = 2;
    }

    /// <summary>A menu gate (gameplay.bin menu_gate_{safehouse,carlot,customshop}/zone_*): drive in and press Enter.</summary>
    public sealed class MenuZone
    {
        public string Name, Type;       // Type: safehouse, carlot, customshop
        public Vec3 Position;
        public float Radius;
        public int BinIndex;            // available once the career bin is at or below this
        public RacePoint Respawn;       // where the player comes back out
    }

    public sealed class CarLotEntry
    {
        public string Car;          // pvehicle / CARS folder name (lower case)
        public int Cost, UnlockedAt;
        public bool Customizable;
        public string Manufacturer;
    }

    /// <summary>
    /// Career data from GLOBAL\gameplay.bin (one VLT database per event, classes from attributes.bin) and FE_ATTRIB.bin:
    /// Blacklist bins race_bin_NN (Boss, RequiredBounty, RequiredRacesWon, RequiredChallenges, WorldRaces, BossRaces,
    /// Barriers), events (EventID, CashValue, NumLaps, Opponents, Checkpoint, startgrid/finishline children, Barriers),
    /// and the car lot (frontend.Cost, frontend.UnlockedAt).
    /// </summary>
    public sealed class CareerData
    {
        public AttribDatabase Db;
        public readonly SortedDictionary<int, BlacklistBin> Bins = new SortedDictionary<int, BlacklistBin>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        public readonly Dictionary<uint, RaceEvent> Events = new Dictionary<uint, RaceEvent>();
        public readonly Dictionary<uint, Racer> Racers = new Dictionary<uint, Racer>();
        public readonly List<CarLotEntry> CarLot = new List<CarLotEntry>();
        public readonly List<MenuZone> Zones = new List<MenuZone>();
        /// <summary>pvehicle records with RandomOpponent (Game_FindPerformanceCandidates 0x61D8E0 picks generic opponents' cars from these).</summary>
        public readonly List<string> RandomOpponents = new List<string>();

        /// <summary>Preset rides (GLOBALB chunk 0x30220, 0x290-byte records: car model[32] @+8, preset name[32] @+40).</summary>
        public readonly Dictionary<string, string> PresetCars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static CareerData Load(string gameRoot)
        {
            string g = Path.Combine(gameRoot, "GLOBAL");
            var data = new CareerData
            {
                Db = AttribDatabase.LoadAll(Path.Combine(g, "attributes.bin"), Path.Combine(g, "gameplay.bin"), Path.Combine(g, "FE_ATTRIB.bin")),
            };
            var globalB = Path.Combine(g, "GlobalB.lzc");
            if (File.Exists(globalB))
            {
                var d = GameFiles.ReadBundle(globalB);
                foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x30220))
                    for (int o = c.DataStart; o + 0x290 <= c.End; o += 0x290)
                    {
                        string car = Chunks.CString(d, o + 8, 32), preset = Chunks.CString(d, o + 40, 32);
                        if (car.Length > 0 && preset.Length > 0 && !data.PresetCars.ContainsKey(preset)) data.PresetCars[preset] = car.ToLowerInvariant();
                    }
            }
            data.Build();
            return data;
        }

        /// <summary>Car (CARS folder, lower case) a racer drives: its preset ride, else null.</summary>
        public string CarFor(Racer r) => r?.PresetRide != null && PresetCars.TryGetValue(r.PresetRide, out var car) ? car : r?.CarType?.ToLowerInvariant();

        AttribCollection Gp(uint key) => Db.Collections.TryGetValue((AttribDatabase.Hash("gameplay"), key), out var c) ? c : null;

        void Build()
        {
            foreach (var c in Db.OfClass("gameplay"))
            {
                string parent = c.Parent?.Name ?? "";
                if (c.Name == null || !parent.StartsWith("zone_") || !c.Has("ZoneType") || !c.Has("Position")) continue;
                var z = new MenuZone
                {
                    Name = c.Name, Type = c.Text("ZoneType"), Position = c.Vector3("Position"),
                    Radius = c.Has("Radius") ? c.Float("Radius") : 20f, BinIndex = c.Has("BinIndex") ? c.Int("BinIndex") : 16,
                };
                foreach (var k in c.Keys("RespawnMarker")) { var r = Gp(k); if (r != null) z.Respawn = PointFor(r); }
                if (z.Type == "safehouse" || z.Type == "carlot" || z.Type == "customshop") Zones.Add(z);
            }
            foreach (var c in Db.OfClass("gameplay"))
            {
                if (c.Parent?.Name != "race_bin" || c.Name == null || !c.Name.StartsWith("race_bin_")) continue;
                var bin = new BlacklistBin
                {
                    Index = c.Int("BinIndex"),
                    RequiredBounty = c.Int("RequiredBounty"),
                    RequiredRacesWon = c.Int("RequiredRacesWon"),
                    RequiredChallenges = c.Int("RequiredChallenges"),
                    EntrySms = c.Has("EntryCellCallID") ? c.Int("EntryCellCallID") : 0,
                    RivalSms = c.Has("SMSRivalChallenge") ? c.Int("SMSRivalChallenge") : 0,
                    BaseOpenWorldHeat = c.Has("BaseOpenWorldHeat") ? c.Float("BaseOpenWorldHeat") : 0f,
                    MaxOpenWorldHeat = c.Has("MaxOpenWorldHeat") ? c.Float("MaxOpenWorldHeat") : 1f,
                    ScaleOpenWorldHeat = c.Has("ScaleOpenWorldHeat") ? c.Float("ScaleOpenWorldHeat") : 1f,
                };
                var bossKeys = c.Keys("Boss");
                if (bossKeys.Length > 0) bin.Boss = RacerFor(bossKeys[0]);
                foreach (var k in c.Keys("WorldRaces")) { var e = EventFor(k, bin.Index); if (e != null) bin.WorldRaces.Add(e); }
                foreach (var k in c.Keys("BossRaces")) { var e = EventFor(k, bin.Index); if (e != null) { e.BossRace = true; bin.BossRaces.Add(e); } }
                bin.Barriers.AddRange(c.Texts("Barriers").Where(b => b != null));
                var cell = c.Keys("CellChallengeRace");
                if (cell.Length > 0) bin.CellChallengeRace = cell[0];
                if (c.Has("SMSCellChallenge")) bin.SmsCellChallenge = c.Int("SMSCellChallenge");
                // the bin's engage triggers (bin_race_engage RaceTriggers -> TargetActivity, InitiallyUnlocked)
                foreach (var k in c.Keys("Children"))
                {
                    var ch = Gp(k);
                    if (ch == null || !ch.Has("RaceTriggers")) continue;
                    foreach (var tk in ch.Keys("RaceTriggers"))
                    {
                        var t = Gp(tk);
                        if (t == null || !t.Has("Position")) continue;
                        var target = t.Keys("TargetActivity");
                        if (target.Length == 0 || target[0] == 0) continue;
                        var p = PointFor(t);
                        if (p.Radius <= 0) p.Radius = 25f;
                        bool unlocked = t.Bool("InitiallyUnlocked");
                        bin.EngageTriggers.Add((p, target[0], unlocked));
                        var ev = EventFor(target[0], bin.Index);
                        if (ev != null && ev.Engage == null) { ev.Engage = p; ev.EngageInitiallyUnlocked = unlocked; }
                    }
                }
                Bins[bin.Index] = bin;
            }

            foreach (var c in Db.OfClass("gameplay"))
            {
                if (c.Name == null || !c.Name.StartsWith("milestones/bin_") || !c.Has("MilestoneName")) continue;
                var m = new Milestone
                {
                    Name = c.Name, Type = c.Text("MilestoneName"), Bin = c.Int("BinIndex"), Bounty = c.Int("Bounty"),
                    GoalEasy = c.Float("GoalEasy"), GoalHard = c.Float("GoalHard"),
                };
                if (Bins.TryGetValue(m.Bin, out var b)) b.Milestones.Add(m);
            }

            foreach (var c in Db.OfClass("gameplay"))
            {
                if (c.Name == null || !c.Name.StartsWith("speedtraps/bin_") || !c.Has("ThreshholdSpeed") || !c.Has("Position")) continue;
                var t = new WorldSpeedTrap { Name = c.Name, Bin = c.Int("BinIndex"), Bounty = c.Int("Bounty"), Threshold = c.Float("ThreshholdSpeed"), Point = PointFor(c) };
                if (Bins.TryGetValue(t.Bin, out var b)) b.SpeedTraps.Add(t);
            }

            uint frontend = AttribDatabase.Hash("frontend");
            foreach (var pv in Db.OfClass("pvehicle"))
            {
                if (pv.Name != null && pv.Has("RandomOpponent") && pv.Bool("RandomOpponent")) RandomOpponents.Add(pv.Name);
                if (pv.Name == null || !Db.Collections.TryGetValue((frontend, pv.Key), out var fe)) continue;
                int cost = fe.Int("Cost");
                if (cost <= 5) continue;
                int unlock = fe.TryGet("UnlockedAt", out int at, out _, out _) ? fe.Source.Data[at] : 16;
                CarLot.Add(new CarLotEntry { Car = pv.Name, Cost = cost, UnlockedAt = unlock, Customizable = fe.Bool("IsCustomizable"), Manufacturer = fe.Parent?.Name });
            }
            CarLot.Sort((a, b) => b.UnlockedAt != a.UnlockedAt ? b.UnlockedAt.CompareTo(a.UnlockedAt) : a.Cost.CompareTo(b.Cost));
        }

        Racer RacerFor(uint key)
        {
            if (Racers.TryGetValue(key, out var r)) return r;
            var c = Gp(key);
            if (c == null) return null;
            string name = c.Name ?? key.ToString("X8");
            int slash = name.LastIndexOf('/');
            string shortName = slash >= 0 ? name.Substring(slash + 1) : name;
            r = new Racer
            {
                Key = key,
                Name = string.Join(" ", shortName.Split('_').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w)),
                PresetRide = c.Text("PresetRide"),
                CarType = c.Text("CarType"),
                IsBoss = c.Bool("IsBoss"),
                SkillLevel = c.Has("SkillLevel") ? c.Int("SkillLevel") : -1,
                MinimumAIPerformance = c.FloatKey(0xB1ECE070, 0f),
                RacerName = c.Has("RacerName") ? c.Text("RacerName") : null,
            };
            foreach (var k in c.Keys("ForceStartPosition")) { var m = Gp(k); if (m != null && m.Has("Position")) r.ForceStart = PointFor(m); }
            Racers[key] = r;
            return r;
        }

        List<RaceEvent> _raceActivities;

        /// <summary>
        /// GRaceDatabase::BuildRaceList / StoreRaceList (0x603970 / 0x603340): every gameplay collection that
        /// CollectionIsRaceActivity (0x5E36D0) accepts, i.e. not a Template and derived from the `race` template
        /// (0xD1E66B67), in collection order; each gets a GRaceParameters with a 0x30-byte GRaceIndexData (GenerateIndex).
        /// </summary>
        public IReadOnlyList<RaceEvent> RaceActivities()
        {
            if (_raceActivities != null) return _raceActivities;
            _raceActivities = new List<RaceEvent>();
            foreach (var c in Db.OfClass("gameplay"))
            {
                if (c.Name == null || (c.Has("Template") && c.Bool("Template"))) continue;
                bool race = false;
                for (var p = c.Parent; p != null && !race; p = p.Parent) race = p.Name == "race";
                if (!race) continue;
                var e = EventFor(c.Key, c.Has("BinIndex") ? c.Int("BinIndex") : 0);
                if (e != null) _raceActivities.Add(e);
            }
            return _raceActivities;
        }

        /// <summary>An event by its EventID ("16.1.0"); scans the gameplay records once.</summary>
        public RaceEvent EventById(string id)
        {
            foreach (var e in Events.Values) if (e.EventId == id) return e;
            foreach (var c in Db.OfClass("gameplay"))
                if (c.Has("EventID") && c.Text("EventID") == id) return EventFor(c.Key, c.Has("ProgressionLevel") ? c.Int("ProgressionLevel") : 0);
            return null;
        }

        /// <summary>A named gameplay.bin point (marker, zone, trigger) by collection name, e.g. "career_start_l2ra".</summary>
        public RacePoint Point(string name)
        {
            var c = Db.Get("gameplay", name);
            return c != null && c.Has("Position") ? PointFor(c) : null;
        }

        public RaceEvent EventFor(uint key, int bin = 0)
        {
            if (Events.TryGetValue(key, out var e)) return e;
            var c = Gp(key);
            if (c == null) return null;
            e = new RaceEvent
            {
                Key = key,
                Name = c.Name,
                EventId = c.Text("EventID"),
                Region = c.Text("Region"),
                IntroNis = c.Text("IntroNIS"),
                OutroNis = c.Text("OutroNIS"),
                IntroMovie = c.Text("IntroMovie"),
                OutroMovie = c.Text("OutroMovie"),
                Icon = c.Text("EventIconType"),
                Laps = Math.Max(1, c.Int("NumLaps", 0, 1)),
                Cash = c.Float("CashValue"),
                Reputation = c.Has("Reputation") ? c.Int("Reputation") : 0,
                TimeLimit = c.Float("TimeLimit"),
                OvertimePenalty = c.Has("OvertimePenaltyPerSec") ? c.Float("OvertimePenaltyPerSec") : 10f,
                Length = c.Float("RaceLength"),
                TrafficLevel = c.Int("TrafficLevel"),
                Difficulty = c.Int("DifficultyLevel"),
                CopsInRace = c.Bool("CopsInRace"),
                BinIndex = bin,
                Reversed = (c.Name ?? "").Contains("_r_") || (c.Name ?? "").EndsWith("reversed"),
                ChallengeSeries = c.Bool("ChallengeSeriesRace"),
                InitiallyUnlocked = c.Bool("InitiallyUnlocked"),
                PlayerCar = c.Text("PlayerCarType"),
                InitialSpeed = c.Float("InitialPlayerSpeed"),
                ForceHeat = c.Int("ForceHeatLevel"),
                BustedLives = c.Int("BustedLives"),
            };
            e.Type = TypeOf(c);
            // IsLoopingRace=False turns a circuit-parented event into a point-to-point race (the D-Day prologue)
            if (e.Type == RaceType.Circuit && c.Has("IsLoopingRace") && !c.Bool("IsLoopingRace")) e.Type = RaceType.Sprint;
            if (c.Has("KnockoutsPerLap") && c.Int("KnockoutsPerLap") > 0) e.Type = RaceType.Knockout;
            e.RollingStart = c.Has("RollingStart") && c.Bool("RollingStart");
            e.StartPercent = c.Has("StartPercent") ? c.Float("StartPercent") : 0;
            e.DDayRace = c.Has("DDayRace") && c.Bool("DDayRace");
            e.StartTime = c.Has("StartTime") ? c.Float("StartTime") : 0f;
            e.TimeOfDay = c.Has("TOD") ? c.Float("TOD") : -1f;
            e.KnockoutsPerLap = c.Has("KnockoutsPerLap") ? c.Int("KnockoutsPerLap") : 0;
            if (e.Type == RaceType.Knockout && e.KnockoutsPerLap <= 0) e.KnockoutsPerLap = 1;
            e.Looping = c.Has("IsLoopingRace") ? c.Bool("IsLoopingRace") : e.Type == RaceType.Circuit || e.Type == RaceType.Knockout;
            e.CheckpointsVisible = !c.Has("CheckpointsVisible") || c.Bool("CheckpointsVisible");
            e.RankByPoints = c.Has("RankPlayersByPoints") ? c.Bool("RankPlayersByPoints") : e.Type == RaceType.SpeedTrap;
            e.NoPostRaceScreen = c.Has("NoPostRaceScreen") && c.Bool("NoPostRaceScreen");
            e.EpicPursuit = c.Has("IsEpicPursuitRace") && c.Bool("IsEpicPursuitRace");
            e.NisShell = c.Has("NISShell") && c.Bool("NISShell");
            // GRaceParameters accessors / GenerateIndex fields (gameplay_core.md §9)
            e.GRaceType = RaceIndex.TypeOf(c.Text("EventIconType"));
            e.RegionIndex = RaceIndex.RegionOf(e.Region);
            e.CopDensityValue = c.Has("CopDensity") ? c.Int("CopDensity") : 0;
            e.NeverInQuickRace = c.Has("NeverInQuickRace") && c.Bool("NeverInQuickRace");
            e.InitiallyUnlockedQuickRace = c.Has("AvailableQR") && c.Bool("AvailableQR");                 // 0x5FBBD0 reads AvailableQR
            e.InitiallyUnlockedOnline = c.Has("AvailableOnline") && c.Bool("AvailableOnline");         // 0x5FBC40 reads AvailableOnline
            e.InitiallyUnlockedChallenge = c.Has("InitiallyUnlocked") && c.Bool("InitiallyUnlocked");   // 0x5FBCB0 reads InitiallyUnlocked
            e.CollectorsEdition = c.Has("CollectorsEditionRace") && c.Bool("CollectorsEditionRace");
            e.ScriptedCopsInRace = c.Has("ScriptedCopsInRace") && c.Bool("ScriptedCopsInRace");
            e.RankByDistance = !c.Has("RankPlayersByDistance") || c.Bool("RankPlayersByDistance");
            e.UseWorldHeatInRace = c.Has("UseWorldHeat") && c.Bool("UseWorldHeat");
            e.PursuitRace = c.Has("PursuitRace") && c.Bool("PursuitRace");
            e.RivalBestTime = c.Has("RivalBestTime") ? c.Float("RivalBestTime") : 0f;
            e.MaxHeatLevel = c.Has("MaxHeatLevel") ? c.Float("MaxHeatLevel") : 0f;
            e.MaxRaceHeatLevel = e.MaxHeatLevel;   // GetMaxRaceHeatLevel (0x5FC440) reads MaxHeatLevel too
            e.TrafficPattern = c.Has("TrafficPattern") ? c.Text("TrafficPattern") : null;
            e.PhotoFinishCamera = c.Has("FinishCamera") ? c.Text("FinishCamera") : null;
            e.PhotoFinishTexture = null;   // GetPhotoFinishTexture (0x5FCC60) reads 0x038A3B53, name not resolved
            if (c.Has("IntroMessageID")) e.IntroMessageId = c.Int("IntroMessageID");
            if (c.Has("PostRaceActivity")) { var pk = c.Keys("PostRaceActivity"); if (pk.Length > 0) e.PostRaceActivity = pk[0]; }
            if (c.Bool("PursuitRace"))
            {
                e.Type = RaceType.Pursuit;
                e.MilestoneType = c.Text("MilestoneName");
                e.Threshold = c.Float("ThreshholdValue");
            }
            foreach (var k in c.Keys("CopSpawnPoints")) { var ch = Gp(k); if (ch != null) e.CopSpawns.Add(PointFor(ch)); }
            foreach (var k in c.Keys("UnlockRaces")) e.UnlockRaces.Add(k);
            foreach (var k in c.Keys("racestart")) { var ch = Gp(k); if (ch != null && e.Start == null) e.Start = PointFor(ch); }
            foreach (var k in c.Keys("racefinish")) { var ch = Gp(k); if (ch != null && e.Finish == null) e.Finish = PointFor(ch); }
            foreach (var k in c.Keys("Opponents")) { e.Opponents.Add(k); RacerFor(k); }
            e.Barriers.AddRange(c.Texts("Barriers").Where(b => b != null));
            foreach (var k in c.Keys("Children"))
            {
                var ch = Gp(k);
                if (ch == null) continue;
                string parent = ch.Parent?.Name ?? "";
                var p = PointFor(ch);
                if (parent == "startgrid") e.Start = p;
                else if (parent == "finishline") e.Finish = p;
                else if (parent == "speedtrap") { if (!e.SpeedTraps.Any(t => t.Name == p.Name)) e.SpeedTraps.Add(p); }
                else if (parent == "shortcut")
                {
                    float Chance(string f, float fb) { float v = ch.Has(f) ? ch.Float(f) : fb; return v > 1f ? v * 0.01f : v; }
                    e.Shortcuts.Add((p, Chance("ShortcutMinChance", 0f), Chance("ShortcutMaxChance", 1f)));
                }
                else if (parent.Contains("trigger") && ch.Name != null) e.Triggers[ch.Name.Substring(ch.Name.LastIndexOf('/') + 1)] = p;
            }
            foreach (var k in c.Keys("Checkpoint")) { var ch = Gp(k); if (ch != null) e.Checkpoints.Add(PointFor(ch)); }
            // SpeedTrapList is the script's trap order; the same traps are also children of the event (keep one of each)
            var listed = new List<RacePoint>();
            foreach (var k in c.Keys("SpeedTrapList")) { var ch = Gp(k); if (ch != null && listed.All(t => t.Name != ch.Name)) listed.Add(PointFor(ch)); }
            if (listed.Count > 0) { e.SpeedTraps.Clear(); e.SpeedTraps.AddRange(listed); }
            Events[key] = e;
            return e;
        }

        static RacePoint PointFor(AttribCollection c) => new RacePoint
        {
            Name = c.Name,
            Position = c.Vector3("Position"),
            Rotation = c.Float("Rotation"),
            Radius = c.Float("Radius"),
            Width = c.Float("Width"),
            TimeBonus = c.Has("TimeBonus") ? c.Int("TimeBonus") : 0,
            Dims = c.Has("Dimensions") ? c.Vector3("Dimensions") : default,
            Threshold = c.Has("ThreshholdSpeed") ? c.Float("ThreshholdSpeed") : 0f,
            Bounty = c.Has("Bounty") ? c.Int("Bounty") : 0,
            ResetsPlayer = c.Has("ResetsPlayer") && c.Bool("ResetsPlayer"),
        };

        /// <summary>The car of a preset ride (GlobalB), e.g. a Challenge Series CS_CAR_nn.</summary>
        public string CarForPreset(string preset) => preset != null && PresetCars.TryGetValue(preset, out var car) ? car : null;

        static RaceType TypeOf(AttribCollection c)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
            {
                switch (p.Name)
                {
                    case "circuit": return (c.Name ?? "").Contains("knockout") ? RaceType.Knockout : RaceType.Circuit;
                    case "knockout": return RaceType.Knockout;
                    case "p2p": case "sprint": return RaceType.Sprint;
                    case "tollboothrace": return RaceType.Tollbooth;
                    case "speedtraprace": return RaceType.SpeedTrap;
                    case "drag": return RaceType.Drag;
                }
            }
            string n = c.Name ?? "";
            if (n.Contains("knockout")) return RaceType.Knockout;
            if (n.Contains("circuit")) return RaceType.Circuit;
            if (n.Contains("sprint")) return RaceType.Sprint;
            if (n.Contains("drag")) return RaceType.Drag;
            if (n.Contains("tollbooth")) return RaceType.Tollbooth;
            if (n.Contains("speedtrap")) return RaceType.SpeedTrap;
            return RaceType.Other;
        }
    }
}
