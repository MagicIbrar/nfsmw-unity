# Car rendering, visual damage and customization visuals (NFS MW 2005 PC v1.3)

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/` (paths relative to it), speed.exe v1.3 (addresses below; IDA
export `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`), `fx\car.efx` disassembled from the exe resource `IDI_CAR_FX`
(file offset 0x5412F8, 0xC254 bytes; `scratchpad/fx/fxdis.py` walks the shader tokens and calls d3dcompiler_47's
`D3DDisassemble`), game data read with the harness mode `carrender` (`NFSMWStaging/Harness/ProbeCarRender.cs`).
`[?]` = not confirmed; `[ours]` = a port decision with no exe counterpart. When the GameCube decomp and the PC exe
differ, the exe wins (each case is flagged).

| tag | file |
|---|---|
| CR | `World/CarRender.cpp` (CarRenderInfo) |
| CI | `World/CarInfo.cpp` (RideInfo, GetUsedCarTextureInfo) |
| CS | `World/CarSkin.cpp` (skin / wheel compositing) |
| VPD | `World/VehiclePartDamage.cpp` (cop car part damage) |
| VFC | `World/VehicleFragmentConn.cpp`, `Physics/Behaviors/DrawVehicle.cpp` (parts that come off) |
| DV | `Physics/Behaviors/DamageVehicle.cpp` (+ DamageRacer, DamageCopCar) |

---

## 1. Parts and LODs

* A car is `CarRenderInfo::mCarPartModels[slot][model][lod]` (CR:1698 UpdateCarParts, exe 0x755800): for every
  model slot (CARSLOTID 0..75, `World/CarPartID.h`) the RideInfo part's `GetModelNameHash(model, lod)` (CarPartDb
  `ModelHash`), LODs `mMinLodLevel..mMaxLodLevel` (A..E). Special LOD ranges (CI:869): decal mounts are A only;
  interior/driver C..E (B..E in the front end; A only with `mSpecialLODBehavior` 2).
* `RideInfo::SetPart(BODY)` (CI:1327) also sets the kit's `DAMAGE0_FRONT/REAR/FRONTLEFT/FRONTRIGHT/REARLEFT/
  REARRIGHT` parts and the kit's door / quarter decal mounts `<CAR>_KITnn_DECAL_{LEFT,RIGHT}_{DOOR,QUARTER}_RECT_MEDIUM`.
  The window decal mounts are the slots' stock parts (`<CAR>_DECAL_FRONT_WINDOW_WIDE_MEDIUM`).
* `SetStockParts` (CI:928): every slot gets its level-0 part, BASE_PAINT the CarTypeInfo `DefaultBasePaint` (a part name
  hash, e.g. GTI → GLOSS_L1_COLOR40, traffic → TRAFFIC_L1_COLOR01, cops → COP_L1_COLOR01), vinyl colours
  VINYL_L1_COLOR01 / L1_COLOR03 / L2_COLOR11 / L1_COLOR01; no vinyl, no decal textures.
* Body LOD by projected size (CarRenderInfo::Render 0x74E160, CR:2803-2947): `pixel = radius·H / (distance − radius)`
  (`eView::GetPixelSize`, radius `mRadius` 3.0), LOD index = number of thresholds above the size in
  `CarBodyLodSwapSize` {120, 25, 20, 10, 0} (0x8B2EBC) or for traffic **{80, 40, 20, 10, 0}** (0x8B2ED0; the GC decomp
  has 20/10/4), ×0.7 in split screen, clamped to mMinLodLevel..mMaxLodLevel. Cop / traffic cars are skinless
  (CarTypeInfo `Skinnable` +0xC7 = 0, `UsageType` +0x94: 0 racer, 1 cop, 2 traffic).
* **PC RideInfo::Init (0x739A70) differs from the GC decomp** (CI:802, which pins the player to LOD B and AI/traffic to
  C..E): on PC every usage gets mMinLodLevel = option max − option chosen ([0x901880] − [0x9017F8], 0 = A at full
  detail), mMaxLodLevel E, FE A..E, MaxLicense B, MinShadow C, MaxShadow B, **MaxTire D, MaxBrake B**, MaxSpoiler /
  MaxRoofScoop E, MinReflection C; NIS cars A..A (special behaviour 2). Brakes are drawn up to MaxBrake, tyres up to
  MaxTire (Render CR:3398, 3617).

## 2. Replacement textures (CR:1191-1290, 2003-2190; CI:1686-1830)

`MasterReplacementTextureTable` maps the names the meshes use to this car's textures (first match wins):

| old name (mesh) | new texture |
|---|---|
| `<CAR>_SKIN1`, `GLOBAL_SKIN1`, `SPOILER_SKIN1`, `ROOF_SKIN`, CARBONSKIN 0xA7366AE6, GLOBALCARBONSKIN 0x3C84D757 | the composite skin `DUMMY_SKINn` (skinnable cars), else `<CAR>_SKIN1` itself |
| `<CAR>_SKIN1B` | ReplaceSkinBHash = 0 [?: treated as the skin] |
| `_WHEEL` + rim TEXTURE_NAME | the composite `DUMMY_WHEELn` |
| `<CAR>_TIRE` | `TRAFFIC_TIRE` on traffic cars |
| WINDOW_FRONT/REAR/LEFT_FRONT/LEFT_REAR/RIGHT_FRONT/RIGHT_REAR | WINDOW_FRONT, or WINDOW_DAMAGE0 when damaged |
| REAR_DEFROSTER | REAR_DEFROSTER, or WINDOW_DAMAGE0 |
| HEADLIGHT_LEFT/RIGHT (0xA7E6EA53/0xA532FC46) | `<base>_ON` (always: `lights_always_on`) |
| HEADLIGHT_GLASS_LEFT/RIGHT | `<base>_GLASS_ON` — **PC 0x737E30 keeps this; the GC decomp's "glass = WINDOW_FRONT" override is absent** |
| BRAKELIGHT_LEFT/RIGHT/CENTRE | `<base>_ON` if that light is on and not broken, else `_OFF` |
| BRAKELIGHT_GLASS_LEFT/RIGHT/CENTRE | `<base>_GLASS_ON` / `_GLASS_OFF` likewise |
| the 26 `*_DECAL` names (CR:383) | DEFAULTALPHA |
| decal mounts: DUMMY_DECAL1..6, DUMMY_NUMBER_LEFT/RIGHT | the decal texture (§7) or DEFAULTALPHA |

Light textures: `<base>` = the HEADLIGHT/BRAKELIGHT slot part's TEXTURE_NAME, else `<CAR>_KIT00_HEADLIGHT` /
`_BRAKELIGHT`; names `bStringHash("_ON", base)` etc. (`_DAMAGE0` exists in the table but is never selected; reverse
lights have no texture). Brake state bits in CarRenderInfo +0x1620 (on) / +0x1624 (broken): 8 left, 0x10 right, 0x20
centre (VehicleFX); `ForceBrakelightsOn` (0x9B0930) forces left and right.

The **carbon table** (hood with CARBONFIBRE ≠ 0): `<CAR>_SKIN1`, CARBONSKIN and GLOBALCARBONSKIN → `CARBONFIBRE`,
SKIN1B and GLOBAL_SKIN1 → the skin.

## 3. Light materials

* Chunk 0x00135200 in GLOBAL\GLOBALB (156 on PC): eLightMaterial {NameHash @0xC, Version 3 @0x10, Name[28] @0x14,
  30 floats @0x30: DiffuseMin{Scale,RGB}, DiffuseMax{Scale,RGB}, DiffuseMinA, DiffuseMaxA, SpecularPower,
  SpecularMin{Scale,RGB}, SpecularMax{Scale,RGB}, EnvmapPower, EnvmapMin{Scale,RGB}, EnvmapMax{Scale,RGB},
  MetallicScale, SpecularHotSpot}. Loader 0x501A10; the one named DEFAULT (0x2C420D64) becomes the default for unknown
  names (elGetLightMaterial 0x502820). Each solid has a light-material table (chunk 0x134013, 8 bytes per entry) and
  each material entry (0x134B02, 0x68 bytes) indexes it with byte +0x1D (texture slots are +0x18..+0x1C).
* PC alpha overrides (`eLightMaterialPlatInterface::UpdatePlatInfo` 0x6BE1B0): DiffuseMinA/MaxA = 0.3/1.0 for
  WINDSHIELD, WINDOWMASK, MOLDINGS, TINT_L1_BLACK/GREEN; 0.4/1.0 for TINT_L2_BLACK/GREEN/RED/BLUE; 0.5/1.0 for the L3
  tints listed in the code; 0.4/0.8 for CLEARPLASTIC and HEADLIGHTGLASS.
* Effect constants (sub_6C76E0, shader path): DiffuseMin = MinScale·MinRGB (a = MinA); DiffuseRange = MaxScale·MaxRGB −
  DiffuseMin (a = (MaxA − MinA)·0.25); Specular{Min,Range} the same with a factor `*(DAT_009b392c+0x60)` [?, = 1];
  Envmap{Min,Range} the same (×0.5 only on the fixed-function path); SpecularPower, EnvmapPower ×1.
* ReplaceLightMaterial (Render, CR:3064-3230): CARSKIN → BASE_PAINT's LIGHT_MATERIAL_NAME (CARBONFIBER on a carbon
  hood; spoiler / roof use the same since `LightMaterial_Spoiler/Roof` are always null); WINDSHIELD → WINDOW_TINT's
  material (stock tint = WINDSHIELD); MAGSILVER/MAGCHROME/MAGGUNMETAL on the wheels → PAINT_RIM's material when the rim
  is an upgrade (level ≠ 0); CALIPER → none in MW. A null replacement leaves the table entry (0x501640).

## 4. fx\car.efx (12 shaders; technique `car`)

Vertex shader (shaders 1/3/5/7/9 identical, 11 = the ps_1_1 variant), constants c18-c25 as above,
`LocalDirectionMatrix` rows = 3 light directions (object space, toward the light), `LocalColourMatrix` columns = their
colours:
```
V = normalize(eye − pos); NdV = max(N·V, 0.01); ao = saturate(2·vertexcolour.red)
lights = Σᵢ saturate(N·Lᵢ + 0.1)·Cᵢ
oD0.rgb = ao · lights · (DiffuseMin + NdV·DiffuseRange).rgb ; oD0.a = (DiffuseMin + NdV·DiffuseRange).a
oD1.rgb = ao · (SpecularMin + SpecularRange·NdV^SpecularPower)      (lit())
oD1.a   = ao · (EnvmapMin.x + EnvmapRange.x·NdV^EnvmapPower)
oT0 = reflect vector (2·NdV·N − V) in view space (cube lookup); oT3 = L0; oT4 = N; oT5 = V; oT2 = shadow transform
```
(vs_1_1 colour outputs saturate.) Pixel shader (shader 8 = ps_2_0; 0/4/6 are the 16-/9-/4-tap shadow variants, 10 the
ps_1_1 one with a Blinn half vector):
```
h  = 2·saturate(reflect(L0,N)·V)^16 ; highlight = h · oD0.a · oD1.rgb
s  = lerp(0.2 + InvShadowStrength, 1, lit)                    (shadow map + shadow colour map test)
rgb = (oD0.rgb·tex.rgb + highlight·s)·s + envcube(R)·oD1.a·oD0.a·0.5 ; a = tex.a·oD0.a
```
No fog (the car effect has no FOGON / oFog). The three lights are **ShaperLightsCarsInGame** (0x8F2E70), all
LIGHT_SUN_DIRECTION: (Theta, Phi, colour·Scale) = (0, 350, 0.85/0.9/1·0.83), (50, 220, 0.55/0.6/1·0.4),
(310, 220, 0.65/0.7/1·0.45). elSetupLights (0x502130) turns the sun vector into bAngle azimuth = atan2(y, x) and polar =
90° − asin(z) (FUN_004fc3c0), adds Theta and Phi + 90, and rebuilds (sin b·cos a, sin b·sin a, cos b) with a = θ + 180,
b = 90 − φ (FUN_004fc320). Result: light 0 = the sun raised 10°, lights 1/2 = sun azimuth ∓130°, polar 140° − sun polar
(below the horizon for a high sun). The env cube is one eEnvMap around the player car (UpdateEnvironmentMapCameras
0x756060: the camera anchor's car + EnvMapEyeOffset (0, 0, 0.95) @0x9053B0).

## 5. Light states

UpdateLightStateTextures (0x737E30): headlights and their glass always ON; brake lamps ON while braking and not broken.
Flares: `RenderFlaresOnCar` (0x742950, LightFlares.cs). Broken lights come from `IDamageable::IsLightDamaged`
(DamageVehicle: all lights when destroyed; individual lights are broken only by the `EDamageLights` events of a vehicle event sequencer: in MW that is the cops' light
bar (LIGHT_COPS, §11); racers and traffic have no engine).

## 6. Skin and wheel compositing (CS; PC CompositeSkin 0x748AD0, CompositeSkin32 0x73B010, CompositeWheel32 0x73B640)

* Skinnable cars draw a 512×512 A8R8G8B8 `DUMMY_SKINn`: filled with the BASE_PAINT colour (RED/GREEN/BLUE, alpha =
  GLOSS) then one vinyl layer: `<CAR>_<TEXTURE>` and `<CAR>_<TEXTURE>_MASK` from `CARS\<CAR>\VINYLS.BIN` (P8). Per
  pixel m = mask green: 0 → keep, < 0x80 → blend weight m/128 (GetBlendColour 0x73AE60, per-term truncation, alpha
  summed), ≥ 0x80 → replace. A REMAP vinyl first maps its pixel to R·c0 + G·c1 + B·c2 of VINYL_COLOUR0_0..2 (alpha =
  max term; an empty colour slot j is 0xFF << 8j; colours swizzled RGBA→BGRA by 0x6BEB80). NUMCOLOURS 0 aborts.
  The four FE swatch pixels are rewritten with the swatch colours (FE only) [ours: skipped].
* `DUMMY_WHEELn` = `<rim TEXTURE_NAME>_WHEEL` blended with itself × PAINT_RIM colour (ScaleColours) by
  `_WHEEL_INNER_MASK` red/255, alpha of the source (white when no rim paint: no change).
* Composites are cached by their parameters (skin composite cache CS:84-118).

## 7. Decals and numbers (UpdateDecalTextures CR:2029, exe 0x74DA80)

Six mounts (CARSLOTID_DECAL_FRONT_WINDOW 70 .. RIGHT_QUARTER 75), each with 8 texture slots `83 + 8·mount + j`
(j 0..5 = DUMMY_DECAL1..6, 6/7 = DUMMY_NUMBER_LEFT/RIGHT). A mount part needs SIZE and SHAPE attributes (SHAPE =
SQUARE/RECT/WIDE hash) and a HOOD part must be set. Texture = `bStringHash("_" + SHAPE, part.NAME)` (e.g.
`5ZIGEN_RECT`, `NUMBER_LEFT_5_RECT`, in CARS\TEXTURES.BIN). Decal material light material DECAL.

## 8. Window tint

The WINDOW_TINT part's LIGHT_MATERIAL_NAME (WINDSHIELD_TINT_L1..L3_*) replaces WINDSHIELD on the glass: its
DiffuseMin/Max colour multiplies WINDOW_FRONT and its DiffuseMin/MaxA (PC overrides) sets the opacity.

## 9. Visual damage

### 9.1 Zones and levels (DV:150-260, exe OnImpact 0x69C030, OnCollision 0x6A85B0)
* `DamageZone::Info` packs 10 zones × 3 bits (DZ_FRONT 0, REAR, LEFT, RIGHT, LFRONT, RFRONT, LREAR, RREAR, TOP,
  BOTTOM). Collisions with |closing velocity| < 1 m/s are ignored; force = impulse (Δv·mass), doubled against objects.
* Zone (car space x right, y up, z forward; dim = rigid body half extents; 0.5 / 0.8 thresholds): arm.z > ½dim.z →
  RFRONT if arm.x > ½dim.x and nₓ < −0.8, LFRONT if arm.x < −½dim.x and nₓ > 0.8, else FRONT; same at the rear; else
  TOP if arm.y > ½dim.y and n_y < −0.8, BOTTOM mirrored, else RIGHT / LEFT by arm.x.
* level = min(2, (int)(force / (FORCE·1000) · VisualScale)) raises the zone (only upward, while ≤ 5); hit points:
  points = ratio·HitPointScale / HIT_POINTS, counted if > HP_THRESHOLD / HIT_POINTS; ≥ 1 = destroyed (all zones 6).
  SUPPRESS_DIST > 0 skips cars farther from the camera; smackables with NO_CAR_EFFECT are ignored; DamageCopCar counts
  smackables only if immobile or > 2000 kg. DamageRacer: visuals only with the gameplay option (FEDatabase settings
  +0x3A, 0x68CD40).
* damagespecs (attributes.bin class 0xC1F0B434, pvehicle field `damagespecs`): FORCE 20 everywhere; racers VisualScale
  FRONT 0.8, REAR 1.5, LEFT/LFRONT/TOP 3.5 (hit points 0 = never destroyed); cops HIT_POINTS 5 (copghost 6, copgto 7,
  copsuv 10…), HP_THRESHOLD 0.75–2, SUPPRESS_DIST 35; traffic FRONT 1, others 5.

### 9.2 Racers and traffic: SetPlayerDamage (CR:1586, exe 0x742680)
DAMAGE0_* overlays (one material, global texture DAMAGE0 512×128, light material HOSES) shown for zones FRONT, REAR,
LFRONT, RFRONT, LREAR, RREAR at level > 0; glass entries WINDOW_FRONT (FRONT), WINDOW_REAR (REAR), WINDOW_LEFT_FRONT
(LFRONT), … and REAR_DEFROSTER (REAR) switch to WINDOW_DAMAGE0 at level > 1. Every window mesh is textured
WINDOW_FRONT (the side entries match nothing), so in practice a heavy front hit cracks all WINDOW_FRONT glass and a
heavy rear hit the rear window. DZ_LEFT/RIGHT/TOP/BOTTOM have no visual.

### 9.3 Cops: VehiclePartDamageBehaviour (VPD; ctor 0x75F0A0, created for COPMIDSIZE/SUV/SPORT/GHOST/GTO/SUVL/...)
* The cop models' parts are DAMAGE slots 1..22 (`<CAR>_KIT00_DAMAGE0_<SLOT>`); each slot's next CarPartDB part
  (DAMAGE_LEVEL 1) is `…_DAMAGE1_<SLOT>` (dented panels, doors without glass, brake lamps without bulb).
  MaximumDamageLevel 1: any zone level ≥ 1 swaps every slot of that zone (mSlotZoneMapList, VPD:13-134: FRONT → front
  bumper, bushguard, body, hood, cop lights, all lamps, all windows; REAR → trunk, rear bumper, body, lamps, windows;
  LEFT/RIGHT → doors, windows, cop lights, lamps; corners → hood/trunk, body, lamps, windows; TOP → nearly everything).
* ManageGlassDamage: a damaged window slot (1, 17-20) sets its entry and REAR_DEFROSTER to WINDOW_DAMAGE0.
* A damaged trunk (15) / hood (10) swings on a damped spring (CalcPartRotation: spring 2.4, damper 0.2, inertia
  80 / 130, max 10°, bounce ×−0.2 at 0 and ×−0.5 at max; pushed by acceleration x, vertical speed and yaw rate — the
  exe uses world·world⁻¹ = identity, i.e. world-frame values), hinges at pivot (1.73, −0.96, 0) / (−1.13, −0.86, 0). The exact hinge line, the shared spring state and the fragments that follow are in §11 and §13.

## 10. The Unity port

| file | what |
|---|---|
| `Core/CarRenderData.cs` | LightMaterial(s) + PC overrides + effect constants; CarTypeRenderInfo; SkinComposite (skin / wheel compositor); DamageZone (packing, Classify, levels); VehiclePartDamageMap (slot map, glass, hood spring); ShaperRig (CarsInGame lights); CarRenderTables (LOD sizes, decal names, overlay and glass tables) |
| `Core/SolidList.cs` (additive) | `Solid.LightMaterialHashes` (0x134013), `SolidPart.LightMaterialHash` (+0x1D) |
| `Shaders/Car.shader` | the car.efx vs_1_1 + ps_2_0 maths (above); blend / zwrite / cull properties for glass, decals, overlays |
| `Runtime/CarRender/CarSurfaces.cs` | light-material library, material creation, composite caches, CarLook (the ride's parts) |
| `Runtime/CarRender/CarRenderInfo.cs` | per-car replacement state: skin/carbon/wheel/light/glass/decal textures, light-material replacement, brake light states, SetDamageInfo (SetPlayerDamage / cop part damage + hood/trunk spring), Restyle for the shop preview |
| `Runtime/CarRender/CarDamage.cs` | DamageVehicle::OnImpact on Unity collisions, damagespecs, hit points; round 2: `ZoneLevelRaised(zone, old, new)`, `DamageReset`, `LastImpactVelocity` for the sequencer |
| `Runtime/CarRender/CarEnvironment.cs` | shaper-light globals, the env cube (one face per frame); round 2: InvShadowStrength (tunnel fade + 1 − shadow strength) and the TOD CarSpecScale as `_MW_CarInvShadow` / `_MW_CarSpecScale` / `_MW_CarTodSet` (§12) |
| `Runtime/CarRender/CarFragments.cs` (round 2) | the pvehicle's event sequencer ('CopCar'), DAMAGE_n / RESET_DAMAGE stimuli, ESpawnFragment fragments (rigid bodies, door hinges, KILL_OFF_SCREEN, 61-part cap), EKillJoint, EDamageLights, EPlayObjectEffect, EFireEventList, EDestroyVehicle, `Explosion()` hook (§11) |
| `Runtime/CarBuilder.cs` | builds every part through CarRenderInfo; DAMAGE0 overlays, decal mounts, cop damage slots, CarDamage |
| `Runtime/UI/CarShowroom.cs` (2 lines), `Runtime/LightFlares.cs` (3 lines) | shop preview restyle; no flare on a broken lamp |

### Deviations / unknowns
1. LODs: the body parts, DAMAGE0 overlays and cop damage parts swap A..E by the projected size (CarRenderInfo.UpdateLod);
   H = 240 / tan(fov/2) (a 480-line reference screen, as WorldStreamer's scenery LOD) [?]; missing LOD models draw
   nothing (exe); new LOD models are built lazily, 6 per frame, keeping the previous LOD until ready [ours]; brakes hide
   past B and tyres past D, but the tyre/rim model itself stays LOD A and the TireFace back-face trick is not ported;
   interior/driver use C..E (front end B is not distinguished) ; showroom cars (no PlayerCar) stay at A [ours].
   Meshes are shared between cars of one model (a cache of 800 solids) [ours].
2. Env cube: a hidden camera, 128², one face per frame, near plane 2.5 m to keep the focus car's own body out; the
   exe's envmap view list (which objects it draws) is not decoded [?].
3. (resolved, §12) InvShadowStrength = tunnel fade + 1 − (0.1 + 0.8·shadow strength) and the specular scale = TOD
   CarSpecScale while the world lighting runs; the front end / editor keep the material's 0.3 and ×1 [ours]. The port
   assumes g_ShadowDetail > 0 (shadows on). The tunnel test is PoliceZones.CameraInTunnel (tunnel / overpass zones at the
   camera) for AmIinATunnel(player view, 1) [?].
4. Sun direction for the shaper rig = `_MW_SunDir` (VisualLighting, toward the sun); assumed the exe's +0x180 vector also
   points toward the sun [?].
5. (resolved, §11) Racers and traffic never break single lamps (no sequencer engine); cops lose the light bar's
   COPRED/BLUE/WHITE through EDamageLights(LIGHT_COPS); destroyed cars: every light. The round-1 guess "a DAMAGE1 lamp is
   broken" is gone (exe: the dented lamp keeps its flare).
6. (resolved, §14) Traffic is drawn untinted like the exe (`_Paint` = 0); the colour the spawner passes is ignored.
7. SKIN1B (ReplaceSkinBHash 0) is drawn with the skin [?]; FE swatch pixels not written [ours].
8. (resolved, §13) Hood / trunk turn about x = −pivot.x at model z = 0 with D3DX RotationY signs (trunk +, hood −), one
   spring state per car shared by trunk, hood and rear bumper, spring constants fixed by the first call of the session.
   The angular-velocity input is −ω_unity.z (handedness) [?: sign].
9. Damage is fed from Unity OnCollisionEnter (impulse = |Collision.impulse|, arm from the centre of mass, box-collider half
   extents); the sim's COLLISION_INFO may count multi-contact events differently [?]. The gameplay "damage" option is
   `CarDamage.VisualsEnabled` (default on [?]).
10. Decal mounts are built only when a decal is installed (the game draws empty ones with DEFAULTALPHA) [ours].
11. Fragments (§11) [?]: Unity rigid bodies with the bound box and the smackable MASS (MOMENT 1.5 unused: Unity inertia
   from the box); velocity = the car's point velocity × fInheritVelocity (the exe hands mLastImpactSpeed through
   SetDynamicData); door hinge = HingeJoint on the female joint bound, vertical axis, 0..52° outward (the conical
   constraint's angle), no prismatic constraint; the fragment ignores its own car's colliders; EKillJoint breaks the
   joints of the parts that system spawned; EPlayObjectEffect at the named bound, else the car; IMPACT_n / COLLISION_*
   stimuli are not sent (CopCar has no responses to them). Trailers' engines (TrailerB / TrailerLog) are attached the
   same way when a trailer is built through CarBuilder.

### Harness probes (`carrender <sub>`)
`allcars` (damage/decal solids per car), `solids <CAR>`, `tex <CAR> [filter]`, `parts <CAR> <slot>` (CarPartDB
attributes), `lightmat [filter]`, `partlm <CAR>` (light material per part), `types` (CarTypeInfo flags), `skin <CAR>
<VINYL> <out.png>` (CompositeSkin32 test image), `refs <car>` (pvehicle refs), `math` (shaper rig, damagespecs, effect
constants, zone tests), `folder <FOLDER> <prefix>`, `texdump <NAME> <out.png>`.
Round 2 (`carrender2 <sub>`, ProbeCarRender2.cs; pass a names .txt to resolve hashes): `seq [engine]` (an L2RA event
sequencer engine with every event word resolved), `keys` (pvehicle StringKeys: EventSequencer, MODEL, …), `bounds
<MODEL>` (collision geometry nodes, joints, constraints), `smack <name|0xhash>` (any collection's fields), `bbox <CAR>
[filter]` (model-space solid bounds + markers), `sim ZONE:level t+N reset boom …` (runs 'CopCar' on the Core
EventSeqEngine and prints the events with their times).

---

## 11. Parts that come off: the vehicle event sequencer and fragments (round 2)

Sources: decomp `Physics/Behaviors/DamageVehicle.cpp`, `DrawVehicle.cpp`, `DrawCar.cpp`, `World/VehicleFragmentConn.cpp`,
`Generated/Events/E*.hpp`; exe symbols EDamageLights 0x62B5B0, EDestroyVehicle 0x62BB40, EHidePart 0x62CF90,
EKillJoint 0x62D320, EPlayObjectEffect 0x62F610, EProcessStimulus 0x62FD20, EResetSequencer 0x630620, ESpawnFragment
0x631A50, EFireEventList 0x638640 (MakeEvent callbacks), DrawVehicle::SpawnModel 0x6B0500, DrawCar::HidePart 0x6BB9F0,
VehicleRenderConn::HidePart 0x7389A0, CarRenderInfo::HideCarPart 0x737CF0 / FindCarPart 0x737CC0. Harness
`carrender2 seq CopCar`, `carrender2 keys`, `carrender2 bounds <MODEL>`, `carrender2 smack <name>`, `carrender2 sim …`
(NFSMWStaging/Harness/ProbeCarRender2.cs).

* **Who has it.** A vehicle's PhysicsObject runs the event-sequencer engine named by the pvehicle StringKey
  `EventSequencer` (DrawVehicle / Sim::Model StartSequencer). In attributes.bin only **cops, copsport, copgto, copsuv,
  copmidsize, copsuvl, copsportghost, copgtoghost, copsuvpatrol → 'CopCar'** and trailerb → TrailerB, trailerlog →
  TrailerLog have one. **Racers, the player and traffic never lose parts in MW**: their damage is the DAMAGE0 overlays
  and cracked glass of §9.2 only, and since `mLightDamage` is only set by the sequencer's EDamageLights, **a racer's or
  traffic car's lamps never break individually** — only all at once when destroyed (DamageVehicle::IsLightDamaged).
* **Stimuli.** DamageVehicle::OnImpact (DamageVehicle.cpp:220-256): the zone's system (`DamageZone::GetSystemName` =
  stringhash32 "DZ_FRONT" …) gets COLLISION (+ COLLISION_VEHICLE / _COP / _PLAYER), IMPACT_1..n (n = min(6, ratio·
  VisualScale)), and, when the visual level rises old → new, **DAMAGE_(j+1) for j in [old, new)** (table 0x905D0C:
  DAMAGE_1..7, IMPACT_1..7 at 0x905D28), all QUEUE_ALLOW. ResetDamage: ResetParts (child models released) +
  RESET_DAMAGE to the engine. The explosion handler (sub_677100) sends EXPLOSION and **0xFFCD8A63** (name unknown) to a
  vehicle's sequencer.
* **'CopCar' (L2RA.BUN pack 5, 19 systems)**. Zone systems → `EProcessStimulus(system, 'trigger')` into part systems
  (each fires once: its 'trigger' state moves to a dead end; 0xFFCD8A63 moves it there without firing):

  | zone system | on | triggers |
  |---|---|---|
  | DZ_FRONT | DAMAGE_1 | hood |
  | DZ_LFRONT / DZ_RFRONT | DAMAGE_2 | hood + left / right front door |
  | DZ_REAR | DAMAGE_2 | rear bumper + trunk |
  | DZ_LREAR / DZ_RREAR | DAMAGE_1 | left / right rear door + trunk |
  | DZ_LEFT | DAMAGE_1, then DAMAGE_2 | left front door, then cop lights |
  | DZ_RIGHT | DAMAGE_1, then DAMAGE_1 again | right front door, then cop lights (never: levels stop at 2, the 2nd step is DAMAGE_2) |
  | DZ_TOP | DAMAGE_1 | cop lights |
  | reset | RESET_DAMAGE | EResetSequencer |
  | (system 0) | 0xFFCD8A63 | EDestroyVehicle + EFireEventList of all nine part lists |

  | part system | action | events |
  |---|---|---|
  | doors (4) | 20 s | start: EPlayObjectEffect("carglass", window node, 1) + ESpawnFragment(DAMAGE_x_DOOR, door bound, "door", hinge joint, inherit 1, HidePart); end: EKillJoint |
  | hood / trunk / rear bumper | 20 s | end: ESpawnFragment(DAMAGE_HOOD / DAMAGE_TRUNK / DAMAGE_REAR_BUMPER, HOOD / TRUNK / REAR_BUMPER, "hood"/"hood"/"door", inherit 0.5, HidePart) |
  | cop lights | 1 s | start: EDamageLights(LIGHT_COPS) + ESpawnFragment(DAMAGE_COP_LIGHTS, COPLIGHTS, "coplights", inherit 0.5, HidePart) |

  So a door is torn open at once and **hangs on its hinge for 20 s, then falls off**; a hood hit makes the hood dent and
  flap on the VehiclePartDamage spring and **fly off 20 s later**; the light bar flies off at once and its flares die.
* **Event static data** (from +4): ESpawnFragment {fPartName (CarPartID name "DAMAGE_<SLOT>"), fCollisionName (bound of
  the car's collision geometry), fAttributeName (smackable collection), fJointName, fAllowDupes, fInheritVelocity,
  fHideParent, fHidePart, fAllowHiddenSpawn, fNoPhysics, fSimplePhysics}; EPlayObjectEffect {fEffect (string),
  fPosition, fIntensity, fID, fTracking, fAllowHiddenSpawn}; EDamageLights {fName = VehicleFX StringKey "LIGHT_COPS"
  0x7000}; EProcessStimulus {fSystemName, fStimulus, fQueueMode}; EFireEventList {'el' tag}.
* **Fragments.** DrawVehicle::SpawnModel: refuses past 61 live parts (Vehicle_Part_Count) or 434 models, needs the
  collision node (`mGeometry->GetChild(collisionnode)`) and the smackable attributes. DrawVehicle::Part: KILL_OFF_SCREEN
  seconds unseen → ReleaseModel. VehicleFragmentConn::UpdateModel: draws `vrc->FindPart(slot)` (the slot's model now,
  the dented DAMAGE1 one after a swap) with a copy of the car's replacement-texture table and CARSKIN light material, at
  the fragment matrix × inverse(bound orientation / pivot). The fragment smackables door / hood / coplights: MASS 50,
  KILL_OFF_SCREEN 8, MOMENT 1.5, rigidbodyspecs fragment. Door joints: a JointFemale bound on the body at the door's front
  edge (COPMIDSIZE left front: pivot (−0.861, 0.649, 1.277)) with a vertical MalePost, a ConstraintConical of 51.9°
  (q (0.007, −0.437, 0.015, 0.899)) and a ConstraintPrismatic. Bound pivots are model-space centres (sim axes x right,
  y up, z forward; model origin at the ground: root pivot (0, 0.716, 0) = half height).
* **Hiding.** DrawCar::HidePart counts per part name; Pkt_Car_Service::HidePart → CarRenderConn::UpdateParts →
  VehicleRenderConn::HidePart(slot) → CarRenderInfo::HideCarPart(slot, true). ResetParts shows them again.
* **Lights.** DrawCar::OnService: for every VehicleFX light with a marker, `IsLightDamaged(id)` → SetBrokenLightState
  (DamageVehicle: destroyed → every light; else mLightDamage bits). RenderFlaresOnCar: broken COPRED / COPBLUE zero the
  red / blue intensities (the bright red / blue flares use them too), COPWHITE the white ones; headlights still flash.

## 12. Car brightness against the world: InvShadowStrength and the specular scale (round 2)

* car.efx parameter table (sub_6C3D20, 0x28-byte entries at effect+0x44): INVSHADOWSTRENGTH is entry 53 (+0x848, handle
  +0x86C), SPECULARMIN 80, … The .efx default of InvShadowStrength is **0** (parameter block of the IDI_CAR_FX blob).
* **Setter** (sub_6D7660 and sub_6E0200, only when **g_ShadowDetail** [0x901830] > 0; registry names in sub_6C1C00:
  [0x9017B8] g_CarEnvironmentMapEnable, [0x9017BC] g_CarEnvironmentMapUpdateData, [0x9017D4] g_RoadReflectionEnable,
  [0x9017F8] g_CarLodLevel, [0x901830] g_ShadowDetail …):
  `InvShadowStrength = [0x982BA8] + 1 − [0x8FAE68]`.
  * [0x8FAE68] = 0.1 + (0.9 − 0.1)·[0x8FAE64] ([0x9018E4] 0.1, [0x9018E8] 0.9), [0x8FAE64] = clamp(sunZ − 0.14, 0,
    0.537)/0.537 · (1 − cloud) (sub_6C83D0: TOD +0x188, Rain +0x290; sky_lighting.md §4, TimeOfDay.ShadowStrength).
  * [0x982BA8] (eDisplayFrame 0x6DE354): `AmIinATunnel(player view, 1)` ([0x982BA4]) → += 0.3·WorldTimeElapsed (≤ 1),
    else −= 2·WorldTimeElapsed (≥ 0).
  * ps_2_0 (shader 8): s = lerp(0.2 + InvShadowStrength, 1, lit). Midday clear: 0.1 → a shadowed pixel ×0.3 (the old
    port's guess 0.3 gave ×0.5); dusk or overcast: 0.9 → ×1.1; in a tunnel up to 1 more.
* **Specular scale**: sub_6C76E0 multiplies SpecularMin and SpecularRange (both scale·RGB terms) by **TOD +0x60
  CarSpecScale** (timeofdaylighting: midday A01A3942 1, dusk 0E1C797C 1, overcast 94ABDFF0 0.4, dusk overcast 919B6689
  0.4), so overcast cars are 60 % less shiny. The envmap terms are ×1 on the shader path (×0.5 when [0x982C80]+0x40 = 0).
* **elSetupLights** (0x502130) does not use the time of day for the colours (Scale·RGB·255/255): the three shaper lights
  only follow the sun direction.

## 13. VehiclePartDamage against the exe (round 2)

* Init (0x754410) only stores the pivots: trunk (slot 15, behaviour +0x70) (1.73, −0.96, 0), hood (slot 10, +0x5C)
  (−1.13, −0.86, 0); no marker lookup (the decomp's InitAnimationPivot is dead).
* Update (0x74C430): Pose; trunk level 1 → CalcPartRotation(maxAngle 0x3CE38E39 = 10/360, 2.4, 0.2, 80)·360 → AnimatePart
  (15, (0, +a, 0)); hood level 1 → (0, −CalcPartRotation(…, 130)·360, 0) → AnimatePart(10); rear bumper (16) level 1 →
  CalcPartRotation(…, 80) without AnimatePart. **All three share mTrunkAngle (+0xC4) / mTrunkVel (+0xC0)**.
* CalcPartRotation (0x73E620): spring / damper / inertia are function statics initialised by the first call of the
  session ([0x9B3768] / [0x9B3764] / [0x9B3760], guard [0x9B376C]); dT = CarRenderInfo +0x17A4; inputs CarRenderInfo
  +0x10 velocity, +0x20 angular velocity, +0x30 acceleration through world·world⁻¹ (= world space).
* AnimatePart (0x73E920): returns at the first hidden LOD (CarPartModel flag 1); partMatrix = world·world⁻¹ ·
  eTranslate(pivot) · eRotateX/Y/Z(bDegToAng) · eTranslate(−pivot) · world, every step D3DXMatrixMultiply(out, src, M)
  (post-multiply) and eRotateY = D3DXMatrixRotationY (x' = x cos + z sin, z' = −x sin + z cos). The fixed line is
  therefore **x = −pivot.x at model z = −pivot.z = 0** (the ground under the trunk / hood hinge), the pivot's Y (the
  marker heights 0.96 / 0.86 typed into the lateral slot) being ignored by a Y rotation; ±10° turns the lid about that
  low axis (it lifts at the free edge and slides ~0.15 m toward the windscreen / rear window at full swing).

## 14. Render connections, loader, other items (round 2)

* CarRenderConn::UpdateTires: wheel roll += clamp(wheelSpeed / radius · dT, ±17°) (mMaxWheelRenderDeltaAngle), wrapped
  at 2π; front tyre and brake matrices turn by mSteering; UpdateSteering: mSteering follows the sim's steering at
  SteerSpeed°/s, clamped to MaxTireSteer°, 0 beyond 100 m (renderModifier·Tweak_MaxDistForSteeringAnim); a blown tyre
  sinks by Tweak_TireBlowOffset and wobbles (SetWheelWobble). There are **no blur-wheel models and no brake-disc glow in
  MW** (no such slots, textures or code). Wheel spin / steer are drawn by PlayerCar (physics owner): see the hooks in
  the round-2 report.
* Traffic paint: VehicleRenderConn::Load (decomp VehicleRenderConn.cpp:344-394) = RideInfo::Init + SetStockParts +
  the PVehicle's customization record, which traffic does not have → BASE_PAINT TRAFFIC_L1_COLOR01 (light material
  TRAFFIC, white): **traffic is drawn untinted** (port follows since round 2).
* CarLoader (0x73A200..): loads per RideInfo the solid packs, texture packs, wheels and the skin layers into a car
  memory pool (MakeSpaceInCarMemoryPool 0x759FF0, UnloadAllSkinTemporaries 0x759E20); the residency rules only govern
  memory, not what is drawn [not ported: Unity keeps the CarAsset cache of CarBuilder (6 folders + shared ones) and a
  shared-mesh cache of 800 solids].

## 13. Reset flashing and the road reflection (round 3, world agent)

* DrawCar::OnService (DrawCar.cpp:175) sets the service packet's mFlashing while RBVehicle's invulnerability is
  FROM_MANUAL_RESET (1) or FROM_RESET (2) - not the control / physics switches (3 / 4). CarRenderConn::Update 0x756509
  copies it to CarRenderInfo +0x17C0 and calls UpdateFlashing 0x7375C0: interval (+0x17C4) += RealTimeElapsed; past
  0.05 (flt_8B2E60 Tweak_ResetFlashOff) it drops by 0.12 (flt_8B48C0, on + off). CarRenderInfo::Render returns before
  drawing anything (body, parts, both shadows) while flashing and interval > 0: hidden 0.05 s of every 0.12 s. The
  flares (RenderFlaresOnCar) have no such test. Port: CarRenderInfo.UpdateFlashing from PlayerCar.Invulnerability,
  Renderer.forceRenderingOff on every renderer under the car (the parts' enabled flags stay with the damage code).
* CarRenderInfo.All / Body (additive): the road reflection view draws every car upside down about its Body transform
  (world_render.md §4).

## 15. Last mile (round 4): light rigs, env map, wheel LODs, render connection, interior LOD

Sources: exe disassembly (CarRenderInfo::Render 0x74E160 via REA FUN_0074e160.c, eEnvMap::UpdateCameras 0x6DA350, UpdateCarParts
0x755800, UpdateTires 0x746750, UpdateWheelYRenderOffset 0x74D780, elSetupLights 0x502130, eDisplayFrame 0x6DE300, the preset 0x6C1990),
the decomp CarRenderConn.cpp for the animation maths (PC checked where it differs).

### 15.1 Light rigs (eLight.cpp; Core/CarRenderData.cs ShaperRig, Runtime/CarRender/CarEnvironment.cs)
* eShaperLightRig is 0xA0 bytes {NameHash, eShaperLight[4] {Mode, Theta, Phi, R, G, B, Scale}, position, NumOverideSlots};
  elSetupLights reads Lights[1..3] as shader lights 0..2 (Lights[0] is never read). Modes: 0 WORLD_SPACE (d = S2C(θ+180,
  90−φ)), 1 CAMERA_SPACE ((x,y,z) = S2C(..); d = −y·camRight − z·camUp + x·camForward [resolved round 9: 0x6BE140 transposes the world→view matrix]), 2 SUN /
  3 OPPOSITE_SUN (the §4 maths), 4 WORLD_POSITION (point light: AddQuickDynamicLight). Colour Scale·RGB·255/255, no time of day.
* Rigs: CarsInGame 0x8F2E70 (§4); BackRoom 0x8F2B50 = CShop 0x8F2C90 = QRace 0x8F2D30 = Safehouse 0x8F2DD0: (θ,φ,RGB×S) =
  (90, 48, 1/1/0.95 ×0.98), (134, 185, 0.83/0.83/1 ×0.5), (27, 175, 0.83/0.83/1 ×0.35) → game dirs (0, −0.669, 0.743),
  (−0.692, 0.717, −0.087), (0.888, 0.452, 0.087); CarLot 0x8F2BF0 the same with θ negated; Characters 0x8F2F10 (mode 1):
  (122.9, 194, 0.5 ×0.53), (28, 0, 1 ×0.59), slot 3 black (the NIS cop quick light); Default 0x8F2AB0 (eView::
  SetupWorldLightContext, only for car-effect draws without a context); CharactersBackup 0x8F2FB0.
* Who uses which (CarRenderInfo::Render 0x74E3E5, WorldModel::RenderModel 0x73B9C0): game flow 6 → CarsInGame for every car,
  smackable and fragment; FE → FEManager::GetGarageType 2 Safehouse / 3 CShop / 4 BackRoom / 5 CarLot / else QRace; NIS
  characters → Characters. worldbone.efx reads only two lights (LocalDirectionMatrix c71-c72), so the NIS cop quick light
  (RenderFlaresOnCar 0x743254: slot 3 = (0.8,0.2,0)·cpr or (0.2,0.2,0.8)·cpb at the cop car + ENISCopLights offset) has no
  visible effect and is not ported. SetSelectCarLighting 0x4FC310 / AdjustQuickDynamicLight are `ret`; sub_4FC020 / sub_501D50
  (scenery light accumulation) have no callers; the context's LocalEyePosition / EnvMapMatrix (elSetupEnvMap) are not read on
  the shader path (LOCALEYEPOS comes from 0x6C8000 = the camera position in object space).
* Port: CarEnvironment.Rig (CarsInGame in game; CarShowroom.UseSet → SetGarage(platform file) maps CAR_LOT / BACKROOM /
  CUSTOMIZATION_SHOP / SAFEHOUSE / else QRace; SetActive(false) → back to CarsInGame) publishes _MW_CarLightDir/Col; the
  characters' rig is rebuilt from the main camera axes each frame as _MW_CharLightDir0/1 / _MW_CharLightCol0/1 (Character.shader
  uses them; the old CarsInGame fallback stays below it). Car.shader's editor fallback is now the QRace rig.

### 15.2 Env map (eEnvMap, CarEnvironment.cs) — contents now as the exe
* Faces 256² ([0x8F8FF4]), near 0.5 / far 100, eye = view 1's anchor car + (0, 0, 0.95), orientation taken from the player
  camera (equivalent to a world-aligned cube: the shader looks up a view-space reflection vector). Schedule (table 0x8F9028,
  period {6, 4, 1}): g_CarEnvironmentMapEnable 1 → one face per frame F R B L U D; 2 → F and U every frame + one of R B L D;
  3 → all six. The preset 0x6C1990 sets the option 1 from performance level 3, g_CarEnvironmentMapUpdateData 1 at level 4,
  2 at level 5.
* Each face: clear black, StuffSkyLayer(view, layer 3) at scale 0.0065 (DrawSkyEnvMap = 1; the brightness argument only acts
  on the fixed-function path), StuffScenery(view, 0) when UpdateData != 0 (else only while g_ShadowDetail < 2, cull flag
  0x1000), then eRenderWorldLightFlares + eRenderLightFlarePool (the sun pool flare type 21 at camera + 40·sun, exclude mask
  0xFE = only env views) + RenderVehicleFlares(view, 0, 1). No cars, fragments, smackables, rain or particles; no shadow
  technique. Cars under DisableEnvMapSize 18 px (0x74E160; GC 5) draw with render flag 1 = no env map (brakes never).
* Port: car renderers live on layer 29 (CarEnvironment.CarLayer; parts, wheels, brakes, shadow, fragments) and the env camera
  masks that layer, the reflection layer 30 and the showroom 31 out; the dome is drawn by DrawMesh at 0.0065 (SkyRender hook),
  LightFlares.DrawEnv draws the frame's flare meshes and the type-21 sun flare into each face; _MW_CarEnvOff per renderer
  (MaterialPropertyBlock) zeroes the env term under 18 px. [ours] the scenery keeps its player-view LOD models; the FE static garage cubes (`<garage>_XPOS..ZNEG`, eInitFEEnvMapPlat 0x6BF690) are loaded since round 9 (§16.3).

### 15.3 Wheels, brakes and the body (CarRenderInfo::Render)
* LOD: car_body_lod = car_tire_lod = min(mMaxLodLevel, mMinLodLevel + ix) (the traffic table also when mMinLodLevel == C);
  tyres draw while car_body_lod ≤ mMaxTireLodLevel (D) at the body LOD (one model per slot per LOD: `..._FRONT_TIRE_A..D`,
  the rear slot takes the front model when empty), brakes while ≤ MaxBrake B at the body LOD (`..._BRAKE_A..C`). TireFace
  0x738440: a rear wheel at tire_lod == mMinLodLevel whose model +Y (axle) axis · camera direction < FaceCos 0 draws LOD B
  (not in the FE, not during an NIS). LICENSE_PLATE (69) ≤ MaxLicense B; UNIVERSAL_SPOILER_BASE (45) hidden unless the
  spoiler part's level ≠ 0. Port: CarRenderInfo.RegisterWheelLods / UpdateWheelLods (CarBuilder passes the A..E solids).
* GetSpecialLODRangeForCarSlot (PC 0x739B40): with mMinLodLevel == 0 (full detail, the default) INTERIOR follows the body
  A..E and DRIVER draws A..C (FE: never); the C..E / B..E rule of the GC decomp only applies below full detail. Port:
  CarRenderInfo.MinLodLevel (0).
* Camera inside (0x74E6xx): body LOD A, car within 4 m, player view, no NIS, the eye in car space with z + 0.5 inside the
  car's AABB → the car is not drawn (the bumper camera). Port: CarRenderInfo.CameraInside → forceRenderingOff.
* Camber: at body LOD ≤ B the wheel matrix gets eRotateX(bDegToAng(ecar Camber{Front,Rear} × 7°)) (FL +, FR −, RR −, RL +)
  and a 0.03·camber push-down [? not ported: the push-down]. Wobble: SetWheelWobble(i, blown) within 80·renderModifier →
  eRotateX(0x4FA) = 7° before the roll (the GC 16° is unused). ExtraRearTireOffset ≠ 0 → dual rear wheels (ported round 9, §16.1).
* (round 9, §16.1) The attribute wheel scaling of UpdateWheelYRenderOffset 0x74D780 is ported (the old "(0, 0, 1.218,
  0.865)" dump was the Vector4 array read 8 bytes early); dual rear wheels (ExtraRearTireOffset) are ported; CarPartCuller
  (CullParts 0x7422D0) is replaced by the depth buffer (§16.1).

### 15.4 CarRenderConn::Update (Runtime/CarRender/CarRenderConn.cs, new)
The PC packet (Pkt_Car_Service: compressions, wheel speeds, skid, slip, steering, ground state, damage, part state, lights,
flashing, nos, gear, shift status, engine power / speed (only in reverse / neutral or while staging: DrawPerformanceCar::
OnService), extra body pitch / roll = the chassis RENDER_MOTION, blow-outs, health) feeds, per frame:
* UpdateSteering: mSteering[FL/FR] chase the sim steer at DEG2RAD(SteerSpeed)·dT, clamped ±MaxTireSteer, 0 beyond
  Tweak_MaxDistForSteeringAnim 100·renderModifier (not for the anchor car / NIS). Roll clamp mMaxWheelRenderDeltaAngle =
  DEG2RAD(360 / (2·spokes) − 1) when the rim part has a SPOKE count or |ecar WheelSpokeCount| ≠ 0 (OnLoaded 0x750B20), else 17°
  (two-line hook in PlayerCar.Update: CarRenderConn.MaxWheelRenderDeltaDeg).
* UpdateTires: a blown tyre sinks by Tweak_TireBlowOffset −0.12 and tilts the body (mFlatTireAngle ±0.5·atan2(0.12, |tire
  y|/|x|) per side [resolved round 9: ∓π × VU0_Atan2's turns = the GC 0.5, §16.1], ride −0.03 per flat wheel); wheel
  hop (Tweak_UseWheelHop, rear wheels on the ground, WheelHopScale² × RENDER_MOTION × 0.15° roll / 0.2° pitch / 0.3° tyre at
  24 Hz × ramp(slip 1..5 burnout | 5..20 brake)); wobble within 80 m.
* UpdateRoadNoise (30 m): the roughest ground simsurface's RenderNoise {Frequency, Amplitude, MinSpeed, MaxSpeed} ×
  ecar RoadNoise × ramp(speed) as pitch / roll sines (plus the blow-out noise 4 Hz, 1°, 0..10 m/s); off with
  Tweak_DisableRoadNoise. (default / concrete have RenderNoise 0.)
* UpdateBodyAnimation (80 m): roll from lateral g with ecar BodyRoll {DegPerG, MaxGs, DegPerSec}, pitch from forward g with
  BodySquat (accelerating) / BodyDive (braking), noise floor 0.2 g, zero under 1 m/s, rate-limited. GTI: roll (3, 1, 6),
  squat (8, 1, 6), dive (6, 1, 6).
* UpdateEngineAnimation (30 m): shift pitch (E_UPSHIFT / E_DOWNSHIFT → mShifting ±1; Sina(|s|/2) × ShiftAngle ×
  0.95^gear × ramp(|a|/g, 0.1..0.5), speed > 10 m/s, downshift × 0.25), torque roll (EngineRevAngle / EngineRevSpeed /
  EngineRev on the power ratio's rise), vibration engineSpeed·sin(animTime·2π·EngineVibrationFreq)·(VibMin + VibMax·engineSpeed).
  Power ratio (round 9) = Ramp(EngineRacer::GetHorsePower, GetMinHorsePower, mMaxHP) in hp [? stock torque, no boost].
* UpdateRenderMatrix: render = Rx(roll)·Ry(pitch)·body with roll = hop.x + bodyAngle.x + flat.x + noise + torque + vibration,
  pitch = hop.y + bodyAngle.y + flat.y + noise + enginePitch + shiftPitch + ExtraPitch, lifted by ride = hop.z + noise.z +
  flat.z + INCH2METERS(ecar RideHeight) (GTI −1 in: the body sits 2.54 cm lower than the sim's); wheel-well clamp: the highest
  tyre top over INCH2METERS(WheelWell) raises the body by the excess. Port: applied to the Body transform as an offset from
  PlayerCar.Setup's placement (re-captured if the body is moved under it).
* Not ported: UpdateContrails (Xenon effect), the ENISCarPitch/Roll/Shake overrides (Runtime/Nis has its own tweak), the own-car
  hide for bumper / hood cameras (OnRender: mover->RenderCarPOV; ChaseCamera is not ours — the camera-inside test covers the
  bumper view), the RVM anchor skip.

### 15.5 CarLoader / CarInfo residency (0x73A200.., 0x759xxx)
Pool 51,200,000 B ([0x8F7EF0] = 0xC350 KB); a VehicleRenderConn stays S_Loading (not drawn, no fallback model) until the solid
pack, wheel pack and skin layers are loaded; eviction unloads 0-instance ride infos first, then skin perms of a loaded car
(RefreshAllRenderInfo: an on-screen car can lose its skin when the pool is full); UnloadAllSkinTemporaries drops vinyl layers
once composited. Nothing visible to port beyond "a car appears only when fully loaded" (CarBuilder is synchronous).
RideInfo::SetRandomPart / SetRandomPaint / FillWithPreset (random AI looks) belong to the career / AI agents.

### 15.6 Status of the four files
* CarRender.cpp: ported (Render's LOD / wheel / brake / interior / license / spoiler-base / camera-inside / env-off rules, the
  rigs, flashing, damage, flares, decals, skins; round 9: wheel scaling, dual rear wheels, CarPartCuller = depth buffer).
* CarRenderConn.cpp: ported (Update's steering, tyres, noise, body / engine animation, render matrix); not ported: contrails,
  the POV hide (camera owner), RVM.
* CarInfo.cpp: ported (RideInfo::Init LOD ranges incl. the PC special-range rule, SetPart / SetStockParts, textures).
* CarLoader.cpp: memory residency only — nothing visible left (see 15.5).

## 16. Round 9: drawn wheel geometry, garage cubes, fragments, last car-render items

Sources: disassembly of speed.exe (python + capstone), harness `render9` (NFSMWStaging/Harness/ProbeRender9.cs: `tireoff
<car…>` raw ecar arrays, `tirebox <CAR…>` wheel model boxes, `wheels <CAR> <kit>` the computed geometry, `extrarear` all
ecar ExtraRearTireOffset values).

### 16.1 Drawn wheels (Core/CarRenderData.cs WheelRenderGeometry, Runtime/CarRender/WheelRenderOffset.cs, CarBuilder)
* **ecar TireOffsets** is a Vector4[4] (game order FL FR RR RL) = (x forward, y left, z, render radius). Vector4 attribute
  arrays are 16-byte aligned: the data starts 16 bytes after the array header, not 8 (Core/Attrib.cs TryGet returns
  header + 8 — the earlier "(0, 0, 1.218, 0.865)" was that misread). GTI (1.218, ±0.865, 0, 0.325) / (−1.37, ∓0.855, 0,
  0.325); 911TURBO (1.13, ±0.85) / (−1.22, ∓0.9), 0.32; COPMIDSIZE (1.75, ±0.94) / (−1.3, ∓0.95), 0.33.
* The **sim** stands on the chassis TRACK_WIDTH / FRONT_AXLE (GTI FRONT_AXLE 1.17, track 1.539) while CarRenderConn's
  mTirePositions = TireOffsets: the exe draws each wheel where ecar says, not where the physics wheel is.
* UpdateCarParts tail (0x755C1D..0x755CDF): model width W = |bbox.max.y − min.y|, radius R = 0.5·|bbox.max.x − min.x|
  ([0x8933D4]) of the FRONT_WHEEL slot model at the min LOD (+0x120 / +0x128) and of the REAR_WHEEL one (+0x124 / +0x12C).
  GTI stock tyre: W 0.263, R 0.328.
* UpdateWheelYRenderOffset (0x74D780): yoff = −TireOffsets.y, pushed outward by KitWheelOffsetFront / Rear[kit] (u8 ×
  0.001; GTI 0 40 120 95 90 0 / 0 40 130 105 100 0; the tweaks [0x9B0960] / [0x9B0964] are 0; kit = the BODY part's
  KITNUMBER 0x796C0CB0); WheelWidthScale = TireSkidWidth[i] × TireSkidWidthKitScale[kit].x / .y ÷ W (else 1);
  WheelRadiusScale = TireOffsets[i].w ÷ R (else 1). The 911's rear tyres come out 1.18 × wider than the fronts.
* Render (0x74F048..0x74F2A7, CarRender.cpp:3343): trans_pivot = scale(r, w, r), v3.y = −p (p = W·w·0.5,
  WheelPivotTranslationAmount [0x903314]); trans_axle.y = −(yoff + p) when yoff &lt; 0, else p − yoff; the tyre matrix's own
  y is zeroed; left wheels get LeftTireRotateZMatrix (180° about Z); z −= camber push-down; tyre = trans_pivot · flip ·
  wobble · tyre matrix · trans_axle. The rim face (model y = 0) sits on the ecar lateral (plus kit offset), the tread
  extends inboard. Brakes: brake_trans_pivot.y = markerY·w − p with the wheel model's FRONT_BRAKE / REAR_BRAKE marker
  (rear falls back to front), else 0.
* **ExtraRearTireOffset** (live data: semi 1.5, trafcemtr / cemtr 0.93, trafdmptr −1, others 0): a second rear tyre per
  side drawn with the rear model at tire2/3.v3.x += offset (CarRender.cpp:3558-3612).
* Port: CarBuilder keeps the sim pivots (PlayerCar's anchors) and gives the Tire model the (r, r, w) scale and the −p
  shift; WheelRenderOffset (LateUpdate, order 60 after CarRenderConn) moves Spin / Brake / SpinExtra by the car-space
  offset ecar − sim with the pivot's rotation undone, so steer and camber turn the wheel about its drawn centre as in
  the exe; CarRenderConn's flat-tyre tilt uses the drawn positions. Cars without their own ecar collection keep the old
  mesh-fitted rims [ours] (the class default's TireOffsets put the rear wheels at x = 3 / 5).
* **CarPartCuller** (CullParts 0x7422D0, CarRender.cpp:264): per-part plane tests that skip tyres / brakes / underside
  hidden inside the body for the camera position — a draw-call saving whose result is invisible behind the body;
  replaced by the depth buffer [deviation, by design].
* Flat-tyre tilt (UpdateTires 0x746BF8): VU0_Atan2 (0x5CB5D0) = (short)bATan(x, y) × 2⁻¹⁶ (a fraction of a turn); × −π
  ([0x89120C]) / +π ([0x8B2B98]) = ∓0.5 × radians — the GC 0.5. Resolved.

### 16.2 Car shadow (Runtime/CarShadow.cs)
* DrawAmbientShadow PC 0x743EA0: scale 1.05 ([0x8B2EA0]), × heliScale 0.43 ([0x903660]) for CARTYPE_COPHELI (RideInfo
  type 4); alpha (0x74443C) = 2 × (min + (110 − min) × |up.z| × (1 − clamp(elev/8))), min 110 in the front end, **50 in
  game** (GC: 0..25), clamped 254. [?] The ×2 is read as the PC ePoly 0x80 = 1.0 colour convention: the port draws
  min + (110 − min)·… over 255. The `shadow_scale` argument (HeliRenderConn mShadowScale = AIVehicleHelicopter::
  GetShadowScale 0x4325E0) is never read on PC ([ebp+0x10] unused). The helicopter uses the in-game rules (one-line
  hook in Helicopter.cs: CarShadow.SetHelicopter), corners off the ground face at z = −100. In-game is "a world is
  loaded" (traffic and the helicopter have no PlayerCar).

### 16.3 Front-end garage cubes (CarEnvironment.SetGarage, Car.shader _MW_CarEnvView)
* eInitFEEnvMapPlat 0x6BF690 (EndGameFLowLoadingFrontEnd 0x64B6EB, sub_6DB0D0, GarageMainScreen::RefreshBackground
  0x7B4183; only while [0x8F945B] = 1): streams `<prefix>_<XPOS|XNEG|YPOS|YNEG|ZPOS|ZNEG>` (0x8F9314; prefix
  GetGaragePrefixFromType 0x516E90: 1 QRACE, 2 CAREER, 3 / 4 CSHOP, 5 CARLOT, else "") from GLOBAL\DYNTEX.BIN (256² BGRA);
  EnvMapTextureLoadedCallback 0x6BF5A0 memcpys each into face 0..5 of the car env cube [0x982A64] once all six are in.
* The FE never renders cube views, and car.efx looks the cube up with the **view-space** reflection vector, so the
  showroom reflection is camera-locked. Port: a Cubemap per prefix (SetPixelData of the raw faces [? row order]),
  `_MW_CarEnvView` = 1 makes Car.shader turn the reflection vector into D3D view space (UNITY_MATRIX_V, z negated).

### 16.4 VehicleFragmentConn, VehicleRenderConn, VehiclePartDamage, CarSkin, eModel
* **VehicleFragmentConn** (0x7475C0 ctor, UpdateModel 0x75AE10, Update 0x75B130, FetchData 0x75B1E0): on first update it
  copies the car's MasterReplacementTextureTable (0x36C bytes = 0x49 entries, a snapshot) and LightMaterial_CarSkin,
  takes `vrc->FindPart(slot)` (FindPart 0x738960) and the inverse of the collision bound's orientation / pivot, then
  draws a WorldModel at offset × fragment matrix; Update feeds InView / DistanceToView to DrawVehicle::Part (the
  KILL_OFF_SCREEN timer) and disables the model when the service fails. Port = CarFragments.Spawn (the slot's drawn
  object copied with its current materials, i.e. the snapshot) + CarFragment (visibility timer).
* DrawVehicle::SpawnModel also refuses while Listable<IModel>::Count() > 434 (DrawVehicle.cpp:191): port counts live
  fragments + spawned smackables [? the port's props carry a Smackable from load; only spawned ones are exe IModels].
* **VehicleRenderConn::CheckForRain** (0x744C70, called from CarRenderConn::Update 0x7565C8): the view's Rain intensity
  (+0x28C) > 0 and the camera mover within 60 m ([0x8B2F38]); then !NoRain (+0x234) within 10 m or for the anchor car,
  else !NoRainAhead (+0x238), which Rain::Update (0x757D8B) copies from NoRain on PC. TireState::SetSurface (0x738D50)
  then picks effect [1] (wet spray) when raining and the surface lists ≥ 2 (flat [2] first with ≥ 3); UpdateWorld
  (0x738F50) re-picks on any change. PC DoFX (0x7457D0) updates the drive effect unconditionally (the decomp's
  numCopsActiveView < 2 / !INIS tests are not in the exe). Port: Runtime/Effects/CarEffects.cs.
* **VehiclePartDamage ManageGlassDamage** (0x74C210): a damaged window slot sets its entry and REAR_DEFROSTER (+0x320)
  to WINDOW_DAMAGE0; an undamaged slot restores only its own entry, and Reset (0x75C330: parts reset, DamageZone(z, 0),
  UpdatePartsEnabled inline, UpdateCarParts) never rebuilds the table: the defroster stays cracked for the life of the
  CarRenderInfo (a pooled cop after RESET_DAMAGE). Ported (CarRenderInfo._defrosterCracked). UnitTest 0x75A820 has no
  caller.
* **CarSkin**: DumpPreComp 0x73B360 and the "_PRECOM_" names are dead: UsePrecompositeVinyls [0x9B09FC] has no writer and
  SkinType 2 needs composite slot 5..12, which VehicleRenderConn::Load picks only with that flag (GarageCarLoader 0x7A27CB
  passes 1 or 2). The P8 CompositeSkin 0x748AD0 / CompositeWheel8 run only for 8-bit DUMMY_SKIN / DUMMY_WHEEL textures
  (PC: A8R8G8B8). Spinners (GetSpinnerTextureHash 0x749310, CompositeRim 0x7535E0's second composite): no MW rim part has
  SPINNER_TEXTURE (`carrender parts GTI 66/67`). GetTempCarSkinTextures 0x753320 = loader residency (§15.5).
* **eModel**: eSmoorthNormals 0x4FB840 (from the CarRenderInfo ctor 0x7600BC: BASE, BODY, mirrors, SPOILER, ROOF, HOOD,
  HEADLIGHT, BRAKELIGHT per LOD, chunks 0x134017-19) merges the solids' eSmoothVertex tables and averages normals per
  hash / smoothing group, but writes them through eSolidPlatInterface::SetSmoothVertex 0x6BE570 = `ret 0x10`: a no-op on
  PC (dead). The rest (Init / UnInit / ConnectSolid lazy hash binding, Attach / Apply / Restore replacement texture
  tables, ReplaceLightMaterial, GetBoundingBox, GetPostionMarker, pivots) is the solid / material plumbing the port does
  with MeshFactory meshes and CarRenderInfo materials.
* elSetupLights mode 1: resolved (§15.1).
