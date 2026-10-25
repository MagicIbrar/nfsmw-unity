using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// WCollider (World/Common/WCollider.cpp, exe 0x771B80-0x78A003): a body's cached collision region — the instances
    /// near it, their faces (the tri list), the barriers and the world OBBs — refreshed only when the requested sphere
    /// leaves the cached one. RigidBody keeps one (mWCollider) and its WWorldPos searches use the tri list
    /// (WWorldPos::FindClosestFace(collider, ...)). Sim axes.
    /// <list type="bullet">
    /// <item>Type mask bits: 4 barriers, 8 instances + tri list, 0x10 objects (OBBs; refreshed every call).</item>
    /// <item>Refresh (:116-155): nothing loaded → empty lists; else GetUpdateMask (objects always, instances/barriers when
    /// not Validate or not InRegion) → empty, then (all types) the region = the request ×1.1, or predictive: moving under
    /// 5 m since the last refresh → centre pushed ahead by 2.5 × that step, radius = request + push + 0.1 (≤ 25 m);
    /// PrepareRegion fills the lists.</item>
    /// <item>InRegion (:265-272): the request sphere inside the cached one; InvalidateIntersectingColliders (:76-84) /
    /// InvalidateAllCachedData (:274-282) clear the colliders that a changed area touches (scenery groups switching,
    /// WCollisionAssets::SetExclusionFlags).</item>
    /// </list>
    /// </summary>
    public sealed class WCollider
    {
        public enum ColliderShape { Sphere = 0, Cylinder = 1 }
        public const uint TypeBarriers = 4, TypeInstances = 8, TypeObjects = 0x10, TypeAll = 0x1C;

        static readonly List<WCollider> All = new List<WCollider>();

        public readonly ColliderShape Shape;
        public readonly uint TypeMask, ExclusionFlags;
        public Vec3 Position;
        public float Radius;
        public bool RegionInitialized { get; private set; }
        Vec3 _requestedPosition, _lastRequestedPosition, _lastRefreshedPosition;
        float _requestedRadius, _lastRequestedRadius;

        public readonly List<WCollisionQueries.Entry> Instances = new List<WCollisionQueries.Entry>();
        public readonly List<WCollisionQueries.Tri> Tris = new List<WCollisionQueries.Tri>();
        public readonly List<WCollisionQueries.BarrierEntry> Barriers = new List<WCollisionQueries.BarrierEntry>();
        public readonly List<CollisionObject> Objects = new List<CollisionObject>();

        WCollider(ColliderShape shape, uint typeMask, uint exclusionMask)
        {
            Shape = shape;
            TypeMask = typeMask;
            ExclusionFlags = exclusionMask;
        }

        /// <summary>WCollider::Create (:49-63): registered in the global list.</summary>
        public static WCollider Create(ColliderShape shape, uint typeCheckMask, uint exclusionMask)
        {
            var c = new WCollider(shape, typeCheckMask, exclusionMask);
            lock (All) All.Add(c);
            return c;
        }

        /// <summary>WCollider::Destroy (:65-74).</summary>
        public static void Destroy(WCollider c)
        {
            if (c == null) return;
            lock (All) All.Remove(c);
        }

        /// <summary>InvalidateIntersectingColliders: clears every collider whose region meets the sphere.</summary>
        public static void InvalidateIntersectingColliders(Vec3 pos, float radius)
        {
            lock (All)
                foreach (var c in All)
                {
                    float dx = c.Position.X - pos.X, dy = c.Position.Y - pos.Y, dz = c.Position.Z - pos.Z;
                    if ((float)Math.Sqrt(dx * dx + dy * dy + dz * dz) < radius + c.Radius) c.Clear();
                }
        }

        /// <summary>InvalidateAllCachedData: every collider cleared (a pack loaded/unloaded, exclusion flags changed).</summary>
        public static void InvalidateAllCachedData()
        {
            lock (All) foreach (var c in All) { c.Clear(); c.RegionInitialized = false; }
        }

        /// <summary>Refresh(pt, radius, predictiveSizing) (:116-155).</summary>
        public void Refresh(WCollisionQueries w, Vec3 pt, float radius, bool predictiveSizing)
        {
            if (w == null || w.InstanceCount == 0) { EmptyLists(TypeAll); return; }   // !WWorld::IsValid
            uint updateMask = GetUpdateMask(pt, radius);
            if (updateMask != 0)
            {
                EmptyLists(updateMask);
                if (updateMask == TypeMask)
                {
                    _requestedPosition = pt;
                    _requestedRadius = radius;
                    if (predictiveSizing) CalcNewRegionSize(pt, radius, out Position, out Radius);
                    else { Position = pt; Radius = radius * 1.1f; }
                }
                PrepareRegion(w, updateMask);
            }
            if (predictiveSizing)
            {
                _lastRefreshedPosition = pt;
                _lastRequestedPosition = pt;
                _lastRequestedRadius = radius;
            }
        }

        /// <summary>CalcNewRegionSizeFromRequested (:86-112).</summary>
        void CalcNewRegionSize(Vec3 reqPos, float reqRad, out Vec3 pos, out float rad)
        {
            if (!RegionInitialized) { pos = reqPos; rad = reqRad * 1.1f; return; }
            float vel = Dist(reqPos, _lastRefreshedPosition);
            if (vel > 5f) { pos = reqPos; rad = reqRad * 1.1f; return; }
            // moveVec = unit(req − the last requested position) × vel × 2.5 (lifeFactor)
            var mv = new Vec3(reqPos.X - _lastRequestedPosition.X, reqPos.Y - _lastRequestedPosition.Y, reqPos.Z - _lastRequestedPosition.Z);
            float l = (float)Math.Sqrt(mv.X * mv.X + mv.Y * mv.Y + mv.Z * mv.Z);
            float k = l > 0f ? vel * 2.5f / l : 0f;
            mv = new Vec3(mv.X * k, mv.Y * k, mv.Z * k);
            pos = new Vec3(reqPos.X + mv.X, reqPos.Y + mv.Y, reqPos.Z + mv.Z);
            rad = Math.Min(reqRad + (float)Math.Sqrt(mv.X * mv.X + mv.Y * mv.Y + mv.Z * mv.Z) + 0.1f, 25f);
        }

        /// <summary>PrepareRegion (:157-190): instances (+ tri list), barriers, objects.</summary>
        void PrepareRegion(WCollisionQueries w, uint updateMask)
        {
            if ((updateMask & 0xC) != 0)
            {
                w.GetInstanceList(Instances, Position, Radius, ExclusionFlags);
                if ((updateMask & TypeInstances) != 0) w.GetTriList(Instances, Position, Radius, Tris);
            }
            if ((updateMask & TypeBarriers) != 0) w.GetBarrierList(Barriers, Instances, Position, Radius, ExclusionFlags);
            if ((updateMask & TypeObjects) != 0) w.GetObjectList(Objects, Position, Radius);
            RegionInitialized = true;
        }

        public bool IsEmpty => Instances.Count == 0 && Barriers.Count == 0;

        /// <summary>Clear (:197-202).</summary>
        public void Clear()
        {
            if (!RegionInitialized) return;
            EmptyLists(TypeAll);
            RegionInitialized = false;
        }

        void EmptyLists(uint typeMask)
        {
            if ((typeMask & TypeInstances) != 0) { Instances.Clear(); Tris.Clear(); }
            if ((typeMask & TypeBarriers) != 0) Barriers.Clear();
            if ((typeMask & TypeObjects) != 0) Objects.Clear();
        }

        public bool Validate() => RegionInitialized;

        /// <summary>GetUpdateMask (:257-263).</summary>
        public uint GetUpdateMask(Vec3 pt, float radius)
        {
            uint m = TypeMask & TypeObjects;
            if (!Validate() || !InRegion(pt, radius)) m |= TypeMask & 0xC;
            return m;
        }

        /// <summary>InRegion (:265-272).</summary>
        public bool InRegion(Vec3 pt, float radius)
        {
            float d = Radius - radius;
            if (d < 0f) return false;
            float dx = pt.X - Position.X, dy = pt.Y - Position.Y, dz = pt.Z - Position.Z;
            return dx * dx + dy * dy + dz * dz < d * d;
        }

        static float Dist(Vec3 a, Vec3 b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }
}
