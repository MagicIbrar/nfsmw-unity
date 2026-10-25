using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// The low-level AI driver shared by racers and cops (AIVehiclePid::OnGasBrake / OnSteering,
    /// AI/Common/AIVehicleRacecar.cpp:197-290, and AIVehicle::OnReverse, AIVehicle.cpp:805): an incremental PID drives
    /// the speed toward the drive speed, an adaptive PID steers the body heading to the drive target. Same tuning as
    /// <see cref="AIRacer"/>; it runs every physics tick on the target and speed the cop's think set.
    /// </summary>
    public sealed class CopDriver
    {
        float _tb;
        readonly PidError _speedErr = new PidError(4, 4, 30f);
        readonly PidError _headingErr = new PidError(5, 5, 30f);
        readonly AdaptivePid _steerPid = new AdaptivePid();
        bool _reversing;

        static readonly float[] HeadingModelX = { 0, 3, 6, 9, 12, 15, 18, 21, 24, 27 };
        static readonly float[] HeadingModelY = { 0, -1, -2.05f, -3.5f, -4.94f, -6.14f, -7.35f, -8.55f, -9.4f, -10f };
        static readonly float[] LowP = { 0.328f, 0.22f, 0.148f, 0.115f, 0.09f, 0.074f, 0.057f, 0.049f, 0.043f, 0.04f };
        static readonly float[] LowI = { 0.21f, 0.244f, 0.267f, 0.29f, 0.305f, 0.321f, 0.328f, 0.336f, 0.341f, 0.344f };
        static readonly float[] LowD = { 0, 0.0758f, 0.0758f, 0.0738f, 0.0604f, 0.047f, 0.047f, 0.0403f, 0.0403f, 0.0403f };

        public void Reset() { _tb = 0; _reversing = false; }

        /// <summary>
        /// One driving tick. <paramref name="reverseOverride"/> = GetUnstuck backing up; <paramref name="allowReverse"/> =
        /// OnReverse (drive flag 4): below 15 m/s a target more than 135° off the nose is reached in reverse.
        /// </summary>
        public DriveInput Drive(PlayerCar car, Rigidbody body, Vector3 target, float driveSpeed, bool nos, bool reverseOverride, bool allowReverse, float dt)
        {
            var input = new DriveInput();
            var fwd = car.transform.forward;
            var pos = car.transform.position;
            float speed = Vector3.Dot(body.linearVelocity, fwd);
            var dir = RoadGraph.FlatDir(target - pos);
            var f = RoadGraph.FlatDir(fwd);
            float maxSteer = Mathf.Max(5f, car.Spec.Steering * 45f);
            if (allowReverse && !reverseOverride && driveSpeed > 0.5f)
            {
                float facing = Vector3.Dot(f, dir);
                if (_reversing && facing > 0) _reversing = false;
                else if (!_reversing && body.linearVelocity.magnitude < 15f && facing < -0.707f) _reversing = true;
            }
            else _reversing = false;
            if (reverseOverride || _reversing)
            {
                float st = Mathf.Asin(Mathf.Clamp(-RoadGraph.Cross2(dir, f), -1f, 1f)) / (maxSteer * Mathf.Deg2Rad);
                input.Steer = Mathf.Clamp(-st, -1f, 1f);
                float want = reverseOverride ? 15f : Mathf.Min(driveSpeed, 15f);
                input.Brake = -speed < want ? 1f : 0.3f;   // the brake at a stop selects reverse (PlayerCar DoAutoReverse)
                return input;
            }
            float e = speed - driveSpeed;
            _speedErr.Record(e, dt);
            if (car.Gear < 0) _tb = 1f;
            else if (driveSpeed < 0.5f) _tb = -1f;
            else if (speed < -1f) _tb = -1f;
            else _tb += -0.4f * e - 0.01f * Mathf.Clamp(_speedErr.Integral, -5f, 5f) - 0.1f * Mathf.Clamp(_speedErr.Derivative, -10f, 10f);
            _tb = Mathf.Clamp(_tb, -1f, 1f);
            input.Throttle = Mathf.Max(_tb, 0);
            input.Brake = Mathf.Max(-_tb, 0);
            input.Nitrous = nos;
            if (!(driveSpeed == 0 && body.linearVelocity.magnitude < 1f))
            {
                float err = Mathf.Asin(Mathf.Clamp(-RoadGraph.Cross2(dir, f), -1f, 1f));
                if (Vector3.Dot(dir, f) < 0) err = err >= 0 ? Mathf.PI - err : -Mathf.PI - err;   // target behind: full lock toward it
                _headingErr.Record(err, dt);
                float P = err, I = Mathf.Clamp(_headingErr.Integral, -0.5f, 0.5f), D = Mathf.Clamp(_headingErr.Derivative, -10f, 10f);
                float v = Mathf.Abs(speed);
                if (v < 10f) _steerPid.Force(Table(LowP, v, 160f), Table(LowI, v, 160f), Table(LowD, v, 160f));
                else
                {
                    float model = Graph(HeadingModelX, HeadingModelY, Mathf.Abs(err) * Mathf.Rad2Deg);
                    float actual = (err * D >= 0 ? Mathf.Abs(D) : -Mathf.Abs(D)) * Mathf.Rad2Deg;
                    _steerPid.Update(model, actual, P, I, D, dt);
                }
                float outv = _steerPid.P * P + _steerPid.I * I + _steerPid.D * D;
                input.Steer = Mathf.Clamp(-outv / (maxSteer * Mathf.Deg2Rad), -1f, 1f);
            }
            return input;
        }

        public static float Table(float[] tab, float x, float max)
        {
            float f = Mathf.Clamp01(x / Mathf.Max(1e-3f, max)) * (tab.Length - 1);
            int i = Mathf.Min((int)f, tab.Length - 2);
            return Mathf.Lerp(tab[i], tab[i + 1], f - i);
        }

        static float Graph(float[] xs, float[] ys, float x)
        {
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) return Mathf.Lerp(ys[i - 1], ys[i], (x - xs[i - 1]) / (xs[i] - xs[i - 1]));
            return ys[ys.Length - 1];
        }
    }

    /// <summary>
    /// A car's AI potentials (AIVehicle top speed, acceleration table and grips; ComputeAccelerationTable PC 0x673810
    /// is not decompiled, so the table is built from the torque curve as in <see cref="AIRacer"/>).
    /// </summary>
    public sealed class Potentials
    {
        public float TopSpeed, StartGrip, EndGrip;
        readonly float[] _accel = new float[10];

        public Potentials(CarSpec s)
        {
            const float LBFT = 1.3558f, RAD2RPM = 9.549297f;
            TopSpeed = Mathf.Max(10f, s.TopSpeed);
            StartGrip = Mathf.Min(s.StaticGripFront, s.StaticGripRear);
            float down = TopSpeed * 2f * s.AeroCoefficient * 1000f;
            EndGrip = StartGrip * (9.81f + down / Mathf.Max(1f, s.Mass)) / 9.81f;
            float r = Mathf.Max(0.2f, s.RimRadius), peak = 0;
            for (float rpm = s.IdleRpm; rpm <= s.RedLine; rpm += 100f) peak = Mathf.Max(peak, s.TorqueAt(rpm));
            float driven = Mathf.Abs(s.TorqueSplit - 0.5f) < 0.45f ? 1f : 0.5f;
            float gripCap = 9.81f * StartGrip * driven;
            for (int i = 0; i < 10; i++)
            {
                float v = TopSpeed * i / 9f, best = 0;
                for (int g = 2; g < s.GearRatios.Length; g++)
                {
                    float ratio = s.GearRatios[g];
                    if (ratio <= 0) continue;
                    float rpm = v / r * ratio * s.FinalGear * RAD2RPM;
                    if (rpm > s.RedLine) continue;
                    float tq = rpm < s.IdleRpm * 2f ? peak * 0.8f : s.TorqueAt(rpm);
                    float eff = g < s.GearEfficiency.Length ? s.GearEfficiency[g] : 1f;
                    best = Mathf.Max(best, tq * LBFT * ratio * s.FinalGear * eff / r);
                }
                _accel[i] = Mathf.Clamp((best - s.DragCoefficient * v * v) / Mathf.Max(1f, s.Mass), 0, gripCap);
            }
        }

        /// <summary>AIVehicle::GetAcceleration: the table over 0..top speed.</summary>
        public float Acceleration(float v) => CopDriver.Table(_accel, Mathf.Abs(v), TopSpeed);

        /// <summary>GetSpeedLimit (AIActionRace.cpp:475): n / max(n/max(0.1,V), 2|κ|), n = g·f1 + √((g·f1)² + 4·g·f0·|κ|).</summary>
        public static float SpeedLimit(float k, float f0, float f1, float top)
        {
            const float g = 9.8f;
            k = Mathf.Abs(k);
            float n = g * f1 + Mathf.Sqrt(g * f1 * g * f1 + 4f * g * f0 * k);
            return n / Mathf.Max(n / Mathf.Max(0.1f, top), 2f * k);
        }

        public float CornerSpeed(float k) => SpeedLimit(k, StartGrip, (EndGrip - StartGrip) / TopSpeed, TopSpeed);
    }

    /// <summary>performance_limiter (AI/Common/AIAction.cpp:20-38): a speed cap that rises at amax and collapses after a hit.</summary>
    public struct PerformanceLimiter
    {
        public float Limit;

        public void Init(float speed) => Limit = speed;

        public void Update(float speed, float vmax, float amax, float dt)
        {
            if (speed <= 0) return;
            if (speed < Limit)
            {
                float t = (Limit - speed) / PoliceData.Kph(5f);
                Limit -= Mathf.Min(1f, dt * t * t) * (Limit - speed);
            }
            Limit = Mathf.Min(vmax, Mathf.Max(speed, Limit) + amax * dt);
        }
    }

    /// <summary>The target-speed delay line (AIActionPursuitOffRoad.cpp:21-114): 16 samples over 2 s.</summary>
    public sealed class SpeedDelay
    {
        const int N = 16;
        const float Span = 2f;
        readonly float[] _v = new float[N];
        int _head, _count;
        float _acc;

        public void Add(float speed, float dt)
        {
            _acc += dt;
            if (_count == 0) { Push(speed); return; }
            while (_acc >= Span / N) { _acc -= Span / N; Push(speed); }
            _v[(_head + N - 1) % N] = speed;   // the newest slot follows the live value
        }

        void Push(float s)
        {
            _v[_head] = s;
            _head = (_head + 1) % N;
            _count = Mathf.Min(N, _count + 1);
        }

        /// <summary>The speed <paramref name="delay"/> seconds ago (clamped to the buffer).</summary>
        public float Sample(float delay)
        {
            if (_count == 0) return 0;
            int back = Mathf.Clamp(Mathf.RoundToInt(delay / (Span / N)), 0, _count - 1);
            return _v[(_head + N - 1 - back) % N];
        }

        /// <summary>reset: every sample set to the given speed.</summary>
        public void Reset(float speed)
        {
            for (int i = 0; i < N; i++) _v[i] = speed;
            _count = N; _head = 0; _acc = 0;
        }
    }

    /// <summary>AISteer helpers (AI/Common/AISteer.cpp).</summary>
    public static class Steer
    {
        /// <summary>
        /// VehicleSeperation (:157-245): from every nearby car, capsule distance d → r²/max(d³,0.1) (static scale r) plus a
        /// closest-approach term relative/(d²/2 + t)² (dynamic scale), capped at 160 km/h. Capsules are the cars' boxes
        /// along their heading [I].
        /// </summary>
        public static Vector3 Separation(PlayerCar me, Rigidbody body, System.Collections.Generic.IEnumerable<PlayerCar> others, float staticScale, float dynamicScale)
        {
            var sum = Vector3.zero;
            var p = me.transform.position;
            var v = body.linearVelocity;
            foreach (var o in others)
            {
                if (!o || o == me) continue;
                var op = o.transform.position;
                var off = p - op; off.y = 0;
                float dist = off.magnitude;
                if (dist > 40f || dist < 1e-3f) continue;
                // capsule-to-capsule: centre distance minus both half lengths along the line, minus the half widths
                float d = Mathf.Max(0.05f, dist - HalfExtent(me, -off) - HalfExtent(o, off));
                var dir = off / dist;
                float push = staticScale * staticScale / Mathf.Max(d * d * d, 0.1f);
                var ob = o.GetComponent<Rigidbody>();
                var rel = (ob ? ob.linearVelocity : Vector3.zero) - v; rel.y = 0;
                float closing = Vector3.Dot(rel, dir);
                if (closing > 0)
                {
                    float t = Mathf.Max(0.05f, d / closing);
                    push += dynamicScale * closing / Mathf.Pow(d * d * 0.5f + t, 2f);
                }
                sum += dir * push;
            }
            float cap = PoliceData.Kph(160f);
            return sum.magnitude > cap ? sum.normalized * cap : sum;
        }

        static float HalfExtent(PlayerCar c, Vector3 towards)
        {
            var box = c.GetComponent<BoxCollider>();
            var size = box ? box.size : new Vector3(1.8f, 1.3f, 4.4f);
            var f = RoadGraph.FlatDir(c.transform.forward);
            var dir = RoadGraph.FlatDir(towards);
            float along = Mathf.Abs(Vector3.Dot(f, dir)), side = Mathf.Abs(RoadGraph.Cross2(dir, f));
            return along * size.z * 0.5f + side * size.x * 0.5f;
        }

        /// <summary>AISteer::Ram: the intercept point solving |pT + vT·t − pC| = |vC|·t (the target when no solution).</summary>
        public static Vector3 Intercept(Vector3 cop, float copSpeed, Vector3 target, Vector3 targetVel)
        {
            var d = target - cop; d.y = 0;
            var vt = targetVel; vt.y = 0;
            float a = Vector3.Dot(vt, vt) - copSpeed * copSpeed, b = 2f * Vector3.Dot(d, vt), c = Vector3.Dot(d, d);
            float t;
            if (Mathf.Abs(a) < 1e-4f) t = b < 0 ? -c / b : 0;
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc < 0) return target;
                float s = Mathf.Sqrt(disc), t1 = (-b - s) / (2f * a), t2 = (-b + s) / (2f * a);
                t = t1 > 0 && t2 > 0 ? Mathf.Min(t1, t2) : Mathf.Max(t1, t2);
            }
            return t > 0 ? target + targetVel * t : target;
        }
    }
}
