using System.Collections;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NfsMw.Tests
{
    /// <summary>Play-mode: a new career opens with the prologue (16.1.0, BMW, rolling start), and Mia's call fires.</summary>
    public class StoryTests
    {
        [SetUp, TearDown]
        public void Reset() => CareerTests.ResetWorld();

        [UnityTest, Timeout(300000)]
        public IEnumerator NewCareerStartsPrologue()
        {
            Profile.Slot = "test";
            new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            new GameObject("Sun", typeof(Light)).GetComponent<Light>().type = LightType.Directional;
            var boot = new GameObject("NFS Most Wanted").AddComponent<GameBootstrap>();
            boot.StartInFrontEnd = true;
            yield return null;
            GameSession.Load();
            Story.NewCareer("Test");
            Assert.AreEqual(16, GameSession.Profile.Bin, "career must start in bin 16");
            yield return null;
            StoryCard.SkipAll();
            float t = 0;
            while ((RaceManager.Active == null || RaceManager.Active.Intro) && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            var race = RaceManager.Active;
            Assert.IsNotNull(race, "prologue race did not start");
            Assert.AreEqual("16.1.0", race.Event.EventId);
            Assert.AreEqual("bmwm3gtre46", GameSession.ActiveCar.Car);
            Assert.IsFalse(race.Countdown, "rolling start has no countdown");
            var player = boot.Player;
            // drive on autopilot along the race route (the bootstrap leaves cars with an AIDriver to it)
            player.UseDeviceInput = false;
            var ai = player.gameObject.AddComponent<AIDriver>();
            ai.Car = player;
            var road = race.RoadRoute(player.transform.position);
            ai.SetPath(road.Count > 2 ? road : System.Linq.Enumerable.ToList(race.Route));
            float s0 = player.SpeedMps, y0 = player.transform.position.y, s1 = 0;
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForSeconds(1f);
                if (i == 0)
                {
                    var cam = Camera.main.transform;
                    cam.position = player.transform.position - player.transform.forward * 7f + Vector3.up * 3f;
                    cam.LookAt(player.transform.position + player.transform.forward * 10f);
                    CareerTests.Snapshot("Preview/story_rolling.png");
                }
                var hits = Physics.RaycastAll(player.transform.position + Vector3.up * 1f, player.transform.forward, 15f);
                foreach (var h in hits) Debug.Log($"NFSMW story test: ahead {h.collider.name} ({h.collider.GetType().Name}, layer {h.collider.gameObject.layer}) at {h.distance:0.0} m");
                foreach (var c in Physics.OverlapSphere(player.transform.position, 3f, ~0, QueryTriggerInteraction.Ignore))
                    if (c.transform.root != player.transform.root) Debug.Log($"NFSMW story test: near {c.name} ({c.GetType().Name}, layer {c.gameObject.layer}, rb {(c.attachedRigidbody ? c.attachedRigidbody.name : "none")})");
                Debug.Log($"NFSMW story test: wheels on ground {player.WheelsOnGround}, grounded {player.Grounded}, staging {player.Staging}, skid {player.SkidAmount:0.00}, vel {player.GetComponent<Rigidbody>().linearVelocity}");
                if (i == 0) s1 = player.SpeedMps;
                Debug.Log($"NFSMW story test: t+{i + 1}s speed {player.SpeedMps * 2.237f:0} mph at {player.transform.position}, progress {race.Entrants[0].Progress:0}, gear {player.Gear} rpm {player.Rpm:0} in {player.Input.Throttle}/{player.Input.Brake}/{player.Input.Handbrake} power {player.PowerScale} kin {player.GetComponent<Rigidbody>().isKinematic} intro {race.Intro} nis {NisPlayer.Active} ts {Time.timeScale} fe {NfsMw.UI.FrontEnd.Instance?.IsOpen}");
            }
            Assert.Greater(s1, 15f, $"rolling start lost its speed (started at {s0 * 2.237f:0} mph)");
            Assert.Greater(player.transform.position.y, y0 - 3f, "the car fell through the world at the rolling start");
            // Mia's call
            Assert.IsTrue(race.Event.Triggers.ContainsKey("call_from_mia"), "trigger call_from_mia missing");
            player.Teleport(Coords.ToUnity(race.Event.Triggers["call_from_mia"].Position) + Vector3.up, player.transform.rotation);
            yield return new WaitForSeconds(1f);
            Assert.Contains(10, GameSession.Profile.Inbox, "SMS 10 not sent");
            // the scripted end: fade, race over, next prologue event
            t = 0;
            while (!GameSession.Profile.EventsDone.Contains("16.1.0") && t < 30f) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.Contains("16.1.0", GameSession.Profile.EventsDone, "16.1.0 did not end by script");
        }
    }
}
