using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>One pursuit's numbers: what the Blacklist challenges and the results screen measure.</summary>
    public sealed class PursuitStats
    {
        public static readonly string[] Types =
        {
            "pursuit_length", "pursuit_evasion_time", "cops_damaged", "bounty_in_pursuit", "roadblocks_dodged",
            "cost_to_state_in_pursuit", "total_infractions", "tire_spikes_dodged",
        };

        public float Length, Bounty;
        public int CostToState, CopsDamaged, CopsDestroyed, RoadblocksDodged, SpikesDodged, CopsInvolved;
        /// <summary>The AIPursuit counters behind CalcTotalCostToState (AIPursuit.cpp:733-746) that HighScoresDatabase::CommitPursuitInfo
        /// (RaceDB.cpp:46-60) adds to the career's CostToStateDetails: GetNumCopCarsDeployed, GetNumSupportVehiclesDeployed,
        /// GetNumTrafficCarsHit, GetNumHeliSpawns, GetNumRoadblocksDeployed, GetNumSpikeStripsDeployed, GetNumHeliSpikeStripDeployed,
        /// GetNumPropertyDamaged, GetValueOfPropertyDamaged. Kept current by PursuitSystem.UpdateCostToState.</summary>
        public int CopCarsDeployed, SupportVehiclesDeployed, TrafficCarsHit, HeliSpawns, RoadblocksDeployed, SpikeStripsDeployed, HeliSpikeStripsDeployed,
            PropertiesDamaged, PropertyDamageValue;
        public bool Evaded;
        public readonly List<string> Infractions = new List<string>();

        /// <summary>GInfractionManager::InfractionType (GInfractionManager.h:11-20; DidInfractionOccur 0x5DE350 tests the flag, PursuitStarted
        /// 0x5DE310 clears them): kInfraction_Speeding 1, Racing 2, Reckless 4, Assault 8, HitAndRun 16, Damage 32, Resist 64, OffRoad 128 —
        /// bit i = InfractionKinds[i] (FECareerRecord::CommitPursuitCarData's `infractions`, the rap sheet's Unserved[8] / fines).</summary>
        public static readonly string[] InfractionKinds = { "Speeding", "Racing", "Reckless Driving", "Ramming a Police Vehicle", "Hit and Run", "Damage to Property", "Resisting Arrest", "Driving Off Roadway" };

        /// <summary>The kInfraction index of an infraction name (the port's aliases: "Excessive Speeding" = Racing, "Driving Off-Roadway"), −1 if none.</summary>
        public static int InfractionIndex(string name) => name switch
        {
            "Excessive Speeding" => 1,
            "Driving Off-Roadway" => 7,
            _ => System.Array.IndexOf(InfractionKinds, name),
        };

        public uint InfractionBits
        {
            get
            {
                uint bits = 0;
                foreach (var n in Infractions) { int i = InfractionIndex(n); if (i >= 0) bits |= 1u << i; }
                return bits;
            }
        }

        public void Infraction(string name)
        {
            if (!Infractions.Contains(name)) Infractions.Add(name);
        }

        public float Value(string type) => type switch
        {
            "pursuit_length" => Length,
            "pursuit_evasion_time" => Evaded && Length >= 30f ? Length : 0,
            "cops_damaged" => CopsDamaged,
            "bounty_in_pursuit" => Bounty,
            "roadblocks_dodged" => RoadblocksDodged,
            "cost_to_state_in_pursuit" => CostToState,
            "total_infractions" => Infractions.Count,
            "tire_spikes_dodged" => SpikesDodged,
            _ => 0,
        };
    }

    // roadblocks, spike strips and tire damage: Police/Roadblock.cs
}
