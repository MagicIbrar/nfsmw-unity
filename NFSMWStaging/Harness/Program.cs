using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// Runs the Core readers over the game's files and reports what parses and what resolves.
// usage: dotnet run -c Release -- <gameRoot> world|cars|globals|section <name>
static class Program
{
    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("NFSMW_ROOT") ?? @"C:\Program Files (x86)\EA GAMES\Need for Speed Most Wanted";
        string mode = args.Length > 1 ? args[1] : "world";
        switch (mode)
        {
            case "world": World(root); break;
            case "cars": Cars(root); break;
            case "globals": Globals(root); break;
            case "section": Section(root, args[2]); break;
            case "groups": Probe.Groups(root); break;
            case "lod": Probe2.Lod(root, args[2]); break;
            case "car": Probe3.Car(root, args[2]); break;
            case "attrib": Probe4.Attrib(root, args[2]); break;
            case "carmats": Probe5.CarMats(root, args[2]); break;
            case "parts": Probe6.Parts(root, args[2], args[3]); break;
            case "solids": Probe7.Solids(root, args[2]); break;
            case "feobj":
                {
                    var all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameB.lzc")));
                    if (!all.ContainsKey(args[2])) all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")));
                    var pk = all[args[2]];
                    foreach (var o in pk.Objects.Where(o => o.Guid.ToString("X") == args[3] || o.Hash.ToString("X8") == args[3] || args[3] == "t" + (int)o.Type))
                    {
                        Console.WriteLine($"obj {o.Hash:X8} g{o.Guid:X} type {o.Type} res {o.Resource} {(o.Resource >= 0 && o.Resource < pk.Resources.Count ? pk.Resources[o.Resource] : "")} uv {o.Data.U0},{o.Data.V0}-{o.Data.U1},{o.Data.V1} q ({o.Data.QX:0.###},{o.Data.QY:0.###},{o.Data.QZ:0.###},{o.Data.QW:0.###}) parent {o.Parent:X8} pos ({o.Data.X:0.#},{o.Data.Y:0.#}) size ({o.Data.SizeX:0.#},{o.Data.SizeY:0.#}) pivot ({o.Data.PivotX:0.#},{o.Data.PivotY:0.#}) rgba ({o.Data.R:0},{o.Data.G:0},{o.Data.B:0},{o.Data.A:0})");
                        foreach (var kv in o.Extra)
                        {
                            var b = kv.Value;
                            var fl = new System.Text.StringBuilder();
                            for (int i = 0; i + 4 <= b.Length; i += 4) fl.Append($"{BitConverter.ToUInt32(b, i):X8}/{BitConverter.ToSingle(b, i):0.###} ");
                            Console.WriteLine($"   {kv.Key} [{b.Length}] {fl}");
                        }
                    }
                }
                break;
            case "alphaeffects":
                {
                    // world parts grouped by (effect id, texture alpha usage): counts and sample texture names
                    using var track = new TrackStream(root);
                    var tex = new System.Collections.Generic.Dictionary<uint, GameTexture>();
                    var groups = new System.Collections.Generic.Dictionary<(int, int), (int n, System.Collections.Generic.HashSet<string> names)>();
                    int sec = 0;
                    foreach (var s2 in track.Sections)
                    {
                        if (args.Length > 2 && sec++ % int.Parse(args[2]) != 0) continue;
                        var data = track.Load(s2, textures: true, solids: true, scenery: false);
                        foreach (var t in data.Textures) tex[t.Hash] = t;
                        foreach (var so in data.Solids)
                            foreach (var pp in so.Parts)
                            {
                                int au = tex.TryGetValue(pp.TextureHash, out var gt) ? gt.AlphaUsage : -1;
                                var k = (pp.EffectId, au);
                                if (!groups.TryGetValue(k, out var g)) g = (0, new System.Collections.Generic.HashSet<string>());
                                g.n++;
                                if (g.names.Count < 14 && gt != null) g.names.Add(gt.Name);
                                groups[k] = g;
                            }
                    }
                    foreach (var kv in groups.OrderBy(k => k.Key))
                        Console.WriteLine($"effect {kv.Key.Item1,2} alpha {kv.Key.Item2,2}: {kv.Value.n,6} parts  e.g. {string.Join(" ", kv.Value.names)}");
                }
                break;
            case "missingsolids":
                {
                    // scenery instances whose LOD-A solid is in neither their section nor X0, by name, and where it lives
                    using var track = new TrackStream(root);
                    var home = new System.Collections.Generic.Dictionary<uint, string>();
                    var datas = new System.Collections.Generic.List<(StreamSection, TrackSectionData)>();
                    foreach (var s2 in track.Sections)
                    {
                        var data = track.Load(s2, textures: false, solids: true, scenery: true);
                        foreach (var so in data.Solids) if (!home.ContainsKey(so.Hash)) home[so.Hash] = s2.Name;
                        datas.Add((s2, data));
                    }
                    int total = 0, missing = 0, lodOnly = 0, elsewhere = 0;
                    var byName = new System.Collections.Generic.Dictionary<string, int>();
                    foreach (var (s2, data) in datas)
                    {
                        if (s2.Name == "X0" || s2.Name == "Y0") continue;
                        var local = new System.Collections.Generic.HashSet<uint>(data.Solids.Select(x => x.Hash));
                        foreach (var g in data.Scenery)
                            foreach (var inst in g.Instances)
                            {
                                if (inst.InfoIndex >= g.Infos.Count) continue;
                                var info = g.Infos[inst.InfoIndex];
                                total++;
                                uint h = info.SolidHashes[0];
                                bool ok = h != 0 && (local.Contains(h) || (home.TryGetValue(h, out var hs) && hs == "X0"));
                                if (ok) continue;
                                missing++;
                                if (h == 0) lodOnly++;
                                else if (home.ContainsKey(h)) elsewhere++;
                                string key = $"{(h == 0 ? "noA" : home.TryGetValue(h, out var w) ? "in " + w : "nowhere")} {info.Name}";
                                byName[key] = byName.TryGetValue(key, out var c) ? c + 1 : 1;
                            }
                    }
                    Console.WriteLine($"{total} instances, {missing} without a reachable LOD-A solid ({lodOnly} have no LOD A, {elsewhere} live in another section)");
                    foreach (var kv in byName.OrderByDescending(k => k.Value).Take(int.Parse(args.Length > 2 ? args[2] : "60"))) Console.WriteLine($"{kv.Value,5} {kv.Key}");
                }
                break;
            case "ice":
                {
                    var ice = IceCameras.Load(root);
                    NisLibrary.Load(root);
                    var names = NisLibrary.Scenes.Keys.ToDictionary(n => IceCameras.StringHash(n), n => n);
                    foreach (var kv in ice.Groups.OrderBy(k => k.Key.context))
                    {
                        string nm = names.TryGetValue(kv.Key.scene, out var n) ? n : kv.Key.scene.ToString("X8");
                        var t0 = kv.Value.FirstOrDefault();
                        string k0 = t0 != null && t0.Keys.Count > 0 ? $"key0 space {t0.Keys[0].EyeSpace}/{t0.Keys[0].LookSpace} eye {t0.Keys[0].EyeStart} lens {t0.Keys[0].LensStart:0.0} lb {t0.Keys[0].LetterboxStart}" : "";
                        Console.WriteLine($"ctx {kv.Key.context} {nm,-24} tracks {kv.Value.Count} len {(t0?.Length ?? 0):0.00}s keys {t0?.Keys.Count} {k0}");
                    }
                }
                break;
            case "visuallook":
                {
                    var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
                    foreach (var c in db.Collections.Values.Where(c => c.ClassKey == 0x339F7D3D))
                    {
                        Console.WriteLine($"{c.Name ?? c.Key.ToString("X8")}");
                        foreach (var (f, n) in new[] { ("DetailMapCurve", 16), ("BlackBloomCurve", 16), ("ColourBloomCurve", 16), ("ColourBloomTint", 4), ("ColourBloomIntensity", 1), ("Desaturation", 1), ("DetailMapIntensity", 1), ("BlackBloomIntensity", 1) })
                        {
                            if (!c.TryGet(f, out int at, out _, out _)) { Console.WriteLine($"   {f} -"); continue; }
                            var v = Enumerable.Range(0, n).Select(i => BitConverter.ToSingle(c.Source.Data, at + i * 4).ToString("0.###"));
                            Console.WriteLine($"   {f} = [{string.Join(",", v)}]");
                        }
                    }
                }
                break;
            case "lookeffects":
                {
                    var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
                    foreach (var c in db.Collections.Values.Where(c => c.ClassKey == 0x6AB2D241))
                    {
                        var sb = new System.Text.StringBuilder($"{c.Name ?? c.Key.ToString("X8")}");
                        foreach (var (f, n) in new[] { ("graph", 16), ("length", 1), ("magnitude", 1), ("heattrigger", 1), ("radialblur_scale", 1) })
                            if (c.TryGet(f, out int at, out _, out _))
                                sb.Append($"  {f}=[{string.Join(",", Enumerable.Range(0, n).Select(i => BitConverter.ToSingle(c.Source.Data, at + i * 4).ToString("0.###")))}]");
                        Console.WriteLine(sb);
                    }
                }
                break;
            case "tod":
                {
                    var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
                    foreach (var c in db.Collections.Values.Where(c => c.ClassKey == 0x399ED882))
                    {
                        Console.WriteLine($"{c.Name ?? c.Key.ToString("X8")} parent {c.Parent?.Name}");
                        foreach (var (f, n) in new[] { ("SpecularColour", 4), ("DiffuseColour", 4), ("AmbientColour", 4), ("FogHazeColour", 4), ("FixedFunctionSkyColor", 4), ("FogSkyColour", 4), ("FogDistanceScale", 1), ("FogHazeColourScale", 1), ("FogSkyColourScale", 1), ("EnvSkyBrightness", 1), ("CarSpecScale", 1), ("FogInLightScatter", 1), ("FogSunFalloff", 1) })
                            if (c.TryGet(f, out int at, out _, out _))
                                Console.WriteLine($"   {f} = [{string.Join(",", Enumerable.Range(0, n).Select(i => BitConverter.ToSingle(c.Source.Data, at + i * 4).ToString("0.####")))}]");
                    }
                }
                break;
            case "smokeables":
                {
                    // smokeables [section]: SmokeableSpawner records (chunk 0x34027, 0x40 bytes) per section
                    using var track = new TrackStream(root);
                    int total = 0;
                    void Walk(byte[] d, int start, int end, string sec, int depth)
                    {
                        foreach (var c in Chunks.Read(d, start, end))
                        {
                            if ((c.Id & 0x80000000) != 0 && depth < 4) Walk(d, c.DataStart, c.End, sec, depth + 1);
                            if (c.Id != 0x34027) continue;
                            int o = c.DataStart;
                            while (o < c.End && d[o] == 0x11) o++;
                            int n = (c.End - o) / 0x40;
                            total += n;
                            if (args.Length > 2 && args[2] != sec) continue;
                            Console.WriteLine($"{sec}: chunk @{c.Start:X} {n} spawners (pad {o - c.DataStart})");
                            var hx = new System.Text.StringBuilder();
                            for (int k = c.Start; k < Math.Min(c.End, c.Start + 0xA0); k += 4) hx.Append($"{BitConverter.ToUInt32(d, k):X8}/{BitConverter.ToSingle(d, k):0.##} ");
                            Console.WriteLine(hx);
                            for (int i = 0; i < Math.Min(n, 12); i++)
                            {
                                int r = o + i * 0x40;
                                Console.WriteLine($"   q ({BitConverter.ToSingle(d, r):0.##},{BitConverter.ToSingle(d, r + 4):0.##},{BitConverter.ToSingle(d, r + 8):0.##},{BitConverter.ToSingle(d, r + 12):0.##}) pos ({BitConverter.ToSingle(d, r + 16):0.#},{BitConverter.ToSingle(d, r + 20):0.#},{BitConverter.ToSingle(d, r + 24):0.#}) model {BitConverter.ToUInt32(d, r + 32):X8} col {BitConverter.ToUInt32(d, r + 36):X8} attr {BitConverter.ToUInt32(d, r + 40):X8} ovr {BitConverter.ToUInt32(d, r + 44)} id {BitConverter.ToUInt32(d, r + 48):X8} excl {BitConverter.ToUInt32(d, r + 52):X}");
                            }
                        }
                    }
                    foreach (var s2 in track.Sections)
                    {
                        var raw = track.ReadRaw(s2);
                        Walk(raw, 0, raw.Length, s2.Name, 0);
                    }
                    Console.WriteLine($"{total} spawners in all");
                }
                break;
            case "smokemap":
                {
                    // smokemap: does OverrideInfo index the 0x34108 table, and is that instance at the spawner's position?
                    using var track = new TrackStream(root);
                    var groups = SceneryGroupTable.Read(track.Bundle);
                    var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
                    var attrNames = db.OfClass("smackable").ToDictionary(c => AttribDatabase.Hash(c.Name ?? ""), c => c.Name);
                    Console.WriteLine($"overrides {groups.Overrides.Count}");
                    var insts = new Dictionary<(int, int), (SceneryInstance, string)>();
                    var spawners = new List<(string, SmokeableSpawner)>();
                    foreach (var s2 in track.Sections)
                    {
                        var data = track.Load(s2, textures: false, solids: false, scenery: true);
                        foreach (var g in data.Scenery)
                            for (int i = 0; i < g.Instances.Count; i++)
                                insts[(g.SectionNumber, i)] = (g.Instances[i], g.Infos[g.Instances[i].InfoIndex].Name);
                        foreach (var sp in data.Smokeables) spawners.Add((s2.Name, sp));
                    }
                    int ok = 0, near = 0, miss = 0, shown = 0, noattr = 0;
                    foreach (var (sec, sp) in spawners)
                    {
                        if (!attrNames.ContainsKey(sp.Attributes)) noattr++;
                        if (sp.OverrideInfo >= groups.Overrides.Count || !insts.TryGetValue(groups.Overrides[(int)sp.OverrideInfo], out var it)) { miss++; continue; }
                        ok++;
                        var p = it.Item1.Position;
                        // sim (-gameY, gameZ, gameX)
                        float dx = p.X - sp.SimPosition.Z, dy = p.Y + sp.SimPosition.X, dz = p.Z - sp.SimPosition.Y;
                        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist < 1f) near++;
                        if ((args.Length > 2 ? it.Item2.Contains(args[2]) && shown++ < 8 : shown++ < 15)) Console.WriteLine($"{sec} ovr {sp.OverrideInfo} -> {groups.Overrides[(int)sp.OverrideInfo]} {it.Item2} attr {(attrNames.TryGetValue(sp.Attributes, out var an) ? an : sp.Attributes.ToString("X8"))} dist {dist:0.00} excl {sp.ExcludeFlags:X}");
                    }
                    Console.WriteLine($"spawners {spawners.Count}: mapped {ok}, within 1 m {near}, unmapped {miss}, unknown attr {noattr}");
                }
                break;
            case "platform2":
                {
                    // platform <file>: the FE garage set's solids with their bounds and matrix translation
                    var d = GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "PLATFORMS", args[2]));
                    foreach (var c in Chunks.Read(d, 0, d.Length))
                    {
                        Console.WriteLine($"chunk {c.Id:X8} {c.End - c.DataStart}");
                        if (c.Id == 0x3BC00)
                            for (int k = c.DataStart; k < c.End; k += 16)
                            {
                                var sb = new System.Text.StringBuilder($"  +{k - c.DataStart:X3} ");
                                for (int j = k; j < Math.Min(k + 16, c.End); j += 4) sb.Append($"{BitConverter.ToUInt32(d, j):X8}/{BitConverter.ToSingle(d, j):0.###} ");
                                sb.Append(" ").Append(new string(System.Text.Encoding.ASCII.GetString(d, k, Math.Min(16, c.End - k)).Select(ch => ch >= 32 && ch < 127 ? ch : '.').ToArray()));
                                Console.WriteLine(sb);
                            }
                    }
                    foreach (var s in SolidList.ReadAll(d, 0, d.Length))
                    {
                        var vmin = new Vec3(1e9f, 1e9f, 1e9f); var vmax = new Vec3(-1e9f, -1e9f, -1e9f);
                        Console.WriteLine($"{s.Name,-40} bounds ({s.BoundsMin.X:0.00},{s.BoundsMin.Y:0.00},{s.BoundsMin.Z:0.00})-({s.BoundsMax.X:0.00},{s.BoundsMax.Y:0.00},{s.BoundsMax.Z:0.00}) t ({s.Matrix[12]:0.00},{s.Matrix[13]:0.00},{s.Matrix[14]:0.00}) parts {s.Parts.Count} markers {s.Markers.Count}");
                    }
                }
                break;
            case "flares":
                {
                    // flares: eLightFlarePackHeader (0x135101) + eLightFlare[0x30] (0x135102) per stream section
                    using var track = new TrackStream(root);
                    var types = new int[32]; int packs = 0, total = 0; var ids = new Dictionary<uint, int>();
                    void Walk(byte[] d, int start, int end, string sec, int depth)
                    {
                        foreach (var c in Chunks.Read(d, start, end))
                        {
                            if ((c.Id & 0xFFFF00) == 0x13500 || (c.Id & 0xFFFFF0) == 0x34010 || (c.Id & 0xFFFFF0) == 0x80034010 || (c.Id & 0xFFFF00) == 0x80135000) ids[c.Id] = ids.GetValueOrDefault(c.Id) + 1;
                            if ((c.Id & 0x80000000) != 0 && depth < 5) Walk(d, c.DataStart, c.End, sec, depth + 1);
                            if (c.Id == 0x135101 || c.Id == 0x3401A)
                            {
                                int o = c.DataStart; while (o < c.End && d[o] == 0x11) o++;
                                packs++;
                                if (packs <= 3) Console.WriteLine($"{sec} header @{o - c.DataStart} ver {BitConverter.ToUInt32(d, o + 8)} name {System.Text.Encoding.ASCII.GetString(d, o + 0x10, 32).TrimEnd('\0')} n {BitConverter.ToUInt16(d, o + 0x50)} sec {BitConverter.ToUInt32(d, o + 0x54)}");
                            }
                            if (c.Id == 0x135102 || c.Id == 0x3401B)
                            {
                                int o = c.DataStart; while (o < c.End && d[o] == 0x11) o++;
                                int n = (c.End - o) / 0x30;
                                for (int i = 0; i < n; i++)
                                {
                                    int r = o + i * 0x30; int t = (sbyte)d[r + 0x2C];
                                    if (t >= 0 && t < 32) types[t]++;
                                    total++;
                                    if (total <= 6) Console.WriteLine($"  {sec} hash {BitConverter.ToUInt32(d, r + 8):X8} tint {BitConverter.ToUInt32(d, r + 12):X8} pos ({BitConverter.ToSingle(d, r + 16):0.0},{BitConverter.ToSingle(d, r + 20):0.0},{BitConverter.ToSingle(d, r + 24):0.0}) refz {BitConverter.ToSingle(d, r + 28):0.0} dir ({BitConverter.ToSingle(d, r + 32):0.00},{BitConverter.ToSingle(d, r + 36):0.00},{BitConverter.ToSingle(d, r + 40):0.00}) type {t} flags {d[r + 0x2D]} sec {BitConverter.ToInt16(d, r + 0x2E)} pad {o - c.DataStart}");
                                }
                            }
                        }
                    }
                    foreach (var s2 in track.Sections) { var raw = track.ReadRaw(s2); Walk(raw, 0, raw.Length, s2.Name, 0); }
                    var bun = track.Bundle; Walk(bun, 0, bun.Length, "BUN", 0);
                    foreach (var kv in ids) Console.WriteLine($"chunk {kv.Key:X8} x{kv.Value}");
                    Console.WriteLine($"packs {packs}, flares {total}; by type " + string.Join(" ", Enumerable.Range(0, 32).Where(i => types[i] > 0).Select(i => $"{i}:{types[i]}")));
                }
                break;
            case "texinfo":
                {
                    // texinfo <file> <name,...>: TextureInfo blend fields and the DXT colour/alpha range of the top level
                    var d = GameFiles.ReadBundle(Path.Combine(root, args[2]));
                    var want = new HashSet<string>(args[3].Split(','));
                    foreach (var t in TexturePack.ReadAll(d, 0, d.Length))
                    {
                        if (!want.Contains(t.Name)) continue;
                        int blocks = Math.Max(1, t.Width / 4) * Math.Max(1, t.Height / 4);
                        double r = 0, g = 0, b = 0, a = 0; int amin = 15, amax = 0;
                        for (int k = 0; k < blocks && (k + 1) * 16 <= t.Data.Length; k++)
                        {
                            int o = k * 16;
                            for (int j = 0; j < 8; j++) { int lo = t.Data[o + j] & 15, hi = t.Data[o + j] >> 4; a += lo + hi; amin = Math.Min(amin, Math.Min(lo, hi)); amax = Math.Max(amax, Math.Max(lo, hi)); }
                            int c0 = BitConverter.ToUInt16(t.Data, o + 8);
                            r += (c0 >> 11) / 31.0; g += ((c0 >> 5) & 63) / 63.0; b += (c0 & 31) / 31.0;
                        }
                        Console.WriteLine($"{t} usage {t.AlphaUsage} blend {t.AlphaBlend} order {t.RenderOrder} avg c0 ({r / blocks:0.00},{g / blocks:0.00},{b / blocks:0.00}) alpha avg {a / blocks / 16 / 15:0.00} range {amin}-{amax}");
                    }
                }
                break;
            case "platfloor":
                {
                    // platfloor <file>: heights of the BACKDROP geometry within 3 m of the turntable centre, per solid/part
                    var d = GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "PLATFORMS", args[2]));
                    foreach (var s in SolidList.ReadAll(d, 0, d.Length))
                    {
                        if (!s.Name.StartsWith("BACKDROP_")) continue;
                        foreach (var p in s.Parts)
                        {
                            float mn = 1e9f, mx = -1e9f; int n = 0;
                            for (int i = 0; i + 2 < p.Positions.Length; i += 3)
                            {
                                float x = p.Positions[i], y = p.Positions[i + 1], z = p.Positions[i + 2];
                                if (x * x + y * y > 9) continue;
                                mn = Math.Min(mn, z); mx = Math.Max(mx, z); n++;
                            }
                            if (n > 0) Console.WriteLine($"{s.Name} part tex {p.TextureHash:X8} mat {p.MaterialName} verts {n} z {mn:0.000}..{mx:0.000}");
                        }
                    }
                }
                break;
            case "stream": StreamProbe.Run(root, args); break;
            case "lodstats":
                {
                    // lodstats: scenery LOD models per info (A/B/C/D), solid density and the draw distances they give
                    using var track = new TrackStream(root);
                    var solids = new Dictionary<uint, Solid>();
                    int infos = 0, withLow = 0, dense = 0;
                    var dens = new List<float>();
                    var samples = new List<string>();
                    foreach (var s2 in track.Sections.Take(int.Parse(args.Length > 2 ? args[2] : "60")))
                    {
                        var data = track.Load(s2, textures: false, solids: true, scenery: true);
                        foreach (var so in data.Solids) solids[so.Hash] = so;
                        foreach (var g in data.Scenery)
                            foreach (var inf in g.Infos)
                            {
                                infos++;
                                uint h0 = inf.SolidHashes[0] != 0 ? inf.SolidHashes[0] : inf.SolidHashes[1];
                                uint h2 = inf.SolidHashes[2] != 0 ? inf.SolidHashes[2] : inf.SolidHashes[1];
                                if (h2 != 0 && h2 != h0) withLow++;
                                if (solids.TryGetValue(h0, out var so) && so.NumPolys > 39)
                                {
                                    dense++;
                                    dens.Add(so.Density);
                                    float r = inf.Radius + 6f;
                                    // H for a 480-line view at a 55-degree vertical FOV
                                    float H = 240f / MathF.Tan(55f * MathF.PI / 360f);
                                    float pxDetail = 8.7f * MathF.Max(so.Density, 6f);
                                    float dDetail = r * H / pxDetail + r, dCull = r * H / 18f + r;
                                    if (samples.Count < 12 && h2 != h0) samples.Add($"{inf.Name} r {inf.Radius:0.0} polys {so.NumPolys} density {so.Density:0.0} -> low model beyond {dDetail:0} m, gone beyond {dCull:0} m");
                                }
                            }
                    }
                    dens.Sort();
                    Console.WriteLine($"infos {infos}, with a separate low model {withLow}, detailed (40+ polys) {dense}");
                    if (dens.Count > 0) Console.WriteLine($"density p10 {dens[dens.Count / 10]:0.0} median {dens[dens.Count / 2]:0.0} p90 {dens[dens.Count * 9 / 10]:0.0}");
                    foreach (var l in samples) Console.WriteLine("  " + l);
                }
                break;
            case "roadcheck":
                {
                    // roadcheck: the extended road reader against specs/ai_racer.md §1 (counts, profile 1, lengths, barriers)
                    var bun = GameFiles.ReadBundle(Path.Combine(root, "TRACKS", "L2RA.BUN"));
                    var net = RoadNetwork.Read(bun);
                    Console.WriteLine($"profiles {net.Profiles.Length} nodes {net.Nodes.Length} segments {net.Segs.Length} roads {net.RoadInfos.Length}");
                    var p1 = net.Profiles[1];
                    Console.WriteLine($"profile 1: zones {p1.Zones} mid {p1.Middle} types [{string.Join(",", p1.Lanes.Take(p1.Zones).Select(l => l.Type))}] offsets [{string.Join(",", p1.Lanes.Take(p1.Zones).Select(l => l.Offset.ToString("0.#")))}] widths [{string.Join(",", p1.Lanes.Take(p1.Zones).Select(l => l.Width.ToString("0.#")))}]");
                    var ratios = new List<float>();
                    int curved = 0, oneway = 0, degree = 0;
                    foreach (var sg in net.Segs)
                    {
                        var a = net.Nodes[sg.A]; var b = net.Nodes[sg.B];
                        float chord = MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
                        if (chord > 1) ratios.Add(sg.Length / chord);
                        if ((sg.Flags & RoadNetwork.SegCurved) != 0) curved++;
                        if ((sg.Flags & RoadNetwork.SegOneWay) != 0) oneway++;
                    }
                    foreach (var l in net.NodeSegments) degree += l.Count;
                    ratios.Sort();
                    Console.WriteLine($"len/chord median {ratios[ratios.Count / 2]:0.000}, curved {curved}, one-way {oneway}, sum degree {degree} (2x segments {2 * net.Segs.Length})");
                    var s0 = net.Segs.First(x => (x.Flags & RoadNetwork.SegCurved) != 0);
                    var n0 = net.Nodes[s0.A]; var n1 = net.Nodes[s0.B];
                    Console.WriteLine($"curved seg: A ({n0.X:0.0},{n0.Y:0.0}) B ({n1.X:0.0},{n1.Y:0.0}) start handle ({s0.StartHandle.X:0.0},{s0.StartHandle.Y:0.0}) end handle ({s0.EndHandle.X:0.0},{s0.EndHandle.Y:0.0}) len {s0.Length:0.0}");
                    var bars = RoadNetwork.ReadBarriers(bun);
                    Console.WriteLine($"path barriers {bars.Count}, player {bars.Count(x => x.PlayerBarrier)}");
                    foreach (var x in bars.Take(3)) Console.WriteLine($"  ({x.X0:0},{x.Y0:0})-({x.X1:0},{x.Y1:0}) group {x.GroupHash:X8}");
                }
                break;
            case "carpartdb":
                {
                    // carpartdb [car] [slot]: the decoded CarPartDB (GlobalB 0x80034602) against specs/customization.md §1-2
                    var gb = GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GlobalB.lzc"));
                    foreach (var c in Chunks.FindAll(gb, 0, gb.Length, 0x80034602))
                        foreach (var k in Chunks.Children(gb, c)) Console.WriteLine($"  child {k}");
                    var db = CarPartDb.Read(gb);
                    Console.WriteLine($"version {db.Version} parts {db.Parts.Count} typenames {db.TypeNames.Length} cartypes {db.CarTypes.Count}");
                    string car = args.Length > 2 ? args[2] : "GTI";
                    if (db.CarTypes.TryGetValue(car, out var ct)) Console.WriteLine($"{car}: rims {ct.WheelInnerMin}..{ct.WheelInnerMax} outer {ct.WheelOuterRadius} base {ct.BaseModel} maker {ct.Manufacturer}");
                    var names = new Dictionary<uint, string>();
                    foreach (var f in typeof(CarPartDb.Attr).GetFields()) names[(uint)f.GetValue(null)] = f.Name;
                    string An(uint h) => names.TryGetValue(h, out var s) ? s : h.ToString("X8");
                    int[] slots = args.Length > 3 ? new[] { int.Parse(args[3]) } : new[] { 23, 44, 62, 63, 66, 76, 77, 78, 79, 83, 99, 105, 115, 131, 132, 133 };
                    foreach (int slot in slots)
                    {
                        var list = db.PartsFor(car, slot).ToList();
                        var (t0, t1) = db.TypesForSlot(slot, car);
                        Console.WriteLine($"slot {slot} (part id {CarPartDb.PartIdForSlot(slot)}, types {t0:X8}/{t1:X8}): {list.Count} parts");
                        foreach (var p in list.Take(args.Length > 3 ? 400 : 12))
                            Console.WriteLine($"   #{p.Index} {p.Name} lvl {p.Level} grp {p.Group} sel {p.BaseModelSelector} model {p.ModelSuffix()} [{string.Join(", ", p.Attributes.Select(a => An(a.hash) + "=" + (a.hash == CarPartDb.Attr.BRAND_NAME ? (names.TryGetValue(a.param, out var bn) ? bn : a.param.ToString("X8")) : a.param.ToString())))}]");
                    }
                    var byId = db.Parts.GroupBy(p => p.PartId).OrderBy(g => g.Key);
                    Console.WriteLine("part ids: " + string.Join(" ", byId.Select(g => $"{g.Key}:{g.Count()}")));
                }
                break;
            case "feresp":
                {
                    // message responses (MsgR) and script events of one package: object, message -> Ru values
                    var all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameB.lzc")));
                    foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")))) all.TryAdd(kv.Key, kv.Value);
                    var pk = all[args[2]];
                    foreach (var o in pk.Objects)
                    {
                        foreach (var r in o.Responses) Console.WriteLine($"{o.Hash:X8} g{o.Guid:X} msg {r.msg:X8} -> {r.target:X8}");
                        foreach (var sc in o.Scripts) foreach (var ev in sc.Events) Console.WriteLine($"{o.Hash:X8} g{o.Guid:X} script {sc.Id:X8} @{ev.time} emits {ev.message:X8} to {ev.target:X8}");
                    }
                }
                break;
            case "tcdata": ProbeTraffic.Run(root, args); break;
            case "trafphys": ProbeTrafficPhysics.Run(root, args); break;
            case "smack": ProbeSmack.Run(root, args); break;
            case "physcore": ProbePhysicsCore.Run(root, args); break;
            case "physcol": ProbePhysCollision.Run(root, args); break;
            case "shoprules":
                {
                    // shoprules: ShopRules tables against specs/customization.md §2.3, §3.4, §4.1, §6 and physics.md §2
                    var g = Path.Combine(root, "GLOBAL");
                    var db = AttribDatabase.LoadAll(Path.Combine(g, "attributes.bin"), Path.Combine(g, "gameplay.bin"), Path.Combine(g, "FE_ATTRIB.bin"));
                    var ents = new[] { 8, 7, 6, 10, 4, 5, 9, 11, 12, 13, 14, 15, 17, 18, 23, 24, 40, 43, 44, 46, 48 };
                    foreach (int e in ents)
                        Console.WriteLine($"cost ent {e,2}: " + string.Join(" ", Enumerable.Range(0, 8).Select(l => ShopRules.PartCost(db, e, l))));
                    foreach (var car in new[] { "gti", "997s", "911turbo", "carreragt", "bmwm3gtre46", "supra" })
                        Console.WriteLine($"{car}: N " + string.Join("/", Enumerable.Range(0, 7).Select(t => ShopRules.NumPackages(db, car, t)))
                            + "  prices " + string.Join(" | ", Enumerable.Range(0, 7).Select(t => ShopRules.PerfTypes[t] + " " + string.Join(",", Enumerable.Range(1, ShopRules.NumPackages(db, car, t)).Select(l => ShopRules.PerfCost(db, car, t, l))))));
                    foreach (int s in new[] { 23, 76, 77, 63, 44, 66, 78, 62, 83, 131, 105, 132 })
                        Console.WriteLine($"cooling slot {s}: {ShopRules.CoolingFactor(db, s, false):0.####} backroom {ShopRules.CoolingFactor(db, s, true):0.####}");
                    for (int t = 0; t < 7; t++)
                        Console.WriteLine($"junkman {ShopRules.PerfTypes[t]}: " + string.Join(", ", ShopRules.JunkmanMods(db, t).Select(m => $"{(db.Names.TryGetValue(m.cls, out var cn) ? cn : m.cls.ToString("X8"))}.{(db.Names.TryGetValue(m.field, out var fn) ? fn : m.field.ToString("X8"))} x{m.scale:0.###}")));
                    for (int b = 16; b >= 1; b--)
                    {
                        var u = ShopRules.BaselineUnlocks(db, b);
                        if (u.Count > 0) Console.WriteLine($"bin {b}: " + string.Join(", ", u.Select(x => $"{x.part} {x.level} (ent {ShopRules.AwardEntity(x.part, x.level)})")));
                    }
                    var gb = GameFiles.ReadBundle(Path.Combine(g, "GlobalB.lzc"));
                    var parts = CarPartDb.Read(gb);
                    var presets = ShopRules.PresetRides(gb);
                    foreach (var kv in presets.Where(k => k.Key.StartsWith("BL")).OrderBy(k => k.Key).Take(4))
                    {
                        var hits = new List<string>();
                        for (int s = 0; s < 139; s++)
                        {
                            if (kv.Value.parts[s] == 0) continue;
                            var p = parts.PartsFor(kv.Value.car, s).FirstOrDefault(x => x.NameHash == kv.Value.parts[s]);
                            if (p != null && (s == 23 || s == 44 || s == 62 || s == 63 || s == 66 || s == 76 || s == 77 || s == 78 || s == 131)) hits.Add($"{s}:{p.Name}(L{p.Level})");
                        }
                        Console.WriteLine($"preset {kv.Key}: {kv.Value.car} physics {kv.Value.physicsLevel} {string.Join(" ", hits)}");
                    }
                    // unlock simulation: the bin at which each package of a car opens (front room / backroom)
                    var lv = new List<int>(new int[ShopRules.UnlockCount]);
                    var openAt = new Dictionary<string, (int front, int back)>();
                    for (int b = 16; b >= 1; b--)
                    {
                        foreach (var (part, level) in ShopRules.BaselineUnlocks(db, b)) { int e = ShopRules.AwardEntity(part, level); if (e > 0) lv[e] = level; }
                        foreach (var car in new[] { "gti", "carreragt" })
                            for (int t = 0; t < 7; t++)
                                for (int l = 1; l <= ShopRules.NumPackages(db, car, t); l++)
                                {
                                    string k = $"{car} {ShopRules.PerfTypes[t]} {l}";
                                    int tier = ShopRules.Tier(t, ShopRules.NumPackages(db, car, t), l);
                                    var cur = openAt.TryGetValue(k, out var v) ? v : (0, 0);
                                    if (cur.Item1 == 0 && ShopRules.IsUnlocked(lv, ShopRules.PerfUnlockable(t), tier, false, false)) cur.Item1 = b;
                                    if (cur.Item2 == 0 && ShopRules.IsUnlocked(lv, ShopRules.PerfUnlockable(t), tier, true, false)) cur.Item2 = b;
                                    openAt[k] = cur;
                                }
                    }
                    Console.WriteLine("package opens at bin (front/backroom): " + string.Join("; ", openAt.Select(kv => $"{kv.Key}: {kv.Value.front}/{kv.Value.back}")));
                    Console.WriteLine($"markers: {string.Join(",", new[] { "brakes", "backroom_rims", "MARKER_VINYL", "out_of_jail_free", "cash_bonus", "pink_slip", "add_impound_box", "release_car_from_impound", "hud" }.Select(n => n + "=" + ShopRules.MarkerOf(n)))}");
                }
                break;
            case "emitters": ProbeEmitters.Run(root, args); break;
            case "lang":
                {
                    // lang <hex or LABEL,...>: English.bin strings by label hash or label name ("LABEL*" lists LABEL0..LABEL40)
                    var st = StringTable.Read(GameFiles.ReadBundle(Path.Combine(root, "LANGUAGES", "English.bin")));
                    foreach (var a in args[2].Split(','))
                    {
                        var keys = a.EndsWith("*") ? Enumerable.Range(0, 41).Select(i => a.TrimEnd('*') + i) : new[] { a };
                        foreach (var k in keys)
                        {
                            uint h = System.Text.RegularExpressions.Regex.IsMatch(k, "^[0-9A-Fa-f]{8}$") ? Convert.ToUInt32(k, 16) : EaHash.HashUpper(k);
                            var s = st.Get(h);
                            if (s != null || !a.EndsWith("*")) Console.WriteLine($"{k} {h:X8} = {s?.Replace("\n", "\\n")}");
                        }
                    }
                }
                break;
            case "spchevt": SpeechProbe.Run(root, args); break;
            case "shopcheck":
                {
                    // shopcheck [car]: CarPartDb lookups the Unity shop relies on (numbers, decals, vinyl textures, rims, stock parts)
                    string car = args.Length > 2 ? args[2] : "GTI";
                    var db = CarPartDb.Load(root);
                    uint nl = EaHash.Hash("NUMBER_LEFT"), nr = EaHash.Hash("NUMBER_RIGHT"), nameAttr = EaHash.Hash("NAME");
                    Console.WriteLine($"numbers: slot113 NUMBER_LEFT {db.PartsFor(car, 113).Count(p => p.Brand == nl)}, slot114 NUMBER_RIGHT {db.PartsFor(car, 114).Count(p => p.Brand == nr)}  e.g. {string.Join(",", db.PartsFor(car, 113).Where(p => p.Brand == nl).Take(4).Select(p => p.Name + "/L" + p.Level))}");
                    var dec = db.PartsFor(car, 83).ToList();
                    Console.WriteLine($"decals slot 83: {dec.Count}, black {dec.Count(p => p.U(nameAttr) == p.Brand)}, white {dec.Count(p => p.U(nameAttr) == EaHash.Hash("_WHITE", p.Brand))}, levels {string.Join(",", dec.Select(p => p.Level).Distinct())}");
                    var vb = Path.Combine(root, "CARS", car, "VINYLS.BIN");
                    var tex = File.Exists(vb) ? TexturePack.ReadAll(File.ReadAllBytes(vb), 0, (int)new FileInfo(vb).Length).Select(t => t.Name).ToList() : new List<string>();
                    var vin = db.PartsFor(car, 77).ToList();
                    int byName = vin.Count(p => tex.Contains(car + "_" + p.Name)), byHash = vin.Count(p => tex.Any(n => EaHash.Hash(n) == p.TextureHash));
                    Console.WriteLine($"vinyls: {vin.Count} parts, {tex.Count} textures, matched by CAR_NAME {byName}, by TEXTURE_NAME hash {byHash}; groups {string.Join(",", vin.GroupBy(p => p.Group).Select(g => g.Key + ":" + g.Count()))}; unmatched e.g. {string.Join(",", vin.Where(p => !tex.Contains(car + "_" + p.Name)).Take(5).Select(p => p.Name))}");
                    Console.WriteLine($"  textures e.g. {string.Join(",", tex.Take(6))}");
                    var rims = db.PartsFor(car, 66).Where(p => p.BaseModelSelector == 2).ToList();
                    var wheels = SolidList.ReadAll(File.ReadAllBytes(Path.Combine(root, "CARS", "WHEELS", "GEOMETRY.BIN")), 0, int.MaxValue).Select(s => s.Name).ToList();
                    var prefixes = wheels.Where(n => n.Contains("_STYLE")).Select(n => n.Substring(0, n.IndexOf("_STYLE"))).Distinct().ToList();
                    var brandOf = prefixes.ToDictionary(x => EaHash.Hash(x), x => x);
                    Console.WriteLine($"rims: {rims.Count}, brands {string.Join(",", rims.Select(p => p.Brand).Distinct().Select(b => brandOf.TryGetValue(b, out var n) ? n : b.ToString("X8")))}; model found {rims.Count(p => wheels.Contains((brandOf.TryGetValue(p.Brand, out var bn) ? bn : "?") + p.ModelSuffix()))}");
                    foreach (int s in new[] { 23, 44, 62, 63, 66, 131, 132 })
                    {
                        uint own = EaHash.Hash(car);
                        var stock = db.PartsFor(car, s, 0).FirstOrDefault(p => p.CarTypeHash == own) ?? db.PartsFor(car, s, 0).FirstOrDefault();
                        Console.WriteLine($"stock slot {s}: {stock?.Name} ({stock?.ModelSuffix()})");
                    }
                }
                break;
            case "audio": AudioProbe.Run(root, args); break;
            case "audiofx": AudioFxProbe.Run(root, args); break;
            case "aems": AemsProbe.Run(root, args); break;
            case "movie": MovieProbe.Run(root, args); break;
            case "music": MusicProbe.Run(root, args); break;
            case "spchai": SpchAiProbe.Run(root, args); break;
            case "copzones": CopZonesProbe.Run(root, args); break;
            case "aiprobe": ProbeAi.Run(root, args); break;
            case "smokecsv":
                {
                    using var track = new TrackStream(root);
                    var outp = new System.Text.StringBuilder();
                    void Walk(byte[] d, int start, int end, string sec, int depth)
                    {
                        foreach (var c in Chunks.Read(d, start, end))
                        {
                            if ((c.Id & 0x80000000) != 0 && depth < 4) Walk(d, c.DataStart, c.End, sec, depth + 1);
                            if (c.Id != 0x34027) continue;
                            int o = c.DataStart;
                            while (o < c.End && d[o] == 0x11) o++;
                            int n = BitConverter.ToInt32(d, o + 12);
                            o += 16;
                            for (int i = 0; i < n && o + 0x40 <= c.End; i++, o += 0x40)
                                outp.AppendLine($"{sec},{BitConverter.ToUInt32(d, o + 32):X8},{BitConverter.ToUInt32(d, o + 40):X8},{BitConverter.ToUInt32(d, o + 44)},{BitConverter.ToUInt32(d, o + 52):X8},{BitConverter.ToSingle(d, o + 16):0.##},{BitConverter.ToSingle(d, o + 20):0.##},{BitConverter.ToSingle(d, o + 24):0.##}");
                        }
                    }
                    foreach (var s2 in track.Sections) { var raw = track.ReadRaw(s2); Walk(raw, 0, raw.Length, s2.Name, 0); }
                    File.WriteAllText(args[2], outp.ToString());
                    // solid names from every section for CRC matching
                    var names = new System.Collections.Generic.HashSet<string>();
                    foreach (var s2 in track.Sections)
                    {
                        var data = track.Load(s2, textures: false, solids: true, scenery: true);
                        foreach (var so in data.Solids) names.Add(so.Name);
                        foreach (var g in data.Scenery) foreach (var inf in g.Infos) names.Add(inf.Name);
                    }
                    File.WriteAllLines(args[3], names);
                    Console.WriteLine($"{outp.Length} chars, {names.Count} names");
                }
                break;
            case "crcmatch":
                {
                    var rows = File.ReadAllLines(args[2]).Select(l => l.Split(',')).ToList();
                    var names = File.ReadAllLines(args[3]).Concat(new AttribDatabase[] { AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin")) }.SelectMany(db => db.OfClass("smackable").Select(c => c.Name ?? ""))).ToList();
                    var map = new System.Collections.Generic.Dictionary<uint, string>();
                    foreach (var n in names)
                        foreach (var v in new[] { n, n.ToLowerInvariant(), n.ToUpperInvariant() })
                            map[AttribDatabase.Hash(v)] = v;
                    int mm = 0, ma = 0;
                    var mset = new System.Collections.Generic.HashSet<string>(); var aset = new System.Collections.Generic.HashSet<string>();
                    foreach (var r in rows)
                    {
                        if (map.TryGetValue(Convert.ToUInt32(r[1], 16), out var mn)) { mm++; mset.Add(mn); }
                        if (map.TryGetValue(Convert.ToUInt32(r[2], 16), out var an)) { ma++; aset.Add(an); }
                    }
                    Console.WriteLine($"models matched {mm}/{rows.Count} ({mset.Count} names), attrs matched {ma}/{rows.Count} ({aset.Count} names)");
                    Console.WriteLine(string.Join(" ", mset.Take(25)));
                    Console.WriteLine(string.Join(" ", aset.Take(40)));
                }
                break;
            case "hashnames":
                {
                    // hashnames <hex,hex,...>: names from speed.exe's strings whose attribute hash matches
                    var want = new System.Collections.Generic.HashSet<uint>(args[2].Split(',').Select(h => Convert.ToUInt32(h, 16)));
                    var exe = File.ReadAllBytes(Path.Combine(root, "speed.exe"));
                    var seen = new System.Collections.Generic.HashSet<string>();
                    int st = -1;
                    for (int i = 0; i <= exe.Length; i++)
                    {
                        bool pr = i < exe.Length && exe[i] >= 0x20 && exe[i] < 0x7F;
                        if (pr && st < 0) st = i;
                        if (!pr && st >= 0)
                        {
                            if (i - st >= 2 && i - st < 64)
                            {
                                var str = System.Text.Encoding.ASCII.GetString(exe, st, i - st);
                                foreach (var cand in new[] { str, str.ToUpperInvariant(), str.ToLowerInvariant() })
                                    if (want.Contains(AttribDatabase.Hash(cand)) && seen.Add(cand)) Console.WriteLine($"{AttribDatabase.Hash(cand):X8} {cand}");
                            }
                            st = -1;
                        }
                    }
                }
                break;
            case "attribdump":
                {
                    // attribdump <file under root> [filter]: every collection with every field (floats and ints)
                    var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, args[2]));
                    foreach (var kv in db.Collections)
                    {
                        var c = kv.Value;
                        string cls = db.Names.TryGetValue(c.ClassKey, out var cn) ? cn : c.ClassKey.ToString("X8");
                        if (args.Length > 3 && !(cls + "/" + c.Name).Contains(args[3])) continue;
                        Console.WriteLine($"{cls}/{c.Name ?? c.Key.ToString("X8")} parent {c.Parent?.Name}");
                        if (c.Class == null) { Console.WriteLine("   (no class)"); continue; }
                        foreach (var f in c.Class.Fields)
                        {
                            string fn = db.Names.TryGetValue(f.Key, out var n) ? n : f.Key.ToString("X8");
                            if (!c.TryGetKey(f.Key, out int at, out int count, out var def)) continue;
                            var src = c.Source.Data;
                            var vals = new System.Collections.Generic.List<string>();
                            for (int i = 0; i < Math.Min(count, 16); i++)
                            {
                                float fv = BitConverter.ToSingle(src, at + i * 4);
                                int iv = BitConverter.ToInt32(src, at + i * 4);
                                vals.Add(Math.Abs(fv) > 1e-4 && Math.Abs(fv) < 1e7 ? fv.ToString("0.###") : iv.ToString());
                            }
                            Console.WriteLine($"   {fn} = [{string.Join(",", vals)}]");
                        }
                    }
                }
                break;
            case "attribclass":
                {
                    // attribclass <class> <field,field,...>: every collection of a class with the given fields as floats
                    var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
                    var fields = args.Length > 3 ? args[3].Split(',') : new string[0];
                    foreach (var c in db.OfClass(args[2]))
                    {
                        var sb = new System.Text.StringBuilder(c.ToString());
                        foreach (var f in fields)
                        {
                            // name:4 reads a Vector4 at the field
                            var parts = f.Split(':');
                            if (parts.Length == 2 && c.TryGet(parts[0], out int at, out _, out _))
                                sb.Append($"  {parts[0]}=({string.Join(",", Enumerable.Range(0, int.Parse(parts[1])).Select(k => Chunks.F32(c.Source.Data, at + 4 * k).ToString("0.###")))})");
                            else if (c.Has(f)) sb.Append($"  {f}=[{string.Join(",", c.Floats(f).Select(x => x.ToString("0.###")))}]");
                        }
                        Console.WriteLine(sb);
                    }
                }
                break;
            case "gpfields":
                {
                    // gpfields <collection> <field,...>: typed reads of named fields of a gameplay.bin collection
                    var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"));
                    var c = db.Get("gameplay", args[2]);
                    if (c == null) { Console.WriteLine("no collection"); break; }
                    Console.WriteLine($"{c} parent {c.Parent?.Name}");
                    foreach (var f in args[3].Split(','))
                    {
                        if (!c.Has(f)) { Console.WriteLine($"  {f}: -"); continue; }
                        string keys = "";
                        try { keys = string.Join(",", c.Keys(f).Select(k => db.Collections.Values.FirstOrDefault(x => x.Key == k)?.Name ?? k.ToString("X8"))); } catch { }
                        string txt = ""; try { txt = c.Text(f); } catch { }
                        string vec = ""; try { var v = c.Vector3(f); vec = $"{v.X:0.#},{v.Y:0.#},{v.Z:0.#}"; } catch { }
                        Console.WriteLine($"  {f}: int {c.Int(f)} float {c.Float(f):0.###} bool {c.Bool(f)} text '{txt}' keys [{keys}] vec {vec}");
                    }
                }
                break;
            case "carrefs":
                {
                    var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
                    var pv = db.Get("pvehicle", args[2]);
                    Console.WriteLine($"pvehicle {pv} junk {(pv.Has("CDC136E8") ? pv.Int("CDC136E8") : -1)} mass {pv.Float("MASS")}");
                    foreach (var part in new[] { "tires", "brakes", "chassis", "transmission", "engine", "induction", "nos" })
                    {
                        var refs = pv.Refs(part);
                        int n = pv.Has(part + "_upgrades") ? pv.Int(part + "_upgrades") : -1;
                        Console.WriteLine($"  {part} upgrades {n}: " + string.Join(", ", refs.Select(r => db.Collections.TryGetValue(r, out var c) ? (c.Name ?? c.Key.ToString("X8")) : $"?{r.Item2:X8}")));
                    }
                }
                break;
            case "platform":
                {
                    var d = GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "PLATFORMS", args[2]));
                    foreach (var c in Chunks.Read(d, 0, d.Length)) Console.WriteLine($"chunk {c.Id:X8} size {c.Size}");
                    var solids = SolidList.ReadAll(d, 0, d.Length);
                    Console.WriteLine($"solids {solids.Count}, textures {TexturePack.ReadAll(d, 0, d.Length, false).Count}, scenery groups {Scenery.ReadAll(d, 0, d.Length).Count}");
                    foreach (var s in solids.Take(80))
                    {
                        Console.WriteLine($"  {s.Name} parts {s.Parts.Count}");
                    }
                    foreach (var g in Scenery.ReadAll(d, 0, d.Length))
                        Console.WriteLine($"  group infos {g.Infos.Count} instances {g.Instances.Count}: {string.Join(" ", g.Infos.Take(12).Select(i => i.Name))}");
                }
                break;
            case "flags": Probe8.Flags(root); break;
            case "bit": Probe9.Bit(root, int.Parse(args[2])); break;
            case "tex": Probe11.TexStats(root, args[2], args[3]); break;
            case "fe": ProbeFe.Fe(root); break;
            case "fecust": ProbeFeCustomize.Run(root, args); break;
            case "feig": ProbeFeInGame.Run(root, args); break;
            case "ferest": ProbeFeRest.Run(root, args); break;
            case "feeng9": ProbeFeEng9.Run(root, args); break;
            case "hudpk": ProbeHudPk.Run(root); break;
            case "bank": ProbeBank.Run(root, args[2]); break;
            case "engine": ProbeEngine.Run(root, args[2]); break;
            case "preset": ProbePreset.Run(root); break;
            case "roads": ProbeRoads.Run(root); break;
            case "heading": ProbeHeading.Run(root); break;
            case "fenames": ProbeFeNames.Run(root, args[2]); break;
            case "fetree": ProbeFeNames.Tree(root, args[2]); break;
            case "fescr":
                {
                    var all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameB.lzc")));
                    if (!all.ContainsKey(args[2])) all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")));
                    var pk = all[args[2]];
                    foreach (var o in pk.Objects.Where(o => o.Guid.ToString("X") == args[3] || o.Hash.ToString("X8") == args[3]))
                        foreach (var sc in o.Scripts)
                        {
                            Console.WriteLine($"script {sc.Id:X8} len {sc.Length} chain {sc.Chain:X8}");
                            foreach (var t in sc.Tracks)
                                Console.WriteLine($"   off 0x{t.Offset:X} keys " + string.Join(" | ", t.Keys.Select(k => k.time + ":" + string.Join(",", k.value.Select(v => v.ToString("0.##"))))));
                        }
                }
                break;
            case "sgroups": ProbeGroups.Run(root); break;
            case "markers":
                {
                    var d = GameFiles.ReadBundle(Path.Combine(root, "CARS", args[2], "GEOMETRY.BIN"));
                    foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x80134010))
                    {
                        string name = null;
                        foreach (var k in Chunks.Children(d, c))
                        {
                            if (k.Id == 0x134011) name = Chunks.CString(d, Chunks.Align(k.DataStart, 16) + 0xA0, 64);
                            if (k.Id == 0x13401A && (args.Length < 4 || (name ?? "").Contains(args[3])))
                            {
                                Console.WriteLine($"{name}: markers {k.Size} bytes");
                                for (int o = k.DataStart; o < k.End; o += 16)
                                {
                                    var row = new System.Text.StringBuilder();
                                    for (int j = 0; j < 4 && o + j * 4 < k.End; j++) row.Append($"{Chunks.U32(d, o + j * 4):X8}/{Chunks.F32(d, o + j * 4),9:0.###} ");
                                    Console.WriteLine("   " + row);
                                }
                            }
                        }
                    }
                }
                break;
            case "solidinfo":
                {
                    var d = GameFiles.ReadBundle(Path.Combine(root, "CARS", args[2], "GEOMETRY.BIN"));
                    foreach (var so in SolidList.ReadAll(d, 0, d.Length).Where(x => x.Name.Contains(args[3])).Take(8))
                        Console.WriteLine($"{so.Name}: min {so.BoundsMin} max {so.BoundsMax} parts {so.Parts.Count} mats {string.Join(",", so.Parts.Select(pp => pp.MaterialName).Distinct())} tex {string.Join(",", so.Parts.Select(pp => pp.TextureHash.ToString("X8")).Distinct())}");
                }
                break;
            case "fepkgs":
                Console.WriteLine(string.Join(" ", FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, args.Length > 2 ? args[2] : "FRONTEND/FrontB.lzc"))).Keys.OrderBy(k => k)));
                break;
            case "challenges":
                {
                    var c = CareerData.Load(root);
                    var bin = c.Bins[19];
                    Console.WriteLine($"{bin.WorldRaces.Count} challenges; CS presets: {string.Join(" ", c.PresetCars.Where(kv => kv.Key.StartsWith("CS_")).Select(kv => kv.Key + "=" + kv.Value))}");
                    foreach (var e in bin.WorldRaces.Take(70))
                        Console.WriteLine($"{e.EventId,-8} {e.Type,-10} car {e.PlayerCar}->{c.CarForPreset(e.PlayerCar)} start {(e.Start != null)} cps {e.Checkpoints.Count} bonus {e.Checkpoints.Sum(x => x.TimeBonus)} limit {e.TimeLimit} {e.MilestoneType} {e.Threshold} heat {e.ForceHeat} cops {e.CopSpawns.Count} speed {e.InitialSpeed} unlocked {e.InitiallyUnlocked} unlocks {e.UnlockRaces.Count}");
                }
                break;
            case "speech":
                {
                    var idx = File.ReadAllBytes(Path.Combine(root, "SOUND", "SPEECH", "copspeech.idx"));
                    var big = File.ReadAllBytes(Path.Combine(root, "SOUND", "SPEECH", "copspeech.big"));
                    int count = Chunks.I32(idx, 4);
                    string outDir = args[2];
                    int done = 0;
                    foreach (int k in new[] { 1, 2, 6, 9, 40, 120, 300, 500 })
                    {
                        if (k >= count) continue;
                        int off = Chunks.I32(idx, 0x54 + 4 + k * 16 + 12);
                        var st = EaStream.Read(big, off, out int next);
                        var x = st.Data[0];
                        double rms = Math.Sqrt(x.Average(v => (double)v * v));
                        int clip = x.Count(v => Math.Abs(v) > 0.99f);
                        // voiced periodicity: best normalised autocorrelation at 70..300 Hz over 40 ms windows
                        int w = st.SampleRate / 25, voiced = 0, frames = 0;
                        for (int a = 0; a + w + st.SampleRate / 70 < x.Length; a += w)
                        {
                            double e = 0; for (int i = 0; i < w; i++) e += x[a + i] * x[a + i];
                            if (e / w < 1e-4) continue;
                            frames++;
                            double best = 0;
                            for (int lag = st.SampleRate / 300; lag <= st.SampleRate / 70; lag++)
                            {
                                double c = 0, e2 = 0;
                                for (int i = 0; i < w; i++) { c += x[a + i] * x[a + i + lag]; e2 += x[a + i + lag] * x[a + i + lag]; }
                                best = Math.Max(best, c / Math.Sqrt(e * e2 + 1e-12));
                            }
                            if (best > 0.6) voiced++;
                        }
                        Console.WriteLine($"stream {k} @{off:X}: {st.Samples} samples {st.SampleRate} Hz ({st.Samples / (float)st.SampleRate:0.00} s) codec {st.Codec2} v{st.Version}, rms {rms:0.000}, clipped {clip}, voiced frames {voiced}/{frames}");
                        using var wav = new BinaryWriter(File.Create(Path.Combine(outDir, $"cop{k}.wav")));
                        wav.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wav.Write(36 + x.Length * 2); wav.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16); wav.Write((short)1); wav.Write((short)1);
                        wav.Write(st.SampleRate); wav.Write(st.SampleRate * 2); wav.Write((short)2); wav.Write((short)16); wav.Write(System.Text.Encoding.ASCII.GetBytes("data")); wav.Write(x.Length * 2);
                        foreach (var v in x) wav.Write((short)(Math.Clamp(v, -1f, 1f) * 32767));
                        done++;
                    }
                }
                break;
            case "nis": ProbeNis.Run(root, args); break;
            case "anim9": ProbeAnim9.Run(root, args); break;
            case "camkeys": ProbeCamera.Run(root, args); break;
            case "camnoise": ProbeCameraNoise.Run(root); break;
            case "nisload": ProbeNisLoad.Run(root, args); break;
            case "nistracks":
                {
                    NisLibrary.Load(root);
                    int scenes = 0, tracks = 0;
                    foreach (var sc in NisLibrary.Scenes.Values.OrderBy(x => x.Name))
                    {
                        int n = sc.Tracks.Count;
                        tracks += n;
                        if (n > 0) scenes++;
                        string cars = string.Join("  ", Enumerable.Range(1, 8).Select(i => "Car" + i).Concat(Enumerable.Range(1, 8).Select(i => "Cop" + i))
                            .Where(a => sc.Pose(a, 999, out _, out _)).Select(a => { sc.Pose(a, 0, out var p0, out var h0); sc.Pose(a, 999, out var p1, out var h1); return $"{a} ({p0.X:0.0},{p0.Y:0.0})@{h0:0}->({p1.X:0.0},{p1.Y:0.0})@{h1:0} {sc.TrackLength(a):0.0}s"; }));
                        if (n > 0 && (args.Length < 3 || sc.Name.Contains(args[2]))) Console.WriteLine($"{sc.Name,-18} {n,3} tracks  {cars}");
                    }
                    Console.WriteLine($"{scenes} scenes with tracks, {tracks} tracks");
                }
                break;
            case "movies":
                {
                    var c = CareerData.Load(root);
                    var names = c.Events.Values.SelectMany(e => new[] { e.IntroMovie, e.OutroMovie }).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x);
                    Console.WriteLine(string.Join(" ", names));
                    var exe = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(root, "speed.exe")));
                    Console.WriteLine(string.Join(" ", System.Text.RegularExpressions.Regex.Matches(exe, @"[A-Za-z0-9_%]+\.vp6|MOVIES\[A-Za-z0-9_%\.]+").Select(m => m.Value).Distinct()));
                }
                break;
            case "musiccheck":
                {
                    using var f = File.OpenRead(args[2]);
                    var buf = new byte[32 << 20];
                    int n = f.Read(buf, 0, buf.Length);
                    int found = 0;
                    for (int i = 0; i + 4 < n && found < 4; i++)
                        if (buf[i] == 'S' && buf[i + 1] == 'C' && buf[i + 2] == 'H' && buf[i + 3] == 'l')
                        {
                            var st = EaStream.Read(buf, i, out int next);
                            var x = st.Data[0];
                            double rms = Math.Sqrt(x.Average(v => (double)v * v));
                            Console.WriteLine($"stream @{i:X}: {st.Channels} ch {st.SampleRate} Hz {st.Length:0.0} s v{st.Version} gstr {st.BigEndianBlocks} rms {rms:0.000} clipped {x.Count(v => Math.Abs(v) > 0.99f)}");
                            found++;
                            i = next - 1;
                        }
                }
                break;
            case "carparts":
                {
                    var d = GameFiles.ReadBundle(Path.Combine(root, "CARS", args[2], "GEOMETRY.BIN"));
                    var names = SolidList.ReadAll(d, 0, d.Length).Select(x => x.Name).ToList();
                    Console.WriteLine($"{names.Count} solids");
                    var groups = names.GroupBy(n => System.Text.RegularExpressions.Regex.Replace(n, @"_[A-D]$", ""));
                    Console.WriteLine(string.Join("  ", groups.Select(g => g.Key)));
                }
                break;
            case "gpdump": ProbeGpDump.Run(root, args[2], args.Length > 3 ? int.Parse(args[3]) : 6); break;
            case "binbarriers":
                {
                    var c = CareerData.Load(root);
                    foreach (var b in c.Bins.Values.OrderByDescending(b => b.Index)) Console.WriteLine($"bin {b.Index}: {string.Join(" ", b.Barriers)}");
                    foreach (var e in c.Events.Values.Where(e => e.Barriers.Count > 0).Take(6)) Console.WriteLine($"event {e.Name} {e.EventId}: {string.Join(" ", e.Barriers)}");
                    Console.WriteLine($"events with barriers {c.Events.Values.Count(e => e.Barriers.Count > 0)}/{c.Events.Count}");
                }
                break;
            case "career2": ProbeCareer2.Run(root); break;
            case "carlot": ProbeCarLot.Run(root); break;
            case "fesafehouse": FeSafehouseProbe.Run(root); break;
            case "frontend": ProbeFrontend.Run(root, args[2]); break;
            case "career": ProbeCareer.Run(root); break;
            case "gameplay": ProbeGameplay.Run(root, args[2]); break;
            case "lua": LuaProbe.Run(root, args); break;
            case "r9gp": ProbeRound9Gp.Run(root, args); break;
            case "gpcore": ProbeGpCore.Run(root, args); break;
            case "wtrigger": WTriggerProbe.Run(root, args); break;
            case "phys9": ProbePhys9.Run(root, args); break;
            case "world9": ProbeWorld9.Run(root, args); break;
            case "smack9": ProbeSmack9.Run(root, args); break;
            case "audio9": ProbeAudio9.Run(root, args); break;
            case "scripts": ProbeScripts.Dump(root, args[2]); break;
            case "fe9menus": ProbeFe9Menus.Run(root, args); break;
            case "fe9sms": ProbeFe9Sms.Run(root, args); break;
            case "fe9qr": ProbeFe9QR.Run(root, args); break;
            case "fe9data": ProbeFe9Data.Run(root, args); break;
            case "just": ProbeJust.Run(root); break;
            case "texlist": ProbeTexList.List(args[2]); break;
            case "minimap": ProbeTexList.MiniMap(args[2]); break;
            case "carrender": ProbeCarRender.Run(root, args); break;
            case "carrender2": ProbeCarRender2.Run(root, args); break;
            case "render9": ProbeRender9.Run(root, args); break;
            case "sky": SkyProbe.Run(root, args); break;
            case "weather": WeatherProbe.Run(root, args); break;
            case "wrender": WorldRenderProbe.Run(root, args); break;
            case "wrefl": WorldReflectProbe.Run(root, args); break;
            case "post": PostProbe.Run(root, args); break;
            case "roadcsv":
                {
                    var net = RoadNetwork.Read(GameFiles.ReadBundle(Path.Combine(root, "TRACKS", "L2RA.BUN")));
                    using var w = new StreamWriter(args[2]);
                    foreach (var n in net.Nodes) w.WriteLine($"{n.X},{n.Y}");
                    foreach (var (a, b) in net.Segments) w.WriteLine($"s,{a},{b}");
                }
                break;
            case "near": Probe10.Near(root, float.Parse(args[2]), float.Parse(args[3]), float.Parse(args[4])); break;
            default: Console.WriteLine("modes: world cars globals section <name>"); return 2;
        }
        return 0;
    }

    static void World(string root)
    {
        using var track = new TrackStream(root);
        Console.WriteLine($"{track.Sections.Count} sections");
        var texHashes = new HashSet<uint>();
        var solidHashes = new HashSet<uint>();
        var sectionSolids = new Dictionary<string, HashSet<uint>>();
        var formats = new Dictionary<string, int>();
        var effects = new Dictionary<int, int>();
        int instances = 0, groups = 0, badNumber = 0, badInfo = 0, parts = 0, verts = 0, tris = 0;
        var refs = new List<(string sec, uint solid)>();
        var partTex = new List<uint>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var s in track.Sections)
        {
            var data = track.Load(s);
            foreach (var t in data.Textures)
            {
                texHashes.Add(t.Hash);
                string k = $"{t.Format}/{t.FourCC}";
                formats[k] = formats.GetValueOrDefault(k) + 1;
            }
            var mine = new HashSet<uint>();
            foreach (var so in data.Solids)
            {
                solidHashes.Add(so.Hash);
                mine.Add(so.Hash);
                foreach (var p in so.Parts)
                {
                    parts++;
                    verts += p.VertexCount;
                    tris += p.Indices.Length / 3;
                    effects[p.EffectId] = effects.GetValueOrDefault(p.EffectId) + 1;
                    if (p.TextureHash != 0) partTex.Add(p.TextureHash);
                }
            }
            sectionSolids[s.Name] = mine;
            foreach (var g in data.Scenery)
            {
                groups++;
                if (g.SectionNumber != s.Number) badNumber++;
                foreach (var inst in g.Instances)
                {
                    instances++;
                    if (inst.InfoIndex >= g.Infos.Count) { badInfo++; continue; }
                    refs.Add((s.Name, g.Infos[inst.InfoIndex].SolidHashes[0]));
                }
            }
        }
        Console.WriteLine($"parsed in {sw.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"textures {texHashes.Count} unique; formats: {string.Join(", ", formats.Select(kv => kv.Key + "=" + kv.Value))}");
        Console.WriteLine($"solids {solidHashes.Count} unique; parts {parts}, verts {verts}, tris {tris}");
        Console.WriteLine($"effects: {string.Join(", ", effects.OrderBy(k => k.Key).Select(kv => kv.Key + "=" + kv.Value))}");
        Console.WriteLine($"scenery groups {groups} (section number mismatch {badNumber}), instances {instances} (bad info index {badInfo})");
        int inSame = refs.Count(r => sectionSolids[r.sec].Contains(r.solid));
        int inShared = refs.Count(r => !sectionSolids[r.sec].Contains(r.solid) && sectionSolids["X0"].Contains(r.solid));
        int anywhere = refs.Count(r => solidHashes.Contains(r.solid));
        Console.WriteLine($"instance solids: same section {inSame}, X0 {inShared}, anywhere {anywhere} of {refs.Count}");
        int texOk = partTex.Count(h => texHashes.Contains(h));
        Console.WriteLine($"part textures resolved {texOk} of {partTex.Count}");
        var missing = partTex.Where(h => !texHashes.Contains(h)).GroupBy(h => h).OrderByDescending(g => g.Count()).Take(8);
        Console.WriteLine("top missing textures: " + string.Join(" ", missing.Select(g => $"{g.Key:X8}x{g.Count()}")));
        File.WriteAllLines("missing_textures.txt", partTex.Where(h => !texHashes.Contains(h)).Distinct().Select(h => h.ToString("X8")));
    }

    static void Cars(string root)
    {
        var global = new List<GameTexture>();
        foreach (var f in new[] { "GLOBAL/GLOBALB.lzc", "GLOBAL/InGameB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/InGameA.bun", "CARS/TEXTURES.BIN" })
        {
            var d = GameFiles.ReadBundle(Path.Combine(root, f));
            global.AddRange(TexturePack.ReadAll(d, 0, d.Length));
        }
        var globalHashes = new HashSet<uint>(global.Select(t => t.Hash));
        Console.WriteLine($"global textures {global.Count}");
        var effects = new Dictionary<int, int>();
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "CARS")).OrderBy(x => x))
        {
            string name = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, "GEOMETRY.BIN"))) continue;
            CarAsset car;
            try { car = CarAsset.Load(root, name); }
            catch (Exception e) { Console.WriteLine($"{name}: FAILED {e.Message}"); continue; }
            var own = new HashSet<uint>(car.Textures.Select(t => t.Hash));
            int used = 0, ok = 0, okGlobal = 0;
            foreach (var s in car.Solids)
                foreach (var p in s.Parts)
                {
                    effects[p.EffectId] = effects.GetValueOrDefault(p.EffectId) + 1;
                    if (p.TextureHash == 0) continue;
                    used++;
                    if (own.Contains(p.TextureHash)) ok++;
                    else if (globalHashes.Contains(p.TextureHash)) okGlobal++;
                }
            if (name == "BMWM3GTR")
            {
                foreach (var t in car.Textures) Console.WriteLine("    own " + t);
                var miss = new Dictionary<uint, string>();
                foreach (var s in car.Solids) foreach (var p in s.Parts)
                    if (p.TextureHash != 0 && !own.Contains(p.TextureHash) && !globalHashes.Contains(p.TextureHash)) miss[p.TextureHash] = s.Name + "/" + p.MaterialName;
                File.WriteAllLines("car_missing.txt", miss.Select(kv => $"{kv.Key:X8} {kv.Value}"));
            }
            Console.WriteLine($"{name,-18} solids {car.Solids.Count,3} textures {car.Textures.Count,3}  part tex: own {ok} global {okGlobal} of {used}");
        }
        Console.WriteLine($"car effects: {string.Join(", ", effects.OrderBy(k => k.Key).Select(kv => kv.Key + "=" + kv.Value))}");
    }

    static void Globals(string root)
    {
        foreach (var f in Directory.GetFiles(Path.Combine(root, "GLOBAL")).Concat(Directory.GetFiles(Path.Combine(root, "FRONTEND"))))
        {
            byte[] d;
            try { d = GameFiles.ReadBundle(f); } catch (Exception e) { Console.WriteLine($"{Path.GetFileName(f)}: {e.Message}"); continue; }
            var tex = TexturePack.ReadAll(d, 0, d.Length);
            var sol = SolidList.ReadAll(d, 0, d.Length);
            var top = Chunks.Read(d, 0, d.Length).GroupBy(c => c.Id).Select(g => $"{g.Key:X8}x{g.Count()}");
            Console.WriteLine($"{Path.GetFileName(f),-28} {d.Length,10} tex {tex.Count,4} solids {sol.Count,4}  {string.Join(" ", top.Take(10))}");
        }
    }

    static void Section(string root, string name)
    {
        using var track = new TrackStream(root);
        var s = track.Find(name);
        var data = track.Load(s);
        Console.WriteLine($"{s}: textures {data.Textures.Count}, solids {data.Solids.Count}, scenery groups {data.Scenery.Count}");
        foreach (var t in data.Textures.Take(10)) Console.WriteLine("  tex " + t);
        foreach (var so in data.Solids.Take(10)) Console.WriteLine($"  solid {so.Name} {so.BoundsMin}..{so.BoundsMax} parts {so.Parts.Count}");
        foreach (var g in data.Scenery)
            foreach (var i in g.Instances.Take(10))
                Console.WriteLine($"  inst {g.Infos[i.InfoIndex].Name} at {i.Position}");
    }
}
static class Probe
{
    public static void Groups(string root)
    {
        using var track = new TrackStream(root);
        var byNum = track.Sections.ToDictionary(s => s.Number, s => s.Name);
        var lines = new List<string>();
        foreach (var s in track.Sections)
        {
            var data = track.Load(s, textures: false, solids: false);
            foreach (var g in data.Scenery)
                if (g.SectionNumber != s.Number)
                    lines.Add($"{s.Name}({s.Number}) holds {g.SectionNumber} = {(byNum.TryGetValue(g.SectionNumber, out var n) ? n : "?")} inst {g.Instances.Count}");
        }
        foreach (var l in lines.Take(25)) Console.WriteLine(l);
        Console.WriteLine(string.Join(" ", lines.Select(l => l.Split(' ')[0].Split('(')[0]).GroupBy(x => x[0]).Select(g => g.Key + ":" + g.Count())));
    }
}
static class Probe2
{
    public static void Lod(string root, string sec)
    {
        using var track = new TrackStream(root);
        var data = track.Load(track.Find(sec), textures: false);
        var solids = data.Solids.ToDictionary(s => s.Hash, s => s);
        foreach (var g in data.Scenery)
        {
            Console.WriteLine($"group {g.SectionNumber}: infos {g.Infos.Count} instances {g.Instances.Count}");
            foreach (var i in g.Instances.Take(8))
            {
                var info = g.Infos[i.InfoIndex];
                string lods = string.Join(",", info.SolidHashes.Select(h => h == 0 ? "-" : solids.TryGetValue(h, out var so) ? so.Name : h.ToString("X8")));
                Console.WriteLine($"   {info.Name,-24} at {i.Position} lods {lods} flags {i.Flags:X8}");
            }
        }
    }
}
static class Probe3
{
    public static void Car(string root, string name)
    {
        var car = CarAsset.Load(root, name);
        foreach (var s in car.Solids)
            if (s.Name.EndsWith("_A") && !s.Name.Contains("DAMAGE") && !s.Name.Contains("DECAL"))
                Console.WriteLine($"{s.Name,-48} {s.BoundsMin} .. {s.BoundsMax} T=({s.Matrix[12]:0.###},{s.Matrix[13]:0.###},{s.Matrix[14]:0.###}) parts {s.Parts.Count} effects {string.Join(",", s.Parts.Select(p => p.EffectId).Distinct())}");
    }
}
static class Probe4
{
    public static void Attrib(string root, string car)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        Console.WriteLine($"classes {db.Classes.Count}, collections {db.Collections.Count}, names {db.Names.Count} in {sw.ElapsedMilliseconds} ms");
        var ch = db.Get("chassis", car); var ti = db.Get("tires", car); var en = db.Get("engine", car); var tr = db.Get("transmission", car); var br = db.Get("brakes", car);
        Console.WriteLine($"chassis {ch}: track {ch.Pair("TRACK_WIDTH")} wheelbase {ch.Float("WHEEL_BASE")} front axle {ch.Float("FRONT_AXLE")} ride {ch.Pair("RIDE_HEIGHT")} weight bias {ch.Float("FRONT_WEIGHT_BIAS")} drag {ch.Float("DRAG_COEFFICIENT")}");
        Console.WriteLine($"tires {ti}: width {ti.Pair("SECTION_WIDTH")} aspect {ti.Pair("ASPECT_RATIO")} rim {ti.Pair("RIM_SIZE")} static {ti.Pair("STATIC_GRIP")} dynamic {ti.Pair("DYNAMIC_GRIP")} steering {ti.Float("STEERING")} yaw {string.Join(",", ti.Floats("YAW_CONTROL"))}");
        Console.WriteLine($"engine {en}: torque [{string.Join(", ", en.Floats("TORQUE"))}] max {en.Float("MAX_RPM")} red {en.Float("RED_LINE")} idle {en.Float("IDLE")} limiter [{string.Join(",", en.Floats("SPEED_LIMITER"))}]");
        Console.WriteLine($"transmission {tr}: gears [{string.Join(", ", tr.Floats("GEAR_RATIO"))}] final {tr.Float("FINAL_GEAR")} split {tr.Float("TORQUE_SPLIT")} diff [{string.Join(",", tr.Floats("DIFFERENTIAL"))}] shift {tr.Float("SHIFT_SPEED")}");
        Console.WriteLine($"brakes {br}: brakes {br.Pair("BRAKES")} lock {br.Pair("BRAKE_LOCK")} ebrake {br.Float("EBRAKE")}");
    }
}
static class Probe5
{
    public static void CarMats(string root, string name)
    {
        var car = CarAsset.Load(root, name);
        var tex = new Dictionary<uint, GameTexture>();
        foreach (var t in car.Textures) tex[t.Hash] = t;
        foreach (var f in new[] { "GLOBAL/GLOBALB.lzc", "GLOBAL/InGameB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/InGameA.bun", "CARS/TEXTURES.BIN" })
        {
            var d = GameFiles.ReadBundle(Path.Combine(root, f));
            foreach (var t in TexturePack.ReadAll(d, 0, d.Length)) if (!tex.ContainsKey(t.Hash)) tex[t.Hash] = t;
        }
        foreach (var s in car.Solids)
        {
            if (!s.Name.EndsWith("_A") || s.Name.Contains("DAMAGE") || s.Name.Contains("DECAL")) continue;
            Console.WriteLine(s.Name);
            foreach (var p in s.Parts)
                Console.WriteLine($"    {p.MaterialName,-28} tex {(tex.TryGetValue(p.TextureHash, out var t) ? t.Name + " " + t.Format : p.TextureHash.ToString("X8") + " MISSING")} slots {BitConverter.ToString(p.TextureSlots)} flags {p.Flags:X} verts {p.VertexCount}");
        }
    }
}
static class Probe6
{
    public static void Parts(string root, string sec, string filter)
    {
        using var track = new TrackStream(root);
        var tex = new Dictionary<uint, string>();
        foreach (var s in track.Sections)
        {
            var raw = track.ReadRaw(s);
            foreach (var t in TexturePack.ReadAll(raw, 0, raw.Length, false)) if (t.Name != null) tex[t.Hash] = t.Name;
        }
        var data = track.Load(track.Find(sec));
        int shown = 0;
        foreach (var so in data.Solids)
        {
            bool any = false;
            foreach (var p in so.Parts)
            {
                var names = string.Join(" | ", p.TextureSlots.Select(i => i < so.TextureHashes.Length ? (tex.TryGetValue(so.TextureHashes[i], out var n) ? n : so.TextureHashes[i].ToString("X8")) : "-"));
                if (filter != "*" && !names.Contains(filter)) continue;
                if (!any) { Console.WriteLine(so.Name); any = true; }
                Console.WriteLine($"   eff {p.EffectId} flags {p.Flags:X8} slots {BitConverter.ToString(p.TextureSlots)} {names}");
            }
            if (any && ++shown > 12) break;
        }
    }
}
static class Probe7
{
    public static void Solids(string root, string sec)
    {
        using var track = new TrackStream(root);
        var data = track.Load(track.Find(sec));
        var prefixes = data.Solids.GroupBy(s => s.Name.Split('_')[0] + "_" + (s.Name.Split('_').Length > 1 ? s.Name.Split('_')[1] : "")).Select(g => $"{g.Key}x{g.Count()}");
        Console.WriteLine(string.Join(" ", prefixes));
        foreach (var s in data.Solids.Where(s => s.Name.Contains("ROAD") && !s.Name.StartsWith("SHD")).Take(10))
            Console.WriteLine($"{s.Name} parts {s.Parts.Count} effects {string.Join(",", s.Parts.Select(p => p.EffectId))} tex {string.Join(",", s.TextureHashes.Select(h => h.ToString("X8")))}");
        var inst = data.Scenery.SelectMany(g => g.Instances.Select(i => g.Infos[i.InfoIndex].Name)).GroupBy(n => n.Split('_')[0]).Select(g => $"{g.Key}x{g.Count()}");
        Console.WriteLine("instances: " + string.Join(" ", inst));
    }
}
static class Probe8
{
    public static void Flags(string root)
    {
        using var track = new TrackStream(root);
        var byPrefix = new Dictionary<string, Dictionary<int, int>>();
        var counts = new Dictionary<string, int>();
        var examples = new Dictionary<string, string>();
        foreach (var s in track.Sections)
        {
            var data = track.Load(s, textures: false, solids: false);
            foreach (var g in data.Scenery)
                foreach (var i in g.Instances)
                {
                    var name = g.Infos[i.InfoIndex].Name;
                    var pre = name.Split('_')[0];
                    if (!byPrefix.TryGetValue(pre, out var bits)) byPrefix[pre] = bits = new Dictionary<int, int>();
                    counts[pre] = counts.GetValueOrDefault(pre) + 1;
                    for (int b = 0; b < 32; b++) if ((i.Flags & (1u << b)) != 0) bits[b] = bits.GetValueOrDefault(b) + 1;
                    if (name.Contains("BARRIER") || name.Contains("ARROW")) examples[name] = $"{i.Flags:X8}";
                }
        }
        foreach (var kv in byPrefix.OrderByDescending(k => counts[k.Key]).Take(14))
            Console.WriteLine($"{kv.Key,-6} n={counts[kv.Key],6} " + string.Join(" ", kv.Value.OrderBy(b => b.Key).Select(b => $"b{b.Key}:{b.Value * 100 / counts[kv.Key]}%")));
        foreach (var e in examples.Take(25)) Console.WriteLine($"   {e.Key} {e.Value}");
    }
}
static class Probe9
{
    public static void Bit(string root, int bit)
    {
        using var track = new TrackStream(root);
        var names = new Dictionary<string, int>();
        foreach (var s in track.Sections)
        {
            var data = track.Load(s, textures: false, solids: false);
            foreach (var g in data.Scenery)
                foreach (var i in g.Instances)
                    if ((i.Flags & (1u << bit)) != 0)
                    {
                        var n = g.Infos[i.InfoIndex].Name;
                        names[n] = names.GetValueOrDefault(n) + 1;
                    }
        }
        foreach (var kv in names.OrderByDescending(k => k.Value).Take(40)) Console.WriteLine($"{kv.Value,5} {kv.Key}");
    }
}
static class Probe10
{
    public static void Near(string root, float x, float y, float r)
    {
        using var track = new TrackStream(root);
        foreach (var s in track.Sections)
        {
            var data = track.Load(s, textures: false, solids: false);
            foreach (var g in data.Scenery)
                foreach (var i in g.Instances)
                {
                    float cx = (i.Min.X + i.Max.X) / 2, cy = (i.Min.Y + i.Max.Y) / 2;
                    if (Math.Abs(cx - x) < r && Math.Abs(cy - y) < r && (i.Max.X - i.Min.X) < 200)
                        Console.WriteLine($"{s.Name,-5} g{g.SectionNumber,-5} {g.Infos[i.InfoIndex].Name,-26} flags {i.Flags:X8} center ({cx:0},{cy:0}) size ({i.Max.X - i.Min.X:0.0},{i.Max.Y - i.Min.Y:0.0},{i.Max.Z - i.Min.Z:0.0})");
                }
        }
    }
}
static class Probe11
{
    public static void TexStats(string root, string sec, string prefix)
    {
        using var track = new TrackStream(root);
        foreach (var s in track.Sections)
        {
            if (sec != "*" && s.Name != sec) continue;
            var raw = track.ReadRaw(s);
            foreach (var t in TexturePack.ReadAll(raw, 0, raw.Length))
            {
                if (!t.Name.StartsWith(prefix) || (t.Format != TexFormat.Dxt1 && t.Format != TexFormat.Dxt3 && t.Format != TexFormat.Dxt5)) continue;
                int bs = t.Format == TexFormat.Dxt1 ? 8 : 16, co = bs - 8;
                int top = TexturePack.LevelSize(t.Format, t.Width, t.Height);
                double r = 0, g = 0, b = 0; int n = 0;
                for (int o = 0; o + bs <= top; o += bs)
                    for (int k = 0; k < 2; k++)
                    {
                        int c = t.Data[o + co + k * 2] | t.Data[o + co + k * 2 + 1] << 8;
                        r += (c >> 11) / 31.0; g += ((c >> 5) & 63) / 63.0; b += (c & 31) / 31.0; n++;
                    }
                Console.WriteLine($"{t.Name,-26} {t.Format} avg ({r / n:0.00},{g / n:0.00},{b / n:0.00}) alpha {t.AlphaUsage}");
            }
            if (sec != "*") break;
        }
    }
}
static class ProbeFe
{
    public static void Fe(string root)
    {
        var d = GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pk = FEPackage.ReadBundle(d);
        Console.WriteLine($"{pk.Count} packages in {sw.ElapsedMilliseconds} ms; objects {pk.Values.Sum(p => p.Objects.Count)}");
        var ids = new Dictionary<uint, int>();
        foreach (var p in pk.Values) foreach (var o in p.Objects) foreach (var s in o.Scripts) ids[s.Id] = ids.GetValueOrDefault(s.Id) + 1;
        var cands = new[] { "INIT", "HIGHLIGHT", "UNHIGHLIGHT", "FADE_IN", "FADE_OUT", "FADEIN", "FADEOUT", "SHOW", "HIDE", "ON", "OFF", "ANIM_IN", "ANIM_OUT", "DEFAULT", "STATIC", "ACTIVE", "INACTIVE", "SELECTED", "UNSELECTED", "GREY", "GRAY", "DISABLED", "ENABLED", "IN", "OUT", "OPEN", "CLOSE", "BLINK", "PULSE", "LEFT", "RIGHT", "UP", "DOWN", "ON_SCREEN", "OFF_SCREEN", "TRANSITION_IN", "TRANSITION_OUT", "ZOOM", "FLASH", "LOCKED", "UNLOCKED", "CHECKED", "UNCHECKED", "NORMAL", "PRESSED", "Init", "Highlight", "UnHighlight", "Fade_In", "Fade_Out" };
        var names = cands.ToDictionary(c => EaHash.Hash(c), c => c);
        foreach (var kv in ids.OrderByDescending(k => k.Value).Take(25))
            Console.WriteLine($"  script {kv.Key:X8} x{kv.Value} {(names.TryGetValue(kv.Key, out var n) ? n : "")}");
        var lang = StringTable.Read(File.ReadAllBytes(Path.Combine(root, "LANGUAGES", "English.bin")));
        Console.WriteLine($"strings {lang.Count}");
        int labelled = 0, resolved = 0;
        foreach (var p in pk.Values) foreach (var o in p.Objects) if (o.Type == FEObjectType.String && o.Label != 0) { labelled++; if (lang.Get(o.Label) != null) resolved++; }
        Console.WriteLine($"string objects with label {labelled}, resolved {resolved}");
        foreach (var f in FEFont.ReadAll(File.ReadAllBytes(Path.Combine(root, "LANGUAGES", "English.bin")), 0, 400000)) Console.WriteLine($"font {f.Name} tex {f.Texture} glyphs {f.Glyphs.Count} line {f.LineHeight}");
        var mm = pk["MainMenu"];
        Console.WriteLine($"MainMenu: {mm.Objects.Count} objects, resources {string.Join(",", mm.Resources)}");
    }
}
static class ProbeTexList
{
    public static void List(string path)
    {
        var d = GameFiles.ReadBundle(path);
        foreach (var t in TexturePack.ReadAll(d, 0, d.Length))
            Console.WriteLine($"{t.Hash:X8} {t.Name} {t.Width}x{t.Height} {t.Format} data {(t.Data == null ? "-" : Convert.ToHexString(System.Security.Cryptography.MD5.HashData(t.Data))[..12])}");
    }
    public static void MiniMap(string path)
    {
        var d = File.ReadAllBytes(path);
        int n = 0;
        foreach (var c in Chunks.Read(d, 0, d.Length))
        {
            var b = Compression.Decompress(d, c.DataStart);
            if (Environment.GetEnvironmentVariable("DUMP") is string dir) File.WriteAllBytes(Path.Combine(dir, $"tile{n}.bin"), b);
            foreach (var t in TexturePack.ReadAll(b, 0, b.Length))
                if (n++ < 2) Console.WriteLine($"{c.Id:X} {t.Hash:X8} {t.Name} {t.Width}x{t.Height} {t.Format}");
        }
    }
}
static class ProbeScripts
{
    public static void Dump(string root, string pkgName)
    {
        var d = GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc"));
        var p = FEPackage.ReadBundle(d)[pkgName];
        int n = 0;
        foreach (var o in p.Objects)
            foreach (var s in o.Scripts)
                foreach (var t in s.Tracks)
                    if (t.Keys.Count > 2 && n++ < 12)
                        Console.WriteLine($"obj {o.Hash:X8} t{(int)o.Type} script {s.Id:X8} len {s.Length} chain {s.Chain:X8} track type {t.ValueType} size {t.ValueSize} interp {t.Interp} off 0x{t.Offset:X}: " +
                            string.Join(" | ", t.Keys.Select(k => k.time + ":" + string.Join(",", k.value.Select(v => v.ToString("0.##"))))));
        var init = p.Objects[5].Scripts[0];
        Console.WriteLine($"obj5 INIT len {init.Length}: " + string.Join("; ", init.Tracks.Select(t => $"off {t.Offset:X} type {t.ValueType} keys " + string.Join(" ", t.Keys.Select(k => k.time + ":" + string.Join(",", k.value))))));
    }
}
static class ProbeJust
{
    public static void Run(string root)
    {
        var p = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")));
        var c = new Dictionary<int, int>(); var ex = new Dictionary<int, string>();
        foreach (var pk in p.Values) foreach (var o in pk.Objects) if (o.Type == FEObjectType.String) { c[o.Justify] = c.GetValueOrDefault(o.Justify) + 1; ex[o.Justify] = pk.Name + ":" + o.Text; }
        foreach (var kv in c) Console.WriteLine($"Sj {kv.Key:X} x{kv.Value} e.g. {ex[kv.Key]}");
        var fl = new Dictionary<uint, int>();
        foreach (var pk in p.Values) foreach (var o in pk.Objects) fl[o.Flags] = fl.GetValueOrDefault(o.Flags) + 1;
        foreach (var kv in fl.OrderByDescending(k => k.Value).Take(14)) Console.WriteLine($"flags {kv.Key:X8} x{kv.Value}");
    }
}
static class ProbeGameplay
{
    public static void Run(string root, string file)
    {
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", file));
        Console.WriteLine($"{file}: classes {db.Classes.Count}, collections {db.Collections.Count}, names {db.Names.Count}");
        foreach (var g in db.Collections.Values.GroupBy(c => c.ClassKey).OrderByDescending(g => g.Count()))
        {
            if (!db.Classes.TryGetValue(g.Key, out var cls)) { Console.WriteLine($"class {g.Key:X8} undefined: {g.Count()}"); continue; }
            string cname = db.Names.TryGetValue(g.Key, out var n) ? n : g.Key.ToString("X8");
            Console.WriteLine($"class {cname}: {g.Count()} collections, {cls.Fields.Count} fields, layout {cls.LayoutSize}; e.g. {string.Join(", ", g.Take(8).Select(c => c.Name ?? c.Key.ToString("X8")))}");
        }
    }
}
static class ProbeCareer
{
    public static void Run(string root)
    {
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"));
        var gp = db.OfClass("gameplay").ToList();
        var byKey = gp.ToDictionary(c => c.Key, c => c);
        string N(uint k) => byKey.TryGetValue(k, out var c) ? (c.Name ?? k.ToString("X8")) : k.ToString("X8");
        var roots = gp.Where(c => c.ParentKey == 0).Select(c => c.Name).ToList();
        Console.WriteLine($"gameplay collections {gp.Count}, roots: {string.Join(", ", roots.Take(30))}");
        // top-level templates: the direct children of the root
        var tops = gp.GroupBy(c => c.Parent?.Name ?? "-").OrderByDescending(g => g.Count()).Take(25);
        foreach (var g in tops) Console.WriteLine($"  parent {g.Key}: {g.Count()} e.g. {string.Join(", ", g.Take(5).Select(c => c.Name))}");
        foreach (var name in new[] { "race_bin_15", "race_bin_15/15_1_1_circuit", "race_bin_15/tony_ho", "race_bin_01/razor_callahan", "race_bin_15/opponent1", "race_bin_15/15_1_1_circuit/startgrid", "milestones/bin_15" })
        {
            var c = gp.FirstOrDefault(x => x.Name == name);
            if (c == null) continue;
            Console.WriteLine($"== {name} (parent {c.Parent?.Name})");
            foreach (var f in new[] { "EventID", "Name", "BinIndex", "Boss", "BossRace", "IsBoss", "Bounty", "CashValue", "NumLaps", "TimeLimit", "RequiredBounty", "RequiredRacesWon", "RequiredChallenges", "Region", "PlayerCarType", "IntroNIS", "OutroNIS", "IntroMovie", "OutroMovie", "EventIconType", "TrafficLevel", "CopsInRace", "RaceLength", "ReputationRequired", "Reputation", "DifficultyLevel", "PursuitRace", "BossReputation", "RacerName", "CarType", "PresetRide" })
            {
                if (!c.Has(f)) continue;
                var cls = db.Classes[c.ClassKey].Fields[AttribDatabase.Hash(f)];
                string v = cls.Type == AttribDatabase.Hash("EA::Reflection::Text") ? c.Text(f) :
                    cls.Type == AttribDatabase.Hash("EA::Reflection::Bool") ? c.Bool(f).ToString() :
                    cls.Type == AttribDatabase.Hash("EA::Reflection::Float") ? c.Float(f).ToString() :
                    cls.Type == AttribDatabase.Hash("EA::Reflection::Int32") ? c.Int(f).ToString() :
                    string.Join(",", c.Keys(f).Select(N));
                Console.WriteLine($"   {f} = {v}");
            }
            foreach (var f in new[] { "Opponents", "Children", "RaceList", "BossRaces", "WorldRaces", "UnlockRaces", "Checkpoint", "Shortcuts", "SpeedTrapList", "Barriers" })
                if (c.Has(f)) Console.WriteLine($"   {f} = {(f == "Barriers" ? string.Join(",", c.Texts(f)) : string.Join(", ", c.Keys(f).Take(12).Select(N)))}");
        }
    }
}
static class ProbeFrontend
{
    public static void Run(string root, string car)
    {
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "FE_ATTRIB.bin"));
        var fe = db.OfClass("frontend").ToList();
        Console.WriteLine($"frontend collections {fe.Count}: " + string.Join(", ", fe.Where(c => c.Parent?.Name == "vehicles" || c.Name == "vehicles").Take(12).Select(c => c.Name)));
        foreach (var c in fe.Where(c => c.Name == car || c.Name == "vehicles" || c.Name == "default"))
        {
            Console.WriteLine($"== {c.Name} parent {c.Parent?.Name}");
            foreach (var f in c.Class.Fields.Values)
            {
                string name = db.Names.TryGetValue(f.Key, out var n) ? n : null;
                if (!c.Entries.ContainsKey(f.Key) && !(f.InLayout && c.Layout >= 0)) continue;
                c.TryGet(name ?? "", out _, out _, out _);
                Console.WriteLine($"   {f.Key:X8} {name} size {f.Size} count {f.Count} flags {f.Flags:X}");
            }
        }
    }
}
static class ProbeCarLot
{
    public static void Run(string root)
    {
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "FE_ATTRIB.bin"));
        var cars = db.OfClass("pvehicle").Select(c => c.Name).Where(n => n != null).ToList();
        foreach (var car in cars.OrderBy(n => n))
        {
            var fe = db.Get("frontend", car);
            if (fe == null) continue;
            int unlock = fe.TryGet("UnlockedAt", out int at, out _, out _) ? fe.Source.Data[at] : -1;
            Console.WriteLine($"{car,-22} cost {fe.Int("Cost"),7} unlockedAt {unlock,3} customizable {fe.Bool("IsCustomizable")} parent {fe.Parent?.Name}");
        }
    }
}
static class ProbeCareer2
{
    public static void Run(string root)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var c = CareerData.Load(root);
        Console.WriteLine($"career loaded in {sw.ElapsedMilliseconds} ms: bins {c.Bins.Count}, events {c.Events.Count}, racers {c.Racers.Count}, car lot {c.CarLot.Count}");
        foreach (var b in c.Bins.Values)
        {
            Console.WriteLine($"#{b.Index,2} boss {b.Boss?.Name} ({b.Boss?.PresetRide} = {c.CarFor(b.Boss)}) bounty {b.RequiredBounty} races {b.RequiredRacesWon} challenges {b.RequiredChallenges} world {b.WorldRaces.Count} boss races {b.BossRaces.Count} barriers {b.Barriers.Count}");
            foreach (var e in b.WorldRaces.Concat(b.BossRaces).Take(2))
                Console.WriteLine($"      {e.DisplayName,-22} {e.Region,-10} ${e.Cash} laps {e.Laps} cp {e.Checkpoints.Count} start {e.Start?.Position} rot {e.Start?.Rotation} finish {e.Finish?.Position} opp {e.Opponents.Count}");
        }
    }
}
static class ProbeFeNames
{
    public static void Tree(string root, string pkgName)
    {
        var exe = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(root, "speed.exe")));
        var names = new Dictionary<uint, string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(exe, @"[A-Za-z0-9_ .%\-]{2,64}"))
        {
            var s = m.Value;
            if (s.Contains("%d")) for (int i = 0; i <= 40; i++) names.TryAdd(EaHash.HashUpper(s.Replace("%d", i.ToString())), s.Replace("%d", i.ToString()));
            else names.TryAdd(EaHash.HashUpper(s), s);
        }
        var all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameSplitScreen.bun")));
        foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameB.lzc")))) all.TryAdd(kv.Key, kv.Value);
        foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")))) all.TryAdd(kv.Key, kv.Value);
        var p = all[pkgName];
        void Walk(uint parent, int depth)
        {
            foreach (var o in p.Objects)
            {
                if (o.Parent != parent) continue;
                string n = names.TryGetValue(o.Hash, out var nn) ? nn : o.Hash.ToString("X8");
                bool hidden = (o.Flags & 0x10000000) != 0;
                Console.WriteLine($"{new string(' ', depth * 2)}{n} g{o.Guid:X} t{(int)o.Type}{(hidden ? " HIDDEN" : "")} pos({o.Data.X:0},{o.Data.Y:0},{o.Data.Z:0}) size({o.Data.SizeX:0},{o.Data.SizeY:0}) '{o.Text}' {(o.Resource >= 0 && o.Resource < p.Resources.Count ? p.Resources[o.Resource] : "")} scripts {string.Join(",", o.Scripts.Select(sc => names.TryGetValue(sc.Id, out var sn) ? sn : sc.Id.ToString("X8")))}");
                Walk(o.Guid, depth + 1);
            }
        }
        Walk(0, 0);
    }

    public static void Run(string root, string pkgs)
    {
        var exe = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(root, "speed.exe")));
        var names = new Dictionary<uint, string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(exe, @"[A-Za-z0-9_ .%\-]{2,64}"))
        {
            var s = m.Value;
            if (s.Contains("%d")) for (int i = 0; i <= 40; i++) names.TryAdd(EaHash.HashUpper(s.Replace("%d", i.ToString())), s.Replace("%d", i.ToString()));
            else names.TryAdd(EaHash.HashUpper(s), s);
        }
        var all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")));
        foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameSplitScreen.bun")))) all[kv.Key] = kv.Value;
        foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameB.lzc")))) all[kv.Key] = kv.Value;
        foreach (var pn in pkgs.Split(','))
        {
            var p = all[pn];
            Console.WriteLine($"== {pn}");
            foreach (var o in p.Objects)
                if (names.TryGetValue(o.Hash, out var n))
                    Console.WriteLine($"   {n,-28} t{(int)o.Type} pos({o.Data.X:0},{o.Data.Y:0}) size({o.Data.SizeX:0},{o.Data.SizeY:0}) parent {(o.Parent != 0 && p.ByGuid.TryGetValue(o.Parent, out var pp) && names.TryGetValue(pp.Hash, out var pn2) ? pn2 : o.Parent.ToString("X"))} '{o.Text}' res {(o.Resource < p.Resources.Count && o.Resource >= 0 ? p.Resources[o.Resource] : "")}");
        }
    }
}
static class ProbeHeading
{
    public static void Run(string root)
    {
        var c = CareerData.Load(root);
        foreach (var e in c.Events.Values.Where(e => e.Start != null && e.Checkpoints.Count > 0).Take(14))
        {
            var s = e.Start.Position; var cp = e.Checkpoints[0].Position;
            double bearing = Math.Atan2(cp.Y - s.Y, cp.X - s.X) * 180 / Math.PI;
            double rot = ((e.Start.Rotation % 360) + 360) % 360;
            double d = Math.Sqrt((cp.X - s.X) * (cp.X - s.X) + (cp.Y - s.Y) * (cp.Y - s.Y));
            Console.WriteLine($"{e.EventId,-10} rot {rot,7:0.0}  bearing-to-cp1 {((bearing + 360) % 360),7:0.0}  diff(rot-bearing) {((rot - bearing + 540) % 360 - 180),7:0.0}  dist {d:0}");
        }
    }
}
static class ProbeRoads
{
    public static void Run(string root)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var net = RoadNetwork.Read(File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN")));
        Console.WriteLine($"roads: {net.Nodes.Length} nodes, {net.Segments.Length} segments in {sw.ElapsedMilliseconds} ms");
        // connectivity: size of the largest component
        var seen = new int[net.Nodes.Length]; int comps = 0, largest = 0;
        for (int i = 0; i < seen.Length; i++)
        {
            if (seen[i] != 0) continue; comps++; int size = 0; var q = new Queue<int>(); q.Enqueue(i); seen[i] = comps;
            while (q.Count > 0) { int n = q.Dequeue(); size++; foreach (var m in net.Links[n]) if (seen[m] == 0) { seen[m] = comps; q.Enqueue(m); } }
            largest = Math.Max(largest, size);
        }
        Console.WriteLine($"components {comps}, largest {largest}");
        var career = CareerData.Load(root);
        foreach (var e in career.Events.Values.Where(e => e.Start != null && e.Checkpoints.Count > 2).Take(8))
        {
            var wps = new List<Vec3> { e.Start.Position };
            for (int lap = 0; lap < e.Laps; lap++) wps.AddRange(e.Checkpoints.Select(c => c.Position));
            if (e.Finish != null) wps.Add(e.Finish.Position);
            var path = net.RouteThrough(wps);
            double len = 0; for (int i = 1; i < path.Count; i++) len += Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
            Console.WriteLine($"{e.DisplayName,-20} laps {e.Laps} route points {path.Count} length {len / 1000:0.00} km (game RaceLength {e.Length / 1000:0.00} km)");
        }
    }
}
static class ProbePreset
{
    public static void Run(string root)
    {
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"));
        foreach (var c in db.OfClass("presetride").Where(c => c.Name != null && (c.Name.StartsWith("BL") || c.Name.StartsWith("E3"))).OrderBy(c => c.Name))
        {
            var fields = c.Class.Fields.Values.Where(f => c.Entries.ContainsKey(f.Key) || f.InLayout).Take(6).Select(f => f.Key.ToString("X8"));
            Console.WriteLine($"{c.Name,-14} parent {c.Parent?.Name} {c.Text("CarType")} {c.Text("ModelName")} {c.Text("PresetCarName")} fields {string.Join(",", fields)}");
        }
    }
}
static class ProbeEngine
{
    public static void Run(string root, string car)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        var pv = db.Get("pvehicle", car);
        var refs = pv.Refs("engineaudio");
        Console.WriteLine($"{car}: engineaudio refs {string.Join(",", refs.Select(r => (db.Names.TryGetValue(r.key, out var n) ? n : r.key.ToString("X8"))))}");
        if (refs.Length == 0) return;
        var ea = db.Collections[(refs[0].cls, refs[0].key)];
        string acc = ea.StringKey("Filename_GinsuAccel"), dec = ea.StringKey("Filename_GinsuDecel");
        Console.WriteLine($"  accel {acc} decel {dec} bank {ea.StringKey("BankName_mainRAM")} rpm {ea.Float("MinRPM")}..{ea.Float("MaxRPM")}");
        var g = GinSound.Load(Path.Combine(root, "SOUND", "ENGINE", acc));
        float peak = g.Samples.Max(Math.Abs), rms = (float)Math.Sqrt(g.Samples.Average(x => x * x));
        Console.WriteLine($"  {acc}: {g.Samples.Length} samples @ {g.SampleRate} Hz, rpm {g.MinRpm:0}..{g.MaxRpm:0}, grains {g.Grains.Length}, peak {peak:0.00} rms {rms:0.000}");
        // write a wav to listen to / verify
        var wav = Path.Combine(Path.GetTempPath(), car + "_accel.wav");
        using (var w = new BinaryWriter(File.Create(wav)))
        {
            int n = g.Samples.Length;
            w.Write("RIFF".ToCharArray()); w.Write(36 + n * 2); w.Write("WAVEfmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(g.SampleRate); w.Write(g.SampleRate * 2); w.Write((short)2); w.Write((short)16); w.Write("data".ToCharArray()); w.Write(n * 2);
            foreach (var s in g.Samples) w.Write((short)(s * 32767));
        }
        Console.WriteLine("  wrote " + wav);
    }
}
static class ProbeBank
{
    public static void Run(string root, string rel)
    {
        var bank = SoundBank.Load(Path.Combine(root, "SOUND", rel));
        foreach (var s in bank.Sounds)
        {
            float peak = s.Data.Max(Math.Abs), mag = s.Data.Average(Math.Abs);
            float dx = 0; for (int i = 1; i < s.Data.Length; i++) dx += Math.Abs(s.Data[i] - s.Data[i - 1]); dx /= s.Data.Length;
            Console.WriteLine($"  #{s.Index} {s.Samples} @ {s.SampleRate} ch {s.Channels} loop {s.LoopStart}-{s.LoopEnd} peak {peak:0.00} mean|x| {mag:0.000} mean|dx| {dx:0.000}");
        }
    }
}
static class ProbeHudPk
{
    public static void Run(string root)
    {
        foreach (var f in new[] { "GLOBAL/InGameB.lzc", "GLOBAL/InGameA.bun", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameSplitScreen.bun", "GLOBAL/WIDESCREEN_GLOBAL.BUN", "GLOBAL/THINSCREEN_GLOBAL.BUN" })
        {
            var d = GameFiles.ReadBundle(Path.Combine(root, f));
            var p = FEPackage.ReadBundle(d);
            Console.WriteLine($"{f}: {string.Join(" ", p.Keys)}");
        }
    }
}

static class ProbeGroups
{
    static uint H(string s) { uint x = 0xFFFFFFFF; foreach (var c in s.ToUpperInvariant()) x = x * 33 + c; return x; }
    public static void Run(string root)
    {
        using var track = new TrackStream(root);
        var d = track.Bundle;
        int ovAt = 0, ovEnd = 0, grAt = 0, grEnd = 0;
        foreach (var c in Chunks.Read(d, 0, d.Length))
        {
            if (c.Id == 0x34108) { ovAt = c.DataStart; ovEnd = c.End; }
            if (c.Id == 0x34109) { grAt = c.DataStart; grEnd = c.End; }
        }
        var groups = new Dictionary<uint, (int num, int flags, List<int> ov)>();
        int o = grAt;
        while (o + 20 <= grEnd)
        {
            uint hash = Chunks.U32(d, o + 8);
            int num = Chunks.U16(d, o + 12), n = Chunks.U16(d, o + 14), fl = Chunks.I32(d, o + 16);
            var ov = new List<int>();
            for (int i = 0; i < n; i++) ov.Add(Chunks.U16(d, o + 20 + i * 2));
            groups[hash] = (num, fl, ov);
            o += 20 + ((n * 2 + 3) & ~3);
        }
        Console.WriteLine($"{groups.Count} groups, overrides {(ovEnd - ovAt) / 6}, parsed to {o - grAt}/{grEnd - grAt}");
        var cache = new Dictionary<int, List<SceneryGroup>>();
        foreach (var ss in track.Sections)
            foreach (var sg in track.Load(ss, false, false, true).Scenery)
            {
                if (!cache.TryGetValue(sg.SectionNumber, out var l)) cache[sg.SectionNumber] = l = new List<SceneryGroup>();
                l.Add(sg);
            }
        Console.WriteLine($"scenery section numbers {cache.Count}, multi-chunk {cache.Count(kv => kv.Value.Count > 1)}");
        var names8000 = new Dictionary<string, int>();
        foreach (var l in cache.Values) foreach (var sg in l) foreach (var ins in sg.Instances)
            if ((ins.Flags & 0x8000) != 0 && ins.InfoIndex < sg.Infos.Count)
            {
                var nm = System.Text.RegularExpressions.Regex.Replace(sg.Infos[ins.InfoIndex].Name, @"_?\d+$", "");
                names8000[nm] = names8000.GetValueOrDefault(nm) + 1;
            }
        Console.WriteLine("0x8000 instances: " + string.Join(", ", names8000.OrderByDescending(kv => kv.Value).Take(25).Select(kv => $"{kv.Key} {kv.Value}")));
        var ovSecs = new HashSet<int>();
        for (int i = ovAt; i + 6 <= ovEnd; i += 6) ovSecs.Add(Chunks.U16(d, i));
        Console.WriteLine($"override sections {ovSecs.Count}, with scenery {ovSecs.Count(cache.ContainsKey)}");
        foreach (var name in new[] { "BARRIER_LOCKED_12", "BARRIER_SPLINE_01", "BARRIER_SPLINE_44", "BARRIER_LOCKED_15" })
        {
            if (!groups.TryGetValue(H(name), out var g)) { Console.WriteLine($"{name}: missing"); continue; }
            int flagged = 0, total = 0;
            var secs = new HashSet<int>();
            foreach (int i in g.ov)
            {
                int sec = Chunks.U16(d, ovAt + i * 6), inst = Chunks.U16(d, ovAt + i * 6 + 2), ef = Chunks.U16(d, ovAt + i * 6 + 4);
                secs.Add(sec);
                if (!cache.TryGetValue(sec, out var sc)) { Console.WriteLine($"   section {sec} has no scenery"); continue; }
                var all = sc.SelectMany(x => x.Instances).ToList();
                if (inst >= all.Count) { Console.WriteLine($"   section {sec} inst {inst} >= {all.Count} (groups {sc.Count})"); continue; }
                total++;
                if ((all[inst].Flags & (1u << 23)) != 0) flagged++;
                if (total <= 3) Console.WriteLine($"   {name} ov {i}: section {sec} inst {inst} flags {ef:X4} -> instance flags {all[inst].Flags:X8} info {sc.SelectMany(x => x.Instances.Select(_ => x)).ElementAt(inst).Infos[all[inst].InfoIndex].Name}");
            }
            Console.WriteLine($"{name}: group {g.num} flags {g.flags} {g.ov.Count} overrides, sections {string.Join(",", secs.Take(8))}, resolved {total}, race-barrier flagged {flagged}");
        }
    }
}

static class ProbeGpDump
{
    public static void Run(string root, string match, int max)
    {
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"));
        var gp = db.OfClass("gameplay").ToList();
        var byKey = gp.ToDictionary(c => c.Key, c => c);
        string N(uint k) => byKey.TryGetValue(k, out var c) ? (c.Name ?? k.ToString("X8")) : (db.Names.TryGetValue(k, out var n) ? n : k.ToString("X8"));
        var cls = db.Classes[AttribDatabase.Hash("gameplay")];
        var fieldNames = new Dictionary<uint, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(root, "..", "NFSMW_RE", "vlt_classes.txt")))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2 && parts[0].StartsWith("+0x")) fieldNames.TryAdd(AttribDatabase.Hash(parts[1]), parts[1]);
        }
        int shown = 0;
        foreach (var c in gp.Where(x => x.Name != null && (match.StartsWith("=") ? x.Name == match.Substring(1) : match.StartsWith("@") ? x.Has(match.Substring(1)) : x.Name.Contains(match))))
        {
            if (shown++ >= max) break;
            Console.WriteLine($"== {c.Name} (parent {c.Parent?.Name})");
            foreach (var kv in cls.Fields)
            {
                string f = fieldNames.TryGetValue(kv.Key, out var fn) ? fn : null;
                if (f == null || !c.Has(f)) continue;
                var t = kv.Value.Type;
                string v;
                try
                {
                    v = t == AttribDatabase.Hash("EA::Reflection::Text") ? string.Join("|", c.Texts(f)) :
                        t == AttribDatabase.Hash("EA::Reflection::Bool") ? c.Bool(f).ToString() :
                        t == AttribDatabase.Hash("EA::Reflection::Float") ? c.Float(f).ToString() :
                        t == AttribDatabase.Hash("EA::Reflection::Int32") || t == AttribDatabase.Hash("EA::Reflection::UInt32") ? c.Int(f).ToString() :
                        t == AttribDatabase.Hash("Attrib::StringKey") ? c.StringKey(f) :
                        string.Join(",", c.Keys(f).Take(12).Select(N));
                }
                catch (Exception e) { v = "?" + e.GetType().Name; }
                Console.WriteLine($"   {f} [{(db.Names.TryGetValue(t, out var tn) ? tn : t.ToString("X8"))}] = {v}");
            }
        }
    }
}
