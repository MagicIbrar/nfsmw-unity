using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// AIPursuit for a non-player perpetrator: an AI racer chased by cops during a race (AI/Common/AIPursuit.cpp with
    /// IsPlayerPursuit() false; the racer is an AIPerpVehicle, AIVehicle.cpp:1934). AICopManager keeps one such pursuit
    /// per target (AICopManager.cpp:1824-1967): a patrol car that sees a racer while <c>CanPursueRacers</c> (the race has
    /// opponents and no racer is pursued yet) acquires it (AIVehicleCopCar::WatchForPerps :170-190) and the manager
    /// creates the pursuit on its next tick. What differs from the player's pursuit, per the decomp:
    /// <list type="bullet">
    /// <item>heat from the race table rows (AIPerpVehicle::SetHeat with play mode Racing, AIVehicle.cpp:2257-2297), rising
    /// from the perp's heat (1 at construction, raised to the pursuit's base heat on attach, :780-784);</item>
    /// <item>GetAdjustedCopCounts caps the wave at 3 cops, 2 while the player is pursued (:2173-2180);</item>
    /// <item>no stats, infractions, rep points, camera or HUD (mAllowStatsToAccumulate is false while racing, :1631-1633;
    /// IsPlayerPursuit gates the rest); the speech AI never follows it;</item>
    /// <item>busted (5 s stopped within 15 m of a cop in sight): BailPursuit, GRacerInfo::Busted + ForceStop (:1965-1975) -
    /// the racer is out of the race and the cops flee.</item>
    /// </list>
    /// Ticks at the AIPursuit rate (0.25 of the 60 Hz sim = 15 Hz) from <see cref="PursuitSystem"/>. [ours] no roadblocks,
    /// heavy / leader support or helicopter for racer pursuits: the race table rows carry no support and the manager's
    /// roadblock, support and heli code is bound to the player's pursuit (ai_cops.md §9.3).
    /// </summary>
    public sealed class RacerPursuit : CopPursuit
    {
        readonly PursuitSystem _police;
        readonly PlayerCar _target;
        readonly AIRacer _racer;
        readonly RaceManager.Entrant _entrant;
        readonly Rigidbody _body;
        readonly Potentials _pot;
        readonly PerpRoads _roads = new PerpRoads();
        public readonly List<CopCar> Pursuers = new List<CopCar>();
        readonly PursuitFormationState _fs = new PursuitFormationState();

        // AIPerpVehicle heat (mHeat), AIPursuit base / maximum heat
        float _heat, _baseHeat, _maxHeat;
        int _level;
        // AIPursuit
        float _pursuitTime, _meterModeTimer, _lostTime = -5f, _hiddenZoneTime, _coolDownRequired = 60f, _evadeLevel;
        bool _cooldownMeter, _busted, _bailed, _perpInSight = true, _ended, _backupRequested, _fastSpawnNext;
        float _bustedTimer, _bustRate, _backupTimer, _spawnCopTimer;
        int _required, _evaded, _backupAt, _totalCops, _needed, _copsDestroyed;
        float _minDistance3 = 100000f;
        Vector3 _lastKnown;
        // UpdateJerk
        Vector3 _jerkLagPos;
        float _jerkLagDist = 1000f, _jerkLagSpeed;
        bool _jerk;

        public RacerPursuit(PursuitSystem police, PlayerCar target, AIRacer racer, RaceManager.Entrant entrant)
        {
            _police = police;
            _target = target;
            _racer = racer;
            _entrant = entrant;
            _body = target.GetComponent<Rigidbody>();
            _pot = new Potentials(target.Spec);
            // the AIPursuit ctor (:629-669): quick race base 1 / max 5; career the bin's base and maximum heat; a race that
            // uses the world heat forces ForceHeatLevel .. MaxRaceHeatLevel, else the race's MaxHeatLevel caps it
            var ev = RaceManager.Active ? RaceManager.Active.Event : null;
            // GRaceStatus::GetBinBaseHeat / GetBinMaxHeat in career (PursuitSystem.Script.cs AIPursuitHeatRange)
            var hr = GameSession.Mode == GameMode.Career || GameSession.Mode == GameMode.QuickRace || GameSession.Mode == GameMode.ChallengeSeries
                ? police.AIPursuitHeatRange(ev, GameSession.Mode == GameMode.Career, GameSession.Mode == GameMode.QuickRace, police.Challenge != null)
                : (1f, police.MaxHeat);   // [ours] the port's free-roam test mode
            _baseHeat = Mathf.Max(1f, hr.baseHeat);
            _maxHeat = Mathf.Max(_baseHeat, hr.maxHeat);
            // OnAttached of the perp (:770-784): heat = max(perp heat, base heat); SetHeat
            _heat = Mathf.Max(racer ? racer.Heat : 1f, _baseHeat);
            if (racer) racer.Heat = _heat;
            _level = (int)_heat;
            _jerkLagPos = target.transform.position;
            _lastKnown = target.transform.position;
            _fs.Reset(Row);
            LockInPursuitAttribs();
        }

        // ---- CopPursuit ----
        public override PlayerCar Target => _target;
        public override Vector3 TargetVelocity => _body ? _body.linearVelocity : Vector3.zero;
        /// <summary>IPerpetrator::GetPursuitLevelAttrib: the race table row of the perp's heat (racing, not a pursuit race).</summary>
        public override HeatRow Row => PoliceData.RowFor(Mathf.Max(1, _level), true);
        public override int Heat => Mathf.Max(1, _level);
        public override bool PerpInSight => _perpInSight;
        public override bool IsJerk => _jerk;
        public override PerpRoads Roads => _roads;
        public override Vector3 SeekAhead => _target ? _roads.GetSeekAhead(_target.transform.position, TargetVelocity, _jerk, Time.time) : Vector3.zero;
        public override Potentials TargetPotentials => _pot;
        public override bool PursuitMeterCanShowBusted => !(_cooldownMeter && _meterModeTimer < 2.5f);
        public override bool PerpHiddenFromCars => false;   // an AI racer never uses the hiding zones (AIPerpVehicle::Update runs, but racers stay on the route)
        public override bool IsPlayerPursuit => false;
        public override bool InCooldown => _cooldownMeter;

        public float HeatValue => _heat;
        public bool IsPerpBusted => _busted;
        public bool Bailed => _bailed;
        public float EvadeLevel => _evadeLevel;
        public float PursuitTime => _pursuitTime;
        public int CopsDestroyed => _copsDestroyed;
        public bool Ended => _ended;
        /// <summary>IPursuit::GetNumCops.</summary>
        public int NumCops => Pursuers.Count;

        /// <summary>AIPursuit::ShouldEnd (:2139-2160): the target is gone, evaded, or busted / bailed with no cops left.</summary>
        public bool ShouldEnd
        {
            get
            {
                if (!_target || _entrant == null || !_entrant.InRace && !_bailed) return true;
                if (_evadeLevel >= 1f) return true;
                if ((_busted || _bailed) && Pursuers.Count == 0) return true;
                return false;
            }
        }

        /// <summary>LockInPursuitAttribs (:723).</summary>
        void LockInPursuitAttribs()
        {
            var row = Row;
            _required = row.FullEngagementCopCount;
            _backupAt = row.NumCopsToTriggerBackup;
            _coolDownRequired = row.EvadeTimeout;
            _evaded = 0;
        }

        /// <summary>AIPursuit::OnAttached for a cop (:785-836): spawn timers, counts, StartPursuit.</summary>
        public void AddVehicle(CopCar cop)
        {
            if (!cop || Pursuers.Contains(cop)) return;
            if (cop.Pursuit is RacerPursuit other && other != this) other.RemoveVehicle(cop);
            cop.InPursuitList = true;
            Pursuers.Add(cop);
            var row = Row;
            if (_totalCops < 3 && !_cooldownMeter) _spawnCopTimer = row.TimeBetweenFirstFourSpawn;
            else
            {
                _spawnCopTimer = row.TimeBetweenCopSpawn;
                if (_needed > 2)
                {
                    if (_fastSpawnNext) { _fastSpawnNext = false; _spawnCopTimer = 0.2f; }
                    else _fastSpawnNext = true;
                }
            }
            _totalCops++;
            cop.StartPursuit(this);
        }

        /// <summary>AIPursuit::OnDetached for a cop (:849-915) without the stats (mAllowStatsToAccumulate is false in a race).</summary>
        public void RemoveVehicle(CopCar cop)
        {
            if (!Pursuers.Remove(cop)) return;
            cop.InPursuitList = false;
            if (cop.Destroyed) _copsDestroyed++;   // IncNumCopsDestroyed: counted, no rep points while racing (:917-955)
            cop.EndPursuit();
        }

        /// <summary>The 15 Hz AIPursuit task (OnTask :1619-2125) for a racer target.</summary>
        public void Tick(float dt)
        {
            if (_ended || !_target) return;
            var p = _target.transform.position;
            var vel = TargetVelocity;
            float speed = _target.SpeedMps;
            _roads.Update(p, vel, _target.transform.forward, Time.time);
            Pursuers.RemoveAll(c => !c || !c.Car);

            UpdateJerk(p, speed, dt);
            var row = Row;
            if (_fs.Update(_target, vel, row, Pursuers, _roads, _jerk, _busted, _bailed, _perpInSight, dt)) { }

            if (_required == 0) LockInPursuitAttribs();
            // heat (:1660-1699): not in cooldown, dT / (TimePerHeatLevel x ScaleEscalationPerBucket(bin)), clamped to base..max
            if (!_cooldownMeter)
            {
                _pursuitTime += 0f;   // mTotalPursuitTime only accumulates with mAllowStatsToAccumulate (false while racing)
                int bin = GameSession.Mode == GameMode.Career ? GameSession.Profile?.Bin ?? 14 : 14;
                _heat = Mathf.Clamp(_heat + dt / row.SecondsPerHeat(Mathf.Min(bin, 14)), _baseHeat, _maxHeat);
                if (_racer) _racer.Heat = _heat;
                if ((int)_heat != _level)
                {
                    _level = (int)_heat;
                    _fs.ActiveTime = 0f;
                    row = Row;
                }
            }
            _spawnCopTimer -= dt;
            _fs.ActiveTime -= dt;
            _meterModeTimer += dt;

            _fs.Pick(row, null, _busted, _bailed, _police.SpeechFinished);
            RemoveUnwantedVehicles();

            // line of sight, distances, engagement (:1776-1847)
            float min3 = float.MaxValue, minxz = float.MaxValue;
            _lostTime += dt;
            int engaged = 0;
            foreach (var c in Pursuers)
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
                    if (!_cooldownMeter) c.SetWithinEngagementRadius();
                }
            }
            int remaining = _required - _evaded;
            if (remaining < 1) { remaining++; _required++; }
            if (engaged - remaining > 0) _required += engaged - remaining;
            if (_required != 0 && !_backupRequested && !_cooldownMeter && remaining <= _backupAt)
            {
                _backupRequested = true;
                _backupTimer = row.BackupCallTimer;
            }
            _minDistance3 = min3;

            // the busted meter (:1884-1933): +0.25 dT per tick at 60 Hz (= 1/s) stopped within 15 m of a level cop while seen
            float bustMps = row.BustSpeedKmh / 3.6f;
            if (_busted) _bustRate = 1f;
            else if (_perpInSight && !_bailed)
            {
                _bustRate = speed < bustMps && minxz < 15f ? 1f : -2f;
                if (_backupRequested)
                {
                    _backupTimer -= dt;
                    if (_backupTimer < 0) { _backupRequested = false; LockInPursuitAttribs(); }
                }
            }
            else _bustRate = -2f;
            _bustedTimer = Mathf.Max(0f, _bustedTimer + _bustRate * dt);
            if (!_busted && !_bailed && _bustedTimer > 5f)
            {
                // a non-player perp (:1965-1975): BailPursuit, GRacerInfo::Busted + ForceStop, MPerpBusted to "AIRacerBusted"
                _busted = true;
                BailPursuit();
                if (_entrant != null) { _entrant.Busted = true; _entrant.Out = true; _entrant.DNF = true; }
                if (_racer) _racer.ForceStop();
                Debug.Log($"NFSMW police: racer {_target.name} busted after {_pursuitTime:0} s");
            }
            if (_bailed)
                foreach (var c in Pursuers) if (!c.Destroyed) c.StartFlee();

            _perpInSight = _lostTime < 7f;
            if (_perpInSight) { _hiddenZoneTime = 0f; _lastKnown = p; }

            // cooldown and evade (:2028-2110)
            float elapsed = _lostTime + _hiddenZoneTime;
            if (_lostTime > 7f)
            {
                _evadeLevel = elapsed / _coolDownRequired;
                if (!_cooldownMeter)
                {
                    _evadeLevel = 0f;
                    if (_meterModeTimer > 2.5f)
                    {
                        _meterModeTimer = 0f;
                        _cooldownMeter = true;
                        _spawnCopTimer = Mathf.Min(_spawnCopTimer, row.TimeBetweenCopSpawn);
                        _backupTimer = 0f;
                    }
                }
                else if (_evadeLevel < 0.05f) _evadeLevel = 0.05f;
                else if (_evadeLevel >= 1f) { _evadeLevel = 1f; }   // PS_EVADED; LockoutCops only for the player's pursuit (:2070-2073)
            }
            else
            {
                if (_cooldownMeter)
                {
                    _evadeLevel = Mathf.Max(0.05f, _evadeLevel * 0.93f);
                    if (_meterModeTimer > 2.5f) { _cooldownMeter = false; _meterModeTimer = 0f; }
                }
                else _evadeLevel = 0f;
            }
            if (_busted || _bailed) _evadeLevel = 0f;
        }

        /// <summary>UpdateJerk (:2824-2853).</summary>
        void UpdateJerk(Vector3 pos, float speed, float dt)
        {
            float k = 0.1f * dt;
            _jerkLagPos = Vector3.Lerp(_jerkLagPos, pos, k);
            _jerkLagDist = Mathf.Lerp(_jerkLagDist, Vector3.Distance(pos, _jerkLagPos), k);
            _jerkLagSpeed = Mathf.Lerp(_jerkLagSpeed, speed, k * 0.5f);
            float f = _jerkLagDist > 0.01f ? 10f * _jerkLagSpeed / _jerkLagDist : 0;
            if (f >= 3f) _jerk = true;
            else if (f <= 1.75f) _jerk = false;
        }

        /// <summary>AIPursuit::BailPursuit (:2643): bailed; the manager treats it as evaded (PursuitIsEvaded does nothing for a racer).</summary>
        public void BailPursuit() => _bailed = true;

        // ---- wave counts (GetAdjustedCopCounts :2165, CopRequest :2356, RemoveUnwantedVehicles :2224, FleeCopOfType :2277) ----

        /// <summary>
        /// GetAdjustedCopCounts for a non-player pursuit: the global (player's) row's cop list scaled to at most 3 cops,
        /// 2 while the player's pursuit is active; in cooldown exactly min(that, own NumPatrolCars).
        /// </summary>
        List<(string type, int count, int chance)> AdjustedCopCounts()
        {
            var global = _police.GlobalRow;
            int max = _police.InPursuit ? 2 : 3, min = 0;
            if (_cooldownMeter) max = min = Mathf.Min(max, Row.NumPatrolCars);
            else max = Mathf.Clamp(_required - _evaded, 0, max);
            int nominal = 0;
            foreach (var c in global.Cops) if (c.type != "copheli") nominal += c.count;
            int want = Mathf.Clamp(nominal, min, max);
            var list = new List<(string, int, int)>();
            foreach (var c in global.Cops)
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

        Dictionary<string, int> Contingent()
        {
            var d = new Dictionary<string, int>();
            foreach (var c in Pursuers) d[c.Type.Name] = (d.TryGetValue(c.Type.Name, out int n) ? n : 0) + 1;
            return d;
        }

        /// <summary>CopRequest (:2356-2457) without the helicopter (not for racer pursuits here).</summary>
        public string CopRequest()
        {
            if (_busted || _spawnCopTimer >= 0 || _bailed || _ended) return null;
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
                if (counts[i].type == "copheli" || need[i] == 0) weight[i] = 0;
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
        }

        void FleeCopOfType(string type, int count)
        {
            CopCar far = null, second = null;
            float d1 = 0, d2 = 0;
            int canSee = 0, fleeing = 0;
            var tp = _target.transform.position;
            foreach (var c in Pursuers)
            {
                if (c.IsSupport || c.Destroyed) continue;
                if (c.Goal == CopGoal.FleePursuit) { fleeing++; continue; }
                bool sees = c.TimeSinceSeen <= 0;
                if (sees) canSee++;
                if (c.Type.Name != type) continue;
                float d = Vector3.Distance(c.transform.position, tp) + (sees ? 0 : 40f);
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

        /// <summary>The manager detaches the pursuit (UpdatePursuits :1930-1952): its cops are removed and flee.</summary>
        public void End()
        {
            if (_ended) return;
            _ended = true;
            foreach (var c in Pursuers.ToArray())
            {
                if (!c) continue;
                c.InPursuitList = false;
                c.EndPursuit();
                if (!c.Destroyed) c.StartFlee();
            }
            Pursuers.Clear();
        }
    }
}
