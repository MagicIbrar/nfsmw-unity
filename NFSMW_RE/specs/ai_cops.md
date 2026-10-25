# Police AI: cop cars, roadblocks, spikes, helicopter, spawning (NFS MW 2005 PC v1.3)

Companion to `pursuit.md`. That spec covers heat tables, busted/evade/cooldown, hiding, bounty and infractions.
This one covers **how the police vehicles behave**. Section numbers like "P§6" refer to `pursuit.md`.

Sources: the CC0 decompilation, `refs/nfsmw/src/Speed/Indep/Src/`. File:line references below are relative to that folder
(`AI/Common/AIPursuit.cpp:1732` and so on). Marks: **[C]** = read from the decomp, **[D]** = data (values in pursuit.md),
**[I]** = inferred, **[?]** = unknown or unverified. Units: metres, m/s and seconds unless a field is named in km/h or mph.
`KPH2MPS(x) = x/3.6` and `MPH2MPS(x) = x*0.44704`. An "angle in rev" is a fraction of a full turn
(`UMath::RotateInXZ` uses `Cosa/Sina`, so 0.25 = 90°).

Coordinates: offsets are in the frame of the **target** (the player). `x` = lateral, `z` = along the heading (+ = ahead,
− = behind). The side vector is `side = (fwd.z, 0, −fwd.x)`. Convert to Unity with the project's `Coords`, and check
whether the lateral sign mirrors **[?]**.

---

## 0. Corrections to pursuit.md (found while reading the decomp)

| pursuit.md says | decomp says |
|---|---|
| Formation ids: 4/5 = "follow/herd", 6 = "side line", 7 = "sandwich" | `FormationType` (`AI/aireflectedtypes.h:16`): 1 PIT, 2 BOX_IN, 3 ROLLING_BLOCK, 4 FOLLOW, 5 HELI_PURSUIT (uses Follow slots), 6 HERD (the 3-car side line), 7 STAGGER_FOLLOW (the front-and-back sandwich) |
| Formation re-pick gated by "15 s since `[perp+0x118]`" | Gated by `speech_finished`: more than 15 s since SoundAI focus was 1 (always true in pursuit races or with speech off), `AIPursuit.cpp:1725-1733` |
| Formation pick: "weighted, default 7" | On **even** attempt counts it always picks STAGGER_FOLLOW for `StaggerFormationTime`. On **odd** counts it picks weighted from `CopFormations`. With an active, undodged roadblock whose cops are undamaged it picks FOLLOW. The next pick comes after `duration + rand[0,3)` (`:1732-1774`) |
| HeavySupport types 3/4 unknown; 4th int unknown | `HeavySupportStrategy` 1 E_BRAKE, 2 COORDINATED_E_BRAKE, **3 RAM** (2 SUVs head-on), **4 HEAVY_ROADBLOCK** (4 SUVs as a roadblock). The 4th int is `ChanceBigSUV` (% `copsuv` (Rhino) versus `copsuvl`) |
| AirSupport type 0/1/2 | `AirSupportStrategy` 0 HI_PATROL, 1 PURSUIT, 2 SKID_HIT, 3 SPIKE_DROP. Only SKID_HIT is read (`AIPursuit::SkidHitEnabled`, `:2523`). Heli spike drop has **no code**. AirSupport Chance/Duration are never read **[C]** |
| LeaderSupport 5 = Cross | 5 CROSS_FOLLOW (1 car), 6 CROSS_BRAKE (1), 7 CROSS_PLUS_V_BLOCK (Cross + 2 `copsporthench`) |
| Patrol budget 3 (2) | 3/2 is the cap for **AI-racer** pursuits (`GetAdjustedCopCounts`, `:2173-2180`). Free-roam patrol cars = `NumPatrolCars` of the player's row (1 at heat 1–8), `AICopManager.cpp:1330` |
| Active budget = requested − present | Wave model, see 6.3 |
| CollapseSpeed unit | km/h (`KPH2MPS`, `:1445`). 125 km/h when the player "is a jerk" (2.5) |
| Cop CTS unknown | `CalcTotalCostToState` (`:733`): 5000·copsDestroyed + 2000·heliSpawns + 500·roadblocks + 250·copsDamaged + 500·trafficHit + 250·spikeStripsDeployed + 225·heliSpikes + 250·copCarsDeployed + 450·supportDeployed + property. Barriers also call `NotifySpikeStripDeployed` (bug, `AICopManager.cpp:1291`) |
| Bounty tick every 10 s | Only while the perp is **in sight** and the pursuit is not bailed (the tick sits inside that branch, `:1918-1923`) |
| TimeToHideInZone | Never read by the decomp; PC `0x4293D0` agrees (the hiding latch is a fixed 0.05 s) **[C]** |

---

## 1. Architecture and update rates

| object | file | task rate | role |
|---|---|---|---|
| `AICopManager` (one) | `AI/Activities/AICopManager.cpp` | 0.5 (`:306`) | owns the cop vehicle pool. Each tick, in order: breaker zones, `UpdatePatrols`, `UpdatePursuits`, `UpdateRoadBlocks`, `UpdateSpawnRequests` (`:2266`) |
| `AIPursuit` (per target) | `AI/Common/AIPursuit.cpp` | 0.25 main + 1.0 busted timer | formation, wave counts, LOS, heat, busted/evade |
| `AIRoadBlock` | `AI/Common/AIRoadBlock.cpp` | — | list of cars and smackables, dodge and cheat tracking |
| `AIVehicleCopCar` / `AIVehicleHelicopter` | `AI/Common/*.cpp` | think 0.125, staggered +0.125 per instance (`AIVehiclePursuit.cpp:13-33`) | per-car goal → action → `SetDriveTarget/SetDriveSpeed/DoDriving` |

A task rate is a fraction of sim ticks (P§4). The low-level driver (`AIVehicle::OnSteering/OnGasBrake`, PID) turns a
**drive target point and a drive speed** into inputs. It is shared with racers and traffic and is out of scope here: in
Unity, `AIDriver` must take (target point, desired speed) instead of a path **[I]**.

**Goals and actions** (`AI/Common/AIGoal.cpp:116-366`). Each goal owns a list of actions. Action scores:
GetUnstuck 1, TooDamaged 1, StaticRoadBlock 1, Ram 0.1, HeadOnRam 0.1, Traffic 0.1, PursuitOffRoad 0.01; Race, Airborne,
StopShort, HeliPursuit and HeliExit 0.

| goal | actions (in order) |
|---|---|
| AIGoalPatrol | Traffic, TooDamaged |
| AIGoalPursuit | PursuitOffRoad, Race, Traffic (not for the player), TooDamaged, GetUnstuck, Airborne |
| AIGoalRam, AIGoalPit | Ram, PursuitOffRoad, Race, TooDamaged, GetUnstuck, Airborne |
| AIGoalPullOver | constructs an **AIGoalRam** (`:251`). The goal *name* stays "AIGoalPullOver", which switches Ram into pull-over mode |
| AIGoalHeadOnRam | sets InPositionOffset (0,0,−3). Actions: HeadOnRam, Race, TooDamaged, GetUnstuck, Airborne |
| AIGoalStaticRoadBlock | StaticRoadBlock |
| AIGoalStopShort | StopShort, TooDamaged, GetUnstuck, Airborne |
| AIGoalFleePursuit | Race (flee mode), TooDamaged, GetUnstuck, Airborne |
| AIGoalHeliPursuit / AIGoalHeliExit | HeliPursuit / HeliExit, TooDamaged. HeliExit unspawns the heli when its action finishes |

`AIGoal::ChooseAction` (`:33-60`, every think): for each action other than the current one with `CanBeAttempted`, it is
taken if `currentDone || action.score >= currentScore`, and the running score is raised to it. The **last** qualifying
action in the list wins. A finished current action lets any attemptable action replace it.

---

## 2. Cop car driving in pursuit

### 2.1 Which action drives (AIGoalPursuit)
* **Traffic** (0.1) when `pursuit.IsPerpInSight() == false` (search mode). It ends as soon as the perp is back in sight
  (`AIActionTraffic.cpp:153-166`). Search speed: city `SearchModeCityMPH`, highway (segment with 4 or more traffic lanes)
  `SearchModeHwyMPH`. Both come from the **default** pursuitlevels row (50 / 71 mph), not the heat row (`:184-196`).
* **PursuitOffRoad** (0.01) when all of these hold (`AIActionPursuitOffRoad.cpp:190-215`): distance to the target ≤
  `60 + |target − target.currentRoadNav.pos|`; `|v_target − v_cop| ≤ 140 km/h`; drivable LOS to the target
  (`AIVehicle::WorldCollision`: false beyond 200 m, `AIVehicle.cpp:1053`); drive-to-nav reachable; not "chicken" (never set).
* **Race** (0) otherwise: road pathfinding toward an intercept point.
* TooDamaged when destroyed (6.6). GetUnstuck: throttle ≥ 0.5 for 3 s with less than 3 m moved → reverse for 2 s at 15 m/s
  (`AIActionGetUnstuck.cpp:44-95`).

### 2.2 Race mode: far cops intercept along the road (`AI/Actions/AIActionRace.cpp`)
* Pursuit mode = goal AIGoalFleePursuit, or this car's target is its pursuit's target (`:270-276`).
* Path goal (`:853-910`): when the nav is not already path-finding, `FindPath(target.GetSeekAheadPosition())`.
  For head-on-ram support cars (`bDontSeekAhead`) it paths to the target position instead.
* `GetSeekAheadPosition` (`AIVehicle.cpp:1886-1926`): recomputed at most every 0.33 s. A nav copied from the target's
  current road (lane type Cop, path type Cop, race-route-only if the target is racing) is advanced
  `min(500, |v_target|·T + 8)` m, with `T = 7.8` s (×0.4 when "jerk"). Cops therefore aim about 8 s ahead of you **on the
  road**. This is the intercept/flank behaviour; there is no separate flank code.
* Look-ahead along the nav: `AiNavLookAheadTable` = lerp 30→100 m over speed limit 0→100 m/s (`:81`).
* **Speed (`GetPotentialSpeed`, `:503-576`)**:
  ```
  seek_dir = unit(lastFindPos − targetPos)          // the road direction ahead of the target
  s        = dot(copPos − targetPos, seek_dir)      // + = cop is ahead of the target
  distant  = KPH2MPS(MAXIMUM_AI_SPEED) (×1.1 jerk)
  fwdNear  = clamp(vT − (s>0 ? 0.2778 : 0.5556)·s, KPH2MPS(10), distant)   // catch up from behind 2× faster
  revNear  = clamp(s>0 ? −vT + 0.1389·s : distant, KPH2MPS(40), distant)
  near     = lerp(revNear, fwdNear, clamp(dot(copFwd, seek_dir)+0.5, 0, 1))
  lat      = |copPos−targetPos − s·seek_dir|·2.5 ;  sAdj = s>0 ? 0.5·s : s
  app      = sqrt(sAdj² + lat²)                       // "apparent distance"
  nearW    = clamp(1 − (app−150)/150, 0, 1) · clamp(|dot(copSteerDir, targetSteerDir)|+0.2, 0, 1)
  maxCop   = clamp(nearW·near + (1−nearW)·distant, 0, distant)
  curve    = GetSpeedLimit(curv, f0=startGrip, f1=(endGrip−startGrip)/topSpeed, topSpeed)   // :475
  speed    = min(maxCop, curve · TopSpeedMultiplier(×2 if racing, ×1.2 jerk) · AiSpeedScale(skill=1)=1)
  ```
  `GetSpeedLimit(c,f0,f1,V) = n / max(n/max(0.1,V), 2|c|)` with `n = 9.8·f1 + sqrt((9.8·f1)² + 4·9.8·f0·|c|)`.
  Grips come from the tire STATIC_GRIP plus aero (`GripTor`, `:310-342`). Cop skill = 1 (`AIVehicleCopCar.h`).
* Speed limit ramp (`:975-997`): `fSpeedLimit` rises toward the potential with
  `clamp(a_pot − a_actual·t^lerp(1.5,2,skill), 0, a_pot)`, where
  `a_pot = AccelOf(speed)·AccelerationMultiplier (×2 racing, ×1.5 jerk) + grade gravity`.
* Turn-around (`:1012-1025`): speed > 40 km/h and the drive target behind (`dot(v, steer) < −0.3·|v|·|steer|`) → drive to
  `GetFutureRoad().pos` at speed 0.
* NOS (`:1032-1069`): in pursuit `mUsableNOS = 1`. Uses the racer NOS gaps (on when speed gap > 15 and accel gap >
  0.5·a_pot after an off time of `lerp(20,4,skill)` s; off after `lerp(2,4,skill)` s when gaps < 5 / 0.15·a_pot).

### 2.3 PursuitOffRoad: near cops hold a formation slot (`AI/Actions/AIActionPursuitOffRoad.cpp`)
Every think step:
1. **Delayed target speed**: a 16-sample ring buffer over 2 s of the target speed. `vd = sample(SpeedReactionTime)`
   (h1 1.75 s … h5 0.1 s) (`:21-114`, `:381-382`). Low heat cops react late to your braking or acceleration.
2. **Seek point** (`:328-375`): `center = target.currentRoadNav.pos`, `future = target.futureRoadNav.pos`,
   `dir = unit(future−center)`. With no valid navs: `center = targetPos`, `dir = v̂_target` (forward if speed < 1),
   `future = targetPos + v_target`. `offset = GetPursuitOffset()` (the formation slot, 2.5).
   `seekPos = center + dir·off.z + side·off.x`, and the same for `seekFut` from `future`.
3. `roadoff = unit(seekFut − seekPos)·vd`. `simpleoff = v̂_target·vd`. `seekoff = seekPos − copPos`, split into the part
   along `roadoff` and the part across it. The along part is scaled by
   `k = slide·1.8 + (1−slide)·min(1, max(KPH2MPS(20), 0.2·vd)/|seekoff|)` with
   `slide = clamp(10·dot(unit(seekoff),dir), −1, 1)·0.5+0.5` (`:400-425`). Slot ahead of the cop: close at 1.8×
   distance per second. Slot behind the cop: ease back.
4. `blended = copPos + seekoff + lerp(roadoff, simpleoff, TETHER_WEIGHT/100)` (`aivehicle.TETHER_WEIGHT`, values **[?]**).
5. **Lane snap** (`:437-508`): drive nav lane type CopReckless at heat ≥ 3, else Cop. Advance the nav in 5 m steps to
   the longitudinal distance of `blended`. If the lane offset of `blended` is within 4 m of a selectable lane, change to
   that lane and use the nav's occluded position at the same distance. Otherwise reset the nav to a valid lane.
   `seek = blended − copPos` (its length is the desired m/s).
6. **Limiter** (`:532-569`): `vmax = min(KPH2MPS(MAXIMUM_AI_SPEED), targetTopSpeed·TopSpeedMultiplier)` (jerk: ×1.2
   mult, ×1.1 max). `amax = targetAccel(copSpeed)·AccelerationMultiplier (×1.5 jerk) − 9.81·fwd.y`, and **×0.2 while the
   target's NOS is engaged**. `performance_limiter.update(speed, vmax, amax, dt)` (2.4). `|seek|` is clamped to the limit.
   Cop pace therefore scales with **your** car's top speed and acceleration.
7. **Steering forces**:
   * wall avoid: ray along v of length `min(80, |v|+10)`; on a hit facing it, `normal·dot²·KPH2MPS(10)/t²` (`:294-326`);
   * vehicle separation `AISteer::VehicleSeperation(..., 2.7, 5.3)`: capsule-to-capsule `r²/max(d³,0.1)` plus a closest-approach
     term `relative/(d²/2 + t)²`, capped at 160 km/h (`AISteer.cpp:157-245`);
   * road affinity: push in from each road edge, `0.5/(d+1)² + 2·(v_side/(d+1))²`, each capped at 100 km/h (`:255-287`).
   `steer = avoid + separation + affinity`. If steer opposes seek (`c = −dot(steer,seek)/|seek| > 0`), the seek component
   along steer is scaled by `clamp((KPH2MPS(35) − c)/KPH2MPS(25), 0, 1)`. Then `steer += seek`.
8. **Curvature cap** (`:595-610`): `curv = (1−cos∠(v,steer))·|v|/|steer|`,
   `|steer| ≤ max(KPH2MPS(40), KPH2MPS(450) − curv·KPH2MPS(950))`.
   Then `SetDriveSpeed(|steer|)` and `SetDriveTarget(copPos + steer)`.
9. NOS: `AIVehicle::DoNOS` (`AIVehicle.cpp:593`): on when desired > 120 km/h, speed > 80 km/h, gap > 60 km/h and aligned
   (`dot(driveoff,v) > 0.95·|driveoff|·|v|`). Thresholds once on: 90 / 50 / 10 km/h, 0.8. On the frame the **player's**
   NOS switches on, the cop waits 1.5 s (`kNOSDelay`) before using NOS.

### 2.4 performance_limiter (`AI/Common/AIAction.cpp:20-38`)
```
if speed > 0:
  if speed < L: t=(L−speed)/KPH2MPS(5); L −= min(1, dt·t²)·(L−speed)   // collapse toward real speed after a hit
  L = min(vmax, max(speed, L) + amax·dt)
```
`init(L = cop speed)` happens at BeginAction. When a cop first gets the action within its first sim frame after spawning
(`GetLastSpawnTime() <= 0`), its speed is set to 60 mph (Ram, PursuitOffRoad, HeadOnRam).

### 2.5 Formations (`AIPursuit::UpdateFormation`, `AIPursuit.cpp:1340-1537`)
Each main tick:
1. `formation.Update()` (box-in, rolling and herd re-scale their slots).
2. **Candidates**: cop cars (not helis, not support units) with drivable LOS and `|cop − target| ≤ 60 + |target − its
   road nav|`. Their positions are converted to the target's rigid-body local frame. Every cop gets `InFormation=false`
   first. Helis get AIGoalHeliPursuit unless there is a roadblock or they are exiting (`:1028`).
3. **EvenOutOffsets** (`:1047`): while slots taken < min(#candidates, MaxCops), take the next unused slot with the
   **lowest priority number** (`mMinTargets`). Ties go to the slot with the smallest **sum** of distances to all candidates.
4. **AssignClosestOffsets** (`:1095`): distances use `z·0.25` (along-track gaps count a quarter). Greedy loop: take the cop
   whose largest slot-distance is the largest, take that farthest slot, give it to its closest unassigned cop, mark both
   used and repeat. It sets `PursuitOffset`, `InPositionOffset`, `InPositionGoal` and `InFormation=true`.
5. **Cops not in formation** (`UpdateOutOfFormationOffsets`, `:1539-1592`), the i-th of them:
   `x = (((i/2)+1)%3 − 1)·3.5`, `z = (1 − 2·(i%2))·(25 + 5·(i/6))`. They hold slots 25 m ahead or behind, 3 lanes wide.
6. **In position**: `|slot − relPos|_xz < 4` (`:1426-1431`).

Slot tables (`PursuitOffset` → `InPositionOffset`, priority, in-position goal; MaxCops; finisher):

| formation | slots | Max | finisher |
|---|---|---|---|
| PIT (`:399`) | (4,−2.7)→(−10,−2.7) p1 Pit; (−4,−2.7)→(10,−2.7) p1 Pit | 1 | yes; TimeToFinisher 1.2, tolerance 0.5, FinisherTime 2.0 |
| BOX_IN (`:179`) | base 0:(0,14) p1, 1:(−3.5,0) p2, 2:(3.5,0) p2, 3:(0,−7.5) p4; all Ram. In-position: slot0 → (0,0,`2−5t`); slots 1–3 → base·`(0.7−0.5t)` | 4 | yes, min 2 cops; FinisherTime `BoxinDuration` |
| ROLLING_BLOCK (`:264`) | base (0,14) p1, (−2.5,14) p2, (2.5,10) p2, (−5,14) p3, (5,14) p3; all Ram. In-position x = base.x·`(1−0.8t)`, z = `2−5t` | 4 | yes, min 2; FinisherTime `RollingBlockDuration` |
| FOLLOW / HELI_PURSUIT (`:325`) | (0,−13) p1, (±3.5,−13) p2, (0,−17) p3, (±3.5,−17) p4 | 6 | no |
| HERD (`:425`) | (−3,0) p1, (−3,5) p2, (−3,−5) p3. Each update x = `−clamp(roadoffset − rightmostTrafficLaneOffset + 2, 1, 3)` (shoves you toward the road edge) | 3 | no |
| STAGGER_FOLLOW (`:357`) | (0,∓13) p1 ×2, (3.5,−13) p2, (−3.5,13) p2, (−3.5,−13) p3, (3.5,13) p3 | 6 | no |

`t` = BoxinTightness / RollingBlockTightness (default 0.5; per heat in P§2). Live slot scale while forming:
box `scale = f·ft + (1−ft)` with `ft = 0.2t+0.2`; rolling `ft = 0.4t`; `f = TimeToFinisherAttempt/TimeToFinisher` goes
from 1 to 0 as the in-formation timer fills. The formation tightens as the finisher approaches.

**Finisher** (`:1451-1536`). `IsFinisherActive = breakerTimer >= 0`.
```
if finisherActive && breakerTimer < FinisherTime && !busted: breakerTimer += dt
elif collapse conditions (2.6): collapse
elif !busted && (breakerTimer>=0 || collapseActive): end it — all assigned cops (and any cop whose goal is its
     InPositionGoal or PullOver) get InPositionGoal=null and StartPursuit (back to AIGoalPursuit)
elif HasFinisher && countInPosition>0:
     ftol = FinisherTolerance; rate = 4·ftol
     inFormTimer = max(0, inFormTimer + clamp((2·rate − avgInPosDist)/rate, −1, 1)·dt)
     if inFormTimer >= TimeToFinisher (4 s; PIT 1.2):
        if countInPosition >= MinFinisherCops (1; box/rolling 2): breakerTimer = 0;
             every in-formation cop with an InPositionGoal → DoInPositionGoal (AIGoalRam / AIGoalPit)
        else inFormTimer = TimeToFinisher − 0.01
else inFormTimer = 0
```
On a new formation, `EndCurrentFormation` sets `activeFormationTime = 0` and `breakerTimer = −1`. Adding a roadblock ends
the current formation unless it is FOLLOW or a finisher is running (`:2508`).

### 2.6 Collapse: cops surround a slow perp (`SetupCollapse`, `:1239-1338`)
Conditions: `targetSpeed < KPH2MPS(CollapseSpeed)` (125 km/h if jerk), countInFormation > 0, in sight, not busted.
* Collapse candidates: within 60 m, drivable, not support, not fleeing, not a heli.
* `front` = target velocity direction (forward vector if slower than 5 km/h). Each cop gets a pseudo-angle
  `cheap_atan(f,s)` (`:1223`).
* With more than `MaxCopsCollapsing` cops: sort by distance. The nearest Max go on the **inner** circle
  `r = max(3, CollapseInnerRadius)`, the rest on the **outer** circle `r = max(inner+1, CollapseOuterRadius)`.
* Within each circle, sort by angle. Cop `(i+frontmost)%n` gets `InPositionOffset = (sin(i·2π/n)·r, 0, cos(i·2π/n)·r)`
  (the front-most cop goes in front) and goal **AIGoalPullOver**.
* When a collapse starts, active heavy support flees and the ground support request resets (`:1460-1474`).

### 2.7 Ram / Pit / PullOver action (`AI/Actions/AIActionRam.cpp`)
* Attempted while there is drivable LOS to the target and the drive nav is valid. Never finishes otherwise.
* Seek point (`:120-173`): `targetPos + fwd·off.z + side·off.x` with `off = InPositionOffset`. Fwd is the velocity
  direction (forward vector below 5 km/h).
  In pull-over mode, if the straight line to the seek point crosses the circle around the target (radius
  `min(|off|,|cop−target|) − 0.2`), it goes around along a tangent (`±2.1·|off|`, the closer of the two).
* `UpdateSeek` (`:175`): adds 70 % of the seek offset's component along the target velocity again, then
  `AISteer::Pursuit` aims 1 s ahead: `seek = (p + v_target·1) − copPos`.
* Pull-over (collapse) mode only (`:211-248`): `aggr = CollapseAggression` (1 if jerk).
  `VehicleSeperation(static=(1−aggr)·4, dynamic=static+1.5)`. If the separation opposes the seek, the seek is scaled
  along it by `clamp((KPH2MPS(55) − c)/KPH2MPS(55), 0, 1)`.
  When `|seek| < KPH2MPS(0.5) + (1−aggr)·KPH2MPS(0.2)`: full brake + handbrake (the cop parks in its ring slot).
* Speed = `|steer|` clamped by the limiter (same vmax/amax rule as 2.3, no NOS factor). No NOS.
* Effect: box-in and rolling-block cops drive to their tight in-position slots in your frame, so they brake in front of
  you and squeeze your sides. PIT cops (in-position x = ∓10 from a ±4 slot) swerve across your rear quarter.
  "Ramming" is contact from steering to these points; there is no impulse code **[C]**.

### 2.8 What a cop does, by situation (summary)
* Perp not seen for ≥ 7 s: every pursuit car switches to Traffic and patrols the roads at search speed.
* Seen, more than ~60 m away or no LOS: Race. Road pathfinding to the 7.8 s seek-ahead point, catch-up speed curve.
* Near: PursuitOffRoad. Holds the slot of the current formation (FOLLOW / STAGGER / HERD push you; BOX / ROLLING / PIT
  form up).
* Formed for ~4 s with enough cops in position: finisher. AIGoalRam (box/rolling) or AIGoalPit for the formation's
  FinisherTime (Boxin/RollingBlockDuration, PIT 2 s), then back to pursuit.
* You are slow (< CollapseSpeed km/h): collapse ring, AIGoalPullOver. This feeds the busted meter (P§5).
* Busted: AIGoalStopShort for all cops (full brake + handbrake, siren lights off) (`AIPursuit.cpp:1944-1958`).
* Bailed or evaded: StartFlee = AIGoalFleePursuit (Race toward a point 500 m behind the player along its velocity
  (`AIActionRace.cpp:862-897`); helis use AIGoalHeliExit).

### 2.9 Jerk detection (`AIPursuit::UpdateJerk`, `:2824-2853`)
```
k = 0.1·dt ; lagPos = lerp(lagPos, pos, k) ; lagDist = lerp(lagDist, |pos−lagPos|, k) ; lagSpeed = lerp(lagSpeed, speed, k/2)
f = lagDist>0.01 ? 10·lagSpeed/lagDist : 0 ;  jerk on if f >= 3, off if f <= 1.75   (init lagDist 1000)
```
Driving straight gives `lagDist ≈ 10·v`, so `f ≈ 1`. Circling or weaving in a small area at speed gives a large `f`.
While "jerk": cop speed mult ×1.2, accel ×1.5, max AI speed ×1.1, seek-ahead time ×0.4, collapse at 125 km/h with
aggression 1.

### 2.10 Support units (`AICopManager.cpp:1439-1673`, `AIPursuit.cpp:2534-2617`)
* **RequestGroundSupport**: needs in sight, not busted, and the 10 s check timer. Needs `MinimumSupportDelay <
  pursuitTime`. Leader priority once (P§3), then `HeavySupportOptions` by Chance (only when no roadblock exists; sets
  `roadBlockTimer = 15`), else `LeaderSupportOptions` by Chance. A leader request is dropped unless Cross is available
  (not spawned, not destroyed).
* **Heavy** vehicles: each slot draws `rand(100) < ChanceBigSUV ? copsuv : copsuvl`. RAM uses 2 cars with support goal
  AIGoalHeadOnRam, a 3 s delay timer and speech `MReqBackup(16)`. HEAVY_ROADBLOCK uses 4 with AIGoalStaticRoadBlock and
  calls `CreateRoadBlock(…, suvList)` (3). E_BRAKE (1 car) and COORDINATED_E_BRAKE (2) use AIGoalPursuit and are not in
  the data.
* Heavy RAM spawn (`StartHeavySupport`, `:1495`): ahead along the road at `400 − 60 − 10k` m (k = 0..7 tries), facing
  back toward you. `AIActionHeadOnRam` (`AIActions/AIActionHeadOnRam.cpp:141-156`): `AISteer::Ram` solves the intercept
  time `t` from `|p_T + v_T·t − p_C| = |v_C|·t` and steers there at drive speed 100 km/h.
* **Leader** (`:1547-1644`): Cross (`copcross`) with PursuitOffset (0,0,−20), sitting 20 m behind you.
  CROSS_PLUS_V_BLOCK adds 2 `copsporthench` at (±4,0,−24). Spawned **behind** you at 150+10k m along the road. Support
  goal AIGoalPursuit, kept out of formations, so their slot never changes.
* Duration: `GroundSupportRequest.mSupportTimer = Duration`. On expiry `Reset()` clears the support goals and the units
  join the normal contingent (not heavy-roadblock SUVs) (`AIPursuit.cpp:504-541`). Support units flee when a collapse
  starts or when the perp is out of sight (`:2261-2274`).

---

## 3. Roadblocks

### 3.1 Request cadence (`AIPursuit::RequestRoadBlock`, `:2459-2506`; `AICopManager::UpdatePursuits`, `:1901-1918`)
* Needs no roadblock already, not busted or bailed, `roadBlockTimer < 0`, and `MinimumSupportDelay ≤ pursuitTime`.
  The timer is then reset to `8 + rand[0,4)` s.
* It returns **the previous roll** (`4` if the last roll passed), then rolls again:
  `p = in sight ? roadblockprobability : SearchModeRoadblockChance·(R − d)/R` with `R = SearchModeRoadblockRadius` and
  `d = |lastKnownLocation − target|`. Pass if `rand[0,100) < p`. A successful roll therefore builds the roadblock one
  cadence (8–12 s) later.
* A non-zero return latches the request. `CreateRoadBlock` is retried every manager tick until it succeeds or the
  pursuit gets a roadblock. Pursuit car spawns are blocked while a roadblock request is latched.

### 3.2 Placement (`AICopManager::CreateRoadBlock`, `:1100-1308`)
1. Nav at the target position and **target forward** (path type Cop), advanced **250 m**.
   Rejected if the segment is missing, has no traffic allowed, is a decision segment (intersection), is shorter than 40 m,
   or the profile has no zones.
2. `crW = |left − right|` (road edges at the nav). `cr_width = crW + 1`. `idx = min(int(cr_width·0.25), 6)`.
   `ask = {2,2,3,3,4,4,5}[idx] + 1`.
3. Cars: the SUV list for heavy roadblocks, else `ask` inactive cars of a valid type for the row. All are unspawned, then
   the shortfall is stolen from active cops that have been off-screen for over 5 s and are more than 100 m from view
   (6.4). Fails if `have < ask − 1`.
4. Spikes if `rand[0,100) < roadblockspikechance` and not in cooldown.
5. `PickRoadblockSetup(cr_width, have, spikes)` (`:1079`): the setup with the smallest positive `cr_width − minWidth`
   and `requiredVehicles ≤ have`.
6. Centre: start at the road edge **nearer the target**, then move `minWidth/2` toward the other edge.
   `xScale = clamp(cr_width/minWidth, 1, 1.14)`. Road forward `F` = nav forward.
7. Each element: `pos = Centre + Rot(F)·(x·xScale, 0, z)`, facing = `RotateInXZ(angle_rev, F)`. A car goes on the
   road there (`ResetVehicleToRoadPos`) and joins the roadblock → `StartRoadBlock` (lights on, AIGoalStaticRoadBlock).
   A barrier spawns smackable `XO_Sawhorse_1b_00` (sawhorse), a spike strip `XO_SpikeBelt_1b_DW_00` (spikestrip),
   aligned to the ground normal.
8. `pursuit.AddRoadBlock` (FOLLOW formation, 2.5) and `NotifyRoadblockDeployed`.

### 3.3 Setup tables (`AI/Common/AIRoadBlockSetups.cpp`)
`C` = cop car, `B` = sawhorse, `S` = spike belt; `(x, z, angle_rev)`. A list ends at the first `minWidth ≤ 0.1`.

`RoadblockCandidateList` (no spikes):

| # | minW | cars | elements |
|---|---|---|---|
| 0 | 8.0 | 2 | C(−3,0,.75) C(2,−.35,.27) |
| 1 | 12.0 | 3 | C(−5.2,−2,.875) C(0,−2,.12) C(4.5,−1,.87) B(−2.9,.2,.48) |
| 2 | 5.5 | 2 | C(−2,0,.26) C(2,5,.48) B(1.5,1.7,.61) |
| 3 | 6.5 | 2 | C(−2,0,.75) C(3,4,.25) B(1.5,1.7,.61) |
| 4 | 8.5 | 2 | C(−.9,0,.72) C(4,0,.32) B(−4.8,−.8,.54) |
| 5 | 15.0 | 4 | C(−5,0,.71) C(1,0,.29) C(5,6,.24) C(8,2,.5) B(4.1,−1.4,.57) B(6.1,−1.4,.43) |
| 6 | 13.0 | 3 | C(−4,1,.26) C(−.9,−4.2,.75) C(5,−4,.26) B(−3.3,−2.2,.4) B(−5,−4,.46) |
| 7 | 11.4 | 3 | C(−4.4,−1.2,.65) C(0,2.4,.24) C(4.4,−1.2,.35) B(0,−2.7,.49) B(2.1,0,.35) B(−2.1,0,.65) |
| 8 | 12.1 | 3 | C(−4.5,0,.75) C(1,0,.25) C(4,4.5,.72) B(4.3,−1.2,.48) B(4.9,.3,.04) |
| 9 | 10.0 | 4 | C(−4.5,0,.27) C(−.3,−4,.75) C(4.2,−4,.24) C(4,0,.26) B(−4.9,−5,.51) B(−4.2,−3.3,.53) |
| 10 | 14.0 | 4 | C(−4.5,0,.25) C(1,0,.75) C(0,5.4,.25) C(5.3,5.4,.22) B(−2,2.2,.37) B(5.4,−.2,.53) |
| 11 | 12.0 | 4 | C(−4.3,0,.74) C(−5,4,.75) C(.2,4,.27) C(4,0,.25) B(−.7,−1,.355) B(.7,−1,.635) |
| 12 | 22.0 | 4 | C(−9,0,.74) C(−3,0,.25) C(3,0,.75) C(9,0,.25) B(0,−3,.5) |
| 13 | 15.0 | 3 | C(−5.9,0,.74) C(0,0,.25) C(5.9,0,.75) B(3,−2,.51) |
| 14 | 28.0 | 5 | C(−12,0,.74) C(−6,0,.25) C(0,0,.75) C(6,0,.75) C(12,0,.25) B(−3,−3.4,.5) |

`SPIKES_RoadblockCandidateList`:

| # | minW | cars | elements |
|---|---|---|---|
| 0 | 8.0 | 1 | C(−3,0,.75) S(2,−.35,.52) |
| 1 | 15.0 | 3 | S(−5,0,.51) C(1,0,.29) C(5,6,.24) C(8,2,.5) B(4.1,−1.4,.57) B(6.1,−1.4,.43) |
| 2 | 15.0 | 2 | C(−4,1,.26) C(−.9,−4.2,.75) S(5,−4,.52) B(−3.3,−2.2,.4) B(−5,−4,.46) |
| 3 | 14.1 | 2 | S(−4.8,0,.5) C(1,0,.25) C(4,4.5,.72) B(4.3,−1.2,.48) B(4.9,.3,.04) |
| 4 | 12.0 | 3 | C(−4,0,.27) S(−.3,−4.4,.5) C(4.8,−4,.24) C(4,0,.26) B(−4.9,−5,.51) B(−4.2,−3.3,.53) |
| 5 | 13.0 | 3 | S(−4,0,.51) C(−5,4,.75) C(.2,4,.27) C(4,0,.25) B(−.7,−1,.355) B(.7,−1,.635) |
| 6 | 21.0 | 2 | S(−9,0,.48) C(−3,0,.25) C(3,0,.75) S(9,0,.52) B(0,−3,.5) |
| 7 | 15.0 | 2 | S(−5.2,0,.51) C(0,0,.25) C(5.9,0,.75) B(3,−2,.51) |
| 8 | 28.0 | 4 | C(−12,0,.74) C(−6,0,.25) S(−1,0,.51) C(5,0,.75) C(11,0,.25) |

In every setup `cars` equals the number of `C` elements. The code takes the gathered cars in order, one per `C`.
Gathered cars that the setup does not use stay unspawned. The chosen setup is the **widest one narrower than
`cr_width`** that the gathered cars can fill. Example: `cr_width = 9.5` with 3 cars gives #4 (8.5).

### 3.4 Roadblock cops and lifetime
* Cars: `AIActionStaticRoadBlock` (gas 0, brake 1, steer 0, no handbrake). They are not in the pursuit (`GetPursuit() ==
  null`) and do not count toward the wave. Siren forced to WAIL (`AIVehiclePursuit.cpp:298`).
* LOS: if the perp is not hidden from cars, the roadblock's nearest cop distance counts in the busted distances, and
  nearest < **60 m** sets `seen = true; lostTime = 0` (`AIPursuit.cpp:1853-1872`).
* Cops in the roadblock within 300 m get `SetWithinEngagementRadius` (`AIRoadBlock.cpp:96`).
* **Joining the chase** (one cop per roadblock):
  * **cheat**: once `minxz < 500`, while the perp stays within 300 m of the roadblock centre the cheat timer runs;
    over 20 s, or while the pursuit is in COOL_DOWN, the perp is "cheating" (`AIRoadBlock.cpp:107-124`). The nearest
    roadblock cop then moves into the pursuit and `NumCopsRequiredToEvade++` (`AIPursuit.cpp:1874-1879`).
  * `SpikesHit` would move up to 3 nearest roadblock cops in, but nothing in the decomp calls it **[?]** (`:2791`).
* **Dodged** (`AICopManager::UpdateRoadBlocks`, `:1970-2012`): once `dot(unit(centre − perp), roadDir) < 0` (the perp is
  past the centre plane): `SetDodged`, `roadblocks_dodged++`, and with spike strips `tire_spikes_dodged += numSpikes`.
  This holds even if you hit it; there is no "touched" test **[C]**.
* **Formation**: while the roadblock is undodged and none of its cops are damaged or destroyed, new picks are FOLLOW.
  Pursuit cops sit 13–17 m behind you, herding you into it.
* **Cleanup**: a roadblock is released when it has **0 cars**. Its smackables are destroyed (`ReleaseAllSmackables`).
  Cars leave by the normal despawn rules (6.5: far and off-screen, ≥ 8–10 s since spawn), by being destroyed (removed
  from the roadblock; `IncNumCopsDestroyed` counts toward the bounty), or by joining. `mRoadBlock` is cleared when the
  roadblock detaches, which allows the next request.

---

## 4. Spike strips and tire pop
* Strip (`Physics/Behaviors/SpikeStrip.cpp`): while nothing touches it, the smackable is turned into a trigger model
  (`PlaceTrigger` + kill the body). On contact, for every rigid body touching it with `ISpikeable`: each wheel on the
  ground with no damage is boxed (`0.15 × wheelRadius × wheelRadius` at the wheel centre, swept by `v·dt`). If the box
  intersects the strip's collision box → `Puncture(wheel)`. **Any** car can be spiked, cops and traffic included **[C]**.
* `DamageRacer::Puncture` (`Physics/Behaviors/DamageRacer.cpp:54-65`): state NONE → PUNCTURED and event
  `ETirePunctured`. Blow-out timer 0.5 s (180 s with run-flat tires, `Physics::Info::HasRunflatTires`). When it expires:
  BLOWN + `ETireBlown` (`:90-100`).
* BLOWN wheel effect (`SuspensionRacer.cpp:1665-1669`): `SetBrake(1)`, `SetEBrake(0)`, traction ×0.3 (permanent drag on
  that wheel). Visual and sound use the same flag. Damage reset (repair or respawn) clears it.
* Heli spike drop: not implemented in the decomp (SPIKE_DROP is never read).

---

## 5. Helicopter

### 5.1 Spawn
* It is the `copheli` row of `cops`, requested through `CopRequest` (6.3). It is eligible when `spawnHeliTimer < 0` (10 s
  at pursuit start; `TimeBetweenHeliActive` after a heli leaves, `AIPursuit.cpp:876-881`) and `MinimumSupportDelay <
  pursuitTime`. Its `Chance` is a **weight** against the ground types' chances. At most one heli exists (`HeliVehicleActive`).
* Search mode: on entering COOL_DOWN, `doTestForHeliSearch = true`. At the next eligible request, if the perp is unseen
  and no heli exists, `rand[0,100) ≤ SearchModeHeliSpawnChance` forces the next request to `copheli` (`:2369-2389`).
* Placement (`AICopManager::SpawnPursuitHelicopter`, `:1035-1077`): road nav from the target pointing **backwards**,
  advanced 250 m (target position if no nav), +20 m up, oriented along the nav. Fuel = `HeliFuelTime` (`SetFuelFull`).

### 5.2 Fuel and exit
* `fuel −= dt`. At 0 → AIGoalHeliExit (`AIVehicleHelicopter.cpp:104-112`). Pursuit end, bail or flee also gives HeliExit.
* HeliExit (`AI/Actions/AIActionHeliExit.cpp`, `Exit_Height = 25`, speed 100):
  1. SeekUp: road-snapped own position +25 m.
  2. Once more than 15 m above the player: SeekCar, a point 85 m ahead of the player at +25 m.
  3. Within 5 m of it: FlyOut to `player − 200·(fwd + right·r)` with `r = 3`, or `−9` if the heli is moving to the
     player's right; y = player.y + 30.
  Finished, and **unspawned**, when FlyOut, more than 25 m above the player, behind the player and over 150 m away.
* On detach: `spawnHeliTimer = TimeBetweenHeliActive`.

### 5.3 Pursuit flight (`AI/Actions/AIActionHeliPursuit.cpp`)
Modes: StraightLine, Search (perp not in sight), SkidHit Approach and Strike. `BeginAction` paths to the player once.
`IsFinished` turns true after 10 s (`Pursuit_Time_Max`) with `ShouldRestartWhenFinished = true`. `AIGoal::ChooseAction`
never re-picks the current action, so how the restart happens is not visible **[?]**. Flying works without the path:
it steers directly.
* **StraightLine** (`:172-267`):
  `lead = min(45, 0.4·v_player + 30)` (×0.75 when skid-hit is enabled). `seek = player + playerFwd·lead`.
  Height above the player's y: +2 during a skid knock; +12 if more than 55 m away or still in the skid cooldown; else +6.
  Closing speed: 50 if more than 300 m away, 30 if more than 15 m, 20 if more than 3 m, else the distance.
  `driveSpeed = closing + dot(unit(seek−heli)_xz, v_player)` (halved when negative).
  It looks at the seek point, or at the player when closer than 5 m and the player is slower than 15 m/s.
  `SetDestinationVelocity(v_player)`.
* **SkidHit** (only when some AirSupport option of the row is SKID_HIT: heat 5–6, `SkidHitEnabled`) (`:182-345`).
  Start: skid timer < −5 (5 s between attempts), heli 5–35 m from the player, ahead of it (`dot(dir,v̂) > 0.707`), and
  less than 13 m above. Approach (timer 8 s): `seek = player + right·(±6) + v·0.23`, y +1.8 (+3 more if the heli is less
  than 2 m above). Within 4 m (xz) and more than 1.9 m to the side → Strike (timer ≥ 2 s):
  `seek = player + v·0.092 − 0.5·sideOffset`, y +0.4, drive speed 100. The heli drops onto the car's side.
  Aborts: in front and more than 40 m away; below the player; behind and more than 12 m away; or collision score > 20
  (ground +2, player contact +5, −1 per update).
* **Search** (`:347-390`): orbit points around `pursuit.LastKnownLocation`. Radius `rand(0.7R) + 0.2R` with
  `R = SearchModeRoadblockRadius`. The angle advances 0.125 rev per point, the next point is taken within 30 m, height
  = heli-sheet elevation + 5, speed 70.
* **Flight model** (`AIVehicleHelicopter::OnDriving`, `:418-514`): working position `p + v·0.45`. Destination
  `dest + destVel·0.45`. `SetDestinationVelocity(v)` (`:79`) computes `AddScale(v, 9v, 0.1)`. That is `1.9·v` if
  AddScale is `a + b·s`, or `v` if it is `(a+b)·s`. The body of `VU0_v3addscale` is not in the decomp **[?]**. It avoids the camera (7 m sphere, 0.2 s lead) and clamps to the **HeliSheet** elevation grids (3 sheets,
  "ooze" slide along the normal 0.3/0.4).
  Braking: if the closing speed is above 3 and `moveLen ≤ cs²/(1.55·min(cs·2, min(80, 0.2·speed+30)))`, drive speed
  ×0.4 with max deceleration.
  Vertical: `dy∈[0,7)` → ×5 (+2·destVel.y); `dy∈(−5,0)` → ×3 (+destVel.y).
  `ISimpleChopper.SetDesiredVelocity(dir·driveSpeed)` and face the look-at point.
* **LOS** (`:119-202`): a candidate within 75 m (`kHeliVisualSphere`, no facing test), or within `heliLOSdistance`
  with the heli facing the player (`dot > 0`). Then a ray to the player +1 m must be clear (this records the last
  sighting), and not while the camera view is in a tunnel/overpass. Sticky rule when unseen: within 150 m and the
  player within 20 m of where the heli last saw it → still seen. A perp hidden from helicopters that the heli fails to
  see latches it blind (`mPerpHiddenFromMe`) instead. Unlike the cars there is no PursuitMeterCanShowBusted test.
* Spotlight: none in the AI. A night spotlight, if any, is render-side **[?]**. The heli shadow scale is
  `1 − (y − ground − 5)·0.025`.

---

## 6. Spawning, counts, despawn and damage

### 6.1 Manager constants (`AICopManager.cpp:257-321`)
Spawn ring around the **camera** (xz): `min 150`, `max 400`. `mMaxActiveCopCars = 8` (platform budget 8),
`mMaxActiveCopHelicopters = 1`. Lockout `kCopLockoutTime = 60` s, or `TimeInactiveFor911` of the speech row in free roam.
`VehicleSpawningEnabled` is false while locked out, while cops are disabled, during an NIS, or after
`NoNewPursuitsOrCops` (`:442`).

### 6.2 Spawn location rules
* `CheckSpawnPosition` (`AISpawnManager.cpp:137-195`):
  * 150 ≤ dist(camera) ≤ 400 (when the check is on);
  * not in a `TRACK_PACH_ZONE_NO_COP_SPAWN` zone;
  * no active vehicle within 15 m (same lane and node, or destroyed) or 5 m (other lane) for traffic navs, or 40 m for
    any other nav.
* Patrol and cooldown spawns (`GetSpawnLocation`, `:96`): a random segment from a rolling list of up to 50 segments
  within 450 m of `camera + playerVelocity·1 s` (no-traffic, decision and < 10 m segments excluded). Traffic lanes are
  used round-robin, at a random position along the segment.
* Pursuit spawn (`SpawnPursuitIVehicle`, `:855-989`):
  * Cooldown: patrol rule.
  * Free roam, "new way": `S = target.SeekAheadPosition`. Rotate the vector `S→target` by ±60° (coin flip). Start a nav
    at S in that direction, advance `|S − target|`, check the position (distance check on), **reverse** the nav and place
    the car. The cop comes at you from a side road off your predicted path.
  * Fallback: centre = target + 200 m along `v̂_target`. Pick a uniform random point in a 190 m circle around it, snap
    it to a cop road, check it.
  * In a race: 230 m ahead along the road, facing you.
* Forced pursuit start (`PursueAtHeatLevel`, `SpawnPursuitCar`, `:800`): 340 m ahead along the road (80 m when warping
  or starting a free-roam pursuit). If that fails, `SpawnCopCarNow` spawns one 15 m **behind** the player.
* Scripted spawns (`SpawnCop` message → `TrySpawnCop`, `:615`): exact position. Fails if it overlaps a car by
  `radius + r + 2` m.
* Car pool: an inactive car of a valid type for the row is reused, else one is created (`PVehicle`, `DRIVER_COP`).
  `copmidsize` is the fallback. With no free car: steal the active cop that has been off-screen for more than 5 s and is
  farthest from view (more than 100 m) (`GetActiveCopVehicleFromOutOfView`, `:539`).

### 6.3 How many cops and of which type (`AIPursuit.cpp:2165-2457`)
* Wave: `required = FullEngagementCopCount` (locked at start and after each backup, `LockInPursuitAttribs`). Cops that
  were inside `FullEngagementRadius` (and not support) and then leave the pursuit count as `evaded`.
  `remaining = required − evaded`; when it is below 1, `required++`.
  If more cops are engaged than `remaining`, `required` grows by the difference.
  When `remaining ≤ NumCopsToTriggerBackup` → BACKUP_REQUESTED, then after `BackupCallTimer` (counted only while in
  sight) a new wave (`:1830-1847`, `:1925-1931`). The HUD shows "cops remaining in wave" = `max(remaining, 1)`.
* `GetAdjustedCopCounts`: `max = clamp(remaining, 0, ∞)` (player pursuit). In cooldown `min = max = min(max,
  NumPatrolCars)`. `want = clamp(Σcount_nonheli, min, max)`. For each non-heli row in order:
  `adj = int(count·want/nominal + 0.5)`; if non-zero, `want −= adj` and `nominal −= count`. The heli row is copied as is.
* `CopRequest` (the cop-spawn gate is `spawnCopTimer < 0`, P§8): `need_i = max(0, adj_i − present_i)`.
  Weight = Chance (100 if 0), 0 when `need = 0` or for a heli not allowed yet. A weighted random pick, spawned only if
  `activeCopCars < 8`.
* **RemoveUnwantedVehicles** (`:2224-2354`, every main tick): for a type with surplus (`present − adj ≥ 1`), picked with
  reservoir odds, flee up to 2 of the **farthest** of that type (+40 m if the cop cannot see you). A cop that can see you
  flees only while more than 2 cops can see you. Flee = AIGoalFleePursuit.

### 6.4 Priorities when over budget (`UpdateCopPriorities`, `:2035`)
Before a scripted spawn, with active count > 8: unspawn the active cop with the lowest
`dot(unit(cop − player), playerFwd) + (InView ? 1 : 0)` (behind you and off-screen first).

### 6.5 Despawn and respawn (`UpdatePatrols`, `:1330-1437`; `AISpawnManager.cpp:55-80`; `AIVehicle.cpp:1200`)
```
respawnAvailable = d_cam > 150 && (d_cam > 275 || (camActive && −0.707 <= dot(unit(pos−cam), camFwd) <= 0.8))
canRespawn:  if !respawnAvailable: sawFlag = true
             (sawFlag && timeSinceSpawn > 8) || (respawnAvailable && timeSinceSpawn > 10)
destroyed:   remove if canRespawn && offscreenTime > 3
alive:       remove if canRespawn && respawnAvailable, except for a pursuit cop within 375 m (heli 600 m) of
             target + 75 m·v̂_target
also remove every active cop when !VehicleSpawningEnabled(true)   // e.g. during the post-evade lockout
```
Removal = leave the pursuit or roadblock and `UnSpawn` (deactivate; the heli clears `gHeliVehicle`).
Because the lockout starts on evade (`ICopMgr::LockoutCops(true)`, `AIPursuit.cpp:2064`), all cops are unspawned on the
next manager tick after an evade, as decompiled **[C]**. Whether that is visible in game **[?]**.

### 6.6 Damage, destroyed and too damaged
* `DamageVehicle::OnImpact` (`Physics/Behaviors/DamageVehicle.cpp:134-216`): no damage beyond `damagespecs.SUPPRESS_DIST`
  from the camera, or from smackables with `NO_CAR_EFFECT`.
  `ratio = force/(FORCE·1000)`; zone from the hit arm and normal (front, rear, left, right and corners, top, bottom).
  `dmg = ratio·zone.HitPointScale/HIT_POINTS`, added when `> HP_THRESHOLD/HIT_POINTS`. Destroyed when the total is above
  1 (`EVehicleDestroyed`). Values **[?]** (`damagespecs` class).
* `DamageCopCar` (`DamageCopCar.cpp`): smackables damage a cop only if immobile or heavier than 2000 kg. Every 0.1 s it
  checks for being flipped: not all wheels down, `dot(up, groundNormal) < 0.5` and `up.y < 0.5`. Flipped for 6 s →
  destroyed. Flipped while the player is the collision cause → destroyed at once.
* `AIActionTooDamaged` (score 1, `AIActionTooDamaged.cpp`): when `IsDestroyed`: `EndPursuit` (lights off), no gas, brake
  0.25 while above 2.5 m/s (unless in shock). The manager then detaches it (bounty and combo, P§9; `cops_destroyed++`)
  and removes it after 3 s off-screen.
* Breaker zones (`MBreakerStopCops`, `:689-728`): every cop within the radius for the duration is destroyed (scripted).

---

## 7. Patrol cops and pursuit start
* Count: `wantPatrol = NumPatrolCars` of the player's row (1 for heat 1–8, 8 for heat 9; 1 if no row). It is 0 when any
  pursuit exists (1 if the race has opponents), and 0 while scripted spawns are pending. One spawn per manager tick when
  short (`:1330-1370`).
* Driving: `AIActionTraffic` (`AI/Actions/AIActionTraffic.cpp`). Lane following on the traffic nav: look-ahead
  `lerp(10,30,ramp(v,0,25)) + v·dt + radius`. Speed 35 mph, or 55 mph on roads with 4+ lanes (`:89-90`).
  Corner limit `GetSpeedLimitForCurvature(friction 1.6 for cops (0.6 traffic), curv, posted)`. Slows for occluding
  traffic with the stopping table 3→50 m over 0→80 m/s. Cops ignore the traffic accel cap and accident logic.
* Noticing the player (`AIVehicleCopCar::WatchForPerps/CheckForPursuit`, `:170-227`), every think while not in pursuit:
  * LOS: patrol cars use the **fixed 150 m front / 50 m rear**, because the row values apply only to cops already in a
    pursuit. Rear = `dot(copFwd, dirToPerp) ≤ −0.3`. Ray from cop +0.5 m to perp +0.5 m. A perp hidden from cars stays
    hidden from this cop until seen.
  * Trigger: seen AND (already in a pursuit OR active 911 call OR hit traffic within the last 1 s OR int(heat) ≥ 4 OR
    speed ≥ 65 mph). The 911 call comes from the speech AI (audio.md §5.1, 9.1).
  * Then the cop acquires the target. The manager adds it to the target's pursuit or creates a new `AIPursuit`
    (`UpdatePatrols`, `:1431`; `UpdatePursuits`, `:1869-1967`).
* LOS in pursuit (`CanSeeTarget`, every 0.25 s, `AIVehiclePursuit.cpp:148-167`): row `frontLOSdistance` /
  `rearLOSdistance` and the same rear cone. A sighting sets `timeSinceSeen = −0.25`. It is also forced false while
  `!PursuitMeterCanShowBusted` (the first 2.5 s after cooldown starts).
* Racers: cops also watch AI racers when `CanPursueRacers` (the race has opponents and no racer is pursued yet).
  Those pursuits use `max_cops` 3 (2 while the player is pursued).

---

## 8. Radio, siren and speech that affect logic
* The formation re-pick waits for `speech_finished` (2.5 / section 0). In Unity: no new formation while the dispatcher is
  in its setup call (15 s after it ends) **[I]**.
* Siren (`AIVehiclePursuit::UpdateSiren`, `:169-301`). Sound only: YELP for 0.5 s at random
  `SirenInitMinPeriod + rand(SirenInitVariation)` in the first 5 s of a player pursuit. Then WAIL, YELP (high heat: SCREAM
  up to `SirenMaxScreamTime` every `SirenScreamPeriod`) while collapse, finisher or certain speech is active and the cop
  is in formation. Otherwise WAIL with a YELP after `SirenWailPeriod` for `SirenMaxYelpTime`. Roadblock cars always WAIL.
  Destroyed: DIE.
* Speech messages with no gameplay effect: `MUnspawnCop(reason)`, `MReqRoadBlock(0/1)` "Created"/"Dodged",
  `MReqBackup(16/64)`, heli `SelfStrategy` / `IntentToRam` / `Quadrant`.

---

## 9. Port status (`Runtime/Game/Police/*`, round 2, 2026-10-24)

The first-round table here compared the game with the old `Systems.cs` / `AIDriver` cops; those are gone. The police
now live in `Runtime/Game/Police/`: `PursuitSystem.cs` (+ partials `PursuitPerp.cs`, `PursuitBreakers.cs`) = AICopManager
+ the player's AIPursuit + AIPerpVehicle, `CopCar.cs` = AIVehicleCopCar/AIVehiclePursuit with the goal/action
system, `CopDriver.cs` (drive target + speed, `performance_limiter`, potentials, separation), `Formation.cs`,
`Roadblock.cs` (roadblocks, spike strips, per-wheel tire state), `Helicopter.cs`, `RoadNavCop.cs` (lane types, seek
ahead), `PoliceData.cs` (heat rows, support rows, cop types, roadblock setups), `PoliceZones.cs` (track path zones).
Sections 1-8 are ported as written; what still differs:

| Unity | Game |
|---|---|
| Damage: DamageCopCar on the cop's chassis (round 3, §9.2); the old Δv/25 rule only with `CopCar.UseSuspensionSimple` off | as ported; the takedown credit (player touch within 3 s) **[?]** |
| `aivehicle.TETHER_WEIGHT` read per cop type: 0 for every cop (round 3) | same |
| Low-level driver: `CopDriver` steers at the drive target and PIDs the drive speed | AIVehicle::OnSteering / OnGasBrake / AIVehiclePid gains (shared with racers, §11.2) **[?]** |
| Helicopter altitude: the HeliSheet (round 3, §9.2), all sections loaded at once on a worker thread; a raycast until then | sheets streamed with the sections |
| Heli LOS "view in a tunnel": the camera inside a tunnel / overpass track-path zone | `AmIinATunnel(view, 1)` reads the view's precipitation inTunnel / inOverpass flags **[?]** |
| ~~Pursuits are cleared when a race starts; no AI-racer pursuits~~ (round 4, §9.3: the manager runs in races with the race table rows; one `RacerPursuit` per chased racer) | cops in races and pursuits of AI racers (§7) |
| No initial / restart lockout: cops can appear as soon as free roam starts | `mLockoutTimer = kCopLockoutTime` (60 s) at construction; `ResetCopsForRestart` sets TimeInactiveFor911 (120 s) in free roam. Its caller is not in the decomp **[?]** |
| Breaker zones: `PursuitSystem.BreakerStopCops(pos, radius, duration)` exists, but nothing triggers it yet (no pursuit-breaker smackables in the port) | the pursuit breakers post MBreakerStopCops |
| Search-mode Traffic speeds whenever the cop is in a pursuit (`InPursuitList`) | set in `AIActionTraffic::BeginAction` only when `GetLastSpawnTime() > 0`, in a pursuit and unseen (`AIActionTraffic.cpp:185-192`); same in practice |
| Siren: `CopAudio` (other agent) owns the siren AI; `PursuitSystem` keeps a muted source | `AIVehiclePursuit::UpdateSiren` (§8) |

### 9.1 Ported in round 2 (this pass)

* **Hiding spots** (`PursuitPerp.cs`, AIPerpVehicle::Update `AIVehicle.cpp:2128-2227`, PC `0x4293D0` checked:
  zone type 14 / 8 calls to `TrackPathManager::FindZone 0x727480`, 1.25 m, latch 0.05 s / 99999 s, 999). 34
  `TRACK_PATH_ZONE_HIDDEN` zones in L2RA (§12). The perp is in a zone when the polygon holds its x/z and the zone's
  elevation is 0 or within 1.25 m of the bottom of the car's body. Entering unseen (evade level ≥ 0.05, or no
  pursuit) latches after 0.05 s; entering in sight never latches until the car leaves and re-enters. `Data[0]`
  "Car" / "Heli" hides from one kind only, anything else (all 34 PC zones: 0) from both. Effects:
  * `mHiddenZoneTime += dT·HiddenZoneTimeMultiplier` while hidden and unseen; the cooldown uses
    `lostTime + hiddenZoneTime` (`AIPursuit.cpp:2003-2027`). Reset when seen.
  * cop LOS (`AIVehicleCopCar::CanSeeTarget :91-160`): a cop that fails to see a hidden perp latches
    `mPerpHiddenFromMe` and stays blind to it until the perp is no longer hidden.
  * heli LOS (`AIVehicleHelicopter::CanSeeTarget :119-202`) the same with IsHiddenFromHelicopters.
  * roadblock: its 60 m sight and distances are skipped while the perp is hidden from cars (`:1853-1872`); the
    cheat join still runs.
  * `TimeToHideInZone` (1 s, 3 s at heat 6) is never read **[C, PC]**.
* **911 calls** (speech-driven, see audio.md §5.1): `PursuitSystem.Set911CallTime` (bMax, PC `0x408FC0`),
  `Active911 = timer > 0` (counts down at the perp rate), `LockoutCops(false)`. The patrol trigger in
  `CopCar.WatchForPerps` (`AIVehicleCopCar.cpp:212-221`) now sees it. `LockoutCops(true)` uses the perp row's
  `TimeInactiveFor911` in free roam, else `kCopLockoutTime` 60 s (`AICopManager.cpp:2245-2258`).
* **Pursuit-start zones** (`TRACK_PATH_ZONE_PURSUIT_START`, 3 zones): out of a pursuit, every 11th perp update
  (30 Hz) with cop spawning enabled (not locked out) in free roam, a zone under the car lifts the lockout and posts
  MForcePursuitStart(heat) → `PursueAtHeatLevel` (`AICopManager.cpp:2098-2141`): heat raised to the minimum, a new
  pursuit with one car 340 m ahead along the road (`SpawnPursuitCar :800`, else 15 m behind), `ZeroTimeSinceTargetSeen`.
* **No-cop-spawn zones** (`TRACK_PACH_ZONE_NO_COP_SPAWN`, 3 large zones): `CheckSpawnPosition` rejects positions inside
  (`AISpawnManager.cpp:146-150`).
* **AIActionAirborne** (`AIActionAirborne.cpp`, PC `0x407080` checked): last action of AIGoalPursuit, StopShort,
  Ram/Pit/PullOver, HeadOnRam and FleePursuit (PC goal ctors `0x43B740..0x43BF70`). Score 0, so it replaces Race
  (0) but not PursuitOffRoad (0.01) or the score-1/0.1 actions. Attemptable when the previous check saw no wheel down
  and the cumulative timer passes 1.5 s; **the timer is never reset**, so after one long jump every later airborne
  think qualifies at once **[C, PC]**. While running, all controls are off; it finishes on landing.
* **AIGoal.cpp** (`0x418E20–0x43D83F`): the missing GC functions are factory ctors, `Construct` wrappers and deleting
  destructors. PC goal lists read from the AddAction string pushes match the decomp; the stripped
  `AIGoalPullOver` is `Construct 0x43BBB0 → AIGoalRam ctor 0x43B9B0` (the Ram list), so "PullOver" mode is decided by
  the goal name only. `ChooseAction 0x42B070` = `>=` score test as decompiled. IDA names of the trivial deleting
  destructors are folded and unreliable (`??_EAIGoalPullOver` sits in the StaticRoadBlock vtable `0x892B40`).
* **AIActionStrafe / Spline / JackKnife / None**: their names are referenced only by the factory registrations
  (`0x8840A1`, `0x884061`, `0x884121`); no goal adds Strafe, Spline or JackKnife on PC, so they are dead code
  **[C, PC]**. AIActionNone belongs to AIGoalNone (`0x43B5C0`), never given to cops.
* **Unspawn notifications**: `PursuitSystem.CopUnspawned(cop, reason)` mirrors `MUnspawnCop` (0 release / lockout
  clear, 1 over budget, 3 roadblock steal, 4/5 out-of-view steal, 6 respawn rules); the speech AI uses it.
* **Support speech messages**: `BackupMessage(type, denied)` = `MReqBackup` "Request" / "ReqDenied": 16 when the RAM
  SUVs are gathered (3 s before they spawn, `AICopManager.cpp:1483-1486`) or cannot be placed, 64 when Cross starts
  or cannot be placed (`:1662-1669`). The RAM delay now starts when the vehicles are gathered, as in the game.
* **Formation re-pick gate**: `speech_finished` now follows the speech AI: 15 s after SoundAI last had focus 1
  (`AIPursuit.cpp:1712-1730`); always true with the speech AI off or in a Challenge Series pursuit (taken as
  `GetIsPursuitRace` **[?]**).
* **Pursuit getters for the speech AI**: `IsFinisherActive`, `IsCollapseActive`, `AttemptingToReAquire`,
  `BackupEta` (mBackupCountdownTimer), `EvadeLevel`, `TimeUntilBusted` (`AIPursuit.cpp:2651`: busted timer > 0.03 →
  `min(1, t/5)` blended from the meter; evade level ≥ 0.05 → −1; else the pursuit meter), `IsPerpBusted`.
* **Breaker zones**: `ApplyBreakerZones` (first step of the manager tick, `:689-728`) and
  `NotifyGasStationExplosion` for the speech AI; entry points only (see the table).

### 9.2 Ported in round 3
* **damagespecs** (`aiprobe cops`, traffic_physics.md §11.1): DamageCopCar = DamageVehicle (zones, hit points, threshold,
  SUPPRESS_DIST, shock) on `TrafficVehicle.Damage` + its smackable rule; `CopCar` posts the destruction
  (`PursuitSystem.CopDestroyed`). Values: FORCE 20; HIT_POINTS / HP_THRESHOLD copmidsize 5 / 0.75, copghost 6 / 0.75, copgto
  7 / 1.5, copgtoghost 8 / 1.5, copsport* 10 / 1.5, SUVs 10 / 2, copcross 400 / 10; SUPPRESS_DIST 35 (Cross 25); HitPointScale
  0.5 front / rear / top, 1 sides and corners (Cross 1 front and rear), 0 bottom. copheli: DamageHeli with "choppers" (0 hit
  points).
* **TETHER_WEIGHT** (`aivehicle`, `aiprobe cops`): 0 for every cop aivehicle (copmidsize … copcross); only an unnamed aivehicle
  0xC4C970D0 has 100 and copheli uses `default` (0). So PursuitOffRoad's blend is the pure road offset; the code reads the value
  and its default is now 0.
* **HeliSheet** (World/HeliSheet.cpp; `Core/HeliSheet.cs`, `Police/HeliSheets.cs`): chunk 0x34159 per drivable section
  (`aiprobe heli`: 435 sections = every drivable section, 51 504 triangles, z −11.4 … 510 m; e.g. 166 m at the safehouse
  gate (1768, 2498), road 148 m). `HeliSheetCoordinate` ×3 (destination, working position 0.45 s, look-ahead 0.65 s) and
  `CheckHeliSheet` / `FilterHeliAltitude` (AIVehicleHelicopter.cpp:237-344): the destination is lifted to the sheet, the
  look-ahead's slope gives a smoothing velocity (30 % of the desired one, ooze 0.4·|cross|·side normal), and a working position
  under the sheet lifts the destination and turns the body's velocity up to it (ooze 0.3). `NeverIgnoreHeliSheet` = true, so
  the skid hits are clamped too. The search destinations use FilterHeliAltitude + 5 m. The ground raycast stays as a 1.5 m floor
  **[ours]**.
* **AvoidCamera** (AIVehicleHelicopter.cpp:363-411) with `AI::Math::SegmentSphereIntersect` (`Runtime/Game/AIMath.cs`, all of
  AIMath.cpp: AngleTo, TimeToIntercept, TimeToImpactXZ, PredictPosition, SegmentSphereIntersect, FloatSpring): a 7 m sphere 4 m in
  front of the camera (rad − 0.5 when already inside, over 2 m); a 0.2 s lead path into it moves the destination to the tangent
  plane and pushes the velocity out by |v·n|·((7 − r)/2 + 1.2).
* **Heli roadblocks**: `roadblockhelichance` is 0 on every PC pursuitlevels row (§12), so AIGoalHeliRoadBlock never runs on PC
  **[C, PC data]**; nothing to port.
* **911 / cell calls**: done in round 2 (§9.1); the cell-phone SMS / ECellCall side is the story's (Story.cs `Sms`).

### 9.3 Ported in round 4 (cops in races, per-perp pursuits, race tables)
* **One AIPursuit per perpetrator** (`AICopManager::UpdatePursuits` :1824-1967, `CanPursueRacers` :1783, `IsPlayerPursuitActive`,
  `PlayerPursuitHasCop`; `AIVehicleCopCar::WatchForPerps` / `CheckForPursuit` :170-227). `CopCar` now carries its pursuit
  (`CopCar.Pursuit : CopPursuit`, the IPursuit view: target, row, PerpInSight, jerk, roads / seek-ahead, potentials,
  PursuitMeterCanShowBusted, hidden) and its `Target` (AITarget; a cop that left a pursuit keeps the last target for the flee
  action). The player's pursuit is `PursuitSystem.PlayerPursuit`; an AI racer's is a `Police/RacerPursuit.cs`. A patrol car
  checks the human players first, then (`CanPursueRacers`: the race has opponents and no racer is pursued yet) every AI racer
  with the same rule (LOS 150 / 50 m; already pursued, 911, traffic hit within 1 s, heat 4+, or 65 mph+), posts
  `PursuitRequest(cop, target)`, and the manager's next tick joins or creates the target's pursuit.
* **RacerPursuit** = AIPursuit with `IsPlayerPursuit()` false: heat from the perp's `AIRacer.Heat` (1 at construction,
  AIVehicle.cpp:1959) raised to the base heat on attach (:780-784), escalating with the race table row's
  `TimePerHeatLevel × ScaleEscalationPerBucket(bin)` and clamped to the ctor's base / maximum heat (:629-669: ForceHeatLevel /
  MaxRaceHeatLevel when the race uses the world heat, else MaxHeatLevel caps); `GetAdjustedCopCounts` with max 3 cops, 2
  while the player is pursued (:2173-2180), scaled from the **global** (player's) row's cop list (`GetGlobalPursuitLevelAttrib`
  :2684); LOS / lost time / cooldown / evade (`EvadeLevel`), the busted meter (+1/s stopped within 15 m in sight, −2/s;
  busted at 5 s → `BailPursuit`, `GRacerInfo::Busted` + `ForceStop` = `Entrant.Busted/Out/DNF` + `AIRacer.ForceStop()`,
  :1965-1975), formations and collapse through the shared `PursuitFormationState`, `RemoveUnwantedVehicles` /
  `FleeCopOfType`, `ShouldEnd` (:2139). No stats, rep points, infractions, camera or speech (all gated on
  `IsPlayerPursuit` / `mAllowStatsToAccumulate`). **[ours]** no roadblocks, heavy / leader support or helicopter for racer
  pursuits: the manager's roadblock / support / heli code is bound to the player's pursuit (the race rows' support tables are
  not read).
* **Cops in races**: `PursuitSystem.Update` no longer clears the police when a race is on (RaceManager.Begin already calls
  `ResetCopsForRestart(true)`, Gameplay/CopReset.cs). `UpdatePatrols` wants `NumPatrolCars` of the global row, 0 with two or
  more pursuits, 1 with one pursuit only when the race has opponents (:1341-1352). The player's pursuit in a race uses the
  race table rows (`PoliceData.RowFor(heat, racing)`: `pursuitescalation/default.racetable` → pursuitlevels `race_01..10`,
  read from attributes.bin by `HeatRow.FromAttributes`; `useRaceHeatNow` = play mode Racing and not a pursuit race,
  AIVehicle.cpp:2262-2266; harness `aiprobe racetables` lists them). Busted while racing aborts the race (GRaceStatus on
  MPerpBusted **[?]** taken as abandon); evading in a race shows no result screen (`PursuitIsEvaded` → EShowMilestones only
  when roaming, AICopManager.cpp:1682-1686). **[ours]** a race not driven by Lua allows cops only when its event has
  CopsInRace / ScriptedCopsInRace / CopDensity > 0 (the exe's race scripts call Game.SetCopsEnabled; Lua Drive still does).
* **mAllowStatsToAccumulate** (:1631-1633: not racing, or a pursuit race) now gates the pursuit length, the bounty tick, cops
  damaged / destroyed / evaded counts and `CopsDisabled`.
* **Heat escalation** is `dT / (TimePerHeatLevel × ScaleEscalationPerBucket[min(bin, 14)])` of the current row (:1676-1699),
  clamped to the pursuit's base .. maximum heat; a new level re-picks the formation and re-arms the support priority check.
  The old `SecondsPerHeat` table is the heat rows' `ScaleEscalationPerBucket` with `TimePerHeatLevel` 1 (checked in
  attributes.bin: heat rows 1 s × 120..600, race rows 600..2000 s).
* **Takedown credit** (`IncNumCopsDestroyed` :917-955): any destroyed pursuit or roadblock cop counts, whoever wrecked it,
  while stats accumulate; the rep points are `aivehicle.RepPointsForDestroying[pursuit level]` (`CopType.BountyAt`, read from
  attributes.bin: flat per type except copsport 100000 at level 10) × the 1..3 bonus multiplier. The "player touched it in the
  last 3 s" rule is gone (the exe never checks the cause; `DestroyedByPlayer` stays as a stat only).
* **IsCopRequestPending** (:1760-1781) is public; the traffic manager spawns nothing while it is true (ComputeDensity 0).
* **Formations** moved to `Police/PursuitFormationState.cs` (pick, UpdateFormation, out-of-formation offsets, SetupCollapse,
  AssignCopsInCircle), unchanged in behaviour and shared by both pursuit kinds.
* **AIVehicle::DisableSimplePhysics** (AIVehicle.cpp:1337-1360): an AI racer leaving the off-world emulation now goes through
  `PlayerCar.SetPhysicsMode(Simulated)`, which applies the 1 s `INVULNERABLE_FROM_PHYSICS_SWITCH`; entering uses
  `SetPhysicsMode(Emulated)`.
* **Helicopter check** (AIVehicleHelicopter.cpp, AIActionHeliPursuit.cpp, AIActionHeliExit.cpp function by function): all ported
  in Helicopter.cs. `AIActionHeliExit::BeginAction` (:106-111) now restricts the exit seek point's x/z to the road net exactly
  (`RestrictPointToRoadNet` :347-361: a traffic nav at the heli along the player's heading, dirWeight 0; the heli keeps its own
  height + Exit_Height). `StartPathToPlayerCar` / `StartPathToPoint` submit a path whose `mBuildingPath` / `mPathTime` are
  never read (dead on PC **[C]**), so there is nothing to port.
* **AIPerpVehicle::OnCausedCollision** for an AI racer hitting traffic (:2483-2490): `AIRacer.LastTrafficHitTime` (direct hit
  closing above 4 m/s, intentional when the racer closes faster along the normal **[?]** post-contact velocities), read by
  `CheckForPursuit`.

## 10. Port plan status
1-11 of the first-round plan are done (driver interface, cop brain, pursuit controller, formations, spawn manager,
roadblocks, spikes, damage (simplified), support, helicopter (raycast altitude), patrols with the 911 and traffic-hit
triggers). Remaining, in order of value:
1. ~~damagespecs~~ (round 3). 2. ~~HeliSheet~~ (round 3).
3. ~~Cops in races and AI-racer pursuits~~ (round 4, §9.3: `RacerPursuit`, `race_01..10`, CanPursueRacers, the 3 / 2 budget).
   Still open there: roadblocks / support / heli for racer pursuits [ours: none], the speech kOtherTarget branch (audio).
4. The pursuit-breaker smackables that post MBreakerStopCops and the gas-station moment (world side).
5. The free-roam start / restart lockout (find the ResetCopsForRestart caller in the exe first).

## 11. Open items
1. ~~`aivehicle.TETHER_WEIGHT` and `damagespecs` values per cop model~~ (round 3, §9.2: `aiprobe cops`).
2. Low-level `AIVehicle::OnSteering/OnGasBrake/AIVehiclePid` gains (shared with racers; not specced anywhere yet).
3. `collisionreactions` per goal (`aivehicle.PlayerCollisions`): how heavy roadblock and ramming cops feel in contacts.
   `Tweak_RoadBlockMass 4400` is defined but unused.
4. ~~HeliSheet data~~ (round 3); the night spotlight. Whether `bIsPointInPoly` counts points on an edge **[?]**; the sheets are
   loaded all at once instead of per streamed section **[ours]**.
5. `AIPursuit::SpikesHit` (PC `0x40AFC0`) has no direct call; it is only reachable through the IPursuit vtable slot at
   `0x89289C` **[?]**. `Get911CallTime` is answered (speech-driven, 9.1).
6. Whether all cops really vanish right after an evade (6.5 lockout); confirm in game.
7. Rate semantics (0.5 / 0.25 / 0.125 of a 60 Hz sim) as in P§4.
8. Who calls `ResetCopsForRestart` (free-roam start lockout of 120 s?).
9. Whether a Challenge Series event is a "pursuit race" (`GetIsPursuitRace`): it skips the 15 s speech gate, forces
   the scripted pursuit flow and disables the 911 "scripted" path.
10. Round 4: the career bin's base / maximum heat (`GRaceStatus::GetBinBaseHeat/GetBinMaxHeat`, the AIPursuit ctor) are
   taken as 1 / `MaxHeat` (5) **[?]**; the support race tables (`supportracetable`) are not read; busted in a race = the race
   abandoned **[?]**; the traffic-hit test on AI racers uses post-contact velocities **[?]**.
11. Per-file status after round 4 (coverage.json): AIPursuit.cpp, AICopManager.cpp, AIVehicle.cpp (the human wrong-way is
   RaceManager's; `GetWorldAvoidanceInfo`, `GetLookAhead`, `UpdateRoadNavInfo` have no PC callers), AIVehicleCopCar /
   AIVehiclePursuit (siren = audio agent), AIGoal / AIAction, AITarget, AISteer, AIMath, AIActionRace / PursuitOffRoad / Ram /
   HeadOnRam / StopShort / StaticRoadBlock / TooDamaged / Airborne, AIRoadBlock(+Setups), AISpawnManager (the vehicle cache
   has no equivalent: cars are spawned and destroyed): ported. Dead on PC: AIActionSpline / Strafe / JackKnife / None (§9.1).

## 12. Track path zones used by the police (`TRACKS/L2RA.BUN` chunk 0x3414A)
Record layout in `Core/TrackPathZones.cs` (World/TrackPath.hpp:68-84). PC 1.3 counts by type: tunnel 70, overpass 143,
small overpass 178, streamer prediction 211, garage 8, **hidden 34**, traffic pattern 11, dynamic 14, neighbourhood 8,
jump cam 22, **no cop spawn 3**, **pursuit start 3**. Harness `copzones` lists them with the pursuitlevels 911 fields.

* Hidden (type 8): 34 small zones (7–43 m), all `Data[0] = 0` (hide from cars and helicopters), each with an
  elevation, e.g. game (4094, 136) z 18.5, (4737, 1011) z 14, (2730, 2194) z 106.5, (885, 3604) z 197,
  (1054, −774) z 109.5. `Position` lies outside the polygon (a reference point, not a centre).
* No cop spawn (type 13): centres (2104, 1382) 133×277 m, (47, 3165) 602×228 m, (851, −794) 353×211 m; elevation 0.
* Pursuit start (type 14): centres (4316, 493) 101×97 m, (1830, 2145) 135×182 m, (1129, −111) 135×192 m; elevation 0.
* pursuitlevels (attributes.bin, `copzones`): CTSFor911 dflt 1750, h1–h6 400/600/800/1000/1500/1750;
  NumCiviHitsFor911 5, 3/4/5/6/7/5; Lifetime911 180, 45×5/180; TimeInactiveFor911 120 everywhere;
  HiddenZoneTimeMultiplier 4, 4/5/6/7/8/8; TimeToHideInZone 3, 1×5/3; roadblockhelichance 0 everywhere.
