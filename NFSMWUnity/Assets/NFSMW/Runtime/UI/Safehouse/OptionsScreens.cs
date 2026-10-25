using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// UIOptionsMain on MainMenu_Sub (Safehouse/options/uiOptionsMain.cpp; PC Setup 0x528F30, NotificationMessage
    /// 0x545310): Audio, Video, Gameplay, Player, Controls, [Online only in online mode], and outside the pause menu
    /// EA Trax and Credits. Video opens OptionsPCDisplay.fng and Controls Options_PC_Controller.fng on PC (in the pause
    /// menu Pause_PCDisplayOptions / Pause_PC_Controller); the rest Options.fng. Back leaves to MainMenu.fng (Pause_Main
    /// from the pause menu), saving first when the options are dirty (ExitOptions → memory card op 0x400B3).
    /// </summary>
    public sealed class OptionsMainScreen : CarouselScreen
    {
        public override string Package => "MainMenu_Sub";
        /// <summary>Setup: TITLE_GROUP ← 0x4ECA678C; from the pause menu 0x863404B5 ← 0x1D7BB6C9 instead (see Open).</summary>
        protected override string Title => _fromPause ? null : FEDb.L(0x4ECA678Cu, "options");
        readonly bool _fromPause;

        public OptionsMainScreen(bool fromPause = false) { _fromPause = fromPause; }

        void Add(uint tex, uint name, OptionsCategory cat)
        {
            Options.Add(new Option
            {
                Label = FEDb.L(name), Icon = SafehouseUi.IconName(tex),
                Run = () => Choose(cat),
            });
        }

        /// <summary>UIOptionsMain ctor: the pause menu's icon colours 0xFFFFAE40 / 0x00FFAE40 (else white).</summary>
        public override void Open()
        {
            if (_fromPause) SetColors(0xFFFFAE40, 0x00FFAE40);
            base.Open();
            if (_fromPause) SafehouseUi.SetLang(View, 0x863404B5, FEDb.L(0x1D7BB6C9u, "options"));
        }

        protected override void Build()
        {
            FEDb.SetGameMode(FEGameMode.Options);
            Add(0xF37AF144, 0xE76CD783, OptionsCategory.Audio);
            Add(0x8A006328, 0xE8E24508, OptionsCategory.Video);
            Add(0x4DF98FB2, 0xD0CF6EE1, OptionsCategory.Gameplay);
            Add(0xD708EFEF, 0xF760EABE, OptionsCategory.Player);
            Add(0xA04A7B26, 0x04DC6DB5, OptionsCategory.Controls);
            if (FEDb.Is(FEGameMode.Online)) Add(0x5A5E5137, 0x7D347E05, OptionsCategory.Online);
            if (!_fromPause && !FEDb.Is(FEGameMode.Online))
            {
                Add(0xC52CCBF6, 0xDCFB6B36, OptionsCategory.EATrax);
                Add(0x51009E20, 0x0905101F, OptionsCategory.Credits);
            }
            Selected = FEDb.LastButton(Package + "/options");
        }

        void Choose(OptionsCategory cat)
        {
            FEDb.SetLastButton(Package + "/options", Selected);
            FEDb.Data.CurrentCategory = (int)cat;
            switch (cat)
            {
                case OptionsCategory.Audio:
                case OptionsCategory.Video:
                case OptionsCategory.Gameplay:
                case OptionsCategory.Player:
                case OptionsCategory.Online:
                    FE.Push(new OptionsScreen(cat, _fromPause));
                    break;
                // PC: Options_PC_Controller.fng (Pause_PC_Controller.fng), EA_Trax_Jukebox.fng, Credits.fng (package switches)
                case OptionsCategory.Controls: FE.Push(new ControllerScreen(_fromPause)); break;
                case OptionsCategory.EATrax: FE.Push(new JukeboxScreen()); break;
                case OptionsCategory.Credits: FE.Push(new CreditsScreen(false)); break;
            }
        }

        /// <summary>__PAD_BACK__ → EXIT_COMPLETE: ClearGameMode(OPTIONS), save when dirty, back to the main menu / pause.</summary>
        protected override void OnBack()
        {
            FEDb.ClearGameMode(FEGameMode.Options);
            if (FEDb.OptionsDirty) FEDb.Save();
            if (_fromPause) FE.Pop();                  // → Pause_Main
            else FE.Replace(new MainMenuScreen());     // ExitOptions("MainMenu.fng")
        }
    }

    /// <summary>
    /// UIOptionsScreen (Safehouse/options/uiOptionsScreen.cpp; PC 0x5102B0-0x54FE69) with the option widgets of
    /// uiOptionWidgets.cpp. Audio / Gameplay / Player on Options.fng, PC Video on OptionsPCDisplay.fng with a basic page
    /// (Level Of Detail 1..5, Resolution) and an advanced page (key 2, message 0x9CDD83CA). The page edits copies; Back
    /// with changes asks 0xE9CB802F and restores them; key 1 (PAD_BUTTON5) asks 0x8AEF5AE8 and restores the defaults; on
    /// the video page Enter applies the block (sub_510300) and asks to keep it within 10 s (0x07770CE7). Enter elsewhere
    /// leaves keeping the changes [?: the package's Event Handler forwards accept to the leave message].
    /// </summary>
    public sealed class OptionsScreen : WidgetMenuScreen
    {
        readonly OptionsCategory _cat;
        readonly bool _fromPause;
        bool _advanced;
        FEAudioSettings _origAudio;
        FEGameplaySettings _origGameplay;
        FEPlayerSettings _origPlayer;
        PcVideoSettings _work, _origVideo;
        int _workLevel, _origLevel;

        /// <summary>The page of the last category chosen (Audio on a fresh profile).</summary>
        public OptionsScreen() : this((OptionsCategory)FEDb.Data.CurrentCategory, false) { }

        public OptionsScreen(OptionsCategory cat, bool fromPause = false)
        {
            _cat = cat == OptionsCategory.PcAdvDisplay ? OptionsCategory.Video : cat;
            _fromPause = fromPause;
        }

        bool Video => _cat == OptionsCategory.Video;

        public override string Package => Video
            ? (_fromPause && FrontEndData.Packages.ContainsKey("Pause_PCDisplayOptions") ? "Pause_PCDisplayOptions" : "OptionsPCDisplay")
            : (_fromPause && FrontEndData.Packages.ContainsKey("Pause_Options") ? "Pause_Options" : "Options");

        public override void Open()
        {
            Settings.Load();
            FEDb.Load();
            MaxWidgetsOnScreen = _fromPause ? 10 : 9;
            InitWidgetMenu();
            var d = FEDb.Data;
            _origAudio = d.Audio.Clone();
            _origGameplay = d.Gameplay.Clone();
            _origPlayer = d.Player(FEDb.PlayerToEdit).Clone();
            _work = d.PcVideo.Clone();
            _origVideo = d.PcVideo.Clone();
            _workLevel = _origLevel = d.PerformanceLevel;
            // ctor (:49): the player page in split screen posts 0x7DB7B6D7 and names the player being edited
            if (_cat == OptionsCategory.Player && GameSession.SplitScreen) SafehouseUi.Message(View, 0x7DB7B6D7);
            Setup();
        }

        /// <summary>UIOptionsScreen::Setup (:139): hides the colour calibration and scroll arrows, builds the category.</summary>
        void Setup()
        {
            ClearWidgets();
            foreach (uint h in new uint[] { 0xE54C30BE, 0x8E1BEA84, 0x608BB8C8, 0x444969FD, 0x444969FE }) View.SetVisible(View.Find(h), false);
            View.SetVisible(View.Find(0xAD6B204F), false);   // brightness calibration group (console video only)
            switch (_cat)
            {
                case OptionsCategory.Audio: SetupAudio(); break;
                case OptionsCategory.Video: SetupVideo(); break;
                case OptionsCategory.Gameplay: SetupGameplay(); break;
                case OptionsCategory.Player: SetupPlayer(); break;
                default: Header(_fromPause ? 0x966C856Du : 0xE463B5F7u); break;
            }
            SetInitialOption(0);
        }

        void Header(uint hash)
        {
            foreach (var n in View.Nodes) if (n.Obj.Hash == 0x42ADB44C && n.Label != null) View.SetText(n, FEDb.L(hash));
        }

        void Icon(uint tex)
        {
            var t = FrontEndData.Texture(tex);
            if (t) View.SetTexture(View.Find(0x08007B4C), t);
        }

        static string OnOff(bool on) => FEDb.L(on ? 0x417B2604u : 0x70DFE5C2u);
        static int Wrap(int v, int dir, int min, int max) => v + dir < min ? max : v + dir > max ? min : v + dir;

        // ---- audio (SetupAudio 0x529310) ----------------------------------------------------------------------------
        void SetupAudio()
        {
            Icon(0xF37AF144);
            Header(_fromPause ? 0xB1426DFAu : 0x3932C2E4u);
            var a = FEDb.Data.Audio;
            AddSlider(0xFD487543, 0, 1, 0.1f, () => a.SoundEffectsVol, v => { a.SoundEffectsVol = v; FEDb.Apply(); });
            AddSlider(0x218E4B08, 0, 1, 0.1f, () => a.CarVol, v => { a.CarVol = v; FEDb.Apply(); });
            AddSlider(0x9E5FB82A, 0, 1, 0.1f, () => a.SpeechVol, v => { a.SpeechVol = v; FEDb.Apply(); });
            AddSlider(0x418E681D, 0, 1, 0.1f, () => a.FEMusicVol, v => { a.FEMusicVol = Mathf.Abs(v) < 0.001f ? 0f : v; FEDb.Apply(); });
            AddSlider(0xDF21EAC2, 0, 1, 0.1f, () => a.IGMusicVol, v => { a.IGMusicVol = v; FEDb.Apply(); });
            AddToggle(dir => { if (dir != 0) a.InteractiveMusicMode = a.InteractiveMusicMode == 0 ? 1 : 0; },
                      w => w.Set(0xA3DBB390, a.InteractiveMusicMode != 0 ? 0x417B2604u : 0x70DFE5C2u));
            AddToggle(dir => { if (dir != 0) a.EATraxMode = a.EATraxMode == 0 ? 1 : 0; },
                      w => w.Set(0xDCFB6B36, a.EATraxMode != 0 ? 0x417B2604u : 0x70DFE5C2u));
            if (!InGame)
                AddToggle(dir => a.AudioMode = Wrap(a.AudioMode, dir, 0, 2),
                          w => w.Set(0x2881AB87, a.AudioMode switch { 0 => 0xC50FA35Fu, 1 => 0x55DA8BF8u, _ => 0xF6FAFF24u }));
        }

        static bool InGame => NfsMw.GameBootstrap.Instance != null && NfsMw.GameBootstrap.Instance.Player != null;

        // ---- PC video (SetupVideo 0x5295F0) -------------------------------------------------------------------------
        void SetupVideo()
        {
            Icon(0x8A006328);
            Header(_fromPause ? 0xD94EA03Fu : 0x48478029u);
            var v = _work;
            var c = FEDb.Caps;
            if (!_advanced)
            {
                AddSlider(0x05A66554, 1, 5, 1, () => _workLevel, x => LevelChanged((int)x));
                AddResolution();
            }
            else
            {
                AddResolution();
                AddSlider(0xE7A04885, 0, c[PcVideoSettings.Fsaa], 1, () => v[PcVideoSettings.Fsaa], x => v[PcVideoSettings.Fsaa] = (int)x, c[PcVideoSettings.Fsaa] > 0);
                AddSlider(0xFD068897, 0, c[PcVideoSettings.TextureFiltering], 1, () => v[PcVideoSettings.TextureFiltering], x => v[PcVideoSettings.TextureFiltering] = (int)x, c[PcVideoSettings.TextureFiltering] > 0);
                AddFlag(0x6CEB9CB6, PcVideoSettings.VSync, true, true);
                AddSlider(0xA271A8E9, 0, c[PcVideoSettings.WorldLod], 1, () => v[PcVideoSettings.WorldLod], x => v[PcVideoSettings.WorldLod] = (int)x, c[PcVideoSettings.WorldLod] > 0);
                AddSlider(0x9032DF3F, 0, c[PcVideoSettings.RoadReflection], 1, () => v[PcVideoSettings.RoadReflection], x => v[PcVideoSettings.RoadReflection] = (int)x, c[PcVideoSettings.RoadReflection] > 0);
                AddSlider(0x1052ED5B, 0, c[PcVideoSettings.ShadowDetail], 1, () => v[PcVideoSettings.ShadowDetail], x => v[PcVideoSettings.ShadowDetail] = (int)x, c[PcVideoSettings.ShadowDetail] > 0);
                AddFlag(0x5F560D76, PcVideoSettings.CarLod, false, c[PcVideoSettings.CarLod] > 0);
                AddFlag(0xBDE7DACF, PcVideoSettings.CarEnvUpdate, false, c[PcVideoSettings.CarEnvMap] > 0);
                AddSlider(0xF2E2D84A, 0, c[PcVideoSettings.CarEnvMap], 1, () => v[PcVideoSettings.CarEnvMap], x => v[PcVideoSettings.CarEnvMap] = (int)x, c[PcVideoSettings.CarEnvUpdate] > 0);
                AddFlag(0xD41A8B13, PcVideoSettings.Rain, true, c[PcVideoSettings.Rain] > 0);
                AddFlag(0x70A7555E, PcVideoSettings.OverBright, true, c[PcVideoSettings.OverBright] > 0);
                AddFlag(0x1DE776AA, PcVideoSettings.VisualTreatment, false, c[PcVideoSettings.VisualTreatment] > 0);
            }
            // the "Advanced" / "Basic" button text (0xCE757C8A): 0x1459065C on the basic page, 0x9C88B5C8 on the advanced one
            View.SetText(View.Find(0xCE757C8A), FEDb.L(_advanced ? 0x9C88B5C8u : 0x1459065Cu, _advanced ? "Basic" : "Advanced"));
        }

        /// <summary>The On/Off (0x16FDEF36 / 0xF6BBD534) or High/Low (0xCE32B239 / 0xF6BBC9AB) toggles: field = !field.</summary>
        void AddFlag(uint title, int field, bool onOff, bool enabled)
        {
            AddToggle(dir => { if (dir != 0) _work[field] = _work[field] == 0 ? 1 : 0; },
                      w => w.Set(title, _work[field] != 0 ? (onOff ? 0x16FDEF36u : 0xCE32B239u) : (onOff ? 0xF6BBD534u : 0xF6BBC9ABu)), enabled);
        }

        /// <summary>VOScreenResolution (Act 0x50F970): steps through the six entries, skipping unavailable ones, wrapping.</summary>
        void AddResolution()
        {
            uint[] labels = { 0xDCCBD59E, 0x6D6BB416, 0xA7470774, 0x4EF21B32, 0x2D31055A, 0x76667B12 };
            AddToggle(dir =>
            {
                if (dir == 0) return;
                int r = _work[PcVideoSettings.Resolution];
                for (int guard = 0; guard < 6; guard++)
                {
                    r += dir;
                    if (r < 0) r = 5; else if (r >= 6) r = 0;
                    if (FEDb.ResolutionAvailable(r)) break;
                }
                _work[PcVideoSettings.Resolution] = r;
            }, w => w.Set(0xC96D57C9, labels[Mathf.Clamp(_work[PcVideoSettings.Resolution], 0, 5)]));
        }

        /// <summary>Message 0x026DED57 (0x54589A): a new level sets the racing resolution (level > 3 → 800x600) and redraws.</summary>
        void LevelChanged(int level)
        {
            if (level == _workLevel) return;
            _workLevel = level;
            _work[PcVideoSettings.Resolution] = level > 3 ? 1 : 0;
            RefreshWidgets();
        }

        // ---- gameplay (SetupGameplay 0x529C10) ----------------------------------------------------------------------
        void SetupGameplay()
        {
            Icon(0x4DF98FB2);
            Header(_fromPause ? 0x3936D9F8u : 0x01CCE8C2u);
            var g = FEDb.Data.Gameplay;
            if (ShouldShowAutoSave())
                AddToggle(dir => { if (dir != 0) g.AutoSaveOn = !g.AutoSaveOn; }, w => w.Set(0xD1056C88, g.AutoSaveOn ? 0x417B2604u : 0x70DFE5C2u));
            if (!GameSession.SplitScreen)
                AddToggle(dir => { if (dir != 0) g.JumpCam = !g.JumpCam; }, w => w.Set(0xF26A5CBF, g.JumpCam ? 0x417B2604u : 0x70DFE5C2u));
            AddToggle(dir => { if (dir != 0) g.Damage = !g.Damage; }, w => w.Set(0x1582ADFF, g.Damage ? 0x417B2604u : 0x70DFE5C2u));
            AddToggle(dir => { if (dir != 0) g.RearviewOn = !g.RearviewOn; }, w => w.Set(0x85A6CE05, g.RearviewOn ? 0x417B2604u : 0x70DFE5C2u));
            AddToggle(dir => { if (dir != 0) { g.SpeedoUnits = g.SpeedoUnits == 0 ? 1 : 0; FEDb.Apply(); } },
                      w => w.Set(0x01E19173, g.SpeedoUnits == 0 ? 0xFBD74FC5u : 0xAF70E736u));
            bool split = _fromPause && GameSession.SplitScreen;
            if (!split)
            {
                if (!FEDb.Is(FEGameMode.Online) && !FEDb.Is(FEGameMode.Lan))
                    AddToggle(dir => g.ExploringMiniMapMode = Wrap(g.ExploringMiniMapMode, dir == 0 ? 0 : dir, 0, 2), w => w.Set(0xC6269082, MiniMap(g.ExploringMiniMapMode)));
                AddToggle(dir => g.RacingMiniMapMode = Wrap(g.RacingMiniMapMode, dir == 0 ? 0 : dir, 0, 2), w => w.Set(0x9FA5EC9E, MiniMap(g.RacingMiniMapMode)));
            }
        }

        static uint MiniMap(int mode) => mode switch { 1 => 0xF4B00E99, 0 => 0xF75595F2, _ => 0x70DFE5C2 };

        /// <summary>ShouldShowAutoSave (:327): memory card + auto save + a loaded profile, not online, outside non-career races.</summary>
        static bool ShouldShowAutoSave()
        {
            var race = RaceManager.Active;
            bool raceOk = race == null || GameSession.Mode == GameMode.Career || GameSession.Mode == GameMode.ChallengeSeries;
            return raceOk && GameSession.Profile != null && !FEDb.Is(FEGameMode.Online);
        }

        // ---- player (SetupPlayer 0x529F10) --------------------------------------------------------------------------
        /// <summary>GetPlayerSettings(GetPlayerToEditForOptions()): the widgets read it on every Draw (TogglePlayer only redraws).</summary>
        static FEPlayerSettings P => FEDb.Data.Player(FEDb.PlayerToEdit);

        void SetupPlayer()
        {
            Icon(0xD708EFEF);
            Header(_fromPause ? 0xD9DC2F12u : 0xC055165Fu);
            View.Play(View.Find(0x8A41F5B9), FEView.Appear, true);
            // Setup (:184): the player page shows the scroll bar arrows (the L / R trigger player switch)
            View.SetVisible(View.Find(0x444969FD), true);
            View.SetVisible(View.Find(0x444969FE), true);
            View.SetText(View.TextOf(View.Find(0x53BF826D)), FEDb.L(FEDb.PlayerToEdit == 0 ? 0x7B070984u : 0x7B070985u));
            foreach (var n in View.Descendants(View.Find(0x53BF826D))) View.SetText(n, FEDb.L(FEDb.PlayerToEdit == 0 ? 0x7B070984u : 0x7B070985u));
            var race = RaceManager.Active;
            bool drag = race != null && race.Event.Type == RaceType.Drag;
            bool toll = race != null && race.Event.Type == RaceType.Tollbooth;
            if (!drag)
                AddToggle(dir => { if (dir != 0) P.Transmission = P.Transmission == 0 ? 1 : 0; }, w => w.Set(0xD31407E7, P.Transmission == 0 ? 0x8CD532A0u : 0x317D3005u));
            // PODriveCam (uiOptionWidgets.cpp:429): ScrollDriveCam over 0..6, skipping the cameras IsPlayerCameraSelectable rejects
            AddToggle(dir => FEDb.ScrollDriveCam(P, dir),
                      w => w.Set(0xF6CCDC5F, P.CurCam switch { 3 => 0x1EA4CEC2u, 2 => 0x5AE3441Fu, 1 => 0x414F19D7u, 4 => 0x916039B4u, _ => 0xC3E9AE58u }));
            AddToggle(dir => { if (dir != 0) P.GaugesOn = !P.GaugesOn; }, w => w.Set(0xAC148579, P.GaugesOn ? 0x417B2604u : 0x70DFE5C2u));
            AddToggle(dir => { if (dir != 0) P.PositionOn = !P.PositionOn; }, w => w.Set(0x82CD8F92, P.PositionOn ? 0x417B2604u : 0x70DFE5C2u));
            AddToggle(dir => { if (dir != 0) P.SplitTimeType = P.SplitTimeType == 0 ? 4 : 0; },
                      w => w.Set(0x084BC378, P.SplitTimeType switch { 0 => 0x417B2604u, 1 => 0xC44D3943u, 2 => 0x17FAFC32u, 3 => 0x1EA459F8u, _ => 0x70DFE5C2u }));
            AddToggle(dir => { if (dir != 0) P.ScoreOn = !P.ScoreOn; }, w => w.Set(0xC03F9F19, P.ScoreOn ? 0x417B2604u : 0x70DFE5C2u));
            if (!drag && !toll)
                AddToggle(dir => { if (dir != 0) P.LeaderboardOn = !P.LeaderboardOn; }, w => w.Set(0xC93FBFB5, P.LeaderboardOn ? 0x417B2604u : 0x70DFE5C2u));
        }

        /// <summary>
        /// PAD_LTRIGGER / PAD_RTRIGGER on the player page (:89): sound 0x6B283007 / 0xF4B32D4D; with changes the dialog 0xBA463431
        /// "Do you wish to keep Player %d's settings?" — Yes 0x9A5AD46D keeps (TogglePlayer(false)), No 0xA2A07AC4 reverts
        /// (TogglePlayer(true)) — else TogglePlayer(false) at once. Not limited to split screen in the exe.
        /// </summary>
        void PlayerTrigger(bool left)
        {
            FeSoundNotify(left ? 0x6B283007u : 0xF4B32D4Du);
            if (!Unchanged())
            {
                string blurb = CareerScreenKit.Format(FEDb.L(0xBA463431), FEDb.PlayerToEdit + 1);
                FEDialog.ShowTwoButtons(FE, FEDialog.Title.Alert, 0x70E01038, 0x417B25E4, () => TogglePlayer(false), () => TogglePlayer(true), () => { }, 1, blurb);
            }
            else TogglePlayer(false);
        }

        static void FeSoundNotify(uint msg) => NfsMw.Audio.FeSound.Notify(msg);

        /// <summary>TogglePlayer (:383): optional revert, the other player, its settings become the originals, 0x53BF826D, redraw.</summary>
        void TogglePlayer(bool revert)
        {
            if (revert) RestoreOriginals();
            FEDb.PlayerToEdit = FEDb.PlayerToEdit == 0 ? 1 : 0;
            if (_cat == OptionsCategory.Player)
            {
                _origPlayer = P.Clone();
                uint name = FEDb.PlayerToEdit == 0 ? 0x7B070984u : 0x7B070985u;
                View.SetText(View.TextOf(View.Find(0x53BF826D)), FEDb.L(name));
                foreach (var n in View.Descendants(View.Find(0x53BF826D))) View.SetText(n, FEDb.L(name));
            }
            RefreshWidgets();
        }

        // ---- state ----------------------------------------------------------------------------------------------------

        /// <summary>OptionsDidNotChange (0x510360); the video page also compares the level.</summary>
        bool Unchanged()
        {
            var d = FEDb.Data;
            return _cat switch
            {
                OptionsCategory.Audio => d.Audio.Same(_origAudio),
                OptionsCategory.Video => _work.Same(_origVideo) && _workLevel == _origLevel,
                OptionsCategory.Gameplay => d.Gameplay.Same(_origGameplay),
                OptionsCategory.Player => d.Player(FEDb.PlayerToEdit).Same(_origPlayer),
                _ => false,
            };
        }

        /// <summary>RestoreOriginals (0x510450).</summary>
        void RestoreOriginals()
        {
            var d = FEDb.Data;
            switch (_cat)
            {
                case OptionsCategory.Audio: d.Audio.CopyFrom(_origAudio); break;
                case OptionsCategory.Video: _work.CopyFrom(_origVideo); _workLevel = _origLevel; break;
                case OptionsCategory.Gameplay: d.Gameplay.CopyFrom(_origGameplay); break;
                case OptionsCategory.Player: d.Player(FEDb.PlayerToEdit).CopyFrom(_origPlayer); break;
            }
            FEDb.Apply();
        }

        /// <summary>RestoreDefaults (0x51D170).</summary>
        void RestoreDefaults()
        {
            var d = FEDb.Data;
            switch (_cat)
            {
                case OptionsCategory.Audio: d.Audio.Default(); break;
                case OptionsCategory.Video:
                    _workLevel = 3;   // [?] the PC default level comes from the card (first boot)
                    _work.ApplyPreset(_workLevel, FEDb.Caps);
                    break;
                case OptionsCategory.Gameplay:
                    bool autoSave = d.Gameplay.AutoSaveOn;
                    d.Gameplay.Default();
                    if (!ShouldShowAutoSave()) d.Gameplay.AutoSaveOn = autoSave;
                    break;
                case OptionsCategory.Player: d.Player(FEDb.PlayerToEdit).DefaultFromOptionsScreen(); break;
            }
            FEDb.Apply();
            RefreshWidgets();
        }

        /// <summary>sub_510300: the working block goes live; the basic page's level change runs the preset (sub_6C1B60).</summary>
        void ApplyVideo()
        {
            var d = FEDb.Data;
            d.PcVideo.CopyFrom(_work);
            if (!_advanced && _workLevel != _origLevel)
            {
                d.PerformanceLevel = _workLevel;
                d.PcVideo.ApplyPreset(_workLevel, FEDb.Caps);
                d.PcVideo[PcVideoSettings.Resolution] = _work[PcVideoSettings.Resolution];
                _work.CopyFrom(d.PcVideo);
            }
            FEDb.Apply(display: true);
        }

        /// <summary>EXIT_COMPLETE: the dirty flag, back to MainMenu_Sub (the options list) or Pause_Main.</summary>
        void Leave()
        {
            FEDb.OptionsDirty |= !Unchanged() || FEDb.OptionsDirty;
            if (_cat == OptionsCategory.Audio) FEDb.Apply();
            FEDb.Save();
            FE.Pop();
        }

        public override void Tick(MenuInput input)
        {
            var keys = PcKeys.Read();
            TickWidgets(input);
            if (_cat == OptionsCategory.Player && (keys.PageLeft || keys.PageRight)) { PlayerTrigger(keys.PageLeft); return; }
            if (Video && keys.Key2)
            {
                _advanced = !_advanced;   // 0x9CDD83CA → toggle +0x154 and Setup
                Setup();
                return;
            }
            if (input.Key1)
            {
                FEDialog.ShowYesNo(FE, FEDb.L(0x8AEF5AE8), RestoreDefaults);
                return;
            }
            if (input.Accept)
            {
                if (Video)
                {
                    if (Unchanged()) { Leave(); return; }
                    ApplyVideo();
                    var keep = _work.Clone();
                    FEDialog.ShowTwoButtonCountdown(FE, 0x70E01038, 0x417B25E4,
                        () => { _origVideo = keep; _origLevel = FEDb.Data.PerformanceLevel; Leave(); },   // 0x4D0D83B9
                        () => { RestoreOriginals(); FEDb.Data.PcVideo.CopyFrom(_origVideo); FEDb.Data.PerformanceLevel = _origLevel; FEDb.Apply(display: true); RefreshWidgets(); },   // 0x85347AE1
                        10f, FEDb.L(0x07770CE7));
                    return;
                }
                Leave();
                return;
            }
            if (input.Back)
            {
                if (Unchanged()) { FE.Pop(); return; }
                FEDialog.ShowYesNo(FE, FEDb.L(0xE9CB802F), () => { RestoreOriginals(); FE.Pop(); });
            }
        }
    }
}
