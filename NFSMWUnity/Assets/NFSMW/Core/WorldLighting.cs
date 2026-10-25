using System;

namespace NfsMw.Core
{
    /// <summary>
    /// The PC time-of-day object (speed.exe: allocated by 0x6BF290 into [0x9B392C], 0x190 bytes, ctor 0x770D70).
    /// It holds the sun position (time of day), the eight `timeofdaylighting` collections it blends and the blended
    /// lighting every world effect reads. Field offsets are the exe's (S[off/4]):
    ///   +0x00 speed (1.0)  +0x04 direction (int, -1)  +0x08 time of day (0.45)  +0x0C sun azimuth (3.28 rad)
    ///   +0x10 sun tilt (0.5 rad)
    ///   +0x20 DiffuseColour  +0x30 AmbientColour (w = 4.0)  +0x40 SpecularColour  +0x60 CarSpecScale
    ///   +0x64 EnvSkyBrightness  +0x70 FogSkyColour  +0x80 FogHazeColour  +0x90 FogSkyColourScale
    ///   +0x94 FogHazeColourScale  +0x98 FogInLightScatter  +0x9C FogSunFalloff  +0xA0 FogDistanceScale
    ///   +0xB0 FixedFunctionSkyColor  +0xC0..+0x15C eight Attrib instances  +0x160 cloud amount  +0x164 dusk blend
    ///   +0x170 sun position (x 100000)  +0x180 normalised sun direction (toward the sun, game space, Z up)
    /// See NFSMW_RE/specs/sky_lighting.md.
    /// </summary>
    public sealed class TimeOfDay
    {
        public const uint LightingClass = 0x399ED882;   // timeofdaylighting
        // ctor order (0x770D70): +0xC0 sunny, +0xD4 dusk_sunny, +0xE8 overcast, +0xFC dusk_overcast (front end set),
        // +0x110 midday (A01A3942), +0x124 0E1C797C, +0x138 94ABDFF0, +0x14C 919B6689 (in-game set)
        public static readonly uint[] Collections = { 0x3DCFA673, 0xB902CFAD, 0x59C373C3, 0xD835D0C8, 0xA01A3942, 0x0E1C797C, 0x94ABDFF0, 0x919B6689 };

        public readonly float[] S = new float[0x190 / 4];
        readonly float[][] _layouts = new float[8][];   // per collection: the fields 0x768EE0 reads, in layout order

        public float Speed { get => S[0]; set => S[0] = value; }
        public int Direction { get => BitConverter.SingleToInt32Bits(S[1]); set => S[1] = BitConverter.Int32BitsToSingle(value); }
        public float Time { get => S[2]; set => S[2] = value; }
        public Vec3 SunPosition => new Vec3(S[0x170 / 4], S[0x174 / 4], S[0x178 / 4]);
        public Vec3 SunDirection => new Vec3(S[0x180 / 4], S[0x184 / 4], S[0x188 / 4]);
        public float CloudAmount => S[0x160 / 4];
        public float DuskBlend => S[0x164 / 4];

        public float[] Vec(int off) => new[] { S[off / 4], S[off / 4 + 1], S[off / 4 + 2], S[off / 4 + 3] };

        /// <summary>The ctor (0x770D70): defaults, one Update and one Blend(0.1).</summary>
        public TimeOfDay(AttribDatabase db)
        {
            for (int i = 0; i < 8; i++) _layouts[i] = ReadLighting(db, Collections[i]);
            Set4(0x20, 0.61f, 0.49f, 0.23f, 1f);      // 3F1C28F6 3EFAE148 3E6B851F
            Set4(0x30, 0.14f, 0.16f, 0.22f, 1f);      // 3E0F5C29 3E23D70A 3E6147AE
            Set4(0x40, 0.58f, 0.53f, 0.42f, 1f);      // 3F147AE1 3F07AE14 3ED70A3D
            S[0x60 / 4] = 1f;
            S[0x64 / 4] = 2.5f;
            Set4(0x70, 0.27f, 0.46f, 0.96f, 1f);      // 3E8A3D71 3EEB851F 3F75C28F
            Set4(0x80, 0.19f, 0.25f, 0.38f, 1f);      // 3E428F5C 3E800000 3EC28F5C
            S[0x90 / 4] = 2f;
            S[0x94 / 4] = 0f;
            S[0x98 / 4] = 8f;
            S[0x9C / 4] = -0.16f;                     // BE23D70A
            S[0x2C / 4] = 1f;
            S[0x3C / 4] = 4f;
            S[0] = 1f;
            S[2] = 0.45f;                             // 3EE66666
            S[3] = 3.28f;                             // 4051EB85
            S[4] = 0.5f;
            Direction = -1;
            Update(0f, false);
            Blend(0.1f, false);
            Set4(0xB0, 0.61f, 0.49f, 0.23f, 1f);
        }

        void Set4(int off, float x, float y, float z, float w)
        {
            S[off / 4] = x; S[off / 4 + 1] = y; S[off / 4 + 2] = z; S[off / 4 + 3] = w;
        }

        // the 0x768EE0 source fields, in this order: Diffuse rgb, Ambient rgb, Specular rgb, CarSpecScale,
        // EnvSkyBrightness, FogSkyColour rgb, FogHazeColour rgb, FogSkyColourScale, FogHazeColourScale,
        // FogInLightScatter, FogSunFalloff, FogDistanceScale, FixedFunctionSkyColor rgb
        static readonly string[] Vec3Fields = { "DiffuseColour", "AmbientColour", "SpecularColour" };

        static float[] ReadLighting(AttribDatabase db, uint key)
        {
            var v = new float[26];
            AttribCollection c = null;
            db?.Collections.TryGetValue((LightingClass, key), out c);
            if (c == null) return v;
            int k = 0;
            foreach (var f in Vec3Fields) { V3(c, f, v, k); k += 3; }
            v[k++] = c.Float("CarSpecScale");
            v[k++] = c.Float("EnvSkyBrightness");
            V3(c, "FogSkyColour", v, k); k += 3;
            V3(c, "FogHazeColour", v, k); k += 3;
            v[k++] = c.Float("FogSkyColourScale");
            v[k++] = c.Float("FogHazeColourScale");
            v[k++] = c.Float("FogInLightScatter");
            v[k++] = c.Float("FogSunFalloff");
            v[k++] = c.Float("FogDistanceScale");
            V3(c, "FixedFunctionSkyColor", v, k);
            return v;
        }

        static void V3(AttribCollection c, string f, float[] v, int k)
        {
            if (!c.TryGet(f, out int at, out _, out _)) return;
            var d = c.Source.Data;
            v[k] = Chunks.F32(d, at); v[k + 1] = Chunks.F32(d, at + 4); v[k + 2] = Chunks.F32(d, at + 8);
        }

        // where each 0x768EE0 source field lands in the blended struct (byte offsets relative to +0x20)
        static readonly int[] Dest = { 0x00, 0x04, 0x08, 0x10, 0x14, 0x18, 0x20, 0x24, 0x28, 0x40, 0x44, 0x50, 0x54, 0x58,
                                       0x60, 0x64, 0x68, 0x70, 0x74, 0x78, 0x7C, 0x80, 0x90, 0x94, 0x98 };

        /// <summary>0x768EE0: A + (B - A) * w per field into a 0xA0-byte temp (layout of +0x20).</summary>
        float[] Lerp(float w, int a, int b)
        {
            var t = new float[0xA0 / 4];
            var A = _layouts[a]; var B = _layouts[b];
            for (int i = 0; i < Dest.Length; i++) t[Dest[i] / 4] = (B[i] - A[i]) * w + A[i];
            return t;
        }

        /// <summary>
        /// 0x769360, called every frame by eDisplayFrame (0x6DE3DC). The time of day runs between limit and 1 - limit
        /// (0.35 in the front end, game flow state 3; 0.05 otherwise) at RealTimeElapsed / 60 * 0.05 per second and
        /// bounces at the ends; then the sun position and direction follow. When the online manager drives the time
        /// (0x735620) the exe reads it from the server clock instead; that path is not ported.
        /// </summary>
        public void Update(float realTimeElapsed, bool inFrontEnd)
        {
            float limit = inFrontEnd ? 0.35f : 0.05f;            // 3EB33333 / 3D4CCCCD
            int dir = Direction;
            float t = realTimeElapsed * 0.016666668f * dir * S[0] * 0.05f + S[2];
            S[2] = t;
            if (t < limit) { S[2] = limit; Direction = -dir; }   // 0x7693B9: (t < limit) -> clamp, flip
            else if (t > 1f - limit) { S[2] = 1f - limit; Direction = -dir; }

            float tc = S[2];
            if (tc <= limit) tc = limit;                          // 0x769415
            if (1f - limit < tc) tc = 1f - limit;
            float e = (float)((tc - 0.5) * 3.14159274);           // qword 0.5 and pi (0x8987A8, 0x8B5BF8)
            float a = S[3], b = S[4];
            const float halfPi = 1.570796f;
            float cosA = BSin(a + halfPi), cosE = BSin(e + halfPi), sinB = BSin(b), sinA = BSin(a), sinE = BSin(e);
            S[0x170 / 4] = (sinE * cosA + sinA * sinB * cosE) * 100000f;
            S[0x174 / 4] = (sinB * cosA * cosE + sinE * sinA) * 100000f;
            S[0x178 / 4] = BSin(b + halfPi) * BSin(e + halfPi) * 100000f;
            S[0x17C / 4] = 1f;
            S[0x18C / 4] = 0f;
            Normalize(0x170, 0x180);                              // 0x45F490
        }

        /// <summary>
        /// 0x769580: blends the four in-game collections by cloud amount (+0x160) and by the sun height. Front-end set
        /// when [0x901828] != 0 (0x6C0A20) [?]. [0x905CE8] (-1) would override the cloud amount when non-negative.
        /// </summary>
        public void Blend(float cloud, bool frontEndSet)
        {
            float z = S[0x188 / 4];
            if (z <= 0.25f) z = 0.25f;
            else if (z > 0.5f) z = 0.5f;
            S[0x160 / 4] = cloud;
            S[0x164 / 4] = 1f - (z - 0.25f) * 4f;
            float c = S[0x160 / 4];
            float[] low, high;
            if (frontEndSet) { low = Lerp(c, 1, 3); high = Lerp(c, 0, 2); }   // dusk_sunny/dusk_overcast, sunny/overcast
            else { low = Lerp(c, 5, 7); high = Lerp(c, 4, 6); }              // 0E1C797C/919B6689, midday/94ABDFF0
            // 0x769190: this+0x20 = high + (low - high) * dusk (only the fields 0x768EE0 wrote)
            float w = S[0x164 / 4];
            foreach (int o in Dest)
            {
                int i = o / 4;
                S[0x20 / 4 + i] = (low[i] - high[i]) * w + high[i];
            }
            S[0x3C / 4] = 4f;
        }

        /// <summary>SetTimeOfDay (0x7696B0); GRaceParameters (0x5FD210/0x600AB0) call it with a race's TOD when >= 0.</summary>
        public void SetTimeOfDay(float t) => S[2] = t;

        void Normalize(int src, int dst)
        {
            float x = S[src / 4], y = S[src / 4 + 1], z = S[src / 4 + 2];
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            float inv = len > 0f ? 1f / len : 0f;
            S[dst / 4] = x * inv; S[dst / 4 + 1] = y * inv; S[dst / 4 + 2] = z * inv;
        }

        /// <summary>The exe's sine (0x45DBD0 -> 0x45DB30): radians to a 16-bit bAngle (x 10430.38, truncated), then sin.</summary>
        public static float BSin(float radians)
        {
            int ang = (int)(radians * 10430.38f);
            ushort u = unchecked((ushort)ang);
            return (float)Math.Sin(u * (2.0 * Math.PI / 65536.0));
        }

        /// <summary>The exe's 6C83D0 shadow strength: clamp(sunZ - 0.14, 0, 0.537) / 0.537 * (1 - cloud).</summary>
        public float ShadowStrength(float cloud)
        {
            float span = 0.677f - 0.14f;
            float v = S[0x188 / 4] - 0.14f;
            if (v < 0f) v = 0f;
            if (v > span) v = span;
            return v / span * (1f - cloud);
        }
    }

    /// <summary>
    /// The fog constants speed.exe sets on the shared world effect parameters every frame (0x6D6CF0, from
    /// eDisplayFrame 0x6DE3C6). [0x901934] = 1 in the release exe, so the inputs come from the time-of-day object, not
    /// from the `fog` collection (whose FogEnable flag is still read):
    ///   br = FogSkyColourScale * 0.01 * (0.01 - 0.0001) + 0.0001            ([0x90193C], [0x901938])
    ///   bm = FogHazeColourScale * 0.01 > 0 ? FogHazeColourScale * 0.01 * (0.01 - 0.0001) + 0.0001 : 0
    ///   Br = br * FogSkyColour, Bm = bm * FogHazeColour                     (stored at 0x8FAE04 / 0x8FAE14)
    ///   Fog_Br_Plus_Bm = Br + Bm, Fog_One_Over_BrBm = 1 / (Br + Bm)
    ///   Fog_Const_1 = Br * 3/(16 pi), Fog_Const_2 = Bm * 1/(4 pi)
    ///   Fog_Const_3 = ((1 - g)^2, 1 + g^2, -2 g, FogInLightScatter), g = FogSunFalloff
    ///   FogValue = FogDistanceScale, SunColor = (1, 1, 1, 1) (0x8FAE24, never written)
    /// With FogEnable off the sums are zero (no extinction, no in-scatter) and Fog_Const_3.w is zero.
    /// </summary>
    public struct FogConstants
    {
        public float[] BrPlusBm, OneOverBrBm, Const1, Const2, Const3, SunColor;
        public float FogValue;
        public float[] Br, Bm;

        public static FogConstants FromTimeOfDay(TimeOfDay tod, bool fogEnable)
        {
            var s = tod.S;
            float range = (float)(0.0099999998f - (double)0.000099999997f);   // fld [0x90193C]; fsub [0x901938] -> qword
            float bmMul = s[0x94 / 4] * 0.01f, brMul = s[0x90 / 4] * 0.01f;
            float bm = bmMul > 0f ? bmMul * range + 0.000099999997f : 0f;
            float br = brMul * range + 0.000099999997f;
            var f = new FogConstants
            {
                Br = new[] { br * s[0x70 / 4], br * s[0x74 / 4], br * s[0x78 / 4], br * s[0x7C / 4] },
                Bm = new[] { bm * s[0x80 / 4], bm * s[0x84 / 4], bm * s[0x88 / 4], bm * s[0x8C / 4] },
                BrPlusBm = new float[4], OneOverBrBm = new float[4], Const1 = new float[4], Const2 = new float[4],
                SunColor = new[] { 1f, 1f, 1f, 1f },
                FogValue = s[0xA0 / 4],
            };
            if (fogEnable)
            {
                for (int i = 0; i < 3; i++)
                {
                    f.BrPlusBm[i] = f.Br[i] + f.Bm[i];
                    f.OneOverBrBm[i] = 1f / f.BrPlusBm[i];
                    f.Const1[i] = f.Br[i] * 0.059683103f;
                    f.Const2[i] = f.Bm[i] * 0.07957747f;
                }
            }
            float g = s[0x9C / 4];
            f.Const3 = new[] { (1f - g) * (1f - g), g * g + 1f, g * -2f, fogEnable ? s[0x98 / 4] : 0f };
            return f;
        }
    }

    /// <summary>The sky effect constants (skybox.efx, set by 0x6C60F0 each time the sky effect starts).</summary>
    public sealed class SkyConstants
    {
        public float CloudScroll;          // [0x988548]
        int _lastFrame = -1;               // [0x98854C] vs the frame counter [0x8F8310]

        /// <summary>CloudScroll += TimeOfDay.Speed * 0.5 * WorldTimeElapsed * 0.04, once per frame.</summary>
        public void AdvanceScroll(int frame, float todSpeed, float worldTimeElapsed)
        {
            if (frame == _lastFrame) return;
            _lastFrame = frame;
            CloudScroll += todSpeed * 0.5f * worldTimeElapsed * 0.04f;
        }

        /// <summary>
        /// SKY_DIFFUSESCALE ("CloudIntensity"): c = the view's Rain cloud intensity (+0x290), 0 -> 0.1;
        /// SkyRenderForceOvercast ([0x8FAE40], -1) replaces it when >= 0. w is left unset by the exe.
        /// </summary>
        public static float[] CloudIntensity(float cloud, float forceOvercast = -1f)
        {
            float w = cloud == 0f ? 0.1f : cloud;
            if (!(forceOvercast < 0f)) w = forceOvercast;
            float x = 3.3333333f * w;
            x = x > 0f ? Math.Min(x, 1f) : 0f;
            float w2 = w * w;
            float y = w2 + w2 > 0f ? Math.Min(w2 + w2, 0.8f) : 0f;
            float z = w2 > 0f ? Math.Min(w2, 0.8f) : 0f;
            return new[] { x, y, z, 0f };
        }
    }

    /// <summary>
    /// The cloud/rain part of Rain::UpdateAndRender (0x758100), run every frame while racing (game flow state 6):
    /// targets come from the "Rain" and "Clouds" parameter-map layers at the camera, intensities ease towards them
    /// (rain at 1/s, clouds at 1/10 s). Offline the rain target is the override [0x9B0A48] (0 unless an NIS sets it).
    /// </summary>
    public sealed class CloudState
    {
        public float Rain;                       // Rain +0x28C
        public float Cloud;                      // Rain +0x290 (starts at twkCloudsMinAmount [0x9B0A3C] = 0)
        public float RainOverride;               // SetOverRideRainIntensity [0x9B0A48]
        public const float CloudsMinAmount = 0f; // twkCloudsMinAmount

        /// <summary>0x73CA50 offline: r = 1 - max(sin((0.7142857 + 0.02 x) deg), 0) (the time term [0x8F8308] = 1).</summary>
        public static float CloudChance(float x)
        {
            float deg = 1f * 0.71428573f + x * 0.02f;
            int n = (int)(deg * 65536f);
            int ang = n / 360;                     // imul 0xB60B60B7 / sar 8: signed divide by 360
            float v = (float)Math.Sin(unchecked((ushort)ang) * (2.0 * Math.PI / 65536.0));
            if (v < 0f) v = 0f;
            return 1f - Math.Abs(v);
        }

        /// <summary>0x753AC0: the Clouds layer's field 0 where CloudChance(x) &lt;= field 1, else the minimum.</summary>
        public static float CloudsAt(ParameterMapLayer clouds, float x, float y)
        {
            float r = CloudChance(x);
            if (clouds == null || !clouds.Valid) return CloudsMinAmount;
            int set = clouds.GetParameterData(x, y);
            float threshold = clouds.GetFloat(1, set);
            return r > threshold ? CloudsMinAmount : clouds.GetFloat(0, set);
        }

        /// <summary>0x7539D0 offline: the override when non-zero; a negative override would read the Rain layer.</summary>
        public float RainAt(ParameterMapLayer rain, float x, float y)
        {
            if (RainOverride > 0f) return RainOverride;
            if (RainOverride == 0f) return 0f;
            // [?] negative override: the map path (0x73C9F0 chance with cos) - not reached offline
            return 0f;
        }

        public void Update(ParameterMaps maps, float camX, float camY, float worldTimeElapsed)
        {
            float rainTarget = RainAt(maps?.Layer("Rain"), camX, camY);
            float cloudTarget = rainTarget > 0f ? rainTarget : CloudsAt(maps?.Layer("Clouds"), camX, camY);
            float dt = worldTimeElapsed == 0f ? 0.001f : worldTimeElapsed;
            float inv = 1f / dt;
            Rain += (rainTarget - Rain) / (inv * 1f);
            if (rainTarget == 0f && Rain < 0.01f) Rain = 0f;
            if (Rain > 1f) Rain = 1f;
            Cloud += (cloudTarget - Cloud) / (inv * 10f);
            if (cloudTarget == 0f && Cloud < CloudsMinAmount + 0.01f) Cloud = CloudsMinAmount;
            if (Cloud > 1f) Cloud = 1f;
        }
    }
}
