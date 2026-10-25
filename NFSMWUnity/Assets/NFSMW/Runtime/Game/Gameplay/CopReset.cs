using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The gameplay side's calls into the cop manager (specs/gameplay_core.md §10.1), resolving ai_cops.md's open
    /// "free-roam start / restart lockout" item:
    /// <list type="bullet">
    /// <item>AICopManager's constructor sets mLockoutTimer = kCopLockoutTime (60 s): <see cref="ConstructionLockout"/>,
    /// called when the world systems are created (Systems.Ensure).</item>
    /// <item>ICopMgr::ResetCopsForRestart(true) (AICopManager.cpp:2150) is called by Game.InitRacers (0x61DC2B) and by the
    /// Lua binding Game.ResetCopsForRestart (0x60AB10), which the `basicrace` prerace StateEnter calls first (#1224); so on
    /// every race start and restart. GManager::NotifyWorldService (0x5EBBDF) calls it too before a queued pursuit warp
    /// (the rap-sheet bounty jump), followed by LockoutCops(false) and PursueAtHeatLevel(1).</item>
    /// <item>GRaceStatus::SetRoaming (0x5FE506) calls ICopMgr::LockoutCops(true) when a race ends (TimeInactiveFor911
    /// since the play mode is roaming again).</item>
    /// </list>
    /// </summary>
    public sealed partial class PursuitSystem
    {
        /// <summary>AICopManager::AICopManager: mLockoutTimer = kCopLockoutTime (60 s).</summary>
        public void ConstructionLockout() => _lockoutUntil = Time.time + CopLockoutTime;

        /// <summary>
        /// ICopMgr::ResetCopsForRestart (AICopManager.cpp:2150-2236): lockout = kCopLockoutTime, 0 in a challenge race,
        /// TimeInactiveFor911 (the perp's pursuit row) when roaming or with no startup race (career races started in the
        /// world); the speech AI resets; every roadblock (its smackables released) and pursuit goes, the pursuit target's
        /// pending rep points are dropped, every active cop unspawns (MUnspawnCop reason 0), and with `release` the cop
        /// vehicles are killed; NoNewPursuitsOrCops clears.
        /// </summary>
        public void ResetCopsForRestart(bool release)
        {
            float lockout = CopLockoutTime;
            var race = RaceManager.Active?.Event;
            bool challengeRace = Challenge != null || (race != null && race.ChallengeSeries);
            if (challengeRace) lockout = 0f;
            // GRaceDatabase::GetStartupRace() == null: a career race engaged in the world has no startup race [port: the
            // career mode]; quick races keep kCopLockoutTime
            if (IsRoaming || GameSession.Mode == GameMode.Career) lockout = PerpRow.TimeInactiveFor911;
            _lockoutUntil = lockout > 0f ? Time.time + lockout : 0f;
            var ch = Challenge;
            Clear(true);
            Challenge = ch;
        }
    }
}
