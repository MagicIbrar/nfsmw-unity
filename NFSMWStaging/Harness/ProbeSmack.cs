using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// smack seq    : the world event sequencer engines of TRACKS\L2RA.BUN (chunk 0x3B811) with their system ids, named where
//                the id is one of Smackable's collision systems (speed.exe Smackable::Impact 0x669E30)
// smack attrs  : every smackable collection with the fields the exe's Smackable reads
static class ProbeSmack
{
    static readonly string[] SystemNames =
    {
        "GROUND_COLLISION", "WORLD_COLLISION", "OBJECT_COLLISION", "PLAYER_COLLISION", "VEHICLE_COLLISION", "GLOBAL",
    };

    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "seq";
        if (sub == "seq") Seq(root);
        else if (sub == "attrs") Attrs(root);
    }

    static void Seq(string root)
    {
        var names = SystemNames.ToDictionary(n => AttribDatabase.Hash(n), n => n);
        foreach (var n in names) Console.WriteLine($"{n.Value} = 0x{n.Key:X8}");
        var d = GameFiles.ReadBundle(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        var counts = new Dictionary<string, int>();
        int engines = 0;
        for (int i = 0; i + 8 <= d.Length; i++)
        {
            if (d[i] != 0x11 || d[i + 1] != 0xB8 || d[i + 2] != 0x03 || d[i + 3] != 0x00) continue;
            int size = Chunks.I32(d, i + 4);
            if (size <= 16 || i + 8 + size > d.Length) continue;
            EventSeqPack pack;
            try { pack = EventSeqPack.Read(d, i + 8, i + 8 + size); } catch { continue; }
            if (pack == null) continue;
            foreach (var e in pack.Engines)
            {
                engines++;
                var sys = e.SystemIds.Select(s => names.TryGetValue(s, out var n) ? n : s.ToString("X8")).ToList();
                foreach (var s in sys) counts[s] = counts.TryGetValue(s, out int c) ? c + 1 : 1;
                Console.WriteLine($"{e.Name}: {string.Join(", ", sys)}");
            }
            i += 7 + size;
        }
        Console.WriteLine($"{engines} engines; systems: {string.Join(", ", counts.OrderByDescending(k => k.Value).Select(k => k.Key + " " + k.Value))}");
    }

    static void Attrs(string root)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var c in db.OfClass("smackable"))
        {
            string F(string f, int i = 0) => c.Has(f) ? c.Float(f, i, 0f).ToString("0.###") : "-";
            string B(string f) => c.Has(f) ? (c.Bool(f) ? "1" : "0") : "-";
            Console.WriteLine($"{c.Name,-24} MASS {F("MASS"),6} DETACH {F("DETACH_FORCE"),7} MOMENT ({F("MOMENT", 0)},{F("MOMENT", 1)},{F("MOMENT", 2)}) KILL {F("KILL_OFF_SCREEN")} DROPOUT ({F("DROPOUT", 0)},{F("DROPOUT", 1)}) AUTO_SIMPLIFY {F("AUTO_SIMPLIFY")} CAN_SIMPLIFY {(c.Has("CAN_SIMPLIFY") ? c.Int("CAN_SIMPLIFY").ToString() : "-")} EXPL {F("ExplosionEffect")} SIMPLE {B("SimplePhysics")} NO_CAR {B("NO_CAR_EFFECT")} OFFWORLD {B("ALLOW_OFF_WORLD")} no_trigger {B("no_trigger")} start_seq {B("start_sequencer")} COST {(c.Has("COST_TO_STATE") ? c.Int("COST_TO_STATE").ToString() : "-")}");
        }
    }
}
