using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// TheUnlockData in the profile (UnlockSystem.cpp:19-143, 349-379): per eUnlockableEntity the career level, the
    /// "new" level and the times the shop was entered since. Levels come from each bin's BaselineUnlocks
    /// (gameplay.bin race_bin_NN, AwardUnlockUpgrade US:1134) applied as the bin becomes current [?: the caller is not
    /// in the decomp; bin 15 holds the level-1 unlocks, so it runs at career start / bin entry].
    /// </summary>
    public static class ShopUnlocks
    {
        static List<int> Fill(List<int> l)
        {
            while (l.Count < ShopRules.UnlockCount) l.Add(0);
            return l;
        }

        public static List<int> Levels => GameSession.Profile == null ? null : Fill(GameSession.Profile.UnlockLevels);

        /// <summary>HasBeatenCareer / CareerModeHasBeenCompletedAtLeastOnce: the last rival fell (Profile.Bin 0).</summary>
        public static bool CareerBeaten
        {
            get
            {
                var p = GameSession.Profile;
                return p != null && (p.Bin <= 0 || p.BossesBeaten.Contains(1));
            }
        }

        /// <summary>Applies the BaselineUnlocks of every bin from the last applied one down to the current bin.</summary>
        public static void Ensure()
        {
            var p = GameSession.Profile;
            var db = GameSession.Career?.Db;
            if (p == null || db == null) return;
            Fill(p.UnlockLevels); Fill(p.UnlockNew); Fill(p.UnlockSeen);
            int from = p.UnlocksAppliedBin <= 0 ? 16 : p.UnlocksAppliedBin - 1;
            int to = Mathf.Max(1, p.Bin);
            for (int bin = from; bin >= to; bin--)
            {
                // EEnterBin (0x62C2D0): ClearAllNewStatus (0x62C3AC) before the bin's baseline EAwardUpgrades, so only the
                // newest bin's unlocks show as new
                ClearAllNewStatus();
                foreach (var (part, level) in ShopRules.BaselineUnlocks(db, bin)) Award(ShopRules.AwardEntity(part, level), level);
                p.UnlocksAppliedBin = bin;
            }
        }

        /// <summary>ClearAllNewStatus (0x577740, US:1157): ClearNewUnlock (0x576870) of every entity in both modes (IsNewPart = old).</summary>
        public static void ClearAllNewStatus()
        {
            var p = GameSession.Profile;
            if (p == null) return;
            var n = Fill(p.UnlockNew);
            for (int i = 0; i < n.Count; i++) n[i] = 0;
        }

        /// <summary>UnlockUnlockableThing(entity, CAREER, level): sets (not max) the level and marks it new.</summary>
        public static void Award(int entity, int level)
        {
            var p = GameSession.Profile;
            if (p == null || entity <= 0 || entity >= ShopRules.UnlockCount) return;
            level = Mathf.Max(0, level);
            Fill(p.UnlockLevels)[entity] = level;
            Fill(p.UnlockNew)[entity] = level;
        }

        /// <summary>
        /// Career/QuickRaceUnlocker::IsUnlockableUnlocked: the quick race mirrors the career levels
        /// (UnlockUnlockableThing sets both), so one rule serves both modes.
        /// </summary>
        public static bool IsUnlocked(int entity, int level, bool backroom = false) =>
            ShopRules.IsUnlocked(Levels, entity, level, backroom, CareerBeaten);

        /// <summary>CareerUnlocker::IsCarPartUnlocked: level-0 parts always, else the slot's entity at the part level.</summary>
        public static bool PartUnlocked(int slot, CarPartDb.Part part, bool backroom = false) =>
            part == null || part.Level == 0 || IsUnlocked(ShopRules.SlotUnlockable(slot), part.Level, backroom);

        /// <summary>UnlockSystem::IsUnlockableNew: the entity's new level equals the level asked about.</summary>
        public static bool IsNew(int entity, int level)
        {
            var p = GameSession.Profile;
            return p != null && level > 0 && entity > 0 && entity < Fill(p.UnlockNew).Count && p.UnlockNew[entity] == level;
        }

        /// <summary>
        /// DoesCategoryHaveNewUnlock (UnlockSystem.cpp:74): any "new" level among a customize category's entities —
        /// UNLOCKABLE_THING_CUSTOMIZE_PARTS (body kit, spoilers, rims, hoods, roofs), _PERFORMANCE (the 7 types),
        /// _VISUAL (decals, custom HUD, numbers, paint, window tint, vinyls).
        /// </summary>
        public static bool DoesCategoryHaveNewUnlock(int group)
        {
            var p = GameSession.Profile;
            if (p == null) return false;
            int[] ents = group switch
            {
                1 => new[] { ShopRules.U_BodyKit, ShopRules.U_Spoilers, ShopRules.U_RimBrands, ShopRules.U_Hoods, ShopRules.U_RoofScoops },
                2 => new[] { ShopRules.U_Tires, ShopRules.U_Brakes, ShopRules.U_Chassis, ShopRules.U_Transmission, ShopRules.U_Engine, ShopRules.U_Induction, ShopRules.U_Nos },
                3 => new[] { ShopRules.U_DecalHood, ShopRules.U_CustomHud, ShopRules.U_DecalNumbers, ShopRules.U_PaintableBody, ShopRules.U_WindowTint, ShopRules.U_VinylsBody },
                _ => new int[0],
            };
            Fill(p.UnlockNew);
            foreach (int e in ents) if (e < p.UnlockNew.Count && p.UnlockNew[e] != 0) return true;
            return false;
        }

        /// <summary>MarkUnlockableThingSeen for every entity (CustomizeMain constructor, CC:1888): new clears on the 4th visit.</summary>
        public static void MarkSeen()
        {
            var p = GameSession.Profile;
            if (p == null || GameSession.Mode != GameMode.Career) return;
            Fill(p.UnlockNew); Fill(p.UnlockSeen);
            for (int e = 0; e < ShopRules.UnlockCount; e++)
            {
                if (++p.UnlockSeen[e] <= 3) continue;
                p.UnlockSeen[e] = 0;
                p.UnlockNew[e] = 0;
            }
        }
    }

    /// <summary>
    /// FEMarkerManager over Profile.Markers (UnlockSystem.cpp:830-1099): owned markers by name (ShopRules.MarkerNames),
    /// the backroom's one-marker-per-item spending, and the immediate rewards (cash, pink slip rival car).
    /// </summary>
    public static class ShopMarkers
    {
        static List<string> List => GameSession.Profile?.Markers;

        public static int Count(int marker) => List?.Count(m => ShopRules.MarkerOf(m) == marker) ?? 0;
        public static bool Has(int marker) => Count(marker) > 0;

        /// <summary>GetNumCustomizeMarkers: types 1..15 (the loop stops before PAINT, US:989-995).</summary>
        public static int CustomizeCount => List?.Count(m => { int k = ShopRules.MarkerOf(m); return k >= 1 && k < ShopRules.M_Paint; }) ?? 0;

        public static void Add(int marker)
        {
            if (GameSession.Profile == null || marker <= 0 || marker >= ShopRules.MarkerNames.Length) return;
            if (List.Count < 63) List.Add(ShopRules.MarkerNames[marker]);
        }

        /// <summary>UtilizeMarker: removes the first owned marker of that type.</summary>
        public static bool Use(int marker)
        {
            var l = List;
            if (l == null) return false;
            int i = l.FindIndex(m => ShopRules.MarkerOf(m) == marker);
            if (i < 0) return false;
            l.RemoveAt(i);
            return true;
        }

        /// <summary>
        /// A picked reward card (AwardMarker immediate, US:1054-1087): cash pays out, a pink slip gives the bin's rival
        /// car, anything else goes to the inventory. Returns the new car for a pink slip.
        /// </summary>
        public static OwnedCar Award(string name, int cash = 0)
        {
            var p = GameSession.Profile;
            if (p == null) return null;
            int m = ShopRules.MarkerOf(name);
            if (m == ShopRules.M_Cash) { p.Cash += cash; return null; }
            if (m == ShopRules.M_PinkSlip) return AwardRivalCar(p.Bin);
            Add(m);
            return null;
        }

        static Dictionary<string, (string car, int physicsLevel, uint[] parts)> _presets;

        /// <summary>
        /// FEPlayerCarDB::AwardRivalCar (VehicleDB.cpp:507-537): preset car "BL&lt;bin&gt;" (GlobalB PresetCar) with its
        /// preset parts and the presetride/bl&lt;bin&gt; package levels (Physics::Upgrades::ApplyPreset), as a new career
        /// car (max 25). `bin` is the rival's number (the pick screen runs before or after Profile.Bin drops: pass it).
        /// </summary>
        public static OwnedCar AwardRivalCar(int bin)
        {
            var p = GameSession.Profile;
            if (p == null || p.Cars.Count >= 25) return null;
            if (_presets == null)
            {
                var path = System.IO.Path.Combine(GameData.Root, "GLOBAL", "GlobalB.lzc");
                _presets = System.IO.File.Exists(path) ? ShopRules.PresetRides(GameFiles.ReadBundle(path)) : new Dictionary<string, (string, int, uint[])>();
            }
            if (!_presets.TryGetValue($"BL{bin}", out var preset)) return null;
            var car = new OwnedCar { Car = preset.car, PerfFormat = 1 };
            for (int slot = 0; slot < preset.parts.Length; slot++)
            {
                if (preset.parts[slot] == 0 || (ShopRules.SlotUnlockable(slot) == 0 && slot != ShopRules.Slot.BasePaint)) continue;
                var part = CarParts.Db.PartsFor(CarParts.TypeName(car.Car), slot).FirstOrDefault(x => x.NameHash == preset.parts[slot]);
                if (part != null) CarParts.SetInstalled(car, slot, part);
            }
            var pr = CarParts.Attribs?.Get("presetride", $"bl{bin}");
            if (pr != null)
                for (int t = 0; t < ShopRules.PerfCount; t++)
                    if (pr.Has(ShopRules.PerfTypes[t]))
                        CarParts.SetPerfLevel(car, t, Mathf.Clamp(pr.Int(ShopRules.PerfTypes[t]), 0, CarParts.NumPackages(car.Car, t)));
            p.Cars.Add(car);
            return car;
        }

        /// <summary>GET_OUT_OF_JAIL on the busted screen: pays the fine with the marker (uiInfractions.cpp:144-167).</summary>
        public static bool UseJailFree() => Use(ShopRules.M_JailFree);

        /// <summary>
        /// ADD_IMPOUND_BOX in My Cars (QRCS:66-70, FEImpoundData::AddMaxBusted VehicleDB.cpp:1275-1287): MaxBusted + 1,
        /// capped at g_MaximumMaximumTimesBusted.
        /// </summary>
        public static bool AddImpoundBox(OwnedCar c)
        {
            if (c == null || c.MaxBusted >= MaximumTimesBusted || !Has(ShopRules.M_ImpoundBox)) return false;
            Use(ShopRules.M_ImpoundBox);
            c.MaxBusted++;
            return true;
        }

        /// <summary>g_MaximumMaximumTimesBusted (speed.exe 0x8F4314 = 5). g_fImpoundPercentageOfOriginalCost (0x8F4310) is 0.25.</summary>
        public const int MaximumTimesBusted = 5;
    }

    /// <summary>
    /// SelectablePart (FECustomize.hpp:107-210): a visual part for a slot, or a performance package (type, level 1..N,
    /// 7 = Junkman), with its price; Part null on a visual slot = remove / stock (decals, numbers).
    /// </summary>
    public sealed class SelectablePart
    {
        public CarPartDb.Part Part;
        public int Slot;
        public int Level;
        public int PhysType = -1;
        public int Price;
        public bool IsPerf => PhysType >= 0;
        public bool Junkman => IsPerf && Level >= ShopRules.JunkmanLevel;

        public static SelectablePart Visual(int slot, CarPartDb.Part p) =>
            new SelectablePart { Slot = slot, Part = p, Level = p?.Level ?? 0, Price = ShopCart.PriceOf(slot, p) };

        public static SelectablePart Perf(int type, int level)
        {
            var s = new SelectablePart { PhysType = type, Level = level };
            s.Price = ShopCart.PriceOf(s);
            return s;
        }

        /// <summary>Package names PN_LEVEL_(6 − N + level), Junkman 0xEDD14807 (CC:4652-4933); parts by LANGUAGEHASH.</summary>
        public string Name => !IsPerf ? CarParts.DisplayName(Part)
            : Junkman ? UI.FrontEndData.Text(0xEDD14807, "Junkman")
            : UI.FrontEndData.Text(UI.FrontEndData.Hash($"PN_LEVEL_{6 - CarParts.NumPackages(ShopCart.Car?.Car, PhysType) + Level}"), $"Level {Level}");
    }

    /// <summary>ShoppingCartItem (FEC:212-272): the part to buy, the installed part traded in, the cart checkbox.</summary>
    public sealed class CartItem
    {
        public SelectablePart Buy, TradeIn;
        public bool Active = true;
        public int PartPrice => Buy.Price;
        /// <summary>GetTradeInPrice = (int)(TradeIn.price × gTradeInFactor 0.25).</summary>
        public int TradeInPrice => TradeIn == null ? 0 : (int)(TradeIn.Price * ShopRules.TradeInFactor);
    }

    /// <summary>
    /// CarCustomizeManager (CustomizeManager.cpp): the tuning car, its preview record, the shopping cart and checkout
    /// (customization.md §4, §6, §8). Selections only change the preview; Checkout charges (or spends markers in the
    /// backroom), installs and cools the car's heat.
    /// </summary>
    public static class ShopCart
    {
        public static OwnedCar Car { get; private set; }
        public static OwnedCar Preview { get; private set; }
        public static bool Backroom;
        public static readonly List<CartItem> Items = new List<CartItem>();

        public static bool Career => GameSession.Mode == GameMode.Career && GameSession.Profile != null;

        /// <summary>CarCustomizeManager::SetInParts / SetInPerformance: which main category the shop is browsing (the cart's marker rows).</summary>
        public static bool InParts, InPerformance;

        /// <summary>
        /// CarCustomizeManager::TempColoredPart (CM:287-299): the part being coloured — a custom HUD on its colour screen, a vinyl
        /// on its colour screen — before it reaches the cart.
        /// </summary>
        public static SelectablePart TempColoredPart { get; private set; }
        public static void SetTempColoredPart(SelectablePart p) => TempColoredPart = p == null ? null : new SelectablePart { Part = p.Part, Slot = p.Slot, Level = p.Level, PhysType = p.PhysType, Price = p.Price };
        public static void ClearTempColoredPart() => TempColoredPart = null;

        /// <summary>GetActualHeat (CM:1325): the tuning car's VehicleHeat.</summary>
        public static float ActualHeat => Car == null ? 0f : CarParts.HeatOf(Car);

        /// <summary>The SelectablePart::PartState nibbles of an item right now (the exe stores them on the part; the port derives them).</summary>
        public static int StateOf(SelectablePart p)
        {
            if (p == null) return CPSAvailable;
            int s = IsLocked(p) ? CPSLocked : IsNew(p) ? CPSNew : CPSAvailable;
            if (IsInstalled(p)) s |= CPSInstalled;
            else if (InCart(p) != null) s |= CPSInCart;
            return s;
        }

        public const int CPSAvailable = 1, CPSLocked = 2, CPSNew = 3, CPSInstalled = 16, CPSInCart = 32;

        /// <summary>IsPartTypeInCart(slot_id) (CM:143).</summary>
        public static CartItem SlotInCart(int slot) => Items.FirstOrDefault(i => !i.Buy.IsPerf && i.Buy.Slot == slot);

        /// <summary>IsPartTypeInCart(Physics::Upgrades::Type) (CM:148).</summary>
        public static CartItem PerfTypeInCart(int type) => Items.FirstOrDefault(i => i.Buy.IsPerf && i.Buy.PhysType == type);

        /// <summary>GetInstalledPerfPkg: the installed level of a type (Junkman is a separate bit).</summary>
        public static int InstalledPerfPkg(int type) => Car == null ? 0 : CarParts.PerfLevel(Car, type);

        /// <summary>GetStockCarPart(slot) (CM:301).</summary>
        public static CarPartDb.Part StockPart(int slot) => Car == null ? null : CarParts.Stock(Car.Car, slot);

        /// <summary>GetInstalledCarPart(slot) (CM:352): the installed part of the tuning car, null when stock / none.</summary>
        public static CarPartDb.Part InstalledPart(int slot) => Car == null ? null : CarParts.Installed(Car, slot);

        /// <summary>GetActivePartFromSlot (CM:169): the cart item's part when one is in the cart, else the installed part.</summary>
        public static CarPartDb.Part ActivePart(int slot)
        {
            var item = SlotInCart(slot);
            return item != null ? item.Buy.Part : InstalledPart(slot);
        }

        /// <summary>CanTradeIn(part) (CM:56): never for performance; else by slot (ShopRules.CanTradeIn).</summary>
        public static bool CanTradeIn(SelectablePart p) => p != null && !p.IsPerf && ShopRules.CanTradeIn(p.Slot);

        /// <summary>UnlockSystem::GetCarPartCost of the installed part × gTradeInFactor: the "trade in value" line of the part screens (CC:2382-2405).</summary>
        public static int TradeInValueOf(SelectablePart p)
        {
            if (!Career || Backroom || p == null || !CanTradeIn(p)) return 0;
            var inst = InstalledPart(p.Slot);
            int v = inst != null ? ShopRules.VisualCost(CarParts.Attribs, p.Slot, inst.Level) : 0;
            return (int)(v * ShopRules.TradeInFactor);
        }

        /// <summary>CustomizeMain: TakeControl copies the installed record into the preview; MarkUnlockableThingSeen.</summary>
        public static void Begin(OwnedCar car, bool fresh = false)
        {
            if (!fresh && Car == car && Preview != null) return;
            Car = car;
            Items.Clear();
            Backroom = false;
            ShopUnlocks.Ensure();
            ShopUnlocks.MarkSeen();
            ResetPreview();
        }

        public static void End()
        {
            Items.Clear();
            Backroom = false;
            Car = null;
            Preview = null;
        }

        public static OwnedCar Copy(OwnedCar c) => c == null ? null : JsonUtility.FromJson<OwnedCar>(JsonUtility.ToJson(c));

        // ---- prices / states ---------------------------------------------------------------------------------------

        /// <summary>GetPartPrice (CM:261-285): 0 in the backroom; perf by tier; visual by part level.</summary>
        public static int PriceOf(SelectablePart p)
        {
            if (p == null || Backroom) return 0;
            return p.IsPerf ? ShopRules.PerfCost(CarParts.Attribs, Car?.Car, p.PhysType, p.Level) : PriceOf(p.Slot, p.Part);
        }

        public static int PriceOf(int slot, CarPartDb.Part part) =>
            Backroom || part == null ? 0 : ShopRules.VisualCost(CarParts.Attribs, slot, part.Level);

        /// <summary>The unlock level of an item: perf tier M − N + i; decals fixed per location (CM:572-719).</summary>
        public static int UnlockLevel(SelectablePart p)
        {
            if (p.IsPerf) return ShopRules.Tier(p.PhysType, CarParts.NumPackages(Car?.Car, p.PhysType), p.Level);
            int e = ShopRules.SlotUnlockable(p.Slot);
            return e == ShopRules.U_DecalWindshield ? 1 : e == ShopRules.U_DecalLeftDoor ? 2 : e == ShopRules.U_DecalLeftQp ? 3 : p.Level;
        }

        static int Entity(SelectablePart p) => p.IsPerf ? ShopRules.PerfUnlockable(p.PhysType) : ShopRules.SlotUnlockable(p.Slot);

        /// <summary>IsPartLocked: not unlocked for the career / quick race at its level (backroom rule in the backroom).</summary>
        public static bool IsLocked(SelectablePart p)
        {
            if (p == null || p.Junkman) return false;
            if (!p.IsPerf && (p.Part == null || p.Level == 0) && ShopRules.SlotUnlockable(p.Slot) != ShopRules.U_DecalWindshield
                && ShopRules.SlotUnlockable(p.Slot) != ShopRules.U_DecalLeftDoor && ShopRules.SlotUnlockable(p.Slot) != ShopRules.U_DecalLeftQp) return false;
            return !ShopUnlocks.IsUnlocked(Entity(p), UnlockLevel(p), Backroom);
        }

        public static bool IsNew(SelectablePart p) => p != null && !IsLocked(p) && ShopUnlocks.IsNew(Entity(p), UnlockLevel(p));

        public static bool IsInstalled(SelectablePart p)
        {
            if (p == null || Car == null) return false;
            if (p.IsPerf) return p.Junkman ? CarParts.HasJunkman(Car, p.PhysType) : CarParts.PerfLevel(Car, p.PhysType) == p.Level;
            var inst = CarParts.Installed(Car, p.Slot);
            if (inst == null) return p.Part == null || p.Part == CarParts.Stock(Car.Car, p.Slot);
            return p.Part == inst;
        }

        /// <summary>IsPartTypeInCart: the cart item for the same physics type / slot.</summary>
        public static CartItem TypeInCart(SelectablePart p) =>
            p == null ? null : Items.FirstOrDefault(i => p.IsPerf ? i.Buy.IsPerf && i.Buy.PhysType == p.PhysType : !i.Buy.IsPerf && i.Buy.Slot == p.Slot);

        /// <summary>IsPartInCart: this exact part / package.</summary>
        public static CartItem InCart(SelectablePart p) =>
            p == null ? null : Items.FirstOrDefault(i => p.IsPerf ? i.Buy.IsPerf && i.Buy.PhysType == p.PhysType && i.Buy.Level == p.Level
                                                                     : !i.Buy.IsPerf && i.Buy.Slot == p.Slot && i.Buy.Part == p.Part);

        // ---- cart --------------------------------------------------------------------------------------------------

        /// <summary>
        /// AddToCart (CM:86-112): replaces the item of the same slot / type (keeping its trade-in); a tradeable slot
        /// (CanTradeIn) gets the installed part as trade-in, priced like a purchase.
        /// </summary>
        public static void Add(SelectablePart part)
        {
            var old = TypeInCart(part);
            SelectablePart tradeIn = null;
            if (old != null)
            {
                if (!part.IsPerf && ShopRules.CanTradeIn(part.Slot)) tradeIn = old.TradeIn;
                Items.Remove(old);
            }
            else if (!part.IsPerf && ShopRules.CanTradeIn(part.Slot))
            {
                var inst = CarParts.InstalledOrStock(Car, part.Slot);
                if (inst != null) tradeIn = SelectablePart.Visual(part.Slot, inst);
            }
            Items.Add(new CartItem { Buy = part, TradeIn = tradeIn });
            ResetPreview();
        }

        public static void Remove(CartItem item)
        {
            if (item != null && Items.Remove(item)) ResetPreview();
        }

        /// <summary>
        /// Accept on a part screen (CC:2283-2380): locked → refused; installed → drops a different cart item of the
        /// slot; else into the cart. Returns a short status for the screen.
        /// </summary>
        public static string Select(SelectablePart p)
        {
            if (p == null) return null;
            if (IsLocked(p)) return "Locked";
            if (InCart(p) != null) return "In cart";
            if (IsInstalled(p))
            {
                var other = TypeInCart(p);
                if (other != null) Remove(other);
                return "Installed";
            }
            if (Backroom && !HasMarkerFor(p)) return "No marker";
            Add(p);
            return "Added to cart";
        }

        /// <summary>Backroom: the item needs one marker of its type (more items of a type than markers can't go in).</summary>
        public static bool HasMarkerFor(SelectablePart p)
        {
            int m = p.IsPerf ? ShopRules.PerfMarker(p.PhysType) : ShopRules.SlotMarker(p.Slot);
            if (m == 0) return true;
            int used = Items.Count(i => i.Buy != p && (i.Buy.IsPerf ? ShopRules.PerfMarker(i.Buy.PhysType) : ShopRules.SlotMarker(i.Buy.Slot)) == m && TypeInCart(p) != i);
            return ShopMarkers.Count(m) > used;
        }

        /// <summary>PreviewPart / PreviewPerfPkg: shows a highlighted part on the preview car without buying it.</summary>
        public static void PreviewItem(SelectablePart p)
        {
            ResetPreview();
            if (p != null) Install(Preview, p);
        }

        /// <summary>Several PreviewPart calls in a row (numbers, vinyl colours): the installed record, the cart, then these.</summary>
        public static void PreviewItems(IEnumerable<SelectablePart> parts)
        {
            ResetPreview();
            if (Preview == null) return;
            foreach (var p in parts) if (p != null) Install(Preview, p);
        }

        /// <summary>ResetPreview (CM:327-343): the installed record plus every cart item.</summary>
        public static void ResetPreview()
        {
            if (Car == null) { Preview = null; return; }
            Preview = Copy(Car);
            foreach (var i in Items) Install(Preview, i.Buy);
        }

        static void Install(OwnedCar c, SelectablePart p)
        {
            if (p.IsPerf)
            {
                if (p.Junkman) { if (CarParts.CanInstallJunkman(c, p.PhysType)) c.Junkman |= 1 << p.PhysType; }
                else CarParts.SetPerfLevel(c, p.PhysType, Mathf.Clamp(p.Level, 0, CarParts.NumPackages(c.Car, p.PhysType)));
            }
            else CarParts.SetInstalled(c, p.Slot, p.Part);
        }

        // ---- totals and checkout -------------------------------------------------------------------------------------

        static bool Counted(CartItem i) => i.Buy.IsPerf || !(ShopRules.FreeSlot(i.Buy.Slot) || i.Buy.Slot == ShopRules.Slot.RightDoorRNumber);

        /// <summary>CCT_PART_PRICES (CM:178-204): active items, backroom = 1 each (marker count).</summary>
        public static int PartsTotal => Items.Where(i => i.Active && Counted(i)).Sum(i => Backroom ? 1 : i.PartPrice);

        /// <summary>CCT_TRADE_IN with the trade-in factor applied once [?: the decomp applies it twice here].</summary>
        public static int TradeInTotal => -Items.Where(i => i.Active && i.TradeIn != null).Sum(i => i.TradeInPrice);

        public static int Total => PartsTotal + (Backroom ? 0 : TradeInTotal);

        public static bool HasActive => Items.Any(i => i.Active && Counted(i));

        /// <summary>Accept in the cart (CC:1218-1283): front-room career checkout needs the total in cash.</summary>
        public static bool CanAfford => !Career || Backroom || Total <= GameSession.Profile.Cash;

        /// <summary>
        /// Checkout (CM:206-238): per active item, career front room pays partPrice − tradeIn (SpendCash clamps at 0,
        /// a negative cost is paid out), backroom spends one marker; then installs it and, for a visual part, cools the
        /// car (UpdateHeatOnVehicle: fecooling × 0.75 in the backroom). Empties the cart.
        /// </summary>
        public static void Checkout()
        {
            var prof = GameSession.Profile;
            foreach (var item in Items.ToList())
            {
                if (!item.Active) continue;
                var p = item.Buy;
                if (Career && p.Slot != ShopRules.Slot.RightDoorRNumber)
                {
                    if (!Backroom)
                    {
                        int cost = item.PartPrice - item.TradeInPrice;
                        prof.Cash = cost >= 0 ? Mathf.Max(0, prof.Cash - cost) : prof.Cash - cost;
                    }
                    else ShopMarkers.Use(p.IsPerf ? ShopRules.PerfMarker(p.PhysType) : ShopRules.SlotMarker(p.Slot));
                }
                Install(Car, p);
                if (!p.IsPerf && Career)
                    CarParts.SetHeat(Car, CarParts.HeatOf(Car) * ShopRules.CoolingFactor(CarParts.Attribs, p.Slot, Backroom));
            }
            Items.Clear();
            ResetPreview();
            prof?.Save();
        }

        /// <summary>Back from the cart: unchecked items leave the cart (CC:1284-1338).</summary>
        public static void DropInactive()
        {
            // ClearUncheckedItems (CC:1100): an unchecked vinyl takes its colour items (VINYL_COLOUR0_0..3) with it
            bool vinylOff = Items.Any(i => !i.Active && !i.Buy.IsPerf && i.Buy.Slot == ShopRules.Slot.VinylLayer);
            Items.RemoveAll(i => !i.Active || (vinylOff && !i.Buy.IsPerf && i.Buy.Slot >= 79 && i.Buy.Slot <= 82));
            ResetPreview();
        }

        /// <summary>GetCartHeat: the car's heat after every active visual item (customization.md §6), 1..5 meter.</summary>
        public static float CartHeat
        {
            get
            {
                float h = CarParts.HeatOf(Car);
                foreach (var i in Items)
                    if (i.Active && !i.Buy.IsPerf) h *= ShopRules.CoolingFactor(CarParts.Attribs, i.Buy.Slot, Backroom);
                return h;
            }
        }

        /// <summary>GetPreviewHeat: the highlighted part (if not installed) plus the active cart items of other slots.</summary>
        public static float PreviewHeat(SelectablePart highlighted)
        {
            float h = CarParts.HeatOf(Car);
            foreach (var i in Items)
                if (i.Active && !i.Buy.IsPerf && (highlighted == null || highlighted.IsPerf || i.Buy.Slot != highlighted.Slot))
                    h *= ShopRules.CoolingFactor(CarParts.Attribs, i.Buy.Slot, Backroom);
            if (highlighted != null && !highlighted.IsPerf && !IsInstalled(highlighted))
                h *= ShopRules.CoolingFactor(CarParts.Attribs, highlighted.Slot, Backroom);
            return h;
        }
    }
}
