using System.Collections;
using System.Linq;
using NfsMw.Game;
using NfsMw.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NfsMw.Tests
{
    /// <summary>Play-mode: a two-player split-screen quick race (both cars on autopilot), two cameras, two HUDs.</summary>
    public class SplitScreenTests
    {
        [SetUp, TearDown]
        public void Reset() => CareerTests.ResetWorld();

        [UnityTest, Timeout(300000)]
        public IEnumerator TwoPlayerRace()
        {
            new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            new GameObject("Sun", typeof(Light)).GetComponent<Light>().type = LightType.Directional;
            var boot = new GameObject("NFS Most Wanted").AddComponent<GameBootstrap>();
            boot.StartInFrontEnd = true;
            yield return null;
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Test" };
            NisPlayer.Enabled = false;
            GameSession.Mode = GameMode.QuickRace;
            GameSession.QuickCar = new OwnedCar { Car = "bmwm3gtr" };
            GameSession.QuickCar2 = new OwnedCar { Car = "gallardo" };
            GameSession.SplitScreen = true;
            var ev = GameSession.Career.Bins[15].WorldRaces.First(e => e.Type == NfsMw.Core.RaceType.Sprint || e.Type == NfsMw.Core.RaceType.Circuit);
            GameFlow.StartRace(ev);
            float t = 0;
            while ((RaceManager.Active == null || RaceManager.Active.Countdown) && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            var race = RaceManager.Active;
            Assert.IsNotNull(race, "race did not start");
            Assert.IsNotNull(boot.Player2, "no player two");
            Assert.AreEqual(2, race.Entrants.Count(e => e.Human), "two humans expected");
            Assert.AreEqual(2, Object.FindObjectsByType<GameHud>(FindObjectsSortMode.None).Length, "two HUDs expected");

            // both humans on autopilot along the route
            foreach (var car in new[] { boot.Player, boot.Player2 })
            {
                var ai = car.gameObject.AddComponent<AIDriver>();
                ai.Car = car;
                var road = race.RoadRoute(car.transform.position);
                ai.SetPath(road.Count > 2 ? road : race.Route.ToList());
            }
            yield return new WaitForSeconds(20f);
            var cams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Where(c => c.enabled).OrderBy(c => c.depth).ToList();
            var rt = new RenderTexture(1280, 720, 24);
            foreach (var c in cams) { c.targetTexture = rt; c.Render(); c.targetTexture = null; }
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            RenderTexture.active = null;
            System.IO.Directory.CreateDirectory("Preview");
            System.IO.File.WriteAllBytes("Preview/splitscreen.png", tex.EncodeToPNG());
            foreach (var e in race.Entrants) Debug.Log($"NFSMW split test: {e.Name} place {e.Place} progress {e.Progress:0} m");
            Assert.Greater(race.Entrants.Where(e => e.Human).Min(e => e.Progress), 150f, "a player did not drive");
            NisPlayer.Enabled = true;
        }
    }
}
