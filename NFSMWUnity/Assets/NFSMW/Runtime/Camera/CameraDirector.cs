using System.Collections.Generic;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw
{
    /// <summary>The camera actions a director can run (CDAction* factories registered under these names).</summary>
    public enum CameraAction { Drive, TrackCar, TrackCop, Ice }

    /// <summary>
    /// CameraAI::Director (speed.exe ctor 0x480d20, Update 0x479de0, SelectAction 0x479900; specs/camera_core.md §3): one
    /// per player view. It picks the camera action every frame from two timers and the NIS state: the TrackCar timer
    /// (+0x2B8: jump, roadblock and totaled shots), the pursuit timer (+0x2B4: the pursuit-start TrackCop shot) and a
    /// NIS scene that controls the camera (ICE). <see cref="Slowdown"/> (+0x2C0) is handed to the drive action each
    /// frame (CDActionDrive::SetSpecial 0x481200 → cinematic slow motion and dolly zoom).
    /// </summary>
    public sealed class CameraDirector
    {
        public CameraAction Action { get; private set; } = CameraAction.Drive;
        /// <summary>The action changed (in <see cref="Update"/> or an event entry point) and its mover needs an Init; see <see cref="Ack"/>.</summary>
        public bool Changed { get; private set; }

        /// <summary>The host has initialised the new action's mover.</summary>
        public void Ack() => Changed = false;
        /// <summary>+0x2BC at the moment the current action was created: the drive action starts fully slowed and fades out.</summary>
        public bool EnteredSmooth { get; private set; }

        /// <summary>+0x2B8: TrackCar shot time left; set negative to end it (it ends when it drops below 0).</summary>
        public float TrackCarTimer;
        /// <summary>+0x2B4: 2.5 at a pursuit start; TrackCop plays while it is in (0, 2).</summary>
        public float PursuitTimer;
        /// <summary>+0x2BC: the next action inherits a slowed camera (set when a timed shot ends).</summary>
        public bool SmoothReturn;
        /// <summary>+0x2C0: seconds of cinematic slowdown the drive action ramps in (CameraAI::StartCinematicSlowdown 0x471b60).</summary>
        public float Slowdown;

        /// <summary>gGameBreakerCamera (0x911055): the speedbreaker zoom is up, so no jump/pursuit/roadblock shots.</summary>
        public bool GameBreaker;
        /// <summary>The drive anchor's mIsCloseToRoadBlock (+0xC3), speed (+0x10) and mIsTouchingGround (+0xC0).</summary>
        public bool CloseToRoadblock, Grounded;
        public float Speed;
        /// <summary>DAT_00911056: the next TrackCar shot frames a roadblock (TrackCarCameraMover mode 2).</summary>
        public bool RoadblockShot;

        /// <summary>
        /// TheICEManager.nPlayGenericGroupHash / the generic track (ICEManager::SetGenericCameraToPlay, from ECinematicMoment
        /// 0x640550 and Camera.SetGenericCamera 0x604A50): while set, SelectAction picks CDActionIce; cleared when the
        /// track's parameter reaches 1.
        /// </summary>
        public string GenericGroup { get; private set; }
        public string GenericTrack { get; private set; }
        public bool GenericRequested => !string.IsNullOrEmpty(GenericGroup);

        /// <summary>ICEManager::SetGenericCameraToPlay(group, track); ("", "") clears it.</summary>
        public void SetGenericCamera(string group, string track)
        {
            GenericGroup = string.IsNullOrEmpty(group) ? null : group;
            GenericTrack = track;
        }

        /// <summary>CameraAI::Reset (0x47cc50): timers cleared, back to the drive camera.</summary>
        public void Reset()
        {
            TrackCarTimer = PursuitTimer = Slowdown = 0;
            SmoothReturn = EnteredSmooth = false;
            Action = CameraAction.Drive;
            Changed = false;
            GenericGroup = GenericTrack = null;
        }

        /// <summary>
        /// Director::Update (0x479de0): the timers run while the game is not paused, then SelectAction. <paramref name="cams"/>
        /// is the "jump cams" gate (0x469e30: the profile option, off in split screen) that also gates the roadblock shot.
        /// </summary>
        public void Update(float dt, bool paused, bool cams, bool nisActive)
        {
            if (!paused && TrackCarTimer > 0) TrackCarTimer -= dt;
            if (!paused && PursuitTimer > 0) PursuitTimer -= dt;
            Select(cams, nisActive);
        }

        void Select(bool cams, bool nisActive)
        {
            var want = Action;
            if (TrackCarTimer < 0) { TrackCarTimer = 0; want = CameraAction.Drive; SmoothReturn = true; }
            if (PursuitTimer < 0) { PursuitTimer = 0; want = CameraAction.Drive; SmoothReturn = true; }
            if (PursuitTimer > 0 && PursuitTimer < 2f) want = CameraAction.TrackCop;
            // roadblock shot: only from the drive camera (view mover type CM_DRIVE_CUBIC)
            if (!GameBreaker && Action == CameraAction.Drive && cams && (Speed > 20f || (Speed > 10f && Grounded)) && CloseToRoadblock)
            {
                want = CameraAction.TrackCar;
                TrackCarTimer = 2f;   // + MGamePlayMoment "MomentStrm" (audio moment)
            }
            if (nisActive) { want = CameraAction.Ice; TrackCarTimer = PursuitTimer = 0; }
            else if (GenericRequested) want = CameraAction.Ice;                 // the generic ICE camera
            else if (Action == CameraAction.Ice) want = CameraAction.Drive;   // + INIS FireEventTag("ICEEnd"), MICECameraFinished
            if (want != Action) Set(want);
        }

        void Set(CameraAction a)
        {
            EnteredSmooth = SmoothReturn;   // the new action's ctor reads +0x2BC before it is cleared
            Action = a;
            SmoothReturn = false;
            Slowdown = 0;
            Changed = true;
        }

        /// <summary>Director::TotaledStart (0x479d80): a 2 s TrackCar shot.</summary>
        public void TotaledStart()
        {
            TrackCarTimer = 2f;
            if (Action != CameraAction.Ice) Set(CameraAction.TrackCar);
        }

        /// <summary>Director::JumpStart (0x469e80): TrackCar time = seconds × 4/3.</summary>
        public void JumpStart(float seconds) => TrackCarTimer = seconds * 1.3333334f;

        /// <summary>Director::EndJumping (0x469ea0): cut the shot short when under 1 s is left.</summary>
        public void EndJumping() { if (TrackCarTimer < 1f) TrackCarTimer = -1f; }

        /// <summary>
        /// Director::PursuitStart (0x469ec0): unless one is running, 2.5 s on the pursuit timer (0.5 s of slowed drive camera,
        /// then 2 s of TrackCop) and a 0.5 s cinematic slowdown; posts MGamePlayMoment "MomentStrm" and MMiscSound(1).
        /// </summary>
        public void PursuitStart()
        {
            if (PursuitTimer > 0) return;
            PursuitTimer = 2.5f;
            Slowdown = 0.5f;
        }

        /// <summary>Director::EndPursuitStart (0x469fa0): end the TrackCop shot.</summary>
        public void EndPursuitStart() { if (PursuitTimer < 2f) PursuitTimer = -1f; }

        /// <summary>CameraAI::StartCinematicSlowdown (0x471b60): only while the drive camera runs (photo finish passes 0.75 s).</summary>
        public void StartCinematicSlowdown(float seconds) { if (Action == CameraAction.Drive) Slowdown = seconds; }

        /// <summary>
        /// CameraAI::MaybeDoPursuitCam (0x471bf0, from AIPursuit::OnAttached): ICE editor off, the cams gate, no NIS, no race
        /// or a non-pursuit race past its first second, the perp not in a drag race, no speedbreaker zoom.
        /// </summary>
        public void MaybeDoPursuitCam(bool cams, PlayerCar perp)
        {
            if (!cams || NisPlayer.Active || GameBreaker) return;
            var race = RaceManager.Active;
            if (race != null && race.RaceTime < 1f) return;
            if (perp && perp.Dragster) return;
            PursuitStart();
        }

        /// <summary>CameraAI::MaybeDoTotaledCam (0x479f20, from EShowRaceOverMessage when the racer is totaled).</summary>
        public void MaybeDoTotaledCam(bool splitScreen) { if (!splitScreen) TotaledStart(); }

        /// <summary>
        /// CameraAI::MaybeDoJumpCam (0x479f80, from EPlayerAirborne): ICE off, the cams gate, no NIS, not a drag race, the
        /// TrackCar timer idle, no speedbreaker zoom and ≥ 10 m/s. The trajectory is predicted for 3 s (0x471cd0): average height
        /// h̄ in (1, 20.1) m, peak height H &lt; 20.1 m; the shot plays when H &gt; 2.5 m (1.8 m inside a jump zone) and
        /// flight time × speed &gt; 25 m (15 m), for clamp(2·h̄, 1, 4) × 4/3 s.
        /// </summary>
        public bool MaybeDoJumpCam(bool cams, PlayerCar car)
        {
            if (!cams || NisPlayer.Active || !car || car.Dragster || GameBreaker) return false;
            if (TrackCarTimer > 0) return false;   // Director::IsJumping (0x4677f0)
            var rb = car.GetComponent<Rigidbody>();
            if (!rb) return false;
            float speed = rb.linearVelocity.magnitude;
            if (speed < 10f) return false;
            // TrackPathManager::FindZone(position, TRACK_PATH_ZONE_JUMP_CAM 0xC) (0x727480): a 2-D point-in-polygon test
            bool inZone = InJumpZone(car.transform.position);
            float avg = PredictJump(car.transform.position, rb.linearVelocity, 3f, out float peak, out float flight);
            if (avg >= 20.1f || peak >= 20.1f || flight >= 3.1f || avg <= 1f) return false;
            // thresholds 0x894658 / 0x894660 indexed by "in a jump zone"
            if (peak <= (inZone ? 1.8f : 2.5f) || flight * speed <= (inZone ? 15f : 25f)) return false;
            Set(CameraAction.TrackCar);
            // the shot lasts clamp(2 · flight time, 1, 4) · 4/3 s (0x47A0B4: the second out-parameter, not the average height)
            TrackCarTimer = Mathf.Clamp(2f * flight, 1f, 4f) * 1.3333334f;
            return true;
        }

        static List<Core.TrackPathZone> _jumpZones;

        /// <summary>A TRACK_PATH_ZONE_JUMP_CAM zone (22 in L2RA) contains the point (game X/Y = Unity x/z).</summary>
        public static bool InJumpZone(Vector3 p)
        {
            if (_jumpZones == null)
            {
                _jumpZones = new List<Core.TrackPathZone>();
                try
                {
                    foreach (var z in Core.TrackPathZones.Load(GameData.Root))
                        if (z.Type == Core.TrackPathZones.JumpCam) _jumpZones.Add(z);
                }
                catch (System.Exception) { }
            }
            foreach (var z in _jumpZones) if (z.Contains(p.x, p.z)) return true;
            return false;
        }

        /// <summary>
        /// 0x471cd0 (simplified): steps the ballistic arc for up to <paramref name="maxTime"/> s and returns the average height
        /// above the ground; <paramref name="peak"/> is the highest point, <paramref name="flight"/> the time until it lands.
        /// </summary>
        public static float PredictJump(Vector3 pos, Vector3 vel, float maxTime, out float peak, out float flight)
        {
            const int steps = 24;
            float dt = maxTime / steps, sum = 0;
            int n = 0;
            peak = 0;
            flight = 0;
            var g = Physics.gravity;
            for (int i = 1; i <= steps; i++)
            {
                float t = i * dt;
                var p = pos + vel * t + 0.5f * t * t * g;
                if (!CameraUtil.Ground(p + Vector3.up * 2f, 60f, out float gy)) gy = pos.y - 30f;
                float h = p.y - gy;
                flight = t;
                if (h <= 0) break;
                sum += h;
                n++;
                peak = Mathf.Max(peak, h);
            }
            return n > 0 ? sum / n : 0;
        }
    }

    /// <summary>Shared camera queries (CameraMover helpers).</summary>
    public static class CameraUtil
    {
        /// <summary>Static world only: dynamic bodies (cars, smackables) never block a camera line.</summary>
        public static bool IsWorld(RaycastHit h) => h.collider && !h.collider.isTrigger && (h.rigidbody == null || h.rigidbody.isKinematic && !h.rigidbody.GetComponent<PlayerCar>());

        /// <summary>CameraMover::IsSomethingInBetween (0x4689e0 / 0x468a90): the world blocks the line from a to b.</summary>
        public static bool IsSomethingInBetween(Vector3 a, Vector3 b)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 1e-3f) return false;
            foreach (var h in Physics.RaycastAll(a, d / len, len, ~0, QueryTriggerInteraction.Ignore))
                if (IsWorld(h)) return true;
            return false;
        }

        /// <summary>The highest world surface under a point (down to <paramref name="depth"/> m).</summary>
        public static bool Ground(Vector3 from, float depth, out float y)
        {
            y = 0;
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(from, Vector3.down, depth, ~0, QueryTriggerInteraction.Ignore))
                if (IsWorld(h) && h.distance < best) { best = h.distance; y = h.point.y; }
            return best < float.MaxValue;
        }

        /// <summary>bATan(x, y)·2 as a FOV in degrees: the angle that frames a half-size of y at distance x.</summary>
        public static float FrameFov(float dist, float halfSize) => 2f * Mathf.Atan2(halfSize, Mathf.Max(1f, dist)) * Mathf.Rad2Deg;
    }
}
