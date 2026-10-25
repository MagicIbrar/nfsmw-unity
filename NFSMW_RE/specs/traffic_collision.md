# Traffic AI and world collision (WCollisionPack)

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/` (paths below are relative to it), attribute data from
`GLOBAL/attributes.bin`, track data from `TRACKS/L2RA.BUN` + `TRACKS/STREAML2RA.BUN` (verified with Python scripts in
`<scratch>\`: `parse.py`, `verify.py`, `groups.py`, `zones.py`,
`dumptraffic.py`, `surf.py`, `roads.py`). `[?]` = uncertain or inferred. Speeds in the attributes are mph
(`MPH2MPS` = x 0.44704). Sim/physics space ("bond" axes) = (-gameY, gameZ, gameX), Y up (`bWare/Inc/bMath.hpp:1014`
bConvertToBond). The project's Unity space is (gameX, gameZ, gameY), so **Unity = (sim.z, sim.y, -sim.x)**. That
is a pure rotation, so sim triangle winding carries over to Unity unchanged.

---------------------------------------------------------------------------------------------------------------------

# Part A: Traffic AI

## A.1 Components

| Piece | File | Role |
|---|---|---|
| `AITrafficManager` (Sim activity) | AI/Activities/AITrafficManager.cpp | picks the pattern, density, spawns, despawns. Task every **0.5 s** (`:180`) |
| `AIVehicleTraffic` (behavior) | AI/Common/AIVehicleTraffic.cpp | per-car AI, updated every **0.1 s**, staggered across cars (`:29,:38-46`) |
| `AIGoalTraffic` | AI/Common/AIGoal.cpp:98-113 | a single action, `AIActionTraffic`. Patrol cops use the same action plus `AIActionTooDamaged` (`:116-132`) |
| `AIActionTraffic` | AI/Actions/AIActionTraffic.cpp | lane following, speed, reaction to accidents |
| `WRoadNav` (kTypeTraffic, kLaneTraffic) | World/Common/WRoadNetwork.cpp | lane splines, junction choice, cookie trail, avoidance ("occlusion") |
| `AIAvoidable` / `AvoidableManager` | AI/AIAvoidable.h, AI/Activities/AvoidableManager.cpp | SAP grid of every AIVehicle, the player included (`AIVehicleHuman`, AIVehicle.cpp:120). Updated every **0.25 s** (`:30`) |
| `CARSFX_TrafficHorn` | EAXSound/CARSFX/CARSFX_TrafficFX.cpp | traffic horns (sound only) |
| `trafficpattern` attrib class | Generated/AttribSys/Classes/trafficpattern.h, Misc/MWAttribUserTypes.h:114-119 | the car mix |

## A.2 Which cars spawn: `trafficpattern`

Fields: `CollectionName` (Text), `SpawnTime` (f32 s), `SpeedStreet` (f32 mph), `SpeedHighway` (f32 mph), and
`Vehicles[]` of `TrafficPatternRecord` (0x18 bytes): `{RefSpec Vehicle (0xC, collection key at +4), f32 Rate,
u32 MaxInstances, u32 Percent}`. Only the first **10** records are used (`MAX_PATTERN_TYPES`, `:31`, `:288`).

Data from attributes.bin (every pattern has SpawnTime 4, SpeedStreet 35, SpeedHighway 55, inherited from `default`):

| pattern | vehicles (rate s / MaxInstances / Percent) |
|---|---|
| default | trafha 3/0/40, trafpickupa 3/0/40, trafnews 10/1/0, traftaxi 6/0/20 |
| drag, industrial | no own entries: they inherit default's list (so NFSMW_RE/specs/races.md §1.3 is wrong to say "drag has no vehicles") |
| cityhighway | semib 30/1/10, semicrate 30/1/10, trafcourt 10/0/20, traf4dsedc 6/0/20, trafdmptr 30/1/10, trafnews 20/1/20 |
| coastalnorth | trafstwag 3/0/30, trafpickupa 3/0/30, trafcemtr 60/1/10, trafficcoup 6/0/20, traftaxi 15/1/10 |
| coastalsouth | traftaxi 6/0/25, trafficcoup 3/0/20, trafgarb 60/1/10, trafpizza 6/0/20, trafpickupa 20/1/10, traf4dseda 6/0/25 |
| collegehighway | semilog 30/1/10, semicon 30/1/10, trafminivan 10/1/20, traf4dseda 6/0/20, trafpickupa 3/0/10, trafficcoup 3/0/20 |
| collegenorth | traftaxi 3/0/40, trafha 3/0/40, trafvanb 10/1/0, trafstwag 6/0/20, trafminivan 3/0/0 |
| collegesouth | trafminivan 3/0/30, trafpizza 3/0/30, trafficcoup 6/0/30, trafnews 3/0/10, trafstwag 6/0/30 |
| downtown | traftaxi 3/0/25, trafgarb 40/1/10, trafnews 15/1/10, traffire 60/1/10, traf4dseda 6/0/25, trafcourt 6/0/20 |

The vehicle keys are `pvehicle` collections. Traffic-class collections in the data: traf4dseda/b/c, trafamb, trafcamper,
trafcemtr, trafcourt, trafdmptr, trafficcoup, traffire, trafgarb, trafha, trafminivan, trafnews, trafpickupa,
trafpizza, trafstwag, trafsuva, traftaxi, trafvanb, and semi/semia/semib/semicmt/semicon/semicrate/semilog
(articulated: a tractor plus a trailer, all using the `CARS\SEMI` model [?]). trafamb, trafcamper, traf4dsedb and trafsuva
are in no pattern, so they only appear through scripts.

**Pattern selection** (`ChoosePattern`, `:536-582`), re-run on every 0.5 s tick:
1. If racing (`kPlayMode_Racing`) and the race has a `TrafficPattern`, use it.
2. Otherwise, average the positions of all traffic centres (`ITrafficCenter`, the camera/player view [?]), convert
   to the 2D track point `(sim.z, -sim.x)` = (gameX, gameY), and find a `TRACK_PATH_ZONE_TRAFFIC_PATTERN` (type 9) zone
   that contains it. The zone's `Data[0]` is `bStringHash(pattern name)`, matched against `bStringHash(CollectionName)`
   (`:194`).
3. If no zone matches, the current pattern is kept. The start pattern is `default` (`:178`).

Zones come from L2RA.BUN chunk 0x80034147 → 0x3414A, a list of `TrackPathZone` records (World/TrackPath.hpp:68-84,
variable size `MemoryImageSize` at +0x42; Type +0, BBox +0x20, Data +0x30, NumPoints +0x40, points +0x44). There are
11 type-9 zones. Their bboxes in game X/Y:
collegesouth [-173,1875..1968,3061], downtown [421,-655..2447,785], collegenorth [7,2970..2798,4720],
collegehighway x4, cityhighway x2, coastalsouth [1207,-499..5186,2176], coastalnorth [1009,1271..4587,4016].
`industrial` and `drag` are not referenced by any zone.

Changing pattern (`SetTrafficPattern`, `:422-434`) re-seeds each record's timer to `Rate * rand(0..1)`.

## A.3 Density

`ComputeDensity` (`:625-653`) returns d in [0,1]:
* 0 while an NIS runs or `ICopMgr::IsCopRequestPending()` (cops are being loaded).
* Racing: `race.TrafficDensity / 100`, where `TrafficLevel` comes from the race (see races.md §6: QR None/Min/Mod/Max =
  0/10/30/50, career per event, `ForceTrafficDensity`).
* Free roam (`kPlayMode_Roaming`): **1.0**. Any other play mode: 0.
* x **0.75** while any pursuit exists (`IPursuit::Count() != 0`).

Density drives three 11-entry tables over d in [0,1] (linear interpolation, `Misc/Table.cpp:5-20`; `:34-48`):

| d | 0 | .1 | .2 | .3 | .4 | .5 | .6 | .7 | .8 | .9 | 1 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| spawn-rate multiplier | 0 | .05 | .1 | .125 | .2 | .4 | .6 | 1 | 3 | 5 | 8 |
| off-screen despawn distance (m) | 130 | 120 | 110 | 100 | 90 | 80 | 70 | 60 | 55 | 45 | 40 |
| off-screen despawn time (s) | 12 | 10 | 9 | 8 | 7 | 6.5 | 6 | 5.5 | 5 | 4.5 | 4 |

Heat has no direct effect on density; the only pursuit effect is the x0.75. `mTrafficMin/MaxSpawnDist` 225/300
(`:160-161`) are never used.

## A.4 Spawning

Every tick (`Update`, `:655-674`): if d > 0 and a pattern is valid, every record timer += `dT * multiplier(d)`,
`mNewInstanceTimer += dT`, then at most **one** `SpawnTraffic()` call (`:336-391`):
1. `NeedsTraffic` (`:393-403`): the count of **all** active vehicles (player, racers, cops, traffic) must be < **10**.
2. `FindSpawnPoint` (`:489-534`). For each traffic centre, alternating the sort order each call:
   * dir = the centre's forward vector, yawed by a random ±0.125 turn (±45°).
   * distance = **200 + rand(±50) + max(0, camera speed along dir)** m (so 150–250 m ahead, plus 1 s of travel).
   * oncoming chance = lerp(1.0, 0.5, ramp(camera speed, 0, 50 m/s)). If chosen, the car faces back towards the
     camera (at a standstill, traffic always comes towards you).
   * `nav.InitAtPoint` must be valid, and `WRoadNav::CanTrafficSpawn` (WRoadNetwork.cpp:3210-3240) requires: not a
     decision/intersection segment, no `kRoadSegmentNoTraffic` flag, not the wrong end of a one-way segment, and ≥1
     traffic lane. The lane is a **random** traffic lane (`:3240`).
   * `CheckRace` (`:470-487`): while racing with a finish line, the segment must be on the race route (`IsInRace`),
     so race traffic only spawns on the route.
   * `FindCollisions` (`:436-468`): rejected if there is no ground under the point (`GetWorldHeightAtPointRigorous`,
     which uses the collision packs), if it is within **150 m** (xz) of any traffic centre, or within **20 m** of any
     active vehicle.
3. `NextSpawn` (`:283-304`): round-robin over the records. A record is eligible when its timer > `Rate` (and Rate > 0),
   its active count < `MaxInstances` (0 = unlimited), and, if `Percent` ≠ 0, its count < max(1, budget * Percent / 100)
   with budget = 10 − non-traffic vehicles (integer maths). So a record spawns at most once per `Rate / multiplier`
   seconds; at d = 1, a Rate 3 car comes every 0.375 s, limited by the 0.5 s tick.
4. `GetAvailableTrafficVehicle` (`:306-334`) reuses an inactive pooled car of that type. A new instance (model load) is
   only allowed when `mNewInstanceTimer > SpawnTime` (4 s), and the timer resets to 0 after a new one.
5. The car is placed with `ResetVehicleToRoadNav` (on the ground at the nav position), then `SetSpawned` (damage
   reset) and `StartDriving(min(SpeedStreet, SpeedHighway) * 0.75 mph)` = 26.25 mph = **11.7 m/s** initial speed. An
   `MSetTrafficSpeed(SpeedStreet, SpeedHighway)` message is posted, and that record's timer resets to 0.

## A.5 Despawn

`ValidateVehicle` (`:584-623`), run each tick for every active traffic car:
* `IsOffWorld()` → unspawn.
* `OffscreenTime > time(d)` **and** distance to the camera > `dist(d)` → unspawn, **unless** any model part is still
  `InView()` (PartChecker, `:52-68`). Articulated trucks stay while their trailer is still valid.
* Unspawn = `Deactivate()` and return to the pool (`AIVehicle::UnSpawn`, AIVehicle.cpp:1181-1198). There is no distance
  cap otherwise: a car you can see is never removed.
* `FlushAllTraffic(release)` (`:676-700`) kills or unspawns everything (used on mode changes [?]).

## A.6 Driving: lanes, junctions, speed

* **Lane following**: the car steers at a look-ahead point on its lane spline. Look-ahead = lerp(10, 30, ramp(speed,
  0, 25)) + speed·dT + body radius, or 30 m (+...) when occluded (`AIActionTraffic::Update`, `:302-313`;
  `UpdateNavPos`, `:253-296`). Steering is `asin(cross(fwd, dirToTarget).y) / maxSteer`, clamped to ±1
  (AIVehicle.cpp:841-895).
* **Lanes**: traffic stays in its lane index. At a junction (`GetNextTraffic`, WRoadNetwork.cpp:~1195-1330) it picks a
  **random** traffic-allowed departing segment. A segment entered from the "right-most traffic entrance" maps the lane
  counted from the kerb, and candidates that would need a far lane change (`LastResort`) are skipped when possible.
  One-way segments entered the wrong way are excluded. At a dead end (`HitDeadEnd`) the speed becomes 0 (`:208-210`).
* **No voluntary lane changes or overtaking.** The only lateral moves are avoidance offsets inside the cookie trail
  (A.7). `ChangeLanes` is only used for snapping and decisions.
* **Speed** (`ComputeSpeed`, `:205-251`): posted = SpeedHighway if the segment has ≥ 4 traffic lanes, else SpeedStreet
  (defaults 55 / 35 mph; constructor `:90-91`, set by message `:393-399`). Then
  `min(posted, sqrt(μ·9.8 / |curvature|))` with μ = **0.6** (1.6 for cops; AIActionRace.cpp:466-473), and an
  acceleration cap of desired ≤ current + 2·dT (≈2 m/s²). `mFixedSpeed` (from scripted `MSetTrafficSpeed`) skips
  the curvature and occlusion terms.
* **Gas/brake** (AIVehicle.cpp:897-967): desired < 0.5 → full brake; desired < current by > 2.5 m/s, or desired < 5 →
  full brake; otherwise full gas. These are bang-bang inputs into the `SuspensionTraffic` vehicle model [?]. A traffic
  car "in shock" (just hit) shifts to neutral and gets no input.

## A.7 Yielding and avoidance (player included)

* Every active AIVehicle is an avoidable with a sweep radius of max(lookAhead, 2·speed) (AIVehicle.cpp:581-591).
  The default radius is 20 m (`:50`). The neighbour lists come from the SAP grid every 0.25 s.
* `WRoadNav::HolePunchAvoidables` (WRoadNetwork.cpp:~1715-1915) works on the cookie trail (road cross-sections ahead).
  Obstacles more than ±5 m in elevation are ignored. For traffic, an obstacle **ahead in the lane counts as blocking**
  when it is within `my_extent + his_extent + 0.5·speed + 2·my_extent` and lateral overlap < extent + 1 m. Otherwise
  it counts only if the time to closest approach is < 3 s. The trail is then "cut" to pass left or right if the gap
  to the road edge > half width + margin. If neither side fits, the car follows the obstacle.
* `UpdateOccludedPosition` (`:1917-2170`) gives the apex position and `fOccludingTrailSpeed` (the obstacle's speed along
  the lane). `ComputeSpeed` then brakes with a stopping-distance table, 3 m at 0 → 50 m at 80 m/s, indexed by speed
  × max(mass·0.0005, 1), with mass doubled for tractors:
  `desired = clamp(occluderSpeed · dist / stopDist, 0, desired)`, never above the current speed (`:226-243`).
  Occlusion from behind is ignored.
* Result: traffic **slows to the player's speed behind them, stops when the player blocks the lane, and swerves inside
  the road** if there is room. It never leaves the road, and there is no deliberate dodge of an oncoming head-on
  beyond this.
* Pull-over is stubbed (`ShouldPullOver` returns false, `:298-300`). `mStopSign` / `mClearIntersection` exist but are
  unused. **There are no traffic lights and no stop signs.** Junction behaviour is only the random branch choice.

## A.8 Reaction to crashes and horns

* `OnCollision` (`:146-151`): an OBJECT collision involving a HUMAN, COP or RACER vehicle, within **80 m** of the
  camera, with that vehicle ≥ 5 mph (or the car not already in `ACCIDENT_OVER`) → `ACCIDENT_INPROGRESS` with a timer of
  3 (`:121-144`). Tractors go straight to `ACCIDENT_OVER`.
* INPROGRESS: gas 0, brake 0, **steering +1 (full)** for 3 AI updates. The timer decrements by 1 per update, about
  0.3 s (`:360-374`). Then OVER: **brake 1, steering +1, permanently**, so the car stays stopped until it despawns
  (`:375-384`). Traffic-traffic bumps are ignored.
* Horns (CARSFX_TrafficFX.cpp:8-24, 164-197, 282-317): checked per traffic car when the player's forward speed > 10
  [units ?] and the player is within **20 m**. The player must be within ±40° in front of the car's nose, or behind
  within 10 m (±115° in drag races); under 3 m always counts. The car honks with 30 % probability, or for sure if no
  traffic car has honked for 5 s. A honk lasts 1.2–4 s, at least 3 s apart per car. The sound comes from `pvehicle.HornType`.
* Damage/smash and "hit & run" heat are in pursuit.md / world_interaction.md.

## A.9 Pursuits and races

* Pursuit: density x0.75, and 0 while a cop spawn request is pending. Traffic is **not** cleared. Traffic keeps driving
  normally; there is no pull-over. The traffic vehicle cache answers `VCR_WANT` only for active cars, so the cop
  manager can evict pooled inactive traffic models (`:218-243`).
* `AIActionTraffic` is also the patrol action for cops. When a pursuit cop has lost sight it uses
  `pursuitlevels.SearchModeCityMPH/HwyMPH` (`:191-195`) and ends when the perp is in sight (`:153-160`).
* Races: density = TrafficLevel %, the pattern can be forced, traffic spawns only on race-route segments while racing,
  and `XO_TrackBarrierTraffic` scenery blocks side roads. Drag races add scripted `trafficspawntrigger` cars with
  fixed speeds (`MSetTrafficSpeed` fix flag). Free roam = full density.
* Off-screen "simple physics" (`UpdateSimplePhysics`, AIVehicle.cpp:1217) is only enabled for cops and racers, not
  for traffic.

## A.10 Unity deviations (`NFSMWUnity/Assets/NFSMW/Runtime/Game/Systems.cs` TrafficSystem, `Game/AIDriver.cs`)

| # | Game | Unity now |
|---|---|---|
| 1 | Car mix from the zone's `trafficpattern` (Rate / MaxInstances / Percent, semis, garbage, fire, cement, dump trucks) | uniform random from 14 fixed models, including TRAF4DSEDB and TRAFSUVA, which no pattern uses; no trucks or semis |
| 2 | Cap of 10 **total** active vehicles; one spawn per 0.5 s; a new model only every 4 s, otherwise reuse the pool | 10 traffic cars on top of cops/racers; spawn attempt every 0.6 s; Destroy/Instantiate every time |
| 3 | Spawns 150–250 m (+speed) **ahead** of the camera ±45°, oncoming-biased, ≥150 m from the camera, ≥20 m from cars, needs ground, not on intersections/no-traffic/wrong-way one-way, race route only when racing | random direction 90–260 m around the player; on-screen rejection only within 160 m; ignores road flags |
| 4 | Despawn when off-screen for 4–12 s **and** > 40–130 m from the camera (density-scaled), never while visible; OffWorld | Destroy beyond 340 m or when flipped |
| 5 | 35 mph street / 55 mph on ≥ 4-lane roads, μ = 0.6 curvature limit, ≈2 m/s² ramp, 11.7 m/s start speed | random 12–19 m/s, `CornerGrip` 0.7 |
| 6 | Lanes from road profiles (RNpf, kLaneTraffic); random lane at spawn; keeps lane through junctions; random branch; stops at a dead end | the graph centreline offset by a raycast kerb search (fallback 2.6 m); `Wander` ignores the NoTraffic (1,356 of 6,538 segments) and OneWay (104) flags |
| 7 | Cookie-trail avoidance: follows the occluder's speed with a stopping table, swerves inside road bounds, yields to the player | 3 forward raycasts, ±8°, brake/steer |
| 8 | Crash panic (full steer), then permanently stopped; horns as A.8 | none |
| 9 | Density: roam 1.0, race TrafficLevel %, pursuit x0.75, 0 during NIS/cop loading | constant |

Scripted hooks (round 4, Runtime/Game/AIDriver.cs): the career scripts' `Game.JackKnife` (0x605030, MJackKnife to the
character vehicle's AI) and `Game.SendCharacterStimulus` (0x60A6E0) reach the scripted characters' AIDriver through
`ScriptedCharacters.JackKnifeRequested` / `StimulusSent` (Gameplay/ScriptedWorld.cs). JackKnife = `AIActionJackKnife`
(AI/Actions/AIActionJackKnife.cpp: MessageJackKnife forces CanBeAttempted for a tractor with a trailer; Update: full gas, steer 0
for mAccelTime (0 when forced, 0.5 s after the proximity trigger), then handbrake + full steer and EMomentStrm "jacknife" once;
the trailer unhitches below 10 mph; never finishes). The proximity trigger (speed ≥ 50 mph, time to impact ≤ 4.25 s, angle ≤
25°) belongs to a goal no PC goal list contains, and no gameplay.bin script calls Game.JackKnife, so the action is dead in the
shipped data [C]. A stimulus goes to the vehicle's EventSequencer in the exe (no AIVehicle handler), so the driver only records
it (`AIDriver.LastStimulus`). The audio moment stream for the jack-knife is the audio agent's [hook].

Round 4 status (Runtime/Game/Traffic.cs): every row above is ported; `ComputeDensity` now also returns 0 while
`ICopMgr::IsCopRequestPending` (PursuitSystem.IsCopRequestPending: a latched roadblock, or a pursuit wanting a cop with the
budget free; AITrafficManager.cpp:627). AITrafficManager.cpp is fully ported apart from the vehicle cache (`OnQueryVehicleCache`
:218-243, the cop manager evicting pooled traffic models: cars are instantiated and destroyed here, no cache) and the debug
action queue. AIVehicleTraffic.cpp, AIActionTraffic.cpp (ShouldPullOver returns false in the decomp, so the pull-over states are
dead) and AvoidableManager.cpp (the 0.25 s avoidable update = TrafficContacts/RoadNavTraffic avoidables) are ported.
| 10 | Physics from the pvehicle attributes (traffic suspension model [?]) | `PlayerCar` with mass forced to 1800 kg |

---------------------------------------------------------------------------------------------------------------------

# Part B: World collision (WCollisionPack)

## B.1 Chunks and where they live

`Misc/SpeedChunks.hpp:328-331,408`; loader `Misc/ResourceLoader.cpp:747-761`; packs per section
`World/Common/WCollisionAssets.cpp:183-212`.

| id | name | where | count / size |
|---|---|---|---|
| **0x0003B801** | `BCHUNK_W_COLLISION_ASSETS` = WCollisionPack, one per drivable section | **STREAML2RA.BUN**, inside the streamed sections | **390 packs**, 5,802,128 bytes |
| 0x0003B800 | CARP_WGRID: the spatial grid (CGrd/CGcn) **plus the AI road network** (RNhd/RNnd/RNsg/RNpf/RNrd) | L2RA.BUN | 1 chunk, 551,432 bytes |
| 0x0003B802 / 0x3B803 | WGrid island data / CARP triggers | not present in L2RA/STREAML2RA | 0 |
| 0x00034191 | `BCHUNK_TRACK_OBJECT_BOUNDS`, TrackOBB[0x60] (World/Track.hpp:19-31) | L2RA.BUN | **1** OBB (hash 0xA4E74294, centre game (2090, 1405, 153.4), half-dims (11.55, 4.4, 0.05), identity rotation) |

The pack's section number (carp header) is the **drivable** section number, e.g. 101 = A01. The game indexes
`mCollisionPackList[section]` with it (max 2700). On disk, 312 packs sit inside the stream section numbered
`section + 40` (`ScenerySectionLODOffset`, World/VisibleSection.hpp:53-66; A01's pack is in stream section A41, which
also holds A01's scenery). The other 78 sit in a stream section with the same number (e.g. C10). **Load the pack
whenever its stream section loads.** Section numbers are in the header, so no mapping is needed.

## B.2 Chunk payload

```
bChunk header           u32 id = 0x3B801, u32 size
padding                 0x11 bytes up to 16-byte alignment (chunk->GetAlignedData(16))
bChunkCarpHeader (0x10) i32 mCrpSize        // bytes of CARP data that follow
                        i32 mSectionNumber  // drivable section (e.g. 101)
                        i32 mFlags          // bit0 kResolved (0 on disk)
                        u32 mLastAddress    // 0 on disk
CARP UGroup             mCrpSize bytes; all offsets are relative and little-endian
```
(bWare/Inc/bChunk.hpp:243-251, WCollisionPack.cpp:19-50.) Check: size − pad − 16 == mCrpSize for all 390 packs.

### UGroup / UData (Libs/Support/Utility/UGroup.hpp:31-124)
Both are 16-byte headers. Tags are a big-endian FourCC stored as a little-endian u32, so `'Arti'` = 0x41727469 is stored
as the bytes `69 74 72 41`. An indexed tag is `(type<<16) | index`.
```
UGroup  +0 u32 tag   +4 u32 bits: b0 indexed, b1 embedded, b2 allocated, b3 dataSorted, b4 groupSorted, b5..31 groupCount
        +8 u32 dataCount   +C u32 offset   // embedded: child array = this + offset*16
        child array = UGroup[groupCount] followed by UData[dataCount]
UData   +0 u32 tag   +4 u32 bits: b0 indexed, b1 embedded, b2 allocated, b3..7 section, b8..31 fSize (bytes)
        +8 u32 count +C u32 offset     // embedded: data = &this_UData + offset (bytes)
```
A pack is UGroup `'CARP'` (bits 0x2A: 1 child group) → UGroup `'Arti'` (bits 0x12; WCollisionPack.cpp:46
`GroupLocate('Ar'<<16,'ti')`) holding the UData:
* `'Name'`: the section name as a C string ("101"), count 0, fSize 4.
* `'ca'|i` (tag 0x6361iiii, indexed): **WCollisionArticle** i, fSize = article bytes, count 1.
* `'ci'|0` (0x63690000): **WCollisionInstance[count]**, fSize = 0x40·count (WCollisionPack.cpp:73-90).
* `'co'|0` (0x636F0000): WCollisionObject[count]. **No pack has one** (OBBs only come from 0x34191 and runtime).

Instance i uses article `'ca'|inst.fRenderInstanceInd` (WCollisionPack.cpp:92-113). In the data every pack has as many
articles as instances (7,544 each), with a 1:1 mapping.

### WCollisionInstance (0x40) (CARP.h:277-288, World/WCollision.h:255-274, World/Common/WCollision.cpp:28-83)
| off | type | field | notes |
|---|---|---|---|
| 0x00 | f32[3] + f32 | fInvMatRow0Width | row 0 of the world→local rotation (local X axis); .w = **half-width** along local X |
| 0x10 | u16 | fIterStamp | runtime |
| 0x12 | u16 | fFlags | 1 = Y not up (local up = cross(row2, row0)); 2 = dynamic; 0xC0 = runtime exclusion. **All 7,544 are 0** (yaw-only, up = (0,1,0)) |
| 0x14 | f32 | fHeight | half-height (pos.y ± fHeight bounds the segment test, WCollisionMgr.cpp:676-683) |
| 0x18 | u16 | fGroupNumber | 0 = always on; else a scenery group number (L2RA 0x34109). 891 instances, 752 distinct groups, **all found in 0x34109** |
| 0x1A | u16 | fRenderInstanceInd | index of the `'ca'` article (and [?] of the section's render instance) |
| 0x1C | u32 | fCollisionArticle | pointer, 0 on disk |
| 0x20 | f32[3] + f32 | fInvMatRow2Length | row 2 (local Z axis); .w = **half-length** along local Z |
| 0x30 | f32[3] + f32 | fInvPosRadius | translation of world→local; .w = XZ bounding radius |

Transform (row vectors, UMath `RotateTranslate`): `local = world.x*row0 + world.y*up + world.z*row2 + t`. Inverse:
`world_k = dot(local − t, row_k)` for rows (row0, up, row2) (CalcPosition, WCollision.cpp:34-47). Verified: the
instance centres fall inside the bounds of their section's render scenery (A41: X 338..373, Y 2450..2548 vs scenery
X 0..402, Y 0..2562; same for D46 and O28). All 320,187 strip vertices lie within ±half-width/half-length of their instance.

### WCollisionArticle (header 0x10) (WCollision.h:90-121)
```
+0x00 u16 fNumStrips   +0x02 u16 fStripsSize  (bytes of spheres + strip verts)
+0x04 u16 fNumEdges    +0x06 u16 fEdgesSize   (= 0x20 * fNumEdges in all 7,544 articles)
+0x08 u8 fResolvedFlag (0) +0x09 u8 fNumSurfaces +0x0A u16 fSurfacesSize (= 4 * fNumSurfaces)
+0x0C u16 fIntermediatObjInd [? meaning unknown, no runtime reader]  +0x0E i16 fFlags (all 0)
+0x10                       WCollisionStripSphere[fNumStrips]          (0x10 each)
+0x10 + 0x10*fNumStrips     strip vertex data, up to +0x10 + fStripsSize
+0x10 + fStripsSize         WCollisionBarrier[fNumEdges]               (0x20 each)
+0x10 + fStripsSize + fEdgesSize   u32 surfaceKey[fNumSurfaces]   // simsurface collection key (Attrib hash)
then 0..60 bytes of unused tail up to the UData fSize [?]
```
Note: `GetBarrier(ind)` multiplies ind by 0x10 (WCollision.h:101-104), but the data stride and `Next()` are 0x20. It is
only ever called with 0.

### WCollisionStripSphere (0x10) (WCollision.h:20-32)
`f32 x, y, z` (centre in instance-local space), `u16 fRadius` (XZ radius × 16, so r = fRadius/16 m),
`u16 fOffset`: the strip starts at **article + fOffset + 0x10**.

### WCollisionStrip (triangle strip) (WCollision.h:10-16, 34-88)
An array of `WCollisionPackedVert` (8 bytes): `i16 x, y, z` (**/128 m, relative to the sphere centre**),
`u8 surface` (index into the article's surfaceKey table), `u8 flags`.
* `verts[0]`'s surface u16 = **NumVerts**; `verts[1]`'s surface u16 = **strip Flags** (1 = kUpFacing, 2 =
  kFacingUnknown; 0 otherwise). So the first two vertices' surface fields are not surfaces.
* Triangle i (0..NumVerts−3) = (v[i], v[i+1], v[i+2]), and its surface/flags are those of **v[i+2]**.
* Winding alternates as in any strip. FindFaceInTriStrip (WCollisionMgr.cpp:46-112) tests right-handed first when
  flag 1 is set, then toggles. When flag 2 is set it uses an orientation-free test.
* Verified: strip bytes = 8·ΣNumVerts exactly, all offsets in range, all surface indices < fNumSurfaces, and every
  vertex lies inside its sphere radius.

### WCollisionBarrier (0x20): walls (WCollision.h:123-200)
```
+0x00 f32 x0, y0, z0   +0x0C WSurface {u8 surfaceIndex, u8 flags} + 2 pad   (fPts[0].w)
+0x10 f32 x1, y1, z1   +0x1C f32 invXZLength = 1/|p1-p0|_xz                  (fPts[1].w)
```
A vertical quad from (x0, z0) to (x1, z1), spanning Y from min(y0, y1) to max(y0, y1). Normal = (dz, 0, −dx)·invLen,
where dz = z1 − z0 and dx = x1 − x0 (`GetNormal`, :154-159). Collision treats it as a zero-thickness box, half-width
w/2 and half-height h/2, and is **single-sided** (contacts ignored when dot(normal, contactNormal) ≤ 0) unless surface
flag **0x10** is set (two-sided) (WCollisionMgr.cpp:744-782). Verified: invXZLength matches |p1 − p0| for all 45,815.

### WSurface flags (byte after the surface index)
| bit | where | meaning |
|---|---|---|
| 0x02 | barriers (45,455 of 45,815 have it) | [?] barrier marker; nothing in the decomp tests it |
| 0x04 | triangles (16,375) | overlapping/stacked surface: the ground search keeps looking for a closer face (WWorldPos.cpp:116,143) |
| 0x08 | triangles (61) | skipped by the ground search (FindFaceInTriStrip :61,82,91), i.e. not drivable ground [?] |
| 0x10 | barriers (351) / triangles (16) | two-sided barrier |
| 0x40, 0x80 | runtime | exclusion bits OR-ed onto barriers and empty instances when fGroupNumber ≠ 0 (WCollisionAssets.cpp:117-147), tested against a collider's exclusion mask (WCollisionMgr.h:67-77) |

### WCollisionObject (0x70): not on disk (CARP.h:295-306, WCollision.h:237-253; harness `world9`: 0 'co' UData in the 390 packs; Core/CollisionPack.cs reads them anyway)
`Vector4 fPosRadius` (centre and XZ radius), `Vector4 fDimensions` (half extents), `u8 fType` (0 = box obstacle),
`u8 fShape` (0 box / 1 cylinder), `u16 fFlags` (1 = dynamic, 0x40 = managed), `u16 fRenderInstanceInd`,
`WSurface`, 8 pad bytes, `Matrix4 fMat` (rotation). TrackOBBs become objects through `bConvertToBond`
(WCollisionAssets.cpp:94-105); `CreateObject` stores fPosRadius.y = mat.y − dim.y (the bottom).

## B.3 Totals (all 390 packs)

* 7,544 instances / articles: 4,035 triangles only, 3,498 barriers only, 11 both.
* 38,379 strips, 320,187 packed vertices, **243,429 triangles** (45 degenerate). Normal |y| ≥ 0.7 for 242,602
  (99.7 %), 678 between 0.3 and 0.7, 104 steeper. **Triangles are the ground; walls are barriers.**
* **45,815 barriers**. Heights: 118 < 2 m, 3,623 2–10 m, 11,264 10–50 m, 30,810 ≥ 50 m (effectively infinite walls).
* World bounds of the triangles (sim): x −4586..1010, y −17..269, z −1466..4876, i.e. game X −1466..4876 and
  Y −1010..4586.
* About 624 triangles and 117 barriers per pack on average. The largest packs are a few thousand triangles.

## B.4 Surfaces → `simsurface`

The article table holds `simsurface` collection keys (Attrib Jenkins hash of the name; Generated/AttribSys/Classes/
simsurface_hash.h). At load, `WCollisionArticle::Resolve` replaces each key with `SimSurface::Lookup(key)`
(WCollisionPack.cpp:60-70). The ground face's surface becomes the wheel's `SimSurface` (WWorldPos.cpp:211,
SuspensionSimple.cpp:495-506 uses `DRIVE_GRIP`, `LATERAL_GRIP`, `ROLLING_RESISTANCE`).

Triangle counts by surface: asphalt 103,734, concrete 34,101, asphalt_no_leaves 27,547, grass 25,038,
grass_shoulder 12,525, cobble 9,939, key 0 6,434, golf_rough 5,989, gravel 4,949, golf_fairway 4,261, sand 2,299,
wood 1,526, dirt 1,201, golf_green 1,158, metal 1,125, stone 1,019, railroad 584.
Barriers: key 0 13,503, asphalt 9,660, concrete 8,805, grass 4,710, … glass 3.
Key 0 means "no surface" → `SimSurface::kNull`/`default`. Two unknown keys (0x33199393, 0x4BB72BD3) are in 3
articles [?]. Treat them as default.

Attribute values (attributes.bin):

| surface | DRIVE_GRIP | LATERAL_GRIP | ROLLING_RESISTANCE |
|---|---|---|---|
| asphalt, asphalt_no_leaves, concrete, cobble, stone, wood, metal, railroad, glass, gravel, dirt, grass_shoulder, default | 1 | 1 | 1 |
| grass | 0.7 | 0.6 | 6 |
| golf_fairway | 0.75 | 0.75 | 1.5 |
| golf_rough | 0.6 | 0.5 | 6 |
| golf_green | 0.5 | 0.25 | 0.1 |
| sand | 0.25 | 1 | 30 |

GROUND_FRICTION = WORLD_FRICTION = 1 and STICK = 0 for all of them. Other attributes are effects/audio.

**Drivable vs wall:** every triangle surface is drivable; the surfaces only change grip and effects. Flag 0x08 faces
(61) are not ground [?]. **Walls are the barriers** (any surface, usually concrete/asphalt/none); the surface on a
barrier only selects the impact effect.

## B.5 Recipe for Unity

Per stream section that loads (any section whose bytes contain chunk 0x3B801):
1. Walk the chunks recursively, as for 0x34027, and for each 0x3B801: skip 0x11 padding to 16-byte alignment, read
   the 16-byte carp header, then the UGroup 'CARP' → 'Arti' → UData `'ci'`, `'ca'|i`.
2. For each instance: R rows = (row0.xyz, (0,1,0) [or cross(row2,row0) if flags & 3], row2.xyz) and t = fInvPosRadius.xyz.
   `toWorldSim(l) = (dot(l−t,row0), dot(l−t,up), dot(l−t,row2))`, then `toUnity(s) = (s.z, s.y, −s.x)`.
   Equivalently, a Transform with position `toUnity(−tR^T)` and yaw from row0. Instances with `fGroupNumber ≠ 0`
   become children enabled with the same scenery-group state the WorldStreamer already applies to 0x34109 groups
   (`_grouped` / `InstanceVisible`).
3. **Ground**: one `Mesh` per pack (or per instance) with
   `vertex = toUnity(toWorldSim(sphere.xyz + (x, y, z)/128))`. Emit each strip triangle (v[i], v[i+1], v[i+2]), then
   orient it so the normal faces up: if `cross(b−a, c−a).y < 0` swap b and c, matching the game's normal flip
   (WCollisionMgr.cpp:227-231). For the 782 steep triangles (|n.y| < 0.7), also emit the reversed triangle or set
   `Physics.queriesHitBackfaces` [?]. Keep a parallel `surfaceKey[]` per triangle and look it up from
   `RaycastHit.triangleIndex` for the wheels (→ simsurface DRIVE/LATERAL_GRIP, ROLLING_RESISTANCE). Optionally split
   submeshes per surface for PhysicMaterials. Skip flag 0x08 triangles for wheel ground [?]. Use a non-convex
   `MeshCollider` with `cookingOptions` including WeldColocatedVertices.
4. **Walls**: one `BoxCollider` per barrier. Convert p0 and p1 to Unity with the same transform. The XZ direction
   d = p1 − p0, length L, height H = |y1 − y0| (clamp to ~30 m above the ground if wanted; 50 m+ walls are deliberate). The
   game normal in sim is (dz, 0, −dx). Convert it with toUnity like a vector, and check it points away from the inside
   of the wall with one sample. Size = (L, H, T) with T ≈ 1 m. Rotate so that local X = d and local Z = −normal, and
   centre = mid(p0, p1) + (0, H/2 from YBot) − normal·T/2, so the front face lies on the barrier plane. This mimics
   one-sidedness (cars coming from behind pass through the thin plane only in the game, so for 0x10 barriers use
   T/2 on both sides). Put them in a "WorldWall" layer.
5. The one TrackOBB (0x34191) → a static BoxCollider. Smackables stay as they are (separate dynamic props, chunk 0x34027).
6. Unload with the section.

## B.6 What changes in the Unity project

* `WorldStreamer.cs:352` currently adds `MeshCollider(sharedMesh = render mesh)` to every scenery instance that
  `WantsCollider()` (bounds ≥ 0.4 m and > 3 vertices, :448-454). That turns foliage, lamp posts, overpass undersides,
  decals and building detail into collision. It also misses the invisible walls the game has (the barriers, often tens
  of metres tall, block shortcuts into buildings and off the map). Grip per surface does not exist.
* Replace it with: (a) no collider on render meshes, except `Smackable.Make` for smackables, which stays; (b) the B.5
  ground MeshColliders and barrier BoxColliders built from 0x3B801 in the same section load; (c) the traffic lane kerb
  raycasts (`Systems.LaneOffset`) and the AI ground raycasts then hit the real road surface. Lane offsets should come
  from the RNpf lane profiles instead.
* Collision memory: ~243k triangles plus ~46k boxes for the whole map. Only the loaded sections exist at once, so this
  is far less than the render-mesh colliders.
* The 0x3B800 grid (CGrd/CGcn) is only a broadphase over instance tags `(section<<16)|index`
  (WCollisionAssets.cpp:216-245). Unity's physics broadphase replaces it, so it does not need to be parsed.

## B.7 The exe's world queries (Core/WCollisionQueries.cs, Runtime/Physics/WorldQueries.cs)
Ported 1:1 over the loaded packs (decomp WCollisionMgr.cpp, WWorldPos.cpp, WWorldMath.cpp; PC addresses below), so AI,
cameras, resets and spawns can ask what WCollisionMgr answers instead of PhysX raycasts:
* GetInstanceList (point/cylinder 0x783D70, segment 0x783B80): XZ distance < radius + fInvPosRadius.w (the spherical
  radius when the instance needs the cross product); the segment version also needs the Y span to overlap
  pos.y ± fHeight. The WGrid broadphase is replaced by a 64 m XZ cell grid [ours]. Grouped instances count only while
  their scenery group is enabled.
* FindFaceInTriStrip(point) 0x763BB0: kFacingUnknown strips → the first face whose XZ holds the point (InTri) with
  MinY < y + 1; other strips alternate InTriR / InTriL, starting right when flag 1; faces with surface flag 0x08 are
  skipped. FindFaceInCInst(point) 0x76C9B0: local point within ±half-width/length; per strip sphere (XZ radius); distance
  y − planeY (normal flipped up, y capped at 0.9999) > 0 and MinY − 0.5 < y; the nearest below wins.
* Segment versions (MakeSegSpaceMatrix 0x767A80, FindFaceInCInst(matrix) 0x76CC10, FindFaceInTriStrip(matrix) 0x76C810):
  faces in segment space, the face whose cross-section holds the segment, nearest along it (−1 < d, then 0 < d).
* WWorldPos: FindClosestFace lifts the point by fYOffset (0.025; 0.05 in SetVehicleOnGround) and keeps the cached face
  while the point stays in it; Update 0x789CC0 (normal, plane distance, kNull surface without a face); HeightAtPoint
  0x768200.
* GetWorldHeightAtPoint 0x7897C0; GetWorldHeightAtPointRigorous 0x789870 (else CheckHitWorld from 2 m below to 1000 m
  above, faces only); CheckHitWorld 0x7854B0 (a segment under 0.01 m is extended to 0.01; barriers first:
  SegmentIntersect 0x767280 with the ±0.0001 tolerance and YBot < y < YTop, nearest to the start, normal
  (dz, 0, −dx)·invLen turned toward the start; then the face along the segment: IntersectSegPlane t ∈ [0, 1], normal
  turned toward the start; the nearer wins); GetBarrierList (barriers within radius in XZ and [y ± radius], sorted by
  distance²).
* Check (harness `physcol verify 3000`): 2,999 of 3,000 random points match a brute-force topmost-triangle search (the
  one difference is the exe's first-face-in-strip rule); vertical ±5 m segments hit the same faces; 0.006 ms per
  query. `physcol query <gameX> <gameY>`: a height and a ring of 16 wall rays.
* Unity API: `WorldQueries.Height`, `HeightRigorous`, `CheckHitWorld(from, to, out hit, primMask, exclusionMask)`,
  `Barriers(pos, radius, list, mask)`, `WorldQueries.Pos` (WWorldPos), `MaskFor(driverClass)`. Packs register in
  WorldCollision.Build and leave with their section.

## B.8 Invisible barriers: scenery-group walls and exclusion masks
WCollisionAssets::SetExclusionFlags 0x76C6A0 ORs 0xC0 onto every barrier surface of an instance with a scenery group
number (891 instances, 752 groups, e.g. race barriers), and onto strip-less grouped instances. WCollisionMgr passes a
primitive only when (mask & flags) == 0, and a vehicle's mask comes from its driver class (PVehicle::LoadBehaviors
0x685240: human/racer/remote 0, cop 0x80, traffic/none/NIS 0x40). **So grouped walls stop only the player and racers;
cops and traffic drive through them** (and nobody while the group is off). Port: WorldCollision registers the walls of
grouped instances with exclusion 0xC0 (`WorldCollision.ExclusionOf`) and CarContacts ignores those contacts for a car
whose mask shares a bit. The ground triangles of grouped instances are not excluded (strip flags are 1/2).

## B.9 SimSurface remainder
SimSurface::InitSystem 0x6E9790: kNull = the "null" collection (0x90A4D09A), mUnknown = "unknown"; Lookup 0x6E95E0
returns "unknown" for any key without a collection (key 0 and the two unknown keys). Every surface except grass, sand
and the golf ones has grip 1, so only the identity changes: `WorldCollision.Surface.Null` (Valid false) is what a wheel
or body has with no world face (PVehicle off-world test, ResetCar ValidTerrain); `SurfaceOf` falls back to "unknown".

## B.10 Round 9: the rest of WCollisionMgr / WWorldPos / WWorldMath / WCollider / WCollisionAssets (Core, harness `world9`)
* **WWorldMath** (Core/WWorldMath.cs): every entry point; IntersectCircle 0x767400 ported (both ends inside → true, u 0;
  roots of |p1 + u·d − c|² = r², the square root through 0x5C5E80, true when a root is in [0, 1]); NearestPointLine2D(3)
  (unclamped projection, u 0 under 1e-6). CopCar's SegmentHitsCircle and AIMath's nearest point are the AI agent's own
  copies (not switched over).
* **GetTriList** 0x787710 (WCollisionMgr.cpp:1130-1199) (WCollisionQueries.GetTriList): faces of the strips whose sphere meets the
  region, kept when their XZ box overlaps and the circle is not wholly outside an edge (PTDir orientation, UVectorMath.h:203).
  The exe offsets local faces by (strip centre − inverse translation), valid for unrotated instances only (all instances
  have flags 0 on disk); ToWorldSim is used (identical for those).
* **GetClosestIntersectingBarrier** 0x773170 **/ GetBarrierNormal(barrierList)** (:893-923, :1074-1092), **GetWorldNormal** (:483-522),
  **ClosestCollisionInfo** (:524-542), **GetBarrierList(instList)** (:1008) and **GetObjectList** (:682-711, type-0 objects,
  XZ circle) in WCollisionQueries. The track OBBs register as managed objects (WCollisionAssets::Init :94-105 /
  CreateObject :255-283: position.y = matrix.y − dim.y, radius = |dim.xz|) from WorldCollision.BuildTrackObbs;
  `WorldQueries.Objects(pos, radius, list)`.
* **WCollider** (Core/WCollider.cs, all of WCollider.cpp; Refresh 0x789EC0): Create/Destroy with the global list, Refresh (update mask:
  objects always, instances+faces / barriers when invalid or the request leaves the region; region = request × 1.1, or
  predictive: under 5 m since the last refresh → pushed 2.5 × the step ahead, radius + push + 0.1 ≤ 25), PrepareRegion,
  InRegion, Clear / EmptyLists, InvalidateIntersectingColliders, InvalidateAllCachedData (also on a pack unload [ours]:
  the exe frees packs under the cached pointers). `WorldQueries.Collider`.
* **WWorldPos** remainder: the collider path FindClosestFace(triList) (WWorldPos.cpp:100-152: the cached face kept while the
  unlifted point is inside it; lifted, the first face with −1 < dist below the best, stop unless stacked flag 4),
  Update(collider), SetTolerance, fUsageCount. Check `world9`: 608 face points, the collider tri-list search and the
  instance search agree on 605 (3 overlapping-face points: the tri list takes the first, by design); 112/112 barrier
  segments hit the same point through a collider's barrier list and CheckHitWorld.
* **Not ported / replaced, with the deviation:** `Collide(geom, barrierList)` (:733-815) is the physics contact itself —
  PhysX against the WorldCollision wall prisms; its side rule (one-sided barriers and ONESIDED spheres) is
  WorldCollision.BarrierContactCounts. `Collide(geom, instanceList)` (:817-891, 4 box diagonals through GetWorldNormal)
  only runs for INSTANCE_COLLISIONS_3D bodies — 0 in every rigidbodyspecs collection (harness `attribclass rigidbodyspecs
  INSTANCE_COLLISIONS_3D`), so dead on PC data. `BuildGeomFromWorldObb` (:713) → the OBB BoxColliders. WCollisionAssets
  Init/Shutdown/LoadCollisionPack/UnLoadCollisionPack/Instance/Object index maps → WorldStreamer + WorldCollision.Build +
  WorldQueries.Register (a pack registers with its section); AddPack/RemovePackLoadCallback have no caller in the
  decomp (dead); SetExclusionFlags ported earlier (B.8). WCollisionPack Init/Resolve/DeInit → CollisionPack.Read (the
  surface keys resolve through WorldCollision.SurfaceOf). WWorld Init/Loader/Open/Close (the UWORLD CARP group → WGrid +
  WCollisionAssets::Init) → the 64 m cell grid of WCollisionQueries [ours] and BuildTrackObbs. WCollision.cpp
  (CalcSphericalRadius, CalcPosition, both MakeMatrix) is in CollisionInstance / WCollisionQueries.Add.
* **Callers still on PhysX raycasts (not this file's to switch; hooks):** PlayerCar.Probe (wheels: Physics.RaycastNonAlloc
  against the WorldCollision MeshColliders — the game's own triangles, not render meshes, so the "render meshes" note was
  stale; the exe's SuspensionRacer uses a WWorldPos per wheel on RigidBody's WCollider tri list → `WorldQueries.Pos.Update
  (pos, collider, ...)`), CarShadow (Physics.RaycastNonAlloc; the exe's VehicleRenderConn keeps a cylinder WCollider 0x1C),
  CameraMover (sphere WCollider 0x1C).
