using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// sky/lighting probes (NFSMW_RE/specs/sky_lighting.md):
//   sky pmaps            every parameter-map layer of L2RA.BUN with its sets
//   sky dome             the SKYDOME* solids: effects, textures, first vertices (both UV sets)
//   sky tod [secs]       TimeOfDay + fog constants + sky constants over time (free roam)
//   sky clouds           the Clouds target along an X sweep
static class SkyProbe
{
    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "tod";
        switch (mode)
        {
            case "pmaps": PMaps(root); break;
            case "dome": Dome(root); break;
            case "clouds": Clouds(root); break;
            default: Tod(root, args.Length > 3 ? float.Parse(args[3]) : 1200f); break;
        }
    }

    static string F(float[] v, int n = 4) => "(" + string.Join(", ", v.Take(n).Select(x => x.ToString("0.######"))) + ")";

    static void PMaps(string root)
    {
        var maps = ParameterMaps.Load(File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN")));
        string[] names = { "Rain", "Clouds", "Wind", "Normal Fog", "Rain Fog", "Fog Blend Distance", "Screen Tint SunRise", "Screen Tint Midday", "Ambience", "Reverb", "Speech" };
        foreach (var l in maps.Layers)
        {
            string n = names.FirstOrDefault(x => EaHash.Hash(x) == l.NameHash) ?? l.NameHash.ToString("X8");
            Console.WriteLine($"{n}: quad ({l.QuadLeft},{l.QuadTop})-({l.QuadRight},{l.QuadBottom}) nodes {l.NumberOfQuadNodes} sets {l.NumberOfParameterSets} x{l.SizeOfParameterSet} fields [{string.Join(",", l.FieldTypes)}] valid {l.Valid} q8 {l.QuadData8 != null} q16 {l.QuadData16 != null}");
            for (int s = 0; s < Math.Min(l.NumberOfParameterSets, 8); s++)
                Console.WriteLine("   set " + s + ": " + string.Join(", ", Enumerable.Range(0, l.NumberOfFields).Select(f => l.FieldTypes[f] == 0 ? l.GetFloat(f, s * l.SizeOfParameterSet).ToString("0.###") : l.GetInt(f, s * l.SizeOfParameterSet).ToString())));
            if (l.NumberOfQuadNodes > 0)
            {
                var hist = new int[l.NumberOfParameterSets + 1];
                for (float y = l.QuadTop; y < l.QuadBottom; y += 40f)
                    for (float x = l.QuadLeft; x < l.QuadRight; x += 40f)
                    {
                        int i = l.GetParameterSetIndex(x, y);
                        hist[Math.Min(Math.Max(i, 0), l.NumberOfParameterSets)]++;
                    }
                Console.WriteLine("   cover (40 m grid): " + string.Join(" ", hist.Select((c, i) => $"{i}:{c}")));
            }
        }
    }

    static void Dome(string root)
    {
        using var track = new TrackStream(root);
        foreach (var s in track.Sections)
        {
            var data = track.Load(s, textures: false, solids: true, scenery: true);
            foreach (var sol in data.Solids.Where(x => x.Name.StartsWith("SKY")))
            {
                Console.WriteLine($"{s.Name}: {sol.Name} parts {sol.Parts.Count} bounds {sol.BoundsMin}-{sol.BoundsMax}");
                foreach (var p in sol.Parts)
                {
                    string tex = string.Join(",", p.TextureSlots.Where(t => t < sol.TextureHashes.Length).Select(t => sol.TextureHashes[t].ToString("X8")));
                    Console.WriteLine($"   {p.MaterialName} effect {p.EffectId} flags {p.Flags:X} tex {p.TextureHash:X8} slots {tex} verts {p.VertexCount} uv2 {p.Uvs2 != null}");
                    for (int i = 0; i < Math.Min(4, p.VertexCount); i++)
                        Console.WriteLine($"      p ({p.Positions[i * 3]:0.#},{p.Positions[i * 3 + 1]:0.#},{p.Positions[i * 3 + 2]:0.#}) n ({p.Normals[i * 3]:0.###},{p.Normals[i * 3 + 1]:0.###},{p.Normals[i * 3 + 2]:0.###}) c {p.Colors[i]:X8} uv ({p.Uvs[i * 2]:0.###},{p.Uvs[i * 2 + 1]:0.###})" + (p.Uvs2 != null ? $" uv2 ({p.Uvs2[i * 2]:0.###},{p.Uvs2[i * 2 + 1]:0.###})" : ""));
                }
            }
            foreach (var g in data.Scenery)
                foreach (var inst in g.Instances)
                    if (inst.InfoIndex < g.Infos.Count && g.Infos[inst.InfoIndex].Name.StartsWith("SKY"))
                        Console.WriteLine($"{s.Name}: instance {g.Infos[inst.InfoIndex].Name} at {inst.Position}");
        }
    }

    static void Tod(string root, float seconds)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        var tod = new TimeOfDay(db);
        bool fogEnable = true;
        if (db.Collections.TryGetValue((0xEBCEE74Cu, 0xCE3B435Bu), out var fog)) fogEnable = fog.FloatKey(0x110882D5) != 0 || fog.TryGetKey(0x110882D5, out int at, out _, out _) && fog.Source.Data[at] != 0;
        Console.WriteLine($"fog collection FogEnable={fogEnable}");
        for (float t = 0; t <= seconds; t += Math.Max(1f, seconds / 24f))
        {
            var probe = new TimeOfDay(db);
            for (float s = 0; s < t; s += 1f) probe.Update(1f, false);
            probe.Blend(0f, false);
            var sun = probe.SunDirection;
            Console.WriteLine($"t={t,6:0}s time {probe.Time:0.000} dir {probe.Direction,2} sun {sun} dusk {probe.DuskBlend:0.00} diffuse {F(probe.Vec(0x20), 3)} ambient {F(probe.Vec(0x30))} fogDist {probe.S[0xA0 / 4]:0.##}");
        }
        tod.Blend(0f, false);
        var f = FogConstants.FromTimeOfDay(tod, fogEnable);
        Console.WriteLine($"midday fog: Br {F(f.Br, 3)} Bm {F(f.Bm, 3)}\n  BrPlusBm {F(f.BrPlusBm, 3)} OneOver {F(f.OneOverBrBm, 3)}\n  Const1 {F(f.Const1, 3)} Const2 {F(f.Const2, 3)} Const3 {F(f.Const3)} FogValue {f.FogValue}");
        foreach (float c in new[] { 0f, 0.5f, 1f })
        {
            var p = new TimeOfDay(db);
            p.Blend(c, false);
            var fc = FogConstants.FromTimeOfDay(p, fogEnable);
            Console.WriteLine($"cloud {c}: diffuse {F(p.Vec(0x20), 3)} ambient {F(p.Vec(0x30))} spec {F(p.Vec(0x40), 3)} Const3 {F(fc.Const3)} BrBm {F(fc.BrPlusBm, 3)} fogValue {fc.FogValue} CloudIntensity {F(SkyConstants.CloudIntensity(c), 3)}");
        }
        // extinction / in-scatter at a few distances, world.efx maths, sun behind the camera
        foreach (float dist in new[] { 50f, 200f, 625f, 2000f })
        {
            float d = Math.Min(dist * f.FogValue, 2500f);
            var ext = Enumerable.Range(0, 3).Select(i => (float)Math.Exp(-d * f.BrPlusBm[i])).ToArray();
            Console.WriteLine($"  dist {dist}: ext {F(ext, 3)}");
        }
    }

    static void Clouds(string root)
    {
        var maps = ParameterMaps.Load(File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN")));
        var cl = maps.Layer("Clouds");
        for (float x = -3000; x <= 7000; x += 500)
        {
            var row = string.Join(" ", Enumerable.Range(0, 11).Select(k => CloudState.CloudsAt(cl, x, -5000 + k * 1000).ToString("0.00")));
            Console.WriteLine($"x {x,6}: chance {CloudState.CloudChance(x):0.000}  clouds(y=-5000..5000) {row}");
        }
    }
}
