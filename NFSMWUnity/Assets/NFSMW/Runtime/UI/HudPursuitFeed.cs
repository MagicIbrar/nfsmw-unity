using System.Collections.Generic;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// [ours] The IPursuit getters the HUD's PursuitBoard feeder reads (LocalPlayer::UpdateHud, PC 0x6F17F2-0x6F19C8) that
    /// PursuitSystem does not expose itself. A partial of the gameplay owner's class kept with the HUD code (UI/HudWidgets.cs
    /// HudPursuitBoard.Feed) so PursuitSystem.cs is not touched; it only reads the pursuit's own fields.
    /// </summary>
    public sealed partial class PursuitSystem
    {
        /// <summary>AIPursuit::GetNumCopsRemainingInWave (PC 0x433CA0): max(cops required to evade − fully engaged cops
        /// evaded, 1) — the HUD's cop counter.</summary>
        public int NumCopsRemainingInWave => Mathf.Max(_required - _evaded, 1);

        /// <summary>AIPursuit::IsHeliInPursuit (PC 0x419200): a chopper among the pursuit's vehicles [?: the port's helicopter
        /// counts until it starts to leave].</summary>
        public bool IsHeliInPursuit => InPursuit && _heli && !_heli.Exiting;

        /// <summary>The pursuit's helicopter (the minimap's HELICOPTER_ICON_GROUP), null when there is none.</summary>
        public CopHeli HudHelicopter => _heli;

        readonly HashSet<CopCar> _hudSeenDestroyed = new HashSet<CopCar>();
        (string type, int mult, int rep) _hudLastDestroyed;

        /// <summary>
        /// AIPursuit::GetMostRecentCopDestroyedType / GetCopDestroyedBonusMultiplier / GetMostRecentCopDestroyedRepPoints
        /// (PC 0x433BC0 / 0x433BA0 / 0x433BB0): the last wrecked cop car's type name, the takedown combo (1..3) and its rep
        /// points at the pursuit's heat — read from the cop list (IncNumCopsDestroyed is CopDestroyed here).
        /// </summary>
        public (string type, int mult, int rep) MostRecentCopDestroyed()
        {
            foreach (var c in _cops)
            {
                if (!c || !c.Destroyed || !_hudSeenDestroyed.Add(c)) continue;
                if (c.Type != null) _hudLastDestroyed = (c.Type.Name, Mathf.Max(1, (int)_combo), c.Type.BountyAt(Heat));
            }
            _hudSeenDestroyed.RemoveWhere(c => !c);
            return _hudLastDestroyed;
        }
    }
}
