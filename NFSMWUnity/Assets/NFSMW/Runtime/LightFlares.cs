using System.Collections.Generic;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// Light flares (Ecstasy/eLight.cpp; the PC eRenderLightFlare is speed.exe 0x505380). Each eLightFlare type draws up to
    /// two billboards. Their parameters come from the table of pointer pairs at 0x8F2868: size at intensity 0 and at 1,
    /// RGBA (128 = 1, the shader doubles), a pull toward the eye, a min/max world scale, and a texture
    /// (0 HEADLIGHTFLAREINNER, 1 HEADLIGHTFLAREOUTER, 2 HEADLIGHTGLOW). The size is in pixels over the projection distance H:
    /// world size = size x clamp(depth / H, min, max). Flares fade out over 179–200 m (squared 32000..40000). Car flares
    /// (FLARE_NORM) spin with screen x by 240 degrees across the screen. The world flare packs (chunk 0x80135100,
    /// eRenderWorldLightFlares) are absent from the PC track data, so only the car flares are drawn.
    /// </summary>
    [DefaultExecutionOrder(10000)]   // after the camera rigs have moved this frame
    public sealed class LightFlares : MonoBehaviour
    {
        public enum Mode { Norm = 0, Env = 1 }

        struct Layer
        {
            public float Size0, Size1, R, G, B, A, Pull, ScaleMin, ScaleMax;
            public int Tex;
            public Layer(float s0, float s1, float r, float g, float b, float a, float pull, float mn, float mx, int tex)
            { Size0 = s0; Size1 = s1; R = r; G = g; B = b; A = a; Pull = pull; ScaleMin = mn; ScaleMax = mx; Tex = tex; }
        }

        // speed.exe 0x8F2868: per eLightFlareType, two layer pointers (the unused 7th float of each layer is left out)
        static readonly Layer HeadInner = new Layer(0, 64, 100, 120, 170, 80, 0.8f, 0.01f, 0.048f, 0);
        static readonly Layer HeadOuter = new Layer(28, 28, 100, 120, 140, 20, 0.8f, 0.01f, 0.015f, 1);
        static readonly Layer[][] Types =
        {
            new[] { HeadInner, HeadOuter },                                            // 0 car headlight
            new[] { new Layer(20, 30, 128, 25, 25, 120, 0.2f, 0.01f, 0.03f, 2) },     // 1 brakelight
            new[] { new Layer(20, 30, 128, 25, 25, 120, 0.2f, 0.01f, 0.03f, 2) },     // 2 traffic brakelight
            new[] { new Layer(20, 20, 255, 255, 255, 128, 0.2f, 0.005f, 0.01f, 2) },  // 3 reverse
            new[] { new Layer(20, 20, 128, 128, 128, 128, 0.2f, 0.005f, 0.01f, 2) },  // 4 fog light
            new[] { new Layer(40, 50, 100, 20, 20, 150, 1, 0.02f, 0.04f, 0) },         // 5 cop red
            new[] { new Layer(40, 50, 12, 12, 150, 180, 1, 0.03f, 0.04f, 0) },         // 6 cop blue
            new[] { new Layer(30, 40, 100, 100, 100, 128, 1, 0.01f, 0.02f, 0) },       // 7 cop white
            new[] { HeadInner, HeadOuter },                                            // 8 cop headlight right
            new[] { HeadInner, HeadOuter },                                            // 9 cop headlight left
            new[] { new Layer(40, 50, 128, 0, 0, 150, 1, 0.01f, 0.02f, 0) },           // 10 cop bright red
            new[] { new Layer(40, 50, 0, 0, 128, 150, 1, 0.01f, 0.02f, 0) },           // 11 cop bright blue
            new[] { new Layer(30, 40, 128, 128, 128, 128, 1, 0.01f, 0.02f, 0) },       // 12 cop orange
            new[] { new Layer(30, 30, 110, 100, 75, 110, 4, 0.001f, 0.1f, 2) },        // 13 lamppost
            new[] { new Layer(10, 10, 110, 75, 0, 150, 2.7f, 0.001f, 0.1f, 2) },       // 14 catseye orange
            new[] { new Layer(10, 10, 110, 0, 0, 150, 4, 0.001f, 0.1f, 2) },           // 15 catseye red
            new[] { new Layer(10, 10, 0, 0, 120, 150, 4, 0.001f, 0.1f, 2) },           // 16 catseye blue
            new[] { new Layer(30, 30, 110, 75, 0, 150, 4, 0.001f, 0.1f, 2) },          // 17 blinking amber
            new[] { new Layer(30, 30, 110, 10, 0, 150, 4, 0.001f, 0.1f, 2) },          // 18 blinking red
            new[] { new Layer(1, 10, 0, 100.5f, 20, 70, 1, 0.07f, 0.07f, 2) },         // 19 blinking green
            new[] { new Layer(1, 10, 105, 26.5f, 0, 70, 1, 0.07f, 0.07f, 2) },         // 20 hand flare
            new[] { new Layer(1, 20, 255, 255.5f, 255, 255, 5, 0.8f, 1, 1) },          // 21 sun
        };
        static readonly uint[] TextureHashes = { 0x8868656F, 0x88D8E8A2, 0x3394FE62 };   // 0x8F2434
        const float FadeNear = 32000f, FadeFar = 40000f, SpinDegrees = 240f;

        static LightFlares _instance;
        readonly Material[] _mats = new Material[3];
        readonly Mesh[] _meshes = new Mesh[3];
        // the road reflection view's flares (FLARE_REFLECT, world_render.md §4): always HEADLIGHTGLOW (0x505A24)
        Mesh _refl;
        readonly List<Vector3> _rv = new List<Vector3>();
        readonly List<Vector2> _ruv = new List<Vector2>();
        readonly List<Color> _rc = new List<Color>();
        readonly List<int> _rt = new List<int>();
        float _reflStretch;
        /// <summary>True while this frame's flares are also drawn into the road reflection view.</summary>
        public bool Reflecting { get; private set; }
        const float MinReflectionStretch = 1.2f, MaxReflectionStretch = 10f;   // [0x8F23F0] MINreflectionStretch, [0x8F23F4]
        const float HalfStretchHeight = 4f;                                     // flt_899C8C (0x505C40)
        readonly List<Vector3>[] _v = { new List<Vector3>(), new List<Vector3>(), new List<Vector3>() };
        readonly List<Vector2>[] _uv = { new List<Vector2>(), new List<Vector2>(), new List<Vector2>() };
        readonly List<Color>[] _c = { new List<Color>(), new List<Color>(), new List<Color>() };
        readonly List<int>[] _t = { new List<int>(), new List<int>(), new List<int>() };
        public static readonly List<CarFlares> Cars = new List<CarFlares>();

        public static LightFlares Instance
        {
            get
            {
                if (_instance) return _instance;
                var go = new GameObject("Light Flares");
                DontDestroyOnLoad(go);
                return _instance = go.AddComponent<LightFlares>();
            }
        }

        bool Ready()
        {
            if (_mats[0]) return true;
            var sh = Shader.Find("NFSMW/WorldDecal");
            if (!sh || GameData.Textures == null) return false;
            for (int i = 0; i < 3; i++)
            {
                var tex = GameData.Textures.Get(TextureHashes[i]);
                if (!tex) return false;
                _mats[i] = new Material(sh) { name = "Flare " + i, mainTexture = tex, renderQueue = 3100 };
                _mats[i].SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _mats[i].SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                _meshes[i] = new Mesh { name = "Flares " + i };
                _meshes[i].MarkDynamic();
            }
            return true;
        }

        Camera _cam;
        float _h;
        Vector3 _camPos, _camFwd, _camRight, _camUp;

        void LateUpdate()
        {
            _cam = Camera.main;
            if (!_cam || !Ready()) return;
            for (int i = 0; i < 3; i++) { _v[i].Clear(); _uv[i].Clear(); _c[i].Clear(); _t[i].Clear(); }
            var ct = _cam.transform;
            _camPos = ct.position; _camFwd = ct.forward; _camRight = ct.right; _camUp = ct.up;
            // H: the projection distance in 640-wide pixels (the game's flare sizes are pixels on its 640 x 480 FE scale)
            float hfov = 2f * Mathf.Atan(Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * _cam.aspect);
            _h = 320f / Mathf.Tan(hfov * 0.5f);

            // eDisplayFrame 0x6DE5EE: the reflection view gets RenderVehicleFlares(view 4, reflection 1) when the light-glow
            // option [0x9017E4] is on; eRenderLightFlare 0x5053A2: reflectionStretch = MIN + (MAX - MIN) x the view's wetness
            Reflecting = World.RoadReflection.WantsReflection && World.RoadReflection.LightGlow;
            _rv.Clear(); _ruv.Clear(); _rc.Clear(); _rt.Clear();
            _reflStretch = MinReflectionStretch + (MaxReflectionStretch - MinReflectionStretch) * World.Rain.RoadWetness;

            foreach (var car in Cars) if (car && car.isActiveAndEnabled) car.Emit(this);
            if (Reflecting)
            {
                if (!_refl) { _refl = new Mesh { name = "Reflected flares" }; _refl.MarkDynamic(); }
                _refl.Clear();
                if (_rv.Count > 0)
                {
                    _refl.SetVertices(_rv);
                    _refl.SetUVs(0, _ruv);
                    _refl.SetColors(_rc);
                    _refl.SetTriangles(_rt, 0, false);
                    _refl.bounds = new Bounds(_camPos, Vector3.one * 1000f);
                }
            }
            for (int i = 0; i < 3; i++)
            {
                var m = _meshes[i];
                m.Clear();
                if (_v[i].Count == 0) continue;
                m.SetVertices(_v[i]);
                m.SetUVs(0, _uv[i]);
                m.SetColors(_c[i]);
                m.SetTriangles(_t[i], 0, false);
                m.bounds = new Bounds(_camPos, Vector3.one * 1000f);
                Graphics.DrawMesh(m, Matrix4x4.identity, _mats[i], 0, _cam);
            }
        }

        /// <summary>RoadReflection hook: queues this frame's reflected flares into the reflection camera.</summary>
        public void DrawReflection(Camera cam, int layer)
        {
            if (!Reflecting || !_refl || _rv.Count == 0 || !_mats[2]) return;
            Graphics.DrawMesh(_refl, Matrix4x4.identity, _mats[2], layer, cam);
        }

        Mesh _envSun;
        readonly List<Vector3> _sv = new List<Vector3>();
        readonly List<Vector2> _suv = new List<Vector2>();
        readonly List<Color> _sc = new List<Color>();
        readonly List<int> _st = new List<int>();

        /// <summary>
        /// CarEnvironment hook, once per env-map face (eDisplayFrame, light-glow option [0x9017E4]): RenderVehicleFlares
        /// (view, 0, 1) draws every car's flares into the env views — this frame's flare meshes [ours: their quads face the
        /// player camera] — and eRenderLightFlarePool draws RenderSunAsFlare's first pool flare (0x503830, type 21, exclude
        /// mask 0xFE = not in views 1-7, so only the env maps and RVM see it): a quad at the player camera + 40 m along
        /// the time-of-day sun direction (TOD+0x170), intensity 1 [?], layer (size 1..20, white, alpha 255, pull 5).
        /// It is the sun glint on the car paint.
        /// </summary>
        public void DrawEnv(Camera envCam, Vector3 eye)
        {
            if (!envCam || !Ready()) return;
            for (int i = 0; i < 3; i++)
                if (_meshes[i] && _v[i].Count > 0) Graphics.DrawMesh(_meshes[i], Matrix4x4.identity, _mats[i], 0, envCam);
            if (!_cam || !World.RoadReflection.LightGlow) return;
            Vector4 sun = Shader.GetGlobalVector("_MW_LightDir");
            if (sun.sqrMagnitude < 1e-6f) return;
            var pos = _camPos + ((Vector3)sun).normalized * 40f;
            var toEye = eye - pos;
            float d = toEye.magnitude;
            if (d < 1e-3f) return;
            toEye /= d;
            // a quad facing the env eye (the faces look every way); H for a 90° face as 640-wide pixels
            var up = Vector3.Cross(toEye, Vector3.Cross(Vector3.up, toEye)).normalized;
            if (up.sqrMagnitude < 0.5f) up = Vector3.up;
            var right = Vector3.Cross(up, toEye).normalized;
            const float h = 320f;
            _sv.Clear(); _suv.Clear(); _sc.Clear(); _st.Clear();
            int tex = -1;
            foreach (var l in Types[21])
            {
                float size = l.Size1;                                   // intensity 1
                var p = pos + toEye * l.Pull;
                float scale = Mathf.Clamp(Vector3.Dot(p - eye, -toEye) / h, l.ScaleMin, l.ScaleMax);
                float half = size * scale * 0.5f;
                var col = new Color(l.R / 255f, l.G / 255f, l.B / 255f, Mathf.Min(255f, l.A * 2f) / 255f);
                int b = _sv.Count;
                tex = l.Tex;
                _sv.Add(p - right * half - up * half); _suv.Add(new Vector2(0, 0));
                _sv.Add(p + right * half - up * half); _suv.Add(new Vector2(1, 0));
                _sv.Add(p - right * half + up * half); _suv.Add(new Vector2(0, 1));
                _sv.Add(p + right * half + up * half); _suv.Add(new Vector2(1, 1));
                for (int k = 0; k < 4; k++) _sc.Add(col);
                _st.Add(b); _st.Add(b + 2); _st.Add(b + 1);
                _st.Add(b + 1); _st.Add(b + 2); _st.Add(b + 3);
            }
            if (tex < 0) return;
            if (!_envSun) { _envSun = new Mesh { name = "Env sun flare" }; _envSun.MarkDynamic(); }
            _envSun.Clear();
            _envSun.SetVertices(_sv);
            _envSun.SetUVs(0, _suv);
            _envSun.SetColors(_sc);
            _envSun.SetTriangles(_st, 0, false);
            _envSun.bounds = new Bounds(pos, Vector3.one * 100f);
            Graphics.DrawMesh(_envSun, Matrix4x4.identity, _mats[tex], 0, envCam);
        }

        /// <summary>
        /// eRenderLightFlare (0x505380) with flare type FLARE_REFLECT (2), as RenderFlaresOnCar calls it for the road
        /// reflection view (reflexion != 0: REF_TOPO, the car's elevation CarRenderInfo +0x116C, size scale 1):
        ///   * the flare's height is mirrored about the elevation: z' = elev - (z - elev) (0x50543F);
        ///   * texture HEADLIGHTGLOW for every layer (0x505A24), no screen-x spin (only FLARE_NORM spins, 0x505B6A);
        ///   * the quad's up vector is stretched by reflectionStretch, by half of it when z - z' &lt; 4 (0x505C3A), so the
        ///     light becomes a vertical streak on the wet road;
        ///   * fade 32000..40000 (squared distance) as FLARE_NORM (0x5057E9).
        /// </summary>
        public void AddReflected(Vector3 pos, int type, float intensity, float elevation)
        {
            if (!Reflecting || type < 0 || type >= Types.Length || intensity <= 0) return;
            float y = elevation - (pos.y - elevation);
            var at = new Vector3(pos.x, y, pos.z);
            var toEye = _camPos - at;
            float d2 = toEye.sqrMagnitude;
            if (d2 > FadeFar) return;
            float fade = d2 > FadeNear ? 1f - (d2 - FadeNear) / (FadeFar - FadeNear) : 1f;
            toEye /= Mathf.Sqrt(d2) + 1e-4f;
            float depth = Vector3.Dot(at - _camPos, _camFwd);
            if (depth < _cam.nearClipPlane) return;
            float stretch = pos.y - y < HalfStretchHeight ? 0.5f * _reflStretch : _reflStretch;
            intensity = Mathf.Min(intensity, 1f);
            foreach (var l in Types[type])
            {
                float size = l.Size0 + (l.Size1 - l.Size0) * intensity;
                if (size <= 0) continue;
                var p = at + toEye * l.Pull;
                float scale = Mathf.Clamp(Vector3.Dot(p - _camPos, _camFwd) / _h, l.ScaleMin, l.ScaleMax);
                float half = size * scale * 0.5f;
                float a = Mathf.Min(255f, l.A * intensity * fade * 2f) / 255f;
                var col = new Color(l.R * intensity / 255f, l.G * intensity / 255f, l.B * intensity / 255f, a);
                var right = _camRight * half;
                var up = _camUp * (half * stretch);
                int b = _rv.Count;
                _rv.Add(p - right - up); _ruv.Add(new Vector2(0, 0));
                _rv.Add(p + right - up); _ruv.Add(new Vector2(1, 0));
                _rv.Add(p - right + up); _ruv.Add(new Vector2(0, 1));
                _rv.Add(p + right + up); _ruv.Add(new Vector2(1, 1));
                for (int k = 0; k < 4; k++) _rc.Add(col);
                _rt.Add(b); _rt.Add(b + 2); _rt.Add(b + 1);
                _rt.Add(b + 1); _rt.Add(b + 2); _rt.Add(b + 3);
            }
        }

        /// <summary>One flare at a world position: its type's layers as camera-facing quads.</summary>
        public void Add(Vector3 pos, Vector3 dir, int type, float intensity, float sizeScale, Mode mode)
        {
            if (type < 0 || type >= Types.Length || intensity <= 0) return;
            var toEye = _camPos - pos;
            float d2 = toEye.sqrMagnitude;
            float near = mode == Mode.Env ? 3900f : FadeNear, far = mode == Mode.Env ? 4000f : FadeFar;
            if (d2 > far) return;
            float fade = d2 > near ? 1f - (d2 - near) / (far - near) : 1f;
            toEye /= Mathf.Sqrt(d2) + 1e-4f;
            float depth = Vector3.Dot(pos - _camPos, _camFwd);
            if (depth < _cam.nearClipPlane) return;
            // spin with screen x (FLARE_NORM)
            float angle = 0;
            if (mode == Mode.Norm)
            {
                var sp = _cam.WorldToViewportPoint(pos);
                angle = (sp.x - 0.5f) * SpinDegrees * Mathf.Deg2Rad;
            }
            float ca = Mathf.Cos(angle), sa = Mathf.Sin(angle);
            var right = _camRight * ca + _camUp * sa;
            var up = -_camRight * sa + _camUp * ca;
            intensity = Mathf.Min(intensity, 1f);
            foreach (var l in Types[type])
            {
                float size = l.Size0 + (l.Size1 - l.Size0) * intensity;
                if (size <= 0) continue;
                var p = pos + toEye * l.Pull;
                float scale = Mathf.Clamp(Vector3.Dot(p - _camPos, _camFwd) / _h, l.ScaleMin, l.ScaleMax);
                float half = size * scale * sizeScale * 0.5f;
                float a = Mathf.Min(255f, l.A * intensity * fade * 2f) / 255f;
                var col = new Color(l.R * intensity / 255f, l.G * intensity / 255f, l.B * intensity / 255f, a);
                int t = l.Tex, b = _v[t].Count;
                _v[t].Add(p - right * half - up * half); _uv[t].Add(new Vector2(0, 0));
                _v[t].Add(p + right * half - up * half); _uv[t].Add(new Vector2(1, 0));
                _v[t].Add(p - right * half + up * half); _uv[t].Add(new Vector2(0, 1));
                _v[t].Add(p + right * half + up * half); _uv[t].Add(new Vector2(1, 1));
                for (int k = 0; k < 4; k++) _c[t].Add(col);
                _t[t].Add(b); _t[t].Add(b + 2); _t[t].Add(b + 1);
                _t[t].Add(b + 1); _t[t].Add(b + 2); _t[t].Add(b + 3);
            }
        }
    }

    /// <summary>
    /// A car's light flares (CarRenderInfo::CreateCarLightFlares / RenderFlaresOnCar). The flares sit on the body
    /// parts' position markers. Headlights burn at 1. Brake lights rest at 0.5 and go to full under braking
    /// (BrakeLightScaleFactor 16, clamped); the centre one is off until then. Reverse lights show in reverse gear.
    /// Cop bars strobe with coplightflicker2 while CopLightsOn.
    /// </summary>
    public sealed class CarFlares : MonoBehaviour
    {
        public struct Flare { public uint Hash; public int Type; public Vector3 Local; }

        public readonly List<Flare> Flares = new List<Flare>();
        public bool Traffic, CopLightsOn, Headlights = true;
        Transform _body;
        PlayerCar _car;
        CarRender.CarRenderInfo _render;
        float _timebase;

        const uint LeftHead = 0x9DB90133, RightHead = 0xD09091C6, CentreHead = 0x7A5BCF69;
        const uint LeftBrake = 0x31A66786, RightBrake = 0xBF700A79, CentreBrake = 0xA2A2FC7C;
        const uint LeftReverse = 0x7A5B2F25, RightReverse = 0x7ADF7EF8;
        const uint CopRed = 0x1E4150B4, CopBlue = 0xE662C161, CopWhite = 0xB4348DBA;
        const uint CopBrightRed = 0x41489594, CopBrightBlue = 0x6A52A241, CopOrange = 0x28CD78F5;

        public static int TypeOf(uint hash)
        {
            switch (hash)
            {
                case LeftHead: case CentreHead: case RightHead: return 0;
                case LeftBrake: case CentreBrake: case RightBrake: return 1;
                case CopRed: return 5;
                case CopBlue: return 6;
                case CopWhite: return 7;
                case CopBrightRed: return 10;
                case CopBrightBlue: return 11;
                case CopOrange: return 12;
                case LeftReverse: case RightReverse: return 3;
                default: return -1;
            }
        }

        /// <summary>Adds the flares of a body part's markers (marker matrices are in game car space).</summary>
        public void AddMarkers(Core.Solid s, Transform body)
        {
            _body = body;
            foreach (var (hash, m) in s.Markers)
            {
                int type = TypeOf(hash);
                if (type < 0) continue;
                // one reverse light per side (the first part that has it)
                if (type == 3 && Flares.Exists(f => f.Hash == hash)) continue;
                Flares.Add(new Flare { Hash = hash, Type = type, Local = Coords.ToUnity(m[12], m[13], m[14]) });
            }
        }

        void OnEnable()
        {
            LightFlares.Cars.Add(this);
            _ = LightFlares.Instance;
            _timebase = Random.value * 10f;
        }

        void OnDisable() => LightFlares.Cars.Remove(this);

        static float Flicker(float time, int offset) => Mathf.Cos((time + 1.5f * offset) * 12f);   // copt 1.5, copm 12

        static float Flicker2(float time, int colour, int count)
        {
            float t = Mathf.Cos(2f * Mathf.PI / (Mathf.PI / 4f / 3f) * time);   // FlarePeriodShort = pi/12
            t *= t;
            if (colour == 2) return t * Flicker(time, count);
            float offset = colour == 0 ? 0f : colour == 1 ? Mathf.PI : 4f;
            return Mathf.Cos(time * 8f + offset) > 0.2f ? t : 0f;
        }

        static readonly RaycastHit[] Hits = new RaycastHit[8];
        int _elevationFrame = -1;
        float _elevation;

        /// <summary>
        /// CarRenderInfo::mCar_elevation (+0x116C, CarRender.cpp:4276): the world height under the car
        /// (WWorldPos::HeightAtPoint) - [ours] a ray down from the body that ignores the car's own colliders.
        /// </summary>
        internal float Elevation()
        {
            if (_elevationFrame == Time.frameCount) return _elevation;
            _elevationFrame = Time.frameCount;
            _elevation = _body.position.y;
            float best = float.MaxValue;
            int n = Physics.RaycastNonAlloc(_body.position + Vector3.up * 1.5f, Vector3.down, Hits, 20f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.collider == null || h.collider.transform.IsChildOf(transform)) continue;
                if (h.distance < best) { best = h.distance; _elevation = h.point.y; }
            }
            return _elevation;
        }

        internal void Emit(LightFlares r)
        {
            if (!_body || Flares.Count == 0) return;
            if (!_car) _car = GetComponent<PlayerCar>();
            if (!_car) return;   // showroom cars: the garage scenes draw no flares here
            Traffic = _car.Traffic;
            float time = Time.time + _timebase;
            bool braking = _car && _car.Input.Brake > 0.05f && _car.Gear >= 0;
            bool reversing = _car && _car.Gear < 0;
            bool nis = Game.NisPlayer.Active;
            float head = Headlights && !nis ? 1f : 0f;
            float brake = 0.5f + (braking ? 16f : 0f), centre = braking ? 16.5f : 0f, reverse = reversing ? 17f : 0f;
            float copR = CopLightsOn ? 1f : 0f, copB = copR, copW = copR;
            float constFlicker = Flicker(time, 0);
            int count = 0;
            if (!_render) _render = GetComponent<CarRender.CarRenderInfo>();
            foreach (var f in Flares)
            {
                // CarRenderInfo::IsLightBroken: a broken lamp draws no flare (car_render.md §5)
                if (_render && _render.IsLightBroken(CarRender.CarRenderInfo.LightBit(f.Hash))) { count++; continue; }
                float k = 0;
                switch (f.Hash)
                {
                    case LeftHead: k = CopLightsOn ? head * constFlicker : head; break;
                    case RightHead: k = CopLightsOn ? head * (1f - constFlicker) : head; break;
                    case CentreHead: k = 0; break;
                    case LeftBrake: case RightBrake: k = brake; break;
                    case CentreBrake: k = centre; break;
                    case LeftReverse: case RightReverse: k = reverse; break;
                    case CopRed: copR *= Flicker2(time, 0, count); k = copR; break;
                    case CopBlue: copB *= Flicker2(time, 1, count); k = copB; break;
                    case CopBrightRed: k = copR * Flicker2(time, 0, count); break;
                    case CopBrightBlue: k = copB * Flicker2(time, 1, count); break;
                    case CopWhite: k = Mathf.Sin(copW * Flicker2(time, 2, count) * 3f); break;   // copWhitemul 3
                    case CopOrange: k = CopLightsOn ? 1f : 0f; break;
                }
                int type = Traffic && f.Type == 1 ? 2 : f.Type;
                if (k > 0)
                {
                    var at = _body.TransformPoint(f.Local);
                    r.Add(at, Vector3.zero, type, Mathf.Min(k, 1f), 1f, LightFlares.Mode.Norm);
                    // RenderFlaresOnCar(view 4, reflexion 1): the same intensities, FLARE_REFLECT (CarRender.cpp:2728)
                    if (r.Reflecting) r.AddReflected(at, type, Mathf.Min(k, 1f), Elevation());
                }
                count++;
            }
        }
    }
}
