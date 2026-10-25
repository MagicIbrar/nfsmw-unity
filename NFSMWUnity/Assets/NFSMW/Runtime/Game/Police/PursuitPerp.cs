using NfsMw.Core;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The player's AIPerpVehicle side (AI/Common/AIVehicle.cpp:2064-2240, IPerpetrator): the 911 call timer, hiding
    /// spots (TRACK_PATH_ZONE_HIDDEN) and the pursuit-start zones, plus the cop manager's lockout (LockoutCops,
    /// AICopManager.cpp:2245-2268). AIPerpVehicle thinks at Perp_AIUpdateRate 0.5 of the 60 Hz sim (30 Hz,
    /// AIVehicle.cpp:1930-1935).
    /// </summary>
    public sealed partial class PursuitSystem
    {
        /// <summary>kCopLockoutTime (AICopManager.cpp): the lockout outside free roam.</summary>
        const float CopLockoutTime = 60f;
        const float PerpDt = 1f / 30f;

        float _perpAcc, _911CallTimer;
        bool _hiddenFromCars, _hiddenFromHelis, _wasInZoneLastUpdate;
        float _hiddenZoneTimer, _hiddenZoneLatchTime = 0.05f;
        int _pursuitZoneCheck;

        /// <summary>IPerpetrator::IsHiddenFromCars: in a hiding zone long enough (entered unseen).</summary>
        public bool PerpHiddenFromCars => _hiddenFromCars;
        /// <summary>IPerpetrator::IsHiddenFromHelicopters.</summary>
        public bool PerpHiddenFromHelicopters => _hiddenFromHelis;

        /// <summary>AIPerpVehicle::IsPartiallyHidden: 0..1 of the latch time spent in the zone (0 when not hiding).</summary>
        public float PerpHowHidden => _hiddenZoneTimer > 0.07f ? Mathf.Min(1f, _hiddenZoneTimer / _hiddenZoneLatchTime) : 0f;

        /// <summary>IPerpetrator::Get911CallTime: seconds left on the 911 call (≤ 0 = none).</summary>
        public float CallTime911 => _911CallTimer;
        /// <summary>A 911 call is active (Get911CallTime &gt; 0): patrol cars that see the player start a pursuit
        /// (AIVehicleCopCar::CheckForPursuit, :212-221).</summary>
        public bool Active911 => _911CallTimer > 0f;

        /// <summary>The perp's pursuit level (IPerpetrator::GetPursuitLevelAttrib): the pursuit's row, else the row of
        /// the car's heat.</summary>
        public HeatRow PerpRow => InPursuit ? Row : PoliceData.RowFor(Mathf.Clamp(GameSession.ActiveCar?.Heat ?? 1, 1, 6), UseRaceTables);

        /// <summary>ICopMgr::GetLockoutTimeRemaining.</summary>
        public float LockoutTimeRemaining => Mathf.Max(0f, _lockoutUntil - Time.time);

        /// <summary>Free roam: no race and no Challenge Series pursuit (GRaceStatus kPlayMode_Roaming).</summary>
        public bool IsRoaming => RaceManager.Active == null && Challenge == null;

        /// <summary>ICopMgr::AreCopsEnabled.</summary>
        public bool CopsEnabled => Enabled;

        /// <summary>AIPerpVehicle::Set911CallTime: the timer only grows (bMax).</summary>
        public void Set911CallTime(float time) => _911CallTimer = Mathf.Max(time, _911CallTimer);

        /// <summary>
        /// ICopMgr::LockoutCops: on, kCopLockoutTime (60 s) or the perp row's TimeInactiveFor911 in free roam;
        /// off, the lockout and NoNewPursuitsOrCops clear (a 911 call or a pursuit-start zone).
        /// </summary>
        public void LockoutCops(bool lockout)
        {
            if (lockout) _lockoutUntil = Time.time + (IsRoaming ? PerpRow.TimeInactiveFor911 : CopLockoutTime);
            else _lockoutUntil = 0f;
        }

        /// <summary>Runs AIPerpVehicle's part of the update at 30 Hz (called from Update).</summary>
        void PerpTick(float dt)
        {
            _perpAcc += dt;
            int guard = 0;
            while (_perpAcc >= PerpDt && guard++ < 4)
            {
                _perpAcc -= PerpDt;
                PerpUpdate(PerpDt);
            }
        }

        /// <summary>
        /// AIPerpVehicle::Update (:2064-2227): the 911 timer counts down; out of a pursuit every 11th update a
        /// pursuit-start zone under the car posts MForcePursuitStart(heat) after lifting the lockout; hiding zones at the
        /// car's height (elevation 0 or within 1.25 m of the car's bottom) latch after 0.05 s when entered unseen
        /// (evade level ≥ 0.05, or no pursuit), never when entered in sight. Data[0] "Car" hides from cars only,
        /// "Heli" from helicopters only, anything else from both.
        /// </summary>
        void PerpUpdate(float dt)
        {
            _911CallTimer -= dt;
            var player = Player;
            if (!player) return;
            var pos = player.transform.position;
            // myPos.y -= dimension.y: the bottom of the body box [?: half height from the collider]
            float bottom = pos.y + PerpBottomOffset(player);

            bool notSeenRightNow = true;
            if (InPursuit) notSeenRightNow = EvadeLevel >= 0.05f;
            else
            {
                if (--_pursuitZoneCheck < 0)
                {
                    _pursuitZoneCheck = 10;
                    if (CanStartPursuit && IsRoaming && PoliceZones.First(TrackPathZones.PursuitStart, pos) != null)
                    {
                        LockoutCops(false);
                        PursueAtHeatLevel(Mathf.Clamp(GameSession.ActiveCar?.Heat ?? 1, 1, MaxHeat));
                    }
                }
            }

            _hiddenFromCars = _hiddenFromHelis = false;
            int zoneCount = 0;
            foreach (var z in PoliceZones.At(TrackPathZones.Hidden, pos))
            {
                if (!(z.Elevation == 0f || Mathf.Abs(bottom - z.Elevation) < 1.25f)) continue;
                if (!_wasInZoneLastUpdate)
                {
                    _wasInZoneLastUpdate = true;
                    _hiddenZoneLatchTime = notSeenRightNow ? 0.05f : 99999f;
                }
                else if (_hiddenZoneLatchTime > 999f) _hiddenZoneTimer = 0f;
                else _hiddenZoneTimer += dt;
                dt = 0f;   // only the first zone advances the timer
                if (_hiddenZoneTimer > _hiddenZoneLatchTime)
                {
                    if (z.Data[0] == TrackPathZones.CarHash) _hiddenFromCars = true;
                    else if (z.Data[0] == TrackPathZones.HeliHash) _hiddenFromHelis = true;
                    else _hiddenFromCars = _hiddenFromHelis = true;
                }
                zoneCount++;
            }
            if (zoneCount == 0)
            {
                _wasInZoneLastUpdate = false;
                _hiddenZoneTimer = 0f;
            }
        }

        static float PerpBottomOffset(PlayerCar car)
        {
            var box = car.GetComponent<BoxCollider>();
            if (!box) return 0f;
            // the rigid body's dimension.y is a half extent; the body centre is the box centre
            return box.center.y - box.size.y * 0.5f;
        }

        /// <summary>
        /// AICopManager::PursueAtHeatLevel (:2098-2141) for MForcePursuitStart: raise the car's heat to the minimum,
        /// start the player's pursuit if there is none and SpawnPursuitCar (340 m ahead along the road, else
        /// SpawnCopCarNow 15 m behind), or add a car to a pursuit that has none.
        /// </summary>
        public void PursueAtHeatLevel(int minHeat)
        {
            var player = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (!player) return;
            if (Player != player) { Player = player; _playerBody = player.GetComponent<Rigidbody>(); PlayerPotentials = new Potentials(player.Spec); }
            var car = GameSession.ActiveCar;
            if (car != null && car.Heat < minHeat) car.Heat = minHeat;
            if (InPursuit && Heat < minHeat) { Heat = minHeat; HeatValue = Mathf.Max(HeatValue, minHeat); LockInPursuitAttribs(); }
            RefreshAvoidables();
            if (!InPursuit)
            {
                StartPursuit(null);
                SpawnForcedPursuitCar(RandomCopType(Row), SpawnMax - 60f);
                Debug.Log($"NFSMW police: pursuit-start zone, heat {Heat}");
            }
            else if (_pursuers.Count == 0) SpawnForcedPursuitCar(RandomCopType(Row), SpawnMax - 60f);
        }
    }
}
