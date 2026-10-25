# World interaction: zones, engage, pause menu, world map, autosave (NFS MW PC v1.3)

Status: static RE only. Nothing here was checked against the running game. Confidence tags:
**[C]** confirmed in code or data, **[I]** inferred (strong), **[?]** open question.
Coordinates are game space (X, Y, Z-up). Rotation is a heading in degrees from +X toward +Y (see REPORT.md).

## 1. Summary: how the player reaches things in MW

| Destination | How it is reached in free roam | Not available |
|---|---|---|
| Safe house menu (`MainMenu_Sub.fng`, mode 2) | Drive into a safehouse zone and press ENGAGE, **or** use Pause > "Jump to Safe House" | While racing, during an active pursuit (except during cooldown, see 3.3), during the prologue (bin 16), during the epic pursuit |
| Car lot (`Car_Select.fng`, mode 5) | Drive into a car lot zone and press ENGAGE | Same as menu gates |
| Customization shop (`CustomizeMain.fng`, mode 3) | Drive into a shop zone and press ENGAGE (only when the current car has a valid record) | Same as menu gates |
| Race event | Drive into the race's engage trigger (radius 25), press ENGAGE, then Accept in `EngageEventDialog` | While racing or in a pursuit |
| Blacklist (in-game `InGameReputationOverview`) | HUDACTION_PAD_DOWN in free roam | Racing; bin 16 |
| World map (`WorldMapMain.fng`) | HUDACTION_PAD_LEFT ("View World Map") in free roam | Racing |
| SMS inbox (`SMS_Mailboxes.fng`) | HUDACTION_PAD_RIGHT in free roam | Racing |
| Pause (`Pause_Main.fng`) | HUDACTION_PAUSEREQUEST | — |

There is **no** "Esc opens the career menu" in MW. The safe-house menu is a front-end screen that you reach only by
entering a safehouse zone or by choosing Jump to Safe House. The current Unity `CareerMenuScreen`-on-Esc should become
`Pause_Main` (section 5), and the safe-house menu should open only through the routes above. **[C]**

HUD input handler: `0x57C660` (FEngHud). Its action ids come from the table at `0x8F689C`: 45 = HUDACTION_PAUSEREQUEST,
46 = HUDACTION_ENGAGE_EVENT, 47 = PAD_LEFT, 48 = PAD_DOWN, 49 = PAD_RIGHT, 50 = SKIPNIS, 51 = NEXTSONG. **[C]**
The handler does nothing while a screen or message queue is pending (`0x633D80`) or while flags `[0x91CB20+0x1E/0x1F]` are set
(probably NIS or fade). **[C]**

## 2. Data: where the locations live

All zones are **gameplay.bin** collections (class `gameplay`). They do not come from TRACKS markers. Reader: `AttribDatabase` /
`gpdump`. The standalone probe used here (not part of the repo) is
`NFSMW_RE/tools/gpprobe` (modes `^menu_gate_ brief`, `blobs <prefix>`, `lang`).

Class tree: `world_activity > menu_gate > menu_gate_{safehouse,carlot,customshop}` (ZoneType text, ZoneList, AutoStart=true,
Persistent=false, FreeRoamOnly=false, IntroNIS="" for all three). Each zone collection is `radiustrigger > zonetrigger >
zone_<type>`, with fields Position (Vector3), Radius, BinIndex, ZoneType, RespawnMarker (key to child `.../respawn`, class `marker`,
Position + Rotation), FireOnExit=true and OneShot=false. The template radius is 20, and every instance overrides it.

### 2.1 Safehouses (`menu_gate_safehouse`, ZoneType `safehouse`) [C]

| Key | Position (x, y, z) | R | BinIndex | Region* | Respawn pos | Respawn heading |
|---|---|---|---|---|---|---|
| menu_gate_safehouse/zone_safehouse_1 | 1777.095, 2503.468, 147.794 | 7 | 16 | college (Rosewood) | 1762.762, 2508.818, 147.284 | -115.416 |
| menu_gate_safehouse/zone_safehouse_2 | 4257.524, 78.698, 9.291 | 7 | 12 | coastal (Camden) | 4245.938, 79.008, 9.712 | -107.138 |
| menu_gate_safehouse/zone_safehouse_4 | 3627.767, 3340.500, 6.773 | 7 | 12 | coastal (Camden) | 3623.124, 3355.153, 7.019 | -27.477 |
| menu_gate_safehouse/zone_safehouse_5 | 2328.785, -66.364, 90.967 | 7 | 8 | city | 2329.800, -82.489, 90.883 | 169.434 |

There is no zone_safehouse_3. The world map has four SAFEHOUSEINDICATOR slots.

### 2.2 Car lots (`menu_gate_carlot`, ZoneType `carlot`) [C]

| Key | Position | R | BinIndex | Region* | Respawn pos | Heading |
|---|---|---|---|---|---|---|
| menu_gate_carlot/zone_carlot_1 | 981.632, 2147.321, 152.294 | 4 | 15 | college | 986.669, 2149.918, 152.305 | -27.312 |
| menu_gate_carlot/zone_carlot_2 | 4204.790, 1257.427, 46.189 | 4 | 12 | coastal | 4206.655, 1269.722, 45.990 | -25.459 |
| menu_gate_carlot/zone_carlot_3 | 1750.890, 512.141, 90.856 | 4 | 8 | city | 1749.084, 517.292, 90.733 | 170.777 |

### 2.3 Customization shops (`menu_gate_customshop`, ZoneType `customshop`) [C]

| Key | Position | R | BinIndex | Region* | Respawn pos | Heading |
|---|---|---|---|---|---|---|
| .../zone_customshop_1 | 698.182, 4523.472, 214.665 | 6 | 15 | college | 716.101, 4523.805, 212.850 | 82.595 |
| .../zone_customshop_2 | 1509.355, 2563.893, 157.616 | 7 | 15 | college | 1497.653, 2571.383, 158.847 | -141.661 |
| .../zone_customshop_3 | 1850.652, 1211.021, 143.550 | 6 | 15 | college | 1842.902, 1215.779, 143.149 | -127.023 |
| .../zone_customshop_4 | 3595.972, 3627.557, 30.477 | 7 | 12 | coastal | 3591.582, 3636.861, 30.631 | 34.067 |
| .../zone_customshop_5 | 3497.088, 2011.371, 73.987 | 7 | 12 | coastal | 3508.649, 2016.714, 72.724 | 108.260 |
| .../zone_customshop_6 | 4246.829, 719.402, 54.270 | 7 | 12 | coastal | 4256.169, 718.011, 54.126 | -95.686 |
| .../zone_customshop_7 | 3398.796, -231.785, 12.447 | 7 | 12 | coastal | 3398.938, -223.055, 12.442 | 4.078 |
| .../zone_customshop_8 | 1064.187, 51.791, 99.678 | 5 | 8 | city | 1069.119, 42.145, 99.596 | 34.987 |

\*Regions are assigned by matching the bounding boxes of the events' `Region` field: college covers x < 2700 and y > 1100,
coastal covers x > 2600, and city is the south (y < 800, x < 3700). The game has no per-zone region field. **[I]**

**BinIndex rule [I]:** the zone exists and is active once the career bin is at or below BinIndex (bins count down from 16 to 1).
BinIndex 16 means from the start, 15 means after the prologue, 12 means once Camden opens, and 8 means once City opens. This
matches the BARRIER_LOCKED unlock bins (REPORT.md) and the SMS texts: Mia gives "another Safe House closer to the coast" when
Camden opens, and "another Safe House" in Rockport City at #9. The code that filters by BinIndex was not located. Two coastal
safehouses share BinIndex 12 **[?]**.

### 2.4 Race engage triggers [C]

* `race_bin_NN/engage` (class `bin_race_engage`, AutoStart) lists `RaceTriggers`, which are `race_bin_NN/engage/engagetrigger_<type>_<id>`.
  There are 146 triggers in all: classes `engagetrigger` (48), `_sprint` (26), `_speedtrap` (20), `_circuit` (19), `_tollbooth` (17),
  `_knockout` (14) and `_drag` (2).
* Each trigger has `TargetActivity` (the event), `Position` (equal to the event's startgrid position), **Radius 25**,
  `InitiallyUnlocked`, FireOnExit=true and OneShot=false.
* `world_engage` (engagetrigger + disengagetrigger) exists only for `race_bin_opm/*` test and online events. You can ignore it.

### 2.5 Other world data

* Prologue: `race_bin_16/drive_to_safe_house` (`drive_to_target`, FreeRoamOnly, AutoStart). It has GPS to `gpstarget`, a boxtrigger
  at 1768.363, 2497.471, 147.748 with Dimensions (2.728, 98.092, 10) and Rotation 48, and MiniMapItem = zone_safehouse_1.
  On arrival it runs `DoSafeHouseIntro` and respawns at zone_safehouse_1/respawn. **[C]**
* Epic pursuit (`race_bin_01/epic_pursuit`, EventID 1.8.1, IsEpicPursuitRace) starts at 4341.374, 48.705, 10.891, beside
  safehouse 2. **[C]**
* Hiding spots (round 9: confirmed as the icon source too — GManager::RefreshZoneIcons 0x5EF640 allocates one type-11 GIcon per type-8
  zone at (Position, Elevation); the pursuit-breaker icons are the 37 IconMarker track position markers; gameplay_core.md §18.1): found in round 2: the 34 TRACK_PATH_ZONE_HIDDEN (type 8) track path zones in L2RA chunk 0x3414A
  (ai_cops.md §12; harness `copzones`). Earlier note: The world map and minimap have an icon type for them (section 6), but no
  gameplay.bin collection, `pursuit_zone` instance or TRACKS name matches. `pursuit_zone` (PursuitLevel, engagetrigger →
  ForcePursuitStart) has no instances. The pursuit attributes `HiddenZoneTimeMultiplier` and `TimeToHideInZone` suggest the
  hiding spots are world volumes, probably inside a streamed track chunk. The leads are the minimap update code around the
  HUD class with vtable `0x8A2040` (ctor `0x59D500`) and icon type 11.

## 3. Zone trigger rules

The logic is Lua 5.0 bytecode ("LuaP", EA HUFF-compressed) in gameplay.bin `stategraph_*` messagehandler `bytecode` blobs. The
probe mode `blobs <prefix>` decompresses them and lists their string constants. The native functions are registered at
`0x61E750`. **[C]**

### 3.1 Menu gates (safehouse / carlot / customshop)

State graph `stategraph_menu_gate` has two states, `initial` and `nis`.
1. **triggerenter** (`initial`): if the sender is the player, and `Game.AllowMenuGates()`, and (for ZoneType == "safehouse")
   `Game.AllowEngageSafehouse()`, and the zone is in `ZoneList`, and `IsActiveMenuGate`, then set `InZone` and call
   `Game.EnterGateZone(ZoneType)`. **[C strings / I order]**
2. EnterGateZone (`0x611FB0`) forwards to the HUD element `Hud_MenuZoneTrigger` ("Engage_Mechanic"/"EventIcon", ctor `0x5A0530`,
   interface method `0x57BDE0`). That shows the engage prompt, with the zone label from `0x56E100(zoneType)`. **[C]**
3. **triggerexit**: `Game.ExitGateZone()` (`0x611FF0` → `0x57BEA0`), which hides the prompt. Exit fires when you leave the
   radius (FireOnExit). **[C]**
4. **ENGAGE pressed** (HUD 46) in free roam, inside the zone, with no race prompt pending: the HUD calls zone methods
   `+0x20` and `+0x24` (`0x569100`) → message `MEnterSafeHouse` → handler `entersafehouse`. If `IntroNIS` is set, the state goes
   to `nis` and plays `EPlayRaceNIS`. In both cases it then calls `Game.DoZoneMenuAction`. **[C]**
5. **DoZoneMenuAction** (`0x612030`) stores the zone's `RespawnMarker` in GRaceStatus+0x108. When the ZoneType is "safehouse" it
   also stores it in +0x10C, the last safehouse used. It then opens the front end (`0x58C640`): safehouse → `MainMenu_Sub.fng`
   (mode 2), carlot → `Car_Select.fng` (mode 5), customshop → `CustomizeMain.fng` (mode 3). The shop opens only if the
   current car record is valid (`+0x10 != 0xFF`). **[C]**
6. When the player returns to the world, they spawn at the stored RespawnMarker position and heading, standing still. **[I]**

**Speed:** no speed check was found anywhere in the ENGAGE path, EnterGateZone or the Lua. The prompt shows inside the
radius at any speed, and ENGAGE works at any speed. **[I]** Because the radii are small (4–7 m), the player in practice stops in
the garage mouth. Do not copy Unity's current `SpeedMps < 9` gate unless testing shows the game has one.

### 3.2 Race engage

`stategraph_bin_race_engage` has the states `initial → wait → prompt → reset → wait`.
* **wait/triggerenter**: if the sender is the player, and `Game.AllowEngageEvents()`, and the trigger is in RaceTriggers, and
  `IsRaceUnlocked(TargetActivity)`, and (very likely) `!IsRaceCompleted`, then go to `prompt`. **[C strings / I]**
* **prompt/stateenter**: `Game.EnterEngagableTrigger(TargetActivity)`. The HUD engage prompt shows the race name and type icon (`0x57BD10`).
* ENGAGE pressed: if the race flag bit 5 is set (`0x5FBD90`, boss race), it opens `InGameRivalChallenge`
  (`0x624360(3)`). Otherwise it opens **`EngageEventDialog.fng`** (`0x624810`). The dialog shows the track map, the
  type, Laps, Distance, Cop Probability, Top/Average Speed, Best Time, Cash Reward and the Beat/Cash table, with
  **Accept / Decline**. **[C]**
* **acceptentercareerevent**: `Activity.Run(TargetActivity)` (race start), then `reset`. **decline**: Exit then re-Enter the
  engagable trigger (the prompt stays). **triggerexit**: `reset` (prompt removed). **[C]**

### 3.3 Allow flags (GRaceStatus `0x91E00C`, recomputed by `0x5EBA60`) [C]

```
notRacing  = (no active race)
inPursuit  = a pursuit object exists
cooldown   = pursuit state == 2        // most likely COOLDOWN (cops lost sight) [I]
AllowEngageEvents    (+0x2CE) = notRacing && !inPursuit
AllowMenuGates       (+0x2D0) = notRacing && !inPursuit
AllowEngageSafehouse (+0x2CF) = notRacing && (!inPursuit || cooldown)
```
So during a pursuit only a **safehouse** can be entered, and only in cooldown ("Safe House is a great place for your car
to cooldown"). The HUD ENGAGE handler also has a special path for this: when the player vehicle's state is 2 and it is in a
safehouse zone, it calls vehicle `+0x130`, which probably ends the pursuit as evaded. **[I]**

### 3.4 Other ENGAGE uses (same button) [C]
* After a race finishes (racing flag set, race over): `PostRace_Results.fng`.
* After a pursuit ends (HUD+700 set): `PostRace_Pursuit.fng`.
* Blocked if `0x735620()` is set or profile flags bit 6 or bit 3 of `[0x91CF90+300]` are set.

## 4. HUD prompt

`Engage_Mechanic` group plus `EventIcon` in the HUD packages. While inside a zone or engage trigger, the HUD shows the
type label and the button prompt. The PC key-binding label is "Enter Shop /^Engage Event" (0xAB1032FA). The PC default key is
probably Enter **[?]**; read the `HUDACTION_ENGAGE_EVENT` ControllerDataRecord in attributes.bin to confirm.

## 5. Pause menu (`Pause_Main.fng`, builder `0x52B4D0`) [C]

The package has an icon scroller (option_1..10, ICON_SCROLL_REGION) and a title/description string. Options are created
in this order. Game mode is `[0x91E000+0x1964]` (0 = quick race, 1 = online, 2 = career). `[+0x1960]` is 1 while racing.
`bin` is the current bin byte. `epic` means the current race is EventID "1.8.1" (`0x56DC00`) or IsEpicPursuitRace.
`pursuit` is the byte `[0x91D3D8]`.

**Career, free roam:**
| Condition | Options |
|---|---|
| bin 16 (prologue) | Resume Free Roam, Quit to Career Main Menu, Options |
| epic | Resume Free Roam, Quit to Career Main Menu, Performance Tuning, Options |
| in pursuit | Resume Free Roam, Performance Tuning, Options |
| otherwise | Resume Free Roam, **Jump to Safe House**, Performance Tuning, Options |

**Career, racing:**
| Condition | Options |
|---|---|
| bin 16 | Resume Race, Restart, Quit to Career Main Menu, Options |
| epic | Resume Race, Restart, Quit to Career Main Menu, Performance Tuning, Options |
| otherwise | Resume Race, Restart, Jump to Safe House, Quit to Free Roam, Performance Tuning, Options |

**"No-resume" variant** (pause object +0x148, for example the restart menu after a fail; the PC Back button is disabled):
for career with bin ≤ 15 and not epic, it shows Restart, Jump to Safe House, Quit to Free Roam. Otherwise it shows
Restart, Quit to Career Main Menu. Quick race shows Resume Race, Restart, Quit to Quick Race Menu / Quit to Main Menu, and
Options.

Actions and confirmations (all use `InGameDialog.fng` with OK/Cancel; handler `0x51DA30`):
* Jump to Safe House: "Are you sure you want to jump to the Safe House?" (0x6925D0BE). In a pursuit the text is "...All pursuit
  progress will be lost." (0x15A1B5A9). While racing it is "...All race progress..." (0xCDE4CAE8). On OK the game unloads
  the world and opens `MainMenu_Sub.fng` (mode 2), the same as `Game.JumpToSafeHouse` (`0x6052B0`). The player respawns at
  the **last safehouse used** (GRaceStatus+0x10C). If none has been used, the fallback is probably the newest unlocked safehouse **[?]**.
* Quit to Career Main Menu: "Are you sure you want to quit to the career main menu? All ..." (0xECD92696).
* Quit to Free Roam: 0x9887EB98. Restart: 0x4D3399A8.
* Performance Tuning opens `Pause_Performance_Tuning.fng`. The predicate `0x510900` checks the installed perf parts over 7
  categories, probably to enable or disable the option **[?]**. Options opens `Pause_Options.fng` (PC: `Pause_PCDisplayOptions`,
  `Pause_PC_Controller`).
* The pause menu has **no World Map entry**. The map has its own button.
* Label hashes: Resume Free Roam 0x01BD185C, Resume Race 0xDED357E7, Restart 0xF893AFA1, Jump to Safe House 0x3C14C420,
  Quit to Free Roam 0x9DC599B0, Quit to Career Main Menu 0x690E9B7C, Performance Tuning 0x6A3672A2, Options 0x2B5A03A8.
  The icon hashes are the second push in each case: 0x12BB5EA2 resume, 0xB295A6B6 restart, 0x4C9E34E6 jump/quit,
  0x56FFBD2C quit-to-free-roam, 0x483238FD tuning, 0x520DE4E3 options.

## 6. World map (`WorldMapMain.fng`, opened by `0x625AD0` from HUD PAD_LEFT, free roam only) [C]

* TRACK_MAP (340×340 in FE space) is the city map, using the same 1024² minimap image and transform as the HUD
  (REPORT.md: u = X·0.1538 + 188, v = −Y·0.1538 + 779.3). It has a cursor ("Move Cursor", outer ring) and a selection crosshair.
* **Filter header** (`0x5488B0`, object 0xD259525F) cycles with L1/R1 (PC keys **[?]**) between LOCATIONS, EVENT, RACE and
  PURSUIT. The tip text: safe houses and shops are under Locations, and races and photo-ticket milestones under Events.
* **Zoom** cycles Zoom All, 1.5X, 2.0X and 3.0X (labels 0x213587BF / 0x0A9BE7D7 / D8 / DA).
* **Selected item panel** shows Item Title (0x9331FD4F), Race Length (0xFEEEB39B) and a mode icon.
* **Buttons**: "Activate GPS" / "Deactivate GPS" (Enter; it toggles the GPS route to the selected icon, global `0x91CA4C`),
  "Toggle"/"Show Icons"/"Hide Icons" (toggle icon visibility per category, PC key 2), Exit (PC key 4) and Back (Esc).
  The tip also mentions "Auto Drive" (label 0x00CA0312), but no code reference to it was found **[?]**.
* **No jump/teleport from the world map** (no code reference). "Jump to this location and engage in a pursuit
  (milestone)?" (0xCD195D0B / 0xA5A8409A) belongs to the Blacklist Bounty/Milestones pages (`0x552570`, `0x551E90`) in both
  the in-game and safehouse versions: Yes warps the player to the challenge location and starts the pursuit.
* Icon type table `0x8F3ACC`, records of {type, filter bit, FE name format, label, description}:

| type | bit | FE object | label |
|---|---|---|---|
| 1 | 0x100 | MMICON_SPRINT_%d | Sprint |
| 2 | 0x80 | MMICON_CIRCUIT_%d | Circuit |
| 3 | 0x400 | MMICON_DRAG_%d | Drag |
| 4 | 0x200 | MMICON_KNOCKOUT_%d | Lap Knockout |
| 5 | 0x1000 | MMICON_TOLLBOOTH_%d | Tollbooth |
| 6 | 0x800 | MMICON_SPEEDTRAP_%d | Speedtrap Race |
| 7 | 0x8000 | MINIMAP_ICON_EVENT_RIVAL | Challenge (rival) |
| 8 | 0x200000 | SAFEHOUSEINDICATOR_%d | Safe House |
| 9 | 0x800000 | CAR_LOT_%d | Car Lot |
| 10 | 0x400000 | SHOP_%d | Shop |
| 11 | 0x2000000 | HIDING_SPOT_%d | Hiding Spot |
| 12 | 0x4000000 | PURSUIT_BREAKER_%d | Pursuit Breaker |
| 13 | 0x100000 | SPEED_TRAP_CAM_%d | Photo Ticket |
| 14 | 0x100000 | SPEED_TRAP_RACE_CAM_%d | Speedtrap Race |
| 15 | 0x1000000 | MMICON_CHECKPOINT | — |

  The minimap creates up to 8 instances of each type. Textures: Minimap_Icon_Safe_House, _Car_Lot, _Chop_Shop,
  _Hiding_Spot, _Pursuit_Breaker, _Event, _Event_Rival, _Speedtrap and _Event_Cell. Events reach the map through the Lua
  `AddAvailableEventToMap` and speedtraps through `AddSpeedTrapToMap`. The zones are probably registered from the menu_gate
  zones (MiniMapItem references point at zone collections) **[I]**.
* Pursuit Breaker and Hiding Spot icons show only during pursuits **[I]** (per tips 86093B53 / 966D081D).

## 7. In-world Blacklist screens (InGame*) [C]

* HUD PAD_DOWN (free roam, bin ≤ 15) → `0x624360(0)`. If the rival challenge is available it opens
  `InGameRivalChallenge.fng`, otherwise `InGameReputationOverview.fng` (BLACKLIST: rival list, Race wins, Bounty, Milestones,
  "Challenge Rival"). Sub-pages: `InGameRaceSheet` (races of the bin; selecting one starts it, which is the second way to
  enter races, per tip 640E072B), `InGameMilestones`, `InGameBounty` (start a pursuit challenge), `InGameRivalBio` and
  `InGameRivalChallenge`. They are the same screens as the `SafeHouse*` versions but use `InGameBackground.fng`, so the world
  stays loaded.
* The FE flag `0x91D3D8` blocks the RivalChallenge/Overview choice in `0x624360` when set (in a pursuit **[I]**).

## 8. Autosave [C core / I triggers]

* `Autosave_Overlay.fng`: AUTOSAVE_ICON_GROUP (Generic_AutoSave.tga with a spinning outer ring and glow) at FE (243, −177),
  top right. It shows while a save is in progress (`0x518C50`, memcard state machine `0x562C30`). The same package also
  carries the SMS mailbox icon states.
* Event `EAutoSave` (vtable 0x8A6820, ctor `0x620920`) is raised from:
  1. `0x602010`, the race wrap-up run before the next activity starts (called by `Game.StartRace` `0x60DBD0`). It saves when the
     previous race exists and `0x5FBD20()` (probably won or completed) holds and the race is not EventID 16.1.0, or when the
     epic pursuit is involved.
  2. `0x602770(bool)`, the activity cleanup. It saves when the argument is true and the player is in free roam.
  3. `0x5A7EA0`, the HUD post-race/PhotoFinish path, after the results screen.
  4. The `0x5FE5AE` area (race setup/cleanup).
* Autosave applies only when enabled on the profile (`[0x91CF90+3]`). Implementation rule for the rebuild: autosave (show the
  overlay for about 2 s) after any race or pursuit result is dismissed back to free roam, when leaving a safehouse, car lot
  or shop to the world, and after milestone or rival rewards. The front-end save points beyond these were not traced **[?]**.

## 9. Implementation checklist (Unity)

1. Load all `menu_gate_*` zones from gameplay.bin (section 2) with Position, Radius, BinIndex, ZoneType and RespawnMarker (pos + heading).
   Activate a zone when `profile.Bin <= BinIndex`.
2. ~~Test the trigger on horizontal distance~~ → the WTrigger volumes of §11 (round 11). Apply the allow flags (3.3).
   Show the HUD prompt. ENGAGE (Enter) opens the screen for the zone type, saves the respawn (and the last safehouse), and
   unloads or hides the world.
3. Race markers: 146 engage triggers, radius 25 at the startgrid, only for unlocked and not-yet-won races of available bins.
   ENGAGE opens EngageEventDialog (Accept starts the race, Decline keeps the prompt), or RivalChallenge for boss triggers.
4. Esc opens Pause_Main with the option lists in section 5. Jump to Safe House opens a confirm dialog, then the safehouse menu
   (MainMenu_Sub), then respawns at the last safehouse.
5. The world map key opens WorldMapMain (filters, zoom, GPS toggle, no teleport). The Blacklist key opens InGameReputationOverview.
6. Autosave overlay per section 8.

## 10. Open questions

* ~~Hiding spot positions: source not found (2.5)~~ — the type-8 track path zones (RefreshZoneIcons 0x5EF640, gameplay_core.md §18.1).
* Whether BinIndex is compared with ≤ (inferred), and why two coastal safehouses share bin 12.
* Pursuit state 2 = cooldown (inferred from the AllowEngageSafehouse logic and the tips).
* PC default keys for ENGAGE / PAD_LEFT / PAD_DOWN / PAD_RIGHT (read ControllerDataRecords).
* Exact `IsRaceCompleted` use in the race-engage Lua (the bytecode was not decompiled; only its constants were read).
* Jump to Safe House destination when no safehouse has been visited yet. On a new career the prologue's drive_to_safe_house
  ends in zone_safehouse_1, so +0x10C is probably set there.
* Whether Performance Tuning is greyed out or hidden without perf parts.

## 11. Round 11: WTrigger / GTrigger — the physical trigger volumes (World/Common/WTrigger.cpp 0x766760-0x784EB9, Gameplay/GTrigger.cpp)

The zone and engage radii of §3 and the race goals are not distance checks in the exe: every gameplay trigger is a **WTrigger**
(CARP::Trigger, 0x40 bytes) tested by **WTriggerManager::Update** (0x781C90, from SimSystem::OnTask 0x6F0EFF every sim frame)
against every rigid body. Port: `Core/WTrigger.cs` (Unity-free: WTrigger, WTriggerBody, WTriggerManager), `Runtime/Game/
Triggers.cs` (GTrigger, GameTriggers, GIcon, Availability), `Runtime/Lua/LuaTriggers.cs` (the stategraphs' triggers on it);
harness `wtrigger selftest | list [filter]` (3185 trigger records: 2339 boxes, 628 radius triggers, 218 planes).

### 11.1 WTrigger (layout, PC flags)
`fMatRow0Width` (right.xyz, w = width), `fFlags` (+0x10), `fHeight`, `fEvents`, `fType` / `fIterStamp` (+0x1E), `fMatRow2Length`
(forward.xyz, w = length), `fPosRadius` (position, w = the bounding radius). The shape sits in bits 4-7 of fFlags (CheckCollideRB
0x766A8C: `and dl, 0xF0; cmp dl, 0x20`; 0x766B5F: `shr eax, 4; and eax, 0xF; cmp eax, 3`): **1 box, 2 sphere, 3 cylinder**. The
PC flag bits are the GameCube header's shifted one byte: 0x1 enabled; **0x100 in the world list** (GTrigger::Enable 0x5E2C90 /
AddActivationReference 0x5E8540 set it, Enable(false) clears it); **0x200 OneShot** (the record's `OneShot` 0xCE4261AC; FireEvents
disables the trigger after firing — GC `fFlags & 2`); 0x400 / 0x800 set by the GTrigger ctor, meaning not traced [?]; **0x10000**
the body-kind bit cars activate [?] (ProcessRB: `fFlags & rBody->GetTriggerFlags()`); 0x40000 silencable (GC 0x400); **0x80000
directional** (GC 0x800; CheckCollideRB 0x766AEB `test ecx, 0x80000` → TestDirection 0x766960: dot(forward, velocity) ≥ 0);
0x100000 tilted box (GC 0x1000, UpdateBox when the matrix' up is not world up); **0x800000 fire-on-exit list** (GC 0x8000) and
**0x4000000 fire on enter** (GC 0x40000) — SubmitForFire 0x7817B0 tests exactly these two. The GTrigger ctor (0x601790) sets
`fFlags = (fFlags & 0xF0) | 0x4810D01`: enabled, in-list, 0x400/0x800, cars, fire-on-exit **and** fire-on-enter — so every
gameplay trigger fires at once on entering, is kept on the exit list, and fires again when the body leaves; not directional.

### 11.2 The GTrigger ctor's shapes (0x601790; attribute hashes resolved with the Jenkins attribute hash)
Position 0x9F743A0E, Rotation 0x5A6A57C6 (degrees; the box matrix is the identity yawed by −Rotation/360 turns — the direction
is (cos r, sin r) in game X/Y, the same as GRaceParameters::ExtractDirection 0x5DCD00), Dimensions 0x6D9E21AD, Radius
0x39BF8002, Width 0x5816C1FC, OneShot 0xCE4261AC, TargetActivity 0x277566F3, BinIndex 0x6CE23062.
* **Dimensions present → box** (shape 1): width = Dimensions.y, length (along the heading) = Dimensions.x, **height = 2·Dimensions.z**
  (the record's z is a half height: `16_1_0_partial_dday/ai_cp1` (2, 42, 40) → w 42, l 2, h 80), bounding radius √((w/2)² + (l/2)²).
* **else Radius → cylinder** (shape 3): radius r, width = length = 2r, **height = 4r** (an engage trigger r 25 → 100 m tall; a
  speed trap r 30 → 120 m); the test is the xz distance plus a height band — no sphere is ever built from gameplay data.
* **else a plane**: a box `Width` wide (the `wrongway` planes: 150 m), 1 m long, 100 m high (the GC ctor's 50 each way).
fPosRadius is stored in the sim frame (−Position.y, Position.z, Position.x); the port keeps the Unity frame (x, z, y).
* The trigger kind by template (DerivesFrom 0x5E28B0): `engagetrigger` 0xF05931AB (flag 0x200 at +0x18; the GIcon type from
  GetRaceType 0..5 → 1..6, a boss race → 7), `zone_carlot` 0x73049919 (+0x19 |= 0x20, icon 9), `zone_customshop` 0x4698966B (0x40,
  10), `zone_safehouse` 0x326427BB (0x80, 8), `speedtrap` 0xB05871D3 (0x1; icon 13, or 14 when OpenWorldSpeedTrap is false —
  GRaceStatus 0x5DA0B0), `finishline` 0x45E28759 (0x4), `time_bonus_checkpoint` 0x76720F56 (0x8), `moneybag` 0xA3E939E9 (0x10),
  `areaunlocktrigger` 0x98217DBF (icon 15). GIcon::GIcon (0x5E9EC0) at the record's Position; with BinIndex > 0 and the career's
  current bin (playerRecord +0xB0) ≤ BinIndex the icon gets flags 1 | 2 (on the map, in the world). The ctor also builds the
  trigger's own CARP event list (+0x88: one EGTriggerInternal 0xC34649C0 event → NotifySimableTrigger).

### 11.3 The manager (WTriggerManager::Update 0x781C90)
1. `fProcessingStimulus = 1`; for every IRigidBody in the list: simple bodies (traffic, cops) → **ProcessSRB** (0x781A00: grid
   search radius max(1.5, r) + |v|·dT; a trigger with the players-only bit (GC 0x80) is skipped for a body not owned by a player),
   others → **ProcessRB** (0x781840). Each: WGrid::FindNodes around the body, every trigger not yet stamped this iteration that
   IsEnabled(silencables) and whose flags intersect the body's trigger flags and that **CheckCollide**s → SubmitForFire.
2. **CheckCollideRB** (0x7669F0): the body a frame ahead (p + v·dT) against the trigger: sphere → |c − p|² ≤ (r + |v|dT + R)²;
   else the xz distance within that, directional → TestDirection(v), then cylinder: `p.y + r ≥ c.y − h/2 && p.y − r < c.y + h/2`;
   box: Dynamics::Collision::Geometry::FindIntersection (0x48C100) of the car's OBB (its matrix, GetDimension, swept by v·dT)
   against the trigger's box (MakeMatrix 0x6200F0, half extents w/2, h/2, l/2). **CheckCollideSRB** (0x766CB0): box → a sphere of
   the body's radius against the OBB; sphere / cylinder as above with strict `<`.
3. **SubmitForFire** (0x7817B0): 0x800000 → a FireOnExitRec {trigger, simable} in the std::set (fires at once instead when the
   simable no longer exists); 0x4000000 → FireEvents now; neither → FireEvents now. **FireEvents** (0x7668A0): gEventDynamicData
   {simable, the trigger position, the trigger, fTriggerStimulus = GetCurrentStimulus()} → EventManager::FireEventList; OneShot →
   Disable.
4. `fProcessingStimulus = 2`; every exit record whose body still collides stays; otherwise FireEvents (stimulus 2) and the record
   goes (a vanished simable goes without firing). DeleteRefs (0x781BF0) drops a destroyed trigger's records; Restart / Init clear
   the list (Init also clears 0x400 on the 0x200-flagged collision-asset triggers).
5. **GTrigger::NotifySimableTrigger(sim, stimulus)** (0x5F6A30) from the EGTriggerInternal: stimulus 1 → if the simable is not
   in the inside list (+0x7C): MarkAsInside (0x5F5B50) + Hermes MTriggerEnter (0x5DB090); always MTriggerInside (0x5DB250);
   stimulus 2 → MarkAsOutside (0x5ED690) and MTriggerExit (0x5DB170) only when the record's FireOnExit is set.
6. Enable(true) (0x5E2C90) = WCollisionAssets::AddTrigger (0x783B60: the world list and the WGrid) + flag 0x100 + the particle
   effects (flares: CreateAllParticleEffects 0x5E2B10, not ported); Enable(false) = RemoveTrigger (0x785430) + the flag cleared.
   AddActivationReference / RemoveActivationReference (0x5E8540 / 0x5E85A0): a reference count from the running activities
   (ActivateReferencedTriggers 0x5E87F0; GActivity::Reset deactivates); the first enables, the last disables.
   GetIntersectingTriggers (0x784C50): xz bounding-circle overlap over the grid (the port: over the list).

### 11.4 Port and deviations
* `WTriggerManager.Update(dt, bodies)` runs the two passes over the registered triggers (no WGrid: a bounding-circle pre-check);
  the OBB tests are a separating-axis implementation with the frame's motion added to the extents [ours: FindIntersection is
  the physics module's]. `GameTriggers` builds the GTriggers of the running stategraphs' referenced triggers (LuaTriggers), tests
  the players' and racers' cars (traffic / cops: simple bodies in the exe, no gameplay handler reacts to them [?]) once per frame
  with dt = max(frame, 1/120) and raises Enter / Inside / Exit; LuaTriggers posts MTriggerEnter / MTriggerExit (MTriggerInside is
  a C# event only — no gameplay.bin handler takes it [?]). The previous port's "a warp is not a crossing" guard is gone (the
  exe's look-ahead test has no segment sweep; a 1 m plane is caught through the car's box extents). `GIcon.IsVisible(type,
  flags)` = GetIsIconVisible / UpdateIconVisibility; `Availability` = UpdateTriggerAvailability / UpdatePursuit's bytes
  (+0x2C8..+0x2D0); EventMarkers now reads them (AllowEngageEvents for the race icons, AllowMenuGates / AllowEngageSafehouse for
  the gates). The C# rules' RaceTrigger (RaceRules.cs) keeps its segment test for the non-Drive race path.
* Not ported: the flares, WGrid, the 0x200 collision-asset triggers of the track (WCollisionAssets, the world owner's), SetTargetability.
* Round 9: **activation** is `(fFlags >> 8) & body.GetTriggerFlags()` (ProcessRB 0x781961-0x781979; RBVehicle::GetTriggerFlags
  0x69AFD0 = 0x64 player / 0x68 other vehicle, | 0x20000 cop, 0 asleep; `WTrigger.Activates`, `PlayerCar.TriggerFlags`); the
  gameplay 0x4810D01 → 0x4810D holds bits 4 and 8, so every car fires them. `UpdateBox` (inlined on PC, WTrigger.cpp:105-140)
  ported. `HasEvent` has no PC symbol (inlined into the event code or stripped) and LoaderTrigger/UnloaderTrigger are empty
  switches (dead). The WGrid lookups of GetIntersectingTriggers / ProcessRB stay the bounding-circle scan [ours].
