using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// "trafphys [vehicle]": the behaviour mechanics, trailer and part specs of every traffic-pattern vehicle
// (specs/traffic_physics.md). With a vehicle name, dumps that pvehicle and every part it references in full.
static class ProbeTrafficPhysics
{
    static readonly string[] Mechanics =
    {
        "BEHAVIOR_MECHANIC_RIGIDBODY", "BEHAVIOR_MECHANIC_SUSPENSION", "BEHAVIOR_MECHANIC_ENGINE", "BEHAVIOR_MECHANIC_INPUT",
        "BEHAVIOR_MECHANIC_DAMAGE", "BEHAVIOR_MECHANIC_RESET", "BEHAVIOR_MECHANIC_DRAW", "BEHAVIOR_MECHANIC_EFFECTS", "BEHAVIOR_MECHANIC_AUDIO",
    };

    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        if (args.Length > 2 && args[2] == "bounds") { Bounds(root, db, args.Length > 3 ? args[3] : null); return; }
        if (args.Length > 2 && args[2] == "react") { Reactions(db); return; }
        if (args.Length > 2 && args[2] == "specs") { Specs(root, db); return; }
        if (args.Length > 2) { Full(db, args[2]); return; }
        var names = new SortedSet<string>();
        uint pv = AttribDatabase.Hash("pvehicle");
        foreach (var tp in db.OfClass("trafficpattern"))
        {
            if (!tp.TryGet("Vehicles", out int at, out int count, out var def)) continue;
            int stride = def.Size >= 0x18 ? def.Size : 0x18;
            var list = new List<string>();
            for (int i = 0; i < count; i++)
            {
                uint key = Chunks.U32(tp.Source.Data, at + i * stride + 4);
                string n = db.Collections.TryGetValue((pv, key), out var v) ? v.Name : key.ToString("X8");
                list.Add(n);
                names.Add(n);
            }
            Console.WriteLine($"pattern {tp.Name}: {string.Join(", ", list)}");
        }
        // plus a racer and a cop for comparison
        names.Add("bmwm3gtre46"); names.Add("copmidsize");
        foreach (var n in names) Summary(db, n);
        // every distinct mechanic value over all pvehicles
        var counts = new SortedDictionary<string, int>();
        foreach (var c in db.OfClass("pvehicle"))
            foreach (var m in Mechanics)
            {
                string k = m.Substring(18) + "=" + (c.StringKey(m) ?? "-");
                counts[k] = counts.TryGetValue(k, out int x) ? x + 1 : 1;
            }
        foreach (var kv in counts) Console.WriteLine($"  {kv.Key}: {kv.Value}");
    }

    static void Summary(AttribDatabase db, string name)
    {
        var c = db.Get("pvehicle", name);
        if (c == null) { Console.WriteLine($"{name}: no pvehicle"); return; }
        var sb = new System.Text.StringBuilder($"{name} (parent {c.Parent?.Name}) model {c.StringKey("MODEL")} class {c.StringKey("CLASS")} mass {c.Float("MASS"):0}");
        foreach (var m in Mechanics) sb.Append($" {m.Substring(18)}={c.StringKey(m) ?? "-"}");
        if (c.TryGet("BEHAVIOR_ORDER", out _, out int oc, out _))
        {
            var order = new List<string>();
            for (int i = 0; i < oc; i++) order.Add(c.StringKey("BEHAVIOR_ORDER", i) ?? "?");
            sb.Append($" order=[{string.Join(",", order)}]");
        }
        var tr = c.Refs("Trailer");
        if (tr.Length > 0) sb.Append($" Trailer={(db.Collections.TryGetValue(tr[0], out var t) ? t.Name : tr[0].key.ToString("X8"))}");
        if (c.Has("TrafficEngType")) sb.Append($" TrafficEngType={c.Source.Data[TryAt(c, "TrafficEngType")]}");
        Console.WriteLine(sb);
        if (tr.Length > 0 && db.Collections.TryGetValue(tr[0], out var trailer) && trailer.Name != null) Summary(db, trailer.Name);
    }

    /// <summary>The GLOBALB bounds pack: which hash names the collections, and every node of the given models.</summary>
    static void Bounds(string root, AttribDatabase db, string only)
    {
        var bun = GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GLOBALB.BUN"));
        var g = CollisionGeometry.Read(bun);
        Console.WriteLine($"{g.ByName.Count} collections");
        var models = new Dictionary<uint, string>();
        var crcs = new Dictionary<uint, string>();
        foreach (var c in db.OfClass("pvehicle"))
        {
            string m = c.StringKey("MODEL");
            if (m == null) continue;
            models[AttribDatabase.Hash(m)] = m;
            models[AttribDatabase.Hash(m.ToLowerInvariant())] = m.ToLowerInvariant();
            crcs[EaHash.Hash(m)] = m;
        }
        int jen = 0, ea = 0;
        foreach (var kv in g.ByName) { if (models.ContainsKey(kv.Key)) jen++; if (crcs.ContainsKey(kv.Key)) ea++; }
        Console.WriteLine($"names matched: jenkins {jen}, bStringHash {ea}");
        var nodeNames = new Dictionary<uint, string>();
        foreach (var s in new[] { "5THWHEEL", "BODY", "CHASSIS", "HITCH", "CAB", "TRAILER", "FRONT", "REAR", "POST", "PIN", "KING", "KINGPIN", "CONE", "LIMIT", "YAW", "PITCH", "ROLL" })
        { nodeNames[AttribDatabase.Hash(s)] = s; nodeNames[EaHash.Hash(s)] = "ea:" + s; }
        foreach (var kv in g.ByName)
        {
            string name = models.TryGetValue(kv.Key, out var mn) ? mn : kv.Key.ToString("X8");
            if (only != null && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (only == null && !name.StartsWith("SEMI") && !name.StartsWith("TRAILER") && name != "TRAFHA") { Console.WriteLine($"  {name}: {kv.Value.Bounds.Length} bounds"); continue; }
            Console.WriteLine($"== {name}: {kv.Value.Bounds.Length} bounds");
            for (int i = 0; i < kv.Value.Bounds.Length; i++)
            {
                var b = kv.Value.Bounds[i];
                string nn = nodeNames.TryGetValue(b.Name, out var x) ? x : b.Name.ToString("X8");
                Console.WriteLine($"  [{i}] {nn} flags {b.Flags} pos {b.Position} half {b.HalfDims} pivot {b.Pivot} q ({b.Qx:0.###},{b.Qy:0.###},{b.Qz:0.###},{b.Qw:0.###}) kids {b.Children}@{b.ChildIndex} r {b.Radius:0.###} pc {b.PointCloud}");
            }
        }
    }

    /// <summary>aivehicle PlayerCollisions / PlayerCollisionsDefault and every collisionreactions record (AIVehicle.cpp:700-721).</summary>
    static void Reactions(AttribDatabase db)
    {
        foreach (var c in db.OfClass("aivehicle"))
        {
            var sb = new System.Text.StringBuilder($"aivehicle {c.Name}:");
            if (c.TryGet("PlayerCollisionsDefault", out int at, out _, out var def)) sb.Append($" default size {def.Size} = {Chunks.U32(c.Source.Data, at):X8} {Chunks.U32(c.Source.Data, at + 4):X8}");
            if (c.TryGet("PlayerCollisions", out at, out int n, out def))
                for (int i = 0; i < n; i++) sb.Append($" [{Chunks.U32(c.Source.Data, at + i * def.Size):X8} -> {Chunks.U32(c.Source.Data, at + i * def.Size + 4):X8}]");
            Console.WriteLine(sb);
        }
        foreach (var c in db.OfClass("collisionreactions"))
        {
            var sb = new System.Text.StringBuilder($"collisionreactions {c.Name} ({c.Key:X8}):");
            foreach (var f in new[] { "FRONT_REACTION", "FRONTSIDE_REACTION", "REAR_REACTION", "REARSIDE_REACTION" })
                if (c.TryGet(f, out int at, out _, out _))
                    sb.Append($" {f}=({string.Join(",", Enumerable.Range(0, 6).Select(k => Chunks.F32(c.Source.Data, at + 4 * k).ToString("0.###")))})");
            Console.WriteLine(sb);
        }
        Console.WriteLine($"goal hashes: AIGoalTraffic {AttribDatabase.Hash("AIGoalTraffic"):X8}");
    }

    /// <summary>Per traffic vehicle: the chassis numbers the simple models use, the static sag against ride height and
    /// travel, the geometry box, and EngineTraffic's top speed (redline in top gear).</summary>
    static void Specs(string root, AttribDatabase db)
    {
        var g = CollisionGeometry.Read(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GLOBALB.BUN")));
        var names = new[] { "trafha", "traftaxi", "trafnews", "trafpickupa", "traf4dseda", "trafficcoup", "trafminivan", "trafgarb", "traffire", "trafcemtr", "semib", "trailerb", "trailerlog", "copmidsize" };
        AttribCollection Part(AttribCollection pv, string part)
        {
            foreach (var r in pv.Refs(part)) if (db.Collections.TryGetValue(r, out var c)) return c;
            return null;
        }
        foreach (var n in names)
        {
            var pv = db.Get("pvehicle", n);
            if (pv == null) { Console.WriteLine($"{n}: -"); continue; }
            var ch = Part(pv, "chassis"); var ti = Part(pv, "tires"); var tr = Part(pv, "transmission"); var en = Part(pv, "engine"); var br = Part(pv, "brakes");
            float mass = pv.Float("MASS");
            var ride = ch.Pair("RIDE_HEIGHT"); var travel = ch.Pair("TRAVEL"); var k = ch.Pair("SPRING_STIFFNESS"); var prog = ch.Pair("SPRING_PROGRESSION");
            var shock = ch.Pair("SHOCK_STIFFNESS"); var track = ch.Pair("TRACK_WIDTH");
            float wb = ch.Float("WHEEL_BASE"), fa = ch.Float("FRONT_AXLE"), fwb = ch.Float("FRONT_WEIGHT_BIAS"), rc = ch.Float("ROLL_CENTER"), drag = ch.Float("DRAG_COEFFICIENT");
            float Sag(float kk, float p, float load) { kk *= 175.1268f; float x = 0; for (int i = 0; i < 60; i++) x = load / (kk * (1 + p * x)); return x; }
            float lf = mass * 9.81f * 0.5f * fwb * 0.01f, lr = mass * 9.81f * 0.5f * (100 - fwb) * 0.01f;
            string model = pv.StringKey("MODEL");
            string box = g.ByName.TryGetValue(AttribDatabase.Hash(model ?? ""), out var col) ? $"half ({col.Bounds[0].HalfDims}) pivot ({col.Bounds[0].Pivot})" : "no bounds";
            Console.WriteLine($"{n} {model} mass {mass} | ride {ride.front}/{ride.rear}in travel {travel.front}/{travel.rear}in k {k.front}/{k.rear} prog {prog.front}/{prog.rear} shock {shock.front}/{shock.rear} -> sag {Sag(k.front, prog.front, lf):0.000}/{Sag(k.rear, prog.rear, lr):0.000}m vs ride {ride.front * 0.0254f:0.000} travel {travel.front * 0.0254f:0.000}");
            Console.WriteLine($"   track {track.front}/{track.rear} wb {wb} fa {fa} fwb {fwb} rc {rc} drag {drag} | {box}");
            if (ti != null) Console.WriteLine($"   tires {ti.Name}: width {ti.Pair("SECTION_WIDTH")} aspect {ti.Pair("ASPECT_RATIO")} rim {ti.Pair("RIM_SIZE")} static {ti.Pair("STATIC_GRIP")} dyn {ti.Pair("DYNAMIC_GRIP")} gripscale {ti.Pair("GRIP_SCALE")} steering {ti.Float("STEERING")} yaw {ti.Float("YAW_SPEED")}");
            if (tr != null && en != null)
            {
                var gr = tr.Floats("GEAR_RATIO");
                float fin = tr.Float("FINAL_GEAR"), red = en.Float("RED_LINE"), idle = en.Float("IDLE");
                var rim = ti.Pair("RIM_SIZE"); var w = ti.Pair("SECTION_WIDTH"); var asp = ti.Pair("ASPECT_RATIO");
                float rad = rim.front * 0.0254f * 0.5f + w.front * 0.001f * asp.front * 0.01f;
                float top = gr.Length > 0 ? rad * red * 0.10471976f / (gr[gr.Length - 1] * fin) : 0;
                Console.WriteLine($"   trans {tr.Name}: split {tr.Float("TORQUE_SPLIT")} gears [{string.Join(",", gr)}] final {fin} shift {tr.Float("SHIFT_SPEED")} conv {tr.Float("TORQUE_CONVERTER")} | engine {en.Name}: idle {idle} red {red} max {en.Float("MAX_RPM")} fly {en.Float("FLYWHEEL_MASS")} torque [{string.Join(",", en.Floats("TORQUE"))}] -> top gear redline {top:0.0} m/s ({top * 2.237f:0} mph)");
            }
            if (br != null) Console.WriteLine($"   brakes {br.Name}: {br.Pair("BRAKES")} ebrake {br.Float("EBRAKE")}");
        }
    }

    static int TryAt(AttribCollection c, string f) => c.TryGet(f, out int at, out _, out _) ? at : 0;

    static void Full(AttribDatabase db, string name)
    {
        var c = db.Get("pvehicle", name);
        if (c == null) { Console.WriteLine("no pvehicle"); return; }
        Summary(db, name);
        Dump(db, c);
        foreach (var part in new[] { "chassis", "tires", "brakes", "transmission", "engine", "rigidbodyspecs", "damagespecs", "aivehicle" })
            foreach (var r in c.Refs(part))
                if (db.Collections.TryGetValue(r, out var pc)) { Console.WriteLine($"-- {part} {pc.Name}"); Dump(db, pc); }
                else Console.WriteLine($"-- {part} ?{r.key:X8}");
    }

    static void Dump(AttribDatabase db, AttribCollection c)
    {
        if (c.Class == null) return;
        foreach (var f in c.Class.Fields)
        {
            string fn = db.Names.TryGetValue(f.Key, out var n) ? n : f.Key.ToString("X8");
            if (!c.TryGetKey(f.Key, out int at, out int count, out var def)) continue;
            var src = c.Source.Data;
            var vals = new List<string>();
            int words = Math.Max(1, def.Size / 4);
            for (int i = 0; i < Math.Min(count * words, 24); i++)
            {
                if (at + i * 4 + 4 > src.Length) break;
                float fv = BitConverter.ToSingle(src, at + i * 4);
                int iv = BitConverter.ToInt32(src, at + i * 4);
                vals.Add(Math.Abs(fv) > 1e-4 && Math.Abs(fv) < 1e7 ? fv.ToString("0.####") : iv.ToString());
            }
            Console.WriteLine($"   {fn} (size {def.Size} n {count}) = [{string.Join(",", vals)}]");
        }
    }
}
