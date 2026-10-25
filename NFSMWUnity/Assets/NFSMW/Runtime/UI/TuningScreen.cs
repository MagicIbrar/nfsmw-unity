using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// Pause menu "Performance Tuning" — Pause_Performance_Tuning.fng, CustomTuningScreen (MenuScreens/InGame/CustomTuning.cpp;
    /// PC: create 0x5A9600, ctor 0x5A8FE0, Setup 0x5A6060, NotificationMessage 0x5A9910, SetSlidersForType 0x589F10,
    /// AddTuningSlider 0x594090, IsTuningAvailable 0x593F20, ShowHelpBlurb 0x5A61E0 / HideHelpBlurb 0x589FC0, StoreSettings
    /// 0x576170, SettingsDidNotChange 0x5762C0; TuningSlider 0x575B60-0x575F6F / ctor 0x589DB0) on UIWidgetMenu
    /// (iMaxWidgetsOnScreen 8, no scroll bar) over the FEng engine.
    /// <list type="bullet">
    /// <item>Seven TuningSlider rows (Steering, Handling, Brakes, Ride Height, Aerodynamics, NOS, Induction — turbo name / help
    /// when the car's induction at level 1 is a turbo): title OPTION_NAME_n (language hash), data OPTION_DATA_n "%2.1f" of
    /// (cur − min) / (max − min) × 10 (the PC package has no data strings: nothing printed), the slider group SLIDER_GROUPn
    /// (INIT when the tuning is available, else GREY, with the title), and two cSliders LEFT_SLIDER_n / RIGHT_SLIDER_n:
    /// negative = InitValues(min, mid, 0, mid + min − cur, −82), positive = InitValues(mid, max, 0, cur, 82) (Slider.cpp; see
    /// <see cref="CSlider"/>). Rows keep their package positions (TuningSlider::Position is empty).</item>
    /// <item>Tunings::LowerLimit / UpperLimit (0x66CA10 / 0x66CA20) read TuningLimits 0x8A9350: −1 / +1 for all 7 paths; the step
    /// is (upper − lower) × 0.1. An unavailable tuning (IsTuningAvailable :239) can still be selected but Act ignores it.</item>
    /// <item>Up / down move the cursor 0x06745352 (POS1..POS8, wrapped); left / right Act on the row and store the value in the
    /// temporary record; L / R triggers (PageUp / PageDown) cycle the three setups (0x05CDDED4 ← 0x40230063..65); key 1
    /// (PAD_BUTTON5) shows the help (SHOWHELP 0x89D332A9; CTextScroller over HELP_TEXT_LINE1..7 with the row's help label, the
    /// scroll bar "Scrollbar"; while it shows, directions scroll the text), back hides it.</item>
    /// <item>Accept: StoreSettings (all 3 setups + the active one, ETuneVehicle) and the package's leave (0x587C018B); back with no
    /// change leaves, else InGameDialog yes / no 0xE9CB802F (yes 0x775DBA97 leaves without storing); EXIT_COMPLETE → Pause_Main
    /// (start: EUnPause).</item>
    /// </list>
    /// </summary>
    public sealed class CustomTuningScreen : FEMenuScreen
    {
        public override string Package => FrontEndData.Packages.ContainsKey("Pause_Performance_Tuning") ? "Pause_Performance_Tuning" : "Pause_Options";

        /// <summary>Physics::Tunings::LowerLimit / UpperLimit: TuningLimits (speed.exe 0x8A9350) = −1 / +1 for every path.</summary>
        public const float LowerLimit = -1f, UpperLimit = 1f;

        const uint ShowHelp = 0x89D332A9, HideHelp = 0x950AD1C2, LeaveScreen = 0x587C018B, MsgDiscardYes = 0x775DBA97, SettingName = 0x05CDDED4;
        const int MaxWidgetsOnScreen = 8;

        /// <summary>A TuningSlider (FEToggleWidget(true): always enabled; bActive = the tuning is available).</summary>
        sealed class TuningSlider
        {
            public int Path;
            public uint Title, HelpBlurb;
            public bool Active;
            public float Min, Max, Increment, Current;
            public FEView.Node TitleObj, DataObj, Backing, LeftImg, RightImg, Group;
            public readonly CSlider Negative = new CSlider(), Positive = new CSlider();
        }

        readonly List<TuningSlider> _options = new List<TuningSlider>();
        TuningSlider _current;
        int _viewTop = 1;
        OwnedCar _car;
        float[] _temp = new float[21];
        int _type;
        bool _helpVisible, _exitWithStart;
        TextScroller _help;
        FEScrollBar _helpScrollBar;

        protected override void Setup()
        {
            _helpScrollBar = new FEScrollBar(View, "Scrollbar");
            _car = GameSession.ActiveCar;
            if (_car != null)
            {
                CarParts.Migrate(_car);
                _temp = (float[])(_car.Tunings ?? new float[21]).Clone();
                if (_temp.Length < 21) System.Array.Resize(ref _temp, 21);
                _type = Mathf.Clamp(_car.ActiveTuning, 0, 2);
            }
            for (int path = 0; path < 6; path++) AddTuningSlider(path, false);
            // Physics::Upgrades::SetLevel(vehicle, PUT_INDUCTION, 1); InductionType == INDUCTION_TURBO_CHARGER (SPOOL > 0)
            bool turbo = false;
            if (_car != null)
            {
                var probe = ShopCart.Copy(_car);
                CarParts.SetPerfLevel(probe, ShopRules.Induction, 1);
                turbo = CarSpec.FromAttributes(GameData.Attributes, _car.Car, probe).Spool > 0;
            }
            AddTuningSlider(CarSpec.TuneInduction, turbo);
            SetSlidersForType();
            SetInitialOption(1);
        }

        /// <summary>CustomTuningScreen::IsTuningAvailable (CT:239-281).</summary>
        public static bool IsTuningAvailable(OwnedCar c, int path)
        {
            if (c == null) return false;
            switch (path)
            {
                case CarSpec.TuneSteering: case CarSpec.TuneHandling: case CarSpec.TuneRideHeight: return c.Suspension > 0;
                case CarSpec.TuneBrakes: return c.Brakes > 0;
                case CarSpec.TuneAero:
                {
                    var stockSpoiler = CarParts.Stock(c.Car, ShopRules.Slot.Spoiler);
                    var spoiler = CarParts.Installed(c, ShopRules.Slot.Spoiler);
                    if (spoiler != null && spoiler != stockSpoiler) return true;
                    var body = CarParts.Installed(c, ShopRules.Slot.Body);
                    return body != null && body != CarParts.Stock(c.Car, ShopRules.Slot.Body);
                }
                case CarSpec.TuneNos: return c.Nitrous > 0;
                case CarSpec.TuneInduction: return c.Induction > 0;
            }
            return false;
        }

        /// <summary>GetNameForPath (CT:283).</summary>
        static uint GetNameForPath(int path, bool turbo)
        {
            switch (path)
            {
                case 0: return 0xC56C5B36; case 1: return 0xFCEEBE1A; case 2: return 0x2EE2A74D; case 3: return 0xCF6215D1;
                case 4: return 0x7196ACB4; case 5: return 0x934FCFA9; case 6: return turbo ? 0x86945521u : 0xE3A932E0u;
            }
            return 0;
        }

        /// <summary>GetHelpForPath (CT:304): available / unavailable help labels.</summary>
        static uint GetHelpForPath(int path, bool active, bool turbo)
        {
            if (active)
                switch (path)
                {
                    case 0: return 0x4A1F18BE; case 1: return 0x81A17BA2; case 2: return 0x473862D5; case 3: return 0xD6C24659;
                    case 4: return 0x64FCEE3C; case 5: return 0xB65CFC31; case 6: return turbo ? 0xB5DCBFA9u : 0xD70F7468u;
                }
            else
                switch (path)
                {
                    case 0: return 0x221D7E85; case 1: return 0x18C12069; case 2: return 0xC213A6DC; case 3: return 0xB6D02C60;
                    case 4: return 0xC6A99483; case 5: return 0xB8124038; case 6: return turbo ? 0xB7D6F7B0u : 0xE3F577AFu;
                }
            return 0;
        }

        /// <summary>AddTuningSlider (CT:343): the row objects by the UIWidgetMenu names for iIndexToAdd, the slider group
        /// SLIDER_GROUPn, the cSliders LEFT_SLIDER_n / RIGHT_SLIDER_n.</summary>
        void AddTuningSlider(int path, bool turbo)
        {
            bool active = IsTuningAvailable(_car, path);
            int i = _options.Count + 1;
            var s = new TuningSlider
            {
                Path = path, Title = GetNameForPath(path, turbo), HelpBlurb = GetHelpForPath(path, active, turbo), Active = active,
                TitleObj = Obj("OPTION_NAME_" + i), DataObj = Obj("OPTION_DATA_" + i), Backing = Obj("OPTION_BACKING_" + i),
                LeftImg = Obj("LEFT_ARROW_" + i) ?? Obj("LEFT_ARROW_0"), RightImg = Obj("RIGHT_ARROW_" + i) ?? Obj("RIGHT_ARROW_0"),
                Group = Obj("SLIDER_GROUP" + i),
            };
            s.Negative.InitObjects(View, "LEFT_SLIDER_" + i);
            s.Positive.InitObjects(View, "RIGHT_SLIDER_" + i);
            _options.Add(s);
        }

        /// <summary>DrawSettingName (CT:225).</summary>
        void DrawSettingName() => SetLanguageHash(SettingName, 0x40230063u + (uint)Mathf.Clamp(_type, 0, 2));

        /// <summary>SetSlidersForType (CT:410): the setup's values into every slider (SetSliderValues + Draw).</summary>
        void SetSlidersForType()
        {
            DrawSettingName();
            foreach (var s in _options)
            {
                float lower = LowerLimit, upper = UpperLimit, inc = (upper - lower) * 0.1f;
                SetSliderValues(s, lower, upper, inc, _temp[_type * 7 + s.Path]);
                Draw(s);
            }
        }

        /// <summary>TuningSlider::SetSliderValues (CT:104).</summary>
        static void SetSliderValues(TuningSlider s, float min, float max, float inc, float cur)
        {
            s.Increment = inc; s.Min = min; s.Max = max; s.Current = cur;
            float mid = (max + min) * 0.5f;
            s.Negative.InitValues(min, mid, 0f, mid + min - cur, -82f);
            s.Positive.InitValues(mid, max, 0f, cur, 82f);
        }

        /// <summary>TuningSlider::Draw (CT:50).</summary>
        void Draw(TuningSlider s)
        {
            View.SetText(s.TitleObj, Lang(s.Title));
            if (s.DataObj != null) View.SetText(s.DataObj, ((s.Current - s.Min) / (s.Max - s.Min) * 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            if (s.Active) { SetScript(s.TitleObj, FEHash.UnHighlight); SetScript(s.Group, FEHash.Init); }
            else { SetScript(s.TitleObj, FEHash.Grey); SetScript(s.Group, FEHash.Grey); }
            s.Negative.Draw();
            s.Positive.Draw();
        }

        /// <summary>TuningSlider::Act (CT:30): left / right step Current within Min..Max, both halves follow.</summary>
        void Act(TuningSlider s, uint msg)
        {
            if (!s.Active) return;
            if (msg == FEHash.PadRight) s.Current += s.Increment;
            else if (msg == FEHash.PadLeft) s.Current -= s.Increment;
            s.Current = Mathf.Min(Mathf.Max(s.Current, s.Min), s.Max);
            s.Negative.SetValue((s.Max + s.Min) * 0.5f + s.Min - s.Current);
            s.Positive.SetValue(s.Current);
            Draw(s);
        }

        /// <summary>TuningSlider::SetFocus (CT:69): the title becomes the current button; available rows UNHIGHLIGHT + group INIT.</summary>
        void SetFocus(TuningSlider s)
        {
            View.SetCurrentButton(s.TitleObj, false);
            if (s.Active) { SetScript(s.TitleObj, FEHash.UnHighlight); SetScript(s.Group, FEHash.Init); }
        }

        /// <summary>TuningSlider::UnsetFocus (CT:79).</summary>
        void UnsetFocus(TuningSlider s)
        {
            if (s.Active) { SetScript(s.TitleObj, FEHash.UnHighlight); SetScript(s.Group, FEHash.Init); }
            else { SetScript(s.TitleObj, FEHash.Grey); SetScript(s.Group, FEHash.Grey); }
        }

        int IndexOf(TuningSlider s) => s == null ? 0 : _options.IndexOf(s) + 1;

        /// <summary>UIWidgetMenu::SetInitialOption (0x5A5EA0) + SetOption (0x59BFB0).</summary>
        void SetInitialOption(int n)
        {
            if (_options.Count == 0) return;
            SetOption(_options[Mathf.Clamp(n, 1, _options.Count) - 1]);
            // SyncViewToSelection → Reposition (0x59BFF0): the rows in the window Show (FEToggleWidget::Show: title, data,
            // arrows, backing visible), Draw; Position is empty for TuningSlider
            foreach (var s in _options)
            {
                bool inView = IndexOf(s) >= _viewTop && IndexOf(s) < _viewTop + MaxWidgetsOnScreen;
                foreach (var o in new[] { s.TitleObj, s.DataObj, s.LeftImg, s.RightImg, s.Backing }) View.SetVisible(o, inView);
                if (inView) Draw(s);
            }
            UpdateCursorPos();
        }

        void SetOption(TuningSlider s)
        {
            if (_current != null && _current != s) UnsetFocus(_current);
            _current = s;
            if (_current != null) SetFocus(_current);
            UpdateCursorPos();
        }

        /// <summary>UIWidgetMenu::UpdateCursorPos (0x593B10): POS(index − view top + 1).</summary>
        void UpdateCursorPos()
        {
            var cursor = Obj(0x06745352);
            if (cursor == null) return;
            int pos = _current == null ? 0 : IndexOf(_current) - _viewTop + 1;
            SetScript(cursor, pos > 0 && pos <= MaxWidgetsOnScreen ? HashF("POS" + pos) : FEHash.Hide);
        }

        /// <summary>UIWidgetMenu::ScrollWrapped (0x5A5A90) for a list shorter than the window (every row enabled).</summary>
        void ScrollWrapped(int dir)
        {
            if (_options.Count == 0) return;
            int i = IndexOf(_current);
            int next = dir > 0 ? (i >= _options.Count ? 1 : i + 1) : (i <= 1 ? _options.Count : i - 1);
            SetOption(_options[next - 1]);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (_helpVisible) _help?.HandleNotificationMessage(msg);
            if (msg == FEHash.InitComplete) foreach (var s in _options) UnsetFocus(s);
            bool direction = msg == FEHash.PadLeft || msg == FEHash.PadRight || msg == FEHash.PadUp || msg == FEHash.PadDown;
            if (!_helpVisible || !direction) WidgetMenuMessage(msg);
            switch (msg)
            {
                case FEHash.PadStart:
                    if (_helpVisible) return;
                    _exitWithStart = true;
                    goto case FEHash.PadAccept;
                case FEHash.PadAccept:
                    if (!_helpVisible) { StoreSettings(); View.PackageMessage(LeaveScreen); }
                    break;
                case FEHash.PadLeft:
                case FEHash.PadRight:
                    if (!_helpVisible && _current != null) _temp[_type * 7 + _current.Path] = _current.Current;
                    break;
                case FEHash.PadLTrigger: ScrollTypes(-1); break;
                case FEHash.PadRTrigger: ScrollTypes(1); break;
                case FEHash.PadButton5:
                    if (!_helpVisible) ShowHelpBlurb();
                    break;
                case FEHash.PadBack:
                    if (_helpVisible) HideHelpBlurb();
                    else if (SettingsDidNotChange()) View.PackageMessage(LeaveScreen);
                    else DialogInterface.ShowTwoButtons(this, "InGameDialog.fng", DialogTitle.Alert, 0x70E01038, 0x417B25E4,
                        MsgDiscardYes, DialogInterface.MsgNo, DialogInterface.MsgNo, true, 1, Lang(0xE9CB802F));
                    break;
                case MsgDiscardYes:
                    View.PackageMessage(LeaveScreen);
                    break;
                case FEHash.ExitComplete:
                    FE.Defer(() =>
                    {
                        if (FE.Top != this) return;
                        if (_exitWithStart) FE.CloseAll();   // EUnPause
                        else FE.Pop();                       // QueuePackageSwitch("Pause_Main.fng")
                    });
                    break;
            }
        }

        /// <summary>UIWidgetMenu::NotificationMessage (feUIWidgetMenu.cpp:41): INIT_COMPLETE refocuses, accept / back / left /
        /// right go to the current widget's Act, up / down ScrollWrapped.</summary>
        void WidgetMenuMessage(uint msg)
        {
            switch (msg)
            {
                case FEHash.InitComplete:
                    if (_current != null) SetOption(_current);
                    break;
                case FEHash.ButtonPressed: case FEHash.PadBack: case FEHash.PadLeft: case FEHash.PadRight:
                    if (_current != null) Act(_current, msg);
                    break;
                case FEHash.PadUp: ScrollWrapped(-1); break;
                case FEHash.PadDown: ScrollWrapped(1); break;
            }
        }

        /// <summary>ScrollTypes (CT:200): 0..2 wrapped; a change redraws every slider.</summary>
        void ScrollTypes(int dir)
        {
            if (_helpVisible) return;
            int t = _type + (dir > 0 ? 1 : -1);
            if (t > 2) t = 0;
            if (t < 0) t = 2;
            if (t != _type) { _type = t; SetSlidersForType(); }
        }

        /// <summary>ShowHelpBlurb (CT:426).</summary>
        void ShowHelpBlurb()
        {
            if (_current == null) return;
            View.PackageMessage(ShowHelp);
            UnsetFocus(_current);
            if (_help == null)
            {
                var line1 = Obj("HELP_TEXT_LINE1");
                _help = new TextScroller();
                _help.Initialise(View, line1 != null ? (int)line1.Obj.MaxWidth : 0, 7, "HELP_TEXT_LINE%d", line1?.Label?.Font);
                _help.UseScrollBar(_helpScrollBar);
                _help.SetTextHash(_current.HelpBlurb);
            }
            _helpVisible = true;
        }

        /// <summary>HideHelpBlurb (CT:443).</summary>
        void HideHelpBlurb()
        {
            View.PackageMessage(HideHelp);
            if (_current != null) SetFocus(_current);
            _help = null;
            _helpVisible = false;
        }

        /// <summary>SettingsDidNotChange (CT:466).</summary>
        bool SettingsDidNotChange()
        {
            if (_car == null) return true;
            var orig = _car.Tunings ?? new float[21];
            for (int i = 0; i < 21; i++) if ((i < orig.Length ? orig[i] : 0f) != _temp[i]) return false;
            return _type == _car.ActiveTuning;
        }

        /// <summary>StoreSettings (CT:454) + ETuneVehicle: the live car's spec is re-read with the new tunings.</summary>
        void StoreSettings()
        {
            if (_car == null) return;
            _car.Tunings = (float[])_temp.Clone();
            _car.ActiveTuning = _type;
            GameSession.Profile?.Save();
            var pc = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (pc != null && pc.Spec != null) pc.Spec = Upgrades.Apply(pc.Spec, _car);
        }
    }
}
