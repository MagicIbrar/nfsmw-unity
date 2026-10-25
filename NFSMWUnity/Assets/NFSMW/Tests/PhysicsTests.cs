using System.Collections;
using NfsMw.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NfsMw.Tests
{
    /// <summary>The racer sim (PlayerCar, specs/physics.md) on a flat pad, stepped by hand at 120 Hz.</summary>
    public class PhysicsTests
    {
        const float Dt = 1f / PlayerCar.SimRate, MPH = 0.44703f;

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
            for (int i = 0; i < 240; i++) Tick(pc);
            return pc;
        }

        static void Tick(PlayerCar pc)
        {
            pc.Step(Dt);
            Physics.Simulate(Dt);
        }

        static float Hold(PlayerCar pc) =>
            Mathf.Clamp(-pc.transform.position.x * 0.05f - Mathf.DeltaAngle(0, pc.transform.eulerAngles.y) * 0.08f, -1f, 1f);

        static float ZeroTo(PlayerCar pc, float mph, float limit)
        {
            for (int i = 0; i < limit * 120; i++)
            {
                pc.Input = new DriveInput { Throttle = 1, Steer = Hold(pc) };
                Tick(pc);
                if (pc.ForwardSpeed >= mph * MPH) return (i + 1) * Dt;
            }
            return float.MaxValue;
        }

        GameObject _pad;
        SimulationMode _mode;

        [SetUp]
        public void Pad()
        {
            _mode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            _pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _pad.transform.localScale = new Vector3(20000f, 1f, 20000f);
            _pad.transform.position = new Vector3(0, -0.5f, 0);
        }

        [TearDown]
        public void Clear()
        {
            foreach (var pc in Object.FindObjectsByType<PlayerCar>(FindObjectsSortMode.None)) Object.DestroyImmediate(pc.gameObject);
            Object.DestroyImmediate(_pad);
            Physics.simulationMode = _mode;
            PlayerCar.ResetTime();
        }

        [UnityTest]
        public IEnumerator HandlingBasics()
        {
            var pc = Spawn("gti", 0);
            Assert.AreEqual(0f, pc.transform.position.y, 0.05f, "car does not settle at its design ride height");
            float t60 = ZeroTo(pc, 60, 15);
            Debug.Log($"NFSMW physics test: gti 0-60 {t60:0.00}s gear {pc.Gear}");
            Assert.Less(t60, 8f, "GTI 0-60 mph too slow");
            Assert.GreaterOrEqual(pc.Gear, 2, "no upshift");

            // 60-0
            float z0 = pc.transform.position.z;
            for (int i = 0; i < 10 * 120 && pc.ForwardSpeed > 0.3f; i++) { pc.Input = new DriveInput { Brake = 1, Steer = Hold(pc) }; Tick(pc); }
            float stop = pc.transform.position.z - z0;
            Debug.Log($"NFSMW physics test: gti 60-0 {stop:0.0} m");
            Assert.Less(stop, 40f, "brakes too weak");
            Assert.Greater(stop, 8f, "brakes impossibly strong");
            yield return null;

            // handbrake turn at 60 mph
            Object.DestroyImmediate(pc.gameObject);
            pc = Spawn("gti", 0);
            ZeroTo(pc, 60, 15);
            float h0 = pc.transform.eulerAngles.y;
            for (int i = 0; i < 180; i++)
            {
                float t = i * Dt;
                pc.Input = new DriveInput { Handbrake = t < 0.7f, Throttle = t < 0.7f ? 0 : 1, Steer = t < 1.2f ? 1 : 0 };
                Tick(pc);
            }
            float turn = Mathf.DeltaAngle(h0, pc.transform.eulerAngles.y);
            Debug.Log($"NFSMW physics test: gti handbrake turn {turn:0} deg, drift {pc.Drift:0.00}");
            Assert.Greater(turn, 45f, "handbrake turn does not rotate the car");

            // steady full-lock corner at 40 mph stays under control
            Object.DestroyImmediate(pc.gameObject);
            pc = Spawn("gti", 0);
            ZeroTo(pc, 40, 15);
            float maxBeta = 0;
            var rb = pc.GetComponent<Rigidbody>();
            for (int i = 0; i < 3 * 120; i++)
            {
                pc.Input = new DriveInput { Throttle = Mathf.Clamp01((40 * MPH - pc.SpeedMps) * 0.5f + 0.3f), Steer = 1 };
                Tick(pc);
                var vl = pc.transform.InverseTransformDirection(rb.linearVelocity);
                if (vl.z > 1) maxBeta = Mathf.Max(maxBeta, Mathf.Abs(Mathf.Atan2(vl.x, vl.z) * Mathf.Rad2Deg));
            }
            Debug.Log($"NFSMW physics test: gti 40 mph full lock max slip {maxBeta:0.0} deg");
            Assert.Less(maxBeta, 30f, "car spins in a steady corner");
            Assert.Greater(Vector3.Dot(pc.transform.up, Vector3.up), 0.9f, "car rolled over");
        }

        [UnityTest]
        public IEnumerator SpeedBreakerAndUpgrades()
        {
            var pc = Spawn("gti", 0);
            pc.SpeedBreaker = 1f;
            pc.ToggleSpeedBreaker();
            Assert.IsFalse(pc.SpeedBreakerOn, "speedbreaker must need 30 mph");
            ZeroTo(pc, 60, 15);
            pc.ToggleSpeedBreaker();
            Assert.IsTrue(pc.SpeedBreakerOn, "speedbreaker did not engage at 60 mph");
            PlayerCar.TickTimeScale(1f);
            Assert.AreEqual(0.25f, PlayerCar.WorldTimeScale, 0.01f, "world time not slowed");
            for (int i = 0; i < 120; i++) { pc.Input = new DriveInput { Throttle = 1 }; Tick(pc); }
            Assert.AreEqual(0.9f, pc.SpeedBreaker, 0.02f, "speedbreaker should drain 0.1 per second");
            pc.ToggleSpeedBreaker();
            Assert.IsFalse(pc.SpeedBreakerOn);
            PlayerCar.TickTimeScale(2f);
            Assert.AreEqual(1f, PlayerCar.WorldTimeScale, 0.01f, "world time not restored");
            yield return null;

            Object.DestroyImmediate(pc.gameObject);
            var stock = Spawn("gti", 0);
            var top = Spawn("gti", 3);
            top.transform.position += Vector3.right * 20f;
            Assert.Greater(Mathf.Max(top.Spec.Torque), Mathf.Max(stock.Spec.Torque) * 1.5f, "engine upgrades not applied");
            Assert.Greater(top.Spec.StaticGripFront, stock.Spec.StaticGripFront, "tire upgrades not applied");
            Assert.Greater(top.Spec.NosCapacity, 0f, "nitrous upgrade not applied");
            Assert.AreEqual(0f, stock.Spec.NosCapacity, "stock GTI has no nitrous (nos ref 'default')");
        }
    }
}
