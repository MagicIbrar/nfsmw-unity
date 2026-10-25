# Career / story flow (NFS MW 2005 PC v1.3)

Sources: `GLOBAL\gameplay.bin` (VLT class `gameplay`), the **stategraph Lua bytecode** inside it (decoded for this spec,
see §0), `LANGUAGES\English.bin` strings, `SUBTITLES\*`, `NIS\Scene_*.bun`, speed.exe (Ghidra addresses).
Companion files: `specs/career_stategraphs.txt` (all 258 stategraph handlers decompiled), `specs/career_sms.txt`
(the 95 SMS: id | from | subject | text, cut to 150 characters), `tools/lua50.py` and `tools/gpprobe/` (the extractor).
Positions are gameplay.bin Vector3 in game space (X, Y, Z = height). Rotation is the heading in degrees.

## 0. How the game's career logic is stored (important for the port)

* Every `messagehandler` collection (`stategraph_<template>_<state>_handler_<message>[_N]`) has `handler_owner`
  (the template, e.g. `race_bin`, or one instance that overrides it, e.g. `race_bin_14`, `16_2_1_d_day_intro`),
  `stateref`, `transitionlist`, `message_id`, and `bytecode` (Attrib::Blob {u32 size, ptr}).
* The blob is `HUFF` (EaHuff, `Compression.Decompress`) → **Lua 5.0 bytecode** (`\x1bLua` 0x50, int/size_t/instr 4,
  OP6/A8/B9/C9, **lua_Number = float32**). Each chunk defines one global function `stategraph_..._handler_...` that gets
  r0 = self (the activity's gameplay record; unnamed fields such as `EntryActivity` are read by name),
  r1 = the message, r2 = the per-instance state table.
* Messages: StateEnter 0x5E1AA4C2, StateExit 0x9C020C14, NotifyRacePlacement 0x122C89F1 (r1.Placing, r1.Activity,
  r1.Racer), NotifyChallengePassed 0x26BB525D, NotifyPlayerRep 0x2F1DBF5A, NotifyFinished 0x0EF58DC9 (r1.Sender),
  NotifyMovieFinished 0x89BB36FE, NISComplete 0x6638715C, NotifyTimer 0x029084E4 (r1.Name), TriggerEnter 0x459EABF5,
  NotifySimTick 0x2F80BB9F, EnteringGameplay 0x32F2E00B.
* Script API seen (implement these as C# hooks): `ChangeState`, `Activity.Run`, `EAddSMS(id)`, `ECellCall(id)`,
  `EPlayRaceMovie(name)`, `EShowMessageScreen(id)`, `NIS.Play(marker, scene, 'Intro'|'Outro', track, '', '')`,
  `Game.UnlockRace/JumpToCarLot/DoSafeHouseIntro/IntroduceRival/PlayTutorial/SkipCareerIntro/SetHasRapSheet/
  SetWorldHeat/SetTimer/KillTimer/ShowGPS/NavigatePlayerTo/SabotageEngine/ForceAIControl/SetCopsEnabled/SpawnCop`,
  `Bin.GetNumRacesWon(bin)`, `Bin.GetNumChallengesPassed(bin)`, `Game.GetPlayerBounty()`.
* The bindings are registered in FUN_0061e750. `Game.IntroduceRival` (0x605550) and `PlayHackE3FMV` are **empty**
  (`ret`) in PC v1.3.

## 1. New Career → free roam (exact order)

### 1.1 Front end
1. Career menu: "Start New Career" (string 0x17E18F87) / "Resume Career" (0xE072DB21).
   With no alias: "You must create a new alias to start a new career…" (0xBADD522C) → alias name entry (Keyboard.fng, profile
   manager MC_ProfileManager). With a career already saved: confirm "Starting a new career will overwrite…" (0x00F89214).
2. `StartNewCareer` = FUN_005a3950(launch):
   * FUN_0057f4d0 resets the career block: **current bin = 16** (byte, career+0xB0), flags 0, 150 SMS slots (slot i =
     i if language label `SMS_MESSAGE_<i>` exists, else 0xFF; inbox count 0).
   * Current car = the garage entry of FE_ATTRIB `frontend/default.default_car` (FUN_0057fb50).
   * Rap-sheet ID string `"MW-%d-%.4sXXX"` (random 0x42D + rand(0x19B3), the first 4 characters of the alias).
   * Unless the skip-intro unlock (DAT_00926125/26, set only by an unlock code in FUN_0065c2e0, type 1) is set: the player
     car is the preset **M3GTRCAREERSTART** (car `bmwm3gtre46`), the stats are zeroed, event **"16.1.0"** is started, and the
     world loads. With skip-intro, the bin is set to 15 and the cars are given by hash (0x2CF385B2 = unknown, 0x03A94520 =
     `E3_DEMO_BMW`), then free roam.
   * Resume (FUN_0057f570, from FUN_00562ff0): if bin == 16 and 16.2.1 is not complete, it sets M3GTRCAREERSTART and starts
     the first **unfinished** event in the prologue table at 0x8A37E4: `16.1.0, 16.2.2, 16.2.3, 16.1.1, 16.2.1`. Else, if 1.2.3
     is complete and epic flag (career flags bit 14) is clear, it starts 1.8.1. Else free roam.

### 1.2 Prologue (D-Day races). All have `DDayRace=True`, `ProgressionLevel=16`, `PlayerCarType=M3GTRCAREERSTART`, and each one
chains to the next with **PostRaceActivity** (basicrace `done` state: `Activity.Run(PostRaceActivity)`).
The race state machine (stategraph_basicrace): initial → tutorial (skipped for DDayRace) → intro_message (if IntroMessageID
≠ -1 → `EShowMessageScreen`) → intromovie (if IntroMovie ≠ '' → FMV) → intronis (NIS.Play(racestart, IntroNIS)) → prerace
→ countdown/racing → raceover → postrace (results) → outronis → outro_message → outromovie → done.

| # | Event (EventID) | Intro | Opponents (preset → car) | Notes |
|---|---|---|---|---|
| 1 | `16_1_0_partial_dday` (16.1.0) | FMV **storyfmv_rac01** ("Present Day… challenging Blacklist Rival #15"), NIS `IntroNisDD` | `razor` RAZORMUSTANG→mustanggt, name BLACKLIST_RIVAL_01_LEADERBOARD, spawn `boss_spawn` (2232.7,-526.4,122.4) | Region college, TOD 1, **RollingStart, InitialPlayerSpeed 60, StartPercent 0.6, StartTime 86**, no countdown, MaxHeat 1, Traffic 10. startgrid (2097.7,-526.7,114.1) rot -0.4, finish (3385.7,-325.5,11.8). Script: 3 s into racing → `EDispIntroRace()`; trigger `call_from_mia` (2541.7,52.2,104.1) → **ECellCall(10)** (Mia: "Oil slick… end the race fast"); 8 s after the call starts: AI takes over both cars + fade; 3 s later → postrace. Not won and gives no cash. prerace exit: `ENISNukeSmack(2183.26,-532.14,119.86,4)`. |
| 2 | `16_2_2_sprint` (16.2.2) | FMV **storyfmv_cro06_coh06a** ("Six Days Ago… entering Rockport City", Sgt Cross stops the BMW, "Four Days Ago…"), NIS GenericStart, IntroCameraTrack 1 | `ronnie_mccrae` DDAYSUPRA→supra (BL #3 name) | city, TOD 1, cash 10000, start (1043.7,104.7,101.4) rot 115.8 |
| 3 | `16_2_3_sprint` (16.2.3) | FMV **storyfmv_raz08** (meet Razor/Mia at the shipyard, bet) NIS IntroNis05 | `toru_sato` BL2→slr | cash 10000; RewardsForWinner = 3× `out_of_jail_free` markers; triggers: cop_spawn_trigger spawns 2 `copmidsize`, bail_pursuit, trigger_911 → E911Call. start (2818.2,616.0,42.8) rot -175.5 |
| 4 | `16_1_1_circuit` (16.1.1) | **IntroMessageID 2** → SixDaysLater.fng with **DDAY_TIMELAPSE_3** = "2 Days Ago…^Building Reputation, Winning Races…", NIS IntroNis10 | `opponent` OPM_MUSTANG_BOSS→gto (BLACKLIST_RIVAL_16_LEADERBOARD = Rog), OPM_WRX_VERSION2→imprezawrx, OPM_RX8_Version3→rx8 | 2 laps, TOD 0.5, cash 10000, FinishCamera PHoldFin_17, start (1631.4,382.1,95.0) rot -173.8 |
| 5 | `16_nis_rog_razor` (16.8.1) | NIS shell (`NISShell=True`): intro NIS inherited IntroNis04, then **OutroNIS EndingNis03** at `startgrid1` (1727.7,386.4,92.5) | rog (gto), razor (mustanggt) | no race; it goes straight to the outro NIS, then done |
| 6 | `16_2_1_d_day_intro` (16.2.1) | FMV **storyfmv_pin11** (pink-slip race vs Razor, then #15), NIS IntroNis04 | `opponent` RAZORMUSTANG, spawn (920.5,-628.4,110.0) | the **D-Day race**. city, TOD 1, start (917.4,-634.5,109.8) rot -191.4, finish (3802.9,-297.5,15.3). Script: `call_from_mia` (2541.3,63.8,102.4) → ECellCall(10) + 15 s timer 'backup'; `kill_engine` trigger (2953.1,282.2,35.0) or 'backup' → **Game.SabotageEngine(player, 5)**; on engine blown: 1 s → Fade → postrace (the race is lost). `cop_spawn_trigger` (618.4,116.9) spawns 2 cops; `bail_trigger` → EDispIntroRace + EBailPursuit. **OutroMovie storyfmv_bus12** (Razor takes the BMW, Cross arrests the player, "Some Time Later…", Mia picks him up: "first you gotta get yourself a new ride… Hey Ed, it's Mia"). Done override: **Game.JumpToCarLot()**. |

`EShowMessageScreen(id)` (event vtable 0x8A74CC, exec 0x625090) pushes **SixDaysLater.fng**; its ctor 0x58E820 sets text
object 0xB769701E to `DDAY_TIMELAPSE_<id+1>` (1 = "Six Days Earlier...", 2/3 = "2 Days Ago..."). The FEng default text is
"SIX DAYS LATER...". Only 16.1.1 uses a message id; the other time cards are burnt into the FMVs.
`EDispIntroRace` = unresolved (probably the race-name HUD card).

### 1.3 Car lot → first free roam
7. `JumpToCarLot` (0x605250) → quit to FE in mode 5 → **Car_Select.fng** (career car lot; FUN_00623f80 mode 5 sets
   flag 0x8000). Cars on offer: FE_ATTRIB `frontend` with `UnlockedAt == 16`: **cobaltss 26000, is300 27000, punto 27000,
   gti 35000**. More cars unlock as the bin falls (`UnlockedAt >= current bin`).
   *Budget:* no starting-cash constant was found. The prologue races that pay are 16.2.2, 16.2.3 and 16.1.1 at **10000
   each = 30000**, so the budget is probably the cash won in the prologue (inferred, unverified).
8. The world is entered at marker **`career_start_l2ra`** (`career_start_%s` + track name, FUN_005e4cf0):
   **(981.9, 2148.4, 152.3), rotation 53.1**. This is the Rosewood car lot (`menu_gate_carlot/zone_carlot_1` is at
   (981.6,2147.3), r=4).
9. **race_bin_16** starts (bin template, §2). `EntryActivity = race_bin_16/drive_to_safe_house` (drive_to_target,
   AutoStart, FreeRoamOnly):
   * initial: SetCopsEnabled(false); unless SkipCareerIntro, **EAddSMS(15)** (Mia: "Check out your map… I'll meet you there").
   * driving: after 0.25 s, ShowGPS + NavigatePlayerTo(gpstarget, MiniMapItem `menu_gate_safehouse/zone_safehouse_1`, 2000);
     after 120 s, **EAddSMS(121)** (Mia: "Where are you? Don't keep me waiting loser.").
   * gpstarget box **(1768.4, 2497.5, 147.7) rot 48, dims (2.7, 98.1, 10)** → at_target → **Game.DoSafeHouseIntro(
     `menu_gate_safehouse/zone_safehouse_1/respawn` (1762.8,2508.8,147.3) rot -115.4)**. DoSafeHouseIntro (0x605560)
     stores that respawn marker as the free-roam spawn, queues the FMV **storyfmv_saf25** ("So, you found it… your safe
     house…") followed by **SafeHouseRivalBio.fng** (FUN_00525d20), then quits to FE mode 2 (Safehouse).
   * done: SetCopsEnabled(true), ShowGPS(false), NotifyActivityFinished → race_bin_16 unlocks the races.
   SkipCareerIntro (0x612490) is also true once career flag 0x20 ("intro done") is set, so a reload skips this.
10. Safehouse screens, then free roam from the safehouse respawn. Blacklist #15 is next (§2).
    Safehouses: zone_safehouse_1 (1777.1,2503.5) Rosewood, _5 (2328.8,-66.4), _2 (4257.5,78.7), _4 (3627.8,3340.5), r=7
    (respawns in gameplay.bin). Car lots: zone_carlot_1 (981.6,2147.3), _2 (4204.8,1257.4), _3 (1750.9,512.1).

## 2. Bin progression (template `race_bin`, per-bin overrides)

The state graph (all in `career_stategraphs.txt`):
* **initial**: if EntryActivity, run it and wait for NotifyFinished; `EAddSMS(EntryCellCallID)` if ≠ 0; → unlock_races.
* **unlock_races**: zero the counters; `Game.UnlockRace(t.TargetActivity)` for every `engage.RaceTriggers[i]` with
  `InitiallyUnlocked`; → introduce_rival.
* **introduce_rival**: `Game.IntroduceRival()` (no-op on PC) → check_rival.
* **check_rival**: rival unlocked when `Bin.GetNumRacesWon(BinIndex) ≥ RequiredRacesWon` **and** `GetPlayerBounty() ≥
  RequiredBounty` **and** `GetNumChallengesPassed(BinIndex) ≥ RequiredChallenges` → rival_unlocked, else rival_locked.
* **rival_locked**: re-checks on NotifyRacePlacement / NotifyPlayerRep / NotifyChallengePassed / EnteringGameplay; per-bin
  SMS hooks (below). When challenges+1 == RequiredChallenges: EAddSMS(SMSCellChallenge) and UnlockRace(CellChallengeRace).
* **rival_unlocked**: **EAddSMS(SMSRivalChallenge)**; UnlockRace(every BossRaces[i]); counts the player's wins (Placing 1)
  of the boss races; when every boss race is won → **done**.
* **done**: UnlockRace(every WorldRaces[i]). The bin then advances in C++ (bin byte −1; not traced). Every race of a beaten
  bin stays open for cash.

Bin requirements (gameplay.bin `race_bin_NN`; Int fields `EntryCellCallID`, `SMSRivalChallenge` are unnamed in
vlt_classes but readable by name):

| Bin | Boss (preset→car) | Races won | Bounty | Milestones | Entry SMS | Rival SMS | Boss races (chained by PostRaceActivity; first race IntroNIS) | Final cash |
|---|---|---|---|---|---|---|---|---|
| 16 | — | 4 | 0 | 0 | (EntryActivity drive_to_safe_house) | — | — | — |
| 15 | tony_ho (Sonny) BL15→gti | 3 | 20,000 | 3 | — | 20 | 15.1.2 (IntroNisBL15) → 15.1.1.r | 5,000 |
| 14 | vince_kilic (Taz) BL14→is300 | 4 | 50,000 | 3 | — (initial: SMS 125, 126) | 29 | 14.2.4 (BL14) → 15.2.1.r | 6,000 |
| 13 | victor_vasquez (Vic) BL13→supra | 4 | 100,000 | 3 | 30 | 36 | 13.2.3 (BL13) → 14.2.2.r | 6,000 |
| 12 | isabel_diaz (Izzy) BL12→rx8 | 4 | 180,000 | 3 | 37 | 41 | 12.2.1 (BL12) → 12.1.2 | 8,000 |
| 11 | lou_park (Big Lou) BL11 | 5 | 300,000 | 3 | 42 | 46 | 11.1.2 (BL11) → 12.1.2.r | 10,000 |
| 10 | karl_smit (Baron) BL10 | 5 | 500,000 | 4 | 48 | 52 | 10.2.1 (BL10) → 10.7.3 | 12,000 |
| 9 | pierre_dupont (Earl) BL9 | 5 | 790,000 | 4 | 54 | 57 | 9.5.3 (BL09) → 9.2.1 | 16,000 |
| 8 | jade_barrett (Jewels) BL8 | 5 | 1,180,000 | 4 | 58 | 63 | 8.2.1 (IntroNisBL16) → 8.7.5 | 20,000 |
| 7 | kira_nazakato (Kaze) BL7 | 7 | 1,680,000 | 4 | 64 | 69 | 7.2.2 (BL07) → 7.2.3 | 24,000 |
| 6 | hector_domingo (Ming) BL6 | 7 | 2,300,000 | 4 | 70 | 72 | 6.1.1 (BL06) → 6.5.2 | 28,000 |
| 5 | wes_allen (Webster) BL5 | 7 | 3,050,000 | 4 | 73 | 76 | 5.2.2 (BL05) → 5.5.4 → 5.2.1.r | 48,000 |
| 4 | joe_vega (JV) BL4 | 7 | 4,050,000 | 5 | 77 | 79 | 4.5.2 (BL04) → 4.7.4 → 4.5.1.r | 54,000 |
| 3 | ronnie_mccrea BL3 | 8 | 5,550,000 | 5 | 80 | 84 | 3.1.2.r (BL03) → 3.2.2 → 3.1.2 | 60,000 |
| 2 | toro_sato (Toru) BL2→slr | 8 | 7,550,000 | 5 | 85 | 88 | 3.1.1.r (BL02) → 9.2.2.r → 2.2.1 | 75,000 |
| 1 | razor_callahan E3_DEMO_BMW→bmwm3gtre46 | 9 | 10,000,000 | 5 | 89 | (override: 93, 94) | 1.5.2 (FMV storyfmv_bla134) → 1.7.3 → 4.2.1 → 1.1.2 → 1.2.3 → **epic_pursuit 1.8.1** | 200,000 |

(Rival nicknames come from SMS text and the language labels `BLACKLIST_RIVAL_%02d_NAME/_AKA/_CAR/_BIO`. They are not in gameplay.bin.)
* Barriers: each bin's `Barriers` (BARRIER_LOCKED_n) are enabled by `RaceStatus:EnableBinBarriers()` (see REPORT). Bins 16–13
  keep Camden Beach and Rockport closed, and bins 12–9 keep Rockport closed. **Region unlock** screen `SafehouseRegionUnlock.fng`
  is shown when the new bin is **12** (Camden Beach, after beating #13) or **8** (Rockport, after beating #9).
* **Markers**: the last boss race has `RewardsForWinner` = the bin's `rewards` folder, 6 markers: 1 `pink_slip`, 3 `backroom`
  (part-type rewards), and 2 of cash_bonus / out_of_jail_free / release_car_from_impound / add_impound_box. Shown in
  **SafeHouseMarkers.fng** (the player picks them; a pink slip gives the rival's preset car). Bin 1 has no markers.
* World races pay their `CashValue` on a win (C++). Milestones give `Bounty` (REPORT).
* `reputation_unlock` (ReputationRequired → Activity.Run(TargetActivities), ECellCall(1,14)) and `completion_unlock`
  (NumRacesRequired top-3 finishes in RaceList → run TargetActivities) are extra unlock graphs. `cell_engage` rings the phone
  after a GateActivity (Placing < 4) and runs TargetActivity if the player accepts.

### 2.1 What plays after a rival is beaten (FE step machine FUN_005302c0, run in the safehouse front end)
Steps (counter +1 per screen closed): RivalChallenge result → **SafeHouseMarkers.fng** → **SafehouseRegionUnlock.fng** (only
new bin 12 or 8) → (conditional screen, FUN_005184d0) → **SafeHouseRivalBio.fng** → **FEAnyMovie `blacklist_<newbin %02d>`**
(rival intro FMV, "Blacklist #N, Hails: …"; WS_FEAnyMovie when widescreen) → if new bin == 15: **SafeHouseReputationOverview.fng**
(sets career flag 0x20); if new bin == 13 for the first time (flag 0x10): FMV **storyfmv_rap30** (Mia gives police-database
access = Rap Sheet; the bin-13 initial handler also calls `Game.SetHasRapSheet()`, career flag 0x10) → back to the game.
The order between steps 0 and 3 comes from a table at 0x8F322C and is approximate.
The boss's first race uses the rival intro NIS `IntroNisBLnn`. IntroNIS is used only in career/boss races, and QuickRaceNIS in quick race.

### 2.2 Endgame
* Bin 1 rival unlocked → SMS 93 ("Race between you and Razor is locked"), 94 ("It's time!").
* 1.5.2 has FMV **storyfmv_bla134** (Mia/Razor: "You ready to donate another car…"). 1.2.3's PostRaceActivity is
  `epic_pursuit`. The launcher also forces 1.8.1 after 1.2.3 is complete.
* **epic_pursuit (1.8.1)**: challengerace, IsEpicPursuitRace, IntroMovie **Storyfmv_her136** (beat Razor, get the pink slips
  back, Cross: "I want every single unit after the guy"), player car **e3_demo_bmw** (the M3 GTR won back), RollingStart,
  InitialPlayerSpeed 40, ForceHeatLevel 6, CopSpawnType copsportghost at 6 tagged markers near start (4341.4,48.7,10.9)
  rot 354.2, CashValue 200000. Script: +10 s **ECellCall(95)** (Cross: "Make a deal"); after 300 s and when > 500 m from
  `barrier_area` (-365.6,3004.6,188.3): **ECellCall(96)** (Mia: "Way out… old bridge"), open `SCENERY_GROUP_DOOR`, GPS to
  barrier_area. **Win**: `win_trigger` (-960.9,2647.3,208.8) entered at **≥ 180 km/h** with angle match ≥ 0.8 →
  NIS **EndingNis04** at win_nis_marker (-954.8,2652.0,207.1) rot -147.4 → EBailPursuit → ChallengeComplete → raceover.
  `lose_trigger` (-988.5,2627.1) totals the car.
* Quit-to-FE handler FUN_00623f80: when the career is complete (FUN_0056dc00) and the current event is completed →
  **GameWon.fng** ("congratulations… you are the Most Wanted racer in Rockport City", bonus cars, Challenge Series, Rap Sheet
  rankings). Otherwise it goes to SafeHouseRaceSheet. The `*_ENDGAME` rap-sheet packages and the credits (RollCredit_ENDGAME,
  CREDITS\*.TXT) follow. Their order was not traced.

## 3. SMS (SMS_MailBoxes.fng / SMS_Message.fng)
* Text is in LANGUAGES\<lang>.bin: labels **`SMS_MESSAGE_<id>`** (body, `^` = newline), `SMS_MESSAGE_<id>_FROM`,
  `SMS_MESSAGE_<id>_SUBJECT` (bStringHash of the upper-case label). 95 ids exist (10–137). The list is in `career_sms.txt`.
* `EAddSMS(id)` adds the message to the career inbox (150 slots, FUN_0057f4d0), shows the incoming-message HUD, and may
  play the phone sound. SMS_Message.fng: header text 0xFECED617 = FROM, 0x2C167533 = SUBJECT, the body is word-wrapped
  into MESSAGE_TEXT_1..10 (FUN_00549150 / 00531CF0). Buttons: Done, Delete, Re-Play.
* `ECellCall(id)` = a voiced cell-phone call that uses the same SMS id (10, 95, 96). Calls happen during races.
* Career-critical ones: 10 (prologue, Mia oil slick), **15** (go to the safehouse), **121** (hurry), every bin's
  EntryCellCallID and **SMSRivalChallenge** (the "rival is ready" message = the moment the Blacklist challenge opens),
  93/94 (Razor), 95/96 (epic pursuit).
* Per-bin hooks (player wins = Placing 1; `wins==N` = Bin.GetNumRacesWon equals N after the win; `chal>=N` = once):

```
bin15 wins==1→123 chal>=1→124 chal>=3→137        bin08 wins==2→59 wins==4→60 chal>=3→61
bin14 initial→125,126 wins==1→18 wins==2→27      bin07 wins==1→66 wins==4→65 (+SetWorldHeat 1) wins==7→134 chal>=2→133
      chal>=2→19 chal>=3→22                     bin06 wins==3→135 chal>=2→100
bin13 initial: SetHasRapSheet; wins==1→23 ==2→24 ==4→31; chal>=1→35 >=2→25 >=3→26
bin12 wins==1→127 ==2→17 ==4→40 chal>=1→33       bin05 chal>=3→101
bin11 wins==2→128 ==4→130 chal>=2→38             bin04 wins==4→136 chal>=4→102
bin10 wins==3→49 chal>=2→132                    bin03 wins==2→81 wins==4→82
bin09 wins==2→53 wins==5→55 chal>=2→43           bin02 wins==4→86 (+SetWorldHeat 5)   bin01 wins==4→90 wins==6→91
```
* Others: 97/98 (impound/busted strikes), 105 (backroom marker), 120/122 (impound release), and 12/13/16 (Rog intro).
  These are sent by C++ (not in the scripts; their triggers were not traced).

## 4. Movies, NIS and tutorials
* Movies are `MOVIES\<name>` (EA VP6). This copy's MOVIES folder is **empty**, but `SUBTITLES\<name>` holds the timing
  ({u32 time, u32 string hash} pairs, 0xFFFF ends the list; time unit probably frames). Existing story FMVs: rac01, cro06_coh06a,
  raz08, pin11, bus12, saf25, rap30, bla134, her136, plus blacklist_01..15. The exe also names dda01, roc02, pol17_mot21 and
  epi138, which have no subtitles and nothing uses them (cut). The precache table is at 0x8F3880.
* Where FMVs are triggered: event `IntroMovie`/`OutroMovie` via `EPlayRaceMovie` (basicrace intromovie/outromovie states);
  saf25 from DoSafeHouseIntro; rap30 and blacklist_NN from the safehouse step machine. Shown in InGameAnyMovie.fng (in game) or
  FEAnyMovie.fng (front end).
* NIS scenes: NIS\Scene_<IntroNIS>_BundleB.bun (REPORT §NIS). Story: IntroNisDD/IntroNisDDEnd, IntroNis10, EndingNis03 (Rog &
  Razor), EndingNis04 (epic-pursuit win), IntroNisBL01..16 (rival race intros), Arrest* (busted scenes, not career-specific).
* **Tutorials**: race state `tutorial` calls `Game.PlayTutorial()` unless DDayRace. It plays only in career, for these race
  types (FUN_005faa20): **drag (type 2) → `drag_tutorial`**, **tollbooth (4) → `tollbooth_tutorial`**,
  **speedtrap (5) → `speedtrap_tutorial`**. Each plays once per career, using career flags bits 0x40/0x100/0x80; pursuit = 0x200
  and bounty = 0x400 are set by the tutorial screen. Screen: InGameAnyTutorial.fng (FE: FEAnyTutorial.fng, ctor FUN_0058f320),
  title `TUTORIAL_DRAG / _SPEEDTRAPRACE / _TOLLBOOTH / _PURSUIT / _BOUNTY`. `pursuit_tutorial`/`bounty_tutorial` are triggered
  by C++ (probably the first pursuit or bounty; not traced). `sprint_tutorial` is unused. Challenge Series
  `challenge_tollbooth_01` also has IntroMovie `tollbooth_tutorial`.

## 5. Implementation checklist (Unity)
1. NewCareer: alias → reset career (bin 16, cash 0 (see §1.3), empty inbox, flags) → **do not** show the car lot first;
   start the prologue with the BMW (`M3GTRCAREERSTART`). Persist per-event completion and resume at the first unfinished id
   of `[16.1.0, 16.2.2, 16.2.3, 16.1.1, 16.2.1]`.
2. Run events through a basicrace state machine with the IntroMessage/IntroMovie/IntroNIS/Outro* hooks and PostRaceActivity
   chaining. Script the prologue specials from §1.2 (rolling start, the scripted fade-out, engine sabotage, forced loss).
3. After 16.2.1's outro FMV → career Car_Select (UnlockedAt 16) → spawn at career_start_l2ra → drive_to_safe_house (GPS, SMS
   15/121) → saf25 → safehouse screens → bin 15 rules.
4. Bin graph per §2 with the SMS tables. Boss races become available when the rival is unlocked. After the final boss race:
   markers, region unlock, rival bio, blacklist movie, rap sheet (bin 13).
5. After Razor: epic pursuit with the BMW → GameWon.

Uncertain / not traced: starting cash; how bin 16 is closed (its RequiredRacesWon=4 is probably met by the prologue wins and
the C++ bin decrement); the exact screen order of steps 0–3 in §2.1; EDispIntroRace; the triggers of the C++ SMS
(12/13/16/97/98/105/120/122); the subtitle time unit.
