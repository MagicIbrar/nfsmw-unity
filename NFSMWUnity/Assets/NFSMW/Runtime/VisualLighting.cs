using NfsMw.Core;
using NfsMw.Sky;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// World lighting entry point. The constants the world effects use are no longer guessed: speed.exe computes them
    /// every frame from its time-of-day object (blend of the `timeofdaylighting` collections by sun height and cloud
    /// amount) and the fog formula at 0x6D6CF0; Runtime/Sky/TimeOfDayDriver.cs runs that and sets the _MW_* globals
    /// that MWLighting.cginc and SkyBox.shader read. See NFSMW_RE/specs/sky_lighting.md.
    /// </summary>
    public static class VisualLighting
    {
        public const uint Class = TimeOfDay.LightingClass, Midday = 0xA01A3942;

        /// <summary>Starts the per-frame lighting and points the scene's directional light along the game's sun.</summary>
        public static void Apply(Light sun) => TimeOfDayDriver.Ensure(sun);

        /// <summary>
        /// SetTimeOfDay (0x7696B0): races with a gameplay "TOD" attribute >= 0 set it when they start
        /// (GRaceParameters 0x5FD210 / GRaceDatabase::SetStartupRace 0x600AB0), e.g. 1.0 for the D-Day intro.
        /// </summary>
        public static void SetTimeOfDay(float tod)
        {
            var d = TimeOfDayDriver.Instance;
            if (d != null && tod >= 0f) d.Tod.SetTimeOfDay(tod);
        }

        /// <summary>The current time of day (0..1, 0.5 = sun highest), or -1 before the lighting runs.</summary>
        public static float TimeOfDayValue => TimeOfDayDriver.Instance != null ? TimeOfDayDriver.Instance.Tod.Time : -1f;

        /// <summary>The player's cloud intensity (Rain +0x290), from the "Clouds" parameter map.</summary>
        public static float CloudIntensity => TimeOfDayDriver.Instance != null ? TimeOfDayDriver.Instance.Clouds.Cloud : 0f;
    }
}
