using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Lua;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// A player view's camera (specs/camera_core.md, specs/camera.md). It hosts the view's
    /// <see cref="CameraDirector"/> (CameraAI::Director 0x479900) and runs the action it picks:
    /// <list type="bullet">
    /// <item>Drive: CDActionDrive::Update (0x472740) fills the camera anchor from the car (speedbreaker zoom, cinematic
    /// slowdown, collision timer, slip, roadblock look-ahead, pull-back POV) and CubicCameraMover::Update (0x47d3c0)
    /// flies eye/look/up/FOV/heading Hermite channels to the camerainfo targets (LAG, HEIGHT, LATOFFSET, ANGLE, FOV;
    /// [0] full screen, [1] split screen) with the speed tables at 0x8EE040.</item>
    /// <item>TrackCar (jump / roadblock / totaled shots) and TrackCop (pursuit start): <see cref="TrackCarCamera"/>,
    /// <see cref="TrackCopCamera"/>.</item>
    /// <item>Ice: <see cref="NisPlayer"/> disables this component while a NIS controls the camera.</item>
    /// </list>
    /// C cycles bumper → hood → close → far (CAMERAACTION_CHANGE), B looks back (held), V toggles the pursuit pull-back
    /// POV. The view's Camera SimTimeMultiplier (cinematic slow motion) scales the game clock.
    /// </summary>
    public sealed class ChaseCamera : MonoBehaviour
    {
        public Transform Target;
        /// <summary>Camera mode (POVTypes): 0 bumper, 1 hood, 2 close (the default), 3 far, 4 superfar, 5 drift, 6 pursuit.</summary>
        public int Mode = 2;
        public bool SplitScreen;
        /// <summary>Both player views are live (DAT_00982B3C == 3): set explicitly or found from two enabled player cameras.</summary>
        public bool Split => SplitScreen || _enabledCount > 1;
        static int _enabledCount;
        /// <summary>CAMERAACTION_PULLBACK (input 0x18): while set the drive camera uses POV 6 (pursuit).</summary>
        public bool PullBack;

        /// <summary>The view's camera director (CameraAI::Director).</summary>
        public CameraDirector Director { get; } = new CameraDirector();
        /// <summary>Camera::CurrentKey.SimTimeMultiplier (+0xCC): drive slowdown 0.25..1, TrackCar 0.2; multiplies Time.timeScale.</summary>
        public float SimTimeMultiplier { get; private set; } = 1f;
        /// <summary>The camera's look-at point (Camera::CurrentKey.Target) and horizontal FOV.</summary>
        public Vector3 LookPoint { get; private set; }
        public float FovH { get; private set; } = 78f;
        /// <summary>The POV the drive camera is using this frame (Mode, or 6 while pulled back).</summary>
        public int Pov => _pov;

        static readonly string[] ModeNames = { "bumper", "hood", "close", "far", "superfar", "drift", "pursuit" };

        /// <summary>
        /// TABLE[mode] (0x8EE040 → {2 entries, 0..1, data}): 44 floats at 0 m/s and at 71 m/s. 0 eye / 1 look / 2 FOV / 3 up
        /// durations; 4-6 up tilt k, 8-10 / 12-14 up clamp; 16-18 eye accel k, 20-22 / 24-26 eye clamp; 28-30 look accel k,
        /// 32-34 / 36-38 look clamp; 40-42 heading durations.
        /// </summary>
        static readonly float[][,] Table =
        {
            new float[,] { { 0, 0, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                           { 0, 0, 0.5f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } },
            new float[,] { { 0.7f, 0.7f, 1f, 0.7f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -0.01f, -0.02f, 0.1f, 0, -0.1f, -0.1f, -0.1f, 0, 0.3f, 0.1f, 0.1f, 0, -0.1f, -0.02f, 0, 0, -0.05f, -0.1f, 0, 0, 0.2f, 0.1f, 0, 0, 0.4f, 0.4f, 0.4f, 0 },
                           { 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -0.1f, -0.1f, -0.1f, 0, 0.3f, 0.1f, 0.1f, 0, 0, 0, 0, 0, -0.05f, -0.1f, 0, 0, 0.2f, 0.1f, 0, 0, 0.2f, 0.2f, 0.2f, 0 } },
            new float[,] { { 1.25f, 0.4f, 1f, 0.8f, 0.004f, 0.004f, 0, 0, -1f, -1f, 1f, 0, 1f, 1f, 1f, 0, -1f, 0, 0, 0, -1.25f, -1f, -1f, 0, 1.5f, 1f, 1f, 0, -0.5f, 0, 0, 0, -2f, 0, -1f, 0, 2f, 0, 1f, 0, 0.4f, 0.4f, 0.4f, 0 },
                           { 0.75f, 0.3f, 0.5f, 0.4f, 0.002f, 0.002f, 0, 0, -1f, -1f, 1f, 0, 1f, 1f, 1f, 0, -0.75f, 0, 0, 0, -1f, -1f, -1f, 0, 1f, 1f, 1f, 0, -0.2f, 0, 0, 0, -0.5f, 0, -1f, 0, 0.5f, 0, 1f, 0, 0.2f, 0.2f, 0.2f, 0 } },
            new float[,] { { 1.25f, 0.4f, 1f, 0.8f, 0.005f, 0.005f, 0, 0, -1f, -1f, 1f, 0, 1f, 1f, 1f, 0, -1f, 0, 0, 0, -1f, -1f, -1f, 0, 2f, 1f, 1f, 0, -0.1f, 0, 0, 0, -2f, 0, -1f, 0, 2f, 0, 1f, 0, 0.4f, 0.4f, 0.4f, 0 },
                           { 0.75f, 0.3f, 0.5f, 0.4f, 0.003f, 0.003f, 0, 0, -1f, -1f, 1f, 0, 1f, 1f, 1f, 0, -0.5f, 0, 0, 0, -1f, -1f, -1f, 0, 2f, 1f, 1f, 0, -0.05f, 0, 0, 0, -0.5f, 0, -1f, 0, 0.5f, 0, 1f, 0, 0.2f, 0.2f, 0.2f, 0 } },
            // modes 4-6 (superfar, drift, pursuit) share one table
            new float[,] { { 1.5f, 0.5f, 1f, 1f, 0.006f, 0.006f, 0, 0, -1f, -1f, 1f, 0, 1f, 1f, 1f, 0, -0.25f, 0, 0, 0, -2.5f, -1f, -1f, 0, 2f, 1f, 1f, 0, -0.1f, 0, 0, 0, -2f, 0, -1f, 0, 2f, 0, 1f, 0, 0.5f, 0.5f, 0.5f, 0 },
                           { 0.5f, 0.2f, 0.5f, 0.5f, 0.002f, 0.002f, 0, 0, -1f, -1f, 1f, 0, 1f, 1f, 1f, 0, -0.05f, 0, 0, 0, -0.5f, -1f, -1f, 0, 0.5f, 1f, 1f, 0, -0.02f, 0, 0, 0, -0.5f, 0, -1f, 0, 0.5f, 0, 1f, 0, 0.1f, 0.1f, 0.1f, 0 } },
        };

        /// <summary>One channel: a cubic from the current value and velocity to the target over a duration (tCubic1D, 0x6513c0).</summary>
        sealed class Cubic
        {
            public float P, V, Target, T = 1, Duration;
            float _c0, _c1, _c2, _c3;
            bool _retarget;

            public void Set(float target) { Target = target; if (target != P) _retarget = true; }
            public void Snap(float value) { P = Target = value; V = 0; T = 1; _retarget = false; }
            /// <summary>Keeps the current value (camera blend) and plans towards the next target.</summary>
            public void Hold(float value) { P = value; V = 0; T = 1; _retarget = true; }

            public void Update(float dt)
            {
                if (_retarget)
                {
                    _retarget = false;
                    T = 0;
                    float d = Target - P;
                    _c3 = V - 2 * d; _c2 = 3 * d - 2 * V; _c1 = V; _c0 = P;
                }
                if (T >= 1) return;
                T = Duration <= 1e-5f ? 1 : T + dt / Duration;
                if (T >= 1) { T = 1; P = Target; V = 0; return; }
                P = ((_c3 * T + _c2) * T + _c1) * T + _c0;
                V = (3 * _c3 * T + 2 * _c2) * T + _c1;
            }
        }

        sealed class Cubic3
        {
            public readonly Cubic X = new Cubic(), Y = new Cubic(), Z = new Cubic();
            public Vector3 P => new Vector3(X.P, Y.P, Z.P);
            public void Set(Vector3 v) { X.Set(v.x); Y.Set(v.y); Z.Set(v.z); }
            public void Snap(Vector3 v) { X.Snap(v.x); Y.Snap(v.y); Z.Snap(v.z); }
            public void Hold(Vector3 v) { X.Hold(v.x); Y.Hold(v.y); Z.Hold(v.z); }
            public void Update(float dt, Vector3 d) { X.Duration = d.x; Y.Duration = d.y; Z.Duration = d.z; X.Update(dt); Y.Update(dt); Z.Update(dt); }
            public void Update(float dt, float d) => Update(dt, new Vector3(d, d, d));
        }

        // CubicCameraMover state: frame-local channels (x forward, y left, z up), world up and heading
        readonly Cubic3 _eye = new Cubic3(), _look = new Cubic3(), _up = new Cubic3(), _heading = new Cubic3();
        readonly Cubic _fov = new Cubic();
        readonly Queue<Vector3> _acc = new Queue<Vector3>();
        float _prevSpeed, _airTime, _shiftAt = -9, _b4, _pulse, _pulseT;
        int _prevGear, _pov = -1, _savedPov = -1;
        bool _snap = true, _blend;
        Camera _cam;
        static readonly Dictionary<string, (float lag, float height, float lat, float angle, float fov)> Infos = new Dictionary<string, (float, float, float, float, float)>();

        // CDActionDrive state
        float _z1, _slowDur = 2f, _z2;   // +0x6C cinematic slowdown, DAT_008ED454 its ramp time, +0x50 speedbreaker zoom
        bool _slowOn;                    // +0x70
        Vector3 _anchorP, _anchorV, _anchorF = Vector3.forward, _anchorUp = Vector3.up, _anchorRight = Vector3.right;

        // TrackCar / TrackCop movers and the director's event edges
        readonly TrackCarCamera _trackCar = new TrackCarCamera();
        readonly TrackCopCamera _trackCop = new TrackCopCamera();
        bool _wasPursuit, _wasTotaled, _wasAirborne, _wasFinished;
        RearViewMirror _mirror;
        readonly ObstructionRaiser _raiser = new ObstructionRaiser();

        /// <summary>
        /// GRacerInfo +0x22 mCameraDetached (Game.DetachCameraFromRacer 0x611540, used by race_bin_01/epic_pursuit): the
        /// drive camera's anchor stops following the player's car (CDActionDrive::Update 0x47288A returns before the
        /// anchor update). Cleared when the race is torn down [hook: the Lua binding sets it].
        /// </summary>
        public static bool CameraDetachedFromRacer;

        /// <summary>Game.DetachCameraFromRacer (0x611540) [hook for the Lua owner].</summary>
        public static void DetachCameraFromRacer() => CameraDetachedFromRacer = true;

        static ChaseCamera _player1;

        /// <summary>
        /// ICEManager::SetGenericCameraToPlay(group, track) for player view 1 (ECinematicMoment 0x640550: PhotoFinishScreen's
        /// "Cinematics"/PhotoFinishCamera, DefaultFinish, DefaultSpeed; Camera.SetGenericCamera 0x604A50). Returns false when
        /// there is no player camera or no such group.
        /// </summary>
        public static bool PlayGenericCamera(string group, string track)
        {
            var c = _player1;
            if (!c || string.IsNullOrEmpty(group)) return false;
            if (IceShot.Data.Find(3, group) == null) return false;
            c.Director.SetGenericCamera(group, track);
            c._genericStart = -1f;
            return true;
        }

        static readonly List<ChaseCamera> Live = new List<ChaseCamera>();

        /// <summary>
        /// The CameraMessagePort jump: after IVehicle::SetVehicleOnGround (0x685810) places a car (reset, warp, off-world
        /// recovery) every view whose camera follows that car snaps (CubicCameraMover::ResetState) instead of flying
        /// there. Raised by PlayerCar.SetVehicleOnGround.
        /// </summary>
        public static void CarPlaced(PlayerCar car)
        {
            if (!car) return;
            foreach (var c in Live)
                if (c && c.isActiveAndEnabled && c.Target == car.transform) c._snap = true;   // the mover only, not CameraAI::Reset
        }

        /// <summary>gGameBreakerCamera (0x911055) of player view 1: the speedbreaker zoom is up.</summary>
        public static bool GameBreakerZoom => _player1 && _player1.Director.GameBreaker;

        /// <summary>CameraAI::StartCinematicSlowdown(EVIEW_PLAYER1, seconds) (0x471b60; PhotoFinishScreen passes 0.75).</summary>
        public static void StartCinematicSlowdown(float seconds) { if (_player1) _player1.Director.StartCinematicSlowdown(seconds); }

        void Awake() => _cam = GetComponent<Camera>();

        void OnEnable()
        {
            _enabledCount++;
            if (!_player1) _player1 = this;
            if (!Live.Contains(this)) Live.Add(this);
            LuaDrive.CinematicMoment -= OnCinematicMoment;
            LuaDrive.CinematicMoment += OnCinematicMoment;
            SimTimeMultiplier = 1f;
            Director.Reset();
            ResetDrive(false);
        }

        /// <summary>Snap every channel to its target next frame (CubicCameraMover::ResetState 0x46b0c0); also CameraMoverRestartRace.</summary>
        public void Snap()
        {
            _snap = true;
            Director.Reset();
            ResetDrive(false);
        }

        /// <summary>A new CDActionDrive (ctor 0x484c20): timers cleared; inheriting a smooth return starts fully slowed (fades over 2 s).</summary>
        void ResetDrive(bool smooth)
        {
            _z2 = 0;
            _slowOn = false;
            _z1 = smooth ? 1f : 0f;
            _slowDur = 2f;
            _pulseTimer = smooth ? 0.3f : 0f;   // +0x68
        }

        float _pulseTimer;

        /// <summary>
        /// IVisualTreatment::TriggerPulse(length) (0x73DEF0: restarts the treatment's pulse at the real timer, 0 = its own
        /// length). CDActionDrive::Update (0x472B1D) fires TriggerPulse(0.2) when the +0x68 timer (0.3 s at a smooth-return
        /// Drive) drops to 0.05: the white flash 0.25 s after a pursuit / jump / roadblock shot ends.
        /// [hook: the visual-treatment owner assigns its pulse here; null = no pulse]
        /// </summary>
        public static System.Action<float> TriggerPulse;

        /// <summary>camerainfo for a mode, the car's own collection first (e.g. "gti_hood"), then the generic one.</summary>
        (float lag, float height, float lat, float angle, float fov) Info(int mode, string car)
        {
            string generic = ModeNames[Mathf.Clamp(mode, 0, 6)];
            string key = (car ?? "") + "|" + generic + (Split ? "|s" : "");
            if (Infos.TryGetValue(key, out var r)) return r;
            int i = Split ? 1 : 0;
            var db = GameData.Attributes;
            var c = (car != null ? db?.Get("camerainfo", car + "_" + generic) : null) ?? db?.Get("camerainfo", generic);
            r = c != null
                ? (c.Float("LAG", i, -5.4f), c.Float("HEIGHT", i, 1.7f), c.Float("LATOFFSET", i, 0), c.Float("ANGLE", i, 0), c.Float("FOV", i, 78))
                : mode == 2 ? (-4.7f, 1.62f, 0f, 0f, 78f) : mode == 6 ? (-9f, 1.5f, 0f, 0f, 65f) : (-5.436f, 1.7f, 0f, 0f, 90f);
            Infos[key] = r;
            return r;
        }

        static float Lerp(float a, float b, float s) => a + (b - a) * s;
        static bool IsChase(int pov) => pov >= 2 && pov <= 6;

        // graphA (0x8ede54): -30,-15,0,15,30 → -1,-0.1,0,0.1,1
        static float GraphA(float x)
        {
            float ax = Mathf.Abs(x), y = ax <= 15 ? ax / 15f * 0.1f : ax <= 30 ? 0.1f + (ax - 15) / 15f * 0.9f : 1f;
            return Mathf.Sign(x) * y;
        }

        // graphB (0x8ede8c) over [0,1] at .25 steps: the impact lurch
        static readonly float[] GraphB = { 0, .02f, .25f, .99f, 0 };
        // graphC (0x8ede68) over [0,1.5], 9 points: the gear-shift lurch
        static readonly float[] GraphC = { 0, .2f, .13f, -.08f, -.2f, -.13f, -.07f, -.03f, 0 };

        static float Graph(float[] g, float x01)
        {
            float f = Mathf.Clamp01(x01) * (g.Length - 1);
            int k = Mathf.Min(g.Length - 2, (int)f);
            return Lerp(g[k], g[k + 1], f - k);
        }

        void LateUpdate()
        {
            if (!Target) return;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f);
            var car = Target.GetComponent<PlayerCar>();
            bool menu = FrontEndOpen();
            bool human = car && car.UseDeviceInput && !menu;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (human && kb != null)
            {
                // CAMERAACTION_CHANGE (0x5a3850): next selectable POV; switching between two chase POVs blends, otherwise snaps
                if (kb.cKey.wasPressedThisFrame) SetMode(Mode >= 3 ? 0 : Mode + 1);
                if (kb.vKey.wasPressedThisFrame) PullBack = !PullBack;
            }
            bool lookBack = human && kb != null && kb.bKey.isPressed;

            // ---- director events (hooks the gameplay code would post; polled here) ----
            bool cams = CameraOptions.JumpCams && !Split;
            if (car && !Split) PollEvents(car, cams);
            Director.Update(dt, menu, cams, NisPlayer.Active);
            if (Director.Changed) { Director.Ack(); EnterAction(car); }

            Vector3 eye, look;
            float fovH;
            switch (Director.Action)
            {
                case CameraAction.TrackCar:
                    if (!_trackCar.Update(Target, out eye, out look, out fovH, Director.TrackCarTimer)) Director.TrackCarTimer = -1f;
                    SimTimeMultiplier = TrackCarCamera.SimTime;
                    Place(eye, look, Vector3.up, fovH);
                    _trackCar.Dof(dt, eye, Target.position);
                    break;
                case CameraAction.TrackCop:
                    _trackCop.Update(dt, out eye, out look, out fovH);   // keeps the drive camera's slowed clock
                    Place(eye, look, Vector3.up, fovH);
                    _trackCop.Dof(dt, eye, look);
                    break;
                case CameraAction.Ice:
                    if (!Generic(car)) { Director.SetGenericCamera(null, null); Drive(car, dt, lookBack); }
                    break;
                default:
                    Drive(car, dt, lookBack);
                    break;
            }
            ApplyCameraShake(_lastAccel, dt);
            UpdateMirror(car);
            ApplyTime(menu);
        }

        /// <summary>CubicCameraMover::SetPovType (0x46b050): snap unless both the old and the new POV are chase POVs.</summary>
        public void SetMode(int mode)
        {
            if (mode == Mode) return;
            if (!(IsChase(mode) && IsChase(Mode))) _snap = true;
            Mode = mode;
        }

        /// <summary>Gameplay events the exe posts to CameraAI, detected from public state.</summary>
        void PollEvents(PlayerCar car, bool cams)
        {
            // AIPursuit::OnAttached → CameraAI::MaybeDoPursuitCam
            var police = Systems.Police;
            bool pursuit = police != null && police.InPursuit && police.Player == car;
            if (pursuit && !_wasPursuit) Director.MaybeDoPursuitCam(cams, car);
            _wasPursuit = pursuit;
            // EShowRaceOverMessage with GRacerInfo::mTotalled → CameraAI::MaybeDoTotaledCam
            var race = RaceManager.Active;
            bool totaled = car.Totaled && race != null;
            if (totaled && !_wasTotaled) Director.MaybeDoTotaledCam(Split);
            _wasTotaled = totaled;
            // EPlayerAirborne → CameraAI::MaybeDoJumpCam
            bool air = car.WheelsOnGround == 0;
            if (air && !_wasAirborne) Director.MaybeDoJumpCam(cams, car);
            _wasAirborne = air;
            // PhotoFinishScreen ctor → CameraAI::StartCinematicSlowdown(view 1, 0.75): FE_ShowPostRaceScreen (0x60AC20)
            // pushes that screen at every finish the player won, not only close ones [polled: the player finishing first;
            // the screen may call StartCinematicSlowdown itself]
            var pe = race?.PlayerEntrant;
            bool finished = pe != null && pe.Car == car && pe.Finished && pe.Place == 1;
            if (finished && !_wasFinished) Director.StartCinematicSlowdown(0.75f);
            _wasFinished = finished;
            // the race is torn down: CameraMoverRestartRace (0x480ce0) → CameraAI::Reset, a fresh drive action
            if (_hadRace && race == null)
            {
                CameraDetachedFromRacer = false;
                Director.Reset();
                ResetDrive(false);
                SimTimeMultiplier = 1f;
            }
            _hadRace = race != null;
        }

        bool _hadRace;

        void EnterAction(PlayerCar car)
        {
            var rb = car ? car.GetComponent<Rigidbody>() : null;
            var vel = rb ? rb.linearVelocity : Vector3.zero;
            if (Director.Action != CameraAction.Drive) VisualTreatment.FlushAccumulationBuffer();   // hard cut to a TrackCar/TrackCop shot
            switch (Director.Action)
            {
                case CameraAction.TrackCar:
                    _trackCar.Init(Target, vel, transform.position, Director.RoadblockShot);
                    Director.RoadblockShot = false;
                    break;
                case CameraAction.TrackCop:
                    if (!_trackCop.Init(transform.position, LookPoint, FovH, Target.position, car) && Director.PursuitTimer < 2f) Director.PursuitTimer = -1f;
                    break;
                case CameraAction.Drive:
                    ResetDrive(Director.EnteredSmooth);
                    // CubicCameraMover ctor (0x481bd0) blendFromPrev: keep the previous camera when it is within 50 m and not reversed
                    _blend = Vector3.Distance(transform.position, Target.position) < 50f;
                    _snap = !_blend;
                    break;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------
        // CDActionDrive::Update (0x472740) + CubicCameraMover::Update (0x47d3c0)

        void Drive(PlayerCar car, float dt, bool lookBack)
        {
            if (_pulseTimer > 0f) _pulseTimer -= dt;
            if (_pulseTimer > 0f && _pulseTimer <= 0.05f) { _pulseTimer = 0f; TriggerPulse?.Invoke(0.2f); }
            // cinematic slowdown (+0x6C), ramp time DAT_008ED454: SetSpecial(t > 0) sets the time and the ramp-up flag
            if (Director.Slowdown > 0) { _slowDur = Director.Slowdown; _slowOn = true; }
            if (!_slowOn) _z1 = _z1 <= 0 ? 0 : _z1 - dt / _slowDur;
            else _z1 = _z1 >= 1f ? 1f : _z1 + dt / _slowDur * 4f;
            _z1 = Mathf.Clamp01(_z1);
            if (_z1 > 0.001f)
            {
                float m = (1f - _z1) * 0.75f + 0.25f;
                SimTimeMultiplier = m > 0.9f ? 1f : m;
            }
            else if (SimTimeMultiplier < 1f && Director.Action == CameraAction.Drive && !Director.EnteredSmooth) SimTimeMultiplier = 1f;
            // speedbreaker zoom (+0x50): player vt+0x40 (game breaker on) → +4/s, else −1/s
            bool breaker = car && car.SpeedBreakerOn;
            _z2 = breaker ? Mathf.Min(1f, _z2 + dt * 4f) : Mathf.Max(0f, _z2 - dt);
            Director.GameBreaker = _z2 > 0.001f;
            // POV: the pull-back input saves the setting and forces POV 6 (DAT_008EE4B4)
            int pov = Mode;
            if (PullBack) { if (_savedPov < 0) _savedPov = Mode; pov = 6; }
            else if (_savedPov >= 0) { pov = Mode; _savedPov = -1; }
            if (pov != _pov)
            {
                if (_pov >= 0 && !(IsChase(pov) && IsChase(_pov))) _snap = true;
                _pov = pov;
            }

            CameraDof.Off();   // the drive camera has no depth of field (CubicCameraMover writes none)
            // ---- anchor (CameraAnchor::Update 0x468fa0) ----
            // CDActionDrive::Update (0x47287E..0x4728B0) returns before the anchor update, so the camera stays where it is
            // and watches the car leave: (a) an outside POV in a race with a time limit whose clock ran out while the
            // racer has not finished (GRaceParameters 0x4684D0, GetRaceTimeRemaining 0x5FE090, GRacerInfo +0x21); (b) the
            // racer's camera detached (GRacerInfo +0x22, Game.DetachCameraFromRacer)
            var rb = Target.GetComponent<Rigidbody>();
            var race = RaceManager.Active;
            var pe = race?.PlayerEntrant;
            bool frozen = race != null && pe != null && pe.Car == car &&
                          ((IsChase(pov) && race.Event != null && race.Event.TimeLimit > 0 && race.TimeLeft <= 0f && !pe.Finished) || CameraDetachedFromRacer);
            if (!frozen || _anchorP == Vector3.zero)
            {
                _anchorV = rb ? rb.linearVelocity : Vector3.zero;
                _anchorP = Target.position;
                _anchorF = Target.forward;
                _anchorUp = Target.up;
                _anchorRight = Target.right;
            }

            var V = _anchorV;
            var P = _anchorP;
            var F = _anchorF;
            float speed = V.magnitude;
            bool chase = IsChase(pov);
            bool grounded = car ? car.WheelsOnGround > 0 : Physics.Raycast(P + Vector3.up * 0.5f, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore);
            _airTime = grounded ? 0 : _airTime + dt;
            var relay = Target.GetComponent<CameraCollisionRelay>();
            if (!relay) relay = Target.gameObject.AddComponent<CameraCollisionRelay>();
            float impact = relay.Timer / 2f;                       // anchor+0xAC mCollisionDamping
            float slip = 0;                                        // anchor+0xB0 mDrift
            if (grounded && speed > 0.1f)
            {
                float ang = Vector3.Angle(Vector3.ProjectOnPlane(F, Vector3.up), Vector3.ProjectOnPlane(V, Vector3.up));
                slip = Mathf.Clamp01(ang / 90f) * Mathf.InverseLerp(10f, 20f, speed);
            }
            UpdateRoadblock(P, F, speed, grounded);
            float zoom = 1f - Mathf.Max(_z1, _z2) * 0.4f;          // anchor+0xC4

            var info = Info(pov, car && car.Spec != null ? car.Spec.Name : null);
            var tab = Table[Mathf.Min(pov, 4)];
            float sf = Mathf.Clamp01(speed / 71f);
            float Prm(int i) => Lerp(tab[0, i], tab[1, i], sf);
            Vector3 Prm3(int i) => new Vector3(Prm(i), Prm(i + 1), Prm(i + 2));

            if (_b4 > 0) _b4 -= dt;   // 1 s after a blended switch: no acceleration response
            // longitudinal acceleration (world), shaped and averaged over 5 frames (0x478b70)
            float accel = (speed - _prevSpeed) / dt;
            _prevSpeed = speed;
            var accW = Mathf.Abs(accel) > 300f ? Vector3.zero : F * accel;
            var shaped = _b4 <= 0 ? new Vector3(30 * GraphA(accW.x), 30 * GraphA(accW.y), 30 * GraphA(accW.z)) : Vector3.zero;
            _acc.Enqueue(shaped);
            while (_acc.Count > 5) _acc.Dequeue();
            var avg = Vector3.zero;
            foreach (var a in _acc) avg += a;
            avg /= _acc.Count;
            // impact pulse arm (mover +0xD0/+0xD8): a horizontal surge ≥ 24 with a fresh collision
            if (Mathf.Max(Mathf.Abs(avg.x), Mathf.Abs(avg.z)) >= 24f && impact > 0 && _pulse < impact) { _pulse = impact; _pulseT = 1f; }

            // ---- reference frame (0x46b1f0 / 0x46b410) ----
            Vector3 X, right, upF;
            if (chase)
            {
                var v = V;
                if (Vector3.Dot(v, F) < 0) v -= 2 * Vector3.Dot(v, F) * F;
                float w = Mathf.InverseLerp(8f, 13f, speed);
                var h = (v.sqrMagnitude > 1e-4f ? v.normalized : F) * w + F * (1 - w);
                h.y *= (1 - F.y * F.y) * Mathf.Clamp01(1 - 0.5f * _airTime);
                if (_snap) _heading.Snap(h);
                else { _heading.Set(h); _heading.Update(dt, new Vector3(Prm(40), Prm(41), Prm(42)) + Vector3.one * (impact + slip)); }
                X = _heading.P.sqrMagnitude > 1e-4f ? _heading.P.normalized : F;
                right = Vector3.Cross(Vector3.up, X).normalized;
                if (right.sqrMagnitude < 1e-4f) right = _anchorRight;
                upF = Vector3.Cross(X, right);
            }
            else
            {
                _heading.Snap(F);
                X = F; right = _anchorRight; upF = _anchorUp;
                if (upF.y < 0) { right = -right; upF = -upF; }   // flipped car: the frame stays upright
            }
            Vector3 World(Vector3 l) => P + X * l.x - right * l.y + upF * l.z;
            Vector3 Local(Vector3 w) { var d = w - P; return new Vector3(Vector3.Dot(d, X), -Vector3.Dot(d, right), Vector3.Dot(d, upF)); }

            // ---- targets (CubicCameraMover::SetDesired 0x47cf00), frame-local ----
            float L = info.lag / zoom, Y = info.lat, H = info.height;
            float top = car && car.Spec != null && car.Spec.TopSpeed > 1 ? car.Spec.TopSpeed : 0f;
            if (top > 0)
            {
                // speed scale (0x8ee0cc): LAG ×1, 1, 1.02, 1.125, 1.2 at 0, .25, .5, .75, 1 of top speed
                float r = Mathf.Clamp01(speed / top) * 4f;
                float[] sx = { 1f, 1f, 1.02f, 1.125f, 1.2f };
                int k = Mathf.Min(3, (int)r);
                L *= Lerp(sx[k], sx[k + 1], r - k);
            }
            float tanA = Mathf.Tan(info.angle * Mathf.Deg2Rad);
            float lookX = chase ? 0 : L + 1, lookZ = chase ? H + L * tanA : H - tanA;
            var aL = new Vector3(Vector3.Dot(avg, X), -Vector3.Dot(avg, right), Vector3.Dot(avg, upF));
            var eyeT = Vector3.Max(Prm3(20), Vector3.Min(Prm3(24), Vector3.Scale(aL, Prm3(16)))) + new Vector3(L, Y, H);
            var lookT = Vector3.Max(Prm3(32), Vector3.Min(Prm3(36), Vector3.Scale(aL, Prm3(28)))) + new Vector3(lookX, 0, lookZ);
            // up: world acceleration tilt with the vertical pinned to 1 (TILTING = 1 in every collection)
            var tilt = Prm3(4);
            var upG = new Vector3(avg.x * tilt.x, 1f, avg.z * tilt.y);
            upG = new Vector3(Mathf.Clamp(upG.x, Prm(8), Prm(12)), 1f, Mathf.Clamp(upG.z, Prm(9), Prm(13)));
            float fovT = info.fov * zoom + (chase && car && car.NosFiring && speed > 0 ? 9f : 0);

            if (_blend && chase)
            {
                // blend from the previous camera: the channels start at its eye/look and fly to the new targets
                _eye.Hold(Local(transform.position));
                _look.Hold(Local(LookPoint));
                _up.Snap(upG.normalized);
                _fov.Hold(FovH);
                _b4 = 1f;
                _snap = false;
            }
            _blend = false;
            if (_snap || !chase)
            {
                if (_snap) VisualTreatment.FlushAccumulationBuffer();
                _eye.Snap(eyeT); _look.Snap(lookT); _up.Snap(upG.normalized); _fov.Snap(fovT);
                _snap = false;
            }
            else
            {
                _eye.Set(eyeT); _look.Set(lookT); _up.Set(upG.normalized); _fov.Set(fovT);
                _up.Update(dt, Prm(3));
                _fov.Duration = Prm(2);
                _fov.Update(dt);
                _eye.Update(dt, Prm(0));
                _look.Update(dt, Prm(1));
            }

            var E = _eye.P; var T = _look.P;
            float sign = lookBack ? -1 : 1;   // SetLookBack: X and Y negated; GetLookbackAngle = 0x8000
            E.x *= sign; E.y *= sign; T.x *= sign; T.y *= sign;
            if (_pulse > 0 && _pulseT > 0)
            {
                float p = Graph(GraphB, 1f - _pulseT) * _pulse * 0.25f;
                E.x *= 1 - p; T.x *= 1 + p;
            }
            if (_pulseT > 0) _pulseT -= dt;
            if (_pulseT <= 0) _pulse = 0;
            // gear-shift lurch (graphC over 1.5 s)
            if (chase && car)
            {
                float ts = Time.unscaledTime - _shiftAt;
                if (ts >= 1.5f) { if (speed > 10f && car.Gear != _prevGear) _shiftAt = Time.unscaledTime; }
                else E.x += (1 - Mathf.Clamp01(speed * 0.01f)) * Mathf.Abs(E.x) * 0.2f * Graph(GraphC, ts / 1.5f);
                _prevGear = car.Gear;
            }
            var eyeW = World(E);
            var lookW = World(T);
            var upW = chase ? _up.P : _anchorUp;

            if (chase)
            {
                eyeW = TerrainClamp(P, eyeW);
                // obstructionRaise (0x47C6F0): out of the dome over every active car / camera-avoidable prop
                eyeW = _raiser.Apply(eyeW, lookW, V, car);
                // avoidLean (0x47C440): the up vector leans with the objects 0.8..7 m ahead
                upW = (upW + AvoidLean(P, V, car)).normalized;
            }
            Place(eyeW, lookW, upW, _fov.P);
            // CubicCameraMover::Update after the look-at (0x47DCCD..0x47DDAA): ApplyCameraShake (here at the end of the
            // frame), HandheldNoise (0x468870: its scale table 0x911060 is BSS nobody writes = 0, dead), ChopperNoise
            // outside tunnels (0x73CFE0), then the speed noise (0x47BEC0) on the final matrix
            float worldT = Time.time;   // WorldTimer seconds
            bool inTunnel = false;
            try { inTunnel = Game.Police.PoliceZones.CameraInTunnel(); } catch (System.Exception) { }
            if (!inTunnel) CameraNoise.Chopper(transform, 1f - 0.8f * Mathf.Min(1f, speed * 1092.2666f), worldT, FovH);
            Shake(speed, Vector3.Dot(accW, F), chase, car, worldT);
            // the IExplosion loop of CDActionDrive::Update (views 1 / 2)
            CameraExplosions.Shake(ViewId == 2 ? 1 : 0, transform.position, dt);
            _lastAccel = accW;
        }

        /// <summary>
        /// avoidLean (0x47C440), the lean when passing a car: for every avoidable object whose centre is 0.8..7 m from
        /// the anchor and ahead of its velocity V (cos &gt; 0.5), add dir · (clamp((|V − v| − 15)/85) + max(0, −v̂·V̂)) ·
        /// clamp(4.84/d²), v = the object's velocity (vt+8): a car passed at 15+ m/s closing speed or met head-on. The sum
        /// is added to the camera's up vector [? param_2 read as the anchor velocity].
        /// </summary>
        static Vector3 AvoidLean(Vector3 p, Vector3 vel, PlayerCar self)
        {
            if (vel.sqrMagnitude < 1e-6f) return Vector3.zero;
            var f = vel.normalized;
            var sum = Vector3.zero;
            foreach (var c in CameraAvoidables.Get())
            {
                if (!c || c == self) continue;
                var d = c.transform.position - p;
                float d2 = d.sqrMagnitude;
                if (d2 <= 0.64000005f || d2 >= 49f) continue;
                var n = d.normalized;
                if (Vector3.Dot(n, f) <= 0.5f) continue;
                float k = Mathf.Clamp01(4.84f / d2);
                var body = c.GetComponent<Rigidbody>();
                var v = body ? body.linearVelocity : Vector3.zero;
                float sp = Mathf.Clamp01((Vector3.Distance(vel, v) - 15f) * 0.011764706f);
                float face = v.sqrMagnitude > 1e-6f ? Mathf.Abs(Mathf.Min(0f, Vector3.Dot(v.normalized, f))) : 0f;
                sum += n * ((sp + face) * k);
            }
            return sum;
        }

        // ---- the generic ICE camera (CDActionIce with ICEManager's generic track; specs/camera_core.md §7.6) ----
        float _genericStart = -1f;
        IceCameras.Track _genericTrack;
        int _genericKey = -1;
        Vector3 _genericCarPos;
        Quaternion _genericCarRot = Quaternion.identity;
        readonly ObstructionRaiser _iceRaiser = new ObstructionRaiser();
        readonly IceCarSpaceLag _iceLag = new IceCarSpaceLag();

        void OnCinematicMoment(string group, string track)
        {
            if (this != _player1) return;
            PlayGenericCamera(group, track);
        }

        /// <summary>
        /// ICEManager::SetUseRealTime: the generic/FMV ICE clock is the RealTimer instead of the WorldTimer (the sim clock,
        /// which the ICE sim-speed keys and the pause stop). [hook: PhotoFinishScreen sets it; its caller is not decompiled]
        /// </summary>
        public static bool IceUseRealTime;

        static float IceTimer => IceUseRealTime ? Time.unscaledTime : Time.time;

        /// <summary>
        /// ICEManager::ChooseGenericCamera + ICETrack::GetCameraData (0x47B030): the named track of the GENERIC group
        /// (InGameB.bun 0x8003B203, e.g. "Cinematics": DefaultFinish, DefaultSpeed, SprintFinish, Challenge...) at
        /// parameter (t - start)/Length capped at 1, t = the WorldTimer (sim time) unless SetUseRealTime. Points: car space =
        /// the player car's frame from its forward and the world up (0x466A60, no roll; bIgnoreOrientation keys keep the
        /// frame captured at the last hard cut with the live position), hybrid = car position + world axes, world; scene
        /// space has no scene here and is read as world [?]. Lens -> horizontal FOV, dutch bank, sim speed ->
        /// SimTimeMultiplier, near clip (>= 0), noise/shake at time Length x param, depth of field, constraints (with the
        /// anchor's velocity). A cut (ICEMover::SetDesired: the keys do not share the point, or a hard entry) flushes the
        /// motion blur. Returns false at parameter 1 (the request is cleared: nPlayGenericGroupHash = bStringHash("")).
        /// </summary>
        bool Generic(PlayerCar car)
        {
            if (!Director.GenericRequested) return false;
            if (_genericStart < 0f || _genericTrack == null)
            {
                var tracks = IceShot.Data.Find(3, Director.GenericGroup);
                _genericTrack = null;
                if (tracks != null)
                    foreach (var t in tracks)
                        if (string.Equals(t.Name, Director.GenericTrack, System.StringComparison.OrdinalIgnoreCase)) { _genericTrack = t; break; }
                if (_genericTrack == null || _genericTrack.Keys.Count == 0 || _genericTrack.Length <= 1e-3f) return false;
                _genericStart = IceTimer;
                _genericKey = -1;
                _iceRaiser.Reset();
                _iceLag.Reset();
            }
            float param = (IceTimer - _genericStart) / _genericTrack.Length;
            if (param >= 1f) { _genericStart = -1f; _genericTrack = null; SimTimeMultiplier = 1f; CameraDof.Off(); return false; }
            var s = IceCameras.Evaluate(_genericTrack, Mathf.Clamp01(param));
            var tgt = Target;
            Quaternion CarFrame()
            {
                if (!tgt) return Quaternion.identity;
                var f = Vector3.ProjectOnPlane(tgt.forward, Vector3.up);
                return Quaternion.LookRotation(f.sqrMagnitude > 1e-6f ? f : Vector3.forward, Vector3.up);
            }
            if (s.KeyIndex != _genericKey)
            {
                var prev = _genericKey >= 0 && _genericKey < _genericTrack.Keys.Count ? _genericTrack.Keys[_genericKey] : null;
                bool hardCut = prev == null ? (s.Key.Smooth & 1) == 0 : !IceCameras.KeysShared(prev, 1, s.Key, 0);
                _genericKey = s.KeyIndex;
                if (hardCut)
                {
                    VisualTreatment.FlushAccumulationBuffer();   // 0x725550
                    if (tgt) { _genericCarPos = tgt.position; _genericCarRot = CarFrame(); }
                }
            }
            Vector3 Map(Vec3 v, IceCameras.Space sp)
            {
                switch (sp)
                {
                    case IceCameras.Space.Car when tgt: return tgt.position + (s.Key.IgnoreOrientation ? _genericCarRot : CarFrame()) * new Vector3(-v.Y, v.Z, v.X);
                    case IceCameras.Space.Hybrid when tgt: return tgt.position + Coords.ToUnity(v.X, v.Y, v.Z);
                    default: return Coords.ToUnity(v.X, v.Y, v.Z);
                }
            }
            var eye = Map(s.Eye, s.EyeSpace);
            var look = Map(s.Look, s.LookSpace);
            var anchor = tgt ? tgt.position : look;
            var anchorVel = tgt && tgt.TryGetComponent<Rigidbody>(out var arb) ? arb.linearVelocity : Vector3.zero;
            // ICEAnchor::Update every frame, then the key's bCarSpaceLag offset on eye and look (0x4834B5)
            if (tgt) _iceLag.Anchor(tgt.position, tgt.forward, anchorVel, Mathf.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f));
            if (s.Key.CarSpaceLag)
            {
                var lag = _iceLag.Offset(Mathf.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f) * s.SimSpeed);
                eye -= lag;
                look -= lag;
            }
            if (s.Key.ConstrainToWorld) eye = TerrainClamp(anchor, eye);
            if (s.Key.ConstrainToCars) eye = _iceRaiser.Apply(eye, look, anchorVel, car);
            Place(eye, look, Vector3.up, s.FovDeg);
            if (s.DutchDeg != 0f) transform.Rotate(0f, 0f, s.DutchDeg, Space.Self);   // ComputeBankedUpVector (0x468960) [sign ?]
            IceShot.ApplyJitter(transform, s, _genericTrack.Length * Mathf.Clamp01(param));   // noise time = track.Length x param
            IceShot.ApplyDof(s.FovDeg, s, eye, look);
            if (_cam && s.NearClip >= 0f) _cam.nearClipPlane = Mathf.Max(0.01f, s.NearClip);
            SimTimeMultiplier = s.SimSpeed;
            return true;
        }

        /// <summary>
        /// The view id of this camera (EVIEW: 1 = player 1, 2 = player 2) for the camera shakers (ApplyCameraShake view 1/3 ->
        /// player 0, view 2 -> player 1).
        /// </summary>
        int ViewId => Split && this != _player1 ? 2 : 1;

        /// <summary>
        /// CameraMover::SetCameraMatrix -> ApplyCameraShake (0x64C420; Misc/Rumble.cpp, ported in Runtime/Input/Rumble.cs
        /// CameraShaker): the player's crash shake (started by CDActionDrive::Update's MaybeCameraShake from the anchor
        /// acceleration, updated by Camera::UpdateAll - both in RumbleDriver) as a camera-space offset and X/Y rotations
        /// of the final view matrix, only at full sim speed. Applied to every mover's result (drive, track and ICE shots).
        /// </summary>
        void ApplyCameraShake(Vector3 accW, float dt)
        {
            if (NfsMw.Controls.RumbleDriver.Instance == null)
            {
                // [ours] without the rumble driver the shakers are kept here (MaybeCameraShake + UpdateCameraShakers)
                NfsMw.Controls.CameraShaker.MaybeCameraShake(ViewId == 2 ? 1 : 0, accW);
                NfsMw.Controls.CameraShaker.UpdateCameraShakers(dt);
            }
            if (!NfsMw.Controls.CameraShaker.ApplyCameraShake(ViewId, out var offset, out var euler)) return;
            transform.position += transform.rotation * offset;
            transform.rotation *= Quaternion.Euler(euler);
        }

        Vector3 _lastAccel;

        /// <summary>
        /// CDActionDrive::Update roadblock look-ahead: a roadblock car 3 m..speed/4 m ahead, inside a 0.7 cosine cone and
        /// within 2.75 m of the travel line, sets the anchor's mIsCloseToRoadBlock and DAT_00911056.
        /// </summary>
        void UpdateRoadblock(Vector3 P, Vector3 F, float speed, bool grounded)
        {
            Director.Speed = speed;
            Director.Grounded = grounded;
            Director.CloseToRoadblock = false;
            var police = Systems.Police;
            if (police == null || speed * 0.25f <= 3f) return;
            foreach (var c in police.CopCars)
            {
                if (!c) continue;
                var cop = c.GetComponent<Game.Police.CopCar>();
                if (cop == null || cop.Roadblock == null) continue;
                var d = c.transform.position - P;
                float dist = d.magnitude;
                if (dist <= 3f || dist >= speed * 0.25f) continue;
                float cos = Vector3.Dot(d / dist, F);
                if (cos <= 0.7f) continue;
                float lateral = dist * Mathf.Sqrt(Mathf.Max(0, 1f - cos * cos));
                if (lateral < 2.75f) { Director.CloseToRoadblock = true; Director.RoadblockShot = true; return; }
            }
        }

        /// <summary>
        /// Speed / surface noise (0x47BEC0, CameraNoise.DriveLayers + Camera::ApplyNoise at the world time, intensity 1):
        /// the speed graph 0x8EDFB0 (0..80 m/s), drag race ×1.5, acceleration above 5 m/s, rev limiter +0.15/×0.7, NOS 0.3/2
        /// and the brake / handbrake 0.3/5 above 5 m/s, the ground's CAMERA_NOISE for layer 2 (only with every wheel down:
        /// the anchor's surface is kNull otherwise), a quarter inside the bumper / hood POVs.
        /// </summary>
        void Shake(float speed, float fwdAccel, bool chase, PlayerCar car, float worldT)
        {
            var st = new CameraNoise.DriveState
            {
                Speed = speed,
                ForwardAccel = fwdAccel,
                DragRace = car && car.Dragster,
                Nos = car && car.NosFiring,
                BrakeEngaged = car && (car.Input.Brake > 0f || car.Input.Handbrake),
                OverRev = car && car.Spec != null && car.Rpm > car.Spec.RedLine - 500f,
                OutsidePov = chase,
            };
            // the anchor's simsurface: the car's ground surface while every wheel touches (0x472E57), else kNull (0)
            // [? the front-left wheel's face stands in for the body's WWorldPos surface]
            if (car && car.WheelsOnGround >= 4)
            {
                var n = CameraNoise.CameraNoiseOf(car.WheelAudio(0).surface);
                st.SurfaceAmp = n.x; st.SurfaceFreq = n.y;
            }
            CameraNoise.DriveLayers(st, out var f1, out var a1, out var f2, out var a2);
            CameraNoise.Apply(transform, f1, a1, f2, a2, worldT, FovH);
        }

        /// <summary>fBm: 6 octaves, gain 0.5, lacunarity 2 (Unity Perlin; kept for old callers, the exe noise is CameraNoise.Noise).</summary>
        internal static float Fbm(float x, float seed)
        {
            float sum = 0, a = 0.5f, f = 1f;
            for (int i = 0; i < 6; i++) { sum += a * (Mathf.PerlinNoise(x * f, seed * 97.13f + i * 13.7f) * 2f - 1f); a *= 0.5f; f *= 2f; }
            return sum;
        }

        /// <summary>terrainClamp (0x470fe0): sphere sweep from between car and eye to the eye, then at least 0.25 m above ground.</summary>
        internal static Vector3 TerrainClamp(Vector3 car, Vector3 eye)
        {
            var from = (car + eye) * 0.5f + Vector3.up * 0.3f;
            var dir = eye - from;
            if (dir.sqrMagnitude > 1e-4f)
            {
                float best = float.MaxValue;
                foreach (var h in Physics.SphereCastAll(from, 0.3f, dir.normalized, dir.magnitude, ~0, QueryTriggerInteraction.Ignore))
                    if (CameraUtil.IsWorld(h) && h.distance > 0 && h.distance < best) best = h.distance;
                if (best < float.MaxValue) eye = from + dir.normalized * Mathf.Max(0.2f, best - 0.05f);
            }
            if (CameraUtil.Ground(eye + Vector3.up * 3f, 8f, out float gy)) eye.y = Mathf.Max(eye.y, gy + 0.25f);
            return eye;
        }

        void Place(Vector3 eye, Vector3 look, Vector3 up, float fovH)
        {
            transform.position = eye;
            var dir = look - eye;
            if (dir.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(dir, up.sqrMagnitude > 1e-6f ? up : Vector3.up);
            LookPoint = look;
            FovH = fovH;
            if (_cam) ApplyProjection(_cam, fovH);
        }

        void UpdateMirror(PlayerCar car)
        {
            bool want = !Split && CameraOptions.RearViewMirror && Director.Action == CameraAction.Drive && _pov >= 0 && _pov <= 1 && car;
            if (want && !_mirror) _mirror = RearViewMirror.Create(transform);
            if (_mirror) { _mirror.Target = Target; _mirror.Show(want); }
        }

        /// <summary>
        /// The reader of the view's SimTimeMultiplier is QuickGame::OnManageTime (0x6F4D10): with the player view's camera
        /// multiplier below 1 the game speed is that multiplier (at least 0.01), replacing the speedbreaker's 0.25; at 1
        /// the speedbreaker (IPlayer vt+0x40, no NIS) applies. GameBootstrap sets PlayerCar.WorldTimeScale each Update.
        /// </summary>
        void ApplyTime(bool menu)
        {
            if (Split || menu || Time.timeScale <= 0) return;
            float baseScale = PlayerCar.WorldTimeScale;
            bool ours = _appliedScale > 0 && Mathf.Abs(Time.timeScale - _appliedScale) < 1e-5f;
            if (!ours && Mathf.Abs(Time.timeScale - baseScale) > 1e-5f) return;   // someone else owns the clock
            if (!ours && SimTimeMultiplier >= 0.999f) return;
            float want = SimTimeMultiplier < 0.999f ? Mathf.Max(SimTimeMultiplier, 0.01f) : baseScale;
            Time.timeScale = want;
            Time.fixedDeltaTime = want / PlayerCar.SimRate;
            _appliedScale = SimTimeMultiplier >= 0.999f ? 0 : want;
        }

        float _appliedScale;

        /// <summary>
        /// The exe's projection (0x6cf52b): x = 1/tan(FOV/2), y = 1/tan(FOV·(H/W)·k/2) with k = 1.15 for the player views:
        /// the FOV is horizontal and the vertical angle a linear scale of it.
        /// </summary>
        public static void ApplyProjection(Camera cam, float fovH, float k = 1.15f)
        {
            if (!cam) return;
            var px = cam.pixelRect;
            float aspect = px.height > 0 ? px.width / px.height : 16f / 9f;
            fovH = Mathf.Clamp(fovH, 1f, 170f);
            float vfov = Mathf.Clamp(fovH / aspect * k, 1f, 170f);
            var m = Matrix4x4.Perspective(vfov, aspect, cam.nearClipPlane, cam.farClipPlane);
            m.m00 = 1f / Mathf.Tan(fovH * 0.5f * Mathf.Deg2Rad);
            m.m11 = 1f / Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad);
            cam.projectionMatrix = m;
            cam.fieldOfView = vfov;
        }

        void OnDisable()
        {
            _enabledCount = Mathf.Max(0, _enabledCount - 1);
            LuaDrive.CinematicMoment -= OnCinematicMoment;
            if (_player1 == this) _player1 = null;
            Live.Remove(this);
            CameraDof.Off();
            if (_cam) _cam.ResetProjectionMatrix();
            if (_mirror) _mirror.Show(false);
            SimTimeMultiplier = 1f;
        }

        static bool FrontEndOpen() => UI.FrontEnd.Instance != null && UI.FrontEnd.Instance.IsOpen;
    }
}
