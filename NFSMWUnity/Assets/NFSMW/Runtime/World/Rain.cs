using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game.Police;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.World
{
    /// <summary>
    /// The player view's Rain object (World/rain.cpp, Core/Weather.cs Precipitation) driven like eDisplayFrame does:
    /// <see cref="Step"/> runs once per frame from Sky/TimeOfDayDriver (in place of its old cloud easing) while in game
    /// (game flow state 6) and only with g_ParticleSystemEnable on (0x6DF4EB). It also runs ScreenEffects' ePreDisplay /
    /// ePostDisplay steps around it. Drawing:
    ///   3D streaks + tunnel-mouth curtain: texture RAINDROP (InGameA, 64x64, 10 drops across), Particles effect
    ///     (NFSMW/Particle: texture x colour x 2), colour 0x80808080, blend by the texture (1 = alpha blend).
    ///   on-screen drops: 0x6D4620 with fx\rain_drop.efx (Shaders/RainDrop.shader), only while g_RainEnable [0x901810]
    ///     and g_ParticleSystemEnable are on: the frame is copied, then each drop is a clip-space square
    ///     (2x-1, 1-2y)..(+0.12 size) that shows the copy displaced by RAINDROPPC (GLOBALB 16x16): u = x - s/4 + s G,
    ///     v = y + s/4 - s B, alpha = A x 3 x timer/Maxtimer, colour x 1.2, SRCALPHA / INVSRCALPHA, RGB only.
    /// <see cref="RoadWetness"/> (Rain +0x36A0) is SpecularColour.w of the world effects (0x6D6AA0).
    /// </summary>
    public sealed class Rain : MonoBehaviour
    {
        public static Rain Instance { get; private set; }
        public readonly Precipitation Precip = new Precipitation();

        /// <summary>Rain +0x36A0 RoadDampness: ramps to RoadReflection (1) at 0.005 per frame while it rains, back down at
        /// the same rate and snaps to 0 under 0.05.</summary>
        public static float RoadWetness => Instance != null ? Instance.Precip.Wetness : 0f;
        /// <summary>Rain +0x28C, the view's rain intensity (0..1).</summary>
        public static float Intensity => Instance != null ? Instance.Precip.Rain : 0f;

        /// <summary>[ours] off: no rain update or drawing at all (the exe has no such switch besides the options).</summary>
        public static bool Enabled = true;

        ParameterAccessorBlendByDistance _wind, _normalFog, _rainFog;
        ParameterAccessor _fogBlendDist;
        /// <summary>The FogQuery of eDisplayFrame 0x6DEBB2 (Core/Weather.cs FogQuery): a probe, nothing reads it on PC.</summary>
        public readonly FogQuery Fog = new FogQuery();
        ParameterMaps _windMaps;
        float _worldSeconds;
        object _worldKey;
        Vector3 _lastCamPos;
        Vector3 _camVel;
        bool _haveCam;
        Material _dropMat, _screenMat;
        Mesh _dropMesh, _screenMesh;
        Camera _bufferCam;
        CommandBuffer _cmd;
        bool _drawScreen;
        static readonly uint RainDropHash = EaHash.Hash("RAINDROP"), RainDropPcHash = EaHash.Hash("RAINDROPPC");

        public static Rain Ensure(GameObject host)
        {
            if (Instance) return Instance;
            var r = host.GetComponent<Rain>();
            if (!r) r = host.AddComponent<Rain>();
            Instance = r;
            return r;
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            DetachBuffer();
            if (Instance == this) Instance = null;
        }

        static bool VideoOption(int index)
        {
            try { return NfsMw.UI.FEDb.Data.PcVideo[index] != 0; }
            catch (System.Exception) { return true; }
        }

        /// <summary>
        /// One eDisplayFrame: ePreDisplay (tunnel bloom, tint), Rain::UpdateAndRender, ePostDisplay. Returns false when the
        /// exe would not run it (not in game, or particles disabled), so the caller keeps its cloud value.
        /// </summary>
        public bool Step(ParameterMaps maps)
        {
            _drawScreen = false;
            var boot = GameBootstrap.Instance;
            var cam = Camera.main;
            bool inGame = Enabled && boot != null && boot.World != null && boot.World.Ready && boot.Player != null && cam != null;
            if (!inGame) { _haveCam = false; return false; }
            if (!VideoOption(PcVideoSettings.ParticleSystem)) return false;

            // WorldTimeSeconds = Sim::GetTime(): seconds of simulation since Sim::Init (world load) [?: reset per world]
            if (!ReferenceEquals(_worldKey, boot.World)) { _worldKey = boot.World; _worldSeconds = 0f; Precip.SetRainBase(); }
            float dt = Time.deltaTime;
            _worldSeconds += dt;

            var f = Input(cam, boot, dt);
            if (!ReferenceEquals(_windMaps, maps))
            {
                _windMaps = maps;
                _wind = new ParameterAccessorBlendByDistance("Wind");
                _wind.Attach(maps);
                // FogAccessor[0] "Normal Fog", RainFogAccessor[0] "Rain Fog" (BlendByDistance), FogBlendDistAccessor[0]
                // "Fog Blend Distance" (static inits 0x88C860 / 0x88CAA0 / 0x88CCE0; player 1 only: the call hard-codes view 1)
                _normalFog = new ParameterAccessorBlendByDistance("Normal Fog"); _normalFog.Attach(maps);
                _rainFog = new ParameterAccessorBlendByDistance("Rain Fog"); _rainFog.Attach(maps);
                _fogBlendDist = new ParameterAccessor("Fog Blend Distance"); _fogBlendDist.Attach(maps);
            }
            ScreenEffects.PreDisplay(f, Precip);
            // eDisplayFrame order: ePreDisplay (tint) -> TOD blend, FogQuery::CalculateFogInfo (last frame's rain / cloud) -> Rain::UpdateAndRender
            if (f.ViewIndex == 0) Fog.CalculateFogInfo(Precip, f.CamPos.X, f.CamPos.Y, _normalFog, _rainFog, _fogBlendDist);
            Precip.UpdateAndRender(f, maps?.Layer("Rain"), maps?.Layer("Clouds"), _wind, ScreenEffects.Player);
            DrawStreaks(cam);
            _drawScreen = VideoOption(PcVideoSettings.Rain) && Precip.OnScreen.NumOnScreen > 0;
            ScreenEffects.PostDisplay();
            return true;
        }

        PrecipitationInput Input(Camera cam, GameBootstrap boot, float dt)
        {
            var ct = cam.transform;
            if (dt > 0f)
            {
                if (_haveCam) _camVel = (ct.position - _lastCamPos) / dt;   // Camera::VelocityKey: eye movement / frame time
                _lastCamPos = ct.position;
                _haveCam = true;
            }
            var f = new PrecipitationInput
            {
                Dt = dt,
                WorldSeconds = _worldSeconds,
                Paused = dt <= 0f,
                SplitScreen = boot.World.SplitScreen,
                ViewIndex = 0,
                CamPos = Coords.ToGame3(ct.position),
                CamDir = Coords.ToGame3(ct.forward),
                CamVelocity = Coords.ToGame3(_camVel),
                FindZone = (type, x, y) => PoliceZones.First(type, new Vector3(x, 0f, y)),
            };
            // the camera mover's anchor car (CameraAnchor type 1): the chase camera's target
            var chase = cam.GetComponent<ChaseCamera>();
            Transform anchor = chase != null ? chase.Target : null;
            if (anchor != null)
            {
                var rb = anchor.GetComponentInParent<Rigidbody>();
                var t = rb != null ? rb.transform : anchor;
                f.HasCar = true;
                f.CarPos = Coords.ToGame3(rb != null ? rb.position : t.position);
                f.CarFwd = Coords.ToGame3(t.forward);
                f.CarLeft = Coords.ToGame3(-t.right);
                f.CarUp = Coords.ToGame3(t.up);
            }
            return f;
        }

        // ---- 3D streaks and the curtain (Rain::Render 0x74ACC0, UpdateAndRenderCurtain 0x74A5D0) -----------------------

        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<Color32> _c = new List<Color32>();
        readonly List<int> _t = new List<int>();

        void AddStreak(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, in Precipitation.Streak s)
        {
            int b = _v.Count;
            _v.Add(v0); _v.Add(v1); _v.Add(v2); _v.Add(v3);
            // ePoly / sub_6D1E30 vertex order: c-(a1+a2), c+a1-a2, c+a1+a2, c-a1+a2
            _uv.Add(new Vector2(s.U0, s.V0)); _uv.Add(new Vector2(s.U1, s.V0)); _uv.Add(new Vector2(s.U1, s.V1)); _uv.Add(new Vector2(s.U0, s.V1));
            var col = new Color32(0x80, 0x80, 0x80, 0x80);
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _t.Add(b); _t.Add(b + 1); _t.Add(b + 2); _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
        }

        static Vec3 Q(Vec3 c, Vec3 a, float sa, Vec3 b, float sb) => new Vec3(c.X + a.X * sa + b.X * sb, c.Y + a.Y * sa + b.Y * sb, c.Z + a.Z * sa + b.Z * sb);

        void DrawStreaks(Camera cam)
        {
            if (Precip.Drops.Count == 0 && Precip.CurtainDrops.Count == 0) return;
            if (!EnsureDropMaterial()) return;
            _v.Clear(); _uv.Clear(); _c.Clear(); _t.Clear();
            foreach (var s in Precip.Drops)   // rain space -> game (+0x3610) -> Unity
                AddStreak(Coords.ToUnity(Precip.LocalToWorld(Q(s.C, s.A1, -1, s.A2, -1))), Coords.ToUnity(Precip.LocalToWorld(Q(s.C, s.A1, 1, s.A2, -1))),
                          Coords.ToUnity(Precip.LocalToWorld(Q(s.C, s.A1, 1, s.A2, 1))), Coords.ToUnity(Precip.LocalToWorld(Q(s.C, s.A1, -1, s.A2, 1))), s);
            foreach (var s in Precip.CurtainDrops)   // already game space
                AddStreak(Coords.ToUnity(Q(s.C, s.A1, -1, s.A2, -1)), Coords.ToUnity(Q(s.C, s.A1, 1, s.A2, -1)),
                          Coords.ToUnity(Q(s.C, s.A1, 1, s.A2, 1)), Coords.ToUnity(Q(s.C, s.A1, -1, s.A2, 1)), s);
            if (_dropMesh == null) { _dropMesh = new Mesh { name = "Rain" }; _dropMesh.MarkDynamic(); }
            _dropMesh.Clear();
            _dropMesh.SetVertices(_v);
            _dropMesh.SetUVs(0, _uv);
            _dropMesh.SetColors(_c);
            _dropMesh.SetTriangles(_t, 0, false);
            _dropMesh.bounds = new Bounds(cam.transform.position, Vector3.one * 200f);
            Graphics.DrawMesh(_dropMesh, Matrix4x4.identity, _dropMat, 0, cam);
        }

        bool EnsureDropMaterial()
        {
            if (_dropMat) return true;
            var sh = Shader.Find("NFSMW/Particle");
            var tex = GameData.Textures?.Get(RainDropHash);
            if (!sh || !tex) return false;
            var src = GameData.Textures.Source(RainDropHash);
            bool additive = src != null && src.AlphaBlend == 2;
            _dropMat = new Material(sh) { name = "Particles RAINDROP", mainTexture = tex, renderQueue = 3100 };
            _dropMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            _dropMat.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            if (_dropMat.HasProperty("_Additive")) _dropMat.SetFloat("_Additive", additive ? 1f : 0f);
            return true;
        }

        // ---- on-screen drops (sub_6D4620) ----------------------------------------------------------------------------

        void LateUpdate()
        {
            var cam = Camera.main;
            if (!_drawScreen || cam == null || !EnsureScreenMaterial()) { DetachBuffer(); return; }
            if (_bufferCam != cam) { DetachBuffer(); AttachBuffer(cam); }
            BuildScreenMesh();
        }

        bool EnsureScreenMaterial()
        {
            if (_screenMat) return true;
            var sh = Shader.Find("NFSMW/RainDrop");
            var tex = GameData.Textures?.Get(RainDropPcHash);
            if (!sh || !sh.isSupported || !tex) return false;
            _screenMat = new Material(sh) { name = "rain_drop.efx" };
            _screenMat.SetTexture("_DisplaceTex", tex);
            return true;
        }

        static readonly int ScreenCopyId = Shader.PropertyToID("_MW_RainScreenCopy");

        void AttachBuffer(Camera cam)
        {
            _screenMesh ??= new Mesh { name = "Rain drops (screen)" };
            _screenMesh.MarkDynamic();
            _cmd = new CommandBuffer { name = "NFSMW rain_drop" };
            // [ours] the exe draws the drops after the motion blur and before the visual treatment; here they go before
            // the image effects (VisualTreatment's blur and look both run in its OnRenderImage)
            _cmd.GetTemporaryRT(ScreenCopyId, -1, -1, 0, FilterMode.Bilinear);
            _cmd.Blit(BuiltinRenderTextureType.CurrentActive, ScreenCopyId);
            _cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _cmd.SetGlobalTexture("_MW_RainScreen", ScreenCopyId);
            _cmd.DrawMesh(_screenMesh, Matrix4x4.identity, _screenMat, 0, 0);
            _cmd.ReleaseTemporaryRT(ScreenCopyId);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, _cmd);
            _bufferCam = cam;
        }

        void DetachBuffer()
        {
            if (_bufferCam != null && _cmd != null) _bufferCam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _cmd);
            _bufferCam = null;
            _cmd?.Release();
            _cmd = null;
        }

        readonly List<Vector3> _sv = new List<Vector3>();
        readonly List<Vector2> _suv = new List<Vector2>();
        readonly List<Vector4> _soff = new List<Vector4>();
        readonly List<Vector2> _salpha = new List<Vector2>();
        readonly List<int> _st = new List<int>();

        /// <summary>Per drop: GetData(i) -> raindropalpha = decay x 3, raindropoffset = (x - s/4, y + s/4, s, -s) with
        /// s = size x 0.12, and a 2-triangle strip at clip (2x-1, 1-2y) .. (+s, +s), texcoords 0..1.</summary>
        void BuildScreenMesh()
        {
            _sv.Clear(); _suv.Clear(); _soff.Clear(); _salpha.Clear(); _st.Clear();
            var os = Precip.OnScreen;
            for (int i = 0; i < os.NumOnScreen; i++)
            {
                os.GetData(i, out float x, out float y, out float decay, out float size);
                float s = size * 0.12f;                                  // [0x8AF9F4]
                var off = new Vector4(x - s * 0.25f, y + s * 0.25f, s, -s);
                float cx = 2f * x - 1f, cy = 1f - 2f * y;
                int b = _sv.Count;
                _sv.Add(new Vector3(cx, cy, 0f)); _sv.Add(new Vector3(cx + s, cy, 0f));
                _sv.Add(new Vector3(cx, cy + s, 0f)); _sv.Add(new Vector3(cx + s, cy + s, 0f));
                _suv.Add(new Vector2(0, 0)); _suv.Add(new Vector2(1, 0)); _suv.Add(new Vector2(0, 1)); _suv.Add(new Vector2(1, 1));
                for (int k = 0; k < 4; k++) { _soff.Add(off); _salpha.Add(new Vector2(decay * 3f, 0f)); }
                _st.Add(b); _st.Add(b + 1); _st.Add(b + 2); _st.Add(b + 2); _st.Add(b + 1); _st.Add(b + 3);
            }
            _screenMesh.Clear();
            _screenMesh.SetVertices(_sv);
            _screenMesh.SetUVs(0, _suv);
            _screenMesh.SetUVs(1, _soff);
            _screenMesh.SetUVs(2, _salpha);
            _screenMesh.SetTriangles(_st, 0, false);
            _screenMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);
        }
    }
}
