using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// CustomizeShoppingCart on ShoppingCart.fng / ShoppingCart_QR.fng / ShoppingCart_BACKROOM.fng (CarCustomize.cpp:302-1215;
    /// a UIWidgetMenu, iMaxWidgetsOnScreen 4 in the career / 6 outside, no wrap). Rows are FEShoppingCartItems over
    /// PART_NAME_n / PRICE_n / TRADE_IN_n / CHECK_ICON_n / OPTION_BACKING_n: the check icon 0x696AE039 (active) /
    /// 0xE719881C, the name "category: part" (DrawPartName), the trade-in and price (career front room only). The vinyl
    /// colours, HUD colours, left-door numbers and the right door's RNUMBER are not listed. Key 2 (PAD_BUTTON4) toggles the
    /// row (a number toggles all four), key 1 (PAD_BUTTON5) unchecks all; Accept: nothing active → empty the cart and
    /// leave (0xCF91AACD), affordable → confirm (0x71D9E710 / 0x8EBAA44B / 0x04810898) → Checkout, else 0x0A984A42; Back
    /// drops the unchecked items (0x5A928018). The header: 0x842B0E89 / 0xD57C95E1 "Add" / "Remove", the heat meter, the
    /// totals 0xD1497A06 / 0x34F7C0E8 / 0x18661565 / 0x8531E22E or the backroom marker rows (MARKER_GROUP_n …).
    /// </summary>
    public sealed class ShoppingCartScreen : WidgetMenuScreen
    {
        public override string Package => ShopCart.Backroom && FrontEndData.Packages.ContainsKey("ShoppingCart_BACKROOM") ? "ShoppingCart_BACKROOM"
            : !ShopCart.Career && FrontEndData.Packages.ContainsKey("ShoppingCart_QR") ? "ShoppingCart_QR" : "ShoppingCart";
        public override bool ShowCar => true;
        public override string Platform => ShopCart.Backroom ? "CUSTOMIZATION_SHOP_BACKROOM.BIN" : "CUSTOMIZATION_SHOP.BIN";
        protected override string TitleName => "PART_NAME_";
        protected override string DataName => "PRICE_";
        readonly CustomizeMeter _heat = new CustomizeMeter();

        /// <summary>FEShoppingCartItem (CC:302-786): a stat widget with the trade-in string and the check icon.</summary>
        sealed class CartRow : Widget
        {
            public CartItem Item;
            public FEView.Node TradeIn, Check;
            ShoppingCartScreen Cart => (ShoppingCartScreen)Menu;

            public override void Act(int dir) { }

            public override void Draw()
            {
                FEngIf.SetTextureHash(View, Check, Item.Active ? 0x696ae039u : 0xe719881cu);
                View.SetText(Title, PartName(Item));
                bool money = ShopCart.Career && !ShopCart.Backroom;
                View.SetText(TradeIn, Item.TradeIn != null && money ? FEngIf.D(Item.TradeInPrice) : "");
                View.SetText(Data, money ? FEngIf.D(Item.PartPrice) : "");
            }

            public override void Show(bool on)
            {
                foreach (var n in new[] { Title, Data, Backing, TradeIn, Check }) View.SetVisible(n, on);
            }

            /// <summary>FEShoppingCartItem::Position (CC:333).</summary>
            public override void Position(float y)
            {
                float top = y - Menu.RowH * 0.5f, left = Menu.TitleLeft;
                Menu.TopLeftAt(Check, left, top - 10f);
                Menu.TopLeftAt(Title, left + 30f, top);
                Menu.RightAt(TradeIn, Menu.DataX + 150f, top);
                Menu.RightAt(Data, Menu.DataX + 40f, top);
                if (Backing != null) Menu.TopLeftAt(Backing, left, top);
            }

            public override void SetFocus()
            {
                foreach (var n in new[] { Title, Data, TradeIn }) View.Play(n, FEView.Highlight);
                if (Backing != null) { View.SetVisible(Backing, true); View.Play(Backing, FEView.Highlight); }
            }

            public override void UnsetFocus()
            {
                uint s = Item.Active ? FEView.UnHighlight : FEView.Grey;
                foreach (var n in new[] { Title, Data, TradeIn }) View.Play(n, s);
                if (Backing != null) { View.SetVisible(Backing, false); View.Play(Backing, FEView.UnHighlight); }
            }

            /// <summary>SetCheckScripts / SetActiveScripts (CC:371-386).</summary>
            public void SetCheckScripts()
            {
                View.Play(Check, FrontEndData.Hash(Item.Active ? "CHECKED" : "UNCHECKED"));
                if (!Item.Active) View.Play(Check, FEView.Grey);
            }
        }

        static string Lang(uint h) => FEDb.L(h);

        /// <summary>FEShoppingCartItem::DrawPartName (CC:388): "category: name" with the slot's own form.</summary>
        static string PartName(CartItem item)
        {
            var buy = item.Buy;
            if (buy.IsPerf)
            {
                string cat = Lang(CustomizeTables.PerfPkgCatHash(buy.PhysType));
                if (buy.Level == ShopRules.JunkmanLevel) return $"{cat}: {Lang(0xedd14807)}";
                int level = buy.Level - (CarParts.NumPackages(ShopCart.Car?.Car, buy.PhysType) - 6);
                return $"{cat}: {Lang(CustomizeTables.PerfPkgLevelHash(level))}";
            }
            var part = buy.Part;
            string c = Lang(CustomizeTables.CarPartCatHash(buy.Slot));
            switch (buy.Slot)
            {
                case ShopRules.Slot.PaintRim:
                    return $"{c}: {Lang(0xb3100a3e)} {Lang(part?.U(CustomizeTables.SpeechColourAttr) ?? 0)}";
                case ShopRules.Slot.BasePaint:
                {
                    uint paintType = part?.Brand switch { 0x2daab07 => 0xb6763cdeu, 0x3797533 => 0xb715070au, 0xda27 => 0xb3100a3eu, _ => 0x452b5481u };
                    return $"{c}: {Lang(paintType)} {Lang(part?.U(CustomizeTables.SpeechColourAttr) ?? 0)}";
                }
                case ShopRules.Slot.RightDoorLNumber:
                {
                    var l = ShopCart.SlotInCart(ShopRules.Slot.RightDoorLNumber)?.Buy.Part;
                    var r = ShopCart.SlotInCart(ShopRules.Slot.RightDoorRNumber)?.Buy.Part;
                    return l != null && r != null ? $"{c}: {l.Name}{r.Name}" : $"{c}: {Lang(0xbe434a38)}";
                }
                case ShopRules.Slot.FrontWheel:
                    if (part != null && part != ShopCart.StockPart(ShopRules.Slot.FrontWheel))
                    {
                        string n = part.Name ?? "";
                        if (n.Length > 6) n = n.Substring(0, n.Length - 6);
                        return $"{c}: {n} {part.InnerRadius}\"";
                    }
                    return $"{c}: {Lang(0x60a662f5)}";
                case ShopRules.Slot.Spoiler: case ShopRules.Slot.Roof: case ShopRules.Slot.Hood:
                    if (part != null && part.Has(CarPartDb.Attr.CARBONFIBRE))
                        return part.Carbon ? $"{c}: {Lang(0x5415b874)} {Lang(part.U(CarPartDb.Attr.LANGUAGEHASH))}" : $"{c}: {Lang(part.U(CarPartDb.Attr.LANGUAGEHASH))}";
                    break;
                case ShopRules.Slot.VinylLayer:
                    if (part == null) return $"{c}: {Lang(0x60a662f5)}";
                    break;
            }
            if (ShopRules.SlotUnlockable(buy.Slot) is ShopRules.U_DecalWindshield or ShopRules.U_DecalLeftDoor or ShopRules.U_DecalLeftQp)
            {
                if (part == null) return $"{Lang(0x955980bc)}: {c} - {Lang(0x7177dc17)}";
                uint slotHash = 0;
                if (buy.Slot >= ShopRules.Slot.DecalLeftDoor && buy.Slot <= ShopRules.Slot.DecalLeftDoor + 5) slotHash = 0x7d212cfa + (uint)(buy.Slot - ShopRules.Slot.DecalLeftDoor);
                else if (buy.Slot >= ShopRules.Slot.DecalRightDoor && buy.Slot <= ShopRules.Slot.DecalRightDoor + 5) slotHash = 0x7d212cfa + (uint)(buy.Slot - ShopRules.Slot.DecalRightDoor);
                return slotHash != 0 ? $"{Lang(0x955980bc)}: {c} {Lang(slotHash)} {part.Name}" : $"{Lang(0x955980bc)}: {c} {part.Name}";
            }
            if (part == null) return $"{c}: {Lang(0x60a662f5)}";
            return part.Has(CarPartDb.Attr.LANGUAGEHASH) ? $"{c}: {Lang(part.U(CarPartDb.Attr.LANGUAGEHASH))}" : $"{c}: {part.Name}";
        }

        /// <summary>Rows the cart lists (CC:1188-1191): no vinyl / HUD colours, no LEFT_DOOR numbers, no RIGHT_DOOR RNUMBER.</summary>
        static bool Listed(CartItem i) => i.Buy.IsPerf || !(ShopRules.FreeSlot(i.Buy.Slot) || i.Buy.Slot == ShopRules.Slot.LeftNumber
                                                            || i.Buy.Slot == ShopRules.Slot.RightNumber || i.Buy.Slot == ShopRules.Slot.RightDoorRNumber);

        public override void Open()
        {
            MaxWidgetsOnScreen = ShopCart.Career ? 4 : 6;
            ScrollWrapped = false;
            InitWidgetMenu();
            _heat.Init(View, 1f, 5f, ShopCart.ActualHeat, ShopCart.CartHeat);
            foreach (var item in ShopCart.Items.Where(Listed)) AddItem(item);
            if (ShopCart.Backroom) SetMarkerImages();
            SetInitialOption(0);
            foreach (var w in Options.OfType<CartRow>()) w.SetCheckScripts();
            RefreshHeader();
            ShopUi.ShowPreview(FE);
        }

        /// <summary>CustomizeShoppingCart::AddItem (CC:1077).</summary>
        void AddItem(CartItem item)
        {
            var row = new CartRow { Item = item, Check = CurrentFEObject("CHECK_ICON_"), TradeIn = CurrentFEObject("TRADE_IN_") };
            AddWidget(row);
        }

        CartRow CurrentRow => Current as CartRow;

        static bool IsNumberDecal(int slot) => slot == ShopRules.Slot.RightDoorLNumber || slot == ShopRules.Slot.RightDoorRNumber || slot == ShopRules.Slot.LeftNumber || slot == ShopRules.Slot.RightNumber;

        /// <summary>ToggleChecked (CC:886): a number row toggles all four number items.</summary>
        void ToggleChecked()
        {
            var row = CurrentRow;
            if (row == null) return;
            row.Item.Active = !row.Item.Active;
            if (IsNumberDecal(row.Item.Buy.Slot))
            {
                foreach (var i in ShopCart.Items) if (IsNumberDecal(i.Buy.Slot)) i.Active = !i.Active;
                row.Item.Active = !row.Item.Active;
            }
            row.SetCheckScripts();
            row.Draw();
            ShopCart.ResetPreview();
            ShopUi.ShowPreview(FE);
        }

        /// <summary>CanCheckout (CC:905).</summary>
        static bool CanCheckout() => !ShopCart.Career || ShopCart.Backroom || ShopCart.Total <= (GameSession.Profile?.Cash ?? 0);

        void SetMarkerData(int num, int markers)
        {
            FEngIf.SetScript(View, FrontEndData.Hash("MARKER_GROUP_" + num), markers == 0 ? FEView.Grey : FrontEndData.Hash("NORMAL"));
            FEngIf.Printf(View, FrontEndData.Hash("MARKER_NUM_" + num), FEngIf.D(markers));
            FEngIf.Printf(View, FrontEndData.Hash("MARKER_BLOOM_" + num), FEngIf.D(markers));
        }

        static int Spending(int slot) { var i = ShopCart.SlotInCart(slot); return i != null && i.Active ? 1 : 0; }

        /// <summary>SetMarkerAmounts (CC:932): markers left per type after the active cart items.</summary>
        void SetMarkerAmounts()
        {
            if (ShopCart.InPerformance)
            {
                int[] types = { ShopRules.Brakes, ShopRules.Engine, ShopRules.Nos, ShopRules.Induction, ShopRules.Chassis, ShopRules.Tires, ShopRules.Transmission };
                for (int i = 0; i < types.Length; i++)
                {
                    var item = ShopCart.PerfTypeInCart(types[i]);
                    int num = ShopMarkers.Count(ShopRules.PerfMarker(types[i]));
                    if (item != null && item.Active) num--;
                    SetMarkerData(i + 1, num);
                }
            }
            else if (ShopCart.InParts)
            {
                int[] slots = { ShopRules.Slot.Body, ShopRules.Slot.Hood, ShopRules.Slot.Spoiler, ShopRules.Slot.FrontWheel, ShopRules.Slot.Roof };
                for (int i = 0; i < slots.Length; i++)
                {
                    var item = ShopCart.SlotInCart(slots[i]);
                    int num = ShopMarkers.Count(ShopRules.SlotMarker(slots[i]));
                    if (item != null && item.Active) num--;
                    SetMarkerData(i + 1, num);
                }
                FEngIf.SetVisible(View, 0x47df0e22, false);
                FEngIf.SetVisible(View, 0x47df0e23, false);
            }
            else
            {
                var item = ShopCart.SlotInCart(ShopRules.Slot.VinylLayer);
                int num = ShopMarkers.Count(ShopRules.M_Vinyl);
                if (item != null && item.Active) num--;
                SetMarkerData(1, num);
                num = 0;
                foreach (int s in new[] { 83, 91, 99, 100, 101, 102, 103, 104, 107, 108, 109, 110, 111, 112, 115, 123 }) num += Spending(s);
                SetMarkerData(2, ShopMarkers.Count(ShopRules.M_Decal) - num);
                item = ShopCart.SlotInCart(ShopRules.Slot.BasePaint);
                num = ShopMarkers.Count(ShopRules.M_Paint);
                if (item != null && item.Active) num--;
                SetMarkerData(3, num);
                item = ShopCart.SlotInCart(ShopRules.Slot.CustomHud);
                num = ShopMarkers.Count(ShopRules.M_CustomHud);
                if (item != null && item.Active) num--;
                SetMarkerData(4, num);
                FEngIf.SetVisible(View, 0x47df0e21, false);
                FEngIf.SetVisible(View, 0x47df0e22, false);
                FEngIf.SetVisible(View, 0x47df0e23, false);
            }
        }

        /// <summary>SetMarkerImages (CC:1149): the marker icons of the room's categories.</summary>
        void SetMarkerImages()
        {
            uint[] tex = ShopCart.InPerformance ? new uint[] { 0x4887f351, 0x4f424e0f, 0x6fea04c8, 0x8e284227, 0x190eb6, 0x7373f1ef, 0xd142d3e3 }
                : ShopCart.InParts ? new uint[] { 0xaf393dba, 0xf375276e, 0xc51a4f62, 0xc19491cc, 0x25a4375e }
                : new uint[] { 0xd35f04c0, 0xa9135927, 0xdb89e17, 0x8ba602fc };
            for (int i = 0; i < tex.Length; i++) FEngIf.SetTextureHash(View, 0xeb957471u + (uint)i, tex[i]);
            if (!ShopCart.InPerformance) { FEngIf.SetVisible(View, 0x47df0e22, false); FEngIf.SetVisible(View, 0x47df0e23, false); }
            if (!ShopCart.InPerformance && !ShopCart.InParts) FEngIf.SetVisible(View, 0x47df0e21, false);
        }

        /// <summary>CustomizeShoppingCart::RefreshHeader (CC:1017).</summary>
        void RefreshHeader()
        {
            var row = CurrentRow;
            if (row != null)
            {
                FEngIf.SetVisible(View, 0x842b0e89, true);
                FEngIf.SetLanguageHash(View, 0xd57c95e1, row.Item.Active ? 0x5dabcbc0u : 0x28feaddu);
            }
            else FEngIf.SetVisible(View, 0x842b0e89, false);
            _heat.SetCurrent(ShopCart.ActualHeat);
            _heat.SetPreview(ShopCart.CartHeat);
            _heat.Draw();
            if (ShopCart.Career)
            {
                if (ShopCart.Backroom)
                {
                    SetMarkerAmounts();
                    int markers;
                    if (ShopCart.InParts)
                    {
                        FEngIf.SetLanguageHash(View, 0x8cdcb8ed, 0xa03a752f); FEngIf.SetLanguageHash(View, 0xd3d3b1f4, 0x4ac68298);
                        markers = ShopMarkers.Count(ShopRules.M_Body) + ShopMarkers.Count(ShopRules.M_Hood) + ShopMarkers.Count(ShopRules.M_Spoiler) + ShopMarkers.Count(ShopRules.M_Rims) + ShopMarkers.Count(ShopRules.M_RoofScoop);
                    }
                    else if (ShopCart.InPerformance)
                    {
                        FEngIf.SetLanguageHash(View, 0x8cdcb8ed, 0x358db897); FEngIf.SetLanguageHash(View, 0xd3d3b1f4, 0x68342700);
                        markers = ShopMarkers.Count(ShopRules.M_Brakes) + ShopMarkers.Count(ShopRules.M_Engine) + ShopMarkers.Count(ShopRules.M_Nos) + ShopMarkers.Count(ShopRules.M_Induction)
                            + ShopMarkers.Count(ShopRules.M_Chassis) + ShopMarkers.Count(ShopRules.M_Tires) + ShopMarkers.Count(ShopRules.M_Transmission);
                    }
                    else
                    {
                        FEngIf.SetLanguageHash(View, 0x8cdcb8ed, 0x93296e59); FEngIf.SetLanguageHash(View, 0xd3d3b1f4, 0x78f1c602);
                        markers = ShopMarkers.Count(ShopRules.M_Vinyl) + ShopMarkers.Count(ShopRules.M_Decal) + ShopMarkers.Count(ShopRules.M_Paint) + ShopMarkers.Count(ShopRules.M_CustomHud);
                    }
                    FEngIf.Printf(View, 0xd1497a06, FEngIf.D(ShopCart.PartsTotal));
                    FEngIf.Printf(View, 0x18661565, FEngIf.D(markers - ShopCart.PartsTotal));
                }
                else
                {
                    FEngIf.Printf(View, 0xd1497a06, FEngIf.D(ShopCart.PartsTotal));
                    FEngIf.Printf(View, 0x34f7c0e8, FEngIf.D(ShopCart.TradeInTotal));
                    int total = ShopCart.Total;
                    FEngIf.Printf(View, 0x18661565, FEngIf.D(total));
                    FEngIf.Printf(View, 0x8531e22e, FEngIf.D((GameSession.Profile?.Cash ?? 0) - total));
                }
            }
            else FEngIf.SetVisible(View, 0x9ea22e0b, false);
        }

        /// <summary>__PAD_ACCEPT__ (CC:805).</summary>
        void Accept()
        {
            if (!ShopCart.HasActive)
            {
                ShopCart.Items.Clear();
                ShopCart.ResetPreview();
                Leave(true);   // 0xCF91AACD to the parent, pop
                return;
            }
            if (!CanCheckout()) { FEDialog.ShowOk(FE, FEDialog.Title.Alert, FEDb.L(0x0a984a42)); return; }
            uint blurb = ShopCart.Career ? (ShopCart.Backroom ? 0x04810898u : 0x8ebaa44bu) : 0x71d9e710u;
            FEDialog.ShowTwoButtons(FE, FEDialog.Title.Alert, 0x70E01038, 0x417B25E4, () =>
            {
                ShopCart.Checkout();   // dialog_message_yes
                GameFlow.RespawnPlayerCar();
                Leave(true);
            }, null, null, 1, FEDb.L(blurb));
        }

        /// <summary>QueuePackagePop with 0xCF91AACD (checkout / empty: the parents switch back to CustomizeMain) or 0x5A928018 (back).</summary>
        void Leave(bool checkedOut)
        {
            FE.Pop();
            if (checkedOut) ShopUi.ExitShoppingCart(FE);
            else ShopUi.ShowPreview(FE);
        }

        public override void Tick(MenuInput input)
        {
            if (input.Up) { Scroll(-1); RefreshHeader(); }
            if (input.Down) { Scroll(1); RefreshHeader(); }
            if (input.Key2 || input.Delete) { ToggleChecked(); RefreshHeader(); }   // __PAD_BUTTON4__ (key 2)
            if (input.Key1)   // __PAD_BUTTON5__: UncheckAllItems
            {
                foreach (var i in ShopCart.Items) i.Active = false;
                foreach (var w in Options.OfType<CartRow>()) { w.SetCheckScripts(); w.Draw(); }
                ShopCart.ResetPreview();
                ShopUi.ShowPreview(FE);
                RefreshHeader();
            }
            if (input.Accept) { Accept(); return; }
            if (input.Back)
            {
                ShopCart.DropInactive();   // ClearUncheckedItems (vinyl colours follow their vinyl)
                Leave(false);
            }
        }

        public override void Resume() => RefreshHeader();
    }
}
