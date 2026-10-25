using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// World collision for one drivable section: WCollisionPack, chunk 0x3B801 (BCHUNK_W_COLLISION_ASSETS) inside the
    /// streamed sections of TRACKS\STREAML2RA.BUN (specs/traffic_collision.md part B; World/Common/WCollisionPack.cpp,
    /// WCollisionAssets.cpp:183-212). After the chunk header: padding to 16 bytes, a bChunkCarpHeader {crpSize, section
    /// number, flags, last address} (bChunk.hpp:243-251), then a CARP UGroup tree 'CARP' → 'Arti' whose UData are
    /// 'Name' (the section as text), 'ci' (WCollisionInstance[count], 0x40 each) and 'ca'|i (WCollisionArticle i).
    /// UGroup/UData (UGroup.hpp:31-124) are 16-byte headers {tag, bits, count, offset}; embedded groups keep their
    /// children at this + offset·16, embedded data at this + offset bytes. Everything is in sim ("bond") axes:
    /// sim = (−gameY, gameZ, gameX), so Unity = (sim.z, sim.y, −sim.x).
    /// </summary>
    public sealed class CollisionPack
    {
        public const uint ChunkId = 0x3B801;
        public int Section;     // the drivable section (e.g. 101 = A01), not the stream section that holds the pack
        public string Name;
        public readonly List<CollisionInstance> Instances = new List<CollisionInstance>();
        /// <summary>The pack's own WCollisionObjects ('co' UData, WCollisionPack::Resolve :72-90; 0x70 each). [harness
        /// `world9 objects`: none in the shipped STREAML2RA packs — the world OBBs are L2RA's TrackOBBs, made into managed
        /// objects by WCollisionAssets::Init (:77-105)]</summary>
        public readonly List<CollisionObject> Objects = new List<CollisionObject>();

        const uint TagCarp = 0x43415250, TagArti = 0x41727469, TagName = 0x4E616D65, TagInstances = 0x63690000, TagArticle = 0x63610000, TagObjects = 0x636F0000;

        /// <summary>Every pack in [start, end), searching containers recursively (as the loader walks chunks).</summary>
        public static List<CollisionPack> ReadAll(byte[] d, int start, int end, int depth = 0)
        {
            var list = new List<CollisionPack>();
            foreach (var c in Chunks.Read(d, start, end))
            {
                if (c.IsContainer && depth < 4) list.AddRange(ReadAll(d, c.DataStart, c.End, depth + 1));
                else if (c.Id == ChunkId)
                {
                    var p = Read(d, c);
                    if (p != null) list.Add(p);
                }
            }
            return list;
        }

        /// <summary>One 0x3B801 chunk (null when its CARP header does not check out).</summary>
        public static CollisionPack Read(byte[] d, Chunk c)
        {
            // chunk->GetAlignedData(16): the 0x11 fill bytes up to the 16-byte boundary; crpSize must fill the rest
            int o = c.DataStart;
            while (o < c.End && d[o] == 0x11 && o - c.DataStart < 16) o++;
            if (o + 16 > c.End || Chunks.I32(d, o) != c.End - o - 16)
            {
                o = Chunks.Align(c.DataStart, 16);
                if (o + 16 > c.End) return null;
            }
            var pack = new CollisionPack { Section = Chunks.I32(d, o + 4) };
            int carp = o + 16, end = Math.Min(c.End, carp + Math.Max(0, Chunks.I32(d, o)));
            if (carp + 16 > end || Chunks.U32(d, carp) != TagCarp) return null;
            int arti = FindGroup(d, carp, end, TagArti);
            if (arti < 0) return null;
            var articles = new Dictionary<int, CollisionArticle>();
            int instAt = -1, instCount = 0;
            foreach (var (tag, at, count, size) in DataOf(d, arti, end))
            {
                if (tag == TagName) pack.Name = Chunks.CString(d, at, Math.Max(4, size));
                else if (tag == TagInstances) { instAt = at; instCount = Math.Min(count, Math.Max(0, (end - at) / 0x40)); }
                else if (tag == TagObjects)
                    for (int i = 0; i < count && at + (i + 1) * 0x70 <= end; i++) pack.Objects.Add(CollisionObject.Read(d, at + i * 0x70));
                else if ((tag & 0xFFFF0000u) == TagArticle) articles[(int)(tag & 0xFFFF)] = CollisionArticle.Read(d, at, Math.Min(end, at + size));
            }
            for (int i = 0; i < instCount; i++)
            {
                var inst = CollisionInstance.Read(d, instAt + i * 0x40);
                articles.TryGetValue(inst.RenderIndex, out inst.Article);
                pack.Instances.Add(inst);
            }
            return pack;
        }

        /// <summary>The first embedded child group with this tag below the group at g (depth first).</summary>
        static int FindGroup(byte[] d, int g, int end, uint tag)
        {
            if (Chunks.U32(d, g) == tag) return g;
            uint bits = Chunks.U32(d, g + 4);
            if ((bits & 2) == 0) return -1;
            int groups = (int)(bits >> 5), kids = g + Chunks.I32(d, g + 12) * 16;
            for (int i = 0; i < groups; i++)
            {
                int k = kids + i * 16;
                if (k < g || k + 16 > end) break;
                int r = FindGroup(d, k, end, tag);
                if (r >= 0) return r;
            }
            return -1;
        }

        /// <summary>A group's UData: (tag, absolute data offset, count, fSize bytes).</summary>
        static IEnumerable<(uint tag, int at, int count, int size)> DataOf(byte[] d, int g, int end)
        {
            uint bits = Chunks.U32(d, g + 4);
            if ((bits & 2) == 0) yield break;
            int groups = (int)(bits >> 5), datas = Chunks.I32(d, g + 8);
            int first = g + Chunks.I32(d, g + 12) * 16 + groups * 16;
            for (int i = 0; i < datas; i++)
            {
                int u = first + i * 16;
                if (u + 16 > end) yield break;
                uint ub = Chunks.U32(d, u + 4);
                if ((ub & 2) == 0) continue;   // not embedded: nothing on disk
                int at = u + Chunks.I32(d, u + 12);
                if (at < 0 || at > end) continue;
                yield return (Chunks.U32(d, u), at, Chunks.I32(d, u + 8), (int)(ub >> 8));
            }
        }
    }

    /// <summary>
    /// WCollisionInstance (0x40; CARP.h:277-288, WCollision.h:255-274, WCollision.cpp:28-83): the world→local rotation
    /// rows 0 and 2 with the half-width/half-length in .w, the translation of world→local with the XZ radius in .w,
    /// the half-height, the scenery group (0 = always on) and the article index.
    /// </summary>
    public sealed class CollisionInstance
    {
        public Vec3 Row0, Row2, T;
        public float HalfWidth, HalfLength, Radius, HalfHeight;
        /// <summary>1 = local Y is not world up (up = cross(row2, row0)); 2 = dynamic. All 0 on disk.</summary>
        public ushort Flags;
        /// <summary>Scenery group number (L2RA 0x34109 +0x0C): the instance only collides while that group is enabled.</summary>
        public ushort Group;
        public ushort RenderIndex;
        public CollisionArticle Article;

        public static CollisionInstance Read(byte[] d, int o) => new CollisionInstance
        {
            Row0 = Vec3.Read(d, o), HalfWidth = Chunks.F32(d, o + 0x0C),
            Flags = Chunks.U16(d, o + 0x12), HalfHeight = Chunks.F32(d, o + 0x14),
            Group = Chunks.U16(d, o + 0x18), RenderIndex = Chunks.U16(d, o + 0x1A),
            Row2 = Vec3.Read(d, o + 0x20), HalfLength = Chunks.F32(d, o + 0x2C),
            T = Vec3.Read(d, o + 0x30), Radius = Chunks.F32(d, o + 0x3C),
        };

        /// <summary>The local up row: (0,1,0), or cross(row2, row0) when flags & 3 (NeedsCrossProduct).</summary>
        public Vec3 Up => (Flags & 3) == 0 ? new Vec3(0, 1, 0) : new Vec3(
            Row2.Y * Row0.Z - Row2.Z * Row0.Y, Row2.Z * Row0.X - Row2.X * Row0.Z, Row2.X * Row0.Y - Row2.Y * Row0.X);

        /// <summary>CalcPosition (WCollision.cpp:34-47): world_k = dot(local − t, row_k) for rows (row0, up, row2). Sim axes.</summary>
        public Vec3 ToWorldSim(Vec3 l)
        {
            float x = l.X - T.X, y = l.Y - T.Y, z = l.Z - T.Z;
            var up = Up;
            return new Vec3(x * Row0.X + y * Row0.Y + z * Row0.Z, x * up.X + y * up.Y + z * up.Z, x * Row2.X + y * Row2.Y + z * Row2.Z);
        }

        /// <summary>A local direction in world sim axes (no translation).</summary>
        public Vec3 DirToWorldSim(Vec3 l)
        {
            var up = Up;
            return new Vec3(l.X * Row0.X + l.Y * Row0.Y + l.Z * Row0.Z, l.X * up.X + l.Y * up.Y + l.Z * up.Z, l.X * Row2.X + l.Y * Row2.Y + l.Z * Row2.Z);
        }
    }

    /// <summary>
    /// WCollisionObject (0x70; CARP.h:296-306): {posRadius (w = XZ radius), dimensions (half extents), u8 type (0 box,
    /// 1 cylinder), u8 shape, u16 flags (1 dynamic, 0x40 managed), u16 render instance, WSurface, pad, Matrix4} in sim axes.
    /// WCollisionAssets::CreateObject (:255-283) makes the track OBBs: position.y = matrix.y − dim.y, radius = |dim.xz|.
    /// </summary>
    public sealed class CollisionObject
    {
        public Vec3 Position, HalfDims;
        public float Radius;
        public byte Type, Shape, SurfaceFlags, Surface;
        public ushort Flags;
        /// <summary>The matrix rows (right, up, forward) and translation (sim axes).</summary>
        public Vec3 Right = new Vec3(1, 0, 0), Up = new Vec3(0, 1, 0), Forward = new Vec3(0, 0, 1), Translation;
        public bool IsDynamic => (Flags & 1) != 0;

        public static CollisionObject Read(byte[] d, int o) => new CollisionObject
        {
            Position = Vec3.Read(d, o), Radius = Chunks.F32(d, o + 0x0C), HalfDims = Vec3.Read(d, o + 0x10),
            Type = d[o + 0x20], Shape = d[o + 0x21], Flags = Chunks.U16(d, o + 0x22), Surface = d[o + 0x26], SurfaceFlags = d[o + 0x27],
            Right = Vec3.Read(d, o + 0x30), Up = Vec3.Read(d, o + 0x40), Forward = Vec3.Read(d, o + 0x50), Translation = Vec3.Read(d, o + 0x60),
        };

        /// <summary>WCollisionAssets::CreateObject (0x7832xx; :255-283): a managed box from half extents and a matrix.</summary>
        public static CollisionObject Create(Vec3 halfDims, Vec3 right, Vec3 up, Vec3 forward, Vec3 translation, bool dynamic)
        {
            var dim = new Vec3(Math.Abs(halfDims.X), Math.Abs(halfDims.Y), Math.Abs(halfDims.Z));
            return new CollisionObject
            {
                Right = right, Up = up, Forward = forward, Translation = translation, HalfDims = dim,
                Flags = (ushort)(0x40 | (dynamic ? 1 : 0)),
                Position = new Vec3(translation.X, translation.Y - dim.Y, translation.Z),
                Radius = (float)Math.Sqrt(dim.X * dim.X + dim.Z * dim.Z),
            };
        }
    }

    /// <summary>
    /// WCollisionArticle (WCollision.h:90-121): a 0x10 header {u16 strips, u16 strip bytes, u16 edges, u16 edge bytes,
    /// u8 resolved, u8 surfaces, u16 surface bytes, u16 intermediate object [?], i16 flags}, WCollisionStripSphere[strips]
    /// (0x10: centre, u16 radius·16, u16 offset → strip at article + offset + 0x10), the strip vertices, the barriers
    /// (0x20 each) and the simsurface collection keys (u32 each).
    /// </summary>
    public sealed class CollisionArticle
    {
        public readonly List<CollisionStrip> Strips = new List<CollisionStrip>();
        public readonly List<CollisionBarrier> Barriers = new List<CollisionBarrier>();
        public uint[] Surfaces = Array.Empty<uint>();
        public ushort IntermediateObject;
        public short Flags;

        public static CollisionArticle Read(byte[] d, int a, int end)
        {
            var art = new CollisionArticle();
            if (a + 0x10 > end) return art;
            int nStrips = Chunks.U16(d, a), stripsSize = Chunks.U16(d, a + 2), nEdges = Chunks.U16(d, a + 4), edgesSize = Chunks.U16(d, a + 6);
            int nSurf = d[a + 9];
            art.IntermediateObject = Chunks.U16(d, a + 0x0C);
            art.Flags = Chunks.I16(d, a + 0x0E);
            int surfAt = a + 0x10 + stripsSize + edgesSize;
            if (surfAt + nSurf * 4 <= end)
            {
                art.Surfaces = new uint[nSurf];
                for (int i = 0; i < nSurf; i++) art.Surfaces[i] = Chunks.U32(d, surfAt + i * 4);
            }
            for (int s = 0; s < nStrips; s++)
            {
                int sp = a + 0x10 + s * 0x10;
                if (sp + 0x10 > end) break;
                var strip = new CollisionStrip { Centre = Vec3.Read(d, sp), Radius = Chunks.U16(d, sp + 0x0C) / 16f };
                int v = a + Chunks.U16(d, sp + 0x0E) + 0x10;
                if (v + 16 > end) continue;
                // verts[0]'s surface u16 is the vertex count, verts[1]'s the strip flags (WCollision.h:34-88)
                int n = Chunks.U16(d, v + 6);
                strip.Flags = Chunks.U16(d, v + 8 + 6);
                if (v + n * 8 > end) n = (end - v) / 8;
                strip.Verts = new CollisionVert[n];
                for (int i = 0; i < n; i++)
                {
                    int p = v + i * 8;
                    strip.Verts[i] = new CollisionVert
                    {
                        Local = new Vec3(strip.Centre.X + Chunks.I16(d, p) / 128f, strip.Centre.Y + Chunks.I16(d, p + 2) / 128f, strip.Centre.Z + Chunks.I16(d, p + 4) / 128f),
                        Surface = d[p + 6], SurfaceFlags = d[p + 7],
                    };
                }
                art.Strips.Add(strip);
            }
            int b0 = a + 0x10 + stripsSize;
            for (int i = 0; i < nEdges; i++)
            {
                int b = b0 + i * 0x20;   // the data stride is 0x20 (Next()), whatever GetBarrier(ind) multiplies by
                if (b + 0x20 > end) break;
                art.Barriers.Add(new CollisionBarrier
                {
                    P0 = Vec3.Read(d, b), Surface = d[b + 0x0C], SurfaceFlags = d[b + 0x0D],
                    P1 = Vec3.Read(d, b + 0x10), InvXZLength = Chunks.F32(d, b + 0x1C),
                });
            }
            return art;
        }

        /// <summary>The simsurface key of a surface index (0 = none → default).</summary>
        public uint SurfaceKey(int index) => index < Surfaces.Length ? Surfaces[index] : 0;
    }

    /// <summary>A WCollisionPackedVert: instance-local position (the sphere centre added), surface index and flags.</summary>
    public struct CollisionVert
    {
        public Vec3 Local;
        public byte Surface, SurfaceFlags;
    }

    /// <summary>
    /// A triangle strip: triangle i (0..n−3) = (v[i], v[i+1], v[i+2]), with the surface and flags of v[i+2]. Flags: 1 =
    /// kUpFacing, 2 = kFacingUnknown. Surface flags on triangles: 0x04 stacked surface, 0x08 not ground [?], 0x10 two-sided.
    /// </summary>
    public sealed class CollisionStrip
    {
        public Vec3 Centre;
        public float Radius;
        public int Flags;
        public CollisionVert[] Verts = Array.Empty<CollisionVert>();
        public int TriangleCount => Math.Max(0, Verts.Length - 2);
    }

    /// <summary>
    /// WCollisionBarrier (0x20; WCollision.h:123-200): a vertical wall from (x0, z0) to (x1, z1), spanning Y between y0
    /// and y1, normal (dz, 0, −dx)/|d| in instance-local sim axes. One-sided unless surface flag 0x10 (two-sided).
    /// </summary>
    public struct CollisionBarrier
    {
        public Vec3 P0, P1;
        public byte Surface, SurfaceFlags;
        public float InvXZLength;
        public bool TwoSided => (SurfaceFlags & 0x10) != 0;
    }

    /// <summary>
    /// Scenery group numbers (L2RA.BUN 0x34109 records: u32 0x0B, u32 0x0B, u32 bStringHash(name), u16 group number,
    /// u16 count, u32 flags, u16 overrides[count], 4-aligned), as WCollisionInstance.fGroupNumber names them, to the group
    /// name hash SceneryGroupTable and WorldStreamer use (IsSceneryGroupEnabled, WCollisionMgr.cpp:566).
    /// </summary>
    public static class SceneryGroupNumbers
    {
        public static Dictionary<int, uint> Read(byte[] d)
        {
            var map = new Dictionary<int, uint>();
            foreach (var c in Chunks.Read(d, 0, d.Length))
            {
                if (c.Id != 0x34109) continue;
                for (int o = c.DataStart; o + 20 <= c.End;)
                {
                    int n = Chunks.U16(d, o + 14);
                    map[Chunks.U16(d, o + 12)] = Chunks.U32(d, o + 8);
                    o += 20 + ((n * 2 + 3) & ~3);
                }
            }
            return map;
        }
    }

    /// <summary>
    /// BCHUNK_TRACK_OBJECT_BOUNDS 0x34191 (World/Track.hpp:19-31): TrackOBB[0x60] {u32 type hash, pad[3], bMatrix4 (game
    /// axes, rows), bVector3 dims (half extents), pad}, 16-aligned. L2RA has one.
    /// </summary>
    public sealed class TrackObb
    {
        public uint TypeHash;
        public float[] Matrix = new float[16];
        public Vec3 Dims;

        public static List<TrackObb> ReadAll(byte[] d)
        {
            var list = new List<TrackObb>();
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x34191))
            {
                int o = c.DataStart;
                while (o < c.End && d[o] == 0x11 && o - c.DataStart < 16) o++;
                if ((c.End - o) % 0x60 != 0) o = Chunks.Align(c.DataStart, 16);
                for (; o + 0x60 <= c.End; o += 0x60)
                {
                    var b = new TrackObb { TypeHash = Chunks.U32(d, o), Dims = Vec3.Read(d, o + 0x50) };
                    for (int i = 0; i < 16; i++) b.Matrix[i] = Chunks.F32(d, o + 0x10 + i * 4);
                    list.Add(b);
                }
            }
            return list;
        }
    }
}
