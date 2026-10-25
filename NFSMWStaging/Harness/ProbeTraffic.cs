using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// "tcdata": checks for the traffic / world collision port (specs/traffic_collision.md).
static class ProbeTraffic
{
    public static void Run(string root, string[] args)
    {
        var bun = GameFiles.ReadBundle(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        var track = new TrackStream(root);
        int packs = 0, inst = 0, arts = 0, tris = 0, bars = 0, grouped = 0, steep = 0, flag8 = 0, noSceneryHome = 0, sizeBad = 0;
        var groupNums = SceneryGroupNumbers.Read(bun);
        int groupsMissing = 0;
        var homes = new Dictionary<string, int>();
        float minX = 1e9f, maxX = -1e9f, minZ = 1e9f, maxZ = -1e9f;
        var surfCount = new Dictionary<uint, int>();
        foreach (var s in track.Sections)
        {
            var raw = track.ReadRaw(s);
            var list = CollisionPack.ReadAll(raw, 0, raw.Length);
            if (list.Count == 0) continue;
            bool scenery = Scenery.ReadAll(raw, 0, raw.Length).Any(g => g.Instances.Count > 0);
            foreach (var p in list)
            {
                packs++;
                if (!scenery) noSceneryHome++;
                string rel = p.Section == s.Number ? "same" : p.Section + 40 == s.Number ? "+40" : "other";
                homes[rel] = homes.TryGetValue(rel, out int h) ? h + 1 : 1;
                foreach (var i in p.Instances)
                {
                    inst++;
                    if (i.Article != null) arts++;
                    if (i.Group != 0) { grouped++; if (!groupNums.ContainsKey(i.Group)) groupsMissing++; }
                    if (i.Article == null) continue;
                    bars += i.Article.Barriers.Count;
                    foreach (var st in i.Article.Strips)
                        for (int t = 0; t + 2 < st.Verts.Length; t++)
                        {
                            tris++;
                            var a = i.ToWorldSim(st.Verts[t].Local); var b = i.ToWorldSim(st.Verts[t + 1].Local); var c = i.ToWorldSim(st.Verts[t + 2].Local);
                            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                            float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                            if (len > 1e-6f && Math.Abs(ny / len) < 0.7f) steep++;
                            if ((st.Verts[t + 2].SurfaceFlags & 8) != 0) flag8++;
                            uint key = i.Article.SurfaceKey(st.Verts[t + 2].Surface);
                            surfCount[key] = surfCount.TryGetValue(key, out int sc) ? sc + 1 : 1;
                            minX = Math.Min(minX, a.X); maxX = Math.Max(maxX, a.X); minZ = Math.Min(minZ, a.Z); maxZ = Math.Max(maxZ, a.Z);
                        }
                }
            }
        }
        Console.WriteLine($"packs {packs} (homes {string.Join(", ", homes.Select(k => k.Key + " " + k.Value))}; in sections without scenery {noSceneryHome}), instances {inst}, with article {arts}, grouped {grouped} (number not in 0x34109: {groupsMissing})");
        Console.WriteLine($"triangles {tris} (steep {steep}, flag 8 {flag8}), barriers {bars}; sim x {minX:0}..{maxX:0} z {minZ:0}..{maxZ:0}");
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var kv in surfCount.OrderByDescending(k => k.Value).Take(20))
        {
            var c = db.OfClass("simsurface").FirstOrDefault(x => x.Key == kv.Key);
            Console.WriteLine($"  surface {kv.Key:X8} {c?.Name ?? "?"}: {kv.Value} tris, drive {c?.Float("DRIVE_GRIP", 0, -1):0.##} lat {c?.Float("LATERAL_GRIP", 0, -1):0.##} roll {c?.Float("ROLLING_RESISTANCE", 0, -1):0.##}");
        }
        foreach (var o in TrackObb.ReadAll(bun))
            Console.WriteLine($"track obb {o.TypeHash:X8} pos ({o.Matrix[12]:0.0},{o.Matrix[13]:0.0},{o.Matrix[14]:0.0}) dims {o.Dims} row0 ({o.Matrix[0]:0.##},{o.Matrix[1]:0.##},{o.Matrix[2]:0.##})");

        // traffic patterns
        foreach (var p in db.OfClass("trafficpattern"))
        {
            p.TryGet("Vehicles", out int at, out int count, out var def);
            var src = p.Source;
            var recs = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int r = at + i * 0x18;
                uint key = Chunks.U32(src.Data, r + 4);
                var pv = db.OfClass("pvehicle").FirstOrDefault(x => x.Key == key);
                recs.Add($"{pv?.Name ?? key.ToString("X8")} {Chunks.F32(src.Data, r + 12):0}/{Chunks.U32(src.Data, r + 16)}/{Chunks.U32(src.Data, r + 20)} horn {pv?.Int("HornType", 0, -1)}");
            }
            Console.WriteLine($"pattern {p.Name} '{p.Text("CollectionName")}' spawn {p.Float("SpawnTime"):0.#} street {p.Float("SpeedStreet"):0} hwy {p.Float("SpeedHighway"):0} elem {def?.Size}: {string.Join(", ", recs)}");
        }

        // lane profiles: traffic lane offsets
        var net = RoadNetwork.Read(bun);
        int neg = 0, zero = 0, pos = 0, trafficZones = 0;
        foreach (var pr in net.Profiles)
            for (int l = 0; l < pr.Zones; l++)
                if (pr.Lanes[l].Type == 1)
                {
                    trafficZones++;
                    if (pr.Lanes[l].Offset < -0.01f) neg++; else if (pr.Lanes[l].Offset > 0.01f) pos++; else zero++;
                }
        Console.WriteLine($"profiles {net.Profiles.Length}, traffic zones {trafficZones}: raw offset <0 {neg}, ~0 {zero}, >0 {pos}");
        // traffic lanes per travel direction at the destination end (WRoadProfile::GetNumTrafficLanes(forward ^ inverted))
        int dirs = 0, noLane = 0, negLane = 0, lanesTotal = 0;
        for (int si = 0; si < net.Segs.Length; si++)
        {
            var sg = net.Segs[si];
            if ((sg.Flags & (RoadNetwork.SegNoTraffic | RoadNetwork.SegDecision)) != 0) continue;
            foreach (bool fwd in new[] { true, false })
            {
                if (!fwd && (sg.Flags & RoadNetwork.SegOneWay) != 0) continue;
                int node = fwd ? sg.B : sg.A;
                int pi = net.NodeProfile[node];
                if (pi < 0 || pi >= net.Profiles.Length) { noLane++; dirs++; continue; }
                var pr = net.Profiles[pi];
                bool inv = (sg.Flags & (fwd ? RoadNetwork.SegEndInverted : RoadNetwork.SegStartInverted)) != 0;
                bool dir = fwd ^ inv;
                int lanes = dir ? pr.Zones - pr.Middle : pr.Middle;
                int n = 0;
                for (int i = 0; i < lanes; i++)
                {
                    int real = dir ? Math.Min(pr.Middle + i, pr.Zones - 1) : Math.Max(pr.Middle - 1 - i, 0);
                    if (pr.Lanes[real].Type == 1) { n++; if (pr.Lanes[real].Offset < 0) negLane++; }
                }
                dirs++;
                lanesTotal += n;
                if (n == 0) noLane++;
            }
        }
        Console.WriteLine($"traffic directions {dirs}: without a traffic lane {noLane}, lanes {lanesTotal}, with negative raw offset {negLane}");
        foreach (var pr in net.Profiles.Take(6))
            Console.WriteLine($"  zones {pr.Zones} middle {pr.Middle}: " + string.Join(" ", pr.Lanes.Take(pr.Zones).Select(l => $"[{l.Type} {l.Offset:0.0} w{l.Width:0.0}]")));

        // traffic pattern zones (0x80034147 -> 0x3414A)
        foreach (var c in Chunks.FindAll(bun, 0, bun.Length, 0x3414A))
        {
            int o = c.DataStart;
            while (o < c.End && bun[o] == 0x11) o++;
            int n9 = 0;
            for (int guard = 0; o + 0x44 <= c.End && guard < 10000; guard++)
            {
                int type = Chunks.I32(bun, o);
                int size = Chunks.U16(bun, o + 0x42);
                if (type == 9)
                {
                    n9++;
                    Console.WriteLine($"  zone type 9 data {Chunks.U32(bun, o + 0x30):X8} bbox ({Chunks.F32(bun, o + 0x20):0},{Chunks.F32(bun, o + 0x24):0})..({Chunks.F32(bun, o + 0x28):0},{Chunks.F32(bun, o + 0x2C):0}) points {Chunks.U16(bun, o + 0x40)} size {size}");
                }
                if (size <= 0) break;
                o += size;
            }
            Console.WriteLine($"zones chunk @{c.Start:X} size {c.Size}: type-9 {n9}");
        }
        foreach (var name in new[] { "default", "downtown", "collegesouth" }) Console.WriteLine($"bStringHash({name}) = {EaHash.Hash(name):X8}");

        var abk = Path.Combine(root, "SOUND", "IG_GLOBAL", "TRAFFIC_MB.abk");
        if (File.Exists(abk)) Console.WriteLine($"TRAFFIC_MB.abk sounds {SoundBank.Load(abk).Sounds.Count}: " + string.Join(" ", SoundBank.Load(abk).Sounds.Select(x => $"{x.Index}:{x.Samples}@{x.SampleRate}")));
    }
}
