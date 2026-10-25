# Misc: GameFlow, EasterEggs, Rumble, ActionQueue (NFS MW 2005 PC v1.3)

Recovered from speed.exe (capstone; REA/Ghidra where it knows the function), the IDA export
`refs/nfsmw/config/SPEED_EXE_1_3/symbols.json` and the GameCube decompilation (`refs/nfsmw/src/Speed/Indep/Src/Misc/GameFlow.cpp`,
`EasterEggs.cpp`, `Rumble.cpp`, `Input/Common/actionqueue.cpp` — these four have source; the PC differences are noted).
Guesses are marked [?], our own additions [ours]. Port: `Runtime/Game/GameFlow.cs`, `Runtime/Input/{ActionQueue,EasterEggs,Rumble}.cs`
(namespace `NfsMw.Controls` — `NfsMw.Input` would shadow `UnityEngine.Input` in every file of the project), `Runtime/GameBootstrap.cs`.

## 1. GameFlow.cpp (0x64B020-0x667338) — fully ported (the loading sequence as states; the file loads themselves are the port's streamer)

* **GameFlowManager** (`TheGameFlowManager`): `SetSingleFunction` / `SetWaitingForCallback` / `Service` (0x6596A0: run the queued
  single function until it stops re-queuing itself, then the looping function, then the demo-disc timeout) and the state
  (`GameFlowState`): NONE 0, LOADING_FRONTEND 1, UNLOADING_FRONTEND 2, IN_FRONTEND 3, LOADING_REGION 4, LOADING_TRACK 5,
  RACING 6, UNLOADING_TRACK 7, UNLOADING_REGION 8, EXIT_DEMO_DISC 9. `IsInFrontEnd` (state 3), `IsRacing` (6), `IsLoading`
  (0x50DE50: 4 or 5), `IsPaused` (0x64B680: not an online race and Sim::GetState() == STATE_IDLE).
* **Front end**: `LoadFrontend` (0x6645C0: SetLeakDetector, LoadFrontEndVault(false) — GLOBAL\FE_ATTRIB.BIN's `frontend` vault —
  then BeginGameFlowLoadingFrontEnd 0x664B80: state 1, TheRaceParameters.InitWithDefaults, ELoadingScreenOn(LS_LOADING_FE),
  CarLoader MODE_FRONT_END, FRONTB_VM, the language resources, FRONTA.BUN, FRONTB.LZC → GameFlowLoadingFrontEndPart1
  (0x662C00: the garage background GameFlowLoadGarageScreen 0x662B80) → Part2 (0x6597B0: LoadAemsFrontEnd) → Part3 (0x659770:
  LoadingTips::SetDoneLoading) → DelayWaitForLoadingScreen until the tips are done → EndGameFlowLoadingFrontEnd (eFixUpTables,
  the FE env map, car effects, garage car loaders, ELoadingScreenOff, FEManager::StartFE, state 3, StartSND11).
  `UnloadFrontend` → BeginGameFlowUnloadingFrontEnd (0x667010: state 2, StopSND11, StopFE, the FE resources, the FE vault,
  then `LoadTrack`).
* **Into the world**: `LoadTrack` (0x666FE0) → BeginGameFlowLoadRegion (0x666F90) → RegionLoader::BeginLoading (state 4,
  TrackInfo of TheRaceParameters.TrackNumber, InGameMemoryFile, CodeOverlayLoadingGame, WWorld::Init, the car loader pool —
  3500 KB single / 1750 KB split screen, 1850 for a split-screen drag, −256 with EmergencySaveMemory — ELoadingScreenOn
  LS_LOADING_GAME_FROM_FE) → LoadHandler phases 1 InGameA.bun + INGAMEB.LZC (+ InGameSplitScreen.bun), 2 TRACKS\<region>.BUN
  (+ the time-of-day suffix file on platforms that need one), 3 the preculler scripts + LoadAemsInGame, 4 InitSkyHash, 5
  FinishedLoading: TheTrackStreamer.InitRegion(TRACKS\STREAM<region>.BUN) → BeginGameFlowLoadTrack → TrackLoader (state 5,
  InitWorldModels) → **BeginWorldLoad** (0x???: pops loading_boot.fng under SkipFE, eFixUpTables, InitTopologyAndSceneryGroups
  — the SCENERY_GROUP_DOOR barrier group and CloseAllGarageDoors —, SunTrackLoader, ActivateAnyRenderEggs, the World/Render/
  Sound services, StartSND11, StartWorldAnimations, `new World`, **Sim::Init(activity, mode)** with activity "OnlineGame" /
  "QuickGame" / "CareerGame" (career and not SkipFE and not split screen) and CalculateSimMode (USER_SPLIT_SCREEN from
  SkipFESplitScreen or FEDatabase::IsSplitScreenMode), OpenVisualTreatment HEAT_LOOK) → WaitForSimulation (Sim::GetState() ≥ 2)
  → **FinishedGameLoading** (state 6 RACING, the car loader drained, the memory file freed, Scheduler::Synchronize, the
  loading screen popped if in control, IHud::JoyEnable for every HUD, ActivateMemorySponge).
* **Out of the world**: `UnloadTrack` (0x66????) = BeginGameFlowUnloadTrack(0), `ReloadTrack` = (1): TrackLoader::Unload (state 7:
  SoundPause off, SNDSTATE_OFF, the FE package popped, DeactivateMemorySponge, StopSND11, the world anim scene, FE_LOOK,
  CloseVisualTreatment, the online race ended, **Sim::Shutdown**, the services restored, `delete pCurrentWorld`, the car loader,
  the world models, the sun, the sky, the barriers / scenery groups off, CloseSound, TheTrackPathManager.Close, **SkipFE = 0,
  bDefaultSeed logged to the Joylog, ResetRenderEggs and gEasterEggs.ClearNonPersistent** (not in the AI/replay demo modes))
  then RegionLoader::Unload (state 8: the streamer hibernated and unloaded, gEmitterSystem.KillEverything, WWorld closed, sound
  loads drained, the HUD texture packs and the region/InGame bundles unloaded, TrackInfo 0, CheckLeakDetector) → destination 0:
  LoadFrontend, 1: LoadTrack.
* **The loading bar** (HandleTrackStreamerLoadingBar, main loop 0x64B770): in Sim STATE_ACTIVE, TheTrackStreamer.CheckLoadingBar
  turning on → FEManager::RequestPauseSimulation("TrackStreamerLoadingBar") + EFadeScreenOn(true); off → EFadeScreenOff(FEHASH_HIDE
  0x16A259) + RequestUnPauseSimulation (ported in round 10: GameFlow.OnLoadingBar).
* `EnableBarrierSceneryGroup(name, flip)`: VisibleSectionManager::EnableGroup(bStringHash(name)), EnceneryGroup, and
  TheTrackPathManager.EnableBarriers(name) — the career's barrier groups (GameBootstrap.RefreshBarriers / WorldStreamer).
  `RedoTopologyAndSceneryGroups` = close + init again. `GlobalChunks`: GLOBAL\ATTRIBUTES.BIN's `db` vault, GLOBALB.LZC, the brake
  and wheel packs, DYNTEX.BIN, the language, the wide/thin screen bundle, the car and spoiler packs, the track streamer pool
  (0x4CF400 + the car loader's 3500 KB), then FE_ATTRIB's vault loaded high.
* **Port** (GameFlow.cs): `GameFlow.Flow` follows GameFlowState — BeginLoad: InFrontend → UnloadingFrontend → LoadingRegion →
  LoadingTrack; `LoadState.Ready` (FinishedGameLoading) → Racing; EndGameplay (QuitToMainMenu / the D-Day car lot's EQuitToFE) →
  UnloadingTrack (EasterEggs.ClearNonPersistent) → UnloadingRegion → LoadingFrontend → InFrontend. `IsInFrontEnd / IsRacing /
  IsLoading / IsPaused`. The QuickGame::eState loading states (lua_hooks.md §4.3) stay as `GameFlow.State`. The world stays
  loaded between a career race and free roam (no RegionLoader per engage) [ours, as before]. CodeOverlay, memory sponge, leak
  detector, memory files, Joylog: not applicable.

## 2. EasterEggs.cpp (0x652F60-0x65E075) — fully ported

* **PC data** (ctor sub_652F70): `EasterEggsTable` = 0x8F8520, **2 entries** of 0x34 bytes `{char code[32], u32 type, item,
  group, unlock_message, u8 unlocked, persistent, enabled}`: `"burgerking"` → type 3 EASTER_EGG_BURGER_KING and `"castrol"` → 4
  EASTER_EGG_CASTROL, both persistent = enabled = 1. (The GC build has two 8-button pad sequences for types 3 / 4 instead; the PC
  ring buffer grew to 32 slots: `+0x18 char ring[32]`, `+0x38 count`, `+0x3C start`.)
* **Where** the codes are typed: `Activate` (0x65C210) from the SplashScreen ctor (0x5A31D0) — the press-start screen —,
  `UnActivate` (0x65C2A0) from its dtor (0x57DC62); `KeyboardInput` (0x65D9E0) from KeyboardEditString::NotifyChar (0x5A8768).
  Activate makes two `fe`-config ActionQueues ("movie q", ports 0 / 1) and enables them.
* **KeyboardInput(c)**: with the buttons enabled: `ring[count & 31] = c; count++`; then for every entry with `strlen(code) <=
  count` whose code equals the last strlen(code) characters of the ring (`ring[(count − n + i) & 31]`) → ActivateEasterEgg.
* **HandleJoy** (0x65C410): every queued `fe` action with value 1.0 goes into the ring as a button id while count < 32; at 32 the
  32 buttons are compared with each entry's `code[32]`; no match → start++, count−− (the sequence slides). No PC entry is a
  32-button sequence, so pads cannot enter a code.
* **ActivateEasterEgg(egg)** (0x65C2E0): if enabled: ClearGroup(group), unlocked = 1, HaveUnlockMessage, UnlockMessage, the ring
  cleared, `PlayUISoundFX(UISND_COMMON_SELECT 4)`; the first time only, by type: 0 UnlockAllThings (DAT_00926124 = 1: GRaceParameters::
  GetIsAvailable answers yes for every race), 1 SkipDDayRaces + SkipCareerIntro (DAT_00926126 / DAT_00926125), 2 MikeMannBuild = 0
  (DAT_0092612C), **3 cFEng::QueueGameMessage(FEHASH_BK_UNLOCK 0x98257537)**, **4 QueueGameMessage(FEHASH_CASTROL_UNLOCK 0x6521E5C2)
  + FEPlayerCarDB::AwardBonusCars (0x56F0C0)**, 5 QueueGameMessage(FEHASH_DEMO_CHEAT 0xA6813B08) + CareerSettings::TryAwardDemoMarker
  (0x57F530). `ClearNonPersistent` (0x652FB0) at TrackLoader::Unload; `IsEasterEggUnlocked(type, item)` (0x653050) / (Special)
  (0x6530A0); `ClearGroup` (0x6530B0). The GC's TriggerSpecial / IsStockCarTypeUnlocked / IsPresetUnlocked / … are stripped.
* **Port** (`Runtime/Input/EasterEggs.cs`): the table, ring, KeyboardInput, HandleJoy, ActivateEasterEgg, ClearNonPersistent,
  IsEasterEggUnlocked; the switches `EasterEggs.UnlockAllThings` (RaceDatabase.IsAvailable), `SkipCareerIntro` (LuaGameHost's
  Game.SkipCareerIntro), `SkipDDayRaces`, `MikeMannBuild`. GameBootstrap activates the cheats while the FE's top screen is the
  PressStartScreen and feeds Keyboard.onTextInput (lower-cased) to KeyboardInput; `EasterEggs.Activated(type, firstTime)` is the
  hook for the FE messages (FEHASH_BK_UNLOCK: the Burger King challenge; the FE agent's); Castrol → `GameSession.AwardBonusCars`
  (the CASTROLGT preset in Profile.BonusCars — the car select's HasBeenAwardedCastrolGT) [?: the other presets AwardBonusCars marks].
  The UI select sound is a hook (no FE sound API in the port's gameplay side).

## 3. Rumble.cpp (0x64BB70-0x64C740) — patterns and camera shake ported; the PC wheel-FFB device maths is not

* **Camera shakers** (PC has them: tShaker::GetValue 0x64C270, UpdateCameraShakers 0x64C2D0 from Camera::UpdateAll 0x480C1C,
  ResetCameraShakers 0x64C370 from World_Init 0x733A6A and ERestartRace 0x63094F, ApplyCameraShake 0x64C420 from
  RearViewMirrorCameraMover::Update 0x47684C and CubicCameraMover::Update 0x47DCCD, MaybeCameraShake 0x64C5F0 from
  CDActionDrive::Update 0x473089 / 0x473189). tShaker {vShake, pAmplitude (the ShakeAmplitude envelope {0:1, 0.1:0.7, 0.3:0.5,
  0.5:0.4, 1:0}), fTime, fDuration, fFrequency}: StartShaking(v, dur, freq): vShake = −v; Update: t += dt / dur until out of
  range; GetValue: amplitude only when `((int)(t·f) ^ 1) & 1` (the odd half-cycles) × vShake. MaybeCameraShake(player, accel):
  a = bias(0.7, 0.7, 1)·accel; |a| below 100 → nothing; ratio = clamp((|a| − 100) / 500); magnitude = 0.1 + 0.3·ratio; if above
  the current amplitude: duration = 0.3 + 0.4·ratio, shake = normalize(a)·magnitude at 11.74265 Hz. ForceCameraShake: 0.6 s.
  ApplyCameraShake(viewID, matrix): nothing when Sim::GetSpeed() < 1 (the speedbreaker); view 1 / 3 → player 0, 2 → player 1;
  the shake vector transformed by the view matrix becomes a translation, then RotateX(−1200·(z + y)), RotateY(2000·x),
  RotateZ(0) in bAngle units (65536 = a turn), multiplied into the view matrix.
* **Rumble envelopes** (255 = full motor): SmallRumble {0:255, 0.05:200, 0.07:80, 0.8:0}, BigRumble {0:255, 0.15:255, 0.2:120,
  1.2:0}, ShockSway {0:55, 0.05:15, 0.1:0}, RoadNoise0/1 {0:80, 0.05:15, 0.1:0}, Binary {0:255, 0.15:255}, SingleRoadBump
  {0:140, 0.07:0}, DoubleRoadBump {0:255, 0.05:0, 0.07:255, 0.15:0}, GearGrind {0:255, 0.075:20, 0.2:0}, EngineHeat {0:45,
  0.25:35}, EngineRev {0:45, 0.01:1}, NOS {0:70, 1:1}, Drift {0:45, 0.01:0}, Burnout {0:55, 0.01:15, 0.05:0}. Intensity tables
  {in lo, in hi, amp lo, amp hi}: ShockSway {9, 40, 0.95, 1.5}, Drift {5, 15, 2, 2.5}, RoadNoise {0.001, 0.025, 1.1, 1.5, 25, 85},
  ShockBottom {1, 3, 1, 2}, Impact {2, 11, 0.3, 2}, BlownTires {2, 11, 0.3, 2}, RoadBump {0, 1, 0.3, 2}, GearGrind {0, 1, 0.3, 3},
  EngineHeat {0, 1, 1.5, 3}, EngineRev {0.3, 0.9, 1.2, 2.75}, NOS {0, 1, 1, 1.9}, Burnout {1, 10, 1.15, 1.9}. tRumbler (0x30: 3
  envelope slots {envelope, time, amplitude, priority}, value, previous value): StartRumble / Update / GetEnvelopeValue are
  stripped on GC and absent on PC — [?] reconstructed as: a free slot or the lowest-priority slot not above the new priority;
  value = max over slots of amplitude·envelope(t), clamped 0..255; a slot ends when its envelope runs out.
* **PC force feedback**: LocalPlayer::DoFFB (0x6EE490, 0x3C1 bytes) when CanDoFFB (0x6EE400: FFB enabled DAT_008F86E0 and a
  device DAT_0092C678, not in a NIS, the vehicle not animating): each frame it feeds the IFeedback device (+0x58, a vtable of
  ~14 entries) from the vehicle: per front/rear axle the wheel slip and load (vtable +0x1C / +0x20), the road surface (+0x18),
  the engine's rpm ratio `clamp((rpm − idle) / (redline − idle))` (+0x24), the gear (+0x28), the NOS (+0x2C), the collision /
  handbrake state (+0x30, +0x34), then Commit (+0x14); PauseFFB (0x6E8B20). The device class is the DirectInput steering-wheel
  driver's and its force equations were not decompiled [?]. The ecar attributes EngineVibrationFreq / Min / Max (0x732970..) are
  its engine channel.
* **Port** (`Runtime/Input/Rumble.cs`): `Envelope`, `RumblePatterns` (all tables), `Rumbler`, `CameraShaker` (Shakers[2],
  Update/Reset/MaybeCameraShake/ForceCameraShake, `ApplyCameraShake(viewId, out offset, out eulerDeg)` for the camera code to
  apply — hook; GameBootstrap.EnterWorld and RaceManager.RestartRace call ResetCameraShakers), `RumbleDriver` (a hidden
  MonoBehaviour from GameBootstrap): each frame the shakers tick, MaybeCameraShake runs on the player's acceleration, and the
  car's state starts patterns — impact = the speed lost in a frame against ImpactCoeffs (Big above 6 m/s, else Small; priority
  3), landing → ShockSway by the vertical speed lost (ShockBottomCoeffs), NOS firing → NOS by the bottle level, a missed shift →
  GearGrind, the limiter (OverRev, rpm > 0.9 redline) → EngineRev, EngineHeat > 0.5 → EngineHeat, the drift state → Drift by the
  slip angle (DriftCoeffs), wheel spin under 8 m/s → Burnout by the slip ratio, blown tires → SingleRoadBump once per wheel turn
  (BlownTiresCoeffs). Output: Gamepad.current.SetMotorSpeeds(v/255, v/255) [ours: both motors], only while FEPlayerSettings.Rumble
  (Player1) is on and the sim is not paused. The mapping of car state to the patterns is [ours] (DoFFB's channels are the
  wheel's); RoadNoise, Binary and DoubleRoadBump have no trigger yet.

## 4. Input/Common/ActionQueue.cpp (0x642C90..0x648DB0) — fully ported (as a module; the drive input still reads the devices directly)

* **ActionQueue** (0x294 bytes: UCircularQueue<ActionData, 50> at +4, port +0x26C, state +0x270 (0 disabled, 1 enabled), the
  InputMapping +0x274, config hash +0x278, name +0x27C, unique id +0x280, connected +0x284, required +0x288, the action and
  activation timers). Ctors 0x643FC0 (required) / 0x648DB0 (port, config, name, required → SetConfig → Init). `Init(port,
  config)` (0x648040): nothing when both unchanged; else the mapping is rebuilt from IOModule::GetDevice(port) and the
  `controller` collection of the config (InputMapping ctor 0x6479D0). `Enable(b)` (0x644060): a state change flushes.
  `IO_SetConnected` → ACTION_PLUGGED / ACTION_UNPLUGGED actions, `IO_Flush` → ACTION_FLUSH. `ReceiveAction` (0x6440B0): only for
  its port; everything but UNPLUGGED updates the static LastAnyActionTime; UNPLUGGED / PLUGGED flush and set the connection,
  FLUSH flushes; the action time, then enqueue (the Joylog records the triple). `GetAction` (0x633DC0) = the oldest, `PopAction`
  (0x633D90), `IsEmpty` (0x633D80), `Flush` (0x642D70: the queue reset and every mapping entry's Previous / Current = −1).
  `FindActionQueue(id)` / `AssignUniqueID` (0x633CD0: the first free id), `OnActivationChange` (0x633D10: the activation timer
  set when enabled + connected + valid). BeginJoylogFrame / EndJoylogFrame (0x644180) replay / record the actions.
* **InputMapEntry** (list node; +0x8 type, +0xC min / threshold, +0x10 max, +0x14 action id, +0x18 device scalar index, +0x1C
  PreviousValue, +0x20 CurrentValue). **FetchCurrentValues** (0x642C90): a missing scalar → Previous = Current = −1; by type
  (jump table 0x642D50): 0 → `Ramp(value, min, 1 − max)` (analog); 1, 2 → `value < 0.5 ? 0 : 1` (digital); 3, 4 → `value < min ? 0
  : 1` (threshold). **IO_UpdateFromDevice** (0x6448E0): for every entry with Current ≥ 0 (known) and |Current − Previous| > 1e-6:
  an analog entry (type 0) is skipped when another type-0 entry of the same action has Current ≥ its own (two axes on one action);
  then by type (jump table 0x644A78): 0 fires on every change, **1 / 3 on the press edge** (Current ≥ 0.5, Previous < 0.5, Previous
  ≥ 0), **2 / 4 on the release edge** (Current < 0.5, Previous ≥ 0.5); a non-zero action id is queued as ActionData(id, Current,
  the device's port (+0x24)); Previous = Current.
* The `fe` config's action ids on PC (EasterEggs.hpp's EasterEggButtons): UP 25, DN 26, LT 27, RT 28, TR 34, R2 38, L2 40, SQ 41,
  L1 43, R1 44.
* **Port** (`Runtime/Input/ActionQueue.cs`): ActionData, InputMapEntry (the five types), InputMapping, ActionQueue (all of the
  above; PLUGGED / UNPLUGGED / FLUSH = 135 / 136 / 137, §6.1), `IInputDevice` + `UnityInputDevice` (port 0 = keyboard + the first
  pad; the Key enum names, PAD_* names and the PC device's logical scalars are the scalar names), `IOModule` (Initialize, GetDevice,
  Update = poll + plugged changes + every queue's IO_UpdateFromDevice; the mappings are InputDefParser's over the `controller`
  attribute, §6.2).
  GameBootstrap runs IOModule.Update every frame, owns the `hud` queue on port 0 and drains it into `ActionPressed(action)`:
  HUDACTION_PAUSEREQUEST opens the pause menu (the raw Escape key only when the mapping is missing), HUDACTION_ENGAGE_EVENT is
  EventMarkers' engage. The drive input (PlayerCar.DriveInput.FromDevices, the physics owner's) still reads the devices directly.

## 5. Not verified / open
* tRumbler's slot and value rules, and which car events start which pattern on PC (DoFFB drives a wheel, not a pad).
* ~~The PC action ids beyond the `fe` ten; ACTION_PLUGGED / UNPLUGGED / FLUSH's numbers~~ — resolved (§6.1).
* EasterEggs: what FEHASH_BK_UNLOCK / CASTROL_UNLOCK do in the FE (the BK challenge's availability; the car select's bonus list),
  AwardBonusCars' full list, and whether the unlock message (unlock_message = 0 on both) is shown.
* GameFlow: the exact functions behind `UnloadTrack` / `ReloadTrack` on PC were not located by name (the GC source names them).

## 6. Round 9 (final): InputDefParser, the ActionID table, SimRandom, LocalPlayer::DoFFB, GameFlow / QuickGame leftovers

### 6.1 The ActionID table (getActionIdString; resolves §5's ids)
StringToNumber instance 0x9203C8 filled from the {id, char*} pairs at **0x8F6728** (138 entries, ids 0..137 in order): NULL_ACTION 0,
GAMEACTION_STEERLEFT 1 … GAMEACTION_RESET 12, FORWARD / BACK / JUMP 13-15, DEBUGHUMAN1-4 16-19, CAMERAACTION_CHANGE 20 / DEBUG 21 /
ENABLE_ICE 22 / LOOKBACK 23 / PULLBACK 24, FRONTENDACTION_UP 25 … RTRIGGER 44 (so EasterEggs' UP 25 … R1 44 are these: TR 34 =
FRONTENDACTION_CANCEL, R2 38 = BUTTON1, L2 40 = BUTTON3, SQ 41 = BUTTON4, L1 43 / R1 44 = LTRIGGER / RTRIGGER), HUDACTION_PAUSEREQUEST
45 … HUDACTION_NEXTSONG 51, VOIPACTION_PUSHTOTALK 52, DEBUGACTION_* 53-80, ICEACTION_* 81-133, ACTION_EXITAPPLICATION 134,
**ACTION_PLUGGED 135, ACTION_UNPLUGGED 136, ACTION_FLUSH 137**. Port: `Core/InputActions.cs` (Names, IdOf); ActionQueue's constants.

### 6.2 InputDefParser (InputMapping::InputMapping 0x6479D0, InputDefParser.cpp:9-54) — ported
For action id 0..137 with a name: `Attrib::Gen::controller(spec).Get(StringToKey(name))`, each **ControllerDataRecord (32 bytes)**
{StringKey mDeviceID +0 (hash64 +0, hash32 +8, char* +0xC), mUpdateType +0x10, mLowerDZ +0x14, mUpperDZ +0x18}; an empty device id
is skipped; the device scalar whose DeviceScalarName equals hash32 (linear scan of GetNumDeviceScalar, vtable +0x14, 16-byte scalars
at +0x18) becomes an InputMapEntry {action, type, LowerDZ, UpperDZ, scalar index, Previous = Current = −1} (list push_back 0x635250 /
_Incsize 0x645F10). The `controller` class (attributes.bin; harness `r9gp controller`): `fe` (FRONTENDACTION_* from FE_ACTION_UP /
DOWN / LEFT / RIGHT / ACCEPT / CANCEL / B0..B5 / START / LTRIG / RTRIG, type 0; START from FE_ACTION_B3 and BUTTON3 from
FE_ACTION_START), `hud` (HUDACTION_PAUSEREQUEST / ENGAGE_EVENT / PAD_LEFT / PAD_DOWN / PAD_RIGHT / NEXTSONG from the scalars of the
same name, SKIPNIS from FE_ACTION_ACCEPT, type 1), `drive` and its children drive2..drive5 (GAMEACTION_* from GAME_ACTION_* and
SC_LEFT / SC_RIGHT / SC_UP / SC_DOWN for steer / turn / gas / brake; CAMERAACTION_CHANGE / LOOKBACK / PULLBACK from CAM_ACTION_*),
the `_analog` variants, `fly`, `tedebugmenu` (type 3, dead zone 0.1), `ice`, `debug`, `renderdebug`, `roadeditor`, `voip`, `human`.
So the PC device's scalars are **logical**: the FE controls page's action names (GAME_ACTION_GAS …, HUDACTION_*, FE_ACTION_B2,
CAM_ACTION_*: the binding table 0x91F418), the FE menu actions and DIK scan codes (SC_*). Port: `IOModule.BuildMapping` reads the
collection exactly so; `UnityInputDevice` adds those logical scalars (a key bound on the FE controls page, read live through
ControllerScreen.KeyOf; FE_ACTION_* = arrows / Enter / Escape and the pad as EasterEggs' button names say; SC_* = the Key of the DIK
name) [ours: the pad side of the logical scalars, the exe's joystick page being per device]. Game action ids are now the exe's
(`GameActionId` = the table index; the earlier `100 + row` scheme is gone).

### 6.3 SimRandom (Math/Common/SimRandom.cpp) — ported (`Core/Sim/SimRandom.cs`)
ctor 0x6272F0 (randSeed 123456789), Reset 0x633E70 (fastRandom 0xF874AF01, 39 dry draws), SimRandom_Generate 0x627320, the inlined
_SimRandom_Float 0x402850 (r / 65536), _SimRandom_FloatRange 0x402870 (r · range / 65536), _SimRandom_IntRange 0x402890 ((range · r)
>> 16); Sim::GetRandom 0x6ED200 (static at 0x988F0C); Reset by Sim::Init 0x6FC417 and Sim::Shutdown 0x6FABD2 (GameFlow.BeginLoad /
EndGameplay). **Every consumer** (call xrefs of 0x627320 / 0x402870 / 0x6ED200) and its port site (one-line hooks in the police
owner's files and SpeechManager):

| exe | use | port |
|---|---|---|
| AICopManager rand_point_in_circle 0x406340 (from SpawnPursuitIVehicle 0x4312DE) | angle FloatRange(2π), radius sqrt(FloatRange(1)) | PursuitSystem.Script.cs RandPointInCircle (PursuitSpawnLocation's 190 m circle) |
| SpawnPursuitIVehicle 0x430EB2 / 0x43101E | an unused Float() at entry; Float() > 0.5 → −60° | the side choice (the unused draw is not made) |
| CreateRoadBlock 0x43E17C | spikes = FloatRange(100) < roadblockspikechance | PursuitSystem.cs `spikes` |
| GetHeavySupportVehicles 0x41F1AB | IntRange(100) < ChanceBigSUV | both copsuv picks |
| AIPursuit::RequestRoadBlock 0x419541 / 0x4195BD | timer FloatRange(4) + 8; FloatRange(100) < probability | `_roadBlockTimer`, `_nextRoadblockRequest` |
| RequestGroundSupport 0x41970F / 0x419794 | IntRange(100) twice | the leader / heavy support rolls |
| RemoveUnwantedVehicles 0x42B954 | Float() ≥ typecount / (typecount + flee) | PursuitSystem / RacerPursuit flee type |
| CopRequest 0x42BB1F / 0x42BC83 | FloatRange(100) ≤ SearchModeHeliSpawnChance; IntRange(totalWeight) | heli search spawn, the weighted type (also RacerPursuit) |
| OnTask 0x444046 / 0x4440C4 | formation FloatRange(sumWeight); FloatRange(3) + duration | PursuitFormationState |
| GetRandomValidCopCar 0x419B75 | IntRange(total count) | RandomCopType |
| AISpawnManager::GetSpawnLocation 0x42FA75 / GetSpawnPointOnSegment 0x419F59 | IntRange(segments); Float() along the segment | PatrolSpawnLocation |
| AIActionHeliPursuit::SetNextPerpSearchDest 0x412F96 | FloatRange(0.7 r) + 0.2 r | Helicopter.NextSearchDest |
| Speech::Manager::CanPlayback 0x704535 | FloatRange(1) ≤ the event's probability | SpeechManager.Rand |

Nothing else draws from it; bRandom (0x45DA30, gameplay_core.md §16.7) and UnityEngine.Random stay where the exe uses bRandom or the
port has its own [ours] choices (radio chatter, RoadNavCop's decisions). Deviation: the port's draw order follows its own frame order,
so the sequence is the exe's generator but not the exe's exact interleaving.

### 6.4 LocalPlayer::DoFFB (0x6EE490) / CanDoFFB (0x6EE400) — ported as far as a device exists
OnTask (0x6F2270: UpdateHud, DoGameBreaker, DoFFB). DoFFB: no device (+0x58) → return; CanDoFFB false → device +4 (pause); an
override object (+0x5C, vtable +0xC) takes over; IVehicle / ISuspension / IEngine / ITransmission required; device +8, +0x10; per
wheel i (ISuspension +8 count): surface = airborne (ISuspension +0x24 false) SimSurface kNull 0x988E80, a punctured tyre (ISpikeable
+4(i)) SimSurface(0xD929E923), else ISuspension +0x44(i); |ISuspension +0x30(i)| and |+0x38(i)| (UMath::Abs 0x401D10); device +0x20
(front = i < 2, surface, transmission), +0x1C (front, {slip, skid}, suspension), +0x18 (transmission, surface, IVehicle +0x98 speed);
then IEngine idle (+0x10) / rpm (+4) / redline (+8): ratio = clamp((rpm − idle) / (redline − idle), 0, 1) when the range is > 0
(0x890968 = 0) → device +0x24(ratio, 0, engine info +0x14); device +0x28(ITransmission +0x2C gear); IEngine +0x2C (has NOS) → device
+0x2C(IEngine +0x20(IEngine +0x1C)); IEngineDamage present → device +0x30(blown +4 || sabotaged +0x10), else device +0x34(IVehicle
+0x10 && IVehicle +0x90 speed > 0.1 (0x890CD4)); device +0x14 commit. CanDoFFB: DAT_008F86E0 (FFB on, 1), the device list 0x92C678
with no running NIS (INIS vtable +8), the player's simable, its IAnimatable +0xC false, IVehicle +0xBC false. LocalPlayer::OnCollision
(0x6F2370): FFB on and a device → device +0x38(collision info, the other object is mine). Port (`Runtime/Input/Rumble.cs`):
`IFfbDevice` (the slots), `ForceFeedback.CanDoFFB / DoFFB` from RumbleDriver.Update. **Deviation by design:** Unity's Input System
has no force-feedback wheel effects (no DirectInput), so `ForceFeedback.Device` is null and DoFFB returns at the first test exactly as
the exe does without a wheel; the pad rumble stays RumbleDriver's (§3). OnCollision's channel is not forwarded (no device).

### 6.5 GameFlow.cpp / QuickGame / GameplayActivity / CareerGame leftovers
* GameFlow.cpp range 0x64B020-0x64B6C0: sub_64B070..sub_64B5F0 and the nullsubs are the RegionLoader / TrackLoader callback stubs;
  HighAttribAlloc::Allocate / Free (0x64B610 / 0x64B640) are the attribute heap (C# GC); MaybeDoMemoryProfile (0x64B380) is empty;
  RegionLoader::LoadHandler (0x64B390) forwards the bundle loader callback — the port's streamer loads files itself (§1).
* QuickGame::OnManageState (0x6EF150) — the sim runs only when: not 0x911092, no in-game movie (InGameAnyMovieScreen::IsPlaying),
  no NIS or the NIS loaded and not holding (INIS +0x38 / +0x40), GRaceStatus::IsLoading false (0x5ED880), no pending warp (GManager
  +0x2B4), FEManager::ShouldPauseSimulation(true) false. Port: the FE pause (time scale 0), RequestPauseSimulation (the loading bar),
  NisPlayer's own hold and StoryCard's movies cover the same cases [the warp hold is not modelled: PlacePlayer is synchronous].
  ShouldPauseInput (0x6EED40): input paused unless the game state (0x9885E0 +0x2C) is 3 and an NIS is up — the HUD queue is drained
  only while the FE is closed (GameBootstrap). OnTask (0x6E9FF0) = "this task is mine". OnQueryVehicleCache (0x6EED60) keeps every
  vehicle a player drives (the car loader's cache; Unity keeps loaded cars). OnRemovedVehicleCache (0x6EA000) is empty; InGameBreaker
  (0x6F88E0) returns the member.
* QuickGame::HandleSkipFEOptions (0x6EA040, only from OnBeginState 0x6F4ED3): the SkipFE debug options (SkipFE = 0 in retail, set
  only from the debug command line) — dead in retail, not ported.

## 7. Round 9: Libs/Support/Miscellaneous/Carp.cpp — the non-road remainder (0x5CB930-0x5D7FD2)
The road half (USpline, the CARP reader's UGroup DataLocateTag / DataLocateFirst / DataCountType / GroupLocateTag 0x5CF0D0 / 0x5CEFE0 /
0x5CEF70 / 0x5CED30) is Core/RoadNetwork.cs (round 4). The rest of the range, by what it does:
* **hash32** (0x5CC090) = Bob Jenkins' lookup2 over (data, length, init in edx); **stringhash32** (0x5CC240) = hash32(s, strlen, 0xABCDEF00),
  0 for a null or empty string; sub_5CC270 / sub_5CC2A0 the (buffer, length[, init]) forms. Ported as `AttribDatabase.JenkinsHash` /
  `Hash` (Core/Attrib.cs: same mix, same init) — every attribute key, collection name and the event-sequencer / stimulus hashes the port
  computes go through it. [deviation: the C# Hash("") hashes the empty string instead of returning 0; no shipped data hashes "".]
* **FastMem** (Init 0x5D0240, CreateBlock 0x5D0450, AssignToFree 0x5D03A0, Alloc 0x5D29D0, Free 0x5D0370): the small-object pools
  (GIcon, events, SimRandom's FASTALLOC) — replaced by the C# allocator / GC (no game-visible effect; AllocIcon's 200 limit is kept as
  GameIcons.MaxIcons).
* **VU0 / UMath helpers**: BuildRotate 0x5CFA60 (CarRenderConn::BuildRenderMatrix 0x73929C), VU0_m4toquat 0x5D06A0 (RigidBody::
  SetOrientation 0x696DA1), VU0_Matrix4ToEuler 0x5D0A70, VU0_v3unitcrossprod 0x5D3BC0 (Chassis::DoJumpStabilizer 0x6A925D..), UBezierLite::
  Evaluate 0x5CE2B0 — matrix / quaternion / Euler conversions replaced by UnityEngine.Matrix4x4 / Quaternion in the physics and render
  code that needs them (the owners' files; the conversions are standard and exact up to float rounding).
* **CARP tag resolvers**: InitResolvers 0x5D79B0 / AddUDataTagResolver 0x5D77C0 (registered by every event class, sub_621xxx-0x625xxx),
  TagReference ctor 0x5D0DF0, EventListResolver 0x5CBBF0, StimulusFilterResolver 0x5CBC40, EventSeqEngine / System / State / Action
  Resolver 0x5D2F40 / 0x5D2FF0 / 0x5D3080 / 0x5D3130, CollisionInstanceResolver 0x5D31C0: at load they turn the packs' {tag, hash}
  references into pointers. The port's readers resolve by lookup when reading (Core/EventSequencer.cs — "CARP::ResolveTagReferences turns
  them into pointers at load", Core/CollisionPack.cs WCollisionPack::Resolve), so the resolver tables have no counterpart.
* **UTL::COM::Object::_IList** Find / Add / Remove (0x5D59F0 / 0x5D7930 / 0x5D5A50: the interface list behind every QueryInterface),
  _KeyedNode::Search 0x5D51F0, StringToNumber::ConvertNumberToString 0x5D0640 (the ActionID / enum name tables: Core/InputActions),
  SplinePointList / USpline ctor-dtor, the STL vector / sort / lower_bound instantiations and the unknown_libname_* CRT stubs: language
  runtime, replaced by C#.
