using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;

// `lua drive [win|lose|quit] [verbose]`: the whole career under Drive mode, offline (specs/lua_hooks.md §4.2, §5.2).
// One stategraph runtime for the career, as in the game (GameplayActivity → LuaRuntime, GManager::BeginGameplay), and a
// stub game that executes the scripts' action bindings with the same rules the Unity Drive code uses (LuaDrive,
// RaceManager.Drive, the Drive variants of GameSession / Story) and posts the same C++ completions as LuaGameHost:
//   * New Career: the C# Story starts 16.1.0 (Drive: no story card of its own); the prologue then runs on the scripts
//     (PostRaceActivity chain, 16.2.1 → Game.JumpToCarLot); the car lot (front end) → race_bin_16 and the AutoStart
//     activities; the player drives into drive_to_safe_house's target → Game.DoSafeHouseIntro → bin 15.
//   * Free roam, every bin 15 .. 1: a stub player engages the bin's races through the engage triggers (the C# engage
//     dialog accepts, MAcceptEnterCareerEvent reaches the script), drives through the speed traps (the
//     speedtrap_group_monitor script awards them), evades pursuits (the C++ milestone/bounty path) until the race_bin
//     script unlocks the rival, then races the boss chain (PostRaceActivity); the rival's fall lowers the bin (FE).
//   * Races: Activity.Run of a race is deferred until the race is loaded (BlockUntilLoaded; LuaFlow.OnRaceBegin), the
//     race waits for the script's NIS.Play / EShowRaceCountdown / StartRaceTimers, racers drive to the goals the script
//     sets (Game.SetRacerGoal → TriggerEnter), laps, knockouts (Game_NotifyLapFinished's C++ half), finish, the photo
//     finish / results (EnterPostRaceFlow, NoPostRaceScreen), the losing screen (restart / quit), PostRaceActivity.
// Variant win: the player always wins; lose: every third race is lost once and restarted from the losing screen;
// quit: every fourth race is lost once and quit (abandoned) and engaged again later.
// It reports every career race completed or not, the bin flow, script errors, and the Drive calls with no C# (missing).
static class LuaDriveProbe
{
    sealed class Car
    {
        public int Index;            // 1-based racer index (0 = not racing)
        public bool Human;
        public bool Finished, Out;
        public GInstance Goal;
        public int Laps;
        public float Points;
        public override string ToString() => Human ? "player" : "racer" + Index;
    }

    sealed class Race
    {
        public RaceEvent Ev;
        public GActivity Act;
        public readonly List<Car> Racers = new List<Car>();
        public bool IntroHeld = true, NisPlaying, Countdown, Racing, Over, Restart, WinDriven;
        public float RacingSince;
        public int Finishers;
        public float StartedAt;
    }

    sealed class World : ILuaGameHost
    {
        public StategraphRuntime Rt;
        public CareerData C;
        public GameplayScripts Gs;
        /// <summary>GManager's object-state maps and timers (LuaFlow's store): they outlive a runtime.</summary>
        public GameplayStateStore Store = new GameplayStateStore();
        /// <summary>The career save's script part (Profile.ScriptStates / ScriptTimers) as of the last save.</summary>
        List<SavedActivityState> _savedStates = new List<SavedActivityState>();
        List<SavedTimer> _savedTimers = new List<SavedTimer>();
        public int Sessions = 1, Resumes, RepeatSms, ReInitial, ErrorsBefore, BossChainSteps;
        public readonly List<string> ErrorsAll = new List<string>();
        readonly HashSet<string> _binsStarted = new HashSet<string>();
        int _racesSinceResume;
        float _resumedAt = -100f;
        bool _resumeAfterBin;
        public string Variant = "win";
        public bool Verbose;
        public readonly Car Player = new Car { Index = 1, Human = true };
        // ---- the profile (Profile / GRaceDatabase score flags)
        public string Stage = "prologue";
        public int Bin = 16, Bounty, Cash;
        public readonly Dictionary<uint, int> Flags = new Dictionary<uint, int>();
        public readonly HashSet<int> Inbox = new HashSet<int>();
        public readonly HashSet<string> Challenges = new HashSet<string>();
        public readonly HashSet<string> EventsDone = new HashSet<string>();
        public readonly HashSet<int> BossesBeaten = new HashSet<int>();
        public int TutorialsSeen;
        // ---- the game
        public Race R;
        public RaceEvent Loading;
        float _loadAt;
        bool _restartLoad;
        RaceEvent _lastRaceEv;
        public bool FeOpen;
        float _feCloseAt;
        Action _feClosed;
        string _fe;
        bool _pursuit;
        float _pursuitEnd;
        int _bin = -1;
        bool _roaming;
        readonly List<(float at, Action a)> _later = new List<(float, Action)>();
        // ---- the report
        public readonly Dictionary<string, int> Calls = new Dictionary<string, int>();
        public readonly Dictionary<string, int> Missing = new Dictionary<string, int>();
        public readonly List<string> Log = new List<string>();
        public readonly Dictionary<string, int> RaceRuns = new Dictionary<string, int>();
        public readonly HashSet<string> RacesDone = new HashSet<string>();
        public int RacesStarted, Restarts, Quits, Pursuits, TrapsDone, MilestonesDone;
        public string Stuck;
        int _raceCount, _idle;
        float _nextAgentAt = 3f;
        readonly HashSet<string> _lostOnce = new HashSet<string>();

        float Now => Rt.SimTime;
        void Later(float dt, Action a) => _later.Add((Now + dt, a));
        void Note(string s) { Log.Add($"{Now,8:0.0} {s}"); if (Verbose) Console.WriteLine($"  {Now,8:0.0} {s}"); }

        int FlagsOf(RaceEvent e) => e != null && Flags.TryGetValue(e.Key, out int f) ? f : 0;
        void SetFlag(RaceEvent e, int f) { if (e != null) Flags[e.Key] = FlagsOf(e) | f; }
        RaceEvent Ev(object o) => o is GInstance g ? C.EventFor(g.Key) : null;
        BlacklistBin CurBin => C.Bins.TryGetValue(Math.Max(1, Bin), out var b) ? b : null;
        int RacesWonIn(BlacklistBin b) => b.WorldRaces.Count(e => (FlagsOf(e) & RaceIndex.CompletedCareer) != 0);
        int ChallengesIn(BlacklistBin b) => b.Milestones.Count(m => Challenges.Contains(m.Name)) + b.SpeedTraps.Count(t => Challenges.Contains(t.Name));

        public World(CareerData c) { C = c; }

        // ------------------------------------------------------------------------------------------------------
        // the per-tick game (GameFlow / LuaFlow.Tick / LuaGameHost.Update)

        public void Step()
        {
            for (int i = 0; i < _later.Count; i++)
                if (_later[i].at <= Now) { var a = _later[i].a; _later.RemoveAt(i--); a(); }
            if (FeOpen && Now >= _feCloseAt) { FeOpen = false; Note($"front end closed: {_fe}"); var a = _feClosed; _feClosed = null; a?.Invoke(); }
            if (Loading != null && Now >= _loadAt) BeginRace();
            if (_pursuit && Now >= _pursuitEnd) { _pursuit = false; Rt.Post(StategraphRuntime.PerpEscaped, ("Perpetrator", Player)); }
            // LuaFlow.Tick: career free roam starts the bin activity (race_bin_16 while driving to the safehouse)
            bool roaming = R == null && Loading == null && !FeOpen && (Stage == "career" || Stage == "to_safehouse");
            int bin = Stage == "to_safehouse" ? 16 : Math.Max(1, Math.Min(16, Bin));
            if (roaming && bin != _bin && Bin > 0) { UnloadBinVault(_bin); if (_bin >= 0 && bin != _bin) _resumeAfterBin = true; _bin = bin; StartCareerActivities(bin); }
            if (roaming && !_roaming && _bin >= 0) Rt.Post(StategraphRuntime.EnteringGameplay);
            _roaming = roaming;
            // a race without goals for the player (the epic pursuit): after 60 s he drives into its win_trigger
            if (R != null && R.Racing && Player.Goal == null && !R.WinDriven && Now - R.RacingSince > 60f && R.Act != null)
            {
                R.WinDriven = true;
                var wv = Rt.L.GetTable(Rt.Push(R.Act.Instance), LuaValue.String("win_trigger"));
                var win = wv.AsUserData?.Value as GInstance;
                if (win != null) { Note($"player drives into {win.Name}"); Rt.Post(StategraphRuntime.TriggerEnter, ("Sender", win), ("Element", Player)); }
            }
            // the race clock
            if (R != null && R.Racing && ((int)(Now * 4) % 4) == 0) Rt.Post(StategraphRuntime.NotifyRaceTimeSecTick);
            if (roaming && Now >= _nextAgentAt) { _nextAgentAt = Now + 3f; Agent(); }
        }

        // LuaFlow.StartCareerActivities: GManager::StartActivities (StartActivitiesCall1, then the bin activity), GActivity::Run
        // without a Reset — a state LoadState restored resumes without StateEnter
        void StartCareerActivities(int bin)
        {
            foreach (var c in Rt.Data.All)
            {
                if (!GameplayScripts.AutoStartLoaded(c, bin)) continue;
                var a = Rt.Activity(Rt.Instance(c));
                if (a == null || a.Running || a.States.Count == 0) continue;
                if (Verbose || a.Current != null) Note($"GActivity::Run {c.Name}{(a.Current != null ? " (resumes in " + a.Current.Name + ")" : "")}");
                Rt.Start(a);
            }
            var ba = Rt.Activity(Rt.Instance($"race_bin_{bin:00}"));
            if (ba != null && !ba.Running)
            {
                Note($"GActivity::Run race_bin_{bin:00}{(ba.Current != null ? " (resumes in " + ba.Current.Name + ")" : "")}");
                Rt.Start(ba);
            }
            Rt.Post(StategraphRuntime.EnteringGameplay);
        }

        // LuaFlow.UnloadBinVault: the previous bin's child vault goes, and its engage activity
        void UnloadBinVault(int oldBin)
        {
            if (oldBin < 0 || oldBin == 16) return;
            foreach (var a in Rt.RunningActivities.ToList())
                if (a.Name.StartsWith($"race_bin_{oldBin:00}/") && !a.Persistent) Rt.Reset(a);
        }

        // ------------------------------------------------------------------------------------------------------
        // the career save and gameplay sessions (Profile.Save -> SaveGameplayData; GameplayActivity's end; LoadGameplayData)

        public void SaveProfile()
        {
            Rt.SaveAll();
            _savedStates = Store.SavePersistent(k => Rt.Data.Get(k)?.Name);
            _savedTimers = Store.Timers.Select(t => new SavedTimer { Name = t.Name, Duration = t.Duration, Elapsed = t.Elapsed, Active = t.Active }).ToList();
        }

        /// <summary>GameplayActivity ends and a new one begins: the runtime is made again over the same GManager (`reload`:
        /// the career is loaded from its save, LoadGameplayData).</summary>
        public void NewSession(string why, bool reload)
        {
            var old = Rt;
            old.Shutdown();
            float t = old.SimTime;
            ErrorsBefore += old.Errors;
            ErrorsAll.AddRange(old.ErrorLog);
            if (reload)
            {
                Store = new GameplayStateStore();
                // variant nostate: the save without the script part (old saves, or the port before SaveGameplayData's states)
                Store.Load(Variant.Contains("nostate") ? null : _savedStates, Variant.Contains("nostate") ? null : _savedTimers);
                Resumes++;
                _resumedAt = t;
            }
            var rt = new StategraphRuntime(Gs, this) { ErrorLogLimit = 100000 };
            rt.Attach(Store);
            rt.SimTime = t;   // the stub's clock goes on (its pending events are kept)
            rt.StateChanged += OnStateChanged;
            Rt = rt;
            _bin = -1;
            _roaming = false;
            Sessions++;
            Note($"--- {why}: a new gameplay session{(reload ? $" from the save ({_savedStates.Count} saved activity states: {string.Join(" ", _savedStates.Select(x => $"{x.Activity}[{x.State}]"))})" : "")}");
        }

        void Run(string name)
        {
            var g = Rt.Instance(name);
            if (g == null) return;
            Note($"Activity.Run {name}");
            Rt.Run(g);
        }

        void OpenFe(string what, float secs, Action closed)
        {
            FeOpen = true;
            _fe = what;
            _feCloseAt = Now + secs;
            _feClosed = closed;
            Note($"front end: {what}");
        }

        // ------------------------------------------------------------------------------------------------------
        // the stub player in free roam

        void Agent()
        {
            if (Stage == "to_safehouse")
            {
                // drive into drive_to_safe_house's target (LuaTriggers → TriggerEnter)
                var a = Rt.RunningActivities.FirstOrDefault(x => x.Name.EndsWith("drive_to_safe_house"));
                var target = a != null ? Rt.Connections(a.Instance).FirstOrDefault(t => Rt.Data.DerivesFrom(t.Record, AttribDatabase.Hash("trigger"))) : null;
                if (target != null) { Rt.Post(StategraphRuntime.TriggerEnter, ("Sender", target), ("Element", Player)); Note($"player drives into {target.Name}"); }
                else if (++_idle > 20) Stuck = "drive_to_safe_house never ran";
                return;
            }
            if (Stage != "career") return;
            // variant resume: quit to the main menu and load the career (Profile.Save, EndGameplay, LoadGameplayData) after
            // every second race and right after each new bin
            if (Variant.Contains("resume") && (_racesSinceResume >= 2 || _resumeAfterBin))
            {
                _racesSinceResume = 0;
                _resumeAfterBin = false;
                SaveProfile();
                NewSession("quit to the main menu, career loaded", true);
                return;
            }
            var bin = CurBin;
            if (bin == null) return;
            var rb = Rt.Activity(Rt.Instance($"race_bin_{bin.Index:00}"));
            bool rivalOpen = rb?.Current != null && (rb.Current.Name == "rival_unlocked" || rb.Current.Name == "done");
            var boss = bin.BossRaces.FirstOrDefault(e => e.Start != null && (FlagsOf(e) & RaceIndex.CompletedCareer) == 0);
            if (rivalOpen && boss != null && (FlagsOf(boss) & RaceIndex.UnlockedCareer) != 0)
            {
                // the safehouse's rival challenge (C# front end) starts the first boss race of the chain
                _idle = 0;
                Note($"rival challenge: {boss.Name}");
                StartRaceCSharp(boss, false);
                return;
            }
            var world = bin.WorldRaces.FirstOrDefault(e => e.Start != null && (FlagsOf(e) & (RaceIndex.UnlockedCareer | RaceIndex.CompletedCareer)) == RaceIndex.UnlockedCareer);
            if (world != null)
            {
                _idle = 0;
                var trig = bin.EngageTriggers.FirstOrDefault(t => t.target == world.Key);
                var tg = trig.point != null ? Rt.Instance(trig.point.Name) : null;
                if (tg != null)
                {
                    // the car enters the engage trigger: bin_race_engage → prompt → Game.EnterEngagableTrigger (C# dialog)
                    Rt.Post(StategraphRuntime.TriggerEnter, ("Sender", tg), ("Element", Player));
                    _engage = world;
                    Note($"engage trigger {tg.LocalName} for {world.Name}");
                }
                else { Note($"no engage trigger: {world.Name} started from the event list"); StartRaceCSharp(world, false); }
                return;
            }
            if (Bounty < bin.RequiredBounty)
            {
                // a pursuit evaded in free roam (PursuitSystem.Evaded: bounty → MNotifyPlayerRep via LuaGameHost.WatchProfile)
                Pursuits++;
                int got = Math.Max(20000, bin.RequiredBounty / 4);
                Bounty += got;
                Rt.Post(StategraphRuntime.NotifyPlayerRep, ("RepPoints", got));
                Note($"pursuit evaded: bounty +{got} = {Bounty}");
                return;
            }
            if (ChallengesIn(bin) < bin.RequiredChallenges)
            {
                var trap = bin.SpeedTraps.FirstOrDefault(t => !Challenges.Contains(t.Name));
                if (trap != null)
                {
                    // speedtrap_group_monitor: TriggerEnter at speed → NotifySpeedTrapTriggered + AwardPlayerBounty
                    Rt.Post(StategraphRuntime.TriggerEnter, ("Sender", Rt.Instance(trap.Name)), ("Element", Player));
                    Note($"speed trap {trap.Name}");
                    _trapTries[trap.Name] = _trapTries.TryGetValue(trap.Name, out int tries) ? tries + 1 : 1;
                    if (tries + 1 > 3) { Challenges.Add(trap.Name); Note($"speed trap {trap.Name} never completed by the script (forced)"); }
                    return;
                }
                var m = bin.Milestones.FirstOrDefault(x => !Challenges.Contains(x.Name));
                if (m != null)
                {
                    // GMilestone::NotifyPursuitOver (C++): the challenge is awarded, AwardPlayerBounty, MNotifyChallengePassed
                    MilestonesDone++;
                    Challenges.Add(m.Name);
                    Bounty += m.Bounty;
                    Rt.Post(StategraphRuntime.NotifyChallengePassed);
                    Rt.Post(StategraphRuntime.NotifyPlayerRep, ("RepPoints", m.Bounty));
                    Note($"milestone {m.Name}");
                    return;
                }
            }
            if (++_idle == 3) Rt.Post(StategraphRuntime.EnteringGameplay);
            if (_idle > 12)
                Stuck = $"bin {bin.Index}: nothing to do; race_bin state {rb?.Current?.Name}, races won {RacesWonIn(bin)}/{bin.RequiredRacesWon}, " +
                        $"bounty {Bounty}/{bin.RequiredBounty}, challenges {ChallengesIn(bin)}/{bin.RequiredChallenges}, boss races left " +
                        string.Join(" ", bin.BossRaces.Where(e => (FlagsOf(e) & 2) == 0).Select(e => $"{e.Name}(flags {FlagsOf(e)})")) +
                        "; world races not won " + string.Join(" ", bin.WorldRaces.Where(e => (FlagsOf(e) & 2) == 0).Select(e => $"{e.Name}(flags {FlagsOf(e)})"));
        }

        RaceEvent _engage;
        readonly Dictionary<string, int> _trapTries = new Dictionary<string, int>();

        // ------------------------------------------------------------------------------------------------------
        // races (GameFlow.StartRace → RaceManager.Begin → LuaFlow.OnRaceBegin)

        public void StartRaceCSharp(RaceEvent ev, bool restart)
        {
            if (Loading != null) return;
            Loading = ev;
            _restartLoad = restart;
            _loadAt = Now + 1f;
            Note($"GameFlow.StartRace {ev.Name}{(restart ? " (restart)" : "")}");
        }

        void BeginRace()
        {
            var ev = Loading;
            Loading = null;
            var old = R;
            R = new Race { Ev = ev, StartedAt = Now };
            R.Racers.Add(Player);
            Player.Finished = Player.Out = false; Player.Goal = null; Player.Laps = 0; Player.Points = 0;
            int opp = ev.Type == RaceType.Tollbooth ? 0 : Math.Min(7, ev.Opponents.Count);
            if (ev.BossRace && opp == 0 && ev.Type != RaceType.Tollbooth) opp = 1;
            for (int i = 0; i < opp; i++) R.Racers.Add(new Car { Index = i + 2 });
            RacesStarted++;
            RaceRuns[ev.Name] = RaceRuns.TryGetValue(ev.Name, out int n) ? n + 1 : 1;
            // LuaFlow.OnRaceBegin: an engage prompt accepts; the race activity runs (a restart posts RestartRace)
            if (Rt.RunningActivities.Any(a => a.Current?.Name == "prompt")) { Rt.Post(StategraphRuntime.AcceptEnterCareerEvent, ("SimTime", Now)); }
            var g = Rt.Instance(ev.Key);
            var act = Rt.Activity(g);
            R.Act = act;
            foreach (var o in Rt.RunningActivities.Where(x => x != act && x.States.Any(s => s.Name == "racing")).ToList()) Rt.Suspend(o);
            bool sameRace = _lastRaceEv == ev && act.Running && act.Current?.Name != "done";
            _lastRaceEv = ev;
            if (sameRace) { if (!_waitLosing) Rt.Post(StategraphRuntime.RestartRace); }
            else { Note($"Activity.Run {g.Name} (race loaded)"); Rt.Run(g); }
        }

        void DriveTo(Car c)
        {
            if (R == null || !R.Racing || c.Goal == null || c.Finished || c.Out) return;
            var goal = c.Goal;
            var race = R;
            bool slow = c.Human && _lostOnce.Contains(R.Ev.Name + "#" + RaceRuns[R.Ev.Name]);
            float dt = c.Human ? (slow ? 40f : 4f) : 5f + c.Index;
            Later(dt, () =>
            {
                if (R == race && c.Goal == goal && !c.Finished && !c.Out && R.Racing)
                    Rt.Post(StategraphRuntime.TriggerEnter, ("Sender", goal), ("Element", c));
            });
        }

        int RacerIndex(object o) => R != null && o is Car c && R.Racers.Contains(c) ? R.Racers.IndexOf(c) + 1 : -1;
        Car Racer(int i) => R != null && i >= 1 && i <= R.Racers.Count ? R.Racers[i - 1] : null;

        /// <summary>GameSession.AwardWin, Drive variant: the C++ half of Game_NotifyRaceFinished plus the FE's rival flow.</summary>
        void AwardWin(RaceEvent e)
        {
            bool already = (FlagsOf(e) & RaceIndex.CompletedCareer) != 0;
            Cash += (int)e.Cash;
            if (!already)
                foreach (var k in e.UnlockRaces) { var u = C.EventFor(k); if (u != null) SetFlag(u, RaceIndex.UnlockedQuickRace | (u.NeverInQuickRace ? 0 : RaceIndex.UnlockedCareer)); }
            SetFlag(e, RaceIndex.CompletedCareer);
            RacesDone.Add(e.Name);
            if (e.DDayRace && e.EventId != null) EventsDone.Add(e.EventId);
            Note($"won {e.Name}");
            var bin = CurBin;
            if (e.BossRace && bin != null && bin.BossRaces.All(b => (FlagsOf(b) & RaceIndex.CompletedCareer) != 0) && !BossesBeaten.Contains(bin.Index))
            {
                BossesBeaten.Add(bin.Index);
                Note($"=== rival {bin.Index} beaten: bin {bin.Index} -> {bin.Index - 1}");
                Bin = bin.Index - 1;
                _rivalFlow = true;
            }
        }

        bool _rivalFlow, _waitLosing;

        void AfterResults()
        {
            _racesSinceResume++;
            _nextAgentAt = Now + 8f;   // the outro and a PostRaceActivity come first
            // RaceManager.AfterResults, Drive: the rival flow (FE) only
            if (_rivalFlow) { _rivalFlow = false; OpenFe("rival flow (uiRepSheetRivalFlow)", 2f, null); }
        }

        // ------------------------------------------------------------------------------------------------------
        // the bindings

        public object Call(LuaBinding b, object[] args)
        {
            string name = b.Table + "." + b.Name;
            Calls[name] = Calls.TryGetValue(name, out int cn) ? cn + 1 : 1;
            if (Verbose && b.Kind != LuaBinding.Kinds.Query) Console.WriteLine($"  {Now,8:0.0}   call {b.FullName}({string.Join(", ", args.Select(a => a ?? "nil"))})");
            switch (name)
            {
                // ---- queries (LuaGameHost)
                case "Game.GetNumRacers": return R?.Racers.Count ?? 0;
                case "Game.GetRacerIndex": return RacerIndex(args[0]);
                case "Game.GetRacerElement": return Racer((int)args[0]);
                case "Game.GetPlayerElement": return Player;
                case "Game.GetRacerCharacter":
                    {
                        int i = (int)args[0];
                        return R != null && i >= 2 && i - 2 < R.Ev.Opponents.Count ? Rt.Instance(R.Ev.Opponents[i - 2]) : null;
                    }
                case "Game.RacerIsHuman": return Racer((int)args[0])?.Human ?? false;
                case "Game.PlayerIsLocal": return args[0] == Player;
                case "ISimable.IsPlayer": return args.Length > 0 && args[0] == Player;
                case "Game.GetSimableSpeedKmh": return args[0] is Car sc ? (sc.Human ? 330f : 150f - 3 * sc.Index) : 0f;
                case "Game.SimableDistance": return 100f;
                case "Game.SimableAngle": return 0.95f;   // 0x6053B0: dot(forward, the instance direction)
                case "Game.CalculateRanking":
                    {
                        if (R == null || !(args[0] is Car rc)) return args[1];
                        bool points = R.Ev.RankByPoints || R.Ev.Type == RaceType.SpeedTrap;
                        if (!points) return args[1];
                        return 1 + R.Racers.Count(o => o != rc && o.Points > rc.Points);
                    }
                case "Game.AllRacersDone": return R == null || R.Racers.All(x => x.Finished || x.Out);
                case "Game.AllHumanPlayersDone": return R == null || R.Racers.Where(x => x.Human).All(x => x.Finished || x.Out);
                case "Game.IsRaceCompleted": return (FlagsOf(Ev(args[0])) & RaceIndex.CompletedCareer) != 0;
                case "Game.IsRaceUnlocked": return (FlagsOf(Ev(args[0])) & RaceIndex.UnlockedCareer) != 0;
                case "Game.IsCareerMode": return true;
                case "Game.IsSplitScreen": case "Game.IsOnlineGame": case "Game.IsLANGame": return false;
                case "Game.GetPlayerBounty": return Bounty;
                case "Bin.GetNumRacesWon": return C.Bins.TryGetValue((int)args[0], out var bw) ? RacesWonIn(bw) : 0;
                case "Bin.GetNumChallengesPassed": return C.Bins.TryGetValue((int)args[0], out var bc) ? ChallengesIn(bc) : 0;
                case "Game.AllowEngageEvents": case "Game.AllowMenuGates": case "Game.AllowEngageSafehouse": return R == null && !_pursuit;
                case "Game.IsActiveMenuGate": return true;
                case "Game.IsActiveSpeedTrap":
                    {
                        string n = (args[0] as GInstance)?.Name;
                        return n != null && !Challenges.Contains(n) && C.Bins.Values.Any(x => x.Index >= Math.Max(1, Bin) && x.Index <= 15 && x.SpeedTraps.Any(t => t.Name == n));
                    }
                case "Game.SkipCareerIntro": return Stage != "to_safehouse" && Stage != "prologue";
                case "Audio.IsCopSpeechPlaying": case "Platform.IsNextGen": return false;
                case "Game.SetCurrentTimeOfDay": case "Game.DoSpecialSetup": return false;
            }
            if (b.Kind != LuaBinding.Kinds.Action && name != "Activity.Run") return null;
            var kind = LuaDriveTable.Of(b);
            if (kind == LuaDriveTable.Kind.Missing) Missing[name] = Missing.TryGetValue(name, out int mn) ? mn + 1 : 1;
            switch (name)
            {
                // ---- career
                case "Game.UnlockRace":
                    {
                        var ev = Ev(args[0]);
                        if (ev != null) { if ((FlagsOf(ev) & 8) == 0) Note($"UnlockRace {ev.Name}"); SetFlag(ev, RaceIndex.UnlockedCareer | (ev.NeverInQuickRace ? 0 : RaceIndex.UnlockedQuickRace)); }
                        return null;
                    }
                case "Game.AwardPlayerBounty":
                    Bounty += (int)args[0];
                    Rt.Post(StategraphRuntime.NotifyPlayerRep, ("RepPoints", args[0]));
                    return null;
                case "Game.NotifySpeedTrapTriggered":
                    if (R == null && args[1] is GInstance trap && Challenges.Add(trap.Name))
                    {
                        TrapsDone++;
                        Note($"speed trap {trap.LocalName} completed");
                        Rt.Post(StategraphRuntime.NotifyChallengePassed);   // LuaGameHost.WatchProfile: Profile.Milestones grew
                    }
                    return null;
                case "Game.JumpToCarLot":
                    Stage = "carlot";
                    if (R != null) R = null;
                    OpenFe("car lot (Story.JumpToCarLot)", 2f, () => { Stage = "to_safehouse"; Note("starter car bought"); SaveProfile(); NewSession("the car lot (EQuitToFE)", false); });
                    return null;
                case "Game.DoSafeHouseIntro":
                    Later(2f, () => Rt.Post(StategraphRuntime.NotifyMovieFinished));
                    Bin = 15;
                    Stage = "career";
                    OpenFe("safehouse + rival bio (Story.SafehouseIntro)", 3f, null);
                    return null;
                case "Game.PlayTutorial":
                    {
                        var ev = R?.Ev;
                        int bit = ev == null ? 0 : ev.Type == RaceType.Drag ? 1 : ev.Type == RaceType.Tollbooth ? 2 : ev.Type == RaceType.SpeedTrap ? 4 : 0;
                        bool play = bit != 0 && (TutorialsSeen & bit) == 0;
                        TutorialsSeen |= bit;
                        Later(play ? 2f : 0.1f, () => Rt.Post(StategraphRuntime.NotifyMovieFinished));
                        return null;
                    }
                case "Game.ForcePursuitStart":
                    _pursuit = true;
                    _pursuitEnd = Now + 20f;
                    // the pursuit's stats as they grow (LuaGameHost: MNotifyMilestoneProgress while in pursuit)
                    foreach (var t in new[] { "pursuit_length", "bounty_in_pursuit", "cost_to_state_in_pursuit", "cops_damaged", "total_infractions", "roadblocks_dodged", "tire_spikes_dodged", "pursuit_evasion_time" })
                    {
                        var tt = t;
                        Later(10f, () => Rt.Post(StategraphRuntime.NotifyMilestoneProgress, ("MilestoneName", tt), ("ValueReached", tt == "pursuit_evasion_time" ? 1f : 1e6f)));
                    }
                    return null;
                case "Camera.SetGenericCamera": Later(3f, () => Rt.Post(StategraphRuntime.IceCameraFinished)); return null;
                case "Game.SabotageEngine":
                    {
                        float t = args[1] is float f ? f : 0;
                        Later(t, () => Rt.Post(StategraphRuntime.NotifyEngineBlown, ("Racer", Player)));
                        return null;
                    }
                case "Game.BlowEngine": Rt.Post(StategraphRuntime.NotifyEngineBlown, ("Racer", args[0])); return null;
                case "NIS.Play":
                    if (R != null && R.IntroHeld && !R.Racing && !R.Ev.RollingStart)
                    {
                        R.NisPlaying = true;
                        var race = R;
                        Later(4f, () => { race.NisPlaying = false; race.IntroHeld = false; Rt.Post(StategraphRuntime.NisComplete); });
                    }
                    else Later(R != null && R.Ev.RollingStart && R.IntroHeld ? 0.2f : 3f, () => Rt.Post(StategraphRuntime.NisComplete));
                    return null;
                case "RaceStatus.NotifyScriptWhenLoaded": Later(0.25f, () => Rt.Post(StategraphRuntime.LoadingComplete)); return null;
                case "Activity.Run":
                    {
                        // LuaDrive: a race runs once it is loaded (GameFlow.StartRace; LuaFlow.OnRaceBegin runs the activity)
                        var ev = Ev(args[0]);
                        if (ev == null || ev.Start == null) return null;
                        if (R?.Ev == ev || Loading == ev) return false;
                        // the race whose activity is done (a NIS shell, a race left by its PostRaceActivity) ends first
                        if (R != null && Loading == null && (R.Act == null || !R.Act.Running || R.Act.Current?.Name == "done")) { Note($"{R.Ev.Name} ends (its activity is done)"); R = null; }
                        if (R == null && Loading == null)
                        {
                            // a race's `done` running its PostRaceActivity (the boss chain, the prologue chain)
                            var from = Rt.CurrentActivity != null ? C.EventFor(Rt.CurrentActivity.Instance.Key) : null;
                            if (from != null && from.BossRace && ev.BossRace) BossChainSteps++;
                            StartRaceCSharp(ev, false);
                            return false;
                        }
                        Note($"Activity.Run {ev.Name} ignored: {R?.Ev.Name ?? Loading?.Name} is running");
                        return false;
                    }
                // ---- the race
                case "Game.EnterEngagableTrigger":
                    {
                        // the C# EngageEventScreen: the player accepts the race he drove in for (GameFlow.StartRace; the
                        // prompt then gets MAcceptEnterCareerEvent from LuaFlow.OnRaceBegin)
                        var ev = Ev(args[0]);
                        if (ev != null && ev == _engage && R == null && Loading == null) { _engage = null; Later(1f, () => StartRaceCSharp(ev, false)); }
                        return null;
                    }
                case "Game.InitRacers": return null;
                case "Game.SetRacerGoal":
                    {
                        var c = Racer((int)args[0]);
                        if (c != null) { c.Goal = args[1] as GInstance; DriveTo(c); }
                        return null;
                    }
                case "Game.StartRaceTimers":
                    if (R != null) { R.Racing = true; R.RacingSince = Now; R.IntroHeld = false; R.Countdown = false; foreach (var c in R.Racers) DriveTo(c); }
                    return null;
                case "Game.NotifyLapFinished":
                    {
                        if (!(args[0] is Car c) || R == null) return null;
                        int lap = (int)args[1];
                        c.Laps = lap;
                        if (R.Ev.Type == RaceType.Knockout)
                        {
                            int kpl = Math.Max(1, R.Ev.KnockoutsPerLap);
                            int done = R.Racers.Count(x => !x.Out && x.Laps >= lap);
                            if (done == R.Racers.Count - kpl * lap)
                                foreach (var x in R.Racers.Where(x => !x.Out && !x.Finished && x.Laps < lap).ToList())
                                {
                                    x.Out = true;
                                    Rt.Post(StategraphRuntime.NotifyKnockedOut, ("Racer", x));
                                }
                        }
                        return null;
                    }
                case "Game.NotifyRaceFinished":
                    {
                        if (!(args[0] is Car c) || R == null || c.Finished) return null;
                        c.Finished = true;
                        R.Finishers++;
                        bool points = R.Ev.RankByPoints || R.Ev.Type == RaceType.SpeedTrap;
                        int place = points ? 1 + R.Racers.Count(o => o != c && o.Points > c.Points) : R.Finishers;
                        if (c.Human && place == 1) AwardWin(R.Ev);
                        return null;
                    }
                case "Game.KnockoutRacer":
                case "Game.TotalRacer":
                    if (args[0] is Car kc) { kc.Out = true; Rt.Post(StategraphRuntime.NotifyKnockedOut, ("Racer", kc)); }
                    return null;
                case "Game.AwardCash":
                case "Game.AwardPoints":
                    if (args[0] is Car pc) pc.Points = Math.Max(0, pc.Points + (args[1] is float pf ? pf : 0));
                    return null;
                case "Game.EnterPostRaceFlow":
                    {
                        if (R == null) { Later(0.5f, () => Rt.Post(StategraphRuntime.FlowReadyForOutro)); return null; }
                        var ev = R.Ev;
                        R.Over = true;
                        if (ev.NoPostRaceScreen)
                        {
                            SetFlag(ev, RaceIndex.CompletedCareer);
                            if (ev.DDayRace) EventsDone.Add(ev.EventId);
                            RacesDone.Add(ev.Name);
                            R = null;
                            Note($"{ev.Name}: no post-race screen");
                            Later(0.5f, () => Rt.Post(StategraphRuntime.FlowReadyForOutro));
                        }
                        else OpenFe($"photo finish + results ({ev.Name})", 2f, () => { R = null; AfterResults(); Rt.Post(StategraphRuntime.FlowReadyForOutro); });
                        return null;
                    }
                case "Game.ShowWinningPostRaceScreen":
                    OpenFe("results (won)", 2f, () => { R = null; AfterResults(); Rt.Post(StategraphRuntime.FlowReadyForOutro); });
                    return null;
                case "Game.ShowLosingPostRaceScreen":
                    {
                        var ev = R?.Ev;
                        bool quit = Variant.Contains("quit");
                        _waitLosing = true;
                        OpenFe($"results (lost: {(quit ? "quit" : "restart")})", 2f, () =>
                        {
                            _waitLosing = false;
                            if (!quit && ev != null) { Restarts++; R = null; StartRaceCSharp(ev, true); Later(1.1f, () => Rt.Post(StategraphRuntime.RestartRace)); }
                            else { Quits++; R = null; Rt.Post(StategraphRuntime.NotifyRaceAbandoned); }
                        });
                        return null;
                    }
                case "Game.AbandonRace":
                    {
                        // Game.AbandonRace + GRaceStatus::RaceAbandoned: an unfinished boss chain starts again
                        var ev = R?.Ev ?? Ev(Rt.CurrentActivity?.Instance);
                        R = null;
                        var bin = CurBin;
                        if (ev != null && ev.BossRace && !ev.EpicPursuit && bin != null && bin.BossRaces.Contains(ev) && !BossesBeaten.Contains(bin.Index))
                            foreach (var bb in bin.BossRaces) if (Flags.ContainsKey(bb.Key)) Flags[bb.Key] &= ~RaceIndex.CompletedCareer;
                        return null;
                    }
                case "RaceStatus.SetRoaming": if (R != null && R.Over) R = null; SaveProfile(); return null;   // EAutoSave
            }
            return null;
        }

        public void Event(string name, object[] args)
        {
            Calls[name] = Calls.TryGetValue(name, out int cn) ? cn + 1 : 1;
            if (LuaDriveTable.Of(name) == LuaDriveTable.Kind.Missing) Missing[name] = Missing.TryGetValue(name, out int mn) ? mn + 1 : 1;
            if (Verbose) Console.WriteLine($"  {Now,8:0.0}   event {name}({string.Join(", ", args.Select(a => a ?? "nil"))})");
            int Id(int i) => args.Length > i && args[i] is float f ? (int)f : -1;
            switch (name)
            {
                case "EAddSMS":
                    if (Id(0) >= 0 && Inbox.Add(Id(0))) Note($"SMS {Id(0)}");
                    else if (Id(0) >= 0 && Now - _resumedAt < 10f) { RepeatSms++; Note($"SMS {Id(0)} sent again right after a resume"); }
                    break;
                case "ECellCall":
                    if (Id(0) >= 0 && Inbox.Add(Id(0))) Note($"cell call {Id(0)}");
                    Rt.Post(StategraphRuntime.NotifyCellCallStarted);
                    Later(6f, () => Rt.Post(StategraphRuntime.NotifyCellCallComplete));
                    break;
                case "EShowRaceCountdown":
                    if (R != null) { R.IntroHeld = false; R.Countdown = true; }
                    Later(3f, () => Rt.Post(StategraphRuntime.CountdownDone));
                    break;
                case "EShowMessageScreen": Later(2f, () => Rt.Post(StategraphRuntime.NotifyMessageDone)); break;
                case "EPlayRaceMovie": Later(2f, () => Rt.Post(StategraphRuntime.NotifyMovieFinished)); break;
                case "EPlayRaceNIS": Later(2f, () => Rt.Post(StategraphRuntime.NisComplete)); break;
                case "EShowPostPursuit": OpenFe("post pursuit", 2f, () => Rt.Post(StategraphRuntime.FlowReadyForOutro)); break;
                case "EBailPursuit": _pursuit = false; break;
            }
        }

        public void Log2(string s) { }
        void ILuaGameHost.Log(string line)
        {
            if (line.StartsWith("error")) { Note(line); Console.WriteLine($"  {Now,8:0.0} {line}"); }
            else if (Verbose) Console.WriteLine($"  {Now,8:0.0} {line}");
        }

        public void OnStateChanged(GActivity a, ScriptState s)
        {
            if (s == null) return;
            // a bin activity that starts over after it ran in this career (a resume that lost its saved state)
            if (s.Name == "initial" && a.Name.StartsWith("race_bin_") && !a.Name.Contains("/") && !_binsStarted.Add(a.Name)) { ReInitial++; Note($"{a.Name} started over at `initial`"); }
            if (Verbose || a.Name.StartsWith("race_bin_") && !a.Name.Contains("/") && s.Name != "check_rival" && s.Name != "rival_locked") Note($"{a.Name} -> {s.Name}");
            // the race variants: decide when the race starts racing whether this attempt is lost
            if (s.Name == "racing" && R != null && R.Act == a)
            {
                _raceCount++;
                string key = R.Ev.Name + "#" + RaceRuns[R.Ev.Name];
                bool firstTry = RaceRuns[R.Ev.Name] == 1;
                if (firstTry && !R.Ev.DDayRace && (Variant.Contains("lose") && _raceCount % 3 == 0 || Variant.Contains("quit") && _raceCount % 4 == 0)) _lostOnce.Add(key);
            }
            // a D-Day race quit (abandoned) is started again by the C# Story (Drive: LuaFlow's done hook) [ours]
            if (s.Name == "done" && Stage == "prologue")
            {
                var ev = C.EventFor(a.Instance.Key);
                var st = Rt.StateOf(a);
                if (ev != null && ev.DDayRace && st.Get("Abandoned").IsTrue && Loading == null && R == null)
                {
                    Note($"{ev.Name} abandoned: Story restarts the prologue race");
                    StartRaceCSharp(ev, false);
                }
            }
        }
    }

    public static void Run(string root, string[] args)
    {
        var rest = args.Skip(3).ToList();
        bool verbose = rest.Remove("verbose");
        // resume*: quit to the main menu and load the save after every second race and at each new bin; nostate: the same
        // with a save that has no script states (shows the bin's entry SMS coming again: expected to fail)
        var variants = rest.Count > 0 ? rest : new List<string> { "win", "lose", "quit", "resume", "resume-lose", "resume-quit" };
        var career = CareerData.Load(root);
        var gs = GameplayScripts.Load(career.Db);
        if (rest.Remove("dday"))
        {
            foreach (var id in RaceIndex.DDayRaces.Concat(new[] { "16.8.1", "1.8.1" }))
            {
                var e = career.EventById(id);
                if (e == null) { Console.WriteLine($"{id}: none"); continue; }
                Console.WriteLine($"{id} {e.Name}: IntroMessageID {e.IntroMessageId} IntroMovie '{e.IntroMovie}' IntroNIS '{e.IntroNis}' OutroMovie '{e.OutroMovie}' OutroNIS '{e.OutroNis}' NoPostRaceScreen {e.NoPostRaceScreen} Rolling {e.RollingStart} Type {e.Type} PostRace {(e.PostRaceActivity != 0 ? career.EventFor(e.PostRaceActivity)?.Name : "-")}");
            }
            return;
        }
        if (rest.Remove("traps"))
        {
            var st = gs.Get("speedtraps");
            var listed = new HashSet<uint>(st.Keys("SpeedTrapList"));
            Console.WriteLine($"speedtraps.SpeedTrapList: {listed.Count} entries");
            foreach (var b in career.Bins.Values)
                foreach (var t in b.SpeedTraps)
                {
                    var c = career.Db.Get("gameplay", t.Name);
                    Console.WriteLine($"  bin {b.Index,2} {t.Name} listed {listed.Contains(c.Key)} threshold {t.Threshold} bounty {t.Bounty}");
                }
            return;
        }
        bool allOk = true;
        foreach (var variant in variants)
        {
            var t0 = DateTime.Now;
            var w = new World(career) { Variant = variant, Verbose = verbose };
            var rt0 = new StategraphRuntime(gs, w) { ErrorLogLimit = 100000 };
            w.Rt = rt0;
            w.Gs = gs;
            rt0.Attach(w.Store);
            rt0.StateChanged += w.OnStateChanged;
            Console.WriteLine($"== lua drive [{variant}]");
            // New Career: Story.NewCareer → RunNextPrologue → GameFlow.StartRace(16.1.0)
            var first = career.EventById(RaceIndex.DDayStart);
            w.StartRaceCSharp(first, false);
            int ticks = 0;
            const float dt = 0.25f;
            long instr = 0;
            while (ticks++ < 24 * 3600 * 4 && w.Stuck == null && w.Bin > 0 && instr + w.Rt.L.Instructions < 400_000_000)
            {
                var before = w.Rt;
                w.Step();
                if (w.Rt != before) instr += before.L.Instructions;
                w.Rt.Tick(dt);
                if (w.Bin <= 0 || w.BossesBeaten.Contains(1) && !w.FeOpen && w.R == null && w.Loading == null) break;
            }
            // ---- report
            var rt = w.Rt;
            instr += rt.L.Instructions;
            int errors = w.ErrorsBefore + rt.Errors;
            var errorLog = w.ErrorsAll.Concat(rt.ErrorLog).ToList();
            var prologue = RaceIndex.DDayRaces.Select(career.EventById).Where(e => e != null).ToList();
            Console.WriteLine($"  sim {rt.SimTime / 60:0} min, {w.RacesStarted} race starts ({w.Restarts} restarts from the losing screen, {w.Quits} quits), " +
                              $"{w.Pursuits} pursuits, {w.TrapsDone} speed traps by script, {w.MilestonesDone} milestones; final bin {w.Bin}, stage {w.Stage}, bounty {w.Bounty}, cash {w.Cash}");
            Console.WriteLine($"  prologue: " + string.Join(" ", prologue.Select(e => $"{e.EventId}:{(w.EventsDone.Contains(e.EventId) ? "done" : "NOT DONE")}")));
            int total = 0, done = 0;
            var notDone = new List<string>();
            foreach (var bin in career.Bins.Values.Where(b => b.Index >= 1 && b.Index <= 15))
            {
                var races = bin.WorldRaces.Concat(bin.BossRaces).Where(e => e.Start != null).ToList();
                int d = races.Count(e => w.RacesDone.Contains(e.Name));
                total += races.Count;
                done += d;
                bool beaten = w.Log.Any(l => l.Contains($"=== rival {bin.Index} beaten"));
                Console.WriteLine($"  bin {bin.Index,2}: {d}/{races.Count} races won (world {bin.WorldRaces.Count(e => w.RacesDone.Contains(e.Name))}/{bin.WorldRaces.Count(e => e.Start != null)}, " +
                                  $"boss {bin.BossRaces.Count(e => w.RacesDone.Contains(e.Name))}/{bin.BossRaces.Count(e => e.Start != null)}), rival {(beaten ? "beaten" : "NOT beaten")}; " +
                                  $"requires {bin.RequiredRacesWon} wins, {bin.RequiredBounty} bounty, {bin.RequiredChallenges} challenges");
                notDone.AddRange(races.Where(e => !w.RacesDone.Contains(e.Name)).Select(e => e.Name));
            }
            Console.WriteLine($"  career races won: {done}/{total}" + (notDone.Count > 0 ? $"; not won: {string.Join(" ", notDone)}" : ""));
            if (w.Stuck != null) Console.WriteLine("  STUCK: " + w.Stuck);
            Console.WriteLine($"  script errors: {errors}");
            foreach (var e in errorLog.Take(10)) Console.WriteLine("    " + e);
            Console.WriteLine($"  gameplay sessions {w.Sessions} ({w.Resumes} resumed from the save); SMS sent again right after a resume {w.RepeatSms}; bin activities started over {w.ReInitial}; boss races chained by PostRaceActivity {w.BossChainSteps}");
            Console.WriteLine($"  Drive calls with no C# (missing): " + (w.Missing.Count == 0 ? "none" : string.Join(" ", w.Missing.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))));
            var running = rt.RunningActivities.Select(a => $"{a.Name}[{a.Current?.Name}]").ToList();
            Console.WriteLine($"  running at the end ({running.Count}): " + string.Join(" ", running.Take(30)));
            Console.WriteLine($"  time {(DateTime.Now - t0).TotalSeconds:0.0} s, {instr} instructions");
            bool ok = w.Stuck == null && errors == 0 && w.RepeatSms == 0 && w.ReInitial == 0 && done == total && prologue.All(e => w.EventsDone.Contains(e.EventId)) &&
                      career.Bins.Keys.Where(k => k >= 1 && k <= 15).All(w.BossesBeaten.Contains);
            if (verbose || !ok) foreach (var l in w.Log.Skip(Math.Max(0, w.Log.Count - 60))) Console.WriteLine("    | " + l);
            Console.WriteLine(ok ? $"DRIVE OK [{variant}]: the prologue, every career race and the bin flow completed" : $"DRIVE FAILED [{variant}]");
            allOk &= ok;
        }
        Console.WriteLine(allOk ? "LUA DRIVE OK" : "LUA DRIVE FAILED");
    }
}
