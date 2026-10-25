using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

/// <summary>
/// Front-end remainder probe (fe_safehouse.md / fe_ingame.md "remainder" sections): `ferest fonts` lists the FNTF fonts
/// (flags, height, kern table, extra-font data), `ferest kern <font> <text>` measures a string with the exe's rules,
/// `ferest pkgs` lists every FE package in every bundle, `ferest objs <pkg>` dumps type / flags / multi-image fields.
/// </summary>
static class ProbeFeRest
{
    static readonly string[] Bundles = { "FRONTEND/FRONTA.BUN", "FRONTEND/FrontB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameA.bun",
        "GLOBAL/InGameB.lzc", "GLOBAL/InGameSplitScreen.bun", "LANGUAGES/LanguageTextures.bin", "GLOBAL/THINSCREEN_GLOBAL.BUN" };

    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "fonts";
        if (mode == "fonts")
        {
            foreach (var f in Bundles)
            {
                var p = Path.Combine(root, f);
                if (!File.Exists(p)) continue;
                var d = GameFiles.ReadBundle(p);
                foreach (var font in FEFont.ReadAll(d, 0, d.Length))
                    Console.WriteLine($"{f}: {font.Name} hash {EaHash.HashUpper(font.Name):X8} tex {font.Texture} h {font.LineHeight} glyphs {font.Glyphs.Count} flags {font.Flags:X} kern {font.KernCount} baseline {font.BaselineOffset} leadScale {font.LeadingScale}");
            }
        }
        else if (mode == "measure")
        {
            var font = AllFonts(root).First(x => x.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
            string s = args[4];
            Console.WriteLine($"width {font.LineWidth(s, 0, 0u)} text width {font.TextWidth(s, 0)} height {font.TextHeight(s, 0, 0u, 0u, false)}");
        }
        else if (mode == "pkgobjs")
        {
            foreach (var kv in AllPackages(root).Where(k => k.Key.Equals(args[3], StringComparison.OrdinalIgnoreCase)))
                foreach (var o in kv.Value.Objects)
                    Console.WriteLine($"{o.Guid:X8} {o.Hash:X8} {o.Type} fl {o.Flags:X8} res {(o.Resource >= 0 && o.Resource < kv.Value.Resources.Count ? kv.Value.Resources[o.Resource] : "-")} par {o.Parent:X8} extra {string.Join(",", o.Extra.Keys)} {o.Text}");
        }
        else if (mode == "keys")
        {
            // ferest keys <pkg> <obj name or hash>: every script's tracks with their keys
            var pk = AllPackages(root)[args[3]];
            uint h = args[4].Length == 8 && uint.TryParse(args[4], System.Globalization.NumberStyles.HexNumber, null, out var hx) ? hx : EaHash.HashUpper(args[4]);
            foreach (var o in pk.Objects.Where(x => x.Hash == h))
            {
                var d = o.Data;
                Console.WriteLine($"{o.Hash:X8} type {o.Type} flags {o.Flags:X8} SA bgra({d.B},{d.G},{d.R},{d.A}) pos({d.X},{d.Y},{d.Z}) size({d.SizeX},{d.SizeY}) uv({d.U0},{d.V0},{d.U1},{d.V1})");
                foreach (var s in o.Scripts)
                {
                    Console.WriteLine($"  script {s.Id:X8} len {s.Length} flags {s.Flags} chain {s.Chain:X8} events {string.Join(" ", s.Events.Select(e => $"{e.time}:{e.message:X8}->{e.target:X8}"))}");
                    foreach (var t in s.Tracks)
                        Console.WriteLine($"    track off {t.Offset} vt {t.ValueType} interp {t.Interp}: " + string.Join("  ", t.Keys.Select(k => k.time + "=[" + string.Join(",", k.value) + "]")));
                }
            }
        }
        else if (mode == "texblend")
        {
            // ferest texblend [pkg]: AlphaUsage / AlphaBlend of the FE textures (all, or the ones a package uses)
            var tex = new Dictionary<uint, GameTexture>();
            foreach (var f in Bundles.Concat(new[] { "GLOBAL/HUDTEXRACE.BIN", "GLOBAL/HUDS_Custom_00.bin", "LANGUAGES/English.bin", "GLOBAL/InGameA.bun" }))
            {
                var p = Path.Combine(root, f);
                if (!File.Exists(p)) continue;
                var d = GameFiles.ReadBundle(p);
                foreach (var t in TexturePack.ReadAll(d, 0, d.Length, false)) tex[t.Hash] = t;
            }
            IEnumerable<string> names;
            if (args.Length > 3) names = AllPackages(root)[args[3]].Resources;
            else names = tex.Values.Select(t => t.Name);
            var hist = new Dictionary<string, int>();
            foreach (var n in names.Distinct())
            {
                var h = EaHash.HashUpper(Path.GetFileNameWithoutExtension(n));
                if (!tex.TryGetValue(h, out var t)) { if (args.Length > 3) Console.WriteLine($"{n}: (not loaded)"); continue; }
                string k = $"usage {t.AlphaUsage} blend {t.AlphaBlend}";
                hist[k] = hist.GetValueOrDefault(k) + 1;
                if (args.Length > 3 || t.AlphaBlend > 1 || t.AlphaBlend == 0) Console.WriteLine($"{n}: {k} {t.Width}x{t.Height} {t.Format}");
            }
            foreach (var kv in hist) Console.WriteLine($"{kv.Key}: {kv.Value}");
        }
        else if (mode == "flagstats")
        {
            int b0 = 0, btn = 0, hie = 0, f40 = 0, f8 = 0, btnVis = 0, b0NotOld = 0, total = 0;
            foreach (var kv in AllPackages(root))
                foreach (var o in kv.Value.Objects)
                {
                    total++;
                    if ((o.Flags & 1) != 0) b0++;
                    if ((o.Flags & 0x10000000) != 0) btn++;
                    if ((o.Flags & 0x80000000) != 0) hie++;
                    if ((o.Flags & 0x40) != 0) f40++;
                    if ((o.Flags & 8) != 0) f8++;
                    bool oldHidden = (o.Flags & 0x90000000) != 0, exeHidden = (o.Flags & 1) != 0;
                    if (oldHidden && !exeHidden) btnVis++;
                    if (!oldHidden && exeHidden) b0NotOld++;
                }
            Console.WriteLine($"objects {total}: bit0 {b0}, IsButton {btn}, HideInEdit {hie}, 0x40 {f40}, 0x8 {f8}; old-hidden but exe-visible {btnVis}; exe-hidden but old-visible {b0NotOld}");
        }
        else if (mode == "visdiff")
        {
            // objects whose start visibility differs between the old rule (0x90000000) and the exe (bit 0 + PC 0x40 pass)
            var names = new Dictionary<uint, string>();
            foreach (var kv in AllPackages(root))
            {
                if (args.Length > 3 && !args.Skip(3).Any(a => kv.Key.Equals(a, StringComparison.OrdinalIgnoreCase))) continue;
                var pk = kv.Value;
                var hidden = new HashSet<uint>();
                var kids = pk.Objects.GroupBy(o => o.Parent).ToDictionary(g => g.Key, g => g.ToList());
                void Hide(FEObject o) { hidden.Add(o.Guid); if (kids.TryGetValue(o.Guid, out var l)) foreach (var c in l) if (c.Guid != o.Guid) Hide(c); }
                foreach (var o in pk.Objects) if ((o.Flags & 1) != 0 || (o.Flags & 0x40) != 0) Hide(o);
                var diff = pk.Objects.Where(o => ((o.Flags & 0x90000000) != 0) != hidden.Contains(o.Guid) && o.Type != FEObjectType.Group).ToList();
                if (diff.Count == 0) continue;
                Console.WriteLine($"{kv.Key}: {diff.Count} -> " + string.Join(" ", diff.Take(24).Select(o => $"{o.Hash:X8}{(hidden.Contains(o.Guid) ? "-" : "+")}{(o.Text != null && o.Text.Length > 0 ? "'" + o.Text + "'" : "")}")));
            }
        }
        else if (mode == "pivots")
        {
            int n = 0, rot = 0;
            foreach (var kv in AllPackages(root))
                foreach (var o in kv.Value.Objects)
                {
                    bool piv = o.Data.PivotX != 0 || o.Data.PivotY != 0 || o.Scripts.Any(sc => sc.Tracks.Any(t => t.Offset == 4 && t.Keys.Any(k => k.value.Any(x => x != 0))));
                    if (!piv) continue;
                    n++;
                    bool r = o.Data.QZ != 0 || o.Scripts.Any(sc => sc.Tracks.Any(t => t.Offset == 10));
                    if (r) rot++;
                    if (r && rot < 30) Console.WriteLine($"{kv.Key} {o.Hash:X8} {o.Type} pivot ({o.Data.PivotX},{o.Data.PivotY}) q ({o.Data.QZ},{o.Data.QW})");
                }
            Console.WriteLine($"objects with a pivot {n}, of them rotating {rot}");
        }
        else if (mode == "initvis")
        {
            // ferest initvis <pkg> [script...]: the objects visible once INIT (and its chains) and the given package-wide
            // scripts have finished, as the engine view computes it (flag bit 0, PC 0x40 pass, alpha through the parents)
            var pk = AllPackages(root)[args[3]];
            var extra = args.Skip(4).Select(a => EaHash.HashUpper(a)).ToList();
            var data = new Dictionary<uint, FEObjectData>();
            foreach (var o in pk.Objects)
            {
                var d = o.Data;
                foreach (var sid in new[] { 0x001744B3u }.Concat(extra))
                {
                    var sc = o.Script(sid);
                    int guard = 0;
                    while (sc != null && guard++ < 8)
                    {
                        if (sc.Tracks.Count > 0 && sc.Tracks[0].Offset == 0) { var v0 = sc.Tracks[0].Evaluate(sc.Length + 1); if (v0 != null) d.Set(0, v0); }
                        if (d.A != 0) foreach (var t in sc.Tracks) { var v = t.Evaluate(sc.Length + 1); if (v != null) d.Set(t.Offset, v); }
                        if (sc.Chain == 0 || (sc.Flags & 3) == 1) break;
                        sc = o.Script(sc.Chain);
                    }
                }
                data[o.Guid] = d;
            }
            var byGuid = pk.Objects.ToDictionary(o => o.Guid);
            foreach (var o in pk.Objects)
            {
                if (o.Type == FEObjectType.Group) continue;
                bool vis = true; float a = 1; var cur = o; string path = "";
                int g2 = 0;
                while (cur != null && g2++ < 20)
                {
                    if ((cur.Flags & 1) != 0 || (cur.Flags & 0x40) != 0) vis = false;
                    a *= data[cur.Guid].A / 255f;
                    path = cur.Hash.ToString("X8") + "/" + path;
                    cur = cur.Parent != 0 && byGuid.TryGetValue(cur.Parent, out var p) && p != cur ? p : null;
                }
                if (vis && a > 0.002f) Console.WriteLine($"VIS a={a:0.00} {path} {o.Type} {o.Text}");
            }
        }
        else if (mode == "types")
        {
            var types = new Dictionary<string, int>();
            var extra = new Dictionary<string, int>();
            foreach (var kv in AllPackages(root))
                foreach (var o in kv.Value.Objects)
                {
                    types[o.Type.ToString()] = types.GetValueOrDefault(o.Type.ToString()) + 1;
                    foreach (var e in o.Extra.Keys) extra[o.Type + ":" + e] = extra.GetValueOrDefault(o.Type + ":" + e) + 1;
                }
            foreach (var t in types) Console.WriteLine($"type {t.Key}: {t.Value}");
            foreach (var t in extra.OrderBy(x => x.Key)) Console.WriteLine($"extra {t.Key}: {t.Value}");
        }
        else if (mode == "tracks")
        {
            // which track offsets / value types / interpolations the packages use
            var c = new Dictionary<string, int>();
            foreach (var kv in AllPackages(root))
                foreach (var o in kv.Value.Objects)
                    foreach (var s in o.Scripts)
                        foreach (var t in s.Tracks)
                        {
                            string k = $"off {t.Offset} vt {t.ValueType} interp {t.Interp} action {t.Action} firstkey<0 {(t.Keys.Count > 0 && t.Keys[0].time < 0)} lenEq {t.Length == s.Length}";
                            c[k] = c.GetValueOrDefault(k) + 1;
                        }
            foreach (var t in c.OrderBy(x => x.Key)) Console.WriteLine($"{t.Key}: {t.Value}");
        }
    }

    public static List<FEFont> AllFonts(string root)
    {
        var r = new List<FEFont>();
        foreach (var f in Bundles)
        {
            var p = Path.Combine(root, f);
            if (!File.Exists(p)) continue;
            var d = GameFiles.ReadBundle(p);
            r.AddRange(FEFont.ReadAll(d, 0, d.Length));
        }
        return r;
    }

    public static Dictionary<string, FEPackage> AllPackages(string root)
    {
        var all = new Dictionary<string, FEPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Bundles)
        {
            var p = Path.Combine(root, f);
            if (!File.Exists(p)) continue;
            foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(p))) all[kv.Key] = kv.Value;
        }
        return all;
    }
}
