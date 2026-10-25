using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;

// `r9gp <sub>`: round-9 gameplay probes. strings: every string constant of the stategraph bytecode containing args[3];
// icons: the IconMarker track markers and the type-8 (hidden) track path zones GManager turns into GIcons.
static class ProbeRound9Gp
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "icons";
        if (sub == "strings")
        {
            var gs = GameplayScripts.Load(root);
            string needle = args.Length > 3 ? args[3] : "";
            foreach (var h in gs.Handlers)
            {
                if (h.Proto == null) continue;
                foreach (var f in LuaProbe.Walk(h.Proto))
                    foreach (var k in f.K)
                        if (k.IsString && k.Str.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) Console.WriteLine($"{h.Name}: {k.Str}");
            }
        }
        else if (sub == "controller")
        {
            var db = AttribDatabase.Load(System.IO.Path.Combine(root, "GLOBAL", "attributes.bin"));
            foreach (var c in db.OfClass("controller"))
            {
                if (args.Length > 3 && c.Name != args[3]) continue;
                Console.WriteLine($"== {c.Name} parent {c.Parent?.Name}");
                for (int id = 0; id < NfsMw.Core.InputActions.Names.Length; id++)
                {
                    string an = NfsMw.Core.InputActions.Names[id];
                    if (!c.TryGet(an, out int at, out int count, out var def)) continue;
                    var src = c.Source;
                    var parts = new List<string>();
                    for (int k = 0; k < count; k++)
                    {
                        int b = at + k * def.Size;
                        string nm = null;
                        int ptr = db.Ptr(src.Pack, b + 0xC);
                        if (ptr >= 0) nm = Chunks.CString(src.Data, ptr, 64);
                        parts.Add($"{nm ?? Chunks.U32(src.Data, b + 8).ToString("X8")}/t{Chunks.I32(src.Data, b + 0x10)}/{Chunks.F32(src.Data, b + 0x14):0.##}/{Chunks.F32(src.Data, b + 0x18):0.##}");
                    }
                    Console.WriteLine($"  {id} {an} (size {def.Size}): {string.Join("  ", parts)}");
                }
            }
        }
        else if (sub == "fields")
        {
            // fields f1,f2,...: every gameplay.bin collection that has any of the fields (its own entry, not inherited)
            var db = AttribDatabase.LoadAll(System.IO.Path.Combine(root, "GLOBAL", "attributes.bin"), System.IO.Path.Combine(root, "GLOBAL", "gameplay.bin"));
            var fs = args[3].Split(',');
            foreach (var c in db.Collections.Values)
            {
                var own = fs.Where(f => c.Entries.ContainsKey(AttribDatabase.Hash(f)) || (c.Class != null && c.Class.Fields.TryGetValue(AttribDatabase.Hash(f), out var fd) && fd.InLayout && c.Layout >= 0)).ToList();
                if (own.Count == 0) continue;
                var parts = own.Select(f => { string v; try { v = c.Float(f).ToString("0.###") + "/" + c.Int(f) + "/" + c.Bool(f); } catch { v = "?"; } return f + "=" + v; });
                Console.WriteLine($"{c.Name} [{c.Parent?.Name}]: {string.Join("  ", parts)}");
            }
        }
        else if (sub == "icons")
        {
            uint h = EaHash.Hash("IconMarker");
            var all = TrackMarkers.Load(root).All;
            Console.WriteLine($"track markers {all.Count}; names: " + string.Join(", ", all.GroupBy(m => m.NameHash).Select(g => $"{g.Key:X8}x{g.Count()}")));
            foreach (var m in all.Where(m => m.NameHash == h)) Console.WriteLine($"IconMarker at {m.Position.X:0.0} {m.Position.Y:0.0} {m.Position.Z:0.0} param {m.Param} track {m.TrackNumber}");
            var zones = TrackPathZones.Load(root).Where(z => z.Type == TrackPathZones.Hidden).ToList();
            Console.WriteLine($"hidden zones {zones.Count}");
            foreach (var z in zones) Console.WriteLine($"  hiding spot {z.PosX:0.0} {z.PosY:0.0} {z.Elevation:0.0} data0 {z.Data[0]:X8}");
        }
    }
}
