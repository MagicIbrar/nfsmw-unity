using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// emitters [groups|data|effects|surfaces|car <name>|lib|tex]: the particle tables (Core/EmitterData.cs)
static class ProbeEmitters
{
    public static void Run(string root, string[] args)
    {
        string what = args.Length > 2 ? args[2] : "groups";
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "FE_ATTRIB.bin"));
        var fx = new FxDatabase(db);
        string N(uint k) => db.Names.TryGetValue(k, out var n) ? n : k.ToString("X8");
        Console.WriteLine($"emitters {fx.Emitters.Count} groups {fx.Groups.Count} effects {fx.Effects.Count} surfaces {fx.Surfaces.Count}");
        switch (what)
        {
            case "groups":
                foreach (var g in fx.Groups.Values.OrderBy(g => g.Name))
                    Console.WriteLine($"{g.Name} far {g.FarClip} [{string.Join(", ", g.Emitters.Select(e => e.Name))}] ranges [{string.Join(" ", g.Intensity.Select(r => $"{r.min}-{r.max}"))}]");
                break;
            case "data":
                foreach (var e in fx.Emitters.Values.OrderBy(e => e.Name))
                {
                    if (args.Length > 3 && !(e.Name ?? "").Contains(args[3])) continue;
                    Console.WriteLine($"{e.Name} tex {e.TextureHash:X8}/{e.TextureIndex} anim {e.AnimType}@{e.AnimFps}{(e.AnimRandomStartFrame ? " rnd" : "")} life {e.Life}+-{e.LifeVariance} n {e.NumParticles}+-{e.NumParticlesVariance} oneshot {e.IsOneShot} on {e.OnCycle}+{e.OnCycleVariance} off {e.OffCycle}+{e.OffCycleVariance} delay {e.StartDelay}{(e.StartDelayRandomVariance ? "r" : "")}");
                    Console.WriteLine($"   vel {e.VelocityStart} d {e.VelocityDelta} acc {e.AccelStart} d {e.AccelDelta} speed {e.Speed}+-{e.SpeedVariance} spread {e.SpreadAngle} disc {e.SpreadAsDisc} grav {e.Gravity} drag {e.Drag} inherit {e.MotionInherit}+-{e.MotionInheritVariance} live {e.MotionLive} elim {e.EliminateUnnecessaryRandomness}");
                    Console.WriteLine($"   vol {e.VolumeCenter} ext {e.VolumeExtent} size {e.Size} keys {e.KeyPositions} angle {e.RelativeAngle} init {e.InitialAngleRange} rotvar {e.RotationVariance} rnddir {e.RandomRotationDirection} constraint {e.AxisConstraint} far {e.FarClip}");
                    Console.WriteLine($"   colours {e.Color1:X8} {e.Color2:X8} {e.Color3:X8} {e.Color4:X8} killA {e.AlphaToKillAt} nokill {e.NoKillAtAlpha} xenon [{string.Join(",", e.XenonEffects.Select(N))}] basis {(e.ColourBasis != null ? "ok" : "singular")}");
                    if (e.ColourBasis != null)
                        Console.WriteLine($"   c(0) {e.ColourAt(0)} c(.5) {e.ColourAt(0.5f)} c(1) {e.ColourAt(1)} x(0) {e.ExtraAt(0)} x(1) {e.ExtraAt(1)}");
                }
                break;
            case "effects":
                foreach (var e in fx.Effects.Values.OrderBy(e => e.Name))
                    Console.WriteLine($"{e.Name} group {N(e.GroupKey)} quad {e.EmitterQuadratic} inherit {e.InheritVelocity} cull {e.VisualCullDist}");
                break;
            case "surfaces":
                foreach (var s in fx.Surfaces.Values.OrderBy(s => s.Name))
                {
                    string T(TireEffect[] a) => string.Join(" | ", a.Select(t => $"{N(t.GroupKey)} {t.MinSpeed}-{t.MaxSpeed}"));
                    Console.WriteLine($"{s.Name} parent {N(s.ParentKey)}\n   slip {T(s.Slip)}\n   slide {T(s.Slide)}\n   drive {T(s.Drive)}");
                }
                break;
            case "car":
                {
                    var c = fx.Car(args[3]);
                    Console.WriteLine($"slipfx {string.Join(",", c.SlipFX)} skidfx {string.Join(",", c.SkidFX)} skidwidth {string.Join(",", c.TireSkidWidth)}");
                    Console.WriteLine($"nos {N(c.NosEffect)} damage {N(c.DamageEffect)} death {N(c.DeathEffect)} blown {N(c.EngineBlownEffect)} missshift {N(c.MissShiftEffect)}");
                    void L(string n, EffectLink[] a) => Console.WriteLine($"{n}: " + string.Join(" | ", a.Select(l => $"{N(l.SurfaceKey)} -> {N(l.EffectKey)} {l.MinSpeed}-{l.MaxSpeed}")));
                    L("OnHitWorld", c.OnHitWorld); L("OnScrapeWorld", c.OnScrapeWorld); L("OnHitObject", c.OnHitObject);
                    L("OnScrapeObject", c.OnScrapeObject); L("OnHitGround", c.OnHitGround); L("OnScrapeGround", c.OnScrapeGround);
                    L("OnBottomOut", c.OnBottomOut); L("OnBottomScrape", c.OnBottomScrape);
                }
                break;
            case "smack":
                foreach (var c in db.Collections.Values.Where(c => c.ClassKey == 0xCE70D7DB))
                {
                    if (!c.Has("OnDetached")) continue;
                    if (!c.TryGet("OnDetached", out int at, out int n, out _)) continue;
                    var s = string.Join(" | ", Enumerable.Range(0, n).Select(i => $"{N(Chunks.U32(c.Source.Data, at + i * 0x20 + 4))} -> {N(Chunks.U32(c.Source.Data, at + i * 0x20 + 0x10))} {Chunks.F32(c.Source.Data, at + i * 0x20 + 0x18)}-{Chunks.F32(c.Source.Data, at + i * 0x20 + 0x1C)}"));
                    Console.WriteLine($"{c.Name}: {s}");
                }
                break;
            case "lib":
                {
                    foreach (var f in Directory.GetFiles(Path.Combine(root, "FRONTEND", "PLATFORMS"), "*.BIN"))
                    {
                        var d = GameFiles.ReadBundle(f);
                        foreach (var p in EmitterReader.ReadAll(d, 0, d.Length))
                            Console.WriteLine($"{Path.GetFileName(f)}: {N(p.GroupKey)} sec {p.Section:X} pos {p.Position} trig {p.Triggers.Length} known {fx.Groups.ContainsKey(p.GroupKey)}");
                    }
                    var b = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN"));
                    foreach (var p in EmitterReader.ReadAll(b, 0, b.Length))
                        Console.WriteLine($"L2RA.BUN: {N(p.GroupKey)} sec {p.Section} pos {p.Position} trig {p.Triggers.Length}");
                    using var track = new TrackStream(root);
                    int total = 0, trig = 0;
                    var byGroup = new System.Collections.Generic.Dictionary<string, int>();
                    foreach (var s in track.Sections)
                    {
                        byte[] d;
                        try { d = track.ReadRaw(s); } catch { continue; }
                        foreach (var p in EmitterReader.ReadAll(d, 0, d.Length))
                        {
                            total++;
                            trig += p.Triggers.Length;
                            string k = N(p.GroupKey) + (fx.Groups.ContainsKey(p.GroupKey) ? "" : "?");
                            byGroup[k] = byGroup.TryGetValue(k, out int v) ? v + 1 : 1;
                            if (args.Length > 3) Console.WriteLine($"{s.Name}/{s.Number}: {N(p.GroupKey)} sec {p.Section} pos {p.Position} trig {p.Triggers.Length} {(p.Triggers.Length > 0 ? $"r {p.Triggers[0].Radius} reset {p.Triggers[0].ResetTime} prob {p.Triggers[0].Probability}" : "")}");
                        }
                    }
                    Console.WriteLine($"track placements {total} triggers {trig}");
                    foreach (var kv in byGroup.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Key} x{kv.Value}");
                }
                break;
            case "libtop":
                {
                    using var track = new TrackStream(root);
                    int top = 0, nested = 0, secs = 0;
                    foreach (var s in track.Sections)
                    {
                        byte[] d;
                        try { d = track.ReadRaw(s); } catch { continue; }
                        bool any = false;
                        foreach (var c in Chunks.Read(d, 0, d.Length)) if (c.Id == EmitterReader.LibraryChunk) { top++; any = true; }
                        if (!any && EmitterReader.ReadAll(d, 0, d.Length).Count > 0) nested++;
                        if (any) secs++;
                        if (any && secs < 4) Console.WriteLine($"{s.Name} {s.Number} size {s.Size} chunks: " + string.Join(" ", Chunks.Read(d, 0, d.Length).Select(c => c.Id.ToString("X"))));
                    }
                    Console.WriteLine($"top-level {top} nested-only {nested} sections {track.Sections.Count}");
                }
                break;
            case "markers":
                {
                    var a = CarAsset.Load(root, args[3]);
                    foreach (var so in a.Solids)
                        foreach (var (h, m) in so.Markers)
                            if (h == 0xA5B28001 || h == 0xBCF8A18B || h == 0xBD7CF15E || args.Length > 4)
                                Console.WriteLine($"{so.Name}: {h:X8} rows ({m[0]:0.##},{m[1]:0.##},{m[2]:0.##}) ({m[4]:0.##},{m[5]:0.##},{m[6]:0.##}) ({m[8]:0.##},{m[9]:0.##},{m[10]:0.##}) pos ({m[12]:0.###},{m[13]:0.###},{m[14]:0.###})");
                }
                break;
            case "scan":
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var l = EmitterReader.ScanTrack(root);
                    Console.WriteLine($"scan {l.Count} placements in {sw.ElapsedMilliseconds} ms, first {(l.Count > 0 ? N(l[0].GroupKey) + " " + l[0].Position : "")}");
                }
                break;
            case "tex":
                {
                    var hashes = fx.Emitters.Values.Select(e => e.TextureHash).Distinct().ToList();
                    var files = new[] { "GLOBAL/GLOBALB.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameB.lzc", "GLOBAL/InGameA.bun", "GLOBAL/GLOBALA.BUN", "FRONTEND/FrontB.lzc", "FRONTEND/FrontA.bun" };
                    foreach (var f in files)
                    {
                        var path = Path.Combine(root, f);
                        if (!File.Exists(path)) continue;
                        var d = GameFiles.ReadBundle(path);
                        foreach (var t in TexturePack.ReadAll(d, 0, d.Length))
                            if (hashes.Contains(t.Hash)) Console.WriteLine($"{f}: {t.Name} {t.Hash:X8} {t.Width}x{t.Height} {t.Format} blend {t.AlphaBlend} usage {t.AlphaUsage} pack {t.PackName}");
                    }
                    foreach (var h in hashes) Console.WriteLine($"used {h:X8} by {fx.Emitters.Values.Count(e => e.TextureHash == h)}");
                }
                break;
        }
    }
}
