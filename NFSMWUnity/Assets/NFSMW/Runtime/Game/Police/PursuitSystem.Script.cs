using NfsMw.Core;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The cop calls of the career scripts (Lua Drive mode, specs/lua_hooks.md §3): scripted cop spawns, world heat,
    /// NoNewPursuitsOrCops, PreventPlayerBeingBusted, EBailPursuit [ours: additive API for LuaDrive].
    /// </summary>
    public sealed partial class PursuitSystem
    {
        /// <summary>ICopMgr::NoNewPursuitsOrCops (Game.NoNewPursuitsOrCops 0x60AAB0): no new pursuit starts until cleared
        /// (Game.SetCopsEnabled(true), SetRoaming, LockoutCops(false)).</summary>
        public bool NoNewPursuits;

        float _noBustUntil;
        RaceManager _scriptRace;

        /// <summary>The race running now has the script's own cops (Game.SpawnCop): the police runs during it [ours: other
        /// races start clean].</summary>
        public bool ScriptRace => RaceManager.Active != null && ReferenceEquals(_scriptRace, RaceManager.Active) && RaceManager.Active.Driven;

        /// <summary>Game.PreventPlayerBeingBusted (0x60AE00, every sim tick of `raceover`): no arrest for a moment.</summary>
        public void PreventBusted(float seconds) => _noBustUntil = Mathf.Max(_noBustUntil, Time.time + seconds);

        /// <summary>The arrest is held off (PreventBusted).</summary>
        public bool BustPrevented => Time.time < _noBustUntil;

        /// <summary>
        /// Game.SpawnCop (0x60A670): ICopMgr::LockoutCops(false) (vtable +0x2C), then ICopMgr::SpawnCop (vtable +0x1C,
        /// AICopManager::SpawnCop 0x439DD0: a queued SpawnCopRequest) at the marker. AICopManager::TrySpawnCop
        /// (AICopManager.cpp): the named cop car on the ground at the marker, then InPursuit → StartPursuit on the local
        /// player and added to his pursuit; else RoadBlock → StartRoadBlock; else StartPatrol.
        /// </summary>
        public PlayerCar SpawnScriptedCop(Vector3 pos, Vector3 dir, string type, bool pursue, bool roadBlock = false)
        {
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (player && Player != player) { Player = player; _playerBody = player.GetComponent<Rigidbody>(); PlayerPotentials = new Potentials(player.Spec); }
            if (RaceManager.Active != null) _scriptRace = RaceManager.Active;
            LockoutCops(false);
            var cop = SpawnCop(string.IsNullOrEmpty(type) ? RandomCopType(Row) : type, pos, dir.sqrMagnitude > 1e-4f ? dir : Vector3.forward);
            if (!cop) return null;
            if (pursue && Player)
            {
                Enabled = true;
                NoNewPursuits = false;
                if (!InPursuit) { RefreshAvoidables(); StartPursuit(null); }
                cop.ZeroTimeSinceTargetSeen();
                AddVehicle(cop);
            }
            else if (roadBlock) cop.SetGoal(CopGoal.StaticRoadBlock);
            else cop.SetGoal(CopGoal.Patrol);
            Debug.Log($"NFSMW police: scripted {cop.Type.Name}{(pursue ? " in pursuit" : roadBlock ? " in a roadblock" : " on patrol")}");
            return cop.Car;
        }

        /// <summary>
        /// Game.SetWorldHeat (0x612660): IPerpetrator::SetHeat on the player's car — the career car's heat, and the
        /// pursuit's level when one is under way.
        /// </summary>
        public void SetWorldHeat(float heat)
        {
            GameSession.SetWorldHeat(heat);
            if (InPursuit && heat >= 1f)
            {
                Heat = Mathf.Clamp(Mathf.RoundToInt(heat), 1, MaxHeat);
                HeatValue = Mathf.Max(HeatValue, Heat);
            }
        }

        /// <summary>AIPursuit +0x240 mEnterSafehouseOnDestruct (GetEnterSafehouseOnDone 0x433C40): the pursuit ended entering a safehouse.</summary>
        public bool EnterSafehouseOnDone { get; private set; }

        /// <summary>
        /// The HUD's HUDACTION_ENGAGE_EVENT during PS_COOL_DOWN inside a "safehouse" menu zone (FEPkg_Hud.cpp:786-805) →
        /// AIPursuit::EndPursuitEnteringSafehouse (0x40B040): mEvadeLevel 1, mPursuitStatus PS_EVADED, mEnterSafehouseOnDestruct;
        /// the evade then runs AICopManager::PursuitIsEvaded (0x41116E: EShowMilestones(GetEnterSafehouseOnDone) when roaming →
        /// PursuitData::PopulateData(…, exitToSafehouse) 0x642083) and ~PostRacePursuitScreen (0x58E320) posts
        /// MEnterSafeHouse("safehouse") when PursuitData.mExitToSafehouse is set — the menu gate's entersafehouse, which the
        /// port's front end does itself (<paramref name="enterSafehouse"/> = Systems.EnterZone of the zone). [ours: the flag
        /// rides on the front end's Closed event of the result screen rather than on PursuitData.]
        /// </summary>
        public bool EndPursuitEnteringSafehouse(System.Action enterSafehouse)
        {
            if (!InPursuit || !InCooldown) return false;
            EnterSafehouseOnDone = true;
            CooldownProgress = 1f;
            bool roaming = IsRoaming;
            Evaded();
            EnterSafehouseOnDone = false;
            var fe = UI.FrontEnd.Instance;
            if (roaming && enterSafehouse != null && fe != null && fe.IsOpen)
            {
                void Exit() { fe.Closed -= Exit; enterSafehouse(); }
                fe.Closed += Exit;
            }
            else if (!roaming) Debug.Log("NFSMW police: entered a safehouse in cooldown while racing: no EShowMilestones, no MEnterSafeHouse");
            return true;
        }

        /// <summary>
        /// rand_point_in_circle (AICopManager.cpp:840, 0x406340): angle = SimRandom_FloatRange(2π), radius = sqrt(SimRandom_FloatRange(1)),
        /// sim (sin·r, 0, cos·r) — as Unity x/z (x = sim z, z = −sim x): (cos·r, −sin·r).
        /// </summary>
        internal static Vector2 RandPointInCircle()
        {
            var rnd = SimRandom.Get();
            float angle = rnd.FloatRange(Mathf.PI * 2f);
            float radius = Mathf.Sqrt(rnd.FloatRange(1f));
            return new Vector2(Mathf.Cos(angle) * radius, -Mathf.Sin(angle) * radius);
        }

        /// <summary>
        /// The AIPursuit ctor's heat range (AIPursuit.cpp:618-653; PC 0x442FEB-0x44316B): with race parameters in the career context
        /// (GRaceStatus +0x1968 / +0x1964 == 2): the final epic pursuit 6 / 6, else the current bin's base / max open-world heat;
        /// in a quick race that is not a challenge race 1 / 5; otherwise (career free roam, challenge races) the bin's base / max
        /// (GRaceStatus::GetBinBaseHeat / GetBinMaxHeat: 0 / 10 without a bin) and scale, then a race with UseWorldHeat forces
        /// ForceHeatLevel .. GetMaxRaceHeatLevel (scale 1), else the race's MaxHeatLevel caps the maximum. The new pursuit's heat
        /// is raised to the base (:780). [port: the Challenge Series pursuits keep 1 .. MaxHeat, their heat being forced]
        /// </summary>
        internal (float baseHeat, float maxHeat) AIPursuitHeatRange(RaceEvent ev, bool career, bool quickRace, bool challengeRace)
        {
            var bin = career ? GameSession.CurrentBin : null;
            float binBase = bin != null ? bin.BaseOpenWorldHeat : 0f, binMax = bin != null ? bin.MaxOpenWorldHeat : 10f;
            if (ev != null && career)
                return ev.EpicPursuit ? (6f, 6f) : (binBase, binMax);   // IsFinalEpicPursuit = GetIsEpicPursuitRace (0x443004)
            if (quickRace && !challengeRace) return (1f, 5f);
            float b = binBase, m = binMax;
            if (ev != null && ev.UseWorldHeatInRace) { b = ev.ForceHeat; m = ev.MaxRaceHeatLevel; }
            else if (ev != null && ev.MaxHeatLevel > 0f && ev.MaxHeatLevel < m) m = ev.MaxHeatLevel;   // [port: an event without MaxHeatLevel does not cap]
            return (b, m);
        }

        /// <summary>EBailPursuit: the pursuit ends without a result screen (the epic pursuit's win, a mini game's end) [?].</summary>
        public void BailPursuit() => Clear(false);
    }
}
