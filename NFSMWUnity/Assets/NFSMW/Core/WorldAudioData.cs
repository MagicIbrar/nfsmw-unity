using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// A parameter-map layer (World/ParameterMaps.cpp; TRACKS\L2RA.BUN chunk 0x8003B600, one 0x8003B601 per layer):
    /// 0x3B602 header {u32 bStringHash(name), float quad left, top (min y), right, bottom (max y), int quad nodes,
    /// int parameter sets, int set size, int fields}, 0x3B603 field types (0 float, 1 int), 0x3B604 field offsets,
    /// 0x3B605 the sets, 0x3B607 / 0x3B608 a quadtree of u8 / u16 {child0..3} nodes (child0 = 0: leaf, child1 = set).
    /// GetParameterSetIndexFromQuadData (ParameterMaps.cpp) splits at the centre: x &lt; cx and y &lt; cy → child0,
    /// x &lt; cx → child1, y &lt; cy → child2, else child3. Point (x, y) is game space = Unity (x, z).
    /// Audio uses "Ambience" (0x3B055833: one int, the ambient zone 0..13) and "Reverb" (0x79B2E325: int zone,
    /// float, int FX offset in hundredths of dB).
    /// </summary>
    public sealed class ParamMapLayer
    {
        public uint NameHash;
        public float Left, Top, Right, Bottom;
        public int Sets, SetSize, Fields, Nodes;
        public int[] Types = Array.Empty<int>(), Offsets = Array.Empty<int>();
        byte[] _data = Array.Empty<byte>();
        byte[] _q8;
        ushort[] _q16;

        public static uint Hash(string s)
        {
            uint x = 0xFFFFFFFF;
            foreach (char c in s) x = x * 33 + (byte)c;
            return x;
        }

        public static List<ParamMapLayer> Read(byte[] d)
        {
            var list = new List<ParamMapLayer>();
            if (d == null) return list;
            foreach (var root in Chunks.FindAll(d, 0, d.Length, 0x8003B600))
                foreach (var lay in Chunks.Children(d, root))
                {
                    if (lay.Id != 0x8003B601) continue;
                    var l = new ParamMapLayer();
                    foreach (var c in Chunks.Children(d, lay))
                    {
                        int o = c.DataStart, n = c.End - c.DataStart;
                        switch (c.Id)
                        {
                            case 0x3B602:
                                l.NameHash = Chunks.U32(d, o);
                                l.Left = Chunks.F32(d, o + 4); l.Top = Chunks.F32(d, o + 8); l.Right = Chunks.F32(d, o + 12); l.Bottom = Chunks.F32(d, o + 16);
                                l.Nodes = Chunks.I32(d, o + 20); l.Sets = Chunks.I32(d, o + 24); l.SetSize = Chunks.I32(d, o + 28); l.Fields = Chunks.I32(d, o + 32);
                                break;
                            case 0x3B603: l.Types = Ints(d, o, n / 4); break;
                            case 0x3B604: l.Offsets = Ints(d, o, n / 4); break;
                            case 0x3B605: l._data = new byte[n]; Buffer.BlockCopy(d, o, l._data, 0, n); break;
                            case 0x3B607: l._q8 = new byte[n]; Buffer.BlockCopy(d, o, l._q8, 0, n); break;
                            case 0x3B608:
                                l._q16 = new ushort[n / 2];
                                for (int i = 0; i < l._q16.Length; i++) l._q16[i] = Chunks.U16(d, o + 2 * i);
                                break;
                        }
                    }
                    list.Add(l);
                }
            return list;
        }

        static int[] Ints(byte[] d, int o, int n)
        {
            var r = new int[n];
            for (int i = 0; i < n; i++) r[i] = Chunks.I32(d, o + 4 * i);
            return r;
        }

        /// <summary>GetParameterSetIndexFromQuadData8/16.</summary>
        public int SetIndex(float x, float y)
        {
            float l = Left, t = Top, r = Right, b = Bottom;
            int cur = 0;
            for (int guard = 0; guard < 64; guard++)
            {
                int c0, c1, c2, c3;
                if (_q8 != null) { if (cur * 4 + 3 >= _q8.Length) return 0; c0 = _q8[cur * 4]; c1 = _q8[cur * 4 + 1]; c2 = _q8[cur * 4 + 2]; c3 = _q8[cur * 4 + 3]; }
                else if (_q16 != null) { if (cur * 4 + 3 >= _q16.Length) return 0; c0 = _q16[cur * 4]; c1 = _q16[cur * 4 + 1]; c2 = _q16[cur * 4 + 2]; c3 = _q16[cur * 4 + 3]; }
                else return 0;
                if (c0 == 0) return c1;
                float cx = (l + r) * 0.5f, cy = (t + b) * 0.5f;
                if (x < cx) { r = cx; if (y < cy) { b = cy; cur = c0; } else { t = cy; cur = c1; } }
                else { l = cx; if (y < cy) { b = cy; cur = c2; } else { t = cy; cur = c3; } }
            }
            return 0;
        }

        public int GetInt(float x, float y, int field)
        {
            int s = SetIndex(x, y), o = s * SetSize + (field < Offsets.Length ? Offsets[field] : 0);
            return o >= 0 && o + 4 <= _data.Length ? BitConverter.ToInt32(_data, o) : 0;
        }

        public float GetFloat(float x, float y, int field)
        {
            int s = SetIndex(x, y), o = s * SetSize + (field < Offsets.Length ? Offsets[field] : 0);
            return o >= 0 && o + 4 <= _data.Length ? BitConverter.ToSingle(_data, o) : 0f;
        }
    }

    /// <summary>
    /// TrackPathZone records (World/TrackPath.hpp:68-84; chunk 0x3414A in L2RA.BUN): {i32 type, vec2 position, vec2
    /// direction, float elevation, s8 source, s8 cached index, s16 visit info, ptr, vec2 bbox min, vec2 bbox max,
    /// i32 data[4], u16 point count (+0x40), u16 record size (+0x42), (x, y) points}. Types: 3 tunnel, 4 overpass,
    /// 5 small overpass, 7 garage, 10 dynamic, 12 jump cam ... TrackPathManager::FindZone tests the polygon.
    /// </summary>
    public sealed class TrackZone
    {
        public const int Tunnel = 3, Overpass = 4, OverpassSmall = 5, Garage = 7, TrafficPattern = 9, Dynamic = 10, JumpCam = 12;
        public int Type, VisitInfo, Index;
        public float Elevation, MinX, MinY, MaxX, MaxY;
        public uint Data0;
        public float[] Px = Array.Empty<float>(), Py = Array.Empty<float>();

        public bool Contains(float x, float y)
        {
            if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
            bool inside = false;
            for (int i = 0, j = Px.Length - 1; i < Px.Length; j = i++)
                if ((Py[i] > y) != (Py[j] > y) && x < (Px[j] - Px[i]) * (y - Py[i]) / (Py[j] - Py[i]) + Px[i]) inside = !inside;
            return inside;
        }

        public static List<TrackZone> Read(byte[] d)
        {
            var list = new List<TrackZone>();
            if (d == null) return list;
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x3414A))
            {
                int o = c.DataStart;
                while (o < c.End && d[o] == 0x11) o++;
                for (int guard = 0; o + 0x44 <= c.End && guard < 100000; guard++)
                {
                    int size = Chunks.U16(d, o + 0x42), n = Chunks.U16(d, o + 0x40);
                    if (o + 0x44 + n * 8 <= c.End)
                    {
                        var z = new TrackZone
                        {
                            Type = Chunks.I32(d, o), Elevation = Chunks.F32(d, o + 0x14), VisitInfo = Chunks.I16(d, o + 0x1A),
                            MinX = Chunks.F32(d, o + 0x20), MinY = Chunks.F32(d, o + 0x24), MaxX = Chunks.F32(d, o + 0x28), MaxY = Chunks.F32(d, o + 0x2C),
                            Data0 = Chunks.U32(d, o + 0x30), Px = new float[n], Py = new float[n], Index = list.Count,
                        };
                        for (int i = 0; i < n; i++) { z.Px[i] = Chunks.F32(d, o + 0x44 + i * 8); z.Py[i] = Chunks.F32(d, o + 0x48 + i * 8); }
                        list.Add(z);
                    }
                    if (size <= 0) break;
                    o += size;
                }
            }
            return list;
        }
    }
}
