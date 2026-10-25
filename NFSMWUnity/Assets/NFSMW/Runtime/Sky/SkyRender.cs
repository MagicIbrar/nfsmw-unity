using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Sky
{
    /// <summary>
    /// The PC sky (World/SkyRender.cpp; PC NotifySkyLoader 0x6D1CF0, StuffSkyLayer 0x6DB310, sky pass 0x6DE210).
    /// With the shader technique of skybox.efx available ([effect 19]+0x40 set) the exe loads the SKYDOME_XENON scenery
    /// model and draws it once per player view with fx\skybox.efx (Shaders/SkyBox.shader, three passes), textures:
    ///   diffuse_sampler     the dome's own texture (SKY_NEXGEN_CLOUDS_A)
    ///   SKY_TEXTURE_MISC_1  SKY_NEXGEN_CAP_A, SKY_TEXTURE_MISC_2 SKY_NEXGEN_CLOUDS_B, SKY_TEXTURE_MISC_3 SKY_NEXGEN_CAP_B
    /// Layer 0 for the player view: identity matrix (no rotation, MainSkyScale 1 x ScaleFactor 1) translated to the
    /// camera. Only without that technique does it draw SKYDOME with skybox_cg.efx in three StuffSkyLayer passes (the
    /// legacy SkyDome in WorldStreamer.cs keeps that path as a fallback). SKY_SPECULAR feeds reflection views only.
    /// </summary>
    public sealed class SkyRender : MonoBehaviour
    {
        static readonly uint Misc1 = EaHash.Hash("SKY_NEXGEN_CAP_A");
        static readonly uint Misc2 = EaHash.Hash("SKY_NEXGEN_CLOUDS_B");
        static readonly uint Misc3 = EaHash.Hash("SKY_NEXGEN_CAP_B");
        const float DomeRadius = 9700f;          // SKYDOME_XENON spans +-9685 m

        static Shader _shader;
        static readonly List<GameObject> CurrentGenDomes = new List<GameObject>();
        public static bool XenonActive { get; private set; }

        readonly List<(Material mat, uint diffuse)> _mats = new List<(Material, uint)>();
        bool _texturesReady;

        static bool ShaderPath()
        {
            _shader ??= Shader.Find("NFSMW/SkyBox");
            return _shader && _shader.isSupported;
        }

        /// <summary>
        /// WorldStreamer hook for any SKYDOME* instance. Returns false when the caller should fall back to its legacy
        /// sky dome (current-gen model without the SkyBox shader).
        /// </summary>
        public static bool Setup(GameObject go, MeshRenderer mr, Solid solid, string name)
        {
            bool xenon = name.StartsWith("SKYDOME_XENON");
            if (!xenon)
            {
                if (!ShaderPath()) return false;
                // the current-gen dome is not drawn when skybox.efx's shader technique is in use
                CurrentGenDomes.RemoveAll(g => !g);
                CurrentGenDomes.Add(go);
                go.SetActive(false);
                return true;
            }
            if (!ShaderPath() || solid == null) { go.SetActive(false); return true; }

            var verts = new List<Vector3>();
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            var subs = new List<int[]>();
            var mats = new List<Material>();
            var comp = go.AddComponent<SkyRender>();
            foreach (var p in solid.Parts)
            {
                int b = verts.Count, n = p.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    verts.Add(Coords.ToUnity(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]));
                    uv0.Add(new Vector2(p.Uvs[i * 2], p.Uvs[i * 2 + 1]));
                    uv1.Add(p.Uvs2 != null ? new Vector2(p.Uvs2[i * 2], p.Uvs2[i * 2 + 1]) : Vector2.zero);
                }
                var idx = new int[p.Indices.Length];
                for (int i = 0; i < idx.Length; i++) idx[i] = b + p.Indices[i];
                subs.Add(idx);
                var m = new Material(_shader) { name = "skyshader " + p.MaterialName };
                mats.Add(m);
                comp._mats.Add((m, p.TextureHash));
            }
            var mesh = new Mesh { name = solid.Name + " (skybox.efx)" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) mesh.SetTriangles(subs[i], i, false);
            mesh.RecalculateBounds();
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            mr.sharedMaterials = mats.ToArray();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            XenonActive = true;
            NfsMw.World.RoadReflection.SetSkyDome(mesh, mr.sharedMaterials);   // sky layer 4 of the road reflection view
            NfsMw.CarRender.CarEnvironment.SetSkyDome(mesh, mr.sharedMaterials);   // sky layer 3 of the env-map views (0.0065)
            foreach (var g in CurrentGenDomes) if (g) g.SetActive(false);
            comp.BindTextures();
            return true;
        }

        void BindTextures()
        {
            var lib = GameData.Textures;
            if (lib == null) return;
            Texture m1 = lib.Get(Misc1), m2 = lib.Get(Misc2), m3 = lib.Get(Misc3);
            bool all = m1 && m2 && m3;
            foreach (var (mat, diffuse) in _mats)
            {
                var d = lib.Get(diffuse);
                if (!d) all = false;
                if (d) mat.SetTexture("_MainTex", d);
                if (m1) mat.SetTexture("_Misc1", m1);
                if (m2) mat.SetTexture("_Misc2", m2);
                if (m3) mat.SetTexture("_Misc3", m3);
            }
            _texturesReady = all;
        }

        void LateUpdate()
        {
            if (!_texturesReady) BindTextures();
            var cam = Camera.main;
            if (!cam) return;
            // LockSky: centred on the camera. [ours] scaled to sit inside the far plane; the shading uses directions only
            float s = cam.farClipPlane * 0.8f / DomeRadius;
            transform.localScale = Vector3.one * s;
            transform.rotation = Quaternion.identity;
            transform.position = cam.transform.position;
        }
    }
}
