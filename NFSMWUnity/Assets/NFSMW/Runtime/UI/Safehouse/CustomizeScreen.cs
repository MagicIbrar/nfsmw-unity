using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// CustomizeMain on CustomizeMain.fng (customize/CarCustomize.cpp:1881-2082; PC 0x7A2B80-0x7C3A45): Parts,
    /// Performance, Visual — the hero car gets Visual only — with the backroom icons (0x73272ED2 / 0xC61C8D3A / 0xE69D4F7C)
    /// in the backroom. The constructor takes control of the car (TakeControl: installed record → preview, empty cart) and
    /// marks every unlockable seen (MarkUnlockableThingSeen), then BackupCarStable in the career. Key 1 (PAD_BUTTON5,
    /// :1964) switches rooms in the career (not the hero car, with customize markers; a warning 0x003B3E83 first when
    /// INDUCTION / NOS markers are unusable, the switch only if usable ones remain); SwitchRooms posts BACKROOM 0xA1CAFF8D /
    /// FRONTROOM 0x005C01C5 to the package and rebuilds the list. Key 4 (PAD_START) opens the cart. Back (:1937): career
    /// front room → QUIT_SCREEN 0x6D5D86A1, RelinquishControl, free roam; career backroom → front room; otherwise the caller.
    /// The title is "customize" (0x1F242E03) / "backroom" (0x92FCDBF0) lower-cased (SetTitle :2038); RefreshHeader
    /// (:2019) shows the Backroom prompt 0xDC6EE739 only in the career front room with customize markers.
    /// </summary>
    public sealed class CustomizeScreen : CustomizeCategoryScreen
    {
        public override string Package => "CustomizeMain";
        readonly bool _fresh;
        int _invalidMarkers;

        /// <summary>Entering the shop takes control of the car (TakeControl): empty cart, front room; a room switch keeps it.</summary>
        public CustomizeScreen() : this(true) { }
        public CustomizeScreen(bool fresh) { _fresh = fresh; Category = 0; }

        /// <summary>CustomizeMain::Setup + BuildOptionsList (:2055-2082).</summary>
        protected override void Build()
        {
            var car = GameSession.ActiveCar;
            ShopCart.Begin(car, _fresh);   // TakeControl + MarkUnlockableThingSeen (ShopUnlocks.MarkSeen) + BackupCarStable [ours: the profile is the stable]
            ShopCart.InPerformance = false;
            ShopCart.InParts = false;
            Options.Clear();
            bool hero = CarParts.IsHeroCar(car?.Car);
            foreach (var o in CustomizeTables.Main(ShopCart.Backroom, hero))
            {
                int cat = o.Cat;
                AddCustomOption(o.Tex, o.Name, cat, () => { FEDb.SetLastButton("CustomizeMain", Selected + 1); FE.Push(new VisualScreen(cat)); });
            }
            Selected = Mathf.Clamp(FEDb.LastButton("CustomizeMain") - 1, 0, Mathf.Max(0, Options.Count - 1));
        }

        public override void Open()
        {
            base.Open();
            SetTitle(ShopCart.Backroom);
            CareerScreenKit.Message(View, ShopCart.Backroom ? 0xA1CAFF8Du : 0x005C01C5u);   // BACKROOM / FRONTROOM (also on 0x1265ECE9, the car viewer's camera message)
        }

        /// <summary>SetTitle (:2038): lower-cased (not in German / Finnish) into TITLE_GROUP.</summary>
        void SetTitle(bool backroom) => FEngIf.Printf(View, 0xB71B576D, FEDb.L(backroom ? 0x92fcdbf0u : 0x1f242e03u).ToLowerInvariant());

        /// <summary>CustomizeMain::RefreshHeader (:2019): the Backroom prompt only in the career front room with markers.</summary>
        protected override void RefreshCategoryHeader()
        {
            base.RefreshCategoryHeader();
            if (View == null) return;
            bool show = ShopCart.Career && !CarParts.IsHeroCar(ShopCart.Car?.Car) && !ShopCart.Backroom && ShopMarkers.CustomizeCount > 0;
            FEngIf.SetVisible(View, 0xDC6EE739, show);
        }

        /// <summary>SwitchRooms (:1897): toggles the room, retitles, posts BACKROOM / FRONTROOM, rebuilds the list at the same index.</summary>
        void SwitchRooms()
        {
            bool backroom = !ShopCart.Backroom;
            ShopCart.Items.Clear();   // the cart is per room [ours: the exe's EmptyCart happens on the cart screens; a room switch with items is not reachable there]
            ShopCart.Backroom = backroom;
            ShopCart.ResetPreview();
            int index = Selected;
            FE.Replace(new CustomizeScreen(false) { Selected = index });
        }

        /// <summary>__PAD_BUTTON5__ (:1964-1983).</summary>
        void ToggleBackroom()
        {
            if (!ShopCart.Career || ShopMarkers.CustomizeCount == 0) return;
            if (ShopCart.Backroom) { SwitchRooms(); return; }
            if (CarParts.IsHeroCar(ShopCart.Car?.Car)) return;
            var car = ShopCart.Car;
            _invalidMarkers = 0;
            if (ShopMarkers.Has(ShopRules.M_Induction) && !CarParts.CanInstallJunkman(car, ShopRules.Induction)) _invalidMarkers++;
            if (ShopMarkers.Has(ShopRules.M_Nos) && !CarParts.CanInstallJunkman(car, ShopRules.Nos)) _invalidMarkers++;
            if (_invalidMarkers > 0)
            {
                // 0x34DC1BEC (OK): switch only when more customize markers than unusable ones exist
                FEDialog.ShowOneButton(FE, FEDialog.Title.Info, 0x417b2601, FEDb.L(0x003b3e83), () => { if (ShopMarkers.CustomizeCount > _invalidMarkers) SwitchRooms(); _invalidMarkers = 0; });
                return;
            }
            SwitchRooms();
        }

        /// <summary>MaxOutPerformance (CM:425-470): the highest unlocked package of every type into the cart (quick race only).</summary>
        public static void MaxOutPerformance()
        {
            var car = ShopCart.Car;
            if (car == null) return;
            for (int t = 0; t < ShopRules.PerfCount; t++)
            {
                int best = 0;
                for (int l = 1; l <= CarParts.NumPackages(car.Car, t); l++)
                    if (!ShopCart.IsLocked(SelectablePart.Perf(t, l))) best = l;
                if (best > 0 && CarParts.PerfLevel(car, t) != best) ShopCart.Add(SelectablePart.Perf(t, best));
            }
        }

        public override void Tick(MenuInput input)
        {
            if (input.Key1) { ToggleBackroom(); return; }
            base.Tick(input);
        }

        /// <summary>__PAD_BACK__ (:1937): backroom → front room; career → free roam (RelinquishControl); else back to the caller.</summary>
        protected override void OnBack()
        {
            if (ShopCart.Career && ShopCart.Backroom) { SwitchRooms(); return; }
            if (ShopCart.HasActive) { FE.Push(new ShoppingCartScreen()); return; }   // [ours] the sub menus normally force the cart first
            CareerScreenKit.Message(View, 0x6d5d86a1);   // QUIT_SCREEN
            ShopCart.End();   // RelinquishControl
            GameSession.Profile?.Save();
            GameFlow.RespawnPlayerCar();
            FE.Pop();
        }

        /// <summary>Hides the package parts the exe never shows on PC [ours: the console help-bar strings].</summary>
        public static void Tidy(FEScreen s)
        {
            s.View.SetVisible(s.View.Find("HANDLING"), false);
        }
    }
}
