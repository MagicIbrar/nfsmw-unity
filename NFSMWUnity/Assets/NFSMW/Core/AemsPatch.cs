using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// The patch area of an AEMS bank (".abk", "ABKC"): the compiled Csis classes that turn a class's parameters into
    /// sample playback. Each class is x86 "link" code (it only wires a node graph) plus a data block; the data blocks
    /// point (through the bank's data relocation list) at shared tables after them. Two table kinds are read here
    /// [RE from the banks, 2026-10-24; node semantics around them not decoded]:
    /// <list type="bullet">
    /// <item>Sample select: {u32 count, count x {u32 0xFF00, u32 slot, u32 0}}: parameter value n plays bank slot
    /// table[n] (0 = nothing). E.g. FE_COMMON_MB PlayCommonSample (23 = UISND_COMMON_MAX_NUM entries), FE_MB
    /// PlayFrontEndSample (137 = triggers 23..159), ENV_COMMON_MB ENV_STATIC (12), FX_Misc_MB FX_UVES {2,3,4}.</item>
    /// <item>Key map: {u16 element bytes (1/2/4), u16 count, s32 min, s32 max, f32 (count-1)/(max-min)} then count
    /// values: a parameter in [min,max] picks entry (key-min) x scale. E.g. FX_Scrape's impulse_magnitude 0..127 →
    /// volume (0..0x7FFF) and pitch (x4096).</item>
    /// </list>
    /// Header: +0x18 BNKl offset, +0x34 data relocation list {u32 n, u32 file offsets of pointers}, +0x38 class list
    /// {u32 n, n x {u32 module header, u32 name record, u32 hash}}; module header +0x24 code, +0x28 data, +0x2C data size;
    /// name record {u16 project, u16 class (the Csis InterfaceId pair), char name[]}.
    /// </summary>
    public sealed class AemsPatch
    {
        public sealed class KeyMap
        {
            public int Offset, ElementBytes, Min, Max;
            public float Scale;
            public int[] Values = Array.Empty<int>();

            /// <summary>The entry for a key (clamped; u16 values read signed when <paramref name="signed"/>).</summary>
            public int Lookup(int key, bool signed = false)
            {
                if (Values.Length == 0) return 0;
                int i = (int)((key - Min) * Scale);
                i = i < 0 ? 0 : i >= Values.Length ? Values.Length - 1 : i;
                int v = Values[i];
                if (signed && ElementBytes == 2) v = (short)v;
                return v;
            }
        }

        public sealed class Module
        {
            public string Name;
            public int Project, ClassId, Header, Code, Data, DataSize;
            /// <summary>Sample-select tables the module's data points at, in data order (duplicates removed).</summary>
            public readonly List<int[]> Selects = new List<int[]>();
            public readonly List<KeyMap> Maps = new List<KeyMap>();
        }

        public readonly List<Module> Modules = new List<Module>();

        public Module Find(string name)
        {
            foreach (var m in Modules) if (m.Name == name) return m;
            return null;
        }

        /// <summary>The first sample-select table of a class (null if none).</summary>
        public int[] Select(string cls, int index = 0)
        {
            var m = Find(cls);
            return m != null && index < m.Selects.Count ? m.Selects[index] : null;
        }

        static readonly Dictionary<string, AemsPatch> Cache = new Dictionary<string, AemsPatch>();

        public static AemsPatch Load(string path)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var p)) return p;
                try { p = File.Exists(path) ? Read(File.ReadAllBytes(path)) : new AemsPatch(); }
                catch (Exception) { p = new AemsPatch(); }
                Cache[path] = p;
                return p;
            }
        }

        static int I32(byte[] d, int o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToInt32(d, o) : 0;

        public static AemsPatch Read(byte[] d)
        {
            var p = new AemsPatch();
            if (d.Length < 0x40 || d[0] != 'A' || d[1] != 'B' || d[2] != 'K' || d[3] != 'C') return p;
            int bnk = I32(d, 0x18), relocs = I32(d, 0x34), classes = I32(d, 0x38);
            if (classes <= 0 || classes + 4 > d.Length || relocs <= 0 || relocs + 4 > d.Length) return p;
            int n = I32(d, classes);
            for (int i = 0; i < n && i < 64; i++)
            {
                int mh = I32(d, classes + 4 + i * 12), name = I32(d, classes + 8 + i * 12);
                if (mh <= 0 || mh + 0x30 > d.Length || name <= 0 || name + 4 > d.Length) continue;
                int e = name + 4;
                while (e < d.Length && d[e] != 0) e++;
                p.Modules.Add(new Module
                {
                    Name = System.Text.Encoding.ASCII.GetString(d, name + 4, e - name - 4),
                    Project = BitConverter.ToUInt16(d, name),
                    ClassId = BitConverter.ToUInt16(d, name + 2),
                    Header = mh,
                    Code = I32(d, mh + 0x24),
                    Data = I32(d, mh + 0x28),
                    DataSize = I32(d, mh + 0x2C),
                });
            }
            int nr = I32(d, relocs);
            var seen = new Dictionary<Module, HashSet<int>>();
            for (int r = 0; r < nr && r < 4096; r++)
            {
                int at = I32(d, relocs + 4 + r * 4), t = I32(d, at);
                if (t <= 0 || t + 16 > d.Length || (bnk > 0 && t >= bnk)) continue;
                foreach (var m in p.Modules)
                {
                    if (at < m.Data || at >= m.Data + m.DataSize) continue;
                    if (!seen.TryGetValue(m, out var s)) seen[m] = s = new HashSet<int>();
                    if (!s.Add(t)) break;
                    var map = ReadMap(d, t);
                    if (map != null) { m.Maps.Add(map); break; }
                    var sel = ReadSelect(d, t);
                    if (sel != null) m.Selects.Add(sel);
                    break;
                }
            }
            return p;
        }

        static KeyMap ReadMap(byte[] d, int t)
        {
            int size = BitConverter.ToUInt16(d, t), count = BitConverter.ToUInt16(d, t + 2);
            if (size != 1 && size != 2 && size != 4 || count == 0 || t + 16 + count * size > d.Length) return null;
            int min = I32(d, t + 4), max = I32(d, t + 8);
            float scale = BitConverter.ToSingle(d, t + 12);
            if (max <= min || scale <= 0 || Math.Abs(scale - (count - 1f) / (max - min)) > 0.01f * scale + 1e-4f) return null;
            var m = new KeyMap { Offset = t, ElementBytes = size, Min = min, Max = max, Scale = scale, Values = new int[count] };
            for (int i = 0; i < count; i++)
                m.Values[i] = size == 1 ? d[t + 16 + i] : size == 2 ? BitConverter.ToUInt16(d, t + 16 + i * 2) : I32(d, t + 16 + i * 4);
            return m;
        }

        static int[] ReadSelect(byte[] d, int t)
        {
            int c = I32(d, t);
            if (c <= 0 || c > 1024 || t + 4 + c * 12 > d.Length) return null;
            var r = new int[c];
            for (int k = 0; k < c; k++)
            {
                if (I32(d, t + 4 + k * 12) != 0xFF00) return null;
                r[k] = I32(d, t + 8 + k * 12);
            }
            return r;
        }
    }
}
