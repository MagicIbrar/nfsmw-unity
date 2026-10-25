using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;
using NfsMw.Game.Gameplay;
using NfsMw.Game.Lua;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Drive mode (specs/lua_hooks.md §4.2): the race activity's `basicrace` script runs the race through the GRaceStatus
    /// bindings (ILuaRaceControl). The race keeps its scene side — grid, cars, AI, route progress, rankings, splits,
    /// knockouts of Game_NotifyLapFinished, the clock, the results screens — and leaves the decisions to the script:
    ///   * the intro NIS starts at the script's NIS.Play, the countdown at EShowRaceCountdown, the race (timers, AI,
    ///     launch) at Game.StartRaceTimers;
    ///   * the goals are the script's (Game.SetRacerGoal); a goal counts when the script hears TriggerEnter from
    ///     LuaTriggers; checkpoints, laps and the finish arrive as Game.NotifyCheckpointReached / NotifyLapFinished /
    ///     NotifyRaceFinished;
    ///   * the race ends by the script: ShowRaceOverSummary, EnterPostRaceFlow (photo finish + results, or straight on for
    ///     NoPostRaceScreen), ShowLosingPostRaceScreen, AbandonRace, SetRoaming; the next race of a chain is the script's
    ///     PostRaceActivity.
    /// Every wait on the script has a fallback that logs "NFSMW race: Drive: …" so a script that never answers cannot
    /// hold the race forever [ours].
    /// </summary>
    public sealed partial class RaceManager : ILuaRaceControl
    {
        /// <summary>The script runs this race (Drive mode, single player career).</summary>
        public bool Driven { get; private set; }
        /// <summary>The intro NIS the script asked for is playing (LuaGameHost's NISComplete waits for it).</summary>
        public bool NisPlaying { get; private set; }
        /// <summary>The race was ended by the script (results closed, NoPostRaceScreen, SetRoaming, the next activity).</summary>
        public bool ScriptEnded { get; private set; }
        /// <summary>The race is over (results up or the race left).</summary>
        public bool Over => _done;

        sealed class ScriptGoal
        {
            public GInstance G;
            public RaceTrigger Trigger;
            public float Progress = -1;   // where it falls on our route (-1: not on it, no guard)
            public bool Guarded;
        }

        readonly Dictionary<Entrant, ScriptGoal> _scriptGoals = new Dictionary<Entrant, ScriptGoal>();
        readonly Dictionary<Entrant, (Vector3 pos, Quaternion rot, Vector3 vel)> _startPositions = new Dictionary<Entrant, (Vector3, Quaternion, Vector3)>();
        readonly Dictionary<int, (AIDriver ai, bool device)> _forcedAI = new Dictionary<int, (AIDriver, bool)>();
        Vector3 _introStart, _introFwd;
        bool _introHeld, _countdownHeld, _timersStarted, _hasBeenWon, _summaryShown, _brakePlayer;
        float _holdTime, _goAt = -1;

        static string IntroCaption(RaceEvent ev)
        {
            string caption = ev.DisplayName.ToUpperInvariant();
            var career = GameSession.Career;
            if (ev.BossRace && career != null && career.Bins.TryGetValue(ev.BinIndex, out var boss) && boss.Boss != null)
                caption = $"BLACKLIST #{ev.BinIndex}   {boss.Boss.Name.ToUpperInvariant()}";
            return caption;
        }

        /// <summary>Setup in Drive: hold for the script's intro NIS and countdown (a rolling start holds in RollingRelease).</summary>
        void DriveSetup(RaceEvent ev)
        {
            _scriptGoals.Clear();
            if (ev.RollingStart) return;
            Intro = true;
            _introHeld = true;
            Countdown = true;
            _countdownHeld = true;
            _countdown = 4f;
            _holdTime = 0;
            Debug.Log($"NFSMW race: Drive: {ev.Name} waits for its script (NIS.Play, EShowRaceCountdown, StartRaceTimers)");
        }

        /// <summary>A race with no script (no stategraph for its event): the C# rules run it after all [ours].</summary>
        public void ReleaseToCSharp()
        {
            if (!Driven || _timersStarted) return;
            Debug.Log($"NFSMW race: Drive: {Event?.Name} has no stategraph; the C# race rules run it");
            Driven = false;
            if (ReferenceEquals(LuaFlow.Race, this)) LuaFlow.Race = null;
            if (_introHeld) { _introHeld = false; Intro = false; }
            _countdownHeld = false;
        }

        /// <summary>Update while the race waits for the script; true = nothing else runs this frame.</summary>
        bool DriveHold()
        {
            if (Event.RollingStart) return false;
            bool phaseGo = !Countdown && !_timersStarted && !_introHeld && !NisPlaying;
            if (!_introHeld && !NisPlaying && !_countdownHeld && !phaseGo) return false;
            StatusText = phaseGo ? "GO!" : "";
            foreach (var e in Entrants) if (e.Car && !e.Human && !e.Remote) e.Car.Input = new DriveInput { Brake = 1, Handbrake = true };
            // the fallbacks count only while nothing the script waits for is on screen
            bool media = StoryCard.Showing || MoviePlayer.Playing || NisPlaying || NisPlayer.Active || LuaFlow.FrontEndOpen;
            if (!media) _holdTime += Time.deltaTime;
            if (_introHeld && _holdTime > 20f)
            {
                Debug.Log($"NFSMW race: Drive: no NIS.Play from the script after 20 s; intro skipped");
                _introHeld = false;
                Intro = false;
                _holdTime = 0;
            }
            else if (!_introHeld && !NisPlaying && _countdownHeld && _holdTime > 20f)
            {
                Debug.Log($"NFSMW race: Drive: no countdown from the script after 20 s; counting down");
                StartCountdown();
            }
            else if (phaseGo && _goAt > 0 && Time.time - _goAt > 5f)
            {
                Debug.Log($"NFSMW race: Drive: no Game.StartRaceTimers 5 s after GO; started");
                StartRaceTimers();
                return false;
            }
            return true;
        }

        /// <summary>Per frame in Drive: the player stopped after his finish (as AfterRaceOver does).</summary>
        void DriveUpdate()
        {
            if (_brakePlayer && _player.Car) _player.Car.Input = new DriveInput { Brake = 1 };
        }

        /// <summary>
        /// The port guard of the C# rules (a goal passed along our route by more than 60 m counts): Drive tells the script
        /// with the TriggerEnter LuaTriggers would have posted for that goal [ours: our route and the trigger boxes can
        /// miss each other].
        /// </summary>
        void DriveGoalGuard(Entrant e, Vector3 pos)
        {
            if (!e.InRace || !_timersStarted || !e.Car || !_scriptGoals.TryGetValue(e, out var g) || g.Guarded || g.Progress < 0) return;
            if (e.RouteProgress <= g.Progress + 60f) return;
            g.Guarded = true;
            Debug.Log($"NFSMW race: Drive: {e.Name} passed {g.G.LocalName} along the route without entering it; TriggerEnter posted");
            LuaFlow.Post(StategraphRuntime.TriggerEnter, ("Sender", g.G), ("Element", e.Car));
        }

        void DriveRaceFinished(Entrant e)
        {
            // Game_NotifyRaceFinished (0x6119F0): the local human with rank 1, once (mHasBeenWon): cash, unlocks, score
            if (e == _player)
            {
                _brakePlayer = true;
                if (e.Place == 1 && !_hasBeenWon && GameSession.Mode == GameMode.Career)
                {
                    _hasBeenWon = true;
                    GameSession.AwardWin(Event, this);
                }
            }
        }

        void DriveCleanup()
        {
            if (ReferenceEquals(LuaFlow.Race, this)) LuaFlow.Race = null;
            foreach (var kv in _forcedAI.ToList()) SetAIControl(kv.Key, false);
            if (Driven) ScriptedCharacters.Clear();
        }

        Entrant RacerAt(int racer) => racer >= 1 && racer <= Entrants.Count ? Entrants[racer - 1] : null;

        // ------------------------------------------------------------------------------------------------------------
        // ILuaRaceControl

        RaceEvent ILuaRaceControl.Event => Event;

        public void InitRacers(RacePoint start)
        {
            // Game.InitRacers (0x61DBE0): the grid was built when the race was loaded (Setup); racers are not re-made
        }

        public void SetRacerGoal(int racer, GInstance goal, RacePoint at)
        {
            var e = RacerAt(racer);
            if (e == null) return;
            if (goal == null || at == null) { _scriptGoals.Remove(e); return; }
            var g = new ScriptGoal { G = goal };
            // where the goal falls on our route: the first goal of the lap order at or after the racer's current one
            var u = Coords.ToUnity(at.Position);
            int found = -1;
            for (int k = 0; k < _goals.Count && found < 0; k++)
            {
                int i = (Mathf.Max(0, e.GoalIndex) + k) % _goals.Count;
                if (k > 0 && i < e.GoalIndex && _laps <= 1) break;
                var c = _goals[i].Trigger.Centre;
                if (new Vector2(c.x - u.x, c.z - u.z).sqrMagnitude < 36f) found = i;
            }
            if (found >= 0)
            {
                e.GoalIndex = found;
                g.Trigger = _goals[found].Trigger;
                g.Progress = _goals[found].Progress;
            }
            else g.Trigger = RaceTrigger.From(at, Vector3.zero, 40f);
            _scriptGoals[e] = g;
        }

        public void CheckpointReached(int racer, int checkpoint)
        {
            // Game.NotifyCheckpointReached (0x6117F0): SetCheckpointTime, StartCheckpoint(n) (0x600660: hit = n if n == hit + 1)
            var e = RacerAt(racer);
            if (e != null && checkpoint == e.CheckpointsHit + 1) e.CheckpointsHit = checkpoint;
        }

        public void LapFinished(int racer, int lap)
        {
            var e = RacerAt(racer);
            if (e != null && !e.Finished && !e.Out) NotifyLapFinished(e, lap);
        }

        public void RaceFinished(int racer)
        {
            var e = RacerAt(racer);
            if (e != null) NotifyRaceFinished(e);
        }

        public void Knockout(int racer)
        {
            var e = RacerAt(racer);
            if (e != null) KnockOut(e);
        }

        public void AwardPoints(int racer, float amount)
        {
            // Game.AwardCash / AwardPoints (0x60E030): GRacerInfo::AddToPointTotal
            var e = RacerAt(racer);
            if (e != null) e.Points = Mathf.Max(0, e.Points + amount);
        }

        void ILuaRaceControl.AwardBonusTime(float seconds) => AwardBonusTime(seconds);

        public void SpeedTrapTriggered(int racer, RacePoint trap, float kmh)
        {
            // Game.NotifySpeedTrapTriggered in a speedtrap race: mSpeedTrapSpeed of that camera (the SPEEDTRAPS page)
            var e = RacerAt(racer);
            if (e == null || trap == null || _traps.Count == 0) return;
            var p = Coords.ToUnity(trap.Position);
            int best = -1;
            float bd = float.MaxValue;
            for (int t = 0; t < _traps.Count; t++) { float d = (_traps[t].pos - p).sqrMagnitude; if (d < bd) { bd = d; best = t; } }
            if (best < 0 || best >= e.TrapSpeeds.Count) return;
            if (kmh > e.TrapSpeeds[best]) e.TrapSpeeds[best] = kmh;
            if (e == _player && e.Car) Systems.HudMessage?.Invoke($"{Settings.Speed(kmh / 3.6f)} {Settings.SpeedUnit}");
        }

        public void StartRace()
        {
            // Game.StartRace (0x60DBD0): traffic, barriers, DetermineRaceLength, MakeCatchUpData — done when the race loaded
        }

        public void StartCountdown()
        {
            // EShowRaceCountdown: the 3-2-1 (an intro never asked for is skipped: the restart path enters prerace)
            if (_timersStarted || Event.RollingStart) return;
            if (_introHeld) { _introHeld = false; Intro = false; }
            _countdownHeld = false;
            Countdown = true;
            _countdown = 4f;
            _holdTime = 0;
        }

        public void StartRaceTimers()
        {
            // Game.StartRaceTimers (0x604EF0) at `racing` StateEnter: the race is on
            if (_timersStarted) return;
            _timersStarted = true;
            _introHeld = _countdownHeld = false;
            if (Event.RollingStart) return;   // RollingRelease lets the cars go
            Intro = false;
            Countdown = false;
            foreach (var e in Entrants) if (e.AI) e.AI.enabled = true;
            foreach (var e in Entrants)
                if (e.Car && e.Car.Launch() && e.Human && !GameSession.SplitScreen) Systems.HudMessage?.Invoke("Perfect Launch!");
        }

        public void SetAllStaging(bool staging)
        {
            // Game.SetAllStaging (0x60DF70): every car held on the line (released by the launch at the race timers)
            if (Event.RollingStart || _timersStarted && staging) return;
            foreach (var e in Entrants) if (e.Car && !e.Remote && (staging || _timersStarted)) e.Car.Staging = staging;
        }

        public void PlayIntroNis()
        {
            // NIS.Play(racestart, IntroNIS / QuickRaceNIS, 'Intro', …) while the race waits for it
            if (!_introHeld || Event.RollingStart) return;
            _introHeld = false;
            _holdTime = 0;
            if (!NisPlayer.Enabled) { Intro = false; return; }
            NisPlaying = true;
            Intro = true;
            NisPlayer.RaceIntro(this, Event, _introStart, _introFwd, IntroCaption(Event), () => { NisPlaying = false; Intro = false; });
        }

        public void EnterPostRaceFlow()
        {
            // Game.EnterPostRaceFlow (0x611F20): FinalizeRaceStats, Game_AbandonRace (timers stop, the AI stops); a race with
            // NoPostRaceScreen posts MFlowReadyForOutro at once and is completed in career (score flags |= 2); otherwise
            // FE_ShowPostRaceScreen(true): the photo finish, then the results (their closing posts FlowReadyForOutro)
            if (_done) return;
            FinalizeRaceStats();
            foreach (var e in Entrants) if (e.AI) { e.AI.enabled = false; if (e.Car) e.Car.Input = new DriveInput { Brake = 1, Handbrake = true }; }
            if (Event.NoPostRaceScreen)
            {
                var s = RaceDatabase.ScoreInfo(Event);
                if (s != null) s.Flags |= RaceIndex.CompletedCareer;
                Story.OnDDayRaceOver(Event);
                Debug.Log($"NFSMW race: Drive: {Event.Name} has no post-race screen");
                ScriptEnded = true;
                _done = true;
                Cleanup(false);
                return;
            }
            ShowPostRaceScreen(true);
        }

        public void ShowPostRaceScreen(bool won)
        {
            // FE_ShowPostRaceScreen (0x60AC20): FinalizeRaceStats, PostRace_Results.fng (the winning path first pushes the
            // photo finish); the screen's closing cleans the race up (Cleanup) and LuaGameHost posts the script's answer
            if (_done) return;
            _done = true;
            Countdown = false;
            ScriptEnded = true;
            ShowResults(won);
        }

        public void ShowRaceOverSummary()
        {
            // Game.ShowRaceOverSummary (0x6120C0): EShowRaceOverMessage (Hud_RaceOverMessage, table 0x8F3C1C)
            if (_summaryShown) return;
            _summaryShown = true;
            _brakePlayer = true;
            Systems.HudMessage?.Invoke(RaceOverText(_player.Finished && _player.Place == 1));
        }

        public void AbandonRace()
        {
            // Game.AbandonRace (0x60DEB0) from `abandoned`: the race stops; GRaceStatus::RaceAbandoned resets a boss chain
            ScriptEnded = true;
            if (Active == this) Abort();
        }

        public void SetRoaming()
        {
            // RaceStatus:SetRoaming (0x5FE3E0): back to roaming (the race, if still here, is left)
            ScriptEnded = true;
            if (Active == this) Cleanup(false);
        }

        public void EndForNextActivity()
        {
            // a script's Activity.Run of another race while this one's activity is done (a NIS shell, a chain) [ours]
            Debug.Log($"NFSMW race: Drive: {Event?.Name} ends (its activity is done; the next race starts)");
            ScriptEnded = true;
            if (Active == this) Cleanup(false);
        }

        public void WarpPlayerTo(RacePoint at)
        {
            // Game.WarpPlayerToTrigger (0x611570): racestart (restart) or the wrong-way reset
            if (at == null || !_player?.Car) return;
            float a = at.Rotation * Mathf.Deg2Rad;
            _player.Car.Teleport(Coords.ToUnity(at.Position) + Vector3.up * 0.4f, Quaternion.LookRotation(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a))));
            _player.HasLastPos = false;
            _player.PathIndex = 0;
        }

        public void SaveStartPositions()
        {
            // Game.SaveStartPositions (0x604E90) at intronis exit: the grid as the NIS left it
            _startPositions.Clear();
            foreach (var e in Entrants)
                if (e.Car && !e.Remote)
                {
                    var body = e.Car.GetComponent<Rigidbody>();
                    _startPositions[e] = (e.Car.transform.position, e.Car.transform.rotation, body ? body.linearVelocity : Vector3.zero);
                }
        }

        public void RestoreStartPositions()
        {
            // Game.RestoreStartPositions (0x60AA50): every car back to its saved place [ours: only a car that moved > 5 m]
            foreach (var kv in _startPositions)
            {
                var e = kv.Key;
                if (!e.Car || (e.Car.transform.position - kv.Value.pos).sqrMagnitude < 25f) continue;
                e.Car.Teleport(kv.Value.pos, kv.Value.rot);
                var body = e.Car.GetComponent<Rigidbody>();
                if (body && !body.isKinematic) body.linearVelocity = kv.Value.vel;
                e.HasLastPos = false;
            }
        }

        public void SetAIControl(int player, bool on)
        {
            // Game.ForceAIControl / ClearAIControl (0x6123B0 / 0x612420): the AI drives local player n (0 or 1) along the
            // race route [ours: an AIDriver on the route; a car that already has an enabled driver is left alone]
            var humans = Entrants.Where(x => x.Human && !x.Remote).ToList();
            var e = player >= 0 && player < humans.Count ? humans[player] : null;
            if (on)
            {
                if (e?.Car == null || _forcedAI.ContainsKey(player)) return;
                var existing = e.Car.GetComponent<AIDriver>();
                if (existing && existing.enabled) return;
                var ai = e.Car.gameObject.AddComponent<AIDriver>();
                ai.Car = e.Car;
                var route = _path.Skip(Mathf.Clamp(e.PathIndex, 0, Mathf.Max(0, _path.Count - 2))).ToList();
                ai.SetPath(route.Count >= 2 ? route : _path);
                _forcedAI[player] = (ai, e.Car.UseDeviceInput);
                e.Car.UseDeviceInput = false;
            }
            else if (_forcedAI.TryGetValue(player, out var f))
            {
                _forcedAI.Remove(player);
                if (f.ai) { if (f.ai.Car) f.ai.Car.UseDeviceInput = f.device; Destroy(f.ai); }
            }
        }
    }
}
