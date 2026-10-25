using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;
using NfsMw.Game.Gameplay;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game.Lua
{
    /// <summary>
    /// The GRaceStatus side of the bindings in Drive mode (specs/gameplay_core.md §1-2, lua_hooks.md §4.2): what the
    /// `basicrace` scripts need from the race. RaceManager implements it for a race it runs in Drive (RaceManager.Driven);
    /// LuaFlow.OnRaceBegin sets LuaFlow.Race. Racer indices are 1-based as in the exe (Game_GetRacerIndex 0x60DAE0).
    /// </summary>
    public interface ILuaRaceControl
    {
        RaceEvent Event { get; }
        /// <summary>The intro NIS the script asked for is playing.</summary>
        bool NisPlaying { get; }
        bool Over { get; }
        /// <summary>Game.InitRacers (0x61DBE0): ClearRacers, the player first, the opponents and the grid at `start`.</summary>
        void InitRacers(RacePoint start);
        /// <summary>Game.SetRacerGoal (0x611700): AITarget::Aquire at the trigger + SetNextCheckpointPos for the player.</summary>
        void SetRacerGoal(int racer, GInstance goal, RacePoint at);
        /// <summary>Game.NotifyCheckpointReached (0x6117F0): SetCheckpointTime, StartCheckpoint(n).</summary>
        void CheckpointReached(int racer, int checkpoint);
        /// <summary>Game.NotifyLapFinished (0x611850): lap time, StartLap, rankings, the lap knockout.</summary>
        void LapFinished(int racer, int lap);
        /// <summary>Game.NotifyRaceFinished (0x6119F0): FinishRace, rankings, the win bookkeeping.</summary>
        void RaceFinished(int racer);
        /// <summary>Game.KnockoutRacer / TotalRacer (0x611440, one function).</summary>
        void Knockout(int racer);
        /// <summary>Game.AwardCash / AwardPoints (0x60E030): speedtrap points (cash grab: cash).</summary>
        void AwardPoints(int racer, float amount);
        /// <summary>RaceStatus:AwardBonusTime (0x5FE600).</summary>
        void AwardBonusTime(float seconds);
        /// <summary>Game.NotifySpeedTrapTriggered (0x60A7B0) in a race: the camera's speed record.</summary>
        void SpeedTrapTriggered(int racer, RacePoint trap, float kmh);
        /// <summary>Game.StartRace (0x60DBD0): traffic, barriers, DetermineRaceLength, MakeCatchUpData, heat.</summary>
        void StartRace();
        /// <summary>EShowRaceCountdown: the 3-2-1.</summary>
        void StartCountdown();
        /// <summary>Game.StartRaceTimers (0x604EF0): the race is on.</summary>
        void StartRaceTimers();
        /// <summary>Game.SetAllStaging (0x60DF70).</summary>
        void SetAllStaging(bool staging);
        /// <summary>NIS.Play of the race's intro while the race waits for it.</summary>
        void PlayIntroNis();
        /// <summary>Game.EnterPostRaceFlow (0x611F20).</summary>
        void EnterPostRaceFlow();
        /// <summary>Game.ShowWinningPostRaceScreen / ShowLosingPostRaceScreen (FE_ShowPostRaceScreen 0x60AC20).</summary>
        void ShowPostRaceScreen(bool won);
        /// <summary>Game.ShowRaceOverSummary (0x6120C0).</summary>
        void ShowRaceOverSummary();
        /// <summary>Game.AbandonRace (0x60DEB0).</summary>
        void AbandonRace();
        /// <summary>RaceStatus:SetRoaming (0x5FE3E0).</summary>
        void SetRoaming();
        /// <summary>The race's activity is done and a script runs another race [ours].</summary>
        void EndForNextActivity();
        /// <summary>Game.WarpPlayerToTrigger (0x611570).</summary>
        void WarpPlayerTo(RacePoint at);
        void SaveStartPositions();
        void RestoreStartPositions();
        /// <summary>Game.ForceAIControl / ClearAIControl (0x6123B0 / 0x612420).</summary>
        void SetAIControl(int player, bool on);
    }

    /// <summary>
    /// Drive mode: the scripts' game-changing calls executed against the C# game (LuaDriveTable says which). Returns false
    /// for a call it cannot execute ("missing"). The race internals go through LuaFlow.Race (ILuaRaceControl).
    /// </summary>
    public static class LuaDrive
    {
        static RacePoint _warpWhenRoaming;
        /// <summary>Camera.SetGenericCamera (0x604A50) → ECinematicMoment(group, track): the GENERIC ICE track to play
        /// (the challenge races' 'Cinematics' / 'Challenge') [ours: for the camera code to subscribe].</summary>
        public static event System.Action<string, string> CinematicMoment;
        static readonly List<(float at, System.Action a)> Later = new List<(float, System.Action)>();

        static RaceEvent Ev(object o) => o is GInstance g && GameSession.Career != null ? GameSession.Career.EventFor(g.Key) : null;
        static RacePoint Pt(object o) => o is GInstance g ? LuaTriggers.PointOf(g.Record) : null;
        static float F(object o) => o is float f ? f : o is int i ? i : 0f;

        /// <summary>The race a binding talks about: the driven race, when there is one.</summary>
        static ILuaRaceControl Race => LuaFlow.Race != null && !(LuaFlow.Race is Object u && !u) ? LuaFlow.Race : null;

        /// <summary>A HUD label from the language tables (the bindings pass label names) [?: LANGUAGE_ prefix dropped].</summary>
        static string Label(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            FrontEndData.Load();
            string t = FrontEndData.Text(s);
            if (t != s) return t;
            if (s.StartsWith("LANGUAGE_")) { var k = s.Substring(9); t = FrontEndData.Text(k); if (t != k) return t; }
            return s;
        }

        /// <summary>The name table of Audio_SetFlag (0x8F5D78, 9 pairs): eNIS_Start 2, eNIS_321GO 3, eNIS_Busted 4, eNIS_EndofRace 5,
        /// eFE_PostRaceScreen 6, eFE_CameraScreen 7, eLOAD_GeneralLoading 10, eLOAD_BustedLoading 11.</summary>
        static readonly (string name, int flow)[] AudioFlags =
        {
            ("eNIS_Start", 2), ("eNIS_321GO", 3), ("eNIS_Busted", 4), ("eNIS_EndofRace", 5), ("eFE_PostRaceScreen", 6),
            ("eFE_CameraScreen", 7), ("eLOAD_GeneralLoading", 10), ("eLOAD_BustedLoading", 11),
        };

        /// <summary>
        /// Audio_SetFlag (0x6048E0): every table entry whose name matches (bStrCmp) posts ESndGameState(flow, on) (ctor 0x6253D0)
        /// and, by the jump table at 0x6049C4/0x6049E4, SetSoundControlState(on, state, "LuaCall:…") (0x4B9080): end of race
        /// (5) → 7, the FE camera (7) → 1, loading (10) → 0, busted loading (11) → 14; the others set no control state. The
        /// scripts use eNIS_Start / eNIS_321GO / eNIS_EndofRace / eFE_PostRaceScreen (basicrace intronis, countdown, outronis,
        /// postrace).
        /// </summary>
        public static void AudioSetFlag(string flag, bool on)
        {
            if (flag == null) return;
            foreach (var (n, flow) in AudioFlags)
            {
                if (n != flag) continue;
                Audio.SoundState.SetGameFlow(flow, on);
                switch (flow)
                {
                    case 5: Audio.SoundState.Set(on, (Audio.SndCtl)7); break;     // "LuaCall:end of race"
                    case 7: Audio.SoundState.Set(on, (Audio.SndCtl)1); break;     // "LuaCall:fe cam"
                    case 10: Audio.SoundState.Set(on, (Audio.SndCtl)0); break;    // "LuaCall:loading"
                    case 11: Audio.SoundState.Set(on, (Audio.SndCtl)14); break;   // "LuaCall:bust load"
                }
            }
        }

        public static bool Execute(LuaBinding b, object[] args, StategraphRuntime rt, out object result)
        {
            result = null;
            var race = Race;
            var police = Systems.Police;
            string name = b.Table + "." + b.Name;
            switch (name)
            {
                // ---- activities ---------------------------------------------------------------------------------------
                case "Activity.Run":
                    {
                        // a race activity runs once its race is loaded (GRaceParameters::BlockUntilLoaded): the C# race
                        // starts here and LuaFlow.OnRaceBegin runs the activity. Not a race: the runtime runs it now.
                        var ev = Ev(args[0]);
                        if (ev == null || ev.Start == null) return true;
                        result = false;
                        var rm = RaceManager.Active;
                        if (rm != null && rm.Event == ev || GameFlow.RaceLoading) return true;
                        if (rm != null && rm.Driven)
                        {
                            var cur = rt.Activity(rt.Instance(rm.Event.Key));
                            if (cur == null || !cur.Running || cur.Current?.Name == "done") rm.EndForNextActivity();
                        }
                        if (RaceManager.Active == null)
                        {
                            Debug.Log($"NFSMW lua: Drive: Activity.Run {ev.Name}: the race loads");
                            GameFlow.StartRace(ev);
                        }
                        else Debug.Log($"NFSMW lua: Drive: Activity.Run {ev.Name} ignored: {RaceManager.Active.Event?.Name} is running");
                        return true;
                    }

                // ---- audio --------------------------------------------------------------------------------------------
                case "Audio.SetFlag": AudioSetFlag(args[0] as string, args.Length > 1 && args[1] is bool flagOn && flagOn); return true;

                // ---- career ------------------------------------------------------------------------------------------
                case "Game.UnlockRace": { var ev = Ev(args[0]); if (ev == null) return false; RaceDatabase.UnlockRace(ev); return true; }
                case "Game.AwardPlayerBounty": GameSession.AwardBounty((int)args[0]); GameSession.Profile?.Save(); return true;
                case "Game.JumpToSafeHouse": GameFlow.JumpToSafeHouse(); return true;
                case "Game.JumpToCarLot": Story.JumpToCarLot(); return true;
                case "Game.DoSafeHouseIntro": Story.SafehouseIntro(Pt(args[0])); return true;
                case "Game.SetWorldHeat": if (police) police.SetWorldHeat(F(args[0])); else GameSession.SetWorldHeat(F(args[0])); return true;
                case "Game.PlayTutorial": Story.PlayTutorial(RaceManager.Active?.Event); return true;
                case "Game.ChallengeComplete":
                case "Game.ChallengeCompleted": Debug.Log($"NFSMW lua: Drive: {name}"); return true;
                case "Game.NotifySpeedTrapTriggered":
                    {
                        // 0x60A7B0: in a race the camera's record; in the open world GSpeedTrap::NotifyTriggered completes it
                        var trap = args[1] as GInstance;
                        int racer = LuaGameHost.RacerIndex(args[2]);
                        if (race != null && racer > 0) race.SpeedTrapTriggered(racer, Pt(trap), F(args[3]));
                        else if (trap != null) GameSession.CompleteSpeedTrapByScript(trap.Name, F(args[3]));
                        return true;
                    }

                // ---- cops and the world --------------------------------------------------------------------------------
                case "Game.DetachCameraFromRacer": ChaseCamera.DetachCameraFromRacer(); return true;   // 0x611540 (epic pursuit)
                case "Game.SetChanceOfRain": Core.Precipitation.SetChanceOfRain(F(args[0])); return true;   // Game_SetChanceOfRain 0x6054A0
                case "Game.SetCopsEnabled": if (police) { police.Enabled = (bool)args[0]; if ((bool)args[0]) police.NoNewPursuits = false; } return true;
                case "Game.ForcePursuitStart": if (police) police.ForcePursuit(Mathf.Clamp((int)args[0], 1, 5)); return true;
                case "Game.NoNewPursuitsOrCops": if (police) police.NoNewPursuits = true; return true;
                case "Game.ResetCopsForRestart": if (police) police.ResetCopsForRestart(true); return true;
                case "Game.PreventPlayerBeingBusted": if (police) police.PreventBusted(0.5f); return true;
                case "Game.SpawnCop":
                    {
                        // 0x60A670 (marker, vehicle, InPursuit, RoadBlock): with a marker position and direction and a
                        // vehicle name, ICopMgr::LockoutCops(false) then AICopManager::SpawnCop (0x439DD0: queued; the cop
                        // pursues, holds a roadblock, or patrols — AICopManager.cpp TrySpawnCop)
                        var p = Pt(args[0]);
                        if (police == null || p == null || string.IsNullOrEmpty(args[1] as string)) return true;
                        float a = p.Rotation * Mathf.Deg2Rad;
                        police.SpawnScriptedCop(Coords.ToUnity(p.Position), new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)), args[1] as string,
                            args[2] is bool pursue && pursue, args[3] is bool roadBlock && roadBlock);
                        return true;
                    }
                case "Game.SpawnCharacter": ScriptedCharacters.Spawn(args[0] as GInstance, Pt(args[1]), Pt(args[2]), F(args[3])); return true;
                case "Game.UnspawnCharacter": ScriptedCharacters.Unspawn(args[0] as GInstance); return true;
                case "Game.SetTrafficSpeed": ScriptedCharacters.SetSpeed(args[0] as GInstance, F(args[1]), F(args[2])); return true;
                case "Game.SendCharacterStimulus": ScriptedCharacters.SendStimulus(args[0] as GInstance, args[1] as string); return true;   // 0x60A6E0
                case "Game.JackKnife": ScriptedCharacters.JackKnife(args[0] as GInstance); return true;   // 0x605030
                case "Game.ShowGPS": if (!(args[0] is bool on && on)) GpsNavigation.Disengage(); return true;
                case "Game.NavigatePlayerTo": { var p = Pt(args[0]); if (p != null) GpsNavigation.Engage(Coords.ToUnity(p.Position), F(args[2])); return true; }
                case "Game.WarpToMarkerWhenRoaming": _warpWhenRoaming = Pt(args[0]); return true;
                case "Game.ResetTrigger": LuaFlow.Triggers?.Reset(args[0] as GInstance); return true;
                // Game.ShowTriggerIcon / HideTriggerIcon (0x604D20 / 0x604D30 → GTrigger 0x5E2D10 / HideIcon 0x5E2D30): the trigger's
                // GIcon (+0xC0) gets / loses flags 1 | 2 (map, world) — basicrace's prerace / racing (the speedtrap race's next trap)
                case "Game.ShowTriggerIcon": { var ic = LuaFlow.Triggers?.Triggers.Get(args[0] as GInstance)?.Icon; if (ic != null) ic.Flags |= GIcon.FlagOnMap | GIcon.FlagInWorld; return true; }
                case "Game.HideTriggerIcon": { var ic = LuaFlow.Triggers?.Triggers.Get(args[0] as GInstance)?.Icon; if (ic != null) ic.Flags &= ~(GIcon.FlagOnMap | GIcon.FlagInWorld); return true; }

                // ---- presentation ---------------------------------------------------------------------------------------
                case "HUD.ShowMessage": Systems.HudMessage?.Invoke(Label(args[0] as string)); return true;
                case "HUD.ShowTimeExtension": Systems.HudMessage?.Invoke($"+{F(args[0]):0}"); return true;
                case "Debug.ShowScreenMessage": Systems.HudMessage?.Invoke(args[0] as string); return true;
                case "Camera.SetGenericCamera":
                    // ECinematicMoment(group, track, 0) for the camera director (LuaGameHost posts ICECameraFinished after the
                    // track's length) [ours: the event is a hook for the camera code]
                    CinematicMoment?.Invoke(args[0] as string, args[1] as string);
                    return true;
                case "RaceStatus.NotifyScriptWhenLoaded":
                    // GRaceStatus::NotifyScriptWhenLoaded (0x5EEA60): mScriptWaitingForLoad = 1 (EFadeScreenOn(false) when
                    // something still loads); MLoadingComplete follows from GRaceStatus::Update — LuaGameHost — whether or
                    // not a race is there
                    return true;
                case "Game.Fade": StoryCard.FadeOut(1.5f, null); return true;
                case "NIS.Play":
                    {
                        // (marker, scene, 'Intro' / 'Outro', camera track, …): the race's intro while the race waits for it,
                        // else the scene at the marker
                        string scene = args[1] as string, kind = args[2] as string;
                        if (race != null && kind == "Intro" && !race.Over) { race.PlayIntroNis(); return true; }
                        var p = Pt(args[0]);
                        if (!string.IsNullOrEmpty(scene)) NisPlayer.PlayAtMarker(scene, p, null);
                        return true;
                    }
            }

            // ---- the race (GRaceStatus) -----------------------------------------------------------------------------
            if (name == "Game.AbandonRace")
            {
                // `abandoned`: Game.AbandonRace; the race may be gone already (the losing screen's Quit)
                if (race != null && !race.Over) race.AbandonRace();
                else
                {
                    var ev = Ev(rt.CurrentActivity?.Instance);
                    if (ev != null) GameSession.AbandonRace(ev);
                    if (RaceManager.Active != null && RaceManager.Active.Event == ev) RaceManager.Active.AbandonRace();
                }
                return true;
            }
            if (name == "RaceStatus.SetRoaming")
            {
                if (RaceManager.Active != null && RaceManager.Active.Driven) RaceManager.Active.SetRoaming();
                if (police && GameSession.Mode == GameMode.Career) police.NoNewPursuits = false;
                GameSession.Profile?.Save();   // SetRoaming's autosave (EAutoSave)
                return true;
            }
            if (race == null) return LuaDriveTable.Of(b) == LuaDriveTable.Kind.NoOp;
            int Racer(object o) => LuaGameHost.RacerIndex(o);
            switch (name)
            {
                case "Game.InitRacers": race.InitRacers(Pt(args[0])); return true;
                case "Game.SetRacerGoal": race.SetRacerGoal((int)args[0], args[1] as GInstance, Pt(args[1])); return true;
                case "Game.NotifyCheckpointReached": race.CheckpointReached(Racer(args[0]), (int)args[1]); return true;
                case "Game.NotifyLapFinished": race.LapFinished(Racer(args[0]), (int)args[1]); return true;
                case "Game.NotifyRaceFinished": race.RaceFinished(Racer(args[0])); return true;
                case "Game.KnockoutRacer":
                case "Game.TotalRacer": race.Knockout(Racer(args[0])); return true;
                case "Game.AwardCash":
                case "Game.AwardPoints": race.AwardPoints(Racer(args[0]), F(args[1])); return true;
                case "RaceStatus.AwardBonusTime": race.AwardBonusTime(F(args[0])); return true;
                case "Game.StartRace": race.StartRace(); return true;
                case "Game.StartRaceTimers": race.StartRaceTimers(); return true;
                case "Game.SetAllStaging": race.SetAllStaging((bool)args[0]); return true;
                case "Game.EnterPostRaceFlow": race.EnterPostRaceFlow(); return true;
                case "Game.ShowWinningPostRaceScreen": race.ShowPostRaceScreen(true); return true;
                case "Game.ShowLosingPostRaceScreen": race.ShowPostRaceScreen(false); return true;
                case "Game.ShowRaceOverSummary": race.ShowRaceOverSummary(); return true;
                case "Game.WarpPlayerToTrigger": { var p = Pt(args[0]); if (p == null) return false; race.WarpPlayerTo(p); return true; }
                case "Game.SaveStartPositions": race.SaveStartPositions(); return true;
                case "Game.RestoreStartPositions": race.RestoreStartPositions(); return true;
                case "Game.ForceAIControl": race.SetAIControl((int)args[0], true); return true;
                case "Game.ClearAIControl": race.SetAIControl((int)args[0], false); return true;
                case "Game.SabotageEngine":
                    // 0x60AB60: EngineRacer::Sabotage(t) — the engine shakes, then blows (LuaGameHost posts NotifyEngineBlown)
                    if (args[0] is PlayerCar sc && sc) sc.Sabotage(Mathf.Max(0.01f, F(args[1])));
                    return true;
                case "Game.BlowEngine": if (args[0] is PlayerCar bc && bc) bc.Blow(); return true;
            }
            return LuaDriveTable.Of(b) == LuaDriveTable.Kind.NoOp;
        }

        /// <summary>E* events in Drive mode.</summary>
        public static bool Event(string name, object[] args)
        {
            int Id(int i) => args.Length > i && args[i] is float f ? (int)f : -1;
            switch (name)
            {
                case "EAddSMS": if (Id(0) >= 0) Sms.Show(Id(0)); return true;
                case "ECellCall": if (Id(0) >= 0) { Sms.Show(Id(0), true); NfsMw.Audio.SpeechAI.SMSCellCall(Id(0)); } return true;   // ~ECellCall 0x62B1CD → MiscSpeech::SMSCellCall
                case "EShowRaceCountdown": Race?.StartCountdown(); return true;
                case "EShowMessageScreen":
                    // SixDaysLater.fng with the message (the D-Day time cards: DDAY_TIMELAPSE_<id + 1>); the host posts
                    // NotifyMessageDone once the screen has gone (LuaGameHost.Update) [?: other ids]
                    if (Id(0) >= 0) UI.FrontEnd.Create().Push(new UI.SixDaysLaterScreen(Id(0)));
                    return true;
                case "EPlayRaceMovie": if (args.Length > 0 && args[0] is string m && m.Length > 0) StoryCard.Run(null, m, null); return true;
                case "EPlayRaceNIS":
                    {
                        string scene = args.Length > 1 ? args[1] as string : null;
                        var p = args.Length > 0 && args[0] is GInstance g ? LuaTriggers.PointOf(g.Record) : null;
                        if (!string.IsNullOrEmpty(scene)) NisPlayer.PlayAtMarker(scene, p, null);
                        return true;
                    }
                case "EFadeScreenNoLoadingBarOn": StoryCard.FadeHold(true); return true;
                case "EFadeScreenNoLoadingBarOff": StoryCard.FadeHold(false); return true;
                case "EBailPursuit": if (Systems.Police && Systems.Police.InPursuit) Systems.Police.BailPursuit(); return true;
                case "EShowPostPursuit": return true;   // LuaGameHost posts FlowReadyForOutro when no screen is up
                case "ETerminateMusic": return true;
                // EResetProps (basicrace intronis; ~EResetProps 0x624930 → World_RestoreProps 0x74D320): the smashed props come
                // back and RestorePursuitBreakerIcons(-1)
                case "EResetProps": Smackable.RestoreAll(); GameIcons.RestorePursuitBreakerIcons(); return true;
            }
            return LuaDriveTable.Of(name) == LuaDriveTable.Kind.NoOp;
        }

        /// <summary>Drive's per-frame side (LuaGameHost.Update): the queued roaming warp; GManager::UnspawnUselessCharacters.</summary>
        static bool _gameWon;

        public static void Update()
        {
            // EQuitToFE's game-won branch (0x6240C2-0x624139): the final epic chase 1.8.1 won (score flag 2) → GameWonScreen
            // [the port has no EQuitToFE after the chase's script ends: its `done` stands for it; run a frame later, outside the script]
            if (_gameWon && RaceManager.Active == null && !NisPlayer.Active && !LuaFlow.FrontEndOpen) { _gameWon = false; UI.GameWonScreen.Begin(); return; }
            ScriptedCharacters.UnspawnUseless();
            if (_warpWhenRoaming != null && RaceManager.Active == null && !GameFlow.RaceLoading && GameBootstrap.Instance && GameBootstrap.Instance.Player && !LuaFlow.FrontEndOpen)
            {
                // Game.WarpToMarkerWhenRoaming (0x605930) → GManager::WarpToMarker(marker, resetProps 0) once roaming (GRaceStatus::Update
                // 0x602CF4): EFadeScreenOn(false), Sim::SetStream(marker), +0x2B4 pending; GManager::NotifyWorldService (0x5EBB10, from
                // World_Service) when the streamer is done: IVehicle SetPosition / ResetToPosition of the first player, GPS_Disengage,
                // the camera resets (0x92C674 +4(1), 0x92C67C +8 / +0x2C / +0x34), EFadeScreenOff (World_RestoreProps only with resetProps:
                // the rap sheet's jumps). [the port's PlacePlayer streams and places in one go; no fade]
                var p = _warpWhenRoaming;
                _warpWhenRoaming = null;
                GameBootstrap.Instance.PlacePlayer(p.Position, p.Rotation);
                GpsNavigation.Disengage();
            }
        }

        /// <summary>A state change in Drive: a D-Day race that ends.</summary>
        public static void OnStateChanged(GActivity a, ScriptState s)
        {
            if (s == null || s.Name != "done" || GameSession.Career == null) return;
            var ev = GameSession.Career.EventFor(a.Instance.Key);
            if (ev != null && UI.GameWonScreen.IsFinalEpicChase(ev.EventId) && RaceDatabase.CheckFlags(ev, RaceIndex.CompletedCareer)) { _gameWon = true; return; }
            if (ev == null || !ev.DDayRace) return;
            bool abandoned = LuaFlow.Runtime != null && LuaFlow.Runtime.StateOf(a).Get("Abandoned").IsTrue;
            if (abandoned) Story.PrologueRaceAbandoned(ev);
            else Story.OnDDayRaceOver(ev);
        }

        public static void Reset()
        {
            _warpWhenRoaming = null;
            Later.Clear();
            _gameWon = false;
        }

        /// <summary>Is a Drive call a no-op in the exe (empty functions on PC)?</summary>
        public static bool IsEmptyOnPc(LuaBinding b) => LuaDriveTable.Of(b) == LuaDriveTable.Kind.NoOp;
    }
}
