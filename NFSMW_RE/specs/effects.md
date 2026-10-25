# Particle effects, skid marks and their data (NFS MW PC v1.3)

Status: static RE plus data decoding. Nothing here was checked against the running game. Tags: **[C]** confirmed in
code or data, **[I]** inferred (strong), **[?]** open question. Coordinates are game space (X, Y, Z-up).

Sources: `refs/nfsmw` Ecstasy/EmitterSystem.cpp (fully decompiled), World/CarRenderConn.cpp, World/VehicleRenderConn.cpp,
World/WorldConn.cpp, World/Skids.cpp, Physics/Behaviors/Effects.cpp, Physics/Behaviors/DrawCar.cpp, and the
Generated/AttribSys classes `emitterdata`, `emittergroup`, `effects`, `simsurface`, `ecar`, `pvehicle`, `smackable`.
PC addresses come from `config/SPEED_EXE_1_3/symbols.json`. EmitterControl::Update (0x4FF190) and the spawn count in
Emitter::SpawnParticles (0x509E66) were checked against the exe through REA. **[C]**

Unity port: `NFSMWUnity/Assets/NFSMW/Core/EmitterData.cs` (readers), `Runtime/Effects/EmitterSystem.cs` (system and
renderer), `CarEffects.cs` (tyres, pipes, collisions, smackables), `SkidMarks.cs`, `WorldEffects.cs` (placements),
`Shaders/Particle.shader`. Harness: `emitters [groups|data|effects|surfaces|car <model>|smack|lib|libtop|scan|markers <CAR>|tex]`.

## 1. Where the data lives

| What | Where | Count |
|---|---|---|
| Emitter definitions | `GLOBAL\attributes.bin`, class `emitterdata` 0xB30B18AF | 205 **[C]** |
| Emitter groups (what code creates) | class `emittergroup` 0xABA86E60: `Emitters[6]` (RefSpec), `IntensityRanges[5]` (Vector2), `FarClip` | 128 **[C]** |
| World effects (collision/hit reactions) | class `effects` 0xEBCEE74C: `emittergroup` ref, `EmitterQuadratic`, `InheritVelocity`, `VisualCullDist`, audio fields | 198 **[C]** |
| Tyre effects per surface | class `simsurface` 0xFB111FEF: `TireSlipEffects[3]`, `TireSlideEffects[3]`, `TireDriveEffects[3]` (TireEffectRecord) | 47 **[C]** |
| Car effect hooks | class `ecar` 0xA5B543B7 (model name, ChangeWithDefault): `SlipFX[2]`, `SkidFX[2]`, `TireSkidWidth[4]`, `NOSEffect`, `MissShiftEffect`, `EngineBlownEffect`, `DamageEffect`, `DeathEffect` | **[C]** |
| Collision effect links | class `pvehicle` 0x4A97EC8F: `OnHitWorld`, `OnScrapeWorld`, `OnHitObject`, `OnScrapeObject`, `OnHitGround`, `OnScrapeGround`, `OnBottomOut`, `OnBottomScrape`; `smackable` 0xCE70D7DB: `OnDetached` (+ the same On* set) | **[C]** |
| Placements | chunk `0x0003BC00` BCHUNK_SPEED_EMITTER_LIBRARY: FE showroom sets (`FRONTEND\PLATFORMS\*.BIN`) and streamed track sections | 40 FE, 1,535 in L2RA (290 sections, 42 triggers) **[C]** |
| Particle textures | `GLOBAL\InGameB.lzc` texture pack `PARTICLE` (FX_SMK01_BLEND, FX_SPARK03_ADDITIVE, ...); `FRONTEND\FrontB.lzc` has a few copies | 41 used **[C]** |
| Skid texture | `SKID_ROAD` (bStringHash) in `GLOBAL\InGameA.bun` | **[C]** |

No emitter data lives in a GLOBAL bundle chunk: `0x3BB00` (EMITTER_GROUP) is accepted and ignored by the loader, and
`0x3BD00` (texture page ranges) is Xenon-only **[C]**. Field names hash with the attribute Jenkins hash, so
`AttribCollection.TryGet("Life")` etc. resolve directly (the vault string table has no field names).

### 1.1 emitterdata layout (0x118 bytes, all in the layout) [C]

| Off | Field | Meaning |
|---|---|---|
| 0x00 | VelocityStart (V4) | base velocity, local axes (see 2.3) |
| 0x10 | AccelDelta (V4) | +- acceleration |
| 0x20 | RelativeAngle (V4) | 4 keys, degrees of spin added to the initial angle |
| 0x30 | VelocityDelta (V4) | +- velocity |
| 0x40 | Size (V4) | 4 keys, full sprite width (m) |
| 0x50 | KeyPositions (V4) | key times x 3 (0,1,2,3 = evenly spread) |
| 0x60 | VolumeExtent (V4) | spawn box size, local |
| 0x70 | AccelStart (V4) | base acceleration, world axes |
| 0x80 | VolumeCenter (V4) | spawn box centre, local |
| 0x90 | TextureAnimation | {i32 AnimType 0/2/4/8/16 (N x N flip-book), u8 FPS, u8 RandomStartFrame} |
| 0x98 | Texture | {u32 texture name hash (eTEG_ParticleTextures), u32 index} |
| 0xA0 | CollectionName | text |
| 0xA4 | InitialAngleRange | degrees |
| 0xA8 | Drag | v += v x max(-1, -dt x Drag x abs(v)) |
| 0xAC/E4/B0/B4 | Color1..4 | RGBA u32, r in the top byte |
| 0xB8/CC | OnCycle, OnCycleVariance | s |
| 0xBC/E8 | Life, LifeVariance | s; variance is a fraction of Life |
| 0xC4/C0 | NumParticles, NumParticlesVariance | per second (x intensity) |
| 0xC8 | AxisConstraint | 0 none, 3 YZ, 5 XZ, 6 XY, 8 camera |
| 0xD0/D8 | OffCycleVariance, OffCycle | s |
| 0xD4 | FarClip | m (the group's FarClip is the one used) |
| 0xDC | RotationVariance | 0..1 extra spin scale per particle |
| 0xE0 | MotionLive | when set, no inherited velocity at spawn |
| 0xEC/104 | MotionInherit, MotionInheritVariance | share of the group's inherit velocity |
| 0xF0/100 | Speed, SpeedVariance | non-zero: cone/disc velocity of this speed |
| 0xF4 | StartDelay | s (StartDelayRandomVariance 0x113: random 0..delay) |
| 0xF8 | SpreadAngle | degrees |
| 0xFC | RandomRotationDirection | |
| 0x108 | Gravity | non-zero: v.z -= Gravity x dt and the acceleration is ignored |
| 0x10C | SpreadAsDisc | disc in local XY instead of a cone around local Z |
| 0x110 | AlphaToKillAt (i8) | die when alpha <= it (unless NoKillAtAlpha 0x111) |
| 0x112 | IsOneShot | |
| 0x114 | EliminateUnnecessaryRandomness | velocity = start + rand(delta) in world axes |
| opt | XenonEffect | fuelcell_effect refs (next-gen sparks; not ported) |

### 1.2 Chunk 0x3BC00 (EmitterLibrary) [C]

16-byte aligned header `{EndianSwapped, Version, NumEmitterLibraries, SectionNumber}`, then per library 0x50 bytes
`{u32 GroupKey (emittergroup collection key), pad, u16 SectionNumber, u16 NumTriggers, EmitterGroup* (0), pad,
bMatrix4 LocalWorld}` followed by NumTriggers x 0x30 `WorldFXTrigger {next, prev, lib*, f32 radius, bVector3 pos (+w),
f32 reset time, u16 probability (0xFFFF always), u8 state, u8 flags, f32 last end, pad}`. In track sections the chunk is
top level (after the 0x80034100 pair). Section 0xA28 = start at load and always pass the distance test (all FE sets);
other sections start within group FarClip + 20 m and 0.55 cos of a view (ServiceWorldEffects); section 0 is looked up
by key only (NIS). Example: CAREER_SAFEHOUSE = 7 x `fxenv_small_steam2` (key 0x31FB2AB6). L2RA: fxenv_leaves1 x627,
fxenv_fog1 x408, fxenv_fog2 x133, fxenv_smokestack x98, fxenv_chimney1 x97, birds, fountains.

## 2. Runtime (EmitterSystem.cpp)

### 2.1 Pools and flags [C]
1024 particles, 500 emitters, 200 groups. Creation flags: 0x20000 always close enough, 0x40000 gameplay (no frustum
test, may evict world effects), 0x400000 forced one-shot, 0x800000 live world effect, 0x8000000 world placement,
0x10000000 owner is a vehicle, 0x40000000 tyre effect, 0x80000 "old surface effect" (owner let go: deleted when its
particles are gone). Emitters copy `group flags & 0xFFFF0000`.

### 2.2 Emitter control (0x4FF190) [C]
States NOT_STARTED, DELAYING, ON_CYCLE, OFF_CYCLE, ON, OFF. one-shot = IsOneShot attribute or flag 0x400000. An on cycle
without an off cycle is ignored for continuous emitters. NOT_STARTED -> DELAYING (StartDelay) -> ON_CYCLE (OnCycle +
rand var) or ON. ON_CYCLE end: one-shot -> OFF, else OFF_CYCLE (OffCycle + rand var). ON spawns every update. An OFF
emitter orphans its particles and is deleted when they are gone; a group without emitters is deleted.

### 2.3 Spawning (0x509B30) [C]
count = intensity x NumParticles x (1 - NumParticlesVariance) x dt, or x Life(1 - LifeVariance) instead of dt for an
effectively-one-shot emitter (emitter ONE_SHOT bit from MakeOneShot, or flag 0x400000) without an on cycle. A count
under 1 accumulates and spawns one particle once the sum passes 1 (fractions over 1 are dropped). Skipped when intensity
is outside the emitter's window (EmitterGroup IntensityRanges, only when there is one per emitter; empty = 0..1).
Per particle (bRandom LCG, seed per emitter):
- Speed != 0: direction = cone (local Z turned by random X/Y/Z angles within +-Spread/2) or disc (local XY, tilted
  about Y); x (Speed - var + rand var).
- else EliminateUnnecessaryRandomness: v = VelocityStart + rand(VelocityDelta), a = AccelStart + rand(AccelDelta), world axes.
- else v = local axes x uniform(start +- delta), a = world axes x uniform(start +- delta).
- position = LocalWorld x (VolumeCenter + rand(VolumeExtent) - Extent/2); life = Life(1-var) + rand(Life var), capped 60 s.
- inherit: v += InheritVelocity x clamp(MI(1-var) + rand, 0, 1) unless MotionLive.
- angle: -range/2 + rand(range) (bAngle, even), odd bit = rotate the other way when RandomRotationDirection.

### 2.4 Curves [C]
`EmitterDataAttribWrapper::CalculateBases`: colour keys Color1..4 (0..1), extra keys x = Size/2, y = RelativeAngle x
182.04 at u = KeyPositions/3. `hermite_basis` multiplies Mf by its own inverse, so the basis is U^-1 P with U rows
[u^3 u^2 u 1]: the cubic through the four keys. t = 1 - lifeLeft / Life (the unvaried Life, so short-lived particles start
mid-curve). Colours clamp to 0..255 each update; alpha <= AlphaToKillAt kills.

### 2.5 Update [C]
Per update: drag, then gravity (v.z) or acceleration, then position. Groups beyond FarClip of every view lose their
particles; spawning also needs the group within 0.7 cos of the view direction unless flag 0x40000. Texture animation
advances dt x FPS / N^2 of the 16-bit frame counter (wraps).

### 2.6 Rendering [C/I]
Per emitter with particles: GetTextureInfo(Texture hash); quads of half-size `mSize` from the view's right/up turned
about its forward by the particle angle (PlatRotateScaleParticle), or a fixed game-space plane for constraints 3/5/6.
Blend comes from the texture (AlphaBlendType 1 blend, 2 additive; the PARTICLE pack names end _BLEND/_ADDITIVE) **[C]**.
particles.efx (PE resource IDI_PARTICLES_FX, effect id 18) technique 0 (`shader` 1) is vs_1_1 (oPos, oD0 = vertex
colour, oT0 = uv, oT1 = 0) + ps_1_1 `mul_x2 r0, t0, v0` **[C]** (round 3, parsed from the resource): colour and alpha
both x2, saturated, so the 0x7F grey keys are neutral. No fog in the effect, and the exe sets D3DRS_FOGENABLE 0 at
device setup (0x6CFD59), so particles are never fogged **[C]**. Technique 1 (shadowLevel 1) multiplies rgb by
sat(shadow + 0.5) and technique 2 is its ATI form; not ported (particles take no shadow technique in the port).
UVs: 0..1, or cell (frame % N, frame / N) of the N x N grid; corner assignment of PlatAddParticle (0x6C1160) not
decompiled **[?]**.

## 3. Who creates effects

### 3.1 Tyres (CarRenderConn::UpdateTires 0x746750, TireState 0x7451D0-0x7457D0) [C]
Per wheel on the ground and within 80 m: slip = abs(wheel slip speed) - tolerated slip (Chassis::ComputeMaxSlip:
Ramp(speed, MinStaticSlipSpeed, MaxStaticSlipSpeed) + MinStaticSlip; 71 in reverse); skid = abs(lateral speed)
(DrawCar.cpp:225). Skid marks when sqrt(max(skid x 0.05 - 0.1, 0)^2 + max(slip x 0.2 - 0.1, 0)^2) > 0.3. Effects:
the surface's slip/slide/drive TireEffectRecords ([0] dry, [1] rain, [2] flat tyre), each Ramp(value, min, max) as the
group intensity, with value = slip x ecar SlipFX[axle], skid x SkidFX[axle], car speed. The group sits at the tyre's
ground point with the car's orientation and inherits the car velocity. Asphalt: slip `fxtd_sl_asphalt` 0.125-5,
slide `fxtd_sk_asphalt` 1-2, drive none. Default ecar: SlipFX 1,1; SkidFX 0.1,1.

### 3.2 Pipes and engine (CarRenderConn::UpdateEffects 0x7561C0, VehicleRenderConn::Effect) [C]
Pipe effects sit on the body's position markers EXHAUST 0xA5B28001, LEFT_EXHAUST 0xBCF8A18B, RIGHT_EXHAUST 0xBD7CF15E
(their Z axis points out of the pipe). NOS: NOSEffect (`fxcar_nos`: emcar_nos_fire + emcar_nos_glow) runs while
nitrous burns; missed shift fires MissShiftEffect (`fxcar_exhaust_bmw`) once; blow-off reuses NOSEffect. Engine
effects sit above the front axle (avg of front tyres, z + 0.2 x track): DeathEffect at health 0, DamageEffect at
health <= 0.5, EngineBlownEffect (`fxcar_engineblow1`). Contrails (NOS or >= 44 m/s) are a Xenon effect (fuelcell_effect
0x16AFDE7B).

### 3.3 Collisions (Physics/Behaviors/Effects.cpp) [C]
Each collision: OnHit with the impulse (Ramp(impulse, min, max) from the matching EffectLinkageRecord: "default"
record, overridden by the record for the other surface or its nearest ancestor), OnScrape with the sliding speed
(over 1 m/s; intensity clamp((speed - min)/(max - min), 0.1, 1), paused 0.2 s after the last contact). Hits fire
the `effects` collection one-shot (World_OneShotEffect): intensity = EmitterQuadratic(x) clamped 0..1, inherit =
owner velocity x InheritVelocity, cull at VisualCullDist, emitter Z along the contact normal. The live scrape effect
turns the local Y axis onto the normal (WorldConn.cpp:338). Cars: OnHitWorld default -> `carhitwall`
(fxcar_impactl), OnScrapeWorld default -> `carscrapewall` (fxcs_sc_metal), OnHitObject carbody -> `carhitcar`,
OnHitGround default -> `carhitpavement`. With 3+ wheels down a ground contact is OnBottomOut/OnBottomScrape.
Smackables: when one comes loose, its `OnDetached` record (e.g. `lamp_lrg_hit_car`, Ramp(|closing velocity|, 0, 10)).

### 3.4 Skid marks (World/Skids.cpp) [C]
Up to 128 skid sets (512 in drift races), 8 segments each; segment = centre + half-width vector (1/64 m bytes) +
intensity byte. Width = ecar TireSkidWidth x max(|tyre axle . track normal|, 0.5); intensity = max(that dot x
intensity, 0.3). Merge rules: under 0.25 m or 1 - dot < 0.002 extends the last segment (up to 3 m); 1 - dot > 0.2
ends the set. Drawn 0.05 m above the ground, colour 0x80808080, alpha = intensity, faded by screen size (pixel size of
1 m: under 10 px alpha drops 42.7 per px, under 4 px skipped).

## 4. Port deviations
- Particles keep float velocities (no 8-bit direction compression).
- A fired (0x400000) group's emitter without an on cycle bursts once; the exe would keep spawning from ECS_ON **[?]**.
- Collision impulse is the normal closing speed (m/s) **[?]**; wall and ground contacts use the "default" surface
  (only ground triangles carry simsurface keys in the port).
- Tyre slip/contact/surface come from PlayerCar's private wheel state by reflection.
- No Xenon effects (sparks lines, contrails), no rain variants, no world triggers, no damage/death by health,
  no CF_BLOWOFF.
- Particles are not fogged (round 3; Particle.shader's Unity fog was removed; it had been inactive since RenderSettings.fog
  is off).

## 5. PC motion blur (the speed / radial blur)

Visualtreatment.efx declares `VisualEffectRadialBlur` and `RadialBlurOffset`, but none of the 140 shaders embedded
in speed.exe reads either one, and no preshader does. The radial-blur value (VisualTreatment +0x178: the max of the
uves, NOS and speedbreaker blurs) is used only by eDisplayFrame's motion blur, `sub_6DBB20`. That function runs only
while `g_MotionBlurEnable` (0x9017DC, a registry option) is set.

Each frame:

1. **Strength.** `m` (0x982C6C) eases toward `0.2 × radialBlur`. It moves +0.02 or −0.01 per frame, and only when
   more than 1/255 away. The target is 0 on a frame where `QueryFlushAccumulationBuffer` (0x725570) is set, **or while
   `____icemanager_isplayingsomething` (0x8F9B28) is 0** (0x6DBB3F). That byte is 1 in .data; ENISMotionBlur
   (0x62E3AC) sets it to fEnableMotionBlur, ICEManager::SetGenericCameraToPlay (0x46E300) to "no generic camera
   named", ICEMover::Update (0x4830DA) / sub_473CD0 / NISActivity::Release (0x6F75D8) back to 1. The speed term below
   is not gated. Port: NisEventRunner.IceManagerIsPlayingSomething (default true, reset to true when a NIS ends - two
   one-line hooks in Nis/NisEvents.cs). The flush is set by FlushAccumulationBuffer 0x725550 on mover snaps (0x47CF00,
   flag +0xA2) and on ICE key cuts (0x482160).
2. **Camera velocity.** Read `Camera::VelocityKey.Position`: PC +0x200, the eye's movement ÷ frame time. Its world
   length is `s`; transformed by the camera matrix (w = 0) and normalised, it gives `n` in view space.
3. **Cut-off.** If `s ≤ 25`, nothing is drawn.
4. **Alpha.** `a = min(clamp((s − 25)/100, 0, 1) + 1.1·m, 0.65)`. Nothing is drawn below 1/255.
5. **Stretch.** `k = (0.5·m + 1) · a · 0.00625 · 1.1`, and
   `rect = (n.x + n.z, n.y + n.z, n.x − n.z, n.y − n.z) · k`.
6. **Copy.** StretchRect copies the back buffer into texture A (0x93DEF8, full size).
7. **Filter.** FUN_006d2840 (filter.efx) runs twice: A → B with `rect`, then B → A with `rect × 4`. It draws a quad
   with 4 texcoord sets of weight 0.25. Set k covers u ∈ [−k·rect.x, 1 − k·rect.z] and v ∈ [−k·rect.y, 1 − k·rect.w].
8. **Blend.** A is drawn over the back buffer with D3DRS_BLENDFACTOR = a (BLENDFACTOR/INVBLENDFACTOR). It falls back
   to DESTALPHA/INVDESTALPHA without blend-factor support.

There is no frame feedback: the "accumulation buffer" is just the copy.

Port: VisualTreatment passes 4 and 5 (pass 5 keeps the frame's alpha: the exe blends RGB only, so the overbright mask
is not blurred). The eye's velocity is measured from the camera transform. A move over 20 m in one frame counts as a
cut [ours].

No depth of field on PC: none of the 31 embedded effects has a DOF pass and no post step reads a focus distance
(DOFullScreenAA is the full-screen anti-aliasing option). CameraDof (Camera/IceShot.cs) therefore drives nothing in the
renderer. ENISVisualLook's DesaturationTarget / ColourBloomIntensityTarget (+0x1DC / +0x1E0) replace the blended look
while >= 0 (UpdateHeat 0x74BFBE); VisualTreatment applies both, but in game sub_6D4EF0 recomputes the desaturation from
the sun strength, so only the colour-bloom override is visible (as in the exe).

The earlier port drew an 8-tap zoom of up to 21% of the half-screen whenever the radial-blur value was non-zero, even
when stopped. That caused the ghost copies at pursuit start.

## 6. Forest fog banks (the "beige shapes" in Highlands race shots)

Placed group `fxenv_fog1thick`, emitter `emenv_fog_far1`, sits along the Highlands circuit (15.1.1). It spawns 5/s
inside a 100 × 50 × 20 box at local (0, −25, 10). Each particle lives 8 s, is 20 m across (Size 20, halved to mSize 10,
as in the decomp), and has a vertex alpha curve 0 → 0x7F → 0x3F → 0. Its texture is FX_SMK06_BLEND (0x0384C271,
32×32 DXT3), which exists only in InGameB.bun. The PC particle effect is a plain `mul_x2 r0, t0, v0` modulate with no
fog output from the vertex shader, so a puff peaks near full opacity. Magnified, the 32×32 alpha mask shows the blocky
contour.

Checked against speed.exe and the decomp: spawn volume, size halving, colour curve, texture and shader all match. The
Particle shader no longer applies Unity fog (round 3), so it matches the PC effect.

## 7. Tyre effects in the rain (round 9)

- VehicleRenderConn::CheckForRain (0x744C70, from CarRenderConn::Update 0x7565C8) **[C]**: raining = the view's Rain
  intensity (+0x28C) > 0, the camera mover within 60 m of the car ([0x8B2F38]) and !NoRain (+0x234; the NoRainAhead branch
  for cars past 10 m that are not the anchor reads +0x238, which Rain::Update 0x757D8B copies from NoRain on PC).
- TireState::SetSurface (0x738D50) **[C]**: per list (slip / slide / drive) index 2 when the tyre is flat and the list has
  3 entries, else 1 when raining and it has ≥ 2, else 0; UpdateWorld (0x738F50) re-picks whenever the surface, the rain
  flag or the flat flag changes. The simsurfaces' second entries are the wet-road spray groups.
- TireState::DoFX (PC 0x7457D0) **[C]**: the slip, skid and drive effects are updated back to back; the decomp's
  `numCopsActiveView < 2` and `!INIS::Exists()` gates on the drive effect are not in the PC exe.
- Port: Runtime/Effects/CarEffects.cs (CheckForRain, TireState.SetSurface(…, raining), drive effect always updated).
