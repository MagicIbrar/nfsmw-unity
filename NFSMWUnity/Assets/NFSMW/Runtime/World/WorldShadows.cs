using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.World
{
    /// <summary>
    /// World shadows (NFSMW_RE/specs/world_render.md §13; shaders: Shaders/MWShadow.cginc).
    ///
    /// Which path a default PC takes: the options preset 0x6C1990 sets g_ShadowDetail [0x901830] = 1 from performance
    /// level 4 (the top preset), and sub_6C1510 sets [0x982C08] = 1 on NVIDIA when D24S8 is a depth texture format, 2 on
    /// ATI with DF16 (0 elsewhere: no shadow maps at all). sub_6D2100 then takes [0x982C3C] = the highest shadowLevel the
    /// effects support, capped at 2. So a current PC at the top preset draws, every frame (eDisplayFrame 0x6DEC4A, while
    /// [0x8FAE68] = 0.1 + 0.8 sun strength >= 0.1):
    ///   * the shadow view 0xF (sub_6E54E0 -> sub_6E44C0): a 1024 x 1024 depth map (GetShadowWidth/Height 0x6C86B0) in a
    ///     light-space perspective fitted to the player view; into it the WorldModels (0x7538D0: smackables, checkpoint),
    ///     the scenery of StuffScenery(view 0xF, 0x2000) - only instances flagged 0x40000000 (option 2: also 0x100000 /
    ///     0x200000 without 0x4000000) - and the cars ([0x903320] = 1: the camera's car and every loaded car); the world
    ///     effects draw there as worldnofog (0x6DABA2), the car as car_shadow_map;
    ///   * in the player view, world / worldreflect / worldnormalmap / GlossyWindow / car draws get shadow techniques by
    ///     distance (sub_6E0AB0: 4 taps to 100 m, 1 tap to 400 m) that scale the sun terms (MWShadow.cginc).
    /// Port: Unity's cascaded sun shadow map stands in for the exe's map [ours]: this class gives the exe's casters a
    /// material copy with the ShadowCaster pass on (every other world material has it off, so it is neither a caster nor
    /// in the camera depth pass), copies the shadow map to _MW_SunShadowMap with a light command buffer, sets the shadow
    /// distance to the exe's 400 m and publishes InvShadowStrength. Trees (tree.efx) have no shadow technique.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class WorldShadows : MonoBehaviour
    {
        /// <summary>g_ShadowDetail [0x901830] > 0 (and a depth-texture card, [0x982C08] != 0).</summary>
        public static bool Enabled = true;
        /// <summary>g_ShadowDetail: 1 = casters flagged 0x40000000, 2 = also 0x100000 / 0x200000 (sub_6E44C0 0x6E5150).</summary>
        public static int Detail = 1;
        /// <summary>[ours] the shadow map atlas size of the sun light (the exe's map is 1024 x 1024, one cascade).</summary>
        public static int Resolution = 2048;
        const float ShadowDistance = 400f;     // [0x8FAE60]: no shadow technique beyond 400 m of view depth
        const float MinStrength = 0.1f;         // [0x8FAE50]

        public static WorldShadows Instance { get; private set; }
        static readonly Dictionary<Material, Material> CasterCopies = new Dictionary<Material, Material>();
        static readonly HashSet<Material> Casters = new HashSet<Material>();
        CommandBuffer _cmd;
        Light _light;
        bool _qualitySet;

        public static WorldShadows Ensure(GameObject host)
        {
            if (Instance) return Instance;
            var s = host.GetComponent<WorldShadows>();
            return s ? s : host.AddComponent<WorldShadows>();
        }

        void Awake() => Instance = this;

        void OnEnable() => Camera.onPreCull += PreCull;

        void OnDisable()
        {
            Camera.onPreCull -= PreCull;
            Detach();
            Shader.SetGlobalFloat("_MW_WorldShadows", 0f);
            Shader.SetGlobalFloat("_MW_SunShadowValid", 0f);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // the map of the camera being rendered: cleared before each camera's culling, set again when its sun shadow
        // map has been drawn (cameras without shadows, e.g. the road reflection view, sample nothing)
        static void PreCull(Camera cam) => Shader.SetGlobalFloat("_MW_SunShadowValid", 0f);

        void Detach()
        {
            if (_light && _cmd != null) _light.RemoveCommandBuffer(LightEvent.AfterShadowMap, _cmd);
            _light = null;
        }

        void LateUpdate()
        {
            var driver = Sky.TimeOfDayDriver.Instance;
            var tod = driver ? driver.Tod : null;
            var sun = driver ? driver.Sun : null;
            if (!Enabled || tod == null || !sun)
            {
                Detach();
                Shader.SetGlobalFloat("_MW_WorldShadows", 0f);
                return;
            }
            if (_light != sun)
            {
                Detach();
                _cmd ??= new CommandBuffer { name = "NFSMW sun shadow map" };
                _cmd.Clear();
                _cmd.SetGlobalTexture("_MW_SunShadowMap", new RenderTargetIdentifier(BuiltinRenderTextureType.CurrentActive));
                _cmd.SetGlobalFloat("_MW_SunShadowValid", 1f);
                sun.AddCommandBuffer(LightEvent.AfterShadowMap, _cmd);
                _light = sun;
            }
            if (sun.shadows == LightShadows.None) sun.shadows = LightShadows.Hard;   // [ours] the map has to exist
            sun.shadowCustomResolution = Resolution;
            if (!_qualitySet)
            {
                _qualitySet = true;
                QualitySettings.shadowDistance = ShadowDistance;
                QualitySettings.shadowProjection = ShadowProjection.StableFit;   // MWShadow.cginc picks cascades by split spheres
            }
            // 0x6C83D0: [0x8FAE68] = 0.1 + (0.9 - 0.1) x clamp(sunZ - 0.14, 0, 0.537) / 0.537 x (1 - cloud)
            float strength = 0.1f + (0.9f - 0.1f) * tod.ShadowStrength(driver.Clouds.Cloud);
            Shader.SetGlobalFloat("_MW_WorldShadows", strength > MinStrength ? 1f : 0f);   // 0x6E0CB4: fcomp [0x8FAE50]
            Shader.SetGlobalFloat("_MW_InvShadowStrength", CarRender.CarEnvironment.TunnelFade + 1f - strength);
            Shader.SetGlobalFloat("_MW_SunShadowTexel", 1f / Mathf.Max(1, Resolution));
        }

        // ---- casters ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldStreamer hook, once per built world solid: world materials neither cast nor enter the camera depth pass
        /// (only the exe's casters get <see cref="CasterMaterials"/>).
        /// </summary>
        public static void Prepare(Material[] mats)
        {
            if (mats == null) return;
            foreach (var m in mats)
                if (m && !Casters.Contains(m)) m.SetShaderPassEnabled("ShadowCaster", false);
        }

        /// <summary>StuffScenery(view 0xF): is this scenery instance (effective flags) drawn into the shadow map?</summary>
        public static bool IsCaster(uint flags)
        {
            if (!Enabled) return false;
            if ((flags & 0x40000000u) != 0) return true;
            return Detail >= 2 && (flags & 0x300000u) != 0 && (flags & 0x4000000u) == 0;
        }

        /// <summary>Copies of the materials with the ShadowCaster pass on (opaque / alpha-tested ones only).</summary>
        public static Material[] CasterMaterials(Material[] mats)
        {
            var r = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || m.renderQueue > 2500 || (m.HasProperty("_ZWrite") && m.GetFloat("_ZWrite") < 0.5f)) { r[i] = m; continue; }
                if (!CasterCopies.TryGetValue(m, out var c) || !c)
                {
                    c = new Material(m) { name = m.name + " (shadow caster)" };
                    c.SetShaderPassEnabled("ShadowCaster", true);
                    CasterCopies[m] = c;
                    Casters.Add(c);
                    TextureScroll.Mirror(m, c);
                }
                r[i] = c;
            }
            return r;
        }

        /// <summary>Makes an instance (and its LOD child) a caster.</summary>
        public static void MakeCaster(GameObject go)
        {
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.sharedMaterials = CasterMaterials(mr.sharedMaterials);
                mr.shadowCastingMode = ShadowCastingMode.On;
            }
        }
    }
}
