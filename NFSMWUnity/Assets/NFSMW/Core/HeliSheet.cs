using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// The helicopter's flight sheet (World/HeliSheet.cpp; chunk BCHUNK_SPEED_HELI_SHEET 0x34159, one per drivable track
    /// section, streamed with the section). A section is `HeliSection {bTNode 8, i32 SectionNumber, i32 NumPolys,
    /// i32 EndianSwapped, HeliPoly* PolyTable}` at the 16-aligned chunk data, the polys right after it (0x18 bytes in the
    /// file, the pointer is fixed up at load): `HeliPoly {i16 X[3], i16 Y[3], i16 Z[3]}` with x, y in quarter metres and z
    /// in sixteenths (GetVertex: ×0.25, ×0.25, ×0.0625), game axes (z up).
    /// HeliSheetManager::FindHeliPoly: the section from VisibleSectionManager::GetDrivableSectionNumber, then the first
    /// poly whose quarter-metre box holds (int)(p·4) and whose triangle holds p (bIsPointInPoly).
    /// HeliSheetCoordinate::GetElevation: the plane of the cached triangle (re-found when the point leaves it), or the
    /// previous elevation (1000 at first) and "invalid" when no poly holds the point.
    /// </summary>
    public sealed class HeliSheet
    {
        public const uint ChunkId = 0x34159;

        public struct Poly
        {
            public short X0, X1, X2, Y0, Y1, Y2, Z0, Z1, Z2;
            public Vec3 Vertex(int n) => n == 0 ? new Vec3(X0 * 0.25f, Y0 * 0.25f, Z0 * 0.0625f)
                : n == 1 ? new Vec3(X1 * 0.25f, Y1 * 0.25f, Z1 * 0.0625f) : new Vec3(X2 * 0.25f, Y2 * 0.25f, Z2 * 0.0625f);
        }

        public sealed class Section
        {
            public int Number;
            public Poly[] Polys;
        }

        /// <summary>Every heli sheet section found in a block of chunks (a stream section's data).</summary>
        public static List<Section> ReadAll(byte[] d, int start, int end)
        {
            var list = new List<Section>();
            foreach (var c in Chunks.Read(d, start, end))
            {
                if (c.Id == ChunkId)
                {
                    int o = Chunks.Align(c.DataStart, 16);
                    if (o + 0x18 > c.End) continue;
                    int number = Chunks.I32(d, o + 8), n = Chunks.I32(d, o + 12);
                    int p = o + 0x18;
                    if (n < 0 || p + n * 18L > c.End) continue;
                    var s = new Section { Number = number, Polys = new Poly[n] };
                    for (int i = 0; i < n; i++, p += 18)
                        s.Polys[i] = new Poly
                        {
                            X0 = Chunks.I16(d, p), X1 = Chunks.I16(d, p + 2), X2 = Chunks.I16(d, p + 4),
                            Y0 = Chunks.I16(d, p + 6), Y1 = Chunks.I16(d, p + 8), Y2 = Chunks.I16(d, p + 10),
                            Z0 = Chunks.I16(d, p + 12), Z1 = Chunks.I16(d, p + 14), Z2 = Chunks.I16(d, p + 16),
                        };
                    list.Add(s);
                }
                else if ((c.Id & 0x80000000u) != 0) list.AddRange(ReadAll(d, c.DataStart, c.End));
            }
            return list;
        }

        /// <summary>FindHeliPoly within one section (game x, y).</summary>
        public static bool Find(Section s, float x, float y, out Poly poly)
        {
            poly = default;
            if (s == null) return false;
            int ix = (int)(x * 4f), iy = (int)(y * 4f);
            foreach (var h in s.Polys)
            {
                int maxX = Math.Max(Math.Max(h.X0, h.X1), h.X2), minX = Math.Min(Math.Min(h.X0, h.X1), h.X2);
                if (ix > maxX || ix < minX) continue;
                int maxY = Math.Max(Math.Max(h.Y0, h.Y1), h.Y2), minY = Math.Min(Math.Min(h.Y0, h.Y1), h.Y2);
                if (iy > maxY || iy < minY) continue;
                if (InTriangle(x, y, h.Vertex(0), h.Vertex(1), h.Vertex(2))) { poly = h; return true; }
            }
            return false;
        }

        /// <summary>bIsPointInPoly for a triangle (the point on the same side of every edge) [?: edge-inclusive].</summary>
        public static bool InTriangle(float x, float y, Vec3 a, Vec3 b, Vec3 c)
        {
            float d0 = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            float d1 = (c.X - b.X) * (y - b.Y) - (c.Y - b.Y) * (x - b.X);
            float d2 = (a.X - c.X) * (y - c.Y) - (a.Y - c.Y) * (x - c.X);
            bool neg = d0 < 0 || d1 < 0 || d2 < 0, pos = d0 > 0 || d1 > 0 || d2 > 0;
            return !(neg && pos);
        }

        /// <summary>The triangle's plane at (x, y): (dot(v0, n) − n.x·x − n.y·y) / n.z, n = unit((v1 − v0) × (v2 − v0)).</summary>
        public static float Elevation(Poly p, float x, float y, out Vec3 normal)
        {
            var v0 = p.Vertex(0); var v1 = p.Vertex(1); var v2 = p.Vertex(2);
            float ax = v1.X - v0.X, ay = v1.Y - v0.Y, az = v1.Z - v0.Z, bx = v2.X - v0.X, by = v2.Y - v0.Y, bz = v2.Z - v0.Z;
            float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
            float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0) { nx /= len; ny /= len; nz /= len; }
            normal = new Vec3(nx, ny, nz);
            if (Math.Abs(nz) < 1e-6f) return v0.Z;
            float plane = v0.X * nx + v0.Y * ny + v0.Z * nz;
            return (plane - (nx * x + ny * y)) / nz;
        }
    }
}
