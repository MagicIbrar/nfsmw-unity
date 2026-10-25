using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;
using NfsMw.Game.Gameplay;
using UnityEngine;

namespace NfsMw.Game.Lua
{
    /// <summary>
    /// The game side of the Lua bindings (LuaGameHooks / BindToGameCode 0x61E750, specs/lua_hooks.md §3).
    ///   Query bindings are answered from the live C# game in every mode (RaceManager, GameSession, Profile, Systems).
    ///   Action bindings are recorded in Shadow mode (LuaFlow.Calls, the ledger) and executed in Drive mode (LuaDrive).
    ///   The messages the C++ half of a binding posts back into the scripts (MNotifyKnockedOut from KnockoutRacer,
    ///   MNotifyPlayerRep from AwardPlayerBounty, LoadingComplete after NotifyScriptWhenLoaded, CountdownDone after the
    ///   countdown, NISComplete, NotifyMovieFinished, FlowReadyForOutro, …) are posted in both modes, from what the C#
    ///   game does (the countdown really ending, the movie really closing), so the script flow follows the real one.
    /// The simables are the cars (PlayerCar); racer indices are 1-based (Game_GetRacerIndex 0x60DAE0).
    /// </summary>
    public sealed class LuaGameHost : ILuaGameHost
    {
        public StategraphRuntime Rt;
        public LuaLedger Ledger;
        public LuaTriggers Triggers;
        readonly HashSet<string> _missing = new HashSet<string>();

        // C++ completions the scripts wait for
        bool _waitLoading, _waitCountdown, _waitNis, _waitMovie, _waitMessage, _waitPostRace, _waitLosing;
        float _waitSince;
        RaceManager _losingRace;
        readonly List<(float at, Action a)> _later = new List<(float, Action)>();

        static RaceManager Race => RaceManager.Active;
        float Now => Rt != null ? Rt.SimTime : 0;

        // ---------------------------------------------------------------------------------------------------------
        // simables

        /// <summary>Racer i (1-based) of the C# race, -1 when the car is not racing.</summary>
        public static int RacerIndex(object simable)
        {
            var rm = Race;
            if (rm == null || !(simable is PlayerCar car)) return -1;
            for (int i = 0; i < rm.Entrants.Count; i++) if (rm.Entrants[i].Car == car) return i + 1;
            return -1;
        }

        static RaceManager.Entrant Entrant(int i)
        {
            var rm = Race;
            return rm != null && i >= 1 && i <= rm.Entrants.Count ? rm.Entrants[i - 1] : null;
        }

        static RaceManager.Entrant EntrantOf(object simable) => Entrant(RacerIndex(simable));

        static PlayerCar PlayerCar(int n)
        {
            var boot = GameBootstrap.Instance;
            if (!boot) return null;
            return n <= 1 ? boot.Player : n == 2 ? boot.Player2 : null;   // GetPlayerElement(0) = the first player (0x612370)
        }

        static RaceEvent Event(object o) => o is GInstance g && GameSession.Career != null ? GameSession.Career.EventFor(g.Key) : null;

        // ---------------------------------------------------------------------------------------------------------
        // bindings

        public object Call(LuaBinding b, object[] args)
        {
            string name = b.Table + "." + b.Name;
            switch (name)
            {
                // ---- queries ------------------------------------------------------------------------------------
                case "Game.GetNumRacers": return Race?.Entrants.Count ?? 0;
                case "Game.GetRacerIndex": return RacerIndex(args[0]);
                case "Game.GetRacerElement": return Entrant((int)args[0])?.Car;
                case "Game.GetPlayerElement": return PlayerCar((int)args[0]);
                case "Game.GetRacerCharacter":
                    {
                        var e = Entrant((int)args[0]);
                        return e?.Racer != null ? Rt.Instance(e.Racer.Key) : null;
                    }
                case "Game.RacerIsHuman": return Entrant((int)args[0])?.Human ?? false;
                case "Game.PlayerIsLocal": { var e = EntrantOf(args[0]); return e != null ? e.Human && !e.Remote : args[0] is PlayerCar pc && pc == PlayerCar(1); }
                case "ISimable.IsPlayer": return args[0] is PlayerCar ip && (ip == PlayerCar(1) || ip == PlayerCar(2) || (EntrantOf(ip)?.Human ?? false));
                case "Game.GetSimableSpeedKmh": return args[0] is PlayerCar sc && sc ? sc.SpeedMps * 3.6f : 0f;
                case "Game.SimableDistance":
                    {
                        // 0x60AB90: distance from the simable to the instance's position
                        var p = LuaTriggers.PointOf((args[1] as GInstance)?.Record);
                        return args[0] is PlayerCar dc && dc && p != null ? Vector3.Distance(dc.transform.position, Coords.ToUnity(p.Position)) : 0f;
                    }
                case "Game.SimableAngle":
                    {
                        // 0x6053B0: angle between the simable's heading and the direction to the instance [?: degrees]
                        // 0x6053B0: dot(the simable's forward vector, GRuntimeInstance::GetDirection) — a cosine, not an
                        // angle (the epic pursuit's win trigger wants ≥ 0.8)
                        var p = LuaTriggers.PointOf((args[1] as GInstance)?.Record);
                        if (!(args[0] is PlayerCar ac) || !ac || p == null) return 0f;
                        float ra = p.Rotation * Mathf.Deg2Rad;
                        return Vector3.Dot(ac.transform.forward, new Vector3(Mathf.Cos(ra), 0, Mathf.Sin(ra)));
                    }
                case "Game.CalculateRanking":
                    {
                        // 0x6058B0: a speedtrap race settles everyone first (FinalizeRaceStats + CalculateRankings); Drive only,
                        // since it changes the race
                        var cr = EntrantOf(args[0]);
                        if (cr != null && Race != null && Race.Driven && Race.SpeedTrapRace) Race.FinalizeRaceStats();
                        return cr?.Place ?? (int)args[1];
                    }
                case "Game.AllRacersDone": return Race == null || Race.Entrants.All(e => e.Finished || e.Out);
                case "Game.AllHumanPlayersDone": return Race == null || Race.Entrants.Where(e => e.Human).All(e => e.Finished || e.Out);
                case "Game.IsRaceCompleted": { var ev = Event(args[0]); return ev != null && RaceDatabase.IsCareerRaceComplete(ev); }
                case "Game.IsRaceUnlocked": { var ev = Event(args[0]); return ev != null && RaceDatabase.IsCareerRaceUnlocked(ev); }
                case "Game.IsCareerMode": return GameSession.Mode == GameMode.Career;
                case "Game.IsSplitScreen": return GameSession.SplitScreen;
                case "Game.IsOnlineGame": return false;
                case "Game.IsLANGame": return GameSession.Lan != null && GameSession.Lan.Started;
                case "Game.GetPlayerBounty": return GameSession.Profile?.Bounty ?? 0;
                case "Bin.GetNumRacesWon": return GameSession.Career != null && GameSession.Career.Bins.TryGetValue((int)args[0], out var bw) ? GameSession.RacesWonInBin(bw) : 0;
                case "Bin.GetNumChallengesPassed": return GameSession.Career != null && GameSession.Career.Bins.TryGetValue((int)args[0], out var bc) ? GameSession.ChallengesDoneInBin(bc) : 0;
                case "Game.AllowEngageEvents":
                case "Game.AllowMenuGates":
                    // UpdateTriggerAvailability (0x5EBA50): not racing and not in a pursuit
                    return Race == null && !(Systems.Police && Systems.Police.InPursuit);
                case "Game.AllowEngageSafehouse":
                    return Race == null && !(Systems.Police && Systems.Police.InPursuit && Systems.Police.Cooldown <= 0);
                case "Game.IsActiveMenuGate":
                    {
                        // 0x604E40: the zone trigger's GIcon (+0xC0) exists and GManager::GetIsIconVisible (0x5DDB30) —
                        // UpdateIconVisibility (0x5FDAE0): safehouse icons (type 8) follow AllowEngageSafehouse, car lot and
                        // customshop icons (9, 10) AllowMenuGates; every icon is hidden during an NIS [?: a zone without an icon]
                        bool safehouse = Rt.CurrentActivity?.Instance.Record.Text("ZoneType") == "safehouse";
                        bool allowed = safehouse ? Race == null && !(Systems.Police && Systems.Police.InPursuit && Systems.Police.Cooldown <= 0)
                                                 : Race == null && !(Systems.Police && Systems.Police.InPursuit);
                        return allowed && NisPlayer.Active == null;
                    }
                case "Game.IsActiveSpeedTrap":
                    {
                        string n = (args[0] as GInstance)?.Name;
                        return n != null && GameSession.ActiveSpeedTraps().Any(t => t.Name == n);
                    }
                // 0x612490: SkipFE, the SkipCareerIntro switch (0x926125, the skip-D-Day cheat) or the career flag (+0xAC bit 0x20: past the drive to the safehouse)
                case "Game.SkipCareerIntro": return Controls.EasterEggs.SkipCareerIntro || GameSession.Profile != null && GameSession.Profile.Stage != Story.ToSafehouse && GameSession.Profile.Stage != Story.Prologue;
                case "Audio.IsCopSpeechPlaying": return CopRadio.Instance != null && CopRadio.Instance.Playing;   // 0x604A00: Speech::Manager::IsPlaying(1)
                case "Platform.IsNextGen": return false;
                // the world never needs a reload in the port (the C# flow sets the time of day and special setups itself)
                case "Game.SetCurrentTimeOfDay": Record(b, args); return false;
                case "Game.DoSpecialSetup": Record(b, args); return false;
            }

            Record(b, args);
            Compare(b, args);
            EmulateCpp(b, args);
            bool driving = Driving;
            if (name == "Activity.Run" && !driving && args[0] is GInstance rg)
            {
                // a script (re)starting the race the C# flow is already running (a late PostRaceActivity): leave it
                var ra = Rt.Activity(rg);
                if (ra != null && ra.Running && ra.Current?.Name != "done" && Race?.Event?.Key == rg.Key) return false;
            }
            if (driving && (b.Kind == LuaBinding.Kinds.Action || name == "Activity.Run"))
            {
                if (LuaDrive.Execute(b, args, Rt, out var r))
                {
                    // the bounty the script paid is no news for WatchProfile (MNotifyPlayerRep was posted with the call)
                    if (name == "Game.AwardPlayerBounty" && GameSession.Profile != null) _bounty = GameSession.Profile.Bounty;
                    return r;
                }
                var kind = LuaDriveTable.Of(b);
                if (kind == LuaDriveTable.Kind.Missing && _missing.Add(name)) Debug.Log($"NFSMW lua: Drive has no C# for {b} (missing)");
                else if (kind == LuaDriveTable.Kind.Executed && _missing.Add(name)) Debug.Log($"NFSMW lua: Drive could not run {b} now (no driven race, or a missing argument)");
            }
            return null;
        }

        /// <summary>
        /// The scripts' actions execute: Drive mode, unless the race running now is not a driven one (a race set up when
        /// the scripts could not drive it: split screen, LAN, no runtime yet) — then the scripts only watch it, as in
        /// Shadow, so they cannot hold its cars with an NIS or a staging call nobody releases [ours].
        /// </summary>
        public static bool Driving => LuaFlow.Mode == LuaFlowMode.Drive && !(Race != null && !Race.Driven);

        /// <summary>The losing results are up: their closing posts RestartRace or NotifyRaceAbandoned (LuaFlow.OnRaceBegin asks).</summary>
        public bool LosingScreenPending => _waitLosing;

        void Record(LuaBinding b, object[] args)
        {
            string line = $"{Now,7:0.00} {b.FullName}({string.Join(", ", args.Select(Fmt))})";
            LuaFlow.Calls.Add(line);
            if (LuaFlow.Calls.Count > 300) LuaFlow.Calls.RemoveAt(0);
            if (LuaFlow.Verbose) Debug.Log("NFSMW lua: call " + line + (LuaFlow.Mode == LuaFlowMode.Shadow && b.Kind == LuaBinding.Kinds.Action ? " [recorded]" : ""));
        }

        static string Fmt(object o)
        {
            switch (o)
            {
                case null: return "nil";
                case PlayerCar c: return "racer" + RacerIndex(c);
                case GInstance g: return g.Name;
                case float f: return f.ToString("0.##");
                default: return o.ToString();
            }
        }

        /// <summary>The records the ledger compares with the C# flow.</summary>
        void Compare(LuaBinding b, object[] args)
        {
            var L = Ledger;
            if (L == null) return;
            switch (b.Table + "." + b.Name)
            {
                case "Game.SetRacerGoal": L.Lua(Now, $"Goal racer={args[0]} {(args[1] as GInstance)?.LocalName ?? "nil"}"); break;
                case "Game.NotifyCheckpointReached": L.Lua(Now, $"Checkpoint racer={RacerIndex(args[0])} n={args[1]}"); break;
                case "Game.NotifyLapFinished": L.Lua(Now, $"LapFinished racer={RacerIndex(args[0])} lap={args[1]}"); break;
                case "Game.NotifyRaceFinished": L.Lua(Now, $"RaceFinished racer={RacerIndex(args[0])}"); break;
                case "Game.KnockoutRacer":
                case "Game.TotalRacer": L.Lua(Now, $"Knockout racer={RacerIndex(args[0])}"); break;
                case "Game.AwardPlayerBounty": L.Lua(Now, $"Bounty +{args[0]}"); break;
                case "Game.UnlockRace":
                    {
                        // only a real change is comparable (the C# side sees flags change)
                        var ev = Event(args[0]);
                        if (ev != null && !RaceDatabase.IsCareerRaceUnlocked(ev)) L.Lua(Now, $"UnlockRace {ev.Name}");
                        break;
                    }
                case "Activity.Run":
                    {
                        var ev = Event(args[0]);
                        if (ev != null && ev.Start != null) L.Lua(Now, $"RaceStart {ev.Name}");
                        break;
                    }
            }
        }

        /// <summary>The C++ half of the bindings that posts back into the scripts.</summary>
        void EmulateCpp(LuaBinding b, object[] args)
        {
            switch (b.Table + "." + b.Name)
            {
                case "RaceStatus.NotifyScriptWhenLoaded": Wait(ref _waitLoading); break;     // 0x5EEA60 → LoadingComplete (GRaceStatus::Update)
                case "Game.KnockoutRacer":
                case "Game.TotalRacer":
                    // 0x611440 MNotifyKnockedOut (WatchRace does not post it again for this racer)
                    Rt.Post(StategraphRuntime.NotifyKnockedOut, ("Racer", args[0]));
                    if (EntrantOf(args[0]) is RaceManager.Entrant ke) _knockPosted.Add(ke);
                    break;
                case "Game.AwardPlayerBounty": Rt.Post(StategraphRuntime.NotifyPlayerRep, ("RepPoints", args[0])); break;   // 0x612220 MNotifyPlayerRep
                case "Game.SabotageEngine":
                    {
                        // 0x60AB60: the engine blows after the given seconds (Drive: the car really blows, and WatchRace posts
                        // MNotifyEngineBlown when it does)
                        if (Driving && args[0] is PlayerCar) break;
                        var car = args[0];
                        float t = args[1] is float f ? f : 0;
                        _later.Add((Now + t, () => Rt.Post(StategraphRuntime.NotifyEngineBlown, ("Racer", car))));
                        break;
                    }
                case "Game.BlowEngine":
                    if (Driving && args[0] is PlayerCar) break;
                    Rt.Post(StategraphRuntime.NotifyEngineBlown, ("Racer", args[0]));
                    break;
                case "Camera.SetGenericCamera":
                    // 0x604A50: ECinematicMoment(group, track, 0) — the GENERIC ICE camera `track` of `group`
                    // ('Cinematics', 'Challenge': 3.5 s); CameraAI::Director::SelectAction (0x479BD5) posts MICECameraFinished
                    // when that ICE action ends, so the message comes after the track's Length (3 s when the track is not found)
                    _later.Add((Now + IceTrackLength(args[0] as string, args[1] as string), () => Rt.Post(StategraphRuntime.IceCameraFinished)));
                    break;
                case "Game.PlayTutorial":
                    // 0x6124E0: a career drag/tollbooth/speedtrap race not yet seen plays its tutorial movie (the C# flow
                    // decides that), else MNotifyMovieFinished at once
                    Wait(ref _waitMovie);
                    break;
                case "NIS.Play": Wait(ref _waitNis); break;
                case "Game.DoSafeHouseIntro": Wait(ref _waitMovie); break;                       // storyfmv_saf25
                case "Game.EnterPostRaceFlow":
                case "Game.ShowWinningPostRaceScreen": Wait(ref _waitPostRace); break;
                case "Game.ShowLosingPostRaceScreen": Wait(ref _waitLosing); _losingRace = Race; break;
            }
        }

        /// <summary>GRaceStatus::NotifyScriptWhenLoaded's "still loading" (0x5EEA60 / 0x603132), as far as the port knows it.</summary>
        static bool StillLoading
        {
            get
            {
                if (GameFlow.RaceLoading) return true;
                var w = GameBootstrap.Instance ? GameBootstrap.Instance.World : null;
                return w && w.FarLoad && w.IsLoadingInProgress;
            }
        }

        static IceCameras _ice;
        static bool _iceTried;

        /// <summary>The GENERIC (context 3) ICE track `track` of scene `group` (InGameB.bun), its Length in seconds.</summary>
        static float IceTrackLength(string group, string track)
        {
            try
            {
                if (!_iceTried) { _iceTried = true; _ice = IceCameras.Load(GameData.Root); }
                var tracks = _ice?.Find(3, group ?? "");
                var t = tracks?.FirstOrDefault(x => string.Equals(x.Name, track, StringComparison.OrdinalIgnoreCase));
                if (t != null && t.Length > 0) return t.Length;
            }
            catch (Exception e) { Debug.LogWarning("NFSMW lua: ICE cameras: " + e.Message); }
            return 3f;   // [ours]
        }

        void Wait(ref bool flag)
        {
            flag = true;
            _waitSince = Time.time;
        }

        public void Event(string name, object[] args)
        {
            if (LuaFlow.Verbose) Debug.Log($"NFSMW lua: event {name}({string.Join(", ", args.Select(Fmt))})");
            LuaFlow.Calls.Add($"{Now,7:0.00} {name}({string.Join(", ", args.Select(Fmt))})");
            int Id(int i) => args.Length > i && args[i] is float f ? (int)f : -1;
            switch (name)
            {
                case "EAddSMS": if (Id(0) >= 0) Ledger?.Lua(Now, $"SMS {Id(0)}"); break;
                case "ECellCall":
                    if (Id(0) >= 0) Ledger?.Lua(Now, $"SMS {Id(0)}");
                    // MNotifyCellCallStarted / Complete come from the speech manager (CopSpeechManager.CellCallStarted / Completed,
                    // GameSpeech.cpp:340 / :391, SpeechManager.cpp:69-77 and :957) once ECellCall's SMSCellCall (0x62B1CD) has run;
                    // [ours] a call that never starts (no speech module, Shadow mode) completes after 2 s so the script cannot hang
                    _cellCallPending = true;
                    _later.Add((Now + 2f, () => { if (_cellCallPending && !_cellCallStarted) { _cellCallPending = false; Rt.Post(StategraphRuntime.NotifyCellCallStarted); Rt.Post(StategraphRuntime.NotifyCellCallComplete); } }));
                    break;
                case "EShowRaceCountdown": Wait(ref _waitCountdown); break;
                case "EPlayRaceNIS": Wait(ref _waitNis); break;
                case "EPlayRaceMovie": Wait(ref _waitMovie); break;
                case "EShowMessageScreen": Wait(ref _waitMessage); break;
                case "EShowPostPursuit": Wait(ref _waitPostRace); break;
            }
            if (Driving && !LuaDrive.Event(name, args) && _missing.Add(name))
                Debug.Log($"NFSMW lua: Drive has no C# for event {name} (missing)");
        }

        public void Log(string line)
        {
            if (line.StartsWith("error")) Debug.LogWarning("NFSMW lua: " + line);
            else if (LuaFlow.Verbose) Debug.Log("NFSMW lua: " + line);
        }

        // ---------------------------------------------------------------------------------------------------------
        // the per-frame side: completions, and what the C# game does that the C++ would post

        bool _countdownSeen, _hadRace, _lateLogged;
        RaceManager _race;
        int _lastSec = -1;
        bool _expiredSent, _inPursuit;
        readonly Dictionary<RaceManager.Entrant, (int hit, int laps, bool fin, bool outed, bool blown, bool wrecked, string goal)> _seen =
            new Dictionary<RaceManager.Entrant, (int, int, bool, bool, bool, bool, string)>();

        bool _cellCallPending, _cellCallStarted;
        static int _cellStartedCount, _cellCompletedCount;
        int _cellStartedSeen = System.Threading.Volatile.Read(ref _cellStartedCount), _cellCompletedSeen = System.Threading.Volatile.Read(ref _cellCompletedCount);
        static bool _cellHooked;

        /// <summary>CopSpeechManager.CellCallStarted / CellCallCompleted → counted here (they may fire from the speech update), posted
        /// into the scripts on the next Update as NotifyCellCallStarted / NotifyCellCallComplete.</summary>
        internal static void HookCellCalls()
        {
            if (_cellHooked) return;
            _cellHooked = true;
            NfsMw.Audio.CopSpeechManager.CellCallStarted += () => System.Threading.Interlocked.Increment(ref _cellStartedCount);
            NfsMw.Audio.CopSpeechManager.CellCallCompleted += () => System.Threading.Interlocked.Increment(ref _cellCompletedCount);
        }

        void PumpCellCalls()
        {
            int st = System.Threading.Volatile.Read(ref _cellStartedCount), co = System.Threading.Volatile.Read(ref _cellCompletedCount);
            while (_cellStartedSeen < st) { _cellStartedSeen++; _cellCallStarted = true; Rt.Post(StategraphRuntime.NotifyCellCallStarted); }
            while (_cellCompletedSeen < co) { _cellCompletedSeen++; _cellCallPending = _cellCallStarted = false; Rt.Post(StategraphRuntime.NotifyCellCallComplete); }
        }

        public void Update()
        {
            float t = Time.time;
            PumpCellCalls();
            for (int i = 0; i < _later.Count; i++)
                if (_later[i].at <= Now) { var a = _later[i].a; _later.RemoveAt(i--); a(); }
            var rm = Race;
            bool fe = LuaFlow.FrontEndOpen;

            // completions. Once the C# race is past its intro and countdown, every intro wait of the script is over too
            // (the script catches up with the C# race); any wait also ends after 20 s, logged once [ours]
            bool drive = LuaFlow.Mode == LuaFlowMode.Drive;
            if (drive) LuaDrive.Update();
            bool csRacing = rm != null && !rm.Countdown && !rm.Intro;
            // a movie, story card or NIS on screen is what the script waits for: the 20 s fallback waits too [ours]
            bool media = MoviePlayer.Playing || StoryCard.Showing || NisPlayer.Active != null;
            if (media && drive) _waitSince = Mathf.Max(_waitSince, t - 19f);
            bool late = t - _waitSince > 20f;
            bool waiting = _waitLoading || _waitCountdown || _waitNis || _waitMovie || _waitMessage;
            if (late && waiting && !_lateLogged)
            {
                _lateLogged = true;
                Debug.Log("NFSMW lua: no C# signal after 20 s for" + (_waitLoading ? " LoadingComplete" : "") + (_waitCountdown ? " CountdownDone" : "") +
                          (_waitNis ? " NISComplete" : "") + (_waitMovie ? " NotifyMovieFinished" : "") + (_waitMessage ? " NotifyMessageDone" : "") +
                          $" (movie {MoviePlayer.Playing}, story card {StoryCard.Showing}, nis {NisPlayer.Active != null}, race intro {rm?.Intro}, countdown {rm?.Countdown}); posted it");
            }
            // GRaceStatus::Update (0x603132): MLoadingComplete once nothing loads — the racers' models and audio, the reserved
            // stock cars, a pending cop spawn, the track streamer (a near load only when TrackStreamer byte +0x193 is set
            // [?]); here: the race's own load and a blocking (far) world load [ours]
            if (_waitLoading && t > _waitSince && (!StillLoading || late)) { _waitLoading = false; Rt.Post(StategraphRuntime.LoadingComplete); }
            if (_waitCountdown && (csRacing || late || (rm == null && t - _waitSince > 4f))) { _waitCountdown = false; Rt.Post(StategraphRuntime.CountdownDone); }
            // (Drive: the race holds Intro while it waits for the script, so the NIS that counts is the one it plays)
            bool nisUp = rm != null && (rm.Driven ? rm.NisPlaying : rm.Intro);
            if (_waitNis && t > _waitSince + 0.1f && ((!nisUp && NisPlayer.Active == null) || csRacing || late)) { _waitNis = false; Rt.Post(StategraphRuntime.NisComplete); }
            if (_waitMovie && t > _waitSince + 0.1f && ((!MoviePlayer.Playing && !StoryCard.Showing) || csRacing || late)) { _waitMovie = false; Rt.Post(StategraphRuntime.NotifyMovieFinished); }
            if (_waitMessage && t > _waitSince + 0.1f && (!StoryCard.Showing && !(UI.FrontEnd.Instance != null && UI.FrontEnd.Instance.Top is UI.SixDaysLaterScreen) || csRacing || late)) { _waitMessage = false; Rt.Post(StategraphRuntime.NotifyMessageDone); }
            if (!waiting) _lateLogged = false;
            // (Drive: the results close and clean the race up first; NoPostRaceScreen leaves at once)
            if (_waitPostRace && t > _waitSince + 0.5f && !fe && !(drive && rm != null && rm.Driven && !rm.Over)) { _waitPostRace = false; Rt.Post(StategraphRuntime.FlowReadyForOutro); }
            if (_waitLosing && t > _waitSince + 0.5f && !fe)
            {
                _waitLosing = false;
                // the losing results closed: Restart (a new RaceManager for the event) or quit
                if (rm != null && rm != _losingRace) Rt.Post(StategraphRuntime.RestartRace);
                else Rt.Post(StategraphRuntime.NotifyRaceAbandoned);
            }

            // races
            if (rm != _race)
            {
                if (_race != null && rm == null && _hadRace && !_waitLosing && !_waitPostRace && !_race.Entrants.Any(e => e.Human && e.Finished) && !_race.ScriptEnded)
                    Rt.Post(StategraphRuntime.NotifyRaceAbandoned);   // quit from the pause menu
                _race = rm;
                _seen.Clear();
                _knockPosted.Clear();
                _lastSec = -1;
                _expiredSent = false;
                _countdownSeen = false;
                _hadRace = rm != null;
                if (rm != null) LuaFlow.OnRaceBegin(rm);
            }
            if (rm != null) WatchRace(rm);

            // the engage dialog (EngageEventScreen: Accept 0xD72F002A → MAcceptEnterCareerEvent — LuaFlow.OnRaceBegin —,
            // Decline 0x694B896E / back → MDeclineEnterCareerEvent): closed with no race on its way = declined
            var feTop = UI.FrontEnd.Instance != null && UI.FrontEnd.Instance.IsOpen ? UI.FrontEnd.Instance.Top : null;
            bool engageUp = feTop is UI.EngageEventScreen;
            if (_engageUp && !engageUp && !GameFlow.RaceLoading && RaceManager.Active == null && Rt.RunningActivities.Any(a => a.Current?.Name == "prompt"))
                Rt.Post(StategraphRuntime.DeclineEnterCareerEvent, ("SimTime", Now));
            _engageUp = engageUp;

            // pursuits (PerpEscaped / PerpBusted) [?: the exact C++ posting points]
            var police = Systems.Police;
            bool inP = police && police.InPursuit;
            if (_inPursuit && !inP)
            {
                var player = PlayerCar(1);
                Rt.Post(police.Busting >= 1f ? StategraphRuntime.PerpBusted : StategraphRuntime.PerpEscaped, ("Perpetrator", player));
            }
            if (inP && !_inPursuit) _bestInPursuit.Clear();   // GManager::NotifyPursuitStarted (0x5EDD10): tracking reset [?]
            _inPursuit = inP;
            // GManager::UpdatePursuit (0x6013F0) → TrackValue(name, v) (0x5EDA20): the value is kept, and only a new best of
            // this pursuit (bigger, or smaller for a smaller-is-better type; the first value always) posts
            // MNotifyMilestoneProgress {MilestoneName, ValueReached} (FUN_005DD670)
            if (inP)
            {
                var stats = police.Stats;
                foreach (var type in PursuitStats.Types)
                {
                    float v = stats.Value(type);
                    bool bigger = type != "pursuit_evasion_time";   // MilestoneTypeInfo flag 1 (Career.Milestone.BiggerIsBetter)
                    if (!bigger && v <= 0) continue;   // [ours: the evasion time is 0 until the evade]
                    if (_bestInPursuit.TryGetValue(type, out float best) && (bigger ? v <= best : v >= best)) continue;
                    _bestInPursuit[type] = v;
                    Rt.Post(StategraphRuntime.NotifyMilestoneProgress, ("MilestoneName", type), ("ValueReached", v));
                }
            }

            WatchProfile(t);
        }

        readonly Dictionary<string, float> _bestInPursuit = new Dictionary<string, float>();
        bool _engageUp;
        readonly HashSet<RaceManager.Entrant> _knockPosted = new HashSet<RaceManager.Entrant>();

        int _inbox = -1, _bounty = -1, _milestones = -1;
        float _unlockScanAt;
        HashSet<string> _unlocked;
        readonly HashSet<string> _unlockSeen = new HashSet<string>();

        /// <summary>
        /// The career side the C# flow changes: new SMS in the inbox, bounty, completed challenges and race unlocks become
        /// ledger records; bounty and challenges also reach the scripts (MNotifyPlayerRep, MNotifyChallengePassed).
        /// </summary>
        void WatchProfile(float t)
        {
            var p = GameSession.Profile;
            if (p == null || GameSession.Mode != GameMode.Career) { _inbox = _bounty = _milestones = -1; _unlocked = null; return; }
            if (_inbox >= 0) for (int i = _inbox; i < p.Inbox.Count; i++) Ledger?.CSharp(Now, $"SMS {p.Inbox[i]}");
            _inbox = p.Inbox.Count;
            if (_bounty >= 0 && p.Bounty > _bounty)
            {
                Ledger?.CSharp(Now, $"Bounty +{p.Bounty - _bounty}");
                Rt.Post(StategraphRuntime.NotifyPlayerRep, ("RepPoints", p.Bounty - _bounty));
            }
            _bounty = p.Bounty;
            if (_milestones >= 0 && p.Milestones.Count > _milestones) Rt.Post(StategraphRuntime.NotifyChallengePassed);
            _milestones = p.Milestones.Count;
            if (t >= _unlockScanAt)
            {
                _unlockScanAt = t + 1f;
                var now = new HashSet<string>(RaceDatabase.AllRaces().Where(RaceDatabase.IsCareerRaceUnlocked).Select(e => e.Name));
                // each race once per session (the C# profile can be reloaded and re-unlocked) [ours]
                if (_unlocked != null) foreach (var n in now) if (!_unlocked.Contains(n) && _unlockSeen.Add(n)) Ledger?.CSharp(Now, $"UnlockRace {n}");
                _unlocked = now;
            }
        }

        void WatchRace(RaceManager rm)
        {
            if (rm.Countdown) _countdownSeen = true;
            bool racing = !rm.Countdown && !rm.Intro;
            // the race clock: NotifyRaceTimeSecTick once per whole second, NotifyRaceTimeExpired once (GRaceStatus::Update 0x602C80)
            if (racing)
            {
                int sec = Mathf.FloorToInt(rm.RaceTime);
                if (_lastSec >= 0 && sec > _lastSec) Rt.Post(StategraphRuntime.NotifyRaceTimeSecTick);
                _lastSec = sec;
                if (rm.TimeLeft == 0 && !_expiredSent && rm.Event != null && rm.Event.TimeLimit > 0)
                {
                    _expiredSent = true;
                    Rt.Post(StategraphRuntime.NotifyRaceTimeExpired, ("Racer", rm.PlayerCarObject));
                }
            }
            var ev = rm.Event;
            for (int i = 0; i < rm.Entrants.Count; i++)
            {
                var e = rm.Entrants[i];
                int idx = i + 1;
                bool blown = e.EngineBlown || (e.Car && e.Car.EngineBlown);
                bool wrecked = e.Totalled || (e.Car && e.Car.Destroyed);
                string goal = GoalName(rm, e, ev);
                if (!_seen.TryGetValue(e, out var s))
                {
                    _seen[e] = (e.CheckpointsHit, e.LapsCompleted, e.Finished, e.Out, blown, wrecked, goal);
                    if (goal != null) Ledger?.CSharp(Now, $"Goal racer={idx} {goal}");
                    continue;
                }
                if (e.CheckpointsHit > s.hit) for (int n = s.hit + 1; n <= e.CheckpointsHit; n++) Ledger?.CSharp(Now, $"Checkpoint racer={idx} n={n}");
                if (e.LapsCompleted > s.laps) for (int n = s.laps + 1; n <= e.LapsCompleted; n++) Ledger?.CSharp(Now, $"LapFinished racer={idx} lap={n}");
                if (e.Finished && !s.fin) Ledger?.CSharp(Now, $"RaceFinished racer={idx}");
                if (e.Out && !s.outed) Ledger?.CSharp(Now, $"Knockout racer={idx}");
                // a knockout the C++ half made (Game_NotifyLapFinished's lap knockout) reaches the script
                if (e.Out && !s.outed && _knockPosted.Add(e) && e.Car) Rt.Post(StategraphRuntime.NotifyKnockedOut, ("Racer", e.Car));
                if (goal != null && goal != s.goal && !e.Finished) Ledger?.CSharp(Now, $"Goal racer={idx} {goal}");
                // what the C++ posts for the C# race's incidents
                if (blown && !s.blown) Rt.Post(StategraphRuntime.NotifyEngineBlown, ("Racer", e.Car));
                if (wrecked && !s.wrecked) Rt.Post(StategraphRuntime.NotifyVehicleDestroyed, ("Racer", e.Car));
                _seen[e] = (e.CheckpointsHit, e.LapsCompleted, e.Finished, e.Out, blown, wrecked, goal);
            }
        }

        /// <summary>The C# racer's goal as the trigger's local name (TargetFor matched to the event's checkpoints / finish).</summary>
        static string GoalName(RaceManager rm, RaceManager.Entrant e, RaceEvent ev)
        {
            var target = rm.TargetFor(e);
            if (!target.HasValue || ev == null) return null;
            foreach (var p in ev.Checkpoints.Concat(ev.Finish != null ? new[] { ev.Finish } : Array.Empty<RacePoint>()))
            {
                var u = Coords.ToUnity(p.Position);
                if ((new Vector2(u.x, u.z) - new Vector2(target.Value.x, target.Value.z)).sqrMagnitude < 4f)
                {
                    int i = p.Name?.LastIndexOf('/') ?? -1;
                    return i >= 0 ? p.Name.Substring(i + 1) : p.Name;
                }
            }
            return null;
        }
    }
}
