# Sky, world lighting constants, time of day and parameter maps (speed.exe v1.3, PC)

Status 2026-10-24. Everything below was read from speed.exe: disassembled code (capstone), the embedded effects (PE
resources `IDI_*_FX`, parsed as fx_2_0 and disassembled with d3dcompiler_47), and attributes.bin / L2RA.BUN data. The
GameCube decomp (refs/nfsmw SkyRender.cpp, ParameterMaps.cpp, rain.cpp) was used for names only where it agrees.
`[?]` = not confirmed, `[ours]` = Unity-side addition.

Code: `Core/ParameterMaps.cs`, `Core/WorldLighting.cs` (TimeOfDay, FogConstants, SkyConstants, CloudState),
`Runtime/Sky/TimeOfDayDriver.cs`, `Runtime/Sky/SkyRender.cs`, `Runtime/VisualLighting.cs` (facade),
`Shaders/MWLighting.cginc`, `Shaders/World.shader`, `Shaders/SkyBox.shader`. `Core/SolidList.cs` now also reads the
second UV set of 44-byte vertices (effect 19). WorldStreamer.cs: two-line hook (SKYDOME_XENON no longer skipped,
SKYDOME* goes through SkyRender.Setup). Harness: `sky pmaps|dome|tod [secs]|clouds` (NFSMWStaging/Harness/SkyProbe.cs).

## 1. Effects table and effect objects

* The effect list is at 0x8F9BE4, stride 0x90: {file name, resource name, shader name, ...}. Effect id = index:
  0 world, 1 worldreflect, 2 worldbone, 3 worldnormalmap, 4 car, 5 GlossyWindow, 6 tree, 7 worldmin, 8 worldnofog,
  9 fe, 10 fe_mask, 11 filter, 12 overbright, 13 screenfilter, 14 rain_drop, 15 runwaylight, 16 visualtreatment,
  17 worldprelit, 18 Particles, 19 skybox ("skyshader"), 20 shadow_map_mesh, 21 skybox_cg ("SkyboxCurrentGen"),
  22 shadow_cg, 23 car_shadow_map, 24-29 depth effects, 30 worldnormalmapnofog. Solids store this id per material
  (+0x30); world scenery uses 0 (world.efx, stride 36), the next-gen sky dome 19 (stride 44).
* Effect objects live at [0x93DE78 + 4*id] (factory 0x6D6000). Each has a 102-entry parameter table at +0x44
  (built by 0x6C3D20; entry = 0x20-byte name, +0x20 hash, +0x24 D3DXHANDLE), the ID3DXEffect at +0x48 and a byte at
  +0x40 = "the selected technique's `shader` annotation > 0" (0x6D5FAF / 0x6D7585, technique chosen by the shader
  detail option [0x982C3C]). Handle offsets used here: AMBIENTCOLOUR 0x74, DIFFUSECOLOUR 0x204, CLOUDSCROLL 0x1B4,
  FFSKYBLENDFACTOR 0x434, FFSKYCOLOR 0x45C, FOG_BR_PLUS_BM 0x63C, FOG_ONE_OVER_BRBM 0x664, FOG_CONST_1/2/3
  0x68C/0x6B4/0x6DC, FOGVALUE 0x704, LOCALLIGHTDIRVEC 0x90C, SKY_TEXTURE_MISC_1/2/3 0xE0C/0xE34/0xE5C,
  SKY_DIFFUSESCALE 0xE84, SUNCOLOR 0xEAC, WORLDLIGHTDIRVEC 0xF9C.
* Per-effect lighting setup is vtable slot 1 (vtables 0x8AF204...): world-type effects (ids 0,3,6,7,8,14,15,18,30) use
  0x6D6AA0, the sky (19) uses 0x6C60F0.

## 2. World lighting constants (replaces the guesses)

All world effect parameters are shared (flags 1) and set every frame from the time-of-day object (section 4):

| parameter | value | code |
|---|---|---|
| DiffuseColour | min(TOD.Diffuse * [0x9018C4] (=1), 1) per channel, w = TOD+0x2C (1) | 0x6C6300 |
| AmbientColour | min(TOD.Ambient, 1) per channel, **w = 4.0** (TOD+0x3C, re-forced to 4 by every blend) | 0x6C6450 |
| SpecularColour | TOD.Specular rgb, w = Rain+0x36A0 (wetness, 0 offline); SpecularPower = [0x8FADC0] = 11 | 0x6D6AA0 |
| LocalLightVec | normalize(inverse(world) * TOD sun direction +0x180) — toward the sun | 0x6C8000 |
| Fog_* / FogValue | section 3 | 0x6D6CF0 |
| SunColor | (1,1,1,1) at 0x8FAE24, never written (not read by the world/sky shaders) | 0x6D6CF0 |
| ffSkyBlendFactor | never set (only its handle is looked up); fixed-function only | - |

world.efx technique 0 (vs_1_1/ps_1_1), now in MWLighting.cginc / World.shader:
```
oD1 = sat(L.N) * DiffuseColour                          (sun, NOT extinguished)
d   = clamp(|v - eye| * FogValue, 0, 2500); ext = 2^(-d (Br+Bm) log2e)
oT2 = clamp((Fog_Const_1 (1+cos^2) + Fog_Const_2 Fog_Const_3.x (Fog_Const_3.z cos + Fog_Const_3.y)^-1.5)
            * Fog_One_Over_BrBm * (1-ext) * Fog_Const_3.w, 0, 1),   cos = L . normalize(eye - v)
oD0 = (min(ext,1) * AmbientColour.rgb * AmbientColour.w * vertexcolour.rgb, vertexcolour.a)
ps:  2 * tex * (oD0 + oD1) + oT2,  alpha = tex.a * oD0.a      (mad_x2)
```
So prelit scenery gets 2 x 4 x ambient (0.14,0.14,0.10 at midday = 1.12/1.12/0.8) x vertex colour, PLUS the dynamic
sun 2 x N.L x diffuse, and the extinction applies to the prelit term only. The old port (tex x vc x 2, no sun,
extinction on the sun) was the reverse. The shadow techniques 1-6 (shader=3) only multiply the sun term by a shadow
factor. worldprelit.efx (id 17) is unlit (tex x vc x 2) and only used by the FE-type vtable; scenery never uses it.

## 3. Fog constants (0x6D6CF0, every frame, first thing in eDisplayFrame)

[0x901934] = 1 in the release exe, so the inputs come from the time-of-day object, not from the `fog` collection
(class EBCEE74C, key CE3B435B: BrMultiply 3, BmMultiply 0, ConstantBr 0.27.., ConstantBm 0.19.., FogG -0.15,
InScatterMulitply 8.1). Only that collection's **FogEnable** (1) is still read.
```
range = 0.01 - 0.0001                          ([0x90193C] - [0x901938], as a double)
br = FogSkyColourScale*0.01*range + 0.0001
bm = FogHazeColourScale*0.01 > 0 ? FogHazeColourScale*0.01*range + 0.0001 : 0
Br = br * FogSkyColour,  Bm = bm * FogHazeColour        (0x8FAE04 / 0x8FAE14)
Fog_Br_Plus_Bm = Br+Bm;  Fog_One_Over_BrBm = 1/(Br+Bm)
Fog_Const_1 = Br * 0.059683103 (3/16pi);  Fog_Const_2 = Bm * 0.07957747 (1/4pi)
Fog_Const_3 = ((1-g)^2, 1+g^2, -2g, FogInLightScatter),  g = FogSunFalloff
FogValue = FogDistanceScale
FogEnable == 0: Br+Bm = 0 (no extinction / in-scatter), Fog_Const_3.w = 0
```
Midday (harness `sky tod`): Br+Bm = (0.000099, 0.000150, 0.000240), FogValue 4, in-scatter 10, g 0 ->
extinction 0.98/0.97/0.95 at 50 m, 0.78/0.69/0.55 from 625 m on (d capped at 2500).

## 4. Time of day (it exists, and it runs)

Object at [0x9B392C] (0x190 bytes, ctor 0x770D70, created by 0x6BF290). Layout in Core/WorldLighting.cs. It holds
eight `timeofdaylighting` instances: front end set sunny / dusk_sunny / overcast / dusk_overcast, in-game set
midday A01A3942 / 0E1C797C (dusk) / 94ABDFF0 (midday overcast) / 919B6689 (dusk overcast).

* **Update 0x769360, every frame (eDisplayFrame 0x6DE3DC)**: `time += RealTimeElapsed / 60 * dir * speed * 0.05`
  (speed +0 = 1, dir +4 = -1 at start, time +8 = 0.45), bouncing between limit and 1-limit (limit 0.35 in the front
  end, game flow state 3, else 0.05). The online manager can drive it from a server clock (0x735620/0x735A30) - not
  ported. Then e = (clamp(time) - 0.5) * pi, a = +0x0C (3.28), b = +0x10 (0.5):
  `sun = (sin e cos a + sin a sin b cos e, sin b cos a cos e + sin e sin a, cos b cos e) * 100000` (+0x170),
  normalised into +0x180 (toward the sun, game space). Sines are the exe's 16-bit bAngle sine (0x45DB30).
* **Blend 0x769580(cloud), every frame**: dusk = 1 - (clamp(sunZ, 0.25, 0.5) - 0.25) * 4;
  high = lerp(cloud, midday, 94ABDFF0), low = lerp(cloud, 0E1C797C, 919B6689); TOD+0x20.. = lerp(dusk, high, low);
  Ambient.w = 4. Front-end set when [0x901828] != 0 (0x6C0A20) [?: never seen set].
  Fields carried (0x768EE0): Diffuse, Ambient, Specular, CarSpecScale, EnvSkyBrightness, FogSkyColour, FogHazeColour,
  FogSkyColourScale, FogHazeColourScale, FogInLightScatter, FogSunFalloff, FogDistanceScale, FixedFunctionSkyColor.
* **SetTimeOfDay 0x7696B0**: races whose gameplay attribute **TOD** (0x9DFF3C3D) is >= 0 set it at start
  (0x5FD210, GRaceDatabase::SetStartupRace 0x600AB0). In gameplay.bin: 16_2_1_d_day_intro, 16_1_0_partial_dday,
  16_2_2/16_2_3 sprints, 1_5_2, 5_2_2, 9_5_3, 10_2_1, 11_1_2 use 1.0 (clamps to 0.95 = dusk), 16_1_1_circuit 0.5;
  all others -1. `VisualLighting.SetTimeOfDay(tod)` is the port; **not yet called** (RaceEvent has no TOD field).
* Consequence (harness `sky tod 2400`): in free roam the sun leaves the midday set after ~5.5 min, the lighting reaches
  the dusk set at ~7.5 min (time ~0.09), bounces at 0.05 after 8 min, passes midday again ~16 min later and reaches the other
  dusk end (0.95) at ~26 min. The azimuth swings too (sun x 0.09 -> 0.98 -> -0.95). So PC MW is NOT always midday: it
  is midday for ~12 of every 18 minutes. `TimeOfDayDriver.RunTimeOfDay = false` freezes it [ours].
* 0x6C83D0 also derives a shadow strength from the sun height: clamp(sunZ-0.14, 0, 0.537)/0.537 * (1-cloud)
  ([0x8FAE64], [0x8FAE68] = 0.1 + 0.8 s); exposed as TimeOfDay.ShadowStrength; it feeds the car's and
  the world's InvShadowStrength and the world shadow gate (world_render.md §13).

## 5. Clouds and the parameter maps

* Chunk 0x8003B600 in TRACKS\L2RA.BUN (offset 0x329C0, 25.6 KB). Format and quadtree walk in Core/ParameterMaps.cs
  (ParameterMaps.cpp matches PC). Layers: Ambience (int), Reverb (int,float,int), Speech (int,int), unnamed FA9F8DAA
  (int), Screen Tint SunRise/Midday + 1F9EB90F + 0569A59E (3 floats, single set), Clouds (2 floats, quad16), Rain
  (2 floats, quad8), Wind, Normal Fog / Rain Fog (8 floats), Fog Blend Distance (50).
* **Cloud intensity** = Rain +0x290 of the player view, updated in Rain::UpdateAndRender (0x758100, game flow state 6):
  rain target = SetOverRideRainIntensity value ([0x9B0A48], 0 offline unless an NIS sets it); cloud target = the rain
  target if > 0, else 0x753AC0: `r = 1 - max(sin((0.7142857*t + 0.02*x) deg), 0)` with t = [0x8F8308] =
  **WorldTimeSeconds (Sim::GetTime, 0x73C980 / 0x654030), which advances** (online: server seconds mod 503), and the
  Clouds layer at the camera gives field0 if r <= field1, else twkCloudsMinAmount (0). (Corrected 2026-10-24: the
  first version of this spec took t as a constant 1. Rain uses the same formula with cos, so rain and clouds alternate
  over a 504 s cycle; see world_render.md §8.) An override of 0 does not mean "no rain": it means "the maps decide";
  only a negative override (Game_SetChanceOfRain < 0.01, e.g. the D-Day races) disables rain. Easing: rain += (target-rain)*dt, cloud += (target-cloud)*dt/10 (dt = WorldTimeElapsed),
  snap to 0 below 0.01 when the target is 0, clamp to 1.
  Result (harness `sky clouds`, still with the old fixed t = 1): an east-west band x ~ 2400..6550 is at 0.5 (one cell
  1.0, one 0.2), elsewhere 0. The port now lives in Core/Weather.cs + Runtime/World/Rain.cs (harness `weather`);
  CloudState.Update / RainAt / CloudChance in Core/WorldLighting.cs are superseded and unused.
* The cloud value drives the lighting blend (overcast sets), the fog (overcast: FogDistanceScale 10, haze scale 1)
  and the sky's CloudIntensity.
* Other consumers: SFXCTL_Tunnel (Reverb fields 0/2), SoundAI (Speech field 1), SFXObj_Pathfinder (Ambience) - audio,
  not wired here (the reader is in Core). Normal Fog / Rain Fog are evaluated each frame into a FogQuery at 0x9AE364
  (sub_753D90 from 0x6DEBB2) but no reader of its result was found on the shader path [?]; not ported.
  Screen Tint layers: ScreenEffects accessors exist (static init 0x88B250/0x88B2B0); readers not traced [?].

## 6. The sky

* **NotifySkyLoader 0x6D1CF0**: if skybox.efx's technique is the shader one ([0x93DEC4]+0x40) the model is
  **SKYDOME_XENON** (else SKYDOME and 0x6D1220). Textures: SKY_TEXTURE_MISC_1 = SKY_NEXGEN_CAP_A, MISC_2 =
  SKY_NEXGEN_CLOUDS_B, MISC_3 = SKY_NEXGEN_CAP_B; the dome's own texture is SKY_NEXGEN_CLOUDS_A (4A31E2A3). The old
  port skipped SKYDOME_XENON and drew the current-gen dome - wrong for any shader-capable PC.
* **Sky pass 0x6DE210(view, brightness, layer)**: shader path: one StuffSkyLayer(view, layer 0) for the player view
  (brightness [0x8F92E4] = 1 goes to [0x8FADE0]). Current-gen path: layers 0,1,2 (+4 reflection, +3 at 0.5).
* **StuffSkyLayer 0x6DB310 (PC)**: identity matrix at the camera; ScaleFactor layer0 1.0, layer1 0.9, layer2 0.8
  (+rotation matAng += (u16)(dt*300)), layer4 0.03 with z flip and +130 m; env-map views 0x12-0x17 0.0065 (only if
  DrawSkyEnvMap), RVM view 3 0.03. Player views multiply MainSkyScale (1). Render flags 0x20000. Current-gen path
  only: ReplaceSkyTextures + GetLayerMod -> FFSKYCOLOR. (The decomp's BLUE 1.1 is GameCube.)
* **Sky constants 0x6C60F0** (skybox effect start): MISC textures; **CloudScroll** += TOD.speed * 0.5 *
  WorldTimeElapsed * 0.04 once per frame; **CloudIntensity** (SKY_DIFFUSESCALE): w = cloud (0 -> 0.1;
  SkyRenderForceOvercast [0x8FAE40] = -1 overrides when >= 0), x = clamp(3.333333 w, 0, 1), y = min(2w^2, 0.8),
  z = min(w^2, 0.8). Clear sky: (0.333, 0.02, 0.01); cloud 0.5: (1, 0.5, 0.25).
* **skybox.efx** (SkyBox.shader), technique "world", 3 passes, all CULL none:
  p0 (no blend): oT0 = (u - 0.4 CloudScroll, v), oT2 = (u - (0.2 CloudScroll + 0.3), v), oT1 = uv1; oT3 = world
  fog in-scatter at a fixed 15000 m (expp(-21640.4258 (Br+Bm))); ps: a = 2 * diffuse(T0).a * diffuse(T2).a *
  CloudIntensity.x, rgb = (MISC_1(T1) + diffuse(T0))/2 * a + oT3 (1-a), alpha 1.
  p1: MISC_2(u - CloudScroll, v) with alpha * CloudIntensity.y; p2: MISC_3(uv1) with alpha * CloudIntensity.z;
  both SRCALPHA/INVSRCALPHA, ZWRITE off, ZFUNC EQUAL. MISC samplers: WRAP, LOD bias -1.
  So the clear sky colour is the fog in-scatter, with the cloud texture laid over it.
* skybox_cg.efx (fixed function, texture factor FFSKYCOLOR) is only the fallback when skybox.efx's shader technique
  fails to validate (NotifySkyLoader); on a shader-capable PC it is never used. The legacy SkyDome in WorldStreamer.cs
  stays as that fallback. SKY_SPECULAR: no code in the PC exe names it (the GameCube SPECULAR views do not exist on PC);
  its instance (Z0, flags 0x48240) is a plain scenery instance that WorldStreamer skips. Not drawn = faithful [?].
* Sun flare: RenderSunAsFlare 0x503830 (from ePreDisplay 0x504C56) adds two flare-pool entries (type 0x15) only when a
  SunInfo is loaded (GLOBALB chunk 0x34202, 5 x 0x110 bytes): camera + 40 x normalize(TOD+0x170) with
  PoolOfFlaresXcludeView 0xFE, and camera + 60 x SunInfo position / |TOD+0x170| with mask 0xFC00F6. eRenderLightFlarePool
  0x507770 skips a flare when mask & (1 << view id): both are excluded from views 1 and 2, so **the sun flare never
  draws in a player view** (only in the RVM, flailer and env-map views: car reflections, not world rendering). The
  visible sun is the sun sprite sub_6D04E0 (world_render.md §5; ported: Runtime/Sky/SunRender.cs).
* The road reflection view 4 draws this dome as sky layer 4 (scale 0.03, z flipped, +130 m): world_render.md §4,
  Runtime/World/RoadReflection.cs.

## 7. Deviations / [?]

* [ours] The dome is scaled to 0.8 x far plane / 9700 and drawn at the far end of the depth range; passes 1-2 use
  ZTest LEqual instead of EQUAL (same pixels).
* [?] ps_1_1 range: the sky's `a` is saturated (only exceeds 1 when CloudIntensity.x = 1).
* [ours] The time-of-day front-end limit (0.35) and the front-end lighting set are never used (no FE world here).
* [ours] RealTimeElapsed = Time.unscaledDeltaTime (the exe clamps it to MaxTicksPerTimestep); WorldTimeElapsed =
  Time.deltaTime. Cloud easing skipped while paused (the exe's paused branch keeps the values).
* [ours] Unity's directional light follows the TOD sun (direction) and DiffuseColour (colour) for the car shader and
  shadows.
* Now ported elsewhere (2026-10-24, world_render.md): rain and road wetness (Runtime/World/Rain.cs; it does rain
  offline), WorldTransparent / WorldDecal world.efx lighting (_MWLit), the per-effect world shaders (worldreflect,
  worldnormalmap, tree, GlossyWindow), the road reflection view, the visual treatment's sun-strength constants and the
  overbright pass (VisualTreatment.cs).
* Not ported: the online time/rain clock, race TOD hook, env-map sky brightness
  ([0x8FADE0] = EnvSkyBrightness for reflection views). The sun flare and SKY_SPECULAR need no port (see §6).

## 7b. Gamma colour space review (2026-10-24)

The project now renders in Gamma colour space. Every constant here (DiffuseColour, AmbientColour w = 4, SpecularColour,
fog Br/Bm/in-scatter, CloudIntensity, CloudScroll, SKY_DIFFUSESCALE, the sun flare / sprite colours) is published raw
with SetGlobalVector / vertex colours, exactly the exe's gamma-space values; SkyBox.shader and MWLighting.cginc do the
exe's maths on them unchanged. The only colour-space branch in this area (MWRawData for data textures) is a no-op in
Gamma. No change was needed; the player camera now renders LDR (world_render.md §10), so the sky's in-scatter and
clouds clamp at 1 per blend as on the 8-bit back buffer. Effect id table names checked against 0x8F9BE4: 21 skybox_cg,
22 shadow_cg (render flag 0x20000 swaps world.efx draws to 22, not 21; world_render.md §1).

## 8. Check in play

1. The sky is the next-gen dome: blue fog-coloured sky with scrolling cloud layers (SKY_NEXGEN_*), not the old
   midday dome. Clouds drift slowly (0.008 UV/s on the base layer).
2. World brightness: roads/buildings in sun much brighter than before (2 x N.L x sun added), shaded faces carry the
   baked vertex light x 1.12; distant scenery fades toward blue-white (25-45% at 625 m+).
3. Free roam for ~5-8 minutes: the sun moves and the light turns orange (dusk set) and back. Compare with the
   real game left running in free roam.
4. Drive to game X ~2400..6500 (= Unity x): cloud amount eases to 0.5 over ~20 s: greyer, bluer light, more haze,
   cloudier sky.
5. D-Day intro / TOD=1 races are not yet dusk (hook missing): `VisualLighting.SetTimeOfDay(1)` should give the
   prologue's sunset look.

## 9. Round 4 corrections
* §5: the FogQuery (Normal Fog / Rain Fog / Fog Blend Distance) is now ported as a probe (Core/Weather.cs FogQuery, Rain.Fog)
  and confirmed unread on PC; the Screen Tint layers have no reader; FA9F8DAA is an empty, unread layer. Full reader list:
  world_render.md §15.6. Wind field 1 is read with the by-distance blend (50 m) — the port's accessor call is fixed.
* §6 / §7: the env-map / RVM sky brightness ([0x8FADE0] = EnvSkyBrightness 2.5) only acts on the fixed-function path; on the
  shader path the dome is drawn at 0.0065 (env views) / 0.03 (RVM) with the same constants — the env cube now draws it
  (Runtime/CarRender/CarEnvironment.cs). The sun pool flare (RenderSunAsFlare, mask 0xFE) is drawn into the env views: the
  sun glint on the car paint. "Mirrored sun" / GetSunPos: no PC code.
* §4: the sun shadow strength [0x8FAE68] also drives shadow_cg's ShadowColor alpha (the SHD_ baked shadows, world_render.md
  §15.1) through sub_6C6630.
* The NIS characters' light rig is ShaperLightsCharacters (camera space), not the car rig: car_render.md §15.1.
* TimeOfDay.cpp (0x768C20) is the eTimeOfDay enum (always MIDDAY); the float time of day of §4 is World/TimeOfDay (0x769360).
