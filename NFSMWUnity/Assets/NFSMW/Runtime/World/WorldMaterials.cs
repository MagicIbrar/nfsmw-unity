using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.World
{
    /// <summary>
    /// Per-material world effects (NFSMW_RE/specs/world_render.md §2-3). Every solid material stores an effect id at +0x30;
    /// eViewPlatInterface::Render (0x6DA9B0) draws it with effect object [0x93DE78 + 4 id], technique chosen by 0x6D7500
    /// (the highest validated `shader` annotation of the no-shadow slot). On a shader-capable PC with world shadows off:
    ///   0 world.efx tech 0 (World.shader, and the blended WorldDecal / WorldTransparent with _MWLit)
    ///   1 worldreflect.efx "world_reflect_noshadow_2_0" (WorldReflect.shader): roads, pavements (9k parts)
    ///   3 worldnormalmap.efx "worldnormalmap_2_0" (WorldNormalMap.shader): walls, cliffs, barriers (8k parts)
    ///   5 GlossyWindow.efx tech 0 (GlossyWindow.shader): windows and facades (3.2k parts)
    ///   6 tree.efx tech 0 (Tree.shader): trees, bushes, tree lines (1.9k parts)
    ///  19 skybox.efx (Sky/SkyRender.cs). Other ids do not occur in L2RA scenery and stay on World.shader.
    /// Material textures (material +0x18..+0x1C, read per draw at 0x6DAC17): slot 0 diffuse, 1 normal map, 2 height map,
    /// 3 specular map, 4 opacity. The exe skips a material whose texture lookup failed, except GlossyWindow, which falls back
    /// to world.efx (0x6DAC90); here a missing normal/specular map falls back to world.efx [ours].
    /// The blend/alpha test state comes from the diffuse TextureInfo (BlendState), as MeshFactory already decides it; the
    /// effect only changes the lighting.
    /// </summary>
    public static class WorldMaterials
    {
        /// <summary>[ours] false: every world material keeps world.efx (the previous port).</summary>
        public static bool Enabled = true;

        static Shader _reflect, _normal, _glossy, _tree, _decal, _transparent, _world;
        static readonly Dictionary<(Material, int, uint, uint), Material> Cache = new Dictionary<(Material, int, uint, uint), Material>();
        static readonly uint WindowReflection = EaHash.Hash("WINDOWREFLECTION");

        static bool Init()
        {
            if (_world != null) return true;
            _world = Shader.Find("NFSMW/World");
            _decal = Shader.Find("NFSMW/WorldDecal");
            _transparent = Shader.Find("NFSMW/WorldTransparent");
            _reflect = Shader.Find("NFSMW/WorldReflect");
            _normal = Shader.Find("NFSMW/WorldNormalMap");
            _glossy = Shader.Find("NFSMW/GlossyWindow");
            _tree = Shader.Find("NFSMW/Tree");
            return _world != null;
        }

        /// <summary>
        /// WorldStreamer hook, once per freshly built world solid: adds the tangents of effect 1/3 parts to the mesh and swaps
        /// each part's world.efx material for its own effect's.
        /// </summary>
        public static void Apply(MeshFactory.Built built, Solid solid, TextureLibrary lib)
        {
            if (!Enabled || built == null || solid == null || lib == null || !Init()) return;
            AddTangents(built);
            for (int i = 0; i < built.Parts.Count && i < built.Materials.Length; i++)
                built.Materials[i] = For(built.Parts[i], solid, built.Materials[i], lib);
        }

        /// <summary>Tangents in part order (MeshFactory.Build concatenates every part's vertices). Game -> Unity swaps Y/Z,
        /// a mirror, so w flips to keep binormal = cross(N, T) * w (world_render.md §2).</summary>
        static void AddTangents(MeshFactory.Built built)
        {
            bool any = false;
            foreach (var p in built.Parts) if (p.Tangents != null) { any = true; break; }
            if (!any) return;
            var t = new List<Vector4>(built.Mesh.vertexCount);
            foreach (var p in built.Parts)
            {
                int n = p.VertexCount;
                for (int v = 0; v < n; v++)
                {
                    if (p.Tangents == null) { t.Add(new Vector4(1, 0, 0, 1)); continue; }
                    var c = Coords.ToUnity(p.Tangents[v * 4], p.Tangents[v * 4 + 1], p.Tangents[v * 4 + 2]);
                    t.Add(new Vector4(c.x, c.y, c.z, -p.Tangents[v * 4 + 3]));
                }
            }
            if (t.Count == built.Mesh.vertexCount) built.Mesh.SetTangents(t);
        }

        static uint Slot(SolidPart p, Solid s, int k)
        {
            int slot = p.TextureSlots[k];
            return slot < s.TextureHashes.Length ? s.TextureHashes[slot] : 0;
        }

        static Material For(SolidPart part, Solid solid, Material baseMat, TextureLibrary lib)
        {
            if (baseMat == null) return baseMat;
            bool blended = baseMat.shader == _decal || baseMat.shader == _transparent;
            int effect = part.EffectId;
            // effect 0 (and anything without its own port): world.efx; blended ones get its lighting too (§3)
            if (effect != 1 && effect != 3 && effect != 5 && effect != 6)
            {
                if (blended && baseMat.HasProperty("_MWLit")) baseMat.SetFloat("_MWLit", 1f);
                return baseMat;
            }
            Shader sh = effect == 1 ? _reflect : effect == 3 ? _normal : effect == 5 ? _glossy : _tree;
            if (sh == null || !sh.isSupported) return Lit(baseMat, blended);
            uint nrm = 0, spec = 0;
            Texture nrmTex = null, specTex = null;
            if (effect == 1 || effect == 3)
            {
                nrm = Slot(part, solid, 1);
                spec = Slot(part, solid, 3);
                nrmTex = lib.Get(nrm);
                specTex = lib.Get(spec);
                if (nrmTex == null || specTex == null || part.Tangents == null) return Lit(baseMat, blended);
                // worldreflect writes alpha 1: it cannot be alpha tested or blended over [?: no blended road part checked]
                if (effect == 1 && blended) return Lit(baseMat, blended);
            }
            var key = (baseMat, effect, nrm, spec);
            if (Cache.TryGetValue(key, out var m) && m) return m;
            m = new Material(sh) { name = baseMat.name + " (" + sh.name.Substring(6) + ")", enableInstancing = true };
            m.mainTexture = baseMat.mainTexture;
            m.mainTextureScale = baseMat.mainTextureScale;
            m.mainTextureOffset = baseMat.mainTextureOffset;
            if (baseMat.HasProperty("_Cull") && m.HasProperty("_Cull")) m.SetFloat("_Cull", baseMat.GetFloat("_Cull"));
            if (nrmTex) m.SetTexture("_NormalMap", nrmTex);
            if (specTex) m.SetTexture("_SpecMap", specTex);
            if (effect == 5)
            {
                var wr = lib.Get(WindowReflection);
                if (wr)
                {
                    wr.wrapMode = TextureWrapMode.Repeat;
                    m.SetTexture("_WindowReflection", wr);
                }
            }
            if (m.HasProperty("_Cutoff"))
                m.SetFloat("_Cutoff", !blended && baseMat.HasProperty("_Cutoff") ? baseMat.GetFloat("_Cutoff") : -1f);
            if (blended && m.HasProperty("_SrcBlend"))
            {
                // as WorldDecal: blended layer over the surface below, depth biased, no depth write
                m.SetFloat("_SrcBlend", baseMat.HasProperty("_SrcBlend") ? baseMat.GetFloat("_SrcBlend") : (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", baseMat.HasProperty("_DstBlend") ? baseMat.GetFloat("_DstBlend") : (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_BlendOp", baseMat.HasProperty("_BlendOp") ? baseMat.GetFloat("_BlendOp") : (float)BlendOp.Add);
                m.SetFloat("_ZWrite", 0f);
                m.SetFloat("_OffsetFactor", -1f);
                m.SetFloat("_OffsetUnits", -2f);
            }
            m.renderQueue = baseMat.renderQueue;
            uint diffuse = part.TextureHash;
            TextureScroll.Register(m, lib.Source(diffuse));
            Cache[key] = m;
            return m;
        }

        static Shader _shadowCg;
        static readonly Dictionary<Material, Material> ShadowCgCache = new Dictionary<Material, Material>();

        /// <summary>
        /// The shadow_cg.efx (effect 22) materials of a scenery instance flagged 0x100 (StuffScenery: render flag 0x20000,
        /// 0x6DABFC swaps world.efx / worldnofog for shadow_cg in every view): copies of the solid's materials on
        /// Shaders/WorldShadowCg.shader with the same texture, blend and scroll (world_render.md §11).
        /// </summary>
        public static Material[] ShadowCg(Material[] mats)
        {
            if (mats == null) return null;
            _shadowCg ??= Shader.Find("NFSMW/WorldShadowCg");
            if (_shadowCg == null || !_shadowCg.isSupported) return mats;
            var r = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) { r[i] = null; continue; }
                if (!ShadowCgCache.TryGetValue(m, out var c) || !c)
                {
                    c = new Material(_shadowCg) { name = m.name + " (shadow_cg)" };
                    c.mainTexture = m.mainTexture;
                    c.mainTextureScale = m.mainTextureScale;
                    c.mainTextureOffset = m.mainTextureOffset;
                    if (m.HasProperty("_SrcBlend")) c.SetFloat("_SrcBlend", m.GetFloat("_SrcBlend"));
                    if (m.HasProperty("_DstBlend")) c.SetFloat("_DstBlend", m.GetFloat("_DstBlend"));
                    if (m.HasProperty("_BlendOp")) c.SetFloat("_BlendOp", m.GetFloat("_BlendOp"));
                    if (m.HasProperty("_Cull")) c.SetFloat("_Cull", m.GetFloat("_Cull"));
                    c.renderQueue = m.renderQueue;
                    TextureScroll.Mirror(m, c);
                    ShadowCgCache[m] = c;
                }
                r[i] = c;
            }
            return r;
        }

        static Material Lit(Material baseMat, bool blended)
        {
            if (blended && baseMat.HasProperty("_MWLit")) baseMat.SetFloat("_MWLit", 1f);
            return baseMat;
        }
    }
}
