using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// The player view's Camera depth-of-field keys (CameraParams +0xB0 TargetDistance, +0xB4 FocalDistance, +0xB8
    /// DepthOfField; specs/camera_core.md §1, §5, §7.3). Written by the TrackCar/TrackCop rack focus and by the ICE
    /// cameras (aperture/focus channels); <see cref="Enabled"/> is false when the shot has no depth of field (aperture
    /// ≥ 37, or the drive camera). [ours] A static mailbox: the renderer's DOF pass (world-render owner) reads it.
    /// </summary>
    public static class CameraDof
    {
        public static bool Enabled { get; private set; }
        public static float TargetDistance { get; private set; }
        public static float FocalDistance { get; private set; }
        public static float DepthOfField { get; private set; }

        public static void Set(float target, float focal, float depth)
        {
            Enabled = true;
            TargetDistance = target;
            FocalDistance = focal;
            DepthOfField = depth;
        }

        public static void Off()
        {
            Enabled = false;
            FocalDistance = DepthOfField = 0f;
        }
    }

    /// <summary>
    /// Shared parts of ICEMover::Update (0x482EC0) for the NIS and generic ICE cameras (specs/camera_core.md §7.3, §7.6):
    /// the noise / ICEShakeTrack camera-space jitter, the depth of field from the aperture and focus channels, and the
    /// key's world / car constraints (terrainClamp 0x470FE0, obstructionRaise 0x47C6F0).
    /// </summary>
    public static class IceShot
    {
        static IceCameras _data;
        /// <summary>GLOBAL\InGameB.bun's ICE tracks and shake tracks, loaded once.</summary>
        public static IceCameras Data => _data ??= IceCameras.Load(GameData.Root);

        /// <summary>
        /// The key's camera-space jitter, applied after the look-at. A key with nShakeType plays that ICEShakeTrack
        /// (0x483702..): key = (int)(t · frequency · 30) mod NumKeys, rotation = the quaternion (q·amplitude, w) and offset
        /// p·amplitude, both in camera space (bMulMatrix(cam, shake, cam)). Otherwise NoiseAmplitude1 = (0, 0, amp, amp),
        /// NoiseFrequency1 = (0, 0, freq·1.618, freq·1.382), layer 2 zero: channels z / w of Camera::ApplyNoise, i.e. pitch
        /// and yaw jitter in degrees (CameraNoise.Apply) [? exe camera axes → Unity: x right, y up, z forward].
        /// </summary>
        public static void ApplyJitter(Transform cam, in IceCameras.Sample s, float t)
        {
            if (!cam || s.Key == null || s.NoiseAmp <= 0f) return;
            if (s.Key.ShakeType != 0 && Data.Shakes.TryGetValue(s.Key.ShakeType, out var track) && track.Count > 0)
            {
                int n = track.Count;
                int i = (int)(t * s.NoiseFreq * 30f);
                i = ((i % n) + n) % n;   // 0x469330: wraps negatives too
                var v = new Vector3(track.Q[i * 3], track.Q[i * 3 + 1], track.Q[i * 3 + 2]) * s.NoiseAmp;
                float w = Mathf.Sqrt(Mathf.Max(0f, 1f - v.magnitude));   // 0x4836F8: w = sqrt(1 - sqrt(|v|^2)) as written
                var p = new Vector3(track.P[i * 3], track.P[i * 3 + 1], track.P[i * 3 + 2]) * s.NoiseAmp;
                cam.position += cam.rotation * p;
                cam.rotation = cam.rotation * new Quaternion(v.x, v.y, v.z, w);
                return;
            }
            if (s.Key.ShakeType != 0) return;   // GetShakeTrack (0x478290) found nothing: no shake at all
            // Camera::ApplyNoise (0x467420 via 0x48390E) with the exe's integer-hash noise and FovRelativeAngle at the key's FOV
            float f = s.NoiseFreq, a = s.NoiseAmp;
            CameraNoise.Apply(cam, new Vector4(0f, 0f, f * 1.618f, f * 1.382f), new Vector4(0f, 0f, a, a), Vector4.zero, Vector4.zero, t, s.FovDeg);
        }

        /// <summary>
        /// The depth-of-field keys of an ICE sample (ICEMover::Update tail): TargetDistance = |eye − look|; off for an
        /// aperture outside 0..37 (DAT_00911020, the DOF debug override, is not set in retail).
        /// </summary>
        public static void ApplyDof(float fovH, in IceCameras.Sample s, Vector3 eye, Vector3 look)
        {
            float target = Vector3.Distance(eye, look);
            if (IceCameras.DepthOfField(fovH, s.Focal, s.Aperture, out float focal, out float depth)) CameraDof.Set(target, focal, depth);
            else CameraDof.Off();
        }
    }

    /// <summary>
    /// The objects the camera keeps out of (list DAT_00911050): every active vehicle (PVehicle activation 0x6864FB adds
    /// it) and the smackables whose CAMERA_AVOIDABLE (0xE9D83D0C) is set (0x66AF24) [smackables: not ported, the
    /// attribute is not read by the smackable owner]. Refreshed every 30 frames [ours].
    /// </summary>
    public static class CameraAvoidables
    {
        static readonly List<PlayerCar> Cars = new List<PlayerCar>();
        static int _frame = -100;

        public static List<PlayerCar> Get()
        {
            if (Time.frameCount - _frame >= 30 || Time.frameCount < _frame)
            {
                _frame = Time.frameCount;
                Cars.Clear();
                foreach (var c in Object.FindObjectsByType<PlayerCar>(FindObjectsSortMode.None))
                    if (c && c.isActiveAndEnabled) Cars.Add(c);
            }
            Cars.RemoveAll(c => !c);
            return Cars;
        }

        /// <summary>An object's half extents and frame (the box collider when it has one).</summary>
        public static bool Box(PlayerCar c, out Vector3 centre, out Vector3 half, out Quaternion rot)
        {
            rot = c.transform.rotation;
            var box = c.GetComponent<BoxCollider>();
            if (box)
            {
                centre = c.transform.TransformPoint(box.center);
                half = Vector3.Scale(box.size * 0.5f, c.transform.lossyScale);
            }
            else
            {
                centre = c.transform.position + Vector3.up * 0.7f;
                half = new Vector3(0.95f, 0.7f, 2.3f);
            }
            return true;
        }
    }

    /// <summary>
    /// CubicCameraMover obstructionRaise (0x47C6F0, with the test 0x47C250): the eye is raised (up to 16 times) out of
    /// the "dome" over every avoidable object: in the object's frame, q = x⁴/(hx + 0.85)⁴ + z⁴/(hz + 0.85)⁴ and, when
    /// q &lt; 1, the dome top is (1 − q)^¼ · (hy + 0.85) above the object's centre. The raise keeps its previous value
    /// while the camera barely moves (horizontal speed &lt; 1 m/s and the eye direction within dot 0.9 of the last
    /// frame's) and is applied half-stepped: applied = (raise + previous) / 2 (mover +0x68..+0x78).
    /// [? Ghidra's stack aliasing in 0x47C250 hides which extent is the height; read here as the box's vertical half.]
    /// </summary>
    public sealed class ObstructionRaiser
    {
        float _held, _acc;
        Vector3 _lastDir;

        public void Reset() { _held = _acc = 0f; _lastDir = Vector3.zero; }

        static float DomeRaise(Vector3 eye, PlayerCar skip)
        {
            foreach (var c in CameraAvoidables.Get())
            {
                if (!c || c == skip) continue;
                CameraAvoidables.Box(c, out var centre, out var half, out var rot);
                var l = Quaternion.Inverse(rot) * (eye - centre);
                float a = half.x + 0.85f, b = half.z + 0.85f, h = half.y + 0.85f;
                if (Mathf.Abs(l.x) >= a || Mathf.Abs(l.z) >= b) continue;
                float qx = l.x / a, qz = l.z / b;
                float q = qx * qx * qx * qx + qz * qz * qz * qz;
                if (q >= 1f) continue;
                float top = Mathf.Sqrt(Mathf.Sqrt(1f - q)) * h;
                if (l.y < top) return top - l.y;
            }
            return 0f;
        }

        /// <summary>Returns the raised eye; <paramref name="planarVel"/> is the anchor's horizontal velocity.</summary>
        public Vector3 Apply(Vector3 eye, Vector3 look, Vector3 planarVel, PlayerCar skip)
        {
            float y0 = eye.y, raise = 0f;
            var e = eye;
            for (int i = 0; i < 16; i++)
            {
                float r = DomeRaise(e, skip);
                if (r <= 0f) break;
                e.y += r;
            }
            raise = e.y - y0;
            var dir = (look - eye).normalized;
            planarVel.y = 0f;
            if (planarVel.magnitude < 1f && Vector3.Dot(dir, _lastDir) > 0.9f && raise < _held) raise = _held;
            _lastDir = dir;
            _held = raise;
            float applied = (raise + _acc) * 0.5f;
            _acc += raise - applied;
            eye.y = y0 + applied;
            return eye;
        }
    }
}
