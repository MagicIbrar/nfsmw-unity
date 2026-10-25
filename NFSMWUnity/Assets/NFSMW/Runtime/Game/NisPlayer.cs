using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NfsMw.Game
{
    /// <summary>
    /// Plays a NIS for its real length (from the scene header): the race intro rolls the grid up to the start line under
    /// a set of staged shots with letterbox bars and a caption; an arrest shows the player through the police video-cam
    /// overlay (BUSTED_OVERLAY). Enter, Space or Esc skips (not an arrest: SkipOverNIS).
    /// The NISActivity side (specs/nis.md): the bundle's characters (<see cref="NisCharacter"/>), the event sequencer
    /// (<see cref="NisEventRunner"/>, ICECut/ICEEnd tags), the 31-tick settle before Play and the skip rules.
    /// </summary>
    public sealed class NisPlayer : MonoBehaviour
    {
        /// <summary>Off for automated runs that need the race to start immediately.</summary>
        public static bool Enabled = true;
        public static NisPlayer Active { get; private set; }

        public NisScene Scene;
        /// <summary>CAnimScene mTimeTotalLength (VanishFrame/30): the scene completes once the clock passes it.</summary>
        public float Length;
        /// <summary>
        /// ICETrack::GetCameraData's NIS parameter (0x47B030): (scene elapsed − scene start) / (scene length − scene start),
        /// not clamped (a key is valid only for 0..1).
        /// </summary>
        public float Time01 => Length - _start > 1e-4f ? (_t - _start) / (Length - _start) : 1;
        /// <summary>The scene clock (s) as of this frame, for NisCarControl's sim-rate track sampling.</summary>
        public float SceneTime => _t;
        /// <summary>Scene seconds per second: 0 while READY_TO_PLAY holds the scene at 0, the ICE sim speed otherwise.</summary>
        public float ClockRate => !_started || _finished || _paused ? 0f : (_iceActive ? _iceSim : 1f);
        /// <summary>NISActivity::Pause / UnPause (mPause, decomp NISActivity.cpp:646): the clock, the cars and the camera hold.</summary>
        public bool Paused => _paused;
        /// <summary>CAnimScene mTimeStart (StartFrame/30).</summary>
        float _start;
        bool _paused;
        /// <summary>Off: tracked NIS cars are placed kinematically on the track (the pre-port behaviour) [ours].</summary>
        public static bool PhysicalCars = true;

        delegate void ShotFn(float u, out Vector3 pos, out Vector3 look, out float fov);
        sealed class Roll
        {
            public PlayerCar Car;
            public Rigidbody Body;
            public Vector3 From, To;
            public Quaternion ToRot;
            public float Lift;
            public bool WasKinematic;
            public Func<float, (Vector3 pos, Quaternion rot)> Path;   // decoded NIS track (seconds -> pose)
            public string Actor;                                       // NIS channel ("Car1", "Cop2"): event sequencer system
            public CarFlares Flares;                                   // lights as they were (ENISLights may change them)
            public bool CopLights0, Headlights0;
            public NisCarControl Ctrl;                                 // SuspensionSpline drives a tracked car (Runtime/Physics/NisCarControl.cs)
            public bool Deactivated;                                   // IVehicle::Deactivate: a car1..8 channel without a track (hidden, still)
            public Renderer[] Hidden;
        }

        /// <summary>
        /// A game-space unit quaternion (x, y, z, w; render axes, Z up, right-handed) as a Unity rotation: the axis permutation
        /// (x, y, z) → (x, z, y) flips handedness, so the vector part is permuted and negated.
        /// </summary>
        static Quaternion GameQuat(float[] q) => new Quaternion(-q[0], -q[2], -q[1], q[3]);

        /// <summary>The NIS car frame is X forward, Y left, Z up (bone 1 of "ROOT"); Unity's car is Z forward: a 90° turn about up.</summary>
        static readonly Quaternion CarFrame = Quaternion.Euler(0f, 90f, 0f);

        /// <summary>
        /// CAnimScene::AnimatedCars_SetMainAndWheels: bone 1 × scene rotation × scene translation. Scene space is x right, y
        /// forward (the start heading), z up; <paramref name="sceneRot"/> = LookRotation(scene forward, up) at
        /// <paramref name="origin"/>, identity at the world origin for a world-coordinate scene (SceneType 1 / 4).
        /// </summary>
        static (Vector3, Quaternion) ScenePose(Vec3 v, float[] q, Vector3 origin, Quaternion sceneRot)
        {
            var pos = origin + sceneRot * new Vector3(v.X, v.Z, v.Y);
            var rot = sceneRot * GameQuat(q) * CarFrame;
            return (pos, rot);
        }

        /// <summary>A channel's full pose at scene time t (position with the track's own height, rotation with pitch and roll).</summary>
        static Func<float, (Vector3, Quaternion)> TrackPath(NisScene scene, string actor, Vector3 origin, Quaternion sceneRot) =>
            t => { scene.PoseFull(actor, t, out var v, out var q); return ScenePose(v, q ?? Identity, origin, sceneRot); };

        static readonly float[] Identity = { 0f, 0f, 0f, 1f };

        // NISActivity (Sim/Activities/NISActivity.cpp, speed.exe 0x6EA270..): the bundle's characters, event sequencer,
        // the READY_TO_PLAY suspension settle and the skip rules (specs/nis.md)
        NisContent _content;
        NisEventRunner _events;
        readonly List<NisCharacter> _chars = new List<NisCharacter>();
        bool _arrest, _started, _skipped;
        int _settle;
        float _settleTime;
        const int SettleFrames = 31;   // OnTask READY_TO_PLAY: Play() once mSuspensionSettle >= 31 (UpdateTime(0) meanwhile)
        IceCameras.Track _iceTrack;

        /// <summary>The NIS canvas (letterbox, captions, overlays) for the event runner.</summary>
        public Canvas OverlayCanvas => _canvas;
        /// <summary>The character whose face FacePixelation follows (mBoneMapType 3, skeleton "Bip23").</summary>
        public NisCharacter PixelateTarget { get; private set; }
        public IReadOnlyList<NisCharacter> Characters => _chars;
        public NisEventRunner Events => _events;

        readonly List<GameObject> _extras = new List<GameObject>();  // cars that only exist for the scene

        readonly List<(float weight, ShotFn fn)> _shots = new List<(float, ShotFn)>();
        readonly List<Roll> _rolls = new List<Roll>();
        readonly List<(Rigidbody body, bool kinematic)> _frozen = new List<(Rigidbody, bool)>();
        Action _done;
        float _t, _fov0, _rollTime;
        bool _finished;
        Camera _cam;
        ChaseCamera _chase;
        Canvas _canvas;
        RectTransform _barTop, _barBottom;
        FEText _caption;

        /// <summary>
        /// NISActivity::Construct (decomp NISActivity.cpp:98) returns null while an INIS exists: a second NIS is refused (and
        /// NISListenerActivity::MessageBusted ignores a bust during one). The refused request's completion fires at once.
        /// </summary>
        static bool Refused(Action done)
        {
            if (!Active || Active._finished) return false;
            Debug.Log($"NFSMW nis: {Active.name} is playing, a second NIS is refused (NISActivity::Construct)");
            done?.Invoke();
            return true;
        }

        static NisPlayer Begin(string name, NisScene scene, float length, Action done)
        {
            if (Active) Active.Finish();
            var p = new GameObject("NIS " + name).AddComponent<NisPlayer>();
            p.Scene = scene;
            p.Length = length;
            p._start = scene != null ? scene.StartTime : 0f;   // CAnimScene::BindToGame: mTimeStart = StartFrame/30
            p._t = p._start;
            p._done = done;
            p._cam = Camera.main;
            p._chase = p._cam ? p._cam.GetComponent<ChaseCamera>() : null;
            if (p._chase) p._chase.enabled = false;
            p._fov0 = p._cam ? p._cam.fieldOfView : 60f;
            p._near0 = p._cam ? p._cam.nearClipPlane : 0.3f;
            p._nonSkippable = NonSkippable(scene?.Name);
            p._precipitationEnable = Precipitation.PrecipitationEnable;   // NISActivity ctor: mIsPrecipitationEnable = PrecipitationEnable
            WorldStreamer.ActivateOverlay(scene?.OverlayName);   // CAnimScene::Init :517: TheVisibleSectionManager.ActivateOverlay(SeeulatorOverlayName)
            Active = p;
            NfsMw.Audio.NisSound.Loaded(p);   // NISActivity::Load: ESndGameState(eLOAD_BustedLoading) for an arrest / ending [hook: audio owner]
            return p;
        }

        /// <summary>
        /// NISActivity::Load (0x6F51F0) sets mNonSkipableNIS (+0x3A0) for the scenes whose name starts (bStrNICmp) with
        /// IntroNisBL, IntroNisDD, IntroNis10 or EndingNIS: the Blacklist intros, the opening, the boss intro and the
        /// endings. SkipOverNIS (0x6EA960) refuses them unless SkipMovies (0x926144, a config flag, 0 in retail). The
        /// skip-to-end branch (mSkipToNIS +0x3A1 → EPlayEndNIS) is dead on PC: the ctor (0x6FD590) clears it and nothing
        /// writes it.
        /// </summary>
        public static bool NonSkippable(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return false;
            foreach (var prefix in new[] { "IntroNisBL", "IntroNisDD", "IntroNis10", "EndingNIS" })
                if (scene.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        bool _nonSkippable;
        int _precipitationEnable = 1;

        // ---- race intro ------------------------------------------------------------------------------------------

        /// <summary>
        /// The event's intro NIS (GenericStart when it has none). CAnimScene::Init (AnimScene.cpp:475-518): the scene frame
        /// is the start line (gNISSceneOrigin/Angle, CAnimLocator::GetInitialAnimMatricies) for SceneType 0/2/3 and the world
        /// origin for 1/4 (the Blacklist intros); every car1..8 the race added is deactivated (InitCarAnimStatesFromStartingPositions)
        /// and only the channels with a _t/_q track get a controller and come back (CreateCarAnimationControllers); the scene
        /// runs from StartFrame/30 to VanishFrame/30 (BindToGame) and the race cars are released as it passes the end.
        /// </summary>
        public static NisPlayer RaceIntro(RaceManager race, RaceEvent ev, Vector3 grid, Vector3 fwd, string caption, Action done)
        {
            if (Refused(done)) return null;
            NisLibrary.Load(GameData.Root);
            var scene = NisLibrary.Get(ev.IntroNis) ?? NisLibrary.Get("GenericStart");
            if (scene == null) { done?.Invoke(); return null; }   // NISActivity::Load with no scene → COMPLETE at once
            bool tracked = scene.HaveCarAnimation != 0 && scene.HasTrack("Car1");
            bool world = scene != null && !scene.UsesStartLine;   // SceneType 1/4: world coordinates
            float length = scene?.Length ?? 6f;
            var p = Begin("Intro " + (scene?.Name ?? ev.EventId), scene, length, done);
            NfsMw.Audio.NisStreams.QueueNis(scene?.Name ?? "GenericStart");
            var right = Vector3.Cross(Vector3.up, fwd);
            // CAnimLocator::GetInitialAnimMatricies: the origin dropped onto the ground (ANIM_GetWorldHeight), RotZ(angle − 90°)
            var origin = world ? Vector3.zero : grid;
            if (!world) origin.y = GroundY(origin, origin.y);
            var sceneRot = world ? Quaternion.identity : Quaternion.LookRotation(fwd, Vector3.up);
            p._rollTime = length * 0.8f;
            var entrants = race.Entrants;
            for (int i = 0; i < entrants.Count; i++)
            {
                var e = entrants[i];
                if (!e.Car) continue;
                // Car1 = player; in a Blacklist intro Car2 is the rival (the last entrant), otherwise the grid order [the
                // channel → vehicle mapping is INIS::AddCar's caller, the race code]
                string actor = world ? (i == 0 ? "Car1" : i == entrants.Count - 1 ? "Car2" : null) : "Car" + (i + 1);
                if (tracked && actor != null && scene.HasTrack(actor))
                    p.AddRoll(e.Car, Vector3.zero, TrackPath(scene, actor, origin, sceneRot), actor);
                else p.AddDeactivated(e.Car, actor);
            }
            if (tracked) p.PrepareVehicles(scene, origin, sceneRot);
            var player = race.Entrants[0].Car.transform;
            var rival = race.Entrants.Count > 1 && race.Entrants[race.Entrants.Count - 1].Car ? race.Entrants[race.Entrants.Count - 1].Car.transform : null;
            Vector3 SceneToWorld(Vec3 v) => origin + sceneRot * new Vector3(v.X, v.Z, v.Y);
            // the scene frame as a matrix (columns: scene x, z, y axes and the origin, Unity space) for the characters
            var frame = world ? Matrix4x4.identity : SceneFrame(right, fwd, origin);
            // the camera track: ChooseGoodSceneCameraTrackIndex only when Load found a marker; otherwise Load's argument, the
            // race-start candidate's track (CRaceStartAnimCandidate.CameraTrack, [?] CAnimChooser::ChooseAnim is stripped) or 0
            int cameraTrack = 0;
            if (scene != null)
                foreach (var c in NisCandidateData.Load(GameData.Root).RaceStarts)
                    if (c.Scene == EaHash.Hash(scene.Name)) { cameraTrack = c.CameraTrack; break; }
            if (scene != null && p.UseIce(scene.Name, SceneToWorld, player, cameraTrack))
            {
                p.BuildOverlay(caption, true);
                p.StartActivity(frame, false);
                return p;
            }
            p.StartActivity(frame, false);
            if (world)
            {
                // the Blacklist intros are chases across the city: shots follow the cars instead of the grid
                p._shots.Add((0.3f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
                {
                    pos = player.position + player.forward * 10f + player.right * 2.5f + Vector3.up * 0.9f;
                    look = player.position + Vector3.up * 0.7f;
                    fov = 42f;
                }));
                if (rival)
                    p._shots.Add((0.3f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
                    {
                        pos = rival.position + rival.right * 4.5f + rival.forward * (2f - 3f * u) + Vector3.up * 0.9f;
                        look = rival.position + Vector3.up * 0.6f;
                        fov = 46f;
                    }));
                p._shots.Add((0.4f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
                {
                    float k = Smooth(u);
                    pos = Vector3.Lerp(player.position - player.forward * 16f + Vector3.up * 8f, player.position - player.forward * 6.2f + Vector3.up * 1.9f, k);
                    look = player.position + Vector3.up * 1.0f;
                    fov = Mathf.Lerp(60f, 58f, k);
                }));
                p.BuildOverlay(caption, true);
                Debug.Log($"NFSMW nis: {scene.Name} '{scene.Description}' (world tracks, {p._extras.Count} cops), playing {length:0.0} s");
                return p;
            }

            p._shots.Add((0.3f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
            {
                pos = grid + fwd * (16f - 3f * u) - right * 2.5f + Vector3.up * 0.7f;
                look = player.position + Vector3.up * 0.7f;
                fov = 38f;
            }));
            p._shots.Add((0.25f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
            {
                pos = player.position + right * 4.2f + fwd * (1.5f - 3f * u) + Vector3.up * 0.8f;
                look = player.position + fwd * 1.2f + Vector3.up * 0.6f;
                fov = 48f;
            }));
            if (rival)
                p._shots.Add((0.2f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
                {
                    pos = rival.position + Quaternion.Euler(0, Mathf.Lerp(40f, -30f, u), 0) * (fwd * 5.5f) + Vector3.up * 1.1f;
                    look = rival.position + Vector3.up * 0.7f;
                    fov = 42f;
                }));
            p._shots.Add((0.25f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
            {
                float k = Smooth(u);
                var chase = player.position - player.forward * 6.2f + Vector3.up * 1.9f;
                pos = Vector3.Lerp(grid - fwd * 16f + Vector3.up * 6f, chase, k);
                look = Vector3.Lerp(grid + Vector3.up, player.position + Vector3.up * 1.0f, k);
                fov = Mathf.Lerp(55f, 58f, k);
            }));
            p.BuildOverlay(caption, true);
            Debug.Log($"NFSMW nis: {(scene != null ? $"{scene.Name} '{scene.Description}' type {scene.SceneType} {scene.Frames} frames{(tracked ? ", decoded car tracks" : "")}" : "default intro")}, playing {length:0.0} s");
            return p;
        }

        /// <summary>
        /// The SpecialCarList tables (decomp Animation/AnimCandidates.cpp:7-60): TRAFFIC_MIX1..9 → "cop1".."cop8".
        /// </summary>
        static readonly string[][] SpecialCarLists =
        {
            new[] { "copmidsize_nis", "copmidsize_nis_ld", "copmidsize_nis_ld", "trafminivan", "copghost", "trafsuva", "trafstwag", "traftaxi" },
            new[] { "copmidsize_nis_ld", "copmidsize_nis_ld", "trafficcoup", "trafpizza", "trafminivan", "trafstwag", "trafsuva", "traftaxi" },
            new[] { "copmidsize_nis_ld", "copmidsize_nis_ld", "copmidsize_nis_ld", "copsuv", "copmidsize_nis_ld", "copsuv", "copmidsize_nis", "semilog" },
            new[] { "eclipsegt", "imprezawrx", "trafficcoup", "trafficcoup", "trafpizza", "trafminivan", "semib", "trafpizza" },
            new[] { "eclipsegt", "eclipsegt", "semilog", "trafficcoup", "traf4dsedc", "trafminivan", "copmidsize_nis", "trafstwag" },
            new[] { "copsportghost", "copsportghost", "copsportghost", "copsportghost", "copsportghost", "copsportghost", "copsportghost", "copcross" },
            new[] { "copmidsize_nis_ld", "copmidsize_nis_ld", "copmidsize_nis_ld", "trafstwag", "trafficcoup", "traf4dsedc", "trafminivan", "trafpizza" },
            new[] { "trafamb", "trafstwag", "trafpizza", "trafminivan", "trafficcoup", "traf4dsedc", "trafstwag", "trafpizza" },
            new[] { "copmidsize_nis_ld", "copmidsize_nis_ld", "copmidsize_nis_ld", "trafminivan", "trafficcoup", "traf4dsedc", "trafstwag", "trafpizza" },
        };

        /// <summary>
        /// NISActivity::PrepareVehicles (0x6F2F60; decomp NISActivity.cpp:654-746) with the scene's SpecialCarList
        /// (CAnimCandidateData): ONE_COP (0) "cop1" = copmidsize_nis; LOTS_OF_COPS (1) cop1 copmidsize_nis + cop2..8
        /// copmidsize_nis_ld; ONE_SPORT_COP (11) cop1 = copsport (COP_CROSS customization [?: not applied]);
        /// TRAFFIC_MIX1..9 (2..10) the tables. Every car is a DRIVER_NIS PVehicle at the origin, added as "cop%d" and given to
        /// the scene; one without a _t/_q track never gets a controller and stays at the origin, so [ours] those are not
        /// spawned (logged).
        /// </summary>
        void PrepareVehicles(NisScene scene, Vector3 origin, Quaternion sceneRot)
        {
            int list = NisCandidateData.Load(GameData.Root).CarList(scene.Name);
            var names = new string[8];
            switch (list)
            {
                case 0: names[0] = "copmidsize_nis"; break;
                case 1: names[0] = "copmidsize_nis"; for (int i = 1; i < 8; i++) names[i] = "copmidsize_nis_ld"; break;
                case 11: names[0] = "copsport"; break;
                default:
                    if (list >= 2 && list <= 10) names = SpecialCarLists[list - 2];
                    break;
            }
            var skipped = new List<string>();
            for (int c = 1; c <= 8; c++)
            {
                string a = "Cop" + c;
                string pv = names[c - 1];
                bool hasTrack = scene.HasTrack(a);
                if (pv == null) { if (hasTrack) skipped.Add(a + " (not in the car list)"); continue; }
                if (!hasTrack) { skipped.Add(a + "=" + pv + " (no track: stays at the origin)"); continue; }
                var (pos0, rot0) = TrackPath(scene, a, origin, sceneRot)(_start);
                PlayerCar car;
                string model = TrafficPhysics.VehicleSpecs.Get(pv)?.Model ?? pv;
                try { car = CarSpawner.Spawn(model, Color.white, new OwnedCar(), pos0, rot0, "NIS " + a + " " + pv); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"NFSMW nis: {a} {pv} ({model}) failed: {ex.Message}; COPMIDSIZE instead");
                    try { car = CarSpawner.Spawn("COPMIDSIZE", Color.white, new OwnedCar(), pos0, rot0, "NIS " + a); }
                    catch (Exception) { continue; }
                }
                _extras.Add(car.gameObject);
                if (pv.StartsWith("cop", StringComparison.OrdinalIgnoreCase)) AddLightBar(car.transform);
                AddRoll(car, Vector3.zero, TrackPath(scene, a, origin, sceneRot), a);
            }
            if (skipped.Count > 0) Debug.Log($"NFSMW nis: {scene.Name} car list {list}: {string.Join(", ", skipped)}");
        }

        static IceCameras Ice => IceShot.Data;
        IceCameras.Sample _iceSample;
        bool _iceSampled;
        readonly ObstructionRaiser _iceRaiser = new ObstructionRaiser();
        readonly IceCarSpaceLag _iceCarLag = new IceCarSpaceLag();
        float _dutch;
        // ICE playback (CDActionIce 0x484270 -> ICEMover::Update 0x482ec0): horizontal FOV, Camera SimTimeMultiplier,
        // LB_height, the current key (overlays and the car pose captured at each cut)
        bool _iceActive;
        float _iceSim = 1f, _iceLetterbox, _near0 = 0.3f;
        int _iceKey = -1;
        GameObject _iceOverlay;
        string _iceOverlayName;
        Vector3 _keyCarPos;
        Quaternion _keyCarRot = Quaternion.identity;

        /// <summary>
        /// Plays the scene's ICE camera track (GLOBAL\InGameB.bun) in place of staged shots (specs/camera_core.md §7).
        /// The track is "Track %d" with the number ICEManager::ChooseGoodSceneCameraTrackIndex (0x47ef70) picks
        /// (ChooseCameraPlaybackTrack 0x483d30); the camera parameter is the scene's progress. Points are mapped by their
        /// space: scene (the scene frame, as the car tracks), world, car (car 1, game X forward, Y left, Z up; the pose at
        /// the last cut when the key ignores orientation) and hybrid (car position, world axes). Each key's lens (mm) gives
        /// a horizontal FOV, its sim speed slows the scene clock, its letterbox sizes the bars and its overlay pushes an
        /// FE package (FLASHERS, BLACK_FADE_OUT, BUSTED_OVERLAY, BLACK_BACKGROUND).
        /// </summary>
        bool UseIce(string sceneName, Func<Vec3, Vector3> sceneToWorld, Transform car1, int track = -1)
        {
            var tracks = sceneName != null ? Ice.Find(0, sceneName) : null;
            if (tracks == null || tracks.Count == 0) return false;
            // the anchor car frame (0x466A60 FrameFromForward): the car's forward and the WORLD up, no roll; a key with
            // bIgnoreOrientation keeps the orientation captured at the last hard cut (mover +0xD0) with the LIVE position
            Quaternion CarFrameNow() => car1 ? Quaternion.LookRotation(Vector3.ProjectOnPlane(car1.forward, Vector3.up).sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(car1.forward, Vector3.up) : Vector3.forward, Vector3.up) : Quaternion.identity;
            Vector3 Map(Vec3 v, IceCameras.Space space, bool frozen)
            {
                switch (space)
                {
                    case IceCameras.Space.World: return Coords.ToUnity(v.X, v.Y, v.Z);
                    case IceCameras.Space.Car when car1: return car1.position + (frozen ? _keyCarRot : CarFrameNow()) * new Vector3(-v.Y, v.Z, v.X);
                    case IceCameras.Space.Hybrid when car1: return car1.position + Coords.ToUnity(v.X, v.Y, v.Z);
                    default: return sceneToWorld(v);
                }
            }
            int number = track >= 0 ? track : ChooseGoodTrack(tracks, (v, sp) => Map(v, sp, false), car1);
            // ChooseCameraPlaybackTrack (0x483D30): GetTrack("Track %d") by name, no fallback: without it there is no ICE camera
            var tr = IceCameras.TrackByName(tracks, "Track " + number);
            if (tr == null || tr.Keys.Count == 0)
            {
                Debug.Log($"NFSMW nis: {sceneName} has no camera track 'Track {number}' ({tracks.Count} tracks): no ICE camera");
                return false;
            }
            _shots.Clear();
            _iceActive = true;
            _iceKey = -1;
            _iceCarLag.Reset();
            _shots.Add((1f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
            {
                // ICETrack::GetCameraData: the key is valid for 0 ≤ param ≤ 1 only; outside, the camera keeps its last pose
                float param = Time01;
                if ((param < 0f || param > 1f) && _iceSampled) { pos = _cam.transform.position; look = pos + _cam.transform.forward; fov = _cam.fieldOfView; return; }
                var s = IceCameras.Evaluate(tr, Mathf.Clamp01(param));
                if (s.KeyIndex != _iceKey)
                {
                    // ICEMover::SetDesired (0x482160): a cut = the new key does not share the point with the old one
                    // (KeysShared), a hard entry = the first key without bSmooth; both flush the motion blur and capture the
                    // car frame (+0xD0); the overlay swaps when it changes; one ICECut%d tag for the new key (not the skipped ones)
                    var prev = _iceKey >= 0 && _iceKey < tr.Keys.Count ? tr.Keys[_iceKey] : null;
                    bool overlayChanged = prev == null || prev.Overlay != s.Key.Overlay;
                    bool hardCut = prev == null ? (s.Key.Smooth & 1) == 0 : !IceCameras.KeysShared(prev, 1, s.Key, 0);
                    _iceKey = s.KeyIndex;
                    if (hardCut)
                    {
                        if (car1) { _keyCarPos = car1.position; _keyCarRot = CarFrameNow(); }
                        VisualTreatment.FlushAccumulationBuffer();   // eVisualTreatment FlushAccumulationBuffer (0x725550)
                    }
                    if (overlayChanged) SetIceOverlay(s.Key.Overlay);
                    if (_started && _events != null) _events.FireTag("ICECut" + _iceKey);
                }
                pos = Map(s.Eye, s.EyeSpace, s.Key.IgnoreOrientation);
                look = Map(s.Look, s.LookSpace, s.Key.IgnoreOrientation);
                // ICEMover::Update (0x483869..): bConstrainToWorld → terrainClamp (0x470FE0), bConstrainToCars →
                // obstructionRaise (0x47C6F0, with the anchor's velocity), whenever the ICE editor state is below 9 (always
                // in game) [ours: on the eye before the look-at; the exe applies them to the final matrix after the noise]
                var anchor = car1 ? car1.position : look;
                var anchorVel = car1 && car1.TryGetComponent<Rigidbody>(out var arb) ? arb.linearVelocity : Vector3.zero;
                // ICEAnchor::Update every frame, then the key's bCarSpaceLag offset on eye and look (ICEMover::Update
                // 0x4834B5..0x4835A4, camera_core.md §13; round 9)
                float lagDt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f);
                if (car1) _iceCarLag.Anchor(car1.position, car1.forward, anchorVel, lagDt);
                if (s.Key.CarSpaceLag)
                {
                    var lag = _iceCarLag.Offset(lagDt * s.SimSpeed);
                    pos -= lag;
                    look -= lag;
                }
                if (s.Key.ConstrainToWorld) pos = ChaseCamera.TerrainClamp(anchor, pos);
                if (s.Key.ConstrainToCars) pos = _iceRaiser.Apply(pos, look, anchorVel, car1 ? car1.GetComponent<PlayerCar>() : null);
                _iceSample = s;
                _iceSampled = true;
                fov = s.FovDeg;
                _dutch = s.DutchDeg;
                _iceSim = s.SimSpeed;
                _iceLetterbox = s.Letterbox;
                if (_cam && s.NearClip >= 0f) _cam.nearClipPlane = Mathf.Max(0.01f, s.NearClip);   // Camera NearZ = near when ≥ 0
            }));
            _iceTrack = tr;
            Debug.Log($"NFSMW nis: {sceneName} camera track '{tr.Name}' {tr.Length:0.00} s, {tr.Keys.Count} keys");
            return true;
        }

        /// <summary>
        /// ICEManager::ChooseGoodSceneCameraTrackIndex (0x47ef70): with two or more tracks, the one whose first key's (key 1
        /// when key 0 is bSmooth) horizontal eye − look direction has the largest dot with the player car's forward (the shot
        /// that sees the car's front; a zero direction scores 0, the first strict maximum wins). Returns the list index n,
        /// looked up as "Track n".
        /// </summary>
        static int ChooseGoodTrack(List<IceCameras.Track> tracks, Func<Vec3, IceCameras.Space, Vector3> map, Transform car)
        {
            if (tracks.Count < 2) return 0;
            int best = 0;
            float bestDot = -1f;
            var fwd = car ? car.forward : Vector3.forward;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Keys.Count == 0) continue;
                var k = tracks[i].Keys[(tracks[i].Keys[0].Smooth & 1) != 0 && tracks[i].Keys.Count > 1 ? 1 : 0];
                var d = map(k.EyeStart, k.EyeSpace) - map(k.LookStart, k.LookSpace);
                d.y = 0;
                float dot = d.sqrMagnitude < 1e-12f ? 0f : Vector3.Dot(d.normalized, fwd);
                if (dot > bestDot) { bestDot = dot; best = i; }
            }
            return best;
        }

        /// <summary>ICE::HideOverlay (0x46eae0) then ICE::ShowOverlay (0x46ea50): the key's overlay package on the NIS canvas.</summary>
        void SetIceOverlay(byte overlay)
        {
            if (_iceOverlay) Destroy(_iceOverlay);
            _iceOverlay = null;
            if (overlay == 0 || overlay >= IceCameras.OverlayPackages.Length || !_canvas) return;
            string package = IceCameras.OverlayPackages[overlay];
            if (!FrontEndData.Packages.ContainsKey(package)) return;
            var view = FEView.Open(package, _canvas.transform);
            if (!view) return;
            _iceOverlay = view.gameObject;
            _iceOverlayName = package;
            if (package == "BUSTED_OVERLAY")
            {
                var car = GameSession.ActiveCar?.Car ?? "";
                view.SetText(view.Find(0xF1ED545A), $"SUSPECT VEHICLE: {CarNames.Display(car).ToUpperInvariant()}   APPREHENDED FOR EXCESSIVE SPEEDING AND EVADING POLICE");
            }
        }

        static Quaternion Rot(float gameHeading)
        {
            float a = gameHeading * Mathf.Deg2Rad;
            return Quaternion.LookRotation(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)), Vector3.up);
        }

        /// <summary>ENISLights fCopLights: the cop car's light bar on or off.</summary>
        public void SetLightBar(Transform car, bool on)
        {
            if (!car) return;
            foreach (Transform c in car)
                if (c.name == "Light Bar" && c.TryGetComponent<Light>(out var l)) l.enabled = on;
        }

        /// <summary>The car playing a NIS channel ("Car1", "Cop2"), or null.</summary>
        public PlayerCar CarOf(string channel)
        {
            if (channel == null) return null;
            foreach (var r in _rolls) if (r.Car && string.Equals(r.Actor, channel, StringComparison.OrdinalIgnoreCase)) return r.Car;
            return null;
        }

        /// <summary>
        /// ENISHideCharacter: fAnimModelName (the character's model, e.g. CUFFS01) shown or hidden.
        /// CAnimScene::GetAnimEntityWithModelName (AnimScene.cpp:789-800) walks the entity list from the tail and returns the
        /// first match: the last-added entity with that model.
        /// </summary>
        public void ShowCharacter(string model, bool show)
        {
            for (int i = _chars.Count - 1; i >= 0; i--)
            {
                var c = _chars[i];
                if (c && string.Equals(c.ModelName, model, StringComparison.OrdinalIgnoreCase)) { c.Hidden = !show; return; }
            }
        }

        /// <summary>NISActivity::Pause / UnPause (mPause): the scene holds (UpdatePlaying dT = 0, READY_TO_PLAY waits).</summary>
        public void SetPaused(bool paused) => _paused = paused;

        /// <summary>
        /// ENISOverlayMessage: the FE package on the NIS canvas (the same one the ICE key overlays use) and the message
        /// sent to it. [?] The message runs the package's script of that name (FLASHTRIGGER flashes BUSTED_OVERLAY).
        /// </summary>
        public void ShowOverlayPackage(string package, string message)
        {
            if (!_canvas || string.IsNullOrEmpty(package)) return;
            FEView view = _iceOverlay ? _iceOverlay.GetComponent<FEView>() : null;
            if (!view || !string.Equals(_iceOverlayName, package, StringComparison.OrdinalIgnoreCase))
            {
                if (!FrontEndData.Packages.ContainsKey(package)) return;
                if (_iceOverlay) Destroy(_iceOverlay);
                view = FEView.Open(package, _canvas.transform);
                if (!view) return;
                _iceOverlay = view.gameObject;
                _iceOverlayName = package;
                if (package == "BUSTED_OVERLAY")
                {
                    var car = GameSession.ActiveCar?.Car ?? "";
                    view.SetText(view.Find(0xF1ED545A), $"SUSPECT VEHICLE: {CarNames.Display(car).ToUpperInvariant()}   APPREHENDED FOR EXCESSIVE SPEEDING AND EVADING POLICE");
                }
            }
            if (!string.IsNullOrEmpty(message))
            {
                uint script = FrontEndData.Hash(message);
                foreach (var n in view.Nodes) if (n.Parent == null) view.Play(n, script, true);
            }
        }

        static Matrix4x4 SceneFrame(Vector3 right, Vector3 fwd, Vector3 origin)
        {
            var m = Matrix4x4.identity;
            m.SetColumn(0, right);
            m.SetColumn(1, Vector3.up);
            m.SetColumn(2, fwd);
            m.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1));
            return m;
        }

        /// <summary>
        /// The NISActivity part of a scene start: CAnimScene::CreateAnimEntities (0x44D0C0) for the bundle's characters
        /// in the scene frame, and the event sequencer (created at load, started with "START" when the scene plays).
        /// </summary>
        void StartActivity(Matrix4x4 frame, bool arrest)
        {
            _arrest = arrest;
            _content = NisContent.For(Scene);
            if (_content == null) return;
            foreach (var info in _content.Characters)
            {
                if (info.TypeId != 1) continue;   // CAnimEntityFactory: 1 = BasicCharacter (the only type in the retail bundles)
                NisCharacter c = null;
                try { c = NisCharacter.Spawn(_content, info, frame, transform); }
                catch (Exception ex) { Debug.LogWarning($"NFSMW nis: character {info.InstanceHash:X8} failed: {ex.Message}"); }
                if (!c) continue;
                _chars.Add(c);
                if (c.BoneMapType == 3) PixelateTarget = c;
            }
            var engine = _content.Events?.Engines.Count > 0 ? _content.Events.Engines[0] : null;
            if (engine != null) _events = new NisEventRunner(this, engine, 0);
            if (_chars.Count > 0 || _events != null)
                Debug.Log($"NFSMW nis: {Scene?.Name} activity: {_chars.Count} characters [{string.Join(", ", _chars.Select(c => c.ModelName))}], events {(engine != null ? engine.Name + " " + engine.Systems.Length + " systems" : "none")}");
        }

        void AddLightBar(Transform car)
        {
            if (car.TryGetComponent<CarFlares>(out var flares)) flares.CopLightsOn = true;
            foreach (var (x, c) in new[] { (-0.4f, Color.red), (0.4f, new Color(0.2f, 0.4f, 1f)) })
            {
                var go = new GameObject("Light Bar");
                go.transform.SetParent(car, false);
                go.transform.localPosition = new Vector3(x, 1.55f, 0);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = c;
                l.range = 14f;
                l.intensity = 5f;
            }
        }

        /// <summary>
        /// CAnimScene::InitCarAnimStatesFromStartingPositions / FromNIS (AnimScene.cpp:546-588): every car1..8 the race
        /// gave the scene is deactivated (IVehicle::Deactivate: not simulated, not drawn); only those with a track are
        /// activated again by CreateCarAnimationControllers. [ours] Hidden and held still at its spot; it comes back at the
        /// end where it was (the race code re-activates it).
        /// </summary>
        void AddDeactivated(PlayerCar car, string actor)
        {
            var body = car.GetComponent<Rigidbody>();
            var r = new Roll { Car = car, Body = body, To = car.transform.position, ToRot = car.transform.rotation, From = car.transform.position, WasKinematic = body && body.isKinematic, Actor = actor, Deactivated = true };
            if (car.TryGetComponent(out r.Flares)) { r.CopLights0 = r.Flares.CopLightsOn; r.Headlights0 = r.Flares.Headlights; }
            r.Lift = RideHeight(car, r.To.y - GroundY(r.To, r.To.y - 0.5f));
            if (body)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
            var hidden = new List<Renderer>();
            foreach (var rend in car.GetComponentsInChildren<Renderer>()) if (rend && rend.enabled) { rend.enabled = false; hidden.Add(rend); }
            r.Hidden = hidden.ToArray();
            _rolls.Add(r);
            Place(r, 1f);
        }

        void AddRoll(PlayerCar car, Vector3 back, Func<float, (Vector3, Quaternion)> path = null, string actor = null)
        {
            var body = car.GetComponent<Rigidbody>();
            var r = new Roll { Car = car, Body = body, To = car.transform.position, ToRot = car.transform.rotation, From = car.transform.position + back, WasKinematic = body && body.isKinematic, Path = path, Actor = actor };
            if (car.TryGetComponent(out r.Flares)) { r.CopLights0 = r.Flares.CopLightsOn; r.Headlights0 = r.Flares.Headlights; }
            r.Lift = RideHeight(car, r.To.y - GroundY(r.To, r.To.y - 0.5f));
            // NISActivity::AddCar → SetAnimating(true): SuspensionSpline + EngineSpline drive a tracked car physically
            if (path != null && PhysicalCars && body)
            {
                body.isKinematic = false;
                r.Ctrl = NisCarControl.Begin(car, this, path, actor);
            }
            if (body && !r.Ctrl)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
            _rolls.Add(r);
            Place(r, 0);
        }

        void Place(Roll r, float k)
        {
            if (!r.Car || r.Ctrl) return;   // the controller places it (and lets it go between ENISDetach / ENISReattach)
            Vector3 pos;
            if (r.Path != null)
            {
                // the kinematic fallback (PhysicalCars off): the track pose on the ground, yaw only [ours]
                var (p, rot) = r.Path(k >= 1f ? Length : _t);
                pos = p;
                pos.y = GroundY(pos, r.To.y - r.Lift) + r.Lift;
                var f = Vector3.ProjectOnPlane(rot * Vector3.forward, Vector3.up);
                if (f.sqrMagnitude > 1e-6f) rot = Quaternion.LookRotation(f, Vector3.up);
                // ENISCarPitch / ENISCarRoll / ENISCarShake body tweaks of this channel (SuspensionSpline::NISCarTweaks)
                if (k < 1f && _events != null && r.Actor != null) rot *= _events.BodyTweak(r.Actor);
                r.Car.transform.rotation = rot;
                if (r.Body) r.Body.rotation = rot;
            }
            else
            {
                pos = Vector3.Lerp(r.From, r.To, k);
                pos.y = GroundY(pos, pos.y - r.Lift) + r.Lift;
            }
            r.Car.transform.position = pos;
            if (r.Body) r.Body.position = pos;
        }

        /// <summary>Car origin above the road with the wheels touching it (the grid drop height is not settled yet).</summary>
        static float RideHeight(PlayerCar car, float fallback)
        {
            return car && car.Spec != null ? car.RestLift : fallback;
        }

        static float GroundY(Vector3 at, float fallback)
        {
            float best = float.MaxValue, y = fallback;
            foreach (var h in Physics.RaycastAll(at + Vector3.up * 4f, Vector3.down, 14f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.rigidbody && h.distance < best) { best = h.distance; y = h.point.y; }
            return y;
        }

        // ---- arrest ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Busted: NISListenerActivity::MessageBusted (decomp AnimChooseArrest.cpp:225-258): ignored while a NIS exists;
        /// ChooseArrestAnimation, ChooseArrestLocation, AddCar("car1", player), StartLocation, Load(Arrest, playNow). The
        /// scene is an arrest from the police video-cam (BUSTED_OVERLAY) and runs for its VanishFrame length.
        /// </summary>
        public static NisPlayer Busted(PlayerCar player, Vector3? cop, Action done)
        {
            if (Refused(done)) return null;
            NisLibrary.Load(GameData.Root);
            // AnimChooseArrest: heat 0-1 picks M01/M16/F02/F18, any higher heat M06/M19/F06/F07 (retail falls through), each
            // with its "b" variant
            int heat = Systems.Police != null ? Systems.Police.Heat : 1;
            var pool = heat <= 1 ? new[] { "M01", "M16", "F02", "F18" } : new[] { "M06", "M19", "F06", "F07" };
            string pick = "Arrest" + pool[UnityEngine.Random.Range(0, pool.Length)] + (UnityEngine.Random.value < 0.5f ? "b" : "");
            var scene = NisLibrary.Get(pick) ?? NisLibrary.Get(pick.TrimEnd('b'));
            if (scene == null)
            {
                var arrests = NisLibrary.Scenes.Values.Where(s => s.Name.StartsWith("Arrest", StringComparison.OrdinalIgnoreCase)).ToList();
                scene = arrests.Count > 0 ? arrests[UnityEngine.Random.Range(0, arrests.Count)] : null;
            }
            var clip = NfsMw.Audio.NisStreams.Clip(scene != null ? scene.Name : pick) ?? NisAudio.Arrest();
            // the scene runs StartFrame..VanishFrame (CAnimScene::BindToGame); the stream is not the clock
            float length = scene != null ? scene.Length : (clip ? Mathf.Clamp(clip.length, 4f, 12f) : 5f);
            var p = Begin("Busted", scene, length, done);
            // UpdateLoading (0x6F7230): an arrest stays in LOADING for at least 2 s of real time after Load, behind the
            // load fade (EFadeScreenOn(false) at Load; EFadeScreenOff "30_IN" 0.6 s at the first UpdatePlaying)
            p._loadHold = 2f;
            if (clip)
            {
                var src = p.gameObject.AddComponent<AudioSource>();
                src.clip = clip;
                src.spatialBlend = 0;
                src.volume = 0.9f;
                src.Play();
            }
            var t = player.transform;
            var body = player.GetComponent<Rigidbody>();
            if (body)
            {
                p._frozen.Add((body, body.isKinematic));
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
            // NISListenerActivity::MessageBusted (decomp AnimChooseArrest.cpp:225) → ChooseArrestLocation: the road centre
            // at the player (WRoadNav kPathCop), facing the player's heading + 90° (the scene angle); then
            // NISActivity::Load (0x6F51F0) moves the scene to the closest BustedMomentMarker (GetClosestMarker 0x4491E0:
            // distance + 25 m per turn of heading difference, no distance limit), drops it on the ground and nukes the
            // smackables within 10 m. The scene frame is RotZ(angle − 90°) at the origin (CAnimLocator::
            // GetInitialAnimMatricies); car 1 (the player) is moved onto its track by its first SetNISPosition.
            var pfwd = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
            float playerHeading = Mathf.Atan2(pfwd.z, pfwd.x) * Mathf.Rad2Deg;
            var origin = t.position;
            float baseHeading = playerHeading + 90f;
            int cameraTrack = 0;   // ChooseArrestAnimation: *cameraTrack = 0
            bool atMarker = false;
            try
            {
                var nav = new RoadNav { DecisionFilter = false };
                nav.SetLaneType(RoadGraph.Lane.Cop);
                if (nav.InitAtPoint(t.position, pfwd, true, 0f)) origin = (nav.LeftPosition + nav.RightPosition) * 0.5f;
            }
            catch (Exception) { }   // [ours] no road graph: the player's own spot
            if (scene != null)
            {
                int markerType = NisCandidateData.Load(GameData.Root).MarkerType(scene.Name);
                if (markerType >= 0 && markerType < NisCandidateData.MarkerNames.Length)
                {
                    var r = new Vec3(origin.x, origin.z, origin.y);   // render space
                    ushort angle = (ushort)Mathf.RoundToInt(Mathf.Repeat(baseHeading, 360f) / 360f * 65536f);
                    if (TrackMarkers.Load(GameData.Root).Closest(NisCandidateData.MarkerNames[markerType], r, angle, out var m, out float score))
                    {
                        origin = Coords.ToUnity(m.Position.X, m.Position.Y, m.Position.Z);
                        baseHeading = m.Angle * 360f / 65536f;
                        cameraTrack = -1;   // a marker: ICEManager::ChooseGoodSceneCameraTrackIndex
                        atMarker = true;
                        Debug.Log($"NFSMW nis: arrest at BustedMomentMarker ({m.Position.X:0},{m.Position.Y:0},{m.Position.Z:0}) {baseHeading:0}°, {Vector3.Distance(origin, t.position):0} m from the bust");
                    }
                }
            }
            origin.y = GroundY(origin, origin.y);
            // Load, arrest at a marker: NIS_NukeSmackablesWithinRange(start location, 10 m)
            if (atMarker) NisEventRunner.NukeSmackables(origin, 10f);
            float br = baseHeading * Mathf.Deg2Rad;
            var sfwd = new Vector3(Mathf.Cos(br), 0, Mathf.Sin(br));
            var sright = Vector3.Cross(Vector3.up, sfwd);
            var sceneRot = Quaternion.LookRotation(sfwd, Vector3.up);
            Vector3 ArrestToWorld(Vec3 v) => origin + sceneRot * new Vector3(v.X, v.Z, v.Y);
            if (scene != null && p.UseIce(scene.Name, ArrestToWorld, t, cameraTrack))
            {
                if (body) body.isKinematic = false;
                p._frozen.Clear();
                if (scene.HaveCarAnimation != 0 && scene.HasTrack("Car1")) p.AddRoll(player, Vector3.zero, TrackPath(scene, "Car1", origin, sceneRot), "Car1");
                else p.AddDeactivated(player, "Car1");
                if (scene.HaveCarAnimation != 0) p.PrepareVehicles(scene, origin, sceneRot);   // ONE_COP: "cop1" = copmidsize_nis
                p.BuildOverlay("", true);
                p.StartActivity(SceneFrame(sright, sfwd, origin), true);
                Debug.Log($"NFSMW nis: busted, {scene.Name} at heat {heat}, audio {(clip ? clip.name : "none")}, playing {p.Length:0.0} s");
                return p;
            }
            var eye = cop.HasValue && (cop.Value - t.position).sqrMagnitude < 40f * 40f
                ? cop.Value + Vector3.up * 1.3f
                : t.position - t.forward * 9f + t.right * 3f + Vector3.up * 1.4f;
            p._shots.Add((1f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
            {
                float wobble = Mathf.Sin(u * 23f) * 0.03f + Mathf.Sin(u * 57f) * 0.015f;
                pos = eye + new Vector3(wobble, wobble * 0.5f, 0);
                look = t.position + Vector3.up * 0.6f;
                fov = Mathf.Lerp(40f, 30f, u);
            }));
            p.BuildArrestOverlay(player);
            if (scene != null) p.StartActivity(SceneFrame(sright, sfwd, origin), true);
            Debug.Log($"NFSMW nis: busted, {(scene != null ? $"{scene.Name} '{scene.Description}'" : "default")}, audio {(clip ? clip.name : "none")}, playing {length:0.0} s");
            return p;
        }

        // ---- overlay ---------------------------------------------------------------------------------------------

        void BuildOverlay(string caption, bool letterbox)
        {
            FrontEndData.Load();
            _canvas = FrontEndCanvas.Create("NIS Canvas", null, 8);
            _canvas.transform.SetParent(transform, false);
            if (letterbox)
            {
                _barTop = Bar("Top", 1);
                _barBottom = Bar("Bottom", 0);
            }
            if (!string.IsNullOrEmpty(caption))
            {
                var go = new GameObject("Caption", typeof(RectTransform), typeof(CanvasRenderer));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_canvas.transform, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0);
                rt.anchoredPosition = new Vector2(0, 30);
                rt.sizeDelta = new Vector2(600, 40);
                _caption = go.AddComponent<FEText>();
                _caption.Font = FrontEndData.FontFor("FONT_MW_TITLE.ffn");
                _caption.Justify = 1 | 4;
                _caption.Text = caption;
                _caption.color = new Color(1, 1, 1, 0);
                _caption.raycastTarget = false;
            }
        }

        RectTransform Bar(string name, float edge)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var rt = (RectTransform)go.transform;
            rt.SetParent(_canvas.transform, false);
            rt.anchorMin = new Vector2(0, edge);
            rt.anchorMax = new Vector2(1, edge);
            rt.pivot = new Vector2(0.5f, edge);
            rt.sizeDelta = new Vector2(0, 0);
            var img = go.GetComponent<RawImage>();
            img.color = Color.black;
            img.raycastTarget = false;
            return rt;
        }

        void BuildArrestOverlay(PlayerCar player)
        {
            BuildOverlay(null, false);
            if (!FrontEndData.Packages.ContainsKey("BUSTED_OVERLAY")) return;
            var view = FEView.Open("BUSTED_OVERLAY", _canvas.transform);
            var car = GameSession.ActiveCar?.Car ?? "";
            view.SetText(view.Find(0xF1ED545A), $"SUSPECT VEHICLE: {CarNames.Display(car).ToUpperInvariant()}   APPREHENDED FOR EXCESSIVE SPEEDING AND EVADING POLICE");
        }

        // ---- playback ----------------------------------------------------------------------------------------------

        static float Smooth(float x) => x * x * (3f - 2f * x);

        // NISActivity::Load → EFadeScreenOn(false): black until the first UpdatePlaying posts EFadeScreenOff("30_IN", 600 ms);
        // UpdateLoading holds an arrest in LOADING for 2 s of real time (0x6F7230)
        float _loadHold, _fadeIn = -1f;
        RawImage _fade;
        bool _moment;
        const float FadeInTime = 0.6f;

        void LateUpdate()
        {
            if (_finished) return;
            // QuickGame::OnManageTime: the sim (and the NIS clock, UpdatePlaying's dT) stops while the game is paused
            // (NISActivity::Pause / the FE pause: Time.timeScale 0); an ICE key's sim speed scales the scene clock (Camera
            // SimTimeMultiplier; 33 % and 9 % in some intros)
            bool paused = _paused || Time.timeScale <= 0f;
            float real = paused ? 0f : Time.unscaledDeltaTime;
            float dt = real * (_iceActive ? _iceSim : 1f);
            if (_loadHold > 0f)
            {
                // LOADING: the screen stays black (the load fade) while the hold runs
                _loadHold -= Time.unscaledDeltaTime;
                Fade(1f);
                return;
            }
            if (!_started)
            {
                // NISActivity::OnTask READY_TO_PLAY: the scene holds at its start time (CAnimScene::UpdateTime(0)) until 31
                // ticks of suspension settle, then Play() starts the events (StartEvents) [ours: capped at 0.6 s of real time]
                // (a moment scene is PLAYING from Load)
                if (_settle == 0) NfsMw.Audio.NisSound.ReadyToPlay(this);   // UpdateLoading → READY_TO_PLAY: the NIS sound control state [hook: audio owner]
                _settle++;
                _settleTime += Time.unscaledDeltaTime;
                Fade(1f);
                if (!paused && (_settle >= SettleFrames || _settleTime >= 0.6f || (_chars.Count == 0 && _events == null)))
                {
                    _started = true;
                    if (Scene != null && Scene.SceneType == 2) NfsMw.Audio.SoundState.SetGameFlow(NfsMw.Audio.SoundState.FlowNisBusted, true);   // NISActivity::Play 0x6F587B: SceneType 2 → ESndGameState(eNIS_Busted, true) [hook: gameplay owner]
                    if (!_moment) _events?.Start(_t);
                    // the camera action starts with the scene: the cut to the first key fires "ICECut0"
                    if (_events != null && _iceActive && _iceKey >= 0) _events.FireTag("ICECut" + _iceKey);
                    _fadeIn = 0f;   // UpdatePlaying: IsFadeScreenOn → EFadeScreenOff 30_IN (0.6 s)
                }
                dt = 0;
            }
            if (_fadeIn >= 0f)
            {
                _fadeIn += Time.unscaledDeltaTime;
                if (_fadeIn >= FadeInTime) { Fade(0f); _fadeIn = -1f; }
                else Fade(1f - _fadeIn / FadeInTime);
            }
            _t += dt;
            var kb = Keyboard.current;
            // SkipOverNIS (0x6EA960; decomp NISActivity.cpp:822): only while playing, never an arrest (or ending) NIS,
            // never a non-skippable scene (NISActivity::Load) unless SkipMovies (0x926144, 0 in retail). JoyHandle: the
            // HUD controller config's action 50 [ours: Enter / Space / Esc]
            if (_started && !_arrest && !_nonSkippable && !paused && _t > _start + 0.4f && kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame))
            {
                Skip();
                return;
            }
            // UpdatePlaying (0x6EA750): the scene runs while anim time ≤ scene length (CAnimScene::IsFinished = elapsed >
            // total length; a moment scene ends at elapsed ≥ its length)
            if (_moment ? _t >= Length : _t > Length)
            {
                Finish();
                return;
            }
            if (_started) _events?.Update(_t, dt);
            float k = _rollTime > 0 ? 1f - Mathf.Pow(1f - Mathf.Clamp01((_t - _start) / _rollTime), 3f) : 1f;
            foreach (var r in _rolls) if (!r.Deactivated) Place(r, r.Path != null ? 0.5f : k);
            // CAnimScene::UpdateTime (AnimScene.cpp:401-442): the entities take the PREVIOUS step's time delta (mTimeDelta),
            // one update behind the clock
            foreach (var c in _chars) if (c) c.Advance(_prevDt);
            _prevDt = dt;

            // shots split the length by weight
            float total = 0;
            foreach (var s in _shots) total += s.weight;
            float u = _t / Length * total;
            for (int i = 0; i < _shots.Count; i++)
            {
                var s = _shots[i];
                if (u > s.weight && i < _shots.Count - 1) { u -= s.weight; continue; }
                Vector3 pos, look;
                float fov;
                try { s.fn(Mathf.Clamp01(u / s.weight), out pos, out look, out fov); }
                catch (Exception) { Finish(); return; }   // a car in the shot was removed (world unloaded)
                if (_cam)
                {
                    _cam.transform.position = pos;
                    _cam.transform.LookAt(look);
                    if (_dutch != 0) _cam.transform.Rotate(0, 0, _dutch, Space.Self);   // ComputeBankedUpVector (0x468960) [sign ?]
                    if (_iceActive) ChaseCamera.ApplyProjection(_cam, fov);              // the lens FOV is horizontal (projection 0x6cf52b)
                    else _cam.fieldOfView = fov;
                    if (_iceActive && _iceSampled)
                    {
                        // the noise / ICEShakeTrack jitter in camera space, and the depth of field (ICEMover::Update)
                        IceShot.ApplyJitter(_cam.transform, _iceSample, _t);
                        IceShot.ApplyDof(fov, _iceSample, pos, look);
                    }
                }
                break;
            }

            float fade = Mathf.Clamp01(Mathf.Min(_t - _start, Length - _t) / 0.5f);
            if (_barTop)
            {
                // ICE: each key's LB_height (letterbox x 0.00125 of the screen height per bar [?]); staged shots fade 52 units
                float h = _iceActive ? _iceLetterbox * ((RectTransform)_canvas.transform).rect.height : 52f * fade;
                _barTop.sizeDelta = _barBottom.sizeDelta = new Vector2(0, h);
            }
            if (_caption) _caption.color = new Color(1, 1, 1, Mathf.Clamp01(Mathf.Min(_t - _start - 0.6f, Length - 0.4f - _t) / 0.4f));
        }

        float _prevDt;

        /// <summary>The load fade (FadeScreen): a full-screen black over the NIS canvas at alpha a (0 removes it).</summary>
        void Fade(float a)
        {
            if (!_canvas) return;
            if (a <= 0f) { if (_fade) Destroy(_fade.gameObject); _fade = null; return; }
            if (!_fade)
            {
                var go = new GameObject("FadeScreen", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_canvas.transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                _fade = go.GetComponent<RawImage>();
                _fade.raycastTarget = false;
            }
            _fade.color = new Color(0, 0, 0, Mathf.Clamp01(a));
        }

        // ---- NIS by marker (Lua Drive: NIS.Play / EPlayRaceNIS) [ours: additive API] -----------------------------------

        /// <summary>
        /// NIS.Play (0x604B80) / EPlayRaceNIS at a marker: the scene (NisLibrary) with the player's car as Car1 — world-space
        /// tracks as they are, start-grid tracks around the marker — on the scene's ICE camera track when it has one, else
        /// a slow orbit of the car. Null when the scene does not exist (the script's NISComplete then comes at once).
        /// </summary>
        public static NisPlayer PlayAtMarker(string sceneName, RacePoint marker, Action done)
        {
            if (!Enabled || string.IsNullOrEmpty(sceneName)) return null;
            if (Refused(done)) return null;
            NisLibrary.Load(GameData.Root);
            var scene = NisLibrary.Get(sceneName);
            if (scene == null) { Debug.Log($"NFSMW nis: {sceneName} not in the NIS library (skipped)"); return null; }
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            var origin = marker != null ? Coords.ToUnity(marker.Position) : player ? player.transform.position : Vector3.zero;
            float heading = marker != null ? marker.Rotation : player ? Mathf.Atan2(player.transform.forward.z, player.transform.forward.x) * Mathf.Rad2Deg : 0f;
            var fwd = new Vector3(Mathf.Cos(heading * Mathf.Deg2Rad), 0, Mathf.Sin(heading * Mathf.Deg2Rad));
            var right = Vector3.Cross(Vector3.up, fwd);
            bool world = !scene.UsesStartLine;   // SceneType 1/4
            bool tracked = scene.HaveCarAnimation != 0 && scene.HasTrack("Car1");
            if (world) origin = Vector3.zero; else origin.y = GroundY(origin, origin.y);
            var sceneRot = world ? Quaternion.identity : Quaternion.LookRotation(fwd, Vector3.up);
            float length = scene.Length;
            var p = Begin("Marker " + scene.Name, scene, length, done);
            NfsMw.Audio.NisStreams.QueueNis(scene.Name);
            if (player)
            {
                if (tracked) p.AddRoll(player, Vector3.zero, TrackPath(scene, "Car1", origin, sceneRot), "Car1");
                else if (scene.HaveCarAnimation != 0) p.AddDeactivated(player, "Car1");
            }
            if (scene.HaveCarAnimation != 0) p.PrepareVehicles(scene, origin, sceneRot);
            Vector3 SceneToWorld(Vec3 v) => origin + sceneRot * new Vector3(v.X, v.Z, v.Y);
            var frame = world ? Matrix4x4.identity : SceneFrame(right, fwd, origin);
            var car = player ? player.transform : null;
            // a marker scene (IntroNisToll01 → TollboothMarker) picks ChooseGoodSceneCameraTrackIndex; others play "Track 0"
            int cameraTrack = NisCandidateData.Load(GameData.Root).MarkerType(scene.Name) >= 0 ? -1 : 0;
            if (!p.UseIce(scene.Name, SceneToWorld, car, cameraTrack))
                p._shots.Add((1f, (float u, out Vector3 pos, out Vector3 look, out float fov) =>
                {
                    var c = car ? car.position : origin;
                    pos = c + Quaternion.Euler(0, 120f * u, 0) * (fwd * 9f) + Vector3.up * 2.5f;
                    look = c + Vector3.up * 0.8f;
                    fov = 45f;
                }));
            p.BuildOverlay(null, true);
            p.StartActivity(frame, false);
            Debug.Log($"NFSMW nis: {scene.Name} at {(marker?.Name ?? "the player")}, playing {p.Length:0.0} s");
            return p;
        }

        /// <summary>
        /// A "Moment" NIS (CAnimChooser::Moment = 4): the ENIS-type event handler (0x640F00) starts one only when the
        /// Jump Cams option is on, not in split screen, without the speedbreaker zoom (gGameBreakerCamera), outside races
        /// or in a pursuit race, within 2 s of the ETriggerMomentNIS time, and with an active cop chasing the player
        /// within (cos + 1)·30 + 20 m (cos between the cop's heading and the direction to the player) [? the vectors].
        /// NISActivity::Load (0x6F51F0) then needs Tweak_EnableNISMoments (on) and a marker of the scene's moment type
        /// (CAnimCandidateData, e.g. WMGasStation → GasStationMoment) whose distance + 25 m per turn of heading difference
        /// is under 75 m: a CAnimMomentScene (0x6EF2C0) is created there — an ICE camera only (IsControllingCamera,
        /// no cars, no characters), track ChooseGoodSceneCameraTrackIndex, state PLAYING at once (no 31-tick settle).
        /// Returns null when a gate or the marker search fails (the exe completes the NIS at once).
        /// [hook: the pursuit-breaker owner calls this with the moment scene of the breaker it knocked down]
        /// </summary>
        public static NisPlayer PlayMoment(string sceneName, Action done = null)
        {
            if (!Enabled || Active || string.IsNullOrEmpty(sceneName)) return null;
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (!player || !CameraOptions.JumpCams || ChaseCamera.GameBreakerZoom) return null;
            var race = RaceManager.Active;
            if (race != null && (race.Event == null || race.Event.Type != RaceType.Pursuit)) return null;
            var police = Systems.Police;
            bool copNear = false;
            if (police != null && police.InPursuit && police.Player == player)
                foreach (var c in police.CopCars)
                {
                    if (!c) continue;
                    var d = player.transform.position - c.transform.position;
                    float cos = d.sqrMagnitude > 1e-4f ? Vector3.Dot(c.transform.forward, d.normalized) : 1f;
                    if (d.magnitude < (cos + 1f) * 30f + 20f) { copNear = true; break; }
                }
            if (!copNear) return null;
            NisLibrary.Load(GameData.Root);
            var scene = NisLibrary.Get(sceneName);
            int type = NisCandidateData.Load(GameData.Root).MarkerType(sceneName);
            if (scene == null || type < 0 || type >= NisCandidateData.MarkerNames.Length) return null;
            var t = player.transform;
            var r = new Vec3(t.position.x, t.position.z, t.position.y);
            float heading = Mathf.Atan2(t.forward.z, t.forward.x) * Mathf.Rad2Deg + 90f;   // ChooseArrestLocation-style scene angle
            ushort angle = (ushort)Mathf.RoundToInt(Mathf.Repeat(heading, 360f) / 360f * 65536f);
            if (!TrackMarkers.Load(GameData.Root).Closest(NisCandidateData.MarkerNames[type], r, angle, out var m, out float score) || score >= 75f)
                return null;
            var origin = Coords.ToUnity(m.Position.X, m.Position.Y, m.Position.Z);
            origin.y = GroundY(origin, origin.y);
            float br = m.Angle * 360f / 65536f * Mathf.Deg2Rad;
            var fwd = new Vector3(Mathf.Cos(br), 0, Mathf.Sin(br));
            var right = Vector3.Cross(Vector3.up, fwd);
            Vector3 SceneToWorld(Vec3 v) => origin + fwd * v.Y + right * v.X + Vector3.up * v.Z;
            var p = Begin("Moment " + scene.Name, scene, Mathf.Max(scene.Length, 1f), done);
            if (!p.UseIce(scene.Name, SceneToWorld, t))
            {
                p.Finish(false);
                return null;
            }
            p.BuildOverlay(null, true);
            // CAnimMomentScene (AnimScene.hpp:256-321): an ICE scene that only controls the camera — no cars, no characters,
            // no event sequencer (Play is never called for a moment), PLAYING straight from Load, finished at elapsed ≥ length
            p._settle = SettleFrames;
            p._moment = true;
            Debug.Log($"NFSMW nis: moment {scene.Name} at {NisCandidateData.MarkerNames[type]} ({m.Position.X:0},{m.Position.Y:0}) score {score:0.0}");
            return p;
        }

        public void Finish() => Finish(true);

        /// <summary>
        /// SkipOverNIS: the sequencer completes (Complete, flush), ICECompleteEventTags (0x47B520) fires the "ICECut%d"
        /// tags of the cuts not reached yet, the scene jumps to its end and the NIS completes.
        /// </summary>
        void Skip()
        {
            _skipped = true;
            NfsMw.Audio.NisSound.Skipped();   // SkipOverNIS: Speech::Manager::ClearPlayback + cop speech ReleaseResource [hook: audio owner]
            // EReloadHud / QueueNISButtonThrough (0x4B8C80): a skip re-queues the stream's End section
            if (Scene != null && !string.IsNullOrEmpty(Scene.Name))
            {
                int trackNo = 0;
                if (_iceTrack != null && _iceTrack.Name != null && _iceTrack.Name.StartsWith("Track ")) int.TryParse(_iceTrack.Name.Substring(6), out trackNo);
                try { NfsMw.Audio.NisStreams.QueueNis(Scene.Name, trackNo, buttonThrough: true); } catch (Exception e) { Debug.LogWarning("NFSMW nis: skip stream: " + e.Message); }
            }
            if (_events != null)
            {
                if (_iceActive && _iceTrack != null)
                    for (int k = Math.Max(_iceKey + 1, 0); k < _iceTrack.Keys.Count; k++) _events.FireTag("ICECut" + k);
                _events.Complete(_t);
            }
            // CAnimScene::JumpToEnd → SetTime(mTimeTotalLength) → AnimatedCars_SetTime: every tracked car snaps to its final
            // track pose with an initial SetNISPosition (SetVehicleOnGround, zero speed); entities to their end frame
            _t = Length;
            foreach (var r in _rolls)
            {
                if (r.Ctrl) r.Ctrl.JumpToEnd(Length);
                else if (r.Path != null) Place(r, 1f);
            }
            foreach (var c in _chars) if (c) c.SetTime(Length);
            Finish();
        }

        void Finish(bool notify)
        {
            if (_finished) return;
            _finished = true;
            NfsMw.Audio.NisSound.Finished(this);   // ~NISActivity: EAXSound::NISFinished, "DDAY flying", ESndGameState(eNIS_Busted, false) [hook: audio owner]
            // CDActionIce ends: INIS::FireEventTag("ICEEnd") (SelectAction 0x479900); the sequencer is released with the NIS
            if (_events != null)
            {
                if (_started && _iceActive && !_skipped) _events.FireTag("ICEEnd");
                if (_events.Unhandled.Count > 0)
                    Debug.Log($"NFSMW nis: {Scene?.Name} events fired {_events.Fired}, not ported: {string.Join(", ", _events.Unhandled.Select(kv => kv.Key + " x" + kv.Value))}");
                _events.Dispose();
            }
            foreach (var r in _rolls)
            {
                // AnimatedCars_UnBind / the NISActivity dtor: SetAnimating(false) — a tracked car stays where its track left it
                // (its own behaviours take over); a deactivated car comes back where the race put it (the race re-activates it)
                bool tracked = r.Ctrl;
                if (r.Ctrl) { r.Ctrl.End(); r.Ctrl = null; }
                if (!tracked) Place(r, 1);
                if (r.Hidden != null) foreach (var rend in r.Hidden) if (rend) rend.enabled = true;
                if (r.Flares) { r.Flares.CopLightsOn = r.CopLights0; r.Flares.Headlights = r.Headlights0; }
                if (!r.Body) continue;
                r.Body.isKinematic = r.WasKinematic;
                if (!r.Body.isKinematic && !tracked) { r.Body.linearVelocity = Vector3.zero; r.Body.angularVelocity = Vector3.zero; }
            }
            foreach (var (body, kinematic) in _frozen) if (body) body.isKinematic = kinematic;
            // NISActivity::Release (0x6F75F7): PrecipitationEnable back to the value saved at start, rain override 0
            Precipitation.NisReleased(_precipitationEnable);
            CameraDof.Off();
            foreach (var go in _extras) if (go) Destroy(go);
            _extras.Clear();
            if (_cam)
            {
                _cam.ResetProjectionMatrix();
                _cam.fieldOfView = _fov0;
                _cam.nearClipPlane = _near0;
            }
            if (_chase) { _chase.enabled = true; _chase.Snap(); }
            WorldStreamer.UnactivateOverlay();   // DestroyAnimScene: UnactivateOverlay 0x72DFF0
            if (Active == this) Active = null;
            var done = _done;
            _done = null;
            if (this) Destroy(gameObject);
            if (notify) done?.Invoke();
        }

        void OnDestroy()
        {
            if (!_finished) Finish(false); // torn down with the scene or race
        }
    }
}
