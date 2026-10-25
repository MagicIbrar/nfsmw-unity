# Races — implementation spec (NFS MW 2005 PC v1.3)

Status: data facts come from `GLOBAL\gameplay.bin`, `attributes.bin` and `LANGUAGES\English.bin` and are **confirmed**. Code
facts come from static analysis of speed.exe and are marked with their address. Anything else is marked **(guess)**.
Probe used: `<scratchpad>/races_probe` (modes `table`, `events`, `own`, `field`, `cls`, `lang`, `jh`).

## 1. Event data model (gameplay.bin)

### 1.1 Type resolution
The type comes from the parent class name, with two overrides:

| parent class | type enum (exe) | rule |
|---|---|---|
| `p2p` | 0 Sprint | point to point, 1 lap |
| `circuit` | 1 Circuit | laps only if `IsLoopingRace` (circuit=True; the D-Day `16_*` events are circuit-parented with IsLoopingRace=False, so they behave as sprints) |
| `drag` | 2 Drag | |
| `lapknockout` **or any event with `KnockoutsPerLap>0`** | 3 Knockout | `race_bin_14/14_3_1_lapknockout` and `race_bin_06/14_3_1_r_lap_ko` are circuit-parented but set KnockoutsPerLap=1 |
| `tollboothrace` | 4 Tollbooth | |
| `speedtraprace` | 5 Speedtrap | |
| `challengerace*`, `pursuit_mini_game` | – | pursuit challenges (out of scope; see the pursuit spec) |

The same 0..5 enum indexes the QR option builders (0x7ba7c0), the results pages (0x5972e0) and the option titles
(`sprint/circuit/drag/knockout/tollbooth/speedtrap options`). There are 218 career/QR events: 39 circuit, 49 p2p, 18 lapknockout,
11 drag, 68 tollbooth (including 34 Challenge Series tollbooths) and 32 speedtrap.

### 1.2 Template defaults (inherit through `race → engaged → basicrace → <type>`)
| field | default | notes |
|---|---|---|
| NumLaps | 3 (basicrace, circuit), 1 (p2p and children) | career circuits use 2–4 |
| CashValue | 1000 | cash for a win (see §8) |
| Reputation | 100 | career values 2000…23999 (see §8) |
| TrafficLevel | 100 (race), per-event 0…100 | density percentage (see §6) |
| CopsInRace | True in the template, **False in 147 events** | True in 19 career events and all Challenge tollbooths |
| CopDensity | 100 | 0 only in 14_4_1 |
| DifficultyLevel | 100 | <34 easy, 34–66 medium, >66 hard (0x5fac10) |
| CatchUp | True | |
| CatchUpOverride | False | **no event sets it**, so per-event CatchUpSkill/Spread are dead data unless the override is on (§5.3) |
| CatchUpSkill / CatchUpSpread | "0.2" / "1000" (Text) | per-event "1"/"250" (bins 15 and 14), "0.5"/"500" (D-Day) |
| CatchUpIntegral / CatchUpDerivative | 5e-5 / 0.001 | D-Day: Derivative 0.005 |
| KnockoutsPerLap | 0 (1 in lapknockout) | |
| TimeLimit | 0, tollboothrace 90 | per-event tollbooth limits are 30…125 s |
| OvertimePenaltyPerSec | 10 (speedtraprace) | see §4.6 |
| RankPlayersByDistance / ByPoints | True / False; speedtraprace False / True | |
| DoCountdown / RollingStart / InitialPlayerSpeed | True / False / 0 | the D-Day opener: rolling start at 60 (mph) with no countdown |
| DoPhotofinish | False | True on 5_4_1, 7_4_2 and 11_4_2 (tollbooth), 7_7_1_r (drag) and 20_2_1 |
| CheckpointsVisible | True | False on speedtrap races and the D-Day events |
| BustedLives | 1 | busted once means the race is lost |
| MaxHeatLevel / ForceHeatLevel / UseWorldHeat | 10 / 0 / False | |
| ChanceOfRain / TOD | 0.5 / -1 | the D-Day events force TOD and no rain |
| IntroNIS / QuickRaceNIS / FinishCamera | IntroNis04 / "" / DefaultFinish | the per-event finish cameras are `PHoldFin_00..28` |
| PlayerCarPerformance | 1 | |
| Region | college | |

### 1.3 Per-event children
* `racestart`/`startgrid` (marker: Position, Rotation = heading in degrees) and `racefinish`/`finishline` (boxtrigger:
  `Dimensions=(depth, width, height)`, e.g. (2,25,40); `FlareSpacing` 5 m with `ParticleEffect=fxgame_flare_red`).
* `Checkpoint[]` are boxtriggers with default Dimensions (40,40,40); per-event values look like (2,40,40). Tollbooths use
  `time_bonus_checkpoint` (TimeBonus default 30 s). Speedtrap races list `SpeedTrapList[]` (speedtrap radiustrigger:
  Radius 20, `ThreshholdSpeed` 88, `Bounty` 100, `SpeedTrapCamera` DefaultSpeed; the last two are for open-world traps)
  and also carry plain AI checkpoints (`proxy_checkpoint` with MasterCheckpoint).
* `wrongway` (`resetplayertrigger`: plane Width 150, ResetsPlayer=True) sits just behind the grid facing backwards.
  Crossing it resets the player to the start **(guess: only before the first checkpoint)**.
* `shortcut` markers (1,029 in all): ShortcutMinChance 0 and ShortcutMaxChance 1 by default, some per-event 0, and
  `ExcludedCharacters` lists racers that never take it. The AI takes a shortcut with chance
  `lerp(Min, Max, skill01)` **(guess)**.
* `trafficspawntrigger` (drag events): Radius 200–300, InitialSpeed 30, `TrafficCharacter` (CarType trafficcoup /
  trafstwag / trafdmptr / trafvanb / semicon…), `TargetMarker` = `start_marker` or `finish_marker`. When the player
  enters the radius, spawn that car at the trigger and send it to the target marker (oncoming or same-direction lane traffic).
  The `trafficN` characters have `spawn` (carmarker) and `triggerN` (planetrigger Width 30) children: a semi that pulls out
  when a plane trigger is crossed.
* `Opponents[]` → `character` collections (see §5.1). `Barriers[]` → BARRIER_SPLINE groups (already handled).

## 2. Event start flow
1. **Engage**: an `engagetrigger_<type>` (radiustrigger Radius 25, `FireOnExit`=True, `InitiallyUnlocked`) at the
   race icon. Stopping in it shows `EngageEventDialog.fng` (event name, type icon `Mode_Icon_*`, laps, length, cash). Accept
   → `ELoadingScreenOn` (Loading.fng; on PC FadeScreen) while opponents load (`LOADING_OPPONENT_MODELS/AUDIO`).
2. Teleport everyone to the grid. The player is in front and the opponents are staggered behind
   **(the grid layout is not in data; use 2 columns, 3.2 m lateral and 7 m longitudinal)**. `ForceStartPosition`
   (character → carmarker) overrides a slot, as in the D-Day and OPM events.
3. **Intro NIS**: career uses `IntroNIS`, Quick Race uses `QuickRaceNIS` and falls back to `IntroNIS`. Tollbooth uses
   `IntroNisToll01`; Blacklist rival races use `IntroNisBL02..16`. The cars finish the NIS on the grid (start-grid space,
   see REPORT §NIS animation). `IntroMovie` (FMV) plays before it when set.
4. **Countdown** when `DoCountdown`: `EShowRaceCountdown`, then "3, 2, 1, GO!" (strings `Ready` 847121DD, `GO!` 61223AB5)
   at one step per second. Everyone holds the brake until GO and the AI is released on GO. Drag adds launch judging (§4.5).
   `RollingStart` skips the countdown: the player starts at `InitialPlayerSpeed` mph.
5. GO → race clock starts at 0; HUD = `HUD_SingleRace.fng` (`HUD_Drag.fng` for drag).

## 3. Common race rules
* **Progress / ranking**: `RankPlayersByDistance` = order by route distance (lap-major); finished racers are ordered by
  finish time ahead of everyone still racing. Speedtrap ranks by points (§4.6).
* **Checkpoints**: must be passed in order; the next one is highlighted when CheckpointsVisible. Laps = the whole
  checkpoint list then the finish line; on circuits the finish line doubles as the start/finish gate.
* **Wrong way**: show `WRONGWAYIMAGE` when the velocity points >90° away from the route for more than about 2 s
  **(the threshold is a guess)**.
* **Race over** for the player: `EShowRaceOverMessage` (`Hud_RaceOverMessage`), then `MEnterPostRaceFlow` →
  `PostRace_Results.fng` (pursuit after a race → `PostRace_Pursuit.fng`; 0x642140).
* Messages (English.bin hash: text): 5DFB6667 "WINNER", E815BC6D "Winner!", 82E4DAFD "You Lost!",
  C9A64EFB / 443FAB5A / AC122037 / 4342D430 / 442E5F4F / FFC2867C "You finished second…seventh!",
  0E2F1243 "You Were Knocked Out", 1B59940C "You were Knocked Out!", 1CE129E0 / 556ED1B2 "Time's up" / "Time's Up!",
  6C09C82D "Race Over.  Time's Up!", 490A722D "Disqualified", 4BA0D22F "Totaled!", 7449D26D "Blown engine!",
  532B5186 "BUSTED", 1EA459F8 "Last Lap", BFD1936F "You have the Lead!", 9B2AF3C7 "You Lost the Lead!",
  1C074E14 "TIME BONUS!", B1E2E5C2 "Perfect Launch!", 0598B065 "Perfect Shift!", 27D2DD45 "Good Shift!",
  2202B5B9 "Early Shift!", C44D3943 "Lap Record".
* **Busted** during a race (CopsInRace): BustedLives=1, so the race is lost and the normal bust/impound flow runs.
* **Photo finish** (`DoPhotofinish`): the finish camera is `ECameraPhotoFinish` (`PhotoFinishScreen`,
  `HUDTEXTURESPHOTOFINISH.BIN`, labels `PHOTOFINISH_RIVAL` / `PHOTOFINISH_TOOBOOTH`). It is a still frame with the
  player's time, shown before the results.

## 4. Race types

### 4.1 Circuit (type 1)
Laps = NumLaps (QR: 1–8). Win = first across the line after the last lap. Track the best lap (`Best Lap`, `Lap Record`
for QR records). "Last Lap" message when the leader (player) starts the final lap.

### 4.2 Sprint (type 0)
One pass start → checkpoints → finish. The results page "STAGES" shows 4 intermediate splits plus the finish (§7.2).

### 4.3 Lap Knockout (type 3)
Circuit rules plus: at the end of every lap except the last, the `KnockoutsPerLap` (=1) racers in last place are knocked
out (`EKnockoutRacer`, `MNotifyKnockedOut`). Knockout happens when **the leader** completes the lap **(guess; this is
what the current RaceManager does)**. A knocked-out AI is removed (it pulls over). A knocked-out player loses at once
("You were Knocked Out!"). Career laps = 3 with 3 opponents, so the final lap is one-on-one. In QR, **laps are forced
equal to the opponent count**: changing Opponents writes Laps (0x7abfd0, `if raceType==3: laps = opponents`).
`KnockoutTime` (0) is unused.

### 4.4 Tollbooth (type 4)
Solo time attack (no opponents; QR shows only Direction and Traffic). The clock starts at `TimeLimit` and counts down;
passing each `time_bonus_checkpoint` adds its `TimeBonus` (default 30) and shows "TIME BONUS!". The clock reaching 0
means "Time's Up!" and the race is lost. Reaching the last booth/finish wins. `TargetGold/Silver/Bronze` (seconds, e.g.
1_4_1: 470/480/490) exist only on 7 events and the medal UI is unused on PC **(guess)**. Results page "TOLLBOOTHS"
lists the time remaining at each booth plus the total time.

### 4.5 Drag (type 2)
* Course: a straight-ish route with checkpoints (Dimensions (2,40,40)/(2,20,40)), 1 opponent in career (`drag_opponent`
  or the boss), traffic from `trafficspawntrigger` scripting (§1.3) plus `TrafficPattern=drag` (pattern with no
  vehicles, so ambient traffic is off).
* **Manual shifting forced**: automatic gearbox disabled; shift up/down `GAME_ACTION_SHIFTUP/DOWN`. The HUD tachometer
  (`Hud_DragTachometer`, `DRAG_RPM_%d_LINES`) has a green shift window; the indicator turns green "when it is time to shift".
* Shift judging **(guess, the thresholds are not found in data)**: rpm/redline ≥ 0.97 → "Perfect Shift!"; 0.90–0.97 →
  "Good Shift!"; < 0.85 → "Early Shift!"; staying at the limiter for more than about 2 s → "Blown engine!"
  (`EEngineBlown`/`MNotifyEngineBlown`), and the race is lost (DNF). Perfect shifts give a short torque bonus.
  `PERFECT SHIFT %:` appears in stats.
* **Perfect launch** (`EPerfectLaunch`): rpm inside the green band (about 70–85 % of redline, **guess**) at the moment
  of GO, otherwise wheelspin or bog.
* Steering is lane-change only (snap between lanes); a head-on with traffic → "Totaled!" and the race is lost.
* Win = first across the finish; times shown to 0.01 s.

### 4.6 Speedtrap (type 5)
* Sprint course with `SpeedTrapList` cameras (radius 20 m by default, 30 in places). Each racer's speed at a trap is
  recorded and the **sum** decides the winner ("Racer with the highest cumulative speed through all the photo radar
  stations wins."). Ranking = `RankPlayersByPoints` (points = summed speed, km/h internally at racerInfo+0x120,
  converted for display by ×0.27778×2.23699 to mph, 0x5964a0).
* `OvertimePenaltyPerSec=10`: once the first racer finishes, every racer still on course loses 10 points (speed units)
  per second until they finish **(meaning inferred from the name and the template; not traced in code)**.
* The race ends when all racers finish (or the timeout). Win = highest total.

### 4.7 Rival (Blacklist boss) races
* `race_bin_NN.BossRaces` lists the boss event chain. Each boss event sets `BossRace=True`, one boss opponent (`boss`
  character: PresetRide `BL<n>`, `RacerName=BLACKLIST_RIVAL_<n>_LEADERBOARD`), and `PostRaceActivity` = the next event in
  the chain (the last event has none, or the epic pursuit for bin 1).
* Chains: 15: 15_1_2 → 15_1_1_reversed; 14: 14_2_4 → 15_2_1_sprint_reverse; 13: 13_2_3 → 14_2_2_r; 12: 12_2_1 → 12_1_2;
  11: 11_1_2 → 12_1_2_r; 10: 10_2_1 → 10_7_3 drag; 9: 9_5_3 → 9_2_1; 8: 8_2_1 → 8_7_5 drag; 7: 7_2_2 → 7_2_3; 6: 6_1_1 →
  6_5_2; 5: 5_2_2 → 5_5_4 → 5_2_1_r; 4: 4_5_2 → 4_7_4 drag → 4_5_1_r; 3: 3_1_2_r → 3_2_2 → 3_1_2; 2: 3_1_1_r → 9_2_2_r →
  2_2_1; 1: 1_5_2 → 1_7_3 → 4_2_1 → 1_1_2 → 1_2_3 → epic_pursuit.
* The rules are the normal ones for the type; you must win each race in turn and losing any race returns you to free roam
  with the chain reset **(guess)**. Cash (`CashValue`) sits on the last event of each chain only (bin 2: 75000, 3: 60000,
  4: 54000, 5: 48000, 6: 28000, 7: 24000, 8: 20000, 9: 16000, 10: 12000, 12: 8000).
* `RivalBestTime` (s) is the rival's record shown on the race sheet (`Rival's Best Time:`). `QuickRaceNIS` is set on
  every boss race.

## 5. AI opponents

### 5.1 Roster and cars
* Event `Opponents[]` → `character`: career world races use `race_bin_NN/character1..3` (SkillLevel and an unnamed float
  `B1ECE070`, no car); boss races use the `boss` character with `PresetRide` (GlobalB preset, e.g. BL4, E3_DEMO_BMW
  for Razor) and `RacerName`. Drag uses `race_bin_NN/drag_opponent` (no fields).
* SkillLevel by bin (characters / boss): 1: 80/80, 2: 80/80, 3: 75/80, 4: 70/75, 5: 60/65, 6: 55/60, 7: 50/55,
  8: 45/50, 9: 40/45, 10: 35/40, 11: 25/30, 12: 20/25, 13: 15/20, 14: 10/15, 15: 0/0. Burger King challenge: 40.
* `B1ECE070` (name not recovered) by bin: 1–2: 0.8 (Razor 0.9), 3: 0.7, 4–5: 0.6, 6–7: 0.5, 8–9: 0.4, 10: 0.3, 11: 0.2,
  12: 0.1, 13: 0.05, 14: 0. Monotone with progress, so it is most likely the **opponent car performance/upgrade fraction**
  (0 = stock, 1 = fully upgraded) **(guess)**.
* Generic characters have no CarType, so the game picks cars at runtime **(rule not found)**. Use cars from the car lot
  whose `UnlockedAt` is at or above the bin (the same tier as the player), distinct, upgraded by `B1ECE070`.
* `attributes.bin aivehicle/racers`: MAXIMUM_AI_SPEED 400 (km/h, guess), Acceleration/TopSpeed multipliers 1.0.
  Templates `character_racy/smart/dumb` exist with no data.

### 5.2 Difficulty
`DifficultyLevel` → index (0x5fac10): `<34 → 0 Easy; 34..66 → 1 Medium; >66 → 2 Hard`. Career events are 100 (Hard) except
the D-Day events (50 / 75). QR sets 33 / 66 / 100 for Easy / Medium / Hard (0x5e9ae0). The index feeds the catch-up blend
`s = {0.0, 0.5, 1.0}[index]` (table 0x8a37d8).

### 5.3 Catch-up (rubber band), FUN_005ff990, run per AI racer each AI tick (from 0x4293d0)
Inputs, from the AI racer's race-status record `r`:
* `t` = r+0x18, the curve parameter on [0,100]: **race completion %** (the curves have xmin 0 and xmax 100).
* `e` = r+0x7c: the distance gap to the player in metres (**sign guess: positive when the AI is behind the player**).
* `dv` = r+0x5c: the rate of the gap (derivative term).
* `I` = `n·r+0x30 / (r+0x78 · r+0x8)` when n = byte r+0x2e > 0, else 0 (integral term; the fields are not identified).

Which parameters apply:
* Career/challenge (mode 0): only if the event's `CatchUp` is true, with `s` from §5.2.
* Quick race (mode 2): `s = clamp((q+1)/2, 0, 1)` where q is the QR difficulty mapped to −1/0/+1 (same 0/0.5/1). With
  catch-up off in QR the function is not used **(guess)**.
* Default (CatchUpOverride false; this is the path every shipping event uses), with built-in 5-point curves over t = 0, 25, 50, 75, 100:

| curve | t=0 | 25 | 50 | 75 | 100 |
|---|---|---|---|---|---|
| spreadEasy (0x8f5b58) | 1000 | 900 | 750 | 600 | 800 |
| spreadHard (0x8f5b6c) | 300 | 275 | 250 | 150 | 250 |
| skillEasy (0x8f5b80) | 0.75 | 0.75 | 0.75 | 0.5 | 0.25 |
| skillHard (0x8f5b94) | 0.75 | 0.75 | 0.75 | 0.75 | 0.25 |

  `spread = lerp(spreadEasy(t), spreadHard(t), s)`, `skill = lerp(skillEasy(t), skillHard(t), s)`, `Ki = 5e-5`, `Kd = 0.01`.
* Override (CatchUpOverride true): `spread = curve(CatchUpSpread)(t)`, `skill = curve(CatchUpSkill)(t)`,
  `Ki = CatchUpIntegral`, `Kd = CatchUpDerivative`. The Text values are comma-separated lists of up to 11 points
  resampled evenly over 0..100 (0x5e8cc0); a single value is a constant. Without race params: skill 0.2, spread 1000,
  Ki 5e-5, Kd 0.001 (0x5ffcc0).
* Output: `spread = max(spread, 100)`; `u = clamp((2/spread)·e + Kd·dv + Ki·I, −1, +1)`;
  `catchup = u·skill` (written to the AI's catch-up value, ai+0x7ac) and `u` is returned separately.
  QR with option flag 0xFF5EE5D6 set: `s = s + (1−s)/2` and the returned `u` is forced to 1.0.
* Use (0x4293d0): `catchup` is damped: halved when FUN_00404af0 is true, else multiplied by a curve. Skipped when the
  AI mode is 2 (drag?). How it drives the car is **not traced**. Implement it as throttle and top-speed scale
  `1 + 0.25·catchup` **(guess)**: AI behind speeds up, AI ahead eases off, ±1 saturates at a gap of `spread/2` metres.
  Example: Hard at the start gives spread 300, so a 150 m gap is full catch-up at 0.75 strength.

### 5.4 Path following
The AI follows the race route (road graph through the checkpoints) and may take `shortcut` markers (§1.3). The
proxy_checkpoints in speedtrap races are AI waypoints.

## 6. Traffic and cops in races
* `TrafficLevel` 0..100 (FUN_005e9a90 clamps) scales the ambient traffic density for the race. Career values: bin 15: 10–40,
  bins 14–10: 15–30, bins 9–5: 0–100, bins 4–1: 40–80, drag: 100 (scripted only), Challenge tollbooths 0–100. QR mapping
  (0x56dd20): **None 0, Minimum 10, Moderate 30, Maximum 50**.
* `ForceTrafficDensity` (challenge events: 10/40/50/80/100) overrides the density **(guess: an absolute %)**.
  `TrafficPattern` (`drag`, `cityhighway`) picks a trafficpattern collection. The default pattern has SpawnTime 4 s,
  SpeedStreet 35, SpeedHighway 55 (mph, guess).
* Race barriers: XO_TrackBarrierTraffic pieces block traffic entering the route (REPORT §Scenery).
* `CopsInRace` true: patrols spawn and pursuits can start during the race at `CopDensity` %. `MaxHeatLevel` caps heat.
  `UseWorldHeat` False means the race uses its own heat, which starts at 1 **(guess)**. `ScriptedCopsInRace` and cop
  characters (`14_2_4_sprint/cop_1..3` with spawn carmarkers) place scripted cops. QR has a hidden CopsInRace flag
  (option struct +0xC; when set, +9 is copied as heat), and no single-player QR menu shows it: **QR has cops off**.
* If a pursuit is active at the finish, the post-race flow goes to the pursuit (the player must evade before the results).

## 7. Finish and PostRace_Results (GLOBAL\InGameB.lzc, package PostRace_Results)

### 7.1 Flow
The player finishes → AI racers keep driving until they finish or the player leaves → `PostRace_Results.fng`
(0x642140 picks it; `PostRace_Pursuit.fng` for pursuits). AI that has not finished when the screen opens gets a
**projected time** (guess) or "DNF" (`DNF%`).

### 7.2 Object map
Rows: `LINE1_GROUP` … `LINE11_GROUP`, each with `COLUMN1_DATAn`, `COLUMN2_DATAn` and `COLUMN3_DATAn`. Hashes (bStringHash):
COLUMN3_DATA1 = D2A5B02A … COLUMN3_DATA9 = D2A5B032, COLUMN3_DATA10 = 275BB59A, COLUMN3_DATA11 = 275BB59B. The row
highlight bar is `06745352` with scripts POS1..POS11. Column header strings: `586AB4A6` (col 1, x −114), `44AC8987` (col 2, x 19),
`30EE5E68` (col 3, x 406), in group `22FAB9E7`. Page title: group `2D691760` (text `B7CD77E5` plus shadow `848122A4`, default
"RACE RESULTS"). The racer name / page label is `EB43CCB0` ('PLAYER', `7C7D1335` plus shadow `EAF1A9F4`) with L1/R1 arrows
(`79354351`/`6B364F8B`) to page between racers. Buttons: `6F2315C4` "Stats" (key 2), `8819D8BA` "Restart" (key 1),
`DE02CF84` "Exit" (Enter). Time format `mm:ss.hh` ("00:00.00"). `77CE0364` in group `7D913050` is another "00:00.00"
timer (**guess: the player's total time**).

**Standings page** (0x5964a0): title "STANDINGS" (FF115FFF); the label object at screen+0x1f0 reads "Statistics" (D0B8AA33).
Columns: "Position" (96B05F47) | "Name" (CE678AD3) | "Time" (B67DA102); for speedtrap the third column is "Speed"
(7540FB04), shown as `%0.0f` plus "MPH"/"KM/H" (8569AB44/8569A25F) from the units setting. One row per racer in rank order
(racerInfo+0x10 = rank).

**Per-racer statistics page** (0x5972e0), with the label at +0x1f0 reading "Standings" (8159A0B2, the button that goes back):
| type | title | col1 / col2 / col3 | rows |
|---|---|---|---|
| Sprint, Drag | "STAGES" (34BA50FF) | "Stage" (E8B7D527) / "Position" / "Time" | 4 split rows (stage n, position and split time from racerInfo+300…), then a 5th row with the total time if finished |
| Circuit, Knockout | "LAPS" (9C8D7FE8) | "LAP" (BF9C) / "Position" / "Time" | one row per lap: lap n, position at the end of the lap, lap time; knocked-out laps show position "–" |
| Tollbooth | "TOLLBOOTHS" (D10A8EA2) | "Tollbooth" (A15E4505) / – / "Time" | one row per booth (split), then the final row = total time |
| Speedtrap | "SPEEDTRAPS" (ECD0E6A6) | "Speedtrap" (EE1EDC76) / "Position" / "Speed" | one row per trap: the speed recorded and the position at that trap |

Other stats strings available for a stats sheet: "Top Speed", "Average Speed", "Perfect Shifts", "BEST LAP TIME",
"LAPS LED:", "Average N20 Consumed", "Time Behind", "Cash Won", "Bounty Won".

## 8. Rewards
* **Cash**: on a career **win**, add `CashValue` (world races: bin 15 1500 … bin 1 25000; drag 750–20000; boss chain
  finals as in §4.7; reversed ".r" races reuse the bin value). There is no cash for 2nd place or below, and none in
  QR or Challenge Series. Shown as "Cash Won" / "Cash Reward".
* **Race win count** feeds `race_bin.RequiredRacesWon` (career spec).
* **Reputation** (int, career 1999…23999 for some events, 100 inherited by the rest, 0 on boss events): read
  together with the event hash at 0x5a29b0 and passed to a points award. **Most likely the bounty/reputation given for a win
  (guess)**; it may also be only the online/rep-sheet score. Until verified, award `Reputation` as bounty only if the
  value is ≥ 1000 **(guess)**.
* Boss chain completion → pink slip, markers (`rewards/reward_*`: pink slip, cash bonus, parts, out-of-jail, impound) →
  career spec.

## 9. Restart / quit
* Pause (`Pause_Main.fng`): "Restart Race" (00DC0C9F), and "Quit to Free Roam" (9DC599B0, career), "Quit to Quick Race Menu"
  (4349998B) or "Quit to Career Main Menu" (690E9B7C). Quitting a race counts as a loss.
* Restart re-runs §2 from step 2: same grid, no engage dialog, the intro NIS is skipped **(guess)**, everything reset (laps,
  splits, clock, AI, traffic, heat 0).
* PostRace: Restart (key 1), Stats/Standings toggle (key 2), Exit/Continue (Enter) → free roam at the finish line (career)
  or back to Track_Select (QR).

## 10. Quick Race front end (FRONTEND\FrontB.lzc)
Flow: MainMenu → Quick Race type (sprint/circuit/drag/knockout/tollbooth/speedtrap) → **Track_Select** → **Track_Options** →
Car_Select → **Quick_Race_Brief** → load.

**Track_Select**: title "quick race track select". The track list is every event of that type whose `NeverInQuickRace` is
false. `AvailableQR=True` events are unlocked from the start; the rest unlock when the career event is won **(guess)**
("This track is locked. To unlock it progress through Career Mode", `CarSelectLock.tga`). Widgets: `TRACK_MAP` (image
`TRACK_MAP_<eventid>.tga`, e.g. TRACK_MAP_14_1_2), `B5154998` type name, `B5154999` length ("10KM"), `B515499C` best time,
`BBF970CD` event id, `number`/`B2037BDC` index/count ("9 / 25"), L1/R1 region switch (`4510987F` "Region"), arrows
`FCD5B255`/`3DF39A82`. Unlocked from the start (22): 5_5_3, 6_2_2, 16_1_1_r, 7_1_1, 7_4_2, 8_3_1, 10_3_1, 11_2_1, 11_5_1,
11_7_1, 12_4_1, 12_7_3, 13_5_1, 14_4_1, 15_1_1, 15_1_1_reversed, 15_1_2, 15_2_1, 15_3_1, 21_1_1, 21_2_1, 21_2_2.

**Track_Options**: rows `OPTION_NAME_1..9` / `option_data_1..9` with arrows `7BCD6704..0C` / `7C51B6D7..DF`; "Defaults" button
`D6463100` (key 1). The option set per type (builders 0x7acad0/0x7ac8b0/0x7accb0/0x7ace50/0x7ad310/0x7ad0f0):

| type | options in order |
|---|---|
| Sprint | [Track Direction], Traffic Level, Opponents, Difficulty, Catch Up |
| Circuit | [Track Direction], Laps, Traffic Level, Opponents, Difficulty, Catch Up |
| Drag | [Track Direction], Opponents, Difficulty, Catch Up |
| Knockout | [Track Direction], Opponents, Laps (= Opponents), Traffic Level, Difficulty, Catch Up |
| Tollbooth | [Track Direction], Traffic Level |
| Speedtrap | [Track Direction], Traffic Level, Opponents, Difficulty, Catch Up |

[Track Direction] appears only when the event has a reverse layout (FUN_005fac80; `racestartReverse`/`racefinishReverse`).
In 2-player split screen the Opponents, Difficulty and Catch Up rows are hidden.

| option (label hash) | values | per-type store (FUN_0056dbe0(type)) | applied as |
|---|---|---|---|
| Track Direction (A88FFEB4) | Forward (DE6EFF34) / Backward (A1CD823E) | +5 | reverse start/finish |
| Laps (48494E83) | 1…8, wraps | +4 | NumLaps |
| Opponents (3384A679) | 1…(4 − humanPlayers) → 1–3 (1–2 in split screen), wraps | +7 | opponent count |
| Difficulty (4D156786) | Easy (61973E01) / Medium (3747F6D0) / Hard (6198E2EE) | +8 | DifficultyLevel 33/66/100 |
| Traffic Level (EB9DFC09) | None (8CDC3937) / Minimum (73C615A3) / Moderate (A2CCA838) / Maximum (61D1C5A5) | +10 | TrafficLevel 0/10/30/50 |
| Catch Up (8B8E913A) | Off (70DFE5C2) / On (417B2604) | +0xB | CatchUp |
| (hidden) cops | bool | +0xC (+9 heat) | CopsInRace |

Defaults are not traced; use **Laps 2 (circuit) / 3 (knockout), Opponents 3, Difficulty Medium, Traffic Minimum,
Catch Up On, Forward (guess)**. Online/LAN uses a different builder (0x7ac5f0: collision detection, performance matching,
number of players, etc.).

**Quick_Race_Brief** ("quick play brief"): car panel (manufacturer `3E01AD1D`, logo `B05DD708`, TOP SPEED / ACCELERATION /
HANDLING bars `4037F0E4`/`46BCC4AA`/`D108E905`), track block (`B5154998` type, e.g. "Circuit - K.O.", `B5154999` track
name, `B515499A` length, `B515499B` laps, `B515499C` opponents), settings (`B515499E` difficulty, `B515499F` catch-up,
`B515499D` traffic) with labels `28462C61..67` ("Track:", "Length:", "Laps:", "Opponents:", "Traffic:", "Difficulty:",
"Catch Up:"), lock note `A63265D0` ("Unlock this car in CAREER by beating …"), buttons Accept (`73DCB662`),
Randomize (`D6463100`), Back. The LED bar `6A0831F6` (`22170AE0..6`) is the loading progress.

## 11. Gaps in the current Unity code (RaceManager.cs / AIDriver.cs)
1. The race type must honour `KnockoutsPerLap>0` and `IsLoopingRace` (the D-Day circuit-parented sprints).
2. Opponents are hard-capped at 3 and boss races add the boss on top. Use `Opponents[]` exactly; for QR take N from the option.
3. AI strength is hand-tuned (MaxSpeed 62 + …). Replace it with §5.2/§5.3: the PID catch-up with the built-in curves and
   `s` from DifficultyLevel, plus car upgrades from `B1ECE070`.
4. The speedtrap race is run as a sprint. Record speed at each `SpeedTrapList` trap, rank by the sum, and add the overtime penalty.
5. Drag has no manual-shift, launch or engine-blow logic, and no scripted traffic triggers.
6. The results screen is a MessageScreen. Build the PostRace_Results pages of §7.2.
7. Cash is awarded on any player win; restrict it to career, and add the Reputation handling once settled.
8. Tollbooth (the TimeLimit / TimeBonus clock) is correct. `CheckpointsVisible` false should hide the markers.
9. Countdown: the current code uses 3.99 s then GO at 0.8. Use 3, 2, 1 at 1 s each, then GO and release.

## 12. Uncertainties (to verify in game or with more RE)
* The meaning and sign of the catch-up inputs (r+0x18 / r+0x7c / r+0x5c / integral term) and how ai+0x7ac maps to
  throttle and top speed; the curve multiplying it when FUN_00404af0 is false.
* Option flag hash 0xFF5EE5D6 (forces full catch-up in QR mode); the role of QR mode 0x449c.
* `Reputation` → bounty or not; whether 2nd–3rd places pay anything (assumed no).
* Opponent car selection for generic characters; the meaning of `B1ECE070`.
* Drag shift and launch thresholds, the engine-blow timer, and the exact lane-change behaviour.
* The speedtrap `OvertimePenaltyPerSec` semantics; the knockout trigger (leader lap vs. per-racer lap completion).
* The Sprint "STAGES" split points (4 splits; positions along the route not traced; assume 20/40/60/80 %).
* QR default option values; the QR track unlock rule; whether a restart replays the intro NIS.
* The heat start level for CopsInRace races, and how `ForceTrafficDensity` combines with `TrafficLevel`.
