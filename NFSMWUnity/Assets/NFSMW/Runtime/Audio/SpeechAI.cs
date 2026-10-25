using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>Speech::SpeechObservations (Speech/Observer.h).</summary>
    public enum SpeechObservation
    {
        None = 0, Collision_Cop_Cop, Collision_Cop_Traffic, Collision_Cop_Suspect, Collision_Cop_World, Collision_Suspect_World,
        Collision_Suspect_Suspect, Collision_Suspect_Traffic, Collision_Suspect_Structure, Collision_Suspect_Tree, Collision_Suspect_Guardrail,
        Collision_Suspect_Train, Collision_Suspect_Semi, Collision_Suspect_GasStation, Collision_Suspect_Spikebelt,
        Behavior_Suspect_Spunout, Behavior_Suspect_Airborne, Behavior_Suspect_Rollover,
    }

    /// <summary>
    /// The police radio's brain: SoundAI (Speech/SoundAI.cpp) with its flows (<see cref="PursuitFlow"/>,
    /// <see cref="StrategyFlow"/>, <see cref="RoadblockFlow"/>, <see cref="SpeechObserver"/>) and voices
    /// (<see cref="SpeechCop"/>, <see cref="SpeechHeli"/>, <see cref="SpeechDispatch"/>, MiscSpeech below). It reads the
    /// port's police state instead of the sim: <see cref="PursuitSystem"/> (pursuit, heat, line of sight, cooldown,
    /// busted meter, stats), the <see cref="CopCar"/>s, the <see cref="CopHeli"/> and the roadblocks, and schedules Csis
    /// events on a <see cref="CopSpeechManager"/>.
    /// <para>Tasks like the game: the main update (SyncPursuit, SyncCarsToActors, SyncPlayers, SyncFormations,
    /// ShuffleActors, state machines, DealWithDeadAir) every 0.1 s, the observations (Observer, RoadblockFlow,
    /// Manager::Deduce) every 0.25 s; collisions arrive from <see cref="SpeechCollisionTap"/> components.</para>
    /// <para>911 calls: outside a pursuit in free roam, enough property damage (mCTS911 &gt;= CTSFor911) or traffic
    /// smashes (&gt;= NumCiviHitsFor911) make dispatch report a 911 call; when that line completes the AI forces the 911
    /// state (the player's 911 timer = Lifetime911, the cop lockout lifts) so patrol cars that see the player start a
    /// pursuit, and dispatch adds "more details" and a unit replies. Tunnels come from the TRACK_PATH_ZONE tunnel
    /// family (SFXCTL_Tunnel's test, Police/PoliceZones.cs). Unspawned cops arrive through PursuitSystem.CopUnspawned.</para>
    /// <para>Not ported: AI-racer pursuits (kOtherTarget), cell calls / SMS, the music flow (MusicFlow drives the
    /// Pathfinder interactive music; the port has no interactive music and MW_Music.mus is blank), weather,
    /// two-player. The offroad "SPAM" zone map is replaced by the road network's offroad road tags [?].</para>
    /// </summary>
    public sealed class SpeechAI
    {
        // MachineState (SoundAI.h)
        public const int kCullCheck = 0, kPursuitFlow = 1, kStrategyFlow = 2, kRoadblockFlow = 5, kTransition = -1, kWaiting = 333, kLost = 666, kTerminal = 999;
        // PursuitState
        public const int kActive = 0, kSearching = 1, kInactive = 2, kOtherTarget = 3;
        // QuadrantState
        const int qInitial = 0, qForming = 1, qFiction1 = 2, qFiction2 = 3, qExpired = 4, qReset = 5;
        // SoundAIFlags
        const int RB_ENABLED = 1, HELIRB_ENABLED = 2, SPIKES_ENABLED = 4, LOWSPEEDTIMER = 8, COPS_ARE_AHEAD = 0x20, HELI_INTRO_REQ = 0x40,
            BUSTED = 0x80, DISP911_ACTIVE = 0x100, SETUP_RESTARTED = 0x200, COPS_IMMUNE = 0x400, RACERS_PROXIMAL = 0x800, PURSUIT_EXPIRED = 0x1000;
        // GInfractionManager::InfractionType
        public const int InfSpeeding = 1, InfRacing = 2, InfReckless = 4, InfAssault = 8, InfHitAndRun = 16, InfDamage = 32, InfResist = 64, InfOffRoad = 128;

        const float MainRate = 0.1f, ObserverRate = 0.25f;   // SOUNDAI_MAIN_UPDATE_RATE, SOUNDAI_OBSERVER_UPDATE_RATE

        public readonly CopSpeechManager Mgr;
        public readonly SpeechTune Tune;
        public readonly PursuitFlow Pursuit;
        public readonly StrategyFlow Strategy;
        public readonly RoadblockFlow RB;
        public readonly SpeechObserver Observer;
        public readonly SpeechDispatch Dispatch;
        public readonly List<SpeechCop> Actors = new List<SpeechCop>();
        public readonly List<SpeechCop> CopsInFormation = new List<SpeechCop>();
        public SpeechCop Leader { get; private set; }
        public SpeechHeli Heli { get; private set; }
        public SpeechCop LatestCop { get; private set; }
        public PursuitSystem Police { get; private set; }

        public double Now;
        public int Focus = kPursuitFlow;
        public int PursuitState { get; private set; } = kInactive;
        int _flags, _quadrant = qReset;
        float _mainAcc, _obsAcc;
        readonly System.Random _rng = new System.Random();

        // SoundAI members
        public bool InPursuit { get; private set; }          // mPursuit != nullptr
        public int NumPursuits { get; private set; }          // mPursuitCount
        public float PursuitDuration { get; private set; } = -1f;
        public float PursuitDist { get; private set; }
        public int NumCopsWithLOS { get; private set; }
        public int NumCopsInView { get; private set; }
        public int NumActiveCopCars { get; private set; }
        public float PlayerSpeed { get; private set; }        // mph
        public Vector3 PlayerPos { get; private set; }
        Vector3 _playerFW, _smoothedFW;
        public int Heat { get; private set; } = 1;
        public float TimeSinceLastChase { get; private set; }
        public int LastInfraction { get; private set; } = -1;
        public int NumTrafficHits { get; private set; }       // mTrafficHits911
        public int Havoc { get; private set; }                // GetHavoc returns mCTS911
        double _tReallyLowSpeed, _tNoLOS, _tLOS, _tLastCrashed, _tLastCopNailed, _tPursuitStart, _tSinceLastPursuit;
        float _prevHeat = 1f;
        // heading (CarHeading mPlayerCurrent[2], mLastKnown)
        int _road0, _road1, _dir0, _dir1, _lastKnownRoad, _lastKnownDir;
        int _dirTracking;
        double _tCurrDir;
        // blow-by record
        public float BlowbyDistance = 32767f, BlowbySpeed;
        public double BlowbyTime;
        // voices and call signs (VoiceUsage)
        readonly List<int> _voices = new List<int>();
        readonly Dictionary<int, List<int>> _callsigns = new Dictionary<int, List<int>>();

        // port bookkeeping
        PlayerCar _player;
        Rigidbody _playerBody;
        PursuitStats _stats;
        int _statsInfractions, _lastBlownTires;
        CopRoadblock _roadblock;
        readonly HashSet<CopRoadblock> _roadblocksSeen = new HashSet<CopRoadblock>();
        readonly Dictionary<CopCar, double> _blewBy = new Dictionary<CopCar, double>();
        readonly List<CopCar> _newCops = new List<CopCar>();
        readonly List<SpeechCop> _tmp = new List<SpeechCop>(), _tmp2 = new List<SpeechCop>();
        CopHeli _heliCar;
        float _heliScan;
        ushort[] _roadSpeech = System.Array.Empty<ushort>();
        int _verbalType = -1;
        string _verbalCar;
        int _playerColor;
        float _airTime, _takeoffY;
        bool _airNotified;
        bool _bustedSeen;

        public SpeechAI(CopSpeechManager mgr)
        {
            Mgr = mgr;
            Tune = mgr.Tune;
            Dispatch = new SpeechDispatch(this);
            Pursuit = new PursuitFlow(this);
            Strategy = new StrategyFlow(this);
            RB = new RoadblockFlow(this);
            Observer = new SpeechObserver(this);
            for (int i = 3; i < 10; i++) _voices.Add(i);
            Shuffle(_voices);
            mgr.Heat = () => Heat;
            mgr.PlayerSpeedMph = () => PlayerSpeed;
            mgr.PursuitDuration = () => PursuitDuration;
            mgr.EventCompleted += OnEventComplete;
            try
            {
                GameData.Mount();
                _roadSpeech = CopSpeechRules.RoadSpeechIds(File.ReadAllBytes(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN")));
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW speech AI: road names unavailable: " + e.Message); }
        }

        // ============================================================================================ helpers

        public float Rand01() => (float)_rng.NextDouble();
        public int Rand(int n) => n <= 0 ? 0 : _rng.Next(n);

        void Shuffle(List<int> l)
        {
            for (int i = 0; i < l.Count; i++)
            {
                int r = Rand(l.Count);
                (l[i], l[r]) = (l[r], l[i]);
            }
        }

        public static bool InCameraView(Vector3 pos)
        {
            var cam = Camera.main;
            if (!cam) return false;
            var vp = cam.WorldToViewportPoint(pos);
            return vp.z > 0 && vp.x > -0.05f && vp.x < 1.05f && vp.y > -0.05f && vp.y < 1.05f;
        }

        /// <summary>Speech::Manager::ScheduleSpeech.</summary>
        public void Schedule(string ev, Dictionary<string, int> args, ISpeechActor actor) => Mgr.Schedule(ev, args, actor);

        /// <summary>SoundAI::IsHighIntensity (PC 0x701510): heat at HighIntensityMark or a pursuit of PursuitDurationHighIntensity s.</summary>
        public bool IsHighIntensity => Heat >= Tune.HighIntensityMark || PursuitDuration >= Tune.PursuitDurationHighIntensity;
        /// <summary>RACERS_PROXIMAL (SyncCarsToActors): the closest AI racer within speechtune AIRacerProximity of the view.</summary>
        public bool AreRacersNearby => (_flags & RACERS_PROXIMAL) != 0;
        public int NumSuspects => AreRacersNearby ? Csis.MultipleSuspects : Csis.OneSuspect;
        public bool AreCopsAhead => (_flags & COPS_ARE_AHEAD) != 0;
        public bool RoadblocksEnabled => (_flags & RB_ENABLED) != 0;
        public bool HeliRoadblocksEnabled => (_flags & HELIRB_ENABLED) != 0;
        public bool SpikesEnabled => (_flags & SPIKES_ENABLED) != 0;
        public void MakeCopsImmune() => _flags |= COPS_IMMUNE;
        public void ClearImmunity() => _flags &= ~COPS_IMMUNE;
        public CopRoadblock Roadblock => _roadblock;
        public int NumRoadBlocks => RB.NumBlocks;
        public float PlayerStopTime => (float)(Now - _tReallyLowSpeed);
        public float PerpLostTime => (float)(Now - _tNoLOS);
        public float TimeInView => (float)(Now - _tLOS);
        public float TimeLastCrashed => (float)(Now - _tLastCrashed);
        public SpeechObservation LastObservation => Observer.LastEvent;
        /// <summary>Observer::PlayerInTunnel (MessageTunnelUpdate from SFXCTL_Tunnel): the player is in a tunnel,
        /// overpass, garage or dynamic zone (PoliceZones.InTunnel).</summary>
        public bool PlayerInTunnel { get; private set; }
        /// <summary>SoundAI::Is911Active: dispatch's 911 report played (Force911State) since the last ResetPursuit.</summary>
        public bool Is911Active => (_flags & DISP911_ACTIVE) != 0;
        public int PlayerCarColor => _playerColor;
        public int PlayerVerbalType => _verbalType > 0 ? _verbalType : 0;
        public int NumCopsDestroyed => Police != null && Police.Stats != null ? Police.Stats.CopsDestroyed : 0;
        public int PlayerRoad => _road0;
        /// <summary>mPlayerOffroadID (SPAMAccessorSpeech data 1, the offroad zone map). The zone map is not read; the road
        /// network tags the same zones (RoadNames offroad_Gas_Station 88 .. offroad_Hydro_Plant 106, in the order of
        /// MiscSpeech::GetSPAMLocation ids 0..18), so a tagged road gives the zone, else -1 [?].</summary>
        public int PlayerOffroadID => _road0 >= 88 && _road0 <= 106 ? _road0 - 88 : -1;
        public int PlayerDirection => _dir0;

        /// <summary>IPursuit::TimeUntilBusted (AIPursuit.cpp:2651): the pursuit meter, blended up to 1 by the busted timer.</summary>
        public float TimeUntilBusted => Police != null ? Police.TimeUntilBusted : 0f;
        /// <summary>IPursuit::GetBackupETA: the backup countdown.</summary>
        public float BackupEta => Police != null ? Police.BackupEta : 0f;
        /// <summary>IPursuit::GetEvadeLevel.</summary>
        public float EvadeLevel => Police != null ? Police.EvadeLevel : 0f;
        /// <summary>IPursuit::IsCollapseActive.</summary>
        public bool IsCollapseActive => Police != null && Police.IsCollapseActive;
        /// <summary>IPursuit::IsFinisherActive.</summary>
        public bool IsFinisherActive => Police != null && Police.IsFinisherActive;

        /// <summary>MiscSpeech::IsVehicleTypeOK: a known make other than Pagani (traffic/cop parents are excluded in the game).</summary>
        public bool IsVehicleTypeOK => _verbalType > 0 && _verbalType != Csis.CarTypePagani;

        /// <summary>SoundAI::IsHeadingValid: a named road below Seaside_Interchange and a settled direction.</summary>
        public bool IsHeadingValid => _road0 > CopSpeechRules.RoadUntagged && _road0 < CopSpeechRules.RoadSeasideInterchange && _dir0 > 0;

        /// <summary>The location of the last known road, else the current, else the previous one (EAXDispatch).</summary>
        public void LastKnownLocation(out int region, out int location)
        {
            if (!CopSpeechRules.GetLocation(_lastKnownRoad, out region, out location))
                if (!CopSpeechRules.GetLocation(_road0, out region, out location))
                    CopSpeechRules.GetLocation(_road1, out region, out location);
        }

        public int LastKnownDirectionOrRandom()
        {
            int dir = _lastKnownDir;
            if (dir == 0) dir = _dir0;
            if (dir == 0) dir = _dir1;
            if (dir == 0) dir = 1 << Rand(4);
            return dir;
        }

        /// <summary>SoundAI::GetTimeLastNailedCop: seconds since the most recent ram of any unit (65535 if none).</summary>
        public float TimeLastNailedCop
        {
            get
            {
                float best = 65535f;
                bool any = false;
                foreach (var c in Actors)
                    if (c.NumRammed > 0) { float t = c.TimeLastRammed; if (!any || t < best) best = t; any = true; }
                return best;
            }
        }

        // ============================================================================================ actor queries

        public SpeechCop GetCop(int speaker)
        {
            foreach (var c in Actors) if (c.SpeakerID == speaker) return c;
            return null;
        }

        public SpeechCop FindClosestCop(bool enforceLOS, bool includeHeli)
        {
            SpeechCop best = null;
            foreach (var c in Actors)
            {
                if ((enforceLOS && !c.HasLOS) || (!includeHeli && c.IsHeli)) continue;
                if (best == null || c.Distance < best.Distance) best = c;
            }
            return best;
        }

        public SpeechCop FindFurthestCop(bool includeHeli)
        {
            SpeechCop best = null;
            foreach (var c in Actors)
                if ((best == null || c.Distance > best.Distance) && (includeHeli || !c.IsHeli)) best = c;
            return best;
        }

        /// <summary>SoundAI::GetRandomActiveCop: type 0 any, 1 primary, 2 secondary.</summary>
        public SpeechCop GetRandomActiveCop(int type, bool reqLOS)
        {
            _tmp.Clear();
            foreach (var c in Actors) if (c.Active && (!reqLOS || c.HasLOS)) _tmp.Add(c);
            return PickRandom(_tmp, type);
        }

        /// <summary>SoundAI::GetRandomCop: like GetRandomActiveCop over every actor.</summary>
        public SpeechCop GetRandomCop(int type)
        {
            _tmp.Clear();
            _tmp.AddRange(Actors);
            return PickRandom(_tmp, type);
        }

        SpeechCop PickRandom(List<SpeechCop> list, int type)
        {
            if (list.Count == 0) return null;
            if (type == 0 && list.Count > 1) return list[Rand(list.Count)];
            if (list.Count == 1)
            {
                var c = list[0];
                if (type == 0) return c;
                if (type == 1) return c.IsPrimary ? c : null;
                if (type == 2) return !c.IsPrimary ? c : null;
                return null;
            }
            _tmp2.Clear();
            foreach (var c in list) if ((type == 1) == c.IsPrimary) _tmp2.Add(c);
            return _tmp2.Count > 0 ? _tmp2[Rand(_tmp2.Count)] : null;
        }

        public SpeechCop GetCopInRB()
        {
            if (_roadblock == null) return null;
            foreach (var c in Actors)
                if (c.Active && !c.IsPrimary && c.Car != null && _roadblock.Cars.Contains(c.Car)) return c;
            return null;
        }

        public void ActiveCops(List<SpeechCop> into, bool reqLOS = false)
        {
            into.Clear();
            foreach (var c in Actors) if (c.Active && (!reqLOS || c.HasLOS)) into.Add(c);
        }

        // ============================================================================================ frame entry

        /// <summary>Runs the AI for one frame (the SoundAI tasks at their rates).</summary>
        public void Tick(PursuitSystem police, float dt)
        {
            if (police != Police)
            {
                if (Police != null) { Police.CopUnspawned -= MessageUnspawnCop; Police.BackupMessage -= MessageBackup; Police.GasStationExploded -= Observer.MessageGasStationExplosion; }
                if (police != null) { police.CopUnspawned += MessageUnspawnCop; police.BackupMessage += MessageBackup; police.GasStationExploded += Observer.MessageGasStationExplosion; }
            }
            Police = police;
            if (police == null) return;
            // MRestartRace / ResetCopsForRestart: a race starting or ending resets the speech AI [?: the game sends it on
            // restarts and world reloads; race entry and exit are the port's equivalent]
            bool inRace = RaceManager.Active != null;
            if (inRace != _inRace) { _inRace = inRace; MessageRestart(); }
            _player = police.Player ? police.Player : (GameBootstrap.Instance ? GameBootstrap.Instance.Player : null);
            if (_player && (!_playerBody || _playerBody.gameObject != _player.gameObject))
            {
                _playerBody = _player.GetComponent<Rigidbody>();
                SpeechCollisionTap.Attach(_player.gameObject, this, null);
            }
            if (!_player) return;

            _mainAcc += dt;
            _obsAcc += dt;
            if (_mainAcc >= MainRate)
            {
                _mainAcc = Mathf.Min(_mainAcc - MainRate, MainRate);
                MainUpdate();
            }
            if (_obsAcc >= ObserverRate)
            {
                _obsAcc = Mathf.Min(_obsAcc - ObserverRate, ObserverRate);
                if ((_flags & BUSTED) == 0)
                {
                    Observer.Update();
                    RB.Update();
                }
                Mgr.Deduce();
            }
            TrackAirborne(dt);
        }

        bool _inRace;

        /// <summary>SoundAI::MessageRestart (SoundAI.cpp:251-258): ResetPursuit(true), drop the stream, clear the queues.</summary>
        public void MessageRestart()
        {
            ResetPursuit(true);
            Mgr.ReleaseResource();
            Mgr.ClearPlayback();
        }

        /// <summary>SoundAI::OnTask (main task).</summary>
        void MainUpdate()
        {
            float deadAir = Mgr.Busy ? 0f : (float)(Now - Mgr.LastEventTime);
            WatchBusted();
            if ((_flags & BUSTED) != 0) return;
            WatchGameplay();
            SyncPursuit();
            SyncCarsToActors();
            SyncPlayers();
            SyncFormations();
            ShuffleActors();
            UpdateStateMachines();
            if (deadAir > 0f) DealWithDeadAir();
        }

        /// <summary>GRaceStatus::GetRaceParameters()->GetIsPursuitRace(): the running event's gameplay attribute PursuitRace
        /// (the Challenge Series pursuit, else the race manager's event).</summary>
        public bool IsPursuitRace
        {
            get
            {
                var ev = Police != null && Police.Challenge != null ? Police.Challenge : RaceManager.Active?.Event;
                return ev != null && ev.PursuitRace;
            }
        }

        /// <summary>SoundAI::MessagePerpBusted (SoundAI.cpp:230-236, PC @0x700F70): IPursuit::IsPerpBusted sets the terminal
        /// focus and BUSTED; only ResetPursuit clears it, which the game reaches through MRestartRace when the world reloads
        /// after the arrest. The port sends that restart itself once the pursuit is gone and no NIS plays [ours].</summary>
        void WatchBusted()
        {
            var p = Police;
            bool busted = p.IsPerpBusted;
            if (busted && (_flags & BUSTED) == 0)
            {
                Focus = kTerminal;
                _flags |= BUSTED;
                _bustedSeen = true;
            }
            if (_bustedSeen && !p.InPursuit && !NisPlayer.Active)
            {
                _bustedSeen = false;
                MessageRestart();
            }
        }

        /// <summary>Game messages the port has no hook for, polled from public state: infractions (MessageInfraction),
        /// blown tyres (MessageTireBlown), roadblock positioned / dodged, support requests (MReqBackup), blow-bys.</summary>
        void WatchGameplay()
        {
            var p = Police;
            if (p.Stats != _stats) { _stats = p.Stats; _statsInfractions = 0; }
            if (_stats != null && _stats.Infractions.Count > _statsInfractions)
            {
                for (int i = _statsInfractions; i < _stats.Infractions.Count; i++) LastInfraction = InfractionId(_stats.Infractions[i]);
                _statsInfractions = _stats.Infractions.Count;
            }
            int blown = _player.TryGetComponent<TireState>(out var tires) ? tires.BlownCount : _player.BlownTires;
            if (blown > _lastBlownTires) MessageTireBlown();
            _lastBlownTires = blown;
        }

        static int InfractionId(string name)
        {
            switch (name)
            {
                case "Speeding": return InfSpeeding;
                case "Racing": return InfRacing;
                case "Reckless Driving": return InfReckless;
                case "Ramming a Police Vehicle": return InfAssault;
                case "Hit and Run": return InfHitAndRun;
                case "Damage to Property": return InfDamage;
                case "Resisting Arrest": return InfResist;
                case "Driving Off-Roadway": return InfOffRoad;
                default: return 0;
            }
        }

        /// <summary>SoundAI::MessageTireBlown.</summary>
        void MessageTireBlown()
        {
            RB.NailedSomethingInRB(RoadblockFlow.NAILED_SPIKES);
            var spkr = FindClosestCop(true, true);
            if (spkr != null) Observer.Observe(SpeechObservation.Collision_Suspect_Spikebelt, spkr.SpeakerID, 1f);
        }

        /// <summary>SoundAI::UpdateStateMachines.</summary>
        void UpdateStateMachines()
        {
            switch (Focus)
            {
                case kPursuitFlow:
                    Pursuit.Update();
                    if (Pursuit.IsTransitionable)
                    {
                        Focus = kStrategyFlow;
                        Strategy.ChangeStateTo(0);
                    }
                    break;
                case kRoadblockFlow:
                case kStrategyFlow:
                    if (Strategy.IsTransitionable) Strategy.ChangeStateTo(kWaiting);
                    Strategy.Update();
                    break;
                case kLost:
                    if (InPursuit && PursuitState == kSearching)
                    {
                        if (Strategy.State != kOtherTarget) Strategy.ChangeStateTo(kOtherTarget);
                        Strategy.Update();
                    }
                    break;
            }
        }

        /// <summary>SoundAI::DealWithDeadAir: dispatch asks for an update after a minute; the leader (or heli) answers
        /// with a pursuit update, a location report, or "lost visual" once the suspect is out of sight.</summary>
        void DealWithDeadAir()
        {
            if (PursuitState != kActive || Leader == null || Focus == kPursuitFlow) return;
            if (!Mgr.IsQueued("AnytimeEvents_DispPursuitUpdate") && PursuitDuration > 60f) Dispatch.PursuitUpdate(Leader);
            // SoundAI.cpp:1045/1057: !IsHeadingValid() || mPlayerOffroadID < 0 || bRandom(1.0) > 0.5 → the pursuit update,
            // else the location report (PlayerOffroadID: the port's road-tag stand-in for the SPAM zone map, see its note)
            if (PerpLostTime >= Tune.NoLOSCommentaryTime)
            {
                if (Heli == null) Leader.LostVisual();
                else if (Heli.HasLOS)
                {
                    if (!IsHeadingValid || PlayerOffroadID < 0 || Rand01() > 0.5f) Heli.PursuitUpdateReply();
                    else Heli.LocationReport();
                }
                else Heli.LostVisual();
            }
            else if (!Mgr.IsQueued("AnytimeEvents_PursuitUpdateRep") && !Mgr.IsQueued("Setup_LocationReport") && PursuitDuration > 60f)
            {
                if (!IsHeadingValid || PlayerOffroadID < 0 || Rand01() > 0.5f) Leader.PursuitUpdateReply();
                else Leader.LocationReport();
            }
            if (Mgr.IsQueued("Setup_LocationReport") && Mgr.HasBeenSaid("AnytimeEvents_DispPursuitUpdate")) Leader.PursuitUpdateReply();
        }

        // ============================================================================================ SyncPursuit

        /// <summary>SoundAI::SyncPursuit: pursuit state (active / searching / inactive), reacquire, outrun bailout into
        /// the "lost" focus, the quadrant search chatter and dispatch's "time expired", pursuit duration, the heli intro.</summary>
        void SyncPursuit()
        {
            var p = Police;
            bool have = p.InPursuit;
            // AttemptReattachPursuit
            if (have && !InPursuit) { NumPursuits++; _tPursuitStart = Now; InPursuit = true; }
            else if (!have && InPursuit) { InPursuit = false; _tPursuitStart = Now; PursuitState = kInactive; }
            else if (!have) { _tPursuitStart = Now; PursuitState = kInactive; }

            if (!InPursuit)
            {
                if (PursuitState != kOtherTarget && PursuitState != kInactive) { _tSinceLastPursuit = Now; PursuitState = kInactive; }
            }
            else
            {
                // AttemptingToReAquire / !IsPerpInSight / PS_COOL_DOWN
                bool searching = !p.PerpInSight || p.InCooldown;
                bool heliLOS = Heli != null && Heli.Active && Heli.HasLOS;
                if (searching || (NumCopsWithLOS == 0 && !heliLOS)) PursuitState = kSearching;
                else
                {
                    if (PursuitState != kActive) TimeSinceLastChase = (float)(Now - _tSinceLastPursuit);
                    PursuitState = kActive;
                }
                if (PursuitState <= kSearching) { NumTrafficHits = 0; Havoc = 0; }
            }

            float tLost = PerpLostTime;
            if (PursuitState == kActive) _tNoLOS = _tSinceLastPursuit = Now;
            else _tLOS = Now;

            float cut0 = Tune.PursuitInactivityTimer0 + Tune.TimeConsideredLostNoLOS;
            float cut1 = Tune.PursuitInactivityTimer1 + Tune.TimeConsideredLostNoLOS;
            bool coolingDown = InPursuit && p.InCooldown && p.CooldownProgress < 1f;    // PS_COOL_DOWN, time remaining
            bool cooledDown = InPursuit && p.InCooldown && p.CooldownProgress >= 1f;

            if (Focus == kLost && PursuitState == kActive && TimeSinceLastChase < cut1)
            {
                Focus = kPursuitFlow;
                _quadrant = qReset;
                _flags &= ~(SETUP_RESTARTED | PURSUIT_EXPIRED);
                Mgr.ClearPlayback();
                Pursuit.Reacquire();
            }

            if (Focus == kStrategyFlow && (PursuitState == kSearching || PursuitState == kInactive) && tLost > cut0 && (!InPursuit || coolingDown))
                TerminatePursuit();
            else if (Focus == kLost)
            {
                if ((PursuitState == kSearching || PursuitState == kInactive) && tLost > cut0 && tLost < cut1 && !Mgr.Busy && (!InPursuit || coolingDown))
                {
                    switch (_quadrant)
                    {
                        case qInitial:
                            QuadrantForming();
                            _quadrant = qForming;
                            break;
                        case qForming:
                            if (Rand01() > 0.5f) { _quadrant = qFiction2; PossibleSuspect(); }
                            else
                            {
                                _quadrant = qFiction1;
                                int r = Rand(3);
                                if (r == 1) QuadrantMoving();
                                else if (r == 0) SuspectPossiblyGone();
                                else OtherLead();
                            }
                            break;
                        case qFiction2:
                            WrongSuspect();
                            _quadrant = qExpired;
                            break;
                        default:
                            _quadrant = qExpired;
                            break;
                    }
                }
                else if ((PursuitState == kSearching || PursuitState == kInactive) && (!InPursuit || cooledDown))
                {
                    if (_quadrant == qExpired && !InPursuit && (_flags & PURSUIT_EXPIRED) == 0)
                    {
                        Mgr.ClearPlayback();
                        Dispatch.TimeExpired();
                        _quadrant = qReset;
                        _flags |= PURSUIT_EXPIRED;
                    }
                    else if (tLost > cut1) ResetPursuit(false);
                }
            }

            if (Focus == kTerminal) PursuitState = kInactive;
            PursuitDuration = PursuitState < kInactive && InPursuit ? (float)(Now - _tPursuitStart) : -1f;

            if ((_flags & HELI_INTRO_REQ) != 0 && Heli != null && Focus == kStrategyFlow && PursuitState != kInactive)
            {
                Heli.BackupArrives();
                _flags &= ~HELI_INTRO_REQ;
            }
        }

        /// <summary>SoundAI::TerminatePursuit(kOutrunBail): "lost him" (when no cop is on screen) and dispatch's break-away.
        /// The scan (SoundAI.cpp:1338-1344) runs over VEHICLE_AICOPS, which holds the helicopter too (and wrecks the port
        /// no longer lists [?]).</summary>
        void TerminatePursuit()
        {
            bool copsVisible = false;
            foreach (var c in Police.CopCars) if (c && InCameraView(c.transform.position)) { copsVisible = true; break; }
            if (!copsVisible && _heliCar && InCameraView(_heliCar.transform.position)) copsVisible = true;
            Mgr.ClearPlayback();
            if (!copsVisible) LostSuspect(Mgr.LastSpeakerID);
            Dispatch.BreakAway();
            Focus = kLost;
            _quadrant = qInitial;
            _flags &= ~SETUP_RESTARTED;
        }

        /// <summary>SoundAI::ResetPursuit.</summary>
        public void ResetPursuit(bool includingMusic)
        {
            Pursuit.Reset();
            Strategy.Reset();
            RB.Reset();
            Observer.Reset();
            Mgr.ResetGlobalHistory();
            Focus = kPursuitFlow;
            _quadrant = qReset;
            while (Actors.Count > 0) RemoveCop(Actors[0]);
            Leader = null;
            Heli = null;
            CopsInFormation.Clear();
            InPursuit = false;
            LatestCop = null;
            _tPursuitStart = Now;
            PursuitState = kInactive;
            PursuitDist = 0;
            _tLastCopNailed = _tLastCrashed = _tNoLOS = _tReallyLowSpeed = _tLOS = 0;
            NumCopsInView = 0;
            LastInfraction = 0;
            _flags = 0;
            NumTrafficHits = 0;
            Havoc = 0;
            _roadblocksSeen.Clear();
        }

        // ============================================================================================ actors

        /// <summary>SoundAI::SyncCarsToActors: line of sight, "ahead", distances, blow-bys; new cars get voices; the heli.</summary>
        void SyncCarsToActors()
        {
            _newCops.Clear();
            int inView = 0, withLOS = 0, ahead = 0, active = 0;
            float pursuitDistance = 65535f;
            _roadblock = FindRoadblock();
            SyncRacers();

            foreach (var pc in Police.CopCars)
            {
                if (!pc) continue;
                var cop = pc.GetComponent<CopCar>();
                if (!cop) continue;
                SpeechCollisionTap.Attach(cop.gameObject, this, cop);
                var pos = cop.transform.position;
                bool inRB = _roadblock != null && _roadblock.Cars.Contains(cop);
                bool seen = InCameraView(pos);
                bool isActive = !cop.Destroyed;   // IVehicle::IsActive: a wreck leaves the chase here [?]
                if (seen) inView++;
                bool visual = false;
                // SoundAI.cpp:1750-1756: IsActive && IPursuitAI && (GetTimeSinceTargetSeen() < 0.05 || (in the roadblock && in view))
                if (isActive && (cop.TimeSinceSeen < 0.05f || (inRB && seen))) { visual = true; withLOS++; }
                bool isAhead = false;
                if (isActive || seen) isAhead = Vector3.Dot((pos - PlayerPos).normalized, _playerFW) > 0f;
                if (isActive)
                {
                    active++;
                    pursuitDistance = Mathf.Min(pursuitDistance, Vector3.Distance(pos, PlayerPos));
                    if (isAhead) ahead++;
                }
                var actor = FindActor(cop);
                if (actor != null)
                {
                    if (isActive) { if (!actor.Active) actor.SetActive(true); }
                    else actor.SetActive(false);
                    actor.SuspectLOS = visual;
                    actor.Ahead = isAhead;
                    actor.Update();
                    UpdateBlowby(actor, isAhead);
                    WatchBlowPast(cop, actor);
                }
                else _newCops.Add(cop);
            }
            // wrecked cars drop out of PursuitSystem.CopCars: their actors go inactive (in the game IVehicle::IsActive is
            // false for the wreck, which the loop above handles through the vehicle list) [ours: the port's list has no wrecks]
            for (int i = Actors.Count - 1; i >= 0; i--)
            {
                var a = Actors[i];
                if (a.IsHeli) continue;
                var car = a.Car;
                if (car == null) continue;
                if (car.Destroyed && a.Active)
                {
                    a.Destroyed = true;
                    a.Health = Mathf.Clamp01(1f - car.HitPoints);
                    a.SetActive(false);   // in formation a moment ago: "unit disabled" (the wreck's InFormation is not re-read first)
                    a.SuspectLOS = false;
                    a.Update();
                }
            }

            // the helicopter (rescanned twice a second)
            _heliScan -= MainRate;
            if (_heliScan <= 0f || (_heliCar == null && Heli != null))
            {
                _heliScan = 0.5f;
                _heliCar = Police.InPursuit ? Object.FindAnyObjectByType<CopHeli>() : null;
            }
            if (_heliCar)
            {
                if (Heli != null)
                {
                    if (Heli.Handle != _heliCar) Heli.Handle = _heliCar;
                    bool heliActive = !_heliCar.Exiting && !_heliCar.Finished;
                    if (heliActive && !Heli.Active) Heli.SetActive(true);
                    else if (!heliActive && Heli.Active) Heli.SetActive(false);
                    if (heliActive) Heli.SuspectLOS = _heliCar.TimeSinceSeen < 0.05f;
                    Heli.Update();
                }
                else AddNewHeli(_heliCar);
            }
            else if (Heli != null)
            {
                if (Heli.Active) Heli.SetActive(false);
                RemoveCop(Heli);
            }

            NumCopsInView = inView;
            NumCopsWithLOS = withLOS;
            PursuitDist = pursuitDistance;
            NumActiveCopCars = active;
            if (active == ahead) _flags |= COPS_ARE_AHEAD; else _flags &= ~COPS_ARE_AHEAD;

            foreach (var c in _newCops) AddNewCop(c);
            // SoundAI.cpp:1854-1863: one active actor whose simable is gone is removed per update (an actor that lost its
            // car and is inactive stays, with its voice, until ShuffleActors / ResetPursuit)
            foreach (var a in Actors)
                if (a.Active && !a.IsHeli && a.Car == null) { RemoveCop(a); break; }
            Dispatch.Update();
        }

        AIRacer[] _racers = System.Array.Empty<AIRacer>();
        float _racerScan;
        /// <summary>mRacerCount (IVehicle::GetList(VEHICLE_AIRACERS).size()).</summary>
        public int RacerCount { get; private set; }

        /// <summary>SoundAI::SyncCarsToActors' first part (SoundAI.cpp:1690-1713): the closest AI racer's DistanceToView
        /// against speechtune AIRacerProximity sets RACERS_PROXIMAL ("multiple suspects"); when it clears a random active unit
        /// says FocusChange. The racer list is rescanned twice a second [ours].</summary>
        void SyncRacers()
        {
            _racerScan -= MainRate;
            if (_racerScan <= 0f)
            {
                _racerScan = 0.5f;
                _racers = Object.FindObjectsByType<AIRacer>(FindObjectsSortMode.None);
            }
            RacerCount = _racers.Length;
            float closest = 65535f;
            var cam = Camera.main;
            foreach (var r in _racers)
            {
                if (!r) continue;
                float d = cam ? Vector3.Distance(r.transform.position, cam.transform.position) : Vector3.Distance(r.transform.position, PlayerPos);
                if (d < closest) closest = d;
            }
            if (closest <= Tune.AIRacerProximity) _flags |= RACERS_PROXIMAL;
            else
            {
                if ((_flags & RACERS_PROXIMAL) != 0) GetRandomActiveCop(0, false)?.FocusChange();
                _flags &= ~RACERS_PROXIMAL;
            }
        }

        SpeechCop FindActor(CopCar cop)
        {
            foreach (var a in Actors) if (a.Handle == cop) return a;
            return null;
        }

        void UpdateBlowby(SpeechCop actor, bool isAhead)
        {
            if (Now - BlowbyTime > Tune.BlowbyInterval && PlayerSpeed > actor.Speed && !isAhead)
                SetBlowby(actor.Distance, PlayerSpeed - actor.Speed);
            if (actor.Distance < BlowbyDistance && PlayerSpeed > actor.Speed && !isAhead && PlayerSpeed - actor.Speed > PlayerSpeed * 0.75f)
                SetBlowby(actor.Distance, PlayerSpeed - actor.Speed);
        }

        void SetBlowby(float dist, float speed) { BlowbyDistance = dist; BlowbySpeed = speed; BlowbyTime = Now; }

        /// <summary>The "BlewByCop" drive-by moment (EAXAITunerCar::UpdatAIDriveBy: within 12 m at 15 m/s closing) feeding
        /// Observer::MessageBlewPastCop; one per cop every 2 s [?].</summary>
        void WatchBlowPast(CopCar cop, SpeechCop actor)
        {
            if (!_playerBody || !cop.Body) return;
            if ((cop.transform.position - PlayerPos).sqrMagnitude > 12f * 12f) return;
            if ((cop.Body.linearVelocity - _playerBody.linearVelocity).magnitude < 15f) return;
            if (_blewBy.TryGetValue(cop, out double t) && Now - t < 2.0) return;
            _blewBy[cop] = Now;
            Observer.MessageBlewPastCop(actor, cop);
        }

        /// <summary>The pursuit's current roadblock (IPursuit::GetRoadBlock): the nearest one with cars, from the cops' links [?].</summary>
        CopRoadblock FindRoadblock()
        {
            CopRoadblock best = null;
            float bd = float.MaxValue;
            foreach (var pc in Police.CopCars)
            {
                if (!pc) continue;
                var cop = pc.GetComponent<CopCar>();
                var rb = cop ? cop.Roadblock : null;
                if (rb == null || rb.NumCops == 0) continue;
                float d = (rb.Centre - PlayerPos).sqrMagnitude;
                if (d < bd) { bd = d; best = rb; }
                if (_roadblocksSeen.Add(rb)) RB.MessagePositionUpdate(SpikeOffset(rb));
            }
            if (best != null && best.Dodged) RB.MessageRoadBlockDodged(best);
            return best;
        }

        /// <summary>MReqRoadBlock "Position": the spike strip's lateral offset (RoadblockElement.mOffsetX) in metres.</summary>
        static int SpikeOffset(CopRoadblock rb)
        {
            if (rb.SpikeStrips <= 0) return int.MinValue;
            var right = RoadGraph.RightOf(RoadGraph.FlatDir(rb.Dir));
            foreach (var p in rb.Props)
                if (p && p.GetComponentInChildren<SpikeStrip>()) return Mathf.RoundToInt(Vector3.Dot(p.transform.position - rb.Centre, right));
            return 0;
        }

        /// <summary>SoundAI::SyncFormations.</summary>
        void SyncFormations()
        {
            foreach (var c in Actors)
            {
                if (c.InFormation && !c.IsHeli) { if (!CopsInFormation.Contains(c)) CopsInFormation.Add(c); }
                else CopsInFormation.Remove(c);
            }
        }

        /// <summary>SoundAI::ShuffleActors: one actor per voice; the leader is the first active primary with sight.</summary>
        void ShuffleActors()
        {
            if (Actors.Count == 0) return;
            _tmp.Clear(); _tmp2.Clear();
            foreach (var c in Actors) (c.Active ? _tmp : _tmp2).Add(c);
            foreach (var i in _tmp2)
                foreach (var j in _tmp)
                    if (i.SpeakerID == j.SpeakerID) { RemoveCop(i); break; }
            for (int a = 0; a < _tmp.Count; a++)
                for (int b = a + 1; b < _tmp.Count; b++)
                    if (_tmp[a].SpeakerID == _tmp[b].SpeakerID && _tmp[a] != _tmp[b])
                    {
                        var far = _tmp[a].Distance < _tmp[b].Distance ? _tmp[b] : _tmp[a];
                        RemoveCop(far);
                        _tmp.Remove(far);
                        a = -1;
                        break;
                    }
            if (_tmp.Count == 0) return;
            if (Leader == null || !Leader.Active || Leader.IsHeli)
            {
                foreach (var c in _tmp)
                    if (c.IsPrimary && c.HasLOS)
                    {
                        Leader = c;
                        if (Focus == kStrategyFlow && !Leader.IsHeli) Leader.PrimaryEngage();
                        break;
                    }
            }
            if (Leader != null && !Leader.Active) MakeLeader(Leader);
        }

        /// <summary>SoundAI::AddNewCop: a battalion (keyed by car type, else the road's region), then either the voice of
        /// a farther actor moves to this closer car, or a free voice (Cross for copcross, a secondary for roadblock cars).</summary>
        void AddNewCop(CopCar cop)
        {
            string type = cop.Type?.Name ?? "";
            bool isRB = _roadblock != null && _roadblock.Cars.Contains(cop);
            bool isCross = type == "copcross";
            int bID = BattalionFor(cop, type);
            if (bID == SkipCop) return;   // GetRoadSpeechId() == MAX_ROADNAMES: the cop gets no voice (SoundAI.cpp:2056)
            SpeechCop latest = null;
            if (_voices.Count == 0 || !isCross)
            {
                float distance = Vector3.Distance(PlayerPos, cop.transform.position);
                foreach (var c in Actors)
                {
                    if (c.IsHeli || isRB || !(distance < c.Distance)) continue;
                    if (c.Callsign != bID) { bID = BattalionFor(cop, type); if (bID == SkipCop) return; }
                    c.Handle = cop;
                    c.Callsign = bID;
                    c.UnitNumber = GetCallsign(bID);
                    c.Update();
                    latest = c;
                    break;
                }
            }
            if (latest == null)
            {
                if (_voices.Count > 0 || isCross)
                {
                    int voice = GetVoice(isCross ? 3 : isRB ? 2 : 0);
                    if (voice > 0)
                    {
                        latest = new SpeechCop(this, voice, cop, bID, GetCallsign(bID));
                        latest.Rank = Actors.Count;
                        Actors.Add(latest);
                        latest.Update();
                    }
                }
                if (latest == null) return;
            }
            LatestCop = latest;
            if ((PursuitState == kActive || PursuitState == kSearching) && !isRB && Focus == kStrategyFlow)
            {
                if (Rand01() > 0.5f) LatestCop.BackupArrives(); else LatestCop.UnitBackupReply();
            }
        }

        const int SkipCop = int.MinValue;

        /// <summary>SoundAI::AddNewCop's battalion (SoundAI.cpp:2052-2065): GetBattalionFromKey (Rhino SUVs, Super Pursuit sports
        /// cars and Cross); else the drive-to nav's road speech id: MAX_ROADNAMES (the port: no road segment within 30 m of the
        /// car [?: the nav's road]) skips the car (<see cref="SkipCop"/>), a road with a region gives its battalion
        /// (GetBattalionFromRoadID), any other 1 &lt;&lt; bRandom(4).</summary>
        int BattalionFor(CopCar cop, string type)
        {
            if (type.StartsWith("copsuv")) return Csis.BatRhino;
            if (type == "copsport" || type == "copsportghost" || type == "copsporthench" || type == "copcross") return Csis.BatSuperPursuit;
            int road = RoadAt(cop.transform.position, out var tangent);
            if (tangent == Vector3.zero) return SkipCop;
            int bid = CopSpeechRules.BattalionFromRoad(road);
            return bid > 0 ? bid : 1 << Rand(4);
        }

        void AddNewHeli(CopHeli heli)
        {
            var h = new SpeechHeli(this, heli);
            Actors.Add(h);
            Heli = h;
            if (Focus != kPursuitFlow)
            {
                if (Focus != kTerminal && Focus != kCullCheck) h.BackupArrives();
                return;
            }
            _flags |= HELI_INTRO_REQ;
        }

        void RemoveCop(SpeechCop cop)
        {
            if (!Actors.Remove(cop)) return;
            CopsInFormation.Remove(cop);
            if (Leader == cop) Leader = null;
            if (Heli == cop) Heli = null;
            if (LatestCop == cop) LatestCop = null;
            Pursuit.OnCopRemoved(cop);
            Observer.OnCopRemoved(cop);
            _voices.Add(cop.SpeakerID);
            Mgr.FlushSpeechForActor(cop);
        }

        /// <summary>SoundAI::MakeLeader: a primary voice leads; a secondary swaps voices with a primary (or becomes one).</summary>
        public bool MakeLeader(SpeechCop c)
        {
            if (c == null) return false;
            if (Leader != null && Leader.SpeakerID == c.SpeakerID) return true;
            if (c.Active && (c.SpeakerID == Speaker.Primary1 || c.SpeakerID == Speaker.Primary2 || c.SpeakerID == Speaker.Primary3 ||
                             c.SpeakerID == Speaker.Heli || c.SpeakerID == Speaker.Cross))
            {
                if (Leader != c) { Leader = c; if (Focus == kStrategyFlow) c.PrimaryEngage(); }
                return true;
            }
            if (!Actors.Contains(c)) return false;
            SpeechCop primary = null;
            foreach (var a in Actors)
                if (a.SpeakerID == Speaker.Primary1 || a.SpeakerID == Speaker.Primary2 || a.SpeakerID == Speaker.Primary3 || a.SpeakerID == Speaker.Cross) { primary = a; break; }
            if (primary == null)
            {
                int r = Rand(3);
                c.SpeakerID = r == 1 ? Speaker.Primary2 : r == 2 ? Speaker.Primary3 : Speaker.Primary1;
                _voices.Remove(c.SpeakerID);
                if (Leader != c) { Leader = c; if (Focus == kStrategyFlow) c.PrimaryEngage(); }
                return true;
            }
            int sa = c.SpeakerID;
            c.SpeakerID = primary.SpeakerID;
            primary.SpeakerID = sa;
            if (Leader != c) { Leader = c; if (Focus == kStrategyFlow) c.PrimaryEngage(); }
            return true;
        }

        /// <summary>SoundAI::GetVoice: 1 primary, 2 secondary, 3 Cross, else any of 3..8 (first free in the shuffled pool).</summary>
        int GetVoice(int type)
        {
            for (int i = 0; i < _voices.Count; i++)
            {
                int v = _voices[i];
                bool ok = type switch
                {
                    1 => v == Speaker.Primary1 || v == Speaker.Primary2 || v == Speaker.Primary3,
                    2 => v == Speaker.Secondary1 || v == Speaker.Secondary2 || v == Speaker.Secondary3,
                    3 => v == Speaker.Cross,
                    _ => v >= Speaker.Primary1 && v <= Speaker.Secondary3,
                };
                if (ok) { _voices.RemoveAt(i); return v; }
            }
            return -1;
        }

        /// <summary>SoundAI::GetCallsign: shuffled call-sign pools per battalion (City 20, Super Pursuit 5, Rhino 6, others 10).</summary>
        int GetCallsign(int battalion)
        {
            int count;
            switch (battalion)
            {
                case Csis.BatRosewood: case Csis.BatCoastal: case Csis.BatAlpine: count = 10; break;
                case Csis.BatCity: count = 20; break;
                case Csis.BatSuperPursuit: count = 5; break;
                case Csis.BatRhino: count = 6; break;
                default: return -1;
            }
            if (!_callsigns.TryGetValue(battalion, out var pool)) _callsigns[battalion] = pool = new List<int>();
            if (pool.Count == 0)
            {
                for (int i = 0; i < count; i++) pool.Add(1 << i);
                Shuffle(pool);
            }
            int id = pool[0];
            pool.RemoveAt(0);
            return id;
        }

        /// <summary>SoundAI::RandomBailoutDeny: the other primaries tell a unit in formation to stay on him.</summary>
        public void RandomBailoutDeny(SpeechCop wimp)
        {
            if (!wimp.InFormation || Actors.Count <= 1 || PursuitState == kInactive) return;
            foreach (var c in Actors)
                if (c.IsPrimary && c.SpeakerID != wimp.SpeakerID) c.DenyBailout();
        }

        // ============================================================================================ player

        /// <summary>SoundAI::SyncPlayers: road and heading (a direction change on a new road), the player's car,
        /// speed and stop timer, heat jumps (dispatch / a primary / the heli; jurisdiction shift at heat 3 and 5),
        /// the pursuit level's roadblock and spike flags.</summary>
        void SyncPlayers()
        {
            var t = _player.transform;
            PlayerPos = t.position;
            // mPlayerFW (SoundAI.h:560) is read by SyncCarsToActors but never written: not in the decomp, and no store to the
            // PC field (this+0x118; SyncPlayers @0x71D080 only smooths mSmoothedFWRoad at +0x124) — it stays zero, so no cop is
            // ever "ahead" (COPS_ARE_AHEAD only with no active cars, ReinitiatePursuit always RegainVisual) [?: zeroed memory]
            _playerFW = Vector3.zero;
            var vel = _playerBody ? _playerBody.linearVelocity : Vector3.zero;

            int road = RoadAt(PlayerPos, out _);
            _road0 = road;
            int dir = CalcPlayerDirection();
            _dir0 = dir;
            if (IsHeadingValid)
            {
                bool roadChanged = _road1 != _road0;
                _road1 = _road0;
                _dir1 = _dir0;
                if (_lastKnownDir != dir && InPursuit && Police.PerpInSight && roadChanged)
                {
                    if (PursuitState == kActive && Focus != kPursuitFlow) FindClosestCop(true, true)?.DirectionChange();
                    _lastKnownDir = dir;
                    _lastKnownRoad = _road0;
                }
            }

            UpdatePlayerCar();
            PlayerSpeed = vel.magnitude * 2.23699f;
            if (PlayerSpeed < Tune.MinSpeedConsideredStopped && (_flags & LOWSPEEDTIMER) == 0) { _tReallyLowSpeed = Now; _flags |= LOWSPEEDTIMER; }
            else if (PlayerSpeed >= Tune.MinSpeedConsideredStopped) { _flags &= ~LOWSPEEDTIMER; _tReallyLowSpeed = Now; }

            float heatValue = Police.HeatValue;
            Heat = (int)heatValue;
            if (Focus != kPursuitFlow && PursuitDuration > 5f && heatValue > _prevHeat)
            {
                int jump = -1;
                for (int i = 3; i >= 0; i--)
                    if (_prevHeat < i + 2 && heatValue >= i + 2) { jump = i; _prevHeat = heatValue; break; }
                if (jump >= 0 && (PursuitState == kActive || PursuitState == kSearching))
                {
                    int level = 1 << jump;   // heat_cutoffs: 2.0 -> heat_level_2 .. 5.0 -> heat_level_5
                    if (Rand01() > 0.5f) Dispatch.HeatJump(level);
                    else
                    {
                        var cop = GetRandomActiveCop(1, false);
                        if (Heli != null)
                        {
                            if (Rand01() > 0.5f) Heli.HeatJump(level);
                            else cop?.HeatJump(level);
                        }
                        else cop?.HeatJump(level);
                    }
                    if (Rand01() > 0.5f)
                    {
                        if (heatValue >= 3f && heatValue < 4f) Dispatch.JurisShift(Csis.JurisState);
                        else if (heatValue >= 5f) Dispatch.JurisShift(Csis.JurisFederal);
                    }
                }
            }
            // (prev_heat is a function static in the game: never reset, so a level is announced once per session)

            var row = Police.PerpRow;   // perp->GetPursuitLevelAttrib()
            if (row != null)
            {
                if (row.RoadblockProbability > 0f) _flags |= RB_ENABLED; else _flags &= ~RB_ENABLED;
                if (row.RoadblockHeliChance > 0f) _flags |= HELIRB_ENABLED; else _flags &= ~HELIRB_ENABLED;   // 0 on every PC row
                if (row.RoadblockSpikeChance > 0f) _flags |= SPIKES_ENABLED; else _flags &= ~SPIKES_ENABLED;
                Check911(row);
            }
            UpdateTunnel();

            var unit = vel.sqrMagnitude > 1e-6f ? vel.normalized : Vector3.zero;
            _smoothedFW = _smoothedFW * 0.9f + unit * 0.1f;
        }

        /// <summary>
        /// The 911 report (SoundAI::SyncPlayers, SoundAI.cpp:1620-1661): with no pursuit and no 911 active, cops enabled,
        /// free roam, and mCTS911 &gt;= CTSFor911 or mTrafficHits911 &gt;= NumCiviHitsFor911 (or a "scripted" 911 when the
        /// lockout has under 20 s left outside free roam and races), dispatch reports a possible wanted (high intensity)
        /// or a speeder. Not on the D-Day race (no pursuit AI runs there in the port). Rescheduling merges with the
        /// queued event until it plays.
        /// </summary>
        void Check911(HeatRow row)
        {
            if (PursuitState != kInactive || Is911Active || !Police.CopsEnabled) return;
            bool roaming = Police.IsRoaming;
            bool isRace = IsPursuitRace;
            float tLockout = Police.LockoutTimeRemaining;
            bool scripted = tLockout > 0f && tLockout < 20f && !isRace && !roaming;
            bool req = (Havoc >= row.CTSFor911 || NumTrafficHits >= row.NumCiviHitsFor911) && roaming;
            if (req || scripted) Dispatch.Report911(IsHighIntensity ? Csis.PossibleWanted : Csis.GenericSpeeder);
        }

        /// <summary>SoundAI::Force911State (:1671-1682): when Disp911Report finishes playing.</summary>
        public void Force911State()
        {
            var p = Police;
            if (p == null || !p.CopsEnabled) return;
            var row = p.PerpRow;   // perp != nullptr && mPursuitLevel.IsValid()
            if (row == null) return;
            _flags |= DISP911_ACTIVE;
            p.Set911CallTime(row.Lifetime911);
            p.LockoutCops(false);
        }

        /// <summary>Observer::MessageTunnelUpdate (MMiscSound "TunnelUpdate" from the player's SFXCTL_Tunnel): entering a
        /// tunnel while the helicopter has the suspect in sight makes it warn "approaching tunnel".</summary>
        void UpdateTunnel()
        {
            // the message comes from the player's SFXCTL_Tunnel::UpdateIsInTunnel (@0x4C5C00) on entry (1) / exit (0)
            TunnelCtl tc = null;
            foreach (var e in NfsMw.Game.EngineAudio.All) if (e && e.IsPlayer && e.Tunnel != null) { tc = e.Tunnel; break; }
            bool inTunnel = tc != null ? tc.InTunnel : PoliceZones.InTunnel(PlayerPos);
            if (inTunnel == PlayerInTunnel) return;
            PlayerInTunnel = inTunnel;
            if (inTunnel && Heli != null && Heli.Active && Heli.HasLOS) Heli.HazardAlert(Csis.HazardTunnel);
        }

        /// <summary>
        /// SoundAI::MessageUnspawnCop (:260-280): a car the manager unspawned (reasons 1 and 3..6) only goes inactive,
        /// so a unit in formation says its bailout (EAXCop::SetActive); reasons 0 and 2 remove the actor at once.
        /// </summary>
        void MessageUnspawnCop(CopCar cop, int reason)
        {
            var actor = cop ? FindActor(cop) : null;
            if (actor == null) return;
            switch (reason)
            {
                case 1: case 3: case 4: case 5: case 6:
                    actor.SetActive(false);
                    return;
                default:
                    RemoveCop(actor);
                    return;
            }
        }

        /// <summary>MReqBackup to "Request" / "ReqDenied" (AICopManager.cpp:1485, 1662-1669) for the strategy flow.</summary>
        void MessageBackup(int type, bool denied)
        {
            if (denied) Strategy.MessageBackupDenied(type);
            else Strategy.MessageReqBackup(type);
        }

        void UpdatePlayerCar()
        {
            var car = GameSession.ActiveCar;
            string name = car?.Car ?? _player.Spec?.Name;
            if (name != _verbalCar)
            {
                _verbalCar = name;
                _verbalType = 0;
                var c = GameData.Attributes?.Get("pvehicle", name ?? "");
                if (c != null && c.TryGet("VerbalType", out int at, out _, out _)) _verbalType = Chunks.I32(c.Source.Data, at);
            }
            // SoundAI::SyncPlayers: re-read while the colour is 0 (mPlayerCarCustom->color == 0)
            if (car != _colorCar || _playerColor == 0) { _colorCar = car; _playerColor = car != null ? SpeechColour(car) : 0; }
        }

        OwnedCar _colorCar;

        /// <summary>ColourHashToSoundColourMap (PC .data 0x902028, 13 pairs): STRINGHASH_RED .. BEIGE → Csis car_color bits.</summary>
        static readonly (uint hash, int colour)[] ColourHashToSoundColour =
        {
            (0x0000D99A, 1), (0x027FF2DC, 2), (0x03F9E820, 4), (0x00136707, 8), (0x02DDC8F0, 16), (0x713E201B, 32), (0x79F61014, 64),
            (0x00163085, 128), (0x73D2EDD7, 256), (0x02837B47, 512), (0x87AC789B, 1024), (0x001B06B1, 2048), (0x027C3EBB, 4096),
        };

        /// <summary>
        /// SoundAI::GetCustomized (SoundAI.cpp:2425-2494): the installed CARSLOTID_BASE_PAINT (76) part, else the car type's
        /// GetDefaultBasePaint part (CarPartDB.NewGetCarPart), its applied attribute SPEECHCOLOUR looked up in
        /// ColourHashToSoundColourMap; 0 when nothing matches. The RGB classifier (CopSpeechRules.NearestColour) only stands in
        /// when the part database cannot be read [ours].
        /// </summary>
        static int SpeechColour(OwnedCar car)
        {
            try
            {
                var part = CarParts.Installed(car, 76);
                if (part == null && CarParts.Db.CarTypes.TryGetValue(CarParts.TypeName(car.Car), out var type))
                    foreach (var p in CarParts.Db.PartsFor(CarParts.TypeName(car.Car), 76))
                        if (p.NameHash == type.DefaultBasePaint) { part = p; break; }
                if (part == null) return CarParts.Db.Parts.Count == 0 ? CopSpeechRules.NearestColour(car.R, car.G, car.B) : 0;
                uint h = part.U(EaHash.Hash("SPEECHCOLOUR"));
                foreach (var (hash, colour) in ColourHashToSoundColour) if (hash == h) return colour;
                return 0;
            }
            catch (System.Exception) { return CopSpeechRules.NearestColour(car.R, car.G, car.B); }
        }

        /// <summary>SoundAI::CalcPlayerDirection: the smoothed velocity's dominant sim axis, reported after 3 s steady.
        /// Sim x = -Unity z, sim z = Unity x (RoadNetwork: game X = sim z, game Y = -sim x), so +x Unity is
        /// Southbound (2), -x Northbound (1), -z Eastbound (4), +z Westbound (8).</summary>
        int CalcPlayerDirection()
        {
            float simX = -_smoothedFW.z, simZ = _smoothedFW.x;
            int dir;
            if (Mathf.Abs(simX) > Mathf.Abs(simZ)) dir = simX > 0f ? 4 : 8;
            else dir = simZ > 0f ? 2 : 1;
            if (dir != _dirTracking) { _dirTracking = dir; _tCurrDir = Now; }
            return Now - _tCurrDir > 3.0 ? dir : 0;
        }

        /// <summary>WRoadNav::GetRoadSpeechId at a position: the nearest road segment's RNrd speech id (0 untagged).</summary>
        public int RoadAt(Vector3 pos, out Vector3 tangent)
        {
            tangent = Vector3.zero;
            RoadGraph.Seg best = null;
            float bd = 30f, bt = 0f;
            foreach (int si in RoadGraph.Near(pos, 30f))
            {
                var s = RoadGraph.Segs[si];
                float d = RoadGraph.Closest(s, pos, out float tt);
                if (d < bd) { bd = d; best = s; bt = tt; }
            }
            if (best == null) return CopSpeechRules.RoadUntagged;
            tangent = RoadGraph.Tangent(best, bt);
            return best.Road >= 0 && best.Road < _roadSpeech.Length ? _roadSpeech[best.Road] : CopSpeechRules.RoadUntagged;
        }

        /// <summary>Observer::NotifyAirborne input: in the game the jump camera trigger (PC 0x479F80, speed 10 m/s or more) passes its predicted flight at take-off; here the
        /// actual hang time and height above the ground are reported once the car has been in the air for
        /// HangTimeForCommentary at 10 m/s or more [?].</summary>
        void TrackAirborne(float dt)
        {
            if (!_player || PursuitState != kActive) { _airTime = 0; _airNotified = false; return; }
            if (_player.WheelsOnGround == 0)
            {
                if (_airTime == 0) _takeoffY = _player.transform.position.y;
                _airTime += dt;
                if (!_airNotified && _airTime >= Tune.HangTimeForCommentary && _player.SpeedMps >= 10f)
                {
                    _airNotified = true;
                    float alt = Physics.Raycast(_player.transform.position, Vector3.down, out var hit, 50f, ~0, QueryTriggerInteraction.Ignore)
                        ? hit.distance : _player.transform.position.y - _takeoffY;
                    Observer.NotifyAirborne(alt, _airTime);
                }
            }
            else { _airTime = 0; _airNotified = false; }
        }

        // ============================================================================================ collisions

        /// <summary>SoundAI::OnCollision for a contact of the player car (player vs cop, traffic, props, world).</summary>
        internal void OnPlayerCollision(Collision c, Vector3 prevVel)
        {
            if (Police == null || !_player) return;   // (no BUSTED gate in SoundAI::OnCollision; Observe() refuses the terminal focus)
            if (c.relativeVelocity.sqrMagnitude < 25f * 0.44704f) return;   // CopMinClosingVelSq = MPH2MPS(25), compared squared
            var otherBody = c.rigidbody;
            var otherCar = otherBody ? otherBody.GetComponent<PlayerCar>() : null;
            float intensity = Intensity(c, _playerBody, otherBody);
            var cop = otherCar ? otherCar.GetComponent<CopCar>() : null;
            var actor = cop ? FindActor(cop) : null;
            if (actor != null) { CopVsPlayer(c, cop, actor, intensity); return; }

            // the player branch: crash slowdown, roadblock props, traffic
            float before = prevVel.magnitude, after = _playerBody ? _playerBody.linearVelocity.magnitude : before;
            float pct = before > 0f ? after / before : 1f;
            bool objectHit = otherBody != null && !otherBody.isKinematic;
            // smackables and roadblock props (OBJECT hits in the game; possibly static colliders in the port)
            bool prop = PropHit(c, intensity);
            if (!objectHit && !prop)
            {
                // WORLD
                if (pct >= 1f - Tune.CrashSlowdownPct) return;
                _tLastCrashed = Now;
                if (PursuitState == kActive && Focus == kStrategyFlow)
                {
                    var spkr = GetRandomActiveCop(0, true);
                    if (spkr != null) Observer.Observe(SpeechObservation.Collision_Suspect_World, spkr.SpeakerID, intensity);
                }
                return;
            }
            if (pct < 1f - Tune.CrashSlowdownPct) _tLastCrashed = Now;
            // SoundAI.cpp:661-690: DRIVER_TRAFFIC / DRIVER_NONE only; DRIVER_RACER (AI racers) and a cop without an actor fall to
            // the default and say nothing; DRIVER_HUMAN (two-player) would be Collision_Suspect_Suspect
            if (otherCar && otherCar.Traffic)
            {
                Observer.Observe(SpeechObservation.Collision_Suspect_Traffic, -1, intensity);
                var spkr = FindClosestCop(true, true);
                int id = spkr != null ? spkr.SpeakerID : -1;
                // SoundAI.cpp:674: GetVehicleClass() == TRACTOR || TRAILER (pvehicle CLASS, the traffic sim's VehicleSpecs)
                var sim = otherCar.GetComponent<NfsMw.Game.TrafficPhysics.TrafficVehicle>();
                string n = otherCar.Spec?.Name ?? "";
                bool semi = sim != null && sim.Specs != null ? sim.Specs.IsTractor || sim.Specs.IsTrailer : n.StartsWith("semi");
                if (semi) { if (intensity >= Tune.MinIntensityTrafficSmash) Observer.Observe(SpeechObservation.Collision_Suspect_Semi, id, intensity); }
                else Observer.Observe(SpeechObservation.Collision_Suspect_Traffic, id, intensity);
                if (intensity >= Tune.MinIntensityTrafficSmash) NumTrafficHits++;
            }
        }

        /// <summary>The OBJECT part of the player branch: cost to state of smackables while no pursuit is on (mCTS911)
        /// and hits on the roadblock's spike belts / barriers. True if the contact was a smackable or a prop.</summary>
        bool PropHit(Collision c, float intensity)
        {
            var go = c.collider ? c.collider.gameObject : null;
            if (!go) return false;
            var smack = go.GetComponentInParent<Smackable>();
            if (smack && PursuitState == kInactive) Havoc += smack.CostToState;
            if (_roadblock == null) return smack;
            foreach (var p in _roadblock.Props)
            {
                if (!p || !(go == p || go.transform.IsChildOf(p.transform))) continue;
                if (p.GetComponentInChildren<SpikeStrip>())
                {
                    RB.NailedSomethingInRB(RoadblockFlow.NAILED_SPIKES);
                    var spkr = FindClosestCop(false, true);
                    Observer.Observe(SpeechObservation.Collision_Suspect_Spikebelt, spkr != null ? spkr.SpeakerID : -1, intensity);
                }
                else RB.NailedSomethingInRB(RoadblockFlow.NAILED_OTHEROBJ);
                return true;
            }
            return smack;
        }

        /// <summary>The game's collision "speed": impulseA + impulseB as Δv (m/s), scaled over PlayerSmashSpeedRange (mph).</summary>
        float Intensity(Collision c, Rigidbody self, Rigidbody other)
        {
            float imp = c.impulse.magnitude;
            float dv = imp / Mathf.Max(1f, self ? self.mass : 1500f);
            if (other && !other.isKinematic) dv += imp / Mathf.Max(1f, other.mass);
            float min = Tune.PlayerSmashSpeedMin * 0.44704f, max = Tune.PlayerSmashSpeedMax * 0.44704f;
            return Mathf.Clamp01((dv - min) / (max - min));
        }

        /// <summary>The cop-vs-player branch of SoundAI::OnCollision: a ram during the setup restarts the pursuit flow as
        /// "unit rammed"; a ram of a unit not braking, not an SUV, not in a roadblock makes it shout (rear-ended, T-boned,
        /// head-on, side-swiped, by intensity).</summary>
        void CopVsPlayer(Collision c, CopCar cop, SpeechCop actor, float intensity)
        {
            bool copBraking = cop.Car != null && (cop.Car.Input.Brake > 0f || cop.Car.Input.Handbrake);
            if (Focus == kPursuitFlow && (_flags & SETUP_RESTARTED) == 0)
            {
                var cause = Pursuit.Cause;
                if (cause != PursuitFlow.kScripted && cause != PursuitFlow.kCopAssaulted && cause != PursuitFlow.kCopAssaultedScripted && Pursuit.RequiresRestart())
                {
                    Mgr.ClearPlayback();
                    Mgr.ReleaseResource();
                    _tLastCopNailed = Now;
                    actor.WasRammed();
                    Pursuit.Cause = cause == PursuitFlow.kScripted ? PursuitFlow.kCopAssaultedScripted : PursuitFlow.kCopAssaulted;
                    Pursuit.ChangeStateTo(PursuitFlow.kPrimaryBranch);
                    Pursuit.Update();
                    _flags |= SETUP_RESTARTED;
                }
            }
            bool inRB = _roadblock != null && _roadblock.Cars.Contains(cop);
            if (inRB) RB.NailedSomethingInRB(RoadblockFlow.NAILED_VEHICLE);
            else Observer.Observe(SpeechObservation.Collision_Cop_Suspect, -1, intensity);
            bool suv = cop.Type != null && cop.Type.Name.StartsWith("copsuv");

            // impact geometry (arms in each car's local frame, half extents as dimensions)
            Vector3 contact = c.contactCount > 0 ? c.GetContact(0).point : cop.transform.position;
            Vector3 normal = c.contactCount > 0 ? c.GetContact(0).normal : Vector3.up;
            var pT = _player.transform;
            var cT = cop.transform;
            Vector3 armCop = cT.InverseTransformPoint(contact), armRacer = pT.InverseTransformPoint(contact);
            Vector3 dimCop = HalfExtents(cop.gameObject), dimRacer = HalfExtents(_player.gameObject);
            Vector3 velCop = cop.Body ? cop.Body.linearVelocity : Vector3.zero;
            Vector3 velRacer = _playerBody ? _playerBody.linearVelocity : Vector3.zero;
            float fwDot = Vector3.Dot(pT.forward, cT.forward);
            float vnCop = Mathf.Abs(Vector3.Dot(velCop, normal)), vnRacer = Mathf.Abs(Vector3.Dot(velRacer, normal));
            bool copRammed = false;
            int coll = -1;   // 1 PerpRECop, 3 PerpTBCop, 5 PerpHOCop, 7 PerpSSCop
            if (fwDot >= 0.7f)
            {
                if (Mathf.Abs(armCop.x) > dimCop.x * 0.75f && Mathf.Abs(armRacer.x) > dimRacer.x * 0.75f)
                {
                    if (vnCop <= vnRacer) { coll = 7; copRammed = true; }
                }
                else
                {
                    // (the game's rear-end test compares armRacer.z with itself)
                    if (armRacer.z >= dimRacer.z * 0.75f && armCop.z <= dimCop.z * -0.75f)
                    {
                        if (velRacer.magnitude > velCop.magnitude) copRammed = true;
                        coll = 1;
                    }
                }
            }
            else if (fwDot > -0.7f)
            {
                if (armRacer.z >= dimRacer.z * 0.7f) { copRammed = true; coll = 3; }
                if (armCop.z >= dimCop.z * 0.7f) coll = 2;
            }
            else
            {
                if (vnRacer > vnCop) { copRammed = true; coll = 5; }
            }

            if (!copRammed) return;
            _tLastCopNailed = Now;
            if (inRB || suv || copBraking || (_flags & COPS_IMMUNE) != 0) return;
            actor.WasRammed();
            if (Focus == kPursuitFlow || Focus == kTerminal || actor.IsDead || !actor.Active) return;
            if (intensity > 0.15f && intensity <= 0.5f)
            {
                if (coll == 1) actor.RearEnded(Csis.Normal);
                else if (coll == 3) actor.TBoned(Csis.Normal);
                else if (coll == 5) actor.HeadOn(Csis.Normal);
                else if (coll == 7) actor.SideSwiped(Csis.Normal);
                return;
            }
            if (intensity > 0.5f && intensity <= 0.75f)
            {
                if (Rand01() < 0.67f)
                {
                    if (coll == 1) actor.RearEnded(Csis.High);
                    else if (coll == 3) actor.TBoned(Csis.High);
                    else if (coll == 5 && !suv) actor.HeadOn(Csis.High);
                    else if (coll == 7) actor.SideSwiped(Csis.High);
                    else actor.InterruptExpletive();
                }
                else actor.InterruptExpletive();
            }
            else if (intensity > 0.75f) actor.InterruptViolent();
        }

        static readonly Dictionary<GameObject, Vector3> _extents = new Dictionary<GameObject, Vector3>();

        static Vector3 HalfExtents(GameObject go)
        {
            if (_extents.TryGetValue(go, out var e)) return e;
            var b = new Bounds(go.transform.position, Vector3.zero);
            bool any = false;
            foreach (var col in go.GetComponentsInChildren<Collider>())
            {
                if (col.isTrigger) continue;
                if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds);
            }
            e = any ? go.transform.InverseTransformVector(b.extents) : new Vector3(0.95f, 0.7f, 2.3f);
            e = new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z));
            if (_extents.Count > 256) _extents.Clear();
            _extents[go] = e;
            return e;
        }

        /// <summary>SoundAI::OnCollision for a cop car's own contacts: traffic (bailout for heavy traffic) and the world
        /// (bailout on a big smash while on screen).</summary>
        internal void OnCopCollision(CopCar cop, Collision c)
        {
            if (Police == null || cop == null) return;
            if (c.relativeVelocity.sqrMagnitude < 25f * 0.44704f) return;
            var otherBody = c.rigidbody;
            var otherCar = otherBody ? otherBody.GetComponent<PlayerCar>() : null;
            if (otherCar && otherCar == _player) return;            // handled from the player's side
            var actor = FindActor(cop);
            if (otherCar && otherCar.GetComponent<CopCar>()) return; // two actors (or none): ignored
            if (actor == null) return;
            // SoundAI.cpp:385-387: an OBJECT collision (a smackable, a roadblock prop) whose other side is not a vehicle returns;
            // only Info::WORLD contacts reach the cop-world branch
            if (c.collider && c.collider.GetComponentInParent<Smackable>()) return;
            float intensity = Intensity(c, cop.Body, otherBody);
            if (otherCar && otherCar.Traffic)
            {
                Observer.Observe(SpeechObservation.Collision_Cop_Traffic, -1, intensity);
                actor.JustHitTraffic();
                if (PursuitState == kActive && Focus == kStrategyFlow && actor.Active)
                {
                    actor.BailoutTraffic();
                    RandomBailoutDeny(actor);
                }
                return;
            }
            if (otherBody && !otherBody.isKinematic) return;
            // WORLD
            if (!InCameraView(cop.transform.position) || !actor.Active) return;
            if (intensity >= Tune.MinIntensityCopSmash) actor.Bailout();   // BailoutBadRoad with weather
            if (actor.InFormation && CopsInFormation.Count > 1) RandomBailoutDeny(actor);
            Observer.Observe(SpeechObservation.Collision_Cop_World, actor.SpeakerID, intensity);
        }

        // ============================================================================================ events

        void OnEventComplete(ScheduledSpeech e)
        {
            Pursuit.MessageEventComplete(e);
            Strategy.MessageEventComplete(e);
            Observer.MessageEventComplete(e);
        }

        /// <summary>An event named by outside code (PursuitSystem's Radio.Say): the AI owns the radio, so these are only
        /// hints. Returns true when the event exists.</summary>
        public bool Hint(string ev)
        {
            switch (ev)
            {
                case "ExtraCops_ExtraRBAverted":
                    if (_roadblock != null) RB.MessageRoadBlockDodged(_roadblock);
                    break;
            }
            return Mgr.HasEvent(ev);
        }

        // ============================================================================================ MiscSpeech

        static Dictionary<string, int> Spk(int id) => new Dictionary<string, int> { ["speaker_id"] = id };

        /// <summary>MiscSpeech::LostSuspect: by the last speaker, else a random unit voice 3..8.</summary>
        public int LostSuspect(int spkr)
        {
            int id = spkr > 0 ? spkr : 3 + Rand(6);
            var d = Spk(id);
            d["intensity"] = IsHighIntensity ? Csis.High : Csis.Normal;
            Schedule("AnytimeEvents_LostSuspect", d, null);
            return id;
        }

        public int Bailout(int spkr)
        {
            int id = spkr > 0 ? spkr : 3 + Rand(6);
            var d = Spk(id);
            d["bailout_type"] = Csis.BailHigh;
            Schedule("AnytimeEvents_Bailout", d, null);
            return id;
        }

        /// <summary>MiscSpeech::MoreDetails (MiscSpeech.cpp:593): a unit voice 3..6 when none is given; returns it.</summary>
        public int MoreDetails(int spkr)
        {
            int id = spkr > 0 ? spkr : (int)(Rand01() * 4f + 3f);
            Schedule("Setup_MoreDetails", Spk(id), null);
            return id;
        }

        /// <summary>MiscSpeech::Unit911Reply (MiscSpeech.cpp:582).</summary>
        public int Unit911Reply(int spkr)
        {
            int id = spkr > 0 ? spkr : 3 + Rand(6);
            Schedule("AnytimeEvents_Unit911Reply", Spk(id), null);
            return id;
        }

        /// <summary>MiscSpeech::D_Day (MiscSpeech.cpp:693): the story-opening radio call. Nothing calls it in the
        /// decomp (the career scripts do); exposed for Story [ours: entry point].</summary>
        public void D_Day() => Schedule("D_Day", new Dictionary<string, int>(), null);

        /// <summary>
        /// MiscSpeech::MapSMSToSPCHEnums (PC @0x701540): SMS ids in runs of 7/6/7/7/5/5/6/5/6/6/3/4/3/5/4/8/9 from 10 to 0x69 map
        /// to cell_call_bucket 1 &lt;&lt; (16 .. 0) and cell_call_number 1 &lt;&lt; (id - run start); other ids have no call.
        /// </summary>
        public static bool MapSMSToSPCHEnums(int sms, out int bucket, out int number)
        {
            int[] starts = { 10, 0x11, 0x17, 0x1E, 0x25, 0x2A, 0x2F, 0x35, 0x3A, 0x40, 0x46, 0x49, 0x4D, 0x50, 0x55, 0x59, 0x61 };
            int[] counts = { 7, 6, 7, 7, 5, 5, 6, 5, 6, 6, 3, 4, 3, 5, 4, 8, 9 };
            bucket = number = 0;
            for (int i = 0; i < starts.Length; i++)
                if ((uint)(sms - starts[i]) < (uint)counts[i])
                {
                    bucket = 1 << (16 - i);
                    number = 1 << (sms - starts[i]);
                    return true;
                }
            return false;
        }

        /// <summary>
        /// MiscSpeech::SMSCellCall (PC @0x71D860): the SMS's voice call (Speech::Manager::ScheduleSpeech(CellCallStruct, CellCallId),
        /// no actor). Called by uiSMSMessage::NotificationMessage (@0x531DAE / @0x531DDA) and the ECellCall event (@0x62B1CD) —
        /// both other owners' code: they call this [hook wanted]. CopSpeechManager.CellCallStarted / CellCallCompleted report it.
        /// </summary>
        public static void SMSCellCall(int sms)
        {
            if (!MapSMSToSPCHEnums(sms, out int bucket, out int number)) return;
            var ai = CopRadio.AI;
            var args = new Dictionary<string, int> { ["cell_call_bucket"] = bucket, ["cell_call_number"] = number };
            if (ai != null) ai.Schedule(CopSpeechManager.CellCall, args, null);
            else CopRadio.Manager?.Schedule(CopSpeechManager.CellCall, args, null);
        }

        /// <summary>MiscSpeech::DispIntroRace (MiscSpeech.cpp:698), like D_Day.</summary>
        public void DispIntroRace() => Schedule("DispIntroRace", new Dictionary<string, int>(), null);

        public void RBWarning() => Schedule("ExtraCops_RBWarning", new Dictionary<string, int>(), null);

        /// <summary>MiscSpeech::RBPosition: spike belt left (≤ -4), right (&gt; 4) or centre.</summary>
        public void RBPosition(int pos) =>
            Schedule("ExtraCops_RBPosition", new Dictionary<string, int> { ["spikebelt_position"] = pos <= -4 ? Csis.SpikeLeft : pos > 4 ? Csis.SpikeRight : Csis.SpikeCenter }, null);

        public void RBEngaged(bool spikesHit)
        {
            if (_roadblock == null) return;
            Schedule("ExtraCops_ExtraRBEngage", new Dictionary<string, int> { ["roadblock_engage_type"] = spikesHit ? Csis.EngageSpikes : Csis.EngageRoadblock }, null);
        }

        public void RBAverted()
        {
            if (_roadblock == null) return;
            Schedule("ExtraCops_ExtraRBAverted", new Dictionary<string, int> { ["roadblock_engage_type"] = _roadblock.SpikeStrips > 0 ? Csis.EngageSpikes : Csis.EngageRoadblock }, null);
        }

        public void SwarmingReply() => Schedule("ExtraCops_SwarmingReply", new Dictionary<string, int>(), null);
        public void SwarmingReplyFollow() => Schedule("ExtraCops_SwarmingReplyFollow", new Dictionary<string, int>(), null);
        void QuadrantForming() => Schedule("ExtraCops_QuadrentForming", new Dictionary<string, int>(), null);
        void SuspectPossiblyGone() => Schedule("ExtraCops_SuspectPossiblyGone", new Dictionary<string, int>(), null);
        void QuadrantMoving() => Schedule("ExtraCops_QuadrentMoving", new Dictionary<string, int>(), null);
        void OtherLead() => Schedule("ExtraCops_OtherLead", new Dictionary<string, int>(), null);
        void PossibleSuspect() => Schedule("ExtraCops_PossibleSuspect", new Dictionary<string, int>(), null);
        void WrongSuspect() => Schedule("ExtraCops_WrongSuspect", new Dictionary<string, int>(), null);

        /// <summary>A one-line state summary for debugging.</summary>
        public string Describe() =>
            $"focus {Focus} state {PursuitState} pflow {Pursuit.State} sflow {Strategy.State} dur {PursuitDuration:0} los {NumCopsWithLOS} actors {Actors.Count} leader {Leader?.SpeakerID} road {_road0} dir {_dir0} | {Mgr.Describe()}";
    }

    /// <summary>Forwards a car's collisions to the speech AI (Sim::Collision::IListener for SoundAI).</summary>
    public sealed class SpeechCollisionTap : MonoBehaviour
    {
        SpeechAI _ai;
        CopCar _cop;
        Rigidbody _rb;
        Vector3 _prevVel;

        public static void Attach(GameObject go, SpeechAI ai, CopCar cop)
        {
            if (!go) return;
            var t = go.GetComponent<SpeechCollisionTap>();
            if (!t) t = go.AddComponent<SpeechCollisionTap>();
            t._ai = ai;
            t._cop = cop;
        }

        void FixedUpdate()
        {
            if (!_rb) _rb = GetComponent<Rigidbody>();
            if (_rb) _prevVel = _rb.linearVelocity;
        }

        void OnCollisionEnter(Collision c)
        {
            if (_ai == null) return;
            try
            {
                if (_cop) _ai.OnCopCollision(_cop, c);
                else _ai.OnPlayerCollision(c, _prevVel);
            }
            catch (System.Exception e) { Debug.LogException(e); }
        }
    }
}
