using System.Linq;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.EditorTools
{
    /// <summary>Batch probes of the AI road graph (RoadGraph / RoadNav) without a scene.</summary>
    public static class NFSMWRoadProbe
    {
        /// <summary>Batch: the zones at both ends of the segments named in NFSMW_ARGS (default 709,699,5733,716).</summary>
        public static void Zones()
        {
            GameData.Mount();
            var arg = System.Environment.GetEnvironmentVariable("NFSMW_ARGS");
            var ids = (string.IsNullOrEmpty(arg) ? "709,699,5733,716" : arg).Split(',').Select(int.Parse);
            var net = RoadGraph.Net;
            foreach (int i in ids)
            {
                var s = RoadGraph.Segs[i];
                string Z(bool atB)
                {
                    var z = RoadGraph.ZonesAt(s, atB);
                    int node = atB ? s.B : s.A;
                    return $"node {node} prof {net.NodeProfile[node]} " + (z == null ? "none" : string.Join(" ", z.Select(x => $"t{x.Type}@{x.Offset:0.0}w{x.Width:0.0}")));
                }
                Debug.Log($"NFSMW zones: seg {i} flags {s.Flags:X} len {s.Length:0.0} | A: {Z(false)} | B: {Z(true)}");
            }
        }

        /// <summary>Batch: nav init and A* between two points of the first career race route, with step logs.</summary>
        public static void PathProbe()
        {
            GameData.Mount();
            var a = new Vector3(510.7f, 230f, 4549f);
            var b = new Vector3(-317.4f, 241f, 4018.8f);
            var nav = new RoadNav { DecisionFilter = false };
            bool ok = nav.InitAtPoint(a, (b - a).normalized, true, 1f);
            Debug.Log($"NFSMW probe: init {ok} seg {nav.Seg} fwd {nav.Fwd} t {nav.T:0.00} pos {nav.Position}");
            foreach (bool f in new[] { true, false })
            {
                nav.Fwd = f;
                var p = nav.SearchPath(b, false);
                Debug.Log($"NFSMW probe: fwd {f} path {(p == null ? "null" : p.Count.ToString())} {(p != null ? string.Join(",", p.Take(12)) : "")}");
            }
            var goal = new RoadNav { DecisionFilter = false };
            goal.InitAtPoint(b, Vector3.zero, true, 0f);
            Debug.Log($"NFSMW probe: goal seg {goal.Seg} at {goal.Position}");
            var net = RoadGraph.Net;
            var s = RoadGraph.Segs[nav.Seg];
            Debug.Log($"NFSMW probe: seg {s.Index} A {s.A} ({net.NodeSegments[s.A].Count} segs: {string.Join(",", net.NodeSegments[s.A])}) B {s.B} ({net.NodeSegments[s.B].Count} segs: {string.Join(",", net.NodeSegments[s.B])}) flags {s.Flags:X}");
        }
    }
}
