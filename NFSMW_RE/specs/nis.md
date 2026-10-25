# NIS characters, events and world animation

Scope: coverage item 13 "NIS characters and world animation" (Sim/Activities/NISActivity.cpp, EAGL4Anim/Skeleton.cpp,
Animation/AnimEntity_BasicCharacter.cpp, AnimScene/AnimPlayer, EAGL4Anim/CsisEventChannel.cpp, the WorldAnim files).
Cars, cops and the ICE cameras were already played by `NisPlayer.cs` / `IceCameras.cs` (camera_core.md §7); this round
adds the bundle's characters, the event sequencer and its events, the NISActivity timing/skip rules, and the track's
world animations. Ground truth: speed.exe v1.3 (addresses below), the CC0 decomp (`refs/nfsmw`, GameCube; the exe wins
where they differ) and the data. Harness checks: `Harness nis chars|events|world|worldmodels`.

## 1. What is in a NIS bundle (`NIS\Scene_<name>_BundleB.bun`)

| chunk | content | reader |
|---|---|---|
| 0xB3300000 | texture packs (character skins) | `TexturePack` |
| 0x00E34009 | EAGL4 skeleton (one per character) | `EaglSkeleton` (Core/EaglAnim.cs) |
| 0x80134000 | character solids (skinned, effect 2) | `SkinnedSolid` (Core/NisContent.cs) |
| 0x00E34010 | animation banks: characters (`ArrestM01_MCop_s`) and the car/cop tracks (`...ZPM_Car1_t`) | `EaglAnimBankReader` |
| 0x0003B811 | event sequencer pack ('CARP' UGroup) | `EventSeqPack` (Core/EventSequencer.cs) |
| 0x80037020 | scene: 0x37030 header, 0x37040 per character entity | `NisLibrary`, `NisCharacterInfo` |

Only the 26 **Arrest** scenes carry characters (64 entities: male/female cop, perp, handcuffs). The story scenes with
Razor/Mia are FMVs (`FMV*`, absent from this copy); the `WM*` world-moment scenes hold car tracks only. All 64/64
characters resolve skeleton, animation, solid and every texture (`Harness nis chars`).

### 1.1 Skeleton (0xE34009) — LoaderEAGLSkeletons 0x44B2C0, CAnimSkeleton::DynamicLoadResolve 0x447BD0
A relocatable MIPS ELF (little-endian, e_machine 8). `.rel.data` only turns section offsets into pointers, so offsets
in `.data` are used as-is. Symbol `__Skeleton:::<name>` → SkeletonData `{u16 core checksum, u16 flags, u32, i32 bone
count, float* inv scales}` then BoneData[count] (0x70): `S f32x3, parent i32, Q f32x4 (x y z w), T f32x3, mirror i32,
InvBase 4x4`. `__Bone:::<skel>.<bone>` symbols point at a u32 bone index (names: "Bip20 L Thigh" …).
GetSkeletonFromList (0x44B280) matches `bStringHash(name)` (Bip20 = 0x02909F7C; the perp is "Bip23").
Bind pose check: local = diag(S)·R(Q) with T in the last row, global = local · parent (row vectors) rebuilds every
InvBase exactly (48/48 bones, including the scaled eye/jaw bones).

### 1.2 Animation bank (0xE34010) — CAnimBank::Initialize 0x448DB0, GetAnimFromBankByNamehash 0x448F20
`__AnimationBank` → `{?, count, ?, ?, anims*, names*}`; an animation is found by `bStringHash(name)`.
Types (EAGL4Anim/AnimTypeId.h): the data uses only 15 (compound), 10, 11, 20 and 21.
* **Compound (15)**: `{u16 type, u16 target checksum, AttributeBlock*, u16 channel count, u16 frames, channels[]}`;
  channels evaluated last to first (FnCompoundChannel::Eval).
* **DeltaLerp (10) / DeltaQuat (11)** (DeltaChan, decoder 0x48FF00): `{type, checksum, DeltaCompressedData*, u16
  frames, u16 dof index[]}` (one index per value / per quaternion). Data `{u16 dofs, u8 bits 4/8/16, u8, {min,
  range, start}[dofs]}` then a row of deltas per frame step, `v += q·range + min` (4-bit: low nibble first).
  Lerp between floor/ceil frames. The old car decoder agrees on all 747 car/cop tracks.
* **DeltaF3 (20) / DeltaF1 (21)** (FnDeltaF3 0x495DB0, FnDeltaF1 0x493F50): `{type, checksum, u16* dof idx, u16* key
  times (null = per frame), u16 keys, u16 animated, u8 bin power, u8 const count, pad}`, DofInfo `{phys min[c], phys
  range[c], u16 qmin[c], u16 qrange[c]}`, bins of 2^power keys (first key u16 physical, the rest u8 deltas):
  `phys = q·range/65535 + min`, `delta = q·(2·qrange/65535·range/255) + (2·qmin/65535·range − range)`; after the bins
  (2-aligned) the constant dof indices and (4-aligned) their floats.
* SQT pose buffer: 12 floats per bone, S at 0, Q at 4, T at 8 (Skeleton::ComputePoseBufferLength = bones·12).
  Characters animate Q of all bones (type 11), T of root + face bones (20) and two scalar dofs (21).

### 1.3 Character entity (0x37040) — CBasicCharacterAnimEntity::Init 0x44C5E0
BasicCharacterAnimEntityInfo (0x90 after the 0x11 pad): type (1 = BasicCharacter), instance/parent hash, play flags,
local matrix (scene space), LOD solid hashes[4], trans/quats/scale anim hashes, skel anim hash, skel hash, play speed,
delay, world object type, loop range. Init: CAnimCtrl flags from play flags (0x40 → 0x60, 0x20 loop, 0x10 ping-pong
else 8, 0x780 copied, +1 when skinned), time scale = speed (0 → 1) **× 0.5 for skeletal animation** (the banks are
15 fps; anim frame = scene seconds × 30 × scale), master delay = play delay. mBoneMapType 2 for 48 bones, 3 when the
skeleton is "Bip23" (FacePixelation follows its head). mKeepOnGround: the entity is lifted so it stands 0.05 m above
GetWorldHeightAtPointRigorous under bone 1 (Init: as found; updates: 50/50 with the previous height, probe raised 2 m).

### 1.4 Character solid — worldbone.efx (fx_2_0 at file offset 0x5310C0)
Materials use effect 2 = worldbone: 60-byte vertex `{pos, normal, colour, uv, blend weights f32x3, blend indices
f32x3}`. An index selects one of 16 palette bones stored as u8 at material +0x1E..+0x2D; the vertex shader reads
`BlendMatrices[index·4]` (16 float4x4) and blends in index order y, x, z. Lighting (vs_1_1, disassembled):
`facing = max(sat(dot(normalize(LocalEyePos − v0), N)), 0.01)`, `diffuse = DiffuseMin + DiffuseRange·facing`,
`light = Σi sat(dot(N, Li) + 0.3)·Ci` over two lights (LocalDirectionMatrix rows, LocalColourMatrix columns),
`oD0 = diffuse.rgb·light`; specular/envmap go to oD1 scaled by vertex colour red (0 for the characters) and are not
used by the pixel shader; ps: `tex · oD0`, alpha ×2. **Unskinned** position and normal feed the lighting.
Skin matrix = InvBase · global (Skeleton::PoseGlobalToSkin).
**CPU constants**: each material's light material comes from the solid's table (chunk 0x134013, 8 bytes per entry,
material byte +0x1D; 0xFF = DEFAULT) and sets DiffuseMin = MinScale·MinRGB (a = MinA), DiffuseRange = MaxScale·MaxRGB −
DiffuseMin (a = (MaxA − MinA)/4) as for the cars (sub_6C76E0). The characters use CHAR_SKIN (min 1.65, range −0.3),
CHAR_CLOTH (1.19 / 0.06), CHAR_LEATHER (1 / 0.6), CHAR_RUBBER (1, 0.95, 0.9 / 0.2..), CHAR_LATEX (0.53 / 0.5)
(`Harness nisload charlm`). The two lights are lights 0 and 1 of the world-model rig ShaperLightsCarsInGame
[? inferred from WorldModel add_lighting; the character's elSetupLightContext call was not traced]; vs_1_1 saturates
oD0. Ported in Character.shader (_MW_CarLightDir/Col0..1) + NisCharacter.

### 1.5 Shadow — CBasicCharacterAnimEntity::RenderEffects 0x449CF0
BoneMap (AnimEntity_BasicCharacter.cpp:25) feet 4/8, head 0x1C for types 2/3. Quad between the feet: parallel = unit
(left − right) · 0.25, perpendicular · 0.35, 1 cm above the entity's ground, colour 0x80808080, texture
CHARACTERSHADOW (InitCharacterEffects).

## 2. Event sequencer (0x3B811) — decomp Main/Common/EventSequencer.cpp, Libs/Support/Miscellaneous/CARP.h
Serialized UGroup 'CARP' `{tag, flags (bit1 embedded, bits 5.. group count), data count, offset/16}` + UData
`{tag (type<<16 | index), flags (bit0 indexed, bit1 embedded, bits 8.. size), name hash, offset from the record}`.
Types: `eE` engine `{name ref, n, system ids[n], system refs[n]}`, `eS` system `{initial, default, n, state ids, state
refs}`, `eT` state `{n, filter ref, stimuli[n], {end state, action ref}[n]}`, `eA` action `{name, tag count, time
count, duration, tags[], times[], event list refs[tags + times]}`, `el` event list `{n, pad3, {event id, size, data
offset (from the record), pad}[n]}`, `cS` static strings, `iN` engine name. References are tags.
* All ids are Jenkins lookup2 hashes (seed 0xABCDEF00) of names: stimuli "create", "start", "end", "stop", "pause",
  "resume", "unload", NISActivity's "START" (0xA6A56EA1); systems "car1", "cop1", "GLOBAL" (0x8DA18577); event ids of
  the class names (e.g. ENISCarRoll 0x3081837D). All 34 ids in the retail packs resolve; no state has a filter.
* Runtime (ported in `EventSeqEngine`): Create sends "create"; System::ProcessStimulus (exe 0x645A40): an idle system
  invokes at once, a busy one queues (QUEUE_SHALLOW only into an empty queue, QUEUE_ALLOW while 4 slots are free);
  InvokeStimulus fires the action's "start" tag list and runs Update; System::Update (0x644BC0) advances the action
  clock and FireTimedEvents (0x644350) fires lists with time in (prev, now] (a list exactly at the old time fires once on
  entry); at the duration TerminateAction fires "end", moves to the end state and pops the queue. Complete fires the
  rest of the timed lists then "stop".
* Tags fired from outside: the ICE camera fires `ICECut%d` at each cut (`ICECompleteEventTags` 0x47B520 fires the
  remaining ones on a skip) and `ICEEnd` when CDActionIce ends (camera_core.md §4, SelectAction 0x479900).
* NISActivity::SetDynamicData: an event's simable is the car of the system's channel (vehicle table keyed by the
  same hash); "GLOBAL" gets the activity.
* Event static data = the class's StaticData (decomp Generated/Events/*.hpp), fields from +4; `const char*` fields
  are `cS` tag references.

### 2.1 Event use (all packs) and status
| event | count | status |
|---|---:|---|
| ENISCarPitch / ENISCarRoll | 97 / 58 | ported in NisCarControl (§7): SetAnimPitch/Roll, NISCarTweaks (0x690EA0) triangle pulse to delta/2 at half time; delta = atan(dip / (box half extent / 2)) / π (AngleToAIAngle) |
| ENISCarShake | 111 | ported in NisCarControl: `cos(2π·phase)·delta` (bCos, 0x6910CE) with period `rate`, ramp in/out over `ceil(ramp/rate)` whole periods, for `time` |
| ENISLights | 86 | ported: cop lights / headlights of the channel's car (CarFlares + the NIS light bar) |
| ENISOverlayMessage | 60 | ported: FE package on the NIS canvas; [?] the message runs the package script of that name (FLASHTRIGGER) |
| ENISPixelate | 116 | ported: ctor 0x6231F0 sets mPixelationOn (0x9B0A74), mWidth/mHeight (0x904B3C/40); FacePixelation follows the "Bip23" head; PC pass FUN_006d49a0 draws the rect in 640×480 units. [?] block size 8 |
| ENISHideCharacter | 16 | ported: show/hide the character whose model is fAnimModelName (CUFFS01) |
| ENISScreenFlash | 1 | ported [?]: white flash fading over its length |
| ENISNukeSmack | 21 | ported: smackables within the radius of the world position are removed (NIS_NukeSmackablesWithinRange) |
| ENISPlayEffect / ENISStopEffects | 31 / 1 | ported: emitter group by lookup2(name) at the world position / KillAll |
| ENISDetach / ENISReattach, ENISBurnout, ENISNitro, ENISBrakelock, ENISNeutralRev, ENISSteering, ENISAeroDynamics, ENISCarDamageReset | 186/177/66/104/77/18/8/23/9 | ported in NisCarControl (§7) |
| ENISRain | 20 | ported: ctor 0x6233D0 = PrecipitationEnable 1 + SetOverRideRainIntensity (0x73C970) → `Precipitation.NisRain`; NisPlayer.Finish = NISActivity::Release → `NisReleased(saved)` |
| ENISTimeOfDay, ENISFakeFar | 78/41 | **no effect on PC**: the MakeEvent callbacks (0x62ECB0 / 0x62DEC0) only store the data and the destructors (0x623690 / 0x622DE0) only reset the vtable |
| ENISVisualLook | 4 | decoded (ctor 0x6236C0): IVisualTreatment +0x1DC fDesaturation / +0x1E0 fColourBloom override the heat look in UpdateHeat (0x74BFBE) while ≥ 0; fBleachFactor → nullsub_166; fFacingScale / fGrazingScale → eLightMaterial 0x93C0EAF6 +0x90 / +0x80 while in 0..2 (else the saved originals); fUseHDR unread. Published as `NisEventRunner.VisualLook*` [hook: renderer] |
| ENISMotionBlur, ENISRoadNoise | 25/4 | decoded: ____icemanager_isplayingsomething (0x8F9B28) = enable ≠ 0; Tweak_DisableRoadNoise (0x9B09B0) = value. Published as statics [hooks: renderer, audio] |
| ECameraPhotoFinish | 33 | ported: dtor 0x640410, only with one local player and exactly two racers: IVisualTreatment::TriggerPulse(0) (the white camera pulse, [?] 0.5 s here), SoundPause(true, 10), SetSoundControlState(false, 1) |
| ENISCopCarDoors | 30 | decoded: ctor 0x622B60 → sub_447390(door 0..3, pos, length, end) animates a door of the cop car; the port's cop models have no door parts — logged |
| ECellCall | 3 | decoded: dtor 0x62B180, languages 5/6/7/12/13 only, pushes SMS_Mailboxes.fng (front end) — logged |
| ENISWolrdGeometry, EPlayObjectEffect, EStopObjectEffect, EDDaySpeech, ESpawnExplosion | 1/5/1/1/2 | not ported (scenery groups, object effects, speech, explosions) — logged |

## 3. NISActivity (0x6EA270..0x6FD697)
States NONE, CREATING, LOADING, READY_TO_PLAY, PRE_MOVIE, PLAYING, POST_MOVIE, COMPLETE.
* **OnTask** (0x6F58D0): READY_TO_PLAY → Play() once `mStartPlayingNow && !mPause && mSuspensionSettle >= 31`,
  otherwise settle++ and `CAnimScene::UpdateTime(0)`. Ported: the scene holds at t = 0 for 31 frames [ours: capped at
  0.6 s real time; only when the scene has characters or events].
* **Play** (0x6F5750): EmitterSystem_OnStartNIS, elapsed = 0, the sequencer is (re)created
  (EventSequencer::Create 0x6459E0), state PLAYING, StartEvents (Reset + "START"), EAXSound::PlayNIS, NIS rev start.
* **UpdatePlaying** (0x6EA750): (paused → dT = 0) elapsed += dT; while anim time ≤ scene length (or ICE editor):
  CAnimScene::UpdateTime + AnimatedCars_Update; past the end → POST_MOVIE when a post movie is set, else COMPLETE.
  [ours] NisPlayer keeps its length rule (ICE track length / clamped scene length; camera agent's behaviour).
* **SkipOverNIS** (decomp NISActivity.cpp:822, 0x6EA960): only while PLAYING, ICE editor off, **never for Arrest
  (1) or Ending (2) NIS**, and not for mNonSkipableNIS (+0x3A0) unless SkipMovies (0x926144, config, 0); completes the
  sequencer, clears speech, ICECompleteEventTags, CAnimScene::JumpToEnd, EFadeScreenOn. The mSkipToNIS (+0x3A1) /
  mNISNoSkipTime / EPlayEndNIS branch is **dead on PC**: the ctor (0x6FD590) clears the string and nothing writes it
  (the only other access is the dtor's EPlayEndNIS at 0x6FD078). Ported: NisPlayer.NonSkippable.
* **Load** (0x6F51F0):
  ```
  loadStartTime = RealTimer·0.00025; mNISType = type; mSceneHash = bStringHash(scene)
  mNonSkipableNIS = scene starts (bStrNICmp) with IntroNisBL | IntroNisDD | IntroNis10 | EndingNIS
  mDDayNIS = IntroNisDD*; mBlackListNIS = IntroNisBL*; mCareerNIS = IntroNisBL* | IntroNis10* | EndingNIS*
  if (Sim user mode == 0 || type != Arrest) && !AnimCfg_DisableAnimations && _mUserMode != 1
     && (Tweak_EnableNISMoments || type != Moment || ICE editor):
      dist = 75; m = GetClosestMarker(sceneHash, mNISPosition, &id, &dist, mNISDirection as bAngle)
      if m: StartLocationInRenderCoords(m.pos, m.angle)
            if ICE editor off && type == Arrest: m.z = GetWorldHeightAtPointRigorous; StartLocation again;
                                                 NIS_NukeSmackablesWithinRange(start, 10 m)
            camera track = ChooseGoodSceneCameraTrackIndex(sceneHash)        // only when a marker was found
            if type == Moment: dist < 75 → CAnimMomentScene, PLAYING; else COMPLETE
      mStartPlayingNow = playNow; bStreamingPositionFromICE = 1
      PrepareVehicles(SpecialCarList of the scene); Arrest/Ending without a pre-movie: mUsingFEngOverlay, ESndGameState(11)
      EFadeScreenOn(false); mSequencerID = stringhash32(scene)
      no pre-movie → CREATING + UpdatePreloading, else EPlayRaceMovie → PRE_MOVIE
      DisableSceneryGroup("SCENERY_GROUP_DOOR") when it exists
  else COMPLETE
  ```
* **Candidate data** (GLOBAL\InGameB.bun 0x80037050 → 0x37070, CAnimCandidateData; `Core/NisContent.cs
  NisCandidateData`, `Harness nisload`): 1 race-start candidate, 41 moment scenes (all 26 Arrest scenes →
  BustedMomentMarker, IntroNisToll01 → TollboothMarker, the WM* world moments → their marker), 53 special car lists
  (Arrest → ONE_COP; IntroNisBL* → TRAFFIC_MIX*, EndingNis03/04, FMV scenes). TrackPositionMarkers (L2RA 0x34146,
  `TrackMarkers`): 328, incl. **194 BustedMomentMarker**, 36 TollboothMarker, 14 GarageMarker.
* JoyHandle: action id 50 of the "FEngHud" controller config skips (offline only). [ours] Enter/Space/Esc.
* PrepareVehicles: ONE_COP / LOTS_OF_COPS spawn `copmidsize_nis` (`..._ld` for cops 2..8) as "cop%d"; ONE_SPORT_COP
  `copsport` with COP_CROSS; TRAFFIC_MIX1..9 spawn SpecialCarList entries as "cop1..8". [ours] NisPlayer keeps
  COPMIDSIZE for every cop channel.
* Destructor: cop channels are killed, cars un-animated, MNISComplete (or EPlayEndNIS when skipped to an end NIS).

### 3.1 Arrest location (NISListenerActivity::MessageBusted, AnimChooseArrest.cpp:225; Load above)
ChooseArrestLocation: the road centre (WRoadNav kPathCop at the player, (left + right)/2) with angle = player heading
+ 90°. Load then moves the scene to the **closest BustedMomentMarker** (distance + 25 m per turn of heading difference, no
distance limit), at the ground height there, nukes the smackables within 10 m and picks the camera track with
ChooseGoodSceneCameraTrackIndex. The scene frame is RotZ(angle − 90°) at the origin (CAnimLocator::
GetInitialAnimMatricies); car 1 (the player, added as "car1") is put onto its track by its first SetNISPosition
(SetVehicleOnGround), i.e. the player car is moved to the marker. Ported in NisPlayer.Busted (it used to keep the
player's own spot and heading).

### 3.2 The NIS cars are immobile in collisions (the stacked cop car fix)
RBVehicle::ModifyCollision(RigidBody) (decomp RBVehicle.cpp:171): `if (GetVehicle()->IsAnimating())
myMoment.MakeImmobile(true)` — an animating car has infinite mass against every other body; two animating cars both do,
so their contact resolves nothing. The port's NIS cop used to be spawned (CarSpawner's raycast) and placed next to the
pursuit cops; both bodies were dynamic and SetNISPosition pins the NIS car's x/z each step, so the solver could only push
it **up**: it climbed onto the pursuit cop. NisCarControl now marks its body immobile through a contact-modification
handler (inverse mass/inertia scale 0 against non-NIS bodies, contacts ignored between two NIS cars), registered after
CarContacts' handler; world contacts are untouched. Together with §3.1 (the arrest plays at a marker, away from the
pursuit cars) this removes the stacking.

## 4. World animation (TRACKS\L2RA.BUN)
Chunks: 0x37090 WorldAnimInstance[] (0xA0: unique id, tree hash, section, play flags, speed, master/loop delay,
range, triggers, LODs, named range, event track, track no/dir, instance matrix, bbox), 0x37150 instance control list,
and per tree an anim bank 0xE34010, entities 0x37080 (WorldAnimEntityInfo 0x70: type 3, instance/parent hash, model
hash, LODs, tree hash, anim hash, content flags 1/2/4 = _t/_q/_s, parent index, local matrix) and a marker 0x37110
(named ranges, all empty). 11 trees: 7 tower crane arms, a sailboat, a cargo ship, a blimp, an airliner (3 parts);
39 animations, all resolve (`Harness nis world`).
* CWorldAnimEntity::Init (0x44F340): the root (tree hash == instance hash) has no model; channels are
  `bStringHash("_t"/"_q"/"_s", anim hash)`; flags 0x28 = linear + loop; speeds 0.005–0.01 (frames = s·30·speed).
* UpdateTimeStep: only while the tree's section is visible; the entity's local matrix becomes bone 1 of the identity
  "ROOT" skeleton (GLOBAL\InGameB.bun) posed by the channels → world = instance matrix · pose.
* Models (Harness `nis worldmodels`, all 720 stream sections): XO_TOWERCRANEARM_1B_00, XO_CARGOSHIPA_1B_00 and
  XO_BLIMP_1B_00 are in the shared section X0. The airliner entities name XO_AIRLINERA_1B_LB_00 (X0 only has _10/_11/
  _12) and the sailboat's model (0x1490A53E) is in no section [?: they may not draw in the exe either]. The static
  scenery copies of these models (P60/P77/R41 cranes, the Z0 airliners, H51/S98 blimps…) carry cull flag 0x10, so the
  streamer does not draw them: no double drawing.
* The instance section numbers (P37, P20, R01, R36, J11…) are visible-section ids, not all of them stream sections.
* Airliner / sailboat resolved: CWorldAnimEntity::Init (decomp AnimEntity_WorldEntity.cpp:135) builds
  `WorldModel(lods = {mModelHash, mLODB, 0, mLODZ})` and every retail entity has LODB = LODZ = 0 (`Harness nisload
  wmlod`). The airliner's XO_AIRLINERA_1B_LB_00 (0xF390CD1A) and the sailboat's XO_SAILBOATA_VC_1B_00 (0x1490A53E) are
  in no stream section (X0 has only XO_AIRLINERA_1B_LB_10/11/12 and the sailboat's _1C/_1Z LODs, which no entity names),
  so their eModels never resolve and **the exe draws neither**. The port matches (Missing) — not a loader gap.
* Ported in `Runtime/Nis/WorldAnimations.cs` (self-starting [ours], needs a WorldStreamer; X0's models read once on a
  worker thread). IsSectionVisible = the tree's stream section is loaded; [ours] an id that is no stream section draws
  within 1500 m of the camera. The full CWorldAnimCtrl clock and the Init rules: §8.3.

## 5. Files
* Core: `EaglAnim.cs` (ELF, skeleton, bank, channel decoders, pose math), `NisContent.cs` (bundle content,
  character entity, skinned solid), `EventSequencer.cs` (pack + runtime), `WorldAnim.cs`.
* Runtime: `Runtime/Nis/NisCharacter.cs`, `NisEvents.cs`, `WorldAnimations.cs`; `Game/NisPlayer.cs` (activity flow).
* Shaders: `Character.shader` (worldbone.efx), `CharacterShadow.shader`, `Pixelate.shader` (always-included list).

## 6. Unknowns [?]
* SetNISPosition's use of the pitch/roll/shake outputs (units, axes); the FacePixelation block size; the overlay
  message → FE script mapping; the worldbone light rig (ShaperLightsCarsInGame assumed).
* UpdateLoading (0x6F7230): the sound-state names per NIS kind (NIS Play Arrest / Intro / Story / BLK, NIS Load DDay)
  are decoded, the rest (car list wait, FE overlay timing) is not ported.
* The race-intro marker search (GetClosestMarker for intro scenes: only IntroNisToll01 has a marker type) and the
  race-start candidate camera track (1 entry) are not wired into RaceIntro.
* TriggerPulse's default length (visual-treatment attribute +0x44) for ECameraPhotoFinish.

## 7. The NIS car controller: SuspensionSpline + EngineSpline (Runtime/Physics/NisCarControl.cs)

### 7.1 How the exe drives a NIS car
* NISActivity::AddCar → `IVehicle::SetAnimating(true)` (PVehicle 0x686410 reloads the behaviours). While animating,
  PVehicle::LookupBehaviorSignature (0x67F0D0) gives SUSPENSION = "SuspensionSpline", ENGINE = "EngineSpline",
  INPUT = "InputNIS", RESET = none (not for CHOPPER) — traffic_physics.md §1.
* CAnimScene::AnimatedCars_Update (AnimScene.cpp:742) advances each car's track (CAnimCtrl, time scale 0.5) and calls
  AnimatedCars_SetMainAndWheels: the root-bone matrix × the scene rotation/translation, **y = ground height + 5 m**
  (GetWorldHeightAtPointRigorous), then `INISCarControl::SetNISPosition(matrix, initial = !HaveLastCarPosition, dT)`;
  a failed initial call retries next update.
* SetNISPosition (0x691110; decomp SuspensionSpline.cpp:621-702, constants checked: 1/2π at 0x890964, 1/720 at
  0x894228): NISCarTweaks(dT); **initial**: PVehicle::SetVehicleOnGround(position, forward) (0x685810), speed 0, zero
  velocities, RestoreState. **Otherwise**: x/z position and x/z velocity (Δpos/dT) from the track, angular velocity y
  = Δheading/dT (Atan2r of the forward, no wrap), pitch/roll = asin of cross(body up, track up).x/.z clamped to
  ±mConstraint/2 (mConstraint 60°, SetConstraintAngle clamps 0..80) with MultZRot/MultXRot by the excess, then the body
  turned about its own up to the track heading (OrthoInverse, SetYRot, Mult). The car's height, pitch and roll are
  physics.
* SuspensionSpline::OnTaskSimulate (0x6B34A0): SetCOG(0, 0); per tyre Locked = mBrakeLock{Front,Rear}, Burnout = the
  burnout speed on the driven axle (TORQUE_SPLIT ≠ 0 front, ≠ 1 rear); IInput gas 1 with a burnout else 0.5; brake 1
  with both locks, e-brake 1 with the rear only; DoWheelForces (0x6AE710): DoSteering (0x691860: the wheels follow the
  slip, `atan2(v.x, |v.z|)` (sign flipped in reverse) × (1 − |v.z·0.02|·0.8), blended with the ENISSteering angle by
  its weight, ±0.125 turn), spring `k·x·(1+p·x)` + digressive shock (SHOCK_VALVING/DIGRESSION) + sway, blow-off over
  SHOCK_BLOWOUT·9.81·mass, load `max(4·d−3, 0.3)·spring`, the "lateral force" `−m/4 · Δv/dT` per wheel (limited to
  DYNAMIC_GRIP·load above STATIC_GRIP·load) at the contact point; **only the body-up part of the summed force and the
  pitch/roll part of the summed torque reach the body**; ModifyYPos past full bump. Tire::UpdateLoaded: the wheel
  turns at road speed + burnout (0 when locked). DoAerodynamics(state, 0, gNIS_AeroDynamics) when ENISAeroDynamics set
  it (0x937800).
* EngineSpline (0x6A1AC0): rpm only — neutral rev (ENISNeutralRev: `idle + (red − idle)·throttle`, approached at
  `speed·(red − idle)` rpm/s, gear N) or `idle + |diff ω|·ratio·final·(1 − idle/red)` through Engine_SmoothRPM, auto
  shift as EngineTraffic, reverse/first from ±π rad/s; IInput gas = the rpm ramp (for the sound).
* The events (event ctors/dtors): ENISBurnout 0x639190 → SetBurnout(fSpeed); ENISBrakelock 0x638FB0 →
  SetBrakeLock(front, rear); ENISSteering 0x63A3F0 → SetSteering(fDegrees/360, fBlendWeight); ENISCarPitch 0x6394D0 /
  ENISCarRoll / ENISCarShake 0x639890 → SetAnim*; ENISNeutralRev 0x639F20 → SetNeutralRev(on, throttle, speed);
  ENISNitro 0x63A140 → SetNitro; ENISAeroDynamics 0x62DA50 → gNIS_AeroDynamics; ENISCarDamageReset 0x639350 →
  IDamageable::ResetDamage; **ENISDetach (dtor 0x639C70) → SetAnimating(false)** (fSpeed is not read on PC): the car's
  own behaviours take over and the track no longer reaches it; **ENISReattach (dtor 0x63A320) → SetAnimating(true)**,
  0x447770 clears HaveLastCarPosition (the next pose snaps it back onto the track), fRepairDamage → ResetDamage.
  Detach/reattach pairs bracket camera cuts in the WM scenes (e.g. 4.66 → 8.13 s).
* DrawCar::OnService (0x6B731F) writes GetAnimPitch/Roll/Shake into the render packet (+0x94/+0x98/+0x9C on PC);
  CarRenderConn::UpdateEngineAnimation (0x746208) uses them as the body's pitch, roll (over the engine-torque roll) and
  shake angles, which UpdateRenderMatrix (0x747050) sums as radians. SetAnimPitch's delta = AngleToAIAngle(bATan(half
  extent / 2, dip)) = atan(dip / (half/2)) / π.

### 7.2 What the port does
* `NisPlayer.AddRoll` with a track starts a `NisCarControl` on the car (`NisPlayer.PhysicalCars`, default on): the
  PlayerCar is disabled, the body stays dynamic, and the controller runs SetNISPosition + SuspensionSpline +
  EngineSpline each fixed step, sampling the track at the scene time (`NisPlayer.SceneTime` + the fixed steps since the
  frame × `NisPlayer.ClockRate`, 0 while READY_TO_PLAY holds the scene at 0: the 31-tick suspension settle now really
  settles). Finish ends the controller (SetAnimating(false)), then the old grid placement runs. NIS events reach it
  through one line in `NisEventRunner.Handle` (`NisCarControl.HandleEvent`).
* `[ours]` The PlayerCar frame: the arms sit at the wheel visuals' x/z, RIDE_HEIGHT − static sag above the car origin
  (which rests on the road), the PlayerCar's centre of mass stands in for SetCOG(0, 0), the box collider's half
  extents for the RigidBody dimension. Wheel visuals move with compression − sag, steer and spin; the pitch/roll/shake
  tweak rotates the "Body" child `[?: axes and signs]`. Rpm/gear/wheels go to the disabled PlayerCar through
  SetExternalSimState; gas/brake/nitrous through its Input.
* `[ours]` The heading rate is wrapped (the exe's unwrapped Δheading spikes for one step at ±180°); the pitch/roll
  excess (measured against the track's up) is rotated off about the world axes `[?: MultZRot/MultXRot frame]`; the initial
  placement is PVehicle::SetVehicleOnGround at the track point (y = GetWorldHeightAtPointRigorous + 5) and waits (car held
  still) while there is no world collision under it (HaveLastCarPosition −1 retry).
* `[ours]` While detached the car coasts with no input (the player's pad is not read until the NIS ends); when it is
  handed back a blown or totaled PlayerCar gets ResetDamage (else the chassis keeps it braked).
* Cars without a track (generic intros that do not name the car) still roll kinematically to the grid.
* Wheel steering is drawn as CarRenderConn::UpdateSteering (0x7459F0): the drawn angle follows the sim's at ecar
  SteerSpeed °/s, clamped to ±MaxTireSteer (90° / 180 °/s for nearly every car; `Harness nisload steer`).
* Immobile in collisions (RBVehicle::ModifyCollision MakeImmobile, §3.2).

## 8. This round: the animation core 1:1 (AnimScene / AnimPlayer / AnimCtrl / WorldAnim* / NISActivity / ICE)

### 8.1 Scene header and clock (CAnimSceneData, AnimScene.hpp:25-39; Core/Nis.cs)
`{+0 hash, +4 name[16], +0x14 description ptr, +0x18 SceneType, +0x1C HaveLayout, +0x20 HaveCarAnimation, +0x24
NumberOfCars, +0x28 StartFrame, +0x2C VanishFrame, +0x30 SeeulatorOverlayName[16]}` (the name was read as 28 bytes).
Harness `nis hdr` dumps them: the 26 Arrest scenes are **type 2**, IntroNis01..09 / GenericStart / IntroNisToll01 / the
WM* moments **type 0**, the Replay* scenes type 3, every IntroNisBL*, IntroNisDD, IntroNis10, EndingNis* and FMV* **type 4**
(world coordinates); every StartFrame is 0; HaveCarAnimation = 1 wherever there are tracks; Flythrough/IntroNisBL03/04/
05/06/10/11/12/13/14/IntroNisDD carry the overlay "FLYBY" (TheVisibleSectionManager.ActivateOverlay — a visible-section
overlay, not ported [hook: the world streamer owner]).
* **Clock** (BindToGame :269-279, UpdateTime :401-442, IsFinished): `_t` starts at StartFrame/30 and the NIS completes
  once `_t > VanishFrame/30` (strict; a moment scene at `≥`). The old clamps (4-9 s intros, the arrest stream's length,
  the ICE track length) are gone. The ICE parameter is `(t − start)/(length − start)`, unclamped; a key is only valid for
  0..1 (ICETrack::GetCameraData), outside it the camera keeps its last pose.
* **Scene frame** (CAnimScene::Init :486, CAnimLocator): SceneType 0/2/3 → the start line (origin dropped onto the
  ground, RotZ(angle − 90°)); 1/4 → the world origin, identity. The old "|x| > 400 → world" guess is gone.
* **Car pose** (AnimatedCars_SetMainAndWheels :666-694; `NisScene.PoseFull`): bone 1 of "ROOT" = the full _t/_q pose
  (the track's own height, pitch and roll). The NIS car frame is X forward, Y left, Z up: Unity rotation = sceneRot ×
  GameQuat(q) × Ry(90°), GameQuat(x,y,z,w) = (−x, −z, −y, w) (the y/z swap flips handedness). `NisPlayer.ScenePose`.
  NisCarControl then sets y = GetWorldHeightAtPointRigorous(track point) + 5 (the track height when no face is found) and
  SetNISPosition measures pitch/roll against the track's up.
* **Entities** take the previous frame's time delta (mTimeDelta, one update behind the clock); the scene's `_prevDt`.
* **Pause** (CAnimScene::ChangePlayStatus, NISActivity::Pause mPause; QuickGame::OnManageTime): the clock, the cars and
  the camera hold while `Time.timeScale == 0` (the FE pause) or `NisPlayer.SetPaused(true)`. (It used to run on unscaled
  time.)

### 8.2 Cars (AnimScene.cpp:540-787)
* `InitCarAnimStatesFrom*`: every car1..8 the race gave the scene is **Deactivated**; `CreateCarAnimationControllers`
  activates only the channels with a _t or _q track (`NisScene.HasTrack`). Port: `NisPlayer.AddDeactivated` hides the
  car (renderers off, body still) for the scene and puts it back where it was at the end — the 30 m "roll-in" of
  untracked cars is gone. Nothing is driven with HaveCarAnimation = 0.
* `AnimatedCars_UnBind` / the dtor: SetAnimating(false) — a tracked car **stays where the track left it** (Finish no
  longer teleports it back to its pre-NIS spot); cops (slots > 7) are deactivated (destroyed here).
* **SkipOverNIS → CAnimScene::JumpToEnd** (:384-386 → AnimatedCars_SetTime :713-724): every tracked car snaps to its
  final track pose with an initial SetNISPosition (`NisCarControl.JumpToEnd`), entities to their end frame.
* **PrepareVehicles** (NISActivity.cpp:654-746; `NisPlayer.PrepareVehicles`): the scene's SpecialCarList (CAnimCandidateData)
  — ONE_COP "cop1" = copmidsize_nis; LOTS_OF_COPS cop1 + cop2..8 copmidsize_nis_ld; ONE_SPORT_COP cop1 = copsport
  ([?] COP_CROSS customization not applied); TRAFFIC_MIX1..9 the AnimCandidates.cpp:7-60 tables (e.g. list 4 cop1 =
  eclipsegt, cop2 = imprezawrx). The model comes from the pvehicle's MODEL (VehicleSpecs). A list entry without a track
  would sit at the origin forever in the exe and is not spawned here (logged). Arrests therefore get **one** cop
  (COPMIDSIZE was spawned for every CopN track before).
* ChooseGoodSceneCameraTrackIndex runs only when Load found a marker (arrest / moment / IntroNisToll01); otherwise Load's
  argument: the race-start candidate's CameraTrack for intros ([?] CAnimChooser::ChooseAnim is stripped), 0 for the rest.
  `ICEGroup::GetTrack("Track %d")` has no fallback: a missing name means no ICE camera (it used to fall back to the index).

### 8.3 CAnimCtrl / CWorldAnimCtrl (Core/EaglAnim.cs `AnimCtrl`)
AdvanceAnimTime ported literally for both (AnimCtrl.cpp:140-220, WorldAnimCtrl.cpp:183-296): frames = dt·30·scale; the
master (0x80) and local (0x100) delays compared in frames with the overshoot carried into the clip (fmod); linear / loop
(range subtraction, flag 0x40 loop range), ping-pong (reflect at the end, clamp at the start), the world controller's
triggered (0x800: pause at the ends), reverse (0x1000), PlayState gate and speed modifier; SetEvalTime's ping-pong
reflection; Play/Pause/Stop/JumpTo*ForTrigger. **PC divides the delay elapsed by 30** (0x8934E0 at 0x449648; the GameCube
decomp's "/ 3" is not the PC's), so a delay lasts delay/scale seconds. FnAnim::GetLength = the frame count (`EaglAnim.Length`
= NumFrames, was NumFrames − 1), taken from the **last-created** dof (t, q, s order). Characters: `NisCharacter.Ctrl`
(flags from the entity, master delay = play delay, loop range × the halved scale), dof 1 quats / dof 2 scale / dof 0
skeletal evaluated in that order, Init's ground probe at the pelvis itself (the +2 m raise is UpdateTimeStep's), an entity
with no animation at all is dropped (Init false), `Advance(dt)` / `SetTime(t)`. World entities: `WorldAnimations.MakeCtrl`
— SetMasterDelayTime/SetLocalDelayTime always, play flags 0 → 0x28, 0x40 → SetLoopRange, **0x200 / 0x400 → bRandom with a
fresh seed 0x69BABE69 (deterministic 0.8260115 × range)**, a start/stop trigger → 0x810 and no auto-play, the
InstantiateAnimTree track-number filter (`WorldAnimations.TrackNumber`), named range → flag 0x40 only; the master delay of
tree 0xDA6A4E92 (0.1 s at speed 0.005) is now 20 s as in the exe. UpdateTimeStep: the clock stands while the tree's section
is not visible ("visible" = the stream section is loaded, else [ours] within 1500 m), a stopped entity's model stays hidden.
`ControlWorldAnim` / `ResetWorldAnimations` / `CloseAllGarageDoors` exist (no-ops on L2RA: no EN_TollBoothArm / EN_Chopshop
trees).

### 8.4 NISActivity (0x6EA270..)
* **Construct** refuses a second NIS (`NisPlayer.Refused`: the request's completion fires at once) and MessageBusted
  ignores a bust while a NIS plays (Begin used to cut the running one).
* **UpdateLoading** (0x6F7230): an arrest stays LOADING for ≥ 2 s of real time after Load, behind the load fade
  (EFadeScreenOn(false) at Load; the first UpdatePlaying posts EFadeScreenOff "30_IN", 600 ms): `_loadHold`, `Fade()`.
* **Moment NIS**: CAnimMomentScene — camera only, no characters, no sequencer (PlayMoment no longer runs StartActivity).
* ENISHideCharacter → GetAnimEntityWithModelName: the **last-added** entity with that model (was all of them).
* Not ported (other owners, listed as hooks): the sound states at READY_TO_PLAY (NIS Load DDay 7 / NIS Play Arrest 10 /
  NIS Play BLK 9 / NIS Play Intro 7 / NIS Play Story 6; the audio code sets NisArrest/NisIntro from NIS creation),
  ESndGameState(11) around the FE overlay, EmitterSystem_OnStartNIS, Speech::Manager::ClearPlayback and the cop-speech
  ReleaseResource on arrest/skip, the pre/post race movies (EPlayRaceMovie, MNotifyMovieFinished), DisableSceneryGroup
  ("SCENERY_GROUP_DOOR"), bStreamingPositionFromICE (the streamer loads around the first ICE eye, GetNISStartLocation),
  the HUD controller's action 50 (pad skip), ForceAllSceneryDetailLevels at the end, MessageBusted's EQuitToFE /
  EPause when no road is found ([ours] the player's spot).

### 8.5 Open [?]
* ChooseArrestLocation's angle uses `xform.v0` (axis 0 of the body matrix = the car's right vector) through
  `1 − Atan2a(x, z) + 0.25`; the port's `heading + 90°` convention was kept (the marker's own angle overrides it once a
  marker is found: only the 25 m/turn bonus and the no-marker fallback depend on it) — verify in the exe.
* The conical constraint composite (physics.md §25), the race-start camera-track choice (CAnimChooser::ChooseAnim), the
  "FLYBY" visible-section overlay, the cop-door animation (StartCopDoorAnim timing is decoded in AnimPlayer.cpp:521-558;
  the port's cop models have no door parts).

## 9. Round 9: Skeleton.cpp, CsisEventChannel.cpp, SimpleModelAnim.cpp

### 9.1 EAGL4Anim/Skeleton.cpp (exe range 0x49F410-0x4A10CF)
Callers found by a full .text scan for E8/E9 rel32 + absolute pointers (vtables / .data):
* **Live**: GetStillPose 0x49FF90 (CAnimPart::Init 0x44AB10 and the pose code 0x4A5EC0..), PoseSQTToGlobal **0x49FCF0**
  (CAnimCtrl::UpdateAnimPose 0x445FA0, 0x445F30, 0x446080, the world-entity poses 0x4481A0/0x448210/0x4482B0), the matrix
  multiply 0x4A05A0 (through the pointer 0x8EE51C: D3DXMatrixMultiply(out, a, b) = a·b). Port: `EaglSkeleton.StillPose /
  PoseGlobal` (Core/EaglAnim.cs) and `WorldAnimSet.Pose` (Core/WorldAnim.cs), now 1:1:
  * GetStillPose writes per bone S (bone +0), **slot 3 = SkeletonData +0xC[i] (bone-length scale array) or 1.0 when the
    pointer is 0**, Q (+0x10), T (+0x20), **slot 11 = 1.0**.
  * PoseSQTToGlobal builds each local matrix with 0x493FF0 (args S, Q, T; row i of R(Q) × S[i]; T in row 3) **from the
    quaternion as evaluated — no renormalisation** (the port used to renormalise [ours]; the DeltaQuat lerp of
    DecompressValuesIndexed is not normalised either), then multiplies **column 0 (m[0], m[4], m[8]) by slot 3**, then
    global = local · global[parent] for parent ≥ 0. The masked variant only skips bones outside the BoneMask.
  * `Harness anim9` (ProbeAnim9.cs, all 126 banks / 66 skeletons in the install): no skeleton has the +0xC array, no channel
    writes slot 3 or 11 (slots written: S 0-2 ×219, Q 4-7 ×2811, T 8-10 ×2820), so the column-0 factor is 1 in retail.
* **Dead** (no call, no pointer anywhere in the image): 0x49F410 (0x5A4), 0x49F9C0, 0x49FAD0, 0x4A01B0, 0x4A0210, 0x4A02D0,
  0x4A0440, 0x4A0500 (→ its callee 0x4A03B0), 0x4A0600-0x4A0640, 0x4A0980-0x4A0AD0, 0x4A0B90-0x4A0F10 (→ 0x4A0B10, 0x4A08A0,
  0x4A08F0), 0x4A1070 (→ 0x4A0F60), 0x4A10A0, the nullsubs. **0x49FE50 = PoseGlobalToSkin** (skin = InvBase · global, then the
  3×3 transposed in place) is reached only from 0x4A1150 / 0x4A84F0, which are unreferenced: dead. (The bone-mask builders,
  mirror / bone-length-scale / ortho-scale helpers of Skeleton.h are among the dead ones.)
* The rest of the range is other units placed there by the linker: 0x4A06B0 = FnCompoundChannel ctor (vtable 0x895DF0,
  type 0xF), its vtable entries 0x4A06E0/0x4A06F0/0x4A0730/0x4A0740, 0x4A07E0 (from AnimBank::NewFnAnim 0x4A8710), the small
  accessors 0x4A05F0/0x4A07B0/0x4A07C0/0x4A07D0, 0x4A0680 (pointer at 0x895568), 0x4A1000 (.data 0xA68BCC, static init) —
  covered by the compound-channel / bank port (`EaglAnim`, `EaglAnimBankReader`).

### 9.2 EAGL4Anim/CsisEventChannel.cpp (exe range 0x48EE40-0x48FD7E)
* CsisEventChannel::Eval 0x48F990 → HandleEvents 0x48F830 (bit-unpacks each event's Csis parameters from the dictionary and
  calls the event handler) is reached only from FnCsisEventChannel::Eval 0x494790 (vtable 0x895770 slot +0x24), whose ctor
  0x494730 (type 0x18 = ANIM_CSISEVENT) is called only from the NewFnAnim type switch 0x4A9D10 (0x4A9F35). `Harness anim9`:
  the shipped banks hold only type 15 (compound, ×852) with channels 10 ×475, 11 ×343, 17 ×2, 20 ×92, 21 ×40 — **no type 24**,
  so the Csis event channel never exists on PC: **N/A (dead with the shipped data)**.
* Type 17 (DeltaQ) occurs only in `Rotate_360_q` of GLOBAL\InGameB.bun / INGAMEC.BUN, whose name hashes (0x968F4DC6 /
  0x7799E3F6) appear nowhere in the exe or the track / NIS / global bundles: unused, not decoded.
* Also in the range: 0x48F2D0-0x48F700 = FnCompoundChannel's methods (vtable 0x895DF0 + 0x18..0x38: the channel FnAnims are
  created last to first by 0x48F320 and evaluated last to first by 0x48F700 — as `EaglAnim.Eval`) and 0x48F6D0 (the bank's
  release, from 0x4AA6D0). The other functions of the range (0x48EE40-0x48F2B0, 0x48F740-0x48F820, 0x48FBF0-0x48FD10) have no
  reference: dead.

### 9.3 World/SimpleModelAnim.cpp (0x73D2B0-0x73D50C) — live, ported
See world_render.md "SimpleModelAnim". Init 0x73D2C0 (eInitEngine 0x500A72), Reset 0x73D300 (BeginWorldLoad 0x662AD7),
Update 0x73D320 (ePreDisplay 0x504C5B), Animate 0x73D3C0 (eViewPlatInterface::Render 0x6DAA2B), IsSimpleAnim 0x73D4E0
(sub_6D7C40, the static batch builder). Port: `Runtime/Nis/SimpleModelAnim.cs`.

### 9.4 Fix in WorldAnimations
`SectionVisible` tested `Section.Data != null`, but WorldStreamer clears Data at the end of BuildRoutine, so a tree in a
stream section was "visible" only while that section was being built (its clock stood and its model hid afterwards). Now
`Root != null || Data != null` (= loaded, the §4 rule).
