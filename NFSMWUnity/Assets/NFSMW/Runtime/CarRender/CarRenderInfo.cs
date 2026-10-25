using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.CarRender
{
    /// <summary>
    /// CarRenderInfo (World/CarRender.cpp; exe ctor 0x75F5D0) on a built car: the replacement-texture state
    /// (composite skin, carbon hood, rim composite, light-state textures, window damage, decals), the light materials of
    /// the paint / tint / rim paint, the DAMAGE0 overlays shown by SetPlayerDamage (0x742680) and the cop cars'
    /// VehiclePartDamageBehaviour (0x75F0A0). CarBuilder registers every drawn part through <see cref="AddPart"/>.
    /// </summary>
    public sealed class CarRenderInfo : MonoBehaviour
    {
        /// <summary>VehicleFX::ID light bits (CarLightsStateMap, the exe tests 8/0x10/0x20 for the brake lights at 0x737F86).</summary>
        public const int LightLHead = 1, LightRHead = 2, LightCHead = 4, LightLBrake = 8, LightRBrake = 0x10, LightCBrake = 0x20;
        /// <summary>VehicleFX::ID (World/VehicleFX.h): reverse lights and the cop light bar; LIGHT_COPS = the three cop bits.</summary>
        public const int LightLReverse = 0x40, LightRReverse = 0x80, LightCopRed = 0x1000, LightCopBlue = 0x2000, LightCopWhite = 0x4000,
            LightCops = 0x7000, LightAll = 0x7FFF;

        /// <summary>ForceBrakelightsOn (0x9B0930), a debug tweak.</summary>
        public static bool ForceBrakelightsOn;

        public string Car;
        public CarLook Look;
        public bool Cop, Traffic;
        /// <summary>mOnLights / mBrokenLights (CarRenderConn::Update 0x7564C0 copies them from the sim each frame).</summary>
        public int OnLights, BrokenLights;
        /// <summary>The DamageZone.Info last applied (SetDamageInfo).</summary>
        public uint DamageInfo { get; private set; }

        enum Kind { Shared, Skin, CarbonSkin, Brake, BrakeGlass, Headlight, HeadlightGlass, Glass, Decal, Wheel, LightMat, Tire }

        sealed class Surface
        {
            public Renderer R;
            public int Index;
            public SolidPart Part;
            public string Group;     // "hood", "wheel", "spoiler", "roof", "decal:<mount>", "" …
            public Kind Kind;
            public int Side;         // light side: 0 left, 1 right, 2 centre
            public int DecalSlot;    // decal texture slot (83..130) of a decal-mount surface
            public Material Own;     // per-car instance
        }

        readonly List<Surface> _surfaces = new List<Surface>();
        readonly List<Surface> _brakes = new List<Surface>();
        readonly List<(LodPart part, int zone)> _damage0 = new List<(LodPart, int)>();
        readonly Dictionary<int, (LodPart intact, LodPart damaged, Solid damagedSolid, int level)> _copParts = new Dictionary<int, (LodPart, LodPart, Solid, int)>();
        readonly HashSet<int> _mountsTried = new HashSet<int>();
        CarAsset _asset;
        readonly Dictionary<uint, bool> _glassDamaged = new Dictionary<uint, bool>();
        MeshFactory _factory;
        Transform _body;
        string _lookKey;
        Texture2D _skin, _wheel;
        PlayerCar _car;
        CarDamage _damage;
        int _appliedLights = -1;

        // ---- names (CarRender.cpp:1196-1290, CarInfo.cpp:1686-1830) -----------------------------------------------
        static readonly uint HeadL = EaHash.Hash("HEADLIGHT_LEFT"), HeadR = EaHash.Hash("HEADLIGHT_RIGHT"),
            HeadGlassL = EaHash.Hash("HEADLIGHT_GLASS_LEFT"), HeadGlassR = EaHash.Hash("HEADLIGHT_GLASS_RIGHT"),
            BrakeL = EaHash.Hash("BRAKELIGHT_LEFT"), BrakeR = EaHash.Hash("BRAKELIGHT_RIGHT"), BrakeC = EaHash.Hash("BRAKELIGHT_CENTRE"),
            BrakeGlassL = EaHash.Hash("BRAKELIGHT_GLASS_LEFT"), BrakeGlassR = EaHash.Hash("BRAKELIGHT_GLASS_RIGHT"), BrakeGlassC = EaHash.Hash("BRAKELIGHT_GLASS_CENTRE");
        static readonly HashSet<uint> GlassNames = new HashSet<uint>(new[] { "WINDOW_FRONT", "WINDOW_REAR", "WINDOW_LEFT_FRONT", "WINDOW_LEFT_REAR",
            "WINDOW_RIGHT_FRONT", "WINDOW_RIGHT_REAR", "REAR_DEFROSTER" }.Select(n => EaHash.Hash(n)));
        static readonly HashSet<uint> DecalNames = new HashSet<uint>(CarRenderTables.DecalNames.Select(n => EaHash.Hash(n)));
        static readonly uint[] DecalDummies = CarRenderTables.DecalDummies.Select(n => EaHash.Hash(n)).ToArray();

        uint SkinHash => EaHash.Hash(Car + "_SKIN1");
        uint SkinBHash => EaHash.Hash(Car + "_SKIN1B");
        uint WheelHash => Look?.Rim != null && Look.Rim.TextureHash != 0 ? EaHash.Hash("_WHEEL", Look.Rim.TextureHash) : 0;

        /// <summary>Creates the component on a car root before its parts are built.</summary>
        public static CarRenderInfo Begin(GameObject root, string car, OwnedCar look, Color paint, MeshFactory factory, Transform body)
        {
            var info = root.AddComponent<CarRenderInfo>();
            info.Car = car.ToUpperInvariant();
            info._factory = factory;
            info._body = body;
            info.Look = CarLook.For(info.Car, look, paint);
            info.Cop = info.Look.Type != null && info.Look.Type.UsageType == 1;
            info.Traffic = info.Look.Type != null && info.Look.Type.UsageType == 2;
            info._lookKey = info.Look.Key;
            info.BuildLookTextures();
            return info;
        }

        void BuildLookTextures()
        {
            var oldSkin = _skin;
            var oldWheel = _wheel;
            _skin = null;
            _wheel = null;
            BuildComposites();
            CarSurfaces.Acquire(_skin);
            CarSurfaces.Acquire(_wheel);
            CarSurfaces.Release(oldSkin);
            CarSurfaces.Release(oldWheel);
        }

        void OnDestroy()
        {
            CarSurfaces.Release(_skin);
            CarSurfaces.Release(_wheel);
            _skin = _wheel = null;
        }

        void BuildComposites()
        {
            if (!Look.Skinnable) return;
            try
            {
                string vinyl = Look.VinylTexture;
                // a vinyl part with NUMCOLOURS 0 makes CompositeSkin return early (no composite) [CarSkin.cpp:745]
                if (Look.Vinyl != null && Look.Vinyl.I(CarSurfaces.NumColours) == 0) vinyl = null;
                _skin = CarSurfaces.Skin(Car, Look.BaseColour, vinyl, Look.VinylRemap, Look.RemapColours);
                if (Look.Rim != null && Look.Rim.TextureHash != 0)
                    _wheel = CarSurfaces.Wheel(Look.Rim.TextureHash, Look.RimPaint != null ? CarSurfaces.PartColour(Look.RimPaint) : 0xFFFFFFFF);
            }
            catch (Exception e) { Debug.LogWarning($"NFSMW car render: composite {Car}: {e.Message}"); }
        }

        // ---- parts and LODs ---------------------------------------------------------------------------------------

        /// <summary>
        /// One CarPartModel column: a slot's model at LOD A..E (UpdateCarParts 0x755800). The object of a LOD is built on
        /// first use; a LOD without a model draws nothing (mCarPartModels[slot][lod] null), as in the exe.
        /// </summary>
        sealed class LodPart
        {
            public readonly Solid[] Lods = new Solid[5];
            public readonly GameObject[] Objects = new GameObject[5];
            public Transform Parent;
            public string Group;
            public int DecalMount = -1;
            public int MinLod, MaxLod = 4;
            public bool Visible = true;
            public bool NoShadows;
            public int Shown = -1;
            public bool Pending;
            public GameObject First;
        }

        readonly List<LodPart> _lodParts = new List<LodPart>();
        readonly List<LodPart> _driverParts = new List<LodPart>();
        readonly List<Renderer> _brakeRenderers = new List<Renderer>(), _tireRenderers = new List<Renderer>();
        /// <summary>mMinLodLevel on PC (RideInfo::Init 0x739A70): the car-detail option's maximum minus the chosen level
        /// ([0x901880] - [0x9017F8]); 0 = LOD A at full detail (g_CarLodLevel = max at the top preset).</summary>
        public static int MinLodLevel = 0;

        /// <summary>
        /// A wheel or brake slot's models A..E (UpdateCarParts 0x755800: FRONT_WHEEL 66 / REAR_WHEEL 67 and the brakes 24 /
        /// 34, one model per LOD; an empty rear slot takes the front model). CarRenderInfo::Render draws the tyre at the
        /// body LOD while car_body_lod &lt;= mMaxTireLodLevel (D) and the brakes while &lt;= mMaxBrakeLodLevel (B); a rear
        /// wheel whose rim face points away from the camera (TireFace 0x738440: the wheel model's +Y axle axis . the camera
        /// direction &lt; FaceCos 0) at LOD A draws its B model instead (not in the FE, not during an NIS).
        /// </summary>
        sealed class WheelLod
        {
            public readonly Solid[] Lods = new Solid[5];
            public readonly GameObject[] Objects = new GameObject[5];
            public GameObject First;
            public bool Rear, Brake;
            public int Shown;
        }

        readonly List<WheelLod> _wheelLods = new List<WheelLod>();
        MaterialPropertyBlock _envBlock;
        bool _envOff;
        float _pixelSize = 1000f;
        /// <summary>The body LOD drawn now (0 = A); CarRenderInfo::Render picks it from the projected size.</summary>
        public int BodyLod { get; private set; }
        /// <summary>Whether the distance LOD runs (in-game cars only; the front-end showroom keeps LOD A) [ours].</summary>
        public bool LodEnabled = true;

        static Camera[] _camBuffer = new Camera[8];
        static int _camCount, _camFrame = -1;
        static readonly Dictionary<Solid, MeshFactory.Built> MeshCache = new Dictionary<Solid, MeshFactory.Built>();
        static readonly Queue<Solid> MeshOrder = new Queue<Solid>();
        static readonly Queue<(CarRenderInfo info, LodPart part, int lod)> BuildQueue = new Queue<(CarRenderInfo, LodPart, int)>();
        const int MeshCacheSize = 800, BuildsPerFrame = 6;

        /// <summary>Car part meshes are shared between cars of the same model (the exe shares the model's solids).</summary>
        MeshFactory.Built Mesh(Solid s)
        {
            if (s == null) return null;
            if (MeshCache.TryGetValue(s, out var b) && b != null && b.Mesh) return b;
            b = _factory.Build(s, PartKind.Car);
            if (b == null) return null;
            MeshCache[s] = b;
            MeshOrder.Enqueue(s);
            while (MeshOrder.Count > MeshCacheSize) MeshCache.Remove(MeshOrder.Dequeue());
            return b;
        }

        /// <summary>Builds (shares) a mesh for CarBuilder's wheels and brakes.</summary>
        public MeshFactory.Built SharedMesh(Solid s) => Mesh(s);

        /// <summary>
        /// Builds a slot's solid as a child of parent with the car materials. lods = the asset holding the slot's _B.._E
        /// models (null = LOD A only). Returns the object first shown (null when it has no geometry).
        /// </summary>
        public GameObject AddPart(Solid s, Transform parent, string group = "", CarAsset lods = null, int decalMount = -1)
        {
            var lp = new LodPart { Parent = parent, Group = group, DecalMount = decalMount };
            lp.Lods[0] = s;
            if (lods != null && s.Name.EndsWith("_A"))
            {
                string b = s.Name.Substring(0, s.Name.Length - 1);
                for (int k = 1; k < 5; k++)
                {
                    string n = b + (char)('A' + k);
                    lp.Lods[k] = lods.Solids.Find(x => x.Name == n);
                }
            }
            // GetSpecialLODRangeForCarSlot (PC 0x739B40): decal mounts A only. With mMinLodLevel == 0 (the car-detail option
            // at full detail, the default) INTERIOR has no special range (it follows the body A..E) and DRIVER draws
            // (FE ? mMinFELodLevel : A)..C; with a lower option: special behaviour 2 -> A..A, 1 -> A.., else (FE ? B : C)..
            // (driver C | interior E) as the GC decomp. LICENSE_PLATE (69) draws up to mMaxLicenseLodLevel B (Render).
            if (decalMount >= 0 || group.StartsWith("decal")) lp.MaxLod = 0;
            if (group == "interior" && MinLodLevel > 0) lp.MinLod = LodEnabled ? 2 : 1;
            if (group == "driver") { lp.MinLod = MinLodLevel > 0 ? (LodEnabled ? 2 : 1) : 0; lp.MaxLod = 2; _driverParts.Add(lp); }
            if (group == "license") lp.MaxLod = 1;
            if (decalMount >= 0 || group == "damage0" || group.StartsWith("decal")) lp.NoShadows = true;
            _lodParts.Add(lp);
            int target = Math.Max(lp.MinLod, Math.Min(lp.MaxLod, BodyLod));
            // the first object: the target LOD, else the nearest model that exists
            int first = lp.Lods[target] != null ? target : Array.FindIndex(lp.Lods, x => x != null);
            lp.Shown = first;
            if (first < 0 || Build(lp, first) == null) { _lodParts.Remove(lp); return null; }
            lp.First = lp.Objects[first];
            return lp.First;
        }

        GameObject Build(LodPart lp, int lod)
        {
            if (lp.Objects[lod]) return lp.Objects[lod];
            var built = Mesh(lp.Lods[lod]);
            if (built == null) return null;
            var go = new GameObject(lp.Lods[lod].Name);
            go.transform.SetParent(lp.Parent, false);
            go.layer = CarEnvironment.CarLayer;   // [ours] car renderers: masked out of the env-map / reflection cameras
            if (lp.First)
            {
                // the slot's other LODs share the first object's pose (spoiler / roof markers, hood / trunk hinge)
                go.transform.localPosition = lp.First.transform.localPosition;
                go.transform.localRotation = lp.First.transform.localRotation;
                go.transform.localScale = lp.First.transform.localScale;
                go.layer = lp.First.layer;
            }
            go.AddComponent<MeshFilter>().sharedMesh = built.Mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (lp.NoShadows) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Register(mr, built, lp.Group, lp.DecalMount);
            lp.Objects[lod] = go;
            go.SetActive(lp.Visible && lp.Shown == lod);
            return go;
        }

        void Show(LodPart lp)
        {
            for (int k = 0; k < 5; k++) if (lp.Objects[k]) lp.Objects[k].SetActive(lp.Visible && k == lp.Shown);
        }

        void SetVisible(LodPart lp, bool visible)
        {
            if (lp == null || lp.Visible == visible) return;
            lp.Visible = visible;
            if (visible) Retarget(lp, true);
            Show(lp);
        }

        /// <summary>Points a part at the current body LOD; a model not built yet is queued (or built now when asked).</summary>
        void Retarget(LodPart lp, bool now)
        {
            int target = Math.Max(lp.MinLod, Math.Min(lp.MaxLod, BodyLod));
            if (lp.Lods[target] == null) { lp.Shown = -1; Show(lp); return; }   // no model at this LOD: nothing drawn
            if (!lp.Objects[target])
            {
                if (!lp.Visible) return;
                if (now) { lp.Shown = target; Build(lp, target); }
                else
                {
                    if (!lp.Pending) { lp.Pending = true; BuildQueue.Enqueue((this, lp, target)); }
                    return;   // keep the current LOD until the new one is built
                }
            }
            lp.Shown = target;
            Show(lp);
        }

        /// <summary>Builds queued LOD models, a few per frame across all cars (CarEnvironment drives it) [ours: time slicing].</summary>
        public static void ProcessBuildQueue()
        {
            for (int i = 0; i < BuildsPerFrame && BuildQueue.Count > 0; i++)
            {
                var (info, lp, lod) = BuildQueue.Dequeue();
                lp.Pending = false;
                if (!info) continue;
                info.Build(lp, lod);
                info.Retarget(lp, false);
            }
        }

        /// <summary>
        /// CarRenderInfo::Render (0x74E160): pixel = radius·H / (distance − radius) (eView::GetPixelSize, radius 3, ×0.7 in
        /// split screen), body LOD = number of CarBodyLodSwapSize (traffic: TrafficCarBodyLodSwapSize) thresholds above
        /// it, from mMinLodLevel A (PC: the car-detail option, full detail [?]) to E. H is taken on a 480-line reference
        /// screen, as WorldStreamer does for scenery (240 / tan(fov/2)) [?].
        /// </summary>
        void UpdateLod()
        {
            float best = 0f;
            int cams = 0;
            if (_camFrame != Time.frameCount)
            {
                _camFrame = Time.frameCount;
                if (_camBuffer.Length < Camera.allCamerasCount) _camBuffer = new Camera[Camera.allCamerasCount + 4];
                _camCount = Camera.GetAllCameras(_camBuffer);
            }
            for (int ci = 0; ci < _camCount; ci++)
            {
                var cam = _camBuffer[ci];
                if (!cam || cam.cameraType != CameraType.Game || (cam.cullingMask & (1 << gameObject.layer)) == 0) continue;
                cams++;
                float H = 240f / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                Vector3 d = transform.position - cam.transform.position;
                const float r = CarRenderTables.Radius;
                if (Vector3.Dot(d, cam.transform.forward) < -r) continue;
                float away = d.magnitude - r;
                float px = away > r * 0.5f ? r * H / away : 2f * H;
                if (cam.rect.height < 0.99f) px *= CarRenderTables.CarTwoPlayerLODFactor;
                best = Mathf.Max(best, px);
            }
            if (cams == 0) return;
            _pixelSize = best;
            // render flag 1 for the body and tyres when EnableEnvMap [0x8F9334] == 0 or pixel < DisableEnvMapSize 18 (0x74E160);
            // the brakes never get it
            SetEnvOff(best < CarEnvironment.DisableEnvMapSize);
            // the traffic table also applies when mMinLodLevel == C (0x74E160) - never at full detail
            var table = Traffic || MinLodLevel == 2 ? CarRenderTables.TrafficCarBodyLodSwapSize : CarRenderTables.CarBodyLodSwapSize;
            int lod = 0;
            while (lod < 4 && best < table[lod]) lod++;
            lod = Math.Max(MinLodLevel, lod);
            if (lod == BodyLod) return;
            BodyLod = lod;
            foreach (var lp in _lodParts) Retarget(lp, false);
            UpdateWheelLods(true);
        }

        void SetEnvOff(bool off)
        {
            if (off == _envOff) return;
            _envOff = off;
            _envBlock ??= new MaterialPropertyBlock();
            foreach (var g in _surfaces.GroupBy(x => x.R))
            {
                if (!g.Key || g.Key.gameObject.name == "Brake") continue;
                g.Key.GetPropertyBlock(_envBlock);
                _envBlock.SetFloat("_MW_CarEnvOff", off ? 1f : 0f);
                g.Key.SetPropertyBlock(_envBlock);
            }
        }

        /// <summary>CarBuilder hook: a wheel (tyre + rim) or brake object built at LOD A with the slot's models A..E.</summary>
        public void RegisterWheelLods(GameObject first, Solid[] lods, bool rear, bool brake)
        {
            if (!first || lods == null) return;
            var w = new WheelLod { First = first, Rear = rear, Brake = brake, Shown = 0 };
            for (int k = 0; k < 5 && k < lods.Length; k++) w.Lods[k] = lods[k];
            w.Objects[0] = first;
            _wheelLods.Add(w);
        }

        GameObject BuildWheel(WheelLod w, int lod)
        {
            if (w.Objects[lod]) return w.Objects[lod];
            if (w.Lods[lod] == null) return null;
            var built = Mesh(w.Lods[lod]);
            if (built == null) return null;
            var first = w.First.transform;
            var go = new GameObject(w.Lods[lod].Name);
            go.transform.SetParent(first.parent, false);
            go.transform.localPosition = first.localPosition;
            go.transform.localRotation = first.localRotation;
            go.transform.localScale = first.localScale;
            go.layer = w.First.layer;
            go.AddComponent<MeshFilter>().sharedMesh = built.Mesh;
            var mr = go.AddComponent<MeshRenderer>();
            Register(mr, built, w.Brake ? "brake" : "wheel");
            if (_envOff && !w.Brake) { _envBlock ??= new MaterialPropertyBlock(); _envBlock.SetFloat("_MW_CarEnvOff", 1f); mr.SetPropertyBlock(_envBlock); }
            w.Objects[lod] = go;
            go.SetActive(false);
            return go;
        }

        /// <summary>The tyre / brake model of the body LOD (CarRenderInfo::Render 0x74E160), with the TireFace B swap.</summary>
        void UpdateWheelLods(bool lodChanged)
        {
            if (_wheelLods.Count == 0) return;
            var cam = Camera.main;
            bool nis = Game.NisPlayer.Active != null;
            foreach (var w in _wheelLods)
            {
                int max = w.Brake ? 1 : 3;                        // mMaxBrakeLodLevel B, mMaxTireLodLevel D
                int lod = BodyLod > max ? -1 : BodyLod;           // beyond: not drawn
                if (!w.Brake && lod == MinLodLevel && w.Rear && !nis && LodEnabled && cam && w.Lods[lod + 1] != null && _car)
                {
                    // TireFace: the wheel model's +Y (axle, inboard) axis in the world against the camera direction
                    var axle = w.First.transform.forward;      // mesh z = game Y (Coords.ToUnity)
                    if (Vector3.Dot(cam.transform.forward, axle) < 0f) lod = lod + 1;
                }
                if (lod >= 0 && w.Lods[lod] == null)
                {
                    // the slot has no model at this LOD: the exe draws nothing (mCarPartModels null) - [ours] the nearest
                    // lower one keeps the wheel on cars whose tyre pack stops early (all stock packs have A..D)
                    int k = lod; while (k > 0 && w.Lods[k] == null) k--;
                    lod = w.Lods[k] != null ? k : -1;
                }
                if (lod == w.Shown && !lodChanged) continue;
                if (lod >= 0) BuildWheel(w, lod);
                for (int k = 0; k < 5; k++) if (w.Objects[k]) w.Objects[k].SetActive(k == lod);
                w.Shown = lod;
            }
        }

        /// <summary>Registers an already built mesh (wheels, brakes) and assigns its materials.</summary>
        public void Register(Renderer r, MeshFactory.Built built, string group, int decalMount = -1)
        {
            var mats = new Material[built.Parts.Count];
            for (int i = 0; i < built.Parts.Count; i++)
            {
                var sf = new Surface { R = r, Index = i, Part = built.Parts[i], Group = group };
                Classify(sf, decalMount);
                _surfaces.Add(sf);
                if (sf.Kind == Kind.Brake || sf.Kind == Kind.BrakeGlass) _brakes.Add(sf);
                mats[i] = Resolve(sf);
            }
            r.sharedMaterials = mats;
            if (group == "brake") _brakeRenderers.Add(r);
            if (group == "wheel") _tireRenderers.Add(r);
        }

        void Classify(Surface s, int decalMount)
        {
            uint t = s.Part.TextureHash, lm = s.Part.LightMaterialHash;
            if (t == SkinHash || t == SkinBHash || t == CarSurfaces.GlobalSkin1 || t == CarSurfaces.SpoilerSkin1 || t == CarSurfaces.RoofSkin) s.Kind = Kind.Skin;
            else if (t == CarSurfaces.CarbonSkin || t == CarSurfaces.GlobalCarbonSkin) s.Kind = Kind.CarbonSkin;
            else if (t == BrakeL || t == BrakeR || t == BrakeC) { s.Kind = Kind.Brake; s.Side = t == BrakeL ? 0 : t == BrakeR ? 1 : 2; }
            else if (t == BrakeGlassL || t == BrakeGlassR || t == BrakeGlassC) { s.Kind = Kind.BrakeGlass; s.Side = t == BrakeGlassL ? 0 : t == BrakeGlassR ? 1 : 2; }
            else if (t == HeadL || t == HeadR) { s.Kind = Kind.Headlight; s.Side = t == HeadL ? 0 : 1; }
            else if (t == HeadGlassL || t == HeadGlassR) { s.Kind = Kind.HeadlightGlass; s.Side = t == HeadGlassL ? 0 : 1; }
            else if (GlassNames.Contains(t)) s.Kind = Kind.Glass;
            else if (DecalNames.Contains(t)) s.Kind = Kind.Decal;
            else if (decalMount >= 0 && Array.IndexOf(DecalDummies, t) >= 0)
            {
                s.Kind = Kind.Decal;
                s.DecalSlot = CarLook.SlotDecalTex0 + decalMount * 8 + Array.IndexOf(DecalDummies, t);
            }
            else if (t != 0 && t == WheelHash) s.Kind = Kind.Wheel;
            else if (lm == Core.LightMaterials.CarSkin || lm == Core.LightMaterials.Windshield || IsMag(lm)) s.Kind = Kind.LightMat;
            else if (Traffic && t == EaHash.Hash(Car + "_TIRE")) s.Kind = Kind.Tire;
            else s.Kind = Kind.Shared;
        }

        static bool IsReplacedLightMaterial(uint lm) => lm == Core.LightMaterials.CarSkin || lm == Core.LightMaterials.Windshield || IsMag(lm);

        static bool IsMag(uint lm) => lm == Core.LightMaterials.MagSilver || lm == Core.LightMaterials.MagChrome || lm == Core.LightMaterials.MagGunmetal;

        /// <summary>The replaced texture of a surface (MasterReplacementTextureTable / CarbonReplacementTextureTable).</summary>
        Texture TextureOf(Surface s, out GameTexture source)
        {
            var lib = GameData.Textures;
            uint t = s.Part.TextureHash;
            bool carbonHood = s.Group == "hood" && Look.CarbonHood;
            source = null;
            uint use = t;
            switch (s.Kind)
            {
                case Kind.Skin:
                case Kind.CarbonSkin:
                    if (carbonHood && (t == SkinHash || s.Kind == Kind.CarbonSkin)) use = CarSurfaces.CarbonFibre;
                    else if (Look.Skinnable && _skin) return _skin;
                    else use = SkinHash;   // ReplaceSkinHash = MappedSkinHash for a car without a composite skin
                    break;
                case Kind.Wheel:
                    if (_wheel) return _wheel;
                    break;
                case Kind.Headlight:
                    use = EaHash.Hash("_ON", LightBase(true));      // lights_always_on (UpdateLightStateTextures 0x737E30)
                    break;
                case Kind.HeadlightGlass:
                    use = EaHash.Hash("_GLASS_ON", LightBase(true));
                    break;
                case Kind.Brake:
                case Kind.BrakeGlass:
                {
                    bool on = BrakeOn(s.Side);
                    use = EaHash.Hash(s.Kind == Kind.Brake ? (on ? "_ON" : "_OFF") : (on ? "_GLASS_ON" : "_GLASS_OFF"), LightBase(false));
                    break;
                }
                case Kind.Glass:
                {
                    // SetCarGlassDamageState: the undamaged state is WINDOW_FRONT for every window entry, REAR_DEFROSTER for the defroster
                    bool rear = t == CarSurfaces.RearDefroster;
                    use = _glassDamaged.TryGetValue(t, out bool dmg) && dmg ? CarSurfaces.WindowDamage0 : rear ? CarSurfaces.RearDefroster : CarSurfaces.WindowFront;
                    break;
                }
                case Kind.Decal:
                    use = s.DecalSlot > 0 ? DecalTexture(s) : CarSurfaces.DefaultAlpha;
                    break;
            }
            if (s.Kind == Kind.Tire) use = CarSurfaces.TrafficTire;   // REPLACETEX_TIRE for traffic cars (CarRender.cpp:1213)
            source = lib.Source(use);
            var tex = lib.Get(use);
            if (!tex && use != t) { source = lib.Source(t); tex = lib.Get(t); }
            return tex;
        }

        /// <summary>CarInfo.cpp:1786-1830: the headlight / brakelight texture base, the slot part's TEXTURE_NAME or &lt;CAR&gt;_KIT00_x.</summary>
        uint LightBase(bool head)
        {
            var p = head ? Look.Headlight : Look.Brakelight;
            if (p != null && p.TextureHash != 0) return p.TextureHash;
            return EaHash.Hash(Car + (head ? "_KIT00_HEADLIGHT" : "_KIT00_BRAKELIGHT"));
        }

        bool BrakeOn(int side)
        {
            int bit = side == 0 ? LightLBrake : side == 1 ? LightRBrake : LightCBrake;
            if (ForceBrakelightsOn && side < 2) return true;
            return (BrokenLights & bit) == 0 && (OnLights & bit) != 0;
        }

        /// <summary>UpdateDecalTextures (CarRender.cpp:2029, exe 0x74DA80): the decal part's NAME hash + "_" + the mount's SHAPE.</summary>
        uint DecalTexture(Surface s)
        {
            if (!Look.Decals.TryGetValue(s.DecalSlot, out var part) || part == null) return CarSurfaces.DefaultAlpha;
            uint shape = MountShape(s.Group);
            string suffix = CarRenderTables.ShapeSuffix(shape);
            if (suffix == null || Look.Hood == null) return CarSurfaces.DefaultAlpha;   // hood_decals: a HOOD part must be set
            uint name = part.U(CarSurfaces.Name);
            return name == 0 ? CarSurfaces.DefaultAlpha : EaHash.Hash(suffix, name);
        }

        readonly Dictionary<string, uint> _mountShapes = new Dictionary<string, uint>();
        uint MountShape(string group) => _mountShapes.TryGetValue(group, out var v) ? v : 0;

        /// <summary>The light material of a surface after ReplaceLightMaterial (CarRender.cpp:3064-3230, 3426-3441).</summary>
        LightMaterial LightMaterialOf(Surface s)
        {
            uint lm = s.Part.LightMaterialHash;
            if (lm == Core.LightMaterials.CarSkin)
            {
                if (s.Group == "hood" && Look.CarbonHood) return CarSurfaces.Get(Core.LightMaterials.CarbonFiber);
                uint paint = Look.LightMaterialOf(Look.PaintMaterialPart);
                return CarSurfaces.Get(paint);
            }
            if (lm == Core.LightMaterials.Windshield)
            {
                uint tint = Look.LightMaterialOf(Look.Tint);
                return CarSurfaces.Get(tint != 0 ? tint : Core.LightMaterials.Windshield);
            }
            if (IsMag(lm) && s.Group == "wheel" && Look.Rim != null && Look.Rim.Level != 0 && Look.RimPaint != null)
            {
                uint rim = Look.LightMaterialOf(Look.RimPaint);
                if (rim != 0) return CarSurfaces.Get(rim);
            }
            return CarSurfaces.Get(lm);
        }

        Material Resolve(Surface s)
        {
            if (s.Kind == Kind.Shared && s.Part.LightMaterialHash != Core.LightMaterials.CarSkin)
                return CarSurfaces.SharedFor(s.Part.TextureHash, s.Part.LightMaterialHash);
            // replacements that never change on a car share one material per replaced texture
            if ((s.Kind == Kind.Headlight || s.Kind == Kind.HeadlightGlass || s.Kind == Kind.Tire || (s.Kind == Kind.Decal && s.DecalSlot == 0))
                && !IsReplacedLightMaterial(s.Part.LightMaterialHash))
            {
                uint use = s.Kind == Kind.Headlight ? EaHash.Hash("_ON", LightBase(true)) : s.Kind == Kind.HeadlightGlass ? EaHash.Hash("_GLASS_ON", LightBase(true))
                    : s.Kind == Kind.Tire ? CarSurfaces.TrafficTire : CarSurfaces.DefaultAlpha;
                return CarSurfaces.SharedFor(GameData.Textures.Contains(use) ? use : s.Part.TextureHash, s.Part.LightMaterialHash);
            }
            var tex = TextureOf(s, out var src);
            var lm = LightMaterialOf(s);
            var (blend, cut) = s.Kind == Kind.Skin && Look.Skinnable && _skin ? (CarSurfaces.BlendKind.Opaque, -1f) : CarSurfaces.BlendOf(src);
            if (s.Kind == Kind.Decal || s.Kind == Kind.Glass || s.Kind == Kind.BrakeGlass || s.Kind == Kind.HeadlightGlass)
                if (blend == CarSurfaces.BlendKind.Opaque) blend = CarSurfaces.BlendKind.Alpha;
            if (s.Own == null) s.Own = CarSurfaces.Create(tex, lm, blend, cut, $"{Car} {s.Kind} {s.Part.MaterialName}");
            else
            {
                s.Own.mainTexture = tex;
                s.Own.SetFloat("_Cutoff", cut);
                CarSurfaces.Apply(s.Own, lm);
                CarSurfaces.SetBlend(s.Own, blend);
            }
            // No caller tint: traffic (and cops) go through VehicleRenderConn::Load (decomp VehicleRenderConn.cpp:
            // 344-394) with SetStockParts and no customization record (DrawCar passes PVehicle::GetCustomizations, null for
            // AITrafficManager spawns), so BASE_PAINT is the type's DefaultBasePaint TRAFFIC_L1_COLOR01 / COP_L1_COLOR01,
            // whose light material TRAFFIC is white: the exe draws <CAR>_SKIN1 as it is. The old [ours] tint from the
            // caller's colour (_Paint) is off; the shader keeps the knob.
            s.Own.SetFloat("_Paint", 0f);
            s.Own.SetColor("_Color", Look.Paint);
            return s.Own;
        }

        void Reapply(Func<Surface, bool> which)
        {
            foreach (var g in _surfaces.Where(which).GroupBy(x => x.R))
            {
                if (!g.Key) continue;
                var mats = g.Key.sharedMaterials;
                foreach (var s in g) if (s.Index < mats.Length) mats[s.Index] = Resolve(s);
                g.Key.sharedMaterials = mats;
            }
        }

        // ---- overlays, decals, cop damage parts ----------------------------------------------------------------------

        /// <summary>
        /// RideInfo::SetPart(BODY) (CarInfo.cpp:1327-1400): the kit's DAMAGE0_FRONT..REARRIGHT overlays (hidden until
        /// SetPlayerDamage shows them) and the kit's door / quarter decal mounts; the window mounts are the car's own.
        /// Only mounts with an installed decal are built [ours: the game draws the rest with DEFAULTALPHA].
        /// </summary>
        public void AddOverlays(CarAsset asset, int kit)
        {
            string k = $"{Car}_KIT{kit:00}_";
            foreach (var (part, zone) in CarRenderTables.Damage0Parts)
            {
                var s = asset.Solids.Find(x => x.Name == k + part + "_A") ?? asset.Solids.Find(x => x.Name == $"{Car}_KIT00_{part}_A");
                if (s == null) continue;
                var go = AddPart(s, _body, "damage0", asset);
                if (!go) continue;
                var lp = _lodParts[_lodParts.Count - 1];
                SetVisible(lp, false);
                _damage0.Add((lp, zone));
            }
            if (!Look.Skinnable) return;
            var db = CarParts.Db;
            for (int m = 0; m < CarRenderTables.DecalMounts.Length; m++)
            {
                bool any = false;
                for (int j = 0; j < 8; j++) any |= Look.Decals.ContainsKey(CarLook.SlotDecalTex0 + m * 8 + j);
                if (!any) continue;
                _mountsTried.Add(m);
                string mount = CarRenderTables.DecalMounts[m];
                // windows: the slot's stock mount part (SetStockParts); doors / quarters: KITnn_<mount>_RECT_MEDIUM
                CarPartDb.Part part = m < 2 ? CarLook.Stock(Car, 70 + m)
                    : db.PartsFor(Car, 70 + m).FirstOrDefault(p => p.ModelSuffix() == $"_KIT{kit:00}_{mount}_RECT_MEDIUM_A")
                      ?? db.PartsFor(Car, 70 + m).FirstOrDefault(p => p.ModelSuffix() == $"_KIT00_{mount}_RECT_MEDIUM_A");
                if (part == null || !part.Has(CarSurfaces.Size) || !part.Has(CarSurfaces.Shape)) continue;
                uint model = part.ModelHash(0, EaHash.Hash(Car));
                var solid = asset.Solids.Find(x => x.Hash == model) ?? asset.Solids.Find(x => x.Name == Car + part.ModelSuffix());
                if (solid == null) continue;
                string group = "decal:" + mount;
                _mountShapes[group] = part.U(CarSurfaces.Shape);
                AddPart(solid, _body, group, null, m);
            }
        }

        /// <summary>A cop car's intact damage-slot part (…_KIT00_DAMAGE0_&lt;SLOT&gt;_A) and its damaged twin (DAMAGE1).</summary>
        public bool RegisterCopPart(Solid intact, GameObject go, CarAsset asset)
        {
            _asset = asset;
            var lp = _lodParts.Find(x => x.First == go);
            if (lp == null) return false;
            for (int slot = 1; slot < VehiclePartDamageMap.SlotNames.Length; slot++)
            {
                string n = VehiclePartDamageMap.SlotNames[slot];
                if (n == null || !intact.Name.EndsWith("_DAMAGE0_" + n + "_A")) continue;
                string dn = intact.Name.Replace("_DAMAGE0_" + n + "_A", "_DAMAGE1_" + n + "_A");
                _copParts[slot] = (lp, null, asset.Solids.Find(x => x.Name == dn), 0);
                return true;
            }
            return false;
        }

        // ---- per-frame state ---------------------------------------------------------------------------------------

        /// <summary>[ours] Every enabled car (VehicleRenderConn::GetList, 0x9B37CC): the road reflection view draws them
        /// mirrored (World/RoadReflection.cs, world_render.md §4).</summary>
        public static readonly List<CarRenderInfo> All = new List<CarRenderInfo>();
        /// <summary>The body transform: the model origin, i.e. the exe's render matrix (CarRenderConn +0x330).</summary>
        public Transform Body => _body;

        void OnEnable()
        {
            CarEnvironment.Ensure();
            if (!All.Contains(this)) All.Add(this);
        }

        void OnDisable() => All.Remove(this);

        // ---- reset flashing (DrawCar::OnService, CarRenderInfo::UpdateFlashing 0x7375C0) ---------------------------------

        bool _flashing;
        float _flashInterval;              // +0x17C4 mFlashInterval (never reset when the flashing stops)
        Renderer[] _flashRenderers;
        bool _flashHidden;
        const float FlashOff = 0.05f;      // flt_8B2E60 Tweak_ResetFlashOff
        const float FlashCycle = 0.12f;    // flt_8B48C0 Tweak_ResetFlashOn + Tweak_ResetFlashOff

        /// <summary>
        /// DrawCar::OnService sets the packet's mFlashing while RBVehicle's invulnerability is FROM_MANUAL_RESET (1) or
        /// FROM_RESET (2) (not the physics/control switches 3/4); CarRenderConn::Update 0x756509 copies it to +0x17C0 and
        /// calls UpdateFlashing: interval += RealTimeElapsed, and past 0.05 s interval -= 0.12. CarRenderInfo::Render
        /// returns before drawing anything (body, parts, ambient and projected shadows; reflection copies too) while
        /// flashing and interval > 0: the car is hidden 0.05 s of every 0.12 s. Its light flares are drawn by
        /// RenderFlaresOnCar, which has no such test, so they stay. [ours] Hidden with Renderer.forceRenderingOff on every
        /// renderer under the car, which leaves the enabled flags of the part / damage code alone.
        /// </summary>
        void UpdateFlashing()
        {
            int inv = _car ? _car.Invulnerability : 0;
            _flashing = inv == 1 || inv == 2;
            if (_flashing)
            {
                _flashInterval += Time.unscaledDeltaTime;                  // RealTimeElapsed
                if (_flashInterval > FlashOff) _flashInterval -= FlashCycle;
            }
            bool hide = (_flashing && _flashInterval > 0f) || CameraInside();
            if (hide == _flashHidden && !hide) return;
            if (_flashRenderers == null || hide != _flashHidden) _flashRenderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in _flashRenderers) if (r) r.forceRenderingOff = hide;
            _flashHidden = hide;
        }

        void LateUpdate()
        {
            if (!_car) _car = GetComponent<PlayerCar>();
            UpdateFlashing();
            if (_car)
            {
                // the sim's light state: brake lights while braking forward (as CarFlares / CarRenderConn mLights)
                bool braking = _car.Input.Brake > 0.05f && _car.Gear >= 0;
                OnLights = LightLHead | LightRHead | (braking ? LightLBrake | LightRBrake | LightCBrake : 0);
            }
            if (!_damage) _damage = GetComponent<CarDamage>();
            var dmg = _damage;
            // DamageVehicle::IsLightDamaged: destroyed = every light (DrawCar::OnService copies it for the lights with a
            // marker: heads, brakes, reverse, cop red/blue/white); EDamageLights bits set by the sequencer stay
            if (dmg && dmg.Destroyed) BrokenLights |= LightAll;
            int state = OnLights | BrokenLights << 8 | (ForceBrakelightsOn ? 1 << 20 : 0);
            if (state != _appliedLights)
            {
                _appliedLights = state;
                // the brake surfaces own per-car instances (assigned at build): the texture swap happens in place
                foreach (var s in _brakes) if (s.R) Resolve(s);
            }
            if (_copParts.Count > 0) AnimateCopParts();
            if (LodEnabled && _car) UpdateLod();
            if (_car) UpdateWheelLods(false);
            else if (!_driverHidden) { _driverHidden = true; foreach (var lp in _driverParts) SetVisible(lp, false); }   // DRIVER never draws in the FE
        }

        bool _driverHidden;
        Bounds _localBounds;
        bool _boundsSet;

        /// <summary>
        /// CarRenderInfo::Render (0x74E6xx): with body LOD A, the car within 4 m of the camera, the player view and no NIS,
        /// the eye in car space (z + 0.5) inside the car's bounding box means the car is not drawn - the bumper camera
        /// sits inside its own car (the hood camera's eye + 0.5 clears the roof).
        /// </summary>
        bool CameraInside()
        {
            if (BodyLod != 0 || !_car || !_body || Game.NisPlayer.Active != null) return false;
            var cam = Camera.main;
            if (!cam) return false;
            if (Vector3.Distance(cam.transform.position, transform.position) >= 4f) return false;
            if (!_boundsSet)
            {
                bool any = false;
                foreach (var lp in _lodParts)
                {
                    if (lp.Group.StartsWith("decal") || lp.Group == "damage0" || !lp.First) continue;
                    var mf = lp.First.GetComponent<MeshFilter>();
                    if (!mf || !mf.sharedMesh) continue;
                    var b = mf.sharedMesh.bounds;
                    var m = _body.worldToLocalMatrix * lp.First.transform.localToWorldMatrix;
                    var c = m.MultiplyPoint3x4(b.center);
                    var e = b.extents;
                    var ext = new Vector3(Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                        Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                        Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                    var bb = new Bounds(c, ext * 2f);
                    if (!any) { _localBounds = bb; any = true; } else _localBounds.Encapsulate(bb);
                }
                _boundsSet = any;
                if (!any) return false;
            }
            var eye = _body.InverseTransformPoint(cam.transform.position);
            eye.y += 0.5f;   // game z (up)
            return _localBounds.Contains(eye);
        }

        /// <summary>IsLightBroken for the flares (CarFlares).</summary>
        public bool IsLightBroken(int bit) => (BrokenLights & bit) != 0;

        /// <summary>The VehicleFX light bit of a flare marker hash (LEFT_HEADLIGHT … CENTRE_BRAKELIGHT), 0 for others.</summary>
        public static int LightBit(uint markerHash)
        {
            if (markerHash == MarkLHead) return LightLHead;
            if (markerHash == MarkRHead) return LightRHead;
            if (markerHash == MarkLBrake) return LightLBrake;
            if (markerHash == MarkRBrake) return LightRBrake;
            if (markerHash == MarkCBrake) return LightCBrake;
            if (markerHash == MarkLReverse) return LightLReverse;
            if (markerHash == MarkRReverse) return LightRReverse;
            // RenderFlaresOnCar (CarRender.cpp:2602-2620): a broken COPRED / COPBLUE zeroes the red / blue intensities, which
            // the bright red / blue flares use too; COPWHITE the white ones (the orange one has no light id)
            if (markerHash == MarkCopRed || markerHash == MarkCopBrightRed) return LightCopRed;
            if (markerHash == MarkCopBlue || markerHash == MarkCopBrightBlue) return LightCopBlue;
            if (markerHash == MarkCopWhite) return LightCopWhite;
            return 0;
        }

        static readonly uint MarkLHead = EaHash.Hash("LEFT_HEADLIGHT"), MarkRHead = EaHash.Hash("RIGHT_HEADLIGHT"), MarkLBrake = EaHash.Hash("LEFT_BRAKELIGHT"),
            MarkRBrake = EaHash.Hash("RIGHT_BRAKELIGHT"), MarkCBrake = EaHash.Hash("CENTRE_BRAKELIGHT"),
            MarkLReverse = EaHash.Hash("LEFT_REVERSE"), MarkRReverse = EaHash.Hash("RIGHT_REVERSE"),
            MarkCopRed = EaHash.Hash("COPLIGHTRED"), MarkCopBlue = EaHash.Hash("COPLIGHTBLUE"), MarkCopWhite = EaHash.Hash("COPLIGHTWHITE"),
            MarkCopBrightRed = EaHash.Hash("COPLIGHTBRIGHTRED"), MarkCopBrightBlue = EaHash.Hash("COPLIGHTBRIGHTBLUE");

        // ---- damage ------------------------------------------------------------------------------------------------

        /// <summary>
        /// CarRenderInfo::SetDamageInfo (0x742900): cop cars → VehiclePartDamageBehaviour::DamageVehicle (or Reset when the
        /// info is 0); every other car → SetPlayerDamage.
        /// </summary>
        public void SetDamageInfo(uint info)
        {
            if (_copParts.Count > 0)
            {
                if (info != DamageInfo)
                {
                    DamageInfo = info;
                    if (info == 0) ResetCopParts();
                    else for (int z = 0; z < DamageZone.Max; z++) DamageCopZone(z, DamageZone.Get(info, z));
                    ManageGlassDamage();
                }
                return;
            }
            if (info == DamageInfo) return;
            DamageInfo = info;
            // SetPlayerDamage: DAMAGE0 overlays at level > 0, glass at level > 1 (kDamageThresh 0, kDamageWindowThresh 1)
            foreach (var (lp, zone) in _damage0) SetVisible(lp, DamageZone.Get(info, zone) > 0);
            _glassDamaged.Clear();
            foreach (var (tex, zone) in CarRenderTables.PlayerGlass)
            {
                uint h = EaHash.Hash(tex);
                bool d = DamageZone.Get(info, zone) > 1;
                _glassDamaged[h] = (_glassDamaged.TryGetValue(h, out bool prev) && prev) || d;
            }
            Reapply(s => s.Kind == Kind.Glass);
        }

        /// <summary>VehiclePartDamageBehaviour::DamageZone (VehiclePartDamage.cpp:300-325): MaximumDamageLevel 1.</summary>
        void DamageCopZone(int zone, int level)
        {
            foreach (int slot in VehiclePartDamageMap.SlotsOf(zone))
            {
                if (!_copParts.TryGetValue(slot, out var p)) continue;
                int lvl = Math.Min(1, Math.Max(level, p.level));
                if (lvl == p.level) continue;
                if (lvl == 1)
                {
                    // CarPartDB.NewGetNextCarPart: the slot's DAMAGE1 model; none = the part is gone
                    if (p.damaged == null && p.damagedSolid != null && p.intact != null && AddPart(p.damagedSolid, p.intact.Parent, SlotGroup(slot), _asset) != null)
                        p.damaged = _lodParts[_lodParts.Count - 1];
                    bool hidden = _hiddenSlots.Contains(slot);
                    SetVisible(p.intact, false);
                    SetVisible(p.damaged, !hidden);
                }
                p.level = lvl;
                _copParts[slot] = p;
                // (no light breaks here: the exe's lamps only break through DamageVehicle::mLightDamage, i.e. the CopCar
                // sequence's EDamageLights LIGHT_COPS (CarFragments) or a destroyed car; a DAMAGE1 lamp keeps its flare)
            }
        }

        void ResetCopParts()
        {
            foreach (var slot in _copParts.Keys.ToList())
            {
                var p = _copParts[slot];
                SetVisible(p.intact, true);
                if (p.damaged != null)
                {
                    SetVisible(p.damaged, false);
                    foreach (var o in p.damaged.Objects) if (o) { o.transform.localRotation = Quaternion.identity; o.transform.localPosition = Vector3.zero; }
                }
                p.level = 0;
                _copParts[slot] = p;
            }
            _hiddenSlots.Clear();
            BrokenLights = 0;
            _partAngle = _partVel = 0;
        }

        bool _defrosterCracked;

        static string SlotGroup(int slot) => slot == VehiclePartDamageMap.Hood ? "hood" : "";

        /// <summary>
        /// ManageGlassDamage (PC 0x74C210, VehiclePartDamage.cpp:273): a damaged window slot cracks its entry and the
        /// REAR_DEFROSTER entry (+0x320); an undamaged slot restores only its own entry, so once cracked the defroster stays
        /// WINDOW_DAMAGE0 for the life of the CarRenderInfo — also through Reset (0x75C330), which calls DamageZone(z, 0)
        /// and UpdateCarParts (0x755800) but never rebuilds the replacement table (a pooled cop keeps the cracked rear
        /// defroster after RESET_DAMAGE).
        /// </summary>
        void ManageGlassDamage()
        {
            _glassDamaged.Clear();
            foreach (var (slot, tex) in VehiclePartDamageMap.BreakableWindows)
            {
                bool d = _copParts.TryGetValue(slot, out var p) && p.level > 0;
                uint h = EaHash.Hash(tex);
                _glassDamaged[h] = (_glassDamaged.TryGetValue(h, out bool prev) && prev) || d;
                if (d) _defrosterCracked = true;
            }
            if (_defrosterCracked) _glassDamaged[CarSurfaces.RearDefroster] = true;
            Reapply(s => s.Kind == Kind.Glass);
        }

        // ---- VehiclePartDamageBehaviour::Update (0x74C430) ---------------------------------------------------------
        // Every frame Pose() puts all damage parts at the car matrix; then a damaged (level 1) trunk (slot 15) turns by
        // +CalcPartRotation·360°, the hood (slot 10) by −CalcPartRotation·360°, and a damaged rear bumper (16) runs
        // CalcPartRotation without using the result. All three calls integrate the one mTrunkAngle / mTrunkVel pair of
        // the car (+0xC4 / +0xC0), so a car with two damaged parts integrates twice per frame (exe; the old port kept one
        // state per part). CalcPartRotation (0x73E620) gets maxAngle 0x3CE38E39 = 10/360 (DEG2ANGLE(10) as a fraction of
        // a turn), spring 2.4, damper 0.2, inertia 80 (trunk, bumper) / 130 (hood) — but keeps them in function statics
        // set on the first call ever ([0x9B3768] / [0x9B3764] / [0x9B3760], guard bits [0x9B376C]), so the first damaged
        // part of the session fixes the inertia for every car (80 or 130).
        float _partAngle, _partVel;
        Vector3 _lastVel, _vel, _acc, _angVel;
        static bool _springStatics;
        static float _tCs, _tCd, _tI;

        void AnimateCopParts()
        {
            float dT = Time.deltaTime;
            if (dT <= 0f) return;
            // CarRenderInfo +0x10 velocity, +0x20 angular velocity, +0x30 acceleration (CarRenderConn copies them from the
            // sim each frame); Unity world space here
            var rb = GetComponent<Rigidbody>();
            _vel = rb ? rb.linearVelocity : Vector3.zero;
            _acc = (_vel - _lastVel) / dT;
            _lastVel = _vel;
            _angVel = rb ? rb.angularVelocity : Vector3.zero;
            if (Level(VehiclePartDamageMap.Trunk) == 1) AnimatePart(VehiclePartDamageMap.Trunk, CalcPartRotation(dT, 80f) * 360f);
            if (Level(VehiclePartDamageMap.Hood) == 1) AnimatePart(VehiclePartDamageMap.Hood, -(CalcPartRotation(dT, 130f) * 360f));
            if (Level(VehiclePartDamageMap.RearBumper) == 1) CalcPartRotation(dT, 80f);
        }

        int Level(int slot) => _copParts.TryGetValue(slot, out var p) ? p.level : 0;

        /// <summary>
        /// CalcPartRotation (0x73E620): world·inverse(world) = identity, so the "local" velocity, acceleration and angular
        /// velocity are the world-space values (game axes x east, y north, z up): acc.x, vel.z (vertical) and ω.y / 2π
        /// (about north). dest = −min(world up.z · 0.25, 0) (0 while upright).
        /// </summary>
        float CalcPartRotation(float dT, float inertia)
        {
            if (!_springStatics) { _springStatics = true; _tCs = 2.4f; _tCd = 0.2f; _tI = inertia; }
            const float maxAngle = 10f / 360f;
            // game from Unity: x = x, y = z, z = y; an angular velocity also flips sign between Unity's left-handed and
            // the game's right-handed frame (ω_game = −swap(ω_unity))
            float upZ = transform.up.y, accX = _acc.x, velZ = _vel.y, angY = -_angVel.z;
            return VehiclePartDamageMap.Spring(ref _partAngle, ref _partVel, dT, maxAngle, _tCs, _tCd, _tI, upZ, accX, velZ, angY);
        }

        /// <summary>
        /// AnimatePart (0x73E920): partMatrix = translate(pivot)·eRotateY(deg)·translate(−pivot)·world (D3DX row vectors,
        /// D3DXMatrixMultiply(out, src, M) = src·M), i.e. a rotation about the model's Y (left) axis through −pivot.
        /// Init (0x754410) sets the pivots to (1.73, −0.96, 0) for the trunk and (−1.13, −0.86, 0) for the hood, so the
        /// hinge line is x = −1.73 / +1.13 at model z = 0, the ground line: the −0.96 / −0.86 sit in the lateral Y slot,
        /// which a Y rotation ignores (the TRUNK / HOOD marker heights 0.97 / 0.83 in the wrong component). eRotateY
        /// (0x6BDC60) builds D3DXMatrixRotationY: x' = x·cos + z·sin, z' = −x·sin + z·cos. A hidden part is skipped.
        /// </summary>
        void AnimatePart(int slot, float degrees)
        {
            if (_hiddenSlots.Contains(slot) || !_copParts.TryGetValue(slot, out var p) || p.damaged == null) return;
            float hx = slot == VehiclePartDamageMap.Trunk ? -1.73f : 1.13f;   // −pivot.x
            const float hz = 0f;                                              // −pivot.z
            // mesh space (Coords.ToUnity): x = game X, y = game Z (up), z = game Y; the game rotation about Y is a
            // rotation about mesh z by −degrees (x' = x·cos + y·sin)
            var rot = Quaternion.AngleAxis(-degrees, Vector3.forward);
            var hinge = new Vector3(hx, hz, 0f);
            foreach (var o in p.damaged.Objects)
                if (o) { o.transform.localRotation = rot; o.transform.localPosition = hinge - rot * hinge; }
        }

        // ---- part hiding (VehicleRenderConn::HidePart / ShowPart → CarRenderInfo::HideCarPart 0x737CF0) -------------

        readonly HashSet<int> _hiddenSlots = new HashSet<int>();

        /// <summary>
        /// HideCarPart (0x737CF0) for a cop damage slot: hides (or shows again) the slot's model at every LOD and returns
        /// the object that was drawn (the fragment copies it: VehicleFragmentConn renders FindCarPart(slot)). Returns null
        /// for a slot this car does not have.
        /// </summary>
        public GameObject HideCarPart(int slot, bool hide)
        {
            if (!_copParts.TryGetValue(slot, out var p)) return null;
            var drawn = Drawn(p);
            if (hide)
            {
                _hiddenSlots.Add(slot);
                SetVisible(p.intact, false);
                SetVisible(p.damaged, false);
            }
            else if (_hiddenSlots.Remove(slot))
            {
                SetVisible(p.level > 0 && p.damaged != null ? p.damaged : p.intact, true);
            }
            return drawn;
        }

        /// <summary>FindCarPart (0x737CC0): the object drawn now for a cop damage slot (null when hidden or absent).</summary>
        public GameObject FindCarPart(int slot) =>
            _hiddenSlots.Contains(slot) || !_copParts.TryGetValue(slot, out var p) ? null : Drawn(p);

        static GameObject Drawn((LodPart intact, LodPart damaged, Solid damagedSolid, int level) p)
        {
            var shown = p.level > 0 && p.damaged != null ? p.damaged : p.intact;
            if (shown == null) return null;
            return shown.Shown >= 0 && shown.Objects[shown.Shown] ? shown.Objects[shown.Shown] : shown.First;
        }

        /// <summary>Whether this car has the cop damage-slot parts (VehiclePartDamageBehaviour).</summary>
        public bool HasPartDamage => _copParts.Count > 0;

        // ---- restyle -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Re-applies the visual customization of a built car (paint, vinyl, vinyl colours, rim paint, window tint,
        /// decals) in place — the shop preview calls it when the look record changes. Returns false when the car needs
        /// a rebuild (decal mounts that were not built).
        /// </summary>
        public static bool Restyle(GameObject car, OwnedCar look, Color paint)
        {
            if (!car) return false;
            bool ok = true;
            foreach (var info in car.GetComponentsInChildren<CarRenderInfo>(true)) ok &= info.Restyle(look, paint);
            return ok;
        }

        public bool Restyle(OwnedCar look, Color paint)
        {
            var l = CarLook.For(Car, look, paint);
            string key = l.Key;
            if (key == _lookKey) return true;
            bool decalsNeedMounts = l.Decals.Keys.Any(k => !_mountsTried.Contains((k - CarLook.SlotDecalTex0) / 8));
            Look = l;
            _lookKey = key;
            BuildLookTextures();
            Reapply(s => s.Kind != Kind.Shared || s.Part.LightMaterialHash == Core.LightMaterials.CarSkin);
            return !decalsNeedMounts;
        }

        /// <summary>A key of the look a car was built with (CarShowroom compares it to decide on Restyle).</summary>
        public static string KeyOf(string car, OwnedCar look, Color paint) => CarLook.For(car, look, paint).Key;
    }
}
