using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// The blob under every car (CarRenderInfo::DrawAmbientShadow, PC 0x743EA0, World/CarRender.cpp:4264): the CARSHADOW
    /// texture on the car's AABB x1.05 ([0x8B2EA0]; x1.05 x heliScale 0.43 [0x903660] for CARTYPE_COPHELI), laid on the
    /// ground, vertex colour 0x808080. In the front end it lies flat on the floor; in game each corner follows the road
    /// (a corner off the car's face takes the car's ground height, or z = -100 on the helicopter).
    /// Alpha (PC 0x74443C): 2 x (min + (110 - min) x |up.z| x (1 - clamp(elevation / 8, 0, 1))), min = 110 in the front
    /// end ([0x925E90] == 3) and 50 in game (the GC decomp has 0..25), clamped to 254. [?] The x2 is taken as the PC
    /// ePoly 0x80 = 1.0 colour convention, so the port uses min + (110 - min) x ... over 255 (front end 110, in game 50..110):
    /// a car or the helicopter keeps a 50 shadow however high it is. The shadow_scale argument (HeliRenderConn's
    /// mShadowScale, AIVehicleHelicopter::GetShadowScale 0x4325E0) is never read by the PC function ([ebp+0x10] unused),
    /// so the helicopter's height only acts through the elevation term. The projected hull (DrawKeithProjShadow) is not
    /// ported [deviation].
    /// </summary>
    public sealed class CarShadow : MonoBehaviour
    {
        const uint CarShadowHash = 0xE43D65FB;   // bStringHash("CARSHADOW"), GLOBALB
        const float FrontEndAlpha = 110f / 255f, InGameMin = 50f, AlphaMax = 110f, Elevation = 0.01f;
        const float HeliScale = 0.43f;   // heliScale [0x903660]

        /// <summary>CARTYPE_COPHELI: the smaller blob, corners off the ground face dropped to z = -100.</summary>
        bool _heli;

        static Material _mat;
        Mesh _mesh;
        Vector3 _min, _max;
        readonly Vector3[] _v = new Vector3[4];
        readonly Color[] _c = new Color[4];
        PlayerCar _car;
        Transform _root;

        public static void Attach(GameObject root)
        {
            if (!Material()) return;
            var go = new GameObject("Shadow");
            go.transform.SetParent(root.transform, false);
            go.layer = root.layer;
            var s = go.AddComponent<CarShadow>();
            s._root = root.transform;
            // the car's AABB in its own space (game X/Y are Unity Z/X here), scaled by 1.05 across the ground plane
            var b = new Bounds();
            bool any = false;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                var mb = mf.sharedMesh.bounds;
                var m = root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, ((i & 4) >> 1) - 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            if (!any) { Destroy(go); return; }
            s._min = new Vector3(b.min.x * 1.05f, 0, b.min.z * 1.05f);
            s._max = new Vector3(b.max.x * 1.05f, 0, b.max.z * 1.05f);
            s._mesh = new Mesh { name = "CarShadow" };
            s._mesh.vertices = new Vector3[4];
            // uv: s runs along the car's length, t across it (the texture is 128x64)
            s._mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            s._mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            go.AddComponent<MeshFilter>().sharedMesh = s._mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            s.LateUpdate();
        }

        static Material Material()
        {
            if (_mat) return _mat;
            var tex = GameData.Textures?.Get(CarShadowHash);
            var sh = Shader.Find("NFSMW/WorldDecal");
            if (!tex || !sh) return null;
            _mat = new Material(sh) { name = "CARSHADOW", mainTexture = tex, renderQueue = 2480 };   // after the blended layers (WorldDecal 2470: the garage's wet floor, road decals)
            _mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            return _mat;
        }

        void LateUpdate()
        {
            if (!_mesh || !_root) return;
            if (!_car) _car = _root.GetComponent<PlayerCar>();
            // the quad lives in world space under the car's yaw; undo the parent's transform each frame
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            var world = transform.worldToLocalMatrix;
            float alpha;
            var up = _root.up;
            var fwd = Vector3.ProjectOnPlane(_root.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            var right = Vector3.Cross(Vector3.up, fwd);
            var centre = _root.position;
            if (_car == null && !_heli && !InGame())
            {
                // front end: every point at the floor (the car's root sits on it)
                for (int i = 0; i < 4; i++)
                {
                    var l = Corner(i);
                    _v[i] = world.MultiplyPoint3x4(centre + right * l.x + fwd * l.z + Vector3.up * Elevation);
                }
                alpha = FrontEndAlpha;
            }
            else
            {
                float ground = centre.y;
                if (Ground(centre + Vector3.up, out float gy)) ground = gy;
                float elev = Mathf.Max(0, centre.y - ground);
                float elevScale = Mathf.Clamp01(elev * 0.125f);
                for (int i = 0; i < 4; i++)
                {
                    var l = Corner(i);
                    var p = centre + right * l.x + fwd * l.z;
                    float y = ground;
                    if (Ground(new Vector3(p.x, centre.y + 1.5f, p.z), out float hy) && Mathf.Abs(hy - ground) < 1.25f)   // TweakCarShadowTopologyTolerance
                        y = hy;
                    else if (_heli) y = -100f;
                    _v[i] = world.MultiplyPoint3x4(new Vector3(p.x, y + Elevation, p.z));
                }
                alpha = Mathf.Min(254f, InGameMin + (AlphaMax - InGameMin) * Mathf.Abs(up.y) * (1f - elevScale)) / 255f;
            }
            var col = new Color(0.5f, 0.5f, 0.5f, alpha);
            for (int i = 0; i < 4; i++) _c[i] = col;
            _mesh.vertices = _v;
            _mesh.colors = _c;
            _mesh.RecalculateBounds();
        }

        /// <summary>Helicopter hook (Runtime/Game/Police/Helicopter.cs, one line after CarBuilder.Build("COPHELI")): the
        /// CARTYPE_COPHELI rules of DrawAmbientShadow, drawn with the in-game rules although it has no PlayerCar.</summary>
        public static void SetHelicopter(GameObject root)
        {
            var s = root ? root.GetComponentInChildren<CarShadow>(true) : null;
            if (!s || s._heli) return;
            s._heli = true;
            s._min *= HeliScale;
            s._max *= HeliScale;
        }

        /// <summary>IsGameFlowInGame: a world is loaded (traffic and the helicopter have no PlayerCar).</summary>
        static bool InGame()
        {
            var boot = GameBootstrap.Instance;
            return boot != null && boot.World != null;
        }

        Vector3 Corner(int i) => new Vector3((i & 2) == 0 ? _min.x : _max.x, 0, (i & 1) == 0 ? _min.z : _max.z);

        static readonly RaycastHit[] Hits = new RaycastHit[8];

        /// <summary>The highest surface below <paramref name="from"/> that is not this car (other cars count, as in the game).</summary>
        bool Ground(Vector3 from, out float y)
        {
            y = 0;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, Hits, 12f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (Hits[i].distance < best && !Hits[i].collider.transform.IsChildOf(_root)) { best = Hits[i].distance; y = Hits[i].point.y; }
            return best < float.MaxValue;
        }
    }
}
