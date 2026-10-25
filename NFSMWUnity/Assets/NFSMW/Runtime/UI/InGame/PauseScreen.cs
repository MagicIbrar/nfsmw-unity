using System;
using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Pause_Main.fng — PauseMenu (MenuScreens/InGame/uiPause.cpp; PC ctor 0x54FE70, Setup 0x546360, SetupOptions 0x52B4D0,
    /// NotificationMessage 0x54FF10, IsTuningAvailable 0x510900), an IconScrollerMenu. Options (icon texture hash, name
    /// label), in the exe's order:
    /// career free roam — prologue: Resume Free Roam, Quit to Career Main Menu, Options; final epic chase: + Performance
    /// Tuning; in a pursuit: Resume Free Roam, Performance Tuning, Options; else Resume Free Roam, Jump to Safe House,
    /// Performance Tuning, Options. Career racing — prologue: Resume Race, Restart, Quit to Career Main Menu, Options;
    /// epic: + Performance Tuning; else Resume Race, Restart, Jump to Safe House, Quit to Free Roam, Performance Tuning,
    /// Options. Quick race: Resume Race, Restart, Quit to Quick Race Menu (challenge series: Quit to Main Menu), Performance
    /// Tuning (not tollbooth / challenge), Options; split screen: Resume, Restart, Quit to Quick Race Menu, Options.
    /// "Called from post race" (Arg ≠ 0, the restart menu): Restart + Quit to Career Main Menu / Jump to Safe House +
    /// Quit to Free Roam, or Restart + Quit for a quick race; title RESTART MENU and back disabled.
    /// Choosing: resume / options / tuning play the handler's FORWARD (0xDE6EFF34) at once; restart and the quits ask
    /// first (InGameDialog, OK / Cancel, Cancel highlighted). EXIT_COMPLETE then acts on the selection hash.
    /// [?] The icon scroller's own layout (feIconScrollerMenu.cpp) is approximated: 72 px spacing, side icons smaller
    /// and dimmer; the option slots are the package's option_1..10.
    /// </summary>
    public sealed class PauseScreen : FEMenuScreen
    {
        const uint Handler = 0x47FF4E7C, Forward = 0xDE6EFF34, Title = 0x863404B5, OptionTitle = 0x5E7B09C9;
        const uint SelRestart = 0xFBDF2EE3, SelResume = 0xFDAE152F, SelQuitFreeRoam = 0xCDD2635A, SelOptions = 0x33195CF0,
            SelTuning = 0x78F1C035, SelQuitToFE = 0xE5C9C609, SelQuitMainMenu = 0x85162CB0;
        const uint MsgQuitToFE = 0x43DA9FD0, MsgQuitFreeRoam = 0x451E768E, MsgQuitMain = 0xC9BFD1C3, MsgQuitQuick = 0x30F32A49,
            MsgRestart = 0xE1A57D51, MsgCancel = 0xB4623F67, MsgUnknownReply = 0x30EB8F53;

        sealed class Option
        {
            public uint Tex, Name;
            public bool Locked;
            public Action React;
        }

        static uint _selection;
        readonly bool _fromPostRace;
        readonly List<Option> _options = new List<Option>();
        readonly List<FEView.Node> _slots = new List<FEView.Node>();
        int _sel;
        float _scroll;
        uint _prevButton;
        bool _subScreen;
        FEView.Node _master;

        public override string Package => FrontEndData.Packages.ContainsKey("Pause_Main") ? "Pause_Main" : "MainMenu_Sub";

        public PauseScreen() : this(false) { }

        /// <summary>calledFromPostRace: the restart menu (ScreenConstructorData.Arg ≠ 0).</summary>
        public PauseScreen(bool calledFromPostRace) { _fromPostRace = calledFromPostRace; }

        static bool Career => GameSession.Mode == GameMode.Career;
        static bool Racing => RaceManager.Active != null;
        static RaceEvent Event => RaceManager.Active?.Event;
        /// <summary>FEDatabase::IsDDay: the prologue (bin 16).</summary>
        static bool DDay => Career && (GameSession.Profile?.Bin ?? 0) >= 16;
        /// <summary>FEDatabase::IsFinalEpicChase [?]: the final pursuit (1.8.1).</summary>
        static bool FinalEpicChase => Event != null && Event.EventId == "1.8.1";
        static bool PursuitActive => Systems.Police != null && Systems.Police.InPursuit;

        protected override void Setup()
        {
            SetLanguageHash(Title, _fromPostRace ? 0x376EB982u : 0x6C839FBEu);
            SetupOptions();
            for (int i = 1; i <= 10; i++) { var n = Obj($"option_{i}"); if (n != null) _slots.Add(n); }
            _master = Obj(0xB04669E3);
            _sel = 0;
            _scroll = 0;
            Layout();
        }

        void Add(uint tex, uint name, Action react, bool locked = false) => _options.Add(new Option { Tex = tex, Name = name, React = react, Locked = locked });

        void ResumeOpt(uint name) => Add(0x12BB5EA2, name, () => { _selection = SelResume; SetScript(Handler, Forward); });
        void RestartOpt() => Add(0xB295A6B6, 0xF893AFA1, () => { _selection = SelRestart; Ask(MsgRestart, 0x4D3399A8); });
        void OptionsOpt() => Add(0x520DE4E3, 0x2B5A03A8, () => { _selection = SelOptions; SetScript(Handler, Forward); });
        void TuningOpt()
        {
            bool avail = false;
            var car = GameSession.ActiveCar;
            if (car != null) for (int i = 0; i < 7; i++) avail |= CustomTuningScreen.IsTuningAvailable(car, i);
            Add(0x483238FD, 0x6A3672A2, () =>
            {
                _selection = SelTuning;
                if (!avail)
                    DialogInterface.ShowOneButton(this, "InGameDialog.fng", DialogTitle.Alert, FEHash.LabelOk, MsgCancel, MsgCancel, true, Lang(0xA7EE8554));
                else SetScript(Handler, Forward);
            }, !avail);
        }

        /// <summary>pm_QuitRaceToFE: the confirmation text by context.</summary>
        void QuitToFEOpt(uint name) => Add(0x4C9E34E6, name, () =>
        {
            _selection = SelQuitToFE;
            uint text = !Career ? 0x1DB1CDE5u : DDay || FinalEpicChase ? 0xECD92696u : Racing ? 0xCDE4CAE8u : PursuitActive ? 0x15A1B5A9u : 0x6925D0BEu;
            Ask(MsgQuitToFE, text);
        });

        void QuitFreeRoamOpt() => Add(0x56FFBD2C, 0x9DC599B0, () => { _selection = SelQuitFreeRoam; Ask(MsgQuitFreeRoam, 0x9887EB98); });
        void QuitQuickRaceOpt() => Add(0x4C9E34E6, 0x4349998B, () => { _selection = SelQuitToFE; Ask(MsgQuitQuick, 0x1DB1CDE5); });
        void QuitMainMenuOpt() => Add(0x4C9E34E6, 0xE950B7AF, () => { _selection = SelQuitToFE; Ask(MsgQuitMain, 0xA2E9B449); });

        void Ask(uint ok, uint text) =>
            DialogInterface.ShowTwoButtons(this, "InGameDialog.fng", DialogTitle.Alert, FEHash.LabelOk, FEHash.LabelCancel, ok, MsgCancel, MsgCancel, true, 1, Lang(text));

        /// <summary>PauseMenu::SetupOptions (0x52B4D0).</summary>
        void SetupOptions()
        {
            bool challenge = GameSession.Mode == GameMode.ChallengeSeries;
            if (_fromPostRace)
            {
                SetInvisible(0x812A09D4);
                if (Career)
                {
                    RestartOpt();
                    if (DDay || FinalEpicChase) QuitToFEOpt(0x690E9B7C);
                    else { QuitToFEOpt(0x3C14C420); QuitFreeRoamOpt(); }
                }
                else
                {
                    RestartOpt();
                    if (challenge) QuitMainMenuOpt(); else QuitQuickRaceOpt();
                }
                return;
            }
            if (Career)
            {
                if (!Racing)
                {
                    if (DDay) { ResumeOpt(0x01BD185C); QuitToFEOpt(0x690E9B7C); OptionsOpt(); }
                    else if (FinalEpicChase) { ResumeOpt(0x01BD185C); QuitToFEOpt(0x690E9B7C); TuningOpt(); OptionsOpt(); }
                    else if (PursuitActive) { ResumeOpt(0x01BD185C); TuningOpt(); OptionsOpt(); }
                    else { ResumeOpt(0x01BD185C); QuitToFEOpt(0x3C14C420); TuningOpt(); OptionsOpt(); }
                }
                else
                {
                    bool epic = Event != null && Event.EpicPursuit;
                    if (DDay) { ResumeOpt(0xDED357E7); RestartOpt(); QuitToFEOpt(0x690E9B7C); OptionsOpt(); }
                    else if (FinalEpicChase || epic) { ResumeOpt(0xDED357E7); RestartOpt(); QuitToFEOpt(0x690E9B7C); TuningOpt(); OptionsOpt(); }
                    else { ResumeOpt(0xDED357E7); RestartOpt(); QuitToFEOpt(0x3C14C420); QuitFreeRoamOpt(); TuningOpt(); OptionsOpt(); }
                }
                return;
            }
            if (GameSession.SplitScreen)
            {
                ResumeOpt(0xDED357E7); RestartOpt(); QuitQuickRaceOpt(); OptionsOpt();
                return;
            }
            ResumeOpt(0xDED357E7);
            RestartOpt();
            if (challenge) QuitMainMenuOpt(); else QuitQuickRaceOpt();
            if (!(Event != null && Event.Type == RaceType.Tollbooth) && !challenge) TuningOpt();
            OptionsOpt();
        }

        // ---- icon scroller [approximated] ----------------------------------------------------------------------

        void Layout()
        {
            var center = _master != null ? new Vector2(_master.Data.X, _master.Data.Y) : new Vector2(116, 12);
            int first = _options.Count <= _slots.Count ? 0 : Mathf.RoundToInt(_scroll) - _slots.Count / 2;
            for (int j = 0; j < _slots.Count; j++)
            {
                var n = _slots[j];
                int i = first + j;
                if (i < 0 || i >= _options.Count) { View.SetVisible(n, false); continue; }
                var tex = FrontEndData.Texture(_options[i].Tex);
                if (tex != null && n.Image != null && n.Image.texture != tex) n.Image.texture = tex;
                float d = i - _scroll, ad = Mathf.Abs(d);
                View.SetVisible(n, ad < 2.6f && n.Image != null && n.Image.texture != null);
                n.Script = null;
                n.Data.X = center.x + d * 72f;
                n.Data.Y = center.y;
                float s = Mathf.Lerp(1.0f, 0.62f, Mathf.Clamp01(ad));
                n.Data.SizeX = n.Data.SizeY = 64 * s;
                // IconScrollerMenu idle colour 0xFFFFAE40, fade colour 0x00FFAE40 (PauseMenu ctor)
                n.Data.R = 255; n.Data.G = 174; n.Data.B = 64;
                n.Data.A = 255f * Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(ad)) * (_options[i].Locked ? 0.4f : 1f);
            }
            if (_options.Count > 0)
            {
                Printf(OptionTitle, Lang(_options[_sel].Name));
                int slot = _sel - first;
                if (slot >= 0 && slot < _slots.Count && View.CurrentButton != _slots[slot]) View.SetCurrentButton(_slots[slot], false);
            }
        }

        /// <summary>
        /// Back from options / tuning: the exe switches packages (QueuePackageSwitch back to Pause_Main); the left pause view
        /// has played its exit, so a fresh one takes its place.
        /// </summary>
        public override void Resume()
        {
            if (!_subScreen) return;   // a dialog closing: the pause menu stays as it is
            _subScreen = false;
            var fe = FE;
            bool post = _fromPostRace;
            fe.Defer(() => { if (fe.Top == this) fe.Replace(new PauseScreen(post)); });
        }

        protected override void Feed(MenuInput i)
        {
            // option_N are FF_DontNavigate buttons: left / right reach the package (game PAD_LEFT / PAD_RIGHT)
            base.Feed(i);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.ScreenTick:
                    _scroll = Mathf.MoveTowards(_scroll, _sel, Time.unscaledDeltaTime * 9f);
                    if (Mathf.Abs(_scroll - _sel) > _options.Count / 2f) _scroll = _sel;
                    Layout();
                    return;
                case FEHash.PadLeft:
                    if (_options.Count > 0) _sel = (_sel + _options.Count - 1) % _options.Count;
                    return;
                case FEHash.PadRight:
                    if (_options.Count > 0) _sel = (_sel + 1) % _options.Count;
                    return;
                case FEHash.ButtonPressed:
                    // IconOption::React (react immediately) on the selected option
                    if (_sel < _options.Count) _options[_sel].React?.Invoke();
                    return;
                case FEHash.PadBack:
                    if (!_fromPostRace) { _prevButton = FEHash.PadBack; SetScript(Handler, Forward); }
                    return;
                case FEHash.PadStart:
                    if (!_fromPostRace) { _selection = SelResume; SetScript(Handler, Forward); }
                    return;
                case MsgQuitToFE: case MsgQuitFreeRoam: case MsgQuitMain: case MsgUnknownReply: case MsgQuitQuick: case MsgRestart:
                    SetScript(Handler, Forward);
                    return;
                case MsgCancel:
                    View.PackageMessage(FEHash.EnableInput);
                    return;
                case FEHash.ExitComplete:
                    if (_prevButton == FEHash.PadBack) { FE.CloseAll(); return; }   // [?] back: the icon scroller leaves → unpause
                    switch (_selection)
                    {
                        case SelRestart:
                            PostRaceResultsScreen.Restart(Event);   // ERestartRace
                            break;
                        case SelResume:
                            FE.CloseAll();                          // EUnPause
                            break;
                        case SelQuitFreeRoam:
                            RaceManager.Active?.Abort();            // EUnPause + RaceAbandoned
                            FE.CloseAll();
                            break;
                        case SelOptions:
                            _subScreen = true;
                            FE.Push(new OptionsMainScreen(true));   // Pause_Main in options mode (its back pops to Pause_Main)
                            break;
                        case SelTuning:
                            _subScreen = true;
                            FE.Push(new CustomTuningScreen());      // Pause_Performance_Tuning.fng
                            break;
                        case SelQuitToFE:
                            if (Career) GameFlow.JumpToSafeHouse(); // EQuitToFE(GARAGETYPE_CAREER_SAFEHOUSE)
                            else GameFlow.QuitToMainMenu();         // EQuitToFE(GARAGETYPE_MAIN_FE)
                            break;
                        case SelQuitMainMenu:
                            GameFlow.QuitToMainMenu();
                            break;
                    }
                    return;
            }
        }
    }
}
