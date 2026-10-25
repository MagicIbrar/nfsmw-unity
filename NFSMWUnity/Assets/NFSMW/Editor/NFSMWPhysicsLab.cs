using System.Text;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.EditorTools
{
    /// <summary>
    /// Batch handling lab on a flat test pad (no world): launch, braking, steady cornering, slalom, handbrake turn and
    /// speedbreaker runs per car and upgrade level, logged as "NFSMW lab:" lines. Args: -labcars gti,bmwm3gtre46 -labstage 0
    /// </summary>
    public static class NFSMWPhysicsLab
    {
        const float Dt = 1f / PlayerCar.SimRate;
        const float MPH = 0.44703f;
        static bool Trace => System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-labtrace") >= 0;

        public static void Run()
        {
            var args = System.Environment.GetCommandLineArgs();
            string cars = "gti,bmwm3gtre46,911turbo,gallardo,copmidsize";
            int stage = -1;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-labcars") cars = args[i + 1];
                if (args[i] == "-labstage") stage = int.Parse(args[i + 1]);
            }
            var prevMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "LabPad";
            pad.transform.localScale = new Vector3(20000f, 1f, 20000f);
            pad.transform.position = new Vector3(0, -0.5f, 0);
            try
            {
                foreach (var car in cars.Split(','))
                    for (int s = stage < 0 ? 0 : stage; s <= (stage < 0 ? 3 : stage); s += 3)
                        Lab(car.Trim(), s);
            }
            finally
            {
                Object.DestroyImmediate(pad);
                Physics.simulationMode = prevMode;
            }
        }

        static PlayerCar Spawn(string car, int stage)
        {
            var owned = new OwnedCar { Car = car, Engine = stage, Transmission = stage, Tires = stage, Suspension = stage, Brakes = stage, Induction = stage, Nitrous = stage };
            var built = CarBuilder.Build(car.ToUpperInvariant(), Color.gray, null, owned);
            built.Root.AddComponent<Rigidbody>();
            GameBootstrap.AddBodyCollider(built);
            var pc = built.Root.AddComponent<PlayerCar>();
            pc.Setup(Upgrades.Apply(built.Spec, owned), built.Wheels);
            pc.UseDeviceInput = false;
            pc.Teleport(new Vector3(0, 0.3f, 0), Quaternion.identity);
            for (int i = 0; i < 240; i++) Tick(pc);        // settle
            return pc;
        }

        static void Tick(PlayerCar pc)
        {
            pc.Step(Dt);
            Physics.Simulate(Dt);
        }

        static void Kill(PlayerCar pc) => Object.DestroyImmediate(pc.gameObject);

        /// <summary>Drives straight with a speed hold until `mph` is reached (or 40 s).</summary>
        static void SpeedUp(PlayerCar pc, float mph)
        {
            for (int i = 0; i < 40 * 120 && pc.ForwardSpeed < mph * MPH; i++)
            {
                pc.Input = new DriveInput { Throttle = 1, Steer = HoldLine(pc) };
                Tick(pc);
            }
        }

        /// <summary>Keeps the car on the +Z line (x = 0, heading 0) while accelerating.</summary>
        static float HoldLine(PlayerCar pc)
        {
            var t = pc.transform;
            float head = Mathf.DeltaAngle(0, t.eulerAngles.y);
            return Mathf.Clamp(-t.position.x * 0.05f - head * 0.08f, -1f, 1f);
        }

        static float Beta(PlayerCar pc)
        {
            var vl = pc.transform.InverseTransformDirection(pc.GetComponent<Rigidbody>().linearVelocity);
            return vl.z > 1f ? Mathf.Atan2(vl.x, vl.z) * Mathf.Rad2Deg : 0;
        }

        static void Lab(string car, int stage)
        {
            var log = new StringBuilder();
            void L(string s) => Debug.Log($"NFSMW lab: {car} s{stage} {s}");

            // ride and setup
            var pc = Spawn(car, stage);
            var spec = pc.Spec;
            L($"mass {spec.Mass:0} kg torque {spec.Torque[0]:0}..{Mathf.Max(spec.Torque):0} lb-ft redline {spec.RedLine:0} gears {spec.ForwardGears} final {spec.FinalGear:0.00} split {spec.TorqueSplit:0.00} grip {spec.StaticGripFront:0.00}/{spec.StaticGripRear:0.00} top(gearing) {spec.TopSpeed / MPH:0} mph cd {spec.DragCoefficient:0.00} aero {spec.AeroCoefficient:0.00} shift {spec.ShiftSpeed:0.00}s ebrake {spec.EBrake:0} brakes {spec.BrakeFront:0}/{spec.BrakeRear:0} lock {spec.BrakeLockFront:0.0}/{spec.BrakeLockRear:0.0} nos {spec.NosCapacity:0.0}s x{1 + spec.NosTorqueBoost:0.00} yawspeed {spec.YawSpeed:0.00} yc [{string.Join(",", spec.YawControl)}]");
            L($"rest y {pc.transform.position.y:0.000} pitch {Mathf.DeltaAngle(0, pc.transform.eulerAngles.x):0.00} roll {Mathf.DeltaAngle(0, pc.transform.eulerAngles.z):0.00} wheels {pc.DebugWheels()}");

            // launch
            var sb = new StringBuilder();
            float t60 = -1, t100 = -1, q = -1, top = 0;
            int gear = pc.Gear;
            float maxSpin = 0;
            for (int i = 0; i < 40 * 120; i++)
            {
                pc.Input = new DriveInput { Throttle = 1, Steer = HoldLine(pc) };
                Tick(pc);
                float t = (i + 1) * Dt, mph = pc.ForwardSpeed / MPH;
                if (t60 < 0 && mph >= 60) t60 = t;
                if (t100 < 0 && mph >= 100) t100 = t;
                if (q < 0 && pc.transform.position.z >= 402.3f) q = t;
                top = Mathf.Max(top, mph);
                if (pc.Gear != gear) { sb.Append($"{gear}>{pc.Gear}@{t:0.0}s/{mph:0}mph "); gear = pc.Gear; }
                if (i % 120 == 0 && i < 3 * 120) sb.Append($"[t{t:0} {mph:0}mph {pc.Rpm:0}rpm g{pc.Gear} {pc.DebugWheels()}] ");
            }
            L($"launch 0-60 {t60:0.00}s 0-100 {t100:0.00}s quarter {q:0.00}s top(40s) {top:0} mph | {sb}");
            Kill(pc);

            // brakes 60-0
            pc = Spawn(car, stage);
            SpeedUp(pc, 60);
            float z0 = pc.transform.position.z, tb = 0;
            bool locked = false;
            for (int i = 0; i < 20 * 120 && pc.ForwardSpeed > 0.3f; i++)
            {
                pc.Input = new DriveInput { Brake = 1, Steer = HoldLine(pc) };
                Tick(pc);
                tb += Dt;
                if (pc.SkidAmount >= 1f) locked = true;
            }
            L($"brake 60-0 {pc.transform.position.z - z0:0.0} m in {tb:0.00}s{(locked ? " (lock-up)" : "")} pitch {Mathf.DeltaAngle(0, pc.transform.eulerAngles.x):0.0}");
            Kill(pc);

            // steady cornering at 40 / 70 mph, full lock, speed held
            foreach (float mphTarget in new[] { 40f, 70f })
            {
                pc = Spawn(car, stage);
                SpeedUp(pc, mphTarget);
                float maxLat = 0, maxBeta = 0, yaw0 = pc.transform.eulerAngles.y, rate = 0;
                var rb = pc.GetComponent<Rigidbody>();
                for (int i = 0; i < 4 * 120; i++)
                {
                    float err = mphTarget * MPH - pc.SpeedMps;
                    pc.Input = new DriveInput { Throttle = Mathf.Clamp01(err * 0.5f + 0.3f), Steer = 1f };
                    var v0 = rb.linearVelocity;
                    Tick(pc);
                    var a = (rb.linearVelocity - v0) / Dt;
                    maxLat = Mathf.Max(maxLat, Mathf.Abs(Vector3.Dot(a, pc.transform.right)) / 9.81f);
                    maxBeta = Mathf.Max(maxBeta, Mathf.Abs(Beta(pc)));
                    rate = rb.angularVelocity.y * Mathf.Rad2Deg;
                }
                L($"corner {mphTarget:0} mph full lock: lat {maxLat:0.00} g, |beta| max {maxBeta:0.0}, yaw rate {rate:0} deg/s, end speed {pc.SpeedMps / MPH:0} mph, roll {Mathf.DeltaAngle(0, pc.transform.eulerAngles.z):0.0}{(maxBeta > 60 ? " SPUN" : "")}");
                Kill(pc);
            }

            // slalom at 80 mph
            {
                pc = Spawn(car, stage);
                SpeedUp(pc, 80);
                float maxBeta = 0;
                for (int i = 0; i < 6 * 120; i++)
                {
                    float t = i * Dt;
                    pc.Input = new DriveInput { Throttle = 1, Steer = Mathf.Sign(Mathf.Sin(t * Mathf.PI * 0.8f)) };
                    Tick(pc);
                    maxBeta = Mathf.Max(maxBeta, Mathf.Abs(Beta(pc)));
                }
                L($"slalom 80 mph: |beta| max {maxBeta:0.0}, end speed {pc.SpeedMps / MPH:0} mph{(maxBeta > 60 ? " SPUN" : "")}");
                Kill(pc);
            }

            // handbrake turn at 60 mph
            {
                pc = Spawn(car, stage);
                SpeedUp(pc, 60);
                float h0 = pc.transform.eulerAngles.y, maxBeta = 0, maxDrift = 0, h15 = 0;
                for (int i = 0; i < 4 * 120; i++)
                {
                    float t = i * Dt;
                    bool hb = t < 0.7f;
                    pc.Input = new DriveInput { Throttle = hb ? 0 : 1, Handbrake = hb, Steer = t < 1.2f ? 1 : 0 };
                    Tick(pc);
                    maxBeta = Mathf.Max(maxBeta, Mathf.Abs(Beta(pc)));
                    maxDrift = Mathf.Max(maxDrift, pc.Drift);
                    if (i == 180) h15 = Mathf.DeltaAngle(h0, pc.transform.eulerAngles.y);
                    if (Trace && i % 12 == 0) L($"  hb t{t:0.0} beta {Beta(pc):0.0} yaw {pc.GetComponent<Rigidbody>().angularVelocity.y * Mathf.Rad2Deg:0} spd {pc.SpeedMps / MPH:0} {pc.DebugWheels()}");
                }
                L($"handbrake turn 60 mph: heading change {h15:0} at 1.5 s, {Mathf.DeltaAngle(h0, pc.transform.eulerAngles.y):0} at 4 s, |beta| max {maxBeta:0.0}, drift {maxDrift:0.00}, end speed {pc.SpeedMps / MPH:0} mph, speedbreaker {pc.SpeedBreaker:0.00}");
                Kill(pc);
            }

            // speedbreaker: 90 mph, on, full lock for 2 s
            {
                pc = Spawn(car, stage);
                SpeedUp(pc, 90);
                pc.SpeedBreaker = 1f;
                pc.ToggleSpeedBreaker();
                bool on = pc.SpeedBreakerOn;
                float h0 = pc.transform.eulerAngles.y;
                for (int i = 0; i < 2 * 120; i++)
                {
                    pc.Input = new DriveInput { Throttle = 1, Steer = 1 };
                    Tick(pc);
                }
                L($"speedbreaker: on {on}, charge after 2 s {pc.SpeedBreaker:0.00}, heading change {Mathf.DeltaAngle(h0, pc.transform.eulerAngles.y):0}, speed {pc.SpeedMps / MPH:0} mph");
                PlayerCar.ResetTime();
                Kill(pc);
            }
        }
    }
}
