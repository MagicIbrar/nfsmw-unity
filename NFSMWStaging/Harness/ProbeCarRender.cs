using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

/// <summary>Car render probes (car_render.md): solids by slot, light/damage/decal textures, part attributes.</summary>
static class ProbeCarRender
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "solids";
        switch (sub)
        {
            case "solids": Solids(root, args[3]); break;
            case "tex": Tex(root, args[3], args.Length > 4 ? args[4] : null); break;
            case "parts": Parts(root, args[3], args.Length > 4 ? int.Parse(args[4]) : -1); break;
            case "global": Global(root, args.Length > 3 ? args[3] : null); break;
            case "lightmat": LightMats(root, args.Length > 3 ? args[3] : null); break;
            case "allcars": AllCars(root); break;
            case "markers": Markers(root, args[3]); break;
            case "refs": ProbeCarRenderAttr.Refs(root, args[3]); break;
            case "names": ProbeCarRenderEvt.Names(root, args[3]); break;
            case "partlm": ProbeCarRenderComposite.PartsLm(root, args[3]); break;
            case "types": ProbeCarRenderComposite.Types(root); break;
            case "skin": ProbeCarRenderComposite.Skin(root, args[3], args[4], args[5]); break;
            case "folder": ProbeCarRenderShared.Folder(root, args[3], args[4]); break;
            case "math": ProbeCarRenderMath.Run(root); break;
            case "texdump": ProbeCarRenderTex.Dump(root, args[3], args[4]); break;
            case "alpha": ProbeCarRenderAlpha.Run(root, args.Skip(3).ToArray()); break;
            case "lmidx": ProbeCarRenderRaw.LightMatIdx(root, args[3], args[4]); break;
        }
    }

    static void Solids(string root, string car)
    {
        var a = CarAsset.Load(root, car.ToUpperInvariant());
        foreach (var s in a.Solids)
            Console.WriteLine($"{s.Name,-48} parts {s.Parts.Count,2} mats {string.Join(",", s.Parts.Select(p => p.MaterialName + ":" + p.TextureHash.ToString("X8")).Distinct().Take(12))}");
    }

    static void Markers(string root, string car)
    {
        var a = CarAsset.Load(root, car.ToUpperInvariant());
        var names = HashNames();
        foreach (var s in a.Solids.Where(s => s.Name.EndsWith("_A")))
            foreach (var (h, m) in s.Markers)
                Console.WriteLine($"{s.Name,-44} {(names.TryGetValue(h, out var n) ? n : h.ToString("X8")),-28} pos {m[12]:F2} {m[13]:F2} {m[14]:F2}");
    }

    static Dictionary<uint, string> HashNames()
    {
        var d = new Dictionary<uint, string>();
        foreach (var n in new[] { "HOOD", "TRUNK", "LEFT_DOOR", "RIGHT_DOOR", "LEFT_REAR_DOOR", "RIGHT_REAR_DOOR", "REAR_BUMPER", "SPOILER", "SPOILER2",
                     "ROOF_SCOOP", "FRONT_BRAKE", "REAR_BRAKE", "LEFT_HEADLIGHT", "RIGHT_HEADLIGHT", "LEFT_BRAKELIGHT", "RIGHT_BRAKELIGHT", "CENTRE_BRAKELIGHT",
                     "LEFT_REVERSE", "RIGHT_REVERSE", "EXHAUST_FX", "ENGINE_FX", "FRONT_LEFT_DOOR", "FRONT_RIGHT_DOOR", "REAR_LEFT_DOOR", "REAR_RIGHT_DOOR" })
            d[EaHash.Hash(n)] = n;
        return d;
    }

    static void Tex(string root, string car, string filter)
    {
        car = car.ToUpperInvariant();
        var dir = Path.Combine(root, "CARS", car);
        foreach (var f in new[] { "TEXTURES.BIN", "VINYLS.BIN", "PREVINYL.BIN" })
        {
            var p = Path.Combine(dir, f);
            if (!File.Exists(p)) continue;
            var d = File.ReadAllBytes(p);
            var list = TexturePack.ReadAll(d, 0, d.Length);
            Console.WriteLine($"== {f}: {list.Count}");
            foreach (var t in list)
                if (filter == null || t.Name.Contains(filter))
                    Console.WriteLine($"  {t.Name,-40} {t.Hash:X8} {t.Width}x{t.Height} fmt {t.Format} au {t.AlphaUsage} ab {t.AlphaBlend}");
        }
    }

    static void Global(string root, string filter)
    {
        foreach (var f in new[] { "GLOBAL/GLOBALB.LZC", "GLOBAL/GlobalB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalA.bun", "CARS/TEXTURES.BIN", "CARS/WHEELS/TEXTURES.BIN" })
        {
            var p = Path.Combine(root, f);
            if (!File.Exists(p)) continue;
            var d = GameFiles.ReadBundle(p);
            var list = TexturePack.ReadAll(d, 0, d.Length);
            Console.WriteLine($"== {f}: {list.Count}");
            foreach (var t in list)
                if (filter == null || t.Name.Contains(filter))
                    Console.WriteLine($"  {t.Name,-40} {t.Hash:X8} {t.Width}x{t.Height} fmt {t.Format} au {t.AlphaUsage} ab {t.AlphaBlend}");
        }
    }

    static void LightMats(string root, string filter)
    {
        // BCHUNK_LIGHT_MATERIALS: eLightMaterial {bTNode 8, ? 4, NameHash @0xC, Version @0x10, Name[28] @0x14, data 0x78 @0x30}
        foreach (var f in Directory.GetFiles(Path.Combine(root, "GLOBAL")).Concat(new[] { Path.Combine(root, "CARS", "TEXTURES.BIN") }))
        {
            if (!File.Exists(f)) continue;
            byte[] d;
            try { d = GameFiles.ReadBundle(f); } catch { continue; }
            var ids = new Dictionary<uint, int>();
            Walk(d, 0, d.Length, 0, (id, o, size, depth) =>
            {
                if (id == 0x00135200)
                {
                    uint ver = Chunks.U32(d, o + 0x10);
                    string name = Chunks.CString(d, o + 0x14, 28);
                    if (filter != null && !name.Contains(filter)) return;
                    var fl = new float[30];
                    for (int i = 0; i < 30; i++) fl[i] = Chunks.F32(d, o + 0x30 + i * 4);
                    Console.WriteLine($"{Path.GetFileName(f)} {name,-26} v{ver} hash {Chunks.U32(d, o + 0xC):X8} size {size} | " + string.Join(" ", fl.Select(x => x.ToString("0.###"))));
                }
            });
        }
    }

    static void Walk(byte[] d, int start, int end, int depth, Action<uint, int, int, int> visit)
    {
        int o = start;
        while (o + 8 <= end)
        {
            uint id = Chunks.U32(d, o);
            int size = Chunks.I32(d, o + 4);
            if (size < 0 || o + 8 + size > end) break;
            visit(id, o + 8, size, depth);
            if ((id & 0x80000000) != 0 && depth < 6) Walk(d, o + 8, o + 8 + size, depth + 1, visit);
            o += 8 + size;
        }
    }

    static void Parts(string root, string car, int slot)
    {
        var db = CarPartDb.Load(root);
        car = car.ToUpperInvariant();
        var attrNames = new[] { "TEXTURE", "TEXTURE_NAME", "NAME", "SIZE", "SHAPE", "NUMCOLOURS", "REMAP", "LIGHT_MATERIAL_NAME", "RED", "GREEN", "BLUE", "GLOSS",
            "BRAND_NAME", "KITNUMBER", "CARBONFIBRE", "SPINNER_TEXTURE", "LANGUAGEHASH", "INNER_RADIUS", "OUTER_RADIUS", "STOCK", "METAL", "PEARL", "DAMAGE_LEVEL",
            "MODEL_TABLE_OFFSET", "PART_NAME_OFFSETS", "SPOKE_COUNT", "NAME_OFFSET", "DEFAULT" };
        var names = attrNames.ToDictionary(n => EaHash.Hash(n), n => n);
        for (int s = 0; s < 139; s++)
        {
            if (slot >= 0 && s != slot) continue;
            var list = db.PartsFor(car, s).ToList();
            if (list.Count == 0) continue;
            Console.WriteLine($"-- slot {s}: {list.Count} parts");
            foreach (var p in list.Take(slot >= 0 ? 400 : 6))
            {
                var attrs = string.Join(" ", p.Attributes.Select(a => (names.TryGetValue(a.hash, out var n) ? n : a.hash.ToString("X8")) + "=" + Show(db, a, names)));
                string model = "";
                for (int lod = 0; lod < 2; lod++)
                {
                    uint mh = p.ModelHash(lod, EaHash.Hash(car));
                    model += $" m{lod}={mh:X8}";
                }
                Console.WriteLine($"   {p.Name,-28} lvl {p.Level} grp {p.Group} sel {p.BaseModelSelector} type {p.CarTypeHash:X8} sfx {p.ModelSuffix()}{model} | {attrs}");
            }
        }
    }

    static string Show(CarPartDb db, (uint hash, uint param) a, Dictionary<uint, string> names)
    {
        if (names.TryGetValue(a.hash, out var n) && (n == "TEXTURE" || n == "LIGHT_MATERIAL_NAME" && false)) return db.String((int)a.param) ?? a.param.ToString("X");
        return a.param.ToString("X");
    }

    static void AllCars(string root)
    {
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "CARS")))
        {
            var geo = Path.Combine(dir, "GEOMETRY.BIN");
            if (!File.Exists(geo)) continue;
            var d = File.ReadAllBytes(geo);
            var sol = SolidList.ReadAll(d, 0, d.Length);
            var dmg = sol.Where(s => s.Name.Contains("DAMAGE") && s.Name.EndsWith("_A")).Select(s => s.Name.Substring(s.Name.IndexOf('_') + 1)).ToList();
            var dec = sol.Count(s => s.Name.Contains("_DECAL_") && s.Name.EndsWith("_A"));
            Console.WriteLine($"{Path.GetFileName(dir),-14} solids {sol.Count,4} decals {dec,3} damage: {string.Join(" ", dmg)}");
        }
    }
}

static class ProbeCarRenderRaw
{
    public static void LightMatIdx(string root, string car, string solidName)
    {
        var d = File.ReadAllBytes(Path.Combine(root, "CARS", car, "GEOMETRY.BIN"));
        foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x80134000))
            foreach (var s in Chunks.Children(d, c))
            {
                if (s.Id != 0x80134010) continue;
                var solid = SolidList.ReadSolid(d, s);
                if (solid == null || solid.Name != solidName) continue;
                foreach (var k in Chunks.Children(d, s))
                {
                    Console.WriteLine($"chunk {k.Id:X8} size {k.Size}");
                    if (k.Id == 0x134013 || k.Id == 0x134012)
                        for (int o = k.DataStart; o + 8 <= k.End; o += 8)
                            Console.WriteLine($"   {Chunks.U32(d, o):X8} {Chunks.U32(d, o + 4):X8}");
                    if (k.Id == 0x80134100)
                        foreach (var m in Chunks.Children(d, k))
                            if (m.Id == 0x134B02)
                            {
                                int a = Chunks.Align(m.DataStart, 16);
                                for (int e = a; e + 0x68 <= m.End; e += 0x68)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    for (int i = 0x10; i < 0x30; i++) sb.Append(d[e + i].ToString("X2") + (i % 4 == 3 ? " " : ""));
                                    Console.WriteLine("   mat " + sb);
                                }
                            }
                }
                foreach (var p in solid.Parts) Console.WriteLine($"   part {p.MaterialName} tex {p.TextureHash:X8}");
            }
    }
}

static class ProbeCarRenderAttr
{
    public static void Refs(string root, string car)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var cls in new[] { "pvehicle" })
        {
            var c = db.Get(cls, car);
            for (var cc = c; cc != null; cc = cc.Parent)
            {
                Console.WriteLine($"-- {cc}");
                foreach (var f in cc.Class.Fields.Values)
                {
                    if (!cc.TryGetKey(f.Key, out int at, out int n, out _)) continue;
                    string name = db.Names.TryGetValue(f.Key, out var nm) ? nm : f.Key.ToString("X8");
                    uint a = Chunks.U32(cc.Data, at), b = at + 8 <= cc.Data.Length ? Chunks.U32(cc.Data, at + 4) : 0;
                    if (db.Classes.ContainsKey(a)) Console.WriteLine($"   {name} -> class {a:X8} key {b:X8} {(db.Names.TryGetValue(b, out var bn) ? bn : "")} (n {n}, size {f.Size})");
                }
            }
        }
    }
}

static class ProbeCarRenderEvt
{
    public static void Names(string root, string pat)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var kv in db.Names) if (kv.Value.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) Console.WriteLine($"{kv.Key:X8} {kv.Value}");
        uint k = AttribDatabase.Hash(pat);
        foreach (var c in db.Collections.Values) if (c.Key == k || c.ClassKey == k) Console.WriteLine($"coll {c} class {c.ClassKey:X8} name {(db.Names.TryGetValue(c.ClassKey, out var n) ? n : "")}");
    }
}

static class ProbeCarRenderComposite
{
    static uint H(string s) => EaHash.Hash(s);

    public static void PartsLm(string root, string car)
    {
        var a = CarAsset.Load(root, car.ToUpperInvariant());
        var lmNames = LightMaterials.Read(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GlobalB.lzc")));
        var extra = new[] { "CARSKIN", "WINDSHIELD", "MAGSILVER", "MAGCHROME", "MAGGUNMETAL", "CALIPER", "CALLIPER" }.ToDictionary(H, x => x);
        foreach (var s in a.Solids.Where(s => s.Name.EndsWith("_A") && !s.Name.Contains("KIT0") || s.Name.Contains("KIT00")))
            Console.WriteLine($"{s.Name,-44} " + string.Join(" ", s.Parts.Select(p => p.MaterialName + "=" + (lmNames.TryGetValue(p.LightMaterialHash, out var m) ? m.Name : extra.TryGetValue(p.LightMaterialHash, out var e) ? "*" + e : p.LightMaterialHash.ToString("X8")))));
        var w = CarAsset.Load(root, "WHEELS");
        foreach (var s in w.Solids.Where(s => s.Name.StartsWith("BBS_STYLE01_17")).Take(2))
            Console.WriteLine($"{s.Name,-44} " + string.Join(" ", s.Parts.Select(p => p.MaterialName + "=" + (lmNames.TryGetValue(p.LightMaterialHash, out var m) ? m.Name : extra.TryGetValue(p.LightMaterialHash, out var e) ? "*" + e : p.LightMaterialHash.ToString("X8")) + ":" + p.TextureHash.ToString("X8"))));
    }

    public static void Types(string root)
    {
        var d = GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GlobalB.lzc"));
        var db = CarPartDb.Read(d);
        foreach (var t in CarTypeRenderInfo.Read(d).Values)
        {
            var paint = db.PartsFor(t.Name, 76).FirstOrDefault(p => p.NameHash == t.DefaultBasePaint || EaHash.Hash(p.Name ?? "") == t.DefaultBasePaint);
            Console.WriteLine($"{t.Name,-14} base {t.BaseModel,-12} usage {t.UsageType} skin {t.Skinnable} defpaint {t.DefaultBasePaint:X8} {(paint?.Name ?? "-")} tint {db.PartsFor(t.Name, 131).Count()}");
        }
    }

    public static void Skin(string root, string car, string vinyl, string outPng)
    {
        car = car.ToUpperInvariant();
        var v = File.ReadAllBytes(Path.Combine(root, "CARS", car, "VINYLS.BIN"));
        var pack = TexturePack.ReadAll(v, 0, v.Length);
        var layer = pack.FirstOrDefault(t => t.Hash == H(car + "_" + vinyl));
        var mask = pack.FirstOrDefault(t => t.Hash == H(car + "_" + vinyl + "_MASK"));
        Console.WriteLine($"layer {layer} mask {mask}");
        var cols = new[] { SkinComposite.Colour(255, 0, 0, 100), SkinComposite.Colour(0, 255, 0, 100), SkinComposite.Colour(0, 0, 255, 100) };
        var px = SkinComposite.Skin(512, 512, SkinComposite.Colour(230, 230, 230, 0x80), SkinComposite.Pixels(layer), SkinComposite.Pixels(mask), true, cols);
        Png(outPng, 512, 512, px);
        Png(outPng.Replace(".png", "_layer.png"), layer.Width, layer.Height, SkinComposite.Pixels(layer));
        Png(outPng.Replace(".png", "_mask.png"), mask.Width, mask.Height, SkinComposite.Pixels(mask));
    }

    public static void Png(string path, int w, int h, uint[] bgra)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                for (int x = 0; x < w; x++) { uint c = bgra[y * w + x]; z.WriteByte((byte)(c >> 16)); z.WriteByte((byte)(c >> 8)); z.WriteByte((byte)c); }
            }
        var idat = ms.ToArray();
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); f.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type); f.Write(t); f.Write(data);
            uint crc = Crc(t.Concat(data).ToArray()); var cb = BitConverter.GetBytes(crc); Array.Reverse(cb); f.Write(cb);
        }
        var ihdr = new byte[13];
        var wb = BitConverter.GetBytes(w); Array.Reverse(wb); Array.Copy(wb, 0, ihdr, 0, 4);
        var hb = BitConverter.GetBytes(h); Array.Reverse(hb); Array.Copy(hb, 0, ihdr, 4, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr); Chunk("IDAT", idat); Chunk("IEND", new byte[0]);
    }

    static uint Crc(byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in b) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }
}

static class ProbeCarRenderShared
{
    public static void Folder(string root, string folder, string prefix)
    {
        var a = CarAsset.Load(root, folder);
        var lms = LightMaterials.Read(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GlobalB.lzc")));
        var names = new[] { "CARSKIN", "SPOILER_SKIN1", "ROOF_SKIN", "GLOBAL_SKIN1", "CARBONFIBRE", "CARBONFIBER", "MAGSILVER", "MAGCHROME", "MAGGUNMETAL", "WINDSHIELD" }.ToDictionary(n => EaHash.Hash(n), n => n);
        string N(uint h) => lms.TryGetValue(h, out var m) ? m.Name : names.TryGetValue(h, out var n) ? n : h.ToString("X8");
        foreach (var s in a.Solids.Where(s => s.Name.StartsWith(prefix) && s.Name.EndsWith("_A")).Take(6))
            Console.WriteLine($"{s.Name,-40} " + string.Join(" ", s.Parts.Select(p => p.MaterialName + ":" + N(p.TextureHash) + "/" + N(p.LightMaterialHash))));
    }
}

static class ProbeCarRenderMath
{
    public static void Run(string root)
    {
        // sun at azimuth 30 deg, elevation 45 deg (game space, z up)
        double az = 30 * Math.PI / 180, el = 45 * Math.PI / 180;
        float sx = (float)(Math.Cos(el) * Math.Cos(az)), sy = (float)(Math.Cos(el) * Math.Sin(az)), sz = (float)Math.Sin(el);
        Console.WriteLine($"sun {sx:0.###} {sy:0.###} {sz:0.###}");
        foreach (var l in ShaperRig.CarsInGame)
        {
            var (x, y, z) = ShaperRig.Direction(l, sx, sy, sz);
            double a = Math.Atan2(y, x) * 180 / Math.PI, e = Math.Asin(z) * 180 / Math.PI;
            Console.WriteLine($"light theta {l.Theta} phi {l.Phi}: dir {x:0.###} {y:0.###} {z:0.###}  azimuth {a:0.#} elevation {e:0.#} colour {l.R * l.Scale:0.###} {l.G * l.Scale:0.###} {l.B * l.Scale:0.###}");
        }
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var car in new[] { "gti", "copmidsize", "copsuv", "traf4dseda", "911turbo" })
        {
            var pv = db.Get("pvehicle", car);
            string r = "-";
            if (pv != null && pv.TryGet("damagespecs", out int at, out _, out _))
                r = $"{Chunks.U32(pv.Source.Data, at):X8} {Chunks.U32(pv.Source.Data, at + 4):X8} ({(db.Names.TryGetValue(Chunks.U32(pv.Source.Data, at + 4), out var nm) ? nm : "?")})";
            Console.WriteLine($"{car}: damagespecs {r}");
        }
        var lms = LightMaterials.Read(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GlobalB.lzc")));
        foreach (var n in new[] { "REGPAINTWHITE", "METPAINTRED", "PEARL1", "WINDSHIELD", "WINDSHIELD_TINT_L1_BLACK", "CHROME", "CARBONFIBER", "TRAFFIC", "DECAL" })
        {
            var m = lms[EaHash.Hash(n)];
            var c = m.Shader();
            string V(float[] f) => string.Join(",", f.Select(x => x.ToString("0.###")));
            Console.WriteLine($"{n,-26} dMin {V(c.DiffuseMin)} dRange {V(c.DiffuseRange)} sMin {V(c.SpecularMin)} sRange {V(c.SpecularRange)} eMin {V(c.EnvmapMin)} eRange {V(c.EnvmapRange)} sp {c.SpecularPower} ep {c.EnvmapPower}");
        }
        // zone classification: half extents 0.9 x 0.7 x 2.2 (x right, y up, z forward)
        (float, float, float, float, float, float)[] hits = { (0, 0, 2.2f, 0, 0, -1), (0.8f, 0, 1.8f, -1, 0, 0), (-0.8f, 0, -1.8f, 1, 0, 0), (0.9f, 0, 0, -1, 0, 0), (0, 0.7f, 0, 0, -1, 0), (0, 0, -2.2f, 0, 0, 1) };
        foreach (var h in hits) Console.WriteLine($"arm {h.Item1},{h.Item2},{h.Item3} n {h.Item4},{h.Item5},{h.Item6} -> {DamageZone.Names[DamageZone.Classify(h.Item1, h.Item2, h.Item3, h.Item4, h.Item5, h.Item6, 0.9f, 0.7f, 2.2f)]}");
        Console.WriteLine($"level racer front, 1400kg x 20 m/s wall (impulse ~ 34000): {DamageZone.VisualLevel(34000, 20, 0.8f)}; side 12000: {DamageZone.VisualLevel(12000, 20, 3.5f)}");
    }
}

static class ProbeCarRenderTex
{
    public static void Dump(string root, string name, string outPng)
    {
        var d = GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GlobalB.lzc"));
        var t = TexturePack.ReadAll(d, 0, d.Length).FirstOrDefault(x => x.Hash == EaHash.Hash(name));
        var px = SkinComposite.Pixels(t);
        Console.WriteLine($"{t} px {(px == null ? "null" : "ok")}");
        if (px == null) return;
        long r = 0, g = 0, b = 0, a = 0;
        foreach (var c in px) { r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; a += c >> 24; }
        Console.WriteLine($"avg rgba {r / px.Length} {g / px.Length} {b / px.Length} {a / px.Length}");
        ProbeCarRenderComposite.Png(outPng, t.Width, t.Height, px);
    }
}

static class ProbeCarRenderAlpha
{
    public static void Run(string root, string[] names)
    {
        var d = GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "GlobalB.lzc"));
        var all = TexturePack.ReadAll(d, 0, d.Length);
        foreach (var n in names)
        {
            var t = all.FirstOrDefault(x => x.Hash == EaHash.Hash(n));
            if (t == null) { Console.WriteLine($"{n}: missing"); continue; }
            if (t.Format != TexFormat.Dxt3) { Console.WriteLine($"{t}"); continue; }
            int blocks = Math.Max(1, t.Width / 4) * Math.Max(1, t.Height / 4);
            long sum = 0; int min = 15, max = 0;
            for (int b = 0; b < blocks; b++)
                for (int i = 0; i < 8; i++)
                {
                    byte v = t.Data[b * 16 + i];
                    int lo = v & 15, hi = v >> 4;
                    sum += lo + hi; min = Math.Min(min, Math.Min(lo, hi)); max = Math.Max(max, Math.Max(lo, hi));
                }
            Console.WriteLine($"{t} alpha avg {sum * 17.0 / (blocks * 16):0.#} min {min * 17} max {max * 17}");
        }
    }
}
