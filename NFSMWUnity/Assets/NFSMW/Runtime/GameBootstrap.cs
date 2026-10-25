using System.Collections;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// Scene entry. With StartInFrontEnd the game boots to the press-start screen and the menus decide what to
    /// load (career, free roam, races); otherwise it drops straight into free roam (tests, quick checks).
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        public static GameBootstrap Instance { get; private set; }

        public string Car = "BMWM3GTR";
        public Color Paint = new Color(0.82f, 0.84f, 0.86f);
        // game coordinates (X east, Y north, Z up) of the free roam start; a street in Rosewood by default
        public Vector3 StartGame = new Vector3(679.4f, 2288.0f, 153.9f);
        public float LoadRadius = 500f;
        public bool StartInFrontEnd;

        public WorldStreamer World { get; private set; }
        public PlayerCar Player { get; private set; }
        public bool Ready { get; private set; }
        public bool Loading { get; private set; }

        // the input module (Input/Common): the HUD action queue on port 0 (controller config `hud`), the cheats, the rumble
        Controls.ActionQueue _hudQueue;
        readonly System.Collections.Generic.HashSet<int> _pressed = new System.Collections.Generic.HashSet<int>();
        bool _eggsActive;
        System.Action<char> _eggKeys;

        /// <summary>A HUD / game action pressed this frame (ActionQueue of the `hud` config: HUDACTION_PAUSEREQUEST, HUDACTION_ENGAGE_EVENT, …; the FE controls page's keys).</summary>
        public bool ActionPressed(string action) => _pressed.Contains(Controls.IOModule.GameActionId(action));

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            Physics.simulationMode = SimulationMode.FixedUpdate;
            GameData.Mount();
            SetupEnvironment();
            GameSession.Load();
            Settings.Apply();
            MusicPlayer.Ensure();
            // IOModule::Initialize + the game's action queues (IPlayer / IHud: `hud` config on port 0) and the rumble (LocalPlayer::DoFFB)
            Controls.IOModule.Initialize();
            _hudQueue = new Controls.ActionQueue(0, "hud", "hud q", true);
            Controls.RumbleDriver.Ensure();
            if (QaTour.Requested && !FindAnyObjectByType<QaTour>()) { gameObject.AddComponent<QaTour>(); yield break; }
            if (StartInFrontEnd)
            {
                // the EA logo and intro movies, when converted copies are in MOVIES
                NfsMw.UI.BootFlow.Start();   // [round 9 FE hook] BootFlowManager: EA logo, PSA, (PAL: attract), splash, main menu
                yield break;
            }
            GameSession.Mode = GameMode.FreeRoam;
            GameSession.QuickCar = new OwnedCar { Car = Car.ToLowerInvariant(), Paint = Paint };
            yield return EnterWorld(Coords.ToUnity(StartGame.x, StartGame.y, StartGame.z), null);
        }

        /// <summary>Streams the world around pos (Unity space), then places the active car there (heading in degrees, or the nearest road's).</summary>
        public IEnumerator EnterWorld(Vector3 pos, float? headingDeg)
        {
            Loading = true;
            Ready = false;
            if (World == null)
            {
                World = new GameObject("World").AddComponent<WorldStreamer>();
                World.LoadRadius = LoadRadius;
                World.UnloadRadius = LoadRadius + 200f;
                World.Begin();
            }
            var focus = new GameObject("Stream Focus").transform;
            focus.position = pos;
            World.Focus = focus;
            while (!World.Ready) yield return null;
            yield return null;
            while (World.BuildingCount > 0 || AnyPending()) yield return null;
            SpawnPlayer(pos, headingDeg);
            World.Focus = Player.transform;
            Destroy(focus.gameObject);
            Controls.CameraShaker.ResetCameraShakers();   // World_Init (0x733A6A)
            Debug.Log($"NFSMW: start area ready, {World.LoadedCount} sections");
            Loading = false;
            Ready = true;
            RefreshBarriers();
        }

        bool AnyPending()
        {
            foreach (var s in World.Sections)
                if (s.Pending != null) return true;
            return false;
        }

        /// <summary>Edit-mode/batch variant: builds the start area synchronously.</summary>
        public void BuildImmediate()
        {
            GameData.Mount();
            GameSession.Load();
            SetupEnvironment();
            World = new GameObject("World").AddComponent<WorldStreamer>();
            World.LoadRadius = LoadRadius;
            World.BeginAndWait();
            var start = Coords.ToUnity(StartGame.x, StartGame.y, StartGame.z);
            World.Stream(start, true);
            GameSession.QuickCar = new OwnedCar { Car = Car.ToLowerInvariant(), Paint = Paint };
            SpawnPlayer(start, null);
            Ready = true;
        }

        /// <summary>Puts up the career's barrier groups: the bin's locked areas, plus the route barriers of a race.</summary>
        public void RefreshBarriers()
        {
            if (!World) return;
            World.Racing = RaceManager.Active != null;
            World.SplitScreen = Player2 != null;
            if (GameSession.Career == null) { World.SetBarrierGroups(new string[0], new string[0]); return; }
            // free roam: the bin's closed-off areas; in a race only that race's route barriers (a prologue race
            // crosses areas the bin keeps closed in free roam)
            var up = RaceManager.Active != null && RaceManager.Active.Event != null ? RaceManager.Active.Event.Barriers : GameSession.FreeRoamBarriers();
            World.SetBarrierGroups(GameSession.AllBarriers(), up);
        }

        public void SpawnPlayer(Vector3 near, float? headingDeg)
        {
            var owned = GameSession.ActiveCar ?? new OwnedCar { Car = Car.ToLowerInvariant(), Paint = Paint };
            if (Player != null) Destroy(Player.gameObject);
            var built = CarBuilder.Build(owned.Car.ToUpperInvariant(), owned.Paint, null, owned);
            var root = built.Root;
            root.name = "Player Car";
            root.AddComponent<Rigidbody>();
            AddBodyCollider(built);
            Player = root.AddComponent<PlayerCar>();
            Player.Setup(Upgrades.Apply(built.Spec, owned), built.Wheels);
            Player.BrakeLights = built.BrakeLights;
            EngineAudio.Attach(Player, owned.Car, 0.6f);
            CarSfx.Attach(Player, true);

            Vector3 pos = near, fwd = Vector3.forward;
            if (headingDeg.HasValue)
            {
                // game heading: degrees from +X (east) toward +Y (north); Unity: X east, Z north
                float a = headingDeg.Value * Mathf.Deg2Rad;
                fwd = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            }
            else if (World.FindRoadPoint(near, out var p, out var f)) { pos = p; fwd = f; }
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 10f)) pos = hit.point;
            var rot = Quaternion.LookRotation(fwd, Vector3.up);
            Player.SetResetPoint(pos, rot);
            Player.Teleport(pos + Vector3.up * 0.4f, rot);

            var cam = Camera.main;
            if (cam)
            {
                var chase = cam.GetComponent<ChaseCamera>();
                if (!chase) chase = cam.gameObject.AddComponent<ChaseCamera>();
                chase.Target = root.transform;
                chase.Snap();
            }
            foreach (var old in FindObjectsByType<UI.GameHud>(FindObjectsSortMode.None))
                if (old.Car == null || old.Car == Player || !Player2) Destroy(old.gameObject);
            if (UI.GameHud.Create(Player, Player2 ? 1 : 0) == null)
            {
                var hud = FindFirstObjectByType<Hud>();
                if (!hud) hud = gameObject.AddComponent<Hud>();
                hud.Car = Player;
            }
            if (World) World.Focus = Player.transform;
            Debug.Log($"NFSMW: spawned {owned.Car} at game {Coords.ToGame(pos)} (torque peak {Mathf.Max(built.Spec.Torque)} lb-ft, {built.Spec.ForwardGears} gears, split {built.Spec.TorqueSplit})");
        }

        public static void AddBodyCollider(CarBuilder.Result built)
        {
            var col = built.Root.AddComponent<BoxCollider>();
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (var r in built.Body.GetComponentsInChildren<Renderer>())
            {
                var lb = r.bounds;
                lb.center -= built.Root.transform.position;
                if (first) { b = lb; first = false; } else b.Encapsulate(lb);
            }
            col.center = b.center + Vector3.up * 0.12f;
            col.size = new Vector3(b.size.x * 0.92f, Mathf.Max(0.5f, b.size.y - 0.3f), b.size.z * 0.96f);
        }

        /// <summary>Puts the player car at a race start / spawn point (game coordinates, heading in degrees).</summary>
        public void PlacePlayer(Vec3 gamePos, float headingDeg)
        {
            var pos = Coords.ToUnity(gamePos);
            if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out var hit, 12f)) pos = hit.point;
            float a = headingDeg * Mathf.Deg2Rad;
            var rot = Quaternion.LookRotation(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)), Vector3.up);
            Player.SetResetPoint(pos, rot);
            Player.Teleport(pos + Vector3.up * 0.4f, rot);
            var chase = Camera.main ? Camera.main.GetComponent<ChaseCamera>() : null;
            if (chase) chase.Snap();
        }

        public PlayerCar Player2 { get; private set; }
        Camera _cam2;

        /// <summary>Split screen: a second human car beside the first, the screen cut in two (player one on top).</summary>
        public PlayerCar BeginSplitScreen(OwnedCar owned)
        {
            EndSplitScreen();
            var t = Player.transform;
            Player2 = CarSpawner.Spawn(owned.Car, owned.Paint, owned, t.position + t.right * 3.4f, t.rotation, "Player Two");
            var ai = Player2.GetComponent<AIDriver>();
            if (ai) Destroy(ai);
            Player2.InputSlot = 2;
            Player.InputSlot = 1;
            var main = Camera.main;
            main.rect = new Rect(0, 0.5f, 1, 0.5f);
            _cam2 = new GameObject("Camera Two").AddComponent<Camera>();
            _cam2.CopyFrom(main);
            _cam2.rect = new Rect(0, 0, 1, 0.5f);
            _cam2.tag = "Untagged";
            var chase = _cam2.gameObject.AddComponent<ChaseCamera>();
            chase.Target = Player2.transform;
            chase.Snap();
            World.ExtraFocus.Add(Player2.transform);
            foreach (var old in FindObjectsByType<UI.GameHud>(FindObjectsSortMode.None)) Destroy(old.gameObject);
            UI.GameHud.Create(Player, 1);
            UI.GameHud.Create(Player2, 2);
            Debug.Log($"NFSMW: split screen, player two in {owned.Car}");
            return Player2;
        }

        public void EndSplitScreen()
        {
            if (Player2) Destroy(Player2.gameObject);
            Player2 = null;
            if (_cam2) Destroy(_cam2.gameObject);
            if (Camera.main) Camera.main.rect = new Rect(0, 0, 1, 1);
            if (World) World.ExtraFocus.Clear();
            if (Player) Player.InputSlot = 0;
        }

        void Update()
        {
            UpdateInput();
            if (!Ready || Loading) return;
            var fe = FrontEnd.Instance;
            bool menuOpen = fe != null && fe.IsOpen;
            if (!menuOpen) PlayerCar.TickTimeScale(Time.unscaledDeltaTime);
            Time.timeScale = menuOpen || Game.GameFlow.SimulationPaused ? 0f : PlayerCar.WorldTimeScale;   // + RequestPauseSimulation (GameFlow)
            if (Time.timeScale > 0) Time.fixedDeltaTime = Time.timeScale / PlayerCar.SimRate;
            bool hold = RaceManager.Active != null && RaceManager.Active.Countdown;
            if (Player && !Player.GetComponent<AIDriver>()) Player.UseDeviceInput = !menuOpen && !hold;
            if (Player2 && !Player2.GetComponent<AIDriver>()) Player2.UseDeviceInput = !menuOpen && !hold;
            // HUDACTION_PAUSEREQUEST from the hud action queue (Escape / Start by default); the raw key is the fallback without a mapping
            bool pause = _hudQueue != null && _hudQueue.IsValid ? ActionPressed("HUDACTION_PAUSEREQUEST")
                : UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
            if (!menuOpen && !Game.NisPlayer.Active && pause)
            {
                fe = FrontEnd.Create();
                fe.Push(new PauseScreen());
            }
        }

        /// <summary>
        /// IOModule::Update once per frame (the devices polled, every ActionQueue fed), the hud queue drained into this frame's
        /// pressed set, EasterEggs::HandleJoy / KeyboardInput while the press-start (SplashScreen) is up — its ctor / dtor
        /// Activate / UnActivate the cheats (0x5A31D0 / 0x57DC62).
        /// </summary>
        void UpdateInput()
        {
            Controls.IOModule.Update();
            _pressed.Clear();
            if (_hudQueue != null)
                while (!_hudQueue.IsEmpty)
                {
                    var a = _hudQueue.GetAction();
                    if (a.HasValue && a.Value.Id > 0 && a.Value.Data >= 0.5f) _pressed.Add(a.Value.Id);
                    _hudQueue.PopAction();
                }
            var fe = FrontEnd.Instance;
            bool splash = fe != null && fe.IsOpen && fe.Top is PressStartScreen;
            if (splash != _eggsActive)
            {
                _eggsActive = splash;
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (splash)
                {
                    Controls.EasterEggs.Instance.Activate();
                    _eggKeys ??= c => Controls.EasterEggs.Instance.KeyboardInput(char.ToLowerInvariant(c));
                    if (kb != null) { kb.onTextInput -= _eggKeys; kb.onTextInput += _eggKeys; }
                }
                else
                {
                    Controls.EasterEggs.Instance.UnActivate();
                    if (kb != null && _eggKeys != null) kb.onTextInput -= _eggKeys;
                }
            }
            if (_eggsActive) Controls.EasterEggs.Instance.HandleJoy();
        }

        public void UnloadWorld()
        {
            EndSplitScreen();
            if (Player) Destroy(Player.gameObject);
            if (World) Destroy(World.gameObject);
            Player = null;
            World = null;
            Ready = false;
            PlayerCar.ResetTime();
            Time.timeScale = 1f;
        }

        static void SetupEnvironment()
        {
            var cam = Camera.main;
            if (cam)
            {
                cam.farClipPlane = 2500f;
                if (!cam.GetComponent<VisualTreatment>()) cam.gameObject.AddComponent<VisualTreatment>();
                cam.nearClipPlane = 0.5f;   // CameraMover.cpp:86
                QualitySettings.lodBias = 1f;           // scenery LOD heights are the game's pixel thresholds as they are
                QualitySettings.maximumLODLevel = 0;
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.78f, 0.74f, 0.62f);
            RenderSettings.fogStartDistance = 150f;
            RenderSettings.fogEndDistance = 1400f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.72f, 0.7f, 0.62f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.52f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.3f, 0.28f, 0.24f);
            var sun = RenderSettings.sun;
            if (!sun)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun)
            {
                sun.intensity = 1.15f;
                sun.transform.rotation = Quaternion.Euler(42, -35, 0);
                sun.shadows = LightShadows.Soft;
            }
            // world effects light and fog themselves (fx\world.efx); Unity's fog would double it
            RenderSettings.fog = false;
            VisualLighting.Apply(sun);
        }
    }

    /// <summary>Speed, gear, tachometer and nitrous, drawn with IMGUI until the original HUD is rebuilt.</summary>
    public sealed class Hud : MonoBehaviour
    {
        public PlayerCar Car;
        GUIStyle _big, _small, _center;

        void OnGUI()
        {
            if (!Car) return;
            if (FrontEnd.Instance != null && FrontEnd.Instance.IsOpen) return;
            _big ??= new GUIStyle(GUI.skin.label) { fontSize = 46, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight, normal = { textColor = Color.white } };
            _small ??= new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.LowerRight, normal = { textColor = new Color(1f, 0.85f, 0.3f) } };
            _center ??= new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, normal = { textColor = Color.white } };
            float w = Screen.width, h = Screen.height;
            int mph = Mathf.RoundToInt(Car.SpeedMps * 2.23694f);
            GUI.Label(new Rect(w - 260, h - 120, 220, 60), mph.ToString(), _big);
            GUI.Label(new Rect(w - 260, h - 70, 220, 30), "MPH   " + (Car.Gear < 0 ? "R" : Car.Gear == 0 ? "N" : Car.Gear.ToString()), _small);
            float rpm01 = Mathf.InverseLerp(0, Car.Spec.MaxRpm, Car.Rpm);
            GUI.DrawTexture(new Rect(w - 260, h - 36, 220, 8), Texture2D.grayTexture);
            var old = GUI.color;
            GUI.color = Car.Rpm > Car.Spec.RedLine ? Color.red : new Color(1f, 0.7f, 0.1f);
            GUI.DrawTexture(new Rect(w - 260, h - 36, 220 * rpm01, 8), Texture2D.whiteTexture);
            GUI.color = new Color(0.3f, 0.7f, 1f);
            GUI.DrawTexture(new Rect(w - 260, h - 22, 220 * Car.Nos, 6), Texture2D.whiteTexture);
            GUI.color = old;
            var race = RaceManager.Active;
            if (race != null) GUI.Label(new Rect(0, 20, w, 120), race.StatusText, _center);
            if (GameSession.Mode == GameMode.Career && GameSession.Profile != null)
                GUI.Label(new Rect(20, 20, 400, 30), $"{GameSession.Profile.Cash:N0} $   bounty {GameSession.Profile.Bounty:N0}", _small);
        }
    }
}
