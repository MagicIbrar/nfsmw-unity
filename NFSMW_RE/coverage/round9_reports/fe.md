| source file | new status | unity files | note |
|---|---|---|---|
| Frontend/Localization/Localize.cpp | Ported | Core/LangLocalize.cs, UI/FrontEndData.cs, FEMenuScreen.cs | language table 0x8F40F8, registry 0x6CB680/0x8FB578, percent 0x57E680, speedo 0x57E6B0, bPrintf locale |
| Frontend/Localization/WideCharHistogram.cpp | Ported | Core/LangLocalize.cs | Unpack PC 0x56BFC0, Pack 0x56BE80 |
| Frontend/MenuScreens/Career/FEGameWonScreen.cpp | Ported | Safehouse/GameWonScreen.cs, CreditsScreen.cs | 0x532B90/0x549380/0x532DD0; endgame chain |
| Frontend/MenuScreens/ControllerUnplugged.cpp | Ported | InGame/ControllerUnpluggedScreen.cs | label 0x54EEFEC2, CheckUnplugged 0x559280 |
| Frontend/RaceStarter.cpp | Ported | GameFlow | SkipFE paths dead; SetupOnlineRace online-only |
| Frontend/MenuScreens/InGame/CustomTuning.cpp | Ported | UI/TuningScreen.cs, Safehouse/CSlider.cs, TextScroller.cs | FEng engine; limits ±1 (0x8A9350) |
| Frontend/MenuScreens/Common/Slider.cpp | Ported | Safehouse/CSlider.cs | cSlider 1:1 |
| Frontend/MenuScreens/Common/feWidget.cpp | Ported | TextScroller.cs, FEScrollBar.cs, WidgetMenu.cs | ToggleImageWidget dead; Input/Date online-only |
| Frontend/MenuScreens/Common/FEMenuScreen.cpp | Partial 0.9 | InGame/FEMenuScreen.cs | BaseNotifySound 0x5724D0; inline PC text input not ported |
| Frontend/MenuScreens/Common/feUIWidgetMenu.cpp | Ported | WidgetMenu.cs | Done-button branches dead |
| Frontend/MenuScreens/Safehouse/options/uiOptionsScreen.cpp | Ported | OptionsScreens.cs | dialog 0xBA463431 |
| Frontend/MenuScreens/Safehouse/options/uiOptionWidgets.cpp | Ported | OptionsScreens.cs | console-only widgets N/A |
| Frontend/MenuScreens/Safehouse/options/uiOptionsMain.cpp | Ported | OptionsScreens.cs | |
| Frontend/MenuScreens/Safehouse/options/uiEATraxJukebox.cpp | Ported | JukeboxScreen.cs | |
| Frontend/MenuScreens/Safehouse/options/uiCredits.cpp | Ported | CreditsScreen.cs | |
| Frontend/MenuScreens/Safehouse/options/uiOptionsTrailers.cpp | N/A | | React 0x50F120 unreferenced |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRTrackSelect.cpp | Ported | QuickRaceScreens.cs | |
| Frontend/MenuScreens/Safehouse/quickrace/uiTrackMapStreamer.cpp | Ported | QuickRaceScreens.cs | |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRTrackOptions.cpp | Ported | QuickRaceScreens.cs | direction dead |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRBrief.cpp | Ported | QuickRaceScreens.cs | |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRModeSelect.cpp | Ported | MainMenuScreens.cs | |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRMainMenu.cpp | Ported | MainMenuScreens.cs | |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRChallengeSeries.cpp | Ported | ChallengeSeriesScreen.cs | |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRPressStart.cpp | Ported | MainMenuScreens.cs | PC text 0x63F6A01C |
| Frontend/MenuScreens/Safehouse/quickrace/uiShowcase.cpp | Ported | ShowcaseScreen.cs | |
| Frontend/MenuScreens/Safehouse/uiMain.cpp | Ported | MainMenuScreens.cs | |
| Frontend/MenuScreens/Safehouse/FEPkg_GarageMain.cpp | Partial 0.95 | CarShowroom.cs, Camera/SelectCarCamera.cs | GarageCarLoader, fades not ported |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetMain.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetLogin.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetRS.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetUS.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetTEP.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetPD.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetRankings.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetRankingsDetail.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetVD.cpp | Ported | RapSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRapSheetCTS.cpp | Partial 0.9 | RapSheetScreens.cs | pursuit counters missing |
| Frontend/MenuScreens/Safehouse/career/uiInfractions.cpp | Ported | RapSheetScreens.cs | exe fine keys |
| Frontend/MenuScreens/Safehouse/career/uiRepSheetMain.cpp | Partial 0.9 | RepSheetScreens.cs | idle colour [?] |
| Frontend/MenuScreens/Safehouse/career/uiRepSheetMilestones.cpp | Partial 0.9 | RepSheetScreens.cs | goal ×546.117 [?] |
| Frontend/MenuScreens/Safehouse/career/uiRepSheetRivalBio.cpp | Ported | RepSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRepSheetBounty.cpp | Ported | RepSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiMarkerSelect.cpp | Ported | RepSheetScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiCareerMain.cpp | Ported | MainMenuScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiCareerManager.cpp | Ported | MainMenuScreens.cs | |
| Frontend/MenuScreens/Safehouse/career/uiRepSheetRaceEvents.cpp | Ported | Safehouse/RaceListScreen.cs | 3x3 FEng 0x55A760 |
| Frontend/MenuScreens/Safehouse/career/uiRepSheetRival.cpp | Ported | InGame/RivalScreen.cs | |
| Frontend/MenuScreens/InGame/uiSMS.cpp | Ported | SmsScreens.cs | FEng engine rewrite |
| Frontend/MenuScreens/InGame/uiSMSMessage.cpp | Ported | SmsScreens.cs | audio hooks |
| Frontend/MenuScreens/Common/feDialogBox.cpp | Ported | Dialogs.cs, FEDialog.cs | |
| Frontend/MenuScreens/Common/feScrollerina.cpp | Ported | JukeboxScreen.cs | |
| Frontend/MenuScreens/Common/feArrayScrollerMenu.cpp | Partial 0.9 | CustomizeExe, SmsScreens, ControllerScreen | scroll-bar update missing |
| Frontend/MenuScreens/InGame/FEpkg_MU_Keyboard.cpp | N/A | | never pushed on PC |
| Frontend/MenuScreens/Loading/FELoadingTips.cpp | Ported | Safehouse/LoadingScreens.cs | 0x62D570, 0x58EE30/0x56AEC0 |
| Frontend/MenuScreens/Loading/FELoadingScreen.cpp | Ported | Safehouse/LoadingScreens.cs | sound states |
| Frontend/MenuScreens/Loading/FELoadingControllerScreen.cpp | N/A | LoadingScreens.cs | no ctor |
| Frontend/MenuScreens/Loading/FEBootFlowManager.cpp | Ported | Safehouse/BootFlow.cs | |
| Frontend/MenuScreens/Loading/FEMovieScreen.cpp | Ported | Game/Media.cs | |
| Frontend/MenuScreens/Loading/FESplashScreen.cpp | Ported | MainMenuScreens.cs | |
| Frontend/MenuScreens/Loading/FELanguageSelect.cpp | N/A | | ShowLanguageSelect 0x64A140 returns 0 |
| Frontend/MoviePlayer/MoviePlayer.cpp | Ported | Game/Media.cs | allocator/RCMP/Joylog N/A |
| Frontend/MenuScreens/InGame/InGameTutorialScreen.cpp | Ported | TutorialScreen.cs | |
| Frontend/MenuScreens/Common/FEAnyMovieScreen.cpp | Ported | Media.cs | |
| Frontend/MenuScreens/InGame/FeFadeScreen.cpp | Partial 0.8 | | controller-error suppression only |
| Frontend/MenuScreens/Safehouse/customize/FECustomize.cpp | Ported | CarSelectScreens.cs | |
| Frontend/Database/FEDatabase.cpp | Partial 0.95 | Core/FEDatabase.cs, FEDb.cs | CareerSettings flags not in Profile |
| Frontend/Database/VehicleDB.cpp | Ported | FEImpound.cs, Core/FEDatabase.cs | stable totals |
| Frontend/Careers/UnlockSystem.cpp | Ported | Shop.cs, ShopRules.cs, CustomizeExe.cs | ClearAllNewStatus |
| Frontend/Database/RaceDB.cpp | Partial 0.9 | FEDb.cs, RapSheetScreens.cs | CST counters missing |
| Frontend/Database/uiProfileManager.cpp | Ported | Safehouse/ProfileManagerScreen.cs | |
| Frontend/MenuScreens/Safehouse/customize/MyCarsManager.cpp | Ported | CarSelectScreens.cs | |
