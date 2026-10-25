using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NfsMw.Tests
{
    /// <summary>Play-mode: the streamed free roam starts, the car drives off and new sections stream in.</summary>
    public class FreeRoamTests
    {
        [SetUp, TearDown]
        public void Reset()
        {
            NfsMw.UI.FrontEnd.Shutdown();
            NfsMw.Game.Systems.Shutdown();
            if (NfsMw.Game.RaceManager.Active) Object.Destroy(NfsMw.Game.RaceManager.Active.gameObject);
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None)) if (go.transform.parent == null) Object.Destroy(go);
            Time.timeScale = 1f;
            NfsMw.Game.GameSession.Mode = NfsMw.Game.GameMode.FrontEnd;
            NfsMw.Game.Profile.Slot = "test";
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator StreamsAndDrives()
        {
            var cam = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            new GameObject("Sun", typeof(Light)).GetComponent<Light>().type = LightType.Directional;
            var boot = new GameObject("NFS Most Wanted").AddComponent<GameBootstrap>();

            float t = 0;
            while (boot.World == null) yield return null;
            boot.World.Verbose = true;
            while (!boot.Ready && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(boot.Ready, "free roam did not start");
            int loadedAtStart = boot.World.LoadedCount;
            Assert.Greater(loadedAtStart, 5, "too few sections around the start");

            var car = boot.Player;
            car.UseDeviceInput = false;
            yield return new WaitForSeconds(1.5f); // settle on the suspension
            var start = car.transform.position;
            float best = 0;
            for (float d = 0; d < 14f; d += Time.deltaTime)
            {
                car.Input = new DriveInput { Throttle = 1, Steer = AutoSteer(car.transform) };
                best = Mathf.Max(best, car.SpeedMps);
                yield return null;
            }
            float moved = (car.transform.position - start).magnitude;
            Debug.Log($"NFSMW playmode: moved {moved:0} m, top {best * 2.237f:0} mph, sections {loadedAtStart} -> {boot.World.LoadedCount}, building {boot.World.BuildingCount}");
            Assert.Greater(best, 20f, "car never got above 45 mph");
            Assert.Greater(moved, 120f, "car did not get far");
            Assert.Greater(Vector3.Dot(car.transform.up, Vector3.up), 0.7f, "car ended up on its side");
            ScreenCapture.CaptureScreenshot("Preview/playmode_end.png");
            yield return null;
            Object.Destroy(boot.gameObject);
            Object.Destroy(cam);
        }

        static float AutoSteer(Transform t)
        {
            var o = t.position + Vector3.up * 0.6f;
            float Ray(float ang)
            {
                var d = Quaternion.AngleAxis(ang, Vector3.up) * t.forward;
                return Physics.Raycast(o, d, out var h, 60f) ? h.distance : 60f;
            }
            float l = Ray(-25f) + Ray(-12f), r = Ray(25f) + Ray(12f);
            return Mathf.Clamp((r - l) / 40f, -1f, 1f);
        }
    }
}
