using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Gameplay;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// CustomizePaint on Paint.fng (CarCustomize.cpp:3455-3987): an ArrayScroller (20 wide, 4 high) of COLOR_n swatches
    /// over the BASE_PAINT parts of one brand filter (gloss 0x02DAAB07 / metallic 0x03437A52 / pearl 0x03797533, filter
    /// text 0xB6763CDE / 0x452B5481 / 0xB715070A into 0x78008599), the PAINT_RIM parts (brand 0xDA27, "Powder Coat"
    /// 0xB3100A3E, arrows 0x2C3CC2D3 / 0x53639A10 / 0x2C526172 hidden) or a vinyl's remap colours (VINYL_COLOUR0_n, brand
    /// 0x03E871F1; the filter is the colour layer, "Color 1/3" 0x5198BA16…; a vinyl with one colour hides the arrows). The
    /// swatch gets the part's RED / GREEN / BLUE. Accept on paint / rim paint = AddToCart; on a vinyl colour the pick is
    /// kept per layer and the vinyl plus its colours go to the cart when any differs from the installed set, then back to
    /// the vinyl list. L / R triggers change the filter (TRIGGER 0x1A7240F3 shows the arrows).
    /// </summary>
    public sealed class PaintScreen : CustomizationScreen
    {
        const int NumSwatches = 40, VinylNumColors = 3;
        public override string Package => ShopCart.Backroom && FrontEndData.Packages.ContainsKey("Paint_backroom") ? "Paint_backroom" : "Paint";
        int _filter = -1, _numRemapColors;
        readonly int[] _selectedIndex = { -1, -1, -1 };
        readonly SelectablePart[] _vinylColors = new SelectablePart[VinylNumColors];
        ArrayScroller _paints;
        List<SelectablePart> _data = new List<SelectablePart>();

        public PaintScreen() : this(CC.Paint) { }
        public PaintScreen(int category) { Category = category; }
        /// <summary>Legacy: a paint screen for a slot (BASE_PAINT / PAINT_RIM).</summary>
        public static PaintScreen ForSlot(int slot) => new PaintScreen(slot == ShopRules.Slot.PaintRim ? CC.RimPaint : CC.Paint);

        bool IsVinyl => CC.IsVinylGroup(Category);
        int ColourSlot(int filter) => ShopRules.Slot.VinylColour0 + filter;

        /// <summary>CustomizePaint::Setup (CC:3696).</summary>
        protected override void Setup()
        {
            _paints = new ArrayScroller(View, 20, 4);
            for (int i = 1; i <= NumSwatches; i++) _paints.AddSlot(View.Find("COLOR_" + i));
            switch (Category)
            {
                case CC.Paint:
                    CareerScreenKit.Message(View, 0x1a7240f3);   // TRIGGER: the filter arrows
                    TitleHash = 0x55da70c;
                    BuildSwatchList(ShopRules.Slot.BasePaint);
                    break;
                case CC.RimPaint:
                    TitleHash = 0xe126ff53;
                    FEngIf.SetVisible(View, 0x2C3CC2D3, false);
                    FEngIf.SetVisible(View, 0x53639A10, false);
                    BuildSwatchList(ShopRules.Slot.PaintRim);
                    break;
                default:
                    TitleHash = 0xd8ee1a80;
                    SetupVinylColor();
                    break;
            }
        }

        /// <summary>SetupVinylColor (CC:3747): the temp vinyl's remap colour count; the installed colours as the layers' picks.</summary>
        void SetupVinylColor()
        {
            BuildSwatchList(ColourSlot(0));
            var temp = ShopCart.TempColoredPart;
            _numRemapColors = temp?.Part != null ? (int)temp.Part.U(CustomizeTables.VinylColoursAttr) : 0;
            if (_numRemapColors < 2) { FEngIf.SetVisible(View, 0x2c3cc2d3, false); FEngIf.SetVisible(View, 0x53639a10, false); }
            else CareerScreenKit.Message(View, 0x1a7240f3);
            for (int i = 0; i < VinylNumColors; i++)
            {
                var part = ShopCart.ActivePart(ColourSlot(i));
                _vinylColors[i] = part != null ? SelectablePart.Visual(ColourSlot(i), part) : null;
            }
        }

        /// <summary>CalcBrandHash (CC:3797).</summary>
        uint CalcBrandHash(CarPartDb.Part installed)
        {
            switch (Category)
            {
                case CC.Paint:
                    return _filter switch { 0 => 0x02daab07u, 1 => 0x03437a52u, 2 => 0x03797533u, _ => installed?.Brand ?? 0 };
                case CC.RimPaint: return 0xda27;
                default: return 0x3e871f1;
            }
        }

        /// <summary>BuildSwatchList (CC:3829): the slot's parts of the filter brand into the swatches, the installed one selected.</summary>
        void BuildSwatchList(int slot)
        {
            _paints.ClearData();
            _data.Clear();
            Options.Clear();
            var installed = ShopCart.ActivePart(slot);
            uint brand = CalcBrandHash(installed);
            if (_filter == -1)
            {
                _filter = brand switch { 0x02daab07 => 0, 0xda27 => 0, 0x03437a52 => 1, 0x03797533 => 2, _ => 0 };
                brand = CalcBrandHash(installed);
            }
            int index = 0;
            foreach (var part in ShopUi.PartList(slot))
            {
                if (part.Part.Brand != brand) continue;
                if (_selectedIndex[_filter] == -1 && installed == part.Part) _selectedIndex[_filter] = index;
                _data.Add(part);
                _paints.AddDatum(new ArrayScroller.Datum { Tag = part });
                var swatch = _paints.SlotAt(index);
                if (swatch != null) { var (r, g, b) = part.Part.Rgb; FEngIf.SetColor(swatch, FEngIf.Argb(r, g, b)); }
                // the icon scroller is not used on this screen: the options list only carries the parts for the base class
                Options.Add(new Option { Tag = new PartOption { Part = part, UnlockBlurb = CustomizeTables.UnlockHash(Category, part.Level), Locked = ShopCart.IsLocked(part) }, Run = Accept });
                index++;
            }
            if (_selectedIndex[_filter] == -1) _selectedIndex[_filter] = 0;
            _paints.SetInitialPosition(_selectedIndex[_filter]);
            Selected = Mathf.Clamp(_paints.CurrentIndex, 0, Mathf.Max(0, Options.Count - 1));
            _paints.RefreshHeader();
            // the swatches past the data are hidden; colours of reused slots are set again by the next build
            for (int i = _data.Count; i < NumSwatches; i++) { var s = _paints.SlotAt(i); if (s != null) View.SetVisible(s, false); }
        }

        public override void Open()
        {
            base.Open();
            // the option_N icon scroller is not part of Paint.fng: hide what the base assigned (none exist) and refresh
            RefreshHeader();
        }

        SelectablePart Current => _paints?.Current?.Tag as SelectablePart;

        /// <summary>CustomizePaint::RefreshHeader (CC:3924).</summary>
        protected override void RefreshHeader()
        {
            if (View == null || _paints == null) return;
            DisplayHelper.DrawTitle();
            _paints.RefreshHeader();
            uint filterHash = 0;
            switch (_filter)
            {
                case 0: filterHash = Category == CC.Paint ? 0xb6763cde : _numRemapColors == 2 ? 0x5198ba16 : _numRemapColors == 3 ? 0x5198ba17 : 0xd8ee1a80; break;
                case 1: filterHash = Category == CC.Paint ? 0x452b5481 : _numRemapColors == 2 ? 0x5198be57 : _numRemapColors == 3 ? 0x5198be58u : 0u; break;
                case 2: filterHash = Category == CC.Paint ? 0xb715070a : _numRemapColors == 3 ? 0x5198c299u : 0u; break;
            }
            FEngIf.SetLanguageHash(View, 0x78008599, filterHash);
            var sel = Current;
            switch (Category)
            {
                case CC.Paint: ShopUi.PreviewPart(FE, sel); break;
                case CC.RimPaint:
                    ShopUi.PreviewPart(FE, sel);
                    FEngIf.SetVisible(View, 0x2c526172, false);
                    FEngIf.SetLanguageHash(View, 0x78008599, 0xb3100a3e);
                    break;
                default:
                {
                    if (_numRemapColors == 1) FEngIf.SetVisible(View, 0x2c526172, false);
                    var parts = new List<SelectablePart> { ShopCart.TempColoredPart };
                    for (int i = 0; i < VinylNumColors; i++) if (i < _numRemapColors && _vinylColors[i] != null) parts.Add(_vinylColors[i]);
                    parts.Add(sel);
                    ShopCart.PreviewItems(parts);
                    ShopUi.ShowPreview(FE);
                    break;
                }
            }
            DisplayHelper.SetCareerStuff(sel, Category, 0);
            var po = _paints.CurrentIndex >= 0 && _paints.CurrentIndex < Options.Count ? Options[_paints.CurrentIndex].Tag as PartOption : null;
            DisplayHelper.SetPartStatus(sel, ShopCart.StateOf(sel), po?.UnlockBlurb ?? 0, _paints.CurrentIndex + 1, _paints.NumDatum);
        }

        /// <summary>__PAD_ACCEPT__ (CC:3483): paint / rim paint → the base (AddToCart); vinyl colour → keep the layer's pick, cart when changed.</summary>
        protected override void Accept()
        {
            var sel = Current;
            if (sel == null) return;
            Selected = _paints.CurrentIndex;
            if (Category == CC.Paint || Category == CC.RimPaint) { base.Accept(); return; }
            _vinylColors[_filter] = sel;
            var installed = ShopCart.ActivePart(ShopRules.Slot.VinylLayer);
            var temp = ShopCart.TempColoredPart;
            bool add = installed != temp?.Part;
            if (!add)
                for (int i = 0; i < _numRemapColors; i++)
                {
                    var inst = ShopCart.ActivePart(ColourSlot(i));
                    if (_vinylColors[i] != null && inst != _vinylColors[i].Part) { add = true; break; }
                }
            if (add)
            {
                AddVinylAndColorsToCart();
                FE.Pop();   // QueuePackageSwitch(g_pCustomizePartsPkg …)
            }
        }

        /// <summary>AddVinylAndColorsToCart (CC:3631).</summary>
        void AddVinylAndColorsToCart()
        {
            var temp = ShopCart.TempColoredPart;
            if (temp != null) ShopCart.Add(temp);
            for (int i = 0; i < VinylNumColors; i++)
            {
                if (i < _numRemapColors && _vinylColors[i] != null) ShopCart.Add(_vinylColors[i]);
                _vinylColors[i] = null;
            }
        }

        /// <summary>ScrollFilters (CC:3646): paint has 3 filters, rim paint none, a vinyl its colour layers (keeping the current pick).</summary>
        protected override void OnTrigger(bool next)
        {
            int max;
            switch (Category)
            {
                case CC.Paint: max = 2; break;
                case CC.RimPaint: return;
                default:
                    max = _numRemapColors - 1;
                    if (max != 0 && Current != null && Current != _vinylColors[_filter]) _vinylColors[_filter] = Current;
                    break;
            }
            int f = _filter + (next ? 1 : -1);
            if (f < 0) f = max; else if (f > max) f = 0;
            if (f == _filter) return;
            _selectedIndex[_filter] = _paints.CurrentIndex;
            _filter = f;
            if (Category == CC.Paint || Category == CC.RimPaint) BuildSwatchList(Current?.Slot ?? ShopRules.Slot.BasePaint);
            else BuildSwatchList(ColourSlot(f));
            RefreshHeader();
        }

        public override void Tick(MenuInput input)
        {
            if (_paints == null) return;
            if (input.Left) { _paints.ScrollHor(false); RefreshHeader(); }
            if (input.Right) { _paints.ScrollHor(true); RefreshHeader(); }
            if (input.Up) { _paints.ScrollVer(false); RefreshHeader(); }
            if (input.Down) { _paints.ScrollVer(true); RefreshHeader(); }
            if (input.Key4) { FE.Push(new ShoppingCartScreen()); return; }
            if (input.Key3) { ShopUi.Showcase(FE); return; }
            if (input.LTrigger) { OnTrigger(false); return; }
            if (input.RTrigger) { OnTrigger(true); return; }
            if (input.Accept) { Accept(); return; }
            if (input.Back) OnBack();
        }

        /// <summary>__PAD_BACK__ (CC:3545): vinyl colours drop their picks and reset the preview.</summary>
        protected override void OnBack()
        {
            if (IsVinyl)
            {
                for (int i = 0; i < VinylNumColors; i++) _vinylColors[i] = null;
                ShopCart.ResetPreview();
            }
            base.OnBack();
        }
    }

    /// <summary>
    /// CustomizePerformance on CustomizePerformance.fng (CarCustomize.cpp:4598-5044): one type's packages as icons
    /// (0xB8C8C0D4) — the car's N levels at tiers M − N + i, the Junkman package after them when the career was completed
    /// (or the BK reward outside the career) and CanInstallJunkman, only Junkman in the career backroom. The highlighted
    /// package is previewed (PreviewPerfPkg) and the ACCELERATION / HANDLING / TOPSPEED two-stage sliders show the preview
    /// (fill) over the installed rating (handle); DETAIL_TEXT_LINE1..3 / PERFORMANCE_DETAILS_ICON1..3 list
    /// PD_&lt;type&gt;_&lt;tier&gt;_&lt;line&gt; (Castrol / rotary variants) with BRAND_ICON_n from the car's frontend p_&lt;type&gt;_&lt;tier&gt;;
    /// 0x5E7B09C9 gets PN_LEVEL_(6 − N + level) or "Junkman" 0xEDD14807. Outside the career the package gets QUICKRACE 0xDE511657.
    /// </summary>
    public sealed class PerformanceScreen : CustomizationScreen
    {
        int _type = ShopRules.Engine;
        readonly TwoStageSlider _accel = new TwoStageSlider(), _handling = new TwoStageSlider(), _topSpeed = new TwoStageSlider();
        public override string Package => PackageFor("CustomizePerformance");

        public PerformanceScreen() : this(CC.Engine) { }
        public PerformanceScreen(int category) { Category = CC.IsPerf(category) ? category : CC.Engine; }

        static float Rating(OwnedCar car, int which)
        {
            if (car == null || !PerformanceRatings.Estimate(car, out var p)) return 0f;
            return which == 0 ? p.Acceleration : which == 1 ? p.Handling : p.TopSpeed;
        }

        /// <summary>CustomizePerformance::Setup (CC:4935).</summary>
        protected override void Setup()
        {
            if (!ShopCart.Career) CareerScreenKit.Message(View, 0xde511657);   // QUICKRACE
            var preview = ShopCart.Preview ?? ShopCart.Car;
            _accel.Init(View, "ACCELERATION", 0f, 1f, 0f, Rating(preview, 0), Rating(ShopCart.Car, 0), 160f);
            _handling.Init(View, "HANDLING", 0f, 1f, 0f, Rating(preview, 1), Rating(ShopCart.Car, 1), 160f);
            _topSpeed.Init(View, "TOPSPEED", 0f, 1f, 0f, Rating(preview, 2), Rating(ShopCart.Car, 2), 160f);
            _type = CustomizeTables.PerfType(Category);
            TitleHash = Category == CC.ForcedInduction ? (CustomizeTables.IsTurbo() ? 0x5b1255cu : 0xbb6812bbu) : CustomizeTables.PerfPkgCatHash(_type);
            const uint icon = 0xb8c8c0d4;
            var car = ShopCart.Car;
            bool hero = CarParts.IsHeroCar(car?.Car);
            bool junkOk = car != null && CarParts.CanInstallJunkman(car, _type);
            if (ShopCart.Backroom && ShopCart.Career && !hero)
            {
                var junk = SelectablePart.Perf(_type, ShopRules.JunkmanLevel);
                AddPartOption(junk, icon, 0, 0, 0, false, junk.Name);
            }
            else
            {
                int n = CarParts.NumPackages(car?.Car, _type), m = ShopRules.MaxPackages(_type);
                for (int i = 1; i <= n; i++)
                {
                    var pkg = SelectablePart.Perf(_type, i);
                    uint unlock = CustomizeTables.UnlockHash(Category, m - n + i);
                    AddPartOption(pkg, icon, 0, 0, unlock, ShopCart.IsLocked(pkg), pkg.Name);
                }
            }
            var prof = GameSession.Profile;
            bool bk = prof != null && prof.BonusCars.Count > 0 && !ShopCart.Career;   // HasBeenAwardedBKReward && !career [?: the port keeps no BK flag; a bonus car stands in]
            if ((bk || (ShopUnlocks.CareerBeaten && !hero)) && junkOk && !(ShopCart.Backroom && ShopCart.Career))
            {
                var junk = SelectablePart.Perf(_type, ShopRules.JunkmanLevel);
                AddPartOption(junk, icon, 0, 0, 0, false, junk.Name);
            }
            if (ShopCart.Backroom && ShopCart.Career) Selected = 0;
            else
            {
                int installed = ShopCart.InstalledPerfPkg(_type);
                var item = ShopCart.PerfTypeInCart(_type);
                if (item != null) installed = item.Buy.Level;
                Selected = Mathf.Clamp(installed - 1, 0, Mathf.Max(0, Options.Count - 1));
            }
        }

        /// <summary>CustomizePerformance::RefreshHeader (CC:4867).</summary>
        protected override void RefreshHeader()
        {
            if (View == null) return;
            var sel = SelectedPart;
            if (sel != null) ShopUi.PreviewPart(FE, sel);
            var preview = ShopCart.Preview ?? ShopCart.Car;
            _accel.SetValue(Rating(preview, 0)); _handling.SetValue(Rating(preview, 1)); _topSpeed.SetValue(Rating(preview, 2));
            _accel.Draw(); _handling.Draw(); _topSpeed.Draw();
            int level = sel?.Level ?? 0, loop = 3;
            int n = CarParts.NumPackages(CarName, _type);
            int descLevel = ShopRules.MaxPackages(_type) - n + level;
            if (ShopCart.Backroom || level == ShopRules.JunkmanLevel) { level = 0; descLevel = 0; loop = 1; }
            bool turbo = CustomizeTables.IsTurbo();
            int i = 0;
            for (; i < loop; i++)
            {
                uint desc = CustomizeTables.PerfPkgDesc(_type, descLevel, i + 1, turbo);
                bool has = desc != 0 && FrontEndData.Strings != null && FrontEndData.Strings.TryGet(desc, out _);
                var line = View.Find("DETAIL_TEXT_LINE" + (i + 1));
                var bullet = View.Find("PERFORMANCE_DETAILS_ICON" + (i + 1));
                View.SetVisible(line, has); View.SetVisible(bullet, has);
                if (has && line != null) View.SetText(line, FEDb.L(desc));
                uint tex = CustomizeTables.PerfPkgBrand(_type, descLevel, i);
                var brand = View.Find("BRAND_ICON_" + (i + 1));
                var t = tex != 0 ? FrontEndData.Texture(tex) : null;
                if (brand != null) { if (t) View.SetTexture(brand, t); View.SetVisible(brand, t != null); }
            }
            for (; i < 3; i++)
            {
                View.SetVisible(View.Find("DETAIL_TEXT_LINE" + (i + 1)), false);
                View.SetVisible(View.Find("PERFORMANCE_DETAILS_ICON" + (i + 1)), false);
                View.SetVisible(View.Find("BRAND_ICON_" + (i + 1)), false);
            }
            base.RefreshHeader();
            uint pkgHash = sel != null && sel.Level == ShopRules.JunkmanLevel ? 0xedd14807 : FrontEndData.Hash($"PN_LEVEL_{6 - n + level}");
            FEngIf.SetLanguageHash(View, 0x5e7b09c9, pkgHash);
            FEngIf.SetLanguageHash(View, 0x0dfb7a2e, pkgHash);
        }
    }

    /// <summary>
    /// CustomizeHUDColor on CustomHUDColor.fng (CarCustomize.cpp:3047-3291): three layer options (needle 0x70F56628,
    /// lettering 0xBF6682C9, gauge face 0xCC9E1CE4) over the HUD colour parts of HUD_NEEDLE / CHARACTER / BACKING_COLOUR;
    /// the layer's colours fill COLOR_1..n (RED / GREEN / BLUE) with the cursor 0xB893252A at (+69, +56) of the selected one;
    /// up / down scroll the colours, left / right the layer; the gauge preview shows the TempColoredPart HUD's textures tinted
    /// by the picks. Accept (0x91DFDF84): a cart HUD other than this one loses its colours, the HUD and the picked colours
    /// go to the cart, back to the HUD list; Back drops the temp part.
    /// </summary>
    public sealed class HudColorScreen : CustomizationScreen
    {
        public override string Package => PackageFor("CustomHUDColor");
        sealed class Layer { public int Slot; public List<SelectablePart> Colors; public SelectablePart SelectedPart; }
        readonly List<Layer> _layers = new List<Layer>();
        List<SelectablePart> _colorOptions = new List<SelectablePart>();
        int _selectedColor = -1;
        FEView.Node _cursor;

        public HudColorScreen(int category) { Category = category; }

        Layer CurrentLayer => Selected >= 0 && Selected < _layers.Count ? _layers[Selected] : null;

        protected override void Setup()
        {
            TitleHash = 0xb1b0e8af;
            _cursor = View.Find(0xB893252A);
            foreach (var (slot, tex, name) in CustomizeTables.HudLayers)
            {
                var layer = new Layer { Slot = slot, Colors = ShopUi.PartList(slot) };
                _layers.Add(layer);
                Options.Add(new Option { IconHash = tex, NameHash = name, Tag = layer, Run = Accept });
            }
            Selected = 0;
        }

        public override void Open()
        {
            base.Open();
            BuildColorOptions();
            SetHudTextures();
            SetInitialColors();
            RefreshHeader();
        }

        /// <summary>SetInitialColors (CC:3160): the cart's or the installed HUD's colours, else the defaults.</summary>
        void SetInitialColors()
        {
            var cartItem = ShopCart.SlotInCart(ShopRules.Slot.CustomHud);
            var installedHud = ShopCart.InstalledPart(ShopRules.Slot.CustomHud);
            var temp = ShopCart.TempColoredPart;
            uint[] colors = { CustomizeTables.DefaultFillColor, CustomizeTables.DefaultNeedleColor, CustomizeTables.DefaultLineColor };
            if (cartItem != null && temp?.Part == cartItem.Buy.Part)
            {
                for (int i = 0; i < 3; i++) { var item = ShopCart.SlotInCart(ShopRules.Slot.HudBacking + i); if (item?.Buy.Part != null) { var (r, g, b) = item.Buy.Part.Rgb; colors[i] = FEngIf.Argb(r, g, b); } }
            }
            else if (temp?.Part == installedHud)
            {
                for (int i = 0; i < 3; i++) { var p = ShopCart.InstalledPart(ShopRules.Slot.HudBacking + i); if (p != null) { var (r, g, b) = p.Rgb; colors[i] = FEngIf.Argb(r, g, b); } }
            }
            PartScreen.ApplyHudColors(View, colors[0], colors[1], colors[2]);
        }

        void SetHudTextures() => PartScreen.SetHudTextures(View, ShopCart.TempColoredPart?.Part, CarName);

        /// <summary>BuildColorOptions (CC:3243).</summary>
        void BuildColorOptions()
        {
            if (_selectedColor >= 0 && _selectedColor < _colorOptions.Count) { var old = View.Find("COLOR_" + (_selectedColor + 1)); if (old != null) View.Play(old, FEView.UnHighlight); }
            _selectedColor = -1;
            var layer = CurrentLayer;
            if (layer == null || layer.Colors.Count == 0) return;
            _colorOptions = layer.Colors;
            var cartItem = ShopCart.SlotInCart(ShopRules.Slot.CustomHud);
            var installedHud = ShopCart.InstalledPart(ShopRules.Slot.CustomHud);
            var temp = ShopCart.TempColoredPart;
            for (int i = 0; i < _colorOptions.Count; i++)
            {
                var part = _colorOptions[i];
                var obj = View.Find("COLOR_" + (i + 1));
                if (obj != null) { var (r, g, b) = part.Part.Rgb; FEngIf.SetColor(obj, FEngIf.Argb(r, g, b)); View.SetVisible(obj, true); }
                if (layer.SelectedPart == null)
                {
                    if (cartItem != null && temp?.Part == cartItem.Buy.Part) { if (ShopCart.InCart(part) != null) { _selectedColor = i; layer.SelectedPart = part; } }
                    else if (temp?.Part == installedHud && ShopCart.IsInstalled(part)) { _selectedColor = i; layer.SelectedPart = part; }
                }
                else if (layer.SelectedPart.Part == part.Part) _selectedColor = i;
            }
            for (int i = _colorOptions.Count; i < 40; i++) { var obj = View.Find("COLOR_" + (i + 1)); if (obj == null) break; View.SetVisible(obj, false); }
            if (_selectedColor < 0) _selectedColor = 0;
            PlaceCursor();
        }

        void PlaceCursor()
        {
            var obj = View.Find("COLOR_" + (_selectedColor + 1));
            if (obj != null) View.Play(obj, FEView.Highlight);
            if (_cursor != null && obj != null)
            {
                var tl = FEngIf.TopLeft(obj);
                // the cursor and the swatches share the package root [?: the exe adds 69 / 56 to the swatch's top-left]
                _cursor.Data.X = tl.x + 69f + (obj.Parent != null ? obj.Parent.Data.X : 0);
                _cursor.Data.Y = tl.y + 56f + (obj.Parent != null ? obj.Parent.Data.Y : 0);
            }
        }

        SelectablePart SelectedColor => _selectedColor >= 0 && _selectedColor < _colorOptions.Count ? _colorOptions[_selectedColor] : null;

        /// <summary>ScrollColors (CC:3120): circular over the layer's colours.</summary>
        void ScrollColors(bool next)
        {
            if (_colorOptions.Count == 0) return;
            int prev = _selectedColor;
            _selectedColor = ((_selectedColor + (next ? 1 : -1)) % _colorOptions.Count + _colorOptions.Count) % _colorOptions.Count;
            if (_selectedColor == prev) return;
            var layer = CurrentLayer;
            if (layer != null) layer.SelectedPart = SelectedColor;
            var old = View.Find("COLOR_" + (prev + 1));
            if (old != null) View.Play(old, FEView.UnHighlight);
            PlaceCursor();
            RefreshHeader();
        }

        protected override void OnSelectionChanged()
        {
            var layer = CurrentLayer;
            if (layer != null && SelectedColor != null) layer.SelectedPart = SelectedColor;
            BuildColorOptions();
            base.OnSelectionChanged();
        }

        /// <summary>CustomizeHUDColor::RefreshHeader (CC:3223): the picked colour on the layer's gauge objects.</summary>
        protected override void RefreshHeader()
        {
            base.RefreshHeader();
            var c = SelectedColor;
            if (c?.Part == null) return;
            var (r, g, b) = c.Part.Rgb;
            uint argb = FEngIf.Argb(r, g, b);
            switch (c.Slot)
            {
                case ShopRules.Slot.HudBacking: FEngIf.SetColor(View, 0x5d19f25, argb); break;
                case ShopRules.Slot.HudCharacter: FEngIf.SetColor(View, 0xc0721eb9, argb); FEngIf.SetColor(View, 0xc62ad685, argb); FEngIf.SetColor(View, 0xb8f1f802, argb); break;
                case ShopRules.Slot.HudNeedle: FEngIf.SetColor(View, 0xd312f0cb, argb); FEngIf.SetColor(View, 0x8fe2a217, argb); break;
            }
        }

        /// <summary>0x91DFDF84 (CC:3094): the HUD and every layer's pick into the cart, then back to the HUD list.</summary>
        protected override void Accept()
        {
            var temp = ShopCart.TempColoredPart;
            if (temp == null) { FE.Pop(); return; }
            var cartItem = ShopCart.SlotInCart(ShopRules.Slot.CustomHud);
            if (cartItem != null && temp.Part != cartItem.Buy.Part)
                for (int i = ShopRules.Slot.HudBacking; i <= ShopRules.Slot.HudCharacter; i++) ShopCart.Remove(ShopCart.SlotInCart(i));
            ShopCart.Add(temp);
            ShopCart.ClearTempColoredPart();
            foreach (var layer in _layers) if (layer.SelectedPart != null) ShopCart.Add(layer.SelectedPart);
            ShopUi.ShowPreview(FE);
            FE.Pop();
        }

        public override void Tick(MenuInput input)
        {
            if (input.Up) { ScrollColors(false); return; }
            if (input.Down) { ScrollColors(true); return; }
            base.Tick(input);
        }

        protected override void OnBack()
        {
            ShopCart.ClearTempColoredPart();
            base.OnBack();
        }
    }

    /// <summary>
    /// CustomizeNumbers on Numbers.fng (CarCustomize.cpp:4230-4596): the two door digits — LeftNumberList (slot 113,
    /// brand NUMBER_LEFT) and RightNumberList (114, NUMBER_RIGHT) — shown in 0x2A08BA92 / 0x1A88DC05 ("-" until a digit is
    /// picked); left / right switch the digit, up / down count it; the preview wears both digits on both doors (slots 113,
    /// 114, 105, 106). Accept (0x91DFDF84): both locked / in cart / installed flash the icon, else the right-door digits go
    /// to the cart and free copies for the left door (slots 105 / 106, price 0). Key 2 (PAD_BUTTON4) removes: installed
    /// numbers → four removal items, cart numbers → dropped; the display resets. The "remove numbers" prompt 0x7A8355D9
    /// shows while digits are set.
    /// </summary>
    public sealed class NumbersScreen : FEScreen
    {
        public override string Package => "Numbers";
        public override bool ShowCar => true;
        public override string Platform => ShopCart.Backroom ? "CUSTOMIZATION_SHOP_BACKROOM.BIN" : "CUSTOMIZATION_SHOP.BIN";
        CustomizeHelper _helper;
        List<SelectablePart> _left = new List<SelectablePart>(), _right = new List<SelectablePart>();
        SelectablePart _theLeft, _theRight;
        int _leftValue = -1, _rightValue = -1;
        bool _isLeft = true;
        const int Category = CC.Numbers;

        public override void Open()
        {
            if (ShopCart.Car == null) ShopCart.Begin(GameSession.ActiveCar);
            _helper = new CustomizeHelper(View) { TitleHash = 0x6857e5ac, InitComplete = true };
            Setup();
        }

        /// <summary>CustomizeNumbers::Setup (CC:4515).</summary>
        void Setup()
        {
            uint nl = EaHash.Hash("NUMBER_LEFT"), nr = EaHash.Hash("NUMBER_RIGHT");
            _left = ShopUi.PartList(ShopRules.Slot.RightDoorLNumber).Where(p => p.Part.Brand == nl).ToList();
            _right = ShopUi.PartList(ShopRules.Slot.RightDoorRNumber).Where(p => p.Part.Brand == nr).ToList();
            var installed = ShopCart.ActivePart(ShopRules.Slot.RightDoorLNumber);
            for (int i = 0; i < _left.Count; i++) if (installed != null && _left[i].Part == installed) { _theLeft = _left[i]; _leftValue = i; }
            if (_theLeft == null) { _leftValue = -1; _theLeft = _left.FirstOrDefault(); }
            installed = ShopCart.ActivePart(ShopRules.Slot.RightDoorRNumber);
            for (int i = 0; i < _right.Count; i++) if (installed != null && _right[i].Part == installed) { _theRight = _right[i]; _rightValue = i; }
            if (_theRight == null) { _rightValue = -1; _theRight = _right.FirstOrDefault(); }
            RefreshHeader();
        }

        static bool Installed(SelectablePart p) => p != null && ShopCart.IsInstalled(p);
        static bool InCart(SelectablePart p) => p != null && ShopCart.InCart(p) != null;
        static bool Locked(SelectablePart p) => p != null && ShopCart.IsLocked(p);

        /// <summary>CustomizeNumbers::RefreshHeader (CC:4473).</summary>
        void RefreshHeader()
        {
            _helper.DrawTitle();
            _helper.SetCareerStuff(_theLeft, Category, 0);
            if (_leftValue != -1 && _rightValue != -1 && _theLeft != null && _theRight != null)
            {
                FEngIf.SetVisible(View, 0x7a8355d9, true);
                int status = CPS.Available;
                if (Locked(_theLeft) && Locked(_theRight)) status = CPS.Locked;
                else if (ShopCart.IsNew(_theLeft) && ShopCart.IsNew(_theRight)) status = CPS.New;
                if (Installed(_theLeft) && Installed(_theRight)) status |= CPS.Installed;
                else if (InCart(_theLeft) && InCart(_theRight)) status |= CPS.InCart;
                _helper.SetPartStatus(_theLeft, status, CustomizeTables.UnlockHash(Category, 1), 0, 0);
                FEngIf.Printf(View, 0x2a08ba92, _leftValue.ToString());
                FEngIf.Printf(View, 0x1a88dc05, _rightValue.ToString());
                var l105 = SelectablePart.Visual(ShopRules.Slot.LeftNumber, _theLeft.Part);
                var r106 = SelectablePart.Visual(ShopRules.Slot.RightNumber, _theRight.Part);
                ShopCart.PreviewItems(new[] { _theLeft, _theRight, l105, r106 });
                ShopUi.ShowPreview(FE);
            }
            else
            {
                FEngIf.SetVisible(View, 0x7a8355d9, false);
                var inCart = ShopCart.SlotInCart(ShopRules.Slot.RightDoorLNumber);
                var part = ShopCart.InstalledPart(ShopRules.Slot.RightDoorLNumber);
                if (part == null) _helper.SetPlayerCarStatusIcon(CPS.Installed);
                else if (inCart != null && inCart.Buy.Part == null) _helper.SetPlayerCarStatusIcon(CPS.InCart);
                else _helper.SetPlayerCarStatusIcon(CPS.Available);
                FEngIf.Printf(View, 0x2a08ba92, "-");
                FEngIf.Printf(View, 0x1a88dc05, "-");
                ShopCart.ResetPreview();
                ShopUi.ShowPreview(FE);
            }
        }

        /// <summary>ScrollNumbers (CC:4421): the first press sets both digits to 0, then the active digit counts 0..9 circularly.</summary>
        void ScrollNumbers(bool next)
        {
            if (_leftValue == -1 || _rightValue == -1)
            {
                _leftValue = 0; _rightValue = 0;
                _theLeft = _left.FirstOrDefault(); _theRight = _right.FirstOrDefault();
                RefreshHeader();
                return;
            }
            var list = _isLeft ? _left : _right;
            if (list.Count == 0) return;
            var cur = _isLeft ? _theLeft : _theRight;
            int i = list.IndexOf(cur);
            i = ((i + (next ? 1 : -1)) % list.Count + list.Count) % list.Count;
            if (_isLeft) { _leftValue = (_leftValue + (next ? 1 : 9)) % 10; _theLeft = list[i]; }
            else { _rightValue = (_rightValue + (next ? 1 : 9)) % 10; _theRight = list[i]; }
            RefreshHeader();
        }

        /// <summary>0x91DFDF84 (CC:4323): both digits into the cart, free left-door copies.</summary>
        void AddToCart()
        {
            ShopCart.Add(_theLeft);
            ShopCart.Add(_theRight);
            var l = SelectablePart.Visual(ShopRules.Slot.LeftNumber, _theLeft.Part); l.Price = 0;
            var r = SelectablePart.Visual(ShopRules.Slot.RightNumber, _theRight.Part); r.Price = 0;
            ShopCart.Add(l);
            ShopCart.Add(r);
            RefreshHeader();
        }

        /// <summary>__PAD_BUTTON4__ (CC:4278): remove the numbers.</summary>
        void Remove()
        {
            if (ShopCart.InstalledPart(ShopRules.Slot.RightDoorLNumber) != null)
            {
                foreach (int slot in new[] { ShopRules.Slot.RightDoorLNumber, ShopRules.Slot.RightDoorRNumber, ShopRules.Slot.LeftNumber, ShopRules.Slot.RightNumber })
                    ShopCart.Add(SelectablePart.Visual(slot, null));
            }
            else if (InCart(_theLeft) || InCart(_theRight))
            {
                foreach (int slot in new[] { ShopRules.Slot.RightDoorLNumber, ShopRules.Slot.RightDoorRNumber, ShopRules.Slot.LeftNumber, ShopRules.Slot.RightNumber })
                    ShopCart.Remove(ShopCart.SlotInCart(slot));
            }
            _leftValue = -1; _rightValue = -1;
            _theLeft = _left.FirstOrDefault(); _theRight = _right.FirstOrDefault();
            RefreshHeader();
        }

        public override void Tick(MenuInput input)
        {
            if (input.Key4) { FE.Push(new ShoppingCartScreen()); return; }
            if (input.Key3) { ShopUi.Showcase(FE); return; }
            if (input.Left || input.Right) { _isLeft = !_isLeft; FEngIf.SetScript(View, _isLeft ? 0x1a88dc05u : 0x2a08ba92u, FEView.UnHighlight); FEngIf.SetScript(View, _isLeft ? 0x2a08ba92u : 0x1a88dc05u, FEView.Highlight); }
            if (input.Up) ScrollNumbers(true);
            if (input.Down) ScrollNumbers(false);
            if (input.Key2) { Remove(); return; }
            if (input.Accept)
            {
                if (_leftValue == -1 || _rightValue == -1 || _theLeft == null || _theRight == null) return;
                if (Locked(_theLeft) && Locked(_theRight)) _helper.PlayLocked();
                else if (InCart(_theLeft) && InCart(_theRight)) _helper.PlayInCart();
                else if (Installed(_theLeft) && Installed(_theRight)) _helper.PlayInstalled();
                else AddToCart();
                return;
            }
            if (input.Back) { ShopCart.ResetPreview(); ShopUi.ShowPreview(FE); FE.Pop(); }
        }

        public override void Resume() => RefreshHeader();
    }
}
