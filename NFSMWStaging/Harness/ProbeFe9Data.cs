using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// Round 9 FE data probe (slice C): `fe9data infr` — the infractions collections GetFineValue (VehicleDB.cpp:1176) asks for.
static class ProbeFe9Data
{
    public static void Run(string root, string[] args)
    {
        var g = Path.Combine(root, "GLOBAL"); var db = AttribDatabase.LoadAll(Path.Combine(g, "attributes.bin"), Path.Combine(g, "gameplay.bin"), Path.Combine(g, "FE_ATTRIB.bin"));
        if (args.Length > 2 && args[2] == "defcar")
        {
            if (db.Collections.TryGetValue((AttribDatabase.Hash("frontend"), 0xEEC2271A), out var fe))
                foreach (var r in fe.Refs("default_car"))
                    Console.WriteLine($"frontend {fe.Name} default_car → class {r.cls:X8} key {r.key:X8} {(db.Collections.TryGetValue(r, out var pv) ? pv.Name : "?")}");
            else Console.WriteLine("no frontend 0xEEC2271A");
            return;
        }
        if (args.Length > 2 && args[2] == "ranks")
        {
            foreach (var c in db.OfClass("frontend"))
                if (c.Has("NameId") || c.Has("RapSheetRanks") || (c.Name ?? "").Contains("rap") || (c.Parent?.Name ?? "").Contains("rap")) Console.WriteLine($"coll {c.Name ?? c.Key.ToString("X8")} parent {c.Parent?.Name} NameId {c.Has("NameId")} Ranks {c.Has("RapSheetRanks")}");
            Console.WriteLine("frontend collections: " + db.OfClass("frontend").Count());
            foreach (var n in new[] { "pursuit_length", "cops_involved", "cops_damaged", "cops_destroyed", "tire_spikes_dodged", "roadblocks_dodged", "helis_involved", "cost_to_state", "total_infractions", "bounty", "pursuit_length_in_pursuit", "bounty_in_pursuit" })
            {
                var c = db.Get("frontend", n);
                if (c == null) { Console.WriteLine($"{n,-28} (none)"); continue; }
                c.TryGet("RapSheetRanks", out _, out int cnt, out _);
                Console.WriteLine($"{n,-28} n={cnt} " + string.Join(",", Enumerable.Range(0, cnt).Select(i => c.Int("RapSheetRanks", i))));
            }
            return;
        }
        foreach (var n in new[] { "assault", "damage", "hit_and_run", "off_road", "racing", "reckless_driving", "resisting_arrest", "speeding",
                                  "excessive_speeding", "ramming", "damage_to_property", "driving_off_roadway" })
        {
            var c = db.Get("infractions", n);
            Console.WriteLine($"{n,-22} {(c == null ? "(none)" : "amount " + c.Int("amount"))}");
        }
    }
}
