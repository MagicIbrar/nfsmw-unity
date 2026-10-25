using System;
using System.IO;
using NfsMw.Core;

// post chain probes (world_render.md, end-of-frame post chain): post gain  -> UVESVIGNETTE (GAINMAP) channel samples
static class PostProbe
{
    public static void Run(string root, string[] args)
    {
        uint h = EaHash.Hash("UVESVIGNETTE");
        var raw = File.ReadAllBytes(Path.Combine(root, "GLOBAL", "GLOBALB.BUN"));
        foreach (var t in TexturePack.ReadAll(raw, 0, raw.Length))
        {
            if (t.Hash != h) continue;
            Console.WriteLine($"{t} format {t.Format} bytes {t.Data?.Length}");
            int w = t.Width;
            foreach (var (x, y) in new[] { (0, 0), (w / 2, w / 2), (w / 4, w / 2), (w / 2, w / 4), (w / 8, w / 8), (w - 1, w - 1), (w / 2, 0), (0, w / 2) })
            {
                int o = (y * w + x) * 4;
                Console.WriteLine($"  ({x},{y}) B {t.Data[o]} G {t.Data[o + 1]} R {t.Data[o + 2]} A {t.Data[o + 3]}");
            }
            for (int x = 0; x < w; x += 16) { int o = ((w / 2) * w + x) * 4; Console.Write($"{t.Data[o + 1]}/{t.Data[o]} "); }
            Console.WriteLine();
        }
    }
}
