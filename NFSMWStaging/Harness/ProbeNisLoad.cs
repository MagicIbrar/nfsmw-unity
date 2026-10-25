using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// Camera/NIS agent probes:
//   nisload           : CAnimCandidateData (InGameB.bun 0x37070) - race start/end candidates, moment marker types, special
//                       car lists - and the TrackPositionMarkers (L2RA 0x34146) the NIS snaps to
//   nisload steer     : CarRenderConn::UpdateSteering's ecar MaxTireSteer / SteerSpeed per collection
//   nisload icenoise  : ICE keys (shake tracks, noise, aperture, constraints), context groups, ICEShakeTracks (0x3B211)
//   nisload charlm    : NIS character parts -> light materials (GLOBAL\GLOBALB 0x135200)
static class ProbeNisLoad
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "";
        if (sub == "steer") { Steer(root); return; }
        if (sub == "icenoise") { IceNoise(root); return; }
        if (sub == "charlm") { CharLightMaterials(root); return; }
        if (sub == "wmlod")
        {
            var set = WorldAnimSet.Load(File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN")));
            foreach (var t in set.Trees)
                foreach (var e in t.Entities)
                    Console.WriteLine($"tree {t.Instance.TreeHash:X8} ent {e.InstanceHash:X8} root {e.IsRoot} model {e.ModelHash:X8} lodB {e.LodB:X8} lodZ {e.LodZ:X8}");
            return;
        }
        NisLibrary.Load(root);
        var c = NisCandidateData.Load(root);
        var names = NisLibrary.Scenes.Keys.ToDictionary(n => EaHash.Hash(n), n => n);
        string N(uint h) => names.TryGetValue(h, out var n) ? n : h.ToString("X8");
        Console.WriteLine($"race starts {c.RaceStarts.Count}, race ends {c.RaceEnds.Count}, moments {c.MomentType.Count}, special car lists {c.SpecialCarList.Count}");
        foreach (var r in c.RaceStarts) Console.WriteLine($"  start {N(r.Scene),-16} track {r.CameraTrack} flags {r.Flags:X2} cars {r.MinCars}..{r.MaxCars} config {r.CarConfig}");
        foreach (var r in c.RaceEnds) Console.WriteLine($"  end   {N(r.Scene),-16} track {r.CameraTrack} flags {r.Flags:X2}");
        foreach (var kv in c.MomentType) Console.WriteLine($"  moment {N(kv.Key),-16} marker {kv.Value} {(kv.Value >= 0 && kv.Value < NisCandidateData.MarkerNames.Length ? NisCandidateData.MarkerNames[kv.Value] : "?")}");
        foreach (var kv in c.SpecialCarList) Console.WriteLine($"  cars  {N(kv.Key),-16} list {kv.Value}");
        var tm = TrackMarkers.Load(root);
        Console.WriteLine($"track position markers: {tm.All.Count}");
        foreach (var name in NisCandidateData.MarkerNames)
        {
            var list = tm.All.Where(m => m.NameHash == EaHash.Hash(name)).ToList();
            Console.WriteLine($"  {name,-26} {list.Count}" + string.Concat(list.Take(4).Select(m => $"  ({m.Position.X:0},{m.Position.Y:0},{m.Position.Z:0})@{m.Angle * 360f / 65536f:0}")));
        }
    }

    static void Steer(string root)
    {
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var col in db.OfClass("ecar"))
        {
            bool a = col.TryGet("MaxTireSteer", out _, out _, out _), b = col.TryGet("SteerSpeed", out _, out _, out _);
            Console.WriteLine($"{col.Name ?? col.Key.ToString("X8"),-20} parent {col.Parent?.Name,-12} MaxTireSteer {(a ? col.Float("MaxTireSteer").ToString("0.###") : "-")} SteerSpeed {(b ? col.Float("SteerSpeed").ToString("0.###") : "-")}");
        }
    }

    static void IceNoise(string root)
    {
        var ice = IceCameras.Load(root);
        int keys = 0, shake = 0, noise = 0, dof = 0, cars = 0, world = 0;
        var shakeTypes = new Dictionary<uint, int>();
        foreach (var g in ice.Groups)
            foreach (var tr in g.Value)
                foreach (var k in tr.Keys)
                {
                    keys++;
                    if (k.ShakeType != 0) { shake++; shakeTypes[k.ShakeType] = shakeTypes.TryGetValue(k.ShakeType, out int n) ? n + 1 : 1; }
                    if (k.NoiseAmpStart != 0 || k.NoiseAmpEnd != 0) noise++;
                    if (k.ApertureStart < 37 || k.ApertureEnd < 37) dof++;
                    if (k.ConstrainToCars) cars++;
                    if (k.ConstrainToWorld) world++;
                }
        foreach (var ctx in new[] { 1, 2, 3 })
            foreach (var g in ice.Groups.Where(g => g.Key.context == ctx))
                Console.WriteLine($"  context {ctx} group {g.Key.scene:X8}{(g.Key.scene == IceCameras.StringHash("Cinematics") ? " (Cinematics)" : "")}: {string.Join(", ", g.Value.Select(t => $"{t.Name} {t.Length:0.0}s/{t.Keys.Count}"))}");
        Console.WriteLine($"shake tracks loaded: {string.Join(", ", ice.Shakes.Values.Select(t => t.Name + "/" + t.Count))}");
        Console.WriteLine($"keys {keys}: shake {shake}, noise {noise}, aperture<37 {dof}, constrainCars {cars}, constrainWorld {world}");
        foreach (var kv in shakeTypes)
            Console.WriteLine($"  shake type {kv.Key:X8} x{kv.Value} -> {(ice.Shakes.TryGetValue(kv.Key, out var t) ? t.Name : "?")}");
        foreach (var g in ice.Groups.Take(12))
            foreach (var tr in g.Value)
                foreach (var k in tr.Keys.Where(k => k.NoiseAmpStart != 0 || k.ApertureStart < 37).Take(1))
                {
                    var s = IceCameras.Evaluate(tr, k.Parameter);
                    IceCameras.DepthOfField(s.FovDeg, s.Focal, s.Aperture, out float fd, out float depth);
                    Console.WriteLine($"  ctx {g.Key.context} {g.Key.scene:X8} {tr.Name}: noise {k.NoiseAmpStart:0.###} f {k.NoiseFreqStart:0.###} shake {k.ShakeType:X8} focal {k.FocalStart:0.#} ap {k.ApertureStart} f/{IceCameras.FNumber(k.ApertureStart):0.#} fov {s.FovDeg:0.#} -> focus {fd:0.##} depth {depth:0.##}");
                }
    }

    static void CharLightMaterials(string root)
    {
        NisLibrary.Load(root);
        string path = Path.Combine(root, "GLOBAL", "GLOBALB.lzc");
        if (!File.Exists(path)) path = Path.Combine(root, "GLOBAL", "GLOBALB.BUN");
        var lms = LightMaterials.Read(GameFiles.ReadBundle(path));
        var seen = new HashSet<string>();
        foreach (var sc in NisLibrary.Scenes.Values.Where(x => x.Name.StartsWith("Arrest")))
        {
            var c = NisContent.For(sc);
            foreach (var so in c.Solids.Values)
                foreach (var part in so.Parts)
                {
                    string key = so.Name + "/" + part.MaterialName + "/" + part.LightMaterialHash.ToString("X8");
                    if (!seen.Add(key)) continue;
                    lms.TryGetValue(part.LightMaterialHash, out var m);
                    string k = "";
                    if (m != null)
                    {
                        var q = m.Shader();
                        k = $"min ({q.DiffuseMin[0]:0.##},{q.DiffuseMin[1]:0.##},{q.DiffuseMin[2]:0.##},{q.DiffuseMin[3]:0.##}) range ({q.DiffuseRange[0]:0.##},{q.DiffuseRange[1]:0.##},{q.DiffuseRange[2]:0.##},{q.DiffuseRange[3]:0.##})";
                    }
                    Console.WriteLine($"{so.Name,-10} {part.MaterialName,-20} lm {part.LightMaterialHash:X8} {(m != null ? m.Name : "-"),-16} {k}");
                }
        }
    }
}
