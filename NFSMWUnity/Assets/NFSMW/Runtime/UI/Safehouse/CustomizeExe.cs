using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>eCustomizeCategory (customize/FECustomize.hpp:21-86): screen id in the high byte, item in the low byte.</summary>
    public static class CC
    {
        public const int ToCatMask = 0xFFFF, ScreenIdMain = 0x800, ScreenIdParts = 0x100, ScreenIdPerformance = 0x200, ScreenIdVisual = 0x300,
            ScreenIdVinylTypes = 0x400, ScreenIdDecalLocation = 0x500, ScreenIdDecalPosition = 0x600, ScreenIdRimBrands = 0x700, IdMask = 0xFF00;
        public const int None = 0, Parts = 0x801, Performance = 0x802, Visual = 0x803,
            BodyKit = 0x101, Spoilers = 0x102, RimBrands = 0x103, Hoods = 0x104, RoofScoops = 0x105,
            Engine = 0x201, Transmission = 0x202, Suspension = 0x203, Nitrous = 0x204, Tires = 0x205, Brakes = 0x206, ForcedInduction = 0x207,
            Paint = 0x301, VinylTypes = 0x302, RimPaint = 0x303, WindowTint = 0x304, DecalLocation = 0x305, Numbers = 0x306, CustomHud = 0x307,
            VinylGroupStock = 0x401, VinylGroupFlame = 0x402, VinylGroupTribal = 0x403, VinylGroupStripe = 0x404, VinylGroupRacingFlag = 0x405,
            VinylGroupNationalFlag = 0x406, VinylGroupBody = 0x407, VinylGroupUnique = 0x408, VinylGroupContest = 0x409,
            RimBrandStock = 0x701, RimBrand5Zigen = 0x702, RimBrandAdr = 0x703, RimBrandBbs = 0x704, RimBrandEnkei = 0x705, RimBrandKonig = 0x706,
            RimBrandLowenhart = 0x707, RimBrandRacingHart = 0x708, RimBrandOz = 0x709, RimBrandVolk = 0x70A, RimBrandRoja = 0x70B,
            DecalWindshield = 0x501, DecalRearWindow = 0x502, DecalLeftDoor = 0x503, DecalRightDoor = 0x504, DecalLeftQp = 0x505, DecalRightQp = 0x506,
            DecalSlot1 = 0x601, DecalSlot2 = 0x602, DecalSlot3 = 0x603, DecalSlot4 = 0x604, DecalSlot5 = 0x605, DecalSlot6 = 0x606;

        public static bool IsVinylGroup(int c) => c >= VinylGroupFlame && c <= VinylGroupContest;
        public static bool IsRimBrand(int c) => c >= RimBrand5Zigen && c <= RimBrandRoja;
        public static bool IsDecalLocation(int c) => c >= DecalWindshield && c <= DecalRightQp;
        public static bool IsDecalSlot(int c) => c >= DecalSlot1 && c <= DecalSlot6;
        public static bool IsPerf(int c) => c >= Engine && c <= ForcedInduction;
    }

    /// <summary>eCustomizePartState (FECustomize.hpp:107-114).</summary>
    public static class CPS
    {
        public const int Available = 1, Locked = 2, New = 3, Installed = 16, InCart = 32, GameMask = 0xF, PlayerMask = 0xF0;
    }

    /// <summary>
    /// The customize screens' constant tables from CarCustomize.cpp / CustomizeManager.cpp (PC 0x7A2B80-0x7C3A45,
    /// 0x7A4990-0x7C391A): category → marker / marker name / unlock string, the option icon and name hashes of every
    /// sub menu, the shopping cart's category and level names, IsCategoryLocked / IsCategoryNew, the performance
    /// package descriptions and brand icons, the car checks (turbo, Castrol, rotary, hero, max RPM).
    /// </summary>
    public static class CustomizeTables
    {
        // ---- category → marker (TranslateCustomizeCatToMarker, CC:35) --------------------------------------------
        public static int MarkerOfCategory(int cat)
        {
            switch (cat)
            {
                case CC.BodyKit: return ShopRules.M_Body;
                case CC.Spoilers: return ShopRules.M_Spoiler;
                case CC.Hoods: return ShopRules.M_Hood;
                case CC.RoofScoops: return ShopRules.M_RoofScoop;
                case CC.CustomHud: return ShopRules.M_CustomHud;
                case CC.Engine: return ShopRules.M_Engine;
                case CC.Transmission: return ShopRules.M_Transmission;
                case CC.Suspension: return ShopRules.M_Chassis;
                case CC.Nitrous: return ShopRules.M_Nos;
                case CC.Tires: return ShopRules.M_Tires;
                case CC.Brakes: return ShopRules.M_Brakes;
                case CC.ForcedInduction: return ShopRules.M_Induction;
                case CC.Paint: case CC.RimPaint: return ShopRules.M_Paint;
            }
            if (cat == CC.VinylTypes || CC.IsVinylGroup(cat)) return ShopRules.M_Vinyl;
            if (cat == CC.RimBrands || CC.IsRimBrand(cat)) return ShopRules.M_Rims;
            if (cat == CC.DecalLocation || CC.IsDecalLocation(cat) || CC.IsDecalSlot(cat)) return ShopRules.M_Decal;
            return 0;
        }

        /// <summary>GetMarkerNameFromCategory (CC:105): the "… Markers" label of a category.</summary>
        public static uint MarkerNameHash(int cat)
        {
            switch (cat)
            {
                case CC.Parts: return 0xd3a2fbe1;
                case CC.Performance: return 0x3c27a989;
                case CC.Visual: return 0x5692be6b;
                case CC.BodyKit: return 0x7c50498c;
                case CC.Spoilers: return 0x52012995;
                case CC.Hoods: return 0x8a4699e1;
                case CC.RoofScoops: return 0x830100f0;
                case CC.CustomHud: return 0xc253ec92;
                case CC.Engine: return 0x2f3ec04d;
                case CC.Transmission: return 0xd1e77ca1;
                case CC.Suspension: return 0xb7cbfcce;
                case CC.Nitrous: return 0xc129562b;
                case CC.Tires: return 0xd3efbefe;
                case CC.Brakes: return 0x2884658f;
                case CC.ForcedInduction: return IsTurbo() ? 0xd3f65323u : 0x63a51aa2u;
                case CC.Paint: return 0xd3a2d4d3;
            }
            if (cat == CC.RimBrands || CC.IsRimBrand(cat)) return 0x8a4bfbf2;
            if (cat == CC.VinylTypes || CC.IsVinylGroup(cat)) return 0xd413e189;
            if (cat == CC.DecalLocation || CC.IsDecalLocation(cat) || CC.IsDecalSlot(cat)) return 0xd2cbc510;
            return 0;
        }

        /// <summary>GetNumMarkersFromCategory (CC:183): the three main categories sum their children.</summary>
        public static int NumMarkers(int cat)
        {
            switch (cat)
            {
                case CC.Parts: return NumMarkers(CC.BodyKit) + NumMarkers(CC.Spoilers) + NumMarkers(CC.RimBrands) + NumMarkers(CC.Hoods) + NumMarkers(CC.RoofScoops) + NumMarkers(CC.CustomHud);
                case CC.Performance: return NumMarkers(CC.Engine) + NumMarkers(CC.Transmission) + NumMarkers(CC.Suspension) + NumMarkers(CC.Nitrous) + NumMarkers(CC.Tires) + NumMarkers(CC.Brakes) + NumMarkers(CC.ForcedInduction);
                case CC.Visual: return NumMarkers(CC.Paint) + NumMarkers(CC.VinylTypes) + NumMarkers(CC.DecalLocation);
            }
            return ShopMarkers.Count(MarkerOfCategory(cat));
        }

        // ---- unlock blurb (CarCustomizeManager::GetUnlockHash, CM:572) -----------------------------------------------
        static string UnlockName(int cat)
        {
            switch (cat)
            {
                case CC.BodyKit: return "PARTS_BODYKITS";
                case CC.Spoilers: return "PARTS_SPOILERS";
                case CC.Hoods: return "PARTS_HOODS";
                case CC.RoofScoops: return "PARTS_ROOFSCOOPS";
                case CC.Engine: return "PERF_ENGINE";
                case CC.Transmission: return "PERF_TRANSMISSION";
                case CC.Suspension: return "PERF_SUSPENSION";
                case CC.Nitrous: return "PERF_NITROUS";
                case CC.Tires: return "PERF_TIRES";
                case CC.Brakes: return "PERF_BRAKES";
                case CC.ForcedInduction: return IsTurbo() ? "PERF_TURBO" : "PERF_SUPERCHARGER";
                case CC.Paint: return "VISUAL_PAINT";
                case CC.RimPaint: return "VISUAL_RIMPAINT";
                case CC.WindowTint: return "VISUAL_WINDOWTINT";
                case CC.Numbers: return "VISUAL_NUMBERS";
                case CC.CustomHud: return "VISUAL_HUDS";
            }
            if (cat == CC.RimBrandStock || CC.IsRimBrand(cat)) return "PARTS_RIMS";
            if (CC.IsVinylGroup(cat)) return "VISUAL_VINYLS";
            if (CC.IsDecalLocation(cat) || CC.IsDecalSlot(cat)) return "VISUAL_DECALS";
            return null;
        }

        /// <summary>CUSTOMIZATION_&lt;cat&gt;_&lt;level&gt; when the string exists, else 0x9BB9CCC3 ("FENG: Default string error.").</summary>
        public static uint UnlockHash(int cat, int level)
        {
            var name = UnlockName(cat);
            if (name != null && level != 0)
            {
                uint h = FrontEndData.Hash($"CUSTOMIZATION_{name}_{level}");
                if (FrontEndData.Strings != null && FrontEndData.Strings.TryGet(h, out _)) return h;
            }
            return 0x9bb9ccc3;
        }

        // ---- shopping cart item names (FEShoppingCartItem::GetPerfPkgCatHash / GetPerfPkgLevelHash / GetCarPartCatHash, CC:679-786)
        public static uint PerfPkgCatHash(int type) => type switch
        {
            ShopRules.Tires => 0x5aa9137, ShopRules.Brakes => 0x91997ee8, ShopRules.Chassis => 0x6e101aa7, ShopRules.Transmission => 0x29aa74ba,
            ShopRules.Engine => 0x9853d9a6, ShopRules.Induction => IsTurbo() ? 0x5b1255cu : 0xbb6812bbu, ShopRules.Nos => 0x4ce19aa4, _ => 0u,
        };

        /// <summary>PN_LEVEL_1..6 by hash (0x69c270c3 + level; the stock level is 0x69c270c3).</summary>
        public static uint PerfPkgLevelHash(int level) => level >= 1 && level <= 6 ? 0x69c270c3u + (uint)level : 0x69c270c3u;

        public static uint CarPartCatHash(int slot)
        {
            switch (slot)
            {
                case ShopRules.Slot.Body: return 0x6134c218;
                case ShopRules.Slot.Spoiler: return 0x94e73021;
                case ShopRules.Slot.Roof: return 0x61e8f83c;
                case ShopRules.Slot.Hood: return 0x4d4a88d;
                case ShopRules.Slot.FrontWheel: return 0xf868eb0b;
                case ShopRules.Slot.BasePaint: return 0x55da70c;
                case ShopRules.Slot.CustomHud: return 0x78980a6b;
                case ShopRules.Slot.WindowTint: return 0xd32729a6;
                case ShopRules.Slot.PaintRim: return 0xe126ff53;
                case ShopRules.Slot.DecalFrontWindow: return 0x301dedd3;
                case ShopRules.Slot.DecalRearWindow: return 0x48e6ca49;
                case ShopRules.Slot.DecalLeftQuarter: return 0x8a7697d6;
                case ShopRules.Slot.DecalRightQuarter: return 0xb1f9b0c9;
                case ShopRules.Slot.RightDoorLNumber: return 0x6857e5ac;
                case ShopRules.Slot.VinylLayer: return 0xbfa52c55;
            }
            if (slot >= ShopRules.Slot.DecalLeftDoor && slot <= ShopRules.Slot.DecalLeftDoor + 5) return 0x34367c86;
            if (slot >= ShopRules.Slot.DecalRightDoor && slot <= ShopRules.Slot.DecalRightDoor + 5) return 0xddf80259;
            return 0;
        }

        // ---- option tables (CustomizeSub::Setup*, CC:1592-1880; CustomizeMain::BuildOptionsList, CC:2067) -------------
        public readonly struct Opt
        {
            public readonly uint Tex, Name;
            public readonly int Cat;
            public Opt(uint tex, uint name, int cat) { Tex = tex; Name = name; Cat = cat; }
        }

        public static Opt[] Main(bool backroom, bool hero)
        {
            var l = new List<Opt>();
            if (!hero)
            {
                l.Add(new Opt(backroom ? 0x73272ed2u : 0x6e0ca66cu, 0x055dce1a, CC.Parts));
                l.Add(new Opt(backroom ? 0xc61c8d3au : 0x3987d054u, 0xbaef8282, CC.Performance));
            }
            l.Add(new Opt(backroom ? 0xe69d4f7cu : 0x3e31ba56u, 0xbfa7d7c4, CC.Visual));
            return l.ToArray();
        }

        public static Opt[] Parts(bool backroom) => backroom
            ? new[] { new Opt(0xaf393dba, 0x6134c218, CC.BodyKit), new Opt(0xc51a4f62, 0x94e73021, CC.Spoilers), new Opt(0xc19491cc, 0xf868eb0b, CC.RimBrands), new Opt(0xf375276e, 0x04d4a88d, CC.Hoods), new Opt(0x25a4375e, 0x61e8f83c, CC.RoofScoops) }
            : new[] { new Opt(0x028c24f6, 0x6134c218, CC.BodyKit), new Opt(0xbb034ea6, 0x94e73021, CC.Spoilers), new Opt(0x0294d2a3, 0xf868eb0b, CC.RimBrands), new Opt(0x028f7092, 0x04d4a88d, CC.Hoods), new Opt(0x79165861, 0x61e8f83c, CC.RoofScoops) };

        public static Opt[] Performance(bool backroom)
        {
            bool turbo = IsTurbo();
            return backroom
                ? new[] { new Opt(0x4f424e0f, 0x9853d9a6, CC.Engine), new Opt(0xd142d3e3, 0x29aa74ba, CC.Transmission), new Opt(0x00190eb6, 0x6e101aa7, CC.Suspension), new Opt(0x6fea04c8, 0x4ce19aa4, CC.Nitrous), new Opt(0x7373f1ef, 0x05aa9137, CC.Tires), new Opt(0x4887f351, 0x91997ee8, CC.Brakes), turbo ? new Opt(0x12fe30a5, 0x05b1255c, CC.ForcedInduction) : new Opt(0x630071e4, 0xbb6812bb, CC.ForcedInduction) }
                : new[] { new Opt(0xc15c94e6, 0x9853d9a6, CC.Engine), new Opt(0x01a29ffa, 0x29aa74ba, CC.Transmission), new Opt(0x178475e7, 0x6e101aa7, CC.Suspension), new Opt(0x9701bde4, 0x4ce19aa4, CC.Nitrous), new Opt(0x06e8e477, 0x05aa9137, CC.Tires), new Opt(0xbaa23a28, 0x91997ee8, CC.Brakes), turbo ? new Opt(0x06ef789c, 0x05b1255c, CC.ForcedInduction) : new Opt(0x93603dfb, 0xbb6812bb, CC.ForcedInduction) };
        }

        public static Opt[] Visual(bool backroom, bool hero)
        {
            if (backroom)
                return new[] { new Opt(0x0db89e17, 0x055da70c, CC.Paint), new Opt(0xd35f04c0, 0xbfa52c55, CC.VinylTypes), new Opt(0xa9135927, 0x955980bc, CC.DecalLocation), new Opt(0x8ba602fc, 0x78980a6b, CC.CustomHud) };
            var l = new List<Opt> { new Opt(0xa3b76154, 0x055da70c, CC.Paint), new Opt(0x55778e5a, 0xbfa52c55, CC.VinylTypes) };
            if (!hero) l.Add(new Opt(0xd223f84a, 0xe126ff53, CC.RimPaint));
            l.Add(new Opt(0x3f23165c, 0xd32729a6, CC.WindowTint));
            l.Add(new Opt(0xda1dae54, 0x955980bc, CC.DecalLocation));
            l.Add(new Opt(0x45a1c644, 0x6857e5ac, CC.Numbers));
            l.Add(new Opt(0x028f88bc, 0x78980a6b, CC.CustomHud));
            return l.ToArray();
        }

        /// <summary>The stock entry's icon of the rim brand / vinyl group menus (SetStockPartOption, CC:1705 / 1780).</summary>
        public const uint RimStockTex = 0x0f3990b6, VinylStockTex = 0x21f3d114;

        public static readonly Opt[] RimBrandOpts =
        {
            new Opt(0xb0da3de4, 0x56b51a0e, CC.RimBrand5Zigen), new Opt(0xf224a729, 0xf93f2d34, CC.RimBrandAdr), new Opt(0xf224ab29, 0xf93f3134, CC.RimBrandBbs),
            new Opt(0x0e38de9e, 0x460d1369, CC.RimBrandEnkei), new Opt(0x0ea60b4a, 0x467a4015, CC.RimBrandKonig), new Opt(0xafc6b9cb, 0x9bb17a11, CC.RimBrandLowenhart),
            new Opt(0x27ebd095, 0xcca3063f, CC.RimBrandRacingHart), new Opt(0x6c2fa9db, 0xc1bc1a86, CC.RimBrandOz), new Opt(0x36c53e8e, 0x213085f9, CC.RimBrandVolk),
            new Opt(0x36c2d130, 0x212e5429, CC.RimBrandRoja),
        };

        /// <summary>CustomizeRims::GetCategoryBrandHash (CC:3428): the BRAND_NAME of each rim brand category.</summary>
        public static uint RimBrandHash(int cat) => cat switch
        {
            CC.RimBrand5Zigen => 0x352d08d1, CC.RimBrandAdr => 0x9136, CC.RimBrandBbs => 0x9536, CC.RimBrandEnkei => 0x2b77feb, CC.RimBrandKonig => 0x324ac97,
            CC.RimBrandLowenhart => 0x48e25793, CC.RimBrandRacingHart => 0xdd544a02, CC.RimBrandOz => 0x648, CC.RimBrandVolk => 0x1e6a3b, CC.RimBrandRoja => 0x1c386b, _ => 0u,
        };

        /// <summary>CustomizeSub::GetRimBrandIndex (CC:1678): option index of a brand (1 = stock).</summary>
        public static int RimBrandIndex(uint brand)
        {
            for (int i = 0; i < RimBrandOpts.Length; i++) if (RimBrandHash(RimBrandOpts[i].Cat) == brand) return i + 2;
            return 1;
        }

        public static readonly Opt[] VinylGroupOpts =
        {
            new Opt(0xf8148554, 0xd9228fc6, CC.VinylGroupFlame), new Opt(0x192d84da, 0x1e8d885f, CC.VinylGroupTribal), new Opt(0xf7352706, 0x1c619fd8, CC.VinylGroupStripe),
            new Opt(0x1223cc89, 0x9c1b8935, CC.VinylGroupRacingFlag), new Opt(0xbc44bbcb, 0x7956f7b0, CC.VinylGroupNationalFlag), new Opt(0x694ca0ca, 0x2d5bff0f, CC.VinylGroupBody),
            new Opt(0x1b3a8dd3, 0x209a9158, CC.VinylGroupUnique), new Opt(0x1ba508fc, 0xcd057d21, CC.VinylGroupContest),
        };

        /// <summary>CustomizeSub::GetVinylGroupIndex (CC:1757): group 0..7 → option 2..9, else 1 (stock).</summary>
        public static int VinylGroupIndex(int group) => group >= 0 && group <= 7 ? group + 2 : 1;

        public static readonly Opt[] DecalLocationOpts =
        {
            new Opt(0x52ded91d, 0x301dedd3, CC.DecalWindshield), new Opt(0xac7937b4, 0x48e6ca49, CC.DecalRearWindow), new Opt(0xda88b711, 0x34367c86, CC.DecalLeftDoor),
            new Opt(0xc9a967c4, 0xddf80259, CC.DecalRightDoor), new Opt(0x2c710c4d, 0x8a7697d6, CC.DecalLeftQp), new Opt(0xffa7d360, 0xb1f9b0c9, CC.DecalRightQp),
        };

        public static Opt[] DecalPositionOpts(int location)
        {
            var l = new Opt[6];
            uint tex = location == CC.DecalLeftDoor ? 0xfe957f48u : 0x2e40eadbu;
            for (int i = 0; i < 6; i++) l[i] = new Opt(tex + (uint)i, 0x7d212cfa + (uint)i, CC.DecalSlot1 + i);
            return l;
        }

        /// <summary>HUDLayerOption slots (CustomizeHUDColor::Setup, CC:3143): needle, lettering, gauge face.</summary>
        public static readonly (int slot, uint tex, uint name)[] HudLayers =
        {
            (ShopRules.Slot.HudNeedle, 0x70f56628, 0xe18ddce1), (ShopRules.Slot.HudCharacter, 0xbf6682c9, 0xe18ddce0), (ShopRules.Slot.HudBacking, 0xcc9e1ce4, 0xe18ddcdf),
        };

        /// <summary>Titles of the sub menus (CustomizeSub::Setup*, lower-cased by DrawTitle).</summary>
        public static uint SubTitle(int cat, bool backroom)
        {
            switch (cat)
            {
                case CC.Parts: return backroom ? 0x5d285ae7u : 0x055dce1au;
                case CC.Performance: return backroom ? 0xbfd5b50fu : 0xbaef8282u;
                case CC.Visual: return backroom ? 0x10c3fe31u : 0xbfa7d7c4u;
                case CC.RimBrands: return 0xe032d89e;
                case CC.VinylTypes: return 0xda129752;
                case CC.DecalLocation: return 0x9de6e6e1;
            }
            if (CC.IsDecalLocation(cat)) return 0x74d1887d;
            return 0;
        }

        // ---- the part screens' slot / icon / title per category (CustomizeParts::Setup CC:2583, Spoiler 2940, Rims 3349, Decals 4159)
        public static int SlotOfCategory(int cat) => cat switch
        {
            CC.BodyKit => ShopRules.Slot.Body, CC.Hoods => ShopRules.Slot.Hood, CC.RoofScoops => ShopRules.Slot.Roof, CC.CustomHud => ShopRules.Slot.CustomHud,
            CC.WindowTint => ShopRules.Slot.WindowTint, CC.Spoilers => ShopRules.Slot.Spoiler, CC.Paint => ShopRules.Slot.BasePaint, CC.RimPaint => ShopRules.Slot.PaintRim,
            CC.Numbers => ShopRules.Slot.RightDoorLNumber, _ => CC.IsVinylGroup(cat) ? ShopRules.Slot.VinylLayer : CC.IsRimBrand(cat) || cat == CC.RimBrandStock ? ShopRules.Slot.FrontWheel : 0,
        };

        /// <summary>The CarPartDB group number of a vinyl group category (0..7).</summary>
        public static int VinylGroupOf(int cat) => cat - CC.VinylGroupFlame;

        /// <summary>CustomizeParts::Setup: title and icon per category (carbon fibre hoods / roofs swap icons).</summary>
        public static (uint title, uint icon, uint carbonIcon) PartsScreenLook(int cat, bool backroom)
        {
            switch (cat)
            {
                case CC.BodyKit: return (0x6134c218, backroom ? 0xaf393dbau : 0x028c24f6u, 0);
                case CC.Hoods: return (0x4d4a88d, backroom ? 0xf375276eu : 0x028f7092u, backroom ? 0x2478e136u : 0x68495926u);
                case CC.RoofScoops: return (0x61e8f83c, backroom ? 0x25a4375eu : 0x79165861u, backroom ? 0xcd6b4e26u : 0xfc618215u);
                case CC.CustomHud: return (0x78980a6b, backroom ? 0x8ba602fcu : 0x028f88bcu, 0);
                case CC.WindowTint: return (0xd32729a6, 0x3f23165c, 0);
            }
            if (CC.IsVinylGroup(cat)) { var o = VinylGroupOpts[VinylGroupOf(cat)]; return (o.Name, o.Tex, 0); }
            return (0, 0, 0);
        }

        /// <summary>CustomizeDecals::Setup titles (CC:4169) and GetSlotIDFromCategory (CC:4032).</summary>
        public static uint DecalTitle(int cat) => CC.IsDecalSlot(cat) ? 0x7d212cfa + (uint)(cat - CC.DecalSlot1) : cat switch
        {
            CC.DecalWindshield => 0x301dedd3, CC.DecalRearWindow => 0x48e6ca49, CC.DecalLeftQp => 0x8a7697d6, CC.DecalRightQp => 0xb1f9b0c9, _ => 0u,
        };

        public static int DecalSlot(int location, int cat)
        {
            switch (location)
            {
                case CC.DecalWindshield: return ShopRules.Slot.DecalFrontWindow;
                case CC.DecalRearWindow: return ShopRules.Slot.DecalRearWindow;
                case CC.DecalLeftDoor: if (CC.IsDecalSlot(cat)) return ShopRules.Slot.DecalLeftDoor + (cat - CC.DecalSlot1); goto case CC.DecalRightDoor;
                case CC.DecalRightDoor: if (CC.IsDecalSlot(cat)) return ShopRules.Slot.DecalRightDoor + (cat - CC.DecalSlot1); goto case CC.DecalLeftQp;
                case CC.DecalLeftQp: return ShopRules.Slot.DecalLeftQuarter;
                case CC.DecalRightQp: return ShopRules.Slot.DecalRightQuarter;
            }
            return ShopRules.Slot.DecalFrontWindow;
        }

        // ---- IsCategoryLocked / IsCategoryNew (CM:740-1180) --------------------------------------------------------
        static int Entity(int cat)
        {
            switch (cat)
            {
                case CC.BodyKit: return ShopRules.U_BodyKit;
                case CC.Spoilers: return ShopRules.U_Spoilers;
                case CC.Hoods: return ShopRules.U_Hoods;
                case CC.RoofScoops: return ShopRules.U_RoofScoops;
                case CC.CustomHud: return ShopRules.U_CustomHud;
                case CC.Engine: return ShopRules.U_Engine;
                case CC.Transmission: return ShopRules.U_Transmission;
                case CC.Suspension: return ShopRules.U_Chassis;
                case CC.Nitrous: return ShopRules.U_Nos;
                case CC.Tires: return ShopRules.U_Tires;
                case CC.Brakes: return ShopRules.U_Brakes;
                case CC.ForcedInduction: return ShopRules.U_Induction;
                case CC.Paint: return ShopRules.U_PaintableBody;
                case CC.RimPaint: return ShopRules.U_PaintableRims;
                case CC.WindowTint: return ShopRules.U_WindowTint;
                case CC.Numbers: return ShopRules.U_DecalNumbers;
                case CC.DecalWindshield: case CC.DecalRearWindow: return ShopRules.U_DecalWindshield;
                case CC.DecalLeftQp: case CC.DecalRightQp: return ShopRules.U_DecalLeftQp;
            }
            if (CC.IsVinylGroup(cat)) return 0x23 + (cat - CC.VinylGroupFlame);
            if (CC.IsRimBrand(cat)) return 0x19 + (cat - CC.RimBrand5Zigen);
            if (cat == CC.DecalLeftDoor || cat == CC.DecalRightDoor || CC.IsDecalSlot(cat)) return ShopRules.U_DecalLeftDoor;
            return -1;
        }

        static int PerfTypeOf(int cat) => cat switch
        {
            CC.Engine => ShopRules.Engine, CC.Transmission => ShopRules.Transmission, CC.Suspension => ShopRules.Chassis, CC.Nitrous => ShopRules.Nos,
            CC.Tires => ShopRules.Tires, CC.Brakes => ShopRules.Brakes, CC.ForcedInduction => ShopRules.Induction, _ => -1,
        };

        public static int PerfType(int cat) => PerfTypeOf(cat);

        /// <summary>CarCustomizeManager::IsCategoryLocked (CM:916).</summary>
        public static bool IsCategoryLocked(int cat, bool backroom)
        {
            int level = 0;
            switch (cat)
            {
                case CC.Parts: for (int i = CC.BodyKit; i <= CC.RoofScoops; i++) if (!IsCategoryLocked(i, backroom)) return false; return true;
                case CC.Performance: for (int i = CC.Engine; i <= CC.ForcedInduction; i++) if (!IsCategoryLocked(i, backroom)) return false; return true;
                case CC.Visual: for (int i = CC.Paint; i <= CC.CustomHud; i++) if (!IsCategoryLocked(i, backroom)) return false; return true;
                case CC.RimBrands: for (int i = CC.RimBrand5Zigen; i <= CC.RimBrandRoja; i++) if (!IsRimCategoryLocked(i, backroom)) return false; return true;
                case CC.VinylTypes: for (int i = CC.VinylGroupFlame; i <= CC.VinylGroupContest; i++) if (!IsVinylCategoryLocked(i, backroom)) return false; return true;
                case CC.DecalLocation:
                    return IsCategoryLocked(CC.DecalWindshield, backroom) && IsCategoryLocked(CC.DecalLeftQp, backroom) && IsCategoryLocked(CC.DecalLeftDoor, backroom);
                case CC.DecalWindshield: case CC.DecalRearWindow: level = 1; break;
                case CC.DecalLeftQp: case CC.DecalRightQp: level = 3; break;
            }
            if (CC.IsVinylGroup(cat)) return IsVinylCategoryLocked(cat, backroom);
            if (CC.IsRimBrand(cat)) return IsRimCategoryLocked(cat, backroom);
            if (cat == CC.DecalLeftDoor || cat == CC.DecalRightDoor || CC.IsDecalSlot(cat)) level = 2;
            int perf = PerfTypeOf(cat);
            if (perf >= 0 && backroom && !CarParts.CanInstallJunkman(ShopCart.Car, perf)) return true;
            int ent = Entity(cat);
            if (ent < 0) return true;
            if (backroom) return !IsBackroomAvailable(ent, level);
            return !ShopUnlocks.IsUnlocked(ent, level, false);
        }

        /// <summary>CareerUnlocker::IsBackroomAvailable (US:398-511): a marker of the entity's type is owned (vinyls / rims also need one of their own).</summary>
        static bool IsBackroomAvailable(int ent, int level)
        {
            int m = ShopRules.EntityMarker(ent);
            return m != 0 && ShopMarkers.Has(m);
        }

        /// <summary>IsRimCategoryLocked (CM:1078): no unlocked part of that brand, or no RIMS marker in the backroom.</summary>
        public static bool IsRimCategoryLocked(int cat, bool backroom)
        {
            uint brand = RimBrandHash(cat);
            var car = ShopCart.Car;
            bool locked = true;
            if (car != null)
                foreach (var p in CarParts.PartList(car.Car, ShopRules.Slot.FrontWheel, brand))
                    if (p.Brand == brand && !ShopCart.IsLocked(SelectablePart.Visual(ShopRules.Slot.FrontWheel, p))) { locked = false; break; }
            if (backroom && !locked) locked = !ShopMarkers.Has(ShopRules.M_Rims);
            return locked;
        }

        /// <summary>IsVinylCategoryLocked (CM:1133).</summary>
        public static bool IsVinylCategoryLocked(int cat, bool backroom)
        {
            int group = VinylGroupOf(cat);
            var car = ShopCart.Car;
            bool locked = true;
            if (car != null)
                foreach (var p in CarParts.PartList(car.Car, ShopRules.Slot.VinylLayer, 0, group))
                    if (p.Group == group && !ShopCart.IsLocked(SelectablePart.Visual(ShopRules.Slot.VinylLayer, p))) { locked = false; break; }
            if (backroom && !locked) locked = !ShopMarkers.Has(ShopRules.M_Vinyl);
            return locked;
        }

        /// <summary>CarCustomizeManager::IsCategoryNew (CM:740): UnlockSystem::IsUnlockableNew(entity, -2) = any new level (US:608).</summary>
        public static bool IsCategoryNew(int cat)
        {
            switch (cat)
            {
                case CC.Parts: for (int i = CC.BodyKit; i <= CC.RoofScoops; i++) if (IsCategoryNew(i)) return true; return false;
                case CC.Performance: for (int i = CC.Engine; i <= CC.ForcedInduction; i++) if (IsCategoryNew(i)) return true; return false;
                case CC.Visual: for (int i = CC.Paint; i <= CC.CustomHud; i++) if (IsCategoryNew(i)) return true; return false;
                case CC.RimBrands: for (int i = CC.RimBrand5Zigen; i <= CC.RimBrandRoja; i++) if (IsCategoryNew(i)) return true; return false;
                case CC.VinylTypes: for (int i = CC.VinylGroupFlame; i <= CC.VinylGroupContest; i++) if (IsCategoryNew(i)) return true; return false;
                case CC.DecalLocation: return IsCategoryNew(CC.DecalWindshield) || IsCategoryNew(CC.DecalLeftQp) || IsCategoryNew(CC.DecalLeftDoor);
            }
            int ent = Entity(cat);
            if (ent < 0) return true;
            var p = GameSession.Profile;
            return p != null && ent < p.UnlockNew.Count && p.UnlockNew[ent] > 0;
        }

        // ---- performance package texts (CustomizePerformance::GetPerfPkgDesc / GetPerfPkgBrand, CC:4652-4865) ----------
        static readonly string[] PdNames = { "PD_TIRES_%d_%d", "PD_BRAKES_%d_%d", "PD_CHASSIS_%d_%d", "PD_TRANSMISSION_%d_%d", "PD_ENGINE_%d_%d", null, "PD_NITROUS_%d_%d" };
        static readonly uint[] PdStock = { 0xe5c1020c, 0x927db4fd, 0x8c96b853, 0x2f525e4f, 0xe74dedbb, 0, 0x9a0ef8f9 };

        public static uint PerfPkgDesc(int type, int level, int line, bool turbo)
        {
            if (type < 0 || type >= ShopRules.PerfCount) return 0;
            if (level == 0) return type == ShopRules.Induction ? (turbo ? 0x5317eb31u : 0x704a6d50u) : PdStock[type];
            if (type == ShopRules.Induction) return FrontEndData.Hash((turbo ? "PD_TURBO_" : "PD_SUPERCHARGER_") + level + "_" + line);
            if (type == ShopRules.Engine)
            {
                if (IsCastrolCar() && level == 4 && line == 3) return FrontEndData.Hash("PD_ENGINE_4_3_CASTROL");
                if (IsRotaryCar() && (level == 2 || level == 4) && line == 1) return FrontEndData.Hash($"PD_ENGINE_{level}_1_ROTARY");
            }
            return FrontEndData.Hash(PdNames[type].Replace("%d_%d", level + "_" + line));
        }

        static readonly string[] BrandFields = { "p_tires_", "p_brakes_", "p_suspension_", "p_transmission_", "p_engine_", "p_induction_", "p_nitrous_" };
        static readonly uint[] BrandStock = { 0xad6a0504, 0xa1a5e9e5, 0xad6a0504, 0x98ed935e, 0x7d0ac98f, 0x9e8f71ad, 0x98ed935e };

        /// <summary>The brand icon texture hash of description line `line` (0..2): the car's frontend `p_&lt;type&gt;_&lt;level&gt;[line]`.</summary>
        public static uint PerfPkgBrand(int type, int level, int line)
        {
            if (type < 0 || type >= ShopRules.PerfCount) return 0;
            if (type == ShopRules.Engine && IsCastrolCar() && level == 4 && line == 2) return 0xb95d4df;
            if (level == 0) return BrandStock[type];
            if (level < 1 || level > 4) return 0;
            var fe = FrontendOf(ShopCart.Car?.Car);
            if (fe == null || !fe.TryGet(BrandFields[type] + level, out int at, out int n, out _)) return 0;
            if (line < 0 || line >= n) return 0;
            uint h = Chunks.U32(fe.Source.Data, at + line * 4);
            return h == 0xFFFFFFFF ? 0 : h;
        }

        /// <summary>The frontend attribute collection of a car (FECarRecord.FEKey).</summary>
        public static AttribCollection FrontendOf(string car) => car == null ? null : CarParts.Attribs?.Get("frontend", car);

        // ---- car checks (CM:1319-1391) -------------------------------------------------------------------------------
        /// <summary>CarCustomizeManager::IsTurbo: Physics::Info::InductionType at induction level 1 (the port's CarSpec Spool &gt; 0).</summary>
        public static bool IsTurbo(string car = null)
        {
            car ??= ShopCart.Car?.Car ?? GameSession.ActiveCar?.Car;
            if (car == null) return false;
            var spec = CarSpec.FromAttributes(GameData.Attributes, car);
            return spec != null && spec.Spool > 0;
        }

        /// <summary>IsCastrolCar: the Ford GT with the Castrol easter egg (EASTER_EGG_CASTROL) [?: the port's "castrol" code awards the Castrol GT, read as the egg].</summary>
        public static bool IsCastrolCar() => string.Equals(ShopCart.Car?.Car, "fordgt", StringComparison.OrdinalIgnoreCase) && GameSession.Profile != null && GameSession.Profile.BonusCars.Contains("castrolgt");

        public static bool IsRotaryCar()
        {
            var c = ShopCart.Car?.Car ?? "";
            return c.Equals("rx7", StringComparison.OrdinalIgnoreCase) || c.Equals("rx8", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>GetMaxRPM rounded to the tach texture set (CC:2592-2600): &gt; 9500 → 10000, &gt; 8500 → 9000, &gt; 7500 → 8000, else 7000.</summary>
        public static int TachRpm(string car)
        {
            float rpm = car != null ? CarSpec.FromAttributes(GameData.Attributes, car)?.MaxRpm ?? 7000 : 7000;
            return rpm > 9500 ? 10000 : rpm > 8500 ? 9000 : rpm > 7500 ? 8000 : 7000;
        }

        public static readonly uint HudIndexAttr = EaHash.Hash("HUDINDEX"), NameAttr = EaHash.Hash("NAME"), SpeechColourAttr = EaHash.Hash("SPEECHCOLOUR");
        /// <summary>Applied attribute 0x6212682b of a vinyl: the number of remappable colours (CC:444, 2795, 3762).</summary>
        public const uint VinylColoursAttr = 0x6212682b;

        /// <summary>CHT_DEFAULT_FILL_COLOR / NEEDLE / LINE (CustomHUD defaults) [?: values from the HUD textures' tints are not in the decomp].</summary>
        public const uint DefaultFillColor = 0xFFFFFFFF, DefaultNeedleColor = 0xFFFF0000, DefaultLineColor = 0xFFFFFFFF;
    }

    /// <summary>Small FEng interface helpers on a view, by object hash (every object of that hash, as the packages repeat them).</summary>
    public static class FEngIf
    {
        public static void Printf(FEView v, uint hash, string text)
        {
            if (v == null) return;
            foreach (var n in v.Nodes)
            {
                if (n.Obj.Hash != hash) continue;
                if (n.Label != null) v.SetText(n, text);
                else foreach (var d in v.Descendants(n)) if (d.Label != null) v.SetText(d, text);
            }
        }

        public static void SetLanguageHash(FEView v, uint hash, uint lang) => Printf(v, hash, lang == 0 ? "" : FEDb.L(lang));

        public static void SetVisible(FEView v, uint hash, bool on)
        {
            if (v == null) return;
            foreach (var n in v.Nodes) if (n.Obj.Hash == hash) v.SetVisible(n, on);
        }

        public static void SetScript(FEView v, uint hash, uint script)
        {
            if (v == null) return;
            foreach (var n in v.Nodes) if (n.Obj.Hash == hash) v.Play(n, script);
        }

        public static bool HasScript(FEView v, uint hash, uint script)
        {
            if (v == null) return false;
            foreach (var n in v.Nodes) if (n.Obj.Hash == hash && n.Obj.Script(script) != null) return true;
            return false;
        }

        /// <summary>FEngSetTextureHash: a missing texture hides the image (FEView.SetTexture).</summary>
        public static void SetTextureHash(FEView v, uint hash, uint tex)
        {
            if (v == null) return;
            var t = FrontEndData.Texture(tex);
            foreach (var n in v.Nodes) if (n.Obj.Hash == hash && n.Image != null) v.SetTexture(n, t);
        }

        public static void SetTextureHash(FEView v, FEView.Node n, uint tex)
        {
            if (v == null || n == null) return;
            v.SetTexture(n, FrontEndData.Texture(tex));
        }

        /// <summary>FEngSetColor(obj, 0xAARRGGBB).</summary>
        public static void SetColor(FEView v, uint hash, uint argb)
        {
            if (v == null) return;
            foreach (var n in v.Nodes) if (n.Obj.Hash == hash) SetColor(n, argb);
        }

        public static void SetColor(FEView.Node n, uint argb)
        {
            if (n == null) return;
            n.Data.A = (argb >> 24) & 0xFF; n.Data.R = (argb >> 16) & 0xFF; n.Data.G = (argb >> 8) & 0xFF; n.Data.B = argb & 0xFF;
        }

        public static uint Argb(int r, int g, int b) => 0xFF000000u | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF);

        /// <summary>FEngGetTopLeft of an image (centre − size / 2) in its parent's space.</summary>
        public static Vector2 TopLeft(FEView.Node n) => n == null ? Vector2.zero : new Vector2(n.Data.X - Mathf.Abs(n.Data.SizeX) * 0.5f, n.Data.Y - Mathf.Abs(n.Data.SizeY) * 0.5f);

        public static void SetTopLeft(FEView.Node n, float x, float y)
        {
            if (n == null) return;
            n.Data.X = x + Mathf.Abs(n.Data.SizeX) * 0.5f;
            n.Data.Y = y + Mathf.Abs(n.Data.SizeY) * 0.5f;
        }

        /// <summary>"%$d": thousands separators (FEPrintf).</summary>
        public static string D(int v) => v.Grouped();
    }

    /// <summary>
    /// CustomizeMeter (CarCustomize.cpp:212-300): the shop's heat meter group 0xF2492598 — HEAT_BASE_LED_1..10 play OFF /
    /// COOL / ON by the current and previewed heat within the current stage, the multiplier images 0x5FFEE1D8 /
    /// 0xE637955C show HEAT_X&lt;stage&gt; and the zoom copy plays ZOOM (0x209C24) when the preview changes.
    /// </summary>
    public sealed class CustomizeMeter
    {
        const int NumStageSegments = 10;
        float _min, _max = 1, _current, _preview, _previousPreview;
        FEView _view;
        FEView.Node _multiplier, _multiplierZoom, _group;
        readonly FEView.Node[] _bases = new FEView.Node[NumStageSegments];
        public const uint ScriptOff = 0xccfa, ScriptCool = 0x13ff6c, ScriptOn = 0x63c, ScriptZoom = 0x209c24;

        public void Init(FEView view, float min, float max, float current, float preview)
        {
            _view = view; _min = min; _max = max;
            SetCurrent(current); SetPreview(preview);
            _multiplier = view?.Find(0x5ffee1d8);
            _multiplierZoom = view?.Find(0xe637955c);
            _group = view?.Find(0xf2492598);
            for (int i = 0; i < NumStageSegments; i++) _bases[i] = view?.Find("HEAT_BASE_LED_" + (i + 1));
        }

        public void SetCurrent(float current) => _current = Mathf.Min(Mathf.Max(current, _min), _max);

        public void SetPreview(float preview)
        {
            _previousPreview = _preview;
            _preview = Mathf.Min(Mathf.Max(preview, _min), _max);
        }

        /// <summary>CustomizeMeter::Draw (UNSOLVED in the decomp, read literally).</summary>
        public void Draw()
        {
            if (_view == null) return;
            const float stageSize = 1f;
            float multiplier = stageSize, stageBottom = _min;
            while (_preview - stageBottom >= stageSize) { multiplier += stageSize; stageBottom += stageSize; }
            multiplier = Mathf.Min(multiplier, 5f);
            var tex = FrontEndData.TextureByName($"HEAT_X{multiplier:0}");
            if (_multiplier != null && tex) _view.SetTexture(_multiplier, tex);
            if (_multiplierZoom != null && tex) _view.SetTexture(_multiplierZoom, tex);
            if (_preview != _previousPreview && _multiplierZoom != null) _view.Play(_multiplierZoom, ScriptZoom);
            float segmentSize = stageSize * 0.1f;
            int curIcon = 10;
            float currentSegment = stageBottom + stageSize;
            if (currentSegment >= stageBottom)
            {
                do
                {
                    currentSegment -= segmentSize;
                    curIcon--;
                    uint script = currentSegment + 0.0005f >= _current ? ScriptOff : currentSegment + 0.0005f >= _preview ? ScriptCool : ScriptOn;
                    if (curIcon >= 0 && curIcon < NumStageSegments && _bases[curIcon] != null) _view.Play(_bases[curIcon], script);
                } while (currentSegment >= stageBottom && curIcon > 0);
            }
        }

        public void SetVisibility(bool on)
        {
            if (_group != null) _view.SetVisible(_group, on);
        }
    }

    /// <summary>
    /// TwoStageSlider (Common/Slider.cpp:163-230): BASE_/FILLBAR_/HANDLE_/PREVIEWBAR_/VALUE_&lt;name&gt;; the fill bar is the
    /// current rating over `range`, the handle sits at the preview rating.
    /// </summary>
    public sealed class TwoStageSlider
    {
        FEView _view;
        FEView.Node _base, _fill, _handle, _value, _preview;
        float _min, _max = 1, _cur, _previewValue, _range;
        float _fillU0, _fillU1, _fillW;

        public void Init(FEView view, string name, float min, float max, float inc, float cur, float preview, float range)
        {
            _view = view;
            _base = view.Find("BASE_" + name); _fill = view.Find("FILLBAR_" + name); _handle = view.Find("HANDLE_" + name);
            _value = view.Find("VALUE_" + name); _preview = view.Find("PREVIEWBAR_" + name);
            if (_fill != null) { _fillU0 = _fill.Data.U0; _fillU1 = _fill.Data.U1; _fillW = _fill.Data.SizeX; }
            _min = min; _max = max; _range = range;
            _cur = Mathf.Min(Mathf.Max(cur, min), max);
            _previewValue = Mathf.Min(Mathf.Max(min, preview), max);
        }

        public void SetValue(float v) => _cur = Mathf.Min(Mathf.Max(v, _min), _max);
        public void SetPreview(float v) => _previewValue = Mathf.Min(Mathf.Max(v, _min), _max);

        /// <summary>TwoStageSlider::Draw (UNSOLVED): fill = range × pct at the base's top-left, handle at the preview position.</summary>
        public void Draw()
        {
            if (_view == null || _base == null) return;
            float d = _max - _min;
            if (d == 0) { _max = 1; _min = 0; d = 1; }
            float pct = (_cur - _min) / d, fill = pct * _range;
            var tl = FEngIf.TopLeft(_base);
            if (_fill != null)
            {
                _fill.Data.SizeX = fill;
                FEngIf.SetTopLeft(_fill, tl.x, tl.y);
                // FEngSetBottomRightU(fill, fill_size): LowerRight.x = fill / the texture's width (FEngSetBottomRightUV :666)
                var tex = _fill.Image != null ? _fill.Image.texture : null;
                if (tex != null && tex.width > 0) _fill.Data.U1 = fill / tex.width;
                else if (_fillW > 0) _fill.Data.U1 = _fillU0 + (_fillU1 - _fillU0) * Mathf.Clamp01(fill / _fillW);
            }
            float previewPct = (_previewValue - _min) / d, previewPos = _range * previewPct;
            if (_handle != null) FEngIf.SetTopLeft(_handle, tl.x + previewPos, tl.y);
            if (_value != null) _view.SetText(_value, ((int)_cur).ToString());
            if (_preview != null)
            {
                _preview.Data.SizeX = previewPos;
                var ptex = _preview.Image != null ? _preview.Image.texture : null;
                if (ptex != null && ptex.width > 0) _preview.Data.U1 = previewPos / ptex.width;   // FEngSetBottomRightU(preview, pct × range)
                FEngIf.SetTopLeft(_preview, tl.x, tl.y);
            }
        }
    }

    /// <summary>
    /// CustomizationScreenHelper (CarCustomize.cpp:2084-2255): the part screens' shared header — lower-cased title in
    /// TITLE_GROUP, the career status icon 0xCFFB7033 (locked 0xF0574BB2 / new 0xCFFB7033), the player car status icon
    /// 0xD0582FEB (installed 0x696AE039 / in cart 0x1A777E25), the cash box 0x8D1559A4, the unlock overlay 0xEBC3E6B7 with
    /// blurb 0xA6298E25, the part counter 0x6F25A248 / 0xB2037BDC and the heat meter.
    /// </summary>
    public sealed class CustomizeHelper
    {
        readonly FEView _v;
        public readonly CustomizeMeter HeatMeter = new CustomizeMeter();
        public bool InitComplete;
        bool _overlayShowing;
        public uint TitleHash;
        public const uint StatusLockedTex = 0xf0574bb2, StatusNewTex = 0xcffb7033, StatusInstalledTex = 0x696ae039, StatusInCartTex = 0x1a777e25,
            ScriptBlink = 0x0298d54f /* BLINK [?] */, ScriptFlash = 0x280164f;

        public CustomizeHelper(FEView view)
        {
            _v = view;
            HeatMeter.Init(view, 1f, 5f, ShopCart.ActualHeat, ShopCart.CartHeat);
        }

        /// <summary>DrawTitle: the localised title lower-cased (not in German / Finnish) into TITLE_GROUP 0xB71B576D.</summary>
        public void DrawTitle()
        {
            string s = TitleHash != 0 ? FEDb.L(TitleHash) : "";
            FEngIf.Printf(_v, 0xB71B576D, s.ToLowerInvariant());
        }

        public void SetCareerStatusIcon(int state)
        {
            switch (state & CPS.GameMask)
            {
                case CPS.Available: FEngIf.SetScript(_v, 0xcffb7033, FEView.Hide); FEngIf.SetVisible(_v, 0xcffb7033, false); break;
                case CPS.Locked:
                    FEngIf.SetVisible(_v, 0xcffb7033, true);
                    FEngIf.SetTextureHash(_v, 0xcffb7033, StatusLockedTex);
                    if (FEngIf.HasScript(_v, 0xcffb7033, FEView.Appear)) FEngIf.SetScript(_v, 0xcffb7033, FEView.Appear);
                    break;
                case CPS.New:
                    FEngIf.SetVisible(_v, 0xcffb7033, true);
                    FEngIf.SetTextureHash(_v, 0xcffb7033, StatusNewTex);
                    if (FEngIf.HasScript(_v, 0xcffb7033, FrontEndData.Hash("BLINK"))) FEngIf.SetScript(_v, 0xcffb7033, FrontEndData.Hash("BLINK"));
                    break;
            }
        }

        public void SetPlayerCarStatusIcon(int state)
        {
            switch (state & CPS.PlayerMask)
            {
                case CPS.Installed: FEngIf.SetVisible(_v, 0xd0582feb, true); FEngIf.SetTextureHash(_v, 0xd0582feb, StatusInstalledTex); break;
                case CPS.InCart: FEngIf.SetVisible(_v, 0xd0582feb, true); FEngIf.SetTextureHash(_v, 0xd0582feb, StatusInCartTex); break;
                default: FEngIf.SetVisible(_v, 0xd0582feb, false); break;
            }
        }

        public void SetCashVisibility(bool visible) => FEngIf.SetVisible(_v, 0x8d1559a4, visible);

        public void SetUnlockOverlayState(bool show, uint blurb)
        {
            _overlayShowing = show;
            if (show) { FEngIf.SetLanguageHash(_v, 0xa6298e25, blurb); FEngIf.SetVisible(_v, 0xebc3e6b7, true); }
            FEngIf.SetScript(_v, 0xebc3e6b7, show ? FEView.Appear : FEHash.Leave);
            if (!show && !FEngIf.HasScript(_v, 0xebc3e6b7, FEHash.Leave)) FEngIf.SetVisible(_v, 0xebc3e6b7, false);
        }

        /// <summary>SetCareerStuff (CC:2176): backroom marker lines or the cost / cash / total / trade-in box, then the heat meter.</summary>
        public void SetCareerStuff(SelectablePart part, int cat, int tradeInValue)
        {
            if (ShopCart.Career)
            {
                if (ShopCart.Backroom)
                {
                    FEngIf.SetLanguageHash(_v, 0x63ca8308, CustomizeTables.MarkerNameHash(cat));
                    FEngIf.Printf(_v, 0x23d918fe, "1");
                    FEngIf.Printf(_v, 0x83e3cd39, FEngIf.D(CustomizeTables.NumMarkers(cat)));
                }
                else
                {
                    if (part != null) FEngIf.Printf(_v, 0xdbb80edd, FEngIf.D(part.Price));
                    else FEngIf.Printf(_v, 0xdbb80edd, ShopCart.TempColoredPart != null ? FEngIf.D(ShopCart.TempColoredPart.Price) : "-1");
                    FEngIf.Printf(_v, 0xc60adcfd, FEngIf.D(GameSession.Profile?.Cash ?? 0));
                    FEngIf.Printf(_v, 0x7a6d2f71, FEngIf.D(ShopCart.Total));
                    FEngIf.Printf(_v, 0xa91eda8a, FEngIf.D(tradeInValue));
                }
                HeatMeter.SetCurrent(ShopCart.ActualHeat);
                HeatMeter.SetPreview(ShopCart.PreviewHeat(part));
                HeatMeter.Draw();
            }
            else
            {
                SetCareerStatusIcon(CPS.Available);
                SetCashVisibility(false);
                HeatMeter.SetVisibility(false);
                FEngIf.SetVisible(_v, 0x24c6bfad, false);
                FEngIf.SetVisible(_v, 0xea903012, false);
            }
        }

        /// <summary>SetPartStatus (CC:2208): the two status icons, the unlock overlay for a locked part, "n / max".</summary>
        public void SetPartStatus(SelectablePart part, int state, uint unlockBlurb, int partNum, int maxParts)
        {
            if (part != null)
            {
                SetPlayerCarStatusIcon(state);
                if ((state & CPS.GameMask) == CPS.Locked && unlockBlurb != 0)
                {
                    if (InitComplete) SetUnlockOverlayState(true, unlockBlurb);
                    SetCareerStatusIcon(CPS.Locked);
                }
                else
                {
                    if (_overlayShowing) SetUnlockOverlayState(false, 0);
                    SetCareerStatusIcon((state & CPS.GameMask) == CPS.New ? CPS.New : CPS.Available);
                }
            }
            FEngIf.Printf(_v, 0x6f25a248, FEngIf.D(partNum));
            FEngIf.Printf(_v, 0xb2037bdc, FEngIf.D(maxParts));
        }

        /// <summary>FlashStatusIcon (CC:2237): script 0x280164F on the icon and UISND_COMMON_WRONG.</summary>
        public void FlashStatusIcon(int state, bool playSound)
        {
            uint obj = (state & CPS.GameMask) == CPS.Locked ? 0xcffb7033u : (state & CPS.PlayerMask) != 0 ? 0xd0582febu : 0;
            if (obj != 0) FEngIf.SetScript(_v, obj, ScriptFlash);
            if (playSound) NfsMw.Audio.FeSound.Play(NfsMw.Audio.FeSoundId.CommonWrong);
        }

        public void PlayLocked() => FlashStatusIcon(CPS.Locked, true);
        public void PlayInCart() => FlashStatusIcon(CPS.InCart, true);
        public void PlayInstalled() => FlashStatusIcon(CPS.Installed, true);
    }

    /// <summary>
    /// ArrayScroller (Common/feArrayScrollerMenu.cpp): a grid of slot images (COLOR_n, CAR_ICON_n …) over a longer list of
    /// data; the current datum's slot plays HIGHLIGHT, the others UNHIGHLIGHT / INIT (GREY when disabled). ScrollHor wraps
    /// within the row, ScrollVer moves a row (and the window when the package has an ARRAY_SCROLL_REGION).
    /// </summary>
    public sealed class ArrayScroller
    {
        public sealed class Datum
        {
            public object Tag;
            public uint Hash, Desc;
            public bool Enabled = true, GreyedOut;
        }

        readonly FEView _v;
        readonly List<FEView.Node> _slots = new List<FEView.Node>();
        readonly List<Datum> _data = new List<Datum>();
        readonly int _width, _height;
        readonly bool _hasScrollRegion;
        int _start;
        Datum _current;
        public bool ShouldPlaySound;
        public uint DescLabel;

        public ArrayScroller(FEView view, int w, int h)
        {
            _v = view; _width = Math.Max(1, w); _height = Math.Max(1, h);
            _hasScrollRegion = view.Find("ARRAY_SCROLL_REGION") != null;
        }

        public void AddSlot(FEView.Node slot) { if (slot != null) _slots.Add(slot); }
        public int NumSlots => _slots.Count;
        public int NumDatum => _data.Count;
        public int Width => _width;
        public int Height => _height;
        public Datum Current => _current;
        public int CurrentIndex => _current == null ? -1 : _data.IndexOf(_current);
        public int Start => _start;
        public IReadOnlyList<Datum> Data => _data;

        public void AddDatum(Datum d)
        {
            _data.Add(d);
            if (_current == null) SetSelection(d, 0);
        }

        public FEView.Node SlotAt(int i) => i >= 0 && i < _slots.Count ? _slots[i] : null;
        public Datum DatumAt(int i) => i >= 0 && i < _data.Count ? _data[i] : null;
        public FEView.Node CurrentSlot => SlotAt(CurrentIndex - _start);

        public void ClearData() { _data.Clear(); _start = 0; _current = null; }

        void SetSelection(Datum d, int start)
        {
            if (d == null || !d.Enabled) return;
            _start = start;
            _current = d;
        }

        /// <summary>ArrayScroller::RefreshHeader: every slot shows the datum at start + i (ArraySlot::Update).</summary>
        public void RefreshHeader()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var d = DatumAt(_start + i);
                var s = _slots[i];
                if (d == null) { _v.SetVisible(s, false); continue; }
                _v.SetVisible(s, true);
                bool sel = d == _current;
                if (d.GreyedOut || !d.Enabled) { if (!_v.IsScriptSet(s, FEView.Grey)) _v.Play(s, FEView.Grey); }
                else if (sel) { if (!_v.IsScriptSet(s, FEView.Highlight)) _v.Play(s, FEView.Highlight); }
                else if (_v.IsScriptSet(s, FEView.Highlight)) _v.Play(s, FEView.UnHighlight);
                else if (!_v.IsScriptSet(s, FEView.Init)) _v.Play(s, FEView.Init);
                if (d.Hash != 0 && s.Image != null) { var t = FrontEndData.Texture(d.Hash); if (t) _v.SetTexture(s, t); }
            }
            if (_current != null && DescLabel != 0) FEngIf.SetLanguageHash(_v, DescLabel, _current.Desc);
        }

        /// <summary>ForceSelectionOnScreen (UNSOLVED, read literally).</summary>
        int ForceOnScreen(int newDatum, int start)
        {
            int ret = start;
            if (newDatum < start) ret = newDatum / _width * _width;
            else if (newDatum > start + _width * _height) ret = (newDatum / _width - (_height - 1)) * _width;
            return ret;
        }

        public void ScrollHor(bool next)
        {
            if (_data.Count == 0) return;
            int cur = CurrentIndex, ni = cur;
            if (!next)
            {
                ni = ni - 1;
                if (ni + 1 == (ni + 1) / _width * _width) ni += _width;
                if (ni >= _data.Count) ni = _data.Count - 1;
            }
            else
            {
                ni = cur + 1;
                if (ni == ni / _width * _width) ni -= _width;
                if (ni >= _data.Count) ni = ni / _width * _width;
            }
            if (ni < 0) ni = 0;
            var nd = DatumAt(ni);
            if (nd != null && nd != _current) { SetSelection(nd, ForceOnScreen(ni, _start)); ShouldPlaySound = true; }
            RefreshHeader();
        }

        public void ScrollVer(bool next)
        {
            if (_data.Count == 0) return;
            int cur = CurrentIndex, ni = cur, ns = _start;
            if (!next)
            {
                ni -= _width;
                if (!_hasScrollRegion)
                {
                    if (ni >= (_height - 1) * _width && ni < _height * _width) ni -= (_height - 1) * _width;
                    ns -= _width;
                }
                else if (ni < ns) ns -= _width;
                if (ni < 0) ni = cur;   // the exe walks GetPrev only while new_index > -1 (stays)
            }
            else
            {
                ni += _width;
                if (!_hasScrollRegion)
                {
                    if (ni >= _width && ni < _width * 2) ni += (_height - 1) * _width;
                    ns += _width;
                }
                else
                {
                    if (_data.Count <= ni && (cur / _width + 1) * _height < ((_data.Count - 1) / _width + 1) * _height) ni = _data.Count - 1;
                    if (ns + _slots.Count <= ni) ns += _width;
                }
                if (ni >= _data.Count) ni = cur;
            }
            if (ns < 0) ns = 0;
            var nd = DatumAt(ni);
            if (nd != null && nd != _current) { SetSelection(nd, Math.Max(0, ForceOnScreen(ni, ns))); ShouldPlaySound = true; }
            RefreshHeader();
        }

        /// <summary>SetInitialPosition (:255).</summary>
        public void SetInitialPosition(int index)
        {
            if (index < 0 || index >= _data.Count) return;
            int newStart = 0, size = _width * _height;
            if (index > size - 1)
            {
                int ni = (index / _width * _width - (size - 1)) / _width * _width;
                newStart = ni + _width;
                if (_width == 1) newStart--;
            }
            SetSelection(_data[index], Math.Max(0, newStart));
        }
    }
}
