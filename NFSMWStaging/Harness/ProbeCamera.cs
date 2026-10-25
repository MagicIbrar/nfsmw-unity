using System;
using System.Linq;
using NfsMw.Core;

// camkeys : ICE key flag counts per context (bCarSpaceLag, bSmooth, constraints, shake types) — round 9 camera agent
static class ProbeCamera
{
    public static void Run(string root, string[] args)
    {
        var ice = IceCameras.Load(root);
        for (int ctx = 0; ctx < 4; ctx++)
        {
            var keys = ice.Groups.Where(g => g.Key.context == ctx).SelectMany(g => g.Value.Select(t => (g.Key.scene, t))).SelectMany(x => x.t.Keys.Select(k => (x.scene, x.t, k))).ToList();
            int lag = keys.Count(x => x.k.CarSpaceLag), sm = keys.Count(x => x.k.Smooth != 0), ign = keys.Count(x => x.k.IgnoreOrientation);
            Console.WriteLine($"ctx {ctx}: groups {ice.Groups.Count(g => g.Key.context == ctx)} keys {keys.Count} carSpaceLag {lag} smooth {sm} ignoreOrient {ign} cars {keys.Count(x => x.k.ConstrainToCars)} world {keys.Count(x => x.k.ConstrainToWorld)}");
            foreach (var x in keys.Where(x => x.k.CarSpaceLag).Take(int.Parse(args.Length > 2 ? args[2] : "12")))
                Console.WriteLine($"   lag: scene {x.scene:X8} {x.t.Name} p {x.k.Parameter:0.00} space {x.k.EyeSpace}/{x.k.LookSpace} sim {x.k.SimSpeedStart}");
        }
    }
}

// camnoise : simsurface CAMERA_NOISE per surface
static class ProbeCameraNoise
{
    public static void Run(string root)
    {
        var db = AttribDatabase.Load(System.IO.Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var c in db.OfClass("simsurface"))
        {
            var v = c.Floats("CAMERA_NOISE");
            Console.WriteLine($"{c.Name ?? c.Key.ToString("X8"),-28} {c.Key:X8} parent {c.Parent?.Name} CAMERA_NOISE [{string.Join(",", v)}]");
        }
    }
}
