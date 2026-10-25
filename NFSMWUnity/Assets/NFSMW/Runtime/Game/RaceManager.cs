using System;
using Random = UnityEngine.Random;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game.Gameplay;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Installed performance (FECustomizationRecord.InstalledPhysics + Tunings): the spec re-read with each part at the
    /// owned package level 0..N lerped L/N, Junkman parts multiplied by junkman/default, and the active tuning preset
    /// folded in (CarSpec.FromAttributes, physics.md §2, customization.md §7).
    /// </summary>
    public static class Upgrades
    {
        public static CarSpec Apply(CarSpec s, OwnedCar c) => c == null ? s : CarSpec.FromAttributes(GameData.Attributes, s.Name, c);
    }

    public static class CarSpawner
    {
        /// <summary>A driveable car (physics + wheels) at a Unity position/rotation.</summary>
        public static PlayerCar Spawn(string car, Color paint, OwnedCar perf, Vector3 pos, Quaternion rot, string name = null)
        {
            var built = CarBuilder.Build(car.ToUpperInvariant(), paint, null, perf);
            var root = built.Root;
            root.name = name ?? car;
            root.AddComponent<Rigidbody>();
            GameBootstrap.AddBodyCollider(built);
            var pc = root.AddComponent<PlayerCar>();
            pc.Setup(Upgrades.Apply(built.Spec, perf), built.Wheels);
            pc.BrakeLights = built.BrakeLights;
            pc.UseDeviceInput = false;
            EngineAudio.Attach(pc, car, 0.35f);
            CarSfx.Attach(pc, false);
            if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out var hit, 15f)) pos = hit.point;
            pc.SetResetPoint(pos, rot);
            pc.Teleport(pos + Vector3.up * 0.4f, rot);
            return pc;
        }
    }

    /// <summary>
    /// Runs one event from gameplay.bin the way GRaceStatus and the `basicrace` stategraph do (specs/gameplay_core.md
    /// §1-2): the grid of Game.InitRacers, a goal sequence per racer (checkpoints, then the finish, wrapping on circuits)
    /// whose triggers must be driven through in order, progress measured against the next goal along the race route,
    /// GRaceStatus rankings, 20/40/60/80 % splits, lap knockouts, speedtrap points with the overtime penalty, the
    /// tollbooth clock, the wrong-way reset, projected times for those still racing, adaptive difficulty, and the career
    /// follow-ups (cash, the boss chain's PostRaceActivity, RaceAbandoned).
    /// </summary>
    public sealed partial class RaceManager : MonoBehaviour
    {
        public static RaceManager Active { get; private set; }

        /// <summary>One racer: the scene side (car, AI) plus the GRacerInfo record (gameplay_core.md §0).</summary>
        public sealed class Entrant
        {
            public string Name;
            public PlayerCar Car;
            public AIRacer AI;
            public Racer Racer;
            public int PathIndex;
            /// <summary>Metres along the race route, held at the racer's next goal until that goal's trigger is entered.</summary>
            public float Progress;
            public bool Finished, Out, Human, Remote;
            public LanSession.Peer Peer;
            /// <summary>Race clock at the finish (the clock starts at the event's StartTime).</summary>
            public float FinishTime;
            /// <summary>mRanking (1-based) and mAiRanking (0 for humans).</summary>
            public int Place, AiRank;
            public readonly List<float> Laps = new List<float>();      // race time at the end of each lap / stage
            public readonly List<int> LapPlaces = new List<int>();
            /// <summary>Speedtrap: the best speed (km/h) through each camera (mSpeedTrapSpeed) and the point total (mPointTotal).</summary>
            public readonly List<float> TrapSpeeds = new List<float>();
            public float Points;
            // GRacerInfo
            public int LapsCompleted, CheckpointsHit, GoalIndex;
            public float PctRaceComplete, PctLapComplete, StopTime;
            public bool Totalled, EngineBlown, Busted, DNF;
            public readonly float[] SplitTimes = new float[4];
            public readonly int[] SplitRanks = new int[4];
            /// <summary>Tollbooth: the time left on the clock as each booth was passed (mTimeRemainingToBooth).</summary>
            public readonly List<float> BoothTimes = new List<float>();
            /// <summary>GRacerInfo mTopSpeed and mDistanceDriven (0x5E92E0; m/s and m): UpdateRaceScore's top and average speed.</summary>
            public float TopSpeed, DistanceDriven;
            internal Vector3 LastPos;
            internal bool HasLastPos;
            internal float RouteProgress;
            internal bool[] InTrap = new bool[0];
            /// <summary>Still racing (the script's InRace): not finished and not knocked out.</summary>
            public bool InRace => !Finished && !Out;
        }

        /// <summary>A goal of the race (checkpoint or finish line) in lap order, with where it falls on the route.</summary>
        sealed class Goal
        {
            public RaceTrigger Trigger;
            public float Progress;
            public bool Finish;
            public int Lap, TimeBonus;
        }

        readonly List<(Vector3 pos, float radius, float threshold)> _traps = new List<(Vector3, float, float)>();
        bool _suddenDeath;
        int _lastSecondTick;
        public bool SpeedTrapRace => Event != null && Event.Type == RaceType.SpeedTrap;
        /// <summary>RankPlayersByPoints (speedtrap races).</summary>
        bool ByPoints => Event != null && (Event.RankByPoints || Event.Type == RaceType.SpeedTrap);

        public RaceEvent Event { get; private set; }
        public readonly List<Entrant> Entrants = new List<Entrant>();
        public bool Countdown { get; private set; }
        /// <summary>The intro NIS is playing (the countdown waits for it).</summary>
        public bool Intro { get; private set; }
        public string StatusText { get; private set; } = "";
        /// <summary>The race master clock: starts at the event's StartTime (GRaceStatus::StartMasterTimer 0x5FDFF0).</summary>
        public float RaceTime { get; private set; }
        /// <summary>Player's share of the route (0..1).</summary>
        public float Completion => CompletionOf(_player);
        public float CompletionOf(Entrant e) => e != null ? Mathf.Clamp01(e.PctRaceComplete * 0.01f) : 0;
        /// <summary>The player is driving against the route.</summary>
        public bool WrongWay { get; private set; }
        /// <summary>Tollbooth clock: GetRaceTimeRemaining (0x5FE090) = TimeLimit + bonus − race clock; -1 for races without a limit.</summary>
        public float TimeLeft { get; private set; } = -1;
        float _bonusTime;
        bool _timeExpiredSent;
        /// <summary>The race route (Unity space), start to finish over all laps.</summary>
        public IReadOnlyList<Vector3> Route => _path;
        List<Vector3> _path;
        float[] _cum;
        float _lapLength;
        int _laps = 1;
        AIDriver _pathTool;
        Entrant _player;
        readonly List<Goal> _goals = new List<Goal>();
        int _goalsPerLap = 1;
        RaceTrigger _wrongWayReset;
        int _numberFinished;
        /// <summary>GRaceStatus mCaluclatedAdaptiveGain: the adaptive difficulty moved already in this race.</summary>
        public bool AdaptiveComputed { get; set; }

        /// <summary>The human player's entrant.</summary>
        public Entrant PlayerEntrant => _player;
        /// <summary>The race route's length (every lap): GRaceStatus fRaceLength.</summary>
        public float RouteLength => _cum != null && _cum.Length > 0 ? _cum[_cum.Length - 1] : 0;

        /// <summary>
        /// The AI's target: Game.SetRacerGoal (0x611700) aims each racer at its own next goal (the next checkpoint, or the
        /// finish line), so it changes only when that goal's trigger is driven through; null once the racer is done.
        /// </summary>
        public Vector3? TargetFor(Entrant e)
        {
            if (e == null || e.Finished || e.Out) return null;
            if (Driven && _scriptGoals.TryGetValue(e, out var sg)) return sg.Trigger.Centre;   // Game.SetRacerGoal
            if (_goals.Count == 0) return null;
            return _goals[Mathf.Clamp(e.GoalIndex, 0, _goals.Count - 1)].Trigger.Centre;
        }

        /// <summary>The direction of the racer's goal trigger (AITarget::Aquire's direction).</summary>
        public Vector3? TargetDirectionFor(Entrant e)
        {
            if (e == null || e.Finished || e.Out) return null;
            if (Driven && _scriptGoals.TryGetValue(e, out var sg)) return sg.Trigger.Normal;
            if (_goals.Count == 0) return null;
            return _goals[Mathf.Clamp(e.GoalIndex, 0, _goals.Count - 1)].Trigger.Normal;
        }

        float _countdown;
        readonly List<GameObject> _markers = new List<GameObject>();
        bool _done;
        float _nextState;

        /// <summary>Story hooks: a per-frame script while racing, and a race-over handler that replaces the result box.</summary>
        public static Action<RaceManager> Script;
        public static Func<RaceManager, bool, bool> OnEnd;

        public PlayerCar PlayerCarObject => _player?.Car;

        /// <summary>The player is within range of one of the event's named trigger planes (gameplay.bin children).</summary>
        public bool PlayerNear(RaceEvent ev, string trigger, float range)
        {
            if (_player?.Car == null || !ev.Triggers.TryGetValue(trigger, out var t)) return false;
            var d = _player.Car.transform.position - Coords.ToUnity(t.Position);
            d.y = 0;
            return d.magnitude < Mathf.Max(range, t.Width * 0.5f);
        }

        public void ForceEnd(bool won) { if (!_done) End(won); }

        /// <summary>MRestartRace (posted by ERestartRace 0x6307F0) for listeners such as the music and speech [ours: event].</summary>
        public static event Action<RaceEvent> Restarted;

        /// <summary>The race running now is a D-Day (prologue) race: GRaceParameters::GetIsDDayRace on the race parameters
        /// (SFXObj_PFEATrax::TestToLicensed 0x4DF56D, PauseMenu, PostRaceResultsScreen, SetRoaming).</summary>
        public static bool IsDDayRace => Active != null && Active.Event != null && Active.Event.DDayRace;

        /// <summary>
        /// ERestartRace (ctor 0x6307F0; the pause menu's and the results screen's Restart): EAXSound::ReStartRace(actively
        /// racing and not a challenge race), UpdateAdaptiveDifficulty(Restart, none) on the race being left, MRestartRace,
        /// EFadeScreenOn; the stategraph then goes back to `prerace` (here the race is set up again).
        /// </summary>
        public static void RestartRace(RaceEvent ev)
        {
            var race = Active;
            if (race != null && race.Event == ev) AdaptiveDifficulty.Update(race, AdaptiveDifficulty.Reason.Restart, null);
            // ERestartRace 0x63090B → World_RestoreProps (0x74D320): the smashed-prop record cleared, RestorePursuitBreakerIcons(-1)
            Smackable.RestoreAll();
            GameIcons.RestorePursuitBreakerIcons();
            try { Restarted?.Invoke(ev); } catch (Exception e) { Debug.LogException(e); }
            Controls.CameraShaker.ResetCameraShakers();   // ERestartRace 0x63094F
            GameFlow.StartRace(ev, skipMovie: true);
        }

        public float CountdownLeft => _countdown;

        /// <summary>The lap an entrant is on (1-based): mLapsCompleted + 1.</summary>
        public int LapOf(Entrant e) => e == null ? 1 : e.Remote ? (int)(e.Progress / Mathf.Max(1f, _lapLength)) + 1 : e.LapsCompleted + 1;

        public static RaceManager Begin(RaceEvent ev)
        {
            if (Active) Active.Abort(false);
            var go = new GameObject("Race " + ev.EventId);
            var rm = go.AddComponent<RaceManager>();
            Active = rm;
            rm.Setup(ev);
            return rm;
        }

        void Setup(RaceEvent ev)
        {
            Event = ev;
            var boot = GameBootstrap.Instance;
            // the career has no split screen: a flag left over from a split-screen quick race (a race torn down without
            // Abort, e.g. by a test) would make this a two-player race with two opponents [ours]
            if (GameSession.Mode == GameMode.Career && GameSession.SplitScreen) { Debug.Log("NFSMW race: stale split-screen flag cleared for a career race"); GameSession.SplitScreen = false; }
            // Drive: the race activity's script runs this race (single player career races) [specs/lua_hooks.md §4.2]
            bool lanRace = GameSession.Lan != null && GameSession.Lan.Started;
            bool drives = Lua.LuaFlow.Drives;
            Driven = drives && !lanRace && !GameSession.SplitScreen;
            Debug.Log($"NFSMW race: {ev.Name}: Driven {Driven} (lua {Lua.LuaFlow.Mode}, game mode {GameSession.Mode}, runtime {(drives ? "up" : Lua.LuaFlow.Runtime != null ? "up but not driving" : "none")}, LAN {lanRace}, split screen {GameSession.SplitScreen})");
            // Game.InitRacers (0x61DC2B) and the prerace StateEnter's Game.ResetCopsForRestart (0x60AB10):
            // ICopMgr::ResetCopsForRestart(true) on every race start and restart (gameplay_core.md §10.1)
            if (Systems.Police != null && ev.Type != RaceType.Pursuit) Systems.Police.ResetCopsForRestart(true);
            // route: start -> checkpoints for every lap -> finish (the RaceRoute of DetermineRaceLength, through our road graph)
            var wps = new List<Vec3> { ev.Start.Position };
            _laps = ev.Looping ? Mathf.Max(1, ev.Laps) : 1;
            var cps = ev.Checkpoints.Count > 0 ? ev.Checkpoints : ev.SpeedTraps;
            for (int lap = 0; lap < _laps; lap++)
            {
                foreach (var c in cps) wps.Add(c.Position);
                if (ev.Finish != null) wps.Add(ev.Finish.Position);
            }
            _path = Roads.Through(wps);
            if (_path.Count < 2) _path = new List<Vector3> { Coords.ToUnity(ev.Start.Position), Coords.ToUnity(ev.Finish?.Position ?? ev.Start.Position) + Vector3.forward };
            _cum = new float[_path.Count];
            for (int i = 1; i < _path.Count; i++) _cum[i] = _cum[i - 1] + Vector3.Distance(_path[i - 1], _path[i]);
            _lapLength = _cum[_cum.Length - 1] / _laps;
            _pathTool = gameObject.AddComponent<AIDriver>();
            _pathTool.enabled = false;
            _pathTool.SetPath(_path);
            // the AI's road flags: barriers of this race, then the race route (InRace + direction) through the waypoints
            RoadGraph.ClearRace();
            var groups = new HashSet<uint>(ev.Barriers.Select(SceneryGroupTable.Hash));
            int barred = RoadGraph.MarkBarriers(groups);
            int shortcutSegs = RoadGraph.MarkShortcuts(ev.Shortcuts.Select(sc => Coords.ToUnity(sc.point.Position)).ToList());
            int inRace = RoadGraph.MarkRace(wps.Select(w => Coords.ToUnity(w)).ToList());
            BuildGoals(ev);
            Debug.Log($"NFSMW race AI: {inRace} route segments, {barred} barred, {ev.Shortcuts.Count} shortcuts ({shortcutSegs} segments), {_goals.Count} goals");

            // grid (Game.InitRacers): the player at racestart, the AI 7 m apart behind it, ±1.5 m alternating
            float a = ev.Start.Rotation * Mathf.Deg2Rad;
            var fwd = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var right = Vector3.Cross(Vector3.up, fwd);
            var start = Coords.ToUnity(ev.Start.Position);
            boot.PlacePlayer(ev.Start.Position, ev.Start.Rotation);
            _player = new Entrant { Name = GameSession.SplitScreen ? "Player 1" : GameSession.Profile?.Name ?? "You", Car = boot.Player, Human = true };
            Entrants.Add(_player);
            int maxOpponents = 7;
            var lan = GameSession.Lan != null && GameSession.Lan.Started ? GameSession.Lan : null;
            if (lan != null)
            {
                // LAN: one grid slot per player in id order, the others' cars follow their states
                maxOpponents = 0;
                var ids = lan.Peers.Select(x => x.Id).OrderBy(x => x).ToList();
                Vector3 Slot(int k) => start - fwd * (7f * k) + right * (k == 0 ? 0 : (k & 1) == 1 ? 1.5f : -1.5f);
                boot.Player.Teleport(Slot(ids.IndexOf(lan.LocalId)) + Vector3.up * 0.4f, Quaternion.LookRotation(fwd));
                _player.Name = lan.LocalName;
                foreach (var peer in lan.Peers)
                {
                    if (peer.Id == lan.LocalId) continue;
                    var pc = CarSpawner.Spawn(string.IsNullOrEmpty(peer.Car) ? "bmwm3gtr" : peer.Car, Color.HSVToRGB(peer.Id * 0.21f % 1f, 0.7f, 0.85f), new OwnedCar(), Slot(ids.IndexOf(peer.Id)), Quaternion.LookRotation(fwd), peer.Name);
                    pc.UseDeviceInput = false;
                    pc.gameObject.AddComponent<NetCar>().Peer = peer;
                    Entrants.Add(new Entrant { Name = peer.Name, Car = pc, Remote = true, Peer = peer });
                }
                lan.SetReady();
            }
            if (GameSession.SplitScreen)
            {
                var p2 = boot.BeginSplitScreen(GameSession.QuickCar2);
                p2.Teleport(start + right * 3.2f + Vector3.up * 0.4f, Quaternion.LookRotation(fwd));
                Entrants.Add(new Entrant { Name = "Player 2", Car = p2, Human = true });
                maxOpponents = 2;
            }

            // opponents (InitRacers: none in a tollbooth); generic characters take RandomOpponent cars by performance
            var career = GameSession.Career;
            var racers = ev.Type == RaceType.Tollbooth ? new List<Racer>() : ev.Opponents.Take(maxOpponents).Select(k => career.Racers.TryGetValue(k, out var r) ? r : null).ToList();
            if (ev.BossRace && racers.Count == 0 && ev.Type != RaceType.Tollbooth && career.Bins.TryGetValue(ev.BinIndex, out var bin) && bin.Boss != null) racers.Add(bin.Boss);
            float minPerf = GameSession.Mode == GameMode.Career ? racers.Where(r => r != null).Select(r => r.MinimumAIPerformance).DefaultIfEmpty(0f).Max() : 0f;
            var candidates = OpponentCars.Candidates(minPerf, GameSession.ActiveCar);
            int nextCandidate = 0;
            var used = new HashSet<string> { GameSession.ActiveCar.Car };
            var names = new HashSet<string>(Entrants.Select(e => e.Name));
            int slot = GameSession.SplitScreen ? 2 : 1;
            float side = 1.5f;
            foreach (var racer in racers)
            {
                string car = career.CarFor(racer);
                if (string.IsNullOrEmpty(car) || !System.IO.Directory.Exists(System.IO.Path.Combine(GameData.Root, "CARS", car.ToUpperInvariant())))
                    car = candidates.Count > 0 ? candidates[nextCandidate++ % candidates.Count] : PickCar(ev.BinIndex, used);
                used.Add(car);
                Vector3 pos;
                Quaternion rot;
                if (racer?.ForceStart != null)
                {
                    pos = Coords.ToUnity(racer.ForceStart.Position);
                    float fa = racer.ForceStart.Rotation * Mathf.Deg2Rad;
                    rot = Quaternion.LookRotation(new Vector3(Mathf.Cos(fa), 0, Mathf.Sin(fa)));
                }
                else
                {
                    pos = start - fwd * (7f * slot) + right * side;
                    rot = Quaternion.LookRotation(fwd);
                    side = -side;
                }
                string name = OpponentCars.NameFor(ev, racer, names);
                names.Add(name);
                Entrants.Add(SpawnOpponent(car, name, pos, rot, racer));
                slot++;
            }
            RaceTime = ev.StartTime;
            VisualLighting.SetTimeOfDay(ev.TimeOfDay);   // races with a TOD >= 0 set the clock (0x5FD210 / 0x600AB0)
            _lastSecondTick = Mathf.FloorToInt(RaceTime);
            foreach (var e in Entrants) InitRacer(e);
            Countdown = true;
            _countdown = 4f;
            PlayerCar.ResetTime();
            // every car stages on the line (held, clutch open: the player can rev); drag cars get the drag engine
            foreach (var e in Entrants)
                if (e.Car && !e.Remote)
                {
                    e.Car.Dragster = ev.Type == RaceType.Drag;
                    e.Car.Staging = !ev.RollingStart;
                    if (e.Human && ev.Type == RaceType.Drag && !e.Car.GetComponent<DragSteering>()) e.Car.gameObject.AddComponent<DragSteering>().Car = e.Car;
                }
            if (ev.RollingStart) RollingStart(ev);
            if (ev.CheckpointsVisible) BuildMarkers();
            boot.RefreshBarriers();
            if (ev.Type == RaceType.SpeedTrap)
            {
                foreach (var t in ev.SpeedTraps) _traps.Add((Coords.ToUnity(t.Position), t.Radius > 0 ? t.Radius : 20f, t.Threshold));
                foreach (var e in Entrants)
                {
                    e.TrapSpeeds.Clear();
                    e.TrapSpeeds.AddRange(Enumerable.Repeat(0f, _traps.Count));
                    e.InTrap = new bool[_traps.Count];
                }
            }
            TimeLeft = ev.TimeLimit > 0 ? ev.TimeLimit : -1;
            // the wrong-way plane behind the grid (resetplayertrigger, ResetsPlayer) — only when it faces against the race
            if (ev.Triggers.TryGetValue("wrongway", out var ww) && (ww.ResetsPlayer || ww.Width > 0))
            {
                var trig = RaceTrigger.From(ww, Vector3.zero, 150f);
                if (Vector3.Dot(trig.Normal, fwd) < -0.7f) _wrongWayReset = trig;
            }
            // DamageRacer/DamageVehicle::ResetDamage on every racer's car at a race start and restart (a car object reused
            // from a race that blew or wrecked it would stay braked)
            foreach (var e in Entrants) if (e.Car && !e.Remote) e.Car.ResetDamage();
            _introStart = start;
            _introFwd = fwd;
            if (Driven) DriveSetup(ev);   // the intro NIS and the countdown wait for the script (NIS.Play, EShowRaceCountdown)
            else if (NisPlayer.Enabled && lan == null && !ev.RollingStart)
            {
                Intro = true;
                NisPlayer.RaceIntro(this, ev, start, fwd, IntroCaption(ev), () => Intro = false);
            }
            Debug.Log($"NFSMW race: {ev.DisplayName} {ev.TypeName}, laps {_laps}, route {_path.Count} points {RouteLength / 1000f:0.00} km, {Entrants.Count - 1} opponents, adaptive {AdaptiveDifficulty.Current:0.00}");
        }

        /// <summary>
        /// The goal sequence of the `prerace` state (GoalList = Checkpoint, then racefinish; NextGoal wraps to 1 on circuits)
        /// over every lap, with each goal's place on the route (DetermineRaceLength's segments) and its trigger.
        /// </summary>
        void BuildGoals(RaceEvent ev)
        {
            _goals.Clear();
            var cps = ev.Checkpoints;
            var finish = ev.Finish ?? (cps.Count > 0 ? cps[cps.Count - 1] : null);
            var perLap = new List<(RacePoint p, bool finish)>();
            foreach (var c in cps) if (c != finish) perLap.Add((c, false));
            if (finish != null) perLap.Add((finish, true));
            _goalsPerLap = Mathf.Max(1, perLap.Count);
            int wi = 0;
            float ahead = _laps > 1 ? _lapLength * 0.95f : float.MaxValue;
            for (int lap = 0; lap < _laps; lap++)
                foreach (var (p, fin) in perLap)
                {
                    var u = Coords.ToUnity(p.Position);
                    float prog = RouteSearch(u, ref wi, ahead);
                    var dir = _pathTool.PointAt(prog + 10f) - _pathTool.PointAt(prog - 10f);
                    _goals.Add(new Goal { Trigger = RaceTrigger.From(p, dir, fin ? 30f : 40f), Progress = prog, Finish = fin, Lap = lap, TimeBonus = p.TimeBonus });
                }
        }

        /// <summary>
        /// Where a point falls on the route: the nearest route segment from `index` up to `ahead` metres further on (the
        /// same place recurs a lap later on circuits, so the search stops short of a lap). Updates `index`.
        /// </summary>
        float RouteSearch(Vector3 p, ref int index, float ahead)
        {
            if (_path.Count < 2) return 0;
            int from = Mathf.Clamp(index, 0, _path.Count - 2);
            float best = float.MaxValue, prog = _cum[from];
            int bestI = from;
            for (int i = from; i < _path.Count - 1 && _cum[i] - _cum[from] <= ahead; i++)
            {
                var a = _path[i];
                var ab = _path[i + 1] - a;
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(0.01f, ab.sqrMagnitude));
                float d = (a + ab * t - p).sqrMagnitude;
                if (d < best) { best = d; prog = _cum[i] + ab.magnitude * t; bestI = i; }
            }
            index = bestI;
            return prog;
        }

        /// <summary>GRacerInfo after AddRacer/StartRace: lap 1, no checkpoint hit, the first goal.</summary>
        void InitRacer(Entrant e)
        {
            e.LapsCompleted = 0;
            e.CheckpointsHit = 0;
            e.GoalIndex = 0;
            e.Points = 0;
            e.PctRaceComplete = 0;
            e.TopSpeed = e.DistanceDriven = 0;
            e.HasLastPos = false;
        }

        /// <summary>
        /// RollingStart events (the prologue's 16.1.0): the cars start moving at InitialPlayerSpeed at StartPercent of the
        /// route, with no countdown.
        /// </summary>
        void RollingStart(RaceEvent ev)
        {
            Countdown = false;
            Intro = true;     // held until the city has streamed in at the rolling-start point
            StartCoroutine(RollingRelease(ev));
        }

        /// <summary>
        /// The exe places a rolling start on the race route's road (WRoadNav along the GRaceParameters route); the port's
        /// route is a coarse polyline whose chords can cut through a lot (16.1.0 started on the lot next to the road, facing
        /// its fence). Snap the point onto the nearest race-road segment, centre lane, heading the race's way [ours].
        /// </summary>
        static void SnapToRaceRoad(ref Vector3 p, ref Vector3 dir)
        {
            var nav = new RoadNav { RaceFilter = true };
            if (!nav.InitAtPoint(p, dir, true, 1f)) return;
            p = new Vector3(nav.Position.x, p.y, nav.Position.z);
            dir = RoadGraph.FlatDir(nav.Forward);
        }

        /// <summary>
        /// [ours: the tests' and QA tour's autopilot] The race route sampled along the road network every <paramref name="step"/>
        /// metres from <paramref name="from"/> (WRoadNav with the race filter, so junctions follow the race's segments). The
        /// route itself (Route) is a coarse polyline whose chords cut corners and lots; an AIDriver on it drives off the road.
        /// Empty when the point is off the road network.
        /// </summary>
        public List<Vector3> RoadRoute(Vector3 from, float step = 8f, float maxLength = 8000f)
        {
            var pts = new List<Vector3>();
            var nav = new RoadNav { RaceFilter = true };
            if (!nav.InitAtPoint(from, Vector3.zero, true, 0f)) return pts;
            for (float d = 0; d < maxLength && nav.Valid; d += step)
            {
                pts.Add(nav.Centre);
                if (nav.DeadEnd) break;
                nav.IncNavPosition(step, 0f);
            }
            return pts;
        }

        System.Collections.IEnumerator RollingRelease(RaceEvent ev)
        {
            float along = Mathf.Clamp01(ev.StartPercent) * _cum[_cum.Length - 1];
            float speed = Mathf.Max(10f, ev.InitialSpeed) * 0.44704f;
            var slots = new List<(Entrant e, Vector3 pos, Vector3 dir)>();
            for (int i = 0; i < Entrants.Count; i++)
            {
                var e = Entrants[i];
                if (!e.Car) continue;
                float at = along - i * 9f;
                var p = _pathTool.PointAt(at);
                var dir = _pathTool.PointAt(at + 25f) - _pathTool.PointAt(at - 5f);
                dir.y = 0;
                dir = dir.normalized;
                SnapToRaceRoad(ref p, ref dir);
                var side = Vector3.Cross(Vector3.up, dir) * (i == 0 ? 0f : (i & 1) == 1 ? 3f : -3f);
                slots.Add((e, p + side, dir));
                var body = e.Car.GetComponent<Rigidbody>();
                if (body) body.isKinematic = true;
                e.Car.transform.SetPositionAndRotation(p + side + Vector3.up * 0.5f, Quaternion.LookRotation(dir));
                if (body) { body.position = e.Car.transform.position; body.rotation = e.Car.transform.rotation; }
            }
            // wait for ground under the player (the streamer follows the player's transform)
            float t = 0;
            var pl = slots.Count > 0 ? slots[0].pos : Vector3.zero;
            while (t < 30f && !Ground(pl, out _))
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            yield return new WaitForFixedUpdate();
            // the route can cut through a lot at that point: move up the route to the first spot with clear road ahead
            bool placed = false;
            for (float shift = 0; shift <= 400f && !placed; shift += 20f)
            {
                var p = _pathTool.PointAt(along + shift);
                var d = _pathTool.PointAt(along + shift + 25f) - _pathTool.PointAt(along + shift - 5f);
                d.y = 0;
                d = d.normalized;
                SnapToRaceRoad(ref p, ref d);
                if (!Ground(p, out var gy)) continue;
                var eye = new Vector3(p.x, gy + 1f, p.z);
                bool blocked = false;
                foreach (var h in Physics.RaycastAll(eye, d, 30f, ~0, QueryTriggerInteraction.Ignore)) if (h.rigidbody == null) { blocked = true; break; }
                if (blocked) continue;
                if (shift > 0)
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var q = _pathTool.PointAt(along + shift - i * 9f);
                        var qd = d;
                        SnapToRaceRoad(ref q, ref qd);
                        var side = Vector3.Cross(Vector3.up, qd) * (i == 0 ? 0f : (i & 1) == 1 ? 3f : -3f);
                        slots[i] = (slots[i].e, q + side, qd);
                    }
                placed = true;
            }
            if (!placed)
            {
                // no clear road there: roll off the event's own start grid instead
                for (int i = 0; i < slots.Count; i++)
                {
                    var q = _pathTool.PointAt(Mathf.Max(0, 30f - i * 9f));
                    var d = _pathTool.PointAt(60f) - _pathTool.PointAt(0f);
                    d.y = 0;
                    d = d.normalized;
                    slots[i] = (slots[i].e, q + Vector3.Cross(Vector3.up, d) * (i == 0 ? 0f : (i & 1) == 1 ? 3f : -3f), d);
                    slots[i].e.Car.transform.position = slots[i].pos + Vector3.up;
                }
                t = 0;
                while (t < 30f && !Ground(slots[0].pos, out _)) { t += Time.unscaledDeltaTime; yield return null; }
                yield return new WaitForFixedUpdate();
            }
            // Drive: the cars roll when the script's `racing` state starts the race timers (countdown: ForceAIControl,
            // NotifyCountdownDone → racing → Game.StartRaceTimers) [ours: held where they are until then]
            float held = 0;
            while (Driven && !_timersStarted && held < 60f) { held += Time.unscaledDeltaTime; yield return null; }
            if (Driven && !_timersStarted) { Debug.Log("NFSMW race: Drive: no Game.StartRaceTimers 60 s into the rolling start; released"); _timersStarted = true; }
            foreach (var (e, pos, dir) in slots)
            {
                if (!e.Car) continue;
                float y = Ground(pos, out var gy) ? gy : pos.y;
                var body = e.Car.GetComponent<Rigidbody>();
                if (body) body.isKinematic = false;
                e.Car.Teleport(new Vector3(pos.x, y + 0.6f, pos.z), Quaternion.LookRotation(dir));
                if (body) body.linearVelocity = dir * speed;
                if (e.AI) e.AI.enabled = true;
                // a partial race: the goals behind the rolling start point count as passed [?]
                int idx = 0;
                float prog = RouteSearch(e.Car.transform.position, ref idx, _laps > 1 ? _lapLength * 0.95f : float.MaxValue);
                e.PathIndex = idx;
                while (e.GoalIndex < _goals.Count - 1 && _goals[e.GoalIndex].Progress < prog + 10f && !_goals[e.GoalIndex].Finish) { e.GoalIndex++; e.CheckpointsHit++; }
                e.HasLastPos = false;
            }
            Intro = false;
        }

        /// <summary>The road surface at a route point: of the static surfaces under it, the one nearest the route's height
        /// (not an overpass above it).</summary>
        static bool Ground(Vector3 at, out float y)
        {
            y = at.y;
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(at + Vector3.up * 8f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.rigidbody != null) continue;
                float d = Mathf.Abs(h.point.y - at.y);
                if (d < best && d < 4f) { best = d; y = h.point.y; }
            }
            return best < float.MaxValue;
        }

        /// <summary>Fallback when no RandomOpponent car fits: a car lot car of this tier or above.</summary>
        static string PickCar(int bin, HashSet<string> used)
        {
            var lot = GameSession.Career.CarLot.Where(c => c.UnlockedAt >= bin && !used.Contains(c.Car)).ToList();
            if (lot.Count == 0) lot = GameSession.Career.CarLot.Where(c => !used.Contains(c.Car)).ToList();
            return lot.Count > 0 ? lot[Random.Range(0, Mathf.Min(lot.Count, 6))].Car : "gti";
        }

        /// <summary>
        /// An AI racer (AIVehicleRacecar). Base skill (ComputeSkill): career = the character's SkillLevel / 100 moved by the
        /// adaptive difficulty (AdaptiveDifficulty.ApplyToSkill); quick race = {0.15, 0.4, 1.0} by difficulty. Opponent
        /// cars carry upgrades in proportion to the character's B1ECE070 (races.md 5.1).
        /// </summary>
        Entrant SpawnOpponent(string car, string name, Vector3 pos, Quaternion rot, Racer racer)
        {
            var color = Color.HSVToRGB(Random.value, 0.7f, 0.8f);
            float minPerf = racer != null && GameSession.Mode == GameMode.Career ? racer.MinimumAIPerformance : 0f;
            // GRacerInfo::CreateVehicle (0x5E8E60, 0x5E8F9E-0x5E8FD1): every opponent is created with Performance {1, 1, 1}, which
            // PVehicle::Construct turns into parts by Upgrades::MatchPeformance (0x67C310: all packages at the top level)
            if (!NfsMw.Dynamics.PhysicsInfo.MatchPerformance(car, 1f, 1f, 1f, out var perf))
            {
                int lvl = Mathf.RoundToInt(minPerf * 3f);   // [ours] a car without performance ratings
                perf = new OwnedCar { Car = car, Engine = lvl, Tires = lvl, Transmission = lvl, Suspension = lvl, Nitrous = lvl, Induction = lvl, Brakes = lvl };
            }
            var pc = CarSpawner.Spawn(car, color, perf, pos, rot, name);
            var ai = pc.gameObject.AddComponent<AIRacer>();
            ai.enabled = false;
            ai.Car = pc;
            ai.Race = this;
            ai.Drag = Event.Type == RaceType.Drag;
            int diff = Event.Difficulty < 34 ? 0 : Event.Difficulty <= 66 ? 1 : 2;
            bool career = GameSession.Mode == GameMode.Career && racer != null && racer.SkillLevel >= 0;
            ai.BaseSkill = career ? AdaptiveDifficulty.ApplyToSkill(racer.SkillLevel * 0.01f) : new[] { 0.15f, 0.4f, 1f }[diff];
            ai.MinPerformance = minPerf;
            var e = new Entrant { Name = name, Car = pc, AI = ai, Racer = racer };
            ai.Me = e;
            return e;
        }

        void BuildMarkers()
        {
            var shader = Shader.Find("NFSMW/WorldTransparent");
            var seen = new HashSet<RacePoint>();
            foreach (var g in _goals)
            {
                var c = g.Trigger.Point;
                if (!seen.Add(c)) continue;
                var m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(m.GetComponent<Collider>());
                m.name = "Checkpoint";
                m.transform.SetParent(transform, false);
                m.transform.position = g.Trigger.Centre + Vector3.up * 12f;
                m.transform.localScale = new Vector3(Mathf.Max(8f, c.Radius * 0.6f), 12f, Mathf.Max(8f, c.Radius * 0.6f));
                var mat = new Material(shader);
                mat.SetColor("_Color", g.Finish ? new Color(1f, 0.85f, 0.2f, 0.18f) : new Color(0.2f, 0.9f, 1f, 0.16f));
                mat.SetFloat("_VertexScale", 1f);
                m.GetComponent<Renderer>().sharedMaterial = mat;
                _markers.Add(m);
            }
        }

        void Update()
        {
            if (_done) return;
            if (Driven && DriveHold()) return;   // waiting for the script (intro NIS, countdown, race timers)
            if (Intro)
            {
                StatusText = "";
                return;
            }
            var lan = GameSession.Lan;
            if (lan != null)
            {
                lan.Poll();
                if (Time.time >= _nextState && _player.Car)
                {
                    _nextState = Time.time + 0.05f;
                    var body = _player.Car.GetComponent<Rigidbody>();
                    lan.SendState(_player.Car.transform.position, _player.Car.transform.rotation, body ? body.linearVelocity : Vector3.zero, _player.Progress);
                }
                foreach (var e in Entrants)
                    if (e.Remote && e.Peer != null && e.Peer.FinishTime >= 0 && !e.Finished) { e.Finished = true; e.FinishTime = e.Peer.FinishTime; e.PctRaceComplete = 100f; }
                if (Countdown && !lan.Go)
                {
                    StatusText = "Waiting for players";
                    foreach (var e in Entrants) if (e.Car && !e.Remote) e.Car.Input = new DriveInput { Brake = 1, Handbrake = true };
                    return;
                }
            }
            if (Countdown)
            {
                _countdown -= Time.deltaTime;
                // staging holds the cars; the AI waits on its brakes, the players keep their throttle to rev
                foreach (var e in Entrants) if (e.Car && !e.Human && !e.Remote) e.Car.Input = new DriveInput { Brake = 1, Handbrake = true };
                StatusText = _countdown > 1f ? ((int)_countdown).ToString() : "GO!";
                if (_countdown <= 1f && Driven)
                {
                    // Drive: GO posts CountdownDone; the script's `racing` StateEnter starts the timers (StartRaceTimers)
                    Countdown = false;
                    _goAt = Time.time;
                    return;
                }
                if (_countdown <= 1f)
                {
                    Countdown = false;
                    foreach (var e in Entrants) if (e.AI) e.AI.enabled = true;
                    foreach (var e in Entrants)
                        if (e.Car && e.Car.Launch() && e.Human && !GameSession.SplitScreen) Systems.HudMessage?.Invoke("Perfect Launch!");
                }
                return;
            }
            RaceTime += Time.deltaTime;
            Script?.Invoke(this);
            if (_done) return;
            // Drive: GRacerInfo::BlowEngine / TotalVehicle only; LuaGameHost posts MNotifyEngineBlown / MNotifyVehicleDestroyed
            // and the script decides (raceover, or the D-Day script's own timers)
            if (Driven && _player.Car && !_player.Finished && (_player.Car.EngineBlown || _player.Car.Totaled) && !_player.EngineBlown && !_player.Totalled)
            {
                if (_player.Car.Totaled) _player.Totalled = true; else _player.EngineBlown = true;
                _player.StopTime = RaceTime;
                RaceRules.CalculateRankings(Entrants, ByPoints);
                AdaptiveDifficulty.Update(this, _player.Totalled ? AdaptiveDifficulty.Reason.VehicleDestroyed : AdaptiveDifficulty.Reason.EngineBlown, _player);
            }
            // drag: a blown engine or a totaled car loses the race (EEngineBlown / DamageDragster)
            if (!Driven && _player.Car && !GameSession.SplitScreen && (_player.Car.EngineBlown || _player.Car.Totaled))
            {
                if (_player.Car.Totaled) _player.Totalled = true; else _player.EngineBlown = true;
                _player.StopTime = RaceTime;
                AdaptiveDifficulty.Update(this, _player.Totalled ? AdaptiveDifficulty.Reason.VehicleDestroyed : AdaptiveDifficulty.Reason.EngineBlown, _player);
                End(false);
                return;
            }
            UpdateClock();
            if (_done) return;
            foreach (var e in Entrants) UpdateRacer(e);
            if (_done) return;
            if (SpeedTrapRace && !Driven) SpeedTraps();   // Drive: speedtraprace's TriggerEnter → AwardCash
            if (Driven) DriveUpdate();
            RaceRules.CalculateRankings(Entrants, ByPoints);
            foreach (var e in Entrants) UpdateSplits(e);

            bool laps = _laps > 1;
            int lap = Mathf.Clamp(LapOf(_player), 1, _laps);
            StatusText = $"{Event.TypeName}   POS {_player.Place}/{Entrants.Count(e => !e.Out)}" + (laps ? $"   LAP {lap}/{_laps}" : "") + $"   {RaceTime / 60:0}:{RaceTime % 60:00.00}";
            UpdateMarkers();
            if (_player.Car)
            {
                var a = _pathTool.PointAt(_player.RouteProgress);
                var b = _pathTool.PointAt(_player.RouteProgress + 15f);
                var dir = b - a; dir.y = 0;
                var v = _player.Car.GetComponent<Rigidbody>().linearVelocity; v.y = 0;
                WrongWay = v.magnitude > 5f && Vector3.Dot(v.normalized, dir.normalized) < -0.5f;
            }
            if (GameSession.SplitScreen)
            {
                // both players finished, or the second one is 25 s behind the first
                var humans = Entrants.Where(e => e.Human).ToList();
                var done = humans.Where(e => e.Finished).ToList();
                if (done.Count == humans.Count || (done.Count > 0 && RaceTime - done.Min(e => e.FinishTime) > 25f)) EndSplit(humans);
            }
            else if (lan != null && _player.Finished) End(_player.Place == 1);
        }

        /// <summary>
        /// The master clock: NotifyRaceTimeSecTick once per whole second (the speedtrap overtime penalty), and the time
        /// limit (GetRaceTimeRemaining 0x5FE090; when it runs out the humans still racing are stopped → NotifyRaceTimeExpired).
        /// </summary>
        void UpdateClock()
        {
            int sec = Mathf.FloorToInt(RaceTime);
            while (_lastSecondTick < sec)
            {
                _lastSecondTick++;
                // speedtraprace racing NotifyRaceTimeSecTick: after the first finisher every racer still in loses OvertimePenaltyPerSec
                if (!Driven && SpeedTrapRace && _suddenDeath && Event.OvertimePenalty > 0)
                    foreach (var e in Entrants)
                        if (e.InRace) e.Points = Mathf.Max(0, e.Points - Event.OvertimePenalty);
            }
            if (Event.TimeLimit <= 0) return;
            TimeLeft = Mathf.Max(0, Event.TimeLimit + _bonusTime - RaceTime);
            if (TimeLeft <= 0 && !_timeExpiredSent)
            {
                _timeExpiredSent = true;
                foreach (var e in Entrants)
                    if (e.Human && !e.Finished && e.Car) e.Car.Input = new DriveInput { Brake = 1 };
                // Drive: MNotifyRaceTimeExpired (LuaGameHost) → the script's raceover
                if (!_player.Finished && !GameSession.SplitScreen && !Driven) End(false);
            }
        }

        /// <summary>
        /// GRacerInfo::Update (0x5E92E0) and the `racing` TriggerEnter handler for one racer: route position held at the
        /// next goal, the goal's trigger entered (or passed along the route by more than 60 m, a port guard for triggers
        /// our road route misses [?]), completion percentages.
        /// </summary>
        void UpdateRacer(Entrant e)
        {
            if (!e.Car) return;
            var pos = e.Car.transform.position;
            int idx = e.PathIndex;
            e.RouteProgress = _pathTool.ProgressOf(pos, ref idx);
            e.PathIndex = idx;
            if (!e.Finished && !e.Out && !e.Totalled && !e.EngineBlown && !Countdown)
            {
                // GRacerInfo::Update (0x5E92E0): top speed and distance driven while racing
                float v = e.Car.SpeedMps;
                if (v > e.TopSpeed) e.TopSpeed = v;
                if (e.HasLastPos) { float step = Vector3.Distance(e.LastPos, pos); if (step < 60f) e.DistanceDriven += step; }   // a warp is not driving
            }
            if (e.Remote)
            {
                e.Progress = e.RouteProgress;
                if (!e.Finished) e.PctRaceComplete = Mathf.Clamp01(e.Progress / Mathf.Max(1f, RouteLength)) * 100f;
                return;
            }
            if (Driven) DriveGoalGuard(e, pos);   // the script owns the goals; only the port guard for missed triggers
            else if (e.InRace && _goals.Count > 0)
            {
                var g = _goals[Mathf.Clamp(e.GoalIndex, 0, _goals.Count - 1)];
                bool jumped = e.HasLastPos && (pos - e.LastPos).sqrMagnitude > 60f * 60f;
                if (e.HasLastPos && !jumped && g.Trigger.Entered(e.LastPos, pos)) HitGoal(e);
                else if (e.RouteProgress > g.Progress + 60f) HitGoal(e);
                if (_done) return;
                if (e == _player && e.HasLastPos && !jumped && WrongWayReset(e.LastPos, pos)) return;
            }
            e.LastPos = pos;
            e.HasLastPos = true;
            if (e.Finished || e.Out) return;
            float cap = _goals.Count > 0 ? _goals[Mathf.Clamp(e.GoalIndex, 0, _goals.Count - 1)].Progress : RouteLength;
            e.Progress = Mathf.Clamp(e.RouteProgress, 0, cap);
            e.PctRaceComplete = Mathf.Clamp01(e.Progress / Mathf.Max(1f, RouteLength)) * 100f;
            float lapStart = _lapLength * e.LapsCompleted;
            e.PctLapComplete = Mathf.Clamp01((e.Progress - lapStart) / Mathf.Max(1f, _lapLength)) * 100f;
        }

        /// <summary>
        /// The racer drove through its goal (`racing` TriggerEnter): a checkpoint → NotifyCheckpointReached (and a TimeBonus
        /// for the player → AwardBonusTime), next goal; the finish → NotifyLapFinished, and on the last lap
        /// NotifyRaceFinished + NotifyRacePlacement.
        /// </summary>
        void HitGoal(Entrant e)
        {
            var g = _goals[Mathf.Clamp(e.GoalIndex, 0, _goals.Count - 1)];
            if (!g.Finish)
            {
                e.CheckpointsHit++;       // StartCheckpoint(NextGoal): hit = NextGoal
                if (g.TimeBonus > 0 && e.Human && Event.TimeLimit > 0) AwardBonusTime(g.TimeBonus);
                if (e.GoalIndex < _goals.Count - 1) e.GoalIndex++;
                return;
            }
            int lap = e.LapsCompleted + 1;
            NotifyLapFinished(e, lap);
            if (lap >= _laps) { NotifyRaceFinished(e); return; }
            if (_done) return;
            if (e.GoalIndex < _goals.Count - 1) e.GoalIndex++;
        }

        /// <summary>
        /// AwardBonusTime (0x5FE600): every racer records the time left at this booth (the TOLLBOOTHS page), then the
        /// clock gains the checkpoint's TimeBonus (HUD.ShowTimeExtension follows the clock).
        /// </summary>
        void AwardBonusTime(float seconds)
        {
            float left = Event.TimeLimit > 0 ? Mathf.Max(0, Event.TimeLimit + _bonusTime - RaceTime) : 0;
            foreach (var e in Entrants) e.BoothTimes.Add(left);
            if (Event.Type == RaceType.Tollbooth && !_player.Finished) { _player.Laps.Add(RaceTime); _player.LapPlaces.Add(_player.Place); }
            _bonusTime += seconds;
            TimeLeft = Mathf.Max(0, Event.TimeLimit + _bonusTime - RaceTime);
        }

        /// <summary>
        /// Game_NotifyLapFinished (0x611850): lap time, StartLap, CalculateRankings, and in a lap knockout every racer that
        /// can no longer stay in (RaceRules.LapKnockouts) is knocked out.
        /// </summary>
        void NotifyLapFinished(Entrant e, int lap)
        {
            e.LapsCompleted = lap;
            e.CheckpointsHit = 0;
            if (_laps > 1) { e.Laps.Add(RaceTime); e.LapPlaces.Add(e.Place); }
            RaceRules.CalculateRankings(Entrants, ByPoints);
            if (Event.Type != RaceType.Knockout) return;
            foreach (var x in RaceRules.LapKnockouts(Entrants.Where(r => !r.Remote).ToList(), lap, Mathf.Max(1, Event.KnockoutsPerLap)))
                KnockOut(x);
        }

        /// <summary>Game_KnockoutRacer (0x611440): GRacerInfo::KnockOut, MNotifyKnockedOut; the AI leaves the race.</summary>
        void KnockOut(Entrant x)
        {
            if (x.Finished || x.Out) return;
            x.Out = true;
            x.StopTime = RaceTime;
            RaceRules.CalculateRankings(Entrants, ByPoints);
            AdaptiveDifficulty.Update(this, AdaptiveDifficulty.Reason.RacerKnockedOut, x);
            if (x == _player && !GameSession.SplitScreen) { if (!Driven) End(false); return; }   // Drive: MNotifyKnockedOut → raceover
            if (x.Human) { if (x.Car) x.Car.Input = new DriveInput { Brake = 1, Handbrake = true }; }
            else if (x.Car) Destroy(x.Car.gameObject);
        }

        /// <summary>
        /// Game_NotifyRaceFinished (0x6119F0): FinishRace, 100 %, rankings; the AI's target is cleared; then
        /// NotifyRacePlacement — the first placement starts a speedtrap race's sudden death, and the player's placement ends
        /// the race (1st → won, else the race-over flow).
        /// </summary>
        void NotifyRaceFinished(Entrant e)
        {
            if (e.Finished) return;
            e.Finished = true;
            e.FinishTime = RaceTime;
            e.PctRaceComplete = 100f;
            e.Progress = RouteLength;
            _numberFinished++;
            RaceRules.CalculateRankings(Entrants, ByPoints);
            if (e.AI) { e.AI.enabled = false; e.Car.Input = new DriveInput { Brake = 1, Handbrake = true }; }
            if (e == _player) GameSession.Lan?.SendFinish(RaceTime);
            AdaptiveDifficulty.Update(this, AdaptiveDifficulty.Reason.RacerFinished, e);
            if (SpeedTrapRace) _suddenDeath = true;
            if (Driven) { DriveRaceFinished(e); return; }   // the win bookkeeping; the script runs the race-over flow
            if (e != _player || GameSession.SplitScreen || GameSession.Lan != null) return;
            // Game.CalculateRanking (0x6058B0): a speedtrap race settles everyone first (FinalizeRaceStats)
            if (SpeedTrapRace) { FinalizeRaceStats(); RaceRules.CalculateRankings(Entrants, true); }
            End(_player.Place == 1);
        }

        /// <summary>UpdateSplits (0x5E3400): the race time and position at 20/40/60/80 % (the STAGES page of sprints and drags).</summary>
        void UpdateSplits(Entrant e)
        {
            for (int k = 0; k < 4; k++)
            {
                if (e.SplitTimes[k] > 0 || e.PctRaceComplete < RaceRules.SplitPercent[k]) continue;
                e.SplitTimes[k] = Mathf.Max(0.001f, RaceTime);
                e.SplitRanks[k] = e.Place;
                if (_laps <= 1 && Event.Type != RaceType.Tollbooth) { e.Laps.Add(e.SplitTimes[k]); e.LapPlaces.Add(e.Place); }
            }
        }

        /// <summary>
        /// speedtraprace TriggerEnter (gameplay_core.md §2.6): entering a camera at ThreshholdSpeed (km/h) or more and faster
        /// than this racer's record there adds the difference to its points (AwardCash → AddToPointTotal).
        /// </summary>
        void SpeedTraps()
        {
            foreach (var e in Entrants)
            {
                if (!e.Car || e.Remote || !e.InRace || e.InTrap.Length != _traps.Count) continue;
                var p = e.Car.transform.position;
                for (int t = 0; t < _traps.Count; t++)
                {
                    var d = p - _traps[t].pos;
                    bool inside = new Vector2(d.x, d.z).magnitude < _traps[t].radius && Mathf.Abs(d.y) < 20f;
                    if (inside && !e.InTrap[t])
                    {
                        float kmh = e.Car.SpeedMps * 3.6f;
                        if (kmh >= _traps[t].threshold && kmh > e.TrapSpeeds[t])
                        {
                            e.Points = Mathf.Max(0, e.Points + kmh - e.TrapSpeeds[t]);
                            e.TrapSpeeds[t] = kmh;
                            if (e == _player && !GameSession.SplitScreen)
                                Systems.HudMessage?.Invoke($"{Settings.Speed(e.Car.SpeedMps)} {Settings.SpeedUnit}");
                        }
                    }
                    e.InTrap[t] = inside;
                }
            }
        }

        /// <summary>
        /// The `wrongway` resetplayertrigger (ResetsPlayer): the player driving back through the plane behind the grid is
        /// warped to racestart (`racing` TriggerEnter → Game.WarpPlayerToTrigger).
        /// </summary>
        bool WrongWayReset(Vector3 from, Vector3 to)
        {
            if (_wrongWayReset == null || !_player.Car || RaceTime < Event.StartTime + 1f || !_wrongWayReset.Entered(from, to, true)) return false;
            float a = Event.Start.Rotation * Mathf.Deg2Rad;
            _player.Car.Teleport(Coords.ToUnity(Event.Start.Position) + Vector3.up * 0.4f, Quaternion.LookRotation(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a))));
            _player.HasLastPos = false;
            _player.PathIndex = 0;
            return true;
        }

        void UpdateMarkers()
        {
            // SetNextCheckpointPos: the player's next goal, plus the gates near the car
            var p = _player.Car ? _player.Car.transform.position : Vector3.zero;
            var next = _goals.Count > 0 && _player.InRace ? _goals[Mathf.Clamp(_player.GoalIndex, 0, _goals.Count - 1)].Trigger.Centre : (Vector3?)null;
            foreach (var m in _markers)
            {
                var c = m.transform.position - Vector3.up * 12f;
                bool isNext = next.HasValue && (c - next.Value).sqrMagnitude < 1f;
                m.SetActive(isNext || (c - p).sqrMagnitude < 250f * 250f);
            }
        }

        /// <summary>
        /// FinalizeRaceStats (0x6021B0 / FUN_006007B0): racers still out get a projected time t / (pct/100); a drag racer
        /// that is wrecked or under 0.4 % is DNF; a speedtrap AI loses (projected − t)·OvertimePenaltyPerSec points; then
        /// the adaptive difficulty settles (FinalizeRace).
        /// </summary>
        public void FinalizeRaceStats()
        {
            foreach (var e in Entrants)
            {
                if (e.Finished || e.Remote) continue;
                float t = RaceTime, projected = t;
                if (e.PctRaceComplete > 0.1f) projected = t / (e.PctRaceComplete * 0.01f);
                if (Event.Type == RaceType.Drag && (e.Totalled || e.EngineBlown || e.PctRaceComplete < 0.4f)) { projected = 0; e.DNF = true; }
                if (SpeedTrapRace && !e.Human && projected - t > 0) e.Points = Mathf.Max(0, e.Points - (projected - t) * Event.OvertimePenalty);
                if (!e.Human)
                    for (int k = 0; k < 4; k++)
                        if (e.SplitTimes[k] <= 0 && !e.DNF) e.SplitTimes[k] = projected * RaceRules.SplitPercent[k] * 0.01f;
                if (e.DNF || e.Out || e.Human) continue;
                e.FinishTime = projected;
                e.Finished = true;
            }
            RaceRules.CalculateRankings(Entrants, ByPoints);
            AdaptiveDifficulty.Update(this, AdaptiveDifficulty.Reason.FinalizeRace, null);
        }

        void End(bool won)
        {
            _done = true;
            Countdown = false;
            string result = won ? "You won!" : $"You finished {Ordinal(_player.Place)}";
            if (GameSession.Lan != null)
            {
                var lines = Entrants.OrderBy(e => e.Finished ? e.FinishTime : 1e9f - e.Progress)
                    .Select(e => $"{e.Name}: {(e.Finished ? $"{e.FinishTime / 60:0}:{e.FinishTime % 60:00.00}" : "racing")}");
                var lanFe = FrontEnd.Create();
                lanFe.Push(new MessageScreen(result, string.Join("   ", lines)));
                lanFe.Closed += Cleanup;
                return;
            }
            Systems.HudMessage?.Invoke(RaceOverText(won));
            StartCoroutine(AfterRaceOver(won));
        }

        static readonly string[] Places = { "Winner!", "You finished second!", "You finished third!", "You finished fourth!", "You finished fifth!", "You finished sixth!", "You finished seventh!" };

        /// <summary>Hud_RaceOverMessage text (table 0x8F3C1C).</summary>
        string RaceOverText(bool won)
        {
            if (_player.Car && _player.Car.Totaled) return "Totaled!^You Lost!";
            if (_player.Car && _player.Car.PowerScale <= 0) return "Blown engine!";
            if (_player.Out) return "You were Knocked Out!^You Lost!";
            if (Event.TimeLimit > 0 && TimeLeft <= 0 && !_player.Finished) return "Time's Up!";
            int n = Entrants.Count(e => !e.Out);
            string place = _player.Place > 1 && _player.Place == n ? "You were last!" : Places[Mathf.Clamp(_player.Place - 1, 0, Places.Length - 1)];
            return _player.Finished ? $"{place}^Your time: {UI.GameHud.TimeString(RaceTime - Event.StartTime)}" : place;
        }

        /// <summary>
        /// `raceover`: the summary stays up for RaceOverMessageDuration (3 s; 1 s for a destroyed car) before the results
        /// (FE_ShowPostRaceScreen(false)); a win goes to `postrace` (the photo-finish screen, here a short hold first).
        /// </summary>
        System.Collections.IEnumerator AfterRaceOver(bool won)
        {
            float hold = won ? 2.5f : _player.Totalled || (_player.Car && _player.Car.Totaled) ? 1f : 3f;
            if (_player.Car) _player.Car.Input = new DriveInput { Brake = 1 };
            float t = 0;
            while (t < hold) { t += Time.unscaledDeltaTime; if (_player.Car) _player.Car.Input = new DriveInput { Brake = 1 }; yield return null; }
            ShowResults(won);
        }

        void ShowResults(bool won)
        {
            var ev = Event;
            FinalizeRaceStats();
            if (!Driven && OnEnd != null && OnEnd(this, won)) return;
            // Drive: Game_NotifyRaceFinished paid the win already (DriveRaceFinished)
            if (won && GameSession.Mode == GameMode.Career && !Driven) GameSession.AwardWin(ev, this);
            else if (won && GameSession.Mode == GameMode.QuickRace) GameSession.QuickRaceWon(this);
            if (won && GameSession.Mode == GameMode.ChallengeSeries) GameSession.CompleteChallenge(ev);
            var fe = FrontEnd.Create();
            fe.Push(new PostRaceResultsScreen(this, won));
            fe.Closed += Cleanup;
            if (GameSession.Mode == GameMode.Career)
            {
                bool driven = Driven;
                void After()
                {
                    fe.Closed -= After;
                    if (GameBootstrap.Instance) GameBootstrap.Instance.StartCoroutine(AfterResults(ev, won, driven));
                    else Story.AfterRace();
                }
                fe.Closed += After;
            }
        }

        /// <summary>
        /// Leaving the results in career (`postrace` → `done`, or the losing screen → `abandoned`): unless a restart is
        /// loading, a won race runs its PostRaceActivity (the next race of the boss chain) and a lost one is abandoned
        /// (GRaceStatus::RaceAbandoned resets an unfinished boss chain); then the career checks (Story.AfterRace).
        /// </summary>
        static System.Collections.IEnumerator AfterResults(RaceEvent ev, bool won, bool driven = false)
        {
            yield return null;
            bool restarting = Active != null || (FrontEnd.Instance != null && FrontEnd.Instance.Top is LoadingScreen);
            if (restarting) yield break;
            // Drive: the script runs the PostRaceActivity (`done`) and the abandon (`abandoned` → Game.AbandonRace); what is
            // left here is the front end's rival flow after a boss race
            if (driven) { Story.AfterRace(); yield break; }
            if (won && ev.PostRaceActivity != 0 && GameSession.Mode == GameMode.Career)
            {
                var next = GameSession.Career.EventFor(ev.PostRaceActivity, ev.BinIndex);
                if (next != null && next.Start != null && Story.PendingRivalBeaten == 0) { GameFlow.StartRace(next); yield break; }
            }
            if (!won) GameSession.AbandonRace(ev);
            Story.AfterRace();
        }

        void EndSplit(List<Entrant> humans)
        {
            _done = true;
            Countdown = false;
            var order = humans.OrderBy(e => e.Finished ? e.FinishTime : 1e9f - e.Progress).ToList();
            string lines = string.Join("   ", order.Select(e => $"{e.Name}: {Ordinal(e.Place)}{(e.Finished ? $" {e.FinishTime / 60:0}:{e.FinishTime % 60:00.00}" : " DNF")}"));
            var fe = FrontEnd.Create();
            fe.Push(new MessageScreen($"{order[0].Name} wins!", lines));
            fe.Closed += Cleanup;
        }

        void Cleanup() => Cleanup(true);

        public void Cleanup(bool unhook)
        {
            if (unhook && FrontEnd.Instance) FrontEnd.Instance.Closed -= Cleanup;
            Abort(false);
        }

        /// <summary>
        /// Ends the race now. A career race still running counts as abandoned (Game.AbandonRace → RaceAbandoned: an
        /// unfinished boss chain starts again); Begin and the results flow call Abort(false).
        /// </summary>
        public void Abort() => Abort(true);

        void Abort(bool abandon)
        {
            if (abandon && !_done && Event != null && GameSession.Mode == GameMode.Career) GameSession.AbandonRace(Event);
            _done = true;
            DriveCleanup();
            if (Intro && NisPlayer.Active) Destroy(NisPlayer.Active.gameObject);
            foreach (var e in Entrants)
                if (!e.Human && e.Car) Destroy(e.Car.gameObject);
                else if (e.Human && e.Car) e.Car.Staging = false;   // [ours] a race left before GO must not keep the car on the line
            if (GameBootstrap.Instance) GameBootstrap.Instance.EndSplitScreen();
            GameSession.SplitScreen = false;
            if (Active == this)
            {
                Active = null;
                if (GameBootstrap.Instance) GameBootstrap.Instance.RefreshBarriers();
                // GRaceStatus::SetRoaming (0x5FE506): ICopMgr::LockoutCops(true) once the play mode is roaming again
                if (Systems.Police != null && Event != null && Event.Type != RaceType.Pursuit) Systems.Police.LockoutCops(true);
            }
            Destroy(gameObject);
        }

        static string Ordinal(int n) => n + (n == 1 ? "st" : n == 2 ? "nd" : n == 3 ? "rd" : "th");
    }
}
