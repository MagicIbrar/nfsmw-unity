using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// The world collision queries of speed.exe over the loaded WCollisionPacks, in sim ("bond") axes
    /// (specs/traffic_collision.md B.7). Unity-free so the harness can run them (`physcol query`).
    /// <list type="bullet">
    /// <item>WCollisionMgr::GetInstanceList (point/cylinder 0x783D70, segment 0x783B80 guts): the WGrid broadphase is
    /// replaced by a uniform XZ cell grid [ours]; the per-instance tests are the exe's (XZ distance against the instance
    /// radius; the segment test also needs the instance's ±fHeight slab).</item>
    /// <item>FindFaceInTriStrip (0x763BB0 point, 0x76C810 matrix), FindFaceInCInst (0x76C9B0, 0x76CC10), GetWorldHeightAtPoint
    /// (0x7897C0), GetWorldHeightAtPointRigorous (0x789870), CheckHitWorld (0x7854B0), GetBarrierNormal and
    /// GetClosestIntersectingBarrier (0x773170), GetBarrierList — WCollisionMgr.cpp:47-1119.</item>
    /// <item>WWorldPos (WWorldPos.cpp; FindClosestFace 0x789190, Update 0x789CC0, HeightAtPoint 0x768200) and WWorldMath
    /// (SegmentIntersect 0x767280, GetPlaneY 0x767790, MakeSegSpaceMatrix 0x767A80, InTri/InTriR/InTriL).</item>
    /// <item>Exclusion (WCollisionMgr.h:67-77, WCollisionAssets::SetExclusionFlags 0x76C6A0): an instance with a scenery
    /// group number gets 0xC0 on every barrier surface, and on the instance itself when it has no strips. A query or
    /// body passes a primitive only when (mask &amp; flags) == 0. Vehicles' masks come from their driver class
    /// (PVehicle::LoadBehaviors 0x685240: human/racer/remote 0, cop 0x80, any other 0x40), so grouped barriers stop
    /// only the player and racers.</item>
    /// </list>
    /// </summary>
    public sealed class WCollisionQueries
    {
        public const uint ExclusionGrouped = 0xC0;
        const float Cell = 64f;

        /// <summary>A loaded instance with the exe's derived values (CalcPosition, radius, runtime flags).</summary>
        public sealed class Entry
        {
            public CollisionInstance Inst;
            public CollisionPack Pack;
            public Vec3 Pos;            // CalcPosition (WCollider.cpp:310-324)
            public float Radius;        // fInvPosRadius.w, or CalcSphericalRadius when the instance needs the cross product
            public uint Flags;          // fFlags | 0xC0 (grouped and strip-less)
            public uint BarrierExtra;   // 0xC0 when grouped (OR-ed onto every barrier surface)
            public int Stamp;
            internal readonly List<long> Cells = new List<long>();
            public bool NeedsCrossProduct => (Inst.Flags & 3) != 0;
            public bool IsYVecNotUp => (Inst.Flags & 1) != 0;
        }

        /// <summary>A world-space triangle (WCollisionTri) with its surface index, flags and simsurface key.</summary>
        public struct Tri
        {
            public Vec3 P0, P1, P2;
            public byte Surface, Flags;
            public uint SurfaceKey;
            public float MinY => Math.Min(Math.Min(P0.Y, P1.Y), P2.Y);
        }

        /// <summary>WorldCollisionInfo: type 1 = ground face, 2 = barrier.</summary>
        public struct Hit
        {
            public int Type;
            public Vec3 Point, Normal;
            public uint Surface;
            public byte SurfaceFlags;
            public bool TwoSided => (SurfaceFlags & 0x10) != 0;
            public float Distance;
            public Entry Instance;
            /// <summary>The barrier in world space (Type 2).</summary>
            public Vec3 B0, B1;
            public bool HitSomething => Type != 0;
        }

        /// <summary>A barrier near a point, world space (WCollisionBarrierListEntry), sorted by XZ distance².</summary>
        public struct BarrierEntry
        {
            public Vec3 P0, P1, Normal;
            public uint Surface;
            public byte SurfaceFlags;
            public float DistSq;
        }

        readonly Dictionary<long, List<Entry>> _grid = new Dictionary<long, List<Entry>>();
        readonly Dictionary<CollisionPack, List<Entry>> _packs = new Dictionary<CollisionPack, List<Entry>>();
        int _iter;

        /// <summary>IsSceneryGroupEnabled for instances with a group number (null = every group on).</summary>
        public Func<CollisionInstance, bool> GroupEnabled;

        public int InstanceCount { get; private set; }

        public void Add(CollisionPack pack)
        {
            if (pack == null || _packs.ContainsKey(pack)) return;
            var list = new List<Entry>();
            foreach (var inst in pack.Instances)
            {
                if (inst.Article == null) continue;
                var e = new Entry { Inst = inst, Pack = pack, Pos = inst.ToWorldSim(new Vec3(0, 0, 0)) };
                e.Radius = e.NeedsCrossProduct ? Math.Max(Math.Max(Math.Max(inst.Radius, inst.HalfLength), inst.HalfHeight), inst.HalfWidth) : inst.Radius;
                e.Flags = inst.Flags;
                if (inst.Group != 0)
                {
                    e.BarrierExtra = ExclusionGrouped;
                    if (inst.Article.Strips.Count == 0) e.Flags |= ExclusionGrouped;
                }
                int x0 = CellOf(e.Pos.X - e.Radius), x1 = CellOf(e.Pos.X + e.Radius);
                int z0 = CellOf(e.Pos.Z - e.Radius), z1 = CellOf(e.Pos.Z + e.Radius);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        long k = Key(cx, cz);
                        if (!_grid.TryGetValue(k, out var cell)) _grid[k] = cell = new List<Entry>();
                        cell.Add(e);
                        e.Cells.Add(k);
                    }
                list.Add(e);
            }
            _packs[pack] = list;
            InstanceCount += list.Count;
        }

        public void Remove(CollisionPack pack)
        {
            if (pack == null || !_packs.TryGetValue(pack, out var list)) return;
            foreach (var e in list)
                foreach (var k in e.Cells)
                    if (_grid.TryGetValue(k, out var cell))
                    {
                        cell.Remove(e);
                        if (cell.Count == 0) _grid.Remove(k);
                    }
            _packs.Remove(pack);
            InstanceCount -= list.Count;
        }

        public bool Contains(CollisionPack pack) => pack != null && _packs.ContainsKey(pack);

        static int CellOf(float v) => (int)Math.Floor(v / Cell);
        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        bool Usable(Entry e, uint mask) =>
            (e.Inst.Group == 0 || GroupEnabled == null || GroupEnabled(e.Inst)) && (mask & e.Flags) == 0;

        // ---- instance lists ------------------------------------------------------------------------------------

        /// <summary>GetInstanceListGuts(point, radius, cylinder) (WCollisionMgr.cpp:544-588): XZ distance &lt; radius + instance radius.</summary>
        public void GetInstanceList(List<Entry> list, Vec3 pt, float radius, uint mask = 0)
        {
            list.Clear();
            ++_iter;
            int x0 = CellOf(pt.X - radius), x1 = CellOf(pt.X + radius), z0 = CellOf(pt.Z - radius), z1 = CellOf(pt.Z + radius);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!_grid.TryGetValue(Key(cx, cz), out var cell)) continue;
                    foreach (var e in cell)
                    {
                        if (e.Stamp == _iter || !Usable(e, mask)) continue;
                        e.Stamp = _iter;
                        float dx = e.Pos.X - pt.X, dz = e.Pos.Z - pt.Z, rs = radius + e.Radius;
                        if (dx * dx + dz * dz < rs * rs) list.Add(e);
                    }
                }
        }

        /// <summary>GetInstanceListGuts(segment) (WCollisionMgr.cpp:605-673): nearest XZ point of the segment within the
        /// instance radius, and the segment's Y range overlapping the instance's ±fHeight (unless it needs the cross product).</summary>
        public void GetInstanceList(List<Entry> list, Vec3 a, Vec3 b, uint mask = 0)
        {
            list.Clear();
            ++_iter;
            float dx = b.X - a.X, dz = b.Z - a.Z, ud = dx * dx + dz * dz, inv = ud > 0f ? 1f / ud : 0f;
            float minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            int x0 = CellOf(Math.Min(a.X, b.X)), x1 = CellOf(Math.Max(a.X, b.X)), z0 = CellOf(Math.Min(a.Z, b.Z)), z1 = CellOf(Math.Max(a.Z, b.Z));
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!_grid.TryGetValue(Key(cx, cz), out var cell)) continue;
                    foreach (var e in cell)
                    {
                        if (e.Stamp == _iter || !Usable(e, mask)) continue;
                        e.Stamp = _iter;
                        float u = ((e.Pos.X - a.X) * dx + (e.Pos.Z - a.Z) * dz) * inv;
                        u = Math.Max(Math.Min(u, 1f), 0f);
                        float nx = e.Pos.X - (u * dx + a.X), nz = e.Pos.Z - (u * dz + a.Z);
                        float r = e.Inst.Radius;
                        if (nx * nx + nz * nz >= r * r) continue;
                        if (e.NeedsCrossProduct) list.Add(e);
                        else if (minY < e.Pos.Y + e.Inst.HalfHeight && maxY > e.Pos.Y - e.Inst.HalfHeight) list.Add(e);
                    }
                }
        }

        // ---- maths (WWorldMath) ------------------------------------------------------------------------------------

        static float PtDir(Vec3 p1, Vec3 p2, Vec3 tp) => (p1.X - p2.X) * (tp.Z - p2.Z) - (tp.X - p2.X) * (p1.Z - p2.Z);

        public static bool InTri(Vec3 pt, Vec3 a, Vec3 b, Vec3 c)
        {
            if (PtDir(a, b, pt) <= 0f) return PtDir(b, c, pt) <= 0f && PtDir(c, a, pt) <= 0f;
            return PtDir(b, c, pt) >= 0f && PtDir(c, a, pt) >= 0f;
        }

        static bool InTriR(Vec3 pt, Vec3 a, Vec3 b, Vec3 c) => PtDir(a, b, pt) <= 0f && PtDir(b, c, pt) <= 0f && PtDir(c, a, pt) <= 0f;
        static bool InTriL(Vec3 pt, Vec3 a, Vec3 b, Vec3 c) => 0f <= PtDir(a, b, pt) && 0f <= PtDir(b, c, pt) && 0f <= PtDir(c, a, pt);

        /// <summary>WCollisionTri::GetNormal: unit cross(p1 − p0, p0 − p2), (0,1,0) when degenerate.</summary>
        public static Vec3 FaceNormal(Vec3 p0, Vec3 p1, Vec3 p2)
        {
            float zx = p1.X - p0.X, zy = p1.Y - p0.Y, zz = p1.Z - p0.Z;
            float xx = p0.X - p2.X, xy = p0.Y - p2.Y, xz = p0.Z - p2.Z;
            float nx = zy * xz - zz * xy, ny = zz * xx - zx * xz, nz = zx * xy - zy * xx;
            if (nx == 0f && ny == 0f && nz == 0f) return new Vec3(0, 1, 0);
            float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return new Vec3(nx / l, ny / l, nz / l);
        }

        /// <summary>The up-facing normal the queries use: flipped to y ≥ 0, y capped at 0.9999 (not renormalised).</summary>
        public static Vec3 UpNormal(Vec3 n)
        {
            if (n.Y < 0f) n = new Vec3(-n.X, -n.Y, -n.Z);
            if (n.Y >= 0.9999f) n.Y = 0.9999f;
            return n;
        }

        /// <summary>WWorldMath::GetPlaneY.</summary>
        public static float GetPlaneY(Vec3 n, Vec3 p, Vec3 test) =>
            n.Y == 0f ? p.Y : p.Y - (n.X * (test.X - p.X) + n.Z * (test.Z - p.Z)) / n.Y;

        /// <summary>WWorldMath::SegmentIntersect: XZ intersection with the exe's ±0.0001 tolerance; y along line 1.</summary>
        public static bool SegmentIntersect(Vec3 a0, Vec3 a1, Vec3 b0, Vec3 b1, out Vec3 p)
        {
            p = default;
            float x11 = a1.X - a0.X, z11 = a1.Z - a0.Z, x22 = b1.X - b0.X, z22 = b1.Z - b0.Z;
            float d = z22 * x11 - x22 * z11;
            if (d == 0f) return false;
            float z12 = a0.Z - b0.Z, x12 = a0.X - b0.X;
            float ua = x22 * z12 - z22 * x12;
            if (!((ua >= -0.0001f && ua <= d) || (ua <= 0.0001f && ua >= d))) return false;
            float ub = x11 * z12 - z11 * x12;
            if (!((ub >= -0.0001f && ub <= d) || (ub <= 0.0001f && ub >= d))) return false;
            float t = ua / d;
            p = new Vec3(t * x11 + a0.X, t * (a1.Y - a0.Y) + a0.Y, t * z11 + a0.Z);
            return true;
        }

        /// <summary>WWorldMath::IntersectSegPlane: t in [0, 1].</summary>
        public static bool IntersectSegPlane(Vec3 p1, Vec3 p2, Vec3 onPlane, Vec3 n, out Vec3 hit, out float t)
        {
            float dx = p2.X - p1.X, dy = p2.Y - p1.Y, dz = p2.Z - p1.Z;
            float d = n.X * dx + n.Y * dy + n.Z * dz;
            hit = default;
            t = 0;
            if (d == 0f) return false;
            t = (n.X * (onPlane.X - p1.X) + n.Y * (onPlane.Y - p1.Y) + n.Z * (onPlane.Z - p1.Z)) / d;
            hit = new Vec3(p1.X + dx * t, p1.Y + dy * t, p1.Z + dz * t);
            return t >= 0f && t <= 1f;
        }

        /// <summary>World → instance local: x·row0 + y·up + z·row2 + t (RotateTranslate with MakeMatrix(invMat, true)).</summary>
        public static Vec3 ToLocal(CollisionInstance i, Vec3 w)
        {
            var up = i.Up;
            return new Vec3(w.X * i.Row0.X + w.Y * up.X + w.Z * i.Row2.X + i.T.X,
                            w.X * i.Row0.Y + w.Y * up.Y + w.Z * i.Row2.Y + i.T.Y,
                            w.X * i.Row0.Z + w.Y * up.Z + w.Z * i.Row2.Z + i.T.Z);
        }

        // ---- faces ----------------------------------------------------------------------------------------------

        static Tri MakeFace(CollisionStrip s, int i)
        {
            var v2 = s.Verts[i + 2];
            return new Tri { P0 = s.Verts[i].Local, P1 = s.Verts[i + 1].Local, P2 = v2.Local, Surface = v2.Surface, Flags = v2.SurfaceFlags };
        }

        /// <summary>FindFaceInTriStrip(point) (WCollisionMgr.cpp:47-108): the first face of the strip whose XZ contains pt.</summary>
        static bool FindFaceInTriStrip(Vec3 pt, CollisionStrip strip, uint mask, out Tri face)
        {
            face = default;
            if ((mask & (uint)strip.Flags) != 0) return false;
            int n = strip.TriangleCount;
            if ((strip.Flags & 2) != 0)
            {
                for (int i = 0; i < n; i++)
                {
                    var f = MakeFace(strip, i);
                    if ((f.Flags & 8) == 0 && InTri(pt, f.P0, f.P1, f.P2) && (mask & f.Flags) == 0 && f.MinY < pt.Y + 1f) { face = f; return true; }
                }
                return false;
            }
            bool right = (strip.Flags & 1) != 0;
            for (int i = 0; i < n; i++, right = !right)
            {
                var f = MakeFace(strip, i);
                if ((f.Flags & 8) != 0) continue;
                bool inside = right ? InTriR(pt, f.P0, f.P1, f.P2) : InTriL(pt, f.P0, f.P1, f.P2);
                if (inside && (mask & f.Flags) == 0) { face = f; return true; }
            }
            return false;
        }

        /// <summary>FindFaceInCInst(point) (WCollisionMgr.cpp:196-261): the face below pt (local) closest along y. World face out.</summary>
        public bool FindFaceInCInst(Vec3 pt, Entry e, uint mask, out Tri face, out float dist)
        {
            face = default;
            dist = 0;
            var inst = e.Inst;
            var art = inst.Article;
            var tpt = ToLocal(inst, pt);
            if (art == null || art.Strips.Count == 0 || tpt.X > inst.HalfWidth || tpt.X < -inst.HalfWidth || tpt.Z > inst.HalfLength || tpt.Z < -inst.HalfLength)
                return false;
            float least = 1e38f;
            bool found = false;
            Tri best = default;
            foreach (var sp in art.Strips)
            {
                float dx = sp.Centre.X - tpt.X, dz = sp.Centre.Z - tpt.Z;
                if (dx * dx + dz * dz >= sp.Radius * sp.Radius) continue;
                if (!FindFaceInTriStrip(tpt, sp, mask, out var f)) continue;
                var n = UpNormal(FaceNormal(f.P0, f.P1, f.P2));
                float d = tpt.Y - GetPlaneY(n, f.P0, tpt);
                bool below = e.IsYVecNotUp ? f.MinY - 0.5f > tpt.Y : f.MinY - 0.5f < tpt.Y;
                if (below && 0f < d && d < least) { found = true; least = d; best = f; dist = d; }
            }
            if (!found) return false;
            face = ToWorld(inst, best, art);
            return true;
        }

        static Tri ToWorld(CollisionInstance inst, Tri f, CollisionArticle art) => new Tri
        {
            P0 = inst.ToWorldSim(f.P0), P1 = inst.ToWorldSim(f.P1), P2 = inst.ToWorldSim(f.P2),
            Surface = f.Surface, Flags = f.Flags, SurfaceKey = art.SurfaceKey(f.Surface),
        };

        /// <summary>A segment-space frame (MakeSegSpaceMatrix): rows right, up (= start − end), forward; origin start.</summary>
        public struct SegFrame
        {
            public Vec3 Right, Up, Fwd, Origin;
            public Vec3 ToSeg(Vec3 w)
            {
                float x = w.X - Origin.X, y = w.Y - Origin.Y, z = w.Z - Origin.Z;
                return new Vec3(x * Right.X + y * Right.Y + z * Right.Z, x * Up.X + y * Up.Y + z * Up.Z, x * Fwd.X + y * Fwd.Y + z * Fwd.Z);
            }
        }

        public static bool MakeSegSpace(Vec3 start, Vec3 end, out SegFrame m)
        {
            m = default;
            if (Math.Abs(start.X - end.X) < 0.0001f && Math.Abs(start.Y - end.Y) < 0.0001f && Math.Abs(start.Z - end.Z) < 0.0001f) return false;
            var up = Unit(new Vec3(start.X - end.X, start.Y - end.Y, start.Z - end.Z));
            var right = Math.Abs(up.Y) > 0.707f ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            var fwd = Unit(Cross(right, up));
            right = Cross(up, fwd);
            m = new SegFrame { Right = right, Up = up, Fwd = fwd, Origin = start };
            return true;
        }

        /// <summary>FindFaceInCInst(matrix, endPt) (WCollisionMgr.cpp:263-340) with FindFaceInTriStrip(matrix) (:140-180).</summary>
        public bool FindFaceInCInst(SegFrame seg, Vec3 endPt, Entry e, uint mask, out Tri face, out float dist)
        {
            face = default;
            dist = 0;
            var inst = e.Inst;
            var art = inst.Article;
            if (art == null) return false;
            var tp0 = ToLocal(inst, seg.Origin);
            var tp1 = ToLocal(inst, endPt);
            float dx = tp1.X - tp0.X, dy = tp1.Y - tp0.Y, dz = tp1.Z - tp0.Z, inv = 1f / (dx * dx + dy * dy + dz * dz);
            float least = 1e38f;
            bool found = false;
            Tri best = default;
            var zero = new Vec3(0, 0, 0);
            foreach (var sp in art.Strips)
            {
                float u = ((sp.Centre.X - tp0.X) * dx + (sp.Centre.Y - tp0.Y) * dy + (sp.Centre.Z - tp0.Z) * dz) * inv;
                u = Math.Max(Math.Min(u, 1f), 0f);
                float nx = sp.Centre.X - (u * dx + tp0.X), ny = sp.Centre.Y - (u * dy + tp0.Y), nz = sp.Centre.Z - (u * dz + tp0.Z);
                if (nx * nx + ny * ny + nz * nz >= sp.Radius * sp.Radius) continue;
                if ((mask & (uint)sp.Flags) != 0) continue;
                // the strip's faces in segment space; the face whose (seg-space) XZ holds the segment, nearest along it
                float bestY = 1e38f;
                int bestI = -1;
                for (int i = 0; i < sp.TriangleCount; i++)
                {
                    var f = MakeFace(sp, i);
                    var a = seg.ToSeg(inst.ToWorldSim(f.P0));
                    var b = seg.ToSeg(inst.ToWorldSim(f.P1));
                    var c = seg.ToSeg(inst.ToWorldSim(f.P2));
                    if (!InTri(zero, a, b, c)) continue;
                    var n = UpNormal(FaceNormal(a, b, c));
                    float fy = 0f - GetPlaneY(n, c, zero);
                    if (fy < bestY && -1f < fy && (mask & f.Flags) == 0) { bestY = fy; bestI = i; }
                }
                if (bestI < 0) continue;
                if (0f < bestY && bestY < least) { least = bestY; best = MakeFace(sp, bestI); dist = bestY; found = true; }
            }
            if (!found) return false;
            face = ToWorld(inst, best, art);
            return true;
        }

        // ---- barriers ---------------------------------------------------------------------------------------------

        /// <summary>GetBarrierNormal(instList, seg) (WCollisionMgr.cpp:925-1006): the closest barrier the segment crosses
        /// within its Y span; the normal is turned toward the segment start (XZ).</summary>
        public bool GetBarrierNormal(List<Entry> list, Vec3 s0, Vec3 s1, uint mask, out Hit hit)
        {
            hit = default;
            float closest = 1e38f;
            Entry bestE = null;
            CollisionBarrier bestB = default;
            Vec3 bestP = default;
            foreach (var e in list)
            {
                var art = e.Inst.Article;
                if (art == null || art.Barriers.Count == 0) continue;
                var t0 = ToLocal(e.Inst, s0);
                var t1 = ToLocal(e.Inst, s1);
                foreach (var b in art.Barriers)
                {
                    if ((mask & (b.SurfaceFlags | e.BarrierExtra)) != 0) continue;
                    if (!SegmentIntersect(t0, t1, b.P0, b.P1, out var p)) continue;
                    float yb = Math.Min(b.P0.Y, b.P1.Y), yt = Math.Max(b.P0.Y, b.P1.Y);
                    if (!(p.Y > yb && p.Y < yt)) continue;
                    float ds = Sq(p.X - t0.X) + Sq(p.Y - t0.Y) + Sq(p.Z - t0.Z);
                    if (ds < closest) { closest = ds; bestE = e; bestB = b; bestP = p; }
                }
            }
            if (bestE == null) return false;
            var w0 = bestE.Inst.ToWorldSim(bestB.P0);
            var w1 = bestE.Inst.ToWorldSim(bestB.P1);
            hit.Type = 2;
            hit.Instance = bestE;
            hit.B0 = w0;
            hit.B1 = w1;
            hit.Point = bestE.Inst.ToWorldSim(bestP);
            hit.Surface = bestE.Inst.Article.SurfaceKey(bestB.Surface);
            hit.SurfaceFlags = (byte)(bestB.SurfaceFlags | bestE.BarrierExtra);
            hit.Normal = BarrierNormal(w0, w1, bestB.InvXZLength);
            float tx = s0.X - hit.Point.X, tz = s0.Z - hit.Point.Z;
            if (hit.Normal.X * tx + hit.Normal.Z * tz < 0f) hit.Normal = new Vec3(-hit.Normal.X, 0f, -hit.Normal.Z);
            hit.Distance = (float)Math.Sqrt(closest);
            return true;
        }

        /// <summary>WCollisionBarrier::GetNormal: (dz, 0, −dx)·invXZLength.</summary>
        public static Vec3 BarrierNormal(Vec3 p0, Vec3 p1, float invLen)
        {
            if (!(invLen > 0f))
            {
                float l = (float)Math.Sqrt(Sq(p1.X - p0.X) + Sq(p1.Z - p0.Z));
                invLen = l > 0f ? 1f / l : 0f;
            }
            return new Vec3((p1.Z - p0.Z) * invLen, 0f, -(p1.X - p0.X) * invLen);
        }

        /// <summary>
        /// GetBarrierList(barrierList, instList, pos, radius) (WCollisionMgr.cpp:1008-1070): the barriers within radius (XZ),
        /// overlapping [y − radius, y + radius], in world space, sorted by distance².
        /// </summary>
        public void GetBarrierList(List<BarrierEntry> result, Vec3 pos, float radius, uint mask = 0)
        {
            result.Clear();
            var list = new List<Entry>();
            GetInstanceList(list, pos, radius, mask);
            float r2 = radius * radius;
            foreach (var e in list)
            {
                var art = e.Inst.Article;
                if (art == null || art.Barriers.Count == 0) continue;
                var tpt = ToLocal(e.Inst, pos);
                foreach (var b in art.Barriers)
                {
                    if ((mask & (b.SurfaceFlags | e.BarrierExtra)) != 0) continue;
                    float ds = BarrierDistSq(b, tpt);
                    float yb = Math.Min(b.P0.Y, b.P1.Y), yt = Math.Max(b.P0.Y, b.P1.Y);
                    if (!(ds < r2 && tpt.Y + radius > yb && tpt.Y - radius <= yt)) continue;
                    var w0 = e.Inst.ToWorldSim(b.P0);
                    var w1 = e.Inst.ToWorldSim(b.P1);
                    var be = new BarrierEntry
                    {
                        P0 = w0, P1 = w1, Normal = BarrierNormal(w0, w1, b.InvXZLength), Surface = art.SurfaceKey(b.Surface),
                        SurfaceFlags = (byte)(b.SurfaceFlags | e.BarrierExtra), DistSq = ds,
                    };
                    int at = result.Count;
                    while (at > 0 && result[at - 1].DistSq > ds) at--;   // upper_bound insert
                    result.Insert(at, be);
                }
            }
        }

        /// <summary>WCollisionBarrier::DistSq: XZ distance² from pt to the barrier segment.</summary>
        static float BarrierDistSq(CollisionBarrier b, Vec3 pt)
        {
            float x1 = b.P0.X, z1 = b.P0.Z, x2 = b.P1.X, z2 = b.P1.Z;
            float inv = b.InvXZLength;
            float u = ((pt.X - x1) * (x2 - x1) + (pt.Z - z1) * (z2 - z1)) * inv * inv;
            if (u < 0f) return Sq(x1 - pt.X) + Sq(z1 - pt.Z);
            if (u > 1f) return Sq(x2 - pt.X) + Sq(z2 - pt.Z);
            return Sq(u * (x2 - x1) + x1 - pt.X) + Sq(u * (z2 - z1) + z1 - pt.Z);
        }

        // ---- the public queries -----------------------------------------------------------------------------------

        readonly List<Entry> _scratch = new List<Entry>();
        readonly WWorldPos _segPos = new WWorldPos(0.025f);

        /// <summary>WCollisionMgr::GetWorldHeightAtPoint (0x7897C0): a fresh WWorldPos(0.025) on the face below pt.</summary>
        public bool GetWorldHeightAtPoint(Vec3 pt, out float height, out Vec3 normal, out uint surface)
        {
            var wp = new WWorldPos(0.025f);
            wp.FindClosestFace(this, pt, true);
            height = 0;
            normal = new Vec3(0, 1, 0);
            surface = 0;
            if (!wp.OnValidFace) return false;
            normal = wp.UNormal();
            height = GetPlaneY(normal, wp.Face.P0, pt);
            surface = wp.Face.SurfaceKey;
            return true;
        }

        /// <summary>
        /// GetWorldHeightAtPointRigorous (0x789870): GetWorldHeightAtPoint, else the first ground face hit by the segment
        /// from 2 m below to 1000 m above (CheckHitWorld, faces only).
        /// </summary>
        public bool GetWorldHeightAtPointRigorous(Vec3 pt, out float height, out Vec3 normal)
        {
            if (GetWorldHeightAtPoint(pt, out height, out normal, out _)) return true;
            var a = new Vec3(pt.X, pt.Y - 2f, pt.Z);
            var b = new Vec3(pt.X, pt.Y + 1000f, pt.Z);
            if (CheckHitWorld(a, b, out var hit, 1) && hit.Type == 1)
            {
                height = hit.Point.Y;
                normal = hit.Normal;
                return true;
            }
            return false;
        }

        /// <summary>
        /// WCollisionMgr::CheckHitWorld (0x7854B0): barriers (primMask 2) then ground faces (primMask 1) along the segment
        /// s0 → s1; the nearer hit to s0 wins. A face hit is where the segment crosses the face's plane; its normal faces s0.
        /// </summary>
        public bool CheckHitWorld(Vec3 s0, Vec3 s1, out Hit hit, uint primMask = 3, uint exclusion = 0)
        {
            hit = default;
            float len = (float)Math.Sqrt(Sq(s1.X - s0.X) + Sq(s1.Y - s0.Y) + Sq(s1.Z - s0.Z));
            if (len == 0f) return false;
            if (len < 0.01f)
            {
                float k = 0.01f / len;
                s1 = new Vec3(s0.X + (s1.X - s0.X) * k, s0.Y + (s1.Y - s0.Y) * k, s0.Z + (s1.Z - s0.Z) * k);
            }
            GetInstanceList(_scratch, s0, s1, exclusion);
            int world = 0;
            if ((primMask & 2) != 0 && GetBarrierNormal(_scratch, s0, s1, exclusion, out hit)) { world = 2; s1 = hit.Point; }
            if ((primMask & 1) != 0)
            {
                _segPos.FindClosestFace(this, _scratch, s0, s1, exclusion);
                if (_segPos.OnValidFace)
                {
                    var n = _segPos.UNormal();
                    if (IntersectSegPlane(s0, s1, _segPos.Face.P0, n, out var p, out _))
                    {
                        if (n.X * (s0.X - p.X) + n.Y * (s0.Y - p.Y) + n.Z * (s0.Z - p.Z) < 0f) n = new Vec3(-n.X, -n.Y, -n.Z);
                        var face = new Hit { Type = 1, Point = p, Normal = n, Surface = _segPos.Face.SurfaceKey, SurfaceFlags = _segPos.Face.Flags };
                        face.Distance = (float)Math.Sqrt(Sq(p.X - s0.X) + Sq(p.Y - s0.Y) + Sq(p.Z - s0.Z));
                        if (world == 0 || face.Distance < hit.Distance) { hit = face; world = 1; }
                    }
                }
            }
            if (world == 2) hit.Distance = (float)Math.Sqrt(Sq(hit.Point.X - s0.X) + Sq(hit.Point.Y - s0.Y) + Sq(hit.Point.Z - s0.Z));
            return hit.HitSomething;
        }

        // ---- WCollisionObjects (OBBs) ------------------------------------------------------------------------------

        readonly List<CollisionObject> _objects = new List<CollisionObject>();

        /// <summary>WCollisionAssets::AddObject / CreateObject: a managed object (the track OBBs, WCollisionAssets::Init).</summary>
        public void AddObject(CollisionObject o) { if (o != null && !_objects.Contains(o)) _objects.Add(o); }
        public void RemoveObject(CollisionObject o) => _objects.Remove(o);
        public int ObjectCount => _objects.Count;

        /// <summary>GetObjectList(pt, radius) (WCollisionMgr.cpp:682-711): type-0 (box) objects whose XZ circle meets
        /// (pt, radius). The exe walks the WGrid nodes; here every managed object and every loaded pack's 'co' objects.</summary>
        public void GetObjectList(List<CollisionObject> list, Vec3 pt, float radius)
        {
            list.Clear();
            void Test(CollisionObject o)
            {
                float rs = radius + o.Radius, dx = o.Position.X - pt.X, dz = o.Position.Z - pt.Z;
                if (dx * dx + dz * dz < rs * rs && o.Type == 0) list.Add(o);
            }
            foreach (var o in _objects) Test(o);
            foreach (var kv in _packs) foreach (var o in kv.Key.Objects) Test(o);
        }

        // ---- tri lists (WCollider's cached faces) ------------------------------------------------------------------

        /// <summary>
        /// GetTriList (WCollisionMgr.cpp:1130-1199): the world-space faces of every strip of the listed instances whose
        /// sphere meets (pt, radius) in XZ, keeping a face when its XZ box overlaps the region's and the region circle is
        /// not wholly outside any of its edges (NearestPointLine2D3 distance ≥ radius on the outer side), each with its
        /// simsurface key. The exe offsets the local faces by the strip centre − the inverse translation, i.e. assumes an
        /// unrotated instance; ToWorldSim is used here (the same for those, exact for the rest) [?].
        /// </summary>
        public void GetTriList(List<Entry> instList, Vec3 pt, float radius, List<Tri> triList)
        {
            float radiusSq = radius * radius;
            float rx0 = pt.X - radius, rz0 = pt.Z - radius, rx1 = pt.X + radius, rz1 = pt.Z + radius;
            foreach (var e in instList)
            {
                var inst = e.Inst;
                var art = inst.Article;
                if (art == null) continue;
                var tpt = ToLocal(inst, pt);
                foreach (var sp in art.Strips)
                {
                    float dx = sp.Centre.X - tpt.X, dz = sp.Centre.Z - tpt.Z, rs = sp.Radius + radius;
                    if (dx * dx + dz * dz >= rs * rs) continue;
                    for (int i = 0; i < sp.TriangleCount; i++)
                    {
                        var f = ToWorld(inst, MakeFace(sp, i), art);
                        float fx0 = Math.Min(f.P2.X, Math.Min(f.P1.X, f.P0.X)), fx1 = Math.Max(f.P2.X, Math.Max(f.P1.X, f.P0.X));
                        float fz0 = Math.Min(f.P2.Z, Math.Min(f.P1.Z, f.P0.Z)), fz1 = Math.Max(f.P2.Z, Math.Max(f.P1.Z, f.P0.Z));
                        if (rx0 > fx1 || rz0 > fz1 || rx1 < fx0 || rz1 < fz0) continue;
                        float dir = PtDir(f.P2, f.P0, f.P1);   // PTDir(p1, p0, p2) (UVectorMath.h:203): the orientation of (p0, p2, p1)
                        if (OutsideEdge(pt, f.P0, f.P1, dir, radiusSq) || OutsideEdge(pt, f.P1, f.P2, dir, radiusSq) || OutsideEdge(pt, f.P2, f.P0, dir, radiusSq))
                            continue;
                        triList.Add(f);
                    }
                }
            }
        }

        static bool OutsideEdge(Vec3 pt, Vec3 a, Vec3 b, float dir, float radiusSq)
        {
            float side = PtDir(a, b, pt);
            if (side * dir > 0f) return false;
            var near = WWorldMath.NearestPointLine2D(pt, a, b);
            return Sq(pt.X - near.X) + Sq(pt.Z - near.Z) >= radiusSq;
        }

        // ---- barrier lists ------------------------------------------------------------------------------------------

        /// <summary>
        /// GetClosestIntersectingBarrier + GetBarrierNormal(barrierList, seg) (WCollisionMgr.cpp:893-923, 1074-1092): the
        /// listed barrier (passing the exclusion mask) the segment crosses within its Y span nearest to the segment start;
        /// the normal is turned toward the start in XZ.
        /// </summary>
        public static bool GetBarrierNormal(List<BarrierEntry> barriers, Vec3 s0, Vec3 s1, uint mask, out Hit hit)
        {
            hit = default;
            float closest = 1e38f;
            int best = -1;
            Vec3 bestP = default;
            for (int i = 0; i < barriers.Count; i++)
            {
                var b = barriers[i];
                if ((mask & b.SurfaceFlags) != 0) continue;
                if (!SegmentIntersect(s0, s1, b.P0, b.P1, out var p)) continue;
                float yb = Math.Min(b.P0.Y, b.P1.Y), yt = Math.Max(b.P0.Y, b.P1.Y);
                if (!(p.Y > yb && p.Y < yt)) continue;
                float ds = Sq(p.X - s0.X) + Sq(p.Y - s0.Y) + Sq(p.Z - s0.Z);
                if (ds < closest) { closest = ds; best = i; bestP = p; }
            }
            if (best < 0) return false;
            var be = barriers[best];
            hit.Type = 2;
            hit.B0 = be.P0;
            hit.B1 = be.P1;
            hit.Point = bestP;
            hit.Surface = be.Surface;
            hit.SurfaceFlags = be.SurfaceFlags;
            hit.Normal = be.Normal;
            float tx = s0.X - bestP.X, tz = s0.Z - bestP.Z;
            if (hit.Normal.X * tx + hit.Normal.Z * tz < 0f) hit.Normal = new Vec3(-hit.Normal.X, 0f, -hit.Normal.Z);
            hit.Distance = (float)Math.Sqrt(closest);
            return true;
        }

        readonly WWorldPos _normalPos = new WWorldPos(0.025f);

        /// <summary>
        /// GetWorldNormal(instList, barrierList, seg) (WCollisionMgr.cpp:483-522): the barrier hit from the barrier list and
        /// the ground face along the segment from the instance list (a WWorldPos(0.025) segment search; the face normal turned
        /// toward the segment start), the nearer to the start wins (ClosestCollisionInfo :524-542; a tie goes to the barrier).
        /// Either list may be null.
        /// </summary>
        public bool GetWorldNormal(List<Entry> instList, List<BarrierEntry> barrierList, Vec3 s0, Vec3 s1, out Hit hit, uint mask = 0)
        {
            Hit faces = default, bar = default;
            if (barrierList != null) GetBarrierNormal(barrierList, s0, s1, mask, out bar);
            if (instList != null)
            {
                _normalPos.FindClosestFace(this, instList, s0, s1, mask);
                if (_normalPos.OnValidFace)
                {
                    var n = _normalPos.UNormal();
                    if (IntersectSegPlane(s0, s1, _normalPos.Face.P0, n, out var p, out _))
                    {
                        if (n.X * (s0.X - p.X) + n.Y * (s0.Y - p.Y) + n.Z * (s0.Z - p.Z) < 0f) n = new Vec3(-n.X, -n.Y, -n.Z);
                        faces = new Hit { Type = 1, Point = p, Normal = n, Surface = _normalPos.Face.SurfaceKey, SurfaceFlags = _normalPos.Face.Flags };
                        faces.Distance = (float)Math.Sqrt(Sq(p.X - s0.X) + Sq(p.Y - s0.Y) + Sq(p.Z - s0.Z));
                    }
                }
            }
            hit = ClosestCollisionInfo(s0, faces, bar);
            return hit.HitSomething;
        }

        /// <summary>ClosestCollisionInfo (WCollisionMgr.cpp:524-542): the hit nearer to the segment start (c2 on a tie).</summary>
        public static Hit ClosestCollisionInfo(Vec3 s0, Hit c1, Hit c2)
        {
            if (!c1.HitSomething && !c2.HitSomething) return default;
            float d1 = c1.HitSomething ? Sq(c1.Point.X - s0.X) + Sq(c1.Point.Y - s0.Y) + Sq(c1.Point.Z - s0.Z) : 9.9e31f;
            float d2 = c2.HitSomething ? Sq(c2.Point.X - s0.X) + Sq(c2.Point.Y - s0.Y) + Sq(c2.Point.Z - s0.Z) : 9.9e31f;
            return d1 < d2 ? c1 : c2;
        }

        /// <summary>GetBarrierList(barrierList, instList, pos, radius) over an instance list the caller already has
        /// (WCollider::PrepareRegion).</summary>
        public void GetBarrierList(List<BarrierEntry> result, List<Entry> instList, Vec3 pos, float radius, uint mask = 0)
        {
            result.Clear();
            float r2 = radius * radius;
            foreach (var e in instList)
            {
                var art = e.Inst.Article;
                if (art == null || art.Barriers.Count == 0) continue;
                var tpt = ToLocal(e.Inst, pos);
                foreach (var b in art.Barriers)
                {
                    if ((mask & (b.SurfaceFlags | e.BarrierExtra)) != 0) continue;
                    float ds = BarrierDistSq(b, tpt);
                    float yb = Math.Min(b.P0.Y, b.P1.Y), yt = Math.Max(b.P0.Y, b.P1.Y);
                    if (!(ds < r2 && tpt.Y + radius > yb && tpt.Y - radius <= yt)) continue;
                    var w0 = e.Inst.ToWorldSim(b.P0);
                    var w1 = e.Inst.ToWorldSim(b.P1);
                    var be = new BarrierEntry
                    {
                        P0 = w0, P1 = w1, Normal = BarrierNormal(w0, w1, b.InvXZLength), Surface = art.SurfaceKey(b.Surface),
                        SurfaceFlags = (byte)(b.SurfaceFlags | e.BarrierExtra), DistSq = ds,
                    };
                    int at = result.Count;
                    while (at > 0 && result[at - 1].DistSq > ds) at--;
                    result.Insert(at, be);
                }
            }
        }

        internal List<Entry> Scratch => _scratch;

        static float Sq(float x) => x * x;
        static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        static Vec3 Unit(Vec3 v)
        {
            float l = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return l > 0f ? new Vec3(v.X / l, v.Y / l, v.Z / l) : v;
        }
    }

    /// <summary>
    /// WWorldPos (World/WWorldPos.h, WWorldPos.cpp): a cached "face under this point". FindClosestFace lifts the point by
    /// fYOffset (0.025 by default) and keeps the old face while the point stays inside it in XZ (quitIfOnSameFace).
    /// </summary>
    public sealed class WWorldPos
    {
        public float YOffset;
        public bool OnValidFace { get; private set; }
        public WCollisionQueries.Tri Face;
        /// <summary>fSurface: the face's simsurface key; null (kNull) when no face [Update clears it].</summary>
        public bool SurfaceNull = true;
        readonly List<WCollisionQueries.Entry> _list = new List<WCollisionQueries.Entry>();

        public WWorldPos(float yOffset) { YOffset = yOffset; }

        /// <summary>fUsageCount: searches made (every FindClosestFace call).</summary>
        public int UsageCount;

        /// <summary>SetTolerance (WWorldPos.h:50): the lift amount (DoInstanceCollision2d sets a speed-based one per point).</summary>
        public void SetTolerance(float liftAmount) => YOffset = liftAmount;

        /// <summary>FindClosestFace(collider, pt, quit) (WWorldPos.cpp:11-16): the collider's tri list when there is one.</summary>
        public bool FindClosestFace(WCollisionQueries w, WCollider collider, Vec3 ptRaw, bool quitIfOnSameFace, uint mask = 0) =>
            collider != null ? FindClosestFace(collider.Tris, ptRaw, quitIfOnSameFace) : FindClosestFace(w, ptRaw, quitIfOnSameFace, mask);

        /// <summary>
        /// FindClosestFace(triList, pt, quit) (WWorldPos.cpp:100-152): the cached face is kept while the (unlifted) point is
        /// inside it in XZ; else, lifted by YOffset, the first listed face holding it whose plane is below it by less than
        /// any found so far and not more than 1 m above it (dist &gt; −1); the search stops at the first hit unless that
        /// face's surface is stacked (flag 4). Returns true unless it stayed on the same face.
        /// </summary>
        public bool FindClosestFace(List<WCollisionQueries.Tri> triList, Vec3 ipt, bool quitIfOnSameFace)
        {
            UsageCount++;
            var pt = ipt;
            bool onSameFace = OnValidFace && WCollisionQueries.InTri(pt, Face.P0, Face.P1, Face.P2);
            if (!onSameFace || !quitIfOnSameFace)
            {
                OnValidFace = false;
                pt.Y += YOffset;
                float best = 1e38f;
                foreach (var tri in triList)
                {
                    if (!WCollisionQueries.InTri(pt, tri.P0, tri.P1, tri.P2)) continue;
                    var n = WCollisionQueries.UpNormal(WCollisionQueries.FaceNormal(tri.P0, tri.P1, tri.P2));
                    float dist = pt.Y - WCollisionQueries.GetPlaneY(n, tri.P0, pt);
                    if (dist < best && -1f < dist)
                    {
                        OnValidFace = true;
                        Face = tri;
                        SurfaceNull = false;
                        best = dist;
                        if ((Face.Flags & 4) == 0) break;
                    }
                }
            }
            return !onSameFace;
        }

        /// <summary>WWorldPos::Update with a collider (the RigidBody / suspension form).</summary>
        public bool Update(WCollisionQueries w, WCollider collider, Vec3 pos, out Vec3 normal, out float planeDist, bool useCache = true, bool keepValid = false)
        {
            bool was = OnValidFace;
            FindClosestFace(w, collider, pos, useCache);
            normal = UNormal();
            planeDist = -1f;
            if (!OnValidFace)
            {
                SurfaceNull = true;
                if (keepValid && was) OnValidFace = true;
                else return false;
            }
            planeDist = (Face.P0.X - pos.X) * normal.X + (Face.P0.Y - pos.Y) * normal.Y + (Face.P0.Z - pos.Z) * normal.Z;
            return true;
        }

        public void Invalidate() { OnValidFace = false; SurfaceNull = true; }

        /// <summary>FindClosestFace(pt, quitIfOnSameFace) → FindClosestFaceInternal (WWorldPos.cpp:18-49). True when searched.</summary>
        public bool FindClosestFace(WCollisionQueries w, Vec3 ptRaw, bool quitIfOnSameFace, uint mask = 0)
        {
            UsageCount++;
            var pt = new Vec3(ptRaw.X, ptRaw.Y + YOffset, ptRaw.Z);
            bool inFace = OnValidFace && WCollisionQueries.InTri(pt, Face.P0, Face.P1, Face.P2);
            if (inFace && quitIfOnSameFace) return false;
            w.GetInstanceList(_list, pt, 0f, mask);
            OnValidFace = false;
            float best = 1e38f;
            WCollisionQueries.SegFrame seg = default;
            bool segValid = false;
            Vec3 end = default;
            foreach (var e in _list)
            {
                WCollisionQueries.Tri f;
                float d;
                if (!e.NeedsCrossProduct)
                {
                    if (!w.FindFaceInCInst(pt, e, mask, out f, out d)) continue;
                }
                else
                {
                    if (!segValid)
                    {
                        var start = new Vec3(pt.X, pt.Y + YOffset, pt.Z);
                        end = new Vec3(pt.X, pt.Y - 750f, pt.Z);
                        segValid = WCollisionQueries.MakeSegSpace(start, end, out seg);
                        if (!segValid) continue;
                    }
                    if (!w.FindFaceInCInst(seg, end, e, mask, out f, out d)) continue;
                    d -= YOffset;
                }
                if (d < best) { OnValidFace = true; Face = f; SurfaceNull = false; best = d; }
            }
            return true;
        }

        /// <summary>FindClosestFace(instList, pt, endPt) (WWorldPos.cpp:154-180): the nearest face along a segment.</summary>
        public void FindClosestFace(WCollisionQueries w, List<WCollisionQueries.Entry> list, Vec3 pt, Vec3 endPt, uint mask = 0)
        {
            OnValidFace = false;
            if (!WCollisionQueries.MakeSegSpace(pt, endPt, out var seg)) return;
            float best = 1e38f;
            foreach (var e in list)
                if (w.FindFaceInCInst(seg, endPt, e, mask, out var f, out float d) && d < best) { OnValidFace = true; Face = f; SurfaceNull = false; best = d; }
        }

        /// <summary>UNormal: the face normal flipped up, y capped at 0.9999; (0,1,0) without a face.</summary>
        public Vec3 UNormal() => OnValidFace ? WCollisionQueries.UpNormal(WCollisionQueries.FaceNormal(Face.P0, Face.P1, Face.P2)) : new Vec3(0, 1, 0);

        /// <summary>HeightAtPoint (0x768200): the face plane's y at pt (0 without a face).</summary>
        public float HeightAtPoint(Vec3 pt) => OnValidFace ? WCollisionQueries.GetPlaneY(UNormal(), Face.P0, pt) : 0f;

        /// <summary>
        /// WWorldPos::Update (0x789CC0): find the face (cached), else surface kNull and (keepValid and it was valid) keep the
        /// old face; dest = normal, w = signed distance from pos to the face plane along it. False when no face.
        /// </summary>
        public bool Update(WCollisionQueries w, Vec3 pos, out Vec3 normal, out float planeDist, bool useCache = true, bool keepValid = false)
        {
            bool was = OnValidFace;
            FindClosestFace(w, pos, useCache);
            normal = UNormal();
            planeDist = -1f;
            if (!OnValidFace)
            {
                SurfaceNull = true;
                if (keepValid && was) OnValidFace = true;
                else return false;
            }
            planeDist = (Face.P0.X - pos.X) * normal.X + (Face.P0.Y - pos.Y) * normal.Y + (Face.P0.Z - pos.Z) * normal.Z;
            return true;
        }
    }
}
