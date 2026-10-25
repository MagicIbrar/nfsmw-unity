using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Dynamics
{
    /// <summary>
    /// Collision shapes from the game's CollisionGeometry packs (Physics/Bounds.h; LoaderBounds 0x687240, chunk
    /// 0x8003B900) instead of render-mesh bounds (specs/physics.md §20).
    /// <list type="bullet">
    /// <item><b>Vehicles</b> (GLOBAL/GLOBALB.BUN, Lookup(UCrc32(pvehicle MODEL)), PVehicle::Construct 0x689820): the root
    /// node is the body box (RigidBody dimension = its half extents, Util_GenerateCarTensor 0x6E8F50 =
    /// Box(mass, 2·dim) × TENSOR_SCALE) whose pivot is the body origin in model space (pivot.y = half height: the model
    /// origin is the box bottom). Its children are the collision primitives (RigidBody::AddCollisionPrimitive 0x6A6DA0
    /// via Collection::AddTo 0x672050): boxes and spheres flagged PrimVsWorld (walls) and/or PrimVsObjects (other
    /// bodies), positioned from the root pivot; MeshVsGround nodes name a ground point cloud (CollisionGeometry
    /// PointClouds; one tiny sphere per vertex here, meeting only the road strips), and only those clouds and PrimVsGround
    /// boxes meet the road (the other PrimVsWorld primitives meet barriers and OBBs). E.g. bmwm3gtre46: lower body box 0.934×0.376×2.289 half, cabin box 0.598×0.232×1.308, four corner
    /// spheres r 0.328 (walls only), two mirror spheres r 0.236 (walls only).</item>
    /// <item><b>Smackables</b> (TRACKS/L2RA.BUN, 379 collections): the SmokeableSpawner's Collision key (+0x24) names
    /// the collection (sub_681540 → Collections::Find 0x681340); no collection (6 keys, 126 of 13,484 spawners) → no
    /// smackable is made. The root box is the prop's collision box.</item>
    /// </list>
    /// </summary>
    public static class CollisionBounds
    {
        /// <summary>Off: cars keep the render-bounds box (GameBootstrap.AddBodyCollider) instead of their collision geometry.</summary>
        public static bool UseCollisionGeometry = true;

        static CollisionGeometry _track;

        /// <summary>Reads the track's bounds pack (L2RA.BUN; WorldCollision.BuildTrackObbs hands it over).</summary>
        public static void LoadTrack(byte[] bundle)
        {
            try { _track = bundle != null ? CollisionGeometry.Read(bundle) : null; }
            catch (System.Exception e) { Debug.LogWarning("NFSMW collision bounds: track pack: " + e.Message); _track = null; }
        }

        /// <summary>A vehicle's collection: Lookup(UCrc32(pvehicle MODEL)) in GLOBALB's pack.</summary>
        public static CollisionGeometry.Collection Vehicle(string pvehicle)
        {
            if (string.IsNullOrEmpty(pvehicle)) return null;
            var db = GameData.Attributes;
            var pv = db?.Get("pvehicle", pvehicle.ToLowerInvariant());
            string model = pv?.StringKey("MODEL") ?? pvehicle.ToUpperInvariant();
            var geo = Game.TrafficPhysics.VehicleSpecs.Geometries;
            return geo != null && geo.ByName.TryGetValue(AttribDatabase.Hash(model), out var col) && col.Bounds != null && col.Bounds.Length > 0 ? col : null;
        }

        /// <summary>Sim body/model axes → Unity model axes for scenery (mesh = game (x, z, y)): (s.z, s.y, −s.x).</summary>
        static Vector3 SceneryAxes(Vec3 s) => new Vector3(s.Z, s.Y, -s.X);

        /// <summary>
        /// A smackable's collision box in its Unity model space (the render mesh's frame): the bounds root, centred on its
        /// pivot. False when the track pack has no collection for the key (the exe then makes no smackable).
        /// </summary>
        public static bool SceneryBox(uint collisionKey, out Vector3 centre, out Vector3 size)
        {
            centre = Vector3.zero;
            size = Vector3.zero;
            if (_track == null || !_track.ByName.TryGetValue(collisionKey, out var col) || col.Bounds == null || col.Bounds.Length == 0) return false;
            var r = col.Bounds[0];
            centre = SceneryAxes(r.Pivot);
            var h = SceneryAxes(r.HalfDims);
            size = 2f * new Vector3(Mathf.Abs(h.x), Mathf.Abs(h.y), Mathf.Abs(h.z));
            return size.sqrMagnitude > 0f;
        }

        /// <summary>Has the track's bounds pack been read (so <see cref="HasScenery"/> is meaningful)?</summary>
        public static bool TrackLoaded => _track != null;

        /// <summary>Is there a track collision collection for this key?</summary>
        public static bool HasScenery(uint collisionKey) => _track != null && _track.ByName.ContainsKey(collisionKey);

        /// <summary>The prims built for a car, for CarContacts (world-only / object-only filtering).</summary>
        public struct Prim
        {
            public Collider Collider;
            public bool VsWorld, VsObjects;
            /// <summary>[?] A sphere against the world only meets barriers (walls), never the road: the ground is the
            /// MeshVsGround point cloud / box corners (DoInstanceCollision2d), the spheres go to DoBarrierCollision.</summary>
            public bool WallsOnly;
            /// <summary>A PrimVsWorld primitive without PrimVsGround on a car that has ground primitives: it meets the
            /// barriers and OBBs (DoBarrierCollision :1364-1405, DoObbCollision) but not the road strips — only VSGROUND
            /// boxes (their 8 corners) and MeshVsGround clouds are tested against the road (DoInstanceCollision2d :790-932).</summary>
            public bool NoGround;
            /// <summary>One point of a MeshVsGround cloud (a tiny sphere [ours]): it meets the road strips only.</summary>
            public bool GroundOnly;
        }

        /// <summary>[ours] The radius of the sphere standing in for one ground point-cloud vertex (the exe tests the point
        /// itself against the face under it, WWorldPos with a speed-based tolerance).</summary>
        public const float CloudPointRadius = 0.01f;

        /// <summary>
        /// Builds a car's collision from its collection: the primary BoxCollider (on the car root, which other systems
        /// read) becomes the first PrimVsObjects box (or the root when it is itself a primitive); every other primitive
        /// is a collider under a "CollisionPrims" child of the body model (so it follows the model when another sim
        /// moves it), in car axes from the body origin. Returns the root dimension (half extents) for the inertia.
        /// </summary>
        public static bool BuildVehicle(CollisionGeometry.Collection col, BoxCollider primary, Transform body, List<Prim> prims, out Vector3 halfDims, out Vector3 pivot, out Vector3 primaryOffset, out Transform holder)
        {
            halfDims = Vector3.zero;
            pivot = Vector3.zero;
            primaryOffset = Vector3.zero;
            holder = null;
            prims.Clear();
            if (col == null || primary == null || body == null) return false;
            var root = col.Bounds[0];
            halfDims = new Vector3(root.HalfDims.X, root.HalfDims.Y, root.HalfDims.Z);
            pivot = new Vector3(root.Pivot.X, root.Pivot.Y, root.Pivot.Z);
            // car space (x right, y up, z forward) = sim body space; the model origin is the Body transform's position
            var modelOrigin = primary.transform.InverseTransformPoint(body.position);

            var list = new List<CollisionGeometry.Bound>();
            const CollisionGeometry.BoundFlags Prim = CollisionGeometry.BoundFlags.PrimVsWorld | CollisionGeometry.BoundFlags.PrimVsObjects;
            bool rootIsPrim = (root.Flags & Prim) != 0 && (root.Flags & CollisionGeometry.BoundFlags.Disabled) == 0;
            if (rootIsPrim) list.Add(root);
            foreach (var b in col.ChildrenOf(root))
                if ((b.Flags & Prim) != 0 && (b.Flags & CollisionGeometry.BoundFlags.Disabled) == 0) list.Add(b);
            if (list.Count == 0) return false;
            // the ground primitives: VSGROUND boxes (PrimVsGround) and MeshVsGround point clouds
            var clouds = new List<Vec3[]>();
            foreach (var b in col.Bounds)
                if ((b.Flags & CollisionGeometry.BoundFlags.MeshVsGround) != 0 && (b.Flags & CollisionGeometry.BoundFlags.Disabled) == 0
                    && b.PointCloud >= 0 && b.PointCloud < col.PointClouds.Length && col.PointClouds[b.PointCloud].Length > 0)
                    clouds.Add(col.PointClouds[b.PointCloud]);
            bool hasGround = clouds.Count > 0 || list.Exists(b => (b.Flags & CollisionGeometry.BoundFlags.PrimVsGround) != 0);

            // the primary: the first box that collides with objects
            int first = list.FindIndex(b => (b.Flags & CollisionGeometry.BoundFlags.Box) != 0 && (b.Flags & CollisionGeometry.BoundFlags.PrimVsObjects) != 0);
            if (first < 0) first = list.FindIndex(b => (b.Flags & CollisionGeometry.BoundFlags.Box) != 0);
            if (first < 0) return false;

            var old = body.Find("CollisionPrims");
            if (old) { old.gameObject.SetActive(false); Object.Destroy(old.gameObject); }
            holder = new GameObject("CollisionPrims").transform;
            holder.SetParent(body, false);
            // undo the body model's own turn so the holder is in car axes, origin at the model origin
            holder.rotation = primary.transform.rotation;
            holder.localScale = Vector3.one;
            holder.gameObject.layer = primary.gameObject.layer;

            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                bool isRoot = rootIsPrim && i == 0;
                var centre = pivot + (isRoot ? Vector3.zero : new Vector3(b.Position.X, b.Position.Y, b.Position.Z));
                var half = new Vector3(Mathf.Abs(b.HalfDims.X), Mathf.Abs(b.HalfDims.Y), Mathf.Abs(b.HalfDims.Z));
                var rot = new Quaternion(b.Qx, b.Qy, b.Qz, b.Qw);
                if (isRoot || rot.w == 0f && rot.x == 0f && rot.y == 0f && rot.z == 0f) rot = Quaternion.identity;
                bool vsWorld = (b.Flags & CollisionGeometry.BoundFlags.PrimVsWorld) != 0;
                bool vsObj = (b.Flags & CollisionGeometry.BoundFlags.PrimVsObjects) != 0;
                Collider c;
                if (i == first && Quaternion.Angle(rot, Quaternion.identity) < 0.5f)
                {
                    primary.center = modelOrigin + centre;
                    primary.size = 2f * half;
                    primaryOffset = centre;
                    c = primary;
                }
                else
                {
                    var go = new GameObject(((b.Flags & CollisionGeometry.BoundFlags.Sphere) != 0 ? "Sphere " : "Box ") + i);
                    go.layer = primary.gameObject.layer;
                    go.transform.SetParent(holder, false);
                    go.transform.localPosition = centre;
                    go.transform.localRotation = rot;
                    if ((b.Flags & CollisionGeometry.BoundFlags.Sphere) != 0)
                    {
                        var s = go.AddComponent<SphereCollider>();
                        s.radius = Mathf.Max(half.x, Mathf.Max(half.y, half.z));
                        c = s;
                    }
                    else
                    {
                        var bc = go.AddComponent<BoxCollider>();
                        bc.size = 2f * half;
                        c = bc;
                    }
                    c.sharedMaterial = primary.sharedMaterial;
                }
                bool sphere = (b.Flags & CollisionGeometry.BoundFlags.Sphere) != 0;
                bool vsGround = (b.Flags & CollisionGeometry.BoundFlags.PrimVsGround) != 0;
                // without any ground primitive (6 of 83 collections: the heli and a few semis/trailers) the boxes keep meeting
                // the road [ours: the exe body would only have its wheels there]
                prims.Add(new Prim { Collider = c, VsWorld = vsWorld, VsObjects = vsObj, WallsOnly = sphere && vsWorld && !vsObj, NoGround = hasGround && !vsGround });
            }
            // RigidBody::AddCollisionMesh (Collection::AddTo): each cloud vertex, from the body origin (root pivot)
            int ci = 0;
            foreach (var cloud in clouds)
            {
                var go = new GameObject("GroundCloud " + ci++);
                go.layer = primary.gameObject.layer;
                go.transform.SetParent(holder, false);
                go.transform.localPosition = pivot;
                go.transform.localRotation = Quaternion.identity;
                foreach (var v in cloud)
                {
                    var sc = go.AddComponent<SphereCollider>();
                    sc.center = new Vector3(v.X, v.Y, v.Z);
                    sc.radius = CloudPointRadius;
                    sc.sharedMaterial = primary.sharedMaterial;
                    prims.Add(new Prim { Collider = sc, VsWorld = true, VsObjects = false, GroundOnly = true });
                }
            }
            return true;
        }

        /// <summary>
        /// Puts a car's collision at a body-origin height: the primary box and the prim holder so the primitives sit at
        /// root y = originY + their offsets (the holder hangs under the Body model, which is only yawed, so only y moves).
        /// </summary>
        public static void PlaceAtOrigin(BoxCollider primary, Vector3 primaryOffset, Transform holder, Transform body, float originY)
        {
            if (primary) primary.center = new Vector3(primaryOffset.x, originY + primaryOffset.y, primaryOffset.z);
            if (holder && body) holder.localPosition = new Vector3(0f, originY - body.localPosition.y, 0f);
        }
    }
}
