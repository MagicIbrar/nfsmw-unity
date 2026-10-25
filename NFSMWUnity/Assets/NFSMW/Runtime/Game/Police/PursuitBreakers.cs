using System.Collections.Generic;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Pursuit breakers on the police side: MBreakerStopCops zones (AICopManager.cpp:689-728) and the gas-station
    /// explosion moment the speech AI comments on (Observer::MessageGamePlayMoment, aud_moment_strm
    /// key_expl_gas_station). Nothing in the port triggers breakers yet: whatever ports the breaker smackables calls
    /// <see cref="BreakerStopCops"/> (and <see cref="NotifyGasStationExplosion"/> for the gas station) [ours: entry points].
    /// </summary>
    public sealed partial class PursuitSystem
    {
        struct BreakerZone { public Vector3 Position; public float Radius, EndTime; }

        readonly List<BreakerZone> _breakerZones = new List<BreakerZone>();

        /// <summary>MBreakerStopCops(initial position, radius, duration): every cop car that enters the radius (x/z)
        /// before the zone expires is destroyed.</summary>
        public void BreakerStopCops(Vector3 position, float radius, float duration) =>
            _breakerZones.Add(new BreakerZone { Position = position, Radius = radius, EndTime = Time.time + duration });

        /// <summary>A gas station blew up (the moment stream's key_expl_gas_station); byPlayer = the causality is a player car.</summary>
        public event System.Action<Vector3, bool> GasStationExploded;

        public void NotifyGasStationExplosion(Vector3 position, bool byPlayer)
        {
            try { GasStationExploded?.Invoke(position, byPlayer); } catch (System.Exception e) { Debug.LogException(e); }
        }

        /// <summary>
        /// AICopManager::ApplyBreakerZones (first step of the manager tick): IDamageable::Destroy on every live cop car
        /// inside a zone (pursuit, patrol or roadblock), then expired zones go. The destroyed cars take the normal wreck
        /// path; the cause is credited to the player like a takedown [?: the game's Destroy has no causality argument].
        /// </summary>
        void ApplyBreakerZones()
        {
            if (_breakerZones.Count == 0) return;
            for (int i = _cops.Count - 1; i >= 0; i--)
            {
                var c = _cops[i];
                if (!c || c.Destroyed) continue;
                var p = c.transform.position;
                foreach (var b in _breakerZones)
                {
                    if (new Vector2(p.x - b.Position.x, p.z - b.Position.z).magnitude < b.Radius)
                    {
                        CopDestroyed(c, true);
                        break;
                    }
                }
            }
            _breakerZones.RemoveAll(b => b.EndTime < Time.time);
        }
    }
}
