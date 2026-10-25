using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// UIQRCarSelect on Car_Select.fng (quickrace/uiQRCarSelect.cpp; PC 0x7A3400-0x7C32E0), one screen in every mode:
    /// the career car lot (and the D-Day starter lot), the safehouse "Car Select" of owned cars, quick race (lists Stock /
    /// Career / My Cars / Bonus on up / down), the My Cars "add new car" pick, plus the port's free-roam / LAN pick.
    /// Lists (RefreshCarList :1355): the FE car records matching the filter, locked by UnlockSystem::IsCarUnlocked, the
    /// stock / career / bonus / preset lists sorted by frontend UnlockedAt (SortCarsByUnlock). Left / right scroll the
    /// list circularly; the car loads 0.5 s after the last move. The car name strings are hidden (CARNAME 0xD6D32016):
    /// the manufacturer logo (0x3E01AD1D) and model badge (0xB05DD708) name the car.
    /// Car lot Enter: not enough cash 0x40FA955D, more than 9 bought 0x41030A1B, D-Day yes/no 0x74317CBC, else Drive Now /
    /// Deliver / Cancel (0x5B9D89D0 / 0x889D822E / 0x1A294DAD, blurb 0x8C451EBA). Career select: key 1 sells for cost / 2
    /// (0xB4A40135; not the last car 0x9A772BD6), Enter keeps the car. Quick race Enter: the transmission question
    /// (0x6F5401D1, Manual / Auto) unless drag or the prompt is off, then the race.
    /// </summary>
    public class CarSelectScreen : FEScreen
    {
        public enum Mode { CarLot, CarLotDDay, Career, QuickRace, Customize, FreePick }

        /// <summary>FE_CAR_FILTER_LIST_* (VehicleDB.hpp): 1 stock, 2 career, 4 quick race (My Cars), 8 bonus, 16 preset.</summary>
        protected const int ListStock = 1, ListCareer = 2, ListQuickRace = 4, ListBonus = 8;

        protected sealed class SelectableCar
        {
            public string Car;            // pvehicle key
            public OwnedCar Owned;        // career / My Cars record, null for stock / bonus
            public CarLotEntry Lot;       // frontend record (cost, UnlockedAt) when known
            public string Preset;         // bonus preset name
            public bool Locked;
            public int UnlockBin = -1;    // the Blacklist member that unlocks a bonus car
        }

        public override string Package => "Car_Select";
        public override bool ShowCar => true;
        public override string Platform => _mode == Mode.CarLot || _mode == Mode.CarLotDDay ? "CAR_LOT.BIN" : _mode == Mode.Career ? "CAREER_SAFEHOUSE.BIN" : base.Platform;

        protected readonly Mode _mode;
        protected readonly int _player;
        protected List<SelectableCar> Cars = new List<SelectableCar>();
        protected int Index;
        int _list = ListStock;
        string _originalCar;
        int _originalCareer;
        float _lastMove = -1;
        static readonly Dictionary<int, string> _listHandles = new Dictionary<int, string>();
        // InitStatsSliders (:852): the heat meter and the three two-stage stat sliders (ACCELERATION / TOPSPEED / HANDLING, range 160)
        readonly CustomizeMeter _heat = new CustomizeMeter();
        readonly TwoStageSlider _accel = new TwoStageSlider(), _topSpeed = new TwoStageSlider(), _handling = new TwoStageSlider();
        /// <summary>QRCarSelectBustedManager::IsImpoundInfoVisible: career, not the car lot.</summary>
        bool ImpoundInfoVisible => _mode == Mode.Career;

        public CarSelectScreen(Mode mode, int player = 0) { _mode = mode; _player = player; }

        protected bool CarLot => _mode == Mode.CarLot || _mode == Mode.CarLotDDay;
        protected Profile P => GameSession.Profile;

        public override void Open()
        {
            FEDb.Load();
            if (!CarLot)
            {
                foreach (uint h in new uint[] { 0x19398802, 0x1930B057, 0x20D113DC, 0x20C83C31 }) SafehouseUi.Visible(View, h, false);
            }
            if (FEDb.Is(FEGameMode.Online) || FEDb.Is(FEGameMode.Lan) || _mode == Mode.FreePick)
            {
                SafehouseUi.Visible(View, 0x0E9ED0A2, false);
                SafehouseUi.Visible(View, 0x18A4384F, false);
            }
            if (FEDb.IsSplitScreen)
                SafehouseUi.SetLang(View, 0xE3FE27FE, FEDb.L(_player == 0 ? 0x7B070984u : 0x7B070985u));
            else View.SetVisible(View.Find(0xE3FE27FE), false);
            // the port's leftovers of the package: impound panels, online icons, load LEDs, debug name
            Hide(0xFBEAE195, 0x07F3EEB0, 0xD4AA6721, 0x64F3A49C, 0x7C430E80, 0x6A0831F6);
            foreach (var n in View.Nodes) if (n.Label != null && n.Obj.Text == "Debug:") View.SetVisible(n, false);
            _heat.Init(View, 1f, 5f, 1f, 1f);
            _accel.Init(View, "ACCELERATION", 0f, 1f, 0f, 0f, 0f, 160f);
            _topSpeed.Init(View, "TOPSPEED", 0f, 1f, 0f, 0f, 0f, 160f);
            _handling.Init(View, "HANDLING", 0f, 1f, 0f, 0f, 0f, 160f);
            Setup();
            // 0xE0B38195 (the package's INIT in the career select) -> CalcGameOver -> GameOver.fng dialog 0x164BED94 -> the career manager
            if (ImpoundInfoVisible && FEImpound.CalcGameOver())
                FEDialog.ShowOneButton(FE, FEDialog.Title.Alert, 0x417b2601, FEDb.L(0x164bed94), () => { FEDb.ClearGameMode(FEGameMode.Career); FEDb.SetGameMode(FEGameMode.CareerManager); FE.Replace(new CareerManagerScreen()); });
        }

        internal static float Rating(OwnedCar car, int which)
        {
            if (car == null || !Game.Gameplay.PerformanceRatings.Estimate(car, out var p)) return 0f;
            return which == 0 ? p.Acceleration : which == 1 ? p.TopSpeed : p.Handling;
        }

        /// <summary>UpdateSliders (:859): the selected car's ratings (fill) over the original car's (handle); D-Day: the same car.</summary>
        void UpdateSliders()
        {
            var sel = Cars.Count > 0 && Index < Cars.Count ? Cars[Index] : null;
            var car = sel == null ? null : sel.Owned ?? new OwnedCar { Car = sel.Car };
            OwnedCar cur = car;
            if (_mode == Mode.Career && P != null && _originalCareer >= 0 && _originalCareer < P.Cars.Count) cur = P.Cars[_originalCareer];
            if (_mode == Mode.CarLotDDay) cur = car;
            _accel.SetValue(Rating(car, 0)); _accel.SetPreview(Rating(cur, 0)); _accel.Draw();
            _topSpeed.SetValue(Rating(car, 1)); _topSpeed.SetPreview(Rating(cur, 1)); _topSpeed.Draw();
            _handling.SetValue(Rating(car, 2)); _handling.SetPreview(Rating(cur, 2)); _handling.Draw();
        }

        /// <summary>GetBonusUnlockText (:961): the bonus cars' lock blurbs (0x4EF2A115 = the rival text).</summary>
        static uint BonusUnlockText(string preset) => preset == null ? 0 : preset.StartsWith("BL") ? 0x4ef2a115u : preset switch
        {
            "BONUS_C6R" => 0xbd8bac94u, "E3_DEMO_BMW" => 0xbd8bac93u, "BONUS_SL65" => 0xbd8bac91u, "BONUS_GT2" => 0xbd8bac92u, _ => 0u,
        };

        /// <summary>QRCarSelectBustedManager::RefreshHeader (:192): the impound stamp, the accept text and the strike row.</summary>
        void RefreshBusted(SelectableCar car)
        {
            if (!ImpoundInfoVisible || car?.Owned == null) return;
            var rec = FEImpound.Record(car.Owned, false);
            uint stamp = FrontEndData.Hash("IMPOUNDED_" + (FrontEndData.LanguageName.Length >= 3 ? FrontEndData.LanguageName.Substring(0, 3) : "ENG"));   // STRINGHASH_IMPOUNDED_<LANG>
            if (FEImpound.IsImpounded(car.Owned))
            {
                if (FrontEndData.Texture(stamp) != null)
                {
                    FEngIf.SetScript(View, 0xbc7b91f, FrontEndData.Hash("NORMAL"));
                    FEngIf.SetScript(View, 0x64f3a49c, FEView.Appear);
                    FEngIf.SetVisible(View, 0x64f3a49c, true);
                    FEngIf.SetTextureHash(View, 0xce18427d, stamp);
                    FEngIf.SetTextureHash(View, 0x5b8f2a45, stamp);
                }
                int cost = FEImpound.ReleaseCost(car.Owned);
                bool hasCash = (P?.Cash ?? 0) >= cost, hasMarker = ShopMarkers.Has(ShopRules.M_ImpoundRelease);
                if (FEImpound.IsReleasable(car.Owned) && hasCash) SafehouseUi.SetLang(View, 0xb94139f4, FEDb.L(0x281dee8a));
                else if (hasMarker) SafehouseUi.SetLang(View, 0xb94139f4, FEDb.L(0xf9c73cc2));
                else SafehouseUi.SetLang(View, 0xb94139f4, FEDb.L(0x2b65a216));
            }
            else
            {
                SafehouseUi.SetLang(View, 0xb94139f4, FEDb.L(0x2b65a216));
                FEngIf.SetScript(View, 0x64f3a49c, FEView.Hide);
                FEngIf.SetVisible(View, 0x64f3a49c, false);
            }
            int times = rec?.TimesBusted ?? 0, max = car.Owned.MaxBusted;
            SafehouseUi.Visible(View, 0x75721326, true);
            int pos = max == 4 ? 2 : max == 5 ? 3 : 1;
            FEngIf.SetScript(View, 0x5bc78037, max >= 4 ? FEView.Show : FEView.Hide);
            FEngIf.SetScript(View, 0x48095518, max >= 5 ? FEView.Show : FEView.Hide);
            FEngIf.SetVisible(View, 0x5bc78037, max >= 4); FEngIf.SetVisible(View, 0x48095518, max >= 5);
            FEngIf.SetScript(View, 0xf9a5ce86, FrontEndData.Hash("POS" + pos));
            FEngIf.SetScript(View, 0xebf0016e, FrontEndData.Hash("POS" + pos));
            for (int i = 1; i <= 5; i++)
            {
                var n = View.Find("IMPOUND_STATE_" + i);
                if (n == null) continue;
                bool on = i <= max && i <= times;
                View.Play(n, on ? FEView.Show : FEView.Hide);
                View.SetVisible(n, on);
            }
        }

        /// <summary>MaybeReleaseCar (:290): the release dialogs by cash / marker.</summary>
        void MaybeReleaseCar(SelectableCar car)
        {
            int cost = FEImpound.ReleaseCost(car.Owned);
            bool hasCash = cost <= (P?.Cash ?? 0), hasMarker = ShopMarkers.Has(ShopRules.M_ImpoundRelease);
            void PayRelease() { FEImpound.Release(car.Owned); if (P != null) P.Cash = Mathf.Max(0, P.Cash - cost); P?.Save(); Refresh(); }   // 0xA0FC39F9
            void MarkerRelease() { FEImpound.Release(car.Owned); ShopMarkers.Use(ShopRules.M_ImpoundRelease); P?.Save(); Refresh(); }   // 0xE845BC1C
            string Fmt(uint h) => FEDb.L(h).Replace("%$d", cost.Grouped());
            if (FEImpound.IsReleasable(car.Owned) && hasCash)
            {
                if (hasMarker) FEDialog.ShowThreeButtons(FE, FEDialog.Title.Confirmation, 0xf9c73cc2, 0x4eb9591f, 0x1a294dad, MarkerRelease, PayRelease, null, null, 1, Fmt(0xb715ae8f));
                else FEDialog.ShowTwoButtons(FE, FEDialog.Title.Confirmation, 0x417b2601, 0x1a294dad, PayRelease, null, null, 0, Fmt(0xcad5722e));
            }
            else if (hasMarker) FEDialog.ShowTwoButtons(FE, FEDialog.Title.Confirmation, 0x417b2601, 0x1a294dad, MarkerRelease, null, null, 0, Fmt(0xed4dd591));
            else FEDialog.ShowOneButton(FE, FEDialog.Title.Alert, 0x417b2601, FEDb.L(0xe96fa0c5));
        }

        /// <summary>MaybeAddImpoundBox (:318): 0x0CEBDA20 confirm (0x8DEFA48B: the marker is used, MaxBusted + 1); at the maximum 0xBCAE8539.</summary>
        void MaybeAddImpoundBox(SelectableCar car)
        {
            if (car?.Owned == null) return;
            if (FEImpound.CanAddMaxBusted(car.Owned) && ShopMarkers.Has(ShopRules.M_ImpoundBox))
                FEDialog.ShowTwoButtons(FE, FEDialog.Title.Confirmation, 0x417b2601, 0x1a294dad, () => { ShopMarkers.Use(ShopRules.M_ImpoundBox); FEImpound.AddMaxBusted(car.Owned); P?.Save(); Refresh(); }, null, null, 1, FEDb.L(0x0cebda20));
            else if (car.Owned.MaxBusted >= ShopMarkers.MaximumTimesBusted)
                FEDialog.ShowOneButton(FE, FEDialog.Title.Info, 0x417b2601, FEDb.L(0xbcae8539));
        }

        /// <summary>Setup (:784): the mode message, the filter, the original car, the list.</summary>
        void Setup()
        {
            uint msg = _mode switch
            {
                Mode.CarLotDDay => 0x3A12D2F5u, Mode.CarLot => 0x5415E304u, Mode.QuickRace => FEDb.IsSplitScreen ? 0x2CF6C390u : 0xDE511657u,
                Mode.Customize => 0xA936C3A2u, Mode.Career => 0x5415C3F1u, _ => 0xDE511657u,
            };
            SafehouseUi.Message(View, msg);
            SafehouseUi.Visible(View, 0x64F6D21F, false);
            if (_mode == Mode.Career || CarLot)
            {
                _originalCareer = P?.CurrentCar ?? 0;
                _list = CarLot ? ListStock : ListCareer;
            }
            else
            {
                _originalCar = FEDb.QuickRace().SelectedCar[_player];
                _list = ListStock;
                if (_mode == Mode.QuickRace && !string.IsNullOrEmpty(_originalCar))
                {
                    if (P != null && P.Cars.Any(c => c.Car == _originalCar)) _list = ListCareer;
                    if (FEDb.MyCars.Any(c => c.Car == _originalCar && c == GameSession.QuickCar)) _list = ListQuickRace;
                }
            }
            RefreshCarList();
            Refresh();
        }

        protected virtual List<SelectableCar> BuildList()
        {
            var list = new List<SelectableCar>();
            var lot = GameSession.Career?.CarLot ?? new List<CarLotEntry>();
            int bin = Mathf.Max(0, P?.Bin ?? 16);
            switch (_list)
            {
                case ListCareer:
                    if (P != null) foreach (var c in P.Cars) list.Add(new SelectableCar { Car = c.Car, Owned = c, Lot = lot.FirstOrDefault(l => l.Car == c.Car) });
                    break;
                case ListQuickRace:
                    foreach (var c in FEDb.MyCars) list.Add(new SelectableCar { Car = c.Car, Owned = c, Lot = lot.FirstOrDefault(l => l.Car == c.Car) });
                    break;
                case ListBonus:
                    return BonusList();
                default:
                    int binNow = _mode == Mode.CarLotDDay ? 16 : bin;   // CurrentBin is 16 during the D-Day prologue
                    foreach (var l in lot)
                    {
                        bool unlocked = l.UnlockedAt >= binNow;   // CareerUnlocker / QuickRaceUnlocker::IsCarUnlocked
                        if (!CarLot && AlwaysUnlocked.Contains(l.Car)) unlocked = true;   // quick race: the ten starter types
                        list.Add(new SelectableCar { Car = l.Car, Lot = l, Locked = !unlocked && _mode != Mode.FreePick });
                    }
                    break;
            }
            if (_list != ListQuickRace)
                list = list.OrderByDescending(c => c.Lot?.UnlockedAt ?? 0).ToList();   // SortCarsByUnlock [?: direction]
            return list;
        }

        /// <summary>QuickRaceUnlocker::IsCarUnlocked (UnlockSystem.cpp:164): RX8, Impreza, Mustang, SL500, 997S, IS300, GTI, Gallardo, Cobalt, Punto.</summary>
        static readonly HashSet<string> AlwaysUnlocked = new HashSet<string> { "rx8", "imprezawrx", "mustanggt", "sl500", "997s", "is300", "gti", "gallardo", "cobaltss", "punto" };

        /// <summary>RefreshBonusCarList (:1298): BL15..BL2, E3_DEMO_BMW, BONUS_SL65, BONUS_C6R, BONUS_GT2, CASTROLGT, the CE cars.</summary>
        List<SelectableCar> BonusList()
        {
            var list = new List<SelectableCar>();
            var presets = GameSession.Career?.PresetCars;
            int bin = P?.Bin ?? 16;
            bool beaten = P != null && P.CareerStarted && P.Bin <= 0;
            var cs = GameSession.ChallengeEvents();
            bool beatenCs = P != null && cs.Count > 0 && cs.All(e => P.HasWon(e.Name));
            void Add(string preset, bool unlocked, int unlockBin = -1)
            {
                if (presets == null || !presets.TryGetValue(preset, out var car)) return;
                list.Add(new SelectableCar { Car = car, Preset = preset, Locked = !unlocked, UnlockBin = unlockBin, Lot = GameSession.Career.CarLot.FirstOrDefault(l => l.Car == car) });
            }
            for (int n = 15; n >= 2; n--) Add("BL" + n, bin <= n - 1, n);
            Add("E3_DEMO_BMW", beaten);
            Add("BONUS_SL65", P != null && P.BonusCars.Contains("sl65"));   // HasBeatenSpecialChallengeEvent [?]
            Add("BONUS_C6R", beaten);
            Add("BONUS_GT2", beatenCs);
            if (P != null && P.BonusCars.Contains("castrolgt")) Add("CASTROLGT", true);   // HasBeenAwardedCastrolGT [?]
            foreach (var ce in new[] { "CE_CORVETTE", "CE_997S", "CE_ELISE", "CE_SL500", "CE_SUPRA", "CE_C6R", "CE_SL65", "CE_GT2", "CE_CAMARO", "CE_GTRSTREET" })
                Add(ce, true);   // GetIsCollectorsEdition: the PC v1.3 is the Collector's Edition build (NfsMWCollectorsRelease)
            return list;
        }

        protected void RefreshCarList()
        {
            Cars = BuildList();
            SetupForPlayer();
        }

        /// <summary>SetupForPlayer (:925): the list's last handle, else the current / selected car, else the head.</summary>
        void SetupForPlayer()
        {
            Index = 0;
            string want = _listHandles.TryGetValue(_list, out var h) ? h : null;
            int at = -1;
            if (want != null) at = Cars.FindIndex(c => c.Car == want);
            if (at < 0)
            {
                string cur = _mode == Mode.Career ? P?.Car?.Car : CarLot ? null : FEDb.QuickRace().SelectedCar[_player];
                if (cur != null) at = Cars.FindIndex(c => c.Car == cur);
            }
            if (CarLot) at = 0;
            Index = Mathf.Max(0, at);
            if (Cars.Count > 0) SetSelectedCar(Index);
        }

        /// <summary>SetSelectedCar (:1237): remembers the list handle; career select switches the current car at once.</summary>
        void SetSelectedCar(int index)
        {
            Index = index;
            var car = Cars[index];
            _listHandles[_list] = car.Car;
            SafehouseUi.Message(View, 0xA05A328E);   // LOADERAPPEAR
            SafehouseUi.Message(View, 0x9C0A27EB);   // FADE_IN_TEXT
            if (_mode == Mode.Career && car.Owned != null && P != null) P.CurrentCar = P.Cars.IndexOf(car.Owned);
            else if (_mode == Mode.QuickRace || _mode == Mode.FreePick) FEDb.QuickRace().SelectedCar[_player] = car.Car;
            _lastMove = Time.unscaledTime;
        }

        static readonly Color StockPaint = new Color(0.8f, 0.82f, 0.84f);

        void LoadCar()
        {
            if (Cars.Count == 0) return;
            var c = Cars[Index];
            if (c.Owned != null) FE.Showroom.Show(c.Car, c.Owned.Paint, c.Owned);
            else FE.Showroom.Show(c.Car, StockPaint);
            SafehouseUi.Message(View, 0x913FA282);   // LOADERLEAVE
        }

        /// <summary>RefreshHeader (:1026).</summary>
        protected void Refresh()
        {
            UpdateSliders();
            uint lang = _list switch { ListCareer => 0xEE053562u, ListQuickRace => 0x2414DE28u, ListBonus => 0xD8A058F7u, _ => 0xD9D6B954u };
            uint tex = _list switch { ListCareer => 0xF0BDDECDu, ListQuickRace => 0x9996CA1Eu, ListBonus => 0xBE5AD8A2u, _ => 0x3A541F7Fu };
            if (CarLot || _mode != Mode.Career) { SafehouseUi.Visible(View, 0x39DC21F9, false); SafehouseUi.Visible(View, 0xE998FE99, false); }
            SafehouseUi.SetLang(View, 0xAA9834BC, FEDb.L(lang));
            var t = FrontEndData.Texture(tex);
            if (t) View.SetTexture(View.Find(0xE3B271B8), t);
            View.Play(View.Find(0xD0F7C7CC), FEView.Hide, true);
            var car = Cars.Count > 0 ? Cars[Index] : null;
            if (car == null || car.Locked) SafehouseUi.Visible(View, 0x7379349B, false);
            if (car == null)
            {
                SafehouseUi.SetLang(View, 0x2D25B2C4, FEDb.L(0x58BBED2A));
                SafehouseUi.Message(View, 0xD9420CD5);
                SafehouseUi.SetLang(View, 0x36C1E04D, FEDb.L((_list & ListQuickRace) != 0 ? 0x0DA87B01u : 0x58BBED2Au));
                SafehouseUi.Visible(View, 0x0E9ED0A2, false);
                SafehouseUi.Visible(View, 0x18A4384F, false);
                FE.Showroom.HideCar();   // the garage stays, the car goes
                _lastMove = -1;
                return;
            }
            FE.Showroom.SetActive(true);
            if (!FEDb.Is(FEGameMode.Online) && !FEDb.Is(FEGameMode.Lan) && _mode != Mode.FreePick)
            {
                SafehouseUi.Visible(View, 0x0E9ED0A2, true);
                SafehouseUi.Visible(View, 0x18A4384F, true);
            }
            SafehouseUi.Visible(View, 0x7379349B, !car.Locked);
            SafehouseUi.Message(View, 0x7C4583DC);
            SafehouseUi.SetLang(View, 0x6F25A248, (Index + 1).ToString());
            SafehouseUi.SetLang(View, 0xB2037BDC, Cars.Count.ToString());
            if (car.Locked)
            {
                View.Play(View.Find(0xD0F7C7CC), FEView.Show, true);
                uint unlock = car.Preset != null ? BonusUnlockText(car.Preset) : 0x4ef2a115u;
                if (unlock == 0x4ef2a115)
                {
                    int rival = car.UnlockBin >= 0 ? car.UnlockBin : (car.Lot?.UnlockedAt ?? 15) + 1;
                    SafehouseUi.SetLang(View, 0x2D25B2C4, SafehouseUi.UnlockByRival(rival));
                }
                else SafehouseUi.SetLang(View, 0x2D25B2C4, FEDb.L(unlock));
            }
            SafehouseUi.Visible(View, 0xD6D32016, false);
            SafehouseUi.Visible(View, 0x79D6E45C, false);
            // GetManuLogoHash / GetLogoHash (VDB:920-945): CARSELECT_MANUFACTURER_<manu> / SECONDARY_LOGO_<collection>, else GENERIC_LOGO_128 / _256
            var badge = CarLotScreen.Badge(car.Car) ?? FrontEndData.TextureByName("GENERIC_LOGO_128");
            var logo = FrontEndData.TextureByName("SECONDARY_LOGO_" + car.Car.ToUpperInvariant()) ?? FrontEndData.TextureByName("GENERIC_LOGO_256");
            var manu = View.Find(0x3E01AD1D);
            if (manu != null) { if (badge) View.SetTexture(manu, badge); View.SetVisible(manu, badge != null); }
            var b = View.Find(0xB05DD708);
            if (b != null) { if (logo) View.SetTexture(b, logo); View.SetVisible(b, logo != null); }
            if (CarLot)
            {
                SafehouseUi.SetLang(View, 0x1930B057, SafehouseUi.Dollars(P?.Cash ?? 0));
                SafehouseUi.SetLang(View, 0x20C83C31, SafehouseUi.Dollars(car.Lot?.Cost ?? 0));
                SafehouseUi.SetLang(View, 0xDC18C4D4, FEDb.L(0xA9950B93));
                SafehouseUi.SetLang(View, 0xB94139F4, FEDb.L(0x7010BBF2));
            }
            // TheHeatMeter (:1138): only in the career car select
            _heat.SetVisibility(_mode == Mode.Career);
            View.SetVisible(View.Find(0xBDE6E3BA), _mode == Mode.Career);
            if (car.Owned != null && _mode == Mode.Career)
            {
                // car->IsCareer() (:1144): the career record's impound box, release / impound / bounty lines
                SafehouseUi.Visible(View, 0x39DC21F9, false);
                SafehouseUi.Visible(View, 0xE998FE99, false);
                if (ShopMarkers.Has(ShopRules.M_ImpoundBox))
                {
                    int n = ShopMarkers.Count(ShopRules.M_ImpoundBox);
                    SafehouseUi.Visible(View, 0x39DC21F9, true);
                    SafehouseUi.SetLang(View, 0x5B875870, n.ToString().PadLeft(2));
                    SafehouseUi.SetLang(View, 0xEA8AECD9, n.ToString().PadLeft(2));
                }
                var rec = FEImpound.Record(car.Owned, false);
                if (FEImpound.IsReleasable(car.Owned))
                {
                    SafehouseUi.SetLang(View, 0x72E7EA88, FEDb.L(0x9db4df7d));
                    SafehouseUi.SetLang(View, 0x9D974DF3, FEDb.L(0x073b79e0));
                    SafehouseUi.SetLang(View, 0x322B18F9, SafehouseUi.Dollars(FEImpound.ReleaseCost(car.Owned)));
                    SafehouseUi.SetLang(View, 0x7044A5A4, SafehouseUi.Dollars(P?.Cash ?? 0));
                    SafehouseUi.Visible(View, 0x0E9ED0A2, false);
                }
                else if (FEImpound.IsImpounded(car.Owned))
                {
                    SafehouseUi.SetLang(View, 0x72E7EA88, FEDb.L(0x9db4df7d));
                    SafehouseUi.SetLang(View, 0x9D974DF3, FEDb.L(0x073b79e0));
                    SafehouseUi.SetLang(View, 0x322B18F9, FEDb.L(0xaefedad9));
                    SafehouseUi.SetLang(View, 0x7044A5A4, SafehouseUi.Dollars(P?.Cash ?? 0));
                    SafehouseUi.Visible(View, 0x0E9ED0A2, false);
                    if (ShopMarkers.Has(ShopRules.M_ImpoundRelease))
                    {
                        int n = ShopMarkers.Count(ShopRules.M_ImpoundRelease);
                        SafehouseUi.Visible(View, 0xE998FE99, true);
                        SafehouseUi.SetLang(View, 0xCC59B910, n.ToString().PadLeft(2));
                        SafehouseUi.SetLang(View, 0xB8F9938A, n.ToString().PadLeft(2));
                        SafehouseUi.Visible(View, 0x39DC21F9, false);
                    }
                }
                else
                {
                    SafehouseUi.SetLang(View, 0x72E7EA88, FEDb.L(0x17574B0E));
                    SafehouseUi.SetLang(View, 0x9D974DF3, FEDb.L(0x915F4D26));
                    if (!FEDb.Is(FEGameMode.Online) && !FEDb.Is(FEGameMode.Lan)) SafehouseUi.Visible(View, 0x0E9ED0A2, true);
                    SafehouseUi.SetLang(View, 0x322B18F9, SafehouseUi.Dollars(rec?.Bounty ?? 0));
                    SafehouseUi.SetLang(View, 0x7044A5A4, SafehouseUi.Dollars(FEImpound.FineValue(car.Owned)));
                }
                float heat = CarParts.HeatOf(car.Owned);
                _heat.SetCurrent(heat); _heat.SetPreview(heat); _heat.Draw();
            }
            else _heat.SetVisibility(false);
            RefreshBusted(car);
        }

        void Scroll(int dir)
        {
            if (Cars.Count == 0) return;
            int i = (Index + dir + Cars.Count) % Cars.Count;
            if (i == Index) return;
            SetSelectedCar(i);
            Refresh();
        }

        /// <summary>ScrollLists (:1419): not in career / customize; Stock → Career → My Cars → Bonus, wrapping.</summary>
        void ScrollLists(int dir)
        {
            if (_mode != Mode.QuickRace) return;
            if (dir < 0) _list = _list == ListStock ? ListBonus : _list >> 1;
            else _list = _list == ListBonus ? ListStock : _list << 1;
            RefreshCarList();
            Refresh();
        }

        public override void Tick(MenuInput input)
        {
            if (_lastMove >= 0 && Time.unscaledTime - _lastMove >= 0.5f) { LoadCar(); _lastMove = -1; }
            if (input.Left) Scroll(-1);
            if (input.Right) Scroll(1);
            if (input.Up) ScrollLists(-1);
            if (input.Down) ScrollLists(1);
            if (input.Key1) Sell();
            if (input.Key2 && ImpoundInfoVisible && Cars.Count > 0) MaybeAddImpoundBox(Cars[Index]);   // __PAD_BUTTON4__ -> MaybeAddImpoundBox
            if (input.Key3 && Cars.Count > 0 && Index < Cars.Count)   // __PAD_BUTTON0__ -> Showcase.fng (not a locked car, not an impounded / releasable one)
            {
                var sc = Cars[Index];
                bool blocked = sc.Locked || (_mode == Mode.Career && sc.Owned != null && (FEImpound.IsImpounded(sc.Owned) || FEImpound.IsReleasable(sc.Owned)));
                if (!blocked) FE.Push(new ShowcaseScreen(sc.Owned ?? new OwnedCar { Car = sc.Car }));
            }
            if (input.Accept) { Accept(); return; }
            if (input.Back) Back();
        }

        // ---- accept / back ----------------------------------------------------------------------------------------------

        protected virtual void Accept()
        {
            if (Cars.Count == 0) return;
            var car = Cars[Index];
            if (car.Locked) return;
            if (_lastMove >= 0 && Time.unscaledTime - _lastMove < 0.5f) return;   // (RealTimer − tLastEventTimer) < 0.5 s
            switch (_mode)
            {
                case Mode.CarLot:
                case Mode.CarLotDDay:
                    BuyFromLot(car);
                    return;
                case Mode.Career:
                    if (car.Owned != null && (FEImpound.IsImpounded(car.Owned) || FEImpound.IsReleasable(car.Owned))) { MaybeReleaseCar(car); return; }
                    FE.Pop();   // 0x2E76EDFB -> EXIT_COMPLETE -> MainMenu_Sub (the crib)
                    GameSession.Profile?.Save();
                    GameFlow.RespawnPlayerCar();
                    return;
                case Mode.Customize:
                    AddToMyCars(car);
                    return;
                case Mode.FreePick:
                    Picked(car);
                    return;
            }
            // quick race
            if (FEDb.RaceMode == GRaceType.Drag || !FEDb.Data.Player(_player).TransmissionPromptOn) Commit(car);
            else ChooseTransmission(car);
        }

        /// <summary>ChooseTransmission (:1225): Manual (0x5F5E3886 → 1) / Auto (0x1A2826E1 → 0), the current one first.</summary>
        void ChooseTransmission(SelectableCar car)
        {
            var ps = FEDb.Data.Player(_player);
            FEDialog.ShowTwoButtons(FE, FEDialog.Title.Confirmation, 0x317D3005, 0x8CD532A0,
                () => { ps.Transmission = 1; Commit(car); },
                () => { ps.Transmission = 0; Commit(car); },
                null, ps.Transmission != 0 ? 0 : 1, FEDb.L(0x6F5401D1));
        }

        /// <summary>The selected car becomes the player's quick race car; player 1 in split screen → PressStart(1), else the race.</summary>
        void Commit(SelectableCar car)
        {
            var owned = car.Owned != null ? car.Owned : new OwnedCar { Car = car.Car };
            CarParts.Migrate(owned);
            if (_player == 0) GameSession.QuickCar = owned; else GameSession.QuickCar2 = owned;
            FEDb.Save();
            if (FEDb.IsSplitScreen && _player == 0) { FE.Replace(new PressStartScreen(1)); return; }
            var ev = TrackSelectScreen.Selected();
            if (ev == null) { FE.Pop(); return; }
            QuickRaceStarter.Start(ev, FEDb.QuickRace());
        }

        /// <summary>The car lot's buy (:620-640) and the dialogs' replies (dialog_message_yes / 0xB1EE867D).</summary>
        void BuyFromLot(SelectableCar car)
        {
            var p = P;
            int cost = car.Lot?.Cost ?? 0;
            if (p == null) return;
            if (cost > p.Cash) { FEDialog.ShowOk(FE, FEDialog.Title.Alert, FEDb.L(0x40FA955D)); return; }
            if (p.Cars.Count > 9) { FEDialog.ShowOk(FE, FEDialog.Title.Alert, FEDb.L(0x41030A1B)); return; }
            if (_mode == Mode.CarLotDDay)
            {
                FEDialog.ShowYesNo(FE, FEDb.L(0x74317CBC), () => Buy(car, true));
                return;
            }
            FEDialog.ShowThreeButtons(FE, FEDialog.Title.Alert, 0x5B9D89D0, 0x889D822E, 0x1A294DAD,
                () => Buy(car, true), () => Buy(car, false), null, null, 2, FEDb.L(0x8C451EBA));
        }

        /// <summary>CreateNewCareerCar + SpendCash (+ SetCurrentCar for Drive Now), then RaceStarter::StartCareerFreeRoam.</summary>
        void Buy(SelectableCar car, bool driveNow)
        {
            var p = P;
            int cost = car.Lot?.Cost ?? 0;
            p.Cash = Mathf.Max(0, p.Cash - cost);   // CareerSettings::SpendCash clamps at 0
            p.Cars.Add(new OwnedCar { Car = car.Car });
            if (driveNow) p.CurrentCar = p.Cars.Count - 1;
            p.Save();
            if (_mode == Mode.CarLotDDay) { Story.StarterBought(); return; }
            StartCareerFreeRoam();
        }

        static void StartCareerFreeRoam()
        {
            var fe = FrontEnd.Instance;
            bool world = NfsMw.GameBootstrap.Instance != null && NfsMw.GameBootstrap.Instance.Player != null;
            if (world) { GameFlow.RespawnPlayerCar(); fe?.CloseAll(); }
            else GameFlow.StartCareer();
        }

        /// <summary>My Cars "Add new car" (customize mode, :548): CreateNewCustomCar, select it, back to MyCarsManager.</summary>
        void AddToMyCars(SelectableCar car)
        {
            var c = new OwnedCar { Car = car.Car };
            CarParts.Migrate(c);
            FEDb.MyCars.Add(c);
            FEDb.QuickRace().SelectedCar[0] = c.Car;
            for (int i = 0; i < FEDb.Data.QuickRace.Length; i++) FEDb.Data.QuickRace[i].SelectedCar[0] = c.Car;
            GameSession.QuickCar = c;
            FEDb.Save();
            FE.Replace(new MyCarsScreen());
        }

        /// <summary>The port's free-roam / LAN / split-screen pick (no counterpart in the game) [ours].</summary>
        protected virtual void Picked(SelectableCar car)
        {
            GameSession.QuickCar = car.Owned ?? new OwnedCar { Car = car.Car };
            GameFlow.StartFreeRoam();
        }

        void Back()
        {
            switch (_mode)
            {
                case Mode.CarLotDDay:
                    return;   // goback = !IsDDay
                case Mode.CarLot:
                    StartCareerFreeRoam();
                    return;
                case Mode.Career:
                {
                    // the original car impounded / releasable -> 0x630931B6, no way back (:690)
                    var orig = P != null && _originalCareer >= 0 && _originalCareer < P.Cars.Count ? P.Cars[_originalCareer] : null;
                    if (orig != null && (FEImpound.IsImpounded(orig) || FEImpound.IsReleasable(orig))) { FEDialog.ShowOk(FE, FEDialog.Title.Alert, FEDb.L(0x630931b6)); return; }
                    if (P != null && orig != null && !FEImpound.IsImpounded(orig)) P.CurrentCar = _originalCareer;
                    FE.Pop();
                    return;
                }
                case Mode.Customize:
                    FE.Replace(new MyCarsScreen());
                    return;
                case Mode.QuickRace:
                    FEDb.QuickRace().SelectedCar[_player] = _originalCar ?? "";
                    if (FEDb.IsSplitScreen) { FE.Replace(new PressStartScreen(_player == 1 ? 1 : 0)); return; }
                    FE.Replace(new TrackOptionsScreen());
                    return;
                default:
                    FE.Pop();
                    return;
            }
        }

        /// <summary>PAD_BUTTON5 (:509): career, not the car lot or D-Day; trade the car in for cost / 2 (no refund of parts).</summary>
        void Sell()
        {
            if (_mode != Mode.Career || Cars.Count == 0 || P == null) return;
            var car = Cars[Index];
            if (car.Owned != null && FEImpound.IsImpounded(car.Owned)) { FEDialog.ShowOneButton(FE, FEDialog.Title.Alert, 0x417b2601, FEDb.L(0x80e4f27c)); return; }
            if (FEImpound.NumAvailableCareerCars() < 2) { FEDialog.ShowOneButton(FE, FEDialog.Title.Alert, 0x417b2601, FEDb.L(0x9A772BD6)); return; }
            int price = (car.Lot?.Cost ?? 0) / 2;
            string blurb = FEDb.L(0xB4A40135).Replace("%s", price.ToString());
            FEDialog.ShowYesNo(FE, blurb, () =>
            {
                var p = P;
                int at = p.Cars.IndexOf(car.Owned);
                if (at < 0) return;
                p.Cash += price;   // AwardCash(cost / 2)
                bool wasOriginal = at == _originalCareer;
                FEImpound.OnCarRemoved(at, true);   // DeleteCareerCar(handle, was_sold = true): BackupSoldCarHistory (QRCS:478)
                p.Cars.RemoveAt(at);
                if (p.CurrentCar >= at && p.CurrentCar > 0) p.CurrentCar--;
                if (_originalCareer > at) _originalCareer--;
                p.CurrentCar = Mathf.Clamp(p.CurrentCar, 0, p.Cars.Count - 1);
                if (wasOriginal) _originalCareer = p.CurrentCar;
                _listHandles.Remove(ListCareer);
                p.Save();
                RefreshCarList();
                Refresh();
            });
        }

    }

    /// <summary>The career car lot (Car_Select in car lot mode); starter = the D-Day lot of a new career.</summary>
    public class CarLotScreen : CarSelectScreen
    {
        public CarLotScreen(bool starter) : base(starter ? Mode.CarLotDDay : Mode.CarLot) { }

        public static string ManufacturerOf(string car) =>
            GameSession.Career?.CarLot.FirstOrDefault(c => c.Car == car)?.Manufacturer?.ToUpperInvariant();

        /// <summary>Manufacturer badge (CARSELECT_MANUFACTURER_x, GetManuLogoHash) of a car.</summary>
        public static Texture2D Badge(string car)
        {
            var m = ManufacturerOf(car);
            return m != null ? FrontEndData.TextureByName("CARSELECT_MANUFACTURER_" + m) : null;
        }
    }

    /// <summary>The new-career starter lot (CareerSettings::StartNewCareer → the D-Day car lot).</summary>
    public sealed class NewCareerScreen : CarLotScreen
    {
        public NewCareerScreen() : base(true) { }

        public override void Open()
        {
            if (GameSession.Profile == null) GameSession.NewCareer(Environment.UserName.Length > 0 ? Environment.UserName : "Player");
            base.Open();
        }
    }

    /// <summary>
    /// [ours] Car_Select as a plain pick: free roam (pause "Change Car"), the LAN and split-screen setup of the port.
    /// The game has no free roam outside the career.
    /// </summary>
    public sealed class QuickCarScreen : CarSelectScreen
    {
        readonly Action<string> _pick;
        readonly string _who;

        public QuickCarScreen(Action<string> pick = null, string who = null) : base(Mode.FreePick) { _pick = pick; _who = who; }

        public override void Open()
        {
            base.Open();
            if (_who != null)
            {
                View.Play(View.Find(0xE3FE27FE), FEView.Appear);
                View.SetVisible(View.Find(0xE3FE27FE), true);
                View.SetText(View.Find(0x42EF4C2E), _who);
            }
        }

        protected override List<SelectableCar> BuildList()
        {
            var list = base.BuildList();
            if (!list.Any(c => c.Car == "bmwm3gtr")) list.Insert(0, new SelectableCar { Car = "bmwm3gtr" });
            foreach (var bonus in GameSession.Profile?.BonusCars ?? new List<string>())
                if (!list.Any(c => c.Car == bonus)) list.Add(new SelectableCar { Car = bonus });
            foreach (var c in list) c.Locked = false;
            return list;
        }

        protected override void Picked(SelectableCar car)
        {
            if (_pick != null) { _pick(car.Car); return; }
            base.Picked(car);
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// MyCarsManager.fng (customize/MyCarsManager.cpp; PC 0x7A7380): the main menu "My Cars", a 5 × 2 grid of the quick
    /// race custom cars (CAR_ICON_1..10, manufacturer logos) plus "Add new car" while fewer than 20 exist. Enter on a car
    /// customizes it (BeginCarCustomize CEP_MAIN_MENU), on "Add new car" opens Car_Select in customize mode; key 1 deletes
    /// the car (0x4F68196E); Back → MainMenu (saving when the stable changed). The car loads 0.25 s after a move; the
    /// selection is the quick race car of every race type. CARNAME 0xB271B295 = the car's name; 0xBFA25765 = "Add New Car"
    /// / "Customize Car".
    /// </summary>
    public sealed class MyCarsScreen : FEScreen
    {
        public override string Package => "MyCarsManager";
        public override bool ShowCar => true;
        const int W = 5, H = 2;
        sealed class Datum { public OwnedCar Car; }
        readonly List<Datum> _data = new List<Datum>();
        int _index, _top;
        float _loadAt = -1;
        bool _dirty;
        OwnedCar _selected;   // pSelectedCar
        readonly TwoStageSlider _accel = new TwoStageSlider(), _topSpeed = new TwoStageSlider(), _handling = new TwoStageSlider();

        /// <summary>MyCarsManager::Setup (0x7BE7D0): the car list, then the three TwoStageSliders (0..1, range 160), RefreshHeader.</summary>
        public override void Open()
        {
            FEDb.Load();
            FEDb.SetGameMode(FEGameMode.Customize);
            RefreshCarList();
            _accel.Init(View, "ACCELERATION", 0f, 1f, 0f, 0f, 0f, 160f);
            _topSpeed.Init(View, "TOPSPEED", 0f, 1f, 0f, 0f, 0f, 160f);
            _handling.Init(View, "HANDLING", 0f, 1f, 0f, 0f, 0f, 160f);
            RefreshHeader();
        }

        /// <summary>UpdateSliders (0x7B34C0): for a car (not "Add new car") value and preview = its EstimatePerformance rating.</summary>
        void UpdateSliders(OwnedCar car)
        {
            if (car == null) return;
            float a = CarSelectScreen.Rating(car, 0), t = CarSelectScreen.Rating(car, 1), h = CarSelectScreen.Rating(car, 2);
            _accel.SetValue(a); _accel.SetPreview(a); _accel.Draw();
            _topSpeed.SetValue(t); _topSpeed.SetPreview(t); _topSpeed.Draw();
            _handling.SetValue(h); _handling.SetPreview(h); _handling.Draw();
        }

        /// <summary>UpdateCar (0x7B3680): only when the datum changed — "Add new car" → LOADERLEAVE 0x913FA282 (no car), else
        /// LOADERAPPEAR 0xA05A328E; the car loads 0.25 s later (the SCREEN_TICK timer).</summary>
        void SelectionChanged(OwnedCar car)
        {
            if (car == _selected) return;
            if (car == null) { SafehouseUi.Message(View, 0x913FA282); _selected = null; }
            else { SafehouseUi.Message(View, 0xA05A328E); _selected = car; }
            _loadAt = Time.unscaledTime;
        }

        static bool CanAddMoreCars() => FEDb.MyCars.Count < 20;

        void RefreshCarList()
        {
            _data.Clear();
            string sel = FEDb.QuickRace().SelectedCar[0];
            int start = 0;
            foreach (var c in FEDb.MyCars)
            {
                if (c == GameSession.QuickCar || c.Car == sel) start = _data.Count;
                _data.Add(new Datum { Car = c });
            }
            if (CanAddMoreCars()) _data.Add(new Datum());
            _index = Mathf.Clamp(start, 0, Mathf.Max(0, _data.Count - 1));
            _loadAt = Time.unscaledTime;
        }

        /// <summary>ArrayScrollerMenu over CAR_ICON_n (5 wide, 2 high) and MyCarsManager::RefreshHeader (:153).</summary>
        void RefreshHeader()
        {
            int perPage = W * H;
            if (_index < _top) _top = _index / W * W;
            if (_index >= _top + perPage) _top = (_index / W - H + 1) * W;
            for (int i = 1; i <= perPage; i++)
            {
                var n = View.Find("CAR_ICON_" + i);
                if (n == null) continue;
                int k = _top + i - 1;
                bool has = k < _data.Count;
                View.SetVisible(n, has);
                if (!has) continue;
                var d = _data[k];
                var tex = d.Car != null ? CarLotScreen.Badge(d.Car.Car) ?? FrontEndData.TextureByName("GENERIC_LOGO_128") : FrontEndData.Texture(0x91D3A6BA);
                if (tex) View.SetTexture(n, tex);
                View.Play(n, k == _index ? FEView.Highlight : FEView.UnHighlight);
            }
            var cur = _data.Count > 0 ? _data[_index] : null;
            if (cur == null) return;
            SafehouseUi.SetLang(View, 0xB271B295, cur.Car != null ? CarNames.Display(cur.Car.Car) : FEDb.L(0x29AE0714));
            if (cur.Car != null)
            {
                // GetLogoHash (VDB:933): SECONDARY_LOGO_<car>, else GENERIC_LOGO_256
                var logo = FrontEndData.TextureByName("SECONDARY_LOGO_" + cur.Car.Car.ToUpperInvariant()) ?? FrontEndData.TextureByName("GENERIC_LOGO_256");
                if (logo) View.SetTexture(View.Find(0xB05DD708), logo);
                SafehouseUi.SetLang(View, 0xBFA25765, FEDb.L(0xC9847935));
                SafehouseUi.Message(View, 0x06D41CCC);   // editdeletecar
                for (int i = 0; i < FEDb.Data.QuickRace.Length; i++) FEDb.Data.QuickRace[i].SelectedCar[0] = cur.Car.Car;
                GameSession.QuickCar = cur.Car;
            }
            else
            {
                SafehouseUi.SetLang(View, 0xBFA25765, FEDb.L(0xC2598BD8));
                SafehouseUi.Message(View, 0x42EA22DD);   // addcar
            }
            View.SetVisible(View.Find(0xB05DD708), cur.Car != null);
            SafehouseUi.SetLang(View, 0x6F25A248, (_index + 1).ToString());
            SafehouseUi.SetLang(View, 0xB2037BDC, _data.Count.ToString());
            UpdateSliders(cur.Car);
            SelectionChanged(cur.Car);
        }

        void UpdateCar()
        {
            var cur = _data.Count > 0 ? _data[_index] : null;
            if (cur?.Car == null) { FE.Showroom.SetActive(true); FE.Showroom.HideCar(); return; }   // "Add new car": the garage without a car
            FE.Showroom.SetActive(true);
            FE.Showroom.Show(cur.Car.Car, cur.Car.Paint, cur.Car);
        }

        public override void Tick(MenuInput input)
        {
            if (_loadAt >= 0 && Time.unscaledTime - _loadAt >= 0.25f) { UpdateCar(); _loadAt = -1; }
            int n = _data.Count;
            if (n == 0) { if (input.Back) Leave(); return; }
            int was = _index;
            if (input.Left && _index > 0) _index--;
            if (input.Right && _index < n - 1) _index++;
            if (input.Up && _index - W >= 0) _index -= W;
            if (input.Down && _index + W < n) _index += W;
            if (_index != was) RefreshHeader();
            var cur = _data[_index];
            if (input.Accept)
            {
                if (cur.Car == null) { FE.Replace(new CarSelectScreen(CarSelectScreen.Mode.Customize)); return; }
                GameSession.QuickCar = cur.Car;
                _dirty = true;
                FE.Push(new CustomizeScreen());   // BeginCarCustomize(CEP_MAIN_MENU, car)
                return;
            }
            if (input.Key3 && cur.Car != null) { FE.Push(new ShowcaseScreen(cur.Car)); return; }   // PAD_BUTTON0 → Showcase.fng
            if (input.Key1 && cur.Car != null)
            {
                FEDialog.ShowYesNo(FE, FEDb.L(0x4F68196E), () =>
                {
                    FEDb.NotifyDeleteCar(cur.Car.Car);   // NotifyDeleteCar (0x58FF30) before DeleteCustomCar
                    FEDb.MyCars.Remove(cur.Car);
                    _dirty = true;
                    RefreshCarList();
                    RefreshHeader();
                });
                return;
            }
            if (input.Back) Leave();
        }

        public override void Resume()
        {
            FEDb.Save();
            RefreshHeader();
            UpdateCar();
        }

        void Leave()
        {
            if (_dirty) FEDb.Save();
            FEDb.ClearGameMode(FEGameMode.Customize);
            if (FE.Top == this && FE.Top != null)
            {
                // from the main menu the game switches back to MainMenu.fng; pushed from elsewhere (QA, career) just pop
                bool fromMain = !(NfsMw.GameBootstrap.Instance != null && NfsMw.GameBootstrap.Instance.Player != null) && GameSession.Mode == GameMode.FrontEnd;
                if (fromMain) FE.Replace(new MainMenuScreen()); else FE.Pop();
            }
        }
    }
}
