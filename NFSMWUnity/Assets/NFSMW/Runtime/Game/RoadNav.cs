using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The road network in Unity space for the AI (WRoadNetwork, World/Common/WRoadNetwork.cpp; specs/ai_racer.md §1-3).
    /// Segments are cubic Bezier curves from node A (+ start handle) to node B (+ end handle), straight when not flagged
    /// curved. Lane profiles give each end a set of zones (type, centre offset, width), mirrored by the end's Inverted
    /// flag. Runtime flags: InRace / RaceRouteForward (the race route), CrossesBarrier (enabled TrackPathBarriers).
    /// </summary>
    public static partial class RoadGraph
    {
        public sealed class Seg
        {
            public int Index, A, B, Flags, Road;
            public float Length, Chord;
            public Vector3 P0, P1, P2, P3;
            public Vector3[] Samples;    // Bezier at t = i/(n-1)
            public Vector3 Min, Max;
            public bool Has(int f) => (Flags & f) != 0;
        }

        public struct Zone { public int Type; public float Offset, Width; }

        static Seg[] _segs;
        static Vector3[] _nodes;
        static readonly Dictionary<(int, int), List<int>> _cells = new Dictionary<(int, int), List<int>>();
        const float Cell = 40f;
        static List<RoadNetwork.PathBarrier> _barriers;

        public static RoadNetwork Net => Roads.Net;
        public static Seg[] Segs { get { Build(); return _segs; } }
        public static Vector3 Node(int i) { Build(); return _nodes[i]; }

        static void Build()
        {
            if (_segs != null) return;
            var net = Net;
            _nodes = new Vector3[net.Nodes.Length];
            for (int i = 0; i < _nodes.Length; i++) _nodes[i] = Coords.ToUnity(net.Nodes[i]);
            _segs = new Seg[net.Segs.Length];
            for (int i = 0; i < _segs.Length; i++)
            {
                var r = net.Segs[i];
                var s = new Seg { Index = i, A = r.A, B = r.B, Flags = r.Flags, Road = r.Road, Length = r.Length };
                s.P0 = _nodes[r.A];
                s.P3 = _nodes[r.B];
                s.Chord = Mathf.Max(0.01f, Vector3.Distance(s.P0, s.P3));
                if (s.Has(RoadNetwork.SegCurved))
                {
                    s.P1 = s.P0 + Coords.ToUnity(r.StartHandle);
                    s.P2 = s.P3 + Coords.ToUnity(r.EndHandle);
                }
                else
                {
                    s.P1 = Vector3.Lerp(s.P0, s.P3, 1f / 3f);
                    s.P2 = Vector3.Lerp(s.P0, s.P3, 2f / 3f);
                }
                int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(s.Length, s.Chord) / 4f), 2, 64) + 1;
                s.Samples = new Vector3[n];
                s.Min = Vector3.one * 1e9f;
                s.Max = -Vector3.one * 1e9f;
                for (int k = 0; k < n; k++)
                {
                    var p = Point(s, k / (n - 1f));
                    s.Samples[k] = p;
                    s.Min = Vector3.Min(s.Min, p);
                    s.Max = Vector3.Max(s.Max, p);
                }
                _segs[i] = s;
                for (int cx = Mathf.FloorToInt((s.Min.x - 20f) / Cell); cx <= Mathf.FloorToInt((s.Max.x + 20f) / Cell); cx++)
                    for (int cz = Mathf.FloorToInt((s.Min.z - 20f) / Cell); cz <= Mathf.FloorToInt((s.Max.z + 20f) / Cell); cz++)
                    {
                        if (!_cells.TryGetValue((cx, cz), out var list)) _cells[(cx, cz)] = list = new List<int>();
                        list.Add(i);
                    }
            }
        }

        /// <summary>The segment curve at t (0 = node A).</summary>
        public static Vector3 Point(Seg s, float t)
        {
            float u = 1f - t;
            return u * u * u * s.P0 + 3f * u * u * t * s.P1 + 3f * u * t * t * s.P2 + t * t * t * s.P3;
        }

        public static Vector3 Tangent(Seg s, float t)
        {
            float u = 1f - t;
            var d = 3f * u * u * (s.P1 - s.P0) + 6f * u * t * (s.P2 - s.P1) + 3f * t * t * (s.P3 - s.P2);
            d.y = 0;
            return d.sqrMagnitude > 1e-8f ? d.normalized : FlatDir(s.P3 - s.P0);
        }

        /// <summary>
        /// USpline::EvaluateCurvatureXZ (speed.exe 0x5D2E60, read from the exe): (x'z'' - z'x'') / |d1_xz|^3 of the cubic
        /// Bezier at t (A to B), from the first (0x5D0D10, basis 0x8A3200) and second (0x5D0D80, basis 0x8A3280) derivative
        /// bases of the Bezier basis 0x8A3180 [-1 3 -3 1; 3 -6 3 0; -3 3 0 0; 1 0 0 0] over (start, start+startControl,
        /// end+endControl, end); a denominator within +-0.0001 (a cusp) returns 1000. Straight segments have no curvature
        /// (WRoadNav::EvaluateSplines, WRoadNetwork.cpp:1370).
        /// </summary>
        public static float Curvature(Seg s, float t)
        {
            if (!s.Has(RoadNetwork.SegCurved)) return 0;
            float u = 1f - t;
            var d1 = 3f * u * u * (s.P1 - s.P0) + 6f * u * t * (s.P2 - s.P1) + 3f * t * t * (s.P3 - s.P2);
            var d2 = 6f * u * (s.P2 - 2f * s.P1 + s.P0) + 6f * t * (s.P3 - 2f * s.P2 + s.P1);
            float den = Mathf.Pow(d1.x * d1.x + d1.z * d1.z, 1.5f);
            return den <= 0.0001f ? 1000f : (d1.x * d2.z - d1.z * d2.x) / den;
        }

        public static Vector3 FlatDir(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; }
        public static Vector3 RightOf(Vector3 f) => new Vector3(f.z, 0, -f.x);
        /// <summary>Lateral offset of d against forward f, positive to the right.</summary>
        public static float Cross2(Vector3 d, Vector3 f) => d.x * f.z - d.z * f.x;

        public static IEnumerable<int> Near(Vector3 p, float radius)
        {
            Build();
            var seen = new HashSet<int>();
            for (int cx = Mathf.FloorToInt((p.x - radius) / Cell); cx <= Mathf.FloorToInt((p.x + radius) / Cell); cx++)
                for (int cz = Mathf.FloorToInt((p.z - radius) / Cell); cz <= Mathf.FloorToInt((p.z + radius) / Cell); cz++)
                    if (_cells.TryGetValue((cx, cz), out var list))
                        foreach (int i in list) if (seen.Add(i)) yield return i;
        }

        /// <summary>Closest point on a segment: its t and the distance (samples, then a local refinement).</summary>
        public static float Closest(Seg s, Vector3 p, out float t)
        {
            int best = 0;
            float bd = float.MaxValue;
            for (int k = 0; k < s.Samples.Length; k++)
            {
                var d = s.Samples[k] - p; d.y *= 0.5f;
                float dd = d.sqrMagnitude;
                if (dd < bd) { bd = dd; best = k; }
            }
            float step = 1f / (s.Samples.Length - 1);
            t = best * step;
            for (int it = 0; it < 6; it++)
            {
                step *= 0.5f;
                float ta = Mathf.Clamp01(t - step), tb = Mathf.Clamp01(t + step);
                float da = (Point(s, ta) - p).sqrMagnitude, db = (Point(s, tb) - p).sqrMagnitude, dc = (Point(s, t) - p).sqrMagnitude;
                if (da < dc && da <= db) t = ta; else if (db < dc) t = tb;
            }
            return Vector3.Distance(Point(s, t), p);
        }

        /// <summary>
        /// A segment end's zones, with offsets right-positive along A→B (WRoadProfile: zones below the middle zone are
        /// left of centre; the end's Inverted flag mirrors the profile).
        /// </summary>
        public static Zone[] ZonesAt(Seg s, bool atB)
        {
            var net = Net;
            int node = atB ? s.B : s.A;
            int pi = net.NodeProfile[node];
            if (pi < 0 || pi >= net.Profiles.Length) return null;
            var p = net.Profiles[pi];
            bool inv = s.Has(atB ? RoadNetwork.SegEndInverted : RoadNetwork.SegStartInverted);
            // WRoadProfile::GetRelativeLaneOffset (WRoadElem.h:142): lane n reads stored lane GetLaneNumber(n) (reversed
            // when inverted), and lanes below the middle zone (N − mid when inverted) negate the signed stored offset
            var z = new Zone[p.Zones];
            int mid = inv ? p.Zones - p.Middle : p.Middle;
            for (int l = 0; l < p.Zones; l++)
            {
                var lane = p.Lanes[inv ? p.Zones - l - 1 : l];
                z[l] = new Zone { Type = lane.Type, Width = Mathf.Abs(lane.Width), Offset = l < mid ? -lane.Offset : lane.Offset };
            }
            System.Array.Sort(z, (x, y) => x.Offset.CompareTo(y.Offset));
            return z;
        }

        // nav lane types (WRoadNetwork.cpp:47-53): drivable / selectable zone masks
        public const uint RacingDrivable = 0xFFFFDF7F, RacingSelectable = 0x402, TrafficMask = 0x2, DragSelectable = 0xFFFFDF5B,
            CopDrivable = 0xFFFFDF7B, CopSelectable = 0x472;

        // ---- runtime race flags ----

        /// <summary>Road index → race shortcut index (WRoad nShortcut; -1 = none).</summary>
        static readonly Dictionary<int, int> _shortcutOfRoad = new Dictionary<int, int>();

        public static int ShortcutOf(Seg s) => s.Road >= 0 && _shortcutOfRoad.TryGetValue(s.Road, out int i) ? i : -1;

        /// <summary>
        /// ResolveShortcuts (WRoadNetwork.cpp:224): each race shortcut marker names the road under it; every segment of
        /// that road gets the Shortcut flag and the road the marker's index.
        /// </summary>
        public static int MarkShortcuts(IList<Vector3> markers)
        {
            Build();
            int n = 0;
            for (int i = 0; i < markers.Count; i++)
            {
                var nav = new RoadNav { DecisionFilter = true };
                if (!nav.InitAtPoint(markers[i], Vector3.zero, true, 0f)) continue;
                int road = _segs[nav.Seg].Road;
                if (road < 0) continue;
                _shortcutOfRoad[road] = i;
                foreach (var s in _segs) if (s.Road == road) { s.Flags |= RoadNetwork.SegShortcut; n++; }
            }
            return n;
        }

        public static void ClearRace()
        {
            Build();
            _shortcutOfRoad.Clear();
            foreach (var s in _segs) s.Flags &= ~RoadNetwork.SegShortcut;
            foreach (var s in _segs) s.Flags &= ~(RoadNetwork.SegInRace | RoadNetwork.SegRaceForward | RoadNetwork.SegBarrier | RoadNetwork.SegDriveThroughBarrier);
        }

        /// <summary>
        /// The race route (WRoadNetwork::AddRaceSegments): segments of an A* route through the waypoints in order get
        /// InRace, and RaceRouteForward when the race runs A→B.
        /// </summary>
        public static int MarkRace(IList<Vector3> waypoints)
        {
            Build();
            int marked = 0;
            for (int i = 0; i + 1 < waypoints.Count; i++)
            {
                var dir = FlatDir(waypoints[i + 1] - waypoints[i]);
                var nav = new RoadNav { DecisionFilter = false };
                if (!nav.InitAtPoint(waypoints[i], dir, true, 1f)) continue;
                // leave the waypoint the way that reaches the next one by the shorter road route (the straight line
                // to the next checkpoint says little about the road's direction where it curves)
                List<int> route = null;
                float bestLen = float.MaxValue;
                bool bestFwd = nav.Fwd;
                float t0 = nav.T;
                foreach (bool f in new[] { nav.Fwd, !nav.Fwd })
                {
                    if (_segs[nav.Seg].Has(RoadNetwork.SegOneWay) && !f) continue;
                    nav.Fwd = f;
                    nav.T = f == bestFwd ? t0 : 1f - t0;
                    var r = nav.SearchPath(waypoints[i + 1], false);
                    if (r == null) continue;
                    float len = (1f - nav.T) * _segs[nav.Seg].Length;
                    foreach (int sg in r) len += _segs[sg].Length;
                    if (len < bestLen) { bestLen = len; route = r; bestFwd = f; }
                }
                nav.Fwd = bestFwd;
                Mark(nav.Seg, nav.Fwd);
                if (route == null) continue;
                int at = nav.Fwd ? _segs[nav.Seg].B : _segs[nav.Seg].A;
                foreach (int sg in route)
                {
                    var s = _segs[sg];
                    bool fwd = s.A == at;
                    Mark(sg, fwd);
                    at = fwd ? s.B : s.A;
                    marked++;
                }
            }
            return marked;

            void Mark(int sg, bool fwd)
            {
                var s = _segs[sg];
                s.Flags |= RoadNetwork.SegInRace;
                if (fwd) s.Flags |= RoadNetwork.SegRaceForward; else s.Flags &= ~RoadNetwork.SegRaceForward;
            }
        }

        /// <summary>
        /// ResolveBarriers: segments whose curve crosses an enabled TrackPathBarrier line get CrossesBarrier
        /// (CrossesDriveThroughBarrier for player barriers). Barriers are enabled by their scenery group.
        /// </summary>
        public static int MarkBarriers(ICollection<uint> enabledGroups)
        {
            Build();
            if (_barriers == null)
            {
                GameData.Mount();
                _barriers = RoadNetwork.ReadBarriers(GameFiles.ReadBundle(System.IO.Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN")));
            }
            int n = 0;
            foreach (var b in _barriers)
            {
                if (!enabledGroups.Contains(b.GroupHash)) continue;
                var a = new Vector2(b.X0, b.Y0);
                var c = new Vector2(b.X1, b.Y1);
                var mid = new Vector3((a.x + c.x) * 0.5f, 0, (a.y + c.y) * 0.5f);
                foreach (int si in Near(mid, Vector2.Distance(a, c) * 0.5f + 5f))
                {
                    var s = _segs[si];
                    for (int k = 0; k + 1 < s.Samples.Length; k++)
                        if (Intersect(a, c, new Vector2(s.Samples[k].x, s.Samples[k].z), new Vector2(s.Samples[k + 1].x, s.Samples[k + 1].z)))
                        {
                            s.Flags |= b.PlayerBarrier ? RoadNetwork.SegDriveThroughBarrier : RoadNetwork.SegBarrier;
                            n++;
                            break;
                        }
                }
            }
            return n;
        }

        static bool Intersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        {
            float C(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            float d1 = C(q1, q2, p1), d2 = C(q1, q2, p2), d3 = C(p1, p2, q1), d4 = C(p1, p2, q2);
            return d1 * d2 < 0 && d3 * d4 < 0;
        }
    }

    /// <summary>
    /// WRoadNav: a cursor that runs along the road graph ahead of a car (WRoadNetwork.h:186; specs/ai_racer.md §2). It
    /// keeps a ring of road cross-sections behind it (the cookie trail); the funnel through them gives the occluded aim
    /// point, i.e. the racing line, and the trail's curvature drives the corner speed.
    /// </summary>
    public sealed partial class RoadNav
    {
        public int Seg = -1;
        /// <summary>Heading to node B (true) or node A.</summary>
        public bool Fwd = true;
        /// <summary>Position along the segment from the from-node, 0..1 (WRoadNav fSegTime).</summary>
        public float T;
        public float LaneOffset;
        public Vector3 Position, Forward, Centre;
        public float Left = -2, Right = 2;
        public bool DeadEnd;
        public bool RaceFilter, DecisionFilter = true, TrafficFilter, CookieTrail;
        public uint Drivable = RoadGraph.RacingDrivable, Selectable = RoadGraph.RacingSelectable;
        public float HalfWidth = 1f;
        /// <summary>Race shortcuts this nav may path through (MakeShortcutDecision, rolled per racer); null = none.</summary>
        public HashSet<int> AllowedShortcuts;
        public List<int> Path;
        int _pathIdx;

        public struct Cookie
        {
            public Vector3 Centre, Left, Right, Forward;
            public float LeftOffset, RightOffset, Curvature, Length;
            public int Flags;
            /// <summary>NavCookie::SegmentNumber: the segment the cookie was dropped on.</summary>
            public int Segment;
        }

        const int MaxCookies = 32;
        readonly Cookie[] _ring = new Cookie[MaxCookies];
        int _first, _count;
        public int CookieCount => _count;
        public Cookie CookieAt(int i) => _ring[(_first + i) % MaxCookies];

        public Vector3 OccludedPosition;
        public bool Occluded;
        public int RoadOcclusion;
        public float OutOfBounds;
        int _cookieIndex;
        Vector3 _apex;
        int _apexCookie = -1;

        RoadGraph.Seg S => RoadGraph.Segs[Seg];
        public bool Valid => Seg >= 0;

        bool Allowed(RoadGraph.Seg s, bool fwd)
        {
            if (DecisionFilter && s.Has(RoadNetwork.SegDecision) && RaceFilter && !s.Has(RoadNetwork.SegInRace)) return false;
            if (RaceFilter && (!s.Has(RoadNetwork.SegInRace) || s.Has(RoadNetwork.SegRaceForward) != fwd)) return false;
            if (TrafficFilter && s.Has(RoadNetwork.SegNoTraffic)) return false;
            if (s.Has(RoadNetwork.SegOneWay) && !fwd) return false;
            if (s.Has(RoadNetwork.SegBarrier) || s.Has(RoadNetwork.SegDriveThroughBarrier)) return false;
            return true;
        }

        /// <summary>
        /// InitAtPoint / FindClosestSegmentInd: the best segment within 32 m by distance (outside the road edges when
        /// dirWeight > 0) plus dirWeight·(1−dot)·10 for heading mismatch.
        /// </summary>
        public bool InitAtPoint(Vector3 pos, Vector3 dir, bool forceCentre, float dirWeight)
        {
            dir = RoadGraph.FlatDir(dir);
            float best = float.MaxValue;
            int bs = -1;
            float bt = 0;
            foreach (int i in RoadGraph.Near(pos, 32f))
            {
                var s = RoadGraph.Segs[i];
                if (DecisionFilter && s.Has(RoadNetwork.SegDecision)) continue;
                if (RaceFilter && !s.Has(RoadNetwork.SegInRace)) continue;
                if (TrafficFilter && s.Has(RoadNetwork.SegNoTraffic)) continue;
                float d = RoadGraph.Closest(s, pos, out float t);
                if (d > 32f) continue;
                float score = d;
                if (dirWeight > 0)
                {
                    var z = RoadGraph.ZonesAt(s, t > 0.5f);
                    float half = 2f;
                    if (z != null && z.Length > 0) half = Mathf.Max(Mathf.Abs(z[0].Offset - z[0].Width * 0.5f), Mathf.Abs(z[z.Length - 1].Offset + z[z.Length - 1].Width * 0.5f));
                    var tan = RoadGraph.Tangent(s, t);
                    float dot = Vector3.Dot(dir, tan);
                    if (!s.Has(RoadNetwork.SegOneWay)) dot = Mathf.Abs(dot);
                    score = Mathf.Max(0, d - half) + dirWeight * (1f - dot) * 10f;
                }
                if (score < best) { best = score; bs = i; bt = t; }
            }
            if (bs < 0) { Seg = -1; return false; }
            Seg = bs;
            var sg = S;
            var tg = RoadGraph.Tangent(sg, bt);
            Fwd = RaceFilter && sg.Has(RoadNetwork.SegInRace) ? sg.Has(RoadNetwork.SegRaceForward) : Vector3.Dot(dir, tg) >= 0;
            if (sg.Has(RoadNetwork.SegOneWay)) Fwd = true;
            T = Fwd ? bt : 1f - bt;
            DeadEnd = false;
            Path = null;
            var c = RoadGraph.Point(sg, bt);
            LaneOffset = forceCentre ? 0 : RoadGraph.Cross2(pos - c, Fwd ? tg : -tg);
            UpdateBound();
            if (forceCentre) LaneOffset = Mathf.Clamp(0, Left + 0.1f, Right - 0.1f);
            else SnapToSelectableLane();
            UpdateBound();
            ResetCookieTrail();
            return true;
        }

        /// <summary>SetBoundPos: centre point, forward, and the drivable corridor (edges inset by half width + 1.5).</summary>
        public void UpdateBound()
        {
            var s = S;
            float tc = Fwd ? T : 1f - T;
            Centre = RoadGraph.Point(s, tc);
            Forward = RoadGraph.Tangent(s, tc) * (Fwd ? 1f : -1f);
            var (l0, r0) = Corridor(s, !Fwd);
            var (l1, r1) = Corridor(s, Fwd);
            Left = Mathf.Lerp(l0, l1, T);
            Right = Mathf.Lerp(r0, r1, T);
            LaneOffset = Mathf.Clamp(LaneOffset, Left + 0.1f, Right - 0.1f);
            Position = Centre + RoadGraph.RightOf(Forward) * LaneOffset;
        }

        (float l, float r) Corridor(RoadGraph.Seg s, bool atB)
        {
            var zones = RoadGraph.ZonesAt(s, atB);
            float m = HalfWidth + 1.5f;
            if (zones == null || zones.Length == 0) return (LaneOffset - 2f, LaneOffset + 2f);
            float sign = Fwd ? 1f : -1f;
            // in travel terms (right-positive along the nav's direction)
            int n = zones.Length;
            var off = new float[n];
            var idx = new int[n];
            for (int i = 0; i < n; i++) { off[i] = zones[i].Offset * sign; idx[i] = i; }
            System.Array.Sort(off, idx);
            int best = -1;
            float bd = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (((Drivable >> zones[idx[i]].Type) & 1) == 0) continue;
                float d = Mathf.Abs(off[i] - LaneOffset);
                if (d < bd) { bd = d; best = i; }
            }
            if (best < 0) return (LaneOffset - 2f, LaneOffset + 2f);
            int lo = best, hi = best;
            while (lo > 0 && ((Drivable >> zones[idx[lo - 1]].Type) & 1) != 0) lo--;
            while (hi < n - 1 && ((Drivable >> zones[idx[hi + 1]].Type) & 1) != 0) hi++;
            float left = off[lo] - zones[idx[lo]].Width * 0.5f + m;
            float right = off[hi] + zones[idx[hi]].Width * 0.5f - m;
            if (right - left < 2f) { float mid = (left + right) * 0.5f; left = mid - 1f; right = mid + 1f; }
            return (left, right);
        }

        /// <summary>
        /// SnapToSelectableLane (WRoadNetwork.cpp:901): for racing/cop lanes the offset is kept within half a lane of
        /// the nearest selectable zone (traffic or road lanes, either direction), inside the drivable corridor.
        /// </summary>
        public void SnapToSelectableLane()
        {
            var zones = RoadGraph.ZonesAt(S, Fwd);
            if (zones == null || zones.Length == 0) return;
            float sign = Fwd ? 1f : -1f, best = float.MaxValue, centre = 0, width = 4f;
            foreach (var z in zones)
            {
                if (((Selectable >> z.Type) & 1) == 0) continue;
                float o = z.Offset * sign, d = Mathf.Abs(o - LaneOffset);
                if (d < best) { best = d; centre = o; width = z.Width; }
            }
            if (best == float.MaxValue) return;
            LaneOffset = centre + Mathf.Clamp(LaneOffset - centre, -width * 0.5f, width * 0.5f);
            UpdateBound();
        }

        /// <summary>
        /// ChangeDragLanes (WRoadNetwork.cpp:2946): from the selectable lane nearest the current offset, step left (−1)
        /// or right (+1) across drivable zones to the next selectable one and jump the offset to its centre.
        /// </summary>
        public void ChangeDragLane(int dir)
        {
            var zones = RoadGraph.ZonesAt(S, Fwd);
            if (zones == null || zones.Length == 0) return;
            float sign = Fwd ? 1f : -1f;
            var lanes = zones.Select(z => (off: z.Offset * sign, z.Type)).OrderBy(z => z.off).ToArray();
            int cur = -1;
            float best = float.MaxValue;
            for (int i = 0; i < lanes.Length; i++)
            {
                if (((Selectable >> lanes[i].Type) & 1) == 0) continue;
                float d = Mathf.Abs(lanes[i].off - LaneOffset);
                if (d < best) { best = d; cur = i; }
            }
            if (cur < 0) return;
            for (int n = cur + dir; dir != 0 && n >= 0 && n < lanes.Length; n += dir)
            {
                if (((Drivable >> lanes[n].Type) & 1) == 0) break;
                if (((Selectable >> lanes[n].Type) & 1) != 0) { cur = n; break; }
            }
            LaneOffset = lanes[cur].off;
            UpdateBound();
        }

        /// <summary>ChangeLanes: an immediate lateral offset (the drag lane change snaps over a distance in the game).</summary>
        public void ChangeLanes(float offset) { LaneOffset = offset; UpdateBound(); }

        /// <summary>
        /// IncNavPosition with a cookie trail: advances in steps of 1.1·gap, gap = clamp(maxLookahead/26, 1, 3), and
        /// drops a cookie each time the nav has moved a gap from the newest one.
        /// </summary>
        public void IncNavPosition(float dist, float maxLookahead)
        {
            if (!Valid) return;
            float gap = maxLookahead > 0 ? Mathf.Clamp(maxLookahead / 26f, 1f, 3f) : 3f;
            float step = CookieTrail ? 1.1f * gap : dist;
            while (dist > 1e-3f && !DeadEnd)
            {
                float s = Mathf.Min(step, dist);
                Advance(s);
                dist -= s;
                if (CookieTrail) UpdateCookieTrail(gap);
            }
        }

        void Advance(float d)
        {
            T += d / S.Chord;
            int guard = 0;
            while (T > 1f && guard++ < 16)
            {
                float rem = (T - 1f) * S.Chord;
                if (!NextSegment()) { T = 1f; DeadEnd = true; break; }
                T = rem / S.Chord;
            }
            UpdateBound();
        }

        /// <summary>
        /// GetNextOffset: the next segment at the arrival node. A path is followed first; otherwise the only allowed
        /// continuation, or at a junction the straightest one (racers pass no direction), walking up to 100 m ahead.
        /// </summary>
        bool NextSegment()
        {
            if (GpsPath != null) return NextOnGpsPath();   // kTypePath + kPathGPS (RoadNavGps.cs)
            var cur = S;
            int node = Fwd ? cur.B : cur.A;
            var net = RoadGraph.Net;
            if (Path != null)
            {
                if (_pathIdx < Path.Count)
                {
                    int next = Path[_pathIdx++];
                    var ns = RoadGraph.Segs[next];
                    if (ns.A == node || ns.B == node)
                    {
                        Seg = next;
                        Fwd = ns.A == node;
                        SnapToSelectableLane();
                        return true;
                    }
                }
                Path = null;   // path exhausted: direction mode
            }
            var endDir = RoadGraph.Tangent(cur, Fwd ? 1f : 0f) * (Fwd ? 1f : -1f);
            int bestSeg = -1;
            bool bestFwd = true;
            float bestScore = float.MaxValue;
            int allowed = 0;
            foreach (int si in net.NodeSegments[node])
            {
                if (si == Seg) continue;
                var s = RoadGraph.Segs[si];
                bool fwd = s.A == node;
                if (!Allowed(s, fwd)) continue;
                allowed++;
                // walk ahead along single continuations (up to 100 m) and keep the worst heading gap
                float worst = 1f - Vector3.Dot(RoadGraph.Tangent(s, fwd ? 0f : 1f) * (fwd ? 1f : -1f), endDir);
                var ws = s;
                bool wf = fwd;
                float walked = s.Length;
                for (int k = 0; k < 20 && walked < 100f; k++)
                {
                    int wn = wf ? ws.B : ws.A;
                    RoadGraph.Seg nxt = null;
                    bool nf = true;
                    foreach (int sj in net.NodeSegments[wn])
                    {
                        if (sj == ws.Index) continue;
                        var c = RoadGraph.Segs[sj];
                        bool cf = c.A == wn;
                        if (!Allowed(c, cf)) continue;
                        if (nxt != null) { nxt = null; break; }   // another junction: stop the walk
                        nxt = c;
                        nf = cf;
                    }
                    if (nxt == null) break;
                    ws = nxt;
                    wf = nf;
                    walked += ws.Length;
                    worst = Mathf.Max(worst, 1f - Vector3.Dot(RoadGraph.Tangent(ws, wf ? 0f : 1f) * (wf ? 1f : -1f), endDir));
                }
                if (worst < bestScore) { bestScore = worst; bestSeg = si; bestFwd = fwd; }
            }
            if (bestSeg < 0) return false;
            Seg = bestSeg;
            Fwd = bestFwd;
            SnapToSelectableLane();
            return true;
        }

        // ---- cookie trail ----

        public void ResetCookieTrail()
        {
            _first = 0;
            _count = 0;
            AddCookie(0);
            _cookieIndex = 0;
        }

        void UpdateCookieTrail(float gap)
        {
            if (_count == 0) { AddCookie(0); return; }
            var last = CookieAt(_count - 1);
            float d = Vector3.Distance(Centre, last.Centre);
            if (d < gap) return;
            var ray = RoadGraph.FlatDir(Centre - last.Centre);
            if (Vector3.Dot(ray, last.Forward) < -0.99f) { _first = 0; _count = 0; }
            AddCookie(d);
        }

        void AddCookie(float length)
        {
            var right = RoadGraph.RightOf(Forward);
            var c = new Cookie
            {
                Centre = Centre,
                Left = Centre + right * Left,
                Right = Centre + right * Right,
                LeftOffset = Left,
                RightOffset = Right,
                Forward = Forward,
                Curvature = RoadGraph.Curvature(S, Fwd ? T : 1f - T) * (Fwd ? 1f : -1f),
                Length = length,
                Segment = Seg,
            };
            if (_count == MaxCookies) { _first = (_first + 1) % MaxCookies; _count--; _cookieIndex = Mathf.Max(0, _cookieIndex - 1); }
            _ring[(_first + _count) % MaxCookies] = c;
            _count++;
        }

        public bool IsSegmentInTrail(int seg) => seg == Seg;

        // ---- WRoadNav queries (WRoadNetwork.cpp) ----

        /// <summary>WRoadNav::IsWrongWay (:2532): a race-filtered nav on an InRace segment heading against the race direction.</summary>
        public bool IsWrongWay => RaceFilter && Valid && S.Has(RoadNetwork.SegInRace) && (S.Has(RoadNetwork.SegRaceForward) != Fwd);

        /// <summary>
        /// WRoadNav::IsPointInCookieTrail (:3328): inside the trail's bounding box (+ margin), the closest cookie ahead of the
        /// point is at or past the current cookie, the point is within 5 m of its height and within its left/right offsets
        /// (+ margin) across its forward.
        /// </summary>
        public bool IsPointInCookieTrail(Vector3 p, float margin)
        {
            if (_count == 0) return false;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < _count; i++)
            {
                var c = CookieAt(i);
                minX = Mathf.Min(minX, Mathf.Min(c.Left.x, c.Right.x)); maxX = Mathf.Max(maxX, Mathf.Max(c.Left.x, c.Right.x));
                minZ = Mathf.Min(minZ, Mathf.Min(c.Left.z, c.Right.z)); maxZ = Mathf.Max(maxZ, Mathf.Max(c.Left.z, c.Right.z));
            }
            if (p.x < minX - margin || p.x > maxX + margin || p.z < minZ - margin || p.z > maxZ + margin) return false;
            int ci = ClosestCookieAhead(p, 0);
            if (ci < _cookieIndex) return false;
            var k = CookieAt(ci);
            if (p.y < k.Centre.y - 5f || p.y > k.Centre.y + 5f) return false;
            float off = RoadGraph.Cross2(p - k.Centre, k.Forward);
            return off >= k.LeftOffset - margin && off <= k.RightOffset + margin;
        }

        /// <summary>WRoadNav::IsSegmentInCookieTrail (:3348): a cookie from the current one (or the whole trail) sits on the segment.</summary>
        public bool IsSegmentInCookieTrail(int segment, bool wholePath)
        {
            for (int i = wholePath ? 0 : _cookieIndex; i < _count; i++)
                if (CookieAt(i).Segment == segment) return true;
            return false;
        }

        /// <summary>WRoadNav::IsSegmentInPath (:3361): the segment is on the nav's path (a racer's FindPath or the GPS path).</summary>
        public bool IsSegmentInPath(int segment)
        {
            if (GpsPath != null) return GpsPath.Contains(segment);
            return Path != null && Path.Contains(segment);
        }

        /// <summary>
        /// WRoadNav::GetPathDistanceRemaining (:3173): along the path segments from the nav's segment (its remaining
        /// parameter) to the goal segment's parameter, each segment's length x its road scale when it is a shortcut.
        /// 0 without a path.
        /// </summary>
        public float GetPathDistanceRemaining()
        {
            var net = RoadGraph.Net;
            float distance = 0f;
            if (GpsPath != null)
            {
                bool accumulate = false;
                for (int i = 0; i < GpsPath.Count; i++)
                {
                    int sn = GpsPath[i];
                    float minP = 0f, maxP = 1f;
                    if (sn == Seg) { minP = T; accumulate = true; }
                    bool last = false;
                    if (sn == PathGoalSegment) { var gs = RoadGraph.Segs[sn]; maxP = Fwd ? PathGoalParam : 1f - PathGoalParam; last = true; }
                    if (accumulate)
                    {
                        var sg = RoadGraph.Segs[sn];
                        float len = Mathf.Max(0f, maxP - minP) * sg.Length;
                        if (sg.Has(RoadNetwork.SegShortcut) && sg.Road >= 0 && net.RoadInfos != null && sg.Road < net.RoadInfos.Length) len *= net.RoadInfos[sg.Road].Scale;
                        distance += len;
                    }
                    if (last) break;
                }
                return distance;
            }
            if (Path == null) return 0f;
            distance = (1f - T) * S.Length;
            for (int i = _pathIdx; i < Path.Count; i++)
            {
                var sg = RoadGraph.Segs[Path[i]];
                float len = sg.Length;
                if (sg.Has(RoadNetwork.SegShortcut) && sg.Road >= 0 && net.RoadInfos != null && sg.Road < net.RoadInfos.Length) len *= net.RoadInfos[sg.Road].Scale;
                distance += len;
            }
            return distance;
        }

        /// <summary>WRoadNav::InitFromOtherNav (:2335): the other nav's segment, direction, parameter and lane offset (optionally
        /// reversed); the cookie trail starts again.</summary>
        public void InitFromOtherNav(RoadNav other, bool flip)
        {
            if (other == null || other == this || !other.Valid) return;
            Seg = other.Seg;
            Fwd = flip ? !other.Fwd : other.Fwd;
            T = flip ? 1f - other.T : other.T;
            LaneOffset = other.LaneOffset;
            DeadEnd = false;
            Path = null;
            UpdateBound();
            ResetCookieTrail();
        }

        /// <summary>GetAttachedDirectionalSegment (:3373): at a decision segment's node, the first other segment that is not a
        /// decision segment.</summary>
        RoadGraph.Seg AttachedDirectionalSegment(int node)
        {
            foreach (int si in RoadGraph.Net.NodeSegments[node])
            {
                var sg = RoadGraph.Segs[si];
                if (si != Seg && !sg.Has(RoadNetwork.SegDecision)) return sg;
            }
            return null;
        }

        /// <summary>WRoadNav::IsOnLegalRoad (:3076): the segment (the attached road at a decision segment's to-node) allows traffic.</summary>
        public bool IsOnLegalRoad
        {
            get
            {
                if (!Valid) return false;
                var sg = S;
                if (sg.Has(RoadNetwork.SegDecision)) sg = AttachedDirectionalSegment(Fwd ? sg.B : sg.A);
                return sg != null && !sg.Has(RoadNetwork.SegNoTraffic);
            }
        }

        /// <summary>WRoadNav::GetShortcutNumber (:3050): the road's shortcut number of a shortcut segment (a decision segment looks
        /// at the roads attached at its nodes), 255 otherwise.</summary>
        public int ShortcutNumber
        {
            get
            {
                if (!Valid) return 0xFF;
                var sg = S;
                if (sg.Has(RoadNetwork.SegDecision))
                {
                    var a = AttachedDirectionalSegment(Fwd ? sg.B : sg.A);
                    if (a == null || !a.Has(RoadNetwork.SegShortcut)) a = AttachedDirectionalSegment(Fwd ? sg.A : sg.B);
                    sg = a;
                }
                var net = RoadGraph.Net;
                if (sg != null && sg.Has(RoadNetwork.SegShortcut) && sg.Road >= 0 && net.RoadInfos != null && sg.Road < net.RoadInfos.Length) return net.RoadInfos[sg.Road].Shortcut;
                return 0xFF;
            }
        }

        /// <summary>WRoadNav::GetRoadSpeechId (:3033): the WRoad nSpeechId of the nav's segment's road (the dispatcher's street
        /// name), 0 without one.</summary>
        public int RoadSpeechId
        {
            get
            {
                if (!Valid) return 0;
                var net = RoadGraph.Net;
                var sg = S;
                return sg.Road >= 0 && net.RoadInfos != null && sg.Road < net.RoadInfos.Length ? net.RoadInfos[sg.Road].SpeechId : 0;
            }
        }

        /// <summary>
        /// UpdateOccludedPosition (WRoadNetwork.cpp:1917): string-pull from the car through the cookie corridor to the nav
        /// point; when an edge blocks the line of sight the aim point sits just outside that inside corner (apex).
        /// </summary>
        public Vector3 UpdateOccludedPosition(Vector3 car, float carHalfWidth)
        {
            Occluded = false;
            RoadOcclusion = 0;
            _apexCookie = -1;
            if (_count == 0) { OccludedPosition = Position; return Position; }
            // the last cookie the car is past
            int baseIdx = 0;
            for (int i = 0; i < _count; i++)
            {
                var c = CookieAt(i);
                if (Vector3.Dot(c.Forward, car - c.Centre) >= 0) baseIdx = i;
            }
            _cookieIndex = baseIdx;
            var b = CookieAt(baseIdx);
            float off = RoadGraph.Cross2(car - b.Centre, b.Forward);
            OutOfBounds = carHalfWidth + Mathf.Max(off - b.RightOffset, b.LeftOffset - off);
            // a car outside the corridor (e.g. merging from a wide road into a narrow one) pulls from its nearest point
            // inside it, so the funnel does not fold back onto the car
            float inside = Mathf.Clamp(off, b.LeftOffset, b.RightOffset);
            if (inside != off) car -= RoadGraph.RightOf(b.Forward) * (off - inside);

            Vector3 lDir = Vector3.zero, rDir = Vector3.zero;
            int lIdx = -1, rIdx = -1, occluder = -1, side = 0;
            for (int i = baseIdx + 1; i < _count && occluder < 0; i++)
            {
                var c = CookieAt(i);
                var a = c.Left - car; a.y = 0;
                var r = c.Right - car; r.y = 0;
                if (lIdx < 0) { lDir = a; rDir = r; lIdx = rIdx = i; continue; }
                // left edge: tighten when it swings right of the left limit; past the right limit, the right limit occludes
                if (RoadGraph.Cross2(a, lDir) > 0)
                {
                    if (RoadGraph.Cross2(a, rDir) > 0) { occluder = rIdx; side = 1; break; }
                    lDir = a; lIdx = i;
                }
                if (RoadGraph.Cross2(r, rDir) < 0)
                {
                    if (RoadGraph.Cross2(r, lDir) < 0) { occluder = lIdx; side = -1; break; }
                    rDir = r; rIdx = i;
                }
            }
            if (occluder < 0 && lIdx >= 0)
            {
                var t = Position - car; t.y = 0;
                if (RoadGraph.Cross2(t, lDir) < 0) { occluder = lIdx; side = -1; }
                else if (RoadGraph.Cross2(t, rDir) > 0) { occluder = rIdx; side = 1; }
            }
            if (occluder < 0) { OccludedPosition = Position; return Position; }
            Occluded = true;
            RoadOcclusion = side;
            _apexCookie = occluder;
            var oc = CookieAt(occluder);
            _apex = side < 0 ? oc.Left : oc.Right;
            // aim just outside the apex: along the outward bisector, at most the corridor's spare width
            var a2c = RoadGraph.FlatDir(_apex - car);
            var n2a = RoadGraph.FlatDir(_apex - Position);
            float dist = Vector3.Distance(new Vector3(_apex.x, 0, _apex.z), new Vector3(car.x, 0, car.z));
            var perp = RoadGraph.FlatDir(a2c + n2a);
            float apexWidth = Mathf.Max(0.5f, (oc.RightOffset - oc.LeftOffset) * 0.25f);   // [?] the game's apex width is not traced
            float proj = Mathf.Clamp(Vector3.Dot(n2a * dist, perp), 0, apexWidth);
            OccludedPosition = _apex + perp * proj;   // the bisector of (apex − car) and (apex − nav) points into the road
            OccludedPosition.y = _apex.y;
            return OccludedPosition;
        }

        /// <summary>
        /// CookieTrailCurvature (WRoadNetwork.cpp:3249): the larger of the trail's length-weighted mean curvature
        /// (each clamped to ±0.01) and the apex term from the occluded line of sight.
        /// </summary>
        public float CookieTrailCurvature(Vector3 car, Vector3 vel)
        {
            float sumL = 0, sumK = 0;
            for (int i = Mathf.Max(1, _cookieIndex + 1); i < _count; i++)
            {
                var c = CookieAt(i);
                var p = CookieAt(i - 1);
                float k = 0.5f * (Mathf.Clamp(c.Curvature, -0.01f, 0.01f) + Mathf.Clamp(p.Curvature, -0.01f, 0.01f));
                sumL += c.Length;
                sumK += c.Length * k;
            }
            float road = sumL > 0 ? Mathf.Abs(sumK / sumL) : 0;
            float apexTerm = 0;
            if (_count > 2 && Occluded && _apexCookie >= 0)
            {
                var c2a = OccludedPosition - car; c2a.y = 0;
                float dist = c2a.magnitude;
                if (dist > 1f)
                {
                    c2a /= dist;
                    var ac = CookieAt(_apexCookie);
                    float w = Mathf.Max(1f, Mathf.Max(dist, Mathf.Abs(ac.RightOffset - ac.LeftOffset)));
                    var n2a = RoadGraph.FlatDir(Position - _apex);
                    float s = Mathf.Abs(Mathf.Asin(Mathf.Clamp(RoadGraph.Cross2(n2a, c2a), -1f, 1f)));
                    if (Vector3.Dot(c2a, n2a) < 0) s = Mathf.PI - s;
                    float apex = s * Mathf.Sin(Mathf.Min(s, Mathf.PI * 0.5f));
                    apexTerm = Mathf.Clamp(apex, 0, Mathf.PI) / w;
                }
            }
            return Mathf.Max(apexTerm, road);
        }

        // ---- path finding ----

        /// <summary>
        /// FindPath (AStarSearch, WPathFinder.cpp:127): segment-length A* from the nav's to-node to the segment closest to
        /// the goal; one-way roads and barriers are respected, Decision→Decision steps are not taken. Follows it.
        /// </summary>
        public bool FindPath(Vector3 goal)
        {
            var p = SearchPath(goal, true);
            if (p == null) return false;
            Path = p;
            _pathIdx = 0;
            return true;
        }

        public List<int> SearchPath(Vector3 goal, bool respectRace)
        {
            if (!Valid) return null;
            var goalNav = new RoadNav { DecisionFilter = false };
            if (!goalNav.InitAtPoint(goal, Vector3.zero, true, 0f)) return null;
            int goalSeg = goalNav.Seg;
            if (goalSeg == Seg) return new List<int>();
            var segs = RoadGraph.Segs;
            var net = RoadGraph.Net;
            int start = Fwd ? S.B : S.A;
            var g = new Dictionary<int, float> { [start] = 0 };
            var cameSeg = new Dictionary<int, int> { [start] = Seg };
            var open = new SortedSet<(float f, int id, int node)>();
            int serial = 0;
            open.Add((Vector3.Distance(RoadGraph.Node(start), goal), serial++, start));
            var closed = new HashSet<int>();
            int found = -1, foundSeg = -1;
            int guard = 0;
            while (open.Count > 0 && guard++ < 6000)
            {
                var cur = open.Min;
                open.Remove(cur);
                int n = cur.node;
                if (!closed.Add(n)) continue;
                var arrival = segs[cameSeg[n]];
                foreach (int si in net.NodeSegments[n])
                {
                    if (si == arrival.Index) continue;
                    var s = segs[si];
                    bool fwd = s.A == n;
                    if (s.Has(RoadNetwork.SegOneWay) && !fwd) continue;
                    if (s.Has(RoadNetwork.SegBarrier) || s.Has(RoadNetwork.SegDriveThroughBarrier)) continue;
                    if (arrival.Has(RoadNetwork.SegDecision) && s.Has(RoadNetwork.SegDecision)) continue;
                    // Admissible: a shortcut only when this racer decided to take it (and the one it is on)
                    if (s.Has(RoadNetwork.SegShortcut) && !(AllowedShortcuts != null && AllowedShortcuts.Contains(RoadGraph.ShortcutOf(s))) && RoadGraph.ShortcutOf(s) != RoadGraph.ShortcutOf(S)) continue;
                    if (respectRace && RaceFilter && s.Has(RoadNetwork.SegInRace) && s.Has(RoadNetwork.SegRaceForward) != fwd) continue;
                    if (si == goalSeg) { found = n; foundSeg = si; break; }
                    int m = fwd ? s.B : s.A;
                    if (closed.Contains(m)) continue;
                    float ng = g[n] + s.Length;
                    if (g.TryGetValue(m, out float old) && old <= ng) continue;
                    g[m] = ng;
                    cameSeg[m] = si;
                    open.Add((ng + Vector3.Distance(RoadGraph.Node(m), goal), serial++, m));
                }
                if (found >= 0) break;
            }
            if (found < 0) return null;
            var path = new List<int> { foundSeg };
            for (int n = found; n != start;)
            {
                int si = cameSeg[n];
                path.Add(si);
                var s = segs[si];
                n = s.A == n ? s.B : s.A;
                if (path.Count > 510) return null;
            }
            path.Reverse();
            return path;
        }
    }
}
