using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>The car on a turntable behind car-related menus (car lot, my cars, customize), drawn by its own camera.</summary>
    public sealed class CarShowroom : MonoBehaviour
    {
        static readonly Vector3 Origin = new Vector3(0, -2000, 0);
        Camera _cam;
        Transform _turntable;
        GameObject _car, _mirror;
        string _carName;
        Color _paint;
        Light _key, _rim;
        float _angle = 210f;
        public bool AutoRotate = true;

        void Awake() => EnsureInit();

        void EnsureInit()
        {
            if (_cam) return;
            var camGo = new GameObject("Showroom Camera");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.07f, 0.075f, 0.07f);
            _cam.fieldOfView = 34f;
            _cam.nearClipPlane = 0.3f;
            _cam.farClipPlane = 120f;
            _cam.depth = 50;
            camGo.AddComponent<VisualTreatment>().FrontEnd = true;
            _cam.cullingMask = 1 << ShowroomLayer;
            _back = FrontEndCanvas.Create("Showroom Back", _cam, 0);
            _back.transform.SetParent(transform, false);
            _back.planeDistance = 55f;
            SetLayer(_back.gameObject, ShowroomLayer);
            _turntable = new GameObject("Turntable").transform;
            _turntable.SetParent(transform, false);
            _turntable.position = Origin;

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(floor.GetComponent<Collider>());
            floor.transform.SetParent(_turntable, false);
            floor.transform.localScale = new Vector3(9f, 0.02f, 9f);
            floor.transform.localPosition = new Vector3(0, -0.02f, 0);
            var fm = new Material(Shader.Find("Standard")) { color = new Color(0.12f, 0.12f, 0.12f) };
            fm.SetFloat("_Glossiness", 0.75f);
            floor.GetComponent<Renderer>().sharedMaterial = fm;
            SetLayer(floor, ShowroomLayer);

            _floor = floor;

            _key = MakeLight("Key", new Vector3(35, -40, 0), 1.3f, new Color(1f, 0.95f, 0.85f));
            _rim = MakeLight("Rim", new Vector3(20, 150, 0), 0.8f, new Color(0.7f, 0.8f, 1f));
            SetActive(false);
        }

        // a layer the world never uses, so the showroom camera sees only the car and the floor
        const int ShowroomLayer = 31;
        GameObject _floor, _backdrop;
        readonly System.Collections.Generic.Dictionary<string, GameObject> _sets = new System.Collections.Generic.Dictionary<string, GameObject>();
        bool _on;

        /// <summary>Shows the named set (loaded once, then kept); null or a missing file falls back to the floor disc.</summary>
        public void UseSet(string file)
        {
            EnsureInit();
            if (file != null && !_sets.ContainsKey(file)) { _backdrop = null; LoadBackdrop(file); _sets[file] = _backdrop; }
            _backdrop = file != null ? _sets[file] : null;
            _garageType = GarageTypeOf(file);
            CarRender.CarEnvironment.SetGarage(file ?? "QRACE");   // the garage light rig of CarRenderInfo::Render (FEManager::GetGarageType)
            foreach (var kv in _sets) if (kv.Value) kv.Value.SetActive(_on && kv.Value == _backdrop);
            if (_floor) _floor.SetActive(_backdrop == null);
        }

        /// <summary>
        /// The game's showroom set (FRONTEND\PLATFORMS\*.BIN: BACKDROP_* solids with their own textures) around the
        /// turntable; the plain floor disc stays as the fallback.
        /// </summary>
        void LoadBackdrop(string file)
        {
            try
            {
                GameData.Mount();
                var path = Path.Combine(GameData.Root, "FRONTEND", "PLATFORMS", file);
                if (!File.Exists(path)) return;
                var d = GameFiles.ReadBundle(path);
                var lib = new TextureLibrary();
                lib.Add(TexturePack.ReadAll(d, 0, d.Length));
                var factory = new MeshFactory(lib);
                _backdrop = new GameObject("Backdrop " + file);
                _backdrop.transform.SetParent(transform, false);
                _backdrop.transform.position = Origin;
                foreach (var s in SolidList.ReadAll(d, 0, d.Length))
                {
                    // FEGeometryModels::Init("BACKDROP"); PC renders the current-gen set, so the NEXTGEN-only models are skipped
                    if (!s.Name.StartsWith("BACKDROP_") || s.Name.Contains("NEXTGEN")) continue;
                    var built = factory.Build(s, PartKind.World, p => s.Name.Contains("SHADOW") ? 3 : 0);
                    if (built == null) continue;
                    var go = new GameObject(s.Name);
                    go.transform.SetParent(_backdrop.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = built.Mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = built.Materials;
                }
                SetLayer(_backdrop, ShowroomLayer);
                if (_backdrop.transform.childCount == 0) { if (Application.isPlaying) Destroy(_backdrop); else DestroyImmediate(_backdrop); _backdrop = null; }
                // the set's steam/fog/leaf emitters (chunk 0x3BC00, section 0xA28: always on while the set shows)
                if (_backdrop && Application.isPlaying) Effects.WorldEffects.Add(EmitterReader.ReadAll(d, 0, d.Length), _backdrop.transform, ShowroomLayer);
                Debug.Log($"NFSMW showroom: {file} backdrop {(_backdrop ? _backdrop.transform.childCount : 0)} solids");
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW showroom backdrop: " + e.Message); }
        }

        Light MakeLight(string name, Vector3 euler, float intensity, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(euler);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.color = color;
            l.cullingMask = 1 << ShowroomLayer;
            return l;
        }

        static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayer(t.gameObject, layer);
        }

        public Camera Camera { get { EnsureInit(); return _cam; } }
        /// <summary>Canvas drawn by the showroom camera behind the car (FE background layers).</summary>
        public Transform BackCanvas { get { EnsureInit(); return _back ? _back.transform : null; } }
        Canvas _back;

        public void SetActive(bool on)
        {
            EnsureInit();
            if (!on) CarRender.CarEnvironment.SetGarage(null);   // back in game: ShaperLightsCarsInGame
            if (_cam) _cam.enabled = on;
            if (_key) _key.enabled = on;
            if (_rim) _rim.enabled = on;
            if (_turntable) _turntable.gameObject.SetActive(on);
            _on = on;
            if (_backdrop) _backdrop.SetActive(on);
        }

        /// <summary>
        /// CarViewer::CancelCarLoad / an empty ride (no car selected): the garage set keeps rendering, only the car goes
        /// (GarageMainScreen draws the set behind every FE package; the old SetActive(false) dropped the whole garage).
        /// </summary>
        public void HideCar()
        {
            EnsureInit();
            if (_car) { if (Application.isPlaying) Destroy(_car); else DestroyImmediate(_car); }
            if (_mirror) { if (Application.isPlaying) Destroy(_mirror); else DestroyImmediate(_mirror); }
            _car = null; _mirror = null; _carName = null;
        }

        /// <summary>Shows the car (CARS folder name) with the given paint; rebuilt only when the car changes.</summary>
        public void Show(string car, Color paint, Game.OwnedCar look = null)
        {
            EnsureInit();
            string key = car + "|" + LookKey(look);
            if (_car != null && _carName == key)
            {
                // paint / vinyl / tint / rim paint / decals change the materials in place (CarRenderInfo.Restyle);
                // a decal on a mount that was not built needs a rebuild [car_render.md]
                if (CarRender.CarRenderInfo.Restyle(_car, look, paint))
                {
                    if (_paint != paint) Repaint(paint);
                    return;
                }
            }
            if (_car) { if (Application.isPlaying) Destroy(_car); else DestroyImmediate(_car); }
            if (_mirror) { if (Application.isPlaying) Destroy(_mirror); else DestroyImmediate(_mirror); }
            _carName = key;
            try
            {
                var built = CarBuilder.Build(car.ToUpperInvariant(), paint, _turntable, look);
                SteerFrontWheels(built);
                _car = built.Root;
                _car.transform.localPosition = Vector3.zero;
                // the reflection pass draws the car again mirrored in the floor; the wet floor blends over it with the
                // set's own BACKDROP_REFLECTION room (no shadow in that pass)
                _mirror = Instantiate(_car, _turntable, false);
                _mirror.name = "Reflection";
                _mirror.transform.localScale = new Vector3(1, -1, 1);
                foreach (var sh in _mirror.GetComponentsInChildren<CarShadow>(true)) Destroy(sh.gameObject);
                SetLayer(_car, ShowroomLayer);
                SetLayer(_mirror, ShowroomLayer);
                _paint = paint;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"NFSMW showroom: {car}: {e.Message}");
                _car = null;
            }
        }

        /// <summary>The visual parts of a car (rebuild only when these change).</summary>
        public static string LookKey(Game.OwnedCar l) => l == null ? "" :
            $"{l.Kit}/{l.Hood}/{l.Spoiler}{(l.SpoilerCF ? "c" : "")}/{l.Roof}{l.RoofType}{(l.RoofCF ? "c" : "")}/{l.RimBrand}{l.RimStyle}_{l.RimSize}/{l.Vinyl}";

        public void Repaint(Color paint)
        {
            _paint = paint;
            if (!_car) return;
            foreach (var r in System.Linq.Enumerable.Concat(_car.GetComponentsInChildren<Renderer>(), _mirror ? _mirror.GetComponentsInChildren<Renderer>() : new Renderer[0]))
                foreach (var m in r.sharedMaterials)
                    if (m && m.HasProperty("_Paint") && m.GetFloat("_Paint") > 0.5f) m.SetColor("_Color", paint);
        }

        /// <summary>
        /// One FE_ATTRIB camera angle (frontend class, Generated/AttribSys/Classes/frontend.h): cam_orbit_horizontal
        /// (yaw), cam_orbit_vertical (polar angle, 90 = level), cam_orbit_radius, cam_roll_angle, cam_fov,
        /// cam_lookat_x/y/z (car space, game X forward, Y left, Z up), cam_anim_speed (move time), cam_damping and
        /// cam_periods (the overshoot of SelectCarCameraMover::Update 0x4769e0).
        /// </summary>
        public struct Angle
        {
            public float Yaw, Vertical, Distance, Roll, Fov, Time, Damping;
            public int Periods;
            public Vector3 LookAt;   // Unity car space

            public static bool Load(string name, out Angle a) => From(GameData.Attributes?.Named(name), out a);

            public static bool From(AttribCollection c, out Angle a)
            {
                a = default;
                if (c == null) return false;
                a.Yaw = c.FloatKey(0x803FA14C);
                a.Vertical = c.FloatKey(0xC8F96E5A, 95f);
                a.Distance = c.FloatKey(0xA10C312C, 6f);
                a.Roll = c.FloatKey(0x05B56526);
                a.Fov = c.FloatKey(0xC220D5AB, 55f);
                a.Time = c.FloatKey(0xF624544D, 2f);
                // SelectCarCameraMover ctor (0x476870) defaults: damping 10, periods 2
                a.Damping = c.Float("cam_damping", 0, 10f);
                a.Periods = c.Int("cam_periods", 0, 2);
                float lx = c.FloatKey(0xA8D9A1F4), ly = c.FloatKey(0xFE9B5900), lz = c.FloatKey(0x7C9B89D3);
                a.LookAt = new Vector3(-ly, lz, lx);
                return true;
            }

            /// <summary>A screen's camera: its FE_ATTRIB collection (e.g. career_mainmenu_sub) points at an angle via cam_angle.</summary>
            public static bool ForScreen(string key, out Angle a)
            {
                a = default;
                var db = GameData.Attributes;
                var screen = db?.Named(key);
                if (screen == null || !screen.TryGetKey(0x28B5440B, out int at, out _, out _)) return false;
                uint target = Chunks.U32(screen.Source.Data, at);
                foreach (var kv in db.Collections) if (kv.Key.key == target) return From(kv.Value, out a);
                return false;
            }

            public static Angle Lerp(Angle x, Angle y, float t) => new Angle
            {
                Yaw = x.Yaw + Mathf.DeltaAngle(x.Yaw, y.Yaw) * t,   // shortest arc (FindBestAngleGoal)
                Vertical = Mathf.Lerp(x.Vertical, y.Vertical, t),
                Distance = Mathf.Lerp(x.Distance, y.Distance, t),
                Roll = Mathf.Lerp(x.Roll, y.Roll, t),
                Fov = Mathf.Lerp(x.Fov, y.Fov, t),
                LookAt = Vector3.Lerp(x.LookAt, y.LookAt, t),
                Time = y.Time,
            };
        }

        string _garage, _screenKey;
        Angle _now;
        bool _hasAngle;

        // ---- GarageMainScreen camera control (FEPkg_GarageMain.cpp; PC HandleTick 0x7B9EA0, HandleJoyEvents 0x7A2310,
        //      __handlemouseinput 0x7A2010, NotificationMessage 0x7BA130) over SelectCarCameraMover (0x476870-0x47723D) ----
        // The mover itself (modes, spring, user-rotation clamps, the 0.555 s re-aim) is the camera owner's 1:1 port
        // Runtime/Camera/SelectCarCamera.cs (camera_core.md §13); this screen only drives it as GarageMainScreen does.
        const int TicksBeforeReturn = 300;    // sNumTicksBeforeCamMovesBackToScreenPosition (.data 0x905E24)
        const float CarRotateSpeed = 0.5f;    // CarRotateSpeed (.data 0x905E28): 0.5° per tick = 30°/s
        const float MouseZoomScale = -0.016f; // flt_8B7240: SetZoomSpeed(wheel delta × −0.016)
        readonly SelectCarCamera _mover = new SelectCarCamera();
        float _ticksSinceUser;                 // sNumTicksSinceUserMovedCamera (ticks)
        bool _autoMovement, _pass1, _userRotate;
        float _orbitHIn, _orbitVIn, _zoomIn;   // mOrbitH / mOrbitV / mZoom (the joystick's last values)
        float _zoomInJoy, _zoomOutJoy;         // HandleJoyEvents' static zoomIn / zoomOut

        void SetHRotateSpeed(float v) => _mover.SetHRotateSpeed(v);
        void SetVRotateSpeed(float v) => _mover.SetVRotateSpeed(v);
        void SetZoomSpeed(float v) => _mover.SetZoomSpeed(v);

        // Angle (Unity car space look-at) ↔ the mover's orientation (game car space: x forward, y left, z up)
        static Vector3 GameLook(Vector3 u) => new Vector3(u.z, -u.x, u.y);
        static Vector3 UnityLook(Vector3 g) => new Vector3(-g.y, g.z, g.x);

        void SetCurrent(Angle a) => _mover.SetCurrentOrientation(new Vector3(a.Vertical, a.Yaw, a.Distance), a.Roll, a.Fov, GameLook(a.LookAt));

        Angle CurrentAngle()
        {
            var c = _mover.Current;
            return new Angle { Yaw = c.OrbitH, Vertical = c.OrbitV, Distance = c.Radius, Roll = c.Roll, Fov = c.Fov, LookAt = UnityLook(c.LookAt), Time = _goal.Time, Damping = _goal.Damping, Periods = _goal.Periods };
        }

        /// <summary>
        /// HandleJoyEvents (0x7A2310) — the FRONTENDACTION queue while the screen allows it (cam_user_rotate or CarGuysCamera):
        /// 0x1D / 0x1E vertical ±, 0x1F / 0x20 horizontal ±, 0x2B / 0x2C zoom out / in (the larger of the two); each restarts
        /// the 300-tick counter; a user action during the automatic turn stops it (an H action replaces its speed). Port: the
        /// pad's right stick and triggers stand for those actions [?: the PC action-map binding is the input owner's].
        /// </summary>
        void HandleJoyEvents()
        {
            if (!(_userRotate || CarGuysCamera)) return;
            var pad = UnityEngine.InputSystem.Gamepad.current;
            if (pad == null) return;
            var st = pad.rightStick.ReadValue();
            bool any = false, hAction = false;
            if (Mathf.Abs(st.y - _orbitVIn) > 1e-4f) { _orbitVIn = st.y; SetVRotateSpeed(st.y); any = true; }
            if (Mathf.Abs(st.x - _orbitHIn) > 1e-4f) { _orbitHIn = st.x; SetHRotateSpeed(st.x); any = true; hAction = true; }
            float zo = pad.rightTrigger.ReadValue(), zi = -pad.leftTrigger.ReadValue();
            if (zo != _zoomOutJoy || zi != _zoomInJoy)
            {
                _zoomOutJoy = zo; _zoomInJoy = zi;
                if (Mathf.Abs(zi) > Mathf.Abs(zo)) _zoomIn = zi;
                else if (Mathf.Abs(zo) > Mathf.Abs(zi)) _zoomIn = zo;
                else if (zo == 0 && zi == 0) _zoomIn = 0;
                SetZoomSpeed(_zoomIn);
                any = true;
            }
            if (!any) return;
            _ticksSinceUser = TicksBeforeReturn;
            if (_autoMovement && !hAction) SetHRotateSpeed(0f);
            _autoMovement = false;
        }

        /// <summary>
        /// __handlemouseinput (0x7A2010, each SCREEN_TICK before HandleTick, only with cam_user_rotate or CarGuysCamera):
        /// SetZoomSpeed(wheel × −0.016) and the counter back to 300 on every tick — so on PC the counter never runs out on a
        /// user-rotate screen and HandleTick's automatic turn / return never happens there; with the left button held
        /// (FEMouseInfo bit 0, byte 0x987F70) SetHRotateSpeed(mouse dx) and SetVRotateSpeed(mouse dy), pixels per tick.
        /// </summary>
        void HandleMouseInput()
        {
            if (!(_userRotate || CarGuysCamera)) return;
            var m = UnityEngine.InputSystem.Mouse.current;
            float wheel = m != null ? m.scroll.ReadValue().y : 0f;   // wheel delta (120 per notch, as DirectInput)
            SetZoomSpeed(wheel * MouseZoomScale);
            _ticksSinceUser = TicksBeforeReturn;
            if (m != null && m.leftButton.isPressed)
            {
                var d = m.delta.ReadValue();
                SetHRotateSpeed(d.x);
                SetVRotateSpeed(-d.y);   // DirectInput y grows downwards
            }
        }

        /// <summary>HandleTick's camera part (0x7B9F28-0x7BA0CC): the counter, the automatic turn and the return to the screen angle.</summary>
        void HandleTickCamera(float dt)
        {
            HandleMouseInput();
            HandleJoyEvents();
            if (_orbitVIn == 0 && _orbitHIn == 0 && _zoomIn == 0 && _ticksSinceUser > 0 && !CarGuysCamera)
                _ticksSinceUser = Mathf.Max(0, _ticksSinceUser - dt * 60f);
            bool timeToRotate = _ticksSinceUser <= 0 && _userRotate && !_autoMovement;
            if (timeToRotate && _pass1)
            {
                SetHRotateSpeed(CarRotateSpeed);
                _pass1 = false;
                _autoMovement = true;
                return;
            }
            if (timeToRotate)
            {
                // same screen, counter out: back to the screen angle (SetDesiredOrientation), bPass1 → the turn follows
                _ticksSinceUser = _goal.Time * 60f;
                _pass1 = true;
                Desire(_goal);
            }
        }

        Angle _goal;   // the screen's angle (HandleTick's camera attributes)

        /// <summary>SetDesiredOrientation (0x4770B0): mode 0 from the current orientation; yaw / polar angle / roll by FindBestAngleGoal.</summary>
        void Desire(Angle goal) =>
            _mover.SetDesiredOrientation(new Vector3(goal.Vertical, goal.Yaw, goal.Distance), goal.Roll, goal.Fov, GameLook(goal.LookAt), goal.Time, goal.Damping, goal.Periods);

        /// <summary>CarGuysCamera (0x926134): free orbit regardless of the screen's cam_user_rotate, no clamps (debug; 0 on PC);
        /// the mover's own switch (SelectCarCamera.CarGuysCamera) follows it.</summary>
        public static bool CarGuysCamera { get => SelectCarCamera.CarGuysCamera; set => SelectCarCamera.CarGuysCamera = value; }
        // yaw 0 looks at the car's nose (the shop's −6° opens on the front, as in the game)
        public static float YawSign = 1f, YawOffset = 180f;

        /// <summary>
        /// FEPkg_GarageMain: entering a garage snaps to angle_entry_GARAGE (UpdateCurrentCameraView 0x7A94C0) and moves on;
        /// each screen then moves the camera from where it is to its own angle (FE_ATTRIB screen collection → cam_angle),
        /// falling back to the garage's final angle. HandleTick (0x7BA028): a new screen key sets the counter to
        /// cam_anim_speed · 60 ticks and clears bPass1 / bAutoMovement; screen.cam_user_rotate allows the orbit.
        /// </summary>
        public void SetAngle(string garage, string screenKey = null)
        {
            EnsureInit();
            if (garage == _garage && screenKey == _screenKey) return;
            bool newGarage = garage != _garage;
            _garage = garage;
            _screenKey = screenKey;
            if (garage == null || !Angle.Load("angle_final_" + garage, out var final)) { _hasAngle = false; return; }
            if (screenKey == null || !Angle.ForScreen(screenKey, out _goal)) _goal = final;
            if (newGarage || !_hasAngle) SetCurrent(Angle.Load("angle_entry_" + garage, out var entry) ? entry : _goal);
            _hasAngle = true;
            _angle = 0;
            Desire(_goal);
            var screen = screenKey != null ? GameData.Attributes?.Named(screenKey) : null;
            _userRotate = screen != null && screen.Has("cam_user_rotate") && screen.Int("cam_user_rotate") != 0;
            _autoMovement = false; _pass1 = false;
            _ticksSinceUser = _goal.Time * 60f;
        }

        // ---- per-garage placement (GarageMainScreen::GetCarRotationX/Y/Z 0x7A20D0 / 0x7A2100 / 0x7A2130, GetGeometryZAngle
        //      0x7A2160, GetGeometryX/Y/ZPos 0x7A2190-0x7A21E0; UpdateRenderingCarParameters 0x7A96C0, HandleRender 0x7A2200) ----
        // eGarageType: 1 main FE (PLATFORMCRIB.BIN), 2 career safehouse, 3 customization shop, 4 backroom, 5 car lot
        // (GetGarageNameFromType 0x516E40). Car body: Rz(CarRotationZ) · Rx(CarRotationX) · Ry(CarRotationY) — car lot
        // 340° / −0.379623° / −0.000193°, every other garage (backroom included: the default branch) 304.96979° / 0 / 0.
        // Set (BACKDROP solids): Rz(GeometryZAngle) at (X, Y, Z) — safehouse 302.85309°, shop / car lot 0°, main FE /
        // backroom 134.4125°; position 0 except car lot Y 0.075. Front wheels steered CarSelectTireSteerAngle 21.6723°
        // (.data 0x905E20). Port: the camera was calibrated against a car at its identity rotation in a 304.97° garage, so
        // car and set are turned by their angle minus 304.96979° (a game Z rotation θ is a Unity yaw −θ); in the four
        // 304.97° garages the car stays as before and only the set turns.
        const float CarRotationRef = 304.96979f;

        /// <summary>The eGarageType of a FRONTEND\PLATFORMS file (GetGarageNameFromType reversed).</summary>
        public static int GarageTypeOf(string file)
        {
            switch ((file ?? "").ToUpperInvariant())
            {
                case "CAREER_SAFEHOUSE.BIN": return 2;
                case "CUSTOMIZATION_SHOP.BIN": return 3;
                case "CUSTOMIZATION_SHOP_BACKROOM.BIN": return 4;
                case "CAR_LOT.BIN": return 5;
                default: return 1;   // PLATFORMCRIB.BIN / QRACE
            }
        }

        public static float CarRotationX(int g) => g == 5 ? -0.379623f : 0f;
        public static float CarRotationY(int g) => g == 5 ? -0.000193f : 0f;
        public static float CarRotationZ(int g) => g == 5 ? 340f : 304.96979f;
        public static float GeometryZAngle(int g) => g == 2 ? 302.85309f : g == 3 || g == 5 ? 0f : 134.4125f;
        public static Vector3 GeometryPos(int g) => new Vector3(0f, g == 5 ? 0.075f : 0f, 0f);   // game X, Y, Z
        public const float CarSelectTireSteerAngle = 21.6723f;

        int _garageType = 1;

        /// <summary>A game-frame rotation (degrees about game X forward, Y left, Z up) as a Unity rotation.</summary>
        static Quaternion GameRot(float zDeg, float xDeg, float yDeg) =>
            Quaternion.AngleAxis(-zDeg, Vector3.up) * Quaternion.AngleAxis(-xDeg, Vector3.forward) * Quaternion.AngleAxis(yDeg, Vector3.right);

        void ApplyGarageTransforms()
        {
            int g = _garageType;
            if (_turntable) _turntable.rotation = GameRot(CarRotationZ(g) - CarRotationRef, CarRotationX(g), CarRotationY(g));
            if (_backdrop)
            {
                var rot = Quaternion.AngleAxis(-(GeometryZAngle(g) - CarRotationRef), Vector3.up);
                var gp = GeometryPos(g);
                _backdrop.transform.position = Origin + Quaternion.AngleAxis(CarRotationRef, Vector3.up) * new Vector3(-gp.y, gp.z, gp.x);
                _backdrop.transform.rotation = rot;
            }
        }

        /// <summary>UpdateRenderingCarParameters: the two front tyres (and brakes) turned by CarSelectTireSteerAngle about Z.</summary>
        static void SteerFrontWheels(CarBuilder.Result built)
        {
            if (built?.Wheels == null) return;
            for (int i = 0; i < 2 && i < built.Wheels.Length; i++)
                if (built.Wheels[i]) built.Wheels[i].localRotation = Quaternion.AngleAxis(-CarSelectTireSteerAngle, Vector3.up) * built.Wheels[i].localRotation;
        }

        /// <summary>Jumps to the end of the current camera move (previews).</summary>
        public void FinishMove() { if (_mover.Mode == 0) _mover.Time = _mover.Total; _mover.Update(0f); }

        void LateUpdate()
        {
            if (!_cam || !_cam.enabled) return;
            if (!_hasAngle)
            {
                if (AutoRotate) _angle += Time.unscaledDeltaTime * 12f;
                _turntable.rotation = Quaternion.Euler(0, _angle, 0);
                _cam.ResetProjectionMatrix();
                _cam.fieldOfView = 34f;
                _cam.transform.position = Origin + new Vector3(-1.2f, 1.55f, -7.2f);
                _cam.transform.LookAt(Origin + new Vector3(-1.2f, 0.55f, 0));
                return;
            }
            ApplyGarageTransforms();
            float dt = Time.unscaledDeltaTime;
            HandleTickCamera(dt);
            _mover.Update(dt);   // SelectCarCameraMover::Update (0x4769E0)
            _now = CurrentAngle();
            // CreateCameraMatrix (0x46b7e0): the eye orbits the car's origin at the radius (yaw about the vertical, the
            // polar angle above level for values over 90), looks at the look-at point, rolled about the view axis
            var target = Origin + _now.LookAt;
            float elev = Mathf.Clamp(_now.Vertical - 90f, -85f, 85f);
            var dir = Quaternion.Euler(elev, YawSign * _now.Yaw + YawOffset, 0) * Vector3.back;
            var pos = Origin + dir * Mathf.Max(1.5f, _now.Distance);
            pos.y = Mathf.Max(pos.y, Origin.y + 0.25f);
            _cam.transform.position = pos;
            _cam.transform.LookAt(target);
            _cam.transform.Rotate(0, 0, _now.Roll, Space.Self);
            // the FOV is horizontal; a front-end view has no 1.15 player-view stretch (projection 0x6cf52b)
            ChaseCamera.ApplyProjection(_cam, _now.Fov, 1f);
        }
    }
}
