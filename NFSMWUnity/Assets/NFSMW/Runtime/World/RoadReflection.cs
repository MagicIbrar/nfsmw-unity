using System.Collections.Generic;
using NfsMw.CarRender;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.World
{
    /// <summary>
    /// The road reflection view (eView 4 at 0x9197A0, render target slot 1 of 0x6BD0D0; NFSMW_RE/specs/world_render.md §4).
    /// worldreflect.efx (the roads) samples it as ReflectedTex at the pixel's own screen position, scaled by the gloss
    /// (2 specmap.x x tex.a x vertex alpha) and SpecularColour.w = road wetness (Rain +0x36A0), so it only shows while
    /// the road is wet.
    ///
    /// Setup 0x6BCDC0: option [0x9017D4] (g_RoadReflectionEnable) 0 -> a 1x1 texture filled with 0xFFFFFFFF; otherwise a
    /// 320x240 render target. The option preset 0x6C1990 gives 1 at performance level 3 and 2 at level 4+ (clamped to the
    /// card's maximum [0x90185C]); <see cref="Detail"/> defaults to 2.
    /// View 4 uses player 1's camera (epInitViews 0x6C0860: same Camera, same view/projection build 0x6CF400, no mirrored
    /// camera): the reflection is made by mirroring each OBJECT about its own origin, drawn from the real eye.
    /// eDisplayFrame 0x6DE40F (option != 0): clear to 0xFF000000 (0x6BD3B0), then
    ///   1. StuffSkyLayer(view 4, layer 4) 0x6DB310: the sky dome scaled 0.03 with z negated, lifted 130 m above the eye;
    ///   2. option > 1 and wetness > 0.01: StuffScenery(view 4, 8) 0x723FA0 - the culled list of view 4, whose cull flags
    ///      are 0x800 | 0x10 | 0x8000040 (sub_4FAE40 from 0x6BFF1F): DrawAScenery 0x729120 keeps only instances flagged
    ///      0x40 or 0x8000000 ("reflects"), draws them at >= 32 px with the REFLECTION model (SceneryInfo model 3, LOD D;
    ///      model 0 when flagged 0x1000100), its matrix's v2.z negated (0x7293DE: upside down about the instance origin);
    ///      StuffScenery's flag 8 then drops the flag-0x80 ground chops. The world effects become worldnofog /
    ///      worldnormalmapnofog (0x6DAB96: unlit 2 x tex x vertex colour, Shaders/WorldNoFog.shader); trees keep tree.efx.
    ///      Then the cars, sub_750B10 -> VehicleRenderConn::RenderAll(view 4, 1) -> CarRenderConn::OnRender 0x750E20:
    ///      single-player views only (view mode &lt; 3), the render matrix's z axis negated (render flags 0x401), position
    ///      unchanged: each car upside down about its model origin, car.efx as usual;
    ///   3. light-glow option [0x9017E4]: eRenderWorldLightFlares(view 4, FLARE_REFLECT) (no world flare packs on PC),
    ///      RenderVehicleFlares(view 4, 1, 0) - per car RenderTextureHeadlights 0x738180 (a 2PLAYERHEADLIGHT1 pool in front
    ///      of the car) and the car flares as FLARE_REFLECT streaks (LightFlares.AddReflected) - and the flare pool (the
    ///      sun flares exclude view 4);
    ///   4. option 3, game flow 6, wetness > 0.01: sub_6D4960 blurs it (filter taps 0x8F9530 then 0x8F9500).
    /// Port: a hidden camera copies the main camera's pose and projection in Camera.onPreCull and everything above is
    /// queued into it with Graphics.DrawMesh(camera). [ours] The RT is only refreshed while wetness > 0.01 (the shader
    /// multiplies it by the wetness); the reflection render has shadows off (the exe picks shadow techniques only for
    /// view 1, 0x6E0BED); layer 30 is reserved for it.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class RoadReflection : MonoBehaviour
    {
        /// <summary>[0x9017D4] Road Reflection Detail: 0 off (white 1x1), 1 sky + flares, 2 + scenery/cars, 3 + blur.</summary>
        public static int Detail = 2;
        /// <summary>[0x9017E4] the light-glow option (preset 0x6C1990: on from performance level 2).</summary>
        public static bool LightGlow = true;
        public static RoadReflection Instance { get; private set; }
        /// <summary>Called with the reflection camera before it renders (extra mirrored objects can be queued here).</summary>
        public static System.Action<Camera> DrawCarsHook;

        /// <summary>True when view 4 is rendered this frame: option on and the player view's road wetness over 0.01.</summary>
        public static bool WantsReflection => Instance && Instance.isActiveAndEnabled && Detail > 0 && Rain.RoadWetness > 0.01f;

        const int Width = 0x140, Height = 0xF0;           // 0x6BCDF9 / 0x6BCDEE
        const float SkyScale = 0.03f;                     // 0x3D75C28F (0x6DB443)
        const float SkyLift = 130f;                       // 0x43020000 (0x6DB455)
        public const int Layer = 30;                      // [ours] drawn only into the reflection camera
        const int MinPixels = 0x20;                       // DrawAScenery far_clip_size (0x729310 / 0x729322)

        static Mesh _skyMesh;
        static Material[] _skyMats;
        RenderTexture _rt;
        Texture2D _white;
        Camera _cam;
        bool _rendering;
        Material _blur;

        /// <summary>Sky/SkyRender.cs hook: the SKYDOME_XENON mesh and its skybox.efx materials.</summary>
        public static void SetSkyDome(Mesh mesh, Material[] mats)
        {
            _skyMesh = mesh;
            _skyMats = mats;
        }

        public static RoadReflection Ensure(GameObject host)
        {
            if (Instance) return Instance;
            var r = host.GetComponent<RoadReflection>();
            return r ? r : host.AddComponent<RoadReflection>();
        }

        void Awake() { Instance = this; _block = new MaterialPropertyBlock(); }

        void OnEnable()
        {
            Camera.onPreCull += PreCull;
            Publish();
        }

        void OnDisable()
        {
            Camera.onPreCull -= PreCull;
            if (_cam) Destroy(_cam.gameObject);
            if (_rt) { _rt.Release(); Destroy(_rt); }
            if (_blur) Destroy(_blur);
            _cam = null;
            _rt = null;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---- scenery (StuffScenery(view 4, 8)) ------------------------------------------------------------------------

        struct SceneryEntry
        {
            public Renderer Owner;       // the player-view instance: its section / group / PVS state switches this one too
            public Mesh Mesh;
            public Material[] Mats;
            public Matrix4x4 Matrix;     // the instance matrix with v2.z negated (Unity m11)
            public Vector3 Centre;
            public float Radius;         // SceneryInfo radius + 6 (DrawAScenery 0x72920D)
            public bool Bump;            // flag 0x2000000: 10 px more (0x729241)
        }

        static readonly Dictionary<GameObject, List<SceneryEntry>> Scenery = new Dictionary<GameObject, List<SceneryEntry>>();
        static readonly List<GameObject> DeadSections = new List<GameObject>();
        static readonly Dictionary<Material, Material> NoFogCache = new Dictionary<Material, Material>();
        static Shader _noFog, _tree;

        /// <summary>
        /// WorldStreamer hook: one scenery instance the reflection view draws (flag 0x40 / 0x8000000, not 0x80), with its
        /// reflection model and the instance matrix (Unity space, before mirroring). The materials are swapped for the
        /// no-fog effects here.
        /// </summary>
        public static void AddScenery(GameObject section, Renderer owner, Mesh mesh, Material[] mats, Matrix4x4 instance,
            float radius, bool bump)
        {
            if (section == null || owner == null || mesh == null || mats == null) return;
            var m = instance;
            m.m11 = -m.m11;                                  // matrix->v2.z = -matrix->v2.z (0x7293DE)
            var list = Scenery.TryGetValue(section, out var l) ? l : Scenery[section] = new List<SceneryEntry>();
            var conv = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++) conv[i] = NoFog(mats[i]);
            list.Add(new SceneryEntry
            {
                Owner = owner, Mesh = mesh, Mats = conv, Matrix = m, Centre = instance.GetColumn(3), Radius = radius, Bump = bump,
            });
        }

        /// <summary>The section was unloaded: drop its entries (also done lazily when the section object is gone).</summary>
        public static void RemoveScenery(GameObject section)
        {
            if (section != null) Scenery.Remove(section);
        }

        /// <summary>
        /// eViewPlatInterface::Render 0x6DAB96 for render-target slots other than 0 and 4: world.efx (0) and GlossyWindow (5)
        /// draw with worldnofog (8), worldreflect (1) / worldnormalmap (3) with worldnormalmapnofog (30); tree.efx (6) and
        /// every other effect keep theirs. The material's blend / alpha test / cull state stays.
        /// </summary>
        static Material NoFog(Material src)
        {
            if (src == null) return null;
            if (_noFog == null) { _noFog = Shader.Find("NFSMW/WorldNoFog"); _tree = Shader.Find("NFSMW/Tree"); }
            if (_noFog == null || src.shader == _tree) return src;
            string sh = src.shader.name;
            if (!sh.StartsWith("NFSMW/World") && sh != "NFSMW/GlossyWindow") return src;
            if (NoFogCache.TryGetValue(src, out var m) && m) return m;
            m = new Material(_noFog) { name = src.name + " (nofog)", hideFlags = HideFlags.DontSave };
            m.mainTexture = src.mainTexture;
            m.mainTextureScale = src.mainTextureScale;
            m.mainTextureOffset = src.mainTextureOffset;
            bool blended = sh == "NFSMW/WorldDecal" || sh == "NFSMW/WorldTransparent" ||
                           (src.HasProperty("_ZWrite") && src.GetFloat("_ZWrite") < 0.5f);
            m.SetFloat("_Cutoff", !blended && src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : -1f);
            m.SetFloat("_Cull", src.HasProperty("_Cull") ? src.GetFloat("_Cull") : 0f);
            if (blended)
            {
                m.SetFloat("_SrcBlend", src.HasProperty("_SrcBlend") ? src.GetFloat("_SrcBlend") : (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", src.HasProperty("_DstBlend") ? src.GetFloat("_DstBlend") : (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_BlendOp", src.HasProperty("_BlendOp") ? src.GetFloat("_BlendOp") : (float)BlendOp.Add);
                m.SetFloat("_ZWrite", 0f);
                m.SetFloat("_OffsetFactor", -1f);
                m.SetFloat("_OffsetUnits", -2f);
            }
            m.renderQueue = src.renderQueue;
            TextureScroll.Mirror(src, m);
            NoFogCache[src] = m;
            return m;
        }

        /// <summary>DrawAScenery for the reflection cull info: InlinedViewGetPixelSize (Scenery.hpp:402) >= 32.</summary>
        void DrawScenery(Camera main)
        {
            var eye = main.transform.position;
            var dir = main.transform.forward;
            // eView H (0x6BE9F0): (window width / 2) / tan(horizontal fov / 2) = W/2 x projection m00
            float h = 0.5f * main.pixelWidth * main.projectionMatrix.m00;
            DeadSections.Clear();
            foreach (var kv in Scenery)
            {
                if (kv.Key == null) { DeadSections.Add(kv.Key); continue; }
                if (!kv.Key.activeInHierarchy) continue;
                foreach (var e in kv.Value)
                {
                    if (!e.Owner || !e.Owner.enabled || !e.Owner.gameObject.activeInHierarchy) continue;
                    var d = e.Centre - eye;
                    if (Vector3.Dot(d, dir) < -e.Radius) continue;
                    float away = d.magnitude - e.Radius;
                    int px = (int)(away > e.Radius ? e.Radius * h / away : h);
                    if (px < 2) continue;
                    if (e.Bump) px += 10;
                    if (px < MinPixels) continue;
                    for (int i = 0; i < e.Mats.Length && i < e.Mesh.subMeshCount; i++)
                        if (e.Mats[i]) Graphics.DrawMesh(e.Mesh, e.Matrix, e.Mats[i], Layer, _cam, i, null, ShadowCastingMode.Off, false);
                }
            }
            foreach (var k in DeadSections) Scenery.Remove(k);
        }

        // ---- cars (sub_750B10 -> CarRenderConn::OnRender(view 4, 1)) ---------------------------------------------------

        sealed class CarParts
        {
            public int Frame = -1000;
            public readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
            public readonly List<MeshFilter> Filters = new List<MeshFilter>();
        }

        readonly Dictionary<CarRenderInfo, CarParts> _cars = new Dictionary<CarRenderInfo, CarParts>();
        MaterialPropertyBlock _block;   // created in Awake: Unity forbids native objects in MonoBehaviour field initializers
        static Shader _carShader;
        WorldStreamer _streamer;
        int _streamerSearch;

        bool SplitScreen()
        {
            if (!_streamer && --_streamerSearch <= 0) { _streamer = FindFirstObjectByType<WorldStreamer>(); _streamerSearch = 120; }
            return _streamer && _streamer.SplitScreen;
        }

        void DrawCars()
        {
            if (SplitScreen()) return;                    // 0x750FC3: eGetCurrentViewMode() >= 3 draws no reflected cars
            if (_carShader == null) _carShader = Shader.Find("NFSMW/Car");
            var flip = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
            foreach (var info in CarRenderInfo.All)
            {
                if (!info || !info.isActiveAndEnabled || !info.Body) continue;
                if (!_cars.TryGetValue(info, out var parts)) _cars[info] = parts = new CarParts();
                if (Time.frameCount - parts.Frame > 60)
                {
                    parts.Frame = Time.frameCount;
                    parts.Renderers.Clear();
                    parts.Filters.Clear();
                    foreach (var r in info.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (!mf || r.sharedMaterial == null || r.sharedMaterial.shader != _carShader) continue;   // car.efx parts only
                        parts.Renderers.Add(r);
                        parts.Filters.Add(mf);
                    }
                }
                // render matrix with v2 = -v2 about the model origin (0x751093): B diag(1, -1, 1) B^-1 in Unity space
                var b = info.Body.localToWorldMatrix;
                var mirror = b * flip * b.inverse;
                for (int k = 0; k < parts.Renderers.Count; k++)
                {
                    var r = parts.Renderers[k];
                    var mf = parts.Filters[k];
                    if (!r || !mf || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy || !mf.sharedMesh) continue;   // forceRenderingOff: reset flashing
                    var mats = r.sharedMaterials;
                    var mesh = mf.sharedMesh;
                    var m = mirror * r.localToWorldMatrix;
                    MaterialPropertyBlock block = null;
                    if (r.HasPropertyBlock()) { r.GetPropertyBlock(_block); block = _block; }
                    for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
                        if (mats[i]) Graphics.DrawMesh(mesh, m, mats[i], Layer, _cam, i, block, ShadowCastingMode.Off, false);
                }
            }
            // forget destroyed cars
            if (_cars.Count > CarRenderInfo.All.Count + 8)
            {
                var dead = new List<CarRenderInfo>();
                foreach (var kv in _cars) if (!kv.Key) dead.Add(kv.Key);
                foreach (var d in dead) _cars.Remove(d);
            }
        }

        // ---- RenderTextureHeadlights (0x738180) ----------------------------------------------------------------------

        // hOffX 13, hOffY 0, hRad0x..3x 10, hRad0y..3y 4.9 (0x9034E4..0x903518): a quad 3..23 m ahead, 9.8 m wide, at the
        // model origin's plane; colour hcL = 0x40CCCCCC (the dword at 0x903524), texture 2PLAYERHEADLIGHT1
        static readonly Vector3[] PoolCorners =
        {
            Coords.ToUnity(13f - 10f, 0f - 4.9f, 0f), Coords.ToUnity(10f + 13f, 0f - 4.9f, 0f),
            Coords.ToUnity(10f + 13f, 4.9f + 0f, 0f), Coords.ToUnity(13f - 10f, 4.9f + 0f, 0f),
        };
        static readonly Color32 PoolColour = new Color32(0xCC, 0xCC, 0xCC, 0x40);
        const float PoolUpright = 0.707f;                 // 0x891058 (0x7381E0): v2.z >= 0.707
        Mesh _pool;
        Material _poolMat;
        bool _poolTried;
        readonly List<Vector3> _pv = new List<Vector3>();
        readonly List<Vector2> _puv = new List<Vector2>();
        readonly List<Color32> _pc = new List<Color32>();
        readonly List<int> _pt = new List<int>();

        void DrawHeadlightPools()
        {
            if (!_poolTried)
            {
                _poolTried = true;
                var lib = GameData.Textures;
                uint hash = EaHash.Hash("2PLAYERHEADLIGHT1");
                var tex = lib?.Get(hash);
                var sh = Shader.Find("NFSMW/WorldDecal");
                if (tex && sh)
                {
                    var src = lib.Source(hash);
                    bool additive = src != null && src.AlphaBlend == 2;
                    _poolMat = new Material(sh) { name = "2PLAYERHEADLIGHT1 (reflection)", mainTexture = tex, hideFlags = HideFlags.DontSave };
                    _poolMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    _poolMat.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                    _poolMat.SetFloat("_VertexScale", 2f);
                    _poolMat.renderQueue = 3000;
                }
            }
            if (!_poolMat) return;
            _pv.Clear(); _puv.Clear(); _pc.Clear(); _pt.Clear();
            foreach (var info in CarRenderInfo.All)
            {
                if (!info || !info.isActiveAndEnabled || !info.Body) continue;
                var body = info.Body;
                if (body.up.y < PoolUpright) continue;
                int b = _pv.Count;
                for (int k = 0; k < 4; k++) _pv.Add(body.TransformPoint(PoolCorners[k]));
                _puv.Add(new Vector2(0, 0)); _puv.Add(new Vector2(1, 0)); _puv.Add(new Vector2(1, 1)); _puv.Add(new Vector2(0, 1));
                for (int k = 0; k < 4; k++) _pc.Add(PoolColour);
                _pt.Add(b); _pt.Add(b + 1); _pt.Add(b + 2);
                _pt.Add(b); _pt.Add(b + 2); _pt.Add(b + 3);
            }
            if (_pv.Count == 0) return;
            if (!_pool) { _pool = new Mesh { name = "headlight pools", hideFlags = HideFlags.DontSave }; _pool.MarkDynamic(); }
            _pool.Clear();
            _pool.SetVertices(_pv);
            _pool.SetUVs(0, _puv);
            _pool.SetColors(_pc);
            _pool.SetTriangles(_pt, 0, false);
            _pool.RecalculateBounds();
            Graphics.DrawMesh(_pool, Matrix4x4.identity, _poolMat, Layer, _cam, 0, null, ShadowCastingMode.Off, false);
        }

        // ---- the view ------------------------------------------------------------------------------------------------

        /// <summary>ReflectedTex for worldreflect.efx (0x6D71F0 SetTexture REFLECTEDTEX = [0x982A6C]).</summary>
        void Publish()
        {
            if (Detail <= 0)
            {
                if (!_white)
                {
                    _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "ReflectedTex (off)" };
                    _white.SetPixel(0, 0, Color.white);   // 0x6BCF41: the locked texel is filled with 0xFFFFFFFF
                    _white.Apply();
                }
                Shader.SetGlobalTexture("_MW_RoadReflectionTex", _white);
                return;
            }
            if (!_rt)
            {
                _rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "ReflectedTex (view 4)", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                };
                _rt.Create();
                var prev = RenderTexture.active;
                RenderTexture.active = _rt;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = prev;
            }
            Shader.SetGlobalTexture("_MW_RoadReflectionTex", _rt);
        }

        void PreCull(Camera main)
        {
            if (_rendering || main == null || main != Camera.main) return;
            Publish();
            if (!WantsReflection) return;   // 0x6DE4F7: fcomp 0.01
            if (!_cam)
            {
                var go = new GameObject("Road reflection (view 4)") { hideFlags = HideFlags.DontSave };
                DontDestroyOnLoad(go);
                _cam = go.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = new Color(0, 0, 0, 1);    // 0x6BD438: clear colour 0xFF000000
                _cam.cullingMask = 1 << Layer;
                _cam.allowHDR = false;
                _cam.allowMSAA = false;
                _cam.useOcclusionCulling = false;
            }
            _cam.targetTexture = _rt;
            _cam.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            _cam.fieldOfView = main.fieldOfView;
            _cam.nearClipPlane = main.nearClipPlane;
            _cam.farClipPlane = main.farClipPlane;
            _cam.projectionMatrix = main.projectionMatrix;
            // 1. StuffSkyLayer layer 4: SkydomeLocalWorld = diag(0.03, 0.03, -0.03) at the eye, z + 130 (game Z = Unity y)
            if (_skyMesh && _skyMats != null)
            {
                var m = Matrix4x4.TRS(main.transform.position + Vector3.up * SkyLift, Quaternion.identity,
                    new Vector3(SkyScale, -SkyScale, SkyScale));
                for (int i = 0; i < _skyMats.Length && i < _skyMesh.subMeshCount; i++)
                    if (_skyMats[i]) Graphics.DrawMesh(_skyMesh, m, _skyMats[i], Layer, _cam, i, null, ShadowCastingMode.Off, false);
            }
            // 2. option > 1 (and wet, checked above): the reflecting scenery, then the cars
            if (Detail > 1)
            {
                DrawScenery(main);
                DrawCars();
                DrawCarsHook?.Invoke(_cam);
            }
            // 3. the light-glow option: reflected car flares and headlight pools
            if (LightGlow)
            {
                DrawHeadlightPools();
                LightFlares.Instance.DrawReflection(_cam, Layer);
            }
            _rendering = true;
            var shadows = QualitySettings.shadows;
            try
            {
                QualitySettings.shadows = ShadowQuality.Disable;
                _cam.Render();
            }
            finally
            {
                QualitySettings.shadows = shadows;
                _rendering = false;
            }
            // 4. option 3 in game (flow 6): sub_6D4960, filter pass 3 with 0x8F9530 (horizontal 1/3 at +-1.5, 1/6 at +-3
            //    texels) into the second target, then 0x8F9500 (vertical) back into ReflectedTex
            if (Detail >= 3)
            {
                if (!_blur)
                {
                    var sh = Shader.Find("NFSMW/VisualTreatment");
                    if (sh) _blur = new Material(sh) { hideFlags = HideFlags.DontSave };
                }
                if (_blur)
                {
                    var tmp = RenderTexture.GetTemporary(Width, Height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(_rt, tmp, _blur, 1);
                    Graphics.Blit(tmp, _rt, _blur, 2);
                    RenderTexture.ReleaseTemporary(tmp);
                }
            }
        }
    }
}
