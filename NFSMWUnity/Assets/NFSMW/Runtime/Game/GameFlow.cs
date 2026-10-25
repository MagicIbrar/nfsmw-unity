using System;
using System.Collections;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Transitions between the front end, free roam, career and races, after the exe's Sim activities
    /// (refs/nfsmw src/Speed/Indep/Src/Sim/Activities, speed.exe 0x6F9800.. 0x6FAEC0):
    ///   * QuickGame (ctor 0x6F9800; quick race, challenge, LAN, free roam) and CareerGame (0x6F9B10, a QuickGame with
    ///     nothing of its own) load the gameplay through QuickGame::eState (OnManageState 0x6F5140 / OnBeginState
    ///     0x6F4EA0): LOADING_WORLD → PLAYERS (CreatePlayers, CreateCars: the player's car) → PLAYER_AUDIO →
    ///     PLAYER_MODELS → ACTIVITIES (AvoidableManager, AICopManager, AITrafficManager, AIParkedCarSpawner, PathFinder,
    ///     GameplayActivity, Gps, NISListenerActivity) → UI → OPPONENT_AUDIO → OPPONENT_MODELS → WAIT_FOR_USER_INPUT →
    ///     NIS → READY. Here: EnterWorld is world + players + models, Systems.Ensure is the AI/cop/traffic managers,
    ///     BeginGameplay is GameplayActivity, the race (RaceManager) is the startup race's opponents and NIS.
    ///   * GameplayActivity (ctor 0x6F9EA0, Construct 0x6F9FD0): GManager::PreBeginGameplay (0x6035F0: the startup
    ///     race's time of day), GRaceStatus::Init, the Lua runtime, bindery and post office, GManager::BeginGameplay
    ///     (0x603500: CreateGameplayObjects, the startup race's activity, ConnectRuntimeInstances, StartActivities,
    ///     the engage / speed-trap icons, the bin barriers, MEnteringGameplay); OnTask (0x6EAAE0) posts MNotifySimTick and
    ///     runs GManager::Update every sim frame; the destructor ends gameplay and shuts the Lua down.
    ///   The exe runs GameplayActivity — and so the scripts — in quick races too; the port's LuaFlow runs them in career
    ///   only [ours], and keeps the world loaded between a career race and free roam (no reload per engage).
    /// </summary>
    public static class GameFlow
    {
        static GameBootstrap Boot => GameBootstrap.Instance;

        // Rosewood, outside the first safehouse area
        static readonly Vec3 CareerStart = new Vec3(679.4f, 2288.0f, 153.9f);

        /// <summary>QuickGame::eState (QuickGame.h; the state name table at 0x901AF0).</summary>
        public enum LoadState
        {
            None = -1, LoadingWorld = 0, LoadingPlayers, LoadingPlayerAudio, LoadingPlayerModels, LoadingActivities, LoadingUI,
            LoadingOpponentAudio, LoadingOpponentModels, LoadingWaitForUserInput, LoadingNis, Ready,
        }

        /// <summary>The Sim activity that runs gameplay: none (front end), QuickGame or CareerGame.</summary>
        public enum GameKind { None, QuickGame, CareerGame }

        /// <summary>
        /// GameFlowManager::GameFlowState (Misc/GameFlow.hpp): LOADING_FRONTEND (BeginGameFlowLoadingFrontEnd) → IN_FRONTEND
        /// (EndGameFlowLoadingFrontEnd) → UNLOADING_FRONTEND → LOADING_REGION (RegionLoader::BeginLoading) → LOADING_TRACK
        /// (TrackLoader) → RACING (FinishedGameLoading) → UNLOADING_TRACK / UNLOADING_REGION (BeginGameFlowUnloadTrack) → the
        /// front end again (destination 0) or LoadTrack (1, ReloadTrack).
        /// </summary>
        public enum FlowState { None = 0, LoadingFrontend = 1, UnloadingFrontend = 2, InFrontend = 3, LoadingRegion = 4, LoadingTrack = 5, Racing = 6, UnloadingTrack = 7, UnloadingRegion = 8, ExitDemoDisc = 9 }

        /// <summary>TheGameFlowManager.CurrentGameFlowState.</summary>
        public static FlowState Flow { get; private set; } = FlowState.InFrontend;
        /// <summary>GameFlowManager::IsInFrontEnd / IsRacing / IsLoading (0x50DE50) / IsPaused (0x64B680: Sim::GetState() == STATE_IDLE).</summary>
        public static bool IsInFrontEnd => Flow == FlowState.InFrontend;
        public static bool IsRacing => Flow == FlowState.Racing;
        public static bool IsLoading => Flow == FlowState.LoadingRegion || Flow == FlowState.LoadingTrack;
        public static bool IsPaused => Flow == FlowState.Racing && (Time.timeScale <= 0f || SimulationPaused);

        static void SetFlow(FlowState f)
        {
            if (Flow == f) return;
            Flow = f;
            Debug.Log($"NFSMW flow: GameFlowManager {f}");
        }

        public static GameKind Game { get; private set; }
        public static LoadState State { get; private set; } = LoadState.None;
        /// <summary>GameplayActivity exists (GManager::GetInGameplay 0x5DD8D0).</summary>
        public static bool InGameplay { get; private set; }
        /// <summary>A race is loading (StartRace until RaceManager.Begin) [ours: LuaDrive must not start it twice]. A loading
        /// coroutine that died with its GameBootstrap (tests destroy the scene) does not count.</summary>
        public static bool RaceLoading
        {
            get => _raceLoading && _loadingBoot && _loadingBoot == Boot && Time.realtimeSinceStartup - _loadingSince < 300f;
            private set { _raceLoading = value; _loadingBoot = Boot; _loadingSince = Time.realtimeSinceStartup; }
        }
        static bool _raceLoading;
        static GameBootstrap _loadingBoot;
        static float _loadingSince;
        static RaceEvent _loadingEvent;
        /// <summary>GameplayActivity's constructor / destructor (GManager::BeginGameplay / EndGameplay) [ours: hooks].</summary>
        public static event Action GameplayBegan, GameplayEnded;

        static void SetState(LoadState s)
        {
            if (State == s) return;
            State = s;
            if (s == LoadState.LoadingWorld || s == LoadState.Ready) Debug.Log($"NFSMW flow: {Game} {s}");
            if (s == LoadState.Ready) SetFlow(FlowState.Racing);   // WaitForSimulation → FinishedGameLoading: GAMEFLOW_STATE_RACING
        }

        /// <summary>QuickGame / CareerGame construction: the loading state machine starts at LOADING_WORLD.</summary>
        static void BeginLoad()
        {
            // BeginGameFlowUnloadingFrontEnd → LoadTrack: RegionLoader (the region's bundles), TrackLoader (the world models), BeginWorldLoad (Sim::Init(activity))
            if (Flow == FlowState.InFrontend) SetFlow(FlowState.UnloadingFrontend);
            SetFlow(FlowState.LoadingRegion);
            SetFlow(FlowState.LoadingTrack);
            Game = GameSession.Mode == GameMode.Career ? GameKind.CareerGame : GameKind.QuickGame;
            SimRandom.Get().Reset();   // Sim::Init (0x6FC417): Sim::GetRandom().Reset()
            SetState(LoadState.LoadingWorld);
        }

        /// <summary>RACESTATE_LOADING_ACTIVITIES: the managers (Systems) and GameplayActivity (GManager::BeginGameplay).</summary>
        static void LoadActivities()
        {
            SetState(LoadState.LoadingActivities);
            Systems.Ensure();
            if (!InGameplay)
            {
                InGameplay = true;
                try { GameplayBegan?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
            }
        }

        /// <summary>GameplayActivity's destructor (GManager::EndGameplay, the Lua runtime shut down): back to the front end.</summary>
        public static void EndGameplay()
        {
            if (InGameplay)
            {
                InGameplay = false;
                try { GameplayEnded?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
            }
            Game = GameKind.None;
            State = LoadState.None;
            RaceLoading = false;
            SimRandom.Get().Reset();   // Sim::Shutdown (0x6FABD2): Sim::GetRandom().Reset()
            UpdatePause();
            // BeginGameFlowUnloadTrack(0): TrackLoader::Unload (SkipFE = 0, ResetRenderEggs, gEasterEggs.ClearNonPersistent) and
            // RegionLoader::Unload, then GameFlowManager::LoadFrontend → IN_FRONTEND
            SetFlow(FlowState.UnloadingTrack);
            SetFlow(FlowState.UnloadingRegion);
            Controls.EasterEggs.Instance.ClearNonPersistent();
            SetFlow(FlowState.LoadingFrontend);
            SetFlow(FlowState.InFrontend);
        }

        // ---- RequestPauseSimulation / the track streamer's loading bar ------------------------------------------------

        static readonly System.Collections.Generic.HashSet<string> PauseRequests = new System.Collections.Generic.HashSet<string>();
        static float _loadingBarSince = -1;

        /// <summary>FEManager::RequestPauseSimulation (0x516EE0) holds the sim (GameBootstrap keeps Time.timeScale at 0).</summary>
        public static bool SimulationPaused => PauseRequests.Count > 0;

        public static void RequestPauseSimulation(string who) { if (who != null) PauseRequests.Add(who); }
        public static void RequestUnPauseSimulation(string who) { if (who != null) PauseRequests.Remove(who); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void HookLoadingBar()
        {
            WorldStreamer.LoadingBarChanged -= OnLoadingBar;
            WorldStreamer.LoadingBarChanged += OnLoadingBar;
        }

        /// <summary>
        /// The main loop's loading-bar check (0x64B770): in the running sim (Sim::GetState() == 3), TrackStreamer::
        /// CheckLoadingBar turning on → RequestPauseSimulation("TrackStreamerLoadingBar") and EFadeScreenOn(true) (the fade
        /// with the loading bar); turning off → EFadeScreenOff(0x16A259) and RequestUnPauseSimulation.
        /// </summary>
        static void OnLoadingBar(bool need)
        {
            const string who = "TrackStreamerLoadingBar";
            if (need)
            {
                if (PauseRequests.Contains(who) || !InGameplay || State != LoadState.Ready || Boot == null || !Boot.Ready) return;
                Debug.Log("NFSMW flow: track streamer loading bar on: simulation paused");
                RequestPauseSimulation(who);
                StoryCard.LoadingBar(true);
                _loadingBarSince = Time.realtimeSinceStartup;
            }
            else if (PauseRequests.Contains(who))
            {
                Debug.Log($"NFSMW flow: track streamer loading bar off after {Time.realtimeSinceStartup - _loadingBarSince:0.0} s");
                StoryCard.LoadingBar(false);
                RequestUnPauseSimulation(who);
            }
        }

        /// <summary>A loading bar that never clears does not hold the game forever (30 s) [ours]; gameplay ending drops it.</summary>
        internal static void UpdatePause()
        {
            if (PauseRequests.Contains("TrackStreamerLoadingBar") && (!InGameplay || Time.realtimeSinceStartup - _loadingBarSince > 30f))
            {
                Debug.Log("NFSMW flow: loading bar released (gameplay ended or 30 s)");
                OnLoadingBar(false);
            }
        }

        public static void StartCareer() => Story.Resume();

        public static void StartFreeRoam()
        {
            GameSession.Mode = GameMode.FreeRoam;
            if (Boot.Ready && Boot.Player != null)
            {
                RespawnPlayerCar();
                FrontEnd.Instance?.CloseAll();
                return;
            }
            Enter(CareerStart, null);
        }

        public static void StartRace(RaceEvent ev, bool skipMovie = false)
        {
            if (ev?.Start == null) return;
            if (RaceLoading && _loadingEvent == ev) return;   // already on its way (two callers in one frame) [ours]
            if (GameSession.Mode == GameMode.FrontEnd) GameSession.Mode = GameMode.QuickRace;
            // a pursuit challenge has no race; in career Drive the scripts run every career event as a race (the epic
            // pursuit is a `challengerace`: its script spawns the cops and decides the end)
            bool pursuit = ev.Type == RaceType.Pursuit && !GameSession.ScriptsDrive;
            // the IntroMovie plays before the race loads (as the C# flow always did [?: the exe's FE]); the script plays it
            // itself only when the race has no IntroNIS (basicrace intro_message → intromovie, e.g. the epic pursuit)
            if (GameSession.ScriptsDrive && string.IsNullOrEmpty(ev.IntroNis)) skipMovie = true;
            // a race started from in-game while roaming (the race sheet / rival screen / engage): GManager::StartRaceFromInGame
            if (GameSession.Mode == GameMode.Career && RaceManager.Active == null && Boot != null && Boot.Ready) Lua.LuaFlow.OnStartRaceFromInGame(ev);
            RaceLoading = true;
            _loadingEvent = ev;
            // RaceStarter (sub_57EA20 / sub_57EB40 / sub_57EBE0 0x57EAB7): the race settings' TrackNumber (0x904FB8) = 0x7D0 (2000,
            // the one MW track L2RA; StartSkipFERace copies SkipFETrackNumber 0x8F86A4); InstantiateAnimTree filters by it % 100
            WorldAnimations.TrackNumber = 2000;
            Boot.StartCoroutine(pursuit ? PursuitRoutine(ev) : RaceRoutine(ev, skipMovie));
        }

        /// <summary>A pursuit challenge: rolling start at the start grid, cops on the event's spawn points.</summary>
        static IEnumerator PursuitRoutine(RaceEvent ev)
        {
            var fe = FrontEnd.Create();
            fe.CloseAll();
            ShowLoading(fe);
            PlayerCar.ResetTime();
            Time.timeScale = 1f;
            BeginLoad();
            yield return Boot.EnterWorld(Coords.ToUnity(ev.Start.Position), ev.Start.Rotation);
            HideLoading(fe);
            LoadActivities();
            RaceLoading = false;
            Systems.Police.BeginChallenge(ev, Boot.Player);
            SetState(LoadState.Ready);
        }

        static IEnumerator RaceRoutine(RaceEvent ev, bool skipMovie)
        {
            bool movie = !skipMovie;
            if (movie) StoryCard.Run(null, ev.IntroMovie, () => movie = false);
            while (movie) yield return null;
            var fe = FrontEnd.Create();
            fe.CloseAll();
            ShowLoading(fe);
            PlayerCar.ResetTime();
            Time.timeScale = 1f;
            BeginLoad();
            yield return Boot.EnterWorld(Coords.ToUnity(ev.Start.Position), ev.Start.Rotation);
            HideLoading(fe);
            LoadActivities();
            // the startup race: GManager::BeginGameplay's CreateRaceActivity; InitRacers' opponents, their models, the NIS
            SetState(LoadState.LoadingOpponentModels);
            RaceLoading = false;
            RaceManager.Begin(ev);
            SetState(LoadState.Ready);
        }

        static void Enter(Vec3 at, float? heading)
        {
            Boot.StartCoroutine(EnterRoutine(at, heading));
        }

        static IEnumerator EnterRoutine(Vec3 at, float? heading)
        {
            var fe = FrontEnd.Create();
            fe.CloseAll();
            ShowLoading(fe);
            PlayerCar.ResetTime();
            Time.timeScale = 1f;
            BeginLoad();
            yield return Boot.EnterWorld(Coords.ToUnity(at), heading);
            HideLoading(fe);
            LoadActivities();
            SetState(LoadState.Ready);
        }

        /// <summary>
        /// Free roam at a point (career resume, the car lot's way out): CareerGame / QuickGame loading, then GameplayActivity
        /// (Story's career entry uses this) [ours: one routine for both].
        /// </summary>
        public static IEnumerator EnterWorldRoutine(Vec3 at, float? heading)
        {
            BeginLoad();
            yield return Boot.EnterWorld(Coords.ToUnity(at), heading);
            LoadActivities();
            SetState(LoadState.Ready);
        }

        static FEScreen _loading;

        static void ShowLoading(FrontEnd fe)
        {
            _loading = fe.Push(new LoadingScreen());
        }

        static void HideLoading(FrontEnd fe)
        {
            if (!LoadingFlow.FinishLoading(fe, _loading)) { _loading = null; return; }   // [round 9 FE hook] DelayWaitForLoadingScreen: a wait tip holds the screen
            if (fe.Top == _loading) fe.Pop();
            _loading = null;
        }

        /// <summary>Rebuilds the player car after a car change, paint or upgrade, keeping its place.</summary>
        public static void RespawnPlayerCar()
        {
            if (Boot == null || Boot.Player == null) return;
            var t = Boot.Player.transform;
            var pos = t.position;
            float heading = Mathf.Atan2(t.forward.z, t.forward.x) * Mathf.Rad2Deg;
            Boot.SpawnPlayer(pos, heading);
        }

        /// <summary>Game.JumpToSafeHouse: the safehouse menu, then back out at the last safehouse used.</summary>
        public static void JumpToSafeHouse()
        {
            RaceManager.Active?.Abort();
            Systems.Police?.Clear(true);
            var fe = FrontEnd.Create();
            fe.CloseAll();
            var p = GameSession.Profile;
            var at = p != null && p.HasSpawn ? new RacePoint { Position = new Vec3(p.SpawnX, p.SpawnY, p.SpawnZ), Rotation = p.SpawnRot } : null;
            Story.ZoneRespawn = at;
            if (at != null) Boot.PlacePlayer(at.Position, at.Rotation);
            fe.Push(new CareerMenuScreen());
        }

        /// <summary>EQuitToFE(GARAGETYPE_MAIN_FE): gameplay ends (GameplayActivity's destructor), the main menu.</summary>
        public static void QuitToMainMenu()
        {
            GameSession.EndLan();
            if (NisPlayer.Active) NisPlayer.Active.Finish();
            if (RaceManager.Active) RaceManager.Active.Abort();
            Systems.Shutdown();
            GameSession.Profile?.Save();
            GameSession.Mode = GameMode.FrontEnd;
            EndGameplay();
            Boot.UnloadWorld();
            var fe = FrontEnd.Create();
            fe.CloseAll();
            fe.Push(new MainMenuScreen());
        }

        public static void Quit()
        {
            GameSession.Profile?.Save();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }

    /// <summary>Loading screen shown while the world streams in: Loading.fng / Loading_Tips.fng (UI/Safehouse/LoadingScreens.cs).</summary>
    public sealed class LoadingScreen : FEScreen
    {
        string _package;
        public override string Package => _package ??= LoadingFlow.Package();
        public override void Open() => LoadingFlow.Open(View);
        public override void Close() => LoadingFlow.Close();   // [round 9 FE hook] LoadingScreen dtor sound states
        public override void Tick(MenuInput input) => LoadingFlow.Tick(View, input);   // [round 9 FE hook] LoadingTips SCREEN_TICK / accept
    }

}
