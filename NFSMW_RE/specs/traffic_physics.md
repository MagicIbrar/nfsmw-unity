# Traffic physics: the simplified vehicle models and the articulated semis

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/` (paths relative to it), speed.exe v1.3 (addresses from
`refs/nfsmw/config/SPEED_EXE_1_3/symbols.json` plus capstone/REA reads; the exe wins where it differs), attribute data
from `GLOBAL/attributes.bin`, collision geometry from `GLOBAL/GLOBALB.BUN`. Harness probe: `trafphys`
(`NFSMWStaging/Harness/ProbeTrafficPhysics.cs`): no argument lists every traffic-pattern vehicle's behaviours;
`trafphys <pvehicle>` dumps it with its parts; `trafphys bounds [MODEL]` the collision geometry; `trafphys react` the
collision reactions; `trafphys specs` the chassis numbers, static sag and top speeds. `[?]` = uncertain or inferred,
`[ours]` = our own addition. Port: `NFSMWUnity/Assets/NFSMW/Runtime/Game/TrafficPhysics/`, `Core/CollisionGeometry.cs`.

---------------------------------------------------------------------------------------------------------------------

## 1. Which behaviours a vehicle gets

A PVehicle builds one behaviour per mechanic from its pvehicle's `BEHAVIOR_MECHANIC_*` StringKeys (order from
`BEHAVIOR_ORDER`: RIGIDBODY, AI, INPUT, ENGINE, SUSPENSION, DAMAGE, DRAW, SOUND). The data (harness `trafphys`):

| pvehicles | RIGIDBODY | SUSPENSION | ENGINE | DAMAGE | RESET |
|---|---|---|---|---|---|
| all 23 traffic cars (`traf*`, parents street / van / truck) | RBVehicle | **SuspensionTraffic** | **EngineTraffic** | DamageVehicle | none |
| semis (`semib`, `semicon`, `semicrate`, `semilog`; MODEL SEMI, CLASS TRACTOR) | **RBTractor** | SuspensionTraffic | EngineTraffic | DamageVehicle | none |
| trailers (`trailer*`, CLASS TRAILER) | **RBTrailer** | **SuspensionTrailer** | none | DamageVehicle or none | none |
| 14 cops | RBCop | **SuspensionSimple** | EngineRacer | DamageCopCar | none |
| 56 racers | RBVehicle | SuspensionRacer | EngineRacer | DamageRacer | ResetCar |

**PVehicle::LookupBehaviorSignature (0x67F0D0)** overrides the pvehicle's keys before they load (checked in order, the
first match wins; a per-vehicle override map at +0x168 is consulted first):

| condition | mechanic | behaviour |
|---|---|---|
| sound disabled | AUDIO | none |
| animating (+0x15C) and class ≠ CHOPPER | SUSPENSION / ENGINE | **SuspensionSpline / EngineSpline** (nis.md §7) |
| animating | RESET / INPUT | none / **InputNIS** |
| driver REMOTE (6) | RIGIDBODY | RBRemote |
| driver HUMAN (0) | INPUT | InputPlayerDrag (drag style) / InputPlayer |
| drag style | DAMAGE; ENGINE on a CAR | DamageDragster; EngineDragster |
| class CAR and driver RACER (3) / NONE (4) / REMOTE (6) | SUSPENSION | **SuspensionSimple** |
| any | AI | the (driver class, vehicle class) table at .data 0x8F8890 (filled at start-up) |
| driver HUMAN or a player | EFFECTS | EffectsPlayer |

DriverClass (Sim/SimTypes.h:218): HUMAN 0, TRAFFIC 1, COP 2, RACER 3, NONE 4, NIS 5, REMOTE 6. So **AI racers run
SuspensionSimple too** (not SuspensionRacer); cops get it from their pvehicles. The port runs both on TrafficVehicle (§11,
ai_racer.md §11); the condition order was re-read from the exe (driver style at +0x144, class at +0x118, driver at +0x140).

So the coverage item "SuspensionSimple / SuspensionSpline / EngineTraffic" is really: traffic = SuspensionTraffic +
EngineTraffic; SuspensionSimple is the **cop** chassis; SuspensionSpline is the NIS car controller (it implements
`INISCarControl`, `SuspensionSpline.cpp:28`) and no pvehicle names it, so it is out of this area. Traffic has no
ResetCar: a flipped traffic car is never put back on its wheels (the despawn rules remove it).

Semis: the pvehicle `Trailer` RefSpec names the trailer (`semib`→`trailerb`, `semicon`→`trailercon`,
`semicrate`→`trailercrate`, `semilog`→`trailerlog`). `semi*` appear in the collegehighway and cityhighway patterns.

## 2. Shared base: Chassis (Physics/Behaviors/Chassis.cpp)

* **Body frame.** The RigidBody's position is the centre of the vehicle's collision-geometry root box and its
  `dimension` the root's half extents (§5). Every root has `pivot.y = half.y`, so the model origin is the box bottom
  (= the body sills). Wheel arms: `(∓(TRACK_WIDTH − SECTION_WIDTH/1000)/2, −dim.y, FRONT_AXLE | FRONT_AXLE − WHEEL_BASE)`
  in the body frame (SuspensionTraffic.cpp:365-381).
* **SetCOG** (:264): `cg.z = rear + (front − rear)·FRONT_WEIGHT_BIAS%` (0.5 airborne), `cg.y = ROLL_CENTER·in −
  (dim.y + max RIDE_HEIGHT·in)`. **Inertia** `Box(mass, 2·dim) × TENSOR_SCALE` (Sim/Common/Util.cpp:6).
* **ComputeState** (:277): inputs clamped; a destroyed vehicle (or blown engine) gets brake 1, e-brake 1, gas 0.
* **DoSleep** (:139): speed < 0.5, four wheels down, brake + e-brake > 0, no gas, |ω| < 0.25, no collision → damp by
  `1 − speed` (wheels stopped); else speed < 1, |ω| < 0.25, no gas → lateral velocity and yaw rate ×speed and the
  sideways force / yaw torque cancelled by `1 − speed`.
* **Wheel ground** (Common/Wheel.cpp): `compression = RIDE_HEIGHT·upness + w` where `w` = the road's height over the
  arm along the face normal; capped at TRAVEL with the excess lifting the body (`ModifyYPos`); a wheel counts only
  with compression > 0 and `upness > ENABLE_ROLL_STOPS_THRESHOLD` = **0.2** (.data 0x8F8760, read from
  SuspensionSimple::DoWheelForces 0x6ACA42).

## 3. SuspensionTraffic + EngineTraffic (the traffic car)

Exe: SuspensionTraffic Construct 0x6B6AB0 → ctor 0x6B20F0, vtable 0x8ABC00; OnBehaviorChange 0x69ED90, DoSimpleAero
0x69EE20, DoDriveForces 0x69EE90, Tire::UpdateLoaded 0x68F7A5. EngineTraffic Construct 0x6B6EB0, OnTaskSimulate
0x6A3480. The decomp matches the exe here; constants read from the exe:

| name | value | where |
|---|---|---|
| `BrakingTorque` (extern) | **4** | .data 0x8F8A90 (×BRAKES ft-lb ×1.3558) |
| `EBrakingTorque` (extern) | **10** | .data 0x8F8A94 |
| `TrafficRollAdjust` | 0.5 | SuspensionTraffic.cpp:523 |
| lateral factor | −2 | .rdata 0x890E94 in Tire::UpdateLoaded |
| EngineTraffic "engine braking step" | `|0|/inertia·dT` = 0 | 0x6A3740: `push ebx` with ebx = 0, so the PC build has the GC quirk too |

SuspensionTraffic each step: drag `−speed·DRAG_COEFFICIENT·v`; drive torque per grounded wheel, **front ×
(1 − TORQUE_SPLIT), rear × TORQUE_SPLIT**, ×0.5 (the reverse of the racer's convention; traffic transmissions have
split 1, so the rear wheels drive while EngineTraffic reads the *front* wheels' speed as the clutch side); steering
`input × 45°` for both front wheels; per wheel springs `k·x·(1 + prog·x)` + one-way shock (`SHOCK_STIFFNESS` both
ways, blown off above `SHOCK_BLOWOUT·9.81·mass`) + sway; tire load `max(4·d² − 3, 0.3)·spring` (d = how square the
wheel stands on the face); tire:

* brake torque `brake·BRAKES·1.3558·4` against the wheel's spin, e-brake (rear) `EBRAKE·1.3558·10`;
* longitudinal = wheel torque / radius (no friction circle); with the e-brake above 0.5 and skid > 1 m/s:
  `−2·v_fwd·load·GRIP_SCALE/skid`;
* lateral `−2·v_lat·load·GRIP_SCALE`, ÷skid above 1 m/s, clamped to `|v_lat/dT·¼·mass|`; at |v_fwd| > 1 the
  longitudinal loses `sin(slip)·lateral·0.5`, below it the lateral fades with |v_lat|;
* the wheel always turns at road speed (`AV = (1 − ebrake)·v_fwd/r`).

Wheel torques act at the contact points about the CG with the lever's world y halved.

EngineTraffic: gear ids 0 R / 1 N / 2.. forward index `GEAR_RATIO` and `GEAR_EFFICIENCY` directly. The engine speed
chases `throttle·RED_LINE` through `w_clutch = idle + diff_w·ratio·(1 − idle/red)`; torque = TORQUE curve (ft-lb →
N·m) × throttle + ENGINE_BRAKING × (1 − throttle), × `ratio × (1 + TORQUE_CONVERTER·throttle·(1 − ramp(rpm, idle,
peak)))` × efficiency; none while the shift timer runs (`SHIFT_SPEED × ratio`, ¼ for downshifts) or when up.y < 0.3.
Shift points from Physics::Info::ShiftPoints without induction (first rpm from (idle+red)/2 in 50 rpm steps where the
next gear makes more wheel torque, else redline − 100; down = up·g2/g1). AutoShift never leaves first downward (its
down point there is idle + 100 at < 0.2 throttle, but the lower-gear check is 0). The engine never selects reverse;
only the AI shifts (§6).

## 4. Semis: RBTractor, RBTrailer, SuspensionTrailer, Dynamics::Articulation

* **RBTractor** (RBTractor.cpp; ctor 0x6B8060, SetHitch 0x69B100, CanCollideWith 0x69B3D0, PlaceObject 0x6A77D0):
  spawns the trailer with the tractor (`ISimable::CreateInstance("PVehicle")`, DRIVER_NONE) and hitches it;
  PlaceObject re-hitches and poses it (5th wheels together, same heading). A 0.1 s task (UpdateTrailer) copies brake
  and e-brake to the trailer and lets go when either body's up.y < **0.75**, their ups' dot < **0.8**, or either has
  fewer than 2 wheels down for > **2 s**. Hitched, the pair never collide (CanCollideWith). Unhitching frees the
  trailer's brakes and posts EJointDetached.
* **CollisionGeometry::CreateJoint** (0x6718F0): the tractor's `5THWHEEL` node with JointFemale (0x400) and the
  trailer's `5THWHEEL` with JointMale (0x800); anchors = their `fPosition` (body frame). For each child of the female
  node with ConstraintConical (0x100) or ConstraintPrismatic (0x200) and a MalePost (0x1000) child of the male node
  with the same name, a limit `FUN_005CB5D0(half.x, 2·half.y)` = atan2 in turns ×360 → **SEMI: prismatic ±80.5°
  (yaw), conical ±11.0° (pitch/roll)**; created by 0x48D1A0 → 0x48D050 (mode 0 prismatic, 2 conical, axis = the
  post's z × 2·half.y).
* **Articulation** (Articulation.cpp is empty in the decomp; exe 0x48A000-0x48D198). Joint (+0x10 anchor A, +0x1C
  entity A, +0x20 A immobile, +0x28 anchor B, +0x34 entity B, +0x38 B immobile, +0x44 constraint list). Resolve
  0x48C910 walks the joint list (0x911328) after the rigid bodies (RigidBody.cpp:1552); Joint::Resolve 0x48C5D0:
  world anchors → target = mass-weighted mean (`w = mA/(mA+mB)`, 1 or 0 if a side is immobile); 0x48AF30 moves each
  mobile body (rotating it toward the target first) so its anchor sits on the target; 0x48A000 (two mobile) /
  0x48A910 (one) cancel the anchors' relative velocity with impulses spread over **32** sub-steps (reduced mass
  `mA·mB/(mA+mB)`); then each constraint 0x48C270 measures the post angle and pushes back past its max/min (0x48B290).
  IsJoined 0x48AEE0.
* **SuspensionTrailer** (Construct 0x6B6BF0, Tire::UpdateLoaded 0x6906AD): no steer, no drive; brakes on all wheels
  `BRAKES·1.3558·4`, e-brake `EBRAKE·1.3558·10`; lateral `load·GRIP_SCALE·TrailerCorneringForce(|slip|)` with the PC
  Table at .data **0x8F8FC4 = {n 7, min 0, max 0.0333 turn (12°), scale 180, data 0x8F8D80 {0, .4, .8, 1.2, 1.6, 1.75,
  1.65}}** (the decomp says max 1/3 turn: the exe wins), rear ×1.5 (×0.75 on the e-brake), friction circle STATIC_GRIP
  then scaled to DYNAMIC_GRIP; unhitched: no inputs and the CG at the box centre; hitched: CG at ROLL_CENTER, z 0.
  Wheel torques use the full lever (no roll adjust).

## 5. Collision geometry (Physics/Bounds.h; Bounds.cpp needs RE: 0x66A5C0-0x6874D4)

LoaderBounds 0x687240 takes chunk **0x8003B900**; BoundsPack ctor 0x6870E0. GLOBALB.BUN holds one pack: 83
children `0x3B901`, each 0x11-padded to 16, `BoundsHeader {UCrc32 name, i32 count, i32 resolved, pad}` then
`Bounds[0x30]`: `i16 quat/32767 [4]`, `i16 pos mm [3]`, `u16 flags`, `i16 half dims mm [3]`, `u8 children`,
`i8 point cloud`, `i16 pivot mm [3]`, `i16 first child`, `f32 radius`, `u32 surface`, `u32 name`, `ptr`.
Collections are named by the Jenkins hash of the MODEL StringKey (`Lookup(UCrc32(MODEL))`; all 83 match), nodes by
the Jenkins hash of their name (`5THWHEEL` matches). `pos` is relative to the root centre, `pivot` is model space
(pivot = pos + root pivot). Children with PrimVsWorld/PrimVsObjects are the collision boxes; PrimVsGround is rare
(the body boxes do not collide with the ground; MeshVsGround marks a point-cloud mesh).

## 6. AI side (AIVehicle / AIActionTraffic)

* OnGasBrake for DRIVER_TRAFFIC (AIVehicle.cpp:913-924): `InShock()` (DamageVehicle shock timer) XOR neutral →
  Shift(shock ? NEUTRAL : drive gear); in shock no pedals.
* OnSteering divides by ISuspension::GetMaxSteering (45° for SuspensionTraffic).
* Accident over: brake 1 and steer 1 (tractors brake only), no handbrake.
* Player collisions: AIVehicle::SetGoal (:700-721) gives RBVehicle the aivehicle's `PlayerCollisions` reaction for the
  goal, else `PlayerCollisionsDefault` (traffic aivehicles have no records). Reactions (`trafphys react`) e.g.
  traffic FRONT (elasticity 0, roll 0.1, bias −0.9, mass ×0.95), REAR (0.1, 0.1, 0.9, ×0.85); van/truck/semi their own
  (semi FRONT ×7 mass). RBVehicle::ModifyCollision (:173-215) applies them when the other is a racer or human:
  elasticity += , mass and inertia × MassScale (> 0), CG += (0, RollHeight, WeightBias). Against the world any
  non-player takes the impact at its body-origin height (cog.y = 0, :141-147).

## 7. DamageVehicle (traffic damage)

Construct 0x6BACC0, OnCollision 0x6A85B0, OnImpact 0x69C030, OnTaskSimulate 0x68CB50. Zone from the hit's arm
(0.5 of dim) and normal (0.8); `ratio = force/(FORCE·1000)`; impact level `min(6, ratio·VisualScale)`, damage level
`min(2, …)`; hit points `ratio·HitPointScale/HIT_POINTS` above `HP_THRESHOLD/HIT_POINTS`, destroyed past 1. Object
hits: shock `Δv/SHOCK_FORCE` (> 0.2) held ≤ 1, draining over SHOCK_TIME. Ignored beyond SUPPRESS_DIST from the
camera. Traffic damagespecs have HitPointScale 0 on every zone: traffic is marked, never destroyed by hits.

---------------------------------------------------------------------------------------------------------------------

## 8. What the port does (Runtime/Game/TrafficPhysics/)

| File | Ports |
|---|---|
| `Core/CollisionGeometry.cs` | the bounds pack reader (§5) |
| `VehicleSpecs.cs` | behaviour names, CarSpec, tire section width, geometry root (dimension, pivot), damagespecs, reactions, trailer ref |
| `Chassis.cs` | Wheel + Chassis base (§2), incl. Ackermann, aero and the jump stabiliser for SuspensionSimple |
| `SuspensionTraffic.cs`, `EngineTraffic.cs` | §3 |
| `SuspensionTrailer.cs`, `Articulation.cs` (TractorHitch) | §4 |
| `SuspensionSimple.cs` | the cop and AI-racer chassis (§11), run by `TrafficVehicle` for every cop car and opponent |
| `EngineRacer.cs` | EngineRacer (EngineRacer.cpp, checked against the exe) for cops and opponents, with the AI's EngineDragster rules (§11) |
| `VehicleDamage.cs` | §7; DamageCopCar for the cops (§11) |
| `TrafficContacts.cs` | §6 reactions + world cog.y = 0, trailer friction |
| `TrafficVehicle.cs` | the component running a vehicle on those behaviours at 120 Hz |

`Traffic.cs` (`TrafficSystem.Available`) builds every traffic car as before (CarSpawner → PlayerCar), then attaches a
TrafficVehicle from the pvehicle's behaviours, which disables the PlayerCar; a TRACTOR also gets its trailer
(TractorHitch.Create). `TrafficCar` resets the physics and re-hitches on spawn, matches the start speed, parks the
trailer with the car, uses the model's gear, max steering and shock state, and no longer needs the racer-model
workarounds (handbrake hold against auto-reverse). The `TrafficCar` component and its role are unchanged.

## 9. Deviations and unknowns

* `[ours]` The PlayerCar stays on traffic cars but disabled; Rpm, Gear, WheelsOnGround, Grounded and SkidAmount are
  copied into its private auto-property backing fields by reflection once a frame. A proper hook in PlayerCar (an
  "external sim" flag that skips Step plus public setters) would remove that. PlayerCar.WheelState/WheelAudio stay
  stale for traffic (no traffic skid marks or tyre audio from wheel slip).
* WWorldPos is now the exe's (round 9, §12): Core.WWorldPos over the loaded packs with the tolerance as the search lift;
  the world-down raycast only stands in while no pack is registered `[ours]`.
* The Articulation solver is now the exe's (physics.md §25: Joint::Resolve 0x48C5D0 position pull onto the mass-weighted
  anchor, the 32-step reduced-mass velocity solve, the limit push-back); `TractorHitch.ExeSolver = false` keeps the old
  PhysX ConfigurableJoint (anchors and limits from the bounds; yaw = rotation about the tractor's up, swing ±cone).
* `[ours]` The collision box is still CarBuilder's render-bounds box (GameBootstrap.AddBodyCollider), not the
  PrimVsWorld/Objects boxes of the geometry; it sits above the body bottom so it does not fight the springs. The
  geometry root drives dimension, arms, inertia, damage zones and spacing.
* `[ours]` CarBuilder lifts the body by the ride height (cm) for a root on the road; with the sim carrying the root at
  RIDE_HEIGHT − sag (≈ 0.16 m for a sedan, 0.5 m for a trailer) that lift is undone for traffic and the collider moved
  down with it. Visual hubs are put at the arms' z (FRONT_AXLE from the box centre) `[?]`: for trailers this moves
  the wheels about 1.2 m back from where CarBuilder puts them.
* `[?]` RBComplex::Damp(x) taken as v, ω × (1 − x). COLLISION_INFO.impulseA taken as Δv (so force = Unity impulse).
* `[?]` Collision reactions: RollHeight/WeightBias are not applied (a PhysX pair shares one contact point, which
  CarContacts already moves for the player); elasticity and MassScale are.
* Smackable NO_CAR_EFFECT is read now (Runtime/Smackable.cs): a loose prop with it is no DamageVehicle impact.
* MaxInductedTorque and AerodynamicDownforce are Physics::Info's (round 9, §12: peak-torque rpm from the TORQUE samples;
  downforce speed·2·AERO·1000, linear). WheelMomentOfInertia = 10 (.data 0x8F8AB0, read from the exe).
* Not done: trailers are not PlayerCars, so cop avoidance (PursuitSystem.Avoidables) and racers' contact friction
  records do not see them; DrawTraffic's extra trailer wheels; jack-knife AI (cops only); the damage visuals (zone
  levels are exposed by VehicleDamage.Impact for the car-render port).

---------------------------------------------------------------------------------------------------------------------

## 10. Smackables (Physics/Common/Smackable.cpp, SmackableTrigger.cpp, SimpleRigidBody.cpp, SmokeableInfo.cpp)

Smackable.cpp, SmackableTrigger.cpp and SmokeableInfo.cpp are empty in the decomp; everything here is from speed.exe.
Harness: `smack seq` (the 75 world sequencer engines of TRACKS\L2RA.BUN and their system ids), `smack attrs` (the
smackable collections). Port: `Runtime/Smackable.cs`.

### 10.1 Spawner → scenery model → trigger
* Chunk 0x34027 loader 0x6829D0: per section `{+8 short section, +0xC short count, +0xE loaded}`, spawners of 0x40 bytes
  from +0x10 (`+0x24 mCollisionName`, `+0x28 mAttributes` → `SmokeableSpawner::FindAttributes` 0x66B670 in the
  smackable class 0xCE70D7DB, `+0x2C mSceneryOverrideInfoNumber` → {section, instance, flags; 0x10 = static instance
  hidden}, `+0x30 mUniqueID`, `+0x34 mExcludeFlags`, `+0x38 mSimModel`). Exclude mask: 1 in split screen, 4 while
  racing, 0x40000 or 0x80000 [?].
* Section OnLoad sub_6828D0: the section record's hidden bits (set at unload, sub_67BF80, for every spawner whose static
  instance is hidden, with the unload time) clear once **now > unload time + 180 s, and never while actively racing**;
  a hidden spawner gets no model. `World_RestoreProps` 0x74D320 restores everything (a new world). RESPAWN_TIME is
  never read by the exe.
* SceneryModel (ctor 0x67BC20): RestoreScene 0x6778F0 places a **virgin** SmackableTrigger (ctor 0x667E70: a WTrigger
  box with the bounds' half extents (+0x10/12/14 shorts × 0.001), centred on the instance, flags 0x40143 = enabled,
  one-shot, activation class 0x40, fire on entry); `no_trigger` → trigger off + StartSequencer; `start_sequencer` →
  StartSequencer. Any awake complex rigid body (cars, props) or trigger-enabled SRB (explosions) fires it
  (WTriggerManager::ProcessRB 0x781840 / ProcessSRB 0x781A00).
* ESpawnSmackable (id 0xEB626F77; ctor 0x63B660 ignores self-hits; the dtor 0x63B790 does the work):
  `ISimable::CreateInstance("Smackable", SmackableParams{M, virgin, model, simple=false})`; refused → the trigger is
  re-enabled.

### 10.2 Smackable (Construct 0x6895A0, ctor 0x688660, size 0x14C)
* Refused when MASS ≤ 1e-6, without bounds, at `CanSpawnRigidBody` (52 bodies) / `CanSpawnSimpleRigidBody` (72), or
  with ≥ 20 RBSmackables (gRBSmackableCount 0x92616C) when SimplifyOne 0x684DB0 finds no candidate.
* Body: half = max(bounds half, 0.025); `I = MASS·((2hy)²+(2hz)², (2hx)²+(2hz)², (2hx)²+(2hy)²)/6`, × MOMENT (a
  Vector3; components > 0). "RBSmackable" (RigidBody + EffectsSmackable) or, for SimplePhysics, "SimpleRigidBody"
  (flags 0x20B: no collisions, damped, owner gravity); then every BEHAVIORS entry. A bounds child "COG" sets the CG.
* OnBehaviorChange 0x684DF0: virgin and DETACH_FORCE ≠ 0 → `AttachedToWorld(true, max(DETACH_FORCE, 0))` (no
  gravity, no ground/world collision; immobile in contacts). Moment::React 0x488F70 breaks it loose when
  `m_other · v_normal > DETACH_FORCE` (resolved as a free collision in that step) → RigidBody::Detach 0x697050
  (WORLD_DETACHED 0xC91B90D9). DETACH_FORCE < 0 never breaks from contacts, only from Resolve* (explosions).
  0 or non-virgin: a free body (virgin non-placeable ones start asleep).
* OnTaskSimulate 0x684F10: age, AUTO_SIMPLIFY, the DROPOUT sink, SRB gravity. OnTask 0x67B6F0 (10 Hz):
  **RestLogic 0x670A30** (at rest = asleep and no object hit, or not modeling; acts on the third rest tick: a DROPOUT
  {time, speed} (both > 0) sinks it → Kill; on a face and asleep: never detached → RestoreScene, else PlaceTrigger
  (non-virgin) at the current transform and the model keeps drawing there; otherwise release + Kill),
  **OffWorld 0x66A170** (unless ALLOW_OFF_WORLD: off a face or under it for more than 1 s → release + Kill),
  **UpdatePriority 0x66A200** (`(CAN_SIMPLIFY·(in view ? 1 : 2) + camera distance) / radius`). The manager 0x685000
  sorts by priority and simplifies while there are more than 20 (Simplify 0x684C60: a collisionless SRB with the same
  motion).
* The model: OnBeginDraw 0x677830 hides the static instance and starts KILL_OFF_SCREEN; 0x66AF60 counts it down while
  unseen (reset in view; ≤ 0 = never) → ReleaseModel 0x66B630 (gone until the section rule above).
* Impacts (IListener::OnCollision 0x6772C0, only with an EventSequencer): |closing velocity| ≥ 1 → Impact 0x669E30:
  GROUND (3) → GROUND_COLLISION 0x2BF74E61, sev v·0.1; WORLD (2) → WORLD_COLLISION 0x7EBE81C0, sev v·0.022369863;
  OBJECT (1) → OBJECT_COLLISION 0xD59062C8 (+ PLAYER_COLLISION 0x2F698829 / VEHICLE_COLLISION 0x80B88C1D), same sev.
  SendImpacts 0x669D70: IMPACT_1..IMPACT_(⌊6·clamp01(sev)⌋+1) (table 0x905D28, up to 7) to **that system only**. In
  the data 54 engines have OBJECT_COLLISION and 6 GROUND_COLLISION; the breaker roofs (gasstationroof_seq,
  donutshoproof_seq …) have neither and are driven by their supports' EProcessAreaStimulus. The event context
  position is the last impact point.
* OnExplosion 0x677100 (needs the explosion's target bit 1 and ExplosionEffect > 0): pushed to `speed·ExplosionEffect`
  along the blast at the point (ResolveForce detaches), EXPLOSION 0xAB556D39 (+ EXPLOSION_DAMAGE 0xFFCD8A63).
* NO_CAR_EFFECT: RBVehicle::ModifyCollision 0x69AE0E keeps the car's speed against a free smackable (CarContacts);
  DamageCopCar counts a smackable over 2000 kg (0x8AAC54) or an immobile one as damaging.
* RBSmackable (ctor 0x67B7D0, vtable 0x8A98D0): RigidBody plus ShouldSleep 0x66A310 (not when joined),
  CanCollideWithGround 0x66A3A0 / CanCollideWithWorld 0x66A3C0 (off while attached; world checks every 4th/2nd frame
  below 2 / 15 m/s without a collision).
* SimpleRigidBody (the decomp matches; the object is 0x64 bytes on PC, pool of 96): integration (damping ×0.99/×0.98
  per step with flag 4), detection only, no impulses.
* NIS_NukeSmackablesWithinRange 0x6F3560: HideModel + ReleaseModel within the radius.

Data (`smack attrs`): defaults MASS 100, DETACH 0, MOMENT (1.5, 1.5, 0), KILL_OFF_SCREEN 8, ExplosionEffect 1,
NO_CAR_EFFECT 1, CAN_SIMPLIFY 50; glass/storefront DETACH 150, woodgate/rm_tollbooth 500, propanetank,
gasstationsupport and sailboat −1; traffic_cable, largewoodenobject and woodroof are `no_trigger`.

### 10.3 What the port does (Runtime/Smackable.cs)
* The prop's static BoxCollider stands in for both the trigger and the attached body [ours]. A touch by an awake
  non-kinematic body spawns it (no_trigger props never do). Ported: the limit of 20 complex bodies with priority
  simplification, MASS, MOMENT inertia, DETACH_FORCE (attached / break test `m_other · v_normal`, −1 = explosions only),
  WORLD_DETACHED, RestLogic (restore / refreeze non-virgin / dropout / release), OffWorld, AUTO_SIMPLIFY,
  KILL_OFF_SCREEN, the 180 s section rule (keyed by the prop's world position [ours]; `Smackable.RestoreAll()` =
  World_RestoreProps, not called yet), the per-system impact stimuli with up to 7 levels, the context position,
  OnExplosion and ESpawnExplosion (props in the radius; vehicles with fDoesDamage get `CarFragments.Explosion`, players
  only with fIncludePlayers), NO_CAR_EFFECT (`CarContacts.SetNoCarEffect`). COST_TO_STATE is counted once per prop.
* `[ours]` PhysX resolves the car against the fixed box before the break is known, so a break hands the car back its
  normal speed minus the inelastic share (all of it with NO_CAR_EFFECT) and gives the prop the shared speed plus half
  the sliding speed `[?]`. The box is the render mesh bounds, not the spawner's collision bounds `[?]`. Not done:
  CanCollideWithWorld frame skipping, the 52/72 body limits, EWakeObject, the COG bounds child, rigidbodyspecs GRAVITY,
  the explosion's expanding front (applied at once), the spawner exclude flags (WorldStreamer's side). `[?]` "actively
  racing" = a race past its countdown.

## 11. Cops on SuspensionSimple + EngineRacer; RBTractor remainder

* `CopCar.Init` attaches a `TrafficVehicle` from the cop's pvehicle (SuspensionSimple + EngineRacer, DriverClass COP
  2); the PlayerCar keeps input, audio and lookups and is disabled (the TrafficVehicle bridge feeds rpm, gear and the
  wheels on the ground; the nitrous button reaches EngineRacer). `CopCar.UseSuspensionSimple = false` keeps the racer
  model. A destroyed cop (`MarkDestroyed`) sets `TrafficVehicle.OwnerDestroyed` → Chassis::ComputeState full brake +
  e-brake, no gas. No CarReset (cop pvehicles have no RESET). RBCop's own ModifyCollision and DamageCopCar stay on the
  CopCar/PursuitSystem side; cops are not in TrafficContacts (no PlayerCollisions reactions) `[?]`.
* `EngineRacer.cs` ports EngineRacer.cpp's OnTaskSimulate in the exe's order (throttle, NOS, ECU, induction,
  shifting, three-state clutch, torque converter, engine braking, wheel coupling / launch spin, LimitFreeWheels, rev
  limiter, catch-up, Engine_SmoothRPM, shift potential) with AutoShift, DoNos, DoInduction, DoECU and the
  differential. Exe constants checked: ClutchStiffness 20 (0x8F8D9C), ClutchLimiter 300 (0x8F8DC8), SmoothRPMDecel
  {2.5, 15} (0x8AAE0C), CoastingPercent 0.65, IdleClutchRPM 800. Exe ≠ decomp: WheelDiameter 0x66BEE0 has one sidewall
  (rim + aspect·width); MaxInductedTorque 0x66C180 (largest TORQUE sample × full-spool boost); the speed limiter never
  cuts in (the speedometer it reads is already capped). Not ported: sabotage, blowing, the dragster parts.
* RBTractor (the decomp's RBTractor.cpp matches the exe's flow): unhitching now sends JOINT_DETACHED (EJointDetached
  for the tractor and the trailer) to each vehicle's sequencer through CarFragments `[?: the event's handler]`. Still
  `[ours]`: the PhysX joint for Dynamics::Articulation, Pose without SetVehicleOnGround, no SetInvulnerability
  pass-through, no ModifyCollision immobile flag.
* Bounds.cpp: the collision boxes (PrimVsWorld/PrimVsObjects children) are still not colliders for traffic and cops
  (the render-bounds box is); the geometry drives dimension, arms, inertia, damage zones and the 5th-wheel joint.

### 11.1 Round 3: AI racers, DamageCopCar, placement
* **AI racers** (`AIRacer.UseSimpleChassis`, default on; ai_racer.md §11): `VehicleSpecs.ForRacer` copies the pvehicle record
  with the opponent's own upgraded CarSpec and Suspension = SuspensionSimple (LookupBehaviorSignature, CAR + RACER), and
  `AIRacer.Awake` attaches a TrafficVehicle with DriverClass RACER (3). DamageRacer and ResetCar stay on the PlayerCar side.
* **EngineDragster** for an AI owner = `EngineRacer.Dragster` (TrafficVehicle.DriverStyle = DRAG, from PlayerCar.Dragster): no
  reverse, only first / neutral while staging, rev limiter only while staging (MAX_RPM after). The player's EngineDragster parts
  (perfect shifts, heat, overrev) remain the PlayerCar's.
* **DamageCopCar** (DamageCopCar.cpp; `Construct` per the cop pvehicles' BEHAVIOR_MECHANIC_DAMAGE): TrafficVehicle now builds
  VehicleDamage for it too, with the cop's damagespecs (harness `aiprobe cops`): FORCE 20 everywhere; HIT_POINTS / HP_THRESHOLD
  cops (copmidsize) 5 / 0.75, copghost 6 / 0.75, copgto 7 / 1.5, copgtoghost 8 / 1.5, copsport / copsporthench / copsportghost
  (an unnamed record 0x0491AA02) 10 / 1.5, copsuv / copsuvl / copsuvpatrol 10 / 2, copcross 400 / 10; SUPPRESS_DIST 35 (Cross 25);
  HitPointScale front / rear / top 0.5, sides and corners 1, bottom 0 (Cross: front and rear 1). Its OnImpact override: a loose
  smackable only damages a cop when immobile or over 2000 kg (`Tweak_MinMassSmackableForCopDamage`). CopCar turns
  `Damage.Destroyed` into PursuitSystem.CopDestroyed (the takedown is the player's when he touched it in the last 3 s `[?]`);
  CheckUpright stays CopCar.FlipCheck. The old Δv/25 rule only runs with `CopCar.UseSuspensionSimple` off.
* **Placement** `[ours]`: TrafficVehicle resets and speed-matches a body that moved more than `2·|v|·dT + 1.5 m` since its last
  step (PlayerCar.Teleport from CarReset, the rolling start, RestoreStartPositions, cop and traffic placement): the exe's
  PlaceObject + Behavior::Reset + MatchSpeed. CarContacts.Refresh gets the pre-step velocity and heading (head-on rule).

## 12. Round 9: SuspensionSimple / EngineTraffic / RBTractor / SuspensionTraffic / SuspensionTrailer / DamageCopCar

Function-by-function against refs/…/Physics/Behaviors and the PC exe (EngineTraffic::OnTaskSimulate 0x6A3480
disassembled). All `[C]` unless marked.
* **Wheel::UpdatePosition** (Common/Wheel.cpp): the wheel's own WWorldPos (`Core.WWorldPos`, sim axes over
  `Dynamics.WorldQueries.Sim`): tolerance min(max(−vy·dT, 0) + radius + |v_xz|·dT, height/2) is the search lift,
  usecache = IsOnGround, keep_valid = true; normal.w = (P0 − pos)·n (positive below the face); surface = the face's
  simsurface, SimSurface::kNull without one (w = −1). Was a world-down raycast from half the vehicle height above the
  arm (kept only while no pack is registered).
* **Chassis::DoAerodynamics**: downforce = Physics::Info::AerodynamicDownforce = speed·2·AERO_COEFFICIENT·1000 (linear,
  0x66BAE0) — was speed²·AERO, ~65× too little at 30 m/s for cops / AI racers.
* **Chassis::DoJumpStabilizer**: the ground normal and altitude are the body's WWorldPos (RBComplex::GetGroundNormal,
  RigidBody.cpp:477/1315, altitude = −w); the passive damping needs `!IsInGroundContact` (TrafficVehicle.GroundContact:
  a road-strip contact last step), was "no wheel down".
* **SuspensionSimple**: ICheater::GetCatchupCheat (TrafficVehicle.CatchupCheat = the AI racer's EngineRacer.CatchupCheat;
  null for cops) → drag × lerp(1, 0, c), downforce × lerp(1, 1.5, c) (≥ 1.5 drag racing), turning drag reduction
  lerp(0.23, 0.5, c) for a RACER. The rest of the file (Tire, DoSteering, DoWallSteer, DoDriveForces, DoWheelForces,
  OnCollision, Reset, MatchSpeed, DoAerobatics) was already 1:1. Not modelled: Tire::UpdateTime/EndFrame (empty),
  state.blown_tires (SuspensionSimple never reads it), the shift boost (EngineDragster::GetShiftBoost, 1 for AI shifts [?]).
* **PVehicle modes**: TrafficVehicle runs no behaviour while its PlayerCar is not SIMULATED (0x6865D0 pauses them;
  SuspensionSimple returns while !IsModeling).
* **EngineTraffic**: CalcShiftPoints = Physics::Info::ShiftPoints (Dynamics.PhysicsInfo, the same boost-free search) +
  MaxInductedTorque's rpm (largest TORQUE sample, was a 50-rpm scan); GetTorquePoint / EngineInertia from PhysicsInfo.
  PC check of OnTaskSimulate: the clutch map's min_w is IDLE, up.y < 0.3 (0x894B58) disengages, and the
  "transmission above target" step really is min(0, d): Abs() is called on the pushed 0 (0x6A3740), as the GC source
  shows. GetHorsePower / GetMaxHorsePower / GetMaxSpeedometer are read-outs nothing in the port uses.
* **SuspensionTraffic / SuspensionTrailer**: checked line by line — already 1:1 (BrakingTorque 4 / EBrakingTorque 10,
  TrafficRollAdjust, the cornering table, the unhitched ComputeState); mTimeInAir (trailer) has no reader.
* **RBTractor::Pose**: resetPos = tractor body origin + R·(m5thWheel − mTrailer5thWheel), then
  `TrafficVehicle.SetVehicleOnGround` (PVehicle 0x685810 for these behaviours: Rigorous height at resetPos + dim.y; each
  arm's face via a WWorldPos(0.05) → body height face + RIDE_HEIGHT − GuessCompression(m·g) + dim.y; the body turned to
  the axle/side mid-point plane; else height + dim.y; behaviours reset, SetSpeed(0)) `[?: per-wheel term]`. `[ours]` the
  trailer then takes the tractor's velocity (the exe leaves it at 0 and the joint pulls it).
  **Dead on PC / no-op**: RBTractor::SetInvulnerability's pass-through to the trailer — the only state a tractor ever
  gets is INVULNERABLE_NONE (AIVehicleTraffic spawn, :74); SetInvulnerability with a state comes from ResetCar (racers'
  RESET only) and AIVehicle::DisableSimplePhysics (racers and cops: AIVehicleRacecar.cpp:561, AIVehicleCopCar.cpp:36),
  never traffic. RBTractor/RBTrailer::ModifyCollision's MakeImmobile(false) vs an immobile body only undoes RBVehicle's
  "animating → immobile" (NO_CAR_EFFECT needs a mobile loose prop); semis animate only as cs_semi in an NIS, where the
  spline drives the body. OnRemovedVehicleCache / OnOwnerDetached / the dtor kill the trailer = TractorHitch.Park.
* **DamageCopCar::CheckUpright** (its 0.1 s "Physics" task; not while destroyed, animating, in an NIS): while modelling
  and not every wheel down, flipped = up·groundNormal < 0.5 && up.y < 0.5 (ground normal = the body's WWorldPos); > 6 s
  flipped → Destroy(); flipped with the car's causality the player's vehicle → Destroy() at once; mFlippedOver cleared by
  ResetDamage. Ported in VehicleDamage.CheckUpright, run by TrafficVehicle for DamageCopCar; the causality is
  `TrafficVehicle.CausedByPlayer` [hook for the police code; null = never]. CopCar.FlipCheck (police code) is the older
  stand-in and now duplicates it. OnImpact's smackable rule (immobile or > 2000 kg) was already in TrafficVehicle.OnHit.

