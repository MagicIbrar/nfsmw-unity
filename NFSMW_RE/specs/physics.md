# NFS MW (PC v1.3) — player car physics spec

Source: static RE of `speed.exe` (REA/Ghidra + local capstone; decompiles in `NFSMW_RE/decompiled/physics/`), values
checked against `GLOBAL/attributes.bin`. Marks: **[C]** = read directly from code, **[U]** = uncertain or inferred.
Units: SI inside the sim (m, s, kg, N, rad). Attributes use inches, lb-ft, lb/in, mph and percentages, and the code converts them:
`IN=0.0254`, `LBFT=1.3558` (lb-ft→Nm), `LBIN=175.1268` (lb/in→N/m), `RPM2RAD=0.10471976`, `MPH=0.44703`, `TOMPH=2.23699`.
`ratio01(x,a,b)=clamp01((x-a)/(b-a))` (0 if b-a<=1e-6). `lerp(a,b,t)=a+(b-a)t`. `Table(x)` = equally spaced samples (count, min, invStep) and
linear interpolation, clamped at both ends (0x6533B0). `Graph(x)` = (x,y) point list with linear interpolation, clamped (0x46F7C0).

## 1. Behaviours and tick order

A racer (`pvehicle` of class `racers`) runs these behaviours in BEHAVIOR_ORDER: RBVehicle (rigid body), EngineRacer
(factory 0x6B6D30, sim 0x6AF740), SuspensionRacer (factory 0x6B6A10, sim 0x6B1DD0), DamageRacer, ... Each sim tick:
1. **Input → State** (0x69D4D0): gas, brake, handbrake ∈[0,1] and steer ∈[-1,1] (clamped); local and world velocity; speed=|v|;
   body slip angle `β=atan2(v_x_local, v_z_local)` (0 if v_z<1 m/s); yaw rate; mass; wheels on ground `g=n/4`; driver class;
   physics mode (§11); flags `staging` (vehicle vfunc+0x20) and `destroyed` (vfunc+0x7C, which forces brake=1, handbrake=1, gas=0).
2. **EngineRacer**: NOS, speed limiter, induction, clutch/engine integration, output `T_drive` (driveshaft torque) and auto shift.
3. **SuspensionRacer**: CG update, speedbreaker blend, tire Begin, aero, differential (puts `T_drive` on the wheels),
   steering, grip modifiers, drift state, suspension and tire forces, air stabiliser, low-speed sleep.
Sim step [U]: global `1/120 s` at 0x8F8300 copied to the sim-rate global 0x925990. The Unity port should use a fixed 120 Hz step.

## 2. Attribute data model and upgrades

`pvehicle/<car>` (parent `racers`→`cars`→`default`) holds `MASS` (kg), `TENSOR_SCALE` (cars 1,2,1: inertia scale [U]),
`HandlingRating[2]` (front-end only) and, per part type, a RefSpec array plus an `*_upgrades` int (max level).
Upgrade type table at 0x8F87A0 = {type, name, class, *_upgrades, *_current, *_package, ref field}:
`0 tires, 1 brakes, 2 chassis, 3 transmission, 4 engine, 5 induction, 6 nos`.

* `X_current` (not in data) is the installed level L (0..`X_upgrades`). SetLevel (0x678AD0 → 0x678730) builds a temporary
  collection (`part_upgrade`) that **interpolates every Float / AxlePair / Int32 field** between two refs with
  `t = L / X_upgrades` (0x677CE0, per-field lerp, arrays element-wise) [C]:
  - refs count 2: `[0]`=stock (car-named), `[1]`=fully upgraded (hash-named, e.g. `A8A12BB2` for `tt`).
    `value(L) = lerp(ref0, ref1, L/N)`. L=0 → ref0.
  - refs count 3 (induction, nos): `[0]`=none (`default`: no turbo / no NOS), `[1]`=first stage, `[2]`=top.
    L=0 → ref0; L≥1 → `lerp(ref1, ref2, L/N)` (so L=1 is not exactly ref1 when N>1).
  - Example `tt` engine (N=4): TORQUE stock `141,189,246,253,225,203,190,190,190` → top `189,345,544,558,497,449,425,414,402`.
  - Cars with `X_upgrades=0` (e.g. `bmwm3gtre46`) only have the stock ref.
* **Junkman** (0x678150): if bit `1<<type` is set in pvehicle int `0xCDC136E8`, the current part collection is copied as
  `junk_upgrade` and each `JunkmanMod{class, field, factor}` of `junkman/default.<type>_package` **multiplies** the field
  (all array elements). Data (all cars share `junkman/default`):
  - engine: TORQUE ×1.10, ENGINE_BRAKING ×1.10 · transmission: GEAR_EFFICIENCY ×1.10 · induction: HIGH_BOOST, LOW_BOOST ×1.15
  - nos: NOS_CAPACITY ×1.10, TORQUE_BOOST ×1.02 · brakes: BRAKES ×1.20, EBRAKE ×1.10
  - tires: STATIC_GRIP ×1.10, DYNAMIC_GRIP ×1.10, GRIP_SCALE ×1.02, YAW_CONTROL ×1.10, STEERING ×1.05
  - chassis: DRAG_COEFFICIENT ×0.90, ROLL_CENTER ×1.05, SPRING_STIFFNESS ×1.05, SPRING_PROGRESSION ×1.05, AERO_COEFFICIENT ×1.05, FRONT_WEIGHT_BIAS ×1.01
  - Junkman is re-applied after a level change. Order: lerp first, then multiply.
* **Tunings** (Physics::Tunings, 7 floats from vehicle vfunc+0xCC, index → effect; range presumably −1..+1 [U]):
  `0 STEERING` max-angle ×(1+0.2t) · `1 HANDLING` lateral force ×(1+0.2t), longitudinal ×(1−0.2t) ·
  `2 BRAKES` bias: front brake input ×(1+0.5t), rear ×(1−0.5t) · `3 RIDEHEIGHT` +t **inches** added to RIDE_HEIGHT (and the CG) ·
  `4 AERO` drag and downforce ×(1+0.25t) · `5 NOS` capacity ×(1−0.25t), boost ×(1+0.25t) ·
  `6 INDUCTION` spool point and boost shape (§3.3).

### Layout field reference (array header = 8 bytes: u16 capacity, **u16 count**, elemsize; AxlePair = [front, rear])
| class | field (layout offset) | meaning / units |
|---|---|---|
| engine | TORQUE[9] (+8) | lb-ft at rpm points equally spaced over **IDLE..MAX_RPM** |
| | SPEED_LIMITER[2] (+0x34) | [0]=limit mph, [1]=fade width mph (0 = off; all cars 0) |
| | ENGINE_BRAKING[3] (+0x44) | fraction of torque at IDLE..MAX_RPM, used as closed-throttle drag |
| | FLYWHEEL_MASS (+0x50), MAX_RPM, RED_LINE, IDLE | inertia; table span; rev limit; idle rpm |
| transmission | GEAR_RATIO[≤9] (+8) | [0]=reverse, [1]=neutral (0), [2..]=1st..top; **count** = R+N+gears |
| | DIFFERENTIAL[3] (+0x34) | lock factor [0]=front axle, [1]=rear axle, [2]=centre (0 = open / fixed split) |
| | GEAR_EFFICIENCY[9] (+0x48) | per gear multiplier on output torque (all 1.0 in stock data) |
| | TORQUE_CONVERTER, TORQUE_SPLIT (front share 0..1), CLUTCH_SLIP, OPTIMAL_SHIFT (unused [U]), SHIFT_SPEED (s), FINAL_GEAR | |
| tires | YAW_CONTROL[4] (+8) | rear-stability gain vs speed/topSpeed (§10) |
| | GRIP_SCALE, DYNAMIC_GRIP, STATIC_GRIP (AxlePair) | lateral-curve scale; sliding μ; peak μ |
| | ASPECT_RATIO (%), RIM_SIZE (in), SECTION_WIDTH (mm) (AxlePair) | radius = (RIM·IN + 2·W·0.001·AR·0.01)/2 |
| | STEERING, YAW_SPEED | steering-angle and rate scale; scale on tire yaw moment |
| chassis | SHOCK_DIGRESSION, SPRING_PROGRESSION, TRAVEL(in), RIDE_HEIGHT(in), TRACK_WIDTH(m), SHOCK_EXT_STIFFNESS, SHOCK_STIFFNESS, SPRING_STIFFNESS(lb/in), SHOCK_VALVING(in/s), SWAYBAR_STIFFNESS(lb/in) (AxlePairs) | §7 |
| | ROLL_CENTER(in), WHEEL_BASE(m), SHOCK_BLOWOUT(g), AERO_CG(% toward front), FRONT_AXLE(m, local z), AERO_COEFFICIENT, FRONT_WEIGHT_BIAS(%), DRAG_COEFFICIENT, RENDER_MOTION (visual) | |
| brakes | BRAKE_LOCK, BRAKES (lb-ft) (AxlePairs), EBRAKE (lb-ft) | §9 |
| induction | LOW_BOOST, SPOOL_TIME_DOWN, VACUUM, SPOOL, SPOOL_TIME_UP, PSI, HIGH_BOOST | §3.3 |
| nos | NOS_DISENGAGE, TORQUE_BOOST, FLOW_RATE (unused by the sim [U]), RECHARGE_MIN, NOS_CAPACITY, RECHARGE_MAX, RECHARGE_MAX_SPEED, RECHARGE_MIN_SPEED | §3.4 |
| simsurface | DRIVE_GRIP, LATERAL_GRIP, ROLLING_RESISTANCE | per-wheel surface (road = 1; grass 0.7/0.6/6; sand 0.25/1/30) |

## 3. Engine (EngineRacer)

State: gear `g` (0=R, 1=N, 2..), engine speed `ωe` (rad/s), driveline speed `ωc`, throttle `θ`, clutch {state, timer, total},
shift timer, NOS amount `n`∈[0,1], spool `s`∈[0,1], boost `b`.

### 3.1 Torque curve [C] (0x672FE0, 0x691D40)
```
TorqueLbFt(rpm): r = clamp(rpm, IDLE, RED_LINE); x = (r-IDLE)/(MAX_RPM-IDLE)*(count-1)
                 i = floor(x); return lerp(TORQUE[i], TORQUE[min(i+1,count-1)], x-i)
EngineTorqueNm(rpm) = TorqueLbFt(rpm) * (1 + b) * 1.3558 * nosMult      // nosMult not applied for driver class 6 (remote)
```
Max engine speed = **RED_LINE** for racers (vfunc+0x34 returns true): `ωmax = RED_LINE·RPM2RAD`, `ωidle = IDLE·RPM2RAD`.
Engine inertia (0x66BB00): `Ie = (FLYWHEEL_MASS*0.025 + 0.25) * (g==N ? 0.35 : 1)`.
Peak torque (Nm) and peak-torque rpm `rpmPk` are precomputed at reset (0x691ED0, 0x66C2D0). HP = T·rpm·0.7376/5252.

### 3.2 Engine braking [C] (0x6A0910)
`EB = lerp over ENGINE_BRAKING[] with x=(clamp(rpm,IDLE,RED_LINE)-IDLE)/(MAX_RPM-IDLE)·(count-1)`, clamped 0..1;
`Tbrake = -EB · EngineTorqueNm(rpm)`. Net engine torque `Tnet = θ·Te + (1-θ)·Tbrake`. In gear and clutch locked, Tbrake is
forced to oppose the wheels: if `dir·Tbrake·ωwheel > 0` then Tbrake = −Tbrake.

### 3.3 Induction (turbo / supercharger) [C] (0x66BB30, 0x66BC60, 0x66BCC0, 0x692C50)
```
type = (HIGH_BOOST<=0 && LOW_BOOST<=0) ? none : (SPOOL>0 ? turbo : supercharger)
spoolPt = SPOOL + 0.25*t6*(t6>=0 ? 1-SPOOL : SPOOL)                      // t6 = INDUCTION tuning
rpmSpool = IDLE + (RED_LINE-IDLE)*spoolPt
target  = ratio01(θ, 0, 0.5);  0 if clutch disengaging (iface+0x10);  turbo: 0 if rpm < rpmSpool
s moves toward target: up at dt/SPOOL_TIME_UP, down at dt/SPOOL_TIME_DOWN (instant if time<=1e-6), clamp 0..1
lo = LOW_BOOST*(1-0.25*t6); hi = HIGH_BOOST*(1+0.25*t6)
boost(rpm) = rpm < rpmSpool ? (VACUUM<0 ? ratio01(rpm,IDLE,rpmSpool)*VACUUM : 0)
                            : lerp(lo, hi, ratio01(rpm, rpmSpool, RED_LINE))
b = clamp01(s) * boost(rpm)                                               // multiplies torque by (1+b)
psi (HUD/audio) = ratio01(b,0,max(lo,hi))·PSI·s, slewed at 20/s
```

### 3.4 Nitrous [C] (0x692930, 0x66BC40, 0x66BC10)
```
cap = NOS_CAPACITY*(1-0.25*t5)  [seconds of full burn];  mult = 1 + TORQUE_BOOST*(1+0.25*t5)
if cap<=0: n=0, return 1
mph = speed*2.23699
fire = nosButton && g>=2(1st+) && θ>0 && !shifting && mph>=10   (>=5 in a special mode [U])
if fire && n>0: n -= dt/cap * (k6) ; disengageTimer=1 ; return mult      // k6 = 0 for infinite-NOS cheat or class 6
if disengageTimer>0 && NOS_DISENGAGE>0: disengageTimer -= dt/NOS_DISENGAGE ; return 1   // no recharge right after use
recharge allowed if !player.InGameBreaker() (or no player) and mph >= RECHARGE_MIN_SPEED and g>=2:
    T = lerp(RECHARGE_MIN, RECHARGE_MAX, ratio01(mph, RECHARGE_MIN_SPEED, RECHARGE_MAX_SPEED))  // seconds for 0→1
    n = min(1, n + dt/T)
```
Stock racer NOS (`1EFBD807`): capacity 2.5 s, boost 0.75 (×1.75 torque), recharge 50 s at 50 mph → 30 s at 100 mph, disengage 2 s.
Top (`40826F5E`): capacity 5 s, boost 1.0. There is an extra optional modifier (iface +0x118, value x) that scales burn by (1−0.5x),
recharge by (1+x) and drive torque by (1+0.5x) [U: probably AI catch-up; treat x=0 for the player].

### 3.5 Speed limiter [C] (0x692840)
In forward gears, if `SPEED_LIMITER[0]>0` and `SPEED_LIMITER[1]>0` and speed > lim: `θ *= 1 - clamp01((v-lim)/(SPEED_LIMITER[1]·MPH))`.

### 3.6 Clutch, torque converter and engine integration [C] (0x6AF740; summarised)
```
ratio = GEAR_RATIO[g]*FINAL_GEAR*(g==R ? -1 : 1)
if TORQUE_CONVERTER>0: tc = (1 - ratio01(rpm, IDLE, rpmPk)) * TORQUE_CONVERTER * θ  (*clutchFactor while engaging)
                       ratio *= (1 + tc)                         // torque multiplication below the peak-torque rpm
ωw  = average angular speed of the driven wheels (max of a pair when spinning; front pair if SPLIT>0, rear if SPLIT<1)
ωc' = ωidle + ratio*ωw*(1 - ωidle/ωmax);  dωc = (ωc'-ωc)/dt;  ωc = ωc'      // idle-offset map: ωw=ωmax/ratio ⇒ ωc=ωmax
clutch: state 0 locked, 1 engaging (timer counts down; clutchFactor = 1-0.75·timer/total), 2 open (factor 0.25)
  gear R or 1st: if ωe or ωc > ωidle+(R ? 2400 : 800)·RPM2RAD or θ>=0.1 → engage over 0.05 s, else (idle at a stop) open
  gear N: open;  other gears: engage over 0.25 s.  A gear change opens the clutch if it was locked.
Tc (torque through clutch), R (reaction on the engine), g = wheels on ground/4 (min 0.25):
  locked:   Tc = Tnet; R = -Tnet·g
            invI = 1/Ie + 0.05·ratio²            (0.05 = reflected wheel inertia term, split over driven axles)
            x = (R+Tnet)/Ie - dωc;  Tc += min(x/invI, 0);  R -= (x/invI)·g
            if wheels on ground: if θ>0.2 && dir·avgWheelSlip>0.1 && g<=1st && ωc<ωe:  ωc=ωe, set driven wheels ω=ωe/ratio (launch wheelspin)
                                 else R += clamp(20·(ωc-ωe), -|Tnet|, |Tnet|)
  engaging: slip = clamp(ωe-ωc, ±300); k=20 (halved when slip reverses with |Δrpm|>2000); Tc = k·slip·clutchFactor; R = -Tc·g
  open:     Tc = 0
  R/1st only, CLUTCH_SLIP: if θ>0, locked and R·Tnet<0: R *= (1 - CLUTCH_SLIP·θ·(1 - ratio01(rpm,IDLE,rpmPk)·θ))²
ωe = clamp(ωe + dt·(Tnet + R)/Ie, ωidle, ωmax)
driven wheel ω is clamped to ωmax/ratio (rev limiter on the wheels); if ωc>ωmax: Tc = 0 when it would accelerate further
T_drive = GEAR_EFFICIENCY[g] · Tc · ratio                                   // goes to the differential (§5)
```
Throttle (and NOS) is 0 while the engine flag at +0x1A4 is set [U: blown engine, sabotage or shifting]. The sabotage timer
(+0x1A8) modulates ωe by `(1+0.5·sin(75.4·t))` [C].
Staging (pre-race countdown) and a positive launch value (vehicle vfunc+0x28) force θ=1, `Te = peakTorque·nosMult` and `Tbrake=0` [U on meaning].

### 3.7 Automatic gearbox [C] (0x673400, 0x691F90, 0x6AF490, 0x6920D0)
Shift points at reset, for each gear k=2..top−1, with `ρ = GEAR_RATIO[k+1]/GEAR_RATIO[k]`:
`up[k]` = first rpm r from `(RED_LINE+IDLE)/2` in 50-rpm steps (while r < RED_LINE) with `T(r)(1+b) < T(rρ)(1+b)·ρ`, else `RED_LINE−100`;
**b is always 0**: 0x673400 calls InductionBoost(engine, induction, **1.0, r**, …) with rpm and spool swapped (0x66BCC0 takes rpm, spool), so the boost is taken at 1 rpm (below the spool point, ramp 0) [C];
`down[k+1] = up[k]·ρ`; `up[top] = RED_LINE`.
Each tick (not staging, clutch locked), rpm = ωc·9.5493:
```
upshift   if rpm >= up[g] && g < top
downshift if g>2 && rpm < min( IDLE+(down[g]-IDLE)·0.65 → lerp(that, down[g], θ),  up[g-1]/GEAR_RATIO[g-1]·GEAR_RATIO[g] - 200 )
```
Execution (0x6AF490) waits for shiftTimer<=0: a downshift may skip several gears; an upshift needs every driven wheel grounded with
slip < 4 (no upshift while spinning); N goes to 1st. Shift time = `SHIFT_SPEED·GEAR_RATIO[newGear]` (×0.25 for a downshift).
Reverse is chosen by the input layer (brake held at a stop) [U, not traced].

## 4. Chassis body, mass and CG
* Mass = `pvehicle.MASS` (kg). Gravity = `rigidbodyspecs.GRAVITY` (cars −9.8128). Inertia from the collision box ×TENSOR_SCALE (1,2,1) [U].
* CG (0x68D800), body-local, z forward: `zf = FRONT_AXLE`, `zr = FRONT_AXLE − WHEEL_BASE`;
  `cg.z = zr + (zf−zr)·FRONT_WEIGHT_BIAS·0.01` (0.5 when airborne); `cg.y = (ROLL_CENTER − max(RIDE_HEIGHT f,r) − t3)·IN − boxOffsetY` [U on the y offset]; `cg.x=0`.

## 5. Differential [C] (0x68F0D0, 0x68EFA0)
```
Diff(a, b, groundedA, groundedB, bias, lock) -> (outA, outB):
  if lock>0 && groundedA && groundedB:
     s = |b·bias + (1-bias)·a|;  outA = (1-lock)·bias + lock·|b·bias|/s;  outB = (1-lock)(1-bias) + lock·|(1-bias)·a|/s   (clamp 0..1)
  else outA = bias, outB = 1-bias
axle split: DIFFERENTIAL[2]>0 ? Diff(ωfront sum, ωrear sum, …, TORQUE_SPLIT, DIFFERENTIAL[2]) : (SPLIT, 1-SPLIT)
per axle i with Ti = T_drive·split[i] ≠ 0: (oL,oR) = Diff(ωL, ωR, grounded, 0.5, DIFFERENTIAL[i])
wheel.driveTorque += o·mult·Ti      // only on grounded, unlocked wheels
```
More torque goes to the slower wheel (limited slip). Power-oversteer helper (normal mode, not drifting): if the axle's mean wheel
lateral speed minus body lateral speed has the sign of the steering and the wheels spin `ws = mean wheel slip − v_z` with
`f = ratio01(ws,1,20)·ratio01(|atan2(lat,|ws|)·360·steer|,1,16)` > 0, the inner wheel gets torque ×(1−f) and the outer ×(1−0.5f),
and grip ×(1+f) [C; geometry U]. Drift-race mode instead uses the drift-assist sides (`0x10C/0x114`).

## 6. Aerodynamics [C] (0x68D8E0, 0x66BAE0)
```
Fdrag = -v_world · |v| · DRAG_COEFFICIENT · dragScale · (1 + (2-1)·(1-θ)) · (1+0.25·t4)    // drag doubles off-throttle
        applied at the CG (raised -0.1·(1-θ) m when ≥2 wheels down); dragScale = 1 (1.5 for traffic)
Fdown = AERO_COEFFICIENT · 2000 · |v| · max(0.4, sqrt(max(0, fwd·v̂))) · up_y · aeroScale · (1+0.25·t4)
        (up_y = clamp>=0 body up.y, or 1 with ≥2 wheels down; ×0.8 when no wheel touches)
        applied along −body up at z = zr + (zf−zr)·AERO_CG·0.01;  aeroScale = 1 − 0.75·sb  (sb = speedbreaker blend, §12)
```
Note: downforce is **linear** in speed (AERO 0.2 → 400 N per m/s).

## 7. Suspension [C] (0x6AA020)
Per wheel (axle a), x = compression (m):
```
rest  = (RIDE_HEIGHT[a] + t3)·IN;  travel = TRAVEL[a]·IN
x     = clamp(upAlign·rest + groundOffset, 0, travel)    // upAlign = clamp01(groundNormal·bodyUp); beyond travel → bottom-out push-up
if x<=0 or upAlign<=0.2: wheel is free (§8 free spin) 
vel   = (x - xPrev)/dt
Fspr  = SPRING_STIFFNESS[a]·LBIN · x · (1 + SPRING_PROGRESSION[a]·x)
v'    = |vel|>SHOCK_VALVING[a]·IN && SHOCK_DIGRESSION[a]<1 ? sign·valv·pow(|vel|/valv, 1-DIGRESSION) : vel      [U on pow args]
Fdmp  = (vel>0 ? SHOCK_STIFFNESS : SHOCK_EXT_STIFFNESS)[a]·LBIN · v';  Fdmp = 0 if Fdmp > MASS·SHOCK_BLOWOUT·9.81
Fsway = ±(xL − xR)·SWAYBAR_STIFFNESS[a]·LBIN       (+ on the left wheel, − on the right)
N     = max(0, Fspr + Fdmp + Fsway)                    // normal load fed to the tire, ×max(0.3, 4·cosθ−3) for tilted contact
```
After summing all wheel forces: the yaw part of the resulting torque is scaled by **YAW_SPEED** (×1.6 extra for traffic):
`τ += (τ·up)(YAW_SPEED−1)·up`. On landing (first contact) the pitch/roll rates are halved.

## 8. Tire model [C] (0x69DA90 Tire::UpdateLoaded; Begin 0x68DFE0; free 0x68E370)
Inputs: `vy` lateral contact speed, `vx` longitudinal contact speed, body speed V, load N, dt, wheel radius r, ω.
Per-tick params (sim 0x6B1DD0, before the tire update):
```
slipLimit = ratio01(V,10,71)+0.5                 (drag mode: 71)
latScale  = 1.2·Table[0.833,0.958,1.008,1.0167,1.033,1.033,1.033,1.0167,1,1](V/37.997)   (traffic 3.0)
grip      = 1.1·Table[0.909,1.045,1.09,1.09,1.09,1.09,1.09,1.045,1,1](V/37.997) + 0.25·launch   (traffic 1.1, drag mode 2.0)
            then ×modifiers of §10 (YAW_CONTROL rear, speedbreaker front, staging ×0.25, blown tire ×0.3)
scrub     = 0.15 + 0.85·sb
latMul,longMul = 1+0.2·t1, 1−0.2·t1;   staticMul = 1 (rear: drift Graph, §10)
lowSpeed  = (!locked && ω>=0 && forward gear) ? 0.85 + 0.15·ratio01(V,13.41,22.35) : 1      // −15 % grip below 30 mph
Tb  = BRAKES[a]·1.3558·4·brakeIn;  Teb = EBRAKE·1.3558·10·ebrakeIn (rear only)
```
Update:
```
if |vx|>=1: brakeTorque -= sign(ω)·(Tb+Teb)
else:       driveTorque -= ebrakeIn·driveTorque;  brakeTorque -= (brakeIn+ebrakeIn)·vx·N/r   (low-speed hold)
α = atan2(vy, |vx|);  slipX = ω·r − vx;  s = sqrt(slipX²+vy²)
μd = DYNAMIC_GRIP[a]·grip·lowSpeed
lock test (0x68E220): Tlock = 1.2·(Teb + Tb·BRAKE_LOCK[a]);  locked = (r·μd·N·|vx|/|v| + 100|ω| < Tlock && Tlock>1) → ω=0
if sliding (traction<1 last tick) or locked:
     k = μd·N/s;  Fx = k·slipX;  Fy = −k·vy                       (÷μd when V<0.447 m/s and μd>0.1)
     Fx limited to (driveTorque+brakeTorque)/r in the same direction
else (gripping):
     Fx = (driveTorque+brakeTorque)/r + (ωdot·r − ax)/r·10           // 10 = wheel inertia, kg·m²
     Fy = −sign(vy)·Lat(|α|, N)
Fy *= wheel.latBoost (front speedbreaker, §12); if (driveTorque+brakeTorque)·vx>0: Fx *= 1.5 (kept only if it saturates)
Fy *= latMul; Fx *= longMul
Fmax = staticMul·grip·STATIC_GRIP[a]·N·lowSpeed
if |F|>Fmax: traction = Fmax/|F|; F *= traction; slipLim = traction²·slipLimit   else traction=1 (undo the 1.5)
if |slipX| > slipLim: traction *= slipLim/|slipX|
Fy *= surface.LATERAL_GRIP; Fx *= surface.DRIVE_GRIP
if vx<=1: Fy *= min(|vy|,1)  else Fx −= scrub/GRIP_SCALE[a]·sin(α)·Fy          // cornering drag
wheel spin: if traction<1: τ = ½(driveTorque+brakeTorque − Fx·r + τprev); ωdot = (τ − 2·ROLLING_RESISTANCE·ω)/10 − traction·slipX/(dt·r)
            ωdot = lerp(ωdot, ax/r, traction);  ω += dt·ωdot          (gripping: ω follows the ground)
```
Lateral curve `Lat(α,N)` (0x68E090), α in degrees, `x = N·0.001·0.8`, tables over x=0,2,4,6,8,10:
`T0=0 · T1=[0,1.2,2.3,3.0,3.0,2.8] · T2=[0,1.7,3.2,4.3,5.1,5.2] · T3=[0,1.8,3.5,4.9,5.8,6.1] · T4=[0,1.83,3.6,5.0,5.96,6.4] ·
T5=[0,1.86,3.7,5.1,6.13,6.7] · T6=[0,1.9,3.8,5.2,6.3,7.1]`, `i=floor(α/2)`: `Lat = (i>=6 ? T6(x) : lerp(Ti(x),Ti+1(x),α/2−i)) ·
GRIP_SCALE[a] · 2 · latScale · 2500` N. The factor 2 is an int at 0x8F8AA0 stored as 0xFFFFFFFE; magnitude 2 is assumed because
the sign must oppose slip [U]. Stiffness is about 5.5 kN/° at 4 kN load, so the static limit (STATIC_GRIP·N) is reached at 1–2°.
Applied force per wheel = `up·N + side·clamp(Fy, ±MASS·0.25·|vy|/dt) + fwd·Fx` (clamp stops overshoot).
Free (airborne) wheel: brake torques applied to ω, `ωdot=(drive+brake)/10`, lock check, Fx=Fy=0.

## 9. Brakes and handbrake
* Foot brake per wheel = brake input × tuning bias (§2). Burnout: gas>0.8, brake>0.5 and |v|<10 mph → only the non-driven axle
  brakes (input = mph·0.05); the driven wheels spin.
* Handbrake → `ebrakeIn` on the rear tires only; +0.5 when handbrake>0.2, |β|>0.3 rad and speed<80 mph.
* BRAKE_LOCK multiplies the brake torque **only in the lock test**, so rear values of 3–3.3 make the rear lock first.
* Handbrake grip blend `hb`: handbrake<0.5 → hb += 1·dt (max 1), else hb −= 20·dt (min 0.5); it fades the YAW_CONTROL stabilizer.
* No ABS or TCS exists in the code.

## 10. Steering and assists
**Steering** (0x69E820, 0x68E9E0, 0x68EC70, 0x68D6A0), angles in degrees, `vz` = forward speed m/s:
```
maxDeg = TableA(vz)                     A over 0..160 m/s (10 pts): 40,20,10,5.5,4.5,3.25,2.9,2.9,2.9,2.9
         (drag-lane mode 1: 45,15,11,8,7,7,…; mode 2: 45)
maxDeg *= 1 + TableB(vz)·1.45·(1 − ((gas+1) − (handbrake+brake)/2)/2)    B: 1,1,1,0.56,0.5,0.35,0.3,0.3,0.3,0.3
maxDeg *= TableC(|q|) [1,1,1.1,1.2,1.25,1.35 over 0..1] · (1+0.2·t0)      q = steering-assist state 0x140 [U]
countersteer: when steering toward the slide, maxDeg >= rear tire slip angle (deg); cap 45
target = clamp(maxDeg·STEERING·steerIn, ±45)
rate   = 180 · TableD(|Δin|/dt)[1,1.05,1.1,1.5,2.2,3.1 over 0..10] (smoothed) · TableB(vz) · TableE(|q|)[1..1.4] · STEERING   (°/s)
angle  = clamp(target, angle − rate·dt, angle + rate·dt);  speedbreaker: angle = lerp(angle, steerIn·60, sb)
Ackermann: inner = a, outer = WB·a/(a·TRACK_WIDTH_F + WB) (rad)
```
**YAW_CONTROL** (0x69EBF0, 0x68EF10): `yc = lerp YAW_CONTROL[0..3] over x = min(1, V/Vtop)`, where
`Vtop = RED_LINE·RPM2RAD·rmean/(GEAR_RATIO[top]·FINAL_GEAR)` (or SPEED_LIMITER if lower). Rear tire grip
`×= 1 + (m−1)·(1−drift)·hb` with `m = 1 + min(0.35, (V/30)·|β|·yc)` (m = 1 when ebrake>0.5 and |β|<20°). yc is +2.5 in one race mode [U].
**Drift state** (0x6A9760; not for traffic or while staging): `drift` += 8/s while state 1–2 and −2/s otherwise (0..1).
Enter (1→2) when V>13.41 m/s and (handbrake>0.5 or |β|>12°). Exit (3) when |β|·gas ≤12°. A counter-yaw spin brakes the drift.
While drift>0:
* rear `staticMul = Graph{(0,0),(0.2618,0.1),(0.5236,0.45),(0.7854,0.85),(1.0472,0.95),(1.5533,1.15),(1.5708,0)}((4·|steer|[steering into the slide] + (|r|+|β|)/2)·drift)`;
* stabilising torque about up: `−4·inertia·drift·Table(|β|)·r` when the yaw rate r opposes β;
* charges the speedbreaker: `+drift·dt·0.5` when |β|>30°, V>15.65 m/s and not in speedbreaker.
**Air control** (0x6A8F40): airborne >1 s, upright, falling from >2 m → extra down force `(3+10·ratio01(V,0,20)·(1−ratio01(h,0,6)))·m`,
else 3·m; pitch/roll levelling torques (gains 40 and 20).
**Sleep** (0x69D120): V<0.5, all wheels down, brake+handbrake>0, gas=0, |ω|<0.25 → damp to rest.

## 11. Physics modes (state 0xCC from race object; driver class 0xC8)
Normal = 1. Drag = 0 (slipLimit 71, grip 2.0, lane steering tables). Drift race = 2 (drift assist 0x69EAC0; unused in MW [U]).
Traffic (class 1) uses fixed constants (latScale 3, grip 1.1, drag ×1.5, yaw ×1.6, 45° steering).

## 12. Speedbreaker [C] (player 0x6EDD60/0x6EDC90/0x6E9AA0, time 0x6F4D10, handling 0x6B1DD0)
```
toggle (button): allowed if mph>=30 and not animating/staging/loading (LocalPlayer::CanDoGameBreaker); ON needs charge>0; pressing again turns it OFF;
         each tick (DoGameBreaker) it switches off when CanDoGameBreaker fails or the charge reaches 0
active:  charge -= 0.1·dt (≈10 s full bar; unless cheat); OFF at 0
inactive: if mph>100 (strictly): charge += dt/25 (Tweak_GameBreakerRechargeTime 25, ...Speed 100); drifting adds charge (§10); clamp 0..1
time scale: target 0.25 while active (ramps at 2.0/s), back to 1.0 at 0.5/s after it ends
on activation: camera mode 10; collision mass override = 2·MASS [U on meaning]
handling blend sb: 1 while active, then −1/3 per s (3 s fade):
   extra down force −2g·m·sb, steering lerp to 60°·input, front grip ×(1+0.75·|steer|·sb), aeroScale 1−0.75sb, scrub 0.15+0.85sb
```
NOS does not recharge while speedbreaker is active.

## 13. Unity port (PlayerCar.cs / CarSpec.cs) — implemented, with these deviations
1. **Wheels.** Each wheel is a raycast from above full bump to below full droop. The design pose (CarBuilder) is held at
   static spring compression. Tire and suspension forces are applied at the hub, and the CG height comes from
   ROLL_CENTER − RIDE_HEIGHT above the hubs.
2. **Clutch.** The locked clutch couples the engine rigidly to the driveline speed: no reflected-inertia term and no
   launch wheelspin rule. While engaging, the torque is capped so the engine cannot overshoot the driveline in one tick.
3. **Gear changes.** A shift opens the clutch and cuts throttle for `SHIFT_SPEED·ratio`, then engages over 0.25 s.
   This is an interpretation of 0x6AF490: syncing the engine instantly gave ~10 kN·m kicks at the wheels.
4. **Engine braking** fades to 0 below ~3 rad/s of wheel speed, so a car in gear does not creep backward at a standstill.
5. **Tire.** The sliding branch also needs |slip| > 0.25 m/s, since a wheel back near rolling speed grips instead of
   sliding on noise. A gripping wheel rolls with the road plus its acceleration (`ω = (vx + ax·dt)/r`).
   Braking forces are clamped so they cannot reverse a contact's motion within one tick.
6. **Cornering drag** is applied against vx (the spec's sign would accelerate the car).
7. **Drift.** The rear static-grip multiplier is the 0x8f8fb0 table `[1.1,0.95,0.87,0.77,0.67,0.6,0.51,0.43,0.37,0.34]` over
   0..1 of `(4·|steer into slide| + (|r|+|β|)/2)·drift`. The Graph at 0x938210 is the stabilising-torque gain.
   §10 above mixes the two up.
8. **Air control** gains are softened: levelling at 4/2 rad/s² per rad, with damping.
9. **Not modelled:**
   - (junkman, tunings and surface grip are now in CarSpec / the tire model: §2, §8)
   - the power-oversteer differential helper
   - the steering-assist state q (TableC/E = 1)
   - staging launch control
   - speedbreaker camera mode 10 (the 2·MASS collision mass is now applied, §15)
10. **Wheel rays** hit other cars, so a car pushed onto another still has grip, but not knocked props. Suspension load is
    capped at 3·MASS·g per wheel.

## 14. Open questions
* The integer ×2 lateral-curve factor (stored as −2); the exact damper digression pow arguments; the meaning of state 0x140
  (steer TableC/E input) and of DAT_009377F4/F8 (assumed 0) in the YAW_CONTROL term.
* Inertia tensor computation; reverse-gear selection; whether the speedbreaker drain uses real or scaled time.
* Engine vfunc+0x28 (launch value) and vehicle vfunc+0x20 (staging) are named by behaviour, not by symbol.
* Tuning slider range (assumed ±1, where 1 = slider ±10).

## 15. RigidBody and RBVehicle (Physics/Behaviors/RigidBody.cpp, RBVehicle.cpp; exe 0x670B90-0x6BB7EC, 0x68BC40-)

The sim's own rigid body. Unity keeps PhysX for integration and the contact solve; the port applies the exe's rules
where they differ (Runtime/CarContacts.cs, Runtime/Physics/RigidBodySpecs.cs, PlayerCar.RigidBodyFrame/MomentScale).

### 15.1 rigidbodyspecs (class 0x7C90BB38; harness `physcore rb`)
| collection | GRAVITY | WALL_FRICTION | GROUND_FRICTION | OBJ_FRICTION | WALL_E | OBJ_E | WORLD_MOMENT | GROUND_MOMENT | OBJ_MOMENT (xyz, w=mass) |
|---|---|---|---|---|---|---|---|---|---|
| cars | −9.813 | 0.2, 0.1 | 0.55, 0.45 | 0.55, 0.35 | (0,0,0.15) | (0,0,0.05) | (2, 2.5, 1e6) | (1.5, 1, 1.5) | (2, 3, 2, 0) |
| racers | −9.813 | **0.55, 0.3** | 0.55, 0.45 | 0.55, 0.35 | 0 | (0,0,0.05) | (3, 1.5, 3) | (1.5, 3, 1.5) | **(4, 15, 3, 0)** |
| cops / copsuv | −9.813 | 0.2, 0.1 | 0.9, 0.8 | 0.55, 0.35 | (0,0,0.15) | (0.15,0,0.15) / 0 | (2, 2.5, 1e6) | (1, 1, 0.8) | (1.2, 1.2, 1, **0.75** / **1.5**) |
| traffic | −9.813 | 0.2, 0.1 | 0.55, 0.45 | 0.55, 0.35 | (0,0,0.15) | (0,0,0.1) | (2, 2.5, 1e6) | (1.5, 1, 1.5) | (2, 1, 4, **0.5**) |
DRAG, DRAG_ANGULAR, CG, COLLISION_BOX_PAD, GROUND_ELASTICITY and NATURAL_ANGULAR_DAMPING are 0 for every car;
SLEEP_VELOCITY 1. Every simsurface has GROUND_FRICTION = WORLD_FRICTION = 1 (harness `attribclass simsurface`), so
the surface never scales collision friction.

### 15.2 Integration [C] (DoIntegration :657-709, OnBeginFrame :507-525)
Semi-implicit Euler: `v += F·dt/m; x += v·dt; ω += I_w⁻¹·τ·dt`, then each component of v bounded to ±300 m/s and of ω to
±30 rad/s; quaternion update `q += ½·(ω·dt)q`, renormalised; the position is corrected so the CG stays put when the body
rotates about it (`x += R0·cg − R1·cg`). Gravity `GRAVITY·m` on y unless attached to the world. Asleep (state ≠ 0) clears
the velocities. No damping anywhere (DoDrag is the only drag and its coefficients are 0 for cars).
**Port:** PhysX integrates; `RigidBodyFrame` bounds the components, `angularDamping = NATURAL_ANGULAR_DAMPING` (0, was
0.05), `maxAngularVelocity = 30·√3`, and the gravity difference (GRAVITY − Unity's) is added each step.

### 15.3 Collision response [C] (ResolveGroundCollision :721, ResolveObjectCollision :1032, ResolveWorldCollision :1227)
Each contact builds a `Dynamics::Collision::Moment` (mass, principal inertia, CG, velocities) and a `Plane` {point, normal,
Friction(uk = min(us, uk), us)}; `Moment::React(plane, n)` (0x488830, an n-step impulse solve) applies it. React uses
**inertia ⊙ mInertialScale** (0x488830: `+0x80·+0xBC`), i.e. SetInertiaScale multiplies the tensor per axis, and
scales the velocity change by (1 + elasticity).
| kind | friction | elasticity | inertia scale | other |
|---|---|---|---|---|
| ground (box corners vs the road face below, DoInstanceCollision2d :790) | GROUND_FRICTION × surface | \|n_body ⊙ GROUND_ELASTICITY\| | GROUND_MOMENT_SCALE | deepest penetration pushed out along the normal |
| wall (barriers / OBB, :1227, :1168 with a 1e6 kg box) | WALL_FRICTION (OBB: OBJ_FRICTION) × WORLD_FRICTION | \|n_body ⊙ WALL_ELASTICITY\| | WORLD_MOMENT_SCALE (ModifyCollision :934) | velocity relative to a moving world object |
| object (two rigid bodies, :1032) | √(us_A·us_B), min(that, √(uk_A·uk_B)) | per body \|n_body ⊙ OBJ_ELASTICITY\| | OBJ_MOMENT_SCALE.xyz; mass × .w when > 0 | Separate(): overlap split by mass (smackable pairs by closing speed) |
**RBVehicle::ModifyCollision** [C]:
* world, every vehicle: Moment CG y = 0 (the impulse acts at the body origin's height). Player only: elasticity 0 and,
  since Tweak_HeadOnDot = −1 always passes, yaw inertia × `1 + 8·ramp(−n·v, 0, 5)·ramp(−n·fwd, −1, 1)`.
* object: animating → immobile. Player vs a vehicle, both with > 2 wheels down: CG y = contact y − position y − 0.125
  (contact 0.125 above the CG: no roll from a side swipe). Non-player vs a racer/human with player reactions:
  collisionreactions (elasticity +, mass/inertia ×, CG roll height / weight bias) [not ported: racers have none].
  A loose smackable with NO_CAR_EFFECT → the car is immobile. Collision mass (`mCollisionMass`, set to
  Tweak_GameBreakerCollisionMass 2 × MASS by the game breaker): inertia × collisionMass/momentMass, mass = collisionMass.
* RBVehicle::OnBeginFrame :66-87: staging (and modelled, not animating) → after integration the local x/z velocity and the
  local yaw rate are zeroed and the x/z position kept: a staged car cannot creep or turn.
* RBVehicle::CanCollideWithGround: non-players with every wheel down and no object hit skip body-vs-ground (optimisation).
  CanCollideWithWorld: traffic tests the world every 2nd/4th frame when slow. ShouldSleep: never while wheels are down
  (unless destroyed on its wheels for 2 s, non-player).

**Port (CarContacts.Modify, PhysX contact modification):** world contacts are ground when |n.y| ≥ 0.7071 [?: Unity has
one contact stream; the exe's ground is the face under each box corner], else wall. Friction, bounce and the CG rules
above per contact (the player-vs-car CG sign was inverted before: the contact now sits 0.125 above the CG); per pair
`inverseMassScale` = 1/(mass multiplier) and `inverseInertiaScale` = 1/(median axis of the moment scale ×
collision-mass tensor scale). **Per-axis remainder** (PhysX has only a scalar): `CarContacts.CorrectAngular`, called from
PlayerCar.OnCollisionEnter/Stay, takes the reported contact impulses J_i at p_i, `L = R⁻¹ Σ (p_i − com) × J_i`, and adds
`R·(L/I ⊙ (1/(S_i·T) − k))` to ω (S_i per contact kind, the player's head-on factor in S.y). This replaces the old
yaw-only HeadOnDamp. Linear impulses still come from PhysX's scalar-scaled solve [deviation: the exe's effective mass at
the contact uses the full per-axis tensor; superseded by §22]. Object elasticity: §22.
Staging hold in `RigidBodyFrame` (the held spot is re-taken when the car is moved > 1 m, [ours]).

## 16. Physics::Info (PhysicsInfo.cpp, exe 0x66BAE0-0x68A7E0) — Runtime/Physics/PhysicsInfo.cs
All [C] from the exe:
* `MaxTorque` 0x66C3A0: the largest TORQUE point (first on ties), at `IDLE + i·(MAX_RPM−IDLE)/(n−1)` clamped IDLE..RED_LINE.
* `MaxInductedTorque` 0x66C180: per point `TORQUE[i]·(1 + InductionBoost(rpm_i, spool 1, tunings))`; EngineRacer's reset
  0x691ED0 keeps it ×1.3558 as the peak torque (Nm) and its rpm (0x66C2D0) — PlayerCar now uses both (torque
  converter ramp, perfect-launch torque `Te = peakTorque·nos`), was a 50-rpm scan without boost.
* `InductionBoost` 0x66BCC0(engine, induction, **rpm, spool**, tunings, psi) as §3.3; `InductionRPM` 0x66BC60.
* `ShiftPoints` 0x673400 (§3.7, boost-free because of the swapped arguments); `NumGears` 0x66C8A0 = Num_GEAR_RATIO − 2;
  `WheelDiameter` 0x66BF20 = RIM·0.0254 + SECTION·0.002·ASPECT·0.01 (front flag selects the axle).
* `PerfStats::Fetch` 0x678CE0 — wheel radius = rear diameter/2; launch rpm = MaxTorque's; dt **0.125 s** (1 s when filling a
  table), 1 s after 100 mph; `rpm = ((v/r)·ratio·(ωred−ωidle)/ωred + ωidle)·9.5493` clamped IDLE..RED_LINE, held at the
  launch rpm in 1st; `F = Torque·1.3558·(1 + InductionBoost(rpm, 1))·ratio/r`, × (TORQUE_BOOST + 1) while t < NOS_CAPACITY
  and v > 5; `D = |DRAG_COEFFICIENT·v²|`; `v += (F − D)/m·dt`; 0-100 time = t when v first ≥ 44.703; top speed = the limiter,
  or v once top gear has F < D or rpm ≥ RED_LINE; v at the end when the run ends in top gear; shift when up[g] ≤ rpm; 120 s
  cap. Raw handling = lerp(HandlingRating[0], [1], Σ PerformanceWeights[i].Handling·GetPercent(i) / Σ weights).
* Init 0x683EB0: stock entries for every pvehicle with ratings (and not a temporary collection), max entries from
  Upgrades::SetMaximum 0x67F950 when it succeeds; normalisation over all entries with 0x6794E0 (min with −1, max with
  +1): TopSpeed = ramp(top), Handling = ramp(handling), Acceleration = 1 − ramp(0-100 time). ComputePerformance 0x679580
  returns the cached stock entry.
* Gameplay/PerformanceRatings.cs now runs PhysicsInfo.Fetch (its stand-in run, 50-rpm launch scan and own shift points
  are gone). ComputeAccelerationTable is a separate function (0x673810, ported in AIRacer.cs by the AI port).

## 17. Damage for racers and the player (DamageVehicle / DamageRacer / DamageDragster, EngineRacer IEngineDamage)
* damagespecs (class 0xC1F0B434; harness `physcore dmg`): **racers** FORCE 20, HIT_POINTS 0, HP_THRESHOLD 0,
  SHOCK_FORCE 40, SHOCK_TIME 3, VisualScale F 0.8 / R 1.5 / sides 3.5 / rear corners 7.5 / bottom 0.7, **HitPointScale 0 on
  every zone**. So hits never take hit points from a racer: there is no damage-based performance loss in MW, only
  visual zones (CarRender/CarDamage), tyres (DamageRacer puncture → blown after 0.5 s, 180 s with run-flats;
  Police/Roadblock.cs TireState) and the engine. Cops: HIT_POINTS 5-10 (copcross 400, D78FDF78 900), thresholds 0.75-10.
* Chassis state [C] (Chassis.cpp:343-347): `IEngineDamage::IsBlown() || IsDestroyed()` → brake 1, e-brake 1, gas 0.
  **Port:** PlayerCar.Step after the input layer (`EngineBlown || Destroyed`).
* EngineRacer [C] (EngineRacer.cpp:467-481, 1270-1280): `Sabotage(t)` arms a timer (ignored if one runs or the engine is
  blown); while it counts down `ω += 0.5·sin(12 turns/s · time left)·ω` each tick; at 0 → `Blow()`: blown (EEngineBlown),
  no throttle, no NOS. `Repair()` clears both (ResetDamage). **Port:** `PlayerCar.Sabotage(float)`, `Blow()`,
  `ResetDamage()`; the old `Sabotage()` now blows at once.
* `IsDestroyed` = DamageVehicle total ≥ 1 (CarDamage.Destroyed) or `Destroy()` (DamageVehicle::Destroy: zones to 6, total
  1). DamageDragster::CheckTotaling [C]: not a ground hit, not animating, this body not immobile, speed before > 100 mph and
  lost > 40 mph → Destroy. **Port:** `PlayerCar.Destroyed`, `PlayerCar.Destroy()` (also CarDamage.Destroy).
* DamageVehicle shock (SHOCK_FORCE/SHOCK_TIME): only read by traffic AI (AIVehicle.cpp:914) and AIActionTooDamaged
  (cops); not kept for racers.

## 18. LocalPlayer (Sim/Entities/LocalPlayer.cpp) — game breaker
CanDoGameBreaker: single-player, ≥ 30 mph speedometer, not animating/staging/loading (and no NIS [?: decomp unsolved]).
ToggleGameBreaker / DoGameBreaker as §12 (drain 0.1 × **real** dt, recharge dt/25 above 100 mph, off at charge 0 or when
CanDo fails); CanRechargeNOS = not in the breaker; Tweak_GameBreakerCollisionMass 2 (§15.3). The rest is HUD
(neighbourhood names), force feedback and controller port — not physics.

## 19. Still open (previous round; §20-24 close most of it)
* PVehicle (0x6681D0-0x68A0F5): behaviour attach/detach, model/driver-class switching and the physics LOD
  (SuspensionSpline/EngineSpline far from the camera) — Ghidra has no functions for PVehicle::SetSpeed 0x670210 and
  neighbours; not ported. DrawCar/SoundCar glue unchanged.
* Moment::React(Moment&, …) (0x488F70, 4 KB): how two elasticities combine and the exact n-step impulse split.
* The sim body origin height the wall rule (CG y = 0) refers to: taken as the Unity box centre [?].
* collisionreactions (non-player vs player reactions) — racers have none; traffic's belong to TrafficContacts.
* smackable NO_CAR_EFFECT needs the Smackable port to call `CarContacts.SetNoCarEffect(rb, true)` on detach.

## 20. Collision geometry for cars and props (Physics/Common/Bounds.cpp; Runtime/Physics/CollisionBounds.cs)
CollisionGeometry packs (LoaderBounds 0x687240, chunk 0x8003B900; Core/CollisionGeometry.cs): GLOBALB.BUN holds 83
vehicle collections, TRACKS/L2RA.BUN 379 scenery collections (STREAML2RA none). Harness `physcol bounds [model]`,
`physcol geo <file>`, `physcol smackgeo`.
* **Vehicles** [C]: PVehicle::Construct 0x689820 → `CollisionGeometry::Lookup(UCrc32(pvehicle MODEL))` (Jenkins hash).
  The root node is the body box: RigidBody dimension = its half extents, inertia `Util_GenerateCarTensor` 0x6E8F50 =
  `m/12·(y²+z², x²+z², x²+y²)` of the full box × TENSOR_SCALE; its pivot is the body origin in model space (pivot.y =
  half height, so the model origin is the box bottom). Children with PrimVsWorld / PrimVsObjects are the collision
  primitives (Collection::AddTo 0x672050 → RigidBody::AddCollisionPrimitive 0x6A6DA0), positioned from the root pivot.
  Example bmwm3gtre46: root 0.938×0.621×2.284 half; [1] lower body box 0.934×0.376×2.289 at (−0.004, −0.244, 0)
  (world+objects); [2] cabin box 0.598×0.232×1.308 at (0, 0.384, −0.968) (world+objects); four corner spheres r 0.328
  at (±0.75, −0.44/−0.05, 2.105) and two mirror spheres r 0.236 (world only); [4] MeshVsGround point cloud (ground).
  Some traffic roots are themselves primitives (TRAFTAXI, TRAFFICCOUP, ...).
* **Port**: PlayerCar.Setup builds them (CollisionBounds.BuildVehicle): the root BoxCollider (which other systems read)
  becomes the first PrimVsObjects box, the rest are colliders under `Body/CollisionPrims` (car axes, so they follow the
  body when TrafficVehicle moves it); the inertia uses the root dimension. CarContacts filters contacts per primitive:
  world-only primitives ignore bodies, object-only ones ignore the world, and the spheres never touch the road [?: the
  exe sends spheres to DoBarrierCollision only; its ground is the point cloud / box corners]. Without a collection the
  old render-bounds box stays (also with `CollisionBounds.UseCollisionGeometry = false`, the fallback switch).
  **Height**: every car's lower box bottom is at the model origin (0.000-0.008 m above it; harness `physcol lowpoint`),
  and the exe's body origin rides at RIDE_HEIGHT - static sag above the road (0.10-0.18 m). The racer sim's design pose
  has the road at root y = 0, so PlayerCar.Setup places the collision at that height (`PlaceCollisionAtOrigin`), not at
  CarBuilder's drawn body, which is lifted by RIDE_HEIGHT x 0.01 (inches read as cm: bmwm3gtre46 0.06 m, gallardo 0.065,
  gti 0.08 instead of ~0.11-0.15). With the box at the drawn height it touched the road under squat, carried the car on
  GROUND_FRICTION 0.55 and unloaded the wheels (the "7,000 rpm in 1st and slowing" play-test failure; the GTI's 2 cm
  more clearance hid it). TrafficVehicle places the same geometry at its own body origin (`PlaceCollisionAtOrigin(0)`).
  The corner/mirror spheres sit 3-5 cm under the road even in the exe (bottom -0.13..-0.19 from the origin), so they
  meet only world walls (contact-filtered against everything else). Non-players on their wheels skip body-vs-ground
  (RBVehicle::CanCollideWithGround). **Deviation**: the MeshVsGround point cloud is not built (the boxes meet the road).
  [CarBuilder's body lift should be RIDE_HEIGHT x 0.0254 - sag: the drawn body sits 4-10 cm lower than the exe's.]
* **Smackables** [C]: sub_681540 looks the SmokeableSpawner's Collision key (+0x24) up in the track pack
  (Collections::Find 0x681340) and makes the smackable only when found and its half dims are non-zero: 13,358 of 13,484
  spawners have one; 6 keys (126 spawners: C8F94D11, 9688E411, FB1A3B11, F52B1011, BAB2FD8B, 1C194C84) have none, so the
  exe never makes those props smackable. `CollisionBounds.SceneryBox(key)` gives the box in the mesh's Unity frame
  (sim (x, y, z) → (z, y, −x)); Smackable.Make still uses the render-mesh bounds [hook].

## 21. PVehicle (Physics/Common/PVehicle.cpp, decomp empty; Runtime/Physics/PlayerCar.PVehicle.cs)
Decompiled this round with headless Ghidra (`tools/ghidra.sh decompile` creates the functions Ghidra missed) and REA.
IVehicle vtable of PVehicle at 0x8AA828 (slot 13 SetPhysicsMode 0x688390, 26 SetVehicleOnGround 0x685810, 37 SetSpeed
0x670210, 46 ComputeHeading 0x670700, 48 SetAnimating 0x686410, 49 IsOffWorld). Mechanic keys (static init
0x887950..0x887BB7): RIGIDBODY 0x92C5D8, INPUT 0x9261B0, SUSPENSION 0x92C650, ENGINE 0x92C598, DAMAGE 0x92C610, DRAW
0x92C588, AUDIO 0x92C560, AI 0x92C5E8, EFFECTS 0x92C5C8, RESET 0x926188.
* **LoadBehaviors** 0x685240 [C]: RBComplexParams with dimension = root half dims, inertia Util_GenerateCarTensor(MASS,
  dim, TENSOR_SCALE) and **mCollisionMask by driver class: human 0 / racer 3 / remote 6 → 0, cop 2 → 0x80, any other
  (traffic, none, NIS) → 0x40** (traffic_collision.md B.8).
* **Physics modes / LOD** [C]: leaving SIMULATED (0x6865D0) = DisableModeling and pause DRAW, SUSPENSION, ENGINE,
  RIGIDBODY, EFFECTS, RESET, AUDIO and DAMAGE; entering SIMULATED (0x6864A0) unpauses them and EnableModeling; INACTIVE
  toggles triggering. EMULATED keeps only INPUT and AI: AIVehicle::UpdateSimplePhysics moves the car. Racers
  (AIVehicleRacecar::ShouldDoSimplePhysics: not animating, not staging, not the player) and cops (AIVehicleCopCar::Update)
  are EMULATED exactly while IsOffWorld; DisableSimplePhysics = SetVehicleOnGround + SetSpeed + 1 s
  INVULNERABLE_FROM_PHYSICS_SWITCH. The "physics LOD" is "no collision loaded under the car", not a distance.
  Port: `PlayerCar.SetPhysicsMode` (EMULATED: Step runs only the off-world test, the body stops colliding and falling;
  back to SIMULATED: 1 s invulnerability). The AI's own simple-physics movement is the AI code's.
* **OnTaskSimulate** 0x686060 [C]: offscreen/on-screen timers (renderable, in view: DrawCar); while SIMULATED the
  suspension is paused when the body sleeps and the vehicle is destroyed [?: ICollisionBody vfunc 0x34 read as
  IsSleeping]; speedometer from the transmission; a no-ground timer; the staging launch timer (0x6702D0).
  Port: `TickPVehicle` (timers with Renderer.isVisible standing in for DrawCar::InView [ours], off world, invulnerability).
* **IsOffWorld** 0x680840 [C]: suspension paused → `!GetWorldHeightAtPointRigorous(position)`; else on world when the
  body's WWorldPos surface ≠ SimSurface::kNull, else off world when ≥ 2 wheels have a kNull road surface (no suspension →
  off world). Port: PlayerCar.WPos (Core WWorldPos over the packs) and Wheel.OnWorld (the wheel ray hit a pack face, or
  the external sim's surface).
* **SetSpeed** 0x670210 [C]: velocity = forward·speed; EngineRacer::MatchSpeed (GuessGear over the shift points,
  GuessRPM = idle + |v|/r̄·FINAL·ratio·(red − idle)/red, clutch engaged, timers 0); SuspensionRacer::MatchSpeed (ω = v/r,
  traction 1, no lock, burnout and drift reset). Port: `PlayerCar.SetSpeed`.
* **SetVehicleOnGround** 0x685810 [C, details U]: camera "CameraMessagePort" jump; orientation from initialVec
  (Util_GenerateMatrix); zero velocities; GetWorldHeightAtPointRigorous at resetPos + dimension.y; not found → placed at
  resetPos, off world, returns false. Found: no suspension → y = height + dimension.y; else a WWorldPos(0.05) per wheel
  and the body turned to the plane through the axle/side mid-points of the four contacts; PlaceObject; reset SUSPENSION,
  ENGINE, AI, RESET; SetSpeed(0); offworld = !found. Port: `PlayerCar.SetVehicleOnGround` (the design pose has the
  road at local y = 0, so the four contacts place that plane) [?: the exe's exact wheel-height term].
* **SetAnimating** 0x686410: pauses the AI behaviour while animating, reloads behaviours, RigidBody::SetAnimating.
* **External sims** [ours]: cars on TrafficVehicle (traffic, cops, AI racers) feed their disabled PlayerCar with
  `SetExternalSimState`, `SetExternalWheelState(i, contact, slip, slipAngle, roadSpeed, load, ω, surface)`,
  `SetExternalNos(firing, tank)` and `TickPVehicle(dt)` (TrafficVehicle.Bridge), so WheelState/WheelAudio/NosFiring and
  the off-world test see them.

## 22. Two-body contacts: elasticity, per-axis inertia, collisionreactions, invulnerability (CarContacts.cs)
* **Moment::React(Moment&, plane, n)** 0x488F70 [C, REA]: reduced mass μ = mA·mB/(mA+mB) (an immobile side uses the
  other's mass); per step i (0..n−1, n = 32 for objects): relative contact velocity (v + ω×r of each)·μ along the
  normal, stop when separating; step 0 only records the normal/tangent velocities and the friction impulse (0x4886F0)
  and breaks immobility when the impulse exceeds the threshold (+0xCC, MakeImmobile(true, detachForce)); steps ≥ 1 apply
  the fraction i/n of the impulse and friction through each body's inverse world tensor (inertia ⊙ moment scale).
  Accumulated: **v += Δv_friction + (1 + e_own)·Δv_normal, ω likewise, per body with its own e** — the two elasticities
  are never mixed. Port: each body's PhysX inverse mass and inertia scale × (1 + e_i), restitution =
  (e_A/M_A + e_B/M_B)/(1/M_A + 1/M_B) (M = mass × multipliers): PhysX then gives body i exactly J(1 + e_i)/M_i.
  A car against the world or a loose prop keeps one restitution (the car's e) [?: the prop's own e is not known there].
* **Per-axis inertia**: the PhysX scalar per body is now k = Σc²/(I·S·T) / Σc²/I with c = r × n of the first contact in
  body axes, so the impulse magnitude matches the per-axis effective mass; CorrectAngular adds the remaining per-axis
  angular change, now also for traffic, cops and AI racers on TrafficVehicle (their PlayerCar is disabled but
  registered), using the scale PhysX was actually given (kept per collider pair). The player's head-on yaw factor is
  part of S on walls.
* **collisionreactions** [C]: AIVehicle::SetGoal 0x422480 (driver classes 1-3) picks the aivehicle's PlayerCollisions
  record whose Goal = hash(goal name), else PlayerCollisionsDefault; RBVehicle::ModifyCollision(RigidBody) 0x69ABF0
  applies it when the other vehicle's driver is a racer or human: e = clamp01(e + Elasticity), mass and inertia ×
  MassScale (when > 0), CG.y += RollHeight, CG.z += WeightBias. ChooseReaction 0x68BEF0: front/rear half by
  (contact − position)·fwd, side when |n·fwd| < 0.707 (REARSIDE +0, FRONTSIDE +0x18, FRONT +0x30, REAR +0x48). RBCop is
  RBVehicle (vtable 0x8AD008 jumps to 0x68BD60 and 0x69ABF0). Data (harness `physcol react`): cops default e.g. "cops":
  FRONT e 0.25 mass 0.6, REAR mass 1.2, REARSIDE roll 0.2 bias 0.8 mass 0.2; AIGoalStaticRoadBlock → the "roadblock"
  family (head-on mass ×2.6); AIGoalPit → 30EA49FC (front e 0.4, mass ×4); racers AIGoalRacer → 71B9AF3D (REARSIDE mass
  ×2), default "racers" all 0; traffic per type (truck, van, street; semi FRONT e 0.4 mass ×7). StunSpeed/StunTime are 0
  everywhere. Port: CarContacts for cops and AI racers (traffic stays with TrafficContacts); the CG shift goes into
  CorrectAngular's lever arm (PhysX shares one contact point between the bodies); `CarContacts.SetGoal(car, goal)`
  switches the record [hook]; AI racers default to AIGoalRacer [?].
* **Invulnerability** [C]: RBVehicle::DoPenetration 0x6B7CE0 skips the object collision while the state ≠ NONE and
  stamps the time; OnTaskSimulate 0x68BC80 clears it once the timer (running only after 1 s without a penetration)
  ends. ResetCar: 2 s (manual 1 / auto 2), physics switch 1 s; DrawCar flashes the car for the reset states. Port:
  CarContacts ignores the pair; `PlayerCar.Invulnerability` for the renderer [hook].
* **Grouped barriers**: traffic_collision.md B.8 (cops and traffic pass through scenery-group walls).

## 23. ResetCar remainder (Runtime/Game/CarReset.cs)
Check task once per second (AddTask "Physics", 1.0; the port checked every tick with spots 2 m apart, so its oldest
spot was ~8 m back instead of ~4 s back); ValidTerrain(n) (body WWorldPos kNull and ≥ n wheels kNull); CanRecord needs
ValidTerrain(0); a human car resets at once when ValidTerrain(2) fails (it left the loaded collision); FindNearestRoad
also needs GetWorldHeightAtPointRigorous at the snapped point within 3.5 m; ResetTo = SetVehicleOnGround (a failed
placement keeps the spots); 2 s invulnerability; manual resets refused while invulnerable. PC L2RA has no RESET /
RESET_TO_POINT / GUIDED_RESET zones, so CheckZone never fires.

## 24. Still open
* DrawCar / SoundCar glue (0x6B6FB0 / 0x694160) is render/audio packet filling (in view, distance to view, flashing,
  wheel spin/steer/compression, part visibility): covered by CarRender/Audio except the reset flashing (hook).
* SetVehicleOnGround's exact per-wheel height term; ICollisionBody vfunc 0x34 (IsSleeping?) in OnTaskSimulate.
* (round 9: the point cloud is built, §27.2; smackables use CollisionBounds, §28)
* React's friction impulse (0x4886F0) vs PhysX friction; the 32-step fractional solve vs PhysX's converged one.

## 25. Dynamics::Articulation — the exe's joint solver (Runtime/Game/TrafficPhysics/Articulation.cs)
Decompiled this round (REA): Joint::Resolve 0x48C5D0, Articulation::Resolve 0x48C910 (the joint list 0x911328, run
after every rigid body's OnEndFrame, RigidBody.cpp:1552), IsJoined 0x48AEE0, anchor move 0x48AF30, arc quaternion
0x48AD40, two-body solve 0x48A000, one-body solve 0x48A910, Moment ctor 0x487160, impulse response 0x486DA0, limit
0x48C270, limit push-back 0x48B290 / 0x48B190. All [C] unless marked.
* **Joint** {+0x10 anchor A (body frame), +0x1C IEntity A, +0x20 A immobile, +0x28 anchor B, +0x34 IEntity B, +0x38 B
  immobile, +0x44 constraint list}. IEntity vtable: 0 GetPosition, +4 SetPosition, +8 GetAngularVelocity, +0xC
  SetLinearVelocity, +0x10 GetLinearVelocity, +0x14 SetAngularVelocity, +0x18 GetMatrix, +0x20 GetOrientation (quat),
  +0x24 SetOrientation, +0x2C GetMass, +0x30 GetCG, +0x34 IsImmobile.
* **Resolve**: return when either entity is null or both are immobile. World anchors wA = pA + R_A·anchorA, wB likewise;
  `w = B immobile ? 0 : A immobile ? 1 : mA/(mA+mB)`; target = w·wA + (1−w)·wB. For each mobile side (0x48AF30):
  arm = R·anchor; if |arm| > 0 and |target − p| > 0: orientation = ArcQuaternion(arm, target − p) × orientation
  ([?] 0x488680 read as "normalize, return length"); then SetPosition(target − R·anchor). Then fresh Moments of both
  bodies and the velocity solve, then every constraint.
* **ArcQuaternion** (0x48AD40): d = a·b; d ≥ 1 → identity; d < −0.99999 → (cross, 0) normalized; else (cross/√(2(1+d)),
  √(2(1+d))/2).
* **Two-body solve** (0x48A000; one-body 0x48A910 is the same with one side fixed): r = target − world CG per body;
  μ = mA·mB/(mA+mB) (an immobile side: the other's mass); for i in 0..31: rel = (vA + ωA×rA) − (vB + ωB×rB); J = rel·μ;
  stop when |J| = 0; i = 0 only records rel/J (COLLISION_INFO); i ≥ 1 applies −J·i/32 to A and +J·i/32 to B through
  0x486DA0 (dv = J/m, dω = I_world⁻¹(r × J)), re-measuring rel each step; the accumulated dv/dω go onto the bodies.
* **Limit** (0x48C270; constraint {+4 frame quat, +0x44/+0x48 female/male side, +0x4C post offset from the male anchor,
  +0x58 max, +0x5C min (turns), +0x60 enabled, +0x64 mode 0 prismatic / 2 conical}): d = (pB + R_B·(anchorB + post)) −
  target in the female constraint frame (orientation × frame quat). Mode 0: angle = atan2(d.x, d.z) turns. Mode ≠ 0:
  the frame is first turned about its own x by −atan2(d.y, d.z) (and for mode 2 about z by atan2(d.y, d.x)) so d lies in
  its x–z plane [?: the port takes the cone angle about the perpendicular]. angle > max → 0x48B290(delta = max − angle);
  angle < −min → delta = −min − angle. Push-back, both mobile: w = mA/(mA+mB); A turned by −(1−w)·delta and B by w·delta
  about the frame's y axis through the target, each re-placed so its anchor stays on the target (0x48B190: orientation ×
  frame × Ry(2π·turns) × frame⁻¹, position = target − R·anchor); then Moment::React(moB, plane{target, normal = the
  constraint's stored normal (+0x14 max side / +0x2C min side)}, 32). One mobile side: it takes the full delta and its yaw
  rate about the frame axis is zeroed when it points into the limit.
* **Port**: `TractorHitch.Resolve` each fixed step (on the previous step's result, i.e. after integration and PhysX's
  contact solve), `SolvePoint` (32 fractional steps; also the limit's React restricted to the normal, closing only [ours]),
  `ApplyLimit`, `RotateAboutTarget`. The PhysX ConfigurableJoint stays behind `TractorHitch.ExeSolver = false`. Hitched
  bodies ignore each other's colliders (RBTractor::CanCollideWith). The constraint frame is the constraint bound's
  quaternion; the stored normals are taken as the frame's ±x [?].

## 26. This round (RigidBody / SuspensionSpline / PhysicsInfo remainder) — what changed and what is left
* RigidBody.cpp (RB-* from the decomp walk): **TrackOBBs** (0x34191 boxes, WorldCollision kind 3) now take OBJ_FRICTION
  as ResolveWorldOBBCollision (:1168, a 1e6 kg box) does, not WALL_FRICTION. **RBVehicle::CanCollideWithGround** (:229-237)
  now needs every wheel down **and** no object collision this step (`CarContacts.RefreshGround`), was "> 2 wheels".
  **Chassis::DoSleep → RigidBody::Damp** (:1555-1562; Chassis.cpp:150-169): v, ω, force and torque × speed (0 under
  1e-6), every wheel ω 0, only without a collision this step and not staging — PlayerCar.Step and TrafficVehicle.Damp
  (was ×0.5 and v/ω only). **TrafficVehicle bodies** (traffic, cops, AI racers) now get the exe rigid body rules the
  PlayerCar had: no angular damping (NATURAL_ANGULAR_DAMPING 0, was 0.05), maxAngularVelocity 30·√3 (was 20), GRAVITY
  −9.813 (the difference to Unity's added each step), the ±300 / ±30 component bounds, the **staging hold**
  (RBVehicle::OnBeginFrame :66-87, re-taken every tick), **mDeadOnWheels / ShouldSleep** (RBVehicle :99-120, RigidBody
  :1475: a destroyed non-player on more than half its wheels for 2 s sleeps; a body with no wheel down sleeps under
  SLEEP_VELOCITY 1 with more than 2 contact points; not while animating or after an object hit; asleep = no velocity, no
  gravity, Rigidbody.Sleep until a contact wakes it). The NIS car (NisCarControl) gets GRAVITY and the bounds too.
* SuspensionSpline / EngineSpline (NisCarControl): **aero** downforce is Physics::Info::AerodynamicDownforce
  (linear, speed·2·AERO·1000; was v² without the ×2000); the pitch/roll constraint is measured against the **track's**
  up (SuspensionSpline.cpp:673; was the world up); the **initial SetNISPosition** is PVehicle::SetVehicleOnGround(track
  point with y = GetWorldHeightAtPointRigorous + 5, forward) + SetSpeed(0) (EngineSpline::MatchSpeed(0) :299-344,
  Reset :273-281); SetAnimating(true) rebuilds the behaviours (ctor values: constraint 60°, no pulses, gear FIRST, rpm 0);
  **Chassis::SetCOG(0,0)** each tick (CG z = rear + wheelbase × FRONT_WEIGHT_BIAS, 0.5 airborne); every wheel lifted with
  ModifyYPos; the ISuspension read-outs (slip, slip angle, load, ω, surface, IsNOSEngaged) reach the PlayerCar through
  SetExternalWheelState/SetExternalNos; IInput order ENGINE → SUSPENSION (the suspension's gas 1/0.5 is the tick's value).
  Still [ours]: the wheel arms at the visuals' x/z and RIDE_HEIGHT − sag (exe: ±(TRACK_WIDTH − SECTION_WIDTH·0.001)/2,
  y = −dimension.y, z = FRONT_AXLE / FRONT_AXLE − WHEEL_BASE); the untuned AERO_COEFFICIENT (tunings null in the exe).
* PhysicsInfo: added MaxInductedPower 0x66BFA0 (max TORQUE·(1+boost)·rpm/5252), Redline 0x66C560, HasNos 0x66BB80
  (TORQUE_BOOST > 0 && NOS_CAPACITY > 0), HasRunflatTires 0x66BC00 (always false on PC); Torque returns 0 with a
  one-point table. AvgInductedTorque (sub_66C760 [?]) is FE-only and not ported.
* Per file: **Articulation.cpp Ported** (solver + limits; [?] conical composite, stored normals). **RigidBody.cpp**:
  the rules above ported; still PhysX for integration/contacts: the ground point cloud / one-deepest-contact-per-tick
  (DoInstanceCollision2d :790-932, ResolveGroundCollision :721-770 — "ground" is only the road face under each corner
  and only the deepest packet reacts), Separate() (:965-1030, raw-mass split), the one-sided spheres (:1492 ONESIDED on
  every AddCollisionSphere), DoDrag (0 for cars), GetTriggerFlags (world triggers: the trigger owner), the traffic
  world-test frame skip (:259-275) — partial by design. **SuspensionSpline.cpp / EngineSpline.cpp Ported** (arms
  [ours]). **PhysicsInfo.cpp Ported** (AvgInductedTorque FE-only). **PVehicle.cpp**: §21 unchanged — behaviour
  attach/detach and model switching are glue the port does in Setup/SetAnimating; partial (the mechanic table and
  LookupBehaviorSignature are not data-driven here).

## 27. Round 9: the mechanic table, ground point clouds, barrier sides, trigger flags, the helicopter

### 27.1 PVehicle mechanic table (Runtime/Physics/VehicleMechanics.cs; PlayerCar.PVehicle.cs)
**PVehicle::LookupBehaviorSignature** (0x67F0D0, read from the exe; Ghidra had no function) decides the behaviour of each
mechanic; LoadBehaviors (0x685240) builds one per non-null signature and OnBehaviorChange (0x67EED0) re-queries the
interfaces. Order of the rules [C]:
1. AUDIO → none while DAT_008f86f8 is 0 (1 in the shipped exe).
2. A per-vehicle override (map at PVehicle +0x168 / end +0x16C) wins [?: no shipped caller seen].
3. Animating (+0x15C) and CLASS (+0x118) ≠ CHOPPER (0x92C4F4 = UCrc32("CHOPPER"), static init 0x887430): SUSPENSION →
   SuspensionSpline, ENGINE → EngineSpline; then any class: RESET → none, INPUT → InputNIS.
4. RIGIDBODY with driver REMOTE (6) → RBRemote. 5. INPUT with driver HUMAN → InputPlayerDrag (STYLE_DRAG) / InputPlayer.
6. DAMAGE with STYLE_DRAG → DamageDragster; ENGINE, CLASS CAR (0x92C52C, 0x8873F0) and STYLE_DRAG → EngineDragster.
7. SUSPENSION, CLASS CAR and driver RACER / NONE / REMOTE → SuspensionSimple.
8. AI → table 0x8F8890 (filled by 0x887610): {NONE, *, AIVehicleEmpty}, {5, *, AIVehicleEmpty}, {RACER, *,
   AIVehicleRacecar}, {TRAFFIC, *, AIVehicleTraffic}, {COP, CHOPPER, AIVehicleHelicopter}, {COP, *, AIVehicleCopCar},
   {HUMAN, *, AIVehicleHuman}, {REMOTE, *, AIVehicleHuman}; no match → AIVehicleEmpty.
9. EFFECTS for a HUMAN driver or vfunc +0x24 (IsPlayer [?]) → EffectsPlayer.
10. Otherwise the pvehicle's `BEHAVIOR_MECHANIC_<m>` StringKey (inherited; empty = none).
Data (harness `phys9 mech`): racers RBVehicle / SuspensionRacer / EngineRacer / DamageRacer / ResetCar / DrawRaceCar /
SoundRacer / EffectsCar; cops RBCop / SuspensionSimple / EngineRacer / DamageCopCar / DrawCopCar / SoundCop (no RESET);
traffic RBVehicle / SuspensionTraffic / EngineTraffic / DamageVehicle / DrawTraffic / SoundTraffic; semis RBTractor;
trailers RBTrailer / SuspensionTrailer / no ENGINE (trailercon etc.: empty DAMAGE too); copheli RBVehicle / SimpleChopper
(as ENGINE) / DamageHeli / DrawHeli / SoundHeli; cs_semi (NIS) RBTractor + racer behaviours; copmidsize_nis DrawNISCar.
**Port**: `VehicleMechanics.Lookup` is the function; `PlayerCar.BehaviorSignature(m)` / `HasBehavior(m)` evaluate it from
the car's live state (driver class from Traffic / CopCar / AiControlled, STYLE_DRAG from Dragster, the pvehicle CLASS,
NisCarControl.Animating). Data-driven now: **CarReset runs only when the car has a RESET behaviour** (racers; cops,
traffic, trailers and an animating car have none — it used to run on every car, so a flipped cop was reset after 4 s,
which the exe never does: DamageCopCar's flip check destroys it instead, traffic_physics.md §12), and
**VehicleSpecs.ForRacer** takes SUSPENSION / ENGINE / DAMAGE / RIGIDBODY from the lookup (was hand-written overrides).
Fixed alongside: TrafficVehicle.DriverClass defaulted to 2 (COP) for traffic (now 1, DRIVER_TRAFFIC), the trailer's to 0
(HUMAN; now 4, DRIVER_NONE). Deviation [ours]: the Unity sims are fixed classes (PlayerCar = the racer behaviours,
TrafficVehicle = Chassis subclasses, NisCarControl = the Spline pair), so a signature change mid-life (SetDriverClass,
SetAnimating) is the owning code switching components, not a behaviour reload; RBRemote / InputPlayerDrag have no class.

### 27.2 Ground collision = the MeshVsGround point cloud (CollisionGeometry.cs, CollisionBounds.cs, CarContacts.cs)
**Data** [C]: after a collection's bounds array come PCloudHeader {i32 count, pad[3]}, count × PCloud {i32 verts, pad[2],
Vector4* list} and each cloud's Vector4 vertices (body frame, from the root pivot). 77 of the 83 vehicle collections
have one cloud (16 points; 14-15 on a few trucks) named by their MeshVsGround node; none has PrimVsGround boxes except
the cops (5-10) and a few semis/trailers; COPHELI and 5 semi/trailer collections have no ground primitive at all
(harness `phys9 pclouds`, `phys9 pcdump <MODEL>`). GTI: cloud lowest y −0.627 against the lower box bottom −0.639 (1.2 cm
higher); traffic sedans/vans/trucks have clouds up to 9 cm *below* their box.
**Exe** [C] (RigidBody::DoWorldCollisions :1353, DoBarrierCollision :1364-1405, DoInstanceCollision2d :790-932,
ResolveGroundCollision :721-770): PrimVsWorld primitives meet only the barrier list (and the OBB list); the road strips
are met only by the 8 corners of VSGROUND boxes and the cloud vertices: each point is a WWorldPos query with tolerance
`speedXZ·dt + max((ωx·Δz − (ωz·Δx + vy))·dt, 0) + |dim·up|` clamped to [0.25, ceiling − y], up to 16 packets, sorted, and
**only the first packet that reacts** (React(plane, 16), GROUND_FRICTION × surface, |n_body ⊙ GROUND_ELASTICITY|,
GROUND_MOMENT_SCALE) changes the velocities; then the body moves out by the deepest penetration along its normal.
**Port**: CollisionBounds.BuildVehicle builds each cloud vertex as a 1 cm SphereCollider [ours: radius] (prim
GroundOnly: meets only the road strips, WorldCollision kind 1) and flags every PrimVsWorld primitive without PrimVsGround
`NoGround` when the car has any ground primitive, so the boxes no longer touch the road strips (barriers, OBBs, standing
props and cars still); a collection with no ground primitive keeps the old box-on-road behaviour [ours]. CarContacts now
classifies a world contact by the collider it hits (road strips = ground whatever the slope, barriers / OBBs = walls; only
other static colliders by the normal), was by the normal alone. **Deviation**: PhysX solves every cloud contact of the
step together (and depenetrates through its solver) instead of the exe's single deepest reacting packet per tick plus a
positional push; the point query is a 1 cm sphere instead of the WWorldPos tolerance window. Ride height and the play
tests are unaffected: the clouds sit at or above the box bottoms on every racer (the body never touches the road while
driving).

### 27.3 Barrier sides and ONESIDED spheres (WorldCollision.BarrierContactCounts, CarContacts)
WCollisionMgr::Collide (:735-800) accepts a barrier hit only when `dot(barrierNormal, collisionNormal) > 0` for a
one-sided barrier (WSurface flag 0x10 clear) or when the primitive is ONESIDED — every sphere, since
RigidBody::AddCollisionSphere ORs 0x10 into its flags (:1496) and DoBarrierCollision passes `force_single_sided` (:1399).
So a body behind a one-sided barrier passes through it, and a car's corner/mirror spheres pass through two-sided barriers
from behind. The Unity walls are prisms (1 m behind a one-sided plane, centred on a two-sided one), which collided from
both sides; WorldCollision now keeps each walls collider's barriers on a 16 m grid and CarContacts ignores a barrier
contact whose normal (oriented toward the car's CG) points against the barrier's normal under the same rule.
Deviation: a contact point inside no barrier prism (an edge case at prism ends) is kept.

### 27.4 RigidBody remainder decided
* **Separate()** (:965-1030): overlap split by mass (`moveA = mB/(mA+mB)`), all of it on the mobile body when one side is
  immobile, smackable-vs-smackable by closing speed. PhysX resolves penetration in its solver weighted by inverse mass
  (the same linear split; immobile/kinematic = infinite mass), spread over steps and capped by maxDepenetrationVelocity;
  the smackable closing-speed split is not reproduced. Documented deviation.
* **GetTriggerFlags** [C, PC disassembly]: RigidBody 0x699F60 → 0x40 unless attached to the world (status bit 1) or asleep;
  RBVehicle 0x69AFD0 → | 0x20 | (player 4 : other 8) | (COP 0x20000). WTriggerManager::ProcessRB (0x781961-0x781979) tests
  `(fFlags >> 8) & flags` (and `(fFlags >> 8) & 1` enabled, `(fFlags >> 8) & 0x400` silencable), so the gameplay triggers'
  0x4810D01 (→ 0x4810D: bits 4 and 8) fire for every car, FlagGameplay 0xC00 being that pair. Ported:
  `WTrigger.VehicleTriggerFlags` / `WTrigger.Activates` (Core) and `PlayerCar.TriggerFlags`; Game/Triggers.cs now passes
  `car.TriggerFlags` (one-line hook; the old 0x10000 stand-in still works through Activates).
* **CanCollideWithWorld** traffic frame skip (RBVehicle :259-275, slow traffic tests the world every 2nd/4th frame):
  a CPU saving; PhysX tests every step (deviation, no gameplay effect). **DoDrag**: 0 for every car (§15.1), N/A for cars.
* **INSTANCE_COLLISIONS_3D / DoInstanceCollision3d**: 0 in every rigidbodyspecs collection → dead.

### 27.5 Wheel (Physics/Common/Wheel.cpp) — kept as a documented deviation
Wheel::UpdatePosition [C]: world arm = R·localArm, point velocity v + ω×(arm − cog), a WWorldPos (tolerance
`max(−v.y·dt, 0) + radius + |v_xz|·dt`, capped at body height/2; cache while on the ground) on the body's WCollider gives
the face normal and penetration; SuspensionRacer then takes `compression = RIDE_HEIGHT·upness + penetration` (:1892).
PlayerCar.Probe keeps its raycast along −up against the same WCollisionPack triangles (WorldCollision MeshColliders): on
a planar face the two give the same compression; on a slope the exe measures the penetration along the face normal and
scales the ride height by upness, the raycast measures along the body's up; the WWorldPos tolerance window (a face beyond
it is "no ground") is the ray length travel + 0.3 + radius here; and the rays also find other cars ([ours], §13.10).
Traffic, cops and AI racers (Chassis) now use the exe's WWorldPos (traffic_physics.md §12).

### 27.6 Other files of this round
* **Physics::Upgrades::MatchPeformance** (0x67C310; `PhysicsInfo.MatchPerformance`): Ramp(target, stock, max) per rating,
  per type tires/chassis (h+t)/2, brakes h, transmission/engine (t+a)/2, induction/nos a; level Ceil(N·t), part at t.
  GRacerInfo::CreateVehicle (0x5E8E60) passes Performance {1, 1, 1} with flags 9 for every race opponent, so the exe's AI
  racers run every part at its top level; RaceManager.SpawnOpponent (gameplay agent) still gives round(3 ×
  MinimumAIPerformance) [hook]. PVehicle::Construct flags: 4 removes the NOS part and its Junkman (0x67C220/0x678530), 0x10
  gives NOS level 1 to a car with none installed [?: no caller seen with them]. ApplyPreset (0x67C080) is Shop.cs'
  presetride path; GetPercent (0x677F80) = PerformanceRatings.Percent; SetMaximum / Get/SetPackage = OwnedCar levels.
* **PhysicsTunings.cpp**: LowerLimit / UpperLimit (0x66CA10 / 0x66CA20, −1..+1) are the file's only functions; the rest of
  its range (0x66CA30-0x66DD22) is compiler-generated Attrib/COM stubs and nullsubs.
* **SoundCar**: `PlayerCar.ToleratedSlip` (Chassis::ComputeMaxSlip, Chassis.cpp:70-76) and `PlayerCar.SoundWheelSlip(i)`
  (slip moved toward 0 by 0.2 × tolerated, Tweak_AudioSlipToleranceScale) for the audio code [hook]; the rest of the
  packet (rpm % of IDLE..RED_LINE, gear, NOS, controls, control source, health, slip angle, terrain, load) is read from
  PlayerCar by Audio/CarSound.
* **IsShiftingGear** (EngineRacer.cpp:188-190, `mGearShiftTimer > 0`): `PlayerCar.IsShifting` (the racer sim's shift
  timer; on TrafficVehicle the EngineRacer / EngineTraffic timer), read by Audio/CarSound's SFXCTL_HybridMotor
  (EAX_CarState::IsShifting +0x1EC).
* **DrawCar / DrawVehicle**: the packet (flashing on reset invulnerability, lights, extra body roll/pitch from RENDER_MOTION
  unless articulated, zone damage, hidden parts, wheel slip/skid/compression/steer, blow-outs, NIS anim pitch/roll/shake;
  DrawPerformanceCar gear/shift/NOS and engine power/speed in 1st/neutral or staging) is CarRender/CarRenderInfo +
  CarRenderConn; DrawVehicle::Part / SpawnModel / KILL_OFF_SCREEN / PlayEffect are CarRender/CarFragments (61-part cap; the
  IModel instance cap 434 is not enforced there [render agent]).
* **Effects** (EffectsVehicle/Car/Player/Smackable/Fragment): Effects/CarEffects.cs covers DoHit (squared speed > 1,
  Ramp(impact)), DoScrape (0.2 s time-out, intensity clamp 0.1..1), EffectsCar bottom-out/scrape with > 2 wheels down,
  EffectsSmackable OnDetached.
* **DamageVehicle / DamageRacer / DamageHeli**: §17 plus DamageRacer's Puncture → BLOWN after 0.5 s (180 s run-flat, never
  on PC: HasRunflatTires false) in Police/Roadblock.cs TireState feeding PlayerCar.BlownTires (brake 1, no e-brake,
  traction ×0.3: SuspensionRacer.cpp:1665); DamageHeli is DamageVehicle with an empty DoAutoDestruct.
* **RBCop** (0x6B8540-0x6B97F0): both ModifyCollision overrides call RBVehicle's (vtable 0x8AD008) — CarContacts applies
  the RBVehicle rules to cops (§22).

### 27.7 The helicopter (Runtime/Game/Police/Helicopter.cs)
* **SimpleChopper::OnTaskSimulate** [C, 0x6A2430]: the body position is offset by `R·(0, −dimension.y, 0)` each tick
  (mLastBodyOffset: it swings about a point dim.y above the body); desired velocity capped at chopperspecs MAX_SPEED_MPS;
  `maxAccel = min(0.6·speed + 30, 80)` (80 with MaxDeceleration; DAT_008f8dcc/dd0/dd4 = 80/30/2); `a = Δv·2`, scaled to
  maxAccel when `|Δv|·2 > 1` and (over maxAccel or MaxDeceleration); force `m·a − m·GRAVITY·up` cancels the rigid body's
  gravity. Angular velocity is **set**: pitch/roll from SetTorqueToMatchPitchAndRoll on the measured acceleration (dt >
  0.005), yaw `−8·(ideal × actual).y` clamped ±1.3 toward the flattened desired facing, then `(ω + 7·ω_last)/8`.
* **SetTorqueToMatchPitchAndRoll** [C, 0x6A2030]: local accel `(4·mLastAccelVector + a)·0.2`, mLastAccelVector never
  written after the ctor clears it (so 0.2·a); pitch `−a.z·9/80·PITCH_ANG` (+0.035·a.y climbing above 15 m/s), roll
  `−a.x·12/80·ROLL_ANG`, both ±0.1 turn; ideal forward/right get `y = sin(2π·angle)`; local rate `−(ideal × actual)·ALIGN`,
  ±1. **chopperspecs** (class 0x5D898EE7): copheli names collection EEC2271A, which does not exist, so BehaviorSpecsPtr
  falls back to "default" (AttributeStructPtr: Change(key_default)): PITCH_ANG 0.05, PITCH_ALIGN_SCALE 6, ROLL_ANG 0.06,
  ROLL_ALIGN_SCALE 4.5, MAX_SPEED_MPS 100 (harness `phys9 chopper`).
* **Body**: COPHELI's collision root (half 0.942 × 1.036 × 2.83, pivot (0.013, 1.043, −0.725), no child primitives) is
  the box; the transform is the body origin and the model hangs at −pivot (HeliRenderConn::Update 0x739420:
  geom = body − R·GetModelOffset(), the offset taken as the root pivot [?]). Previously a 2.6 × 2.6 × 11 box on the model origin, so the AI's heights (all taken
  from the body position in the exe) were 1.04 m low.
* **DrawHeli::OnTask** [C, 0x693990] (task "HeliWash", 1 s): on a valid WWorldPos face, effect "heliwash" on the ground
  below with magnitude `(1 − Ramp(altitude, 5, 30))·groundNormal`, paused otherwise; run like a WorldEffectConn
  (EmitterQuadratic of |magnitude|, VisualCullDist). The render packet's shadow scale (`1 − (y − ground − 5)·0.025`,
  AIVehicleHelicopter::Update) / dust storm (always 0) are not passed: CarShadow is the render agent's.
* **SoundHeli** only opens the EAXSound connection (Pkt_Heli_Open) and has empty Reset/OnTaskSimulate/OnService:
  Audio/WorldAudio's SFXCTL_Helicopter attach is that connection. **DamageHeli** = DamageVehicle (empty DoAutoDestruct).
* [ours] kept: the ground clamp (body ≥ ground + 1.5 m + pivot.y; the exe's heli collides through its RBVehicle with
  WORLD_MOMENT_SCALE 1e6), the kinematic body.

## 28. Smackables (round 9)
Runtime/Smackable.cs; harness `smack9` (rigidbodyspecs per smackable), `physcore rb`. All [C] unless marked.
* **Creation** (sub_681540): no track collection for the spawner's Collision key → no smackable (the prop stays a plain
  model, no collider). `Make` now returns null then, once CollisionBounds.TrackLoaded (before that it builds the prop
  [ours]). The box is CollisionBounds.SceneryBox when present (render bounds otherwise) — already in place.
* **SmackableTrigger** (ctor 0x667E70, Move 0x668070, Enable/Disable 0x667FC0/0x667FB0 = fFlags |=/&= ~0x100): a WTrigger
  box with WTrigger-ctor flags `0x10 | (0x40143 << 8)` (both callers 0x6728E5 / 0x6A3AA5 pass extra 0). ProcessRB tests
  `(fFlags >> 8) & GetTriggerFlags()`, so any awake, unattached rigid body (bit 0x40) fires it: cars, loose props
  (prop-on-prop chains), not a simplified prop (SimpleRigidBody CAN_HIT_TRIGGER 0x100 is never set). The port keeps
  the contact stand-in (an awake, non-kinematic Rigidbody touching the static box) [deviation: the exe tests the body's
  box a frame ahead against the trigger box, not a PhysX contact].
* **GetTriggerFlags of props** (SimpleRigidBody / RigidBody 0x699F60): 0x40 (+4 player-owned) — no bit of the gameplay
  triggers' 0x4810D01 >> 8, so props never fire GTriggers: nothing to feed the trigger manager (dead for gameplay).
* **rigidbodyspecs** (smackable RefSpec, default when absent): 12 collections; "smackable" (156 props) GRAVITY −13,
  DRAG (2,2,2), DRAG_ANGULAR (1,1,1), GROUND_FRICTION 0.3/0.15, SLEEP_VELOCITY 2; cars 4DEF9A87 −20; watertower −36,
  sailboat/023A0371 −40, radiotower −26, 2E44C2E3 −30; IMMOBILE_OBJECT_COLLISIONS on watertower, radiotower, sailboat,
  E2402805, 0D598302; NO_WORLD_COLLISIONS on E2402805, 0D598302, 493A8A87. Ported:
  - gravity = GRAVITY for RBSmackable (RigidBody::OnBeginFrame :516-522) and for the simplified body (Smackable
    OnTaskSimulate 0x684F10: ResolveForce((0, GRAVITY·m, 0)) on the ISimpleBody) — was Unity's −9.81;
  - DoDrag (0x6972D0, 1.225 checked in the exe; :538-570): F = −½ρ|v|(v ⊙ 4dim² ⊙ DRAG), τ = −½ρ|ω|(ω ⊙ cs ⊙ DRAG_ANGULAR),
    cs_i = 4·dim_i·√(dim_j² + dim_k²), body axes, half-extent dims; RBSmackable only;
  - RBSmackable::ShouldSleep (0x66A310: not while joined → RigidBody::ShouldSleep :1475): |v| + |ω|·radius < 2 with > 2
    contact points (counted from OnCollisionEnter/Stay) → asleep (Rigidbody.Sleep, nothing applied until woken);
  - collision switches through a Smackable ContactModifyEvent: NO_WORLD (barriers/OBBs ignored), NO_GROUND, NO_OBJ,
    IMMOBILE_OBJECT_COLLISIONS (RigidBody::ModifyCollision :947-952: inverse mass/inertia 0 against a non-immobile body);
  - GROUND_FRICTION as the prop's PhysicsMaterial with minimum combine [deviation: one material, so walls use the ground
    pair instead of WALL_FRICTION 0.3/0.2; cars set object friction per contact in CarContacts];
  - no damping: NATURAL_ANGULAR_DAMPING (0) replaces Unity's 0.05.
* **SimpleRigidBody** (simplified prop): Smackable::OnBehaviorChange 0x684DF0 ModifyFlags(0, 0x20B) = no SRB/RB collision,
  position + orientation integration; ApplyFriction (flag 4: v×0.99, ω×0.98) is NOT enabled → the port's old damping is
  removed. DoSRBCollisions / DoRBCollisions only fill SimCollisionMaps for bodies with the collide bits clear — a
  simplified smackable has both set, so they are dead for props (the SpikeStrip, 0x8001, is the other SRB user).
* **RBSmackable CanCollideWithWorld** (0x66A3C0): not while IsSimple(owner vfunc 0x5C); slow bodies test the world only
  every 4th frame (|v|² < 4) or 2nd (< 225) via the +0x144 frame counter — a CPU saving; PhysX tests every step
  [deviation, documented]. CanCollideWith (0x66A360): not with a body it is joined to (Articulation; props have no
  joints in the port).
