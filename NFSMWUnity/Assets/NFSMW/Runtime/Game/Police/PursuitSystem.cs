using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game.Police;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The police: AICopManager (the cop pool, patrols, spawning, roadblocks, support) and the player's AIPursuit (heat,
    /// line of sight, wave counts, formations, finishers, collapse, busted, cooldown and evade) in one component
    /// (specs/ai_cops.md, specs/pursuit.md). The manager ticks at 0.5 and the pursuit at 0.25 of the 60 Hz sim
    /// (30 / 15 Hz); each cop car thinks on its own (<see cref="CopCar"/>). The AI racers' pursuits during races are
    /// <see cref="RacerPursuit"/> objects (one AIPursuit per perpetrator, AICopManager.cpp:1824-1967); the manager runs
    /// in races too, with the race table rows (AIPerpVehicle::SetHeat) and no stats (mAllowStatsToAccumulate).
    /// </summary>
    public sealed partial class PursuitSystem : MonoBehaviour
    {
        public bool Enabled = true;
        public bool InPursuit { get; private set; }
        public int Heat { get; private set; } = 1;
        public float Bounty { get; private set; }
        public float Cooldown { get; private set; }
        public float Busting { get; private set; }
        public int CopsDisabled { get; private set; }
        public PursuitStats Stats { get; private set; } = new PursuitStats();
        /// <summary>Heat as the HUD shows it: the level plus progress to the next one.</summary>
        public float HeatValue { get; private set; } = 1;
        /// <summary>Pursuit meter for the HUD bar: −1 (cooldown) .. +0.5 (busted side).</summary>
        public float BarValue { get; private set; }
        public bool InCooldown { get; private set; }
        /// <summary>Cooldown progress 0..1 (1 = evaded).</summary>
        public float CooldownProgress { get; private set; }
        public float PursuitTime => _pursuitTime;
        public int CopsInvolved { get; private set; }
        public int MaxHeat = 5;
        /// <summary>The Challenge Series pursuit being played (null in free roam).</summary>
        public RaceEvent Challenge { get; private set; }
        /// <summary>The police scanner (speech from copspeech.big).</summary>
        public CopRadio Radio => _radio ? _radio : _radio = gameObject.AddComponent<CopRadio>();

        /// <summary>Every active cop car that is not wrecked (pursuit, patrol, roadblock and support units).</summary>
        public IEnumerable<PlayerCar> CopCars
        {
            get
            {
                foreach (var c in _cops)
                    if (c && c.Car && !c.Destroyed) yield return c.Car;
            }
        }

        // ---- state the cop cars read ----
        public PlayerCar Player { get; private set; }
        /// <summary>IPerpetrator::GetPursuitLevelAttrib: the heat row, or the race table row (race_NN) while racing.</summary>
        public HeatRow Row => PoliceData.RowFor(Heat, UseRaceTables);
        public SupportRow Support => PoliceData.SupportFor(Heat);
        /// <summary>AIPerpVehicle::SetHeat (AIVehicle.cpp:2262-2266): play mode Racing and not a pursuit race selects the
        /// pursuitescalation race tables (racetable / supportracetable; the support race rows are not loaded [?]).</summary>
        public bool UseRaceTables => RaceManager.Active != null && !IsPursuitRace;
        /// <summary>GRaceParameters::GetIsPursuitRace: a Challenge Series pursuit, or a pursuit-type / PursuitRace event.</summary>
        public bool IsPursuitRace => Challenge != null || (RaceManager.Active != null && RaceManager.Active.Event != null && (RaceManager.Active.Event.Type == RaceType.Pursuit || RaceManager.Active.Event.PursuitRace));
        /// <summary>mAllowStatsToAccumulate (AIPursuit.cpp:1631-1633): not racing, or a pursuit race. Gates the pursuit
        /// length, bounty, cops damaged / destroyed / evaded counts and the manager's cops_destroyed.</summary>
        public bool AllowStatsToAccumulate => RaceManager.Active == null || IsPursuitRace;
        /// <summary>GetGlobalPursuitLevelAttrib (AIPursuit.cpp:2684): the local player's pursuit level row (the patrol count
        /// and the cop list every pursuit's wave is scaled from).</summary>
        public HeatRow GlobalRow => InPursuit ? Row : PoliceData.RowFor(PerpHeat, UseRaceTables);
        /// <summary>(int) IPerpetrator::GetHeat of the local player: the pursuit's level, else the car's heat.</summary>
        public int PerpHeat => InPursuit ? Heat : Mathf.Clamp(GameSession.ActiveCar?.Heat ?? 1, 1, 6);
        /// <summary>The player's pursuit as the cop cars see it (IPursuit).</summary>
        public CopPursuit PlayerPursuit => _playerView ?? (_playerView = new PlayerView(this));
        PlayerView _playerView;
        readonly List<RacerPursuit> _racerPursuits = new List<RacerPursuit>();
        /// <summary>The AI racers' pursuits under way (none outside races).</summary>
        public IReadOnlyList<RacerPursuit> RacerPursuits => _racerPursuits;
        public bool PerpInSight { get; private set; }
        public bool IsJerk { get; private set; }
        public readonly PerpRoads Roads = new PerpRoads();
        public Vector3 SeekAhead => Player ? Roads.GetSeekAhead(Player.transform.position, PlayerVelocity, IsJerk, Time.time) : Vector3.zero;
        public Potentials PlayerPotentials { get; private set; }
        public readonly List<PlayerCar> Avoidables = new List<PlayerCar>();
        /// <summary>PursuitMeterCanShowBusted: false for the first 2.5 s after the cooldown starts.</summary>
        public bool PursuitMeterCanShowBusted => !(InCooldown && _stateTime < 2.5f);
        public float LastTrafficHitTime { get; private set; } = -100f;
        public Vector3 LastKnownLocation { get; private set; }
        /// <summary>IPursuit::IsPerpBusted.</summary>
        public bool IsPerpBusted => InPursuit && _busted;
        /// <summary>IPursuit::IsFinisherActive (AIPursuit.cpp:2635): the breaker timer is running.</summary>
        public bool IsFinisherActive => InPursuit && _fs.BreakerTimer >= 0;
        /// <summary>IPursuit::IsCollapseActive.</summary>
        public bool IsCollapseActive => InPursuit && _fs.CollapseActive;
        /// <summary>IPursuit::GetFormationType (for the speech AI's StrategyFlow) [hook: audio owner].</summary>
        public int FormationType => _fs.Type;
        /// <summary>IPursuit::AttemptingToReAquire: not in sight, not busted, not bailed.</summary>
        public bool AttemptingToReAquire => InPursuit && !PerpInSight && !_busted && !_bailed;
        /// <summary>IPursuit::GetBackupETA: the backup countdown (mBackupCountdownTimer).</summary>
        public float BackupEta => InPursuit ? _backupTimer : 0f;
        /// <summary>IPursuit::GetEvadeLevel (mEvadeLevel): 0 until the cooldown meter shows, then elapsed/required
        /// (≥ 0.05), decaying ×0.93 per update after a re-sighting.</summary>
        public float EvadeLevel => InPursuit ? CooldownProgress : 0f;
        /// <summary>
        /// IPursuit::TimeUntilBusted (AIPursuit.cpp:2651-2664): while the busted timer runs, min(1, timer/5) blended
        /// up from the pursuit meter; −1 once the evade level reaches 0.05; else the pursuit meter (−1 .. 0.5).
        /// </summary>
        public float TimeUntilBusted
        {
            get
            {
                if (!InPursuit) return 0f;
                if (Busting > 0.03f) { float rv = Mathf.Min(1f, Busting * 0.2f); return (1f - _meter) * rv + _meter; }
                if (CooldownProgress >= 0.05f) return -1f;
                return _meter;
            }
        }
        /// <summary>Unspawned cop cars (MUnspawnCop to "SoundAI", AICopManager.cpp): reason 0 release/lockout clear,
        /// 1 over budget (UpdateCopPriorities), 3 taken for a roadblock, 4/5 taken from out of view, 6 despawned by
        /// the respawn rules. Raised before the car is destroyed.</summary>
        public event System.Action<CopCar, int> CopUnspawned;
        /// <summary>MReqBackup(type) to "Request" (denied = false) or "ReqDenied" (true): 16 = the RAM SUVs were gathered
        /// (3 s before they spawn) or could not be placed; 64 = Cross started or could not be placed.</summary>
        public event System.Action<int, bool> BackupMessage;

        void SendBackup(int type, bool denied)
        {
            try { BackupMessage?.Invoke(type, denied); } catch (System.Exception e) { Debug.LogException(e); }
        }
        /// <summary>SkidHitEnabled: some AirSupport option of the support row is SKID_HIT.</summary>
        public bool SkidHitEnabled => Support.Air.Any(a => a.strategy == 2);
        /// <summary>VehicleSpawningEnabled(false) (AICopManager.cpp:442): cops on, no NoNewPursuitsOrCops, no lockout, no NIS;
        /// plus [ours] the teleport grace and the race gate.</summary>
        public bool CanStartPursuit => Enabled && !NoNewPursuits && Time.time >= _lockoutUntil && Time.time > _graceUntil && !NisPlayer.Active && RaceAllowsCops;
        /// <summary>[ours] The exe's race scripts call Game.SetCopsEnabled per event; a race not driven by Lua lets the
        /// event's own flags decide (CopsInRace / ScriptedCopsInRace / CopDensity). Driven races are gated by the script.</summary>
        bool RaceAllowsCops
        {
            get
            {
                var race = RaceManager.Active;
                if (!race) return true;
                if (race.Driven || ScriptRace) return true;
                var ev = race.Event;
                return ev != null && (ev.CopsInRace || ev.ScriptedCopsInRace || ev.CopDensityValue > 0);
            }
        }
        public Vector3 PlayerVelocity => _playerBody ? _playerBody.linearVelocity : Vector3.zero;

        readonly List<CopCar> _cops = new List<CopCar>();          // every active cop car
        readonly List<CopCar> _pursuers = new List<CopCar>();      // the pursuit's vehicles (not roadblock cars)
        readonly List<CopRoadblock> _roadblocks = new List<CopRoadblock>();
        readonly Dictionary<CopCar, float> _lastContact = new Dictionary<CopCar, float>();
        CopRoadblock _roadblock;
        CopCar _rbNearest;   // mNearestCopInRoadblock
        CopHeli _heli;
        CopCar _pursuitRequest;
        PlayerCar _pursuitRequestTarget;
        Rigidbody _playerBody;
        PlayerCar _watched;
        CopRadio _radio;
        Vector3 _lastPlayerPos;
        float _graceUntil, _lockoutUntil, _managerAcc, _pursuitAcc, _racerAcc;

        // ---- AIPursuit ----
        float _pursuitTime, _stateTime, _lostTime = 99f, _hideBonus, _combo, _comboTimer, _bustRate;
        float _roadBlockTimer, _spawnCopTimer, _spawnHeliTimer = 10f, _supportCheckTimer = 10f;
        float _backupTimer, _coolDownRequired = 20f, _setupSpeechTime, _meter;
        int _required, _evaded, _backupAt, _totalCops, _rbCopsAdded;
        // the AIPursuit ctor's mBaseHeat / mMaximumHeat (AIPursuit.cpp:629-669)
        float _baseHeat = 1f, _maxHeatNow = 5f;
        bool _busted, _bailed, _backupRequested, _nextRoadblockRequest, _latchedRoadblock, _forceHeliNext, _testHeliSearch, _fastSpawnNext;
        bool _lostSightSaid, _bullhornSaid, _crossSpawned, _crossDestroyed, _supportPriorityDone;
        float _nextChatter, _minDistance3 = float.MaxValue, _minDistanceXZ = float.MaxValue;
        // the active formation, its timers and the collapse / finisher state (PursuitFormationState)
        readonly PursuitFormationState _fs = new PursuitFormationState();
        // jerk (UpdateJerk, AIPursuit.cpp:2824-2853)
        Vector3 _jerkLagPos;
        float _jerkLagDist = 1000f, _jerkLagSpeed;
        // counters for CalcTotalCostToState (:733)
        int _copsDestroyed, _heliSpawns, _roadblocksDeployed, _copsDamaged, _trafficHit, _spikesDeployed, _copCarsDeployed, _supportDeployed, _property, _propertyCount;
        // ground support (GroundSupportRequest)
        enum SupportStatus { NotActive, Pending, Active }
        SupportStatus _support;
        (int strategy, int chance, float duration, int chanceBigSuv)? _heavy;
        (int strategy, int chance, float duration, int priorityChance, float priorityTime)? _leader;
        float _supportTimer, _heavyDelay;
        bool _heavyGathered;   // GetHeavySupportVehicles has filled the request's vehicle list
        readonly List<CopCar> _supportCars = new List<CopCar>();

        static readonly string[] Chatter =
        {
            "AnytimeEvents_DispPursuitUpdate", "Setup_DispGoAhead", "Backup_DispHeliBUETA", "AnytimeEvents_Spotted",
            "Setup_SpotterReply", "AnytimeEvents_Disp911CsPnt", "Backup_BUReminder", "Outcome_AnticipateFail",
        };
        const float ManagerDt = 1f / 30f, PursuitDt = 1f / 15f;
        internal const int MaxActiveCopCars = 8;
        const float SpawnMin = 150f, SpawnMax = 400f;

        // ======================================================================================================
        // frame update
        // ======================================================================================================

        void Update()
        {
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (NisPlayer.Active) return;
            // races: RaceManager.Begin calls ResetCopsForRestart (the exe's Game.InitRacers); the manager keeps running during
            // the race with the race table rows (AICopManager runs in every play mode)
            // VehicleSpawningEnabled false (cops disabled): every cop goes
            if (!Enabled || !player) { if (InPursuit || _cops.Count > 0) Clear(true); return; }
            if (Player != player) { Player = player; _playerBody = player.GetComponent<Rigidbody>(); PlayerPotentials = new Potentials(player.Spec); }
            var p = player.transform.position;
            // a teleport or respawn (a jump of tens of metres) is not driving: give it a moment before cops react
            if ((p - _lastPlayerPos).sqrMagnitude > 30f * 30f) _graceUntil = Time.time + 3f;
            _lastPlayerPos = p;
            if (_watched != player) Watch(player);

            float dt = Time.deltaTime;
            PerpTick(dt);
            _managerAcc += dt;
            int guard = 0;
            while (_managerAcc >= ManagerDt && guard++ < 4) { _managerAcc -= ManagerDt; ManagerTick(ManagerDt); }
            if (InPursuit)
            {
                _pursuitAcc += dt;
                guard = 0;
                while (_pursuitAcc >= PursuitDt && guard++ < 4 && InPursuit) { _pursuitAcc -= PursuitDt; PursuitTick(PursuitDt); }
                // the busted meter integrates every sim tick (the rate-1.0 task)
                if (InPursuit)
                {
                    Busting = Mathf.Max(0, Busting + _bustRate * dt);
                    if (Busting > 2f && !_bullhornSaid) { _bullhornSaid = true; Radio.Say("HeliSpecific_HeliBullhornArrest"); }
                    if (Busting <= 0) _bullhornSaid = false;
                    if (Busting > 5f && !_busted) SetBusted();
                    if (Busting > 0) BarValue = Mathf.Max(BarValue, 0.5f + 0.5f * Mathf.Min(1f, Busting / 5f));
                }
            }
            // the AI racers' pursuits (their own AIPursuit tasks at 0.25)
            if (_racerPursuits.Count > 0)
            {
                _racerAcc += dt;
                guard = 0;
                while (_racerAcc >= PursuitDt && guard++ < 4)
                {
                    _racerAcc -= PursuitDt;
                    for (int i = 0; i < _racerPursuits.Count; i++) _racerPursuits[i].Tick(PursuitDt);
                }
            }
            else _racerAcc = 0f;
            foreach (var c in _cops) if (c) Flash(c);
        }

        // ======================================================================================================
        // AICopManager (rate 0.5): patrols, pursuit requests, spawns, roadblocks, despawns
        // ======================================================================================================

        void ManagerTick(float dt)
        {
            _cops.RemoveAll(c => !c || !c.Car);
            _pursuers.RemoveAll(c => !c || !c.Car);
            if (Time.frameCount % 30 == 0) RefreshAvoidables();
            ApplyBreakerZones();
            UpdatePatrols(dt);
            if (InPursuit && !_busted) UpdatePursuitSpawns();
            UpdateRacerPursuits();
            UpdateRoadBlocks();
            if (_heli && _heli.Finished) RemoveHeli();
        }

        void RefreshAvoidables()
        {
            Avoidables.Clear();
            foreach (var pc in FindObjectsByType<PlayerCar>(FindObjectsSortMode.None))
                if (Player && (pc.transform.position - Player.transform.position).sqrMagnitude < 400f * 400f) Avoidables.Add(pc);
        }

        /// <summary>
        /// UpdatePatrols (AICopManager.cpp:1330-1437): despawn by the camera rules, keep NumPatrolCars patrols, and turn a
        /// cop's pursuit request into a pursuit.
        /// </summary>
        void UpdatePatrols(float dt)
        {
            var cam = Camera.main;
            bool locked = Time.time < _lockoutUntil;
            for (int i = _cops.Count - 1; i >= 0; i--)
            {
                var c = _cops[i];
                var pos = c.transform.position;
                bool inView = InView(cam, pos);
                c.OffscreenTime = inView ? 0 : c.OffscreenTime + dt;
                if (locked) { RemoveCop(c, 6); continue; }
                float dcam = cam ? Vector3.Distance(pos, cam.transform.position) : Vector3.Distance(pos, Player.transform.position);
                float dot = cam ? Vector3.Dot((pos - cam.transform.position).normalized, cam.transform.forward) : 0;
                bool respawnAvailable = dcam > SpawnMin && (dcam > 275f || (cam && dot >= -0.707f && dot <= 0.8f));
                if (!respawnAvailable) c.SawFlag = true;
                float age = Time.time - c.SpawnTime;
                bool canRespawn = (c.SawFlag && age > 8f) || (respawnAvailable && age > 10f);
                if (c.Destroyed) { if (canRespawn && c.OffscreenTime > 3f) RemoveCop(c, 6); continue; }
                if (!canRespawn || !respawnAvailable) continue;
                if (c.InPursuitList)
                {
                    // 75 m ahead of its pursuit's target (:1395-1408)
                    var tgt = c.Pursuit != null && c.Pursuit.Target ? c.Pursuit.Target : Player;
                    var tv = c.Pursuit != null ? c.Pursuit.TargetVelocity : PlayerVelocity;
                    var keep = tgt.transform.position + RoadGraph.FlatDir(tv.sqrMagnitude > 1f ? tv : tgt.transform.forward) * 75f;
                    if (Vector3.Distance(pos, keep) < 375f) continue;
                }
                RemoveCop(c, 6);
            }
            if (_heli && !InPursuit && !_heli.Exiting) _heli.Exit();

            if (_pursuitRequest)
            {
                // UpdatePursuits (:1869-1967): the requesting cop joins its target's pursuit, or a new AIPursuit is created for it
                var req = _pursuitRequest;
                var tgt = _pursuitRequestTarget;
                _pursuitRequest = null;
                _pursuitRequestTarget = null;
                if (req && !req.Destroyed && CanStartPursuit)
                {
                    if (!tgt || tgt == Player)
                    {
                        if (!InPursuit) StartPursuit(null);
                        AddVehicle(req);
                    }
                    else StartRacerPursuit(tgt, req);
                }
            }
            if (locked || !CanStartPursuit) return;
            // NumPatrolCars of the global (player's) row (1 at heat 1-8); 0 with two or more pursuits, with one pursuit only
            // when the race has opponents (:1330-1370); one spawn per tick when short
            int pursuits = (InPursuit ? 1 : 0) + _racerPursuits.Count;
            int want = GlobalRow.NumPatrolCars;
            if (pursuits > 1) want = 0;
            else if (pursuits == 1) want = RaceManager.Active != null && RaceManager.Active.Entrants.Any(e => e.AI) ? 1 : 0;
            int have = _cops.Count(c => c.Goal == CopGoal.Patrol && !c.Destroyed);
            if (have < want && ActiveCopCars < MaxActiveCopCars && PatrolSpawnLocation(out var sp, out var sf))
            {
                var cop = SpawnCop(RandomCopType(GlobalRow), sp, sf);
                if (cop) cop.SetGoal(CopGoal.Patrol);
            }
        }

        static bool InView(Camera cam, Vector3 pos)
        {
            if (!cam) return false;
            var vp = cam.WorldToViewportPoint(pos);
            return vp.z > 0 && vp.x > -0.05f && vp.x < 1.05f && vp.y > -0.05f && vp.y < 1.05f;
        }

        internal int ActiveCopCars => _cops.Count;

        /// <summary>A cop asks to pursue the player (PursuitRequest): handled on the next manager tick.</summary>
        public void PursuitRequest(CopCar cop) => PursuitRequest(cop, Player);

        /// <summary>AIVehiclePursuit::PursuitRequest (AIVehiclePursuit.cpp:140): a cop out of a pursuit with a target acquired;
        /// the manager keeps the first request of its tick (mPursuitRequestVehicle, AICopManager.cpp:1431).</summary>
        public void PursuitRequest(CopCar cop, PlayerCar target)
        {
            if (_pursuitRequest) return;
            _pursuitRequest = cop;
            _pursuitRequestTarget = target;
        }

        /// <summary>UpdatePursuits (:1869-1967): support units, a cop of the requested type, the latched roadblock.</summary>
        void UpdatePursuitSpawns()
        {
            UpdateSupportCops();
            if (ActiveCopCars < MaxActiveCopCars && !_latchedRoadblock)
            {
                string want = CopRequest();
                if (want == "copheli") { if (!_heli) SpawnHeli(); }
                else if (want != null) SpawnPursuitCar(want);
            }
            if (!_latchedRoadblock && RequestRoadBlock() > 0) _latchedRoadblock = true;
            if (_latchedRoadblock && _roadblock == null)
            {
                if (CreateRoadBlock(null)) { _latchedRoadblock = false; Radio.Say(Random.value < 0.5f ? "ExtraCops_RBPosition" : "ExtraCops_RBWarning"); }
            }
            else if (_roadblock != null) _latchedRoadblock = false;
        }

        /// <summary>UpdateRoadBlocks (:1970-2012): dodged once the perp crosses the centre plane; released with no cars left.</summary>
        void UpdateRoadBlocks()
        {
            for (int i = _roadblocks.Count - 1; i >= 0; i--)
            {
                var rb = _roadblocks[i];
                rb.Cars.RemoveAll(c => !c);
                if (rb.NumCops == 0)
                {
                    rb.ReleaseProps();
                    _roadblocks.RemoveAt(i);
                    if (_roadblock == rb) _roadblock = null;
                    continue;
                }
                if (!rb.Dodged && InPursuit && Player && rb.PerpPast(Player.transform.position))
                {
                    rb.Dodged = true;
                    Stats.RoadblocksDodged++;
                    Stats.SpikesDodged += rb.SpikeStrips;
                    Radio.Say("ExtraCops_ExtraRBAverted");
                }
            }
        }

        // ---- spawning (AISpawnManager / SpawnPursuitIVehicle; ai_cops.md §6.2) ----

        /// <summary>CheckSpawnPosition (AISpawnManager.cpp:137-195): 150-400 m from the camera, outside the
        /// TRACK_PACH_ZONE_NO_COP_SPAWN zones, and no car within the clearance.</summary>
        bool CheckSpawnPosition(Vector3 pos, bool checkDistance, float clearance)
        {
            var cam = Camera.main;
            var c = cam ? cam.transform.position : Player.transform.position;
            float d = new Vector2(pos.x - c.x, pos.z - c.z).magnitude;
            if (checkDistance && (d < SpawnMin || d > SpawnMax)) return false;
            if (PoliceZones.First(TrackPathZones.NoCopSpawn, pos) != null) return false;
            foreach (var car in Avoidables)
                if (car && (car.transform.position - pos).sqrMagnitude < clearance * clearance) return false;
            foreach (var cop in _cops)
                if (cop && (cop.transform.position - pos).sqrMagnitude < clearance * clearance) return false;
            return true;
        }

        /// <summary>
        /// GetSpawnLocation (AISpawnManager.cpp:96): a random segment within 450 m of camera + v·1 s (no no-traffic,
        /// decision or short segments), a traffic lane at a random point along it.
        /// </summary>
        bool PatrolSpawnLocation(out Vector3 pos, out Vector3 fwd)
        {
            pos = fwd = Vector3.zero;
            var cam = Camera.main;
            var centre = (cam ? cam.transform.position : Player.transform.position) + PlayerVelocity;
            var list = new List<int>();
            foreach (int si in RoadGraph.Near(centre, 450f))
            {
                var s = RoadGraph.Segs[si];
                if (s.Has(RoadNetwork.SegNoTraffic) || s.Has(RoadNetwork.SegDecision) || s.Length < 10f) continue;
                if ((s.Samples[s.Samples.Length / 2] - centre).sqrMagnitude > 450f * 450f) continue;
                list.Add(si);
                if (list.Count >= 50) break;
            }
            for (int tries = 0; tries < 4 && list.Count > 0; tries++)
            {
                var s = RoadGraph.Segs[list[NfsMw.Core.SimRandom.RandIntRange(list.Count)]];   // GetSpawnLocation: SimRandom_IntRange(mNumSpawnSegments)
                bool dirFwd = !s.Has(RoadNetwork.SegOneWay) ? Random.value < 0.5f : true;
                float t = NfsMw.Core.SimRandom.RandFloat();   // GetSpawnPointOnSegment: timeStep = SimRandom_Float()
                var nav = new RoadNav { DecisionFilter = false, TrafficFilter = true };
                nav.SetLaneType(RoadGraph.Lane.Traffic);
                var p0 = RoadGraph.Point(s, t);
                var tan = RoadGraph.Tangent(s, t) * (dirFwd ? 1f : -1f);
                if (!nav.InitAtPoint(p0 + RoadGraph.RightOf(tan) * 3f, tan, false, 1f)) continue;
                if (!CheckSpawnPosition(nav.Position, true, 15f)) continue;
                pos = nav.Position;
                fwd = nav.Forward;
                return true;
            }
            return false;
        }

        /// <summary>
        /// SpawnPursuitIVehicle (:855-989), free roam: from the 7.8 s seek-ahead point S, a nav turned ±60° off the
        /// S→target line, advanced |S − target| and reversed (a side road onto your predicted path); else a random
        /// road point in a 190 m circle 200 m ahead. Cooldown uses the patrol rule.
        /// </summary>
        bool PursuitSpawnLocation(out Vector3 pos, out Vector3 fwd) =>
            PursuitSpawnLocation(Player.transform.position, PlayerVelocity, Player.transform.forward, SeekAhead, InCooldown, out pos, out fwd);

        /// <summary>SpawnPursuitIVehicle for any pursuit target (its position, velocity, heading and seek-ahead point).</summary>
        internal bool PursuitSpawnLocation(Vector3 tp, Vector3 tv, Vector3 tfwd, Vector3 s, bool cooldown, out Vector3 pos, out Vector3 fwd)
        {
            pos = fwd = Vector3.zero;
            if (cooldown) return PatrolSpawnLocation(out pos, out fwd);
            var toTarget = tp - s; toTarget.y = 0;
            if (toTarget.sqrMagnitude > 1f)
            {
                var dir = PoliceData.RotateInXZ(NfsMw.Core.SimRandom.RandFloat() > 0.5f ? -1f / 6f : 1f / 6f, toTarget.normalized);   // SimRandom_Float() > 0.5 → −60°
                var nav = new RoadNav { DecisionFilter = false };
                nav.SetLaneType(RoadGraph.Lane.Cop);
                if (nav.InitAtPoint(s, dir, false, 1f))
                {
                    nav.IncNavPosition(toTarget.magnitude, 0f);
                    if (!nav.DeadEnd && CheckSpawnPosition(nav.Position, true, 40f))
                    {
                        nav.Reverse();
                        pos = nav.Position;
                        fwd = nav.Forward;
                        return true;
                    }
                }
            }
            var ahead = tp + RoadGraph.FlatDir(tv.sqrMagnitude > 1f ? tv : tfwd) * 200f;
            for (int i = 0; i < 3; i++)
            {
                var r = RandPointInCircle() * 190f;   // rand_point_in_circle (0x406340) · kPursuitCarSpawnRadius
                var nav = new RoadNav { DecisionFilter = false };
                nav.SetLaneType(RoadGraph.Lane.Cop);
                var at = ahead + new Vector3(r.x, 0, r.y);
                if (!nav.InitAtPoint(at, tp - at, false, 1f)) continue;
                if (!CheckSpawnPosition(nav.Position, true, 40f)) continue;
                pos = nav.Position;
                fwd = nav.Forward;
                return true;
            }
            return false;
        }

        /// <summary>GetSpawnPositionAheadOfTarget: along the road from the player (negative = behind), checked.</summary>
        bool SpawnAheadOfTarget(float distance, bool check, out Vector3 pos, out Vector3 fwd)
        {
            pos = fwd = Vector3.zero;
            var nav = new RoadNav { DecisionFilter = false };
            nav.SetLaneType(RoadGraph.Lane.Cop);
            var tf = RoadGraph.FlatDir(PlayerVelocity.sqrMagnitude > 1f ? PlayerVelocity : Player.transform.forward);
            if (!nav.InitAtPoint(Player.transform.position, distance >= 0 ? tf : -tf, false, 1f)) return false;
            nav.IncNavPosition(Mathf.Abs(distance), 0f);
            if (nav.DeadEnd || !CheckSpawnPosition(nav.Position, check, 40f)) return false;
            pos = nav.Position;
            fwd = nav.Forward;
            return true;
        }

        CopCar SpawnPursuitCar(string type)
        {
            if (!PursuitSpawnLocation(out var pos, out var fwd)) return null;
            var cop = SpawnCop(type, pos, fwd);
            if (cop) AddVehicle(cop);
            return cop;
        }

        /// <summary>
        /// PursueAtHeatLevel / SpawnPursuitCar (:800): 340 m ahead along the road (80 m for a free-roam start), else
        /// SpawnCopCarNow 15 m behind the player.
        /// </summary>
        CopCar SpawnForcedPursuitCar(string type, float ahead)
        {
            Vector3 pos, fwd;
            if (!SpawnAheadOfTarget(ahead, false, out pos, out fwd))
            {
                fwd = Player.transform.forward;
                pos = Player.transform.position - fwd * 15f;
            }
            else fwd = -fwd;
            var cop = SpawnCop(type, pos, fwd);
            if (cop) { cop.ZeroTimeSinceTargetSeen(); AddVehicle(cop); }   // AICopManager.cpp:825
            return cop;
        }

        /// <summary>GetRandomValidCopCar: a non-heli type of the row, weighted by its count.</summary>
        static string RandomCopType(HeatRow row)
        {
            int total = row.Cops.Where(c => c.type != "copheli").Sum(c => c.count);
            int r = NfsMw.Core.SimRandom.RandIntRange(total);   // GetRandomValidCopCar: SimRandom_IntRange(totalRequested)
            foreach (var c in row.Cops)
            {
                if (c.type == "copheli") continue;
                r -= c.count;
                if (r < 0) return c.type;
            }
            return "copmidsize";
        }

        /// <summary>Builds a cop car (DRIVER_COP) on the road at a position and heading.</summary>
        CopCar SpawnCop(string typeName, Vector3 pos, Vector3 fwd)
        {
            var type = PoliceData.Type(typeName);
            if (!System.IO.Directory.Exists(System.IO.Path.Combine(GameData.Root, "CARS", type.Model))) type = PoliceData.Type("copmidsize");
            PlayerCar pc;
            var rot = Quaternion.LookRotation(RoadGraph.FlatDir(fwd), Vector3.up);
            try { pc = CarSpawner.Spawn(type.Model, Color.white, new OwnedCar { Engine = Heat >= 3 ? 2 : 1, Tires = 2 }, pos, rot, "Cop " + type.Model); }
            catch (System.Exception e) { Debug.LogWarning("NFSMW police: " + type.Model + ": " + e.Message); return null; }
            var cop = pc.gameObject.AddComponent<CopCar>();
            cop.Init(pc, type, this);
            Lights(cop);
            var meter = pc.gameObject.AddComponent<CollisionMeter>();
            meter.OnContact = (impulse, c) => CopHit(cop, c);
            _cops.Add(cop);
            return cop;
        }

        void Lights(CopCar cop)
        {
            var pc = cop.Car;
            cop.RedLight = MakeLight(pc.transform, new Vector3(-0.4f, 1.55f, 0), Color.red, "Light Bar Red");
            cop.BlueLight = MakeLight(pc.transform, new Vector3(0.4f, 1.55f, 0), new Color(0.2f, 0.4f, 1f), "Light Bar Blue");
            var sirenGo = new GameObject("Siren");
            sirenGo.transform.SetParent(pc.transform, false);
            var siren = sirenGo.AddComponent<AudioSource>();
            siren.clip = SoundLibrary.Clip("IG_GLOBAL/SIREN_MB.abk", 2);
            siren.loop = true;
            siren.spatialBlend = 1f;
            siren.minDistance = 10f;
            siren.maxDistance = 250f;
            siren.volume = 0;
            if (siren.clip) siren.Play();
            cop.SirenSource = siren;
        }

        static Light MakeLight(Transform parent, Vector3 local, Color c, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = c;
            l.range = 14f;
            l.intensity = 0;
            return l;
        }

        void Flash(CopCar c)
        {
            bool on = !c.Destroyed && (c.InPursuit || c.Roadblock != null);
            float t = Time.time * 7f;
            if (c.RedLight) c.RedLight.intensity = on && Mathf.Sin(t) > 0 ? 6f : 0;
            if (c.BlueLight) c.BlueLight.intensity = on && Mathf.Sin(t) <= 0 ? 6f : 0;
            if (c.SirenSource) c.SirenSource.volume = on && c.SirenOn ? 0.8f : 0f;
            if (c.TryGetComponent<CarFlares>(out var flares)) flares.CopLightsOn = on;
        }

        /// <summary>UnSpawn: leave the pursuit or roadblock and go; MUnspawnCop(reason) tells the speech AI first.</summary>
        void RemoveCop(CopCar c, int reason)
        {
            try { CopUnspawned?.Invoke(c, reason); } catch (System.Exception e) { Debug.LogException(e); }
            if (c.Pursuit is RacerPursuit rp) rp.RemoveVehicle(c);
            else if (c.InPursuitList) RemoveVehicle(c);
            if (c.Roadblock != null) { c.Roadblock.Cars.Remove(c); c.Roadblock = null; }
            _supportCars.Remove(c);
            _lastContact.Remove(c);
            _cops.Remove(c);
            if (c) Destroy(c.gameObject);
        }

        /// <summary>
        /// GetActiveCopVehicleFromOutOfView (:539): the active cop off-screen for over 5 s and farthest from view (over
        /// 100 m) is unspawned so its slot can be reused.
        /// </summary>
        bool StealOutOfViewCop(int reason = 5)
        {
            var cam = Camera.main;
            CopCar best = null;
            float bestD = 100f;
            foreach (var c in _cops)
            {
                if (c.OffscreenTime <= 5f || c.Roadblock != null) continue;
                float d = cam ? Vector3.Distance(c.transform.position, cam.transform.position) : 0;
                if (d > bestD) { bestD = d; best = c; }
            }
            if (!best) return false;
            RemoveCop(best, reason);
            return true;
        }

        // ---- helicopter (SpawnPursuitHelicopter, :1035-1077) ----

        void SpawnHeli()
        {
            var tp = Player.transform.position;
            var nav = new RoadNav { DecisionFilter = false };
            Vector3 pos = tp, fwd = -Player.transform.forward;
            if (nav.InitAtPoint(tp, -Player.transform.forward, false, 0f))
            {
                nav.IncNavPosition(250f, 0f);
                pos = nav.Position;
                fwd = nav.Forward;
            }
            _heli = CopHeli.Create(this, pos + Vector3.up * 20f, fwd, Row.HeliFuelTime);
            _forceHeliNext = false;
            _heliSpawns++;
            _totalCops++;
            CopsInvolved = _totalCops;
            Stats.CopsInvolved = CopsInvolved;
            SetSpawnCopTimer();
            Radio.Say("Backup_DispHeliBUETA");
            Debug.Log("NFSMW police: helicopter deployed");
        }

        void RemoveHeli()
        {
            if (_heli) Destroy(_heli.gameObject);
            _heli = null;
            _spawnHeliTimer = Row.TimeBetweenHeliActive;
        }

        // ======================================================================================================
        // AIPursuit
        // ======================================================================================================

        /// <summary>A new pursuit (the AIPursuit ctor + OnAttached of the perp).</summary>
        void StartPursuit(int? forceHeat)
        {
            InPursuit = true;
            _pursuitTime = 0;
            var car = GameSession.ActiveCar;
            Heat = forceHeat ?? Mathf.Clamp(car?.Heat ?? 1, 1, MaxHeat);
            // the AIPursuit ctor (:629-669): quick race base 1 / max 5, career the bin's base / max heat [port: MaxHeat]; a
            // race that uses the world heat forces ForceHeatLevel .. MaxRaceHeatLevel, else the race's MaxHeatLevel caps it
            var ev = RaceManager.Active ? RaceManager.Active.Event : null;
            _baseHeat = 1f;
            _maxHeatNow = MaxHeat;
            if (Challenge == null && (GameSession.Mode == GameMode.Career || GameSession.Mode == GameMode.QuickRace))
            {
                // the AIPursuit ctor's base / maximum heat (PursuitSystem.Script.cs AIPursuitHeatRange; career: the bin's open-world heat)
                var hr = AIPursuitHeatRange(ev, GameSession.Mode == GameMode.Career, GameSession.Mode == GameMode.QuickRace, false);
                _baseHeat = Mathf.Max(1f, hr.baseHeat); _maxHeatNow = Mathf.Max(_baseHeat, hr.maxHeat);
                Heat = Mathf.Clamp(Mathf.Max(Heat, (int)_baseHeat), 1, Mathf.Max(MaxHeat, (int)_maxHeatNow));
            }
            HeatValue = Heat;
            Bounty = 0;
            CopsDisabled = 0;
            _lostTime = 0; _stateTime = 0; _hideBonus = 0; _combo = 0; _comboTimer = 0;
            _busted = _bailed = false;
            InCooldown = false;
            CooldownProgress = 0;
            Busting = 0;
            Stats = new PursuitStats();
            _pursuers.Clear();
            _totalCops = 0;
            CopsInvolved = 0;
            _roadBlockTimer = 0;
            _spawnCopTimer = 0;
            _spawnHeliTimer = 10f;
            _supportCheckTimer = 10f;
            _fs.Reset(Row);
            _jerkLagPos = Player.transform.position;
            _jerkLagDist = 1000f;
            _jerkLagSpeed = 0;
            IsJerk = false;
            _nextRoadblockRequest = _latchedRoadblock = _forceHeliNext = _testHeliSearch = false;
            _supportPriorityDone = _crossSpawned = _crossDestroyed = false;
            _support = SupportStatus.NotActive;
            _supportCars.Clear();
            _copsDestroyed = _heliSpawns = _roadblocksDeployed = _copsDamaged = _trafficHit = _spikesDeployed = _copCarsDeployed = _supportDeployed = _property = _propertyCount = 0;
            _backupRequested = false;
            LockInPursuitAttribs();
            LastKnownLocation = Player.transform.position;
            _setupSpeechTime = Time.time;
            _nextChatter = Time.time + 8f;
            if (!Radio.Say("Setup_InitPursuit")) Radio.Say("AnytimeEvents_Spotted");
            if (GameSession.Profile != null) GameSession.Profile.Pursuits++;
        }

        /// <summary>LockInPursuitAttribs (:723): the wave size, backup trigger and cooldown length.</summary>
        void LockInPursuitAttribs()
        {
            var row = Row;
            _required = row.FullEngagementCopCount;
            _backupAt = row.NumCopsToTriggerBackup;
            _coolDownRequired = row.EvadeTimeout;
            _evaded = 0;
        }

        /// <summary>AIPursuit::OnAttached for a cop: spawn timers, counts, StartPursuit / the support goal.</summary>
        void AddVehicle(CopCar cop)
        {
            if (cop.InPursuitList) return;
            cop.InPursuitList = true;
            _pursuers.Add(cop);
            SetSpawnCopTimer();
            _totalCops++;
            CopsInvolved = _totalCops;
            Stats.CopsInvolved = CopsInvolved;
            if (cop.Type.IsSpecial || cop.Type.Name == "copsporthench" && cop.IsSupport)
            {
                _supportDeployed++;
                if (cop.Type.Name == "copcross") _crossSpawned = true;
            }
            else _copCarsDeployed++;
            cop.StartPursuit(PlayerPursuit);
            if (cop.IsSupport) cop.StartSupportGoal();
        }

        void SetSpawnCopTimer()
        {
            var row = Row;
            if (_totalCops < 3 && !InCooldown) _spawnCopTimer = row.TimeBetweenFirstFourSpawn;
            else
            {
                _spawnCopTimer = row.TimeBetweenCopSpawn;
                if (_needed > 2)
                {
                    if (_fastSpawnNext) { _fastSpawnNext = false; _spawnCopTimer = 0.2f; }
                    else _fastSpawnNext = true;
                }
            }
        }

        /// <summary>AIPursuit::OnDetached for a cop: engaged cops that leave count as evaded.</summary>
        void RemoveVehicle(CopCar cop)
        {
            if (!cop.InPursuitList) return;
            cop.InPursuitList = false;
            _pursuers.Remove(cop);
            if (cop.WithinEngagementRadius && !cop.IsSupport && AllowStatsToAccumulate) _evaded++;
            if (cop.Type.Name == "copcross" && cop.Destroyed) _crossDestroyed = true;
            if (cop.IsSupport && _supportCars.Contains(cop))
            {
                _supportCars.Remove(cop);
                if (_supportCars.Count == 0) ResetSupport();
            }
            cop.EndPursuit();
        }

        int _needed;

        void PursuitTick(float dt)
        {
            var player = Player;
            var p = player.transform.position;
            var row = Row;
            float speed = player.SpeedMps;
            Roads.Update(p, PlayerVelocity, player.transform.forward, Time.time);

            _roadBlockTimer -= dt;
            _fs.ActiveTime -= dt;
            _spawnCopTimer -= dt;
            _spawnHeliTimer -= dt;
            _supportCheckTimer -= dt;
            _comboTimer -= dt;
            _stateTime += dt;
            _heavyDelay -= dt;
            if (_support != SupportStatus.NotActive) { _supportTimer -= dt; if (_supportTimer < 0) ResetSupport(); }
            float before = _pursuitTime;
            if (!InCooldown && AllowStatsToAccumulate) _pursuitTime += dt;   // mTotalPursuitTime (:1649-1652)

            // formation pick (:1725-1774): every other pick is STAGGER_FOLLOW; with an intact roadblock ahead, FOLLOW
            _fs.Pick(row, _roadblock, _busted, _bailed, SpeechFinished);

            RemoveUnwantedVehicles();

            // line of sight, distances, engagement (:1776-1847)
            _lostTime += dt;
            float min3 = float.MaxValue, minxz = float.MaxValue;
            int engaged = 0;
            foreach (var c in _pursuers)
            {
                if (c.Destroyed) continue;
                _lostTime = Mathf.Min(_lostTime, c.TimeSinceSeen);
                var cp = c.transform.position;
                float dy = Mathf.Abs(cp.y - p.y), d3 = Vector3.Distance(cp, p), dxz = new Vector2(cp.x - p.x, cp.z - p.z).magnitude;
                if (dy < 1.5f && dxz < minxz) minxz = dxz;
                if (d3 < min3) min3 = d3;
                if (d3 < row.FullEngagementRadius)
                {
                    if (!c.IsSupport) engaged++;
                    if (!InCooldown) c.SetWithinEngagementRadius();
                }
            }
            if (_heli && !_heli.Exiting)
            {
                _lostTime = Mathf.Min(_lostTime, _heli.TimeSinceSeen);
                min3 = Mathf.Min(min3, Vector3.Distance(_heli.transform.position, p));
            }
            int remaining = _required - _evaded;
            if (remaining < 1) { remaining++; _required++; }
            if (engaged - remaining > 0) _required += engaged - remaining;
            if (_required != 0 && !_backupRequested && !InCooldown && remaining <= _backupAt)
            {
                _backupRequested = true;
                _backupTimer = row.BackupCallTimer;
            }
            // the roadblock: its cops see 60 m; the nearest joins when the perp loiters ("cheating")
            if (_roadblock != null)
            {
                // a perp hidden from cars is not measured (or seen) by the roadblock (AIPursuit.cpp:1853-1872)
                if (!PerpHiddenFromCars)
                {
                    float rb3 = _roadblock.MinDistanceToTarget(p, dt, InCooldown, out float rbxz, out _rbNearest);
                    if (rb3 < min3) { min3 = rb3; if (rb3 < 60f) { PerpInSight = true; _lostTime = 0; } }
                    if (rbxz < minxz) minxz = rbxz;
                }
                var nearest = _rbNearest;
                if (_roadblock.PerpCheating && _rbCopsAdded == 0 && nearest && _roadblock.Cars.Contains(nearest))
                {
                    _roadblock.Cars.Remove(nearest);
                    nearest.Roadblock = null;
                    AddVehicle(nearest);
                    _required++;
                    _rbCopsAdded++;
                }
            }
            _minDistance3 = min3;
            _minDistanceXZ = minxz;

            // busted meter rate (applied per frame): +1/s stopped within 15 m of a level cop while seen, −2/s otherwise
            float bustMps = row.BustSpeedKmh / 3.6f;
            if (_busted) _bustRate = 1f;
            else if (PerpInSight && !_bailed)
            {
                _bustRate = speed < bustMps && minxz < 15f ? 1f : -2f;
                // the bounty tick: whenever floor(time / 10) changes, while seen
                if ((int)(before * 0.1f) != (int)(_pursuitTime * 0.1f)) Bounty += row.BountyPer10s;
                if (_backupRequested)
                {
                    _backupTimer -= dt;
                    if (_backupTimer < 0) { _backupRequested = false; LockInPursuitAttribs(); Radio.Say("Backup_BUReminder"); }
                }
            }
            else _bustRate = -2f;

            PerpInSight = _lostTime < 7f;
            // mHiddenZoneTime (AIPursuit.cpp:2003-2024): hidden and unseen, the cooldown clock runs (1 + multiplier)× faster
            if ((PerpHiddenFromCars || PerpHiddenFromHelicopters) && !PerpInSight) _hideBonus += dt * row.HiddenZoneTimeMultiplier;
            if (PerpInSight) { _hideBonus = 0; LastKnownLocation = p; }

            // heat (:1660-1699): outside cooldown, dT / (TimePerHeatLevel x ScaleEscalationPerBucket[min(bin, 14)]) of the
            // current row, clamped to the pursuit's base .. maximum heat; a new level re-picks the formation and re-arms
            // the support priority check
            if (!InCooldown)
            {
                int bin = GameSession.Mode == GameMode.Career && Challenge == null ? GameSession.Profile?.Bin ?? 14 : 14;
                HeatValue = Mathf.Clamp(HeatValue + dt / row.SecondsPerHeat(Mathf.Min(bin, 14)), Mathf.Min(_baseHeat, HeatValue), Mathf.Max(_maxHeatNow, _baseHeat));
                if ((int)HeatValue != Heat) { Heat = (int)HeatValue; _fs.ActiveTime = 0f; _supportPriorityDone = false; Radio.Say("Backup_BUReminder"); }
            }
            if (Time.time > _nextChatter && !Radio.Busy)
            {
                _nextChatter = Time.time + Random.Range(12f, 22f);
                Radio.Say(Chatter[Random.Range(0, Chatter.Length)]);
            }
            Stats.Length = _pursuitTime;
            Stats.Bounty = Bounty;
            if (speed > 33f) Stats.Infraction("Speeding");

            // cooldown after 7 s unseen; evaded after evadetimeout s more (hiding spots speed it up)
            float elapsed = _lostTime + _hideBonus;
            if (_lostTime > 7f)
            {
                BarValue = -1;
                if (!InCooldown)
                {
                    CooldownProgress = 0;
                    if (_stateTime > 2.5f)
                    {
                        InCooldown = true;
                        _stateTime = 0;
                        _spawnCopTimer = Mathf.Min(_spawnCopTimer, row.TimeBetweenCopSpawn);
                        _backupTimer = 0;
                        _testHeliSearch = true;
                        Stats.Infraction("Resisting Arrest");
                        if (!_lostSightSaid) { _lostSightSaid = true; Radio.Say(Random.value < 0.5f ? "HeliSpecific_HeliLostVisual" : "Cross_CrossPursuitEsc"); }
                    }
                }
                else
                {
                    CooldownProgress = Mathf.Max(0.05f, elapsed / _coolDownRequired);
                    if (CooldownProgress >= 1f) { Evaded(); return; }
                }
            }
            else
            {
                if (InCooldown)
                {
                    CooldownProgress = Mathf.Max(0.05f, CooldownProgress * 0.93f);
                    if (_stateTime > 2.5f) { InCooldown = false; _stateTime = 0; _lostSightSaid = false; }
                }
                else CooldownProgress = 0;   // mEvadeLevel = 0 once the cooldown meter is gone (AIPursuit.cpp:2096-2110)
                // the HUD meter (display only)
                if (_lostTime > 0.29f) BarValue = Mathf.Clamp(-0.5f - _lostTime / 14f, -1f, -0.5f);
                else if (min3 > 50f) BarValue = Mathf.Clamp(-0.1f - 0.4f * (min3 - 50f) / (row.FrontLOS - 50f), -0.5f, -0.1f);
                else if (min3 <= 35f && speed < 31.29f)
                    BarValue = 0.1f + 0.4f * (0.7f * Mathf.Clamp01((27.78f - speed) / (27.78f - bustMps)) + 0.3f * Mathf.Clamp01((35f - min3) / 20f));
                else BarValue = 0;
            }
            _meter = BarValue;   // mPursuitMeter before the per-frame busted display
            Cooldown = InCooldown ? CooldownProgress : 0;

            UpdateJerk(p, speed, dt);
            // UpdateFormation (:1340-1537); a collapse sends the heavy support away (:1447-1456)
            if (_fs.Update(player, PlayerVelocity, row, _pursuers, Roads, IsJerk, _busted, _bailed, PerpInSight, dt) && _support == SupportStatus.Active && _heavy != null)
            {
                foreach (var c in _supportCars) if (c) c.StartFlee();
                ResetSupport();
            }
            UpdateCostToState();
            if (_heli && (_busted || _bailed) && !_heli.Exiting) _heli.Exit();
        }

        /// <summary>UpdateJerk (:2824-2853): circling or weaving at speed in a small area.</summary>
        void UpdateJerk(Vector3 pos, float speed, float dt)
        {
            float k = 0.1f * dt;
            _jerkLagPos = Vector3.Lerp(_jerkLagPos, pos, k);
            _jerkLagDist = Mathf.Lerp(_jerkLagDist, Vector3.Distance(pos, _jerkLagPos), k);
            _jerkLagSpeed = Mathf.Lerp(_jerkLagSpeed, speed, k * 0.5f);
            float f = _jerkLagDist > 0.01f ? 10f * _jerkLagSpeed / _jerkLagDist : 0;
            if (f >= 3f) IsJerk = true;
            else if (f <= 1.75f) IsJerk = false;
        }

        void UpdateCostToState()
        {
            Stats.CostToState = 5000 * _copsDestroyed + 2000 * _heliSpawns + 500 * _roadblocksDeployed + 250 * _copsDamaged + 500 * _trafficHit
                + 250 * _spikesDeployed + 250 * _copCarsDeployed + 450 * _supportDeployed + _property;
            Stats.CopCarsDeployed = _copCarsDeployed; Stats.SupportVehiclesDeployed = _supportDeployed; Stats.TrafficCarsHit = _trafficHit; Stats.HeliSpawns = _heliSpawns;
            Stats.RoadblocksDeployed = _roadblocksDeployed; Stats.SpikeStripsDeployed = _spikesDeployed; Stats.PropertiesDamaged = _propertyCount; Stats.PropertyDamageValue = _property;
        }

        bool FinisherActive => _fs.FinisherActive;

        /// <summary>speech_finished (AIPursuit.cpp:1712-1730): 15 s after SoundAI last had focus 1 (the pursuit-setup flow);
        /// always true with the speech AI off or in a pursuit race (GetIsPursuitRace).</summary>
        internal bool SpeechFinished
        {
            get
            {
                var speechAi = CopRadio.AiEnabled ? CopRadio.AI : null;
                if (speechAi != null && speechAi.Focus == NfsMw.Audio.SpeechAI.kPursuitFlow) _setupSpeechTime = Time.time;
                return speechAi == null || IsPursuitRace || Time.time - _setupSpeechTime > 15f;
            }
        }

        // ---- AI-racer pursuits (one AIPursuit per target, AICopManager.cpp:1824-1967) ----

        /// <summary>The pursuit whose target is this car (ICopMgr::GetPursuitActivity for a racer), or null.</summary>
        public RacerPursuit PursuitOf(PlayerCar car)
        {
            foreach (var rp in _racerPursuits) if (rp.Target == car) return rp;
            return null;
        }

        /// <summary>AICopManager::CanPursueRacers (:1783-1800): the race has opponents and no pursuit targets a DRIVER_RACER yet.</summary>
        public bool CanPursueRacers
        {
            get
            {
                var race = RaceManager.Active;
                if (!race || race.Event == null) return false;
                bool any = false;
                foreach (var e in race.Entrants) if (e.AI) { any = true; break; }
                return any && _racerPursuits.Count == 0;
            }
        }

        /// <summary>
        /// AICopManager::IsCopRequestPending (:1760-1781): spawning enabled and a scripted spawn, a latched roadblock, a
        /// pursuit wanting a cop (budget free) or a roadblock request. The traffic manager spawns nothing while it is true.
        /// </summary>
        public bool IsCopRequestPending
        {
            get
            {
                if (!CanStartPursuit) return false;
                if (_latchedRoadblock) return true;
                if (InPursuit && !_busted && !_bailed)
                {
                    if (ActiveCopCars < MaxActiveCopCars && CopRequest() != null) return true;
                    if (_nextRoadblockRequest) return true;
                }
                foreach (var rp in _racerPursuits)
                    if (!rp.ShouldEnd && ActiveCopCars < MaxActiveCopCars && rp.CopRequest() != null) return true;
                return false;
            }
        }

        /// <summary>UpdatePursuits (:1953-1967): a request on a new target creates its AIPursuit; the cop joins it.</summary>
        void StartRacerPursuit(PlayerCar target, CopCar cop)
        {
            var rp = PursuitOf(target);
            if (rp == null)
            {
                var race = RaceManager.Active;
                RaceManager.Entrant e = null;
                if (race) foreach (var x in race.Entrants) if (x.Car == target) e = x;
                var ai = target.GetComponent<AIRacer>();
                if (e == null || !ai || !e.InRace) return;
                rp = new RacerPursuit(this, target, ai, e);
                _racerPursuits.Add(rp);
                Debug.Log($"NFSMW police: pursuit of racer {e.Name} (heat {rp.Heat}, row {rp.Row.Source})");
            }
            rp.AddVehicle(cop);
        }

        /// <summary>UpdatePursuits (:1869-1967) for the racer pursuits: ended ones detach (their cops flee); the rest get the
        /// cop they request while the manager's budget allows and no roadblock request is latched.</summary>
        void UpdateRacerPursuits()
        {
            for (int i = _racerPursuits.Count - 1; i >= 0; i--)
            {
                var rp = _racerPursuits[i];
                if (rp.ShouldEnd)
                {
                    Debug.Log($"NFSMW police: racer pursuit over ({(rp.IsPerpBusted ? "busted" : rp.Bailed ? "bailed" : "evaded")}, {rp.CopsDestroyed} cops destroyed)");
                    rp.End();
                    _racerPursuits.RemoveAt(i);
                    continue;
                }
                if (ActiveCopCars < MaxActiveCopCars && !_latchedRoadblock)
                {
                    string want = rp.CopRequest();
                    if (want != null && want != "copheli") SpawnPursuitCarFor(rp, want);
                }
            }
        }

        /// <summary>SpawnPursuitCarByName -> SpawnPursuitIVehicle (:855-989) for a racer target.</summary>
        bool SpawnPursuitCarFor(RacerPursuit rp, string type)
        {
            var t = rp.Target;
            if (!t) return false;
            if (!PursuitSpawnLocation(t.transform.position, rp.TargetVelocity, t.transform.forward, rp.SeekAhead, rp.InCooldown, out var pos, out var fwd)) return false;
            var cop = SpawnCop(type, pos, fwd);
            if (!cop) return false;
            rp.AddVehicle(cop);
            return true;
        }

        /// <summary>The player's pursuit as the cop cars see it (IPursuit of the local player's AIPerpVehicle).</summary>
        sealed class PlayerView : CopPursuit
        {
            readonly PursuitSystem _p;
            public PlayerView(PursuitSystem p) { _p = p; }
            public override PlayerCar Target => _p.Player;
            public override Vector3 TargetVelocity => _p.PlayerVelocity;
            public override HeatRow Row => _p.Row;
            public override int Heat => _p.Heat;
            public override bool PerpInSight => _p.PerpInSight;
            public override bool IsJerk => _p.IsJerk;
            public override PerpRoads Roads => _p.Roads;
            public override Vector3 SeekAhead => _p.SeekAhead;
            public override Potentials TargetPotentials => _p.PlayerPotentials ?? (_p.Player ? _p.PlayerPotentials = new Potentials(_p.Player.Spec) : null);
            public override bool PursuitMeterCanShowBusted => _p.PursuitMeterCanShowBusted;
            public override bool PerpHiddenFromCars => _p.PerpHiddenFromCars;
            public override bool IsPlayerPursuit => true;
            public override bool InCooldown => _p.InCooldown;
        }

        // ---- formations: PursuitFormationState (UpdateFormation :1340-1537, SetupCollapse :1239-1338) ----

        // ---- wave counts (GetAdjustedCopCounts / CopRequest / RemoveUnwantedVehicles, :2165-2457) ----

        Dictionary<string, int> Contingent()
        {
            var d = new Dictionary<string, int>();
            foreach (var c in _pursuers) d[c.Type.Name] = (d.TryGetValue(c.Type.Name, out int n) ? n : 0) + 1;
            if (_heli) d["copheli"] = (d.TryGetValue("copheli", out int h) ? h : 0) + 1;
            return d;
        }

        List<(string type, int count, int chance)> AdjustedCopCounts()
        {
            var row = Row;
            int max = int.MaxValue - 2, min = 0;
            if (InCooldown) max = min = Mathf.Min(max, row.NumPatrolCars);
            else max = Mathf.Clamp(_required - _evaded, 0, max);
            int nominal = row.Cops.Where(c => c.type != "copheli").Sum(c => c.count);
            int want = Mathf.Clamp(nominal, min, max);
            var list = new List<(string, int, int)>();
            foreach (var c in row.Cops)
            {
                if (c.type == "copheli") { list.Add(c); continue; }
                int adj = nominal > 0 ? (int)(c.count * want / (float)nominal + 0.5f) : 0;
                if (adj == 0) continue;
                list.Add((c.type, adj, c.chance));
                want -= adj;
                nominal -= c.count;
            }
            return list;
        }

        string CopRequest()
        {
            if (_busted || _spawnCopTimer >= 0 || _bailed) return null;
            bool allowHeli = _spawnHeliTimer < 0 && Support.MinimumSupportDelay < _pursuitTime;
            if (allowHeli && _testHeliSearch)
            {
                _testHeliSearch = false;
                if (!PerpInSight && !_heli && NfsMw.Core.SimRandom.RandFloatRange(100f) <= Row.SearchModeHeliSpawnChance) _forceHeliNext = true;
            }
            if (_forceHeliNext) return "copheli";
            var counts = AdjustedCopCounts();
            var have = Contingent();
            var need = new int[counts.Count];
            int total = 0;
            for (int i = 0; i < counts.Count; i++)
            {
                need[i] = Mathf.Max(0, counts[i].count - (have.TryGetValue(counts[i].type, out int n) ? n : 0));
                total += need[i];
            }
            _needed = total;
            if (total == 0) return null;
            var weight = new int[counts.Count];
            int sum = 0;
            for (int i = 0; i < counts.Count; i++)
            {
                weight[i] = counts[i].chance != 0 ? counts[i].chance : 100;
                if (counts[i].type == "copheli" && (!allowHeli || _heli)) weight[i] = 0;
                if (need[i] == 0) weight[i] = 0;
                sum += weight[i];
            }
            if (sum <= 0) return null;
            int r = NfsMw.Core.SimRandom.RandIntRange(sum);   // CopRequest: SimRandom_IntRange(totalWeight)
            for (int i = 0; i < counts.Count; i++)
            {
                r -= weight[i];
                if (r < 0) return counts[i].type;
            }
            return null;
        }

        void RemoveUnwantedVehicles()
        {
            var counts = AdjustedCopCounts();
            int typecount = 0, fleecount = 0;
            string fleetype = null;
            foreach (var kv in Contingent())
            {
                int flee = kv.Value;
                foreach (var c in counts) if (c.type == kv.Key) { flee = kv.Value - c.count; break; }
                if (flee < 1 || kv.Key == "copheli") continue;
                if (NfsMw.Core.SimRandom.RandFloat() >= typecount / (float)(typecount + flee)) { fleetype = kv.Key; fleecount = flee; break; }
                typecount += flee;
            }
            if (fleetype != null) FleeCopOfType(fleetype, Mathf.Min(2, fleecount));
            if (!PerpInSight && _support == SupportStatus.Active && _heavy != null)
            {
                foreach (var c in _supportCars) if (c) c.StartFlee();
                ResetSupport();
            }
        }

        /// <summary>FleeCopOfType: the two farthest of the type flee (+40 m if unseen); a seeing cop only while 3+ see you.</summary>
        void FleeCopOfType(string type, int count)
        {
            CopCar far = null, second = null;
            float d1 = 0, d2 = 0;
            int canSee = 0, fleeing = 0;
            foreach (var c in _pursuers)
            {
                if (c.IsSupport || c.Destroyed) continue;
                if (c.Goal == CopGoal.FleePursuit) { fleeing++; continue; }
                bool sees = c.TimeSinceSeen <= 0;
                if (sees) canSee++;
                if (c.Type.Name != type) continue;
                float d = Vector3.Distance(c.transform.position, Player.transform.position) + (sees ? 0 : 40f);
                if (d > d1 || far == null) { d2 = d1; second = far; d1 = d; far = c; }
                else if (d > d2) { d2 = d; second = c; }
            }
            if (far && count > fleeing)
            {
                bool sees = far.TimeSinceSeen <= 0;
                if (!sees || canSee > 2) { if (sees) canSee--; far.StartFlee(); }
            }
            if (second && count > fleeing + 1)
            {
                bool sees = second.TimeSinceSeen <= 0;
                if (!sees || canSee > 2) second.StartFlee();
            }
        }

        // ---- roadblocks (RequestRoadBlock :2459, CreateRoadBlock AICopManager.cpp:1100-1308) ----

        int RequestRoadBlock()
        {
            if (_busted || _bailed || _roadblock != null || _roadBlockTimer >= 0) return 0;
            if (Support.MinimumSupportDelay > _pursuitTime) return 0;
            var row = Row;
            _roadBlockTimer = NfsMw.Core.SimRandom.RandFloatRange(4f) + 8f;
            int rv = _nextRoadblockRequest ? 4 : 0;
            float prob;
            if (PerpInSight) prob = row.RoadblockProbability;
            else
            {
                float R = row.SearchModeRoadblockRadius, d = Vector3.Distance(LastKnownLocation, Player.transform.position);
                prob = row.SearchModeRoadblockChance * (R - d) / R;
            }
            _nextRoadblockRequest = NfsMw.Core.SimRandom.RandFloatRange(100f) < prob;
            return rv;
        }

        bool CreateRoadBlock(List<string> suvs)
        {
            var player = Player;
            var tp = player.transform.position;
            var nav = new RoadNav { CookieTrail = true, DecisionFilter = false };
            nav.SetLaneType(RoadGraph.Lane.Cop);
            if (!nav.InitAtPoint(tp, player.transform.forward, false, 0f)) return false;
            nav.IncNavPosition(250f, 0f);
            var seg = nav.Segment;
            if (seg == null || nav.DeadEnd || seg.Has(RoadNetwork.SegNoTraffic) || seg.Has(RoadNetwork.SegDecision) || seg.Length < 40f) return false;
            var zones = RoadGraph.ZonesAt(seg, nav.Fwd);
            if (zones == null || zones.Length == 0) return false;
            var left = nav.LeftPosition;
            var right = nav.RightPosition;
            float crW = Vector3.Distance(left, right), width = crW + 1f;
            int idx = Mathf.Min((int)(width * 0.25f), 6);
            int ask = new[] { 2, 2, 3, 3, 4, 4, 5 }[idx] + 1;
            var row = Row;
            int have;
            // inactive cars first, then cops stolen from out of view (the budget is 8 cars); a heavy roadblock brings its SUVs
            int wantCars = suvs?.Count ?? ask;
            have = 0;
            for (int i = 0; i < wantCars; i++)
            {
                if (ActiveCopCars + have < MaxActiveCopCars || StealOutOfViewCop(3)) have++;
                else break;
            }
            if (suvs != null ? have < suvs.Count : have < ask - 1) return false;
            bool spikes = NfsMw.Core.SimRandom.RandFloatRange(100f) < row.RoadblockSpikeChance && !InCooldown;
            var setup = PoliceData.PickRoadblock(width, have, spikes);
            if (setup == null) return false;
            Vector3 edge, across;
            if ((left - tp).sqrMagnitude > (right - tp).sqrMagnitude) { edge = right; across = left - right; }
            else { edge = left; across = right - left; }
            var centre = edge + across * (setup.MinWidth * 0.5f / Mathf.Max(0.01f, crW));
            float xScale = Mathf.Clamp(width / setup.MinWidth, 1f, 1.14f);
            var F = RoadGraph.FlatDir(nav.Forward);
            var Rt = RoadGraph.RightOf(F);
            var rb = new CopRoadblock { Centre = centre, Dir = F, Heavy = suvs != null };
            int carIndex = 0;
            foreach (var e in setup.Elements)
            {
                var pos = centre + Rt * (e.X * xScale) + F * e.Z;
                var facing = PoliceData.RotateInXZ(e.AngleRev, F);
                var ground = GroundAt(pos, out var normal);
                if (e.Type == ElementType.Car)
                {
                    CopCar cop;
                    if (carIndex >= have) continue;
                    cop = SpawnCop(suvs != null ? suvs[carIndex] : RandomCopType(row), ground, facing);
                    carIndex++;
                    if (!cop) continue;
                    cop.Roadblock = rb;
                    cop.SupportGoal = CopGoal.None;
                    cop.SetGoal(CopGoal.StaticRoadBlock);
                    rb.Cars.Add(cop);
                }
                else
                {
                    rb.Props.Add(CopRoadblock.MakeProp(e.Type, ground, facing, normal));
                    if (e.Type == ElementType.Spikes) rb.SpikeStrips++;
                    _spikesDeployed++;   // NotifySpikeStripDeployed is called for barriers too (AICopManager.cpp:1291)
                }
            }
            if (rb.NumCops == 0) { rb.ReleaseProps(); return false; }
            _roadblocks.Add(rb);
            _roadblock = rb;
            _rbCopsAdded = 0;
            if (_fs.Type != PoliceData.FOLLOW && !FinisherActive) _fs.EndCurrent();   // AddRoadBlock -> EndCurrentFormation (:2508-2516)
            _roadblocksDeployed++;
            Debug.Log($"NFSMW police: roadblock {(centre - tp).magnitude:0} m ahead, width {width:0.0}, {rb.NumCops} cars{(rb.SpikeStrips > 0 ? $", {rb.SpikeStrips} spike belt(s)" : "")}");
            return true;
        }

        static Vector3 GroundAt(Vector3 p, out Vector3 normal)
        {
            normal = Vector3.up;
            if (CopCar.WorldRay(p + Vector3.up * 20f, Vector3.down, 60f, out var hit)) { normal = hit.normal.y < 0 ? -hit.normal : hit.normal; return hit.point; }
            return p;
        }

        // ---- ground support (RequestGroundSupport :2534, UpdateSupportCops AICopManager.cpp:1439-1673) ----

        void UpdateSupportCops()
        {
            if (!RequestGroundSupport() || _support != SupportStatus.Pending) return;
            if (_heavy != null)
            {
                var h = _heavy.Value;
                int n = h.strategy == 1 ? 1 : h.strategy == 4 ? 4 : 2;
                var goal = h.strategy == 3 ? CopGoal.HeadOnRam : h.strategy == 4 ? CopGoal.StaticRoadBlock : CopGoal.Pursuit;
                if (h.strategy == 4)
                {
                    // HEAVY_ROADBLOCK: 4 SUVs as a roadblock (CreateRoadBlock with the SUV list)
                    if (_roadblock != null) return;
                    var suvs = new List<string>();
                    for (int i = 0; i < n; i++) suvs.Add(NfsMw.Core.SimRandom.RandIntRange(100) < h.chanceBigSuv ? "copsuv" : "copsuvl");
                    if (CreateRoadBlock(suvs))
                    {
                        _support = SupportStatus.Active;
                        _supportTimer = h.duration;
                        _supportDeployed += n;
                        Radio.Say(Random.value < 0.5f ? "ExtraCops_RBPosition" : "ExtraCops_RBWarning");
                    }
                    return;
                }
                if (ActiveCopCars + n > MaxActiveCopCars) return;
                // GetHeavySupportVehicles (:1444-1492): gathered once; RAM starts kHeavySupportDelayTime (3 s) and says MReqBackup(16)
                if (!_heavyGathered)
                {
                    _heavyGathered = true;
                    if (h.strategy == 3) { _heavyDelay = 3f; SendBackup(16, false); }
                }
                if (_heavyDelay > 0) return;
                // ahead along the road at 400 − 60 − 10k m, facing back toward you
                var spots = new List<(Vector3 pos, Vector3 fwd)>();
                float adjust = 60f;
                for (int i = 0; i < n; i++)
                {
                    bool got = false;
                    for (int j = 0; j < 8 && !got; j++)
                    {
                        float d = SpawnMax - adjust;
                        adjust += 10f;
                        if (SpawnAheadOfTarget(d, true, out var pos, out var fwd)) { spots.Add((pos, -fwd)); got = true; }
                    }
                    if (!got) { SendBackup(16, true); return; }   // StartHeavySupport failed: "ReqDenied"
                }
                foreach (var s in spots)
                {
                    var c = SpawnCop(NfsMw.Core.SimRandom.RandIntRange(100) < h.chanceBigSuv ? "copsuv" : "copsuvl", s.pos, s.fwd);
                    if (!c) continue;
                    c.SupportGoal = goal;
                    c.InPositionOffset = new Vector3(0, 0, -3f);   // AIGoalHeadOnRam
                    _supportCars.Add(c);
                    AddVehicle(c);
                }
                _support = SupportStatus.Active;
                _supportTimer = h.duration;
                Radio.Say("Backup_BUReminder");
            }
            else if (_leader != null)
            {
                var l = _leader.Value;
                string[] names = { "copcross", "copsporthench", "copsporthench" };
                Vector3[] offsets = { new Vector3(0, 0, -20), new Vector3(-4, 0, -24), new Vector3(4, 0, -24) };
                int n = l.strategy == 7 ? 3 : 1;
                if (ActiveCopCars + n > MaxActiveCopCars) return;
                var spots = new List<(Vector3, Vector3)>();
                float adjust = 0;
                for (int i = 0; i < n; i++)
                {
                    bool got = false;
                    for (int j = 0; j < 8 && !got; j++)
                    {
                        float d = -(SpawnMin + adjust);
                        adjust += 10f;
                        if (SpawnAheadOfTarget(d, true, out var pos, out var fwd)) { spots.Add((pos, -fwd)); got = true; }
                    }
                    if (!got) { SendBackup(64, true); return; }   // StartLeaderSupport failed: "ReqDenied"
                }
                for (int i = 0; i < n; i++)
                {
                    var c = SpawnCop(names[i], spots[i].Item1, spots[i].Item2);
                    if (!c) continue;
                    c.SupportGoal = CopGoal.Pursuit;
                    c.PursuitOffset = offsets[i];
                    c.InPositionGoal = CopGoal.None;
                    c.InFormation = true;
                    _supportCars.Add(c);
                    AddVehicle(c);
                }
                _support = SupportStatus.Active;
                _supportTimer = l.duration;
                SendBackup(64, false);
                Radio.Say("Cross_CrossPursuitEsc");
                Debug.Log("NFSMW police: Cross joins the pursuit");
            }
        }

        bool RequestGroundSupport()
        {
            if (_busted || !PerpInSight || _bailed) return false;
            if (_support != SupportStatus.NotActive) return true;
            if (_supportCheckTimer >= 0) return false;
            _supportCheckTimer = 10f;
            var ps = Support;
            if (ps.MinimumSupportDelay > _pursuitTime) return false;
            int r = NfsMw.Core.SimRandom.RandIntRange(100);   // RequestGroundSupport: SimRandom_IntRange(100)
            if (!_supportPriorityDone)
                foreach (var l in ps.Leader)
                    if (l.priorityTime < _pursuitTime)
                    {
                        _supportPriorityDone = true;
                        if (l.priorityChance > r) { _leader = l; _heavy = null; _supportTimer = l.duration; _support = SupportStatus.Pending; break; }
                    }
            if (_support != SupportStatus.Pending)
            {
                r = NfsMw.Core.SimRandom.RandIntRange(100);
                if (_roadblock == null)
                    foreach (var h in ps.Heavy)
                    {
                        r -= h.chance;
                        if (r < 0) { _heavy = h; _leader = null; _supportTimer = h.duration; _support = SupportStatus.Pending; _roadBlockTimer = 15f; _heavyGathered = false; break; }
                    }
                if (r >= 0)
                    foreach (var l in ps.Leader)
                    {
                        r -= l.chance;
                        if (r < 0) { _leader = l; _heavy = null; _supportTimer = l.duration; _support = SupportStatus.Pending; break; }
                    }
            }
            // a leader request needs Cross available (not spawned, not destroyed)
            if (_leader != null && _support == SupportStatus.Pending && (_crossSpawned || _crossDestroyed)) { _support = SupportStatus.NotActive; _leader = null; }
            return r < 0 && _support == SupportStatus.Pending;
        }

        /// <summary>GroundSupportRequest::Reset: support units join the normal contingent (heavy-roadblock SUVs do not).</summary>
        void ResetSupport()
        {
            bool join = !(_support == SupportStatus.Active && _heavy != null && _heavy.Value.strategy == 4);
            _support = SupportStatus.NotActive;
            _heavy = null;
            _leader = null;
            _supportTimer = 0;
            _heavyGathered = false;
            foreach (var c in _supportCars)
            {
                if (!c) continue;
                c.SupportGoal = CopGoal.None;
                if (join && c.InPursuitList && !c.Destroyed && c.Goal != CopGoal.FleePursuit) c.StartPursuit();
            }
            _supportCars.Clear();
        }

        // ---- damage, contacts and the end of a pursuit ----

        /// <summary>The last time the player touched this cop (DamageCopCar: "the player is the collision cause").</summary>
        public float LastPlayerContact(CopCar c) => _lastContact.TryGetValue(c, out float t) ? t : -100f;

        /// <summary>
        /// DamageVehicle::OnImpact on a cop (DamageVehicle.cpp:134-216; DamageCopCar). A cop on its own chassis
        /// (CopCar.Sim) takes it in TrafficVehicle's VehicleDamage with its damagespecs (`aiprobe cops`: cops 5 hit points /
        /// threshold 0.75, copghost 6, copgto 7 / 1.5, copgtoghost 8, copsport* 10 / 1.5, SUVs 10 / 2, Cross 400 / 10; FORCE
        /// 20, SUPPRESS_DIST 35 (Cross 25); HitPointScale front/rear/top 0.5, sides and corners 1); here only the player's
        /// last touch is kept. On the racer model (CopCar.UseSuspensionSimple off) an impact adds Δv/25 above 3 m/s [ours];
        /// light props (under 2000 kg, not fixed) do not damage cops.
        /// </summary>
        void CopHit(CopCar cop, Collision c)
        {
            if (!cop || cop.Destroyed) return;
            var other = c.rigidbody;
            var otherCar = other ? other.GetComponent<PlayerCar>() : null;
            if (other && !otherCar && !other.isKinematic && other.mass < 2000f) return;
            float dv = c.impulse.magnitude / Mathf.Max(1f, cop.Body.mass);
            if (otherCar && otherCar == Player) _lastContact[cop] = Time.time;
            if (cop.Sim && cop.Sim.Damage != null) return;
            if (dv <= 3f) return;
            cop.HitPoints += dv / 25f;
            if (cop.HitPoints > 1f) CopDestroyed(cop, otherCar && otherCar == Player || Time.time - LastPlayerContact(cop) < 3f);
        }

        /// <summary>A cop car is wrecked: TooDamaged, out of the pursuit, its bounty × the takedown combo (1..3 within DestroyCopBonusTime).</summary>
        public void CopDestroyed(CopCar c, bool byPlayer)
        {
            if (c.Destroyed) return;
            c.MarkDestroyed(byPlayer);
            if (c.Roadblock != null) c.Roadblock.CopsDestroyed++;
            bool inPursuit = c.InPursuitList;
            if (c.Pursuit is RacerPursuit rp) { rp.RemoveVehicle(c); return; }   // a racer's pursuit: counted there, no rep points while racing
            if (inPursuit) RemoveVehicle(c);
            if (!InPursuit || !(inPursuit || c.Roadblock != null)) return;
            if (c.Roadblock != null) { c.Roadblock.Cars.Remove(c); c.Roadblock = null; }
            // IncNumCopsDestroyed (:917-955): any destroyed pursuit or roadblock cop counts, whoever wrecked it, while
            // mAllowStatsToAccumulate; rep points = aivehicle RepPointsForDestroying[level] x the bonus multiplier (1..3
            // within DestroyCopBonusTime)
            if (!AllowStatsToAccumulate) return;
            CopsDisabled++;
            _copsDestroyed++;
            _combo = _comboTimer > 0 ? Mathf.Min(3, _combo + 1) : 1;
            _comboTimer = Row.DestroyCopBonusTime;
            int award = c.Type.BountyAt(Heat) * (int)_combo;
            Bounty += award;
            Stats.CopsDestroyed++;
            // the takedown message is the HUD PursuitBoard's (FePursuitBoard.cpp SetNumCopsDestroyed: localised cop type,
            // COPS_TAKENOUT_ICON, Priority_4); the exe has no other, so the [ours] "Combo Bounty" HudMessage is gone (round 9)
        }

        void Watch(PlayerCar player)
        {
            _watched = player;
            var meter = player.GetComponent<CollisionMeter>();
            if (!meter) meter = player.gameObject.AddComponent<CollisionMeter>();
            meter.OnContact = (impulse, c) => PlayerHit(player, meter, impulse, c);
        }

        /// <summary>
        /// AIPerpVehicle::OnCollision (AIVehicle.cpp:2418-2530): a hit above 4 m/s closing is causal; it is intentional
        /// when the player closes faster than the other car. Cops: damaged by the perp, assault infraction, cost to state
        /// 2000 scaled over 4..30 m/s; a non-pursuing cop rammed on purpose starts a pursuit. Traffic: the hit time
        /// (patrol trigger) and hit and run.
        /// </summary>
        void PlayerHit(PlayerCar player, CollisionMeter meter, float impulse, Collision c)
        {
            var otherBody = c.rigidbody;
            var other = otherBody ? otherBody.GetComponent<PlayerCar>() : null;
            if (other)
            {
                var n = c.contactCount > 0 ? c.GetContact(0).normal : Vector3.zero;   // points from the other car to the player
                var otherMeter = other.GetComponent<CollisionMeter>();
                var myVel = meter.PrevVelocity;
                var hisVel = otherMeter ? otherMeter.PrevVelocity : otherBody.linearVelocity;
                float closing = (myVel - hisVel).magnitude;
                bool causal = closing > 4f;
                bool intentional = Vector3.Dot(myVel, -n) > Vector3.Dot(hisVel, n);
                var cop = other.GetComponent<CopCar>();
                if (cop)
                {
                    _lastContact[cop] = Time.time;
                    bool pursuitCop = cop.InPursuitList || cop.Roadblock != null;
                    if (!cop.DamagedByPerp && causal)
                    {
                        cop.DamagedByPerp = true;
                        if (InPursuit && pursuitCop && AllowStatsToAccumulate)   // NotifyCopDamaged (:2670-2680)
                        {
                            _copsDamaged++;
                            Stats.CopsDamaged++;
                            if (cop.Roadblock != null) cop.Roadblock.CopsDamaged++;
                            if (intentional) Stats.Infraction("Ramming a Police Vehicle");
                        }
                    }
                    if (!cop.Destroyed && InPursuit && intentional)
                        _property += Mathf.Max(50, (int)(2000 / 50 * Mathf.Clamp01((closing - 4f) / 26f)) * 50);
                    if (!cop.InPursuit && cop.Roadblock == null && intentional && causal && !cop.Destroyed) PursuitRequest(cop);
                    return;
                }
                if (causal && intentional)
                {
                    LastTrafficHitTime = Time.time;
                    if (InPursuit)
                    {
                        _trafficHit++;
                        if (PerpInSight && _minDistance3 < 25f) Stats.Infraction("Hit and Run");
                        if (player.SpeedMps > 25f) Stats.Infraction("Reckless Driving");
                    }
                }
                return;
            }
            if (!InPursuit) return;
            var smack = c.collider ? c.collider.GetComponentInParent<Smackable>() : null;
            if (smack && smack.CostToState > 0) { _property += smack.CostToState; _propertyCount++; }   // mPropertyDamageValue / mNumPropertyDamaged
            if (impulse > 0.5f && PerpInSight && _minDistance3 < 25f) Stats.Infraction("Damage to Property");
        }

        void SetBusted()
        {
            _busted = true;
            // every cop: StopShort (full brake + handbrake), lights off
            foreach (var c in _pursuers)
                if (!c.Destroyed) { c.InPositionGoal = CopGoal.StopShort; c.DoInPositionGoal(); c.SirenOn = false; }
            Busted();
        }

        // ---- public entry points ----

        /// <summary>
        /// A pursuit challenge: the player rolls off the start at InitialPlayerSpeed, cops spawn on the event's
        /// markers at the forced heat level, and the challenge is won by reaching its target and then escaping.
        /// </summary>
        public void BeginChallenge(RaceEvent ev, PlayerCar player)
        {
            Clear(true);
            Challenge = ev;
            Enabled = true;
            if (Player != player) { Player = player; _playerBody = player.GetComponent<Rigidbody>(); PlayerPotentials = new Potentials(player.Spec); }
            _lockoutUntil = 0;
            int heat = Mathf.Clamp(ev.ForceHeat, 1, 6);
            MaxHeat = Mathf.Max(5, heat);
            Heat = heat;
            RefreshAvoidables();
            StartPursuit(heat);
            var row = Row;
            foreach (var sp in ev.CopSpawns)
            {
                var pos = Coords.ToUnity(sp.Position);
                float a = sp.Rotation * Mathf.Deg2Rad;
                var cop = SpawnCop(RandomCopType(row), pos, new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)));
                if (cop) AddVehicle(cop);
            }
            if (_pursuers.Count == 0) SpawnForcedPursuitCar(RandomCopType(row), 80f);
            var body = player.GetComponent<Rigidbody>();
            if (body) body.linearVelocity = player.transform.forward * ev.InitialSpeed * 0.44704f;
            Debug.Log($"NFSMW police: challenge {ev.EventId} {ev.MilestoneType} {ev.Threshold}, heat {Heat}, {ev.CopSpawns.Count} cops");
        }

        bool ChallengeMet()
        {
            var m = new Milestone { Type = Challenge.MilestoneType, GoalEasy = Challenge.Threshold };
            return m.Met(Stats.Value(Challenge.MilestoneType == "pursuit_evasion_time" ? "pursuit_length" : Challenge.MilestoneType));
        }

        void ChallengeEnd(bool won)
        {
            var ev = Challenge;
            Challenge = null;
            if (won) GameSession.CompleteChallenge(ev);
            var m = new Milestone { Type = ev.MilestoneType };
            Debug.Log($"NFSMW police: challenge {ev.EventId} {(won ? "won" : "failed")}");
            var fe = FrontEnd.Create();
            fe.Push(new MessageScreen(won ? "CHALLENGE COMPLETE" : "CHALLENGE FAILED",
                $"{RaceListScreen.ChallengeGoal(ev)}   ({m.Title}: {m.Format(Stats.Value(ev.MilestoneType == "pursuit_evasion_time" ? "pursuit_length" : ev.MilestoneType))})"));
            fe.Closed += Back;

            void Back()
            {
                fe.Closed -= Back;
                GameFlow.QuitToMainMenu();
            }
        }

        /// <summary>QA / debug: a pursuit under way with three cops coming at the player (PursueAtHeatLevel).</summary>
        public void ForcePursuit(int heat = 2)
        {
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (!player) return;
            Enabled = true;
            _lockoutUntil = 0;
            if (Player != player) { Player = player; _playerBody = player.GetComponent<Rigidbody>(); PlayerPotentials = new Potentials(player.Spec); }
            RefreshAvoidables();
            StartPursuit(Mathf.Clamp(heat, 1, 5));
            for (int i = 0; i < 3; i++) SpawnForcedPursuitCar(RandomCopType(Row), 80f + 15f * i);
            _roadBlockTimer = 3f;
        }

        /// <summary>QA / debug: the arrest sequence.</summary>
        public void ForceBusted() => Busted();

        void Busted()
        {
            if (NisPlayer.Active || BustPrevented) return;   // already being arrested / Game.PreventPlayerBeingBusted
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            Vector3? cop = null;
            float best = float.MaxValue;
            foreach (var c in CopCars)
            {
                float d = player ? (c.transform.position - player.transform.position).sqrMagnitude : 0;
                if (d < best) { best = d; cop = c.transform.position; }
            }
            if (player && NisPlayer.Enabled) NisPlayer.Busted(player, cop, BustedResult);
            else BustedResult();
        }

        void BustedResult()
        {
            if (Challenge != null) { Clear(true); ChallengeEnd(false); return; }
            // busted while racing: GRaceStatus ends the race on MPerpBusted (UCRC32_Gameplay) [?: the race is abandoned here]
            if (RaceManager.Active != null) RaceManager.Active.Abort();
            var prof = GameSession.Profile;
            if (prof != null && GameSession.Mode == GameMode.Career)
            {
                prof.Busted++;
                Gameplay.CareerStable.OnPursuitBusted(prof.Car, Stats, Mathf.RoundToInt(Bounty));   // CommitPursuitDetails 0x411373 (busted) + the busted screen's NotifyBusted / BecomeImpounded
                prof.Save();
                // Infractions.fng (uiInfractions.cpp): fines, Get Out of Jail markers, impound strikes, then the safehouse
                FrontEnd.Create().Push(new InfractionsScreen(Stats, Heat));
            }
            else FrontEnd.Create().Push(new MessageScreen("BUSTED", ""));
            Clear(true);
        }

        void Evaded()
        {
            var prof = GameSession.Profile;
            int bounty = Mathf.RoundToInt(Bounty);
            Stats.Evaded = true;
            UpdateCostToState();
            // ICopMgr::LockoutCops(true) (AIPursuit.cpp:2064): no cops (and no new pursuits) for TimeInactiveFor911
            LockoutCops(true);
            if (Challenge != null)
            {
                bool met = ChallengeMet();
                Clear(true);
                ChallengeEnd(met);
                return;
            }
            var done = new List<Milestone>();
            if (prof != null && GameSession.Mode == GameMode.Career)
            {
                prof.PursuitsEvaded++;
                prof.Bounty += bounty;
                prof.CopsDisabled += CopsDisabled;
                prof.Infractions += Stats.Infractions.Count;
                prof.RoadblocksDodged += Stats.RoadblocksDodged;
                if (prof.Car != null) prof.Car.Heat = Heat;
                done = GameSession.ScorePursuit(Stats);
                Gameplay.CareerStable.OnPursuitEvaded(prof.Car, Stats, bounty);   // CommitPursuitDetails 0x411373 + AdjustStableImpound/Heat_EvadePursuit 0x4110F3 / 0x411211
                prof.Save();
            }
            Debug.Log($"NFSMW police: evaded after {Stats.Length:0} s, bounty {bounty}, {Stats.Infractions.Count} infractions, {Stats.RoadblocksDodged} roadblocks dodged, {done.Count} challenges done");
            // PursuitIsEvaded (AICopManager.cpp:1675-1687): EShowMilestones (the pursuit result) only in play mode Roaming
            if (IsRoaming)
            {
                var pfe = FrontEnd.Create();
                pfe.Push(new PursuitResultScreen(Stats, bounty, done));
                void AfterPursuit() { pfe.Closed -= AfterPursuit; Story.CheckProgress(); }
                pfe.Closed += AfterPursuit;
            }
            Clear(true);
        }

        public void Clear(bool despawn)
        {
            foreach (var rp in _racerPursuits) rp.End();
            _racerPursuits.Clear();
            InPursuit = false;
            Busting = 0;
            Cooldown = 0;
            InCooldown = false;
            CooldownProgress = 0;
            BarValue = 0;
            PerpInSight = false;
            _busted = _bailed = false;
            _bustRate = 0;
            _latchedRoadblock = false;
            if (_watched)
            {
                _watched.TireDamage = 0;
                if (_watched.TryGetComponent<TireState>(out var tires)) tires.Repair();
                else _watched.BlownTires = 0;
            }
            foreach (var c in _pursuers) if (c) { c.InPursuitList = false; c.EndPursuit(); if (!c.Destroyed) c.StartFlee(); }
            _pursuers.Clear();
            ResetSupport();
            if (!despawn) return;
            foreach (var c in _cops)
            {
                if (!c) continue;
                try { CopUnspawned?.Invoke(c, 0); } catch (System.Exception e) { Debug.LogException(e); }
                Destroy(c.gameObject);
            }
            _cops.Clear();
            _lastContact.Clear();
            _breakerZones.Clear();
            foreach (var r in _roadblocks) r.ReleaseProps();
            _roadblocks.Clear();
            _roadblock = null;
            if (_heli) Destroy(_heli.gameObject);
            _heli = null;
        }

        void OnGUI()
        {
            // pursuit challenges: the goal and progress under the HUD's pursuit board
            if (!InPursuit || Challenge == null || (FrontEnd.Instance != null && FrontEnd.Instance.IsOpen) || NisPlayer.Active) return;
            float s = Screen.height / 720f;
            var m = new Milestone { Type = Challenge.MilestoneType };
            string stat = m.Format(Stats.Value(Challenge.MilestoneType == "pursuit_evasion_time" ? "pursuit_length" : Challenge.MilestoneType));
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * s), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 0.85f, 0.3f) } };
            GUI.Label(new Rect(0, 120 * s, Screen.width, 24 * s), $"CHALLENGE {Challenge.EventId}: {RaceListScreen.ChallengeGoal(Challenge)}  [{stat}]{(ChallengeMet() ? "  - now lose the cops!" : "")}", style);
        }

        void OnDestroy() => Clear(true);
    }

    /// <summary>Reports collision impulses (cop damage, the player's contacts) and the velocity before each physics step.</summary>
    public sealed class CollisionMeter : MonoBehaviour
    {
        public System.Action<float> OnHit;
        public System.Action<float, Collision> OnContact;
        /// <summary>The body's velocity at the last FixedUpdate (before the step that produced the contact).</summary>
        public Vector3 PrevVelocity { get; private set; }
        Rigidbody _rb;

        void FixedUpdate()
        {
            if (!_rb) _rb = GetComponent<Rigidbody>();
            if (_rb) PrevVelocity = _rb.linearVelocity;
        }

        void OnCollisionEnter(Collision c)
        {
            if (!_rb) _rb = GetComponent<Rigidbody>();
            float impulse = c.impulse.magnitude / Mathf.Max(1f, _rb ? _rb.mass : 1f);
            if (impulse <= 2f) return;
            OnHit?.Invoke(impulse / 8f);
            OnContact?.Invoke(impulse / 8f, c);
        }
    }
}
