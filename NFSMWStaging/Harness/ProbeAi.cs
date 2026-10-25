using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// "aiprobe heli": the helicopter flight sheets (Core/HeliSheet.cs) in STREAML2RA.BUN, per stream section;
// "aiprobe heliat <x> <y>": the sheet elevation at a game point (section by FindDrivableSection);
// "aiprobe solid <bundle under root> <name>": a solid's bounds and parts (the GPS arrow MARKER_DIRECTION_AID, InGameA);
// "aiprobe cops": every cop pvehicle's aivehicle TETHER_WEIGHT and damagespecs (specs/ai_cops.md §6.6, §11).
static class ProbeAi
{
    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "heli";
        if (mode == "heli" || mode == "heliat") { Heli(root, args, mode == "heliat"); return; }
        if (mode == "solid") { Solid(root, args[3], args[4]); return; }
        if (mode == "cops") { Cops(root); return; }
        if (mode == "fields") { Fields(root, args[3], args[4]); return; }
        if (mode == "racetables") { RaceTables(root); return; }
        Console.WriteLine("aiprobe heli | heliat x y | solid <bundle> <name> | cops | fields <class> <collection> | racetables");
    }

    static void Heli(string root, string[] args, bool at)
    {
        using var ts = new TrackStream(root);
        var vis = VisibleSections.Read(ts.Bundle);
        int total = 0, secs = 0;
        float zmin = float.MaxValue, zmax = float.MinValue;
        var all = new System.Collections.Generic.Dictionary<int, HeliSheet.Section>();
        foreach (var s in ts.Sections)
        {
            if (s.IsShared || s.Size <= 0) continue;
            var raw = ts.ReadRaw(s);
            foreach (var h in HeliSheet.ReadAll(raw, 0, raw.Length))
            {
                secs++;
                total += h.Polys.Length;
                foreach (var p in h.Polys) for (int i = 0; i < 3; i++) { var v = p.Vertex(i); zmin = Math.Min(zmin, v.Z); zmax = Math.Max(zmax, v.Z); }
                all[h.Number] = h;
                if (!at && secs <= 12) Console.WriteLine($"stream {s.Name} ({s.Size} bytes): heli section {h.Number} polys {h.Polys.Length} drivable {vis.BoundaryOf(h.Number) != null}");
            }
        }
        Console.WriteLine($"heli sections {secs}, polys {total}, z {zmin:0.#}..{zmax:0.#}; drivable sections {vis.DrivableOrder.Count}, without a sheet {vis.DrivableOrder.Count(n => !all.ContainsKey(n))}");
        if (!at) return;
        float x = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), y = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        int sec = vis.FindDrivableSection(x, y);
        all.TryGetValue(sec, out var hs);
        if (HeliSheet.Find(hs, x, y, out var poly)) Console.WriteLine($"({x}, {y}) section {sec}: elevation {HeliSheet.Elevation(poly, x, y, out var n):0.##} normal ({n.X:0.##},{n.Y:0.##},{n.Z:0.##})");
        else Console.WriteLine($"({x}, {y}) section {sec}: no heli poly");
    }

    static void Solid(string root, string bundle, string name)
    {
        var d = GameFiles.ReadBundle(Path.Combine(root, bundle));
        foreach (var so in SolidList.ReadAll(d, 0, d.Length).Where(x => x.Name.Contains(name)))
        {
            Console.WriteLine($"{so.Name} {so.Hash:X8}: min {so.BoundsMin} max {so.BoundsMax} parts {so.Parts.Count} polys {so.NumPolys} tex {string.Join(",", so.TextureHashes.Select(t => t.ToString("X8")))}");
            foreach (var p in so.Parts)
            {
                float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (int i = 0; i < p.VertexCount; i++)
                {
                    x0 = Math.Min(x0, p.Positions[i * 3]); x1 = Math.Max(x1, p.Positions[i * 3]);
                    y0 = Math.Min(y0, p.Positions[i * 3 + 1]); y1 = Math.Max(y1, p.Positions[i * 3 + 1]);
                    z0 = Math.Min(z0, p.Positions[i * 3 + 2]); z1 = Math.Max(z1, p.Positions[i * 3 + 2]);
                }
                Console.WriteLine($"   part verts {p.VertexCount} tris {p.Indices.Length / 3} x {x0:0.##}..{x1:0.##} y {y0:0.##}..{y1:0.##} z {z0:0.##}..{z1:0.##} mat {p.MaterialName} tex {p.TextureHash:X8}");
            }
        }
    }

    static void Cops(string root)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var pv in db.OfClass("pvehicle").Where(c => (c.Name ?? "").StartsWith("cop")).OrderBy(c => c.Name))
        {
            var sb = new System.Text.StringBuilder($"{pv.Name}: damage {pv.StringKey("BEHAVIOR_MECHANIC_DAMAGE")}");
            foreach (var r in pv.Refs("aivehicle"))
                if (db.Collections.TryGetValue(r, out var ai)) sb.Append($" aivehicle {ai.Name ?? ai.Key.ToString("X8")} TETHER_WEIGHT {ai.Float("TETHER_WEIGHT"):0.##}");
            foreach (var r in pv.Refs("damagespecs"))
                if (db.Collections.TryGetValue(r, out var dm))
                {
                    sb.Append($" damagespecs {dm.Name ?? dm.Key.ToString("X8")} FORCE {dm.Float("FORCE"):0.##} HIT_POINTS {dm.Float("HIT_POINTS"):0.##} HP_THRESHOLD {dm.Float("HP_THRESHOLD"):0.##} SUPPRESS_DIST {dm.Float("SUPPRESS_DIST"):0.##} zones");
                    foreach (var z in new[] { "DZ_FRONT", "DZ_REAR", "DZ_LEFT", "DZ_RIGHT", "DZ_LFRONT", "DZ_RFRONT", "DZ_LREAR", "DZ_RREAR", "DZ_TOP", "DZ_BOTTOM" })
                    {
                        var p = dm.Pair(z);
                        sb.Append($" {z.Substring(3)}({p.front:0.##},{p.rear:0.##})");
                    }
                }
            Console.WriteLine(sb);
        }
    }

    // "aiprobe fields <class> <collection>": every field of a collection (name, count, first value as float / int).
    static void Fields(string root, string cls, string coll)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        var c = db.Get(cls, coll);
        if (c == null) { Console.WriteLine($"{cls}/{coll}: missing"); return; }
        Console.WriteLine($"{cls}/{coll}: parent {(c.Parent != null ? c.Parent.Name : "-")} layout {c.Layout} entries {c.Entries.Count}");
        foreach (var f in c.Class.Fields.Values.OrderBy(f => f.Offset))
        {
            string name = db.Names.TryGetValue(f.Key, out var n) ? n : f.Key.ToString("X8");
            if (!c.TryGetKey(f.Key, out int at, out int count, out _)) { Console.WriteLine($"  {name}: unset (type {f.Type:X8} size {f.Size} array {f.IsArray})"); continue; }
            var d = c.Source.Data;
            var sb = new System.Text.StringBuilder($"  {name}: type {f.Type:X8} size {f.Size} count {count} [");
            int n0 = Math.Min(count, f.IsArray ? 12 : 1);
            int stride = f.IsArray ? Math.Max(4, f.Size) : 4;
            for (int i = 0; i < n0; i++)
            {
                int b = at + i * stride;
                sb.Append(i > 0 ? " | " : "");
                int words = Math.Min(stride, 24) / 4;
                for (int w = 0; w < words; w++) sb.Append((w > 0 ? "," : "") + Chunks.F32(d, b + w * 4).ToString("0.###") + "/" + Chunks.I32(d, b + w * 4));
            }
            Console.WriteLine(sb + "]");
        }
    }

    // "aiprobe racetables": pursuitescalation's heat and race tables and the pursuitlevels race_NN rows' main fields
    // (the heat rows of a pursuit during a race, AIPerpVehicle::SetHeat AIVehicle.cpp:2257-2297), plus the cop
    // aivehicles' RepPointsForDestroying per level (AIPursuit::IncNumCopsDestroyed).
    static void RaceTables(string root)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var e in db.OfClass("pursuitescalation"))
        {
            Console.WriteLine($"pursuitescalation {e.Name}:");
            foreach (var f in new[] { "heattable", "racetable", "supporttable", "supportracetable" })
            {
                var keys = e.Keys(f);
                Console.WriteLine($"  {f}: " + string.Join(" ", keys.Select(k => db.Collections.Values.FirstOrDefault(c => c.Key == k)?.Name ?? k.ToString("X8"))));
            }
        }
        foreach (var c in db.OfClass("pursuitlevels").OrderBy(c => c.Name))
        {
            string F(string f) => c.Has(f) ? c.Float(f).ToString("0.##") : "-";
            string I(string f) => c.Has(f) ? c.Int(f).ToString() : "-";
            var cops = new System.Text.StringBuilder();
            if (c.TryGet("Cops", out int at, out int count, out var def))
            {
                int stride = def.Size >= 12 ? def.Size : 12;
                for (int i = 0; i < count; i++)
                {
                    int b = at + i * stride;
                    var sb2 = new System.Text.StringBuilder();
                    for (int w = 0; w < stride / 4; w++) sb2.Append((w > 0 ? "," : "") + Chunks.I32(c.Source.Data, b + w * 4).ToString("X8"));
                    cops.Append($" [{sb2}]");
                }
            }
            Console.WriteLine($"pursuitlevels/{c.Name ?? c.Key.ToString("X8")} (parent {c.Parent?.Name ?? "-"}): Cops{cops} | NumPatrolCars {I("NumPatrolCars")} FullEngagementCopCount {I("FullEngagementCopCount")} NumCopsToTriggerBackup {I("NumCopsToTriggerBackup")} BackupCallTimer {F("BackupCallTimer")} evadetimeout {F("evadetimeout")} TimePerHeatLevel {F("TimePerHeatLevel")} frontLOS {F("frontLOSdistance")} rearLOS {F("rearLOSdistance")} heliLOS {F("HeliLOSdistance")} BustSpeed {F("bustSpeed")} RepPointsPerMinute {I("RepPointsPerMinute")} RoadblockProbability {F("roadblockProbability")} RoadblockSpikeChance {F("RoadblockSpikeChance")} SearchModeRoadblockChance {F("SearchModeRoadblockChance")} SearchModeHeliSpawnChance {F("SearchModeHeliSpawnChance")} TimeBetweenCopSpawn {F("TimeBetweenCopSpawn")} TimeBetweenFirstFourSpawn {F("TimeBetweenFirstFourSpawn")} FullEngagementRadius {F("FullEngagementRadius")} HiddenZoneTimeMultiplier {F("HiddenZoneTimeMultiplier")} CollapseSpeed {F("CollapseSpeed")} MaxCopsCollapsing {I("MaxCopsCollapsing")} StaggerFormationTime {F("StaggerFormationTime")} CopFormations {(c.TryGet("CopFormations", out _, out int nf, out _) ? nf : 0)} DestroyCopBonusTime {F("DestroyCopBonusTime")} MeterDeadZoneBustedDistance {F("MeterDeadZoneBustedDistance")} MeterDeadZoneEvadeDist {F("MeterDeadZoneEvadeDist")} ScaleEscalationPerBucket {(c.TryGet("ScaleEscalationPerBucket", out _, out int nb, out _) ? nb : 0)}");
        }
        foreach (var ai in db.OfClass("aivehicle").OrderBy(c => c.Name))
        {
            if (!(ai.Name ?? "").StartsWith("cop")) continue;
            Console.WriteLine($"aivehicle/{ai.Name}: RepPointsForDestroying [{string.Join(" ", ai.Floats("RepPointsForDestroying").Select(v => v.ToString("0")))}] ints [{(ai.TryGet("RepPointsForDestroying", out int a2, out int n2, out _) ? string.Join(" ", Enumerable.Range(0, n2).Select(i => Chunks.I32(ai.Source.Data, a2 + i * 4))) : "")}] MAXIMUM_AI_SPEED {ai.Float("MAXIMUM_AI_SPEED"):0.#} TopSpeedMultiplier {ai.Float("TopSpeedMultiplier"):0.##} AccelerationMultiplier {ai.Float("AccelerationMultiplier"):0.##}");
        }
    }
}
