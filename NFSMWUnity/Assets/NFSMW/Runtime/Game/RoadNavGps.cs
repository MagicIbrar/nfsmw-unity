using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The GPS nav (WRoadNav kTypePath + kPathGPS + kLaneRacing; specs/ai_racer.md §12): a path found once by the
    /// path finder (FindPathNow → AStarSearch, WPathFinder.cpp:127-470) and kept in the nav's path buffer
    /// (pPathSegments, ≤ 510) while the nav itself is re-placed every update (InitAtPoint does not clear the path).
    /// The nav follows it by looking its segment up (GetNextOffset kTypePath, WRoadNetwork.cpp:711-745); off the path or
    /// at its end a GPS nav holds where it is (the path type GPS returns the same segment: fDeadEnd).
    /// </summary>
    public sealed partial class RoadNav
    {
        /// <summary>pPathSegments of a GPS path (null = none): the nav's segment first, the goal's last.</summary>
        public List<int> GpsPath { get; private set; }
        /// <summary>SetPathGoal: the goal segment and its parameter (A→B).</summary>
        public int PathGoalSegment { get; private set; } = -1;
        public float PathGoalParam { get; private set; }

        /// <summary>CancelPathFinding: the path is dropped (a path nav falls back to direction mode).</summary>
        public void CancelGpsPath() { GpsPath = null; PathGoalSegment = -1; }

        /// <summary>
        /// FindPathNow(goal, no direction) for kPathGPS (WRoadNetwork.cpp:3160, WPathFinder.cpp:127-470): the goal segment
        /// from a direction nav at the goal (the nav's own filters, fake direction (1,0,0), weight 0); the goal on the
        /// nav's own segment is FULL_WAY with no path, so it fails. A* over (node, arrival segment) states from both ends
        /// of the current segment (not a race route), cost = segment lengths, estimate = straight distance to the goal;
        /// Admissible(kPathGPS): one-way roads only forwards, no CrossesBarrier / CrossesDriveThroughBarrier; no
        /// Decision → Decision step. A solution starting from the far end turns the nav round (Reverse). True with a path.
        /// </summary>
        public bool FindPathGps(Vector3 goal)
        {
            GpsPath = null;
            PathGoalSegment = -1;
            if (!Valid) return false;
            var goalNav = new RoadNav { DecisionFilter = DecisionFilter, TrafficFilter = TrafficFilter, RaceFilter = false };
            if (!goalNav.InitAtPoint(goal, Vector3.right, false, 0f)) return false;
            int goalSeg = goalNav.Seg;
            PathGoalSegment = goalSeg;
            PathGoalParam = goalNav.Fwd ? goalNav.T : 1f - goalNav.T;
            if (IsSegmentInTrail(goalSeg)) return false;   // FULL_WAY without a solution: nPathSegments 0
            var segs = RoadGraph.Segs;
            var net = RoadGraph.Net;
            int to = Fwd ? S.B : S.A, from = Fwd ? S.A : S.B;
            var best = new Dictionary<(int node, int seg), float>();
            var parent = new Dictionary<(int node, int seg), (int node, int seg)>();
            var open = new SortedSet<(float f, int serial, int node, int seg)>();
            int serial = 0;
            void Push(int node, int seg, float g, (int, int)? par)
            {
                best[(node, seg)] = g;
                if (par.HasValue) parent[(node, seg)] = par.Value;
                open.Add((g + Vector3.Distance(RoadGraph.Node(node), goal), serial++, node, seg));
            }
            Push(to, Seg, 0f, null);
            if (from != to) Push(from, Seg, 0f, null);
            (int node, int seg) found = (-1, -1);
            int guard = 0;
            while (open.Count > 0 && guard++ < 20000)
            {
                var cur = open.Min;
                open.Remove(cur);
                var key = (cur.node, cur.seg);
                float g = best[key];
                if (cur.f - Vector3.Distance(RoadGraph.Node(cur.node), goal) > g + 1e-3f) continue;   // a stale entry
                if (cur.seg == goalSeg) { found = key; break; }   // IsGoal (no goal direction: any end)
                var arrival = segs[cur.seg];
                foreach (int si in net.NodeSegments[cur.node])
                {
                    if (si == cur.seg) continue;
                    var s = segs[si];
                    if (arrival.Has(RoadNetwork.SegDecision) && s.Has(RoadNetwork.SegDecision)) continue;
                    bool forward = s.A == cur.node;
                    if (s.Has(RoadNetwork.SegOneWay) && !forward) continue;
                    if (s.Has(RoadNetwork.SegBarrier) || s.Has(RoadNetwork.SegDriveThroughBarrier)) continue;
                    float ng = g + s.Length;
                    int next = forward ? s.B : s.A;
                    if (best.TryGetValue((next, si), out float old) && old <= ng) continue;
                    Push(next, si, ng, key);
                }
            }
            if (found.seg < 0) return false;   // NO_WAY (and a GPS HALF_WAY keeps no path either, :447)
            var path = new List<int>();
            var k = found;
            var root = found;
            while (true)
            {
                path.Add(k.seg);
                root = k;
                if (!parent.TryGetValue(k, out var p)) break;
                k = p;
                if (path.Count > 510) return false;
            }
            path.Reverse();
            // the root state is the current segment arrived at one of its ends: from the far end = turn round (Reverse)
            if (root.node != to) { Fwd = !Fwd; T = 1f - T; UpdateBound(); }
            GpsPath = path;
            return path.Count > 0;
        }

        /// <summary>
        /// OnPath (WRoadNetwork.cpp:671): a valid nav with a path whose segment is on it and whose next path segment
        /// leaves the node the nav heads for (false on the path's last segment).
        /// </summary>
        public bool OnPath()
        {
            if (!Valid || GpsPath == null || GpsPath.Count == 0) return false;
            int i = GpsPath.IndexOf(Seg);
            if (i < 0 || i + 1 >= GpsPath.Count) return false;
            var ns = RoadGraph.Segs[GpsPath[i + 1]];
            int node = Fwd ? S.B : S.A;
            return ns.A == node || ns.B == node;
        }

        /// <summary>GetNextOffset for a GPS path: the next path segment (or a U-turn when it does not leave this node);
        /// off the path / at its end: false (the nav holds).</summary>
        bool NextOnGpsPath()
        {
            int i = GpsPath.IndexOf(Seg);
            if (i < 0 || i + 1 >= GpsPath.Count) return false;
            int node = Fwd ? S.B : S.A;
            var ns = RoadGraph.Segs[GpsPath[i + 1]];
            if (ns.A == node || ns.B == node)
            {
                Seg = ns.Index;
                Fwd = ns.A == node;
                SnapToSelectableLane();
                return true;
            }
            Fwd = !Fwd;   // nodeInd ^= 1: back along this segment from the node just reached
            return true;
        }

        /// <summary>
        /// InitAtPath (WRoadNetwork.cpp:2605) via FindClosestOnPath (:2545): the closest point on any path segment; the
        /// direction is the segment's, reversed when its A end joins the next path segment or its B end the previous one.
        /// </summary>
        public bool InitAtPath(Vector3 pos, bool forceCentre)
        {
            if (GpsPath == null || GpsPath.Count == 0) { Seg = -1; return false; }
            int bestI = -1;
            float bestD = float.MaxValue, bestT = 0;
            for (int i = 0; i < GpsPath.Count; i++)
            {
                float d = RoadGraph.Closest(RoadGraph.Segs[GpsPath[i]], pos, out float t);
                if (d < bestD) { bestD = d; bestI = i; bestT = t; }
            }
            if (bestI < 0) { Seg = -1; return false; }
            var s = RoadGraph.Segs[GpsPath[bestI]];
            int next = bestI < GpsPath.Count - 1 ? GpsPath[bestI + 1] : -1, prev = bestI >= 1 ? GpsPath[bestI - 1] : -1;
            bool towardsA = (next >= 0 && Touches(s.A, next)) || (prev >= 0 && Touches(s.B, prev));
            Seg = s.Index;
            Fwd = !towardsA;
            if (s.Has(RoadNetwork.SegOneWay)) Fwd = true;   // [?] InitAtSegment keeps one-way roads forward as InitAtPoint does
            T = Fwd ? bestT : 1f - bestT;
            DeadEnd = false;
            Path = null;
            var tg = RoadGraph.Tangent(s, bestT);
            LaneOffset = forceCentre ? 0 : RoadGraph.Cross2(pos - RoadGraph.Point(s, bestT), Fwd ? tg : -tg);
            UpdateBound();
            if (!forceCentre) SnapToSelectableLane();
            UpdateBound();
            ResetCookieTrail();
            return true;
        }

        static bool Touches(int node, int seg)
        {
            var s = RoadGraph.Segs[seg];
            return s.A == node || s.B == node;
        }

        /// <summary>The path ahead of the nav as points (for the minimap) [ours].</summary>
        public List<Vector3> GpsPolyline(float maxLength)
        {
            var pts = new List<Vector3>();
            if (!Valid || GpsPath == null) return pts;
            int i = GpsPath.IndexOf(Seg);
            if (i < 0) return pts;
            pts.Add(Position);
            float len = 0;
            bool fwd = Fwd;
            float t0 = T;
            for (; i < GpsPath.Count && len < maxLength; i++)
            {
                var s = RoadGraph.Segs[GpsPath[i]];
                var smp = s.Samples;
                int n = smp.Length;
                for (int k = 0; k < n; k++)
                {
                    float tk = k / (float)(n - 1);
                    if (tk < t0) continue;
                    var p = smp[fwd ? k : n - 1 - k];
                    len += Vector3.Distance(pts[pts.Count - 1], p);
                    pts.Add(p);
                }
                t0 = 0;
                if (i + 1 < GpsPath.Count)
                {
                    int node = fwd ? s.B : s.A;
                    var ns = RoadGraph.Segs[GpsPath[i + 1]];
                    fwd = ns.A == node;
                }
            }
            return pts;
        }
    }
}
