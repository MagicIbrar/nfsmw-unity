using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// IPursuit as a cop car reads it (AI/IPursuit.h): the pursuit's target (AITarget) and the per-pursuit state the actions
    /// use. The player's pursuit is <see cref="PursuitSystem.PlayerPursuit"/>; an AI racer's is a <see cref="RacerPursuit"/>
    /// (AICopManager keeps one AIPursuit per perpetrator, AICopManager.cpp:1824-1967).
    /// </summary>
    public abstract class CopPursuit
    {
        /// <summary>AITarget: the perpetrator's car.</summary>
        public abstract PlayerCar Target { get; }
        public abstract Vector3 TargetVelocity { get; }
        /// <summary>IPerpetrator::GetPursuitLevelAttrib of the target (heat or race table row).</summary>
        public abstract HeatRow Row { get; }
        /// <summary>The target's pursuit level (int heat).</summary>
        public abstract int Heat { get; }
        /// <summary>IPursuit::IsPerpInSight.</summary>
        public abstract bool PerpInSight { get; }
        /// <summary>IPursuit::IsAJerk.</summary>
        public abstract bool IsJerk { get; }
        /// <summary>The perp's AIVehicle::UpdateRoads navs (current / future road, seek-ahead).</summary>
        public abstract PerpRoads Roads { get; }
        /// <summary>IPerpetrator (AIVehicle::GetSeekAheadPosition) of the target.</summary>
        public abstract Vector3 SeekAhead { get; }
        /// <summary>The target car's AIVehicle performance (top speed / acceleration table) for performance_limiter.</summary>
        public abstract Potentials TargetPotentials { get; }
        /// <summary>IPursuit::PursuitMeterCanShowBusted.</summary>
        public abstract bool PursuitMeterCanShowBusted { get; }
        /// <summary>IPerpetrator::IsHiddenFromCars of the target.</summary>
        public abstract bool PerpHiddenFromCars { get; }
        /// <summary>IPursuit::IsPlayerPursuit.</summary>
        public abstract bool IsPlayerPursuit { get; }
        /// <summary>GetPursuitStatus() == PS_COOL_DOWN.</summary>
        public abstract bool InCooldown { get; }
    }
}
