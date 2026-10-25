using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;

// world render probes (NFSMW_RE/specs/world_render.md):
//   wrender mats [step]     per effect id: which textures sit in the material's 5 texture slots (diffuse, normal, ...)
//   wrender verts <effect>  raw floats past +36 of the first vertices of that effect (tangent layout)
//   wrender tex <name>      a texture's TextureInfo fields by name (searched in all sections + GLOBALB)
static class WorldRenderProbe
{
    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "mats";
        switch (mode)
        {
            case "mats": Mats(root, args.Length > 3 ? int.Parse(args[3]) : 4); break;
            case "verts": Verts(root, int.Parse(args[3])); break;
            case "tex": Tex(root, args[3]); break;
            case "skyinst": SkyInst(root); break;
            case "sun": Sun(root); break;
            case "effects": Effects(root, args.Length > 3 ? int.Parse(args[3]) : 1); break;
        }
    }

    static void Mats(string root, int step)
    {
        using var track = new TrackStream(root);
        var tex = new Dictionary<uint, GameTexture>();
        var stats = new Dictionary<(int eff, int slot), Dictionary<string, int>>();
        int sec = 0;
        foreach (var s in track.Sections)
        {
            if (sec++ % step != 0 && !s.Name.StartsWith("Y") && !s.Name.StartsWith("X")) continue;
            var data = track.Load(s, textures: true, solids: true, scenery: false);
            foreach (var t in data.Textures) tex[t.Hash] = t;
            foreach (var so in data.Solids)
                foreach (var p in so.Parts)
                {
                    if (p.EffectId == 0 || p.EffectId == 19) continue;
                    for (int k = 0; k < 5; k++)
                    {
                        int slot = p.TextureSlots[k];
                        string n = slot == 0xFF ? "-" : slot < so.TextureHashes.Length
                            ? (tex.TryGetValue(so.TextureHashes[slot], out var gt) ? gt.Name : so.TextureHashes[slot].ToString("X8")) : $"bad{slot}";
                        if (!stats.TryGetValue((p.EffectId, k), out var m)) stats[(p.EffectId, k)] = m = new Dictionary<string, int>();
                        m[n] = m.TryGetValue(n, out int c) ? c + 1 : 1;
                    }
                }
        }
        foreach (var kv in stats.OrderBy(k => k.Key))
        {
            var top = kv.Value.OrderByDescending(x => x.Value).Take(10);
            Console.WriteLine($"effect {kv.Key.eff} slot {kv.Key.slot}: {kv.Value.Values.Sum()} parts, {kv.Value.Count} names: " + string.Join(" ", top.Select(x => $"{x.Key}x{x.Value}")));
        }
    }

    static void Verts(string root, int effect)
    {
        SolidList.CaptureExtra = true;
        using var track = new TrackStream(root);
        int shown = 0;
        foreach (var s in track.Sections)
        {
            var data = track.Load(s, textures: false, solids: true, scenery: false);
            foreach (var so in data.Solids)
                foreach (var p in so.Parts)
                {
                    if (p.EffectId != effect || p.Extra == null) continue;
                    Console.WriteLine($"{s.Name} {so.Name} {p.MaterialName} verts {p.VertexCount} extra/vertex {p.ExtraPerVertex}");
                    for (int i = 0; i < Math.Min(4, p.VertexCount); i++)
                    {
                        var ex = Enumerable.Range(0, p.ExtraPerVertex).Select(k => p.Extra[i * p.ExtraPerVertex + k].ToString("0.###"));
                        Console.WriteLine($"   n ({p.Normals[i * 3]:0.###},{p.Normals[i * 3 + 1]:0.###},{p.Normals[i * 3 + 2]:0.###}) uv ({p.Uvs[i * 2]:0.###},{p.Uvs[i * 2 + 1]:0.###}) extra " + string.Join(" ", ex));
                    }
                    if (++shown >= 6) return;
                }
        }
    }

    // what Runtime/World/WorldMaterials.cs will pick per part: effect id x (tangents, normal + spec map found, blended diffuse)
    static void Effects(string root, int step)
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
            var data = track.Load(s, textures: true, solids: true, scenery: false);
            foreach (var t in data.Textures) tex[t.Hash] = t;
            foreach (var so in data.Solids)
                foreach (var p in so.Parts)
                {
                    uint H(int k) => p.TextureSlots[k] < so.TextureHashes.Length ? so.TextureHashes[p.TextureSlots[k]] : 0;
                    tex.TryGetValue(p.TextureHash, out var d);
                    string k2 = $"effect {p.EffectId,2} blend {(d == null ? "?" : d.AlphaBlend.ToString())} usage {(d == null ? "?" : d.AlphaUsage.ToString())}";
                    if (p.EffectId == 1 || p.EffectId == 3)
                        k2 += $" tangents {(p.Tangents != null ? 1 : 0)} normal {(tex.ContainsKey(H(1)) ? 1 : 0)} spec {(tex.ContainsKey(H(3)) ? 1 : 0)}";
                    count[k2] = count.TryGetValue(k2, out int c) ? c + 1 : 1;
                }
        }
        foreach (var kv in count) Console.WriteLine($"{kv.Key}: {kv.Value}");
    }

    // the SunInfo table (chunk 0x34202) of GLOBALB and the sun textures
    static void Sun(string root)
    {
        foreach (var f in new[] { "GLOBAL/GLOBALB.lzc", "GLOBAL/GLOBALB.BUN" })
        {
            var path = System.IO.Path.Combine(root, f);
            if (!System.IO.File.Exists(path)) continue;
            var d = GameFiles.ReadBundle(path);
            Console.WriteLine($"{f}: {d.Length} bytes");
            foreach (var s in SunInfo.ReadAll(d))
            {
                Console.WriteLine($"  {s} hash {s.NameHash:X8} (E3DEMO {EaHash.Hash("E3DEMO"):X8})");
                foreach (var l in s.Layers)
                    Console.WriteLine($"     tex {l.Texture} alpha {l.AlphaType} intensity {l.IntensityScale:0.###} size {l.Size:0.##} off ({l.OffsetX:0.##},{l.OffsetY:0.##}) colour {string.Join(",", l.Colour)} angle {l.Angle} sweep {l.SweepAngleAmount:0.###}");
            }
            var tex = TexturePack.ReadAll(d, 0, d.Length, false);
            foreach (var n in SunInfo.TextureNames)
            {
                var t = tex.Find(x => x.Hash == EaHash.Hash(n));
                Console.WriteLine($"  {n}: {(t == null ? "missing" : $"{t} usage {t.AlphaUsage} blend {t.AlphaBlend}")}");
            }
        }
    }

    // every SKY* scenery instance with its flags (SKY_SPECULAR: is it culled from the player view by flag 0x02?)
    static void SkyInst(string root)
    {
        using var track = new TrackStream(root);
        foreach (var s in track.Sections)
        {
            var data = track.Load(s, textures: false, solids: false, scenery: true);
            foreach (var g in data.Scenery)
                foreach (var i in g.Instances)
                    if (i.InfoIndex < g.Infos.Count && g.Infos[i.InfoIndex].Name.StartsWith("SKY"))
                        Console.WriteLine($"{s.Name}: {g.Infos[i.InfoIndex].Name} flags {i.Flags:X8} at {i.Position}");
        }
    }

    static void Tex(string root, string name)
    {
        uint h = EaHash.Hash(name);
        using var track = new TrackStream(root);
        foreach (var s in track.Sections)
        {
            var raw = track.ReadRaw(s);
            foreach (var t in TexturePack.ReadAll(raw, 0, raw.Length, false))
                if (t.Hash == h) Console.WriteLine($"{s.Name}: {t} usage {t.AlphaUsage} blend {t.AlphaBlend} pack {t.PackName}");
        }
        foreach (var f in new[] { "GLOBAL/GLOBALB.BUN", "GLOBAL/INGAMEA.BUN", "GLOBAL/INGAMEB.BUN", "TRACKS/L2RA.BUN" })
        {
            var path = System.IO.Path.Combine(root, f);
            if (!System.IO.File.Exists(path)) continue;
            var raw = System.IO.File.ReadAllBytes(path);
            try
            {
                foreach (var t in TexturePack.ReadAll(raw, 0, raw.Length, false))
                    if (t.Hash == h) Console.WriteLine($"{f}: {t} usage {t.AlphaUsage} blend {t.AlphaBlend} pack {t.PackName}");
            }
            catch (Exception e) { Console.WriteLine($"{f}: {e.Message}"); }
        }
    }
}
