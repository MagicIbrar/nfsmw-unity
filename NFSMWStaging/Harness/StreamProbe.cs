using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// TrackStreamer probes (NFSMW_RE/specs/world_render.md, "TrackStreamer"):
//   stream info                  sections, loading sections, barriers, prediction zones, set sizes per zone vs the pool
//   stream zone <x> <y> [z] [vx vy]   predicted zone, its streaming set and the loading priorities (game coords)
//   stream sweep <x0> <y0> <x1> <y1> [speed]   drive a straight line: zone switches and sets loaded/unloaded
static class StreamProbe
{
    public static void Run(string root, string[] args)
    {
        var bundle = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        var vis = VisibleSections.Read(bundle);
        var r = TrackStreamerRules.Read(bundle, vis);
        string mode = args.Length > 2 ? args[2] : "info";
        float F(int i, float def) => args.Length > i ? float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture) : def;
        switch (mode)
        {
            case "info": Info(r, vis); break;
            case "zone": Zone(r, F(3, 679.4f), F(4, 2288f), F(5, 154f), F(6, 0), F(7, 0)); break;
            case "pzones": PZones(r); break;
            case "sweep": Sweep(r, F(3, 679.4f), F(4, 2288f), F(5, 2679.4f), F(6, 2288f), F(7, 40f)); break;
        }
    }

    static long SetBytes(TrackStreamerRules r, IEnumerable<int> set) => set.Sum(n => r.ByNumber.TryGetValue(n, out var s) ? (long)s.Size : 0);

    static void Info(TrackStreamerRules r, VisibleSections vis)
    {
        Console.WriteLine($"streaming sections {r.Sections.Count}, total {r.Sections.Sum(s => (long)s.Size) / 1048576.0:0.0} MB, LOD offset {r.LodOffset} (vis {vis.LodOffset})");
        foreach (var g in r.Sections.GroupBy(s => TrackStreamerRules.Letter(s.Number)).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key}: {g.Count(),3} sections, {g.Sum(s => (long)s.Size) / 1048576.0,6:0.0} MB, file types [{string.Join(",", g.Select(s => s.FileType).Distinct())}], priorities [{string.Join(",", g.Select(s => s.SectionPriority).Distinct().OrderBy(x => x).Take(8))}]");
        foreach (var n in TrackStreamerRules.Permanent)
            Console.WriteLine($"  permanent {n}: {(r.ByNumber.TryGetValue(n, out var s) ? s.ToString() : "absent")}");
        int nonzeroSizeDiff = r.Sections.Count(s => s.CompressedSize != s.Size);
        Console.WriteLine($"  compressed != size: {nonzeroSizeDiff}; sample {string.Join("; ", r.Sections.Take(4).Select(s => $"{s.Name} c({s.CentreX:0},{s.CentreY:0}) r{s.Radius:0} perm {s.PermSize}"))}");
        Console.WriteLine($"drivable sections {vis.DrivableOrder.Count}, boundaries {vis.Boundaries.Count} (panorama {vis.Boundaries.Count(b => b.Panorama)})");
        Console.WriteLine($"loading sections {r.LoadingSections.Count}");
        foreach (var l in r.LoadingSections.Take(12)) Console.WriteLine("  " + l);
        Console.WriteLine($"barriers {r.Barriers.Count}, prediction zones {r.PredictionZones.Count}");
        foreach (var z in r.PredictionZones.Take(6))
            Console.WriteLine($"  zone at ({z.PosX:0},{z.PosY:0}) elev {z.Elevation:0.#} data [{string.Join(",", z.Data)}] points {z.Xs.Length}");
        Console.WriteLine($"elevation-gated prediction zones: {r.PredictionZones.Count(z => z.Elevation != 0)}");
        // per-zone set sizes vs the PC pool
        long max = 0; int maxN = 0, maxZone = 0, sumN = 0;
        var pos = new List<StreamingPosition> { new StreamingPosition() };
        foreach (var d in vis.DrivableOrder)
        {
            pos[0].CurrentZone = d;
            var set = r.DetermineStreamingSections(pos);
            long bytes = SetBytes(r, set.Keys);
            sumN += set.Count;
            if (bytes > max) { max = bytes; maxN = set.Count; maxZone = d; }
        }
        Console.WriteLine($"zone sets: avg {sumN / Math.Max(1, vis.DrivableOrder.Count)} sections, largest {maxZone} {maxN} sections {max / 1048576.0:0.0} MB; pool {TrackStreamerRules.MemoryPoolSize / 1048576.0:0.0} MB");
        int withLoading = vis.DrivableOrder.Count(d => r.FindLoadingSection(d) != null);
        Console.WriteLine($"drivable sections inside a loading section: {withLoading}/{vis.DrivableOrder.Count}");
    }

    static void Zone(TrackStreamerRules r, float x, float y, float z, float vx, float vy)
    {
        var e = new StreamingPosition { X = x, Y = y, Elevation = z, VelX = vx, VelY = vy, DirX = vx, DirY = vy, PositionSet = true, FollowingCar = true };
        int here = r.Visibility.FindDrivableSection(x, y);
        int zone = r.GetPredictedZone(e);
        e.CurrentZone = zone;
        var l = zone > 0 ? r.FindLoadingSection(zone) : null;
        Console.WriteLine($"at ({x},{y},{z}) v ({vx},{vy}): drivable {here}, predicted zone {zone}, loading section {(l != null ? l.Name : "-")}");
        var pos = new List<StreamingPosition> { e };
        var set = r.DetermineStreamingSections(pos);
        Console.WriteLine($"set: {set.Count} sections, {SetBytes(r, set.Keys) / 1048576.0:0.0} MB");
        foreach (var n in set.Keys.OrderBy(n => r.ByNumber.TryGetValue(n, out var s) ? r.LoadingPriority(s, pos, 1) : int.MaxValue))
        {
            r.ByNumber.TryGetValue(n, out var s);
            Console.WriteLine($"  {n,5} {(s != null ? s.Name : "?"),-6} phase {TrackStreamerRules.Phase(n)} prio {(s != null ? r.LoadingPriority(s, pos, 1) : -1),7} size {(s != null ? s.Size / 1024 : 0),6} KB");
        }
    }

    // for each prediction zone: from its polygon centroid at 40 m/s in 8 headings, how often the prediction differs
    static void PZones(TrackStreamerRules r)
    {
        int tests = 0, ahead = 0, samples = 0;
        foreach (var z in r.PredictionZones)
        {
            float cx = z.Xs.Average(), cy = z.Ys.Average();
            if (!z.Contains(cx, cy)) continue;
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4;
                var e = new StreamingPosition { X = cx, Y = cy, Elevation = z.Elevation > 0 ? z.Elevation + 1 : 0, VelX = 40 * MathF.Cos(a), VelY = 40 * MathF.Sin(a), PositionSet = true };
                int here = r.Visibility.FindDrivableSection(cx, cy), pz = r.GetPredictedZone(e);
                tests++;
                if (pz != here)
                {
                    ahead++;
                    if (samples++ < 8) Console.WriteLine($"  zone [{string.Join(",", z.Data)}] at ({cx:0},{cy:0}) heading {k * 45}: here {here} -> predicted {pz}");
                }
            }
        }
        Console.WriteLine($"{tests} tests, {ahead} predicted a section ahead");
    }

    static void Sweep(TrackStreamerRules r, float x0, float y0, float x1, float y1, float speed)
    {
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        float dx = (x1 - x0) / len, dy = (y1 - y0) / len;
        var e = new StreamingPosition { PositionSet = true, FollowingCar = true, VelX = dx * speed, VelY = dy * speed, DirX = dx, DirY = dy };
        var pos = new List<StreamingPosition> { e };
        var zones = new int[1];
        HashSet<int> cur = new HashSet<int>();
        int switches = 0;
        for (float t = 0; t <= len; t += speed / 30f)
        {
            e.X = x0 + dx * t; e.Y = y0 + dy * t;
            if (r.DetermineCurrentZones(pos, zones))
            {
                e.CurrentZone = zones[0];
                var set = new HashSet<int>(r.DetermineStreamingSections(pos).Keys);
                int add = set.Count(n => !cur.Contains(n)), rem = cur.Count(n => !set.Contains(n));
                Console.WriteLine($"  {t,6:0} m: zone {zones[0]} (here {r.Visibility.FindDrivableSection(e.X, e.Y)}) set {set.Count} (+{add} -{rem}) {SetBytes(r, set) / 1048576.0:0.0} MB");
                cur = set;
                switches++;
            }
        }
        Console.WriteLine($"{switches} zone switches over {len:0} m");
    }
}
