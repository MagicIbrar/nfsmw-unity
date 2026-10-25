using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// A `trafficpattern` collection (Generated/AttribSys/Classes/trafficpattern.h, Misc/MWAttribUserTypes.h:114-119):
    /// the car mix as TrafficPatternRecord[0x18] {RefSpec Vehicle (pvehicle key at +4), f32 Rate (s), u32 MaxInstances
    /// (0 = no cap), u32 Percent (0 = no share cap)}, of which the first 10 count (MAX_PATTERN_TYPES), plus SpawnTime and
    /// the street/highway speeds in mph. drag and industrial have no records of their own and inherit default's.
    /// </summary>
    public sealed class TrafficPattern
    {
        public sealed class Record
        {
            public uint Vehicle;
            public string Name;
            public float Rate;
            public int MaxInstances, Percent;
        }

        public uint Key, NameHash;
        public string Name;
        public float SpawnTime = 4f, SpeedStreet = 35f, SpeedHighway = 55f;
        public readonly List<Record> Vehicles = new List<Record>();
        public const int MaxTypes = 10;

        static Dictionary<uint, TrafficPattern> _byKey, _byName;

        /// <summary>All patterns by attribute collection key.</summary>
        public static Dictionary<uint, TrafficPattern> ByKey { get { Load(); return _byKey; } }
        /// <summary>All patterns by bStringHash(CollectionName), as the traffic zones name them (AITrafficManager.cpp:194).</summary>
        public static Dictionary<uint, TrafficPattern> ByNameHash { get { Load(); return _byName; } }

        public static TrafficPattern Default => ByKey.TryGetValue(AttribDatabase.Hash("default"), out var p) ? p : null;

        static void Load()
        {
            if (_byKey != null) return;
            _byKey = new Dictionary<uint, TrafficPattern>();
            _byName = new Dictionary<uint, TrafficPattern>();
            GameData.Mount();
            var db = GameData.Attributes;
            uint pv = AttribDatabase.Hash("pvehicle");
            foreach (var c in db.OfClass("trafficpattern"))
            {
                var p = new TrafficPattern
                {
                    Key = c.Key,
                    Name = c.Text("CollectionName") ?? c.Name,
                    SpawnTime = c.Float("SpawnTime", 0, 4f),
                    SpeedStreet = c.Float("SpeedStreet", 0, 35f),
                    SpeedHighway = c.Float("SpeedHighway", 0, 55f),
                };
                if (c.TryGet("Vehicles", out int at, out int count, out var def))
                {
                    var d = c.Source.Data;
                    int stride = def.Size >= 0x18 ? def.Size : 0x18;
                    for (int i = 0; i < count && i < MaxTypes; i++)
                    {
                        int r = at + i * stride;
                        uint key = Chunks.U32(d, r + 4);
                        p.Vehicles.Add(new Record
                        {
                            Vehicle = key,
                            Name = db.Collections.TryGetValue((pv, key), out var v) ? v.Name : null,
                            Rate = Chunks.F32(d, r + 12),
                            MaxInstances = (int)Chunks.U32(d, r + 16),
                            Percent = (int)Chunks.U32(d, r + 20),
                        });
                    }
                }
                p.NameHash = EaHash.Hash(p.Name ?? "");
                _byKey[p.Key] = p;
                _byName[p.NameHash] = p;
            }
        }
    }

    /// <summary>
    /// TRACK_PATH_ZONE_TRAFFIC_PATTERN zones (type 9) from L2RA.BUN chunk 0x80034147 → 0x3414A: TrackPathZone records
    /// (World/TrackPath.hpp:68-84) {i32 type, ..., bbox min/max (game X/Y) at +0x20, u32 Data[0] at +0x30 =
    /// bStringHash(pattern name), u16 point count at +0x40, u16 record size at +0x42, (x, y) points at +0x44}.
    /// </summary>
    public sealed class TrafficZone
    {
        public uint Pattern;
        public float MinX, MinY, MaxX, MaxY;
        public Vector2[] Points;

        public bool Contains(float x, float y)
        {
            if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
            bool inside = false;
            for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
            {
                var a = Points[i]; var b = Points[j];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        static List<TrafficZone> _all;

        public static List<TrafficZone> All
        {
            get
            {
                if (_all != null) return _all;
                _all = new List<TrafficZone>();
                GameData.Mount();
                var w = GameBootstrap.Instance ? GameBootstrap.Instance.World : null;
                var d = w != null && w.Track != null ? w.Track.Bundle : GameFiles.ReadBundle(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN"));
                foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x3414A))
                {
                    int o = c.DataStart;
                    while (o < c.End && d[o] == 0x11) o++;
                    for (int guard = 0; o + 0x44 <= c.End && guard < 100000; guard++)
                    {
                        int size = Chunks.U16(d, o + 0x42), n = Chunks.U16(d, o + 0x40);
                        if (Chunks.I32(d, o) == 9 && o + 0x44 + n * 8 <= c.End)
                        {
                            var z = new TrafficZone
                            {
                                MinX = Chunks.F32(d, o + 0x20), MinY = Chunks.F32(d, o + 0x24), MaxX = Chunks.F32(d, o + 0x28), MaxY = Chunks.F32(d, o + 0x2C),
                                Pattern = Chunks.U32(d, o + 0x30), Points = new Vector2[n],
                            };
                            for (int i = 0; i < n; i++) z.Points[i] = new Vector2(Chunks.F32(d, o + 0x44 + i * 8), Chunks.F32(d, o + 0x48 + i * 8));
                            _all.Add(z);
                        }
                        if (size <= 0) break;
                        o += size;
                    }
                }
                return _all;
            }
        }

        /// <summary>TrackPathManager::FindZone for a game X/Y point (Unity x, z).</summary>
        public static TrafficZone Find(float x, float y)
        {
            foreach (var z in All) if (z.Contains(x, y)) return z;
            return null;
        }
    }

    /// <summary>
    /// AITrafficManager (AI/Activities/AITrafficManager.cpp; specs/traffic_collision.md §A.2-A.5), a task every 0.5 s:
    /// <list type="bullet">
    /// <item>Pattern: the race's TrafficPattern, else the traffic-pattern zone under the players, else the last one
    /// (default at start).</item>
    /// <item>Density d: 0 during an NIS, the race's TrafficLevel % while racing, 1 roaming, ×0.75 in a pursuit.</item>
    /// <item>Spawning: record timers run at dT × rate(d); at most one car per tick while fewer than 10 vehicles of any
    /// kind are active; 150–250 m (+1 s of travel) ahead of a player ±45°, oncoming-biased, on a traffic lane, with
    /// ground, ≥150 m from the players and ≥20 m from other cars (race route only while racing). Cars are pooled; a new
    /// model only every SpawnTime seconds.</item>
    /// <item>Despawn: off the world, or off screen longer than time(d) and further than dist(d) from the camera, never
    /// while any part is in view.</item>
    /// </list>
    /// </summary>
    public sealed class TrafficSystem : MonoBehaviour
    {
        /// <summary>The cap on all active vehicles (player, racers, cops and traffic; NeedsTraffic, :393-403).</summary>
        public const int MaxVehicles = 10;
        /// <summary>Pooled traffic models kept loaded (active + parked) [?: the game's cache is bounded by memory].</summary>
        public int PoolLimit = 16;

        // Misc/Table.cpp lookups over d in [0, 1] (AITrafficManager.cpp:34-48)
        static readonly float[] OffScreenDistance = { 130, 120, 110, 100, 90, 80, 70, 60, 55, 45, 40 };
        static readonly float[] OffScreenTime = { 12, 10, 9, 8, 7, 6.5f, 6, 5.5f, 5, 4.5f, 4 };
        static readonly float[] SpawnRates = { 0, 0.05f, 0.1f, 0.125f, 0.2f, 0.4f, 0.6f, 1, 3, 5, 8 };
        const float TickTime = 0.5f, MphToMps = 0.44704f;

        public float Density { get; private set; }
        public TrafficPattern Pattern { get; private set; }
        public string PatternName => Pattern?.Name;

        readonly List<TrafficCar> _cars = new List<TrafficCar>();
        readonly float[] _timers = new float[TrafficPattern.MaxTypes];
        int _spawnIdx;
        float _newInstanceTimer, _tick, _avoidTick;
        bool _sortDir;

        /// <summary>Every active AIVehicle and the players (AvoidableManager, refreshed every 0.25 s).</summary>
        public static readonly List<PlayerCar> Vehicles = new List<PlayerCar>();
        /// <summary>pvehicles whose model could not be built (skipped from then on).</summary>
        static readonly HashSet<uint> Failed = new HashSet<uint>();

        /// <summary>The active traffic cars.</summary>
        public IEnumerable<PlayerCar> ActiveCars
        {
            get { foreach (var c in _cars) if (c && c.isActiveAndEnabled) yield return c.Car; }
        }

        public int ActiveCount
        {
            get { int n = 0; foreach (var c in _cars) if (c && c.isActiveAndEnabled) n++; return n; }
        }

        void Start() => SetPattern(TrafficPattern.Default);

        void Update()
        {
            if ((_avoidTick -= Time.deltaTime) <= 0)
            {
                _avoidTick = 0.25f;
                RefreshVehicles();
            }
            _tick += Time.deltaTime;
            if (_tick < TickTime) return;
            float dt = _tick;
            _tick = 0;
            Tick(dt);
        }

        static void RefreshVehicles()
        {
            Vehicles.Clear();
            // a traffic car's PlayerCar is disabled (TrafficPhysics drives it): count it while its object is live
            foreach (var pc in FindObjectsByType<PlayerCar>(FindObjectsSortMode.None))
                if (pc && pc.Spec != null) Vehicles.Add(pc);
            TrafficPhysics.TrafficContacts.Refresh(Vehicles);
        }

        /// <summary>AITrafficManager::Update.</summary>
        void Tick(float dt)
        {
            Density = ComputeDensity();
            if (Density > 0 && ChoosePattern())
            {
                float rate = dt * Table(SpawnRates, Density);
                for (int i = 0; i < _timers.Length; i++) _timers[i] += rate;
                _newInstanceTimer += dt;
                SpawnTraffic();
            }
            for (int i = _cars.Count - 1; i >= 0; i--)
            {
                var c = _cars[i];
                if (!c) { _cars.RemoveAt(i); continue; }
                if (c.isActiveAndEnabled && !Validate(c)) c.UnSpawn();
            }
        }

        // ---- density and pattern ----

        /// <summary>ComputeDensity (:625-653).</summary>
        float ComputeDensity()
        {
            if (NisPlayer.Active != null) return 0;   // INIS::Exists
            if (Systems.Police != null && Systems.Police.IsCopRequestPending) return 0;   // ICopMgr::IsCopRequestPending: no traffic while a cop spawn is wanted
            float d = 0;
            var race = RaceManager.Active;
            if (race != null && race.Event != null) d = Mathf.Clamp01(race.Event.TrafficLevel * 0.01f);
            else if (GameSession.Mode == GameMode.FreeRoam || GameSession.Mode == GameMode.Career) d = 1f;
            if (Systems.Police != null && Systems.Police.InPursuit) d *= 0.75f;
            return d;
        }

        /// <summary>ChoosePattern (:536-582).</summary>
        bool ChoosePattern()
        {
            var race = RaceManager.Active;
            if (race != null && race.Event != null)
            {
                var gp = GameSession.Career?.Db;
                if (gp != null && gp.Collections.TryGetValue((AttribDatabase.Hash("gameplay"), race.Event.Key), out var ev))
                {
                    var name = ev.Text("TrafficPattern");
                    if (!string.IsNullOrEmpty(name) && TrafficPattern.ByKey.TryGetValue(AttribDatabase.Hash(name), out var rp))
                    {
                        SetPattern(rp);
                        return Pattern != null;
                    }
                }
            }
            int n = 0;
            var centre = Vector3.zero;
            foreach (var c in Centres()) { centre += c.transform.position; n++; }
            if (n > 0)
            {
                centre /= n;
                // the track point (sim.z, −sim.x) is game (X, Y) = Unity (x, z)
                var zone = TrafficZone.Find(centre.x, centre.z);
                if (zone != null && TrafficPattern.ByNameHash.TryGetValue(zone.Pattern, out var zp)) SetPattern(zp);
            }
            return Pattern != null;
        }

        /// <summary>SetTrafficPattern (:422-434): each record's timer starts at Rate × rand(0..1).</summary>
        void SetPattern(TrafficPattern p)
        {
            if (p == null || p == Pattern) return;
            Pattern = p;
            System.Array.Clear(_timers, 0, _timers.Length);
            for (int i = 0; i < p.Vehicles.Count && i < _timers.Length; i++) _timers[i] = p.Vehicles[i].Rate * Random.value;
        }

        /// <summary>The traffic centres (ITrafficCenter): the human players' cars [?: the game uses each player's view].</summary>
        static IEnumerable<PlayerCar> Centres()
        {
            var gb = GameBootstrap.Instance;
            if (!gb) yield break;
            if (gb.Player && gb.Player.isActiveAndEnabled) yield return gb.Player;
            if (gb.Player2 && gb.Player2.isActiveAndEnabled) yield return gb.Player2;
        }

        // ---- spawning ----

        /// <summary>SpawnTraffic (:336-391).</summary>
        bool SpawnTraffic()
        {
            if (Pattern == null || !NeedsTraffic()) return false;
            if (!FindSpawnPoint(out var nav, out var dir)) return false;
            int rec = NextSpawn();
            if (rec < 0) return false;
            var car = Available(Pattern.Vehicles[rec], _newInstanceTimer > Pattern.SpawnTime);
            if (car == null) return false;
            if (!car.ResetToRoadNav(nav, dir))
            {
                car.UnSpawn();
                return false;
            }
            // StartDriving(min(street, highway) × 0.75 mph), then MSetTrafficSpeed(street, highway)
            car.StartDriving(Mathf.Min(Pattern.SpeedStreet, Pattern.SpeedHighway) * MphToMps * 0.75f, Pattern.SpeedStreet * MphToMps, Pattern.SpeedHighway * MphToMps);
            for (int i = 0; i < Pattern.Vehicles.Count && i < _timers.Length; i++)
                if (Pattern.Vehicles[i].Vehicle == car.VehicleKey) _timers[i] = 0;
            return true;
        }

        /// <summary>NeedsTraffic: fewer than 10 active vehicles of any kind.</summary>
        bool NeedsTraffic()
        {
            RefreshVehicles();
            return Vehicles.Count < MaxVehicles;
        }

        int ActiveOf(uint key)
        {
            int n = 0;
            foreach (var c in _cars) if (c && c.isActiveAndEnabled && c.VehicleKey == key) n++;
            return n;
        }

        /// <summary>
        /// NextSpawn (:283-304): round-robin over the records; one is due when its timer passed Rate, it is under
        /// MaxInstances, and under max(1, budget × Percent / 100) with budget = 10 − the non-traffic vehicles.
        /// </summary>
        int NextSpawn()
        {
            int types = Mathf.Min(Pattern.Vehicles.Count, TrafficPattern.MaxTypes);
            if (types == 0) return -1;
            int budget = MaxVehicles - (Vehicles.Count - ActiveCount);
            int found = -1;
            for (int i = 0; i < types && found < 0; _spawnIdx++, i++)
            {
                _spawnIdx %= types;
                var r = Pattern.Vehicles[_spawnIdx];
                if (!(_timers[_spawnIdx] > r.Rate && r.Rate > 0) || Failed.Contains(r.Vehicle)) continue;
                int count = ActiveOf(r.Vehicle);
                if (count >= r.MaxInstances && r.MaxInstances != 0) continue;
                if (r.Percent != 0 && count >= Mathf.Max(1, Mathf.Max(0, budget) * r.Percent / 100)) continue;
                found = _spawnIdx;
            }
            return found;
        }

        /// <summary>
        /// FindSpawnPoint (:489-534): per centre (alternating order), its forward yawed ±45°, 200 ± 50 m plus its speed
        /// along that line; oncoming with chance lerp(1, 0.5, speed / 50 m/s). The nav must allow traffic there
        /// (CanTrafficSpawn), sit on the race route while racing (CheckRace), and pass FindCollisions.
        /// </summary>
        bool FindSpawnPoint(out RoadNav nav, out Vector3 navDir)
        {
            nav = null;
            navDir = Vector3.forward;
            _sortDir = !_sortDir;
            var centres = new List<PlayerCar>(Centres());
            if (_sortDir) centres.Reverse();
            bool raceOnly = RaceManager.Active != null && RouteMarked();
            foreach (var c in centres)
            {
                var pos = c.transform.position;
                var rb = c.GetComponent<Rigidbody>();
                var vel = rb ? rb.linearVelocity : Vector3.zero;
                var dir = Quaternion.AngleAxis((Random.value * 2f - 1f) * 45f, Vector3.up) * RoadGraph.FlatDir(c.transform.forward);
                float camSpeed = Vector3.Dot(vel, dir);
                float dist = (Random.value * 2f - 1f) * 50f + 200f + Mathf.Max(camSpeed, 0);
                var point = pos + dir * dist;
                float oncoming = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(camSpeed / 50f));
                if (Random.value <= oncoming) dir = -dir;
                var n = RoadNav.NewTraffic(1f);
                if (!n.InitAtPoint(point, dir, false, 1f) || !n.CanTrafficSpawn(dir)) continue;
                if (raceOnly && !RoadGraph.Segs[n.Seg].Has(RoadNetwork.SegInRace)) continue;
                if (FindCollisions(n.Position)) continue;
                nav = n;
                navDir = dir;
                return true;
            }
            return false;
        }

        static int _routeChecked = -1;
        static bool _routeMarked;

        /// <summary>CheckRace applies when the race has its route (InRace segments) marked.</summary>
        static bool RouteMarked()
        {
            int id = RaceManager.Active ? RaceManager.Active.GetInstanceID() : 0;
            if (id == _routeChecked) return _routeMarked;
            _routeChecked = id;
            _routeMarked = false;
            foreach (var s in RoadGraph.Segs) if (s.Has(RoadNetwork.SegInRace)) { _routeMarked = true; break; }
            return _routeMarked;
        }

        /// <summary>FindCollisions (:436-468): true (reject) without world ground there, within 150 m of a centre or 20 m of a car.</summary>
        static bool FindCollisions(Vector3 p)
        {
            if (!TrafficCar.GroundBelow(p + Vector3.up * 3f, 20f, out _)) return true;
            foreach (var c in Centres())
            {
                var d = c.transform.position - p; d.y = 0;
                if (d.sqrMagnitude < 22500f) return true;
            }
            foreach (var v in Vehicles)
            {
                if (!v || !v.gameObject.activeInHierarchy) continue;
                var d = v.transform.position - p; d.y = 0;
                if (d.sqrMagnitude < 400f) return true;
            }
            return false;
        }

        /// <summary>
        /// GetAvailableTrafficVehicle (:306-334): a parked car of that type, else (when allowed) a new one, which resets
        /// the new-instance timer.
        /// </summary>
        TrafficCar Available(TrafficPattern.Record r, bool makeNew)
        {
            foreach (var c in _cars) if (c && !c.isActiveAndEnabled && c.VehicleKey == r.Vehicle) return c;
            if (!makeNew) return null;
            string model = ModelFor(r.Name);
            if (model == null) { Failed.Add(r.Vehicle); return null; }
            if (_cars.Count >= PoolLimit)
            {
                // make room: drop a parked car of another type (the cache evicts unused traffic models)
                var spare = _cars.Find(c => c && !c.isActiveAndEnabled);
                if (spare == null) return null;
                _cars.Remove(spare);
                Destroy(spare.gameObject);
            }
            PlayerCar pc;
            try
            {
                var paint = Color.HSVToRGB(Random.value, Random.Range(0.1f, 0.7f), Random.Range(0.3f, 0.9f));   // [?] traffic paint
                pc = CarSpawner.Spawn(model, paint, null, new Vector3(0, -1000, 0), Quaternion.identity, "Traffic " + r.Name);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"NFSMW traffic: {r.Name} ({model}): {e.Message}");
                Failed.Add(r.Vehicle);
                return null;
            }
            pc.Traffic = true;
            pc.AiControlled = true;
            pc.UseDeviceInput = false;
            pc.transform.SetParent(transform, true);
            // the pvehicle's own behaviours (SuspensionTraffic + EngineTraffic + DamageVehicle; RBTractor's trailer)
            // replace the racer sim (specs/traffic_physics.md); without them the PlayerCar keeps driving it
            TrafficPhysics.TrafficVehicle sim = null;
            TrafficPhysics.TractorHitch hitch = null;
            try
            {
                var specs = TrafficPhysics.VehicleSpecs.Get(r.Name);
                sim = TrafficPhysics.TrafficVehicle.Attach(pc.gameObject, specs, pc, pc.WheelVisuals);
                if (sim != null)
                {
                    TrafficPhysics.TrafficContacts.Register(sim);
                    if (specs.IsTractor) hitch = TrafficPhysics.TractorHitch.Create(sim, Color.HSVToRGB(Random.value, Random.Range(0f, 0.3f), Random.Range(0.6f, 0.95f)));   // [?] trailer paint
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"NFSMW traffic physics: {r.Name}: {e.Message}"); }
            var car = pc.gameObject.AddComponent<TrafficCar>();
            car.Init(pc, r.Vehicle, r.Name, sim, hitch);
            _newInstanceTimer = 0;
            _cars.Add(car);
            if (hitch) hitch.Park();
            pc.gameObject.SetActive(false);
            return car;
        }

        /// <summary>The CARS folder of a pvehicle: its own, or SEMI for the articulated trucks (the trailer comes with RBTractor).</summary>
        static string ModelFor(string vehicle)
        {
            if (string.IsNullOrEmpty(vehicle)) return null;
            string up = vehicle.ToUpperInvariant();
            if (Directory.Exists(Path.Combine(GameData.Root, "CARS", up))) return up;
            if (up.StartsWith("SEMI") && Directory.Exists(Path.Combine(GameData.Root, "CARS", "SEMI"))) return "SEMI";
            return null;
        }

        // ---- despawn ----

        /// <summary>ValidateVehicle (:584-623).</summary>
        bool Validate(TrafficCar c)
        {
            bool invalid = c.OffWorld;
            if (!invalid && c.OffscreenTime > Table(OffScreenTime, Density))
            {
                var cam = Camera.main;
                var from = cam ? cam.transform.position : c.transform.position;
                invalid = Vector3.Distance(from, c.transform.position) > Table(OffScreenDistance, Density);
            }
            if (invalid && c.InView) return true;   // PartChecker: a part still in view keeps it
            return !invalid;
        }

        /// <summary>FlushAllTraffic (:676-700): unspawns everything (release: destroys the pool too).</summary>
        public void FlushAll(bool release = false)
        {
            foreach (var c in _cars)
            {
                if (!c) continue;
                if (release) Destroy(c.gameObject);
                else if (c.isActiveAndEnabled) c.UnSpawn();
            }
            if (release) _cars.Clear();
            if (Pattern != null)
                for (int i = 0; i < Pattern.Vehicles.Count && i < _timers.Length; i++) _timers[i] = Pattern.Vehicles[i].Rate * Random.value;
        }

        void OnDestroy()
        {
            foreach (var c in _cars) if (c) Destroy(c.gameObject);
            _cars.Clear();
        }

        /// <summary>Table (Misc/Table.cpp:5-20): equally spaced samples over [0, 1], linear, clamped.</summary>
        public static float Table(float[] t, float x)
        {
            float p = Mathf.Clamp01(x) * (t.Length - 1);
            int i = Mathf.Min((int)p, t.Length - 2);
            return Mathf.Lerp(t[i], t[i + 1], p - i);
        }
    }

    /// <summary>
    /// One traffic car: AIVehicleTraffic + AIGoalTraffic's AIActionTraffic (AI/Common/AIVehicleTraffic.cpp,
    /// AI/Actions/AIActionTraffic.cpp; specs/traffic_collision.md §A.6-A.8).
    /// <list type="bullet">
    /// <item>Every 0.1 s (staggered): the nav runs lerp(10, 30, speed/25) + speed·dT + radius ahead in its lane (30 m
    /// while occluded by a car), the trail is cut around other cars, and the aim is the string-pulled point.</item>
    /// <item>Speed: 35 mph, 55 mph on roads with 4+ traffic lanes; under sqrt(0.6·g/|κ|) of the trail; behind a car,
    /// its speed scaled by the gap over a stopping distance; at most +2 m/s per second; 0 at a dead end.</item>
    /// <item>Every tick: steering asin(fwd × dir)/max steer, bang-bang gas and brake (AIVehicle.cpp:841-967).</item>
    /// <item>A hit by the player, a cop or a racer within 80 m of the camera: 3 updates of full steer with no pedals,
    /// then braked with full steer for good (tractors brake at once). Horns per CARSFX_TrafficHorn.</item>
    /// </list>
    /// </summary>
    public sealed class TrafficCar : MonoBehaviour
    {
        public PlayerCar Car;
        /// <summary>The simplified vehicle model driving this car (null: the racer model still does).</summary>
        public TrafficPhysics.TrafficVehicle Sim;
        /// <summary>A semi's trailer hitch (RBTractor).</summary>
        public TrafficPhysics.TractorHitch Hitch;
        public uint VehicleKey;
        public string VehicleName;
        public RoadNav Nav { get; private set; }
        public Vector3 DriveTarget { get; private set; }
        public float DriveSpeed { get; private set; }
        public float OffscreenTime { get; private set; }
        public bool OffWorld { get; private set; }
        /// <summary>Articulated truck (VehicleClass TRACTOR): no panic steering.</summary>
        public bool Tractor;
        public enum AccidentState { None, InProgress, Over }
        public AccidentState Accident { get; private set; }

        Rigidbody _rb;
        Renderer[] _renderers;
        Vector3 _dim = new Vector3(0.9f, 0.7f, 2.3f);
        float _radius = 2.5f, _street = 35f * 0.44704f, _highway = 55f * 0.44704f, _think, _accidentTimer, _groundTimer;
        bool _fixedSpeed, _steeringBehind;
        int _hornType;
        static float _stagger;
        const float UpdateRate = 0.1f;
        readonly List<RoadNav.Avoidable> _avoid = new List<RoadNav.Avoidable>();
        static readonly RaycastHit[] Hits = new RaycastHit[16];

        /// <summary>Any part in view, the trailer included (AITrafficManager's PartChecker covers IArticulatedVehicle).</summary>
        public bool InView
        {
            get
            {
                foreach (var r in _renderers) if (r && r.isVisible) return true;
                if (Hitch && Hitch.TrailerRenderers != null && Hitch.Trailer && Hitch.Trailer.gameObject.activeInHierarchy)
                    foreach (var r in Hitch.TrailerRenderers) if (r && r.isVisible) return true;
                return false;
            }
        }

        public void Init(PlayerCar car, uint key, string name, TrafficPhysics.TrafficVehicle sim = null, TrafficPhysics.TractorHitch hitch = null)
        {
            Car = car;
            Sim = sim;
            Hitch = hitch;
            VehicleKey = key;
            VehicleName = name;
            Tractor = sim != null ? sim.Specs.IsTractor : name != null && name.StartsWith("semi");
            _rb = car.GetComponent<Rigidbody>();
            _renderers = car.GetComponentsInChildren<Renderer>(true);
            var box = car.GetComponent<BoxCollider>();
            if (box) { _dim = box.size * 0.5f; _radius = _dim.magnitude; }
            // the RigidBody dimension of the collision geometry when there is one (avoidance and spacing use it)
            if (sim != null && sim.Specs.HasBounds) { _dim = sim.Specs.Dimension; _radius = _dim.magnitude; }
            var pv = GameData.Attributes?.Get("pvehicle", name ?? "");
            _hornType = pv != null ? pv.Int("HornType", 0, 0) & 0xFF : 0;   // a one-byte field; Int() reads four
            // AIVehicleTraffic: updates staggered by 0.1 s per car
            _think = _stagger;
            _stagger += UpdateRate;
            if (_stagger >= 1f) _stagger = 0;
        }

        /// <summary>ResetVehicleToRoadNav: on the ground at the nav point, facing along the lane.</summary>
        public bool ResetToRoadNav(RoadNav spawn, Vector3 dir)
        {
            var pos = spawn.Position;
            if (!GroundBelow(pos + Vector3.up * 3f, 20f, out float y)) return false;   // the nav sits on the road: not a deck above it
            pos.y = y;
            gameObject.SetActive(true);
            _rb.isKinematic = false;
            var rot = Quaternion.LookRotation(spawn.Forward, Vector3.up);
            Car.Teleport(pos + Vector3.up * 0.4f, rot);
            Car.SetResetPoint(pos, rot);
            // PlaceObject + Reset: the wheels find the road; RBTractor::PlaceObject re-hitches and poses the trailer
            if (Sim) Sim.ResetPhysics();
            if (Hitch) Hitch.Place();
            // the spawn nav becomes the car's drive nav, already in its lane (ResetDriveToNav(SELECT_VALID_LANE))
            Nav = spawn;
            Nav.SetTrafficVehicle(_dim.x);
            return true;
        }

        /// <summary>StartDriving + MSetTrafficSpeed: the start speed, the posted speeds, damage and state reset.</summary>
        public void StartDriving(float speed, float street, float highway, bool fix = false)
        {
            _street = street;
            _highway = highway;
            _fixedSpeed = fix;
            Accident = AccidentState.None;
            _accidentTimer = 0;
            OffscreenTime = 0;
            OffWorld = false;
            _rb.isKinematic = false;
            _rb.linearVelocity = Car.transform.forward * speed;
            DriveSpeed = speed;
            Car.Input = new DriveInput();
            if (Sim)
            {
                // SetSpeed → MatchSpeed on the engine and the wheels; the damage of the last life is gone
                Sim.ResetDamage();
                Sim.MatchSpeed(speed);
                if (Hitch && Hitch.Trailer)
                {
                    Hitch.Trailer.Rb.linearVelocity = _rb.linearVelocity;
                    Hitch.Trailer.MatchSpeed(speed);
                    Hitch.Trailer.ResetDamage();
                }
            }
            UpdateNavPos(30f, 0f, speed);   // BeginAction: UpdateNavPos(30)
            DriveTarget = Nav.OccludedPosition;
        }

        /// <summary>AIVehicle::UnSpawn: parked in the pool.</summary>
        public void UnSpawn()
        {
            StopHorn();
            Car.Input = new DriveInput();
            if (Hitch) Hitch.Park();
            gameObject.SetActive(false);
        }

        void Update()
        {
            if (Car == null) return;
            OffscreenTime = InView ? 0 : OffscreenTime + Time.deltaTime;
            Horn(Time.deltaTime);
        }

        void FixedUpdate()
        {
            if (Car == null || Car.Spec == null || Nav == null) return;
            float dt = Time.fixedDeltaTime;
            _think += dt;
            if (_think >= UpdateRate)
            {
                Think(_think);
                _think = 0;
            }
            if ((_groundTimer -= dt) <= 0) { _groundTimer = 0.5f; CheckGround(); }
            Drive();
        }

        /// <summary>No world ground under the car (its section is not loaded): held in place, and off the world [?].</summary>
        void CheckGround()
        {
            bool ground = GroundBelow(transform.position + Vector3.up * 3f, 60f, out _);
            OffWorld = !ground || transform.position.y < -200f;
            if (!ground && !_rb.isKinematic) _rb.isKinematic = true;
            else if (ground && _rb.isKinematic) _rb.isKinematic = false;
        }

        /// <summary>The nearest world-collision ground below a point (GetWorldHeightAtPointRigorous).</summary>
        public static bool GroundBelow(Vector3 from, float range, out float y)
        {
            y = 0;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, Hits, range, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (Hits[i].distance < best && WorldCollision.IsGround(Hits[i].collider)) { best = Hits[i].distance; y = Hits[i].point.y; }
            return best < float.MaxValue;
        }

        /// <summary>AIActionTraffic::Update (:302-388).</summary>
        void Think(float dT)
        {
            var pos = _rb.position;
            float speed = _rb.linearVelocity.magnitude;
            float t = Mathf.Clamp01(speed / 25f);
            if (Nav.OccludedByAvoidable) t = 1f;
            float look = Mathf.Lerp(10f, 30f, t) + speed * dT + _radius;
            UpdateNavPos(look, dT, speed);
            float desired = ComputeSpeed(speed, dT);
            DriveTarget = Nav.OccludedPosition;
            DriveSpeed = Nav.Valid ? desired : 0;
            if (Accident == AccidentState.InProgress && (_accidentTimer -= 1f) <= 0)
            {
                _accidentTimer = 0;
                Accident = AccidentState.Over;
            }
        }

        /// <summary>UpdateNavPos (:253-296): keep the nav lookahead in front, then the occluded aim with the avoidables.</summary>
        void UpdateNavPos(float look, float dT, float speed)
        {
            var pos = _rb.position;
            if (!Nav.DeadEnd)
            {
                float distToNav = Vector3.Dot(RoadGraph.FlatDir(Nav.Forward), Nav.Position - pos);
                if (distToNav < look) Nav.IncTrafficPosition(look - distToNav, look);
            }
            GatherAvoidables(Mathf.Max(look, 2f * speed));
            var tr = Car.transform;
            Nav.UpdateTrafficOccludedPosition(pos, RoadGraph.FlatDir(tr.forward), RoadGraph.RightOf(RoadGraph.FlatDir(tr.forward)), _rb.linearVelocity, _dim, _avoid);
        }

        /// <summary>FetchAvoidables: the cars whose sweep circles overlap this one's (default sweep 20 m, AIVehicle.cpp:50).</summary>
        void GatherAvoidables(float sweep)
        {
            _avoid.Clear();
            var me = _rb.position;
            foreach (var v in TrafficSystem.Vehicles)
            {
                if (!v || v == Car || !v.gameObject.activeInHierarchy) continue;
                var rb = v.GetComponent<Rigidbody>();
                var vel = rb ? rb.linearVelocity : Vector3.zero;
                var p = v.transform.position;
                float his = Mathf.Max(20f, 2f * vel.magnitude), r = sweep + his;
                var d = p - me; d.y = 0;
                if (d.sqrMagnitude > r * r) continue;
                var box = v.GetComponent<BoxCollider>();
                var f = RoadGraph.FlatDir(v.transform.forward);
                _avoid.Add(new RoadNav.Avoidable
                {
                    Position = p, Forward = f, Right = RoadGraph.RightOf(f), Velocity = vel,
                    Dimension = box ? box.size * 0.5f : new Vector3(0.9f, 0.7f, 2.3f),
                });
            }
        }

        // aAIStoppingDist: 3 m at 0 to 50 m at 80 m/s
        static float StoppingDistance(float x) => Mathf.Lerp(3f, 50f, Mathf.Clamp01(x / 80f));

        /// <summary>ComputeSpeed (:205-251).</summary>
        float ComputeSpeed(float current, float dT)
        {
            if (Nav.DeadEnd || !Nav.Valid) return 0;
            float posted = RoadGraph.SegmentTrafficLanes(RoadGraph.Segs[Nav.Seg]) >= 4 ? _highway : _street;
            float desired = posted;
            if (!_fixedSpeed)
            {
                float k = Nav.CookieTrailCurvature(_rb.position, _rb.linearVelocity);
                desired = Mathf.Min(posted, SpeedLimitForCurvature(0.6f, k, posted));
                if (Nav.OccludedByAvoidable && !Nav.OccludedFromBehind)
                {
                    float mass = _rb.mass * (Tractor ? 2f : 1f);
                    float myLength = 2f * _radius;
                    float dist = Mathf.Max(Vector3.Distance(Nav.ApexPosition, _rb.position) - myLength, 0);
                    float stop = StoppingDistance(current * Mathf.Max(mass * 0.0005f, 1f));
                    if (dist < stop)
                    {
                        desired = Mathf.Clamp(Nav.OccludingTrailSpeed * dist / stop, 0, desired);
                        desired = Mathf.Min(desired, current);
                    }
                }
            }
            return Mathf.Min(desired, current + dT + dT);
        }

        /// <summary>GetSpeedLimitForCurvature (AIActionRace.cpp:466-473).</summary>
        static float SpeedLimitForCurvature(float friction, float curvature, float top)
        {
            float side = friction * 9.8f;
            return Mathf.Sqrt(side / Mathf.Max(side / Mathf.Max(1e-3f, top * top), Mathf.Abs(curvature)));
        }

        /// <summary>The accident inputs, else AIVehicle::OnSteering and OnGasBrake.</summary>
        void Drive()
        {
            var input = new DriveInput();
            float speed = Car.ForwardSpeed;
            if (Accident == AccidentState.InProgress)
            {
                if (!Tractor) input.Steer = 1f;   // SetControlSteering(1) [?: + taken as right]
                Car.Input = input;
                return;
            }
            if (Accident == AccidentState.Over)
            {
                input.Brake = 1f;
                // the racer model would select reverse on the brake; EngineTraffic never does
                if (Sim == null) input.Handbrake = Mathf.Abs(speed) < 2.5f;
                if (!Tractor) input.Steer = 1f;
                Car.Input = input;
                return;
            }
            // OnSteering (AIVehicle.cpp:841-895)
            var pos = Car.transform.position;
            var fwd = RoadGraph.FlatDir(Car.transform.forward);
            var dir = RoadGraph.FlatDir(DriveTarget - pos);
            float speedXZ = new Vector2(_rb.linearVelocity.x, _rb.linearVelocity.z).magnitude;
            _steeringBehind = false;
            bool reversing = Sim != null && Sim.Engine != null ? Sim.Engine.IsReversing : Car.Gear < 0;
            if (!(DriveSpeed == 0 && speedXZ < 1f))
            {
                // ISuspension::GetMaxSteering: 45° for SuspensionTraffic
                float maxSteer = (Sim != null ? Mathf.Max(5f, Sim.MaxSteeringDeg) : Mathf.Max(5f, Car.Spec.Steering * 45f)) * Mathf.Deg2Rad;
                float steer = Mathf.Asin(Mathf.Clamp(RoadGraph.Cross2(dir, fwd), -1f, 1f)) / maxSteer;   // cross(fwd, dir).y
                if (reversing) steer = -steer;
                else if (Vector3.Dot(dir, fwd) < -0.2f) { steer = steer < 0 ? -1f : 1f; _steeringBehind = true; }
                input.Steer = Mathf.Clamp(steer, -1f, 1f);
            }
            // OnGasBrake (AIVehicle.cpp:897-967): a shocked traffic car drops to neutral and lets go of the pedals
            if (Sim != null && Sim.Engine != null)
            {
                var eng = Sim.Engine;
                bool shock = Sim.InShock, neutral = eng.Gear == TrafficPhysics.Chassis.GNeutral;
                if (shock ^ neutral) eng.Shift(shock ? TrafficPhysics.Chassis.GNeutral : reversing ? TrafficPhysics.Chassis.GReverse : TrafficPhysics.Chassis.GFirst);
                if (shock) { Car.Input = input; return; }
            }
            // bang-bang
            float desired = DriveSpeed;
            if (_steeringBehind) { input.Throttle = 1f; input.Handbrake = true; }
            else if (desired < 0.5f) input.Brake = 1f;
            else if (reversing) { if (speed < -1f) input.Brake = 1f; else input.Throttle = 1f; }
            else if (speed < -1f) input.Brake = 1f;
            else if (desired < speed) { if (speed - desired > 2.5f || desired < 5f) input.Brake = 1f; }
            else input.Throttle = 1f;
            // the racer model's brake selects reverse at a stop: hold that car instead (EngineTraffic has no auto-reverse)
            if (Sim == null && input.Brake > 0 && input.Throttle == 0 && speed < 2.5f) input.Handbrake = true;
            Car.Input = input;
        }

        /// <summary>AIActionTraffic::OnCollision / OnAccident (:121-151).</summary>
        void OnCollisionEnter(Collision c)
        {
            var other = c.rigidbody ? c.rigidbody.GetComponent<PlayerCar>() : null;
            if (other == null || other == Car || other.Traffic) return;   // human, cop or racer only
            var cam = Camera.main;
            var at = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            if (cam && Vector3.Distance(cam.transform.position, at) > 80f) return;
            if (other.SpeedMps < 5f * 0.44704f && Accident == AccidentState.Over) return;
            if (Tractor) { Accident = AccidentState.Over; _accidentTimer = 0; }
            else { Accident = AccidentState.InProgress; _accidentTimer = 3f; }
        }

        // ---- horn (EAXSound/CARSFX/CARSFX_TrafficFX.cpp:8-38, 164-317) ----

        static float _lastHonk = -100f;
        AudioSource _horn;
        float _hornStart, _hornLength, _lastHorn = -100f, _lastAttempt = -100f, _fade;
        bool _honking, _ending;

        void Horn(float dt)
        {
            float now = Time.time;
            if (!_honking)
            {
                if (_lastHorn + 3f >= now || _lastAttempt + 3f >= now) return;
                var gb = GameBootstrap.Instance;
                if (!gb) return;
                foreach (var p in new[] { gb.Player, gb.Player2 })
                {
                    if (!p || p.ForwardSpeed <= 10f || !PlayerInRange(p)) continue;
                    if (Random.value < 0.3f || _lastHonk + 5f < now) { BeginHonk(now); break; }
                    _lastAttempt = now;
                }
                return;
            }
            if (!_ending && now >= _hornStart + _hornLength) { _ending = true; _fade = 1f; }
            if (_ending)
            {
                _fade -= dt / 0.15f;   // 150 ms linear fade
                if (_horn) _horn.volume = Mathf.Max(0, _fade) * 0.8f;
                if (_fade <= 0) StopHorn();
            }
        }

        /// <summary>IsPlayerCarInRange: within 20 m, under 3 m always; ±40° ahead of the nose, or behind within 10 m (±115° in drag races).</summary>
        bool PlayerInRange(PlayerCar p)
        {
            var d = p.transform.position - transform.position;
            float dist = d.magnitude;
            if (dist > 20f) return false;
            if (dist < 3f) return true;
            float dot = Vector3.Dot(RoadGraph.FlatDir(d), RoadGraph.FlatDir(transform.forward));
            bool drag = RaceManager.Active != null && RaceManager.Active.Event != null && RaceManager.Active.Event.Type == RaceType.Drag;
            float c40 = Mathf.Cos(40f * Mathf.Deg2Rad);
            return drag ? dot >= Mathf.Cos(115f * Mathf.Deg2Rad) : dot > c40 || (dot < -c40 && dist < 10f);
        }

        void BeginHonk(float now)
        {
            // CARSFX_TrafficHorn plays ENV_STATIC(HornType, ENV_COMMON): the bank's own ENV_STATIC select table maps the
            // stitch index to a slot (5 → 3, 6 → 4, 11 → 5; audio.md §11), as the fountains use; TRAFFIC_MB stays a fallback
            var es = SoundLibrary.Patch("IG_GLOBAL/ENV_COMMON_MB.abk").Select("ENV_STATIC");
            var clip = es != null && _hornType < es.Length ? SoundLibrary.ClipAt("IG_GLOBAL/ENV_COMMON_MB.abk", es[_hornType]) : null;
            if (!clip)
            {
                const string Bank = "IG_GLOBAL/TRAFFIC_MB.abk";
                int n = SoundLibrary.Count(Bank);
                if (n == 0) return;
                clip = SoundLibrary.Clip(Bank, Mathf.Max(0, _hornType - 1) % n);
            }
            if (!_horn)
            {
                var go = new GameObject("Horn");
                go.transform.SetParent(transform, false);
                _horn = go.AddComponent<AudioSource>();
                _horn.spatialBlend = 1f;
                _horn.minDistance = 5f;
                _horn.maxDistance = 120f;
                _horn.loop = true;
                _horn.playOnAwake = false;
            }
            _horn.clip = clip;
            if (!_horn.clip) return;
            _horn.volume = 0.8f;
            _horn.Play();
            _honking = true;
            _ending = false;
            _hornStart = now;
            _hornLength = Random.Range(1.2f, 4f);
            _lastHonk = now;
        }

        void StopHorn()
        {
            if (_horn) _horn.Stop();
            if (_honking) _lastHorn = Time.time;
            _honking = _ending = false;
        }

        void OnDisable() => StopHorn();

        void OnDrawGizmosSelected()
        {
            if (Nav == null || !Nav.Valid) return;
            Gizmos.color = Color.cyan;
            for (int i = 0; i < Nav.CookieCount; i++) { var c = Nav.CookieAt(i); Gizmos.DrawLine(c.Left, c.Right); }
            Gizmos.color = Nav.OccludedByAvoidable ? Color.red : Color.green;
            Gizmos.DrawSphere(DriveTarget, 0.5f);
        }
    }
}
