# NFS MW (PC v1.3) — racer AI and road navigation spec

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/` (file:line below, short names:
`AIVehicle.cpp`=AI/Common/AIVehicle.cpp, `Racecar.cpp`=AI/Common/AIVehicleRacecar.cpp, `Race.cpp`=AI/Actions/AIActionRace.cpp,
`WRN.cpp`=World/Common/WRoadNetwork.cpp, `WPF.cpp`=World/Common/WPathFinder.cpp, `PID.cpp`=AI/Common/AdaptivePIDController.cpp,
`Table.cpp`=Misc/Table.cpp), PC addresses from `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`, and a byte check of
`TRACKS/L2RA.BUN` (script in the session scratchpad; counts below are from it). Related specs: `races.md` (events, catch-up curves
§5.3, difficulty §5.2), `physics.md` (car sim, 120 Hz step). Marks: **[?]** = inferred / not in the decomp; everything else is
read from code or verified in data. Units: m, s, m/s, rad unless stated. `Ramp(x,a,b)=clamp01((x-a)/(b-a))` (0 if b-a≤ε).
`Table(x)` = N equally spaced samples over [min,max], linear, clamped (Table.cpp:5). `Graph(x)` = (x,y) points, linear, clamped
(Table.cpp:49). **Gotcha:** `UMath::Lerp(Vector2 a, b, t)` returns `a·t + b·(1−t)` (reversed!); the float and Vector3 Lerps are
the normal `a+(b−a)t` (UMath.h:510-525).

## 0. Coordinates, rates, object model

* Road data is in **sim space** (UMath, Y up, left-handed): node `(x,y,z)` with `y` = height. The existing reader turns it into
  "game" space `(z, −x, y)` and Unity uses `(game.X, game.Z, game.Y)`, i.e. **Unity = (sim.z, sim.y, −sim.x)**, a pure rotation.
  All 2-D AI maths use `(x,z)`. The road right vector of a forward `f` is `(f.z, 0, −f.x)` and lateral offset
  `= cross2(d, f) = d.x·f.z − d.z·f.x` (positive = right); this formula is valid unchanged in Unity coordinates.
* Sim tick 1/120 s (physics.md §1). `SimTask::Run` (Sim/Common/Simulation.cpp:345): a FIXED task with rate r runs when an
  accumulator `+= r` reaches 1, with `dT = dT_sim / r`. AI racers: think task rate **0.5** (AIVehicle.cpp:1930,1935) ⇒ 60 Hz,
  dT = 1/60, start offsets staggered 0 / 0.5 between consecutive racers. `AIActionRace` off-path check rate 0.25 ⇒ 30 Hz
  (Race.cpp:291). Steering/throttle (`OnDriving`) run from `OnTaskSimulate` every sim tick while drive flags are set
  (AIVehicle.cpp:562) **[?: assumed per 120 Hz step]**.
* Class chain: `AIVehicle` → `AIVehiclePid` (PID steering/throttle) → `AIPerpVehicle` (skill, catch-up, heat) →
  `AIVehicleRacecar` (goal `AIGoalRacer`). The player is `AIVehicleHuman` (same chain; its nav drives the wrong-way/HUD logic).
* Think order (`AIVehicleRacecar::Update`, Racecar.cpp:554): simple-physics on/off → `AIPerpVehicle::Update` (catch-up) →
  spawn timer → reverse-override timer → `UpdateTargeting` → `AIGoal::Update` = `ChooseAction` + current action `Update`.
  Every think starts with `ClearDriveFlags()`; the action sets flags with `DoDriving(7)` (1 steer, 2 gas/brake, 4 reverse).
* `AIGoalRacer` = actions **AIActionRace (score 0)** + **AIActionGetUnstuck (score 1)** only (AIGoal.cpp:393). No airborne,
  ram or traffic action for racers. `ChooseAction` (AIGoal.cpp:33): any other action whose `CanBeAttempted` is true and whose
  score ≥ current (or the current is finished) replaces it (`FinishAction` old, `BeginAction` new).

## 1. Road network data (chunk 0x3B800, 'CARP')

### 1.1 Container
Chunk `0x3B800` body is 16-aligned; it starts `'CARP'` (bytes `PRAC`), then i32 122 **[?]**, 0, 1, then 16-byte directory
entries `{tag (bytes reversed), u32 (size<<8)|flags, i32 count, u32 offset}`; flag 2 = offset relative to the entry. Entries
with flags 0x1A are groups (their `count` = children) **[?]**. L2RA.BUN directory (11 entries, then 0xAA fill):

| # | tag | size | count | role (decomp) |
|---|---|---|---|---|
| 0 | CDat | group | 3 | grid group `('CD','at')`, WGrid.cpp:30 |
| 1 | Map | group | 0 | **[?]** |
| 2 | RNgp | group | 5 | road-network group `('RN','gp')`, WRN.cpp:72 |
| 3 | CGcn | 210900 | 2779 | WGrid cells, variable-size `WGridNode` records (`TotalSize()`), WGrid.cpp:41 |
| 4 | CGrd | 36 | 1 | WGrid header: min, rows, cols, edge size (WGrid.cpp:31) **[? exact layout]** |
| 5 | co | 0 | 0 | **[?]** |
| 6 | RNhd | 14 | 1 | `WRoadNetworkInfo` |
| 7 | RNnd | 140320 | 4385 | `WRoadNode[]`, 32 B |
| 8 | RNpf | 45440 | 710 | `WRoadProfile[]`, 64 B (lane profiles, **not** "pathfind data") |
| 9 | RNrd | 10464 | 1308 | `WRoad[]`, 8 B |
| 10 | RNsg | 143836 | 6538 | `WRoadSegment[]`, 22 B |

`WRoadNetwork::Init` (WRN.cpp:58, PC 0x789980) maps `RNhd/pf/nd/sg/rd` in place, then `ResolveBarriers`, `ResolveShortcuts`,
`ResetRaceSegments`. The grid (`CGrd/CGcn`) is only a spatial index (`WGrid_kRoadSegment` = elem type 3 lists the segments in
each cell); a port can replace it with its own segment hash (segments by bounding box). Intersections are 0 in the data.

### 1.2 Records (WRoadElem.h; all offsets verified against L2RA)
`WRoadNetworkInfo` (0xE): u16 NumProfiles 710, NumNodes 4385, NumSegments 6538, NumIntersections 0, NumRoads 1308,
NumJunctions 0, pad[2].

`WRoadNode` (0x20, WRoadElem.h:479): `+0 Vector3 fPosition` · `+0xC s16 fIndex` (= array index, verified) ·
`+0xE s16 fProfileIndex` (0..709; <0 = invalid profile; **not a road index** as RoadNetwork.cs/encyclopedia say) ·
`+0x10 u8 fNumSegments` · `+0x11 u8 pad (0xAA)` · `+0x12 u16 fSegmentIndex[7]` (unused = 0xAAAA).

`WRoadSegment` (0x16, WRoadElem.h:230):

| off | type | field | decode |
|---|---|---|---|
| 0x0 | u16[2] | fNodeIndex | node 0 = "start", node 1 = "end" |
| 0x4 | u16 | nLength | `len = n·1000/65535` m (max 1000; verified len/chord median 1.005) |
| 0x6 | s16 | fRoadID | index into RNrd, −1 = none |
| 0x8 | s16 | fIndex | = array index (verified) |
| 0xA | u16 | fFlags | table below |
| 0xC | u16 | fEndHandleLength | |
| 0xE | u16 | fStartHandleLength | |
| 0x10 | s8[3] | vEndHandle | end control = `vEnd·EndLen·500/(127·65535)` (points back from node 1) |
| 0x13 | s8[3] | vStartHandle | start control = `vStart·StartLen·500/(127·65535)` |

Directions: start fwd = `vStart/127`, end fwd = `−vEnd/127`; start right = `(vStart.z,0,−vStart.x)/127`, end right =
`(−vEnd.z,0,vEnd.x)/127` (WRoadElem.h:371-417). Curved segments (flag 0x100, 5892 of 6538) are a cubic spline
`BuildSplineEx(P0=node0, P1=node0+startCtrl, P2=node1+endCtrl, P3=node1)` (WRN.cpp:493) — **Bezier basis [?]** (USpline has
Bezier and Catmull-Rom; impl at PC 0x5D0BF0 not decompiled; handles measured 0.23-0.52 × chord and within 15° of it, which fits
Bezier). Straight segments are a lerp. `EvaluateCurvatureXZ` (PC 0x5D2E60) = signed planar curvature of the spline
`(x′z″−z′x″)/(x′²+z′²)^1.5` **[?]**.

Segment flags (WRoadElem.h:212; counts in L2RA):

| bit | name | n | meaning / users |
|---|---|---|---|
| 0 | Decision | 3463 | short connector inside a junction; GetAttachedDirectionalSegment skips them |
| 1 | NoTraffic | 1356 | `IsTrafficAllowed()=!bit1` (traffic filter; "legal road") |
| 2 | RaceRouteForward | runtime | race goes node0→node1 |
| 3 | Intersection | 3463 | (same set as Decision) |
| 4 | Entrance | 1809 | traffic |
| 5 | CopsXorTraffic | 21 | `ShouldCopsConsider = trafficAllowed ^ bit5` |
| 6 | OneWay | 104 | only node0→node1 |
| 7 | Shortcut | runtime | road belongs to a race shortcut |
| 8 | Curved | 5892 | use spline |
| 9 / 10 | EndInverted / StartInverted | 1220 / 1222 | profile at that end is mirrored |
| 11 | ChopperStayLow | 7 | helicopter |
| 12 / 13 | CrossesBarrier / CrossesDriveThroughBarrier | runtime | set by ResolveBarriers |
| 14 | LaneMap | 239 | **[?]** unused by AI |
| 15 | InRace | runtime | segment is on the race route |

`WRoad` (8, WRoadElem.h:47): u16 nScale (`GetScale = (nScale<<8)/65536`, data 256 → 1.0; multiplies shortcut length in
`GetPathDistanceRemaining`), u16 nLength, u8 nShortcut (runtime: race shortcut index or 0xFF), u8 nMinWidth, u16 nSpeechId.

`WRoadProfile` (0x40, WRoadElem.h:113): u8 fNumZones, u8 fMiddleZone, pad[2], `WRoadLane mLanes[15]`. Each lane u32:
type = bits 0-3, width = signed bits 4-17 × 100/8191 m, offset = signed bits 18-31 × 100/8191 m (stored offsets are
**unsigned distances from the centre line**). Helpers: `LaneNumber(l,inv) = inv ? N−1−l : l`; `MiddleZone(inv) = inv ? N−mid : mid`;
`RelativeLaneOffset(l,inv) = l < MiddleZone(inv) ? −off : +off` (left negative). Forward lanes = `[mid, N)`, backward lanes =
`[0, mid)` (`GetNthForwardLane(n)=min(mid+n,N−1)`, `GetNthBackwardLane(n)=max(mid−1−n,0)`). Example profile 1: 6 zones, mid 3,
types [3,1,1,1,1,3], offsets [10,6,2,2,6,10], width 4. The profile used at a segment end is `node[end].fProfileIndex`, mirrored
when the end's Inverted flag is set.

Lane (zone) types — 4-bit; name order from the Undercover constant list in WRoadElem.h:20-32 starting at 1 **[? numbering]**,
consistent with the data (type 1 = traffic is compared against `kLaneTraffic`=1 in code; type 4 is a 15.9 m centre zone; 3 sits
at the edges; 7 is excluded from every drivable mask): 1 Traffic, 2 Sidewalk, 3 Shoulder, 4 Median, 5 CurbMedian,
6 GrassMedian, 7 Barrier, 8 Train, 9 Parking, 10 Road, 11 Center. Histogram: 1:1634, 2:251, 3:732, 4:160, 7:75, 10:471.

Lane masks per nav lane type (`(mask >> zoneType) & 1`, WRN.cpp:47-53, 2664):

| nav lane type | drivable | selectable |
|---|---|---|
| 0 Racing | 0xFFFFDF7F (all but 7,13) | 0x402 (1 Traffic, 10 Road) |
| 1 Traffic | 0x2 | 0x2 |
| 2 Drag | 0xFFFFDF7F | 0xFFFFDF5B (no 2,5,7,13) |
| 3 Cop | 0xFFFFDF7B (no 2,7,13) | 0x472 (1,4,5,6,10) |
| 4 CopReckless | 0xFFFFDF7F | 0xFFFFDF7F |
| 5 Reset | 0x402 | 0x402 |
| 6 StartingGrid | 0xFFFFDF7F | 0x402 |
| 7 Any | all | all |

### 1.3 Runtime flags set from race data
* `ResolveBarriers` (WRN.cpp:268): for each enabled `TrackPathBarrier` (chunk 0x3414D, 0x18 B: bVector2 Points[2] in game 2-D
  `(z,−x)`, s8 Enabled, pad, s8 PlayerBarrier, s8 LeftHanded, u32 GroupHash), every segment whose spline (sampled in
  `max(ceil(len/10),4)` pieces) crosses it gets bit 12 (or bit 13 if `PlayerBarrier`, i.e. drive-through), unless its road is one
  of ≤4 `BarrierExemptions` markers of the race. Racers/player/GPS respect both bits; cops only full barriers
  (WRoadNetwork.h:314-320).
* `ResolveShortcuts` (WRN.cpp:224): each race `Shortcuts[i]` marker → nav at the marker (`InitAtPoint`, decision filter on,
  centre lane) → that road's `nShortcut = i`; every segment of such a road gets bit 7.
* Race route (bits 15 and 2): `ResetRaceSegments` clears; `AddRaceSegments(nav)` (WRN.cpp:185) marks every segment of a
  `kTypePath` nav as InRace and sets RaceRouteForward per consecutive pair (`FlagSegmentRaceDirection`, WRN.cpp:170).
  Who builds that route (a `kPathRaceRoute` search through the checkpoints, with a per-shortcut `shortcut_allowed[]`) is in
  `GRaceStatus` (not decompiled) **[?]**; `bRaceRouteError` is set when it fails, and then AI racers stop (§4.1).

## 2. WRoadNav — the navigation cursor (WRoadNetwork.h:186, WRN.cpp)

A nav is a point that runs along the graph: `fSegmentInd`, `fNodeInd` (the node it is heading **to**: 1 = forward along the
segment), `fSegTime` ∈[0,1] measured from the from-node, `fLaneOffset` (lateral, m, + right of travel), `fPosition`,
`fForwardVector`, `fCurvature`, start/end positions + controls for the centre and (with a cookie trail) the left/right corridor
edges, `fDeadEnd`, nav type (None, Traffic, Direction, Path), path type (Cop, None, Racer, GPS, Player, Chopper, RaceRoute),
lane type (above) and filters (race, traffic, cop, decision). AI racers: path type Racer, cookie trail on, decision filter on
(AIVehicle.cpp:503); race filter on while racing (`AIPerpVehicle::Update` sets it every think, AIVehicle.cpp:2074).

* **InitAtPoint(pos, dir, forceCentre, dirWeight)** (WRN.cpp:2618) → `FindClosestSegmentInd` (WRN.cpp:2213): candidate
  segments from grid cells within 32 m; skip by filters (decision / !InRace / traffic / cops). Project `pos` on the chord
  (clamped), refine on curved segments by stepping the spline at 1 m then 0.25 m (`FindClosestOnSpline`). Score =
  distance; if `dirWeight>0`: distance measured **outside the road edges only** (edges = outer lane offset ± half width,
  interpolated along t) + `dirWeight·(1−dot)·10` with `dot = dir·chordDir` (abs unless one-way). Lowest score wins.
  `InitAtSegment` (WRN.cpp:2476) heads backward if `dot(dir, segFwd) < 0` — or, with race filter, if `!RaceRouteForward` —
  and unless `forceCentre` sets the lane offset to the car's current lateral offset (`InitLaneOffset`, WRN.cpp:2359).
* **SetBoundPos** (WRN.cpp:2698): centre point = node + right·offset. Corridor edges for a cookie-trail, non-traffic nav: in the
  end profile find the drivable zone closest to `offset`, grow left/right while neighbours are drivable, then
  `left = relOff(leftZone) − w/2 + m`, `right = relOff(rightZone) + w/2 − m` with `m = vehicleHalfWidth + 1.5`
  (half width = rigid body dimension.x; +2 for a tractor in drag); if `right−left < 2m` widen both by the shortfall/2; clamp
  the centre offset into `[left+0.1, right−0.1]`. With no profile: offset ± 2.
* **SnapToSelectableLane(offset, seg, node)** (WRN.cpp:901): nearest selectable zone centre (forward lanes, and for
  racing/cop/grid/drag also backward lanes with negated offset); for racing/cop lanes the result is
  `clamp(laneCentre + clamp(offset−laneCentre, ±w/2), driveLeft+m, driveRight−m)` with the drivable span grown as above — i.e.
  racers may use the whole drivable road, not lane centres. `ChangeLanes(off, dist)` (WRN.cpp:2789): dist>0 blends linearly over
  `dist` metres (`UpdateLaneChange`, WRN.cpp:3017), else immediate (on a Decision segment only the end is moved).
* **IncNavPosition(dist, to, maxLookahead)** (WRN.cpp:1441): with a cookie trail it advances in steps of `1.1·gap`,
  `gap = clamp(maxLookahead/26, 1, 3)` (3 when maxLookahead=0), adding a cookie each step. `PrivateIncNavPosition`
  (WRN.cpp:1462): `segTime += dist/|end−start|`; on overflow choose the next segment with `GetNextOffset` and carry the rest.
  If the choice returns the same segment and node, `fDeadEnd=1`.
* **GetNextOffset(to, …)** (WRN.cpp:704, PC 0x774430): Path mode → next segment in `pPathSegments` (reverses at a node that does
  not connect; at the end of the path switches to Direction mode, except GPS). Direction mode at the arrival node:
  1. node with ≤1 segment, or no non-decision continuation while on a Decision segment → turn around;
  2. else if `GetAttachedDirectionalSegment` (first non-decision segment ≠ current, WRN.cpp:3373) exists → take it (plain road
     continuation, no choice);
  3. else (a junction) for each other segment walk ahead up to 20 segments / 100 m (stop at length budget after the first),
     rejecting the branch if a segment crosses a respected barrier, fails the race filter (`!InRace` or wrong race direction),
     traffic/cop filters, a shortcut that `MakeShortcutDecision` refuses, or meets a one-way the wrong way; track the worst
     `|dot(segDir, unit(to)) − 1|` along the walk; pick the branch with the smallest worst-gap (<2). When `to` is zero (racers
     pass zero), `to` = the current segment's end tangent, i.e. "go straightest". No branch ⇒ turn around.
  Then the lane offset is re-snapped to the new segment (`SnapToSelectableLane`).
* **Reverse()** is ignored for racer/player navs with race filter unless `IsWrongWay()` (WRN.cpp:997).

### 2.1 Cookie trail and the racing line
Each nav with a cookie trail keeps a ring of 32 `NavCookie` (0x40, WRoadNetwork.h:159): Left/Right/Forward (2-D), Length (gap
to previous), Curvature (spline curvature at the nav), LeftOffset/RightOffset (signed, relative to Centre), Flags (1 = cut by
an avoidable, 2 = cut from behind), Centre (3-D), SegmentParameter (u16 of t), SegmentNumber:15, SegmentNodeInd:1.
`UpdateCookieTrail(gap)` (WRN.cpp:1382) appends a cookie when the nav moved ≥ gap from the newest one (clears the trail if
the new ray points back, dot < −0.99); Forward = perpendicular of (Right−Left); offsets = cross(centre→edge, Forward).
`ResetCookieTrail` = clear + one cookie (gap 3). The trail therefore spans the road from the car to the nav point.

**UpdateOccludedPosition(occludeAvoidables)** (WRN.cpp:1917, PC 0x776600) — string-pulling through the corridor:
1. Advance `nCookieIndex` to the last cookie the car is past (dot(Forward, car−Centre) ≥ 0), looking up to `look_ahead` behind
   (2 → ≤8 m, grows by 2·out-of-bounds); interpolate the current cookie between it and the next (blend
   `1 − dot/(dot+|dot_next|)`; mind the Vector2-Lerp gotcha: Left/Right/Forward/Centre weight the *current* cookie by the blend,
   while LeftOffset/RightOffset use the float Lerp and weight the *next* one — copy as is).
2. `mOutOfBounds = halfWidth + max(off − RightOffset, LeftOffset − off)` (off = car lateral offset in the current cookie).
3. Copy cookies ahead into a local array; if `occludeAvoidables`, `HolePunchAvoidables` (§6.1) narrows them.
4. Funnel from the car: left limit / right limit rays to cookie edges; for each later cookie tighten the limit when an edge
   ray is inside it; the centre (or, past the last cookie, the nav position) falling outside a limit marks that limit's cookie as
   the occluder; stop when the limits cross. Track the trail AABB (for `IsPointInCookieTrail`).
5. If occluded: `apex` = car + occluder ray (y = apex cookie centre y). `nRoadOcclusion` = ±1 (−1 left) when the apex cookie is
   uncut, else `nAvoidableOcclusion` = ±1; `bOccludedFromBehind` if that cookie has flag 2. Aim point:
   `a2c = unit(apex−car)`, `n2a = unit(apex−nav)`, `d = |apex−car|`, `perp = unit(a2c+n2a)` (outward bisector),
   `proj = min(dot(n2a·d, perp), apexWidth)`; for avoidable occlusion `proj = min(proj·2·Ramp(v·a2c − trailSpeed, 0, 2·v·a2c),
   apexWidth)`; **occluded position = apex + perp·proj** (y = apex y). Not occluded ⇒ occluded position = nav position.
The racer steers at the occluded position (§4.1): it aims just outside the inside corner of the visible corridor, which is the
racing line, and around cars that cut the corridor.

**CookieTrailCurvature(carPos, carVel)** (WRN.cpp:3249, PC 0x778910): `max(apexTerm, roadTerm)`.
* roadTerm = |Σ len·avg(κi,κi−1) / Σ len| over cookies from `nCookieIndex`, each κ clamped to **±0.01** (≥100 m radius).
* apexTerm (only if >2 cookies, occluded, not from behind): `c2a = unit_xz(occluded − car)`, `dist = |…|` (>1 m), apex cookie
  `= ClosestCookieAhead(apex)`, `w = max(1, max(dist, |R−L|))`, `n2a = unit_xz(nav − apex)`,
  `s = |asin(clamp(cross(n2a,c2a)))|`, `s = π − s` if `dot(c2a,n2a)<0`; `apex = s·sin(min(s,π/2))`; avoidable occlusion:
  `apex ·= Ramp((v·c2a − trailSpeed)/(v·c2a), 0, 1)²`; `apexTerm = clamp(apex,0,π)/w`.

## 3. Route choice

* **Planned path (A*)** — `WRoadNav::FindPath(goalPos, goalDir, shortcutAllowed)` (WRN.cpp:3147) submits an `AStarSearch`
  (WPF.cpp:127) serviced by `PathFinder` (1 ms per sim tick, 3072-node pool, 16 searches). Goal = a direction nav at the goal
  (`InitAtPoint` dirWeight 0); if the goal segment is already in the cookie trail → done. Open list seeded with the current
  segment's to-node (and, except RaceRoute, its from-node). Expand node by node over its segments: skip the arrival segment,
  skip Decision→Decision (non-cop), `Admissible` (WPF.cpp:243: racers reject one-way against, either barrier bit, and
  shortcuts refused by `MakeShortcutDecision`); `g += segment length`, `h = |node − goalPos|` (costs quantised ×0.25 into u16);
  goal = the goal segment (and its arrival node when a goal direction was given). Result: up to 510 segment indices; nav type
  becomes Path (and the nav reverses if the path starts behind it). A search out of nodes returns the best partial (HALF_WAY)
  except RaceRoute (NO_WAY); RaceRoute also fails if a segment repeats. There are **no extra costs** (no traffic, lanes, turns).
* **Racer use** (Race.cpp:918): while `PlayMode == Racing` and the race has a finish line: if the AI target is valid and there
  is no route error, then when the nav is not in Path mode and no search is pending, `FindPath(target.pos, target.dir)` unless
  the target is unchanged and the goal is already in the cookie trail. Otherwise `need_to_stop = true`. The nav therefore
  follows the A* path to the target, then (path exhausted) Direction mode with the race filter (InRace + race direction) until
  the target moves and a new search starts. The target is set by `GRaceStatus` — next checkpoint / finish position and
  direction via `AITarget::Aquire(pos, dir)` (`SetNextCheckpointPos`, PC 0x5FE660) **[?]**.
* **Shortcuts** (`MakeShortcutDecision`, WRN.cpp:3100): racers roll once per shortcut per search/branch walk (cached bitmask):
  `chance = min + shortcutSkill·(max − min)` from the marker's `ShortcutMinChance/ShortcutMaxChance` (values >1 are percent ⇒
  ×0.01); `take = random01 < chance`. The shortcut the nav is already on is always allowed. Player paths take only the
  shortcut they are on. `shortcutSkill = clamp(baseSkill + glueOutput, 0, 1)` (AIVehicle.h:523, §7).
* **Look-ahead**: `L = AiNavLookAheadTable(fSpeedLimit)` = {30, 100} over 0..100 m/s ⇒ `L = clamp(30 + 0.7·v, 30, 100)` m
  (drag uses the same values) (Race.cpp:81). `UpdateNavPos` (Race.cpp:748): with `N = nav − car`, `F = unit(navFwd)` (x,z
  only) solve `|N + d·F| = L`: `a=F·F, b=2N·F, c=N·N−L²`, `d = (−b+√(b²−4ac))/2a`; if `d>0` `IncNavPosition(d, desiredDir
  (zero for racers), L)`; then `UpdateOccludedPosition(true)`. The avoidable radius is set to L (§6.1).
* **Off-path recovery** `CheckOffPath` (Race.cpp:697, 30 Hz, not in pursuit): if `mOutOfBounds > 2` build a fresh nav at the car
  (same filters, dirWeight 1); if valid, not a Decision segment, not in the cookie trail nor the path, and its out-of-bounds is
  smaller ⇒ `ResetDriveToNav(SELECT_VALID_LANE)`, Direction mode, `IncNavPosition(L(currentSpeed))`, `UpdateOccludedPosition`.
* `AIVehicle::UpdateRoads` (AIVehicle.cpp:1750) keeps separate current/future road navs with a scored road walker (start score
  `dist−2 + 3·(|Δy|−2) + 2(1−dot)`; end `+16(1−dot(target))+24(1−dot(future))+2(1−dot(dir))+2 if future segment changed +5 cop
  penalty`; future = `v·max(1,2/v)` m, target = `v·max(2,90/v)` m, rethink 0.33 s). Racers only use it for the pursuit
  turn-around; port it with the cop AI, not here.

## 4. The race action

### 4.1 AIActionRace::Update (Race.cpp:832, PC 0x428720), every think
1. `CanBeAttempted`: rigid body, engine, input present and a centre-lane direction nav at the car is valid. `BeginAction`
   (Race.cpp:257): Direction mode, Racing lane, cookie trail on and reset, `ResetDriveToNav(SELECT_VALID_LANE)` (InitAtPoint
   at the car with its heading, dirWeight 1, cancel path finding, snap lane), `ComputePotentials`, `fSpeedLimit = max(speed,0)`,
   timers 0, start the 30 Hz off-path task. `StartRace` (Racecar.cpp:429) additionally sets race filter, finds the path to the
   target and pre-advances the nav **60 m** (drag 30 m).
2. Lane type Racing (Drag in drag races); route maintenance (§3); `need_to_stop = IsDestroyed()` or no target / route error.
3. `L` and `UpdateNavPos` (§3); `SetAvoidableRadius(L)`.
4. `κ = CookieTrailCurvature(carPos, carVel)`; `skill = GetSkill()` (§7); `fPotentialSpeed = GetPotentialSpeed(κ, skill)`.
5. Speed-limit integrator (§5.3) → `fSpeedLimit`.
6. Staging (pre-start): `fSpeedLimit = lerp(2.5, 3.0, skill) mph` (1.12-1.34 m/s), drive target = nav position (not the
   occluded one); unstage timer 2.5 s counts down only above 24 m/s **[unused otherwise]**.
7. `driveTarget = occludedPosition`, `driveSpeed = need_to_stop ? 0 : fSpeedLimit`. (Pursuit/flee only: turn-around when
   speed > 40 km/h and `dot(vel, target−car) < −0.3·speed·|target−car|`.) `SetDriveTarget`, `SetDriveSpeed`.
8. NOS decision (§5.4); `DoDriving(7)`.

### 4.2 ComputePotentials (Race.cpp:408) — performance matching
Performance ratings `Physics::Info::Performance {TopSpeed, Handling, Acceleration}` (0..1, the customise-screen ratings
**[?]**). Bias `b`: career with race params: `minPerf = character.MinimumAIPerformance()` and for each axis
`b = Ramp(minPerf, playerMax, 1)` = `clamp01((minPerf − P)/(1 − P))` with P = the best player rating; all other contexts
(quick race, free roam) **b = 0**. `MinimumAIPerformance` is very likely the unnamed character float `B1ECE070` in races.md §5.1
(0.8 bin 1-2, 0.9 Razor … 0 bin 14) **[?]**. Then, with "lowest" = min over this AI and every player car:
* `mStartGrip = lerp(lowest, mine, b.Handling)` of `min(tires.STATIC_GRIP front, rear)`;
  `mEndGrip` likewise of `StartGrip·(g' + AerodynamicDownforce(chassis, topSpeed)/MASS)/g'` where `g' = −GRAVITY attr` and
  `AerodynamicDownforce = speed·2·AERO_COEFFICIENT·1000` (PhysicsInfo.cpp:10; sign convention of GRAVITY **[?]**).
* NOS: boost = `max(NosBoost−1, 0)`, `NosBoost = TORQUE_BOOST·(1 + 0.25·tuneNOS) + 1`, capacity
  `NOS_CAPACITY·(1 − 0.25·tuneNOS)`; `mNosCapability = lerp(lowestBoost, myBoost, b.Accel)`;
  `mUsableNOS = (lowestCap/myCap)·(mNosCapability/myBoost)` (1 in pursuit/flee), `mBottleTime = myCap` (0 if no NOS).
* `mTopSpeed = lerp(lowestTop, myTop, b.TopSpeed)` where top speed and the 10-entry acceleration-vs-speed table come from
  `Physics::Info::ComputeAccelerationTable(pvehicle)` (PC 0x673810, **not decompiled [?]**: build it by simulating full-throttle
  acceleration of the car model over [0, top]).
So with b = 0 an AI car never out-grips, out-accelerates or out-runs the player's car (except via catch-up, §7).

## 5. Speed control

### 5.1 Corner speed `GetSpeedLimit` (Race.cpp:475, PC 0x407940)
Lateral grip that grows linearly with speed: `μ(v) = f0 + f1·v`, `v²|κ| = g·μ(v)`, g = 9.8:
`n = g·f1 + sqrt((g·f1)² + 4·g·f0·|κ|)`, `v = n / max(n/max(0.1, top), 2|κ|)` = `min(top, n/(2|κ|))`.

### 5.2 GetPotentialSpeed (Race.cpp:485) — racing (non-pursuit)
`0` if `mTopSpeed ≤ 0` or the nav hit a dead end. `scale = AICorneringScaleTable(skill)` = lerp(0.36, 0.9, skill);
`f0 = mStartGrip`, `f1 = (lerp(StartGrip, EndGrip, scale) − StartGrip)/mTopSpeed`; `v = GetSpeedLimit(κ, f0, f1, mTopSpeed)`.
Then `v *= aivehicle.TopSpeedMultiplier · AiSpeedScaleTable(skill)` (lerp 0.85→1.0; drag same values) (Race.cpp:579-591).
(Pursuit/flee have an extra target-tracking cap, Race.cpp:503-575; out of scope.) Drag: `v = mTopSpeed·mult·scale`.

### 5.3 Acceleration model and the speed-limit integrator (Race.cpp:635, 965-997)
`potAcc = GetPotentialAcceleration(max(v, fSpeedLimit), skill, wasNOS)`:
`lerp(minAccel(all players, me), myAccel, b.Accel) · AccelerationMultiplier · AiAccelScaleTable(skill)` (lerp 0.65→1.0;
drag 0.82→1.0) `· (wasNOS ? 1 + mNosCapability : 1) · AiCatchupAcceleration(catchupCheat)` (table {1, 1.33, 1.66, 2} over
0..1) `− 9.81·forward.y` (grade), floored at 0. `myAccel = Table(mAccelData[10], 0..mTopSpeed)(|speed|)`.
`actAcc = (v − vLast)/dT`. Then:
* if `actAcc < 0` and `v < fSpeedLimit < fPotentialSpeed`: `fSpeedLimit += min(potAcc + actAcc, 0)·dT` (falling behind the
  model while braking/blocked pulls the limit down);
* if `fSpeedLimit < fPotentialSpeed`: `t = fSpeedLimit/fPotentialSpeed`, `e = lerp(1.5, 2, skill)`,
  `fSpeedLimit += clamp(potAcc − actAcc·t^e, 0, potAcc)·dT`;
* `fSpeedLimit = clamp(fSpeedLimit, 0, fPotentialSpeed)` — **drops instantly** when the curvature ahead demands it.
There is no separate braking-distance model: anticipation comes from κ being measured over the cookie trail out to L
(30-100 m) and the apex geometry. `fSpeedLimit` (not the actual speed) also drives the look-ahead table.

### 5.4 Nitrous (Race.cpp:673, 1032-1069) — not for the player
`potNOS = GetPotentialNOS(v, wasOn, skill)`: 0 if `v < 10` or `v ≥ fSpeedLimit` or no capability/usable NOS; else
`useable = mUsableNOS·lerp(0.33, 1, skill)`, `need = useable·lerp(0.5, 0.3, skill)`, or `need = 1 − useable` when already on;
bottle = engine NOS capacity fraction; `potNOS = bottle > need ? (bottle − need)·mBottleTime : 0` (seconds).
`canNOS = !turnAround && !needToStop && !staging && potNOS > 0`. `k = lerp(0.25, 1, skill)` (+1 when `CheckSpeedTraps`: speed-
trap race, a trap within `v·lerp(1,3,skill)` m ahead with `dot(fwd, dir) > 0.5`, reachable within potNOS seconds, and
`v ≤ fSpeedLimit − 5` unless already on). `speedGap = k(fPotentialSpeed − v)`, `accelGap = k(potAcc − actAcc)`.
Off → on when `mNOSTimer < −lerp(20, 4, skill)` s, nav not occluded, `speedGap > 15` and `accelGap > 0.5·potAcc`.
On → off when on for `> lerp(2, 4, skill)` s and (`speedGap < 5` or `accelGap < 0.15·potAcc`). `mNOSTimer` counts up while
on (from 0) and down while off (from 0). `SetControlNOS(want)`.

### 5.5 Throttle / brake `AIVehiclePid::OnGasBrake` (Racecar.cpp:197, PC 0x42A0B0), per sim tick
Inputs cleared (gas, brake, handbrake 0). Staging ⇒ `tb = 0.8`. Steering-behind (only possible in reverse override, §6.2) ⇒
`tb = 1` + handbrake 1. Else `e = speed − driveSpeed` recorded into `PidError(4,4,30)`; `driveSpeed < 0.5` ⇒ `tb = −1`;
reverse gear ⇒ `tb = speed > 1 ? −1 : +1`; `speed < −1` ⇒ `tb = −1`; otherwise the **incremental** PID
`tb += −0.4·e − 0.01·clamp(I, ±5) − 0.1·clamp(D, ±10)`. `tb = clamp(tb, ±1)`; gas = `max(tb,0)`, brake = `max(−tb,0)` (both
capped at 1). `tb` persists between ticks (reset to 0 on `Reset`). The adaptive throttle controller exists but is disabled
(`bAdaptiveGasBrake=false`). Handbrake is otherwise never used by racers (busted cars: brake 1 + handbrake 1, AIVehicle.cpp:2152).

`PidError(nI, nD, freq)` (Misc/Table.hpp:250, Table.cpp:239): `Record(e, dt)`: `ΔE = e − ePrev`; integral sample
`dt·(ePrev + ΔE/2)`; derivative sample `ΔE/dt`; both pushed into ring averages of nI / nD slots, dt into an nI-slot ring.
`GetError = e`, `GetErrorDerivative = mean of derivative samples`, `GetErrorIntegral = Σintegral·n/(freq·Σdt)` (n = samples).

### 5.6 Steering `AIVehiclePid::OnSteering` (Racecar.cpp:290, PC 0x42A390), per sim tick
In reverse override it uses the simple `AIVehicle::OnSteering` (§6.2). Otherwise: skip if `driveSpeed == 0` and planar speed < 1.
`dir = unit_xz(dest − pos)`, `fwd = unit_xz(bodyForward)`; heading = blend of fwd and unit velocity by `clamp(speed,0,1)` (only
recorded, weight 0). `bodyErr = asin(clamp(cross(fwd,dir).y, ±1))` into `PidError(5,5,30)`. Terms: `P = err`,
`I = clamp(∫, ±0.5)`, `D = clamp(d/dt, ±10)`. Controller `AdaptivePIDControllerComplicated(MIT rule, window 0.1)`,
time slice 0.1, adaptation gains 1e-5, tuning threshold 0.01, coefficient clamps P [0.4, 1.0], I [0.01, 0.1], D [0.1, 0.4]
(drag human: P [0.6,1.2], I [0.05,0.5], D [0.02,0.6]). Each tick:
`model = HeadingErrorModelGraph(|err|°)` with points (0,0) (3,−1) (6,−2.05) (9,−3.5) (12,−4.94) (15,−6.14) (18,−7.35) (21,−8.55)
(24,−9.4) (27,−10) (deg → deg/s of desired closing rate); `actual = deg(err·D ≥ 0 ? |D| : −|D|)`; `Update(model, actual)`:
`modelError = actual − model`; the term tuned this tick is `floor(worldTime/0.1) mod 3` (P,I,D); if `|term| ≥ 0.01`:
`coef += dt·(−gain·modelError·S)` clamped, `S = clamp(avgModelErrorDeriv/avgCoefDeriv, ±1000)` (if avgCoefDeriv≈0: sign of
avgModelErrorDeriv, 0 within 0.001); coefficient-derivative and model-error-derivative histories are `AverageWindow(0.1 s, 45 Hz)`
= 5 slots, time-stamped with world time (PID.cpp:209-283, Table.cpp:211). Below **10 m/s** the coefficients are forced each tick
from speed tables over 0..160 m/s (10 entries): P {0.328, 0.22, 0.148, 0.115, 0.09, 0.074, 0.057, 0.049, 0.043, 0.04},
I {0.21, 0.244, 0.267, 0.29, 0.305, 0.321, 0.328, 0.336, 0.341, 0.344}, D {0, 0.0758, 0.0758, 0.0738, 0.0604, 0.047, 0.047,
0.0403, 0.0403, 0.0403} (initial coefficients are 0; they first enter the clamps on their first tuned update above 10 m/s).
`out = Pc·P + Ic·I + Dc·D`; `steer = out / rad(maxSteeringDeg)` (suspension max steering); in reverse gear
`steer = speed < 0 ? (steer < 0 ? +1 : −1) : 0`; clamp ±1. `GetOverSteerCorrection` returns 0 in the decomp
("dwarf suggests more", AIVehicle.cpp:836) **[?]**.

### 5.7 Reverse gear `AIVehicle::OnReverse` (AIVehicle.cpp:805), flag 4
Skipped in reverse override. If in a forward gear and speed ≥ 15 m/s → nothing. Else `facing = dot(fwd3, unit(dest − pos))`:
in reverse and `facing > 0` → shift 1st; in a forward gear and `facing < −0.707` → shift reverse.

## 6. Racer behaviours

### 6.1 Traffic / opponent avoidance = corridor hole-punching (`HolePunchAvoidables`, WRN.cpp:1697, PC 0x775BE0)
The decomp of this function is marked unfinished (non-matching) — treat constants as good, control flow as **[?]**; verify
against PC 0x775BE0. Avoidables = this AI's neighbour list (`AIAvoidable`, ≤32), each with sweep radius
`max(avoidRadius, 2·speed)` (AIVehicle.cpp:581; the race sets avoidRadius = L); trailers hitched to me skipped. For each
avoidable within ±5 m height of my current cookie (traffic trailers seen side-on by a racer are treated as a 1.8 m box 6 m
behind the cab):
* extents along my cookie forward: `his = dimX + 0.5 + |F·hisFwd|·dimZ + |F·hisRight|·dimX`, `mine = myDimX + 0.5 + myDimZ`;
  `ahead = (his − me)·F`; side extent `|myRight·hisFwd|·hisDimZ + myDimX + |myRight·hisRight|·hisDimX`.
* cut flag 2 ("from behind/alongside") when `ahead − his ≤ mine` (at ≥ 20 m/s; below 20 or traffic nav: `ahead + his < mine`).
  Skip if `ahead < −(mine+his)` or (`ahead + his < 0` and side gap < side extent).
* time to closest approach between the noses (`TimeToClosestApproach`, 3.0 if no relative motion); without flag 2 skip if not
  closing and `ahead > mine + his`; skip if approach time ≥ 3 s.
* impact point = his position + F·(trailingSpeed·t − (mine+his)); its cookie `c`; predicted lateral drift
  `Δ = cross(hisVel, F)·clamp(t,0,1)`: cut centre moved 0.8Δ sideways, extra half-width 0.2Δ·closeFactor,
  `closeFactor = Ramp(hisAhead − myAhead, −6, 6)`. Half width = max |projection of his box diagonals| + extra.
  My expected offset `cur + t·closeFactor·0.2·Δmy + 2·cross(navF, F)`. Gaps right/left of him vs `closeFactor (·0.8 in drag) +
  halfWidth`; pass on the side that fits, else the side my offset is already on.
* `CookieCutter` from cookie `c` onward (until the cookie is `2(mine+his)` past the impact): move that cookie's Right (pass
  left) to `max(L+1, l − proj)` or Left to `min(R−1, l + proj)` (min width 0.1 for traffic), `proj = halfWidth + myHalfWidth +
  closeFactor`; set flags 1|cut. The nearest cut ahead sets `fOccludingTrailSpeed = his speed along F`.
* `ClampCookieCentres`: cut cookies get centre = mid(L,R) and symmetric offsets.
Effect: the funnel apex moves round the other car (overtake on the free side), `IsOccluded` blocks NOS, and the apex
curvature term (scaled by the closing ratio²) slows the car when the gap is closing. No dedicated overtake/block/draft logic
exists. `AISteer::VehicleSeperation` (capsule repulsion, AI/Common/AISteer.cpp:247) and the `AiSeparation*` tables
(Race.cpp:88-97) are **not used** by racers.

### 6.2 Getting unstuck (AI/Actions/AIActionGetUnstuck.cpp)
`CanBeAttempted` (each think, while racing): when gas input ≥ 0.5: start/accumulate a timer at the current position (restart
while staging); after **3 s**, if moved < **3 m** ⇒ `SetReverseOverride(2 s)` (shift to the opposite of the current gear),
`ResetDriveToNav(SELECT_CENTER_LANE)`, and the action (score 1) pre-empts the race. Gas < 0.5 resets the timer.
`Update`: `driveSpeed = 15`, flags steer + gas/brake (no reverse flag). Steering then uses `AIVehicle::OnSteering`
(AIVehicle.cpp:841): `steer = asin(cross(fwd,dir).y)/rad(maxSteer)`, negated in reverse; if `dot(dir,fwd) < −0.2` in a forward
gear: full lock and `mSteeringBehind` (⇒ gas 1 + handbrake 1). Gas/brake is the PID one: in reverse, gas 1 until moving.
Finished when the 2 s override expires (`UpdateReverseOverride` shifts back to 1st) ⇒ `ResetDriveToNav(SELECT_VALID_LANE)`, and
`AIActionRace` is re-chosen (BeginAction resets the nav and potentials).

### 6.3 Airborne
`AIActionAirborne` (zero all inputs after 1.5 s with no wheel on the ground) is **not** in `AIGoalRacer`; racers keep driving in
the air. (Its timer never resets — irrelevant for racers.)

### 6.4 Resets
* `ResetCar` behaviour (Physics/Behaviors/ResetCar.cpp, applied to racer vehicles **[?]**): records a reset cookie (ring of 4)
  whenever all wheels are on valid ground and the car is > 2 m from the last one; resets when flipped for > **4 s**
  (`up·groundNormal < 0.5` and `up.y < 0.5` while not all wheels down; humans also when ≥2 tyres are off-surface). Reset =
  oldest cookie snapped to the road (`kLaneReset` nav, race filter while racing, |Δy| ≤ 6, not across an enabled barrier,
  world height within 3.5 m), facing the race direction, invulnerable 2 s. Reset-to-point track zones override.
* Off-world simple physics (`AIVehicleRacecar::ShouldDoSimplePhysics`, Racecar.cpp:542): when the car is `IsOffWorld()`
  (no streamed collision) and not staging/animating/player ⇒ physics mode EMULATED and `UpdateSimplePhysics`
  (AIVehicle.cpp:1217): move towards `dest + (0,1,0)` at a speed that drops at 30 m/s² or rises by
  `accelTable(v)·lerp(0.5,1,skill)`, capped by `driveSpeed` and `topSpeed·lerp(0.75,1,skill)` (×−0.5 in reverse); orientation
  from the ground normal (if y ≥ 0.707) else world up; height = ground (or the current cookie centre + 1 off-world) +
  max ride height + dimension.y. Leaving: `SetVehicleOnGround` keeping speed, 1 s invulnerability. Catch-up glue is not
  damped in this mode (§7).
* Unstuck is not a reset; there is no "teleport ahead" for racers.

### 6.5 Collisions and aggression
Racers have no ram/block behaviour. Contact response with the player comes from the `aivehicle.PlayerCollisions[]` list
(goal → `collisionreactions` collection) or `PlayerCollisionsDefault` (AIVehicle.cpp:700-722) applied to the rigid body —
see the physics/collision spec **[?: fields not traced]**. Hitting traffic only feeds pursuit/infraction counters.

## 7. Skill and catch-up (AIVehicle.cpp:1976-2305)

* Base skill (`ComputeSkill`, PC 0x408E30), computed at `PrepareForRace` / `SetRacerInfo`; player = 1:
  Quick race: `{0.15, 0.4, 1.0}[difficulty]` with catch-up on, `{0.15, 0.4, 0.8}` with it off (difficulty index per
  races.md §5.2). Career: `cs = clamp(SkillLevel·0.01, 0, 1)`, `d = GRaceStatus::GetAdaptiveDifficulty()` (PC 0x5DC180,
  not decompiled **[?]**, assume 0), `base = clamp(cs + d·(d > 0 ? Up(cs) : Down(cs)), 0, 1)`, Up = {0.5, 0.75, 1.0},
  Down = {0.5, 0.375, 0.25} over cs 0..1. Other contexts: 0.
* Glue (each think while racing and not staging, AI only): every 1 s record into `pGlueError = PidError(10, 5, 1.0)`:
  `e = (raceLength/100)·(avgPlayerPct − myPct)` metres (**positive = AI behind**), `·= 1 + 0.5·(1 − playerPerf.TopSpeed)`.
  Then `ComputeCatchUpSkill(racerInfo, pGlueError, &glueOutput, &glueSkill, offWorld)` (PC 0x5FF990) = races.md §5.3, whose
  unknown inputs are now identified: `t` = racer's `PctRaceComplete` (GRacerInfo+0x18), `e` = `pid.GetError()` (+0x7C),
  `dv` = `pid.GetErrorDerivative()` (+0x5C), `I` = `pid.GetErrorIntegral()` (the n·+0x30/(+0x78·+0x8) formula),
  output `u` → `glueOutput`, `u·skillCurve` → `glueSkill`. Returns false ⇒ both 0.
  Damping when not off-world: `glueSkill > 0`: ×0.5 in speed-trap races (FUN_00404af0 = IsSpeedTrapRace) else
  ×CatchupGlueTable(base) = {0.33, 0.66, 1.0}; `glueSkill < 0`: ×0.5 speed trap else ×SlowDownGlueTable(base) = {1, 1, 0.66}.
* Use: `GetSkill = clamp(base + glueSkill, 0, 1)` → every skill table in §4-5 (speed ×0.85-1.0, accel ×0.65-1.0, cornering
  downforce share 0.36-0.9, accel-limit exponent 1.5-2, NOS availability/timing, staging speed, off-world speeds).
  `GetCatchupCheat = clamp(base + glueSkill − 1, 0, 1)·0.5` → `AiCatchupAcceleration` (max ×1.5). `GetShortcutSkill =
  clamp(base + glueOutput, 0, 1)` → shortcut chance. So rubber-banding is a skill shift, not a direct top-speed multiplier.

## 8. Deviations in current Unity code (AIDriver.cs, RaceManager.cs, Core/RoadNetwork.cs)

1. **Data**: RoadNetwork.cs reads only RNnd positions and RNsg endpoints; it ignores RNhd/RNpf/RNrd, node profile index (its
   comment calls +0x0E "?" and +0x10 "flags"), segment length, flags, handles/curves, lanes. Graph edges are undirected node
   pairs: no one-way, barriers, InRace/race direction, Decision→Decision rule, shortcut handling, or road widths.
2. **Route**: one polyline built once at race start (`RouteThrough` checkpoints via node-to-node A*, nearest node snapping,
   ad-hoc pruning near waypoints); the game keeps a per-racer nav that replans to the current target with segment-length A*
   and otherwise follows InRace segments in race direction, choosing lanes across the whole drivable width.
3. **Line/steering**: pure pursuit to a point `clamp(0.7v, 8, 34)` m ahead on the centre polyline, `steer = angle/28°`. Game:
   look-ahead `30 + 0.7·fSpeedLimit` (30-100 m), aim at the funnel apex of the cookie corridor (racing line), adaptive PID on
   the body angle error scaled by the car's max steering angle, run per sim tick.
4. **Corner speed**: angle-sum heuristic `MaxSpeed·(1 − bend/115°·CornerGrip)`, min 13 m/s, plus a polyline "near corner"
   scan. Game: `GetSpeedLimit` from tyre static grip + downforce share, curvature from cookie-spline curvature (±0.01 clamp)
   and apex geometry, then the acceleration-limited `fSpeedLimit` integrator.
5. **Throttle/brake**: `clamp01(err/3 + 0.55)·Aggression`, brake only when 3 m/s too fast and reduced by steer. Game:
   incremental PID (0.4/0.01/0.1) on `speed − driveSpeed`, full brake when `driveSpeed < 0.5`, 0.8 throttle when staging.
6. **NOS**: `err > 15 && |angle| < 8° && Aggression > 1`. Game: bottle/time/skill-gated logic of §5.4 with speed-trap boost.
7. **Avoidance**: two ±8° raycasts adding ±0.35 steer and a follow-speed cap. Game: corridor hole-punching with 3 s
   time-to-approach and side choice (§6.1); no raycasts against walls (walls are the corridor edges; `GetWorldAvoidanceInfo`
   exists but racers do not call it).
8. **Strength**: `MaxSpeed = 62 + 1.4·(16−bin)`, `CornerGrip = 1.05·skill`, `Aggression = skill`, opponent upgrades by bin.
   Game: base skill from SkillLevel / QR difficulty, performance matched to the player's car (QR: AI capped at the weaker of its
   own and the player's car; career raised towards `MinimumAIPerformance`), `TopSpeedMultiplier`/`AccelerationMultiplier`.
9. **Catch-up**: `MaxSpeed·(1 + 0.25·CatchUp)` (a guess); RaceManager.CatchUp differentiates the gap per frame, has no
   integral, no 1 s sampling, no player-top-speed factor, no base-skill damping tables, no speed-trap halving, and feeds the
   AI's own completion as `t` (correct) but the gap from the player only. Game: §7 (skill shift + accel cheat + shortcut skill).
10. **Stuck**: `|v| < 1.5 && throttle > 0.2` for 3.5 s ⇒ teleport 6 m ahead. Game: gas ≥ 0.5 for 3 s and moved < 3 m ⇒ 2 s
    reverse at 15 m/s target with simple steering, then nav reset; teleports only via ResetCar (flipped > 4 s).
11. **Off-world**: kinematic "ghost" when no ground raycast, moving at `0.75·MaxSpeed` along the polyline. Game: emulated
    physics towards the nav aim point with the skill-scaled accel/top-speed limits of §6.4.
12. Missing: reverse-gear logic (§5.7), staging creep, wrong-way handling, shortcuts, `need_to_stop` when the route fails,
    off-path nav reset (§3), nav dead-end stop, handbrake/busted handling, 60 Hz think vs per-tick drive split.
13. Finish: AIDriver brakes to 0 in the last 8 m of the polyline; the game keeps racing to the target (the race status
    decides finishing) and only stops when there is no target.

## 9. Port plan

1. **RoadNetwork reader**: parse RNhd/RNnd/RNsg/RNpf/RNrd fully (§1.2) with flags, profiles/lanes, handles, Bezier
   evaluation (+tangent, XZ curvature) and a segment spatial hash (or CGrd/CGcn); fix the RoadNetwork.cs comments; unit-check
   index fields, len/chord ≈ 1, Σdegree = 2·segments.
2. **Runtime flags**: barriers (TrackPathBarrier chunk 0x3414D + exemptions), shortcuts (`nShortcut`), race route flagging
   (`AddRaceSegments` from a RaceRoute A* through the checkpoints — reconstruct GRaceStatus or use the checkpoints in order).
3. **WRoadNav**: FindClosestSegmentInd/InitAtPoint, SetBoundPos corridor, Snap/ChangeLanes, PrivateIncNavPosition +
   GetNextOffset (filters, junction walk, shortcuts), Reverse/IsWrongWay. Debug-draw nav, corridor edges.
4. **PathFinder**: A* exactly as §3 (sliced per tick or immediate), path following in GetNextOffset, GetPathDistanceRemaining.
5. **Cookie trail**: UpdateCookieTrail, UpdateOccludedPosition (funnel, apex, aim point), CookieTrailCurvature; debug-draw
   cookies/apex/aim point.
6. **Helpers**: Table, Graph, Average, AverageWindow, PidError, AdaptivePIDControllerComplicated (bit-exact).
7. **AIActionRace**: ComputePotentials (needs a ComputeAccelerationTable equivalent from the car model and performance ratings),
   GetSpeedLimit/GetPotentialSpeed/GetPotentialAcceleration, the fSpeedLimit integrator, NOS, staging, CheckOffPath.
8. **AIVehiclePid** OnSteering/OnGasBrake/OnReverse per physics tick; think at 60 Hz (rate 0.5) with staggering.
9. **Skill/catch-up** (§7, using races.md §5.3 curves) and shortcut decisions; replace RaceManager.CatchUp and the
   MaxSpeed/CornerGrip/Aggression knobs.
10. **HolePunchAvoidables** + an avoidable neighbour list (verify against PC 0x775BE0 first).
11. **GetUnstuck**/reverse override, ResetCar, off-world simple physics; then remove AIDriver's raycasts, teleport and ghost.
12. Validate: lap times vs the original on a few career races at QR Easy/Medium/Hard, corner entry speeds, shortcut rates.

## 10. Open items
* `ComputeAccelerationTable` (0x673810), `GRaceStatus` route building / target updates / adaptive difficulty (0x5DC180,
  0x5FE660), `USpline` basis and curvature (0x5D0BF0, 0x5D2E60), `HolePunchAvoidables` exact flow (0x775BE0),
  CARP group-entry semantics and `CGrd` layout, lane-type numbering names, whether `OnDriving` runs at 120 Hz.
* Round 4: `USpline` read from the exe (0x5D0BF0 BuildSplineEx stores [start; start+startControl; end+endControl; end];
  0x5D0C70 EvaluateSpline with the Bezier basis 0x8A3180 = [-1 3 -3 1; 3 -6 3 0; -3 3 0 0; 1 0 0 0]; 0x5D0D10 / 0x5D0D80 first
  and second derivative bases 0x8A3200 / 0x8A3280; 0x5D2D30 EvaluateTangent = unit derivative; 0x5D2E60 EvaluateCurvatureXZ =
  (x′z″ − z′x″)/|d1_xz|³, **1000 when |d1_xz|³ ≤ 0.0001**): RoadGraph.Point / Tangent / Curvature match (the cusp case now
  returns 1000). The 0x8A31C0 / 0x8A3240 / 0x8A32C0 bases are the Catmull-Rom set (kSpline_CatMulRom, unused by roads).
  WRoadNav queries added to RoadNav.cs: IsWrongWay (:2532), IsPointInCookieTrail (:3328), IsSegmentInCookieTrail (:3348),
  IsSegmentInPath (:3361), GetPathDistanceRemaining (:3173, shortcut road scale), InitFromOtherNav (:2335), IsOnLegalRoad
  (:3076), GetShortcutNumber (:3050), GetRoadSpeechId (:3033; WRoad nSpeechId now in Core/RoadNetwork.RoadInfo). Dead on PC
  and not ported: `WRoadNav::PullOver` (AIActionTraffic::ShouldPullOver returns false), the gradual lane change
  (`ChangeLanes(offset, dist)` / `UpdateLaneChange`: every caller passes 0). `InitAtSegment(seg, lane, t)` (the drag
  `trafficspawntrigger` cars, AICopManager::SpawnPatrolCar's segment pick) is still only reachable through InitAtPoint.
  Cops chasing AI racers: ai_cops.md §9.3 (`AIRacer.Heat`, `LastTrafficHitTime`, `ForceStop`). The 1 s
  `INVULNERABLE_FROM_PHYSICS_SWITCH` after simple physics is applied through `PlayerCar.SetPhysicsMode` (§11.3).
* Added in round 3: the opponents' vehicle model (§11), the GPS (§12), off-world simple physics (§11.3). Still open: AI racers
  chased by cops (`race_01..10`, CanPursueRacers; ai_cops.md §11), `AIVehicle::GetOverSteerCorrection` (returns 0 in the
  decomp), the 1 s `INVULNERABLE_FROM_PHYSICS_SWITCH`.

## 11. Opponents on the exe's vehicle model (round 3)

### 11.1 What the exe builds
`PVehicle::LookupBehaviorSignature` (0x67F0D0, read from the exe; traffic_physics.md §1) overrides the pvehicle's
`BEHAVIOR_MECHANIC_*` for a **CAR-class vehicle whose driver is RACER (3), NONE (4) or REMOTE (6)**: SUSPENSION =
**SuspensionSimple** (0x67F322-0x67F35F), whatever the pvehicle names (all 56 racer pvehicles name SuspensionRacer). With driver
style DRAG (`+0x144 == 1`) ENGINE = **EngineDragster** (0x67F2E7) and DAMAGE = **DamageDragster** (0x67F2B9) on a CAR. So an AI
opponent runs RBVehicle + SuspensionSimple + EngineRacer (EngineDragster in drag) + DamageRacer + ResetCar, like the cops'
chassis, not the player's SuspensionRacer sim. The AI driving (§4-7) is unchanged.

EngineDragster for a non-player owner (`Tweak_AllowAIPerfectShift` false, EngineRacer.cpp:1355-1645) is EngineRacer except:
`OnGearChange` refuses reverse (`bUseReverseInDrag` false) and, while staging, anything but first / neutral; `UseRevLimiter` =
IsStaging (MAX_RPM otherwise, EngineRacer.cpp:1111); the shift boost (1 without a perfect shift) and the heat (only DRIVER_HUMAN
builds it) leave the torque unchanged.

`AIVehicleRacecar` is the `ICheater`: EngineRacer reads `GetCatchupCheat` for the NOS drain (×lerp(1, 0.5, cheat)), the recharge
(×(1 + cheat)) and the drive torque (×(1 + 0.5·cheat), `Tweak_CheaterTorqueBoost` 0x8933D4).

Steering: `AIVehiclePid::OnSteering` divides by `ISuspension::GetMaxSteering` = SuspensionSimple's current 45..60° (it then
applies `STEERING` itself), so the commanded wheel angle is `PID output × STEERING`.

Reverse on the EngineRacer gearbox (it never selects reverse by itself): `SetReverseOverride` (AIVehicle.cpp:1012) shifts to the
opposite of the current gear; `UpdateReverseOverride` (:1023) shifts back to first when the 2 s run out; `OnReverse` (flag 4;
AIActionRace calls `DoDriving(7)`, Race.cpp:1070) shifts to reverse below 15 m/s when the target is behind (facing < −0.707)
and back to first when it is in front; `OnGasBrake` in reverse gear: brake above 1 m/s forward, else gas.

### 11.2 Port (`Runtime/Game/AIRacer.cs`, `TrafficPhysics/*`)
* `AIRacer.UseSimpleChassis` (default **true**; false = the old PlayerCar racer model). `AIRacer.Awake` → `AttachChassis()`:
  `VehicleSpecs.ForRacer(pvehicle, car spec, drag)` (a per-car copy of the pvehicle record with the racer's own upgraded CarSpec,
  Suspension = SuspensionSimple) → `TrafficVehicle.Attach` (the PlayerCar keeps input, audio, DamageRacer, ResetCar and lookups
  and is disabled; rpm, gear and wheels are bridged as for the cops), DriverClass RACER. RaceManager needs no call:
  `SpawnOpponent` adds the component to the spawned car. A human's car (Player, Player2) is never switched.
* `TrafficVehicle`: DriverStyle = DRAG when `PlayerCar.Dragster` (RaceManager sets it for drag races) → `EngineRacer.Dragster`
  and SuspensionSimple's drag rules; a body moved by more than its speed allows since the last step (PlayerCar.Teleport: CarReset,
  the rolling start, RestoreStartPositions) is reset and speed-matched (PlaceObject + MatchSpeed) `[ours]`; CarContacts gets the
  pre-step velocity and heading (its head-on rule) as from a PlayerCar.
* `AIRacer.DriveSimple`: §5.5/5.6 on the new gearbox plus OnReverse, the reverse override's gear shifts and AIVehicle::OnSteering
  in the override (steering-behind → gas + handbrake); NOS from EngineRacer's tank; the catch-up cheat into EngineRacer.

### 11.3 Off-world simple physics (Racecar.cpp:542-575, AIVehicle.cpp:1214-1364)
`ShouldDoSimplePhysics`: `IsOffWorld()` and not animating / staging / the player → PHYSICS_MODE_EMULATED; each AI update
`UpdateSimplePhysics`: towards `dest + (0,1,0)`, speed falls at 30 m/s² to the drive speed or rises by
`accelTable(v)·lerp(0.5, 1, skill)` (`Tweak_OffWorldAccel`), capped by `topSpeed·lerp(0.75, 1, skill)` (`Tweak_OffWorldSpeed`),
×−0.5 in reverse; upright (the ground normal only when on the world); height = the nav's current cookie centre + 1 (the world
height when on the world) + the largest ride height + dimension.y; velocity = dir·speed, no spin. Leaving: `SetVehicleOnGround`
keeping the speed, `INVULNERABLE_FROM_PHYSICS_SWITCH` for 1 s. Port: `AIRacer.SimplePhysics` at the 60 Hz think; off-world = no
world-collision ground within 60 m below (twice a second) `[?: PVehicle::IsOffWorld]`; the body is kinematic while emulated
`[ours]`; "current cookie" = the cookie nearest the car `[?]`; leaving calls `PlayerCar.SetPhysicsMode(Simulated)`, which sets
the 1 s `INVULNERABLE_FROM_PHYSICS_SWITCH` (round 4; CarContacts lets the car pass through other cars for that second).

### 11.4 Unverified
* Corner speeds and lap times on the new model (the §4.2 potentials come from the car spec, the grip from SuspensionSimple). The
  CareerRaceRuns assertion (opponents > 250 m in 35 s) depends on it.
* AI NOS flames (PlayerCar.NosFiring), wheel-slip skid marks and tyre audio are stale for opponents, as for cops: the disabled
  PlayerCar's `SetExternalSimState` carries rpm, gear, wheels down and skid only.

## 12. The GPS (AI/Gps.cpp; speed.exe 0x41A590-0x440255)

Functions: ctor 0x41A5E0 (arrow model `MARKER_DIRECTION_AID`, task "WorldUpdate" rate 1, nav kTypePath / kPathGPS / kLaneRacing,
look-ahead 20, max deviation 200), Render 0x41A7D0, GPS_Disengage 0x41ACE0, GPS_IsEngaged 0x41AD00, RenderGpsArrows 0x41AD20,
Construct 0x424380, Update 0x42C390, Engage 0x42C6C0, GPS_Engage 0x42C830, OnTask 0x42FBB0. Update and Engage were checked against
the PC code: the decomp matches (20 / 80 pushed, ×3 at 0x890604, ×10 at 0x890BF8, arrived 30 at 0x890614, maxDeviation < 0.1 →
200 = 0x43480000, ε 1e-6 at 0x890518).

* **Engage(target, maxDeviation)**: state DOWN, the path cancelled, `mDrawn` / `mScale` / `mDeviation` 0; the first player's car:
  `InitAtPoint(pos, forward, centre lane, 1)`; invalid → false; `FindPathNow(target)` fails → false; `OnPath()` → position and
  direction from the nav, TRACKING, look-ahead 20, true; else false.
* **Update(dT)** (every sim tick): DOWN → nothing; no player → DOWN. `InitAtPoint(pos, heading, centre, 1)` (the path stays);
  `OnPath()` → TRACKING, else SEARCHING with `InitAtPath(pos)` (the closest point on any path segment; invalid → **EGPSLost**,
  DOWN). Look-ahead target `clamp(v·pathDir·3, 20, 80)`, approached at 10 m/s; `IncNavPosition(lookAhead)`; position and
  direction = the nav's. Within **30 m** of the target → **EGPSFinished**, DOWN. SEARCHING and farther than maxDeviation from
  the nav point → EGPSLost, DOWN; else `deviation = distance / maxDeviation`.
* **The path** (`FindPathNow` → `AStarSearch`, WPathFinder.cpp:127-470) for kPathGPS: the goal segment from a direction nav at
  the goal (same filters; the WRoadNav defaults are all off); the goal on the nav's own segment is FULL_WAY with no solution, so
  Engage fails; states (node, arrival segment) from **both ends** of the current segment (not a race route), cost = segment length,
  estimate = straight distance to the goal; `Admissible(kPathGPS)`: one-way roads only forwards, no barrier / drive-through
  barrier; no Decision → Decision step; a solution rooted at the far end turns the nav round (`Reverse`); a GPS search ending
  HALF_WAY keeps no path (:447). `OnPath` (WRN.cpp:671): the nav's segment is on the path and the next path segment leaves the
  node it heads for (false on the last path segment). `GetNextOffset` with a path: the next path segment, or a U-turn on this one
  when the next does not leave this node; off the path or at its end a **GPS** nav returns its own segment (it holds, fDeadEnd).
  `InitAtPath` / `FindClosestOnPath` (WRN.cpp:2545-2616): direction reversed when the found segment's A end joins the next path
  segment or its B end the previous one.
* **Render** (view 1 only; not with an NIS, paused, or a non-drive camera): the point to aim at = nav position + direction × the
  camera's speed (1 s ahead); the arrow sits at a fixed point in the camera, `(0, sin(−hf/2)·2cos hf, cos(−hf/2)·2cos hf·alt)` in
  camera space (hf = half the camera FOV, halved again in split screen; alt 1.5 in view mode 2 with the rear-view mirror on); its
  heading `atan2` of (arrow − point) in the ground plane turns at `lerp(0.125, 1, ramp(gap, 0, π))` turns per second (snapped on
  the first frame); the model matrix rows are (left, up, ray)·0.2·pulse, pulse `1 + 0.1·sin(2π·s)` with s cycling at
  `lerp(1.5, 3.5, deviation)` per second while SEARCHING and decaying at 1.5/s otherwise.
* **Events**: EGPSFinished (dtor 0x62CE70) does nothing; EGPSLost (dtor 0x6409A0) shows "GPS connection lost" (0xEE236ED9) on the
  first player's HUD. Callers: Game.NavigatePlayerTo 0x605360 (WorldMap::SetGPSing + GPS_Engage), Game.ShowGPS(false) 0x605340
  (GPS_Disengage), the world map's GPS button (GPS_Engage(icon, 0); false → "Can't establish GPS connection" 0x7AFDF4CC), the warp
  and rap-sheet jumps (GPS_Disengage).
* **Port**: `Runtime/Game/Gameplay/Gps.cs` (`GpsNavigation`: Engage → bool, Disengage, Engaged, Mode, Finished / Lost events),
  `Runtime/Game/RoadNavGps.cs` (FindPathGps, OnPath, InitAtPath, the path branch of RoadNav.NextSegment). The arrow is the
  InGameA.bun solid (harness `aiprobe solid GLOBAL/InGameA.bun MARKER_DIRECTION`: 248 vertices, ±1.1 × 0.3 × 3 m) drawn with
  Graphics.DrawMesh on the main camera after the camera rigs (`ChaseCamera` = the drive camera). `[?]`: the exe's FOV taken as
  the horizontal one; camera-space y taken as down (the arrow above the view axis); view mode 2's 1.5 not mapped. `[ours]`: the
  path ahead is also put on the minimap (`Minimap.Gps`, twice a second).
* Hooks for other owners: `WorldMapScreen` (front end) still runs its own `GpsDrive`; the exe's button is
  `GpsNavigation.Engage(iconPos, 0)` with the 0x7AFDF4CC dialog on false. `Story.GpsDrive` (the non-script safehouse errand) can
  call `GpsNavigation.Engage(Target, 2000)` instead of drawing its own route.
