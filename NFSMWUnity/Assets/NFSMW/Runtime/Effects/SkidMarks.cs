using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Effects
{
    /// <summary>
    /// Skid marks (World/Skids.cpp, PC SkidMaker::MakeSkid 0x72F120, CreateNewSkidSet 0x72F030). A skid set is up to 8
    /// segments, each a centre point and a half-width vector (stored as 1/64 m bytes in the game), with an intensity.
    /// AddSegment merges a new point into the last segment while the run is short (under 0.25 m) or straight
    /// (1 - dot under 0.002), up to 3 m; a bend over 0.2 ends the set and the maker starts a new one from the last
    /// point. Sets draw as quads between consecutive segments, 0.05 m above the road, texture SKID_ROAD, colour
    /// 0x80808080 with the intensity as alpha; small on screen they fade (RenderSkids: under 10 px per metre the alpha
    /// drops 42.7 per px, under 4 px they are skipped). 128 sets at most (512 in drift races, World.cpp:329); the
    /// least recently drawn is recycled. UVs run (0,0)-(1,1) per segment quad [?] (ePoly's defaults are not decompiled).
    /// </summary>
    [DefaultExecutionOrder(10002)]
    public sealed class SkidMarks : MonoBehaviour
    {
        public const int MaxSegments = 8;
        public static int MaxSets = 128;
        const float WayTooCurvy = 0.2f, TooCurvy = 0.002f, ExtraHeight = 0.05f;
        static readonly uint SkidTexture = EaHash.Hash("SKID_ROAD");

        public sealed class SkidSet
        {
            public readonly Vector3[] Pos = new Vector3[MaxSegments];     // game space
            public readonly Vector3[] Delta = new Vector3[MaxSegments];   // half width, game space
            public readonly byte[] Intensity = new byte[MaxSegments];
            public int Count;
            public Vector3 LastNormal;
            public float LastLength;
            public Maker Owner;
            public int LastDrawn;
            public Vector3 Min, Max;

            /// <summary>SkidSet::AddSegment (Skids.cpp:150): 1 when the set is full or bends too much (start a new one).</summary>
            public int Add(Vector3 p, Vector3 delta, float intensity)
            {
                Vector3 normal = Vector3.zero;
                float length = 0f;
                if (Count > 0)
                {
                    var f = p - Pos[Count - 1];
                    length = f.magnitude;
                    normal = length > 1e-6f ? f / length : Vector3.zero;
                }
                bool expand = false;
                if (Count > 1)
                {
                    float error = 1f - Vector3.Dot(normal, LastNormal);
                    float newLength = LastLength + length;
                    if (error > WayTooCurvy) { Finish(); return 0; }
                    if (newLength < 0.25f) expand = true;
                    if (error < TooCurvy) expand = true;
                    if (newLength > 3f) expand = false;
                }
                if (!expand && Count == MaxSegments) return 1;
                int i;
                if (expand)
                {
                    i = Count - 1;
                    LastLength += length;
                }
                else
                {
                    i = Count++;
                    if (Count > 1) LastNormal = normal;
                    LastLength = length;
                }
                Pos[i] = p;
                // SetPoints stores the half width in 1/64 m signed bytes
                Delta[i] = new Vector3(Mathf.Clamp((int)(delta.x * 64f), -128, 127), Mathf.Clamp((int)(delta.y * 64f), -128, 127),
                    Mathf.Clamp((int)(delta.z * 64f), -128, 127)) / 64f;
                Intensity[i] = (byte)Mathf.Clamp((int)(intensity * 255f), 0, 255);
                if (Count == 1 && i == 0) { Min = Max = p; }
                Min = Vector3.Min(Min, p - Vector3.one * length);
                Max = Vector3.Max(Max, p + Vector3.one * length);
                return 0;
            }

            /// <summary>SkidSet::FinishedAddingSkids: detach from the maker.</summary>
            public void Finish()
            {
                if (Owner != null) { Owner.Set = null; Owner = null; }
            }
        }

        /// <summary>SkidMaker (Skids.hpp): one per tyre.</summary>
        public sealed class Maker
        {
            internal SkidSet Set;

            /// <summary>SkidMaker::MakeSkid (Skids.cpp:285); position and half-width delta in game space.</summary>
            public void MakeSkid(Vector3 position, Vector3 delta, float intensity)
            {
                if (Set == null) { Set = Instance.NewSet(this, position, delta, intensity); return; }
                if (Set.Add(position, delta, intensity) != 0)
                {
                    var s = Set;
                    var lastPos = s.Pos[s.Count - 1];
                    var lastDelta = s.Delta[s.Count - 1];
                    float lastIntensity = s.Intensity[s.Count - 1] / 255f;
                    s.Finish();
                    var n = Instance.NewSet(this, lastPos, lastDelta, lastIntensity);
                    n.Add(position, delta, intensity);
                    Set = n;
                }
            }

            /// <summary>SkidMaker::MakeNoSkid.</summary>
            public void MakeNoSkid() => Set?.Finish();
        }

        static SkidMarks _instance;
        readonly List<SkidSet> _sets = new List<SkidSet>();
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<Color> _c = new List<Color>();
        readonly List<int> _t = new List<int>();
        readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();
        Material _mat;
        bool _matTried;
        int _frame;

        public static SkidMarks Instance
        {
            get
            {
                if (_instance) return _instance;
                var go = new GameObject("Skid Marks");
                if (Application.isPlaying) DontDestroyOnLoad(go);
                return _instance = go.AddComponent<SkidMarks>();
            }
        }

        SkidSet NewSet(Maker m, Vector3 p, Vector3 delta, float intensity)
        {
            if (_sets.Count >= MaxSets)
            {
                int oldest = 0;
                for (int i = 1; i < _sets.Count; i++) if (_sets[i].LastDrawn < _sets[oldest].LastDrawn) oldest = i;
                _sets[oldest].Finish();
                _sets.RemoveAt(oldest);
            }
            var s = new SkidSet { Owner = m, LastDrawn = _frame };
            s.Add(p, delta, intensity);
            _sets.Add(s);
            return s;
        }

        /// <summary>DeleteAllSkids (race restart).</summary>
        public static void Clear()
        {
            if (!_instance) return;
            foreach (var s in _instance._sets) s.Finish();
            _instance._sets.Clear();
        }

        void LateUpdate()
        {
            _frame++;
            if (_sets.Count == 0) return;
            if (!_mat)
            {
                if (_matTried) return;
                _matTried = true;
                var sh = Shader.Find("NFSMW/Particle");
                bool own = sh;
                if (!sh) sh = Shader.Find("NFSMW/WorldDecal");
                var tex = GameData.Textures?.Get(SkidTexture);
                if (!sh || !tex) return;
                var src = GameData.Textures.Source(SkidTexture);
                _mat = new Material(sh) { name = "Skid marks", mainTexture = tex, renderQueue = 2990 };
                _mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _mat.SetFloat("_DstBlend", (float)(src != null && src.AlphaBlend == 2 ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                _alphaScale = own ? 1f : 2f;
            }
            int count = EmitterSystem.Cameras(out var cams);
            for (int c = 0; c < count; c++)
            {
                var cam = cams[c];
                if (!cam || !cam.enabled || cam.cameraType != CameraType.Game || (cam.cullingMask & 1) == 0) continue;
                BuildFor(cam);
                if (_v.Count == 0) continue;
                int id = cam.GetInstanceID();
                if (!_meshes.TryGetValue(id, out var mesh) || !mesh)
                {
                    mesh = new Mesh { name = "Skids" };
                    mesh.MarkDynamic();
                    _meshes[id] = mesh;
                }
                mesh.Clear();
                mesh.SetVertices(_v);
                mesh.SetUVs(0, _uv);
                mesh.SetColors(_c);
                mesh.SetTriangles(_t, 0, false);
                mesh.bounds = new Bounds(cam.transform.position, Vector3.one * 2000f);
                Graphics.DrawMesh(mesh, Matrix4x4.identity, _mat, 0, cam);
            }
        }

        float _alphaScale = 1f;

        void BuildFor(Camera cam)
        {
            _v.Clear(); _uv.Clear(); _c.Clear(); _t.Clear();
            var eye = FxFrame.G(cam.transform.position);
            // eView::GetPixelSize(distance, 1 m) on the 640-wide screen
            float hfov = 2f * Mathf.Atan(Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.aspect);
            float h = 320f / Mathf.Tan(hfov * 0.5f);
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            foreach (var s in _sets)
            {
                if (s.Count < 2) continue;
                var centre = (s.Min + s.Max) * 0.5f;
                var b = new Bounds(FxFrame.U(centre), FxFrame.U(s.Max - s.Min) + Vector3.one);
                if (!GeometryUtility.TestPlanesAABB(planes, b)) continue;
                float px = h / Mathf.Max(0.01f, Vector3.Distance(centre, eye));
                if (px <= 4f) continue;
                int reduction = px > 10f ? 0 : (int)(256f - (px - 4f) * 42.666668f) & 0xFF;
                s.LastDrawn = _frame;
                for (int n = 0; n < s.Count - 1; n++)
                {
                    // GetEndPoints: left = pos + delta, right = pos - delta
                    var up = new Vector3(0, 0, ExtraHeight);
                    var l0 = s.Pos[n] + s.Delta[n] + up;
                    var r0 = s.Pos[n] - s.Delta[n] + up;
                    var l1 = s.Pos[n + 1] + s.Delta[n + 1] + up;
                    var r1 = s.Pos[n + 1] - s.Delta[n + 1] + up;
                    int a0 = Mathf.Max(0, s.Intensity[n] - reduction), a1 = Mathf.Max(0, s.Intensity[n + 1] - reduction);
                    int i0 = _v.Count;
                    _v.Add(FxFrame.U(l0)); _uv.Add(new Vector2(0, 0)); _c.Add(Col(a0));
                    _v.Add(FxFrame.U(l1)); _uv.Add(new Vector2(1, 0)); _c.Add(Col(a1));
                    _v.Add(FxFrame.U(r1)); _uv.Add(new Vector2(1, 1)); _c.Add(Col(a1));
                    _v.Add(FxFrame.U(r0)); _uv.Add(new Vector2(0, 1)); _c.Add(Col(a0));
                    _t.Add(i0); _t.Add(i0 + 1); _t.Add(i0 + 2);
                    _t.Add(i0); _t.Add(i0 + 2); _t.Add(i0 + 3);
                }
            }
        }

        Color Col(int a) => new Color(0.5f, 0.5f, 0.5f, a / 255f * _alphaScale);

        void OnDestroy()
        {
            foreach (var m in _meshes.Values) if (m) Destroy(m);
            if (_mat) Destroy(_mat);
            if (_instance == this) _instance = null;
        }
    }
}
