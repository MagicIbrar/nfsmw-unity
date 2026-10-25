using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// The game's world collision (WCollisionPack, chunk 0x3B801; specs/traffic_collision.md part B) as Unity colliders.
    /// The triangle strips are the ground: one non-convex MeshCollider per pack (plus one per scenery-group instance),
    /// every triangle turned to face up as WCollisionMgr does (WCollisionMgr.cpp:227-231), steep and two-sided ones
    /// also emitted reversed. The barriers are the walls: a thin prism per barrier whose front face lies on the barrier
    /// plane, extending 1 m behind it (a one-sided wall, WCollisionMgr.cpp:744-782), or centred on it when the surface
    /// says two-sided (flag 0x10). They go into one MeshCollider per pack rather than a BoxCollider each (thousands of
    /// GameObjects otherwise). Each ground triangle keeps its simsurface key, read back from RaycastHit.triangleIndex by
    /// <see cref="SurfaceAt"/> for the wheels (WWorldPos.cpp:211 → SuspensionRacer.cpp:788-805).
    /// </summary>
    public static class WorldCollision
    {
        /// <summary>A simsurface: the grip and rolling-resistance factors the tyres use (attributes.bin).</summary>
        public struct Surface
        {
            public uint Key;
            public string Name;
            public float DriveGrip, LateralGrip, RollingResistance;
            /// <summary>Triangle flag 0x08: skipped by the game's ground search (FindFaceInTriStrip) [?].</summary>
            public bool NotGround;
            /// <summary>False for SimSurface::kNull (no world face found: WWorldPos::Update, PVehicle off-world test).</summary>
            public bool Valid;
            public static readonly Surface Default = new Surface { Name = "default", DriveGrip = 1, LateralGrip = 1, RollingResistance = 1, Valid = true };
            /// <summary>SimSurface::kNull: the "null" simsurface collection (0x90A4D09A, SimSurface::InitSystem 0x6E9790),
            /// what a wheel or body has while no world face is under it. Grip 1 like every non-terrain surface.</summary>
            public static readonly Surface Null = new Surface { Name = "null", DriveGrip = 1, LateralGrip = 1, RollingResistance = 1, Valid = false };
        }

        sealed class MeshInfo { public uint[] Keys; public byte[] Flags; }

        static readonly Dictionary<int, MeshInfo> Meshes = new Dictionary<int, MeshInfo>();
        static readonly Dictionary<uint, Surface> Surfaces = new Dictionary<uint, Surface>();
        static int _wallLayer = -2;

        /// <summary>Wall thickness behind a barrier plane (m).</summary>
        public const float WallThickness = 1f;

        /// <summary>The "WorldWall" layer when the project defines one, else Default.</summary>
        public static int WallLayer
        {
            get
            {
                if (_wallLayer == -2) _wallLayer = Mathf.Max(0, LayerMask.NameToLayer("WorldWall"));
                return _wallLayer;
            }
        }

        /// <summary>Is this collider world collision (ground or walls built here)?</summary>
        public static bool IsWorld(Collider c) => c != null && Meshes.ContainsKey(c.GetInstanceID());

        /// <summary>Is this collider the world's ground (triangle strips)?</summary>
        public static bool IsGround(Collider c) => c != null && Meshes.TryGetValue(c.GetInstanceID(), out var m) && m.Keys != null;

        /// <summary>
        /// The simsurface under a raycast hit: the triangle's key for the world ground, the default surface for
        /// anything else (walls, props, cars, render meshes).
        /// </summary>
        public static Surface SurfaceAt(in RaycastHit hit)
        {
            var c = hit.collider;
            if (c == null || !Meshes.TryGetValue(c.GetInstanceID(), out var m) || m.Keys == null) return Surface.Default;
            int t = hit.triangleIndex;
            if (t < 0 || t >= m.Keys.Length) return Surface.Default;
            var s = SurfaceOf(m.Keys[t]);
            if ((m.Flags[t] & 0x08) != 0) s.NotGround = true;
            return s;
        }

        /// <summary>
        /// A simsurface collection by key (WCollisionArticle::Resolve → SimSurface::Lookup 0x6E95E0, WCollisionPack.cpp:60-70).
        /// A key with no collection (key 0 and the unknown 0x33199393, 0x4BB72BD3) resolves to the "unknown" collection
        /// (SimSurface::mUnknown, SimSurface.cpp:11-17); its grip values equal default's (all 1).
        /// </summary>
        public static Surface SurfaceOf(uint key)
        {
            if (Surfaces.TryGetValue(key, out var s)) return s;
            s = Surface.Default;
            s.Key = key;
            var db = GameData.Attributes;
            AttribCollection c = null;
            if (db != null && !db.Collections.TryGetValue((AttribDatabase.Hash("simsurface"), key), out c))
                if (!db.Collections.TryGetValue((AttribDatabase.Hash("simsurface"), AttribDatabase.Hash("unknown")), out c))
                    db.Collections.TryGetValue((AttribDatabase.Hash("simsurface"), AttribDatabase.Hash("default")), out c);
            if (c != null)
            {
                s.Name = c.Name ?? key.ToString("X8");
                s.DriveGrip = c.Float("DRIVE_GRIP", 0, 1f);
                s.LateralGrip = c.Float("LATERAL_GRIP", 0, 1f);
                s.RollingResistance = c.Float("ROLLING_RESISTANCE", 0, 1f);
            }
            Surfaces[key] = s;
            return s;
        }

        static void Register(Collider c, MeshInfo info, uint exclusion = 0, int kind = 0)
        {
            Meshes[c.GetInstanceID()] = info;
            lock (Kinds) Kinds[c.GetInstanceID()] = kind != 0 ? kind : info.Keys != null ? 1 : 2;
            var reg = c.gameObject.AddComponent<Registration>();
            reg.Id = c.GetInstanceID();
            if (exclusion != 0)
            {
                reg.Exclusion = true;
                lock (Excluded) Excluded[c.GetInstanceID()] = exclusion;
            }
        }

        /// <summary>Forgets a collider's triangle table when it is destroyed (section unload).</summary>
        sealed class Registration : MonoBehaviour
        {
            public int Id;
            public bool Exclusion;
            void OnDestroy()
            {
                Meshes.Remove(Id);
                lock (Kinds) Kinds.Remove(Id);
                lock (Walls) Walls.Remove(Id);
                if (Exclusion) lock (Excluded) Excluded.Remove(Id);
            }
        }

        /// <summary>Unregisters a pack from the world queries when its collision root goes (section unload).</summary>
        sealed class PackRegistration : MonoBehaviour
        {
            public CollisionPack Pack;
            void OnDestroy() => Dynamics.WorldQueries.Unregister(Pack);
        }

        static readonly Dictionary<int, uint> Excluded = new Dictionary<int, uint>();
        static readonly Dictionary<int, int> Kinds = new Dictionary<int, int>();
        static readonly Dictionary<int, WallInfo> Walls = new Dictionary<int, WallInfo>();

        /// <summary>The barriers behind one walls collider (Unity space), on a 16 m XZ grid, for the side test.</summary>
        sealed class WallInfo
        {
            const float Cell = 16f;
            readonly List<(Vector3 a, Vector3 d, float len, Vector3 n, float y0, float y1, bool two)> _bars = new List<(Vector3, Vector3, float, Vector3, float, float, bool)>();
            readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();

            static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

            public void Add(Vector3 a, Vector3 d, float len, Vector3 n, float y0, float y1, bool two)
            {
                int idx = _bars.Count;
                _bars.Add((a, d, len, n, y0, y1, two));
                var b = a + d * len;
                int x0 = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - WallThickness) / Cell), x1 = Mathf.FloorToInt((Mathf.Max(a.x, b.x) + WallThickness) / Cell);
                int z0 = Mathf.FloorToInt((Mathf.Min(a.z, b.z) - WallThickness) / Cell), z1 = Mathf.FloorToInt((Mathf.Max(a.z, b.z) + WallThickness) / Cell);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!_grid.TryGetValue(Key(x, z), out var l)) _grid[Key(x, z)] = l = new List<int>();
                        l.Add(idx);
                    }
            }

            /// <summary>The barrier whose wall prism holds the point (nearest plane first); false when none does.</summary>
            public bool Find(Vector3 p, out Vector3 n, out bool two)
            {
                n = Vector3.zero; two = false;
                if (!_grid.TryGetValue(Key(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell)), out var l)) return false;
                const float eps = 0.05f;
                float best = float.MaxValue;
                foreach (int i in l)
                {
                    var b = _bars[i];
                    var r = p - b.a;
                    float t = Vector3.Dot(r, b.d), dist = Vector3.Dot(r, b.n);
                    float back = b.two ? WallThickness * 0.5f : WallThickness, front = b.two ? WallThickness * 0.5f : 0f;
                    if (t < -eps || t > b.len + eps || dist < -back - eps || dist > front + eps || p.y < b.y0 - eps || p.y > b.y1 + eps) continue;
                    if (Mathf.Abs(dist) < best) { best = Mathf.Abs(dist); n = b.n; two = b.two; }
                }
                return best < float.MaxValue;
            }
        }

        /// <summary>
        /// WCollisionMgr::Collide's side rule (WCollisionMgr.cpp:776-780): a barrier contact counts only when the collision
        /// normal (out of the barrier toward the body) has a positive dot with the barrier's normal — for one-sided barriers
        /// (WSurface flag 0x10 clear) and, forced, for primitives flagged ONESIDED (every sphere: RigidBody::AddCollisionSphere
        /// ORs 0x10; DoBarrierCollision :1399). So a body behind a one-sided barrier passes through it, as does a sphere
        /// behind a two-sided one. The Unity wall is a prism (1 m thick behind a one-sided plane, centred on a two-sided one);
        /// this finds the barrier holding the contact point. Any thread. True (keep) when the point is in no barrier's prism.
        /// </summary>
        public static bool BarrierContactCounts(int colliderInstanceId, Vector3 point, Vector3 normalTowardBody, bool forceSingleSided)
        {
            WallInfo w;
            lock (Walls) if (!Walls.TryGetValue(colliderInstanceId, out w)) return true;
            if (!w.Find(point, out var n, out bool two)) return true;
            if (two && !forceSingleSided) return true;
            return Vector3.Dot(n, normalTowardBody) > 0f;
        }

        /// <summary>What a world collider is, from any thread: 0 not world collision, 1 ground (strips), 2 wall (barriers),
        /// 3 a world OBB (TrackOBB: RigidBody::DoObbCollision 0x6B... takes OBJ_FRICTION, RigidBody.cpp:1168-1225).</summary>
        public static int KindOf(int colliderInstanceId)
        {
            lock (Kinds) return Kinds.TryGetValue(colliderInstanceId, out int k) ? k : 0;
        }

        /// <summary>
        /// The WSurface exclusion bits of a world collider (any thread): 0xC0 for the walls of an instance with a scenery
        /// group number (WCollisionAssets::SetExclusionFlags 0x76C6A0). A body whose collision mask shares a bit
        /// (cops 0x80, traffic 0x40) passes through them (WCollisionMgr::SurfacePassesExclusion).
        /// </summary>
        public static uint ExclusionOf(int colliderInstanceId)
        {
            lock (Excluded) return Excluded.TryGetValue(colliderInstanceId, out uint f) ? f : 0u;
        }

        static Vector3 ToUnity(Vec3 sim) => new Vector3(sim.Z, sim.Y, -sim.X);

        /// <summary>
        /// Colliders for one pack under parent. Instances with a scenery group number become their own objects, listed in
        /// grouped with their group number for the caller to switch (IsSceneryGroupEnabled, WCollisionMgr.cpp:566).
        /// Meshes are added to owned.
        /// </summary>
        public static GameObject Build(CollisionPack pack, Transform parent, List<Mesh> owned, List<(GameObject go, int group)> grouped)
        {
            var root = new GameObject("Collision " + (pack.Name ?? pack.Section.ToString()));
            root.transform.SetParent(parent, false);
            var always = new List<CollisionInstance>();
            var groupObjects = new List<(CollisionInstance, GameObject)>();
            foreach (var inst in pack.Instances)
            {
                if (inst.Article == null) continue;
                if (inst.Group == 0) { always.Add(inst); continue; }
                var go = new GameObject("Group " + inst.Group);
                go.transform.SetParent(root.transform, false);
                AddColliders(go, new[] { inst }, owned, Core.WCollisionQueries.ExclusionGrouped);
                grouped.Add((go, inst.Group));
                groupObjects.Add((inst, go));
            }
            AddColliders(root, always, owned, 0);
            // the exe's own queries (WCollisionMgr) over the same pack, for as long as the section is loaded
            Dynamics.WorldQueries.Register(pack, groupObjects);
            root.AddComponent<PackRegistration>().Pack = pack;
            return root;
        }

        static readonly List<Vector3> V = new List<Vector3>();
        static readonly List<int> I = new List<int>();
        static readonly List<uint> K = new List<uint>();
        static readonly List<byte> F = new List<byte>();

        static void AddColliders(GameObject go, IList<CollisionInstance> insts, List<Mesh> owned, uint wallExclusion)
        {
            // ground
            V.Clear(); I.Clear(); K.Clear(); F.Clear(); Shared.Clear();
            foreach (var inst in insts)
                foreach (var strip in inst.Article.Strips)
                {
                    var vs = strip.Verts;
                    for (int t = 0; t + 2 < vs.Length; t++)
                    {
                        var a = ToUnity(inst.ToWorldSim(vs[t].Local));
                        var b = ToUnity(inst.ToWorldSim(vs[t + 1].Local));
                        var c = ToUnity(inst.ToWorldSim(vs[t + 2].Local));
                        var n = Vector3.Cross(b - a, c - a);
                        float len = n.magnitude;
                        if (len < 1e-5f) continue;   // degenerate (45 in the data)
                        if (n.y < 0) { var tmp = b; b = c; c = tmp; }
                        var last = vs[t + 2];
                        uint key = inst.Article.SurfaceKey(last.Surface);
                        Tri(a, b, c, key, last.SurfaceFlags);
                        // steep faces (782) and two-sided ones are walls in effect: solid from both sides [?]
                        if (Mathf.Abs(n.y / len) < 0.7f || (last.SurfaceFlags & 0x10) != 0) Tri(a, c, b, key, last.SurfaceFlags);
                    }
                }
            if (I.Count > 0)
            {
                var mesh = NewMesh(go.name + " ground");
                owned.Add(mesh);
                var g = new GameObject("Ground");
                g.transform.SetParent(go.transform, false);
                var mc = g.AddComponent<MeshCollider>();
                // no mesh cleaning: it may drop the reversed copies and renumber triangles, and triangleIndex must
                // keep indexing the surface table
                mc.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.UseFastMidphase;
                mc.sharedMesh = mesh;
                Register(mc, new MeshInfo { Keys = K.ToArray(), Flags = F.ToArray() });
            }

            // walls
            V.Clear(); I.Clear();
            var wall = new WallInfo();
            foreach (var inst in insts)
                foreach (var bar in inst.Article.Barriers)
                {
                    var p0 = ToUnity(inst.ToWorldSim(bar.P0));
                    var p1 = ToUnity(inst.ToWorldSim(bar.P1));
                    var d = p1 - p0; d.y = 0;
                    float L = d.magnitude;
                    if (L < 1e-3f) continue;
                    d /= L;
                    // sim normal (dz, 0, −dx) = cross(up, d), and sim → Unity is a rotation, so the same in Unity
                    var normal = Vector3.Cross(Vector3.up, d);
                    float y0 = Mathf.Min(p0.y, p1.y), y1 = Mathf.Max(p0.y, p1.y);
                    if (y1 - y0 < 0.5f) y1 = y0 + 0.5f;   // [?] 118 barriers are under 2 m, a few nearly flat
                    float back = bar.TwoSided ? WallThickness * 0.5f : WallThickness, front = bar.TwoSided ? WallThickness * 0.5f : 0f;
                    var a = new Vector3(p0.x, y0, p0.z);
                    Box(a + normal * front, d, Vector3.up, -normal, L, y1 - y0, front + back);
                    wall.Add(a, d, L, normal, y0, y1, bar.TwoSided);
                }
            if (I.Count > 0)
            {
                var mesh = NewMesh(go.name + " walls");
                owned.Add(mesh);
                var w = new GameObject("Walls") { layer = WallLayer };
                w.transform.SetParent(go.transform, false);
                var mc = w.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                Register(mc, new MeshInfo(), wallExclusion);
                lock (Walls) Walls[mc.GetInstanceID()] = wall;
            }
        }

        static readonly Dictionary<Vector3, int> Shared = new Dictionary<Vector3, int>();

        static void Tri(Vector3 a, Vector3 b, Vector3 c, uint key, byte flags)
        {
            I.Add(Vert(a)); I.Add(Vert(b)); I.Add(Vert(c));
            K.Add(key);
            F.Add(flags);
        }

        /// <summary>Strip vertices shared by position (the strips repeat them exactly), so no welding is needed.</summary>
        static int Vert(Vector3 p)
        {
            if (Shared.TryGetValue(p, out int i)) return i;
            i = V.Count;
            V.Add(p);
            Shared[p] = i;
            return i;
        }

        /// <summary>A closed prism from corner o along ex (length), ey (height), ez (depth), faces outward.</summary>
        static void Box(Vector3 o, Vector3 ex, Vector3 ey, Vector3 ez, float lx, float ly, float lz)
        {
            int n = V.Count;
            for (int k = 0; k < 8; k++)
                V.Add(o + ex * ((k & 1) != 0 ? lx : 0) + ey * ((k & 2) != 0 ? ly : 0) + ez * ((k & 4) != 0 ? lz : 0));
            var centre = o + (ex * lx + ey * ly + ez * lz) * 0.5f;
            // the six faces as corner quads (bit = axis): x-, x+, y-, y+, z-, z+
            int[][] faces = { new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 } };
            foreach (var f in faces)
            {
                Vector3 a = V[n + f[0]], b = V[n + f[1]], c = V[n + f[2]];
                var fc = (a + c) * 0.5f;
                bool outward = Vector3.Dot(Vector3.Cross(b - a, c - a), fc - centre) > 0;
                if (outward) { I.Add(n + f[0]); I.Add(n + f[1]); I.Add(n + f[2]); I.Add(n + f[0]); I.Add(n + f[2]); I.Add(n + f[3]); }
                else { I.Add(n + f[0]); I.Add(n + f[2]); I.Add(n + f[1]); I.Add(n + f[0]); I.Add(n + f[3]); I.Add(n + f[2]); }
            }
        }

        static Mesh NewMesh(string name)
        {
            var mesh = new Mesh { name = name };
            if (V.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(V);
            mesh.SetTriangles(I, 0, true);
            return mesh;
        }

        /// <summary>
        /// The track's object bounds (L2RA 0x34191, one OBB): static boxes, as WCollisionAssets::Init turns them into
        /// collision objects (WCollisionAssets.cpp:94-105). Game-axis matrix rows and half extents.
        /// </summary>
        public static void BuildTrackObbs(byte[] bundle, Transform parent)
        {
            // the track's CollisionGeometry pack (smackables' collision boxes, sub_681540)
            Dynamics.CollisionBounds.LoadTrack(bundle);
            Dynamics.WorldQueries.ClearObjects();
            foreach (var o in TrackObb.ReadAll(bundle))
            {
                var go = new GameObject($"TrackOBB {o.TypeHash:X8}");
                go.transform.SetParent(parent, false);
                var rot = new float[9];
                for (int r = 0; r < 3; r++)
                    for (int k = 0; k < 3; k++) rot[r * 3 + k] = o.Matrix[r * 4 + k];
                WorldStreamer.ApplyMatrix(go.transform, Coords.InstanceMatrix(rot, new Vec3(o.Matrix[12], o.Matrix[13], o.Matrix[14])));
                go.transform.localScale = Vector3.one;
                var box = go.AddComponent<BoxCollider>();
                box.size = 2f * new Vector3(Mathf.Abs(o.Dims.X), Mathf.Abs(o.Dims.Z), Mathf.Abs(o.Dims.Y));
                Register(box, new MeshInfo(), 0, 3);   // kind 3: ResolveWorldOBBCollision uses OBJ_FRICTION, not WALL_FRICTION
                // WCollisionAssets::Init (:94-105): each TrackOBB is also a managed WCollisionObject for GetObjectList
                var t = go.transform;
                Dynamics.WorldQueries.AddObject(t.position, t.right, t.up, t.forward, box.size * 0.5f);
            }
        }
    }
}
