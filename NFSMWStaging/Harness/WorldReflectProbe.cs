using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;

// road reflection probes (NFSMW_RE/specs/world_render.md §4):
//   wrefl scenery [step]   which scenery instances the reflection view (eView 4) draws: DrawAScenery 0x729120 keeps
//                          instances with flag 0x40 or 0x8000000, StuffScenery(view 4, 8) drops flag 0x80; model =
//                          LOD A when flag 0x1000100, else LOD D (the reflection model)
static class WorldReflectProbe
{
    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "scenery";
        switch (mode)
        {
            case "scenery": Scenery(root, args.Length > 3 ? int.Parse(args[3]) : 1); break;
            case "flag": Flag(root, Convert.ToUInt32(args[3], 16), args.Length > 4 ? int.Parse(args[4]) : 1); break;
            case "flagtex": FlagTex(root, Convert.ToUInt32(args[3], 16), args.Length > 4 ? int.Parse(args[4]) : 1); break;
        }
    }

    // name prefixes of the instances carrying a flag bit (e.g. 100 = drawn with shadow_cg, render flag 0x20000)
    static void Flag(string root, uint bit, int step)
    {
        using var track = new TrackStream(root);
        var names = new Dictionary<string, int>();
        int sec = 0, n = 0;
        foreach (var s in track.Sections)
        {
            if (sec++ % step != 0) continue;
            var data = track.Load(s, textures: false, solids: false, scenery: true);
            foreach (var g in data.Scenery)
                foreach (var i in g.Instances)
                {
                    if (i.InfoIndex >= g.Infos.Count || (i.Flags & bit) == 0) continue;
                    n++;
                    var nm = g.Infos[i.InfoIndex].Name;
                    nm = nm.Length > 8 ? nm.Substring(0, 8) : nm;
                    names[nm] = names.TryGetValue(nm, out int c) ? c + 1 : 1;
                }
        }
        Console.WriteLine($"{n} instances with flag {bit:X}: " + string.Join(", ", names.OrderByDescending(kv => kv.Value).Take(40).Select(kv => $"{kv.Key} {kv.Value}")));
    }

    // effect id / diffuse AlphaBlendType of the solids of instances carrying a flag bit (LOD A model)
    static void FlagTex(string root, uint bit, int step)
    {
        using var track = new TrackStream(root);
        var tex = new Dictionary<uint, GameTexture>();
        foreach (var s in track.Sections)
            if (s.Name.StartsWith("Y") || s.Name.StartsWith("X") || s.Name.StartsWith("V"))
                foreach (var t in track.Load(s, textures: true, solids: false, scenery: false).Textures) tex[t.Hash] = t;
        var count = new SortedDictionary<string, int>();
        int sec = 0;
        foreach (var s in track.Sections)
        {
            if (sec++ % step != 0) continue;
            var data = track.Load(s, textures: true, solids: true, scenery: true);
            foreach (var t in data.Textures) tex[t.Hash] = t;
            var solids = new Dictionary<uint, Solid>();
            foreach (var so in data.Solids) solids[so.Hash] = so;
            foreach (var g in data.Scenery)
                foreach (var i in g.Instances)
                {
                    if (i.InfoIndex >= g.Infos.Count || (i.Flags & bit) == 0) continue;
                    if (!solids.TryGetValue(g.Infos[i.InfoIndex].SolidHashes[0], out var so)) continue;
                    foreach (var p in so.Parts)
                    {
                        tex.TryGetValue(p.TextureHash, out var d);
                        string k = $"effect {p.EffectId} blend {(d == null ? "?" : d.AlphaBlend.ToString())} usage {(d == null ? "?" : d.AlphaUsage.ToString())} {(d == null ? "" : d.Name)}";
                        count[k] = count.TryGetValue(k, out int c) ? c + 1 : 1;
                    }
                }
        }
        foreach (var kv in count.OrderByDescending(kv => kv.Value).Take(25)) Console.WriteLine($"{kv.Value,6}  {kv.Key}");
    }

    static bool None(uint x) => x == 0 || x == 0xBE43EDBB || x == 0x90F70174;

    static void Scenery(string root, int step)
    {
        using var track = new TrackStream(root);
        var count = new SortedDictionary<string, int>();
        var names = new Dictionary<string, int>();
        int sec = 0, total = 0;
        var flagBits = new int[32];
        foreach (var s in track.Sections)
        {
            if (sec++ % step != 0) continue;
            var data = track.Load(s, textures: false, solids: true, scenery: true);
            var solids = new HashSet<uint>(data.Solids.Select(x => x.Hash));
            foreach (var g in data.Scenery)
                foreach (var i in g.Instances)
                {
                    if (i.InfoIndex >= g.Infos.Count) continue;
                    total++;
                    for (int b = 0; b < 32; b++) if ((i.Flags & (1u << b)) != 0) flagBits[b]++;
                    var info = g.Infos[i.InfoIndex];
                    bool refl = (i.Flags & 0x8000040u) != 0;
                    bool ground = (i.Flags & 0x80u) != 0;
                    bool lodA = (i.Flags & 0x1000100u) != 0;
                    uint h = lodA ? info.SolidHashes[0] : info.SolidHashes[3];
                    string model = None(h) ? "none" : solids.Contains(h) ? "in section" : "elsewhere";
                    string k = $"reflect {(refl ? 1 : 0)} ground {(ground ? 1 : 0)} lodA {(lodA ? 1 : 0)} model {model}";
                    count[k] = count.TryGetValue(k, out int c) ? c + 1 : 1;
                    if (refl && !ground && model != "none")
                    {
                        string n = info.Name.Length > 6 ? info.Name.Substring(0, 6) : info.Name;
                        names[n] = names.TryGetValue(n, out int m) ? m + 1 : 1;
                    }
                }
        }
        Console.WriteLine($"{total} instances");
        foreach (var kv in count) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        Console.WriteLine("flag bits: " + string.Join(" ", Enumerable.Range(0, 32).Where(b => flagBits[b] > 0).Select(b => $"{1u << b:X}:{flagBits[b]}")));
        Console.WriteLine("drawn in the reflection view (name prefixes): " +
            string.Join(", ", names.OrderByDescending(kv => kv.Value).Take(40).Select(kv => $"{kv.Key} {kv.Value}")));
    }
}
