using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// weather probes (NFSMW_RE/specs/world_render.md, Core/Weather.cs):
//   weather tex                 where RAINDROP / RAINDROPPC live and their TextureInfo
//   weather regions             the GenericRegions of L2RA.BUN (chunk 0x34250)
//   weather events              EventTrigger ids over all stream sections (chunk 0x80036000)
//   weather rain [x y [secs]]   rain / cloud targets and the simulated Rain object over world time at a point
//   weather cover [t]           share of the map raining / cloudy at world time t (40 m grid)
static class WeatherProbe
{
    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "rain";
        switch (mode)
        {
            case "tex": Tex(root); break;
            case "regions": Regions(root); break;
            case "events": Events(root); break;
            case "cover": Cover(root, args.Length > 3 ? float.Parse(args[3]) : 0f); break;
            default:
                Rain(root, args.Length > 4 ? float.Parse(args[3]) : 1000f, args.Length > 4 ? float.Parse(args[4]) : 2000f,
                    args.Length > 5 ? float.Parse(args[5]) : 1100f);
                break;
        }
    }

    static void Tex(string root)
    {
        var want = new[] { "RAINDROP", "RAINDROPPC" };
        var hashes = want.ToDictionary(x => EaHash.Hash(x), x => x);
        var files = new List<string>();
        foreach (var dir in new[] { "GLOBAL", "FRONTEND", "TRACKS" })
            if (Directory.Exists(Path.Combine(root, dir))) files.AddRange(Directory.GetFiles(Path.Combine(root, dir)));
        foreach (var f in files)
        {
            if (new FileInfo(f).Length > 64 << 20) continue;
            byte[] d;
            try { d = GameFiles.ReadBundle(f); } catch (Exception) { continue; }
            List<GameTexture> tex;
            try { tex = TexturePack.ReadAll(d, 0, d.Length, false); } catch (Exception) { continue; }
            foreach (var t in tex)
                if (hashes.ContainsKey(t.Hash))
                    Console.WriteLine($"{Path.GetFileName(f)}: {t} usage {t.AlphaUsage} blend {t.AlphaBlend} order {t.RenderOrder} tile {t.TileUV}");
        }
    }

    static void Regions(string root)
    {
        var d = GameFiles.ReadBundle(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        var list = WeatherRegions.Read(d);
        Console.WriteLine($"{list.Count} regions; by type: " + string.Join(", ", list.GroupBy(r => r.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        foreach (var r in list) Console.WriteLine("  " + r);
    }

    static void Events(string root)
    {
        using var track = new TrackStream(root);
        var hist = new Dictionary<uint, int>();
        int n = 0;
        foreach (var s in track.Sections)
        {
            var raw = track.ReadRaw(s);
            foreach (var t in EventTrigger.ReadAll(raw, 0, raw.Length))
            {
                hist.TryGetValue(t.EventId, out int c);
                hist[t.EventId] = c + 1;
                n++;
            }
        }
        Console.WriteLine($"{n} event triggers");
        foreach (var kv in hist.OrderBy(k => k.Key)) Console.WriteLine($"  event 0x{kv.Key:X} x{kv.Value}");
    }

    static ParameterMaps Maps(string root) => ParameterMaps.Load(File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN")));

    static void Rain(string root, float x, float y, float secs)
    {
        var maps = Maps(root);
        var rain = maps.Layer("Rain");
        var clouds = maps.Layer("Clouds");
        var wind = new ParameterAccessorBlendByDistance("Wind");
        wind.Attach(maps);
        var p = new Precipitation(1);
        var fx = new ScreenEffectDB();
        float dt = 1f / 30f, t = 0f, next = 0f;
        Console.WriteLine($"at game ({x}, {y}): rain set {rain.GetParameterSetIndex(x, y)} ({rain.GetFloatAt(0, x, y)}, {rain.GetFloatAt(1, x, y)}), cloud set {clouds.GetParameterSetIndex(x, y)} ({clouds.GetFloatAt(0, x, y)}, {clouds.GetFloatAt(1, x, y)})");
        while (t <= secs)
        {
            var f = new PrecipitationInput
            {
                Dt = dt, WorldSeconds = t, CamPos = new Vec3(x, y, 10f), CamDir = new Vec3(1, 0, 0), CamVelocity = new Vec3(20, 0, 0),
                HasCar = true, CarPos = new Vec3(x + 5f, y, 9f), CarFwd = new Vec3(1, 0, 0), CarLeft = new Vec3(0, 1, 0), CarUp = new Vec3(0, 0, 1),
            };
            p.UpdateAndRender(f, rain, clouds, wind, fx);
            if (t >= next)
            {
                Console.WriteLine($"t {t,6:0} s  chance rain {RainMath.RainChance(x, t):0.00} cloud {RainMath.CloudChance(x, t):0.00}  target rain {p.RainTarget:0.00} cloud {p.CloudTarget:0.00}  " +
                                  $"rain {p.Rain:0.000} cloud {p.Cloud:0.000} wet {p.Wetness:0.000}  drops {p.NumActive} (desired {p.Desired[0]}) streaks {p.Drops.Count} on-screen {p.OnScreen.NumOnScreen} wind ({p.Wind.X:0.00},{p.Wind.Y:0.00})");
                next += 10f;
            }
            t += dt;
        }
    }

    static void Cover(string root, float t)
    {
        var maps = Maps(root);
        var rain = maps.Layer("Rain");
        var clouds = maps.Layer("Clouds");
        // the drivable area of L2RA, roughly
        int n = 0, wet = 0, cloudy = 0;
        for (float y = -2000f; y < 7000f; y += 40f)
            for (float x = -4000f; x < 7000f; x += 40f)
            {
                n++;
                float r = RainMath.DesiredRain(rain, x, y, t, 0f);
                if (r > 0f) wet++;
                else if (RainMath.DesiredCloud(clouds, x, y, t) > 0f) cloudy++;
            }
        Console.WriteLine($"t {t} s: raining {100.0 * wet / n:0.0}%, cloudy (dry) {100.0 * cloudy / n:0.0}% of {n} cells");
        for (float tt = 0; tt < 504f; tt += 24f)
        {
            int w2 = 0, c2 = 0;
            for (float y = -2000f; y < 7000f; y += 200f)
                for (float x = -4000f; x < 7000f; x += 200f)
                {
                    if (RainMath.DesiredRain(rain, x, y, tt, 0f) > 0f) w2++;
                    else if (RainMath.DesiredCloud(clouds, x, y, tt) > 0f) c2++;
                }
            Console.WriteLine($"  t {tt,4:0}: rain {w2,4} cloud {c2,4} (200 m grid)");
        }
    }
}
