# NFS Most Wanted (2005) — reverse engineering notes

Target: `NFSMW\speed.exe` (v1.3, 2006-01-06, 6,029,312 bytes, SHA-256 `80774c2e…253c`, PDB name
`NfsMWCollectorsRelease.pdb`). Analysed statically with REA (Ghidra 12.1.4 provider) plus a persistent
headless Ghidra project (`tools/ghidra.sh`, `tools/ghidra_scripts/DecompileAt.java`) for functions that
auto-analysis missed. Decompiled C is in `decompiled/` (`loaders/`, `core/`, `codec/`, `vlt/`).

Everything below was confirmed against the game data with the readers in
`NFSMWUnity/Assets/NFSMW/Core` and the harness in `NFSMWStaging/Harness`.

## Containers

| Format | Where | Notes |
|---|---|---|
| bChunk | every `.BUN`/`.BIN` | `[u32 id][u32 size][payload]`; bit 31 set = container. Payloads are aligned in memory: headers to 16, GPU buffers to 128 (`(p + 0x87) & ~0x7F` = align(p+8, 128)). Padding chunks have id 0 and fill bytes `0x11`. |
| Chunk loaders | static initialisers 0x886000–0x88D700 | `bChunkLoader(id, load, unload)` ctor at **0x45D600**; 30 registrations listed in `chunkloaders.txt`. |
| JDLZ | `.lzc`, texture blocks | `JDLZ 02 10 00 00, u32 out, u32 in`; two flag streams (see `Core/Jdlz.cs`). Decoder **0x64DB40**. |
| HUFF | texture blocks | EA Huffman: canonical lengths, move-to-front symbol list, 8-bit fast table, escape symbol for runs/literals/end, optional delta filters (types 0x30FB/0x32FB/0x34FB). Decoder **0x64DCA0** (global `DAT_0092592c` is never written, always 0). Port: `Core/EaHuff.cs`. |
| Dispatcher | | **0x65B5C0** picks JDLZ / HUFF / RAWW / COMP per block; compressor **0x65B350**. |

## World (TRACKS)

* `L2RA.BUN` chunk **0x34110**: 720 × 0x5C streaming records — name[8] (`D46`), number (= letter·100 + n),
  offset +0x14, size +0x18 into `STREAML2RA.BUN`. `X0` = shared solids, `Y0` = shared textures (105 MB), `Z0` = sky/panorama.
* Section contents: texture pack, solid list, scenery, plus 0x34027, 0x34159, 0x3B801, 0x3BC00, 0x80036000 (not decoded yet).
* **Scenery 0x80034100** (loader **0x7311F0**): 0x34101 header (+0x0C section number), 0x34102 SceneryInfo[0x48]
  (name[24], 4 LOD solid hashes, 4 runtime pointers, radius @+0x38), 0x34103 SceneryInstance[0x40]
  (16-aligned: bbox min/max, flags @+0x18, position @+0x20, 3×3 rotation as s16/8192 @+0x2C (rows = local axes),
  info index @+0x3E). A section `N` also carries group `N−40`: world-baked terrain/road chunks vs. instanced props.
* Instance flags (from the data): bit 18 = baked shadow layer (all `SHD_*`), bit 23 = race barrier
  (`XO_TrackBarrierPlayer/Traffic`, hidden in free roam), bit 25 = breakable prop, bit 1 = freeway signs/overpasses.
* 77,783 instances, 99.93 % resolve to a solid (section-local or X0); 46,323/46,351 material textures resolve.

## Solids (geometry)

Loader **0x5075A0** → per-solid **0x5070E0** → mesh **0x6E3EE0**.

* 0x134011 solid header (16-aligned): +0x10 name hash, +0x19 texture count, +0x1A shader count,
  +0x20/+0x30 bbox, +0x40 4×4 matrix, +0xA0 name.
* 0x134012 texture hashes (8 bytes each), 0x134013 shader hashes, 0x13401A markers (0x50: hash + matrix;
  car markers are light/exhaust mounts, e.g. `LEFT_HEADLIGHT`).
* Mesh 0x80134100: 0x134900 header (+0x10 materials, +0x2C indices), 0x134B02 materials[0x68],
  0x134B03 u16 triangle-list indices, 0x134B01 vertex buffers, 0x134C02 material names.
* Material: bbox, 5 texture slots @+0x18 (0 diffuse, 1 normal, 3 specular), **effect id @+0x30**, flags @+0x38,
  vertex count @+0x3C, index start @+0x44, index count @+0x5C.
* Vertex buffer → material: each buffer goes to the next material and to the following ones with the same effect id.
  Indices are relative to that buffer.
* Stride by effect: 0/4/5/6 = 36 (pos, normal, D3DCOLOR, uv), 1/3 = 60 (+ second uv, tangent, sign), 19 = 44.
  Effects 1/3 (roads, lots, facades) keep a specular/reflection mask in texture alpha: draw opaque.
* Hash: `bStringHash` — `h = h*33 + c`, seed 0xFFFFFFFF, names upper-case.

## Textures (TPK 0xB3300000)

Loader **0x507B50**. 0x33310001 pack name, 0x33310002 hashes, 0x33310003 compressed-block table
(hash, offset from pack start, in, out, flags), 0x33310004 TextureInfo[0x7C], 0x33310005 format[0x20] (FourCC @+0x14),
0x33320002 pixels (128-aligned).

TextureInfo: name[24] @0x0C, hash @0x24, class @0x28, data offset @0x30, palette @0x34, size @0x38, palette size @0x3C,
width/height @0x44, shifts @0x48, compression @0x4A (0x22 DXT1, 0x24 DXT3), mips @0x4E, tiling @0x4F (bit0 U, bit1 V),
bias @0x50, render order @0x51, **alpha usage @0x55** (0 opaque, 1 alpha test, 2 blend), alpha blend type @0x56.
In compressed packs each block is `pixels + TextureInfo + format`, and the info's data offset still refers to the
uncompressed layout (pixels start at 0 in the block).

## Cars (CARS\NAME)

`GEOMETRY.BIN` (solids) + `TEXTURES.BIN` (JDLZ/HUFF pack), shared `CARS\TEXTURES.BIN`, `GLOBAL\GLOBALB.lzc`.
Stock car = `{CAR}_BASE_A` + `{CAR}_KIT00_*_A` (not DAMAGE/DECAL). Space: X forward, Y left, Z up, ground at Z=0.
The tire model is a right-hand wheel (rim face at Y≈0, tread toward +Y).

Runtime texture replacement (string table @0x8B4F5C…): meshes use generic names that the game maps per car —
`HEADLIGHT_LEFT/RIGHT` → `{CAR}_KIT00_HEADLIGHT_ON|OFF`, `HEADLIGHT_GLASS_*` → `…_HEADLIGHT_GLASS_ON`,
`BRAKELIGHT_*` → `{CAR}_KIT00_BRAKELIGHT_OFF|ON`, `WINDOW_*` → `WINDOW_FRONT`; body panels use `{CAR}_SKIN1`
(composited paint/vinyl render target, not on disk). Other formats: `%s_TIRE`, `%s_RIM`, `%s_BADGING`, `%s_INTERIOR`, `%s_SKIN1B`, `GLOBAL_SKIN1`.

Wheel placement comes from attributes: front axle at `chassis.FRONT_AXLE`, rear `WHEEL_BASE` behind it,
`±TRACK_WIDTH/2`, radius `RIM_SIZE·0.0254/2 + SECTION_WIDTH·ASPECT_RATIO`.

## Attribute database (GLOBAL\attributes.bin, VLT)

Loader **0x664010** → chunk scan **0x455D70** (`DepN` `StrN` `ExpN` `PtrN`, MSVC multi-char literals) → fix-ups and
export dispatch **0x457670**.

* VPAK header: [5] bin size, [6] vlt size, [7] bin offset, [8] vlt offset. Dependencies `db.vlt` (base 0) and `db.bin` (base 1).
* PtrN {offset, u16 type, u16 index, target}: 2 = switch base, 3 = pointer to base[index]+target, 0 = end.
* ExpN {key, type, extra, size, offset}: Database 0xCBBC628F, Class (ClassLoadData 0x5E970CBC), Collection 0x8E112EB7.
* Class: key, ?, field count, fields* (16 bytes: key, type hash, offset, size, count, flags, align), layout size.
  Flags: 2 = in layout, 1 = array (8-byte header, u16 count), otherwise optional entry.
* Collection: key, class, parent, reserve, ?, entry count, type count, layout*, types[], entries {key, value|ptr, u16 type, u16 flags}.
* Keys: Jenkins lookup2, seed 0xABCDEF00. 57 classes, 2,309 collections; `StrE` gives 1,966 names (all 113 vehicles with parents).
* Decoded fields: **chassis** (springs, shocks, travel, ride height, track width, wheelbase, front axle, weight bias, aero, drag),
  **tires** (static/dynamic grip, section width, aspect, rim, steering, yaw control/speed), **engine** (9-point TORQUE, MAX_RPM,
  RED_LINE, IDLE, FLYWHEEL_MASS, ENGINE_BRAKING, SPEED_LIMITER), **transmission** (GEAR_RATIO[9]: reverse, neutral, 1..6,
  FINAL_GEAR, TORQUE_SPLIT, DIFFERENTIAL, SHIFT_SPEED, …), **brakes**, **induction**, **nos**. `gameplay.bin` and `FE_ATTRIB.bin`
  are the same format; see Career below.

## Career (GLOBAL\gameplay.bin, FE_ATTRIB.bin, GlobalB)

* Class `gameplay` has 220 fields; the names are in `vlt_classes.txt`. Collections form a tree by parent, and an event's type is its
  parent class name (`circuit`, `p2p`, `knockout`, `tollboothrace`, `speedtraprace`, `drag`, `challengerace`).
* Bins `race_bin_NN` hold BinIndex, Boss, WorldRaces, BossRaces, RequiredBounty, RequiredRacesWon, RequiredChallenges and Barriers.
  Events hold EventID, CashValue, NumLaps, Region, IntroNIS, Opponents, Checkpoint, startgrid/finishline children (Position Vector3,
  Rotation = heading in degrees from +X toward +Y), TrafficLevel, CopsInRace, RaceLength, TimeLimit and Barriers.
* Milestones are `milestones/bin_NN/challenge_NN_<type>`. MilestoneName gives the stat: pursuit_length, bounty_in_pursuit,
  cost_to_state_in_pursuit, cops_damaged, total_infractions, roadblocks_dodged, tire_spikes_dodged or pursuit_evasion_time
  (the one where smaller is better). GoalEasy and GoalHard are floats; Bounty is the reward.
* FE_ATTRIB class `frontend` holds the car lot: Cost, and UnlockedAt (16 = from the start). Preset rides are in GlobalB chunk
  0x30220, 0x290-byte records with car[32] at +8 and preset name[32] at +40.

## Scenery groups and barriers (TRACKS\L2RA.BUN)

* Chunk 0x34108 is the override table: 6 bytes each, {u16 scenery section number, u16 instance index, u16 flags}. The section number
  is the one in scenery chunk 0x34101 (+0x0C), not the stream index number. Only 79 of the 377 override sections exist as index
  numbers; all 377 exist as scenery section numbers.
* Chunk 0x34109 holds the groups: {u32 0x0B, u32 0x0B, u32 bStringHash(name), u16 group number, u16 count, u32 flags,
  u16 override[count]}, padded to 4. There are 754 groups and 22,340 overrides, and the parse covers the chunk exactly.
* gameplay.bin names the groups: BARRIER_LOCKED_n are the closed city areas per bin (bins 16–13 lock 11–15, bins 12–9 lock
  10/12/13, bins 8–1 lock none); BARRIER_SPLINE_n are the race-route barriers per event, where a leading `*` means flipped art.
  The pieces are XO_TrackBarrierPlayer_1 (race barriers, instance flag bit 23) and XO_TrackBarrierLocked_1.

## Road graph (L2RA.BUN 0x3B800, loader 0x78A120)

A 'CARP' pack of 16-byte entries {tag, (size << 8) | flags, count, offset}; flag 2 means the offset is relative to the entry.
RNhd gives the counts (710 profiles, 4,385 nodes, 6,538 segments, 1,308 roads). RNnd nodes are 32 bytes, with game X = c,
Y = -a and height = b. RNsg segments are 22 bytes and start with u16 node A and u16 node B. The graph is one connected component.

## Front end (FEng)

* Chunk 0x30210 holds a package's name hash and its data, raw or JDLZ. Inside are PkHd, the resource names, and objects (FOb\xEA:
  ObjD data, Scrp scripts, MsgR). Object types: 1 image, 2 string, 5 group, 7 multi-image, 12 coloured image. Flag 0x10000000 = hidden.
* Space is 640x480 with the origin at the centre and +Y down; a larger Z is further back. Scripts are named by bStringHash
  (INIT, SHOW, HIDE, APPEAR, LEAVE, GREY, ANIMATE…). A track's first key is the base and later keys are deltas, and a script's
  `chain` is played after it. INIT often chains into HIDE, so the game must play a fade-in script (e.g. 0x5FB2E82A in
  PostRace_Pursuit).
* Fonts are chunk 0x30201 (FNTF glyph tables). Strings are chunk 0x39000 in LANGUAGES\English.bin. Object names are recovered by
  hashing the strings in speed.exe.

## HUD minimap (TRACKS\L2RA\MINI_MAP.BIN)

There are 64 chunks 0x3A100, each a JDLZ-packed texture pack holding one 128x128 DXT3 tile (MINI_MAP_CHOP0..63, row-major 8x8).
Together they form a 1024x1024 map of the city. Map pixels from game space: u = X * 0.1538 + 188.0, v = -Y * 0.1538 + 779.3.
This transform was fitted against the road graph by correlation and refinement, and 96% of the nodes land on road pixels.

## Audio (SOUND)

* `.abk` banks contain 'BNKl' v5. PT headers are tag/length/big-endian value; data is EA-XA v2 (decoder 0x828D60): 15-byte frames
  of 28 samples, 0xEE = raw PCM frame.
* `.gin` granular engines start "Gnsu20" and hold EA-XAS v0 (decoder 0x4E1070, 0x13-byte frames of 32 samples). The car's
  pvehicle.engineaudio is a RefSpec to Filename_GinsuAccel/Decel.
* `.big` streams are 'SCHl' (PT header: 0x82 channels, 0x84 rate, 0x85 samples, 0xA0 codec2), then 'SCCl', then 'SCDl' blocks
  {u32 samples, per-channel offsets from the end of the table, per-channel EA-XA v2 with history carried across blocks}, then 'SCEl'.
  - NISAudio.big has 142 streams at 44.1 kHz, mostly 5.1, in four sets. NISAudio.idx: from 0x58, one 16-byte entry per set
    {0x0100000N, ?, ?, offset}. Set 1 starts with 13 streams matching the 13 arrest scenes.
  - copspeech.big is codec2 4 (EA MicroTalk 10:1); see Police speech.
* MW_Music.mus is all zeros in this copy, so there is no licensed soundtrack. MOVIES is empty.

## NIS cutscenes (NIS\Scene_*_BundleB.bun)

* 0xE34010 is an EAGL animation bank: an ELF whose channels (`<Scene>ZPM_Car1_q/_s/_t`) are ZPM delta-compressed; see NIS animation.
* 0x3B811 is a 'PRAC' event pack (srtS, Ae/Ee/Se/Te tracks).
* 0x80037020 → 0x37030 is the header: hash, name[28], ?, actor count, ?, frames at 30 fps, description at +0x40.
  86 scenes: race intros (IntroNis01–10, GenericStart), Blacklist intros (IntroNisBL01–16), 26 arrests, endings and world-event cameras.
* InGameB.bun 0x80037050 lists the bundle files.

## Car parts (CARS\*)

* Each car's GEOMETRY.BIN has body kits CAR_KIT00..04_* (00 is stock) and hood styles CAR_STYLEnn_HOOD.
* Shared folders:
  - WHEELS holds BRAND_STYLEnn_SIZE_25 rims: a rim with its tyre, normalised to about 0.33 m radius. The brands are
    5ZIGEN, ADR, BBS, ENKEI, KONIG, LOWENHART, OZ, RACINGHART and others.
  - SPOILER, SPOILER_HATCH, SPOILER_PORSCHES and SPOILER_CARRERA hold SPOILER_STYLEnn[_family][_CF]. A material
    named BODY takes the paint.
  - ROOF holds ROOF_STYLEnn[_DUAL|_OFFSET][_CF].
* Solid chunk 0x13401A holds markers, 16-aligned, 80 bytes each: {u32 bStringHash(name), 12 bytes, 4x4 matrix with the
  translation in the last row}. Names include SPOILER, ROOF_SCOOP, LEFT/RIGHT_HEADLIGHT, LEFT/RIGHT_BRAKELIGHT and
  LEFT/RIGHT_REVERSE. Aftermarket parts mount on these.
* VINYLS.BIN holds 512² P8 vinyls in the body-skin UV layout, each with a _MASK partner (recolour layers, not used here).
* The game's own part database is GlobalB chunk 0x80034602 (0x34603 header, 0x34604 parts, 0x34605 attributes, 0x34606
  strings, 0x3460A/B/C). It is not decoded; availability is read from the geometry instead.

## Challenge Series (gameplay.bin race_bin_challenge, bin 19)

* There are 70 events: tollbooth trials and pursuit challenges alternate, two per car.
* Fields: ChallengeSeriesRace, InitiallyUnlocked, UnlockRaces (the chain), PlayerCarType (a GlobalB preset ride
  CS_CAR_nn, e.g. CS_CAR_10 = gallardo, CS_CAR_PIZZA = pizza), PursuitRace, MilestoneName plus ThreshholdValue (the
  target), ForceHeatLevel, CopSpawnPoints (carmarker children), InitialPlayerSpeed (mph, rolling start), BustedLives,
  racestart and racefinish.
* Tollbooth checkpoints of class time_bonus_checkpoint carry TimeBonus (seconds) on top of the event's TimeLimit.

## Police speech (SOUND\SPEECH\copspeech.*)

* .idx: u32 1, u32 count (579), then from 0x58 one entry per line {0x0100000N, info size, info offset, .big offset}.
  The info block is {u16 id, ffff, u8 cue width - 2, u8 take count, ...}, with cues from +12: big-endian u16 x 0x100 =
  offset of the take's own SCHl stream. All 13,562 takes resolve.
* Streams are MicroTalk (SCHl codec2 4, 24 kHz). In v2 PC streams each SCDl channel opens with a flag byte (0x01 =
  start) and every frame with a marker byte (0xEE = raw PCM follows). The decoder is ported from vgmstream (ISC).
* .csi is 'MOIR' with 139 events {name offset, .evt offset}. The .evt scripts are not fully decoded; line references
  appear as {u16 line, c8 00 f4 01}, which gives 128 references across 60 events.

## NIS animation (EAGL4, chunk 0xE34010)

* Each bank is a relocatable MIPS ELF. `__AnimationBank` points at the header {?, count, ..., +0x10 animation
  pointers, +0x14 name pointers}; pointers are already .data offsets.
* Animation type 0x0F is a compound (vtable 0x895DF0, created by factory 0x4A9D10). Its children are type 0x0A
  (vector, vtable 0x896048) or 0x0B (quaternion, 0x896098): {u16 type, u16 flags, ptr block, u16 keys, u16 output slot
  per value}. The slots are translation 20–22, scale 12–14 and rotation 16–19.
* Block (decoder 0x48FF00): {u16 n, u8 bits 4/8/16, u8, n x {f32 offset, f32 scale, f32 initial}}, then (keys - 1)
  records of quantised deltas: v[k] = v[k-1] + offset + q x scale. Keys are 15 per second; interpolation is linear,
  and quaternions are renormalised.
* Channels are named `<Scene>ZPM_CarN_t/_q/_s` and `<Scene>ZPM_CopN_*`.
  - Generic intros are in start-grid space: x right, y along the start heading, and the cars finish on the grid.
  - Blacklist intros are in world coordinates.
  - 84 of 86 scenes decode, 747 tracks in all.
* Chunk 0x3B811 ('PRAC', CARP layout) is the scene's event graph: Strs, plus Ae/Ee/Se/Te/le records named
  Begin/ActionCarN/pause/resume. There are no camera keys in the scene files.

## Music and movies

SOUND\PFDATA\MW_Music.mpf ('PFDx' v5, the interactive-music map) is intact, but MW_Music.mus is all zeros in this copy.
MOVIES is empty. Events name their movies in IntroMovie and OutroMovie (storyfmv_bla134, Storyfmv_her136,
tollbooth_tutorial).

## Behaviour specs (second RE pass)

The behaviour of each game system, read from speed.exe and the data, is written up under `specs/`. Each item is marked
read from code, read from data, inferred or unknown.

| Spec | Covers |
|---|---|
| `career_flow.md` | The career/story scripts. These are gameplay.bin `stategraph_*` blobs: HUFF-compressed Lua 5.0 bytecode with float32 numbers (`tools/lua50.py`; all 258 handlers in `career_stategraphs.txt`). It also covers the prologue order, the bin graph and SMS hooks, the post-rival screens and the epic pursuit. |
| `world_interaction.md` | The menu-gate zones (safehouse, car lot and shops) with their positions and respawns, race engage triggers, the allow flags, the Pause_Main option lists, the world map and autosave. |
| `races.md` | Type resolution, template defaults and per-event data, the catch-up PID, the PostRace_Results object map, rewards and the Quick Race options. |
| `pursuit.md` | Heat rows (heat_01..10), the busted meter, cooldown and evade, support units, formations, spawning, bounty and combo, infractions and fines. |
| `hud.md` | HUD_SingleRace widgets, the 64-bit visibility mask, the tach and redline tables, multi-image masks and the pursuit board bars. |
| `camera.md` | CubicCameraMover. camerainfo [0]/[1] are full-screen and split-screen values. It covers Hermite channels, the speed tables, the heading model, shake and the projection. |
| `physics.md` | The engine torque span (IDLE..MAX_RPM), engine braking, nitrous, the gearbox, tires, suspension, aero, brakes, steering assists, upgrades and the speedbreaker. |

Format fixes from this pass:
- FEng chunk 0x30203 is a raw package with no name-hash prefix; `HUD_SingleRace` is stored this way.
- FEng type-12 multi-images carry mask textures M1..M3. Their `SA` block continues to 0x90: three mask UV rectangles,
  then the pivot and an angle in degrees.
- VLT array headers are u16 capacity, then **u16 count**.

## Not reversed yet

MW's own chassis/tire physics (the rebuild uses WheelColliders tuned from the attributes), NIS camera paths (not in
the scene files) and character rigs, the speech sentence scripts (.evt), the car part database (GlobalB 0x80034602),
collision packs (0x3B801), sky/time of day, particles, and the gameplay state graphs (stategraph_* bytecode).
