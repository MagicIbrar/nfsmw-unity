using System.Collections.Generic;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// The loading screens (MenuScreens/Loading/FELoadingScreen.cpp, FELoadingTips.cpp; PC ELoadingScreenOn 0x62D570,
    /// LoadingScreen ctor 0x58EF50 / dtor 0x57E050, LoadingTips ctor 0x5A3400, NotificationMessage 0x57DF10, ShowTipInfo
    /// 0x57DFA0, WhatTipScreenShouldIUseToday 0x597CF0, GetARandomTipScreen 0x56ADA0, GetGameTip 0x56AFB0). All packages
    /// live in GLOBAL/THINSCREEN_GLOBAL.BUN. ELoadingScreenOn pushes exactly one package (the port matches): GAME_TIP_NONE
    /// (28) gives Loading.fng (blinker 0x06D91704), GAME_TIP_USE_CONTROLLER_CONFIG (27) Loading_Controller.fng (no factory
    /// row on PC and no LoadingControllerScreen constructor in the exe: the package runs on its own), else Loading_Tips.fng
    /// with the tip (mDoneLoading = 0). Loading_Tips shows GetGameTip(tip): 0 &lt; tip &lt; 27 that row, else row 16 (so
    /// GAME_TIP_LAST_CAR_AND_2_STRIKES = 0 shows TIP_HIDING_SPOTS, sic); texture FEngHashString(name), &lt;name&gt;_DESC
    /// 0xC5FBC710, &lt;name&gt;_HEADER 0x0D555245, APPEAR on 0x3248E720. A tip with GFT_WAIT_FOR_BUTTON_PRESS (0x400) holds the
    /// game flow (DelayWaitForLoadingScreen 0x659710 waits on mDoneShowingLoadingTips): once loading is done and 5 s have
    /// passed, AllowInput posts 0x9938A38F (the press-accept prompt) and accept releases it.
    /// </summary>
    public static class LoadingFlow
    {
        public enum Direction { LoadingFE = 0, LoadingGameFromFE = 1 }
        public const int TipLastCarAnd2Strikes = 0, TipIntro = 1, TipMapIntro = 2, TipUseControllerConfig = 27, TipNone = 28;

        /// <summary>GameTipInfoTable (speed.exe 0x8F3D78, 16 bytes each: name, eGameTipBins, eGameTipType, eGameTipFlags).</summary>
        static readonly (string name, uint bin, uint cat, uint flags)[] Tips =
        {
            ("TIP_STRIKEOUT", 0, 0, 0x400), ("TIP_ENGAGE_MECHANIC", 0, 0, 0x400), ("TIP_WORLDMAP", 0, 0, 0x400),
            ("TIP_CUSTOMIZATION_SHOPS", 0xD800, 0x10, 0x200), ("TIP_CARLOTS", 0x2400, 0x10, 0x200), ("TIP_ENGAGE_MECHANIC", 0xE000, 0x10, 0x200),
            ("TIP_SMS", 0xE200, 0x10, 0x200), ("TIP_UNLOCKS", 0x4800, 0x10, 0x200), ("TIP_SPEEDBREAKER", 0x1440, 0x10, 0x200),
            ("TIP_ROADBLOCKS", 0x5540, 0x10, 0x200), ("TIP_SUVRAM", 0xA80, 0x10, 0x200), ("TIP_CROSS", 0x2A, 0x10, 0x200),
            ("TIP_HELICOPTER", 0x150, 0x10, 0x200), ("TIP_ROLLING_ROADBLOCK", 0xA800, 0x10, 0x200), ("TIP_BOX_FORMATION", 0x5400, 0x10, 0x200),
            ("TIP_PURSUIT_BREAKERS", 0xE490, 0x10, 0x200), ("TIP_HIDING_SPOTS", 0xE924, 0x10, 0x200), ("TIP_BUSTED", 0x1210, 0x10, 0x200),
            ("TIP_WORLDMAP", 0x5400, 0x10, 0x200), ("TIP_RIVAL_CHALLENGES", 0xC000, 0x10, 0x200), ("TIP_MARKER_REWARDS", 0x2A00, 0x10, 0x200),
            ("TIP_BLACKLIST", 0x108A, 0x10, 0x200), ("TIP_BOUNTY", 0x4A0, 0x10, 0x200), ("TIP_CHALLENGE_SERIES", 0x96, 0x10, 0x200),
            ("TIP_RAP_SHEET", 0x328, 0x10, 0x200), ("TIP_SAFEHOUSE", 0xE000, 0x10, 0x200), ("TIP_STRIKEOUT", 0x1F80, 0x10, 0x200),
            ("GAME_TIP_USE_CONTROLLER_CONFIG", 0xFFFFFFFF, 0xFFFF, 0x200),
        };

        static readonly System.Random Rng = new System.Random();

        /// <summary>GRaceDatabase::GetStartupRace() == null ("lolley says this means free roam").</summary>
        static bool FreeRoam => RaceManager.Active == null;

        /// <summary>GetARandomTipScreen (0x56ADA0): bin = 1 &lt;&lt; current bin once the career started (else bins 13-16 when
        /// loading the game from the FE), type general | free roam, flags transition-to-game | all race types or transition-to-FE;
        /// a random match among the 28 table rows (controller config included), else GAME_TIP_NONE.</summary>
        static int GetARandomTipScreen(Direction dir)
        {
            var p = GameSession.Profile;
            uint bin = 0, type = 1, flags = 0;
            if (p != null && p.CareerStarted) bin = 1u << Mathf.Clamp(p.Bin, 0, 31);
            else if (dir == Direction.LoadingGameFromFE) bin = (1u << 13) | (1u << 14) | (1u << 15) | (1u << 16);
            if (FreeRoam) type |= 0x10;
            flags = dir == Direction.LoadingGameFromFE ? 0x200u | 0xFu : 0x100u;
            var valid = new List<int>();
            for (int i = 0; i < Tips.Length; i++)
                if ((Tips[i].bin & bin) != 0 && (Tips[i].cat & type) != 0 && (Tips[i].flags & flags) != 0) valid.Add(i);
            return valid.Count == 0 ? TipNone : valid[Rng.Next(valid.Count)];
        }

        static bool CareerMode => FEDb.Is(Core.FEGameMode.Career) || GameSession.Mode == GameMode.Career;
        /// <summary>CareerSettings::HasDoneCareerIntro (flag 0x20) [?: the port's story stage past the prologue / car lot / first
        /// safehouse visit].</summary>
        static bool HasDoneCareerIntro => GameSession.Profile != null && GameSession.Profile.Stage == Story.Free;

        /// <summary>TipTestLastCarWithTwoStrikes (0x58EE30): career, free roam, loading the game; one available career car whose
        /// record has TimesBusted == MaxBusted - 1.</summary>
        static bool TipTestLastCarWithTwoStrikes(Direction dir)
        {
            if (!CareerMode || !FreeRoam || dir != Direction.LoadingGameFromFE) return false;
            if (FEImpound.NumAvailableCareerCars() != 1) return false;
            var car = GameSession.Profile?.Car;
            var rec = car != null ? FEImpound.Record(car, false) : null;
            return rec != null && rec.TimesBusted == car.MaxBusted - 1;
        }

        /// <summary>WhatTipScreenShouldIUseToday's inlined TipTestFirstTimeIntoSafeHouse (0x597D06): career, loading the FE, the
        /// career intro not done, post-rival mode.</summary>
        static bool TipTestFirstTimeIntoSafeHouse(Direction dir) =>
            CareerMode && dir == Direction.LoadingFE && !HasDoneCareerIntro && GameSession.Profile != null && GameSession.Profile.PostRivalMode;

        /// <summary>TipTestFirstTimeOutOfSafeHouse (0x56AEC0): career, free roam, loading the game, intro done and the
        /// map-loading-tip flag (0x80000) clear: set it [ours: kept in the slot's FE data].</summary>
        static bool TipTestFirstTimeOutOfSafeHouse(Direction dir)
        {
            if (!CareerMode || !FreeRoam || dir != Direction.LoadingGameFromFE || !HasDoneCareerIntro) return false;
            var prof = GameSession.Profile;   // CareerSettings flag 0x80000
            if (prof == null || prof.HasDoneMapLoadingTip) return false;
            prof.HasDoneMapLoadingTip = true;
            prof.Save();
            return true;
        }

        /// <summary>WhatTipScreenShouldIUseToday (0x597CF0).</summary>
        public static int ChooseTip(Direction dir)
        {
            if (TipTestLastCarWithTwoStrikes(dir)) return TipLastCarAnd2Strikes;
            if (TipTestFirstTimeIntoSafeHouse(dir)) return TipIntro;
            if (TipTestFirstTimeOutOfSafeHouse(dir)) return TipMapIntro;
            return GetARandomTipScreen(dir);
        }

        /// <summary>GetGameTip (0x56AFB0): 0 &lt; tip &lt; 27 gives that row, else row 16.</summary>
        static int GameTipRow(int tip) => tip > 0 && tip < 27 ? tip : 16;

        static int _tip = TipNone;
        static bool _doneLoading, _doneShowing = true, _pressShown;
        static float _displayTime = float.MaxValue;
        static (FrontEnd fe, FEScreen screen) _hold;

        /// <summary>mDoneShowingLoadingTips: false while a wait-for-button tip is up and not yet accepted.</summary>
        public static bool DoneShowingLoadingTips => _doneShowing;

        /// <summary>ELoadingScreenOn (0x62D570): the package for the next loading screen (picks the tip once per load).</summary>
        public static string Package(Direction dir = Direction.LoadingGameFromFE)
        {
            FEDb.EnsureLoadingPackages();
            _tip = ChooseTip(dir);
            _doneLoading = false;
            _pressShown = false;
            _displayTime = float.MaxValue;
            _doneShowing = true;
            if (_tip == TipNone) return Pick("Loading");
            if (_tip == TipUseControllerConfig) return Pick("Loading_Controller");
            // LoadingTips ctor (0x5A3400): mDoneShowingLoadingTips = !(flags & GFT_WAIT_FOR_BUTTON_PRESS)
            _doneShowing = (Tips[GameTipRow(_tip)].flags & 0x400) == 0;
            return Pick("Loading_Tips");
        }

        static string Pick(string pkg)
        {
            if (FrontEndData.Packages.ContainsKey(pkg)) return pkg;
            _doneShowing = true;
            if (FrontEndData.Packages.ContainsKey("Loading")) return "Loading";
            return FrontEndData.Packages.ContainsKey("MW_LS_Splash") ? "MW_LS_Splash" : "PressStart";
        }

        /// <summary>
        /// The loading has finished (mDoneLoading): true when the loading screen may go now; false while a wait-for-button tip
        /// holds it (DelayWaitForLoadingScreen) - the sim is held (RequestPauseSimulation "Loading_Tips.fng") and the screen
        /// pops itself on accept.
        /// </summary>
        public static bool FinishLoading(FrontEnd fe, FEScreen screen)
        {
            _doneLoading = true;
            if (Application.isBatchMode) _doneShowing = true;   // [ours] automated runs (tests, QA tour) press nothing
            if (_doneShowing) return true;
            GameFlow.RequestPauseSimulation("Loading_Tips.fng");
            _hold = (fe, screen);
            return false;
        }

        /// <summary>LoadingTips::NotificationMessage (0x57DF10): SCREEN_TICK, after loading and 5 s, AllowInput on a wait tip;
        /// accept / BUTTON_PRESSED on a wait tip ends the showing.</summary>
        public static void Tick(FEView view, MenuInput input)
        {
            if (_tip == TipNone || _tip == TipUseControllerConfig) return;
            if ((Tips[GameTipRow(_tip)].flags & 0x400) == 0) return;
            if (_pressShown && input.Accept && !_doneShowing)
            {
                _doneShowing = true;
                GameFlow.RequestUnPauseSimulation("Loading_Tips.fng");
                var (fe, screen) = _hold;
                _hold = default;
                if (fe != null && fe.Top == screen) fe.Pop();
                return;
            }
            if (_doneLoading && !_pressShown && Time.realtimeSinceStartup - _displayTime > 5f)
            {
                _pressShown = true;   // AllowInput (0x56AF70): 0x9938A38F to the package
                if (view != null) SafehouseUi.Message(view, 0x9938A38F);
            }
        }

        /// <summary>LoadingScreen dtor (0x57E0BB): both sound states off again.</summary>
        public static void Close()
        {
            NfsMw.Audio.SoundState.SetGameFlow(NfsMw.Audio.SoundState.FlowLoadGeneral, false);
            NfsMw.Audio.SoundState.Set(false, NfsMw.Audio.SndCtl.Off);
        }

        /// <summary>LoadingScreen ctor (blinker on) / LoadingTips::ShowTipInfo (:68).</summary>
        public static void Open(FEView view)
        {
            // LoadingScreen ctor (0x58F019): ESndGameState(10, on) + sound control state SNDSTATE_OFF on
            NfsMw.Audio.SoundState.SetGameFlow(NfsMw.Audio.SoundState.FlowLoadGeneral, true);
            NfsMw.Audio.SoundState.Set(true, NfsMw.Audio.SndCtl.Off);
            if (view == null) return;
            view.SetVisible(view.Find(0x06D91704), true);
            if (_tip == TipNone || _tip == TipUseControllerConfig || view.Package == null || !FrontEndData.Packages.TryGetValue("Loading_Tips", out var tipsPkg) || view.Package != tipsPkg) return;
            var (name, _, _, _) = Tips[GameTipRow(_tip)];
            SafehouseUi.SetLang(view, 0xC5FBC710, FEDb.L(FrontEndData.Hash(name + "_DESC"), ""));
            SafehouseUi.SetLang(view, 0x0D555245, FEDb.L(FrontEndData.Hash(name + "_HEADER"), ""));
            var tex = FrontEndData.Texture(FrontEndData.Hash(name));   // eLoadStreamingTexture [?: streamed tip images may be absent]
            var img = view.Find(0xC9D77CB6);
            if (tex) view.SetTexture(img, tex); else view.SetVisible(img, false);
            view.Play(view.Find(0x3248E720), FEView.Appear, true);
            _displayTime = Time.realtimeSinceStartup;   // ShowTipInfo: DisplayTime = RealTimer
        }
    }
}
