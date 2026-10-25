# In-game HUD (HUD_SingleRace.fng) — exe behaviour spec

Source: speed.exe v1.3, static analysis (REA/Ghidra and capstone disassembly), cross-checked against `GLOBAL/InGameB.lzc`
package `HUD_SingleRace` (chunk 0x30203). Hashes are FEng `bStringHash` upper-case. Names in `code` come from exe
strings; bare 8-hex values are object hashes. Script names were recovered by hashing (FADEIN = 0x5B0D9106,
FADEOUT = 0xBCBFCC87, NORM = 0x001A07FB, WIDE = 0x001EDC08, PULSE = 0x03826A28, Highlight = 0x249DB7B7,
SPEEDTRAP = 0xFA44E387, REGULAR = 0x620E4851, THREE/TWO/ONE/GO = 0x03C3C2F7/0xE479/0xCE01/0x535, COPS = 0x13FF94,
FLIP = 0x15970A). APPEAR = 0x5079C8F8, LEAVE = 0x033113AC, HIDE = 0x16A259, SHOW = 0x1CA7C0, INIT = 0x1744B3,
ACTIVATE = 0x77031C70, Warning = 0xDA600155, active = 0x4F90CF9B. Unknown names are written as hex.

## 1. Package selection, construction and update loop

* `FUN_005694E0(type)` picks the package: 2 = HUD_Drag, 3/4 = HUD_Player1/2, 5/6 = HUD_Drag_Player1/2, and
  **every other value = HUD_SingleRace**. Single player uses **type 1**.
* HUD texture pack (`FUN_005A6540`): type 1 loads `GLOBAL\HUDTEXRACE.BIN`. The custom-HUD pack
  `GLOBAL\HUDS_Custom_%2.2d.bin` supplies `<rpm>_LINES_NN`, `TACH_NEEDLE_NN`, `TURBO_NEEDLE_NN` and `TURBO_LINES_NN`.
  NN = custom HUD index `DAT_008F3C44`.
* HUD object = class `FEngHud`, ctor `FUN_005A6600`, vtable 0x8A2538. Widgets are allocated in the ctor; the unwind
  table names each allocation:

| Widget (exe name) | ctor | Update (vtbl+4) | data-iface vtbl | vis mask lo / hi | created for type 1? |
|---|---|---|---|---|---|
| Hud_Speedometer | 59E7F0 | 57A540 | 8A21CC | 0x08000000 | yes |
| Hud_RaceInformation | 59EE10 | 57A900 | 8A22CC | 0x04000000 | yes |
| Hud_LeaderBoard | 59F0E0 | 58C160 | 8A234C | 0x18 | yes |
| Hud_NitrousGauge | 59E260 | 568330 | 8A20BC | 0x80000800 | yes |
| Hud_RaceOverMessage | 59E0F0 | 57A4B0 | 8A20AC | 0x4 | yes |
| Hud_GenericMessage | 59DEE0 | 567F80 | 8A2088 | 0x01000000 | yes |
| Hud_TurboMeter | 5A0380 | 57BB40 | 8A24C0 | 0x80020000 | yes |
| Hud_WrongWIndi | 59EC70 | 568800 | 8A2278 | 0x20 | yes |
| Hud_Coundown | 59DD30 | 5679B0 | 8A2070 | 0x400 | yes |
| Hud_TimeExtension | 5A0060 | 57B780 | 8A2470 | 0x02000000 | yes (non-drag) |
| Hud_Tachometer | 59E9D0 | 57A6E0 | 8A2248 | 0x2 | yes (non-drag) |
| Hud_Reputation | 59D2A0 | 5669B0 | 8A1F6C | 0x1000 | only if GRaceStatus+0x1964 == 2 |
| Hud_HeatMeter | 59CEC0 | 5666A0 | 8A1F44 | 0x40004000 | yes |
| Hud_CostToState | 59D0C0 | 5668F0 | 8A1F58 | 0x1000 | yes |
| Hud_PursuitBoard | 59FC80 | 57AEE0 | 8A2428 | 0x00100000 | yes |
| Hud_MilestoneBoard | 59F440 | 59F7C0 | 8A23F8 | hi 0x4 | yes |
| Hud_BustedMeter | 5A01D0 | 568EB0 (`ret`, a stub) | 8A248C | 0x00800000 | yes (does nothing) |
| Hud_MenuZoneTrigger | 5A0530 | 57BBC0 | 8A24F8 | 0x00400000 | yes |
| Hud_Infractions | 5A1380 | 569A50 | 8A2684 | hi 0x2 | yes |
| Hud_RadarDetector | 59CC30 | 566170 | 8A1F2C | 0x00200000 | yes |
| Hud_Minimap | 59D500 | 59DB50 | — | 0x40010000 | types 1 and 3 |
| Hud_SpeedBreakerMeter | 59E420 | 568460 | 8A210C | 0x00040000 | types 1 and 2 |
| Hud_GetAwayMeter | 59CA80 | 565B10 | 8A1EE0 | 0x200 (never set) | types 1 and 2 (objects absent) |
| Hud_ShiftUpdater / EngineTempGauge / DragTachometer | 5A0C60 / 59E620 / 5A0EF0 | 569780 / 5685E0 / — | | 0x20000000 / 0x40 / 0x2 | drag types only, except ShiftUpdater (also created for type 1, see 4.3) |

(The ShiftUpdater is created in the drag branch only. In type 1 the Shift_light is driven by the Tachometer. See 4.3.)

* Widget base: `[+0x10]` package, `[+0x18,+0x1C]` 64-bit visibility mask, `[+0x20,+0x24]` current HUD flags,
  `[+0x08]` list of the FE objects it owns. The ctor fills the list with `FUN_0058BBD0(groupHash)`, which recursively
  adds every leaf of that group, or by adding single objects.
* **Per-frame** (`FUN_0058CA30`, HUD vtbl+4, called every rendered frame with the player):
  1. `flags = FUN_0057CA60(player)` (section 2). When the flags change, `FUN_0057CDB0` calls `FUN_005793F0` on every
     widget whose bit changed. That walks the widget's object list: if `(mask & flags) == 0` it **hides** each object
     (`FUN_00514C70`: runtime flag bit 0, recursive into groups); otherwise it **shows** each one (`FUN_00514CC0`: clears
     bit 0 unless the object's flag 0x40 is set).
  2. Each widget's Update(player) runs only if `(mask & flags) != 0`. GenericMessage (+0x314) always updates.
  3. `FUN_0057C660`: HUD input (HUDACTION 0x2D..0x31: engage event, map, and so on).
* Data is pushed into widgets by the sim through COM-style interfaces (`FUN_005D7930(ifaceId, …)`); the feeder is
  `FUN_006F13F0`. Setters usually store the value and set a dirty flag; the Update renders it. Everything runs per frame.
* FEng helpers used below: `514D10(obj, script, restart)` play script; `514DA0(obj, script)` = is this the current
  script; `514DF0` = script finished; `515D70(obj, fmt, …)` sprintf text; `515C00(obj, labelHash)` localised text;
  `5253B0(pkg, objHash, fmt, …)` sets text by hash; `515B90(obj, texHash)` sets an image texture;
  `5155E0(obj, w, h)` sets the size; `514FD0`/`524CC0` get/set the **top-left** (pos − size/2: images are centred on
  pos); `515140`/`524E20` get/set the **bottom-right**; `514E20(obj, deg)` sets a Z-rotation quaternion from a 2x2
  matrix [[cos a, sin a], [−sin a, cos a]]; `514FB0(obj, deg)` writes **MultiImage data +0x8C** (see 3.2);
  `524C00(obj, {u,v}, layer)` sets a layer's bottom-right UV; `5157E0` sets colour.
* Scripts emit FEng events (`EV` keys, target 0xFFFFFFFC). Examples: 6F85ED55 INIT/ACTIVATE send 0x5FDAD6BC and
  Warning sends 0x5FFEE086; the N2O icon sends 0x46579AC9 / 0x6EBCD62B / 0x82788C71. Other objects' `MsgR` responses
  react to them, e.g. 0x5FDAD6BC plays HIDE on 10E50F5E and INIT on the heat-meter halves, and 0x5FFEE086 plays Warning
  / PULSE. **The Unity FEng player must route script events to package responses.** The exe never sends these hashes.

## 2. Visibility flags (`FUN_0057CA60`), 64-bit

All flags are 0 (the whole HUD is hidden) unless all of these hold: the player has a sim object; the game state
`FUN_006E8D20() == 3` (in game); none of `Pause_Main.fng`, `MC_Main.fng` or `PostRace_Results.fng` is active; no NIS
(`DAT_0091CAE4 < 1`, `DAT_0091CA58 == 0`); this HUD type matches the current one; `DAT_009885C8 == 0`; the widescreen
state is already in sync (profile+0x34 == hud+0x330); and so on. Then, with `P` = player options
(`FEDatabase(0x91CF90)+0x10 + playerIdx*0x14`) and `G` = game options (`FEDatabase+0x10`):

| bit | set when | widgets |
|---|---|---|
| 0x10000 (+0x4000 offline) | on the city map (`DAT_009A3A20+0x8A == 2000`) and the minimap option ≠ 2. The option is G+0x3C (RacingMiniMap) while a race is loaded (GRaceStatus+0x1968 ≠ 0), else G+0x3D (ExploringMiniMap) | Minimap (0x10000), HeatMeter (0x4000) |
| 0x08000000 | P+0x80 (Gauges); in drag P+0x81 | Speedometer |
| 0x2 / 0x20000 / 0x40 / 0x800 / 0x40000 | if P+0x80 (Gauges): 0x2; turbo 0x20000 if hud+0x2BD (car has a turbo, HUD vtbl+0x20); 0x40 in drag; 0x800 always; 0x40000 offline | Tach, Turbo, EngTemp, N2O, SpeedBreaker |
| 0x8, 0x04000000 | **racing** (GRaceStatus+0x1960 == 1) and not a pursuit race: 0x8 if P+0x85 (LeaderBoard); 0x04000000 if P+0x81 (Position) | LeaderBoard, RaceInformation |
| 0x100000, hi 0x4 | otherwise: **free roam or pursuit race** | PursuitBoard, MilestoneBoard |
| 0x10 | P+0x90 ≠ 4 (drive-camera index ≠ 4) | LeaderBoard (mask 0x18) |
| 0x02000000 | race type == 4 (tollbooth) | TimeExtension |
| 0x1000 | vehicle flag +3 and (GRaceStatus+0x1964 == 2 or pursuit race) | Reputation, CostToState |
| 0x20 | racing (GRaceStatus+0x1960 == 1) | WrongWay |
| 0x4 | the RaceOverMessage object reports "pending" (its vtbl+0xC) | RaceOverMessage |
| always | 0x31C80400: shift 0x20000000, GenericMessage 0x01000000, BustedMeter 0x800000, MenuZone 0x400000, 0x80000, Countdown 0x400 (offline also 0x200000 RadarDetector); hi 0x3 (Infractions) | |

Race type (`FUN_005FAA20` on race params GRaceStatus+0x1968): 0 sprint, 1 circuit, 2 drag, 3 lap knockout,
4 tollbooth, 5 speedtrap (8 = challenge). Pursuit race = race attribute 0x2B1F54F6 (`FUN_005FBE70`).
Units: G+0x3B == 1 means metric. Mirror/rear-view option: G+0x39.

## 3. Load and global layout

### 3.1 Load sequence (`FUN_005A0710`, HUDLoaded)
1. HUD texture pack loaded (step 1).
2. `FUN_0057C000(hudType, &linesTex, &angle)` reads the player car's **engine** attributes (`+0x54` = max rpm,
   `+0x58` = redline rpm; demo build uses `bmwm3gtre46`):
   * `dialMax = FUN_00569730(maxRpm)`: < 7000 gives 7000, < 8000 gives 8000, < 9000 gives 9000, else 10000 (strict `<`).
   * **`TAC_Lines_7500` (309878BC) texture = `"%d_LINES_%2.2d"` (dialMax, customHudIndex)**, e.g. `8000_LINES_00`.
   * **`RPM_REDLINE` (CDFCE1B0) +0x8C = redline angle** (degrees) from this table (non-drag):

| dial | redline ≥ … → angle |
|---|---|
| 7000 | ≥6500 164.5 · ≥6000 149.5 · ≥5500 131.5 · else 113.5 |
| 8000 | ≥7500 165 · ≥7000 152 · ≥6500 138 · ≥6000 123 · else 110 |
| 9000 | ≥8500 166 · ≥8000 154 · ≥7500 140.5 · ≥7000 127 · else 115 |
| 10000 | ≥9500 167 · ≥9000 156 · ≥8500 145 · ≥8000 134 · else 123 |

   (The drag table is 17.25..42; it is not used here.)
3. `DAT_008F511C = 2`, then the "HUDLoaded" event.

No script is played by name at load. FEng plays INIT on every object; many INITs chain to HIDE (all the pop-up
groups: 82D60021, 32A7A521, 321_GO_GROUP, WRONGWAYIMAGE, the pursuit groups, the milestone groups, EventIcon,
Engage_Mechanic, GENERIC_ICONS, TOTAL_INFRACTIONS_TEXT, Milestone_TollBooth, and others). The cluster groups
(TRACK_MAP, 157C5B37, AB9C1D4D, 5C7A5C8A, 7FAC287B, 00A1A862) have INIT alpha 255, so they are visible.

### 3.2 MultiImage (type 12) data
SA is 0x90 bytes: the base 0x54, then TL-UV for layers 1-3 (0x54/0x5C/0x64), BR-UV for layers 1-3 (0x6C/0x74/0x7C),
then PivotRot xyz at 0x84 (packages use (0.5, 0.5, 0)). Extra fields: `M1..M3` = texture hash per layer;
`Ma/Mb/Mc` = flags. **+0x8C (PivotRot.z) = rotation in degrees of the layer UVs about the pivot.** This is how all the
arc fills work: the base texture is drawn through the rotated mask in M1.

| object | base tex | M1 mask | driven by |
|---|---|---|---|
| RPM_REDLINE CDFCE1B0 | Redline_Color | REDLINE_MASK 0x732C6E0D | redline angle (load) |
| EDFB6D37 | Meter_Backing2 | HEATMETERMASKTOP 0xCB0B685D | N2O fill |
| Speedbreaker_Meter_Multi_Image 5DF2F5B4 | Meter_Backing2 | HEATMETERMASKTOP | speedbreaker fill |
| 862824C9 / 4B2CBE1B | Meter_Backing | HEATMETERMASKTOP | heat halves |
| 9F59065A / 69AA01E7 | Radar_LEDs_Color | RADAR_LEDS 0x6CCD6830 | radar strength (UV) |
| TRACK_MAP1..4 | Track2000_map | MINIMAP_MASK 0x40CDC515 | minimap |

### 3.3 Fade, activate, widescreen
* HUD vtbl+0x10 (`FUN_00569570(bool b)`) sends package messages. b = true sends **FADE_IN** (0xBCC00F05) and
  **DEACTIVATE**; b = false sends **FADE_OUT** (0x54C20A66) and **ACTIVATE**. Package responses: FADE_IN plays
  FADEIN (alpha 0→255, 600 ms) and FADE_OUT plays FADEOUT (255→0, 600 ms) on TRACK_MAP, 157C5B37, AB9C1D4D,
  5C7A5C8A, 7FAC287B and 00A1A862. ACTIVATE/DEACTIVATE play FADEIN/FADEOUT only on **82D60021** (the bottom-centre
  speedbreaker bar). So when the HUD is faded out (for example during speedbreaker slow-motion) only that bar stays.
* **Widescreen** (`FUN_005696C0`, every frame): when G+0x34 changes, send **WIDESCREEN_MODE** (0x62ED04EC) or
  **NORMAL_MODE** (0x53EC068C) to the package. Responses: **1603009E** (left cluster) gets WIDE, pos x −120, or NORM,
  x 0. **5D0101F1** (right cluster) gets WIDE, x 329.65, or NORM, x 209.65 (+120). The minimap widget also shifts
  its own objects by −120/+120 in x (`FUN_005678A0`). There is **no scaling**: the 640x480 layout simply gains ±120 px
  at the sides (an 880-wide virtual screen).

## 4. Widgets (HUD_SingleRace objects)

### 4.1 Hud_Speedometer (`SpeedometerGroup`)
| object | set |
|---|---|
| SPEED_DIGIT_3 / _2 / _1 (TAC_NUMBERS) | v = \|speed m/s\| × 3.6 (km/h) or × 2.23699 (mph). h = (int)(v·0.01); r = v − 100h; t = (int)(r·0.1); o = (int)(r − 10t). DIGIT_3 = "%d" h, DIGIT_2 = t, DIGIT_1 = o. **Hide DIGIT_3 if h == 0; hide DIGIT_2 if h == 0 and t == 0**; DIGIT_1 always shown |
| 3rdPersonSpeedUnits C3383B63 | label "KM/H" (0x8569A25F) or "MPH" (0x8569AB44) from G+0x3B |
| 4113F134 "888" | untouched (the unlit-segment backdrop) |

### 4.2 Hud_Tachometer (`GaugeCluster`)
| object | set |
|---|---|
| 3rdPersonNeedle F0250DAC | **angle = 66 + 228·clamp01(rpm / dialMax)** degrees (wrapped to 0..360) → `514E20` Z rotation. Package rest q(0,0,0.53,0.848) ≈ 64°. (If global 0x988E04 == 1, an alternate mode, the needle is a bar instead: sizeX = rpm/max · initial sizeX, and the REDLINE script plays while rpm ≥ redline.) |
| 3rdPersonGear B84589BE | "%c": gear 0 → 'R', 1 → 'N', 2..9 → '1'..'8' |
| Shift_light 25CF2F74 | shift state ≥ 2 → play **GREEN** (colour 255,255,255,255), else INIT (0,0,0,128) |
| TAC_Lines_7500, RPM_REDLINE | set once at load (3.1) |
| 05D19F25 TAC_FILL_00, 8BC6EBEF | untouched |
Setters (+0x30 iface): +4 rpm, +8 (redline, maxRpm), +0xC SetGear(gear, shiftState, keep), +0x10 bool (gear text
colour 0x88000000 vs 0xFF000000; skipped in mode 1), +0x14 bool (when it toggles, the needle colour is inverted:
`~c | 0xFF000000`).

### 4.3 Shift light (Hud_ShiftUpdater, mask 0x20000000)
In HUD_SingleRace only `Shift_light` exists from its object set. Shift potential (the iface setter `5A0DB0`; MW enum
0 none, 1 down, 2 up, 3 good, 4 perfect, 5 miss): **< 3 → hide Shift_light**; 3/4/5 → show it and play
PulseBlue/PulseGreen/PulseRed. Those scripts are absent here, so only the show/hide applies. Combined with 4.2:
the light is visible for potentials 3-5 and lit (GREEN) for ≥ 2.

### 4.4 Hud_TurboMeter (`TURBO_GROUP`)
`3rdperson_TurboDial` rotation (on change) = −((boost + 20)·0.025·90 − 45) degrees. boost is in the sim's units,
range −20..+20, which gives +45°..−45°. `TURBO_LINES` is untouched. Visible only with Gauges on and a turbo car.

### 4.5 Hud_NitrousGauge (group 87C38E97)
| object | set |
|---|---|
| EDFB6D37 (MultiImage) | +0x8C = **175·(1 − fill)** (fill 0..1; 180 in mode 1) |
| 27DDF583 (N2O icon group) | fill ≤ 0 → INIT; fill < previous (burning) → **ACTIVATE** (0x77031C70); else **PULSE** (NitrousGauge::SetNos in the decomp; this row had them swapped) |

### 4.6 Hud_SpeedBreakerMeter (Speedbreaker_Meter_Group + bottom bar 82D60021)
On change: Speedbreaker_Meter_Multi_Image +0x8C = **175·(1 − fill)**. While 82D60021 plays FADEIN (activated, 3.3):
bar **1FDAF669** width = fill × 216 (initial width), keeping the left edge (`564AF0`); fill ≤ 0.3 → flash script
026DED57, else **REGULAR** (0x620E4851). SPEEDBREAKER_METER_ICON_GROUP: fill > 0 → ACTIVATE, else INIT.

### 4.7 Hud_RaceInformation (`RaceInformationGroup`, racing only)
Data: +0x38 numRacers, +0x3C numLaps, +0x40 position, +0x44 = min(lap, numLaps), +0x48 lap time (s), +0x4C bool
"sudden death", +0x50 % complete, +0x54 / +0x58 tollbooths passed / total.
| object | set |
|---|---|
| POSITION_GROUP | INIT (shown); HIDE in tollbooth races |
| 9C183BF8 (top digit) | "%d" position |
| 3BBD6268 (below divider) | "%d" numRacers |
| Milestone_TollBooth | tollbooth race: APPEAR, otherwise HIDE. 1CDDD8D0 = "%d" passed, 18A7ACD2 = "%d" total |
| 9CB5C95D + Complete_Text | race type 1/2/3/≥6: Complete_Text = "LAP" (0xBF9C), 9CB5C95D = **"%d/%d"** (lap, laps). Types 0/4/5: "COMPLETE" (0x59BB1918), **"%d %%"** (int pct) |
| CurrentLapTime | sudden death: label "SUDDEN DEATH!" (0x733C8147); else time string (4.20) of the lap time |

### 4.8 Hud_LeaderBoard (`LeaderBoardGroup`, 4 rows)
* Player option LeaderBoard (P+0x85) off → HIDE on the group; on → INIT.
* Rows i = 1..min(numRacers, 4): `LeaderText_i` = "%$d" racer position; `LBData_i` alternates between two pages
  every **90 frames**: all rows play LEAVE, then when LEAVE finishes play APPEAR and flip the page.
  * page A: racer name ("%s").
  * page B: knocked out → "K.O." (0x5D82DBA2); busted → "BUSTED" (0x532B5186); speedtrap race → "%$0.0f %s"
    (speedtrap total, MPH ×0.27778×2.23699, or KM/H); the player's own row → "-----"; others →
    "+%$0.0f %s" / "-%$0.0f %s" = \|d_i − d_player\|·0.01·scale, where scale = GRaceStatus+0x4484 (×3.2808 when
    imperial) and the unit is "M" (0xE2078322) or "FT" (0x2E8496B1).
* Rows ≥ count (all rows if count ≤ 1): hide LBData, LeaderText, OL_ICON and LBBacking.
* LBBacking of the player row → **Highlight**, others → INIT.

### 4.9 Hud_Coundown (`321_GO_GROUP`, 321_GO, 321_GO_SHADOW)
Start (iface +4) sets state 4. Steps use the game timer (4000 ticks/s):
state 3: play **THREE**, text "%d" (3) on both strings → 1 s → **TWO**, "2" → 1 s → **ONE**, "1" → 1 s →
**GO** (0x535), text label "GO!" (0x61223AB5), sound event "NIS 321". This step also triggers the
EN_TollBoothArm_01..04 animations → 6 s → HIDE.

### 4.10 Hud_WrongWIndi (`WRONGWAYIMAGE`)
Wrong-way on: after **2 s** continuous, play 0x47510B1E (or 0x908E787E in widescreen). Both blink 5x, then slide
off to x −224 / −343 over 3.6 s. Off for 2 s → HIDE. Racing only.

### 4.11 Hud_GenericMessage (group E0BA07EC → 32A7A521 text, icon 6DD754EC, glow CAEC9D04)
`Show(text, keepIfPlaying, msgScript, iconScript, iconTex, priority)` (iface +4): rejected if priority < the
current one. Plays INIT on E0BA07EC. Both DA768EA6 strings in 32A7A521 get "%s" text (`^` = newline). msgScript
plays on 32A7A521: 0x8AB83EDB 2.4 s, 0xA19BB14C 3.2 s, 0x9D73BC15 3.2 s, 0x9D73BC16 3.6 s, 0x821E6378 1.2 s
(loops), 0x4F79CBA2, 0x5230FAF6. If iconTex is set: 6DD754EC texture = iconTex and play iconScript; else HIDE it.
Update: when msgScript finishes, HIDE 32A7A521 and the icon, priority = 0. iface +0xC: busy (priority > 0); iface +8:
play E1C034FC on E0BA07EC (dismiss).
Senders: **RaceOverMessage** (4.12), **TimeExtension** (4.13), cops destroyed (4.14), menu/zone code
(iface id 0x5650B0 users 0x60A953, 0x6113A8, 0x6121EB, 0x63A6A1, …).

### 4.12 Hud_RaceOverMessage
At race end (racer state 1) it sends GenericMessage(text, 0, 0x8AB83EDB, 0, 0, prio 5). In this order:
engine blown "Blown engine!"; totaled "Totaled!"; knocked out "You were Knocked Out!\nYou Lost!"; time
expired (time limit > 0, race time ≥ limit, not a knockout loss) → "Time's Up!" (script 0x9D73BC15); challenge failed
"Challenge Incomplete\nYou Lost!"; otherwise `"%s\n%s"` = position string + "Your time: %s". The position string is
Winner!, You finished second! … seventh!, or **"You were last!"** when position == numRacers > 1
(table 0x8F3C1C).

### 4.13 Hud_TimeExtension (tollbooth race only)
* Bonus received (+0x40 > 0, GenericMessage idle): "TIME BONUS!\n+<time>", script 0x8AB83EDB, icon TIMER_ICON with
  0x609F6B15, prio 3.
* 2 s later: "NEXT TOLLBOOTH\n<time left>", same script and icon.
* Time left ≤ 10 s: "HURRY!\n<time>", script 0x821E6378 (10..5 s) or 0x4F79CBA2 (≤ 5 s), restart, prio 1.

### 4.14 Hud_PursuitBoard (free roam / pursuit; owns the evade/busted and cooldown bars)
Groups: DE89E070 pursuit list (BOUNTY, timer), 8674E6D4 bounty row, E0B1430B cop counters, 7B422BA3 EVADE/BUSTED
bar, 84A226EC COOLDOWN bar, 6A144066 BACKUP timer, 5C2A4F20 HIDING box.
* Not in pursuit: LEAVE on DE89E070, 7B422BA3, 84A226EC and E0B1430B.
* In pursuit: APPEAR DE89E070 and 8674E6D4. **FC39CB0A** = time string of the pursuit time. **875E92EB** = "%$d" bounty.
* **v = bar value (−1..1, + = busted side)**. v ≤ −1 means **cooldown**: the bar group plays 0x92975065 (switches to
  COOLDOWN) with sound 0xC. **CF817638 width = (1 − coolT/coolTotal)·216**, left edge kept. Hiding (+0x3C > 0) →
  APPEAR 5C2A4F20 ("HIDING" + Hide_Car_Icon), else LEAVE.
* Otherwise: 7B422BA3 APPEAR (or 0x92975065 on 84A226EC to switch back). Counters: **814918CA** = cops involved
  (minus 1 if the helicopter flag is set), **A16F9F1E** = cops destroyed, **5FA70D4C** = cops damaged (all "%$d").
  Backup timer t: t ≤ 0 → LEAVE 8C20A763; ≤ 10 s → 026DED57 flash; else APPEAR. **BEE44775** = time string.
* Bar segments (initial widths 54/48/11/48/54):

| object | width factor | anchor | script |
|---|---|---|---|
| E0E0169B (busted end) | v > 0.5 ? (v − 0.5)·2 : 0 | right edge | v > 0.5 → 026DED57 else INIT |
| 8EEEBD33 | v > 0.5 ? 0.9 : (v > 0.1 ? 2v − 0.1 : 0) | right edge | |
| 47A4E2A9 (centre) | — | — | \|v\| ≥ 0.1 → SHOW, else PULSE |
| 8E3653A6 | v < −0.5 ? 1 : (v < −0.1 ? −2v − 0.1 : 0) | left edge | |
| 30F39A6F (evade end) | v < −0.5 ? (v + 0.5)·−2 : 0 | left edge | v < −0.5 → 026DED57 else INIT |

* Count setters: cops involved up → **active** on Cops_Icon 3787231C; otherwise FB12D252, and if it went down,
  0x579DBC92 on 3B9919A8. Destroyed up → **active** on +0x68; GenericMessage "<cop type>^Combo Bounty: %$d" (value =
  a·b) with icon COPS_TAKENOUT_ICON, script COPS, prio 2. Types: Civic Cruiser (copmidsize), Civic Undercover
  Cruiser (copghost), State Pursuit Vehicle (copgto), State Undercover (copgtoghost), Federal Pursuit Vehicle
  (copsporthench), Federal Undercover (copsportghost), SUV Heavy (copsuv / copsuvpatrol), SUV Light (copsuvl),
  Cross (copcross / copsport).

### 4.15 Hud_HeatMeter (C46A80A9; mask 0x4000, so the minimap must be on)
h = heat (override +0x3C if > 0, else +0x40); n = (int)h; f = h − n.
| object | set |
|---|---|
| 862824C9 (first half) | +0x8C = 175 − 2·min(f, 0.5)·175 |
| 4B2CBE1B (second half) | +0x8C = 175 − 2·max(f − 0.5, 0)·175 |
| 7F91DA62 ("x10" group) | h < 1 → hidden; else shown, the two A81A2028 strings = **"x%d" n**; f < 0.5 → 0x41E1FEDC (pop/fly-in, 2.4 s), else INIT |
| 6F85ED55 (heat icon) | h ≤ 0 → INIT; f ≤ 0.75 → ACTIVATE; else **Warning** (flash 360 ms loop) |
So heat level = integer part (1..5+), and the dial shows progress to the next level.

### 4.16 Hud_Reputation (EA903012) and Hud_CostToState (D60FC075 = `CTS_GROUP`)
Value set (Reputation: any change; CTS: only increases) → show for **120 frames**: label text "TOTAL BOUNTY:"
(41A55ECF) / "PURSUIT CTS:" (64247241), value "%$d" (9B0AC8CA / 3FF5F33C), APPEAR once; afterwards LEAVE.
CTS_GROUP gets HIDE in its ctor. 3F8644CA (CASH) is not driven by any widget.

### 4.17 Hud_MilestoneBoard (MILESTONE_INFO_GROUP / MILESTONE_ICON_GROUP; free roam only)
Up to 4 milestones (records of 0x18: tex, value, goal, done). None active → LEAVE on both groups. Otherwise APPEAR
both. 894662C5 = "%d" milestones done. MILSTONE_ICON_i: SHOW + texture = record icon (Milestone_Pursuit_MaxTime,
_Cost_To_State, _Roadblocks, _SpikeStrips, …) + alpha. Rows ≥ count → HIDE. Selected row (one incomplete, or cycling
every 3 s through incomplete ones with AFF37F61 → D6C950A0 on F4405EC0) → its MILESTONE_ICON_BACKING_i plays
Highlight, the others INIT. C3E48FBF = `FUN_00599740(value, goal, type)` (e.g. "10,000,000/10,000,000").
F4405EC0 and 5C697702 are SHOWn while incomplete milestones exist, otherwise HIDE.

### 4.18 Hud_Infractions (INFRACTIONS_HEADER_GROUP, GENERIC_ICONS, INFRACTION_GROUP_1..4, TOTAL_INFRACTIONS_TEXT)
Add(text): the first INFRACTION_GROUP_i currently in HIDE gets APPEAR, and both its strings = text. GENERIC_ICONS
APPEAR. TOTAL_INFRACTIONS_TEXT = "%d" total (GRaceStatus infractions). Update: once all four groups are back in
HIDE, GENERIC_ICONS gets LEAVE.

### 4.19 Hud_RadarDetector (062743F5)
* Rear-view mirror camera (`FUN_006BF530() == 2`) and G+0x39 on: HIDE 839E7D77 (Radar_Backing), APPEAR 9EE06631
  (mirror backing). Otherwise the reverse. A disabled flag (+0x64 set, +0x65 clear) puts the group in LEAVE/HIDE.
* Target (+0x54 > 0): the LED fill s rises by 0.1 per frame to 1.0. A beep resets s to 0.3 every
  1.5·max(0.1, +0x54) s. Both LED MultiImages get layer-0 BR-UV = (s, 1). Radar_DirectionArrow rotation = bearing
  (rad → deg). If the target type == 2 (speed trap) → **SPEEDTRAP** on the arrow, icon and LEDs; else INIT.
  No target: UV (0, 1), arrow HIDE, icon INIT.

### 4.20 Time string (`FUN_006547A0` → `6541B0`, flags 4, English)
ticks = (int)(sec·4000 + 0.5); ms = (ticks % 4000)·1000/4000; hh = ms/10. Format: h > 0 → "HH:MM:SS"; m == 0 and
s == 0 → "0.hh"; m == 0 → "ss.hh"; else **"mm:ss.hh"**. The leading digit becomes a space when < 10
(e.g. " 1:05.23", " 5.23").

### 4.21 Hud_MenuZoneTrigger (Engage_Mechanic, EventIcon)
Prompts are **icon-only**; HUD_SingleRace has no prompt text. Entering an event zone: EventIcon texture by race
type: 0 MODE_ICON_SPRINT, 1 MODE_ICON_CIRCUIT, 2 MODE_ICON_DRAG, 3 MODE_ICON_LAP_KNOCKOUT, 4/10 0x1A091045
(tollbooth), 5/9 0x66C9A7B6 (speedtrap). A menu zone uses carlot 0x4EAEE18B, safehouse 0x0ED39F69, customshop
0x0CF07089. EventIcon then plays APPEAR (unless APPEAR or BLINK is playing), button ring 0A729B1B plays APPEAR, and
sound 0x13 plays. Repeated after 6 s if still inside. Leaving: LEAVE on Engage_Mechanic, EventIcon and the rings
0A729B1B, 717C82AE, A206A0B4, 7180B901. Engage (HUDACTION_ENGAGE_EVENT): start the event (0x624810) or enter the zone
(safehouse / carlot / customshop).

### 4.22 Hud_Minimap (TRACK_MAP, 00A1A862)
Binds TRACK_MAP1..4, MINIMAP_NORTH_INDICATOR, PLAYERCARINDICATOR(2), TRACKMAPTARGETRING, MAP_COLOR_TINT, the
HELICOPTER_ objects, MMICON_COPCAR_0..7 / AIRACER_0..7, MMICON_CHECKPOINT and the icon pools in table 0x8F3ABC
(SPRINT, CIRCUIT, DRAG, KNOCKOUT, TOLLBOOTH, SPEEDTRAP, EVENT_RIVAL, SAFEHOUSEINDICATOR, CAR_LOT, SHOP, HIDING_SPOT,
PURSUIT_BREAKER, SPEED_TRAP_CAM, SPEED_TRAP_RACE_CAM; all hidden at bind). Map rotation = heading u16·360/65536,
but forced to 0 (north up) when the minimap option == 0 (1 = rotating, 2 = off). Map offset = ((x − ox)/scale,
(oy − y)/scale + 1) using the track's map parameters. The speed zoom computes 1 − speed/100 but is then clamped up
to 1.0, so the **zoom is effectively constant**. Widescreen shifts it by −120.

## 5. Visibility summary (single player)

| state | shown (if options allow) |
|---|---|
| always in game | speedo + tach + N2O + speedbreaker (Gauges), shift light, radar (offline), generic messages, infractions, menu-zone prompts, countdown |
| free roam (no race) | minimap + heat meter, PursuitBoard (only during a pursuit, bit 0x38), milestones (during challenge pursuits), CTS / Reputation when pursuing |
| race (GRaceStatus+0x1960 == 1) | RaceInformation (Position option), LeaderBoard (LeaderBoard option), WrongWay; no PursuitBoard unless it is a pursuit race |
| tollbooth race | + TimeExtension messages; POSITION_GROUP replaced by Milestone_TollBooth |
| cooldown | PursuitBoard in cooldown mode (COOLDOWN bar, HIDING box) |

Unresolved: names of the scripts 0x8AB83EDB, 0x9D73BC15, 0x821E6378, 0x92975065 and 0x41E1FEDC (their behaviour is
taken from the keyframes); the meaning of GRaceStatus+0x1964; the exact event that calls HUD vtbl+0x10 (fade);
and P+0x90 (drive-camera guess).

## 6. Port status (2026-10-24, in-game FE round; see fe_ingame.md)

Now as the decomp / exe (GameHud.cs, HudExtras.cs):
* Shift_light (Tachometer::Update): GREEN 0x02DDC8F0 for shift potential > 1, else INIT (was: shown above 95 % redline).
  The ShiftUpdater exists only in the drag HUD (FEPkg_Hud.cpp:505-511), so 4.3's PulseBlue/Green/Red do not apply here.
* WrongWIndi: 2 s of wrong way (game time) → 0x47510B1E / 0x908E787E, 2 s right way → HIDE (was: shown at once).
* TurboMeter: TURBO_GROUP only for a turbo car [?: CarSpec Spool > 0 stands for HUD vtbl+0x20, whose caller in the feeder
  0x6F13F0 is not traced]; needle −(min + (psi+20)/40·(max−min)), ±45° (drag 200..270°), from PlayerCar.BoostPsi.
* HeatMeter: multiplier 0x41E1FEDC in the first half of a level else INIT; icon INIT / ACTIVATE / Warning (> 0.75).
* NitrousGauge icon: INIT / ACTIVATE (burning) / PULSE.
* LeaderBoard (FeLeaderBoard.cpp): 90-frame LEAVE → APPEAR page flip, page B gap = pct difference · race length (M / FT),
  BUSTED / K.O. / speedtrap totals, player backing Highlight, rows past the racer count invisible; RaceInformation's total
  is the racer count.
* RadarDetector: APPEAR / LEAVE / HIDE scripts on the group and backing as the decomp, LED +0.1 per frame, 0.3 reset
  every 1.5·max(range, 0.1) s, SPEEDTRAP scripts for a camera target [?: SetTarget's caller is not decompiled; target =
  nearest cop car or speed-trap camera within 250 m].
* Plain FEViews now loop flag-1 scripts (Warning, PULSE, …) and keep `LastScriptId` for FEngIsScriptSet.
* RaceStatsTracker (InGame/RaceStats.cs) is ticked from the HUD during races for the results screens.
Still open: the gear-string colour while shifting (no shift-in-progress flag on PlayerCar), the Reputation widget. The
engine mode, the fade messages and the feature flags are in §7.

## 7. HUD on the FEng engine (FE remainder round, 2026-10-24)

* GameHud opens the HUD package with `FEView.Open(..., engine: true)` (UI/FEView.cs + InGame/FEngEngine.cs): the package's
  INIT chains, script events (6F85ED55's INIT → 0x5FDAD6BC → HIDE on 10E50F5E etc.), MsgR / PkgR responses and the
  flag-bit-0 / 0x40 passes replace the old hide-set; FEngHud has no pad input (game messages are dropped).
* Open: FADE_IN (0xBCC00F05) + DEACTIVATE (0xDE7BDA39) are sent — HUD vtbl+0x10 (0x569570) with true; [?] the exe caller.
  Every frame (FEngHud::Update 0x58CA30): DetermineHudFeatures 0x57CA60 → SetHudFeatures 0x57CDB0 when changed →
  HudElement::Toggle 0x5793F0 per widget (visible unless flag 0x40 / invisible, recursively through FUN_00514CC0 / 514C70);
  then SetWideScreenMode 0x5696C0 (WIDESCREEN_MODE 0x62ED04EC / NORMAL_MODE 0x53EC068C on a change, aspect > 1.45 [?: G+0x34]).
* `UI/HudFeatures.cs` holds the widget object lists from the decomp's HudElement ctors (RegisterGroup adds a group's leaves
  recursively, RegisterObject one object): Speedometer SpeedometerGroup; RaceInformation RaceInformationGroup; LeaderBoard
  LeaderBoardGroup; NitrousGauge 87C38E97 + EDFB6D37; GenericMessage 32A7A521 + 6DD754EC + CAEC9D04; TurboMeter TURBO_GROUP;
  WrongWIndi WRONGWAYIMAGE; Coundown 321_GO_GROUP; Tachometer GaugeCluster + 045E9562; HeatMeter C46A80A9 + 862824C9 +
  4B2CBE1B; CostToState CTS_GROUP; PursuitBoard DE89E070, 7B422BA3, E0B1430B, 8674E6D4, 84A226EC, 6A144066, 3C165F39,
  BEE44775; MilestoneBoard MILESTONE_INFO/ICON_GROUP; MenuZoneTrigger Engage_Mechanic + EventIcon + DA8141D4; Infractions
  INFRACTIONS_HEADER_GROUP, INFRACTION_GROUP_1..4, GENERIC_ICONS, TOTAL_INFRACTIONS_TEXT; RadarDetector 062743F5 + 839E7D77
  + 9EE06631; SpeedBreakerMeter Speedbreaker_Meter_Group + Speedbreaker_Meter_Multi_Image + 82D60021; GetAwayMeter
  GetawayDistanceMeter; drag: ShiftUpdater Shift_light + ENGINE_HEAT_SHIFTLIGHT_GROUP + SHIFT_GROUP, EngineTempGauge
  Engine_Heat_Meter_Group + ENGINE_METER, DragTachometer RPM_fill, Drag_Turbo_Backing/Lines, 3rdPersonSpeedUnits,
  SPEED_BACKING, RPM, TAC_Lines_7500, RPM_Redline, 3rdPersonNeedle, 3rdPersonGear. The minimap widget (0x40010000) toggles
  Minimap.cs. [?] The previous-flags value at start (the ctor 0x5A6600 leaves +0x18/+0x1C) is taken as all ones.
* Determine (§2) with the PC packed PlayerSettings: +0x80 Gauges, +0x81 Position, +0x85 Leaderboard, **+0x90 =
  SplitTimeType (Off = 4)**, G+0x3C / +0x3D minimap modes; 0x1000 = pursuit race [?: the vehicle flag / GRaceStatus+0x1964
  == 2 term]; 0x4 (RaceOverMessage) not raised.
* Blend state: the HUD's additive textures (BasePoly_Add, Heat_Icon_Additive, button_ring, outter_ring, IconSelection_Glow,
  Pursuit_LeaderList_Pulse, Helicopter_Line_Of_Sight) draw SrcAlpha / One, Radar_LEDs_Color blend-off with an alpha test;
  the leaderboard backings are plain BasePoly at alpha 80 (fe_ingame.md §10.4).
* The EA Trax chyron (EA_TRAX.fng) now shows over the HUD (fe_ingame.md §12).

## 8. Round 9 (2026-10-25): FEPkg_Hud, FeHudElement, FePursuitBoard, FeMinimap diffed against the PC code

Port: `UI/GameHud.cs`, `UI/HudFeatures.cs`, `UI/HudWidgets.cs` (new: HudGenericMessage, HudPursuitBoard), `UI/HudPursuitFeed.cs`
(new: a partial of PursuitSystem with the IPursuit getters the feeder needs), `UI/Minimap.cs` (rewritten), `UI/HudExtras.cs`;
the FEng setters they use are fe_ingame.md §13.3.

### 8.1 FEngHud ctor / SetHudFeatures / Update (PC 0x5A6600 / 0x57CDB0 / 0x58CA30; FEPkg_Hud.cpp:478, 1022, 617)
* The ctor makes **every object of the package invisible** (FEngSetAllObjectsInPackageVisibility(pkg, false), 0x5248C0) right
  after pushing it, creates the widgets for the HUD type (they register their objects), hides the two online lines when
  offline, and runs `CurrentHudFeatures = 0; SetHudFeatures(-1); SetHudFeatures(0)`. Objects no widget registers stay
  invisible — the port's hand-made hide list in GameHud.Bind (split divider, map groups, mirror backing, online lines, …) is
  gone; §7's "previous flags start as all ones" guess is replaced by the exe's sequence.
* SetHudFeatures toggles a widget when its **own toggle bit** changes (not its full mask): Speedometer 0x8000000, Tachometer /
  DragTachometer 0x2, ShiftUpdater 0x20000000, TurboMeter 0x20000, EngineTempGauge 0x40, NitrousGauge 0x800, SpeedBreakerMeter
  0x40000, HeatMeter 0x4000, Minimap 0x10000, GetAwayMeter 0x200, MenuZoneTrigger 0x400000, RaceInformation 0x4000000,
  LeaderBoard 0x8 | 0x10, PursuitBoard 0x100000, MilestoneBoard hi 0x4, BustedMeter 0x800000, TimeExtension 0x2000000,
  CostToState / Reputation 0x1000, WrongWIndi 0x20, RaceOverMessage 0x4, GenericMessage 0x1000000, RadarDetector 0x200000,
  Coundown 0x400, Infractions hi 0x2. Widgets per type: drag → EngineTemp / DragTach / ShiftUpdater; else TimeExtension and
  Tachometer, and for PHT_STANDARD Reputation (career context only), HeatMeter, CostToState, PursuitBoard, MilestoneBoard,
  BustedMeter, MenuZoneTrigger, Infractions, RadarDetector; Minimap for standard and split 1; SpeedBreaker / GetAway for
  standard and drag. FEngSetVisible on PC skips flag-0x40 objects.
* Update: each widget's Update only while the HUD shows anything and its element is visible (`GameHud.On(mask)`; GenericMessage
  included — §1's "always updates" was wrong); SetWideScreenMode after SetHudFeatures; JoyHandle (HudExtras.HudActions) only
  with CurrentHudFeatures ≠ 0, in roaming: PAD_LEFT world map, PAD_RIGHT SMS, PAD_DOWN Blacklist unless D-Day (the port's extra
  "free-roam stage" / "not in a pursuit" conditions are gone; the SMS inbox still needs a career profile).
* DetermineHudFeatures (PC 0x57CA60, re-read): all 0 unless the player's view runs the drive camera (CM_DRIVE_CUBIC) **without
  the look-back angle** [?: ChaseCamera has no public look-back state — GameHud reads its condition, B held by a human driver;
  the director's TrackCar / TrackCop / ICE shots count as not the drive camera], not restarting a race (0x91CF28), no pause,
  sim active, none of Pause_Main / MC_Main / PostRace_Results, resources loaded, no NIS, widescreen synced. **0x1000 = player
  settings +3 (ScoreOn) and (race context career (GRaceStatus+0x1964 == 2) or a pursuit race)** — §2's "vehicle flag" is
  ScoreOn; the port had pursuit races only. Minimap 0x10000 needs TrackInfo 2000 loaded (always for L2RA).
* SetWideScreenMode (0x5696C0): the HUD starts "not wide"; a change sends WIDESCREEN_MODE / NORMAL_MODE **and calls
  Minimap::AdjustForWidescreen** (port: on the aspect ratio [?: FEDatabase video WideScreen]).
* HudResourceManager custom HUD (PHT_STANDARD; GetCustomHudTexPackFilename 0x58C8B0, callbacks 0x595CC0 / 0x58C930): custom
  index = HUDINDEX of the car's CUSTOM_HUD part (slot 132, else 0), GLOBAL\HUDS_Custom_%2.2d.bin; TACH_FILL_nn → 05D19F25
  (colour slot 133 HUD backing), &lt;dial&gt;_LINES_nn → 309878BC (135 character), TURBO_LINES_nn → C62AD685 (135),
  TACH_NEEDLE_nn → F0250DAC (134 needle), TURBO_NEEDLE_nn → 6D5ECE44 (134); FEngSetColor(0xFF RRGGBB of the part's RED / GREEN /
  BLUE) only when the slot holds a part; C3383B63 gets the character colour. (The decomp's CUSTOMHUD_* names belong to another
  build.) The career car, else a quick race's car, none in a pursuit race. GameHud.CustomHud.
* Speedometer: unit label by FEngSetLanguageHash KM/H 0x8569A25F / MPH 0x8569AB44; digits / gear FEPrintf.
* RaceInformation::Update (FeRaceInformation.cpp:31): tollbooth → POSITION_GROUP HIDE + Milestone_TollBooth APPEAR (scripts,
  each unless set) and the passed / total counters; else INIT / HIDE and position / racer count; Complete_Text by
  FEngSetLanguageHash (0x59BB1918 sprint / speedtrap / tollbooth with "%d %%", else 0xBF9C with "%d/%d"); lap time by
  Timer::PrintToString(4). The port's SetVisible calls on RaceInformationGroup / POSITION_GROUP / 321_GO_GROUP / Shift_light /
  WRONGWAYIMAGE ([ours]) are gone — the feature bits and the scripts decide.
* Countdown: THREE 0x03C3C2F7 / TWO 0xE479 / ONE 0xCE01 with FEPrintf "%d", GO 0x535 with the GO! label 0x61223AB5, HIDE 6 s
  later [?: the step clock is RaceManager's].
* Timer::PrintToString (Misc/Timer.cpp:239, :435; GameHud.PrintTime): (int)(sec·4000 + 0.5) ticks; hours "h:mm:ss", minutes
  "m:ss.hh", seconds "s.hh", else "0.hh" (a lone zero, not " 0.hh"), leading field without leading zero, negative / plus
  flags shift the string. `TimeString` = flags 4 (English).

### 8.2 HudElement (FeHudElement.cpp; PC 0x58BBD0 RegisterGroup, 0x5793F0 Toggle)
RegisterString / Image / MultiImage / Object (one object) and RegisterGroup (child groups recurse, leaves added), Toggle =
FEngSetVisible / FEngSetInvisible on every registered object by (features &amp; mask) — HudFeatures, exact now; IsElementVisible
= the toggle state (`On`).

### 8.3 GenericMessage (FeGenericMessage.cpp; HudGenericMessage)
RequestGenericMessage: refused below the current priority (IFengHud.h: Priority_1 = 5 highest … Priority_5 = 1); INIT on
E0BA07EC unless set; text bSafeStrCpy into 64 chars and FEPrintf on 32A7A521 (group: both strings); the message script set
(single-frame requests only when not already set); icon script + FEngSetTextureHash, or HIDE. Update: when the script stops
running (or after the first frame of a single-frame request) HIDE the line and the icon, priority none. ZoomOut: E1C034FC.
Systems.HudMessage → Priority_4 with 0x8AB83EDB [?]. HudExtras' own message code and GameHud's 2.6 s timer are gone.

### 8.4 PursuitBoard (FePursuitBoard.cpp; PC 0x59FC80, Update 0x57AEE0) and its feeder (LocalPlayer::UpdateHud 0x6F17F2)
* Feeder (run out of races or in a pursuit race), IPursuit vtable on PC (AIPursuit 0x892770): SetInPursuit(!IsPursuitBailed),
  IsInView(IsPerpInSight +0x28), PursuitDuration (+0x18), CoolDownTimeRemaining (+0x20) / Required (+0x24 = mCoolDownTimeRequired
  − 7), NumCopsInPursuit = **GetNumCopsRemainingInWave** (+0xD0: max(required − evaded, 1)), NumCopsDestroyed (+0x98) with the
  most recent type / bonus multiplier / rep points (+0xDC / +0xD4 / +0xD8), NumCopsDamaged (+0x94), TotalNumCopsInvolved (+0xA0),
  HeliInvolved (+0x88), TimeUntilBusted(+0x60, IsPerpBusted +0x74), TimeUntilBackup = GetTimeToBackupSpawned (+0x118); the
  perpetrator: IsHiding / TimeUntilHidden = IsPartiallyHidden(&amp;HowHidden) (+0x34), PursuitRep = pending rep normal + from cop
  destruction (+0x40 + +0x44). Port: PursuitSystem (TimeUntilBusted, IsPerpBusted, BackupEta, PursuitTime, PerpHowHidden,
  Bounty, Stats) + HudPursuitFeed.cs (NumCopsRemainingInWave, IsHeliInPursuit, MostRecentCopDestroyed). [?] cooldown remaining
  / required from CooldownProgress; the pending rep = Bounty.
* Update literal: out of pursuit LEAVE on the board (if APPEAR), the meter (unless LEAVE / HIDE), the cooldown meter (if APPEAR
  or 0x13F51124) and the icons (if APPEAR). In pursuit: APPEAR board and summary, FC39CB0A time string, 875E92EB "%$d" rep.
  TimeUntilBusted ≤ −1 (cooldown): 0x92975065 on the meter group when the cooldown meter is not in 0x13F51124 (+ UI sound
  COMMON_SCROLL_START), CF817638 width = 216 · (1 − remaining / required) keeping its top-left x, HIDING box 5C2A4F20 APPEAR /
  LEAVE. Else: switch back (0x92975065 on the cooldown meter + sound) or APPEAR the meter after HIDE / LEAVE, LEAVE the hiding
  box, counters "%$d" (cops in pursuit − 1 with the helicopter, destroyed, damaged), the backup box 8C20A763 APPEAR (> 10 s) /
  flash 026DED57 (≤ 10 s) / LEAVE (none) and BEE44775 time string, then the five bar segments by FEngSetSizeX and
  FEngSetBottomRightX / SetTopLeftX on their own current edges (factors as §4.14; centre 47A4E2A9 PULSE for |t| &lt; 0.1 else
  SHOW). Setters: busted → BUSTED message (0x532B5186, 0x9D73BC15, Priority_1); cops up → "active" on 3787231C, down →
  FB12D252 / 0x579DBC92 on 3B9919A8; destroyed up → the localised type string (copcross / copsport 0x8FE02B9F, copmidsize
  0xF49A550A, copghost 0x902311DA, copgto 0x9BFD379F, copgtoghost 0x65E97524, copsporthench 0x4EE07213, copsportghost 0x4ED00512,
  copsuv / copsuvpatrol 0x9BFD6AD3, copsuvl 0x1BAAC57F) formatted with rep × multiplier, COPS_TAKENOUT_ICON + COPS 0x13FF94,
  Priority_4; damaged up → "active" on 5FA70D4C. (The old GameHud.Pursuit bars / visibility were [ours].)
* [hook note] PursuitSystem.CopDestroyed also raises its own "Combo Bounty" HUD message; at the same priority the board's
  request of the same frame replaces it.

### 8.5 Minimap (FeMinimap.cpp, FeMinimapStreamer.cpp; Minimap.cs rewritten)
* Calibration: TrackInfo (chunk 0x34201 in GLOBAL\GlobalB.lzc, 0x120 bytes each; harness feeng9 trackinfo): track 2000
  "Most Wanted World" upper-left (−1224.8894, −1591.1449), width 6659.332 m. ConvertPos = ((X − ulX) / w, (ulY − Y) / w + 1)
  — the old fitted transform (0.1538 px/m, 188 / 779.3) was this within rounding. `Minimap.MapPoint` keeps its API.
* Maps (ChooseMinimapTextureName 0x57BEC0): roaming MINI_MAP / MINI_MAP_UNLOCK_2 (bin ≥ 9) / MINI_MAP_UNLOCK_1 (bin ≥ 13);
  races MINI_MAP_&lt;event id with '.' → '_'&gt; (267 files ship, each 64 chops with the race route drawn in), pursuit races by
  region; TRACKS\L2RA\&lt;name&gt;.BIN = 64 × chunk 0x3A100 (LZ + texture pack, &lt;name&gt;_CHOPn 128×128, AlphaBlend 2 = additive
  over the MiniMap_Backing_Color disc). [?] Checked every frame (the exe picks it when the HUD loads).
* Update (0x59DB50): rotate unless split screen or the mode is 0; SetupMinimap with last frame's zoom (the 2×2 chop block
  around the player, textures, base UVs (zoom − 1 .. 1), mask UVs − offset, TRACK_MAP pos / pivot written directly); heading
  bAngToDeg(bATan(dir.y, dir.x)); zoom 1 − speed/100 clamped up to 1; UpdateTrackMapArt (indicator vs map rotation);
  UpdateCopElements (flash counter 0..8, blue / red flashes, at most 8 cars within engagement range, the helicopter with its
  TRACKING / INIT line of sight); UpdateAiRacerElements; player 2 in split screen; UpdateGameplayIcons: GatherVisibleIcons
  (0x5E4940, sorted by squared 2D distance) placed per type up to 8 when IsMapItemEnabled (kGameplayIconInfo 0x8F3AB8: SPRINT
  0x100, CIRCUIT 0x80, DRAG 0x400, KNOCKOUT 0x200, TOLLBOOTH 0x1000, SPEEDTRAP 0x800, rival 0x8000, SAFEHOUSE 0x200000, CAR_LOT
  0x800000, SHOP 0x400000, HIDING_SPOT 0x2000000, PURSUIT_BREAKER 0x4000000, SPEED_TRAP(_RACE)_CAM 0x100000, CHECKPOINT
  0x1000000). UpdateElementArt: pinned to the 0.06 rim, fading 0.125 → 0.23, 1024 px per map width, FEngSetCenter /
  RotationZ / colour alpha, GPS glow E8741681. AdjustForWidescreen: ±120 on the centre, the pieces and the indicator.
  MinimapShowNonPursuitCops = false (bss), MinimapShowPursuitCops = true, MinimapMaxSpeed 100.
* [?] Icons: the Lua triggers' icons + GManager's pool with the world map's visibility rule; the GPS target = an icon flagged
  0x80 or within 30 m of the GPS target; MapItems = the profile's. The GPS route the port drew is gone (the exe draws none;
  `Minimap.Gps` stays for GpsNavigation). The helicopter counts as within engagement range while in the pursuit.

### 8.6 Other HUD fixes this round
* HeatMeter: FEngSetMultiImageRot; the second half (f > 0.5 ? f − 0.5 : 0) is the PC code (0x5666A0; the decomp's is wrong);
  the multiplier FEPrintf + FEngSetVisible / Invisible.
* NitrousGauge: SetNos runs every frame (equal fill → PULSE), the arc by FEngSetMultiImageRot; TurboMeter by FEngSetRotationZ,
  gated by its feature bit (the port's own TURBO_GROUP SetVisible is gone).
* RadarDetector: LED fill = layer 0 (the RADAR_LEDS mask) bottom-right UV (FEngSetMultiImageBottomRightUVs 0x524C00, not the
  base UVs), the arrow by FEngSetRotationZ, scripts without forcing visibility.
* TimeExtension rewritten literally (§8 of FeTimeExtension.cpp: pending bonus waits for a free message line, 2 s timers,
  single-frame HURRY / NEXT TOLLBOOTH requests at Priority_5 / Priority_3, ZoomOut on a bonus).
* Reputation (career only): HIDE at creation, 120 frames of "TOTAL BOUNTY" (0x7D0171E4) "%$d" on a change, then LEAVE [?: the
  value = the profile bounty; the feeder is not traced].
* The extras' widget updates are gated by their feature bits (radar 0x200000, CTS / Reputation 0x1000, milestones hi 0x4,
  infractions hi 0x2, leaderboard 0x18); the leaderboard no longer toggles its group's visibility.
