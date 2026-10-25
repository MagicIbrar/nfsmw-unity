using System.Collections;
using System.Linq;
using NfsMw.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NfsMw.Tests
{
    /// <summary>Play-mode: the first Challenge Series tollbooth (clock + time bonuses) and pursuit challenge (cops on their markers).</summary>
    public class ChallengeTests
    {
        [SetUp, TearDown]
        public void Reset() => CareerTests.ResetWorld();

        [UnityTest, Timeout(300000)]
        public IEnumerator ChallengeSeriesRuns()
        {
            Profile.Slot = "test";
            new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            new GameObject("Sun", typeof(Light)).GetComponent<Light>().type = LightType.Directional;
            var boot = new GameObject("NFS Most Wanted").AddComponent<GameBootstrap>();
            boot.StartInFrontEnd = true;
            yield return null;
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Test" };
            NisPlayer.Enabled = false;
            var events = GameSession.ChallengeEvents();
            Assert.Greater(events.Count, 60, "challenge series missing");
            Assert.IsTrue(GameSession.ChallengeUnlocked(events[0]) && !GameSession.ChallengeUnlocked(events[2]), "unlock chain wrong");

            // tollbooth: the clock runs down
            var toll = events.First(e => e.Type == NfsMw.Core.RaceType.Tollbooth);
            GameSession.Mode = GameMode.ChallengeSeries;
            GameSession.QuickCar = new OwnedCar { Car = GameSession.Career.CarForPreset(toll.PlayerCar) };
            GameFlow.StartRace(toll);
            float t = 0;
            while ((RaceManager.Active == null || RaceManager.Active.Countdown) && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            var race = RaceManager.Active;
            Assert.IsNotNull(race, "tollbooth did not start");
            float start = race.TimeLeft;
            yield return new WaitForSeconds(3f);
            Debug.Log($"NFSMW challenge test: tollbooth {toll.EventId} in {GameSession.QuickCar.Car}, clock {start:0.0} -> {race.TimeLeft:0.0}");
            Assert.Greater(start, 0, "no tollbooth clock");
            Assert.Less(race.TimeLeft, start, "clock not running");
            race.Abort();

            // pursuit challenge: cops chase from the start
            var chase = events.First(e => e.Type == NfsMw.Core.RaceType.Pursuit);
            GameSession.QuickCar = new OwnedCar { Car = GameSession.Career.CarForPreset(chase.PlayerCar) };
            GameFlow.StartRace(chase);
            t = 0;
            while ((Systems.Police == null || Systems.Police.Challenge == null) && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsNotNull(Systems.Police?.Challenge, "pursuit challenge did not start");
            yield return new WaitForSeconds(4f);
            Debug.Log($"NFSMW challenge test: pursuit {chase.EventId}, in pursuit {Systems.Police.InPursuit}, heat {Systems.Police.Heat}, cops {Systems.Police.CopCars.Count()}, length {Systems.Police.Stats.Length:0.0}");
            Assert.IsTrue(Systems.Police.InPursuit, "not in pursuit");
            Assert.Greater(Systems.Police.CopCars.Count(), 0, "no cops");
            NisPlayer.Enabled = true;
        }
    }
}
