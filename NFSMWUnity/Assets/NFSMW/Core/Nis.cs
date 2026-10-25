using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NfsMw.Core
{
    /// <summary>
    /// One in-engine cutscene (NIS) header (CAnimSceneData::SceneHeaderData, decomp Animation/AnimScene.hpp:25-39: +0
    /// name hash, +4 name[16], +0x14 description pointer, +0x18 SceneType, +0x1C HaveLayout, +0x20 HaveCarAnimation,
    /// +0x24 NumberOfCars, +0x28 StartFrame, +0x2C VanishFrame, +0x30 SeeulatorOverlayName[16]) and its animation
    /// tracks once loaded. CAnimScene::BindToGame: mTimeStart = StartFrame/30, mTimeTotalLength = VanishFrame/30;
    /// IsFinished = elapsed &gt; total length.
    /// </summary>
    public sealed class NisScene
    {
        public string Name, Description, File;
        public int Actors, Frames;
        /// <summary>SceneType: 0 race start, 1 world coordinates (no cars), 2 NIS in world coordinates, 3 race start with the
        /// player forced to its slot, 4 moment/world scene. 0/2/3 use the start-line frame (CAnimScene::Init :486), 1/4 the
        /// world origin.</summary>
        public int SceneType;
        public int HaveLayout, HaveCarAnimation, StartFrame;
        /// <summary>TheVisibleSectionManager.ActivateOverlay(name) while the scene exists (CAnimScene::Init :517), "" for none.</summary>
        public string OverlayName = "";
        /// <summary>mTimeTotalLength = VanishFrame / 30.</summary>
        public float Length => Frames / 30f;
        /// <summary>mTimeStart = StartFrame / 30 (the clock starts here, ResetTime).</summary>
        public float StartTime => StartFrame / 30f;
        /// <summary>CAnimScene::Init: SceneType 0, 2 or 3 → the scene frame is the start line (gNISSceneOrigin/Angle); else the world.</summary>
        public bool UsesStartLine => SceneType == 0 || SceneType == 2 || SceneType == 3;
        Dictionary<string, NisTrack> _tracks;

        /// <summary>The scene's animation channels by name (e.g. IntroNis01ZPM_Car1_t), read on first use.</summary>
        public Dictionary<string, NisTrack> Tracks
        {
            get
            {
                if (_tracks != null) return _tracks;
                _tracks = new Dictionary<string, NisTrack>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var d = System.IO.File.ReadAllBytes(File);
                    foreach (var c in Chunks.Read(d, 0, d.Length))
                        if (c.Id == 0xE34010) EaglAnimBank.Read(d, c.DataStart, c.End, _tracks);
                }
                catch (Exception) { }
                return _tracks;
            }
        }

        /// <summary>
        /// An actor's position (scene space: x right, y forward along the start heading, z up) and heading (degrees,
        /// counter-clockwise from +x) at time t; false when the scene has no track for it ("Car1", "Cop2", ...).
        /// </summary>
        public bool Pose(string actor, float t, out Vec3 pos, out float heading)
        {
            pos = default;
            heading = 0;
            if (!Tracks.TryGetValue(Name + "ZPM_" + actor + "_t", out var tr) || !Tracks.TryGetValue(Name + "ZPM_" + actor + "_q", out var q)) return false;
            var p = tr.Sample(t * NisTrack.KeysPerSecond);
            var r = q.Sample(t * NisTrack.KeysPerSecond);
            pos = new Vec3(p[0], p[1], p.Length > 2 ? p[2] : 0);
            heading = (float)(2 * Math.Atan2(r[2], r[3]) * 180 / Math.PI);
            return true;
        }

        /// <summary>
        /// CAnimScene::AnimatedCars_SetMainAndWheels (AnimScene.cpp:666-694): the car's full pose, bone 1 of the "ROOT"
        /// skeleton (identity bind pose) from the _t and _q channels at time t: position (scene space, game axes, with the
        /// track's own height) and the unit quaternion (x, y, z, w). False without both channels. The caller multiplies by
        /// the scene rotation/translation and replaces y by ground + 5 m.
        /// </summary>
        public bool PoseFull(string actor, float t, out Vec3 pos, out float[] quat)
        {
            pos = default;
            quat = null;
            if (!Tracks.TryGetValue(Name + "ZPM_" + actor + "_t", out var tr) || !Tracks.TryGetValue(Name + "ZPM_" + actor + "_q", out var q)) return false;
            var p = tr.Sample(t * NisTrack.KeysPerSecond);
            var r = q.Sample(t * NisTrack.KeysPerSecond);
            pos = new Vec3(p[0], p[1], p.Length > 2 ? p[2] : 0);
            quat = r.Length >= 4 ? new[] { r[0], r[1], r[2], r[3] } : new[] { 0f, 0f, 0f, 1f };
            return true;
        }

        /// <summary>CAnimScene::CreateCarAnimationControllers: a channel exists when either its _t or _q track does.</summary>
        public bool HasTrack(string actor) =>
            Tracks.ContainsKey(Name + "ZPM_" + actor + "_t") || Tracks.ContainsKey(Name + "ZPM_" + actor + "_q");

        /// <summary>When the actor's track ends (seconds).</summary>
        public float TrackLength(string actor) =>
            Tracks.TryGetValue(Name + "ZPM_" + actor + "_t", out var tr) ? (tr.Keys.Length - 1) / NisTrack.KeysPerSecond : 0;
    }

    /// <summary>A decoded animation channel: values per key (3 for translation/scale, 4 for a quaternion).</summary>
    public sealed class NisTrack
    {
        public const float KeysPerSecond = 15f; // one key every second frame at 30 fps
        public int Type, Dofs;
        public float[][] Keys;

        public float[] Sample(float key)
        {
            if (Keys.Length == 0) return new float[Dofs];
            if (key <= 0) return Keys[0];
            int i = (int)key;
            if (i >= Keys.Length - 1) return Keys[Keys.Length - 1];
            float f = key - i;
            var o = new float[Dofs];
            for (int k = 0; k < Dofs; k++) o[k] = Keys[i][k] + (Keys[i + 1][k] - Keys[i][k]) * f;
            if (Type == 0x0B)
            {
                float n = 0;
                for (int k = 0; k < Dofs; k++) n += o[k] * o[k];
                n = (float)Math.Sqrt(Math.Max(n, 1e-12f));
                for (int k = 0; k < Dofs; k++) o[k] /= n;
            }
            return o;
        }
    }

    /// <summary>
    /// EAGL4 animation bank (NIS chunk 0xE34010): a relocatable MIPS ELF whose .data holds the bank
    /// (__AnimationBank -> header: +4 count, +0x10 animation pointers, +0x14 name pointers); .rel.data only fixes
    /// pointers, which are already section offsets. An animation of type 0x0F (compound, speed.exe vtable 0x895DF0)
    /// is {u16 type, u16 flags, ptr, u16 child count, u16, child ptr...}; each child, type 0x0A (vector, vtable
    /// 0x896048) or 0x0B (quaternion, 0x896098), is {u16 type, u16 flags, ptr block, u16 key count, u16 output slot
    /// per value}. The block (decoder 0x48FF00) is {u16 value count, u8 bits 4/8/16, u8, per value {f32 offset,
    /// f32 scale, f32 first key}}, then one record per key step of quantised deltas:
    /// value[k] = value[k-1] + offset + q * scale. Quaternions are renormalised after interpolation.
    /// </summary>
    public static class EaglAnimBank
    {
        public static void Read(byte[] d, int start, int end, Dictionary<string, NisTrack> output)
        {
            int elf = start;
            while (elf + 4 < end && !(d[elf] == 0x7F && d[elf + 1] == 0x45 && d[elf + 2] == 0x4C && d[elf + 3] == 0x46)) elf++;
            if (elf + 52 > end) return;
            int shoff = Chunks.I32(d, elf + 32), shnum = Chunks.U16(d, elf + 48);
            int dataOff = -1, dataSize = 0, symOff = -1, symSize = 0, strOff = -1;
            for (int i = 0; i < shnum; i++)
            {
                int sh = elf + shoff + i * 40;
                int type = Chunks.I32(d, sh + 4), off = Chunks.I32(d, sh + 16), size = Chunks.I32(d, sh + 20), link = Chunks.I32(d, sh + 24);
                if (type == 1 && dataOff < 0) { dataOff = elf + off; dataSize = size; }
                else if (type == 2) { symOff = elf + off; symSize = size; strOff = elf + Chunks.I32(d, elf + shoff + link * 40 + 16); }
            }
            if (dataOff < 0 || symOff < 0 || dataOff + dataSize > d.Length) return;
            var data = new byte[dataSize];
            Buffer.BlockCopy(d, dataOff, data, 0, dataSize);
            int bank = -1;
            for (int s = symOff; s + 16 <= symOff + symSize; s += 16)
                if (Chunks.CString(d, strOff + Chunks.I32(d, s), 128).StartsWith("__AnimationBank")) bank = Chunks.I32(d, s + 4);
            if (bank < 0 || bank + 4 > dataSize) return;
            int hdr = bank;   // the symbol marks the bank header
            if (hdr + 0x18 > dataSize) return;
            int count = Chunks.I32(data, hdr + 4), anims = Chunks.I32(data, hdr + 0x10), names = Chunks.I32(data, hdr + 0x14);
            for (int i = 0; i < count && anims + i * 4 + 4 <= dataSize && names + i * 4 + 4 <= dataSize; i++)
            {
                int a = Chunks.I32(data, anims + i * 4);
                string name = Chunks.CString(data, Chunks.I32(data, names + i * 4), 128);
                if (a <= 0 || a + 0x10 > dataSize || Chunks.U16(data, a) != 0x0F) continue;
                int child = Chunks.I32(data, a + 0xC);
                if (child <= 0 || child + 0x10 > dataSize) continue;
                var track = Decode(data, Chunks.U16(data, child), Chunks.I32(data, child + 4), Chunks.U16(data, child + 8));
                if (track != null) output[name] = track;
            }
        }

        static NisTrack Decode(byte[] d, int type, int block, int keys)
        {
            if (block <= 0 || block + 4 > d.Length || keys <= 0) return null;
            int n = Chunks.U16(d, block), bits = d[block + 2];
            if (n <= 0 || n > 16 || (bits != 4 && bits != 8 && bits != 16)) return null;
            int rec = block + 4 + 12 * n;
            int size = bits == 4 ? (n + 1) / 2 : bits == 8 ? n : 2 * n;
            var t = new NisTrack { Type = type, Dofs = n, Keys = new float[keys][] };
            var cur = new float[n];
            for (int i = 0; i < n; i++) cur[i] = Chunks.F32(d, block + 12 + 12 * i);
            t.Keys[0] = (float[])cur.Clone();
            for (int k = 1; k < keys; k++)
            {
                int r = rec + (k - 1) * size;
                if (r + size <= d.Length)
                    for (int i = 0; i < n; i++)
                    {
                        int q = bits == 4 ? (d[r + i / 2] >> (4 * (i & 1))) & 15 : bits == 8 ? d[r + i] : Chunks.U16(d, r + 2 * i);
                        cur[i] += Chunks.F32(d, block + 4 + 12 * i) + q * Chunks.F32(d, block + 8 + 12 * i);
                    }
                t.Keys[k] = (float[])cur.Clone();
            }
            return t;
        }
    }

    /// <summary>
    /// NIS scene headers from NIS\Scene_&lt;name&gt;_BundleB.bun. Each bundle holds chunk 0xE34010, an EAGL animation
    /// bank (an ELF whose channels are ZPM-compressed rotation/scale/translation per actor, e.g. IntroNis01ZPM_Car1_q),
    /// 0x3B811, a 'PRAC' event pack (srtS / Ae Ee Se Te tracks), and 0x80037020 { 0x37030 header: u32 name hash,
    /// name[28], u32 ?, u32 actor count, u32 ?, u32 frames at 30 fps, description at +0x40 }. GLOBAL\InGameB.bun chunk
    /// 0x80037050 lists the bundle file names. Car and cop motion is decoded (EaglAnimBank); the camera paths are not in
    /// these banks, so the runtime stages its own shots.
    /// </summary>
    public static class NisLibrary
    {
        static Dictionary<string, NisScene> _scenes;

        public static IReadOnlyDictionary<string, NisScene> Scenes => _scenes ?? (IReadOnlyDictionary<string, NisScene>)new Dictionary<string, NisScene>();

        public static void Load(string gameRoot)
        {
            if (_scenes != null) return;
            _scenes = new Dictionary<string, NisScene>(StringComparer.OrdinalIgnoreCase);
            var dir = Path.Combine(gameRoot, "NIS");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "Scene_*.bun"))
            {
                var d = System.IO.File.ReadAllBytes(f);
                foreach (var c in Chunks.Read(d, 0, d.Length))
                {
                    if (c.Id != 0x80037020) continue;
                    foreach (var h in Chunks.Read(d, c.DataStart, c.End))
                    {
                        if (h.Id != 0x37030 || h.End - h.DataStart < 0x48) continue;
                        var s = Read(d, h.DataStart, h.End);
                        s.File = f;
                        if (s.Name.Length > 0) _scenes[s.Name] = s;
                    }
                }
            }
        }

        static NisScene Read(byte[] d, int o, int end) => new NisScene
        {
            Name = CString(d, o + 4, 16),
            SceneType = Chunks.I32(d, o + 0x18),
            HaveLayout = Chunks.I32(d, o + 0x1C),
            HaveCarAnimation = Chunks.I32(d, o + 0x20),
            Actors = Chunks.I32(d, o + 0x24),
            StartFrame = Chunks.I32(d, o + 0x28),
            Frames = Chunks.I32(d, o + 0x2C),
            OverlayName = CString(d, o + 0x30, 16),
            Description = CString(d, o + 0x40, end - (o + 0x40)),
        };

        static string CString(byte[] d, int o, int max)
        {
            int n = 0;
            while (n < max && o + n < d.Length && d[o + n] != 0) n++;
            return Encoding.ASCII.GetString(d, o, n);
        }

        public static NisScene Get(string name) => name != null && _scenes != null && _scenes.TryGetValue(name, out var s) ? s : null;
    }
}
