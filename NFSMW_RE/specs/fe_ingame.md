# In-game and common front-end screens (NFS MW 2005 PC v1.3)

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/FEng/` and `Frontend/` (GameCube build: where the PC exe differs,
the exe wins and the difference is noted), `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json` (addresses below), speed.exe
disassembly (scratchpad `dx.py`), and the packages themselves (`feig` probe in `NFSMWStaging/Harness/ProbeFeInGame.cs`:
`feig tree <pkg>` dumps flags, scripts with events, and full response command lists; `feig pckeys`, `feig lang <hash>`,
`feig tex <bundle>`, `feig chunks <pkg> [tag]`). Companion specs: `hud.md`, `fe_career_screens.md`, `world_interaction.md`.

Port: `NFSMWUnity/Assets/NFSMW/Runtime/UI/InGame/*.cs` (+ the engine additions in `UI/FEView.cs`, `UI/FrontEnd.cs`,
`Core/FEng.cs`). Tags: `[?]` guess, `[ours]` our own addition, otherwise from the decomp / exe.

## 1. The FEng message engine (shared infrastructure)

Most of what a MW screen "does" is data in its package: scripts emit events, objects and the package answer messages
with command lists, and the C++ screen only sees the messages that reach the game. The port now runs this for screens
that opt in (`FEScreen.ExeEngine`; all `FEMenuScreen`s do). Plain views are unchanged.

### 1.1 Package data (Core/FEng.cs, additive)
* **MsgR / PkgR** (`FEPackageReader::ReadMessageResponseTags`, :891): tags `Mi` message id, `MC` count, then per command
  `Ri` id, `Ru` uint param, `Rs` string param, `Rt` target. Commands (`FEMessageResponseCommands`): 0 SetScript, 1 post
  to FEng (target object GUID, 0 = broadcast, 0xFFFFFFFC = package, 0xFFFFFFFF = game), 2 post to game, 3 post to
  sound, 256 SetActiveButton, 257 SetInputProcessing, 258/259 record / recall button, 260 DontNavigate, 512-516
  switch / push / pop package, 768/769 if script ==/!=, 1280 else, 1281 endif. The old reader kept only the `Ru` values;
  `FEObject.MessageResponses`, `FEPackage.PackageResponses` and `FEPackage.MessageTargets` (the `Targ` chunk: `Tc`
  count, `Mt` = message id + target GUIDs) are new.
* **Object flags** (`FEObject.h`): bit 0 = invisible (FEngSetInvisible / FEGameInterface::RenderObject checks
  `Flags & 1`), 0x8 = never made visible by code, 0x10 = tinted by game mode, 0x40 = console-only,
  FF_IsButton 0x10000000, FF_IgnoreButton 0x04000000, FF_DontNavigate 0x80000, FF_MouseObject 0x20000. `FEObject.Hidden`
  is bit 0 now (it was FF_IsButton | FF_HideInEdit); see §10.1 for the per-object rule.
* **PC load pass** (speed.exe 0x525520, the package-loaded callback): `FEngHidePCObjects` (0x516230) — on PC it hides
  every **0x40** object recursively and clears its FF_IsButton (the GC decomp version hides 0x8 objects instead) — then
  `FEngTransferFlagsToChildren(4)` (0x516270). This is what removes the console button glyph groups on PC; engine views
  do it instead of the old `HideConsoleButtons` heuristic.

### 1.2 Messages (`FEngine::ProcessMessageQueue`, FEngine.cpp:971)
Target 0 = every package: package responses, then the objects in that package's target list for the message;
0xFFFFFFFE every package's own responses; 0xFFFFFFFD the sending package's responses; 0xFFFFFFFC the sending package
(responses + target list); 0xFFFFFFFF = the game (`MenuScreen::NotificationMessage(msg, obj)`); 0xFFFFFFFB = sound;
0xFFFFFFFA = input enable / disable [?: hashes not known]; anything else = one object's responses.
`ProcessResponses` (:1116) runs the command list in order; If/Else/EndIf use FindConditionBranchTarget.
Port: `FEView.QueueMessage / PackageMessage / ProcessMessages` (InGame/FEngEngine.cs); broadcasts reach every live
engine view.

### 1.3 Scripts (`FEPackage::UpdateObject`, FEPackage.cpp:259; `IssueScriptMessages`, :185)
Per frame the current script's time advances by the frame's ms. At the end: chain to `Sc` (events of the old script to
its end, then the chained one's from 0 with the overflow), loop when `Flags & 3 == 1` (the only other value the
packages use; 2306 of 48382 scripts loop), else stop at Length + 1 (the script stays *set*; a finished script's tracks
are not re-applied, so code-set values survive). Events fire for `from <= t < to` (`to` + 1 at the script end); event
0x1B3909AA = set current button. Tracks: the colour track first, the others only while alpha > 0
(`UpdateObjectTracks`). Port: `FEView.EngineUpdate` / `StepScript`. **Plain views now loop flag-1 scripts too**
(previously every script stopped after one pass, so e.g. the HUD Warning / PULSE flashes ran once).
`FEngIsScriptSet` = the current script (also after it finished) → `FEView.IsScriptSet`, which for plain views uses the new
`Node.LastScriptId`; `FEngIsScriptRunning` → `IsScriptRunning`.

### 1.4 Buttons and pad input (`FEngine::ProcessPadsForPackage`, :440-690; `FEButtonMap::GetButtonFrom`)
* Accept: BUTTON_PRESSED (0x0C407210) to the current button if it has a response for it, else PAD_ACCEPT to the
  package (only if the package responds). Back / start / triggers / PAD_BUTTON0..9: to the current button if it
  responds, else to the package. Directions: the message to the current button (if it responds) or the package, and
  unless FF_DontNavigate (or the response has DontNavigate) the button map picks the next button: score =
  (1 − cos²)·200 + distance for directions within 60°, best < 1500. Wrap mode 0 [?: the package wrap flag is not read].
* SetCurrentButton sends UNHIGHLIGHT 0x55D1E635 / HIGHLIGHT 0xABC08912 to the old / new button (and to sound).
* Mouse (`UpdateMouseState`, :700): mouse objects get 0x13F4BD45 on enter, 0xB30D0683 while over, 0xB30793C1 on leaving,
  0xF459B307 left press over it, 0x1E646B2E held, 0x7EABCA56 release over it (= click; the PC key buttons' click
  responses post the pad message, e.g. PC_key_1 → PAD_BUTTON5). Hit test [?]: the object's rect, a group = any child.
* PC keys (from the PC key glyph buttons' click responses, `feig pckeys`): Enter = accept, Esc = back,
  **1 = PAD_BUTTON5, 2 = PAD_BUTTON4**, 4 = screen-specific. L / R triggers [?]: PageUp / PageDown and the pad
  shoulders. `MenuInput` gained Key2, Key4, LTrigger, RTrigger (additive).
* Input processing (MR_SetInputProcessing): the packages switch input off while their handler scripts play (INIT
  `13FD3296` → off, the `call` script's `8CB81F09` / `C6341FF6` at 500 ms → on), so a screen ignores keys for its first
  0.5 s and while leaving, exactly as in the exe.

### 1.5 MenuScreen (FEMenuScreen.cpp) → `FEMenuScreen`
`Setup()` = the original constructor; `NotificationMessage(msg, obj)` gets the game messages; `FEHash.ScreenTick`
(FEMSG_SCREEN_TICK) every frame; `QueueGameMessage` (cFEng::QueueGameMessage to this package), `QueuePackagePop`.
Helpers mirror FEngInterface: SetScript, IsScriptSet, Printf (FEPrintf; a group prints into all its strings),
SetLanguageHash, Set(In)Visible (recursive; SetVisible skips flag-8 objects), SetTextureHash.
`FEMenuScreen.InGame` (= TheGameFlowManager.IsInGame) [ours]: the city is loaded **and** no front-end screen
(MainMenu, MainMenu_Sub, or a showroom screen) is on the stack — the port keeps the city under the safehouse menus.
`FEScreen.Overlay` [ours, additive]: pushed screens that draw over the one below without hiding it (dialogs).
`FrontEnd.Defer(action)` [ours]: runs after the current push / pop finishes (dialog replies, screen routing).
`FrontEnd.Stack` (additive).

## 2. Dialogs — feDialogBox.cpp (PC: feDialogConfig 0x56C5E0, DismissDialog 0x56C6E0, ShowDialog 0x5983C0,
BuildFromConfig 0x58F720, NotificationMessage 0x57EE60, dtor 0x57F0D0)
* Package: "" → **InGameDialog.fng in game, Dialog.fng in the front end**; "animating" OL_Dialog, "3button" Dialog,
  "3buttons_stacked" OL_Dialog_Stacked_Buttons. One dialog at a time (a new one dismisses the old).
* BuildFromConfig: blurb into 0x1E2640FA (the blurb is the printf format); 3 / 2 / 1 / 0 buttons: labels on
  0xF9363F30 / 0xFB8B67D1 / 0xFDE09072 by language hash, unused buttons disabled + invisible (0 buttons also hides the
  cursor 0x07F9DCA9); size message by strlen · font height: < 2200 DIALOG_SMALL 0x79B0C1C7, < 4400 DIALOG_MEDIUM
  0xA13ADCAF, else DIALOG_LARGE 0x792BC959; header 0x42ADB44C + icons 0x0D4F4069 / 0xFAC88427: alert / fatal →
  "ATTENTION" 0x2BD146D3 in game ("attention" 0x6FD91524 in the FE), tex 0x6948E2B3; none / info / countdown →
  "information" 0xDBE419D4, 0x1A7AFE27; confirmation → "selection" 0x60249A74, 0x39949433.
* INIT_COMPLETE highlights FirstButton (button 2 = Cancel for ShowOkCancel / the in-game confirmations). BUTTON_PRESSED
  on Button1..3 (0xB8A7C6CC..CE) returns ButtonN's message and dismisses; PAD_BACK returns the cancel message when
  dismissable; PAD_UP / PAD_DOWN move between buttons; countdown dialogs reprint "%d" seconds and press back at 0.
  The package does the rest: pressing a button plays leave-all + the handler's FORWARD and the button's 300 ms
  AE59D884 script, whose event sends BUTTON_PRESSED to the game; back plays the 500 ms exit first.
* ~feDialogScreen: `QueueGameMessage(ReturnWithMessage, ParentPackage)`. Port: `DialogInterface.Show*` with a parent
  `FEScreen` (FEMenuScreen parents get the reply as a game message) and/or an `OnReply` callback [ours].
* Replies used in game: OK 0x34DC1BEC, Yes 0xD05FC3A3, No 0x34DC1BCF, Cancel 0x1FAB5998, cancelled 0xB4EDEB6D.
* `ConfirmScreen(title, body, ok)` and `MessageScreen(title, body)` (public, unchanged signatures) are now dialogs:
  ShowTwoButtons(alert, OK / Cancel, Cancel first) and ShowOk(info). [ours] A non-empty title goes on the blurb's first
  line (the exe's dialogs have no title text). `Safehouse/FEDialog.cs` (the safehouse screens' callback API: ShowOk,
  ShowOneButton, ShowTwoButtons, ShowYesNo, ShowThreeButtons, ShowTwoButtonCountdown, Press) is now a thin subclass of
  DialogScreen, so there is one dialog implementation. `DialogScreen.Press(n)` presses a button at once (QA tour).

## 3. Post race — FEPKg_PostRace.cpp
### 3.1 Which screen (FE_ShowPostRaceScreen 0x60AC20, EShowResults ctor 0x642140, EShowMilestones 0x641FE0)
FinalizeRaceStats; pursuit race: won → EShowMilestones(0) (PostRace_MilestoneRewards), else EPause. Racing: **won (and
not split screen) → push InGamePhotoMaster.fng (Arg 0 = FERESULTTYPE_RACE)**, else EShowResults(RACE) →
PostRace_Results.fng. EShowResults(PURSUIT) → PostRace_Pursuit.fng after PursuitData::PopulateData; SPEEDTRAP → no push.
Game_EnterPostRaceFlow (0x611F20): career + NoPostRaceScreen → outro without a screen.
Port: RaceManager pushes `PostRaceResultsScreen(race, won)`; a win (no LAN / split) is routed at once to
`PhotoFinishScreen` (Defer + Replace, the stack never empties).

### 3.2 PostRace_Results (PostRaceResultsScreen; ctor 0x5A6EC0, Setup 0x5A19C0, NotificationMessage 0x5A7120)
* Modes RESULTS / STATS / LAPSTATS; tollbooth races start on LAPSTATS and BUTTON4 toggles LAPSTATS ↔ STATS, others
  cycle RESULTS → STATS → LAPSTATS. The package plays the row LEAVE cascade on BUTTON4 and sends BUTTON4 to the game
  500 ms later (handler STATS), then re-runs the row APPEAR cascade (`call` events D664E41B..23 at 60 ms steps).
* Setup: LINE1..11_GROUP HIDE; COLUMN2 invisible on STATS (and tollbooth LAPSTATS); the three headers invisible; the
  page; restart group 0x445A862B APPEAR (offline). Each row added plays INIT on its LINEn_GROUP.
* RESULTS: headers Position / Name / Time (speedtrap: Speed), title "STANDINGS" (0xFF115FFF), race button "Statistics";
  rows in ranking order: name, time or Blown Engine / Totaled / K.O. / "--" (not finished), position "%d"; speedtrap rows
  show the point total "%$0.0f units" (km/h → mph conversion when imperial).
* STATS (title STATISTICS): sprint: top speed, average speed, time behind, traffic collisions; circuit / KO: + lap
  variance; drag: 0-60 (labelled "0 to 100 km/h" in metric but measured at 26.82 m/s), quarter mile (400 m label),
  perfect shifts, time behind, traffic collisions; speedtrap: accumulated speed, speed variance (worst − best,
  "%.2f"), speed behind; tollbooth: top / average speed, traffic collisions. LAN replaces time behind / variance /
  traffic with N2O used. Missing data → "--" (0x0FC1BF40). L / R triggers pick the racer, its name in 0xEB43CCB0.
* LAPSTATS: stages (20/40/60/80/100 %, split position and time), laps (per-lap time and lap position, K.O. for a
  knocked-out racer below 2nd), speedtraps (**SpeedStat multiplies the stored speed by 3.6 / 2.23699, i.e. reads it as
  m/s**, "%0.0f"), tollbooths ("+time left" per booth, the finish row the time remaining; **TollboothStat compares the
  position with −1.0 (0x890DA8)**, the decomp's 1.0 is a misread).
* BUTTON5 (key 1): ShowTwoButtons(alert, OK / Cancel, 0xE1A57D51 / 0xB4623F67, Cancel first, 0x4D3399A8 "restart …");
  0xE1A57D51 → EUnPause + ERestartRace (under the photo: mRestartSelected). Accept: career boss race the player did not
  win → dialog 0x9887EB98 (quit to free roam); otherwise the handler's 0x001335F0 exit → EXIT_COMPLETE: under the
  photo → pop back to it (and InGameBackground); career (not D-Day) → RaceAbandoned if done, EUnPause; else done →
  EQuitToFE(main), not done → EUnPause.
* GRacerInfo stats RaceManager does not keep (top speed, distance / update time, 0-60 at 26.8218 m/s, quarter mile at
  402.335 m, finishing speed, perfect shifts, traffic hits, NOS) are tracked by `RaceStatsTracker` (InGame/RaceStats.cs)
  [ours: per frame from the HUD, traffic hits by a collision component; NOS pounds scale [?]].
* Timer::PrintToString (Misc/Timer.cpp:239) → `FETimer.Print(sec, flags)`: "HH:MM:SS" / "MM:SS.hh" / "SS.hh" /
  "0.hh", leading field without a leading zero (space), flags 1 '-' 2 '+' 4 unused digits 8 keep last 16 no ms.

### 3.3 InGamePhotoMaster (PhotoFinishScreen; ctor 0x58E630, NotificationMessage 0x5A7EA0, Setup 0x5A2AD0)
Photo 0x286A9CD4 from GLOBAL\HUDTEXTURESPHOTOFINISH.BIN (PHOTOFINISH_SPRINT / _RIVAL / _DRAG / _TOOBOOTH): career boss
race → PHOTOFINISH_RIVAL, else the race's `PhotoFinishTexture` attribute [fallback by type ours]. Timeline: cinematic
slowdown 0.75 s → hide HUD, pause, ECinematicMoment(PhotoFinishCamera) → 0.75 s → handler `call`, the text group
(TOLL_BOOTH_GROUP / RIVAL_GROUP / SPRINT_GROUP) and the photo APPEAR, Setup prints "time @ speed units" (finishing
speed), "+bonus time", cash "CASH: %$0.0f" (not in quick race), BK reward dialog for 19.8.31. Accept (ignored while the
photo is hidden or appearing): career boss → rival flow (all boss races done → SafeHouseRivalChallenge; bin 1's last
→ outro; else ERaceSheetOn(2)); career → EUnPause + outro; else EQuitToFE (challenge autosave not ported). BUTTON4 →
InGameBackground + PostRace_Results; BUTTON5 → restart dialog. The slowdown and the ICE photo-finish camera run through ChaseCamera
(StartCinematicSlowdown 0.75 s, PlayGenericCamera "Cinematics" / DefaultFinish or PhotoFinishCamera) with
`ChaseCamera.IceUseRealTime` set while the camera plays over the paused sim (ICEManager::SetUseRealTime) and cleared in
OnClose; the boss-race branches all close the FE and leave the career flow to RaceManager.AfterResults / Story.

### 3.4 PostRace_MilestoneRewards (PostRaceMilestonesScreen; 0x58E490 / 0x5A7D20)
INIT_COMPLETE → pursuit bounty (icon type 0x033FA23A, POS0, text "Pursuit Duration"); each 0x0D3C3DE7 (sent at the end of
the icon's POSn script, which starts when its APPEAR has run) → cop-destruction bounty (if cops destroyed; POS00,
"Immobilized Police Vehicles: n") → the milestones done this pursuit ("header: goal", POSn by type) → done (icon HIDE,
desc / earned LEAVE, 0x962B9C62 / 0xEC85C7E4 POS2). Lines: 0xE526D0D2 description, 0xE1045A4F "Bounty: %$0.0f"
(hidden at 0), 0x324F7792 "Total Bounty: %$0.0f". Accept → pop + EShowResults(PURSUIT).
Port: the pursuit system's `new PursuitResultScreen(stats, bounty, done)` opens this first, then the table [ours: the
whole bounty counts as "normal" — the pursuit system keeps one bounty]. In a pursuit race other than the final chase
(`PursuitResultScreen.ChallengeRace`: Event.PursuitRace / Type Pursuit, EventId ≠ RaceIndex.FinalEpicChase) the
0x0D3C3DE7 after the bounties runs StartChallengeAnimations (:1298) once: "CHALLENGE_SERIES_DESC_nn_HEADER: desc" of the
race's MilestoneType (GManager::GetBestValue = PursuitStats.Value) against Threshold, worth the race's Reputation.

### 3.5 PostRace_Pursuit (PostRacePursuitScreen; 0x5A9430, Initialize 0x5A7910, NotificationMessage 0x5A7AE0)
ArrayScrollerMenu 1×9 (LINEn_GROUP, COLUMN1/2_DATAn, CHECKn, EMPTYn). PURSUIT (title PURSUIT 0xFEA872D4): duration,
damaged / immobilized police vehicles, spike strips, roadblocks, cost to state, unserved infractions. INFRACTIONS
(0xFB415E78): 8 rows in GInfractionManager order (Speeding, Excessive Speeding, Reckless, Ramming, Hit & Run, Damage to
Property, Resisting Arrest, Driving off Roadway). MILESTONES (0x578B767B; pursuit race: CHALLENGE): the bin's milestones,
ticked = done pending escape, greyed = awarded. BUTTON4 cycles PURSUIT → INFRACTIONS → MILESTONES → PURSUIT (in the FE:
PURSUIT ↔ INFRACTIONS). Accept: a finished pursuit race → EQuitToFE, else EUnPause. [?] Check rows: the decomp sets GREY in
both branches; the port shows the tick for a committed infraction. [?] Milestone values print with
SetMilestoneDescriptionString(type −1): number milestones show only the goal, time ones "cur/goal" as numbers.

## 4. EngageEventDialog (FEPkg_EngageEventDialog.cpp; ctor 0x597DB0, NotificationMessage 0x56B470)
Icon 0xAD9E232A (GetRaceIconHash), type 0xA01B9361 (GetRaceNameHash), track 0xF601F2D4 (TRACKNAME_<event id, '.'→'_'>:
FixDot confirmed against English.bin, e.g. TRACKNAME_15_1_1 = Ironwood Estates), laps 0x644AB208, distance 0xBCE13923
("%$0.1f" × 0.001 km or × 0.000625 miles of RaceLength), cops 0x762F1D7A (Maximum / Minimum), best time 0x8FD41BB4
(or --:--.-- for untimed types), average / top speed 0x35D1AB83 / 0xDE9145FB from the save, cash 0x45276F1F, track map
TRACK_MAP_<event>. The package selects Decline (0x694B896E) at 500 ms; Accept 0xD72F002A → MAcceptEnterCareerEvent
(port: GameFlow.StartRace), Decline / back → pop.

## 5. Rival challenge — uiRepSheetRival.cpp (SafeHouseRivalChallenge / InGameRivalChallenge)
Arg 0 FE, 1 in game, 2 mid rival flow, 3 one-off (boss-race zone). Rival art + the DEFEATED image (0x7FE4020F / 0x26869897,
texture 0x087B7D0A English, shown once loaded); messages: mid flow 0x34297CB0, post-rival 0x0B21A45F + 0xB4C144B1,
else 0xAF922178; row layout by boss-race count (≥5 0xE7177701, 4 0x9A1D3A40, 3 0x4D22FD7F, else 0x0028C0BE),
CROSSOUT_n per completed race; rows EVENT_ICON_n / EVENT NAME_n / DATA_n (track name) / RIVAL_BEST_DATA_n
(RivalBestTime [?: attribute name]); bounty / cash lines. Accept: mid flow → ERaceSheetOff + outro; post-rival → EEnterBin
+ flow stage 1; else start the bin's **first** boss race. Back: one-off → unpause; else switch to the Blacklist overview
(not mid flow / post-rival). Port: `RivalScreen()` = arg 3 in game / 0 in the FE; `RivalScreen(arg)`,
`RivalScreen.PostRivalMode(bin)` (RivalFlow stage 0 now uses it instead of a message box); the rep sheet's BUTTON4 now
switches (Replace) to it.

## 6. Pause menu — uiPause.cpp (Pause_Main; SetupOptions 0x52B4D0, NotificationMessage 0x54FF10)
Option lists exactly as SetupOptions (see the class comment in InGame/PauseScreen.cs; matches world_interaction.md §5,
plus: quick race adds Performance Tuning unless tollbooth / challenge; split screen has no tuning; "called from post race"
= the restart menu). Options / tuning are pushed (their back pops to a fresh Pause_Main: the exe switches packages).
Removed [ours] entries the exe does not have: World Map, Messages, Blacklist, Change Car.
Labels / icons by hash (Resume 0x12BB5EA2, restart 0xB295A6B6, quit 0x4C9E34E6, free roam 0x56FFBD2C, tuning 0x483238FD,
options 0x520DE4E3). Reactions: resume / options / tuning → handler FORWARD (0xDE6EFF34) → EXIT_COMPLETE acts on the
selection (0xFDAE152F unpause, 0xFBDF2EE3 restart, 0xCDD2635A quit to free roam + RaceAbandoned, 0x33195CF0 options,
0x78F1C035 tuning, 0xE5C9C609 quit to FE: career → safehouse, else main menu); restart / quits ask first (texts
0x4D3399A8, 0x9887EB98, 0x1DB1CDE5, 0xA2E9B449, and for pm_QuitRaceToFE by context: quick race 0x1DB1CDE5, D-Day / epic
0xECD92696, racing 0xCDE4CAE8, pursuit 0x15A1B5A9, else 0x6925D0BE); tuning without tunable parts → 0xA7EE8554.
Title 0x863404B5: PAUSE MENU / RESTART MENU. [?] The icon scroller layout (feIconScrollerMenu.cpp) is approximated; back
leaves through FORWARD and unpauses. The class moved from Game/GameFlow.cs to UI/InGame/PauseScreen.cs (same namespace
NfsMw.Game, same constructor).

## 7. Busted overlay — FeBustedOverlay.cpp (BUSTED_OVERLAY; ctor 0x58D340)
Ticker 0xF1ED545A: arrest NIS → 0x626AC042..46 by roadblocks / spike strips / cops destroyed / cost to state, intro NIS
→ 0x626AC047. Port: `BustedOverlayScreen.Show(stats)` exists but nothing calls it yet (the arrest NIS is NIS / police code).

## 8. Not ported / open
* EA_Trax.fng (Chyron) is in GLOBAL\GlobalB.lzc: ported, §12.
* FEAnyTutorial and SixDaysLater: ported (§12). InGameTutorialScreen, FEAnyMovieScreen, FeFadeScreen, FEpkg_MU_Keyboard: not ported.
* The HUD runs on the engine now (§11, hud.md §7).
* PC FE key table for the triggers and keys 3/4 not read; button-map wrap mode not read.
* ArrayScroller scrolling for > 9 rows not ported. IconScrollerMenu: ported (Screens.cs CarouselScreen, fe_safehouse.md §15.4).

## 9. Wiring with the other systems (this round)
* Restart (results, photo finish, pause): `RaceManager.RestartRace(ev)` (adaptive-difficulty Restart update, `Restarted`
  event), called while the race being left is still active (the photo's mRestartSelected / destructor order is collapsed).
* Minimap and world-map race icons sit at `GameSession.EngagePoint(ev)` and only for
  `RaceDatabase.IsAvailable(ev, RaceIndex.ContextCareer)`.
* Race reputation is not paid as bounty any more; its only reader in the exe is PostRaceMilestonesScreen's
  StartChallengeAnimations (pursuit challenges), ported this round (§3.4) — reached when the pursuit system pushes the
  result screens during a pursuit race (PursuitSystem.ChallengeEnd's own dialog is not mine).

## 10. FEng engine remainder (FE remainder round, 2026-10-24)

Port: `Core/FEng.cs`, `UI/FEView.cs`, `UI/InGame/FEngEngine.cs`, `UI/FEngRender.cs`, `UI/FEText.cs`, `UI/FEColoredImage.cs`,
`Shaders/UIBlend.shader`, `Shaders/UIMultiImage.shader`. Harness: `ferest fonts|measure|keys|tracks|texblend|initvis|visdiff|pivots`
(`NFSMWStaging/Harness/ProbeFeRest.cs`).

### 10.1 Visibility (fixed)
* `FEObject.Hidden` was `Flags & 0x90000000` (FF_IsButton | FF_HideInEdit): every button of a plain view started hidden
  and a console-glyph heuristic (`HideConsoleButtons`) guessed the rest. Now: game flag **bit 0** = invisible, as
  `cFEngGameInterface::RenderObject` (FEGameInterface.cpp:153) and `cFEngRender::AddToRenderList` **0x5A46A0** test it;
  FEngSetInvisible **0x514C70** ORs 0x2400001 recursively, FEngSetVisible **0x514CC0** clears bit 0 (not for flag-0x40
  objects) recursively.
* The test is **per object**: `FEngine::RenderGroup` (FEngine.cpp:823) walks a group's children whatever the group's own
  bit 0 and only drops them at alpha 0. The PC key prompt groups (flags 0x40020009: bit 0 + flag 8 + mouse object) are
  invisible themselves while their children draw. `FEView.Layout` no longer inherits visibility; `FEView.SetVisible` is
  recursive like the exe's setters (`SetVisibleTree` keeps the flag-8 rule for FEMenuScreens).
* Package-loaded pass (**0x525520**, every package): `FEPackage::InitializePackage` 0x5B8510 (callback 0x5B8470: current
  script = INIT, SetupMoveToTracks, dirty), one package Update, then three ForAllObjects: FEngMovieStarter 0x559680 (type 7
  movies), **FEngHidePCObjects 0x516230** (flag 0x40 → invisible recursively, FF_IsButton cleared, 0x400000 set),
  **FEngTransferFlagsToChildren(4) 0x516270**. Plain views now get the 0x40 pass too (was engine views only).
* Flag 0x10 (`RenderObject`): the object's colour is replaced by gNormal / gTint / gRapsheet by the FE game mode [not ported].
* `cFEngRender::RenderObject` **0x5A45B0** draws types 1 image, 2 string, 7 movie, 9 coloured image, 12 multi-image
  (RenderImage 0x59A500, RenderString 0x5A4390, RenderMovie 0x599DB0, RenderCBVImage 0x59A790, RenderMultiImage 0x59A060) and
  nothing for a flag-0x40 object; FEObjType 9 = ColoredImage, 12 = MultiImage (the old reader had them swapped: 7 / 12).

### 10.2 Object data and tracks (FEKeyTrack.cpp, FEKeyInterpLinear.cpp, FEKeyInterpNone.cpp, FEObject::SetupMoveToTracks)
* SA is read dword by dword (`FEObjectData[i]`): 0-3 colour, 4-6 pivot, 7-9 pos, 10-13 rot, 14-16 size, images 17-20 UVs;
  multi-image 21-32 the three layers' TL / BR UVs, 33-35 PivotRot; coloured image (0x94) 21-36 four vertex colours (TL, TR,
  BR, BL, ints) — `RenderCBVImage` uses only those (no object / parent colour), `FEColoredImage` draws them.
* FI = {ParamType, ParamSize, **InterpType** (0 none, 1 / 3 linear, 2 / 4 ignored), **InterpAction** (0 clamp, 1 loop over the
  track Length, 2 ping-pong), Length}. The first `Kd` key is the base, the rest deltas. Linear: before the first delta key
  base + first; after the last base + last; ints / colours lerp as `n1 + (int)((n2 − n1)·t + 0.5)`; quaternions base ·
  slerp (FEQuaternion::operator*, FEMath.h:55; nlerp above dot 0.999). `FEKeyInterpFast`: a track without delta keys is
  applied once (action |= 0x80) until the script is set again — values set by code afterwards stay (the old port re-applied
  every frame). MoveTo tracks (InterpType 3 / 4) get their first delta key = current value − base at SetScript and at every
  loop (`SetupMoveToTracks`). Port: `FETrack.Evaluate(t, firstDelta)`, `FEView.Node.StaticTracks / MoveTo`.
* `FEPackage::UpdateObject` is now shared by plain and engine views (`FEngEngine.cs StepScript`): chain, loop (1), ping-pong
  (2: time modulo 2·Length, events from `prev − len` on the way back), stop at Length + 1. Plain views issue no events.
* The packages use: 48k scripts; actions 0 / 1 / 128 / 129, interp 1 and 3 (none use 0 / 2 / 4), offsets 0, 4, 7, 10, 14,
  17, 19, 21, 33 (`ferest tracks`).
* MakeRenderMatrix **0x5848B0**: `v · Size · T(−Pivot) · Rot · T(+Pivot) · T(Pos)` then the parent's; `FEView.Layout`
  applies the pivot (564 objects have one, 463 of them rotate: Car_Select's stat needles, the arcs).

### 10.3 Fonts and text (Frontend/FEngFont.cpp; PC ctor 0x585BA0, GetCharacterWidth 0x586050, GetLineWidth 0x586130,
GetTextWidth 0x586210, GetNextWordWidth 0x591F60, GetTextHeight 0x591FD0, RenderString 0x59AC60, PrintCharacter 0x585D60,
ConvertCharacter 0x5722C0, RealFontOld GetGlyph 0x569BF0 / BSearch 0x7E88F0 / GetKern 0x7E89D0)
* FNTF: +0x0A glyph count, **+0x0C flags (0x40000 = 16-byte glyphs with kern runs)**, +0x14 glyph table, **+0x18 kern table**
  (count dword then {s16 prev char, s8 kern, u8 char}), +0x24 height. Glyph: {u16 code, u8 w, u8 h, u16 u, u16 v, s8 advY,
  s8 offX, s8 offY, u8 numKern, u16 kernIndex, s16 advX}. The four shipped fonts (font_mw_title 44 px 127 kern pairs,
  font_mw_body 23 px 71, font_mw_custom_numbers 45, tac_numbers 31) are all flags 0x40009.
* The font hash is the chunk name without a trailing `_<LANGUAGE>` (table 0x8F40F8); ExtraFontDataTable **0x8F48B8**
  {DCA5485A 18/2, 833A8678 22/2, F88A75F9 18/1, 71C777D7 23/1} = baseline offset / leading scale (none of the shipped
  fonts match [?: other builds' fonts]).
* Layout rules (FEText.cs): per-line X by format bit 0 centre / bit 1 right of GetLineWidth, Y by bit 2 centre / bit 3
  bottom of GetTextHeight; `\n` and `^` break lines (not with object flag 0x20); word wrap (bit 4) at a space whose next
  word passes MaxWidth from the line start, the space skipped; the first space of a wrapped line skipped; leading = (int)(Sl
  · leading scale) added to the font height; `$$` → `$`, a lone `$` ends the string on PC (no joy-event textures); kern
  only after the first character; glyph quad at (x + kern + offX, y + offY + baseline), width ≥ 4, UVs (u − 1, v − 1)..(u + w
  + 1, v + h + 1) over the atlas (PrintCharacter). ConvertCharacter: ≥ 0xFF80 → low byte, 0x99 ™, 0x9C œ, 0xA0 space.
* `ferest measure <font> <text>` prints the exe widths; `FEText.TextWidth / TextHeight` expose them (used by the chyron).

### 10.4 Blend state (FEngRender.cs, UIBlend.shader) — the fix for the leaderboard question
* The FE draws through eViewPlatInterface::Render **0x6D1260** → texture state **0x6C68B0**, which sets each texture's own
  blend from TextureInfo +0x56 AlphaBlendType: 0 off, 1 SrcAlpha / InvSrcAlpha, 2 SrcAlpha / One, 3 Zero / InvSrcColor,
  5 DestAlpha / InvDestAlpha, 6 DestAlpha / One; +0x55 AlphaUsageType 1 = alpha test (ref 0x5F, 0x6C67C0).
* fe.efx (IDI_FE_FX, file 0x51E140) ps_1_1: `r0 = tex · v0; rgb ×2; a = tex.a · v0.a`; the CPU halves the FEColor rgb
  (FEngColorToEpolyColor **0x56FA90**: `(c / 2)`, alpha whole) — net plain modulate. fe_mask.efx (0x520910): alpha also ×
  the mask's alpha (multi-images).
* HUD_SingleRace: BasePoly_Add, Heat_Icon_Additive, button_ring, outter_ring, IconSelection_Glow, Pursuit_LeaderList_Pulse,
  Helicopter_Line_Of_Sight are additive (type 2); Radar_LEDs_Color is blend-off + alpha test; everything else type 1
  (`ferest texblend HUD_SingleRace`). The leaderboard backings LBBacking_n are BasePoly (type 1), INIT (255,255,255,80),
  only the player's row plays Highlight (orange, alpha 60..140) — the light translucent look is the package's.
* `FrontEndData.SourceOf(texture)` keeps the TPK record; `FEView.ApplyBlend` picks a shared UIBlend material per (src, dst,
  cutoff) and sets `_SrcBlend/_DstBlend/_Cutoff` on multi-image materials.

### 10.5 FEView additions
`Clone(node)` / `Remove(node)` (FEObject::Clone + FEPackage::AddObject / RemoveObject, the credits), `FEView.Open(...,
engine: true)` for the HUD, movies (type 7) get a node (no video surface [ours]).

## 11. HUD on the engine (hud.md §7)
GameHud opens HUD_SingleRace / HUD_Drag as engine views: script events, MsgR / PkgR responses and the flag-bit-0 /
0x40 passes come from the package; FADE_IN + DEACTIVATE are sent when the HUD opens (FEngHud vtbl+0x10, 0x569570 [?: its
caller]), WIDESCREEN_MODE / NORMAL_MODE on an aspect change (SetWideScreenMode 0x5696C0). `HudFeatures.cs` =
DetermineHudFeatures 0x57CA60 → SetHudFeatures 0x57CDB0 → HudElement::Toggle 0x5793F0 over the decomp's RegisterGroup /
RegisterObject lists. The old hide-set in GameHud.Bind is down to the port's own items (split divider, the Minimap.cs
objects, the mirror backing, the online debug lines).

## 12. New screens (this round)
* **EA_Trax.fng chyron** → `InGame/ChyronScreen.cs` (FEPkg_Chyron.cpp; InitChyron 0x569C80, SummonChyron 0x595ED0,
  Chyron::NotificationMessage 0x595E80, Start 0x58D670, DismissChyron 0x569CC0). `MusicSystem.Chyron` → Summon; start at
  once in the front end, else 5 s later; title 0xF5387816 / artist 0x11DB9E17 / album 0x48097852; panel by the widest text
  (≤ 185 → 0x57521F29, ≤ 250 → 0x57520493, else 0x73D17048); TRAX_POS_2 in the FE else TRAX_POS_1 (POS_3 = D-Day race while
  autosaving [not reproduced]); WIDESCREEN_MODE(HUD) / NORMAL_MODE(HUD) broadcast; the package's 6 s timer posts 0x835C45E8
  (leave) then 0x84CFC46F to the game → the view is dropped [ours: the exe keeps it pushed invisible]. Dismissed while a
  movie plays. Own canvas (sort 20).
* **SixDaysLater.fng** → `InGame/SixDaysLaterScreen.cs` (ctor 0x58E820, tick 0x56AA70; EShowMessageScreen dtor 0x625090
  pushes it): text 0xB769701E = DDAY_TIMELAPSE_<id + 1>; tick: text APPEAR set → image 0x53D9EB7E APPEAR; text ANIMATE
  (0x5A8E4EBE) set and finished → pop + MNotifyMessageDone (`Done` callback). **Hook**: Story.cs / LuaDrive's `StoryCard.Run`
  draws its own card; `FE.Push(new SixDaysLaterScreen(id, done))` replaces it 1:1.
* **FEAnyTutorial.fng** → `Safehouse/TutorialScreen.cs` (FEAnyTutorialScreen::LaunchMovie): the movie through
  MoviePlayer.TryPlay, accept / back / start skip, `Done` = the caller's 0xC3960EB9. Used by the Challenge Series "View
  Tutorial" (tollbooth_tutorial).
* **BUSTED_OVERLAY.fng** (BustedOverlayScreen exists): the exe never pushes it from FE code — the ICE camera system does:
  `ICEMover::SetDesired` 0x482160 → `ShowOverlay(n)` 0x46EA50 (`PushNoControlPackage(name, 0x67)`) when a camera key's
  nOverlay changes; overlay table 0x8EDEEC {0 none, 1 FLASHERS, 2 BLACK_FADE_OUT, 3 BUSTED_OVERLAY, 4 BLACK_BACKGROUND};
  every Scene_Arrest* NIS camera track keys overlay 3 from its first key, `HideOverlay` 0x46EAE0 pops it on a change and
  from `NISActivity::Release` 0x6F7520. The ticker (0xF1ED545A) picks 0x626AC042..46 from PursuitData (0x91D3D8: roadblocks
  ≥ 2 and spikes ≤ 1 → 42; roadblocks ≥ 2 → 45; destroyed ≥ 2 → 44; cost > 10000 → 46; else 43), time = length · 4000 ticks;
  INIS type 0 (intro) → 0x626AC047. The arrest scenes send ENISOverlayMessage("BUSTED_OVERLAY.fng", "FLASHTRIGGER"
  0xEEF1C021) = the white FLASH. **Hook (NIS / ICE owner)**: on an arrest NIS camera key with nOverlay 3 push
  `BustedOverlayScreen.Show(stats)`, pop it at the NIS release; post FLASHTRIGGER on the scene event.
* **FadeScreen.fng** (FeFadeScreen.cpp; EFadeScreenOn 0x62C9A0, EFadeScreenOff dtor 0x62C810, IsFadeScreenOn 0x569BB0):
  group 0x027FF2DC scripts APPEAR (instant black + loading box 0x75BB3638), 0xD42290DD (loading bar only), 15_IN 0x014035FB /
  30_IN 0x0161A918 / 45_IN 0x01767F7E (300 / 600 / 900 ms black → clear), 15_OUT 0x2947101C / 30_OUT 0x2D96E6D9 / 45_OUT
  0x304689FF; FEngHud::Update sends 15_IN once the HUD runs and no NIS. Not ported (GameFlow has its own fades) [?].

## Addendum (2026-10-24): shared FE pieces touched by the customize round
* IconScrollerMenu (feIconScrollerMenu.cpp) is now exact in `Runtime/UI/Screens.cs` CarouselScreen (bookends, option_N
  assignment, tCubic1D 0.2 s seek, IconScroller::Scale ramp, idle/fade tints) — see fe_safehouse.md §15.4; the in-game pause /
  options menus built on it inherit the geometry.
* FEListBox.cpp / FECodeListBox.cpp: no PC package has a List (type 4) or CodeList (type 6) object (harness `fecust objtypes`),
  so nothing in game is ported from them — N/A.
* PhotoFinishScreen (FEPkg_PostRace.cpp photo finish): while the ICE "Cinematics" camera plays over the paused sim the screen
  sets `ChaseCamera.IceUseRealTime = true` (ICEManager::SetUseRealTime) and clears it in OnClose, so the ICE camera keeps
  moving on real time while the sim clock is frozen.

## Addendum 2 (2026-10-24): FEPKg_PostRace.cpp and FEngine.cpp diffed function by function
### Post race (§3) — now Ported
* `PostRaceResultsScreen::NotificationMessage` INIT_COMPLETE (:683): LAN / online only, 0x812A09D4 HIDE and 0x05D85A9F
  APPEAR (the LAN button group) — added. `NotifySoundMessage` (:840) suppresses the trigger sounds (0x4A805994 /
  0x9AFA53A7) with one racer or on RESULTS and those plus 0x7B6B89D7 while the button bar 0x57EFB2FB is hidden: declared
  as an override, but [?] the port's FE sounds come from FrontEnd's input layer, not from FEng sound messages, so there
  is nothing to suppress yet. All SetupStat_* / SetupResults / SetupRacerStats / SetupLapStats and the exit branches
  matched the decomp line by line (the known exe-vs-decomp corrections stay: TollboothStat −1.0, SpeedStat m/s).
* `PostRacePursuitScreen`: the front-end branches (GameFlowManager::IsInFrontend → `GameFlow.IsInFrontEnd`) — INFRACTIONS
  button "Done" 0x7448870B, BUTTON4 only PURSUIT ↔ INFRACTIONS, accept → QueuePackageSwitch ChallengeSeries.fng (quick
  race mode) / Car_Select.fng (:1196); in game 0x9145A5F2 / MILESTONES title 0x334FA7FB in a pursuit race that is not the
  final chase (cFrontendDatabase::IsFinalEpicChase = EventId 1.8.1). `SetupMilestones` (:1126) pursuit-race branch: one
  row of the race's challenge (type → Time for 0x5392E4FD, MilestoneTime for 0x033FA23A, else MilestoneNumber; best value
  vs goal, ticked when met). Not wired: `~PostRacePursuitScreen` posts MEnterSafeHouse("safehouse") when
  PursuitData.mExitToSafehouse is set — nothing in the port sets it (the bust-in-safehouse flow is the pursuit owner's).
* `PostRaceMilestonesScreen::StartChallengeAnimations` (:1298) — added (see §3.4).
* `PursuitData::PopulateData` / `PursuitResultsArraySlot::Update`: already matched; no change.

### FEngine.cpp remainder (§1, §10) — reachability from the shipped packages
Harness `fecust responses` (FEResponse id histogram over every object and package response list of FrontB / InGameB /
GlobalB): the data only uses MR 0 SetScript, 1 PostMessageToFEng, 2 PostMessageToGame, 3 PostMessageToSound,
256 SetActiveButton, 257 SetInputProcessing, 259 RecallRecordedButton (3 uses: Keyboard, GenericDialog_ThreeButton — no
package ever records, so the fallback button applies), 513 PushPackageGlobal (6), 515 PopPackage (3), 768 / 769 / 1280 /
1281 (If / Else / EndIf). Never present: 258 RecordCurrentButton, 260-263 DontNavigate / PassControl*, 512
SwitchToPackage, 514 / 516 PushPackageCurrent / None, 704-706 the package markers. The port's ProcessResponses covers
every reachable case. Pad messages beyond the plain presses: no package answers PAD_*_RELEASED, PAD_L/RTRIGGER_HELD or
the control-transfer message 0x334C5493; BUTTON_RELEASED 0x936A6A7F is answered only by Options_PC_Controller /
Pause_PC_Controller (the key-remap screens, which ControllerScreen drives itself). FEListBox / FECodeListBox: N/A (no
type 4 / 6 objects). Direction repeat (FEngine.cpp:603): first repeat after FEFramesToTicks(20) = 320 ms, then every
0x78 = 120 ms — FrontEnd.MenuInput.Read now uses 0.32 / 0.12 s (was 0.38 / 0.09). Package levels, control masks,
library packages and the per-pad JoyMask are replaced by the FrontEnd stack (one local player) — N/A.

## 13. Round 9 (2026-10-25): FEng core diffed function by function (FEKeyTrack, FEMessageResponse, FEPackage, FEObject,
FEKeyInterpLinear, FEMsgTargetList, FEString, FEScript, FEGroup, FEButtonMap; FERenderObject / FEngRender)

Port: `Core/FEng.cs`, `UI/FEView.cs`, `UI/InGame/FEngEngine.cs`, `UI/FEngRender.cs`, `UI/FEText.cs`. Harness: `feeng9 scripts |
strings [pkg] | chunks | flag10 | sa <pkg> <obj..> | trackinfo | minimap [file] | texfind <name|hash..>`
(`NFSMWStaging/Harness/ProbeFeEng9.cs`).

### 13.1 Data facts (harness feeng9, all 199 packages of the shipped bundles)
* Script sub-tags present: only `Sh`, `Sc`, `FI`, `To`, `Kd`, `EV`. The exe's ReadScriptTags (PC 0x5C3610) also knows Sn / Si /
  Sl / Sf / Ti / Tt / Ta / Tb — never present: dead. FI Length is a 24-bit field (FEKeyTrack.h:72; FEInterpLinear sign-extends
  it): no track has a high byte. Every track has a `To` tag; the exe takes its **first byte** (`To` ≤ 255 everywhere) and
  without one the running sum of ParamSize / 4 (now the reader's default too).
* Play actions: script Flags & 3 only 0 (47091) / 1 (2333) → ping-pong (2) and 3 dead; track InterpAction 0 / 1 / 128 / 129,
  InterpType 1 / 3 → interp 0 (FEInterpNone) and 2 / 4 dead; MoveTo (3) used 3290 times.
* Events are sorted by time in every script (IssueScriptMessages scans from the first time ≥ from and stops at the first ≥ to).
  Targets: 0 ×909, 0xFFFFFFFA ×6, 0xFFFFFFFB ×438, 0xFFFFFFFC ×787, 0xFFFFFFFF ×862, objects ×796.
* Every object has an INIT script; every package has a `Targ` chunk (the port's "no Targ → every object with a response"
  fallback was [ours] and never ran — removed); no package has library references (FEPackage::SetNumLibraryRefs /
  FindLibraryReference, FEngine library lists, ReadLibraryRefsChunk / ReadReferencedPackagesChunk: dead).
* FEString `Sb` (buffer length): 1464 strings 8-15, 1109 16-31 …; FEPrintf cuts to Sb − 1 (HUD numbers Sb 15, speed digits 2).
  2 strings carry game flag 2 with a label.
* Game flag 0x10 (cFEngGameInterface::RenderObject colour override): the console button glyphs and their labels of ~70
  packages (Cross / Triangle / … images and the strings beside them).
* Message responses used: 0/1/2/3/256/257/259/513/515/768/769/1280/1281 (round 8); 258, 260-263, 512, 514, 516, 704-706 dead.

### 13.2 Script update — FEPackage::Update / UpdateObject / UpdateObjectTracks (PC 0x5C1B30 / 0x5C17E0 / 0x5BEC90)
* Update order = a pre-order walk of the object tree (roots in package order, groups recursing, UpdateGroup 0x5B1670); was the
  flat package list (only the order of same-frame messages changes).
* UpdateObject literal: CurTime += ticks (≥ 0); ≥ Length: chain (old tracks at the overflow time, old events to the end,
  SetCurrentScript(chain) + SetupMoveToTracks, CurTime = overflow, chain events from 0), else Flags & 3: 0 events to the end
  and Length + 1; 1 loop (events, wrap, events from 0, SetupMoveToTracks); 2 ping-pong (modulo 2·Length, **no events**); 3
  nothing. Below Length the events by play action (2: `prev < len` or the two-part issue). Then the tracks — skipped when the
  script was already finished (prev == CurTime == Length + 1) **and FF_DirtyCode is clear**. The port's earlier version
  issued events in the ping-pong end branch and always skipped finished scripts.
* FF_DirtyCode (Node.DirtyCode) is cleared after every object's update (FEngine.cpp:309: uHoldDirtyFlags = 0 while the FE
  renders every frame). It is set by the exe-faithful setters below; [deviation] not by FEngSetVisible / FEngSetInvisible
  (the exe sets it there too), because screens of other owners still write node data directly, which a re-application of
  a finished script's tracks would overwrite.
* UpdateObjectTracks / FEKeyInterpFast: unchanged (colour track first, others while alpha ≠ 0, static tracks once).
* IssueScriptMessages (0x5BC530): unsigned time compares, the sorted scan; target 0xFFFFFFFF goes through
  FEngine::SendMessageToGame (0x5B0BB0) — **delivered at once**, not queued (now `GameMessage` is invoked during the update;
  [deviation] queued when the screen has not hooked GameMessage yet, so INIT-time events still reach it); 0x1B3909AA with
  target 0 sets the current button to FindObjectByGUID(0).

### 13.3 FEObject (FEObject.cpp; PC 0x5B7C30-0x5B8430, 0x5BC160, 0x5C31A0)
* `FEObject::SetScript(script, false)` (0x5B8400) and FEngSetScript: a script that is not current becomes current
  (SetupMoveToTracks) **at time 0**; the current one is left alone; start_at_beginning then resets the time (no
  SetupMoveToTracks). `FEView.SetScript` follows it; before, a non-restart switch resumed a remembered time [ours]
  (`Node.ScriptTimes`, removed) and a restart of the current script re-ran SetupMoveToTracks. `Play` stays MR_SetScript
  (SetCurrentScript + CurTime 0 always). FEngIsScriptFinished → `FEView.IsScriptFinished` [?: one time per object].
* **SetTrackValue** (0x5B7DE0 / 0x5B7EE0 / 0x5B7FE0): code that moves / recolours an object writes the **base key** of every
  script's track for that field (FEScript::FindTrack 0x5B2910 by LongOffset) and clears its 0x80 bit, then the data. The
  package tracks are shared by all views, so the base keys are per node (`Node.BaseKeys`, read by `FETrack.Evaluate(t,
  firstDelta, baseKey)` and by SetupMoveToTracks). New API (FEngEngine.cs): `SetPosition` (dirty unless within 1e-6),
  `SetColor(r,g,b,a | 0xAARRGGBB)` (dirty unless within 1; FEColor(u32) 0x5B2D50: a = bits 24-31, r 16-23, g 8-15, b 0-7),
  `GetColor`, `SetRotation` (always dirty; relative = base · q), `SetRotationZ` (FEngSetRotationZ 0x514E20),
  `SetSize` / `SetSizeX` (FEngSetSize: data directly + dirty — a size track of the current script wins, as in the exe),
  `GetSize`, `Get/SetTopLeft`, `Get/SetBottomRight`, `Get/SetCenter` (FEngInterfaceFEObjects.cpp: boxes pos ∓ size/2,
  strings by the justify offsets of Size · text extent), `SetMultiImageRot` (FEngSetMultiImageRot 0x514FB0: PivotRot.z
  directly + dirty), `SetMultiImageUVs(layer, …)` (FEMultiImage::SetUVs: layer 0 = the first mask's UVs, dwords 21/22/27/28),
  `SetUVs` (image UpperLeft / LowerRight via SetTrackValue), `SetTextureHash` (FEngSetTextureHash: same hash ignored,
  visibility untouched; an overload takes a texture the FE library does not hold).
* Clone (copy ctor 0x5C31A0 + FEPackage::AddObject 0x50DB90): the clone goes to the **package root** (no parent), copies the
  scripts (current script and its CurTime, base keys) and flags, gets a new GUID; the exe zeroes the data and uiCredits
  copies the prototype's FEObjData over it — folded into `FEView.Clone` (data copied).
* `FEngSetVisible` (0x514CC0) skips objects with flag **0x40** on PC (the GC decomp tests 8; `SetVisibleTree` used 8).

### 13.4 Strings (FEString.cpp; FEngInterfaceFEStrings.cpp; PC DoFEngPrintf 0x515D20, FESetString 0x515CC0)
* `FEView.Printf` = FEPrintf: the text is cut to Sb − 1 characters when Sb < 0x400, set, flags |= 0x400002 (dirty + 2 =
  literal); a group prints into every string below it (FEngGroupFEPrintf 0x515DB0). `SetText` stays (no cut: FESetString).
  `SetLanguageHash` = FEngSetLanguageHash (label, flag 2 cleared). Package strings with flag 2 now show their literal text
  (RenderString 0x5A4390 uses the label's localisation only without flag 2). Screens of the menus owner may move their
  FEPrintf calls from SetText to Printf to get the Sb cut.

### 13.5 Rendering (FEngRender.cpp / FERenderObject.cpp / FEGameInterface.cpp)
* MakeRenderMatrix colour (0x5848B0): an object inside a group gets (context · own + 128) >> 8 per channel, the context
  being the group's composite clamped to 0..255 (GenerateRenderContext 0x585320); root objects their own colour; then
  FEngColorToEpolyColor (rgb / 2) and fe.efx (× 2) — the rgb lose their lowest bit. Was float parent × own / 255.
* FEngine::RenderGroup (0x5BB9C0) / RenderObject (0x5B7690): an object or group with data alpha 0 is not drawn (a group's
  subtree skipped) — now also for coloured images, which draw only their vertex colours (whose rgb also lose the low bit).
* cFEngGameInterface::RenderObject (0x516630): objects with game flag 0x10 that reach rendering get their colour replaced every
  frame by the game mode's: gNormal 0xFFE6E6C8 (front end), gTint 0xFFFFAF41 (in game: GRaceStatus exists), gRapsheet
  0xFFAAE646 (rap-sheet mode, FEDatabase FEGameMode 0x200) — iGameMode is set when a package loads (PackageWasLoaded
  0x525520; static inits 0x8863F0 / 0x886400 / 0x886410). `FEView.GameMode` [?: "GRaceStatus exists" = FEMenuScreen.InGame].
* RenderString (0x5A4390): MaxWidth without word wrap and a first line wider than it → the string is squeezed horizontally by
  MaxWidth / width (FEText). The Korean / Chinese ×2 scale of one font: language-specific, the port runs English.
* Clipping: GenerateRenderContext (PC 0x585320) always leaves the context's clip object null (its child loop is empty), so
  MakeRenderMatrix never returns clip info and FERenderObject::ClipGeneral / ClipAligned (0x582D20 / 0x584800) never run —
  dead on PC.
* Non-power-of-two textures: the exe scales UVs by size / next_pow2 because D3D pads the texture; Unity holds the unpadded
  image, so plain 0..1 UVs are the same picture. Textures the FE library does not hold (the minimap chops) register their
  TPK record for the blend state (`FEView.RegisterTextureSource`).
* [deviation, unchanged] the matrices are decomposed onto RectTransforms (position, Z angle, the image's own full quaternion,
  sign flips): a group's own X/Y flips and shear from a non-uniform group scale under rotation are not reproduced.

### 13.6 Dead / N-A in the remaining FEng files (with proof)
* FEKeyTrack key editing — InsertKey 0x5B5900, CreateKeyAt 0x5C0830, SetKeyValueAt ×5 (0x5C0980-0x5C0B70), GenerateNewKeyAt
  0x5C1F20 and the thunks 0x5C1BC0-0x5C1C20: no call, jump, push or relocation reaches GenerateNewKeyAt or the thunks (the
  editor's API); ComputeSize is empty; FEKeyNode / FEScript / FEMessageResponse pools, FEResponse string params,
  SetCount / Allocate / PurgeResponses: memory management (C# lists).
* FEButtonMap wrap modes: FEngine::WrapMode (PC +0xCC) is written only by the FEngine ctor (0x5C13F1, = 0); SetWrapMode is
  stripped → GetButtonFrom always runs pass 0 only (the port's ButtonFrom). ComputeButtonLocation = the object's position
  through its group's context transform (`Node.AbsPos`).
* FEMsgTargetList FECountMsgs / FEBuildMsgs (building lists at runtime): every package ships its Targ chunk.
* FEGroup::FindChildRecursive / ForAllChildren: tree walks (`Descendants`); the copy ctor with children: no group is cloned.
* FEScript::SetName / FEObject::SetName / FEPackage::SetFilename: names are not loaded (hashes only).
* FEPackage mouse offsets (BuildMouseObjectStateList / AddMouseObjectState / UpdateMouseObjectOffsets / OffsetCalculatron) feed
  FEngine::UpdateMouseState (FEngine.cpp, not this round's file): the port's mouse test uses the laid-out rectangles.


## Round 9 (2026-10-25) — FE menus: SMS, tuning, controller unplugged, sound hooks
* **SMS_MailBoxes — uiSMS** (Setup 0x548AE0, RefreshHeader 0x531AD0, NotificationMessage 0x5534F0; PC = decomp) →
  `SmsInboxScreen : FEMenuScreen`: ArrayScrollerMenu 1×6 (MESSAGE_GROUP_n / MESSAGE_TEXT_n by language hash /
  NEW_MESSAGE_ICON_n), the selected group is the current button (cursor 0x07890734 via the groups' HIGHLIGHT → POSn); valid
  READ / UNREAD messages oldest first; indicators 0x19161CCC / 0x0D6FD6F9 (HIGHLIGHT shown tab, SHOW other, else HIDE);
  left / right flip the tab; BUTTON_PRESSED → handler 0x47FF4E7C READ → EXIT_COMPLETE at 500 ms → SMS_Message; key 1 → InGameDialog
  Yes 0x70E01038 / No 0x417B25E4, blurb 0x8C3C2171, No first (Yes → 0x8CB81F09, flags 0, Setup); back → QueuePackagePop(0);
  0x775CE5DF → Setup; the Arg message (EShowSMS) auto-plays on the first SCREEN_TICK. Memcard auto-save branch N/A.
* **SMS_Message — uiSMSMessage** (ctor 0x553810, Setup 0x549150, NotificationMessage 0x531D40, dtor 0x511EF0): CTextScroller
  (`TextScroller.cs`) over MESSAGE_TEXT_1..10, scroll bar "scrollbar"; INIT_COMPLETE: voice + not-played flag → SMSCellCall
  (0x531DDA), flags = READ; PAD_BUTTON4 → voice: ClearPlayback + SMSCellCall (0x531DAE); PAD_BUTTON5 → delete dialog (Yes →
  PAD_BACK to the package → BackDelay → 0x775CE5DF broadcast → pop); no accept handling; text messages hide 0x2A631207 /
  0x914614E5. Sound hooks (audio agent): ctor ESndGameState(13, on), FEUpscreen off, FESmsMessage on (0x553892); dtor reverse
  (0x511F3A); SoundPause(eSNDPAUSE 0xB) and the cop-speech ClearPlayback have no port API [?].
* **Other sound hooks**: FEMovieScreen (boot movies) ESndGameState(1) + SNDSTATE_FMV on in the ctor (0x58ED07 / 0x58ED1A), off
  when it ends (0x57DE24) — in MoviePlayer for MovieScreen.Boot; PhotoFinishScreen ESndGameState(7, on) when the photo shows
  (0x5A86AD, the SCREEN_TICK branch that sets the handler's `call`), off in the dtor (0x57D91C); LoadingScreen 10 / SNDSTATE_OFF
  (fe_safehouse.md §17.3). MenuScreen::BaseNotifySound (0x5724D0) now drives FEng-engine screens' sounds (fe_safehouse.md §17.6).
* **Pause_Performance_Tuning — CustomTuningScreen** (create 0x5A9600, ctor 0x5A8FE0, Setup 0x5A6060, NotificationMessage
  0x5A9910, SetSlidersForType 0x589F10, AddTuningSlider 0x594090, IsTuningAvailable 0x593F20, Show / HideHelpBlurb 0x5A61E0 /
  0x589FC0, StoreSettings 0x576170, SettingsDidNotChange 0x5762C0; TuningSlider 0x575B60-0x575F6F, ctor 0x589DB0) rebuilt on the
  FEng engine: UIWidgetMenu with iMaxWidgetsOnScreen 8, no masters in the package (SetInitialPositions skipped; TuningSlider::
  Position is empty → rows keep the package positions); 7 rows (induction name / help by the level-1 induction type: turbo
  0x86945521 / 0xB5DCBFA9 / 0xB7D6F7B0, else 0xE3A932E0 / 0xD70F7468 / 0xE3F577AF); per row OPTION_NAME_n (language hash;
  UNHIGHLIGHT + SLIDER_GROUPn INIT when available, else GREY / GREY), OPTION_DATA_n "%2.1f" (absent on PC), cSliders
  LEFT_SLIDER_n (InitValues(min, mid, 0, mid + min − cur, −82)) / RIGHT_SLIDER_n (InitValues(mid, max, 0, cur, 82)) — BASE_ =
  the 82×6 bars, FILLBAR_ = the LED images; limits ±1 (TuningLimits 0x8A9350), step 0.2; unavailable rows selectable but inert;
  up / down ScrollWrapped (cursor POS1..8), L / R triggers cycle the setups (0x05CDDED4 ← 0x40230063..65), key 1 help (SHOWHELP
  0x89D332A9 / HIDEHELP 0x950AD1C2, CTextScroller HELP_TEXT_LINE1..7, scroll bar "Scrollbar"); accept stores all 3 setups + the
  active one (ETuneVehicle) and leaves (0x587C018B); back: unchanged → leave, else InGameDialog Yes 0x70E01038 / No 0x417B25E4
  "0xE9CB802F" (Yes 0x775DBA97 leaves); EXIT_COMPLETE → Pause_Main (start → EUnPause).
* **ControllerUnplugged** — see fe_safehouse.md §17.5. FadeScreen (FeFadeScreen.cpp): only toggles FEManager's controller-error
  suppression on 0x83323AEB / 0xC7D61AC7 [not wired: the port's fade is the story owner's StoryCard].
