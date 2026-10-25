using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    // Unity-free data and maths of speed.exe's car renderer (NFSMW_RE/specs/car_render.md). Decomp references are
    // refs/nfsmw/src/Speed/Indep/Src/World/<file>:<line>; exe addresses are speed.exe v1.3.

    /// <summary>
    /// eLightMaterial (Ecstasy/eLight.hpp:106-151): chunk BCHUNK_LIGHT_MATERIALS 0x00135200 in GLOBAL\GLOBALB, one per
    /// chunk {bTNode 8, plat 4, u32 NameHash @0xC, u32 Version @0x10 (3), char Name[28] @0x14, eLightMaterialData @0x30}.
    /// eLightMaterialData is 30 floats: DiffuseMin{Scale,R,G,B}, DiffuseMax{Scale,R,G,B}, DiffuseMinA, DiffuseMaxA,
    /// SpecularPower, SpecularMin{Scale,R,G,B}, SpecularMax{Scale,R,G,B}, EnvmapPower, EnvmapMin{Scale,R,G,B},
    /// EnvmapMax{Scale,R,G,B}, MetallicScale, SpecularHotSpot. 156 materials on PC (car paints, window tints, chrome…).
    /// </summary>
    public sealed class LightMaterial
    {
        public uint Hash;
        public string Name;
        public readonly float[] D = new float[30];

        public float DiffuseMinScale => D[0];
        public float DiffuseMaxScale => D[4];
        public float DiffuseMinA { get => D[8]; set => D[8] = value; }
        public float DiffuseMaxA { get => D[9]; set => D[9] = value; }
        public float SpecularPower => D[10];
        public float EnvmapPower => D[19];
        public float MetallicScale => D[28];

        /// <summary>The effect constants of one material (vs_1_1 of fx\car.efx c18..c25).</summary>
        public struct Constants
        {
            public float[] DiffuseMin, DiffuseRange, SpecularMin, SpecularRange, EnvmapMin, EnvmapRange; // float4 each
            public float SpecularPower, EnvmapPower;
        }

        /// <summary>
        /// The material setter sub_6C76E0 (shader path, effect type 4 = car, flag +0x40 set on a ps_2 card):
        /// DiffuseMin = MinScale·MinRGB, a = DiffuseMinA; DiffuseRange = MaxScale·MaxRGB − DiffuseMin, a =
        /// (DiffuseMaxA − DiffuseMinA)·0.25; Specular{Min,Range} likewise scaled by the time-of-day specular factor
        /// (*(DAT_009b392c+0x60), not decoded → 1 [?]); Envmap{Min,Range} likewise (×0.5 on the fixed-function path, ×1 on
        /// the shader path); SpecularPower and EnvmapPower ×1 (globals 0x9018E0 / 0x9018DC = 1.0).
        /// </summary>
        public Constants Shader(float specularScale = 1f, bool shaderPath = true)
        {
            var c = new Constants();
            c.DiffuseMin = new[] { D[1] * D[0], D[2] * D[0], D[3] * D[0], D[8] };
            c.DiffuseRange = new[] { D[5] * D[4] - c.DiffuseMin[0], D[6] * D[4] - c.DiffuseMin[1], D[7] * D[4] - c.DiffuseMin[2], (D[9] - D[8]) * 0.25f };
            float sMin = specularScale * D[11], sMax = specularScale * D[15];
            c.SpecularMin = new[] { sMin * D[12], sMin * D[13], sMin * D[14], 0f };
            c.SpecularRange = new[] { sMax * D[16] - c.SpecularMin[0], sMax * D[17] - c.SpecularMin[1], sMax * D[18] - c.SpecularMin[2], 0f };
            float f = shaderPath ? 1f : 0.5f;
            c.EnvmapMin = new[] { D[21] * D[20] * f, D[22] * D[20] * f, D[23] * D[20] * f, 0f };
            c.EnvmapRange = new[] { D[25] * D[24] * f - c.EnvmapMin[0], D[26] * D[24] * f - c.EnvmapMin[1], D[27] * D[24] * f - c.EnvmapMin[2], 0f };
            c.SpecularPower = D[10];
            c.EnvmapPower = D[19];
            return c;
        }

        public override string ToString() => $"{Name} {Hash:X8}";
    }

    public static class LightMaterials
    {
        public const uint ChunkId = 0x00135200;
        public static readonly uint Default = EaHash.Hash("DEFAULT"), CarSkin = EaHash.Hash("CARSKIN"), Windshield = EaHash.Hash("WINDSHIELD"),
            CarbonFiber = EaHash.Hash("CARBONFIBER"), MagSilver = EaHash.Hash("MAGSILVER"), MagChrome = EaHash.Hash("MAGCHROME"),
            MagGunmetal = EaHash.Hash("MAGGUNMETAL"), Caliper = EaHash.Hash("CALIPER"), Calliper = EaHash.Hash("CALLIPER");

        /// <summary>All version-3 light materials of a bundle, with the PC alpha overrides of UpdatePlatInfo applied.</summary>
        public static Dictionary<uint, LightMaterial> Read(byte[] d)
        {
            var map = new Dictionary<uint, LightMaterial>();
            foreach (var c in Chunks.FindAll(d, 0, d.Length, ChunkId))
            {
                int o = c.DataStart;
                if (c.Size < 0xA8 || Chunks.U32(d, o + 0x10) != 3) continue;
                var m = new LightMaterial { Hash = Chunks.U32(d, o + 0xC), Name = Chunks.CString(d, o + 0x14, 28) };
                for (int i = 0; i < 30; i++) m.D[i] = Chunks.F32(d, o + 0x30 + i * 4);
                UpdatePlatInfo(m);
                map[m.Hash] = m;
            }
            return map;
        }

        /// <summary>
        /// eLightMaterialPlatInterface::UpdatePlatInfo (0x6BE1B0): the PC build forces DiffuseMinA/MaxA of the glass
        /// materials: WINDSHIELD, WINDOWMASK, MOLDINGS (0x12C9453C) and the L1 black/green tints 0.3/1.0; the L2
        /// black/green/red/blue tints 0.4/1.0; every other L3 tint 0.5/1.0 (incl. L3 lavender/grape/black/green);
        /// CLEARPLASTIC and HEADLIGHTGLASS 0.4/0.8. Constants at 0x8F91E4..0x8F9208.
        /// </summary>
        public static void UpdatePlatInfo(LightMaterial m)
        {
            switch (m.Hash)
            {
                case 0x471A1DCA: case 0x3ED70C43: case 0x12C9453C: Set(m, 0.3f, 1f); break;            // WINDSHIELD, WINDOWMASK, MOLDINGS
                case 0x89946400: case 0x89F23A14: Set(m, 0.3f, 1f); break;                                // L1 BLACK, L1 GREEN
                case 0xD68EA0C1: case 0xD6EC76D5: case 0x8050CA3F: case 0x8A616C4C: Set(m, 0.4f, 1f); break; // L2 BLACK/GREEN/RED/BLUE
                case 0x33A26CB6: case 0xA6348EE3: Set(m, 0.4f, 0.8f); break;                              // CLEARPLASTIC, HEADLIGHTGLASS
                case 0x2388DD82: case 0x23E6A3F4: case 0x23E6B396: case 0x6CF41096: case 0x8062E2C0: case 0x8CB61DCD:
                case 0x8CB694ED: case 0x8CB95E6B: case 0x8CBC02EC: case 0x8CBE3497: case 0xB3645F81: case 0xC9D2B801:
                    Set(m, 0.5f, 1f); break;                                                              // L3 tints
            }
        }

        static void Set(LightMaterial m, float min, float max) { m.DiffuseMinA = min; m.DiffuseMaxA = max; }
    }

    /// <summary>The CarTypeInfo fields the renderer needs (World/CarInfo.hpp:386-482, chunk 0x34600, 0xD0 bytes).</summary>
    public sealed class CarTypeRenderInfo
    {
        public string Name, BaseModel;
        public int UsageType;      // +0x94 CarUsageType: 0 racing, 1 cop, 2 traffic [?: enum order from CarInfo.hpp:52]
        public bool Skinnable;     // +0xC7: composite skin (vinyls, base paint colour) — RideInfo::SetCompositeSkinNameHash
        public uint DefaultBasePaint; // +0xCC

        public static Dictionary<string, CarTypeRenderInfo> Read(byte[] d)
        {
            var map = new Dictionary<string, CarTypeRenderInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x00034600))
            {
                // same alignment handling as CarPartDb.ReadCarTypes
                int start = c.DataStart;
                while (start < c.End && Chunks.U32(d, start) == 0x11111111) start += 4;
                start = c.DataStart + ((start - c.DataStart) & ~3);
                if ((start & 15) != 0 && Chunks.U32(d, start) == 0) start = Chunks.Align(start, 16);
                for (int o = start; o + 0xD0 <= c.End; o += 0xD0)
                {
                    var t = new CarTypeRenderInfo
                    {
                        Name = Chunks.CString(d, o, 16), BaseModel = Chunks.CString(d, o + 0x10, 16), UsageType = Chunks.I32(d, o + 0x94),
                        Skinnable = d[o + 0xC7] != 0, DefaultBasePaint = Chunks.U32(d, o + 0xCC),
                    };
                    if (t.Name.Length > 0 && !map.ContainsKey(t.Name)) map[t.Name] = t;
                }
            }
            return map;
        }
    }

    /// <summary>
    /// The PC skin compositor (World/CarSkin.cpp; exe CompositeSkin32 0x73B010, CompositeWheel32 0x73B640,
    /// GetBlendColour 0x73AE60, ScaleColours 0x73AD90). Pixels are 32-bit BGRA words (b | g&lt;&lt;8 | r&lt;&lt;16 | a&lt;&lt;24),
    /// the layout of the DUMMY_SKINn / DUMMY_WHEELn targets (A8R8G8B8).
    /// </summary>
    public static class SkinComposite
    {
        /// <summary>A part colour (RED/GREEN/BLUE/GLOSS attributes) as a BGRA word, alpha = GLOSS (CompositeSkin 0x748AD0).</summary>
        public static uint Colour(int r, int g, int b, int gloss) => (uint)(b & 0xFF) | (uint)(g & 0xFF) << 8 | (uint)(r & 0xFF) << 16 | (uint)(gloss & 0xFF) << 24;

        /// <summary>A P8 or BGRA32 game texture's top mip as BGRA words; null for other formats.</summary>
        public static uint[] Pixels(GameTexture t)
        {
            if (t?.Data == null) return null;
            int n = t.Width * t.Height;
            var px = new uint[n];
            if (t.Format == TexFormat.P8 && t.Palette != null)
            {
                for (int i = 0; i < n && i < t.Data.Length; i++)
                {
                    int p = t.Data[i] * 4;
                    if (p + 3 < t.Palette.Length) px[i] = Chunks.U32(t.Palette, p);
                }
                return px;
            }
            if (t.Format == TexFormat.Bgra32 && t.Data.Length >= n * 4)
            {
                for (int i = 0; i < n; i++) px[i] = Chunks.U32(t.Data, i * 4);
                return px;
            }
            return null;
        }

        static int C(uint c, int shift) => (int)(c >> shift) & 0xFF;

        /// <summary>
        /// GetBlendColour (0x73AE60): Σ weight·channel per colour with weight &gt; 1/255 (each term truncated), alpha
        /// either the max term or the sum; channels clamp to 255.
        /// </summary>
        public static uint Blend(uint[] colours, float[] weights, int n, bool maxAlpha)
        {
            int r = 0, g = 0, b = 0, a = 0;
            for (int i = 0; i < n; i++)
            {
                float w = weights[i];
                if (w <= 1f / 255f) continue;
                uint c = colours[i];
                b += (int)(w * C(c, 0)); g += (int)(w * C(c, 8)); r += (int)(w * C(c, 16));
                int ta = (int)(w * C(c, 24)) & 0xFF;
                if (maxAlpha) { if (ta > a) a = ta; } else a += (int)(w * C(c, 24));
            }
            return (uint)Math.Min(b, 255) | (uint)Math.Min(g, 255) << 8 | (uint)Math.Min(r, 255) << 16 | (uint)Math.Min(a, 255) << 24;
        }

        /// <summary>
        /// CompositeSkin32 (0x73B010) with one vinyl layer: the target is filled with the base paint (alpha = GLOSS); a
        /// layer pixel whose mask (green channel of the _MASK texture) is 0 is skipped, below 0x80 it blends with weight
        /// mask/128, from 0x80 it replaces the pixel. A REMAP vinyl first turns its pixel into R·c0 + G·c1 + B·c2 of the
        /// three vinyl colours (max alpha). The FE swatch pixels (four magic colours) are not written [ours: no FE swatch].
        /// </summary>
        public static uint[] Skin(int w, int h, uint baseColour, uint[] layer, uint[] mask, bool remap, uint[] remapColours)
        {
            int n = w * h;
            var dst = new uint[n];
            for (int i = 0; i < n; i++) dst[i] = baseColour;
            if (layer == null || mask == null || layer.Length < n || mask.Length < n) return dst;
            var cols = new uint[2];
            var wts = new float[2];
            var rw = new float[4];
            for (int i = 0; i < n; i++)
            {
                uint src = layer[i];
                int m = C(mask[i], 8);
                if (remap && m != 0 && remapColours != null)
                {
                    rw[0] = C(src, 16) / 255f; rw[1] = C(src, 8) / 255f; rw[2] = C(src, 0) / 255f; rw[3] = 0;
                    src = Blend(remapColours, rw, 3, true);
                }
                if (m < 0x80)
                {
                    if (m == 0) continue;
                    float a = m / 128f;
                    if (a > 1f) a = 1f;
                    float b = 1f - a;
                    if (b < 0f) b = 0f;
                    cols[0] = src; cols[1] = dst[i]; wts[0] = a; wts[1] = b;
                    dst[i] = Blend(cols, wts, 2, false);
                }
                else dst[i] = src;
            }
            return dst;
        }

        /// <summary>ScaleColours (0x73AD90): per channel a·(b/255).</summary>
        public static uint Scale(uint a, uint b)
        {
            uint r = 0;
            for (int s = 0; s < 32; s += 8) r |= (uint)(int)(C(a, s) * (C(b, s) / 255f)) << s;
            return r;
        }

        /// <summary>
        /// CompositeWheel32 (0x73B640): dest = blend(src, src·colour) by the mask's red byte/255, keeping src alpha. The
        /// colour is the PAINT_RIM part (white when none is installed).
        /// </summary>
        public static uint[] Wheel(uint[] src, uint[] mask, uint colour)
        {
            if (src == null) return null;
            var dst = new uint[src.Length];
            var cols = new uint[2];
            var wts = new float[2];
            for (int i = 0; i < src.Length; i++)
            {
                float blend = mask != null && i < mask.Length ? C(mask[i], 16) / 255f : 0f;
                cols[0] = src[i]; cols[1] = Scale(src[i], colour);
                wts[0] = 1f - blend; wts[1] = blend;
                uint c = Blend(cols, wts, 2, false);
                dst[i] = (c & 0xFFFFFF) | (src[i] & 0xFF000000);
            }
            return dst;
        }
    }

    /// <summary>
    /// Damage zones (World/DamageZones.h): ten zones, a 3-bit level each packed in one word (Info.Set/Get), and the
    /// zone choice and levels of DamageVehicle::OnImpact (Physics/Behaviors/DamageVehicle.cpp:150-260, exe 0x69C030).
    /// </summary>
    public static class DamageZone
    {
        public const int Front = 0, Rear = 1, Left = 2, Right = 3, LFront = 4, RFront = 5, LRear = 6, RRear = 7, Top = 8, Bottom = 9, Max = 10;
        public static readonly string[] Names = { "DZ_FRONT", "DZ_REAR", "DZ_LEFT", "DZ_RIGHT", "DZ_LFRONT", "DZ_RFRONT", "DZ_LREAR", "DZ_RREAR", "DZ_TOP", "DZ_BOTTOM" };

        public static uint Set(uint info, int zone, int level) => (info & ~(7u << (zone * 3))) | ((uint)(level & 7) << (zone * 3));
        public static int Get(uint info, int zone) => (int)(info >> (zone * 3)) & 7;

        /// <summary>
        /// The zone of a contact: arm = contact point relative to the body centre and normal = contact normal pointing
        /// into the car, both in the sim's car space (x right, y up, z forward — bConvertToBond); dim = the rigid body's
        /// half extents. Thresholds kZoneThreshold 0.5 and kDirectionThreshold 0.8.
        /// </summary>
        public static int Classify(float ax, float ay, float az, float nx, float ny, float nz, float dx, float dy, float dz)
        {
            const float zt = 0.5f, dt = 0.8f;
            if (az > dz * zt)
            {
                if (ax > dx * zt && -dt > nx) return RFront;
                if (ax < -dx * zt && dt < nx) return LFront;
                return Front;
            }
            if (az < -dz * zt)
            {
                if (ax > dx * zt && -dt > nx) return RRear;
                if (ax < -dx * zt && dt < nx) return LRear;
                return Rear;
            }
            if (ay > dy * zt && ny < -dt) return Top;
            if (ay < -dy * zt && dt < ny) return Bottom;
            return ax > 0f ? Right : Left;
        }

        /// <summary>newdamage_level = min(2, (int)(force / (FORCE·1000) · VisualScale)); 0 when FORCE ≤ 0.</summary>
        public static int VisualLevel(float force, float materialForce, float visualScale)
        {
            float strength = materialForce * 1000f;
            if (strength <= 0f) return 0;
            return Math.Min(2, Math.Max(0, (int)(force / strength * visualScale)));
        }

        /// <summary>The impact stimulus count min(6, ratio·VisualScale) (event sequencer IMPACT_1..7) [not used for visuals].</summary>
        public static int ImpactLevel(float force, float materialForce, float visualScale) =>
            materialForce <= 0f ? 0 : Math.Min(6, Math.Max(0, (int)(force / (materialForce * 1000f) * visualScale)));
    }

    /// <summary>
    /// VehiclePartDamageBehaviour (World/VehiclePartDamage.cpp, exe ctor 0x75F0A0): the cop cars' damage. Each zone
    /// lists the CARSLOTID_DAMAGE_* slots it damages (mSlotZoneMapList); a damaged slot shows the next CarPartDB part
    /// of the slot (DAMAGE1_x, MaximumDamageLevel 1). Hood (10) and trunk (15) then swing on a damped spring.
    /// </summary>
    public static class VehiclePartDamageMap
    {
        public const int FrontWindow = 1, Body = 2, CopLights = 3, CopSpoiler = 4, FrontWheel = 5, LeftBrakelight = 6, RightBrakelight = 7,
            LeftHeadlight = 8, RightHeadlight = 9, Hood = 10, Bushguard = 11, FrontBumper = 12, RightDoor = 13, RightRearDoor = 14, Trunk = 15,
            RearBumper = 16, RearLeftWindow = 17, FrontLeftWindow = 18, FrontRightWindow = 19, RearRightWindow = 20, LeftDoor = 21, LeftRearDoor = 22;

        public static readonly string[] SlotNames =
        {
            null, "FRONT_WINDOW", "BODY", "COP_LIGHTS", "COP_SPOILER", "FRONT_WHEEL", "LEFT_BRAKELIGHT", "RIGHT_BRAKELIGHT", "LEFT_HEADLIGHT",
            "RIGHT_HEADLIGHT", "HOOD", "BUSHGUARD", "FRONT_BUMPER", "RIGHT_DOOR", "RIGHT_REAR_DOOR", "TRUNK", "REAR_BUMPER", "REAR_LEFT_WINDOW",
            "FRONT_LEFT_WINDOW", "FRONT_RIGHT_WINDOW", "REAR_RIGHT_WINDOW", "LEFT_DOOR", "LEFT_REAR_DOOR",
        };

        static readonly int[] Glass = { FrontWindow, RearLeftWindow, FrontLeftWindow, FrontRightWindow, RearRightWindow };
        static readonly int[] Lights = { LeftBrakelight, RightBrakelight, LeftHeadlight, RightHeadlight };

        /// <summary>mSlotZoneMapList (VehiclePartDamage.cpp:13-134), duplicates kept as in the table.</summary>
        public static int[] SlotsOf(int zone)
        {
            var l = new List<int>();
            void Add(params int[] s) => l.AddRange(s);
            switch (zone)
            {
                case DamageZone.Front: Add(FrontBumper, Bushguard, Body, Hood, CopLights, Body); Add(Lights); Add(FrontWindow, RearLeftWindow, FrontLeftWindow, FrontRightWindow, RearRightWindow); break;
                case DamageZone.Rear: Add(Trunk, RearBumper, Body); Add(Lights); Add(Glass); break;
                case DamageZone.Left: Add(LeftDoor, LeftRearDoor, FrontLeftWindow, RearLeftWindow, CopLights, Body); Add(Lights); Add(Glass); break;
                case DamageZone.Right: Add(RightDoor, RightRearDoor, Body, CopLights); Add(Lights); Add(Glass); break;
                case DamageZone.LFront: Add(Hood, Body); Add(Lights); Add(FrontLeftWindow, FrontWindow, RearLeftWindow, FrontLeftWindow, FrontRightWindow, RearRightWindow); break;
                case DamageZone.LRear: Add(Trunk, Body); Add(Lights); Add(RearLeftWindow, FrontWindow, RearLeftWindow, FrontLeftWindow, FrontRightWindow, RearRightWindow); break;
                case DamageZone.RFront: Add(Hood, Body); Add(Lights); Add(Glass); break;
                case DamageZone.RRear: Add(Trunk, Body); Add(Lights); Add(Glass); break;
                case DamageZone.Top: Add(Bushguard, CopLights, Hood, Body, Trunk, RightDoor, RightRearDoor, LeftDoor, LeftRearDoor); Add(Lights); Add(Glass); break;
            }
            return l.ToArray();
        }

        /// <summary>mBreakableWindowInfoList: window slot → the replacement-table entry that turns to WINDOW_DAMAGE0.</summary>
        public static readonly (int slot, string texture)[] BreakableWindows =
        {
            (FrontWindow, "WINDOW_FRONT"), (FrontLeftWindow, "WINDOW_LEFT_FRONT"), (RearLeftWindow, "WINDOW_LEFT_REAR"),
            (FrontRightWindow, "WINDOW_RIGHT_FRONT"), (RearRightWindow, "WINDOW_RIGHT_REAR"),
        };

        /// <summary>
        /// CalcPartRotation (VehiclePartDamage.cpp:356-412): a damped spring toward −min(up.z·0.25, 0) (radians,
        /// capped at maxAngle) pushed by the body's local acceleration, forward speed and yaw rate; bounces off 0
        /// (×−0.2) and maxAngle (×−0.5). Returns the new angle; the caller turns it into degrees with ×360 as the decomp
        /// does (angle units) [?].
        /// </summary>
        public static float Spring(ref float angle, ref float vel, float dT, float maxAngle, float spring, float damper, float inertia,
            float upZ, float accX, float velZ, float yawRate)
        {
            if (maxAngle <= 0f) { angle = 0f; return 0f; }
            float dest = -Math.Min(upZ * 0.25f, 0f);
            float acc = spring * (dest - angle) + damper * -vel;
            if (Math.Abs(accX) > 0.1f) acc -= accX / inertia;
            if (Math.Abs(velZ) > 0.35f) acc += -velZ * 3f / inertia;
            float yaw = yawRate / (float)(2 * Math.PI);
            if (Math.Abs(-yaw) > 0.01f) acc += -yaw * 36f;
            vel = acc * dT + vel;
            angle = vel * dT + angle;
            if (angle < 0f) { angle = 0f; vel = -vel * 0.2f; }
            else if (angle > maxAngle) { angle = maxAngle; vel = -vel * 0.5f; }
            return angle;
        }
    }

    /// <summary>
    /// The eShaperLightRig tables of speed.exe (0xA0 bytes: NameHash, eShaperLight[4] {int Mode, Theta, Phi, R, G, B,
    /// Scale}, position, NumOverideSlots). elSetupLights (0x502130) reads Lights[1..3] as shader lights 0..2 (Lights[0] is
    /// never read; the AddQuickDynamicLight slot index is the array index). Modes: 0 WORLD_SPACE (an absolute
    /// direction), 1 CAMERA_SPACE (relative to the rendering camera), 2 SUN / 3 OPPOSITE_SUN (relative to the
    /// time-of-day sun direction), 4 WORLD_POSITION (a point light from the rig's position, the NIS quick light).
    /// The colour is Scale·R·255·(1/255) = Scale·RGB, no time of day. The car effect gets the directions as
    /// LocalDirectionMatrix and the colours as LocalColourMatrix (sub_6C7420, params 58/59).
    /// Which rig a draw uses (CarRenderInfo::Render 0x74E3E5, WorldModel::RenderModel 0x73B9C0): in game (game flow 6)
    /// every car, smackable and car fragment → CarsInGame; in the front end by FEManager::GetGarageType(): 2 Safehouse,
    /// 3 CShop, 4 BackRoom, 5 CarLot, else QRace; NIS characters → Characters (camera-relative, two lights; worldbone.efx
    /// reads only LocalDirectionMatrix rows 0-1, so the third slot — the NIS cop quick light — has no visible effect).
    /// </summary>
    public static class ShaperRig
    {
        public struct Light { public int Mode; public float Theta, Phi, R, G, B, Scale; }

        public enum Kind { CarsInGame, QRace, CShop, BackRoom, Safehouse, CarLot, Characters, Default }

        static Light L(int mode, float theta, float phi, float r, float g, float b, float scale) =>
            new Light { Mode = mode, Theta = theta, Phi = phi, R = r, G = g, B = b, Scale = scale };

        /// <summary>ShaperLightsCarsInGame 0x8F2E70.</summary>
        public static readonly Light[] CarsInGame =
        {
            L(2, 0f, 350f, 0.85f, 0.9f, 1f, 0.83f),
            L(2, 50f, 220f, 0.55f, 0.6f, 1f, 0.4f),
            L(2, 310f, 220f, 0.65f, 0.7f, 1f, 0.45f),
        };

        /// <summary>ShaperLightsBackRoom 0x8F2B50 = CShop 0x8F2C90 = QRace 0x8F2D30 = Safehouse 0x8F2DD0 (all WORLD_SPACE).</summary>
        public static readonly Light[] Garage =
        {
            L(0, 90f, 48f, 1f, 1f, 0.95f, 0.98f),
            L(0, 134f, 185f, 0.83f, 0.83f, 1f, 0.5f),
            L(0, 27f, 175f, 0.83f, 0.83f, 1f, 0.35f),
        };

        /// <summary>ShaperLightsCarLot 0x8F2BF0: the garage rig with the azimuths negated.</summary>
        public static readonly Light[] CarLot =
        {
            L(0, -90f, 48f, 1f, 1f, 0.95f, 0.98f),
            L(0, -134f, 185f, 0.83f, 0.83f, 1f, 0.5f),
            L(0, -27f, 175f, 0.83f, 0.83f, 1f, 0.35f),
        };

        /// <summary>ShaperLightsCharacters 0x8F2F10 (CAMERA_SPACE; the third slot is the NIS cop quick light, black at rest).</summary>
        public static readonly Light[] Characters =
        {
            L(1, 122.9f, 194f, 0.5f, 0.5f, 0.5f, 0.53f),
            L(1, 28f, 0f, 1f, 1f, 1f, 0.59f),
            L(1, 0f, 0f, 0f, 0f, 0f, 0f),
        };

        /// <summary>ShaperLightsDefault 0x8F2AB0: eView::SetupWorldLightContext's rig, used only for car-effect draws that
        /// arrive without a light context (FE geometry models).</summary>
        public static readonly Light[] Default =
        {
            L(2, 0f, 0f, 1f, 1f, 1f, 0.2f),
            L(3, 0f, 0f, 1f, 1f, 1f, 0.2f),
            L(1, 0f, 0f, 0f, 0f, 0f, 0f),
        };

        public static Light[] Table(Kind k) => k switch
        {
            Kind.CarLot => CarLot,
            Kind.QRace or Kind.CShop or Kind.BackRoom or Kind.Safehouse => Garage,
            Kind.Characters => Characters,
            Kind.Default => Default,
            _ => CarsInGame,
        };

        /// <summary>FEManager::GetGarageType by the showroom's platform set file (FRONTEND\PLATFORMS\*.BIN) [ours: the
        /// mapping; the exe keys on the garage enum 2 SAFEHOUSE, 3 CUSTOMIZATION_SHOP, 4 BACKROOM, 5 CAR_LOT, else QRace].</summary>
        public static Kind GarageOf(string platformFile)
        {
            if (string.IsNullOrEmpty(platformFile)) return Kind.QRace;
            string f = platformFile.ToUpperInvariant();
            if (f.Contains("CAR_LOT") || f.Contains("CARLOT")) return Kind.CarLot;
            if (f.Contains("BACKROOM")) return Kind.BackRoom;
            if (f.Contains("CUSTOMIZATION") || f.Contains("SHOP")) return Kind.CShop;
            if (f.Contains("SAFEHOUSE")) return Kind.Safehouse;
            return Kind.QRace;
        }

        const double Deg = Math.PI / 180.0;

        /// <summary>
        /// Light directions in game space (Z up) for a sun direction (game space, toward the sun) — FUN_004fc3c0 (xyz →
        /// bAngle azimuth atan2(y, x), polar 90° − asin(z/len)) and FUN_004fc320 (angles → (sinB·cosA, sinB·sinA, cosB)).
        /// Mode 0 ignores the sun: a = θ + 180, b = 90 − φ. Mode 1 (camera space) is resolved by the caller
        /// (<see cref="CameraSpace"/>). Mode 4 needs the rig position (not used by any PC rig at rest).
        /// </summary>
        public static (float x, float y, float z) Direction(Light l, float sx, float sy, float sz)
        {
            if (l.Mode == 0 || l.Mode == 1) return FromAngles(l.Theta + 180.0, 90.0 - l.Phi);
            if (l.Mode == 3) { sx = -sx; sy = -sy; sz = -sz; }
            double len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            if (len < 1e-6) len = 1;
            // 16-bit bAngles as the exe keeps them (×0.005493164 = 360/65536)
            int azim = (int)(Math.Atan2(sy, sx) / (2 * Math.PI) * 65536.0) & 0xFFFF;
            int polar = (0x4000 - (int)(Math.Asin(Math.Max(-1, Math.Min(1, sz / len))) / (2 * Math.PI) * 65536.0)) & 0xFFFF;
            double theta = azim * 0.005493164 + l.Theta;
            double phi = polar * 0.005493164 + 90.0 + l.Phi;
            return FromAngles(theta + 180.0, 90.0 - phi);
        }

        /// <summary>SphericalToCartesian 0x4FC320 with r = 1 (16-bit angles: (int)(deg·65536)/360).</summary>
        public static (float x, float y, float z) FromAngles(double aDeg, double bDeg)
        {
            double a = ((int)(aDeg * 65536.0) / 360 & 0xFFFF) * (2 * Math.PI / 65536.0);
            double b = ((int)(bDeg * 65536.0) / 360 & 0xFFFF) * (2 * Math.PI / 65536.0);
            return ((float)(Math.Sin(b) * Math.Cos(a)), (float)(Math.Sin(b) * Math.Sin(a)), (float)Math.Cos(b));
        }

        /// <summary>
        /// elSetupLights mode 1 CAMERA_SPACE (0x502130, case 0x5021F7): (x, y, z) = S2C(θ+180, 90−φ) (0x4FC320);
        /// c = (−y, −z, x, 0); 0x6BE140 copies the view's Camera matrix (CameraParams::Matrix = world→view, Camera::
        /// SetCameraMatrix takes its transpose's v2 as the direction) through bTransposeMatrix (0x45ED30 →
        /// D3DXMatrixTranspose) and zeroes the translation / w column; D3DXVec4Transform(c, Mᵀ) = Σ c_j · (column j of the
        /// view matrix) = Σ c_j · camera axis j, i.e. d = −y·camRight − z·camUp + x·camForward (world space, toward the
        /// light). Resolved in round 9 (was [?] on the column naming).
        /// </summary>
        public static (float right, float up, float forward) CameraSpace(Light l)
        {
            var (x, y, z) = FromAngles(l.Theta + 180.0, 90.0 - l.Phi);
            return (-y, -z, x);
        }
    }

    /// <summary>Tables of CarRenderInfo (World/CarRender.cpp) used by the Unity car renderer.</summary>
    public static class CarRenderTables
    {
        /// <summary>CarBodyLodSwapSize (0x8B2EBC) and the PC TrafficCarBodyLodSwapSize (0x8B2ED0; the GC table is 20/10/4).</summary>
        public static readonly float[] CarBodyLodSwapSize = { 120f, 25f, 20f, 10f, 0f };
        public static readonly float[] TrafficCarBodyLodSwapSize = { 80f, 40f, 20f, 10f, 0f };
        public const float CarTwoPlayerLODFactor = 0.7f;
        /// <summary>mRadius default (CarRenderInfo ctor, CarRender.cpp:1128).</summary>
        public const float Radius = 3f;

        /// <summary>CarReplacementDecalHash[26] (CarRender.cpp:383): decal texture names that default to DEFAULTALPHA.</summary>
        public static readonly string[] DecalNames =
        {
            "BOTTOM_DECAL", "FRONT_BUMPER_DECAL", "FRONT_DECAL", "GTWING_DECAL", "HOOD_DECAL", "LEFT_BRAKELIGHT_DECAL", "LEFT_DOOR_DECAL",
            "LEFT_FENDER_DECAL", "LEFT_QUARTER_DECAL", "LEFT_SIDE_MIRROR_DECAL", "LEFT_SKIRT_DECAL", "REAR_BUMPER_DECAL", "REAR_DECAL",
            "RIGHT_BRAKELIGHT_DECAL", "RIGHT_DOOR_DECAL", "RIGHT_FENDER_DECAL", "RIGHT_QUARTER_DECAL", "RIGHT_SIDE_MIRROR_DECAL",
            "RIGHT_SKIRT_DECAL", "TOP_DECAL", "FRONT_WINDOW_DECAL", "REAR_WINDOW_DECAL", "LEFT_FRONT_WINDOW_DECAL", "LEFT_REAR_WINDOW_DECAL",
            "RIGHT_FRONT_WINDOW_DECAL", "RIGHT_REAR_WINDOW_DECAL",
        };

        /// <summary>UpdateDecalTextures (CarRender.cpp:2029): the 8 dummy textures of a decal mount, TEX0..5 then the numbers.</summary>
        public static readonly string[] DecalDummies =
            { "DUMMY_DECAL1", "DUMMY_DECAL2", "DUMMY_DECAL3", "DUMMY_DECAL4", "DUMMY_DECAL5", "DUMMY_DECAL6", "DUMMY_NUMBER_LEFT", "DUMMY_NUMBER_RIGHT" };

        /// <summary>The decal mount slots (CARSLOTID_DECAL_FRONT_WINDOW 70 .. RIGHT_QUARTER 75) and their part names.</summary>
        public static readonly string[] DecalMounts = { "DECAL_FRONT_WINDOW", "DECAL_REAR_WINDOW", "DECAL_LEFT_DOOR", "DECAL_RIGHT_DOOR", "DECAL_LEFT_QUARTER", "DECAL_RIGHT_QUARTER" };

        /// <summary>The decal texture suffix of a mount's SHAPE attribute (SQUARE / RECT / WIDE hashes).</summary>
        public static string ShapeSuffix(uint shape) =>
            shape == EaHash.Hash("SQUARE") ? "_SQUARE" : shape == EaHash.Hash("RECT") ? "_RECT" : shape == EaHash.Hash("WIDE") ? "_WIDE" : null;

        /// <summary>The six DAMAGE0 overlay slots (CARSLOTID_DAMAGE0_FRONT 46 .. REARRIGHT 51) and the zone that shows each
        /// (SetPlayerDamage, CarRender.cpp:1586-1622: level &gt; 0).</summary>
        public static readonly (string part, int zone)[] Damage0Parts =
        {
            ("DAMAGE0_FRONT", DamageZone.Front), ("DAMAGE0_FRONTLEFT", DamageZone.LFront), ("DAMAGE0_FRONTRIGHT", DamageZone.RFront),
            ("DAMAGE0_REAR", DamageZone.Rear), ("DAMAGE0_REARLEFT", DamageZone.LRear), ("DAMAGE0_REARRIGHT", DamageZone.RRear),
        };

        /// <summary>
        /// SetPlayerDamage's glass (level &gt; 1 → WINDOW_DAMAGE0): replacement entry → zone. The car meshes reference only
        /// WINDOW_FRONT (every side window too) and REAR_DEFROSTER, so in practice DZ_FRONT cracks all the WINDOW_FRONT
        /// glass and DZ_REAR the rear window; the side entries match no texture.
        /// </summary>
        public static readonly (string texture, int zone)[] PlayerGlass =
        {
            ("WINDOW_FRONT", DamageZone.Front), ("WINDOW_REAR", DamageZone.Rear), ("WINDOW_LEFT_FRONT", DamageZone.LFront),
            ("WINDOW_LEFT_REAR", DamageZone.LRear), ("WINDOW_RIGHT_FRONT", DamageZone.RFront), ("WINDOW_RIGHT_REAR", DamageZone.RRear),
            ("REAR_DEFROSTER", DamageZone.Rear),
        };
    }
    /// <summary>
    /// The drawn wheel geometry of CarRenderInfo (round 9): UpdateWheelYRenderOffset (0x74D780, CarRender.cpp:1918),
    /// the model sizes of UpdateCarParts (0x755C1D..0x755CDF) and the per-wheel matrices of Render (0x74F048..0x74F2A7,
    /// CarRender.cpp:3343-3385). The wheels are drawn from the `ecar` attributes, not from the physics chassis
    /// (TRACK_WIDTH / FRONT_AXLE, which the sim uses): GTI ecar front x 1.218 vs chassis FRONT_AXLE 1.17.
    /// <list type="bullet">
    /// <item>ecar TireOffsets[4] (Vector4, game order FL FR RR RL) = (x forward, y left, z up, radius): GTI (1.218,
    /// ±0.865, 0, 0.325) front, (−1.37, ∓0.855, 0, 0.325) rear. Vector4 arrays are 16-byte aligned: the data starts 16
    /// bytes after the array header, not 8 (Core/Attrib.cs TryGet returns header + 8; the old "(0, 0, 1.218, 0.865)"
    /// reading was that offset).</item>
    /// <item>model width W / radius R (+0x120 / +0x128 front, +0x124 / +0x12C rear) = |bbox.max.y − min.y| and
    /// 0.5·|bbox.max.x − min.x| of the FRONT_WHEEL / REAR_WHEEL slot model at the min LOD.</item>
    /// <item>WheelYRenderOffset = −TireOffsets.y, then pushed outward by KitWheelOffsetFront/Rear[kit] (u8, ×0.001;
    /// the tweaks [0x9B0960] / [0x9B0964] are 0); kit = the BODY part's KITNUMBER (0x796C0CB0).</item>
    /// <item>WheelWidthScale = TireSkidWidth[i] × TireSkidWidthKitScale[kit].x (front) / .y (rear) ÷ W, else 1;
    /// WheelRadiusScale = TireOffsets[i].w ÷ R, else 1.</item>
    /// <item>trans_pivot = scale(r, w, r) with v3.y = −p, p = W·w·0.5 (WheelPivotTranslationAmount [0x903314]);
    /// trans_axle.y = −(yoff + p) when yoff &lt; 0 else p − yoff; the tyre matrix's own y is zeroed and its x is
    /// TireOffsets.x (CarRenderConn ctor → mTirePositions). The model's rim face (y = 0) thus sits at the TireOffsets
    /// lateral (plus the kit offset) and the tread extends inboard by the scaled width.</item>
    /// <item>brake_trans_pivot.y = markerY·w − p when the wheel model has a FRONT_BRAKE / REAR_BRAKE marker (y ≠ 0;
    /// rear falls back to the front marker), else 0.</item>
    /// </list>
    /// </summary>
    public static class WheelRenderGeometry
    {
        public struct Wheel
        {
            /// <summary>Wheel centre (the scale pivot) in game car space: x forward, y left (z from the sim).</summary>
            public float X, Y;
            public float RadiusScale, WidthScale;
            /// <summary>p: the model shift along its axle after scaling (trans_pivot.v3.y = −p).</summary>
            public float Pivot;
            /// <summary>brake_trans_pivot.v3.y (model space, before the left-side flip).</summary>
            public float BrakeY;
        }

        /// <summary>Port order FL FR RL RR → game order FL FR RR RL.</summary>
        public static int GameIndex(int portIndex) => portIndex < 2 ? portIndex : 5 - portIndex;

        /// <summary>A Vector4 array element: TryGet's offset is header + 8, the 16-byte aligned data starts 8 later.</summary>
        static bool Vec4(AttribCollection e, string field, int i, out float x, out float y, out float z, out float w)
        {
            x = y = z = w = 0f;
            if (e == null || !e.TryGet(field, out int at, out int count, out var def) || i >= count) return false;
            int o = at + (def.IsArray && def.Size == 16 ? 8 : 0) + i * 16;
            var d = e.Source.Data;
            if (o + 16 > d.Length) return false;
            x = Chunks.F32(d, o); y = Chunks.F32(d, o + 4); z = Chunks.F32(d, o + 8); w = Chunks.F32(d, o + 12);
            return true;
        }

        /// <summary>Attrib array bounds rule (0x6269B0 default element): an index past the count reads 0.</summary>
        static float ArrF(AttribCollection e, string field, int i, int stride = 4, int sub = 0)
        {
            if (e == null || !e.TryGet(field, out int at, out int count, out _) || i >= count) return 0f;
            return Chunks.F32(e.Source.Data, at + i * stride + sub * 4);
        }

        static int ArrU8(AttribCollection e, string field, int i)
        {
            if (e == null || !e.TryGet(field, out int at, out int count, out _) || i >= count) return 0;
            return e.Source.Data[at + i];
        }

        /// <summary>
        /// The four wheels (game order) from the car's own ecar collection; false when the car has none (the port then
        /// keeps its mesh-fitted wheels [ours]: the class default's TireOffsets put the rear wheels at x = 3 / 5).
        /// modelW/R[0] front, [1] rear; brakeMarkerY[0..1] (0 = none).
        /// </summary>
        public static bool Compute(AttribDatabase db, string car, int kit, float[] modelW, float[] modelR, float[] brakeMarkerY, Wheel[] outWheels)
        {
            var e = db?.Get("ecar", (car ?? "").ToLowerInvariant());
            if (e == null || !e.Has("TireOffsets")) return false;
            for (int i = 0; i < 4; i++)
            {
                int end = i > 1 ? 1 : 0;
                if (!Vec4(e, "TireOffsets", i, out float tx, out float ty, out _, out float tw)) return false;
                float yoff = -ty;
                int kitOff = ArrU8(e, end == 0 ? "KitWheelOffsetFront" : "KitWheelOffsetRear", Math.Max(0, kit));
                float k = kitOff * 0.001f;
                yoff = yoff <= 0f ? yoff - k : yoff + k;
                float desiredW = ArrF(e, "TireSkidWidth", i) * ArrF(e, "TireSkidWidthKitScale", Math.Max(0, kit), 8, end);
                float ws = modelW[end] > 0f && desiredW > 0f ? desiredW / modelW[end] : 1f;
                float rs = modelR[end] > 0f && tw > 0f ? tw / modelR[end] : 1f;
                float p = modelW[end] * ws * 0.5f;
                float axleY = yoff < 0f ? -(yoff + p) : p - yoff;
                float marker = brakeMarkerY[end];
                outWheels[i] = new Wheel { X = tx, Y = axleY, RadiusScale = rs, WidthScale = ws, Pivot = p, BrakeY = marker != 0f ? marker * ws - p : 0f };
            }
            return true;
        }
    }
}
