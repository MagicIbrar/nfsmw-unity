using System.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NfsMw.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NfsMw
{
    /// <summary>
    /// Streams Rockport (TRACKS\STREAML2RA.BUN) the way speed.exe's TrackStreamer does (World/TrackStreamer.cpp,
    /// exe 0x723600-0x73182B; NFSMW_RE/specs/world_render.md "TrackStreamer"):
    ///  * each followed position (the exe follows the player views' cameras, UpdateCameraMovers 0x47CA9E; here the
    ///    Focus / ExtraFocus transforms) has a zone = its predicted drivable section (GetPredictedZone 0x72DB80: 1.5 s
    ///    ahead inside streamer-prediction zones), and the zone switches the frame it changes (0x72DF20);
    ///  * the streamed set is Y0, X0, Z0, the keep sections and, per zone, its loading section's (or its own) visible
    ///    list (DetermineStreamingSections 0x72DD70, GetSectionsToLoad 0x72C750);
    ///  * on a switch every loaded section that left the set is unloaded at once (SwitchZones 0x730460, PC), the new
    ///    ones are loaded textures first, then libraries, then regular sections, two at a time, lowest
    ///    LoadingPriority first (0x728620 / 0x72E8F0); a far load (teleport, first load) blocks behind the loading
    ///    screen instead, so there everything is requested at once [ours: no disc to emulate];
    ///  * the pool (0x7EF4000 + 50000 KB, LoadGlobalChunks 0x664A24) jettisons the least important sections only
    ///    when a set does not fit (ChooseSectionToJettison 0x72C410; one player's set always fits on PC);
    ///  * NeedLoadingBar = CheckLoadingBar 0x730D20 (an unloaded road section less than 5 m ahead).
    /// Sections are parsed on worker threads and built over several frames. X0 holds shared solids (props, lights,
    /// trees), Y0 shared textures; both stay loaded.
    /// </summary>
    public sealed class WorldStreamer : MonoBehaviour
    {
        public Transform Focus;
        /// <summary>More cars to stream around (split screen: the exe's second streaming position).</summary>
        public readonly List<Transform> ExtraFocus = new List<Transform>();
        readonly List<Vector3> _focus = new List<Vector3>();
        /// <summary>Radius streaming of the old port, used only when <see cref="ExeStreaming"/> is off.</summary>
        public float LoadRadius = 500f;
        public float UnloadRadius = 700f;
        /// <summary>The exe's zone streaming (default). Off: the old radius + visible-list residency.</summary>
        public bool ExeStreaming = true;
        /// <summary>[ours] Sections within this distance of a focus point stay loaded on top of the exe's set, so the cars
        /// always have ground (they draw only when listed). 0 = the exe's set alone.</summary>
        public float GroundRadius = 150f;
        public bool Colliders = true;
        /// <summary>Race barriers (instance flag bit 23) outside any controlled group only appear when set.</summary>
        public bool ShowRaceBarriers = false;
        public float FrameBudgetMs = 6f;
        public bool Verbose;

        public sealed class Section
        {
            public StreamSection Info;
            public float MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue, MinZ = float.MaxValue;
            public bool HasScenery;
            public GameObject Root;
            public Task<TrackSectionData> Pending;
            public bool Building;
            public TrackSectionData Data;
            /// <summary>The section's world collision packs (0x3B801), parsed with Data.</summary>
            public List<CollisionPack> Collision;
            public readonly List<Mesh> OwnedMeshes = new List<Mesh>();
            public Renderer[] Renderers;
            public bool Shown = true;
            /// <summary>The exe's record (0x34110) for this section, when it has one.</summary>
            public StreamingSectionRecord Record;
            /// <summary>TrackStreamingSection.CurrentlyVisible: bit per streaming position that needs the section.</summary>
            public int Needed;
            /// <summary>[ours] held by <see cref="GroundRadius"/> only.</summary>
            public bool Ground;
            /// <summary>AssignLoadingPriority: best priority * 100000 + SectionPriority (lower loads first).</summary>
            public int LoadingPriority;
            /// <summary>Texture pack sections (Y*): their textures are in the library.</summary>
            public bool TexturesLoaded;

            public bool Resident => Needed != 0 || Ground;

            public float Distance(Vector3 unityPos)
            {
                float dx = Mathf.Max(0, Mathf.Max(MinX - unityPos.x, unityPos.x - MaxX));
                float dy = Mathf.Max(0, Mathf.Max(MinY - unityPos.z, unityPos.z - MaxY));
                return Mathf.Sqrt(dx * dx + dy * dy);
            }
        }

        public TrackStream Track { get; private set; }
        public SceneryGroupTable Groups { get; private set; }
        /// <summary>The game's per-drivable-section draw lists (what is visible from where).</summary>
        public VisibleSections Visibility { get; private set; }
        /// <summary>The exe's streaming rules and data (sections, loading sections, prediction zones).</summary>
        public TrackStreamerRules Rules { get; private set; }
        /// <summary>Sections drawn from the focus points' drivable sections (empty: off the drivable map, draw all).</summary>
        readonly HashSet<int> _pvs = new HashSet<int>();
        readonly List<int> _pvsKey = new List<int>();
        public readonly List<Section> Sections = new List<Section>();
        readonly Dictionary<int, Section> _byNumber = new Dictionary<int, Section>();
        public bool Ready { get; private set; }
        public int BuildingCount { get; private set; }
        public int LoadedCount { get; private set; }

        // ---- TrackStreamer state ----------------------------------------------------------------------------------
        /// <summary>StreamingPositionEntries: [0] Focus, [1] ExtraFocus[0] (more extras are [ours]).</summary>
        public readonly List<StreamingPosition> Positions = new List<StreamingPosition>();
        readonly List<Vector3> _lastPos = new List<Vector3>();
        readonly List<Transform> _lastFocus = new List<Transform>();
        int[] _zones = new int[2];
        bool _needsRefresh = true;
        /// <summary>CurrentZoneFarLoad: set by a switch to a zone not touching the old one (and at start); the load then
        /// runs as a blocking one and the loading bar is off.</summary>
        public bool FarLoad { get; private set; } = true;
        /// <summary>LoadingBacklog (CalculateLoadingBacklog 0x723AD0, PC): KB of the set still to load x 0.0001.</summary>
        public float LoadingBacklog { get; private set; }
        /// <summary>CheckLoadingBar 0x730D20: the game would pause the simulation behind a fade (0x64B770).</summary>
        public bool NeedLoadingBar { get; private set; }
        /// <summary>Raised when NeedLoadingBar changes (the exe: RequestPauseSimulation("TrackStreamerLoadingBar") +
        /// EFadeScreenOn(1); off: EFadeScreenOff(0x16A259) + RequestUnPauseSimulation). Not wired here.</summary>
        public static event Action<bool> LoadingBarChanged;
        /// <summary>The current zone of each position (0 = none).</summary>
        public int CurrentZone(int position) => position < Positions.Count ? Positions[position].CurrentZone : 0;
        /// <summary>A section in the set (TrackStreamer::IsSectionVisible, the CurrentVisibleSectionTable).</summary>
        public bool IsSectionVisible(int number) => _byNumber.TryGetValue(number, out var s) && s.Needed != 0;
        readonly List<Section> _queue = new List<Section>();
        int _loading;
        static readonly Task<TrackSectionData> Queued = new TaskCompletionSource<TrackSectionData>().Task;
        /// <summary>A move longer than this in one frame is a warp: the position is set, not predicted [ours].</summary>
        public float WarpDistance = 50f;

        readonly Dictionary<uint, Solid> _shared = new Dictionary<uint, Solid>();
        readonly Dictionary<uint, MeshFactory.Built> _sharedMeshes = new Dictionary<uint, MeshFactory.Built>();
        readonly Dictionary<uint, StreamSection> _textureHome = new Dictionary<uint, StreamSection>();
        // model packs (V*, X* sections) hold solids that instances in other sections use: fences, guardrails, walls,
        // beach and condo buildings (1.7k instances); their solids are indexed at scan time and loaded on first use
        readonly Dictionary<uint, StreamSection> _solidHome = new Dictionary<uint, StreamSection>();
        readonly Dictionary<string, Dictionary<uint, Solid>> _packSolids = new Dictionary<string, Dictionary<uint, Solid>>();
        readonly HashSet<string> _textureSectionsLoaded = new HashSet<string>();
        MeshFactory _factory;
        Task _init;
        // barrier instances that belong to scenery groups: built always, shown by group state
        readonly Dictionary<(int, int), (GameObject go, uint flags)> _grouped = new Dictionary<(int, int), (GameObject, uint)>();
        readonly HashSet<uint> _enabled = new HashSet<uint>();
        // world collision instances with a scenery group number: they collide only while that group is enabled
        readonly List<(GameObject go, int group)> _collisionGrouped = new List<(GameObject, int)>();
        Dictionary<int, uint> _groupNumbers;
        GameObject _staticCollision;

        public static readonly HashSet<string> RoadTextureWords = new HashSet<string> { "ROAD", "STREET", "HWY", "ASPHALT", "BRIDGE", "TUNNEL" };

        public void Begin()
        {
            if (_init != null) return;
            GameData.Mount();
            Track = new TrackStream(GameData.Root);
            Groups = SceneryGroupTable.Read(Track.Bundle);
            Visibility = VisibleSections.Read(Track.Bundle);
            Rules = TrackStreamerRules.Read(Track.Bundle, Visibility);
            _groupNumbers = SceneryGroupNumbers.Read(Track.Bundle);
            _factory = new MeshFactory(GameData.Textures);
            GameData.Textures.Resolver = ResolveTexture;
            _init = Task.Run(Scan);
        }

        /// <summary>Synchronous variant for edit-mode tools and tests.</summary>
        public void BeginAndWait()
        {
            Begin();
            _init.Wait();
            FinishInit();
        }

        void Scan()
        {
            var sw = Stopwatch.StartNew();
            foreach (var s in Track.Sections)
            {
                var sec = new Section { Info = s };
                byte[] raw = Track.ReadRaw(s);
                if (s.Name == "X0")
                {
                    foreach (var so in SolidList.ReadAll(raw, 0, raw.Length)) _shared[so.Hash] = so;
                }
                else if (s.Name == "Y0")
                {
                    lock (GameData.Textures) GameData.Textures.Add(TexturePack.ReadAll(raw, 0, raw.Length));
                    lock (_textureSectionsLoaded) _textureSectionsLoaded.Add(s.Name);
                    sec.TexturesLoaded = true;
                }
                else
                {
                    if (s.Name.StartsWith("V") || s.Name.StartsWith("X"))
                        foreach (var so in SolidList.ReadAll(raw, 0, raw.Length))
                            lock (_solidHome)
                                if (!_solidHome.ContainsKey(so.Hash)) _solidHome[so.Hash] = s;
                    foreach (var t in TexturePack.ReadAll(raw, 0, raw.Length, false))
                        lock (_textureHome)
                            if (!_textureHome.ContainsKey(t.Hash)) _textureHome[t.Hash] = s;
                    foreach (var g in Scenery.ReadAll(raw, 0, raw.Length))
                        foreach (var i in g.Instances)
                        {
                            sec.HasScenery = true;
                            sec.MinX = Math.Min(sec.MinX, i.Min.X);
                            sec.MaxX = Math.Max(sec.MaxX, i.Max.X);
                            sec.MinY = Math.Min(sec.MinY, i.Min.Y);
                            sec.MaxY = Math.Max(sec.MaxY, i.Max.Y);
                            sec.MinZ = Math.Min(sec.MinZ, i.Min.Z);
                        }
                }
                lock (Sections) Sections.Add(sec);
            }
            Debug.Log($"NFSMW: scanned {Track.Sections.Count} sections in {sw.ElapsedMilliseconds} ms ({_shared.Count} shared solids, {_solidHome.Count} pack solids, {_textureHome.Count} indexed textures)");
        }

        void FinishInit()
        {
            if (Ready) return;
            Ready = true;
            _byNumber.Clear();
            foreach (var s in Sections)
            {
                _byNumber[s.Info.Number] = s;
                if (Rules != null) Rules.ByNumber.TryGetValue(s.Info.Number, out s.Record);
            }
            if (Colliders && _staticCollision == null)
            {
                _staticCollision = new GameObject("Track OBBs");
                _staticCollision.transform.SetParent(transform, false);
                WorldCollision.BuildTrackObbs(Track.Bundle, _staticCollision.transform);
            }
        }

        /// <summary>A solid from the model pack section that holds it (loaded once, kept).</summary>
        Solid PackSolid(uint hash)
        {
            StreamSection home;
            lock (_solidHome)
                if (!_solidHome.TryGetValue(hash, out home)) return null;
            if (!_packSolids.TryGetValue(home.Name, out var pack))
            {
                pack = new Dictionary<uint, Solid>();
                var raw = Track.ReadRaw(home);
                foreach (var so in SolidList.ReadAll(raw, 0, raw.Length)) pack[so.Hash] = so;
                _packSolids[home.Name] = pack;
            }
            return pack.TryGetValue(hash, out var solid) ? solid : null;
        }

        bool ResolveTexture(uint hash)
        {
            StreamSection home;
            lock (_textureHome)
                if (!_textureHome.TryGetValue(hash, out home)) return false;
            lock (_textureSectionsLoaded)
                if (_textureSectionsLoaded.Contains(home.Name)) return false;
            var raw = Track.ReadRaw(home);
            GameData.Textures.Add(TexturePack.ReadAll(raw, 0, raw.Length));
            lock (_textureSectionsLoaded) _textureSectionsLoaded.Add(home.Name);
            return true;
        }

        void Update()
        {
            if (_init == null) return;
            if (!Ready)
            {
                if (!_init.IsCompleted) return;
                if (_init.IsFaulted) { Debug.LogException(_init.Exception); enabled = false; return; }
                FinishInit();
            }
            if (Focus == null) return;
            _focus.Clear();
            _focus.Add(Focus.position);
            foreach (var t in ExtraFocus) if (t) _focus.Add(t.position);
            if (ExeStreaming) TrackPositions();
            Stream(_focus, false);
        }

        /// <summary>
        /// UpdateCameraMovers 0x47CA9E: PredictStreamingPosition(view, eye, eye velocity, eye direction, following car)
        /// per player view, every frame. Here the focus transforms: velocity from their motion, direction from their
        /// forward axis, "following a car" when a Rigidbody carries them. A jump over WarpDistance is a warp
        /// (SetStreamingPosition 0x723820: velocity 0, prediction reset).
        /// </summary>
        void TrackPositions()
        {
            float dt = Time.deltaTime;
            int n = 1;
            foreach (var t in ExtraFocus) if (t) n++;
            while (Positions.Count < n) { Positions.Add(new StreamingPosition()); _lastPos.Add(Vector3.zero); _lastFocus.Add(null); }
            if (Positions.Count > n)
            {
                // a position stopped being followed (ClearStreamingPositions): its sections leave the set
                Positions.RemoveRange(n, Positions.Count - n);
                _lastPos.RemoveRange(n, _lastPos.Count - n);
                _lastFocus.RemoveRange(n, _lastFocus.Count - n);
                _needsRefresh = true;
            }
            int i = 0;
            foreach (var t in Enumerate())
            {
                var e = Positions[i];
                var p = t.position;
                var fwd = t.forward;
                bool warp = _lastFocus[i] != t || !e.PositionSet || (p - _lastPos[i]).sqrMagnitude > WarpDistance * WarpDistance;
                e.X = p.x; e.Y = p.z; e.Elevation = p.y;           // Coords: game X = Unity x, game Y = Unity z
                e.DirX = fwd.x; e.DirY = fwd.z;
                e.FollowingCar = t.GetComponentInParent<Rigidbody>() != null;
                if (warp)
                {
                    e.VelX = e.VelY = 0f;
                    e.PredictedZone = 0;
                    e.PredictedZoneValidTime = -1;
                    _needsRefresh = true;
                }
                else if (dt > 0f)
                {
                    e.VelX = (p.x - _lastPos[i].x) / dt;
                    e.VelY = (p.z - _lastPos[i].z) / dt;
                }
                e.PositionSet = true;
                _lastPos[i] = p;
                _lastFocus[i] = t;
                i++;
            }
        }

        IEnumerable<Transform> Enumerate()
        {
            if (Focus) yield return Focus;
            foreach (var t in ExtraFocus) if (t) yield return t;
        }

        /// <summary>
        /// Sim::SetStream 0x6F1170 (warps, GManager::WarpToMarker / Game_WarpPlayerToTrigger): streams at a point;
        /// blocking = BlockUntilLoadingComplete (here: parse and build the set synchronously).
        /// </summary>
        public void SetStreamingPosition(Vector3 unityPos, bool blocking)
        {
            if (Positions.Count == 0) { Positions.Add(new StreamingPosition()); _lastPos.Add(unityPos); _lastFocus.Add(null); }
            var e = Positions[0];
            e.X = unityPos.x; e.Y = unityPos.z; e.Elevation = unityPos.y;
            e.VelX = e.VelY = e.DirX = e.DirY = 0f;
            e.PositionSet = true;
            e.FollowingCar = false;
            e.PredictedZone = 0;
            e.PredictedZoneValidTime = -1;
            _lastPos[0] = unityPos;
            _needsRefresh = true;
            Stream(new[] { unityPos }, blocking);
        }

        /// <summary>AddKeepSection / RemoveKeepSection: up to 4 sections kept in the set whatever the zone.</summary>
        public void AddKeepSection(int number)
        {
            if (Rules == null || Array.IndexOf(Rules.KeepSections, number) >= 0) return;
            int i = Array.IndexOf(Rules.KeepSections, 0);
            if (i >= 0) { Rules.KeepSections[i] = number; _needsRefresh = true; }
        }

        public void RemoveKeepSection(int number)
        {
            if (Rules == null) return;
            int i = Array.IndexOf(Rules.KeepSections, number);
            if (i >= 0) { Rules.KeepSections[i] = 0; _needsRefresh = true; }
        }

        /// <summary>IsLoadingInProgress / !AreAllSectionsActivated: something in the set is not built yet.</summary>
        public bool IsLoadingInProgress
        {
            get
            {
                if (BuildingCount > 0 || _queue.Count > 0 || _loading > 0) return true;
                foreach (var s in Sections) if (s.Pending != null) return true;
                return false;
            }
        }

        /// <summary>Starts loads/unloads around pos. With immediate, parses and builds synchronously (tools, tests).</summary>
        public void Stream(Vector3 pos, bool immediate) => Stream(new[] { pos }, immediate);

        /// <summary>Loads what the streamer needs for the points (the exe's zone sets, or the old radius rule).</summary>
        public void Stream(IList<Vector3> points, bool immediate)
        {
            float budgetEnd = Time.realtimeSinceStartup + FrameBudgetMs / 1000f;
            UpdatePvs(points);
            if (ExeStreaming && Rules != null) StreamExe(points, immediate);
            else StreamRadius(points, immediate);
            ApplyPvs();
            if (immediate) return;
            foreach (var s in Sections)
            {
                if (s.Pending == null || s.Pending == Queued || !s.Pending.IsCompleted || s.Building) continue;
                _loading = Math.Max(0, _loading - 1);
                if (s.Pending.IsFaulted) { Debug.LogException(s.Pending.Exception); s.Pending = null; continue; }
                s.Data = s.Pending.Result;
                // SectionLoadedCallback: a section that left the set while loading is not activated
                if (ExeStreaming && !s.Resident) { s.Data = null; s.Collision = null; s.Pending = null; continue; }
                if (!s.HasScenery) { ActivateTextures(s); continue; }
                StartCoroutine(BuildRoutine(s));
                if (Time.realtimeSinceStartup > budgetEnd) break;
            }
            if (ExeStreaming) UpdateLoadingBar();
        }

        void StreamRadius(IList<Vector3> points, bool immediate)
        {
            foreach (var s in Sections)
            {
                if (!s.HasScenery) continue;
                float d = float.MaxValue;
                foreach (var pos in points) d = Mathf.Min(d, s.Distance(pos));
                bool listed = _pvs.Contains(s.Info.Number);
                // residency: near (ground for every car) or listed as visible from where the player is
                if ((d < LoadRadius || listed) && s.Root == null && s.Pending == null) StartLoad(s, immediate);
                else if (d > UnloadRadius && !listed && s.Root != null && !s.Building)
                {
                    if (Verbose) Debug.Log($"NFSMW stream: unload {s.Info.Name} at distance {d:0}");
                    Unload(s);
                }
            }
        }

        /// <summary>
        /// HandleZoneSwitching + SwitchZones 0x730460 + StartLoadingSections 0x72E8F0 (see the class summary). The
        /// GroundRadius sections are [ours].
        /// </summary>
        void StreamExe(IList<Vector3> points, bool immediate)
        {
            if (Positions.Count == 0 || immediate)
            {
                // tools and tests: a position per point, no prediction
                while (Positions.Count < points.Count) { Positions.Add(new StreamingPosition()); _lastPos.Add(Vector3.zero); _lastFocus.Add(null); }
                for (int i = 0; i < points.Count; i++)
                {
                    var e = Positions[i];
                    if (!immediate && e.PositionSet) continue;
                    e.X = points[i].x; e.Y = points[i].z; e.Elevation = points[i].y;
                    if (immediate) { e.VelX = e.VelY = 0f; e.PredictedZoneValidTime = -1; }
                    e.PositionSet = true;
                }
                if (immediate) _needsRefresh = true;
            }
            if (_zones.Length < Positions.Count) _zones = new int[Positions.Count];
            bool different = Rules.DetermineCurrentZones(Positions, _zones);
            bool ground = UpdateGround(points);
            if (different || _needsRefresh) SwitchZones();
            else if (ground) ApplyResidency(false);
            if (immediate)
            {
                foreach (var s in _queue) { s.Pending = null; StartLoad(s, true); }
                _queue.Clear();
                return;
            }
            // StartLoadingSections: two loads in flight, the lowest priority value first (the list is kept sorted);
            // a far load streams everything at once [ours]
            int max = FarLoad ? int.MaxValue : TrackStreamerRules.MaxSectionsLoading;
            while (_queue.Count > 0 && _loading < max)
            {
                var s = _queue[0];
                _queue.RemoveAt(0);
                if (!s.Resident || s.Root != null) { s.Pending = null; continue; }
                s.Pending = null;
                StartLoad(s, false);
                _loading++;
            }
            if (FarLoad && _queue.Count == 0 && !IsLoadingInProgress) FarLoad = false;   // FinishedLoading
        }

        /// <summary>[ours] The GroundRadius residency; true when it changed.</summary>
        bool UpdateGround(IList<Vector3> points)
        {
            bool changed = false;
            foreach (var s in Sections)
            {
                bool g = false;
                if (GroundRadius > 0f && s.HasScenery && TrackStreamerRules.IsRegular(s.Info.Number))
                    foreach (var p in points)
                        if (s.Distance(p) < GroundRadius) { g = true; break; }
                if (g != s.Ground) { s.Ground = g; changed = true; }
            }
            return changed;
        }

        void SwitchZones()
        {
            _needsRefresh = false;
            var vs = Visibility;
            for (int p = 0; p < Positions.Count; p++)
            {
                var e = Positions[p];
                int zone = _zones[p];
                if (e.CurrentZone == zone) continue;
                // far load: the new zone's boundary does not come within 0.1 m of the old one's points
                float best = 10000f;
                var b1 = vs.BoundaryOf(e.CurrentZone);
                var b2 = vs.BoundaryOf(zone);
                if (b1 != null && b2 != null)
                    for (int k = 0; k < b1.Points.Length / 2; k++)
                        best = Math.Min(best, b2.DistanceOutside(b1.Points[k * 2], b1.Points[k * 2 + 1], 999f));
                if (best > 0.1f) FarLoad = true;
                if (Verbose) Debug.Log($"NFSMW stream: position {p} zone {e.CurrentZone} -> {zone}{(best > 0.1f ? " (far load)" : "")}");
                e.CurrentZone = zone;
            }
            var set = Rules.DetermineStreamingSections(Positions);
            foreach (var s in Sections) s.Needed = set.TryGetValue(s.Info.Number, out int bits) ? bits : 0;
            Jettison();
            ApplyResidency(true);
        }

        /// <summary>
        /// HandleMemoryAllocation 0x72FBA0 / ChooseSectionToJettison 0x72C410: while the set does not fit the pool,
        /// drop the section with the highest discard priority (CurrentZoneOutOfMemory).
        /// </summary>
        void Jettison()
        {
            long total = 0;
            foreach (var s in Sections) if (s.Needed != 0 && s.Record != null) total += s.Record.Size;
            while (total > TrackStreamerRules.MemoryPoolSize)
            {
                Section worst = null;
                int bestD = 0;
                foreach (var s in Sections)
                {
                    if (s.Needed == 0 || s.Record == null) continue;
                    int d = Rules.DiscardPriority(s.Record, Positions, s.Root != null && !s.Building);
                    if (d > bestD) { bestD = d; worst = s; }
                }
                if (worst == null) break;
                if (Verbose) Debug.Log($"NFSMW stream: out of memory, jettison {worst.Info.Name}");
                total -= worst.Record.Size;
                worst.Needed = 0;
            }
        }

        /// <summary>Unloads what left the set (non-texture, non-library sections at once, as on PC) and queues the rest in
        /// loading order: phase (textures, libraries, regular), then LoadingPriority.</summary>
        void ApplyResidency(bool assignPriority)
        {
            foreach (var s in Sections)
            {
                if (!s.Resident)
                {
                    if (s.Root != null && !s.Building)
                    {
                        if (Verbose) Debug.Log($"NFSMW stream: unload {s.Info.Name}");
                        Unload(s);
                    }
                    if (s.Pending == Queued) s.Pending = null;
                    continue;
                }
                if (assignPriority)
                    s.LoadingPriority = s.Record != null ? Rules.LoadingPriority(s.Record, Positions, s.Needed == 0 ? 1 : s.Needed) : int.MaxValue;
                bool wanted = s.HasScenery || (TrackStreamerRules.IsTexture(s.Info.Number) && !s.TexturesLoaded && !TextureSectionLoaded(s));
                if (wanted && s.Root == null && s.Pending == null && !_queue.Contains(s))
                {
                    s.Pending = Queued;
                    _queue.Add(s);
                }
            }
            _queue.RemoveAll(s => !s.Resident);
            _queue.Sort((a, b) =>
            {
                int pa = TrackStreamerRules.Phase(a.Info.Number), pb = TrackStreamerRules.Phase(b.Info.Number);
                if (pa != pb) return pa.CompareTo(pb);
                if (a.Needed == 0 != (b.Needed == 0)) return a.Needed == 0 ? 1 : -1;   // [ours] ground-only last
                return a.LoadingPriority.CompareTo(b.LoadingPriority);
            });
            long left = 0;
            foreach (var s in Sections)
                if (s.Needed != 0 && s.Record != null && s.Root == null) left += s.Record.Size / 1024;
            LoadingBacklog = left * 0.0001f;
        }

        bool TextureSectionLoaded(Section s)
        {
            lock (_textureSectionsLoaded) return _textureSectionsLoaded.Contains(s.Info.Name);
        }

        void StartLoad(Section s, bool immediate)
        {
            var info = s.Info;
            var sec = s;
            bool collision = Colliders && s.HasScenery;
            bool scenery = s.HasScenery;
            if (immediate)
            {
                s.Data = Track.Load(info, true, scenery, scenery);
                s.Collision = collision ? LoadCollision(info) : null;
                if (!scenery) ActivateTextures(s);
                else BuildNow(s);
            }
            else s.Pending = Task.Run(() =>
            {
                var data = Track.Load(info, true, scenery, scenery);
                sec.Collision = collision ? LoadCollision(info) : null;
                return data;
            });
        }

        /// <summary>A texture section of the set (LOADING_TEXTURE_SECTIONS): its textures go into the library.</summary>
        void ActivateTextures(Section s)
        {
            if (!TextureSectionLoaded(s) && s.Data != null) GameData.Textures.Add(s.Data.Textures);
            lock (_textureSectionsLoaded) _textureSectionsLoaded.Add(s.Info.Name);
            s.TexturesLoaded = true;
            s.Data = null;
            s.Pending = null;
            s.Collision = null;
        }

        /// <summary>
        /// CheckLoadingBar 0x730D20 (called from the main loop in game, 0x64B770): while a near load runs and a road
        /// section of the set (drivable or LOD) is not in yet less than 3 m ahead of a car-following position.
        /// </summary>
        void UpdateLoadingBar()
        {
            bool need = false;
            if (!FarLoad && IsLoadingInProgress)
            {
                var pending = new List<int>();
                foreach (var s in Sections)
                    if (s.Needed != 0 && (s.Root == null || s.Building)) pending.Add(s.Info.Number);
                need = pending.Count > 0 && Rules.LoadingBarDistance(Positions, pending) < 3f;   // 0x730F11: fcomp [0x8B27CC] = 3.0 (the decomp's 5 is not the PC value)
            }
            if (need == NeedLoadingBar) return;
            NeedLoadingBar = need;
            if (Verbose) Debug.Log($"NFSMW stream: loading bar {(need ? "on" : "off")}");
            LoadingBarChanged?.Invoke(need);
        }

        /// <summary>The draw set: the visible list of the drivable section under the view's camera eye
        /// (GrandSceneryCullInfo::WhatSectionsShouldWeDraw 0x731870: eView->Camera +0x40 through FindDrivableSection
        /// 0x72CA00; the shadow views 0xF/0x10 use the player camera). Without a camera the focus points stand in [ours].</summary>
        void UpdatePvs(IList<Vector3> points)
        {
            _pvsKey.Clear();
            var cam = Camera.main;
            if (Visibility != null && cam)
            {
                var p = cam.transform.position;
                int sec = Visibility.FindDrivableSection(p.x, p.z);
                if (sec >= 0) _pvsKey.Add(sec);
            }
            else if (Visibility != null)
                foreach (var p in points)
                {
                    int sec = Visibility.FindDrivableSection(p.x, p.z);
                    if (sec >= 0 && !_pvsKey.Contains(sec)) _pvsKey.Add(sec);
                }
            _pvsKey.Sort();
            if (_pvsKey.SequenceEqual(_lastPvsKey)) return;
            _lastPvsKey.Clear();
            _lastPvsKey.AddRange(_pvsKey);
            _pvs.Clear();
            foreach (var k in _pvsKey)
                if (Visibility.Visible.TryGetValue(k, out var list)) foreach (var n in list) _pvs.Add(n);
            if (Verbose) Debug.Log($"NFSMW stream: in {string.Join(",", _pvsKey)}, {_pvs.Count} sections visible");
        }

        readonly List<int> _lastPvsKey = new List<int>();

        /// <summary>Draws only the listed sections (colliders stay, so off-screen cars keep their ground).</summary>
        void ApplyPvs()
        {
            foreach (var s in Sections)
            {
                if (s.Root == null || s.Building) continue;
                // as GrandSceneryCullInfo::WhatSectionsShouldWeDraw: the Z row always, then the drivable section's list;
                // off the drivable map, only subsections below 10
                int n = s.Info.Number;
                bool show = n / 100 == 26 || (_pvs.Count > 0 ? _pvs.Contains(n) : n % 100 < 10);
                if (show == s.Shown) continue;
                s.Shown = show;
                s.Renderers ??= s.Root.GetComponentsInChildren<Renderer>(true);
                foreach (var r in s.Renderers) if (r) r.enabled = show;
            }
        }

        void BuildNow(Section s)
        {
            var e = BuildRoutine(s, false);
            while (e.MoveNext()) { }
        }

        IEnumerator BuildRoutine(Section s, bool timeSliced = true)
        {
            s.Building = true;
            BuildingCount++;
            var root = new GameObject(s.Info.Name);
            root.transform.SetParent(transform, false);
            s.Root = root;
            var data = s.Data;
            GameData.Textures.Add(data.Textures);
            _textureSectionsLoaded.Add(s.Info.Name);
            var local = new Dictionary<uint, Solid>();
            foreach (var so in data.Solids) local[so.Hash] = so;
            var localMeshes = new Dictionary<uint, MeshFactory.Built>();
            _local = local;
            _localMeshes = localMeshes;
            _building = s;
            // SmokeableSpawner records (0x34027) name, through the override table, the static instance each one replaces
            var smackables = new Dictionary<(int, int), SmokeableSpawner>();
            if (Groups != null)
                foreach (var sp in data.Smokeables)
                    if (sp.OverrideInfo < Groups.Overrides.Count) smackables[Groups.Overrides[(int)sp.OverrideInfo]] = sp;

            float sliceEnd = Time.realtimeSinceStartup + FrameBudgetMs / 1000f;
            // the ground and walls first (WCollisionAssets::LoadCollisionPack on the section's load)
            if (s.Collision != null)
            {
                foreach (var pack in s.Collision)
                {
                    WorldCollision.Build(pack, root.transform, s.OwnedMeshes, _collisionGrouped);
                    if (timeSliced && Time.realtimeSinceStartup > sliceEnd)
                    {
                        yield return null;
                        sliceEnd = Time.realtimeSinceStartup + FrameBudgetMs / 1000f;
                        _local = local;
                        _localMeshes = localMeshes;
                        _building = s;
                    }
                }
                s.Collision = null;
                ApplyCollisionGroups();
            }
            foreach (var g in data.Scenery)
            {
                for (int index = 0; index < g.Instances.Count; index++)
                {
                    var inst = g.Instances[index];
                    if (inst.InfoIndex >= g.Infos.Count) continue;
                    var info = g.Infos[inst.InfoIndex];
                    var key = (g.SectionNumber, index);
                    // effective flags: a group override replaces the low 16 bits (Scenery.cpp:155). Never-in-player-view
                    // (0x02) is not built; disabled-until-group-enabled (0x10), hidden-while-racing (0x04) and
                    // hidden-in-split-screen (0x01) are built and switched by ApplyInstanceFlags
                    uint flags = inst.Flags;
                    bool grouped = Groups != null && Groups.ByInstance.ContainsKey(key);
                    if (grouped && Groups.OverrideFlags.TryGetValue(key, out var ov)) flags = (flags & 0xFFFF0000u) | ov;
                    if ((flags & 0x02) != 0) continue;
                    if (!grouped && (flags & 0x10) != 0) continue;
                    bool conditional = grouped || (flags & 0x05) != 0;
                    // SceneryInfo models (Scenery.cpp:390): model 0 = LOD A (else B), model 2 = LOD C (else B)
                    uint h0 = LodHash(info, 0), h2 = LodHash(info, 2);
                    var built = Resolve(h0, out var solid);
                    if (built == null) { built = Resolve(h2, out solid); h0 = h2; }
                    if (built == null) continue;
                    MeshFactory.Built built2 = h2 != 0 && h2 != h0 ? Resolve(h2, out _) : null;
                    // the reflection-only sky is not drawn in the player view; SKYDOME_XENON is the PC shader-path sky
                    // (Sky/SkyRender.cs, NotifySkyLoader 0x6D1CF0)
                    if (info.Name.StartsWith("SKY_SPECULAR")) continue;

                    var go = new GameObject(info.Name);
                    go.transform.SetParent(root.transform, false);
                    ApplyMatrix(go.transform, Coords.InstanceMatrix(inst.Rotation, inst.Position));
                    go.AddComponent<MeshFilter>().sharedMesh = built.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    // instance flag 0x100 (the SHD_ baked shadows): render flag 0x20000 -> shadow_cg.efx in every view (0x6DABFC)
                    mr.sharedMaterials = (flags & 0x100u) != 0 && CurrentGenShadows ? World.WorldMaterials.ShadowCg(built.Materials) : built.Materials;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    if (info.Name.StartsWith("SKYDOME")) { if (!Sky.SkyRender.Setup(go, mr, solid, info.Name)) SkyDome.Setup(go, mr); continue; }
                    // distant panoramas (PAN_*) are one-sided rings around the play area: seen from inside, cull them
                    bool panorama = info.Name.StartsWith("PAN_") || info.Name.StartsWith("Panorama");
                    if (panorama) mr.sharedMaterials = OneSided(built.Materials);
                    else if (Lods) AddLod(go, mr, built2, info, solid, flags);
                    // breakable props are dynamic bodies; everything else collides through the section's WCollisionPack
                    SmokeableSpawner spawner = default;
                    bool smackable = Colliders && smackables.TryGetValue(key, out spawner);
                    if (smackable) Smackable.Make(go, built.Mesh, SmackableAttributes(spawner.Attributes), spawner.Collision);
                    else if (!panorama) AddReflection(root, mr, info, inst, flags);
                    // the shadow view 0xF (sub_6E44C0): StuffScenery(view 0xF, 0x2000) draws the instances flagged
                    // 0x40000000; RenderWorldModels (0x7538D0) every WorldModel, i.e. the smackables (world_render.md §13)
                    if (!panorama && (World.WorldShadows.IsCaster(flags) || (smackable && World.WorldShadows.Enabled)))
                        World.WorldShadows.MakeCaster(go);
                    if (conditional)
                    {
                        _grouped[key] = (go, flags);
                        go.SetActive(InstanceVisible(key, flags));
                    }

                    if (timeSliced && Time.realtimeSinceStartup > sliceEnd)
                    {
                        yield return null;
                        sliceEnd = Time.realtimeSinceStartup + FrameBudgetMs / 1000f;
                        // other sections may have built in between: Resolve reads this section's solids again
                        _local = local;
                        _localMeshes = localMeshes;
                        _building = s;
                    }
                }
            }
            s.Data = null;
            s.Pending = null;
            s.Building = false;
            BuildingCount--;
            LoadedCount++;
            s.Renderers = null;
            s.Shown = true;
            if (Verbose) Debug.Log($"NFSMW stream: built {s.Info.Name} ({root.transform.childCount} objects), loaded {LoadedCount}, building {BuildingCount}");
            // left the set while it was building (the exe would not activate it): unload
            if (ExeStreaming && Rules != null && !s.Resident && Positions.Count > 0) { Unload(s); yield break; }
            ApplyPvs();
        }

        // SceneryInstance flag bit 18 marks the baked shadow layer (all SHD_*). Breakable props are not a flag: they are the
        // instances a section's SmokeableSpawner records point at.
        public const uint FlagShadow = 1u << 18;
        /// <summary>[ours] false draws the flag-0x100 (SHD_) instances with world.efx lighting instead of shadow_cg.</summary>
        public static bool CurrentGenShadows = true;

        static AttribCollection SmackableAttributes(uint key)
        {
            var db = GameData.Attributes;
            if (db == null) return null;
            if (db.Collections.TryGetValue((Smackable.ClassKey, key), out var c)) return c;
            return db.Collections.TryGetValue((Smackable.ClassKey, AttribDatabase.Hash("default")), out c) ? c : null;
        }

        /// <summary>Scenery LOD switching and distance culling as in the game (off: always the full model, never culled).</summary>
        public static bool Lods = true;

        // NameHash values the loader treats as "no model" (Scenery.cpp:397)
        static uint LodHash(SceneryInfo info, int level)
        {
            bool None(uint x) => x == 0 || x == 0xBE43EDBB || x == 0x90F70174;
            uint h = info.SolidHashes[level];
            if (None(h) && (level == 0 || level == 2)) h = info.SolidHashes[1];
            return None(h) ? 0 : h;
        }

        Dictionary<uint, Solid> _local;
        Dictionary<uint, MeshFactory.Built> _localMeshes;
        Section _building;

        /// <summary>A scenery model by solid hash: the section's own solids first, then the shared and pack libraries.</summary>
        MeshFactory.Built Resolve(uint h, out Solid solid)
        {
            solid = null;
            if (h == 0) return null;
            MeshFactory.Built built = null;
            if (_local.TryGetValue(h, out solid))
            {
                if (!_localMeshes.TryGetValue(h, out built))
                {
                    built = _factory.Build(solid, PartKind.World, VariantFor(solid));
                    World.WorldMaterials.Apply(built, solid, GameData.Textures);   // per-effect world shaders (world_render.md §2)
                    if (built != null) World.WorldShadows.Prepare(built.Materials);   // casters only by instance (world_render.md §13)
                    _localMeshes[h] = built;
                    if (built != null) _building.OwnedMeshes.Add(built.Mesh);
                }
            }
            else if (_shared.TryGetValue(h, out solid) || (solid = PackSolid(h)) != null)
            {
                if (!_sharedMeshes.TryGetValue(h, out built))
                {
                    built = _factory.Build(solid, PartKind.World, VariantFor(solid));
                    World.WorldMaterials.Apply(built, solid, GameData.Textures);
                    if (built != null) World.WorldShadows.Prepare(built.Materials);
                    _sharedMeshes[h] = built;
                }
            }
            return built;
        }

        /// <summary>
        /// SceneryDrawInfo LOD pick (Scenery.cpp:711, main view). The pixel size of the instance's sphere
        /// (radius + 6) is r x H / (d - r). Under 18 px nothing draws. Instances flagged 0x2000000 count 10 px more.
        /// A detailed model (40+ polys) shows while pixels / max(Density, 6) >= 8.7, otherwise model 2 does.
        /// H is the view's (0x6BE9F0, from 0x6CF400): (__window_width [0x982BE4] / 2) / tan(horizontal fov / 2), i.e. real
        /// screen pixels, so on PC the LOD distances grow with the resolution. With square pixels that is
        /// pixels = (screen height / 2) x Unity's screen-relative height, so the thresholds become LODGroup transition
        /// heights for the screen the section is built on (<see cref="LodHalfLines"/>; it was a fixed 480-line screen).
        /// </summary>
        static void AddLod(GameObject go, MeshRenderer mr, MeshFactory.Built low, SceneryInfo info, Solid solid, uint flags)
        {
            float bump = (flags & 0x2000000) != 0 ? 10f : 0f;
            float lines = LodHalfLines;
            float cull = Mathf.Max(2f, 18f - bump) / lines;
            float detail = cull;
            if (low != null && solid != null && solid.NumPolys > 39)
                detail = Mathf.Max(cull, (8.7f * Mathf.Max(solid.Density, 6f) - bump) / lines);
            var group = go.AddComponent<LODGroup>();
            group.localReferencePoint = Vector3.zero;
            group.size = 2f * (info.Radius + 6f);
            if (low != null && detail > cull * 1.001f)
            {
                var child = new GameObject("LOD2");
                child.transform.SetParent(go.transform, false);
                child.layer = go.layer;
                child.AddComponent<MeshFilter>().sharedMesh = low.Mesh;
                var lr = child.AddComponent<MeshRenderer>();
                lr.sharedMaterials = low.Materials;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                group.SetLODs(new[] { new LOD(detail, new Renderer[] { mr }), new LOD(cull, new Renderer[] { lr }) });
            }
            else group.SetLODs(new[] { new LOD(cull, new Renderer[] { mr }) });
        }

        /// <summary>Half the player view's height in pixels (the eView H scale of <see cref="AddLod"/>); 240 without a camera.</summary>
        static float LodHalfLines
        {
            get
            {
                var cam = Camera.main;
                return cam && cam.pixelHeight > 0 ? cam.pixelHeight * 0.5f : 240f;
            }
        }

        /// <summary>
        /// The road reflection view's copy of an instance (World/RoadReflection.cs, world_render.md §4): DrawAScenery
        /// 0x729120 with view 4's cull flags keeps instances flagged 0x40 or 0x8000000, StuffScenery(view 4, 8) drops the
        /// flag-0x80 ground; the model is SceneryInfo model 3 (the reflection LOD D, no fallback) or model 0 when the
        /// instance is flagged 0x1000100 (0x72932B). [ours] Breakable props (smackables) are left out: the exe draws them
        /// as WorldModels, which view 4 does not render.
        /// </summary>
        void AddReflection(GameObject root, MeshRenderer owner, SceneryInfo info, SceneryInstance inst, uint flags)
        {
            if ((flags & 0x8000040u) == 0 || (flags & 0x80u) != 0) return;
            uint h = (flags & 0x1000100u) != 0 ? LodHash(info, 0) : NoModel(info.SolidHashes[3]) ? 0 : info.SolidHashes[3];
            var model = h != 0 ? Resolve(h, out _) : null;
            if (model == null) return;
            var world = root.transform.localToWorldMatrix * Coords.InstanceMatrix(inst.Rotation, inst.Position);
            World.RoadReflection.AddScenery(root, owner, model.Mesh, model.Materials, world, info.Radius + 6f, (flags & 0x2000000u) != 0);
        }

        static bool NoModel(uint x) => x == 0 || x == 0xBE43EDBB || x == 0x90F70174;

        static System.Func<SolidPart, int> VariantFor(Solid solid)
        {
            if (solid.Name.StartsWith("SHD_")) return _ => 3;
            return _ => 0;
        }

        /// <summary>
        /// The main view's cull test (Ecstasy.cpp:171, Scenery.cpp:689): hidden when the effective flags carry 0x10
        /// (disabled), 0x02 (not in player views), 0x01 in split screen or 0x04 while racing. Enabling one of the
        /// instance's scenery groups masks the flags with 0xFBEF (EnableRendering), which clears 0x10.
        /// </summary>
        bool InstanceVisible((int, int) key, uint flags)
        {
            if (Groups.ByInstance.TryGetValue(key, out var groups))
                foreach (var h in groups)
                    if (_enabled.Contains(h)) { flags &= 0xFFFFFBEF; break; }
            uint mask = 0x12u | (SplitScreen ? 0x01u : 0) | (Racing ? 0x04u : 0);
            return (flags & 0xFF & mask) == 0;
        }

        /// <summary>Split-screen and racing views hide more props (cull flags 0x01 / 0x04).</summary>
        public bool SplitScreen, Racing;

        /// <summary>
        /// Enabled scenery groups (barriers of the bin's locked areas or the race route, and any other group the game
        /// switches on): their instances lose the disabled flag. Also re-applies the split-screen and racing cull flags.
        /// </summary>
        public void SetBarrierGroups(IEnumerable<string> controlled, IEnumerable<string> enabled)
        {
            _enabled.Clear();
            foreach (var n in enabled) _enabled.Add(SceneryGroupTable.Hash(n));
            var dead = new List<(int, int)>();
            int shown = 0;
            foreach (var kv in _grouped)
            {
                if (!kv.Value.go) { dead.Add(kv.Key); continue; }
                bool on = InstanceVisible(kv.Key, kv.Value.flags);
                kv.Value.go.SetActive(on);
                if (on) shown++;
            }
            foreach (var k in dead) _grouped.Remove(k);
            ApplyCollisionGroups();
            Debug.Log($"NFSMW barriers: {_enabled.Count} groups up, {shown}/{_grouped.Count} switchable pieces shown");
        }

        static readonly Dictionary<Material, Material> CulledCopies = new Dictionary<Material, Material>();

        static Material[] OneSided(Material[] mats)
        {
            var r = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || !m.HasProperty("_Cull")) { r[i] = m; continue; }
                if (!CulledCopies.TryGetValue(m, out var c)) { c = new Material(m); c.SetFloat("_Cull", 2); CulledCopies[m] = c; }
                r[i] = c;
            }
            return r;
        }

        /// <summary>The section's collision packs (the 0x3B801 chunks in its stream data; worker thread).</summary>
        List<CollisionPack> LoadCollision(StreamSection info)
        {
            var raw = Track.ReadRaw(info);
            return CollisionPack.ReadAll(raw, 0, raw.Length);
        }

        /// <summary>
        /// Grouped collision instances collide only while their scenery group is enabled (WCollisionMgr.cpp:566,
        /// IsSceneryGroupEnabled); the 0xC0 exclusion bits (WCollisionAssets.cpp:117-147) need no extra handling here.
        /// </summary>
        void ApplyCollisionGroups()
        {
            for (int i = _collisionGrouped.Count - 1; i >= 0; i--)
            {
                var (go, group) = _collisionGrouped[i];
                if (!go) { _collisionGrouped.RemoveAt(i); continue; }
                bool on = _groupNumbers != null && _groupNumbers.TryGetValue(group, out uint hash) && _enabled.Contains(hash);
                if (go.activeSelf != on) go.SetActive(on);
            }
        }

        public static void ApplyMatrix(Transform t, Matrix4x4 m)
        {
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0)
            {
                scale.x = -scale.x;
                x = -x;
            }
            t.localPosition = m.GetColumn(3);
            if (scale.x != 0 && scale.y != 0 && scale.z != 0)
                t.localRotation = Quaternion.LookRotation(z / scale.z, y / scale.y);
            t.localScale = scale;
        }

        void Unload(Section s)
        {
            World.RoadReflection.RemoveScenery(s.Root);
            Destroy(s.Root);
            foreach (var m in s.OwnedMeshes) Destroy(m);
            s.OwnedMeshes.Clear();
            s.Root = null;
            s.Pending = null;
            s.Renderers = null;
            s.Shown = true;
            LoadedCount--;
        }

        /// <summary>Closest point on a road-textured triangle near pos (Unity space), with a heading along the road.</summary>
        public bool FindRoadPoint(Vector3 near, out Vector3 point, out Vector3 forward)
        {
            point = near;
            forward = Vector3.forward;
            float best = float.MaxValue;
            foreach (var mf in GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                var mesh = mf.sharedMesh;
                if (!mesh.isReadable || mr == null) continue;
                var verts = mesh.vertices;
                var l2w = mf.transform.localToWorldMatrix;
                for (int sm = 0; sm < mesh.subMeshCount && sm < mr.sharedMaterials.Length; sm++)
                {
                    var mat = mr.sharedMaterials[sm];
                    if (mat == null || !IsRoad(mat.name)) continue;
                    var tris = mesh.GetTriangles(sm);
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        var a = l2w.MultiplyPoint3x4(verts[tris[t]]);
                        var b = l2w.MultiplyPoint3x4(verts[tris[t + 1]]);
                        var c = l2w.MultiplyPoint3x4(verts[tris[t + 2]]);
                        var n = Vector3.Cross(b - a, c - a);
                        if (n.sqrMagnitude < 4f || Mathf.Abs(n.normalized.y) < 0.95f) continue; // flat, not tiny
                        var center = (a + b + c) / 3f;
                        float d = (center - near).sqrMagnitude;
                        if (d < best)
                        {
                            best = d;
                            point = center;
                            var e1 = b - a; var e2 = c - b; var e3 = a - c;
                            var longest = e1.sqrMagnitude > e2.sqrMagnitude ? (e1.sqrMagnitude > e3.sqrMagnitude ? e1 : e3) : (e2.sqrMagnitude > e3.sqrMagnitude ? e2 : e3);
                            forward = Vector3.ProjectOnPlane(longest, Vector3.up).normalized;
                        }
                    }
                }
            }
            return best < float.MaxValue;
        }

        static bool IsRoad(string textureName)
        {
            var up = textureName.ToUpperInvariant();
            foreach (var w in RoadTextureWords)
                if (up.Contains(w)) return true;
            return false;
        }

        void Awake() => Current = this;

        /// <summary>The streamer of the loaded world (the last one created).</summary>
        public static WorldStreamer Current { get; private set; }

        /// <summary>
        /// TheVisibleSectionManager.ActivateOverlay(name) (0x731D80, from CAnimScene::Init with the NIS scene header's
        /// SeeulatorOverlayName, e.g. "FLYBY" on the Blacklist intro scenes): the overlay's entries edit the drivable sections'
        /// visible lists (Core/Track.cs VisibleSections.ApplyOverlay), then RefreshTrackStreamer 0x731750 re-runs the zone
        /// switch so the streaming set follows them. The draw set is rebuilt on the next Stream.
        /// </summary>
        public static void ActivateOverlay(string name)
        {
            var w = Current;
            if (!w || w.Visibility == null) return;
            if (w.Visibility.ApplyOverlay(name)) w.OverlayChanged(name);
        }

        /// <summary>UnactivateOverlay (0x72DFF0, DestroyAnimScene): the undo list restores the visible lists.</summary>
        public static void UnactivateOverlay()
        {
            var w = Current;
            if (!w || w.Visibility == null) return;
            if (w.Visibility.UndoOverlay()) w.OverlayChanged(null);
        }

        void OverlayChanged(string name)
        {
            _lastPvsKey.Clear();
            _needsRefresh = true;
            if (Verbose) Debug.Log($"NFSMW stream: visible-section overlay {(name ?? "off")}");
        }

        void OnDestroy() => Track?.Dispose();
    }

    /// <summary>The game's sky dome: unlit, fog-free, drawn first and kept centred on the camera inside its far plane.</summary>
    public sealed class SkyDome : MonoBehaviour
    {
        static Shader _shader;
        Vector3 _scale;

        public static void Setup(GameObject go, MeshRenderer mr)
        {
            _shader ??= Shader.Find("NFSMW/Sky");
            if (_shader)
            {
                var mats = mr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = new Material(_shader);
                    if (mats[i]) m.mainTexture = mats[i].mainTexture;
                    mats[i] = m;
                }
                mr.sharedMaterials = mats;
                // SkyRender.cpp: the BLUE layer (SKY_MIDDAY_A_GRADIENT_A in the clouds' slot, x1.1) is drawn under the
                // CLOUDS layer; without it the camera clear shows through
                var grad = GameData.Textures.Get(EaHash.Hash("SKY_MIDDAY_A_GRADIENT_A"));
                if (grad)
                {
                    var under = new GameObject("SkyGradient");
                    under.transform.SetParent(go.transform, false);
                    under.transform.localScale = Vector3.one * 1.1f;
                    under.AddComponent<MeshFilter>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
                    var umr = under.AddComponent<MeshRenderer>();
                    var umats = new Material[mats.Length];
                    for (int i = 0; i < mats.Length; i++)
                    {
                        umats[i] = new Material(_shader) { renderQueue = 999 };
                        var t = mats[i] ? mats[i].mainTexture : null;
                        umats[i].mainTexture = t && t.name.Contains("CLOUD") ? grad : t;
                    }
                    umr.sharedMaterials = umats;
                    umr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
            go.AddComponent<SkyDome>();
        }

        void Awake() => _scale = transform.localScale;

        void LateUpdate()
        {
            var cam = Camera.main;
            if (!cam) return;
            // the dome is ~19 km across: shrink it to sit within the far plane, centred on the camera
            float s = cam.farClipPlane * 0.8f / 9700f;
            transform.localScale = _scale * s;
            transform.position = cam.transform.position;   // LockSky: centred on the camera
        }
    }
}
