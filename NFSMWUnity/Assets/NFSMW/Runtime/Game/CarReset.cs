using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// ResetCar (Physics/Behaviors/ResetCar.cpp). Its check task runs once a second ("Physics" task, 1.0 s, :96):
    /// <list type="bullet">
    /// <item>TrackState: flipped while not all wheels are down, the body is modelled, up·ground normal &lt; 0.5 and
    /// up.y &lt; 0.5; the flip time adds the task's dT.</item>
    /// <item>CanRecord: the newest spot is 2 m away, the terrain is valid (ValidTerrain(0): the body is over a world face)
    /// and every wheel is down → a cookie {position, forward, flags 1}; up to 4 are kept.</item>
    /// <item>ShouldReset: the human's car with ValidTerrain(2) failing (no world face under the body and two or more
    /// wheels without one: it left the collision), or flipped for more than 4 s.</item>
    /// <item>ResetVehicle: the oldest spot, snapped onto the road (FindNearestRoad: reset lanes, race route while racing,
    /// within 6 m height, not across an enabled barrier, and GetWorldHeightAtPointRigorous there within 3.5 m of the
    /// spot), then ResetTo → IVehicle::SetVehicleOnGround. A failed placement keeps the spots.</item>
    /// </list>
    /// RESET_TO_POINT / GUIDED_RESET zones (CheckZone) do not exist in the PC L2RA data, so they are not checked here.
    /// A reset makes the car invulnerable for 2 s (RBVehicle::SetInvulnerability: it passes through other bodies; the
    /// timer runs once 1 s has passed without such a contact), and a manual reset is refused meanwhile.
    /// </summary>
    public sealed class CarReset : MonoBehaviour
    {
        const float UprightDot = 0.5f, GroundDot = 0.5f, FlipTime = 4f, CookieDist = 2f, CheckPeriod = 1f, MaxResetAltitudeChange = 3.5f, ResetInvulnerability = 2f;

        PlayerCar _car;
        readonly System.Collections.Generic.List<(Vector3 pos, Vector3 dir, int flags)> _cookies = new System.Collections.Generic.List<(Vector3, Vector3, int)>();
        float _flipped, _taskTime;

        public bool HasResetPosition => _cookies.Count > 0;

        void Awake() => _car = GetComponent<PlayerCar>();

        void FixedUpdate()
        {
            if (!_car || _car.Spec == null) return;
            if (NisPlayer.Active) return;
            _taskTime += Time.fixedDeltaTime;
            if (_taskTime < CheckPeriod) return;
            float dt = _taskTime;
            _taskTime = 0f;
            // only vehicles whose mechanic table has a RESET behaviour run it (PVehicle::LookupBehaviorSignature 0x67F0D0:
            // racers' ResetCar; cops, traffic and trailers have none, an animating car none) — was every car
            if (!_car.HasBehavior(Dynamics.VehicleMechanics.Reset)) { _cookies.Clear(); _flipped = 0f; return; }
            Check(dt);
        }

        /// <summary>ResetCar::Check (once per task period).</summary>
        void Check(float dt)
        {
            if (!_car.enabled) _car.WPos.FindClosestFace(transform.position, true);   // the body's WWorldPos when the racer sim is off
            TrackState(dt);
            if (CanRecord())
            {
                _cookies.Add((transform.position, transform.forward, 1));
                if (_cookies.Count > 4) _cookies.RemoveAt(0);
            }
            else if (ShouldReset()) ResetVehicle(false);
        }

        void TrackState(float dt)
        {
            bool flipped = false;
            if (_car.WheelsOnGround < 4 && _car.PhysicsMode == PlayerCar.PhysicsModes.Simulated)
            {
                var up = transform.up;
                // RigidBody::GetGroundNormal: the face under the body (its WWorldPos), straight up without one
                var normal = _car.WPos.OnValidFace ? _car.WPos.Normal : Vector3.up;
                if (Vector3.Dot(up, normal) < GroundDot) flipped = up.y < UprightDot;
            }
            _flipped = flipped ? _flipped + dt : 0;
        }

        bool CanRecord()
        {
            if (_cookies.Count > 0 && (_cookies[_cookies.Count - 1].pos - transform.position).sqrMagnitude < CookieDist * CookieDist) return false;
            if (!_car.ValidTerrain(0)) return false;
            return _car.WheelsOnGround == 4;
        }

        bool ShouldReset()
        {
            if (_cookies.Count == 0) return false;
            bool human = !_car.AiControlled && !_car.Traffic;
            if (human && !_car.ValidTerrain(2)) return true;
            return _flipped > FlipTime;
        }

        /// <summary>ResetVehicle: the oldest spot, snapped to the road (FindNearestRoad), then the car set down there.</summary>
        public bool ResetVehicle(bool manual)
        {
            if (_cookies.Count == 0) return false;
            var cookie = _cookies[0];
            if ((cookie.flags & 1) != 0) FindNearestRoad(ref cookie.pos, ref cookie.dir);
            // a manual reset is refused while the last one's invulnerability lasts (ResetCar.cpp:339)
            if (manual && CarContacts.GetInvulnerability(_car) != CarContacts.InvulnerableNone) return false;
            if (!ResetTo(cookie.pos, cookie.dir, manual)) return false;
            _cookies.Clear();
            _cookies.Add(cookie);
            return true;
        }

        /// <summary>ResetCar::FindNearestRoad (ResetCar.cpp:279-330).</summary>
        static void FindNearestRoad(ref Vector3 pos, ref Vector3 dir)
        {
            var nav = new RoadNav { Drivable = 0x402, Selectable = 0x402, DecisionFilter = true };
            var race = RaceManager.Active;
            nav.RaceFilter = race != null && !race.Countdown;
            nav.InitAtPoint(pos, dir, false, 1f);
            if (!nav.Valid) return;
            nav.SnapToSelectableLane();
            if (!nav.Valid || Mathf.Abs(nav.Position.y - pos.y) > 6f) return;
            // [?] TrackPathBarrier crossing is the road code's (RoadNav); the world height there must be near the spot
            if (!Dynamics.WorldQueries.HeightRigorous(nav.Position, out float h, out _) || Mathf.Abs(pos.y - h) >= MaxResetAltitudeChange) return;
            pos = nav.Position;
            dir = nav.Forward;   // a race-filtered nav already faces the race direction
        }

        /// <summary>ResetCar::ResetTo: IVehicle::SetVehicleOnGround (PVehicle 0x685810).</summary>
        bool ResetTo(Vector3 pos, Vector3 dir, bool manual)
        {
            if (!_car.SetVehicleOnGround(pos, RoadGraph.FlatDir(dir)))
            {
                // [ours] no loaded collision there: the old placement (the streamer may not have the section yet)
                if (Dynamics.WorldQueries.InstanceCount > 0) return false;
                _car.Teleport(pos + Vector3.up * 0.5f, Quaternion.LookRotation(RoadGraph.FlatDir(dir), Vector3.up));
            }
            _flipped = 0;
            // Tweak_ResetInvulnerablitiyTime 2 s of passing through other bodies (the car flashes)
            CarContacts.SetInvulnerability(_car, manual ? CarContacts.InvulnerableManualReset : CarContacts.InvulnerableReset, ResetInvulnerability);
            return true;
        }
    }
}
