using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// audio9 surfaces|stitchloop|<class> <fields>: round-9 audio probes (audio agent).
//   surfaces    simsurface Aud_Roadnoise_LOOP / Aud_Skid_Type / Aud_RoadNoise_TransON / TransOFF as ints
//   ints <class> <f,f,..>   every collection of a class with the given fields as ints
static class ProbeAudio9
{
    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        string what = args.Length > 2 ? args[2] : "surfaces";
        if (what == "surfaces") Ints(db, "simsurface", new[] { "Aud_Roadnoise_LOOP", "Aud_Skid_Type", "Aud_RoadNoise_TransON", "Aud_RoadNoise_TransOFF" });
        else if (what == "ints" && args.Length > 4) Ints(db, args[3], args[4].Split(','));
        else if (what == "colours") Colours(root);
    }

    // SoundAI::GetCustomized: STRINGHASH_* of the colour names vs the PC map, and the default base paint's SPEECHCOLOUR per car type
    static void Colours(string root)
    {
        foreach (var n in new[] { "RED", "BLACK", "WHITE", "BLUE", "GREEN", "ORANGE", "SILVER", "GOLD", "PURPLE", "BROWN", "YELLOW", "PINK", "BEIGE" })
            Console.WriteLine($"{n} {EaHash.Hash(n):X8}");
        var db = CarPartDb.Load(root);
        uint sc = EaHash.Hash("SPEECHCOLOUR");
        int shown = 0;
        foreach (var t in db.CarTypes.Values)
        {
            CarPartDb.Part def = null;
            foreach (var p in db.PartsFor(t.Name, 76)) if (p.NameHash == t.DefaultBasePaint) { def = p; break; }
            int paints = db.PartsFor(t.Name, 76).Count();
            Console.WriteLine($"{t.Name}: default {t.DefaultBasePaint:X8} -> {(def == null ? "none" : def.Name)} speech {(def == null ? 0 : def.U(sc)):X8}  paints {paints}");
            if (++shown > 25) break;
        }
        var cnt = new System.Collections.Generic.Dictionary<uint, int>();
        foreach (var p in db.Parts) if (p.PartId == 76) { uint h = p.U(sc); cnt[h] = cnt.TryGetValue(h, out int c) ? c + 1 : 1; }
        foreach (var kv in cnt) Console.WriteLine($"paint speechcolour {kv.Key:X8} x{kv.Value}");
    }

    static void Ints(AttribDatabase db, string cls, string[] fields)
    {
        foreach (var c in db.OfClass(cls))
            Console.WriteLine(c + "  " + string.Join("  ", fields.Select(f => f + "=" + (c.Has(f) ? c.Int(f).ToString() : "-"))));
    }
}
