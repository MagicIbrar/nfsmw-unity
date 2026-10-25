using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// Round 9 quick race / options probe: `fe9qr maps` (TrackMaps.bin textures).
static class ProbeFe9QR
{
    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "maps";
        if (mode == "maps")
        {
            var d = GameFiles.ReadBundle(Path.Combine(root, "TRACKS", "L2RA", "TrackMaps.bin"));
            var all = TexturePack.ReadAll(d, 0, d.Length, true);
            Console.WriteLine($"{all.Count} textures");
            foreach (var t in all.Take(args.Length > 3 ? int.Parse(args[3]) : 40)) Console.WriteLine($"  {t}");
        }
        else if (mode == "fekey") ProbeFe9QRHash.Run(root, args);
    }
}
static class ProbeFe9QRHash
{
    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        uint k = Convert.ToUInt32(args[3], 16);
        foreach (var kv in db.Collections)
            if (kv.Key.Item2 == k) Console.WriteLine($"class {kv.Key.Item1:X8} {kv.Value.Name} UnlockedAt={(kv.Value.Has("UnlockedAt") ? kv.Value.Int("UnlockedAt") : -1)}");
        foreach (var f in new[] { "../../NFSMW_RE/vlt_names_all.txt", "../../NFSMW_RE/exe_strings.txt" })
            foreach (var line in File.ReadLines(f))
                foreach (var w in line.Split(' ', '	', '"'))
                    if (w.Length > 1 && (AttribDatabase.Hash(w) == k || AttribDatabase.Hash(w.ToLowerInvariant()) == k)) Console.WriteLine("name: " + w);
    }
}
