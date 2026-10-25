using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>Cop additions to the road graph: lane types, traffic lane counts, the traffic corner limit.</summary>
    public static partial class RoadGraph
    {
        /// <summary>WRoadNav::ELaneType (World/WRoadNetwork.h:205).</summary>
        public enum Lane { Racing, Traffic, Drag, Cop, CopReckless, Reset, StartingGrid, Any }

        // drivable_lanes / selectable_lanes (WRoadNetwork.cpp:47-53), indexed by ELaneType
        static readonly uint[] DrivableLanes = { 0xFFFFDF7F, 0x2, 0xFFFFDF7F, 0xFFFFDF7B, 0xFFFFDF7F, 0x402, 0xFFFFDF7F, 0xFFFFFFFF };
        static readonly uint[] SelectableLanes = { 0x402, 0x2, 0xFFFFDF5B, 0x472, 0xFFFFDF7F, 0x402, 0x402, 0xFFFFFFFF };

        public static uint DrivableMask(Lane l) => DrivableLanes[(int)l];
        public static uint SelectableMask(Lane l) => SelectableLanes[(int)l];

        /// <summary>Traffic lanes (zone type 1) on a segment end, both directions (a "highway" has 4 or more).</summary>
        public static int TrafficLanes(Seg s)
        {
            var z = ZonesAt(s, false);
            if (z == null) return 0;
            int n = 0;
            foreach (var zone in z) if (zone.Type == (int)Lane.Traffic) n++;
            return n;
        }

        /// <summary>
        /// GetSpeedLimitForCurvature (AIActionTraffic): the speed whose lateral acceleration on |κ| equals friction·g,
        /// capped by the posted speed [I: body not decompiled, the usual v = √(μg/κ)].
        /// </summary>
        public static float SpeedLimitForCurvature(float friction, float curvature, float posted)
        {
            float k = Mathf.Abs(curvature);
            if (k < 1e-5f) return posted;
            return Mathf.Min(posted, Mathf.Sqrt(friction * 9.81f / k));
        }
    }

    /// <summary>Cop additions to WRoadNav: lane type, offset snapping, reversing, copies and traffic wandering.</summary>
    public sealed partial class RoadNav
    {
        public RoadGraph.Lane LaneType { get; private set; } = RoadGraph.Lane.Racing;

        /// <summary>SetLaneType: the drivable and selectable zone masks of a lane type.</summary>
        public void SetLaneType(RoadGraph.Lane lane)
        {
            LaneType = lane;
            Drivable = RoadGraph.DrivableMask(lane);
            Selectable = RoadGraph.SelectableMask(lane);
        }

        public Vector3 LeftPosition => Centre + RoadGraph.RightOf(Forward) * Left;
        public Vector3 RightPosition => Centre + RoadGraph.RightOf(Forward) * Right;
        public RoadGraph.Seg Segment => Valid ? S : null;
        /// <summary>The node the nav is heading to.</summary>
        public int ToNode => Fwd ? S.B : S.A;

        /// <summary>WRoadNav::SnapToSelectableLane(float): the offset moved inside the nearest selectable lane, without moving the nav.</summary>
        public float SnapOffset(float offset)
        {
            if (!Valid) return offset;
            var zones = RoadGraph.ZonesAt(S, Fwd);
            if (zones == null || zones.Length == 0) return offset;
            float sign = Fwd ? 1f : -1f, best = float.MaxValue, centre = 0, width = 4f;
            foreach (var z in zones)
            {
                if (((Selectable >> z.Type) & 1) == 0) continue;
                float o = z.Offset * sign, d = Mathf.Abs(o - offset);
                if (d < best) { best = d; centre = o; width = z.Width; }
            }
            if (best == float.MaxValue) return offset;
            return centre + Mathf.Clamp(offset - centre, -width * 0.5f, width * 0.5f);
        }

        /// <summary>WRoadNav::Reverse: the same point heading the other way (offset mirrored); path and trail dropped.</summary>
        public void Reverse()
        {
            if (!Valid) return;
            if (S.Has(RoadNetwork.SegOneWay)) return;
            Fwd = !Fwd;
            T = 1f - T;
            LaneOffset = -LaneOffset;
            Path = null;
            DeadEnd = false;
            UpdateBound();
            ResetCookieTrail();
        }

        /// <summary>InitFromOtherNav: a copy at the same place and settings (no path, fresh trail).</summary>
        public RoadNav Clone(bool cookieTrail = false)
        {
            var n = new RoadNav
            {
                Seg = Seg, Fwd = Fwd, T = T, LaneOffset = LaneOffset, RaceFilter = RaceFilter, DecisionFilter = DecisionFilter,
                TrafficFilter = TrafficFilter, CookieTrail = cookieTrail, Drivable = Drivable, Selectable = Selectable,
                HalfWidth = HalfWidth, LaneType = LaneType,
            };
            if (n.Valid) { n.UpdateBound(); n.ResetCookieTrail(); }
            return n;
        }

        /// <summary>Follows a given segment list from the to-node (traffic routes, wander).</summary>
        public void SetPath(List<int> segments)
        {
            Path = segments;
            _pathIdx = 0;
        }

        public bool PathDone => Path == null || _pathIdx >= Path.Count;

        /// <summary>GetCurrentCookie: the last trail cookie the car is past (set by UpdateOccludedPosition).</summary>
        public Cookie CurrentCookie => _count > 0 ? CookieAt(Mathf.Clamp(_cookieIndex, 0, _count - 1)) : default;

        /// <summary>
        /// GetNextTraffic, simplified: a random allowed continuation at each node (no U-turns), as a path of up to
        /// <paramref name="count"/> segments [I: the lane/curb candidate rules of WRoadNav::GetNextTraffic are not ported].
        /// </summary>
        public List<int> Wander(int count)
        {
            var list = new List<int>();
            if (!Valid) return list;
            var net = RoadGraph.Net;
            int seg = Seg, node = ToNode;
            for (int i = 0; i < count; i++)
            {
                var options = new List<int>();
                foreach (int si in net.NodeSegments[node])
                {
                    if (si == seg) continue;
                    var s = RoadGraph.Segs[si];
                    if (!Allowed(s, s.A == node)) continue;
                    options.Add(si);
                }
                if (options.Count == 0) break;
                seg = options[Random.Range(0, options.Count)];
                list.Add(seg);
                var ns = RoadGraph.Segs[seg];
                node = ns.A == node ? ns.B : ns.A;
            }
            return list;
        }
    }

    /// <summary>
    /// A perpetrator's road navs (AIVehicle::UpdateRoads, AIVehicle.cpp:1750): the current road at the car and the future
    /// road one second (|v| metres) ahead, plus GetSeekAheadPosition (:1886) — the point |v|·7.8 s + 8 m along the road
    /// (×0.4 while the pursuit thinks the perp is a jerk), recomputed at most every 0.33 s.
    /// </summary>
    public sealed class PerpRoads
    {
        public readonly RoadNav Current = new RoadNav { DecisionFilter = false };
        public readonly RoadNav Future = new RoadNav { DecisionFilter = false };
        public Vector3 SeekAhead { get; private set; }
        float _updateTime = -1e9f, _seekTime = -1e9f;

        public bool Valid => Current.Valid && Future.Valid;

        public void Update(Vector3 pos, Vector3 vel, Vector3 fwd, float now)
        {
            float speed = vel.magnitude;
            var dir = speed > 1f ? vel : fwd;
            var off = pos - Current.Position;
            bool valid = Current.Valid && Future.Valid && off.magnitude < 20f;
            if (!valid || now - _updateTime >= 0.33f)
            {
                _updateTime = now;
                Current.SetLaneType(RoadGraph.Lane.Racing);
                if (!Current.InitAtPoint(pos, dir, false, 1f)) { Future.Seg = -1; return; }
                CopyTo(Current, Future);
                if (speed > 0.05f) Future.IncNavPosition(speed, 0f);
                return;
            }
            var road = RoadGraph.FlatDir(Current.Forward);
            float lane = Vector3.Dot(RoadGraph.RightOf(road), off), along = Vector3.Dot(road, off);
            if (along > 0.05f)
            {
                Current.ChangeLanes(Current.SnapOffset(Current.LaneOffset + lane));
                Current.IncNavPosition(along, 0f);
            }
            float ahead = speed - Vector3.Distance(Future.Position, Current.Position);
            if (ahead > 0.05f) Future.IncNavPosition(ahead, 0f);
        }

        static void CopyTo(RoadNav a, RoadNav b)
        {
            b.Seg = a.Seg; b.Fwd = a.Fwd; b.T = a.T; b.LaneOffset = a.LaneOffset; b.Drivable = a.Drivable; b.Selectable = a.Selectable;
            b.Path = null; b.DeadEnd = false;
            b.UpdateBound();
        }

        public Vector3 GetSeekAhead(Vector3 pos, Vector3 vel, bool jerk, float now)
        {
            if (now - _seekTime <= 0.33f) return SeekAhead;
            if (!Current.Valid) { SeekAhead = pos; return SeekAhead; }
            var nav = Current.Clone();
            nav.SetLaneType(RoadGraph.Lane.Cop);
            _seekTime = now;
            float t = 7.8f * (jerk ? 0.4f : 1f);
            nav.IncNavPosition(Mathf.Min(500f, vel.magnitude * t + 8f), 0f);
            SeekAhead = nav.Position;
            return SeekAhead;
        }
    }
}
