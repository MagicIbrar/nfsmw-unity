using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// CollisionGeometry bounds packs (Physics/Bounds.h; speed.exe LoaderBounds 0x687240 takes chunk 0x8003B900,
    /// BoundsPack ctor 0x6870E0). GLOBAL/GLOBALB.BUN holds the vehicles' pack: 83 children 0x3B901, each padded with
    /// 0x11 to 16, then BoundsHeader {UCrc32 name, i32 count, i32 resolved, pad} and count × Bounds[0x30]:
    /// <code>
    /// +0x00 i16 quat x,y,z,w (/32767)   +0x08 i16 position x,y,z (mm)   +0x0E u16 flags (BoundFlags)
    /// +0x10 i16 half dims x,y,z (mm)    +0x16 u8 children   +0x17 i8 point cloud   +0x18 i16 pivot (mm)
    /// +0x1E i16 first child index        +0x20 f32 radius   +0x24 UCrc32 surface   +0x28 UCrc32 name   +0x2C ptr
    /// </code>
    /// Vectors are in the sim's body frame (x right, y up, z forward). A collection is looked up by the vehicle's MODEL
    /// StringKey hash (CollisionGeometry::Lookup(UCrc32(MODEL)), RBTractor.cpp:105) and its nodes by UCrc32(name).
    /// </summary>
    public sealed class CollisionGeometry
    {
        [Flags]
        public enum BoundFlags : ushort
        {
            Disabled = 1, PrimVsWorld = 2, PrimVsObjects = 4, PrimVsGround = 8, MeshVsGround = 0x10, Internal = 0x20,
            Box = 0x40, Sphere = 0x80, ConstraintConical = 0x100, ConstraintPrismatic = 0x200, JointFemale = 0x400,
            JointMale = 0x800, MalePost = 0x1000, JointInvert = 0x2000,
        }

        public struct Bound
        {
            public float Qx, Qy, Qz, Qw;
            public Vec3 Position, HalfDims, Pivot;
            public BoundFlags Flags;
            public int Children, ChildIndex, PointCloud;
            public float Radius;
            public uint Surface, Name;
        }

        public sealed class Collection
        {
            public uint Name;
            public Bound[] Bounds;
            /// <summary>The point clouds after the bounds (PCloudHeader {i32 count, pad[3]}, count × PCloud {i32 verts,
            /// pad[2], Vector4* list}, then each cloud's Vector4 vertices in turn), in the sim body frame from the body
            /// origin (root pivot). A MeshVsGround node's PointCloud index names one: Collection::AddTo hands it to
            /// RigidBody::AddCollisionMesh, and DoInstanceCollision2d (RigidBody.cpp:875-910) tests each vertex against the
            /// road. E.g. BMWM3GTRE46: one cloud of 16 points (x ±0.92, y −0.56..0.59, z −2.28..2.19).</summary>
            public Vec3[][] PointClouds = Array.Empty<Vec3[]>();

            /// <summary>Collection::GetBounds: the node with that name hash (any depth).</summary>
            public bool Find(uint name, out Bound b)
            {
                foreach (var x in Bounds) if (x.Name == name) { b = x; return true; }
                b = default;
                return false;
            }

            public IEnumerable<Bound> ChildrenOf(Bound parent)
            {
                for (int i = 0; i < parent.Children; i++)
                {
                    int k = parent.ChildIndex + i;
                    if (parent.ChildIndex >= 0 && k < Bounds.Length) yield return Bounds[k];
                }
            }
        }

        public const uint ChunkPack = 0x8003B900, ChunkCollection = 0x3B901;
        public readonly Dictionary<uint, Collection> ByName = new Dictionary<uint, Collection>();

        public static CollisionGeometry Read(byte[] d)
        {
            var g = new CollisionGeometry();
            foreach (var pack in Chunks.FindAll(d, 0, d.Length, ChunkPack))
                foreach (var c in Chunks.Children(d, pack))
                {
                    if (c.Id != ChunkCollection) continue;
                    int o = c.DataStart;
                    while (o < c.End && d[o] == 0x11) o++;
                    if (o + 16 > c.End) continue;
                    var col = new Collection { Name = Chunks.U32(d, o) };
                    int n = Chunks.I32(d, o + 4);
                    if (n < 0 || o + 16 + n * 0x30 > c.End) continue;
                    col.Bounds = new Bound[n];
                    for (int i = 0; i < n; i++)
                    {
                        int b = o + 16 + i * 0x30;
                        col.Bounds[i] = new Bound
                        {
                            Qx = Chunks.I16(d, b) / 32767f, Qy = Chunks.I16(d, b + 2) / 32767f, Qz = Chunks.I16(d, b + 4) / 32767f, Qw = Chunks.I16(d, b + 6) / 32767f,
                            Position = V3c(d, b + 8),
                            Flags = (BoundFlags)Chunks.U16(d, b + 0xE),
                            HalfDims = V3c(d, b + 0x10),
                            Children = d[b + 0x16],
                            PointCloud = (sbyte)d[b + 0x17],
                            Pivot = V3c(d, b + 0x18),
                            ChildIndex = Chunks.I16(d, b + 0x1E),
                            Radius = Chunks.F32(d, b + 0x20),
                            Surface = Chunks.U32(d, b + 0x24),
                            Name = Chunks.U32(d, b + 0x28),
                        };
                    }
                    ReadPointClouds(d, o + 16 + n * 0x30, c.End, col);
                    g.ByName[col.Name] = col;
                }
            return g;
        }

        static void ReadPointClouds(byte[] d, int o, int end, Collection col)
        {
            end = Math.Min(end, d.Length);
            if (o < 0 || o + 16 > end) return;
            int n = Chunks.I32(d, o);
            if (n <= 0 || n > 64 || o + 16 + n * 16 > end) return;
            var clouds = new Vec3[n][];
            int v = o + 16 + n * 16;
            for (int i = 0; i < n; i++)
            {
                int count = Chunks.I32(d, o + 16 + i * 16);
                if (count < 0 || count > 4096 || v + count * 16 > end) return;
                var pts = new Vec3[count];
                for (int k = 0; k < count; k++, v += 16) pts[k] = new Vec3(Chunks.F32(d, v), Chunks.F32(d, v + 4), Chunks.F32(d, v + 8));
                clouds[i] = pts;
            }
            col.PointClouds = clouds;
        }

        static Vec3 V3c(byte[] d, int o) => new Vec3(Chunks.I16(d, o) / 1000f, Chunks.I16(d, o + 2) / 1000f, Chunks.I16(d, o + 4) / 1000f);

        /// <summary>UCrc32(const char*) = stringhash, which in this build is Attrib's Jenkins string hash [?: matched
        /// against the pack's collection names by the harness probe `trafphys bounds`].</summary>
        public static uint NameHash(string s) => AttribDatabase.Hash(s);
    }
}
