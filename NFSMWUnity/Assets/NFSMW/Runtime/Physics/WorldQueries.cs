using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Dynamics
{
    /// <summary>
    /// The exe's world collision queries in Unity space, over the WCollisionPacks the streamer has loaded
    /// (Core/WCollisionQueries.cs; specs/traffic_collision.md B.7). These answer from the game's own triangles and
    /// barriers, not from PhysX, so the AI, cameras, resets and spawns see exactly what speed.exe's WCollisionMgr sees:
    /// <list type="bullet">
    /// <item><see cref="Height"/>: GetWorldHeightAtPoint (0x7897C0), the face under a point lifted 0.025 m.</item>
    /// <item><see cref="HeightRigorous"/>: GetWorldHeightAtPointRigorous (0x789870), also the faces above (segment from
    /// 2 m below to 1000 m above).</item>
    /// <item><see cref="CheckHitWorld"/>: CheckHitWorld (0x7854B0), segment vs barriers and ground faces, nearest hit.</item>
    /// <item><see cref="Barriers"/>: GetBarrierList (WCollisionMgr.cpp:1008), walls near a point.</item>
    /// <item><see cref="Pos"/>: WWorldPos, the cached face under a moving point (wheels, bodies, the camera).</item>
    /// </list>
    /// Exclusion masks are the vehicle collision masks (<see cref="MaskFor"/>): grouped barriers (0xC0) only stop
    /// queries made with mask 0. Sim axes = (−unity.z, unity.y, unity.x).
    /// </summary>
    public static class WorldQueries
    {
        static readonly WCollisionQueries World = new WCollisionQueries();
        static readonly Dictionary<CollisionInstance, GameObject> GroupObjects = new Dictionary<CollisionInstance, GameObject>();

        static WorldQueries()
        {
            // IsSceneryGroupEnabled: a grouped instance's colliders are switched with its scenery group (WorldStreamer)
            World.GroupEnabled = inst => !GroupObjects.TryGetValue(inst, out var go) || (go && go.activeInHierarchy);
        }

        /// <summary>Loaded instances (all packs).</summary>
        public static int InstanceCount => World.InstanceCount;

        /// <summary>The Core query object (sim axes) for code that wants the raw WCollisionMgr calls.</summary>
        public static WCollisionQueries Sim => World;

        public static Vec3 ToSim(Vector3 u) => new Vec3(-u.z, u.y, u.x);
        public static Vector3 ToUnity(Vec3 s) => new Vector3(s.Z, s.Y, -s.X);

        /// <summary>RigidBody mCollisionMask from the driver class (PVehicle::LoadBehaviors 0x685240): human, racer and
        /// remote 0; cop 0x80; traffic, none and NIS 0x40.</summary>
        public static uint MaskFor(int driverClass) => driverClass == 0 || driverClass == 3 || driverClass == 6 ? 0u : driverClass == 2 ? 0x80u : 0x40u;

        internal static void Register(CollisionPack pack, IEnumerable<(CollisionInstance inst, GameObject go)> grouped)
        {
            if (grouped != null) foreach (var (inst, go) in grouped) GroupObjects[inst] = go;
            World.Add(pack);
        }

        internal static void Unregister(CollisionPack pack)
        {
            if (pack == null) return;
            World.Remove(pack);
            foreach (var inst in pack.Instances) GroupObjects.Remove(inst);
            // [ours] the colliders' cached lists may hold the pack's instances (the exe frees the pack under them)
            WCollider.InvalidateAllCachedData();
        }

        // ---- world OBBs (WCollisionObjects) ---------------------------------------------------------------------

        static readonly List<CollisionObject> ObjectScratch = new List<CollisionObject>();

        /// <summary>WCollisionAssets::CreateObject for a track OBB given in Unity space (centre, axes, half extents).</summary>
        public static void AddObject(Vector3 centre, Vector3 right, Vector3 up, Vector3 forward, Vector3 halfExtents) =>
            World.AddObject(CollisionObject.Create(new Vec3(halfExtents.x, halfExtents.y, halfExtents.z), ToSim(right), ToSim(up), ToSim(forward), ToSim(centre), false));

        /// <summary>WCollisionAssets::Shutdown's object part (a new track).</summary>
        public static void ClearObjects()
        {
            var tmp = new List<CollisionObject>();
            World.GetObjectList(tmp, new Vec3(0, 0, 0), 1e9f);
            foreach (var o in tmp) World.RemoveObject(o);
        }

        /// <summary>A world OBB in Unity space.</summary>
        public struct Obb
        {
            public Vector3 Centre, Right, Up, Forward, HalfExtents;
        }

        /// <summary>GetObjectList(pt, radius) (WCollisionMgr.cpp:682-711): the box objects whose XZ circle meets (pos, radius).</summary>
        public static void Objects(Vector3 pos, float radius, List<Obb> result)
        {
            result.Clear();
            World.GetObjectList(ObjectScratch, ToSim(pos), radius);
            foreach (var o in ObjectScratch)
                result.Add(new Obb
                {
                    Centre = ToUnity(o.Translation), Right = ToUnity(o.Right), Up = ToUnity(o.Up), Forward = ToUnity(o.Forward),
                    HalfExtents = new Vector3(o.HalfDims.X, o.HalfDims.Y, o.HalfDims.Z),
                });
        }

        /// <summary>
        /// WCollider in Unity space: a body's cached region (instances + faces, barriers, OBBs), refreshed only when the
        /// requested sphere leaves it (WCollider::Refresh, predictive sizing on). <see cref="Pos.Update(Vector3, Collider, out Vector3, out float, bool, bool)"/>
        /// searches its face list as RigidBody's / the suspension's WWorldPos do.
        /// </summary>
        public sealed class Collider
        {
            internal readonly WCollider C;
            public Collider(uint typeMask = WCollider.TypeAll, uint exclusionMask = 0, bool cylinder = false)
            {
                C = WCollider.Create(cylinder ? WCollider.ColliderShape.Cylinder : WCollider.ColliderShape.Sphere, typeMask, exclusionMask);
            }
            public void Refresh(Vector3 pos, float radius, bool predictive = true) => C.Refresh(World, ToSim(pos), radius, predictive);
            public bool RegionValid => C.Validate();
            public int FaceCount => C.Tris.Count;
            public int BarrierCount => C.Barriers.Count;
            public void Clear() => C.Clear();
            public void Destroy() => WCollider.Destroy(C);
        }

        /// <summary>A ground face (WCollisionTri) in Unity space.</summary>
        public struct Face
        {
            public Vector3 P0, P1, P2, Normal;
            public uint SurfaceKey;
            public byte SurfaceFlags;
        }

        /// <summary>A WorldCollisionInfo in Unity space: Type 1 ground face, 2 barrier.</summary>
        public struct Hit
        {
            public int Type;
            public Vector3 Point, Normal;
            public float Distance;
            public uint SurfaceKey;
            public byte SurfaceFlags;
            public bool TwoSided => (SurfaceFlags & 0x10) != 0;
            public bool IsBarrier => Type == 2;
        }

        /// <summary>GetWorldHeightAtPoint: the height of the face under pos (searching from 0.025 m above it).</summary>
        public static bool Height(Vector3 pos, out float height, out Vector3 normal, out WorldCollision.Surface surface)
        {
            bool ok = World.GetWorldHeightAtPoint(ToSim(pos), out height, out var n, out uint key);
            normal = ok ? ToUnity(n) : Vector3.up;
            surface = ok ? WorldCollision.SurfaceOf(key) : WorldCollision.Surface.Null;
            return ok;
        }

        public static bool Height(Vector3 pos, out float height) => Height(pos, out height, out _, out _);

        /// <summary>GetWorldHeightAtPointRigorous: the face under pos, else the first face above it (up to 1000 m).</summary>
        public static bool HeightRigorous(Vector3 pos, out float height, out Vector3 normal)
        {
            bool ok = World.GetWorldHeightAtPointRigorous(ToSim(pos), out height, out var n);
            normal = ok ? ToUnity(n) : Vector3.up;
            return ok;
        }

        /// <summary>GetWorldNormal over a collider's instances and barriers along from → to (WCollisionMgr.cpp:483-522).</summary>
        public static bool GetWorldNormal(Collider collider, Vector3 from, Vector3 to, out Hit hit)
        {
            hit = default;
            if (collider == null) return false;
            bool ok = World.GetWorldNormal(collider.C.Instances, collider.C.Barriers, ToSim(from), ToSim(to), out var h, collider.C.ExclusionFlags);
            if (!ok) return false;
            hit = new Hit { Type = h.Type, Point = ToUnity(h.Point), Normal = ToUnity(h.Normal), Distance = h.Distance, SurfaceKey = h.Surface, SurfaceFlags = h.SurfaceFlags };
            return true;
        }

        /// <summary>CheckHitWorld(seg, info, primMask): primMask 1 = ground faces, 2 = barriers, 3 = both.</summary>
        public static bool CheckHitWorld(Vector3 from, Vector3 to, out Hit hit, uint primMask = 3, uint exclusionMask = 0)
        {
            bool ok = World.CheckHitWorld(ToSim(from), ToSim(to), out var h, primMask, exclusionMask);
            hit = default;
            if (!ok) return false;
            hit = new Hit
            {
                Type = h.Type, Point = ToUnity(h.Point), Normal = ToUnity(h.Normal), Distance = h.Distance,
                SurfaceKey = h.Surface, SurfaceFlags = h.SurfaceFlags,
            };
            return true;
        }

        /// <summary>A wall near a point (WCollisionBarrierListEntry), Unity space.</summary>
        public struct Barrier
        {
            public Vector3 P0, P1, Normal;
            public uint SurfaceKey;
            public byte SurfaceFlags;
            public float DistanceXZ;
            public bool TwoSided => (SurfaceFlags & 0x10) != 0;
        }

        static readonly List<WCollisionQueries.BarrierEntry> BarrierScratch = new List<WCollisionQueries.BarrierEntry>();

        /// <summary>GetBarrierList(pos, radius): barriers within radius (XZ) overlapping [y ± radius], nearest first.</summary>
        public static void Barriers(Vector3 pos, float radius, List<Barrier> result, uint exclusionMask = 0)
        {
            result.Clear();
            World.GetBarrierList(BarrierScratch, ToSim(pos), radius, exclusionMask);
            foreach (var b in BarrierScratch)
                result.Add(new Barrier
                {
                    P0 = ToUnity(b.P0), P1 = ToUnity(b.P1), Normal = ToUnity(b.Normal), SurfaceKey = b.Surface,
                    SurfaceFlags = b.SurfaceFlags, DistanceXZ = Mathf.Sqrt(b.DistSq),
                });
        }

        /// <summary>
        /// WWorldPos (0.025 m lift by default): the cached face under a moving point. <see cref="Update"/> is
        /// WWorldPos::Update (0x789CC0): the plane normal and the signed distance from the point to the face plane;
        /// Surface is kNull while no face is found.
        /// </summary>
        public sealed class Pos
        {
            readonly WWorldPos _p;
            public Pos(float yOffset = 0.025f) { _p = new WWorldPos(yOffset); }

            public bool OnValidFace => _p.OnValidFace;
            /// <summary>fSurface == SimSurface::kNull (no face found by the last Update).</summary>
            public bool SurfaceIsNull => _p.SurfaceNull;
            public WorldCollision.Surface Surface => _p.SurfaceNull ? WorldCollision.Surface.Null : WorldCollision.SurfaceOf(_p.Face.SurfaceKey);
            public Vector3 Normal => ToUnity(_p.UNormal());
            public Face Face => new Face
            {
                P0 = ToUnity(_p.Face.P0), P1 = ToUnity(_p.Face.P1), P2 = ToUnity(_p.Face.P2), Normal = ToUnity(_p.UNormal()),
                SurfaceKey = _p.Face.SurfaceKey, SurfaceFlags = _p.Face.Flags,
            };

            /// <summary>FindClosestFace(pt, quitIfOnSameFace). True when it searched (the point left the old face).</summary>
            public bool FindClosestFace(Vector3 pt, bool quitIfOnSameFace = true, uint mask = 0) => _p.FindClosestFace(World, ToSim(pt), quitIfOnSameFace, mask);

            /// <summary>Update(pos, dest, usecache, collider, keep_valid): normal and plane distance; false without a face.</summary>
            public bool Update(Vector3 pos, out Vector3 normal, out float planeDistance, bool useCache = true, bool keepValid = false)
            {
                bool ok = _p.Update(World, ToSim(pos), out var n, out planeDistance, useCache, keepValid);
                normal = ToUnity(n);
                return ok;
            }

            /// <summary>Update with a WCollider: the face searched in the collider's cached face list (WWorldPos.cpp:11-16, 100-152).</summary>
            public bool Update(Vector3 pos, Collider collider, out Vector3 normal, out float planeDistance, bool useCache = true, bool keepValid = false)
            {
                bool ok = _p.Update(World, collider?.C, ToSim(pos), out var n, out planeDistance, useCache, keepValid);
                normal = ToUnity(n);
                return ok;
            }

            /// <summary>SetTolerance: the lift above the point the face search starts from.</summary>
            public void SetTolerance(float lift) => _p.SetTolerance(lift);

            /// <summary>HeightAtPoint: the face plane's height at pt (0 without a face).</summary>
            public float HeightAtPoint(Vector3 pt) => _p.HeightAtPoint(ToSim(pt));

            public void Invalidate() => _p.Invalidate();
        }
    }
}
