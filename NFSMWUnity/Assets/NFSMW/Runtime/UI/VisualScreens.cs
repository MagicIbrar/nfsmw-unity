using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>Shared bits of the customize screens: preview car, part lists, the cart hand-off (customization.md §2, §8).</summary>
    public static class ShopUi
    {
        /// <summary>The showroom shows the preview record (installed parts + cart + highlighted part), restyled in place.</summary>
        public static void ShowPreview(FrontEnd fe)
        {
            var c = ShopCart.Preview ?? GameSession.ActiveCar;
            if (fe?.Showroom && c != null) fe.Showroom.Show(c.Car, c.Paint, c);
        }

        /// <summary>CarCustomizeManager::PreviewPart(slot, part): the highlighted part on the preview car, nothing bought.</summary>
        public static void PreviewPart(FrontEnd fe, SelectablePart p)
        {
            ShopCart.PreviewItem(p);
            ShowPreview(fe);
            if (p != null && !p.IsPerf && p.Slot == ShopRules.Slot.BasePaint && ShopCart.Preview != null && fe?.Showroom) fe.Showroom.Repaint(ShopCart.Preview.Paint);
        }

        /// <summary>
        /// CarCustomizeManager::GetCarPartList (CM:1200-1287) as SelectableParts: CarParts.PartList (model / texture / brand /
        /// group filters) then the room rules — backroom keeps the parts the backroom rule unlocks (decal locations too), the
        /// front room drops level-7 (UNIQUE) parts until the career is beaten.
        /// </summary>
        public static List<SelectablePart> PartList(int slot, uint param = 0, int vinylGroup = -1)
        {
            var list = new List<SelectablePart>();
            var car = ShopCart.Car ?? GameSession.ActiveCar;
            if (car == null) return list;
            int ent = ShopRules.SlotUnlockable(slot);
            bool decal = ent == ShopRules.U_DecalLeftDoor || ent == ShopRules.U_DecalLeftQp || ent == ShopRules.U_DecalWindshield;
            int decalLevel = ent == ShopRules.U_DecalLeftDoor ? 2 : ent == ShopRules.U_DecalLeftQp ? 3 : 1;
            foreach (var p in CarParts.PartList(car.Car, slot, param, vinylGroup))
            {
                if (decal)
                {
                    if (ShopCart.Backroom && !ShopUnlocks.IsUnlocked(ent, decalLevel, true)) continue;
                    var sp = SelectablePart.Visual(slot, p);
                    sp.Level = decalLevel;
                    list.Add(sp);
                    continue;
                }
                if (ShopCart.Backroom) { if (!ShopUnlocks.PartUnlocked(slot, p, true)) continue; }
                else if (!ShopUnlocks.CareerBeaten && p.Level == ShopRules.JunkmanLevel) continue;
                list.Add(SelectablePart.Visual(slot, p));
            }
            return list;
        }

        /// <summary>
        /// Leaving a Parts / Performance / Visual menu (CC:1296-1317): active cart items force the cart open (0x1720B124);
        /// a cart with only unchecked items is emptied. Returns true when the cart was opened instead of leaving.
        /// </summary>
        public static bool LeaveMenu(FrontEnd fe)
        {
            if (ShopCart.HasActive) { fe.Push(new ShoppingCartScreen()); return true; }
            if (ShopCart.Items.Count > 0) { ShopCart.Items.Clear(); ShopCart.ResetPreview(); }
            return false;
        }

        /// <summary>CustomizeShoppingCart::ExitShoppingCart (CC:854): back to CustomizeMain, leaving the backroom.</summary>
        public static void ExitShoppingCart(FrontEnd fe)
        {
            bool backroom = ShopCart.Backroom;
            if (backroom) { ShopCart.Backroom = false; ShopCart.ResetPreview(); }
            while (fe.Top != null && !(fe.Top is CustomizeScreen) && !(fe.Top is CareerMenuScreen) && !(fe.Top is MyCarsScreen)) fe.Pop();
            if (fe.Top is CustomizeScreen) fe.Replace(new CustomizeScreen(false));
            else fe.Push(new CustomizeScreen(false));
        }

        /// <summary>Showcase.fng from a customize screen (PAD_BUTTON0, key 3): the preview car.</summary>
        public static void Showcase(FrontEnd fe) => fe.Push(new ShowcaseScreen(ShopCart.Preview ?? GameSession.ActiveCar));

        /// <summary>The display name of a part option: its LANGUAGEHASH string, else the CarPartDB name (CustomizeParts::RefreshHeader, CC:2871).</summary>
        public static string PartName(CarPartDb.Part p)
        {
            if (p == null) return FEDb.L(0x60a662f5, "Stock");
            uint h = p.U(CarPartDb.Attr.LANGUAGEHASH);
            if (h != 0 && FrontEndData.Strings != null && FrontEndData.Strings.TryGet(h, out var s)) return s;
            return p.Name ?? "";
        }
    }

    /// <summary>
    /// CustomizeCategoryScreen (CarCustomize.cpp:1217-1338): an IconScrollerMenu of CustomizeMainOptions with the heat
    /// meter. RefreshHeader shows the category's lock / new icon (0xCFFB7033: 0xF0574BB2 locked, 0xCFFB7033 new, APPEAR),
    /// the heat meter at the actual / cart heat and, in the career, either the backroom marker lines (0x63CA8308 name,
    /// 0x83E3CD39 count, 0x23D918FE customize markers) or the cart total 0x7A6D2F71 and cash 0xC60ADCFD; outside the career
    /// the meter and the cash box 0x8D1559A4 are hidden. PAD_START opens the cart; PAD_BACK from Parts / Performance /
    /// Visual with active cart items opens the cart instead (0x1720B124), with only inactive items it empties the cart.
    /// </summary>
    public abstract class CustomizeCategoryScreen : CarouselScreen
    {
        protected int Category, FromCategory;
        protected readonly CustomizeMeter HeatMeter = new CustomizeMeter();
        public override bool ShowCar => true;
        public override string Platform => ShopCart.Backroom ? "CUSTOMIZATION_SHOP_BACKROOM.BIN" : "CUSTOMIZATION_SHOP.BIN";

        protected sealed class MainOption
        {
            public int Cat, UnlockStatus = CPS.Available;
            public Action Open;
            public SelectablePart StockPart;   // SetStockPartOption
        }

        /// <summary>CustomizeCategoryScreen::AddCustomOption (CC:1266): skipped in the backroom when locked there; status locked / new.</summary>
        protected int AddCustomOption(uint tex, uint name, int toCat, Action open)
        {
            if (ShopCart.Career && ShopCart.Backroom && CustomizeTables.IsCategoryLocked(toCat, true)) return -1;
            var mo = new MainOption { Cat = toCat, Open = open };
            if (CustomizeTables.IsCategoryLocked(toCat, false)) mo.UnlockStatus = CPS.Locked;
            else if (CustomizeTables.IsCategoryNew(toCat)) mo.UnlockStatus = CPS.New;
            Options.Add(new Option { IconHash = tex, NameHash = name, Tag = mo, Run = () => mo.Open?.Invoke() });
            return Options.Count;
        }

        /// <summary>SetStockPartOption (CC:1340 / 1705 / 1780): reacts at once — the stock part into the cart unless installed.</summary>
        protected void AddStockOption(uint tex, int toCat, SelectablePart stock)
        {
            var mo = new MainOption { Cat = toCat, StockPart = stock };
            Options.Add(new Option { IconHash = tex, NameHash = 0, Tag = mo, Run = () => OnStockOption(mo) });
        }

        protected virtual void OnStockOption(MainOption mo)
        {
            if (!ShopCart.IsInstalled(mo.StockPart)) ShopCart.Add(mo.StockPart);
            ShopUi.ShowPreview(FE);
            RefreshCategoryHeader();
        }

        protected MainOption CurrentMain => Selected >= 0 && Selected < Options.Count ? Options[Selected].Tag as MainOption : null;

        public override void Open()
        {
            if (ShopCart.Car == null) ShopCart.Begin(GameSession.ActiveCar);
            HeatMeter.Init(View, 1f, 5f, ShopCart.ActualHeat, ShopCart.CartHeat);
            base.Open();
            ShopUi.ShowPreview(FE);
            RefreshCategoryHeader();
        }

        public override void Resume()
        {
            base.Resume();
            ShopUi.ShowPreview(FE);
            RefreshCategoryHeader();
        }

        protected override void OnSelectionChanged() => RefreshCategoryHeader();

        /// <summary>CustomizeCategoryScreen::RefreshHeader (CC:1233).</summary>
        protected virtual void RefreshCategoryHeader()
        {
            var mo = CurrentMain;
            int status = mo?.UnlockStatus ?? CPS.Available;
            if (status == CPS.Locked || status == CPS.New)
            {
                FEngIf.SetVisible(View, 0xcffb7033, true);
                FEngIf.SetTextureHash(View, 0xcffb7033, status == CPS.Locked ? 0xf0574bb2u : 0xcffb7033u);
                FEngIf.SetScript(View, 0xcffb7033, FEView.Appear);
            }
            else FEngIf.SetVisible(View, 0xcffb7033, false);
            if (ShopCart.Career)
            {
                HeatMeter.SetCurrent(ShopCart.ActualHeat);
                HeatMeter.SetPreview(ShopCart.CartHeat);
                HeatMeter.Draw();
                if (ShopCart.Backroom)
                {
                    FEngIf.SetLanguageHash(View, 0x63ca8308, CustomizeTables.MarkerNameHash(Category));
                    FEngIf.Printf(View, 0x83e3cd39, FEngIf.D(CustomizeTables.NumMarkers(Category)));
                    FEngIf.Printf(View, 0x23d918fe, FEngIf.D(ShopMarkers.CustomizeCount));
                }
                else
                {
                    FEngIf.Printf(View, 0x7a6d2f71, FEngIf.D(ShopCart.Total));
                    FEngIf.Printf(View, 0xc60adcfd, FEngIf.D(GameSession.Profile?.Cash ?? 0));
                }
            }
            else
            {
                HeatMeter.SetVisibility(false);
                FEngIf.SetVisible(View, 0x8d1559a4, false);
            }
        }

        public override void Tick(MenuInput input)
        {
            if (input.Key4) { FE.Push(new ShoppingCartScreen()); return; }   // __PAD_START__ → ShowShoppingCart
            if (input.Key3) { ShopUi.Showcase(FE); return; }                 // __PAD_BUTTON0__ → Showcase.fng
            base.Tick(input);
        }

        /// <summary>__PAD_BACK__ (CC:1302): the three main menus force the cart open with active items, else empty it; then leave.</summary>
        protected override void OnBack()
        {
            if (Category == CC.Parts || Category == CC.Performance || Category == CC.Visual)
            {
                if (ShopCart.HasActive) { FE.Push(new ShoppingCartScreen()); return; }
                ShopCart.Items.Clear();
                ShopCart.ResetPreview();
            }
            ShopUi.ShowPreview(FE);
            FE.Pop();
        }
    }

    /// <summary>
    /// CustomizeSub (CarCustomize.cpp:1347-1880): the Parts / Performance / Visual menus and the rim brand, vinyl group,
    /// decal location and decal position menus — the option tables of CustomizeSub::Setup*, the stock entries of the rim
    /// brands and vinyl groups (SetStockPartOption), the in-cart / installed icon 0xD0582FEB (0x1A777E25 / 0x696AE039) on
    /// those two lists, the "max performance" prompt 0x5AEC8D91 (quick race Performance, key 2 → 0x892CB612 → MaxOut) and
    /// the rim paint check (stock rims → 0xBDB19A9F). Titles are lower-cased into TITLE_GROUP.
    /// </summary>
    public sealed class VisualScreen : CustomizeCategoryScreen
    {
        public enum Kind { Parts, Visual, Performance, RimBrands, VinylTypes, DecalLocation, DecalLeftDoor, DecalRightDoor }
        readonly Kind _kind;
        int _installedIndex, _inCartIndex;
        uint _titleHash;

        public VisualScreen() : this(Kind.Visual) { }
        public VisualScreen(Kind kind) { _kind = kind; Category = CatOf(kind); }
        public VisualScreen(int category) : this(KindOf(category)) { }

        static int CatOf(Kind k) => k switch
        {
            Kind.Parts => CC.Parts, Kind.Visual => CC.Visual, Kind.Performance => CC.Performance, Kind.RimBrands => CC.RimBrands,
            Kind.VinylTypes => CC.VinylTypes, Kind.DecalLocation => CC.DecalLocation, Kind.DecalLeftDoor => CC.DecalLeftDoor, _ => CC.DecalRightDoor,
        };
        static Kind KindOf(int c) => c switch
        {
            CC.Parts => Kind.Parts, CC.Performance => Kind.Performance, CC.RimBrands => Kind.RimBrands, CC.VinylTypes => Kind.VinylTypes,
            CC.DecalLocation => Kind.DecalLocation, CC.DecalLeftDoor => Kind.DecalLeftDoor, CC.DecalRightDoor => Kind.DecalRightDoor, _ => Kind.Visual,
        };

        /// <summary>g_pCustomizeSubPkg / g_pCustomizeSubTopPkg (CustomizeMain::SetScreenNames, CC:1987): the brand / group / decal lists use CustomizeGenericTop.</summary>
        public override string Package
        {
            get
            {
                bool top = _kind >= Kind.RimBrands;
                string name = top ? "CustomizeGenericTop" : "CustomizeCategory";
                if (ShopCart.Backroom && FrontEndData.Packages.ContainsKey(name + "_BACKROOM")) return name + "_BACKROOM";
                return FrontEndData.Packages.ContainsKey(name) ? name : "CustomizeMain";
            }
        }

        protected override void Build()
        {
            Options.Clear();
            bool br = ShopCart.Backroom, hero = CarParts.IsHeroCar(ShopCart.Car?.Car ?? GameSession.ActiveCar?.Car);
            _installedIndex = _inCartIndex = 0;
            _titleHash = CustomizeTables.SubTitle(Category, br);
            switch (_kind)
            {
                case Kind.Parts:
                    ShopCart.InParts = true; ShopCart.InPerformance = false;
                    foreach (var o in CustomizeTables.Parts(br)) AddCustomOption(o.Tex, o.Name, o.Cat, () => OpenCategory(o.Cat));
                    break;
                case Kind.Performance:
                    ShopCart.InPerformance = true; ShopCart.InParts = false;
                    foreach (var o in CustomizeTables.Performance(br)) AddCustomOption(o.Tex, o.Name, o.Cat, () => OpenCategory(o.Cat));
                    break;
                case Kind.Visual:
                    ShopCart.InParts = false; ShopCart.InPerformance = false;
                    foreach (var o in CustomizeTables.Visual(br, hero)) AddCustomOption(o.Tex, o.Name, o.Cat, () => OpenCategory(o.Cat));
                    break;
                case Kind.RimBrands:
                {
                    var stock = SelectablePart.Visual(ShopRules.Slot.FrontWheel, ShopCart.StockPart(ShopRules.Slot.FrontWheel));
                    AddStockOption(CustomizeTables.RimStockTex, CC.RimBrandStock, stock);
                    foreach (var o in CustomizeTables.RimBrandOpts) AddCustomOption(o.Tex, o.Name, o.Cat, () => OpenCategory(o.Cat));
                    var item = ShopCart.SlotInCart(ShopRules.Slot.FrontWheel);
                    if (item?.Buy.Part != null) _inCartIndex = CustomizeTables.RimBrandIndex(item.Buy.Part.Brand);
                    var inst = ShopCart.InstalledPart(ShopRules.Slot.FrontWheel);
                    if (inst != null) _installedIndex = CustomizeTables.RimBrandIndex(inst.Brand);
                    Selected = Mathf.Max(0, (_inCartIndex != 0 ? _inCartIndex : _installedIndex != 0 ? _installedIndex : 1) - 1);
                    break;
                }
                case Kind.VinylTypes:
                {
                    var stock = SelectablePart.Visual(ShopRules.Slot.VinylLayer, null);
                    AddStockOption(CustomizeTables.VinylStockTex, CC.VinylGroupStock, stock);
                    foreach (var o in CustomizeTables.VinylGroupOpts) AddCustomOption(o.Tex, o.Name, o.Cat, () => OpenCategory(o.Cat));
                    var item = ShopCart.SlotInCart(ShopRules.Slot.VinylLayer);
                    if (item != null) _inCartIndex = item.Buy.Part != null ? CustomizeTables.VinylGroupIndex(item.Buy.Part.Group) : 1;
                    var inst = ShopCart.InstalledPart(ShopRules.Slot.VinylLayer);
                    _installedIndex = inst != null ? CustomizeTables.VinylGroupIndex(inst.Group) : 1;
                    Selected = Mathf.Max(0, (_inCartIndex != 0 ? _inCartIndex : _installedIndex != 0 ? _installedIndex : 1) - 1);
                    break;
                }
                case Kind.DecalLocation:
                    foreach (var o in CustomizeTables.DecalLocationOpts) AddCustomOption(o.Tex, o.Name, o.Cat, () => OpenCategory(o.Cat));
                    break;
                case Kind.DecalLeftDoor:
                case Kind.DecalRightDoor:
                    foreach (var o in CustomizeTables.DecalPositionOpts(Category)) AddCustomOption(o.Tex, o.Name, o.Cat, () => OpenCategory(o.Cat));
                    break;
            }
            // AddCustomOption skips backroom-locked categories: the sub menu's remembered index may now be out of range
            if (_kind <= Kind.Performance) Selected = Mathf.Clamp(FEDb.LastButton(LastKey) - 1, 0, Mathf.Max(0, Options.Count - 1));
        }

        string LastKey => "CustomizeSub/" + Category;

        /// <summary>CustomizeMainOption::React: QueuePackageSwitch(ToPkg, Category | FromCategory) — the port pushes the target screen.</summary>
        void OpenCategory(int cat)
        {
            FEDb.SetLastButton(LastKey, Selected + 1);
            if (Category == CC.Visual && cat == CC.RimPaint)
            {
                // CustomizeSub::NotificationMessage (CC:1398): rim paint needs aftermarket rims
                if (ShopCart.StockPart(ShopRules.Slot.FrontWheel) == ShopCart.InstalledPart(ShopRules.Slot.FrontWheel) || ShopCart.InstalledPart(ShopRules.Slot.FrontWheel) == null)
                {
                    FEDialog.ShowOneButton(FE, FEDialog.Title.Alert, 0x417b2601, FEDb.L(0xbdb19a9f), null);
                    return;
                }
                FE.Push(new PaintScreen(CC.RimPaint));
                return;
            }
            if (CC.IsDecalLocation(cat)) DecalsScreen.CurrentDecalLocation = cat;
            switch (cat)
            {
                case CC.Parts: case CC.Performance: case CC.Visual: case CC.RimBrands: case CC.VinylTypes: case CC.DecalLocation:
                case CC.DecalLeftDoor: case CC.DecalRightDoor:
                    FE.Push(new VisualScreen(cat)); break;
                case CC.BodyKit: case CC.Hoods: case CC.RoofScoops: case CC.WindowTint: case CC.CustomHud:
                    FE.Push(new PartScreen(cat)); break;
                case CC.Spoilers: FE.Push(new SpoilerScreen()); break;
                case CC.Paint: FE.Push(new PaintScreen(CC.Paint)); break;
                case CC.Numbers: FE.Push(new NumbersScreen()); break;
                case CC.DecalWindshield: case CC.DecalRearWindow: case CC.DecalLeftQp: case CC.DecalRightQp:
                    FE.Push(new DecalsScreen(cat)); break;
                default:
                    if (CC.IsPerf(cat)) FE.Push(new PerformanceScreen(cat));
                    else if (CC.IsRimBrand(cat)) FE.Push(new RimsScreen(cat));
                    else if (CC.IsVinylGroup(cat)) FE.Push(new PartScreen(cat));
                    else if (CC.IsDecalSlot(cat)) FE.Push(new DecalsScreen(cat));
                    break;
            }
        }

        /// <summary>The stock entries (CC:1394-1433): installed and a cart item of the slot → remove it (vinyl colours too).</summary>
        protected override void OnStockOption(MainOption mo)
        {
            int slot = mo.Cat == CC.RimBrandStock ? ShopRules.Slot.FrontWheel : ShopRules.Slot.VinylLayer;
            if (ShopCart.IsInstalled(mo.StockPart) && _inCartIndex != 0)
            {
                if (slot == ShopRules.Slot.VinylLayer)
                    for (int i = 0; i <= 2; i++) ShopCart.Remove(ShopCart.SlotInCart(ShopRules.Slot.VinylColour0 + i));
                ShopCart.Remove(ShopCart.SlotInCart(slot));
                _inCartIndex = 0;
            }
            else base.OnStockOption(mo);
            if (!ShopCart.IsInstalled(mo.StockPart) && ShopCart.SlotInCart(slot) != null) _inCartIndex = 1;
            ShopUi.ShowPreview(FE);
            RefreshCategoryHeader();
        }

        /// <summary>CustomizeSub::RefreshHeader (CC:1518).</summary>
        protected override void RefreshCategoryHeader()
        {
            base.RefreshCategoryHeader();
            if (View == null) return;
            FEngIf.Printf(View, 0xB71B576D, (_titleHash != 0 ? FEDb.L(_titleHash) : "").ToLowerInvariant());
            if (Category == CC.RimBrands || Category == CC.VinylTypes)
            {
                int index = Selected + 1;
                if (index == _inCartIndex) { FEngIf.SetVisible(View, 0xd0582feb, true); FEngIf.SetTextureHash(View, 0xd0582feb, 0x1a777e25); }
                else if (index == _installedIndex) { FEngIf.SetVisible(View, 0xd0582feb, true); FEngIf.SetTextureHash(View, 0xd0582feb, 0x696ae039); }
                else FEngIf.SetVisible(View, 0xd0582feb, false);
            }
            else FEngIf.SetVisible(View, 0xd0582feb, false);
            FEngIf.SetVisible(View, 0x5aec8d91, !ShopCart.Career && Category == CC.Performance);
        }

        public override void Resume()
        {
            // 0x5A928018 (the cart was left without checkout): a stock pick no longer in the cart loses its mark (CC:1366)
            if (Category == CC.RimBrands && ShopCart.SlotInCart(ShopRules.Slot.FrontWheel) == null) _inCartIndex = 0;
            if (Category == CC.VinylTypes && ShopCart.SlotInCart(ShopRules.Slot.VinylLayer) == null) _inCartIndex = 0;
            base.Resume();
        }

        public override void Tick(MenuInput input)
        {
            // __PAD_BUTTON4__ (key 2) outside the career on the Performance menu: confirm 0x892CB612 → MaxOutPerformance (CC:1495)
            if (input.Key2 && !ShopCart.Career && Category == CC.Performance)
            {
                FEDialog.ShowTwoButtons(FE, FEDialog.Title.Confirmation, 0x70E01038, 0x417B25E4,
                    () => { CustomizeScreen.MaxOutPerformance(); ShopUi.ShowPreview(FE); RefreshCategoryHeader(); }, null, null, 0, FEDb.L(0x892cb612));
                return;
            }
            base.Tick(input);
        }
    }

    /// <summary>
    /// CustomizationScreen (CarCustomize.cpp:2257-2456): an IconScrollerMenu of CustomizePartOptions with the
    /// CustomizationScreenHelper. Accept on the highlighted part: locked → flash the lock icon (wrong sound); in the cart →
    /// flash; installed → a cart item of the same slot / type is removed; else AddToCart (replacing the slot's item) and the
    /// header refreshes. The previewed part follows the highlight 0.3 s after the last scroll (ScrollTime / bNeedsRefresh).
    /// PAD_START opens the cart, PAD_BUTTON0 the showcase; the title is the category's, lower-cased; "trade in value" =
    /// the installed part's cost × gTradeInFactor for tradeable slots.
    /// </summary>
    public abstract class CustomizationScreen : CarouselScreen
    {
        protected CustomizeHelper DisplayHelper;
        protected int Category, FromCategory;
        protected float ScrollTime = -10f;
        protected bool NeedsRefresh;
        public override bool ShowCar => true;
        public override string Platform => ShopCart.Backroom ? "CUSTOMIZATION_SHOP_BACKROOM.BIN" : "CUSTOMIZATION_SHOP.BIN";
        protected uint TitleHash { get => DisplayHelper.TitleHash; set => DisplayHelper.TitleHash = value; }

        protected sealed class PartOption
        {
            public SelectablePart Part;
            public uint UnlockBlurb, DescHash;
            public bool Locked;
        }

        protected string PackageFor(string front) => ShopCart.Backroom && FrontEndData.Packages.ContainsKey(front + "_BACKROOM") ? front + "_BACKROOM" : front;

        /// <summary>CustomizationScreen::AddPartOption (CC:2407).</summary>
        protected void AddPartOption(SelectablePart part, uint tex, uint nameHash, uint descHash, uint unlockHash, bool locked, string label = null)
        {
            var po = new PartOption { Part = part, UnlockBlurb = unlockHash, DescHash = descHash, Locked = locked };
            Options.Add(new Option { IconHash = tex, NameHash = nameHash, Label = label ?? (part != null && !part.IsPerf ? ShopUi.PartName(part.Part) : ""), Tag = po, Run = Accept });
        }

        protected PartOption CurrentOption => Selected >= 0 && Selected < Options.Count ? Options[Selected].Tag as PartOption : null;
        protected SelectablePart SelectedPart => CurrentOption?.Part;

        protected abstract void Setup();

        protected override void Build()
        {
            if (ShopCart.Car == null) ShopCart.Begin(GameSession.ActiveCar);
            DisplayHelper = new CustomizeHelper(View);
            Options.Clear();
            Setup();
        }

        public override void Open()
        {
            base.Open();
            DisplayHelper.InitComplete = true;   // FEHASH_INITCOMPLETE [ours: at once; the package's INIT takes ~0.5 s]
            RefreshHeader();
        }

        public override void Resume()
        {
            base.Resume();
            // 0x5A928018 (cart left without checkout): parts no longer in the cart lose their mark — the port derives the state
            RefreshHeader();
        }

        protected override void OnSelectionChanged()
        {
            ScrollTime = Time.unscaledTime;
            RefreshHeader();
        }

        /// <summary>__PAD_ACCEPT__ (CC:2283-2380).</summary>
        protected virtual void Accept()
        {
            var sel = SelectedPart;
            if (sel == null) return;
            int state = ShopCart.StateOf(sel);
            if ((state & CPS.GameMask) == CPS.Locked) { DisplayHelper.PlayLocked(); return; }
            if ((state & CPS.PlayerMask) == CPS.InCart) { DisplayHelper.PlayInCart(); return; }
            if ((state & CPS.PlayerMask) == CPS.Installed)
            {
                var item = ShopCart.TypeInCart(sel);
                if (item != null) ShopCart.Remove(item);
                DisplayHelper.PlayInstalled();
                RefreshHeader();
                return;
            }
            // 0x91DFDF84: AddToCart replaces the slot's item
            ShopCart.Add(sel);
            RefreshHeader();
        }

        /// <summary>CustomizationScreen::RefreshHeader (CC:2382): title, career box, status / counter; subclasses preview and name the part.</summary>
        protected virtual void RefreshHeader()
        {
            if (View == null) return;
            DisplayHelper.DrawTitle();
            if (Options.Count > 0)
            {
                var sel = SelectedPart;
                int tradeIn = ShopCart.TradeInValueOf(sel);
                DisplayHelper.SetCareerStuff(sel, Category, tradeIn);
                DisplayHelper.SetPartStatus(sel, ShopCart.StateOf(sel), CurrentOption?.UnlockBlurb ?? 0, Selected + 1, Options.Count);
            }
            PartHeader();
        }

        /// <summary>The subclass part of RefreshHeader (preview, name line 0x5E7B09C9, filters).</summary>
        protected virtual void PartHeader() { }

        /// <summary>CustomizeParts / Spoiler / Decals: preview the highlighted part 0.3 s after the last scroll, else mark bNeedsRefresh.</summary>
        protected void PreviewSelected(SelectablePart p)
        {
            if (Time.unscaledTime - ScrollTime > 0.3f) ShopUi.PreviewPart(FE, p);
            else NeedsRefresh = true;
        }

        public override void Tick(MenuInput input)
        {
            if (NeedsRefresh && Time.unscaledTime - ScrollTime > 0.25f) { NeedsRefresh = false; RefreshHeader(); }   // FEMSG_SCREEN_TICK
            if (input.Key4) { FE.Push(new ShoppingCartScreen()); return; }
            if (input.Key3 && !CC.IsPerf(Category) && Category != CC.CustomHud) { ShopUi.Showcase(FE); return; }
            if (input.LTrigger) { OnTrigger(false); return; }
            if (input.RTrigger) { OnTrigger(true); return; }
            base.Tick(input);
        }

        protected virtual void OnTrigger(bool next) { }

        protected override void OnBack()
        {
            ShopCart.ResetPreview();
            ShopUi.ShowPreview(FE);
            FE.Pop();
        }

        protected string CarName => ShopCart.Car?.Car ?? GameSession.ActiveCar?.Car;
    }

    /// <summary>
    /// CustomizeParts (CarCustomize.cpp:2459-2900): body kits, hoods, roof scoops, window tint, custom HUDs and the vinyls of
    /// one group. Hoods / roofs with CARBONFIBRE use the carbon icon; the part name goes to 0x5E7B09C9 (LANGUAGEHASH or the
    /// DB name); vinyls with remappable colours (attribute 0x6212682B) label the accept button "Continue" (0x8098A54C) and
    /// open the vinyl colour screen on accept, HUDs open the gauge colour screen; the HUD preview (0xDEE8632B) shows the
    /// highlighted HUD's textures (HUDS_Custom_NN: TACH_FILL / &lt;rpm&gt;_LINES / TACH_NEEDLE / TURBO_*) in its colours.
    /// </summary>
    public class PartScreen : CustomizationScreen
    {
        int _slot, _vinylGroup = -1;
        bool _isVinyl;
        public override string Package => PackageFor(Category == CC.CustomHud ? "CustomHUD" : "CustomizeParts");

        public PartScreen(int category) { Category = category; }

        // legacy factories (QA tour / editor previews)
        public static PartScreen BodyKits() => new PartScreen(CC.BodyKit);
        public static PartScreen Hoods() => new PartScreen(CC.Hoods);
        public static PartScreen Roofs() => new PartScreen(CC.RoofScoops);
        public static PartScreen WindowTint() => new PartScreen(CC.WindowTint);
        public static PartScreen CustomHud() => new PartScreen(CC.CustomHud);
        public static CustomizationScreen Spoilers() => new SpoilerScreen();
        /// <summary>The rims of the installed brand (or the first brand) at the installed size.</summary>
        public static CustomizationScreen Rims()
        {
            var inst = ShopCart.InstalledPart(ShopRules.Slot.FrontWheel) ?? (GameSession.ActiveCar != null ? CarParts.Installed(GameSession.ActiveCar, ShopRules.Slot.FrontWheel) : null);
            int cat = CC.RimBrand5Zigen;
            if (inst != null) foreach (var o in CustomizeTables.RimBrandOpts) if (CustomizeTables.RimBrandHash(o.Cat) == inst.Brand) cat = o.Cat;
            return new RimsScreen(cat);
        }
        /// <summary>The vinyls of the installed vinyl's group (else flames).</summary>
        public static PartScreen Vinyls()
        {
            var inst = GameSession.ActiveCar != null ? CarParts.Installed(GameSession.ActiveCar, ShopRules.Slot.VinylLayer) : null;
            return new PartScreen(inst != null && inst.Group <= 7 ? CC.VinylGroupFlame + inst.Group : CC.VinylGroupFlame);
        }
        public static CustomizationScreen Numbers() => null;   // CustomizeNumbers is NumbersScreen (not an icon scroller)
        public static CustomizationScreen Decals(int slot, string title) => new DecalsScreen(slot >= ShopRules.Slot.DecalLeftDoor && slot <= ShopRules.Slot.DecalLeftDoor + 5 ? CC.DecalSlot1 + slot - ShopRules.Slot.DecalLeftDoor
            : slot >= ShopRules.Slot.DecalRightDoor && slot <= ShopRules.Slot.DecalRightDoor + 5 ? CC.DecalSlot1 + slot - ShopRules.Slot.DecalRightDoor
            : slot == ShopRules.Slot.DecalRearWindow ? CC.DecalRearWindow : slot == ShopRules.Slot.DecalLeftQuarter ? CC.DecalLeftQp : slot == ShopRules.Slot.DecalRightQuarter ? CC.DecalRightQp : CC.DecalWindshield);

        /// <summary>CustomizeParts::Setup (CC:2583).</summary>
        protected override void Setup()
        {
            var (title, icon, carbonIcon) = CustomizeTables.PartsScreenLook(Category, ShopCart.Backroom);
            TitleHash = title;
            _slot = CustomizeTables.SlotOfCategory(Category);
            _isVinyl = CC.IsVinylGroup(Category);
            _vinylGroup = _isVinyl ? CustomizeTables.VinylGroupOf(Category) : -1;
            CarPartDb.Part installed = null;
            bool found = false;
            if (Category == CC.CustomHud && ShopCart.TempColoredPart != null) { installed = ShopCart.TempColoredPart.Part; found = true; }
            if (_isVinyl && ShopCart.TempColoredPart != null) { installed = ShopCart.TempColoredPart.Part; found = true; }
            if (!found) installed = ShopCart.ActivePart(_slot);
            var list = ShopUi.PartList(_slot, 0, _vinylGroup);
            int installedIndex = 0, current = 1;
            foreach (var part in list)
            {
                uint unlock = CustomizeTables.UnlockHash(Category, ShopCart.UnlockLevel(part));
                uint tex = icon;
                if (_isVinyl)
                {
                    if (part.Part.Group != _vinylGroup) continue;
                }
                else if (carbonIcon != 0 && part.Part.Carbon) tex = carbonIcon;
                AddPartOption(part, tex, 0, 0, unlock, ShopCart.IsLocked(part));
                if (installed != null && part.Part == installed) installedIndex = current;
                current++;
            }
            Selected = Mathf.Max(0, installedIndex - 1);
            if (Category == CC.CustomHud) FEngIf.SetVisible(View, 0xdee8632b, false);
        }

        public override void Open()
        {
            base.Open();
            if (Category == CC.CustomHud) { FEngIf.SetScript(View, 0xDEE8632B, FEView.Appear); FEngIf.SetVisible(View, 0xDEE8632B, true); }   // ShowHudObjects
        }

        /// <summary>__PAD_ACCEPT__ (CC:2491): HUDs and multi-colour vinyls go to their colour screens (TempColoredPart) first.</summary>
        protected override void Accept()
        {
            var sel = SelectedPart;
            if (sel == null) return;
            if (Category == CC.CustomHud)
            {
                if ((ShopCart.StateOf(sel) & CPS.GameMask) == CPS.Locked) { DisplayHelper.PlayLocked(); return; }
                ShopCart.SetTempColoredPart(sel);
                FE.Push(new HudColorScreen(Category));
                return;
            }
            if (_isVinyl)
            {
                if ((ShopCart.StateOf(sel) & CPS.GameMask) == CPS.Locked) { DisplayHelper.PlayLocked(); return; }
                if (sel.Part != null && sel.Part.U(CustomizeTables.VinylColoursAttr) != 0)
                {
                    ShopCart.SetTempColoredPart(sel);
                    FE.Push(new PaintScreen(Category));
                    return;
                }
            }
            base.Accept();
        }

        /// <summary>CustomizeParts::RefreshHeader (CC:2871).</summary>
        protected override void PartHeader()
        {
            var sel = SelectedPart;
            if (sel == null) return;
            if (sel.Part != null && sel.Part.Has(CustomizeTables.VinylColoursAttr))
                FEngIf.SetLanguageHash(View, 0xb94139f4, sel.Part.U(CustomizeTables.VinylColoursAttr) != 0 ? 0x8098a54cu : 0x649f4a65u);
            if (Category == CC.CustomHud) { SetHudTextures(sel); SetHudColors(sel); }
            else PreviewSelected(sel);
            FEngIf.Printf(View, 0x5e7b09c9, ShopUi.PartName(sel.Part));
        }

        /// <summary>CustomizeParts::SetHUDTextures (CC:2819): the HUDINDEX part's tach textures.</summary>
        internal static void SetHudTextures(FEView v, CarPartDb.Part hud, string car)
        {
            if (v == null || hud == null) return;
            int rpm = CustomizeTables.TachRpm(car);
            int idx = hud.I(CustomizeTables.HudIndexAttr);
            HudTextures.Ensure(idx);
            FEngIf.SetTextureHash(v, 0xc0721eb9, FrontEndData.Hash($"{rpm:0000}_LINES_{idx:00}"));
            FEngIf.SetTextureHash(v, 0x5d19f25, FrontEndData.Hash($"TACH_FILL_{idx:00}"));
            FEngIf.SetTextureHash(v, 0xd312f0cb, FrontEndData.Hash($"TACH_NEEDLE_{idx:00}"));
            if (CustomizeTables.IsTurbo(car))
            {
                FEngIf.SetTextureHash(v, 0xc62ad685, FrontEndData.Hash($"TURBO_LINES_{idx:00}"));
                FEngIf.SetTextureHash(v, 0x8fe2a217, FrontEndData.Hash($"TURBO_NEEDLE_{idx:00}"));
                FEngIf.SetVisible(v, 0xc5d551b7, true);
            }
            else FEngIf.SetVisible(v, 0xc5d551b7, false);
        }

        void SetHudTextures(SelectablePart sel) => SetHudTextures(View, sel.Part, CarName);

        /// <summary>CustomizeParts::SetHUDColors (CC:2833): the installed / cart HUD's colour parts, else the defaults.</summary>
        void SetHudColors(SelectablePart sel)
        {
            var hudItem = ShopCart.SlotInCart(ShopRules.Slot.CustomHud);
            var installedHud = ShopCart.InstalledPart(ShopRules.Slot.CustomHud);
            uint[] colors = { CustomizeTables.DefaultFillColor, CustomizeTables.DefaultNeedleColor, CustomizeTables.DefaultLineColor };
            bool cartHud = hudItem != null && sel.Part == hudItem.Buy.Part;
            if (sel.Part == installedHud || cartHud)
            {
                for (int i = 0; i < 3; i++)
                {
                    int slot = ShopRules.Slot.HudBacking + i;
                    var item = ShopCart.SlotInCart(slot);
                    var colorPart = item != null && cartHud ? item.Buy.Part : ShopCart.InstalledPart(slot);
                    if (colorPart != null) { var (r, g, b) = colorPart.Rgb; colors[i] = FEngIf.Argb(r, g, b); }
                }
            }
            ApplyHudColors(View, colors[0], colors[1], colors[2]);
        }

        internal static void ApplyHudColors(FEView v, uint fill, uint needle, uint line)
        {
            FEngIf.SetColor(v, 0x5d19f25, fill);
            FEngIf.SetColor(v, 0xc0721eb9, line); FEngIf.SetColor(v, 0xc62ad685, line); FEngIf.SetColor(v, 0xb8f1f802, line);
            FEngIf.SetColor(v, 0xd312f0cb, needle); FEngIf.SetColor(v, 0x8fe2a217, needle);
        }

        protected override void OnBack()
        {
            if (_isVinyl) ShopCart.ClearTempColoredPart();
            base.OnBack();
        }
    }

    /// <summary>GLOBAL\HUDS_Custom_NN.bin: the custom HUD texture packs the HUD screens stream in (CustomizeParts::LoadHudTextures).</summary>
    public static class HudTextures
    {
        static readonly HashSet<int> _loaded = new HashSet<int>();

        public static void Ensure(int index)
        {
            if (index < 0 || index > 10 || !_loaded.Add(index)) return;
            try
            {
                var path = System.IO.Path.Combine(GameData.Root, "GLOBAL", $"HUDS_Custom_{index:00}.bin");
                if (!System.IO.File.Exists(path)) return;
                var d = GameFiles.ReadBundle(path);
                FrontEndData.Textures.Add(TexturePack.ReadAll(d, 0, d.Length));
            }
            catch (Exception e) { Debug.LogWarning("NFSMW custom HUD textures: " + e.Message); }
        }
    }

    /// <summary>
    /// CustomizeSpoiler (CarCustomize.cpp:2902-3045): the car's spoilers of one filter group (sport / tuner / sport carbon /
    /// tuner carbon, group number 0..3; group 4 is in every filter), L / R triggers cycle the filter (0x78008599 shows its
    /// name), the selection index is kept per filter, carbon spoilers use the carbon icon.
    /// </summary>
    public sealed class SpoilerScreen : CustomizationScreen
    {
        int _filter;
        readonly int[] _selectedIndex = { 1, 1, 1, 1 };
        static readonly uint[] FilterNames = { 0x1f0e2b2, 0x205b328, 0x9912746, 0xe7416fc };
        public override string Package => PackageFor("Spoilers");
        public SpoilerScreen() { Category = CC.Spoilers; }

        protected override void Setup()
        {
            TitleHash = 0x94e73021;
            var installed = ShopCart.ActivePart(ShopRules.Slot.Spoiler);
            if (installed != null && installed.Group != 4) _filter = installed.Group;
            BuildPartOptionListFromFilter(installed);
        }

        void BuildPartOptionListFromFilter(CarPartDb.Part installed)
        {
            Options.Clear();
            int current = 1;
            foreach (var part in ShopUi.PartList(ShopRules.Slot.Spoiler))
            {
                if (part.Part.Group != _filter && part.Part.Group != 4) continue;
                uint unlock = CustomizeTables.UnlockHash(Category, part.Level);
                uint icon = ShopCart.Backroom ? 0xc51a4f62u : 0xbb034ea6u;
                if (part.Part.Carbon) icon = ShopCart.Backroom ? 0x611d142au : 0x4d1c18bau;
                AddPartOption(part, icon, 0, 0, unlock, ShopCart.IsLocked(part));
                if (_selectedIndex[_filter] == 1 && installed != null && part.Part == installed) _selectedIndex[_filter] = current;
                current++;
            }
            Selected = Mathf.Clamp(_selectedIndex[_filter] - 1, 0, Mathf.Max(0, Options.Count - 1));
        }

        protected override void OnSelectionChanged()
        {
            _selectedIndex[_filter] = Selected + 1;
            base.OnSelectionChanged();
        }

        protected override void PartHeader()
        {
            FEngIf.SetLanguageHash(View, 0x78008599, FilterNames[_filter]);
            var sel = SelectedPart;
            if (sel == null) return;
            PreviewSelected(sel);
            FEngIf.Printf(View, 0x5e7b09c9, ShopUi.PartName(sel.Part));
        }

        /// <summary>ScrollFilters (CC:3027).</summary>
        protected override void OnTrigger(bool next)
        {
            int f = _filter + (next ? 1 : -1);
            if (f < 0) f = 3; else if (f > 3) f = 0;
            if (f == _filter) return;
            _filter = f;
            BuildPartOptionListFromFilter(null);
            Relayout();
            ScrollTime = -10f;
            RefreshHeader();
        }
    }

    /// <summary>
    /// CustomizeRims (CarCustomize.cpp:3293-3453): one brand's rims (BRAND_NAME of the category) at the current inner
    /// radius; L / R triggers cycle the size between the car type's WheelInnerRadiusMin..Max (0xE6782841 shows it as
    /// `nn"`), the name line drops the last 6 characters of the DB name (the size suffix).
    /// </summary>
    public sealed class RimsScreen : CustomizationScreen
    {
        int _radius = 15, _min = 15, _max = 15;
        public override string Package => PackageFor("Rims");
        public RimsScreen(int brandCategory) { Category = brandCategory; }

        protected override void Setup()
        {
            TitleHash = 0xe167f7c8;
            if (CarParts.Db.CarTypes.TryGetValue(CarParts.TypeName(CarName), out var ct) && ct.WheelInnerMax >= ct.WheelInnerMin && ct.WheelInnerMin > 0)
            { _min = ct.WheelInnerMin; _max = ct.WheelInnerMax; }
            _radius = _min;
            var installed = ShopCart.ActivePart(ShopRules.Slot.FrontWheel);
            if (installed != null && installed.InnerRadius >= _min && installed.InnerRadius <= _max) _radius = installed.InnerRadius;
            BuildRimsList(-1);
        }

        /// <summary>BuildRimsList (CC:3368): the brand's parts of the current radius, the installed one selected.</summary>
        void BuildRimsList(int selectedIndex)
        {
            Options.Clear();
            var installed = selectedIndex == -1 ? ShopCart.ActivePart(ShopRules.Slot.FrontWheel) : null;
            int installedIndex = 0, current = 1;
            foreach (var part in ShopUi.PartList(ShopRules.Slot.FrontWheel, CustomizeTables.RimBrandHash(Category)))
            {
                if (part.Part.InnerRadius != _radius) continue;
                uint unlock = CustomizeTables.UnlockHash(Category, part.Level);
                AddPartOption(part, 0x294d2a3, 0, 0, unlock, ShopCart.IsLocked(part));
                if (installed != null && part.Part == installed) installedIndex = current;
                current++;
            }
            if (selectedIndex == -1) selectedIndex = installed != null && installedIndex > 0 ? installedIndex : 1;
            Selected = Mathf.Clamp(selectedIndex - 1, 0, Mathf.Max(0, Options.Count - 1));
        }

        static string ShortName(CarPartDb.Part p)
        {
            string s = p?.Name ?? "";
            return s.Length > 6 ? s.Substring(0, s.Length - 6) : "";
        }

        protected override void PartHeader()
        {
            var sel = SelectedPart;
            if (sel == null) return;
            ShopUi.PreviewPart(FE, sel);
            FEngIf.Printf(View, 0xe6782841, FEngIf.D(_radius) + "\"");
            FEngIf.Printf(View, 0x5e7b09c9, ShortName(sel.Part));
        }

        /// <summary>ScrollRimSizes (CC:3329).</summary>
        protected override void OnTrigger(bool next)
        {
            int r = _radius + (next ? 1 : -1);
            if (r < _min) r = _max; else if (r > _max) r = _min;
            if (r == _radius) return;
            _radius = r;
            BuildRimsList(Selected + 1);
            Relayout();
            RefreshHeader();
        }
    }

    /// <summary>
    /// CustomizeDecals (CarCustomize.cpp:3989-4228): the decals of one location / door slot — a removal entry first (icon
    /// 0x697B4AD4, "Stock" 0x60A662F5), then the brand decals whose NAME is the brand (black) or brand + "_WHITE"; L / R
    /// triggers toggle black / white (0x889BACB6 ← 0x41F0A3A5 / 0x436A98E9), keeping the brand selected.
    /// </summary>
    public sealed class DecalsScreen : CustomizationScreen
    {
        /// <summary>CustomizeDecals::CurrentDecalLocation: the location picked on the decal location menu.</summary>
        public static int CurrentDecalLocation = CC.DecalWindshield;
        bool _isBlack = true;
        int _slot;
        public override string Package => ShopCart.Backroom && FrontEndData.Packages.ContainsKey("Decals_backroom") ? "Decals_backroom" : "Decals";
        public DecalsScreen(int category)
        {
            Category = category;
            if (CC.IsDecalLocation(category)) CurrentDecalLocation = category;
        }

        protected override void Setup()
        {
            _slot = CustomizeTables.DecalSlot(CurrentDecalLocation, Category);
            TitleHash = CustomizeTables.DecalTitle(Category);
            uint installedBrand = 0;
            var item = ShopCart.SlotInCart(_slot);
            var buying = item?.Buy.Part;
            if (buying != null) { _isBlack = buying.Brand == buying.U(CustomizeTables.NameAttr); installedBrand = buying.Brand; }
            else
            {
                var installed = ShopCart.InstalledPart(_slot);
                if (installed != null) { _isBlack = installed.Brand == installed.U(CustomizeTables.NameAttr); installedBrand = installed.Brand; }
            }
            BuildDecalList(installedBrand);
        }

        /// <summary>BuildDecalList (CC:4097).</summary>
        void BuildDecalList(uint selectedBrand)
        {
            ScrollTime = -10f;
            NeedsRefresh = true;
            Options.Clear();
            var removal = SelectablePart.Visual(_slot, null);
            AddPartOption(removal, 0x697b4ad4, 0x60a662f5, 0, 0, false, FEDb.L(0x60a662f5, "Stock"));
            int ent = ShopRules.SlotUnlockable(_slot);
            int level = ent == ShopRules.U_DecalLeftDoor ? 2 : ent == ShopRules.U_DecalLeftQp ? 3 : 1;
            int i = 2, index = 1;
            foreach (var part in ShopUi.PartList(_slot))
            {
                uint name = part.Part.Brand;
                if (!_isBlack) name = EaHash.Hash("_WHITE", name);
                if (name != part.Part.U(CustomizeTables.NameAttr)) continue;
                uint unlock = CustomizeTables.UnlockHash(Category, level);
                AddPartOption(part, 0x697b4ad4, name, 0, unlock, ShopCart.IsLocked(part), part.Part.Name);
                if (part.Part.Brand == selectedBrand) index = i;
                i++;
            }
            Selected = Mathf.Clamp(index - 1, 0, Mathf.Max(0, Options.Count - 1));
        }

        protected override void PartHeader()
        {
            var sel = SelectedPart;
            if (sel == null) return;
            FEngIf.Printf(View, 0x5e7b09c9, sel.Part != null ? sel.Part.Name : FEDb.L(0x60a662f5, "Stock"));
            FEngIf.SetLanguageHash(View, 0x889bacb6, _isBlack ? 0x41f0a3a5u : 0x436a98e9u);
            PreviewSelected(sel);
        }

        protected override void OnTrigger(bool next)
        {
            _isBlack = !_isBlack;
            BuildDecalList(SelectedPart?.Part?.Brand ?? 0);
            Relayout();
            RefreshHeader();
        }

        public override void Tick(MenuInput input)
        {
            if (input.Key2) return;   // __PAD_BUTTON4__ ignored (CC:4028)
            base.Tick(input);
        }
    }

    /// <summary>Kept for callers that opened the decal menu directly: the decal location sub menu (CustomizeSub, CC_DECAL_LOCATION).</summary>
    public sealed class DecalLocationScreen : FEScreen
    {
        public override string Package => "CustomizeGenericTop";
        public override void Open() => FE.Defer(() => FE.Replace(new VisualScreen(CC.DecalLocation)));
    }
}
