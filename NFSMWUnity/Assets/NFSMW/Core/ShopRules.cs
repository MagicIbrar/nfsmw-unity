using System;
using System.Collections.Generic;
using System.Linq;

namespace NfsMw.Core
{
    /// <summary>
    /// The customization shop's rules and tables (specs/customization.md), Unity-free so the harness can check them
    /// against the data: package counts and tiers (CustomizeManager.cpp:400-419, 261-285), FE_ATTRIB part costs
    /// (LookupFEPartInfo, UnlockSystem.cpp:711), unlock entities and levels (UnlockSystem.cpp:19-143, 349-379, 773-860,
    /// 1101-1155), trade-in, heat factors (fecooling, VehicleDB.cpp:1326-1390) and Junkman multipliers
    /// (junkman/default, physics.md §2). Reads the CareerData database (attributes.bin + gameplay.bin + FE_ATTRIB.bin).
    /// </summary>
    public static class ShopRules
    {
        /// <summary>Physics::Upgrades::Type order (0x8F87A0) and the pvehicle/junkman field prefix of each.</summary>
        public static readonly string[] PerfTypes = { "tires", "brakes", "chassis", "transmission", "engine", "induction", "nos" };
        public const int Tires = 0, Brakes = 1, Chassis = 2, Transmission = 3, Engine = 4, Induction = 5, Nos = 6, PerfCount = 7;
        /// <summary>PPL_LEVEL_JUNKMAN: the Junkman package's level.</summary>
        public const int JunkmanLevel = 7;
        /// <summary>gTradeInFactor (CarCustomize.cpp:33).</summary>
        public const float TradeInFactor = 0.25f;
        /// <summary>CarCustomizeManager::GetMaxPackages (CM:400-419): the global tier count per type.</summary>
        public static int MaxPackages(int type) => type switch { Tires => 3, Brakes => 4, Chassis => 3, Transmission => 4, Engine => 4, Induction => 3, Nos => 3, _ => -1 };

        /// <summary>The car's package count N = pvehicle `&lt;type&gt;_upgrades` (Physics::Upgrades::GetMaxLevel).</summary>
        public static int NumPackages(AttribDatabase db, string car, int type)
        {
            var pv = db?.Get("pvehicle", car);
            string f = PerfTypes[type] + "_upgrades";
            return pv != null && pv.Has(f) ? Math.Max(0, pv.Int(f)) : 0;
        }

        /// <summary>Package i (1..N) is global tier M − N + i, used for its price and unlock (CM:261-285, 425-470).</summary>
        public static int Tier(int type, int numPackages, int level) => level >= JunkmanLevel ? level : MaxPackages(type) - numPackages + level;

        // ---- unlockable entities (eUnlockableEntity, FEReflected.hpp:19) ------------------------------------------
        public const int UnlockCount = 57;
        public const int U_Tires = 4, U_Brakes = 5, U_Chassis = 6, U_Transmission = 7, U_Engine = 8, U_Induction = 9, U_Nos = 10,
            U_BodyKit = 11, U_Spoilers = 12, U_RimBrands = 13, U_Hoods = 14, U_RoofScoops = 15, U_LicensePlate = 16, U_CustomHud = 17,
            U_WindowTint = 18, U_PaintableBody = 23, U_PaintableRims = 24, U_VinylsBody = 40, U_DecalNumbers = 43,
            U_DecalWindshield = 44, U_DecalLeftDoor = 46, U_DecalLeftQp = 48, U_DecalHood = 50;

        /// <summary>gMaxPartLevels (UnlockSystem.cpp:22-37).</summary>
        public static int MaxPartLevel(int entity) => entity switch
        {
            U_Tires => 3, U_Brakes => 4, U_Chassis => 3, U_Transmission => 4, U_Engine => 4, U_Induction => 3, U_Nos => 3,
            U_BodyKit => 4, U_Spoilers => 5, U_RimBrands => 6, U_Hoods => 6, U_RoofScoops => 6, U_CustomHud => 4,
            U_WindowTint => 4, U_PaintableBody => 3, U_VinylsBody => 6, _ => 0,
        };

        /// <summary>MapPerfPkgToUnlockable (UnlockSystem.cpp:842).</summary>
        public static int PerfUnlockable(int type) => type switch
        {
            Tires => U_Tires, Brakes => U_Brakes, Chassis => U_Chassis, Transmission => U_Transmission, Engine => U_Engine,
            Induction => U_Induction, Nos => U_Nos, _ => 0,
        };

        /// <summary>MapCarPartToUnlockable (UnlockSystem.cpp:773).</summary>
        public static int SlotUnlockable(int slot)
        {
            switch (slot)
            {
                case Slot.Body: return U_BodyKit;
                case Slot.Spoiler: return U_Spoilers;
                case Slot.Hood: return U_Hoods;
                case Slot.Roof: return U_RoofScoops;
                case 69: return U_LicensePlate;
                case Slot.CustomHud: return U_CustomHud;
                case Slot.WindowTint: return U_WindowTint;
                case Slot.FrontWheel: case 67: return U_RimBrands;
                case 105: case 106: case 113: case 114: return U_DecalNumbers;
                case 83: case 91: return U_DecalWindshield;
                case Slot.BasePaint: case Slot.PaintRim: return U_PaintableBody;
                case 115: case 123: return U_DecalLeftQp;
            }
            if ((slot >= 99 && slot <= 104) || (slot >= 107 && slot <= 112)) return U_DecalLeftDoor;
            if (slot >= Slot.VinylLayer && slot <= 82 && slot != Slot.PaintRim) return U_VinylsBody;
            return 0;
        }

        /// <summary>CAR_SLOT_ID values the shop uses (World/CarPartID.h).</summary>
        public static class Slot
        {
            public const int Body = 23, Spoiler = 44, Roof = 62, Hood = 63, FrontWheel = 66, RearWheel = 67, BasePaint = 76, VinylLayer = 77,
                PaintRim = 78, VinylColour0 = 79, DecalFrontWindow = 83, DecalRearWindow = 91, DecalLeftDoor = 99, LeftNumber = 105,
                RightNumber = 106, DecalRightDoor = 107, RightDoorLNumber = 113, RightDoorRNumber = 114, DecalLeftQuarter = 115,
                DecalRightQuarter = 123, WindowTint = 131, CustomHud = 132, HudBacking = 133, HudNeedle = 134, HudCharacter = 135;
        }

        /// <summary>Visual items that cost nothing and are hidden from the cart totals (CM:186-188, 290-298).</summary>
        public static bool FreeSlot(int slot) => (slot >= 79 && slot <= 82) || (slot >= 133 && slot <= 135);

        /// <summary>
        /// CarCustomizeManager::CanTradeIn (CM:56-84, UNSOLVED decomp read literally): never for performance; true for
        /// the model slots below BASE_PAINT (body kit, spoiler, roof, hood, rims) and a few decal texture slots that the
        /// shop never trades.
        /// </summary>
        public static bool CanTradeIn(int slot)
        {
            if (slot <= 0x73) return slot < 0x4C;
            if (slot == 0x7B) return false;
            if (slot < 0x7B) return true;
            if (slot > 0x87) return true;
            return slot < 0x83;
        }

        // ---- costs -------------------------------------------------------------------------------------------------

        /// <summary>
        /// LookupFEPartInfo (UnlockSystem.cpp:711): the frontend collection under "carparts" whose feCarPartName is the
        /// entity, entry of feCarPartInfo[] {i32 Level, f32 Rep, f32 Cost} with that Level; none → 0.
        /// </summary>
        public static int PartCost(AttribDatabase db, int entity, int level)
        {
            if (db == null || entity == 0) return 0;
            uint carparts = AttribDatabase.Hash("carparts");
            foreach (var c in db.OfClass("frontend"))
            {
                if (c.ParentKey != carparts || !c.TryGet("feCarPartName", out int at, out _, out _) || Chunks.I32(c.Source.Data, at) != entity) continue;
                if (!c.TryGet("feCarPartInfo", out int ia, out int n, out var def)) return 0;
                int stride = def.Size > 0 ? def.Size : 12;
                var src = c.Source.Data;
                for (int i = 0; i < n; i++)
                    if (Chunks.I32(src, ia + i * stride) == level) return (int)Chunks.F32(src, ia + i * stride + 8);
                return 0;
            }
            return 0;
        }

        /// <summary>A performance package's price: FECarPartInfo(map(type), tier) (CM:261-275); Junkman (tier ≥ 7) = 0.</summary>
        public static int PerfCost(AttribDatabase db, string car, int type, int level) =>
            PartCost(db, PerfUnlockable(type), Tier(type, NumPackages(db, car, type), level));

        /// <summary>A visual part's price: FECarPartInfo(map(slot), part level); vinyl / HUD colours are free (CM:276-284).</summary>
        public static int VisualCost(AttribDatabase db, int slot, int level) => FreeSlot(slot) ? 0 : PartCost(db, SlotUnlockable(slot), level);

        // ---- heat (fecooling/default, CM:494-553) --------------------------------------------------------------------

        /// <summary>The fecooling field a bought visual part multiplies the car's heat by (null = no change).</summary>
        public static string CoolingField(int slot)
        {
            switch (slot)
            {
                case Slot.Spoiler: return "NewSpoiler";
                case Slot.Hood: return "NewHood";
                case Slot.Roof: return "NewRoofScoop";
                case Slot.FrontWheel: return "NewRim";
                case Slot.WindowTint: return "NewWindowTint";
                case Slot.BasePaint: return "NewPaint";
                case Slot.PaintRim: return "NewRimPaint";
                case Slot.VinylLayer: return "NewVinyl";
                case Slot.Body: case Slot.LeftNumber: return "NewBodyKit";   // LNUMBER → body kit: "is this a bug?" (CM:530)
                case 83: case 91: case 115: case 123: return "NewDecal";
            }
            if ((slot >= 99 && slot <= 104) || (slot >= 107 && slot <= 112)) return "NewDecal";
            return null;
        }

        /// <summary>Heat multiplier for buying a visual part: fecooling/default.&lt;field&gt; × (backroom ? 0.75 : 1).</summary>
        public static float CoolingFactor(AttribDatabase db, int slot, bool backroom)
        {
            var f = CoolingField(slot);
            if (f == null) return 1f;
            var c = db?.Get("fecooling", "default");
            float v = c != null && c.Has(f) ? c.Float(f) : 1f;
            return v * (backroom ? 0.75f : 1f);
        }

        // ---- Junkman (junkman/default.<type>_package: JunkmanMod {Key class, Key field, float Scale}) --------------

        public static List<(uint cls, uint field, float scale)> JunkmanMods(AttribDatabase db, int type)
        {
            var list = new List<(uint, uint, float)>();
            var c = db?.Get("junkman", "default");
            if (c == null || !c.TryGet(PerfTypes[type] + "_package", out int at, out int n, out var def)) return list;
            int stride = def.Size >= 12 ? def.Size : 12;
            var src = c.Source.Data;
            for (int i = 0; i < n; i++)
                list.Add((Chunks.U32(src, at + i * stride), Chunks.U32(src, at + i * stride + 4), Chunks.F32(src, at + i * stride + 8)));
            return list;
        }

        // ---- unlocks from gameplay.bin -------------------------------------------------------------------------------

        /// <summary>ConvertBigBangUpgradeAward (UnlockSystem.cpp:1101): UpgradePartID → entity ("decals" → DECAL_HOOD).</summary>
        public static int UpgradeEntity(string partId) => partId switch
        {
            "brakes" => U_Brakes, "chassis" => U_Chassis, "engine" => U_Engine, "induction" => U_Induction, "nos" => U_Nos,
            "tires" => U_Tires, "transmission" => U_Transmission, "bodykit" => U_BodyKit, "decals" => U_DecalHood, "hood" => U_Hoods,
            "hud" => U_CustomHud, "numbers" => U_DecalNumbers, "paint" => U_PaintableBody, "rims" => U_RimBrands,
            "roofscoop" => U_RoofScoops, "spoiler" => U_Spoilers, "tint" => U_WindowTint, "vinyls" => U_VinylsBody, _ => 0,
        };

        /// <summary>
        /// AwardUnlockUpgrade (UnlockSystem.cpp:1134): the entity a gameplay upgrade item unlocks; decals level 1/2/3
        /// open the windshield / doors / quarter panels.
        /// </summary>
        public static int AwardEntity(string partId, int level)
        {
            int e = UpgradeEntity(partId);
            if (e == U_DecalHood) e = level switch { 1 => U_DecalWindshield, 2 => U_DecalLeftDoor, 3 => U_DecalLeftQp, _ => e };
            return e;
        }

        /// <summary>The upgrade items (UpgradePartID, UpgradeLevel) of race_bin_NN's BaselineUnlocks.</summary>
        public static List<(string part, int level)> BaselineUnlocks(AttribDatabase db, int bin)
        {
            var list = new List<(string, int)>();
            var c = db?.Get("gameplay", $"race_bin_{bin:00}");
            if (c == null) return list;
            uint gp = AttribDatabase.Hash("gameplay");
            foreach (var k in c.Keys("BaselineUnlocks"))
                if (db.Collections.TryGetValue((gp, k), out var u) && u.Has("UpgradePartID"))
                    list.Add((u.Text("UpgradePartID"), u.Int("UpgradeLevel")));
            return list;
        }

        // ---- markers (FEMarkerManager, UnlockSystem.hpp:87-161) -------------------------------------------------------

        /// <summary>ePossibleMarker by the names Profile.Markers stores (ConvertBigBangMarkerAward, UnlockSystem.cpp:1011).</summary>
        public static readonly string[] MarkerNames =
        {
            null, "brakes", "engine", "nos", "induction", "chassis", "tires", "transmission", "bodykit", "hood", "spoiler", "rims",
            "roofscoop", "hud", "vinyls", "decals", "paint", "out_of_jail_free", "pink_slip", "cash_bonus", "add_impound_box",
            "release_car_from_impound",
        };
        public const int M_Brakes = 1, M_Engine = 2, M_Nos = 3, M_Induction = 4, M_Chassis = 5, M_Tires = 6, M_Transmission = 7,
            M_Body = 8, M_Hood = 9, M_Spoiler = 10, M_Rims = 11, M_RoofScoop = 12, M_CustomHud = 13, M_Vinyl = 14, M_Decal = 15,
            M_Paint = 16, M_JailFree = 17, M_PinkSlip = 18, M_Cash = 19, M_ImpoundBox = 20, M_ImpoundRelease = 21;

        /// <summary>
        /// A stored marker name → ePossibleMarker. Accepts the part id / reward type, "backroom_&lt;part&gt;", "MARKER_&lt;x&gt;"
        /// and a few spellings, so markers stored by other screens resolve the same.
        /// </summary>
        public static int MarkerOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string n = name.Trim().ToLowerInvariant();
            if (n.StartsWith("marker_")) n = n.Substring(7);
            if (n.StartsWith("backroom_")) n = n.Substring(9);
            n = n switch
            {
                "body" or "body_kit" or "bodykits" => "bodykit", "roof_scoop" or "roof" => "roofscoop", "custom_hud" => "hud",
                "vinyl" => "vinyls", "decal" => "decals", "spoilers" => "spoiler", "rim" => "rims", "hoods" => "hood",
                "suspension" => "chassis", "nitrous" => "nos", "tire" => "tires", "brake" => "brakes",
                "get_out_of_jail" or "jail" or "jail_free" or "outofjail" => "out_of_jail_free", "impound_box" => "add_impound_box",
                "impound_release" => "release_car_from_impound", "cash" => "cash_bonus", _ => n,
            };
            return Math.Max(0, Array.IndexOf(MarkerNames, n));
        }

        /// <summary>The marker a backroom performance item uses (FEMarkerManager::UtilizeMarker(Type), US:921).</summary>
        public static int PerfMarker(int type) => type switch
        {
            Tires => M_Tires, Brakes => M_Brakes, Chassis => M_Chassis, Transmission => M_Transmission, Engine => M_Engine,
            Induction => M_Induction, Nos => M_Nos, _ => 0,
        };

        /// <summary>The marker a backroom visual item uses (FEMarkerManager::UtilizeMarker(slot), US:874).</summary>
        public static int SlotMarker(int slot)
        {
            switch (slot)
            {
                case Slot.Body: return M_Body;
                case Slot.Hood: return M_Hood;
                case Slot.Spoiler: return M_Spoiler;
                case Slot.FrontWheel: return M_Rims;
                case Slot.Roof: return M_RoofScoop;
                case Slot.CustomHud: return M_CustomHud;
                case Slot.VinylLayer: return M_Vinyl;
                case Slot.BasePaint: return M_Paint;
                case 83: case 91: case 115: case 123: return M_Decal;
            }
            if ((slot >= 99 && slot <= 104) || (slot >= 107 && slot <= 112)) return M_Decal;
            return 0;
        }

        /// <summary>
        /// CareerUnlocker::IsBackroomAvailable (US:398-511): the marker that opens a backroom category entity
        /// (window tint, numbers and the license plate have none).
        /// </summary>
        public static int EntityMarker(int entity)
        {
            switch (entity)
            {
                case U_Tires: return M_Tires;
                case U_Brakes: return M_Brakes;
                case U_Chassis: return M_Chassis;
                case U_Transmission: return M_Transmission;
                case U_Engine: return M_Engine;
                case U_Induction: return M_Induction;
                case U_Nos: return M_Nos;
                case U_BodyKit: return M_Body;
                case U_Spoilers: return M_Spoiler;
                case U_Hoods: return M_Hood;
                case U_RoofScoops: return M_RoofScoop;
                case U_CustomHud: return M_CustomHud;
                case U_RimBrands: return M_Rims;
            }
            if (entity >= 19 && entity <= 23) return M_Paint;
            if (entity >= 25 && entity <= 34) return M_Rims;
            if (entity >= 35 && entity <= 42) return M_Vinyl;
            if (entity >= 44 && entity <= 56) return M_Decal;
            return 0;
        }

        // ---- unlock state (TheUnlockData) ---------------------------------------------------------------------------

        /// <summary>
        /// CareerUnlocker::IsUnlockableUnlocked (US:349-363): unlocked when the career was beaten or the level is at most
        /// the entity's career level; in the backroom also one level above it, and any level up to 7 once the entity
        /// reached its gMaxPartLevels.
        /// </summary>
        public static bool IsUnlocked(IReadOnlyList<int> levels, int entity, int level, bool backroom, bool careerBeaten)
        {
            if (careerBeaten) return true;
            int cur = levels != null && entity >= 0 && entity < levels.Count ? levels[entity] : 0;
            if (level <= cur) return true;
            if (backroom)
            {
                if (level <= cur + 1) return true;
                if (cur == MaxPartLevel(entity) && level <= 7) return true;
            }
            return false;
        }

        /// <summary>Preset rides (GlobalB chunk 0x30220, PresetCar 0x290 bytes, CarInfo.hpp:84): part name hash per slot.</summary>
        public static Dictionary<string, (string car, int physicsLevel, uint[] parts)> PresetRides(byte[] globalB)
        {
            var r = new Dictionary<string, (string, int, uint[])>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Chunks.FindAll(globalB, 0, globalB.Length, 0x30220))
                for (int o = c.DataStart; o + 0x290 <= c.End; o += 0x290)
                {
                    string car = Chunks.CString(globalB, o + 8, 32), preset = Chunks.CString(globalB, o + 40, 32);
                    if (car.Length == 0 || preset.Length == 0 || r.ContainsKey(preset)) continue;
                    var parts = new uint[139];
                    for (int i = 0; i < 139; i++) parts[i] = Chunks.U32(globalB, o + 0x60 + i * 4);
                    r[preset] = (car.ToLowerInvariant(), Chunks.I32(globalB, o + 0x5C), parts);
                }
            return r;
        }
    }
}
