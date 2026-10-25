# Front-end career screens: world map, SMS, Blacklist, Rap Sheet, Infractions, HUD extras (NFS MW 2005 PC v1.3)

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/Frontend/` (paths below are relative to it; `file:line`),
speed.exe v1.3 (PC addresses, read with a byte-level disassembler where the decomp is the GameCube build and may differ),
`LANGUAGES/English.bin` (label hashes resolved to English below). Companion specs: `career_flow.md` (bins, SMS triggers,
movies, markers' origin), `hud.md` (HUD widgets), `career_sms.txt` (SMS texts). This file does not repeat them.

Targets what `Runtime/UI/Screens.cs` / `GameHud.cs` lack. Already present: MainMenu, CareerMenu (MainMenu_Sub), Customize,
CarLot, MyCars, Paint, Performance, RaceSheet (SafeHouseRaceSheet), RivalChallenge, RapSheetMain (approximate), Options,
PostRace, pursuit results, Milestones (drawn in PostRace_Pursuit, **not** the real SafeHouseMilestones package; see 3.5).

## 0. Conventions used by every screen

* **Hashes.** Object/script/message ids are `FEHashUpper(name)` = bStringHash of the upper-cased name
  (`h = 0xFFFFFFFF; h = h*33 + c`). `FEngHashString(fmt, n)` = the same hash of the formatted string. Language labels are
  the same hash of the label (English.bin chunk 0x39000). Most object names are not in speed.exe; bind by hash.
* **Script names**: INIT 0x001744B3, SHOW 0x001CA7C0, HIDE 0x0016A259, APPEAR 0x5079C8F8, LEAVE 0x033113AC,
  HIGHLIGHT 0x249DB7B7, UNHIGHLIGHT 0x7AB5521A, GREY 0x00163C76, NORMAL 0x6EBBFB68,
  PULSE 0x03826A28, SNAP 0x001CBF71, UNSNAP 0x7EFE8FF4, FLIP 0x0015970A, POS1 0x001B20C2, POS2 0x001B20C3, READ 0x001C0CBB,
  SPEEDTRAP 0xFA44E387.
* **Messages**: PAD_ACCEPT 0x406415E3, PAD_BACK 0x911AB364, PAD_UP 0x72619778, PAD_DOWN 0x911C0A4B, PAD_LEFT 0x9120409E,
  PAD_RIGHT 0xB5971BF1, PAD_START 0xB5AF2461, PAD_BUTTON0..9 = 0xC519BFBF + n (BUTTON4 0xC519BFC3, BUTTON5 0xC519BFC4),
  PAD_LTRIGGER 0x5073EF13, PAD_RTRIGGER 0xD9FEEC59, BUTTON_PRESSED 0x0C407210 (accept on the current button),
  SCREEN_TICK 0xC98356BA (every frame), INIT_COMPLETE 0x35F8620B, EXIT_COMPLETE 0xE1FDE1D1. Dialog replies: yes 0xD05FC3A3,
  no 0x34DC1BCF, cancelled 0xB4EDEB6D. `0x587C018B` = "leave screen" (plays the package's exit, then EXIT_COMPLETE).
  BUTTON_n objects: BUTTON_1 = 0xCDA0A66B … BUTTON_6 = 0xCDA0A670, BUTTON_10 = 0x81B573FB. TITLE_GROUP = 0xB71B576D.
* **Package switch vs push.** "switch" replaces the top package (arg = ScreenConstructorData.Arg); "push" stacks it.
  In-game variants (`InGame*.fng`) get Arg 1 = `bIsInGame`; their safehouse twins get Arg 0. The in-game packages are in
  `GLOBAL/InGameB.lzc`; the safehouse ones in `FRONTEND/FrontB.lzc` (both already loaded by FrontEndData).
* **Common text**: `0xB514E2D8` = "%s %$d" with "Bounty:" (0xCE6B99B1) + total bounty; `0xF91A59F6` = "Cash:" (0x073B79E0) + cash.
  `%$d` = number with thousands separators.
* **Dialogs**: `DialogInterface::ShowTwoButtons(pkg, "InGameDialog.fng" in game / "" (= Dialog.fng) in FE, …, button1 text,
  button2 text, msg on b1, msg on b2, msg on back, default = button 2, blurb)`. The project's ConfirmScreen can host these.

### 0.1 ArrayScroller (feArrayScrollerMenu.cpp, used by SMS, Bounty, Milestones, RapSheet lists)
* `ArrayScrollerMenu(sd, w, h, selectable)`: slots are FE objects (list rows or grid images); data is a list of
  `ArrayDatum{hash, desc, enabled, greyed, locked, checked}`. Scrollbar = objects with prefix `scrollbar`
  (`ScrollBar.Update(viewRows, dataRows, topRow, selRow)`), optional `ARRAY_SCROLL_REGION`.
* Slot update (`ArraySlot::Update`, :20): datum null → invisible; greyed/disabled → GREY; selected → HIGHLIGHT
  (once); previously highlighted → UNHIGHLIGHT, else INIT. `ImageArraySlot` also sets the texture = datum.hash. After a
  refresh, the `descLabel` object gets the language hash `datum.desc`.
* Movement (:97 ScrollHor, :148 ScrollVer): left/right wrap inside the current row; up/down move by `w`, scrolling the
  window by one row when leaving it. Sounds only when the selection changed. Mouse click on a slot = BUTTON_PRESSED.

### 0.2 UITrackMapStreamer (quickrace/uiTrackMapStreamer.cpp; world map, Bounty, Milestones)
* Texture: `TRACKS/L2RA/TrackMaps.bin` → `TRACK_MAP` (whole city), or `TRACK_MAP_UNLOCK_1/2` (region-locked), or
  `TRACK_MAP_<race>` when built for a race (`CalcMapTextureHash`, :100). Target = a MultiImage (`TRACK_MAP`, 0x0F365871).
* State: two `tCubic2D` (Hermite, Misc/Point.cpp:10/220): **Zoom** = visible fraction of the texture (1 = whole map) and
  **Pan** = UV centre (0.5, 0.5 = middle). `ZoomTo/PanTo` start a cubic from (value, velocity) to (target, 0) over
  `duration` seconds (default 1.0; the world map sets 0.5); `SetZoom/SetPan` snap. Each tick `UpdateAnimation()` advances
  both by real dt and writes the multi-image UVs (:143): `TL = pan − zoom/2`, `BR = pan + zoom/2` (square, using the larger
  half-size). `GetZoomFactor() = 1/zoom.x`; `GetPan()` returns `pan − 0.5`. `ResetZoom(false)` = SetZoom(1,1).

### 0.3 Rival art streamer (uiRepSheetRivalStreamer.cpp)
Loads from `GLOBAL/RIVALS.BIN` for bin N: rival photo `RIVAL_%02d` (in game `RIVAL_%02d_IG`), graffiti tag `RIVAL_%02d_GRAF`
(`_IG_GRAF`), background `RIVAL_%02d_BG` (`_IG_BG`). Images: photo 0xC1F62308, tag 0xF5A2A087, background 0x2CBE1DD0. All are
hidden until loaded, then the package gets messages 0xC0942E85 (rival), 0x8C9D4547 (graf), 0xD22B95D0 (bg) and the images
are shown with the textures (:112). A port can load synchronously and send the three messages at once.

## 1. World map (WorldMapMain.fng) — uiWorldMap.cpp

**Reached**: free roam only. HUD action HUDACTION_PAD_LEFT (FEPkg_Hud.cpp:827) → `EWorldMapOn` (exe 0x625AD0) → if the
game can pause (0x542670) push `WorldMapMain.fng` (Arg 0) with the player's joystick port. On EXIT_COMPLETE the screen
fires `EWorldMapOff` (0x625A50: pop it if still pushed). The destructor calls `IHud::RefreshMiniMapItems()`.
In the PC exe the screen object is: Cursor +0x124, MapTopLeft +0x144, MapSize +0x14C, SelectedItem +0x15C,
MapStreamer +0x160, CurrentView +0x164, CurrentZoom +0x168, bInToggleMode +0x170, bCursorMoving +0x172,
bLeftHeldOnMap +0x173, fSnapDist +0x174. Functions: ScrollZoom 0x552CB0, PanToCursor 0x530C10, MoveCursor 0x530660,
SnapCursor 0x530A60, UpdateCursor ~0x552F40, mouse 0x552DA0, mouse drag-pan 0x511B80, NotificationMessage ~0x55B0C0.

### 1.1 Objects
| hash | what |
|---|---|
| 0x0F365871 `TRACK_MAP` | MultiImage, map texture via 0.2; `MapTopLeft`/`MapSize` = its rect at Setup (FE units, 640x480 space) |
| 0xF156F6C5 | cursor (scripts SNAP / UNSNAP; package msgs 0x9F710838 "cursor active", 0x7E6687DA "cursor idle") |
| 0xDD9EF5FF `PLAYERCARINDICATOR` | player arrow |
| `MMICON_COPCAR_%d`, `MMICON_ROADBLOCK_%d` | cop / roadblock icon pools (pursuit view) |
| 0xE26BE422 `HELICOPTER_ICON_GROUP`, 0x21390E47 `HELICOPTER_LINE_OF_SIGHT` | helicopter + view cone |
| icon pools from table 1.4 | `MMICON_SPRINT_%d`, `SAFEHOUSEINDICATOR_%d`, … (n = 0,1,2…) |
| 0xD259525F | view title: LOCATIONS 0xBF55E8B2 / EVENT 0xDFD23484 / RACE 0xF74B357D / PURSUIT 0xFEA872D4 |
| 0xCB76CE5B | zoom label: "Zoom All" 0x213587BF, "Zoom 1.5X" 0x0A9BE7D7, "Zoom 2.0X" 0x0A9BE7D8, "Zoom 3.0X" 0x0A9BE7DA (labels, not the real factors) |
| 0x9331FD4F | selected item type name (table 1.4 title) |
| 0xFEEEB39B | distance "%$.1f %s" |
| 0x9A5AB124 | selected item's big icon (table 1.4 tex) |
| 0x32490131 / 0x29456CC8 | GPS button group (NORMAL / GREY) / its text |
| 0x51F0064F | back-button text: "Back" 0x001335F0, or "Done" 0x58B828ED in toggle mode |
| 0xA808E057, 0x95FDFC4E | made invisible in the pursuit view (view-switch hint) [?] |
| 0x5BC, 0x682, 0xFBB0B78E | button-glyph images (`FEngSetButtonTexture`) |
| toggle list: `OPTION_ICON_`, `ICON_VIS_GROUP_` + widget rows | the filter list (UIWidgetMenu, max 10 rows) |

### 1.2 Setup (:844)
1. `region_unlock = CurrentBin >= 13 ? 1 : CurrentBin > 8 ? 2 : 0` → texture TRACK_MAP_UNLOCK_1 / _2 / TRACK_MAP
   (race parameters non-null → race map). Zoom/pan speed 0.5 s; ResetZoom/ResetPan(false).
2. `pCurrentTrack = TrackInfo(track)`; add the player car item.
3. View: 3 (PURSUIT) if the player is in a pursuit, else `GameplaySettings.LastMapView` (0 NAVIGATION, 1 EVENT;
   2 RACE is never entered). Zoom = `LastMapZoom` (views 0/1, default 1) or `LastPursuitMapZoom` (view 3, default 2).
4. Populate (1.4), `PanToPlayer()`, `SetZoom(1/factor)`, first option, RefreshHeader.

### 1.3 Coordinates (:1083 ConvertPos, :830 PanToPlayer)
World (game X, Y; from a Sim position `(x, z)` the decomp uses `X = z, Y = −x`):
```
u = (X − UL.x) / W            v = (UL.y − Y) / W + 1          // 0..1 over the map texture
mapPos = MapTopLeft + (u, v) * MapSize                         // FE units, unzoomed
rot = atan2(dir.y, dir.x) in degrees
```
`UL = TrackInfo.TrackMapCalibrationUpperLeft` (+0xAC), `W = TrackMapCalibrationMapWidthMetres` (+0xB4) of L2RA.
[?] If the TrackInfo chunk is not parsed yet, the HUD minimap fit (REPORT/Minimap.cs: u·1024 = X·0.1538 + 188,
v·1024 = −Y·0.1538 + 779.3) gives W ≈ 6658 m, UL ≈ (−1222.4, −1591.4); this assumes TRACK_MAP covers the same area.
`PanToPlayer`: pan = (u, v) of the player clamped to `[m, 1−m]`, `m = 0.5 / factor`.

### 1.4 Items per view
NAVIGATION (:1053): cursor visible; icons of type CustomShop, Safehouse, CarLot. EVENT (:1060): Sprint, Circuit, Drag,
Knockout, Tollbooth, Speedtrap(race), Rival, SpeedTrap (photo cams), Checkpoint. PURSUIT (:1073): hide 0xA808E057 and
0x95FDFC4E; Safehouse, PursuitBreaker, HidingSpot, cops, roadblocks. The player car is in every view.

`AddIcons(type)` (:1031): `GManager.GatherVisibleIcons` (max 200 GIcons: type + 2D position); for each icon of that type
take the next pool image `FEngHashString(elem, n)` (n = 0,1,…); the item is a plain MapItem (rotation 0) keeping the GIcon.
If ≥ 1 was placed, add a filter toggle (title, item type). Table `kGameplayIconInfo` (exe 0x8F3AB8, 17 × {GIcon type,
item flag, element, world-map title, icon tex}):

| GIcon type | WMIT flag | element | title | tex |
|---|---|---|---|---|
| 1 RaceSprint | 0x100 | MMICON_SPRINT_%d | Sprint 0xB94FD70E | 0x2521E5EB |
| 2 RaceCircuit | 0x80 | MMICON_CIRCUIT_%d | Circuit 0x034FA2C1 | 0xE9638D3E |
| 3 RaceDrag | 0x400 | MMICON_DRAG_%d | Drag 0x6F547E4C | 0xAAAB31E9 |
| 4 RaceKnockout | 0x200 | MMICON_KNOCKOUT_%d | Lap Knockout 0x4930F5FC | 0x3A015595 |
| 5 RaceTollbooth | 0x1000 | MMICON_TOLLBOOTH_%d | Tollbooth 0xA15E4505 | 0x1A091045 |
| 6 RaceSpeedtrap | 0x800 | MMICON_SPEEDTRAP_%d | Speedtrap Race 0xEE7386EA | 0x66C9A7B6 |
| 7 RaceRival | 0x8000 | MINIMAP_ICON_EVENT_RIVAL (single) | Challenge 0x213CC8D1 | 0x9F562D57 |
| 8 GateSafehouse | 0x200000 | SAFEHOUSEINDICATOR_%d | Safe House 0xDED7B298 | 0x0ED39F69 |
| 9 GateCarLot | 0x800000 | CAR_LOT_%d | Car Lot 0xB9D71959 | 0x4EAEE18B |
| 10 GateCustomShop | 0x400000 | SHOP_%d | Shop 0xE0C8090F | 0x0CF07089 |
| 11 HidingSpot | 0x2000000 | HIDING_SPOT_%d | Hiding Spot 0xA31E817A | 0xF172B456 |
| 12 PursuitBreaker | 0x4000000 | PURSUIT_BREAKER_%d | Pursuit Breaker 0xDAC9D619 | 0xD01A771E |
| 13 SpeedTrap | 0x100000 | SPEED_TRAP_CAM_%d | Photo Ticket 0x5BDCF050 | 0x66C9A7B6 |
| 14 SpeedTrapInRace | 0x100000 | SPEED_TRAP_RACE_CAM_%d | Speedtrap Race | 0x66C9A7B6 |
| 15 AreaUnlock | 0x1000000 | MMICON_CHECKPOINT | – | – |
| 0, 16 Checkpoint | 0 | – (never placed) | | |

Cops (`AddCops`, :949): for each AI cop vehicle (the decomp tests `IsActive()` inverted, [?] treat as "active") that
`WasWithinEngagementRadius()`: helicopter → `HeliItem` (group 0xE26BE422 + cone 0x21390E47; toggle "Helicopter"
0xEAD9BD85, flag 0x8); else `CopItem` on `MMICON_COPCAR_%d` (toggle "Cop Cars" 0xEAD6EF6C, flag 0x4). Roadblocks
(:988): each `IRoadBlock` centre/dir → `MMICON_ROADBLOCK_%d` (toggle "Roadblocks" 0x411F1F86, flag 0x20).
Item visibility starts from `GameplaySettings.MapItems` (bit mask of the flags, default all on, :144).

Toggle icons (`ItemTypeToggle::SetIcon`, :117): colour/texture copied from the first pool image of that type, except
player car 0xFFABDA4D / tex 0xADA85247, cop car 0xFFFFFFFF / 0xDAC364E9, roadblock 0xFFFFED00 / 0x123F07E2. Row
`Draw`: on → NORMAL on the icon group and the title (unless highlighted); off → GREY. Icon group placed at the row's
top-left + (−22, +11).

### 1.5 Per-frame update (SCREEN_TICK, :226; only while the package has control)
```
UpdateCursor(false); MapStreamer.UpdateAnimation(); UpdateCursor(true)       // PC: streamer update, then mouse (1.7)
zoom = MapStreamer.GetZoomFactor(); maxZoom = 5.0; pan = MapStreamer.GetPan()   // pan is −0.5..0.5
c = centre of TRACK_MAP
for each item:
    p = c + (item.InitialPos − c) * zoom − (pan * MapSize) * zoom
    item.Centre = p
    item.Size = InitialSize * (((zoom − 1)/(maxZoom − 1)) * 0.5 + 1)          // heli: scale X/Y instead of size
    if ClampToMapBounds(p) → Hide else if !item.hidden → Show
    item.Draw()
```
`ClampToMapBounds` (:609): the box `[TL.x+8, BR.x−8] × [TL.y+26, BR.y−32]`; checks x low, x high, y low, y high in that
order and clamps only the first violated axis (else-if chain); returns true if clamped.
`CopItem.Draw` (:35): counter 1..8 cycling each frame: < 3 → colour 0xFF0000FF, 5–6 → 0xFFA00000, else 0xFFCCCCCC
(red/blue flash; colours are ARGB as stored). `HeliItem.Draw`: cone width = 32·sin(realSeconds) + 32 [bSin takes the
engine angle unit, [?] radians vs 65536-turn].

### 1.6 Cursor (keys / pad)
* Right stick / its keyboard binding: ActionQueue `"WorldMapMain"` (config 0x82D21520), actions FRONTENDACTION_RUP/RDOWN/
  RLEFT/RRIGHT set `velocity = ±data × 14` FE units per frame (:636). Ignored in toggle mode.
* `UpdateCursor(zoomPass)` (:660): while the zoom cubic is moving, the cursor follows the point it was over:
  `pos = c + (CursorMoveFrom − c)·zoom − pan·MapSize·zoom`, clamped (only on the zoom pass is anything else skipped).
  Otherwise on the first pass: velocity ≠ 0 → (once) queue 0x9F710838, `MoveCursor(v)`, and if a selected item is now
  ≥ `fSnapDist` (20) away → UNSNAP, deselect, RefreshHeader. Velocity 0 → (once) queue 0x7E6687DA, `SnapCursor()`.
* `MoveCursor(dx, dy)` (:720): new = centre + d. If zoomed in (CurrentZoom ≠ 0) and new crosses the clamp box edge, pan:
  `excess.x = dx / MapSize.x` (resp. y), `p = clamp(GetPan() + excess, ±(0.5 − 0.5/factor))`,
  `SetPan(((p + GetPan()) * 0.5) + 0.5)` (snap, averaged with the old pan). Then clamp the cursor into the box.
* `SnapCursor()` (:767): nearest non-hidden item with distance < 20 → centre the cursor on it, play SNAP, select it;
  none and something selected → UNSNAP, deselect. Returns true if the selection changed (→ RefreshHeader).
  **PC v1.3 (0x530A60)** measures from the **mouse pointer** (`mouse − (320, 240)`), not from the cursor centre. [?] For
  keyboard play, measure from the cursor; for mouse play, from the pointer.

### 1.7 PC mouse (exe only; not in the decomp)
* Every tick (0x552DA0), unless in toggle mode: if the pointer is inside the clamp box: hide the OS pointer
  (0x91CFB9 = 1), show the cursor, run SnapCursor; if nothing is selected, the cursor centre = pointer; left button down
  → `bLeftHeldOnMap = true`; if an item is selected and it is clicked → queue BUTTON_PRESSED (the GPS dialog, 1.9).
  Outside the box: pointer visible again. Button up → `bLeftHeldOnMap = false`.
* Message 0x9803F6E2 (PC mouse update [?]) when not in toggle mode: wheel delta (0x987F6C) > 0 → `ScrollZoom(−1)`
  (zoom out), < 0 → `ScrollZoom(+1)`; then drag-pan (0x511B80): if `bLeftHeldOnMap` and zoom ≠ 0:
  `d = −mouseDelta · 2·(1/factor)` → `p = clamp(GetPan() + d/MapSize, ±(0.5 − 0.5/factor))`,
  `SetPan(((p + GetPan()) · 0.5) + 0.5)`.

### 1.8 Zoom (:528 ScrollZoom, :560 GetZoomFactor)
Factors: level 0 = 1.0, 1 = 2.0, 2 = 3.3, 3 = 5.0. **PC clamps** (0..3), the GameCube decomp wraps. LTRIGGER → −1,
RTRIGGER → +1 (both ignored in toggle mode). On change: RefreshHeader; `ZoomTo(1/f)` (animated 0.5 s); `PanToCursor(f)`;
save `LastMapZoom` (views 0/1) or `LastPursuitMapZoom` (view 3).
`PanToCursor(f)` (:803): `t = (GetPan() + 0.5) + ((cursor − c)/MapSize) · (1/currentFactor)`;
`CursorMoveFrom = t·MapSize + MapTopLeft`; `PanTo(clamp(t, m, 1 − m))` with `m = 0.5/f`.

### 1.9 Input summary (NotificationMessage, :196)
| input | action |
|---|---|
| PAD_LEFT / PAD_RIGHT | not in toggle mode and view ≠ PURSUIT: UNSNAP, clear items, re-add player, switch NAVIGATION ↔ EVENT, save `LastMapView` |
| PAD_UP / PAD_DOWN | only in toggle mode: move in the filter list (base UIWidgetMenu) |
| BUTTON4 (□ / PC key [?]) | toggle mode on: queue 0x5C28136D (show list), focus the current row; off: queue 0x947E6205, unfocus |
| BUTTON_PRESSED in toggle mode | flip the row: `MapItems` bit, UI sound, show/hide all items of that type |
| BUTTON_PRESSED otherwise | selected item has a GIcon → dialog Yes/No, blurb "Do you want to activate the GPS system for this location?" (0x96AC0A32), Yes = 0xA16CA7BD; nothing selected but a GPS target is active → OK/Cancel "Would you like to de-activate GPS?" (0xA6BE2EBB), OK = 0xA16CA7BD |
| 0xA16CA7BD | disengage GPS if engaged (clear the GPS icon); then if an icon is selected: `GPS_Engage(iconPos, 0)`; failure → one-button dialog "Can't establish GPS connection" (0x7AFDF4CC); success → mark the icon as GPS target (`GIcon::SetGPSing`, static `mGPSingIcon`) and close the map (queue PAD_BACK) |
| PAD_START | last button = 0, then like BACK |
| PAD_BACK | toggle mode → leave it (0x947E6205); else queue 0x587C018B (exit) |
| EXIT_COMPLETE | `new EWorldMapOff` |

### 1.10 RefreshHeader (:1144)
Titles (1.1). Selection: type name (hidden if 0), distance = |item world pos − player (X, Y)| as km (`·0.001`, "KMs"
0x8569A26A) when `SpeedoUnits == 1`, else miles (`·0.000625`, "Miles" 0x867DCFD9); hidden for the player car; big
icon if the type has one. Nothing selected → both strings "". GPS button: toggle mode → NORMAL, text "Hide Icons"
0x2C35EC64 (row on) / "Show Icons" 0xBA0A6A2B, back text "Done"; else selected icon → "Activate GPS" 0x43512519 NORMAL;
GPS active → "Deactivate GPS" 0xF1D0D8A5 NORMAL; else "Activate GPS" GREY; back text "Back".

### 1.11 Port status (2026-10-24, function-by-function diff of uiWorldMap.cpp against WorldMapScreen.cs)
Ported this round (everything else in §1.1-1.10 was already in place):
* `AddCops` (:949): only cops that were within the engagement radius (`CopCar.WithinEngagementRadius`), the chopper as a
  HeliItem (group 0xE26BE422 scaled, cone 0x21390E47 following it, width `sin(real s)·32 + 32` — bSin taken as radians
  [?]), rows "Helicopter" 0xEAD9BD85 (flag 0x8) and "Cop Cars" 0xEAD6EF6C (0x4). `AddRoadBlocks` (:988):
  MMICON_ROADBLOCK_n (5 in the package) at the block centre turned to its direction, row 0x411F1F86 (flag 0x20);
  [ours] the blocks are found through the cop cars standing in them (the pursuit system's list is private).
* `SetupNavigation` / `SetupEvent` / `SetupPursuit` now add in the exe's type order (shop, safehouse, car lot; the six
  race types, rival, speed-trap cameras; safehouse, pursuit breakers, hiding spots, cops, roadblocks), so the toggle rows
  come out in the exe's order. Speed-trap cameras (kType_SpeedTrap, SPEED_TRAP_CAM_n, "Photo Ticket") and the pursuit
  view's breakers / hiding spots are gathered from the gameplay triggers that carry a GIcon (`LuaFlow.Triggers`).
  [?] the port never sets the on-map flag on speed-trap triggers, so they count as shown while events may be engaged;
  pursuit breakers and hiding spots now come from GManager's own icon pool (`set.Icons`: Triggers.cs GameIcons — IconMarker
  track markers and type-8 track path zones; gameplay agent, round 9); there is no CHECKPOINT pool in WorldMapMain, so kType_Checkpoint adds nothing (as in the exe).
* `ClearItems` ResetSize (the pool keeps its authored size for the next view), `MoveCursor` pans by the movement on the
  first edge crossed only (x past a side, else y), `CopItem::Draw` red is 0xFFA00000 (160, 0, 0), PAD_START leaves like
  BACK, the cursor starts where the package puts it (:854; the pan centres the player, so at zoom > 1 it sits on the car).
* `ItemTypeToggle::SetIcon` (:117): cop rows white / 0xDAC364E9, roadblock rows 0xFFFFED00 / 0x123F07E2 (the player car
  row 0xFFABDA4D / 0xADA85247 never appears: the player item has no toggle); `Act` plays UISND_COMMON_LEFT.
* Verified against the package (harness `feig tree WorldMapMain`): ICON_VIS_GROUP_1 = C9784C95, OPTION_ICON_1 =
  A713B090, 7 OPTION_NAME rows, pools 10 cop / 5 roadblock / 34 pursuit breaker / 32 hiding spot / 6 speed-trap cam.
Still [?]: the player car's kGameplayIconInfo row (title shown as the profile name); the right-stick speed unit (14 FE
units per FE frame, taken as 30 Hz); `~WorldMap`'s IHud::RefreshMiniMapItems (the port's minimap does not filter by
MapItems); the exe freezes the cops' map positions while the map is up, the port follows the cars.

## 2. SMS (SMS_Mailboxes.fng, SMS_Message.fng) — uiSMS.cpp, uiSMSMessage.cpp

Data (FEDatabase.hpp:264–330, FEDatabase.cpp:258–310): `CareerSettings.SMSMessages[150]` (index = SMS id; ids whose
`SMS_MESSAGE_<id>` label is missing get handle 0xFF = invalid), each `{handle u8, flags u8, sortOrder u16}`;
flags (non-A124 build): **UNREAD = 2, READ = 4**, 0 = not received / deleted. `GetSMSSortOrder()` = ++counter (stamp on
arrival). **Voice vs text**: `IsVoice()` is false for ids 110, 111, 120–130, 132–137 (text messages) and true for all
others. Labels: body `SMS_MESSAGE_%d`, `_FROM`, `_SUBJECT`, `_VOICE`.

### 2.1 Reaching it
* HUD HUDACTION_PAD_RIGHT in free roam → `EShowSMS(-1)` (FEPkg_Hud.cpp:841; exe handler 0x62B240): push
  `SMS_Mailboxes.fng` Arg 0 unless already pushed.
* `EShowSMS(id ≥ 0)` (incoming message opened) → push with Arg = that SMSMessage → auto-open it.

### 2.2 Inbox (uiSMS, ArrayScroller 1×6, click-to-select)
Slots i = 1..6: group `MESSAGE_GROUP_%d`, new icon `NEW_MESSAGE_ICON_%d`, text `MESSAGE_TEXT_%d` (= subject label;
icon visible when the datum is "checked" = unread).
* `Setup()` (:82): collect every valid message with READ or UNREAD; sort ascending by sortOrder (oldest first [?: the
  comparator flips when before > after]); keep only voice messages (voice tab) or only text (text tab).
  New indicators: voice 0x19161CCC, text 0x0D6FD6F9: the open tab's indicator plays HIGHLIGHT if it has unread, else
  HIDE; the other tab's plays SHOW if unread, else HIDE. Selection = the remembered message per tab (`last_msg[2]`).
* `RefreshHeader` (:186): voice tab → 0x4A2EEBC8 POS1, 0x8A6AD1C1 HIGHLIGHT, 0x8F2FAD70 UNHIGHLIGHT; text tab → POS2 and
  the reverse. No messages → 0x07890734 HIDE.
* Ctor (:38): starts on the voice tab; Arg null → 0x2CF801C2 APPEAR; Arg set → auto-play mode (open after
  INIT_COMPLETE, unless the memory-card screen is pushed). If autosaving: queue `HIDE_INCOMING` (0x3F9628CC) to itself and
  `SMS_MAILBOX` (0x7847143D) globally.
* Input (:207): UP/DOWN scroll and remember; LEFT/RIGHT switch tab (`Setup()` again; no move sound); BUTTON_PRESSED on a
  message → play READ on 0x47FF4E7C, then on EXIT_COMPLETE push `SMS_Message.fng` (Arg = message); BUTTON5 → dialog
  Yes/No "Are you sure you want to delete this SMS message?" (0x8C3C2171) → yes: flags = 0, `Setup()`; any dialog close
  queues 0x8CB81F09; message 0x775CE5DF (back from SMS_Message) → `Setup()` (reset the remembered ids if the message
  was deleted); PAD_BACK → pop.

### 2.3 Message (uiSMSMessage)
* Ctor: game audio ducked (`ESndGameState(0xD, true)`, SMS sound state), cop speech released.
* Header: 0xFECED617 = FROM label, 0x2C167533 = SUBJECT label. Body: text scroller over `MESSAGE_TEXT_1..10`, width =
  MESSAGE_TEXT_1's MaxWidth and font, `^` = new line, with `scrollbar` objects; up/down scroll by line.
* Text messages hide 0x2A631207 and 0x914614E5 (the replay button/icon) and get no 0x610FB237 sound.
* INIT_COMPLETE: voice + unread → play the voiced call (`MiscSpeech::SMSCellCall(id)`); then flags = READ.
* BUTTON4 → replay the voice (voice only). BUTTON5 → delete dialog → yes: flags = 0, queue PAD_BACK.
  EXIT_COMPLETE → stop speech, pop. (Back/accept are the package's own exit scripts.)

## 3. Blacklist (rep sheet) and the post-rival flow

Shared global `iCurrentViewBin` (uiRepSheetMain.cpp:19): the bin shown. Set to the current bin by the career menu
"Blacklist" option (`CTop15`, uiCareerMain.hpp:63; the decomp names "WorldMap_Main.fng", which does not exist in the exe,
[?] the real target is SafeHouseReputationOverview.fng), by `ERaceSheetOn` [?: exe 0x6243C8 copies a career byte] and by
the post-rival bio.

### 3.1 Overview (SafeHouseReputationOverview.fng / InGameReputationOverview.fng) — uiRepSheetMain.cpp
**Reached**: safehouse career menu "Blacklist"; in game `ERaceSheetOn(0)` (HUD PAD_DOWN in free roam unless D-Day,
FEPkg_Hud.cpp:833) → exe 0x6243B0 pushes `InGameReputationOverview.fng` Arg 1 (arg > 0 pushes
`InGameRivalChallenge.fng` with that arg: 2 = after a photo finish, 3 = boss-race zone engage). In game: idle colour
0xFFFFAE40, `EFadeScreenOff`. Safehouse: shows the player's car in the garage viewer.
* IconScroller options (tex, label, id): (0xEFC9662E, "Race Events" 0x84E4A54C, 0), (0xD807E9B3, "Milestone Events"
  0x216F1B81, 1), (0x021A4B0C, "Bounty" 0xE451941E, 2), (0xE97E4E83, "Bio" 0x2D159737, 4). TITLE_GROUP = "Blacklist"
  (0x2E3919E9 in game / 0xCACE5999).
* Bin 15 hides 0x47B22FCA and 0x72AD598C (the up/down rival arrows).
* `UpdateInfo` (:201) for `bin = iCurrentViewBin`: 0x15D80973 races won (`GRaceBin.AwardedRaceWins`), 0xDE7AD199
  required wins, 0xD802FBA8 challenges done, 0x7242962E required, 0x322B18F9 `%$d` total bounty (all cars),
  0x055C6E5F required bounty; ticks 0x4C3B1536/37/38 play 0xE6361F46 when met, else HIDE. 0x242657CE = "Blacklist %d"
  (0x96CA2471; FE 0x3A64DE21). 0x7AC3D0C9 = `BLACKLIST_RIVAL_%02d_AKA`, 0x79CF0442 = `BLACKLIST_RIVAL_%02d_CHALLENGE`.
  Rival photo/tag via 0.3.
* Boss state: `available` = any boss race of the bin is available; `beaten` = career beaten or `bin > CurrentBin`.
  Beaten → hide 0x55F6AA1A, show 0x34D4433B and queue 0xB4C144B1 (the "DEFEATED" stamp 0x7FE4020F, texture English
  0x087B7D0A, other languages in :167). Else available → queue `RIVAL_CHALLENGE_DEACTIVATED`, show 0x55F6AA1A; else queue
  `RIVAL_CHALLENGE_ACTIVATED`, hide 0x55F6AA1A (names as in the decomp; [?] they read inverted).
* Input: DOWN → `ScrollRival(next)`: bin − 1, wrap to 15 below CurrentBin; UP → bin + 1, wrap to CurrentBin above 15;
  nothing when CurrentBin == 15; on change play 0xAF9D73F2 (down) / 0x9E5E6B5F (up) on 0xC1F62308, reload art, UpdateInfo.
  BUTTON4 → if available and not beaten: switch to SafeHouseRivalChallenge.fng / InGameRivalChallenge.fng (Arg 1).
  BUTTON_PRESSED → on EXIT_COMPLETE switch to RaceSheet (id 0), Milestones (1), Bounty (2), RivalBio (4), each
  SafeHouse*.fng Arg 0 or InGame*.fng Arg 1. BACK → in game `ERaceSheetOff`; safehouse `MainMenu_Sub.fng`.

### 3.2 Rival bio (SafeHouseRivalBio.fng / InGameRivalBio.fng) — uiRepSheetRivalBio.cpp
* Text (:93): 0x242657CE "Blacklist %d"; language labels 0x7AC3D0C9 `BLACKLIST_RIVAL_%02d_NAME`, 0xB1F2748D `_AKA`,
  0x27E1D6D8 `_BIO`, 0xCB5BF41A `_CAR`, 0xA6F07BF3 `_STRENGTH`. Photo, tag and background via 0.3.
* Normal mode: queue 0xAF922178; safehouse hides the garage car. BUTTON0 → build the preset car `BL%d`
  (bin 1: `E3_DEMO_BMW`) and switch to `Showcase.fng` (Arg = car; return package = this, BlackListNumber = bin).
  BUTTON4 → play FMV `blacklist_%02d` (FEAnyMovie). BACK → back to the overview (in game Arg 1; safehouse re-enables car
  rendering).
* Post-rival mode (`eFE_GAME_MODE_POST_RIVAL`): hide cars; if CurrentBin == 16 → `EEnterBin(15)`; iCurrentViewBin =
  CurrentBin; queue 0x0B21A45F. ACCEPT → `RivalFlow.StartFlow(5)` if idle, else `Next()`.

### 3.3 Post-rival flow (uiRepSheetRivalFlow.cpp:42) — replaces career_flow.md §2.1's approximate order
Stages, `Next()` = stage + 1 then:
| stage | screen / action |
|---|---|
| 0 | `SafeHouseRivalChallenge.fng` (result) |
| 1 | `SafeHouseMarkers.fng` (3.6) |
| 2 | `SafeHouseRegionUnlock.fng` only if the beaten bin (CurrentBin + 1) is 13 or 9, else skip |
| 3 | autosave (`MemcardEnter`, then continues to stage 4's screen) if autosave is on, else skip |
| 4 | `SafeHouseRivalBio.fng` (the new rival) |
| 5 | FMV `blacklist_%02d` of CurrentBin (FEAnyMovie / WS_FEAnyMovie) |
| 6 | CurrentBin == 15 → leave post-rival mode, career mode, SetHasDoneCareerIntro, pop all, push SafeHouseReputationOverview; else if no rap sheet and CurrentBin == 13 → SetHasRapSheet, FMV `storyfmv_rap30`, stay at 6; else `StartCareerFreeRoam` |
| 7 | (in-game blacklist) `MFlowReadyForOutro`, `ERaceSheetOn(0)`, leave post-rival mode |

### 3.4 Bounty (SafeHouseBounty.fng / InGameBounty.fng) — uiRepSheetBounty.cpp
Pursuit start points. ArrayScroller 3×3 over images `EVENT_ICON_1..9`; map `TRACK_MAP` via 0.2 (full map, 1 s cubic).
* Data (:171): the GManager bounty spawn markers (gameplay markers, ordered by their tag 1..n via `orderedList`; [?] the
  marker records are not decompiled: `GManager::FindBountySpawnPoints`). Entry i (0-based) exists if
  `i < 4 || (i < 8 && CurrentBin < 13) || CurrentBin < 9`. Datum: icon `GetBountyIconHash(i+1)` (1–4 0x8A21B882, 5–8
  0x895EC0AE, 9+ 0x9129E7FB), header `BLACKLIST_BOUNTY_SPAWN_POINT_%02d` → shown in desc label 0xB5117FDE.
* Header (:213): 0x5A856A34 = index+1, 0x2D4D22C8 = count, bounty/cash lines, 0xF97EC5D5 = icon of the current tag,
  0x028049D6 = `BLACKLIST_BOUNTY_SPAWN_POINT_%02d_DESC`; locked datum → queue 0xC5DD9D68, else 0x38091FA1.
  `CHECK_1..9` get texture 0x0018ED48 and are hidden where a datum exists. Title 0xBDE82FCC = "BOUNTY" 0x6DDFA694 in game,
  else "Bounty".
* Map (:194): on selection change `ResetZoom(false)`, `PanTo(markerMapCoords)`, `ZoomTo(0.27, 0.27)`; arrow 0xAF51DD73
  rotation = marker heading (`GManager::CalcMapCoordsForMarker`, [?] = 1.3's (u, v)). Tick: `UpdateAnimation()`.
* Input: BUTTON_PRESSED on an unlocked entry → Yes/No "Are you sure you want to jump to this location and engage in a
  pursuit?" (0xCD195D0B). Yes: if the bounty tutorial was never seen → play `bounty_tutorial` (in game
  InGameAnyTutorial, hide `InGameBackground.fng` 0x2716CDBF; FE FEAnyTutorial), play HIDE on 0x99344537, hide the map,
  set flag 0x400; otherwise post 0xC3960EB9. 0xC3960EB9 (also sent when the tutorial ends): after a BUTTON4 viewing →
  INIT on 0x99344537 only; else in game: show the background, `WarpToMarker(marker, pursuit = true)`, `ERaceSheetOff`;
  safehouse: `OverrideFreeRoamStartMarker(marker)`, `QueueFreeRoamPursuit(0)`, `StartCareerFreeRoam()`.
  BUTTON4 (safehouse only) → watch `bounty_tutorial`. BACK → the overview. Locked entries use the "wrong" sound.

### 3.5 Milestones (SafeHouseMilestones.fng / InGameMilestones.fng) — uiRepSheetMilestones.cpp
Same layout as 3.4 (3×3 `EVENT_ICON_n`, TRACK_MAP, arrow 0xAF51DD73, 0x5A856A34/0x2D4D22C8, desc label 0xB5117FDE).
Title 0xBDE82FCC = "MILESTONES" 0x578B767B in game, else "Milestone Events".
* Data (:191) for `iCurrentViewBin`: every `GMilestone` (icon `GetMilestoneIconHash(typeKey)` FEDatabase.cpp:1154,
  header `BLACKLIST_PURSUIT_MILESTONES_%02d_SHORT` of its localization tag; locked/awarded flags), then every
  `GSpeedTrap` (icon 0x66C9A7B6, header "Photo Ticket" 0xF3B3D8DC; completed flag).
* Header (:260): 0xF97EC5D5 icon, 0xB21D69BD bounty `%$0.0f`, 0x028049D6 = description
  `BLACKLIST_PURSUIT_MILESTONES_%02d` + " " + goal (`%$0.0f`; time types 0x033FA23A/0x5392E4FD divide by 60), or for a
  speed trap "Get clocked at over %s to successfully complete this challenge." (0xB14018BD) with the trigger speed
  `%$0.0f` + unit ("KM/H" 0x8569A25F when metric, "MPH" 0x8569AB44; [?] the decomp pairs them inverted).
  `MEDAL_THUMB_1..9`: locked → texture 0x0018ED48, done → 0x028FEADD, else hidden.
* Input: BUTTON_PRESSED on a not-done entry → Yes/No "…engage in a pursuit milestone?" (0xA5A8409A) or "…attempt this
  photo ticket milestone?" (0xBF1DCD38); done → wrong sound. Yes → `pursuit_tutorial` once (flag 0x200), same pattern as
  3.4; then jump to the record's `JumpMarkerKey`: in game `ERaceSheetOff` + `WarpToMarker(marker, pursuit = milestone)`;
  safehouse `OverrideFreeRoamStartMarker` (+ `QueueFreeRoamPursuit(0)` for milestones) + `StartCareerFreeRoam`.
  BUTTON4 (safehouse) → watch `pursuit_tutorial`. BACK → overview.

### 3.6 Marker selection (SafehouseMarkers.fng) — uiMarkerSelect.cpp ("Junkman"/rival markers)
**Reached**: post-rival stage 1. Source: the 6 "for later selection" markers queued when the boss reward was given
(`FEMarkerManager::AwardMarker(inst, false)`, Careers/UnlockSystem.cpp:1054; reward folder per career_flow.md §2).
* Ctor (:13): order the up-to-6 markers by category: misc 0xBDAA5794 (jail-free, pink slip, cash, impound box, impound
  release), visual 0xE69D4F7C (vinyl, decal, HUD), body parts 0x73272ED2 (body, hood, spoiler, rims, roof scoop),
  performance 0xC61C8D3A (brakes, engine, NOS, induction, chassis, tires, transmission). Then shuffle: for i = 0..2 swap
  card i with card `rand(3)`. Rival art of the **beaten** bin (CurrentBin + 1). 0xBDB541B3 and 0x7603F3D5 = "Choose
  Markers" 0x9A375734. Queue 0x9F04347D / 0x5B032D25 / 0x96B11F47 if Parts / Performance / Visual has a new unlock, and
  0x7F8AAF09 if a car unlocks at the current bin (`frontend.UnlockedAt == CurrentBin`).
* Cards `BUTTON_1..6`: texture = category icon (face down) or the marker icon once picked (table `MarkerSelectInfos`,
  :187: e.g. engine 0x4F424E0F, pink slip 0x67E2461D, cash 0x28D128D2, get-out-of-jail 0x28D4FE33).
  0x4960F369 header / 0xEB0A8ABD text = marker name/blurb when picked (cash: blurb printf with the amount), else category
  name/blurb. 0x38DEAC6B = "Selections Remaining: %d" (0x5BB3A130) with 2 − picked; 0xD6C0E097 = "You can now select
  %d markers from %s." (0xAE5BC899) with the rival's AKA.
* Input: highlight (0xABC08912) → FLIPHIGHLIGHT 0x6B718FA1 if picked else HIGHLIGHT, redraw; unhighlight (0x55D1E635)
  → 0xC5DECC84 / UNHIGHLIGHT. BUTTON_PRESSED with < 2 picked on an unpicked card → FLIP, pick: pink slip →
  `AwardRivalCar(param = FEngHashString("BL%d"))`, cash → add param to cash, else add to the marker inventory; at 2
  picked both prompts become "Continue" 0x8098A54C. With 2 picked any press → queue 0x587C018B. EXIT_COMPLETE → clear the
  temp markers, `RivalFlow.Next()`. INIT_COMPLETE → current button BUTTON_1.

### 3.7 Region unlock (SafehouseRegionUnlock.fng) — uiSafehouseRegionUnlock.cpp
Rival art of the beaten bin; 0xD6C0E097 = "Camden Beach borough has now been unlocked." (0x29E4B193) when the beaten
bin is 13, "Rockport borough has now been unlocked." (0x2B0BCA2D) when it is 9. ACCEPT → `RivalFlow.Next()`.
(The world map's region textures follow the same bins, 1.2.)

### 3.8 Career menu (MainMenu_Sub.fng in career, uiCareerCrib) — for reference
Options: Resume free roam (0x12BB5EA2 / 0x01BD185C), Blacklist (0x2C14AC23 / "Blacklist 15" 0x80B9FF9B → 3.1), Car select
(0xC6A1A6E0 → `IG_CarLot.fng` [?]), Rap Sheet only if `HasRapSheet` (0x2FD8B206 / 0xAC22F27E → game mode RAP_SHEET,
`RapSheetMain.fng`), Save. Texts: `CAREER_DATA` "%d%%" completion, `BOUNTY_DATA`, `CASH_DATA`; `PLAYER_MASTER` SHOW.

## 4. Rap Sheet and Infractions

All rap-sheet pages print 0x1232703A = "CASE FILE: %s" (0xE21D083C) with `CareerSettings.CaseFileName`
(`"MW-%d-%.4sXXX"`, number 1069..7647, first 4 letters of the profile name, upper-cased, max 12 chars;
FEDatabase.cpp:433). Data = `HighScoresDatabase` (Database/RaceDB.cpp) and the player car stable (VehicleDB.cpp).
Every page's EXIT_COMPLETE switches back to `RapSheetMain.fng` (detail pages to their list). Standard labels:
NAME 0x6031106E, BOUNTY 0x364E4525, COST TO STATE 0xA355FEDD, CARS IMPOUNDED 0xB1E58DB1, FINES DUE 0x79FB7D16,
PURSUITS EVADED 0x463B461B, BUSTED 0xC5094459, CARS MONITORED 0x6DEE0C7A (all printf formats like "BOUNTY: %$d").

| package | screen | content |
|---|---|---|
| RapSheetLogin.fng / RapSheetLogin2.fng | uiRapSheetLogin | login animation. Login2 (Arg 2) prints 0x03CC94D6 = "> <profile name>". ACCEPT or 0x7EABCA56 → stop the login sound, EXIT → Login2 (from Login) or RapSheetMain (from Login2). BACK → `MainMenu_Sub.fng`, clear RAP_SHEET mode. [?] who opens Login: the career menu goes straight to RapSheetMain in the decomp |
| RapSheetMain.fng | uiRapSheetMain | 0xE3DA78E7.. EE = NAME, BOUNTY (total), COST TO STATE (career CTS score), CARS IMPOUNDED, FINES DUE (unserved), PURSUITS EVADED, BUSTED, CARS MONITORED (cars with a record). Buttons BUTTON_1..6 → RS, US, CTS, TEP, Rankings, VD; anything else (back) → MainMenu_Sub + clear mode. Remembers the last button (initial focus: decomp uses `BL_%d`, [?] BUTTON_%d) |
| RapSheetRS.fng | uiRapSheetRS (record summary) | 0xB259EEA7..AA = name, bounty, CTS, impounded; 0x6EB5AC50/51 = unserved (0x091CB790) / served (0x1903C44D) infractions; 0xD919049F/A0 evaded (0x3598476F) / busted (0x2E90D7ED); 0xD01E18C5/C6 fines unserved (0x82A67697) / paid (0xD77B89B7); 0xD7E5D0CC/CD cops damaged (0x50EC3763) / destroyed (0xE8DB4BF3). 0x90211462 = `RAPSHEET_WARNING_%d`, level = first i with `served+unserved ≤ WarningLevel[i]` of frontend VLT record `rap_sheet_summary` (min 1, default = count) |
| RapSheetUS.fng | uiRapSheetUS (infractions), list 1×8 | rows `INFRACTION_TYPE_%d` (label), `UNSERVED_QTY_%d`, `TOTAL_QTY_%d` for Speeding 0x676B575C, Racing 0x81705AC5, Reckless 0x0E9D1CB6, Assault 0x1536B1FA, Hit and run 0xAAF89AB3, Damage 0x706C0F0D, Resisting 0xAD524B30, Off-road 0xF9748B0B. BUTTON5 toggles unserved/served (0x9D974DF3 = "UNSERVED" 0xC225D554 / "SERVED" 0x6A1151D1); header 0x1FFFB988..8B, 0x2ECAFA80, 0xBBE88932 (counts and fines for the view) |
| RapSheetCTS.fng | uiRapSheetCTS (cost to state), list 1×9 | `TXT_QUANTITY_%d` `%$d`, `TXT_CATEGORY_%d` label, `TXT_CASH_%d` `%$d`; rows and value per unit (RaceDB.cpp:262): property damage (summed value) 0x3682A8CF, traffic hit ×500 0x6DE4810A, cop cars deployed ×250 0x89A9C941, support vehicles ×450 0x443B615F, cops damaged ×250 0xD3AA88DA, cops destroyed ×5000 0xBDB16FEA, roadblocks ×500 0xD320C6C3, spike strips ×250 0xA83862AF, helicopters ×2000 0x80E9CCB2. Header 0x358672CC = career CTS |
| RapSheetTEP.fng | uiRapSheetTEP (top evaded pursuits) | 5 rows: `CAR_USED_%d` "CAR USED: %s" (0x69EAB50F), `BOUNTY_%d` (0x060C058A), `PURSUIT_ID_%d` "ID: %s" (0x41474FB1), `PURSUIT_LENGTH_%d` "LENGTH: %s" (0x36175146, time string); empty rows "< CLEAN RECORD >" (0xE3274304) with BUTTON_n disabled. None → hide 0xEB5E7757, 0x73DCB662. UP on BUTTON_1 wraps to the last, DOWN on the last to BUTTON_1. BUTTON_n → `RapSheetPD.fng` Arg n−1 |
| RapSheetPD.fng | uiRapSheetPD (pursuit detail) | 0xD91934E7 "PURSUIT ID: %s" (0xA656CD29); 0x9068C46D..75 = length (time), cops, damaged, destroyed, roadblocks dodged, spikes dodged, CTS, infractions, helicopters (`%$d`); 0x9D81523D bounty. Back → TEP. Pursuit ID = "MW-" + alternating digit/letter (RaceDB.cpp:316) |
| RapSheetRankings.fng | uiRapSheetRankings | ranks `0x7711109B..A3` + 0x5933242B for pursuit length, cops involved, damaged, destroyed, roadblocks, spike strips, CTS, infractions, helicopters, bounty (buttons BUTTON_1..9, BUTTON_10). Rank = first i (1..15) with player value ≥ `RapSheetRanks[i]` of frontend VLT record (`pursuit_length`, `cops_involved`, … career view; `…_in_pursuit` single-pursuit view), else 16 → "UNRANKED" 0xF3799455 (RaceDB.cpp:152). BUTTON5 toggles career/single; 0x001E4FDA = "LIFETIME PURSUIT" 0x96DDF504 / "SINGLE PURSUIT" 0x56E940F4, 0x0DD2F4FB and 0x9AE9B5CD = "Single Pursuit" 0x554BBDB5 / "Lifetime" 0xA88B3FC5 (the other view's name). Button → RankingsDetail Arg = type |
| RapSheetRankingsDetail.fng | uiRapSheetRankingsDetail, list 1×10 | `RANK_%d`, `PLAYER_NAME_%d`, `VEHICLE_%d`, `TIME_%d`: 15 rival rows (`BLACKLIST_RIVAL_%.2d_AKA` of `NameId[i]`, car `_CAR` in single view, value `RapSheetRanks[i]`), the player inserted at his rank (name = profile, car of his best pursuit), 16 rows if unranked ("--" rank). Lengths as time strings. Scrolls so the player is visible; message `POS%d` (player row on screen) or 0x58B123F7 (off screen). 0x8224E17C = category ("> PURSUIT LENGTH" 0xD70811D1 …). BUTTON5 toggles views |
| RapSheetVD.fng | uiRapSheetVD (vehicle details), list 1×2 | per career car with a record (filter 0xF0002): `CAR_%d` name, `BOUNTY_%d`, `FINES_%d`, `UNSERVED_%d`, `TO_DRIVE_%d` = "< VEHICLE IMPOUNDED >" / "< CONFIRMED TO DRIVE >" (busted > 0) / "< SUSPECTED TO DRIVE >", `EVADED_%d`, `BUSTED_%d`; empty rows "< no record >". Header 0x1FFFB98A "PREFERRED VEHICLE: %s" (0xFBAF89DF), 0x1FFFB98B "RECENT VEHICLE: %s" (0x074A86E1) |

### 4.1 Busted → Infractions.fng (uiInfractions.cpp, PostPursuitInfractionsScreen)
**Reached**: after the arrest animation, `EQuitToFE(CAREER_SAFEHOUSE, "Infractions.fng")` (Animation/AnimChooseArrest.cpp:252).
* Ctor (:13): first bust ever → give 4 "Get Out of Jail Free" markers if the player has none, remember to show the help
  dialog. Stamp 0x2347122A ("BUSTED", English 0xB419EC5F) shown when loaded. Texts: 0xBD66334A this pursuit's fine,
  0xB967F64D its infraction count, 0xA4C79522 previously unserved fines (car total − this), 0x5344F2A6 previous unserved
  count, 0x5B875870 and 0xEA8AECD9 marker count, 0x854AF1F4 total to pay (all unserved fines of the current car),
  0x1930B057 cash. No marker → GREY on 0xF9363F30, 0x6B6973C1, 0x39F11E5C and disable button 0xB8A7C6CC; else
  NORMAL on 0x39F11E5C.
* INIT_COMPLETE: first bust → focus 0xB8A7C6CC and a one-button dialog (0x9C14B5F1 "When you get busted, you'll need to
  settle all your infractions…"); else focus 0xB8A7C6CD.
* BUTTON_PRESSED: **0xB8A7C6CD = pay cash**: spend the amount (cash floors at 0), `impound.NotifyBusted()` (returns
  "strike limit reached"), serve all infractions, car heat = 1. **0xB8A7C6CC = use a marker**: consume one, update the
  counts (grey at 0), waive this pursuit's infractions. Then: strike limit → impound + dialog 0x78F0E298; paid cash
  without enough money → impound + 0x1ECFFA6E; both dialogs answer 0x34DC1BEC → `Car_Select.fng` Arg 0x200. Otherwise
  `Car_Select.fng` Arg 0 (marker) / 0x100 (cash).

## 5. HUD items not yet in GameHud.cs

Behaviour is in `hud.md` §4.8, 4.13, 4.16–4.19; decomp files add the following (only differences or missing bits).
GameHud currently hides the radar (0x062743F5) and has no tollbooth messages, CTS, milestones, infractions, or
leaderboard paging.
* **Hud_TimeExtension** (HUD/FeTimeExtension.cpp, mask 0x2000000, tollbooth race type 4 only). Bonus
  (`RequestTimeExtensionMessage(t)`, which first dismisses a showing message with 0xE1C034FC): when the generic message
  is free → "TIME BONUS!" (0x1C074E14) `"%s\n+%s"` with time string(remaining − t) [?], script 0x8AB83EDB, icon
  `TIMER_ICON` tex 0x609F6B15, priority 3. 2 s later starts a 2 s window repeating "NEXT TOLLBOOTH" (0x171471B4)
  `"%s\n%s"` remaining. If the current checkpoint time left ≤ 10 s: "HURRY" (0x862A0519) `"%s!\n%s"`, script 0x821E6378
  (5–10 s) then 0x4F79CBA2 (≤ 5 s), priority 5, keep-if-playing. Time strings with flags 4 (`hud.md` §4.20).
* **Hud_CostToState** (FeCostToState.cpp, mask 0x1000): `CTS_GROUP` HIDE at start; `SetCostToState(v)` only in a pursuit:
  v > shown → shown = v, show for 120 frames; v == 0 → reset. While showing: title 0x64247241 = "PURSUIT CTS:"
  (0x3DD874C5), value 0x3FF5F33C `%$d`, APPEAR once; afterwards LEAVE.
* **Hud_MilestoneBoard** (FeMilestoneBoard.cpp, mask 0x400000000, up to 4 milestones; in a pursuit only). As hud.md
  §4.17, but the cycle interval is **5 s** in the decomp (hud.md says 3 s [?]): when > 1 incomplete, after 5 s play
  0xAFF37F61 on details group 0xF4405EC0, when it ends play 0xD6C950A0 and advance to the next incomplete one. Icons
  alpha 0.5 (incomplete) / 1.0 (complete). Backing of the shown one: HIGHLIGHT (unless PULSE is running). A value change
  on an incomplete, non-time milestone selects it and plays PULSE on its backing and on 0x5C697702, 0xD6C950A0 on the
  details. Goal text 0xC3E48FBF = `SetMilestoneDescriptionString(type, cur, goal, true)` (FEDatabase.cpp:1218: time types
  0x033FA23A / 0x5392E4FD print "cur/goal" time strings).
* **Hud_Infractions** (FeInfractions.cpp, mask 0x200000000): as hud.md §4.18; GENERIC_ICONS leaves when all four rows are
  hidden and it is in APPEAR or PULSE.
* **Hud_RadarDetector** (FeRadarDetector.cpp, mask 0x200000): objects group 0x062743F5, LEDs 0x69AA01E7 / 0x9F59065A
  (MultiImages), `Radar_DirectionArrow`, `Radar_Icon`, backings 0x839E7D77 / 0x9EE06631. Visible only out of pursuit or in
  cooldown (in pursuit: group and backing → LEAVE/HIDE). `SetTarget(type 0 none / 1 cop / 2 camera, range, bearing rad)`:
  LED fill +0.1 per frame to 1; every `1.5·max(range, TWK_RadarDetectorMinThreshold)` s reset to 0.3 and beep (sound 0);
  camera → script SPEEDTRAP on arrow, icon and LEDs, else INIT. No target → LEDs UV (0, 1), arrow HIDE.
  [?] The caller of SetTarget (cop / camera proximity, range units) is not decompiled.
* **Hud_LeaderBoard**: implement hud.md §4.8 paging (90 frames, page B = gap to the player in m/ft or K.O./BUSTED).

## 6. Event wiring for the port (summary)

| event | handler |
|---|---|
| HUD PAD_LEFT (free roam) | `EWorldMapOn` → push WorldMapMain (pauses the sim [?]) ; EXIT → `EWorldMapOff` pop |
| HUD PAD_DOWN (free roam, not D-Day) | `ERaceSheetOn(0)` → push InGameReputationOverview (Arg 1) |
| HUD PAD_RIGHT (free roam) | `EShowSMS(-1)` → push SMS_Mailboxes (Arg 0) |
| boss-race zone engage | `ERaceSheetOn(3)` → push InGameRivalChallenge (Arg 3) |
| photo finish with races left | `ERaceSheetOn(2)` → InGameRivalChallenge (Arg 2, mid rival flow) |
| `ERaceSheetOff` | pop the in-game sheet stack, unpause [?] |
| busted (career) | quit to safehouse with Infractions.fng first |
| boss beaten | post-rival mode, flow 3.3 from stage 0 |
PC default keys for the three HUD pad actions are in the controller VLT data [?]; WorldMapMain shows PC_key_2 / PC_key_4 /
PC_key_enter / PC_key_esc glyphs.

## 7. Open items
* PC SnapCursor uses the mouse pointer (1.6); exact PC message 0x9803F6E2 meaning (mouse move/wheel) inferred.
* TrackInfo calibration values for L2RA not read (1.3 fallback is derived from the minimap fit).
* SMS inbox order (ascending sortOrder) and how EAddSMS stamps sortOrder (exe 0x57F4D0, see career_flow.md §3).
* Bounty spawn markers / `CalcMapCoordsForMarker`, radar `SetTarget` caller, and `ERaceSheetOff`'s handler are not in the
  decomp. The CTop15 target package name in the decomp is wrong.


## Round 9 (2026-10-25) — rap sheet, infractions, rep sheet, race events, rival re-diff against speed.exe
* Rap sheet pages run on the FEng engine (`RapSheetPage : FEMenuScreen`, Runtime/UI/RapSheetScreens.cs); the package scripts
  drive leave → EXIT_COMPLETE, page switches are FE.Replace (MainMenu_Sub = pop back to the crib). The crib's Rap Sheet opens
  **RapSheetLogin.fng** (CRapSheet::React PC 0x5320E0; the GC decomp opens RapSheetMain). uiRapSheetLogin (0x546510 / 0x52C670):
  Login (Arg 0) → Login2 (Arg 2, 0x03CC94D6 "> %s") → RapSheetMain; accept or click 0x7EABCA56 stops UISND_RAPSHEET_LOGIN /
  LOGIN2; back → MainMenu_Sub with RAP_SHEET cleared.
* PC mouse on the button pages (RapSheetMain 0x52C790, Rankings 0x550E50, TEP 0x52DE40): a click on the highlighted button
  queues BUTTON_PRESSED, else the clicked button becomes current; 0x08B84A5C takes the current button; initial focus is
  BUTTON_<last> (PC "BUTTON_%d"; the decomp's "BL_%d" is GC).
* Data: HighScoresDatabase = `RapSheetRecord` (CareerPursuitDetails Career[8], lengths in Timer ticks, BestValue / BestCar[10],
  CostToStateDetails, TopPursuits[5], RecentCar, HasRapSheet, BustedBefore). CommitPursuitInfo (0x580170): only an evaded
  pursuit updates CTS / career sums / bests (strictly greater) / top five (bounty-ordered insert); busted only sets the previous
  car. GeneratePursuitID (0x56E840): "MW-" + 8 kept characters D L D L D L D L. Stable totals = FEImpound (`RapStable`
  delegates; sold-car history kept). [?] GetTotalBounty = the profile's single bounty.
* CaseFileName (0x57F770): bSNPrintf size 13 → at most 12 characters "MW-nnnn-NAME"; number 1069..7663.
* RapSheetRS (0x52CBC0): 0x90211462 = **RAP_SUMMARY_%.2d** (0x89D414) of frontend **rapsheet_summary_strings** (0x89D428),
  WarningLevel 0xB54D4A41 = [0, 100, 400, 800, 1000]: level = first i with total infractions ≤ WL[i], none → count, 0 → 1.
* Rankings (CalcPursuitRank 0x5A3C30): RapSheetRanks 0xF9A7D5F7 are floats; length compares ticks ≥ (int)(rank × 4000 + 0.5),
  others ≥ (int)rank; not exactly 15 ranks → 16 (UNRANKED 0xF3799455, "--" 0x0FC1BF40 in the detail page). NameId 0x2C3C7FEB.
* RankingsDetail (0x52E750, new `RapSheetRankingsDetailScreen` 1×10): rivals use _CAR only in the single-pursuit view; the player
  is inserted at his rank (16 rows when unranked); ScrollDown while rank − 10 + 4 > 0; POS%d or 0x58B123F7.
* VD (0x52D640): CONFIRMED TO DRIVE 0x2089554C when busted > 0; labels 0xDFC1111B / 0x092D727E / 0x71DAD325 / 0x49F29E04 /
  0x42EB2E82; empty slot "< no record >" 0x73AF0386. US: exe header labels, key 1 toggles view. TEP: disabled empty buttons, wrap
  first / last, accept → 0x587C018B. PD (0x52E450): objects 0x9068C46D..75, 0x9D81523D. CTS: 1×9 from GetCareerCST 0x5A3F10
  (Partial: PursuitStats lacks cop cars / support / traffic / heli / property counters — hook).
* Infractions (uiInfractions.cpp, FEMenuScreen): BUSTED stamp by language; fines = attributes infractions/<key>.amount over the
  kInfraction bits (keys: speeding, racing, reckless_driving, assault, hit_and_run, damage, resisting_arrest, off_road); cash =
  SpendCash + NotifyBusted + serve + heat 1; marker = waive this pursuit; impound dialogs; ends in the career Car_Select over the
  crib. [?] Car_Select args 0x100 / 0x200 not passed. Port note: CareerStable.OnPursuitBusted's stand-in strike is undone on open.
  **Bug for the gameplay owner**: `PursuitStats.InfractionKinds` order differs from kInfraction (GInfractionManager.h:12) — the
  committed bits land on the wrong types until Pursuit.cs is fixed.
* Rep sheet: DEFEATED stamp master 0x7FE4020F gets the language texture (0x511570, English 0x087B7D0A); TITLE_GROUP 0xB71B576D.
  RivalBio (0x551C80): key 3 builds preset "BL%d" (bin 1 "E3_DEMO_BMW") into Showcase; keys 2 / 3 only outside post-rival mode.
* Bounty: only open spawn points listed. Milestones: speed traps after the milestones (AddSpeedtrap 0x51F3C0, icon 0x66C9A7B6,
  "Photo Ticket", 0xB14018BD; SpeedoUnits 1 → ×3.6 KM/H else ×2.237 MPH truncated — resolves §3.5's [?]); only done entries
  blocked; first jump plays bounty_tutorial / pursuit_tutorial (flags 0x400 / 0x200); confirmation Yes/No first No. [?] PC
  multiplies the time-type goal (GMilestone +0xC) by 546.117; the port divides seconds by 60.
* Region unlock: 0xD6C0E097 only for bins 13 / 9; accept only. RivalFlow stage 6 sets HasRapSheet at bin 13 and re-runs the stage
  after storyfmv_rap30.
* **Race events** (uiRepSheetRaceEvents.cpp) → `SafehouseRaceSheetScreen` (Safehouse/RaceListScreen.cs; ctor 0x55A760, Setup
  0x547E30, RefreshHeader 0x547730, NotificationMessage 0x551530, AddRace 0x51F120): ArrayScrollerMenu 3×3 over EVENT_ICON_1..9;
  title 0xBDE82FCC ← 0x2F32A021 in game / 0x84E4A54C in the FE; world races of iCurrentViewBin minus the jump types; checked =
  score flag 2; desc 0x9BA78BA2; header: position / count, bounty / cash "%s %$d", laps, length × 0.001 (× 0.621504 miles,
  flt_89B054), TRACKNAME_ into 0x0F2CD475, cops 0x9B21 (0x61D1C5A5 / 0x73C615A3), best time 0x8FD41BB4 (timed types; 0 →
  "--:--.--" 0x472AA00A), top / average "%$0.2f %s", race icon 0xF97EC5D5, MEDAL_THUMB_n lock 0x0018ED48 / check 0x028FEADD, the
  track map; accept → Yes/No 0x77CF03C5 (first No) → race; back pops; triggers nothing (ToggleList is empty in the exe).
* **Rival challenge** (uiRepSheetRival.cpp; 0x561260 / 0x551A20 / 0x548020) matches; GetDefeatedTexture 0x511820 (French
  0x087B81CD, German 0x087B846E, Italian 0x087B8ECE, Spanish 0x087BB8D4, Dutch 0x087B79BD, Swedish 0x087BB9BF, Danish 0x087B7723,
  Polish 0x087BABFB, Finnish 0x087B80AD, else 0x087B7D0A). Not reproduced: EFadeScreenOff(0x161A918) in the ctor.
* Game won: GameWon / RapSheetLogin*_ENDGAME / RapSheetMain_ENDGAME chain — see fe_safehouse.md §17.4.
