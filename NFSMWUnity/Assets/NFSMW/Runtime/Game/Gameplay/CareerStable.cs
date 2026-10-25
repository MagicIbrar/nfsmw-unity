using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// The career stable's heat / impound bookkeeping the exe's gameplay and cop code calls into the FE database
    /// (FEDatabase.cpp's FEImpoundData / FECareerRecord, ported in Runtime/UI/Safehouse/FEImpound.cs). Callers in speed.exe:
    /// * Game_NotifyRaceFinished (0x6119F0): after the career cash and the first-win EAwardUpgrades, **every career win** runs
    ///   AdjustStableHeat_EventWin(player) (0x611C17 → 0x590260) and AdjustStableImpound_EventWin(player) (0x611C1D → 0x580990)
    ///   — outside the `alreadyComplete` skip (correction to gameplay_core.md §2.5's "first career win only").
    /// * AICopManager::PursuitIsEvaded (0x411070): for the player's pursuit, after NotifyPursuitEnded(true):
    ///   AdjustStableImpound_EvadePursuit(0) (0x4110F3); then for the perp's player index 0 / 1: MPerpEscaped and
    ///   AdjustStableHeat_EvadePursuit(index) (0x411211) — roaming or not.
    /// * AICopManager::CommitPursuitDetails (0x411240, from UpdatePursuits 0x43EB6E when a pursuit ends, skipped in Challenge
    ///   Series races): the perp's FECareerRecord gets CommitPursuitCarData(GInfractionManager's infraction bits, the pursuit's
    ///   bounty (IPerpetrator +0x44 + +0x40), evaded = !IPursuit::IsBusted (+0x74)) (0x411373); UserProfile::CommitPursuitInfo;
    ///   IReputation::SetBounty(FEPlayerCarDB::GetTotalBounty) and, when not busted, MNotifyPlayerRep.
    /// * FEImpoundData::NotifyBusted (0x56F6A0) / BecomeImpounded (0x56F630) / NotifyWin are the FE's QRCarSelectBustedManager
    ///   (SetSelectedCar 0x7AD77D / 0x7AD7BA: the busted car-select screen); the busted hook here stands in for it [?].
    /// * AdjustStableHeat_MilestoneComplete (0x5901E0) has **no caller** in speed.exe v1.3 (no call or pointer reference): not wired.
    /// </summary>
    public static class CareerStable
    {
        /// <summary>Game_NotifyRaceFinished's stable adjustments on a career win (every win).</summary>
        public static void OnCareerRaceWon()
        {
            if (GameSession.Mode != GameMode.Career || GameSession.Profile == null) return;
            FEImpound.AdjustStableHeat_EventWin();
            FEImpound.AdjustStableImpound_EventWin();
        }

        /// <summary>PursuitIsEvaded + CommitPursuitDetails for the player's car (career; not in a Challenge Series race).</summary>
        public static void OnPursuitEvaded(OwnedCar car, PursuitStats stats, int bounty)
        {
            if (GameSession.Mode != GameMode.Career || GameSession.Profile == null) return;
            if (car != null && stats != null) FEImpound.CommitPursuitCarData(car, stats.InfractionBits, bounty, true);
            FEImpound.AdjustStableImpound_EvadePursuit();
            FEImpound.AdjustStableHeat_EvadePursuit();
        }

        /// <summary>
        /// AICopManager::CommitPursuitDetails on a bust (0x411373: CommitPursuitCarData with evaded = false). The impound strike is
        /// not taken here: FEImpoundData::NotifyBusted / BecomeImpounded belong to the busted screen (uiInfractions / QRCarSelectBusted
        /// Manager 0x7AD77D / 0x7AD7BA — the port's InfractionsScreen), which applies them 1:1.
        /// </summary>
        public static void OnPursuitBusted(OwnedCar car, PursuitStats stats, int bounty)
        {
            if (GameSession.Mode != GameMode.Career || GameSession.Profile == null || car == null) return;
            if (stats != null) FEImpound.CommitPursuitCarData(car, stats.InfractionBits, bounty, false);
        }
    }
}
