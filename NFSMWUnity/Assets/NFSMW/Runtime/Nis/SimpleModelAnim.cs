using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// World/SimpleModelAnim.cpp (exe 0x73D2B0-0x73D50C): two scenery solids spin in place.
    /// <list type="bullet">
    /// <item>Table gSimpleSolidHashList (0x904B44, 2 × 0x14: hash, type, speed, angle, last time) = {0, ROTATEY 2, 50, 0, 0},
    /// {0, ROTATEZ 4, 50, 0, 0}. Init (0x73D2C0, from eInitEngine 0x500A72) fills the hashes with bStringHash
    /// "XO_WINDMILL_BLADE_1B_JG_00" (0x8B4AC0) and "XB_DONUTSIGNB_1B_BK_00" (0x8B4AA8); both solids are in STREAML2RA.BUN.</item>
    /// <item>Reset (0x73D300, from BeginWorldLoad 0x662AD7): last time = WorldTimer seconds (ticks [0x925AE8] × 0.00025).</item>
    /// <item>Update (0x73D320, from ePreDisplay 0x504C5B, once per drawn frame): angle += (now − last) × speed (50 °/s), then
    /// one "if ≥ 360 subtract 360".</item>
    /// <item>Animate (0x73D3C0, from eViewPlatInterface::Render 0x6DAA2B for every model drawn in every view): when the solid's
    /// NameHash (+0x10) is one of the two, pivot = local_world · local_world⁻¹ (= identity), eRotateX/Y/Z(pivot,
    /// bDegToAng(angle)) per type bit (D3DXMatrixMultiply(pivot, pivot, R)), local_world = pivot · local_world: the solid turns
    /// about its own axis through its origin, in the solid's space before the instance matrix. bDegToAng truncates:
    /// (int)(angle·65536) / 360 (0xB60B60B7 division).</item>
    /// <item>IsSimpleAnim (0x73D4E0) is only read by sub_6D7C40 (the section's static-geometry batch builder, from 0x6DC3FB):
    /// the two solids are kept out of the batch so they can move. No batch in the port: nothing to do.</item>
    /// </list>
    /// Port: the instance GameObjects WorldStreamer builds carry the solid name as their mesh name (MeshFactory), so the
    /// component finds them per loaded section and turns them about their local axis. Axes: game local Y = Unity local Z,
    /// game Z = Unity Y; D3DX RotationY (x' = x cos + z sin) / RotationZ (x' = x cos − y sin) become Unity
    /// AngleAxis(−a, forward) / AngleAxis(−a, up) (the Y/Z swap flips the handedness). A LOD child with another solid
    /// is counter-rotated (the exe rotates only the matching model). [ours] Self-starting like WorldAnimations: no hook.
    /// The road-reflection copies (World/RoadReflection, fixed matrices) do not spin [?: the exe's Animate also runs in the
    /// reflection view].
    /// </summary>
    public sealed class SimpleModelAnim : MonoBehaviour
    {
        public static bool Enabled = true;
        public static SimpleModelAnim Instance { get; private set; }

        const int RotateX = 1, RotateY = 2, RotateZ = 4;

        sealed class Info
        {
            public string Name;
            public int Type;
            public float Speed, Angle;
        }

        // gSimpleSolidHashList (0x904B44)
        static readonly Info[] List =
        {
            new Info { Name = "XO_WINDMILL_BLADE_1B_JG_00", Type = RotateY, Speed = 50f },
            new Info { Name = "XB_DONUTSIGNB_1B_BK_00", Type = RotateZ, Speed = 50f },
        };

        struct Animated
        {
            public Transform T;
            public Quaternion Base;
            public Info Info;
            public bool Counter;   // a LOD child under an animated parent whose own solid does not match
        }

        readonly List<Animated> _animated = new List<Animated>();
        readonly HashSet<GameObject> _scanned = new HashSet<GameObject>();
        readonly List<GameObject> _roots = new List<GameObject>();
        WorldStreamer _streamer;
        float _poll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance || !Application.isPlaying) return;
            var go = new GameObject("Simple Model Anim") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<SimpleModelAnim>();
        }

        /// <summary>SimpleModelAnim::Update: the WorldTimer step (Time.deltaTime: stands while the game is paused).</summary>
        public static void Step(float elapsed)
        {
            foreach (var m in List)
            {
                m.Angle += elapsed * m.Speed;
                if (m.Angle >= 360f) m.Angle -= 360f;
            }
        }

        /// <summary>bDegToAng(angle) back in degrees: (int)(angle · 65536) / 360 binary angle units of 360/65536°.</summary>
        static float Quantized(float angle) => (ushort)((int)(angle * 65536f) / 360) * (360f / 65536f);

        static Quaternion Rotation(Info m)
        {
            float a = Quantized(m.Angle);
            var q = Quaternion.identity;
            if ((m.Type & RotateX) != 0) q = Quaternion.AngleAxis(-a, Vector3.right) * q;   // unused by the table
            if ((m.Type & RotateY) != 0) q = Quaternion.AngleAxis(-a, Vector3.forward) * q;
            if ((m.Type & RotateZ) != 0) q = Quaternion.AngleAxis(-a, Vector3.up) * q;   // X, then Y, then Z as the row-vector products
            return q;
        }

        void Update()
        {
            if (!Enabled) return;
            if (!_streamer)
            {
                _animated.Clear();
                _scanned.Clear();
                _poll -= Time.unscaledDeltaTime;
                if (_poll > 0) return;
                _poll = 1f;
                _streamer = WorldStreamer.Current ? WorldStreamer.Current : FindFirstObjectByType<WorldStreamer>();
                if (!_streamer) return;
            }
            Step(Time.deltaTime);
            Scan();
            for (int i = _animated.Count - 1; i >= 0; i--)
            {
                var a = _animated[i];
                if (!a.T) { _animated.RemoveAt(i); continue; }
                var r = Rotation(a.Info);
                a.T.localRotation = a.Counter ? Quaternion.Inverse(r) * a.Base : a.Base * r;
            }
        }

        /// <summary>Each section once, after WorldStreamer has built it (Root set, not building).</summary>
        void Scan()
        {
            _roots.Clear();
            lock (_streamer.Sections)
                foreach (var s in _streamer.Sections)
                    if (s.Root && !s.Building && !_scanned.Contains(s.Root)) _roots.Add(s.Root);
            _scanned.RemoveWhere(g => !g);
            foreach (var root in _roots)
            {
                _scanned.Add(root);
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    var m = Match(mf);
                    if (m == null) continue;
                    var t = mf.transform;
                    if (t.parent && Match(t.parent.GetComponent<MeshFilter>()) != null) continue;   // a matching LOD child turns with its parent
                    _animated.Add(new Animated { T = t, Base = t.localRotation, Info = m });
                    foreach (Transform c in t)
                    {
                        var cmf = c.GetComponent<MeshFilter>();
                        if (cmf && Match(cmf) == null) _animated.Add(new Animated { T = c, Base = c.localRotation, Info = m, Counter = true });
                    }
                }
            }
        }

        static Info Match(MeshFilter mf)
        {
            if (!mf || !mf.sharedMesh) return null;
            string n = mf.sharedMesh.name;
            foreach (var m in List) if (n == m.Name) return m;
            return null;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
