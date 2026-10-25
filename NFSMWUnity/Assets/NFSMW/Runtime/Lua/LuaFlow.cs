using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;
using NfsMw.Game.Gameplay;
using UnityEngine;

namespace NfsMw.Game.Lua
{
    /// <summary>Who runs the career and race flow: the hand-ported C# only, the game's Lua alongside it (recorded), or the Lua.</summary>
    public enum LuaFlowMode
    {
        /// <summary>The Lua stategraphs do not run.</summary>
        Off,
        /// <summary>
        /// The stategraphs run alongside the hand-ported flow; their game-changing calls are recorded instead of executed
        /// and compared with what the C# flow does ("NFSMW lua: divergence …").
        /// </summary>
        Shadow,
        /// <summary>
        /// The stategraphs run the career (specs/lua_hooks.md §4.2): Action bindings execute through LuaDrive, the race
        /// internals through ILuaRaceControl on RaceManager, and the hand-ported career triggers and race rules step aside.
        /// </summary>
        Drive,
    }

    /// <summary>
    /// The game's own career and race scripts (Lua 5.0 stategraphs in gameplay.bin, specs/lua_hooks.md) on the C# Lua VM
    /// (Core/Lua). This drives the stategraph runtime the way GManager does: the bin activity race_bin_NN for the current
    /// Blacklist position and the AutoStart activities (engage triggers, menu gates, speed traps, milestones) in career
    /// free roam, the race activity when a race starts (Activity.Run(TargetActivity) after an engage), the triggers
    /// (LuaTriggers) and one tick per frame (GManager::Update 0x6036C0 → the GEventTimers, NotifySimTick).
    /// It needs no call from other code; LuaFlow.Observe / LuaFlow.Post are optional taps.
    /// </summary>
    public static class LuaFlow
    {
        /// <summary>
        /// The mode a session starts in [ours]; `-lua off|shadow|drive` on the command line overrides it. Drive since the
        /// harness's `lua drive` ran the prologue, all 169 career races and the bin flow 15 → 1 on the scripts (won, lost
        /// and restarted, lost and quit); Shadow and Off remain for comparison and as the fallback.
        /// </summary>
        public const LuaFlowMode DefaultMode = LuaFlowMode.Drive;
        public static LuaFlowMode Mode = FromCommandLine();
        /// <summary>Log every binding call and event (otherwise only errors, divergences and activity starts).</summary>
        public static bool Verbose;
        /// <summary>Drive mode's race adapter: the RaceManager running a driven race (set by OnRaceBegin).</summary>
        public static ILuaRaceControl Race;

        public static StategraphRuntime Runtime { get; private set; }
        public static LuaGameHost Host { get; private set; }
        public static LuaLedger Ledger { get; private set; }
        public static LuaTriggers Triggers { get; private set; }
        /// <summary>The last binding calls and events, as text (what Shadow recorded) [ours].</summary>
        public static readonly List<string> Calls = new List<string>();
        /// <summary>The event of the race the C# flow is running (Game.InitRacers' event in Drive).</summary>
        public static RaceEvent CurrentRaceEvent => RaceManager.Active?.Event;

        static bool _failed;
        static int _bin = -1;
        /// <summary>GManager's object-state maps and timers (GameplayStateStore): they outlive a runtime (GManager is not
        /// GameplayActivity) and belong to the loaded career — LoadGameplayData (0x5F81D0) replaces them when another
        /// profile is loaded, SaveGameplayData (0x5F7410) writes the persistent ones with every career save.</summary>
        static GameplayStateStore _store;
        static Profile _storeProfile;
        static bool _roaming;
        static Profile _profile;
        static RaceEvent _lastRace;
        static bool _resetRequested;
        /// <summary>GRaceStatus mRefreshBinAfterRace (+0x19A5): the bin activities suspended for a race of another bin come back when roaming again.</summary>
        static bool _refreshBinAfterRace;

        /// <summary>GameplayActivity's destructor (GameFlow.EndGameplay): the Lua runtime shuts down — on the next frame,
        /// since a script's own call (Game.JumpToCarLot) can end gameplay [ours].</summary>
        internal static void OnGameplayEnded() => _resetRequested = true;

        static LuaFlowMode FromCommandLine()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i].Equals("-lua", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(args[i + 1], true, out LuaFlowMode m)) return m;
            }
            catch (Exception) { }
            return DefaultMode;
        }

        /// <summary>
        /// The exe's QuickGame creates GameplayActivity — and with it the Lua — for quick races too (lua_hooks.md §4.3): the
        /// startup race's activity is the only one run (StartActivities with a startup race). Off: the C# rules run quick
        /// races as before [ours: a switch; `-lua careeronly` on the command line].
        /// </summary>
        public static bool QuickRaceScripts = !HasArg("careeronly");

        /// <summary>The game modes whose GameplayActivity runs the stategraphs: career, and quick race (QuickGame) when enabled.</summary>
        public static bool ScriptedMode => GameSession.Mode == GameMode.Career || QuickRaceScripts && GameSession.Mode == GameMode.QuickRace;

        static bool HasArg(string a)
        {
            try { foreach (var x in Environment.GetCommandLineArgs()) if (x.Equals(a, StringComparison.OrdinalIgnoreCase)) return true; } catch (Exception) { }
            return false;
        }

        /// <summary>
        /// The scripts run the career now: Drive mode, career (or quick race) gameplay, and the runtime is up. A runtime that
        /// failed hands the flow back to the C# rules (the next race runs on them) [ours].
        /// </summary>
        public static bool Drives => Mode == LuaFlowMode.Drive && ScriptedMode && Ensure();

        /// <summary>A C# observation for the Shadow comparison (optional tap), e.g. LuaFlow.Observe("SMS 15").</summary>
        public static void Observe(string record) { if (Host?.Ledger != null && Runtime != null) Host.Ledger.CSharp(Runtime.SimTime, record); }

        /// <summary>Post a message into the scripts (optional tap for events the polling does not see).</summary>
        public static void Post(uint message, params (string field, object value)[] fields) => Runtime?.Post(message, fields);

        /// <summary>Creates the runtime once the career data is loaded. False when it cannot run.</summary>
        public static bool Ensure()
        {
            if (Mode == LuaFlowMode.Off) return false;
            // the driver may be gone (play-mode tests destroy every root object), and a runtime from another profile is stale
            if (ScriptedMode) LuaFlowDriver.EnsureExists();
            if (Runtime != null && !ReferenceEquals(_profile, GameSession.Profile)) Reset();
            if (Runtime != null) return true;
            // the scripts are the career's and the quick race's flow: split screen / LAN / challenge runs have none [ours]
            if (_failed || GameSession.Career?.Db == null || !ScriptedMode) return false;
            try
            {
                var t0 = Time.realtimeSinceStartup;
                var data = GameplayScripts.Load(GameSession.Career.Db);
                HookProfile();
                LuaGameHost.HookCellCalls();
                Host = new LuaGameHost();
                Ledger = new LuaLedger();
                Runtime = new StategraphRuntime(data, Host);
                // GManager's saved activity states and timers (LoadGameplayData for a profile loaded from the save) and
                // bRandom re-seeded from the clock (SeedRandomNumber, Main.cpp)
                Runtime.Attach(StoreFor(GameSession.Profile));
                Runtime.Random.SetSeed(unchecked((uint)Environment.TickCount + 0x12345678u));
                Host.Rt = Runtime;
                Host.Ledger = Mode == LuaFlowMode.Shadow ? Ledger : null;   // Drive: one flow, nothing to compare
                Triggers = new LuaTriggers(Runtime);
                Host.Triggers = Triggers;
                _profile = GameSession.Profile;
                Runtime.StateChanged += (a, s) =>
                {
                    Debug.Log($"NFSMW lua: {a.Name} -> {s?.Name ?? "-"} at {Runtime.SimTime:0.0} s");
                    if (Mode == LuaFlowMode.Drive) LuaDrive.OnStateChanged(a, s);
                };
                Debug.Log($"NFSMW lua: {Mode}: {data.Handlers.Count} handlers, {data.States.Count} states loaded in {(Time.realtimeSinceStartup - t0) * 1000:0} ms ({Runtime.Errors} errors)");
                return true;
            }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogWarning("NFSMW lua: could not start: " + e);
                return false;
            }
        }

        /// <summary>A front-end screen is up (FrontEnd.Instance outlives its screens: IsOpen is what counts).</summary>
        public static bool FrontEndOpen => UI.FrontEnd.Instance != null && UI.FrontEnd.Instance.IsOpen;

        /// <summary>Run an activity by collection name (GManager / Activity.Run) [ours: also for tests].</summary>
        public static GActivity Run(string activity)
        {
            if (!Ensure()) return null;
            var g = Runtime.Instance(activity);
            if (g == null) return null;
            Debug.Log($"NFSMW lua: Activity.Run {activity}");
            return Runtime.Run(g);
        }

        /// <summary>
        /// The C# flow started a race (RaceManager.Begin): an engage prompt that is up accepts, and the race activity runs.
        /// In Drive this is where every race activity runs, also the ones a script asked for: Activity.Run of a race waits
        /// until the race is loaded (GRaceParameters::BlockUntilLoaded 0x6048A0; QuickGame's RACESTATE_LOADING_ACTIVITIES
        /// creates GameplayActivity after the players' cars).
        /// </summary>
        internal static void OnRaceBegin(RaceManager rm)
        {
            var ev = rm.Event;
            if (ev == null || Runtime == null) return;
            if (rm.Driven) Race = rm;
            var previous = _lastRace;
            _lastRace = ev;
            Host.Ledger?.CSharp(Runtime.SimTime, $"RaceStart {ev.Name}");
            // an engage prompt on screen accepts (AcceptEnterCareerEvent → Activity.Run(TargetActivity))
            if (Runtime.RunningActivities.Any(a => a.Current?.Name == "prompt")) { Runtime.Post(StategraphRuntime.AcceptEnterCareerEvent, ("SimTime", Runtime.SimTime)); Runtime.Pump(); }
            var g = Runtime.Instance(ev.Key);
            var act = g != null ? Runtime.Activity(g) : null;
            if (act == null || act.States.Count == 0) { if (rm.Driven) rm.ReleaseToCSharp(); return; }
            // one race at a time: a race script still running (one the C# flow left early) stops [ours]
            foreach (var old in Runtime.RunningActivities.Where(x => x != act && x.States.Any(s => s.Name == "racing")).ToList())
            {
                Debug.Log($"NFSMW lua: {old.Name} stopped in state {old.Current?.Name} (the C# flow started {g.Name})");
                Runtime.Suspend(old);
            }
            if (!act.Running || act.Current?.Name == "done")
            {
                // in the exe the engage dialog (or a script's PostRaceActivity) runs it: count it for the Lua side too
                Debug.Log($"NFSMW lua: Activity.Run {g.Name} (started by the C# flow)");
                Host.Ledger?.Lua(Runtime.SimTime, $"RaceStart {ev.Name}");
                Runtime.Run(g);
            }
            else if (rm.Driven && previous == ev && !Host.LosingScreenPending)
            {
                // the same race again while its script runs: a restart from the pause menu (ERestartRace → MRestartRace;
                // the losing screen's restart is posted by LuaGameHost when that screen closes)
                Debug.Log($"NFSMW lua: {g.Name} restarted (MRestartRace)");
                Runtime.Post(StategraphRuntime.RestartRace);
            }
        }

        /// <summary>
        /// Career free roam (GManager::BeginGameplay with no startup race → StartActivities 0x5F9E70): StartActivitiesCall1
        /// (0x5F95F0) runs every loaded AutoStart activity that is not running (FreeRoamOnly ones too, there being no
        /// startup race), then the bin activity race_bin_NN if it is not running — GActivity::Run without a Reset, so a
        /// state LoadState restored resumes without its StateEnter. EnterBin (0x602BB0) does the same for a new bin.
        /// race_bin_16/drive_to_safe_house runs every time and ends itself once the career intro is over (SkipCareerIntro).
        /// </summary>
        static void StartCareerActivities(int bin)
        {
            foreach (var c in Runtime.Data.All)
            {
                if (!GameplayScripts.AutoStartLoaded(c, bin)) continue;
                var a = Runtime.Activity(Runtime.Instance(c));
                if (a == null || a.Running || a.States.Count == 0) continue;
                Debug.Log($"NFSMW lua: GActivity::Run {c.Name}{(a.Current != null ? " (resumes in " + a.Current.Name + ")" : "")}");
                Runtime.Start(a);
            }
            var ba = Runtime.Activity(Runtime.Instance($"race_bin_{bin:00}"));
            if (ba != null && !ba.Running)
            {
                Debug.Log($"NFSMW lua: GActivity::Run race_bin_{bin:00}{(ba.Current != null ? " (resumes in " + ba.Current.Name + ")" : "")}");
                Runtime.Start(ba);
            }
            Runtime.Post(StategraphRuntime.EnteringGameplay);
        }

        /// <summary>
        /// GManager::StartRaceFromInGame (0x5FDCB0; UISafehouseRaceSheet 0x55172B and uiRepSheetRival 0x5519BD): only when
        /// roaming; a race of a bin other than GRaceStatus' current bin first runs FUN_005F77F0 — every bin's running activity
        /// SaveState(0) + Suspend — and sets mRefreshBinAfterRace (the bin's activities return through EnterBin when roaming
        /// again, GRaceStatus::Update 0x602C80); both race vaults are loaded; then GActivity::Reset + Run of the race's activity
        /// (the Run is OnRaceBegin's when the race is loaded).
        /// </summary>
        internal static void OnStartRaceFromInGame(RaceEvent ev)
        {
            if (ev == null || Runtime == null || GameSession.Mode != GameMode.Career || RaceManager.Active != null) return;
            int current = _bin > 0 ? _bin : GameSession.Profile?.Bin ?? 0;
            if (ev.BinIndex <= 0 || ev.BinIndex == current || ev.BinIndex >= 16) return;
            int n = 0;
            foreach (var a in Runtime.RunningActivities.ToList())
            {
                if (!a.Name.StartsWith("race_bin_")) continue;
                Runtime.SaveState(a, false);
                Runtime.Suspend(a);
                n++;
            }
            _refreshBinAfterRace = true;
            Debug.Log($"NFSMW lua: StartRaceFromInGame {ev.Name} (bin {ev.BinIndex}, current {current}): {n} bin activities suspended; the bin refreshes after the race");
        }

        /// <summary>
        /// The bin changed (EEnterBin → GRaceStatus::EnterBin): the previous bin's child vault is unloaded (LoadVaultSync →
        /// GVault::Unload 0x5EE0F0), and with it its race_bin_NN/engage activity (not Persistent: its state goes) [ours: the
        /// activity is reset].
        /// </summary>
        static void UnloadBinVault(int oldBin)
        {
            if (oldBin < 0 || Runtime == null) return;
            foreach (var a in Runtime.RunningActivities.ToList())
                if (a.Name.StartsWith($"race_bin_{oldBin:00}/") && !a.Persistent && oldBin != 16) Runtime.Reset(a);
        }

        static GameplayStateStore StoreFor(Profile p)
        {
            if (_store == null || !ReferenceEquals(_storeProfile, p))
            {
                // LoadGameplayData (0x5F81D0): ResetAllGameplayData, then the save's persistent blocks and timers
                _store = new GameplayStateStore();
                _store.Load(p?.ScriptStates, p?.ScriptTimers);
                _storeProfile = p;
                if (p != null && p.ScriptStates != null && p.ScriptStates.Count > 0)
                    Debug.Log($"NFSMW lua: {p.ScriptStates.Count} saved activity states loaded ({string.Join(", ", p.ScriptStates.Select(s => $"{s.Activity}[{s.State}]"))})");
            }
            return _store;
        }

        /// <summary>
        /// Profile.Save → GManager::SaveGameplayData (0x5F7410): every activity's SaveState(false), then the persistent map
        /// and the timers into the career save (the C# profile keeps the rest of SaveGameplayData: milestones, speed traps,
        /// the pending SMS, the respawn markers).
        /// </summary>
        static void OnProfileSaving(Profile p)
        {
            if (Mode == LuaFlowMode.Off || p == null) return;
            try
            {
                if (Runtime != null && ReferenceEquals(_profile, p)) Runtime.SaveAll();
                if (_store == null || !ReferenceEquals(_storeProfile, p)) return;
                var data = Runtime?.Data;
                p.ScriptStates = _store.SavePersistent(k => data?.Get(k)?.Name);
                p.ScriptTimers = _store.Timers.Select(t => new SavedTimer { Name = t.Name, Duration = t.Duration, Elapsed = t.Elapsed, Active = t.Active }).ToList();
            }
            catch (Exception e) { Debug.LogWarning("NFSMW lua: SaveGameplayData failed: " + e.Message); }
        }

        internal static void HookProfile()
        {
            Profile.Saving -= OnProfileSaving;
            Profile.Saving += OnProfileSaving;
        }

        /// <summary>One frame (LuaFlowDriver.Update).</summary>
        internal static void Tick(float dt)
        {
            if (_resetRequested) { _resetRequested = false; if (Runtime != null) Reset(); }
            if (Mode == LuaFlowMode.Off || _failed) return;
            // another profile (a new career, a test's profile) starts the scripts over [ours]
            if (Runtime != null && !ReferenceEquals(_profile, GameSession.Profile)) Reset();
            if (!Ensure()) return;
            try
            {
                var p = GameSession.Profile;
                bool career = GameSession.Mode == GameMode.Career && p != null;
                bool roaming = career && RaceManager.Active == null && GameBootstrap.Instance && GameBootstrap.Instance.Player && !FrontEndOpen && !GameFlow.RaceLoading;
                if (career && p.Stage == Story.Free || career && p.Stage == Story.ToSafehouse)
                {
                    int bin = p.Stage == Story.ToSafehouse ? 16 : Mathf.Clamp(p.Bin, 1, 16);
                    if (roaming && bin != _bin && p.Bin > 0)
                    {
                        UnloadBinVault(_bin);
                        _bin = bin;
                        _refreshBinAfterRace = false;
                        StartCareerActivities(bin);
                    }
                    else if (roaming && _refreshBinAfterRace)
                    {
                        // mRefreshBinAfterRace → mQueueBinChange → EnterBin(current): GActivity::Run of the suspended bin activities (they resume)
                        _refreshBinAfterRace = false;
                        Debug.Log($"NFSMW lua: EnterBin {bin} after a race of another bin: the bin activities resume");
                        StartCareerActivities(bin);
                    }
                }
                if (roaming && !_roaming && _bin >= 0) Runtime.Post(StategraphRuntime.EnteringGameplay);
                _roaming = roaming;
                Host.Update();
                var cars = new List<PlayerCar>();
                var boot = GameBootstrap.Instance;
                if (boot) { if (boot.Player) cars.Add(boot.Player); if (boot.Player2) cars.Add(boot.Player2); }
                if (RaceManager.Active != null) foreach (var e in RaceManager.Active.Entrants) if (e.Car && !cars.Contains(e.Car)) cars.Add(e.Car);
                Triggers.Update(cars);
                Runtime.Tick(dt);
                if (Host.Ledger != null) Ledger.Flush(Runtime.SimTime);
            }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogWarning("NFSMW lua: stopped after an exception: " + e);
            }
        }

        /// <summary>A one-line status for the QA tour / debug overlay [ours].</summary>
        public static string Status => Runtime == null ? $"lua {Mode}: not running" :
            $"lua {Mode}: {string.Join(", ", Runtime.RunningActivities.Select(a => $"{a.Name}[{a.Current?.Name}]"))}; errors {Runtime.Errors}" +
            (Host?.Ledger != null ? $"; ledger {Ledger.Summary}" : "");

        /// <summary>Forget the runtime (a new career, a quit to the main menu: GameplayActivity's destructor) [ours].</summary>
        public static void Reset()
        {
            if (Ledger != null && Host?.Ledger != null) Ledger.Flush(Runtime?.SimTime ?? 0, true);
            if (Runtime != null) Debug.Log("NFSMW lua: reset: " + Status);
            // GameplayActivity's destructor: the persistent activities page their state into GManager's map (dtor 0x5F7C10)
            try { if (Runtime != null && ReferenceEquals(Runtime.Store, _store)) Runtime.Shutdown(); }
            catch (Exception e) { Debug.LogWarning("NFSMW lua: shutdown: " + e.Message); }
            Runtime = null;
            Host = null;
            Ledger = null;
            Triggers = null;
            Race = null;
            _bin = -1;
            _refreshBinAfterRace = false;
            _roaming = false;
            _profile = null;
            _lastRace = null;
            _failed = false;   // a new career gets a fresh try
            LuaDrive.Reset();
        }
    }

    /// <summary>Ticks LuaFlow every frame; created when the game starts [ours].</summary>
    public sealed class LuaFlowDriver : MonoBehaviour
    {
        static LuaFlowDriver _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            GameFlow.GameplayEnded -= LuaFlow.OnGameplayEnded;
            GameFlow.GameplayEnded += LuaFlow.OnGameplayEnded;
            LuaFlow.HookProfile();
            EnsureExists();
        }

        /// <summary>The driver again when something destroyed it (play-mode tests destroy every root object) [ours].</summary>
        internal static void EnsureExists()
        {
            if (_instance || !Application.isPlaying) return;
            _instance = FindAnyObjectByType<LuaFlowDriver>();
            if (_instance) return;
            var go = new GameObject("NFSMW LuaFlow") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<LuaFlowDriver>();
        }

        GameMode _mode;

        void Update()
        {
            // a career (or a scripted quick race) that ends, or a change of game mode, starts the scripts over
            if (_mode != GameSession.Mode && (_mode == GameMode.Career || _mode == GameMode.QuickRace)) LuaFlow.Reset();
            _mode = GameSession.Mode;
            GameFlow.UpdatePause();
            LuaFlow.Tick(Time.deltaTime);
        }

        void OnApplicationQuit()
        {
            if (LuaFlow.Runtime != null) Debug.Log("NFSMW lua: " + LuaFlow.Status);
        }
    }
}
