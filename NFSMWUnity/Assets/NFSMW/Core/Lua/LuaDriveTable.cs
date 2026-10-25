using System.Collections.Generic;

namespace NfsMw.Core.Lua
{
    /// <summary>
    /// What Drive mode does with each Action binding and E* event the scripts use (specs/lua_hooks.md §4.2) [ours: the
    /// classification; the C# behind each entry is LuaDrive / RaceManager.Drive in the Unity runtime]. Shared by the game
    /// and the harness (`lua drive`), so the offline simulation reports exactly the calls the game would log as missing.
    ///   Executed  the C# game does what the exe does (career, race status, cops, NIS, GPS, movies, …).
    ///   NoOp      the call has no effect worth porting (map icons, audio flags, debug output, HUD-only state) or the
    ///             exe's function is empty on PC; it is accepted silently.
    ///   FrontEnd  the C# front end does the same job itself (the engage dialog, the menu-gate prompt, the pause menu):
    ///             the dialog's answer reaches the scripts the way the exe's does (MAcceptEnterCareerEvent, …).
    /// Anything else is "missing": Drive logs it once and the script goes on.
    /// Keys are Table.Name (RaceStatus methods without the ':').
    /// </summary>
    public static class LuaDriveTable
    {
        public enum Kind { Missing, Executed, NoOp, FrontEnd }

        static readonly Dictionary<string, Kind> Map = new Dictionary<string, Kind>();

        static void Add(Kind k, params string[] names) { foreach (var n in names) Map[n] = k; }

        static LuaDriveTable()
        {
            Add(Kind.Executed,
                // career (GManager, GRaceDatabase); Activity.Run is Core, but Drive defers a race's until it is loaded
                "Activity.Run", "Game.UnlockRace", "Game.AwardPlayerBounty", "Game.JumpToSafeHouse", "Game.JumpToCarLot", "Game.DoSafeHouseIntro",
                "Game.SetWorldHeat", "Game.NotifySpeedTrapTriggered", "Game.PlayTutorial", "Game.ChallengeComplete", "Game.ChallengeCompleted",
                // cops and the world
                "Game.SetChanceOfRain", "Game.DetachCameraFromRacer", "Game.SetCopsEnabled", "Game.ForcePursuitStart", "Game.NoNewPursuitsOrCops", "Game.ResetCopsForRestart",
                "Game.PreventPlayerBeingBusted", "Game.SpawnCop", "Game.SpawnCharacter", "Game.UnspawnCharacter", "Game.SetTrafficSpeed",
                "Game.ShowGPS", "Game.NavigatePlayerTo", "Game.WarpToMarkerWhenRoaming", "Game.ResetTrigger", "Game.ShowTriggerIcon", "Game.HideTriggerIcon", "Game.SendCharacterStimulus", "Game.JackKnife",
                // the race (ILuaRaceControl on RaceManager)
                "Game.InitRacers", "Game.SetRacerGoal", "Game.NotifyCheckpointReached", "Game.NotifyLapFinished", "Game.NotifyRaceFinished",
                "Game.KnockoutRacer", "Game.TotalRacer", "Game.AwardCash", "Game.AwardPoints", "Game.StartRace", "Game.StartRaceTimers",
                "Game.SetAllStaging", "Game.EnterPostRaceFlow", "Game.ShowWinningPostRaceScreen", "Game.ShowLosingPostRaceScreen",
                "Game.ShowRaceOverSummary", "Game.WarpPlayerToTrigger", "Game.SaveStartPositions", "Game.RestoreStartPositions",
                "Game.SabotageEngine", "Game.BlowEngine", "Game.ForceAIControl", "Game.ClearAIControl", "Game.AbandonRace",
                "RaceStatus.AwardBonusTime", "RaceStatus.SetRoaming", "RaceStatus.NotifyScriptWhenLoaded",
                // presentation
                "NIS.Play", "Audio.SetFlag", "HUD.ShowMessage", "HUD.ShowTimeExtension", "Camera.SetGenericCamera", "Game.Fade", "Debug.ShowScreenMessage");
            Add(Kind.NoOp,
                "MiniMap.AddEngagedRace", "Game.SetRacerLapsLeft",
                "Game.NotifyCountdownDone", "Game.SetRaceActivity", "Game.SetPlayerStartPosition",
                "Game.DoSpecialFinalization", "Game.ReloadWorld", "Game.SetHasRapSheet", "Game.HideRaceOverSummary",
                "Game.ShowOnlinePostRaceScreen", "Game.IntroduceRival", "Movie.PlayHackE3FMV", "Demo.SetRaceCompleteForFE",
                "Demo.StorePursuitRepForFE", "RaceStatus.AddAvailableEventToMap", "RaceStatus.AddSpeedTrapToMap",
                "RaceStatus.ClearRacers", "RaceStatus.AddRacer", "RaceStatus.ClearCheckpoints", "RaceStatus.AddCheckpoint",
                "RaceStatus.SetNextCheckpointPos", "RaceStatus.SetIsLoading", "RaceStatus.EnterSuddenDeath", "RaceStatus.SetActivelyRacing",
                "RaceStatus.SetTaskTime", "RaceStatus.DisableBarriers", "RaceStatus.EnableBinBarriers", "RaceStatus.SetHasBeenWon");
            Add(Kind.FrontEnd,
                "Game.EnterEngagableTrigger", "Game.ExitEngagableTrigger", "Game.EnterGateZone", "Game.ExitGateZone",
                "Game.DoZoneMenuAction", "Game.ShowPauseMenu");
            // E* events
            Add(Kind.Executed, "EAddSMS", "ECellCall", "EShowRaceCountdown", "EShowMessageScreen", "EPlayRaceMovie", "EPlayRaceNIS",
                "EFadeScreenNoLoadingBarOn", "EFadeScreenNoLoadingBarOff", "EBailPursuit", "EShowPostPursuit", "ETerminateMusic", "EResetProps");
            Add(Kind.NoOp, "ENISWolrdGeometry", "ENISNukeSmack", "EDispIntroRace", "EJumpToStrategyFlow",
                "ECinematicMoment", "EDebugScreenMessage", "E911Call");
        }

        /// <summary>What Drive does with a binding (Table.Name) or an E* event name.</summary>
        public static Kind Of(string name) => name != null && Map.TryGetValue(name, out var k) ? k : Kind.Missing;
        public static Kind Of(LuaBinding b) => Of(b.Table + "." + b.Name);
        public static IEnumerable<KeyValuePair<string, Kind>> All => Map;
    }
}
