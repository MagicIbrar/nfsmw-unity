using System;

namespace NfsMw.Core
{
    /// <summary>
    /// WWorldMath (World/Common/WWorldMath.cpp, WWorldMath.h; exe 0x7628B0-0x767EF1), sim axes (y up, the XZ plane is the
    /// ground). The face/plane helpers WCollisionMgr uses live in <see cref="WCollisionQueries"/> (InTri, GetPlaneY,
    /// IntersectSegPlane, MakeSegSpace, SegmentIntersect); this class gives every WWorldMath entry point in one place
    /// with the exe's exact arithmetic.
    /// </summary>
    public static class WWorldMath
    {
        static float Pow2(float a) => a * a;

        /// <summary>InCircle (WWorldMath.h:25): strictly inside.</summary>
        public static bool InCircle(float x, float y, float cx, float cy, float r) => (cx - x) * (cx - x) + (cy - y) * (cy - y) < r * r;

        /// <summary>PtsEqual (WWorldMath.h:33): every axis within the tolerance.</summary>
        public static bool PtsEqual(Vec3 p0, Vec3 p1, float tolerance) =>
            Math.Abs(p0.X - p1.X) < tolerance && Math.Abs(p0.Y - p1.Y) < tolerance && Math.Abs(p0.Z - p1.Z) < tolerance;

        /// <summary>SegmentIntersect (0x7628B0): the two segments' XZ crossing (±1e-4 slack on both parameters), y from line 1.</summary>
        public static bool SegmentIntersect(Vec3 l10, Vec3 l11, Vec3 l20, Vec3 l21, out Vec3 p) => WCollisionQueries.SegmentIntersect(l10, l11, l20, l21, out p);

        /// <summary>
        /// IntersectCircle (0x767400): does the segment (x1,y1)→(x2,y2) meet the circle (cx, cy, r)? Both ends inside →
        /// true with u1 = u2 = 0; a zero-length segment → true (u 0); else the roots u1 ≤ u2 of |p1 + u·d − c|² = r²
        /// (b = 2·d·p1, c = 4(|p1|² − r²), disc = b² − a·c, the root through sqrt 0x5C5E80) and true when either lies in [0, 1].
        /// </summary>
        public static bool IntersectCircle(float x1, float y1, float x2, float y2, float cx, float cy, float r, out float u1, out float u2)
        {
            u1 = u2 = 0f;
            if (InCircle(x1, y1, cx, cy, r) && InCircle(x2, y2, cx, cy, r)) return true;
            y1 -= cy; y2 -= cy; y2 -= y1;
            x1 -= cx; x2 -= cx; x2 -= x1;
            float a = Pow2(x2) + Pow2(y2);
            if (a == 0f) return true;
            float b = x2 * x1 + y2 * y1 + (x2 * x1 + y2 * y1);
            float c = (x1 * x1 + y1 * y1 - r * r) * 4f;
            float t = b * b - a * c;
            if (t < 0f) return false;
            float root = (float)Math.Sqrt(t);
            float inv2A = 1f / (a + a);
            u1 = (-b - root) * inv2A;
            u2 = (-b + root) * inv2A;
            return u1 >= 0f && u1 <= 1f || u2 >= 0f && u2 <= 1f;
        }

        /// <summary>NearestPointLine2D / NearestPointLine2D3 (0x767xxx): the XZ projection of pt on the infinite line p0→p1
        /// (u not clamped; u = 0 for a line under 1e-3 m), y = 0.</summary>
        public static Vec3 NearestPointLine2D(Vec3 pt, Vec3 p0, Vec3 p1)
        {
            float u = (pt.X - p0.X) * (p1.X - p0.X) + (pt.Z - p0.Z) * (p1.Z - p0.Z);
            float div = Pow2(p1.X - p0.X) + Pow2(p1.Z - p0.Z);
            u = 1e-6f < div ? u / div : 0f;
            return new Vec3(u * (p1.X - p0.X) + p0.X, 0f, u * (p1.Z - p0.Z) + p0.Z);
        }

        /// <summary>GetPlaneY: the plane's y at testPoint (pointOnPlane.y when the normal is horizontal).</summary>
        public static float GetPlaneY(Vec3 normal, Vec3 pointOnPlane, Vec3 testPoint) => WCollisionQueries.GetPlaneY(normal, pointOnPlane, testPoint);

        /// <summary>IntersectSegPlane: the crossing point and t; true for t in [0, 1].</summary>
        public static bool IntersectSegPlane(Vec3 p1, Vec3 p2, Vec3 onPlane, Vec3 normal, out Vec3 hit, out float t) =>
            WCollisionQueries.IntersectSegPlane(p1, p2, onPlane, normal, out hit, out t);

        /// <summary>MakeSegSpaceMatrix: up = unit(start − end), right/forward completed; false for a degenerate segment.</summary>
        public static bool MakeSegSpaceMatrix(Vec3 start, Vec3 end, out WCollisionQueries.SegFrame m) => WCollisionQueries.MakeSegSpace(start, end, out m);

        /// <summary>InTri (WWorldMath.h:42): pt inside the triangle in XZ, either winding.</summary>
        public static bool InTri(Vec3 pt, Vec3 a, Vec3 b, Vec3 c) => WCollisionQueries.InTri(pt, a, b, c);
    }
}
