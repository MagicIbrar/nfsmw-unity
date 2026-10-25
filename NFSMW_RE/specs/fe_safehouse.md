# Front end: main menu, safehouse, quick race, car select, My Cars, customize entry, options, loading (NFS MW 2005 PC v1.3)

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/Frontend/` (paths relative to it), speed.exe v1.3 read with the
disassembler / REA-Ghidra where the decomp is the GameCube build (marked **PC**), `LANGUAGES/English.bin` (hashes
resolved below), `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`. Conventions (hashes, messages, scripts) as in
`fe_career_screens.md` §0. Companion: `customization.md` (shop rules), `career_flow.md` (story).
Unity: `NFSMWUnity/Assets/NFSMW/Runtime/UI/Safehouse/*.cs`, `Core/FEDatabase.cs`. Harness check: `fesafehouse`
(every object / string hash used below is present: 173/173 strings; the only missing objects are optional ones the exe
also tests for null — Options.fng has no category icon 0x08007B4C, OptionsPCDisplay has no LEFT_ARROW_13 and uses
LEFT_ARROW_0, Car_Select has no CARNAME shadow, Loading has no blinker 0x06D91704).

## 0. Screen factory and the shared packages

The PC screen table is at **0x8F4320** (`{package, CreateFn}` pairs; GameCube table in FEPackageData.cpp:414). PC rows
the decomp lacks: `OptionsPCDisplay.fng`, `Pause_PCDisplayOptions.fng` → CreateOptionsScreen; `Options_PC_Controller.fng`,
`Pause_PC_Controller.fng` → CreatePausePCController; `UI_PC_LAN*.fng`, the `OL_*` online screens, `DiscErrorPC.fng`.
`MainMenu_Sub.fng` is one package with five screen classes (CreateSubMenu, FEPackageData.cpp:116) chosen by the
FEGameMode bits: OPTIONS → UIOptionsMain (UIOptionsTrailers for OC_TRAILERS); CAREER → uiCareerCrib (the safehouse);
CAREER_MANAGER → uiCareerManager; MODE_SELECT → UIQRModeSelect; QUICK_RACE → UIQRMainMenu; else UIOptionsMain.
Packages are *switched* (replace) almost everywhere; the port uses `FE.Replace` for switches and `FE.Push` where the
caller expects to come back (dialogs, sub screens opened from a screen the QA tour pushes).

Loading packages (`Loading`, `Loading_Controller`, `Loading_Tips`, `PC_Loading`) are in `GLOBAL/THINSCREEN_GLOBAL.BUN`
(widescreen twins in WIDESCREEN_GLOBAL.BUN), which FrontEndData does not read: `FEDb.EnsureLoadingPackages()` adds them.

### 0.1 PC keys (from the packages' PC_key_* prompts)
| key | message | used by |
|---|---|---|
| Enter | PAD_ACCEPT / BUTTON_PRESSED | everything |
| Esc | PAD_BACK | everything |
| 1 | PAD_BUTTON5 | main menu Game Stats, Car_Select Sell Car, My Cars Delete Car, CustomizeMain Backroom, options / track options Defaults, cart Clear All, Quick Play re-roll |
| 2 | PAD_BUTTON4 (options video: message 0x9CDD83CA) | Car_Select add impound box, cart Select (toggle), CustomizeCategory Max Performance, OptionsPCDisplay Advanced/Basic, MainMenu_Sub View Tutorial |
| 3 | PAD_BUTTON0 | Showcase (not ported) |
| 4 | PAD_START | View Cart (CustomizeCategoryScreen :1288 → ShowShoppingCart) |
| Q | PC quit | main menu |
MenuInput (FrontEnd.cs) has Accept/Back/Key1/Key3/Delete; `PcKeys` (FEDb.cs) reads 2, 4, Q, PgUp/PgDn (triggers).

## 1. FEDatabase (Database/FEDatabase.cpp, RaceDB.cpp) → `Core/FEDatabase.cs`, `Safehouse/FEDb.cs`

* **eFEGameModes** (hpp:22) bit flags; Set/Clear/Reset as the decomp (`FEDb.SetGameMode` …).
* **PlayerSettings** (FED:67): Gauges, Position, LapInfo, Score, Leaderboard, TransmissionPrompt, Rumble, DriveWithAnalog
  all on; Config 0; SplitTimeType 0; CurCam 0; Transmission 0 (Auto); Handling 1. DefaultFromOptionsScreen keeps analog,
  config and rumble.
* **GameplaySettings** (FED:138): AutoSave, Rearview, Damage, JumpCam on; RacingMiniMap 1 (rotating), ExploringMiniMap
  0 (static); MapItems all; LastMapZoom 1, LastPursuitMapZoom 2, LastMapView 0; HighlightCam 127.5; SpeedoUnits = 0
  (mph) for English, 1 (km/h) otherwise.
* **AudioSettings** (FED:187): Master/Speech/SFX/Engine/Car/Ambient/Speed 1.0, FE music 0.8, IG music 0.8,
  AudioMode 1 (WIN32), InteractiveMusic 1, EATrax 1, PlayState 0. WIN32 compares with memcmp.
* **RaceSettings** (RaceDB.cpp:30) ×11 (by GRace::Type): opponents 3, AI skill 1, cop density 1, traffic 1, catch-up
  on, cops off, direction 0, laps 2, region filter 3 (all); DefaultRaceSettings (FED:838) then P2P / Drag / SpeedTrap
  laps 1, tollbooth opponents 0 and laps 1, knockout laps = opponents. `GetQuickRaceSettings(NumTypes)` = the current
  RaceMode's record (FED:979). FillCustomRace (FED:1016): catch-up, cops (heat = cop density when on), difficulty =
  AI skill, laps, opponents, traffic 0/10/30/50 % for density 0..3, reversed = direction 1.
* **GameCompletionStats** (FED:1366): career = (career races + milestones done) / (all), challenge = completed /
  total Challenge Series races, rap sheet = Σ(15 − pursuit rank) / 140, overall = 0.7 / 0.2 / 0.1.
* **PC display block** — 33 dwords at **0x9017B0** (current) and the same layout at **0x901838** (capability maxima,
  filled from the D3D device at 0x6D2130). Registry names from the load at 0x6C1C80; the widgets index the block
  directly (offset = field × 4):

| field | offset | registry | widget (title) | kind |
|---|---|---|---|---|
| 2 | 0x08 | g_CarEnvironmentMapEnable | Car Reflection Update Rate (0xF2E2D84A) | slider 0..caps[2], enabled if caps[3] > 0 |
| 3 | 0x0C | g_CarEnvironmentMapUpdateData | Car Reflection Detail (0xBDE7DACF) | Low/High, enabled if caps[2] > 0 |
| 9 | 0x24 | g_RoadReflectionEnable | Road Reflection Detail (0x9032DF3F) | slider |
| 11 | 0x2C | g_MotionBlurEnable | — | |
| 15 | 0x3C | g_ParticleSystemEnable | — | |
| 17 | 0x44 | g_WorldLodLevel | World Level Of Detail (0xA271A8E9) | slider |
| 18 | 0x48 | g_CarLodLevel | Car Geometry Detail (0x5F560D76) | Low/High |
| 19 | 0x4C | g_OverBrightEnable | Over Bright (0x70A7555E) | On/Off |
| 22 | 0x58 | g_FSAALevel | Full Screen Anti-Aliasing (0xE7A04885) | slider |
| 24 | 0x60 | g_RainEnable | Rain Effect (0xD41A8B13) | On/Off |
| 26 | 0x68 | g_TextureFiltering | Texture Filtering (0xFD068897) | slider |
| 27 | 0x6C | g_RacingResolution | Resolution (0xC96D57C9) | 640x480 … 1600x1200 (table 0x93DBEC, unavailable entries skipped, wraps) |
| 29 | 0x74 | g_VSyncOn | Vsync (0x6CEB9CB6) | On/Off |
| 30 | 0x78 | g_VisualTreatment | Visual Treatment (0x1DE776AA) | Low/High |
| 32 | 0x80 | g_ShadowDetail | Shadow Detail (0x1052ED5B) | slider |
On/Off = 0x16FDEF36 / 0xF6BBD534, High/Low = 0xCE32B239 / 0xF6BBC9AB (chosen branch-free: `neg; sbb; and; add`).
Sliders are `SetSliderValues(0, caps[f], 1, value, 160)` (range 160). g_PerformanceLevel (**0x982C00**) is the "Level Of
Detail" slider (0x05A66554), 1..5. **Preset sub_6C1990** (exact, harness `fesafehouse` prints it): resets the block
(res 0, env 0/0, road 0, particle 1, world 0, car lod 0, rain 1, motion blur 1, fsaa 0, filtering 0, vsync 0, VT 0,
shadow 0, …), then level > 1: world 1; > 2: env map 1, filtering 1, road 1, world 2; > 3: env update 1, resolution 1,
fsaa 1, VT 1, shadow 1, overbright 1; ≥ 4: env update 2, road 2, car lod 1; VT 1 whenever caps VT ≠ 0; less than 400 MB
RAM zeroes world / car lod. sub_6C1B60 = preset + ATI FSAA fix + sub_6C1780 (clamp to caps [?]). Initial .data values
are in `PcVideoSettings.ExeInitial`.

Port: everything above is in `FEDatabaseData` (JSON per profile slot, `nfsmw_fedb_<slot>.json`), plus the quick race
"My Cars" list. `FEDb.Apply` pushes speedo units (Settings.Kmh), IG music volume (Settings.Music), v-sync, FSAA
(QualitySettings.antiAliasing 0/2/4), texture filtering (anisotropic off / on / forced) and, on the video page's
accept, the racing resolution (Settings.Apply). **Hooks for other owners**: SFX / car / speech / FE music volumes,
mini map modes, rearview, damage, jump cams, player HUD switches, transmission, drive camera and the display detail
switches are stored in `FEDb.Data` but their systems do not read them yet.

## 2. Boot splash and press start

* **MW_LS_Splash** (Loading/FESplashScreen.cpp, PC create 0x5A4710): HD group 0x534CC377, startclick 0x13CF446D,
  mouseclick 0x8C0BD743 hidden; licence blurb 0x9B580A55 shown; Enter/Start continue after INIT completes →
  BootFlowManager → MainMenu.fng; 30 s idle → attract movie (absent in the PC install). Port: `PressStartScreen()` now
  shows MW_LS_Splash (it showed PressStart.fng).
* **PressStart.fng = uiQRPressStart** (quickrace/uiQRPressStart.cpp): the split-screen "Player %d press START."
  (0xCF538E1C into 0xB244CF71) before each player's Car_Select; Back → player 1's Car_Select or the track screens.
  Port: `PressStartScreen(int player)`.

## 3. Main menu — UIMain (Safehouse/uiMain.cpp; PC Setup **0x544E50**, Notify **0x559FC0**, UpdateProfileData 0x528BC0)

Options (tex, name): Career (0x03704F3D, 0x5815A2B5), Challenge Series (0x9A962438, 0xCC8CB746), Quick Race
(0x4E6FBB02, 0x54020A7A), My Cars (0xB0C46023, 0x1AFD5BE6), **LAN Play** (0xD13A2E66, 0xD6DB74DE), **Online**
(0x771849A5, 0x74FB90A8), Alias Manager (0x6B303856, 0xBCB18F38 — IsMemcardEnabled, 0x8F8708 = 1 on PC), Options
(0x3058FE37, 0x19A8C0AF). Title 0xB24AAE58. Mike Mann demo build: Challenge, Quick Race, My Cars, Options only.
EXIT_COMPLETE after a press: career / career manager / quick race / options → MainMenu_Sub; profile manager →
MC_ProfileManager; challenge → ChallengeSeries; customize → MyCarsManager; LAN → **UI_PC_LAN_ServerSelect**; Online →
dialog then the EA login. Key 1 toggles GAMESTATS_APPEAR / GAMESTATS_LEAVE with a profile. UpdateProfileData: with a
profile GAME_COMPLETED_DATA, CAREER_COMPLETED_DATA ("%d%%"), RAP_SHEET_ITEMS_DATA_1 hidden, _DATA_2 rap %,
CHALLENGE_SERIES_DATA_1 "%d/%d", _DATA_2 %, name into 0xB514E2D8, NAME_GROUP and GAME STATS GROUP visible.
Port: `MainMenuScreen` exactly this list. Deviations: Challenge Series opens the port's RaceListScreen(Challenge)
(ChallengeSeries.fng / uiQRChallengeSeries not ported: needs the races' LocalizationTag); LAN opens LanMenuScreen;
Online and Alias Manager show a notice; Q asks to quit (text ours). Removed (not in the exe): Free Roam, Split Screen
(now under Quick Race), Quit as a menu item.

## 4. Career — uiCareerManager / uiCareerCrib

* **uiCareerManager** (career/uiCareerManager.cpp; PC Setup 0x532890): started career → Resume Career (0xC1C089CE,
  0xE072DB21; not when game over) + Start New Career (0xE7353BE7, 0x17E18F87), else Start Career (0x6005281E); Load
  (0x2287E063, 0x0018ECFF). Title 0x8FFF61F2. Profile name in 0xEB406FEC (0xC87422F7 SHOW). Back → MainMenu.
  Resume → ResumeCareer then the safehouse (IG_SafehouseMain). New over a career → memcard op 0x80063 first.
  Port: `CareerManagerScreen`; Resume returns to the crib when the world is up, else GameFlow.StartCareer; New asks a
  yes/no (the memcard text is not reproduced); Load re-reads the save. Back with the world loaded = QuitToMainMenu.
* **uiCareerCrib** = the safehouse (career/uiCareerMain.cpp; PC Setup **0x532220**, reacts 0x532080-0x5321C0): Resume
  Free Roam (0x12BB5EA2, 0x01BD185C; react-immediately, asks 0xEB694C0C, yes → StartCareerFreeRoam), Blacklist
  (0x2C14AC23, "Blacklist 15" 0x80B9FF9B → **SafehouseReputationOverview.fng** on PC; GC went to WorldMap_Main), Car
  Select (0xC6A1A6E0, 0x00D5F627 → **Car_Select.fng**; GC: IG_CarLot), Rap Sheet (0x2FD8B206, 0xAC22F27E, only with
  CareerSettings flag 0x10 → **RapSheetLogin.fng**), Save (0x228B7E32, 0x001C8ACE; memcard 0x2251). Title 0xE596C4A3.
  PLAYER_MASTER SHOW, CAREER_DATA %, BOUNTY_DATA, CASH_DATA. Back → career manager.
  Port: `CareerMenuScreen` (was a scroller of 11 port options). HasRapSheet [?] = any pursuit / bust recorded.

## 5. Quick race

* **UIQRMainMenu** (PC 0x7AA730): Quick Play (0xE6313967, 0xB5E8F82F → Quick_Race_Brief), Custom Race (0x2A49B5E2,
  0x25BBD4C3 → MODE_SELECT), Split Screen (0xF365B5F5, 0x841D518A → MODE_SELECT, iNumPlayers 2). Title 0xCDF0CC03.
* **UIQRModeSelect**: Circuit, Sprint, Drag, then (not online/LAN) Lap Knockout (not split) and Speedtrap — no tollbooth.
  Title 0x1F203817 / 0x43C825ED (split) / 0x6703B807 (online). → Track_Select; Back → QR menu.
* **UIQRTrackSelect** (Track_Select.fng): list = bins 21, 16..1 (world then boss races) of RaceMode, available,
  not NeverInQuickRace, not D-Day, region filter (0 College 0xA6850651, 1 Coastal 0xA5C20E7D, 2 City 0x8663FAEF, 3 All
  0x632DD19B); locked unless IsQuickRaceUnlocked (19.8.31 always). Header: title by type (0x3DE80A85 …), region,
  n/N, race name, track name TRACKNAME_<id> or the event id, length "%$0.1f KMs/Miles", best time / best speed
  (0x0512E823), "Best Lap:" for circuit/knockout, race icon; locked → 0xBD563BE5 with the rival aka and number.
  Left/right circular, triggers = regions, Enter → Track_Options (split sprint/drag/speedtrap → PressStart with 1
  opponent), Back → mode select. Port: `TrackSelectScreen` (track map streamer not ported; up/down also change region).
* **UIQRTrackOptions** (Track_Options.fng, UIWidgetMenu, 9 rows): per type — circuit: [direction], laps, traffic,
  opponents, AI skill, catch-up; sprint / speedtrap: [direction], traffic, opponents, skill, catch-up; drag:
  [direction], opponents, skill, catch-up; knockout: [direction], opponents, laps (greyed, = opponents), traffic,
  skill, catch-up; tollbooth: [direction], traffic (split screen drops traffic/opponents/skill/catch-up). Ranges:
  opponents 1..4−players, laps 1..8, traffic None/Minimum/Moderate/Maximum, skill Easy/Medium/Hard. Enter → custom race
  → Car_Select (PressStart in split); key 1 defaults (0x8AEF5AE8). Port: `TrackOptionsScreen`; direction never shown
  (GetCanBeReversed has no source in the port [?]).
* **UIQRBrief** "Quick Play" (Quick_Race_Brief.fng): stock cars unlocked in [bin−1, bin+3], all quick-race-unlocked
  races but tollbooth/challenge/19.8.31; 30-tick random spin of car, track and GetRandomRaceOptions (FED:995: catch-up,
  cop density rnd 4, skill 1, opponents 1..3, laps KO = opponents / circuit 1..5 / else 1, traffic rnd 4, direction 0);
  Enter races with NOS maxed (random paint/parts not ported). Port: `QuickRaceBriefScreen`.
* Starting: `QuickRaceStarter.Custom` copies the RaceEvent (reflection) and applies laps, difficulty (0/50/100 →
  the 33/66 buckets), traffic %, opponents (truncated list), cops off, reversed flag [?: route not mirrored]. Catch-up
  is stored in `QuickRaceStarter.Current` (AIRacer.CatchUpOn not wired — hook).

## 6. Car_Select — UIQRCarSelect (quickrace/uiQRCarSelect.cpp; PC 0x7A3400-0x7C32E0)

One class for: car lot (CAR_LOT mode; D-Day first-time lot), career Car Select, quick race (player 0/1), My Cars add-car
(customize mode), online. Lists = FE car records by filter: 1 Stock 0xD9D6B954, 2 Career 0xEE053562, 4 My Cars
0x2414DE28, 8 Bonus 0xD8A058F7 (16 preset, 32 debug); up/down cycle Stock → Career → My Cars → Bonus only in quick
race. Locks: career = frontend UnlockedAt ≥ CurrentBin; quick race adds RX8, Impreza, Mustang, SL500, 997S, IS300, GTI,
Gallardo, Cobalt, Punto always; bonus BLn when CurrentBin ≤ n−1, E3 BMW / C6R after the career, SL65 special
challenge, GT2 the whole Challenge Series, Castrol GT awarded, CE cars on the Collector's Edition. Sort by UnlockedAt.
RefreshHeader: list name/texture (0xAA9834BC/0xE3B271B8), lock box 0xD0F7C7CC + 0x4EF2A115 "Unlock this car by
beating %s, Blacklist member %d." (UnlockedAt+1 / bonus rival), n/N, car name strings hidden (logos only), car lot
cash 0x1930B057 / cost 0x20C83C31, "Exit" / "Buy Car", career bounty / fines (or impound release cost / pending),
impound markers. The car loads 0.5 s after the last move. Accept: car lot — cash 0x40FA955D, > 9 bought 0x41030A1B,
D-Day yes/no 0x74317CBC, else Drive Now / Deliver / Cancel (blurb 0x8C451EBA); career — impounded → release, else keep
and back to the crib; customize — create the custom car → MyCarsManager; quick race — transmission dialog (0x6F5401D1,
Manual 0x317D3005 / Auto 0x8CD532A0, current first) unless drag or the prompt is off, split p0 → PressStart(1), else
StartRace. Key 1 sell (career): impounded 0x80E4F27C, last car 0x9A772BD6, else 0xB4A40135 for cost/2. Back: D-Day
none, car lot → free roam, career restores the original car (impounded → 0x630931B6), customize → My Cars, quick race
→ Track_Options. Port: `CarSelectScreen` (+ `CarLotScreen`, `NewCareerScreen`, the port's `QuickCarScreen` pick).
Deviations: max cars 10 (was 25 in the port); stats sliders, showcase, impound states and the busted manager are not
ported; sort direction [?] (descending UnlockedAt so unlocked cars lead).

## 7. My Cars — MyCarsManager (customize/MyCarsManager.cpp; PC 0x7A7380)
The main menu's quick race garage, not the career cars: 5×2 grid (CAR_ICON_1..10), cars of filter 0xF0004 plus "Add new
car" while < 20 (0x29AE0714, icon 0x91D3A6BA); selection = every race type's quick race car; 0xBFA25765 "Add New Car" /
"Customize Car"; Enter → BeginCarCustomize(CEP_MAIN_MENU) or Car_Select (customize); key 1 delete (0x4F68196E) with
NotifyDeleteCar; car load 0.25 s; Back → MainMenu (save if dirty). Port: `MyCarsScreen` over `FEDb.MyCars`.

## 8. CustomizeMain (customize/CarCustomize.cpp:1881-2082)
Parts (0x6E0CA66C), Performance (0x3987D054), Visual (0x3E31BA56); backroom icons 0x73272ED2 / 0xC61C8D3A / 0xE69D4F7C;
hero car: Visual only. Key 1 switches rooms in the career (warning 0x003B3E83 when induction / NOS markers are
unusable). Key 4 cart. Back: career front room → free roam; backroom → front room; else back to the caller. Title
"customize" 0x1F242E03 / "backroom" 0x92FCDBF0 lower-cased. Max Performance is key 2 in the performance category
outside the career (confirm 0x892CB612) — the port puts it on PerformanceScreen (it has no performance category
screen). Port: `CustomizeScreen` (dropped the port's Backroom / Max Performance / Shopping Cart / Done options); cart
key 4 in Paint / Performance / Parts, cart toggle key 2. Rest of the shop: customization.md.

## 9. Options

* **UIOptionsMain** (PC Setup **0x528F30**, Notify **0x545310**): Audio (0xF37AF144, 0xE76CD783), Video
  (0x8A006328, 0xE8E24508), Gameplay (0x4DF98FB2, 0xD0CF6EE1), Player (0xD708EFEF, 0xF760EABE), Controls (0xA04A7B26,
  0x04DC6DB5), [Online 0x5A5E5137 in online mode], and outside the pause menu EA Trax (0xC52CCBF6) and Credits
  (0x51009E20). Title 0x4ECA678C (pause 0x1D7BB6C9 on 0x863404B5). PC switches: Video → OptionsPCDisplay
  (pause: Pause_PCDisplayOptions), Controls → Options_PC_Controller (Pause_PC_Controller), Audio / Gameplay / Player →
  Options (Pause_Options), Credits → Credits, EA Trax → EA_Trax_Jukebox. Back → MainMenu (memcard save when dirty).
  Port: `OptionsMainScreen(fromPause)`; Controls, EA Trax and Credits show a notice (not ported).
* **UIOptionsScreen** (PC 0x5102B0-0x54FE69) on UIWidgetMenu (§10). Audio (0x529310): SFX 0xFD487543, Car 0x218E4B08,
  Speech 0x9E5FB82A, FE Music 0x418E681D, IG Music 0xDF21EAC2 (sliders 0..1 step 0.1), Interactive Music 0xA3DBB390,
  EA Trax 0xDCFB6B36 (On/Off), Audio Mode 0x2881AB87 Mono/Stereo/Surround (in the FE). Gameplay (0x529C10): Autosave
  0xD1056C88 (ShouldShowAutoSave), Jump Cams 0xF26A5CBF (not split), Damage 0x1582ADFF, Rear View 0x85A6CE05, Speedo
  0x01E19173 (MPH 0xFBD74FC5 / KPH 0xAF70E736), Exploring map 0xC6269082 (not online/LAN), Racing map 0x9FA5EC9E
  (Static 0xF75595F2 / Rotating 0xF4B00E99 / Off). Player (0x529F10): Transmission 0xD31407E7 (not in a drag race),
  Drive Cam 0xF6CCDC5F (5 labelled cameras), Gauges, Position, Split Times (On ↔ Off = 0 ↔ 4), Score, Leaderboard
  (not drag / tollbooth). Headers 0x3932C2E4 / 0x48478029 / 0x01CCE8C2 / 0xC055165F (pause variants).
  **PC video** (SetupVideo 0x5295F0): basic page Level Of Detail + Resolution; advanced page (flag +0x154, key 2,
  button text 0xCE757C8A = "Advanced" 0x1459065C / "Basic" 0x9C88B5C8): Resolution, FSAA, Texture Filtering, Vsync,
  World LOD, Road Reflection, Shadow, Car Geometry, Car Reflection Detail, Car Reflection Update Rate, Rain, Over Bright,
  Visual Treatment. The page edits a copy (+0x144 block, +0x148 level). Level change (msg 0x026DED57) sets resolution
  = level > 3 ? 1 : 0. Enter with changes (0x545817): sub_510300 applies (copy live; basic page + new level → set
  0x982C00, run the preset; racing resolution) then ShowTwoButtonCountdown 10 s "Are you sure you want to accept these
  changes? Changes will be reverted in %d seconds." (0x07770CE7) Yes → keep (0x4D0D83B9) / No or timeout → restore
  (0x85347AE1). Back: unchanged → leave, else 0xE9CB802F yes → restore + leave. Key 1 → 0x8AEF5AE8 → RestoreDefaults
  (gameplay keeps AutoSave when hidden; player keeps analog/config/rumble). EXIT_COMPLETE sets OptionsDirty and goes
  back to MainMenu_Sub / Pause_Main / OL_MAIN.
  Port: `OptionsScreen(category, fromPause)`; `new OptionsScreen()` = the last category (the QA tour and the pause menu
  use it). [?] Enter on the non-video pages leaves keeping the changes (the package's Event Handler object forwards
  accept; the code only handles accept for video). Defaults on the video page use level 3 [?: the exe picks the level
  from the card on first boot].

## 10. UIWidgetMenu / widgets (Common/feUIWidgetMenu.cpp, feWidget.cpp, Slider.cpp) → `WidgetMenu.cs`
Rows: OPTION_NAME_n, OPTION_DATA_n, OPTION_BACKING_n, LEFT_ARROW_n / RIGHT_ARROW_n (else _0), BASE/FILLBAR/HANDLE/
VALUE_SLIDER_n. Geometry from the hidden masters 0xA753C46C (title) / 0xC128B184 (data): FEngGetTopLeft = centre −
size/2; the centre of on-screen row k (1-based) = title top + (k − ½) × title height (24 in Options.fng, matching the
cursor's POS1..POS9 scripts at 24 px steps); titles / values centred in their column (FEngSetCenter centres a string by its
measured box, FEngInterfaceFEObjects.cpp:438); arrows centred at the data column's left and right edge; slider base
top-left = (data centre − width/2, data top + 9.5), fill = range × pct at base + (2, −12). Up/down ScrollWrapped skipping
disabled widgets; window of iMaxWidgetsOnScreen (9 FE, 10 pause); cursor 0x06745352 POSn / HIDE. Focus = HIGHLIGHT on
title/data/arrows/backing (sliders: title + cSlider::Highlight); disabled = GREY. [?] FEngSetBottomRightU on the fill
bar is modelled as cutting the U span with the width.

## 11. Dialogs (Common/feDialogBox.cpp) → `FEDialog.cs`
Dialog.fng: Button1..3 (0xB8A7C6CC..CE) with Button1_Text..3_Text, blurb 0x1E2640FA, header (attention 0x6FD91524 /
in game 0x2BD146D3, information 0xDBE419D4, selection 0x60249A74) and icon (0x6948E2B3 / 0x1A7AFE27 / 0x39949433),
DIALOG_SMALL/MEDIUM/LARGE by blurb length × font height < 2200 / 4400. Back only on dismissable dialogs (two/three
buttons); countdown dialogs press Back at 0. [?] the cursor POSn ↔ button mapping. Note: the in-game agent wrote its own
DialogInterface (UI/InGame/Dialogs.cs) in parallel; the two can be merged.

## 12. Loading (Loading/FELoadingScreen.cpp, FELoadingTips.cpp) → `LoadingScreens.cs`, GameFlow.LoadingScreen
GameTipInfoTable at **0x8F3D78** (28 × {name, bins, category, flags}); tips 0..2 wait for a button (strikeout, engage,
world map); the rest are free-roam (category 0x10), transition-to-game (0x200) tips with bin masks. Text
<name>_HEADER / <name>_DESC, texture = hash(name) (streamed; may be absent). Random choice: bins & (1 << CurrentBin) (or
13-16 before a career from the FE), category & (general | free roam), flags & (to-game | race types).
Port: the loading screen shows Loading_Tips with a chosen tip, else Loading (one package — GameFlow pops only what it
pushed). The specific tests (last car with two strikes, first time in/out of the safehouse) need flags the port lacks.

## 13. What to verify in play / open items
* Cursor ↔ button positions in Dialog.fng, widget row alignment vs the original, slider fill look.
* Car lot list order (SortCarsByUnlock direction) and the D-Day lot contents.
* PC caps (FSAA / filtering / shadow / road maxima) are guesses for a modern card.
* Ported in §14: ChallengeSeries.fng, Showcase, Credits, EA Trax jukebox, PC controller page, the tutorial movie screen.
  Still not: profile manager, online, the track map streamer's animation, car stat sliders, impound states.

## 14. FE remainder round (2026-10-24): the screens that were missing

Engine notes (visibility, tracks, fonts, blend state, pivots) are in fe_ingame.md §10; everything below runs on the FEng
engine (`FEMenuScreen`). Harness: `ferest` (ProbeFeRest.cs); research dumps from the exe are cited by address.

### 14.1 Challenge Series — ChallengeSeries.fng, UIQRChallengeSeries → `Safehouse/ChallengeSeriesScreen.cs`
(ctor 0x7B5630, Setup 0x7AE950, RefreshHeader 0x7A4190, NotificationMessage 0x7AE680; factory 0x8F4368 → 0x570CC0)
* The missing text ids were never in the race data: `GetChallengeHeaderHash` 0x57FDC0 = `CHALLENGE_SERIES_DESC_%02d_HEADER`
  and `GetChallengeDescHash` 0x57FDE0 = `CHALLENGE_SERIES_DESC_%02d` of the race's **LocalizationTag** attribute (0xDB89AB5C,
  GetLocalizationTag 0x5FB990; tollbooth N = 32 + …, pursuit challenges 1..30 and 66..69, burger king 31, CE race 70).
  Goal = **ThreshholdValue** (0x4E90219D, GetChallengeGoal 0x5FBB60), ÷ 60 for pursuit_length / pursuit_evasion_time
  (IsMilestoneTimeFormat 0x56DFF0), printed "%$0.0f" twice into the description (0x7B230D64). Type = StringHash32(MilestoneName)
  (0 for tollbooths and burger king).
* List: bin 19 WorldRaces in array order, minus 99.1.1 and 19.8.31 (easter egg 3 [?: never in the port]); 19.9.70 listed
  (0x8F8718 = 1). ArrayScrollerMenu 4 × 3 over EVENT_ICON_1..12 (HIGHLIGHT / UNHIGHLIGHT / INIT), CHECK_n = LOCK 0x0018ED48
  (locked) / CHECK 0x028FEADD (completed) APPEAR, else HIDE. Locked = !IsTrackUnlocked(quick race) [port: UnlockedQuickRace
  flag or ChallengeUnlocked]; completed = quick-race completed flag [port: or HasWon].
* RefreshHeader: 0x5A856A34 "%d" position, 0x2D4D22C8 "%d" count, 0x0790CE49 ← header hash; type 0 posts 0xB295482E (View
  Tutorial button 12FD7D4A) else 0x0F7B54C7; 0xA018DE49 ← GetMilestoneIconHash (pursuit_length 0x950FCEBC, bounty 0xB4E6456B,
  evasion 0x831B7EBE, cost to state 0x87807869, roadblocks 0xC43959D2, cops damaged 0x88E8DE9E, infractions 0x6784A80E, spike
  strips 0xE621B2EF, cops destroyed 0x0FE608E6, none 0x65818EE8); locked → 0xC5DD9D68 (lock group SHOW) and 0x68215623 =
  MSG 0xCED8931E "…beating challenge %d." (n > 60: n − 1; n % 5 ∈ {1, 2}: 5·(n/5)) or 0xCED8931D "…%d or %d." (n % 5 ∈ {3, 4}:
  5p + 1, 5p + 2; n % 5 = 0: 5p − 2, 5p − 1), else 0x38091FA1 (HIDE); the cash / length lines go to objects the PC package
  lacks (nothing drawn); TRACK_MAP 0x0F365871 ← TRACK_MAP_<event> (UITrackMapStreamer).
* Keys: directions scroll; accept on a locked row → UISND wrong, else 0x77CF03C5 Yes / No (0xD05FC3A3) → transmission
  prompt (TransmissionPromptOn: 0x6F5401D1, Manual 0x317D3005 / Auto 0x8CD532A0) → RaceStarter::StartRace with the preset
  car; back → MainMenu.fng; key 2 (PAD_BUTTON4, type 0 only) → FEAnyTutorialScreen::LaunchMovie("tollbooth_tutorial") with
  0x99344537 hidden until 0xC3960EB9. Triggers are forwarded but unhandled (no paging). MainMenu's Challenge Series entry
  and the QA tour's `challenge_list` now open this screen (RaceListScreen(Kind.Challenge) remains for tests).

### 14.2 Credits.fng — uiCredits → `Safehouse/CreditsScreen.cs` (PC 0x532E90-0x533337)
CREDITS\NA_<LANGUAGE>.TXT (UTF-16, UnicodeFile 0x511FF0-: CR / LF end lines, runs collapse, LineWrap(45) at the last space);
INIT_COMPLETE loads; CREDIT_NEXT 0xE6E946B8 (handler `call` @320 ms, each line's RollCredit @500 ms) clones the CreditsArea
prototype string (`FEView.Clone`) with the next line (looping) and sets RollCredit (5.2 s; RollCredit_ENDGAME 7.2 s after
the game is won); CREDIT_AT_TOP 0x29161540 deletes it on the next tick. Not beat-game: 0xEB4CF244 hidden + 0x8CB81F09;
beat-game: 0x0BF41045 hidden + 0x3111B806. Back → 0x587C018B → EXIT_COMPLETE → MainMenu_Sub (options) [beat-game chain
not ported]. Region fixed to NA_ [?].

### 14.3 EA_Trax_Jukebox.fng — UIEATraxScreen → `Safehouse/JukeboxScreen.cs` (PC 0x52C520-0x55A4AE)
Scrollerina (feScrollerina.cpp) of 4 slots: strings E454E9A5+i "%.2d", 66646FC4+i artist, 2890C8AF+i title, 77BD189E+i
album, F3EBDC4E+i playability (0x9CCE9F86 / 0x5278C50B / 0x5C1B351C / 0x9CCE64C4), backing MOUSE_REGION_<i+1>, scroll bar
"ScrollBar" (`FEScrollBar.cs` = feWidget.cpp:501 FEScrollBar: _Backing / _Handle / _Arrow_1 / _Arrow_2, hidden unless list >
view, handle resized view/list, DIM on the end arrow). Header 0xCA74A2FA = 0x4CA36B89 Sequential / 0xA9C9C8F7 Random
(AudioSettings.PlayState). Triggers toggle the state, left / right cycle playability, up / down scroll (move while grabbed by
key 1), key 2 previews (MControlPathfinder(song.PathEvent) → `MusicSystem.MessageStartPathfinder`), back asks 0xE9CB802F when
changed (yes 0x775DBA97 restores), EXIT_COMPLETE → EATraxInit(-1) (stop the preview), OptionsDirty, MainMenu_Sub.
The playlist (UserProfile::Playlist, JukeboxEntry {SongIndex, PlayabilityField}) is `FEDb.Data.Playlist`, defaulted from the
songs' DefPlay (FE 1, IG 2, AL 3). **Hook (audio)**: MusicSystem keeps its own `_playability`; read `FEDb.Data.Playlist`
and `FEDb.Data.Audio.PlayState` in InitializeEATrax / GenNextMusicTrackID ("EATraxInit").

### 14.4 Showcase.fng → `Safehouse/ShowcaseScreen.cs` (uiShowcase.cpp; PC 0x7A4680-0x7AEAE1)
Key 3 (PAD_BUTTON0) from Car_Select and My Cars. Blacklist number set: title 0x242657CE = 0x3A64DE21 "%d", 0x89D0649C posted,
0xB695A891 "%d", 0x7AC3D0C9 ← BLACKLIST_RIVAL_%02d_AKA, tag art 0xF5A2A087 (RivalStreamer); else 0x3E01AD1D manufacturer
logo, 0xB05DD708 car logo. CURRENT_GEN_WIDESCREEN on widescreen. Accept / back → back to the caller (pop). The car-rotate /
zoom controls of the help bar are the showroom's [?: rotation input not ported].

### 14.5 Options_PC_Controller.fng / Pause_PC_Controller.fng → `Safehouse/ControllerScreen.cs`
UIOptionsPCController (PC only; CreatePausePCController 0x571D00, ctor 0x545EB0, Setup 0x52A7B0, Populate 0x52AC70,
RefreshHeader 0x52AB90, NotificationMessage 0x545F70, capture 0x52AF70 / poll 0x6349B0, assign 0x6285F0, clear 0x628560,
defaults 0x51D6E0 / 0x6284D0, key names 0x628230 via IDirectInputDevice8::GetObjectInfo):
* ArrayScrollerMenu 2 × 8 over the 19 actions of the live table 0x91F418 (keyboard mode 18, joystick adds Pause); slot n:
  KEY_BOX_P/Sn (buttons, HIGHLIGHT → POSn cursor + KEY_TEXT_n highlight), KEY_TEXT_n = bStringHash(action name)
  (GAME_ACTION_GAS 90B41FD0 "Accelerate" … HUDACTION_PAUSEREQUEST A3F20AC4), KEY_TEXT_P/Sn "%s" key name ("---" unbound).
* Accept / click 0x936A6A7F → capture: 0xB2D837B0 ← action, 0x3BBE5E51 ← key, broadcast 0xD707AE8D (popup 1ABDBC33 SHOW,
  text 5FD43704) and 0x13FD3296; the next key assigns (Esc cancels), then 0x9B2BA546 + 0x8CB81F09. Assign rules: secondary
  with empty primary → primary; same key in the other column clears it; other actions lose the key (secondary moves up).
  Key 2 / 0x2DCC19C8 clears (primary takes the secondary); key 1 → 0x8AEF5AE8 Yes / No → defaults (0x8F6D80: Up, Down, Left,
  Right, Space, RCtrl + G, LAlt + X, LCtrl, LShift, R, Enter, B, M, Tab, T, C, L, P, Esc).
* No joystick: device group 8A41F5B9 HIDE, rumble row 9CCF9099 hidden, triggers ignored; 0x4E11CF90 = device name.
  Back → 0x587C018B → EXIT_COMPLETE → Pause_Main (pop) or MainMenu_Sub; start in the pause menu → EUnPause [?: leaves].
* Port storage: `FEDb.Data.KeyBindings` (UnityEngine.InputSystem.Key values), `ControllerScreen.KeyOf(action)`.
  **Hook (input owners)**: PlayerCar / HUD / camera key reads should go through `ControllerScreen.KeyOf`.
  Key names: Windows DirectInput strings for the common keys, else the Key enum name [?].

### 14.6 Settings now applied / exposed
* `HudFeatures.Determine`: PlayerSettings Gauges (0x08000000 + 0x2 / 0x800 / 0x40000 / turbo), Position (0x04000000),
  Leaderboard (0x8), SplitTimeType ≠ 4 (0x10; hud.md's P+0x90 is SplitTimeType in the packed PC struct), GameplaySettings
  RacingMiniMapMode / ExploringMiniMapMode ≠ 2 (0x10000 + 0x4000). Minimap.cs: mode 0 = static north-up map with a turning
  player blip, 1 = rotating, split screen always static (Minimap::Update, FeMinimap.cpp:244).
* `FEDb.ManualTransmission(player)`, `FEDb.DriveCamera(player)`, `FEDb.DamageOn`, `FEDb.MiniMapMode(racing)` are the
  read-outs for the gearbox, camera and damage owners [hooks]. Score / LapInfo have no reader in the HUD code.

### 14.7 Still open
Profile / alias manager (MC_ProfileManager: an icon scroller of memory-card profiles, option_7..11 — the port has one
profile file per slot), Car_Select stat bars and impound states, uiTrackMapStreamer's zoom animation, online screens, the
Collector's Edition showcase art.

## 15. Last-mile round (2026-10-24): Car_Select remainder, impound records, scroller geometry, cheats, options, garage

### 15.1 Car_Select (uiQRCarSelect.cpp) — now ported, including the busted manager
* **Stat sliders** (InitStatsSliders :852, UpdateSliders :859): TwoStageSlider ACCELERATION / TOPSPEED / HANDLING (range 160):
  fill = the selected car's `Physics::Info::EstimatePerformance` rating, handle = the original car's (career: the car the screen
  opened on; D-Day: the same car). The heat meter (0xF2492598) shows the career car's VehicleHeat, only in the career select.
* **QRCarSelectBustedManager** (:40-388, `CarSelectScreen.RefreshBusted`, `FEImpound`): IsImpoundInfoVisible = career, not car
  lot. Impounded car: stamp IMPOUNDED_<LANG> on 0xCE18427D / 0x5B8F2A45, 0x0BC7B91F NORMAL, 0x64F3A49C APPEAR; the accept
  text 0xB94139F4 = "Release" 0x281DEE8A (releasable + cash) / "Use Marker" 0xF9C73CC2 / "Accept" 0x2B65A216. Strike row
  0x75721326: IMPOUND_STATE_1..5 SHOW up to TimesBusted (ANIMATE for a fresh strike not reproduced), 0x5BC78037 / 0x48095518
  (the 4th / 5th box) with the cursor rows 0xF9A5CE86 / 0xEBF0016E POS1..3 by MaxBusted. Accept on an impounded / releasable car
  → MaybeReleaseCar (:290): releasable + cash → 0xB715AE8F three buttons (Use Marker / Pay 0x4EB9591F / Cancel) when a marker is
  owned, else 0xCAD5722E OK / Cancel; impounded with a marker → 0xED4DD591; neither → 0xE96FA0C5. Pay = cost ×
  g_fImpoundPercentageOfOriginalCost 0.25 (0x8F4310), heat 0; marker release uses MARKER_IMPOUND_RELEASE. Key 2 →
  MaybeAddImpoundBox (:318): 0x0CEBDA20 yes → the marker is used, MaxBusted + 1 (max g_MaximumMaximumTimesBusted 5, 0x8F4314);
  at the maximum 0xBCAE8539. Back on an impounded / releasable original car → 0x630931B6 and no way back; sell blocked by
  0x80E4F27C (impounded) / 0x9A772BD6 (fewer than 2 available cars); showcase blocked for impounded / releasable cars; the
  0xE0B38195 game-over check (no available car, no release marker) → 0x164BED94 → back to the career manager.
* RefreshHeader career lines (:1144): impound box marker count 0x39DC21F9 (0x5B875870 / 0xEA8AECD9 "%2d"), releasable →
  "Release Cost:" 0x9DB4DF7D / "Cash:" 0x073B79E0 with the cost and the cash; impounded → "Pending" 0xAEFEDAD9 and the release
  marker count 0xE998FE99 (0xCC59B910 / 0xB8F9938A); else bounty 0x17574B0E / unserved fines 0x915F4D26 of the car's record.
  Locked bonus cars use GetBonusUnlockText (:961): BLn → 0x4EF2A115 with the rival, C6R 0xBD8BAC94, E3 BMW 0xBD8BAC93, SL65
  0xBD8BAC91, GT2 0xBD8BAC92. Logos fall back to GENERIC_LOGO_128 / GENERIC_LOGO_256 (GetManuLogoHash / GetLogoHash).
* **FECareerRecord / FEImpoundData store** (`Core/FEDatabase.cs` FECareerCarRecord in FEDb.Data.CareerCars, keyed by the car's
  index in Profile.Cars; `Runtime/UI/Safehouse/FEImpound.cs`): TimesBusted, ImpoundedState (0 none, 1 busted by Cross, 2 strike
  limit, 3 insufficient funds, 4 released), DaysBeforeRelease, EvadeCount, Bounty, evaded / busted counts, unserved / served
  infraction counts; MaxBusted stays on OwnedCar. NotifyBusted / NotifyEvade / NotifyWin / BecomeImpounded / Release /
  CommitPursuitCarData / ServeAllInfractions / FineValue / AdjustStableHeat_* (pursuitlevels EventWinHeatAdjust 0.94,
  MilestoneCompleteHeatAdjust 0.93, EvadeSuccessHeatAdjust 0.95) / AdjustStableImpound_EventWin (SMS 0x78 when a car becomes
  releasable) are ported 1:1 but **nothing in the game code calls them yet — hooks for the pursuit / career owners** (listed in
  the class comment). Selling a car removes its record (OnCarRemoved).

### 15.2 Splash screen cheats (Loading/FESplashScreen.cpp:98, `PressStartScreen.OnEasterEgg`)
`EasterEggs.Activated` (Runtime/Input/EasterEggs.cs) is subscribed while MW_LS_Splash is up; the first activation of a code
shows the exe's one-button alert: FEHASH_BK_UNLOCK 0x98257537 → 0x53F13FD1 ("A special race event has just been added to your
Challenge Series"), FEHASH_CASTROL_UNLOCK 0x6521E5C2 → 0x6521E5C2 (the Castrol Ford GT), FEHASH_DEMO_CHEAT 0xA6813B08 →
0xA1161AAF (the demo marker). No package has a response for these messages (MW_LS_Splash / PressStart / MainMenu checked):
the dialog is the FE's whole reaction; the unlock itself is the easter egg code's.

### 15.3 Options: PODriveCam (uiOptionWidgets.cpp:429)
`FEDb.ScrollDriveCam`: CurCam steps 0..6 circularly skipping cameras IsPlayerCameraSelectable rejects — the car model's
camerainfo collection (`<model>_<cam>` else `<cam>`: bumper / hood / close / far / superfar / drift / pursuit) SELECTABLE[0]
(index 1 in the two-player view). [?] every camerainfo reads SELECTABLE = 0 through the port's attribute reader, so a zero
falls back to "the five labelled cameras 0..4 are selectable" (the labels 0xC3E9AE58 … 0x916039B4 cover 0..4 only).

### 15.4 IconScrollerMenu geometry (Common/feIconScrollerMenu.cpp) → `Screens.cs` CarouselScreen
Now the exe's layout: option_N images handed out in order with two book ends (STRINGHASH_END_OF_SCROLLER 0x43B6310F) first
and last, XPos = running width + spacing −5, the list slides so the current option sits on ICON_SCROLL_REGION's centre
(tCubic1D AnimateCubic, 0.2 s Hermite from the current value / velocity), icon scale = IconScroller::Scale (0 outside
±width/2 = ±175, ramps to 1 within 1.5 px of the centre), pulled towards the centre by OrigWidth·(1−scale)³, clipped at the
region edges, tinted IdleColor ↔ FadeColor by scale (greyed options alpha 150), 9-frame fade-in; the name goes to 0x5E7B09C9
and its shadow 0x0DFB7A2E; ENDPADLEFT 0xD7118934 / ENDPADRIGHT 0xB9B17747 at the list ends; Scroll does not wrap. [ours] the
option acts at once (the exe plays the leave scripts first). Used by MainMenu, MainMenu_Sub, Options, the customize menus.

### 15.5 Marker select badges (uiMarkerSelect.cpp:54-81)
SafehouseMarkers posts 0x9F04347D / 0x5B032D25 / 0x96B11F47 when DoesCategoryHaveNewUnlock (UnlockSystem.cpp:74) says the
parts / performance / visual category has a new unlock, and 0x7F8AAF09 when a lot car's UnlockedAt equals the current bin.

### 15.6 Garage → `CarShowroom` (round 9: FEPkg_GarageMain.cpp re-diff)
Source: FEPkg_GarageMain.cpp; PC HandleTick 0x7B9EA0, HandleJoyEvents 0x7A2310, __handlemouseinput 0x7A2010,
NotificationMessage 0x7BA130, UpdateRenderingCarParameters 0x7A96C0, HandleRender 0x7A2200.
* Constants (.data): sNumTicksBeforeCamMovesBackToScreenPosition 0x905E24 = 300 ticks (old guess 90); CarRotateSpeed 0x905E28 =
  0.5° per tick (30°/s; old guess 12°/s); CarSelectTireSteerAngle 0x905E20 = 21.6723° (front wheels turned on every garage
  screen); mouse zoom scale flt_8B7240 = −0.016.
* The camera mover (SelectCarCameraMover 0x476870-0x47723D: modes 0 spring / 1 user / 2 settled, the 94.5..110 / 4.65..6.65
  clamps, the 0.555 s re-aim to roll 0 / FOV 45 / look-at (0, 0, 0.75)) is the camera owner's 1:1 port
  `Runtime/Camera/SelectCarCamera.cs` (camera_core.md §13); CarShowroom only drives it as GarageMainScreen does (its own copy
  and guessed scales were removed).
* HandleTick: the counter only counts down without joystick input; a new screen key sets it to cam_anim_speed·60 and clears
  bPass1 / bAutoMovement, then SetDesiredOrientation to the screen angle. At 0 on a cam_user_rotate screen: back to the screen
  angle (bPass1), then the turn at 0.5 per tick (bAutoMovement).
* PC mouse (__handlemouseinput, each SCREEN_TICK before HandleTick, cam_user_rotate screens only): SetZoomSpeed(wheel × −0.016),
  the counter reset to 300 every tick, left button held (FEMouseInfo bit 0, 0x987F70) → H / V speed = mouse dx / dy pixels per
  tick. Because of the reset the automatic turn / return never happen on a user-rotate screen on PC.
* Joystick actions 0x1D / 0x1E vertical ±, 0x1F / 0x20 horizontal ±, 0x2B / 0x2C zoom out / in (larger wins), each resets the
  counter [?: the port reads the pad's right stick and triggers].
* Placement (GetCarRotationX/Y/Z 0x7A20D0 / 0x7A2100 / 0x7A2130, GetGeometryZAngle / Pos 0x7A2160-0x7A21E0; garage types 1 main
  FE / PLATFORMCRIB, 2 safehouse, 3 shop, 4 backroom, 5 car lot from GetGarageNameFromType 0x516E40): car lot 340° / −0.379623°
  / −0.000193°, set 0°, Y 0.075; safehouse 304.96979°, set 302.85309°; shop 304.96979°, set 0°; main FE / backroom (default
  branch) 304.96979°, set 134.4125°. Car body Rz·Rx·Ry. The port's camera was tuned against an unrotated car in a 304.97° garage,
  so car and set turn by (angle − 304.96979°) (a Unity yaw of −θ) [?].
* Not ported: GarageCarLoader, the exact ride height, the CameraPushRequested gate (UpdateCurrentCameraView 0x7A94C0), the fades
  on a background change, SetFEDrivingCarState. Open: My Cars / Quick Play still use CAR_LOT.BIN (the exe: PLATFORMCRIB, type 1).

### 15.7 FEng list objects
`fecust objtypes`: no FEng package of the PC data contains a List (type 4) or CodeList (type 6) object (images 9891, strings
4172, groups 4035, movies 14, coloured images 13, one simple image, 25 multi-images). FEListBox.cpp / FECodeListBox.cpp are
therefore N/A for this build — nothing reaches them.

## 16. Round 9 (2026-10-25): FEng engine changes that touch these screens
See fe_ingame.md §13: FEngSetScript without restart now starts a non-current script at time 0 (no remembered time); FEngSetVisible skips flag-0x40 objects (PC; was flag 8); FEView.Printf (FEPrintf: cut to the Sb buffer length, literal flag) and SetLanguageHash; package strings with game flag 2 show their literal text; group colours compose as (ctx * own + 128) >> 8; objects with game flag 0x10 take the game-mode colour (front end 0xFFE6E6C8, in game 0xFFFFAF41, rap sheet 0xFFAAE646); non-wrapping strings wider than MaxWidth are squeezed (RenderString); FEView.Clone puts the clone at the package root with the source's current script; the exe-faithful setters SetPosition / SetColor / SetTopLeft / SetSize / SetRotationZ / SetMultiImageRot / SetTextureHash (base keys + FF_DirtyCode) are available to screens.


## 17. Round 9 (2026-10-25): the last FE files

### 17.1 Localization (Localization/Localize.cpp, WideCharHistogram.cpp) → `Core/LangLocalize.cs`
* **Strings are packed.** LANGUAGES\*.bin chunk 0x39000 starts {HistogramTablePos, NumStringRecords, RecordTablePos,
  StringTablePos}; the histogram is u32 NumEntries + u16 EntryTable[]. LoaderLanguage (0x57E960) sets pWideCharHistogram;
  DisableWideStringHistogram (0x91CF88) is BSS 0, so GetLocalizedWideString (0x57E920) always runs **UnpackString (PC
  0x56BFC0, differs from the GC decomp)**: byte < 0x80 → itself; e = EntryTable[b]: e == 0 → the raw byte, e ≥ 0x80 → e,
  1..0x7F → second byte n (n < 0x80 → error) → EntryTable[e·128 + n − 0x80]; a zero result prints '_' (error). English.bin
  maps 0x81..0x85 to ™ “ ” – ‘ (used 22 / 19 / 19 / 6 / 2 times) — the port decoded Latin-1 and printed control characters
  there. PackString (0x56BE80) is ported for WideStringToPackedString. Hook: `StringTable.Read` (Core/FEng.cs, 2 lines).
* GetLocalizedString (0x56BC10): a missing label returns label 0x9BB9CCC3 "FENG: Default string error." (FEMenuScreen.Lang
  and `FrontEndData.Localized` now do that instead of "[hash]"). SearchForString (0x56BB80) is a binary search of the
  sorted record table (English.bin: sorted, no duplicate hashes → the port's dictionary is equivalent).
* **Language**: regeditthang (0x6CB680) reads HKLM\Software\Electronic Arts\EA Games\Need for Speed Most Wanted\Language
  through LanguageConversionTable (0x8FB578: name, eLanguages, region): English US 0/0, English UK 0/5, French 1/6, German
  2/7, Italian 3/8, Spanish 4/9, Swedish 6/1, Danish 7/1, Dutch 5/1, Korean 8/2, Chinese 9/4 and 9/11, Japanese 10/3, Thai
  11/12, Polish 12/1, Finnish 13/1; nothing matched → English (PC_GetOSLanguage 0x6CBB00). The port reads the same key by
  reflection (no Registry in .NET Standard 2.1) and loads LanguageInfoTable[lang].Filename (0x8F40F8: 10 rows, 0x14 bytes:
  English, French, German, Italian, Spanish, Dutch, Swedish, Polish, Finnish, Danish). BuildRegion::IsAmerica (0x64A010) =
  region 0, IsPal / IsEurope (0x64A110) = region 1, 5..9.
* GetLocalizedPercentSign (0x57E680): " %" for Danish / Finnish / French / German / Swedish (main menu stats use it).
  LanguageHasChanged (0x57E6B0): GameplaySettings.SpeedoUnits default = 0 English, 1 otherwise.
* **bPrintf locale** (bPrintfSetLocaleInfo from LanguageInfo +0x10): {decimal, group, group_len} = English ". , 3", French
  ", ' ' 3", German / Italian / Dutch / Danish ", . 3", Spanish ", ' ' 0" (no grouping), Swedish / Polish / Finnish ", ' ' 3".
  "%$d" / "%$0.Nf" (bPrintf.cpp:385 / :528) use it → `Localize.Group` / `Localize.Fixed` / `.Grouped()`; the FE screens no
  longer format with the OS culture ("N0", ":N1"). Not changed (other owners): FrontEnd.cs Money(), GameHud, HudExtras.
* InitLocalization / LoadLanguageResources / Unloader: memory pool, LANGUAGETEXTURES.BIN and font packs (FrontEndData
  loads them once) — N/A by design. LS_LangSelect: BuildRegion::ShowLanguageSelect (0x64A140) is `xor al, al; ret` → never in
  the boot flow (N/A).

### 17.2 Boot flow (Loading/FEBootFlowManager.cpp, FEMovieScreen.cpp, FESplashScreen.cpp) → `Safehouse/BootFlow.cs`
eIsWidescreen (0x4FB000) is constant false on PC, so the list is the NTSC table 0x8F3C94 (DiscErrorPC, MC_Bootup, LS_EAlogo,
LS_PSA, MW_LS_Splash, MC_Main, MainMenu) or, IsPal, the PAL table 0x8F3CB0 (… LS_PSA, **MW_LS_AttractFMV**, MW_LS_Splash …).
MovieData: ealogo (not bypassable), psa (bypassable, subtitles), attract_movie; a missing file (none of them is in the PC
install's MOVIES) ends the movie at once (0xC3960EB9 → ChangeToNextBootFlowScreen). Splash (0x5A32A0): accept / start after
INIT_COMPLETE → next; SCREEN_TICK with no input for SplashScreenMovieTimeout 30 s (0x8F3CD0; SplashScreenTotalTimeout 0) →
DoAttract, none in the list → the timer parks at 0x7FFFFFFF. The old port played ealogo → intro_movie (intro_movie is not in
the PC boot list). DiscErrorPC / MC_Bootup / MC_Main pass straight through (CD check / memory-card boot: N/A by design).
Hook: GameBootstrap → `BootFlow.Start()`.

### 17.3 Loading (FELoadingScreen.cpp, FELoadingTips.cpp, FELoadingControllerScreen.cpp) → `Safehouse/LoadingScreens.cs`
* ELoadingScreenOn (0x62D570) pushes **one** package: tip 28 (NONE) → Loading.fng, 27 (USE_CONTROLLER_CONFIG) →
  Loading_Controller.fng, else Loading_Tips.fng with the tip. The §12 "deviation" was not one.
* WhatTipScreenShouldIUseToday (0x597CF0): TipTestLastCarWithTwoStrikes (0x58EE30: career, free roam, loading the game, one
  available career car with TimesBusted == MaxBusted − 1) → 0; the inlined FirstTimeIntoSafeHouse (0x597D06: career, loading
  the FE, intro not done (flag 0x20 [?: port = story stage past the prologue]), post-rival) → 1; FirstTimeOutOfSafeHouse
  (0x56AEC0: career, free roam, loading the game, intro done, flag 0x80000 clear → set; port: FEDb.Data.HasDoneMapLoadingTip)
  → 2; else GetARandomTipScreen over the 28 rows (controller config included).
* GetGameTip (0x56AFB0): 0 < tip < 27 → that row, else row 16 — so tip 0 (last car, two strikes) shows TIP_HIDING_SPOTS (sic).
* GFT_WAIT_FOR_BUTTON_PRESS (0x400, rows 0..2): DelayWaitForLoadingScreen (0x659710) holds the flow until accept; LoadingTips
  SCREEN_TICK (0x57DF10) after loading + 5 s → AllowInput (0x56AF70: package message 0x9938A38F). Port: GameFlow.HideLoading
  asks `LoadingFlow.FinishLoading`; a wait tip keeps the screen with the sim held ("Loading_Tips.fng" pause request) until
  accept [ours: skipped in batch mode].
* LoadingControllerScreen has no constructor in the exe (only dtor / helpers; Loading_Controller.fng has no factory row):
  the package runs alone (N/A).
* Sound states: ctor ESndGameState(10, on) + SNDSTATE_OFF (0x58F019), dtor both off (0x57E0BB).

### 17.4 Game won (Career/FEGameWonScreen.cpp) → `Safehouse/GameWonScreen.cs`
EQuitToFE (0x623F80) in the world, IsFinalEpicChase (0x56DC00, event "1.8.1") and score flag 2 → SetPlayerHasBeatenTheGame
(CareerSettings 0x4000; port FEDb.Data.PlayerHasBeatenTheGame), FEGameMode = Career | BeatGame, garage 2, Initialize
(sub_511FE0), GameWon.fng. CreateGameWonScreen (0x5715C0) serves GameWon / RapSheetLogin_ENDGAME / RapSheetLogin2_ENDGAME /
RapSheetMain_ENDGAME; EXIT_COMPLETE (0x549380) → QueuePackageSwitchForNextScreen (0x532DD0): Credits (beat-game, uiCredits
0x5332BB calls it too) → Login_ENDGAME → Login2_ENDGAME (0x03CC94D6 "> name") → Main_ENDGAME (the RapSheetMain lines from the
stable totals) → MainMenu_Sub (the career crib). **Hook**: the epic-pursuit win / quit owner calls `GameWonScreen.Begin()`.

### 17.5 Controller unplugged (MenuScreens/ControllerUnplugged.cpp) → `InGame/ControllerUnpluggedScreen.cs`
PC ctor 0x5493A0: label 0x54EEFEC2 (not the decomp's 0x54EEF4C5) with port + 1 into 0xB244CF71; NotificationMessage
0x549420: START released (0xEBFCDA65) → PopErrorPackage(port) only (no EPause). Shown by cFEngJoyInput::CheckUnplugged
(0x559280) → FEManager::WantControllerError (0x54ECE0; not in an online / LAN race) → FEManager::Update (0x56435D)
PushErrorPackage (pause sim + SNDSTATE_ERROR). Port: `Monitor` on Input System device removal of a used pad [?: port 0].

### 17.6 Main menu / press start re-diff
* UIMain (0x559FC0): Alias Manager → MC_ProfileManager (`ProfileManagerScreen`, slice C) — no longer a notice; stats use the
  localized percent sign. Key 1 strings are GAMESTATS_APPEAR / GAMESTATS_LEAVE on PC (the decomp's "GAMESATS" typo is GC).
* uiQRPressStart (ctor 0x7AE580): only 0xB244CF71 = 0x63F6A01C "Player %d press the START button." (decomp's 0xCF538E1C and
  the 0xA065EFFE body print are GC); back for player 0 (0x7A4033): split screen and RaceMode == Drag → Track_Select, else
  Track_Options. [?] player 2's START from another joystick port is not distinguished.
* MenuScreen ctor (0x586290): FEngSetButtonTexture on L1 / R1 / the Esc key — FEngSetButtonTexture (0x515BB0) swaps
  PC_key_esc (0x1BB3C514) for 0xEC617269 in French (ported); 0x41AAF577 is online.
* MenuScreen::BaseNotifySound (0x5724D0): FEng-engine screens now play the packages' sound messages through
  `FeSound.Notify` (audio agent's PC table) with the screen's NotifySoundMessage filter (PostRace_Results: :840); FrontEnd's
  generic key sounds are skipped for them (hook in FrontEnd.Update).

### 17.7 Performance tuning (InGame/CustomTuning.cpp) → `TuningScreen.cs` (CustomTuningScreen, FEMenuScreen)
Rebuilt 1:1 (see the class comment; fe_ingame.md has the summary): TuningSlider rows over the package's own positions,
cSliders LEFT_SLIDER_n / RIGHT_SLIDER_n (BASE_ 0x21030EE9 / 0xD82C8976, FILLBAR_ = the LED images 0x5A65632A / 0x4684D997 —
the old port drew the bases as the bars), SLIDER_GROUPn, Tunings limits ±1 (TuningLimits 0x8A9350), help blurb CTextScroller
(`TextScroller.cs`), setups on L / R triggers, store / discard dialog. `CSlider.cs` = cSlider (Slider.cpp);
FEngSetBottomRightU = fill / texture width (also in WidgetMenu.Slider and TwoStageSlider).

### 17.8 RaceStarter, dead code
StartSkipFERace (0x57ECD0, from main 0x6665C5) / InitSkipFEOnline (0x56C3E0) run only with SkipFE (0x926064, BSS 0): its
writers are LoadConfigItems (0x66747A: Joylog::AddOrGetData of its own value — replay only), OnlineCfg::ProcessSetting (online)
and sub_56C530 (no callers) → dead. SetupOnlineRace (0x5A3590) is OnlineManager's (online, N/A). StartRace (0x57ECA0) /
StartCareerFreeRoam (0x56C5B0) = GameFlow.StartRace / StartCareer.
feWidget.cpp: FEToggleImageWidget has no constructor caller (dead); FEInputWidget (0x520310..0x537640) and FEDateWidget
(0x5214C0) are created only by the online account screens (N/A).

### 17.9 Quick race, options, widget menu (round 9 slice B)
* **IsEventAvailable** (UnlockSystem.cpp:767, PC 0x576A20) → `QuickRaceUnlock` (QuickRaceScreens.cs): 99.1.1 never; 21.1.1 /
  21.2.1 / 21.2.2 / 19.9.70 only with IsCollectorsEdition (0x8F8708 = 1); 19.8.31 needs easter egg 3 and CareerSettings 0x2000
  clear [?: no BK flag in the port]. IsTrackUnlocked(quick race) (0x576790 → 0x5765A0) = UnlockAllThings | the UnlockedQuickRace
  score flag | 19.8.31. Used by Track_Select, Quick Play and the Challenge Series.
* **Mike Mann build is dead on PC**: MikeMannBuild (0x92612C) is 0 in .data; its only writer, ActivateEasterEgg type 2
  (0x65C376), writes 0 — every IsRaceValidForMike / Mike branch is unreachable (EasterEggs.cs initialises it to 1: wrong, unread).
* **Track_Select** (0x7BF460 / 0x7BA5C0 / 0x7AAC70 / 0x7AADE0): bins 21, then 15..1 (after 21 the counter is set to 0x10 and
  decremented: bin 16 is never listed). Best time = Timer::PrintToString(mBestTime, 0) for P2P / circuit / drag / knockout /
  tollbooth (zero prints too); speedtrap "%$0.0f" with 0x0512E823; others 0x472AA00A. Regions only on the L / R triggers.
* **UITrackMapStreamer** (0x510980-0x55A280) → `QRTrackMap`: TRACK_MAP_<event id, dots → '_'> (297 maps in
  TRACKS\L2RA\TrackMaps.bin, 1024²), hidden until loaded; ZoomToTrack / PanToTrack use GetBoundingBox 0x5FCEA0 (GRaceIndexData
  +0x2C..0x2F = (int)(v·255) from start / finish / checkpoints via Minimap::ConvertPos, GenerateIndex 0x600FC4) grown by 0.125;
  zoom cubic 1 s from 1, pan from (0, 0); UV box = centre ± max half size [?: CareerScreenKit.MapUV for ConvertPos].
* **Track direction is dead**: GetCanBeReversed 0x5FAC80 reads index flag 8, but GenerateIndex 0x600E09 calls it while the
  index is still null (returns 0, 0x5FACC4): the flag is never set, the widget never shows.
* **Track_Options key 1** = DefaultRaceSettings (FED:838): every record defaulted, EventHash kept.
* **Quick_Race_Brief** (0x7BA1A0 / 0x7B42B0 / 0x7B4580): stock list (0xF0001) with UnlockedAt in [bin−1, bin+3] minus the root
  "vehicles" collection; tracks: not challenge / jump / tollbooth / 19.8.31, available and unlocked; 30-tick spin with UISND 0x8B
  each tick, the car only after the spin; TwoStageSliders ACCELERATION / TOPSPEED / HANDLING (0..1, range 160, value = preview
  = EstimatePerformance); logos GENERIC_LOGO_* then GENERICPLACEHOLDER; back → MainMenu_Sub (0x7B45CE). Random paint / parts [?].
* **Options**: player page L / R triggers (uiOptionsScreen.cpp:89): sound 0x6B283007 / 0xF4B32D4D; changed → dialog 0xBA463431
  (Yes keep, No revert), then TogglePlayer (0x53BF826D renamed, redraw), not split screen only; arrows ScrollBar_Arrow_1/2 shown;
  split screen posts 0x7DB7B6D7. UIOptionsMain from the pause menu: icon colours 0xFFFFAE40 / 0x00FFAE40, title 0x863404B5 ←
  0x1D7BB6C9. PC SetupVideo widget order (vtables 0x89BB40 …) matches; VOWideScreen / screen pos / COVibration / COConfig are
  console pages, not created on PC.
* **uiOptionsTrailers is dead**: OMTrailers::React (0x50F120) has no reference.
* **UIWidgetMenu**: ScrollBar = FEScrollBar(pkg, "scrollbar") (0x688A1551 / 0x3FBB7A4E / 0x444969FD / FE), Update(iMax, count,
  viewTop, current) from SetInitialOption and on option change (DIM 0x9E99 on the end arrow, never reset). No PC widget package
  has the Done button 0xD79B07A0 / text 0xF16CF3A9: the Done branches are unreachable.
* Jukebox: ScrollTracks / MoveTrack stop at the ends without a sound; ScrollNext / Prev UISND_COMMON_UP; MoveSelected (0x5A5320)
  WRONG at an end, else UP (Scrollerina's only offline users: the jukebox and UIMemcardList).

### 17.10 FE data (round 9 slice C) — addendum to §1
* **Stable totals** (`FEImpound`, RapStable delegates): GetTotalBounty / GetTotalEvadedPursuits 0x590550 / GetTotalBustedPursuits
  0x5905A0 add the sold-car history, GetNumImpoundedCars 0x5905F0 / GetTotalFines 0x590630 do not; GetNumCareerCarsWithARecord
  0x5906B0 = the career car count (CreateNewCareerCar 0x599D20 gives every car a record); GetNumInfraction / GetTotalNumInfractions
  / GetNumInfractionsOnCar 0x5903B0 / 0x590430 / 0x580EA0; GetPreferedCarName 0x599C50. BackupSoldCarHistory 0x580FD0 on the
  career Car_Select sell (QRCS:478) → `FEDatabaseData.SoldHistory*`. [?] the rap-sheet bounty stays Profile.Bounty.
* **GetFineValue**: attributes `infractions` keys in kInfraction bit order — speeding 150, racing 350, reckless_driving 1000,
  assault 350, hit_and_run 300, damage 100, resisting_arrest 300, off_road 75 (harness `fe9data infr`); the port's old keys
  (excessive_speeding / ramming / damage_to_property / driving_off_roadway) do not exist.
* **CalcPursuitRank 0x5A3C30**: FE_ATTRIB `rapsheet` collections, 15 RapSheetRanks floats best first; rank = first i with value
  ≥ rank[i] + 1, 16 otherwise. CareerPursuitScores::GetValue 0x5999F0. GetGameCompletionStats: rap sheet % = Σ max(0, 15 − rank)
  / 140; challenge totals skip 19.8.31; CE races count.
* GetDefaultCar 0x57FB50 (frontend/default → default_car → m3gtre46careerstart) and NotifyDeleteCar 0x58FF30 (every quick race
  setting holding the car gets the default; a match on SelectedCar[1] also writes [0], kept).
* PC VideoSettings::Default 0x6DF8F0: g_PerformanceLevel from a GPU score (sub_6DD380; NVIDIA 0x322 / 0x312 capped at 3, SiS
  0x6330 → 0), low-memory flag 0x982C31, resolution 0, preset sub_6C1990, ATI clears multisample quality, clamp sub_6C1780 —
  not ported (RestoreDefaults uses level 3 [?]).
* Dead: NotifyStartNewRace 0x57FCE0, RestartDemoCareer 0x58FF60, MatchesGameMode 0x566140; memcard Save/LoadFromBuffer and
  RestoreFromBackupDB are the JSON stores (N/A by design).
* CareerSettings::SpecialFlags (FEDatabase.hpp:330) not in Profile: started 1, cash 2, rap sheet 0x10, intro 0x20, tutorials
  0x40..0x400, game over 0x800, busted once 0x1000, BK 0x2000, beaten 0x4000 (FEDb.Data.PlayerHasBeatenTheGame), special
  challenge 0x8000, challenge series 0x10000, demo marker 0x20000, Castrol 0x40000, map loading tip 0x80000
  (FEDb.Data.HasDoneMapLoadingTip), epic car 0x100000.
* **MC_ProfileManager — UIProfileManager** (create 0x5599C0, ctor 0x54F420, Setup 0x543300, Refresh 0x543180, Notification
  0x543280) → `ProfileManagerScreen`: Create New (0x43798644 / 0x55423473), Load (0x2287E063 / 0x0018ECFF), Save (0x228B7E32 /
  0x001C8ACE, greyed with no profile), Delete (0x0D9035CE / 0x56B00632) → MemcardEnter ops 0x61 / 0x411 / 0x2251 / 0x31 [ours:
  the port's slot files; create / delete ask yes / no]; back → MainMenu; 0x7E998E5E refresh. UIDeleteProfile only from the
  memcard flow (not ported).
* MyCarsManager: stat sliders (UpdateSliders 0x7B34C0), loader messages LOADERAPPEAR 0xA05A328E / LOADERLEAVE 0x913FA282 only on a
  selection change (UpdateCar 0x7B3680), logo fallbacks, NotifyDeleteCar before the delete.

### 17.11 Dialogs, scrollers, keyboard (round 9 slice D) — addendum to §11
* The PC dialog (0x57EE60 / 0x58F720) matches the decomp; countdown elapsed = (RealTimer − start) × 0.00025 → PAD_BACK at the
  countdown. `FEDialog` is a callback front for `DialogScreen` (merged). Deviation: FEDialog.ShowOk runs ok on Esc [ours].
* ArrayScroller: ScrollHor wraps inside the row; with ARRAY_SCROLL_REGION ScrollVer stops at the ends (ControllerScreen,
  ChallengeSeriesScreen follow it). [?] the shared ArrayScroller (CustomizeExe.cs) still lacks the scroll-bar update and
  current-button set (the SMS inbox does both locally).
* **FEKeyboard is unreachable on PC**: Keyboard.fng / Keyboard_GC.fng are only factory rows (0x8F4610 / 0x8F4618), no package
  pushes them; PC text entry is FEngBeginTextInput 0x5865E0 → KeyboardEditString::StartCapture 0x57F1D0 (inline).
