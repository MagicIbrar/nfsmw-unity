using NfsMw.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw
{
    /// <summary>
    /// MW's full-screen look (World/VisualTreatment.cpp, shader fx\visualtreatment.efx). Midday uses the `visuallook`
    /// collection 0xEEC2271A, which is absent in the data and so falls back to `default`. The heat look 0x681BEF75
    /// ("uves", blue) flashes in when the heat level rises or a pursuit starts and fades over the UvesTransition
    /// graph, with a brightness pulse (UvesPulse) and radial blur. NOS (0x6B40EB80) and the speedbreaker
    /// (pursuit breaker 0x90D06C71) ramp in their own radial blur. Front-end cameras use the plain midday look.
    /// On PC no shader reads the radial blur value. It only drives eDisplayFrame's motion blur (sub_6DBB20, on while
    /// the g_MotionBlurEnable option is set): the frame is filtered twice with 4 taps stretched along the camera's
    /// view-space velocity, then blended back over the frame by a blend factor.
    /// After it comes the rest of eDisplayFrame's post chain (NFSMW_RE/specs/world_render.md, "End-of-frame post chain"):
    /// the 4x4-box quarter-size bloom source and its 2 x (H, V) filter.efx blur (sub_6DBEC0 / sub_6DBFE0), the visual
    /// treatment with the CPU constants of sub_6D4EF0 and the UVESVIGNETTE gain map, then the overbright pass
    /// (sub_6DC040): the sky (and tunnel glare) alpha mask, pContrast, blur, added with a blend factor.
    /// </summary>
    [RequireComponent(typeof(Camera)), ExecuteAlways]
    public sealed class VisualTreatment : MonoBehaviour
    {
        public bool FrontEnd;

        sealed class Look
        {
            public float[] BlackCurve = new float[4], ColourCurve = new float[4];
            public Vector4 Tint = new Vector4(0.88f, 0.8f, 0.44f, 1);
            public float ColourIntensity = 1.75f, Desaturation = 0.5f, BlackIntensity = 0.9f;
        }

        sealed class Effect
        {
            public float[] Graph = { 0, 0, 1, 1 };
            public float Length = 1, Magnitude = 1, RadialBlurScale = 1;
        }

        static Look _midday, _uves;
        static Effect _transition, _pulse, _uvesBlur, _breaker, _nos;
        static bool _loaded;
        static readonly Look Blend = new Look();

        Material _mat;
        Texture2D _spline;
        float _uvesStart = -1, _blurStart = -1, _pulseStart = -1, _breakerBlend, _nosBlend;
        int _lastHeat = -1;
        bool _lastPursuit;

        /// <summary>g_MotionBlurEnable (0x9017DC), the display option that turns eDisplayFrame's motion blur on.</summary>
        public static bool MotionBlurEnable = true;
        static bool _flush;
        float _motion;              // 0x982C6C, eased toward 0.2 x the radial blur
        float _blurAlpha;           // this frame's blend factor (0 = no blur)
        Vector4 _blurRect;          // left, top, right, bottom tap stretch
        Vector3 _lastEye;
        bool _haveEye;

        /// <summary>[0x901828] the visual treatment option (preset 0x6C1990: on at performance level 4+).</summary>
        public static bool VisualTreatmentEnable = true;
        /// <summary>[0x9017FC] the overbright option (preset 0x6C1990: on at performance level 4+).</summary>
        public static bool OverbrightEnable = true;
        const int BlurIterations = 2;                       // [0x8F9474]
        static readonly uint GainMapHash = EaHash.Hash("UVESVIGNETTE");   // IVisualTreatmentPlat ctor 0x6DC377
        Texture _gain;
        bool _gainTried;
        Camera _cam;
        CommandBuffer _mask, _sunQuery;
        Mesh _quad, _glareMesh;
        Sky.SkyRender _dome;
        int _domeSearch;
        float _sun = 1f, _cloud;                            // [0x8FAE64] (0x6C83D0), player Rain +0x290

        /// <summary>FlushAccumulationBuffer 0x725550: camera cuts (mover snaps 0x47CF00, ICE key cuts 0x482160) zero the
        /// blur target for a frame.</summary>
        public static void FlushAccumulationBuffer() => _flush = true;

        static void LoadAttributes()
        {
            if (_loaded) return;
            _loaded = true;
            var db = GameData.Attributes;
            AttribCollection Find(uint cls, uint key)
            {
                if (db == null) return null;
                if (db.Collections.TryGetValue((cls, key), out var c)) return c;
                return db.Collections.TryGetValue((cls, AttribDatabase.Hash("default")), out c) ? c : null;
            }
            Look ReadLook(uint key)
            {
                var c = Find(0x339F7D3D, key);
                var l = new Look();
                if (c == null) return l;
                l.BlackCurve = CurveY(c, "BlackBloomCurve");
                l.ColourCurve = CurveY(c, "ColourBloomCurve");
                if (c.TryGet("ColourBloomTint", out int at, out _, out _))
                    l.Tint = new Vector4(Chunks.F32(c.Source.Data, at), Chunks.F32(c.Source.Data, at + 4), Chunks.F32(c.Source.Data, at + 8), 1);
                l.ColourIntensity = c.Float("ColourBloomIntensity", 0, l.ColourIntensity);
                l.Desaturation = c.Float("Desaturation", 0, l.Desaturation);
                l.BlackIntensity = c.Float("BlackBloomIntensity", 0, l.BlackIntensity);
                return l;
            }
            Effect ReadEffect(uint key)
            {
                var c = Find(0x6AB2D241, key);
                var e = new Effect();
                if (c == null) return e;
                e.Graph = CurveY(c, "graph");
                e.Length = c.Float("length", 0, 1f);
                e.Magnitude = c.Float("magnitude", 0, 1f);
                e.RadialBlurScale = c.Float("radialblur_scale", 0, 1f);
                return e;
            }
            _midday = ReadLook(0xEEC2271A);
            _uves = ReadLook(0x681BEF75);
            _transition = ReadEffect(0x15746132);
            _pulse = ReadEffect(0x334F1E4D);
            _uvesBlur = ReadEffect(0xBFA7B6AC);
            _breaker = ReadEffect(0x90D06C71);
            _nos = ReadEffect(0x6B40EB80);
        }

        /// <summary>The y of a Matrix4 spline's four control points (GetValueFromSpline uses only .y).</summary>
        static float[] CurveY(AttribCollection c, string field)
        {
            var y = new float[] { 0, 0, 1, 1 };
            if (!c.TryGet(field, out int at, out _, out _)) return y;
            for (int i = 0; i < 4; i++) y[i] = Chunks.F32(c.Source.Data, at + i * 16 + 4);
            return y;
        }

        /// <summary>GetValueFromSpline: a cubic Bézier over the control points' y.</summary>
        static float Spline(float[] p, float t)
        {
            float m = 1 - t;
            return m * m * m * p[0] + 3 * t * m * m * p[1] + 3 * t * t * m * p[2] + t * t * t * p[3];
        }

        static float Run(Effect e, float start, float now, float length = -1f)
        {
            if (start < 0) return 0;
            float len = length > 0f ? length : e.Length;
            float s = now - start;
            if (s >= len) return Spline(e.Graph, 1) * e.Magnitude;
            return Spline(e.Graph, Mathf.Clamp01(s / Mathf.Max(0.01f, len))) * e.Magnitude;
        }

        /// <summary>The pulse length set by TriggerPulse (-1 = the effect's own, the heat-rise path).</summary>
        float _pulseLength = -1f;

        /// <summary>
        /// IVisualTreatment::TriggerPulse(length) (0x73DEF0): restarts the pulse effect at the real timer ([0x925B14]) with
        /// the given length, or the pulse effect's own length (+0x44) when 0. Callers: CDActionDrive's smooth-return flash
        /// (ChaseCamera.TriggerPulse, 0.2 s) [round 9 hook].
        /// </summary>
        public void TriggerPulse(float length)
        {
            if (_pulse == null) return;
            float len = length != 0f ? length : _pulse.Length;
            if (len == 0f) return;
            _pulseStart = Time.unscaledTime;
            _pulseLength = len;
        }

        void OnEnable()
        {
            ChaseCamera.TriggerPulse = TriggerPulse;
            if (_mat) return;
            var sh = Shader.Find("NFSMW/VisualTreatment");
            if (sh) _mat = new Material(sh) { hideFlags = HideFlags.DontSave };
            // 0x6C3328: 256 x 1 A8R8G8B8; spline_sample is POINT filtered, CLAMP (visualtreatment.efx sampler states)
            _spline = new Texture2D(256, 1, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            _cam = GetComponent<Camera>();
            // speed.exe renders into an A8R8G8B8 back buffer (D3DFMT_A8R8G8B8, gamma-space values clamped to 0..1 after
            // every blend); an HDR (half float) camera target keeps additive layers above 1 for the blends and copies
            // that follow, so the player camera renders LDR as the exe does (world_render.md §10)
            if (_cam && Application.isPlaying) _cam.allowHDR = false;
            if (_cam && _mask == null && Application.isPlaying)
            {
                _mask = new CommandBuffer { name = "NFSMW overbright alpha mask" };
                _cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, _mask);
                // the sun's occlusion query (sub_6D0350) after all geometry, before the mask rebuilds the alpha
                _sunQuery = new CommandBuffer { name = "NFSMW sun query" };
                _cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _sunQuery);
            }
        }

        void OnDisable()
        {
            if (_mat) Destroy(_mat);
            if (_spline) Destroy(_spline);
            if (_cam && _mask != null) _cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _mask);
            if (_cam && _sunQuery != null) _cam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _sunQuery);
            _mask?.Release();
            _mask = null;
            _sunQuery?.Release();
            _sunQuery = null;
            if (_quad) Destroy(_quad);
            if (_glareMesh) Destroy(_glareMesh);
        }

        /// <summary>
        /// The back buffer's alpha that the overbright source multiplies by: cleared to 0, the tunnel glare quad, then the
        /// visible sky (eDisplayFrame 0x6E7628 clear, 0x6DEFCE glare, 0x6DF409 sky pass 0 with ColorWriteMode 8). The exe
        /// writes it before the motion blur and the particles; both only touch RGB there. Here it is drawn after them, which
        /// gives the same alpha: the particles do not write depth and the motion-blur pass keeps the alpha (pass 5).
        /// The sun sprite (sub_6D04E0, FE effect with FECOLORWRITEFLAG 0xF) then writes RGB and alpha: Sky/SunRender.cs.
        /// </summary>
        void OnPreRender()
        {
            if (_mask == null || !_mat) return;
            _mask.Clear();
            _sunQuery?.Clear();
            if (!FrontEnd && _sunQuery != null) Sky.SunRender.Query(_sunQuery, _cam);
            if (FrontEnd) return;
            if (OverbrightEnable) DrawAlphaMask();
            Sky.SunRender.Draw(_mask, _cam);   // the sun sprite sub_6D04E0 (RGBA, after the mask; world_render.md §5)
        }

        void DrawAlphaMask()
        {
            if (!_quad)
            {
                _quad = new Mesh { name = "clip quad", hideFlags = HideFlags.DontSave };
                _quad.SetVertices(new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0) });
                _quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            }
            _mask.DrawMesh(_quad, Matrix4x4.identity, _mat, 0, 9);
            var glare = World.ScreenEffects.Glare;
            if (glare.Active)
            {
                if (!_glareMesh) _glareMesh = new Mesh { name = "glare quad", hideFlags = HideFlags.DontSave };
                _glareMesh.SetVertices(new[] { glare.A, glare.B, glare.C, glare.D });
                _glareMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                _glareMesh.bounds = new Bounds(glare.A, Vector3.one * 1e6f);
                _mask.DrawMesh(_glareMesh, Matrix4x4.identity, _mat, 0, 11);
            }
            if (!_dome && --_domeSearch <= 0)
            {
                _dome = FindFirstObjectByType<Sky.SkyRender>();
                _domeSearch = 60;
            }
            if (_dome && _dome.isActiveAndEnabled)
            {
                var mf = _dome.GetComponent<MeshFilter>();
                var mesh = mf ? mf.sharedMesh : null;
                if (mesh)
                    for (int i = 0; i < mesh.subMeshCount; i++)
                        _mask.DrawMesh(mesh, _dome.transform.localToWorldMatrix, _mat, i, 10);
            }
        }

        void Update()
        {
            if (!_loaded && GameData.Attributes != null) LoadAttributes();
            if (!_mat) OnEnable();
            if (!_loaded) return;
            float now = Time.time, real = Time.unscaledTime;
            float uves = 0, pulse = 0, blur = 0;
            if (!FrontEnd)
            {
                var police = Game.Systems.Police;
                var car = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
                bool pursuit = police != null && police.InPursuit && !police.InCooldown;
                int heat = police != null ? police.Heat : 0;
                // UpdateHeat: a heat rise, or a pursuit starting, triggers the transition, pulse and blur
                if ((_lastHeat >= 0 && heat > _lastHeat) || (pursuit && !_lastPursuit)) { _uvesStart = now; _blurStart = now; _pulseStart = real; _pulseLength = -1f; }
                _lastHeat = heat;
                _lastPursuit = pursuit;
                uves = Mathf.Clamp01(Run(_transition, _uvesStart, now));
                pulse = Run(_pulse, _pulseStart, real, _pulseLength);
                blur = Run(_uvesBlur, _blurStart, now);
                // VisualLookEffectTarget: the speedbreaker and NOS blends follow their graphs toward 1 or 0
                bool breaker = car && car.SpeedBreakerOn;
                _breakerBlend = Mathf.MoveTowards(_breakerBlend, breaker ? 1f : 0f, Time.unscaledDeltaTime / Mathf.Max(0.05f, _breaker.Length));
                bool nos = car && car.NosFiring;
                if (nos && _nosBlend < 1f) _nosBlend = 1f;
                _nosBlend = Mathf.MoveTowards(_nosBlend, nos ? 1f : 0f, Time.deltaTime / Mathf.Max(0.05f, _nos.Length));
                float breakerBlur = Mathf.Clamp01(Spline(_breaker.Graph, _breakerBlend) * _breaker.RadialBlurScale);
                float nosBlur = Mathf.Clamp(Spline(_nos.Graph, _nosBlend) * _nos.RadialBlurScale, 0, 2);
                blur = Mathf.Max(blur, Mathf.Max(nosBlur, breakerBlur));
            }
            // BlendVisualLookAttribute: (1 - uves)·midday + uves·heat
            float d = 1f - uves;
            for (int i = 0; i < 4; i++)
            {
                Blend.BlackCurve[i] = d * _midday.BlackCurve[i] + uves * _uves.BlackCurve[i];
                Blend.ColourCurve[i] = d * _midday.ColourCurve[i] + uves * _uves.ColourCurve[i];
            }
            Blend.Tint = d * _midday.Tint + uves * _uves.Tint;
            Blend.ColourIntensity = d * _midday.ColourIntensity + uves * _uves.ColourIntensity;
            Blend.Desaturation = d * _midday.Desaturation + uves * _uves.Desaturation;
            Blend.BlackIntensity = d * _midday.BlackIntensity + uves * _uves.BlackIntensity;
            // UpdateHeat 0x74BFBE (VisualTreatment.cpp:417-423): ENISVisualLook's DesaturationTarget (+0x1DC) and
            // ColourBloomIntensityTarget (+0x1E0) replace the blended values while >= 0 (Nis/NisEvents.cs)
            if (Game.NisEventRunner.VisualLookDesaturation >= 0f) Blend.Desaturation = Game.NisEventRunner.VisualLookDesaturation;
            if (Game.NisEventRunner.VisualLookColourBloom >= 0f) Blend.ColourIntensity = Game.NisEventRunner.VisualLookColourBloom;
            var tod = Sky.TimeOfDayDriver.Instance;
            _cloud = tod ? tod.Clouds.Cloud : 0f;
            _sun = tod ? tod.Tod.ShadowStrength(_cloud) : 1f;   // [0x8FAE64] (0x6C83D0); .data initial value 1
            _brightness = 1f + pulse;
            _radialBlur = blur;
            _vignette = -0.2f * _breakerBlend;
            BuildSpline();
            if (Application.isPlaying && !FrontEnd) UpdateMotionBlur();
            else _blurAlpha = 0;
        }

        float _brightness = 1, _radialBlur, _vignette;

        /// <summary>sub_6DBB20. The ease is per frame, as in the exe. The speed is Camera::VelocityKey.Position (the eye's
        /// movement over the frame); a jump over 20 m in one frame is treated as a cut [ours].</summary>
        void UpdateMotionBlur()
        {
            _blurAlpha = 0;
            bool flush = _flush;
            _flush = false;
            // 0x6DBB2A: the target is 0.2 x the radial blur only while ____icemanager_isplayingsomething [0x8F9B28] is set
            // (.data 1; ENISMotionBlur, ICEManager::SetGenericCameraToPlay, ICEMover::Update and NISActivity::Release write
            // it) and no flush is pending; the speed term below is not gated
            bool gate = Game.NisEventRunner.IceManagerIsPlayingSomething;
            float target = flush || !gate || !MotionBlurEnable ? 0f : _radialBlur * 0.2f;
            if (Mathf.Abs(_motion - target) > 1f / 255f) _motion += _motion < target ? 0.02f : -0.01f;

            var eye = transform.position;
            float dt = Time.deltaTime;
            var vel = Vector3.zero;
            if (_haveEye && !flush && dt > 1e-5f && (eye - _lastEye).sqrMagnitude < 400f) vel = (eye - _lastEye) / dt;
            _lastEye = eye;
            _haveEye = true;
            float speed = vel.magnitude;
            if (!MotionBlurEnable || speed <= 25f) return;

            float a = (speed < 125f ? (speed - 25f) / 100f : 1f) + _motion * 1.1f;
            a = Mathf.Min(a, 0.65f);
            if (a < 1f / 255f) return;
            var n = speed > 1e-5f ? transform.InverseTransformDirection(vel).normalized : Vector3.zero;
            float k = (_motion * 0.5f + 1f) * a * 0.00625f * 1.1f;
            _blurRect = new Vector4(n.x + n.z, n.y + n.z, n.x - n.z, n.y - n.z) * k;
            _blurAlpha = a;
        }

        void BuildSpline()
        {
            if (!_spline) return;
            var px = new Color32[256];
            for (int i = 0; i < 256; i++)
            {
                float u = i / 255f;
                px[i] = new Color(Mathf.Clamp01(Spline(Blend.BlackCurve, u)), Mathf.Clamp01(Spline(Blend.ColourCurve, u)), 0, 1);
            }
            _spline.SetPixels32(px);
            _spline.Apply(false);
        }

        /// <summary>
        /// sub_6D4EF0 (technique 0, shader annotation 3; byte_8F9B5D = front end). In game the look's values are rescaled
        /// by the sun strength s = [0x8FAE64]:
        ///   BlackBloomIntensity = (x, 1 - x), x = (0.6 - 0.05 s) x look  ([0x90191C] 0.6, [0x901918] 0.55)
        ///   ColourBloomIntensity = [0x901914] (1) x look
        ///   ColourBloomTint = (1 - k) tint + k lum(tint), k = 0.3 + 0.3 s ([0x901908] 0.3, [0x90190C] 0.6),
        ///                     lum = 0.2125 r + 0.7154 g + 0.0721 b ([0x8AFB18..20])
        ///   Desaturation = (d, 1 - d), d = 0.9 - 0.4 s replaces the look's ([0x9018F8] 0.9, [0x9018F4] 0.5)
        /// Front end: the look's own values; the tint's k comes from [0x982C78] x 0.001 [?: kept at 0].
        /// </summary>
        void SetTreatmentConstants()
        {
            float s = _sun;
            float bb = FrontEnd ? Blend.BlackIntensity : (0.6f - 0.05f * s) * Blend.BlackIntensity;
            float d = FrontEnd ? Blend.Desaturation : 0.9f - 0.4f * s;
            float k = FrontEnd ? 0f : 0.3f + 0.3f * s;
            var t = Blend.Tint;
            float lum = 0.2125f * t.x + 0.7154f * t.y + 0.0721f * t.z;
            _mat.SetTexture("_SplineTex", _spline);
            _mat.SetVector("_BlackBloomIntensity", new Vector4(bb, 1f - bb, 0, 0));
            _mat.SetVector("_Desaturation", new Vector4(d, 1f - d, 0, 0));
            _mat.SetVector("_ColourBloomTint", (1f - k) * t + k * new Vector4(lum, lum, lum, lum));
            _mat.SetFloat("_ColourBloomIntensity", 1f * Blend.ColourIntensity);
            _mat.SetFloat("_Brightness", _brightness);
            _mat.SetFloat("_Vignette", _vignette);
            if (!_gain && !_gainTried && GameData.Textures != null)
            {
                _gainTried = true;
                var g = GameData.Textures.Get(GainMapHash);
                if (g) { g.wrapMode = TextureWrapMode.Clamp; _gain = g; }
            }
            _mat.SetFloat("_HasGain", _gain ? 1f : 0f);
            if (_gain) _mat.SetTexture("_GainTex", _gain);
        }

        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (!Application.isPlaying) Update();   // editor previews render without a player loop
            if (!_mat || !_loaded) { Graphics.Blit(src, dst); return; }
            RenderTexture blurred = null;
            if (_blurAlpha > 0)
            {
                // two filter passes (the second with the stretch x4), then the copy blended over the frame
                var t1 = RenderTexture.GetTemporary(src.width, src.height, 0, src.format);
                var t2 = RenderTexture.GetTemporary(src.width, src.height, 0, src.format);
                _mat.SetVector("_BlurRect", _blurRect);
                Graphics.Blit(src, t1, _mat, 4);
                _mat.SetVector("_BlurRect", _blurRect * 4f);
                Graphics.Blit(t1, t2, _mat, 4);
                blurred = RenderTexture.GetTemporary(src.width, src.height, 0, src.format);
                _mat.SetTexture("_BlurTex", t2);
                _mat.SetFloat("_MotionAlpha", _blurAlpha);
                Graphics.Blit(src, blurred, _mat, 5);
                RenderTexture.ReleaseTemporary(t1);
                RenderTexture.ReleaseTemporary(t2);
                src = blurred;
            }
            // sub_6DBEC0 / sub_6DBFE0: quarter-size A8R8G8B8 targets (0x6C2E60: W/4 x H/4)
            int w = Mathf.Max(1, src.width / 4), h = Mathf.Max(1, src.height / 4);
            var a = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var b = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture c = null;
            bool vt = VisualTreatmentEnable, ob = OverbrightEnable && !FrontEnd && _mask != null;   // [ours] no mask outside play mode or in the front end
            if (ob)
            {
                // [ours] without the treatment the exe point-samples one tap (StretchRect + filter pass 5, 1 tap);
                // the 4x4 box is used in both cases
                c = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, c, _mat, 6);
            }
            if (vt)
            {
                Graphics.Blit(src, a, _mat, 0);
                for (int i = 0; i < BlurIterations; i++)
                {
                    Graphics.Blit(a, b, _mat, 1);
                    Graphics.Blit(b, a, _mat, 2);
                }
                SetTreatmentConstants();
                _mat.SetTexture("_BloomTex", a);
                Graphics.Blit(src, dst, _mat, 3);
            }
            else Graphics.Blit(src, dst);
            if (ob)
            {
                // sub_6DC040 + sub_6D2CD0 (effect 12): OverBrightOffset / OverBrightGreyScale (0.5), 2 x blur, the add
                var glare = World.ScreenEffects.Glare;
                float off = glare.Active ? Mathf.Max((1f - glare.Intensity) * (0.3f + 0.2f * _cloud), 0f) : 0.35f + 0.5f * _cloud;
                _mat.SetFloat("_OverBrightOffset", Mathf.Min(off, 1f));
                Graphics.Blit(c, a, _mat, 7);
                for (int i = 0; i < BlurIterations; i++)
                {
                    Graphics.Blit(a, b, _mat, 1);
                    Graphics.Blit(b, a, _mat, 2);
                }
                // D3DRS_BLENDFACTOR: ftol(0.8 or 0.6 (visual treatment on) x 255), or ftol(glare x [0x8FAE64] x 200)
                float factor = glare.Active ? Mathf.Floor(glare.Intensity * _sun * 200f) : Mathf.Min(Mathf.Floor((vt ? 0.6f : 0.8f) * 255f), 255f);
                _mat.SetFloat("_OverBrightBlend", Mathf.Clamp(factor, 0f, 255f) / 255f);
                Graphics.Blit(a, dst, _mat, 8);
                RenderTexture.ReleaseTemporary(c);
            }
            RenderTexture.ReleaseTemporary(a);
            RenderTexture.ReleaseTemporary(b);
            if (blurred) RenderTexture.ReleaseTemporary(blurred);
        }
    }
}
