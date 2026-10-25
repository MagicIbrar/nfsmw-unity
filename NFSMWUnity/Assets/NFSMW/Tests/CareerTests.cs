using System.Collections;
using System.Linq;
using NfsMw.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NfsMw.Tests
{
    /// <summary>Play-mode: a fresh career enters the world with traffic and police, then runs the first Blacklist race with AI.</summary>
    public class CareerTests
    {
        [SetUp, TearDown]
        public void Reset() => ResetWorld();

        public static void ResetWorld()
        {
            NfsMw.UI.FrontEnd.Shutdown();
            NfsMw.Game.Systems.Shutdown();
            if (NfsMw.Game.RaceManager.Active) Object.Destroy(NfsMw.Game.RaceManager.Active.gameObject);
            if (NfsMw.Game.NisPlayer.Active) Object.Destroy(NfsMw.Game.NisPlayer.Active.gameObject);
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None)) if (go.transform.parent == null) Object.Destroy(go);
            Time.timeScale = 1f;
            NfsMw.Game.GameSession.Mode = NfsMw.Game.GameMode.FrontEnd;
            NfsMw.Game.Profile.Slot = "test";
            NfsMw.Game.NisPlayer.Enabled = true;
        }

        /// <summary>Renders the main camera to a PNG (no UI) for a visual check.</summary>
        public static void Snapshot(string path)
        {
            var cam = Camera.main;
            if (!cam) return;
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            System.IO.Directory.CreateDirectory("Preview");
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(rt);
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator CareerRaceRuns()
        {
            Profile.Slot = "test";
            AIRacer.Log = true;
            var cam = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            new GameObject("Sun", typeof(Light)).GetComponent<Light>().type = LightType.Directional;
            var boot = new GameObject("NFS Most Wanted").AddComponent<GameBootstrap>();
            boot.StartInFrontEnd = true;
            yield return null;
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Test", CareerStarted = true, Cash = 30000 };
            GameSession.Profile.Cars.Add(new OwnedCar { Car = "gti" });
            GameFlow.StartCareer();
            float t = 0;
            while (!boot.Ready && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(boot.Ready, "career world did not load");
            Assert.IsNotNull(Systems.Traffic, "traffic system missing");
            // traffic spawns one car per 0.5 s tick, 150-250 m ahead on streamed collision ground (TrafficSystem)
            int traffic = 0;
            float waited = 0;
            while (waited < 20f && (traffic = Object.FindObjectsByType<TrafficCar>(FindObjectsSortMode.None).Length) == 0)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            Debug.Log($"NFSMW career test: {traffic} traffic cars around the player after {waited:0.0} s");
            Assert.Greater(traffic, 0, "no traffic spawned");

            var events = GameSession.AvailableEvents();
            Assert.Greater(events.Count, 0, "no Blacklist events");
            var ev = events.First(e => e.Start != null);
            GameFlow.StartRace(ev);
            t = 0;
            bool intro = false, shot = false;
            while ((RaceManager.Active == null || RaceManager.Active.Countdown) && t < 120f)
            {
                intro |= RaceManager.Active && RaceManager.Active.Intro && NisPlayer.Active;
                if (NisPlayer.Active && !shot && NisPlayer.Active.Time01 > 0.3f) { shot = true; Snapshot("Preview/nis_intro.png"); }
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            var race = RaceManager.Active;
            Assert.IsNotNull(race, "race did not start");
            Assert.IsTrue(intro, "the intro NIS did not play");
            Assert.GreaterOrEqual(race.Entrants.Count, 2, "no opponents");

            // the player drives with the same AI along the race route
            var auto = boot.Player.gameObject.AddComponent<AIDriver>();
            auto.Car = boot.Player;
            var road = race.RoadRoute(boot.Player.transform.position);
            auto.SetPath(road.Count > 2 ? road : new System.Collections.Generic.List<Vector3>(race.Route));
            boot.Player.UseDeviceInput = false;
            for (int k = 0; k < 7; k++)
            {
                yield return new WaitForSeconds(5f);
                foreach (var e in race.Entrants)
                    if (e.Car) Debug.Log($"NFSMW career test: t{(k + 1) * 5} {e.Name} progress {e.Progress:0} m speed {e.Car.SpeedMps * 2.237f:0} mph gear {e.Car.Gear} rpm {e.Car.Rpm:0} grounded {e.Car.Grounded} in {e.Car.Input.Throttle:0.0}/{e.Car.Input.Brake:0.0}/{e.Car.Input.Steer:0.0} {e.Car.DebugWheels()}");
            }
            foreach (var e in race.Entrants) Debug.Log($"NFSMW career test: {e.Name} place {e.Place} progress {e.Progress:0} m");
            Assert.Greater(race.Entrants.Max(e => e.Progress), 400f, "nobody got far in 35 s");
            Assert.Greater(race.Entrants.Skip(1).Max(e => e.Progress), 250f, "opponents did not drive");
            GameFlow.QuitToMainMenu();
            yield return null;
        }
    }
}
