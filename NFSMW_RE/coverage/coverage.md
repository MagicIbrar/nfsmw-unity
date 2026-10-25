# NFS Most Wanted (PC v1.3) → Unity port: code coverage map

Snapshot: 2026-10-24, Unity tree `NFSMWUnity/Assets/NFSMW` as it stood around 01:45 (the main session was adding `Runtime/Game/RoadNav.cs` and `AIRacer.cs` at the time; both are counted). Machine-readable rows: `coverage.json`.

## 1. Overall summary

speed.exe `.text` holds **46,360 functions / 4,209,494 bytes (4,111 KB)** of code (function list from the decomp's PC symbol table `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`). Every function was attributed to an original source file (name-anchored: 49% of bytes; the rest filled from neighbouring anchors, see §4).

Of that, **1,759 KB (43%) is N/A** for a Unity port (CRT, DirectX/Win32, EA audio/movie/memcard/network libraries, the D3D/GC platform layer, EA online services, memory/containers/math, event plumbing, the Lua VM). The remaining **2,352 KB of game-relevant code** splits as follows:

| Measure | Game-relevant total | Ported | Partial | Missing | Effective ported* |
|---|---:|---:|---:|---:|---:|
| speed.exe code bytes | 2,352 KB | 80.0% | 20.0% | 0.0% | **98%** |
| Original source lines (est., GC line tables) | 324,681 | 81.8% | 17.9% | 0.3% | **98%** |
| Decomp source lines (refs/nfsmw, files with code) | 160,387 | 85.7% | 14.1% | 0.1% | **98%** |

\* *Effective ported* weights every Partial file by a judged share of its behaviour that the Unity code reproduces (0.15–0.8, from the specs' deviation lists; `ported_fraction` in the JSON). Ported counts 1, Missing 0. It is an estimate. The Ported/Partial/Missing columns are the hard numbers.

Status per file: **Ported** = behaviour implemented and cited to the original. **Partial** = a Unity counterpart exists but differs from or covers only part of the original. **Missing** = no counterpart. **N/A** = replaced by the engine or out of scope.

### Per subsystem (sorted by game-relevant bytes)

| Subsystem | Relevant code | N/A code | Ported | Partial | Missing | Effective | Decomp lines (rel.) |
|---|---:|---:|---:|---:|---:|---:|---:|
| Frontend: menus & screens | 312 KB | 86 KB | 82% | 18% | 0% | 98% | 23,866 |
| Physics | 277 KB | 21 KB | 95% | 5% | 0% | 100% | 15,285 |
| Audio (EAXSound) | 240 KB | 92 KB | 100% | 0% | 0% | 100% | 20,429 |
| World: streaming & rendering | 227 KB | 35 KB | 90% | 9% | 0% | 98% | 22,303 |
| AI | 213 KB | 0 KB | 100% | 0% | 0% | 100% | 16,378 |
| Gameplay/career | 153 KB | 0 KB | 7% | 93% | 0% | 94% | 4 |
| Frontend: data & career logic | 133 KB | 0 KB | 75% | 25% | 0% | 99% | 11,297 |
| World: collision, roads, triggers | 113 KB | 22 KB | 97% | 3% | 0% | 100% | 7,315 |
| Animation/NIS | 99 KB | 28 KB | 100% | 0% | 0% | 100% | 6,045 |
| Camera | 97 KB | 13 KB | 89% | 11% | 0% | 99% | 621 |
| Lua scripting | 87 KB | 0 KB | 0% | 100% | 0% | 95% | 6,475 |
| Audio (Speech) | 82 KB | 8 KB | 100% | 0% | 0% | 100% | 8,132 |
| Frontend: HUD | 61 KB | 0 KB | 32% | 68% | 0% | 94% | 4,692 |
| Ecstasy renderer | 58 KB | 6 KB | 100% | 0% | 0% | 100% | 4,746 |
| FEng (UI engine) | 53 KB | 39 KB | 98% | 2% | 0% | 100% | 4,439 |
| Foundation (bWare/UTL/AttribSys) | 51 KB | 155 KB | 75% | 25% | 0% | 99% | 1,818 |
| Sim | 44 KB | 32 KB | 36% | 64% | 0% | 88% | 1,594 |
| Misc | 38 KB | 71 KB | 54% | 46% | 0% | 98% | 4,574 |
| Input | 11 KB | 24 KB | 100% | 0% | 0% | 100% | 374 |
| Online/LAN | – | 295 KB | – | – | – | – | – |
| Libs: CRT/DirectX/third-party | – | 570 KB | – | – | – | – | – |
| Platform (PC/GC/PS2/Xenon low level) | – | 160 KB | – | – | – | – | – |
| Events & main loop | – | 103 KB | – | – | – | – | – |

## 2. Subsystems

### Libs: CRT/DirectX/third-party

570 KB in the exe, all N/A.

MSVC CRT/C++ runtime, DirectX/Win32 thunks, EA snd/csis/path/spch audio libraries, realcore/realmemcard, the RCMP/VP6 movie codec. N/A.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| pc/libs:MSVC CRT/C++ runtime | 0x794420-0x7F437C | 257,973 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| pc/libs:movie(RCMP/VP6) | 0x7FAA42-0x8185F1 | 113,425 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| pc/libs:DirectX/Win32 imports | 0x835120-0x866F71 | 59,665 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| pc/libs:EA audio (snd/csis/path/spch) | 0x81A980-0x832EC7 | 37,457 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Libs/stl/STLport-4.5/stlport/stl/_ios.c | 0x533760-0x53EDBE | 36,402 | 124 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/MyWork/packages/snd/9/source/library/cmn/saems.c | 0x8185F1-0x81A801 | 8,363 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/packages/realmemcard/3.04.01-layer2/source/lib/gc/gc_memcard_interface_impl.cpp | 0x7F437C-0x7F6E7C | 6,859 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/packages/path/5.01.04/source/cmn/pathcontrol.cpp | 0x8274F0-0x8290FD | 6,822 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/packages/path/5.01.04/source/cmn/pathvol.cpp | 0x82D520-0x82EF5B | 6,340 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| d:/packages/realcore/6.24.00/source/file/cmn/filesys.cpp | 0x7ECFEC-0x7EE299 | 4,603 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| d:/packages/realcore/6.24.00/source/system/cmn/mutex.cpp | 0x7EAE00-0x7EC30A | 4,591 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/MyWork/packages/snd/9/source/library/cmn/sclcptch.c | 0x820907-0x8219BB | 4,153 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/packages/realmemcard/3.04.01-layer2/source/lib/cmn/memcard_taskmanager.cpp | 0x7F61EF-0x7F7159 | 3,654 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/audio/libs/csis/dev/source/library/cmn/csis.cpp | 0x830EF7-0x831FC1 | 3,370 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| D:/perforce/audio/libs/SPCH/dev/source/library/cmn/sputil.c | 0x832EC7-0x833DD7 | 3,224 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| pc/libs:EA realcore/realfont/filesys | 0x7E8880-0x7F0E0F | 3,014 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/packages/path/5.01.04/source/cmn/pathinit.cpp | 0x829450-0x82A2D0 | 2,684 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/packages/path/5.01.04/source/cmn/pathevent.cpp | 0x82C1D0-0x82CC9B | 2,678 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| D:/env/egami/rcmp/dev/source/av/cmn/avplayer.cpp | 0x7F78E6-0x7F84E5 | 2,672 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| C:/packages/path/5.01.04/source/cmn/pathsnd.cpp | 0x82AD20-0x82B7E7 | 2,602 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| *63 smaller files* | | 13,039 | | N/A 63 | | |

### Frontend: menus & screens

398 KB in the exe (312 KB relevant): Ported 82%, Partial 18%, Missing 0%, effective ≈ 98%.

Many FEng screens exist in Unity with their own C# logic over the original packages (`Screens.cs`, `VisualScreens.cs`, `FrontEnd.cs`). Pause, carousel, engage dialog and fonts are faithful. Missing: world map, SMS inbox/message, bounty, marker select, custom tuning, photo finish, jukebox, tutorials, keyboard entry, loading tips. Memory-card UI is N/A.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Frontend/MenuScreens/InGame/uiWorldMap.cpp | 0x52F5C0-0x561BE1 | 21,295 | 1,224 | Ported | WorldMapScreen.cs | WorldMapScreen: views, cursor, zoom/pan, items incl. HeliItem / roadblocks / engagement-radius cops, toggle rows with SetIcon colours, GPS dialogs, header (fe_career_screens.md §1.11). [?] kGameplayIconInfo player row; pursuit breakers / hiding spots have no GIcon source yet; PC mouse extras |
| Frontend/MenuScreens/InGame/FEPKg_PostRace.cpp | 0x56A3B0-0x5A95EF | 18,246 | 1,406 | Ported | PostRaceResultsScreen.cs, PostPursuitScreens.cs, RaceStats.cs | results / stats / lap stats pages, pursuit table (FE + in-game branches, challenge row), milestone rewards incl. StartChallengeAnimations (fe_ingame.md §3, addendum 2). Not wired: PursuitData.mExitToSafehouse → MEnterSafeHouse; NotifySoundMessage suppression (port FE sounds come from input) |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRCarSelect.cpp | 0x7A3400-0x7C32E0 | 16,235 | 1,463 | Ported | CarSelectScreens.cs, FEImpound.cs, FEDatabase.cs | stat sliders, QRCarSelectBustedManager (impound states, strikes, release / marker dialogs), bonus unlock texts, logo fallbacks, sell / back rules (fe_safehouse.md §15.1). FEImpound records are not fed by the pursuit owner yet |
| Frontend/FEngInterfaces/FEngInterfaceFEObjects.cpp | 0x514C60-0x564CB1 | 15,833 | 769 | N/A |  | memory card, FE plumbing, input glue, debug screens |
| Frontend/MenuScreens/Safehouse/options/uiOptionWidgets.cpp | 0x50EEF0-0x528F2E | 13,405 | 688 | Ported | OptionsScreens.cs | [round 9] console-only widgets N/A |
| Frontend/MenuScreens/MemCard/uiMemcardBase.cpp | 0x517BB0-0x5642BB | 13,022 | 1,037 | N/A |  | memory card, FE plumbing, input glue, debug screens |
| Frontend/MenuScreens/Common/feWidget.cpp | 0x574020-0x59C61B | 12,014 | 925 | Ported | TextScroller.cs, FEScrollBar.cs, WidgetMenu.cs | [round 9] ToggleImageWidget dead; Input/Date online-only |
| Frontend/MenuScreens/Safehouse/FEPkg_GarageMain.cpp | 0x7A1C70-0x7BF1E7 | 11,403 | 914 | Partial | CarShowroom.cs, SelectCarCamera.cs | [round 9] GarageCarLoader, fades not ported. ready: decomp source |
| Frontend/FERenderObject.cpp | 0x5681D0-0x591AA4 | 10,764 | 634 | Ported | FEView.cs, FEngRender.cs, UIMultiImage.shader | [round 9] clipping dead (0x585320) |
| Frontend/MenuScreens/InGame/uiPause.cpp | 0x5108E0-0x5504C4 | 9,738 | 263 | Ported | GameFlow.cs | Pause_Main builder 0x52B4D0 |
| Frontend/MenuScreens/Common/feIconScrollerMenu.cpp | 0x566CD0-0x59BD23 | 9,716 | 811 | Ported | Screens.cs | CarouselScreen |
| Frontend/FEngRender.cpp | 0x56FA90-0x5A470E | 9,676 | 563 | Partial | FEView.cs, FEText.cs, FEngRender.cs | [round 9] colour compose, alpha-0 skip, squeeze, flag 0x10; movie objects draw no video surface. ready: decomp source |
| Frontend/MenuScreens/InGame/CustomTuning.cpp | 0x589DB0-0x5AA83D | 8,661 | 480 | Ported | TuningScreen.cs, CSlider.cs, TextScroller.cs | [round 9] FEng engine; limits ±1 (0x8A9350) |
| Frontend/MenuScreens/Common/FEMenuScreen.cpp | 0x572360-0x59B3A1 | 8,487 | 582 | Partial | FEMenuScreen.cs | [round 9] BaseNotifySound 0x5724D0; inline PC text input not ported. ready: decomp source |
| Frontend/MenuScreens/Common/feScrollerina.cpp | 0x573170-0x5A8DAB | 8,425 | 571 | Ported | JukeboxScreen.cs | [round 9]  |
| Frontend/FEPackageData.cpp | 0x568240-0x59ABDB | 8,056 | 757 | N/A |  | memory card, FE plumbing, input glue, debug screens |
| Frontend/MemoryCard/MemoryCard.cpp | 0x50EB40-0x5436F0 | 7,955 | 631 | N/A |  | memory card, FE plumbing, input glue, debug screens |
| Frontend/MenuScreens/Common/feUIWidgetMenu.cpp | 0x573C60-0x5A9907 | 7,669 | 556 | Ported | WidgetMenu.cs | [round 9] Done-button branches dead |
| Frontend/MenuScreens/Safehouse/options/uiOptionsScreen.cpp | 0x5102B0-0x54FE69 | 7,586 | 409 | Ported | OptionsScreens.cs | [round 9] dialog 0xBA463431 |
| Frontend/MenuScreens/Safehouse/quickrace/uiQRTrackOptions.cpp | 0x7A31D0-0x7BF78B | 7,386 | 421 | Ported | QuickRaceScreens.cs | [round 9] direction dead |
| Frontend/MenuScreens/Common/feArrayScrollerMenu.cpp | 0x573980-0x5A58B8 | 6,256 | 331 | Partial | CustomizeExe, SmsScreens, ControllerScreen | [round 9] scroll-bar update missing. ready: decomp source |
| Frontend/FEJoyInput.cpp | 0x558AF0-0x559675 | 6,088 | 256 | N/A |  | memory card, FE plumbing, input glue, debug screens |
| *84 smaller files* | | 169,620 | | Ported 50, N/A 17, Partial 17 | | |

### Audio (EAXSound)

331 KB in the exe (240 KB relevant): Ported 100%, Partial 0%, Missing 0%, effective ≈ 100%.

Decoders are ported (EA-XA/XAS, SCHl, Ginsu, MicroTalk). Playback logic is minimal: Ginsu engine grains, a handful of bank one-shots, a linear soundtrack and the police scanner. The sound-control layer (sfxctl, carsfx objects, dynamic mixer, state managers, ambience, interactive music) is missing. Audio-engine infrastructure is N/A.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| EAXSound/EAXSound.cpp | 0x4AE7E0-0x4D1E96 | 25,154 | 1,769 | N/A |  | audio engine/streaming infrastructure replaced by Unity audio + C# decoders |
| EAXSound/Stream/SpeechManager.cpp | 0x6FEB40-0x71DFEC | 23,385 | 1,261 | Ported | SpeechScript.cs, SpeechManager.cs, CopRadio.cs | verified function by function against the exe (audio.md s5.4): slot pool modelled, pointer sorts by slot; the game leaks dropped events, the port frees them |
| EAXSound/sfxctl/SFXCTL_AccelTrans.cpp | 0x49A370-0x4C58FA | 21,569 | 262 | Ported | AiEngine.cs, CarSound.cs, CollisionAudio.cs | every function checked against the exe (audio.md s23); IsAICar gear exemption fixed |
| EAXSound/EAXAemsManager.cpp | 0x4AC390-0x4D2456 | 19,236 | 808 | N/A |  | audio engine/streaming infrastructure replaced by Unity audio + C# decoders |
| EAXSound/Dynamic_Mixer/NFSMixMap.cpp | 0x4E1EB0-0x4F43D8 | 12,341 | 1,964 | Ported | MixMap.cs | [round 9] 0x4F4270, 0x4F18E0, 0x4F17F0, 0x4EA230.., 0x4E9B30 compared; allocation replaced by Build |
| EAXSound/CARSFX/CARSFX_Engine.cpp | 0x4D4080-0x4F8023 | 11,204 | 509 | Ported | EngineAudio.cs | [round 9] main bank only, sPU_or_EE = 1, tranny clamp |
| EAXSound/SndBase.cpp | 0x4AC200-0x4F5265 | 11,194 | 103 | N/A |  | audio engine/streaming infrastructure replaced by Unity audio + C# decoders |
| EAXSound/CARSFX/SFXObj_Pathfinder.cpp | 0x4DF010-0x4F9E32 | 9,199 | 1,410 | Ported | Music.cs | [round 9] MessageSwapInteractive 0x4DFC40 |
| EAXSound/Stream/GameSpeech.cpp | 0x6FF800-0x71E368 | 9,134 | 562 | Ported | SpeechScript.cs, SpeechManager.cs, CopRadio.cs | Update/CheckNextEvent/IssuePlayback/RadioChirp/UpdateChirps/GetVolForSpeaker verified against the exe; stream volume law (x258, clamp) ported |
| EAXSound/STICH_PlayBack.cpp | 0x4B0C10-0x4CFF9E | 7,335 | 504 | Ported | WorldAudio.cs | [round 9] 25-voice limit + pruning 0x4CF520/0x4CEB80/0x4CECE0; cStitchLoop unreachable |
| EAXSound/CARSFX/SFXObj_Collision.cpp | 0x4AB770-0x4F822A | 7,119 | 257 | Ported | CollisionAudio.cs | [round 9]  |
| EAXSound/sfxctl/SFXCTL_Helicopter.cpp | 0x4B6730-0x4C0BDC | 6,550 | 79 | Ported | WorldAudio.cs | [round 9] 0x4BFA30 |
| EAXSound/States/STATE_Base.cpp | 0x4AFAC0-0x4F4B65 | 6,473 | 412 | N/A | SndSystem.cs | [round 9] object creation/attach plumbing replaced by SndMixer units |
| EAXSound/SoundConn.cpp | 0x4ADC30-0x4CD133 | 5,919 | 266 | N/A |  | audio engine/streaming infrastructure replaced by Unity audio + C# decoders |
| EAXSound/CARSFX/CARSFX_Roadnoise.cpp | 0x4DAA30-0x4F7C40 | 5,577 | 469 | Ported | Sfx.cs | [round 9] wet-road loop 0x4DAB10, spike-strip transition |
| EAXSound/sfxctl/SFXCTL_Tunnel.cpp | 0x4B53F0-0x4C8E61 | 5,287 | 532 | Ported | WorldAudio.cs | [round 9] 12 functions; PC reverb table 0x8F2140 |
| EAXSound/sfxctl/SFXCTL_Shifting.cpp | 0x4B3540-0x4C5577 | 5,262 | 505 | Ported | CarSound.cs | [round 9] 0x4BBC10/0x4BBF10/0x4C4DB0 |
| EAXSound/RealStream/src/stream.cpp | 0x4B25A0-0x4C4A6D | 5,123 | 1,217 | N/A |  | audio engine/streaming infrastructure replaced by Unity audio + C# decoders |
| EAXSound/SoundCollision.cpp | 0x4AFD60-0x4C78C8 | 5,120 | 303 | Ported | CollisionAudio.cs | [round 9]  |
| EAXSound/CARSFX/CARSFX_TrafficFX.cpp | 0x4DDE10-0x4F86BA | 5,080 | 434 | Ported | CarSound.cs, Sfx.cs, WorldAudio.cs | [round 9] constant scan clean |
| EAXSound/sfxctl/SFXCTL_Physics.cpp | 0x4B4E20-0x4C8DF4 | 5,014 | 474 | Ported | CarSound.cs | [round 9] constant scan clean |
| EAXSound/EAXCar.cpp | 0x4D59C0-0x4F51DC | 4,910 | 221 | Ported | CarSound.cs | [round 9] Attach 0x4E4FD0; 0x4D5C00/0x4D5C20 |
| *87 smaller files* | | 122,238 | | Ported 60, N/A 12, Partial 11, Missing 4 | | |

### Physics

298 KB in the exe (277 KB relevant): Ported 95%, Partial 5%, Missing 0%, effective ≈ 100%.

Player-car simulation is the most complete area: EngineRacer, SuspensionRacer (tyres, steering, diffs, drift), Chassis aero and air control, physics info/upgrades and the drag model are ported with cited addresses (`Runtime/PlayerCar.cs`, `CarSpec.cs`). Collision response runs on PhysX with the original friction/elasticity rules (`CarContacts.cs`). Missing: the simplified AI/traffic/trailer vehicle models, the helicopter, tunings, surface grip, collision bounds, damage zones and physics effects.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Physics/Behaviors/RigidBody.cpp | 0x670B90-0x6BB7EC | 33,203 | 1,605 | Ported | CarContacts.cs, CollisionBounds.cs, WorldCollision.cs | [round 9] ground point cloud, ONESIDED/side rule, GetTriggerFlags 0x699F60/0x69AFD0; one-packet react, Separate, frame skip = documented PhysX deviations |
| Physics/Common/PVehicle.cpp | 0x6681D0-0x68A0F5 | 23,593 | 0 | Ported | VehicleMechanics.cs, PlayerCar.PVehicle.cs, CarReset.cs | [round 9] LookupBehaviorSignature 0x67F0D0 + AI table 0x8F8890; data-driven RESET and racer behaviours; mid-life switching = component swap [ours] |
| Physics/Behaviors/SuspensionRacer.cpp | 0x68DDA0-0x6B6AA6 | 23,193 | 2,022 | Ported | PlayerCar.cs, CarSpec.cs | SuspensionRacer tire/steering/diff/drift cited (0x68E090..0x6B1DD0); raycast wheels; surface grip, power-oversteer helper, steering-assist state not modelled |
| Physics/PhysicsInfo.cpp | 0x66BAE0-0x68A7E0 | 21,035 | 243 | Ported | PhysicsInfo.cs, CarSpec.cs | MaxInductedPower 0x66BFA0, Redline 0x66C560, HasNos 0x66BB80, HasRunflatTires 0x66BC00, Torque one-point table; AvgInductedTorque (FE-only) not ported |
| Physics/Behaviors/EngineRacer.cpp | 0x691A10-0x6B6D92 | 14,494 | 1,645 | Ported | PlayerCar.cs, CarSpec.cs | EngineRacer + EngineDragster (0x6920D0, 0x692840, 0x6AF490 cited); deviations in physics.md §13 (clutch, shift interpretation); launch control not modelled |
| Physics/Behaviors/SuspensionSpline.cpp | 0x690A00-0x6B6D26 | 14,429 | 976 | Ported | NisCarControl.cs | NIS car behaviours 1:1: linear AerodynamicDownforce, pitch/roll vs track up, SetVehicleOnGround at the track point, ModifyYPos wheels, per-wheel read-outs, JumpToEnd |
| Physics/Common/Bounds.cpp | 0x66A5C0-0x6874D4 | 13,744 | 0 | Ported | CollisionGeometry.cs, CollisionBounds.cs, Articulation.cs | [round 9] PCloud parse; joints in Articulation |
| Physics/PhysicsUpgrades.cpp | 0x66B850-0x680651 | 12,502 | 0 | Ported | PhysicsInfo.cs, CarSpec.cs, PerformanceRatings.cs | [round 9] MatchPerformance 0x67C310 |
| Physics/Behaviors/SuspensionSimple.cpp | 0x68FAD0-0x6B6BE6 | 10,706 | 1,091 | Ported | SuspensionSimple.cs, Chassis.cs | [round 9] catch-up cheat, linear downforce, !IsModeling pause, jump stabiliser |
| Physics/Common/PhysicsObject.cpp | 0x6694F0-0x688626 | 10,222 | 0 | N/A |  | sim object framework / narrow-phase collision replaced by Unity + PhysX |
| Physics/Common/SmokeableInfo.cpp | 0x66B150-0x68275E | 8,619 | 0 | Ported | Smackable.cs, CollisionBounds.cs | [round 9] spawners without collection get no smackable; SceneryBox |
| Physics/Dynamics/Articulation.cpp | 0x48A000-0x48D198 | 8,269 | 0 | Ported | Articulation.cs | the exe's joint solver (Joint::Resolve 0x48C5D0, 32-step reduced-mass solve 0x48A000/0x48A910, limits 0x48C270) replaces the PhysX joint; TractorHitch.ExeSolver=false restores it |
| Physics/Dynamics/Geometry.cpp | 0x487500-0x48C265 | 7,289 | 0 | N/A |  | sim object framework / narrow-phase collision replaced by Unity + PhysX |
| Physics/Behaviors/Chassis.cpp | 0x68D1E0-0x6B1BAB | 6,566 | 551 | Ported | PlayerCar.cs | Chassis aero/air control/speedbreaker (0x68D800, 0x69D120, 0x6A8F40); air control gains softened |
| Physics/Behaviors/DrawCar.cpp | 0x693AF0-0x6BC890 | 6,469 | 467 | Ported | CarRenderInfo.cs, CarRenderConn.cs, PlayerCar.PVehicle.cs | [round 9] packet incl. reset flashing |
| Physics/Behaviors/SpikeStrip.cpp | 0x6A4B10-0x6B8DA6 | 6,241 | 142 | Ported | Roadblock.cs, PlayerCar.cs | per-wheel puncture/blow on any car |
| Physics/Behaviors/ResetCar.cpp | 0x6947A0-0x6B09B9 | 5,618 | 352 | Ported | CarReset.cs | ResetCar cookies, flip timer, road snap |
| Physics/Behaviors/DamageVehicle.cpp | 0x68C1B0-0x6BAD56 | 5,411 | 328 | Ported | PlayerCar.cs, CarDamage.cs, VehicleDamage.cs | [round 9] shock, Destroy, ResetDamage |
| Physics/Behaviors/SoundCar.cpp | 0x693EA0-0x6B50D5 | 5,307 | 421 | Partial | CarSound.cs, PlayerCar.PVehicle.cs | [round 9] ToleratedSlip/SoundWheelSlip added, audio not using yet. ready: decomp source |
| Physics/Behaviors/Effects.cpp | 0x69B880-0x6A859B | 4,752 | 461 | Ported | CarEffects.cs | [round 9] DoHit/DoScrape/bottom-out/OnDetached |
| Physics/Behaviors/EngineTraffic.cpp | 0x6933E0-0x6B6F12 | 4,573 | 552 | Ported | EngineTraffic.cs | [round 9] 0x6A3480 |
| Physics/Behaviors/PInput.cpp | 0x68CEA0-0x6B8D45 | 4,545 | 388 | Ported | PlayerCar.cs | InputPlayer -> DriveInput |
| *24 smaller files* | | 54,404 | | Ported 16, N/A 5, Partial 3 | | |

### Online/LAN

295 KB in the exe, all N/A.

EA online services (lobby, buddies, persona, web offers, online race sync) are N/A. LAN racing was re-implemented from scratch over UDP (`Lan.cs`, `LanScreens.cs`) and is not a port of the original.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| pc/online | 0x75DF10-0x7B4BB7 | 302,352 | 0 | N/A | Lan.cs, LanScreens.cs | EA online services (lobby, buddies, web offers) are N/A; LAN races re-implemented independently over UDP |

### World: streaming & rendering

262 KB in the exe (227 KB relevant): Ported 90%, Partial 9%, Missing 0%, effective ≈ 98%.

Section streaming, scenery instancing, visible-section culling and VisualTreatment are ported (`WorldStreamer.cs`, `Core/Track.cs`, `VisualTreatment.cs`). Car rendering is basic: shadow blob and flares, but no env-map reflections, damage/part swaps or projected hull shadow. Missing: rain/weather, skid marks, visual damage, parameter maps, world animations.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| World/CarRender.cpp | 0x736D30-0x76113C | 37,832 | 4,807 | Ported | CarRenderInfo.cs, CarEnvironment.cs, CarBuilder.cs | Render rules complete: tyre/rim/brake LODs at the body LOD, TireFace B-swap, LICENSE_PLATE/UNIVERSAL_SPOILER_BASE rules, interior/driver special LOD ranges (0x739B40), camera-inside hide, no env map under 18 px, light rigs by game flow; not ported: CarPartCuller plane culling, TireOffsets wheel scaling, dual rear wheels |
| World/SkyRender.cpp | 0x6C0AB0-0x6DBEBD | 21,324 | 387 | Ported | SkyRender.cs, SunRender.cs, TimeOfDayDriver.cs | env-view sky dome and sun pool flare drawn into the env cube; EnvSkyBrightness fixed-function only; GetSunPos/mirrored sun absent on PC; TimeOfDay is the fixed MIDDAY enum |
| World/ParameterMaps.cpp | 0x5DF1F0-0x609903 | 19,887 | 549 | Ported | ParameterMaps.cs, Weather.cs, Rain.cs | every PC reader located and ported (Rain/Clouds, Wind, FogQuery 0x753D90, the audio layers); Screen Tint/FA9F8DAA have no reader |
| World/TrackStreamer.cpp | 0x723600-0x73182B | 17,421 | 2,496 | Ported | WorldStreamer.cs, Track.cs, TrackStreamerRules.cs | full audit; loading bar at 3.0 m (0x730F11), PVS keyed on the view camera, visible-section overlays (chunk 0x34158, FLYBY); hibernation/hole filling/LRU are memory-only on PC |
| World/WorldConn.cpp | 0x768570-0x7A0A79 | 16,196 | 511 | N/A |  | render/sim connection framework, debug and online-only code |
| World/CarRenderConn.cpp | 0x738C10-0x75E74A | 15,841 | 1,410 | Ported | CarRenderConn.cs | UpdateSteering/Tires/RoadNoise/BodyAnimation/EngineAnimation/RenderMatrix 1:1 (flat tyre sink+wobble, wheel hop, squat/dive/roll, shift nod, ride height incl. ecar RideHeight); contrails/RVM not ported |
| World/CarInfo.cpp | 0x739650-0x75B312 | 15,694 | 2,026 | Ported | CarRenderInfo.cs, CarRenderData.cs | PC special-LOD rule (0x739B40) added; rest already ported |
| World/CarLoader.cpp | 0x73A200-0x75CBD9 | 14,225 | 2,217 | Ported | CarBuilder.cs | residency rules are memory-only; cars drawn only when fully loaded (synchronous build) |
| World/rain.cpp | 0x73C5B0-0x758595 | 10,926 | 484 | Partial | SunRender.cs, Rain.cs, RoadReflection.cs | sun occlusion queries, alpha wrap, mirrored sun, reflections of scenery/cars/lights. ready: decomp source |
| World/Scenery.cpp | 0x723BA0-0x731D78 | 9,707 | 1,184 | Ported | Track.cs, WorldStreamer.cs | scenery loader 0x7311F0, GrandSceneryCullInfo, section draw lists |
| World/CarSkin.cpp | 0x73AB50-0x753650 | 8,409 | 1,095 | Ported | CarRenderData.cs, * | [round 9] precomposite vinyls (0x9B09FC never written), P8 composite 0x748AD0, spinners proven dead; cache = optimisation |
| World/VehiclePartDamage.cpp | 0x73E540-0x75F18B | 8,033 | 556 | Ported | CarRenderInfo.cs | [round 9] ManageGlassDamage 0x74C210, Reset 0x75C330; UnitTest no caller |
| World/VisibleSection.cpp | 0x7242A0-0x7321EE | 7,184 | 707 | Ported | Track.cs, WorldStreamer.cs | drivable/visible sections |
| World/Skids.cpp | 0x721B30-0x72F3FB | 6,511 | 376 | Ported | SkidMarks.cs | Skids.cpp port |
| World/VehicleRenderConn.cpp | 0x7388B0-0x75D31F | 5,095 | 525 | Ported | CarEffects.cs, * | [round 9] CheckForRain 0x744C70 → SetSurface 0x738D50; DoFX 0x7457D0 |
| World/World.cpp | 0x732CD0-0x75AB9E | 4,907 | 364 | N/A |  | render/sim connection framework, debug and online-only code |
| World/VisualTreatment.cpp | 0x73D9F0-0x74C1F7 | 4,899 | 487 | Ported | VisualTreatment.cs, VisualTreatment.shader | visuallook, heat 'uves' flash, NOS/speedbreaker radial blur |
| World/WorldModel.cpp | 0x73B800-0x75A5A8 | 4,533 | 367 | Ported | WorldStreamer.cs, Smackable.cs | RenderWorldModels only for view 1 and the shadow view (smackables never in reflections) |
| World/EventManager.cpp | 0x7256A0-0x72E551 | 3,977 | 286 | Partial | WorldStreamer.cs, WorldShadows.cs | resolution-scaled LOD, world shadow casters/receivers (Unity shadow map deviation). ready: decomp source |
| World/TrackPath.cpp | 0x722910-0x72BB7F | 3,908 | 276 | Ported | RoadNetwork.cs, Weather.cs, TrackStreamerRules.cs | [round 9] zone table/cache = lookup accelerators, same first-match order |
| World/WeatherMan.cpp | 0x729DA0-0x72CC45 | 3,392 | 272 | Partial | SunRender.cs, Rain.cs, RoadReflection.cs | sun occlusion queries, alpha wrap, mirrored sun, reflections of scenery/cars/lights. ready: decomp source |
| World/ScreenEffects.cpp | 0x725200-0x72CC7B | 2,965 | 371 | Partial | SunRender.cs, Rain.cs, RoadReflection.cs | sun occlusion queries, alpha wrap, mirrored sun, reflections of scenery/cars/lights. ready: decomp source |
| *20 smaller files* | | 25,750 | | Ported 9, N/A 6, Missing 3, Partial 2 | | |

### AI

213 KB in the exe (213 KB relevant): Ported 100%, Partial 0%, Missing 0%, effective ≈ 100%.

Racer AI is being ported at the snapshot (`AIRacer.cs`: AIVehicleRacecar PID, AIActionRace racer branch, adaptive PID, GetUnstuck). Cops and traffic still run on the older `AIDriver` path follower with home-grown rules (`Systems.cs`, `Pursuit.cs`): no goal/action system, formations, PIT/ram, helicopter, Rhino/Cross support or spawn-ring rules (ai_cops.md §9).

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| AI/Common/AIPursuit.cpp | 0x410770-0x444D41 | 40,136 | 2,854 | Ported | PursuitSystem.cs, PursuitPerp.cs, RacerPursuit.cs | round 4: one AIPursuit per perp (RacerPursuit for AI racers), race table rows, heat clamp base..max, mAllowStatsToAccumulate gating, IncNumCopsDestroyed rep points per level; formations in PursuitFormationState; siren = audio agent (ai_cops.md 9.3) |
| AI/Common/AIVehicle.cpp | 0x405170-0x442CFA | 29,373 | 2,548 | Ported | AIMath.cs, AIRacer.cs, CopCar.cs | round 4: DisableSimplePhysics 1 s INVULNERABLE_FROM_PHYSICS_SWITCH via PlayerCar.SetPhysicsMode, AIPerpVehicle heat/LastTrafficHitTime on racers, SetHeat race tables; GetWorldAvoidanceInfo/GetLookAhead/UpdateRoadNavInfo have no PC callers; AIVehicleHuman::UpdateWrongWay = RaceManager.WrongWay |
| AI/Activities/AITrafficManager.cpp | 0x410800-0x441C75 | 28,795 | 713 | Ported | Traffic.cs, RoadNavTraffic.cs | round 4: density 0 while IsCopRequestPending; vehicle cache (OnQueryVehicleCache) has no equivalent; debug queue not applicable |
| AI/Activities/AICopManager.cpp | 0x410D40-0x43EF6D | 23,710 | 2,285 | Ported | PursuitSystem.cs, PursuitPerp.cs, RacerPursuit.cs | round 4: runs in races, UpdatePursuits per target, CanPursueRacers, IsCopRequestPending, patrol count rule with pursuits/opponents, PursuitIsEvaded roaming-only screen; vehicle cache has no equivalent (ai_cops.md 9.3) |
| AI/Gps.cpp | 0x41A590-0x440255 | 16,284 | 422 | Ported | Gps.cs, RoadNavGps.cs | round 4 check: ctor/Update/Engage/Render = Gps.cs Ensure/Step/DoEngage/LateUpdate (route search, recovery, 3D arrow, connection lost); RoadNavGps.cs path nav |
| AI/Actions/AIActionRace.cpp | 0x4076B0-0x42F137 | 10,233 | 1,074 | Ported | AIRacer.cs, CopCar.cs | racer branch + cop pursuit/flee branches ported; cops aim at their own pursuit target (round 4) |
| AI/Actions/AIActionPursuitOffRoad.cpp | 0x406B30-0x4277CD | 7,371 | 633 | Ported | AIMath.cs, AIRacer.cs, CopCar.cs | formation slot driving per pursuit target (round 4) |
| AI/Common/AIVehicleHelicopter.cpp | 0x4098D0-0x43B522 | 5,780 | 515 | Ported | Helicopter.cs, HeliSheets.cs, HeliSheet.cs | round 4 check: fuel, CanSeeTarget (hidden latch), SteerToNav/OnDriving = Fly, FilterHeliAltitude, CheckHeliSheet (HeliSheets.cs), AvoidCamera, RestrictPointToRoadNet / StartPathToPoint = the search nav; player pursuit only |
| AI/Common/AIVehicleRacecar.cpp | 0x409730-0x43D462 | 5,620 | 576 | Ported | AIRacer.cs | AIVehicleRacecar/AIVehiclePid gas-brake + adaptive steering |
| AI/Common/AIRoadBlock.cpp | 0x40B060-0x43DCC5 | 5,224 | 163 | Ported | PursuitSystem.cs, CopCar.cs, Formation.cs | roadblocks, spikes, perp-past/cheating, nearest cop join; player pursuit only [ours] |
| AI/Common/AISteer.cpp | 0x409F40-0x423BC7 | 5,176 | 295 | Ported | AIMath.cs, AIRacer.cs, CopCar.cs | Separation / Intercept / Pursuit steering (CopDriver.Steer) |
| AI/Actions/AIActionHeliPursuit.cpp | 0x412570-0x4279EF | 4,245 | 431 | Ported | CopCar.cs, Helicopter.cs | round 4 check: StraightLinePursuit, SkidHitPursuit, SetNextPerpSearchDest/SearchForPerp, OnCollision abort = Helicopter.cs |
| AI/Actions/AIActionTraffic.cpp | 0x4068B0-0x42023A | 3,674 | 400 | Ported | Traffic.cs, RoadNavTraffic.cs | traffic and patrol/search driving; ShouldPullOver returns false (dead pull-over states) |
| AI/Common/AIMath.cpp | 0x40B1B0-0x410CC2 | 3,671 | 125 | Ported | AIMath.cs, AIRacer.cs, CopCar.cs | AIMath.cs: all of AIMath.cpp |
| AI/Actions/AIActionRam.cpp | 0x413540-0x4281AA | 3,603 | 293 | Ported | CopCar.cs, Helicopter.cs | Ram / Pit / PullOver per pursuit target (round 4) |
| AI/Common/AIGoal.cpp | 0x418E20-0x43D83F | 3,449 | 398 | Ported | PursuitSystem.cs, PursuitPerp.cs, PoliceZones.cs | goal lists and ChooseAction; PC goal ctors checked (ai_cops.md 9.1) |
| AI/Common/AIVehiclePursuit.cpp | 0x409820-0x43B338 | 3,323 | 302 | Ported | PursuitSystem.cs, PursuitPerp.cs, PoliceZones.cs | StartPursuit(target)/PursuitRequest/LOS test; UpdateSiren = audio agent (CopAudio) |
| AI/Common/AISpawnManager.cpp | 0x40B070-0x42FBA8 | 3,181 | 289 | Ported | PursuitSystem.cs, CopCar.cs, Formation.cs | spawn position rules, no-cop-spawn zones; the vehicle cache has no equivalent |
| AI/Common/AIVehicleCopCar.cpp | 0x409590-0x43D322 | 2,364 | 228 | Ported | PursuitSystem.cs, CopCar.cs, Formation.cs | WatchForPerps over players and AI racers (CanPursueRacers), CheckForPursuit, CanSeeTarget with the hidden latch (round 4) |
| AI/Common/AdaptivePIDController.cpp | 0x4099E0-0x4237A0 | 1,804 | 172 | Ported | AIRacer.cs | AdaptivePIDControllerComplicated |
| AI/Actions/AIActionHeadOnRam.cpp | 0x413170-0x421664 | 1,663 | 159 | Ported | CopCar.cs, Helicopter.cs | Rhino head-on ram per pursuit target |
| AI/Activities/AvoidableManager.cpp | 0x40FDF0-0x43CE17 | 1,594 | 116 | Ported | Traffic.cs, RoadNavTraffic.cs | 0.25 s avoidable update = TrafficContacts / RoadNavTraffic avoidables |
| *14 smaller files* | | 8,302 | | Ported 14 | | |

### Foundation (bWare/UTL/AttribSys)

206 KB in the exe (51 KB relevant): Ported 75%, Partial 25%, Missing 0%, effective ≈ 99%.

AttribSys (VLT database) is ported as `Core/Attrib.cs`, along with the chunk/hash helpers. bWare memory, math and containers are N/A.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Tools/AttribSys/Runtime/Common/AttribInstance.cpp | 0x73D920-0x76A9C3 | 36,743 | 255 | N/A |  | mostly inline Attrib::Instance/Attribute copies emitted in every object (accessor plumbing); the database itself is counted under the other AttribSys files |
| Libs/Support/Utility/UMath.cpp | 0x5C6520-0x5CFC9A | 20,401 | 80 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Libs/Support/Utility/UVectorMath.cpp | 0x4862B0-0x489FFA | 17,198 | 106 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Libs/Support/Miscellaneous/Carp.cpp | 0x5CB930-0x5D7FD2 | 13,054 | 17 | Partial | Attrib.cs, EventSequencer.cs | [round 9] hash32 = JenkinsHash; FastMem/VU0/tag resolvers replaced (misc §7). needs RE from speed.exe 0x5CB930-0x5D7FD2 |
| Speed/indep/libs/support/utility/UCOM.cpp | 0x40CB30-0x40F008 | 10,936 | 0 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Speed/indep/libs/support/utility/USpline.cpp | 0x5D0BE0-0x5D6BC6 | 8,405 | 0 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Libs/Support/Utility/FastMem.cpp | 0x5CFCA0-0x5D2B83 | 7,851 | 34 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Tools/AttribSys/Runtime/Common/AttribCollection.cpp | 0x4533F0-0x45A394 | 7,762 | 371 | Ported | Attrib.cs | VLT attribute database (classes, collections, inheritance, RefSpec) |
| Tools/AttribSys/Runtime/Common/AttribDatabase.cpp | 0x454530-0x45C8F0 | 7,565 | 243 | Ported | Attrib.cs | VLT attribute database (classes, collections, inheritance, RefSpec) |
| Tools/AttribSys/Runtime/Common/Attribute.cpp | 0x66DD30-0x68A289 | 7,013 | 87 | N/A |  | mostly inline Attrib::Instance/Attribute copies emitted in every object (accessor plumbing); the database itself is counted under the other AttribSys files |
| Libs/Support/Utility/UGroup.cpp | 0x5C72D0-0x5CF5E7 | 6,348 | 85 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| bWare/Src/bMemory.cpp | 0x460770-0x466364 | 6,249 | 1,100 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Tools/AttribSys/Runtime/Common/AttribClass.cpp | 0x453BE0-0x458C04 | 5,990 | 146 | Ported | Attrib.cs | VLT attribute database (classes, collections, inheritance, RefSpec) |
| bWare/Src/bPrintf.cpp | 0x45ED50-0x464848 | 5,336 | 955 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| bWare/Src/bVector.cpp | 0x45F330-0x46076A | 4,890 | 369 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Libs/Support/Miscellaneous/StringHash.cpp | 0x5CBEC0-0x5CD28D | 4,447 | 0 | Ported | Chunks.cs, Attrib.cs | bStringHash / Jenkins hash / bChunk walker |
| Libs/Support/Utility/UBezierLite.cpp | 0x5CD290-0x5CE7F2 | 4,034 | 32 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| Tools/AttribSys/Runtime/Common/AttribLoadAndGo.cpp | 0x452560-0x45C5BC | 3,966 | 371 | Ported | Attrib.cs | VLT attribute database (classes, collections, inheritance, RefSpec) |
| Tools/AttribSys/Runtime/Common/AttribSupport.cpp | 0x452850-0x45BF85 | 3,710 | 161 | Ported | Attrib.cs | VLT attribute database (classes, collections, inheritance, RefSpec) |
| bWare/Src/Strings.cpp | 0x460BC0-0x4662E0 | 3,478 | 700 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| bWare/Src/bQuaternion.cpp | 0x7321F0-0x732CCA | 3,468 | 81 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| bWare/Src/SpeedScript.cpp | 0x461A40-0x465D7A | 3,154 | 357 | N/A |  | engine foundation (memory, math, containers) replaced by .NET/Unity |
| *16 smaller files* | | 18,676 | | N/A 11, Ported 5 | | |

### Platform (PC/GC/PS2/Xenon low level)

160 KB in the exe, all N/A.

The PC D3D9 renderer, movie and memory-card platform code. It matched GameCube file names in the symbol tables. N/A.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| pc/static initialisers (chunk loaders, globals) | 0x883C80-0x88F241 | 35,513 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Ecstasy/EcstasyE.cpp | 0x6BE6C0-0x6E774B | 27,232 | 1,037 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Render/AcidFX_G.cpp | 0x6BC890-0x6DA345 | 25,764 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Movie_GC.cpp | 0x6C8B60-0x6E44BD | 23,206 | 23 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Ecstasy/eVisualTreatment.cpp | 0x6C3360-0x6E0A94 | 15,563 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/xSparks.cpp | 0x6C5090-0x6C8B54 | 13,530 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Ecstasy/TextureInfoPlat.cpp | 0x6BED60-0x6DE2FC | 6,238 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/MemoryCard/MemoryCardImp.cpp | 0x6CBD20-0x6CD67D | 5,357 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Ecstasy/eEnvMapE.cpp | 0x6BD0D0-0x6DA8DF | 3,113 | 0 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Ecstasy/eMathE.cpp | 0x6BD7C0-0x6CF2A4 | 3,049 | 23 | N/A |  | platform/library code replaced by Unity/.NET |
| Speed/GameCube/Src/Ecstasy/eViewPlat.cpp | 0x6BE590-0x6CF897 | 2,392 | 134 | N/A |  | platform/library code replaced by Unity/.NET |
| *5 smaller files* | | 3,129 | | N/A 5 | | |

### Gameplay/career

153 KB in the exe (153 KB relevant): Ported 7%, Partial 93%, Missing 0%, effective ≈ 94%.

**Not decompiled at all** (every Gameplay/*.cpp in refs/nfsmw is an empty placeholder). The Unity career (`GameSession.cs`, `Story.cs`, `Career.cs`, `RaceManager.cs`) was rebuilt from gameplay.bin data, the stategraph dump and the specs, so everything is Partial: it works, but it does not follow GManager/GRaceStatus/GActivity code paths.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Gameplay/GManager.cpp | 0x5DD750-0x603720 | 39,932 | 0 | Partial | *, Triggers.cs, LuaDrive.cs | [round 9] icon pool (AllocIcon/FreeDisposable/RefreshZoneIcons/Hide/Restore), WarpToMarker/NotifyWorldService, EnableBin*; remaining accounted §18.5; left: per-section icon restore, warp camera reset/fade. needs RE from speed.exe 0x5DD750-0x603720 |
| Gameplay/GRaceDatabase.cpp | 0x5D9580-0x603D4C | 31,111 | 4 | Partial | RaceDatabase.cs, Career.cs | [round 9] accessor-to-attribute table §18.5; UpdateRaceScore max fix; bin open-world heat; save layout = Profile; GRaceCustom = FE. needs RE from speed.exe 0x5D9580-0x603D4C |
| Gameplay/GRaceStatus.cpp | 0x5DB170-0x6040A3 | 28,148 | 0 | Partial | RaceManager*.cs, PursuitSystem.Script.cs | [round 9] GetBin*Heat ported (§18.6); racer-info mapped; stock-car / load-wait plumbing remains. needs RE from speed.exe 0x5DB170-0x6040A3 |
| Gameplay/GActivity.cpp | 0x5E77B0-0x60791A | 15,118 | 0 | Partial | StategraphRuntime, LuaFlow | [round 9] Register/UnregisterMessageHandlers, DeleteObjects mapped/replaced. needs RE from speed.exe 0x5E77B0-0x60791A |
| Gameplay/GTrigger.cpp | 0x5DB470-0x601F77 | 13,823 | 0 | Partial | Triggers.cs, LuaDrive.cs | [round 9] Show/HideIcon; flares not ported. needs RE from speed.exe 0x5DB470-0x601F77 |
| Gameplay/GIcon.cpp | 0x5DE480-0x5ECCDD | 9,351 | 0 | Partial | Triggers.cs | [round 9] ctor, pool, visibility; particles/geometry = EventMarkers [ours]. needs RE from speed.exe 0x5DE480-0x5ECCDD |
| Gameplay/GRuntimeInstance.cpp | 0x5DAA80-0x5E82FD | 6,704 | 0 | Ported | LuaTriggers.cs | [round 9] GetPosition/Direction 0x5E2970/0x5DACF0 = PointOf |
| Gameplay/GObjectBlock.cpp | 0x5E50E0-0x602BAD | 4,525 | 0 | Partial |  GameplayState | [round 9] vault object blocks replaced by the resident database. needs RE from speed.exe 0x5E50E0-0x602BAD |
| Gameplay/GVault.cpp | 0x5DE0E0-0x5FA8DB | 1,939 | 0 | Partial | LuaFlow.cs | [round 9] paging replaced (resident data). needs RE from speed.exe 0x5DE0E0-0x5FA8DB |
| Gameplay/GHandler.cpp | 0x5DD3C0-0x5E7CD1 | 1,584 | 0 | Partial | StategraphRuntime.cs | [round 9] HandleMessage = Deliver; no FilterBlocks in data. needs RE from speed.exe 0x5DD3C0-0x5E7CD1 |
| *7 smaller files* | | 4,593 | | Ported 5, Partial 2 | | |

### World: collision, roads, triggers

135 KB in the exe (113 KB relevant): Ported 97%, Partial 3%, Missing 0%, effective ≈ 100%.

Road network reader plus WRoadNav/cookie trail/A* port in progress (`Core/RoadNetwork.cs`, `RoadNav.cs`). World collision does **not** use the game's WCollisionPack (0x3B801): colliders are the render meshes (traffic_collision.md Part B). Triggers are re-done as distance checks.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| World/Common/WRoadNetwork.cpp | 0x764AA0-0x789C8B | 49,556 | 3,384 | Ported | RoadNetwork.cs, RoadNav.cs, RoadNavTraffic.cs | round 4: IsWrongWay, IsPointInCookieTrail, IsSegmentInCookieTrail, IsSegmentInPath, GetPathDistanceRemaining, InitFromOtherNav, IsOnLegalRoad, GetShortcutNumber, GetRoadSpeechId added; PullOver and the gradual lane change are dead on PC; InitAtSegment(seg, lane, t) only via InitAtPoint |
| World/Common/WTrigger.cpp | 0x766760-0x784EB9 | 16,242 | 515 | Ported | WTrigger.cs | [round 9] activation test, UpdateBox |
| World/Common/WGrid.cpp | 0x763540-0x7817A2 | 15,093 | 368 | N/A |  | spatial grid broadphase, replaced by PhysX/Unity (spec B.6) |
| World/Common/WCollisionMgr.cpp | 0x763B40-0x789971 | 12,191 | 1,207 | Ported | WCollisionQueries.cs, WorldCollision.cs | [round 9] GetTriList, barrier queries; Collide(instances) dead |
| World/Common/WCollider.cpp | 0x771B80-0x78A003 | 8,466 | 360 | Ported | WCollider.cs | [round 9] Refresh 0x789EC0 |
| World/Common/WCollisionAssets.cpp | 0x763790-0x78979C | 7,834 | 304 | Ported | CollisionPack.cs, WorldQueries.cs | [round 9] OBB query objects |
| World/Common/WPathFinder.cpp | 0x76D1E0-0x788887 | 4,705 | 574 | Ported | RoadNav.cs | AStarSearch (WPathFinder.cpp:127) |
| World/Common/WWorldMath.cpp | 0x7628B0-0x767EF1 | 4,382 | 211 | Ported | WWorldMath.cs | [round 9] IntersectCircle 0x767400 |
| World/Common/WWorldPos.cpp | 0x767F00-0x789D88 | 4,159 | 213 | Ported | WCollisionQueries.cs, WorldQueries.cs | [round 9] face-list search, SetTolerance |
| World/HeliRenderConn.cpp | 0x738930-0x75E80A | 3,081 | 121 | Partial | Helicopter.cs | [round 9] model offset 0x739420; shadow scale not passed. ready: decomp source |
| World/Common/WGridManagedDynamicElem.cpp | 0x782CB0-0x784B59 | 2,963 | 131 | N/A |  | spatial grid broadphase, replaced by PhysX/Unity (spec B.6) |
| World/HeliSheet.cpp | 0x73D150-0x7543E2 | 2,925 | 219 | Ported | HeliSheet.cs, HeliSheets.cs | [round 9] GetElevation/FindHeliPoly 1:1 |
| World/Common/WWorld.cpp | 0x770280-0x78A211 | 2,650 | 88 | N/A | WCollisionQueries, WorldCollision | [round 9] WGrid/asset loading replaced by streaming |
| World/Common/WGridNode.cpp | 0x77A050-0x782C55 | 1,695 | 10 | N/A |  | spatial grid broadphase, replaced by PhysX/Unity (spec B.6) |
| *2 smaller files* | | 1,867 | | Ported 2 | | |

### Frontend: data & career logic

133 KB in the exe (133 KB relevant): Ported 75%, Partial 25%, Missing 0%, effective ≈ 99%.

Shop/garage data logic (CarCustomize, CustomizeManager, VehicleDB, UnlockSystem, FEDatabase) is decompiled but only loosely followed: prices and unlocks are invented, and the CarPartDB (GlobalB 0x80034602) is not decoded (customization.md §9).

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Frontend/MenuScreens/Safehouse/customize/CarCustomize.cpp | 0x7A2B80-0x7C3A45 | 52,213 | 5,045 | Ported | CustomizeScreen.cs, CustomizeExe.cs, CustomizeShopScreens.cs | CustomizeMain / Category / Customization / Part screens, Paint (ArrayScroller), Performance (TwoStageSliders), HUD colour, Numbers, ShoppingCart (customization.md §10). [?] HUD colour cursor offset, visual match |
| Frontend/Database/FEDatabase.cpp | 0x57F4D0-0x5ACFB7 | 30,601 | 1,447 | Partial | FEDatabase.cs, FEDb.cs | [round 9] CareerSettings flags not in Profile. ready: decomp source |
| Frontend/Database/VehicleDB.cpp | 0x56EBB0-0x5A438C | 16,456 | 1,439 | Ported | FEImpound.cs, FEDatabase.cs | [round 9] stable totals |
| Frontend/Careers/UnlockSystem.cpp | 0x576340-0x5A649D | 13,215 | 1,163 | Ported | Shop.cs, ShopRules.cs, CustomizeExe.cs | [round 9] ClearAllNewStatus |
| Frontend/MenuScreens/Safehouse/customize/CustomizeManager.cpp | 0x7A4990-0x7C391A | 13,006 | 1,411 | Ported | Shop.cs, VisualScreens.cs | part lists, preview, cart, trade-in, part states, temp coloured part (customization.md §10) |
| Frontend/Database/RaceDB.cpp | 0x56D9D0-0x5A41D3 | 4,040 | 367 | Partial | FEDb.cs, RapSheetScreens.cs | [round 9] CST counters missing. ready: decomp source |
| Frontend/Database/uiProfileManager.cpp | 0x526200-0x56093E | 3,718 | 153 | Ported | ProfileManagerScreen.cs | [round 9]  |
| Frontend/MenuScreens/Safehouse/customize/MyCarsManager.cpp | 0x7A7380-0x7C13D9 | 3,225 | 272 | Ported | CarSelectScreens.cs | [round 9]  |

### Animation/NIS

127 KB in the exe (99 KB relevant): Ported 100%, Partial 0%, Missing 0%, effective ≈ 100%.

EAGL4 animation banks (relocatable ELF, delta-compressed channels) are decoded (`Core/Nis.cs`). NIS playback drives the car tracks and ICE cameras (`NisPlayer.cs`). Skinned characters, world animations and anim-driven events are missing.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| EAGL4Anim/eagl4supportdlopen.cpp | 0x4914C0-0x4A9F41 | 24,692 | 253 | Ported | Nis.cs | EAGL4 anim banks: relocatable ELF + delta-compressed channels (0x48FF00) |
| EAGL4Anim/FnDeltaF3.cpp | 0x495DB0-0x49A368 | 16,733 | 456 | Ported | Nis.cs | EAGL4 anim banks: relocatable ELF + delta-compressed channels (0x48FF00) |
| EAGL4Anim/SystemCmn.cpp | 0x4A5740-0x4A86C5 | 11,784 | 0 | N/A |  | EAGL4 runtime support |
| EAGL4Anim/eagl4supportsympool.cpp | 0x492070-0x493F4A | 7,534 | 129 | N/A |  | EAGL4 runtime support |
| EAGL4Anim/FnDeltaF1.cpp | 0x493F50-0x495DA4 | 6,972 | 422 | Ported | Nis.cs | EAGL4 anim banks: relocatable ELF + delta-compressed channels (0x48FF00) |
| EAGL4Anim/Skeleton.cpp | 0x49F410-0x4A10CF | 6,935 | 0 | Ported | EaglAnim.cs, WorldAnim.cs, NisCharacter.cs | [round 9] GetStillPose 0x49FF90, PoseSQTToGlobal 0x49FCF0 1:1; others dead by xref |
| Animation/AnimScene.cpp | 0x4477B0-0x450DBE | 5,682 | 839 | Ported | Nis.cs, NisPlayer.cs, EaglAnim.cs | scene header 1:1 (SceneType, StartFrame..VanishFrame, overlay), clock, scene frame by SceneType, bone-1 car pose, untracked cars deactivated, SpecialCarList tables, one-frame entity lag, pause, Construct/MessageBusted rules, AdvanceAnimTime literal (PC /30 at 0x449648) |
| Animation/AnimPlayer.cpp | 0x447230-0x45132A | 5,577 | 559 | Ported | Nis.cs, NisPlayer.cs, EaglAnim.cs | scene header 1:1 (SceneType, StartFrame..VanishFrame, overlay), clock, scene frame by SceneType, bone-1 car pose, untracked cars deactivated, SpecialCarList tables, one-frame entity lag, pause, Construct/MessageBusted rules, AdvanceAnimTime literal (PC /30 at 0x449648) |
| EAGL4Anim/DeltaCompressedData.cpp | 0x48FD80-0x49132E | 5,461 | 343 | Ported | Nis.cs | EAGL4 anim banks: relocatable ELF + delta-compressed channels (0x48FF00) |
| EAGL4Anim/eagl4AnimBank.cpp | 0x48E270-0x4A8B9A | 3,618 | 90 | Ported | Nis.cs | EAGL4 anim banks: relocatable ELF + delta-compressed channels (0x48FF00) |
| Animation/AnimEntity_BasicCharacter.cpp | 0x446660-0x44CAC7 | 3,546 | 400 | Ported | NisCharacter.cs | incremental CAnimCtrl, t/q/s dofs before the skeletal dof, pelvis ground probe, Advance/SetTime; LOD switching and SetLocalVelocity not ported |
| EAGL4Anim/CsisEventChannel.cpp | 0x48EE40-0x48FD7E | 3,363 | 121 | N/A | EaglAnim.cs | [round 9] no type-24 channel in the 126 shipped banks |
| Animation/AnimCtrl.cpp | 0x445A10-0x44E0E8 | 3,296 | 280 | Ported | Nis.cs, NisPlayer.cs, EaglAnim.cs | scene header 1:1 (SceneType, StartFrame..VanishFrame, overlay), clock, scene frame by SceneType, bone-1 car pose, untracked cars deactivated, SpecialCarList tables, one-frame entity lag, pause, Construct/MessageBusted rules, AdvanceAnimTime literal (PC /30 at 0x449648) |
| Animation/WorldAnimCtrl.cpp | 0x447D60-0x44D4D5 | 2,980 | 354 | Ported | WorldAnimations.cs, WorldAnim.cs | CWorldAnimCtrl clock, Init rules (master+local delay, deterministic bRandom seed 0x69BABE69, trigger/named-range flags), InstantiateAnimTree track filter, section visibility, ControlWorldAnim/ResetWorldAnimations |
| Animation/AnimEntity_WorldEntity.cpp | 0x44A150-0x44FAE9 | 2,835 | 596 | Ported | WorldAnimations.cs, WorldAnim.cs | CWorldAnimCtrl clock, Init rules (master+local delay, deterministic bRandom seed 0x69BABE69, trigger/named-range flags), InstantiateAnimTree track filter, section visibility, ControlWorldAnim/ResetWorldAnimations |
| Animation/AnimBank.cpp | 0x4485E0-0x44C512 | 2,810 | 230 | Ported | Nis.cs, NisPlayer.cs, EaglAnim.cs | scene header 1:1 (SceneType, StartFrame..VanishFrame, overlay), clock, scene frame by SceneType, bone-1 car pose, untracked cars deactivated, SpecialCarList tables, one-frame entity lag, pause, Construct/MessageBusted rules, AdvanceAnimTime literal (PC /30 at 0x449648) |
| Animation/WorldAnimInstanceDirectory.cpp | 0x44BEC0-0x44F213 | 2,659 | 319 | Ported | WorldAnimations.cs, WorldAnim.cs | CWorldAnimCtrl clock, Init rules (master+local delay, deterministic bRandom seed 0x69BABE69, trigger/named-range flags), InstantiateAnimTree track filter, section visibility, ControlWorldAnim/ResetWorldAnimations |
| Animation/AnimEngineManager.cpp | 0x48D1A0-0x48DB35 | 2,427 | 67 | N/A |  | EAGL4 runtime support |
| Animation/AnimWorldScene.cpp | 0x44B380-0x4505D1 | 2,388 | 265 | Ported | WorldAnimations.cs, WorldAnim.cs | CWorldAnimCtrl clock, Init rules (master+local delay, deterministic bRandom seed 0x69BABE69, trigger/named-range flags), InstantiateAnimTree track filter, section visibility, ControlWorldAnim/ResetWorldAnimations |
| Animation/AnimChooseArrest.cpp | 0x44D6A0-0x44DE1B | 1,869 | 262 | Ported | Nis.cs, NisPlayer.cs, EaglAnim.cs | scene header 1:1 (SceneType, StartFrame..VanishFrame, overlay), clock, scene frame by SceneType, bone-1 car pose, untracked cars deactivated, SpecialCarList tables, one-frame entity lag, pause, Construct/MessageBusted rules, AdvanceAnimTime literal (PC /30 at 0x449648) |
| EAGL4Anim/eagl4supportbase.cpp | 0x48DB40-0x4914B7 | 1,798 | 28 | N/A |  | EAGL4 runtime support |
| Animation/AnimLocator.cpp | 0x446B70-0x44A505 | 1,657 | 105 | Ported | Nis.cs, NisPlayer.cs, EaglAnim.cs | scene header 1:1 (SceneType, StartFrame..VanishFrame, overlay), clock, scene frame by SceneType, bone-1 car pose, untracked cars deactivated, SpecialCarList tables, one-frame entity lag, pause, Construct/MessageBusted rules, AdvanceAnimTime literal (PC /30 at 0x449648) |
| *21 smaller files* | | 3,412 | | N/A 17, Ported 4 | | |

### Camera

110 KB in the exe (97 KB relevant): Ported 89%, Partial 11%, Missing 0%, effective ≈ 99%.

Player chase camera ported from CubicCameraMover (`ChaseCamera.cs`, 0x47D3C0), ICE tracks read and played for NIS. **Camera code is barely decompiled** (most Camera/*.cpp are empty), so the pursuit/track/rear-view/jump cameras need RE.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Camera/ICE/ICEManager.cpp | 0x46CE80-0x484026 | 21,875 | 44 | Ported | IceCameras.cs, IceShot.cs, ChaseCamera.cs | hard-cut classification via KeysShared, ICECut tags, car frame from forward+up, IgnoreOrientation, NIS parameter from scene progress, generic clock on sim time (IceUseRealTime), NearZ, obstructionRaise, noise/shake rules, ChooseGoodSceneCameraTrackIndex; open: bCarSpaceLag springs, smooth entry/exit (unused in retail) |
| Camera/Movers/Cubic.cpp | 0x46AE40-0x482EBD | 12,120 | 0 | Ported | ChaseCamera.cs | CubicCameraMover::Update 0x47D3C0 |
| Camera/CameraMover.cpp | 0x467F30-0x480D1F | 10,724 | 304 | Ported | CameraNoise.cs, ChaseCamera.cs | [round 9] ChopperNoise 0x470A60; HandheldNoise dead |
| Camera/Actions/CDActionDrive.cpp | 0x469C30-0x485C65 | 9,950 | 0 | Ported | ChaseCamera.cs, CameraNoise.cs | [round 9] explosion shake 0x47308E, TriggerPulse 0x472B1D |
| Camera/Movers/SelectCar.cpp | 0x46B7A0-0x47AFAF | 9,529 | 0 | Partial | SelectCarCamera.cs | [round 9] full mover; CarShowroom still on its own copy. needs RE from speed.exe 0x46B7A0-0x47AFAF |
| Camera/Movers/TrackCop.cpp | 0x46B6B0-0x47E4C8 | 9,268 | 0 | Ported | TrackCameras.cs | [round 9] FindPursuitVehiclePosition 0x475170; CopViewCameraMover dead |
| Camera/Camera.cpp | 0x466E70-0x480C52 | 7,854 | 253 | Ported | CameraNoise.cs, IceShot.cs, ChaseCamera.cs | [round 9] integer-hash noise 0x467250..0x467420 |
| Camera/CameraAI.cpp | 0x4719F0-0x48102E | 6,490 | 6 | Ported | CameraDirector.cs | [round 9] JumpCamsAllowed 0x469E30 |
| Camera/Actions/CDActionShowcase.cpp | 0x46A770-0x485ED5 | 5,066 | 0 | N/A |  | [round 9] registered 0x884DC0, never requested |
| Camera/Actions/CDActionDebugWatchCar.cpp | 0x46AA50-0x485AF3 | 3,632 | 0 | N/A |  | debug cameras |
| Camera/ICE/ICEData.cpp | 0x477900-0x47EE4C | 3,052 | 0 | Ported | IceCameras.cs | ICE track reader |
| Camera/Movers/DebugWorld.cpp | 0x4762D0-0x47EDA8 | 2,996 | 0 | N/A |  | debug cameras |
| Camera/ICE/ICEOverlays.cpp | 0x46E890-0x46F5F9 | 2,703 | 0 | Ported | IceCameras.cs, IceShot.cs, ChaseCamera.cs | hard-cut classification via KeysShared, ICECut tags, car frame from forward+up, IgnoreOrientation, NIS parameter from scene progress, generic clock on sim time (IceUseRealTime), NearZ, obstructionRaise, noise/shake rules, ChooseGoodSceneCameraTrackIndex; open: bCarSpaceLag springs, smooth entry/exit (unused in retail) |
| Camera/Actions/CDActionIce.cpp | 0x46A930-0x4862A3 | 2,512 | 0 | Ported | IceCameras.cs, CameraNoise.cs, ChaseCamera.cs | [round 9] bCarSpaceLag 0x4834B5; event 0x15 dead |
| Camera/Movers/TrackCar.cpp | 0x46B410-0x47E18C | 2,084 | 0 | Ported | TrackCameras.cs | [round 9] rack focus 0x475015 |
| *5 smaller files* | | 2,415 | | Partial 3, N/A 1, Missing 1 | | |

### Misc

109 KB in the exe (38 KB relevant): Ported 54%, Partial 46%, Missing 0%, effective ≈ 98%.

JDLZ/HUFF codecs, tCubic and Table/Graph helpers, and attribute loading are ported. The rest is engine plumbing (N/A); cheats and rumble are missing.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Misc/QuickSpline.cpp | 0x6490E0-0x65E5C7 | 11,791 | 267 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/GameFlow.cpp | 0x64B020-0x667338 | 11,300 | 1,394 | Partial | GameFlow.cs | [round 9] loader stubs / HighAttribAlloc documented. ready: decomp source |
| Misc/Hermes.cpp | 0x6548C0-0x665C00 | 11,209 | 155 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/LZCompress.cpp | 0x64DA20-0x6503D1 | 10,144 | 1,758 | Ported | Jdlz.cs, EaHuff.cs | JDLZ/HUFF/RAWW (0x64DCA0, 0x65B5C0) |
| Misc/bFile.cpp | 0x64CEC0-0x6621BD | 10,041 | 1,138 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/Main.cpp | 0x64A500-0x66688E | 6,740 | 600 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/QueuedFile.cpp | 0x64CAC0-0x661CB5 | 6,120 | 593 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/Timer.cpp | 0x653F20-0x66139B | 5,770 | 465 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/Point.cpp | 0x651030-0x6522E9 | 5,446 | 448 | Ported | ChaseCamera.cs, PlayerCar.cs, AIRacer.cs | tCubic 0x6513C0, Table/Graph/Average/PidError |
| Misc/ResourceLoader.cpp | 0x64A870-0x663F3E | 5,375 | 881 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/MD5.cpp | 0x6503E0-0x65BCB4 | 5,358 | 206 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/Joylog.cpp | 0x64C750-0x661A29 | 5,154 | 601 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| Misc/EasterEggs.cpp | 0x652F60-0x65E075 | 4,055 | 194 | Partial | EasterEggs.cs, GameBootstrap.cs | PC table (burgerking/castrol), 32-slot ring, KeyboardInput/HandleJoy, switches (misc.md §2). ready: decomp source |
| Misc/Table.cpp | 0x653260-0x65C683 | 3,664 | 272 | Ported | ChaseCamera.cs, PlayerCar.cs, AIRacer.cs | tCubic 0x6513C0, Table/Graph/Average/PidError |
| Misc/Rumble.cpp | 0x64BB70-0x64C740 | 2,731 | 368 | Partial | Rumble.cs | [round 9] DoFFB channels; no device by design. ready: decomp source |
| Misc/BuildRegion.cpp | 0x649A80-0x64A4F3 | 2,538 | 96 | N/A |  | file IO, timers, messaging (Hermes), main loop, debug |
| *15 smaller files* | | 4,020 | | N/A 13, Ported 2 | | |

### Events & main loop

103 KB in the exe, all N/A.

Generated event classes, the scheduler and the event sequencer are message plumbing (N/A). The behaviour they trigger is counted in the handler files.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Generated/Events/EAIEngineRev.cpp | 0x635FA0-0x637FA4 | 7,809 | 0 | N/A |  | event/message plumbing (generated event classes); behaviour lives in handlers elsewhere |
| Generated/Events/EEngineBlown.cpp | 0x6219B0-0x640E66 | 6,887 | 0 | N/A |  | event/message plumbing (generated event classes); behaviour lives in handlers elsewhere |
| Generated/Events/EventDefs.cpp | 0x6293A0-0x648DAB | 6,719 | 0 | N/A |  | event/message plumbing (generated event classes); behaviour lives in handlers elsewhere |
| Generated/Events/EWorldMapOn.cpp | 0x625A50-0x632D93 | 4,149 | 0 | N/A |  | event/message plumbing (generated event classes); behaviour lives in handlers elsewhere |
| Main/Common/EventSequencer.cpp | 0x627480-0x645DC8 | 3,952 | 631 | N/A |  | event/message plumbing (generated event classes); behaviour lives in handlers elsewhere |
| Main/Common/Scheduler.cpp | 0x627660-0x648555 | 2,592 | 0 | N/A |  | event/message plumbing (generated event classes); behaviour lives in handlers elsewhere |
| *160 smaller files* | | 73,163 | | N/A 160 | | |

### FEng (UI engine)

92 KB in the exe (53 KB relevant): Ported 98%, Partial 2%, Missing 0%, effective ≈ 100%.

The .fng reader is complete (`Core/FEng.cs`). `FEView.cs` builds packages, runs scripts and key tracks. Message routing is per screen, and list boxes are not ported.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| FEng/FEngine.cpp | 0x5B06F0-0x5C5719 | 14,342 | 1,421 | Ported | FEngEngine.cs, FEView.cs, FrontEnd.cs | message queue, responses (every MR id the shipped packages use: 0-3, 256, 257, 259, 513, 515, 768, 769, 1280, 1281 — harness fecust responses), pad/direction/mouse processing, repeat 320/120 ms. Unreachable from data: MR 258/260-263/512/514/516/704-706, list-box responses, PAD_*_RELEASED / *_HELD (only BUTTON_RELEASED in the PC controller packages, driven by ControllerScreen). Package stack / levels / control masks replaced by FrontEnd |
| FEng/FEListBox.cpp | 0x5AD990-0x5C0E1C | 8,700 | 432 | N/A |  | no FEng List object (type 4) in any shipped package (harness fecust objtypes) — never reached |
| FEng/FECodeListBox.cpp | 0x5ADC30-0x5C40A1 | 8,637 | 610 | N/A |  | no FEng CodeList object (type 6) in any shipped package (harness fecust objtypes) — never reached |
| FEng/FEPackageReader.cpp | 0x5B22F0-0x5C53B9 | 8,154 | 992 | Ported | FEng.cs | .fng package reader |
| FEng/FEPackage.cpp | 0x5B05B0-0x5C1CFA | 8,047 | 739 | Ported | FEngEngine.cs, FEView.cs | [round 9] Update/UpdateObject/UpdateObjectTracks/IssueScriptMessages 0x5C1B30/0x5C17E0/0x5BEC90/0x5BC530; library refs dead |
| FEng/FEMath.cpp | 0x5ACFC0-0x5C5B59 | 4,572 | 87 | N/A |  | FEng containers/math/type library |
| FEng/FEObject.cpp | 0x5B10F0-0x5C3542 | 4,307 | 426 | Ported | FEngEngine.cs, FEView.cs | [round 9] SetScript 0x5B8400/0x5BC160, SetTrackValue, Clone 0x5C31A0 |
| FEng/FEKeyTrack.cpp | 0x5AD500-0x5C205C | 4,224 | 88 | Ported | FEng.cs, FEngEngine.cs, FEView.cs | [round 9] GetKeyAt/GetDeltaKeyAt; per-node base keys; key editing 0x5B5900/0x5C0830-0x5C0B70/0x5C1F20 no xrefs (dead) |
| FEng/FEWideString.cpp | 0x5B30E0-0x5C02A6 | 4,043 | 83 | N/A |  | FEng containers/math/type library |
| FEng/FEList.cpp | 0x5AED80-0x5BA522 | 3,757 | 171 | N/A |  | FEng containers/math/type library |
| FEng/FETypeLib.cpp | 0x5B2C30-0x5C3183 | 3,614 | 187 | N/A |  | FEng containers/math/type library |
| FEng/FEMessageResponse.cpp | 0x5AD690-0x5C2096 | 3,510 | 120 | Ported | FEng.cs, FEngEngine.cs | [round 9] FindConditionBranchTarget 0x5B0140; unused MR ids dead by data scan |
| FEng/FEKeyInterpLinear.cpp | 0x5AEB00-0x5BDFFA | 2,868 | 252 | Ported | FEng.cs | [round 9] clamp/loop; ping-pong/interp 0 dead by data |
| FEng/FEScript.cpp | 0x5B27D0-0x5C3072 | 2,703 | 103 | Ported | FEng.cs | [round 9] FindTrack 0x5B2910 |
| FEng/FEGroup.cpp | 0x5AE500-0x5C4A28 | 2,167 | 32 | Ported | FEView.cs | [round 9] tree walks |
| *15 smaller files* | | 11,006 | | N/A 11, Ported 3, Partial 1 | | |

### Audio (Speech)

90 KB in the exe (82 KB relevant): Ported 100%, Partial 0%, Missing 0%, effective ≈ 100%.

Only the police scanner plays fixed lines for named events (`CopRadio.cs`). The speech AI (SoundAI, observers, pursuit/roadblock/strategy/music flows), sentence scripts and street-name slotting are missing.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Speech/SoundAI.cpp | 0x700F50-0x721B25 | 32,852 | 2,527 | Ported | SpeechAI.cs | [round 9] GetCustomized; split screen off |
| Speech/Observer.cpp | 0x7009A0-0x7200A5 | 10,163 | 834 | Ported | SpeechScript.cs, SpeechAI.cs, SpeechActors.cs | AssessArrest DMIX 5, Process GetRandomCop draw, Reset, Assess180 char counter fixed; road direction from the road graph [?] |
| Speech/MiscSpeech.cpp | 0x701540-0x71DCDD | 10,148 | 702 | Ported | SpeechAI.cs | [round 9] SMSCellCall 0x71D860, MapSMSToSPCHEnums 0x701540 |
| Speech/EAXCop.cpp | 0x7003E0-0x719B23 | 9,309 | 1,246 | Ported | SpeechScript.cs, SpeechAI.cs, SpeechActors.cs | all event methods matched; heli health needs IDamageable (port heli has none) |
| Speech/SpeechCache.cpp | 0x6FFF80-0x712BCE | 8,067 | 372 | N/A | SpeechManager.cs | [round 9] sample cache replaced; readiness gate kept |
| Speech/StrategyFlow.cpp | 0x700840-0x71E893 | 5,145 | 619 | Ported | SpeechScript.cs, SpeechAI.cs, SpeechActors.cs | matched; formation type now read from PursuitSystem.FormationType |
| Speech/MusicFlow.cpp | 0x700CC0-0x71F6A7 | 4,312 | 503 | Ported | Music.cs | [round 9] 600 s swap |
| Speech/PursuitFlow.cpp | 0x7006E0-0x71E474 | 3,818 | 653 | Ported | SpeechScript.cs, SpeechAI.cs, SpeechActors.cs | matched |
| Speech/EAXDispatch.cpp | 0x700370-0x71F853 | 3,018 | 378 | Ported | SpeechScript.cs, SpeechAI.cs, SpeechActors.cs | all methods matched |
| Speech/RoadblockFlow.cpp | 0x700AD0-0x7200C0 | 2,945 | 388 | Ported | SpeechScript.cs, SpeechAI.cs, SpeechActors.cs | matched; MMiscSound(1) RBLOS has no listener in the game |
| *3 smaller files* | | 2,618 | | Ported 3 | | |

### Lua scripting

87 KB in the exe (87 KB relevant): Ported 0%, Partial 100%, Missing 0%, effective ≈ 95%.

The Lua 5.0 VM and bindings are N/A: the scripts are not executed, and their race/career behaviour is hand-ported. LuaGameHooks.cpp (the C++ side of the scripts) is not decompiled.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Lua/LuaGameHooks.cpp | 0x6044C0-0x61E59B | 30,849 | 0 | Partial | LuaDrive.cs | [round 9] SetFlag, Show/HideTriggerIcon, EResetProps; NoOps verified. needs RE from speed.exe 0x6044C0-0x61E59B |
| Lua/source/lapi.c | 0x6059E0-0x615320 | 18,519 | 857 | Partial | * | [round 9] C API account lua_hooks §8.2; coroutines/debug/dump/GC-tuning unreachable. ready: decomp source |
| Lua/LuaBindery.cpp | 0x6040B0-0x61F1FB | 13,937 | 0 | Partial | LuaBindingTable.cs | [round 9] binding tables. needs RE from speed.exe 0x6040B0-0x61F1FB |
| Lua/LuaRuntime.cpp | 0x609F20-0x61F54B | 13,112 | 0 | Partial | LuaFlow.cs | [round 9] debug dump / heap pager replaced. needs RE from speed.exe 0x609F20-0x61F54B |
| Lua/source/lstate.c | 0x615430-0x616D3D | 6,041 | 208 | Partial | LuaState.cs | [round 9] one state per runtime. ready: decomp source |
| Lua/LuaAttributes.cpp | 0x61BE80-0x61F7D7 | 3,660 | 0 | Partial | AttribDatabase | [round 9] Init registration only. needs RE from speed.exe 0x61BE80-0x61F7D7 |
| Lua/LuaPostOffice.cpp | 0x609FE0-0x61FE78 | 2,954 | 0 | Partial | StategraphRuntime.Post | [round 9] subscriptions delivered directly. needs RE from speed.exe 0x609FE0-0x61FE78 |
| *19 smaller files* | | 0 | | Partial 19 | | |

### Sim

76 KB in the exe (44 KB relevant): Ported 36%, Partial 64%, Missing 0%, effective ≈ 88%.

Simulation framework N/A. NIS activity, local player and quick-game setup are partial. SimSurface (per-surface grip and sound) is missing.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Sim/Common/Simulation.cpp | 0x6E8700-0x6FC4A9 | 20,550 | 993 | N/A |  | simulation framework (tasks, entities, activities) replaced by Unity update loop |
| Sim/Activities/NISActivity.cpp | 0x6EA270-0x6FD697 | 16,432 | 853 | Ported | NisPlayer.cs, NisCarControl.cs, NisSound.cs | load/ready/play/skip flow, arrest hold + fade, sound control states per NIS kind, speech clears, FLYBY overlay; EQuitToFE(Infractions) when ChooseArrestLocation fails kept as the player's spot [ours] |
| Sim/Entities/LocalPlayer.cpp | 0x6E98C0-0x6FCC90 | 9,268 | 405 | Partial | Rumble.cs | [round 9] DoFFB/CanDoFFB/OnCollision-FFB (no device in Unity). ready: decomp source |
| Sim/Activities/QuickGame.cpp | 0x6E9FA0-0x6F9B03 | 8,133 | 202 | Partial | GameFlow.cs | [round 9] OnManageState/ShouldPauseInput (misc §6.5); HandleSkipFEOptions dead. ready: decomp source |
| Sim/Common/SimSurface.cpp | 0x4A9FB0-0x4AAAE5 | 4,889 | 46 | Partial | PlayerCar.cs | game breaker rules, handling tuning in the tire model. ready: decomp source |
| Sim/Activities/GameplayActivity.cpp | 0x6EAAD0-0x6FA4CC | 4,269 | 78 | Partial | GameFlow.cs | [round 9] no uncited functions remain. ready: decomp source |
| Sim/Common/SimActivity.cpp | 0x402900-0x40FDE8 | 3,677 | 34 | N/A |  | simulation framework (tasks, entities, activities) replaced by Unity update loop |
| Sim/Common/SimEntity.cpp | 0x6E9D00-0x6FCA28 | 2,547 | 76 | N/A |  | simulation framework (tasks, entities, activities) replaced by Unity update loop |
| Sim/Activities/CareerGame.cpp | 0x6F9B10-0x6FB50E | 2,256 | 10 | Partial | GameFlow.cs | [round 9] no uncited functions remain. needs RE from speed.exe 0x6F9B10-0x6FB50E |
| Sim/Common/Util.cpp | 0x6E8EF0-0x6F13E2 | 1,505 | 12 | N/A |  | simulation framework (tasks, entities, activities) replaced by Unity update loop |
| *6 smaller files* | | 4,192 | | N/A 6 | | |

### Ecstasy renderer

65 KB in the exe (58 KB relevant): Ported 100%, Partial 0%, Missing 0%, effective ≈ 100%.

File formats are fully ported (solids, TPK textures, streaming packs). The D3D renderer core is N/A (Unity URP/shaders). Missing: the particle emitter system (no smoke, sparks or NOS flames), which is the largest piece in this group.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Ecstasy/EmitterSystem.cpp | 0x4FDBD0-0x50D50D | 28,400 | 1,925 | Ported | EmitterSystem.cs, EmitterData.cs | EmitterSystem.cpp port (emitterdata/emittergroup attributes, 0x3BC00 placements) |
| Ecstasy/eLight.cpp | 0x4FC020-0x5078DD | 9,039 | 404 | Ported | CarRenderData.cs, CarEnvironment.cs | eDynamicLightContext rig tables (CarsInGame, BackRoom/CShop/QRace/Safehouse, CarLot, Characters, Default), modes 0-3, rig selection by game flow / garage type, NIS characters on the camera-space rig; dead/unread functions documented |
| Ecstasy/Texture.cpp | 0x4FCC40-0x510A40 | 8,100 | 760 | Ported | TexturePack.cs, TextureLibrary.cs | TPK 0x507B50, compressed blocks, scroll types |
| Ecstasy/eStreamingPack.cpp | 0x4FB0F0-0x508AD9 | 6,492 | 881 | Ported | TexturePack.cs, WorldStreamer.cs | streamed texture/solid packs |
| Ecstasy/eSolid.cpp | 0x4FB9A0-0x508B04 | 5,159 | 471 | Ported | SolidList.cs, MeshFactory.cs | solid loader 0x5075A0/0x5070E0 |
| Ecstasy/Ecstasy.cpp | 0x4FA940-0x504D76 | 4,848 | 254 | N/A |  | renderer core replaced by Unity (shaders re-authored in Shaders/) |
| Ecstasy/eModel.cpp | 0x4FB5F0-0x506C87 | 2,710 | 305 | Ported | MeshFactory.cs, CarBuilder.cs, CarRenderInfo.cs | [round 9] eSmoothNormals dead (SetSmoothVertex 0x6BE570 = ret on PC); rest plumbing |
| Ecstasy/eView.cpp | 0x4FCA60-0x506054 | 1,524 | 131 | N/A |  | renderer core replaced by Unity (shaders re-authored in Shaders/) |
| *1 smaller files* | | 261 | | N/A 1 | | |

### Frontend: HUD

61 KB in the exe (61 KB relevant): Ported 32%, Partial 68%, Missing 0%, effective ≈ 94%.

HUD package selection and the gauges are ported (`GameHud.cs`, 0x5694E0). The minimap works with fitted calibration. Pursuit/milestone boards, heat meter and race info are simplified. The leaderboard, radar detector and turbo meter are missing.

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Frontend/HUD/FEPkg_Hud.cpp | 0x567030-0x5A6E1E | 11,514 | 1,199 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | [round 9] ctor/SetHudFeatures/Determine/WideScreen/custom HUD exact; FadeAll caller [?], RaceOverMessage pending bit. ready: decomp source |
| Frontend/HUD/FeMinimap.cpp | 0x567480-0x5A653E | 8,437 | 583 | Ported | Minimap.cs | [round 9] 0x59DB50/0x579900/0x5675D0/0x579E00 |
| Frontend/HUD/FeHudElement.cpp | 0x564930-0x59C9F2 | 6,250 | 94 | Ported | HudFeatures.cs | [round 9] Register* + Toggle 0x5793F0 |
| Frontend/HUD/FePursuitBoard.cpp | 0x568C80-0x5A0059 | 5,557 | 390 | Ported | HudWidgets.cs, HudPursuitFeed.cs, GameHud.cs | [round 9] Update 0x57AEE0 + feeder 0x6F17F2 |
| Frontend/HUD/FeLeaderBoard.cpp | 0x568950-0x59F439 | 3,000 | 288 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | HUD runs on the FEng engine; DetermineHudFeatures/SetHudFeatures; settings applied. ready: decomp source |
| Frontend/HUD/FeMenuZoneTrigger.cpp | 0x568FC0-0x58C760 | 2,604 | 190 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | HUD runs on the FEng engine; DetermineHudFeatures/SetHudFeatures; settings applied. ready: decomp source |
| Frontend/HUD/FeMilestoneBoard.cpp | 0x57ADF0-0x59FC79 | 2,479 | 212 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | HUD runs on the FEng engine; DetermineHudFeatures/SetHudFeatures; settings applied. ready: decomp source |
| Frontend/HUD/FeRadarDetector.cpp | 0x566170-0x56669E | 1,937 | 157 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | HUD runs on the FEng engine; DetermineHudFeatures/SetHudFeatures; settings applied. partly decompiled (1/2 GC functions): RE the rest from speed.exe 0x566170-0x56669E |
| Frontend/HUD/FeCountdown.cpp | 0x567970-0x567F7B | 1,918 | 103 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | HUD runs on the FEng engine; DetermineHudFeatures/SetHudFeatures; settings applied. ready: decomp source |
| Frontend/HUD/FeRaceOverMessage.cpp | 0x568310-0x59E259 | 1,626 | 143 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | HUD runs on the FEng engine; DetermineHudFeatures/SetHudFeatures; settings applied. ready: decomp source |
| Frontend/HUD/FETachometerDrag.cpp | 0x57D130-0x5A137A | 1,626 | 91 | Partial | GameHud.cs, HudFeatures.cs, HudExtras.cs | HUD runs on the FEng engine; DetermineHudFeatures/SetHudFeatures; settings applied. ready: decomp source |
| *20 smaller files* | | 15,744 | | Partial 20 | | |

### Input

35 KB in the exe (11 KB relevant): Ported 100%, Partial 0%, Missing 0%, effective ≈ 100%.

DirectInput layer N/A; action mapping partial (fixed bindings in Unity).

| Source file | exe range (main cluster) | bytes | decomp lines | status | Unity | notes |
|---|---|---:|---:|---|---|---|
| Input/Common/iomodule.cpp | 0x6278F0-0x64D011 | 24,562 | 0 | N/A |  | DirectInput layer |
| Input/Common/actionqueue.cpp | 0x626D70-0x6490D1 | 8,657 | 317 | Ported | ActionQueue.cs | [round 9] PLUGGED/UNPLUGGED/FLUSH = 135/136/137 |
| Input/Common/InputDefParser.cpp | 0x647020-0x647D09 | 3,042 | 57 | Ported | ActionQueue.cs, InputActions.cs | [round 9] 0x6479D0 over the controller attributes |
| *2 smaller files* | | 0 | | N/A 2 | | |

## 3. What remains (prioritised)

Ordered by gameplay impact first, then by the amount of unported code (Σ bytes × (1 − effective fraction)). *Ready* means refs/nfsmw has real source for those files (≥ 60% of the GC function count, ≥ 40 lines). *Needs RE* gives the PC address ranges to decompile from speed.exe.

| # | Area | Unported code (est.) | Decomp lines available | Readiness | Why it matters |
|---|---|---:|---:|---|---|
| 1 | Cop AI and pursuit logic | 0 KB of 0 KB | 0 | ready (decomp source) | Pursuits are the core loop of MW. Unity's PursuitSystem diverges on every rule (pursuit.md §13, ai_cops.md §9). ai_cops.md §10 has the port plan. |
| 2 | Career/gameplay core (GManager, GRaceStatus, GActivity, GTrigger, GRaceDatabase, GIcon…) | 8 KB of 142 KB | 4 | needs RE for 142 KB (43% of it has PC/GC symbol names): Gameplay/GManager.cpp 0x5DD750-0x603720; Gameplay/GRaceDatabase.cpp 0x5D9580-0x603D4C; Gameplay/GRaceStatus.cpp 0x5DB170-0x6040A3; Gameplay/GActivity.cpp 0x5E77B0-0x60791A | Drives bins, milestones, race status, triggers, markers and unlocks. The Unity version is rebuilt from data and is approximate. |
| 3 | Traffic AI and the simplified vehicle models it uses | 0 KB of 0 KB | 0 | ready (decomp source) | Traffic density, the pattern mix, spawning, lanes and avoidance (traffic_collision.md A.10). Traffic currently runs on the full racer physics. |
| 4 | World collision packs (WCollisionMgr/Assets/Collider/Pack) | 0 KB of 0 KB | 0 | ready (decomp source) | Correct walls, invisible barriers and ground; removes render-mesh MeshColliders (traffic_collision.md B.5/B.6). |
| 5 | Helicopter (AI, physics, render, sound) | 1 KB of 5 KB | 279 | ready for 3 KB; needs RE for 2 KB (38% of it has PC/GC symbol names): Physics/Behaviors/DrawHeli.cpp 0x68C470-0x6BB30E | Heat 4+ pursuits (ai_cops.md §5). |
| 6 | Customization/shop rules + CarPartDB | 0 KB of 0 KB | 297 | ready (decomp source) | Prices, unlocks, cart/trade-in, heat from visuals, tuning sliders (customization.md §9). Data blocker: CarPartDB GlobalB 0x80034602. |
| 7 | Audio behaviour layer (sfxctl, carsfx, dynamic mixer, state managers) | 0 KB of 0 KB | 16 | needs RE for 0 KB (0% of it has PC/GC symbol names): EAXSound/sfxctl/SFXCTL_3DCarPos.cpp ; EAXSound/sfxctl/SFXCTL_Collision.cpp  | Engine/turbo/skid/collision mix, ambience, reverb, music transitions. Unity's current audio is minimal. |
| 8 | Speech AI (SoundAI, observers, flows) | 0 KB of 0 KB | 0 | ready (decomp source) | Police scanner dialogue and its context-dependent selection. |
| 9 | Front-end career screens (world map, SMS, bounty, markers, leaderboard, photo finish…) | 1 KB of 7 KB | 603 | ready for 4 KB; needs RE for 3 KB (94% of it has PC/GC symbol names): Frontend/HUD/FeRadarDetector.cpp 0x566170-0x56669E; Frontend/HUD/FeTurboMeter.cpp 0x57BB40-0x5A0529 | Spec'd in fe_career_screens.md and world_interaction.md. |
| 10 | Particles and world effects (emitters, physics effects, skids, rain) | 2 KB of 14 KB | 756 | ready (decomp source) | Visible polish: tyre smoke, sparks, NOS flames, skid marks, rain. |
| 11 | Car rendering and visual damage | 0 KB of 0 KB | 30 | needs RE for 0 KB (100% of it has PC/GC symbol names): World/DamageZones.cpp 0x768BB0-0x768C12 | Reflections, damage states, parts falling off, projected shadows. |
| 12 | Cameras beyond the chase cam (pursuit/track/rear-view/jump, camera AI, ICE manager) | 1 KB of 10 KB | 14 | needs RE for 10 KB (38% of it has PC/GC symbol names): Camera/Movers/SelectCar.cpp 0x46B7A0-0x47AFAF; Camera/Movers/Rearview.cpp 0x476630-0x47686C; Camera/Actions/CDActionTrackCar.cpp 0x485C70-0x485D35; Camera/Actions/CDActionTrackCop.cpp 0x485D40-0x485E05 | Pursuit cameras, rear-view mirror, jump cams, director switching. |
| 13 | NIS characters and world animation | 0 KB of 0 KB | 0 | ready (decomp source) | Cutscene characters (Razor, Mia, cops), NIS effects and events. |
| 14 | Lua game hooks (C++ side of the race/career scripts) | 2 KB of 30 KB | 0 | needs RE for 30 KB (47% of it has PC/GC symbol names): Lua/LuaGameHooks.cpp 0x6044C0-0x61E59B | Needed to run the scripts natively instead of hand-porting each stategraph. |
| 15 | PVehicle / sim surface / damage specs | 1 KB of 5 KB | 46 | ready (decomp source) | Vehicle object glue, per-surface grip, damage hit points. |

Per-file detail for every Partial/Missing row, including its `readiness` and exe range, is in `coverage.json`.

Largest single Missing/Partial files by unported bytes:

| Source file | status | unported (est.) | exe range | readiness |
|---|---|---:|---|---|
| Gameplay/GManager.cpp | Partial | 1,997 | 0x5DD750-0x603720 | needs RE from speed.exe 0x5DD750-0x603720 |
| Gameplay/GRaceStatus.cpp | Partial | 1,970 | 0x5DB170-0x6040A3 | needs RE from speed.exe 0x5DB170-0x6040A3 |
| Sim/Entities/LocalPlayer.cpp | Partial | 1,854 | 0x6E98C0-0x6FCC90 | ready: decomp source |
| World/rain.cpp | Partial | 1,639 | 0x73C5B0-0x758595 | ready: decomp source |
| Gameplay/GRaceDatabase.cpp | Partial | 1,556 | 0x5D9580-0x603D4C | needs RE from speed.exe 0x5D9580-0x603D4C |
| Lua/LuaGameHooks.cpp | Partial | 1,542 | 0x6044C0-0x61E59B | needs RE from speed.exe 0x6044C0-0x61E59B |
| Frontend/Database/FEDatabase.cpp | Partial | 1,530 | 0x57F4D0-0x5ACFB7 | ready: decomp source |
| Sim/Common/SimSurface.cpp | Partial | 1,222 | 0x4A9FB0-0x4AAAE5 | ready: decomp source |
| Sim/Activities/QuickGame.cpp | Partial | 1,220 | 0x6E9FA0-0x6F9B03 | ready: decomp source |
| Gameplay/GTrigger.cpp | Partial | 968 | 0x5DB470-0x601F77 | needs RE from speed.exe 0x5DB470-0x601F77 |
| Camera/Movers/SelectCar.cpp | Partial | 953 | 0x46B7A0-0x47AFAF | needs RE from speed.exe 0x46B7A0-0x47AFAF |
| Gameplay/GIcon.cpp | Partial | 935 | 0x5DE480-0x5ECCDD | needs RE from speed.exe 0x5DE480-0x5ECCDD |
| Lua/source/lapi.c | Partial | 926 | 0x6059E0-0x615320 | ready: decomp source |
| Sim/Activities/GameplayActivity.cpp | Partial | 854 | 0x6EAAD0-0x6FA4CC | ready: decomp source |
| Frontend/MenuScreens/Common/FEMenuScreen.cpp | Partial | 849 | 0x572360-0x59B3A1 | ready: decomp source |
| World/EventManager.cpp | Partial | 795 | 0x7256A0-0x72E551 | ready: decomp source |
| Gameplay/GActivity.cpp | Partial | 756 | 0x5E77B0-0x60791A | needs RE from speed.exe 0x5E77B0-0x60791A |
| Lua/LuaBindery.cpp | Partial | 697 | 0x6040B0-0x61F1FB | needs RE from speed.exe 0x6040B0-0x61F1FB |
| Lua/LuaRuntime.cpp | Partial | 656 | 0x609F20-0x61F54B | needs RE from speed.exe 0x609F20-0x61F54B |
| Libs/Support/Miscellaneous/Carp.cpp | Partial | 653 | 0x5CB930-0x5D7FD2 | needs RE from speed.exe 0x5CB930-0x5D7FD2 |
| Frontend/MenuScreens/Common/feArrayScrollerMenu.cpp | Partial | 626 | 0x573980-0x5A58B8 | ready: decomp source |
| Frontend/HUD/FEPkg_Hud.cpp | Partial | 576 | 0x567030-0x5A6E1E | ready: decomp source |
| Frontend/MenuScreens/Safehouse/FEPkg_GarageMain.cpp | Partial | 570 | 0x7A1C70-0x7BF1E7 | ready: decomp source |
| Misc/GameFlow.cpp | Partial | 565 | 0x64B020-0x667338 | ready: decomp source |
| Physics/Behaviors/SoundCar.cpp | Partial | 531 | 0x693EA0-0x6B50D5 | ready: decomp source |

## 4. Method

1. **Function list.** `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json` (an IDA export of this exact exe: same image base, `.text` 0x401000–0x890000) lists 46,360 functions with sizes, and 14,327 of them have names (MSVC-mangled, demangled, or GCC-style names carried over from the GameCube symbols). Spot checks against addresses cited in our code and specs all land on the expected functions (e.g. 0x47D3C0 CubicCameraMover::Update, 0x775BE0 WRoadNav::HolePunchAvoidables, 0x428720 AIActionRace::Update, 0x5694E0 HudResourceManager::GetHudFengName, 0x4E1070 GinsuSynthData::DecodeBlock). Ghidra/REA and the capstone scan were not needed, so the game exe was never run or modified.
2. **Coarse segmentation.** `splits.json` gives the PC link ranges of the 30 unity-build objects (`SourceLists/zAI.cpp` … `zWorld2.cpp`, 0x401000–0x789D88) plus 30 'unassigned' blocks (0x789EC0–0x88F12A: PC-only front end, online, CRT, D3DX imports, EA libraries, the VP6 codec at 0x7FB0F9–0x817130, which I identified from its strings, and static initialisers). The PC link order does not always follow these boundaries (EAGL4 code sits inside the zEAXSound range), so the final attribution relies on names.
3. **Function → source file.** The GameCube build's symbols (`config/GOWE69/symbols.txt`, 18,430 functions) were joined with its DWARF line table (`symbols/debug_lines.txt`), which gives each GC function's .cpp. That builds maps (class, method) → file, class → file and free function → file. Those maps, plus `Class::` definitions scanned from the decomp sources, resolve the PC names (31% of bytes by exact class+method, 2% by class, 5% free functions, 2.9% decomp class scan). Template instantiations and Attrib::Gen accessors are not used as anchors. Name rules add the PC-only buckets (online 2%, FLIRT-flagged CRT 4%).
4. **Unnamed functions** (51% of bytes) take the file of the neighbouring anchors. When the anchors before and after agree, that file is used (11%); otherwise the nearer anchor (39%). This works because MSVC emits a unity object's code in include order.
5. **Subsystem** comes from the source path (Src/AI, Src/Physics, …; GameCube platform files matched by PC renderer code count as Platform).
6. **Decomp inventory** (`refs/nfsmw/src`): per .cpp line count, function definitions and empty bodies. Many files are 1-line placeholders, notably all of Gameplay/, most of Camera/, Lua/LuaGameHooks, PVehicle, Bounds and Smackable. The JSON keeps `gc_functions` next to `decomp_functions` so the decomp completeness of each file can be read off. *Original source lines* is the highest line number per .cpp in the GC line table: a lower bound on the real file length.
7. **Unity status** was set per file by hand (`status_rules.py` in the scratch folder). Inputs: decomp file, class and function names, and exe addresses cited in `NFSMWUnity/Assets/NFSMW` (2336 distinct code addresses, landing in 446 original files); the deviation sections of `NFSMW_RE/specs/*.md` (physics §13, ai_racer §8, ai_cops §9, pursuit §13, races §11, traffic_collision A.10/B.6, customization §9); and reading the C# headers. A reader alone never made a file Ported: Ported needs the behaviour to be implemented and traceable.

## 5. Confidence and caveats

* **Function boundaries and totals: high.** They come from IDA's analysis of the same binary. A few hundred tiny thunks/funclets (SEH `Unwind@`, `$E` initialisers) are counted where they fall.
* **File attribution: medium-high.** 49% of bytes are anchored by name. Fill-ins can drift at file boundaries, and inline/template code is charged to whichever file it was emitted next to (e.g. `AttribInstance.cpp` and `_ios.c` collect scattered accessor/STL code). Small files can be over- or under-counted by a few KB. Subsystem totals are more reliable than single files.
* **Ported/Partial/Missing: medium.** This is judgement at file granularity, conservative by design. A Partial file can be 15% or 80% done, so read the 'effective' column as an estimate. Files are judged against the snapshot. `RoadNav.cs`/`AIRacer.cs` were being written during the scan.
* **N/A choices** that a reader might count differently: event plumbing (~100 KB), the Lua VM, Sim framework, FE memory-card/plumbing screens, the audio engine core (EAXSound.cpp, AEMS manager, streaming), EA online (~300 KB; LAN is a new implementation, not a port), and the D3D renderer. Counting all Online code as relevant-Missing would move overall Ported from 80.0% to 71.1%.
* **Decomp line metric** under-represents Gameplay, Camera and Lua, which have almost no decompiled code. Use the exe-bytes or original-lines rows for those.
* Not blocked: Ghidra/REA were not started because the symbol table made them unnecessary. No game files or refs were modified, and nothing was executed from them.

Scratch scripts (reproducible): `<coverage scripts>\` (gcmap.py → attrib_pc.py → fill.py → decomp_inv.py → build.py → report.py; status_rules.py holds the per-file judgements).
