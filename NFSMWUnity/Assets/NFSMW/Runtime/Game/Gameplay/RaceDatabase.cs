using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// GRaceDatabase (GRaceDatabase.cpp 0x5D9580-0x603D4C; specs/gameplay_core.md §9): the per-event score infos of the
    /// save (GRaceSaveInfo: ScoreFlags + records) and the unlock / availability rules built on them. Kept in
    /// <see cref="Profile.RaceScores"/>; the career-completed flag (2) is the port's <see cref="Profile.RacesWon"/> list.
    /// </summary>
    public static class RaceDatabase
    {
        static Profile P => GameSession.Profile;
        static CareerData Career => GameSession.Career;

        /// <summary>GetScoreInfo (0x5DCA40): the race's record, created on first use (the game fills the first free slot).</summary>
        public static RaceScore ScoreInfo(RaceEvent ev)
        {
            if (P == null || ev?.Name == null) return null;
            EnsureInitialized();
            var s = P.RaceScores.FirstOrDefault(r => r.Race == ev.Name);
            if (s == null) { s = new RaceScore { Race = ev.Name }; P.RaceScores.Add(s); }
            if (P.HasWon(ev.Name)) s.Flags |= RaceIndex.CompletedCareer;
            return s;
        }

        /// <summary>CheckRaceScoreFlags (0x5DCA70): (flags &amp; mask) != 0. The career-completed bit reads RacesWon.</summary>
        public static bool CheckFlags(RaceEvent ev, int mask)
        {
            if (P == null || ev?.Name == null) return false;
            if ((mask & RaceIndex.CompletedCareer) != 0 && P.HasWon(ev.Name)) return true;
            EnsureInitialized();
            var s = P.RaceScores.FirstOrDefault(r => r.Race == ev.Name);
            return s != null && (s.Flags & mask) != 0;
        }

        public static bool IsCareerRaceComplete(RaceEvent ev) => CheckFlags(ev, RaceIndex.CompletedCareer);
        public static bool IsCareerRaceUnlocked(RaceEvent ev) => CheckFlags(ev, RaceIndex.UnlockedCareer);
        public static bool IsQuickRaceUnlocked(RaceEvent ev) => CheckFlags(ev, RaceIndex.UnlockedQuickRace);
        public static bool IsOnlineRaceUnlocked(RaceEvent ev) => CheckFlags(ev, RaceIndex.UnlockedOnline);

        /// <summary>
        /// ClearRaceScores (0x600B20), run once for a save: every race except the D-Day ones gets UnlockedQuickRace (4)
        /// when it is InitiallyUnlockedQuickRace or (not a boss race and) InitiallyUnlockedChallenge, and UnlockedOnline
        /// (16) when InitiallyUnlockedOnline.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (P == null || Career == null || P.RaceScoresInitialized) return;
            P.RaceScoresInitialized = true;
            foreach (var ev in AllRaces())
            {
                if (ev.DDayRace || ev.Name == null) continue;
                bool qr = ev.InitiallyUnlockedQuickRace, online = ev.InitiallyUnlockedOnline, challenge = ev.InitiallyUnlockedChallenge && !ev.BossRace;
                if (!qr && !online && !challenge) continue;
                var s = P.RaceScores.FirstOrDefault(r => r.Race == ev.Name);
                if (s == null) { s = new RaceScore { Race = ev.Name }; P.RaceScores.Add(s); }
                if (qr || challenge) s.Flags |= RaceIndex.UnlockedQuickRace;
                if (online) s.Flags |= RaceIndex.UnlockedOnline;
            }
        }

        /// <summary>GetRaceCount / GetRaceParameters: every race activity of gameplay.bin (CareerData.RaceActivities).</summary>
        public static IEnumerable<RaceEvent> AllRaces() =>
            Career == null ? Enumerable.Empty<RaceEvent>() : Career.RaceActivities();

        /// <summary>
        /// Game.UnlockRace (0x612140): UnlockedQuickRace (4) unless NeverInQuickRace, and UnlockedCareer (8);
        /// then RefreshEngageTriggerIcons.
        /// </summary>
        public static void UnlockRace(RaceEvent ev)
        {
            var s = ScoreInfo(ev);
            if (s == null) return;
            if (!ev.NeverInQuickRace) s.Flags |= RaceIndex.UnlockedQuickRace;
            s.Flags |= RaceIndex.UnlockedCareer;
        }

        /// <summary>
        /// Game_NotifyRaceFinished's UnlockRaces loop (0x611C29, first win only): as compiled it sets UnlockedQuickRace (4)
        /// always and UnlockedCareer (8) unless NeverInQuickRace (the reverse of Game.UnlockRace).
        /// </summary>
        public static void UnlockFromWin(RaceEvent ev)
        {
            var s = ScoreInfo(ev);
            if (s == null) return;
            s.Flags |= RaceIndex.UnlockedQuickRace;
            if (!ev.NeverInQuickRace) s.Flags |= RaceIndex.UnlockedCareer;
        }

        /// <summary>ResetCareerCompleteFlag (0x5DCBB0): flags &amp;= ~2 (RaceAbandoned on a boss chain).</summary>
        public static void ResetCareerCompleteFlag(RaceEvent ev)
        {
            if (P == null || ev?.Name == null) return;
            P.RacesWon.Remove(ev.Name);
            var s = P.RaceScores.FirstOrDefault(r => r.Race == ev.Name);
            if (s != null) s.Flags &= ~RaceIndex.CompletedCareer;
        }

        /// <summary>
        /// GRaceParameters::GetIsAvailable (0x5FD160): the unlock-all switch (DAT_00926124, EasterEggs.UnlockAllThings) answers yes; quick race =
        /// not NeverInQuickRace and UnlockedQuickRace; online = UnlockedOnline; career = UnlockedCareer and not completed.
        /// </summary>
        public static bool IsAvailable(RaceEvent ev, int context)
        {
            if (ev == null) return false;
            if (Controls.EasterEggs.UnlockAllThings) return true;   // DAT_00926124
            switch (context)
            {
                case RaceIndex.ContextQuickRace: return !ev.NeverInQuickRace && IsQuickRaceUnlocked(ev);
                case RaceIndex.ContextOnline: return IsOnlineRaceUnlocked(ev);
                case RaceIndex.ContextCareer: return IsCareerRaceUnlocked(ev) && !IsCareerRaceComplete(ev);
                default: return false;
            }
        }

        /// <summary>GetNextDDayRace (0x600C30): the first of sDDayRaces not completed in career (null when all are).</summary>
        public static string NextDDayRace()
        {
            foreach (var id in RaceIndex.DDayRaces)
            {
                var ev = Career?.EventById(id);
                if (ev == null) continue;
                if (!IsCareerRaceComplete(ev) && !(P != null && P.EventsDone.Contains(id))) return id;
            }
            return null;
        }

        /// <summary>
        /// UpdateRaceScore (0x602320) for the winning human (GetWinningPlayerInfo): top / average speed, then the record by
        /// GRace::Type — P2P, drag, tollbooth and the jumps keep the lowest race time, circuit and knockout the lowest lap
        /// time, speedtrap and cash grab the highest points (checkpoint races: an integer score) — replaced when no
        /// completion flag is set yet or the new value is better. `finished` sets CompletedCareer (career context) or
        /// CompletedQuickRace, then RefreshBinProgress.
        /// </summary>
        public static void UpdateRaceScore(RaceManager race, RaceManager.Entrant winner, bool finished)
        {
            var ev = race?.Event;
            if (ev == null || winner == null) return;
            var s = ScoreInfo(ev);
            if (s == null) return;
            bool first = (s.Flags & (RaceIndex.CompletedQuickRace | RaceIndex.CompletedCareer)) == 0;
            float raceTime = Mathf.Max(0f, winner.FinishTime - ev.StartTime);
            // 0x60237D-0x602422: the stored top / average speed (udecfix16 / GetScale) are kept when bigger — max(old, GRacerInfo
            // +0x100 top speed) and max(old, CalcAverageSpeed 0x5E3390 = DistanceDriven +0xFC / (race time, live while racing))
            s.TopSpeed = Mathf.Max(s.TopSpeed, winner.TopSpeed);
            s.AverageSpeed = Mathf.Max(s.AverageSpeed, raceTime > 0 ? winner.DistanceDriven / raceTime : 0f);
            int type = ev.GRaceType >= 0 ? ev.GRaceType : TypeFromPort(ev.Type);
            switch (type)
            {
                case RaceIndex.P2P: case RaceIndex.Drag: case RaceIndex.Tollbooth: case RaceIndex.JumpToSpeedTrap: case RaceIndex.JumpToMilestone:
                    if (first || raceTime < s.Best) s.Best = raceTime;
                    break;
                case RaceIndex.Circuit: case RaceIndex.Knockout:
                    float lap = BestLap(winner, ev.StartTime);
                    if (first || lap < s.Best) s.Best = lap;
                    break;
                case RaceIndex.SpeedTrap: case RaceIndex.CashGrab: case RaceIndex.Checkpoint:
                    if (first || s.Best < winner.Points) s.Best = winner.Points;
                    break;
            }
            if (finished)
            {
                if (GameSession.Mode == GameMode.Career)
                {
                    s.Flags |= RaceIndex.CompletedCareer;
                    if (!P.HasWon(ev.Name)) P.RacesWon.Add(ev.Name);
                }
                else s.Flags |= RaceIndex.CompletedQuickRace;
            }
        }

        /// <summary>FUN_005FFFD0: the racer's best lap (lap times are the gaps between lap end times; lap 1 from StartTime).</summary>
        public static float BestLap(RaceManager.Entrant e, float startTime)
        {
            float best = 0, prev = startTime;
            foreach (var t in e.Laps)
            {
                float lap = t - prev;
                prev = t;
                if (lap > 0 && (best <= 0 || lap < best)) best = lap;
            }
            if (best <= 0) best = Mathf.Max(0f, e.FinishTime - startTime);
            return best;
        }

        static int TypeFromPort(RaceType t) => t switch
        {
            RaceType.Sprint => RaceIndex.P2P, RaceType.Circuit => RaceIndex.Circuit, RaceType.Drag => RaceIndex.Drag,
            RaceType.Knockout => RaceIndex.Knockout, RaceType.Tollbooth => RaceIndex.Tollbooth, RaceType.SpeedTrap => RaceIndex.SpeedTrap,
            RaceType.Pursuit => RaceIndex.Challenge, _ => -1,
        };

        // ---- the race_bin stategraph's unlocks (career_stategraphs.txt) --------------------------------------------

        /// <summary>
        /// race_bin `unlock_races` StateEnter (#540): Game.UnlockRace on the target of every engage trigger of the bin with
        /// InitiallyUnlocked. (The race_bin_15 override handler is an empty extra handler; handlers add up.)
        /// </summary>
        public static void OnUnlockRaces(BlacklistBin bin)
        {
            if (bin == null || Career == null) return;
            foreach (var (_, target, unlocked) in bin.EngageTriggers)
                if (unlocked && Career.EventFor(target, bin.Index) is RaceEvent ev) UnlockRace(ev);
        }

        /// <summary>race_bin `rival_unlocked` StateEnter (#310): Game.UnlockRace on every BossRaces entry.</summary>
        public static void OnRivalUnlocked(BlacklistBin bin)
        {
            if (bin == null) return;
            foreach (var ev in bin.BossRaces) UnlockRace(ev);
        }

        /// <summary>race_bin `done` StateEnter (#216): Game.UnlockRace on every WorldRaces entry.</summary>
        public static void OnBinDone(BlacklistBin bin)
        {
            if (bin == null) return;
            foreach (var ev in bin.WorldRaces) UnlockRace(ev);
        }

        /// <summary>
        /// race_bin `rival_locked` NotifyChallengePassed (#336): when the challenge passed is the one that reaches
        /// RequiredChallenges, EAddSMS(SMSCellChallenge) if set and Game.UnlockRace(CellChallengeRace) if set.
        /// </summary>
        public static void OnChallengePassed(BlacklistBin bin, int passedBefore)
        {
            if (bin == null || Career == null || passedBefore + 1 != bin.RequiredChallenges) return;
            if (bin.SmsCellChallenge >= 0) Sms.Show(bin.SmsCellChallenge);
            if (bin.CellChallengeRace != 0 && Career.EventFor(bin.CellChallengeRace) is RaceEvent ev) UnlockRace(ev);
        }
    }
}
