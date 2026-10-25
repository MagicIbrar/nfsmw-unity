using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;

// `lua run [activity|*] [variant] [verbose]`: proves the C# Lua 5.0 VM runs every stategraph handler of gameplay.bin.
//   0. every chunk undumps and its main function defines the handler's global;
//   1. every activity (each non-template collection with states, plus templates nothing is derived from) runs from
//      Activity.Run in its own runtime against SimWorld, a stub game that answers the queries and answers the
//      scripts' requests the way the C++ does (NotifyScriptWhenLoaded → LoadingComplete, EShowRaceCountdown →
//      CountdownDone, NIS.Play → NISComplete, PlayTutorial → NotifyMovieFinished, racers drive to their SetRacerGoal
//      triggers → TriggerEnter, …) for up to 15 simulated minutes, once per scenario variant (win, lose, restart,
//      abandon, knocked out, engine blown, wrecked, time out, busted, escaped, world reload, and "probe": every 3 s
//      every message the current state's handlers take is delivered with fields built from the activity's data).
// Every message reaches a handler only in a state the flow really got to, so an error is a real one: a Lua runtime
// error (or a host exception) inside a handler. Templates that gameplay.bin never instantiates run without the
// instance data their scripts read; their errors are counted apart.
static class LuaRunProbe
{
    static readonly string[] RaceVariants = { "win", "lose", "restart", "abandon", "knockout", "engine", "destroyed", "timeout", "busted", "escaped", "loserestart", "reload", "freeroam", "online", "probe" };
    static readonly string[] OtherVariants = { "win", "probe", "reload", "restart", "busted", "timeout" };

    sealed class StubRacer
    {
        public int Index;
        public bool Finished;
        public override string ToString() => "racer" + Index;
    }

    /// <summary>The stub game (see the header).</summary>
    sealed class SimWorld : ILuaGameHost
    {
        public StategraphRuntime Rt;
        public GActivity Main;
        public string Variant = "win";
        public readonly Dictionary<string, int> Calls, Events;
        public bool Verbose;
        // racer indices are 1-based (Game_GetRacerIndex 0x60DAE0 returns i + 1, -1 when not racing; GetRacerElement,
        // GetRacerCharacter and RacerIsHuman take i and read racer info i − 1); the player is racer 1
        public readonly List<StubRacer> Racers = new List<StubRacer> { new StubRacer { Index = 1 } };
        readonly List<(float at, Action a)> _later = new List<(float, Action)>();
        readonly Dictionary<int, GInstance> _goals = new Dictionary<int, GInstance>();
        bool _racing, _incidentDone, _restartDone, _reloadDone;
        float _racingSince = -1;
        int _placing;
        public bool RaceFinished, Won, Abandoned;
        int _flip, _racesWon, _challenges, _bounty;
        int _nextTrigger, _nextRace;
        float _nextTriggerAt = 5f, _nextPlacementAt = 8f, _nextProbeAt = 3f;

        public SimWorld(Dictionary<string, int> calls, Dictionary<string, int> events) { Calls = calls; Events = events; }

        readonly HashSet<string> _statesRestarted = new HashSet<string>();

        /// <summary>
        /// Hooked to StateChanged: "restart" posts RestartRace 5 s into every state that takes it (once per state);
        /// "probe" delivers every message the new state takes right after it is entered.
        /// </summary>
        public void OnStateChanged(GActivity a, ScriptState s)
        {
            if (s == null || !a.Handlers.TryGetValue(s, out var hs)) return;
            if (Variant == "restart" && hs.Any(h => h.MessageId == StategraphRuntime.RestartRace) && _statesRestarted.Add(a.Name + "/" + s.Name))
                Later(5f, () => { if (a.Current == s) Rt.Post(StategraphRuntime.RestartRace); });
            if (Variant == "probe" && _statesProbed.Add(a.Name + "/" + s.Name)) Probe(a);
        }

        readonly HashSet<string> _statesProbed = new HashSet<string>();

        void Probe(GActivity a)
        {
            var s = a.Current;
            if (s == null || !a.Handlers.TryGetValue(s, out var hs)) return;
            var conns = Rt.Connections(a.Instance).Concat(Rt.Children(a.Instance)).Distinct().ToList();
            foreach (var kind in hs.Select(h => h.MessageId).Distinct())
            {
                if (kind == StategraphRuntime.StateEnter || kind == StategraphRuntime.StateExit) continue;
                foreach (var fields in Synth(a, hs.Where(h => h.MessageId == kind), kind, this, conns).Take(6))
                {
                    if (a.Current != s || !a.Running) return;
                    Rt.DeliverNow(a, kind, fields);
                }
            }
        }

        void Later(float dt, Action a) => _later.Add((Rt.SimTime + dt, a));
        StubRacer Player => Racers[0];

        /// <summary>The instances every running activity connects to (what the player can drive into).</summary>
        // (not the wrong-way ResetsPlayer plane: offline, entering it restarts the race from prerace)
        List<GInstance> Triggers() => Rt.RunningActivities.SelectMany(a => Rt.Connections(a.Instance).Concat(Rt.Children(a.Instance)))
            .Where(t => !t.Record.Bool("ResetsPlayer") || Variant == "probe").Distinct().ToList();

        public void Step()
        {
            for (int i = 0; i < _later.Count; i++)
                if (_later[i].at <= Rt.SimTime) { var a = _later[i].a; _later.RemoveAt(i--); a(); }
            // the player keeps driving through the triggers (speed traps, scripted triggers, engage zones, gates)
            if (Rt.SimTime >= _nextTriggerAt)
            {
                _nextTriggerAt = Rt.SimTime + 5f;
                var ts = Triggers();
                if (ts.Count > 0)
                {
                    var t = ts[_nextTrigger++ % ts.Count];
                    Rt.Post(StategraphRuntime.TriggerEnter, ("Sender", t), ("Element", Player));
                    Later(1f, () => Rt.Post(StategraphRuntime.TriggerExit, ("Sender", t), ("Element", Player)));
                }
            }
            // a Blacklist bin: the player wins its races one by one (WorldRaces, then BossRaces), earns bounty, passes challenges
            var rec = Main.Instance.Record;
            if (Rt.SimTime >= _nextPlacementAt && (rec.Has("WorldRaces") || rec.Has("BossRaces") || rec.Has("GateActivity")))
            {
                _nextPlacementAt = Rt.SimTime + 8f;
                var races = rec.Keys("WorldRaces").Concat(rec.Keys("BossRaces")).Concat(rec.Keys("GateActivity")).ToList();
                if (races.Count > 0)
                {
                    var r = Rt.Instance(races[_nextRace++ % races.Count]);
                    _racesWon++;
                    Rt.Post(StategraphRuntime.NotifyRacePlacement, ("Activity", r), ("Racer", Player), ("Placing", 1));
                }
                _bounty += 250000;
                Rt.Post(StategraphRuntime.NotifyPlayerRep, ("RepPoints", 25000));
                if ((_nextRace & 1) == 0) { _challenges++; Rt.Post(StategraphRuntime.NotifyChallengePassed); }
            }
            // the race incident of the variant, 10 s into racing
            if (_racing && !_incidentDone && Rt.SimTime - _racingSince >= 10f)
            {
                _incidentDone = true;
                switch (Variant)
                {
                    case "abandon": Abandoned = true; Rt.Post(StategraphRuntime.NotifyRaceAbandoned); break;
                    case "knockout": Rt.Post(StategraphRuntime.NotifyKnockedOut, ("Racer", Player)); break;
                    case "engine": Rt.Post(StategraphRuntime.NotifyEngineBlown, ("Racer", Player)); break;
                    case "destroyed": Rt.Post(StategraphRuntime.NotifyVehicleDestroyed, ("Racer", Player)); break;
                    case "timeout": Rt.Post(StategraphRuntime.NotifyRaceTimeExpired, ("Racer", Player)); break;
                    case "busted": Rt.Post(StategraphRuntime.PerpBusted, ("Perpetrator", Player)); break;
                    case "escaped": Rt.Post(StategraphRuntime.PerpEscaped, ("Perpetrator", Player)); break;
                }
            }
            if (_racing && Rt.SimTime - _racingSince >= 1f && ((int)(Rt.SimTime * 4) % 4) == 0) Rt.Post(StategraphRuntime.NotifyRaceTimeSecTick);
            // probe: whatever the current states take, now
            if (Variant == "probe" && Rt.SimTime >= _nextProbeAt)
            {
                _nextProbeAt = Rt.SimTime + 3f;
                foreach (var a in Rt.RunningActivities.ToList()) Probe(a);
            }
        }

        void DriveTo(int i)
        {
            if (!_racing || !_goals.TryGetValue(i, out var g) || g == null || i < 1 || i > Racers.Count) return;
            var r = Racers[i - 1];
            // the player is fastest (5 s per goal; AI i: 5 + i s), except in "lose" (30 s)
            float dt = i == 1 && (Variant == "lose" || Variant == "loserestart" && !_restartDone) ? 30f : 5f + i;
            Later(dt, () =>
            {
                if (_goals.TryGetValue(i, out var cur) && cur == g && !r.Finished)
                    Rt.Post(StategraphRuntime.TriggerEnter, ("Sender", g), ("Element", r));
            });
        }

        public object Call(LuaBinding b, object[] args)
        {
            Calls[b.FullName] = Calls.TryGetValue(b.FullName, out int n) ? n + 1 : 1;
            if (Verbose) Console.WriteLine($"    {Rt.SimTime,6:0.0} call {b.FullName}({string.Join(", ", args.Select(a => a ?? "nil"))})");
            _flip++;
            switch (b.Table + "." + b.Name)
            {
                // ---- queries
                case "Game.GetNumRacers": return Racers.Count;
                case "Game.GetRacerIndex": return args[0] is StubRacer r ? r.Index : -1;
                case "Game.GetRacerElement": { int i = (int)args[0]; return i >= 1 && i <= Racers.Count ? Racers[i - 1] : null; }
                case "Game.GetPlayerElement": return Player;
                case "Game.GetRacerCharacter":
                    {
                        int i = (int)args[0];
                        var opp = Main.Instance.Record.Keys("Opponents");
                        return i >= 2 && i - 2 < opp.Length ? Rt.Instance(opp[i - 2]) : null;
                    }
                case "Game.RacerIsHuman": return (int)args[0] == 1;
                case "Game.PlayerIsLocal": return args[0] is StubRacer pr && pr.Index == 1;
                case "ISimable.IsPlayer": return args.Length > 0 && args[0] is StubRacer ip && ip.Index == 1;
                case "Game.GetSimableSpeedKmh": return 150f;
                case "Game.SimableDistance": return 100f;
                case "Game.SimableAngle": return 10f;
                case "Game.CalculateRanking": return args[1] is int p ? p : 1;
                case "Game.GetPlayerBounty": return _bounty;
                case "Bin.GetNumRacesWon": return _racesWon;
                case "Bin.GetNumChallengesPassed": return _challenges;
                case "Game.IsCareerMode": return true;
                case "Game.SetCurrentTimeOfDay": return Variant == "reload" && !_reloadDone;
                case "Game.DoSpecialSetup": return false;
                case "Game.AllowEngageEvents":
                case "Game.AllowMenuGates":
                case "Game.AllowEngageSafehouse":
                case "Game.IsActiveMenuGate":
                case "Game.IsActiveSpeedTrap":
                case "Game.IsRaceUnlocked": return true;
                case "Game.IsRaceCompleted": return (_flip & 1) == 0;
                case "Game.AllRacersDone": return Racers.All(x => x.Finished);
                case "Game.AllHumanPlayersDone": return Player.Finished || Abandoned;

                // ---- what the C++ does that the scripts then wait for
                case "Game.ReloadWorld":
                    _reloadDone = true;
                    Later(1f, () => Rt.Post(StategraphRuntime.LoadingComplete));
                    return null;
                case "Game.InitRacers":
                    {
                        Racers.RemoveRange(1, Racers.Count - 1);
                        foreach (var x in Racers) x.Finished = false;
                        int opp = Main.Instance.Record.Keys("Opponents").Length;
                        for (int i = 0; i < Math.Min(opp, 7); i++) Racers.Add(new StubRacer { Index = i + 2 });
                        return null;
                    }
                case "Game.RestoreStartPositions":
                    foreach (var x in Racers) x.Finished = false;
                    _placing = 0;
                    return null;
                case "RaceStatus.NotifyScriptWhenLoaded":
                    Later(0.5f, () => Rt.Post(StategraphRuntime.LoadingComplete));
                    return null;
                case "Game.StartRaceTimers":
                    _racing = true;
                    _racingSince = Rt.SimTime;
                    foreach (var i in _goals.Keys.ToList()) DriveTo(i);
                    return null;
                case "Game.SetRacerGoal":
                    {
                        int i = (int)args[0];
                        _goals[i] = args[1] as GInstance;
                        DriveTo(i);
                        return null;
                    }
                case "Game.NotifyRaceFinished":
                    if (args[0] is StubRacer fr)
                    {
                        fr.Finished = true;
                        if (fr.Index == 1)
                        {
                            RaceFinished = true;
                            Won = _placing == 0;
                            if (Variant == "online") Later(1f, () => Rt.Post(StategraphRuntime.NotifyOnlineRaceOver, ("ShowPostRace", true)));
                        }
                        _placing++;
                    }
                    return null;
                case "Game.PlayTutorial":
                    // 0x6124E0: a career drag/tollbooth/speedtrap race not seen before plays its tutorial movie, else
                    // MNotifyMovieFinished is posted at once
                    Rt.Post(StategraphRuntime.NotifyMovieFinished);
                    return null;
                case "NIS.Play":
                    Later(2f, () => Rt.Post(StategraphRuntime.NisComplete));
                    return null;
                case "Game.EnterPostRaceFlow":
                    Later(2f, () => Rt.Post(Variant == "freeroam" ? StategraphRuntime.EnterFreeRoam : StategraphRuntime.FlowReadyForOutro));
                    return null;
                case "Game.IsOnlineGame":
                case "Game.IsLANGame":
                    return Variant == "online";
                case "Game.ShowOnlinePostRaceScreen":
                    return true;
                case "Game.ShowLosingPostRaceScreen":
                    if (Variant == "loserestart" && !_restartDone)
                    {
                        _restartDone = true;
                        Later(2f, () => Rt.Post(StategraphRuntime.RestartRace));
                    }
                    else Later(2f, () => { Abandoned = true; Rt.Post(StategraphRuntime.NotifyRaceAbandoned); });
                    return null;
                case "Game.ShowWinningPostRaceScreen":
                    Later(2f, () => Rt.Post(StategraphRuntime.FlowReadyForOutro));
                    return null;
                case "Game.SabotageEngine":
                case "Game.BlowEngine":
                    Later(3f, () => Rt.Post(StategraphRuntime.NotifyEngineBlown, ("Racer", Player)));
                    return null;
                case "Game.EnterEngagableTrigger":
                    Later(1f, () => Rt.Post((_flip & 2) == 0 ? StategraphRuntime.AcceptEnterCareerEvent : StategraphRuntime.DeclineEnterCareerEvent, ("SimTime", Rt.SimTime)));
                    return null;
                case "Game.EnterGateZone":
                    Later(1f, () => Rt.Post(StategraphRuntime.EnterSafehouse, ("ZoneType", args[1])));
                    return null;
                case "Game.ForcePursuitStart":
                    // escaped (win) or busted after 20 s; in "timeout" the pursuit goes on until the time limit
                    if (Variant == "win" || Variant == "escaped" || Variant == "busted" || Variant == "probe") Later(20f, () => Rt.Post(Variant == "busted" ? StategraphRuntime.PerpBusted : StategraphRuntime.PerpEscaped, ("Perpetrator", Player)));
                    return null;
                case "Game.AwardPlayerBounty":
                    _bounty += args[0] is int ab ? ab : 0;
                    Rt.Post(StategraphRuntime.NotifyPlayerRep, ("RepPoints", args[0]));
                    return null;
                case "Game.DoSafeHouseIntro":
                    // 0x605560: the storyfmv_saf25 FMV, then the safehouse screens
                    Later(2f, () => Rt.Post(StategraphRuntime.NotifyMovieFinished));
                    Later(3f, () => Rt.Post(StategraphRuntime.CareerIntroDone));
                    return null;
                case "Camera.SetGenericCamera":
                    Later(2f, () => Rt.Post(StategraphRuntime.IceCameraFinished));
                    return null;
            }
            return null;
        }

        public void Event(string name, object[] args)
        {
            Events[name] = Events.TryGetValue(name, out int n) ? n + 1 : 1;
            if (Verbose) Console.WriteLine($"    {Rt.SimTime,6:0.0} event {name}({string.Join(", ", args.Select(a => a ?? "nil"))})");
            switch (name)
            {
                case "EShowRaceCountdown": Later(3f, () => Rt.Post(StategraphRuntime.CountdownDone)); break;
                case "EPlayRaceNIS": Later(2f, () => Rt.Post(StategraphRuntime.NisComplete)); break;
                case "EPlayRaceMovie": Later(2f, () => Rt.Post(StategraphRuntime.NotifyMovieFinished)); break;
                case "EShowMessageScreen": Later(2f, () => Rt.Post(StategraphRuntime.NotifyMessageDone)); break;
                case "EShowPostPursuit": Later(2f, () => Rt.Post(StategraphRuntime.FlowReadyForOutro)); break;
                case "ECellCall":
                    Later(0.5f, () => Rt.Post(StategraphRuntime.NotifySpeechStatus, ("SoundID", args.Length > 0 ? args[0] : 0)));
                    Later(0.5f, () => Rt.Post(StategraphRuntime.NotifyCellCallStarted));
                    Later(5f, () => Rt.Post(StategraphRuntime.NotifyCellCallComplete));
                    break;
                case "EAddSMS":
                    Later(1f, () => Rt.Post(StategraphRuntime.NotifySpeechStatus, ("SoundID", args.Length > 0 ? args[0] : 0)));
                    break;
            }
        }

        public void Log(string line)
        {
            if (Verbose || line.StartsWith("error")) Console.WriteLine($"    {Rt?.SimTime ?? 0,6:0.0} {line}");
        }
    }

    public static void Run(string root, GameplayScripts gs, string[] args)
    {
        bool verbose = args.Contains("verbose");
        var rest = args.Skip(3).Where(a => a != "verbose").ToList();
        string only = rest.Count > 0 && rest[0] != "*" ? rest[0] : null;
        string onlyVariant = rest.Count > 1 ? rest[1] : null;
        var t0 = DateTime.Now;
        var calls = new Dictionary<string, int>();
        var events = new Dictionary<string, int>();
        var executed = new HashSet<string>();
        var errors = new List<string>();
        var templateErrors = new List<string>();
        long instructions = 0;
        var ops = new long[35];

        // 0. the chunks
        var probe = new StategraphRuntime(gs, new SimWorld(calls, events));
        int loadErr = gs.Handlers.Count(h => h.LoadError != null);
        int defined = gs.Handlers.Count(h => probe.L.Globals.Get(h.Name).Type == LuaType.Function);
        Console.WriteLine($"Lua 5.0.1 (float numbers) chunks: {gs.Handlers.Count} handlers, {loadErr} undump errors, {defined} handler functions defined, {gs.States.Count} states, {probe.Errors} main-chunk errors");

        // the activities: collections with states that are not templates, plus templates nothing is derived from
        var acts = new List<(AttribCollection c, bool bareTemplate)>();
        foreach (var c in gs.All)
        {
            if (only != null && c.Name != only) continue;
            if (gs.StatesOf(c).Count == 0) continue;
            bool template = c.Bool("Template");
            if (template && gs.All.Any(x => x != c && !x.Bool("Template") && gs.DerivesFrom(x, c.Key) && gs.StatesOf(x).Count > 0)) continue;
            acts.Add((c, template));
        }
        Console.WriteLine($"activities: {acts.Count} ({acts.Count(a => a.bareTemplate)} templates with no instance: {string.Join(" ", acts.Where(a => a.bareTemplate).Select(a => a.c.Name))})");

        int scenarios = 0, races = 0, finished = 0, won = 0, reachedDone = 0;
        var final = new Dictionary<string, int>();
        var stuck = new List<string>();
        foreach (var (c, bare) in acts)
        {
            bool isRace = gs.StatesOf(c).Any(s => s.Name == "racing");
            foreach (var variant in (isRace ? RaceVariants : OtherVariants).Where(v => onlyVariant == null || v == onlyVariant))
            {
                scenarios++;
                var world = new SimWorld(calls, events) { Verbose = verbose, Variant = variant };
                var rt = new StategraphRuntime(gs, world) { ErrorLogLimit = 100000 };
                world.Rt = rt;
                rt.StateChanged += world.OnStateChanged;
                var a = rt.Activity(rt.Instance(c));
                world.Main = a;
                if (verbose) Console.WriteLine($"== {c.Name} [{variant}]");
                rt.Run(a.Instance);
                rt.Post(StategraphRuntime.EnteringGameplay);
                rt.Pump();
                for (int i = 0; i < 15 * 60 * 4 && (a.Running || rt.PendingEvents > 0) && rt.L.Instructions < 20000000; i++)
                {
                    world.Step();
                    rt.Tick(0.25f);
                }
                string fin = a.Current?.Name ?? "-";
                if (variant == "win") final[fin] = final.TryGetValue(fin, out int fn) ? fn + 1 : 1;
                if (isRace && variant == "win")
                {
                    races++;
                    if (world.RaceFinished) finished++;
                    if (world.Won) won++;
                    if (rt.Executed.Any(h => h.Name.StartsWith("stategraph_basicrace_done_"))) reachedDone++;
                    else stuck.Add($"{c.Name}({c.Parent?.Name}:{fin})");
                }
                if (verbose) Console.WriteLine($"   ends in {fin} at {rt.SimTime:0} s, race finished {world.RaceFinished}, won {world.Won}, {rt.Errors} errors");
                foreach (var h in rt.Executed) executed.Add(h.Name);
                (bare ? templateErrors : errors).AddRange(rt.ErrorLog.Select(e => $"[{variant}] {e}"));
                instructions += rt.L.Instructions;
                for (int o = 0; o < 35; o++) ops[o] += rt.L.OpCounts[o];
            }
        }
        // 3. handlers no data-driven flow reaches (templates without instances, branches no instance has the data
        //    for): their state is entered directly in an activity that has it, after 30 s of its natural flow
        var direct = new List<string>();
        int directErrors = 0;
        foreach (var h in gs.Handlers.Where(h => !executed.Contains(h.Name)).ToList())
        {
            var st = gs.States.FirstOrDefault(x => x.Record.Key == h.StateKey);
            var owner = gs.Get(h.OwnerKey);
            if (st == null || owner == null) continue;
            var host = gs.All.FirstOrDefault(x => x == owner && !x.Bool("Template")) ?? gs.All.FirstOrDefault(x => !x.Bool("Template") && gs.DerivesFrom(x, owner.Key)) ?? owner;
            var world = new SimWorld(calls, events) { Verbose = verbose, Variant = "win" };
            var rt = new StategraphRuntime(gs, world) { ErrorLogLimit = 100000 };
            world.Rt = rt;
            var a = rt.Activity(rt.Instance(host));
            world.Main = a;
            rt.Run(a.Instance);
            rt.Pump();
            for (int i = 0; i < 120 && a.Running; i++) { world.Step(); rt.Tick(0.25f); }
            var state = a.States.FirstOrDefault(x => x.Record.Key == h.StateKey);
            if (state == null) continue;
            rt.ForceState(a, state);
            var conns = rt.Connections(a.Instance).Concat(rt.Children(a.Instance)).Distinct().ToList();
            if (h.MessageId != StategraphRuntime.StateEnter && h.MessageId != StategraphRuntime.StateExit)
                foreach (var fields in Synth(a, new[] { h }, h.MessageId, world, conns).Take(8))
                {
                    if (a.Current != state) rt.ForceState(a, state);
                    rt.DeliverNow(a, h.MessageId, fields);
                }
            if (h.MessageId == StategraphRuntime.StateExit) rt.ForceState(a, null);
            rt.Pump();
            if (rt.Executed.Contains(h)) { executed.Add(h.Name); direct.Add($"{h.Name} (in {host.Name})"); }
            directErrors += rt.Errors;
            (host.Bool("Template") ? templateErrors : errors).AddRange(rt.ErrorLog.Select(e => $"[direct] {e}"));
            instructions += rt.L.Instructions;
            for (int o = 0; o < 35; o++) ops[o] += rt.L.OpCounts[o];
        }
        Console.WriteLine($"scenarios: {scenarios}; races {races} (variant win): player finished {finished}, won {won}, reached `done` {reachedDone}; final states (win): " +
                          string.Join(" ", final.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
        Console.WriteLine($"phase 3 (state entered directly): {direct.Count} handlers, {directErrors} errors");
        foreach (var d in direct) Console.WriteLine("  direct: " + d);
        if (stuck.Count > 0) Console.WriteLine($"races the stub world does not bring to `done` (variant win): " + string.Join(" ", stuck));
        var missed = gs.Handlers.Where(h => !executed.Contains(h.Name)).ToList();
        Console.WriteLine($"handlers executed: {executed.Count}/{gs.Handlers.Count}");
        foreach (var h in missed.Take(40)) Console.WriteLine($"  not run: {h.Name} (owner {gs.Get(h.OwnerKey)?.Name}, msg {StategraphRuntime.MessageName(h.MessageId)})");
        Console.WriteLine($"VM: {instructions} instructions; opcodes executed: " + string.Join(" ", Enumerable.Range(0, 35).Where(o => ops[o] > 0).Select(o => $"{LuaState.OpNames[o]}={ops[o]}")));
        Console.WriteLine($"bindings called: {calls.Count}: " + string.Join(" ", calls.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Console.WriteLine($"events: {events.Count}: " + string.Join(" ", events.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Console.WriteLine($"errors: {errors.Count} (+ {templateErrors.Count} in templates gameplay.bin never instantiates)");
        foreach (var g in errors.Concat(templateErrors).GroupBy(e => System.Text.RegularExpressions.Regex.Replace(e, @" \([^)]*\):", ":")).Take(60)) Console.WriteLine($"  {g.Count()}x {g.First()}");
        Console.WriteLine($"time {(DateTime.Now - t0).TotalSeconds:0.0} s");
        Console.WriteLine(errors.Count == 0 && missed.Count == 0 && loadErr == 0 ? "LUA OK: every chunk loaded and every handler ran without errors" : "LUA FAILED");
    }

    /// <summary>Message fields for a handler set, from what the scripts read off r1 (`lua scan`).</summary>
    static IEnumerable<(string, object)[]> Synth(GActivity a, IEnumerable<ScriptHandler> hs, uint kind, SimWorld w, List<GInstance> conns)
    {
        if (conns.Count == 0) conns = new List<GInstance> { a.Instance };   // a bare template: itself as the sender
        object player = w.Racers[0];
        object ai = w.Racers.Count > 1 ? w.Racers[1] : w.Racers[0];
        switch (kind)
        {
            case StategraphRuntime.TriggerEnter:
            case StategraphRuntime.TriggerExit:
                foreach (var c in conns)
                {
                    yield return new (string, object)[] { ("Sender", c), ("Element", player) };
                    yield return new (string, object)[] { ("Sender", c), ("Element", ai) };
                }
                break;
            case StategraphRuntime.NotifyRacePlacement:
                foreach (var c in conns) yield return new (string, object)[] { ("Activity", c), ("Racer", player), ("Placing", 1) };
                foreach (int p in new[] { 1, 2 })
                {
                    yield return new (string, object)[] { ("Activity", a.Instance), ("Racer", player), ("Placing", p) };
                    yield return new (string, object)[] { ("Activity", a.Instance), ("Racer", ai), ("Placing", p) };
                }
                break;
            case StategraphRuntime.NotifyFinished:
                foreach (var c in conns) yield return new (string, object)[] { ("Sender", c) };
                yield return new (string, object)[] { ("Sender", a.Instance) };
                break;
            case StategraphRuntime.NotifyTimer:
                {
                    var names = new HashSet<string>();
                    foreach (var h in hs) foreach (var p in LuaProbe.Walk(h.Proto)) foreach (var k in p.K) if (k.IsString) names.Add(k.Str);
                    foreach (var n in names) yield return new (string, object)[] { ("Name", n) };
                    break;
                }
            case StategraphRuntime.NotifySimTick:
            case StategraphRuntime.DeclineEnterCareerEvent:
                yield return new (string, object)[] { ("SimTime", w.Rt.SimTime) };
                break;
            case StategraphRuntime.NotifyKnockedOut:
            case StategraphRuntime.NotifyEngineBlown:
            case StategraphRuntime.NotifyVehicleDestroyed:
            case StategraphRuntime.NotifyRaceTimeExpired:
                yield return new (string, object)[] { ("Racer", ai) };
                yield return new (string, object)[] { ("Racer", player) };
                break;
            case StategraphRuntime.NotifyPlayerRep:
                yield return new (string, object)[] { ("RepPoints", 1000) };
                break;
            case StategraphRuntime.NotifySpeedTrap:
                foreach (var c in conns) yield return new (string, object)[] { ("TrapActivity", c) };
                break;
            case StategraphRuntime.NotifyMilestoneReached:
            case StategraphRuntime.NotifyMilestoneProgress:
                yield return new (string, object)[] { ("MilestoneName", a.Instance.Record.Text("MilestoneName") ?? "pursuit_length"), ("ValueReached", 1e6f) };
                yield return new (string, object)[] { ("MilestoneName", "pursuit_bounty"), ("ValueReached", 100f) };
                break;
            case StategraphRuntime.NotifySpeechStatus:
                yield return new (string, object)[] { ("SoundID", 1) };
                break;
            case StategraphRuntime.PerpEscaped:
            case StategraphRuntime.PerpBusted:
                yield return new (string, object)[] { ("Perpetrator", player) };
                break;
            case StategraphRuntime.EnterSafehouse:
                yield return new (string, object)[] { ("ZoneType", a.Instance.Record.Text("ZoneType") ?? "safehouse") };
                break;
            case StategraphRuntime.NotifyOnlineRaceOver:
                yield return new (string, object)[] { ("ShowPostRace", true) };
                break;
            default:
                yield return new (string, object)[0];
                break;
        }
    }
}
