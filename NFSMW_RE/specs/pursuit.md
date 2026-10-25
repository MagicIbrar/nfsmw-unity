# Police and pursuits (NFS MW 2005 PC v1.3)

Sources: `GLOBAL/attributes.bin` classes `pursuitlevels`, `pursuitescalation`, `pursuitsupport`, `infractions`,
`aivehicle`, `smackable`, `speechtune`, `milestonetypes`; `gameplay.bin` (MaxHeatLevel, ForceHeatLevel, ...);
`LANGUAGES/English.bin`; static analysis of speed.exe. Marks: **[D]** = read from data, **[C]** = read from code,
**[I]** = inferred, **[?]** = unknown or unverified.

Probe used: `pursuitprobe` (standalone, compiles `NFSMWUnity/Assets/NFSMW/Core`). Modes:
`table <root> <class> <c1,c2..>`, `raw <root> <class> <hexkey> <cols> arr`, `lay <root> <class> <hexoff> <cols>`,
`lang <root>`. Collection names were recovered by hashing: `heat_01..heat_10`, `race_01..race_10`,
`support01..support10`, `supportrace_01..supportrace_10`, and the infractions `speeding`, `racing`,
`reckless_driving`, `assault`, `hit_and_run`, `damage`, `resisting_arrest`, `off_road`.

## 1. Tables and how a heat level is chosen

* `pursuitescalation/default` **[D]** holds four RefSpec[10] tables: `heattable` = heat_01..heat_10,
  `racetable` = race_01..race_10, `supporttable` = support01..support10, `supportracetable` = supportrace_01..10.
* `0x409060` (IPerpetrator::SetHeat) **[C]**: `index = (int)heat - 1`. When the player is in a race activity
  (`[0x91E000]+0x1960 == 1`), it uses `racetable`/`supportracetable` (cops inside races, the `CopsInRace` events).
  Otherwise it uses `heattable`/`supporttable`. The tables are reloaded only when the integer heat or the race flag changes.
* Heat 1–5 is free roam. Heat 6 is reachable only where `gameplay.MaxHeatLevel` allows it: `race_bin_01/epic_pursuit`
  = 6, challenge 34 = 6, challenge 35 (collectors) = 7. Other challenges = 5, base race types = 10. Heat 7–10 rows
  exist but are not normally reached **[D]**. The free-roam cap source is **[?]** (the clamp is `[perp+0xdc .. +0xe0]`).
  The heat 6 row is listed with the others below.

## 2. Per-heat numbers (heattable, free roam) [D]

`dflt` is `pursuitlevels/default`, the fallback for fields a row does not set. Distances are metres, times are seconds.

| field | dflt | h1 | h2 | h3 | h4 | h5 | h6 |
|---|---|---|---|---|---|---|---|
| cops `{type,count,chance}` | midsize 1/0 | midsize 4/100 | copghost 5/100 | copgto 7/100 | copgtoghost 8/100 + copheli 1/50 | copsporthench 10/50 + copheli 1/60 | copsportghost 8/80 + copheli 1/50 + copsuvpatrol 2/80 |
| BustSpeed (km/h) | 5 | 9 | 12 | 19 | 20 | 25 | 25 |
| evadetimeout (cooldown length) | 5 | 20 | 45 | 75 | 90 | 120 | 120 |
| HiddenZoneTimeMultiplier | 4 | 4 | 5 | 6 | 7 | 8 | 8 |
| TimeToHideInZone | 3 | 1 | 1 | 1 | 1 | 1 | 3 |
| frontLOSdistance | 151 | 151 | 151 | 151 | 151 | 151 | 151 |
| rearLOSdistance | 51 | 151 | 151 | 151 | 151 | 151 | 151 |
| heliLOSdistance | 201 | 251 | 251 | 251 | 251 | 251 | 251 |
| TimePerHeatLevel | 60 | 1 | 1 | 1 | 1 | 1 | 600 |
| 80DEB840[15] (s per heat, by career index) | 1×15 | 120×11,180,240,360,600 | 120×7,180,240,240,360,480,600×3 | 120×5,180,240,300,600×7 | 120,240,360,480,600×11 | 600×15 | (dflt 1) |
| 1E2A1051 (layout+0x88, bounty per 10 s) | 5 | 100 | 500 | 1000 | 5000 | 25000 | 30000 |
| DestroyCopBonusTime (combo window) | 120 | 10 | 10 | 10 | 10 | 10 | 10 |
| TimeBetweenFirstFourSpawn | 10 | 40 | 30 | 20 | 15 | 10 | 10 |
| TimeBetweenCopSpawn | 20 | 10 | 10 | 5 | 5 | 2 | 5 |
| NumPatrolCars (search mode) | 2 | 1 | 1 | 1 | 1 | 1 | 1 |
| FullEngagementCopCount | 1 | 5 | 10 | 15 | 20 | 25 | 30 |
| FullEngagementRadius | 10 | 50 | 50 | 50 | 50 | 50 | 50 |
| NumCopsToTriggerBackup | 0 | 2 | 2 | 2 | 2 | 2 | 2 |
| BackupCallTimer | 5 | 180 | 120 | 120 | 120 | 90 | 90 |
| roadblockprobability % | 0 | 0 | 10 | 25 | 30 | 40 | 50 |
| roadblockspikechance % | 0 | 0 | 0 | 0 | 70 | 80 | 85 |
| roadblockhelichance | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| SearchModeRoadblockChance % | 50 | 0 | 3 | 5 | 7 | 10 | 12 |
| SearchModeRoadblockRadius | 1000 | 1000 | 1000 | 2000 | 5000 | 10000 | 10000 |
| SearchModeHeliSpawnChance % | 0 | 0 | 0 | 0 | 2 | 4 | 5 |
| SearchModeCityMPH / HwyMPH | 50/71 | 75/100 | 75/100 | 75/100 | 75/100 | 75/100 | 75/100 |
| HeliFuelTime | 20 | 10 | 10 | 10 | 60 | 90 | 180 |
| TimeBetweenHeliActive | 20 | 0 | 0 | 120 | 180 | 180 | 180 |
| BoxinDuration / Tightness | 3/0.5 | 4/0.5 | 5/0.6 | 6/0.8 | 7/0.9 | 8/1 | 10/1 |
| RollingBlockDuration / Tightness | 3/0.5 | 4/0.5 | 5/0.6 | 6/0.8 | 7/0.9 | 8/1 | 10/1 |
| CollapseSpeed / Aggression | 15/0 | 58/0.4 | 68/0.5 | 78/0.6 | 88/0.7 | 96/0.8 | 120/0.8 |
| CollapseInner/OuterRadius | 3/15 | 6/15 | 5/15 | 4/8 | 3/5 | 3/5 | 3/7 |
| MaxCopsCollapsing | 6 | 4 | 5 | 6 | 7 | 8 | 8 |
| SpeedReactionTime | 0 | 1.75 | 1.5 | 0.75 | 0.5 | 0.1 | 0.1 |
| StaggerFormationTime | 10 | 7 | 6 | 5 | 4 | 3 | 5 |
| CTSFor911 / NumCiviHitsFor911 | 1750/5 | 400/3 | 600/4 | 800/5 | 1000/6 | 1500/7 | 1750/5 |
| Lifetime911 / TimeInactiveFor911 | 180/120 | 45/120 | 45/120 | 45/120 | 45/120 | 45/120 | 180/120 |
| EvadeSuccess/EventWin/MilestoneComplete HeatAdjust | .95/.94/.93 | .9/.9/1 | .9/.9/1 | .9/.9/1 | .9/.9/1 | .9/.9/1 | .9/.9/1 |
| MeterDeadZoneBustedDistance / EvadeDist | 35/50 | 35/50 (all rows) | | | | | |
| Siren* (all rows) | InitMinPeriod 0.5, InitVariation 1.5, MaxScreamTime 3, MaxYelpTime 6, ScreamPeriod 60, WailPeriod 11 | | | | | | |

`CopFormations` records are `{type, duration s, weight}` **[D/C]**:
h1 `{3,40,100}`; h2 `{2,40,100}`; h3 `{2,40,25}{1,40,60}{3,40,20}{6,40,40}`; h4 `{1,40,50}{2,40,50}{6,40,30}{3,40,50}`;
h5 `{3,40,50}{2,40,50}{1,40,50}{6,40,50}`; h6 `{3,40,60}{2,40,50}{6,40,50}`; dflt `{1,10,10}`.

Heat 7–10 **[D]**: h7 copsuvpatrol 6/90, Bust 19, evade 120, TimePerHeat 600, roadblock 35 (spike 85).
h8 copsportghost 8/100 + heli 1/60, evade 220, roadblock 50, search roadblock 60, front/rear/heli LOS 201/101/301,
FullEngagement 80/200. h9 copmidsize 4/100 + heli 1/80, NumPatrolCars 8, roadblock 100 (spike 100), evade 60.
h10 copsport 1/90 + heli 1/100, evade 220, roadblock 80, heli fuel 400, TimeBetweenHeliActive 5, TimePerHeat 2000.
Bounty/10 s for h7–h10: 30000, 5000, 10, 5000.

Race table (`race_01..10`, cops inside races) **[D]**: midsize 2, copghost 3, copgto 4, copgtoghost 5 + heli 60,
copsporthench 5 + heli 60, copsportghost 6 + heli 50, copsuvpatrol 8 + heli 60, copghost 8 + heli 75/80/90 (r8–r10).
BustSpeed is 5 everywhere, evadetimeout is 120 (r7 60, r8 60, r9 90), TimePerHeatLevel is 900, and the formation is
`{7,10,100}` (r8–r10 `{4,...}`). Roadblocks appear only in r8–r10 (35/40/50 %). FullEngagementCopCount is 5..30, then 1.

## 3. Pursuit support (helicopter / heavy SUVs / Cross) [D + C 0x419680]

`pursuitsupport` per heat (supporttable index = heat-1):

| | MinimumSupportDelay | AirSupport `{type,chance,dur}` | HeavySupport `{type,weight,dur,x}` | LeaderSupport `{type,weight,dur,onceChance,minTime}` |
|---|---|---|---|---|
| h1 | 60 | {1,0,60} | {3,0,0,0} | {5,0,0,0,0} |
| h2 | 60 | {1,0,60} | {3,0,20,0} | none |
| h3 | 60 | {1,0,0} | {4,5,40,0} {3,10,20,0} | none |
| h4 | 45 | {1,50,60} | {3,25,30,100} {4,25,20,100} | none |
| h5 | 35 | {2,60,90} | {3,25,30,100} {4,40,20,100} | {5,5,2000,10,120} |
| h6 | 10 | {2,100,180} | {3,20,30,100} | {5,5,2000,100,60} |
| h7 | 60 | {2,0,0} | {3,30,30,100} | none |
| h8 | 10 | {0,75,180} | {3,40,20,80} | {5,0,500,0,0} |
| h9 | 10 | {2,100,2000} | {3,0,0,0} | {5,100,2000,100,30} |
| h10 | 10 | {2,100,999} | {3,0,20,100} | {5,0,2000,0,0} |

Selection, `0x419680` **[C]**. It runs every 10 s (timer reset to 10.0) once `pursuitTime >= MinimumSupportDelay`:
1. Leader, once per pursuit: for each LeaderSupport whose `minTime < pursuitTime`, take it if `rand(100) < onceChance`.
2. Otherwise draw `r = rand(100)`. If no heavy unit is active, walk HeavySupport subtracting `weight`. The first record
   that drives `r < 0` is chosen; its `dur` is how long it stays active, and a 15 s timer is set.
3. If `r` is still >= 0, walk LeaderSupport the same way using `weight`.
* Type 5 = Cross (`copcross`) **[I]** (Cross appears from heat 5; h6 guarantees him after 60 s).
* Heavy types 3 and 4 **[?]**: one is the Rhino head-on ram (`copsuv`, AIGoalHeadOnRam), the other probably the heavy
  roadblock (`copsuvl`). The 4th HeavySupport int (0/80/100) is unknown.
* AirSupport.type 0/1/2 is a helicopter strategy **[?]**. `0x4195f0` asks "any AirSupport with type 2", probably to
  allow the heli spike-belt drop ("HELICOPTER SPIKE BELTS DROPPED"). The heli itself comes from the `copheli` entry in
  `cops` (count 1, chance %). It stays `HeliFuelTime` s, with `TimeBetweenHeliActive` s between deployments.
* Spawns of `copcross`, `copsuv`, `copsuvl` and `copsporthench` are counted as special units (not regular cops) in `0x423df0`.

## 4. Pursuit state machine (AIPursuit, ctor 0x442d00, update 0x443ba0) [C]

AIPursuit registers two sim tasks named "AIPursuit": rate 0.25 (every 4th sim tick, the main update `0x443ba0`) and
rate 1.0 (every tick). Rate is a fraction of sim ticks (`0x6ed0f0` clamps it to <= 1). At 60 Hz sim **[I]** the main
update runs at 15 Hz. `state` (`AIPursuit+0x260`, seen as `esi+0x258` with `esi = this+8`):

```
0 ACTIVE      1 BACKUP_REQUESTED (timer = BackupCallTimer, then spawn backup, back to 0)
2 COOLDOWN    3 BUSTED      4 EVADED
```

Per main update (dt = main-task dt):
1. `pursuitTime += dt`. HUD `pursuit_length` updates every whole second (milestone `pursuit_length`).
2. **Heat** (perp `+0x1c`, float):
   `heat += dt / (TimePerHeatLevel * T[idx])`, where `T` = field `80DEB840[15]`, `idx` = byte
   `[[0x91CF90]+0x10]+0xB0` clamped to 0..14 (14 if `0x404ac0()`; plain TimePerHeatLevel if that profile is missing).
   `idx` is the career progress (most likely Blacklist rivals beaten) **[I]**. Then `heat = clamp(heat, min, max)`.
   When `floor(heat)` changes, the heat table row is reloaded (and with it all numbers above).
   Effect: early career, 120 s per heat level; at the end of the career, 600 s at heat 1–4. Heat 5→6 always takes 600 s.
3. **Bounty tick**: whenever `floor(pursuitTime/10)` changes, call `perp->AddPendingBounty(row.1E2A1051)`
   (IPerpetrator vtbl+0x3C, `0x409480`, adds to perp+0x24, only in free roam or race type 8).
4. **Engagement / backup**: for each live cop car compute the planar distance `d` and the height difference `dh`.
   `nearestAny = min d`, `nearestLevel = min d over cops with |dh| < 1.5 m`. Cops with
   `d < FullEngagementRadius` (150 if no row) are counted as engaged. The cop request counter rises by engaged minus
   deficit (at least +1). When the deficit is <= NumCopsToTriggerBackup and state == 0, state = 1 with
   timer = BackupCallTimer. On expiry the AIPursuit spawns backup (vfunc +0x138) and the state goes back to 0.
5. **Line of sight**: every cop keeps its own "time since it saw the suspect". `lostTime = min over cops` (+dt each
   update). Being within 60 m of an active roadblock counts as seen (`seen = true; lostTime = 0`).
   `seen = lostTime < 7.0`. The LOS ranges are frontLOSdistance (ahead of the cop), rearLOSdistance and
   heliLOSdistance **[I by name]**. speechtune `TimeConsideredLostNoLOS` = 3 is for speech only.
6. **Formation** (section 6).
7. **Busted meter** (section 5).
8. **Evade / cooldown** (section 7).

## 5. Busted rule [C 0x443ba0 @0x444400..0x4447a0]

```
bustMps = BustSpeed * (1/3.6)                    // BustSpeed is km/h: h1 2.5 m/s .. h5 6.9 m/s
if busted:                 rate = +0.25*dt
elif seen && !evaded:
    if playerDisabled:     rate = (nearestLevel < 90) ? +1.0*dt : -0.5*dt      // [?] 'disabled' = perp sub-object state==1
    elif speed < bustMps && nearestLevel < 15: rate = +0.25*dt
    else:                  rate = -0.5*dt
else:                      rate = -0.5*dt
if game paused/NIS:        rate = 0
// every sim tick (rate-1.0 task):  meter = max(0, meter + rate)
if meter > 5 and not busted -> BUSTED
```
At 60 Hz with a 15 Hz main update this is +1.0 meter/s while stopped next to a cop (bust after about 5 s), and
-2.0/s when you move away. In the disabled case it is +4/s within 90 m (about 1.25 s). On BUSTED:
`busted = true`, state 3, every cop gets `AIGoalStopShort`, and race opponents get `AIRacerBusted` events.
The busted overlay is `BUSTED_OVERLAY.fng` / `BustedOverlayScreen`.

HUD pursuit-meter value (`+0x150`, -1..+0.5) **[C]**. It is display only:
* `lostTime > 7`: -1 (cooldown bar takes over).
* `0.29 < lostTime <= 7`: `clamp(-0.5 - lostTime/14, -1, -0.5)`. The evade bar fills over 7 s.
* Seen and `nearestAny > EvadeDist(50)`: `clamp(-0.1 - 0.4*(d-50)/(frontLOS-50), -0.5, -0.1)`.
* `nearestAny <= BustedDist(35)` and speed < 31.29 m/s (70 mph):
  `0.1 + 0.4*(0.7*clamp((27.78-speed)/(27.78-bustMps),0,1) + 0.3*clamp((35-d)/(35-15),0,1))`.
* Otherwise 0.

## 6. Cop AI tactics [C]

Formation choice: done when the formation timer <= 0, there are cops, the suspect is not busted or evaded, and
15 s have passed since a timestamp (`[perp+0x118]`, probably the pursuit start or the last reset **[?]**).
* If a roadblock is active and not yet passed: type 4, with duration StaggerFormationTime.
* Else pick from `CopFormations` by weight; next re-evaluation after `duration + rand(0..3)` s.
* Default when none is chosen: type 7.

Formation types (`0x440330` switch; slot offsets in metres, x = lateral, z = along heading, negative = behind):

| type | ctor | meaning | slots |
|---|---|---|---|
| 1 | 0x43fe90 | PIT (AIGoalPit) | (+4,−2.7) approach from (−10,−2.7); (−4,−2.7) from (+10,−2.7) |
| 2 | 0x43f1a0 | BOX-IN (uses BoxinTightness/BoxinDuration) | built from the player's surroundings |
| 3 | 0x43f520 | ROLLING ROADBLOCK (RollingBlockTightness/Duration) | ahead of the player |
| 4, 5 | 0x43f770 | follow/herd pack behind | (0,−13); (±3.5,−13); (0,−17); (±3.5,−17) |
| 6 | 0x440050 | side line (3 cars on the left) | (−3,0), (−3,+5), (−3,−5) |
| 7 | 0x43fb00 | front-and-back sandwich | (0,−13), (0,+13), (+3.5,−13), (−3.5,+13), (−3.5,−13), (+3.5,+13) |

Other AI goals in the exe: AIGoalRam, AIGoalHeadOnRam (Rhino), AIGoalStaticRoadBlock, AIGoalHeliPursuit,
AIGoalHeliRoadBlock, AIGoalPatrol, AIGoalStopShort, AIGoalPullOver. Collapse* fields (CollapseSpeed mph?,
Inner/OuterRadius, MaxCopsCollapsing) drive cops converging on a slow or stopped suspect (`0x443300` reads CollapseSpeed)
**[?]**. Cop car stats (`aivehicle`) **[D]**:

| type | TopSpeedMult | AccelMult | MAX_AI_SPEED | bounty for destroying |
|---|---|---|---|---|
| copmidsize | 1.6 | 1.05 | 280 | 250 |
| copghost | 1.15 | 1.09 | 310 | 500 |
| copgto | 1.2 | 1.1 | 325 | 2,500 |
| copgtoghost | 1.25 | 1.15 | 350 | 5,000 |
| copsporthench | 1.3 | 1.2 | 400 | 20,000 |
| copsportghost | 1.5 | 1.25 | 400 | 25,000 |
| copsport | 2.0 | 1.5 | 400 | 20,000 (h10 100,000) |
| copsuvpatrol | 0.9 | 1.05 | 250 | 25,000 |
| copsuv (Rhino) | 0.5 | – | 50 | 15,000 |
| copsuvl | 0.5 | – | 50 | 10,000 |
| copcross | 2.0 | 1.5 | 400 | 100,000 |

`RepPointsForDestroying` is Int32[10] indexed by heat. All heats are equal except copsport.

## 7. Evading, cooldown and hiding spots [C 0x444800..0x444cd3]

```
hideBonus (+0x164): if !seen && perp in hiding zone(s): hideBonus += HiddenZoneTimeMultiplier*dt (3*dt if no row)
                    if seen: hideBonus = 0; lastSeenPos = player pos
elapsed = lostTime + hideBonus
if lostTime > 7:                       // evade bar full
    progress = elapsed / evadetimeout
    if !cooldown && timeInState > 2.5: enter COOLDOWN (state 2, cooldown=1, reset timeInState, spawn timer =
                                       max(timer, TimeBetweenCopSpawn), music cue 0x40)
    if cooldown: progress <  0.05 -> shown as 0.05
                 progress >= 1    -> EVADED (state 4)
else if cooldown:                      // re-spotted during cooldown
    shown progress *= 0.93 per update (floor 0.05)
    if timeInState > 2.5: leave cooldown (state 0)
remainingCooldown (+0x140) = max(0, evadetimeout - elapsed)
```
* Cooldown starts after 7 s without LOS. It ends in EVADED after `evadetimeout` seconds without LOS: 20/45/75/90/120/120 s
  for heat 1–6.
* Hiding spot: the cooldown clock runs `1 + HiddenZoneTimeMultiplier` times faster (h1 5x … h5 9x). The spot must be
  entered unseen (Mia SMS / heli tip: "make sure they don't see you drivin' into the spot"). `TimeToHideInZone`
  (1 s, 3 s for heat 6+) is never read: the latch is a fixed 0.05 s, and a spot entered in sight never latches
  **[C, PC 0x4293D0]**. Zones: the 34 TRACK_PATH_ZONE_HIDDEN track path zones in L2RA (ai_cops.md 9.1, §12).
* Re-entering cooldown or leaving it needs 2.5 s of hysteresis.
* While in cooldown the AIPursuit uses search mode: NumPatrolCars caps the cops, searching at SearchModeCityMPH /
  SearchModeHwyMPH. SearchModeRoadblockChance places roadblocks within SearchModeRoadblockRadius and
  SearchModeHeliSpawnChance spawns a search heli **[I by name]**.
* Safehouse: it can be entered only in cooldown (world_interaction.md). Entering it ends the pursuit as evaded **[I]**.
* After a pursuit ends (`0x43a200`), new pursuits and 911 reactions wait `TimeInactiveFor911` = 120 s in free roam
  (60 s default, 0 in race type 8) **[C]**.

## 8. Spawning [C 0x424110, 0x423df0, 0x43dd30]

* Cop budget `N`:
  * Patrol (not in a pursuit): 3 (2 when `[0x92C67C]->vf28` is set).
  * Cooldown/search: `min(that, NumPatrolCars)`.
  * Active pursuit: `max(0, copsRequested - copsPresent)`.
* `cops` CopCountRecord = `{StringKey type (16 B), int count, int chance}`. Each non-heli type gets
  `round(count * N / Σcount)` of the budget. The heli entry is copied as is (1 heli, `chance` % to deploy **[I]**).
  `chance` for ground types (50–100) is probably the per-spawn probability **[?]**.
* Delay before the next cop joins: `TimeBetweenFirstFourSpawn` while fewer than 3 cops have joined (and not in
  cooldown), then `TimeBetweenCopSpawn`. Once more than 2 spawn requests are queued, every other one is 0.2 s
  (cars arrive in pairs). Each join increments `total_cops_in_pursuit`.
* Patrol cops in free roam: up to 3 roaming cars **[I]**. The density is gameplay `CopDensity` (100) / TrafficLevel **[?]**.
* **Roadblocks** (`0x43dd30`):
  * The road segment is searched 250 m ahead along the suspect's route and must be at least 40 m long.
  * Car count = `{3,3,4,4,5,5,6}[min(laneIndex,6)]` from the road width.
  * Spikes are added when `rand(100) < roadblockspikechance`.
  * The layout template holds up to 6 elements: 1 = cop car, 2 = `XO_Sawhorse_1b_00`, 3 = `XO_SpikeBelt_1b_DW_00`.
    It is scaled ×1..1.14 to the road width.
  * Roadblock cars use `aivehicle` collision reactions `roadblock` and the "AIRoadBlock" goal.
  * The chance to request a roadblock is `roadblockprobability` % per request **[I]**; the request cadence is **[?]**.
  * Passing a roadblock increments `roadblocks_dodged`, and spikes increment `tire_spikes_dodged`.

## 9. Bounty, combo, cost to state

* Pursuit bounty is held in two pending pools on the perp:
  * `+0x24`: the periodic heat bounty from section 4.3.
  * `+0x28`: cop takedowns (IPerpetrator vtbl+0x38, `0x4094C0`).
* **Cop destroyed** (`0x418f30`):
  * `pts = RepPointsForDestroying[heatIdx]`.
  * Combo: if the combo timer > 0 when the next cop is destroyed, the multiplier goes up by 1 (max 3).
    Otherwise it resets to 1.
  * The timer is set to `DestroyCopBonusTime` (10 s in heat rows).
  * Award = `pts * multiplier`. The HUD shows "<car name>^Combo Bounty: %d".
  * Milestone `cops_destroyed_in_pursuit++`.
* **End of pursuit** (`0x411240`):
  * `bounty = pending24 + pending28`; both pools are then cleared.
  * Infractions mask = `[*0x91E014]`, infraction count = popcount.
  * The result goes to the results screen `0x58fd00` and the car record `0x56f7c0(mask, bounty, evaded)`:
    * Always: `unpaid[i] += bit i` (at most one ticket per infraction type per pursuit).
    * Evaded: `car.bounty += bounty`, `car.evaded++`.
    * Busted: `car.busted++`, and the pursuit bounty is lost.
  * Career bounty is credited through IPerpetrator `+0x18` (`0x409410`, `perp+0x20`) **[I]**.
* **Cost to state**:
  * Props: `smackable.COST_TO_STATE` per object: default 100, cardboard 25, parked_car 200, glass 200, tollbooth 200,
    gas station roof 3000, water tower support 2000.
  * Cop cars: the `aivehicle.CostToStateForDestroying` field is not set in data and is never referenced in code.
    The cop-damage CTS rule is **[?]**.
  * Milestone `cost_to_state_in_pursuit` resets at pursuit start.
* **911 calls** (speech side `0x71d080`): when `CTS >= CTSFor911` or civilian hits >= `NumCiviHitsFor911`, dispatch
  plays a 911 report. Lifetime911 is the report lifetime. Whether a 911 call spawns cops or starts a pursuit is **[?]**.

## 10. Infractions and fines [C 0x56f410, 0x581cb0; D infractions]

| bit | index | collection | fine $ | ticket label |
|---|---|---|---|---|
| 0x01 | 0 | speeding | 150 | Speeding |
| 0x02 | 1 | racing | 350 | (street racing) |
| 0x04 | 2 | reckless_driving | 1000 | Reckless Driving |
| 0x08 | 3 | assault | 350 | Ramming a Police Vehicle |
| 0x10 | 4 | hit_and_run | 300 | Hit & Run |
| 0x20 | 5 | damage | 100 | Damage to Property |
| 0x40 | 6 | resisting_arrest | 300 | Resisting Arrest |
| 0x80 | 7 | off_road | 75 | Driving off Roadway |

* Fines due = `Σ unpaid[i] * amount[i]` (`0x581cb0`). The unpaid list is per car at record `+0x18` (u16[8]).
  Paid/served tickets are at `+0x28`. `0x56f890` moves unpaid to served (pay fines).
* `0x56f840` undoes one pursuit's tickets and `busted--`: the "out_of_jail_free" marker ("Get out of jail for free
  next time you get busted").
* Post-pursuit infractions screen (`0x58cef0`) shows fines due, this pursuit's new tickets and the marker count
  (inventory type 0x11).
* **Detection** of each infraction is **not found [?]**. Infractions reach the tracker through the data-driven event
  `EReportInfraction` (ctor `0x624770`, factory `0x630400`, registered in the .data table at `0x8F6114`). Natural
  triggers by name: speeding (seen above a speed limit), racing (seen while in an event), reckless (near misses or
  wrong way), assault (hitting a cop car), hit & run (hitting traffic), damage (smackables), resisting arrest
  (failing to pull over once the pursuit starts), off-road.

## 11. Pursuit start [mostly ?]

* Patrol cops (AIGoalPatrol) switch to pursuit when they have LOS to the suspect (front 151 m / rear 51 m default,
  151 m from heat 1) and the suspect commits an infraction, or rams them **[I]**. speechtune `PursuitInitDelay` = 2 s
  is the delay before the "Setup_InitPursuit" call.
* Scripted starts:
  * `ForcePursuitStart` / `MForcePursuitStart`.
  * `pursuit_zone` (PursuitLevel; no instances).
  * Challenge Series `ForceHeatLevel` + `CopSpawnPoints`.
  * `pursuit_mini_game` (post-race pursuits, ForceHeatLevel 1 or 5).
  * Milestone jump from the Blacklist pages.
* Initial heat = the car's stored heat (or ForceHeatLevel).

## 12. Heat persistence, impound and results

* Heat is stored per car; free roam starts at that car's heat **[I]**. "Heat will drop on inactive cars in your safe
  house" **[D text]**. Rate **[?]**.
* EvadeSuccessHeatAdjust 0.9, EventWinHeatAdjust 0.9 and MilestoneCompleteHeatAdjust 1.0 are heat multipliers applied
  on evade, event win and milestone completion **[I by name]**. No code reference was found.
* **Impound** **[D text]**:
  * Each bust gives the car one impound strike; 3 strikes impound it.
  * The "add Extra Impound Strike" marker raises the limit by 1, up to 5.
  * "Evading multiple pursuits … will decrease impound strikes" (count **[?]**).
  * An impounded car needs a release fee or a "release_car_from_impound" marker. "You will receive an SMS message when
    your car can be released".
  * All cars impounded with no cash = game over.
  * Car record fields: `+0x10` bounty, `+0x14` evaded count, `+0x16` busted count, `+0x18` unpaid[8], `+0x28` paid[8].
    Whether the strike counter is `+0x16` is **[?]**.
* Busted: fines due are shown and paid **[I]**. The car gets a strike and the pursuit bounty is lost.
* Results screens:
  * `PostRace_Pursuit.fng` / `PostRacePursuitScreen`: pursuit length, bounty, cops involved/damaged/immobilized,
    roadblocks/spikes dodged, helicopters deployed, CTS, infractions.
  * `PostPursuitInfractionsScreen`.
  * Milestones with `ResetWhenPursuitStarts` reset at pursuit start: bounty_in_pursuit, cops_damaged,
    cops_destroyed_in_pursuit, cost_to_state_in_pursuit, pursuit_evasion_time (type 2, smaller is better),
    pursuit_length (type 3), roadblocks_dodged, tire_spikes_dodged, total_infractions.

## 13. Deviations in the current Unity code (Systems.cs PursuitSystem, Pursuit.cs)

**Superseded**: the police were rewritten in `Runtime/Game/Police/*`; the current state is ai_cops.md §9. The table
below describes the first-round code.

| Unity now | Game |
|---|---|
| Heat++ every 75·heat s, cap 5 | Continuous `heat += dt/(TimePerHeatLevel·T[careerIdx])` (120–600 s per level), clamp to max heat; reload row on change |
| Pursuit when speed > 33 m/s within 55 m of a cop, or touching one | LOS (front/rear distances) + infraction / ram **[?]** |
| Busted: speed < 3 m/s, nearest < 14 m, for 5 s; decay 2× | speed < BustSpeed km/h (2.5–6.9 m/s), nearest level cop < 15 m, cops saw you < 7 s ago; +1/s, −2/s, bust at 5 |
| Evade: nearest > 200 m for 8+4·heat s, no cooldown state | 7 s without LOS → COOLDOWN; evadetimeout (20/45/75/90/120) s more unseen → EVADED; hiding spots ×(1+mult); re-spot 2.5 s → active |
| Bounty 25·heat/s; 1500·heat per disabled cop | per 10 s: 100/500/1000/5000/25000; takedown RepPointsForDestroying × combo 1–3 (10 s window); pending, paid only on evade |
| Cop models random by heat (COPSPORT/GTO/SUV/MIDSIZE) | `cops` table per heat (midsize → ghost → gto → gtoghost → sporthench → sportghost+suvpatrol), heli from heat 4 |
| Wanted cops = 2 + heat, spawn every 4 s | Budget distribution, TimeBetweenFirstFourSpawn then TimeBetweenCopSpawn |
| Roadblocks from heat 2 every (50−4·heat) s, spikes from heat 3 | roadblockprobability 0/10/25/30/40 %, spikes 70–85 % from heat 4; 250 m ahead; 3–6 cars by width |
| Fine on bust 500·heat | Σ tickets × {150,350,1000,350,300,100,300,75}; one ticket per type per pursuit; impound strikes |
| Infractions added freely by impulse/speed; CTS = 350/150/80·impulse | Bitmask; CTS from smackable COST_TO_STATE; cop CTS **[?]** |
| Car heat on evade = max(old, heat), 0 on bust | Heat kept per car; adjust factors 0.9; decays in safehouse **[?]** |
| No formations (cops path straight to player) | PIT / box-in / rolling block / herd / side / sandwich formations from CopFormations weights |
| No heavy/Cross/heli support | pursuitsupport tables (Rhino, Cross from heat 5, heli from heat 4) |

## 14. Open items

1. Infraction detection thresholds (speeding limit, reckless rule, off-road) and the exact pursuit-start trigger.
   Next step: find users of the `0x8F6114` event table and the cop AI patrol goal.
2. Free-roam max heat source (`perp+0xE0`) and the per-car heat save/decay. `idx` for `80DEB840` is assumed to be the
   rivals beaten.
3. The meaning of HeavySupport types 3/4, the 4th int, and AirSupport type 0/1/2. Ground `chance` in CopCountRecord.
4. Impound strike counter location, the evade-reduces-strikes rule, and release fee formula.
5. Cost to state for cop damage/destruction and for roadblock cars.
6. The 'disabled' bust branch (perp sub-object state 1) and the 15 s formation-suppression timestamp.
7. Sim rate is assumed to be 60 Hz (busted timing derives from it).
