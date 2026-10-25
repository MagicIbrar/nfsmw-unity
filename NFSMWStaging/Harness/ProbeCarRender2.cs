using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

/// <summary>
/// Car render probes, round 2 (car_render.md §11-14): the vehicle event sequencer engine ('CopCar' in TRACKS\L2RA.BUN)
/// with every static-data word resolved, the pvehicle StringKey fields (which cars run which engine), the cars'
/// collision bounds (fragment nodes) and the fragment smackable collections.
/// usage: carrender2 seq [engine] [names.txt] | carrender2 keys | carrender2 bounds &lt;MODEL&gt; [names.txt] | carrender2 smack &lt;name|hash&gt;
/// </summary>
static class ProbeCarRender2
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "seq";
        var names = Names(root, args.Skip(3).Where(a => a.EndsWith(".txt")).ToArray());
        switch (sub)
        {
            case "seq": Seq(root, args.Length > 3 && !args[3].EndsWith(".txt") ? args[3] : "CopCar", names); break;
            case "keys": Keys(root, names); break;
            case "bounds": Bounds(root, args[3], names); break;
            case "smack": Smack(root, args[3], names); break;
            case "bbox": BBox(root, args[3], args.Length > 4 ? args[4] : ""); break;
            case "sim": Sim(root, args.Skip(3).Where(a => !a.EndsWith(".txt")).ToArray(), names); break;
        }
    }

    static Dictionary<uint, string> Names(string root, string[] files)
    {
        var d = new Dictionary<uint, string>();
        void Add(string s) { if (!string.IsNullOrEmpty(s)) d[AttribDatabase.Hash(s)] = s; }
        foreach (var f in files) foreach (var l in File.ReadAllLines(f)) Add(l.Trim());
        foreach (var s in new[] { "create", "trigger", "start", "end", "stop", "pause", "resume", "unload", "reset", "RESET_DAMAGE",
            "COLLISION", "COLLISION_VEHICLE", "COLLISION_COP", "COLLISION_PLAYER", "Default", "Damaged", "DESTROYED", "destroyed" }) Add(s);
        for (int i = 1; i <= 6; i++) { Add("DAMAGE_" + i); Add("IMPACT_" + i); }
        foreach (var z in DamageZone.Names) Add(z);
        foreach (var l in new[] { "LIGHT_LHEAD", "LIGHT_RHEAD", "LIGHT_CHEAD", "LIGHT_LBRAKE", "LIGHT_RBRAKE", "LIGHT_CBRAKE", "LIGHT_LREVERSE",
            "LIGHT_RREVERSE", "LIGHT_LRSIGNAL", "LIGHT_RRSIGNAL", "LIGHT_LFSIGNAL", "LIGHT_RFSIGNAL", "LIGHT_COPRED", "LIGHT_COPBLUE", "LIGHT_COPWHITE",
            "LIGHT_COPS", "LIGHT_LSIGNAL", "LIGHT_RSIGNAL", "LIGHT_HEADLIGHTS", "LIGHT_REVERSE", "LIGHT_BRAKELIGHTS" }) Add(l);
        try
        {
            var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
            foreach (var kv in db.Names) d.TryAdd(kv.Key, kv.Value);
            foreach (var c in db.Collections.Values) if (c.Name != null) d.TryAdd(c.Key, c.Name);
        }
        catch { }
        return d;
    }

    static string N(Dictionary<uint, string> names, uint h) => names.TryGetValue(h, out var n) ? n : h.ToString("X8");

    static IEnumerable<EventSeqPack> Packs(string root)
    {
        var d = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        for (int i = 0; i + 8 <= d.Length; i++)
        {
            if (d[i] != 0x11 || d[i + 1] != 0xB8 || d[i + 2] != 0x03 || d[i + 3] != 0) continue;
            int size = BitConverter.ToInt32(d, i + 4);
            if (size <= 0 || i + 8 + size > d.Length) continue;
            EventSeqPack p = null;
            try { p = EventSeqPack.Read(d, i + 8, i + 8 + size); } catch { }
            if (p != null) yield return p;
        }
    }

    static void Seq(string root, string engine, Dictionary<uint, string> names)
    {
        foreach (var pack in Packs(root))
            foreach (var e in pack.Engines)
            {
                if (!string.Equals(e.Name, engine, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine($"engine '{e.Name}' ({AttribDatabase.Hash(e.Name):X8}) systems {e.Systems.Length}");
                for (int si = 0; si < e.Systems.Length; si++)
                {
                    var sys = e.Systems[si];
                    if (sys == null) continue;
                    Console.WriteLine($"  system {N(names, e.SystemIds[si])} initial {N(names, sys.InitialState)} default {N(names, sys.DefaultState)}");
                    foreach (var st in sys.States)
                        foreach (var r in st.Value.Responses)
                        {
                            var a = r.Value.action;
                            Console.WriteLine($"    state {N(names, st.Key)} on {N(names, r.Key)} -> {N(names, r.Value.endState)} action {(a == null ? "-" : N(names, a.Name) + " dur " + a.Duration.ToString("0.###"))}");
                            if (a?.Lists == null) continue;
                            int nt = a.Tags?.Length ?? 0;
                            for (int li = 0; li < a.Lists.Length; li++)
                            {
                                string when = li < nt ? "tag " + N(names, a.Tags[li]) : "t=" + a.Times[li - nt].ToString("0.###");
                                foreach (var ev in a.Lists[li] ?? new List<SeqEvent>())
                                {
                                    var vals = new List<string>();
                                    for (int o = 4; o < ev.Size; o += 4)
                                    {
                                        uint u = ev.U(o);
                                        float f = ev.F(o);
                                        string s = names.TryGetValue(u, out var nm) ? nm : (u >> 16) == 0x6353 ? "\"" + ev.S(o) + "\"" : (u >> 16) == 0x656C ? "el#" + (u & 0xFFFF)
                                            : u == 0 ? "0" : u < 0x10000 ? u.ToString() : Math.Abs(f) > 1e-3 && Math.Abs(f) < 1e5 ? f.ToString("0.###") + "f" : u.ToString("X8");
                                        vals.Add(s);
                                    }
                                    Console.WriteLine($"      [{when}] {N(names, ev.Id)} size {ev.Size} ({string.Join(", ", vals)})");
                                }
                            }
                        }
                }
            }
    }

    /// <summary>Every pvehicle StringKey field (16 bytes: hash64, hash32 @+8, char*) with its hash32 resolved.</summary>
    static void Keys(string root, Dictionary<uint, string> names)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var c in db.OfClass("pvehicle"))
        {
            var parts = new List<string>();
            foreach (var f in c.Class.Fields.Values)
            {
                if (f.Size != 16 || !c.TryGetKey(f.Key, out int at, out _, out _)) continue;
                var src = c.Source.Data;
                if (at + 12 > src.Length) continue;
                uint h = Chunks.U32(src, at + 8);
                if (h == 0) continue;
                parts.Add($"{N(names, f.Key)}={N(names, h)}");
            }
            Console.WriteLine($"{c.Name}: {string.Join(" ", parts)}");
        }
    }

    static void Bounds(string root, string model, Dictionary<uint, string> names)
    {
        var d = File.ReadAllBytes(Path.Combine(root, "GLOBAL", "GLOBALB.BUN"));
        CollisionGeometry g = null;
        for (int i = 0; i + 8 <= d.Length; i += 4)
        {
            if (BitConverter.ToUInt32(d, i) != CollisionGeometry.ChunkPack) continue;
            int size = BitConverter.ToInt32(d, i + 4);
            var b = new byte[size + 8];
            Buffer.BlockCopy(d, i, b, 0, Math.Min(b.Length, d.Length - i));
            try { g = CollisionGeometry.Read(b); } catch (Exception ex) { Console.WriteLine("read: " + ex.Message); }
            break;
        }
        if (g == null) { Console.WriteLine("no bounds pack"); return; }
        uint key = AttribDatabase.Hash(model);
        if (!g.ByName.TryGetValue(key, out var col)) { Console.WriteLine($"no collection {model} {key:X8}; have {g.ByName.Count}"); return; }
        foreach (var b in col.Bounds)
            Console.WriteLine($"  {N(names, b.Name)} flags {b.Flags} pos ({b.Position.X:0.###},{b.Position.Y:0.###},{b.Position.Z:0.###}) half ({b.HalfDims.X:0.###},{b.HalfDims.Y:0.###},{b.HalfDims.Z:0.###}) pivot ({b.Pivot.X:0.###},{b.Pivot.Y:0.###},{b.Pivot.Z:0.###}) q ({b.Qx:0.###},{b.Qy:0.###},{b.Qz:0.###},{b.Qw:0.###}) children {b.Children}@{b.ChildIndex} surf {N(names, b.Surface)}");
    }

    /// <summary>
    /// Runs the CopCar engine with the Core EventSeqEngine (the port's runtime): each argument "ZONE:level" raises a
    /// zone (DAMAGE_(j+1) for the steps, as DamageVehicle::OnImpact), "t+N" advances N seconds, "reset", "boom"
    /// (0xFFCD8A63). EProcessStimulus is executed; every other event is printed with its time.
    /// </summary>
    static void Sim(string root, string[] steps, Dictionary<uint, string> names)
    {
        EventSeqEngineData data = null;
        foreach (var pack in Packs(root)) foreach (var e in pack.Engines) if (e.Name == "CopCar") data = e;
        if (data == null) { Console.WriteLine("no CopCar"); return; }
        float t = 0f;
        EventSeqEngine eng = null;
        var levels = new int[10];
        eng = new EventSeqEngine(data, t)
        {
            OnEvent = (sys, ev) =>
            {
                string n = N(names, ev.Id);
                if (n == "EProcessStimulus")
                {
                    Console.WriteLine($"  t={t:0.00} [{N(names, sys)}] EProcessStimulus {N(names, ev.U(8))} -> {N(names, ev.U(4))}");
                    foreach (var s in eng.Systems) if (s.Id == ev.U(4)) s.ProcessStimulus(ev.U(8), t, EventSeqEngine.QueueMode.Allow);
                    return;
                }
                var vals = new List<string>();
                for (int o = 4; o < Math.Min(ev.Size, 24); o += 4) { uint u = ev.U(o); vals.Add(names.TryGetValue(u, out var nm) ? nm : u.ToString("X")); }
                Console.WriteLine($"  t={t:0.00} [{N(names, sys)}] {n} ({string.Join(", ", vals)})");
            }
        };
        foreach (var st in steps)
        {
            if (st.StartsWith("t+"))
            {
                float dt = float.Parse(st.Substring(2), System.Globalization.CultureInfo.InvariantCulture);
                for (float k = 0; k < dt; k += 0.1f) { t += 0.1f; eng.Update(t); }
                continue;
            }
            Console.WriteLine($"> {st} at t={t:0.00}");
            if (st == "reset") { eng.ProcessStimulus(AttribDatabase.Hash("RESET_DAMAGE"), t, EventSeqEngine.QueueMode.Allow); continue; }
            if (st == "boom") { eng.ProcessStimulus(0xFFCD8A63, t, EventSeqEngine.QueueMode.Allow); continue; }
            var parts = st.Split(':');
            int z = Array.IndexOf(DamageZone.Names, parts[0]);
            int lv = int.Parse(parts[1]);
            uint sysId = AttribDatabase.Hash(parts[0]);
            for (int j = levels[z]; j < lv; j++)
                foreach (var s in eng.Systems) if (s.Id == sysId) s.ProcessStimulus(AttribDatabase.Hash("DAMAGE_" + (j + 1)), t, EventSeqEngine.QueueMode.Allow);
            levels[z] = Math.Max(levels[z], lv);
        }
    }

    /// <summary>Model-space bounds (game axes x forward, y left, z up) and markers of a car's solids.</summary>
    static void BBox(string root, string car, string filter)
    {
        var a = CarAsset.Load(root, car.ToUpperInvariant());
        foreach (var s in a.Solids.Where(s => s.Name.EndsWith("_A") && s.Name.Contains(filter)))
        {
            float[] mn = { 1e9f, 1e9f, 1e9f }, mx = { -1e9f, -1e9f, -1e9f };
            foreach (var p in s.Parts)
                for (int i = 0; i < p.Positions.Length; i += 3)
                    for (int k = 0; k < 3; k++) { mn[k] = Math.Min(mn[k], p.Positions[i + k]); mx[k] = Math.Max(mx[k], p.Positions[i + k]); }
            Console.WriteLine($"{s.Name,-44} min ({mn[0]:0.###},{mn[1]:0.###},{mn[2]:0.###}) max ({mx[0]:0.###},{mx[1]:0.###},{mx[2]:0.###}) markers {s.Markers.Count}");
            foreach (var (h, m) in s.Markers) Console.WriteLine($"     marker {h:X8} pos ({m[12]:0.###},{m[13]:0.###},{m[14]:0.###})");
        }
    }

    static void Smack(string root, string name, Dictionary<uint, string> names)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        uint key = name.StartsWith("0x") ? Convert.ToUInt32(name.Substring(2), 16) : AttribDatabase.Hash(name);
        foreach (var c in db.Collections.Values.Where(x => x.Key == key))
        {
            Console.WriteLine($"{c} class {N(names, c.ClassKey)} parent {c.Parent}");
            foreach (var f in c.Class.Fields.Values)
            {
                if (!c.TryGetKey(f.Key, out int at, out int n, out _)) continue;
                var src = c.Source.Data;
                var vals = new List<string>();
                for (int i = 0; i < Math.Min(Math.Max(1, f.Size / 4), 6) && at + i * 4 + 4 <= src.Length; i++)
                {
                    uint u = Chunks.U32(src, at + i * 4);
                    float fl = BitConverter.ToSingle(src, at + i * 4);
                    vals.Add(names.TryGetValue(u, out var nm) ? nm : Math.Abs(fl) > 1e-4 && Math.Abs(fl) < 1e6 ? fl.ToString("0.###") : u.ToString("X"));
                }
                Console.WriteLine($"   {N(names, f.Key)} = {string.Join(",", vals)}");
            }
        }
    }
}
