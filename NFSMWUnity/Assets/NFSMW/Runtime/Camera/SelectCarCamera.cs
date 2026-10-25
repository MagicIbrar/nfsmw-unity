using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// SelectCarCameraMover (ctor 0x476870, Update 0x4769E0, SetDesiredOrientation 0x4770B0, SetCurrentOrientation
    /// 0x46B920, Set{V,H}RotateSpeed 0x476E70 / 0x476F30, SetZoomSpeed 0x476FF0, FindBestAngleGoal 0x46B980,
    /// CreateCameraMatrix 0x46B7E0; specs/camera_core.md §9): the front-end garage orbit, driven by GarageMainScreen
    /// (UpdateCurrentCameraView 0x7A94C0, HandleJoyEvents 0x7A2310, mouse 0x7A2010). The orientation is
    /// {OrbitV (polar angle, 90 = level), OrbitH (yaw), Radius, Roll, FOV (horizontal), LookAt (car space, game axes x
    /// forward, y left, z up)}; Current +0x90, Start +0xC0, Goal +0xF0; speeds +0x120 zoom / +0x124 V / +0x128 H;
    /// damping +0x12C (10), periods +0x130 (2), time +0x134, total +0x138; mode +0x80: 0 animate, 1 user, 2 static.
    /// [ours] a plain class: Runtime/UI/CarShowroom (the FE owner's) keeps its own copy of mode 0; this is the full mover
    /// for it to call (see the round-9 report for the hook).
    /// </summary>
    public sealed class SelectCarCamera
    {
        public struct Orientation
        {
            public float OrbitV, OrbitH, Radius, Roll, Fov;
            public Vector3 LookAt;   // game car space (x forward, y left, z up)
        }

        public Orientation Current, Start, Goal;
        public float ZoomSpeed, VSpeed, HSpeed;      // +0x120 / +0x124 / +0x128
        public float Damping = 10f;                  // +0x12C
        public int Periods = 2;                      // +0x130
        public float Time, Total = 1f;               // +0x134 / +0x138
        public int Mode = 2;                         // +0x80
        /// <summary>CarGuysCamera (0x926134): no orbit clamps in user mode (a debug switch, off in retail).</summary>
        public static bool CarGuysCamera;

        /// <summary>FindBestAngleGoal (0x46B980): goal, goal + 360 or goal − 360, whichever is nearest the start.</summary>
        public static float BestAngleGoal(float start, float goal)
        {
            float d0 = Mathf.Abs(start - goal), dp = Mathf.Abs(start - (goal + 360f)), dm = Mathf.Abs(start - (goal - 360f));
            if (dp < d0 && dp < dm) return goal + 360f;
            if (dm < d0 && dm < dp) return goal - 360f;
            return goal;
        }

        /// <summary>SetCurrentOrientation (0x46B920): Current = the arguments (no mode change).</summary>
        public void SetCurrentOrientation(Vector3 orbit, float roll, float fov, Vector3 lookAt)
        {
            Current.OrbitV = orbit.x; Current.OrbitH = orbit.y; Current.Radius = orbit.z;
            Current.Roll = roll; Current.Fov = fov; Current.LookAt = lookAt;
        }

        /// <summary>
        /// SetDesiredOrientation (0x4770B0): mode 0, Start = Goal = Current, then Goal = the arguments; OrbitV, OrbitH and
        /// Roll take FindBestAngleGoal from Start; damping / periods; time 0, total = animSpeed.
        /// </summary>
        public void SetDesiredOrientation(Vector3 orbit, float roll, float fov, Vector3 lookAt, float animSpeed, float damping, int periods)
        {
            Mode = 0;
            Start = Current;
            Goal = new Orientation { OrbitV = orbit.x, OrbitH = orbit.y, Radius = orbit.z, Roll = roll, Fov = fov, LookAt = lookAt };
            Damping = damping;
            Periods = periods;
            Goal.OrbitV = BestAngleGoal(Start.OrbitV, Goal.OrbitV);
            Goal.OrbitH = BestAngleGoal(Start.OrbitH, Goal.OrbitH);
            Goal.Roll = BestAngleGoal(Start.Roll, Goal.Roll);
            Time = 0f;
            Total = animSpeed;
        }

        /// <summary>
        /// The user speeds (0x476E70 / 0x476F30 / 0x476FF0): ignored while animating (mode 0); from static (mode 2) the
        /// roll, FOV and look-at first re-aim at roll 0 (shortest way), FOV 45, look-at (0, 0, 0.75) over 0.555 s
        /// (0x3F0E147B); then mode 1 with the new speed.
        /// </summary>
        public void SetVRotateSpeed(float s) { if (EnterUser()) VSpeed = s; }
        public void SetHRotateSpeed(float s) { if (EnterUser()) HSpeed = s; }
        public void SetZoomSpeed(float s) { if (EnterUser()) ZoomSpeed = s; }

        bool EnterUser()
        {
            if (Mode == 0) return false;
            if (Mode != 1)
            {
                Start.Roll = Current.Roll; Start.Fov = Current.Fov;
                Goal.Roll = 0f; Goal.Fov = 45f;
                Start.LookAt = Current.LookAt;
                Goal.LookAt = new Vector3(0f, 0f, 0.75f);
                Goal.Roll = BestAngleGoal(Start.Roll, Goal.Roll);
                Time = 0f;
                Total = 0.555f;
            }
            Mode = 1;
            return true;
        }

        static float Wrap360(float a)
        {
            if (a > 360f) a -= 360f;
            if (a < 0f) a += 360f;
            return a;
        }

        /// <summary>
        /// Update (0x4769E0). Unless static: time += dT, u = time/total (1 when total ≤ 0 or past it), k = 1 −
        /// e^(−u²·damping)·cos((periods + ½)·π·u²). Mode 0: every channel Start + (Goal − Start)·k, yaw and roll wrapped to
        /// 0..360, mode 2 at u ≥ 1. Mode 1: OrbitH += 60·dT·HSpeed (wrapped), OrbitV = clamp(OrbitV + 60·dT·VSpeed, 94.5, 110),
        /// Radius = clamp(Radius + 60·dT·ZoomSpeed·0.125, 4.65, 6.65) (no clamps with CarGuysCamera); roll, FOV and look-at
        /// keep easing with k.
        /// </summary>
        public void Update(float dt)
        {
            if (Mode == 2) return;
            Time += dt;
            float u = Total > 0f && Time < Total ? Time / Total : 1f;
            float u2 = u * u;
            float k = 1f - Mathf.Exp(-u2 * Damping) * Mathf.Cos((Periods + 0.5f) * u2 * 3.1415925f);
            float j = 1f - k;
            if (Mode == 1)
            {
                float a = dt * 60f;
                Current.OrbitH += a * HSpeed;
                float v = Current.OrbitV + a * VSpeed;
                if (!CarGuysCamera) v = Mathf.Clamp(v, 94.5f, 110f);
                Current.OrbitV = v;
                float r = Current.Radius + a * ZoomSpeed * 0.125f;
                if (!CarGuysCamera) r = Mathf.Clamp(r, 4.65f, 6.65f);
                Current.Radius = r;
                Current.Roll = j * Start.Roll + k * Goal.Roll;
                Current.Fov = j * Start.Fov + k * Goal.Fov;
                Current.LookAt = Start.LookAt + (Goal.LookAt - Start.LookAt) * k;
                Current.OrbitH = Wrap360(Current.OrbitH);
                return;
            }
            Current.OrbitV = j * Start.OrbitV + k * Goal.OrbitV;
            Current.OrbitH = j * Start.OrbitH + k * Goal.OrbitH;
            Current.Radius = j * Start.Radius + k * Goal.Radius;
            Current.Roll = j * Start.Roll + k * Goal.Roll;
            Current.Fov = j * Start.Fov + k * Goal.Fov;
            Current.LookAt = Start.LookAt + (Goal.LookAt - Start.LookAt) * k;
            Current.OrbitH = Wrap360(Current.OrbitH);
            Current.Roll = Wrap360(Current.Roll);
            if (u >= 1f) Mode = 2;
        }

        /// <summary>
        /// CreateCameraMatrix (0x46B7E0): M = RotZ(OrbitH)·RotX(OrbitV)·T(0, 0, Radius) (D3DX row vectors), eye = the
        /// translation of M⁻¹ = (−R·sinV·sinH, −R·sinV·cosH, −R·cosV) in the car's game frame (an orbit about the car
        /// origin, above the ground for OrbitV > 90); look-at = LookAt; up = world up banked by Roll about the view axis
        /// (ComputeBankedUpVector 0x468960). Camera FOV = Fov, TargetDistance = Radius. Returned in game car space.
        /// </summary>
        public void Pose(out Vector3 eye, out Vector3 look, out Vector3 up)
        {
            float v = Current.OrbitV * Mathf.Deg2Rad, h = Current.OrbitH * Mathf.Deg2Rad, r = Current.Radius;
            eye = new Vector3(-r * Mathf.Sin(v) * Mathf.Sin(h), -r * Mathf.Sin(v) * Mathf.Cos(h), -r * Mathf.Cos(v));
            look = Current.LookAt;
            var axis = look - eye;
            up = axis.sqrMagnitude > 1e-8f ? Quaternion.AngleAxis(Current.Roll, axis.normalized) * Vector3.forward : Vector3.forward;   // game z up [? bank sign]
        }

        /// <summary><see cref="Pose"/> placed on a Unity car transform (game x forward, y left, z up → Unity z, −x, y).</summary>
        public void Place(Transform cam, Transform car)
        {
            if (!cam || !car) return;
            Pose(out var e, out var l, out var u);
            Vector3 U(Vector3 g) => new Vector3(-g.y, g.z, g.x);
            var eye = car.TransformPoint(U(e));
            var look = car.TransformPoint(U(l));
            var up = car.TransformDirection(U(u));
            cam.position = eye;
            var dir = look - eye;
            if (dir.sqrMagnitude > 1e-8f) cam.rotation = Quaternion.LookRotation(dir, up);
        }
    }
}
