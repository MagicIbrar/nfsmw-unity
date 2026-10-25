# Lua stategraphs and their bindings (NFS MW 2005 PC v1.3) — LuaGameHooks, the stategraph runtime, Shadow/Drive

Recovered from speed.exe (capstone disassembly; REA/Ghidra where it had the function), the IDA symbol export
`refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`, and gameplay.bin itself (the 258 handler chunks run on the C# VM). It
extends `gameplay_core.md` §5 and §7 and `career_flow.md` §0. Guesses are marked [?], our own additions [ours].

Code: `NFSMWUnity/Assets/NFSMW/Core/Lua/` (VM + runtime, Unity-free), `Runtime/Lua/` (the game side, Shadow/Drive).
Harness: `lua scan | run [activity|*] [variant] [verbose] | selftest | drive [win|lose|quit|resume|resume-lose|resume-quit|resume-nostate|dday|traps] [verbose] | dis <handler> | entries <collection> | chain | persist | order | ice [group]`.

## 1. The Lua inside speed.exe

* **Lua 5.0.1** (version string at 0x8A3EC8; `_luaV_execute` 0x60E9D0, `_luaD_precall` 0x60E7D0, `lua_open` 0x6156A0,
  `_LoadHeader` 0x614A40). No EA changes to the VM were found; the only configuration change is the number type.
* **lua_Number = float (32-bit)**. Chunk header (lundump 5.0): `1B 4C 75 61`, version `0x50`, endianness 1, sizeof
  int/size_t/Instruction 4/4/4, SIZE_OP/A/B/C 6/8/9/9, sizeof(lua_Number) **4**, test number 3.14159265e7 as a float.
  Number → string is `sprintf("%.14g")` of the float promoted to double (0x8A461C), so `0.1` prints `0.10000000149012`
  and exponents have MSVC's three digits (`1e+020`).
* Instruction layout (5.0): OP bits 0-5, C 6-14, B 15-23, A 24-31; Bx = bits 6-23; sBx = Bx − 131071; RK ≥ 250 = K[x−250].
  35 opcodes. gameplay.bin uses 25 of them (no MUL, DIV, POW, NOT, TFORLOOP, TFORPREP, SETLIST, SETLISTO, CLOSE).
* **No standard library is opened**: there is no `print`, `pairs`, `string`, `table`, `math` or `coroutine` in the exe.
  The only globals are the EA bindings (§3), `ChangeState` (bound per dispatch, §2.4), and the E* event constructors,
  which the globals table's `__index` (LuaRuntime::HandleGlobalIndex 0x61B970) creates on first use: a name starting
  with `E` is looked up in the event registry (RegisterEvent::GetLuaBinding 0x6337A0), the C closure is cached in the
  globals; anything else is nil. `^` would call the global `__pow`, which does not exist. No script uses coroutines.
* gameplay.bin: 258 `messagehandler` records, each with `bytecode` (Attrib::Blob → EA HUFF → one chunk). The chunk's
  main function does `CLOSURE` + `SETGLOBAL <record name>`; it is run once at load. The chunks are stripped (no line
  info): our error messages give the instruction index instead (`(none):pc14: attempt to index a nil value`).

## 2. The stategraph runtime (GManager / GActivity / GHandler / LuaPostOffice)

### 2.1 Data
* **Handler** (`messagehandler`, GHandler): `handler_owner` (0x857FE432), `stateref` (0x918C796E), `message_id`
  (layout +4, the Hermes UCrc32 of the message class), `transitionlist` (data only), `FilterBlocks` (0x56E1436D) /
  `FilterModePassAll` (0x0D038CFA). **No PC record has FilterBlocks**, so MessagePassesFilters (0x5DD410) always passes.
  (As compiled, a pass-any filter list whose filters all fail still passes; only pass-all lists can reject.)
* **State** (`state`, GState): `Name` (0x3E225EC1), `templateref` (0xA0697302).
* An activity's states: every state whose templateref is the activity or one of its ancestors (FUN_005E2D70).
  A state's handlers (FUN_005E2E70): stateref = the state **and** (handler_owner = the activity, or the owner's
  CollectionName — layout field 0 — equals the activity's, or the owner is an ancestor of the activity). Template and
  instance handlers **both** run (e.g. 16.2.1's `done` runs basicrace's done handler and its own JumpToCarLot one), in
  the **GHandler ring order** (resolved, round 10): GatherStatesAndHandlers (0x5F9270) fills each state's vector with
  FUN_005F5B90, which walks the GHandler ring (DAT_0091DFEC) forward from its head; the GRuntimeInstance ctor (0x5E8130)
  links every new instance at the ring's tail and CreateObjects_GHandler_ (0x5F55D0) creates them in the vault's export
  order (FUN_005F4090: Attrib::Vault exports of type CollectionLoadData). Every messagehandler record is in the gpcore
  vault (harness `lua order`), so the order is gameplay.bin's ExpN order — the order Core/Lua keeps. 40 (activity, state,
  message) groups run an instance's handler before its template's (e.g. 16_1_0's racing TriggerEnter: `_9` of the
  instance, then basicrace's `_13` and the base one).
* GActivity::HandleMessage (0x5F9DA0) builds the message table once per delivery (LuaMessageDeliveryInfo::
  BuildMessageTable 0x609FE0 caches it, flag +0x24) and the activity tables once, then calls every matching handler of
  the state's vector to its end — a Suspend inside a handler does not stop the loop (a Reset frees the vector; we stop).

### 2.2 Activities
* `Activity.Run(x)` (0x6048A0): GRaceParameters::BlockUntilLoaded, **GActivity::Reset** (0x5F4780: stop, deactivate
  the triggers, unregister, `registry[CollectionName] = nil` — the state table is dropped, current state = none), then
  **GActivity::Run** (0x5F93D0): mark running, ActivateReferencedTriggers, then a queued `EChangeState("initial")`.
* `Activity.Suspend(x)` (0x5F4730): stop and unregister, state and table kept.
* **ChangeState(name)** (FUN_005ED7D0, the closure bound in BuildActivityTables 0x5F9D30): GetStateByName, then an
  **EChangeState event is queued**; the handler goes on running. When it executes (GActivity::ChangeState 0x5FA3C0):
  same state → nothing; else StateExit to the old state's handlers (delivered directly), unregister, set, register,
  StateEnter (directly).
* The state table (r2) is `registry[CollectionName]`, created on first use (0x5F9CC0); it survives state changes
  and is only dropped by Reset (or a releasing SaveState). Activity state is saved in GManager object-state blocks
  (§2.2.1, implemented in round 10).
* **GActivity::Run (0x5F93D0) is not Activity.Run**: nothing when running; else running = 1,
  ActivateReferencedTriggers, and **with a current state (+0x28) only RegisterMessageHandlers — no StateEnter**; without
  one, EChangeState("initial"). Activity.Run (the binding) Resets first, so a script's run always starts at `initial`;
  GManager::StartActivities, EnterBin and StartRaceFromInGame call GActivity::Run directly, so a suspended or restored
  activity **resumes** where it was.

### 2.2.1 Saved activity state (GActivity::SaveState 0x5F6BA0 / LoadState 0x5F9480, GManager object-state blocks)
* **SaveState(release)**: only with a state table (+0x3D). Block = {stringhash32(current state's `Name`) (0 = none),
  flags: bit 0 running, bit 1 the state is `done`, high word the table size; the serialized table — not in `done`}.
  AllocObjectStateBlock (0x5F5F10, key = the collection key) puts it in the **persistent** map (+0x12C) when the
  activity's `Persistent` attribute (0xE4542E9B) is true, else in the **transient** map (+0x138). `release` drops the table
  (registry[name] = nil, +0x3D = 0). Callers: SaveGameplayData (0x5F7410, every activity, release 0), the GActivity dtor
  (0x5F7C10: Persistent ones, release 1 — EndGameplay unloads the vaults), StartRaceFromInGame's FUN_005F77F0 (the bins'
  running activities, then Suspend) and the Lua heap pager 0x612E80 (release 1) [ours: no pager].
* **The serializer** (FUN_00617A80 / reader FUN_00617F80) keeps pairs whose **key** is a boolean, number or string;
  **values** boolean, number, string, nested table — and light/full userdata **only in the transient map** (raw bytes +
  the metatable name's hash). So a Persistent activity's instance references are not saved (none of race_bin, speedtraps,
  milestones keeps one in r2). [ours: our own byte layout; the transient map holds a live copy whose instances are
  re-bound by key in the next runtime.]
* **LoadState** runs whenever the activity has no table: in its ctor (0x5F9C30), the table getter (0x5F9CC0) and
  ChangeState (0x5FA3C0, before and after the unregister): the block from the transient map, else the persistent one
  (FUN_005ED960); the current state whose stringhash32(Name) matches is set (handlers not registered) and the table is
  restored (+0x3D = 1). **The running flag is not restored.** GActivity::Reset (0x5F4780) clears the block
  (ClearObjectStateBlock 0x5F30F0).
* `Persistent` activities: race_bin_01..16, race_bin_challenge(_tollbooth), race_bin_collectors, race_bin_opm, speedtraps,
  milestones (and their templates); the engage activities, menu gates, drive_to_safe_house and the races are not.
* The career save: SaveGameplayData writes "Game", the persistent map, the 8 timers (SaveTimerInfo 0x5E4C40 {duration,
  active, elapsed, name[19]}), the milestone type info and milestones, the speed traps, 16 dwords at +0x264, the pending
  SMS list (+0x2A8) and the free-roam / safehouse respawn markers (+0x108/+0x10C), with a checksum (FUN_0065B6E0).
  LoadGameplayData (0x5F81D0) first runs ResetAllGameplayData (0x5F7730: both maps, milestones, speed traps, timers,
  markers cleared). The transient map lives as long as GManager (the session).
* So **a resumed career continues its stategraphs**: BeginGameplay → StartActivities → GActivity::Run of race_bin_NN,
  speedtraps, milestones → handlers of the saved state registered, no StateEnter, no entry SMS again.

### 2.3 Messages and the Event queue
* `Hermes::Message::Post` (0x654AF0) **does not deliver**: it creates an `EDeliverMessage` event. Messages from
  bindings and from C++ are therefore delivered in queue order together with EChangeState and the E* events, after
  the handler that caused them has returned. LuaPostOffice (RegisterHandler 0x61FD00) maps a message id to the
  activities whose current state has a handler for it, in registration order.
* GActivity::HandleMessage (0x5F9DA0): for every handler of the current state with that id: on the first one,
  BuildMessageTable (a fresh table r1 per delivery) and BuildActivityTables (r0 = the activity's GRuntimeInstance
  userdata, r2 = the state table, global `ChangeState` bound to this activity); then GHandler::HandleMessage
  (0x5DD520): `_G[record name](r0, r1, r2)` with `lua_call` (an error is not caught in the exe; we log it and go on).

| id | message | r1 fields (read by the scripts) | posted by |
|---|---|---|---|
| 5E1AA4C2 / 9C020C14 | StateEnter / StateExit | — | ChangeState |
| 459EABF5 / A72E4B1B | TriggerEnter / TriggerExit | Sender (trigger), Element (simable) | GTrigger::NotifySimableTrigger 0x5F6A30 (exit only with FireOnExit) |
| 122C89F1 | NotifyRacePlacement | Activity, Racer, Placing | Game.NotifyRacePlacement 0x60AA00 |
| 0EF58DC9 | NotifyFinished | Sender | Game.NotifyActivityFinsihed 0x605420 |
| 029084E4 | NotifyTimer | Name | GEventTimer::Update 0x5DDF50 (repeating) |
| 2F80BB9F | NotifySimTick | SimTime | every sim tick |
| 319915CD | LoadingComplete | — | GRaceStatus::Update after NotifyScriptWhenLoaded / a world reload |
| D98FA287 | CountdownDone | — | the countdown screen |
| 6638715C | NISComplete | — | the NIS player (NIS.Play, EPlayRaceNIS) |
| 89BB36FE | NotifyMovieFinished | — | the FMV player; PlayTutorial when no tutorial plays |
| 52584CC7 | NotifyMessageDone | — | SixDaysLater.fng (EShowMessageScreen) |
| 51E7B945 / 7FCF45CF | FlowReadyForOutro / EnterFreeRoam | — | the post-race flow (EnterPostRaceFlow, photo finish, results) |
| 6CE66AA0 | RestartRace | — | pause menu / results Restart |
| C1E81A41 | NotifyRaceAbandoned | — | pause menu / results Quit |
| 0D4D38E5 | NotifyKnockedOut | Racer | Game.KnockoutRacer 0x611440 |
| 5BC34E1C / 04FD4A1B | NotifyEngineBlown / NotifyVehicleDestroyed | Racer | damage, Game.SabotageEngine |
| AA228814 / 3E6D010D | NotifyRaceTimeExpired / NotifyRaceTimeSecTick | Racer / — | GRaceStatus::Update |
| 2F1DBF5A | NotifyPlayerRep | RepPoints | Game.AwardPlayerBounty 0x612220 |
| 26BB525D | NotifyChallengePassed | — | milestone / speed trap completion |
| 5F72B9E8 | NotifySpeedTrap | TrapActivity | speed-trap completion [?] |
| 5F7D24A3 / C9EABFC6 | NotifyMilestoneReached / Progress | MilestoneName, ValueReached | pursuit stats |
| 8CB52A16 / CE28AB70 | PerpEscaped / PerpBusted | Perpetrator | the pursuit |
| 32F2E00B | EnteringGameplay | — | entering free roam |
| AF4C4648 / 8870DE98 | AcceptEnterCareerEvent / DeclineEnterCareerEvent | SimTime | the engage dialog |
| D8E54190 | EnterSafehouse | ZoneType | the menu-gate dialog |
| 5E2D52FC / 23B3ABD0 / 24624AED | NotifyCellCallStarted / Complete / NotifySpeechStatus | — / — / SoundID | cell calls, speech |
| 21F36738 | CareerIntroDone | — | the safehouse intro screens |
| DCDEAB15 | ICECameraFinished | — | Camera.SetGenericCamera |
| B16576C0 | NotifyOnlineRaceOver | ShowPostRace | online races |

### 2.4 The userdata the scripts index
* **GRuntimeInstance** (metatable 0x61C180): `__index` (0x61BB60) = Attrib::Instance::Get(StringToKey(key)) (parents
  included): an array attribute → **LuaAttribArrayInfo** {instance, key}; otherwise by the field type (table
  0x8F5DC0): Float/Double/Int*/UInt* → number, Bool → boolean, Text/Char/StringKey → string, GCollectionKey/RefSpec →
  the connected instance (GetConnectedInstance 0x5E2850); a type without a handler (vectors: `Position`) → nil. A key
  that is no attribute of the class is looked up as a **connection**: GManager::ConnectInstanceReferences (0x5E4210)
  connects every GCollectionKey entry of the collection (class-defined or extra, e.g. `call_from_mia`) under
  (key, index), and FUN_005E43D0 connects every `Children` entry under StringToKey of the **part of its name after the
  last '/'** (so `r0.traffic1` is the child `…/traffic1`). `__newindex` (0x61BD60) has no setter for any type: writes
  are dropped. `__eq` (0x61BE80): same instance.
* **LuaAttribArrayInfo** `__index` (0x61BF00): a number n → element **n − 1** (arrays are **1-based**); `"Count"` →
  the length. `__eq` (0x61C0F0): same instance and attribute.
* **ISimable** userdata (the racers' and players' vehicles) with the method `IsPlayer` (FUN_0061B0B0).
* `RaceStatus` is the GRaceStatus object, its functions are methods (`RaceStatus:AddCheckpoint(x)`).

### 2.5 Racer indices (correction)
Racer indices are **1-based**: Game_GetRacerIndex (0x60DAE0) returns i + 1 (−1 for a non-racer);
GetRacerElement / GetRacerCharacter / RacerIsHuman (0x60DB60 / 0x604DE0 / 0x611E20) read racer info i − 1. The player is
racer 1. GetPlayerElement(0) and (1) are the first player (0x612370). The `basicrace` loops run `for i = 1, NumRacers`.

### 2.6 Timers
Game.SetTimer(name, t) (GManager::SetTimer 0x5E4A40): 8 slots {duration, active, elapsed, hash, name[19]}; a timer of
the same name is restarted, else a free slot is taken. **Timers repeat**: GEventTimer::Update (0x5DDF50) posts
MNotifyTimer(name) each time elapsed ≥ duration and subtracts the duration, until KillTimer.

## 3. The bindings (LuaBindery::BindToGameCode 0x61E750)

Signature = the binder's conversion (v void, i int, f float, b bool, s string, I instance, S simable). Kind: Q query
(answered from the C# game in every mode), A action (recorded in Shadow, executed in Drive), C core (the runtime).
C# = what LuaGameHost answers / LuaDrive executes. "post" = the message the C++ half posts back (emulated in both modes).

| binding | addr | sig | kind | what it does (exe) → C# |
|---|---|---|---|---|
| Table.Randomize | 618AC0 | v(T) | C | raw binding: n = luaL_getn(t); n passes of `for i = 1, n-1: j = bRandom(n)+1; if j ~= i swap t[i], t[j]` |
| Activity.Run / Suspend | 6048A0 / 6048D0 | v(I) | C | §2.2; Drive: a race's runs when its race is loaded (GameFlow.StartRace, §4.2) |
| Audio.SetFlag | 6048E0 | v(sb) | A | music/NIS audio flags (eNIS_Start, eNIS_321GO, …) |
| Audio.IsCopSpeechPlaying | 604A00 | b() | Q | Speech::Manager::IsPlaying(COPSPEECH_MODULE 1) → CopRadio.Instance.Playing |
| MiniMap.AddEngagedRace | 604B50 | v(I) | A | map icon |
| Bin.GetNumRacesWon / GetNumChallengesPassed | 604A30 / 604A10 | i(i) | Q | GRaceBin stats → GameSession.RacesWonInBin / ChallengesDoneInBin |
| Math.RandomInt | 604CB0 | i(i) | C | bRandom(n) + 1 (1..n; 1 for n = 0). bRandom (0x45DA30): r = seed % n, seed' = t ^ (s ^ (t << 27)), s = seed ^ 0x1D872B41, t = s ^ (s >> 5) on bDefaultSeed (0x12345678, re-seeded from the ticker at boot); the sequence is shared with the whole game [?: unreproducible] |
| Camera.SetGenericCamera | 604A50 | v(ss) | A | ECinematicMoment(group, track, 0) (+ MGamePlayMoment for 'Cinematics'/'Challenge'); CameraAI::Director::SelectAction (0x479BD5) posts MICECameraFinished when that GENERIC ICE track ends → after the track's Length (Cinematics/Challenge 3.5 s) → LuaDrive.CinematicMoment (hook for the camera code) |
| NIS.Play | 604B80 | v(Ississ) | A | (marker, scene, 'Intro'/'Outro', track, …); post NISComplete when RaceManager.Intro / NisPlayer ends |
| Movie.PlayHackE3FMV, Debug.Assert, Demo.*, Game.IntroduceRival, Game.HideRaceOverSummary, RaceStatus:AddAvailableEventToMap / AddSpeedTrapToMap | — | | A | empty on PC (`ret`) |
| Debug.Print / PrintInstance / ShowScreenMessage | 604C70 / 604C80 / 6121C0 | | C/A | debug output |
| HUD.ShowMessage / ShowTimeExtension | 611320 / 6113E0 | v(s) / v(f) | A | HUD text (label) / +time → Systems.HudMessage |
| Platform.IsNextGen | 604CC0 | b() | Q | false |
| Game.SabotageEngine / BlowEngine | 60AB60 / 60AB30 | v(Sf) / v(S) | A | engine blows (after t s); post NotifyEngineBlown |
| Game.DetachCameraFromRacer | 611540 | v(S) | A | |
| Game.ChallengeComplete / ChallengeCompleted | 60E200 / 605140 | v(S) / v() | A | challenge bookkeeping |
| Game.ForceAIControl / ClearAIControl | 6123B0 / 612420 | v(i) | A | AI drives player i (rolling starts, D-Day) |
| Game.InitRacers | 61DBE0 | v(I) | A | gameplay_core §2.2 → ILuaRaceControl.InitRacers |
| Game.WarpPlayerToTrigger / SetPlayerStartPosition / ResetTrigger | 611570 / 611670 / 604D10 | v(I) | A | → ILuaRaceControl.WarpPlayerTo / – / LuaTriggers.Reset |
| Game.SetRacerGoal | 611700 | v(iI) | A | §1.6 of gameplay_core → ILuaRaceControl.SetRacerGoal; ledger `Goal` |
| Game.SetRacerLapsLeft | 604DB0 | v(ii) | A | HUD laps |
| Game.KnockoutRacer / TotalRacer | 611440 | v(S) | A | GRacerInfo::KnockOut; post NotifyKnockedOut → ILuaRaceControl.Knockout; ledger |
| Game.NotifyRacePlacement | 60AA00 | v(ISi) | C | posts MNotifyRacePlacement(activity, racer, placing) |
| Game.NotifySpeedTrapTriggered | 60A7B0 | v(IISf) | A | records the trap speed (open world: completes the trap) |
| Game.NotifyCheckpointReached / NotifyLapFinished / NotifyRaceFinished | 6117F0 / 611850 / 6119F0 | v(Si) / v(Si) / v(S) | A | gameplay_core §2.3-2.5 → ILuaRaceControl; ledger |
| Game.SetRaceActivity / StartRace / StartRaceTimers | 604ED0 / 60DBD0 / 604EF0 | | A | → ILuaRaceControl.StartRace / StartRaceTimers |
| Game.SaveStartPositions / RestoreStartPositions | 604E90 / 60AA50 | v() | A | grid snapshot for restarts |
| Game.EnterPostRaceFlow | 611F20 | v() | A | results; post FlowReadyForOutro when the FE closes |
| Game.ShowWinningPostRaceScreen / ShowLosingPostRaceScreen | 60E240 / 60E230 | v() | A | FE_ShowPostRaceScreen(b) 0x60AC20; losing: RestartRace or NotifyRaceAbandoned after the FE closes |
| Game.ShowOnlinePostRaceScreen | 605690 | b() | A | |
| Game.SetCopsEnabled / NoNewPursuitsOrCops / ForcePursuitStart / ResetCopsForRestart / PreventPlayerBeingBusted | 604F40 / 60AAB0 / 60AAC0 / 60AB10 / 60AE00 | | A | → Systems.Police.Enabled / – / ForcePursuit(heat) |
| Game.EnterEngagableTrigger / ExitEngagableTrigger | 604F80 / 604FD0 | v(I) | A | engage dialog (Accept/Decline) |
| Game.EnterGateZone / ExitGateZone | 611FB0 / 611FF0 | v(Is) | A | menu-gate prompt (EnterSafehouse) |
| Game.ShowRaceOverSummary | 6120C0 | v() | A | EShowRaceOverMessage |
| Game.SetTrafficSpeed / SpawnCop / SpawnCharacter / UnspawnCharacter / SendCharacterStimulus / JackKnife | | | A | scripted traffic and cops |
| Game.SetAllStaging | 60DF70 | v(b) | A | cars held on the line → ILuaRaceControl.SetAllStaging |
| Game.ShowPauseMenu | 6050F0 | v() | A | |
| Game.AwardCash / AwardPoints | 612130 / 60E030 | v(Sf) | A | speedtrap points / cash grab → ILuaRaceControl.AwardPoints |
| Game.UnlockRace | 612140 | v(I) | A | score flags 8 (+4) → RaceDatabase.UnlockRace; ledger |
| Game.NotifyCountdownDone | 60E160 | v() | A | |
| Game.ShowTriggerIcon / HideTriggerIcon | 604D20 / 604D30 | v(I) | A | |
| Game.WarpToMarkerWhenRoaming / DoSpecialFinalization | 605930 / 605890 | v(I) | A | |
| Game.IsActiveSpeedTrap / IsActiveMenuGate | 611E80 / 604E40 | b(I) | Q | → GameSession.ActiveSpeedTraps / the zone trigger's GIcon (+0xC0) visible (GetIsIconVisible 0x5DDB30): safehouse icons AllowEngageSafehouse, car lot / customshop AllowMenuGates, none during an NIS |
| Game.GetSimTime | 604CD0 | f() | C | Sim::GetTime |
| Game.GetRacerIndex / GetRacerElement / GetPlayerElement / GetRacerCharacter / GetNumRacers | §2.5 | | Q | → RaceManager.Active.Entrants (simable = the PlayerCar) |
| Game.GetSimableSpeedKmh / RacerIsHuman / PlayerIsLocal / ISimable:IsPlayer | | | Q | PlayerCar.SpeedMps·3.6, Entrant.Human |
| Game.SimableAngle (correction) | 6053B0 | f(SI) | Q | **dot(simable forward vector (vtable +0x34), GRuntimeInstance::GetDirection)** — a cosine; the epic pursuit's win trigger wants ≥ 0.8 |
| Game.IsRaceCompleted / IsRaceUnlocked | 6051B0 / 605170 | b(I) | Q | score flags 2 / 8 → RaceDatabase |
| Game.IsOnlineGame / IsLANGame / IsCareerMode / IsSplitScreen | | b() | Q | GameSession |
| Game.AllRacersDone / AllHumanPlayersDone | 6051F0 / 60E0A0 | b() | Q | Entrants finished or out |
| Game.SimableDistance / SimableAngle | 60AB90 / 6053B0 | f(SI) | Q | distance to the instance's Position / angle [?] |
| Game.AllowEngageEvents / AllowMenuGates / AllowEngageSafehouse | 605640.. | b() | Q | UpdateTriggerAvailability 0x5EBA50 → no race, pursuit state |
| Game.DoSpecialSetup / SetCurrentTimeOfDay | 605830 / 605780 | b(I) | A | true = the world must reload first; → false in the port (no reload) |
| Game.ReloadWorld | 6057E0 | v(I) | A | then LoadingComplete |
| Game.GetPlayerBounty / AwardPlayerBounty | 612300 / 612220 | i() / v(i) | Q/A | Profile.Bounty / GameSession.AwardBounty; post NotifyPlayerRep; ledger |
| Game.JumpToCarLot / AbandonRace / JumpToSafeHouse | 605250 / 60DEB0 / 6052B0 | v() | A | → – / RaceManager.Abort / GameFlow.JumpToSafeHouse |
| Game.SetTimer / KillTimer | 605300 / 605320 | v(sf) / v(s) | C | §2.6 |
| Game.ShowGPS / NavigatePlayerTo | 605340 / 605360 | v(b) / v(IIf) | A | GPS |
| Game.NotifyActivityFinsihed (sic) | 605420 | v(I) | C | posts MNotifyFinished{Sender} |
| Game.SkipCareerIntro | 612490 | b() | Q | SkipFE, the SkipCareerIntro switch (0x926125), or the first player's career flags +0xAC bit 0x20 → Profile.Stage past the safehouse drive |
| Game.SetChanceOfRain / SetWorldHeat | 6054A0 / 612660 | v(f) | A | |
| Game.Fade / PlayTutorial / DoSafeHouseIntro / DoZoneMenuAction / SetHasRapSheet | 605500 / 6124E0 / 605560 / 612030 / 605670 | | A | PlayTutorial: a career drag/tollbooth/speedtrap race not seen (career+0xAC bits 0x40/0x100/0x80) plays `drag_tutorial` / `tollbooth_tutorial` / `speedtrap_tutorial`, else posts MNotifyMovieFinished |
| Game.CalculateRanking | 6058B0 | i(Si) | Q | mRanking (speedtrap: FinalizeRaceStats first) → Entrant.Place |
| RaceStatus:ClearRacers / AddRacer / ClearCheckpoints / AddCheckpoint / SetNextCheckpointPos / SetRoaming / SetIsLoading / EnterSuddenDeath / AwardBonusTime / SetActivelyRacing / SetTaskTime / DisableBarriers / EnableBinBarriers / NotifyScriptWhenLoaded / SetHasBeenWon | 5E30A0.. | | A | GRaceStatus (gameplay_core §1); NotifyScriptWhenLoaded → LoadingComplete |

**E* events** used by the scripts (deferred like every event; argument counts from the bytecode): E911Call(0),
EAddSMS(id) (GManager::AddSMS 0x5F5120), EBailPursuit(2), ECellCall(0-2: id, …), ECinematicMoment(3),
EDebugScreenMessage(4), EDispIntroRace(0), EFadeScreenNoLoadingBarOn/Off(0), EJumpToStrategyFlow(0), ENISNukeSmack(4),
ENISWolrdGeometry(2), EPlayRaceMovie(name), EPlayRaceNIS(7), EResetProps(0), EShowMessageScreen(id), EShowPostPursuit(0),
EShowRaceCountdown(0), ETerminateMusic(0).

Decoded for Drive (capstone on speed.exe, names from the IDA export):
* **Game.EnterPostRaceFlow** (0x611F20): FinalizeRaceStats (0x6021B0), **Game_AbandonRace** (0x60DEB0: timers stop, the AI
  stops); then, racing with race parameters that have **NoPostRaceScreen** (GetNoPostRaceScreen 0x5FC320): post
  MFlowReadyForOutro at once and set the event's score flag 2 (completed in career) — this is how 16.1.0 and 16.2.1
  complete; otherwise FE_ShowPostRaceScreen(true) (0x60AC20: FinalizeRaceStats, the pursuit milestones screen for pursuit
  races, the photo finish and PostRace_Results.fng). MFlowReadyForOutro is posted by PhotoFinishScreen::NotificationMessage
  (0x5A7FEC/0x5A818B/0x5A822F) and uiRepSheetRivalFlow::Next (0x53045C). Game.ShowWinningPostRaceScreen is never called by
  gameplay.bin; wins go postrace → EnterPostRaceFlow.
* **Game.SpawnCop** (0x60A670): (marker, vehicle, InPursuit, RoadBlock): nothing without a marker position/direction or
  a vehicle name; ICopMgr::LockoutCops(false) (+0x2C), then ICopMgr::SpawnCop (+0x1C, AICopManager::SpawnCop 0x439DD0: a
  queued request). AICopManager::TrySpawnCop: InPursuit → StartPursuit on the local player and added to his pursuit,
  else RoadBlock → StartRoadBlock, else StartPatrol. **Game.SpawnCharacter** (0x604D40): GCharacter::Spawn(position,
  direction, target marker, speed) (0x601320) — **speed in mph** (stored × 0.44703 as m/s). Game.SetTrafficSpeed
  (0x605090): MSetTrafficSpeed(speed, f2, 1) to the character's vehicle [?: units, f2]. **Game.SetWorldHeat** (0x612660): IPerpetrator::SetHeat on the player's simable.
  **Game.JumpToCarLot** (0x605250): SetTimeOfDay, EQuitToFE (car lot). **Game.NavigatePlayerTo** (0x605360):
  WorldMap::SetGPSing + GPS_Engage(gGPSDestination = the marker's position, distance); **Game.ShowGPS(false)** (0x605340):
  GPS_Disengage.

### 3.1 Corrections to other specs found by running the scripts
* gameplay_core §2.3 "the jump to prerace branch is compiled out": it is not. `racing` TriggerEnter on a
  `ResetsPlayer` trigger by the player **restarts the race from `prerace`** offline; only online races
  (`r2.OnlineRace`) warp to racestart instead. (A finish crossing is also reported as NotifyCheckpointReached(racer, 0)
  before NotifyLapFinished.)
* Racer indices are 1-based (§2.5); timers repeat (§2.6); messages and ChangeState are deferred (§2.3).

## 4. Shadow and Drive (Runtime/Lua)

`LuaFlow.Mode = Off | Shadow | Drive` (default **Drive** since round 9, see §4.2 and §5.2; `-lua off|shadow|drive` on the
command line). The scripts run in career and (round 11, §7) in quick races — the exe's QuickGame creates GameplayActivity, and
with it the Lua, for quick races too (§4.3); `-lua careeronly` keeps them to the career. `LuaFlowDriver` ticks it every frame; it is re-created if something destroys it (play-mode
tests destroy every root object), and the runtime starts over when the profile object changes or gameplay ends
(GameFlow.EndGameplay: the main menu, the D-Day car lot), the reset deferred to the next frame.

### 4.1 Common to Shadow and Drive
* **Activities** (round 10, resolved): career free roam = GManager::BeginGameplay with no startup race →
  StartActivities (0x5F9E70) → StartActivitiesCall1 (0x5F95F0): **every AutoStart activity of a loaded vault** that is not
  running (FreeRoamOnly ones too, there being no startup race), then the bin activity race_bin_NN (16 while driving to the
  safehouse) if not running — all with **GActivity::Run, no Reset** (§2.2), so saved states resume. Loaded vaults (`lua
  order`): gpcore holds the menu gates, speedtraps, milestones, race_bin_NN and race_bin_16/drive_to_safe_house; each
  race_bin_NN/engage lives in the race_bin_NN vault, which GRaceBin keeps loaded only for the current bin (EnterBin 0x602BB0
  → LoadVaultSync; the previous bin's vault and its engage activity go — LuaFlow.UnloadBinVault resets it); race_bin_opm's
  are in vaults a career never loads. So only the **current** bin's engage runs (the port ran the beaten bins' too), and
  drive_to_safe_house runs on **every** free-roam start and ends itself at once when SkipCareerIntro (driving → done:
  SetCopsEnabled(true), ShowGPS(false), NotifyActivityFinsihed). EnterBin (in gameplay) runs StartActivitiesCall1(1) and
  the new bin activity the same way. A race activity runs when its race is loaded (LuaFlow.OnRaceBegin, Activity.Run
  semantics; a Lua engage prompt that is up gets AcceptEnterCareerEvent first). EnteringGameplay on entering free roam.
* **Saved state** (§2.2.1): LuaFlow keeps GManager's maps and timers in a GameplayStateStore per loaded profile (a
  profile loaded from the save = LoadGameplayData: its `ScriptStates` / `ScriptTimers`; the same profile object = the same
  GManager, the transient map included). Profile.Save → SaveGameplayData (Profile.Saving hook: every activity's
  SaveState(false), the persistent map and the timers into the profile); GameplayEnded → the runtime's Shutdown (the
  Persistent activities page out, the timers and the Lua sim clock [ours] are kept). **A resumed career no longer repeats
  a bin's entry SMS**: race_bin_NN, speedtraps and milestones come back in their saved state without StateEnter. The
  pending SMS list is saved too (Profile.SmsPending, Sms.SyncPending). Old saves without ScriptStates start the scripts
  over (as before).
* **Triggers** (LuaTriggers): §2.3 TriggerEnter/Exit for the players' and racers' cars against the box / radius / plane
  triggers connected to running activities and their children.
* **The C++ completions** (LuaGameHost): LoadingComplete the frame after NotifyScriptWhenLoaded, CountdownDone when
  RaceManager's countdown and intro are over, NISComplete when the NIS ends (a driven race: the NIS the script asked for,
  RaceManager.NisPlaying), NotifyMovieFinished when no movie / story card is up, NotifyMessageDone when the card closes,
  FlowReadyForOutro / RestartRace / NotifyRaceAbandoned when the results close, NotifyEngineBlown / NotifyVehicleDestroyed
  (PlayerCar.Destroyed) / NotifyRaceTimeExpired / NotifyRaceTimeSecTick from the race, **NotifyKnockedOut for a knockout the
  race made itself** (Game_NotifyLapFinished's lap knockout; not twice for a script's KnockoutRacer), PerpEscaped /
  PerpBusted when the pursuit ends [?], **NotifyMilestoneProgress {MilestoneName, ValueReached} when a pursuit stat
  reaches a new best of the pursuit** (GManager::TrackValue 0x5EDA20: the first value, then only bigger — smaller for
  pursuit_evasion_time, MilestoneTypeInfo flag 1; reset when a pursuit starts [?: NotifyPursuitStarted 0x5EDD10]),
  **ICECameraFinished after the ICE track's Length** (Camera.SetGenericCamera: GENERIC group/track of InGameB.bun; 3.5 s
  for Cinematics/Challenge), **LoadingComplete** once NotifyScriptWhenLoaded was called and nothing loads (GRaceStatus::
  Update 0x603132: racers' models/audio, stock cars, a cop spawn, the track streamer [?: TrackStreamer byte +0x193]; here
  GameFlow.RaceLoading and a blocking world load), **DeclineEnterCareerEvent** when the engage dialog closes without a race,
  NotifyPlayerRep / NotifyChallengePassed when the profile's bounty / challenges grow, NotifyCellCallStarted / Complete (6 s)
  around ECellCall.
* GManager::AddSMS (0x5F5120) ids 10..14, 95, 96 go through DispatchSMSMessage (0x5DDD20): only when not yet received,
  marked unread with the next sort order, played as ECellCall(id) — Sms.Show now shows them as calls and ignores repeats.
* Script errors: `NFSMW lua: error in <handler> (<activity>): …` (warning); the flow goes on with the next handler. Every
  state change is logged (`NFSMW lua: <activity> -> <state> at T s`); LuaFlow.Reset logs `reset: <status>`.
* [ours] Free roam is "no RaceManager, no race loading, a player car, no front-end screen open" (`FrontEnd.IsOpen`).
  Every wait also completes after 20 s (`no C# signal after 20 s for …`); in Drive the 20 s do not run while a movie,
  story card or NIS is on screen.

### Shadow
Query bindings read the live game; action bindings are recorded (`LuaFlow.Calls`, `NFSMW lua: call …` with
`LuaFlow.Verbose`) and nothing is executed. **LuaLedger** compares normalized records of both flows — Goal, Checkpoint,
LapFinished, RaceFinished, Knockout, SMS, UnlockRace, Bounty, RaceStart — within a window (3 s; Goal/Unlock 30 s, SMS 60 s,
RaceStart/Bounty 10 s) and logs `NFSMW lua: divergence at T s: …`. Once the C# race is past its intro and countdown, every
intro wait of the script completes so the script catches up; a script's late Activity.Run of the race already running is
ignored; starting a race stops any other race script still running.

### 4.2 Drive
The scripts run the career; LuaDrive executes their Action bindings and E* events (Core/Lua/LuaDriveTable.cs says which
are Executed, NoOp, FrontEnd — the C# front end does the same job — or missing, logged once as `Drive has no C# for …`).
`LuaFlow.Drives` = Drive mode, career gameplay, a live runtime; a runtime that fails hands the next race back to the C#
rules. A race is driven when it starts while LuaFlow.Drives (single player, not LAN or split screen).

**Races** (RaceManager.Drive.cs implements ILuaRaceControl; LuaFlow.Race is the driven race):
* **Activity.Run** of a race (a Core binding: LuaGameHost routes it to LuaDrive in Drive) answers false and starts the C#
  race instead (GameFlow.StartRace); the activity runs when the race is loaded (OnRaceBegin = BlockUntilLoaded /
  QuickGame's ACTIVITIES state), so InitRacers and GetNumRacers see the grid. A race whose activity is `done` (the
  16_nis_rog_razor NIS shell, a race a chain leaves) ends when the script runs the next one; a race already running or
  loading is not started twice (`ignored: … is running`).
* The grid, cars, AI, route progress, rankings, splits, the lap knockout of Game_NotifyLapFinished, the clock and the
  results screens stay C#. RaceManager.Setup resets every car's damage (DamageRacer::ResetDamage).
* The race **waits for the script**: the intro NIS starts at NIS.Play('Intro') (RaceIntro), the 3-2-1 at
  EShowRaceCountdown, the AI and the launch at Game.StartRaceTimers (`racing` StateEnter); a rolling start (16.1.0, the
  epic pursuit) holds its cars until StartRaceTimers. Fallbacks [ours]: no NIS.Play 20 s → the intro is skipped, no
  countdown 20 s → it counts down, no StartRaceTimers 5 s after GO → started (`NFSMW race: Drive: …`). An event without a
  stategraph is handed back to the C# rules (ReleaseToCSharp).
* **Goals** are the script's: SetRacerGoal is mapped onto the race route (TargetFor / TargetDirectionFor, the AI's
  AITarget, the player's checkpoint marker, the progress cap); TriggerEnter from LuaTriggers moves them on. Port guard
  [ours, as the C# rules]: a racer 60 m past its goal along the route gets the TriggerEnter LuaTriggers would have posted.
  The C# HitGoal, wrong-way reset, speedtrap scoring and overtime penalty, time-out and blown/totalled race ends are off.
* NotifyCheckpointReached → StartCheckpoint (hit = n when n = hit + 1); NotifyLapFinished → lap times, rankings, knockout;
  NotifyRaceFinished → FinishRace, rankings, and for the local human with rank 1 once (mHasBeenWon) **GameSession.AwardWin**
  (Game_NotifyRaceFinished's cash, UnlockRaces, UpdateRaceScore; the rival's fall); AwardCash/AwardPoints → points;
  AwardBonusTime → the tollbooth clock; NotifySpeedTrapTriggered → the camera's speed record; CalculateRanking in a
  speedtrap race runs FinalizeRaceStats first (0x6058B0).
* **The end**: ShowRaceOverSummary → the race-over HUD text, the player brakes; EnterPostRaceFlow → FinalizeRaceStats, the
  AI stops, NoPostRaceScreen → score flag 2 and the race leaves at once, else the photo finish/results (won);
  ShowLosingPostRaceScreen → the results (lost) with Restart (ERestartRace → a new race; LuaGameHost posts RestartRace when
  the screen closes) or Quit (NotifyRaceAbandoned → `abandoned` → Game.AbandonRace → GRaceStatus::RaceAbandoned's boss
  chain reset); a pause-menu restart posts RestartRace from OnRaceBegin; SetRoaming leaves a race still there and saves.
  RaceManager.AfterResults leaves the PostRaceActivity and the abandon to the script; the rival flow (FE) stays.
* WarpPlayerToTrigger, Save/RestoreStartPositions (only a car that moved > 5 m is put back [ours]), Force/ClearAIControl
  (an AIDriver on the route; a car that already has one is left alone [ours]), SabotageEngine → PlayerCar.Sabotage(t)
  (the engine shakes, then blows; WatchRace posts NotifyEngineBlown), BlowEngine → Blow().

**Career** (the hand-ported copies of script rules step aside when `GameSession.ScriptsDrive`): BinScript's SMS,
RaceDatabase.OnUnlockRaces / OnRivalUnlocked / OnBinDone / OnChallengePassed, Story.CheckProgress and the speed-trap
completion of EventMarkers are not called — race_bin, speedtrap_group_monitor and the engage scripts do it through EAddSMS,
Game.UnlockRace, NotifySpeedTrapTriggered (GameSession.CompleteSpeedTrapByScript) and AwardPlayerBounty. What stays C++/FE:
cash and score flags of a win, milestones of a pursuit (GMilestone::NotifyPursuitOver, ScorePursuit), the rival's fall
(PostRivalMode, Bin − 1 = EEnterBin from the FE, the rival flow), the engage dialog and the menu gates. In Drive the
prologue is the scripts' too: Story starts 16.1.0 (its story movie first, as before), the PostRaceActivity chain runs
16.2.2 → 16.2.3 → 16.1.1 → 16_nis_rog_razor (16.8.1) → 16.2.1, EShowMessageScreen shows the DDAY_TIMELAPSE card,
EFadeScreenNoLoadingBarOn/Off hold the screen black, a D-Day race is done at its EnterPostRaceFlow / win (Profile
.EventsDone), 16.2.1's `done` calls Game.JumpToCarLot (Story.JumpToCarLot), race_bin_16/drive_to_safe_house sends SMS 15,
starts the GPS (NavigatePlayerTo → GpsNavigation), SMS 121 after 'hurry', and at the target Game.DoSafeHouseIntro
(Story.SafehouseIntro: spawn, saf25, the rival bio; bin 15). A D-Day race quit is started again (Story.PrologueRaceAbandoned
[ours]). PostRaceResultsScreen leaves a D-Day race to the script in Drive (one-line hook). Game.PlayTutorial plays
drag_tutorial / tollbooth_tutorial / speedtrap_tutorial once (Profile.TutorialsSeen = career flags +0xAC).
GameFlow.StartRace keeps playing a race's IntroMovie before it loads, except when the race has no IntroNIS: then the
script's `intromovie` state plays it (EPlayRaceMovie; the epic pursuit's Storyfmv_her136).

* **RaceStatus:NotifyScriptWhenLoaded** (0x5EEA60) needs no race: it sets mScriptWaitingForLoad (EFadeScreenOn(false) if
  something still loads) and GRaceStatus::Update posts LoadingComplete; Drive accepts it in every state (the QA log's
  "Drive could not run RaceStatus:NotifyScriptWhenLoaded" came from the missing case, round 10).

**World** (new APIs): PursuitSystem.Script.cs — SpawnScriptedCop (LockoutCops(false); pursue: joins or starts the
pursuit, roadBlock: StaticRoadBlock, else Patrol; such a race keeps its police running, other races still start clean), SetWorldHeat (career car + live heat), NoNewPursuits,
PreventBusted, BailPursuit (EBailPursuit); Gameplay/ScriptedWorld.cs — GpsNavigation (Engage/Disengage) and
ScriptedCharacters (SpawnCharacter: the character's PresetRide/CarType at the trigger driving to its TargetMarker at
InitialSpeed mph × 0.44703, UnspawnCharacter, SetTrafficSpeed); NisPlayer.PlayAtMarker (NIS.Play 'Outro' / EPlayRaceNIS: the
scene with the player's car as Car1, its ICE track or an orbit); LuaDrive.Update (WarpToMarkerWhenRoaming).
Game.SendCharacterStimulus / Game.JackKnife (not in the data's flow) are executed since round 11 (§7).

### 4.3 The game-mode activities (Sim/Activities, GameFlow.cs)
* **QuickGame** (ctor 0x6F9800, Construct 0x6F9A80) and **CareerGame** (0x6F9B10 / 0x6FAEC0: a QuickGame with nothing of
  its own) load gameplay through QuickGame::eState (OnManageState 0x6F5140, OnBeginState 0x6F4EA0, IsStateDone 0x6EEF20):
  LOADING_WORLD → PLAYERS (HandleSkipFEOptions, CreatePlayers 0x6F2700, CreateCars 0x6F47A0) → PLAYER_AUDIO
  (ECommitAudioAssets) → PLAYER_MODELS (ECommitRenderAssets) → ACTIVITIES (AvoidableManager, AICopManager,
  AITrafficManager, AIParkedCarSpawner, PathFinder, **GameplayActivity**, Gps, NISListenerActivity) → UI →
  OPPONENT_AUDIO → OPPONENT_MODELS → WAIT_FOR_USER_INPUT → NIS → READY. RaceReset (0x6F4760) turns the speedbreaker
  off. The ctor disables zone switching when the startup race has an IntroNIS, else streams at the start location.
* **GameplayActivity** (ctor 0x6F9EA0): GManager::PreBeginGameplay (0x6035F0: the pending race's custom race and its
  time of day), GRaceStatus::Init, LuaRuntime::Init(0x10000), LuaPostOffice, LuaBindery::BindToGameCode,
  LuaAttributes::BindAccessors, the reset snapshot, **GManager::BeginGameplay** (0x603500: CreateGameplayObjects once,
  the startup race's CreateRaceActivity, ConnectRuntimeInstances, StartActivities 0x5F9E70, RefreshEngageTriggerIcons,
  RefreshSpeedTrapIcons, SpawnAllLoadedSectionIcons, GRaceBin::EnableBarriers when there is no startup race,
  MEnteringGameplay). OnTask (0x6EAAE0): MNotifySimTick(time, step) and GManager::Update every sim frame. The destructor:
  EndGameplay and the Lua shut down.
* Port (GameFlow): `GameFlow.Game` (None / QuickGame / CareerGame) and `GameFlow.State` follow those states (EnterWorld =
  world + players + models, Systems.Ensure = the managers, `GameplayBegan` = GameplayActivity, RaceManager.Begin = the
  startup race's opponents and NIS); `EndGameplay` (QuitToMainMenu, the D-Day car lot's EQuitToFE) raises
  `GameplayEnded`, which resets the Lua. The port keeps the world loaded between a career race and free roam (no reload per
  engage) and runs the scripts in career only [ours].
* **The track streamer's loading bar** (main loop 0x64B770): in the running sim (Sim::GetState() == 3) TrackStreamer::
  CheckLoadingBar turning on → FEManager::RequestPauseSimulation("TrackStreamerLoadingBar") + EFadeScreenOn(true); off →
  EFadeScreenOff(0x16A259) + RequestUnPauseSimulation. Port: GameFlow subscribes to WorldStreamer.LoadingBarChanged
  (in gameplay, State Ready): GameFlow.RequestPauseSimulation → GameBootstrap holds Time.timeScale at 0 (one-line hook),
  StoryCard.LoadingBar fades to black [ours: no bar drawn; released after 30 s or when gameplay ends].
* Game.SetChanceOfRain (0x6054A0) → Core.Precipitation.SetChanceOfRain (wired in round 9).

## 5. Proof (harness)
### 5.1 `lua run`, `lua selftest`
* All 258 chunks undump; all 258 handler functions are defined; `lua selftest` checks 16 hand-assembled programs for
  the opcodes the data does not use (MUL, DIV, POW without __pow, NOT, SETLIST, CLOSE, varargs, TFORLOOP, TAILCALL),
  float32 rounding, %.14g, coercion, comparisons and metamethods.
* `lua run`: 313 activities × up to 15 scenario variants (4227 runs, 43 M instructions) against a stub game that answers
  queries and posts the C++ completions. 218 of the 261 races (variant win) run from `initial` to `done`; the rest are
  challenge/pursuit races that wait for pursuit milestones the stub does not simulate. **All 258 handlers ran; 0 errors**,
  except 8 in `speedtrap_challenge_engage`, a template gameplay.bin never instantiates.

### 5.2 `lua drive [win|lose|quit] [verbose]` — the career under Drive (NFSMWStaging/Harness/LuaDriveProbe.cs)
One stategraph runtime for the whole career and a stub game that executes the action bindings with the Drive rules of
§4.2 (same LuaDriveTable): New Career → 16.1.0 → the scripts' D-Day chain → JumpToCarLot → race_bin_16 /
drive_to_safe_house → DoSafeHouseIntro → bins 15 … 1: a stub player engages every world race through its engage trigger
(the C# dialog accepts), drives through the speed traps (the script awards them), evades pursuits and earns milestones
(the C++ path) until race_bin unlocks the rival, then races the boss chain (PostRaceActivity); the rival's fall lowers the
bin. Result (2026-10-24, all three variants): **the prologue (5 D-Day races + the 16.8.1 NIS shell), 169/169 career races
won, every rival beaten (bins 15 → 1 and the epic pursuit), 39 speed traps completed by speedtrap_group_monitor,
0 script errors, 0 Drive calls without C#** — win: 175 race starts; lose (every third race lost once and restarted from
the losing screen): 223 starts, 48 restarts; quit (every fourth race lost once and quit, re-engaged later; boss chains
reset by RaceAbandoned): 215 starts, 29 quits. `lua drive dday` prints the D-Day events' movies/NIS/NoPostRaceScreen,
`lua drive traps` the speed traps of `speedtraps.SpeedTrapList` (all 39 are listed). The stub's physical side (driving
times, the player's win trigger in the epic pursuit, pursuit outcomes) is invented; what it proves is the scripts'
decisions under Drive's bookkeeping.

### 5.3 Round 10: resume, lost races, boss chains (`lua drive [win|lose|quit|resume|resume-lose|resume-quit|resume-nostate]`)
The stub now follows the game's session model: one GameplayStateStore (GManager) per career; Profile.Save =
SaveGameplayData (at SetRoaming's EAutoSave and before every quit), the D-Day car lot ends gameplay (new runtime, same
store), `resume*` quits to the main menu and loads the save (new runtime over a store rebuilt from the saved persistent
blocks = LoadGameplayData) after every second race and at every new bin; StartActivities is GActivity::Run of the loaded
AutoStart activities and the bin activity (§4.1). It counts SMS sent again within 10 s of a resume, bin activities that
start over at `initial`, and boss races chained by PostRaceActivity. Result (default set, 2026-10-24):

| variant | race starts | restarts / quits | sessions (resumes) | SMS again after resume | bins restarted | boss chain steps | result |
|---|---|---|---|---|---|---|---|
| win | 175 | 0 / 0 | 2 (0) | 0 | 0 | 23 | 169/169, rivals 15 → 1 |
| lose | 223 | 48 / 0 | 2 (0) | 0 | 0 | 23 | 169/169 |
| quit | 215 | 0 / 29 | 2 (0) | 0 | 0 | 30 | 169/169 |
| resume | 175 | 0 / 0 | 79 (77) | 0 | 0 | 23 | 169/169 |
| resume-lose | 223 | 48 / 0 | 79 (77) | 0 | 0 | 23 | 169/169 |
| resume-quit | 204 | 0 / 29 | 82 (80) | 0 | 0 | 23 | 169/169 |
| resume-nostate (a save without script states: the old port) | 175 | 0 / 0 | 79 (77) | **79** | **76** | 23 | FAILED as expected |

0 script errors and 0 Drive calls without C# in every variant; `lua run` (4227 scenarios) and `lua selftest` (18 checks,
now with bRandom and the state-table round trip) unchanged. `lua persist` lists the Persistent / AutoStart / FreeRoamOnly
activities, `lua order` the vault of each activity and the handler groups, `lua ice [group]` the GENERIC ICE tracks.

## 6. Not verified / open
* Drive's resume, the loading-bar pause and the new posts (LoadingComplete gating, MilestoneProgress on a new best,
  ICECameraFinished after the track, DeclineEnterCareerEvent) have not run in the Unity player yet.
* BuildMessageTable per message class: field names are those the scripts read; the exact set the exe writes is not traced.
* The PerpEscaped/Busted and NotifySpeedTrap posting points (MPerpEscaped 0x405C40 / MNotifySpeedTrap 0x604350 kinds; the
  posters in AIPursuit and the speed-trap screen not traced) [?].
* TrackStreamer byte +0x193 in GRaceStatus's load wait (which near loads hold LoadingComplete) [?].
* TrackValue's reset at a pursuit start (GManager::NotifyPursuitStarted 0x5EDD10 → ResetMilestoneTrackingInfo) [?].
* SetTrafficSpeed's units and second float [?].
* The Lua sim clock across runtimes (kept in the store [ours]; the exe's Sim::GetTime is not the Lua's).
* bRandom's sequence (one default seed for the whole game).
* ~~StartRaceFromInGame's bin suspend~~ and ~~the quick-race GameplayActivity~~: ported in round 11 (§7); neither has run in the Unity player yet.

## 7. Round 11: the last bindings, quick-race scripts, the race sheet's bin suspend, triggers on WTrigger

* **Game.SendCharacterStimulus(character, name)** (0x60A6E0): GCharacter::GetSpawnedVehicle (0x5DD290) → the simable's IVehicle
  (interface 0x4040D0; its vtable +4 gives the owner) → the IVehicleAI (0x402830) → vtable +0x74 (the stimulus receiver) →
  `SendStimulus(stringhash32(name), 0, Sim::GetTime(), 2)`. **Game.JackKnife(character)** (0x605030): Hermes::Message::Post of
  **MJackKnife** ("MJackKnife" 0x8912E4, _GetKind 0x604670) to the spawned vehicle's AI → AIActionJackKnife (0x4071C0: a tractor
  swings its trailer). No gameplay.bin handler calls either (they are for the opm test races); Drive now executes both:
  `ScriptedCharacters.SendStimulus` (logs, raises `StimulusSent`) and `.JackKnife` (logs, raises `JackKnifeRequested`) — hooks for
  the AI / TrafficPhysics owners. LuaDriveTable: Executed. **No binding is "missing" any more** (every Drive call has C#).
* **Quick-race scripts** (§4.3 / §6): QuickGame's GameplayActivity runs the stategraphs for quick races too, with the startup race
  the only activity run (StartActivities with a startup race: GActivity::Run of it, no AutoStart activities). `LuaFlow.ScriptedMode`
  = career, or quick race when `LuaFlow.QuickRaceScripts` (default on; `-lua careeronly` off); `Drives` and `Ensure` follow it; the
  LuaFlowDriver resets the runtime on any change of game mode out of career / quick race. In a quick race nothing but the race
  activity runs (LuaFlow.Tick's bin / roaming logic is career-only); RaceManager's Driven path is the same (LAN, split screen and
  the Challenge Series stay on the C# rules — their tests depend on Systems.Police.BeginChallenge). The quick race's win still
  goes through GameSession.QuickRaceWon (RaceManager.End). [ours: not run in the player yet — check the QA tour's quick race.]
* **StartRaceFromInGame** (0x5FDCB0; gameplay_core.md §17.2): a race of another bin started from the safehouse race sheet / the
  rival screen suspends the running `race_bin_*` activities (SaveState + Suspend) and sets mRefreshBinAfterRace; when roaming
  again EnterBin runs them back (`LuaFlow.OnStartRaceFromInGame`, `_refreshBinAfterRace` in Tick). Resolves §6's open item.
* **Triggers**: LuaTriggers now owns a `GameTriggers` (Runtime/Game/Triggers.cs) over `Core/WTrigger.cs` — the exe's trigger
  volumes (world_interaction.md §11): GTriggers with activation references for the running activities' connected / child
  triggers, the WTriggerManager scan of the players' and racers' cars each frame (velocity look-ahead, OBB for boxes, the
  cylinder's height band, the fire-on-exit list), NotifySimableTrigger's Enter (first time) / Inside (every tick, C# event only) /
  Exit (FireOnExit). Game.ResetTrigger → GTrigger.Reset (the inside list and the exit records forgotten).
* `Game.SkipCareerIntro` now also answers the SkipCareerIntro switch (0x926125) the skip-D-Day cheat sets (misc.md §2);
  `GRaceParameters::GetIsAvailable`'s unlock-all switch (DAT_00926124) is `EasterEggs.UnlockAllThings` (RaceDatabase.IsAvailable).
* Harness: `wtrigger selftest` (the shapes of all 3185 trigger records, a box crossing at 40 m/s / 30 Hz with its exit, a miss
  beside the box, a directional trigger, a simple body in a cylinder, a OneShot trigger) passes; `lua run` / `lua drive` unchanged
  (the stub posts TriggerEnter itself).

## 8. Round 9 (final): the last NoOp bindings, the C API account (lapi.c / lstate.c), the runtime plumbing

### 8.1 Bindings / events now executed in Drive (gameplay_core.md §18.2-18.3)
Audio.SetFlag (0x6048E0: ESndGameState + SetSoundControlState by the jump table), Game.ShowTriggerIcon / HideTriggerIcon (0x604D20 /
0x604D30), EResetProps (World_RestoreProps). Re-verified NoOp: MiniMap.AddEngagedRace, Game.SetRacerLapsLeft, DoSpecialFinalization,
ReloadWorld (EReloadGame; the port never asks for it, SetCurrentTimeOfDay / DoSpecialSetup answer false), SetHasRapSheet, E911Call,
EDispIntroRace. `lua drive` still reports "missing: none" and DRIVE OK.

### 8.2 lapi.c / lstate.c — the C API the bindings reach, against the C# VM (Core/Lua)
The exe's bindings marshal through the Lua 5.0.1 C API (LuaBindery's binders 0x61A270.. call lua_gettop 0x605A50, lua_tonumber
0x60B060, lua_pushnil / pushnumber / pushboolean 0x605EB0 / 0x605ED0 / 0x605EF0, lua_settable 0x60E370, lua_newuserdata 0x614F10, and
the unnamed rest of the 0x6059E0-0x615320 range). What a script can observe, and where the C# VM does it:
* argument access and conversion — lua_gettop / lua_type / lua_isnumber / lua_isstring / lua_tonumber (luaV_tonumber: numbers and
  numeric strings, else 0) / lua_toboolean (nil and false are false) / lua_tostring (strings, numbers as "%.14g", else NULL) /
  lua_touserdata: StategraphRuntime.Arg ('i' truncates like __ftol, 'f', 'b' = IsTrue, 's', 'I' = the GRuntimeInstance userdata),
  LuaArgs.Number / Bool / String / UserData, LuaState.ToNumber / ToStr, LuaNumber.ToString ("%.14g").
* results — lua_pushnil / pushnumber / pushboolean / pushstring / pushlstring / pushlightuserdata / newuserdata (+ setmetatable for
  the instance metatable, LuaBindery::LoadMetatable 0x618970): LuaValue.Nil / Number / Bool / String / Light / UserData with the
  LuaUserData metatable (selftest "userdata __index __eq").
* tables — lua_newtable / gettable / settable (luaV_gettable / settable with __index / __newindex) / rawget / rawset / rawgeti / rawseti
  / lua_next / getmetatable: LuaState.GetTable / SetTable, LuaTable.Get / Set / Next (Table.Randomize, the state tables).
* calls — lua_call / lua_pcall (a handler's error is caught and logged; the activity goes on), lua_error, lua_load (luaU_undump of the
  gameplay.bin chunks; selftest "undump header"), lua_concat, lua_equal / lessthan / rawequal: LuaState.Call / CallAt / Error,
  LuaUndump.Load, the VM's OP_CONCAT / EQ / LT / LE.
* state — lua_open (0x6156A0) / lstate.c f_luaopen / luaE_newthread / luaE_freethread / lua_close: one LuaState per runtime
  (StategraphRuntime), dropped with it; luaL_openlib (0x619530) / luaL_ref (0x6165A0) and the auxiliary buffers are only reached at
  start-up (the binding tables: LuaBindingTable / LuaState.Register / Library).
Not reachable from the shipped scripts (the bytecode reads only Activity, Audio, Bin, Camera, ChangeState, Debug, Demo, the E* events,
Game, HUD, MiniMap, NIS, RaceStatus and Table: harness `lua scan`): coroutines (lua_newthread / lua_xmove / resume), the debug API
(lua_getlocal 0x60B200, lua_setlocal 0x60B2A0, lua_getupvalue 0x606190, lua_setupvalue, hooks), lua_dump, lua_cpcall, lua_atpanic,
lua_pushvfstring / pushfstring beyond error text, the GC tuning calls (lua_getgcthreshold / getgccount / setgcthreshold: the C# GC),
lua_checkstack (the C# stack grows), lua_getfenv / setfenv (no script changes an environment), lua_pushupvalues, lua_version.

### 8.3 LuaRuntime / LuaBindery / LuaPostOffice / LuaAttributes plumbing
LuaRuntime ctor / Init / CreateState (0x61F3B0 / 0x61F490 / 0x61D250: the Lua heap and the state), TakeResetSnapshot (0x609F20: the
heap pager's snapshot for a reset — StategraphRuntime is rebuilt instead, LuaFlow.Reset), DumpStack (0x6182F0: the debug stack dump on
an error; the port logs the C# exception); LuaBindery::Init / GetGlobalTable / LoadMetatable (0x6040C0 / 0x618A40 / 0x618970: the
binding tables, LuaBindingTable); LuaPostOffice ctor / Init / UnregisterHandler (0x61F8A0 / 0x61F940 / 0x61FD40: the message
subscriptions — StategraphRuntime.Post delivers to the current states' handlers directly, RouteMessage §17.3); LuaAttributes::Init
(0x61F750: the attribute accessors' registration; Core/Lua reads the attributes through AttribDatabase). None has a script-visible
effect beyond what §8.2 lists.
* Round 9 follow-up: ECellCall now also runs SpeechAI.SMSCellCall (0x62B1CD), and NotifyCellCallStarted / Complete come from CopSpeechManager.CellCallStarted / CellCallCompleted instead of a 6 s timer (gameplay_core.md §18.7).
