using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// cFEngRender / FERenderObject (Frontend/FEngRender.cpp, FERenderObject.cpp; PC RenderImage 0x59A500, RenderMultiImage
    /// 0x59A060, RenderString 0x5A4390, FERenderObject::AddPoly 0x582660) — the parts the Unity canvas does not do by itself:
    /// the per-texture blend state of the FE ePoly path (eViewPlatInterface::Render 0x6D1260 → texture state 0x6C68B0).
    /// </summary>
    public sealed partial class FEView
    {
        static readonly Dictionary<(int, int, float), Material> BlendMaterials = new Dictionary<(int, int, float), Material>();
        static readonly Dictionary<Texture, NfsMw.Core.GameTexture> LocalSources = new Dictionary<Texture, NfsMw.Core.GameTexture>();

        /// <summary>The TPK record of a texture the FE texture library does not hand out (the minimap chops, AlphaBlend 2:
        /// additive over the map backing), for the blend state.</summary>
        public static void RegisterTextureSource(Texture t, NfsMw.Core.GameTexture src)
        {
            if (t != null && src != null) LocalSources[t] = src;
            if (LocalSources.Count > 512) { var dead = new List<Texture>(); foreach (var k in LocalSources.Keys) if (!k) dead.Add(k); foreach (var k in dead) LocalSources.Remove(k); }
        }

        /// <summary>
        /// TextureInfo AlphaBlendType (+0x56) → D3D (src, dst), in Unity BlendMode values (0x6C68B0's switch):
        /// 0 = blending off, 1 = SrcAlpha / InvSrcAlpha, 2 = SrcAlpha / One, 3 = Zero / InvSrcColor, 5 = DestAlpha / InvDestAlpha,
        /// 6 = DestAlpha / One; other values keep SrcAlpha / InvSrcAlpha.
        /// </summary>
        public static (int src, int dst) BlendFactors(int alphaBlend)
        {
            switch (alphaBlend)
            {
                case 0: return (1, 0);
                case 2: return (5, 1);
                case 3: return (0, 6);
                case 5: return (7, 8);
                case 6: return (7, 1);
                default: return (5, 10);
            }
        }

        static Material BlendMaterial(int src, int dst, float cutoff)
        {
            if (BlendMaterials.TryGetValue((src, dst, cutoff), out var m) && m) return m;
            var sh = Shader.Find("NFSMW/UIBlend");
            if (!sh) return null;
            m = new Material(sh) { name = $"FE blend {src}/{dst}" + (cutoff > 0 ? " test" : "") };
            m.SetFloat("_SrcBlend", src);
            m.SetFloat("_DstBlend", dst);
            m.SetFloat("_Cutoff", cutoff);
            BlendMaterials[(src, dst, cutoff)] = m;
            return m;
        }

        static bool IsBlendMaterial(Material m)
        {
            if (m == null) return true;
            foreach (var kv in BlendMaterials) if (kv.Value == m) return true;
            return false;
        }

        /// <summary>
        /// Sets the image's blend state from its texture (when the texture changes): the texture's AlphaBlendType and,
        /// for AlphaUsageType 1 (punch-through), the alpha test with reference 0x5F (0x6C68B0 → 0x6C67C0(1, 0x5F)) [?: the
        /// compare function is not read; clip keeps alpha ≥ ref]. Images with a material of their own (the minimap's round
        /// clip) are left alone; multi-images set the same state on their mask material.
        /// </summary>
        void ApplyBlend(Node n)
        {
            n.BlendTex = n.Image.texture;
            var src = FrontEndData.SourceOf(n.Image.texture);
            if (src == null && n.Image.texture != null) LocalSources.TryGetValue(n.Image.texture, out src);
            var (sb, db) = BlendFactors(src != null ? src.AlphaBlend : 1);
            float cut = src != null && src.AlphaUsage == 1 ? 0x5F / 255f : 0f;
            if (n.Mask)
            {
                n.Mask.SetFloat("_SrcBlend", sb);
                n.Mask.SetFloat("_DstBlend", db);
                n.Mask.SetFloat("_Cutoff", cut);
                return;
            }
            var cur = n.Image.material == n.Image.defaultMaterial ? null : n.Image.material;
            if (!IsBlendMaterial(cur)) return;
            var want = sb == 5 && db == 10 && cut == 0 ? null : BlendMaterial(sb, db, cut);
            if (cur != want) n.Image.material = want;
        }
    }
}
