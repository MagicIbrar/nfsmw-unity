using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// The AI road graph in TRACKS\L2RA.BUN chunk 0x3B800 (WRoadNetwork::Init, PC 0x789980), stored in a "CARP"
    /// pack: 'CARP', entry count, 0, 1, then 16-byte entries {tag, (size &lt;&lt; 8) | flags, count, offset} where flag 2
    /// means the offset is relative to the entry. Road blocks (WRoadElem.h; specs/ai_racer.md §1):
    ///   RNhd WRoadNetworkInfo: u16 profiles, nodes, segments, intersections, roads, junctions
    ///   RNnd WRoadNode 0x20: sim Vector3 position (game X = z, game Y = -x, height = y), s16 index, s16 profile index,
    ///        u8 segment count, pad, u16 segment refs[7]
    ///   RNsg WRoadSegment 0x16: u16 node[2], u16 length (n·1000/65535 m), s16 road, s16 index, u16 flags,
    ///        u16 end/start handle length, s8[3] end/start handle (sim axes)
    ///   RNpf WRoadProfile 0x40: u8 zones, u8 middle zone, pad[2], u32 lanes[15] (type 4 bits, width and offset 14 bits
    ///        signed × 100/8191 m)
    ///   RNrd WRoad 8: u16 scale, u16 length, u8 shortcut, u8 min width, u16 speech id
    /// </summary>
    public sealed class RoadNetwork
    {
        public Vec3[] Nodes = Array.Empty<Vec3>();          // game space
        public (int a, int b)[] Segments = Array.Empty<(int, int)>();
        public List<int>[] Links;                             // node -> neighbour nodes

        public struct Lane { public int Type; public float Width, Offset; }
        public sealed class Profile { public int Zones, Middle; public Lane[] Lanes = new Lane[15]; }
        public struct Segment
        {
            public int A, B, Road, Flags;
            public float Length;
            /// <summary>Spline handles from each end, in game space (metres): start control = node A + StartHandle.</summary>
            public Vec3 StartHandle, EndHandle;
        }
        /// <summary>WRoad (WRoadElem.h:47-68, 8 bytes): nScale (u16, x256/65536), nLength (u16), nShortcut, nMinWidth, nSpeechId (u16 at +6).</summary>
        public struct RoadInfo { public float Scale, Length; public int Shortcut, MinWidth, SpeechId; }

        // WRoadSegment flags (WRoadElem.h:212)
        public const int SegDecision = 1, SegNoTraffic = 2, SegRaceForward = 4, SegIntersection = 8, SegEntrance = 0x10,
            SegCopsXorTraffic = 0x20, SegOneWay = 0x40, SegShortcut = 0x80, SegCurved = 0x100, SegEndInverted = 0x200,
            SegStartInverted = 0x400, SegChopperLow = 0x800, SegBarrier = 0x1000, SegDriveThroughBarrier = 0x2000,
            SegInRace = 0x8000;

        public Profile[] Profiles = Array.Empty<Profile>();
        public short[] NodeProfile = Array.Empty<short>();
        public List<int>[] NodeSegments = Array.Empty<List<int>>();
        public Segment[] Segs = Array.Empty<Segment>();
        public RoadInfo[] RoadInfos = Array.Empty<RoadInfo>();

        /// <summary>sim (x, y, z) -> game (z, -x, y)</summary>
        static Vec3 SimToGame(float x, float y, float z) => new Vec3(z, -x, y);
        float _cell = 100f;
        readonly Dictionary<(int, int), List<int>> _grid = new Dictionary<(int, int), List<int>>();

        public static RoadNetwork Read(byte[] bundle)
        {
            foreach (var c in Chunks.Read(bundle, 0, bundle.Length))
                if (c.Id == 0x3B800) return Parse(bundle, Chunks.Align(c.DataStart, 16), c.End);
            return new RoadNetwork { Links = Array.Empty<List<int>>() };
        }

        static RoadNetwork Parse(byte[] d, int carp, int end)
        {
            var net = new RoadNetwork();
            if (Chunks.U32(d, carp) != 0x43415250) throw new FormatException("road network: not a CARP pack"); // 'CARP'
            int n = Chunks.I32(d, carp + 4);
            var blocks = new Dictionary<string, (int at, int size, int count)>();
            for (int i = 0; i < n; i++)
            {
                int e = carp + 16 + i * 16;
                string tag = new string(new[] { (char)d[e + 3], (char)d[e + 2], (char)d[e + 1], (char)d[e] });
                uint a = Chunks.U32(d, e + 4);
                int count = Chunks.I32(d, e + 8), off = Chunks.I32(d, e + 12);
                int at = (a & 2) != 0 ? e + off : off;
                if (!blocks.ContainsKey(tag)) blocks[tag] = (at, (int)(a >> 8), count);
            }
            if (!blocks.TryGetValue("RNnd", out var nd) || !blocks.TryGetValue("RNsg", out var sg)) return net;
            int nodes = nd.size / 32, segs = sg.size / 22;
            net.Nodes = new Vec3[nodes];
            for (int i = 0; i < nodes; i++)
            {
                int o = nd.at + i * 32;
                float a = Chunks.F32(d, o), b = Chunks.F32(d, o + 4), c = Chunks.F32(d, o + 8);
                net.Nodes[i] = new Vec3(c, -a, b);
            }
            net.Segments = new (int, int)[segs];
            net.Links = new List<int>[nodes];
            for (int i = 0; i < nodes; i++) net.Links[i] = new List<int>(4);
            for (int i = 0; i < segs; i++)
            {
                int o = sg.at + i * 22;
                int na = Chunks.U16(d, o), nb = Chunks.U16(d, o + 2);
                net.Segments[i] = (na, nb);
                if (na < nodes && nb < nodes && na != nb)
                {
                    if (!net.Links[na].Contains(nb)) net.Links[na].Add(nb);
                    if (!net.Links[nb].Contains(na)) net.Links[nb].Add(na);
                }
            }
            for (int i = 0; i < nodes; i++)
            {
                var key = net.Cell(net.Nodes[i].X, net.Nodes[i].Y);
                if (!net._grid.TryGetValue(key, out var list)) net._grid[key] = list = new List<int>();
                list.Add(i);
            }

            // the full records: node profiles and segment lists, segment flags/lengths/handles, lane profiles, roads
            net.NodeProfile = new short[nodes];
            net.NodeSegments = new List<int>[nodes];
            for (int i = 0; i < nodes; i++)
            {
                int o = nd.at + i * 32;
                net.NodeProfile[i] = (short)Chunks.U16(d, o + 0x0E);
                int ns = d[o + 0x10];
                net.NodeSegments[i] = new List<int>(ns);
                for (int k = 0; k < ns && k < 7; k++) net.NodeSegments[i].Add(Chunks.U16(d, o + 0x12 + k * 2));
            }
            net.Segs = new Segment[segs];
            for (int i = 0; i < segs; i++)
            {
                int o = sg.at + i * 22;
                float endLen = Chunks.U16(d, o + 0x0C), startLen = Chunks.U16(d, o + 0x0E);
                const float hs = 500f / (127f * 65535f);
                Vec3 Handle(int at, float len) => SimToGame((sbyte)d[at] * len * hs, (sbyte)d[at + 1] * len * hs, (sbyte)d[at + 2] * len * hs);
                net.Segs[i] = new Segment
                {
                    A = Chunks.U16(d, o), B = Chunks.U16(d, o + 2),
                    Length = Chunks.U16(d, o + 4) * 1000f / 65535f,
                    Road = (short)Chunks.U16(d, o + 6),
                    Flags = Chunks.U16(d, o + 0x0A),
                    EndHandle = Handle(o + 0x10, endLen),
                    StartHandle = Handle(o + 0x13, startLen),
                };
            }
            if (blocks.TryGetValue("RNpf", out var pf))
            {
                int np = pf.size / 64;
                net.Profiles = new Profile[np];
                for (int i = 0; i < np; i++)
                {
                    int o = pf.at + i * 64;
                    var p = new Profile { Zones = d[o], Middle = d[o + 1] };
                    for (int l = 0; l < 15; l++)
                    {
                        uint v = Chunks.U32(d, o + 4 + l * 4);
                        int w = (int)((v >> 4) & 0x3FFF), off = (int)(v >> 18);
                        if (w >= 0x2000) w -= 0x4000;
                        if (off >= 0x2000) off -= 0x4000;
                        p.Lanes[l] = new Lane { Type = (int)(v & 15), Width = w * 100f / 8191f, Offset = off * 100f / 8191f };
                    }
                    net.Profiles[i] = p;
                }
            }
            if (blocks.TryGetValue("RNrd", out var rd))
            {
                int nr = rd.size / 8;
                net.RoadInfos = new RoadInfo[nr];
                for (int i = 0; i < nr; i++)
                {
                    int o = rd.at + i * 8;
                    net.RoadInfos[i] = new RoadInfo
                    {
                        Scale = (Chunks.U16(d, o) << 8) / 65536f, Length = Chunks.U16(d, o + 2), Shortcut = d[o + 4], MinWidth = d[o + 5], SpeechId = Chunks.U16(d, o + 6),
                    };
                }
            }
            return net;
        }

        /// <summary>
        /// TrackPathBarrier (chunk 0x3414D, 0x18 bytes): a 2-D line in game space, enabled with its scenery group;
        /// PlayerBarrier marks a drive-through one (WRoadNetwork.cpp ResolveBarriers).
        /// </summary>
        public struct PathBarrier { public float X0, Y0, X1, Y1; public bool PlayerBarrier, LeftHanded; public uint GroupHash; }

        public static List<PathBarrier> ReadBarriers(byte[] bundle)
        {
            var list = new List<PathBarrier>();
            foreach (var c in Chunks.FindAll(bundle, 0, bundle.Length, 0x3414D))
            {
                int o = c.DataStart;
                while (o < c.End && bundle[o] == 0x11) o++;
                for (; o + 0x18 <= c.End; o += 0x18)
                    list.Add(new PathBarrier
                    {
                        X0 = Chunks.F32(bundle, o), Y0 = Chunks.F32(bundle, o + 4), X1 = Chunks.F32(bundle, o + 8), Y1 = Chunks.F32(bundle, o + 12),
                        PlayerBarrier = bundle[o + 0x12] != 0, LeftHanded = bundle[o + 0x13] != 0, GroupHash = Chunks.U32(bundle, o + 0x14),
                    });
            }
            return list;
        }

        (int, int) Cell(float x, float y) => ((int)Math.Floor(x / _cell), (int)Math.Floor(y / _cell));

        /// <summary>Nearest node to a game-space point (2D), or -1.</summary>
        public int Nearest(float x, float y, float maxDist = 1500f)
        {
            int best = -1;
            float bestD = maxDist * maxDist;
            var (cx, cy) = Cell(x, y);
            int r = (int)Math.Ceiling(maxDist / _cell);
            for (int ring = 0; ring <= r; ring++)
            {
                for (int gx = cx - ring; gx <= cx + ring; gx++)
                    for (int gy = cy - ring; gy <= cy + ring; gy++)
                    {
                        if (Math.Abs(gx - cx) != ring && Math.Abs(gy - cy) != ring) continue;
                        if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                        foreach (int i in list)
                        {
                            float dx = Nodes[i].X - x, dy = Nodes[i].Y - y, dd = dx * dx + dy * dy;
                            if (dd < bestD) { bestD = dd; best = i; }
                        }
                    }
                if (best >= 0 && (ring + 1) * _cell > Math.Sqrt(bestD) + _cell) break;
            }
            return best;
        }

        float Dist(int a, int b)
        {
            float dx = Nodes[a].X - Nodes[b].X, dy = Nodes[a].Y - Nodes[b].Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>A* shortest route between nodes; empty if unreachable.</summary>
        public List<int> Route(int from, int to)
        {
            var path = new List<int>();
            if (from < 0 || to < 0) return path;
            if (from == to) { path.Add(from); return path; }
            var g = new Dictionary<int, float> { [from] = 0 };
            var came = new Dictionary<int, int>();
            var open = new SortedSet<(float f, int n)> { (Dist(from, to), from) };
            var closed = new HashSet<int>();
            while (open.Count > 0)
            {
                var cur = open.Min;
                open.Remove(cur);
                int n = cur.n;
                if (n == to)
                {
                    for (int x = to; ; x = came[x])
                    {
                        path.Add(x);
                        if (x == from) break;
                    }
                    path.Reverse();
                    return path;
                }
                if (!closed.Add(n)) continue;
                foreach (int m in Links[n])
                {
                    if (closed.Contains(m)) continue;
                    float ng = g[n] + Dist(n, m);
                    if (g.TryGetValue(m, out float old) && old <= ng) continue;
                    g[m] = ng;
                    came[m] = n;
                    open.Add((ng + Dist(m, to), m));
                }
            }
            return path;
        }

        /// <summary>
        /// Route through game-space waypoints (e.g. race checkpoints). Graph nodes behind the previous waypoint or
        /// right next to the next one are dropped, so the path never doubles back at a checkpoint.
        /// </summary>
        public List<Vec3> RouteThrough(IList<Vec3> waypoints)
        {
            var pts = new List<Vec3>();
            int prev = -1;
            Vec3 prevW = default;
            bool havePrev = false;
            foreach (var w in waypoints)
            {
                int node = Nearest(w.X, w.Y);
                if (havePrev && prev >= 0 && node >= 0)
                {
                    float dx = w.X - prevW.X, dy = w.Y - prevW.Y;
                    var r = Route(prev, node);
                    for (int i = 0; i < r.Count; i++)
                    {
                        var p = Nodes[r[i]];
                        float px = p.X - prevW.X, py = p.Y - prevW.Y;
                        if (px * dx + py * dy < 0) continue;                   // behind the last waypoint
                        float qx = p.X - w.X, qy = p.Y - w.Y;
                        if (qx * qx + qy * qy < 15f * 15f) continue;           // the waypoint itself follows
                        if (px * px + py * py < 10f * 10f) continue;
                        pts.Add(p);
                    }
                }
                pts.Add(w);
                prev = node;
                prevW = w;
                havePrev = true;
            }
            return pts;
        }
    }
}
