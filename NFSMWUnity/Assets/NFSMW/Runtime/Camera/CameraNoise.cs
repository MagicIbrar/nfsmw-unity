using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// Camera::ApplyNoise (0x467420; decomp Camera/Camera.cpp:176-247) and the movers that feed it (specs/camera_core.md
    /// §13). The camera keeps two 4-channel noise layers (CameraParams NoiseFrequency1 +0x70, NoiseAmplitude1 +0x80,
    /// NoiseFrequency2 +0x90, NoiseAmplitude2 +0xA0); ApplyNoise sums Noise(freq·t)·amp of both layers (channel x / y = a
    /// camera-space translation in metres, z / w = X / Y rotations in degrees, each turned by FovRelativeAngle) and
    /// multiplies m = T(x, y, 0)·RotX(z)·RotY(w) into the world-to-camera matrix (eMulMatrix(p, p, m): camera space).
    /// </summary>
    public static class CameraNoise
    {
        /// <summary>NoiseBase (0x467250): Hugo Elias' integer hash, 1 − (n(n²·15731 + 789221) + 1376312589 &amp; 0x7FFFFFFF)/2³⁰.</summary>
        public static float NoiseBase(int x)
        {
            unchecked
            {
                int n = (x << 13) ^ x;
                int h = (n * (n * n * 15731 + 789221) + 1376312589) & 0x7FFFFFFF;
                return 1f - h * (1f / 1073741824f);   // flt_894968
            }
        }

        /// <summary>NoiseInterpolated (0x467290): floor (ftol, minus one when above x), linear between the two hashes.</summary>
        public static float NoiseInterpolated(float x)
        {
            int a = (int)x;
            if (a > x) a--;
            float s = NoiseBase(a), t = NoiseBase(a + 1), f = x - a;
            return f * t + (1f - f) * s;
        }

        /// <summary>Noise (0x467350): 6 octaves, frequency ×2, amplitude ×0.5 from 1 (range about ±2).</summary>
        public static float Noise(float x)
        {
            float total = 0f, frequency = 1f, amplitude = 1f;
            for (int i = 0; i < 6; i++)
            {
                total += amplitude * NoiseInterpolated(x * frequency);
                frequency *= 2f;
                amplitude *= 0.5f;
            }
            return total;
        }

        /// <summary>
        /// Camera::FovRelativeAngle (0x4673C0): asin(sin(a)·sin(fov/2)/sin(baseline/2)), baseline aBaselineFovNoise 0x2AAA
        /// (60°, 0x8EDF1C), fov = Camera +0xC4 FieldOfView (the horizontal FOV): a narrow lens shakes less.
        /// </summary>
        public static float FovRelativeAngle(float deg, float fovHDeg)
        {
            float f = Mathf.Sin(deg * Mathf.Deg2Rad) * Mathf.Sin(fovHDeg * 0.5f * Mathf.Deg2Rad) / Mathf.Sin(30f * Mathf.Deg2Rad);
            return Mathf.Asin(Mathf.Clamp(f, -1f, 1f)) * Mathf.Rad2Deg;
        }

        static Vector4 Noise4(Vector4 f, float t) => new Vector4(Noise(f.x * t), Noise(f.y * t), Noise(f.z * t), Noise(f.w * t));

        /// <summary>
        /// ApplyNoise(matrix, time, intensity): v = (Noise(f1·t)·a1 + Noise(f2·t)·a2)·intensity. In the camera's frame the
        /// world moves by (v.x, v.y) and turns by RotX(z')·RotY(w') (row vectors, D3DX signs), so the camera itself moves by
        /// −(v.x, v.y) and turns by RotX(−z')·RotY(−w') [? the exe camera space read as x right, y up, z forward].
        /// </summary>
        public static void Apply(Transform cam, Vector4 f1, Vector4 a1, Vector4 f2, Vector4 a2, float time, float fovH, float intensity = 1f)
        {
            if (!cam) return;
            var v = (Vector4.Scale(Noise4(f1, time), a1) + Vector4.Scale(Noise4(f2, time), a2)) * intensity;
            if (v == Vector4.zero) return;
            var rot = cam.rotation;
            cam.position -= rot * new Vector3(v.x, v.y, 0f);
            float rx = FovRelativeAngle(v.z, fovH), ry = FovRelativeAngle(v.w, fovH);
            cam.rotation = rot * Quaternion.Euler(-rx, 0f, 0f) * Quaternion.Euler(0f, -ry, 0f);
        }

        // ---- CubicCameraMover's speed noise (0x47BEC0, called from CubicCameraMover::Update 0x47DDAA) ----

        // the speed graph 0x8EDFB0 (5 rows, 0..80 m/s, step 1/20; tGraph 0x46F020): {A1, A2, F1, F2}
        static readonly Vector4[] SpeedRows =
        {
            new Vector4(0f, 0f, 0.6f, 1f), new Vector4(0.005f, 0.5f, 1.1f, 1.1f), new Vector4(0.03f, 0.8f, 1.2f, 1.2f),
            new Vector4(0.07f, 1f, 1.3f, 1.3f), new Vector4(0.02f, 1f, 1f, 1.4f),
        };
        // per-channel scales 0x8EDF70 (Freq1), 0x8EDF80 (Amp1), 0x8EDF90 (Freq2), 0x8EDFA0 (Amp2)
        static readonly Vector4 Freq1 = new Vector4(1.8f, 2f, 2.125f, 2f), Amp1 = new Vector4(0.03f, 0.025f, 0.68f, 0.28f);
        static readonly Vector4 Freq2 = new Vector4(3f, 5f, 7f, 5.5f), Amp2 = new Vector4(0.007f, 0.01f, 0.3f, 0.4f);

        static Vector4 SpeedGraph(float speed)
        {
            float x = speed * 0.05f;
            int i = (int)x;
            if (i < 0) return SpeedRows[0];
            if (i >= SpeedRows.Length - 1) return SpeedRows[SpeedRows.Length - 1];
            if (i > x) i--;
            return Vector4.Lerp(SpeedRows[i], SpeedRows[i + 1], x - i);
        }

        /// <summary>The anchor's state the speed noise reads (CameraAnchor, filled by CDActionDrive::Update).</summary>
        public struct DriveState
        {
            public float Speed;            // +0x10
            public float ForwardAccel;     // +0x40 · forward (+0x50 row 0)
            public bool DragRace;          // +0xBE (racer vt+0x30 == 1)
            public bool Nos;               // +0xBC IEngine::IsNOSEngaged
            public bool BrakeEngaged;      // +0xBD controls fBrake > 0 || fHandBrake > 0
            public bool OverRev;           // +0xBF rpm > redline − 500 (flt_8946A4)
            public bool OutsidePov;        // CubicCameraMover vt+0x24
            public float SurfaceAmp, SurfaceFreq;   // +0x98 simsurface CAMERA_NOISE[0] / [1] (kNull off the ground: 0)
        }

        /// <summary>
        /// 0x47BEC0: the noise layers of the drive camera. Graph row g = SpeedGraph(speed): A1 = g.A1 × the POV scale
        /// (0x8EDE1C, 1 for every POV; ×1.5 in a drag race), F1 = g.F1; above 5 m/s while accelerating, r = min(a, 20)·0.05:
        /// A1 += 0.15·r, F1 += (0.5 − F1)·r; over-rev: A1 += 0.15, F1 ×0.7; above 5 m/s NOS sets (0.3, 2) and the brake
        /// (0.3, 5); layer 2: A2 = g.A2·CAMERA_NOISE[0] × the POV scale (0x8EDE38, 1), F2 = g.F2·CAMERA_NOISE[1]; inside
        /// POVs (bumper / hood) both amplitudes ×0.25. Channels: Freq1·F1, Amp1·A1, Freq2·F2, Amp2·A2.
        /// </summary>
        public static void DriveLayers(in DriveState s, out Vector4 f1, out Vector4 a1, out Vector4 f2, out Vector4 a2)
        {
            var g = SpeedGraph(s.Speed);
            float A1 = g.x, F1 = g.z;
            if (s.DragRace) A1 *= 1.5f;
            if (s.Speed > 5f && s.ForwardAccel > 0f)
            {
                float r = Mathf.Min(s.ForwardAccel, 20f) * 0.05f;
                A1 += 0.15f * r;
                F1 += (0.5f - F1) * r;
            }
            if (s.OverRev) { A1 += 0.15f; F1 *= 0.7f; }
            if (s.Nos && s.Speed > 5f) { A1 = 0.3f; F1 = 2f; }
            if (s.BrakeEngaged && s.Speed > 5f) { A1 = 0.3f; F1 = 5f; }
            float A2 = g.y * s.SurfaceAmp, F2 = g.w * s.SurfaceFreq;
            if (!s.OutsidePov) { A1 *= 0.25f; A2 *= 0.25f; }
            f1 = Freq1 * F1; a1 = Amp1 * A1; f2 = Freq2 * F2; a2 = Amp2 * A2;
        }

        static readonly Dictionary<uint, Vector2> SurfaceNoise = new Dictionary<uint, Vector2>();

        /// <summary>simsurface CAMERA_NOISE (0xF0C9E498) [0] amplitude, [1] frequency of a surface; absent = 0 (DefaultDataArea).
        /// Retail: particulate / dirt / grass / sand / gravel / mud (0.5, 1), cobble (0.3, 2), grass_shoulder (0.1, 0.5),
        /// golf_fairway (0.25, 1), roads 0 (harness camnoise).</summary>
        public static Vector2 CameraNoiseOf(uint surfaceKey)
        {
            if (SurfaceNoise.TryGetValue(surfaceKey, out var v)) return v;
            v = Vector2.zero;
            var db = GameData.Attributes;
            if (db != null && db.Collections.TryGetValue((AttribDatabase.Hash("simsurface"), surfaceKey), out var c))
            {
                var f = c.Floats("CAMERA_NOISE");
                if (f.Length > 0) v.x = f[0];
                if (f.Length > 1) v.y = f[1];
            }
            SurfaceNoise[surfaceKey] = v;
            return v;
        }

        // ---- CameraMover::ChopperNoise (0x470A60) ----

        static readonly List<CopHeli> Helis = new List<CopHeli>();
        static int _heliFrame = -100;
        // CameraNoiseChopperFrequency 0x8EDF50 / CameraNoiseChopperAmplitude 0x8EDF60
        static readonly Vector4 ChopperFreq = new Vector4(3.141f, 2.971f, 0.84234f, 0.92345f), ChopperAmp = new Vector4(0.01f, 0.05f, 1.1f, 2.7f);

        /// <summary>
        /// ChopperNoise(matrix, scale, world timer): for every active helicopter within 40 m horizontally of the camera,
        /// NoiseFrequency1 = ChopperFreq, NoiseAmplitude1 = ChopperAmp·scale·(1 − d·0.025) and ApplyNoise at the world
        /// time (layer 2 keeps whatever the camera last had: 0 in the drive camera before its speed noise). CubicCameraMover
        /// calls it outside tunnels (0x73CFE0) with scale = (1 − 0.8·min(1, speed·1092.27))·1 (0x8EDE00 per POV, all 1;
        /// the 1092.27 = 65536/60 factor makes it 0.2 for any moving car and 1 when stopped).
        /// </summary>
        public static void Chopper(Transform cam, float scale, float time, float fovH)
        {
            if (!cam || scale <= 0f) return;
            if (Time.frameCount - _heliFrame >= 30 || Time.frameCount < _heliFrame)
            {
                _heliFrame = Time.frameCount;
                Helis.Clear();
                Helis.AddRange(Object.FindObjectsByType<CopHeli>(FindObjectsSortMode.None));
            }
            foreach (var h in Helis)
            {
                if (!h || !h.isActiveAndEnabled || h.Finished) continue;
                var d = h.transform.position - cam.position;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist >= 40f) continue;
                Apply(cam, ChopperFreq, ChopperAmp * (scale * (1f - dist * 0.025f)), Vector4.zero, Vector4.zero, time, fovH);
            }
        }
    }

    /// <summary>
    /// IExplosion sources of the drive camera's shake (CDActionDrive::Update 0x47308E..0x4731A5; the explosion list
    /// _Listable&lt;IExplosion, 96&gt;): every live explosion whose maximum radius + 2 m reaches the camera position
    /// (camera +0x40) shakes the view: MaybeCameraShake(view − 1, −normalize(origin − camera)·expansionSpeed/dT) (Rumble.cpp,
    /// Runtime/Input/Rumble.cs CameraShaker). An explosion lives while it expands from its start radius to the maximum at
    /// its speed [hook: whoever spawns explosions calls <see cref="Add"/>; ESpawnExplosion in Smackable].
    /// </summary>
    public static class CameraExplosions
    {
        struct Blast { public Vector3 Origin; public float Speed, MaxRadius, Until; }
        static readonly List<Blast> Live = new List<Blast>();

        /// <summary>A new explosion (ESpawnExplosion {fPosition, fSpeed, fRadius, fStartRadius}).</summary>
        public static void Add(Vector3 origin, float speed, float maxRadius, float startRadius = 0f)
        {
            float life = speed > 1e-3f ? Mathf.Max(0f, maxRadius - startRadius) / speed : 0f;
            Live.Add(new Blast { Origin = origin, Speed = speed, MaxRadius = maxRadius, Until = Time.time + Mathf.Max(life, Time.deltaTime) });
        }

        /// <summary>The per-frame loop of CDActionDrive::Update for one view (player 0 / 1).</summary>
        public static void Shake(int player, Vector3 camPos, float dt)
        {
            if (Live.Count == 0 || dt <= 0f) return;
            Live.RemoveAll(b => Time.time > b.Until);
            foreach (var b in Live)
            {
                var d = b.Origin - camPos;
                float dist = d.magnitude;
                if (b.MaxRadius + 2f <= dist || dist <= 0f) continue;
                var n = d / dist;
                // the exe hands the world vector (game axes: z up) to MaybeCameraShake
                Controls.CameraShaker.MaybeCameraShake(player, Coords.ToGame(-n * (b.Speed / dt)));
            }
        }

        public static void Clear() => Live.Clear();
    }

    /// <summary>
    /// ICEMover's bCarSpaceLag (ICEMover::Update 0x4834B5..0x4835A4; specs/camera_core.md §13): with the current key's
    /// bCarSpaceLag (ICEData +9) a tCubic3D (mover +0xB0, ctor 0x46A300: duration 1 s, flags 1 = re-aimed every frame)
    /// chases the ICE anchor's acceleration vector (ICEAnchor::Update 0x46D3F0 +0x60: (|v| − |v_prev|)/dT along the car's
    /// forward, kept when the anchor jumped faster than 300 m/s), updated with dT × the key's sim speed; the eye and look
    /// both move by −clamp(value × (0.05, 0.05, 0.02), (−1, −1, −0.2), (1, 1, 1)) (0x8EE2F0 min, 0x8EE300 max, 0x8EE310
    /// scale; game axes: x/y horizontal, z up): the camera trails the car when it speeds up and runs ahead when it brakes.
    /// Retail keys with the flag: 5 NIS (context 0), 6 FMV, 71 replay (unused on PC), 2 in the generic "Challenge" track.
    /// </summary>
    public sealed class IceCarSpaceLag
    {
        // tCubic1D ×3: value, slope (per second at duration 1), state 2 = re-aim pending
        readonly float[] _val = new float[3], _slope = new float[3];
        readonly bool[] _moving = new bool[3];
        Vector3 _accel, _pos;
        float _speed;
        bool _init;

        public void Reset()
        {
            for (int i = 0; i < 3; i++) { _val[i] = _slope[i] = 0f; _moving[i] = false; }
            _accel = Vector3.zero;
            _init = false;
        }

        /// <summary>ICEAnchor::Update(dT, carMatrix, velocity) every frame of the ICE action (CDActionIce::Update 0x48450E).</summary>
        public void Anchor(Vector3 pos, Vector3 forward, Vector3 velocity, float dt)
        {
            float speed = velocity.magnitude;
            if (!_init) { _init = true; _pos = pos; _speed = speed; return; }
            if (dt > 0f)
            {
                float inv = 1f / dt;
                if (Vector3.Distance(_pos, pos) * inv < 300f) _accel = forward * ((speed - _speed) * inv);   // flt_894AE4
            }
            _pos = pos;
            _speed = speed;
        }

        /// <summary>The lag offset for a key with bCarSpaceLag; <paramref name="dtSim"/> = camera dT × the key's sim speed.</summary>
        public Vector3 Offset(float dtSim)
        {
            var g = Coords.ToGame(_accel);   // game axes: the clamp's z is the vertical
            float[] des = { g.x, g.y, g.z };
            for (int i = 0; i < 3; i++)
            {
                if (des[i] != _val[i]) _moving[i] = true;   // tCubic1D::SetValDesired (0x46A480): state 2 when it differs
                if (!_moving[i]) continue;
                // tCubic1D::Update (0x46BDC0) state 2 with flags 1: a Hermite from (value, slope) to (desired, 0) restarted
                // every frame, evaluated at t = dT/duration (1 s); past 1 it lands
                float t = dtSim;
                if (t > 1f) { _val[i] = des[i]; _slope[i] = 0f; _moving[i] = false; continue; }
                float d = des[i] - _val[i], v0 = _slope[i];
                float c3 = v0 - 2f * d, c2 = 3f * d - 2f * v0;   // desired slope 0
                float nv = ((c3 * t + c2) * t + v0) * t + _val[i];
                _slope[i] = (3f * c3 * t + 2f * c2) * t + v0;
                _val[i] = nv;
            }
            var lag = new Vector3(Mathf.Clamp(_val[0] * 0.05f, -1f, 1f), Mathf.Clamp(_val[1] * 0.05f, -1f, 1f), Mathf.Clamp(_val[2] * 0.02f, -0.2f, 1f));
            return Coords.ToUnity(lag.x, lag.y, lag.z);
        }
    }
}
