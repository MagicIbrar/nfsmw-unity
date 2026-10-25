# Gameplay core: GRaceStatus, GRaceDatabase, GManager, GActivity, GTrigger, GIcon, smackable breakers (NFS MW 2005 PC v1.3)

Recovered from speed.exe (Ghidra via REA, capstone for functions Ghidra does not know), the IDA symbol table
`refs/nfsmw/config/SPEED_EXE_1_3/symbols.json`, the GameCube headers (`refs/nfsmw/src/Speed/Indep/Src/Gameplay/*.h`) and
the stategraph Lua (`career_stategraphs.txt`). It extends `career_flow.md`, `races.md` and `ai_racer.md` §3-4/§7 and
corrects them where the exe disagrees (marked **correction**). Inferred items are marked [?]. Attribute names were
resolved from the hashes with `vlt_names_all.txt` (`jhash`).

Most race rules are not in C++. They are in the `basicrace` stategraph (Lua bytecode in gameplay.bin), which calls
Lua bindings (`Game.*`, `RaceStatus:*`) registered in FUN_0061e750 (§7). The C++ keeps the bookkeeping: racer
records, timers, rankings, distances, catch-up, adaptive difficulty.

## 0. PC layouts (they differ from the GC headers)

**GRacerInfo**: 0x194 bytes (GC: 0x1A8; PC packs the bools into bytes).

| off | field | off | field |
|---|---|---|---|
| 0x00 | mhSimable | 0x74 | mSpeedTrapsCrossed |
| 0x04 | mGameCharacter (0 = human) | 0x78 | mSpeedTrapSpeed[16] (km/h) |
| 0x08 | mName | 0xB8 | mSpeedTrapPosition[16] |
| 0x0C | mIndex | 0xF8 | mDistToNextCheckpoint |
| 0x10 | mRanking (1-based) | 0xFC | mDistanceDriven |
| 0x14 | mAiRanking (0 for humans) | 0x100 | mTopSpeed |
| 0x18 | mPctRaceComplete (0..100) | 0x104 | mFinishingSpeed |
| 0x1C/1D/1E/1F | mKnockedOut / mTotalled / mEngineBlown / mBusted | 0x108 | mPoundsNOSUsed |
| 0x20/21/22 | mChallengeComplete / mFinishedRacing / mCameraDetached | 0x10C | mTimeCrossedLastCheck |
| 0x24 | mPctLapComplete | 0x110 | mTotalUpdateTime |
| 0x28 | mLapsCompleted | 0x120 | mPointTotal (speedtrap points) |
| 0x2C | mCheckpointsHitThisLap | 0x124 / 0x128 | mZeroToSixtyTime / mQuarterMileTime |
| 0x30 | mTollboothsCrossed | 0x12C / 0x13C | mSplitTimes[4] / mSplitRankings[4] |
| 0x34 | mTimeRemainingToBooth[16] | 0x14C / 0x158 / 0x164 | mRaceTimer / mLapTimer / mCheckTimer (GTimer: start, total, running) |
| | | 0x170..0x18C | saved start position / heat / direction / speed |
| | | 0x190 | mDNF |

**GRaceStatus** (singleton `fObj` 0x91E000): mRacerInfo[16] at 0x18, mRacerCount 0x1958, mIsLoading 0x195C, mPlayMode
0x1960 (0 roaming, 1 racing), mRaceContext 0x1964 (0 quick race, 1 online, 2 career), mRaceParms 0x1968, mRaceBin 0x196C,
mRaceMasterTimer 0x1970, mPlayerPursuitInCooldown 0x197C, mBonusTime 0x1980, mTaskTime 0x1984, mSuddenDeath 0x1988,
mTimeExpiredMsgSent 0x1989, mActivelyRacing 0x198A, mLastSecondTickSent 0x198C, checkpoint model/emitter 0x1990/0x1994,
mQueueBinChange 0x1998, mWarpWhenInFreeRoam 0x199C, mNumTollbooths 0x19A0, mScriptWaitingForLoad 0x19A4,
mRefreshBinAfterRace 0x19A5, mCheckpoints 0x19A8, mNextCheckpoint 0x19B8, mLapTimes[10][16] 0x19BC, mCheckTimes 0x1C3C,
mSegmentLengths[18] 0x443C, fRaceLength 0x4484, fFirstLapLength 0x4488, fSubsequentLapLength 0x448C,
mCaluclatedAdaptiveGain 0x4490, fCatchUpIntegral/Derivative 0x4494/0x4498, **fCatchUpAdaptiveBonus 0x449C**,
fAveragePercentComplete 0x44A0, catch-up skill/spread tables 0x44A4/0x44D4, nSpeedTraps 0x4504, aSpeedTraps 0x4508,
bRaceRouteError 0x4549, mTrafficDensity 0x454C, mTrafficPattern 0x4550, mHasBeenWon 0x4554.

GManager (0x91E00C): milestones 0xA4/0xA8 (20-byte GMilestone), speed traps 0xAC/0xB0 (20-byte GSpeedTrap), bounty spawn
markers 0xB4.., freeroam start marker 0x108, from-safehouse marker 0x10C, start-from-safehouse 0x110, in-gameplay 0x118,
override start marker 0x11C, timers 0x144 (8 × 0x24), pending SMS list 0x2A8/0x2AC, icon flags 0x2C8..0x2D0
(AllowEngageEvents 0x2CE, AllowEngageSafehouse 0x2CF, AllowMenuGates 0x2D0), restart event 0x2D4.

## 1. GRaceStatus — race bookkeeping

### 1.1 Update (0x602C80), every sim tick via GManager::Update (0x6036C0)
1. `Racing && mRefreshBinAfterRace` → mQueueBinChange; `Roaming && mQueueBinChange` → EnterBin (0x602BB0); roaming with a
   queued warp → GManager::WarpToMarker.
2. Racing with racers:
   * `nAI` = racers with a character. For every racer: GRacerInfo::Update (0x5E92E0, §1.2), then
     `fAveragePercentComplete = Σ w·pct / Σ w`, w = 1 for AI, w = nAI for humans (w = 1 when there is no AI).
   * CalculateRankings (§1.4); UpdateSplits per racer (0x5E3400, §1.5).
   * Post the race-time message; once per whole second of master time post `NotifyRaceTimeSecTick` (the speedtrap
     overtime penalty runs off it, §2.6).
   * Time limit (`TimeLimit` > 0): challenge races (type 8) pause the master timer while the player's pursuit is in
     cooldown. When `GetRaceTimeRemaining() <= 0` the first time: post `NotifyRaceTimeExpired`, and stop every human who
     has not finished.
3. While the script waits for loading: when no model/audio/track loading is in progress, post `LoadingComplete`.

### 1.2 GRacerInfo::Update (0x5E92E0) — progress
Skipped once finished, blown, totalled or knocked out. Accumulates NOS used, speed-breaker time, top speed, distance
driven, update time; `mQuarterMileTime` at 402.335 m driven, `mZeroToSixtyTime` at 26.8218 m/s.
`mDistToNextCheckpoint = IVehicleAI::GetPathDistanceRemaining()` (the racer's nav path to its goal, §2.3). Then, when the
race length is known:

```
prior   = (laps>0 ? FirstLap : 0) + max(laps-1,0)·SubsequentLap          laps = mLapsCompleted
done    = Σ_{i<hit} Segment(i, laps)                                      hit  = mCheckpointsHitThisLap
if dist != 0:  d = Segment(hit, laps) − dist + done
               pctLap  = LapLength(laps) > 0 ? clamp01(d / LapLength(laps))·100 : 100
               pctRace = clamp01((d + prior) / RaceLength)·100
```
`Segment(i, lap)` (0x5DC720) = mSegmentLengths[i], except segment 0 on laps ≥ 1 = `+ Subsequent − First` (= the
finish→first-checkpoint leg). `LapLength(0)` = FirstLap, else SubsequentLap (0x5DB420).

### 1.3 DetermineRaceLength (0x5FEF30), DetermineRaceSegmentLength (0x5FE840)
Points: [0] start, [1..n] checkpoints, [n+1] finish (needs a finish line). Segments: non-looping n+1 (start→…→finish);
looping (`IsLoopingRace`) n+2, the last one finish→checkpoint 1. Segment length = RaceRoute A* (`WRoadNav` path type 6,
`FindPathNow`) between the two points; the search is repeated with each shortcut found on the path disallowed until the
path has no shortcut, and the shortcut roads are annotated with their distance [?]. A failed search sets bRaceRouteError.
```
non-looping: RaceLength = FirstLap = SubsequentLap = Σ seg
looping:     SubsequentLap = Σ seg − seg[0];  FirstLap = Σ seg − seg[n+1];  RaceLength = (NumLaps−1)·SubsequentLap + FirstLap
```
Then it walks 100 m from the finish marking segments InRace (0x8000) / direction bit 4, and collects the race speed traps
(runtime instances with flag 0x100 and `OpenWorldSpeedTrap` false) into aSpeedTraps.

### 1.4 Rankings
* **CalculateRankings** (0x5FDE80): bubble sort. With `RankPlayersByPoints` (speedtrap): compare `mPointTotal`
  (higher first) **only when the two totals sum to more than 1**, otherwise `IsBehind`. Then mRanking = i+1;
  mAiRanking = 0 for humans, else rank minus the humans ahead.
* **GRacerInfo::IsBehind** (0x5E31D0, unnamed in the symbol table; it is the sort comparator), first rule that applies:
  1. both finished → the larger race time is behind; a finished racer is ahead of an unfinished one.
  2. both knocked out / both blown / both totalled → the one whose race timer stopped earlier is behind; both DNF → by
     pct (as compiled: `rhs.pct < this.pct` → behind [?]).
  3. a knocked-out / blown / totalled racer is behind one that is not.
  4. fewer laps completed is behind; then lower `mPctRaceComplete`.
* **SortCheckPointRankings** (0x5DBD80): for each speed trap slot, mSpeedTrapPosition = rank by mSpeedTrapSpeed (desc).

### 1.5 Splits, laps, checkpoints, finish
* **UpdateSplits** (0x5E3400): split k (0..3) is recorded when pct ≥ 20/40/60/80 (time = race timer, position = rank).
  **correction** to races.md ("assume 20/40/60/80"): confirmed.
* **StartRace** (0x6032A0): race timer starts at `StartTime` (gameplay attr; 16.1.0 = 86 s) → StartLap(1).
* **StartLap(n)** (0x602200): lapsCompleted = n−1, hit = 0, restart the lap timer, StartCheckpoint(0).
* **StartCheckpoint(i)** (0x600660): `if i == hit+1: hit = i`; restart the checkpoint timer; mTimeCrossedLastCheck =
  race time; dist = 0.
* **FinishRace** (0x5E97B0): stop the race timer, mFinishedRacing, mFinishingSpeed = |v|.
* **KnockOut / TotalVehicle / Busted / BlowEngine** (0x600210/0x6002C0/0x600370/0x600420): only if not finished; set the
  flag, stop all three timers, CalculateRankings.
* **StartMasterTimer** (0x5FDFF0): master clock = StartTime; **GetRaceTimeRemaining** (0x5FE090) =
  `max(0, TimeLimit + mBonusTime − master)` (0 when there is no limit). **AwardBonusTime** (0x5FE600): every racer
  records the time remaining in `mTimeRemainingToBooth[mTollboothsCrossed++]` (the TOLLBOOTHS results page), then
  `mBonusTime += seconds`.
* **FinalizeRaceStats** (0x6021B0, not online): per racer FUN_006007B0 — unfinished racers get a **projected time**
  `t / (pct/100)` (pct > 0.1); drag racers that are totalled, blown or under 0.4 % are DNF (time 0); a speedtrap AI loses
  `(projected − t) · OvertimePenaltyPerSec` points; looping races fill the last lap time; AI splits that were not reached
  are `projected · 0.2/0.4/0.6/0.8`; non-DNF racers are then finished at the projected time. Then
  UpdateAdaptiveDifficulty(FinalizeRace).
* **GetWinningPlayerInfo** (0x5EB4E0): the human that finished with rank 1.

### 1.6 Race goals for the AI (correction to ai_racer.md §3 "[?]")
`Game.SetRacerGoal(index, trigger)` (0x611700): `AITarget::Aquire(trigger position, trigger direction)` on the racer's
IVehicleAI, cancel its path finding (so the nav replans to the new goal); for the local player also
`SetNextCheckpointPos(trigger)` (0x5FE660: places the checkpoint marker model 0x738B1F9B at the trigger, or removes it
when `CheckpointsVisible` is false). The goal sequence per racer is kept by the script (§2.3): checkpoint 1..n, then the
finish, wrapping to checkpoint 1 on circuits. So the AI target is always **the racer's own next goal**, not the next goal
ahead of its distance.

### 1.7 Catch-up (MakeCatchUpData 0x5FFCC0, ComputeCatchUpSkill 0x5FF990)
As races.md §5.3 / ai_racer.md §7. The inputs are now identified: `t` = GRacerInfo+0x18 (pct complete), and the PidError
fields. MakeCatchUpData runs at Game.StartRace (0x60DBD0) after DetermineRaceLength.

### 1.8 Adaptive difficulty (resolves ai_racer.md §10 "GetAdaptiveDifficulty 0x5DC180 [?]")
* `GetAdaptiveDifficutly` (0x5DC180) = `fCatchUpAdaptiveBonus` in career context, else 0.
* `SyncronizeAdaptiveBonus` (0x5DC1A0, at Game.InitRacers) = `clamp(careerSettings.short@+0x10 / 32767, −1, 1)`
  (the bonus persists in the career save as a signed 16-bit fraction; written back by FUN_005DB470).
* `UpdateAdaptiveDifficulty(reason, who)` (0x5FF2E0, the symbol table calls it `GRaceStatus::__unk`). Runs once per
  race (mCaluclatedAdaptiveGain) and only when: career context, ≥ 2 racers, single player, racing, the event shows a
  post-race screen, race length > 0, no human busted. `R(x,a,b) = clamp01((x−a)/(b−a))` (UMath::Ramp 0x401AE0); the
  result is clamped to [−1, 1].

| reason | rule |
|---|---|
| 0 engine blown, 1 vehicle destroyed (who must be the player), 5 restart | over racers not knocked out (5: not knocked/totalled/blown): H = max human pct, A = max AI pct; if A > H > 0: `b += −0.4·R((A−H)·len·0.01, 0, 300)·H·0.01` |
| 2 finalize, 3 knocked out, 4 racer finished — speedtrap race | only when every racer finished; P = human points, M = max AI points, f = (P−M)/max(P,M): `f ≤ 0: b −= 0.2·R(−f, 0, 0.2)`, `f > 0: b += 0.2·R(f, 0.05, 0.2)` |
| 2/3/4 — a human won | gap = (100 − best pct of unfinished, unwrecked racers)·len·0.01: `b += 0.4·R(gap, 200, 750)` |
| 2/3/4 — an AI won | for each unfinished human: d = (100 − pct)·len·0.01: `b += (−0.3·R(d, 0, 300) − 0.1)·pct·0.01` |
| 2/3/4 — a human knocked out (knockout race) | d = fmod((100 − pct)·len·0.01, len/laps): `b += (−0.3·R(d, 0, 300) − 0.1)·pct·0.01` |
| otherwise | no change, not marked computed |

Use (ai_racer.md §7): `base = clamp(cs + d·(d > 0 ? Up(cs) : Down(cs)), 0, 1)`.

### 1.9 SetRoaming (0x5FE3E0), RaceAbandoned (0x603220), EnterBin (0x602BB0)
* SetRoaming: play mode 0, context career, clear race params, reset shortcuts, restore cops, autosave (EAutoSave) and a
  post-race event unless the race was 16.2.1, clear rain override (except D-Day), respawn icons.
* **RaceAbandoned**: career, a bin, a race → if the race is a **boss race (not the epic pursuit), the career-complete
  flag of every boss race of the bin is reset** (the rival chain restarts). Confirms races.md §4.7's guess.
* EnterBin(n): reset topology/barriers/race segments, `mRaceBin = bin n`, GRaceBin::EnableBarriers, start the bin
  activity if not running, refresh engage and speed-trap icons.

## 2. The race flow (stategraph `basicrace`, career_stategraphs.txt) and its C++ hooks

### 2.1 States
`initial` (CareerRace = Game.IsCareerMode; time of day / DoSpecialSetup may reload the world; SetCopsEnabled(CopsInRace),
ChanceOfRain, ClearRacers, SetRaceActivity, **InitRacers** §2.2, PlayerBusted=false) → `tutorial` (skipped for DDayRace;
else Game.PlayTutorial) → `intro_message` → `intromovie` → `intronis` (career boss race: the IntroNIS, else the
QuickRaceNIS; `SaveStartPositions` on exit) → `prerace` (§2.3) → `countdown` (DoCountdown: SetAllStaging(true),
EShowRaceCountdown; RollingStart: ForceAIControl(0/1); on `CountdownDone` → `racing`) → `racing` → `raceover` /
`postrace` → `outronis` → `outro_message` → `outromovie` → `done`.
* `racing` enter: Game.StartRaceTimers, RaceOverMessageDuration = 3.
* `done`: DisableBarriers, EnableBinBarriers; abandoned → SetRoaming; otherwise in career with a **PostRaceActivity** →
  `Activity.Run(PostRaceActivity)` (the boss chain / prologue chain); else SetRoaming.
* `abandoned` (quit from the pause menu or the losing post-race screen): SetActivelyRacing(false),
  SetCopsEnabled(true), **Game.AbandonRace** (0x60DEB0: stop the master timer, clear checkpoints, unspawn the
  characters, IRacer stop) → the RaceAbandoned rule of §1.9 → `done`.

### 2.2 InitRacers (Lua binding 0x61DBE0)
ClearRacers, SyncronizeAdaptiveBonus, clear stock cars, reset shortcuts, cops off; every local player vehicle is added
first (Game_AddPlayer). Single player and not a tollbooth: `numOpponents + 1 − racers` AI from `Opponents[i]`
(AddRacer 0x601F90). In career, `m = max(character.B1ECE070)` over them (MinimumAIPerformance).
* **Generic opponent cars** (resolves races.md §5.1 "rule not found"): `perf = max(m, every player's
  Physics::Info::Performance)` per axis, then `Game_FindPerformanceCandidates(list, 3, perf)` (0x61D8E0):
  `perf' = clamp01(perf + 0.2)` per axis; candidates = every vehicle whose **stock** performance is ≤ perf' on all three
  axes and whose pvehicle `RandomOpponent` is true; shuffle; keep up to 3. Each AI racer without a PresetRide/CarType
  takes the next candidate (cycling). GRacerInfo::CreateVehicle (0x5E8E60): PresetRide (FindFEPresetCar) > CarType >
  candidate; non-preset cars get random visual parts. RandomOpponent cars: clio db9 monaro gallardo eclipsegt slr
  911turbo mustanggt elise 997s gti tt viper punto sl500 rx8 lancerevo8 supra cts is300 corvettec6r gto rx7 clk500
  cobaltss fordgt caymans corvette murcielago (+ a3 a4 bmwm3gtr carreragt imprezawrx, not in the car lot).
* **Grid** (correction to races.md §2 "2 columns, 3.2 m / 7 m"): the player is at `racestart`. Each AI is placed by a
  WRoadNav started at racestart facing backwards: `IncNavPosition(7 m)` per AI (so 7, 14, 21 m behind along the road),
  lateral offset `±1.5 m` from the nav centre line, sign flipping per AI. A character's `ForceStartPosition` marker
  overrides its slot.
* **Names** (AddRacer): career boss race → `BLACKLIST_RIVAL_%02d_LEADERBOARD` (bin) for every AI (ChooseBossName
  0x600730); career → the character's `RacerName` string (0x5DD2A0); else a random unused `RACERNAME_%03d` (0..149)
  (0x5DC800).

### 2.3 prerace and racing (the rules)
`prerace` enter (#1224): per racer i: GoalList = `Checkpoint` (or the character's `CannedPath`), GoalCount, NextGoal = 1,
LapsLeft = NumLaps, InRace = true, `SetRacerGoal(i, GoalList[1])`. `NumAllowedForLaps[NumLaps] = NumRacers+1`, then
downwards `− KnockoutsPerLap` (min 1) (HUD only [?]). `RaceStatus:ClearCheckpoints`, AddCheckpoint(each checkpoint),
AddCheckpoint(racefinish). Warp to racestart unless coming from the NIS; RestoreStartPositions; NotifyScriptWhenLoaded.
`LoadingComplete` → RestoreStartPositions, **Game.StartRace** (0x60DBD0: traffic density = ForceTrafficDensity if > 0
else TrafficLevel, drag = 0; traffic pattern; EnableBarriers; **DetermineRaceLength; MakeCatchUpData**; heat =
max(ForceHeatLevel, current)), SetActivelyRacing(true) → countdown or racing.

`racing` TriggerEnter (#1661), racer = Game.GetRacerIndex(element):
* A trigger with `ResetsPlayer` (the `wrongway` plane behind the grid) entered by the player → WarpPlayerToTrigger
  (racestart). (The "jump to prerace" branch is compiled out.)
* goal = NextGoal == 0 ? racefinish : GoalList[NextGoal]. The trigger counts if it **is** the goal or its
  `MasterCheckpoint` is the goal (proxy checkpoints).
  * checkpoint: `NotifyCheckpointReached(element, NextGoal)` (0x6117F0: SetCheckpointTime, StartCheckpoint); a
    `TimeBonus` on it and the player → `RaceStatus:AwardBonusTime(TimeBonus)` + `HUD.ShowTimeExtension`.
  * NextGoal = NextGoal < GoalCount ? NextGoal+1 : 0; `SetRacerGoal(i, next goal)`.
  * the finish: LapsLeft −= 1; `NotifyLapFinished(element, NumLaps − LapsLeft)` (§2.4); `SetRacerLapsLeft`; LapsLeft == 0 →
    NumberFinished += 1, `CalculateRanking(element, NumberFinished)`, **NotifyRaceFinished** (§2.5),
    `NotifyRacePlacement(placing)`, InRace = false.
  * `SharedCheckpoints` (online) copies goals; `TokenValue` → AwardCash (cash grab).
* `NotifyRacePlacement` (#660) for a human when every human is done: placing 1 → `postrace`; else → `raceover`.
* `NotifyRaceTimeExpired`, `NotifyKnockedOut`, `NotifyEngineBlown` (not in D-Day races), `NotifyVehicleDestroyed`
  (RaceOverMessageDuration 1) for the human → `raceover`.
* `raceover`: race-over summary (EShowRaceOverMessage) for RaceOverMessageDuration (3 s) → `ShowLosingPostRaceScreen` =
  FE_ShowPostRaceScreen(false) (0x60AC20: FinalizeRaceStats, then PostRace_Results.fng; the win path,
  FE_ShowPostRaceScreen(true), first pushes the photo-finish screen InGamePhotoMaster.fng). From there Restart →
  `prerace`, or quit → `abandoned`. **So a lost single-player race cannot continue the chain: it is restarted or
  abandoned.**
* `postrace`: EnterPostRaceFlow (results) → `FlowReadyForOutro` → outronis; `EnterFreeRoam` → SetCopsEnabled(true) →
  done.

### 2.4 Knockout (Game_NotifyLapFinished 0x611850) — correction to races.md §4.3 ("when the leader completes the lap")
After SetLapTime/StartLap/CalculateRankings for racer r finishing lap L: count racers with lapsCompleted ≥ L. When that
count equals `NumOpponents + 1 − KnockoutsPerLap·L`, every racer not yet knocked out with lapsCompleted < L is knocked
out (`Game_KnockoutRacer` 0x611440: GRacerInfo::KnockOut, MNotifyKnockedOut, the AI pulls over, then
UpdateAdaptiveDifficulty(KnockedOut)). So the knockout fires when **the last surviving racer completes the lap**, and it
removes everyone behind. On the final lap the count is 1, so the winner's finish knocks out whoever is left.

### 2.5 NotifyRaceFinished (0x6119F0)
FinishRace, pct = 100, CalculateRankings. Local human with rank 1 and !mHasBeenWon (set now):
`alreadyComplete = career-complete flag`; career: **cash += CashValue on every win** (repeat wins pay again); first
career win only: one EAwardUpgrade per reward entry, AdjustStableHeat_EventWin / AdjustStableImpound_EventWin;
first win: every `UnlockRaces` event gets its quick-race unlock flag (4) and, unless NeverInQuickRace, flag 8.
`GRaceDatabase::UpdateRaceScore(true)` (records best time / best lap / best points by type, sets completed-in-career (2)
or quick-race (1), RefreshBinProgress), RefreshEngageTriggerIcons. Any finisher: AITarget::Clear (the AI stops),
UpdateAdaptiveDifficulty(RacerFinished, who). Then Challenge Series bookkeeping (19.8.x, collector's edition).
`Game.CalculateRanking` (0x6058B0): speedtrap races FinalizeRaceStats + CalculateRankings first; returns mRanking.

### 2.6 Speedtrap race (stategraph overrides, owner `speedtraprace`)
* prerace: TrapRecords[racer][trap] = 0, SuddenDeathMode = false, show the trap icons.
* TriggerEnter on `SpeedTrapList[t]`: `kmh = GetSimableSpeedKmh`; counts only if `kmh ≥ ThreshholdSpeed` (**88 km/h**
  on every race trap) and `kmh > TrapRecords[r][t]`: `AwardCash(element, kmh − record)` (= AwardPoints 0x60E030 →
  AddToPointTotal), `NotifySpeedTrapTriggered` (records the speed), record = kmh. Re-entering a trap faster upgrades it.
* NotifyRacePlacement (the first finisher) → SuddenDeathMode. `NotifyRaceTimeSecTick` while SuddenDeathMode: every racer
  still InRace gets `AwardCash(−OvertimePenaltyPerSec)` (once per race second).
* Ranking by points (§1.4); results page SPEEDTRAPS from mSpeedTrapSpeed/mSpeedTrapPosition.

### 2.7 Tollbooth
Opponents none (InitRacers skips type 4). The clock is GetRaceTimeRemaining; booth checkpoints carry TimeBonus (§2.3,
player only). Time expired → raceover (lost). `perpbusted` sets PlayerBusted.

## 3. GRaceDatabase / GRaceParameters / GRaceBin

* Race records (GRaceIndexData 0x30 bytes): key, EventID[10], challenge goal/type, race hash, flags, length, loc tag,
  cash, reputation, rival best, laps, region, cop density, race type, map box. Score info (GRaceSaveInfo 0x10):
  hash, flags (1 completed QR, 2 completed career, 4 unlocked QR, 8 unlocked career, 16 online), best (time / lap /
  points / speed by type), top/average speed.
* Constants: DDay start "16.1.0", DDay end "16.2.1", final boss "1.2.3", final epic chase "1.8.1", Burger King
  "19.8.31"; `sDDayRaces` table (GetNextDDayRace 0x600C30).
* GRaceParameters accessors (0x5FA960..0x5FD2D0) read the gameplay attributes of the same name (GetRaceType 0x5FAA20 maps
  the template to the GRace::Type enum; GetDifficulty 0x5FAC10 <34/≤66/>66; GetTrafficDensity 0x5FABB0;
  GetCopDensity 0x5FD240; GetIsAvailable(context) 0x5FD160).
* GRaceBin (0x1C): bin record, child vault, stats {u16 challengesCompleted, u16 racesWon}. **RefreshProgress**
  (0x6010E0): `racesWon` = WorldRaces with the career-complete flag (boss races do not count); `challengesCompleted` =
  milestones of this bin in state 4 (awarded) **plus open-world speed traps of this bin with the Completed flag**.
  **correction** to the Unity port: speed traps are Blacklist challenges. `Bin.GetNumRacesWon` /
  `GetNumChallengesPassed` (0x604A30/0x604A10) return these.
* GRaceBin accessors: BaseOpenWorldHeat / MaxOpenWorldHeat / ScaleOpenWorldHeat, Required*, Barriers (EnableBarriers
  0x5DD090), BaselineUnlocks (part unlocks awarded on entering the bin, §4.2).

## 4. GManager

### 4.1 Update (0x6036C0)
GRaceStatus::Update → UnspawnUselessCharacters → ServicePendingCharacters → UpdatePursuit → the 8 GEventTimers
(`Game.SetTimer/KillTimer` 0x5E4A40/0x5E4AD0; they post NotifyTimer with the name) → **UpdatePendingSMS** →
**UpdateTriggerAvailability** → UpdateIconVisibility.

### 4.2 Bins, rivals, milestones, speed traps, bounty
* The current bin is careerSettings+8 (playerRecord+0xB0). **EEnterBin(newBin)** (0x62C2D0) loops b = current−1 down to
  newBin: set the bin, **EnableBinMilestones(b)** (state 1 → 2), **EnableBinSpeedTraps(b)** (flags |= Unlocked|Active),
  and (outside gameplay) award the bin's BaselineUnlocks (EAwardUpgrade); then GRaceStatus::RefreshBinWhileInGame.
  It is fired by uiRepSheetRival::NotificationMessage (0x551790) in post-rival mode with `CurrentBin − 1` (the bin
  falls when the beaten rival's result screen is accepted) and by uiRepSheetRivalBio when the bin is 16 (→ 15).
  (Resolves career_flow.md §2 "bin byte −1; not traced".)
* Milestones (GMilestone 0x14: type, challenge key, state, flags, bin, required, recorded): required = **GoalEasy**
  (0x5EE250); state 1 locked, 2 available, 3 done pending escape, 4 awarded. `NotifyPursuitEnded(evaded)` (0x602770) →
  per milestone in state 2/3 FUN_006015B0: state 3 + evaded → awarded (record, MNotifyMilestone, RefreshProgress,
  **AwardPlayerBounty(Bounty)**, ChallengeCompleted); state 3 + busted → back to 2. Then EAutoSave if evaded.
* Open-world speed traps (`speedtraps/bin_NN/speedtrapK`: Position, Radius 20/25, `ThreshholdSpeed` km/h, `Bounty`,
  `BinIndex`; `taggedcarmarkerK` are the jump markers). Stategraph `speedtrap_group_monitor`: the player entering an
  active trap with `kmh > ThreshholdSpeed` → `NotifySpeedTrapTriggered` (0x60A7B0 → GSpeedTrap::NotifyTriggered 0x601730:
  only if Unlocked and Active → Completed, not Active, record, RefreshProgress, refresh icons; roaming → speed-trap screen
  + ChallengeCompleted) and `AwardPlayerBounty(Bounty)`; slower → `HUD.ShowMessage('HUD_TOO_SLOW')`.
* Bounty: `Game.GetPlayerBounty` (0x612300) = FEPlayerCarDB::GetTotalBounty (0x590500) = Σ per-car career bounty + a
  base value; `AwardPlayerBounty(n)` (0x612220) adds to the current car's career record and posts MNotifyPlayerRep (the
  race_bin script re-checks the rival on it).
* `Game.UnlockRace(activity)` (0x612140): score flags |= 8 (and 4 unless NeverInQuickRace), refresh engage icons.
  `IsRaceUnlocked` / `IsRaceCompleted` read flags 8 / 2.

### 4.3 SMS (GManager::AddSMS 0x5F5120, UpdatePendingSMS 0x5EDFE0, CanPlaySMS 0x5EC040, PushSMSToInbox 0x5EC160)
* AddSMS(id): ids 10..14, 95, 96 are dispatched **at once** (voice cell calls, FUN_005DDD20); any other id is queued
  (pending list) unless already received or already pending.
* Each update with pending messages and `CanPlaySMS` (speech enabled, game flow in game, no NIS, no photo finish,
  **roaming** (not racing), the FE allows a pause, no cop speech 0xCC, the player vehicle exists and is not destroyed /
  not in a pursuit [?: vehicle vtable checks]): every pending message goes into the inbox (state 2 = unread, sort
  order), EShowSMS(first voice message, else the first pending) and the menu-zone prompt is refreshed.

### 4.4 Respawn and safehouse
* GetRespawnMarker (0x5E4CF0): override marker if set; else (start-from-safehouse ? safehouse marker : freeroam start
  marker); if that collection is missing, `career_start_%s` (track name). GetRespawnLocation (0x5E4DF0) = its Position
  and Rotation. BeginGameplay (0x603500) clears start-from-safehouse.
* `Game.DoZoneMenuAction(zone)` (0x612030): freeroam start marker = zone.RespawnMarker, then the menu-zone action.
* UpdateTriggerAvailability (0x5EBA50): AllowEngageEvents = AllowMenuGates = !racing && !pursuit;
  AllowEngageSafehouse = !racing && (!pursuit || cooldown).
* StartActivities (0x5F9E70) / StartRaceFromInGame (0x5FDCB0) / WarpToMarker (0x5EBC80): run the startup race or the
  bin activity; warp the player to a marker (bounty jump), optionally queuing a pursuit.

## 5. GActivity / GHandler / GState — the stategraph runtime

* Each `stategraph` activity (GActivity, a GRuntimeInstance over a gameplay record) owns states (GState) and handlers
  (GHandler: one `messagehandler` record = one Lua function + message id + filters). `GatherStatesAndHandlers`
  (0x5F9270) builds `map<GState*, vector<GHandler*>>`.
* `GActivity::Run` (0x5F93D0): mark running, ActivateReferencedTriggers, register the current state's handlers, or post
  EChangeState to "initial". `ChangeStateFromScript` (0x605F20) = Lua `ChangeState(name)` → GetStateByName (0x5ED750),
  unregister old handlers, register new ones, post StateExit/StateEnter.
* `GActivity::HandleMessage` (0x5F9DA0): for each handler of the current state with a matching message id that passes
  `GHandler::MessagePassesFilters` (0x5DD410: each filter = a Query function over a copy of message bytes; flag 0xD038CFA
  inverts) → BuildMessageTable, BuildActivityTables (r0 = activity record, r2 = per-instance state table), then
  `GHandler::HandleMessage` (0x5DD520): look up the global function by handler name and call it with 3 args.
* Activity state (current state hash, flags, Lua table bytes) is saved in GManager object-state blocks.

## 6. GTrigger and GIcon

* GTrigger (ctor 0x601790) wraps a WTrigger (box / radius / plane from the gameplay record), direction, enabled tag, list
  of simables inside, particle effects (flares), icon. `NotifySimableTrigger(sim, stimulus)` (0x5F6A30): stimulus 1
  (inside): first time → MarkAsInside + **MTriggerEnter**; every tick **MTriggerInside**. stimulus 2 (exit): MarkAsOutside
  and **MTriggerExit** only if the record's `FireOnExit`. Triggers are enabled by activation references from running
  activities (AddActivationReference 0x5E8540). The script handlers see TriggerEnter with r1.Sender = the trigger and
  r1.Element = the simable.
* Engage (`world_engage` / `bin_race_engage`): entering an engage trigger of an **unlocked, not completed** race →
  prompt (EnterEngagableTrigger → EngageEventDialog); accept → Activity.Run(TargetActivity); leaving → reset.
  `menu_gate`: TriggerEnter of a zone of the gate's ZoneType, gates allowed (safehouse: AllowEngageSafehouse) and
  `IsActiveMenuGate` (0x604E40: the gate's icon is visible) → EnterGateZone; EnterSafehouse message → NIS / DoZoneMenuAction.
  `cell_engage`: after GateActivity placed < 4, ring (ECellCall) for RingTime, accept → run TargetActivity.
* GIcon types: 1 sprint, 2 circuit, 3 drag, 4 knockout, 5 tollbooth, 6 speedtrap race, 7 rival, 8 safehouse, 9 car lot,
  10 custom shop, 11 hiding spot, 12 pursuit breaker, 13 speed trap, 14 speed trap in race, 15 area unlock,
  16 checkpoint. Flags: 1 shown on map, 2 shown in world, 4 spawned, 8 visible, 0x80 GPSing.
  `RefreshEngageTriggerIcons` (0x602840): an engage trigger's icon is shown when its race has the career-unlocked flag
  and **not** the career-completed flag. `UpdateIconVisibility` (0x5FDAE0): each icon type is visible per the
  availability flags of §4.4 (events, gates, safehouse, speed traps, speed traps in race only in a speedtrap race), all
  hidden during an NIS; visible icons are compacted to the front of the list (`GetIsIconVisible` 0x5DDB30).

## 7. Lua bindings used by the race/career scripts (FUN_0061e750)
Activity.Run 0x6048A0, Bin.GetNumChallengesPassed 0x604A10, Bin.GetNumRacesWon 0x604A30, NIS.Play 0x604B80,
HUD.ShowMessage 0x611320, HUD.ShowTimeExtension 0x6113E0, Game.SabotageEngine 0x60AB60, BlowEngine 0x60AB30,
ForceAIControl 0x6123B0, ClearAIControl 0x612420, **InitRacers 0x61DBE0**, WarpPlayerToTrigger 0x611570,
SetPlayerStartPosition 0x611670, **SetRacerGoal 0x611700**, SetRacerLapsLeft 0x604DB0, KnockoutRacer/TotalRacer
0x611440, **NotifyRacePlacement 0x60AA00**, NotifySpeedTrapTriggered 0x60A7B0, **NotifyCheckpointReached 0x6117F0**,
**NotifyLapFinished 0x611850**, **NotifyRaceFinished 0x6119F0**, StartRace 0x60DBD0, StartRaceTimers 0x604EF0,
SaveStartPositions 0x604E90, RestoreStartPositions 0x60AA50, EnterPostRaceFlow 0x611F20, SetCopsEnabled 0x604F40,
SpawnCop 0x60A670, SpawnCharacter 0x604D40, SetAllStaging 0x60DF70, AwardCash 0x612130 (= AwardPoints 0x60E030 for
races), UnlockRace 0x612140, NotifyCountdownDone 0x60E160, ShowTriggerIcon/HideTriggerIcon 0x604D20/0x604D30,
GetRacerIndex 0x60DAE0, GetSimableSpeedKmh 0x604E10, RacerIsHuman 0x611E20, AllRacersDone 0x6051F0,
AllHumanPlayersDone 0x60E0A0, GetPlayerBounty 0x612300, AwardPlayerBounty 0x612220, JumpToCarLot 0x605250, AbandonRace
0x60DEB0, JumpToSafeHouse 0x6052B0, SetTimer/KillTimer 0x605300/0x605320, ShowGPS 0x605340, NavigatePlayerTo 0x605360,
SkipCareerIntro 0x612490, Fade 0x605500, IntroduceRival 0x605550 (empty), PlayTutorial 0x6124E0, DoSafeHouseIntro
0x605560, DoZoneMenuAction 0x612030, SetHasRapSheet 0x605670, SetWorldHeat 0x612660, ShowWinningPostRaceScreen 0x60E240
/ ShowLosingPostRaceScreen 0x60E230 (FE_ShowPostRaceScreen(true/false) 0x60AC20), CalculateRanking 0x6058B0.

## 8. Port notes (Unity, what follows these paths)
* `Runtime/Game/RaceManager.cs`: per-racer goal sequence (checkpoints, finish, wrapping per lap) entered through the
  goal's trigger (plane crossing inside the box width/height, `Gameplay/RaceTrigger`; in Drive the scripts' triggers are the
  exe's WTrigger volumes, §17.1), progress held at the next goal
  (§1.2), `TargetFor` = the racer's own goal (§1.6), lap counting, lap knockout (§2.4), finish/placement (§2.5),
  speedtrap threshold/record/overtime and the speedtrap FinalizeRaceStats at the player's finish (§2.6), tollbooth clock
  = TimeLimit + bonus − clock from StartTime with booth times (§1.5, §2.7), wrong-way reset (§2.3), 20/40/60/80 % splits,
  projected times (FinalizeRaceStats), grid 7 m / ±1.5 m with ForceStartPosition (§2.2), no opponents in tollbooths,
  racer names, race-over hold 3 s / 1 s, the boss chain's PostRaceActivity after the results, RaceAbandoned on quitting
  or leaving a lost career race (§1.9). Port guards: a goal also counts when the racer is 60 m past it along our route,
  and a rolling start marks the goals behind the start point as passed [?].
* `Runtime/Game/Gameplay/RaceRules.cs` (IsBehind, CalculateRankings, LapKnockouts, Ramp, RaceTrigger),
  `AdaptiveDifficulty.cs` (§1.8; persisted in `Profile.AdaptiveBonus`; feeds `AIRacer.BaseSkill`),
  `OpponentCars.cs` (RandomOpponent candidates by Physics::Info::Performance, §11; racer names), `BinScript.cs` (the per-bin race_bin SMS hooks and SetWorldHeat, career_flow.md §3).
* `GameSession.cs`: cash on every career win, RacesWon = world races, challenges = milestones + open-world speed traps
  (`ActiveSpeedTraps`, `CompleteSpeedTrap`), `AwardBounty`, `AbandonRace`, the next bin's `initial` messages.
* `Story.cs`: the SMS pending queue (§4.3); rival-unlocked messages (bin 1: 93/94). `Systems.cs` EventMarkers: the
  open-world speed traps (§4.2), engage availability (§4.4). `Core/Career.cs`: StartTime, KnockoutsPerLap, IsLoopingRace,
  CheckpointsVisible, RankPlayersByPoints, NoPostRaceScreen, IsEpicPursuitRace, trigger Dimensions/ThreshholdSpeed/
  Bounty/ResetsPlayer, RacerName, ForceStartPosition, speedtraps/bin_NN, RandomOpponent, and no duplicated race traps.

## 9. GRaceDatabase, GRaceParameters, the race index (round 2)

### 9.1 Loading: GVault, GObjectBlock, GRuntimeInstance
* **GManager::InitializeVaults** (0x5F8080): BuildVaultTable (0x5E3D80: one 0x40-byte GVault per vault of the gameplay
  pack image, `name`, bin/vlt offsets and 16-aligned sizes), FindVault("gpcore") + **GVault::LoadResident** (0x5F65F0:
  copy `<name>.bin`, load `<name>.vlt`, allocate the object block of CalcSpaceRequired (0x5F51A0) rounded to 16, flag 1
  loaded), FindKeyReductionShifts, AllocateMilestones / AllocateSpeedTraps / FindBountySpawnPoints, RefreshZoneIcons,
  FreeDisposableIcons(12) and the pursuit-breaker icons (§12.3), SpawnAllLoadedSectionIcons when in gameplay.
* Transient vaults (one per race's child data): **PreloadTransient** (0x5F66D0) registers them; **InitTransient**
  (0x5FA5E0) loads one at a given address, CreateGameplayObjects (0x5FA520) when in gameplay, then
  GRaceDatabase::NotifyVaultLoaded (0x5E9930). **LoadVaultSync** (0x5FA780) is what every GRaceParameters accessor calls
  first (`if (vault && !loaded) LoadVaultSync(vault)`): one resident-style slot (+0x38) and one transient slot (+0x44),
  the previous occupant unloaded (GVault::Unload 0x5EE0F0), then ConnectRuntimeInstances.
* **GObjectBlock::Initialize** (0x5FA270) creates the runtime objects in this order, each over the collections derived
  from its template: GHandler (0x5F55D0), GState (0x5F5730), GActivity (0x5FA0A0), GCharacter (0x5F5880), GMarker
  (0x5F59D0), GTrigger (0x602A60). **GRuntimeInstance** (ctor 0x5E8130) wraps an Attrib instance of the collection,
  links itself into a circular per-type list (DAT_0091DFE4[type]) and GManager::RegisterInstance (0x5E41A0, by key);
  ConnectInstanceReferences (0x5E4210) resolves the GCollectionKey fields.
* **Port**: Core/Career.cs reads every gameplay.bin collection at once through AttribDatabase (no vault streaming, no
  object blocks); the stategraph objects are the Lua agent's (Core/Lua). Deviation without visible effect.

### 9.2 The race list and the index
* **BuildRaceList / StoreRaceList** (0x603970 / 0x603340): every gameplay collection that **CollectionIsRaceActivity**
  (0x5E36D0) accepts, i.e. `Template` false and derived from the `race` template (0xD1E66B67 = stringhash("race")), gets
  a GRaceParameters (0x14 on PC: vtable, index, record, parent vault, child vault) and a 0x30-byte GRaceIndexData filled
  by **GenerateIndex** (0x600CD0). Port: `CareerData.RaceActivities()`.
* GRaceIndexData.mFlags (bit, accessor → attribute): 0x1 InitiallyUnlockedQuickRace (0x5FBBD0 → `AvailableQR`), 0x2
  InitiallyUnlockedOnline (→ `AvailableOnline`), 0x4 InitiallyUnlockedChallenge (→ `InitiallyUnlocked`), 0x8 CanBeReversed (0x5FAC80: reads the index only, so it is always false as compiled),
  0x10 DDayRace, 0x20 IsBossRace (`BossRace`), 0x40 IsMarkerRace (0x5FBE00), 0x80 PursuitRace, 0x100 IsLoopingRace, 0x200
  RankPlayersByPoints, 0x400 RankPlayersByDistance, 0x800 CopsInRace (GetCopsEnabled 0x5FC030), 0x1000
  ScriptedCopsInRace, 0x2000 sunset (TOD > 0.8), 0x4000 NeverInQuickRace, 0x8000 ChallengeSeriesRace, 0x10000
  CollectorsEditionRace, 0x20000 midday (0 ≤ TOD < 0.8). Other index fields: key, EventID[10], ChallengeGoal (decimal16),
  challenge type (stringhash of MilestoneName, 0x5FA9A0), event hash, RaceLength, localization tag, CashValue and
  Reputation (decimal16), RivalBestTime (udecfix16), NumLaps, Region, CopDensity tier, race type, map box (GetBoundingBox).
* **GetRaceType** (0x5FAA20): `EventIconType` through the table at 0x8F5CD8: circuit 1, p2p 0, drag 2, knockout 3,
  tollbooth 4, speedtrap 5, cashgrab 7, checkpointrace 6, challenge 8, speedtrapjump 9, milestonejump 10 (else -1).
  Harness `gpcore types`: the port's template-derived RaceType agrees with it for every career race (opm's rival race has
  no icon type). **GetRegion** (0x5FAAB0): college 0, coastal 1, city 2 (table 0x8F5D30). **GetDifficulty** (0x5FAC10):
  DifficultyLevel < 34 easy, ≤ 66 medium, else hard. **GetCopDensity** (0x5FD240): CopDensity 0 → CopsInRace ? light :
  off; ≤ 33 light, ≤ 66 medium, else heavy. **GetTrafficDensity** (0x5FABB0) = TrafficLevel.
* Other accessor attributes (resolved by jhash of the pushed key): GetUseWorldHeatInRace → `UseWorldHeat`, GetMaxHeatLevel
  and GetMaxRaceHeatLevel → both `MaxHeatLevel`, GetPhotoFinishCamera → `FinishCamera`, GetPhotoFinishTexture → 0x038A3B53
  (unnamed), GetTimeOfDay → `TOD`. 255 race activities; ClearRaceScores opens 22 for quick race, 85 online, 3 challenges.
* **GetReputation** is read only by GenerateIndex and PostRaceMilestonesScreen::StartChallengeAnimations (the bounty shown
  for a pursuit challenge). Nothing pays a race's Reputation as bounty on a win (**correction**: the earlier port's
  "Reputation ≥ 1000 → bounty" is removed). Races pay CashValue only; bounty comes from pursuits, milestones and speed traps.

### 9.3 Score infos (GRaceSaveInfo) and unlocks
* ScoreFlags: 1 completed in quick race, 2 completed in career, 4 unlocked in quick race, 8 unlocked in career, 16
  unlocked online. GetScoreInfo (0x5DCA40) finds or takes the first free slot; CheckRaceScoreFlags (0x5DCA70) = any of the
  mask; IsQuickRaceUnlocked / IsOnlineRaceUnlocked / IsCareerRaceUnlocked / IsCareerRaceComplete (0x565DA0 / 0x565DB0 /
  0x565D90 / 0x50DF90) test 4 / 16 / 8 / 2. ResetCareerCompleteFlag (0x5DCBB0) clears 2.
* **ClearRaceScores** (0x600B20, new career): every race that is not a D-Day race: flag 4 if InitiallyUnlockedQuickRace
  or (InitiallyUnlockedChallenge and not a boss race), flag 16 if InitiallyUnlockedOnline.
* **GetIsAvailable(context)** (0x5FD160): an unlock-all switch (DAT_00926124) answers yes; quick race: not
  NeverInQuickRace and flag 4; online: flag 16; career: flag 8 and not flag 2.
* **Game.UnlockRace** (0x612140): flag 4 unless NeverInQuickRace, flag 8 always, RefreshEngageTriggerIcons.
  Game_NotifyRaceFinished's UnlockRaces loop (0x611C29, only when the race was not career-complete before) does it the
  other way round as compiled: flag 4 always, flag 8 unless NeverInQuickRace.
* **UpdateRaceScore** (0x602320) for GetWinningPlayerInfo: top speed and average speed (udecfix16), then by race type:
  P2P / drag / tollbooth / the jumps keep the lowest race time, circuit and knockout the lowest lap (FUN_005FFFD0), speed
  trap and cash grab the highest mPointTotal, checkpoint races an integer score; a record is replaced when neither
  completion flag is set yet or it improves. With `finished`: flag 2 in career context, else flag 1; RefreshBinProgress.
* **GetNextDDayRace** (0x600C30): sDDayRaces at 0x8A37E4 = 16.1.0, 16.2.2, 16.2.3, 16.1.1, 16.2.1 (8-byte strings); the
  first without flag 2. Callers: uiCareerCrib and CareerSettings::ResumeCareer. SimulateDDayComplete (0x5DD240) is empty.
* **The race_bin stategraph's unlocks** (handlers of one state add up: GatherStatesAndHandlers keeps a vector per state,
  so the per-bin "overrides" are extra handlers; race_bin_15's empty unlock_races handler changes nothing):
  `unlock_races` StateEnter: Game.UnlockRace on the target of every engage trigger with InitiallyUnlocked (every world
  race; the first boss race's trigger has InitiallyUnlocked false); `rival_unlocked`: UnlockRace on every BossRaces entry;
  `done` (all boss races won): UnlockRace on every WorldRaces entry; `rival_locked` NotifyChallengePassed: when this
  challenge is the one reaching RequiredChallenges, EAddSMS(SMSCellChallenge) and UnlockRace(CellChallengeRace) (only
  race_bin_15 sets one: race_bin_01/1_2_2_sprint, SMS -1). completion_unlock / reputation_unlock / cell_engage exist as
  templates only (no instances in gameplay.bin).
* **Engage triggers** (harness `gpcore unlocks` / `types`): each bin's child `engage` (bin_race_engage) lists RaceTriggers
  with Position, Radius 25, FireOnExit, TargetActivity, InitiallyUnlocked. The trigger is where the icon stands; many are
  hundreds of metres to kilometres from racestart (14.4.1: 2.5 km, 1.5.1: 5.5 km): accepting the race warps the player
  to racestart (the `prerace` warp, §2.3). Later boss races of a chain have no trigger (reached by PostRaceActivity).
* RefreshEngageTriggerIcons (0x602840) shows a trigger's icon when its race has flag 8 and not flag 2.

### 9.4 Post-rival mode, D-Day
* **cFrontendDatabase::IsPostRivalMode** (0x50E560) = flags(+0x12C) bit 17. Set by **EQuitToFE** (dtor 0x623F80) in career
  context when the target package is SafeHouseRivalChallenge.fng (or an FEAnyMovieScreen package); that quit comes from
  PhotoFinishScreen::NotificationMessage (0x5A8146): a career boss race won with no boss race of the bin left without
  flag 2 (bin 1: when exactly one is left, the epic pursuit, it posts MFlowReadyForOutro instead; otherwise
  ERaceSheetOn(2)). uiRepSheetRivalFlow::Next (0x530359) clears it. (Resolves the round-1 open item "who sets post-rival mode".)
* **cFrontendDatabase::IsDDay** (0x50E480) = career CurrentBin > 15 (inlined at its users).
* **GetIsDDayRace** (0x5FBD20) = the race's DDayRace. SFXObj_PFEATrax::TestToLicensed (0x4DF56D) refuses licensed music
  while the race parameters are set and the race is a D-Day race or GRaceStatus::IsDragRace; also read by
  AIVehiclePursuit::UpdateSiren, MemoryCard::ShowAutoSaveIcon, PauseMenu, Chyron::Start, PostRaceResultsScreen,
  SetRoaming (rain override), EQuitToFE, SoundAI::SyncPlayers, EAXDispatch::BreakAway.
* **Set321**: EAXSound::START_321Countdown (0x4B8BF0) sets the PFEATrax flag 1 (Set321(true)) and starts the NIS stream
  object 0x40000050; Countdown::Update (0x5679B0) calls it when the countdown shows "3" (its first digit), not at GO.
  ERestartRace calls EAXSound::ReStartRace(actively racing and the race type is not 8 challenge).

## 10. GManager remainder, cops and SMS (round 2)

### 10.1 Cop lockout and reset (resolves ai_cops.md "who calls ResetCopsForRestart")
* AICopManager's constructor sets mLockoutTimer = kCopLockoutTime = 60 s.
* **ICopMgr::ResetCopsForRestart(true)** (vtable +8; AICopManager.cpp:2150) callers: **Game.InitRacers** (0x61DC2B) and
  the Lua binding **Game.ResetCopsForRestart** (0x60AB10), called first in the `basicrace` prerace StateEnter (#1224), so
  on every race start and restart; and **GManager::NotifyWorldService** (0x5EBBDF) for a warp queued with a pursuit (the
  rap sheet's bounty / milestone jump, WarpToMarker 0x5EBC80): World_RestoreProps, GPS_Disengage, ITrafficMgr flush,
  ResetCopsForRestart(true), LockoutCops(false), PursueAtHeatLevel(1) and again each service until PlayerPursuitHasCop.
  Its lockout: 60 s; 0 in a challenge race; TimeInactiveFor911 of the perp row when roaming or with no startup race (a
  career race engaged in the world has none; a quick race has one).
* **GRaceStatus::SetRoaming** (0x5FE506) calls LockoutCops(true) when a race ends: TimeInactiveFor911, as the play mode
  is roaming again.
* ICopMgr vtable (PC): +4 VehicleSpawningEnabled, +8 ResetCopsForRestart, +0xC SetAllBustedTimersToZero, +0x10
  PursuitIsEvaded, +0x14 IsCopRequestPending, +0x18 IsCopSpawnPending, +0x1C SpawnCop, +0x20 PlayerPursuitHasCop, +0x24
  CanPursueRacers, +0x28 IsPlayerPursuitActive, +0x2C LockoutCops, +0x30 NoNewPursuitsOrCops, +0x34 PursueAtHeatLevel,
  +0x38 GetLockoutTimeRemaining.

### 10.2 ERestartRace (ctor 0x6307F0) — adaptive difficulty reason 5
The pause menu's and the results screen's Restart: EAXSound::ReStartRace(actively racing and not a challenge race),
**UpdateAdaptiveDifficulty(5 Restart, 0)** (the only caller with reason 5), a global restart flag (0x91CF28), MRestartRace,
EFadeScreenOn(false). The stategraph's RestartRace handler goes back to `prerace`.

### 10.3 CanPlaySMS (0x5EC040), full
DAT_00992244 clear, game flow state 6 (in game), no movie / NIS (DAT_009885C8), no photo finish (DAT_0091CF41), GRaceStatus
exists and roaming, FEManager::IsOkayToRequestPauseSimulation(0, 1, 1), not Speech::IsCopSpeechPlaying(0xCC), and the first
player's simable has an IVehicle with: not IsAnimating (+0xBC), not IsStaging (+0x20), not IsLoading (+0x5C), and
GetAIVehiclePtr (+0xAC)'s slot +0xA0 (GetPursuit [?: the slot index is 40 on PC vs 41 in the GC header]) null.

### 10.4 Pursuit stat tracking (for the cop / milestone owners)
* **GManager::UpdatePursuit** (0x6013F0): TrackValue cost_to_state_in_pursuit (IPursuit +0xE0), cost_to_state and
  bounty_in_pursuit (IPerpetrator); mPlayerPursuitInCooldown = pursuit state 2; icon flags +0x2C9 = (roaming or a
  Challenge Series race) and pursuit in cooldown (hiding spots [?]), +0x2C8 = (roaming / CS race / epic pursuit) and an
  active pursuit not in cooldown (pursuit breakers [?]).
* **GManager::TrackValue(name, v)** (0x5EDA20): per milestone type keeps the best value (bigger or smaller per the type's
  flag), posts MNotifyMilestoneProgress, and when roaming tells every milestone of that type in state 2/3.
* **GInfractionManager::ReportInfraction(type)** (0x5FDDA0): counted only when roaming or in a pursuit race; the first
  report of each type in a pursuit sets its bit, fires EInfraction and TrackValue("total_infractions", number of distinct
  types); every report increments the count. So the `total_infractions` milestone counts distinct infraction types.

### 10.5 Other answers
* **NumAllowedForLaps** (prerace #1224): written, never read by any handler; the knockout itself is C++ (§2.4).
* Reputation: see §9.2.

## 11. Physics::Info::Performance (resolves "opponent car picker uses the car-lot tier")
* **Physics::Info::Init** (0x683EB0): every pvehicle that HasPerformanceRatings (0x66C9A0: HandlingRating[0] <
  HandlingRating[1] and [1] > 0: the car-lot and bonus race cars plus the career-start M3 and cs_c6_copsporthench; no traffic or cops) gets PerfStats::Fetch stock and after
  Upgrades::SetMaximum (0x67F950, every package at GetMaxLevel, no Junkman). Each raw value is normalised by the
  minimum / maximum over all estimates (sub_6794E0): **TopSpeed = ramp(top speed)**, **Handling = ramp(handling)**,
  **Acceleration = 1 − ramp(0-100 mph time)**. GetStockPerformance / GetMaximumPerformance (0x679660 / 0x6796C0) read
  the table; ComputePerformance (0x679580) answers 0 for an unrated car.
* **PerfStats::Fetch** (0x678CE0): full-throttle run from the peak-torque rpm in first, dt 0.125 s until 100 mph
  (44.703 m/s: the raw acceleration time) then 1 s until 120 s: rpm = (v/r·ratio·(1 − idle/red) + idle) in rad/s, clamped
  idle..redline; force = Torque(rpm)·1.3558·(1 + InductionBoost(rpm, spool 1))·ratio/r, ×(1 + NOS boost) while the run is
  shorter than the NOS duration field and v > 5; drag = chassis drag coefficient·v²; shifts at ShiftPoints; top speed
  = the speed limiter, or v when top gear's force falls below drag or the rpm reaches redline. Raw handling =
  lerp(HandlingRating[0], [1], Σ Handling weight · Upgrades::GetPercent / Σ weight).
* **PerformanceWeights[7]** (0x8F8928) {TopSpeed, Handling, Accel}: tires (0.25, 1.5, 0.25), brakes (0, 0.5, 0),
  chassis (0.25, 1, 0.2), transmission (1, 0, 0.75), engine (0.5, 0, 1), induction (0.25, 0, 1.25), nos (0.25, 0, 1.5).
  Upgrades::GetPercent (0x677F80) = 0 without packages, 1 at the top level, else level/max.
* **EstimatePerformance** (0x67C570): per axis f = Σ w·pct / Σ w; with j = (Σ w of installed Junkman parts) / Σ w:
  max' = max + (1 − max)·j, stock' = stock + (max' − stock)·j·0.33, f' = min(1, f·(1 + 0.33·j)); result =
  stock' + (max' − stock')·f'. PVehicle::GetPerformance (0x688270) returns the copy made at construction.
* **Game_FindPerformanceCandidates** (0x61D8E0 → sub_684610): perf' = perf + 0.2 per axis; candidates = rated cars with
  stock ≤ perf' on all axes and maximum ≥ (0,0,0), filtered by pvehicle RandomOpponent (0x6DF0ABFE), shuffled, up to N.
* **Port**: `Gameplay/PerformanceRatings.cs` runs the same run on CarSpec (torque table, gears, final drive, rear tyre
  radius, DRAG_COEFFICIENT, MASS, NOS_CAPACITY / TORQUE_BOOST as the duration / boost fields [?], PlayerCar's induction
  model at full spool [?]); `OpponentCars.Candidates` uses it (perf = max(MinimumAIPerformance on all axes, the player's
  EstimatePerformance)) and keeps the car-lot tier only as a fallback when no car is rated. Cars without a model folder are
  skipped [ours].

## 12. Pursuit breakers: smackable event sequences (resolves "nothing calls BreakerStopCops")
* The `smackable` field **EventSequencer** (0x5AAB860F; read by sub_668290 with a 16-byte default) is an
  Attrib::StringKey {u64 hash64, u32 hash32, char*}; hash32 = stringhash32 of an engine name. **start_sequencer**
  (0x5739788B) is set on radiotower, amphitheater, gasstationsupport, radiotowersupport(_frag). The engines are the 73
  world event-sequencer packs of TRACKS\L2RA.BUN (chunk 0x3B811; harness `gpcore evseq`): bench, billboard_seq,
  donut_seq, drivein_seq, gasstationroof_seq, gasstationsupport_seq, gaspumpexplosion_seq, watertower(support)_seq,
  radiotower(support)_seq, seafoodpatio_seq, CopCar, TrailerB, TrailerLog, ...
* Stimuli (stringhash32, resolved against speed.exe): **IMPACT_1..IMPACT_6** (table 0x905D28, DamageZone::
  GetImpactStimulus 0x768C00; sub_669D70 fires levels 0..floor(clamp01(x)·6) for an impact of intensity x),
  **WORLD_DETACHED** 0xC91B90D9 (RigidBody::Detach 0x697050), **EXPLOSION** 0xAB556D39, **JOINT_DETACHED** 0xE16B3A09,
  TRIGGER_ALL; states `Default` 0xEA4F1C8F, `Damaged` 0x819DF67C. Three stimuli stay unnamed: 0x758DF920, 0xA6E01DE9,
  0x074C2A46, all sent by other engines through EProcessAreaStimulus (a support passing on to the roof it carries).
* Events: **EBreakerStopCops** {fEventRadius, fDuration} at the prop → MBreakerStopCops (ApplyBreakerZones); per engine:
  donutshoproof 16 m (t 0.23), donut 12, drive-in 12 (0.15), gas station roof 16 (0.07), gazebo 5, greenhouse 10 (0.19),
  heavy concrete 20, hinkley base 24 (0.2), radio tower support 10/20/24/20/10 (0.16-0.38), radio tower 10, sailboat 12,
  scaffold support 10, seafood patio 5 and 9, water tower support 20 (0.2), wooden scaffold 10; durations all 5 s.
  **EMomentStrm** {fAttribStrm (string), key}: moments boat_fall, scaffold_crash_big, car_carrier, **expl_gas_station**
  (gaspumpexplosion_seq), gazebo_crash, logs_drop, fish_sign_crash, torus, trailer_park, drive_in, wtr_tower_crash,
  unpause. **EProcessAreaStimulus** {system id, stimulus, queue mode, radius}; **ESpawnExplosion** {position, speed,
  radius, start radius, effect this, does damage, include players, exclude vehicles, exclude smackables}.
* Pursuit-breaker icons (GIcon type 12): world markers named `IconMarker` (sub_5EBE70 over the marker list 0x9AA494 from
  InitializeVaults), restored per section by RestorePursuitBreakerIcons (0x5E48F0).
* **Port** (`Runtime/Smackable.cs`): `WorldSequences` loads the L2RA packs; a smackable with an EventSequencer engine runs
  an EventSeqEngine (Core/EventSequencer.cs): impacts send IMPACT_1..n with x = closing speed / 30 m/s [?: the game's
  intensity comes from RBSmackable's collision handler, not decompiled], coming loose sends WORLD_DETACHED; EBreakerStopCops
  → `PursuitSystem.BreakerStopCops(pos, radius, duration)`, EMomentStrm → `Smackable.MomentFired` [ours] and
  `NotifyGasStationExplosion` for expl_gas_station, EProcessAreaStimulus → the props in range with that system,
  ESpawnExplosion → EXPLOSION to the props in its radius at once [?: the game grows it at fSpeed and damages cars].
  Fragments, effects and hiding are not ported.

## 13. Port notes, round 2 (files)
* `Core/Career.cs`: RaceEvent GRaceType (EventIconType), RegionIndex, CopDensityValue / CopDensityTier, DifficultyTier,
  NeverInQuickRace, InitiallyUnlockedQuickRace / Online / Challenge, CollectorsEdition, ScriptedCopsInRace, RankByDistance,
  UseWorldHeatInRace, PursuitRace, RivalBestTime, MaxHeatLevel, MaxRaceHeatLevel, TrafficPattern, PhotoFinishCamera /
  Texture, IsSunsetRace / IsMiddayRace, Engage + EngageInitiallyUnlocked; BlacklistBin CellChallengeRace,
  SmsCellChallenge, EngageTriggers; `RaceIndex` (types, regions, flags, contexts, sDDayRaces); `RaceActivities()`.
* `Runtime/Game/Gameplay/RaceDatabase.cs`: score infos in `Profile.RaceScores` (flag 2 mirrors `Profile.RacesWon`),
  ClearRaceScores, UnlockRace / UnlockFromWin, IsAvailable(context), NextDDayRace, UpdateRaceScore, the race_bin unlocks.
  `GameSession.SyncRaceUnlocks` re-applies them from the career position (old saves); `AwardWin(ev, race)` /
  `QuickRaceWon` record scores; `EngagePoint(ev)`.
* `Systems.cs` EventMarkers: markers only at the current bin's engage triggers whose race is career-available, at the
  trigger (not the start grid); the world systems start with the 60 s construction lockout.
* `RaceManager.cs`: ResetCopsForRestart at setup, LockoutCops(true) when the race ends, `RestartRace(ev)` (ERestartRace:
  adaptive reason 5, `Restarted` event), `IsDDayRace`, Entrant.TopSpeed / DistanceDriven; a race left before GO no
  longer keeps the player car staged [ours]. `Gameplay/CopReset.cs` (PursuitSystem partial): ConstructionLockout,
  ResetCopsForRestart.
* `Story.cs`: unlock_races for bin 15 at the safehouse intro, rival_unlocked unlocks, post-rival mode cleared when the
  rival flow starts, CanPlaySMS's vehicle checks (staging, movie). `GameSession.Load` sets `MoviePlayer.IsDDay` = career
  bin > 15.

## 14. Still unknown / not traced
* DetermineRaceSegmentLength's per-road shortcut annotation (how progress is measured while on a shortcut).
* ~~DispatchSMSMessage~~ (resolved in §16.6); the EShowRaceOverMessage text selection.
* The three unnamed sequencer stimuli (0x758DF920, 0xA6E01DE9, 0x074C2A46) and RBSmackable's impact intensity.
* PerfStats::Fetch's exact attribute fields (the NOS duration / boost and drag offsets are matched by meaning, not
  confirmed), and InductionBoost (0x66BCC0) / ShiftPoints (0x673400) are reused from the physics port, not re-checked.
* IVehicleAI slot +0xA0 in CanPlaySMS (GetPursuit assumed).

## 15. Round 9: the scripts drive (Lua Drive mode, lua_hooks.md §4.2)
* **Game.EnterPostRaceFlow** (0x611F20) decoded: FinalizeRaceStats, Game_AbandonRace (the timers and the AI stop), then
  with NoPostRaceScreen (GetNoPostRaceScreen 0x5FC320) MFlowReadyForOutro at once and score flag 2 on the event (16.1.0 and
  16.2.1 complete this way), else FE_ShowPostRaceScreen(true). Correction to §2.3: the win path is `postrace` →
  EnterPostRaceFlow (Game.ShowWinningPostRaceScreen is never called by gameplay.bin).
* **Game.SimableAngle** (0x6053B0) is dot(the simable's forward vector, the instance's direction), not an angle.
* In Drive (the default in career since round 9) the §2 rules are the scripts': RaceManager keeps the GRaceStatus
  bookkeeping (progress, rankings, splits, the lap knockout of Game_NotifyLapFinished, FinalizeRaceStats, the results)
  and takes the goals, checkpoints, laps, finish, race over, restart and abandon from the `basicrace` calls
  (RaceManager.Drive.cs); the win's cash/unlocks/score run at Game_NotifyRaceFinished (rank 1, once), not after the
  results. The race_bin SMS / unlocks, the speed-trap award and the D-Day chain are the scripts' (§4.2 of lua_hooks.md);
  the C# copies in BinScript, RaceDatabase.On*, Story.CheckProgress and EventMarkers stay for Shadow / Off.
* GameFlow follows the exe's QuickGame / CareerGame loading states and GameplayActivity (lua_hooks.md §4.3).

## 16. Round 10: GManager's saved gameplay data, activity lifecycle, vaults, icons, loading
### 16.1 SaveGameplayData (0x5F7410) / LoadGameplayData (0x5F81D0) / ResetAllGameplayData (0x5F7730)
* Save: the buffer is zeroed; every GRuntimeInstance of the activity ring (DAT_0091DFE4) runs GActivity::SaveState(0)
  (0x5F6BA0); header "Game" (0x656D6147), version 8, the persistent object-state map's count (+0x134) and its blocks
  (each {key, size, data} padded to 16), SaveTimerInfo (0x5E4C40: 8 × 0x20 {duration, active, elapsed, name[19]}),
  SaveMilestoneInfo (0x5EDCB0: 16-byte type infos), the milestones (+0xA4/+0xA8, 20 bytes each), the speed traps
  (+0xAC/+0xB0, 20 bytes each), 0x200 bits at +0x264, FUN_005E35D0's block, the pending SMS ids (list +0x2A8), the free-roam
  start marker (+0x108) and the from-safehouse marker (+0x10C), then a checksum over the lot (FUN_0065B6E0).
* Load: checksum and "Game" ≥ 8 checked; ResetAllGameplayData (both object-state maps, the block allocator,
  ResetMilestoneTrackingInfo, ResetMilestones, ResetSpeedTraps, +0x264, ResetTimers, the markers +0x108/+0x10C/+0x11C,
  start-from-safehouse) then every part back (AllocObjectStateBlock(key, size, persistent) for the blocks, LoadTimerInfo
  0x5E4C90 re-hashing the names, LoadMilestoneInfo, LoadMilestones, LoadSpeedTraps, LoadSMSInfo 0x5F50C0, the markers).
* Port: Profile.ScriptStates / ScriptTimers (LuaFlow's GameplayStateStore, Core/Lua/GameplayState.cs), Profile.SmsPending
  (Sms); milestones, speed traps and the spawn were already in the profile.
### 16.2 Object-state blocks and the activity lifecycle — lua_hooks.md §2.2 / §2.2.1
AllocObjectStateBlock (0x5F5F10: key >> FindKeyReductionShifts, map +0x12C persistent / +0x138 transient, reused when
the new size fits, zeroed when persistent), ClearObjectStateBlock (0x5F30F0: both maps), FUN_005ED960 (find: transient,
then persistent). GActivity: ctor 0x5F9BA0/0x5F9C30 (+0x28 current state, +0x34 state map, +0x3C running, +0x3D has
table; LoadState at once), Run 0x5F93D0, Reset 0x5F4780, Suspend 0x5F4730, dtor 0x5F7C10 (Persistent → SaveState(1)),
SaveState 0x5F6BA0, LoadState 0x5F9480, table getter 0x5F9CC0, ChangeState 0x5FA3C0, HandleMessage 0x5F9DA0,
GatherStatesAndHandlers 0x5F9270 (ring order: lua_hooks.md §2.1). Attributes: `Persistent` 0xE4542E9B, `AutoStart`
0x883C65E3, `FreeRoamOnly` 0xAA0135E9.
### 16.3 StartActivities (0x5F9E70), EnterBin (0x602BB0), StartRaceFromInGame (0x5FDCB0)
* StartActivities (BeginGameplay): with a startup race (race DB +0x2C) GActivity::Run of it, context = its context, and
  mRefreshBinAfterRace when the race is not of the current bin (career); without one (and not SkipFE) StartActivitiesCall1
  (0x5F95F0: every activity with AutoStart and (not FreeRoamOnly or no startup race) → GActivity::Run) and the current bin's
  activity if it is not running.
* EnterBin(n): topology/barriers/race segments reset when a bin was set, mRaceBin = bin n, GRaceBin::EnableBarriers, the
  bin's child vault (GRaceBin +0x14) LoadVaultSync'd; in gameplay StartActivitiesCall1(1), the bin activity Run if not
  running, RefreshEngageTriggerIcons, RefreshSpeedTrapIcons. The old bin activity keeps running until gameplay ends.
* StartRaceFromInGame(raceHash) (UISafehouseRaceSheet, uiRepSheetRival), only when roaming: a race of another bin →
  FUN_005F77F0 (every bin's running activity SaveState(0) + Suspend) and mRefreshBinAfterRace; both race vaults loaded;
  Reset + Run of the race activity.
* Vaults (harness `lua order`, 272 in gameplay.bin): gpcore holds every handler, state, race_bin_NN, the menu gates,
  speedtraps, milestones and race_bin_16/drive_to_safe_house; race_bin_NN/engage is in vault race_bin_NN; each race's
  children in its own vault. So only the current bin's engage activity exists (the port ran the beaten bins' too).
### 16.4 Icons (UpdateIconVisibility 0x5FDAE0, GetIsIconVisible 0x5DDB30) and IsActiveMenuGate (0x604E40)
Per icon type a visibility byte: 1-7 (races) AllowEngageEvents (+0x2CE), 8 safehouse AllowEngageSafehouse (+0x2CF), 9 car
lot and 10 customshop AllowMenuGates (+0x2D0), 11 hiding spot +0x2C9, 12 pursuit breaker +0x2C8, 13 speed trap
AllowEngageEvents, 14 speed trap in race = a speedtrap race (type 5), 15 always; all hidden by a global switch (0x91E00A)
or an NIS (0x9885C8); an icon needs flag 1 (on the map) too. Visible icons are compacted to the front (+0x2C0 count).
IsActiveMenuGate(trigger): an instance with flag 0x8000/0x2000/0x4000 at +0x18, a GIcon at +0xC0, and GetIsIconVisible.
### 16.5 GRaceStatus load wait (NotifyScriptWhenLoaded 0x5EEA60, Update 0x603132)
waiting = a racer's IAudible not loaded (0x5E95F0) or IRenderable not loaded (0x5E9660) or a reserved stock car still
loading (GManager 0x5EBF30, IVehicle +0x5C); streaming = TrackStreamer::IsLoadingInProgress; cop = ICopMgr
IsCopSpawnPending (+0x18). NotifyScriptWhenLoaded: EFadeScreenOn(false) when something loads, mScriptWaitingForLoad = 1.
Update: post MLoadingComplete (and clear the flag) when not waiting, no cop pending, and (not streaming, or streaming with
TrackStreamer byte +0x193 clear, or that byte set and the load over) [?: the byte].
### 16.6 SMS, message screen, milestone tracking
* GManager::AddSMS (0x5F5120): ids 10..14, 95, 96 → DispatchSMSMessage (0x5DDD20): only a message in state 0 (not
  received) → state 2 (unread), sort order = GetSMSSortOrder, **ECellCall(id)**; others → the pending list unless
  received or pending.
* EShowMessageScreen(id) → SixDaysLater.fng (ctor 0x58E820): the text is always `DDAY_TIMELAPSE_%d` with id + 1, and the
  screen fades in (EFadeScreenOff 0x14035FB) — the scripts' Intro/OutroMessageID use only these cards.
* GManager::TrackValue(name, v) (0x5EDA20): the current value is stored; when the best (+0x18, −1 = none) is beaten
  (bigger, or smaller when the type's flag bit 0 is clear) the best is set and MNotifyMilestoneProgress(name, v)
  (FUN_005DD5F0) posted; when roaming, every milestone of that type in state 2/3 is told (FUN_005DE4B0).
### 16.7 bRandom (0x45DA30) and the Lua RNG bindings
bRandom(range) = seed % range, seed' = t ^ (s ^ (t << 27)) with s = seed ^ 0x1D872B41, t = s ^ (s >> 5); bDefaultSeed
0x12345678 at load, SeedRandomNumber (Main.cpp) seeds it from the ticker + 0x12345678. Math.RandomInt = bRandom(n) + 1;
Table.Randomize (0x618AC0) = n passes of swaps t[i] ↔ t[bRandom(n) + 1] for i = 1 .. n−1.
### 16.8 The track streamer's loading bar (0x64B770)
Main loop, Sim state 3: CheckLoadingBar on → FEManager::RequestPauseSimulation("TrackStreamerLoadingBar"),
EFadeScreenOn(true); off → EFadeScreenOff(0x16A259), RequestUnPauseSimulation. Port: GameFlow.OnLoadingBar
(WorldStreamer.LoadingBarChanged), GameFlow.SimulationPaused (GameBootstrap's time scale), StoryCard.LoadingBar.
### 16.9 Other bindings decoded this round
Game.SpawnCop (0x60A670: LockoutCops(false), AICopManager::SpawnCop(pos, dir, name, InPursuit, RoadBlock)),
GCharacter::Spawn (0x601320: speed mph × 0.44703), Game.SetTrafficSpeed (0x605090: MSetTrafficSpeed(f, f, 1)),
Camera.SetGenericCamera (0x604A50: ECinematicMoment; MICECameraFinished from CameraAI::Director::SelectAction 0x479BD5 when
the ICE action ends), Audio.IsCopSpeechPlaying (Speech::Manager::IsPlaying(1)), Game.SkipCareerIntro (0x612490).

## 17. Round 11: GTrigger / GIcon on WTrigger, the remaining GManager pieces, a function-by-function pass

### 17.1 GTrigger and GIcon (resolves §6's "wraps a WTrigger (box / radius / plane)")
The GTrigger ctor (0x601790) and the WTrigger scan are decoded in world_interaction.md §11 (shapes: Dimensions → box with
width = y, length = x, height = 2z; Radius → cylinder of height 4r; else a plane Width × 1 m × 100 m; flags 0x4810D01: fire on
enter and again on exit, not directional; the template → type bits and the GIcon type; BinIndex gates the icon's map/world
flags). Port: `Core/WTrigger.cs`, `Runtime/Game/Triggers.cs` (GTrigger, GameTriggers, GIcon, Availability), `Runtime/Lua/
LuaTriggers.cs`. **GIcon**: types 1-16 and flags as §6; `GIcon.IsVisible` is UpdateIconVisibility / GetIsIconVisible (§16.4);
`Availability.AllowEngageEvents / AllowMenuGates / AllowEngageSafehouse / PursuitInCooldown / ActivePursuit` are
UpdateTriggerAvailability (0x5EBA50) and UpdatePursuit's (0x6013F0) bytes +0x2CE / +0x2D0 / +0x2CF / +0x2C9 / +0x2C8; EventMarkers
reads them. GIcon::Spawn / Unspawn / RefreshEffects / SnapToGround (0x5EC270 / 0x5E5A00 / 0x5EA190 / 0x5EA0F0) are the icon's
world model and flare (SpawnSectionIcons / UnspawnSectionIcons 0x5EDEF0 / 0x5E9F70 per streamed section): the port's markers
are EventMarkers' columns [ours].

### 17.2 GManager: StartRaceFromInGame, useless characters, the bounty markers
* **StartRaceFromInGame(raceHash)** (0x5FDCB0; UISafehouseRaceSheet::NotificationMessage 0x55172B, uiRepSheetRival 0x5519BD):
  only when `mPlayMode == roaming`; GetRaceFromHash (0x5FB710); when GRaceStatus has a bin (+0x196C) and the race's parent vault
  (+0xC) is not the bin's (+0x14): **FUN_005F77F0** (every bin activity SaveState(0) + Suspend) and `mRefreshBinAfterRace = 1`
  (+0x19A5); the parent and child vaults LoadVaultSync'd if not loaded; **GActivity::Reset + Run** of the race's activity.
  GRaceStatus::Update then (§1.1) turns mRefreshBinAfterRace into a queued bin change → EnterBin(current) when roaming again,
  which runs StartActivitiesCall1 and the bin activity (GActivity::Run: the suspended activities resume in their state). Port:
  `LuaFlow.OnStartRaceFromInGame(ev)` from GameFlow.StartRace (a race started from in-game while roaming in career): the running
  `race_bin_*` activities SaveState + Suspend when the race's bin differs from the current one, `_refreshBinAfterRace`; LuaFlow.Tick
  runs StartCareerActivities(bin) again when roaming returns (resolves lua_hooks.md §6's open item).
* **UnspawnUselessCharacters** (every GManager::Update) → **GCharacter::IsNoLongerUseful** (0x5EB800): a character in spawn state
  4 / 5 whose vehicle's IVehicle value (vtable +0x60) is ≥ 0.5 [?: speed or age] is useless when for every local player (the
  player list 0x92CF5C) it is at least 100 m away **and** behind him (dot(character − player, player's forward (vtable +0x34))
  < 0); then GCharacter::Unspawn (0x5EEB90). UnspawnAllCharacters (0x5EED90) at the race end. Port: `ScriptedCharacters.
  UnspawnUseless` from LuaDrive.Update (0.5 s after the spawn).
* **GetBountySpawnMarkerTag(i)** (0x5E47C0): the i-th bounty spawn marker (+0xB4 count, +0xB8 keys, from FindBountySpawnPoints)
  → its attribute 0xDB89AB5C (the tag the rap sheet's bounty jump shows) — the FE's; not ported.
* **GRaceStatus::GetLapPosition(racer, lap)** (0x5DC320): 1 + the racers whose lap time for that lap is positive and smaller
  (GetLapTime 0x5DC280) — the results' lap placings (Entrant.LapPlaces). **GRacerInfo::GetHudPctRaceComplete** (0x6004D0):
  `f = clamp01(FUN_005FCD80())`, result = 100·f + (1 − f)·mPctRaceComplete [?: the blend source].
* **GRaceParameters::ExtractDirection(angleOffset)** (0x5DCD00): the identity yawed by −(offset + Rotation)/360 turns applied to
  (0, 0, 1) in the sim frame = (cos r, sin r) in game X/Y — the convention GetStartDirection / GetCheckpointDirection /
  GetFinishDirection (0x5FAE00 / 0x5FB240 / 0x5FB060) and the port's triggers use.

### 17.3 Function-by-function pass (symbols 0x5D9000-0x620000 of the G* / Lua* classes: 545 functions, 84.7 KB)
Every function whose name or address is not referenced by the port or the specs was listed (scratch `gp_gaps.txt`, 19 KB in
all). What remains unported, with the reason:
* Engine plumbing with no Unity counterpart: GManager::GManager (0x5F7D80), ReserveStockCar / ClearStockCars /
  PreloadStockCarsForActivity (0x5F6490 / 0x5EBF90 / 0x602990: the car loader's reserved slots), PreloadTransientVaults /
  GVault / GObjectBlock::CalcNumConnections / CollectionIsInstanceOfTemplate / FindInstances_GHandler_ (0x5F7390, 0x5E5330,
  0x5E5220, 0x5F3D90: the vault object blocks, §9.1), RefreshWorldParticleEffects and the GTrigger / GIcon particle effects
  (0x5EBD90, 0x5E2B10, 0x5E2A50: the flares), GRaceDatabase::Init / BuildBinList / NotifyVaultUnloading / SerializeBins /
  LoadBestScores (the save's layout; Profile.RaceScores holds the same data), GRaceCustom (0x602680: the custom quick race's
  attribute overrides — the FE's QuickRaceScreens.Custom), LuaRuntime::DumpStack / CreateState / TakeResetSnapshot (the heap
  pager and the Lua debug dump), LuaPostOffice::RouteMessage (the C# runtime delivers directly), LuaBindery::GetGlobalTable.
* Accessors already read as attributes: every GRaceParameters::Get* (GetCashValue, GetNumLaps, GetEventID, GetEventHash,
  GetBarrierName / GetBarrierIsFlipped, GetIsLoopingRace, GetRankPlayersByPoints / ByDistance, GetScriptedCopsInRace,
  GetNeverInQuickRace, GetIsChallengeSeriesRace, GetIsCollectorsEditionRace, GetStart / Finish / Checkpoint Position / Direction),
  GMilestone / GSpeedTrap::GetJumpMarkerKey (the `taggedcarmarkerK` jump markers; the rap sheet's), GRacerInfo::SetIndex.
* GManager::IncValue (0x5EDB50) = TrackValue(name, current + 1) for the counting milestones; GetPlayerPursuitInterfaces (0x5EB9E0).
So the Gameplay files are ported as far as they have gameplay in them; the remainder is memory / vault / particle management.

### 17.4 The career stable's heat / impound calls (FEImpoundData / FECareerRecord, Runtime/UI/Safehouse/FEImpound.cs) — wired
Callers in speed.exe (capstone xrefs): **Game_NotifyRaceFinished** (0x6119F0) calls AdjustStableHeat_EventWin (0x611C17 → 0x590260)
and AdjustStableImpound_EventWin (0x611C1D → 0x580990) on **every** career win, after the cash and the first-win EAwardUpgrade loop
(correction to §2.5: not first-win only) → `CareerStable.OnCareerRaceWon` from GameSession.AwardWin. **AICopManager::PursuitIsEvaded**
(0x411070): the player's pursuit → TrackValue(pursuits evaded, length), NotifyPursuitEnded(true), **AdjustStableImpound_EvadePursuit**
(0x4110F3), then (roaming) the music control and EShowMilestones; for the perp's player index 0/1: MPerpEscaped and
**AdjustStableHeat_EvadePursuit** (0x411211). **AICopManager::CommitPursuitDetails** (0x411240, from UpdatePursuits 0x43EB6E at a
pursuit's end; skipped during a Challenge Series race): the perp's FECareerRecord ← **CommitPursuitCarData(GInfractionManager's
infraction bits, bounty = IPerpetrator +0x44 + +0x40, evaded = !IPursuit::IsBusted (+0x74))** (0x411373), UserProfile::CommitPursuitInfo,
IReputation::SetBounty(GetTotalBounty), MNotifyPlayerRep when not busted. Port: `Gameplay/CareerStable.cs`; one-line hooks in
PursuitSystem.Evaded / BustedResult (`OnPursuitEvaded` / `OnPursuitBusted`; the infraction bits from PursuitStats.InfractionKinds [?: order]).
FEImpoundData::NotifyBusted / BecomeImpounded / NotifyWin are called only by the FE's QRCarSelectBustedManager::SetSelectedCar (0x7AD77D /
0x7AD7BA, the busted car-select screen); the port runs NotifyBusted + the strike-limit impound at the bust [?]. **AdjustStableHeat_
MilestoneComplete (0x5901E0) has no caller in v1.3** (no call or pointer reference) — not wired. UnlockSystem's "new unlock" flags:
UnlockUnlockableThing sets the level and the new level (ShopUnlocks.Award → Profile.UnlockNew), applied for every bin reached by
ShopUnlocks.Ensure (GameSession.AwardWin on the rival's fall); DoesCategoryHaveNewUnlock (0x576470, FEMarkerSelection) reads them — already in place.

## 18. Round 9 (final round): the hooks left over, GManager's icon pool, the last bindings, a function-by-function account

### 18.1 GManager's own icon pool — hiding spots and pursuit breakers (resolves world_interaction.md §2.5's hiding-spot source)
* GManager +0x2BC count, +0x2C0 visible count, +0x2C4 array of 200 icon pointers (FastMem 0x320 bytes in InitializeVaults 0x5F8080).
  **AllocIcon** (0x5E9EC0; (type, position, rotation, disposable)): null when 200 are allocated; GIcon ctor 0x5E5970 (type +0, flags +2,
  section ids +4 / +6 = −1, position +0x10, rotation·65536/360 +0x1C, FindSection 0x5DE590); disposable → flag 0x10.
  **FreeDisposableIcons(type)** (0x5EEF00): disposable icons of that type freed (sub_5EDD60).
* **RefreshZoneIcons** (0x5EF640; from InitializeVaults 0x5F8189 and NotifyGameZonesChanged 0x5F19CA): FreeDisposableIcons(11), then for
  every TrackPathZone of type 8 (TrackPathManager::FindZone(0, 8, prev) 0x727480 loop) AllocIcon(11, (Position.x, Position.y, Elevation),
  0, true) and flags |= 3; SpawnAllLoadedSectionIcons when +0x118. 34 zones in L2RA (harness `r9gp icons`).
* **Pursuit breakers**: InitializeVaults 0x5F818E: FreeDisposableIcons(12), then sub_727240(sub_5EBE70, bStringHash("IconMarker")) walks
  the TrackPositionMarker list (0x9AA494): every marker whose NameHash (+8) matches gets AllocIcon(12, marker +0x10, 0, true), flags |= 3.
  37 IconMarker markers in L2RA.
* **HidePursuitBreakerIcon** (0x5E4850, from the EBreakerStopCops handler sub_620DF0 → MBreakerStopCops then HidePursuitBreakerIcon(pos, 20)):
  every type-12 icon whose position is within the radius (3D, render space; the event position converted sim → render) loses flag 1.
  **RestorePursuitBreakerIcons(section)** (0x5E48F0): type-12 icons of that section (+4 or +6), or all for −1, get flag 1 back; callers
  World_RestoreProps (0x74D418, −1; reached from ERestartRace 0x63090B, EResetProps 0x62495B and NotifyWorldService 0x5EBBC8 with
  resetProps) and sub_6828D0 (a streamed section's smackables restored).
* Port (`Runtime/Game/Triggers.cs`): `GameIcons` (AllocIcon / FreeDisposableIcons / RefreshZoneIcons / HidePursuitBreakerIcon /
  RestorePursuitBreakerIcons; the pool is built on first use), `GIcon.FlagDisposable`, `GameTriggers.Icons` (GatherVisibleIcons'
  source: the triggers' icons then the pool). Hooks: WorldMapScreen.AddTriggerIcons iterates `set.Icons` (FE owner's file), Smackable's
  EBreakerStopCops branch calls HidePursuitBreakerIcon (world owner's file), RaceManager.RestartRace and Drive's EResetProps run
  World_RestoreProps' two parts (Smackable.RestoreAll + RestorePursuitBreakerIcons). [The per-section restore of sub_6828D0 is folded
  into the −1 restore: the port's icons carry no section ids.]

### 18.2 The other hooks the round brief listed
* **eNIS_Busted (4) on** is not posted by a script (no gameplay.bin chunk contains the string; harness `r9gp strings eNIS`): its only
  poster is **NISActivity::Play** (0x6F587B): when the scene's SceneType (CAnimScene → NisScene +0x18, sub_4478A0) is 2,
  SetNISRaceDriverVisible(0), IVisualTreatment::Reset + state 1, then ESndGameState(4, true). Port: one line in NisPlayer where the
  scene starts (animation owner's file) → `SoundState.SetGameFlow(FlowNisBusted, true)`; NisSound.Finished turns it off (~NISActivity).
* **Audio.SetFlag** (Audio_SetFlag 0x6048E0) now executes in Drive: the name table 0x8F5D78 (eNIS_Start 2, eNIS_321GO 3, eNIS_Busted
  4, eNIS_EndofRace 5, eFE_PostRaceScreen 6, eFE_CameraScreen 7, eLOAD_GeneralLoading 10, eLOAD_BustedLoading 11) → ESndGameState(flow,
  on), and by the jump table 0x6049C4 / 0x6049E4 SetSoundControlState(on, 7 "LuaCall:end of race" for 5, 1 "fe cam" for 7, 0 "loading"
  for 10, 14 "bust load" for 11). The scripts use 2, 3, 5, 6 (basicrace intronis / countdown / outronis / postrace). `LuaDrive.AudioSetFlag`.
* **WorldAnimations.TrackNumber**: the value InstantiateAnimTree (0x44FFF0) filters by is the race settings' TrackNumber (0x904FB8)
  % 100 — RaceStarter (sub_57EA20 / sub_57EB40 / sub_57EBE0) always writes **0x7D0 (2000, L2RA)** and StartSkipFERace copies
  SkipFETrackNumber (0x8F86A4); GRaceParameters has no track field. So TrackNumber % 100 = 0 in every retail race. GameFlow.StartRace
  sets `WorldAnimations.TrackNumber = 2000`.
* **PursuitData.mExitToSafehouse**: set by PursuitData::PopulateData(…, exitToSafehouse) (0x56A440) from EShowMilestones' argument
  (0x642083); AICopManager::PursuitIsEvaded (0x41116E) passes IPursuit::GetEnterSafehouseOnDone (AIPursuit 0x433C40 = +0x240), which only
  AIPursuit::EndPursuitEnteringSafehouse (0x40B040: evade level 1, PS_EVADED, +0x240 = 1) sets — the HUD's HUDACTION_ENGAGE_EVENT during
  PS_COOL_DOWN inside a "safehouse" menu zone (FEPkg_Hud.cpp:786-805). ~PostRacePursuitScreen (0x58E320) then posts MEnterSafeHouse
  ("safehouse") (the menu gate's entersafehouse). Port: Systems' engage (`PursuitSystem.EndPursuitEnteringSafehouse`, PursuitSystem.Script.cs)
  evades the pursuit and opens the safehouse (Systems.EnterZone) when the pursuit result screen closes [ours: the flag rides on the front
  end's Closed event instead of PursuitData].

### 18.3 Bindings and events changed this round (lua_hooks.md §4.2)
* Game.ShowTriggerIcon / HideTriggerIcon (0x604D20 / 0x604D30 → 0x5E2D10 / HideIcon 0x5E2D30): the trigger's GIcon flags 1 | 2 set /
  cleared — executed (they were NoOp).
* EResetProps (basicrace intronis) → World_RestoreProps — executed (was NoOp).
* Game.WarpToMarkerWhenRoaming → GManager::WarpToMarker (0x5EBC80, resetProps 0 from GRaceStatus::Update 0x602CF4) / NotifyWorldService
  (0x5EBB10): the warp now also disengages the GPS.
* Still NoOp, verified: MiniMap.AddEngagedRace (0x604B50 reads attribute 0x9F743A0E and discards it), Game.SetRacerLapsLeft (0x604DB0:
  GetRacerInfo only), Game.DoSpecialFinalization (0x605890: GetRaceFromActivity only), E911Call / EDispIntroRace (event classes whose
  destructor does nothing: 0x62A460), Game.ReloadWorld (0x6057E0: EReloadGame → GManager +0x2D4 = the race's hash and, racing,
  TheGameFlowManager reload 0x6672D0 — called by basicrace initial only when Game.SetCurrentTimeOfDay / DoSpecialSetup return true,
  which the port answers false: it changes the time of day without reloading), Game.SetHasRapSheet (0x605670: an FE database flag).

### 18.4 GRaceDatabase::UpdateRaceScore correction
0x60237D-0x602422: the score info's top speed and average speed are **max(stored, new)** (udecfix16 / FixedPoint::GetScale), the new
average being GRacerInfo::CalcAverageSpeed (0x5E3390: DistanceDriven +0xFC / (Sim::GetTime() − start +0x14C + accumulated +0x150 while
the clock runs, else the accumulated time)). RaceDatabase.UpdateRaceScore keeps the max now (it overwrote them).

### 18.5 Function-by-function account of what §17.3 left (symbols 0x5D9000-0x620000; scratch gaps.py / acc.py / dead.py)
Each address below is either ported (the port location), an attribute read the port does in Career.cs / RaceDatabase (the attribute it
reads), or engine plumbing with the reason it has no Unity counterpart.
* **GRaceParameters accessors** (index field when the race index entry exists, else the vault attribute): GetNumLaps 0x5FBA00 (idx +0x28,
  NumLaps), GetEventID 0x5FBA70 (EventID), GetRaceLengthMeters 0x5FB850 (idx +0x1C, RaceLength), HasFinishLine 0x5FAF10 / GetFinishPosition
  0x5FAF70 (racefinish), GetStartPosition 0x5FAD10 (racestart), GetCheckpointPosition 0x5FB150, GetNumCheckpoints 0x5FCA40, GetTimeLimit
  0x5FC260, GetMaxHeatLevel 0x5FC2C0, GetUseWorldHeatInRace 0x5FC380, GetForceHeatLevel 0x5FC3E0, GetInitialPlayerSpeed 0x5FC4A0,
  GetIsRollingStart 0x5FC500, GetIsEpicPursuitRace 0x5FC560, GetPlayerCarType 0x5FC5C0, GetKnockoutsPerLap 0x5FC6E0, GetCheckpointsVisible
  0x5FCA80, GetTrafficPattern 0x5FCBA0, GetPhotoFinishCamera 0x5FCC00 (FinishCamera), GetTimeOfDay 0x5FCCC0 (TOD), GetStartTime 0x5FCD20,
  GetStartPercent 0x5FCD80, GetBarrierCount 0x5FCB60, GetNumOpponents 0x5FACD0, GetRaceType 0x5FAA20 (EventIconType), GetRegion 0x5FAAB0,
  GetEventHash 0x5FAB40, GetTrafficDensity 0x5FABB0 (TrafficLevel), GetDifficulty 0x5FAC10 (DifficultyLevel), GetChallengeType 0x5FA9A0
  (MilestoneName), GetChallengeGoal 0x5FBB60 (ThreshholdValue), GetReputation 0x5FB8B0, GetCashValue 0x5FB920, GetRivalBestTime 0x5FBAD0,
  the flag getters 0x5FBBD0-0x5FC1F0 (AvailableQR, AvailableOnline, InitiallyUnlocked, DDayRace, BossRace = BossRaces membership,
  PursuitRace, IsLoopingRace, RankPlayersByPoints / ByDistance, CopsInRace, ScriptedCopsInRace, NeverInQuickRace, ChallengeSeriesRace,
  CollectorsEditionRace: index byte +0x18 / +0x1A), GetCopDensity 0x5FD240, GetOpponentChar 0x5FD2D0 (Opponents), GetActivity 0x5FCE40,
  GetCollectionKey 0x5FB800, ExtractPosition 0x5DCCA0 (Position) — all read by `Core/Career.cs` RaceEvent from the same attributes.
  Read but **inert in the data**: GetCatchUp / CatchUpOverride / CatchUpSkill / Spread / Integral / Derivative (0x5FC7A0-0x5FC980; only
  ComputeCatchUpSkill 0x5FFA1D and MakeCatchUpData 0x5FFCC0 read them; CatchUpOverride is false everywhere, races.md §5.3), GetMaxRaceHeatLevel
  0x5FC440 (= MaxHeatLevel, AIPursuit ctor 0x443130), GetPhotoFinishTexture 0x5FCC60 (attribute 0x038A3B53, the FE's photo-finish screen
  0x58E6C4). FE / road-network readers, not gameplay: GetPlayerCarPerformance 0x5FC620 (QuickGame::CreateCars 0x6F4945, the quick race car's
  performance tier — the FE's car choice), GetShortcut 0x5FB340 / GetNumShortcuts 0x5FCAE0 / GetNumBarrierExemptions 0x5FCB20
  (WRoadNetwork::ResolveShortcuts / ResolveBarriers / WRoadNav::MakeShortcutDecision 0x788904 / 0x788A83 / 0x76FFA9: the road owner's
  RoadGraph.MarkBarriers; no retail race has Shortcuts). Lifetime: GRaceParameters ctor 0x602590 / dtor 0x5E9980, GetIsLoaded 0x5E3A00,
  NotifyParentVaultLoaded 0x5E3A70, BlockUntilLoaded 0x5FA960 (vault paging; the C# database is resident), GetGameplayObj 0x5DCC70.
  GenerateIndex 0x600CD0 (the race index cache, harness-verified in round 2 §9.2), GetBoundingBox 0x5FCEA0 (idx +0x2C; the FE map's race
  bounds), GetIsAvailable 0x5FD160 (RaceDatabase.IsAvailable).
* **GRaceBin**: GetBarrierCount 0x5D9620 / EnableBarriers 0x5DD090 / DisableBarriers 0x5DD140 (Barriers; GameSession.FreeRoamBarriers +
  RoadGraph.MarkBarriers), GetBossRaceCount / Hash 0x5D97A0 / 0x601020 (BossRaces), GetWorldRaceCount / Hash 0x5DA7A0 / 0x601060
  (WorldRaces), GetBaselineUnlock(Count) 0x5DCFA0 / 0x5D96A0 (attribute 0xBAF89280; Career.cs bin unlocks), GetBinNumber 0x5DCEA0
  (BinIndex), Get{Base,Max,Scale}OpenWorldHeat 0x5DCEE0 / 0x5DCF00 / 0x5DCF20 (attributes 0x8F186AC4 / 0xE8C24416 / 0x1823B89E; the bin
  heat range of the AIPursuit ctor, §18.6), GetRequiredBounty / Challenges / RaceWins 0x5DD160 / 0x5DD180 / 0x5DD1A0, GetCompleted
  Challenges / GetAwardedRaceWins 0x5DD1C0 / 0x5DD1D0 (GameSession.ChallengesDoneInBin, RaceDatabase), RefreshProgress 0x6010E0
  (RaceDatabase.RefreshBinProgress), GetChildVault 0x5DCE90 (vault paging). GRaceDatabase::GetBinNumber 0x5E3980 / GetRaceFromKey 0x600A20
  / GetRaceParameters 0x5DC930 / GetRaceFromHash 0x5FB710 / GetRaceFromActivity 0x6022D0 / GetRaceCount 0x5DC920 / GetBinCount 0x5DC950 =
  Career.EventFor / Bins; RefreshBinProgress 0x6022A0 (RaceDatabase); BuildBinList 0x5EB640, Init 0x603A40, ctor 0x6039C0,
  NotifyVaultUnloading 0x5E38D0, SerializeBins / DeserializeBins / LoadBestScores 0x5E35D0 / 0x5E98D0 / 0x5FB770 (the save's layout;
  Profile.RaceScores and the JSON save hold the same data); the custom race (AllocCustomRace 0x603440, FreeCustomRace 0x5E39E0,
  DestroyCustomRace 0x5DC970, ClearStartupRace 0x5DC9D0, GetStartupRace 0x5DCA00, GRaceCustom::* 0x5E74C0-0x5FD6F0, CreateRaceActivity
  0x5FD370) is the quick race's attribute override object — the FE's QuickRaceScreens build the RaceEvent copy directly.
* **GMilestone / GSpeedTrap / GTimer / GInfractionManager**: GetBounty 0x5E54F0 / 0x5E56C0 (Bounty), GMilestone::GetLocalizationTag
  0x5E55A0, GetJumpMarkerKey 0x5E5640 / 0x5E5810 (SpawnPoint; the rap sheet's jump), GetCurrentValue 0x5EE220 (the tracked value),
  NotifyTriggered 0x601730 (GameSession.CompleteSpeedTrap), GManager::LoadMilestones / LoadSpeedTraps 0x5DDBD0 / 0x5DDCB0,
  AllocateMilestones / AllocateSpeedTraps 0x5F4BF0 / 0x5F4E50, ResetMilestones / ResetSpeedTraps / ResetMilestoneTrackingInfo 0x5F4D40 /
  0x5F4FA0 / 0x5F62F0, LoadMilestoneInfo 0x5F6430, GetMilestone / GetNumMilestones / GetFirst / GetNext 0x5DDA50 / 0x5DDA40 / 0x5E46D0 /
  0x5DDA70, GetFirstSpeedTrap / GetNextSpeedTrap 0x5E9D40 / 0x5E4770, EnableBinMilestones 0x5E46F0 (EEnterBin 0x62C347: the bin's
  milestones in state 1 → 2) and EnableBinSpeedTraps 0x5E9D60 (0x62C353: flags |= 3), GetValue / GetBestValue 0x5EDBC0 / 0x5EDC00 /
  0x5EDC60 — the career's milestone / speed-trap lists in Career.cs, Profile.Milestones, GameSession.ActiveSpeedTraps / ScorePursuit and
  the Lua GameplayState (§16.1). GTimer::GetTime 0x5DDEC0 and GManager::UpdateTimers 0x5E4C10 = GameplayState's GEventTimers.
  GInfractionManager::PursuitStarted / GetNumInfractions / DidInfractionOccur / ReportInfraction 0x5DE310 / 0x5DE330 / 0x5DE350 /
  0x5FDDA0 = PursuitStats.Infractions / InfractionKinds.
* **GRaceStatus / GRacerInfo**: ctor 0x603720 / Init 0x603900 (RaceManager.Setup), AddSimablePlayer 0x5E8BF0 (Game.InitRacers),
  GetRacerInfo 0x5E8B20 / 0x5DBED0, GetRacerCount 0x5DBEF0, StopMasterTimer 0x5E8BA0, GetRaceTimeElapsed 0x5E3060, IsLoading 0x5ED880,
  SetRaceActivity 0x6031F0, SetCheckpointTime 0x5DC2F0, SetLapTime 0x5DC260, EnableBarriers 0x5FE330, RefreshBinWhileInGame 0x5DBD70 —
  RaceManager / RaceManager.Drive; GRacerInfo::SaveStartPosition 0x600540 (SaveStartPositions), SetIndex 0x5E84D0, ForceStop 0x5E9240
  (AIRacer.ForceStop), CalcAverageSpeed 0x5E3390 (§18.4), AddToPointTotal 0x5DC6D0 (AwardPoints), AreStatsReady 0x5DC7D0 (the results
  screen's stat rows), SetName 0x5DC6B0, ChooseRandomName 0x5DC800. The trigger vector helpers 0x5F3A70 / 0x5F54A0 and the binder thunk
  0x6103C0 are STL / Lua glue.
* **GTrigger / GIcon / GManager icons**: GIcon ctor 0x5E5970, SetPosition 0x5E5A90, FindSection 0x5DE590 (section ids for the per-section
  restore, not kept), CreateParticleEffect / CreateGeometry 0x5DE610 / 0x5DE690, GatherVisibleIcons 0x5E4940 (+ its sort 0x5DDB20:
  GameTriggers.Icons), RefreshSpeedTrapIcons 0x5E9DE0, SpawnAllLoadedSectionIcons 0x5EDE20, NotifyCollisionPackLoaded 0x5EED60 (Spawn /
  UnspawnSectionIcons per streamed section: EventMarkers' columns), CalcMapCoordsForMarker 0x5E44D0 (the FE map's projection);
  GTrigger::HideIcon 0x5E2D30 (§18.3), GetTargetActivity 0x5E2A40, GetType 0x5EF420, GetPosition 0x5DB5E0 (Triggers.cs); the GTrigger
  particle effects ClearParticleEffects / EnableParticleEffects / RefreshParticleEffects / NotifyEmitterGroupDelete 0x5DB520 / 0x5DB570 /
  0x5E2C60 / 0x5DB5B0 are the trigger flares (not ported: the port's markers are EventMarkers'), GObjectIterator<GTrigger> 0x5E7470 /
  0x5E1780 and dtor 0x5F1750 are containers.
* **Object lifetime (vault paging)**: GActivity / GTrigger / GCharacter / GMarker / GState / GHandler ::DeleteObjects (0x5E77B0 /
  0x5E76F0 / 0x5E7770 / 0x5E7730 / 0x5E77F0 / 0x5E7830), GVault::DestroyGameplayObjects / LoadSyncTransient / IsLoaded (0x5EE090 / 0x5FA860
  / 0x5DE1F0), GActivity::RegisterMessageHandlers / UnregisterMessageHandlers (0x5EE870 / 0x5ED6F0: StategraphRuntime subscribes per
  state), GHandler::NotifyBytecodeFlushed 0x5DD400 and the state → handler map 0x5EC5B0 / 0x5F2530 (StategraphRuntime's dictionaries),
  GRuntimeInstance::GetPosition / GetDirection 0x5E2970 / 0x5DACF0 (LuaTriggers.PointOf), GCollectionKey 0x5DAD50 / 0x5E29E0;
  GManager::Init 0x5FA430, FindKeyReductionShifts 0x5F2F60 (the object-state key compression, GameplayStateStore keys by full key),
  ConnectRuntimeInstances 0x5F18A0, FindVault 0x5E3E60, FindInstance 0x5DD930 (StategraphRuntime.Instance), FindBountySpawnPoints /
  GetBountySpawnMarker(s) 0x5F4AD0 / 0x5DDB00 / 0x5DDAF0 (the rap sheet's bounty jump, §17.2), GetHasPendingSMS 0x5EDFC0 (Sms),
  ServicePendingCharacters 0x5FDD60 (GManager::Update: characters in spawn state 2 / 3 retried by sub_5FD820 — ScriptedCharacters spawns
  at once), UnspawnUselessCharacters 0x5EEFA0 (§17.2), NotifyWorldService / WarpToMarker 0x5EBB10 / 0x5EBC80 (§18.3).

### 18.6 The bin's open-world heat (resolves ai_cops.md §10 item 10 "bin base / max heat taken as 1 / 5")
GRaceStatus::GetBinBaseHeat / GetBinMaxHeat / GetBinHeatScale (sub_404B50 / sub_404B70 / sub_404B90: the current bin (+0x196C) →
GRaceBin::GetBaseOpenWorldHeat / GetMaxOpenWorldHeat / GetScaleOpenWorldHeat 0x5DCEE0 / 0x5DCF00 / 0x5DCF20, else 0 / 10 / 1). Data
(gameplay.bin race_bin_NN): race_bin 0 / 1 / 1; **bin 15: 1 / 2; 14-13: max 2; 12-9: max 3; 8-5: max 4; 4-1: max 5; 16: 1 / 1**.
The AIPursuit ctor (AIPursuit.cpp:618-653, PC 0x442FEB-0x44316B; GRaceStatus +0x1964 = the race context, 2 = career): career with race
parameters → the epic pursuit 6 / 6 or the bin's base / max; quick race (not a challenge race) → 1 / 5; else (career free roam,
challenge races) the bin's base / max / scale, then UseWorldHeat → ForceHeatLevel .. GetMaxRaceHeatLevel, else MaxHeatLevel caps the
maximum; OnAttached raises the perp's heat to the base; the escalation clamps to base .. max (:1687) — so early in the career the
free-roam heat stops at 2. Port: `BlacklistBin.BaseOpenWorldHeat / MaxOpenWorldHeat / ScaleOpenWorldHeat` (Career.cs),
`PursuitSystem.AIPursuitHeatRange` (PursuitSystem.Script.cs), one-call hooks in PursuitSystem.StartPursuit and the RacerPursuit ctor
(police files). [port: the heat stays ≥ 1 (the rows start at level 1); the Challenge Series pursuits keep their forced 1 .. MaxHeat;
mHeatScale (+0xCC) is stored by the exe but read nowhere in AIPursuit.cpp, so it is not kept.]

### 18.7 Follow-up hooks to the other owners' final APIs
* **ECellCall** (Drive): ~ECellCall (0x62B1CD) → MiscSpeech::SMSCellCall — `LuaDrive` calls `SpeechAI.SMSCellCall(id)` after the SMS.
  NotifyCellCallStarted / NotifyCellCallComplete are now posted from `CopSpeechManager.CellCallStarted / CellCallCompleted` (GameSpeech.cpp
  :340 / :391, SpeechManager.cpp:69-77 / :957; audio.md §25), counted thread-safely and posted on LuaGameHost.Update; the old 6 s timer is
  gone. [ours: a call that never starts within 2 s — no speech module, Shadow mode — posts both, so a script never waits forever.]
* **Opponent parts**: GRacerInfo::CreateVehicle (0x5E8E60, 0x5E8F9E-0x5E8FD1) creates every opponent with Performance {1, 1, 1};
  RaceManager.SpawnOpponent now uses `PhysicsInfo.MatchPerformance(car, 1, 1, 1, out owned)` (physics.md §27: every package at its top
  level) instead of round(3 × MinimumAIPerformance), which stays only as the fallback for a car without ratings. The play tests only
  require opponents to drive (CareerTests: > 250 m in 35 s), which faster cars still satisfy.
* **DamageCopCar**: TrafficVehicle.CausedByPlayer for cops = the player's car touched this cop in the last second [?: the exe reads the
  model's ICause]; CopCar.FlipCheck (the port's duplicate of the flip rule) is removed, so only VehicleDamage.CheckUpright's exe rule
  (6 s not upright, at once when caused by the player) destroys a flipped cop; CopCar still credits the takedown when Sim.Damage is
  destroyed and the player touched it within 3 s.

### 18.8 Follow-ups for the FE owner's screens
* **Infraction bits**: GInfractionManager::InfractionType (GInfractionManager.h:11-20; DidInfractionOccur 0x5DE350 = `flags & type`,
  PursuitStarted 0x5DE310 clears them) — Speeding 1, Racing 2, Reckless 4, Assault 8, HitAndRun 16, Damage 32, Resist 64, OffRoad 128.
  `PursuitStats.InfractionKinds` / `InfractionBits` now use this order (they had Hit and Run / Assault / Resist / Damage swapped and two
  invented kinds), so CommitPursuitCarData's bits, the stable's Unserved[8] and the fines land on the right types; readers checked:
  CareerStable (InfractionBits), RapSheetRecord.TypeOf / BitsOf (same order), SpeechAI.InfractionId and HudExtras (by name).
* **Game won**: EQuitToFE's game-won branch (0x6240C2-0x624139: IsFinalEpicChase 0x56DC00 and CheckRaceScoreFlags(hash, 2) →
  SetPlayerHasBeatenTheGame, FEGameMode |= 0x40000, garage type 2, GameWon.fng). The port has no EQuitToFE after the chase, so the
  1.8.1 activity reaching `done` with the race won stands for it: LuaDrive runs `GameWonScreen.Begin()` on the next frame once no race,
  NIS or FE screen is up [?: the exe's exact moment is the post-race / outro screen's quit].
* **Busted strike**: CareerStable.OnPursuitBusted keeps only CommitPursuitDetails (0x411373); FEImpoundData::NotifyBusted /
  BecomeImpounded are the busted screen's (InfractionsScreen applies them 1:1), and its UndoStandInStrike call is removed.
* **Cost-to-state details**: `PursuitStats.CopCarsDeployed / SupportVehiclesDeployed / TrafficCarsHit / HeliSpawns / RoadblocksDeployed /
  SpikeStripsDeployed / HeliSpikeStripsDeployed / PropertiesDamaged / PropertyDamageValue` — the AIPursuit counters of CalcTotalCostToState
  (AIPursuit.cpp:733-746) that HighScoresDatabase::CommitPursuitInfo (RaceDB.cpp:55-62, 70) adds to CostToStateDetails and
  PD_HELICOPTERS_INVOLVED; kept by PursuitSystem.UpdateCostToState (a smashed prop with a cost now also counts one property);
  RapSheetRecord.Commit reads them (it estimated them before). Heli spike strips stay 0 (the port's helicopter drops none).
* **CareerSettings flags**: Profile.BeatenTheGame (0x4000) and Profile.HasDoneMapLoadingTip (0x80000); GameWonScreen and LoadingScreens
  use them instead of the FEDatabaseData stand-ins (those two fields are now unused).
* **MikeMannBuild** (0x92612C) is in .data's zero-filled tail (past the 0x24000 raw bytes), written only by ActivateEasterEgg
  (0x65C377): 0, not 1 — EasterEggs.MikeMannBuild = 0.
