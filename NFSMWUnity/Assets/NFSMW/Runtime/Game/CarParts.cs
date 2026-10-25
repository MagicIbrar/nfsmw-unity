using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// What a car can wear. The shop lists come from the game's car part database (GlobalB chunk 0x80034602,
    /// <see cref="CarPartDb"/>: slot, upgrade level, group, brand, rim size, colour per part); the installed part of each
    /// slot is stored by name in OwnedCar.PartSlots/PartNames and mirrored into the fields CarBuilder renders: body kits
    /// CAR_KITnn_*, hoods CAR_STYLEnn_HOOD, shared spoilers (CARS\SPOILER*, mounted on the car's SPOILER marker), roof
    /// scoops (CARS\ROOF on ROOF_SCOOP), rims (CARS\WHEELS BRAND_STYLEnn_SIZE_25) and vinyls (CARS\CAR\VINYLS.BIN).
    /// </summary>
    public static class CarParts
    {
        public static readonly string[] RimBrands = { "5ZIGEN", "ADR", "BBS", "ENKEI", "KONIG", "LOWENHART", "OZ", "RACINGHART", "RAJA", "VOLK", "MOMO" };
        public static readonly int[] RimSizes = { 17, 18, 19, 20 };
        static readonly Dictionary<string, List<string>> Vinyls = new Dictionary<string, List<string>>();

        public static string SpoilerFolder(string car) => car.ToUpperInvariant() switch
        {
            "911GT2" or "911TURBO" or "997S" or "CAYMANS" => "SPOILER_PORSCHES",
            "CARRERAGT" => "SPOILER_CARRERA",
            "GTI" or "PUNTO" or "CLIO" or "A3" => "SPOILER_HATCH",
            _ => "SPOILER",
        };

        static string SpoilerSuffix(string folder) => folder == "SPOILER" ? "" : "_" + folder.Substring("SPOILER_".Length);

        public static string SpoilerSolid(string car, int style, bool cf)
        {
            string folder = SpoilerFolder(car);
            return $"SPOILER_STYLE{style:00}{SpoilerSuffix(folder)}{(cf ? "_CF" : "")}_A";
        }

        public static string RoofSolid(int style, string type, bool cf) =>
            $"ROOF_STYLE{style:00}{(string.IsNullOrEmpty(type) ? "" : "_" + type)}{(cf ? "_CF" : "")}_A";

        public static string RimSolid(string brand, int style, int size) => $"{brand}_STYLE{style:00}_{size}_25_A";

        static List<int> Styles(IEnumerable<string> names, string pattern)
        {
            var re = new Regex(pattern);
            return names.Select(n => re.Match(n)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();
        }

        public static List<int> Kits(string car)
        {
            string c = Regex.Escape(car.ToUpperInvariant());
            return Styles(CarBuilder.Asset(car).Solids.Select(s => s.Name), $"^{c}_KIT(\\d\\d)_BODY_A$").Where(k => k > 0).ToList();
        }

        public static List<int> Hoods(string car)
        {
            string c = Regex.Escape(car.ToUpperInvariant());
            return Styles(CarBuilder.Asset(car).Solids.Select(s => s.Name), $"^{c}_STYLE(\\d\\d)_HOOD_A$");
        }

        public static bool HasMarker(string car, string marker) => CarBuilder.Asset(car).Solids.Any(s => s.Marker(marker) != null);

        public static List<int> Spoilers(string car)
        {
            string folder = SpoilerFolder(car);
            return Styles(CarBuilder.Asset(folder).Solids.Select(s => s.Name), $"^SPOILER_STYLE(\\d\\d){Regex.Escape(SpoilerSuffix(folder))}_A$");
        }

        public static List<int> Roofs() => Styles(CarBuilder.Asset("ROOF").Solids.Select(s => s.Name), "^ROOF_STYLE(\\d\\d)_A$");

        public static List<int> RimStyles(string brand) =>
            Styles(CarBuilder.Asset("WHEELS").Solids.Select(s => s.Name), $"^{Regex.Escape(brand)}_STYLE(\\d\\d)_\\d\\d_25_A$");

        public static IEnumerable<string> AvailableRimBrands() => RimBrands.Where(b => RimStyles(b).Count > 0);

        /// <summary>Vinyl texture names for a car (the _MASK partners left out).</summary>
        public static List<string> VinylNames(string car)
        {
            car = car.ToUpperInvariant();
            if (Vinyls.TryGetValue(car, out var list)) return list;
            list = new List<string>();
            var path = Path.Combine(GameData.Root, "CARS", car, "VINYLS.BIN");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                var d = File.ReadAllBytes(path);
                foreach (var t in TexturePack.ReadAll(d, 0, d.Length)) // compressed blocks: names need the full read
                {
                    var n = t.Name ?? "";
                    if (n.EndsWith("_MASK") || n.EndsWith("_MAS") || n.EndsWith("_MA") || n.EndsWith("_M")) continue;
                    list.Add(n);
                }
                list.Sort();
            }
            Vinyls[car] = list;
            return list;
        }

        // ---- the car part database -----------------------------------------------------------------------------------

        static CarPartDb _db;
        public static CarPartDb Db => _db ??= GameData.Root != null ? CarPartDb.Load(GameData.Root) : new CarPartDb();

        /// <summary>Attribute data with FE_ATTRIB (prices) and, once the career is loaded, gameplay.bin (unlocks).</summary>
        public static AttribDatabase Attribs => GameSession.Career?.Db ?? GameData.Attributes;

        /// <summary>The CarTypeInfo name of a CARS folder (upper case).</summary>
        public static string TypeName(string car) => (car ?? "").ToUpperInvariant();

        /// <summary>The hero car (CARTYPE_BMWM3GTRE46): Visual only, no rim paint (CM:1387, CC:1662).</summary>
        public static bool IsHeroCar(string car) => string.Equals(car, "bmwm3gtre46", StringComparison.OrdinalIgnoreCase);

        public static CarPartDb.Part Find(string car, int slot, string name) =>
            string.IsNullOrEmpty(name) ? null : Db.PartsFor(TypeName(car), slot).FirstOrDefault(p => p.Name == name);

        /// <summary>The part installed in a slot (FECustomizationRecord.GetInstalledPart), null = stock / none.</summary>
        public static CarPartDb.Part Installed(OwnedCar c, int slot)
        {
            if (c == null) return null;
            int i = c.PartSlots.IndexOf(slot);
            return i >= 0 && i < c.PartNames.Count ? Find(c.Car, slot, c.PartNames[i]) : null;
        }

        /// <summary>
        /// The car's stock part for a slot (RideInfo::SetStockParts): the level-0 part filed under the car's own type
        /// name (BODY_00, KIT00 spoiler/hood, WHEEL), else the slot's first level-0 part ("NO ROOF SCOOP", stock tint).
        /// </summary>
        public static CarPartDb.Part Stock(string car, int slot)
        {
            uint own = EaHash.Hash(TypeName(car));
            var parts = Db.PartsFor(TypeName(car), slot, 0).ToList();
            return parts.FirstOrDefault(p => p.CarTypeHash == own) ?? parts.FirstOrDefault();
        }

        /// <summary>Installed or else stock (what GetInstalledCarPart returns for pricing and trade-in).</summary>
        public static CarPartDb.Part InstalledOrStock(OwnedCar c, int slot) => Installed(c, slot) ?? Stock(c.Car, slot);

        /// <summary>FECustomizationRecord::SetInstalledPart, plus the look fields CarBuilder draws.</summary>
        public static void SetInstalled(OwnedCar c, int slot, CarPartDb.Part p)
        {
            int i = c.PartSlots.IndexOf(slot);
            if (i >= 0) { c.PartSlots.RemoveAt(i); if (i < c.PartNames.Count) c.PartNames.RemoveAt(i); }
            if (p != null) { c.PartSlots.Add(slot); c.PartNames.Add(p.Name); }
            ApplyLook(c, slot, p);
        }

        static readonly Regex SpoilerRe = new Regex(@"SPOILER (\d+)"), RoofRe = new Regex(@"_STYLE(\d+)(_OFFSET|_DUAL)?(_CF)?_A$"),
            HoodRe = new Regex(@"_STYLE(\d+)_HOOD_A$"), RimRe = new Regex(@"_STYLE(\d+)_(\d+)_25_A$");

        /// <summary>Mirrors a database part into the OwnedCar fields CarBuilder renders (null = stock).</summary>
        public static void ApplyLook(OwnedCar c, int slot, CarPartDb.Part p)
        {
            switch (slot)
            {
                case ShopRules.Slot.Body:
                    c.Kit = p != null ? Mathf.Max(0, p.I(CarPartDb.Attr.KIT_NUMBER)) : 0;
                    break;
                case ShopRules.Slot.Spoiler:
                {
                    var m = p != null && p.BaseModelSelector != 1 ? SpoilerRe.Match(p.Name ?? "") : Match.Empty;
                    c.Spoiler = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    c.SpoilerCF = m.Success && p.Carbon;
                    break;
                }
                case ShopRules.Slot.Roof:
                {
                    var m = p != null ? RoofRe.Match(p.ModelSuffix() ?? "") : Match.Empty;
                    c.Roof = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    c.RoofType = m.Success && m.Groups[2].Success ? m.Groups[2].Value.Substring(1) : "";
                    c.RoofCF = m.Success && m.Groups[3].Success;
                    break;
                }
                case ShopRules.Slot.Hood:
                {
                    var m = p != null ? HoodRe.Match(p.ModelSuffix() ?? "") : Match.Empty;
                    c.Hood = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    c.HoodCF = p != null && p.Carbon;
                    break;
                }
                case ShopRules.Slot.FrontWheel:
                {
                    var m = p != null && p.BaseModelSelector == 2 ? RimRe.Match(p.ModelSuffix() ?? "") : Match.Empty;
                    string brand = m.Success ? RimBrandOf(p) : null;
                    if (brand == null) { c.RimBrand = ""; break; }
                    c.RimBrand = brand;
                    c.RimStyle = int.Parse(m.Groups[1].Value);
                    c.RimSize = int.Parse(m.Groups[2].Value);
                    break;
                }
                case ShopRules.Slot.BasePaint:
                    if (p != null) { var (r, g, b) = p.Rgb; c.Paint = new Color(r / 255f, g / 255f, b / 255f); }
                    break;
                case ShopRules.Slot.VinylLayer:
                    c.Vinyl = p != null ? VinylTexture(c.Car, p) ?? "" : "";
                    break;
            }
        }

        static Dictionary<uint, string> _rimBrandNames;

        /// <summary>The WHEELS geometry prefix whose bStringHash is the rim's BRAND_NAME (model base, selector 2).</summary>
        public static string RimBrandOf(CarPartDb.Part p)
        {
            if (_rimBrandNames == null)
            {
                _rimBrandNames = new Dictionary<uint, string>();
                foreach (var s in CarBuilder.Asset("WHEELS").Solids)
                {
                    int k = s.Name.IndexOf("_STYLE", StringComparison.Ordinal);
                    if (k > 0) _rimBrandNames[EaHash.Hash(s.Name.Substring(0, k))] = s.Name.Substring(0, k);
                }
            }
            return p != null && _rimBrandNames.TryGetValue(p.Brand, out var n) ? n : null;
        }

        /// <summary>
        /// The car's VINYLS.BIN texture for a vinyl part: CAR_NAME (texture names are cut at 23 characters, e.g.
        /// BMWM3GTRE46_BODYSTRIPE0). Vinyls without a texture for this car (the rivals' own vinyls) are not offered
        /// (GetCarPartList "vinyl texture exists", CM:1200-1287). Checked with the harness `shopcheck` mode.
        /// </summary>
        public static string VinylTexture(string car, CarPartDb.Part p)
        {
            var names = VinylNames(car);
            string want = TypeName(car) + "_" + p.Name;
            return names.FirstOrDefault(n => n == want) ?? names.FirstOrDefault(n => n.Length >= 23 && want.Length > n.Length && want.StartsWith(n, StringComparison.Ordinal));
        }

        /// <summary>The part's display name: its LANGUAGEHASH string, else the database name.</summary>
        public static string DisplayName(CarPartDb.Part p)
        {
            if (p == null) return "Stock";
            uint h = p.U(CarPartDb.Attr.LANGUAGEHASH);
            string s = h != 0 ? UI.FrontEndData.Text(h, null) : null;
            return string.IsNullOrEmpty(s) ? (p.Name ?? "").Replace('_', ' ') : s;
        }

        /// <summary>
        /// CarCustomizeManager::GetCarPartList (CM:1200-1287) for a slot, before the unlock filter: body kits whose
        /// model exists, rims of one brand (param = BRAND_NAME hash, 0 = all), vinyls of one group (param) whose texture
        /// exists, minus the Collector's Edition vinyls (brand SPECIAL, CE01-CE03).
        /// </summary>
        public static List<CarPartDb.Part> PartList(string car, int slot, uint param = 0, int vinylGroup = -1)
        {
            var list = new List<CarPartDb.Part>();
            List<int> kits = slot == ShopRules.Slot.Body ? Kits(car) : null;
            foreach (var p in Db.PartsFor(TypeName(car), slot))
            {
                if (slot == ShopRules.Slot.Body)
                {
                    int k = p.I(CarPartDb.Attr.KIT_NUMBER);
                    if (k != 0 && !kits.Contains(k)) continue;
                }
                else if (slot == ShopRules.Slot.FrontWheel && param != 0 && p.Brand != param) continue;
                else if (slot == ShopRules.Slot.VinylLayer)
                {
                    if (vinylGroup >= 0 && p.Group != vinylGroup) continue;
                    if (p.Brand == CarPartDb.Attr.SPECIAL || p.Name == "CE01" || p.Name == "CE02" || p.Name == "CE03") continue;
                    if (VinylTexture(car, p) == null) continue;
                }
                list.Add(p);
            }
            return list;
        }

        // ---- performance levels ------------------------------------------------------------------------------------

        public static int PerfLevel(OwnedCar c, int type) => type switch
        {
            ShopRules.Tires => c.Tires, ShopRules.Brakes => c.Brakes, ShopRules.Chassis => c.Suspension, ShopRules.Transmission => c.Transmission,
            ShopRules.Engine => c.Engine, ShopRules.Induction => c.Induction, _ => c.Nitrous,
        };

        public static void SetPerfLevel(OwnedCar c, int type, int v)
        {
            switch (type)
            {
                case ShopRules.Tires: c.Tires = v; break;
                case ShopRules.Brakes: c.Brakes = v; break;
                case ShopRules.Chassis: c.Suspension = v; break;
                case ShopRules.Transmission: c.Transmission = v; break;
                case ShopRules.Engine: c.Engine = v; break;
                case ShopRules.Induction: c.Induction = v; break;
                default: c.Nitrous = v; break;
            }
        }

        public static bool HasJunkman(OwnedCar c, int type) => (c.Junkman & (1 << type)) != 0;

        public static int NumPackages(string car, int type) => ShopRules.NumPackages(Attribs, car, type);

        /// <summary>
        /// Physics::Upgrades::CanInstallJunkman (speed.exe 0x677FE0): the type must have a junkman entry, and
        /// induction/nitrous Junkman parts need a car that has induction/nitrous. The game also checks the junkman
        /// attribute for that type, which this approximates as "has packages of that type and no Junkman part yet".
        /// </summary>
        public static bool CanInstallJunkman(OwnedCar c, int type) => c != null && !HasJunkman(c, type) && NumPackages(c.Car, type) > 0;

        /// <summary>
        /// Keeps a loaded car valid: package levels clamped to the car's pvehicle `*_upgrades` (older saves held 0..3
        /// front-end stages; JsonUtility keeps field initializers, so they cannot be told apart and are read as levels)
        /// and the 3 × 7 tuning array present.
        /// </summary>
        public static void Migrate(OwnedCar c)
        {
            if (c == null || string.IsNullOrEmpty(c.Car) || Attribs?.Get("pvehicle", c.Car) == null) return;
            for (int t = 0; t < ShopRules.PerfCount; t++)
                SetPerfLevel(c, t, Mathf.Clamp(PerfLevel(c, t), 0, NumPackages(c.Car, t)));
            if (c.Tunings == null || c.Tunings.Length < 21) c.Tunings = new float[21];
            c.PerfFormat = 1;
        }

        // ---- heat ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// The car's float heat (FECareerRecord.VehicleHeat): VehicleHeat while it still matches the int Heat the
        /// pursuit code stores, else the int Heat (a pursuit changed it since).
        /// </summary>
        public static float HeatOf(OwnedCar c)
        {
            if (c == null) return 0;
            if (c.VehicleHeat < 0 || Mathf.FloorToInt(c.VehicleHeat) != c.Heat) c.VehicleHeat = c.Heat;
            return c.VehicleHeat;
        }

        public static void SetHeat(OwnedCar c, float h)
        {
            if (c == null) return;
            c.VehicleHeat = Mathf.Max(0, h);
            c.Heat = Mathf.FloorToInt(c.VehicleHeat);
        }
    }
}
