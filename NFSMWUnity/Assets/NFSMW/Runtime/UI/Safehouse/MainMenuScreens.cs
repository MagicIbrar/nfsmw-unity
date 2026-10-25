using System;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// An IconScrollerMenu screen (FEIconScrollerMenu.hpp) built the original way: AddOption(IconOption(tex_hash,
    /// name_hash, desc_hash)) in order, SetInitialOption(FEngGetLastButton(package)), the title by language hash.
    /// </summary>
    public abstract class IconMenuScreen : CarouselScreen
    {
        protected virtual uint TitleHash => 0;
        protected override string Title => TitleHash != 0 ? FEDb.L(TitleHash) : null;
        string LastKey => Package + "/" + GetType().Name;

        protected void AddOption(uint tex, uint name, Action run, Func<bool> enabled = null)
        {
            Options.Add(new Option
            {
                Label = FEDb.L(name), Icon = SafehouseUi.IconName(tex),
                Run = () => { FEDb.SetLastButton(LastKey, Selected); run?.Invoke(); },
                Enabled = enabled ?? (() => true),
            });
        }

        protected sealed override void Build()
        {
            Setup();
            Selected = FEDb.LastButton(LastKey);
        }

        protected abstract void Setup();

        public override void Open()
        {
            base.Open();
            // RefreshHeader: TITLE 0x03C458C1 and TITLESHADOW 0xB5C74226 (and the TITLE_GROUP 0xB71B576D strings)
            if (TitleHash != 0)
                foreach (uint h in new uint[] { 0x03C458C1, 0xB5C74226, 0xB71B576D }) SafehouseUi.SetLang(View, h, Title);
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The boot splash, MW_LS_Splash (Loading/FESplashScreen.cpp; PC create 0x5A4710): HD group hidden, the licence blurb
    /// 0x9B580A55, Enter / Start continue once INIT completed (BootFlowManager → MainMenu.fng); 30 s without input plays
    /// the attract movie (MW_LS_AttractFMV [?: the movie is not in the PC install]).
    /// With a player number this is uiQRPressStart on PressStart.fng instead (quickrace/uiQRPressStart.cpp): "Player %d
    /// press START." (0xCF538E1C) for the split screen car selection; Back goes to Car_Select of player 1 or the track
    /// screens, Enter continues to Car_Select of this player.
    /// </summary>
    public sealed class PressStartScreen : FEScreen
    {
        readonly int _player = -1;
        float _idle;
        public PressStartScreen() { }
        public PressStartScreen(int player) { _player = player; }

        public override string Package => _player >= 0 || !FrontEndData.Packages.ContainsKey("MW_LS_Splash") ? "PressStart" : "MW_LS_Splash";

        public override void Open()
        {
            FEDb.Load();
            FEDb.Apply();
            if (_player < 0) NfsMw.Controls.EasterEggs.Activated += OnEasterEgg;   // EasterEggs::Activate runs while the splash is up (0x5A3030)
            if (_player < 0)
            {
                SafehouseUi.Visible(View, 0x534CC377, false);   // HD group
                SafehouseUi.Visible(View, 0x13CF446D, false);   // startclick
                SafehouseUi.Visible(View, 0x8C0BD743, false);   // mouseclick
                return;
            }
            // uiQRPressStart ctor (PC 0x7AE580): only 0xB244CF71 = GetLocalizedString(0x63F6A01C) "Player %d press the START
            // button." with player + 1 (the decomp's 0xCF538E1C and the FONT_MW_BODY 0xA065EFFE print are the GC build)
            string fmt = FEDb.L(0x63F6A01C, "Player %d press the START button.");
            SafehouseUi.SetAll(View, 0xB244CF71, fmt.Replace("%d", (_player + 1).ToString()));
        }

        public override void Close()
        {
            if (_player < 0) NfsMw.Controls.EasterEggs.Activated -= OnEasterEgg;
        }

        /// <summary>
        /// SplashScreen::NotificationMessage (Loading/FESplashScreen.cpp:98): the game messages ActivateEasterEgg queues the
        /// first time a code is typed — FEHASH_BK_UNLOCK 0x98257537 → alert 0x53F13FD1 ("A special race event has just been
        /// added to your Challenge Series"), FEHASH_CASTROL_UNLOCK 0x6521E5C2 → alert 0x6521E5C2 (the Castrol Ford GT),
        /// FEHASH_DEMO_CHEAT 0xA6813B08 → alert 0xA1161AAF (the demo marker); OK 0x417B2601, cancel 0x1FAB5998. No package
        /// responds to these messages (MW_LS_Splash / PressStart / MainMenu have no MsgR for them): the dialog is the whole FE effect.
        /// </summary>
        void OnEasterEgg(NfsMw.Controls.EasterEggs.Special type, bool first)
        {
            if (!first || FE == null || FE.Top != this) return;
            uint blurb = type switch
            {
                NfsMw.Controls.EasterEggs.Special.BurgerKing => 0x53f13fd1u,
                NfsMw.Controls.EasterEggs.Special.Castrol => 0x6521e5c2u,
                NfsMw.Controls.EasterEggs.Special.DemoCheat => 0xa1161aafu,
                _ => 0u,
            };
            if (blurb != 0) FEDialog.ShowOneButton(FE, FEDialog.Title.Alert, 0x417b2601, FEDb.L(blurb));
        }

        public override void Tick(MenuInput input)
        {
            if (_player < 0)
            {
                if (input.Any) _idle = 0; else _idle += Time.unscaledDeltaTime;
                // SplashScreen::NotificationMessage (0x5A32A0): accept / start → ChangeToNextBootFlowScreen (MC_Main → MainMenu)
                if (input.Accept || input.Back)
                {
                    if (BootFlow.Active) BootFlow.Next();
                    else FE.Replace(new MainMenuScreen());
                    return;
                }
                // SCREEN_TICK: SplashScreenMovieTimeout (30 s) without input → DoAttract; none in the list → never again
                if (_idle > BootFlow.SplashScreenMovieTimeout && !BootFlow.DoAttract()) _idle = float.NegativeInfinity;
                return;
            }
            if (input.Accept) { FE.Replace(new CarSelectScreen(CarSelectScreen.Mode.QuickRace, _player)); return; }
            if (input.Back)
            {
                if (_player == 1) FE.Replace(new CarSelectScreen(CarSelectScreen.Mode.QuickRace, 0));
                // PC 0x7A4033: split screen (quick race mode, two players) and RaceMode == Drag only → Track_Select, else Track_Options
                else if (FEDb.IsSplitScreen && FEDb.RaceMode == GRaceType.Drag) FE.Replace(new TrackSelectScreen());
                else FE.Replace(new TrackOptionsScreen());
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// UIMain on MainMenu.fng (Safehouse/uiMain.cpp; PC Setup 0x544E50, NotificationMessage 0x559FC0). PC options in
    /// order: Career, Challenge Series, Quick Race, My Cars, LAN Play, Online, Alias Manager (IsMemcardEnabled = 1 on PC,
    /// 0x8F8708), Options. Career → MainMenu_Sub as the career manager (memory card build), Challenge → ChallengeSeries,
    /// Quick Race / Options → MainMenu_Sub, My Cars → MyCarsManager, LAN → UI_PC_LAN_ServerSelect, Online → the EA login,
    /// Alias Manager → MC_ProfileManager. Key 1 (PAD_BUTTON5) toggles the game stats (GAMESTATS_APPEAR / GAMESTATS_LEAVE)
    /// while a profile is loaded; Q quits (PC). UpdateProfileData (0x528BC0) fills the stats.
    /// </summary>
    public sealed class MainMenuScreen : IconMenuScreen
    {
        public override string Package => "MainMenu";
        protected override uint TitleHash => 0xB24AAE58;
        bool _stats;

        protected override void Setup()
        {
            FEDb.ResetGameMode();
            FEDb.NumPlayers = 1;
            AddOption(0x03704F3D, 0x5815A2B5, () => { FEDb.SetGameMode(FEGameMode.CareerManager); FE.Replace(new CareerManagerScreen()); });
            AddOption(0x9A962438, 0xCC8CB746, () => { FEDb.SetGameMode(FEGameMode.Challenge); FE.Replace(new ChallengeSeriesScreen()); });   // QueuePackageSwitch("ChallengeSeries.fng")
            AddOption(0x4E6FBB02, 0x54020A7A, () => { FEDb.SetGameMode(FEGameMode.QuickRace); FE.Replace(new QuickRaceMenuScreen()); });
            AddOption(0xB0C46023, 0x1AFD5BE6, () => { FEDb.SetGameMode(FEGameMode.Customize); FE.Replace(new MyCarsScreen()); });
            AddOption(0xD13A2E66, 0xD6DB74DE, () => { FEDb.SetGameMode(FEGameMode.Lan); FE.Push(new LanMenuScreen()); });
            AddOption(0x771849A5, 0x74FB90A8, () => FEDialog.ShowOk(FE, FEDialog.Title.Info, FEDb.L(0x74FB90A8) + "\n\n(EA online is not available in this port)"));
            // MainProfileManager (IsMemcardEnabled = 1 on PC): EXIT_COMPLETE in profile manager mode → MC_ProfileManager.fng (0x55A170)
            AddOption(0x6B303856, 0xBCB18F38, () => { FEDb.SetGameMode(FEGameMode.ProfileManager); FE.Replace(new ProfileManagerScreen()); });
            AddOption(0x3058FE37, 0x19A8C0AF, () => { FEDb.SetGameMode(FEGameMode.Options); FE.Replace(new OptionsMainScreen()); });
        }

        static bool ProfileLoaded => GameSession.Profile != null && GameSession.Profile.CareerStarted;

        public override void Open()
        {
            FEDb.Load();
            base.Open();
            GameSession.SplitScreen = false;
            GameSession.EndLan();
            UpdateProfileData();
        }

        public override void Resume()
        {
            base.Resume();
            UpdateProfileData();
        }

        /// <summary>UIMain::UpdateProfileData (uiMain.cpp:143).</summary>
        void UpdateProfileData()
        {
            if (ProfileLoaded)
            {
                var s = FEDb.CompletionStats();
                string pct = Localize.GetLocalizedPercentSign();   // " %" for Danish / Finnish / French / German / Swedish (uiMain.cpp:155)
                SetText("GAME_COMPLETED_DATA", $"{s.Overall}{pct}");
                SetText("CAREER_COMPLETED_DATA", $"{s.Career}{pct}");
                View.SetVisible(View.Find("RAP_SHEET_ITEMS_DATA_1"), false);
                SetText("RAP_SHEET_ITEMS_DATA_2", $"{s.RapSheetRankings}{pct}");
                SetText("CHALLENGE_SERIES_DATA_1", $"{s.CompletedChallengeRaces}/{s.TotalChallengeRaces}");
                SetText("CHALLENGE_SERIES_DATA_2", $"{s.Challenge}{pct}");
                SafehouseUi.SetLang(View, 0xB514E2D8, GameSession.Profile.Name);
                View.SetVisible(View.Find("NAME_GROUP"), true);
                View.SetVisible(View.Find("GAME STATS GROUP"), true);
            }
            else
            {
                View.SetVisible(View.Find("NAME_GROUP"), false);
                View.SetVisible(View.Find("GAME STATS GROUP"), false);
            }
        }

        public override void Tick(MenuInput input)
        {
            base.Tick(input);
            if (FE.Top != this) return;
            if (input.Key1 && ProfileLoaded)
            {
                SafehouseUi.Message(View, _stats ? "GAMESTATS_LEAVE" : "GAMESTATS_APPEAR");
                _stats = !_stats;
            }
            if (PcKeys.Read().Q)
                FEDialog.ShowYesNo(FE, "Quit Need for Speed Most Wanted?", GameFlow.Quit);   // [ours: PC quit prompt text]
        }

        protected override void OnBack() { }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// uiCareerManager on MainMenu_Sub (career/uiCareerManager.cpp; PC Setup 0x532890): with a career started Resume
    /// Career (unless game over) and Start New Career, else Start Career; then Load. Title 0x8FFF61F2 "career main menu".
    /// Back → MainMenu.fng (ClearGameMode CAREER_MANAGER). Resume → the safehouse (IG_SafehouseMain / uiCareerCrib);
    /// New over an existing career → the memory card overwrite question (op 0x80063) first.
    /// </summary>
    public sealed class CareerManagerScreen : IconMenuScreen
    {
        public override string Package => "MainMenu_Sub";
        protected override uint TitleHash => 0x8FFF61F2;

        static bool Started => GameSession.Profile != null && GameSession.Profile.CareerStarted && GameSession.Profile.Cars.Count > 0;

        protected override void Setup()
        {
            FEDb.SetGameMode(FEGameMode.CareerManager);
            if (Started)
            {
                AddOption(0xC1C089CE, 0xE072DB21, Resume);
                AddOption(0xE7353BE7, 0x17E18F87, () => FEDialog.ShowYesNo(FE, FEDb.L(0x17E18F87) + "?\n" + FEDb.L(0x6005281E), NewCareer));   // [?: memcard 0x80063 text]
            }
            else AddOption(0xE7353BE7, 0x6005281E, NewCareer);
            AddOption(0x2287E063, 0x0018ECFF, Load);
        }

        public override void Open()
        {
            base.Open();
            if (GameSession.Profile != null)
            {
                View.Play(View.Find(0xC87422F7), FEView.Show, true);
                SafehouseUi.SetLang(View, 0xEB406FEC, GameSession.Profile.Name);
            }
        }

        static string Alias() => GameSession.Profile != null && !string.IsNullOrEmpty(GameSession.Profile.Name) && GameSession.Profile.Name != "Player"
            ? GameSession.Profile.Name : Environment.UserName.Length > 0 ? Environment.UserName : "Player";

        /// <summary>CResumeCareer::React: ResumeCareer, then the safehouse (FE crib) when the world is up, else into the career.</summary>
        void Resume()
        {
            FEDb.ClearGameMode(FEGameMode.CareerManager);
            FEDb.SetGameMode(FEGameMode.Career);
            bool world = NfsMw.GameBootstrap.Instance != null && NfsMw.GameBootstrap.Instance.Player != null && GameSession.Mode == GameMode.Career;
            if (world) FE.Replace(new CareerMenuScreen());
            else GameFlow.StartCareer();
        }

        /// <summary>CStartNewCareer::React → CareerSettings::StartNewCareer (FED:330).</summary>
        void NewCareer()
        {
            FEDb.ClearGameMode(FEGameMode.CareerManager);
            FEDb.SetGameMode(FEGameMode.Career);
            Story.NewCareer(Alias());
        }

        /// <summary>CLoadCareer: the memory card load (op 0x413) [ours: rereads the save file], then refresh (0x7E998E5E).</summary>
        void Load()
        {
            GameSession.Profile = Profile.Load();
            GameSession.PrepareProfile();
            FEDb.Reload();
            FE.Replace(new CareerManagerScreen());
        }

        protected override void OnBack()
        {
            FEDb.ClearGameMode(FEGameMode.CareerManager);
            bool world = NfsMw.GameBootstrap.Instance != null && NfsMw.GameBootstrap.Instance.Player != null;
            if (world) GameFlow.QuitToMainMenu();
            else FE.Replace(new MainMenuScreen());
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// uiCareerCrib, the safehouse menu on MainMenu_Sub (career/uiCareerMain.cpp; PC Setup 0x532220, reacts
    /// 0x532080-0x5321C0): Resume Free Roam (asks 0xEB694C0C), Blacklist (→ SafehouseReputationOverview), Car Select
    /// (→ Car_Select in career mode), Rap Sheet (once the career has one → RapSheetLogin), Save (memory card, PC has it).
    /// Title 0xE596C4A3 "safe house"; PLAYER_MASTER SHOW, CAREER_DATA = career completion %, BOUNTY_DATA = total
    /// bounty, CASH_DATA = cash. Back → the career manager (memory card build) with the garage back to the main FE.
    /// </summary>
    public sealed class CareerMenuScreen : IconMenuScreen
    {
        public override string Package => "MainMenu_Sub";
        protected override uint TitleHash => 0xE596C4A3;
        public override bool ShowCar => true;
        public override string Platform => "CAREER_SAFEHOUSE.BIN";

        protected override void Setup()
        {
            FEDb.SetGameMode(FEGameMode.Career);
            AddOption(0x12BB5EA2, 0x01BD185C, () => FEDialog.ShowYesNo(FE, FEDb.L(0xEB694C0C), ResumeFreeRoam));
            AddOption(0x2C14AC23, 0x80B9FF9B, () => FE.Push(new RepSheetMainScreen()));
            AddOption(0xC6A1A6E0, 0x00D5F627, () => FE.Push(new CarSelectScreen(CarSelectScreen.Mode.Career)));
            // CRapSheet::React (PC 0x5320E0): RapSheetLogin.fng (the decomp's GC build goes straight to RapSheetMain)
            if (HasRapSheet) AddOption(0x2FD8B206, 0xAC22F27E, () => FE.Push(new RapSheetLoginScreen(0)));
            AddOption(0x228B7E32, 0x001C8ACE, Save);   // the Blacklist option's name is the string 0x80B9FF9B as is ("Blacklist 15")
        }

        /// <summary>CareerSettings flag 0x10 (HasRapSheet): set by the post-rival flow when bin 13 is reached (uiRepSheetRivalFlow
        /// stage 6, RivalFlow) or Lua Game.SetHasRapSheet (0x605670). [ours] saves made before the flag existed: Blacklist 13 or
        /// lower counts as having it.</summary>
        static bool HasRapSheet => GameSession.Profile != null && ((GameSession.Profile.RapSheet?.HasRapSheet ?? false) || GameSession.Profile.Bin <= 13);

        public override void Open()
        {
            base.Open();
            Refresh();
        }

        protected override void Refresh()
        {
            var p = GameSession.Profile;
            if (p == null) return;
            var car = p.Car;
            if (car != null) FE.Showroom.Show(car.Car, car.Paint, car);
            View.Play(View.Find("PLAYER_MASTER"), FEView.Show, true);
            SetText("CAREER_DATA", $"{FEDb.CompletionStats().Career}%");
            SetText("BOUNTY_DATA", SafehouseUi.Dollars(p.Bounty));
            SetText("CASH_DATA", SafehouseUi.Dollars(p.Cash));
            View.SetText(View.TextOf(View.Find("PLAYER_MASTER")), p.Name);
        }

        /// <summary>The dialog's yes (uiCareerMain.cpp:44): RaceStarter::StartCareerFreeRoam.</summary>
        void ResumeFreeRoam()
        {
            GameSession.Profile?.Save();
            bool world = NfsMw.GameBootstrap.Instance != null && NfsMw.GameBootstrap.Instance.Player != null;
            if (world) { GameFlow.RespawnPlayerCar(); FE.CloseAll(); }
            else GameFlow.StartCareer();
        }

        /// <summary>CSave: the memory card save (op 0x2251) [ours: the JSON saves, then a notice].</summary>
        void Save()
        {
            GameSession.Profile?.Save();
            FEDb.Save();
            FEDialog.ShowOk(FE, FEDialog.Title.Info, FEDb.L(0x001C8ACE) + ": " + GameSession.Profile?.Name);
        }

        protected override void OnBack()
        {
            FEDb.ClearGameMode(FEGameMode.Career);
            FEDb.SetGameMode(FEGameMode.CareerManager);
            FE.Replace(new CareerManagerScreen());
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// UIQRMainMenu on MainMenu_Sub (quickrace/uiQRMainMenu.cpp; PC 0x7AA730): Quick Play (→ Quick_Race_Brief), Custom
    /// Race (→ mode select), Split Screen (→ mode select with iNumPlayers 2). Title 0xCDF0CC03. Back → MainMenu.fng.
    /// </summary>
    public sealed class QuickRaceMenuScreen : IconMenuScreen
    {
        public override string Package => "MainMenu_Sub";
        protected override uint TitleHash => 0xCDF0CC03;

        protected override void Setup()
        {
            FEDb.SetGameMode(FEGameMode.QuickRace);
            AddOption(0xE6313967, 0xB5E8F82F, () => { FEDb.NumPlayers = 1; FE.Replace(new QuickRaceBriefScreen()); });
            AddOption(0x2A49B5E2, 0x25BBD4C3, () => { FEDb.NumPlayers = 1; FEDb.SetGameMode(FEGameMode.ModeSelect); FE.Replace(new ModeSelectScreen()); });
            AddOption(0xF365B5F5, 0x841D518A, () => { FEDb.NumPlayers = 2; FEDb.SetGameMode(FEGameMode.ModeSelect); FE.Replace(new ModeSelectScreen()); });
        }

        protected override void OnBack()
        {
            FEDb.ClearGameMode(FEGameMode.QuickRace);
            FE.Replace(new MainMenuScreen());
        }
    }

    /// <summary>
    /// UIQRModeSelect on MainMenu_Sub (quickrace/uiQRModeSelect.cpp): Circuit, Sprint, Drag, then (not online / LAN)
    /// Lap Knockout (not in split screen) and Speedtrap. Picking sets RaceMode and goes to Track_Select. Title
    /// 0x1F203817 / split screen 0x43C825ED / online 0x6703B807. Back → MainMenu_Sub (quick race menu).
    /// </summary>
    public sealed class ModeSelectScreen : IconMenuScreen
    {
        public override string Package => "MainMenu_Sub";
        protected override uint TitleHash => FEDb.Is(FEGameMode.Online) || FEDb.Is(FEGameMode.Lan) ? 0x6703B807u : FEDb.IsSplitScreen ? 0x43C825EDu : 0x1F203817u;

        void Mode(uint tex, uint name, GRaceType t) => AddOption(tex, name, () => { FEDb.RaceMode = t; FE.Replace(new TrackSelectScreen()); });

        protected override void Setup()
        {
            Mode(0xE9638D3E, 0x034FA2C1, GRaceType.Circuit);
            Mode(0x2521E5EB, 0xB94FD70E, GRaceType.P2P);
            Mode(0xAAAB31E9, 0x6F547E4C, GRaceType.Drag);
            if (!FEDb.Is(FEGameMode.Online) && !FEDb.Is(FEGameMode.Lan))
            {
                if (!FEDb.IsSplitScreen) Mode(0x3A015595, 0x4930F5FC, GRaceType.Knockout);
                Mode(0x66C9A7B6, 0xEE1EDC76, GRaceType.SpeedTrap);
            }
        }

        protected override void OnBack()
        {
            FEDb.ClearGameMode(FEGameMode.ModeSelect);
            if (FEDb.Is(FEGameMode.Lan)) { FE.Pop(); return; }
            FE.Replace(new QuickRaceMenuScreen());
        }
    }
}
