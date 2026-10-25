using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>
    /// FEGameWonScreen (Career/FEGameWonScreen.cpp; PC ctor 0x532B90, NotificationMessage 0x549380,
    /// QueuePackageSwitchForNextScreen 0x532DD0, Initialize = sub_511FE0 → mCurrentScreen 0x91CA54 = 0). One class for four
    /// packages (factory 0x8F44C8..0x8F44E0, CreateGameWonScreen 0x5715C0): GameWon.fng → Credits.fng (uiCredits in beat-game
    /// mode; its EXIT_COMPLETE also calls QueuePackageSwitchForNextScreen, 0x5332BB) → RapSheetLogin_ENDGAME.fng →
    /// RapSheetLogin2_ENDGAME.fng → RapSheetMain_ENDGAME.fng → MainMenu_Sub.fng (the career crib: FEGameMode still has the
    /// career bit). The packages run themselves (GameWon: the EVENT HANDLER's FORWARD pages the three texts, LEAVE ends
    /// in EXIT_COMPLETE); the code only fills the strings:
    /// <list type="bullet">
    /// <item>screen 3 (RapSheetLogin2_ENDGAME): 0x03CC94D6 = "&gt; %s" profile name;</item>
    /// <item>screen 4 (RapSheetMain_ENDGAME): 0x1232703A case file (0xE21D083C), 0xE3DA78E7 / 0x22F33E0A name (0x6031106E),
    /// 0xE3DA78E8 bounty (0x364E4525, GetTotalBounty), E9 cost to state (0xA355FEDD, career pursuit score PD_COST_TO_STATE 7),
    /// EA impounded cars (0xB1E58DB1), EB unserved fines (0x79FB7D16, GetTotalFines(true)), EC evaded (0x463B461B), ED busted
    /// (0xC5094459), EE cars with a record (0x6DEE0C7A).</item>
    /// </list>
    /// The values are the stable totals (RapStable / FEImpound) and the career cost-to-state score, as <see cref="RapSheetScreen"/>.
    /// </summary>
    public sealed class GameWonScreen : FEMenuScreen
    {
        /// <summary>FEGameWonScreen::mCurrentScreen (0x91CA54).</summary>
        public static int CurrentScreen;
        static readonly string[] Packages = { "GameWon", "Credits", "RapSheetLogin_ENDGAME", "RapSheetLogin2_ENDGAME", "RapSheetMain_ENDGAME" };
        readonly int _screen;

        public override string Package => Packages[Mathf.Clamp(_screen, 0, Packages.Length - 1)];

        public GameWonScreen() { _screen = CurrentScreen; }

        /// <summary>FEGameWonScreen::Initialize (sub_511FE0).</summary>
        public static void Initialize() => CurrentScreen = 0;

        /// <summary>
        /// EQuitToFE's game-won branch (0x6240C2-0x624139): in the world (GameFlow state 6), the current race is the final epic
        /// chase (IsFinalEpicChase 0x56DC00: event "1.8.1") and its score flag 2 (won) is set → SetPlayerHasBeatenTheGame,
        /// FEGameMode = Career | BeatGame (0x40000), garage type 2, Initialize, next package GameWon.fng.
        /// [hook: the epic pursuit's raceover / quit-to-FE owner calls this instead of the usual quit when 1.8.1 is won.]
        /// </summary>
        public static void Begin()
        {
            if (GameSession.Profile != null) { GameSession.Profile.BeatenTheGame = true; GameSession.Profile.Save(); }   // CareerSettings 0x4000
            FEDb.ResetGameMode();
            FEDb.SetGameMode(FEGameMode.Career | FEGameMode.BeatGame);
            Initialize();
            GameFlow.QuitToMainMenu();
            var fe = FrontEnd.Create();
            fe.CloseAll();
            fe.Push(new GameWonScreen());
        }

        /// <summary>IsFinalEpicChase (0x56DC00) for an event id.</summary>
        public static bool IsFinalEpicChase(string eventId) => eventId == RaceIndex.FinalEpicChase;

        protected override void Setup()
        {
            var p = GameSession.Profile;
            string name = p?.Name ?? "";
            switch (_screen)
            {
                case 3:
                    Printf(0x03CC94D6, "> " + name);
                    break;
                case 4:
                {
                    var r = p == null ? new RapSheetRecord() : p.RapSheet ??= new RapSheetRecord();
                    Printf(0x1232703A, K.Format(Lang(0xE21D083C), K.CaseFile()));
                    Printf(0xE3DA78E7, K.Format(Lang(0x6031106E), name));
                    Printf(0x22F33E0A, K.Format(Lang(0x6031106E), name));
                    Printf(0xE3DA78E8, K.Format(Lang(0x364E4525), RapStable.TotalBounty()));
                    Printf(0xE3DA78E9, K.Format(Lang(0xA355FEDD), r.CareerScore(RapSheetRecord.PD_CostToState)));
                    Printf(0xE3DA78EA, K.Format(Lang(0xB1E58DB1), RapStable.NumImpoundedCars()));
                    Printf(0xE3DA78EB, K.Format(Lang(0x79FB7D16), RapStable.TotalFines(true)));
                    Printf(0xE3DA78EC, K.Format(Lang(0x463B461B), RapStable.TotalEvadedPursuits()));
                    Printf(0xE3DA78ED, K.Format(Lang(0xC5094459), RapStable.TotalBustedPursuits()));
                    Printf(0xE3DA78EE, K.Format(Lang(0x6DEE0C7A), RapStable.NumCareerCarsWithARecord()));
                    break;
                }
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg == FEHash.ExitComplete) QueuePackageSwitchForNextScreen(FE, this);
        }

        /// <summary>QueuePackageSwitchForNextScreen (0x532DD0): ++mCurrentScreen; 1 Credits, 2 RapSheetLogin_ENDGAME,
        /// 3 RapSheetLogin2_ENDGAME, 4 RapSheetMain_ENDGAME, 5 MainMenu_Sub; anything later does nothing.</summary>
        public static void QueuePackageSwitchForNextScreen(FrontEnd fe, FEScreen from)
        {
            CurrentScreen++;
            int next = CurrentScreen;
            if (fe == null || next < 1 || next > 5) return;
            fe.Defer(() =>
            {
                if (fe.Top != from) return;
                if (next == 1) fe.Replace(new CreditsScreen(true));
                else if (next <= 4) fe.Replace(new GameWonScreen());
                else fe.Replace(new CareerMenuScreen());
            });
        }
    }
}
