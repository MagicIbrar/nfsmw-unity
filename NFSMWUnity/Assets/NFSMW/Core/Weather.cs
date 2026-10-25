using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    // ============================================================================================================
    // World/WeatherMan.cpp, World/rain.cpp and World/ScreenEffects.cpp as speed.exe v1.3 (PC) runs them.
    // Unity-free: Runtime/World/Rain.cs and Runtime/World/ScreenEffects.cs feed the per-frame inputs and draw.
    // Spec: NFSMW_RE/specs/world_render.md ("Rain and wet roads", "WeatherMan", "ScreenEffects", "EventManager").
    // [?] = not confirmed, [ours] = Unity-side addition. Offsets in comments are the PC Rain object's (0x3C80+ bytes,
    // ctor 0x749DC0), not the GameCube header's.
    // ============================================================================================================

    /// <summary>GenericRegion (WeatherMan.hpp, 0xA4 bytes on disc and in memory).</summary>
    public sealed class GenericRegion
    {
        public const int Rain = 0, Fog = 1, Snow = 2, Wind = 3, Clear = 4, Tunnel = 5, HorizonColour = 6, FogVolume = 7, Bloom = 8;
        public string Name, GroupName;
        public int Type;
        public uint FogColour;
        public float FogStart, Parameter1, X, Y, Z, Radius, Intensity, FarFalloffStart, FogFalloff, FogFalloffX, FogFalloffY,
            ScreenPosX, ScreenPosY, Modifier, Effect;
        public uint NameHash, GroupNameHash, InFlags;
        public int Shape, Blend, Dynamic;
        public override string ToString() => $"{Name} ({GroupName}) type {Type} at ({X:0.#}, {Y:0.#}, {Z:0.#}) r {Radius:0.#} intensity {Intensity:0.##}";
    }

    /// <summary>
    /// LoaderWeatherMan 0x72CAB0: chunk 0x34250 (BCHUNK_GENERIC_REGIONS, TRACKS\L2RA.BUN) = WeatherRegionPack {bTNode 8,
    /// i32 Version (must be 2), i32 NumRegions} at 16-aligned data, then NumRegions x 0xA4 GenericRegion. A type-0 (rain)
    /// region with Intensity 0 becomes type 5 (tunnel); types 0..8 go to RegionLists[type] (0x9AA380), others are dropped.
    /// On PC only two readers exist: RegionQuery::CalculateRegionInfo 0x72A170 (its only caller 0x6BFFD0 is never called)
    /// and GetClosestRegionInView 0x72A760 (DoTunnelBloom). getHorizonColour 0x72A500 has no caller.
    /// </summary>
    public static class WeatherRegions
    {
        public const uint ChunkId = 0x34250;
        public const int RecordSize = 0xA4;

        public static List<GenericRegion> Read(byte[] d)
        {
            var list = new List<GenericRegion>();
            if (d == null) return list;
            foreach (var c in Chunks.FindAll(d, 0, d.Length, ChunkId))
            {
                int p = Chunks.Align(c.DataStart, 16);
                if (p + 16 > c.End || Chunks.I32(d, p + 8) != 2) continue;
                int n = Chunks.I32(d, p + 12);
                for (int i = 0, o = p + 16; i < n && o + RecordSize <= c.End; i++, o += RecordSize)
                {
                    var r = new GenericRegion
                    {
                        Name = Chunks.CString(d, o + 8, 32), GroupName = Chunks.CString(d, o + 0x28, 32),
                        Type = Chunks.I32(d, o + 0x48), FogColour = Chunks.U32(d, o + 0x4C), FogStart = Chunks.F32(d, o + 0x50),
                        Parameter1 = Chunks.F32(d, o + 0x54), X = Chunks.F32(d, o + 0x58), Y = Chunks.F32(d, o + 0x5C),
                        Z = Chunks.F32(d, o + 0x60), Radius = Chunks.F32(d, o + 0x64), Intensity = Chunks.F32(d, o + 0x68),
                        FarFalloffStart = Chunks.F32(d, o + 0x6C), FogFalloff = Chunks.F32(d, o + 0x70),
                        FogFalloffX = Chunks.F32(d, o + 0x74), FogFalloffY = Chunks.F32(d, o + 0x78),
                        ScreenPosX = Chunks.F32(d, o + 0x7C), ScreenPosY = Chunks.F32(d, o + 0x80),
                        NameHash = Chunks.U32(d, o + 0x84), GroupNameHash = Chunks.U32(d, o + 0x88), Shape = Chunks.I32(d, o + 0x8C),
                        Blend = Chunks.I32(d, o + 0x90), Modifier = Chunks.F32(d, o + 0x94), InFlags = Chunks.U32(d, o + 0x98),
                        Effect = Chunks.F32(d, o + 0x9C), Dynamic = Chunks.I32(d, o + 0xA0),
                    };
                    if (r.Type == GenericRegion.Rain && r.Intensity == 0f) r.Type = GenericRegion.Tunnel;   // 0x72CAE7
                    if (r.Type >= 0 && r.Type < 9) list.Add(r);
                }
            }
            return list;
        }

        /// <summary>
        /// GetClosestRegionInView 0x72A760: among the REGION_BLOOM regions, the nearest one (3D, under 99999) whose
        /// direction from the camera is in front (dot with the camera direction &gt; 0). angleCos = that dot.
        /// (The exe first sorts the list with DepthRegion; the pick does not depend on the order.)
        /// </summary>
        public static GenericRegion ClosestBloomInView(IList<GenericRegion> regions, Vec3 camPos, Vec3 camDir, out float angleCos)
        {
            angleCos = 0f;
            GenericRegion best = null;
            float bestD = 99999f;
            if (regions == null) return null;
            foreach (var r in regions)
            {
                if (r.Type != GenericRegion.Bloom) continue;
                float dx = r.X - camPos.X, dy = r.Y - camPos.Y, dz = r.Z - camPos.Z;
                float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len <= 0f) continue;
                float c = (dx * camDir.X + dy * camDir.Y + dz * camDir.Z) / len;
                if (c > 0f && len < bestD) { bestD = len; best = r; angleCos = c; }
            }
            return best;
        }
    }

    /// <summary>TrackPathZone segment helpers used by the rain curtain and the tunnel bloom.</summary>
    public static class ZoneSegments
    {
        /// <summary>
        /// TrackPathZone::GetSegmentNextTo 0x723330: for each edge (i, i+1) the signed distance d of p to the edge's line
        /// (unit normal (dy, -dx)); the edge is a candidate when p +- 1.1 d n is inside the polygon. Returns the smallest
        /// |d| and that edge's end points, or -1 when no edge qualifies.
        /// </summary>
        public static float GetSegmentNextTo(TrackPathZone z, float px, float py, out float ax, out float ay, out float bx, out float by)
        {
            ax = ay = bx = by = 0f;
            int n = z?.Xs?.Length ?? 0;
            int best0 = -1, best1 = -1;
            float best = 99999f;
            for (int i = 0; i < n; i++)
            {
                int i0 = i % n, i1 = (i + 1) % n;
                float ex = z.Xs[i0] - px, ey = z.Ys[i0] - py;
                float nx = z.Ys[i1] - z.Ys[i0], ny = -(z.Xs[i1] - z.Xs[i0]);
                float l = (float)Math.Sqrt(nx * nx + ny * ny);
                if (l > 0f) { nx /= l; ny /= l; }
                float d = ex * nx + ey * ny;
                if (Math.Abs(d) < best && (z.Contains(px - 1.1f * nx * d, py - 1.1f * ny * d) || z.Contains(px + 1.1f * nx * d, py + 1.1f * ny * d)))
                {
                    best0 = i0; best1 = i1; best = Math.Abs(d);
                }
            }
            if (best0 < 0 || best1 < 0) return -1f;
            ax = z.Xs[best0]; ay = z.Ys[best0]; bx = z.Xs[best1]; by = z.Ys[best1];
            return best;
        }

        /// <summary>
        /// sub_722E90: the zone edge most parallel to (a - b) (max |dot| of unit directions), skipping the edge a-b itself in
        /// either direction: the far mouth of a tunnel.
        /// </summary>
        public static void OppositeSegment(TrackPathZone z, float ax, float ay, float bx, float by, out float cx, out float cy, out float dx, out float dy)
        {
            int n = z?.Xs?.Length ?? 0;
            cx = cy = dx = dy = 0f;
            if (n == 0) return;
            float ux = ax - bx, uy = ay - by, ul = (float)Math.Sqrt(ux * ux + uy * uy);
            if (ul > 0f) { ux /= ul; uy /= ul; }
            float best = 0f;
            int bi = 0;
            for (int i = 0; i < n; i++)
            {
                int i0 = i % n, i1 = (i + 1) % n;
                bool same = ax == z.Xs[i0] && ay == z.Ys[i0] && bx == z.Xs[i1] && by == z.Ys[i1];
                bool rev = bx == z.Xs[i0] && by == z.Ys[i0] && ax == z.Xs[i1] && ay == z.Ys[i1];
                if (same || rev) continue;
                float vx = z.Xs[i0] - z.Xs[i1], vy = z.Ys[i0] - z.Ys[i1], vl = (float)Math.Sqrt(vx * vx + vy * vy);
                if (vl > 0f) { vx /= vl; vy /= vl; }
                float f = Math.Abs(vx * ux + vy * uy);
                if (best < f) { best = f; bi = i; }
            }
            int j0 = bi % n, j1 = (bi + 1) % n;
            cx = z.Xs[j0]; cy = z.Ys[j0]; dx = z.Xs[j1]; dy = z.Ys[j1];
        }
    }

    /// <summary>The weather maths of rain.cpp that the PC exe keeps (most of it is stripped in the GameCube decomp).</summary>
    public static class RainMath
    {
        /// <summary>bSin / bCos of a 16-bit bAngle (0x45DB30 / 0x45DBF0).</summary>
        public static float BSin(ushort a) => (float)Math.Sin(a * (2.0 * Math.PI / 65536.0));
        public static float BCos(ushort a) => (float)Math.Cos(a * (2.0 * Math.PI / 65536.0));

        /// <summary>Degrees x 65536 -> ftol -> / 360 (imul 0xB60B60B7, signed) -> bAngle, as 0x73C9F0 / 0x73CA50 / 0x753B50.</summary>
        public static ushort DegToAng(float deg)
        {
            double v = (double)deg * 65536.0;
            int n = v >= int.MaxValue || v <= int.MinValue ? int.MinValue : (int)v;   // __ftol2 overflow = 0x80000000
            return unchecked((ushort)(n / 360));
        }

        /// <summary>
        /// 0x73C9F0 (offline; online squares it in 0x7539D0 and uses the server clock): the rain chance at game X.
        /// r = 1 - max(cos((0.7142857 t + 0.02 x) deg), 0). t = WorldTimeSeconds (0x73C980: [0x8F8308], = Sim::GetTime
        /// since the world was initialised; online: server seconds mod 503). One cycle = 504 s.
        /// </summary>
        public static float RainChance(float x, float t)
        {
            float c = BCos(DegToAng(t * 0.71428573f + x * 0.02f));
            if (c < 0f) c = 0f;
            return 1f - Math.Abs(c);
        }

        /// <summary>0x73CA50 offline: the cloud chance, r = 1 - max(sin((0.7142857 t + 0.02 x) deg), 0).</summary>
        public static float CloudChance(float x, float t)
        {
            float s = BSin(DegToAng(t * 0.71428573f + x * 0.02f));
            if (s < 0f) s = 0f;                     // offline only; online keeps the negative and takes |s|
            return 1f - Math.Abs(s);
        }

        /// <summary>
        /// GetDesiredRainIntensity 0x7539D0 (offline): rainOverrideIntensity [0x9B0A48] when &gt; 0; 0 when it is negative;
        /// otherwise the "Rain" layer at (x, y): field 0 when RainChance &lt;= field 1, else 0. The quadtree always yields
        /// a set (set 0 outside any node), so set 0 (0.5, 0.25) covers most of the map.
        /// </summary>
        public static float DesiredRain(ParameterMapLayer rain, float x, float y, float t, float overrideIntensity)
        {
            if (overrideIntensity > 0f) return overrideIntensity;
            if (overrideIntensity < 0f) return 0f;
            float r = RainChance(x, t);
            if (rain == null || !rain.Valid) return 0f;     // [0x9B2A9C] null: the caller skips the update instead
            int set = rain.GetParameterData(x, y);
            return r <= rain.GetFloat(1, set) ? rain.GetFloat(0, set) : 0f;
        }

        /// <summary>GetDesiredCloudyness 0x753AC0: the "Clouds" layer's field 0 when CloudChance &lt;= field 1, else
        /// twkCloudsMinAmount [0x9B0A3C] (0).</summary>
        public static float DesiredCloud(ParameterMapLayer clouds, float x, float y, float t, float minAmount = 0f)
        {
            float r = CloudChance(x, t);
            if (clouds == null || !clouds.Valid) return minAmount;
            int set = clouds.GetParameterData(x, y);
            return r <= clouds.GetFloat(1, set) ? clouds.GetFloat(0, set) : minAmount;
        }
    }

    /// <summary>One frame of input for <see cref="Precipitation"/>, all in game space (X east, Y north, Z up).</summary>
    public struct PrecipitationInput
    {
        public float Dt;               // WorldTimeElapsed [0x925970]
        public float WorldSeconds;     // WorldTimeSeconds [0x8F8308]
        public bool Paused;            // GameFlowManager::IsPaused
        public bool SplitScreen;       // eGetCurrentViewMode() >= 3
        public int ViewIndex;          // 0 = player 1 (eView id 1), 1 = player 2
        public Vec3 CamPos, CamDir;    // eView camera +0x40 / +0x50 (unit forward)
        public Vec3 CamVelocity;       // Camera::VelocityKey.Position (+0x200), m/s
        public bool HasCar;            // the camera mover's anchor (type 1) has a car
        public Vec3 CarPos;            // anchor car position (+0x20)
        public Vec3 CarFwd, CarLeft, CarUp;   // anchor car matrix rows (+0x50): local x forward, y left, z up
        /// <summary>TheTrackPathManager.FindZone(point, type) (0x727480): the first zone of the type holding the point.</summary>
        public Func<int, float, float, TrackPathZone> FindZone;
    }

    /// <summary>
    /// FogQuery::CalculateFogInfo (sub_753D90, the global FogQuery at 0x9AE364; called once per frame from eDisplayFrame
    /// 0x6DEBB2 for player 1's view, with the camera eye) - the reader of the Normal Fog / Rain Fog / Fog Blend Distance
    /// parameter-map layers. Field order of the 8 floats: 0 DistFogPower, 1 DistFogStart (m), 2/3/4 colour R/G/B
    /// (0..255 as floats; [? channel order from the Base*Colour debug globals 0x9031A4.. R, G, B]), 5 FogFalloff,
    /// 6 FogFalloffX, 7 FogFalloffY. Normal Fog is blended by distance over a hard-coded 50 m; with w = rain (or cloud/3
    /// when not raining) > 0 the Rain Fog set (blended over the Fog Blend Distance layer's field 0, 50 m) is mixed in by w.
    /// Nothing reads the result on PC except the precipitation fog colour it writes (Rain +0x34A8..B0; a whole-image scan
    /// finds no other reader of 0x9AE364..): the shader fog comes from the time of day (sky_lighting.md §3). Kept as a probe.
    /// The FogControlOverRide debug path ([0x9B0A70], never written) is left out.
    /// </summary>
    public sealed class FogQuery
    {
        public float FogFalloff, FogFalloffX, FogFalloffY, DistFogStart, DistFogPower;
        /// <summary>0x80BBGGRR (alpha byte fixed 0x80, 0x753F31..).</summary>
        public uint DistFogColour;
        public bool HasLayer;

        static float F(ParameterAccessor a, int i) => a.GetDataFloat(i);
        static uint Pack(float r, float g, float b) => 0x80000000u | ((uint)(int)b & 0xFF) << 16 | ((uint)(int)g & 0xFF) << 8 | ((uint)(int)r & 0xFF);

        public bool CalculateFogInfo(Precipitation precip, float camX, float camY,
            ParameterAccessorBlendByDistance normalFog, ParameterAccessorBlendByDistance rainFog, ParameterAccessor fogBlendDist)
        {
            HasLayer = normalFog != null && normalFog.Layer != null;
            if (!HasLayer) return false;
            normalFog.CaptureByDistance(camX, camY, 50f);               // 0x753DD7: 50.0 hard-coded, not the Fog Blend Distance layer
            var n = new float[8];
            for (int i = 0; i < 8; i++) n[i] = F(normalFog, i);
            FogFalloff = n[5]; FogFalloffX = n[6]; FogFalloffY = n[7]; DistFogStart = n[1]; DistFogPower = n[0];
            DistFogColour = Pack(n[2], n[3], n[4]);
            float w = precip != null ? (precip.Rain != 0f ? precip.Rain : precip.Cloud * (1f / 3f)) : 0f;   // [0x894C30] = 0.33333334
            if (w <= 0f) return true;
            float blendDist = 50f;
            if (fogBlendDist != null && fogBlendDist.Layer != null) { fogBlendDist.CaptureData(camX, camY); blendDist = fogBlendDist.GetDataFloat(0); }
            if (rainFog == null || rainFog.Layer == null) return true;
            rainFog.CaptureByDistance(camX, camY, blendDist);
            var r = new float[8];
            for (int i = 0; i < 8; i++) r[i] = F(rainFog, i);
            float k = 1f - w;                                           // 0x754169..0x754216: normal x (1 - w) + rainfog x w
            FogFalloff = FogFalloff * k + r[5] * w; FogFalloffX = FogFalloffX * k + r[6] * w; FogFalloffY = FogFalloffY * k + r[7] * w;
            DistFogStart = DistFogStart * k + r[1] * w; DistFogPower = DistFogPower * k + r[0] * w;
            int c2 = (int)(n[2] * k + r[2] * w), c3 = (int)(n[3] * k + r[3] * w), c4 = (int)(n[4] * k + r[4] * w);   // ftol truncates
            if (precip != null) { precip.FogR = c2; precip.FogG = c3; precip.FogB = c4; }   // SetPrecipFogColour 0x754235..0x75424F
            DistFogColour = Pack(c2, c3, c4);
            return true;
        }
    }

    /// <summary>
    /// The view's Rain object (World/rain.cpp; eView +0x68 "Precipitation"). Constructed per player view (0x749DC0) and
    /// set to INACTIVE by SetRainBase 0x749D70 / Init(INACTIVE, 1) at World_Init. Each frame eDisplayFrame (0x6DF545, game
    /// flow state 6, only while g_ParticleSystemEnable [0x9017EC] is set) calls <see cref="UpdateAndRender"/> 0x758100.
    /// Outputs: <see cref="Rain"/> (+0x28C), <see cref="Cloud"/> (+0x290, the lighting/sky cloud amount),
    /// <see cref="Wetness"/> (+0x36A0, = SpecularColour.w of the world effects, 0x6D6AA0), the 3D streaks
    /// (<see cref="Drops"/>), the tunnel-mouth curtain (<see cref="CurtainDrops"/>) and the on-screen drops
    /// (<see cref="OnScreen"/>, drawn with fx\rain_drop.efx by 0x6D4620).
    /// </summary>
    public sealed class Precipitation
    {
        public const int MaxPoints = 0x15E;          // 350 (Init 0x73C6DE)
        public const int CurtainPoints = 200;        // +0x3880, 0xC8 (0x758518)
        public const int Inactive = 1, RainType = 0;

        // ---- globals the exe keeps outside the object (BSS unless noted) ----
        public static int PrecipitationEnable = 1;   // [0x8F86E4] .data 1; ENISRain sets 1, NISActivity restores its copy
        public static int AlwaysRain;                // [0x9B0A30] 0
        public static float GeneralRainAmount = 1f;  // [0x904A14]
        public static float RoadReflection = 1f;     // [0x904B38] the wet value
        public static float RainOverrideIntensity;   // [0x9B0A48] SetOverRideRainIntensity 0x73C970 (ENISRain events)
        public static float CloudsMinAmount;         // twkCloudsMinAmount [0x9B0A3C] 0
        public static int PrecipRender = 1;          // precipRENDER [0x904AD0]
        public static int OnScreenOverride;          // OverRide [0x9B0A58] 0: freezes the on-screen drop timers
        static float _windAng;                       // windAng [0x9B0A50] += 45 dt, wraps at 360 (no reader on PC found)
        static int _gustState = 1;                   // [0x905CE0] .data 1
        static float _gustBlend;                     // [0x9B378C]
        static readonly ushort[] _lastHeading = new ushort[2];   // [0x9B37A8] per view

        // ---- tweakables (.data 0x904A20..) ----
        const float RainX = 0.02f, RainY = 0.02f, RainZ = 0.03f, RainZConstant = 0.35f;
        const float BoundX = 20f, BoundY = 7f, BoundZ = 6f, AheadX = 8f, AheadY = 0f, AheadZ = 0f;   // AheadY/Z: BSS 0
        const float RainWindEffect = 0.3f, RadiusX = 0.01f, RadiusY = 0.45f, DriveFactor = -0.03f;
        const float WetRamp = 0.005f, DryCut = 0.05f, TunnelHeight = 10f;   // [0x8B2F60], [0x8B2E60], TUNHEIGHT [0x9031F0]

        readonly Random _rng;
        float Rand(float r) => (float)(_rng.NextDouble() * r);   // bRandom(float) 0x45DA70 [ours: System.Random]

        // ---- state ----
        public float Rain;              // +0x28C intensity
        public float Cloud;             // +0x290
        public int DesiredRainCount;    // +0x294 = 350 - inactive
        public float RainTarget;        // +0x3694
        public float CloudTarget;       // +0x3698
        public float DesiredWetness;    // +0x369C
        public float Wetness;           // +0x36A0 RoadDampness
        public bool InTunnel, InOverpass;   // +0x23C / +0x240
        public bool NoRain;             // +0x234 (in a tunnel or under an overpass: every drop goes inactive)
        public TrackPathZone Zone;      // +0x244 the covering zone found by Update
        public int CurtainState;        // +0x280 (CurtainStatus)
        public int NumActive;           // +0x284 drops falling (status 0)
        public float Speed;             // +0x3690 |camera drift| clamped to 1
        public Vec3 CamLocal;           // +0x270 camera drift per frame in rain space

        public readonly int[] NumOfType = new int[2], Desired = new int[2];   // +0x33DC, +0x33E4
        public readonly float[] Percent = new float[2];                      // +0x33EC
        readonly float[] _windEffect = new float[4];                         // +0x33F4 [type*2 + subType]
        readonly Vec3[] _radius = new Vec3[2], _speedRange = new Vec3[2];    // +0x3420, +0x3440
        readonly float[] _zConstant = new float[2];                          // +0x3460
        readonly Vec3[] _velocities = new Vec3[20];                          // +0x36C8 [type*10 + i%10]
        public Vec3 Wind;               // +0x3480 windSpeed (rain space)
        Vec3 _windTarget;               // +0x3490
        float _gustPeriod, _gustTimer;  // +0x34A0, +0x34A4
        Vec3 _windBase;                 // +0x36B0
        float _maxX, _maxY, _minX, _minY;   // +0x34B4 .. +0x34C0
        int _new, _old = 1;             // +0x298 / +0x29C buffer selectors

        // rain space: +0x3610 local2world (rotation about Z by the heading, at the anchor), +0x3650 world2local
        public ushort Heading;
        public Vec3 Origin;
        Vec3 _w2lX = new Vec3(1, 0, 0), _w2lY = new Vec3(0, 1, 0), _w2lZ = new Vec3(0, 0, 1);

        public readonly Vec3[][] Points = { new Vec3[MaxPoints], new Vec3[MaxPoints] };
        public readonly byte[] Status = new byte[MaxPoints];   // 0 falling, 1 hit the ground, 2 new
        public readonly byte[] Type = new byte[MaxPoints], SubType = new byte[MaxPoints];
        public readonly Vec3[][] Curtain = { new Vec3[CurtainPoints], new Vec3[CurtainPoints] };
        public Vec3 CurtainP0, CurtainP1, CurtainP2, CurtainP3;   // +0x3830..+0x3860 (top a, top b, bottom a, bottom b)
        float _curtainLength;                                     // +0x3870
        float _segAx, _segAy, _segBx, _segBy, _segCx, _segCy, _segDx, _segDy;   // +0x3808..+0x3824
        Vec3 _curtainNormal;                                      // +0x250

        public readonly OnScreenRain OnScreen;

        /// <summary>A drawable streak: centre and the two half axes, plus its u column (in game space for the curtain,
        /// rain space for the drops).</summary>
        public struct Streak { public Vec3 C, A1, A2; public float U0, U1, V0, V1; }
        public readonly List<Streak> Drops = new List<Streak>(MaxPoints);
        public readonly List<Streak> CurtainDrops = new List<Streak>(CurtainPoints);

        /// <summary>
        /// Game_SetChanceOfRain 0x6054A0 (Lua Game.SetChanceOfRain; the `basicrace` initial state passes the race's
        /// ChanceOfRain, 0.5 by default, 0 on the D-Day events): clamped to 0..1; under 0.01 the override becomes -1 (no
        /// rain at all), otherwise 0 (the Rain map decides). [hook: the Lua binding should call this]
        /// </summary>
        public static void SetChanceOfRain(float chance)
        {
            float c = chance >= 1f ? 1f : chance;
            if (0f > c) c = 0f;
            RainOverrideIntensity = c < 0.01f ? -1f : 0f;
        }

        /// <summary>ENISRain event (0x6233D0): PrecipitationEnable = 1 and the override = the event's intensity.
        /// NISActivity::Release (0x6F75F7) puts back the PrecipitationEnable it saved at start and sets the override to 0.
        /// [hook: the NIS player should call these]</summary>
        public static void NisRain(float intensity) { PrecipitationEnable = 1; RainOverrideIntensity = intensity; }
        public static void NisReleased(int savedPrecipitationEnable) { PrecipitationEnable = savedPrecipitationEnable; RainOverrideIntensity = 0f; }

        public Precipitation(int seed = 0)
        {
            _rng = seed == 0 ? new Random() : new Random(seed);
            OnScreen = new OnScreenRain(_rng);
            Cloud = CloudsMinAmount;                 // ctor 0x749DF3
            Init(Inactive, 1f);                      // SetRainBase 0x749D70 (World_Init 0x733A83)
        }

        /// <summary>SetRainBase 0x749D70 (World_Init 0x733A83): dry road, every drop inactive. Rain and cloud carry over
        /// (the Rain objects are built once, by epInitViews 0x6C074D).</summary>
        public void SetRainBase()
        {
            Wetness = 0f;
            Init(Inactive, 1f);
        }

        /// <summary>Rain::Init 0x73C6B0.</summary>
        public void Init(int type, float percent)
        {
            NumOfType[0] = NumOfType[1] = Desired[0] = Desired[1] = 0;
            _new = 0; _old = 1;
            NumOfType[type] = MaxPoints;
            _windEffect[0] = RainWindEffect; _windEffect[1] = RainWindEffect * 0.5f; _windEffect[2] = 1f; _windEffect[3] = 1f;
            _radius[0] = new Vec3(RadiusX, RadiusY, 0.05f);
            _speedRange[0] = new Vec3(RainX, RainY, RainZ);
            _zConstant[0] = RainZConstant;
            Wind = default;
            _gustTimer = 0f;
            for (int i = 0; i < MaxPoints; i++)
            {
                Status[i] = 2;
                Type[i] = (byte)type;
                SubType[i] = (byte)_rng.Next(2);
                var p = new Vec3(AheadX + (Rand(BoundX) - BoundX * 0.5f), AheadY + (Rand(BoundY) - BoundY * 0.5f), AheadZ + Rand(BoundZ));
                Points[_new][i] = p;
                Points[_old][i] = p;                 // PC copies new -> old (0x73C88D)
            }
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 10; i++)
                {
                    var r = _speedRange[j];
                    _velocities[j * 10 + i] = new Vec3(Rand(r.X) - r.X * 0.5f, Rand(r.Y) - r.Y * 0.5f, -Rand(r.Z) - _zConstant[j]);
                }
        }

        /// <summary>Rain::SetRainIntensity 0x73CC80: the inactive share is ftol((1 - rain) x 350).</summary>
        public void SetRainIntensity(float percent)
        {
            Rain = AlwaysRain != 0 ? GeneralRainAmount : percent;
            int inactive = (int)((1f - Rain) * 350f);
            Desired[1] = inactive;
            DesiredRainCount = MaxPoints - inactive;
            Percent[1] = 1f - Percent[0];
        }

        /// <summary>
        /// Rain::UpdateAndRender 0x758100 (game flow state 6). Paused: only the precip-fog tint is offered to the screen
        /// effects and the drops are re-drawn. Otherwise: on-screen drops, windAng, the wetness ramp, the rain / cloud
        /// targets from the parameter maps (when both layers are loaded), their easing, SetRainIntensity, Update, Render and
        /// the tunnel-mouth curtain.
        /// </summary>
        public void UpdateAndRender(in PrecipitationInput f, ParameterMapLayer rainLayer, ParameterMapLayer cloudLayer,
            ParameterAccessorBlendByDistance wind, ScreenEffectDB effects)
        {
            if (f.Paused)
            {
                // 0x758135: SE_TINT {fog r, g, b (+0x34A8..), 128, cloud (x8 in split screen while raining)}, lock, SEC_FRAME
                var def = new ScreenEffectDef { R = FogR, G = FogG, B = FogB, A = 128f, Intensity = f.SplitScreen && Rain > 0f ? Cloud * 8f : Cloud };
                effects?.AddScreenEffect(ScreenEffectType.Tint, def, true, ScreenEffectControl.Frame);
                Render(f);
                return;
            }
            OnScreen.Update(f, this);
            _windAng += 45f * f.Dt;
            if (_windAng > 360f) _windAng = 0f;
            if (PrecipitationEnable == 0)
            {
                // 0x758235: AmIinATunnelSlow(view, 1) for the flags, no rain, dry road (the cloud keeps its value)
                InOverpass = false;
                int t = TunnelSlow(f);
                InTunnel = t != 0;
                if ((t & 2) != 0) { InOverpass = true; InTunnel = false; }
                Rain = 0f;
                Wetness = 0f;
                return;
            }
            if (AlwaysRain != 0) Wetness = RoadReflection;
            else DesiredWetness = Rain != 0f ? RoadReflection : 0f;   // (the state != 6 branch, = 1, is unreachable here)
            // 0x7582D7: per frame, not per second
            float next = Wetness + (DesiredWetness - Wetness) * WetRamp;
            if (DesiredWetness > Wetness) Wetness = next;
            else Wetness = next < DryCut ? 0f : next;

            if (rainLayer != null && cloudLayer != null)
            {
                float x = f.CamPos.X, y = f.CamPos.Y;
                RainTarget = RainMath.DesiredRain(rainLayer, x, y, f.WorldSeconds, RainOverrideIntensity);
                CloudTarget = RainTarget > 0f ? RainTarget : RainMath.DesiredCloud(cloudLayer, x, y, f.WorldSeconds, CloudsMinAmount);
                if (f.SplitScreen) RainTarget *= 0.125f;
            }
            float dt = f.Dt == 0f ? 0.001f : f.Dt;
            float inv = 1f / dt;
            Rain += (RainTarget - Rain) / (inv * 1f);                // [0x904AC4] twkRainRateOfChange 1
            if (RainTarget == 0f && Rain < 0.01f) Rain = 0f;
            if (Rain > 1f) Rain = 1f;
            Cloud += (CloudTarget - Cloud) / (inv * 10f);            // [0x904AC8] 10
            if (CloudTarget == 0f && Cloud < CloudsMinAmount + 0.01f) Cloud = CloudsMinAmount;
            if (Cloud > 1f) Cloud = 1f;

            SetRainIntensity(Rain);
            Update(f, wind);
            Render(f);

            // 0x7584C3: the curtain at a tunnel / overpass mouth while covered
            if (InTunnel || InOverpass)
            {
                switch (CurtainState)
                {
                    case 0:
                        CurtainState = 2;
                        FindCurtain(f);
                        ChooseCurtain(f);
                        for (int i = 0; i < CurtainPoints; i++) SeedCurtain(i, true);
                        UpdateCurtain(f);
                        break;
                    case 1:
                    case 2:
                        CurtainState = 1;
                        ChooseCurtain(f);
                        UpdateCurtain(f);
                        break;
                    case 3:
                        CurtainState = 1;
                        UpdateCurtain(f);
                        break;
                    default:
                        UpdateCurtain(f);
                        break;
                }
            }
            else CurtainState = 0;
        }

        /// <summary>The precipitation fog colour (+0x34A8..+0x34B0, ints): zeroed by the ctor (0x749EFC), written by
        /// FogQuery::CalculateFogInfo (0x754235, Rain::SetPrecipFogColour) when (rain or cloud / 3) > 0 and a Rain Fog layer
        /// exists. Its only consumers (DoTinting, the paused SE_TINT) build an effect PC never draws.</summary>
        public float FogR, FogG, FogB;

        /// <summary>AmIinATunnelSlow 0x74B000: bit 0 = a tunnel zone above the anchor, bit 1 = an overpass / small overpass /
        /// garage zone above it.</summary>
        static int TunnelSlow(in PrecipitationInput f)
        {
            if (!f.HasCar || f.FindZone == null) return 0;
            float z = f.CarPos.Z;
            int r = 0;
            var t = f.FindZone(3, f.CarPos.X, f.CarPos.Y);
            if (t != null && z < t.Elevation) r = 1;
            foreach (int type in new[] { 4, 5, 7 })
            {
                var o = f.FindZone(type, f.CarPos.X, f.CarPos.Y);
                if (o != null && z < o.Elevation) return r + 2;
            }
            return r;
        }

        static Vec3 Add(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        static Vec3 Sub(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        static Vec3 Mul(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        static float Len(Vec3 a) => (float)Math.Sqrt(Dot(a, a));
        static Vec3 Norm(Vec3 a) { float l = Len(a); return l > 0f ? Mul(a, 1f / l) : a; }
        Vec3 ToLocal(Vec3 v) => new Vec3(Dot(v, _w2lX), Dot(v, _w2lY), Dot(v, _w2lZ));

        /// <summary>Rain-space point to game space (the +0x3610 matrix: rotation about Z by <see cref="Heading"/>, then Origin).</summary>
        public Vec3 LocalToWorld(Vec3 p)
        {
            float c = RainMath.BCos(Heading), s = RainMath.BSin(Heading);
            return new Vec3(Origin.X + p.X * c - p.Y * s, Origin.Y + p.X * s + p.Y * c, Origin.Z + p.Z);
        }

        /// <summary>Rain::Update 0x757770 (state 6 only, [0x9B085C] = 0).</summary>
        void Update(in PrecipitationInput f, ParameterAccessorBlendByDistance wind)
        {
            // frame factor: 1 - (1 - min(30 dt, 1))^3 (the motion is per frame, scaled down above 30 fps)
            float k = Math.Min(f.Dt * 30f, 1f);
            k = 1f - k;
            k = 1f - k * k * k;

            // anchor: the camera mover's car (its matrix and position) or the camera (+1 m along its direction)
            Vec3 pos, dir;
            if (f.HasCar)
            {
                pos = f.CarPos;
                dir = f.CarFwd;
                _w2lX = f.CarFwd; _w2lY = f.CarLeft; _w2lZ = f.CarUp;   // fInvertMatrix(+0x3650, car matrix) [rotation only]
                // [?] 0x757865: when the anchor's vfunc +0x30 returns non-zero, pos += dir x 10 (not identified, not ported)
            }
            else
            {
                dir = f.CamDir;
                pos = Add(f.CamPos, Mul(f.CamDir, 1f));                // [0x904AE0] 1
                // [?] the exe inverts the camera matrix here; the heading frame stands in for it [ours]
                float h = (float)Math.Atan2(dir.Y, dir.X);
                _w2lX = new Vec3((float)Math.Cos(h), (float)Math.Sin(h), 0); _w2lY = new Vec3(-(float)Math.Sin(h), (float)Math.Cos(h), 0); _w2lZ = new Vec3(0, 0, 1);
            }
            Origin = pos;

            // covering zones (0x757928): tunnel (3) -> InTunnel; overpass (4), small overpass (5), garage (7) -> InOverpass;
            // each must be above the anchor and lower than the previous pick
            InTunnel = InOverpass = false;
            float lowest = 9999f;
            if (f.FindZone != null)
            {
                var z = f.FindZone(3, pos.X, pos.Y);
                if (z != null && pos.Z < z.Elevation) { lowest = z.Elevation; InTunnel = true; Zone = z; }
                foreach (int type in new[] { 4, 5, 7 })
                {
                    z = f.FindZone(type, pos.X, pos.Y);
                    if (z != null && pos.Z < z.Elevation && z.Elevation < lowest)
                    {
                        if (type != 7) lowest = z.Elevation;
                        Zone = z;
                        InOverpass = true;
                    }
                }
            }

            UpdateWind(f, wind);
            Desired[0] = (int)(DesiredRainCount * 1f);                 // sub_73CCF0(0, [0x904AD8] = 1)
            Percent[0] = 1f;

            if (f.HasCar)
            {
                var v = Mul(ToLocal(f.CamVelocity), DriveFactor);
                v.Y = 0f;
                Speed = Len(v);
                if (Speed > 1f) { Speed = 1f; v = Norm(v); }
                CamLocal = v;
            }
            else CamLocal = default;

            // heading (0x757B04): 0x4000 - bASin(normalize(dir).x); for dir.y < 0 it is 0xFBB4 - that (the exe's constant)
            var nd = Norm(dir);
            int ang = 0x4000 - AsinAng(nd.X);
            if (nd.Y < 0f) ang = 0xFBB4 - ang;
            Heading = unchecked((ushort)ang);

            // yaw change since the last frame, wrapped to +-32768 and clamped to +-400 bAngle units
            int vi = f.ViewIndex == 0 ? 0 : 1;
            float yaw = unchecked((ushort)(_lastHeading[vi] - Heading));
            _lastHeading[vi] = Heading;
            if (yaw >= 32768f) yaw -= 65536f;
            if (yaw > 400f) yaw = 400f; else if (yaw < -400f) yaw = -400f;

            _maxX = BoundX * 0.5f + AheadX; _minX = BoundX * -0.5f + AheadX;
            _maxY = BoundY * 0.5f + AheadY; _minY = BoundY * -0.5f + AheadY;
            int tmp = _old; _old = _new; _new = tmp;

            int rem = MaxPoints - (Desired[0] + Desired[1]);
            if (rem != 0)
                for (int j = 0; j < 2; j++)
                    if ((uint)Desired[j] >= (uint)rem) { Desired[j] -= rem; break; }
            bool steady = NumOfType[0] == Desired[0] && NumOfType[1] == Desired[1];

            NoRain = InTunnel || InOverpass;
            NumActive = 0;
            var cur = Points[_new];
            var prev = Points[_old];
            for (int i = 0; i < MaxPoints; i++)
            {
                int type = Type[i], sub = SubType[i];
                if (Status[i] == 0)
                {
                    NumActive++;
                    prev[i].Y += yaw * 5e-5f * prev[i].X;
                    float w = _windEffect[type * 2 + sub];
                    var vel = _velocities[type * 10 + i % 10];
                    float nx = CamLocal.X + prev[i].X + (w * Wind.X + vel.X) * k;
                    float ny = prev[i].Y + CamLocal.Y + (w * Wind.Y + vel.Y) * k;
                    float nz = CamLocal.Z + prev[i].Z + (w * Wind.Z + vel.Z) * k;
                    cur[i] = new Vec3(nx, ny, nz);
                    if (nz < 0f) { Status[i] = 1; NumActive--; }
                    else if (nx > _maxX) { cur[i].X -= BoundX; prev[i].X -= BoundX; }
                    else if (nx < _minX) { cur[i].X += BoundX; prev[i].X += BoundX; }
                    else if (ny > _maxY) { cur[i].Y -= BoundY; prev[i].Y -= BoundY; }
                    else if (ny < _minY) { cur[i].Y += BoundY; prev[i].Y += BoundY; }
                }
                else if (Status[i] <= 2)
                {
                    if (!steady)
                        for (int u = 0; u < 2; u++)
                            if (u != type && NumOfType[u] < Desired[u])
                            {
                                NumOfType[type]--; Type[i] = (byte)u; NumOfType[u]++;
                                break;
                            }
                    if (Type[i] == Inactive)
                    {
                        if (Status[i] == 1) { cur[i].Z = Rand(BoundZ) + AheadZ; Status[i] = 2; }
                    }
                    else
                    {
                        if (Status[i] == 1) { cur[i].Z += BoundZ; prev[i].Z += BoundZ; }
                        Status[i] = 0;
                        NumActive++;
                    }
                }
                if (NoRain)
                {
                    if (Status[i] == 0) NumActive--;
                    NumOfType[Type[i]]--;
                    Type[i] = Inactive;
                    NumOfType[Inactive]++;
                    Status[i] = 2;
                }
            }
        }

        static int AsinAng(float x)
        {
            if (x > 1f) x = 1f; else if (x < -1f) x = -1f;
            return unchecked((ushort)(int)(Math.Asin(x) * (65536.0 / (2.0 * Math.PI))));   // bASin 0x45DD10
        }

        /// <summary>
        /// sub_753B50 (Rain::Wind): the "Wind" layer's field 1 (blended by distance, 50 m) is an angle in degrees ->
        /// (sin, cos, 0) x 0.01 in rain space. Gusts: wait rand(5)+1 s, then ease toward base + (rand(0.05)-0.025) x 25
        /// on x/y over one second.
        /// </summary>
        void UpdateWind(in PrecipitationInput f, ParameterAccessorBlendByDistance wind)
        {
            if (wind != null && wind.Layer != null)
            {
                wind.CaptureByDistance(f.CamPos.X, f.CamPos.Y, 50f);   // 0x753B94 -> ParameterAccessorBlendByDistance::CaptureData(x, y, full_blend_distance 50)
                ushort a = RainMath.DegToAng(wind.GetDataFloat(1));
                _windBase = new Vec3(RainMath.BSin(a), RainMath.BCos(a), 0f);
            }
            _windBase = ToLocal(Mul(_windBase, 0.01f));                // [0x904AF4]; eMulVector(+0x3650) [rotation only]
            float dt = f.Dt;
            if (_gustState == 0)
            {
                _gustTimer += dt;
                if (_gustTimer >= _gustPeriod) _gustState = 1;
            }
            else if (_gustState == 1)
            {
                _gustTimer = 0f;
                _gustPeriod = Rand(5f) + 1f;
                _gustState = 2;
                _windTarget = new Vec3((Rand(0.05f) - 0.025f) * 25f + _windBase.X, (Rand(0.05f) - 0.025f) * 25f + _windBase.Y, _windBase.Z);
            }
            else if (_gustState == 2)
            {
                Wind.X += (_windTarget.X - Wind.X) * dt;
                Wind.Y += (_windTarget.Y - Wind.Y) * dt;
                _gustBlend += dt;
                if (_gustBlend >= 1f) { _gustBlend = 0f; _gustState = 0; }
            }
        }

        /// <summary>
        /// Rain::Render 0x74ACC0: every type-0 falling drop as a quad in rain space (texture RAINDROP, Particles effect):
        /// centre = this frame's point, d = normalize(last - this), half width = cross(camera dir in rain space, d) x
        /// RAINRadiusX (0.01), half length = d x (RAINRadiusY 0.45 + Speed); u column (i % 10) / 10 of the 10-drop strip
        /// (u0 = a + 0.1 .. u1 = a, v 1 .. 0); vertex colour 0x80808080.
        /// </summary>
        void Render(in PrecipitationInput f)
        {
            Drops.Clear();
            if (PrecipitationEnable == 0 || PrecipRender == 0 || NumActive == 0) return;
            var camLocal = ToLocal(f.CamDir);
            var cur = Points[_new];
            var prev = Points[_old];
            for (int i = 0; i < MaxPoints; i++)
            {
                if (Type[i] != 0 || Status[i] != 0) continue;
                var d = Norm(Sub(prev[i], cur[i]));
                var a1 = Mul(Cross(camLocal, d), _radius[0].X);
                var a2 = Mul(d, _radius[0].Y + Speed);
                float a = (i % 10) * 0.1f;
                Drops.Add(new Streak { C = cur[i], A1 = a1, A2 = a2, U0 = a + 0.1f, U1 = a, V0 = 1f, V1 = 0f });
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // the rain curtain at tunnel / overpass mouths (0x74A070, 0x74A160, 0x74A320, 0x74A490, 0x74A5D0, 0x749FB0)

        /// <summary>sub_74A070: the zone edge next to the point 4 m behind the camera, and the far mouth (sub_722E90).</summary>
        void FindCurtain(in PrecipitationInput f)
        {
            if (!f.HasCar || Zone == null) return;
            float px = f.CamPos.X - f.CamDir.X * 4f, py = f.CamPos.Y - f.CamDir.Y * 4f;
            ZoneSegments.GetSegmentNextTo(Zone, px, py, out _segAx, out _segAy, out _segBx, out _segBy);
            ZoneSegments.OppositeSegment(Zone, _segAx, _segAy, _segBx, _segBy, out _segCx, out _segCy, out _segDx, out _segDy);
        }

        /// <summary>sub_74A160: the mouth whose centre is ahead of the camera (2D) gets the curtain, from the car's
        /// height to TUNHEIGHT (10 m) above it.</summary>
        void ChooseCurtain(in PrecipitationInput f)
        {
            if (!f.HasCar) return;
            float cdx = f.CamDir.X, cdy = f.CamDir.Y, cl = (float)Math.Sqrt(cdx * cdx + cdy * cdy);
            if (cl > 0f) { cdx /= cl; cdy /= cl; }
            float mx = (_segAx + _segBx) * 0.5f - f.CarPos.X, my = (_segBy + _segAy) * 0.5f - f.CarPos.Y, ml = (float)Math.Sqrt(mx * mx + my * my);
            if (ml > 0f) { mx /= ml; my /= ml; }
            float z = f.CarPos.Z;
            if (mx * cdx + cdy * my <= 0f)
                AttachRainCurtain(_segCx, _segCy, TunnelHeight + z, _segDx, _segDy, TunnelHeight + z, _segCx, _segCy, z, _segDx, _segDy, z);
            else
                AttachRainCurtain(_segAx, _segAy, TunnelHeight + z, _segBx, _segBy, TunnelHeight + z, _segAx, _segAy, z, _segBx, _segBy, z);
        }

        /// <summary>AttachRainCurtain 0x749FB0 (also called by DoTunnelBloom with the glare quad; that sets state 3).</summary>
        public void AttachRainCurtain(float x0, float y0, float z0, float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3)
        {
            CurtainP0 = new Vec3(x0, y0, z0); CurtainP1 = new Vec3(x1, y1, z1);
            CurtainP2 = new Vec3(x2, y2, z2); CurtainP3 = new Vec3(x3, y3, z3);
            _curtainLength = Len(Sub(CurtainP1, CurtainP0));
        }

        /// <summary>sub_74A320 (anywhere in the curtain, falling from rand(10) below the top) / sub_74A490 (at the top).</summary>
        void SeedCurtain(int i, bool anyHeight)
        {
            float along = Rand(_curtainLength);
            float drop = anyHeight ? Rand(TunnelHeight) : 0f;
            float depth = Rand(10f);
            var d = Norm(Sub(CurtainP1, CurtainP0));
            var p = new Vec3(depth * _curtainNormal.X + d.X * along + CurtainP0.X,
                             d.Y * along + CurtainP0.Y + depth * _curtainNormal.Y,
                             d.Z * along + CurtainP0.Z + depth * _curtainNormal.Z - drop);
            Curtain[_new][i] = p;
            Curtain[_old][i] = p;
        }

        /// <summary>
        /// Rain::UpdateAndRenderCurtain 0x74A5D0, zones of at most 5 points: ftol(rain x 200) points in game space fall by the
        /// type-0 velocities plus 0.15 x wind and the camera drift; outside [bottom, top] they restart at the top. Only when
        /// the point 5 m outward from the mouth is not inside another tunnel zone. Quads like Rain::Render but 2 x RadiusX
        /// wide, texture RAINDROP, UVs (0,1) (0.1,1) (0.1,0) (0,0).
        /// </summary>
        void UpdateCurtain(in PrecipitationInput f)
        {
            CurtainDrops.Clear();
            if (Zone == null || (Zone.Xs?.Length ?? 0) > 5) return;
            int n = (int)(Rain * 200f);
            // the camera drift back to game space (+0x3610 without translation)
            float c = RainMath.BCos(Heading), s = RainMath.BSin(Heading);
            var drift = new Vec3(CamLocal.X * c - CamLocal.Y * s, CamLocal.X * s + CamLocal.Y * c, CamLocal.Z);
            float mx = (CurtainP1.X + CurtainP0.X) * 0.5f, my = (CurtainP1.Y + CurtainP0.Y) * 0.5f;
            float nx = mx - (Zone.MinX + Zone.MaxX) * 0.5f, ny = my - (Zone.MaxY + Zone.MinY) * 0.5f, nl = (float)Math.Sqrt(nx * nx + ny * ny);
            if (nl > 0f) { nx /= nl; ny /= nl; }
            _curtainNormal = new Vec3(nx, ny, 0f);
            var outside = f.FindZone?.Invoke(3, nx * 5f + mx, ny * 5f + my);
            if (!(outside == null || outside == Zone) || n <= 0) return;
            if (n > CurtainPoints) n = CurtainPoints;
            var cur = Curtain[_new];
            var prev = Curtain[_old];
            float w = _windEffect[1];
            for (int i = 0; i < n; i++)
            {
                var vel = _velocities[i % 10];
                var p = new Vec3(drift.X + prev[i].X + w * Wind.X + vel.X, drift.Y + prev[i].Y + w * Wind.Y + vel.Y, drift.Z + prev[i].Z + w * Wind.Z + vel.Z);
                cur[i] = p;
                if (p.Z < CurtainP3.Z || CurtainP0.Z < p.Z) { SeedCurtain(i, false); continue; }
                var d = Norm(Sub(prev[i], p));
                var a1 = Mul(Cross(f.CamDir, d), _radius[0].X + _radius[0].X);
                var a2 = Mul(d, Speed + _radius[0].Y);
                CurtainDrops.Add(new Streak { C = p, A1 = a1, A2 = a2, U0 = 0f, U1 = 0.1f, V0 = 1f, V1 = 0f });
            }
        }
    }

    /// <summary>
    /// OnScreenRain (Rain +0, 20 drops of 0x1C: x, y, timer, Maxtimer, Size, DripSpeed, DripShape). Ctor 0x73CD30,
    /// Update 0x73CDB0, GetData 0x73CFA0. Drawn by 0x6D4620 with fx\rain_drop.efx (see Shaders/RainDrop.shader).
    /// </summary>
    public sealed class OnScreenRain
    {
        public const int Max = 20;
        public int NumOnScreen;
        public readonly float[] X = new float[Max], Y = new float[Max], Timer = new float[Max], MaxTimer = new float[Max],
            Size = new float[Max], DripSpeed = new float[Max];
        public readonly int[] DripShape = new int[Max];
        readonly Random _rng;

        public OnScreenRain(Random rng)
        {
            _rng = rng;
            for (int i = 0; i < Max; i++)
            {
                MaxTimer[i] = (float)(_rng.NextDouble() * 5.0) + 0.3f;      // bRandom(5) + [0x894B58] 0.3
                DripSpeed[i] = (float)(_rng.NextDouble() * 0.1) + 0.02f;     // bRandom(0.1) + 0.02 (double 0x8B4AA0)
                Size[i] = (float)_rng.NextDouble();                         // bRandom(1)
                DripShape[i] = i & 3;
            }
        }

        /// <summary>
        /// 0x73CDB0: 20 drops (10 in split screen) while the view is raining and not under cover, else none. A live drop
        /// counts its timer down, runs down the screen at 0.2 x DripSpeed per second, is pushed outward from (0.5, 0.1)
        /// by the camera speed x 0.0001 and dies past the edges; a dead one restarts at a random spot with its full timer.
        /// </summary>
        public void Update(in PrecipitationInput f, Precipitation p)
        {
            float dt = f.Dt;
            var v = f.CamVelocity;
            float speed = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z) * 0.0001f;   // [0x904B30]
            if (!p.NoRain) NumOnScreen = p.Rain == 0f ? 0 : (f.SplitScreen ? 10 : 20);
            else NumOnScreen = 0;
            for (int i = 0; i < NumOnScreen; i++)
            {
                if (Precipitation.OnScreenOverride == 0) Timer[i] -= dt;
                if (Timer[i] > 0f)
                {
                    Y[i] += 0.2f * DripSpeed[i] * dt;                      // [0x904B2C] DripSpeed 0.2
                    if (Y[i] > 1f || X[i] > 1f || X[i] < 0f) Timer[i] = 0f;
                    else
                    {
                        float dx = X[i] - 0.5f, dy = Y[i] - 0.1f, l = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (l > 0f) dx /= l;
                        X[i] += dx * speed;
                    }
                    if (speed > 0.0025f) DripShape[i] = (DripShape[i] + 1) & 3;   // [0x904B34] DropShapeSpeedChange
                }
                else
                {
                    Timer[i] = MaxTimer[i];
                    X[i] = (float)_rng.NextDouble();
                    Y[i] = (float)_rng.NextDouble();
                }
            }
        }

        /// <summary>GetData 0x73CFA0: x, y, decay = timer / Maxtimer, size.</summary>
        public void GetData(int i, out float x, out float y, out float decay, out float size)
        {
            x = X[i]; y = Y[i]; decay = MaxTimer[i] != 0f ? Timer[i] / MaxTimer[i] : 0f; size = Size[i];
        }
    }

    // ============================================================================================================
    // ScreenEffects.cpp

    public enum ScreenEffectType { Tint = 0, MotionBlur = 1, VisualSig = 2, Glare = 3, FeBlur = 4, Count = 5 }
    public enum ScreenEffectControl { Frame = 0, Boolean = 1, Function = 2 }

    /// <summary>ScreenEffectDef (0x50 bytes): colour, intensity and 14 data floats (the glare keeps its fade in data[1]
    /// and its quad in data[2..13]).</summary>
    public sealed class ScreenEffectDef
    {
        public float R, G, B, A, Intensity;
        public readonly float[] Data = new float[14];
        public ScreenEffectDef Clone()
        {
            var c = new ScreenEffectDef { R = R, G = G, B = B, A = A, Intensity = Intensity };
            Array.Copy(Data, c.Data, 14);
            return c;
        }
        public void CopyFrom(ScreenEffectDef o)
        {
            R = o.R; G = o.G; B = o.B; A = o.A; Intensity = o.Intensity;
            Array.Copy(o.Data, Data, 14);
        }
    }

    /// <summary>
    /// ScreenEffectDB (eView +0x64, 0x1E8 bytes): Update 0x7252A0, AddScreenEffect 0x7252F0 (lock / averaged), palettes
    /// 0x725410. The PC renderer reads only SE_TINT (sub_6D44B0, gated by [0x901804] which is never non-zero, so the tint
    /// is never drawn) and SE_GLARE (tunnel bloom: the alpha-only quad in eDisplayFrame 0x6DEFCE and the overbright blend
    /// factor in sub_6DC040 / sub_6D2CD0 / sub_6C3870). No UpdateFnc is ever set on PC [?].
    /// </summary>
    public sealed class ScreenEffectDB
    {
        public readonly bool[] Active = new bool[5];
        public readonly int[] FrameNum = new int[5], NumType = new int[5];
        public readonly ScreenEffectControl[] Controller = new ScreenEffectControl[5];
        public readonly ScreenEffectDef[] Data = { new ScreenEffectDef(), new ScreenEffectDef(), new ScreenEffectDef(), new ScreenEffectDef(), new ScreenEffectDef() };
        public float Time;

        public bool IsActive(ScreenEffectType t) => Active[(int)t];
        public float GetIntensity(ScreenEffectType t) => Data[(int)t].Intensity;
        public float GetData(ScreenEffectType t, int i) => Data[(int)t].Data[i];
        public void SetData(ScreenEffectType t, float v, int i) => Data[(int)t].Data[i] = v;

        /// <summary>0x7252A0: SEC_FRAME and SEC_FUNCTION effects last one update (UpdateAllScreenEFX passes 1/30 s).</summary>
        public void Update(float dt)
        {
            Time += dt;
            for (int i = 0; i < 5; i++)
            {
                if (!Active[i]) continue;
                FrameNum[i]++;
                if (Controller[i] == ScreenEffectControl.Frame || Controller[i] == ScreenEffectControl.Function)
                {
                    Active[i] = false;
                    NumType[i] = 0;
                }
            }
        }

        /// <summary>0x7252F0: lock replaces the def; otherwise averages colour and intensity with the running count.
        /// Below 0.01 intensity the effect is not active.</summary>
        public void AddScreenEffect(ScreenEffectType type, ScreenEffectDef def, bool locked, ScreenEffectControl controller)
        {
            int t = (int)type;
            var d = Data[t];
            if (locked)
            {
                if (def != null) d.CopyFrom(def);
                NumType[t] = 1;
            }
            else
            {
                NumType[t]++;
                float inf = NumType[t] / (float)(NumType[t] + 1), inv = 1f - inf;
                d.R = inv * def.R + inf * d.R; d.G = inv * def.G + inf * d.G; d.B = inv * def.B + inf * d.B;
                d.A = inv * def.A + inf * d.A; d.Intensity = inv * def.Intensity + inf * d.Intensity;
            }
            Active[t] = true;
            Controller[t] = controller;
            if (d.Intensity < 0.01f) Active[t] = false;
        }
    }
}
