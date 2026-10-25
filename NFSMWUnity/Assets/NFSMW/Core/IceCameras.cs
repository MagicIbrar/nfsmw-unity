using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// ICE camera tracks (GLOBAL\InGameB.bun; decomp Camera/ICE/ICEData.hpp, specs/camera_core.md §7): containers
    /// 0x8003B200 NIS, 0x8003B201 FMV, 0x8003B202 REPLAY, 0x8003B203 GENERIC, each holding 0x3B210 groups {u32 scene hash
    /// (bStringHash), u32 track count} followed by the tracks: a 0x28-byte ICETrack header (Start +0xC, Length +0x10
    /// seconds, NumKeys +0x14, Name +0x17, e.g. "Track 0") and NumKeys 0x84-byte ICEData keys.
    /// <see cref="Sample"/> is ICEMover::SetDesired (0x482160) + ICEMover::Update (0x482ec0): per key segment a Hermite
    /// cubic for eye/look/lens/dutch whose tangents come from ICEManager::GetSlope (0x47f490), and smooth-stepped
    /// scalar channels (near clip, noise, focus, aperture, letterbox, sim speed).
    /// </summary>
    public sealed class IceCameras
    {
        public enum Space { Car = 0, World = 1, Hybrid = 2, Scene = 3 }

        /// <summary>The ICE overlay packages (ICE::GetOverlayName 0x46e9c0, table gIceOverlays 0x8EDEEC).</summary>
        public static readonly string[] OverlayPackages = { null, "FLASHERS", "BLACK_FADE_OUT", "BUSTED_OVERLAY", "BLACK_BACKGROUND" };

        /// <summary>One ICEData key: the segment from its parameter to the next key's, with start/end values.</summary>
        public sealed class Key
        {
            public byte Type, Smooth;                                   // +0x00 nType (12..18 in the data), +0x01 bSmooth (bit0 entry, bit1 exit)
            public bool CubicEye, CubicLook;                            // +0x02/+0x03
            public Space EyeSpace, LookSpace;                           // +0x04/+0x05
            public bool ConstrainToCars, ConstrainToWorld;              // +0x06/+0x07: obstruction raise / terrain clamp (replay + generic)
            public bool IgnoreOrientation, CarSpaceLag;                 // +0x08/+0x09
            public byte Overlay;                                        // +0x0A: OverlayPackages index
            public uint ShakeType;                                      // +0x0C: ICEShakeTrack hash (0 = Perlin noise)
            public float Parameter;                                     // +0x10: 0..1 of the track
            public float TangentStart, TangentEnd;                      // +0x14 fTangentLength[2]
            public Vec3 EyeStart, EyeEnd, LookStart, LookEnd;           // +0x1C / +0x34
            public float DutchStart, DutchEnd;                          // +0x4C: fraction of a turn (×65536 → bAngle)
            public float LensStart, LensEnd;                            // +0x54: focal length in mm (FOV = 2·atan(15.96/lens))
            public float NearClipStart, NearClipEnd;                    // +0x5C
            public float NoiseAmpStart, NoiseAmpEnd;                    // +0x64
            public float NoiseFreqStart, NoiseFreqEnd;                  // +0x6C
            public float FocalStart, FocalEnd;                          // +0x74
            public byte ApertureStart, ApertureEnd;                     // +0x7C
            public byte LetterboxStart, LetterboxEnd;                   // +0x7E (×0.00125 = Camera LB_height)
            public byte SimSpeedStart, SimSpeedEnd;                     // +0x80 (percent of game speed)
        }

        public sealed class Track
        {
            public string Name;
            public float Start, Length;
            public readonly List<Key> Keys = new List<Key>();
        }

        /// <summary>Tracks per context (0 NIS, 1 FMV, 2 replay, 3 generic) and scene hash.</summary>
        public readonly Dictionary<(int context, uint scene), List<Track>> Groups = new Dictionary<(int, uint), List<Track>>();

        /// <summary>
        /// ICEShakeTrack (decomp ICEData.hpp:185; chunk 0x3B211 in InGameB.bun, ICEManager::LoadCameraShakes 0x478890):
        /// {u32 count} then per track {bTNode, group, i16 NumKeys +0xC, i8, Name[14] +0xF, ICEShakeData[NumKeys] +0x20}
        /// with ICEShakeData {float q[3] (quaternion vector part), float p[3] (offset)}. Retail: Jog, Still, WalkFast,
        /// WalkSlow (119 keys each); an ICE key names one by bStringHash in nShakeType (GetShakeTrack 0x478290).
        /// </summary>
        public sealed class ShakeTrack
        {
            public string Name;
            public float[] Q, P;   // xyz per key
            public int Count => Q.Length / 3;
        }

        public readonly Dictionary<uint, ShakeTrack> Shakes = new Dictionary<uint, ShakeTrack>();

        /// <summary>The game's bStringHash: h = h*33 + c from 0xFFFFFFFF.</summary>
        public static uint StringHash(string s)
        {
            uint h = 0xFFFFFFFF;
            foreach (char c in s) h = h * 33 + c;
            return h;
        }

        public static IceCameras Load(string gameRoot)
        {
            var ice = new IceCameras();
            var path = Path.Combine(gameRoot, "GLOBAL", "InGameB.bun");
            if (!File.Exists(path)) path = Path.Combine(gameRoot, "GLOBAL", "InGameB.lzc");
            if (!File.Exists(path)) return ice;
            ice.Read(GameFiles.ReadBundle(path));
            return ice;
        }

        public void Read(byte[] d)
        {
            foreach (var top in Chunks.Read(d, 0, d.Length))
            {
                if (top.Id == 0x3B211 && top.Size >= 4)
                {
                    int n = (int)Chunks.U32(d, top.DataStart), o = top.DataStart + 4;
                    for (int i = 0; i < n && o + 0x20 <= top.End; i++)
                    {
                        int keys = Math.Max(0, (int)(short)Chunks.U16(d, o + 0xC));
                        if (o + 0x20 + keys * 0x18 > top.End) break;
                        var t = new ShakeTrack { Name = CString(d, o + 0xF, 14), Q = new float[keys * 3], P = new float[keys * 3] };
                        for (int k = 0; k < keys; k++)
                            for (int j = 0; j < 3; j++)
                            {
                                t.Q[k * 3 + j] = Chunks.F32(d, o + 0x20 + k * 0x18 + j * 4);
                                t.P[k * 3 + j] = Chunks.F32(d, o + 0x2C + k * 0x18 + j * 4);
                            }
                        if (keys > 0) Shakes[StringHash(t.Name)] = t;
                        o += 0x20 + keys * 0x18;
                    }
                    continue;
                }
                if (top.Id < 0x8003B200 || top.Id > 0x8003B203) continue;
                int context = (int)(top.Id - 0x8003B200);
                foreach (var g in Chunks.Read(d, top.DataStart, top.End))
                {
                    if (g.Id != 0x3B210 || g.Size < 8) continue;
                    uint scene = Chunks.U32(d, g.DataStart);
                    int count = (int)Chunks.U32(d, g.DataStart + 4);
                    var tracks = new List<Track>(count);
                    int o = g.DataStart + 8;
                    for (int t = 0; t < count && o + 0x28 <= g.End; t++)
                    {
                        var tr = new Track
                        {
                            Start = Chunks.F32(d, o + 0xC),
                            Length = Chunks.F32(d, o + 0x10),
                            Name = CString(d, o + 0x17, 14),
                        };
                        int n = (short)Chunks.U16(d, o + 0x14);
                        o += 0x28;
                        for (int k = 0; k < n && o + 0x84 <= g.End; k++, o += 0x84) tr.Keys.Add(ReadKey(d, o));
                        tracks.Add(tr);
                    }
                    Groups[(context, scene)] = tracks;
                }
            }
        }

        static Key ReadKey(byte[] d, int o)
        {
            // vEye/vLook are declared [3][2] but stored start xyz then end xyz
            Vec3 V(int at) => new Vec3(Chunks.F32(d, at), Chunks.F32(d, at + 4), Chunks.F32(d, at + 8));
            float F(int at) => Chunks.F32(d, o + at);
            return new Key
            {
                Type = d[o],
                Smooth = d[o + 1],
                CubicEye = d[o + 2] != 0,
                CubicLook = d[o + 3] != 0,
                EyeSpace = (Space)d[o + 4],
                LookSpace = (Space)d[o + 5],
                ConstrainToCars = d[o + 6] != 0,
                ConstrainToWorld = d[o + 7] != 0,
                IgnoreOrientation = d[o + 8] != 0,
                CarSpaceLag = d[o + 9] != 0,
                Overlay = d[o + 0xA],
                ShakeType = Chunks.U32(d, o + 0xC),
                Parameter = F(0x10),
                TangentStart = F(0x14), TangentEnd = F(0x18),
                EyeStart = V(o + 0x1C), EyeEnd = V(o + 0x28),
                LookStart = V(o + 0x34), LookEnd = V(o + 0x40),
                DutchStart = F(0x4C), DutchEnd = F(0x50),
                LensStart = F(0x54), LensEnd = F(0x58),
                NearClipStart = F(0x5C), NearClipEnd = F(0x60),
                NoiseAmpStart = F(0x64), NoiseAmpEnd = F(0x68),
                NoiseFreqStart = F(0x6C), NoiseFreqEnd = F(0x70),
                FocalStart = F(0x74), FocalEnd = F(0x78),
                ApertureStart = d[o + 0x7C], ApertureEnd = d[o + 0x7D],
                LetterboxStart = d[o + 0x7E], LetterboxEnd = d[o + 0x7F],
                SimSpeedStart = d[o + 0x80], SimSpeedEnd = d[o + 0x81],
            };
        }

        static string CString(byte[] d, int o, int max)
        {
            int n = 0;
            while (n < max && o + n < d.Length && d[o + n] != 0) n++;
            return System.Text.Encoding.ASCII.GetString(d, o, n);
        }

        public List<Track> Find(int context, string scene) =>
            Groups.TryGetValue((context, StringHash(scene)), out var t) ? t : null;

        /// <summary>
        /// ICEManager::ChooseCameraPlaybackTrack (0x483d30): a NIS plays the track named "Track %d" with the scene's camera
        /// track number; falls back to that index, then the first track.
        /// </summary>
        /// <summary>ICEGroup::GetTrack(name): the track with that name, or null (the exe has no fallback).</summary>
        public static Track TrackByName(List<Track> tracks, string name)
        {
            if (tracks == null) return null;
            foreach (var t in tracks) if (t.Name == name) return t;
            return null;
        }

        public static Track TrackByNumber(List<Track> tracks, int number)
        {
            if (tracks == null || tracks.Count == 0) return null;
            string name = "Track " + number;
            foreach (var t in tracks) if (t.Name == name) return t;
            return tracks[Math.Max(0, Math.Min(number, tracks.Count - 1))];
        }

        /// <summary>One evaluated camera: eye/look with their spaces, FOV (horizontal degrees) and the scalar channels.</summary>
        public struct Sample
        {
            public int KeyIndex;
            public Key Key;
            public float U;                         // 0..1 inside the key segment
            public Vec3 Eye, Look;
            public Space EyeSpace, LookSpace;
            public float FovDeg, DutchDeg, NearClip, NoiseAmp, NoiseFreq, Focal, Aperture;
            public float Letterbox;                 // LB_height = letterbox × 0.00125 (fraction of the screen height) [?]
            public float SimSpeed;                  // Camera SimTimeMultiplier = clamp(simspeed, 0, 100) × 0.01, ≥ 0.01
        }

        /// <summary>ICETrack::GetKeyNumber: the last key whose parameter is ≤ param.</summary>
        public static int KeyNumber(Track tr, float param)
        {
            var keys = tr.Keys;
            int i = 0;
            while (i + 1 < keys.Count && param >= keys[i + 1].Parameter) i++;
            return i;
        }

        static float SegmentEnd(Track tr, int i) => i + 1 < tr.Keys.Count ? tr.Keys[i + 1].Parameter : 1f;

        /// <summary>FOV (degrees) of a lens: bATan(lens, 15.96)·2 (ICEMover::SetDesired 0x482160, constant 0x417F5C2A).</summary>
        public static float LensToFov(float lens) => 2f * (float)(Math.Atan2(15.96, Math.Max(1e-3f, lens)) * 180.0 / Math.PI);

        /// <summary>
        /// The camera at a track parameter (NIS: scene elapsed / scene length, ICETrack::GetCameraData 0x47b030; generic and
        /// FMV: (time − Start) / Length clamped to 1).
        /// </summary>
        public static Sample Evaluate(Track tr, float param)
        {
            var s = new Sample();
            if (tr == null || tr.Keys.Count == 0) return s;
            int i = KeyNumber(tr, param);
            var k = tr.Keys[i];
            float p0 = k.Parameter, p1 = SegmentEnd(tr, i);
            float u = Math.Abs(p1 - p0) > 1e-4f ? (param - p0) / (p1 - p0) : 0f;
            u = Math.Max(0f, Math.Min(1f, u));
            s.KeyIndex = i;
            s.Key = k;
            s.U = u;
            s.EyeSpace = k.EyeSpace;
            s.LookSpace = k.LookSpace;

            Slopes(tr, i, 0, out var eye0, out var look0, out float dutch0, out float lens0);
            Slopes(tr, i, 1, out var eye1, out var look1, out float dutch1, out float lens1);
            // ICEMover::GetEye/GetLook (0x46dd10/0x46ddd0): the cubic when bCubicEye/bCubicLook, else a straight lerp
            s.Eye = k.CubicEye ? Hermite(k.EyeStart, k.EyeEnd, eye0, eye1, u) : Lerp(k.EyeStart, k.EyeEnd, u);
            s.Look = k.CubicLook ? Hermite(k.LookStart, k.LookEnd, look0, look1, u) : Lerp(k.LookStart, k.LookEnd, u);
            // lens cubic in FOV units; its slopes are d(fov)/d(lens) × lens slope (0x46d620); GetFOV/GetDutch use bCubicEye
            float f0 = LensToFov(k.LensStart), f1 = LensToFov(k.LensEnd);
            float fs0 = (LensToFov(k.LensStart + lens0 * 0.01f) - f0) * 100f, fs1 = (LensToFov(k.LensEnd + lens1 * 0.01f) - f1) * 100f;
            s.FovDeg = k.CubicEye ? Hermite(f0, f1, fs0, fs1, u) : f0 + (f1 - f0) * u;
            float dutch = k.CubicEye ? Hermite(k.DutchStart, k.DutchEnd, dutch0, dutch1, u) : k.DutchStart + (k.DutchEnd - k.DutchStart) * u;
            s.DutchDeg = dutch * 360f;
            // the scalar channels are Hermite cubics with zero end slopes (a smoothstep)
            float e = u * u * (3f - 2f * u);
            float Ease(float a, float b) => a + (b - a) * e;
            s.NearClip = Ease(k.NearClipStart, k.NearClipEnd);
            s.NoiseAmp = Math.Max(0f, Ease(k.NoiseAmpStart, k.NoiseAmpEnd));
            s.NoiseFreq = Math.Max(0f, Ease(k.NoiseFreqStart, k.NoiseFreqEnd));
            s.Focal = Ease(k.FocalStart, k.FocalEnd);
            s.Aperture = Ease(k.ApertureStart, k.ApertureEnd);
            s.Letterbox = Math.Max(0f, Ease(k.LetterboxStart, k.LetterboxEnd)) * 0.00125f;
            float sim = Ease(k.SimSpeedStart, k.SimSpeedEnd);
            s.SimSpeed = sim < 0 ? 0.01f : Math.Max(0.01f, Math.Min(100f, sim) * 0.01f);
            return s;
        }

        /// <summary>
        /// The f-number of an ICE aperture byte (0x46D680): 2^(i/6) for i = (int)(aperture + 0.5) clamped to 0..36
        /// (f/1 .. f/64 in sixth stops).
        /// </summary>
        public static float FNumber(float aperture)
        {
            int i = (int)(aperture + 0.5f);
            i = i <= 0 ? 0 : Math.Min(i, 36);
            return (float)Math.Pow(2.0, i / 6.0);
        }

        /// <summary>
        /// ICEMover::Update's depth of field (0x482EC0 tail): off (false) for an aperture &lt; 0 or ≥ 37; otherwise a lens of
        /// f = 15.96 / tan(FOV/2) mm (0x46D5E0) focused at s = max(focal, 0.01) m, circle of confusion 0.03 mm:
        /// H = f²/(N·0.03) + f, near = (H − f)s/(H + s − 2f), far = (H − f)s/(H − s) (100 km when H ≤ s; near + 0.01 when
        /// below near), in metres. Camera FocalDistance (+0xB4) = (near + far)/2, DepthOfField (+0xB8) = far − near.
        /// </summary>
        public static bool DepthOfField(float fovDeg, float focal, float aperture, out float focalDistance, out float depth)
        {
            focalDistance = depth = 0f;
            if (aperture < 0f || aperture >= 37f) return false;
            float s = Math.Max(focal, 0.01f) * 1000f;
            double t = Math.Tan(fovDeg * 0.5 * Math.PI / 180.0);
            float f = (float)(15.96 / (t > 0 ? t : 1e-4));
            float h = f * f / (FNumber(aperture) * 0.03f) + f;
            float k = (h - f) * s;
            float near = k / (h + s - 2f * f) * 0.001f;
            float far = h - s > 1e-5f ? k / (h - s) * 0.001f : 100000f;
            if (far < near) far = near + 0.01f;
            focalDistance = (near + far) * 0.5f;
            depth = far - near;
            return true;
        }

        /// <summary>Old entry point: eye/look/lens (as FOV degrees)/dutch (degrees)/letterbox at a parameter.</summary>
        public static void Evaluate(Track tr, float u, out Vec3 eye, out Space eyeSpace, out Vec3 look, out Space lookSpace,
                                    out float lens, out float dutch, out float letterbox)
        {
            var s = Evaluate(tr, u);
            eye = s.Eye; eyeSpace = s.EyeSpace; look = s.Look; lookSpace = s.LookSpace;
            lens = s.FovDeg; dutch = s.DutchDeg; letterbox = s.Letterbox;
        }

        /// <summary>
        /// ICEManager::GetSlope (0x47f490) for side 0 (start) or 1 (end) of key i. A key of type 0 has flat tangents. A side
        /// whose neighbour does not share the point (ICE::KeysShared) gets chord × fTangentLength[side]; a shared side blends
        /// the two chords by interval size (a non-uniform Catmull-Rom tangent) × fTangentLength[side].
        /// </summary>
        static void Slopes(Track tr, int i, int side, out Vec3 eye, out Vec3 look, out float dutch, out float lens)
        {
            eye = default; look = default; dutch = 0; lens = 0;
            var keys = tr.Keys;
            var k = keys[i];
            if (k.Type == 0) return;
            float tl = side == 0 ? k.TangentStart : k.TangentEnd;
            int ni = side == 0 ? i - 1 : i + 1;
            var n = ni >= 0 && ni < keys.Count ? keys[ni] : null;
            if (n == null || n.Type == 0 || !Shared(k, side, n))
            {
                eye = Scale(Sub(k.EyeEnd, k.EyeStart), tl);
                look = Scale(Sub(k.LookEnd, k.LookStart), tl);
                dutch = (k.DutchEnd - k.DutchStart) * tl;
                lens = (k.LensEnd - k.LensStart) * tl;
                return;
            }
            float sk = Interval(tr, i), sn = Interval(tr, ni);
            float w = sn + sk > 1e-6f ? sk / (sn + sk) : 0.5f;
            float b = 1f - w;
            float a = sn > 1e-6f ? w / sn * sk : w;
            float fa = tl * a, fb = tl * b;
            eye = Add(Scale(Sub(n.EyeEnd, n.EyeStart), fa), Scale(Sub(k.EyeEnd, k.EyeStart), fb));
            look = Add(Scale(Sub(n.LookEnd, n.LookStart), fa), Scale(Sub(k.LookEnd, k.LookStart), fb));
            dutch = fa * (n.DutchEnd - n.DutchStart) + fb * (k.DutchEnd - k.DutchStart);
            lens = fa * (n.LensEnd - n.LensStart) + fb * (k.LensEnd - k.LensStart);
        }

        /// <summary>ICEManager::GetIntervalSize: the key's span of the track parameter.</summary>
        static float Interval(Track tr, int i) => Math.Max(0f, SegmentEnd(tr, i) - tr.Keys[i].Parameter);

        /// <summary>ICE::KeysShared (0x477ee0) [?]: the key's point on this side equals the neighbour's opposite point, in the same spaces.</summary>
        public static bool KeysShared(Key k, int side, Key n, int nside) => Shared(k, side, n);

        static bool Shared(Key k, int side, Key n)
        {
            if (k.EyeSpace != n.EyeSpace || k.LookSpace != n.LookSpace) return false;
            var ke = side == 0 ? k.EyeStart : k.EyeEnd;
            var ne = side == 0 ? n.EyeEnd : n.EyeStart;
            var kl = side == 0 ? k.LookStart : k.LookEnd;
            var nl = side == 0 ? n.LookEnd : n.LookStart;
            return Near(ke, ne) && Near(kl, nl);
        }

        static bool Near(Vec3 a, Vec3 b) => Math.Abs(a.X - b.X) < 1e-3f && Math.Abs(a.Y - b.Y) < 1e-3f && Math.Abs(a.Z - b.Z) < 1e-3f;
        static Vec3 Sub(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        static Vec3 Add(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        static Vec3 Scale(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        /// <summary>The tCubic Hermite (0x6513c0): c3 = v0 + v1 − 2d, c2 = 3d − v1 − 2v0, c1 = v0, c0 = p0.</summary>
        public static float Hermite(float p0, float p1, float v0, float v1, float u)
        {
            float d = p1 - p0;
            float c3 = v0 + v1 - 2f * d, c2 = 3f * d - v1 - 2f * v0;
            return ((c3 * u + c2) * u + v0) * u + p0;
        }

        static Vec3 Hermite(Vec3 p0, Vec3 p1, Vec3 v0, Vec3 v1, float u) =>
            new Vec3(Hermite(p0.X, p1.X, v0.X, v1.X, u), Hermite(p0.Y, p1.Y, v0.Y, v1.Y, u), Hermite(p0.Z, p1.Z, v0.Z, v1.Z, u));
    }
}
