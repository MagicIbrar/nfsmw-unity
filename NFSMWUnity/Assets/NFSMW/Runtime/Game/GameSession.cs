using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    public enum GameMode { FrontEnd, Career, QuickRace, FreeRoam, ChallengeSeries }

    /// <summary>What the player is doing now: mode, profile and the career data shared by menus and gameplay.</summary>
    public static class GameSession
    {
        public static GameMode Mode = GameMode.FrontEnd;
        public static Profile Profile;
        public static CareerData Career { get; private set; }
        /// <summary>Car chosen for free roam / quick race when not in career.</summary>
        public static OwnedCar QuickCar = new OwnedCar { Car = "bmwm3gtr" };
        /// <summary>Split-screen races: player two's car (keyboard arrows / second gamepad).</summary>
        public static bool SplitScreen;
        /// <summary>The LAN session while hosting or joined (null otherwise).</summary>
        public static LanSession Lan;

        public static void EndLan()
        {
            Lan?.Dispose();
            Lan = null;
        }
        public static OwnedCar QuickCar2 = new OwnedCar { Car = "gallardo", Paint = new Color(0.9f, 0.55f, 0.05f) };

        public static void Load()
        {
            GameData.Mount();
            Career ??= CareerData.Load(GameData.Root);
            Profile ??= Profile.Load();
            // cFrontendDatabase::IsDDay (0x50E480, inlined at its users): the career's CurrentBin is above 15 (bin 16 =
            // the prologue, through the car lot and the drive to the safehouse)
            MoviePlayer.IsDDay = () => Profile != null && Profile.CareerStarted && Profile.Bin > 15;
            PrepareProfile();
        }

        /// <summary>Brings a loaded profile up to date: performance levels (CarParts.Migrate) and bin unlocks.</summary>
        public static void PrepareProfile()
        {
            if (Profile == null) return;
            foreach (var c in Profile.Cars) CarParts.Migrate(c);
            CarParts.Migrate(QuickCar);
            CarParts.Migrate(QuickCar2);
            ShopUnlocks.Ensure();
            SyncRaceUnlocks();
        }

        /// <summary>
        /// Brings the GRaceDatabase score flags in line with where the career stands (idempotent; older saves had none):
        /// ClearRaceScores' initial unlocks, then the race_bin states every bin reached has been through: unlock_races
        /// for each bin from 15 down to the current one, rival_unlocked and done for the bins already beaten, and
        /// rival_unlocked for the current bin once its rival is open.
        /// </summary>
        public static void SyncRaceUnlocks()
        {
            if (Profile == null || Career == null) return;
            _syncedProfile = Profile;
            Gameplay.RaceDatabase.EnsureInitialized();
            if (!Profile.CareerStarted || Profile.Bin > 15) return;
            foreach (var bin in Career.Bins.Values)
            {
                if (bin.Index > 15 || bin.Index < Mathf.Max(1, Profile.Bin)) continue;
                Gameplay.RaceDatabase.OnUnlockRaces(bin);
                if (bin.Index > Profile.Bin || Profile.BossesBeaten.Contains(bin.Index))
                {
                    Gameplay.RaceDatabase.OnRivalUnlocked(bin);
                    Gameplay.RaceDatabase.OnBinDone(bin);
                }
            }
            if (CanChallengeBoss) Gameplay.RaceDatabase.OnRivalUnlocked(CurrentBin);
        }

        static Profile _syncedProfile;

        /// <summary>SyncRaceUnlocks once for the profile in use (profiles made directly, e.g. by tests, skip Load).</summary>
        public static void EnsureRaceUnlocks()
        {
            if (Profile != null && !ReferenceEquals(_syncedProfile, Profile)) SyncRaceUnlocks();
        }

        /// <summary>Where a career race is engaged: its engage trigger (bin_race_engage, radius 25), else its start grid.
        /// Accepting warps to racestart (the `prerace` warp), which can be kilometres away.</summary>
        public static RacePoint EngagePoint(RaceEvent e) => e?.Engage ?? e?.Start;

        public static OwnedCar ActiveCar => Mode == GameMode.Career && Profile?.Car != null ? Profile.Car : QuickCar;

        public static BlacklistBin CurrentBin => Career != null && Career.Bins.TryGetValue(Mathf.Max(1, Profile?.Bin ?? 15), out var b) ? b : null;

        /// <summary>Race events available now: this rival's world races, then his boss races once the requirements are met.</summary>
        /// <summary>The Challenge Series (Blacklist bin 19), in the game's order: a tollbooth and a pursuit per car.</summary>
        public static List<RaceEvent> ChallengeEvents() =>
            Career != null && Career.Bins.TryGetValue(19, out var cs) ? cs.WorldRaces.Where(e => e.Start != null).ToList() : new List<RaceEvent>();

        public static bool ChallengeUnlocked(RaceEvent e) =>
            e.InitiallyUnlocked || Profile == null || Profile.ChallengesUnlocked.Contains(e.Name) || Profile.HasWon(e.Name);

        /// <summary>FEPlayerCarDB::AwardBonusCars (the Castrol cheat, EasterEggs::ActivateEasterEgg 0x65C2E0): the Castrol GT preset
        /// becomes selectable (RefreshBonusCarList's HasBeenAwardedCastrolGT) [?: the other bonus presets it marks].</summary>
        public static void AwardBonusCars()
        {
            if (Profile == null) return;
            if (!Profile.BonusCars.Contains("castrolgt")) Profile.BonusCars.Add("castrolgt");
            Profile.Save();
        }

        /// <summary>A Challenge Series win: opens the events it unlocks and gives its car for Quick Race and Free Roam.</summary>
        public static void CompleteChallenge(RaceEvent e)
        {
            if (Profile == null) return;
            if (!Profile.HasWon(e.Name)) Profile.RacesWon.Add(e.Name);
            foreach (var k in e.UnlockRaces)
                if (Career.EventFor(k, 19) is RaceEvent next && !Profile.ChallengesUnlocked.Contains(next.Name)) Profile.ChallengesUnlocked.Add(next.Name);
            var car = Career.CarForPreset(e.PlayerCar);
            if (car != null && !Profile.BonusCars.Contains(car)) Profile.BonusCars.Add(car);
            Profile.Save();
        }

        /// <summary>Every barrier group gameplay.bin switches (the locked areas and all race-route barriers).</summary>
        public static IEnumerable<string> AllBarriers() => Career == null ? Enumerable.Empty<string>() :
            Career.Bins.Values.SelectMany(b => b.Barriers).Concat(Career.Events.Values.SelectMany(e => e.Barriers))
                .Where(n => n.TrimStart('*').StartsWith("BARRIER_")).Distinct();

        /// <summary>Barriers standing in free roam: the current Blacklist bin's closed-off areas (career only).</summary>
        public static IEnumerable<string> FreeRoamBarriers() =>
            Mode == GameMode.Career && CurrentBin != null ? CurrentBin.Barriers : Enumerable.Empty<string>();

        public static List<RaceEvent> AvailableEvents()
        {
            var bin = CurrentBin;
            if (bin == null) return new List<RaceEvent>();
            var list = new List<RaceEvent>(bin.WorldRaces);
            if (CanChallengeBoss) list.AddRange(bin.BossRaces);
            return list;
        }

        /// <summary>GRaceBin::RefreshProgress (0x6010E0) racesWon: the bin's WorldRaces completed in career (boss races do not count).</summary>
        public static int RacesWonInBin(BlacklistBin bin) => bin == null || Profile == null ? 0 : bin.WorldRaces.Count(e => Profile.HasWon(e.Name));

        /// <summary>
        /// race_bin check_rival: Bin.GetNumRacesWon ≥ RequiredRacesWon, GetPlayerBounty ≥ RequiredBounty and
        /// Bin.GetNumChallengesPassed ≥ RequiredChallenges (each capped by what the bin offers, a port guard).
        /// </summary>
        public static bool CanChallengeBoss
        {
            get
            {
                var bin = CurrentBin;
                if (bin == null || Profile == null) return false;
                return Profile.Bounty >= bin.RequiredBounty && RacesWonInBin(bin) >= Mathf.Min(bin.RequiredRacesWon, bin.WorldRaces.Count) &&
                       ChallengesDoneInBin(bin) >= Mathf.Min(bin.RequiredChallenges, ChallengesInBin(bin));
            }
        }

        /// <summary>
        /// GRaceBin::RefreshProgress challengesCompleted: the bin's milestones awarded plus its open-world speed traps
        /// completed (both are kept in Profile.Milestones by collection name).
        /// </summary>
        public static int ChallengesDoneInBin(BlacklistBin bin) => bin == null || Profile == null ? 0 :
            bin.Milestones.Count(m => Profile.Milestones.Contains(m.Name)) + bin.SpeedTraps.Count(t => Profile.Milestones.Contains(t.Name));

        public static int ChallengesInBin(BlacklistBin bin) => bin == null ? 0 : bin.Milestones.Count + bin.SpeedTraps.Count;

        /// <summary>
        /// Open-world speed traps of the bins reached so far that are still open: EEnterBin (0x62C2D0) enables the traps of
        /// every bin it passes (EnableBinSpeedTraps), GSpeedTrap::NotifyTriggered clears Active once completed.
        /// </summary>
        public static IEnumerable<WorldSpeedTrap> ActiveSpeedTraps() =>
            Career == null || Profile == null || Mode != GameMode.Career ? Enumerable.Empty<WorldSpeedTrap>() :
            Career.Bins.Values.Where(b => b.Index >= Mathf.Max(1, Profile.Bin) && b.Index <= 15).SelectMany(b => b.SpeedTraps).Where(t => !Profile.Milestones.Contains(t.Name));

        /// <summary>
        /// speedtrap_group_monitor: passing an active trap faster than its ThreshholdSpeed → NotifySpeedTrapTriggered
        /// (GSpeedTrap::NotifyTriggered 0x601730: completed, counts for its bin) and AwardPlayerBounty(Bounty).
        /// </summary>
        public static void CompleteSpeedTrap(WorldSpeedTrap trap, float kmh)
        {
            if (Profile == null || trap == null || Profile.Milestones.Contains(trap.Name)) return;
            Profile.Milestones.Add(trap.Name);
            Profile.RecordBest("speedtrap", kmh, true);
            AwardBounty(trap.Bounty);
            if (CurrentBin != null && trap.Bin == CurrentBin.Index)
            {
                int passed = ChallengesDoneInBin(CurrentBin);
                Gameplay.RaceDatabase.OnChallengePassed(CurrentBin, passed - 1);
                Gameplay.BinScript.OnChallengePassed(CurrentBin, passed);
            }
            Profile.Save();
            Story.CheckProgress();
        }

        /// <summary>
        /// The career scripts drive (Lua Drive mode): the race_bin / speedtrap / engage stategraphs send the SMS, unlock the
        /// races and pay the speed-trap bounty themselves, so the hand-ported copies of those script rules step aside.
        /// The C++ and front-end parts (cash, score flags, the rival's fall and the bin change) stay.
        /// </summary>
        public static bool ScriptsDrive => Lua.LuaFlow.Drives;

        /// <summary>
        /// Drive: Game.NotifySpeedTrapTriggered in the open world (0x60A7B0 → GSpeedTrap::NotifyTriggered 0x601730): the
        /// trap is completed and counts for its bin; the script pays its bounty (AwardPlayerBounty) and LuaGameHost tells
        /// the race_bin script (MNotifyChallengePassed when the challenges grow).
        /// </summary>
        public static void CompleteSpeedTrapByScript(string trapName, float kmh)
        {
            if (Profile == null || Career == null || trapName == null || Profile.Milestones.Contains(trapName)) return;
            var trap = Career.Bins.Values.SelectMany(b => b.SpeedTraps).FirstOrDefault(t => t.Name == trapName);
            Profile.Milestones.Add(trapName);
            Profile.RecordBest("speedtrap", kmh, true);
            Profile.Save();
            Systems.HudMessage?.Invoke($"{NfsMw.UI.FrontEndData.Text("SPEEDTRAP")}^{Settings.Speed(kmh / 3.6f)} {Settings.SpeedUnit}" + (trap != null ? $"^+{trap.Bounty:N0}" : ""));
        }

        /// <summary>Game.SetWorldHeat (0x612660) without a police system: IPerpetrator::SetHeat on the player's car.</summary>
        public static void SetWorldHeat(float heat)
        {
            if (Profile?.Car != null && heat > 0) CarParts.SetHeat(Profile.Car, heat);
        }

        /// <summary>Game.AwardPlayerBounty (0x612220): the player's bounty grows (GetPlayerBounty = the career total).</summary>
        public static void AwardBounty(int bounty)
        {
            if (Profile == null || bounty == 0) return;
            Profile.Bounty = Mathf.Max(0, Profile.Bounty + bounty);
        }

        /// <summary>
        /// An evaded pursuit: records the best stats, completes the current bin's challenges it meets and pays their
        /// bounty. Returns the challenges completed now.
        /// </summary>
        public static List<Milestone> ScorePursuit(PursuitStats s)
        {
            var done = new List<Milestone>();
            if (Profile == null || Mode != GameMode.Career) return done;
            foreach (var type in PursuitStats.Types)
                Profile.RecordBest(type, s.Value(type), type != "pursuit_evasion_time");
            var bin = CurrentBin;
            if (bin != null)
                foreach (var m in bin.Milestones)
                    if (!Profile.Milestones.Contains(m.Name) && m.Met(s.Value(m.Type)))
                    {
                        // GMilestone::NotifyPursuitOver (0x6015B0): awarded on escape, AwardPlayerBounty(Bounty), ChallengeCompleted
                        Profile.Milestones.Add(m.Name);
                        AwardBounty(m.Bounty);
                        done.Add(m);
                        if (ScriptsDrive) continue;   // the race_bin script hears MNotifyChallengePassed (LuaGameHost)
                        int passed = ChallengesDoneInBin(bin);
                        Gameplay.RaceDatabase.OnChallengePassed(bin, passed - 1);
                        Gameplay.BinScript.OnChallengePassed(bin, passed);
                    }
            return done;
        }

        /// <summary>
        /// Car lot entries unlocked for the current Blacklist position: CareerUnlocker::IsCarUnlocked
        /// (UnlockSystem.cpp:388) `frontend.UnlockedAt >= CurrentBin` (16 = from the start; Profile.Bin is the
        /// game's CurrentBin, lowered when a rival falls).
        /// </summary>
        public static IEnumerable<CarLotEntry> CarLot(bool all = false) =>
            Career.CarLot.Where(c => all || CarUnlocked(c));

        public static bool CarUnlocked(CarLotEntry c) => c.UnlockedAt >= Mathf.Max(0, Profile?.Bin ?? 15);

        public static void NewCareer(string name)
        {
            Profile = new Profile { Name = name, CareerStarted = true };
            ShopUnlocks.Ensure();
            Profile.Save();
        }

        /// <summary>
        /// Applies a career win (Game_NotifyRaceFinished 0x6119F0 / UpdateRaceScore 0x602320): CashValue on every win (repeat
        /// wins pay again), the career-complete mark the first time, the bin's per-win text messages, and the boss
        /// progression when every boss race of the bin is won.
        /// </summary>
        public static void AwardWin(RaceEvent e) => AwardWin(e, null);

        /// <summary>
        /// AwardWin with the race, in the order of Game_NotifyRaceFinished: the already-complete check, CashValue
        /// (career), the UnlockRaces of a first win, then GRaceDatabase::UpdateRaceScore(true) for the records. The race
        /// Reputation is not paid as bounty: GetReputation (0x5FB8B0) is only read by GenerateIndex and by the post-race
        /// milestone screen of pursuit races, never on a win (correction of the earlier port).
        /// </summary>
        public static void AwardWin(RaceEvent e, RaceManager race)
        {
            if (Profile == null || e == null) return;
            bool rivalLocked = !CanChallengeBoss;
            bool alreadyComplete = Gameplay.RaceDatabase.IsCareerRaceComplete(e);
            Profile.Cash += Mathf.RoundToInt(e.Cash);
            if (!alreadyComplete)
                foreach (var k in e.UnlockRaces)
                    if (Career.EventFor(k) is RaceEvent u) Gameplay.RaceDatabase.UnlockFromWin(u);
            // 0x611C17 / 0x611C1D: AdjustStableHeat_EventWin + AdjustStableImpound_EventWin on every career win (after the rewards)
            Gameplay.CareerStable.OnCareerRaceWon();
            if (race != null && race.PlayerEntrant != null) Gameplay.RaceDatabase.UpdateRaceScore(race, race.PlayerEntrant, true);
            if (!Profile.HasWon(e.Name)) Profile.RacesWon.Add(e.Name);
            bool scripts = ScriptsDrive;   // Drive: race_bin's NotifyRacePlacement handlers send the SMS and unlock
            if (e.DDayRace && scripts) Story.OnDDayRaceOver(e);
            var cur = CurrentBin;
            if (cur != null && cur.WorldRaces.Contains(e) && !scripts) Gameplay.BinScript.OnRaceWon(cur, RacesWonInBin(cur), rivalLocked);
            if (e.BossRace)
            {
                var bin = CurrentBin;
                bool allBoss = bin != null && bin.BossRaces.All(b => Profile.HasWon(b.Name));
                if (allBoss && !Profile.BossesBeaten.Contains(bin.Index))
                {
                    // race_bin rival_unlocked NotifyRacePlacement: every boss race won -> `done` (all WorldRaces unlocked);
                    // the photo-finish screen quits to SafeHouseRivalChallenge.fng, which turns on post-rival mode
                    if (!scripts) Gameplay.RaceDatabase.OnBinDone(bin);   // Drive: race_bin `done` StateEnter
                    Profile.PostRivalMode = true;
                    Profile.BossesBeaten.Add(bin.Index);
                    // EEnterBin(CurrentBin − 1) (0x62C2D0; fired from the rival's result screen in post-rival mode): the next
                    // bin's milestones and speed traps open (they are counted from Profile.Bin), its `initial` messages go out
                    Profile.Bin = bin.Index - 1;
                    ShopUnlocks.Ensure();
                    Story.PendingRivalBeaten = bin.Index;
                    // Drive: the new bin's race_bin_NN runs `initial` (EntryCellCallID) and `unlock_races` when LuaFlow
                    // starts it in free roam
                    if (!scripts && Career.Bins.TryGetValue(Profile.Bin, out var next))
                    {
                        Gameplay.BinScript.OnEnterBin(next);
                        Gameplay.RaceDatabase.OnUnlockRaces(next);
                    }
                }
            }
            Profile.Save();
        }

        /// <summary>
        /// A quick race won by the player (Game_NotifyRaceFinished outside career): the first win's UnlockRaces, then
        /// UpdateRaceScore(true): the records and the completed-in-quick-race flag (1).
        /// </summary>
        public static void QuickRaceWon(RaceManager race)
        {
            var e = race?.Event;
            if (Profile == null || e == null || race.PlayerEntrant == null) return;
            if (!Gameplay.RaceDatabase.CheckFlags(e, RaceIndex.CompletedCareer))
                foreach (var k in e.UnlockRaces)
                    if (Career.EventFor(k) is RaceEvent u) Gameplay.RaceDatabase.UnlockFromWin(u);
            Gameplay.RaceDatabase.UpdateRaceScore(race, race.PlayerEntrant, true);
            Profile.Save();
        }

        /// <summary>
        /// GRaceStatus::RaceAbandoned (0x603220): quitting (or leaving after losing) a career boss race other than the epic
        /// pursuit clears the career-complete mark of every boss race of the bin, so the rival chain starts again.
        /// </summary>
        public static void AbandonRace(RaceEvent e)
        {
            if (Profile == null || e == null || Mode != GameMode.Career || !e.BossRace || e.EpicPursuit) return;
            var bin = Career.Bins.Values.FirstOrDefault(b => b.BossRaces.Contains(e));
            if (bin == null || Profile.BossesBeaten.Contains(bin.Index)) return;
            int removed = Profile.RacesWon.RemoveAll(n => bin.BossRaces.Any(b => b.Name == n));
            foreach (var b in bin.BossRaces) Gameplay.RaceDatabase.ResetCareerCompleteFlag(b);
            if (removed > 0) Profile.Save();
        }
    }
}
