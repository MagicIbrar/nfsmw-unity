# Audio behaviour (EAXSound, Speech) — spec for the Unity port

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/EAXSound/**` and `Src/Speech/**` (matched mainly against GC — PC
constants spot-checked in speed.exe 1.3 where noted), game data under `NFSMW/SOUND`, `GLOBAL/attributes.bin`,
`GLOBAL/InGameB`. Port code: `NFSMWUnity/Assets/NFSMW/Core/{Ginsu,MixMap,SoundMath,Stitch,SpeechScript,CopSpeech,CopSpeechRules}.cs`,
`Runtime/Audio/*.cs` (+ Music.cs, WorldAudio.cs, AemsAudio.cs, NisRevManager.cs), `Core/{PathMusic,WorldAudioData,AemsVm,AemsMixer,NisRevving}.cs`, `Runtime/Game/{EngineAudio,Sfx,CopRadio}.cs`. Harness checks: `audio mix|ginsu|attr|pitch|keys`,
`spchevt`, `spchai`, `aems scan|graph|run|wav|spchmask`.

## 1. Architecture

The game's audio is four layers: SFXCTL controllers (sim → numbers), CARSFX/SFXObj sound objects (numbers →
Csis/AEMS voices), the **dynamic mixer** in between, and Csis/AEMS (EA's scripted voice runtime).

- Every sound object (SndBase) owns 16 *DMIX inputs* (`SetDMIX_Input`) and reads 32 packed 16-bit *outputs*
  (`GetDMixOutput(slot, VOL|PITCH|FREQ|AZIM)`: Q15 volume, cents→pitch x4096, low-pass Hz, bAngle azimuth).
- Objects live in *mix states* (eMAINMAPSTATES: MAIN, MUSIC, PLAYERCAR, AIRACECAR, COPCAR, TRAFFIC, ENVIRONMENT,
  COLLISION, DRIVEBY, …, HELICOPTER, TRUCK), one state instance per live entity. An object id is
  `type<<29 | state<<16 | instance<<11 | objNum<<4 | slot`; objNum is the class's DEFINE_CREATABLE index (bits 4-10).
- PC state SFX sets (CSTATEMGR_*::EnterWorld): player `0x53BFC` (@0x4F2E90, **dual Ginsu**, obj 2; the decomp's GC
  value 0x53BFA is single Ginsu), AI racer `0x91`/`0x92` (AEMS or single Ginsu + turbo + skids), cop `0x20081`
  (@0x4F3350: AEMS engine 0, skids 7, siren 17), MAIN `0xFF` + controllers MasterVol(0)/GameState(1).
- Csis/AEMS: each `.abk` is `ABKC` + a module area + `BNKl` samples. **The module area is compiled x86 code** (the
  per-sound scripts that map Csis parameters to sample playback). It is not interpreted; the port picks samples by
  "AEMS sample id n = bank slot n+1" (slot 0 is empty in every MW bank) [?] and drives pitch/volume from the
  controller/mixer values directly.

## 2. Dynamic mixer — `SOUND/MIXMAPS/MAPOUTPUT.mxb` (Core/MixMap.cs)

Header {MixMapID, NumStates 13, StateTableOffset, DynamicMapOffset}; per state {StateIndex, Off MixCtl, Off 3D,
Off Sub, Off Master, Off Preset, Off Event, res} (offsets from the state header). Items:

- **MixCtl** {INPUTID, UScaleCntSwing, scaleIds[(swing>>16)&0x1F]}: `Q = Curve(INPUTID>>24 & 0xF, input)`;
  swing low 16: +offset (bit15 clear) or −range dB; `out = dB(0x7FFF − ((0x7FFF−Q)·ratio>>15)) + offset`,
  scaled by the Q15 product of the scale inputs. Ten shapes (NFSMixShape: eq-power, 1−eq-power, squared, linear,
  up/down) on the game's cos/dB tables.
- **3D** {INPUTID, per camera state {INFOID (cam bits 24-27, dist type 12-15, azim type 8-11), CURVEID_DOPPLER
  (quadrant curves in the high nibbles, speed of sound low 16), Q0..Q3 min|max metres}}: inputs are an SFXCTL_3DObjPos
  block [0 dist to car cm, 1 dist to cam cm, 2 azim to car, 3 azim to cam, 13/14 closing speed cm/s, 15 live flag].
  Rolloff between two quadrant curves blended by azimuth; Doppler cents smoothed by 0.2.
- **Event** {EVTCTLID (type bits 24-27: AR/ASR/ATR/LFO), swing, TriggerID, P0..P2 (stage time·1/60 s, curve 12-15),
  scales}: envelopes fired by a trigger input.
- **Sub channel** {MIXCHID (inputs count bits 16-23), UpperLowerSwing}: dB sum clamped to [low16, high16].
- **Master channel** {MIXCHID, MixData, SFXOBJID, inputs}: base dB = **high 16 bits of MixData on PC**
  (`movzx edx, word [edx+6]` @0x4E3190; the decomp's `*(short*)&MixData` is the GC big-endian read) + inputs, then
  presets {count|type<<24; entries slot<<26 | 3Dindex<<21 | dB(short), sign = azimuth} write the target object's
  output slots (type 0/4 Q15, 1 cents ±, 2 low-pass = cents→pitch·25000, else 0..25000; @0x4E9B30 checked).
- Instances: an input from another state expands to all instances of that state. Masters only mix *attached* objects.

Verified with the harness: at rest the player engine gets slot1/2 ≈ 0.62, LPF 24485 Hz; skid volume rises with
the slip inputs; with SNDSTATE_NIS_STORY active every player-car output drops to 0.

### Control states (SoundPause.h, SetSoundControlState @0x4B9080)
`g_ActiveSFXStates = OR of g_CtlStateActions[active ctl states]` (PC .data 0x8EE570 = decomp values
FFFFFFFF 3F 0 1C BE BC 479 121 200 33 838 7F 1 7F 38 1000 FFFFFFFF FFFFFFFF); SFXCTL_GameState feeds bits 0..13 to
MAIN SFXCTL 1; paused = ctl & 0x3483B (@0x4AE8B0). SFXCTL_MasterVol (MAIN SFXCTL 0, @0x4B59F0): 0..5 = 1 − master·vol·
eqpwr(master) per bus, 6 paused, 7 FMV, **8 = game mode SND_PURSUITBREAKER, which is what the speedbreaker sets**
(LocalPlayer::SetGameBreaker @0x6E9AA0 → EPursuitBreaker @0x641670 saves the mode and writes 10), 9 jump cam,
10 radar range. Port drives: InGame, Pause (in-game menu), FE, NisIntro / NisArrest (NisPlayer.Active,
Busting), Nis321 (race countdown), speedbreaker mode, camera state (bumper 1, hood→in-car 2, NIS 4).

## 3. Engine (Ginsu + controllers)

### GIN ("Gnsu20", Core/Ginsu.cs)
{id, ver "20", flags, float minFreq, float maxFreq, segCount, cycleCount, sampleCount, sampleRate} then
segCount+1 sample positions for evenly spaced frequencies (MW: rpm units), cycleCount+1 firing-cycle marks, XAS v0
blocks. Decel recordings run high→low. `GinsuSynthesis` (ginsusynth.cpp): 11 ms packets at the recording's rate,
whole-cycle jumps toward the target position (≤ 1 per 11 ms, 0.5 ms crossfade), `UpdateFrequency(f, 60 ms)`;
`GetCurrentPitch·120 = rpm`. Harness ramp tracks the target within ~60 ms.

### Controller chain per frame (port steps them at 60 Hz: the smoothing constants are per frame)
- **SFXCTL_Physics**: rpm% = (rpm−idle)/(redline−idle) through engineaudio `PhysicsRPM_Map` (Bezier, y at t=pct,
  player only) → audio rpm `pct·9000+1000`; throttle·100; accelerating = throttle > 30; torque = throttle.
  DMIX: 0..3 mph·{1092,546,328,234}, 4 (10000−rpm)·3.64, 5 wheels on ground, 6 POV, 10 accelerating.
- **SFXCTL_AccelTrans**: attack (+1000 rpm → phys over 500 ms, vol 0.8→0 200 ms) when throttle jumps ≥30 above
  3000 rpm in 2nd+ (≥2 s apart); idle rev flare in 1st below 1500 rpm/15 mph (acceltrans PEAK_T/RPM/VOL, RESUME_T);
  interrupt on lift (INTERUPT_T). Decel sweetener on lift above 6000 rpm in 2nd+.
- **SFXCTL_Shifting**: up-shift (≥3000 rpm) = Up_DisengageFall stages (stShiftPair {Int16 RPM, Int16 Time} + Bezier
  curves sampled at 7 points) → Up_Engage curve → post-shift RPM/volume LFO (amp·{1, .85, .7, .55, .3} by gear,
  decays); down-shift = Down_Disengage fall → Engaging rise (+Rise_RPM, EQ_PWR_SQ) → fall → re-attach
  (|Δ|·Down_Reattach_Scale ms ≤ 800). Shift sound after Up/Down_Shift_Sound_Delay; brake-mash; reverse whine.
- **SFXCTL_Engine**: clutch model (<2500 rpm, smooth 999 up/60 down), LFOs, idle "compression" wobble (random
  25..125 rpm, triggered by HybridMotor after 3 s steady above 30 mph), redline at 9800 audio rpm (engine factor
  →0.15, redline sample →0.85, time ×Slope(1..5 by gear)), distance low-pass 725 Hz..24 kHz over 6.5..50 m.
- **SFXCTL_HybridMotor (dual)**: Δrpm average (4) → pct of Accel/DecelDeltaRPMThreshold; accel mix
  AEMS/GINSU `pct·(L−S)+S`, decel mix DECEL_* with the decel Ginsu cross-faded over GINSU_Decel_Min/MaxRPM
  (FADE_OUT/FADE_IN) and AEMS = 1−xf+xf·mix; blended by torque/35; volumes ×AEMSVol/GINSUAccelVol/GinsuDecelVol,
  smoothed ±7000; Ginsu rpm = audio rpm mapped to engineaudio MinRPM..MaxRPM.
- **CARSFX_DualGinsuEng**: SNDvol = (EngVol·mix slot 2 >>15)>>8 (0..127), pitch = slot 4 × (rpm/minFreq below the
  recording), low-pass = min(GinsuLPF, slot 5). AEMS layer (Csis::CAR, CAR_nn_ENG bank): **approximated** — loops of
  slots 2 and 5 pitched rpm/4000, limiter = last slot at EngVolRedLine [?]. Tranny whine (CAR_TRANNY) not rendered.

## 4. Other car sounds (Runtime/Game/Sfx.cs)
- Shift (CARSFX_Shift): gear clunk from the shiftpattern BankName (GEAR_*.abk; up slot 2, down 1, brake mash 3),
  vol = Up/Down_Vol_Shift·cos(512−k·512)·slot 1/2, k from rpm 1500..8000; sweeteners from engineaudio SweetBank
  (SWTN_CAR_nn) at Vol_ShiftSweets·Slope(26000..32767 over 4000..7000 rpm)·slots 3..6 [sample choice ?].
- Turbo (CARSFX_Turbo, turbosfx BankName/ChargeTime/Leak_Rate/Vol_*): spool charge from torque>20, capped by rpm,
  ducked after 1 s at peak; blow-off 1 (or 2/3 above 75 % spool) when torque drops, ramped out on re-throttle.
- Nitrous: loop slot 1 while firing (mix slot 1/3/4), purge slot 3 when empty [?].
- Skids (CARSFX_Skids + SFXCTL_Wheel): slip·200 / lateral skid·81 → ±1023, inputs 0/2 fwd, 1 side, 3 load; outputs
  5 fwd, 6 back, 7 side, 8 pitch; simsurface Aud_Skid_Type 0 (hard) / 1 (loose) picks the loops [?]. Needs
  `PlayerCar.WheelAudio(i)` (read-only accessor added for this).
- Road noise (CARSFX_RoadNoise): per side, loop = simsurface Aud_Roadnoise_LOOP + 1 (ROADNOISE_00 slots 1..7),
  transitions TransON/TransOFF + 8, volume graph (0, 60:28000, 100:32500, 150:24500, 175:18000) mph, pitch
  Slope(1500..4500)/4096, slip boosts; mixer volume slot by loop (MapLoopToVolume).
- Wind (CARSFX_WindNoise): two emitters on a sphere shrinking with speed (65 m → car radius, separation
  1280..13568 bAngle), vol slots 2/3/4 × speed/40 m/s, pitch slot 5.
- Collisions (SFXObj_Collision + CollisionEvent::InitAsImpact): magnitude → intensity·127, stitch level by
  magnitude, variant by counter, from the **stitch bundle** (InGameB chunk 0x8003B500: 608 collision stitches,
  sample refs into Stich_Collision_MB) by STICH_COLLISION_TYPE group (car front/side, smackable, wall front/side,
  bottom-out), volume from a COLLISION mix state slot by description. The physics' audioimpact selection and
  magnitude: replaced in round 3 by the effects path (§13).
- Siren (CARSFX_Siren + AIVehiclePursuit siren AI): bleeps during the first 5 s, scream/yelp/wail by intensity
  (Heat ≥ 2 or 150 s) and the pursuitlevels Siren* periods, wail in roadblocks, die when wrecked; state resampled every
  0..3 s; SIREN_MB slot per state [?]. The pursuit system's own siren source is muted.

## 5. Cop speech (Core/SpeechScript.cs, Runtime/Audio/Speech*.cs, CopRadio)
- `.idx` = SPCH banks (one per line: id, speaker sub-id, attribute count, takes {BE u16 cue, attribute bits},
  masks, repeat ring); `.csi` = names + interface CRCs (no params); `.evt` = event DB: records {id, expiry,
  priority, sentences {weight, probability, slots {bankId, param index, mode, per-attribute allowed masks}}}.
  SPCH_Choose (@0x834314): weighted draw without replacement over sentences, each slot takes a bank by
  (id[, speaker]) and a take whose attributes match the Csis parameter bits, avoiding the repeat ring (keyed
  anti-repeat random @0x8346FF). Parameter structs/enum bits from COPSPEECH.hpp.
- Scheduling (SpeechManager.cpp/GameSpeech.cpp) uses each event's `speech` attributes (priority, interrupts,
  expiry, interval, max plays, required silence, cull range, follow-ons, heat/speed windows) and speechtune.
- AI (SoundAI + PursuitFlow/StrategyFlow/RoadblockFlow/Observer + EAXCop/EAXDispatch/EAXAirSupport parameter fills):
  pursuit setup sequence by reason (blow-by / rammed / scripted / vehicle stop), vehicle report (colour from paint,
  make from the car, speed bracket), location reports (road name ids), backup / strategy / roadblock calls, observer
  commentary (LOS, rollover, U-turn, braking, offroad, impacts, rams by direction), bullhorn near busted,
  lost → break-away → quadrant → time expired. Direction words follow the game's own axes (UMath x/z: +Unity x is
  "Southbound"). External `Radio.Say()` calls from the pursuit system are hints only.

### 5.1 Round 2 (2026-10-24): 911 calls, game-event hooks, pursuit getters

Addresses are PC 1.3 (`refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`); the decomp matched every function checked.

* **911 call** (SoundAI::SyncPlayers `0x71D080`, decomp SoundAI.cpp:1620-1661). With no pursuit
  (PursuitState kInactive), no 911 active (flag DISP911_ACTIVE 0x100), cops enabled and free roam:
  `mCTS911 >= CTSFor911 || mTrafficHits911 >= NumCiviHitsFor911` of the perp's pursuitlevels row (h1–h6:
  400/600/800/1000/1500/1750 and 3/4/5/6/7/5) → `EAXDispatch::Report911` (`0x717560`) with pursuit_type possible
  wanted (high intensity) or generic speeder. A "scripted" 911 also fires when the cop lockout has 0–20 s left outside
  free roam and races. mCTS911 adds the smackable COST_TO_STATE of root models the player hits while inactive;
  mTrafficHits911 counts traffic smashes with intensity ≥ MinIntensityTrafficSmash; both reset while a pursuit is
  active or searching, and in ResetPursuit. The report is rescheduled every main tick and merges with the queued
  event until it plays.
* When Disp911Report completes (Observer::MessageEventComplete `0x71EA00`): `MiscSpeech::MoreDetails(0)` (`0x71DA50`,
  a unit voice 3 + bRandom(4)), remembered in the function-static spkrID911, and `SoundAI::Force911State` (`0x70AD40`):
  flag 0x100, `perp->Set911CallTime(Lifetime911)` (45 s, 180 s at heat 6), `ICopMgr::LockoutCops(false)`. The cop side
  (ai_cops.md 9.1): patrol cars that see the player while the 911 timer runs start a pursuit, and the lockout after an
  evade is lifted. When MoreDetails completes outside an active pursuit: dispatch's vehicle description (colour known,
  make OK, coin flip) or "no vehicle description", then `Unit911Reply(spkrID911)` (`0x71DA00`).
* PursuitFlow with the 911 flag (decomp PursuitFlow.cpp:142-148, 200-202): the cause becomes k911Reported (the
  spotter-wait branch) and Reacquire is ignored. The flag stays until ResetPursuit (no new 911 until then).
* **Tunnels** (Observer::MessageTunnelUpdate `0x704950`): the player's SFXCTL_Tunnel test is done by the speech AI
  from the track path zones (tunnel, overpass, small overpass, garage, dynamic; elevation 0 or at/above the car,
  `Police/PoliceZones.cs`). PlayerInTunnel feeds HeliLostVisual "in tunnel"; entering a tunnel while the heli has the
  suspect in sight says HeliHazardAlert "approaching tunnel". Dynamic zones' VisitInfo is runtime state, taken as 0 **[?]**.
* **MUnspawnCop** (SoundAI::MessageUnspawnCop `0x71B940`): `PursuitSystem.CopUnspawned(cop, reason)`; reasons 1 and
  3..6 make the actor inactive (EAXCop::SetActive(false): a unit in formation says its bailout), 0 and 2 remove it.
* **MReqBackup** "Request" / "ReqDenied" (StrategyFlow::MessageReqBackup / MessageBackupDenied, decomp
  StrategyFlow.cpp:574-590): `PursuitSystem.BackupMessage(type, denied)` from the cop manager (16 RAM SUVs gathered or
  not placeable, 64 Cross started or not placeable). Replaces the earlier "first sight of a support car" polling.
* **Gas station** (Observer::MessageGamePlayMoment `0x70ABC0`, GasStationAftermath `0x707F90`):
  `PursuitSystem.NotifyGasStationExplosion(pos, byPlayer)`. In the strategy focus, a player-caused blast makes the
  closest unit with sight call it out (Collision_Suspect_GasStation); for 15 s every unit within 50 m calls for
  emergency vehicles (CallForEV gas fire). Nothing in the port fires the moment yet (no pursuit-breaker smackables).
* **Pursuit getters** now read the pursuit itself: TimeUntilBusted (AIPursuit.cpp:2651, the meter blended by the
  busted timer; −1 once the evade level reaches 0.05), GetBackupETA (the backup countdown), GetEvadeLevel,
  IsCollapseActive, IsFinisherActive (StrategyFlow.cpp:359 uses both; Observer::AssessOutcome only the collapse),
  IsPerpBusted (MessagePerpBusted).
* **MRestartRace** (SoundAI::MessageRestart, SoundAI.cpp:251-258): ResetPursuit(true), release the stream, clear the
  queues. The port sends it when a race starts or ends **[?: the game sends it on restarts / world reloads, and
  ResetCopsForRestart resets the speech too]**; this is also what clears a stale 911 flag.
* The cop side waits for the speech: the formation re-pick needs 15 s since SoundAI last had focus 1 (ai_cops.md 9.1).
* **Speech::Manager** (`0x6FEB40–0x71DFEC`): every Manager method is named in the IDA export and decompiled; the
  missing GC functions are the 132 `ScheduleSpeech<T>` template wrappers (31 bytes each, they only call
  ScheduleSpeechPartII `0x713B20`). CalcProbPlayback `0x704570` checked against the port.
* Exposed for the story code [ours: entry points]: `SpeechAI.D_Day()` and `DispIntroRace()` (MiscSpeech.cpp:693-701;
  their callers are outside the decomp). Harness `spchai` covers Disp911Report, MoreDetails, HeliHazardAlert, D_Day
  and DispIntroRace (all choose sentences; Disp911Report fails only for location_region 8, like the other dispatch
  location lines).

### 5.3 Round 4 (2026-10-24): SpeechScript presence-mask fix
* `.evt` record layout: the rule block and the parameter-test block each advance by `((n + 1) x 3) & ~3`, rounded
  **down** to 4 (0x831BEB for rules, 0x832FD4/0x832FAB/0x831C3B for tests), not up as SpeechScript read it. With one
  sentence and no tests the presence spec therefore sits 4 bytes earlier: NIS_Select_Start/Blacklist/End now decode spec
  0x15 (params 1..3 "set when non-zero"), mask [7], and `SpeechScript.Choose` picks exactly the takes the direct evaluation
  did (harness `aems spchmask`: 28 scene/section pairs agree, the 6 "none" are scenes with no take in NISAudio:
  GenericStart, OPMRivalIntro, IntroNisBL01). `NisStreamSelect.Pick` now calls Choose (committed, so the repeat ring
  applies); the old one-slot evaluation stays as `Direct` for the harness. copspeech.evt has no rules/tests (139 events
  with nr 0, npt 0), so cop speech is unaffected.

### 5.4 Round 5 (2026-10-24): the speech edits verified against the exe, SoundAI/Observer/flows review

Every Speech::Manager and GameSpeech function named in §5.1 was decompiled from speed.exe 1.3 (REA/Ghidra) and read against
`Runtime/Audio/SpeechManager.cs` / `Runtime/Game/CopRadio.cs`; the round-4 edits stand, with these corrections:

* **Slot pool**: Cache::Init @0x70F500 pushes `bNewSlotPool(0x80, 0xA0, "CACHE: Speech Event slotpool")` and clears flag bit 0
  (OVERFLOW_IF_FULL): 160 slots of 0x80 bytes. ScheduleSpeechPartII @0x713B20 returns null while `NumAllocated == Total`
  (the `+0x18 != +0x2C` test). The port now models the pool itself (`SlotPool`: FlushSlotPool's free list runs slot 0 → 1 → …,
  Free pushes at the head) and gives each event its slot index: the two `std::sort` calls on event pointers (ServiceFilteredEvents
  @0x7142D0 and ServiceInterruptEvents @0x714000 both call the introsort @0x70FBC0) are now sorts by slot, i.e. address order,
  instead of the round-4 allocation order. The game never frees the events it drops out of mEvents[0] / mEvents[3] by vector
  assignment (`mEvents[0] = mEvents[1]` @0x714136, `mEvents[0] = mEvents[3]` @0x714272, `mEvents[3].clear()`), so a long world
  session leaks slots; the port frees them **[ours]**, so its pool only fills with live events.
* **ScheduleSpeechPartII** merge: the Csis data is copied into the queued event whenever the actor changed, also after its sentence
  was chosen (round 4 kept the old data; no audible difference since SPCH_Choose is not rerun). Interrupt sort, `priority + 100`,
  InitDelay flag 2, RecallList, CanPlayback: as ported.
* **Deduce @0x714410**: the exe's `erase(i); ++i` over the deferred list skips (and leaks) the event after a ditched one; a deferred
  event can only be ditched through flag 1 (sample request outstanding), which the port's asynchronous decode has no equivalent
  of, so the port keeps the plain loop. PreValidate @0x70CC30 returns 4 (defer) on flag 1, then InitDelay, then PostValidate for
  interrupts (0 → 1 = kIntEvt). PostValidate @0x70C600 (1582 bytes) matched clause by clause (WorldTimer ticks × 0.00025 s).
* **GetNextEvent @0x711950**: sort_nested_priority only when the head event is not an interrupt (priority < 0x65); the comparator
  (EaxSoundTypes.cpp:238) ends on `frameindex`, which is never advanced (always 0), so equal priority + entry time are
  "equal" for std::sort — the port's arrival-order tie-break is a stand-in **[?]**.
* **GameSpeech**: Update @0x71DFF0, CheckNextEvent @0x716E30 (priority ≤ 100 waits on flag 8, else ReleaseResource first),
  IssuePlayback @0x714710, RadioChirp @0x6FF8E0 (flag 0x400, STICH_TYPE_STATIC 2: start 4 + bRandom(6); end bRandom(4) when
  bRandom(1.0) > 0.67 else 7 + bRandom(15)), UpdateChirps @0x6FF9E0 (output 13), GetVolForSpeaker @0x6FFA30 (1 → out 11, 2 → 10,
  3..8 → 3..8, else 14) all match CopRadio.cs. New: the **speech stream volume** is `GetVolForSpeaker × 0.01 × 32767` into
  SndStrmWrapper::SetVol @0x4BAD20, which multiplies by 1/127 (0.007874016) for SNDSTRM_setvol: ×258 overall, so any non-zero
  mixer output plays speech at full level and only a zero output (NIS, pause) mutes it; `CopRadio.StreamVolume` does the same
  **[?: SNDSTRM_setvol assumed to clamp at 1.0]**.
* **CopRadio / GameSpeech::IssuePlayback** with a take that failed to load: the game deletes the event but leaves its pointer in
  mEvents[2]; the port removes it (round 4 behaviour kept).

**SoundAI / Observer / EAXCop / flows review** (two read-only passes over the decomp, every function; the exe checked where the
decomp was in doubt). Fixed in `SpeechAI.cs` / `SpeechFlows.cs`:
* `mPlayerFW` (SoundAI.h:560, PC this+0x118) is never written: no store in the decomp and none in SyncPlayers @0x71D080 / the
  constructor (only mSmoothedFWRoad at +0x124 is smoothed). The port now keeps it zero like the game, so `is_ahead` is always false:
  COPS_ARE_AHEAD only with no active cars, blow-bys never blocked by "ahead", EAXCop::ReinitiatePursuit always RegainVisual, never
  Spotted **[?: assumes zeroed memory]**.
* RACERS_PROXIMAL (SoundAI.cpp:1690-1713) is now computed: the closest `AIRacer`'s distance to the camera ≤ speechtune
  AIRacerProximity sets it ("multiple suspects", `_MS` backup calls); when it clears a random active unit says FocusChange.
* LOS counts without the port-only `cop.InPursuit` term (:1750-1756); one active actor whose car is gone is removed per update
  (:1854-1863), inactive handle-less actors keep their voice until ShuffleActors / ResetPursuit.
* DealWithDeadAir's `mPlayerOffroadID < 0` term (:1045, :1057) restored; TerminatePursuit's "cops on screen" scan includes the heli
  (VEHICLE_AICOPS holds it); Force911State guards the pursuit row; BUSTED comes only from IsPerpBusted (MPerpBusted) and the port's
  own un-bust goes through MessageRestart (ResetPursuit + ReleaseResource + ClearPlayback); SoundAI::OnCollision has no BUSTED gate
  (Observe refuses the terminal focus), so the collision handlers lost theirs; the player-vs-traffic branch (:661-690) takes
  DRIVER_TRAFFIC only (AI racers no longer count as traffic hits / 911 counts); a cop hitting a smackable (an OBJECT whose other
  side is not a vehicle, :385-387) says nothing.
* Observer::AssessArrest (Observer.cpp:252-269) writes `t_busted × 32767` (clamped) to the speech object's DMIX input 5 every tick
  (the "busted" radio treatment); Observer::Process draws `GetRandomCop(0)` per observation like the game (RNG parity); Reset keeps
  the observations and resets the gas-station fields; Assess180's frame counter is a signed char (wraps after 127 frames).
* StrategyFlow reads the pursuit's real formation (`PursuitSystem.FormationType`, one-line hook; the values are the Csis
  formation ids) instead of reconstructing it from the cop slots (kept as `FormationTypeFromSlots` fallback).
* RoadblockFlow's `MMiscSound(1)` "RBLOS" (RoadblockFlow.cpp:110) goes to the channel `RBLOS`; EAXCommon and SFX_Common only listen
  on `Snd` (SFX_Common.cpp:6, EAXFrontEnd.cpp:157), so **FX_UVES 0 is unreachable in the game** — not wired, by design.

Still open after the review (not fixed; the decomp does not settle them or the port lacks the input): the actor map is sorted by
HSIMABLE in the game (first-match picks can differ) **[?]**; the EAXCop/EAXAirSupport health comes from IDamageable — the port's heli
has no damage, so the heli bailout cause is always fuel **[?]**; Sim::Collision ordering (objA/objB asymmetry :382) **[?]**; the road id
and offroad zone come from the road graph, not the nav / SPAM map **[?]**; mPursuitCount counts the no-pursuit → pursuit edge only;
pursuit restarts (MRestartRace) are sent on race entry/exit.

### 5.2 Speech not ported (and why)
* MusicFlow: ported (Music.cs MusicFlow, verified against the PC in round 9, §25); PC MW does use the interactive music.
* Cell calls: MiscSpeech::SMSCellCall / MapSMSToSPCHEnums ported in round 9 (§25; the callers are other owners' code).
  CrossDialog / ForcedRhinoArrival / SuspectGone and PlayE3Specific are empty.
* RoadblockFlow::MessageReqHeliJoinRB: heli roadblocks (roadblockhelichance is 0 on every PC row).
* EAXDispatch::EVReply / VehicleCustomPaint / VehicleVinyls: no callers in the decomp.
* AI-racer pursuits (kOtherTarget, MessageAIPerpBusted): the port's pursuit system never targets an AI racer; weather lines
  have no caller. Two-player: speech is off in split screen (round 9, §25).
* Car colour: SoundAI::GetCustomized ported in round 9 (base-paint part SPEECHCOLOUR → ColourHashToSoundColourMap, §25).

## 6. Not done / approximate (refreshed in round 4, see §19-§22)
* AEMS: the module graphs now run in the interpreter (§19), so the engine layer, skids, turbo, wind, scrape, helicopter,
  traffic, truck FX, nitrous, shifting and sweeteners play the banks' own logic. Still approximate: the bank voice renderer
  (pitch/volume exact; the PC driver's low/high-pass shapes and pan law are one-pole filters and the port's 2D pan law
  **[?]**); the engine bank: resolved in round 9 (§25: only BankName_mainRAM with sPU_or_EE = 1); ControlClass nodes only serve
  CAR_Sputter → CAR_SputOutput (other Csis classes a patch might create are ignored).
* Round 5: CARSFX_Rain and CARSFX_WindWeather are ported (§23); FX_Camera (photo finish, MiscSound 2) and the radar ping
  (MiscSound 0) are wired by their screen owners; FX_UVES type 0 is unreachable in the exe (§5.4); FX_MAIN_MEM is a memory
  budget class, not a sound. The world pursuit-breaker moment markers (g_MomentMappings → TrackPositionMarkers, matched within
  25 m of an "unpause" MGamePlayMoment, SFXObj_MomentStrm.cpp:93-116/227-236) stay unwired: nothing in the port sends the
  "unpause" moment (the world-animation side) **[hook wanted: MomentStream.Send(key by marker) from the breaker's anim start]**.
* NIS button-through re-queue: skipping a scene queues its End section through the `EReloadHud` SoundID path (handler
  @0x641840 → QueueNISButtonThrough @0x4B8C80, QueueNISStream(last start anim, -1, buttonthrough, false)); **wired**: NisPlayer.Skip
  calls `NisStreams.QueueNis(Scene.Name, track, buttonThrough: true)` (NIS owner's file).
* NIS revving (§21): the Lua `Audio_SetFlag("eNIS_321GO")` (GameFlowSndState[3]) gate is replaced by the race countdown;
  SceneTime is the port's scene clock.
* Traction usage per wheel (SFXCTL_Wheel m_fWheelTractionMag) is not exposed by PlayerCar: road noise misses that term;
  wheel slip vectors and the under/oversteer skid speeds come from slip ratio and slip angle **[?]**.
* Collision impulse = Unity's contact impulse / mass (COLLISION_INFO impulseA), falling back to the normal closing speed
  when the contact carries none **[ours]**; static world colliders carry no simsurface.
* Volumes are linear (SNDvol/127, Q15); FE/NIS/moment voices are 2D AudioSources.
* Two-player: the 2CR/2DR maps load (§17) but split-screen per-player 3D (SndCamera player 2) is not done.
* Speech: the round-4 slot-pool / interrupt-sort / RadioChirp edits are verified against the exe and corrected in §5.4; the
  SoundAI/Observer/MiscSpeech leftovers of §5.2 and the open items at the end of §5.4 stand.

## 7. Interactive music — PathFinder 5 (Core/PathMusic.cs, Runtime/Audio/Music.cs)

`SOUND/PFDATA/MW_Music.mpf` + `MW_Music.mus` are one **EA PathFinder 5.01.04** project (path library in speed.exe
0x828F90-0x830E30; `MW_Mus_1..4.mpf` of the console decomp do not exist on PC: licensed songs, the four pursuit pieces
and the ambience beds share the one project). Harness: `music map|sim|wav|mix|states|zones|stitch|smack`.

### 7.1 MPF ("xDFP" v5.1, PATH_addmapfile @0x829D20), little-endian
* Header: +0xC project, +0xD tracks (1), +0xF events (70), +0x10 remap lists (123), +0x11 named values (5),
  +0x12 u16 nodes (3681); +0x14..+0x34 nine section offsets s0..s8 (project +0x1C..+0x3C), +0x38 file size.
* s0 u16 per node = record offset/4 (FUN_0082a1d0). **Node** (s1): dw0 {s16 section (1-based into s8; 0 marker,
  -1 repeat counter, -2 random, -3 post event dw3), bits 16-20 track, 21-26 part, 27-31 repeat count}; dw1 {0-11 remap
  list, 12-16 branch count, 17-19 marker mode (1 = random controller), 20-23 beats/bar, 24-31 bars}; dw2 {u16 root node
  of the piece, bits 24-27 beat units}; dw3; then branches {s8 min, s8 max, s16 node}: next node by controller.
* s2/s3 **events**: {queue time, lifetime, action time, id | count<<24, flags (bit 17 disabled)} + 12-byte actions
  {track mask, op, data}. op: bits 8-14 type, 15 done, 16-17/18-19 operand modes (0/3 immediate, 1 track property
  FUN_0082ea50 cases 1-26, 2 named value), 20-22 if sub-op (0 while, 1 if, 2 elseif, 3 else, 4 endif), 23-25 compare
  (1 ==, 2 !=, 3 >, 4 <, 5 >=, 6 <=), 26-28 nesting (numbered at post, FUN_0082c020).
  Types (FUN_0082efb0): 1 if, 2 wait ms, 3 sync to a bar/beat subdivision (FUN_0082faf0), 4 branch (data bit 24 =
  flush: stop now; A = node or the A-th marker of part B; blocks while the stream is full), 5/6/7 volume/fx/dry fade
  (target, ms, curve 1 linear 2 quadratic 3 0.04/x), 8 set, 9 post event, 10-12 enable/disable events, 13 game
  callback, 14 math, 15 pause/resume timers, 16 banks, 17/18 pitch/stretch.
* s4 named values {name[16], value}: rapsheet, pursuitid, partnode, newnode, ambstate. s5 remap lists (pairs
  from<<16|to, FUN_0082ff00). s6/s7 PATHTRACKINFO (MUS checksum 0x8CA5CEFA). s8 sections {u32 MUS offset/128, u32 ms}.
* 27 song events (EV0 splash 0x01C53FC7 → node 1487, 26 songs), EV28 PF_EVT_START (branch by pursuitid to node
  0/427/755/1127), EV49-54 NEUTRAL/LOSE/WIN/ELUDE/ENDWIN/ENDBUSTED, EV55-68 ambience zones, EV33 stop 0x01C3FA91.

### 7.2 Runtime (Core/PathMusic.cs PathEngine)
Service (FUN_0082e510) runs queued events (≤16, FUN_0082c7b0/c400), fades, pause timers, and keeps the stream fed:
when less than the track latency (2500 ms, PATH_createstreamtrack) is queued and the stream takes requests (< 3),
Advance (FUN_00830440): branch of the queued node for the controller (FUN_0082fe00; a counter node uses its count;
nearest range else, FUN_0082fd90), remap, through marker (SongProgressCallback), counter, random and event nodes
(FUN_00830260), Play (FUN_0082ff80: whole section). Track status (FUN_0082e980): 1 stopped, 2 queueing, 3 paused,
4 ready, 5 playing, 6 fading. Volume = track vol (0..127) x PATH_volume scale (0..100) / 100.
Stream (MusicStream, PathTrackSndStream vtable 0x8C5F30): sections decoded when queued (EA-XA stereo 36 kHz),
gapless, linear resample, one-pole low-pass **[?]**, linear gain **[?]**. A blanked .mus (no SCHl at section 1)
gives silent requests of the section length: the logic still runs.

### 7.3 The music object (SFXObj_PFEATrax; PC UpdateParams @0x4E79A0, ProcessUpdate @0x4F6EA0, StartLicensedMusic
@0x4F6C80, StartInteractiveMusic @0x4DF1C0, InitSFX @0x4DFAA0) — MUSIC mix state **obj 1** (0x1010010)
* Types: licensed 0 (DMIX 6), interactive 1 / splash 3 (7), ambience 2 (8). Volume slots: FE licensed 0, IG licensed
  1, interactive 3, FE ambience 4, IG ambience 5 (×100>>15 → PATH_volume, only while status 5/2), low-pass slot 9
  (and the speedbreaker filter 65535→630→1800 Hz EQ_PWR_SQ, back over 1 s, UpdatePursuitBreaker @0x4DF7E0).
  Pause and NIS states drive these to 0 through the map (harness `music mix`).
* EA Trax (FEDatabase.cpp:606, harness `music map`): audiosystem 0x7E4B0ED2 → LicensedMusic → PFMapping[26] →
  music {SongName, Artist, Album, DefPlay FE/IG/AL, PathEvent} (StringKey fields). Default playlist by DefPlay;
  GenNextMusicTrackID @0x4F4CD0 in order (PlayState 0) or random; NotifyChyron → `MusicSystem.Chyron` event.
* Flow per frame (UpdateInGame, decomp): licensed until a pursuit (SoundAI active/searching) → interactive
  (StartInteractiveMusic; MusicFlow posts START 0.5 s later); interactive → ambience when MusicFlow ends; ambience →
  licensed after PURSUIT_TO_LIC_DELAY 40 s; licensed → next song when the track stops. Races: licensed starts only
  after the 3-2-1 (Set321 **[?]** port sets it when RaceManager.Countdown ends), none in drag races (D-Day not known).
  Song starts are muted (skip) until the song's first marker node reports.
* PC: one project, so the pursuit piece is the named value "PursuitID" = piece+1 (InitSFX, StartInteractiveMusic,
  the swap in UpdateParams after each world's first song: `m_Flags |= 0x10` only, @0x4DF100); PFXMAP @0x8EE7E8 (node
  params 0..8 per piece, events 9..20 shared); SongProgressCallback @0x4B6360 always also sends PartUpdate;
  EventActionCallback @0x4B6440 (0 set pursuitid, 2 InteractiveDone, 3 SWAP1/SWAP2).
* MusicFlow (Speech/MusicFlow.cpp, port reads SoundAI from the speech port's `CopRadio.AI`): Neutral/Lose/Win/Elude/
  Terminal by pursuit distance, speed vs the car's top speed, crashes, cop rams, cooldown, collapse; intensity
  0..127 → PATH_control. Game hooks: busted → event 15, evaded while roaming → 14, reacquire (focus lost → pursuit).
* Ambience zone: "Ambience" parameter map at the player (FE: zone 11 **[?]** garage types not told apart).
* Hook in `Runtime/Game/Media.cs` (other owner): MusicPlayer.Ensure now creates MusicSystem and returns null.

## 8. World sound states (Runtime/Audio/WorldAudio.cs)
Instance counts now follow the PC EnterWorld (SndMixer Counts): TRAFFIC 6 (@0x4E5C00), ENVIRONMENT 5 (@0x4E61A0),
COLLISION 10 (@0x4E5D30), DRIVEBY 6 (@0x4E6050), HELICOPTER 1, TRUCK 2; AI 8 / cops 16 instances are kept but only
the first 4 AI and 4 cops within 135 m of the camera sound (CSTATEMGR_CarState::UpdateParams @0x4E6330; voices gated
with a short ramp **[ours]**). Mixer objects per state: harness `music states`.
* **Stitches** (StitchPlayer = cStichWrapper): SND_SampleRef.SampleIndex n plays bank slot **n+1** (woosh bank: 23
  slots for indices 0..22); pitch = Pitch/4096 − rnd(RND_Pitch); vol = (Volume − rnd(RND_Vol)) x Params.Vol >> 15;
  azimuth Params.Az + sample Az. (The impact code used slot n: fixed.)
* **Drive-bys** (CSTATEMGR_DriveBy, SFXObj_Woosh InitSFX @0x4DD6F0): intensity = Slope(0..1 over 18..45 m/s) x 127
  (60 in the speedbreaker); stitch = GEN_RND_OFFSET(base, blocks, size) per type (tree 57/3/6, lamppost 9/3/6,
  camera 21/1/6, smackable 33/2/6, tunnel in 75/3/3, out 84/2/3, AI/traffic 45/2/6, pre-col 27/2/3, else 0/3/3);
  DMIX 11 trigger, 13 = camera-by flag for every type but tree/bridge/pre-col (PC jump table), 12 always 0 (the
  `<<8 … >>15` of the exe); volume slot by type (AI 3, tunnel in/out 4/5, overpass 6/7, smackable 8, bridge 9,
  lamppost 2, camera 10, tree 1). Dedup within 0.5 m / same unique; full → steal a state of type ≤ new.
  Sources: smackables within 20 m of the player at ≥10 m/s (smackable IsWooshable/WooshType; upright within 12 m and
  ≥3 m/s, else camera-by within 4 m of camera+2 m), tunnel entries/exits (§9), EAXAITunerCar::UpdatAIDriveBy @0x4C3980
  for cops and trucks (≤12 m, ≥15 m/s relative). Lamppost/tree triggers (event 0x10007): §16.
* **Traffic** (CSTATEMGR_TrafficCar, CARSFX_TrafficFX): 6 states within 65 m of the camera. Engine: FX_TRAFFIC id =
  pvehicle TrafficEngType (u8) → TRAFFIC_MB loop slot id+1 **[?]**, vol slot 1, azimuth 0, pitch slot 4 x range
  (mph x 7.31, the script's use guessed **[?]**). Woosh: past the player within 10 m at >10 m/s relative, 3 s apart,
  pvehicle WooshType, intensity (rel−10)/50. Skids: CARSFX_Skids inputs from the wheels, SKID_SML_MB **[?]**. The horn
  stays in Traffic.cs (traffic owner; note: CARSFX_TrafficHorn plays ENV_STATIC(HornType, ENV_COMMON) — ENV_COMMON_MB,
  not TRAFFIC_MB **[?]**).
* **Helicopter** (SFXCTL_Helicopter @0x4BFA30, SFXObj_Helicopter): speed = fwd/500 (<0.25 → 0.5) x 1023, distance to
  the closest player ≤100 (negative behind), rotation = acos(dot)>>6; vol slot 1, azimuth 0, pitch slot 2. FX_Helicopter
  is an AEMS script: the port loops slot 25 and fires blade slaps (1-12 near, 19-24 far) at 4.5..6 Hz **[?]**.
* **Fountains** (CSTATEMGR_Enviro, SFXObj_WorldObject): 4 fixed positions, 70 m; ENV_STATIC(4, ENV_COMMON) →
  ENV_COMMON_MB slot 2 (the bank's select table, §11); vol 1, azimuth 0, pitch 2, low-pass 3.
* **Player extras** (Sfx.cs): CARSFX_BottomOut jump landings (PLAYERCAR obj 13; 0.12 s / 0.7 s hang times, collision
  stitches 81.. / 93.., JumpLandingVolumes 13000/15000/24000/32767; load share as the z force **[?]**);
  CARSFX_PreColWoosh (obj 18; the barrier probe is a 0.4 s-ahead ray **[ours]**).

## 9. Tunnels and reverb (TunnelCtl, ReverbBus; Core/WorldAudioData.cs)
* TrackPathZones (chunk 0x3414A, all types: 70 tunnels, 143+178 overpasses, 8 garages, 14 dynamic; elevation set on
  all of them). Parameter maps (chunk 0x8003B600, layer = bStringHash name): "Ambience" 0x3B055833, "Reverb"
  0x79B2E325 {int zone, float, int FX offset}; lookup (x, y) = Unity (x, z).
* SFXCTL_Tunnel (ctl 6 of every car state): in-zone order tunnel, overpass, small overpass, garage, dynamic
  (visit 1 skipped); DMIX 0 tunnel / 1 overpass / 2 garage, 3 any, 4 occluded, 5/6 AEMS dry/wet (player). Player
  reverb: entry → Simple_Tunnel (5) / Simple_Tunnel_Sml (6) / Garage (3), exit → the Reverb map zone via
  ReverbZoneCrossMap; fades by g_REVERBFXMODULES (decomp values) with the 1-LSB toggle. Wooshes 0.4 s ahead.
  Occlusion (AI/cops) every 0.5 s: a ray from the player car +2 m when exactly one is in a tunnel.
* SFXObj_Reverb (PLAYERCAR obj 16): wet = AEMS wet x slot 0; the patch switches on the lead car's ReadyForSwitch.
  The FX processor is a send bus [ours]: every SndVoice and the player engine carry a SendTap at the global wet level;
  ReverbBus renders comb filters from the .fx patch's type-7 modules (ms, level/400 feedback) and its type-1 low-pass
  **[?]** (FX graph semantics guessed). Music bypasses it.

## 10. Master volume
SFXCTL_MasterVol (@0x4B59F0): slots 0/1 FE/IG music now take `Settings.Music` (one slider for both); the master is
AudioListener.volume (Settings.Master), so the mixer's master stays 1.

## 11. AEMS bank patches (Core/AemsPatch.cs) — round 3 (2026-10-24)
Each `.abk` ABKC header: +0x18 BNKl offset, +0x30 code relocation list, +0x34 **data relocation list** {u32 n, n x file
offsets of pointers}, +0x38 **class list** {u32 n, n x {u32 module header, u32 name record, u32 hash}}; module header
+0x24 code (x86 "link" function that only wires the node graph), +0x28 data, +0x2C data size; name record {u16 project,
u16 class = the Csis InterfaceId pair (e.g. FX_Scrape 3EE6/0625), char name[]}. The data relocations inside a module's
data point at shared tables after all module data:
* **Sample select** {u32 count, count x {u32 0xFF00, u32 slot, u32 0}}: parameter n → bank slot (0 = silence).
* **Key map** {u16 element bytes 1/2/4, u16 count, s32 min, s32 max, f32 (count-1)/(max-min), values}: a parameter in
  [min,max] picks entry (key-min) x scale.

Harness `audiofx banks` lists every bank's classes and tables. Used now:

| bank / class | table | used for |
|---|---|---|
| GLOBAL/FE_COMMON_MB PlayCommonSample | select[23] {1,0,2,0,3,4,0,5..12,0,0,13..18} | FE common triggers 0..22 (§12) |
| FE/FE_MB PlayFrontEndSample(_RS) | select[137] | FE triggers 23..159 (§12) |
| IG_GLOBAL/ENV_COMMON_MB ENV_STATIC | select[12] {0,1,0,0,2,3,4,0,0,0,0,5} | fountain id 4 → slot 2 (was 5); horn ids (Traffic.cs owner) 5→3, 6→4, 11→5 |
| IG_GLOBAL/TRAFFIC_MB FX_TRAFFIC | select {1,0,2}; FX_TRUCK_FX {3..8} | TrafficEngType 0 → slot 1, 2 → slot 2, 8 clamped **[?]** |
| IG_GLOBAL/FX_Misc_MB | FX_UVES {2,3,4}, FX_Scrape {1} + 4 maps, FX_Radar {5} | §12, §13 |
| IG_GLOBAL/ROADNOISE_00_MB | FX_ROADNOISE {1..7}, {8..11}; FX_ROADNOISE_TRANS {12..15} | loops; transitions now 12..15 (were 8+id) |
| IG_GLOBAL/FX_Helicopter_MB | select[31] {1..24, 0 x6, 25} | heli loop = index 30 (slot 25) |
| NOS, TURBO, SKIDS, WIND, SIREN banks | selects + maps | sample sets confirmed; graph not decoded |

Bank sounds with two channels keep the second at PT tag 0x89 (Core/Audio.cs `BankSound.DataR`); SoundLibrary now makes
stereo clips of them (50 of FE_MB's 75 sounds).

## 12. Front-end and HUD sounds (Runtime/Audio/FeSound.cs)
* `FeSound.Play(FeSoundId)` = EAXSound::PlayUISoundFX (@0x4AE8F0): trigger < 23 → EAXCommon::Play (@0x4B9900,
  FE_COMMON_MB PlayCommonSample(id, vol = SFXObj_FEHUD out 1, pitch 0x1000)); else EAXFrontEnd::Play (@0x4B9470,
  FE_MB PlayFrontEndSample(id-23, out 2)); triggers 103..118 except 110/111 use PlayFrontEndSample_RS (one at a time,
  `FeSound.Stop`). UISND_ENTER_TRIGGER raises SFXObj_FEHUD (MAIN obj 7) input 4 for a frame.
* `FeSound.Notify(hash)` = MenuScreen::BaseNotifySound (@0x5724D0): 143 FEng sound-message hashes → triggers, **read
  from the exe** (it differs from the decomp, e.g. 0x08464444 → 0x6F). FEHASH_SOUND_UP and DOWN → UISND_COMMON_UP,
  LEFT and RIGHT → UISND_COMMON_LEFT, ACCEPT → SELECT, BACK → BACK, BUTTON0..5 → WRONG; FE_COMMON leaves DOWN and RIGHT
  silent, which matches.
* `FeSound.Input(...)` [ours]: the port's screens read keys instead of running FEng button scripts, so FrontEnd.Update
  posts the FEng message each generic action would send (one-line hook replacing the old placeholder click).
* `MiscSound.Play(id)` = MMiscSound("Snd"): 0 radar-detector ping (EAXCommon, FX_Radar, FEHUD out 3; the HUD hook is not
  wired), 2 photo-finish camera (FX_Camera.abk, SFX_Common out 0; not wired), 3/4 speedbreaker start/end (FX_UVES 2/1 →
  FX_Misc slots 4/3, SFX_Common out 3/4; posted on the speedbreaker toggle like ReceivePursuitBreaker). SFX_Common =
  MAIN obj 4; its inputs are the message id for one frame.

## 13. Collisions and scrapes (Core/CollisionAudioData.cs, Runtime/Audio/CollisionAudio.cs)
The physics `effects` → audioimpact / audioscrape path, replacing the closing-speed impact guess of §4:
* `CollisionEffects` (on every car with CarSfx, traffic cars, and smackables once loose) = Effects::OnCollision
  (Physics/Behaviors/Effects.cpp): other car → OnHitObject/OnScrapeObject on "carbody"; smackable → OnHitObject, plus the
  prop's OnDetached once it comes loose (closing speed, EffectsSmackable); ground → OnHitGround/OnScrapeGround, or with
  3+ wheels down and the normal along the car's up OnBottomOut/OnBottomScrape; else OnHitWorld/OnScrapeWorld.
  EffectLookup by surface; hit intensity Ramp(impact, min, max); scrape over 1 m/s, clamp((v-min)/(max-min), 0.1, 1),
  paused 0.2 s after the last contact.
* World_OneShotEffect / WorldEffectConn audio half: AudioQuadratic (clamped 0..1), AudioCullDist (30 m),
  **ChooseAudioAttributes** (normal · owner forward < -0.707 FRONT, > 0.707 REAR, · up < -0.707 TOP, > 0.707 BOTTOM, else
  SIDE; an empty zone → DEFAULT) → an `audioimpact` or `audioscrape` (198 effects, 131 impacts, 7 scrapes).
* Sound::CollisionEvent: 100 m from the view; DESCRIPTION bits (SMOKABLE 2, WALL 4, TWO_CAR 8, CAR 0x10, ROLLOVER 0x20,
  BOTTOMOUT 0x40, FRONT 0x80, SIDE 0x100, SCRAPE 0x200, EVENT 0x400) | PRIMARY 1 when the camera car is actor or actee;
  smackable-vs-wall under intensity 10 is silent; level = clamp((int)(mag x (levels-1) + 0.5)), variant = shared
  counter % count, volume = Volumes[level]; IsDescribed(mask) = all bits.
* CSTATEMGR_Collision (10 states): smokable dedup (0.25 s, same actor/actee, the 70-intensity rule), a free state, else
  steal the lowest GetCollisionPriority. SFXObj_Collision: volume/azimuth/pitch/reverb slots by description (0x412 → 10,
  2 → 11, car+wall front 7 / side 8, bottom-out 6, rollover 5, car side 4, car front 3); primary two-car or EVENT
  impacts start their StreamSweetner moment (§14); first-update inputs 0/1/2 and input 4 = intensity x 0xFF; the stitch
  plays at out(VolSlot) x volume with the first azimuth. Scrapes: FX_Scrape = FX_Misc_MB slot 1 (a 0.8 s loop) at out 12,
  pitch 18, azimuth 1, wet 15, faded over 0.25 s once released; the patch's impulse maps (0..127 → volume 0..0x7FFF,
  pitch 3903..4374 /4096) and its two 10-entry tables (gain, hold ms) as a flutter **[?]**.
Harness `audiofx effects` dumps the data.

## 14. NIS and moment streams (Core/NisStreamSelect.cs, Runtime/Audio/NisStreams.cs)
* One NIS/SFX stream channel (Speech module 0 SED_NISSFX, Stream/NISSFXModule.cpp) over SOUND\STREAMS\NISAudio.big;
  takes chosen through the NISAudio SPCH events. SpeechScript.Choose rejects the NIS_Select_* events (they carry the
  presence-mask flag 8) **[?: reported to the speech owner]**, so NisStreamSelect.Pick evaluates their one
  sentence / one slot directly (take attribute bits in the slot masks and set in the parameters). Decoded on a worker
  thread, played when ready [ours: whole take, not the 3-block stream].
* SFXObj_NISStream::QueueNISStream (PC @0x4E7D30), GenerateNISAnimHashMap (@0x4DFE30, the decomp's 68 entries and
  values), GetCsisEventIndex (@0x4E0760, compares bStringHash(name)): starts → NIS_Select_Start(scene, track (GenericStart
  only), Complete / End when skipped), Blacklist intros → NIS_Select_Blacklist (BL06End/BL07End keep the BL06/BL07 bits:
  the button-through remap is dead code on PC too; BL01's bit has no take), arrests/endings → NIS_Select_End. Volume =
  SFXObj_NISStream (MAIN obj 5) out 1 (start) / 2 (busted) >> 8 / 127; inputs 6/7 while playing; the stream stops with
  the scene [ours]. Hooks: NisPlayer.RaceIntro queues the intro stream, NisPlayer.Busted plays the chosen arrest stream
  (the old random one as fallback).
* SFXObj_MomentStrm (MAIN obj 6, ctl 2 = SFXCTL_3DMomentPos): `aud_moment_strm` (45) {stream, param → SoundFX_Select,
  VolSlot, strmpriority, CanInterupt, IsPositioned}; ShouldStreamPlay (2 s retrigger except tollbooth/window, not during
  the countdown, not over a NIS stream, priority); positioned moments pan by out 0; dropped beyond 120 m;
  pursuit_breaker_moments raise input 5. Senders: blown engine / sabotage (blownengine / engine_breakdown), the player
  totalled, the speedbreaker start (purs_break_start_music with interactive music, else _fx; the end stops it), impact
  StreamSweetner (car2car follows its car). Harness `audiofx moments` / `audiofx nis` / `audiofx nisbanks`.

## 15. Car controllers (Runtime/Audio/CarSound.cs, AiEngine.cs, Runtime/Game/EngineAudio.cs, Sfx.cs)
* SFXCTL_AIPhysics (PC UpdateParams @0x4C8B40) + SndAIStateManager / SndAITrigger (EAXAIUtils.cpp; @0x4B1040,
  @0x4BA7B0, @0x4C4380) for AI racers: the audio rpm follows the AI driving state, not the physics. GenDeltaRPM
  (@0x4B5040) tables checked in the exe (normal {40,40,40.5,21,12,6.5,3.6,2,1} x 3.02, x2.5 coasting, drift and
  short-track tables unused); UpdateRPM (@0x4BE120) up-shift rpm = GetShiftDownRPM 3000 / max rpm; SuggestGear
  (@0x4B51E0) = speed x 0.0976 (the EAX_CarState stubs 3000 and top speed 100 are the real PC values); UpdateGear
  (@0x4B5290) runs twice. AccelTrans and redlining stay off for AI.
* CSTATEMGR_AICar::QueueSlots (@0x4E5C80) / EnterWorld (@0x4F3000): AI racers get the single Ginsu (obj 1) only when at
  most 2 engine slots are needed (opponents + 1 for cops outside quick races); otherwise, and in free roam, the AEMS
  engine (obj 0) with a per-instance pitch offset (-1500..+1000) and no MAX_RPM layer.
* EAXCar::Attach: g_TurboInfo — the first car attached (the player) picks the turbosfx every later car uses;
  GenerateUpgradedEngine — AI racers' engineaudio interval follows the player's engine level; m_EngUGL = 2 (DMIX 0 =
  21844), m_TransmissionUGL = 0.
* SFXCTL_Physics: the NOS-forces-full-throttle line is gone (not in the exe); cops (CONTROL_AI) use the smoothed
  desired-speed throttle with its 0.5 s hold **[?: proxy]**. AccelTrans idle rev at ≤ 15 mph and not during a NIS. The
  single-Ginsu mix is set, not smoothed (UpdateSingleMixEng's smooth(new, new)). Ginsu voices take the tunnel dry level.
* SFXCTL_Engine: blown-engine and totalled moments; FinalAudioRpm (the tachometer value, with the redline bounce and the
  0.7 s merge after the countdown) is exposed on CarSoundModel for the HUD owner.
* CARSFX_Turbo: the spool duck after the peak lasts T_TURBO_DUCK_POWER_DROP = 2000 ms (was 6). CARSFX_RoadNoise: a side's
  terrain changes only when its front and rear tyres agree; a blown tyre is the "blown_tire" surface and plays BLOWN;
  per-tyre transition detection, ON else OFF, DONTPLAY 4, volumes 15500/26000/0x7FFF on mixer slots 2/13/14, the
  same-loop early return.
* [ours] CarSpawner gives every spawned car an AI-racer engine; EngineAudio now retires it on traffic cars (WorldAudio's
  TRAFFIC state voices them) and on cops (CopAudio then attaches the COPCAR state with its siren).

## 16. Drive-by triggers (Core/EventTriggers.cs, WorldAudio.TestTriggers)
World event triggers: chunk 0x80036000 per streamed section {0x36001 EventTriggerPack (Version 2, section, count),
0x36002 vAABBTree, 0x36003 EventTrigger[0x20] {NameHash, EventID, Parameter, TrackDirectionMask, x, y, z, radius}}.
L2RA has 13,144, all EventID 0x10007 (TRIGGER_EVENT_SOUND), radius 10. CSTATEMGR_DriveBy::UpdateParams (@0x4FA330):
at ≥ 5 m/s the point 0.15 s ahead is tested against the drivable section's triggers (3D sphere); each hit asks for a
woosh at the trigger (closing velocity = player speed; GetFreeState drops repeats within 0.5 m). The PC compares the
trigger's first dword (a per-instance name here) with LAMPPOST / PILLAR / TRAFFIC_LIGHT / FREEWAY_SIGN / TREE and
defaults to DRIVE_BY_LAMPPOST, so every trigger plays the lamppost woosh; the LAMPPOST/TREE type hashes sit in
Parameter, which the exe does not read. Section packs load on a worker thread when first reached [ours]. Harness
`audiofx triggers`.

## 17. Mix maps and game modes
EAXSound::StartNewGamePlay (@0x4D0D70) → NFSMixMaster::CreateMainMainMap (@0x4F1EC0, szMixMapFiles @0x8F0394): a drag
race loads MAPOUTPUTDRG (split screen MAPOUTPUT2DR), other races and roaming MAPOUTPUT (split 2CR); sound game mode drag
2, challenge 6 (port: challenge series [?]), other races 5, roaming 7. `MixMap.Swap` rebuilds the graph from another
file keeping every object's input/output blocks (harness `audiofx mixswap`).

## 18. Options volumes and music timing (round 3 coordination)
* SFXCTL_MasterVol inputs 0..5 read FEDb.Data.Audio (FEMusicVol, IGMusicVol, SpeechVol, SoundEffectsVol, CarVol x2);
  EngineVol, AmbientVol and SpeedVol drive no input in the exe. The master slider stays on AudioListener (FEDb.Apply),
  so the mixer's master term is 1. MusicSystem.MusicVolume reads the FE or in-game slider by mode.
* Set321(true) when the countdown shows its first digit "3" (was at GO). RaceManager.Restarted (ERestartRace →
  EAXSound::ReStartRace, not in challenge races) restarts the race music.

## 19. AEMS module interpreter (Core/AemsVm.cs, AemsMixer.cs; Runtime/Audio/AemsAudio.cs) — round 4 (2026-10-24)
The bank patches are no longer approximated: every `.abk` module is run by a port of EA's rwaudiocore 2.09 AEMS runtime
(decomp `Libs/snd/9/source/library/cmn/saems.c` + `extern/aemsdef.h`; PC 0x8196C2–0x81A800, node table sndaemsfuncs[40]
@0x90BC40, checked against the exe disassembly node by node).
* **ModuleBank header** (aemsdef.h ModuleBank): +0xA nummodules, +0x1C moduleoffset, +0x20 sfx bank offset, +0x30 func
  fixups, +0x34 static data fixups, +0x38 interface fixups {handleOffset, IDOffset, type}. **Module** (0x3C + 4 x
  (numPlayers + numClassControllers) bytes): +0x4 class handle (its interface name = the Csis class), +0x1E maxinstances,
  +0x20 numGlobals, +0x22 numFunctions, +0x24 numPlayers, +0x25/+0x26 destructor / class-data present, +0x27 class
  controllers, +0x28 code, +0x2C data, +0x30 data size, +0x34 destroy state offset, +0x3C player/controller state offsets.
* **Code**: the module "link" function is straight-line x86 glue — `push esi; call <node>; mov [esi+d], eax` per node,
  inline `add/sub/imul/cmp/min-max` on the outputs, `add esi, n` to walk the state image, `mov byte [esi+d], 0` to clear
  triggers. The call operands are func-fixup indices into sndaemsfuncs. `AemsModule.Compile` decodes exactly that subset
  (all 386 modules of the 301 MW banks decode; harness `aems scan`).
* **Instance** (SNDAEMSI_CreateModuleInstance): a copy of the data image; header 0x18, then ClassDestructor state (0x14),
  GlobalVariable states (0x1C each), ClassData state (0x14 + 4 x outputs = the Csis member data = the class parameters),
  Function states. Tick = SNDAEMSI_timerupdate at the variable timer rate: MW sets **50 Hz** (@0x4C422F pushes 50.0 from
  0x8EE64C into the period setter @0x81B341; period = 1000/rate, so gVariableTimerPeriod @0x9C1B54 = 20 ms).
* **Nodes** ported 1:1 (saems.c): counter, random, randomshuffle, randomweighted, rangetrig, delaytrig, stategen, merge,
  envelope, table (resolution 1 direct, else linear interpolation; PC ftoitruncpositive = fistp(x - 0.5)), delayline,
  mux/demux, min/max/scale/add/sub/mul/div/mod (+ the 2-input and capped variants), player, oscillator (sine via the
  1024-step quarter-wave table @0x90BD38 x amplitude/65536), ramp (delta x period / duration x 1/4096 per tick, times the
  "scale" input), ControlClass (host callback), SetGlobalVariable / GlobalVariable (`AemsInstance.Globals`).
* **Player** (SNDAEMSI_updateplayer @0x81A52C): playcontrol 0 stop / 1 play / 2 pause; on 0 to 1 the sample group entry
  `[sampleselect]` (clamped) gives {type, patnum}: `SNDBANK_play(bank, patnum, opts)` — **patnum is the BNKl slot**, so
  the old "AEMS id n = slot n+1" rule was only the common select-table shape. Inputs {type, prev, value} x numinputs after
  the state: 0 pitch x4096 (0..0xFFFF), 1 time mult, 2 volume Q15 (opt: >>8), 3 azimuth, 5 FX wet, 6 low-pass Hz, 7
  high-pass Hz, 8 dry; changed values go to SNDAEMSI_bank*. Pause = vol 0 and pitch 0; unpause restores pitch/vol. A
  voice that ended resets sampletype to -1 (the node returns 0, so graphs restart or stop).
* **Host** (`IAemsHost`): `AemsMixer` plays BNKl sounds (Core/Audio.cs decode) with linear resampling, Q15 volume, one-pole
  low/high-pass [?], the port's pan law, sustain loops between the PT loop points; rendered in `AemsHost`'s
  OnAudioFilterRead on a child object (cars: "AEMS Engine", "AEMS Fx"; traffic slots, trucks, helicopter, scrapes).
  `CsisObj` = a Csis class instance: one module instance per bank that compiles the class (the graph self-destroys when
  its car_class / sPU_or_EE gate fails), indexer = SetXxx + CommitMemberData, Release = the destructor trigger, `Alive` =
  GetRefCount() at least 2.
* Harness `aems scan|graph|run|wav|loops|mixobjs|spchmask` (NFSMWStaging/Harness/AemsProbe.cs). `run` traces the player
  nodes (PLAY/SET/STOP per tick); `wav` renders a class through AemsMixer to a scratch WAV (CAR_00 rpm ramp, FX_SKID,
  FX_Helicopter rendered and level-checked).

## 20. Csis classes now driven by the game (parameters = the SND_GEN structs)
| class (bank) | created | parameters set per frame (struct index) | source |
|---|---|---|---|
| CAR (CAR_nn_ENG_MB_EE/_SPU) | EngineAudio.SetupAems: `CAR(CarID, 0 x 11, SPU_or_EE = 0, 0 x 13)` (PC InitSFX @0x4DC360 clamps car_class 0..99, sPU_or_EE 0..2; ctor @0x4F5C60 zeroes it) | Ginsu cars: VOL_ENG/EXH[4,5] = ((EngVolAEMS x slot 1)>>15 x 0x7FFF>>15) - 0x7FFF, MAX_RPM[25] = EngVolRedLine x slot 1 >>15, RPM[1] = Ginsu scaled rpm, PITCH_OFFSET[16] = pitch x 16383 - 0x3FFF; AEMS cars (cops, AEMS AI): EngineVol x slot 1, RPM = (rpm-1000) x (Max-Min)/9000 + Min, PITCH_OFFSET = (offset+16000) x pitch - 16000; TORQUE[3] = EngTrq x 10.24, ROTATION[17] = 150, AZIMUTH[15] = slot 0 | CARSFX_Engine.cpp:140-175, 355-380 |
| CAR_TRANNY (CAR_TRANNY.abk) | player with engineaudio Tranny: `(CarID, 0,0,0,0, 25000, 0, 0x7FFF, 0)` | magnitude[1] = fwd speed x 15, VOL[2] slot 3, AZIMUTH[3] slot 0, PITCH[4] slot 4, LoPass[5] slot 5 freq, Wet/Dry[6,7] tunnel AEMS, single_Shot[8] = shifting | :341-400 |
| FX_SHIFTING_01 (GEAR_* bank) | clunk `(up 1 / down 0, vol, 0x1000, az, SHIFT, cam = bumper)`; brake mash `(2, slot 10, ...)` while mashed | one-shots; dropped when the patch ends | CARSFX_Shifting.cpp:290-335 |
| CAR_SWTN (SWTN_CAR_nn) | Disengage id 0 (rpm >= 7000, slot 4), Engage id 1 (RPM_AtShift >= 7000, slot 3), Accel id 1 (slot 5), Decel id 0 (slot 6); vol = Vol_ShiftSweets x Slope(26000..32767 over 4000..7000) x slot | `(id, CarID, rpm, vol, az, 0, 0)` | :217-288 |
| CAR_WHINE (CAR_WHINE_00) | while m_bShouldBeWhining `(0 x 6, 25000, 0, 0x7FFF)` | VOL[2] = slot 8 x rpm/max rpm, AZIMUTH[3], RPM[1] | :175-213 |
| CAR_Sputter (SWTN_CAR_nn) | player: `(CarID, this, 0 x 9)`; its ControlClass node makes CAR_SputOutput whose member data {volume, class, id} is the blip volume (SparkChatUpdateCallBack): DMIX 0 = blip, 2 = 0x7FFF while non-zero | AZIMUTH[4] slot 0, PITCH/ROTATION/force 0, RPM[2], TORQUE[7] = PhysTrq x 10.24, VOL[3] = slot 1 x Vol_Sputters, accel_true[9], shifting_true[10] | CARSFX_SparkChatter.cpp:57-154 |
| FX_TURBO_01 (turbosfx bank) | spool at InitSFX `(0, 0, 0, az, 0, rpm)`; blow-off `(1, or 2/3 above 75 %, 0, spool x 1024, 0, rotation 0, rpm)`, released when its ramp (150 ms on re-throttle) hits 0 or the patch ends | spool: volume[1] = vol_Spool x slot 1, PSI[2] = spool x 1024, azimuth[3], rotation[4] = PhysTrq x 10.24, rPM[5]; blow-off: volume = BlowoffVol x slot 2/3 x ramp | CARSFX_Turbo.cpp:118-299 |
| FX_NITROUS / FX_PURGE (Nitrous_00) | `FX_NITROUS(0, slot 1, az, 0, 0, 25000, 0, 0x7FFF, 0)` on fire, NIT_STOP[3] = 1 on release (the patch plays its tail); `FX_PURGE(0,0,0,0, 25000, 0, 0x7FFF, 0)` when the bottle empties | NIT volume[1] slot 1, azimuth[2], pitch[4] slot 3, LoPass[5] slot 4; PURGE volume[0] slot 2, azimuth[1], pitch[3] 0x1000 | CARSFX_Nitrous.cpp:50-200 |
| FX_SKID (SKID_BIG; SKID_SML for traffic) | `(0,0,0,0, SkidType, 0 x 13, 25000, 0, 0x7FFF, 0)` | VOL_Fwd/Side/Back[0..2] slots 5/7/6, Wet[21] slot 9, PITCH[5] slot 8, azimuth[3] slot 1, surface[11] = max Aud_Skid_Type, sPEED[7] mph, UNDER/OVERSTEER[9,10] (Chassis::Calculate*Factor: mean lateral skid of the front pair while opposing the steering / of the rear pair, over speed, at most 1), front/back FB/LR/Load[12..17] averages while a side is down | CARSFX_Skids.cpp:104-150 |
| FX_WIND (WIND_00) | player `(0x1000, 0 x 8, 25000, 0, 0x7FFF, 0, 0xF)` | volume_left/right[1,2] = slot 2/3 x weight, rumble[8] = slot 4 x weight (weight = clamp(speed, 2..40-0.05)/40 x 32767), pitch[0] slot 5, azimuth l/r[3,4] slots 0/1, intensity[5] = ratio x 1040, FX_Dry[11] 0x7FFF | CARSFX_WindNoise.cpp:107-191 |
| FX_Scrape (FX_Misc) | per scrape `(0,0,0,0,0, 25000, 0, 0x7FFF, 0)`; released when the event detaches (0.25 s after the contact ends) | volume[0] slot 12, pitch[1] slot 18, azimuth[2] slot 1, Dry[7] 0x7FFF, Wet[8] slot 15, LoPass[5] 25000, impulse_magnitude[4] = intensity | SFXObj_Collision.cpp:178-220 |
| FX_TRAFFIC (TRAFFIC_MB) | per traffic slot `(TrafficEngType, 0,0,0,0, 25000, 0, 0x7FFF, 0)`; the patch's sample group {1, 0, 2} and pitch tables take every id (type 1 silent; 8 = garbage truck / fire engine takes the last entry with its own table rows) | volume[1] slot 1, azimuth[3] slot 0, range[4] = mph x 7.3142858 (0..1024), pitch_OFFSET[2] = slot 4 - 0x1000 | CARSFX_TrafficFX.cpp:66-128 |
| FX_TRUCK_FX (TRAFFIC_MB) | TRUCK state (2, SFXIDs 0x184001, obj 19): while a tractor shifts an air puff `(2 + bRandom(4), 0, 0, az, 25000, 0, 0x7FFF, 0)`, braking to a stop from >= 1 mph a hiss `(bRandom(2), ...)`; one at a time until the patch ends | volume[1] slot 1, azimuth[3], pitch_OFFSET[2] = slot 2 - 0x1000 | SFXObj_TruckFX.cpp:25-78 |
| FX_Helicopter (FX_Helicopter_MB) | HELICOPTER state `(0 x 7, 16000, 0 x 4)` while a heli exists | Azmuth[4] slot 0, Distance[6] = min(100, d), negated when azimuth - 0x4001 < 0x7FFF unsigned (behind), Volume[3] and FX_Dry[9] slot 1, Speed[5], Pitch_Offset[1] slot 2, Rotation[11] | SFXObj_Helicopter.cpp:60-104 |

The port's pause gate: each AemsHost's Gain = (paused ? 0 : 1) x attach ramp **[ours]** instead of every CARSFX object
zeroing its Csis volumes (the g_EAXIsPaused branches). Road noise keeps the select-table loops (§15) and is silent while
`NisEventRunner.DisableRoadNoise` is set (ENISRoadNoise sets Tweak_DisableRoadNoise 0x9B09B0, checked in
CarRenderConn::UpdateRoadNoise @0x745C70, which then feeds the sound side no road-noise packet).

## 21. NIS revving (Core/NisRevving.cs, Runtime/Audio/NisRevManager.cs; SFXCTL_NISReving.cpp)
* `SOUND\NISREVDATA\<scene>.bin`: 16 sets {s32 n, n x {f32 time, s32 rpm, s32 trq}}, channels car1..car8 = 0..7,
  cop1..cop8 = 8..15; little-endian on PC. Opened when a NIS starts (NISActivity.cpp:544 OpenNISRevData(mSceneHash)).
* SFXCTL_Physics::UpdateNIS: while a NIS plays, every car with a set follows it at the scene time (linear between points);
  AI racers during the 3-2-1 ("QRev" from Start321Reving, EAXSound::Update on gb_Is321) play RevPat5..12 (pattern 5 +
  bRandom(7); the eight tables ported verbatim) dt-driven; under 1 s of countdown left: NIS_MERGE_WITH_PHYSICS (torque 100
  while the last set rpm is below the physics rpm, rpm smoothed toward it by 500/update); after a pattern rpm - 500 and
  torque - 15 per update. Clamps 1000..10000 / 0..100; accelerating = torque > 30, throttle = torque; the early returns (no
  data for the car) leave the car's values as last set. Hooked into CarSoundModel.StepOnce after the physics/AI controller
  and before SFXCTL_Shifting (SFXCTL_Physics.cpp:104-113, SFXCTL_AIPhysics :290-298).
* GameFlowSndState[3] (`eNIS_321GO`, Lua Audio_SetFlag @0x6048E0 with the name table @0x8F5D78) gates the AI countdown
  revving; the port uses `RaceManager.Countdown` and sends Start321 when the first "3" shows (as Music.cs Set321) **[?]**.

## 22. Real inputs replacing round-3 proxies
* Cop throttle (SFXCTL_Physics CONTROL_AI): `m_fDeltaDesiredSpeed` = 2-sample average of the per-frame delta of
  `mDesiredSpeed`, now fed by `CopCar.DriveSpeed` (the speed the cop AI drives to); throttle toward 100 while the average
  is above DesiredVelDifThresh -1 and no decel hold runs, else toward 0 with HoldDecelDuration 0.5 s after a negative
  delta (SFXCTL_Physics.cpp:60-78).
* AI steering for SndAIStateManager (EAXAIUtils.cpp:175): `GetSteering()` is a bAngle = bRadToAng(fSteering) of the -1..1
  input (SoundCar.cpp:120), x 360/65536 and wrapped above 180; the port converts the AI steer input the same way (was
  input x 30 deg).
* Collision intensity: COLLISION_INFO impulseA = contact impulse / mass (RigidBody.cpp:759/1116), taken from Unity's contact
  impulse (fallback: normal closing speed); smackables keep Length(closingVel) (EffectsSmackable::OnCollision).

## 23. Round 5 (2026-10-24): AccelTrans verified, rain and wind-weather objects, speech review
* **SFXCTL_AccelTrans** (Runtime/Audio/CarSound.cs UpdateAccelTrans): every function disassembled / decompiled and compared —
  UpdateParams @0x4C5860 (returns for eMM_AIRACECAR, PlayEngOffSweet cleared twice, the attack/idle begin on the accelerating edge,
  m_bNeed_DeccelSnd on the lift edge), UpdateState @0x4BCDC0 (the interrupt uses the attribute AccelFromIdle_INTERUPT_T, hash
  0x49FB8CE5, EQ_PWR_SQ 5; the decomp's second 100 ms LINEAR block is in the exe too but dead; RESUME_T / PEAK_T / PEAK_RPM / PEAK_VOL
  at acceltrans +4 / +0 / +8 / +0xC), UpdateRPM @0x4BCCD0, UpdateTRQ @0x4BCD50 (states 1..4), BeginAccelTrans @0x4B47A0
  (+1000 rpm over 0.5 s EQ_PWR_SQ, vol 0.8 → 0 over 0.2 s, torque 100 for 10 ms, m_bNeed_AccelSnd), BeginAccelTrans_Idle @0x4B4830
  (PEAK_T ≤ 0 → 10 ms), ShouldBeginAccelTrans_Idle @0x4BCF70 (≤ 15 mph, Δthrottle ≥ 30, NISRevingEnabled — only ever cleared,
  SFXCTL_Physics.cpp:31 — state NONE, shift idle, FIRST_GEAR 2, ≤ 1500 rpm, no NIS playing), ShouldBeginAccelTrans @0x4B4900
  (Δthrottle ≥ 30, state NONE, shift idle, ≥ 3000 rpm, 2 s since the last, then `IsAICar || gear ≥ SECOND_GEAR`), ShouldPlayEngOffSweet
  @0x4B4990 (≥ 6000 rpm, gear > 2). One fix: the gear exemption is `IsAICar` (every non-player car: cops run AccelTrans, AI racers
  return early), the port tested the AI-racer state only. The symbol `ShouldBeginAccelTrans@0x49DC20` in the IDA export is a
  mis-name (it initialises signature-channel tables). File fully ported.
* **CARSFX_Rain** (PLAYERCAR obj 11, EnterWorld SFXIDs 0x53BFC bit 11) and **CARSFX_WindWeather** (obj 12, bit 12) are ported in
  Runtime/Game/Sfx.cs (UpdateRain / UpdateWindWeather / ApplyWeather) — PC MW does have the rain audio: `IG_GLOBAL\FX_Rain_MB.abk`
  carries the Csis class FX_Weather, `WIND_00_MB.abk` FX_WIND_Weather, and MAPOUTPUT's PLAYERCAR state has obj 11 (outputs 0/1)
  and obj 12 (output 0). Rain: intensity = Rain.Intensity (eView::Precipitation→GetRainIntensity), > 0.01 creates
  `FX_Weather(0,0,0,0,0,0,25000,0,0x7FFF,0)`; ≤ 0.01 sets rain_on_off = 1 and deletes the object 10 s later; ProcessUpdate volume =
  out 1 with the hood/bumper camera else out 0, Hood_Rain by the camera, Width 7000; DMIX input 2 = 0x7FFF while raining; thunder
  moments (key_thunder_distant 0x016C4FA1 below 0.5, key_thunder_close 0xD2C5E045) every 25 + bRandom(20) s above 0.25 when the
  NIS/SFX channel is free, with the 33-frame block counter (IsWorldDataStreaming taken as false **[?]**). Wind weather: created at
  once, volume = out 0 × Slope(1 → 0.25 over 0..70 mph), Width 4000, Intensity 0. Harness `aems run` on both patches: the rain
  patch ramps four voices up with the volume and decays them after rain_on_off (≈ 8 s), the wind-weather patch starts its two
  loops a second after it gets a volume. Not heard in the editor yet.
* **FX_UVES 0** unreachable in the exe (channel mismatch, §5.4); **FX_Camera** (MiscSound 2) wired by PhotoFinishScreen, the radar
  ping (MiscSound 0) by HudExtras; **FX_MAIN_MEM** is the Csis memory-budget class, no sound.
* Engine bank: both `CAR_nn_ENG_MB_EE` and `_SPU` keep getting the CAR instance (§20); the exe has no literal `_EE` / `_SPU` string,
  the names come from the engineaudio BankName_mainRAM / BankName_auxRAM attributes **[? unchanged]**.
* Coverage: SFXCTL_AccelTrans, Stream/SpeechManager, Stream/GameSpeech, Speech/SoundAI, Observer, EAXCop, EAXDispatch, EAXAirSupport,
  EAXCharacter, SpeechFlow, PursuitFlow, StrategyFlow, RoadblockFlow, MiscSpeech, SFX_Common, CARSFX_Rain → Ported (deviations
  listed in §5.2 / §5.4 / this section); MusicFlow (no interactive music engine) and SpeechCache (the sample cache is replaced by
  the asynchronous decode) stay Partial.

## 24. Round 5b (2026-10-24): NISActivity sound hooks, GameFlowSndState, breaker moment markers
Hooks are one-liners in NisPlayer.cs (Begin, the first READY_TO_PLAY tick, Skip, Finish) and WorldAnimations.ControlWorldAnim; the
logic lives in `Runtime/Audio/NisSound.cs` and `NisStreams.cs` (MomentStream).
* **GameFlowSndState[15]** (@0x911EE4) is now modelled (`SoundState.GameFlow`, `SetGameFlow` = the ESndGameState(index, on) ctor
  @0x6253D0, which is also what Lua `Audio_SetFlag` @0x6048E0 posts: name table @0x8F5D78 — eNIS_Start 2, eNIS_321GO 3, eNIS_Busted 4,
  eNIS_EndofRace 5, eFE_PostRaceScreen 6, eFE_CameraScreen 7, eLOAD_GeneralLoading 10, eLOAD_BustedLoading 11; FEMovieScreen uses 1,
  uiSMSMessage 13, PhotoFinish 7, FELoadingScreen 10). Readers ported: SFXObj_FEHUD::UpdateMixerOutputs @0x4DDDE0 copies the 15
  flags into MAIN obj 7's inputs (FeSound.UpdateMixerInputs; the UISND_ENTER_TRIGGER input 4 is OR-ed in **[?]**), SFXObj_PFEATrax
  (SFXObj_Pathfinder.cpp:989-997) keeps EATRAX_OFF while 4/5/6/7/11 are set (Music.cs Tick), SFXCTL_Physics reads 3 (§21). Not
  ported: EAXSound::Update's extra `Speech::Manager::Deduce()` while 13 (SMS) is set; the other screens' flags (FE / HUD owners can
  call `SoundState.SetGameFlow`).
* **NISActivity::Load @0x6F51F0** (`NisSound.Loaded`): mNISType Arrest (1) or Ending (2) with an empty skip-to name →
  ESndGameState(11, true). The flags set from the scene name (bStrNICmp): +0x39e IntroNisDD, +0x39f IntroNisBL, +0x39d
  IntroNisBL/IntroNis10/EndingNIS ("story"), +0x3a0 non-skippable (same three + IntroNisDD).
* **UpdateLoading @0x6F7230 → READY_TO_PLAY** (`NisSound.ReadyToPlay`): SetSoundControlState(true, …) @0x4B9080 — DD "NIS Load DDay" 7,
  else non-story with type ≠ Intro: Arrest "NIS Play Arrest" 10 (other types none), else non-BL: "NIS Play Story" 6 / "NIS Play Intro" 7,
  else "NIS Play BLK" 9; a Moment (type 4) sets none. An Arrest then calls Speech::Manager::ClearPlayback @0x70E570 and
  `SoundAI::Get()->mDispatch->ArrestReply()` @0x717110 (PC; the GC decomp has `cop_speech->ReleaseResource()` there). The arrest's
  2 s LOADING hold (`elapsed − 2 ≤ load time`) precedes it (NisPlayer `_loadHold`). The port infers CAnimChooser::eType from its entry
  points (Busted → Arrest, EndingNIS* → Ending, race-start candidates / GenericStart → Intro, moment candidates → Moment, else World)
  **[?]**.
* **SkipOverNIS @0x6EA960** (`NisSound.Skipped`): ClearPlayback + the cop speech module's ReleaseResource.
* **~NISActivity @0x6FCE10** (`NisSound.Finished`): EAXSound::NISFinished @0x4C1C60 = SFXObj_NISStream::NISActivityDone @0x4E0800 + clear
  NIS_STORY/INTRO/BLK/ARREST (inlined, RUN_SOUND_STATE-gated); "DDAY flying": NIS_321 off when the scene is IntroNisFlyInDD;
  ESndGameState(eNIS_Busted 4, false). **PlayNIS @0x4B8D80 is only SFXObj_NISStream::StartNIS** — the decomp's
  SoundPause(eSNDPAUSE_NISON) in PlayNIS / NISFinished does not exist in the PC build, so there is no NIS pause reason to wire.
  SndMixer no longer derives NisArrest/NisIntro from `NisPlayer.Active` + Busting; it only drops a leftover state when no NIS exists
  (a player torn down without Finish) [ours]. eNIS_Busted (4) **on** is posted by the busted Lua/career script, not the NIS (not wired
  here: `SoundState.SetGameFlow(4, true)` for the career owner).
* **Pursuit-breaker moment markers** (`MomentStream.WorldAnimUnpaused`, hook in WorldAnimations.ControlWorldAnim's play branch):
  SFXObj_MomentStrm::InitSFX (:93-116) collects every TrackPositionMarker named after a g_MomentMappings marker type
  (GasStation 1 → expl_gas_station, Firehall 2 and PoliceStationGround 15 → comm_tower_crash, WaterTower 4 → wtr_tower_crash,
  PorchCollapse 5 → porch, SignageCollapse 6 → gate, Scaffolding 7 → scaffold_crash_big, Gazebo 8 → gazebo_crash, FishMarket 9 →
  fish_sign_crash, BoatKnockdown 10 → boat_fall, TrailerPark 17 → trailer_park, Amphitheatre 19 → amphitheatre, StripMall 20 →
  stripmall, DriveIn 16 → drive_in, TorusRoof 18 → torus; aud_moment_strm collection = Jenkins key of the name); ReceiveMoment
  (:221-236) maps an "unpause" MGamePlayMoment to the moment whose marker is within 25 m on both horizontal axes (the last match
  wins), else nothing. The port fires it when a world-animation tree is set to play (not pause / hide) from its placement **[?: the
  game's sender of the "unpause" moment is outside the decomp — the breaker's world-anim trigger]**.


## 25. Round 9 (2026-10-25): PC function-by-function pass
Tools: Ghidra headless `tools/ghidra.sh decompile` (for functions REA does not know), capstone, and a float-constant diff of every
audio/speech function against the port's literals (only the mph factor, MPH2KPH and sentinels differed). Harness probe
`audio9 surfaces|colours|ints`.
* **Engine banks** (CARSFX_AEMSEngine / SingleGinsuEng / DualGinsuEng::SetupLoadData PC @0x4E8880 / @0x4E8930 / @0x4E8A40, InitSFX
  @0x4DC360, InitializeEngine @0x4EE1C0): only engineaudio BankName_mainRAM ("_EE") is loaded, and SetupLoadData sets sPU_or_EE = 1
  before InitSFX (AEMS engines only when BankName_auxRAM is non-empty, else no bank and no CAR). Harness `aems run`: the EE patch with
  1 stays alive, EE with 0 and SPU with 1 destroy themselves. EngineAudio makes the CAR in the main bank with param 12 = 1; CAR_TRANNY
  car_class is clamped 0..60.
* **CSTATEMGR_CarState::ResolveCarBanks** (PC @0x4F9020 from EAXSound::CommitAssets; ResetCarBanks @0x4F30D0, DestroyCar @0x4F3200,
  AddMapping @0x4EBD10): Runtime/Audio/EngineBanks.cs. AI racers and cops share at most 4 engineaudio banks; over budget, engines are
  remapped (the cop V8 rule with corvette_z06 / for_mus_b / for_mus_c / hum_h1 @0x897D7C, same-EngType pairing, same-type or random
  loaded engine) and the car's engineaudio is swapped (EngineAudio.Remap). CopsCanBeInGame = !quick race (FEGameMode bit 2). The port
  batches the cars whose engines first tick in the same frame [ours].
* **SndCamera::UpdateCameras** (@0x4D5D40): the mixer camera state is latched only when the camera director's action changes
  (CDActionDrive → POV bumper 1 / hood 2 / else 0; CDActionTrackCar → 5 when the car is totalled else 3; CDActionIce → 4;
  CDActionTrackCop keeps it). A POV switch while driving does not change it and the first drive action of a session stays 0 (exe
  behaviour). EAXTunerCar::UpdatePov (@0x4B9FE0) → SndMixer.PovType / IsDriveCamera; IsBumperCameraOn / IsHoodCameraOn
  (@0x4D5C20 / @0x4D5C00) feed CARSFX_Rain, GetPOV the shift clunk, SFXCTL_Physics and the 3D positions. EAXTunerCar's
  SFX_BOTTOMOUT / SFX_TRUNKBOUNCE messages have no sender on PC (scan of vtable +0x44 calls): dead.
* **SndBase::GetDMixOutput** (@0x4EB7D0): DMX_AZIM reads 0 in mono (AudioSettings.AudioMode 0).
* **SFXCTL_3DObjPos** (GenerateSinglePlayerMix @0x4B5E50, SetCameraAngle @0x4B5D70, UpdateDoppler @0x4BF2E0): the camera closing
  speed subtracts the camera velocity (SndMixer.CamVelocity, from the listener's movement); a closing-speed sign change sets input 15
  bit 31 (car) / bit 30 (camera), so the mixer skips that frame's Doppler.
* **Mixer** (ProcessMixMap @0x4F4270, Update3DMixCtls @0x4F18E0, UpdateEvtMixCtls @0x4F17F0, AR / ASR / ATR @0x4EA230 / @0x4EA0D0 /
  @0x4E9E70, Sub / Master @0x4E30D0 / @0x4E3160, MixMasterChannels @0x4E9B30) compared: matches; event scale counts are masked 0x1F;
  AddScaleIDs' overlapping fill (`n + m`) changes nothing on the four shipped maps (scan).
* **SFXCTL_Tunnel** (all 12 functions compared): g_REVERBFXMODULES is the PC table @0x8F2140 (rows 5-7, 9 and 10 differ from the GC
  decomp). The speech observer's TunnelUpdate now comes from the player's TunnelCtl.
* **STICH_PlayBack**: the per-type voice limit (cStichWrapper::Play @0x4CF520: more than 25 queued + playing samples → prune by
  priority 0..10, queued first @0x4CEB80, then playing @0x4CECE0). cStitchLoop (road-noise loop 8) is unreachable: no simsurface has
  Aud_Roadnoise_LOOP 8 (harness `audio9 surfaces`).
* **CARSFX_RoadNoise**: the wet-road loop m_pWetRoad[2] (InitSFX @0x4DAB10: FX_ROADNOISE(4, 0 x 6, 25000, 0, 0x7FFF, 0)) at generated
  x RoadNoiseVolumes[4] 24000 x mixer slot 3; punctured tyres (DidTireJustPucture, TireState.Punctured) play the SPIKESTRIP transition.
* **SoundCar slip** (physics hook, physics.md §27): SFXCTL_Wheel's slip and skid come from PlayerCar.SoundWheelSlip (SoundCar::OnService:
  slip moved toward 0 by 0.2 x ToleratedSlip, skid = -GetWheelSkid) for the player car (Sfx.cs) and the traffic skids (WorldAudio).
* **SFXCTL_Engine** (UpdateParams @0x4BC420, UpdateRPM @0x4BC870, UpdateRedlining @0x4BC6A0, LFO @0x4B4110, Volume @0x4B4030, Torque
  @0x4B4080, FilterFX @0x4B3ED0, Clutch @0x4B4330): redlining also needs CurGear != the top gear (EAX_CarState+0x1FC = GEAR_RATIO count
  - 1, set in its ctor @0x4C9ED1 **[? attribute 0x07A7A3E5]**); the visual rpm uses the pre-LFO base rpm and last frame's redline flag
  (UpdateRPM runs before UpdateRedlining).
* **SFXCTL_HybridMotor** (Single / Dual mix @0x4BD160 / @0x4BD650, outputs @0x4BDC30, delta @0x4B4CA0): no smoothing also on the
  down-reattach stage (7) while accelerating; the driveline IsShifting flag (+0x1EC) is private in PlayerCar **[hook wanted]**. The
  steady-cruise compression wobble can never trigger (tSteadyDuration is reset every frame it has not elapsed — PC and port alike).
  NISRevingEnabled (the delta's first branch) is only ever cleared.
* **SFXCTL_Shifting, EAXCar::Attach, SFXCTL_Helicopter, SFXObj_Helicopter**: compared, matching.
* **MusicFlow** (the IDA names of the state handlers are shifted on PC: state 0 → 0x71B1A0, 1 → 0x71B4E0, 2 → 0x71B740, 3 → 0x704B30,
  333 → 0x700E10, 999 → 0x700ED0): the PC adds a 600 s piece swap in Neutral and Win (RequestSwap @0x700E80: MControlPathfinder "Swap"
  → SFXObj_PFEATrax::MessageSwapInteractive @0x4DFC40 = PFXMAP[proj][PF_EVT_INITSWAP 18]); ported. gXMP_DOWNSTATE (Xbox user tunes)
  is never set on PC. PC MW plays the interactive pursuit music from MW_Music.mpf/.mus (§7).
* **NIS stream channel** (SED_NISSFX::QueStream @0x6FFC50): while a request is queued and unplayed, thunder / collision / moment
  requests are refused; a moment cutting a playing stream ends the NIS stream object's activity (inputs 6/7).
* **NISActivity::UpdatePlaying** (@0x6EA7DC): eLOAD_BustedLoading (11) goes off at the first playing frame (it used to stay on and
  keep EA Trax off after an arrest).
* **eNIS_321GO**: NIS revving reads GameFlow[3] (Lua Audio.SetFlag in the basicrace countdown state) when the race is Lua-driven.
* **Speech**: SoundAI::GetCustomized (colour from the base-paint part's SPEECHCOLOUR, ColourHashToSoundColourMap @0x902028);
  MiscSpeech::SMSCellCall + MapSMSToSPCHEnums (@0x71D860 / @0x701540) with CopSpeechManager.CellCallStarted / CellCallCompleted
  (GameSpeech.cpp:340/391, SpeechManager.cpp:69-77/957) and the bucket_01 / call_07 double volume (GameSpeech.cpp:539); speech is off
  in split screen (EAXSound.cpp:1319); MPH2KPH 1.60931 (.rdata 0x8A0298). Every audio/speech mph conversion uses the PC's 2.23699
  (.rdata 0x8967E0).
* **N/A by design**: EAXS_StreamChannel and Speech::Module (stream plumbing, bank offsets → the async decode of the take at its .big
  offset), SpeechCache (sample cache and memory pool; the readiness gate is kept), STATE_Base / Registration / STATEMGR_Base (object
  creation, controller sorting, attach/detach → SndMixer units; controllers in SFXCTL id order, then objects), NFSMixMap allocation /
  PreProcess / Connect (→ MixMap.Build), STICH_Loader (Core/Stitch.cs), EAXVolumes (tables inlined).
* Hooks wanted (other owners): FEMovieScreen `SoundState.SetGameFlow(1, true)` + `SoundState.Set(true, SndCtl.Fmv)` in the ctor
  (@0x58ED07/@0x58ED1A), `SetGameFlow(1, false)` + Fmv off in NotificationMessage (@0x57DE24); PhotoFinishScreen `SetGameFlow(7, true)`
  in NotificationMessage (@0x5A86AD), false in the dtor (@0x57D91C); FELoadingScreen `SetGameFlow(10, true)` + `SoundState.Set(true,
  SndCtl.Off)` in the ctor (@0x58F019), both off in the dtor (@0x57E0BB); uiSMSMessage `SetGameFlow(13, true)`, FEUpscreen off,
  FESmsMessage on in the ctor (@0x553892), the reverse in the dtor (@0x511F3A); `SpeechAI.SMSCellCall(id)` from
  uiSMSMessage::NotificationMessage (@0x531DAE / @0x531DDA) and ECellCall (@0x62B1CD); NotifyCellCallStarted / Complete from
  CopSpeechManager.CellCallStarted / CellCallCompleted (replacing LuaGameHost's 6 s timer); PlayerCar.IsShifting.
