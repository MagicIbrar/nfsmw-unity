using System.Collections;
using System.Linq;
using NfsMw.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NfsMw.Tests
{
    /// <summary>Play-mode: a LAN host and client in one process over loopback (discovery, join, start, go, states, finish).</summary>
    public class LanTests
    {
        const int TestPort = 47915;

        [SetUp, TearDown]
        public void Reset()
        {
            GameSession.Lan = null;
            CareerTests.ResetWorld();
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator HostRaceWithRemoteCar()
        {
            new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            new GameObject("Sun", typeof(Light)).GetComponent<Light>().type = LightType.Directional;
            var boot = new GameObject("NFS Most Wanted").AddComponent<GameBootstrap>();
            boot.StartInFrontEnd = true;
            yield return null;
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Host" };
            var host = LanSession.Host("Host", "bmwm3gtr", TestPort + 1);
            var client = LanSession.Client("Remote", "gallardo");
            GameSession.Lan = host;
            float t = 0;
            client.Join(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, TestPort + 1));
            while (host.Peers.Count < 2 && t < 10f) { host.Poll(); client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            Assert.AreEqual(2, host.Peers.Count, "client did not join");

            var ev = GameSession.Career.Bins[15].WorldRaces.First(e => e.Type == NfsMw.Core.RaceType.Sprint || e.Type == NfsMw.Core.RaceType.Circuit);
            GameSession.Mode = GameMode.QuickRace;
            GameSession.QuickCar = new OwnedCar { Car = "bmwm3gtr" };
            host.StartRace(ev.Key);
            GameFlow.StartRace(ev);
            t = 0;
            while (RaceManager.Active == null && t < 120f) { client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            var race = RaceManager.Active;
            Assert.IsNotNull(race, "race did not start");
            var remote = race.Entrants.FirstOrDefault(e => e.Remote);
            Assert.IsNotNull(remote, "no remote car");
            Assert.IsTrue(race.Countdown, "should wait for the remote player");
            client.Poll();
            client.SetReady();
            t = 0;
            while (race.Countdown && t < 20f) { client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsFalse(race.Countdown, "race did not go");

            // the remote player drives along the route
            var route = race.Route.ToList();
            float along = 0, sent = 0;
            var start = remote.Car.transform.position;
            while (along < 300f)
            {
                along += Time.deltaTime * 30f;
                if (Time.time > sent) { sent = Time.time + 0.05f; var pos = PointAt(route, along); client.SendState(pos + Vector3.up * 0.5f, Quaternion.identity, Vector3.zero, along); }
                client.Poll();
                yield return null;
            }
            yield return new WaitForSeconds(0.5f);
            float moved = (remote.Car.transform.position - start).magnitude;
            Debug.Log($"NFSMW lan test: remote car moved {moved:0} m, progress {remote.Progress:0} m, place {remote.Place}");
            Assert.Greater(moved, 150f, "remote car did not follow its states");
            Assert.Greater(remote.Progress, 150f, "remote progress not tracked");
            client.Dispose();
        }

        static Vector3 PointAt(System.Collections.Generic.List<Vector3> path, float d)
        {
            for (int i = 1; i < path.Count; i++)
            {
                float seg = Vector3.Distance(path[i - 1], path[i]);
                if (d <= seg) return Vector3.Lerp(path[i - 1], path[i], d / seg);
                d -= seg;
            }
            return path[path.Count - 1];
        }

        [UnityTest, Timeout(60000)]
        public IEnumerator HostAndClientRace()
        {
            using var host = LanSession.Host("Host", "bmwm3gtr", TestPort);
            using var client = LanSession.Client("Client", "gallardo");
            uint started = 0;
            client.OnStart += k => started = k;

            float t = 0;
            while (client.Hosts.Count == 0 && t < 10f) { client.Discover(TestPort); host.Poll(); client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            Assert.Greater(client.Hosts.Count, 0, "host not found");
            client.Join(client.Hosts[0].EndPoint);
            t = 0;
            while ((client.LocalId < 0 || client.Peers.Count < 2) && t < 10f) { host.Poll(); client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            Assert.AreEqual(1, client.LocalId, "no WELCOME");
            Assert.AreEqual(2, host.Peers.Count, "host roster");
            Assert.AreEqual("gallardo", host.Peers.First(p => p.Id == 1).Car);

            host.StartRace(1234u);
            t = 0;
            while (started == 0 && t < 5f) { host.Poll(); client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            Assert.AreEqual(1234u, started, "START not received");

            host.SetReady();
            Assert.IsFalse(host.Go, "GO before the client is ready");
            client.SetReady();
            t = 0;
            while ((!host.Go || !client.Go) && t < 5f) { host.Poll(); client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(host.Go && client.Go, "GO not reached");

            client.SendState(new Vector3(10, 2, 30), Quaternion.Euler(0, 45, 0), new Vector3(5, 0, 5), 123f);
            host.SendState(new Vector3(-3, 2, 8), Quaternion.identity, Vector3.forward * 20, 77f);
            client.SendFinish(95.5f);
            t = 0;
            var remoteOnHost = host.Peers.First(p => p.Id == 1);
            var hostOnClient = client.Peers.First(p => p.Id == 0);
            while ((remoteOnHost.Progress < 1 || hostOnClient.Progress < 1 || remoteOnHost.FinishTime < 0) && t < 5f) { host.Poll(); client.Poll(); t += Time.unscaledDeltaTime; yield return null; }
            Debug.Log($"NFSMW lan test: host sees client at {remoteOnHost.Position} progress {remoteOnHost.Progress} finish {remoteOnHost.FinishTime}; client sees host progress {hostOnClient.Progress}");
            Assert.AreEqual(123f, remoteOnHost.Progress, 0.5f);
            Assert.AreEqual(77f, hostOnClient.Progress, 0.5f);
            Assert.AreEqual(95.5f, remoteOnHost.FinishTime, 0.01f);
        }
    }
}
