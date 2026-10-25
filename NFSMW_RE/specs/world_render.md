# World streaming and world rendering (speed.exe v1.3, PC)

Status 2026-10-24. Read from speed.exe: capstone disassembly with the IDA symbols of this exe
(refs/nfsmw/config/SPEED_EXE_1_3/symbols.json), the 31 embedded D3DX effects (PE resources `IDI_*_FX`, parsed as fx_2_0
and disassembled with d3dcompiler_47), and the game data through the harness. The GameCube decomp (refs/nfsmw) was used
for names and layouts only where it agrees; the exe wins. `[?]` = not confirmed, `[ours]` = Unity-side choice.
Companion spec: sky_lighting.md (time of day, fog, sky dome, parameter maps).

Harness probes: `wrender mats|effects|verts <id>|skyinst|sun|tex <name>` (NFSMWStaging/Harness/WorldRenderProbe.cs),
`wrefl scenery [step]|flag <hex> [step]|flagtex <hex> [step]` (WorldReflectProbe.cs), `stream ...` (StreamProbe.cs),
`weather ...` (WeatherProbe.cs), `post` (PostProbe.cs).
Round 3 (2026-10-24, Gamma colour space): §4 road reflection contents, §5 sun query, §9 mask order, §10 gamma review,
§13 world shadows, §14 LOD pixel scale. Effect dumps: the 31 PE resources parsed as fx_2_0 (techniques, annotations,
state assignments, the shader blobs per technique) - the parser lives in the session scratchpad, the results are here.

Contents: §1 technique choice, §2 per-material world effects, §3 blended world materials, §4 road reflection view,
§5 sun, sky leftovers, §6 WorldModel, §7 TrackStreamer, §8 rain / WeatherMan / ScreenEffects / EventManager,
§9 end-of-frame post chain, §10 colour space, §11 deviations and unknowns, §12 check in play, §13 world shadows,
§14 scenery LOD pixel scale.

## 1. Effects and technique choice

* Effect objects: [0x93DE78 + 4 id], created by 0x6D6000. Each id gets its own vtable at 0x8AF204 + 12 k through the
  jump table at 0x6D645C; slot 1 is the per-frame constant setup:
  ids 0,3,6,7,8,14,15,18,30 -> 0x6D6AA0 (world type), 1 -> 0x6D71F0 (worldreflect), 5 -> 0x6D7400 (GlossyWindow),
  4 -> 0x6C5B80 (car), 19 -> 0x6C60F0 (sky), 2 and 9..13, 17 -> nullsubs.
* Technique: sub_6D5C10 validates every technique and files it into slot `shadowLevel` (annotation; 0 when absent),
  keeping per slot the highest `shader` annotation (+0x18 + 4 slot = handle, +0x28 + 4 slot = shader level).
  sub_6D7500 picks slot [0x982C3C] (the shadow level, derived in 0x6D2100 from the depth-texture support [0x982C08] and
  the effects' supported maximum, capped at 2; 0 with world shadows off) and sets +0x40 = "shader level > 0".
  With shadows off and a shader-model-2 card: world.efx tech 0 (`shader`=1, vs_1_1/ps_1_1), worldreflect tech 0
  "world_reflect_noshadow_2_0" (`shader`=3), worldnormalmap tech 0 "worldnormalmap_2_0" (3), GlossyWindow tech 0 (1),
  tree tech 0 (1). With world shadows (the top preset on a depth-texture card) the render list picks shadowLevel 1-2
  per draw instead: §13.
* Per draw, eViewPlatInterface::Render 0x6DA9B0 swaps effects by render-target slot ([0x982A20]+0, see §4):
  slot 0 (player view) keeps them; slot 4 (shadow map, view 0xF) draws 4 car -> 23 car_shadow_map, 0 and 5 -> 8
  worldnofog, 1 and 3 -> 30 worldnormalmapnofog (corrected: not the depth effects); every other slot (road reflection,
  RVM, env maps) draws 0 and 5 -> 8, 1 and 3 -> 30, the rest (tree 6, car 4, sky 19) unchanged. Then render flag
  0x20000 (StuffScenery sets it for instance flag 0x100, and StuffSkyLayer for the sky) turns 0 / 8 into 22 shadow_cg
  (0x6DABFC). A material with a missing texture (any of its 5 TextureInfo pointers null) is skipped, except
  GlossyWindow which falls back to world.efx (0x6DAC90).
* Effect id table (0x8F9BE4, stride 0x90, file / resource): ... 20 shadow_map_mesh, 21 skybox_cg, 22 shadow_cg,
  23 car_shadow_map, 24 worlddepth, 25 worldnormalmapdepth, 26 cardepth, 27 GlossyWindowDepth, 28 treedepth,
  29 shadow_map_mesh_depth, 30 worldnormalmapnofog.
* worldnofog (8): technique 0 vs_1_1/ps_1_1, oD0 = vertex colour, `mul_x2 r0.xyz, t0, v0 + mul r0.w, t0.w, v0.w`: unlit,
  unfogged. worldnormalmapnofog (30): only "world_fixed": stage 0 COLOROP MODULATE2X (TEXTURE, DIFFUSE), ALPHAOP
  MODULATE, lighting off - the same. shadow_cg (22): `rgb = tex vc ShadowColor, a = 2 tex.a vc.a ShadowColor.a`; the
  parameter name SHADOWCOLOR is in no exe parameter table (only SHADOWCOLORMAP), so it keeps the .efx default 0 [?]
  (§11).

## 2. Per-material world effects (Runtime/World/WorldMaterials.cs)

Material layout (0x68 bytes, Core/SolidList.cs): +0x18..+0x1C five texture slots indexing the solid's texture table,
read per draw at 0x6DAC17: 0 diffuse, 1 normal map, 2 height map, 3 specular map, 4 opacity (the data agrees: slot 1
holds *_NORMAL / *_NORM, slot 3 *_SPEC / *_S; harness `wrender mats`). +0x30 effect id.
Vertex format of effects 1 and 3 (stride 60): +0 position, +12 normal, +24 colour, +28 uv, +36 a second uv pair the
shaders never read, **+44 tangent xyzw** (w = +-1; harness `wrender verts 1`). SolidPart.Tangents reads it.

L2RA scenery (harness `wrender effects`): effect 0 ~10k parts, **1 roads/pavements 2.7k** (all opaque, gloss in
alpha), **3 walls/cliffs/barriers 3.3k**, **5 windows/facades 2k**, **6 trees/bushes 0.65k**, 19 the sky.

Constants (vtable slot 1):
* world type 0x6D6AA0: DiffuseColour 0x6C6300, AmbientColour 0x6C6450, SpecularColour = TOD +0x40 rgb, w = the view's
  Rain +0x36A0 (wetness); SpecularPower = [0x8FADC0] = 11.
* worldreflect 0x6D71F0: SpecularColour = **(1 - Rain +0x290 cloud) x** TOD +0x40, w = wetness; ReflectedTex =
  [0x982A6C] (§4); SpecularPower 11; DiffuseColour, AmbientColour.
* GlossyWindow 0x6D7400: WindowReflection = TextureInfo "WINDOWREFLECTION" (GLOBAL\InGameA, 256x128 DXT3), Diffuse,
  Ambient. (Handle offsets: the parameter-name table of 0x6C3D20, handle = entry + 4: SPECULARCOLOUR 0xC7C,
  SPECULARPOWER 0xCF4, REFLECTEDTEX 0xAEC, WINDOWREFLECTION 0xF74.)
Published by TimeOfDayDriver: `_MW_Specular` (world type), `_MW_SpecularRoad` (worldreflect), `_MW_SpecularPower`.

Shader maths (L = LocalLightVec toward the sun, V = normalize(eye - v), fog block = world.efx's, sky_lighting.md §2):

| effect | port | vertex | pixel |
|---|---|---|---|
| 1 worldreflect | WorldReflect.shader | oD0 = (Amb.rgb Amb.w, 1) x vc (not extinguished); oT1 = projective screen pos; oT3 = normalize(TBN L); oT4 = TBN V; oT5 in-scatter; oT6 = min(ext,1); B = cross(N,T) T.w | n = normalize(2(nm - 0.5)); R = 2(n.L)n - L; spec = pow(sat(R.V), 11) Spec.rgb; s = 2 specmap; c = (Refl(t1) x s.x x tex.a x v0.a x Spec.w + tex v0 + tex sat(n.L) Diff + spec s) x oT6; out 2c + oT5, alpha 1 |
| 3 worldnormalmap | WorldNormalMap.shader | as worldreflect without oT1 | as worldreflect without the reflection term, specmap not doubled; alpha tex.a v0.a |
| 6 tree | Tree.shader | oD1 = (Amb.rgb Amb.w, 1) vc (not extinguished); oD0 = ext x sat(N.L) Diff | 2 tex (oD0 + oD1) + in-scatter; alpha = tex.a only |
| 5 GlossyWindow | GlossyWindow.shader | oD0 = sat(N.L) Diff; oD1 = ext Amb.rgb Amb.w vc; oT2 = sat(R.V)^10 (R = reflect(L, N)); oT1 = (0.5u + 0.5 viewNormal.x, 0.5v) | 2 tex (oD1 + oD0) + 4 tex.a oT2 + tex.a WindowReflection(oT1) + in-scatter; alpha tex.a |
| 0 world | World.shader (unchanged) | sky_lighting.md §2 | 2 tex (prelit + sun) + in-scatter |

Notes: the extinction lands on a different term in each effect (world: prelit only; worldreflect/normalmap: the whole
lit colour; tree: the sun only; glossy: prelit only). Tree and GlossyWindow techniques set CULLMODE none.
worldreflect writes alpha 1, so it can never be alpha tested.

Port: WorldStreamer.Resolve calls `World.WorldMaterials.Apply(built, solid, GameData.Textures)` after each world
MeshFactory.Build: it adds the tangents (Y/Z swap mirrors the space, so w is negated to keep B = cross(N, T) w) and
replaces each part's world.efx material by its effect's, copying texture, scroll (TextureScroll.Register), cull,
cutoff and, for blended textures, the WorldDecal blend/queue/depth-bias. Normal and specular maps are decoded with
`MWRawData` (undoes the sRGB decode in a Linear project, §10). Fallback to world.efx when a normal/specular map or the
tangents are missing [ours: the exe would skip the material]. `WorldMaterials.Enabled = false` restores the old look.

## 3. Blended world materials

The exe has no separate transparent world effect: a blended material (TextureInfo AlphaBlendType != 0) is drawn by its
own effect (world.efx for effect 0) with the texture's BlendState. WorldDecal.shader and WorldTransparent.shader gained
`_MWLit`: 1 (set by WorldMaterials on world scenery materials only) runs world.efx technique 0 exactly as World.shader
(rgb = 2 tex (prelit + sun) + in-scatter, alpha = tex.a vc.a); 0 keeps the old unlit path for the car shadow, skid
marks, light flares, particle fallback, race props and car glass. Depth bias, queue (2470 + RenderOrder) and ZWrite
off are unchanged [ours: the exe sorts blended world parts with its own render lists].

## 4. Road reflection view (Runtime/World/RoadReflection.cs)

* Render targets (0x6BD0D0, table 0x93DAB8, 0x1C each): slot 0 view 1 (back buffer), **slot 1 view 4 = the road
  reflection, 320 x 240**, slot 2 view 0, slot 3 view 3 (RVM), slot 4 view 0xF (shadow map), slots 5-10 env-map views.
* 0x6BCDC0: option [0x9017D4] g_RoadReflectionEnable 0 -> ReflectedTex [0x982A6C] is a 1x1 texture filled with
  0xFFFFFFFF; else a 320x240 target (+ a second one for the option-3 blur). Preset 0x6C1990: 1 at performance level 3,
  2 at level 4+ (clamped by [0x90185C]); the port defaults to 2.
* **No mirrored camera**: view 4 shares player 1's Camera (epInitViews 0x6C0860) and 0x6CF400 builds its matrices as
  view 1's (aspect x 1.15 as views 1, 2, 5; H = (__window_width / 2) / tan(fov / 2) from 0x6BE9F0). The reflection is
  made by drawing each OBJECT upside down about its own origin from the real eye; the road samples the target at its
  own screen position. eDisplayFrame 0x6DE40F (option != 0): clear to 0xFF000000 (0x6BD3B0), then
  1. StuffSkyLayer(view 4, layer 4) 0x6DB310: SkydomeLocalWorld = diag(0.03, 0.03, -0.03), translation = eye + (0, 0,
     130): a 290 m upside-down dome whose zenith hangs 33 m below the eye.
  2. option > 1 and wetness > 0.01:
     * **scenery**: StuffScenery(view 4, 8) on view 4's own cull list. Its cull flags (sub_4FAE40 from 0x6BFF1F) are
       0x800 | 0x10 | 0x8000040 (+1 split screen, +4 racing). DrawAScenery 0x729120: an instance flagged 0x40 or
       0x8000000 gets 0x8000040; (flags ^ 0xFFFFFF60) & cull & 0x80000FF != 0 rejects it, so **only instances flagged
       0x40 / 0x8000000 reflect** (minus disabled 0x10). Cull & 0x1800 picks the model: flag 0x80 -> model 2,
       flag 0x1000100 -> model 0, else **model 3 = the REFLECTION LOD (D)**, all at pixel size >= 32 (0x729310); the
       matrix's v2.z is negated (0x7293DE, cull & 0x800): upside down about the instance origin. StuffScenery's flag 8
       then drops render flag 0x2000 = instance flag 0x80 (the TRN_ / SHD_ ground). Harness `wrefl scenery 3`: of 25.6k
       sampled instances 12.2k carry the flag; 11.1k of those have a reflection model (traffic lights, poles, trees,
       barriers, fences, buildings, freeway parts; 10.7k of the LOD D solids live in the shared libraries); 3k are
       ground. The effects become worldnofog / worldnormalmapnofog (§1: unlit 2 tex vc); trees keep tree.efx.
     * **cars**: sub_750B10 -> VehicleRenderConn::RenderAll(view 4, 1) -> CarRenderConn::OnRender 0x750E20: only with
       view mode < 3 (no split screen) and view 4; the render matrix's v2 (local z) is negated, position unchanged,
       render flags 0x401 (0x751093): each car upside down about its model origin, car.efx unchanged (slot 1 keeps id 4),
       side mirrors not drawn (CarRender.cpp:3221), the reset flash applies (Render returns early).
  3. light-glow option [0x9017E4] (on from performance level 2): eRenderWorldLightFlares(view 4, FLARE_REFLECT) (no world
     flare packs on PC), RenderVehicleFlares(view 4, 1, 0), nullsub_47, eRenderLightFlarePool(view 4) (both sun flares
     exclude view 4). Per car (RenderFlaresOnCar, reflexion != 0):
     * RenderTextureHeadlights 0x738180 first: when the car's up axis has z >= 0.707, a quad in car space x 3..23 m,
       y +-4.9 m, z 0 (hOffX 13, hRad*x 10, hRad*y 4.9, hOffY 0), colour hcL = the dword 0x40CCCCCC, texture
       2PLAYERHEADLIGHT1: the headlight pool that shows on the wet road in front of every car;
     * every lit flare as eRenderLightFlare(FLARE_REFLECT, REF_TOPO, mCar_elevation (+0x116C, the ground height under
       the car), size 1) 0x505380: reflectionStretch = 1.2 + (10 - 1.2) wetness ([0x8F23F0] / [0x8F23F4]); the flare
       height is mirrored, z' = elev - (z - elev) (0x50543F); texture HEADLIGHTGLOW for every layer (0x505A24); no
       screen-x spin; the quad's up vector x reflectionStretch, x 0.5 of it when z - z' < 4 (0x505C3A, so car lights
       always get half); fade 32000..40000 as FLARE_NORM.
  4. option 3, game flow 6, wetness > 0.01: sub_6D4960 = filter pass 3 with 0x8F9530 (horizontal 1/3 at +-1.5, 1/6 at
     +-3 texels) into the second target, then 0x8F9500 (vertical) back into ReflectedTex.
  Before the cars, effect 17 (worldprelit) is begun and set as [0x982C80]; the per-material effect still wins (0x6DA9B0).
* worldreflect samples it at the pixel's own screen position (oT1 = ((x + w)/2, (-y + w)/2, z, w), texldp), scaled
  by gloss x wetness: the inverted cloud dome, upside-down props / buildings / cars and light streaks on wet roads.
* Port: a hidden camera (layer 30) copies the main camera's pose and projection in Camera.onPreCull; everything is
  queued with Graphics.DrawMesh(camera): the sky dome; the scenery registered by WorldStreamer.AddReflection (instance
  matrix with Unity m11 negated = game v2.z, model 3 or 0, Shaders/WorldNoFog.shader copies of the materials, pixel
  size >= 32 with H = W/2 x projection m00); the cars (every car.efx renderer under each CarRenderInfo, matrix
  B diag(1,-1,1) B^-1 about the Body transform); the headlight pools; LightFlares' reflected flares
  (LightFlares.AddReflected); option 3 blurs with VisualTreatment passes 1 and 2. [ours] Refreshed only while
  wetness > 0.01 (the shader multiplies by the wetness); rendered with shadows off (QualitySettings toggled around the
  render); smackables are not reflected (the exe draws them as WorldModels, which view 4 never renders);
  mCar_elevation by a ray down from the body.

## 5. Sun, and the rest of SkyRender

* **Sun sprite** sub_6D04E0 (eDisplayFrame 0x6DF585, after the rain, before the visual treatment), game flow 6 only.
  Port: Runtime/Sky/SunRender.cs + Shaders/Sun.shader, drawn from VisualTreatment's BeforeImageEffects buffer after the
  overbright alpha mask (it writes RGBA, so it blooms).
  * SetCurrentSunInfo("E3DEMO") every frame. SunInfo = GLOBALB chunk 0x34202 (LoaderSun 0x4FD330; 0x110 bytes per
    entry: PARKLAND, SUNSET, PROTOTYPE, E3DEMO, MIDDAY; Core/SunInfo.cs). E3DEMO's layers: SUNCENTER 60 px intensity 1,
    SUNCENTER 320 px 0.5, SUNMAJORRAYS 500 px 0.7 and 0.32 (angle 0x4000), colours (255,200,100) (130,110,25)
    (159,122,75) (166,119,72), sweep 0.2 on the rays. Textures SUNCENTER .. SUNRING (InGameA, AlphaBlend 2 = add).
  * Position: GetScreenPosition 0x6BE940 of TOD +0x170 (sun x 100000), as 640x480 units; no w test.
  * Visibility sub_6D0350: 4 D3D occlusion queries in a ring over a 32x32 (640x480 units) WHITE16X16_NOALPHA quad at
    the sun (sub_6C0160, z 0.999, fe.efx, colour writes off, ZENABLE on); vis = 0.5 vis + 0.5 min(pixels / 1024, 1)
    ([0x93E020] = 0.5), 0 until primed. The pixel count is in real pixels while the divisor stays 1024, so above
    640x480 the ratio saturates early (at 1280x720 the quad is 64x48 = 3072 px: fully "visible" until 2/3 covered).
    Port (round 3): Shaders/SunQuery.shader from VisualTreatment's AfterForwardAlpha buffer: alpha cleared, the quad
    drawn at D3D depth 0.999 with the depth test into alpha, its lit pixels counted into a 1x1 RFloat target (/ 1024)
    and read back with AsyncGPUReadback, so trees, buildings and cars all occlude. [ours] 1-3 frames of latency
    instead of 4; the old 16 physics rays remain only without async readback.
  * Quad per layer sub_6C0160: intensity = (1 - cloud) IntensityScale; turn = (u16)(Angle - ftol((maxsize + x) /
    (2 maxsize + 640) Sweep x -65536)); square of side Size rotated by turn at (x + OffsetX, y + OffsetY), z 0.999;
    colour (rgb, alpha byte = (byte)ftol(intensity x vis x 256)); fe.efx: rgb 2 tex vc, a tex.a vc.a; Z disabled.
  * The alpha byte store wraps - **confirmed** (0x6C0258 ftol of intensity x vis x 256, 0x6C029B stores al, no clamp):
    intensity 1 (clear sky, cloud snapped to 0) at vis exactly 1.0f gives alpha 0. vis = 0.5 vis + 0.5 reaches 1.0f
    after ~25 fully visible frames, so the 60 px core fades in, then vanishes while the sun is fully clear and comes
    back as soon as anything covers part of the query quad. Faithful; compare with the real game.
  * GetScreenPosition 0x6BE940 has no w test and sub_6D04E0 none either (its extra D3DXVec4Transform result is unused):
    a sun behind the camera is projected mirrored and both query and sprites use that point. Port: SunRender
    .MirrorBehindCamera = true (exe); false = the earlier [ours] "hidden".
* **Sun flare** RenderSunAsFlare 0x503830: two pool flares (type 21) with exclude masks 0xFE and 0xFC00F6; the pool
  0x507770 skips a flare when mask & (1 << view id), so neither ever draws in a player view (only RVM / env maps: car
  reflections). Nothing to port for the world.
* **SKY_SPECULAR**: no PC code names it; its Z0 instance (flags 0x48240) is skipped by WorldStreamer. Faithful [?].
* **skybox_cg.efx**: only when skybox.efx's shader technique fails (NotifySkyLoader); the legacy SkyDome is that
  fallback.
* Parameter-map consumers: Clouds and Rain (Rain.cs), Wind (Rain.cs gusts), Screen Tint (SE_TINT, never drawn on PC,
  §8), Normal Fog / Rain Fog (FogQuery 0x753D90 at 0x6DEBB2; no reader on the shader path found [?]), Ambience / Reverb
  / Speech (audio).

## 6. WorldModel (World/WorldModel.cpp, 0x73B800-0x75A5A8)

A WorldModel is a renderable eModel (or ModelHeirarchy node) with a matrix or SpaceNode, kept in WorldModelList and
drawn by RenderWorldModels 0x7538D0 (eDisplayFrame 0x6DED79) through eViewPlatInterface::Render with the normal
effects; `add_lighting` gives it the car light rig (elSetupLightContext with ShaperLightsCarsInGame). Users on PC
(ctor xrefs): GIcon::CreateGeometry 0x5DE690 (created disabled, +0x28 = 0), GRaceStatus::SetNextCheckpointPos 0x5FE660
(checkpoint model), SmackableRenderConn::Update 0x75ABD0 (breakables), VehicleFragmentConn::UpdateModel 0x75AE10 (car
fragments). Reflection views use pReflectionModel (LOD D) with the z axis negated (decomp WorldModel.cpp RenderModel,
flag 0x800). In the port every one of these is a Unity GameObject owned by its system (Smackable.cs, RaceManager
markers, CarFragments.cs); no separate WorldModel list is needed. Not ported: the WorldModel light rig on smackables.

## 7. TrackStreamer

Exe range 0x723600-0x73182B (World/TrackStreamer.cpp), compared with the decomp's TrackStreamer.cpp / VisibleSection.cpp;
where they differ the exe is followed. Port: `Core/TrackStreamerRules.cs` (rules and data), `Core/Track.cs`
(VisibleSections boundary queries), `Runtime/WorldStreamer.cs`. Harness: `stream info | zone <x> <y> [z] [vx vy] |
sweep <x0> <y0> <x1> <y1> [speed] | pzones`.

### Data (TRACKS\L2RA.BUN, TrackStreamer::Loader 0x72BD60)
* **0x34110 TrackStreamingSection** (0x5C): Name[8], Number i16 +8, FileType +0x10, FileOffset +0x14, Size +0x18,
  CompressedSize +0x1C, PermSize +0x20, SectionPriority +0x24, Centre +0x28, Radius +0x30. 720 sections, 507 MB, all
  file type 1, compressed size = size. SectionPriority: A..T = 10000 + 100 letter + sub; V = 4000+n; Y = 2000+n;
  X = 3000+n; X0 = 10, Z0 = 20, Y0 = 30.
* 0x34112 barriers: none on PC. 0x34113 disc bundles, 0x34111 infos.
* Inside 0x80034150: 0x34151 VisibleSectionManagerInfo, **LODOffset = 40** (drivable sections are sub 1..39, LOD
  sections 40..79); 0x34155 LoadingSection (0x4C): name, default flag, i16 count + 16 drivable sections, i16 count +
  8 extra sections; 39 of them (CP1.., 57 of the 435 drivable sections).
* **Drivable sections are not stream sections on PC**: only the combined LOD sections (A41 = A1 + 40) exist; drivable
  numbers in visible lists find no section and are skipped (AddCurrentStreamingSections, FindSection returns null).
* Prediction zones: TrackPathZone type 6, 211 zones (13 gated by elevation); Data[0..3] = drivable section numbers.

### Which sections load: DetermineStreamingSections 0x72DD70
1. Always Y0, X0, Z0 and the KeepSectionTable (+0x850, 4 x i16).
2. Per streaming position with CurrentZone > 0: the first LoadingSection that lists the zone gives GetSectionsToLoad
   0x72C750 (union of its drivable sections' visible lists, its extra sections, the LOD section of each drivable
   extra; cap 0x180); otherwise the zone's own visible list.
3. A typical set: ~38 sections, 125-138 MB (Y0 alone 103 MB).

### Streaming positions and prediction
* UpdateCameraMovers 0x47CA9E calls PredictStreamingPosition 0x7238D0 every frame for views 1 and 2 with the camera eye
  (+0x40), eye velocity (Camera +0x200), eye direction (+0x50) and "following car" = the first mover's type == 1 [?];
  skipped while GManager+0x2B4 or GRaceStatus+0x19A4 is set [?]. Entries 0x64 bytes from TrackStreamer+0xC0.
* GetPredictedZone 0x72DB80, for each prediction zone holding the position: elevation gate (Elevation > 0 needs camera
  Z >= Elevation, < 0 needs Z <= |Elevation|); predict point = position if speed > 178.8109 m/s (400 mph), else
  position + velocity x (100/speed) when 1.5 speed > 100, else position + 1.5 velocity; if FindDrivableSection(point)
  is in the zone's Data list it is the prediction (dropped across streaming barriers: none on PC). Else the drivable
  section under the position, or 0. FindDrivableSection 0x72CA00: first polygon holding the point, else the closest
  boundary under 0.1 m.

### Switching and loading
* DetermineCurrentZones 0x72DF20 -> SwitchZones 0x730460 on the frame the predicted zone changes (the valid-time
  counter never delays it). **PC unlike the decomp**: every activated non-texture, non-library section that left the
  set is unactivated and unloaded at once. Far load = the new zone's boundary is over 0.1 m from every point of the old
  one (also at start). Then FreeSectionMemory, phase ALLOCATING_TEXTURE_SECTIONS, AssignLoadingPriority,
  CalculateLoadingBacklog.
* Phases: textures (Y/W + Y0/X0/W0/U0/Z0), libraries (X/U), regular. StartLoadingSections 0x72E8F0 keeps 2 loads in
  flight, lowest LoadingPriority first.
* AssignLoadingPriority 0x728620: best over positions of GetLoadingPriority x 100000 + SectionPriority.
  GetLoadingPriority 0x728430: 0 without a boundary or below speed 1; q = position + velocity x 1 s;
  d = GetDistanceOutside(q, 999); angle = acos(heading . normalize(Centre - q)) clamped 20..90;
  prio = clamp(int(d (1 - (90 - angle)/70 x min(speed/60, 1) x 0.67) / 75), 0, 2). For jettison: speed 100, camera
  direction.
* CalculateLoadingBacklog 0x723AD0 (PC): sum size/1024 x 0.0001 (x 0.4 or 0.2 by BaseLoadingPriority); no +0.15.
* Budget: InitMemoryPool(0x7EF4000 + 50000 x 1024) = 175.8 MB (LoadGlobalChunks 0x664A24); the biggest single-zone set
  is 138.7 MB, so one player never jettisons; split screen can. ChooseSectionToJettison 0x72C410: texture/library 2
  (Y0/W0/X0 1), regular 100 + 10 x jettison priority, +1 if not loaded yet; highest goes first.

### Waits
* Sim::SetStream 0x6F1170 (WarpToMarker, Game_WarpPlayerToTrigger): SetStreamingPosition; if blocking PauseInput,
  BlockUntilLoadingComplete (RefreshLoading + WaitForCurrentLoadingToComplete), SetStreamingPosition, ServiceGameState.
* Free driving streams in the background (ServiceNonGameState -> HandleLoading from MainLoop 0x663EA9).
* Loading bar, CheckLoadingBar 0x730D20 via 0x64B770, Sim state 3 only: loading in progress, not a far load, following a
  car, <= 400 mph, and some not-activated drivable/LOD section of the set has (distance outside its boundary - 0.2 s of
  approach) < 5 m -> FEManager::RequestPauseSimulation("TrackStreamerLoadingBar") + EFadeScreenOn(1); off:
  EFadeScreenOff(0x16A259) + RequestUnPauseSimulation.

### Port
* Position 0 = Focus, 1 = ExtraFocus[0]; velocity = per-frame difference of the focus transform, direction its forward
  axis, FollowingCar = a Rigidbody in its parents; a jump over 50 m or a new Focus = SetStreamingPosition.
* Zone switches, the set, immediate unloading, phase + priority order and 2 loads in flight follow the exe; a far load
  requests everything at once; jettison only when a set exceeds the pool; Y* sections load their textures eagerly.
* `SetStreamingPosition(pos, blocking)` = Sim::SetStream; `Stream(pos, immediate: true)` still builds synchronously.
* UpdatePvs uses FindDrivableSection (the old SectionAt also matched non-drivable and panorama boundaries).
* New API: NeedLoadingBar, `static event Action<bool> LoadingBarChanged` (not wired to pause/fade yet),
  IsSectionVisible(n), Add/RemoveKeepSection, CurrentZone(i), IsLoadingInProgress, LoadingBacklog, Rules, Positions,
  `ExeStreaming` (false = the old radius streaming; LoadRadius / UnloadRadius only apply then).

### Deviations and unknowns
* [ours] Follows the Focus transforms (player car), not the camera eye. [ours] GroundRadius 150 m: regular sections
  within 150 m of a focus stay loaded on top of the exe's set (ground under every car); 0 = the exe's set alone.
  [ours] More than two focus points become extra positions. [ours] Far loads are not throttled to 2. [ours] Textures
  are never evicted. [ours] Sections leaving the set while parsing are dropped, while building unloaded afterwards.
* Not ported: disc bundles, hole filling, hibernation, LRU unloading, AmountNotRendered, online prediction locks.
* [?] mover type 1; GManager+0x2B4 / GRaceStatus+0x19A4; FindDrivableSection's move-to-front list; MyIsPointInPoly
  assumed even-odd.

## 8. Rain, WeatherMan, ScreenEffects, EventManager

Code: Core/Weather.cs (GenericRegion / WeatherRegions, ZoneSegments, RainMath, Precipitation = the PC Rain object,
OnScreenRain, ScreenEffectDB), Runtime/World/Rain.cs, ScreenEffects.cs, WeatherMan.cs, Shaders/RainDrop.shader.
TimeOfDayDriver calls `Rain.Ensure(gameObject).Step(Maps)` (replacing the old cloud easing) and publishes
`Rain.RoadWetness` as `_MW_Specular.w` / `_MW_SpecularRoad.w`.

### Rain and wet roads: PC MW really rains
* eDisplayFrame 0x6DF545 calls Rain::UpdateAndRender 0x758100 in game flow 6 while g_ParticleSystemEnable [0x9017EC].
  PrecipitationEnable [0x8F86E4] = 1; ENISRain (0x6233D0) sets it and the override; NISActivity::Release 0x6F75F7
  restores it and zeroes the override. Rain objects: ctor 0x749DC0 (epInitViews); World_Init -> SetRainBase 0x749D70
  (dry road, Init(INACTIVE, 1)).
* GetDesiredRainIntensity 0x7539D0: override [0x9B0A48] > 0 -> it; < 0 -> none; 0 -> the "Rain" map at the camera:
  field 0 when r = 1 - max(cos((0.7142857 t + 0.02 x) deg), 0) <= field 1 (0x73C9F0), t = WorldTimeSeconds [0x8F8308]
  (Sim::GetTime). Clouds use sin, so rain and clouds alternate; a cycle is 504 s. Set 0 applies where no node covers
  the point; rain sets (0.5, 0.25) default, (1, 0.08), (0.8, 0.05), (0.6, 0.04). Harness `weather cover`: 0-26 % of the
  map raining at a time, ~50-115 s per local cycle. Game_SetChanceOfRain 0x6054A0: < 0.01 -> override -1, else 0
  (races 0.5: maps decide; D-Day 0: never rains).
* Per frame: OnScreenRain::Update; windAng += 45 dt; if PrecipitationEnable == 0: tunnel test, rain = wetness = 0.
  Wetness +0x36A0: target RoadReflection (1) while raining else 0, moves 0.005 of the gap per frame, snaps to 0 below
  0.05 when drying. Targets only when both Rain and Clouds layers exist; cloud target = rain target if > 0 else the
  cloud map; split screen rain x 0.125. Easing rain += (T - rain) dt, cloud += (T - cloud) dt/10, snap under 0.01 when
  the target is 0, clamp 1. SetRainIntensity 0x73CC80: inactive drops = ftol((1 - rain) 350). Paused: SE_TINT offer +
  Render only.
* 3D streaks (Update 0x757770, Render 0x74ACC0, Init 0x73C6B0): 350 points in a rain box (X 8 +- 10, Y +- 3.5, Z 0..6)
  following the camera mover's car heading; per-frame motion scaled by 1 - (1 - min(30 dt, 1))^3, drop velocity from
  10 random vectors plus 0.3 (0.15) x wind and -0.03 x camera velocity; wrap/reseed; all inactive under a covering zone
  (tunnel 3, overpass 4/5, garage 7). Wind sub_753B50 (field 1 of "Wind", 0 everywhere) plus gusts (rand(5)+1 s, +-0.625
  over 1 s). Quads: half width cross(camDir, dropDir) x 0.01, half length 0.45 + speed, texture RAINDROP (InGameA,
  10 columns), colour 0x80808080, Particles effect.
* Tunnel-mouth curtain (0x74A070 / 0x74A160 / 0x74A320 / 0x74A490 / 0x74A5D0 / 0x749FB0): while covered, zones with
  <= 5 points, up to ftol(rain 200) points falling through the mouth from car z to z + 10.
* On-screen drops (0x73CDB0 / 0x73CFA0 / 0x6D4620, rain_drop.efx): 20 drops (10 split) while raining and not covered,
  when g_RainEnable [0x901810] and particles are on; each lives rand(5)+0.3 s, slides at 0.2 DripSpeed, pushed out
  from (0.5, 0.1) by camera speed x 1e-4; refraction of the frame copy through RAINDROPPC (GLOBALB 16x16), alpha
  D.a x 3 timer/Maxtimer, colour x 1.2, SRCALPHA/INVSRCALPHA, RGB only; after the motion blur, before the look.
* Deviations: [ours] WorldTimeSeconds = accumulated Time.deltaTime, reset per world load [?]; drops drawn before the
  motion blur; streaks get Unity fog; rotation-only rain-space transforms; anchor = ChaseCamera.Target; player 2 has no
  Rain; glare ground height by raycast; System.Random. [?] anchor vfunc +0x30 (0x757865), sub_740240, precip fog colour
  +0x34A8, AlwaysRain sub_74AB20, curtain zone lookups.
* Hooks not made (other owners): Lua Game.SetChanceOfRain -> Precipitation.SetChanceOfRain; NIS ENISRain ->
  Precipitation.NisRain / NisReleased.

### WeatherMan
LoaderWeatherMan 0x72CAB0, chunk 0x34250 (L2RA): Version 2 pack of 0xA4-byte GenericRegions (type 0 with intensity 0
-> 5; RegionLists 0x9AA380). L2RA: 61 regions, all type 8 REGION_BLOOM ("BLOOM_CP/CT/TC_nn") at tunnel exits. Readers:
GetClosestRegionInView 0x72A760 (DoTunnelBloom; ported); RegionQuery::CalculateRegionInfo 0x72A170 (only caller
0x6BFFD0 never called) and getHorizonColour 0x72A500 (no caller) not ported. Probe `weather regions`.

### ScreenEffects
ScreenEffectDB (eView +0x64): Update 0x7252A0 (Frame/Function effects expire after one update), AddScreenEffect
0x7252F0 (lock replaces, else running average; < 0.01 inactive), palettes 0x725410. ePreDisplay 0x504C50: TickSFX,
DoTunnelBloom 0x72AAC0, DoTinting 0x725580; ePostDisplay 0x504D00: UpdateAllScreenEFX(1/30).
* SE_TINT is never drawn on PC (sub_6D44B0 gated by [0x901804], only ever written 0).
* SE_GLARE (tunnel bloom): in a tunnel zone above the car, nearest bloom region, zone edge within 40 m
  (GetSegmentNextTo 0x723330); quad on that edge from ground to +10 m; +0.04 per frame to 1, within 17 m d x 0.0588 x
  previous (PC /17, decomp /1.7); new region/zone resets; outside -0.02 per frame. eDisplayFrame 0x6DEFCE draws it white
  into back-buffer alpha only; the overbright pass (§9) uses it.
* MOTION_BLUR / VISUAL_SIG / FE_BLUR: no PC renderer. No fades or flashes come from ScreenEffects on PC (fades are
  GameFlow's). Ported: DB, TickSFX/Pre/Post, DoTinting, DoTunnelBloom -> `ScreenEffects.Glare`.

### EventManager
emEventManagerInit 0x725700 (pools 0x24 x 0x3C, 0x18 x 0x14); LoaderEventManager 0x72B130 reads chunk 0x80036000 in
every STREAML2RA section (0x36001 header, 0x36002 vAABB tree, 0x36003 triggers); emProcessAllEvents from MainLoop
0x663E4C; emAddHandler 0x72CC80's only caller is the EAXAITunerCar ctor 0x4B9BEA. Data: 13,144 triggers, all EventID
0x10007 (TRIGGER_EVENT_SOUND): drive-by audio only, already ported (Core/EventTriggers.cs, Runtime/Audio/WorldAudio.cs).

## 9. End-of-frame post chain (overbright, visual treatment)

Code: `Runtime/VisualTreatment.cs`, `Shaders/VisualTreatment.shader` (motion blur passes 4/5 unchanged). Probe `post`.
* Targets (0x6C2E60, A8R8G8B8): two full-size scene copies (0x93DEF8/FC, surfaces 0x93DE60/64; current [0x982C60]); four
  quarter targets 0x982964..70 (surfaces 0x982990..9C; A, B, C at W/4 x H/4, the fourth W/32 unused); the 256x1 spline
  0x982900 (sub_6C3B20: r black curve, g colour curve; point, clamp).
* Option gates (preset 0x6C1990, level 4+): [0x901828] visual treatment on, [0x9017FC] overbright on, [0x901800]
  sub_6D3850 forced off.
* Filter primitive sub_6D2CD0(dst, src, taps, weights, mode): quad with up to 4 texcoord sets, tap = {weight, du, dv}
  in source texels; mode 1 overbright.efx, 2/3 filter.efx passes 4/5, else pass taps-1. filter.efx pass n-1 weighted
  sum, pass 4 puts dot(rgb, LUMINANCE_VECTOR (0.6125, 0.5154, 0.0721)) in alpha, pass 5 outputs rgb x a.
* Order: (1) sub_6DBEC0: copy back buffer -> copy0; A = pass 4 of copy0 with 4 taps 0.25 at (+-0.5, +-0.5) texels
  (0x8F9560, a 4x4 box); C = pass 5 with the same taps (without VT/rain/pixelation: point StretchRect to A, then C).
  (2) sub_6DBFE0: [0x8F9474] = 2 iterations of horizontal + vertical blur, taps 1/3 at +-1.5, 1/6 at +-3. (3) rain drops
  sub_6D4620 (§8); pixelation sub_73D0C0 / sub_6D49A0 off. (4) the sun sprite sub_6D04E0 (§5). (5) VT sub_6D53D0 (copy
  [c60] + A -> the other copy when overbright follows, else back buffer; tech 0 ps_2_0); CPU sub_6D4EF0 with s =
  [0x8FAE64] sun strength (0x6C83D0): BlackBloomIntensity = (x, 1-x), x = (0.6 - 0.05 s) look;
  ColourBloomIntensity = look; Tint = (1-k) tint + k lum(tint), k = 0.3 + 0.3 s (lum 0.2125/0.7154/0.0721);
  Desaturation = (d, 1-d), d = 0.9 - 0.4 s (replaces the look's value); VisualEffectBrightness = field + 1; GAINMAP =
  UVESVIGNETTE (GLOBALB 256x256: g vignette shape, b brightness 1 centre .. 0.39 corners); out = (scene curve.y CBI tint
  + desat (curve.x BB.x + BB.y) + gain.y Vignette) Brightness gain.z, curve = spline(A.a, A.r). (6) Overbright
  sub_6DC040: A = pContrast(C): t = sat(2 (C - off)), rgb = t dot(t, 0.5), a = 0; off = max((1 - I)(0.3 + 0.2 cloud), 0)
  under the glare, else 0.35 + 0.5 cloud, capped 1; grey scale 0.5; 2x blur; sub_6D3B80 adds A over the copy with
  BLENDFACTOR / ONE, factor ftol(0.6 x 255) (0.8 without VT), under glare ftol(I x [0x8FAE64] x 200); one-tap copy out.
* Round 3: the player camera renders LDR (allowHDR off, like the exe's A8R8G8B8 back buffer, §10); the motion-blur
  blend (pass 5) keeps the frame's alpha, so drawing the mask after the motion blur and the particles gives the exe's
  alpha (particles write no depth, the exe's motion blur writes RGB only); the sun query (§5) runs before the mask.
  ENISMotionBlur / ICEManager drive the blur target through ____icemanager_isplayingsomething (effects.md §5).
* Alpha mask: the frame is cleared to 0x00000000 after Present (0x6E7628); the world writes RGB only (ColorWriteMode 7);
  the glare quad writes alpha (0x6DEFCE); the sky's pass 0 is redrawn with ColorWriteMode 8, ZFUNC LESSEQUAL
  (0x6DF409); the sun sprite writes RGBA. So only visible sky, the tunnel glare and the sun bloom.
* Ported: pass 0 the box with the exe luminance in alpha; 1/2 the exe taps twice; 3 the exe luminance + gain map,
  point-filtered spline; SetTreatmentConstants (sub_6D4EF0); 6 overbright source, 7 pContrast, 8 the add; 9-11 the
  alpha mask in a BeforeImageEffects CommandBuffer (clear alpha, glare quad from ScreenEffects.Glare, the SKYDOME_XENON
  mesh at SkyBox's far-plane z, ZTest LEqual, Offset -1,-1), then SunRender. Statics VisualTreatmentEnable,
  OverbrightEnable (default true).
* Not ported: sub_6D4100 (gate [0x982C38] never written [?]), the X360 path, the 1_1 VT technique, sub_6D3850.
* Deviations: [ours] the mask is drawn after the motion blur and particles (both RGB-only in the exe; passes 4/5 still
  lerp alpha); without VT the box source is used instead of the point tap; no overbright in the front end or outside
  play mode; the gain map is decoded to raw values in Linear colour space. [?] front-end tint k ([0x982C78] x 0.001)
  kept 0; glare quad depth test assumed LEqual.

## 10. Colour space (affects every MW shader)

ProjectSettings `m_ActiveColorSpace: 0` = **Gamma** since round 3 (the user's decision): every texture, vertex colour
and constant now reaches the shaders as the exe's gamma-space value and blending happens on gamma values, as in D3D9.
Review of this area (2026-10-24):
* C# constants: TimeOfDayDriver (_MW_LightDir/Diffuse/Ambient/Specular/fog/cloud), CarEnvironment, WorldShadows,
  SkyRender, VisualTreatment (curves, tint, spline texture), LightFlares, SunRender, Rain, EmitterSystem and
  SkidMarks all pass raw exe numbers with SetGlobalVector / SetFloat / vertex colours - no linear conversion anywhere;
  Material.SetColor is not used for exe constants. Nothing to change.
* The three colour-space branches (MWLighting.cginc MWRawData for normal/specular maps, VisualTreatment's gain map,
  MovieYUV) compile to no-ops in Gamma; kept so a Linear build stays correct.
* Changed: the player camera had HDR on (scene m_HDR 1): additive layers (flares, particles, overbright) stayed above 1
  in a half-float target and the later blends / copies saw them. VisualTreatment now sets allowHDR = false: an 8-bit
  target clamped after every blend, as the exe's back buffer. (The reflection and env-map cameras were already LDR.)
* Particles: Particle.shader no longer applies Unity fog (particles.efx has no fog, the exe sets D3DRS_FOGENABLE 0 at
  device setup 0x6CFD59); with RenderSettings.fog off it was already inactive.
* Unity's directional light colour (TimeOfDayDriver) is DiffuseColour raw; only the character shaders read it.

## 11. Deviations and unknowns (summary)

* [ours] Missing normal/specular map or tangents -> world.efx (exe: material skipped).
* [ours] Blended world materials keep the port's depth bias and queue.
* [ours] Road reflection refreshed only when wet; drawn without shadows; smackables not reflected; car elevation by ray.
* [ours] Sun query result 1-3 frames late (exe 4); ray fallback without async readback.
* Sun core alpha wraps to 0 at full visibility - confirmed in code (§5).
* World shadows (§13): Unity's cascaded sun map instead of the exe's 1024² light-space perspective map; the map-edge
  fade term replaced by Unity's distance fade [ours]; ATI DF16 manual-compare techniques (4-6) not used [?].
* [?] SHD_ instances flagged 0x100 (1.4k of 26k sampled, all SHD_*_ROAD/PROPS/TERRAIN, effect 0, alpha blend) get render
  flag 0x20000 and draw with shadow_cg, whose ShadowColor no code sets (default 0: rgb 0, alpha 0, i.e. invisible
  under SRCALPHA blending). The port still draws them with world.efx lighting; check against the real game whether
  those baked shadows show (if not, hide flag-0x100 instances).
* [?] SKY_SPECULAR never drawn on PC; Normal/Rain Fog FogQuery has no shader reader.
* Not ported: the WorldModel light rig; particles' shadow technique (particles.efx shadowLevel 1: rgb x sat(shadow +
  0.5)); the DOF (no depth-of-field pass exists on PC: none of the 31 effects has one, DOFullScreenAA is the
  full-screen anti-aliasing option).

## 12. Check in play

1. Roads and pavements: visible normal-mapped bumps and a sun highlight (pow 11) moving with the view; dimmer
   highlight in cloudy areas (x (1 - cloud)).
2. Walls, cliffs, barriers (effect 3): normal-mapped lighting; no seams at mesh edges (tangent sign).
3. Trees: the distant haze dims their sunlit side; leaves keep their alpha cut-outs.
4. Windows/facades: a WINDOWREFLECTION sky picture that slides as you turn, plus a tight sun glint on glossy panes.
5. Blended world layers (road decals, baked SHD_ shadows, neon): lit and fogged like their surroundings.
6. Rain (§8, §4): as the road gets wet (0.005 per frame) the roads show upside-down lamp posts, traffic lights, trees,
   barriers and buildings (only within ~32 px size), upside-down cars under each car, a bright headlight pool 3-23 m
   ahead of every car and vertical light streaks under head/brake lights (longer the wetter: x1.2..x10 stretch, half
   for car lights). None when dry. Detail 3 (RoadReflection.Detail) blurs it.
7. World shadows (§13), clear midday: props (street lights, signs, bins, barriers, bushes, traffic lights - instances
   flagged 0x40000000) and cars cast sun shadows onto roads, walls and glossy facades; the shaded side is dark (sun
   term x 0.1 at midday) and lighter at dusk / under clouds (x 0.9); nothing beyond 400 m; 4-tap soft edges near,
   hard 1-tap beyond 100 m. In a tunnel the shadows fade out (InvShadowStrength + tunnel fade). Trees and buildings
   that are not flagged cast nothing (as the exe). Watch the frame rate: casters now render into the sun map.
8. Sun: look at the sun through tree branches / past a building edge: the sprite fades out behind trees now (GPU query)
   and the 60 px core disappears when the sun is fully clear (exe byte wrap). Turn away at dusk: a mirrored sun may
   appear opposite (exe, no w test) - SunRender.MirrorBehindCamera = false hides it.
9. Streaming (§7): zone switches every 100-250 m, sections unloading behind and loading ahead.
10. Bloom (§9): only sky edges, the sun and tunnel exits bloom; with speed motion blur the bloom mask is unblurred.
11. LOD distances (§14) are now resolution dependent (H from the screen width): at 1080p props switch to the detailed
    model ~2.25x farther than before; check the frame rate and pop-in against the real game at the same resolution.
12. After a reset (manual or automatic) the car blinks for 2 s: hidden 0.05 s of every 0.12 s, flares stay lit.
13. If any world material is magenta, a shader failed to compile: check the console for World / WorldReflect /
    WorldNormalMap / GlossyWindow / WorldDecal / WorldTransparent (MWShadow.cginc), WorldNoFog, SunQuery; set
    WorldShadows.Enabled = false / WorldMaterials.Enabled = false to compare.

## 13. World shadows (Runtime/World/WorldShadows.cs, Shaders/MWShadow.cginc)

* **Which path a default PC takes**: preset 0x6C1990 sets g_ShadowDetail [0x901830] = 1 at performance level 4 (0
  below); sub_6C1510 sets [0x982C08] = 1 on NVIDIA when D24S8 is a depth-texture format (CheckDeviceFormat usage 2),
  2 on ATI with DF16, else 0; sub_6D2100 takes [0x982C3C] = the highest shadowLevel of the effects (world 6,
  worldreflect / worldnormalmap / GlossyWindow 5, car 5, particles 4) capped at 2. A current PC at the top preset
  therefore uses world shadow maps; technique levels 1 = hardware 1 tap (texldp), 2 = hardware 4 taps at +-0.5 texel
  (ShadowMapScale = 1 / 1024), 3 = 16 taps, 4-6 = the ATI manual-compare versions (cmp on z, Poisson taps).
* **Shadow view 0xF** (eDisplayFrame 0x6DEC4A -> sub_6E54E0 -> sub_6E44C0, while [0x8FAE68] >= 0.1): a 1024 x 1024 depth
  map (GetShadowWidth / Height 0x6C86B0) with a light-space perspective projection fitted to the player view
  (D3DXMatrixPerspectiveLH / OrthoLH, not ported); it draws RenderWorldModels (0x7538D0: smackables, the checkpoint),
  StuffScenery(view 0xF, 0x2000) = **instances flagged 0x40000000** (g_ShadowDetail 2: arg 0x200 adds 0x100000 /
  0x200000 without 0x4000000) and, with [0x903320] = 1, the camera's car and every loaded car (vtable +0xC). Effects:
  car -> car_shadow_map, world -> worldnofog / worldnormalmapnofog (alpha test kept). Harness `wrefl flag 40000000 3`:
  5.2k of 25.6k sampled instances: street lights, bins, boxes, traffic lights, medians, bushes, pylons, orange barrels,
  campus props, parking blocks, toll booths, chevrons, trash cans, crash barriers, hydrants, phone boxes...
* **Receivers** (sub_6E0AB0, player view 1 only, effect ids via the table at 0x6E0E04): while [0x8FAE68] > 0.1
  ([0x8FAE50]), the view-space depth of the draw's bounding-box centre picks the technique: <= 100 m level L (2),
  <= 200 m max(L-1, 1), <= 400 m max(L-2, 1), beyond 400 m ([0x8FAE60]) technique 0. Shadow pixel shaders (world 0x9F84 /
  0x8DC0, worldreflect 0xAAF0 / 0x95AC, worldnormalmap 0xA604 / 0x91EC, GlossyWindow 0x6C44 / 0x5A38) all compute
  `f = sat(taps + sat((0.2 - t.y/t.w) * 5) + InvShadowStrength)` and multiply only the sun terms: world / decal
  `2 tex (prelit + sun f) + inscatter`; worldreflect / worldnormalmap `(tex sat(n.L) Diffuse + spec s) f + tex prelit`;
  GlossyWindow `2 (tex prelit + (tex sun + 2 a spec) f) + ...`. Trees (tree.efx) have no shadow technique.
  InvShadowStrength = [0x982BA8] tunnel fade + 1 - [0x8FAE68] (as car.efx, car_render.md §12).
* Port: WorldStreamer gives instances flagged 0x40000000 (and smackables) caster material copies with the ShadowCaster
  pass on (World.shader's pass; the other world shaders fall back to it) and shadowCastingMode On; every other world
  material has the pass off (WorldShadows.Prepare), so it neither casts nor enters the camera depth pass. A light
  CommandBuffer (AfterShadowMap) publishes Unity's sun shadow map as _MW_SunShadowMap and _MW_SunShadowValid; the
  shaders select the cascade by Unity's split spheres (shadowProjection StableFit) and sample it with 4 taps at +-0.5
  texel (<= 100 m) or 1 tap (to 400 m); QualitySettings.shadowDistance = 400; the sun's map is 2048² (Resolution).
  [ours] Unity's cascades stand in for the light-space perspective map, Unity's shadow fade for the map-edge fade, the
  object origin for the bounding-box centre. Globals: _MW_WorldShadows, _MW_InvShadowStrength, _MW_SunShadowTexel.

## 14. Scenery LOD pixel scale

SceneryDrawInfo's pixel size uses the view's H (0x6BE9F0, called from 0x6CF400): (__window_width [0x982BE4] / 2) /
tan(horizontal fov / 2) - real screen pixels. WorldStreamer.AddLod assumed a 480-line screen (pixels = 240 x Unity's
relative height); it now uses half the main camera's pixel height, so the 18 px cull and the 8.7 x density detail
threshold scale with the resolution as on PC. The reflection view uses the same H (W/2 x projection m00).

## 15. Last mile (round 4): baked shadows, overlays, PVS, loading bar, particles, env views, parameter maps

### 15.1 SHD_ baked shadows are drawn with shadow_cg (resolves §11's [?])
* 0x6DABFC swaps world.efx / worldnofog for shadow_cg (effect 22) on render flag 0x20000 unconditionally, and StuffScenery
  sets it for instances flagged 0x100 (the SHD_*_ROAD/PROPS/TERRAIN layers). shadow_cg's ShadowColor is **set**: its slot-1
  per-frame function sub_6C6630 (vtable 0x8AF298) writes parameter 74 SHADOWCURRENTGEN_COLOR = (rgb [0x9018D0..D8] = 0.5,
  0.5, 0.5; a = [0x9018CC] 1.0 × [0x8FAE68] = 0.1 + 0.8·sun shadow strength) in the player view (render-target slot 0; other
  slots: rgb 0.5, a = [0x9018C8] 2.0 × strength); without the shader technique FFSKYCOLOR = (0, 0, 0, strength) instead.
  ps_1_1: rgb = tex·vc·0.5, a = 2·tex.a·vc.a·ShadowColor.a; no fog, blend from the material (SRCALPHA/INVSRCALPHA). So the baked
  shadows darken to half and fade with the sun's shadow strength (clouds / dusk), like the real-time shadows.
* Port: Shaders/WorldShadowCg.shader, WorldMaterials.ShadowCg (copies per material), WorldStreamer assigns them to flag-0x100
  instances (WorldStreamer.CurrentGenShadows = false restores the lit decals), TimeOfDayDriver publishes _MW_ShadowCgColor.

### 15.2 TrackStreamer: audit against the exe (function table in the round-4 agent report)
* Dead on PC: HibernateStreamingSections (ret), DoHoleFilling (no callers), AmountNotRendered / WasRendered (never written /
  never read), the LRU unload (memory only, only sees sections already out of the set), disc bundles (none in the data),
  DisableZoneSwitching (no callers), TSMemoryPool (PC sections come from bMalloc).
* **Loading bar threshold is 3.0 m** (0x730F11 fcomp [0x8B27CC] = 0x40400000), not the decomp's 5 m; fixed in WorldStreamer.
  The hook 0x64B770 (pause "TrackStreamerLoadingBar" + fade) is wired in GameFlow.OnLoadingBar (§7's "not wired yet" is stale).
* What draws (GrandSceneryCullInfo::WhatSectionsShouldWeDraw 0x731870): per view, the drivable section under the **view's
  camera eye** (Camera +0x40; the shadow views use the player camera); none → every loaded header with number % 100 < 10; else
  the Z row 2600..2699 plus that section's 0x34153 list. WorldStreamer.UpdatePvs now keys on Camera.main (the focus points only
  without a camera).
* **Visible-section overlays** (chunk 0x34158, VisibleSectionManager::Loader 0x731E70): {i32 3, i32 3, Name[40], i32 NumEntries
  @0x30, 6-byte entries @0x34 {u8 AddRemove, u8 pad, i16 DrivableSection, i16 Section}}. L2RA: FlyBy (500 entries: 447 removes,
  53 adds over 36 drivable sections) and E3Demo (garbage count → nothing). CAnimScene::Init (0x450DB0) calls ActivateOverlay
  (0x731D80) with the NIS scene header's SeeulatorOverlayName ("FLYBY" on the Blacklist intro scenes and the flythrough): each
  entry adds / removes the section in the drivable section's visible list (re-sorted), the inverse goes into an "Undo" overlay,
  the preculler is disabled and RefreshTrackStreamer (DetermineCurrentZones + SwitchZones) re-runs, so the streaming set
  follows the edited lists; DestroyAnimScene → UnactivateOverlay (0x72DFF0) applies the undo. Port: VisibleSections.Overlays /
  ApplyOverlay / UndoOverlay (Core/Track.cs), WorldStreamer.ActivateOverlay / UnactivateOverlay (static; invalidate the PVS and
  request a zone switch), one-line hooks in NisPlayer.Begin / Finish. [ours] the preculler is not modelled.

### 15.3 SkyRender / Sun / TimeOfDay.cpp / WorldModel: nothing visible left
* StuffSkyLayer's brightness argument and [0x8FADE0] (EnvSkyBrightness, TOD +0x64 = 2.5 for RVM / env views) act only on the
  fixed-function path (0x6DB4E3..); no reader on the shader path. GetSunPos / GetSunIntensity / SetCurrentSunInfo() have no
  callers; the "mirrored sun" (StuffSpecular / SKY_SPECULAR) does not exist on PC. RenderSunAsFlare's two pool flares: mask
  0xFE → drawn in the env-map views (now ported into the env cube, car_render.md §15.2), mask 0xFC00F6 → RVM only.
* TimeOfDay.cpp 0x768C20-0x768CEC is the eTimeOfDay enum (MIDDAY / SUNSET stream-file suffix); gTheTimeOfDay is never
  written (SetCurrentTimeOfDay / TickOverTimeOfday have no callers) → always MIDDAY; unrelated to the float time of day.
* RenderWorldModels 0x7538D0 is called only for view 1 (0x6DED79) and the shadow view 0xF (0x6E5110): smackables, the checkpoint
  and fragments cast shadows and are never in view 4 / RVM / env maps (§4's "smackables not reflected" is the exe's behaviour,
  not a deviation). The WorldModel light rig (CarsInGame) only matters for car-material fragments, which already use it.
* mCar_elevation (+0x116C) is set in DrawAmbientShadow (view 1, in game) from WWorldPos::FindClosestFace + HeightAtPoint under
  the car origin — the collision-mesh height, which the port's ray down from the body approximates [ours].

### 15.4 Particles: the shadow technique
particles.efx technique 1 (shadowLevel 1, ps_2_0): one texldp tap, rgb × saturate(shadow + 0.5), alpha untouched (technique
2 is the ATI shadowLevel-4 form: cmp + 0.3). Port: Particle.shader samples MWShadow.cginc's raw tap (MWSunShadowTap, no edge
fade / InvShadowStrength) with the particle position as the draw centre [ours]. The map-edge fade of the world techniques
stays Unity's shadow distance fade [ours]; the sun-query latency stays 1-3 frames [ours] (§5).

### 15.5 Env-map views (cross-reference)
The env cube now draws what the exe's views 0x12-0x17 draw (sky layer 3 at 0.0065, scenery, flares, the sun pool flare; no
cars / fragments / smackables / rain / particles; 256² faces, near 0.5, far 100, face schedule by g_CarEnvironmentMapEnable):
car_render.md §15.2.

### 15.6 Parameter maps: every reader on PC (closes §5's [?] on FogQuery and the tint)
PC code 0x73D560-0x75EBD0 (the coverage map's 0x5DF1F0 range is GRaceParameters). Accessor instances (static inits): Speech
0x91474C, Reverb 0x914A84, Ambience 0x914A30, Rain 0x9B2A90, Clouds 0x9B0C3C; per view: Wind 0x905800/30, "Normal Fog"
0x905860/90, "Rain Fog" 0x9058C0/F0 (BlendByDistance), "Fog Blend Distance" 0x905920/3C, "Screen Tint SunRise" 0x9AA4E0/510,
"Screen Tint Midday" 0x9A3AD0/B00. Readers: Rain / Clouds (Rain::UpdateAndRender, camera eye), Wind field 1 only (Rain::Wind
0x753B50, CaptureData-by-distance 50 m; field 0 unread; the layer is 0° everywhere), Normal Fog / Rain Fog / Fog Blend
Distance → FogQuery::CalculateFogInfo 0x753D90 (eDisplayFrame 0x6DEBB2, view 1 only, last frame's rain / cloud): fields 0
DistFogPower, 1 DistFogStart, 2-4 colour R/G/B, 5-7 FogFalloff/X/Y; Normal Fog blended over a hard-coded 50 m, Rain Fog mixed
in by w = rain (or cloud/3) when > 0 over the Fog Blend Distance field (50). **Nothing reads the FogQuery** (whole-image scan
of 0x9AE364..) except its write of Rain's precipitation fog colour (+0x34A8..B0), whose only consumers build SE_TINT, which
is dead three ways (DoTinting / the paused branch feed sub_6D44B0, which has no caller; the gate [0x982B29] = active &&
[0x901804], and 0x901804 is only ever written 0). The four Screen Tint layers and FA9F8DAA (0 nodes, value 0) have no reader.
Audio: Reverb fields 0/2 (SFXCTL_Tunnel::UpdateCityVerb 0x4BEA70), Ambience field 0 (SFXObj_PFEATrax 0x4DF6D0), Speech
field 1 (SoundAI::SyncPlayers 0x71D080, sim → game axes). Port: FogQuery in Core/Weather.cs (Rain.Fog, a probe; writes
Precipitation.FogR/G/B as the exe), the Wind accessor now uses the by-distance overload (it called the ratio overload with
50: a latent bug, invisible with the 0° layer). ParameterMaps.cpp is therefore fully ported: the reader, and every consumer
with a visible or audible effect.

### 15.7 Check in play (additions)
14. Baked road shadows (under overpasses, around props) are now half-dark decals that fade under clouds and at dusk; they no
    longer take the world lighting / fog. Compare with the real game; WorldStreamer.CurrentGenShadows = false shows the old look.
15. Loading bar comes on at 3 m instead of 5 m from an unloaded road section (only on far-away warps).
16. A Blacklist intro NIS ("FLYBY" scenes): far sections vanish / extra ones appear while the scene runs, back afterwards.
17. Chase camera straddling a section boundary: the drawn set follows the camera, not the car (no more pop when they differ).
18. Particles in sun shadow (smoke under a bridge) are dimmed to 50 %.
19. Car reflections (car_render.md §15.2): the paint shows the sky, scenery and a sun glint but no cars; distant cars
    (< 18 px) lose the reflection; the cube refreshes one face per frame.
20. Showroom cars take the garage rig (fixed world-space key / fill lights, mirrored in the car lot); NIS characters are lit
    from behind the camera (camera-relative key / fill).
21. Cars: brake discs show only up to LOD B, tyres to D with the body LOD; the rear wheels seen from inside show the B rim;
    the interior is visible through the glass at every distance; a bumper camera no longer shows the own body.
22. Body motion: squat / dive / roll with acceleration, a shift nod, engine shake while revving in neutral, the body 2.5 cm
    lower on cars with ecar RideHeight −1; blown tyres sink 12 cm, wobble and tilt the body; the drawn wheel roll steps at most
    360/(2·spokes) − 1 degrees per frame.

## 16. Round 9: Track / TrackInfo / TrackPositionMarker / RaceParameters / TrackPath (Core/Track.cs)

* **TrackInfo** (0x34201 in GLOBAL\GlobalB.lzc, 0x120-byte records; GetTrackInfo 0x722840 = first record whose s16
  TrackNumber +0x8A matches): Core/Track.cs `TrackInfo` (full record, cached), for the 14 readers (WorldMap, Minimap,
  GManager::CalcMapCoordsForMarker, GRaceParameters::GetBoundingBox, TrackLoader / RegionLoader, race starters). The UI
  keeps its own calibration reader (Runtime/UI/Minimap.cs).
* **TrackPositionMarkers** (0x34146, LoaderTrackPositionMarkers 0x7270B0 links the 0x30-byte records into [0x9AA494] and
  calls sub_5F19D0): records read by Core/NisContent.cs TrackMarkers; Core/Track.cs `TrackPositionMarkers.Count`
  (GetNumTrackPositionMarkers 0x727170: query track 0 = any) and `.Get` (GetTrackPositionMarker 0x727280: the marker's own
  track 0 = any track).
* **TopologyCoordinate::GetElevation** (0x726F60): a WWorldPos face query (FindClosestFace 0x789CC0, HeightAtPoint
  0x768200); its callers are 0x726FE0 / 0x727060 (no callers) and RenderVisibleSectionBoundary 0x728D40 (debug): dead.
  LoaderTrackOBB / GetTrackOBB: Core/CollisionPack.cs TrackObb (collision owner). EstablishRemoteCaffeineConnection
  0x7224F0 / TrackPathInitRemoteCaffeineConnection 0x723310 are `ret`.
* **RaceParameters** (TheRaceParameters 0x904FB8): InitWithDefaults 0x733B00 ported as Core/Track.cs
  `RaceParameters.Defaults()`; every other method is an empty stub on PC (AddDriverInfo 0x733BD0 ret 4, 0x733C20 /
  0x733C30 return 0, nullsubs without callers). Races run from GRaceParameters (gameplay).
* **TrackPath**: barriers (Core/RoadNetwork.cs ReadBarriers), zones and FindZone (Core/WorldAudioData.cs,
  Runtime/Game/Police/PoliceZones.cs, Core/Weather.cs), DoLinesIntersect (Core/TrackStreamerRules.cs), GetSegmentNextTo
  (Core/Weather.cs). BuildZoneInfoTable / FindZone's 64 m zone cache are lookup accelerators: same first-match order
  (memory order, resuming after prev_zone) without them.

## SimpleModelAnim (World/SimpleModelAnim.cpp, round 9; Runtime/Nis/SimpleModelAnim.cs)
* Two scenery solids spin: gSimpleSolidHashList (0x904B44, 2 × {hash, type, speed, angle, last}) = XO_WINDMILL_BLADE_1B_JG_00
  ROTATEY (2) and XB_DONUTSIGNB_1B_BK_00 ROTATEZ (4), both 50 °/s (hashes filled by Init 0x73D2C0 from eInitEngine; both solids
  are in STREAML2RA.BUN). Update 0x73D320 (ePreDisplay, every drawn frame): angle += WorldTimer delta (ticks [0x925AE8] ×
  0.00025) × 50, one "≥ 360 → −360"; Reset 0x73D300 (BeginWorldLoad) re-bases the clock.
* Animate 0x73D3C0, called by eViewPlatInterface::Render (0x6DAA2B) for every model in every view: on a NameHash match
  local_world = (I · eRotateX/Y/Z(bDegToAng(angle))) · local_world, i.e. the solid turns about its own local axis through its
  origin (bDegToAng truncating: (int)(angle·65536)/360). IsSimpleAnim 0x73D4E0 keeps the two out of sub_6D7C40's static batch.
* Port: a self-starting component scans each built section's MeshFilters for those mesh (= solid) names and sets
  localRotation = base · AngleAxis(−a, forward) (game local Y = Unity Z) / AngleAxis(−a, up) (game Z = Unity Y). Not ported:
  the road-reflection copies keep a fixed matrix [hook: RoadReflection owner, if wanted].
