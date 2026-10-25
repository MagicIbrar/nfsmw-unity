using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// Start the game with -qa to walk through every screen and mode unattended: each step saves a full-window
    /// screenshot (UI included) to QA\NN_step.png next to the game and writes QA\qa.log with the errors seen during
    /// that step. Uses its own save slot.
    /// </summary>
    public sealed class QaTour : MonoBehaviour
    {
        string _dir;
        int _step;
        readonly List<string> _errors = new List<string>();
        StreamWriter _log;

        public static bool Requested => Environment.GetCommandLineArgs().Any(a => a.Equals("-qa", StringComparison.OrdinalIgnoreCase));

        void Awake()
        {
            _dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "QA");
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.GetFiles(_dir, "*.png")) File.Delete(f);
            _log = new StreamWriter(Path.Combine(_dir, "qa.log")) { AutoFlush = true };
            Application.logMessageReceived += OnLog;
            Profile.Slot = "qa";
        }

        void OnDestroy() => Application.logMessageReceived -= OnLog;

        void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _errors.Add(msg + (string.IsNullOrEmpty(stack) ? "" : " @ " + stack.Split('\n')[0]));
        }

        IEnumerator Shot(string name, string note = "")
        {
            yield return new WaitForEndOfFrame();
            string file = $"{++_step:00}_{name}.png";
            ScreenCapture.CaptureScreenshot(Path.Combine(_dir, file));
            var cam = Camera.main;
            float fxRadius = name.StartsWith("race_running") ? 80f : 6f;   // the race shots pass the forest fog banks
            string fx = cam && Effects.EmitterSystem.Instance ? Effects.EmitterSystem.Instance.Near(cam.transform.position, fxRadius) : "";
            if (fx.Length > 0) note += $"\n   particles within {fxRadius} m of the camera: " + fx;
            var chase = cam ? cam.GetComponent<ChaseCamera>() : null;
            if (chase && chase.isActiveAndEnabled && chase.Target)
                note += $"\n   camera {chase.Director.Action} fov {chase.FovH:0} dist {Vector3.Distance(cam.transform.position, chase.Target.position):0.0} m" +
                        $" up {cam.transform.position.y - chase.Target.position.y:0.0} m timescale {Time.timeScale:0.00}";
            _log.WriteLine($"{file}: {note}{(_errors.Count > 0 ? "\n   ERRORS: " + string.Join("\n   ", _errors.Distinct().Take(6)) : "")}");
            _errors.Clear();
            yield return null;
        }

        /// <summary>A story shot: waits (up to 10 s, past the time card) for the movie to be on screen, then 1.5 s in.</summary>
        IEnumerator MovieShot(string name, string note)
        {
            yield return Until(() => MoviePlayer.Playing, 10f);
            if (MoviePlayer.Playing) yield return Wait(1.5f);
            yield return Shot(name, $"{note}; movie {(MoviePlayer.Playing ? MoviePlayer.Current : "none")}");
        }

        static IEnumerator Wait(float s)
        {
            float end = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        static FrontEnd FE => FrontEnd.Instance ? FrontEnd.Instance : FrontEnd.Create();

        IEnumerator Screen(FEScreen screen, string name, float settle = 1.2f)
        {
            FE.Push(screen);
            yield return Wait(settle);
            yield return Shot(name, screen.Package);
        }

        IEnumerator Run(string name, Func<IEnumerator> step)
        {
            IEnumerator e;
            try { e = step(); }
            catch (Exception ex) { _log.WriteLine($"STEP {name} FAILED to start: {ex}"); yield break; }
            while (true)
            {
                object cur;
                try
                {
                    if (!e.MoveNext()) break;
                    cur = e.Current;
                }
                catch (Exception ex) { _log.WriteLine($"STEP {name} FAILED: {ex.Message}\n{ex.StackTrace}"); break; }
                yield return cur;
            }
        }

        IEnumerator Start()
        {
            var boot = GameBootstrap.Instance;
            _log.WriteLine($"QA tour {DateTime.Now}, screen {UnityEngine.Screen.width}x{UnityEngine.Screen.height}");
            yield return Wait(4f);
            yield return Run("frontend", FrontEndSteps);
            yield return Run("story", () => StorySteps(boot));
            yield return Run("career", () => CareerSteps(boot));
            yield return Run("safehouse", SafehouseSteps);
            yield return Run("markers", () => MarkerSteps(boot));
            yield return Run("race", () => RaceSteps(boot));
            yield return Run("pursuit", () => PursuitSteps(boot));
            yield return Run("challenge", () => ChallengeSteps(boot));
            yield return Run("split", () => SplitSteps(boot));
            yield return Run("lan", LanSteps);
            _log.WriteLine("QA tour done");
            _log.Close();
            Application.Quit();
        }

        IEnumerator FrontEndSteps()
        {
            yield return Shot("press_start", FrontEnd.Instance?.Top?.Package);
            FE.Replace(new MainMenuScreen());
            yield return Wait(1.5f);
            yield return Shot("main_menu");
            yield return Screen(new NewCareerScreen(), "new_career_car_lot");
            yield return Screen(new QuickCarScreen(), "quick_car");
            yield return Screen(new RaceListScreen(RaceListScreen.Kind.QuickRace), "quick_race_list");
            yield return Screen(new OptionsScreen(), "options");
            FE.CloseAll();
        }

        static IEnumerator Until(Func<bool> cond, float timeout)
        {
            float t = 0;
            while (!cond() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
        }

        /// <summary>The career opening: prologue races, the scripted loss to Razor, car lot, drive to the safehouse.</summary>
        IEnumerator StorySteps(GameBootstrap boot)
        {
            FE.CloseAll();
            Story.NewCareer("QA Driver");
            yield return Wait(1.5f);
            yield return MovieShot("story_rac01", "intro movie (subtitles)");
            StoryCard.SkipAll();
            yield return Until(() => RaceManager.Active != null && !RaceManager.Active.Intro, 120f);
            yield return Wait(3f);
            var race = RaceManager.Active;
            yield return Shot("prologue_16_1_0", $"{race?.Event.EventId} rolling start, speed {boot.Player.SpeedMps * 2.237f:0} mph, car {GameSession.ActiveCar.Car}");
            // drive into Mia's call trigger
            if (race != null && race.Event.Triggers.TryGetValue("call_from_mia", out var call))
                boot.Player.Teleport(Coords.ToUnity(call.Position) + Vector3.up * 1f, boot.Player.transform.rotation);
            yield return Wait(2.5f);
            yield return Shot("prologue_mia_call", "SMS 10");
            yield return Until(() => RaceManager.Active == null, 30f);
            yield return Wait(1f);
            yield return Shot("prologue_16_1_0_over", FE.Top?.Package);
            FE.CloseAll();
            yield return Wait(1.5f);
            yield return MovieShot("story_cro06", "16.2.2 intro movie");
            // the three normal prologue races: count them as done, go to the D-Day race
            StoryCard.SkipAll();
            yield return Until(() => RaceManager.Active != null && !RaceManager.Active.Intro, 120f);
            yield return Wait(4.5f);
            yield return Shot("prologue_16_2_2_race", $"{RaceManager.Active?.Event.EventId} countdown {RaceManager.Active?.StatusText}");
            foreach (var id in new[] { "16.2.2", "16.2.3", "16.1.1" }) if (!GameSession.Profile.EventsDone.Contains(id)) GameSession.Profile.EventsDone.Add(id);
            GameSession.Profile.Cash = 30000;
            RaceManager.OnEnd = null;
            RaceManager.Script = null;
            RaceManager.Active?.Abort();
            Story.Resume();
            yield return Wait(1f);
            StoryCard.SkipAll();
            yield return Until(() => RaceManager.Active != null && !RaceManager.Active.Intro && !RaceManager.Active.Countdown, 120f);
            race = RaceManager.Active;
            yield return Wait(2f);
            yield return Shot("prologue_16_2_1_dday", race?.Event.EventId);
            if (race != null && race.Event.Triggers.TryGetValue("kill_engine", out var kill))
                boot.Player.Teleport(Coords.ToUnity(kill.Position) + Vector3.up * 1f, boot.Player.transform.rotation);
            yield return Wait(1.2f);
            yield return Shot("prologue_engine_blown", $"power {boot.Player.PowerScale}");
            yield return Until(() => StoryCard.Showing && !StoryCard.Fading, 30f);
            yield return Wait(1.5f);
            yield return MovieShot("story_bus12", "outro movie (subtitles)");
            StoryCard.SkipAll();
            yield return Wait(2f);
            yield return Shot("career_car_lot", $"{FE.Top?.Package} cash {GameSession.Profile.Cash}");
            FE.Top?.Tick(new MenuInput { Accept = true });   // buy the first starter car
            // D-Day lot: "Are you sure you wish to buy this car?" → Yes (uiQRCarSelect.cpp:627); the FEng engine delivers the
            // accept as a button message on a later frame, so the dialog opens after this Tick
            yield return Until(() => FE.Top is FEDialog, 5f);
            (FE.Top as FEDialog)?.Press(0);
            if (!(FE.Top is FEDialog) && GameSession.Profile.Stage == Story.CarLot) _log.WriteLine("   car lot: no buy dialog appeared");
            yield return Until(() => boot.Ready && boot.Player && FrontEnd.Instance != null && !FrontEnd.Instance.IsOpen, 120f);
            yield return Wait(3f);
            yield return Shot("drive_to_safehouse", $"stage {GameSession.Profile.Stage}, car {GameSession.ActiveCar.Car}, SMS 15 + GPS");
            boot.Player.Teleport(Coords.ToUnity(new Vec3(1768.4f, 2497.5f, 148.5f)), boot.Player.transform.rotation);
            yield return Wait(2f);
            yield return MovieShot("story_saf25", "safehouse intro");
            StoryCard.SkipAll();
            yield return Wait(1.5f);
            yield return Shot("safehouse_first", $"{FE.Top?.Package}, bin {GameSession.Profile.Bin}, stage {GameSession.Profile.Stage}");
            FE.CloseAll();
            yield return Wait(1f);
        }

        IEnumerator CareerSteps(GameBootstrap boot)
        {
            GameSession.Profile = new Profile { Name = "QA Driver", CareerStarted = true, Cash = 250000, Bounty = 30000 };
            GameSession.Profile.Cars.Add(new OwnedCar { Car = "gti", Paint = new Color(0.1f, 0.3f, 0.85f) });
            GameFlow.StartCareer();
            float t = 0;
            while (!boot.Ready && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return Wait(4f);
            yield return Shot("career_spawn", $"player {boot.Player?.transform.position}");
            // drive a few hundred metres on autopilot
            var ai = boot.Player.gameObject.AddComponent<AIDriver>();
            ai.Car = boot.Player;
            boot.Player.UseDeviceInput = false;
            int node = Roads.Nearest(boot.Player.transform.position, 200f);
            ai.SetPath(Systems.Wander(node, 30, 2.6f));
            ai.MaxSpeed = 35f;
            yield return Wait(8f);
            yield return Shot("career_driving", $"speed {boot.Player.SpeedMps * 2.237f:0} mph");
            yield return Wait(8f);
            yield return Shot("career_driving2", $"speed {boot.Player.SpeedMps * 2.237f:0} mph");
            // speedbreaker: world time ×0.25, the HUD bar drains
            boot.Player.SpeedBreaker = 1f;
            for (float w = 0; w < 8f && boot.Player.SpeedMps < 31f * 0.447f; w += Time.unscaledDeltaTime) yield return null;
            if (boot.Player.SpeedMps < 31f * 0.447f)   // city autopilot too slow: give it the 30 mph the button needs
                boot.Player.GetComponent<Rigidbody>().linearVelocity = boot.Player.transform.forward * 16f;
            boot.Player.ToggleSpeedBreaker();
            yield return Wait(1.5f);
            yield return Shot("career_speedbreaker", $"on {boot.Player.SpeedBreakerOn} time {Time.timeScale:0.00} charge {boot.Player.SpeedBreaker:0.00} speed {boot.Player.SpeedMps * 2.237f:0} mph");
            if (boot.Player.SpeedBreakerOn) boot.Player.ToggleSpeedBreaker();
            yield return Wait(2.5f);
            Destroy(ai);
            boot.Player.UseDeviceInput = true;
        }

        IEnumerator SafehouseSteps()
        {
            yield return Screen(new CareerMenuScreen(), "safehouse");
            yield return Screen(new SafehouseRaceSheetScreen(), "race_events");   // the Blacklist overview's Race Events (round 9)
            FE.Pop();
            yield return Screen(new RivalScreen(), "rival");
            FE.Pop();
            yield return Screen(new MilestonesScreen(), "milestones");
            FE.Pop();
            yield return Screen(new RapSheetScreen(), "rap_sheet", 4f);   // the package types its lines in over ~3 s (handler `call`)
            FE.Pop();
            yield return Screen(new CarLotScreen(false), "car_lot", 2.5f);
            FE.Pop();
            yield return Screen(new MyCarsScreen(), "my_cars", 2.5f);
            FE.Pop();
            yield return Screen(new CustomizeScreen(), "customize", 2.5f);
            yield return Screen(new PaintScreen(), "paint", 1.5f);
            FE.Pop();
            yield return Screen(new PerformanceScreen(), "performance");
            FE.Pop();
            yield return Screen(new VisualScreen(), "visual", 2f);
            yield return Screen(PartScreen.BodyKits(), "parts_bodykits", 3f);
            FE.Pop();
            yield return Screen(PartScreen.Spoilers(), "parts_spoilers", 3f);
            FE.Pop();
            yield return Screen(PartScreen.Rims(), "parts_rims", 3f);
            FE.Pop();
            yield return Screen(PartScreen.Vinyls(), "parts_vinyls", 3f);
            FE.CloseAll();
            yield return Wait(1f);
        }

        IEnumerator MarkerSteps(GameBootstrap boot)
        {
            var ev = GameSession.AvailableEvents().FirstOrDefault(e => e.Start != null);
            if (ev == null) { _log.WriteLine("no event for the marker check"); yield break; }
            var at = Coords.ToUnity(ev.Start.Position);
            boot.Player.Teleport(at - new Vector3(30, 0, 0) + Vector3.up * 1f, Quaternion.LookRotation(Vector3.right));
            yield return Wait(4f);
            yield return Shot("event_marker", ev.DisplayName);
            boot.Player.Teleport(at + Vector3.up * 1f, Quaternion.identity);
            yield return Wait(2f);
            yield return Shot("event_marker_prompt", ev.DisplayName);
        }

        IEnumerator RaceSteps(GameBootstrap boot)
        {
            var ev = GameSession.AvailableEvents().First(e => e.Start != null);
            GameFlow.StartRace(ev);
            float t = 0;
            while (RaceManager.Active == null && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return Wait(1f);
            yield return Shot("race_intro_1", ev.DisplayName);
            yield return Wait(2f);
            yield return Shot("race_intro_2");
            t = 0;
            while (RaceManager.Active != null && RaceManager.Active.Intro && t < 30f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return Wait(1.2f);
            yield return Shot("race_countdown", RaceManager.Active?.StatusText);
            var race = RaceManager.Active;
            var ai = boot.Player.gameObject.AddComponent<AIDriver>();
            ai.Car = boot.Player;
            var road = race.RoadRoute(boot.Player.transform.position);
            ai.SetPath(road.Count > 2 ? road : race.Route.ToList());
            yield return Wait(10f);
            yield return Shot("race_running", $"place {race.Entrants[0].Place}, progress {race.Entrants[0].Progress:0} m");
            yield return Wait(10f);
            yield return Shot("race_running2", $"place {race.Entrants[0].Place}, progress {race.Entrants[0].Progress:0} m");
            Destroy(ai);
            race.Abort();
            yield return Wait(2f);
            yield return Shot("after_race");
        }

        IEnumerator PursuitSteps(GameBootstrap boot)
        {
            Systems.Ensure();
            var police = Systems.Police;
            police.ForcePursuit(3);
            var ai = boot.Player.gameObject.AddComponent<AIDriver>();
            ai.Car = boot.Player;
            ai.SetPath(Systems.Wander(Roads.Nearest(boot.Player.transform.position, 300f), 40, 2.6f));
            ai.MaxSpeed = 40f;
            yield return Wait(6f);
            yield return Shot("pursuit", $"heat {police.Heat}, cops {police.CopCars.Count()}");
            yield return Wait(12f);
            yield return Shot("pursuit2", $"heat {police.Heat}, cops {police.CopCars.Count()}, roadblocks dodged {police.Stats.RoadblocksDodged}");
            Destroy(ai);
            police.ForceBusted();
            yield return Wait(2f);
            yield return Shot("busted_dashcam");
            float t = 0;
            while (NisPlayer.Active && t < 20f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return Wait(1f);
            yield return Shot("busted_result", FrontEnd.Instance?.Top?.Package);
            FrontEnd.Instance?.CloseAll();
            yield return Wait(1f);
        }

        IEnumerator ChallengeSteps(GameBootstrap boot)
        {
            GameFlow.QuitToMainMenu();
            yield return Wait(3f);
            yield return Screen(new ChallengeSeriesScreen(), "challenge_list");
            FE.CloseAll();
            var ev = GameSession.ChallengeEvents().First(e => e.Type == RaceType.Pursuit);
            GameSession.Mode = GameMode.ChallengeSeries;
            GameSession.QuickCar = new OwnedCar { Car = GameSession.Career.CarForPreset(ev.PlayerCar) ?? "gti" };
            GameFlow.StartRace(ev);
            float t = 0;
            while ((Systems.Police == null || Systems.Police.Challenge == null) && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return Wait(5f);
            yield return Shot("pursuit_challenge", ev.EventId);
            GameFlow.QuitToMainMenu();
            yield return Wait(3f);
        }

        IEnumerator SplitSteps(GameBootstrap boot)
        {
            GameSession.Mode = GameMode.QuickRace;
            GameSession.QuickCar = new OwnedCar { Car = "bmwm3gtr" };
            GameSession.QuickCar2 = new OwnedCar { Car = "gallardo", Paint = new Color(0.9f, 0.55f, 0.05f) };
            GameSession.SplitScreen = true;
            NisPlayer.Enabled = false;
            var ev = GameSession.Career.Bins[15].WorldRaces.First(e => e.Start != null);
            GameFlow.StartRace(ev);
            float t = 0;
            while ((RaceManager.Active == null || RaceManager.Active.Countdown) && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return Wait(3f);
            yield return Shot("split_screen", ev.DisplayName);
            NisPlayer.Enabled = true;
            GameFlow.QuitToMainMenu();
            yield return Wait(3f);
        }

        IEnumerator LanSteps()
        {
            yield return Screen(new LanMenuScreen(), "lan_menu");
            GameSession.Lan = LanSession.Client("QA", "gti");
            yield return Screen(new LanLobbyScreen(), "lan_join_lobby", 3f);
            GameSession.EndLan();
            FE.CloseAll();
        }
    }
}
