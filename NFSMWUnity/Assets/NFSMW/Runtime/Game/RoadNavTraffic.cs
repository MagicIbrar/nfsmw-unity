using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Traffic lanes from the road profiles (WRoadProfile, World/WRoadElem.h:114-202; WRoadNetwork.cpp:389-431, 1009-1056).
    /// A profile's zones at or above the middle zone are the "forward" lanes, the rest the "backward" ones; a segment end
    /// flagged inverted swaps the two. Lane n of a direction counts from the centre (GetNthTrafficLane) or from the kerb
    /// (GetNthTrafficLaneFromCurb). The traffic nav uses a lane's raw offset as is: metres right of the travel direction.
    /// </summary>
    public static partial class RoadGraph
    {
        /// <summary>Nav lane type kLaneTraffic (WRoadNav::ELaneType).</summary>
        public const int LaneTraffic = 1;

        /// <summary>The profile at a segment end and its inverted flag (GetSegmentProfile / IsProfileInverted).</summary>
        public static RoadNetwork.Profile ProfileAt(Seg s, bool atB, out bool inverted)
        {
            var net = Net;
            int node = atB ? s.B : s.A;
            int pi = node < net.NodeProfile.Length ? net.NodeProfile[node] : -1;
            inverted = s.Has(atB ? RoadNetwork.SegEndInverted : RoadNetwork.SegStartInverted);
            return pi >= 0 && pi < net.Profiles.Length ? net.Profiles[pi] : null;
        }

        static int NumLanes(RoadNetwork.Profile p, bool forward) => forward ? p.Zones - p.Middle : p.Middle;

        static int NthLane(RoadNetwork.Profile p, int n, bool forward) =>
            forward ? Mathf.Min(p.Middle + n, p.Zones - 1) : Mathf.Max(p.Middle - 1 - n, 0);

        /// <summary>GetNumTrafficLanes(forward): traffic zones on that side of the middle zone.</summary>
        public static int NumTrafficLanes(RoadNetwork.Profile p, bool forward)
        {
            if (p == null) return 0;
            int n = 0, lanes = NumLanes(p, forward);
            for (int i = 0; i < lanes; i++) if (p.Lanes[NthLane(p, i, forward)].Type == LaneTraffic) n++;
            return n;
        }

        /// <summary>GetNthTrafficLane: the n-th traffic zone counted from the centre (fallback: the last one found).</summary>
        public static int NthTrafficLane(RoadNetwork.Profile p, int n, bool forward)
        {
            int count = 0, lanes = NumLanes(p, forward), fallback = forward ? p.Middle : Mathf.Max(0, p.Middle - 1);
            for (int i = 0; i < lanes; i++)
            {
                int real = NthLane(p, i, forward);
                if (p.Lanes[real].Type != LaneTraffic) continue;
                if (n == count++) return real;
                fallback = real;
            }
            return fallback;
        }

        /// <summary>GetNthTrafficLaneFromCurb: the n-th traffic zone counted from the kerb.</summary>
        public static int NthTrafficLaneFromCurb(RoadNetwork.Profile p, int n, bool forward)
        {
            int count = 0, lanes = NumLanes(p, forward), fallback = forward ? p.Middle : Mathf.Max(0, p.Middle - 1);
            for (int i = lanes - 1; i >= 0; i--)
            {
                int real = NthLane(p, i, forward);
                if (p.Lanes[real].Type != LaneTraffic) continue;
                if (n == count++) return real;
                fallback = real;
            }
            return fallback;
        }

        /// <summary>GetSegmentNumTrafficLanes (WRoadNetwork.cpp:414): traffic zones of either end profile, the larger.</summary>
        public static int SegmentTrafficLanes(Seg s)
        {
            int Count(RoadNetwork.Profile p)
            {
                if (p == null) return 0;
                int n = 0;
                for (int i = 0; i < p.Zones; i++) if (p.Lanes[i].Type == LaneTraffic) n++;
                return n;
            }
            return Mathf.Max(Count(ProfileAt(s, false, out _)), Count(ProfileAt(s, true, out _)));
        }
    }

    /// <summary>
    /// The traffic nav (WRoadNav kTypeTraffic / kLaneTraffic): it keeps its lane index through junctions, picks a random
    /// traffic-allowed branch (GetNextTraffic, WRoadNetwork.cpp:1168-1342), lerps the lane offset from the entry to the
    /// exit profile along each segment, leaves a cookie trail only the car's half width wide (SetBoundPos, :2698-2717),
    /// and punches holes in that trail around other cars (HolePunchAvoidables, :1697-1915).
    /// </summary>
    public sealed partial class RoadNav
    {
        /// <summary>Profile lane index (fLaneInd) at the segment's destination end.</summary>
        public int TrafficLane = -1;
        float _laneFrom, _laneTo;

        /// <summary>Another car as the avoidance sees it (IBody: transform, velocity, half dimensions).</summary>
        public struct Avoidable
        {
            public Vector3 Position, Forward, Right, Velocity, Dimension;
        }

        /// <summary>The aim point is behind a cut for another car (nAvoidableOcclusion ≠ 0), and from behind (cut flag 2).</summary>
        public bool OccludedByAvoidable, OccludedFromBehind;
        /// <summary>The occluding car's speed along the trail (fOccludingTrailSpeed).</summary>
        public float OccludingTrailSpeed;
        public Vector3 ApexPosition => _apex;

        /// <summary>A traffic nav: decision segments and no-traffic roads are not starting points.</summary>
        public static RoadNav NewTraffic(float halfWidth) => new RoadNav
        {
            TrafficFilter = true, DecisionFilter = true, CookieTrail = true, HalfWidth = halfWidth,
            Drivable = RoadGraph.TrafficMask, Selectable = RoadGraph.TrafficMask,
        };

        bool DestInverted => S.Has(Fwd ? RoadNetwork.SegEndInverted : RoadNetwork.SegStartInverted);
        RoadNetwork.Profile DestProfile => RoadGraph.ProfileAt(S, Fwd, out _);

        static float RawOffset(RoadNetwork.Profile p, int lane) => p != null && lane >= 0 && lane < p.Zones ? p.Lanes[lane].Offset : 0;

        /// <summary>
        /// CanTrafficSpawn (WRoadNetwork.cpp:3210-3240) after InitAtPoint(pos, dir): not a decision segment, traffic
        /// allowed, not the wrong way down a one-way road (dir decides, since InitAtPoint turns one-way navs forward),
        /// at least one traffic lane; then a random traffic lane.
        /// </summary>
        public bool CanTrafficSpawn(Vector3 dir)
        {
            if (!Valid) return false;
            var s = S;
            if (s.Has(RoadNetwork.SegDecision) || s.Has(RoadNetwork.SegNoTraffic)) return false;
            if (s.Has(RoadNetwork.SegOneWay) && Vector3.Dot(RoadGraph.FlatDir(dir), RoadGraph.Tangent(s, T)) < 0) return false;
            var p = DestProfile;
            bool dirFwd = Fwd ^ DestInverted;
            int n = RoadGraph.NumTrafficLanes(p, dirFwd);
            if (n == 0) return false;
            TrafficLane = RoadGraph.NthTrafficLane(p, Random.Range(0, n), dirFwd);
            _laneFrom = _laneTo = RawOffset(p, TrafficLane);
            ApplyTrafficLane();
            ResetCookieTrail();
            return true;
        }

        /// <summary>
        /// InitLaneOffset for traffic (WRoadNetwork.cpp:2359-2423): the nearest of the lanes counted from the kerb at both
        /// ends, the offset lerped between them along the segment.
        /// </summary>
        public bool SnapToTrafficLane(Vector3 carPos)
        {
            if (!Valid) return false;
            var s = S;
            var pEnd = RoadGraph.ProfileAt(s, Fwd, out bool invEnd);
            var pStart = RoadGraph.ProfileAt(s, !Fwd, out bool invStart);
            int n = Mathf.Min(RoadGraph.NumTrafficLanes(pEnd, Fwd ^ invEnd), RoadGraph.NumTrafficLanes(pStart, Fwd ^ invStart));
            if (n == 0) return false;
            float current = RoadGraph.Cross2(carPos - Centre, Forward), best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                int le = RoadGraph.NthTrafficLaneFromCurb(pEnd, i, Fwd ^ invEnd), ls = RoadGraph.NthTrafficLaneFromCurb(pStart, i, Fwd ^ invStart);
                float a = RawOffset(pStart, ls), b = RawOffset(pEnd, le), o = Mathf.Lerp(a, b, T);
                if (Mathf.Abs(current - o) < best) { best = Mathf.Abs(current - o); TrafficLane = le; _laneFrom = a; _laneTo = b; }
            }
            ApplyTrafficLane();
            ResetCookieTrail();
            return true;
        }

        /// <summary>Hands the nav to a car (SetVehicle → DetermineVehicleHalfWidth) and restarts its trail.</summary>
        public void SetTrafficVehicle(float halfWidth)
        {
            HalfWidth = halfWidth;
            if (!Valid) return;
            ApplyTrafficLane();
            ResetCookieTrail();
        }

        /// <summary>The lane offset at T, and the trail corridor: the offset ± a quarter of the car's width (SetBoundPos).</summary>
        void ApplyTrafficLane()
        {
            LaneOffset = Mathf.Lerp(_laneFrom, _laneTo, T);
            Left = LaneOffset - HalfWidth * 0.5f;
            Right = LaneOffset + HalfWidth * 0.5f;
            Position = Centre + RoadGraph.RightOf(Forward) * LaneOffset;
        }

        /// <summary>IncNavPosition for the traffic nav: the same stepping and cookie trail, GetNextTraffic at the nodes.</summary>
        public void IncTrafficPosition(float dist, float maxLookahead)
        {
            if (!Valid) return;
            float gap = maxLookahead > 0 ? Mathf.Clamp(maxLookahead / 26f, 1f, 3f) : 3f;
            float step = 1.1f * gap;
            while (dist > 1e-3f && !DeadEnd)
            {
                float d = Mathf.Min(step, dist);
                dist -= d;
                T += d / S.Chord;
                int guard = 0;
                while (T > 1f && guard++ < 16)
                {
                    float rem = (T - 1f) * S.Chord;
                    if (!NextTraffic()) { T = 1f; DeadEnd = true; break; }
                    T = rem / S.Chord;
                }
                UpdateBound();
                ApplyTrafficLane();
                UpdateCookieTrail(gap);
            }
        }

        /// <summary>
        /// GetNextTraffic: at a node with one way on, take it keeping the lane counted from the centre; at a junction pick a
        /// random traffic-allowed departing segment (one-way roads only the right way, barriers closed) with a traffic lane.
        /// The right-most entrance maps the lane counted from the kerb and is a last resort when that is not the kerb lane
        /// (the next candidate is taken instead). Which departure is the right-most is the most clockwise turn here [?]
        /// (GetRightMostTrafficEntrance works on the far node's geometry). False at a dead end.
        /// </summary>
        bool NextTraffic()
        {
            var cur = S;
            int node = Fwd ? cur.B : cur.A;
            var net = RoadGraph.Net;
            var curProfile = DestProfile;
            bool curDir = Fwd ^ DestInverted;
            int numLanes = RoadGraph.NumTrafficLanes(curProfile, curDir), nth = 0;
            for (int i = 0, k = 0; curProfile != null && i < (curDir ? curProfile.Zones - curProfile.Middle : curProfile.Middle); i++)
            {
                int real = curDir ? Mathf.Min(curProfile.Middle + i, curProfile.Zones - 1) : Mathf.Max(curProfile.Middle - 1 - i, 0);
                if (curProfile.Lanes[real].Type != RoadGraph.LaneTraffic) continue;
                if (real == TrafficLane) nth = k;
                k++;
            }
            var segs = net.NodeSegments[node];
            if (segs.Count < 2) return false;
            if (segs.Count == 2)
            {
                // GetAttachedDirectionalSegment: a plain continuation is followed whatever its flags, same lane from the centre
                int on = segs[0] == Seg ? segs[1] : segs[0];
                var s = RoadGraph.Segs[on];
                bool fwd = s.A == node;
                var p = RoadGraph.ProfileAt(s, fwd, out bool inv);
                _laneFrom = LaneOffset;
                Seg = on;
                Fwd = fwd;
                if (RoadGraph.NumTrafficLanes(p, fwd ^ inv) > 0)
                {
                    TrafficLane = RoadGraph.NthTrafficLane(p, nth, fwd ^ inv);
                    _laneTo = RawOffset(p, TrafficLane);
                }
                else _laneTo = _laneFrom;
                return true;
            }
            var arrive = RoadGraph.Tangent(cur, Fwd ? 1f : 0f) * (Fwd ? 1f : -1f);
            _candidates.Clear();
            float mostRight = 0.5f;   // sin of the turn: anything sharper right than ~30° counts [?]
            int rightMost = -1;
            foreach (int si in segs)
            {
                if (si == Seg) continue;
                var s = RoadGraph.Segs[si];
                bool fwd = s.A == node;
                if (s.Has(RoadNetwork.SegNoTraffic)) continue;
                if (s.Has(RoadNetwork.SegOneWay) && !fwd) continue;
                if (s.Has(RoadNetwork.SegBarrier) || s.Has(RoadNetwork.SegDriveThroughBarrier)) continue;
                var p = RoadGraph.ProfileAt(s, fwd, out bool inv);
                int lanes = RoadGraph.NumTrafficLanes(p, fwd ^ inv);
                if (lanes == 0) continue;
                var depart = RoadGraph.Tangent(s, fwd ? 0f : 1f) * (fwd ? 1f : -1f);
                float turn = RoadGraph.Cross2(depart, arrive);   // + = to the right
                if (turn > mostRight) { mostRight = turn; rightMost = _candidates.Count; }
                _candidates.Add(new Candidate { Seg = si, Fwd = fwd, Profile = p, Dir = fwd ^ inv, Lanes = lanes });
            }
            if (_candidates.Count == 0) return false;
            for (int i = 0; i < _candidates.Count; i++)
            {
                var c = _candidates[i];
                if (i == rightMost && _candidates.Count > 1)
                {
                    int fromCurb = Mathf.Max(0, numLanes - nth - 1);
                    c.LastResort = fromCurb > 0;
                    c.Lane = RoadGraph.NthTrafficLaneFromCurb(c.Profile, Mathf.Min(fromCurb, c.Lanes - 1), c.Dir);
                }
                else c.Lane = RoadGraph.NthTrafficLane(c.Profile, nth, c.Dir);
                _candidates[i] = c;
            }
            int sel = 0;
            if (_candidates.Count > 1)
            {
                sel = Random.Range(0, _candidates.Count);
                if (_candidates[sel].LastResort) sel = (sel + 1) % _candidates.Count;
            }
            var pick = _candidates[sel];
            _laneFrom = LaneOffset;   // the old end position is the new start (useOldStartPos)
            Seg = pick.Seg;
            Fwd = pick.Fwd;
            TrafficLane = pick.Lane;
            _laneTo = RawOffset(pick.Profile, pick.Lane);
            return true;
        }

        struct Candidate
        {
            public int Seg, Lanes, Lane;
            public bool Fwd, Dir, LastResort;
            public RoadNetwork.Profile Profile;
        }

        readonly List<Candidate> _candidates = new List<Candidate>(7);
        readonly Cookie[] _saved = new Cookie[MaxCookies];

        /// <summary>
        /// UpdateOccludedPosition(occlude_avoidables = true) for traffic: the cookies ahead of the car are cut around the
        /// other cars (HolePunchAvoidables), the string pull runs through the cut trail, and an apex on a cut cookie is an
        /// avoidable occlusion. The trail itself is left as it was.
        /// </summary>
        public Vector3 UpdateTrafficOccludedPosition(Vector3 car, Vector3 carFwd, Vector3 carRight, Vector3 carVel, Vector3 carDim, IList<Avoidable> avoidables)
        {
            OccludedByAvoidable = OccludedFromBehind = false;
            OccludingTrailSpeed = 0;
            if (_count == 0) return UpdateOccludedPosition(car, HalfWidth);
            System.Array.Copy(_ring, _saved, MaxCookies);
            int baseIdx = 0;
            for (int i = 0; i < _count; i++)
            {
                var c = CookieAt(i);
                if (Vector3.Dot(c.Forward, car - c.Centre) >= 0) baseIdx = i;
            }
            var b = CookieAt(baseIdx);
            float currentOffset = RoadGraph.Cross2(car - b.Centre, b.Forward), deltaOffset = RoadGraph.Cross2(carVel, b.Forward);
            if (avoidables != null && avoidables.Count > 0)
                HolePunch(baseIdx + 1, car, carFwd, carRight, carVel, carDim, avoidables, currentOffset, deltaOffset);
            var aim = UpdateOccludedPosition(car, HalfWidth);
            if (Occluded && _apexCookie >= 0)
            {
                int flags = CookieAt(_apexCookie).Flags;
                if ((flags & 1) != 0)
                {
                    OccludedByAvoidable = true;
                    OccludedFromBehind = (flags & 2) != 0;
                    RoadOcclusion = 0;
                    // the aim beside the apex shrinks while the car is not closing on the occluder (WRoadNetwork.cpp:2131-2145)
                    var apex2 = new Vector2(_apex.x, _apex.z);
                    var car2 = new Vector2(car.x, car.z);
                    var a2c = (apex2 - car2).normalized;
                    var a2n = (apex2 - new Vector2(Position.x, Position.z)).normalized;
                    float dist = Vector2.Distance(apex2, car2);
                    var perp = (a2c + a2n).normalized;
                    var oc = CookieAt(_apexCookie);
                    float width = Mathf.Abs(oc.RightOffset - oc.LeftOffset);
                    float proj = Mathf.Min(Vector2.Dot(a2n * dist, perp), width);
                    float trailing = Vector2.Dot(new Vector2(carVel.x, carVel.z), a2c);
                    float closing = trailing - OccludingTrailSpeed;
                    float ratio = trailing > 1e-3f ? Mathf.Clamp01(closing / (2f * trailing)) : 0f;
                    proj = Mathf.Min(proj * 2f * ratio, width);
                    OccludedPosition = new Vector3(_apex.x + perp.x * proj, _apex.y, _apex.z + perp.y * proj);
                    aim = OccludedPosition;
                }
            }
            if (!OccludedByAvoidable) { OccludedFromBehind = false; OccludingTrailSpeed = 0; }
            System.Array.Copy(_saved, _ring, MaxCookies);
            return aim;
        }

        /// <summary>ClosestCookieAhead over ring cookies [from, _count): the cookie whose plane the point is just behind.</summary>
        int ClosestCookieAhead(Vector3 p, int from)
        {
            int ret = -1;
            float closest = -1, prev = 0;
            for (int n = from; n < _count; n++)
            {
                var c = CookieAt(n);
                var d = p - c.Centre; d.y = 0;
                float dot = Vector3.Dot(c.Forward, d), dd = d.sqrMagnitude;
                if (n == from) { if (dot < 0) { ret = n; closest = dd; } }
                else if (dot * prev < 0 && (ret < 0 || dd < closest)) { ret = n; closest = dd; }
                prev = dot;
            }
            return ret;
        }

        /// <summary>HolePunchAvoidables (WRoadNetwork.cpp:1697-1915) for a traffic nav, on ring cookies [from, _count).</summary>
        void HolePunch(int from, Vector3 myPos, Vector3 myFwd, Vector3 myRight, Vector3 myVel, Vector3 myDim, IList<Avoidable> avoidables, float currentOffset, float deltaOffset)
        {
            int mine = ClosestCookieAhead(myPos, from);
            if (mine < 0) return;
            var my = CookieAt(mine);
            float mySpeed = myVel.magnitude;
            var navF = RoadGraph.FlatDir(Forward);
            int closestAvoidable = int.MaxValue;
            float myExtent = myDim.x + 0.5f + myDim.z;
            foreach (var av in avoidables)
            {
                float elevation = av.Position.y - my.Centre.y;
                if (elevation < -5f || elevation > 5f) continue;
                float hisExtent = av.Dimension.x + 0.5f + Mathf.Abs(Vector3.Dot(my.Forward, av.Forward)) * av.Dimension.z + Mathf.Abs(Vector3.Dot(my.Forward, av.Right)) * av.Dimension.x;
                var toHim = av.Position - myPos;
                float distAhead = toHim.x * my.Forward.x + toHim.z * my.Forward.z;
                float extentSide = Mathf.Abs(Vector3.Dot(myRight, av.Forward)) * av.Dimension.z + myDim.x + Mathf.Abs(Vector3.Dot(myRight, av.Right)) * av.Dimension.x;
                float distSide = Mathf.Abs(Vector3.Dot(toHim, myRight));
                int cut = distAhead + hisExtent < myExtent ? 2 : 0;   // traffic: alongside or behind
                if (distAhead < -(myExtent + hisExtent)) continue;
                if (distAhead + hisExtent < 0 && distSide < extentSide) continue;
                float gapAhead = Mathf.Max(distAhead - (myExtent + hisExtent), 0);
                var myNose = myPos + my.Forward * Mathf.Min(myExtent, gapAhead);
                var hisNose = av.Position - my.Forward * Mathf.Min(hisExtent, gapAhead);
                float approach = TimeToClosestApproach(myNose, myVel, hisNose, av.Velocity, out float closingSpeed);
                bool blocked = false;
                if (cut == 0)
                {
                    if (closingSpeed <= 0)
                    {
                        float safe = myExtent + hisExtent + mySpeed * 0.5f + 2f * myExtent;
                        if (distAhead > safe) continue;
                        if (distSide > extentSide + 1f) continue;
                    }
                    blocked = true;
                }
                if (!blocked && approach >= 3f) continue;
                float trailing = av.Velocity.x * my.Forward.x + av.Velocity.z * my.Forward.z;
                float timeOffset = trailing * approach - (myExtent + hisExtent);
                var impact = av.Position + new Vector3(my.Forward.x, 0, my.Forward.z) * timeOffset;
                int ci = ClosestCookieAhead(impact, from);
                if (ci < 0) continue;
                var ck = CookieAt(ci);
                var r = av.Right * av.Dimension.x;
                var f = av.Forward * av.Dimension.z;
                var leftDiag = f - r;
                var rightDiag = r + f;
                float avDelta = RoadGraph.Cross2(av.Velocity, ck.Forward);
                if (ci < closestAvoidable && distAhead > myExtent + hisExtent)
                {
                    closestAvoidable = ci;
                    OccludingTrailSpeed = trailing;
                }
                float a = Mathf.Clamp01(approach);
                float offsetChange = avDelta * a;
                var cutTo = impact + RoadGraph.RightOf(ck.Forward) * (offsetChange * 0.8f);
                float extraWidth = offsetChange * 0.2f;
                var c2a = cutTo - ck.Centre; c2a.y = 0;
                var c2m = myPos - ck.Centre; c2m.y = 0;
                float avAhead = Vector3.Dot(c2a, ck.Forward) - Vector3.Dot(c2m, ck.Forward);
                float close = Mathf.Clamp01((avAhead + 6f) / 12f);
                float avOffset = RoadGraph.Cross2(c2a, ck.Forward);   // [?] the decomp crosses (forward, offset): taken right-positive
                float half = Mathf.Max(Mathf.Abs(RoadGraph.Cross2(rightDiag, ck.Forward)), Mathf.Abs(RoadGraph.Cross2(leftDiag, ck.Forward))) + extraWidth * close;
                float newOffset = a * close * (deltaOffset * 0.2f) + currentOffset + RoadGraph.Cross2(navF, ck.Forward) * 2f;
                float margin = close;
                float gapRight = ck.RightOffset - avOffset - half, gapLeft = avOffset - half - ck.LeftOffset, need = margin + HalfWidth;
                bool fitRight = gapRight > need, fitLeft = gapLeft > need;
                bool passLeft = newOffset < avOffset;
                if (fitLeft ^ fitRight) passLeft = fitLeft;
                float projection = half + HalfWidth + margin;
                for (int i = ci; i < _count; i++)
                {
                    if (!CookieCutter(i, cutTo, projection, passLeft, cut) && i == ci) break;
                    var c = CookieAt(i);
                    var delta = impact - c.Centre;
                    if (2f * (myExtent + hisExtent) + delta.x * c.Forward.x + delta.z * c.Forward.z < 0) break;
                }
            }
            // ClampCookieCentres: a cut cookie is re-centred between its edges
            for (int i = from; i < _count; i++)
            {
                int k = (_first + i) % MaxCookies;
                var c = _ring[k];
                if ((c.Flags & 1) == 0) continue;
                float size = (c.RightOffset - c.LeftOffset) * 0.5f;
                c.Centre = new Vector3((c.Left.x + c.Right.x) * 0.5f, c.Centre.y, (c.Left.z + c.Right.z) * 0.5f);
                c.RightOffset = size;
                c.LeftOffset = -size;
                _ring[k] = c;
            }
        }

        /// <summary>CookieCutter (WRoadNetwork.cpp:1570-1598): narrows the cookie to pass left or right of a point.</summary>
        bool CookieCutter(int index, Vector3 centre, float projection, bool passLeft, int cutFlags)
        {
            int k = (_first + index) % MaxCookies;
            var c = _ring[k];
            var d = centre - c.Centre; d.y = 0;
            float l = RoadGraph.Cross2(d, c.Forward);
            if (l < c.LeftOffset - projection || l > c.RightOffset + projection) return false;
            c.Flags |= 1 | cutFlags;
            var right = RoadGraph.RightOf(c.Forward);
            const float minimumWidth = 0.1f;   // traffic
            if (passLeft)
            {
                c.RightOffset = Mathf.Max(c.LeftOffset + minimumWidth, l - projection);
                c.Right = c.Centre + right * c.RightOffset;
            }
            else
            {
                c.LeftOffset = Mathf.Min(c.RightOffset - minimumWidth, l + projection);
                c.Left = c.Centre + right * c.LeftOffset;
            }
            _ring[k] = c;
            return true;
        }

        static float TimeToClosestApproach(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, out float closingSpeed)
        {
            var p = p1 - p0;
            var v = v1 - v0;
            closingSpeed = -Vector3.Dot(p.normalized, v);
            float b = Vector3.Dot(v, v);
            return b < 0.0001f ? 3f : -Vector3.Dot(p, v) / b;
        }
    }
}
