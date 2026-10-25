# Camera core: director, drive action, track/rear-view movers, ICE, front-end orbit (NFS MW 2005 PC v1.3)

Builds on `camera.md` (CubicCameraMover maths, camerainfo, speed tables, shake, terrain clamp), which stays the
reference for the per-frame chase algorithm. This file covers everything around it, and §1 corrects `camera.md`
where the exe disagrees.

Sources:
* speed.exe, read with Ghidra/REA pseudo-C (`decompiled/camera/FUN_*.c`) and capstone with the IDA symbol table
  `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`. Names in that table are given as `Class::Method`.
* GameCube symbols (`refs/nfsmw/config/GOWE69/symbols.txt`) for the class and method list, and the decomp headers
  `Camera/*.hpp`, `ICE/ICEData.hpp`, `Gameplay/GRaceStatus.h` and `AttribSys/Classes/frontend.h` for layouts.
* `GLOBAL/InGameB.bun`: 187 ICE tracks, parsed for value ranges.

Conventions:
* Addresses are VAs with image base 0x400000.
* Render space is Z-up; "sim space" (UMath) is Y-up. Unity mapping: render (x, y, z) → Unity (x, z, y), as
  `Coords.ToUnity` does. A car or camera frame is X forward, Y left, Z up.
* `bAngle` means 65536 per turn.
* **[?]** marks an inferred item; everything else was read from code or data.

## 1. Corrections to camera.md

| camera.md says | the exe says |
|---|---|
| anchor flags bc/bd/be/bf/c0 "(?)" | PC CameraAnchor packs the GC bools (§4.2): +0xBC mIsNosEngaged, +0xBD mIsBrakeEngaged (controller +0x18/+0x1C > 0), +0xBE mIsDragRace (vehicle driver style == 1), +0xBF mIsOverRev (rpm > redline − 500), +0xC0 mIsTouchingGround, +0xC1 mIsVehicleDestroyed, +0xC2 mIsGearChanging, +0xC3 mIsCloseToRoadBlock, +0xC4 mZoom. |
| +0xAC "impact … writer not found" | +0xAC is mCollisionDamping = drive+0x58 / 2. CDActionDrive::OnCollision (0x472460, a Sim::Collision listener) sets drive+0x58 = max(·, clamp((v−5)·0.025)·2) for type-2 hits over 5 m/s. Type 3 (> 3 m/s, ·0.05) feeds +0xB4 mGroundCollision and type 1 (> 20 m/s, ·0.01) feeds +0xB8 mObjectCollision. All three decay at 1/s and are divided by 2.0 (drive+0x64). |
| z1 "dir.0x6c … started by vt+0x18 (?)", z2 "neither caller identified" | z1 (drive+0x6C) is the **cinematic slowdown**. CDActionDrive::SetSpecial (0x481200, vtable +0x18) is fed every frame from Director+0x2C0 (CameraAI::StartCinematicSlowdown 0x471b60; PursuitStart passes 0.5 s, the photo-finish screen 0.75 s). `view+0xCC` is CameraParams::SimTimeMultiplier, the game-speed multiplier (§4.3). z2 (drive+0x50) is the **speedbreaker** (player vt+0x40). While it is above 0.001 it sets gGameBreakerCamera (0x911055), which blocks every scripted shot. |
| view+0xb4/+0xb8 = 15/65 DOF (?) | CameraParams +0xB0 TargetDistance, +0xB4 FocalDistance, +0xB8 DepthOfField. |
| PULLBACK "a toggle on player+0x2e0" | Drive Update reads the input's vt+0x3c each frame. While it returns true, the current setting is saved in DAT_008EE4B4 and POV 6 is used; the saved setting comes back when it returns false. Whether the input latches a toggle is [?]. |
| mode change snaps | CubicCameraMover::SetPovType (0x46b050) snaps (mover+0xA2) **unless both the old and new POV are chase POVs (2..6)**. Close↔far and the pull-back glide on the Hermite channels; bumper and hood snap. |
| "the race-start countdown uses the normal player camera" | Correct. The finish-line freeze happens only when a script calls Game.DetachCameraFromRacer (GRacerInfo+0x22). In the career graphs only the epic pursuit does this (§4.1). |
| speed tables | Verified: 7 tTable{2 entries, 0..1} at 0x8EE040 + mode·0x14, each with a pointer to 2×44 floats (modes 4..6 share values). They are dumped as is into `ChaseCamera.Table`. |

## 2. Class map

| Class / function | PC address | Notes |
|---|---|---|
| CameraAI::Update | 0x480e40 | creates one Director (0x2C8 bytes) per local player view (EVIEW 1, 2), then Director::Update |
| CameraAI::Director ctor | 0x480d20 | vtable 0x895110; +8 view, +0x10..+0x1C action StringKey (u64, u32, char*), +0x20 action |
| Director::SelectAction | 0x479900 | §3 |
| Director::Update | 0x479de0 | timers, SelectAction, Drive SetSpecial(+0x2C0), action vt+4 Update |
| Director::SetAction(StringKey) | 0x4797f0 | factory list DAT_009111FC keyed by StringHash32 |
| Director::TotaledStart | 0x479d80 | TrackCar, +0x2B8 = 2 |
| Director::JumpStart / EndJumping | 0x469e80 / 0x469ea0 | +0x2B8 = t·4/3 / cut if < 1 s left |
| Director::PursuitStart / EndPursuitStart | 0x469ec0 / 0x469fa0 | +0x2B4 = 2.5, +0x2C0 = 0.5 / cut if < 2 |
| Director::IsJumping | 0x4677f0 | +0x2B8 > 0 |
| CameraAI::Reset | 0x47cc50 | all directors: timers 0, Drive, action Reset |
| CameraAI::SetAction(view, name) | 0x479eb0 | |
| CameraAI::StartCinematicSlowdown | 0x471b60 | +0x2C0 = seconds if the action is Drive |
| CameraAI::MaybeDoPursuitCam | 0x471bf0 | from AIPursuit::OnAttached 0x423e71 |
| CameraAI::MaybeDoTotaledCam | 0x479f20 | from EShowRaceOverMessage 0x631257 when GRacerInfo+0x1D mTotalled |
| CameraAI::MaybeDoJumpCam | 0x479f80 (unnamed) | from EPlayerAirborne 0x63a853 |
| jump trajectory predictor | 0x471cd0 | 3 s ballistic arc against the world |
| "jump cams allowed" | 0x469e30 | FEDatabase game-mode bits; then profile+0x47 (the Jump Cams option) |
| CDActionDrive ctor / Update / AquireCar / OnCollision | 0x484c20 / 0x472740 / 0x4725d0 / 0x472460 | vtable 0x895150; creates a CubicCameraMover (0x481bd0) and a RearViewMirrorCameraMover |
| CDActionTrackCar ctor / Update | 0x484fc0 / 0x4733f0 | vtable 0x895190; mover TrackCarCameraMover 0x47aa20 |
| CDActionTrackCop ctor | 0x485210 | vtable 0x8951D0 |
| CDActionIce ctor / Update / GetNext | 0x4856e0 / 0x484270 / 0x473e80 | vtable 0x894CA0; ICEAnchor + ICEMover (0x4846c0) |
| CDActionShowcase ctor / Update / AquireCar | 0x4854a0 / 0x473bb0 / 0x473ac0 | vtable 0x894C54 |
| CameraAnchor::Update | 0x468fa0 | camera.md §5 |
| CubicCameraMover | 0x481bd0 ctor, 0x47d3c0 Update | camera.md; SetPovType 0x46b050 |
| TrackCarCameraMover ctor / Init / Update | 0x47aa20 / 0x4745e0 / 0x47de30 | vtable 0x894D10, type 6 |
| IsAnyCopNear | 0x4744b0 | an active cop within 30 m |
| TrackCopCameraMover ctor / Init / Update | 0x47ab00 / 0x475350 / 0x47e190 | vtable 0x894D60, type 7 |
| TrackCopCameraMover::FindPursuitVehiclePosition | 0x475170 | |
| Bezier::GetPoint | 0x46b6b0 | (1, s, s², s³)·basis·points |
| RearViewMirrorCameraMover::Update | 0x476690 | vtable 0x894E48, type 5 |
| CopViewCameraMover::SetDesired / Update | 0x475a50 / 0x4762d0 | type 16; not used by the director [?] |
| SelectCarCameraMover ctor / Update | 0x476870 / 0x4769e0 | vtable 0x894E90, type 9 |
| SelectCar CreateCameraMatrix / FindBestAngleGoal / SetDesiredOrientation / SetVRotateSpeed | 0x46b7e0 / 0x46b980 / 0x4770b0 / 0x476e70 | |
| GarageMainScreen::UpdateCurrentCameraView | 0x7a94c0 | angle_entry_/angle_final_ |
| ICEManager (TheICEManager 0x8EE320) | Init 0x47f310, Resolve 0x478320, ChooseCameraPlaybackTrack 0x483d30, ChooseGoodSceneCameraTrackIndex 0x47ef70, GetCameraData 0x47b3f0 | +0x28 nState (0x8EE348), +0x60 nPlayGenericGroupHash (0x8EE380) |
| ICETrack::GetCameraData (parameter) | 0x47b030 | §7.2 |
| ICEMover SetDesired / Update | 0x482160 / 0x482ec0 | vtable 0x894F70, type 14 |
| ICEManager::GetSlope | 0x47f490 | §7.3 |
| ICE::ShowOverlay / HideOverlay / GetOverlayName | 0x46ea50 / 0x46eae0 / 0x46e9c0 | gIceOverlays 0x8EDEEC |
| camera shaker: Start / Value / Update / Apply / MaybeShake | 0x64c1d0 / 0x64c270 / 0x64c2d0 / 0x64c420 / 0x64c5f0 | §4.4 |
| ECinematicMoment handler | 0x640550 | SetGenericCameraToPlay(group, track) + SetAction(CDActionIce) |
| ENIS-type handler (Intro/Outro/Arrest/Moment) | 0x640f00 | starts NISActivity; "Moment" NIS gated by gGameBreakerCamera, split screen, pursuit race, sim time > 2 [?] |
| Camera_SetGenericCamera (Lua) | 0x604a50 | |

## 3. CameraAI::Director: which camera when

Fields: +0x2B4 pursuit timer, +0x2B8 TrackCar timer, +0x2BC smooth-return flag, +0x2C0 slowdown seconds.

**Update** (0x479de0):
```
if !paused:                   +0x2B8 -= dT   (when > 0)
if !FE-layer-up (0x525b20):   +0x2B4 -= dT   (when > 0)
SelectAction()
if action is CDActionDrive: action.SetSpecial(+0x2C0)
action.Update(dT)
```

**SelectAction** (0x479900):
```
if TheICEManager.nState > 0: skip to GetNext            // ICE editor
if +0x2B8 < 0: +0x2B8 = 0; want Drive; +0x2BC = 1
if +0x2B4 < 0: +0x2B4 = 0; want Drive; +0x2BC = 1
if 0 < +0x2B4 < 2: want CDActionTrackCop
if !gGameBreakerCamera && view mover type == CM_DRIVE_CUBIC && anchor && JumpCamsAllowed()
   && (speed > 20 || (speed > 10 && mIsTouchingGround)) && mIsCloseToRoadBlock:
     want CDActionTrackCar; +0x2B8 = 2; deliver MGamePlayMoment("MomentStrm", 0x2D8ACB81)      // roadblock shot
if INIS && INIS.IsPlaying (vt+0x3C) && scene (vt+0x48) && scene.IsControllingCamera (vt+8):
     +0x2BC = INIS vt+0x64; want CDActionIce; +0x2B4 = +0x2B8 = 0
else if TheICEManager.nPlayGenericGroupHash != bStringHash(""): want CDActionIce          // generic ICE camera
else if action is CDActionIce:
     TheICEManager+0x78 = 0; want Drive; INIS.FireEventTag("ICEEnd"); post message 0x20D60DBF
next = action.GetNext(); if next.name != "": want next                 // only CDActionIce returns its previous action
if want != action: new = factory(want); +0x2BC = 0; +0x2C0 = 0; replace action; SetNewSndCamAction(want, view)
```
The new action's constructor reads +0x2BC **before** it is cleared, so a Drive created after a timed shot starts
slowed (§4.3).

**Event entry points:**

| Event (poster) | Function | Conditions | Effect |
|---|---|---|---|
| pursuit begins: AIPursuit::OnAttached | MaybeDoPursuitCam 0x471bf0 | ICE editor off; JumpCamsAllowed; no INIS; no race, or (!GRaceParameters::GetIsPursuitRace && race time ≥ 1 s); target not in a drag race (vt+0x30 != 1); its view has a director; !gGameBreakerCamera | PursuitStart: if +0x2B4 ≤ 0: +0x2B4 = 2.5, +0x2C0 = 0.5, MGamePlayMoment "MomentStrm" (0x88BFF834), MMiscSound(1). Result: 0.5 s of slowed drive camera (§4.3), then 2 s of TrackCop, then Drive with smooth return. |
| race over and the racer is totaled: EShowRaceOverMessage | MaybeDoTotaledCam 0x479f20 | not split screen (0x6e8df0 == 0) | TotaledStart: CDActionTrackCar, +0x2B8 = 2 |
| player airborne: EPlayerAirborne | MaybeDoJumpCam 0x479f80 | ICE editor off; JumpCamsAllowed; no INIS; not a drag race; director !IsJumping; !gGameBreakerCamera; speed ≥ 10 | predict(3 s) → avgH, peakH, flightT. Need avgH < 20.1, peakH < 20.1, flightT < 3.1, avgH > 1. Shot when peakH > 2.5 (1.8 inside a TrackPathManager zone of type 0xC, the jump zones: FindZone 0x727480, 2-D point in polygon; 22 in L2RA) and flightT·speed > 25 m (15 m) (tables 0x894658 / 0x894660). Then SetAction TrackCar, +0x2B8 = **clamp(2·flightT, 1, 4)**·4/3 (the second out-parameter; earlier versions of this file said avgH), "MomentStrm" moment, and SoundAI notified. |
| roadblock ahead (polled) | SelectAction | above | TrackCar 2 s, mode 2 (§5) |
| photo finish: PhotoFinishScreen ctor 0x58e701 (also OnlineRacer::Finish) | StartCinematicSlowdown(view 1, 0.75) | action is Drive | slowed, zoomed drive camera (§4.3). The screen (InGamePhotoMaster) is pushed by FE_ShowPostRaceScreen at **every finish the player wins**, not only close ones. 0.75 s real time later it hides the HUD, pauses the sim, sets ICE real time and posts ECinematicMoment("Cinematics", GetPhotoFinishCamera / "DefaultFinish" for pursuits / "DefaultSpeed" for speedtraps) → the generic ICE camera (§7.6); 0.75 s after that the photo appears (decomp PhotoFinish.cpp:130-316). |
| scripted moment: ECinematicMoment | 0x640550 | | SetGenericCameraToPlay(group, track); SetAction(CDActionIce) |
| NIS (intro, arrest/busted, outro, moments) | SelectAction | INIS scene controls the camera | CDActionIce (§7) |
| race restart / world init | CameraMoverRestartRace 0x480ce0 → CameraAI::Reset | | timers 0, Drive, movers ResetState |

JumpCamsAllowed (0x469e30): returns false in some FEDatabase game modes (flag bit 2 with mode 2, bit 6, or bit 3
with an online check). These are split screen and online [?]. Otherwise it returns the profile's Jump Cams option
(+0x47). The roadblock and pursuit shots are gated by the same option.

Jump trajectory predictor (0x471cd0): it walks N steps over 3 s of the car's ballistic path (with the car's gravity)
against WCollisionMgr height queries. It returns the average height above ground. The out-parameters are the peak
height and the time of the last step before the path hits the ground.

## 4. CDActionDrive (0x472740) and the anchor

### 4.1 Per frame
```
gCinematicMomementCamera = gGameBreakerCamera = 0
+0x58, +0x5C, +0x60 -= dT (≥ 0)                       // collision timers
if no player: release the vehicle and return
AquireCar(); anchor.mIsVehicleDestroyed = vehicle vt+0x7C
if GRaceStatus:
   if mover.OutsidePOV && race has a time limit && time left ≤ 0 && !racer.mFinishedRacing: return   // anchor freezes
   if racer.mCameraDetached (+0x22, Game.DetachCameraFromRacer): return                          // anchor freezes
anchor.mIsCloseToRoadBlock = 0
if vehicle AI → pursuit → roadblock distance d with 3 < d < speed·0.25:
   for each active roadblock vehicle 3..speed·0.25 m away with cos(dir, F) > 0.7 and lateral < 2.75 m:
       mIsCloseToRoadBlock = 1; DAT_00911056 = 1 (TrackCar mode 2)
input: mover.SetLookBack(input.LookBack && !vehicle vt+0x20)
anchor.mIsBrakeEngaged = controls+0x1C > 0 || controls+0x18 > 0
+0x68 (0.3 s after a smooth start): when it crosses 0.05 → 0x73def0(0.2) [?]
z1 (+0x6C): flag +0x70 ? (z1 ≥ 1 ? 1 : z1 + 4·dT/D) : (z1 ≤ 0 ? 0 : z1 − dT/D)          // D = DAT_008ED454
if z1 > 0.001 || gCinematicMomementCamera: view.SimTimeMultiplier = m = 0.25 + 0.75·(1 − z1), m > 0.9 → 1
z2 (+0x50): player vt+0x40 (game breaker) ? min(1, z2 + 4·dT) : max(0, z2 − dT); z2 > 0.001 → gGameBreakerCamera = 1
POV: input vt+0x3C → save setting in DAT_008EE4B4, SetPovType(6); else restore the saved one, else SetPovType(setting)
anchor fill: dragRace, touching ground (count from vt+0x4C > 0), NOS (engine vt+0x20), over-rev, drift
     (grounded: ramp(|slip|·360, 0, 90)·ramp(speed vt+0x98, 10, 20)), gearChanging,
     CollisionDamping = +0x58/2, GroundCollision = +0x5C/2, ObjectCollision = +0x60/2,
     mZoom = 1 − 0.4·max(z1, z2), CameraAnchor::Update(dT, matrix, velocity, accel)
if view is 1 or 2: MaybeCameraShake(view−1, anchor.accel); for each ECameraShake source within radius + 2 m:
     MaybeCameraShake(view−1, −dir·strength/dT)
```
The anchor freeze is how the epic-pursuit ending leaves the camera behind: the career graphs call
DetachCameraFromRacer only in `race_bin_01/epic_pursuit` (`specs/career_stategraphs.txt` line 1621).

### 4.2 Constructor (0x484c20)
```
+0x50..+0x74 = 0; +0x64 = 2.0; +0x54 = view index; gCinematicMomementCamera = gGameBreakerCamera = 0
if director+0x2BC (smooth return): gCinematicMomementCamera = 1; z1 = 1; D = 2 s; +0x68 = 0.3
AquireCar; CameraAnchor (0x4716a0); anchor fill; CubicCameraMover(view, anchor, POV, …) (0x481bd0)
RearViewMirrorCameraMover(view 3?, anchor) (vtable 0x894E48)
```

### 4.3 Slow motion and dolly zoom
`view.SimTimeMultiplier` (+0xCC) is the game-speed multiplier of the player view. Its reader is
QuickGame::OnManageTime (0x6F4D10): Tweak_GameSpeed (0x901B1C) ≠ 1 wins; user mode 1 → 1; a camera multiplier below 1 is
the target speed directly (at least 0.01, constant 0x890DC4) and **replaces** the speedbreaker; at 1 the speedbreaker
(IPlayer vt+0x40, only without an INIS) asks for 0.25 and ramps back afterwards.
Writers:
* drive z1 (0.25…1);
* TrackCar 0.2 every frame;
* an ICE key's sim speed (§7.4).

TrackCop writes nothing, so it keeps the drive camera's last value. After PursuitStart's 0.5 s ramp that value is
0.25, so the TrackCop shot plays in slow motion.

mZoom divides LAG and multiplies FOV (a dolly zoom). Typical sequences:
* Pursuit start: 0.5 s ramp to 0.25× speed and zoom 0.6, then TrackCop at 0.25×, then a new Drive with z1 = 1 that
  fades to normal over 2 s.
* Jump or roadblock: TrackCar at 0.2×, then Drive fading from 0.25× over 2 s.
* Speedbreaker: zoom eases to 0.6 at 4/s. The game-speed part is the speedbreaker's own.

### 4.4 Crash shake (Rumble camera shakers, 0x64c1d0..0x64c5f0)
```
MaybeCameraShake(view, a):  a' = a·(0.7, 0.7, 1); m = |a'|; if m ≥ 100:
    i = (min(m, 600) − 100)·0.002; amp = 0.1 + 0.3·i
    if current value < amp: start(dir = −a'/m·amp, duration = 0.3 + 0.4·i, freq = 11.74)
Update: t += dT/duration; ends past the envelope
Value(t) = ((int)(11.74·t) even) ? env(t) : 0;  env 0x8F7FC8: (0,1) (.1,.7) (.3,.5) (.5,.4) (1,0)
Apply (views 1-3, only when Sim speed ≥ 1): v = dir·Value in camera space; eRotateX(−1200·v.x), eRotateY(2000·v.y) bAngle
```

## 5. TrackCarCameraMover (jump, roadblock, totaled)

Mode (+0xD4):
* 2 if DAT_00911056 (a roadblock was seen; the flag is cleared);
* else 1 if the view has a local player and IsAnyCopNear (an active cop within 30 m);
* else 0.

| | mode 0 | mode 1 cop near | mode 2 roadblock |
|---|---|---|---|
| lead time t (0x894714) | 0.5 s | 1.3 s | 1.0 s |
| eye height above ground (0x894770) | 0.1 | 3.0 | 3.5 |
| look offset, car frame x/y/z (0x89474C/58/64) | 1.2, 0, −0.2 | −4, 0, −0.2 | 1.2, 0, 1.0 |
| framed half-size (0x894738) | 3.3 m | 3.3 m | 3.3 m |

**Init** (0x4745e0):
```
t: if speed·t > 150: t = 150/speed;   t2 = 1.5 (same clamp)
p01 = P + V·0.1;  pA = P + V·t;  pB = P + V·t2
WRoadNav at P along the car's forward, advanced speed·t; for each road edge e:  c = centre + (e − centre)·k,
   k = 0.9, or 0.9·10/|e − centre| when the half-width exceeds 10 m
side candidates: pB ± 4·carLeft
each candidate: height = max(height, ground probed from +4 m) + height[mode]   (0x46b590, only in game)
   valid if !blocked(c, P) || (mode != 2 && !blocked(c, p01)), and |c − lastEye| > 3
   score: road = |c − pA|; side = 0.1·|c − pB|
pick: if sideL + roadA ≤ sideR + roadB: (sideR < roadB ? roadB : sideR) else (sideL < roadA ? roadA : sideL)
none valid: eye = camera + 2 m up, minus 2 if that is blocked
eye.z += 0.5;  lastEye (DAT_009110B0) = eye
rack focus: focus offset = ±(26 + rand 19) m easing to 0 over 0.42 s (tCubic at +0xA4) → FocalDistance [sign: random ?]
```
**Update** (0x47de30):
```
view.SimTimeMultiplier = 0.2
look = P + carMatrix·lookOffset[mode]; eye fixed
FOV = clamp(2·bATan(dist, 3.3), bAngle 800, 0x332C)            // 4.39° .. 71.96°, dist ≥ 1
DOF: TargetDistance = dist, FocalDistance = dist + 2·rack, DepthOfField 7.5
if IsSomethingInBetween(camera, P) && director.+0x2B8 < 1: +0x2B8 = −1        // end early when hidden
```

## 6. TrackCopCameraMover (pursuit start)

Three cubic Bézier curves with 4 control points each:
* eye at +0x170, parameter +0x1B0 advancing at 1.275/s;
* look at +0x210, parameter +0x250 at 0.765/s;
* FOV at +0xD0, parameter +0x110 at 0.765/s.

**Init** (0x475350): every point starts at the current camera: eye = position, look = target, FOV = current.
FindPursuitVehiclePosition (0x475170) then picks the nearest cop within 300 m of the player that meets all of:
* active, of the cop class, with a pursuit whose target is this player;
* passes the vt+0x14/0x74 checks (in pursuit [?]);
* not hidden from the camera.

If such a cop is found:
```
d = eye − target;  u = 5·normalize_xy(target − cop);  h = min(max(d.z, target.z − cop.z), 2.25) + 0.25
Q = (target.xy + u, target.z + h)                    // 5 m past the player, away from the cop
cos = dot(d, (u, h)) / (|d||(u, h)|)
if cos < 0:                                          // Q is on the far side: swing round instead of cutting through
   k = (1 − cos)/2, negated when cross_z(d, u) < 0
   p1 = (eye.x − d.y·k, eye.y + d.x·k, eye.z);  p2 = (target.x + u.x + u.y·k, target.y + u.y − u.x·k, Q.z)
   if neither p1 nor p2 is blocked from the target: eye points = (eye, p1, p2, Q)
look[3] = cop
FOV[3] = clamp(2·bATan(|eye[3] − cop|, 3.0), bAngle 1000, 0x332C)     // 5.49° .. 71.96°
```
If no cop is found and the director's +0x2B4 < 2, it sets +0x2B4 = −1 and the shot is skipped.

**Update** (0x47e190): advance the three parameters (each capped at 1) and LookAt(eyeBezier, lookBezier).
The FOV comes from the FOV curve. The look adds no car-frame offset (it is zeroed). The rack focus is the same as
TrackCar.

## 7. ICE: NIS, generic and replay cameras

### 7.1 Data
* ICEData key layout: `ICEData.hpp`. The data has nType 12..18, bSmooth always 0, tangent lengths mostly 1.0
  (sometimes 0 or about 0.7) and dutch values of ±0.09.
* Lens is a focal length in mm (19.5..501, median 35.4). The FOV comes from `2·bATan(lens, 15.96)`, i.e.
  **FOV = 2·atan(15.96/lens)** (constant 0x417F5C2A). `Core/IceCameras` used to treat the lens as degrees.
* Dutch is a fraction of a turn (×65536 → bAngle), so ±0.09 ≈ ±32°.
* Letterbox bytes are 0 or 100; ×0.00125 gives Camera LB_height = 0.125.
* Sim speed bytes are 100 (normal), with 75..88, 33, 30 and 9 in some shots.
* Overlay indices are 0, 2, 3 and 4; see §7.5.
* Spaces: 0 car, 1 world, 2 hybrid, 3 scene. Car space uses the anchor car matrix (x forward, y left, z up). Hybrid
  is car position + world axes. Scene space uses the INIS scene transform (vt+0x30).

### 7.2 Track choice and parameter
* **NIS** (ChooseCameraPlaybackTrack 0x483d30): the track named `"Track %d"` (GetTrack by name) with
  `scene->GetCameraTrackNumber()`. NISActivity::Load fills that number from ChooseGoodSceneCameraTrackIndex
  (0x47ef70):
  * with fewer than 2 tracks: 0;
  * otherwise, for each track, take its first key's eye and look in world space and project eye − look onto the
    horizontal plane. Pick the track whose normalized direction has the largest dot product with the player car's
    forward, i.e. the shot that sees the car's front.
* **Generic** (ChooseGenericCamera): the track that SetGenericCameraToPlay(group, track) named. The start time is
  the world (or real) timer.
* **Replay**: ChooseReplayCamera (ICEReplay::ChooseGoodCamera / CameraCutIsGood, 0x47f490.. 0x4804a0) uses
  category hashes (NosScore, JumpScore, SpeedScore, CornerScore, BurnoutScore, PowerSlideScore) and remembers the
  last 3 tracks used. The PC build has no replay mode to drive them (ICEReplay.cpp has no PC bytes in coverage.json);
  not ported.
* **Containers** (PC InGameB.bun): 0x8003B200 NIS (context 0), 0x8003B201 FMV (1), 0x8003B202 (2: groups of 1-key
  3 s tracks, the replay/marker set), 0x8003B203 (3: the GENERIC group "Cinematics" with Challenge 3.5 s,
  DefaultSpeed 5 s, DefaultFinish 3 s, DragFinish, SprintFinish, TollFinish, PHoldFin_xx...). The GameCube
  SpeedChunks.hpp numbering (MKR = B202, REPLAY = B203, GENERIC = B204) does not match the PC data.
* **Parameter** (ICETrack::GetCameraData 0x47b030):
  * NIS: (scene elapsed − scene start) / (scene length − scene start), i.e. the scene's progress, not the track
    length;
  * generic or FMV: (timer − track.Start) / track.Length, at most 1.

  The key is GetKeyNumber(param), the last key whose parameter is ≤ param. The segment runs from key.param to the
  next key's param (or 1). u = (param − start)/(end − start), or 0 when the span is under 1e-4.
* When a generic camera's parameter reaches 1, the generic request is cleared (nPlayGenericGroupHash =
  bStringHash("")) and the director returns to Drive.

### 7.3 Interpolation (ICEMover::SetDesired 0x482160 at each key change, ICEMover::Update 0x482ec0)
* Eye and look are tCubic3D from vEye[0]→vEye[1] (vLook likewise). Slopes come from ICEManager::GetSlope (0x47f490)
  for side 0 (start) and side 1 (end):
  ```
  if key.nType == 0: slopes 0
  N = neighbour (previous for side 0, next for side 1)
  if !N || N.nType == 0 || !KeysShared(key, side, N, side^1):
      slope = (end − start)·fTangentLength[side]                     // eye, look, dutch, lens
  else:
      sK = interval(key), sN = interval(N), w = sK/(sN + sK)
      a = (sN > 1e-6) ? w·sK/sN : w;  b = 1 − w
      slope = fTangentLength[side]·(a·chord(N) + b·chord(key))
  ```
* Evaluation: GetEye/GetLook (0x46dd10/0x46ddd0) use the Hermite cubic (c3 = v0 + v1 − 2d, c2 = 3d − v1 − 2v0) when
  bCubicEye / bCubicLook is set, else a straight lerp. GetFOV and GetDutch (0x46dee0/0x46de90) use the cubic when
  bCubicEye is set.
  * The FOV cubic runs from fov(lens0) to fov(lens1). Its slopes are (fov(lens + slope·0.01) − fov(lens))·100
    (0x46d620).
  * Dutch ×65536 is the bAngle bank of the up vector about the view axis (ComputeBankedUpVector).
* Scalar channels are Hermite with zero end slopes, i.e. a smoothstep over u:
  * near clip (+0x5C);
  * noise amplitude (+0x64) and frequency (+0x6C);
  * focal distance (+0x74);
  * aperture (bytes +0x7C);
  * letterbox (+0x7E);
  * sim speed (+0x80).
* Smooth entry/exit (bSmooth bits) blends from the live camera. The car matrix captured at the cut (mover+0xD0)
  serves bIgnoreOrientation keys. bCarSpaceLag (replay context) adds a spring lag on the car position.
* **Depth of field** (ICEMover::Update tail): aperture < 0 or ≥ 37 → off (+0xB4 = +0xB8 = 0, unless the DOF override
  DAT_00911020). Otherwise N = 2^(i/6), i = (int)(aperture + 0.5) clamped 0..36 (table in 0x46D680, f/1..f/64);
  f = 15.96 / tan(FOV/2) mm (0x46D5E0); s = max(focal, 0.01)·1000 mm; c = 0.03 mm; H = f²/(N·c) + f;
  near = (H − f)s/(H + s − 2f)·0.001, far = (H − f)s/(H − s)·0.001 (100 000 when H − s ≤ 1e-5; near + 0.01 if below
  near); FocalDistance = (near + far)/2, DepthOfField = far − near, TargetDistance = |eye − look|.
* **Noise / shake**: without a shake track, NoiseAmplitude1 = (0, 0, amp, amp), NoiseFrequency1 = (0, 0, freq·1.618,
  freq·1.382), Amplitude/Frequency2 = 0, then the camera noise (0x467420) at time = ICE context time × dT [?]. With
  nShakeType ≠ 0, ICEManager::GetShakeTrack (0x478290) finds the ICEShakeTrack by bStringHash of its name (chunk
  0x3B211 in InGameB.bun: {u32 count}, tracks {bTNode, group, i16 NumKeys +0xC, Name[14] +0xF, {q[3], p[3]}[n] +0x20};
  retail **Jog** 0xBD1F ×159 keys, **Still** 0x03CA3227 ×162, **WalkSlow** 0x0460AC13 ×27, **WalkFast** 0x04595CDC ×9,
  119 keys each): key = (int)(time·freq·30) mod NumKeys (0x469330 wraps negatives), quaternion (q·amp, w =
  √(1 − |q·amp|)) and offset p·amp multiplied into the camera matrix in camera space (handheld-camera wobble).
* **Constraints**: bConstrainToWorld → terrainClamp (0x470FE0) and bConstrainToCars → obstructionRaise (0x47C6F0)
  whenever the ICE editor state is below 9 — i.e. always in game (168 / 172 keys use them), not only in replays.
* Letterbox: camera +0xC8 LB_height = letterbox·0.00125 when the eased value is ≥ 0.
* Effects of the scalar channels:
  * view SimTimeMultiplier = clamp(simSpeed, 0, 100)·0.01, at least 0.01; the camera's own dT is also scaled by it;
  * NearZ = near clip;
  * LB_height = letterbox·0.00125;
  * noise: NoiseAmplitude1 = (0, 0, amp, amp), NoiseFrequency1 = (0, 0, freq·1.618, freq·1.382), or the key's
    ICEShakeTrack (nShakeType hash) scaled by amp;
  * DOF from focal distance and aperture (off when aperture ≥ 37).
* bConstrainToWorld → terrainClamp (0x470fe0) and bConstrainToCars → obstructionRaise (0x47c6f0). These apply only
  in the replay context, or when the ICE editor state is < 9.

### 7.4 CDActionIce (0x484270)
CDActionIce holds an ICEAnchor that it fills from the player car: velocity, matrix, NOS, rpm, gear and the per-wheel
data (0x46d3f0). It runs ChooseCameraPlaybackTrack at construction. An event 0x15 sets +0x2E8, and GetNext then
returns the action that was active before the ICE camera.

### 7.5 Overlays
When the key changes and its nOverlay differs (or there was no previous key), ICE calls HideOverlay and then
ShowOverlay(n). This pushes the FE package without controls (cFEng::PushNoControlPackage, flag 0x67):

| n | package |
|---|---|
| 0 | none |
| 1 | FLASHERS.fng |
| 2 | BLACK_FADE_OUT.fng |
| 3 | BUSTED_OVERLAY.fng |
| 4 | BLACK_BACKGROUND.fng |

NISActivity::Release hides it. The arrest NIS keys carry overlay 3, which is where the busted video-cam frame comes
from.

### 7.6 Generic ICE camera (ECinematicMoment 0x640550, Camera.SetGenericCamera 0x604A50)
SetGenericCameraToPlay(group, track) sets nPlayGenericGroupHash; SelectAction then picks CDActionIce until the track's
parameter (t − start)/Length reaches 1, when the request is cleared (bStringHash("")) and MICECameraFinished is posted.
Users: PhotoFinishScreen (DefaultFinish / DefaultSpeed / the race's FinishCamera), the challenge and moment scripts
(Camera.SetGenericCamera "Cinematics"/"Challenge"). The spaces are as §7.1 with the player car as the anchor.

### 7.7 Moment NIS (CAnimChooser::Moment = 4)
The ENIS-type handler (0x640F00) starts a Moment NIS only when: the Jump Cams option (profile +0x47) is on, not split
screen (0x469120), no gGameBreakerCamera, no race or a pursuit race (GetIsPursuitRace 0x5FBE70), within 2 s of the
ETriggerMomentNIS time (0x90D9D4), and an active cop chasing the player is within (cos + 1)·30 + 20 m. NISActivity::Load
then needs Tweak_EnableNISMoments (0x901B28) and the closest marker of the scene's moment type (CAnimCandidateData
mMomentAnim → MarkerNameList 0x8930B0, e.g. WMGasStation → GasStationMoment) with distance + 25 m per turn of heading
difference (GetClosestMarker 0x4491E0) under 75 m. It creates a **CAnimMomentScene** (0x6EF2C0; decomp AnimScene.hpp:256):
an ICEScene that only controls the camera (no cars, no characters) at the marker, track
ChooseGoodSceneCameraTrackIndex, state PLAYING immediately.

### 7.8 Drive-camera avoidance (list DAT_00911050)
The list holds every active vehicle (PVehicle activation 0x6864FB) and the smackables whose attribute 0xE9D83D0C
(CAMERA_AVOIDABLE) is set (0x66AF24); vehicles leave it on deactivation (0x68664B), models on release.
* **obstructionRaise** (0x47C6F0 with the test 0x47C250): up to 16 times, raise the eye out of the dome over each
  object: object-space q = x⁴/(hx + 0.85)⁴ + z⁴/(hz + 0.85)⁴, dome top (1 − q)^¼·(hy + 0.85). Hysteresis: keep the
  previous raise when the horizontal speed is < 1 m/s and the eye direction is within dot 0.9 of last frame's. Apply
  half-stepped: applied = (raise + acc)/2, acc += raise − applied (mover +0x68..+0x78). Then the look-at is rebuilt.
  [? which box extent is the height: Ghidra's stack aliasing]
* **avoidLean** (0x47C440) — the car-passing lean: for each object 0.8..7 m from the anchor and ahead of its velocity V
  (cos > 0.5): k = clamp(4.84/d²), sp = clamp((|V − v| − 15)/85) (v = the object's velocity), face = max(0, −v̂·V̂);
  sum += dir·(sp + face)·k; the sum is added to the camera's up vector (passing a car at 15+ m/s closing speed, or
  meeting one head-on, banks the camera).
* The "vertical dip graph" 0x91107C is BSS with no writer: the impact lurch only.

## 8. Rear-view mirror (RearViewMirrorCameraMover::Update 0x476690)
* Shown only when the profile option is on (+0x39).
* The view is the car matrix, rotated 0x4000 about two axes so it faces backwards, at the car position. 0.5 is added
  to the camera-space translation (0x8EE2B0 = (0, 0.5, 0)), i.e. the eye sits 0.5 m above the origin [?].
* FOV bAngle 20000 (109.9°), NearZ 0.75, FarZ 192 (0x8EDED8). Camera shake is applied.
* It is the third render view and is drawn by the HUD in the bumper and hood POVs (CubicCameraMover::RenderCarPOV) [?].

## 9. Front end: SelectCarCameraMover and CDActionShowcase

**SelectCarCameraData**: OrbitV (polar angle; 90 = level, above for > 90 in the data), OrbitH (yaw), Radius, Roll,
FOV (horizontal) and LookAt (car space). The mover holds Current (+0x90), Start (+0xC0) and Goal (+0xF0), plus
speeds +0x120.., damping +0x12C (10), periods +0x130 (2), time +0x134 and total +0x138 (1). Control mode +0x80 is
0 (animate), 1 (user orbit) or 2 (static).
* **SetDesiredOrientation** (0x4770b0): Start = Current, Goal = the arguments. Yaw, OrbitV and Roll go through
  FindBestAngleGoal (goal, goal ± 360, whichever is nearest). Time = 0 and total = animSpeed.
* **Update** (0x4769e0):
  ```
  u = total > 0 && time < total ? time/total : 1
  k = 1 − e^(−u²·damping)·cos((periods + ½)·π·u²)               // damped spring with overshoot
  mode 0: every channel = k·Goal + (1−k)·Start; angles wrapped to 0..360; u ≥ 1 → mode 2
  mode 1 (user): H += 60·dT·Hspeed; V = clamp(V + 60·dT·Vspeed, 94.5, 110); R = clamp(R + 60·dT·Rspeed/8, 4.65, 6.65)
       (no clamps with DAT_00926134); roll/FOV/look-at keep animating
  ```
* **SetVRotateSpeed** (0x476e70): entering user mode from static re-aims at roll 0, FOV 45 and look-at (0, 0, 0.75)
  over 0.555 s.
* **CreateCameraMatrix** (0x46b7e0): identity, RotZ(H), RotX(V) (eRotateX; round 9), translate (0, 0, R), invert. The eye is that
  translation: an orbit of radius R about the **car origin**, not about the look-at point. The camera then looks at
  LookAt with the up vector banked by Roll. TargetDistance = R.
* **Garage driving** (GarageMainScreen::UpdateCurrentCameraView 0x7a94c0, FE_ATTRIB `frontend` class): on entry it
  sets the current orientation to `angle_entry_<garage>`. It then calls SetDesiredOrientation with
  `angle_final_<garage>`, the orbit (cam_orbit_vertical, cam_orbit_horizontal, cam_orbit_radius), cam_roll_angle,
  cam_fov, cam_lookat_xyz, cam_anim_speed, cam_damping and cam_periods. Each screen's collection names its angle
  through cam_angle (FindScreenCameraInfo 0x7a8fe0).
* **CDActionShowcase / ShowcaseCameraMover** (CM_SHOWCASE): a drive-like action whose anchor follows the attached
  car. The GC names (SetFromTweakables, BuildPhotoCameraMatrix) point at the in-world photo / showcase view used by
  the Showcase screen (CreateShowcase 0x570d20) [?]. It was not reversed further, and the PC ShowcaseCameraMover is
  unnamed.

## 10. Port (NFSMWUnity)

| Exe | Unity |
|---|---|
| CameraAI::Director, event entry points | `Runtime/Camera/CameraDirector.cs` (`CameraDirector`, `CameraUtil`) |
| CDActionDrive + CubicCameraMover | `Runtime/ChaseCamera.cs` (hosts the director, polls events, applies SimTimeMultiplier to `Time.timeScale` on top of `PlayerCar.WorldTimeScale`) |
| TrackCar / TrackCop movers | `Runtime/Camera/TrackCameras.cs` |
| profile options, collision listener, rear-view mirror | `Runtime/Camera/CameraOptions.cs` (`CameraOptions`, `CameraCollisionRelay`, `RearViewMirror`) |
| ICEData / ICEMover interpolation / overlays table | `Core/IceCameras.cs` (`Evaluate`, `TrackByNumber`, `OverlayPackages`) |
| CDActionIce, track choice, overlays, sim speed, letterbox | `Runtime/Game/NisPlayer.cs` (`UseIce`, `ChooseGoodTrack`, `SetIceOverlay`) |
| SelectCarCameraMover + garage angles | `Runtime/UI/CarShowroom.cs` (`SetAngle`, `SpringK`, `LateUpdate`) |
| generic ICE camera, freezes, avoidance, CameraMessagePort snap | `Runtime/ChaseCamera.cs` (`Generic`, `PlayGenericCamera`, `CameraDetachedFromRacer`, `AvoidLean`, `CarPlaced`) |
| DOF keys, ICE jitter/DOF/constraints, obstruction raise, avoidables | `Runtime/Camera/IceShot.cs` (`CameraDof`, `IceShot`, `ObstructionRaiser`, `CameraAvoidables`) |
| shake tracks, DOF maths | `Core/IceCameras.cs` (`Shakes`, `FNumber`, `DepthOfField`) |
| jump zones | `CameraDirector.InJumpZone` (Core/TrackPathZones, type 12) |
| Moment NIS | `NisPlayer.PlayMoment` |
| Camera::ApplyNoise, speed / chopper noise, bCarSpaceLag, explosion shake (round 9) | `Runtime/Camera/CameraNoise.cs` (`CameraNoise`, `CameraExplosions`, `IceCarSpaceLag`) |
| SelectCarCameraMover (full, round 9) | `Runtime/Camera/SelectCarCamera.cs` |

Event polling, since the gameplay code is not edited:
* pursuit start = `Systems.Police.InPursuit` rising for the player;
* totaled = `PlayerCar.Totaled` rising inside a race;
* airborne = `WheelsOnGround` dropping to 0;
* photo finish = `RaceManager.Active.PlayerEntrant.Finished` rising [? the photo-finish screen may only appear on
  close finishes];
* roadblock = cop cars with `CopCar.Roadblock` set.

* Photo finish: polled as the player finishing first (the screen may call `ChaseCamera.StartCinematicSlowdown` and
  `PlayGenericCamera` itself).
* Generic camera requests come from `LuaDrive.CinematicMoment` (subscribed) and `ChaseCamera.PlayGenericCamera`.
* Car placement (`PlayerCar.SetVehicleOnGround`) calls `ChaseCamera.CarPlaced`: the following mover snaps.

Deviations:
* DOF and rack focus are computed into `CameraDof` (TargetDistance / FocalDistance / DepthOfField); no DOF render
  pass reads them yet (renderer owner).
* Replay cameras: no replay mode on PC; not ported.
* Avoidables: active cars only; CAMERA_AVOIDABLE smackables are not exposed by the smackable code.
* Generic ICE time is real time [? scripted moments in the exe use the world timer]; scene-space points of a generic
  track are read as world; generic shots draw no letterbox bars or overlays [ours].
* The explosion shake is CameraExplosions (§13.5); the crash shake is the Rumble.cs CameraShaker (§12).
* The exe's ICE noise time base (ICE context +0x10 × dT) is read as the shot's elapsed time [?].

## 11. Unknowns
1. ~~SimTimeMultiplier reader~~: QuickGame::OnManageTime 0x6F4D10 (§4.3).
2. ~~Bézier basis~~: the TrackCop ctor (0x47AB00) stores rows (1,−3,3,−1), (0,3,−6,3), (0,0,3,−3), (0,0,0,1) and GetPoint
   multiplies (s³, s², s, 1), s = 1 − t: the Bernstein weights (s³, 3s²t, 3st², t³).
3. The axis mapping of the crash-shake rotation (ApplyCameraShake 0x64C420: eRotateX(−1200·a), eRotateY(2000·b),
   eRotateZ(0·c) on the camera-space shake vector; which components a/b are is hidden by Ghidra's stack offsets), the
   sign of the dutch bank, and the exe camera-space axes used for the ICE shake tracks (taken as x right, y up,
   z forward).
4. KeysShared (0x477ee0) is assumed to compare positions and spaces.
5. LB_height units (fraction of the screen per bar) and the rear-view eye offset frame.
6. Which input latches PULLBACK. The Jump Cams / rear-view mirror defaults come from FEDb (Options → Gameplay).
7. ~~The game-mode bits in JumpCamsAllowed~~: §13.6.
8. 0x73def0 is IVisualTreatment::TriggerPulse(length) (the white camera pulse; 0 = the treatment's own length): the
   smooth-return call 0.25 s in pulses for 0.2 s; the timer is ported (§13.5), the pulse is the visual-treatment owner's hook.
9. ~~CopViewCameraMover usage; the Showcase mover~~: both dead on PC (§13.7).
10. obstructionRaise's exact box axes; avoidLean's param_2 read as the anchor velocity.

## 12. This round: ICEMover / ICEManager remainder and the camera shaker
* **ICEMover::SetDesired** (0x482160): a cut = the new key does not share the point with the old one (KeysShared), a
  hard entry = the first key without bSmooth bit 0; both flush the motion blur (0x725550 FlushAccumulationBuffer) and
  capture the anchor car frame (+0xD0); the overlay swaps when nOverlay changes; **one** `ICECut%d` tag per key change
  (the skipped keys' tags only come from ICECompleteEventTags on a skip). The NIS and generic cameras now do this (the
  NIS fired every skipped tag and captured on every key; the generic flushed on key 0 only).
* **Car frame** (0x466A60 FrameFromForward): the anchor's forward and the **world up** — no roll. bIgnoreOrientation keeps
  the frame captured at the last hard cut with the **live** position (the port froze the position too and used the full
  car rotation).
* **ICETrack::GetCameraData**: NIS parameter = (scene elapsed − scene start)/(scene length − scene start) from the anim
  scene (NisPlayer.Time01, unclamped; the key is valid for 0..1 only); generic/FMV = (WorldTimer − Start)/Length capped at 1
  — the **sim** clock unless ICEManager::SetUseRealTime (`ChaseCamera.IceUseRealTime` [hook: PhotoFinishScreen]), so the
  ICE sim-speed keys slow the track and a pause freezes it (was Time.unscaledTime).
* NearZ = near when ≥ 0 (was > 0.01); obstructionRaise gets the anchor's velocity (was zero: the hysteresis always
  held); the noise time is track.Length × param; an unknown nShakeType applies nothing (was Perlin); the shake
  quaternion's w = sqrt(1 − |v|) as the exe writes it (0x4836F8; was sqrt(1 − |v|²)); ChooseCameraPlaybackTrack's
  `GetTrack("Track %d")` has no fallback (no track → no ICE camera); ChooseGoodSceneCameraTrackIndex only when Load found
  a marker, with key 1 when key 0 is bSmooth and a zero direction scoring 0.
* **Camera shaker** (Misc/Rumble.cpp tShaker, ported by the input agent in Runtime/Input/Rumble.cs `CameraShaker`):
  CDActionDrive::Update's MaybeCameraShake and Camera::UpdateAll's UpdateCameraShakers run in RumbleDriver;
  CameraMover::SetCameraMatrix's **ApplyCameraShake** (0x64C420) is applied by ChaseCamera at the end of every frame's
  pose (drive, TrackCar, TrackCop and generic ICE shots): the shake vector as a camera-space translation plus the X/Y
  rotations (−1200·(z + y), 2000·x bAngle), only at full sim speed, view 1/3 → player 0, view 2 → player 1. ChaseCamera's
  own CrashShake (§4.4) is gone; without a RumbleDriver the camera keeps the shakers itself [ours].
* Still open: the smooth entry/exit live blend (bSmooth set in only 4 retail keys), the FMV context. bCarSpaceLag and
  event 0x15: §13.

## 13. Round 9: camera noise, car-space lag, explosions, dead code

Harness `camkeys` (ICE key flag counts) and `camnoise` (simsurface CAMERA_NOISE), NFSMWStaging/Harness/ProbeCamera.cs.

### 13.1 Camera::ApplyNoise (0x467420) and its noise (Camera.cpp)
* NoiseBase 0x467250 = Hugo Elias' hash `1 − ((n(n²·15731 + 789221) + 1376312589) & 0x7FFFFFFF)/2³⁰`, n = (x << 13) ^ x;
  NoiseInterpolated 0x467290 (floor, lerp); Noise 0x467350 = 6 octaves, ×2 frequency, ×0.5 amplitude from 1. The exe
  matches the decomp exactly.
* ApplyNoise(matrix, t, intensity): v = (Noise(F1·t)·A1 + Noise(F2·t)·A2)·intensity over the 4 channels of the camera's
  NoiseFrequency1/Amplitude1 (+0x70/+0x80) and NoiseFrequency2/Amplitude2 (+0x90/+0xA0); m = T(v.x, v.y, 0)·RotX(z')·RotY(w')
  multiplied after the world-to-camera matrix (camera space), z' / w' = FovRelativeAngle(deg) 0x4673C0 =
  asin(sin a·sin(fov/2)/sin 30°) (baseline 0x2AAA at 0x8EDF1C, fov = Camera +0xC4).
* Port: `Runtime/Camera/CameraNoise.cs` (`CameraNoise.Noise/Apply`); the ICE noise (IceShot.ApplyJitter) now uses it (was
  Unity Perlin fBm) [? camera-space y sign: the noise is symmetric].

### 13.2 CubicCameraMover speed noise (0x47BEC0): was approximated, now exact
Called from CubicCameraMover::Update (0x47DDAA) after LookAt → ApplyCameraShake → HandheldNoise → ChopperNoise:
* speed graph 0x8EDFB0 (tGraph 0x46F020: 5 rows, 0..80 m/s, step 0.05), rows {A1, A2, F1, F2}: (0, 0, .6, 1), (.005, .5,
  1.1, 1.1), (.03, .8, 1.2, 1.2), (.07, 1, 1.3, 1.3), (.02, 1, 1, 1.4); A1 × POV scale 0x8EDE1C (all 1), ×1.5 in a drag race
  (anchor +0xBE); above 5 m/s with forward accel a > 0 (anchor +0x40 · forward): r = min(a, 20)·0.05, A1 += 0.15r,
  F1 += (0.5 − F1)r; over-rev (+0xBF, rpm > redline − 500): A1 += 0.15, F1 ×0.7; above 5 m/s: NOS (+0xBC) → (0.3, 2), brake or
  handbrake (+0xBD, InputControls fBrake +0x18 / fHandBrake +0x1C > 0) → (0.3, 5).
* layer 2: A2 = g.A2·CAMERA_NOISE[0] × 0x8EDE38 (1), F2 = g.F2·CAMERA_NOISE[1]; CAMERA_NOISE (simsurface 0xF0C9E498) of the
  anchor's surface +0x98 = the car's ground surface while every wheel touches (0x472E57), else SimSurface::kNull; absent
  = 0 (DefaultDataArea): roads 0, particulate / dirt / grass / sand / gravel / mud / snow (0.5, 1), cobble (0.3, 2),
  grass_shoulder (0.1, 0.5), golf_fairway (0.25, 1), golf_green 0.
* inside POVs (vt+0x24 OutsidePOV false): A1, A2 ×0.25. Channels: F1·(1.8, 2, 2.125, 2), A1·(0.03, 0.025, 0.68, 0.28),
  F2·(3, 5, 7, 5.5), A2·(0.007, 0.01, 0.3, 0.4) (0x8EDF70..0x8EDFAC); ApplyNoise at the WorldTimer seconds, intensity 1.
  The old port had only channels x / y of layer 1 with Perlin noise; the rotations (0.68° / 0.28° per unit A1) were missing.
* Port: ChaseCamera.Shake → CameraNoise.DriveLayers + Apply on the placed camera [? the front-left wheel's surface stands in
  for the body's WWorldPos surface].

### 13.3 ChopperNoise (CameraMover 0x470A60) and HandheldNoise (0x468870)
* Called outside tunnels (0x73CFE0, the view's tunnel / overpass test) with scale = (1 − 0.8·min(1, speed·1092.27))·0x8EDE00
  (all 1): 1 when stopped, 0.2 when moving. Each active CHOPPER within 40 m (horizontal) of the camera: NoiseFrequency1 =
  (3.141, 2.971, 0.84234, 0.92345), NoiseAmplitude1 = (0.01, 0.05, 1.1, 2.7)·scale·(1 − d/40), ApplyNoise at the world time.
  Port: CameraNoise.Chopper (CopHeli objects, PoliceZones.CameraInTunnel).
* HandheldNoise's scale is dword_911060[pov]: BSS with no writer (only the read at 0x47DCD8) → 0 → never applies. Dead.

### 13.4 ICEMover bCarSpaceLag (0x4834B5..0x4835A4)
* ICEAnchor::Update (0x46D3F0, from CDActionIce::Update 0x48450E): pos +0, matrix +0x10, velocity +0x50, speed +0x70; when
  dT > 0 and move/dT < 300 (flt_894AE4): +0x60 = carMatrix · ((|v| − |v_prev|)/dT, 0, 0, w 0), the forward acceleration
  as a world vector (else it keeps its value).
* With the current key's bCarSpaceLag (ICEData +9; key pointer mover +0xC4): tCubic3D mover +0xB0 (ctor 0x46A300: duration
  1.0, flags 1) SetValDesired(+0x60) (0x46A480: state 2 when it differs) and Update(dT × key sim speed, 0, 0) (0x46C380 →
  tCubic1D::Update 0x46BDC0: state 2 re-aims a Hermite from (value, slope) to (desired, 0) every frame and samples it at
  dT / 1 s). Lag = clamp(value × (0.05, 0.05, 0.02) [0x8EE310..18], (−1, −1, −0.2) [0x8EE2F0], (1, 1, 1) [0x8EE300]) (game
  axes, z up), subtracted from both eye and look (world space, before dutch / noise / constraints).
* Retail keys with the flag (`camkeys`): 5 NIS, 6 FMV, 71 replay (no PC replay), 2 in the generic "Challenge" track.
* Port: `IceCarSpaceLag` (CameraNoise.cs) in ChaseCamera.Generic; the NIS ICE camera (Runtime/Game/NisPlayer.cs, not
  camera-owned) needs the same two calls [hook listed in the round-9 report].

### 13.5 Other ports this round
* IExplosion shake (CDActionDrive::Update 0x47308E): for each live explosion (list _Listable<IExplosion, 96>; vt+4
  GetOrigin, +8 GetExpansionSpeed, +0xC GetMaximumRadius) with maxRadius + 2 > |origin − camera| > 0: MaybeCameraShake(view −
  1, −normalize(origin − camera)·speed/dT). Port: `CameraExplosions` (lifetime (max − start radius)/speed [?]) fed by
  Smackable's ESpawnExplosion [hook].
* TriggerPulse: the +0x68 timer (0.3 s on a smooth-return Drive) fires IVisualTreatment::TriggerPulse(0.2) at ≤ 0.05 s
  (0x472B1D..0x472B69). Port: ChaseCamera.TriggerPulse delegate [hook for the visual-treatment owner].
* FindPursuitVehiclePosition (0x475170) filters: IsActive, class CAR, pursuit live (+0x2C) and targeting this player,
  IPursuitAI::GetInPursuit (vt+0x14), GetTimeSinceTargetSeen (vt+0x74) ≤ 0, no height offset on the sim position, nearest the
  player under 300 m, seen from the camera. Port: TrackCopCamera (CopCar.InPursuit / Pursuit.Target / TimeSinceSeen).
* Rack focus (0x475015): integer 26 + bRandom(19), negated on bRandom(2) ≠ 0; tCubic duration 0.42 (0x3ED70A3D).
* SelectCarCameraMover complete (`Runtime/Camera/SelectCarCamera.cs`): Update modes 0/1/2 with the user clamps (OrbitV
  94.5..110, Radius 4.65..6.65, zoom ×0.125, 60·dT), Set{V,H}RotateSpeed / SetZoomSpeed (ignored while animating; from static
  re-aim roll 0 / FOV 45 / look-at (0, 0, 0.75) over 0.555 s), CreateCameraMatrix (eye (−R sinV sinH, −R sinV cosH, −R cosV)
  in the car's game frame). CarShowroom (FE owner) still drives its own mode-0 copy [hook: route GarageMainScreen through it].

### 13.6 JumpCamsAllowed (0x469E30) resolved
false when the FEDatabase game mode (+0x12C, eFEGameModes) has QUICK_RACE (4) with FEDatabase byte 0 == 2 (two players:
split screen), LAN (0x40), or ONLINE (8) unless 0x734FE0; else the profile's Jump Cams (+0x47). The port's
`JumpCams && !Split` is equivalent (no LAN / online).

### 13.7 Dead on PC (proofs)
* **CDActionIce event 0x15** (0x4842A4): the ActionQueue action 0x15 sets +0x2E8 (GetNext 0x473E80 → return to the previous
  action) only while byte 0x911039 is set. 0x911039 is BSS (.data raw data ends at 0x90E000) and its only references are
  the reads at 0x4842A9 / 0x46A89C and the clear at 0x4842B2 (no other relocation into 0x911038..0x91103B except the debug
  CameraDebugWatchCar read 0x481B42) → never set; it is the debug camera's toggle (0x46A870 is CDActionDebug's handler,
  vtable 0x895204).
* **Replay ICE path** (0x91103A): set only by sub_42A810, which has no caller (no relocation, no rel32 call); its
  CDActionIce uses (0x48451C ChooseReplayCamera, 0x485F79) never run.
* **CDActionShowcase / ShowcaseCameraMover** (ctor 0x4854A0, mover 0x47AF20): registered in the action factory (0x884DC0,
  hash of "CDActionShowcase") but nothing requests it: SetAction(StringKey) 0x4797F0 is called only with "CDActionDrive"
  (CameraAI::Reset 0x47CC2C / 0x47CCB8), by TotaledStart and by SetAction(char*) 0x479EB0, whose two callers pass
  "CDActionTrackCar" (0x47A129) and "CDActionIce" (0x6405B0); SelectAction only picks Drive / TrackCar / TrackCop / Ice.
  The FE garage uses SelectCarCameraMover directly (GarageMainScreen ctor 0x7BF0FC).
* **CopViewCameraMover** (ctor 0x47AD50, SetDesired 0x475A50, Update 0x4762D0): the ctor has no caller (no relocation,
  no rel32 call); its vtable 0x894DA8 is referenced only by the ctor / dtor. Dead.
* **CDActionDebug / CDActionDebugWatchCar / DebugWorldCameraMover**: debug-only (gated by the CameraDebug bytes, never set).
