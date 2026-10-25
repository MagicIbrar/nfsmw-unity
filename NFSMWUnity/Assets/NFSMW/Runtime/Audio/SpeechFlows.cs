using System.Collections.Generic;
using NfsMw.Game;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>Speech::SpeechFlow (Speech/SpeechFlow.h): a state with the previous one, and a busy counter.</summary>
    public abstract class SpeechFlowBase
    {
        protected readonly SpeechAI AI;
        public int State { get; protected set; }
        public int LastState { get; protected set; } = -1;
        protected int Busy;

        protected SpeechFlowBase(SpeechAI ai) { AI = ai; }

        public void ChangeStateTo(int s)
        {
            if (s != State) LastState = State;
            State = s;
        }

        public virtual void Reset()
        {
            ChangeStateTo(-1);
            Busy = 0;
        }

        protected CopSpeechManager Mgr => AI.Mgr;
        protected NfsMw.Core.SpeechTune Tune => AI.Tune;
    }

    /// <summary>
    /// Speech::PursuitFlow (Speech/PursuitFlow.cpp): the setup of a pursuit. After PursuitInitDelay of an active
    /// pursuit the cause is decided (spotted after a fast blow-by past a cop, a unit rammed in the last 10 s, a pursuit
    /// challenge = scripted, else unknown) and a branch runs:
    /// <list type="bullet">
    /// <item>primary: the closest unit becomes a primary leader and calls "attempting vehicle stop" (+ the reason),
    /// dispatch "go ahead", a vehicle report (colour, make, speed); repeat offenders get "suspect confirmed" / driver
    /// history; a reacquired suspect gets "reinitiate pursuit".</item>
    /// <item>spotter: the closest unit is the spotter (or "spotter wanted" at high intensity), maybe a vehicle report,
    /// dispatch go-ahead / ack, another unit replies; then waits for a unit with sight, which answers (spotted /
    /// spotter reply) and dispatch escalates the pursuit.</item>
    /// <item>scripted: dispatch escalation and a vehicle description.</item>
    /// </list>
    /// A player who stops gets an interruption ("pull over"); the setup ends with "initiate pursuit" (60 mph or more),
    /// a location report (below high intensity) and the strategy flow takes over.
    /// </summary>
    public sealed class PursuitFlow : SpeechFlowBase
    {
        public const int k911Reported = 0, kCopAssaulted = 1, kSpotted = 2, kReacquired = 3, kScripted = 4, kCopAssaultedScripted = 5, kUnknown = 6;
        public const int kTransition = -1, kCullCheck = 0, kCloseInCheck = 1, kPrimaryBranch = 2, kSpotterBranch = 3, kScriptedBranch = 4,
            kWaitForSpotter = 5, kLostWhileSpotWait = 6, kPlayerStopped = 7, kBailout = 8, kChangeTarget = 9, kTerminal = 999;

        public int Cause = kUnknown;
        SpeechCop _firstOnScene;
        bool _reqRestart, _avsUnitRammedSaid;
        int _speaker;

        public PursuitFlow(SpeechAI ai) : base(ai) { }

        public bool IsTransitionable => State == kTransition;

        public void OnCopRemoved(SpeechCop cop)
        {
            if (_firstOnScene == cop) _firstOnScene = null;
        }

        public override void Reset()
        {
            _firstOnScene = null;
            base.Reset();
            State = kCullCheck;
            Busy = 0;
            Cause = kUnknown;
        }

        public void Update()
        {
            switch (State)
            {
                case kTransition: return;
                case kCullCheck: if (AI.PursuitState == SpeechAI.kActive) ChangeStateTo(kCloseInCheck); return;
                case kCloseInCheck: CloseInCheck(); return;
                case kPrimaryBranch: PrimaryBranch(); return;
                case kSpotterBranch: SpotterBranch(); return;
                case kScriptedBranch: ScriptedBranch(); return;
                case kWaitForSpotter: SpotterWait(); return;
                case kLostWhileSpotWait: LostWhileSpotterWait(); return;
                case kPlayerStopped: PlayerStopped(); return;
                case kBailout: BailoutState(); return;
                case kChangeTarget: ChangeTarget(); return;
                case kTerminal: Terminal(); return;
                default: ChangeStateTo(kCullCheck); return;
            }
        }

        /// <summary>PursuitFlow::Reacquire: not while a 911 call is active (PursuitFlow.cpp:142-148).</summary>
        public void Reacquire()
        {
            if (AI.Is911Active) return;
            Cause = kReacquired;
            ChangeStateTo(kPrimaryBranch);
        }

        /// <summary>PursuitFlow::RequiresRestart: false once "unit rammed" was the stop reason.</summary>
        public bool RequiresRestart()
        {
            var cur = Mgr.Current;
            if (cur != null && cur.Name == "Setup_AttmptVehStp" && cur.Arg("pursuit_type") == Csis.UnitRammed)
            {
                Cause = kCopAssaulted;
                _avsUnitRammedSaid = true;
            }
            if (_avsUnitRammedSaid) return false;
            _reqRestart = true;
            Busy = 0;
            return true;
        }

        void CloseInCheck()
        {
            if (AI.PursuitState == SpeechAI.kInactive || AI.Actors.Count < 1) { Reset(); return; }
            if (!AI.InPursuit) return;
            if (AI.PursuitDuration < Tune.PursuitInitDelay) return;
            float lastBlowby = (float)(AI.Now - AI.BlowbyTime);
            if (AI.BlowbyDistance < Tune.RangeForSpotterBranch && AI.BlowbySpeed >= Tune.SpeedDiffForBlowby && lastBlowby < Tune.BlowbyInterval &&
                AI.PlayerSpeed >= 35f && !AI.AreCopsAhead)
                Cause = kSpotted;
            if (AI.Is911Active) Cause = k911Reported;
            if (AI.IsPursuitRace) Cause = kScripted;   // GetIsPursuitRace (PursuitFlow.cpp)
            if (AI.TimeLastNailedCop < 10f && Cause != kScripted) Cause = kCopAssaulted;
            switch (Cause)
            {
                case kSpotted: ChangeStateTo(kSpotterBranch); break;
                case kCopAssaulted: case kCopAssaultedScripted: case kUnknown: ChangeStateTo(kPrimaryBranch); break;
                case k911Reported: ChangeStateTo(kWaitForSpotter); break;
                default: ChangeStateTo(kScriptedBranch); break;
            }
        }

        bool Stopped => AI.PlayerStopTime >= Tune.MinTimeConsideredStopped && Cause != kCopAssaulted && Cause != kCopAssaultedScripted;

        void PrimaryBranch()
        {
            if (AI.PursuitState == SpeechAI.kInactive) { ChangeStateTo(kBailout); return; }
            if (AI.PursuitState == SpeechAI.kOtherTarget) { ChangeStateTo(kChangeTarget); return; }
            if (Busy == 0)
            {
                var leader = AI.FindClosestCop(false, false);
                if (leader == null) { ChangeStateTo(kCullCheck); return; }
                if (!leader.IsPrimary && !AI.MakeLeader(leader)) { ChangeStateTo(kCullCheck); return; }
                if (leader.IsHeli) { ChangeStateTo(kCullCheck); return; }
                if (Cause == kReacquired) leader.ReinitiatePursuit();
                else
                {
                    if (_reqRestart)
                    {
                        if (_speaker > 0 && leader.SpeakerID != _speaker)
                        {
                            var orig = AI.GetCop(_speaker);
                            if (orig != null) leader.SwapVoices(orig);
                        }
                        _reqRestart = false;
                    }
                    leader.AttemptVehicleStop();
                    _speaker = leader.SpeakerID;
                    if (AI.Leader == null) AI.MakeLeader(leader);
                    AI.Dispatch.GoAhead();
                    if (Cause != kCopAssaulted && Cause != kCopAssaultedScripted) leader.VehicleReport();
                    if (Cause == kCopAssaultedScripted) { ChangeStateTo(kTerminal); return; }
                    if (AI.NumPursuits > 1)
                    {
                        if (AI.NumPursuits > 2)
                        {
                            var rp = AI.GetRandomActiveCop(1, false);
                            if (rp != null)
                            {
                                if (rp != leader) rp.DriverHistory();
                                else AI.Dispatch.DriverHistory();
                            }
                        }
                        else leader.SuspectConfirmed();
                        Cause = kReacquired;
                    }
                }
                Busy++;
                return;
            }
            if (Stopped) { Busy = 0; ChangeStateTo(kPlayerStopped); return; }
            if (!Mgr.Busy)
            {
                if (Busy == 0 || _reqRestart) return;
                ChangeStateTo(kTerminal);
                Busy = 0;
            }
        }

        void PlayerStopped()
        {
            if (AI.PursuitState == SpeechAI.kInactive) { ChangeStateTo(kBailout); return; }
            if (AI.PursuitState == SpeechAI.kOtherTarget) { ChangeStateTo(kChangeTarget); return; }
            if (Busy == 0)
            {
                bool setupSaid = Said("Setup_AttmptVehStp") || Said("Setup_Spotter") || Said("Setup_SpotterReply") || Said("Setup_DispGoAhead");
                if (setupSaid && (AI.PlayerStopTime > 1f || AI.PlayerSpeed < 10f) && AI.Leader != null)
                {
                    AI.Leader.InterruptComposedLow();
                    Busy++;
                }
            }
            else if (AI.PlayerStopTime >= Tune.MinTimeConsideredStopped && AI.Leader != null)
            {
                AI.Leader.InterruptComposedLow();
                ChangeStateTo(kTerminal);
                Busy = 0;
                return;
            }
            if (Busy != 0) return;
            ChangeStateTo(kTransition);
            Busy = 0;
        }

        bool Said(string ev) => Mgr.HasBeenSaid(ev) || Mgr.IsPlaying(ev);

        void SpotterBranch()
        {
            int st = AI.PursuitState;
            if (st == SpeechAI.kInactive) { ChangeStateTo(kBailout); return; }
            if (st == SpeechAI.kOtherTarget) { ChangeStateTo(kChangeTarget); return; }
            if (st != SpeechAI.kActive && st != SpeechAI.kSearching) return;
            if (Busy == 0)
            {
                if (AI.IsHighIntensity || AI.LastInfraction == SpeechAI.InfResist)
                {
                    var cop = AI.FindClosestCop(false, true);
                    if (cop == null) return;
                    _firstOnScene = cop;
                    cop.SpotterWanted();
                }
                else
                {
                    var cop = AI.FindClosestCop(false, false);
                    if (cop == null) return;
                    _firstOnScene = cop;
                    cop.Spotter();
                }
                if (AI.Rand01() > 0.5f) _firstOnScene.VehicleReport();
                if (AI.Rand01() > 0.5f) AI.Dispatch.GoAhead(); else AI.Dispatch.Ack();
                var another = AI.FindClosestCop(false, true);
                if (another != null && (_firstOnScene == null || another.SpeakerID != _firstOnScene.SpeakerID)) another.Reply911();
                Busy++;
                return;
            }
            if (Stopped) { Busy = 0; ChangeStateTo(kPlayerStopped); return; }
            if (!Mgr.Busy && Busy != 0)
            {
                if (AI.NumCopsWithLOS > 0) ChangeStateTo(kWaitForSpotter);
                else
                {
                    _firstOnScene?.LostVisual();
                    ChangeStateTo(kLostWhileSpotWait);
                }
                Busy = 0;
            }
        }

        void ScriptedBranch()
        {
            if (Busy == 0)
            {
                if (AI.IsHeadingValid) AI.Dispatch.PursuitEscalation(); else AI.Dispatch.PursuitEscalationGeneric();
                if (AI.PlayerCarColor > 0 && AI.IsVehicleTypeOK) AI.Dispatch.VehicleDescription();
                Busy++;
            }
            else if (!Mgr.Busy) { ChangeStateTo(kTerminal); Busy = 0; }
        }

        void LostWhileSpotterWait()
        {
            if (Busy == 0)
            {
                if (AI.PursuitState == SpeechAI.kInactive) { ChangeStateTo(kBailout); return; }
                if (AI.PursuitState == SpeechAI.kOtherTarget) { ChangeStateTo(kChangeTarget); return; }
                if (AI.NumCopsWithLOS > 0) { ChangeStateTo(kWaitForSpotter); Busy = 0; return; }
            }
            if (Mgr.Busy) return;
            if (Busy != 0)
            {
                Reset();
                ChangeStateTo(kCullCheck);
                Busy = 0;
            }
        }

        void SpotterWait()
        {
            if (Busy == 0)
            {
                if (AI.PursuitState != SpeechAI.kActive) { ChangeStateTo(kLostWhileSpotWait); return; }
                var see = AI.FindClosestCop(true, true);
                if (see != null && AI.MakeLeader(see) && Cause != kScripted)
                {
                    if (Cause == k911Reported || (_firstOnScene != null && _firstOnScene.SpeakerID != see.SpeakerID))
                    {
                        if (AI.AreCopsAhead || !see.IsPrimary) see.Spotted(); else see.SpotterReply();
                    }
                    if (Cause != k911Reported)
                    {
                        if (AI.Rand01() > 0.5f && AI.IsHeadingValid) AI.Dispatch.PursuitEscalation();
                        else AI.Dispatch.PursuitEscalationGeneric();
                    }
                }
                Busy++;
                return;
            }
            if (Stopped) { Busy = 0; ChangeStateTo(kPlayerStopped); return; }
            if (!Mgr.Busy && Busy != 0) { ChangeStateTo(kTerminal); Busy = 0; }
        }

        void BailoutState()
        {
            AI.GetRandomActiveCop(0, false)?.LoBailout();
            ChangeStateTo(kTransition);
            Reset();
        }

        void ChangeTarget()
        {
            AI.FindClosestCop(false, true)?.FocusChange();
            AI.Dispatch.PursuitEscalation();
            ChangeStateTo(kTransition);
            Reset();
        }

        void Terminal()
        {
            if (Busy == 0)
            {
                var leader = AI.Leader;
                if (AI.PlayerSpeed >= 60f && leader != null && Cause != kSpotted && Cause != k911Reported && Cause != kScripted &&
                    Cause != kReacquired && Cause != kCopAssaultedScripted && Cause != kCopAssaulted)
                    leader.InitiatePursuit();
                if (!AI.IsHighIntensity && leader != null && Cause != kCopAssaulted && Cause != kCopAssaultedScripted) leader.LocationReport();
                if (AI.Havoc >= Tune.MinHavocForSuspectBehavior && Cause != kCopAssaulted && Cause != kCopAssaultedScripted)
                    AI.FindClosestCop(true, true)?.SuspectBehavior();
                Busy++;
                return;
            }
            if (!Mgr.Busy && Busy != 0)
            {
                ChangeStateTo(kTransition);
                Busy = 0;
                _firstOnScene = null;
            }
        }

        /// <summary>PursuitFlow::MessageEventComplete.</summary>
        public void MessageEventComplete(ScheduledSpeech e)
        {
            switch (e.Name)
            {
                case "Setup_AttmptVehStp":
                    if (e.Arg("pursuit_type") == Csis.UnitRammed) _avsUnitRammedSaid = true;
                    break;
                case "Setup_ReInitPursuit":
                    if (AI.NumPursuits > 2)
                    {
                        if (AI.Rand01() > 0.5f && AI.IsHeadingValid) AI.Dispatch.PursuitEscalation();
                        else AI.Dispatch.PursuitEscalationGeneric();
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Speech::StrategyFlow (Speech/StrategyFlow.cpp): the chase itself. A lone unit (or no sight) calls for backup;
    /// one unit in formation announces a self strategy (PIT, ram, rolling roadblock, herding), several the formation
    /// (InitiateStrategy). While waiting it watches formation changes (strategy reset), finishers / collapse (intent to
    /// ram, execute, call to position, bullhorn), units in formation (bullhorn), distance (outrun) and losses of units
    /// or evade level (backup request: initial call, call, reminder after BURemindTime, swarming, with dispatch
    /// replies and ETAs). Outcome compares distance and speed before and after (anticipate success / fail, reset).
    /// <para>The port's formation, finisher and collapse state is private to PursuitSystem; they are read back from the
    /// cops (InPositionGoal, PursuitOffset, Goal) [?].</para>
    /// </summary>
    public sealed class StrategyFlow : SpeechFlowBase
    {
        public const int kTransition = -1, kCullCheck = 0, kSoloCheck = 1, kOutrun = 2, kLost = 3, kReqBackup = 4, kCallToPos = 5,
            kWaiting = 333, kTerminal = 999, kOutcome = 1000;
        const int SOLO = 1, BUDENIED = 2;
        const int PIT = 1, BOX_IN = 2, ROLLING_BLOCK = 3, FOLLOW = 4, HELI_PURSUIT = 5, HERD = 6, STAGGER_FOLLOW = 7;

        int _flags, _formationCount, _formationType, _backupType, _lastBackupType;
        float _dist0, _dist1, _speed0, _speed1;
        double _tRequested;
        readonly List<SpeechCop> _active = new List<SpeechCop>();
        int _lastFormation = STAGGER_FOLLOW;

        public StrategyFlow(SpeechAI ai) : base(ai) { }

        public bool IsTransitionable => State == kTransition || State == kWaiting;

        public override void Reset()
        {
            Terminal();
            base.Reset();
        }

        public void Update()
        {
            switch (State)
            {
                case kTransition: return;
                case kCullCheck:
                    if (AI.PursuitState == SpeechAI.kActive || AI.PursuitState == SpeechAI.kSearching) ChangeStateTo(kSoloCheck);
                    return;
                case kSoloCheck: SoloCheck(); return;
                case kWaiting: Waiting(); return;
                case kTerminal: Terminal(); return;
                case kOutcome: Outcome(); return;
                case kOutrun: Outrun(); return;
                case kLost:
                    // StrategyFlow::Lost: (state != inactive) || ... -> terminal
                    if (AI.PursuitState != SpeechAI.kInactive) ChangeStateTo(kTerminal);
                    return;
                case kReqBackup: ReqBackup(); return;
                case kCallToPos: CallToPos(); return;
                default: ChangeStateTo(kCullCheck); return;
            }
        }

        // ---------------------------------------------------------------- formation read-back (IPursuit)

        /// <summary>IPursuit::GetFormationType: the formation the pursuit chose (PursuitSystem.FormationType, the AIPursuit
        /// port's pick from the pursuitlevels Formations; its values are the Csis formation_type ids). The slot-based
        /// reconstruction below is kept as the fallback while no pursuit system is attached [ours].</summary>
        int FormationType()
        {
            var police = AI.Police;
            if (police == null) return _lastFormation;
            return _lastFormation = police.FormationType;
        }

        int FormationTypeFromSlots()
        {
            var police = AI.Police;
            if (police == null) return _lastFormation;
            bool any = false, ram = false, pit = false, side = false, herd = false, ahead = false;
            foreach (var pc in police.CopCars)
            {
                if (!pc) continue;
                var c = pc.GetComponent<CopCar>();
                if (!c || !c.InPursuit || !c.InFormation) continue;
                any = true;
                if (c.InPositionGoal == CopGoal.Pit) pit = true;
                else if (c.InPositionGoal == CopGoal.Ram) { ram = true; if (Mathf.Abs(c.PursuitOffset.z) < 4f) side = true; }
                else if (Mathf.Abs(c.PursuitOffset.x + 3f) < 0.6f) herd = true;
                else if (c.PursuitOffset.z > 5f) ahead = true;
            }
            int t;
            if (pit) t = PIT;
            else if (ram) t = side ? BOX_IN : ROLLING_BLOCK;
            else if (herd) t = HERD;
            else if (ahead) t = STAGGER_FOLLOW;
            else if (any) t = FOLLOW;
            else if (AI.Heli != null && AI.Heli.Active && AI.NumActiveCopCars == 0) t = HELI_PURSUIT;
            else t = _lastFormation;
            return _lastFormation = t;
        }

        /// <summary>IPursuit::IsCollapseActive || IsFinisherActive (StrategyFlow.cpp:359), from the pursuit itself.</summary>
        public static bool FinisherOrCollapse(SpeechAI ai) => ai.IsCollapseActive || ai.IsFinisherActive;

        // ---------------------------------------------------------------- states

        void SoloCheck()
        {
            _dist0 = AI.PursuitDist;
            _speed0 = AI.PlayerSpeed;
            if (AI.PursuitState == SpeechAI.kInactive || AI.Focus != SpeechAI.kStrategyFlow) { ChangeStateTo(kTerminal); return; }
            var leader = AI.Leader;
            if (leader == null || Mgr.Busy) return;
            AI.ActiveCops(_active);
            if (_active.Count == 1 || AI.NumCopsWithLOS == 0) { _backupType = 32; ChangeStateTo(kReqBackup); return; }
            _formationType = FormationType();
            if (AI.NumCopsWithLOS <= 1 || AI.CopsInFormation.Count <= 1)
            {
                if (leader.IsHeli && AI.Heli != null && AI.Heli.Active) AI.Heli.SelfStrategy(Csis.HeliOffsetFollow);
                else if (leader.Active) leader.SelfStrategy(_formationType);
                _flags |= SOLO;
            }
            else if (leader.Active)
            {
                leader.InitiateStrategy(_formationType);
                _flags &= ~SOLO;
            }
            _dist0 = AI.PursuitDist;
            ChangeStateTo(kWaiting);
        }

        void CallToPos()
        {
            if (AI.PursuitState == SpeechAI.kInactive || AI.Focus != SpeechAI.kStrategyFlow) { ChangeStateTo(kTerminal); return; }
            var leader = AI.Leader;
            if (leader == null) return;
            if (leader.IsHeli) { ChangeStateTo(kWaiting); return; }
            if (!leader.Active) return;
            foreach (var c in AI.Actors)
            {
                if (!c.InFormation || !c.Active || c.InPosition) continue;
                if (Mgr.HasBeenSaid("RollingStrategy_CallToPosition")) leader.CallToPositionReminder();
                else leader.CallToPosition(c);
                int last = Mgr.LastEventId;
                if ((last == Mgr.IdOf("RollingStrategy_CallToPosition") || last == Mgr.IdOf("RollingStrategy_CallToPositionRem")) && !Mgr.IsQueued("Acknowledge"))
                    c.Ack();
            }
            ChangeStateTo(kWaiting);
        }

        void ReqBackup()
        {
            if (AI.PursuitState == SpeechAI.kInactive || AI.Focus != SpeechAI.kStrategyFlow) { ChangeStateTo(kTerminal); return; }
            if (!AI.InPursuit) return;
            if (!Mgr.Busy && Busy == 0)
            {
                AI.ActiveCops(_active);
                if (_active.Count == 0) return;
                var caller = _active[0];   // std::sort of the pointer list: an arbitrary unit [?]
                float eta = AI.BackupEta;
                bool etaValid = eta > 10f && eta < 200f;
                bool denied = (_flags & BUDENIED) != 0;
                if (Mgr.Count("Setup_InitialCallForBU") == 0 && !AI.IsHighIntensity)
                {
                    caller.InitialCallForBackup();
                    if (etaValid && AI.Rand01() > 0.5f && !denied) AI.Dispatch.BackupETA();
                    else
                    {
                        AI.Dispatch.BackupReply(caller, !denied, _backupType);
                        if (denied) caller.NegativeBackupReply();
                    }
                }
                else if (Mgr.Count("Backup_CallForBU") == 0 || AI.NumActiveCopCars > 1)
                {
                    caller.CallForBackup(_backupType);
                    if (etaValid && AI.Rand01() > 0.5f && !denied) AI.Dispatch.BackupETA();
                    else
                    {
                        AI.Dispatch.BackupReply(caller, !denied, _backupType);
                        if (denied) caller.NegativeBackupReply();
                    }
                }
                else if (Mgr.Count("Backup_BUReminder") == 0 && !AI.IsHighIntensity)
                {
                    if (AI.Now - _tRequested >= Tune.BURemindTime && _lastBackupType == _backupType) caller.BackupReminder(_backupType);
                    else caller.CallForBackup(_backupType);
                    if (etaValid && AI.Rand01() > 0.5f && !denied) AI.Dispatch.BackupETA();
                    else AI.Dispatch.BackupUpdate(!denied);
                }
                else
                {
                    caller.CallForSwarming();
                    if (!denied)
                    {
                        AI.Dispatch.BackupETA();
                        if (AI.Heli != null && AI.Rand01() > 0.5f) AI.Heli.Swarming();
                        else AI.SwarmingReply();
                        AI.SwarmingReplyFollow();
                    }
                    else AI.Dispatch.BackupUpdate(false);
                }
                Busy++;
                return;
            }
            if (Busy != 0 && !Mgr.Busy)
            {
                Busy = 0;
                ChangeStateTo(LastState);
            }
        }

        void Waiting()
        {
            if (AI.PursuitState == SpeechAI.kInactive) { ChangeStateTo(kTerminal); return; }
            var leader = AI.Leader;
            if (leader == null || Mgr.Busy) return;
            if (AI.PursuitState == SpeechAI.kSearching) { ChangeStateTo(kOutrun); return; }
            AI.ActiveCops(_active);
            if (_active.Count == 1) _flags |= SOLO;
            else if (_active.Count == 0) { ChangeStateTo(kTerminal); return; }
            int ft = FormationType();
            if (_formationType != ft && ft != FOLLOW && ft != STAGGER_FOLLOW && _active.Count > 1)
            {
                leader.StrategyReset(true);
                ChangeStateTo(kSoloCheck);
                return;
            }
            if (FinisherOrCollapse(AI))
            {
                switch (ft)
                {
                    case PIT:
                        if (_active.Count < 2) break;
                        if (AI.Rand01() > 0.5f) AI.FindClosestCop(false, false)?.IntentToRam();
                        else leader.StrategyExecute();
                        ChangeStateTo(kOutcome);
                        break;
                    case HELI_PURSUIT:
                        AI.Heli?.SelfStrategy(Csis.HeliOffsetFollow);
                        break;
                    case BOX_IN:
                    case ROLLING_BLOCK:
                        if (_active.Count < 2) break;
                        if (AI.Rand01() > 0.5f) leader.StrategyExecute();
                        else CallToPos();
                        ChangeStateTo(kOutcome);
                        break;
                    default:
                        AI.FindClosestCop(false, false)?.Bullhorn();
                        break;
                }
                return;
            }
            if (AI.PursuitState == SpeechAI.kActive)
            {
                bool contact = ft == PIT || ft == BOX_IN || ft == ROLLING_BLOCK || ft == HERD;
                if (contact && AI.CopsInFormation.Count == 0 && _active.Count > 1) { ChangeStateTo(kCallToPos); return; }
                foreach (var c in AI.Actors) if (c.InFormation) c.Bullhorn();
                if (AI.PursuitDist > Tune.SuspectOutrunRange) { ChangeStateTo(kOutrun); return; }
            }
            if (AI.CopsInFormation.Count < _formationCount || AI.EvadeLevel > 0.25f || _active.Count == 1)
            {
                _formationCount = AI.CopsInFormation.Count;
                _backupType = 32;
                ChangeStateTo(kReqBackup);
                return;
            }
            _formationCount = AI.CopsInFormation.Count;
        }

        void Outrun()
        {
            if (AI.PursuitState != SpeechAI.kSearching && AI.PursuitState != SpeechAI.kActive) { ChangeStateTo(kTerminal); return; }
            if (AI.Leader == null || Mgr.Busy) return;
            AI.ActiveCops(_active, true);
            if (_active.Count > 1) { ChangeStateTo(kWaiting); return; }
            if (_active.Count == 1 && !AI.AreCopsAhead && AI.PursuitDist >= Tune.SuspectOutrunRange)
            {
                _active[0].SuspectOutrun();
                ChangeStateTo(kWaiting);
            }
            else if (_active.Count == 0 && AI.PerpLostTime >= Tune.NoLOSCommentaryTime)
            {
                AI.ActiveCops(_active);
                if (_active.Count > 0)
                {
                    var closest = _active[0];   // std::sort of the pointer list [?]
                    if (closest.IsHeli) AI.Heli?.LostVisual();
                    else
                    {
                        closest.LostVisual();
                        var h = AI.Heli;
                        if (h != null && h.HasLOS && h.Active) { h.Spotter(); h.LocationReport(); }
                    }
                }
                ChangeStateTo(kLost);
            }
            else ChangeStateTo(kWaiting);
        }

        void Terminal()
        {
            Busy = 0;
            ChangeStateTo(kTransition);
            _dist0 = _dist1 = _speed0 = _speed1 = 0;
            _formationCount = _backupType = _formationType = 0;
            _flags = 0;
            _tRequested = 0;
        }

        void Outcome()
        {
            if (!AI.InPursuit || AI.Leader == null || Mgr.Busy) return;
            _dist1 = AI.PursuitDist;
            _speed1 = AI.PlayerSpeed;
            float delta = _dist1 - _dist0, deltav = _speed1 - _speed0;
            if (delta > 0f || deltav > 0f)
            {
                if (AI.PursuitDist > 50f || AI.NumCopsWithLOS == 0 || !AI.AreCopsAhead)
                {
                    var spokes = AI.Leader.IsHeli ? AI.FindClosestCop(false, false) : AI.Leader;
                    spokes?.AnticipateFail();
                }
                else AI.Leader.StrategyReset(_formationType != FormationType());
            }
            else if ((_flags & SOLO) == 0) AI.Leader.AnticipateSuccess();
            ChangeStateTo(kWaiting);
        }

        /// <summary>MReqBackup "Request" (support units requested: 16 heavy SUVs, 64 Cross; StrategyFlow.cpp:574).</summary>
        public void MessageReqBackup(int type)
        {
            _backupType = type;
            _flags &= ~BUDENIED;
            if (AI.PursuitState == SpeechAI.kActive && AI.Focus == SpeechAI.kStrategyFlow) ChangeStateTo(kReqBackup);
        }

        /// <summary>MReqBackup "ReqDenied" (StrategyFlow.cpp:583): the support units could not be placed.</summary>
        public void MessageBackupDenied(int type)
        {
            _backupType = type;
            _flags |= BUDENIED;
            if (AI.PursuitState == SpeechAI.kActive && AI.Focus == SpeechAI.kStrategyFlow) ChangeStateTo(kReqBackup);
        }

        public void MessageEventComplete(ScheduledSpeech e)
        {
            if (e.Name == "Backup_CallForBU" || e.Name == "Backup_BUReminder")
            {
                _lastBackupType = e.Arg("backup_type");
                _tRequested = AI.Now;
            }
        }
    }

    /// <summary>
    /// Speech::RoadblockFlow (Speech/RoadblockFlow.cpp): asks for roadblocks once a strategy was announced (and is
    /// told no while the heat has none), announces a new roadblock (setup / position of the spike belt / warning),
    /// its approach when its units see the player, and the outcome RBOutcomeTimer after it was hit or dodged.
    /// </summary>
    public sealed class RoadblockFlow : SpeechFlowBase
    {
        public const int SETUP = 1, LOS = 2, HELIJOINED = 4, AVERTED = 8, NAILED_SPIKES = 0x10, NAILED_OTHEROBJ = 0x20, NAILED_VEHICLE = 0x40,
            ENGAGED = 0x80, POSITIONED = 0x100, REQUESTED = 0x200, RB_ENABLED = 0x400, HELIRB_ENABLED = 0x800, SPIKES_ENABLED = 0x1000,
            CALLED_4_SPIKES = 0x2000, CALLED_4_NORMAL = 0x4000, CALLED_4_HELI = 0x8000, CALL_DENIED = 0x10000, RESET_PENDING = 0x20000,
            OUTCOMETIMERSET = 0x40000, REQ_SERVICE = 0x10000000;

        int _flags, _spikeOffset;
        double _tSetup, _tEngaged, _tAverted, _tReset;
        CopRoadblock _pertinent, _dodgedSeen;
        public int NumBlocks { get; private set; }

        public RoadblockFlow(SpeechAI ai) : base(ai) { State = 0; }

        public bool HasNailedSpikes => (_flags & NAILED_SPIKES) != 0;

        public void NailedSomethingInRB(int what)
        {
            if ((_flags & OUTCOMETIMERSET) == 0 && (_flags & (AVERTED | ENGAGED)) == 0)
            {
                _tEngaged = AI.Now;
                _flags |= OUTCOMETIMERSET;
            }
            _flags |= ENGAGED | what;
        }

        /// <summary>MReqRoadBlock "Dodged" (AICopManager::UpdateRoadBlocks; here the roadblock's Dodged flag).</summary>
        public void MessageRoadBlockDodged(CopRoadblock rb)
        {
            if (rb == _dodgedSeen) return;
            _dodgedSeen = rb;
            if ((_flags & OUTCOMETIMERSET) == 0 && (_flags & (AVERTED | ENGAGED)) == 0)
            {
                _tAverted = AI.Now;
                _flags |= OUTCOMETIMERSET;
            }
            _flags |= AVERTED;
        }

        /// <summary>MReqRoadBlock "Position": a spike belt was placed (offset in metres; int.MinValue = no spikes, not sent).</summary>
        public void MessagePositionUpdate(int offset)
        {
            if (offset == int.MinValue) return;
            _flags |= POSITIONED | REQ_SERVICE;
            _spikeOffset = offset;
        }

        void SyncRoadblock()
        {
            var rb = AI.Roadblock;
            if ((_flags & RESET_PENDING) != 0)
            {
                if (_pertinent != rb && AI.Now - _tReset >= Tune.RBPostOutcomeResetTime)
                {
                    _pertinent = rb;
                    _flags &= ~RESET_PENDING;
                    if ((_flags & SETUP) != 0) _flags |= REQ_SERVICE;
                    _flags &= ~(SETUP | LOS | HELIJOINED | AVERTED | NAILED_SPIKES | NAILED_OTHEROBJ | NAILED_VEHICLE | ENGAGED | POSITIONED);
                    NumBlocks++;
                }
            }
            if (rb != null && (_flags & RESET_PENDING) == 0)
            {
                if ((_flags & SETUP) == 0)
                {
                    _flags |= SETUP | REQ_SERVICE;
                    _tSetup = AI.Now;
                }
                bool visual = false;
                foreach (var car in rb.Cars)
                    if (car && (car.TimeSinceSeen <= 0f || SpeechAI.InCameraView(car.transform.position))) { visual = true; break; }
                if (visual && (_flags & LOS) == 0) _flags |= LOS | REQ_SERVICE;
                else _flags &= ~LOS;
            }
            bool rbe = AI.RoadblocksEnabled, hrb = AI.HeliRoadblocksEnabled, sp = AI.SpikesEnabled;
            if (((_flags & RB_ENABLED) == 0 && rbe) || ((_flags & HELIRB_ENABLED) == 0 && hrb) || ((_flags & SPIKES_ENABLED) == 0 && sp) ||
                ((_flags & RB_ENABLED) != 0 && !rbe) || ((_flags & HELIRB_ENABLED) != 0 && !hrb) || ((_flags & SPIKES_ENABLED) != 0 && !sp))
                Request();
        }

        public void Update()
        {
            SyncRoadblock();
            Service();
        }

        void Request()
        {
            var leader = AI.Leader;
            if (!AI.RoadblocksEnabled && (_flags & (REQUESTED | RB_ENABLED)) == 0)
            {
                if (Mgr.Count("StaticRoadblock_CallForRB") == 0 && !Mgr.IsQueued("StaticRoadblock_CallForRB") && !Mgr.IsPlaying("StaticRoadblock_CallForRB") && leader != null)
                {
                    leader.CallForRB();
                    AI.Dispatch.RBReply(leader, false, 0);
                    leader.NegRBReply();
                }
                return;
            }
            if (((AI.RoadblocksEnabled && (_flags & RB_ENABLED) == 0) ||
                 (AI.SpikesEnabled && (_flags & (RB_ENABLED | SPIKES_ENABLED | CALLED_4_SPIKES)) == RB_ENABLED)) && leader != null)
            {
                if (Mgr.Count("StaticRoadblock_CallForRB") == 0) leader.CallForRB(); else leader.RBReminder();
                AI.Dispatch.RBReply(leader, true, 0);
                _flags |= RB_ENABLED;
                if (AI.SpikesEnabled) _flags |= SPIKES_ENABLED;
            }
            if (AI.RoadblocksEnabled && (_flags & RB_ENABLED) != 0 && AI.HeliRoadblocksEnabled && (_flags & HELIRB_ENABLED) == 0 &&
                (_flags & (CALLED_4_SPIKES | CALLED_4_NORMAL)) != 0 && (_flags & REQUESTED) != 0 && leader != null)
            {
                leader.CallForBackup(8);
                AI.Dispatch.RBReply(leader, false, Csis.RbHeli);
                _flags |= HELIRB_ENABLED;
            }
        }

        void Setup()
        {
            if ((_flags & SETUP) == 0)
            {
                var primary = AI.GetRandomCop(1);
                var secondary = AI.GetRandomCop(2);
                if (NumBlocks > 1 && secondary != null)
                {
                    if (AI.Rand01() > 0.5f) secondary.CallForSubRB(); else AI.Dispatch.SubRBReply();
                }
                else
                {
                    AI.Dispatch.RBUpdate(false);
                    if (primary != null && !primary.IsHeli) primary.NegRBReply();
                }
                _flags &= ~REQ_SERVICE;
                return;
            }
            if ((_flags & POSITIONED) != 0)
            {
                bool posComment = AI.Roadblock == null || AI.Roadblock.SpikeStrips <= 1;
                if (posComment) AI.RBPosition(_spikeOffset);
                else
                {
                    var cop = AI.GetCopInRB() ?? AI.GetRandomCop(2);
                    if (cop != null) cop.RBApproach(); else AI.RBWarning();
                }
                _flags &= ~REQ_SERVICE;
                return;
            }
            bool interruptDispatch = Mgr.IsPlaying("StaticRoadblock_DispRBReply") || Mgr.IsPlaying("StaticRoadblock_DispRBUpdate");
            if (AI.Rand01() > 0.5f && !interruptDispatch)
            {
                var primary = AI.GetRandomActiveCop(1, false);
                if (primary == null) return;
                AI.Dispatch.RBUpdate(true);
                primary.PursuitApproaching();
            }
            else AI.RBWarning();
            _flags &= ~REQ_SERVICE;
        }

        void Approach()
        {
            var cop = AI.GetRandomCop(2);
            if (cop == null) return;
            cop.RBApproach();
            _flags &= ~REQ_SERVICE;
        }

        void Effect()
        {
            if ((_flags & OUTCOMETIMERSET) == 0) return;
            float tEngaged = (float)(AI.Now - _tEngaged), tAverted = (float)(AI.Now - _tAverted), wait = Tune.RBOutcomeTimer;
            int ae = _flags & (AVERTED | ENGAGED);
            if (ae == (AVERTED | ENGAGED) && tAverted >= wait && tEngaged >= wait) _flags &= ~OUTCOMETIMERSET;
            if (ae == AVERTED && tAverted >= wait) _flags &= ~OUTCOMETIMERSET;
            if (ae == ENGAGED && tEngaged >= wait && (_flags & LOS) != 0) AI.GetRandomActiveCop(0, true)?.Arrest();
            if ((_flags & OUTCOMETIMERSET) != 0) return;
            var rb = AI.Roadblock;
            if (rb != null)
            {
                var secondary = AI.GetRandomCop(2);
                var randcop = AI.GetRandomCop(0);
                if (rb.CopsDamaged > 0 || rb.CopsDestroyed > 0 || (_flags & ENGAGED) != 0)
                {
                    if (secondary != null && AI.Rand01() > 0.5f) secondary.RBEngage((_flags & NAILED_SPIKES) != 0);
                    else AI.RBEngaged((_flags & NAILED_SPIKES) != 0);
                }
                else
                {
                    bool disp = false;
                    if (rb.SpikeStrips > 0) AI.RBAverted();
                    else if (AI.Rand01() > 0.5f) { disp = true; AI.Dispatch.SubRBReply(); }
                    else if (secondary != null && AI.Rand01() > 0.5f) secondary.RBAverted();
                    else AI.RBAverted();
                    if (!disp && randcop != null && randcop.IsPrimary) randcop.CallForSubRB();
                    else AI.Dispatch.SubRBReply();
                }
                _flags &= ~REQ_SERVICE;
            }
            if ((_flags & (AVERTED | ENGAGED)) != 0 && (_flags & REQ_SERVICE) == 0)
            {
                _flags |= RESET_PENDING;
                _tReset = AI.Now;
            }
        }

        void Service()
        {
            if (AI.Focus != SpeechAI.kStrategyFlow && AI.Focus != SpeechAI.kLost) { ChangeStateTo(999); return; }
            if ((_flags & REQUESTED) == 0 && (Mgr.Count("RollingStrategy_InitStrategy") > 0 || Mgr.Count("Setup_SelfStrategy") > 0)) Request();
            if ((_flags & REQ_SERVICE) != 0 && (_flags & (AVERTED | ENGAGED)) == 0)
            {
                if ((_flags & LOS) != 0) Approach();
                else if ((_flags & SETUP) != 0) Setup();
            }
            if ((_flags & OUTCOMETIMERSET) != 0) Effect();
        }

        public override void Reset()
        {
            Busy = 0;
            _flags = 0;
            _spikeOffset = 0;
            _pertinent = null;
            _dodgedSeen = null;
            base.Reset();
        }
    }

    /// <summary>
    /// Speech::Observer (Speech/Observer.cpp), 4 Hz while the pursuit is active: queued collision observations become
    /// commentary from their unit; it also assesses the arrest (bullhorn arrest when the busted meter passes 80 %),
    /// regained sight, rollovers, U-turns, outcomes of rams and strategies, hard braking, being outrun, plus blow-bys
    /// past a unit and airborne jumps.
    /// </summary>
    public sealed class SpeechObserver : SpeechFlowBase
    {
        const int UTurn = 1, Braking = 2, Outcome = 4, CarRam = 8, UTurnResult = 16, Lost = 32, Arrest = 64;

        struct Obs { public SpeechObservation Type; public double Time; public float Force; public int Speaker; }

        readonly List<Obs> _obs = new List<Obs>();
        readonly List<SpeechCop> _list = new List<SpeechCop>();
        public SpeechObservation LastEvent { get; private set; }
        int _tracking = 0, _offroadHistory, _datapts;
        double _tUnstable, _tFlipped, _tTrackingOutcome, _tBrakeStart;
        float _dotTrack, _speed0;
        Vector3 _fw0, _startPos, _fwPlayer, _fwRoad;
        SpeechCop _ramCop;
        /// <summary>The function-static spkrID911 of Observer::MessageEventComplete (never reset).</summary>
        static int _spkrID911;

        public SpeechObserver(SpeechAI ai) : base(ai) { }

        public SpeechCop RamCop => _ramCop;

        public void OnCopRemoved(SpeechCop cop)
        {
            if (_ramCop == cop) _ramCop = null;
        }

        /// <summary>Observer::Reset (Observer.cpp:81-102): the timers, tracking and vectors; mObservations is not cleared.</summary>
        public override void Reset()
        {
            base.Reset();
            _tUnstable = _tFlipped = _tTrackingOutcome = 0;
            _tGasStationExpl = -1000.0;   // mT_gasstationexpl = Timer(0)
            _gasStationPos = Vector3.zero;
            _dotTrack = 0;
            _offroadHistory = 0;
            _tracking = 0;
            _ramCop = null;
            _fwPlayer = _fwRoad = Vector3.zero;
        }

        public void Update()
        {
            if (State != -1 && State == 0)
            {
                if (AI.PursuitState == SpeechAI.kActive) Process();
                return;
            }
            ChangeStateTo(0);
        }

        /// <summary>Observer::Observe: one record per observation type (newest speaker, strongest force).</summary>
        public void Observe(SpeechObservation type, int speaker, float force)
        {
            if (!AI.InPursuit || AI.Focus == SpeechAI.kPursuitFlow || AI.Focus == SpeechAI.kTerminal) return;
            bool found = false;
            for (int i = 0; i < _obs.Count; i++)
            {
                if (_obs[i].Type != type) continue;
                var o = _obs[i];
                o.Time = AI.Now;
                o.Speaker = speaker;
                o.Force = Mathf.Max(o.Force, force);
                _obs[i] = o;
                found = true;
            }
            if (!found) _obs.Add(new Obs { Type = type, Time = AI.Now, Force = force, Speaker = speaker });
            LastEvent = type;
        }

        void Process()
        {
            if (!AI.InPursuit) return;
            if (AI.Focus == SpeechAI.kPursuitFlow && AI.TimeUntilBusted < 0.5f) return;
            if (AI.Focus == SpeechAI.kTerminal) return;
            foreach (var o in _obs)
            {
                var speaker = AI.GetCop(o.Speaker);
                if (speaker == null) continue;
                AI.GetRandomCop(0);   // Observer.cpp:151 passes ai->GetRandomCop(0) (unused by EAXCop::Collision; its bRandom draw stands)
                speaker.Collision(o.Type, o.Force);
            }
            _obs.Clear();
            var p = AI.Police?.Player;
            if (p) _fwPlayer = p.transform.forward;
            AssessArrest();
            if ((_tracking & Arrest) == 0)
            {
                AssessLOS();
                AssessFlippage();
                Assess180();
                AssessOutcome();
                AssessBraking();
                AssessOutrun();
                AssessOffroad();
                GasStationAftermath();
            }
        }

        double _tGasStationExpl = -1000.0;
        Vector3 _gasStationPos;

        /// <summary>Observer::MessageGamePlayMoment (Observer.cpp:806-831): a gas station explosion in the strategy
        /// focus; set off by the player, the closest unit (with sight) shouts about it; the time and place are kept.</summary>
        public void MessageGasStationExplosion(Vector3 pos, bool byPlayer)
        {
            if (AI.Focus != SpeechAI.kStrategyFlow) return;
            if (byPlayer)
            {
                var closest = AI.FindClosestCop(false, true);
                if (closest != null && closest.HasLOS) closest.Collision(SpeechObservation.Collision_Suspect_GasStation, 1f);
            }
            _tGasStationExpl = AI.Now;
            _gasStationPos = pos;
        }

        /// <summary>Observer::GasStationAftermath (:196-209): for 15 s after the blast, units within 50 m call for
        /// emergency vehicles (ev_type gas fire).</summary>
        void GasStationAftermath()
        {
            if (AI.Now - _tGasStationExpl >= 15.0) return;
            foreach (var c in AI.Actors)
                if (c.Handle != null && Vector3.Distance(_gasStationPos, c.Handle.transform.position) < 50f) c.CallforEV(Csis.EvGasFire);
        }

        int _currOffroadID = -1;

        /// <summary>Observer::AssessOffroad: entering an offroad zone (park, campus, beach ...) in sight of a unit;
        /// "subsequent" once that zone was called out before.</summary>
        void AssessOffroad()
        {
            if (AI.NumCopsWithLOS < 1 || AI.TimeInView < 2f) return;
            int id = AI.PlayerOffroadID;
            if (_currOffroadID == id) return;
            var cop = AI.FindClosestCop(true, true);
            if (cop == null) return;
            _currOffroadID = id;
            if (id < 0 || id > 23) return;   // MiscSpeech::GetSPAMLocation
            int moment = 1 << id;
            cop.Offroad(moment, (_offroadHistory & moment) != 0);
        }

        /// <summary>CalcFWVec_Road_Car: the player's forward against the road direction, kept continuous like the
        /// player's drive-to nav (the tangent sign that follows the previous road forward) [?].</summary>
        float CalcFWVecRoadCar()
        {
            var p = AI.Police?.Player;
            if (!p) return -666.6f;
            AI.RoadAt(p.transform.position, out var tangent);
            tangent.y = 0f;
            if (tangent.sqrMagnitude < 1e-6f) return -666.6f;
            tangent.Normalize();
            var fwCar = p.transform.forward;
            if (_fwRoad.sqrMagnitude < 0.5f) _fwRoad = Vector3.Dot(tangent, fwCar) >= 0f ? tangent : -tangent;
            else if (Vector3.Dot(tangent, _fwRoad) < 0f) tangent = -tangent;
            _fwRoad = tangent;
            _fwPlayer = fwCar;
            return Vector3.Dot(fwCar, tangent);
        }

        void AssessArrest()
        {
            if (!AI.InPursuit) return;
            float tBusted = AI.TimeUntilBusted;
            if (tBusted > 0.8f && !Mgr.IsQueued("HeliSpecific_HeliBullhornArrest") && !Mgr.IsPlaying("HeliSpecific_HeliBullhornArrest") &&
                !Mgr.IsQueued("Arrest_BullhornArrest") && !Mgr.IsPlaying("Arrest_BullhornArrest"))
            {
                _tracking |= Arrest;
                var cop = AI.GetRandomActiveCop(0, true);
                if (cop != null)
                {
                    Mgr.ReleaseResource();
                    Mgr.ClearPlayback();
                    var h = AI.Heli;
                    if (h != null && h.Distance < 100f && h.Active) h.BullhornArrest();
                    else cop.BullhornArrest();
                }
            }
            else _tracking &= ~Arrest;
            // Observer.cpp:252-269: the busted meter goes to the cop speech object's DMIX input 5 (Q15, clamped) every tick
            var mix = SndMixer.Instance;
            if (mix && mix.Speech != null) mix.Speech.Set(5, Mathf.Clamp(Mathf.RoundToInt(tBusted * 32767f), 0, 32767));
        }

        void AssessLOS()
        {
            if ((AI.Focus == SpeechAI.kPursuitFlow || AI.Focus == SpeechAI.kLost) && (_tracking & Lost) != 0) _tracking &= ~Lost;
            if (AI.TimeInView > 0f && (_tracking & Lost) != 0)
            {
                AI.ActiveCops(_list, true);
                if (_list.Count > 0)
                {
                    _list[0].RegainVisual();
                    _tracking &= ~Lost;
                }
            }
        }

        /// <summary>Observer::NotifyAirborne.</summary>
        public void NotifyAirborne(float alt, float t)
        {
            if (AI.NumCopsWithLOS > 0 && alt >= Tune.MinHeightAirborne && t >= Tune.HangTimeForCommentary)
                AI.FindClosestCop(true, true)?.SuspectAirborne(AI.PlayerSpeed >= Tune.SpeedThreshFlyFlipIntensity ? Csis.High : Csis.Normal);
        }

        void AssessFlippage()
        {
            if (AI.NumCopsWithLOS <= 0) return;
            var p = AI.Police?.Player;
            bool flipped = false;
            if (p)
            {
                flipped = p.transform.up.y < 0f;
                if (!flipped) _tFlipped = AI.Now;
            }
            if (flipped && AI.Now - _tFlipped >= Tune.FlipTimeForCommentary)
                AI.FindClosestCop(true, true)?.SuspectRollover(AI.PlayerSpeed > Tune.SpeedThreshFlyFlipIntensity ? Csis.High : Csis.Normal);
        }

        void Assess180()
        {
            if (AI.NumCopsWithLOS <= 0)
            {
                _tracking &= ~(UTurn | UTurnResult);
                _tUnstable = AI.Now;
                return;
            }
            float fwDot = CalcFWVecRoadCar();
            if (fwDot >= 0.7f && (_tracking & UTurnResult) == 0) _tUnstable = AI.Now;
            if (fwDot < 0.7f && (_tracking & UTurn) == 0 && (_tracking & UTurnResult) == 0)
            {
                _dotTrack = fwDot;
                _fw0 = _fwRoad;
                _tracking |= UTurn;
                _datapts = 0;
                _startPos = AI.PlayerPos;
            }
            if ((_tracking & UTurn) == 0) return;
            if (fwDot > -0.7f && (_tracking & UTurnResult) == 0)
            {
                if (fwDot > _dotTrack)
                {
                    _tracking &= ~UTurn;
                    _tUnstable = AI.Now;
                    _datapts = 0;
                    _startPos = Vector3.zero;
                }
                else
                {
                    _dotTrack = fwDot;
                    _datapts = (sbyte)(_datapts + 1);   // Observer.cpp:352: a static char, wraps after 127 frames
                }
            }
            if (fwDot <= -0.7f && _datapts >= Tune.MinContigFramesFor180 && (_tracking & UTurnResult) == 0)
            {
                if (Vector3.Distance(_startPos, AI.PlayerPos) > Tune.MaxRangeFor180)
                {
                    _tracking &= ~UTurn;
                    _tUnstable = AI.Now;
                    _datapts = 0;
                    _startPos = Vector3.zero;
                }
                else
                {
                    _tracking |= UTurnResult;
                    _tUnstable = AI.Now;
                }
            }
            if ((_tracking & UTurnResult) != 0)
            {
                float deltaDot = Vector3.Dot(_fwPlayer, _fw0);
                if (deltaDot > -0.7f)
                {
                    _tracking &= ~(UTurn | UTurnResult);
                    _tUnstable = AI.Now;
                    _datapts = 0;
                }
                else if (AI.Now - _tUnstable > Tune.MaxTimeFor180)
                {
                    var cop = AI.FindClosestCop(true, true);
                    if (cop != null)
                    {
                        cop.SuspectUTurn();
                        _tracking &= ~(UTurn | UTurnResult);
                        _tUnstable = AI.Now;
                        _datapts = 0;
                    }
                }
            }
        }

        void AssessOutcome()
        {
            if (AI.Focus != SpeechAI.kStrategyFlow || AI.PursuitState != SpeechAI.kActive) return;
            float tBusted = AI.TimeUntilBusted;
            if ((tBusted > 0f && tBusted < 1f) || AI.IsCollapseActive)
            {
                var cop = AI.GetRandomActiveCop(1, false);
                if (cop != null && !cop.IsHeli && !Mgr.IsQueued("Outcome_AnticipateSuccess")) cop.AnticipateSuccess();
            }
            if ((_tracking & Outcome) == 0 || tBusted >= 1f) return;
            float tTracking = (float)(AI.Now - _tTrackingOutcome);
            if (tTracking < Tune.OutcomeTrackTime) return;
            _list.Clear();
            foreach (var c in AI.Actors) if (c.Active && c.HasLOS && c.IsPrimary) _list.Add(c);
            if (_list.Count == 0) return;
            bool sayOutcome = true;
            var spk = _list[0];
            if ((_tracking & CarRam) != 0)
            {
                if (_ramCop != null)
                {
                    sayOutcome = false;
                    if (_ramCop.TimeLastRammed <= tTracking) _ramCop.AnticipateSuccess(); else _ramCop.AnticipateFail();
                }
                _tracking &= ~CarRam;
                AI.ClearImmunity();
            }
            if (sayOutcome)
            {
                if (AI.PlayerSpeed >= Tune.OutcomeFailSpeed) spk.OutcomeFail();
                else if (!spk.IsHeli) spk.AnticipateSuccess();
            }
            _tracking &= ~Outcome;
        }

        void AssessBraking()
        {
            if (AI.NumCopsWithLOS <= 0) return;
            var p = AI.Police?.Player;
            if (!p) return;
            float steer = CalcFWVecRoadCar();
            float brake = Mathf.Clamp01(p.Input.Brake), ebrake = p.Input.Handbrake ? 1f : 0f;
            float tLastCrashed = Mathf.Min(AI.TimeLastCrashed, AI.TimeLastNailedCop);
            if ((_tracking & Braking) == 0)
            {
                if (steer >= 0.7f && (brake >= 1f || ebrake >= 1f) && AI.PlayerSpeed > 60f && tLastCrashed > 5f)
                {
                    _tracking |= Braking;
                    _speed0 = AI.PlayerSpeed;
                    _tBrakeStart = AI.Now;
                }
                if ((_tracking & Braking) == 0) return;
            }
            if (steer < 0.7f || (brake < 0.5f && ebrake < 0.5f) || tLastCrashed < 5f) _tracking &= ~Braking;
            else if (_speed0 > 0f && AI.PlayerSpeed / _speed0 < 1f - Tune.CrashSlowdownPct)
            {
                var cop = AI.FindClosestCop(true, true);
                if (cop != null) { cop.SuspectBrake(); _tracking &= ~Braking; }
            }
        }

        void AssessOutrun()
        {
            if (AI.NumCopsWithLOS <= 0 || AI.TimeInView < 2f) return;
            AI.ActiveCops(_list, true);
            if (_list.Count == 0) return;
            int outrun = 0;
            foreach (var c in _list) if (c.TimeLastClosing > 8f) outrun++;
            if (outrun == _list.Count && !AI.AreCopsAhead) AI.GetRandomActiveCop(0, false)?.SuspectOutrun();
        }

        /// <summary>Observer::MessageBlewPastCop: a unit the player blows past at 50 mph more calls it out (at most one
        /// other unit has sight, strategy flow, not a roadblock car).</summary>
        public void MessageBlewPastCop(SpeechCop cop, CopCar car)
        {
            if (AI.Focus != SpeechAI.kStrategyFlow || (AI.PursuitState != SpeechAI.kActive && AI.PursuitState != SpeechAI.kSearching) || AI.NumCopsWithLOS > 1) return;
            float copSpeed = car.Speed * 2.23699f;
            if (AI.PlayerSpeed <= copSpeed + 50f) return;
            if (AI.Roadblock != null && AI.Roadblock.Cars.Contains(car)) return;
            cop.Spotted();
        }

        /// <summary>Observer::MessageEventComplete: outcome tracking after collisions, flips, arrests, rams and strategy
        /// executions; the lost flag after lost-visual lines; offroad history.</summary>
        public void MessageEventComplete(ScheduledSpeech e)
        {
            bool outcomeReq = false;
            switch (e.Name)
            {
                case "AnytimeEvents_OffroadMoment":
                    _offroadHistory |= e.Arg("offroad_moment_id");
                    break;
                case "AnytimeEvents_CollisionWorld":
                case "AnytimeEvents_CollWorld_Flip":
                case "Arrest_BullhornArrest":
                case "Arrest_Arrest":
                case "RollingStrategy_StrategyExecute":
                    outcomeReq = true;
                    break;
                case "StaticRoadblock_RBEngage":
                case "ExtraCops_ExtraRBEngage":
                case "HeliSpecific_HeliBullhornArrest":
                    outcomeReq = !AI.RB.HasNailedSpikes;
                    break;
                case "AnytimeEvents_IntentToRam":
                    if (e.Arg("speaker_id") == Speaker.Heli) _tracking &= ~CarRam;
                    else
                    {
                        _tracking |= CarRam;
                        _ramCop = AI.GetCop(e.Arg("speaker_id"));
                        if (_ramCop != null) AI.MakeCopsImmune();
                    }
                    outcomeReq = true;
                    break;
                case "AnytimeEvents_LostVisual":
                case "AnytimeEvents_LostSuspect":
                case "HeliSpecific_HeliLostVisual":
                    _tracking |= Lost;
                    break;
                case "Setup_ReInitPursuit":
                    _tracking &= ~Lost;
                    if (Mgr.IsQueued("AnytimeEvents_RegainVisual")) Mgr.RecallSpeechEvent(Mgr.IdOf("AnytimeEvents_RegainVisual"));
                    break;
                case "AnytimeEvents_Disp911Report":
                    // a unit asks for more details, and the 911 state goes on (cops come out, patrols react)
                    _spkrID911 = AI.MoreDetails(0);
                    AI.Force911State();
                    break;
                case "Setup_MoreDetails":
                    if (AI.PursuitState != SpeechAI.kActive)
                    {
                        if (AI.PlayerCarColor != 0 && AI.IsVehicleTypeOK && AI.Rand01() > 0.5f) AI.Dispatch.VehicleDescription();
                        else AI.Dispatch.NoVehicleDescription();
                        if (_spkrID911 > 0) AI.Unit911Reply(_spkrID911);
                    }
                    break;
            }
            if (outcomeReq)
            {
                _tracking |= Outcome;
                _tTrackingOutcome = AI.Now;
            }
        }
    }
}
