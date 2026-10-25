using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Sky
{
    /// <summary>
    /// Runs speed.exe's per-frame world lighting (eDisplayFrame 0x6DE300) and publishes it to the shaders:
    ///   0x6D6CF0 fog constants (from the time of day of the previous frame), 0x769360 time-of-day update,
    ///   0x769580 lighting blend with the player view's cloud intensity, 0x6C6300/0x6C6450 DiffuseColour and
    ///   AmbientColour (clamped to 1), 0x6C60F0 sky constants, then Rain::UpdateAndRender's cloud easing (0x758100)
    ///   from the "Clouds" parameter map at the camera.
    /// Shader globals (MWLighting.cginc, SkyBox.shader): _MW_LightDir (Unity space, toward the sun), _MW_Diffuse,
    /// _MW_Ambient (w = 4), _MW_Specular, _MW_FogBrBm, _MW_FogOneOverBrBm, _MW_FogConst1/2/3, _MW_FogValue,
    /// _MW_CloudIntensity, _MW_CloudScroll, _MW_Enabled. See NFSMW_RE/specs/sky_lighting.md.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class TimeOfDayDriver : MonoBehaviour
    {
        public static TimeOfDayDriver Instance { get; private set; }

        public TimeOfDay Tod { get; private set; }
        public readonly CloudState Clouds = new CloudState();
        public readonly SkyConstants Sky = new SkyConstants();
        public ParameterMaps Maps { get; private set; }
        public FogConstants Fog { get; private set; }
        public Light Sun;
        /// <summary>[ours] false freezes the time of day (the exe always runs it offline; see the spec).</summary>
        public static bool RunTimeOfDay = true;
        bool _fogEnable = true;

        public static TimeOfDayDriver Ensure(Light sun)
        {
            if (!Instance)
            {
                var go = new GameObject("World Lighting (time of day)");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<TimeOfDayDriver>();
            }
            if (sun) Instance.Sun = sun;
            return Instance;
        }

        void Awake()
        {
            Instance = this;
            var db = GameData.Attributes;
            Tod = new TimeOfDay(db);
            // the `fog` collection (class EBCEE74C, key bStringHash32("fog") = CE3B435B): only FogEnable is used on PC
            if (db != null && db.Collections.TryGetValue((0xEBCEE74Cu, 0xCE3B435Bu), out var fog)
                && fog.TryGetKey(0x110882D5u, out int at, out _, out _))
                _fogEnable = fog.Source.Data[at] != 0;
            try
            {
                if (!string.IsNullOrEmpty(GameData.Root))
                    Maps = ParameterMaps.Load(File.ReadAllBytes(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN")));
            }
            catch (IOException e) { Debug.LogWarning("ParameterMaps: " + e.Message); }
            Clouds.Cloud = CloudState.CloudsMinAmount;
            Fog = FogConstants.FromTimeOfDay(Tod, _fogEnable);
            Publish();
            NfsMw.World.RoadReflection.Ensure(gameObject);   // ReflectedTex of the roads (world_render.md §4)
            NfsMw.World.WorldShadows.Ensure(gameObject);     // the sun shadow map of the world effects (world_render.md §13)
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            float real = Time.unscaledDeltaTime, world = Time.deltaTime;
            // 0x6D6CF0 runs first in eDisplayFrame, on last frame's blended lighting
            Fog = FogConstants.FromTimeOfDay(Tod, _fogEnable);
            if (RunTimeOfDay) Tod.Update(real, false);    // [?] front-end limit (game flow state 3) not used: no FE world
            Tod.Blend(Clouds.Cloud, false);
            Sky.AdvanceScroll(Time.frameCount, Tod.Speed, world);
            Publish();
            // Rain::UpdateAndRender 0x758100 (eDisplayFrame 0x6DF545: game flow state 6, g_ParticleSystemEnable): rain,
            // wetness and the cloud amount from the Rain / Clouds parameter maps over WorldTimeSeconds (Runtime/World/Rain.cs)
            var rain = NfsMw.World.Rain.Ensure(gameObject);
            if (rain.Step(Maps))
            {
                Clouds.Rain = rain.Precip.Rain;
                Clouds.Cloud = rain.Precip.Cloud;
            }
        }

        static Vector4 Clamp1(float[] v, float w) => new Vector4(Mathf.Min(v[0], 1f), Mathf.Min(v[1], 1f), Mathf.Min(v[2], 1f), w);
        static Vector4 V(float[] v) => new Vector4(v[0], v[1], v[2], v.Length > 3 ? v[3] : 0f);

        void Publish()
        {
            var s = Tod.S;
            var sd = Tod.SunDirection;
            var dir = new Vector3(sd.X, sd.Z, sd.Y);                       // Coords.ToUnity
            Shader.SetGlobalVector("_MW_LightDir", dir);
            // 0x6C6300: min(diffuse * [0x9018C4] (1), 1), w = +0x2C; 0x6C6450: min(ambient, 1), w = +0x3C (4)
            Shader.SetGlobalVector("_MW_Diffuse", Clamp1(Tod.Vec(0x20), s[0x2C / 4]));
            Shader.SetGlobalVector("_MW_Ambient", Clamp1(Tod.Vec(0x30), s[0x3C / 4]));
            // 0x6D6AA0: SpecularColour = +0x40 rgb, w = the view's Rain +0x36A0 wetness (Runtime/World/Rain.cs), SpecularPower 11
            Shader.SetGlobalVector("_MW_Specular", new Vector4(s[0x40 / 4], s[0x44 / 4], s[0x48 / 4], NfsMw.World.Rain.RoadWetness));
            // 0x6D71F0 (worldreflect.efx, effect 1, the roads): SpecularColour = (1 - player view Rain +0x290 cloud) x +0x40,
            // w = wetness; the same SpecularPower [0x8FADC0] = 11 (world_render.md §2)
            float dry = 1f - Clouds.Cloud;
            Shader.SetGlobalVector("_MW_SpecularRoad", new Vector4(dry * s[0x40 / 4], dry * s[0x44 / 4], dry * s[0x48 / 4], NfsMw.World.Rain.RoadWetness));
            Shader.SetGlobalFloat("_MW_SpecularPower", 11f);
            var f = Fog;
            Shader.SetGlobalVector("_MW_FogBrBm", V(f.BrPlusBm));
            Shader.SetGlobalVector("_MW_FogOneOverBrBm", V(f.OneOverBrBm));
            Shader.SetGlobalVector("_MW_FogConst1", V(f.Const1));
            Shader.SetGlobalVector("_MW_FogConst2", V(f.Const2));
            Shader.SetGlobalVector("_MW_FogConst3", V(f.Const3));
            Shader.SetGlobalFloat("_MW_FogValue", f.FogValue);
            Shader.SetGlobalVector("_MW_CloudIntensity", V(SkyConstants.CloudIntensity(Clouds.Cloud)));
            Shader.SetGlobalFloat("_MW_CloudScroll", Sky.CloudScroll);
            // shadow_cg.efx ShadowColor (sub_6C6630, the effect's per-frame setter): rgb [0x9018D0..D8] = 0.5, a = [0x9018CC] 1.0 x
            // [0x8FAE68] = 0.1 + 0.8 x sun shadow strength (0x6C83D0) in the player view (Shaders/WorldShadowCg.shader)
            float shadowStrength = 0.1f + 0.8f * Tod.ShadowStrength(Clouds.Cloud);
            Shader.SetGlobalVector("_MW_ShadowCgColor", new Vector4(0.5f, 0.5f, 0.5f, 1.0f * shadowStrength));
            Shader.SetGlobalFloat("_MW_Enabled", 1f);
            // [ours] Unity's directional light (car shading, shadows) follows the same sun
            if (Sun)
            {
                if (dir.sqrMagnitude > 1e-6f) Sun.transform.rotation = Quaternion.LookRotation(-dir);
                var dc = Clamp1(Tod.Vec(0x20), 1f);
                Sun.color = new Color(dc.x, dc.y, dc.z);
            }
        }
    }
}
