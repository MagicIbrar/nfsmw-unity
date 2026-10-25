using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Drag-race steering for the player (AIVehicleHuman::Update / IsDragSteering, AIVehicle.cpp:296-430). Once rolling
    /// (over 1 m/s, no speedbreaker), the steering input only picks a lane: a push left or right moves the drag nav to
    /// the next selectable lane (WRoadNav::ChangeDragLanes). The car then steers itself at the nav point
    /// (HumanDragNavLookAheadTable 8..40 m over 0..100 m/s) with the PID steering, which uses the drag-human
    /// coefficient limits P [0.6, 1.2], I [0.05, 0.5], D [0.02, 0.6].
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class DragSteering : MonoBehaviour
    {
        public PlayerCar Car;
        RoadNav _nav;
        int _lastDir;
        readonly PidError _err = new PidError(5, 5, 30f);
        readonly AdaptivePid _pid = new AdaptivePid { MinP = 0.6f, MaxP = 1.2f, MinI = 0.05f, MaxI = 0.5f, MinD = 0.02f, MaxD = 0.6f };
        const float NavTooFar = 70f;   // fHumanNavTooFar

        void OnEnable()
        {
            if (!Car) Car = GetComponent<PlayerCar>();
            _nav = null;
        }

        bool Reset()
        {
            _nav = new RoadNav { RaceFilter = true, Selectable = RoadGraph.DragSelectable, HalfWidth = 1f };
            if (!_nav.InitAtPoint(Car.transform.position, Car.transform.forward, false, 1f))
            {
                _nav.RaceFilter = false;
                if (!_nav.InitAtPoint(Car.transform.position, Car.transform.forward, false, 1f)) { _nav = null; return false; }
            }
            _nav.ChangeDragLane(0);   // DetermineDragLane: the lane the car is in
            return true;
        }

        void FixedUpdate()
        {
            if (!Car || Car.Spec == null) return;
            var race = RaceManager.Active;
            if (race == null || race.Event == null || race.Event.Type != Core.RaceType.Drag) { _nav = null; return; }
            var body = Car.GetComponent<Rigidbody>();
            float speed = body ? Vector3.Dot(body.linearVelocity, Car.transform.forward) : 0;
            // IsDragSteering: rolling, no speedbreaker (wrong way is not tracked here)
            if (Car.Staging || speed < 1f || Car.SpeedBreakerOn) { _nav = null; return; }
            if (_nav == null && !Reset()) return;
            var pos = Car.transform.position;
            float look = Mathf.Lerp(8f, 40f, Mathf.Clamp01(speed / 100f));
            float d = Vector3.Distance(_nav.Position, pos);
            if (d > NavTooFar) { if (!Reset()) return; d = Vector3.Distance(_nav.Position, pos); }
            if (d < look) _nav.IncNavPosition(look - d, 0);

            // a fresh push left or right changes lane
            float raw = Car.UseDeviceInput ? DriveInput.FromDevices(Car.InputSlot).Steer : Car.Input.Steer;
            int dir = raw < -0.5f ? -1 : raw > 0.5f ? 1 : 0;
            if (dir != 0 && dir != _lastDir) _nav.ChangeDragLane(dir);
            _lastDir = dir;

            // steer at the nav point (AIVehiclePid::OnSteering)
            var target = RoadGraph.FlatDir(_nav.Position - pos);
            var f = RoadGraph.FlatDir(Car.transform.forward);
            float err = Mathf.Asin(Mathf.Clamp(-RoadGraph.Cross2(target, f), -1f, 1f));
            float dt = Time.fixedDeltaTime;
            _err.Record(err, dt);
            float P = err, I = Mathf.Clamp(_err.Integral, -0.5f, 0.5f), D = Mathf.Clamp(_err.Derivative, -10f, 10f);
            float model = AdaptivePid.HeadingModel(Mathf.Abs(err) * Mathf.Rad2Deg);
            _pid.Update(model, (err * D >= 0 ? Mathf.Abs(D) : -Mathf.Abs(D)) * Mathf.Rad2Deg, P, I, D, dt);
            if (_pid.P <= 0) _pid.Force(0.6f, 0.05f, 0.02f);
            float outv = _pid.P * P + _pid.I * I + _pid.D * D;
            float maxSteer = Mathf.Max(5f, Car.Spec.Steering * 45f);
            var input = Car.Input;
            input.Steer = Mathf.Clamp(-outv / (maxSteer * Mathf.Deg2Rad), -1f, 1f);
            Car.Input = input;
        }
    }
}
