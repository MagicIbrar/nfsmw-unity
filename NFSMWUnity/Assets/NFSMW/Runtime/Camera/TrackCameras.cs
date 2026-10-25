using System.Collections.Generic;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// TrackCarCameraMover (speed.exe ctor 0x47aa20, Init 0x4745e0, Update 0x47de30; specs/camera_core.md §4): the jump,
    /// roadblock and totaled shot. A fixed eye is chosen once beside the road ahead of the car (where the car will be in
    /// 0.5 / 1.3 / 1.0 s for mode 0 normal / 1 cop near / 2 roadblock), then the camera only turns and zooms to keep a
    /// 3.3 m half-size around the car, in slow motion (Camera SimTimeMultiplier 0.2).
    /// </summary>
    public sealed class TrackCarCamera
    {
        static readonly float[] Lead = { 0.5f, 1.3f, 1.0f };            // 0x894714
        static readonly float[] Height = { 0.1f, 3.0f, 3.5f };          // 0x894770 (above the ground; +0.5 after the pick)
        // look offset in the car frame (x forward, y left, z up): 0x89474C / 0x894758 / 0x894764
        static readonly Vector3[] LookOffset = { new Vector3(1.2f, 0, -0.2f), new Vector3(-4f, 0, -0.2f), new Vector3(1.2f, 0, 1f) };
        const float FrameHalf = 3.3f;                                   // "33S@" 0x894738
        public const float SimTime = 0.2f;                              // 0x3E4CCCCD → Camera+0xCC
        static Vector3 _lastEye = new Vector3(1e6f, 0, 0);             // DAT_009110B0

        public int Mode;
        public Vector3 Eye;

        /// <summary>
        /// Init (0x4745e0). Candidates: two points 0.9 of the way from the road centre to each edge (at most 9 m out) at the
        /// lead point, and two points 4 m to either side of where the car will be in 1.5 s. A candidate counts when it
        /// sees the car (or, outside mode 2, the car's position 0.1 s ahead) and is over 3 m from the last TrackCar eye;
        /// road points score their distance to the lead point, side points 0.1 × theirs. No candidate: 2 m above the
        /// current camera.
        /// </summary>
        public void Init(Transform car, Vector3 vel, Vector3 camPos, bool roadblock)
        {
            var P = car.position;
            float speed = vel.magnitude;
            Mode = roadblock ? 2 : AnyCopNear(P, 30f) ? 1 : 0;
            float t = Lead[Mode], t2 = 1.5f;
            if (t * speed > 150f) t = 150f / speed;
            if (t2 * speed > 150f) t2 = 150f / speed;
            var p01 = P + vel * 0.1f;
            var pA = P + vel * t;
            var pB = P + vel * t2;
            var left = -car.right;

            float sA = 0, sB = 0, sL = 0, sR = 0;
            Vector3 cA = default, cB = default;
            if (RoadEdges(P, car.forward, speed * t, out var centre, out var edgeA, out var edgeB))
            {
                cA = Candidate(centre, edgeA);
                cB = Candidate(centre, edgeB);
                if (Sees(cA, P, p01)) sA = Vector3.Distance(cA, pA);
                if (Sees(cB, P, p01)) sB = Vector3.Distance(cB, pA);
            }
            var cL = Lift(pB + left * 4f);
            var cR = Lift(pB - left * 4f);
            if (Sees(cL, P, p01)) sL = Vector3.Distance(pB, cL) * 0.1f;
            if (Sees(cR, P, p01)) sR = Vector3.Distance(pB, cR) * 0.1f;

            if (sA > 0 || sB > 0 || sL > 0 || sR > 0)
                Eye = sL + sA <= sR + sB ? (sR < sB ? cB : cR) : (sL < sA ? cA : cL);
            else
            {
                Eye = camPos + Vector3.up * 2f;
                if (CameraUtil.IsSomethingInBetween(Eye, P)) Eye.y -= 2f;
            }
            Eye.y += 0.5f;
            _lastEye = Eye;
            Rack.Start();
        }

        /// <summary>The shot's rack focus (tCubic at mover +0xA4).</summary>
        public readonly RackFocus Rack = new RackFocus();

        /// <summary>
        /// Update's depth of field (0x47DE30): TargetDistance = |eye − car|, FocalDistance = that + 2 × the rack offset,
        /// DepthOfField 7.5.
        /// </summary>
        public void Dof(float dt, Vector3 eye, Vector3 car)
        {
            float dist = Vector3.Distance(eye, car);
            CameraDof.Set(dist, dist + 2f * Rack.Update(dt), 7.5f);
        }

        Vector3 Candidate(Vector3 centre, Vector3 edge)
        {
            var d = edge - centre;
            float w = d.magnitude, k = w > 10f ? 10f / w * 0.9f : 0.9f;
            return Lift(centre + d * k);
        }

        /// <summary>0x46b590: on the ground (probed from 4 m up) plus the mode's height.</summary>
        Vector3 Lift(Vector3 p)
        {
            if (CameraUtil.Ground(p + Vector3.up * 4f, 40f, out float gy) && gy > p.y) p.y = gy;
            p.y += Height[Mode];
            return p;
        }

        bool Sees(Vector3 c, Vector3 car, Vector3 ahead)
        {
            bool ok = !CameraUtil.IsSomethingInBetween(c, car) || (Mode != 2 && !CameraUtil.IsSomethingInBetween(c, ahead));
            return ok && Vector3.Distance(_lastEye, c) > 3f;
        }

        /// <summary>WRoadNav at the car, advanced by the lead distance: the road centre and both edges there.</summary>
        static bool RoadEdges(Vector3 pos, Vector3 fwd, float ahead, out Vector3 centre, out Vector3 a, out Vector3 b)
        {
            centre = a = b = default;
            try
            {
                var nav = new RoadNav { DecisionFilter = false, HalfWidth = -1.5f };   // corridor inset 0: the real edges
                if (!nav.InitAtPoint(pos, fwd, true, 1f)) return false;
                if (ahead > 0.1f) nav.IncNavPosition(ahead, ahead + 10f);
                var right = Vector3.Cross(Vector3.up, nav.Forward).normalized;
                centre = nav.Centre;
                a = centre + right * nav.Left;
                b = centre + right * nav.Right;
                return true;
            }
            catch (System.Exception) { return false; }
        }

        /// <summary>IsAnyCopNear (GC 0x80074E40): an active cop car within <paramref name="range"/> m.</summary>
        public static bool AnyCopNear(Vector3 p, float range)
        {
            var police = Systems.Police;
            if (police == null) return false;
            foreach (var c in police.CopCars)
                if (c && (c.transform.position - p).sqrMagnitude < range * range) return true;
            return false;
        }

        /// <summary>
        /// Update (0x47de30): look at the car plus the mode's offset in the car frame, FOV = 2·atan(3.3/dist) clamped to
        /// bAngle 800..0x332C (4.39°..71.96°). Returns false when the world hides the car and under 1 s is left, which ends
        /// the shot (director TrackCar timer → −1).
        /// </summary>
        public bool Update(Transform car, out Vector3 eye, out Vector3 look, out float fovH, float timerLeft)
        {
            var P = car.position;
            var o = LookOffset[Mode];
            eye = Eye;
            look = P + car.forward * o.x - car.right * o.y + Vector3.up * o.z;
            float dist = Mathf.Max(1f, Vector3.Distance(Eye, P));
            fovH = Mathf.Clamp(CameraUtil.FrameFov(dist, FrameHalf), 800f * 360f / 65536f, 13100f * 360f / 65536f);
            return !(CameraUtil.IsSomethingInBetween(Eye, P) && timerLeft < 1f);
        }
    }

    /// <summary>
    /// TrackCopCameraMover (speed.exe ctor 0x47ab00, Init 0x475350, FindPursuitVehiclePosition 0x475170, Update 0x47e190;
    /// specs/camera_core.md §5): the pursuit-start shot. Three cubic Bézier curves start at the current camera: the eye
    /// swings round to 5 m past the player on the side away from the nearest pursuing cop (over 1/1.275 s), the look
    /// point pans from the player to the cop and the FOV zooms to frame 3 m round the cop (both over 1/0.765 s).
    /// </summary>
    public sealed class TrackCopCamera
    {
        readonly Vector3[] _eye = new Vector3[4], _look = new Vector3[4];
        readonly float[] _fov = new float[4];
        float _tEye, _tLook, _tFov;

        /// <summary>Init (0x475350). Returns false when no pursuing cop is visible within 300 m (the shot is cut).</summary>
        public bool Init(Vector3 camPos, Vector3 camLook, float camFov, Vector3 player, PlayerCar target = null)
        {
            for (int i = 0; i < 4; i++) { _eye[i] = camPos; _look[i] = camLook; _fov[i] = camFov; }
            _tEye = _tLook = _tFov = 0;
            if (!FindPursuitVehicle(camPos, player, target, out var cop)) return false;

            var d = camPos - camLook;                                    // eye − target
            var v = new Vector2(camLook.x - cop.x, camLook.z - cop.z);   // target − cop, horizontal
            float len = Mathf.Max(0.1f, v.magnitude);
            var u = v / len * 5f;
            float h = Mathf.Min(Mathf.Max(d.y, camLook.y - cop.y), 2.25f) + 0.25f;
            var q = new Vector3(camLook.x + u.x, camLook.y + h, camLook.z + u.y);
            var qd = new Vector3(u.x, h, u.y);
            float cos = Vector3.Dot(d, qd) / Mathf.Max(1e-4f, qd.magnitude * d.magnitude);
            if (cos < 0)
            {
                // swing round the target instead of cutting through it
                float k = (1f - cos) * 0.5f;
                if (d.x * u.y - d.z * u.x < 0) k = -k;
                var p1 = new Vector3(camPos.x - d.z * k, camPos.y, camPos.z + d.x * k);
                var p2 = new Vector3(camLook.x + u.y * k + u.x, q.y, camLook.z - u.x * k + u.y);
                if (!CameraUtil.IsSomethingInBetween(p1, camLook) && !CameraUtil.IsSomethingInBetween(p2, camLook))
                {
                    _eye[1] = p1; _eye[2] = p2; _eye[3] = q;
                }
            }
            _look[3] = cop;
            Rack.Start();
            float dist = Mathf.Max(1f, Vector3.Distance(_eye[3], cop));
            _fov[3] = Mathf.Clamp(CameraUtil.FrameFov(dist, 3f), 1000f * 360f / 65536f, 13100f * 360f / 65536f);
            return true;
        }

        /// <summary>
        /// FindPursuitVehiclePosition (0x475170): over the active vehicles (list 0x92CE9C, IsActive vt+0x88) of class CAR
        /// whose IVehicleAI pursuit (vt+0x64) is live (+0x2C) and targets this view's player (pursuit +0x1C → vt+0x70 ==
        /// anchor +0x94), with IPursuitAI::GetInPursuit (vt+0x14) and GetTimeSinceTargetSeen (vt+0x74) ≤ 0 (the cop sees the
        /// player right now): the one nearest the player under 300 m whose position (the sim body position, no height offset)
        /// the camera sees (IsSomethingInBetween 0x468A90 from Camera +0x40).
        /// [? the +0.7 m lift: the Unity cop transform sits at the ground, the sim position at the body centre]
        /// </summary>
        static bool FindPursuitVehicle(Vector3 cam, Vector3 player, PlayerCar target, out Vector3 pos)
        {
            pos = default;
            var police = Systems.Police;
            if (police == null || !police.InPursuit) return false;
            float best = 300f;
            foreach (var c in police.CopCars)
            {
                if (!c || !c.isActiveAndEnabled) continue;
                var ai = c.GetComponent<NfsMw.Game.Police.CopCar>();
                if (!ai || !ai.InPursuit || ai.Pursuit == null || (target && ai.Pursuit.Target != target) || ai.TimeSinceSeen > 0f) continue;
                var p = c.transform.position + Vector3.up * 0.7f;
                if (CameraUtil.IsSomethingInBetween(cam, p)) continue;
                float dd = Vector3.Distance(player, p);
                if (dd < best) { best = dd; pos = p; }
            }
            return best < 300f;
        }

        public readonly RackFocus Rack = new RackFocus();

        /// <summary>The rack focus as TrackCar, on the look point (the cop at the end of the pan).</summary>
        public void Dof(float dt, Vector3 eye, Vector3 look)
        {
            float dist = Vector3.Distance(eye, look);
            CameraDof.Set(dist, dist + 2f * Rack.Update(dt), 7.5f);
        }

        /// <summary>Update (0x47e190).</summary>
        public void Update(float dt, out Vector3 eye, out Vector3 look, out float fovH)
        {
            _tEye = Mathf.Min(1f, _tEye + dt * 1.275f);
            _tLook = Mathf.Min(1f, _tLook + dt * 0.765f);
            _tFov = Mathf.Min(1f, _tFov + dt * 0.765f);
            eye = Bezier(_eye, _tEye);
            look = Bezier(_look, _tLook);
            fovH = Bezier(_fov, _tFov);
        }

        /// <summary>
        /// Bezier::GetPoint (0x46b6b0): (s³, s², s, 1)·B·P with s = 1 − t and the basis B the TrackCop ctor (0x47AB00)
        /// stores at +0x90 = rows (1, −3, 3, −1), (0, 3, −6, 3), (0, 0, 3, −3), (0, 0, 0, 1): the Bernstein weights
        /// (s³, 3s²t, 3st², t³), P0 at t = 0.
        /// </summary>
        static Vector3 Bezier(IList<Vector3> p, float t)
        {
            float s = 1f - t;
            return s * s * s * p[0] + 3f * s * s * t * p[1] + 3f * s * t * t * p[2] + t * t * t * p[3];
        }

        static float Bezier(IList<float> p, float t)
        {
            float s = 1f - t;
            return s * s * s * p[0] + 3f * s * s * t * p[1] + 3f * s * t * t * p[2] + t * t * t * p[3];
        }
    }

    /// <summary>
    /// The TrackCar/TrackCop rack focus (Init 0x4745E0 0x475015..0x475094): the focus offset (tCubic1D at mover +0xA4,
    /// duration 0.42 s 0x3ED70A3D, desired 0, flat ends) starts at the integer 26 + bRandom(19) m (26..44), negated when
    /// bRandom(2) ≠ 0 (a coin toss), and eases to 0.
    /// </summary>
    public sealed class RackFocus
    {
        float _from, _t = 1f;

        public void Start()
        {
            _from = (26 + Random.Range(0, 19)) * (Random.Range(0, 2) != 0 ? -1f : 1f);
            _t = 0f;
        }

        public float Update(float dt)
        {
            if (_t >= 1f) return 0f;
            _t = Mathf.Min(1f, _t + dt / 0.42f);
            float e = _t * _t * (3f - 2f * _t);
            return _from * (1f - e);
        }
    }
}
