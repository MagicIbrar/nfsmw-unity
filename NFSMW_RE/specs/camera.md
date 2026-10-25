# Gameplay cameras (NFS MW 2005 PC v1.3)

> See `camera_core.md` for the director, drive action, TrackCar/TrackCop, ICE and front-end movers. Its §1 resolves
> several (?) items below: the anchor flag identities, the collision timers, the z1 cinematic slowdown and z2
> speedbreaker zoom, the DOF fields, and the rule that chase↔chase POV changes blend instead of snapping.

Sources: speed.exe static analysis (Ghidra/REA plus capstone over the file; addresses are VAs, image base 0x400000,
sha256 80774c2e…), `GLOBAL\attributes.bin` classes `camerainfo`, `ecar`, `simsurface`. Every constant below was read
from the exe or the attribute data. Statements that are inferred rather than read are marked (?).
Convention: game space is Z-up. Car/camera-local axes are **X forward, Y left, Z up**. Unity mapping: (x,y,z)local ->
Unity (-y, z, x). Time is seconds unless noted. The game's tick clock `DAT_00925ae8` runs at 4000 ticks/s, so code
writes `ticks*0.00025` to get seconds.

## 1. Classes and functions

| Thing | Address | Notes |
|---|---|---|
| CubicCameraMover ctor | 0x481bd0 | vtable 0x894cc8; args (view, anchor, mode, blendFromPrev, noTilt, lookBack, noDOF) |
| CubicCameraMover::Update(dT) | 0x47d3c0 | vtable+0x08, the per-frame algorithm (§4) |
| ComputeTargets | 0x47cf00 | builds eye/look/up/FOV targets from camerainfo (§4.3) |
| Heading target / reference frame | 0x46b1f0 / 0x46b410 | smoothed travel direction (§4.2) |
| IsChaseMode (vtable+0x24) | 0x46afa0 | mode in {2,3,4,5,6} |
| SetLookBack / SetNoTilt (vt+0x14/+0x1c) | 0x4741a0 / 0x4741b0 | mover+0xa1 / mover+0xa0 |
| CameraAnchor ctor / SetCameraInfo(mode) | 0x4716a0 / 0x4713c0 | reads camerainfo into anchor+0xc8.. |
| Player camera director update | 0x472740 | fills the anchor from the vehicle every frame |
| Anchor fill from vehicle | 0x468fa0 | velocity, speed, position, matrix, accel |
| tCubic1D update (Hermite) | 0x6513c0 | 3D version 0x651980; set target 0x649cb0 |
| Speed table lerp (44 floats) | 0x46f2b0 | tables at 0x8ee040 + mode*20 |
| Shake: speed/surface | 0x47bec0 | §6 |
| Shake: helicopter wash (?) | 0x470a60 | §6 |
| Terrain/collision clamp | 0x470fe0 | §7 |
| Obstruction raise | 0x47c6f0 | §7 |
| Camera::SetCameraMatrix | 0x4700d0 | stores the matrix, previous matrix and velocities |
| Mode is selectable | 0x599a50 | reads SELECTABLE[idx] |
| CAMERAACTION_CHANGE cycle | 0x5a3850 (in game), 0x51c870 (FE options) | §3 |
| Input action switch | 0x6a8a04 | CHANGE=0x14, LOOKBACK=0x17, PULLBACK=0x18 |
| Projection matrix | 0x6cf52b | §2 FOV |

## 2. camerainfo attributes (what each field means)

Reader: `CameraAnchor::SetCameraInfo` 0x4713c0. **Array index** `idx = (DAT_00982b3c == 3) ? 1 : 0`.
DAT_00982b3c is set by 0x6cfa10 from the active render views: 3 means the PLAYER1 and PLAYER2 views are both live
(split screen), 1 means one player, 2 means player plus rear-view mirror. **So [0] is the normal full-screen value and
[1] is the split-screen value.** The pair is not a low/high speed pair. On PC single player, always use [0].

| Field | Stored at | Meaning |
|---|---|---|
| LAG | anchor+0xcc = LAG[idx] / zoom | Eye X offset in metres along the reference frame (negative = behind). Reference point = vehicle rigid-body position (world-matrix translation). For bumper/hood it is the eye position relative to that point. |
| HEIGHT | anchor+0xd0 | Eye Z offset in metres above the same reference point (frame Z axis). |
| LATOFFSET | anchor+0xd4 | Eye Y offset in metres (all data = 0). |
| ANGLE | anchor+0xca = ANGLE*65536/360 (u16 binary angle) | **Downward pitch in degrees** (negative looks up). |
| FOV | anchor+0xd8 = FOV*zoom*65536/360 | **Horizontal** full FOV in degrees (see projection below). |
| STIFFNESS | anchor+0xdc | Copied but **never read** by any camera code in v1.3 (no reader of anchor+0xdc). Ignore it. |
| TILTING | anchor+0xe0 (u8) | 1 = add the acceleration tilt to the up vector (§4.3). All collections = 1. |
| SELECTABLE | read at +0x76 by 0x599a50 | The player can cycle to this mode. bumper, hood, close, far and every per-car override = 1. default, superfar, drift and pursuit = 0. (The harness `attribclass` dump shows garbage here because these are u8.) |

`zoom` = anchor+0xc4 (1.0 normally; §5). LAG is divided and FOV multiplied by it, so a lower zoom pulls the camera
back and narrows the view (a dolly-zoom).

**Projection** (0x6cf52b): `xscale = 1/tan(FOV/2)`, `yscale = 1/tan(FOV*(H/W)*k/2)` with k = 1.15 for player views
(views 1,2,4,5) and 1.0 otherwise. The FOV is horizontal, and the vertical angle is a linear scale of it, not a tan
correction. To match in Unity, write a custom `projectionMatrix` with m00 = xscale and m11 = yscale. Otherwise set
`fieldOfView = FOV*(H/W)*1.15` and accept a ~2–4% horizontal mismatch.

### Mode → collection (ecar RefSpec fields, unnamed hashes)
The car's `ecar` collection = the vehicle's MODEL name (0x599a50: pvehicle MODEL 0x9047C9E0 → hash → `ecar`). Parents
are racers/cops/traffic/van/truck/trailers. The 7 RefSpec fields are:

| mode | ecar field | default target | chase? | selectable |
|---|---|---|---|---|
| 0 | 585517F3 | bumper (van/semi/pickupa/news_ variants) | no | yes |
| 1 | D74C1435 | `<car>_hood` for every racer, else hood | no | yes |
| 2 | 0C2DA793 | close (semi_/van_/news_/pickupa_close) | yes | yes |
| 3 | CCF03CB3 | far (semi_/van_/news_/pickupa_far) | yes | yes |
| 4 | 10204A90 | superfar | yes | no |
| 5 | 4B675DC8 | drift | yes | no |
| 6 | D76A6FAD | pursuit | yes | no |

If the mode is outside 0..6, SetCameraInfo uses collection `default` and forces mode 3.

## 3. Default camera, cycling, look-back, pull-back

* **Default mode = 2 (close)**: 0x57edab writes `map(DAT_008f86c4 = 2)` into both players' camera setting
  (profile+0x8c / +0xa0). The anchor ctor's internal default is 3, but the director overwrites it each frame from
  the setting (0x472740 → mover vt+0x20 SetMode).
* **CAMERAACTION_CHANGE** (action 0x14): mode = (mode+1) mod 7, repeated until SELECTABLE[idx] is true. The FE
  option list also supports -1 (0x51c870). In-game order: **bumper → hood → close → far → bumper**.
* **LOOKBACK** (0x17, held): the controller's IsLookBack (player+0x2dc > 0) makes the director call SetLookBack(1),
  unless a vehicle query (vt+0x20) blocks it. With look-back on, the eye and look offsets get **X and Y negated**
  (`sign=-1`, §4.4), so the chase eye sits LAG metres *in front* looking back at the car, and the hood cam looks back
  from the roof. vt+0x30 then returns yaw 0x8000 (180°).
* **PULLBACK** (0x18, a toggle on player+0x2e0): while it is set, the director saves the current mode and sets
  **mode 6 (pursuit)**: LAG −9, HEIGHT 1.5, FOV 65. It restores the saved mode when the toggle clears (0x472cxx,
  DAT_008ee4b4).

## 4. Per-frame algorithm (CubicCameraMover::Update, 0x47d3c0)

### 4.1 Smoothing primitive: tCubic (0x6513c0)
Each scalar channel has {p, v, target, t, duration, coeffs}. Velocity is per *normalised* time.
Every channel is created with "retarget every frame" (flag 1), so each update re-plans a Hermite curve from the
current (p, v) to (target, v=0):
```
update(dT):                               // runs BEFORE this frame's targets are set (one frame of latency)
  if state == RETARGET:                   // the target was changed since the last update
     t = 0; d = target - p
     c3 = v + 0 - 2d ; c2 = 3d - 0 - 2v ; c1 = v ; c0 = p
  t = (duration <= 1e-5) ? 1 : t + dT/duration
  if t > 1: t = 1; p = target; v = 0; state = IDLE
  p = ((c3*t + c2)*t + c1)*t + c0 ;  v = (3*c3*t + 2*c2)*t + c1
setTarget(x): target = x; if x != p: state = RETARGET
snap():       p = target; v = 0; state = IDLE
```
Channels: eye (3 axes), look (3), up (3), heading (3), fov (1, in 65536-per-turn units).

### 4.2 Reference frame (0x46b1f0 / 0x46b410)
Anchor data comes from the vehicle each frame (0x468fa0):
* V = linear velocity, `speed` = |V|
* P = body position
* M = body rotation (rows fwd F, left, up U)
* `accW` = F * (speed − speed_prev)/dT (longitudinal acceleration only, world space; 0 if the frame jump is > 300 m/s)

```
chase modes (2..6):
  v = V; if dot(v,F) < 0: v = v - 2*dot(v,F)*F          // reversing still faces forward
  w = graph(speed; 8 m/s→0, 13 m/s→1)                  // 0x9111f0 ← pts at 0x8ee02c
  h = normalize(v)*w + F*(1-w)                          // follow travel direction above 13 m/s (drift!)
  g = clamp(1 - 0.5*secondsSinceGrounded, 0, 1)         // grounded = anchor.c0 (wheels on ground (?))
  h.z *= (1 - F.z^2) * g                                // pitch kept only while grounded; flattens over 2 s airborne
  heading.setTarget(h)
  frame X = normalize(heading.p); Y = cross(Zworld, X); Z = cross(X, Y); origin = P   // no roll
bumper/hood (0,1): heading.snap(F); frame = M at P (flipped if M.up.z < 0)
```

### 4.3 Targets (0x47cf00), all in reference-frame-local space unless noted
```
sf  = clamp(speed / 71.0)                                // 71 m/s = 255.6 km/h, absolute speed (0x895078)
prm = lerp(TABLE[mode].e0, TABLE[mode].e1, sf)            // 44 floats, §8
shaped = (switchBlendTimer b4 <= 0) ? 30*graphA(accW per axis) : 0
           graphA: x∈[-30,30] pts (-30,-15,0,15,30)→(-1,-0.1,0,0.1,1)   (0x8ede54)
avg = mean of the last 5 `shaped` samples (ring buffer 0x478b70, one sample per frame)
aL  = accelEnabled (mover+0xa0 = !ctor.noTilt) ? worldToFrame(avg) : 0

(L,Y,H) = (LAG, LATOFFSET, HEIGHT)
if topSpeed > 0: s = speedScale(speed/topSpeed); L *= s.x; H *= s.z    // topSpeed = anchor+0x14 from transmission vt+0x1c
      speedScale (5 pts at 0,.25,.5,.75,1): x = 1,1,1.02,1.125,1.2 ; z = 1   (0x8ee0cc)
a = ANGLE
if chase: lookX = 0       ; lookZ = H + L*tan(a)          // pitch down by ANGLE
else:     lookX = L + 1   ; lookZ = H - tan(a)
eye  = clamp(aL*prm[16..18], prm[20..22], prm[24..26]) + (L, Y, H)
look = clamp(aL*prm[28..30], prm[32..34], prm[36..38]) + (lookX, 0, lookZ)
up   = (TILTING ? clamp(avg*prm[4..6] + (0,0,1), prm[8..10], prm[12..14]) : (0,0,1))   // WORLD accel, z pinned to 1
if speed > 5: up += avoidLean(P)                        // 0x47c440, see §7
eyeC.setTarget(eye); lookC.setTarget(look); upC.setTarget(normalize(up))
fovT = FOV + (chase && anchor.nos && speed > 0 ? 9.0° (0x666) : 0); fovC.setTarget(fovT)
if snapFlag (mover+0xa2) || !chase: snap eye, look, up, fov   // bumper/hood are rigid, with no smoothing
```
Debug/photo-mode globals (0x904ff9/0x904ffa/0x9b0890/0x9b0894) zero prm[16..18] or use fixed values. Ignore them.

### 4.4 Update order
```
Update(dT):
  if paused && !forceUpdate: return
  if b4 > 0: b4 -= dT                                   // 1 s after a blended camera switch: no accel response
  anchor.SetCameraInfo(mode)                            // re-reads attributes every frame
  prm = table lerp (§4.3); shaped/avg push
  -- impact pulse arm (mover d0/d8):
  if max(|avg.x|,|avg.y|) >= 24 && anchor.impact > 0 && d0 < anchor.impact: d0 = anchor.impact; d8 = 1
  -- vertical arm (d4/dc): if avg.z >= 24: d4 = max(d4, clamp((avg.z-24)/6)); dc = 1   (its graph 0x91107c is all
     zero in BSS, so it has no visible effect)
  durations: eye = prm[0]; look = prm[1]; fov = prm[2]; up = prm[3]
             heading_i = prm[40+i] + anchor.impact + anchor.slip
  update(dT) on: up, fov, eye, look, heading
  ComputeTargets (§4.2 heading + §4.3)
  sign = lookBack ? -1 : 1
  E = (sign*eyeC.x, sign*eyeC.y, eyeC.z) ; T = (sign*lookC.x, sign*lookC.y, lookC.z)
  Up = chase ? (upC.x, upC.y, 1) : car up U            // world space
  if d0>0 && d8>0: p = graphB(d8)*d0*0.25; E.x *= (1-p); T.x *= (1+p)
        graphB over [0,1] pts .25: 0, .02, .25, .99, 0  (0x8ede8c)   (a forward lurch peaking ~0.25 s after a crash)
  d8 -= dT (if > 0); if d8 <= 0: d0 = 0 ; same for dc/d4
  if chase:                                             // gear-shift lurch
     ts = (now - shiftT)
     if ts >= 1.5: if speed > 10 && anchor.gearChanged: shiftT = now
     else: g = graphC(ts) ; E.x += (1 - clamp(speed*0.01))*|E.x|*0.2*g
        graphC over [0,1.5], 9 pts: 0,.2,.13,-.08,-.2,-.13,-.07,-.03,0  (0x8ede68)
  if chase:                                             // "car passing" dip, set by 0x4741f0 (?)
     f = clamp(1.2 - (now-passT)*1.25, 0, 1.2); if f > 1: f = 2 - f
     E.z -= 0.9*f ; dipActive = f > 0
  Ew = frame*E ; Tw = frame*T
  view.fov = fovC.p ;  view = LookAt(Ew, Tw, Up)
  view.focusDist = |viewPos - P| ; view DOF near/far = noDOF ? (0,0) : (15, 65) (?)
  shakes (§6); if chase: terrainClamp (§7); if chase && !dipActive: obstructionRaise (§7)
  Camera::SetCameraMatrix(view, dT)
```

### 4.5 Camera switch (ctor 0x481bd0)
All cubics start at the targets for the new mode. If `blendFromPrev`, and the previous eye is within 50 m, and the
view directions are not reversed (dot ≥ −0.9), the cubics instead keep the previous camera's eye/look as their start
values and set b4 = 1 s. The Hermite curves then fly the camera to the new position over each channel's duration.
Otherwise everything snaps.

## 5. Anchor inputs the camera reads (director 0x472740)
| anchor field | source | effect |
|---|---|---|
| +0x10 speed, +0x00 V, +0x20 P, +0x50.. M, +0x40 accW | 0x468fa0 | §4 |
| +0x14 topSpeed | transmission interface vt+0x1c | LAG speed scale |
| +0xac impact | dir.0x58 / 2.0 (a timer that decays 1/s; normalised by 2 s) | heading lag + impact lurch. Its writer is not found; likely collision (?) |
| +0xb0 slip | grounded ? ramp(|slipAngle(rev)|*360, 0, 90) * ramp(v, 10, 20) : 0 | heading lag of up to +1 s while drifting |
| +0xbc nos | engine interface vt+0x20 (?) | FOV +9°, shake amp 0.3/freq 2 |
| +0xbd | controller state +0x18 or +0x1c > 0 (?) | shake amp 0.3/freq 5 |
| +0xbe | vehicle vt+0x30 == 1 (?) | shake amp ×1.5 |
| +0xbf | rpm > redline − 500 | shake amp +0.15, freq ×0.7 |
| +0xc0 grounded | count from vt+0x4c > 0 (wheels on ground (?)) | §4.2 pitch fade |
| +0xc2 gearChanged | transmission gear differs from last frame | §4.4 lurch |
| +0xc4 zoom | 1 − 0.4*max(z1, z2) | LAG/zoom, FOV*zoom |

z1 = dir.0x6c ramps toward 1 at 4/D per second while dir.0x70 is set, and decays at 1/D per second. D defaults to 2 s
(0x8ed454). It is started by the director's vt+0x18 (duration), which also sets `view+0xcc = 0.25+0.75*(1−z1)`.
z2 = dir.0x50 ramps up at 4/s while player vt+0x40 is true and decays at 1/s. Neither caller is identified
(speedbreaker or arrest (?)).

## 6. Shake
**Speed/surface shake** (0x47bec0), applied every frame:
```
T = lerp4(speed; table 0x8edfb0 at 0,20,40,60,80 m/s):
     (0,0,.6,1) (.005,.5,1.1,1.1) (.03,.8,1.2,1.2) (.07,1,1.3,1.3) (.02,1,1,1.4)
A1 = T0 ; F1 = T2 ; if be: A1 *= 1.5
if speed > 5: a = clamp(dot(F, accW), 0, 20); A1 += a*0.0075; F1 += (0.5 - F1)*a*0.05
if bf: A1 += 0.15; F1 *= 0.7
if nos && speed>5: A1 = 0.3; F1 = 2 ;  if bd && speed>5: A1 = 0.3; F1 = 5
A2 = T1 * CAMERA_NOISE[0](surface) ; F2 = T3 * CAMERA_NOISE[1](surface)   // simsurface; asphalt has none → 0
if !chase: A1 *= 0.25; A2 *= 0.25
freqA = F1*(1.8, 2, 2.125, 2)   ampA = A1*(0.03, 0.025, 0.68, 0.28)
freqB = F2*(3, 5, 7, 5.5)       ampB = A2*(0.007, 0.01, 0.3, 0.4)
s_i = fbm(freqA_i*t)*ampA_i + fbm(freqB_i*t)*ampB_i      // t = game time s; fbm = 6 octaves, gain .5, lacunarity 2
camera-local offset (s0, s1) metres; then rotate s2° and s3° (pitch/yaw jitter (?)), each scaled ≈ sin(FOV/2)/sin(30°)
```
CAMERA_NOISE data: dirt/gravel/grass etc. (0.5, 1), cobble (0.3, 2), golf_fairway (0.25, 1),
grass_shoulder (0.1, 0.5).
**Helicopter wash (?)** (0x470a60): for each object of type DAT_0092c4f4 within 40 m (XY), it applies
`k = (1 − d/40)*(1 − 0.8*clamp(speed/30))` with freq (3.141, 2.971, .842, .923) and amp k*(.01, .05, 1.1, 2.7).
It is skipped while view flag 0x73cfe0 is set.
The generic per-mode noise (0x468870) has magnitude 0 (BSS table 0x911060), so it is off.

## 7. Terrain, collision, avoidance (chase modes only)
* **terrainClamp** (0x470fe0):
  * Sweep a sphere from the midpoint of car and eye to the eye against world collision (WCollider), and push the
    eye out along the hit normal.
  * Then cast down for the ground: `eye.z = max(eye.z, ground + 0.25)`.
  * If view flag 0x73cfe0 is set, also `eye.z ≤ ground + 4.5`. Otherwise the probe start is raised to ≥ P.z + 0.5.
  * The look-at is recomputed so the target stays fixed.
* **obstructionRaise** (0x47c6f0):
  * Up to 16 iterations of raising eye.z while the eye is occluded by objects in list 0x911050. These are likely the
    smackables with `CAMERA_AVOIDABLE` (?).
  * Hysteresis: keep the previous raise if the frame direction changed by less than ~25° (dot > 0.9) and horizontal
    speed < 1 m/s.
  * Apply `(raise + prevAcc)/2` (half-step smoothing).
* **avoidLean** (0x47c440): for each object in the same list that is 0.8–7 m from P and ahead (dot > 0.5), add
  `dirToObj * (clamp((|v·n|−15)/85) + |dot(n,F)|) * clamp(4.84/d²)` to the up vector. The camera leans away from
  close roadside objects.

## 8. Speed tables (TABLE[mode], 0x8ee040, e0 at 0 m/s, e1 at ≥71 m/s; prm index: e0 → e1)

| idx | meaning | bumper(0) | hood(1) | close(2) | far(3) | superfar/drift/pursuit(4-6) |
|---|---|---|---|---|---|---|
| 0 | eye duration s | 0→0 | .7→.5 | 1.25→.75 | 1.25→.75 | 1.5→.5 |
| 1 | look duration | 0→0 | .7→.5 | .4→.3 | .4→.3 | .5→.2 |
| 2 | FOV duration | 1→.5 | 1→.5 | 1→.5 | 1→.5 | 1→.5 |
| 3 | up duration | 0→0 | .7→.5 | .8→.4 | .8→.4 | 1→.5 |
| 4,5,6 | up tilt k | 0 | 0 | .004,.004,0→.002,.002,0 | .005,.005,0→.003,.003,0 | .006→.002 |
| 8-10 / 12-14 | up clamp | (−1,−1,1)/(1,1,1) all modes |||||
| 16-18 | eye accel k | 0 | (−.01,−.02,.1)→0 | (−1,0,0)→(−.75,0,0) | (−1)→(−.5) | (−.25)→(−.05) |
| 20-22 | eye min | 0 | (−.1)×3 | (−1.25,−1,−1)→(−1,−1,−1) | (−1,−1,−1) | (−2.5,−1,−1)→(−.5,−1,−1) |
| 24-26 | eye max | 0 | (.3,.1,.1) | (1.5,1,1)→(1,1,1) | (2,1,1) | (2,1,1)→(.5,1,1) |
| 28-30 | look accel k | 0 | (−.1,−.02,0)→0 | (−.5,0,0)→(−.2) | (−.1)→(−.05) | (−.1)→(−.02) |
| 32-34 | look min | 0 | (−.05,−.1,0) | (−2,0,−1)→(−.5,0,−1) | same as close | same |
| 36-38 | look max | 0 | (.2,.1,0) | (2,0,1)→(.5,0,1) | same | same |
| 40-42 | heading duration | 0 | .4→.2 | .4→.2 | .4→.2 | .5→.1 |

(Indices 7, 11, 15, 19, 23, 27, 31, 35, 39 and 43 are vector padding.)

## 9. Not covered by this mover
Race intro, finish and arrest shots are NIS/animated cameras (NisCameras, GenericCameras, ECameraPhotoFinish).
Jump cams (JUMP_CAM, GOJumpCams), the rear-view mirror (RearViewMirrorCameraMover) and the FE movers (SelectCar,
Showcase, TrackCar, TrackCop, CubicCameraMover reused with other params, ICE) are separate. The race-start countdown
uses the normal player camera after the intro NIS.

## 10. Uncertainties
1. Interface identities behind anchor flags bc/bd/be/c0 and z1/z2 (NOS, controller buttons, wheels-on-ground,
   speedbreaker/arrest) are inferred from vtable slots and value ranges. They were not confirmed against symbols.
2. anchor.impact (+0xac) timer writer not found; assumed to be set to 2 s on collision.
3. 0x91107c (vertical dip graph) and 0x911060 (per-mode noise) are BSS with no static writer found, so they are taken
   as 0. A runtime tweak could set them.
4. fbm base noise (0x467290) not decoded; the shake rotation axes (s2/s3 = pitch/yaw) and the exact FOV scaling of
   shake (0x4673c0 uses sin(fov/2)/sin(30°) inside an asin (?)) need confirmation.
5. FUN_0045db30 = sin and FUN_0045dbf0 = cos is assumed from the polynomial (db30 is a sin series). If swapped, ANGLE
   would use cot. The data (ANGLE ≈ 0–3°) only makes sense as tan.
6. Default mode 2 comes from the profile-default writer 0x57edab. Saved profiles override it.
7. The view flag 0x73cfe0 (affects ground clamp and heli shake) is unknown. It might be "inside a tunnel or
   underground" (?).
8. view+0xb4/+0xb8 = 15/65 (cleared with noDOF) are assumed to be DOF/focus distances.
