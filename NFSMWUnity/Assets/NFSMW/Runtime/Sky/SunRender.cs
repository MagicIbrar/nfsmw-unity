using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.Sky
{
    /// <summary>
    /// The sun sprite, sub_6D04E0 (called from ___eDisplayFrame 0x6DF585 with ecx = the sun state 0x93E018, view 1), in
    /// game flow state 6 only (NFSMW_RE/specs/world_render.md §5):
    ///   * SetCurrentSunInfo("E3DEMO") every frame: GLOBALB chunk 0x34202 entry E3DEMO (4 used layers: SUNCENTER 60 px
    ///     at intensity 1, SUNCENTER 320 px at 0.5, SUNMAJORRAYS 500 px at 0.7 and 0.32 rotated 90 degrees).
    ///   * screen position = GetScreenPosition(TOD +0x170, the sun x 100000) scaled to 640 x 480 (0x6BE940: no w test).
    ///   * visibility (sub_6D0350): a ring of 4 D3D occlusion queries over a 32 x 32 (640x480 units) WHITE16X16_NOALPHA
    ///     quad at the sun with depth test on and colour writes off; vis = 0.5 vis + 0.5 min(pixels / 1024, 1) per frame
    ///     ([+0x08] = 0.5, ctor 0x6C00D0), 0 until the ring has filled once.
    ///   * maxsize = the largest Size of the centre-texture layers with IntensityScale > 0 (320 for E3DEMO).
    ///   * per layer (sub_6C0160): intensity = (1 - cloud) x IntensityScale; turn = (u16)(Angle - ftol((maxsize + x) /
    ///     (2 maxsize + 640) x SweepAngleAmount x -65536)); a square of side Size rotated by turn at (x + OffsetX,
    ///     y + OffsetY), z 0.999; vertex colour (Colour rgb, alpha byte = (byte)ftol(intensity x vis x 256)) - the byte
    ///     store wraps, so a layer at intensity 1 and full visibility gets alpha 0 (confirmed 2026-10-24: 0x6C0258 ftol,
    ///     0x6C029B stores al with no clamp; vis reaches exactly 1.0f after ~25 fully visible frames, then the 60 px
    ///     core disappears until something covers part of the sun); drawn with fe.efx (Sun.shader), Z off, RGBA writes,
    ///     the texture's blend (SUN* are additive).
    /// Visibility now follows the exe: the query quad is drawn against the frame's depth (Shaders/SunQuery.shader, from
    /// VisualTreatment's AfterForwardAlpha buffer) and its lit pixels are counted on the GPU and read back
    /// asynchronously, so every rendered occluder (trees, buildings, cars) hides the sun. [ours] The result arrives
    /// 1-3 frames later (the exe reads the query issued 4 frames earlier); without async readback support the old 16
    /// physics rays are used. GetScreenPosition has no w test: a sun behind the camera is projected mirrored, as in the
    /// exe (<see cref="MirrorBehindCamera"/> = false restores the port's earlier "hidden" choice).
    /// </summary>
    public static class SunRender
    {
        public static bool Enabled = true;
        /// <summary>true: as GetScreenPosition 0x6BE940 (x/w, y/w with no w test); false: [ours] behind = hidden.</summary>
        public static bool MirrorBehindCamera = true;
        const float QuerySize = 32f;          // +0x40 of the query layer (0x6D039F)
        const float Ease = 0.5f;              // [0x93E020] (0x6C00DE)

        static SunInfo _info;
        static bool _loaded;
        static float _vis;                    // [0x93E030] (+0x18); ctor value 1, cleared until the query ring is primed
        static int _primed;                   // +0x14 / +0x10: the ring fills after 4 frames
        static Mesh _mesh;
        static Material _mat;
        static MaterialPropertyBlock _props;
        static readonly Texture2D[] Tex = new Texture2D[5];
        static readonly List<Vector3> Verts = new List<Vector3>();
        static readonly List<Vector2> Uvs = new List<Vector2>();
        static readonly List<Color32> Cols = new List<Color32>();
        static readonly int[] Quad = { 0, 1, 2, 0, 2, 3 };

        static void Load()
        {
            _loaded = true;
            if (string.IsNullOrEmpty(GameData.Root)) return;
            foreach (var f in new[] { "GLOBAL/GLOBALB.lzc", "GLOBAL/GLOBALB.BUN" })
            {
                var path = Path.Combine(GameData.Root, f);
                if (!File.Exists(path)) continue;
                try
                {
                    uint want = EaHash.Hash("E3DEMO");
                    foreach (var s in SunInfo.ReadAll(GameFiles.ReadBundle(path)))
                        if (s.NameHash == want && s.Version == 2) _info = s;   // SetCurrentSunInfo 0x4FD370: Version must be 2
                }
                catch (IOException e) { Debug.LogWarning("SunRender: " + e.Message); }
                if (_info != null) break;
            }
            for (int i = 0; i < Tex.Length; i++) Tex[i] = GameData.Textures?.Get(EaHash.Hash(SunInfo.TextureNames[i]));
            var sh = Shader.Find("NFSMW/Sun");
            if (sh) _mat = new Material(sh) { hideFlags = HideFlags.DontSave };
            _props = new MaterialPropertyBlock();
        }

        /// <summary>VisualTreatment hook: queues the sun into the camera's BeforeImageEffects buffer (after the alpha mask).</summary>
        public static void Draw(CommandBuffer cmd, Camera cam)
        {
            if (!Enabled || cmd == null || cam == null || cam != Camera.main || !Application.isPlaying) return;
            if (!_loaded) Load();
            var tod = TimeOfDayDriver.Instance;
            if (_info == null || _mat == null || tod == null) return;
            if (!ScreenPosition(cam, tod, out float x, out float y, out bool front)) return;
            UpdateVisibility(cam, x, y, front);
            float cloud = tod.Clouds.Cloud;                          // view +0x68 -> Rain +0x290

            var L = _info.Layers;
            float maxSize = 0f;
            for (int i = 0; i < 4; i++)                              // 0x6D06C0..0x6D0793
                if (L[i].IntensityScale > 0f && L[i].Texture == 0 && (i == 0 ? L[i].Size > 0f : L[i].Size > maxSize)) maxSize = L[i].Size;

            Verts.Clear(); Uvs.Clear(); Cols.Clear();
            var layers = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                var l = L[i];
                if (l.Texture < 0 || l.Texture >= Tex.Length || Tex[l.Texture] == null) continue;   // SunTextures[] null: skipped
                float intensity = (1f - cloud) * l.IntensityScale;
                float ms = maxSize;
                if (l.Texture == 0 && l.Size > ms) ms = l.Size;
                int sweep = (int)((ms + x) / (2f * ms + 640f) * l.SweepAngleAmount * -65536f);
                ushort turn = (ushort)(l.Angle - sweep);
                float rad = turn * (Mathf.PI * 2f / 65536f);
                float half = l.Size * 0.5f, ch = Mathf.Cos(rad) * half, sh = Mathf.Sin(rad) * half;
                float a = ch + sh, b = ch - sh;
                float X = x + l.OffsetX, Y = y + l.OffsetY;
                int alpha = intensity < 0f ? (int)(intensity * -256f) : (int)(intensity * _vis * 256f);
                var col = new Color32(l.Colour[0], l.Colour[1], l.Colour[2], (byte)alpha);   // mov byte ptr [esi+0x83], al
                int b0 = Verts.Count;
                AddVert(X - a, Y - b, 0, 0, col);
                AddVert(X + b, Y - a, 1, 0, col);
                AddVert(X + a, Y + b, 1, 1, col);
                AddVert(X - b, Y + a, 0, 1, col);
                layers.Add(i);
            }
            if (layers.Count == 0) return;
            if (!_mesh) _mesh = new Mesh { name = "sun sprite", hideFlags = HideFlags.DontSave };
            _mesh.Clear();
            _mesh.SetVertices(Verts);
            _mesh.SetUVs(0, Uvs);
            _mesh.SetColors(Cols);
            _mesh.subMeshCount = layers.Count;
            for (int s = 0; s < layers.Count; s++)
            {
                var idx = new int[6];
                for (int k = 0; k < 6; k++) idx[k] = s * 4 + Quad[k];
                _mesh.SetTriangles(idx, s, false);
            }
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            for (int s = 0; s < layers.Count; s++)
            {
                // all five SUN* textures are AlphaBlendType 2 (SRCALPHA / ONE): Sun.shader's default blend
                _props.Clear();
                _props.SetTexture("_MainTex", Tex[L[layers[s]].Texture]);
                cmd.DrawMesh(_mesh, Matrix4x4.identity, _mat, s, 0, _props);
            }
        }

        /// <summary>GetScreenPosition 0x6BE940 of TOD +0x170 (the sun x 100000), in 640 x 480 units.</summary>
        static bool ScreenPosition(Camera cam, TimeOfDayDriver tod, out float x, out float y, out bool front)
        {
            var sp = tod.Tod.SunPosition;
            var clip = cam.projectionMatrix * cam.worldToCameraMatrix * new Vector4(sp.X, sp.Z, sp.Y, 1f);
            front = clip.w > 1e-6f || (MirrorBehindCamera && clip.w < -1e-6f);
            float w = front ? clip.w : 1f;
            x = (clip.x / w + 1f) * 320f;                      // (x/w + 1) W/2, x 640/W
            y = (1f - clip.y / w) * 240f;                      // (y/w - 1) H (-1/2), x 480/H
            return true;
        }

        // ---- the occlusion query (sub_6D0350) -------------------------------------------------------------------------

        static Material _queryMat;
        static Mesh _queryMesh, _fullMesh;
        static RenderTexture _countRT;
        static float _ratio;
        static bool _queryResult;
        static int _queryFrame = -1;
        static readonly System.Action<UnityEngine.Rendering.AsyncGPUReadbackRequest> OnReadback = r =>
        {
            if (r.hasError || r.layerCount == 0) return;
            var data = r.GetData<float>();
            if (data.Length > 0) { _ratio = Mathf.Min(data[0], 1f); _queryResult = true; }
        };

        /// <summary>True when the GPU query runs (async readback available).</summary>
        static bool GpuQuery => SystemInfo.supportsAsyncGPUReadback && _queryMat != null;

        /// <summary>
        /// VisualTreatment hook (its AfterForwardAlpha buffer): clear the frame's alpha, draw the 32 x 32 query quad at
        /// z 0.999 with the depth test into alpha, count its lit pixels into a 1x1 target and read it back.
        /// </summary>
        public static void Query(CommandBuffer cmd, Camera cam)
        {
            if (!Enabled || cmd == null || cam == null || cam != Camera.main || !Application.isPlaying) return;
            if (!_loaded) Load();
            var tod = TimeOfDayDriver.Instance;
            if (_info == null || tod == null) return;
            if (_queryMat == null)
            {
                var sh = Shader.Find("NFSMW/SunQuery");
                if (sh) _queryMat = new Material(sh) { hideFlags = HideFlags.DontSave };
            }
            if (!GpuQuery) return;
            if (!ScreenPosition(cam, tod, out float x, out float y, out bool front) || !front) return;
            if (!_countRT)
            {
                _countRT = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat) { name = "sun query count", hideFlags = HideFlags.DontSave };
                _countRT.Create();
            }
            if (!_fullMesh)
            {
                _fullMesh = new Mesh { name = "clip quad", hideFlags = HideFlags.DontSave };
                _fullMesh.SetVertices(new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0) });
                _fullMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                _fullMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            }
            if (!_queryMesh) { _queryMesh = new Mesh { name = "sun query quad", hideFlags = HideFlags.DontSave }; _queryMesh.MarkDynamic(); }
            // sub_6C0160(quad, x, y, 32, ...): an axis-aligned square of side QuerySize around the sun
            float h = QuerySize * 0.5f;
            float x0 = (x - h) / 320f - 1f, x1 = (x + h) / 320f - 1f;          // clip x
            float y0 = 1f - (y + h) / 240f, y1 = 1f - (y - h) / 240f;          // clip y (up)
            _queryMesh.Clear();
            _queryMesh.SetVertices(new[] { new Vector3(x0, y0, 0), new Vector3(x0, y1, 0), new Vector3(x1, y1, 0), new Vector3(x1, y0, 0) });
            _queryMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _queryMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            int W = cam.pixelWidth, H = cam.pixelHeight;
            // pixel centres inside the square (top-left fill convention), clipped to the screen
            int px0 = Mathf.Max(0, Mathf.CeilToInt((x0 * 0.5f + 0.5f) * W - 0.5f));
            int px1 = Mathf.Min(W - 1, Mathf.CeilToInt((x1 * 0.5f + 0.5f) * W - 0.5f) - 1);
            int py0 = Mathf.Max(0, Mathf.CeilToInt((y0 * 0.5f + 0.5f) * H - 0.5f));
            int py1 = Mathf.Min(H - 1, Mathf.CeilToInt((y1 * 0.5f + 0.5f) * H - 0.5f) - 1);
            cmd.DrawMesh(_fullMesh, Matrix4x4.identity, _queryMat, 0, 0);
            if (px1 < px0 || py1 < py0)
            {
                // off screen: no pixel passes (the D3D query returns 0)
                cmd.SetRenderTarget(_countRT);
                cmd.ClearRenderTarget(false, true, Color.clear);
            }
            else
            {
                cmd.DrawMesh(_queryMesh, Matrix4x4.identity, _queryMat, 0, 1);
                cmd.SetGlobalVector("_MW_SunQueryRect", new Vector4(px0, py0, px1, py1));
                cmd.SetGlobalVector("_MW_SunQuerySize", new Vector4(W, H, 1f / W, 1f / H));
                cmd.Blit(BuiltinRenderTextureType.CurrentActive, _countRT, _queryMat, 2);
            }
            cmd.RequestAsyncReadback(_countRT, OnReadback);
            cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _queryFrame = Time.frameCount;
        }

        static void AddVert(float x, float y, float u, float v, Color32 c)
        {
            Verts.Add(new Vector3(x / 320f - 1f, 1f - y / 240f, 0f));   // 640x480 screen units -> clip space
            Uvs.Add(new Vector2(u, v));                                   // raw D3D UVs, as every port mesh (rows uploaded top first)
            Cols.Add(c);
        }

        /// <summary>sub_6D0350: eased visibility of the 32 x 32 sun square (vis = 0.5 vis + 0.5 min(pixels / 1024, 1)).</summary>
        static void UpdateVisibility(Camera cam, float x, float y, bool front)
        {
            if (_primed < 4) { _primed++; _vis = 0f; return; }   // byte +0x10 is set after the 4th query is issued
            if (GpuQuery && _queryFrame >= 0 && Time.frameCount - _queryFrame <= 2)
            {
                // the GPU count (Query): a sun query that never ran this frame (behind the camera, off) counts 0
                float r = front && _queryResult ? _ratio : 0f;
                _vis = (1f - Ease) * _vis + r * Ease;
                return;
            }
            int open = 0;
            const int N = 4;
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    if (!front) continue;
                    float px = x + ((i + 0.5f) / N - 0.5f) * QuerySize, py = y + ((j + 0.5f) / N - 0.5f) * QuerySize;
                    if (px < 0 || px > 640 || py < 0 || py > 480) continue;   // clipped pixels never pass
                    var ray = cam.ViewportPointToRay(new Vector3(px / 640f, 1f - py / 480f, 0f));
                    if (!Physics.Raycast(ray, cam.farClipPlane, ~0, QueryTriggerInteraction.Ignore)) open++;
                }
            float ratio = Mathf.Min(open / (float)(N * N), 1f);
            _vis = (1f - Ease) * _vis + ratio * Ease;
        }
    }
}
