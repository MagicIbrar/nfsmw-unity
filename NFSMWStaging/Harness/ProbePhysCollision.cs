using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// physcol react|bounds [model]|query x y z|probe: collision data for the physics/world-collision port (physics agent 2).
//   react   collisionreactions records (6 floats each) and every aivehicle's PlayerCollisions / PlayerCollisionsDefault
//   bounds  CollisionGeometry root nodes of the vehicle pack (GLOBALB) vs the pvehicle MODEL names
//   query   WCollisionQueries over the packs of the stream section(s) around a game-space point (height, segment hits)
static class ProbePhysCollision
{
    static readonly string[] Records = { "REARSIDE_REACTION", "FRONTSIDE_REACTION", "FRONT_REACTION", "REAR_REACTION" };
    static readonly string[] Goals =
    {
        "AIGoalTraffic", "AIGoalRacer", "AIGoalPursuit", "AIGoalPatrol", "AIGoalRam", "AIGoalHeadOnRam", "AIGoalPit",
        "AIGoalBoxIn", "AIGoalStaticRoadBlock", "AIGoalFleePursuit", "AIGoalNone", "AIGoalHassle", "AIGoalEncounterPursuit",
        "AIGoalPursuitEncounter", "AIGoalCrossTrafficPursuit", "AIGoalPursuitCollapse", "AIGoalGetAway", "AIGoalTrafficRacer",
        "AIGoalDragRace", "AIGoalStage", "AIGoalHeliPursuit", "AIGoalHeliExit", "AIGoalTooDamaged", "AIGoalFollowLeader",
        "AIGoalSpikeStrip", "AIGoalHerd", "AIGoalBlockade", "AIGoalRoadBlock", "AIGoalCollapse", "AIGoalBullsEye",
        "AIGoalPursuitBullsEye", "AIGoalDrafting", "AIGoalHeadOn", "AIGoalEncounter", "AIGoalNIS", "AIGoalNISCop",
    };

    public static void Run(string root, string[] args)
    {
        string what = args.Length > 2 ? args[2] : "react";
        if (what == "react") React(root);
        else if (what == "bounds") Bounds(root, args.Length > 3 ? args[3] : null);
        else if (what == "query") Query(root, args);
        else if (what == "geo") Geo(root, args.Length > 3 ? args[3] : "TRACKS/L2RA.BUN");
        else if (what == "smackgeo") SmackGeo(root);
        else if (what == "lowpoint") LowPoint(root);
        else if (what == "verify") Verify(root, args.Length > 3 ? int.Parse(args[3]) : 2000);
    }

    static void React(string root)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var c in db.OfClass("collisionreactions"))
        {
            var sb = new System.Text.StringBuilder(c.ToString());
            foreach (var f in Records)
                if (c.TryGet(f, out int at, out _, out _))
                    sb.Append($"\n   {f,-19} e {F(c, at)} roll {F(c, at + 4)} bias {F(c, at + 8)} mass {F(c, at + 12)} stunV {F(c, at + 16)} stunT {F(c, at + 20)}");
            Console.WriteLine(sb);
        }
        var goalNames = Goals.ToDictionary(g => AttribDatabase.Hash(g), g => g);
        foreach (var g in Goals) goalNames[Crc32(g)] = "crc:" + g;
        foreach (var c in db.OfClass("aivehicle"))
        {
            var sb = new System.Text.StringBuilder($"aivehicle {c} parent {c.Parent?.Name}");
            if (c.TryGet("PlayerCollisionsDefault", out int dat, out _, out _))
                sb.Append($"  default -> {Name(db, Chunks.U32(c.Source.Data, dat + 4))}");
            if (c.TryGet("PlayerCollisions", out int at, out int n, out _))
                for (int i = 0; i < n; i++)
                {
                    uint goal = Chunks.U32(c.Source.Data, at + i * 16);
                    uint rk = Chunks.U32(c.Source.Data, at + i * 16 + 8);
                    sb.Append($"\n   goal {(goalNames.TryGetValue(goal, out var gn) ? gn : goal.ToString("X8"))} -> {Name(db, rk)}");
                }
            Console.WriteLine(sb);
        }
        foreach (var pv in db.OfClass("pvehicle"))
        {
            var r = pv.Refs("aivehicle");
            if (r.Length > 0) Console.WriteLine($"pvehicle {pv.Name} aivehicle {Name(db, r[0].key)}");
        }
    }

    static string Name(AttribDatabase db, uint key) =>
        db.Collections.Values.FirstOrDefault(x => x.Key == key)?.Name ?? key.ToString("X8");

    static string F(AttribCollection c, int at) => Chunks.F32(c.Source.Data, at).ToString("0.###");

    static uint Crc32(string s)
    {
        // bStringHash-style fallback check [?]: the game's UCrc32(const char*) may be a CRC32
        uint crc = 0xFFFFFFFF;
        foreach (char ch in s)
        {
            crc ^= (byte)ch;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    static void Bounds(string root, string model)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        var geo = CollisionGeometry.Read(File.ReadAllBytes(Path.Combine(root, "GLOBAL", "GLOBALB.BUN")));
        Console.WriteLine($"{geo.ByName.Count} collections");
        var models = new Dictionary<uint, string>();
        foreach (var pv in db.OfClass("pvehicle"))
        {
            var m = pv.StringKey("MODEL");
            if (m != null) models[AttribDatabase.Hash(m)] = m + " (" + pv.Name + ")";
        }
        foreach (var kv in geo.ByName)
        {
            string nm = models.TryGetValue(kv.Key, out var s) ? s : kv.Key.ToString("X8");
            if (model != null && !nm.ToLowerInvariant().Contains(model.ToLowerInvariant())) continue;
            var b = kv.Value.Bounds;
            if (b.Length == 0) continue;
            var r = b[0];
            Console.WriteLine($"{nm}: {b.Length} nodes; root half {r.HalfDims.X:0.###},{r.HalfDims.Y:0.###},{r.HalfDims.Z:0.###} pos {r.Position.X:0.###},{r.Position.Y:0.###},{r.Position.Z:0.###} pivot {r.Pivot.X:0.###},{r.Pivot.Y:0.###},{r.Pivot.Z:0.###} flags {r.Flags} kids {r.Children}@{r.ChildIndex}");
            if (model != null)
                for (int i = 1; i < b.Length; i++)
                    Console.WriteLine($"   [{i}] {b[i].Name:X8} half {b[i].HalfDims.X:0.###},{b[i].HalfDims.Y:0.###},{b[i].HalfDims.Z:0.###} pos {b[i].Position.X:0.###},{b[i].Position.Y:0.###},{b[i].Position.Z:0.###} q {b[i].Qx:0.##},{b[i].Qy:0.##},{b[i].Qz:0.##},{b[i].Qw:0.##} flags {b[i].Flags} kids {b[i].Children}@{b[i].ChildIndex} pc {b[i].PointCloud} r {b[i].Radius:0.##}");
        }
    }

    static void Verify(string root, int n)
    {
        // verify N: GetWorldHeightAtPoint from 1000 m against a brute-force topmost-triangle search over every strip
        var data = File.ReadAllBytes(Path.Combine(root, "TRACKS", "STREAML2RA.BUN"));
        var packs = CollisionPack.ReadAll(data, 0, data.Length);
        var world = new WCollisionQueries();
        foreach (var p in packs) world.Add(p);
        var tris = new List<(Vec3 a, Vec3 b, Vec3 c)>();
        foreach (var p in packs)
            foreach (var inst in p.Instances)
            {
                if (inst.Article == null) continue;
                foreach (var st in inst.Article.Strips)
                    for (int i = 0; i + 2 < st.Verts.Length; i++)
                    {
                        if ((st.Verts[i + 2].SurfaceFlags & 8) != 0) continue;
                        tris.Add((inst.ToWorldSim(st.Verts[i].Local), inst.ToWorldSim(st.Verts[i + 1].Local), inst.ToWorldSim(st.Verts[i + 2].Local)));
                    }
            }
        // a coarse grid over the triangles for the brute force
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            int x0 = (int)Math.Floor(Math.Min(t.a.X, Math.Min(t.b.X, t.c.X)) / 16), x1 = (int)Math.Floor(Math.Max(t.a.X, Math.Max(t.b.X, t.c.X)) / 16);
            int z0 = (int)Math.Floor(Math.Min(t.a.Z, Math.Min(t.b.Z, t.c.Z)) / 16), z1 = (int)Math.Floor(Math.Max(t.a.Z, Math.Max(t.b.Z, t.c.Z)) / 16);
            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) { if (!grid.TryGetValue((x, z), out var l)) grid[(x, z)] = l = new List<int>(); l.Add(i); }
        }
        var rnd = new Random(7);
        int both = 0, onlyQ = 0, onlyB = 0, none = 0, mism = 0, segOk = 0, segBad = 0, rigOk = 0;
        double sw = 0;
        for (int k = 0; k < n; k++)
        {
            var t0 = tris[rnd.Next(tris.Count)];
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble() * (1 - u);
            var pt = new Vec3(t0.a.X + (t0.b.X - t0.a.X) * u + (t0.c.X - t0.a.X) * v, 1000f, t0.a.Z + (t0.b.Z - t0.a.Z) * u + (t0.c.Z - t0.a.Z) * v);
            var w = System.Diagnostics.Stopwatch.StartNew();
            bool q = world.GetWorldHeightAtPoint(pt, out float hq, out _, out _);
            sw += w.Elapsed.TotalMilliseconds;
            float hb = float.NegativeInfinity;
            if (grid.TryGetValue(((int)Math.Floor(pt.X / 16), (int)Math.Floor(pt.Z / 16)), out var cand))
                foreach (int i in cand)
                {
                    var t = tris[i];
                    if (!WCollisionQueries.InTri(pt, t.a, t.b, t.c)) continue;
                    var nn = WCollisionQueries.UpNormal(WCollisionQueries.FaceNormal(t.a, t.b, t.c));
                    float y = WCollisionQueries.GetPlaneY(nn, t.a, pt);
                    if (y > hb) hb = y;
                }
            bool b = !float.IsNegativeInfinity(hb);
            if (q)
            {
                // a vertical segment through the face hits it; a point 1 m under the face finds it rigorously (or a lower one)
                if (world.CheckHitWorld(new Vec3(pt.X, hq + 5f, pt.Z), new Vec3(pt.X, hq - 5f, pt.Z), out var hh, 1) && Math.Abs(hh.Point.Y - hq) < 0.05f) segOk++;
                else if (segBad++ < 5) Console.WriteLine($"  segment missed at sim ({pt.X:0.#},{pt.Z:0.#}) h {hq:0.###} got {(hh.HitSomething ? hh.Point.Y.ToString("0.###") : "none")}");
                if (world.GetWorldHeightAtPointRigorous(new Vec3(pt.X, hq - 1f, pt.Z), out float hr, out _) && hr <= hq + 0.05f) rigOk++;
            }
            if (q && b) { both++; if (Math.Abs(hq - hb) > 0.05f) { mism++; if (mism <= 8) Console.WriteLine($"  mismatch at sim ({pt.X:0.#},{pt.Z:0.#}): query {hq:0.###} brute {hb:0.###}"); } }
            else if (q) onlyQ++; else if (b) { onlyB++; if (onlyB <= 5) Console.WriteLine($"  query missed at sim ({pt.X:0.#},{pt.Z:0.#}) brute {hb:0.###}"); } else none++;
        }
        Console.WriteLine($"{n} points: both {both} (height mismatch {mism}), query only {onlyQ}, brute only {onlyB}, none {none}; {sw / n:0.###} ms per query");
        Console.WriteLine($"segment ±5 m hits the face: {segOk}, missed {segBad}; rigorous from 1 m under: {rigOk}");
    }

    static void LowPoint(string root)
    {
        // lowpoint: per car, how far the collision primitives reach below the model origin vs the chassis ride height
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        var geo = CollisionGeometry.Read(File.ReadAllBytes(Path.Combine(root, "GLOBAL", "GLOBALB.BUN")));
        foreach (var pv in db.OfClass("pvehicle"))
        {
            var m = pv.StringKey("MODEL");
            if (m == null || !geo.ByName.TryGetValue(AttribDatabase.Hash(m), out var col) || col.Bounds.Length == 0) continue;
            float rh = 0;
            foreach (var r in pv.Refs("chassis")) if (db.Collections.TryGetValue(r, out var ch)) { var p2 = ch.Pair("RIDE_HEIGHT"); rh = (p2.front + p2.rear) * 0.5f; break; }
            var root0 = col.Bounds[0];
            float boxBottom = float.MaxValue, sphereBottom = float.MaxValue, boxTop = float.MinValue;
            foreach (var b in col.ChildrenOf(root0))
            {
                bool prim = (b.Flags & (CollisionGeometry.BoundFlags.PrimVsWorld | CollisionGeometry.BoundFlags.PrimVsObjects)) != 0;
                if (!prim) continue;
                float bottom = root0.Pivot.Y + b.Position.Y - b.HalfDims.Y, top = root0.Pivot.Y + b.Position.Y + b.HalfDims.Y;
                if ((b.Flags & CollisionGeometry.BoundFlags.Sphere) != 0) sphereBottom = Math.Min(sphereBottom, bottom);
                else { boxBottom = Math.Min(boxBottom, bottom); boxTop = Math.Max(boxTop, top); }
            }
            Console.WriteLine($"{pv.Name,-16} {m,-14} rideHeight {rh:0.0} in = {rh * 0.0254f:0.000} m; root pivot.y {root0.Pivot.Y:0.000} half.y {root0.HalfDims.Y:0.000}; box bottom {boxBottom:0.000} top {boxTop:0.000}; sphere bottom {(sphereBottom == float.MaxValue ? "-" : sphereBottom.ToString("0.000"))}");
        }
    }

    static void SmackGeo(string root)
    {
        // smackgeo: every SmokeableSpawner's Collision key (+0x24) against the L2RA bounds pack (sub_681540 looks it up)
        var geo = CollisionGeometry.Read(File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN")));
        var data = File.ReadAllBytes(Path.Combine(root, "TRACKS", "STREAML2RA.BUN"));
        var sp = SmokeableSpawner.ReadAll(data, 0, data.Length);
        int found = 0, zero = 0, sameAsModel = 0;
        var missing = new HashSet<uint>();
        foreach (var s in sp)
        {
            if (s.Collision == 0) { zero++; continue; }
            if (s.Collision == s.Model) sameAsModel++;
            if (geo.ByName.TryGetValue(s.Collision, out var c) && c.Bounds.Length > 0) found++; else missing.Add(s.Collision);
        }
        Console.WriteLine($"{sp.Count} spawners: {found} with bounds, {zero} key 0, {missing.Count} missing keys, {sameAsModel} key == model");
        foreach (var m in missing.Take(10)) Console.WriteLine($"  missing {m:X8}");
    }

    static void Geo(string root, string file)
    {
        // geo <file>: CollisionGeometry packs (0x8003B900) in a bundle: collection count and a few names
        var d = File.ReadAllBytes(Path.Combine(root, file));
        var geo = CollisionGeometry.Read(d);
        Console.WriteLine($"{file}: {geo.ByName.Count} collections");
        foreach (var kv in geo.ByName.Take(20))
        {
            var b = kv.Value.Bounds;
            if (b.Length == 0) continue;
            Console.WriteLine($"  {kv.Key:X8}: {b.Length} nodes, root half {b[0].HalfDims} pivot {b[0].Pivot} flags {b[0].Flags}");
        }
    }

    static void Query(string root, string[] args)
    {
        // physcol query <gameX> <gameY> [gameZ]: heights and wall hits from the packs of the stream sections loaded around it
        float gx = float.Parse(args[3]), gy = float.Parse(args[4]), gz = args.Length > 5 ? float.Parse(args[5]) : 1000f;
        var data = File.ReadAllBytes(Path.Combine(root, "TRACKS", "STREAML2RA.BUN"));
        var packs = CollisionPack.ReadAll(data, 0, data.Length);
        var world = new WCollisionQueries();
        foreach (var p in packs) world.Add(p);
        Console.WriteLine($"{packs.Count} packs, {world.InstanceCount} instances");
        // sim = (−gameY, gameZ, gameX)
        var sim = new Vec3(-gy, gz, gx);
        var t0 = DateTime.Now;
        bool ok = world.GetWorldHeightAtPoint(sim, out float h, out var n, out uint surf);
        Console.WriteLine($"height at game ({gx},{gy}) from z {gz}: {(ok ? h.ToString("0.###") : "none")} normal {n.X:0.###},{n.Y:0.###},{n.Z:0.###} surface {surf:X8} ({(DateTime.Now - t0).TotalMilliseconds:0.0} ms)");
        if (ok)
        {
            ok = world.GetWorldHeightAtPointRigorous(new Vec3(sim.X, h + 2f, sim.Z), out float h2, out _);
            Console.WriteLine($"rigorous from h+2: {(ok ? h2.ToString("0.###") : "none")}");
            // a ring of 16 rays 1 m above the ground, 30 m out
            for (int k = 0; k < 16; k++)
            {
                float a = k * MathF.PI / 8f;
                var p0 = new Vec3(sim.X, h + 1f, sim.Z);
                var p1 = new Vec3(sim.X + 30f * MathF.Cos(a), h + 1f, sim.Z + 30f * MathF.Sin(a));
                if (world.CheckHitWorld(p0, p1, out var hit))
                    Console.WriteLine($"  ray {k * 22.5f,5:0.0}°: wall at {hit.Distance:0.##} m normal {hit.Normal.X:0.##},{hit.Normal.Y:0.##},{hit.Normal.Z:0.##} surface {hit.Surface:X8} {(hit.TwoSided ? "2-sided" : "")}");
                else Console.WriteLine($"  ray {k * 22.5f,5:0.0}°: clear");
            }
        }
    }
}
