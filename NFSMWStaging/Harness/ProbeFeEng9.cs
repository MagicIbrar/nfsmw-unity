using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NfsMw.Core;

/// <summary>
/// Round 9 FEng data scans (fe_ingame.md §13): `feeng9 scripts` histograms the raw script sub-tags (the exe's
/// ReadScriptTags 0x5C3610 knows Sn/Sh/Sc/Si/Sl/Sf/FI/To/Ti/Tt/Ta/Tb/Kd/EV), FI track lengths past 24 bits, tracks without a
/// To tag, unsorted script events, play actions / interp types, objects without INIT, packages without a Targ chunk.
/// `feeng9 objtags` histograms the ObjD field tags per object type.
/// </summary>
static class ProbeFeEng9
{
    static readonly string[] Bundles = { "FRONTEND/FRONTA.BUN", "FRONTEND/FrontB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameA.bun",
        "GLOBAL/InGameB.lzc", "GLOBAL/InGameSplitScreen.bun", "LANGUAGES/LanguageTextures.bin", "GLOBAL/THINSCREEN_GLOBAL.BUN" };

    static IEnumerable<(string name, byte[] pkg)> RawPackages(string root)
    {
        foreach (var f in Bundles)
        {
            var p = Path.Combine(root, f);
            if (!File.Exists(p)) continue;
            var d = GameFiles.ReadBundle(p);
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x30210).Concat(Chunks.FindAll(d, 0, d.Length, 0x30203)))
            {
                int at = c.DataStart + (c.Id == 0x30203 ? 0 : 4);
                byte[] b;
                if (Jdlz.IsJdlz(d, at)) b = Jdlz.Decompress(d, at);
                else { b = new byte[c.End - at]; Buffer.BlockCopy(d, at, b, 0, b.Length); }
                if (b.Length < 8 || Chunks.U32(b, 0) != 0xE76E4546) continue;
                yield return (f + ":" + c.Start.ToString("X"), b);
            }
        }
    }

    static IEnumerable<(string tag, int off, int size)> Sub(byte[] p, int o, int end)
    {
        while (o + 8 <= end)
        {
            string tag = Encoding.GetEncoding(28591).GetString(p, o, 4);
            int s = Chunks.I32(p, o + 4);
            if (s < 0 || o + 8 + s > end) yield break;
            yield return (tag, o + 8, s);
            o += 8 + s;
        }
    }

    static IEnumerable<(string tag, int v, int len)> Tags(byte[] p, int o, int end)
    {
        while (o + 4 <= end)
        {
            string tag = Encoding.ASCII.GetString(p, o, 2);
            int len = Chunks.U16(p, o + 2);
            if (o + 4 + len > end) yield break;
            yield return (tag, o + 4, len);
            o += 4 + len;
        }
    }

    static void Inc(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "scripts";
        if (mode == "appear")
        {
            // objects with an APPEAR script that no response sets (MR_SetScript APPEAR) and whose INIT (or its chain end)
            // leaves alpha 0: hidden unless code plays APPEAR on them
            uint ap = EaHash.HashUpper("APPEAR"), init = 0x1744B3;
            foreach (var kv in ProbeFeRest.AllPackages(root).OrderBy(k => k.Key))
            {
                int noResp = 0, hiddenNoResp = 0;
                var names = new List<string>();
                foreach (var o in kv.Value.Objects)
                {
                    if (o.Script(ap) == null) continue;
                    if (o.MessageResponses.Exists(r => r.List.Exists(c => c.Id == 0 && c.Param == ap))) continue;
                    noResp++;
                    var s = o.Script(init);
                    for (int k = 0; k < 4 && s != null && s.Chain != 0 && s.Chain != ap; k++) s = o.Script(s.Chain);
                    if (s != null && s.Chain == ap) continue;   // INIT chains into APPEAR itself
                    float a = o.Data.A;
                    if (s != null) foreach (var t in s.Tracks) if (t.Offset == 0) { var v = t.Evaluate(s.Length + 1); if (v != null && v.Length > 3) a = v[3]; }
                    if (a == 0) { hiddenNoResp++; if (names.Count < 6) names.Add(o.Hash.ToString("X8")); }
                }
                if (hiddenNoResp > 0) Console.WriteLine($"{kv.Key}: APPEAR without a response {noResp}, of them alpha 0 after INIT {hiddenNoResp}: {string.Join(" ", names)}");
            }
            return;
        }
        if (mode == "chunks")
        {
            // top-level package chunk tags (FEPackageReader::Load: PkHd, TypS, ResC, ObjC, PkgR, Targ, library refs, ...)
            var h = new Dictionary<string, int>();
            int buttons = 0, mouse = 0, groups = 0;
            foreach (var (name, p) in RawPackages(root))
            {
                int end = Math.Min(p.Length, 8 + Chunks.I32(p, 4));
                foreach (var (tag, o, s) in Sub(p, 8, end))
                {
                    string k = string.Concat(tag.Select(ch => ch >= 32 && ch < 127 ? ch : '?'));
                    Inc(h, k);
                }
            }
            foreach (var kv in ProbeFeRest.AllPackages(root))
                foreach (var o in kv.Value.Objects)
                {
                    if ((o.Flags & 0x10000000) != 0) buttons++;
                    if ((o.Flags & 0x20000) != 0) mouse++;
                    if (o.Type == FEObjectType.Group) groups++;
                }
            foreach (var kv in h.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Key}: {kv.Value}");
            Console.WriteLine($"buttons {buttons}, mouse objects {mouse}, groups {groups}");
            return;
        }
        if (mode == "flag10")
        {
            // objects with game flag 0x10 (cFEngGameInterface::RenderObject 0x516630 replaces their colour by the game mode's)
            foreach (var kv in ProbeFeRest.AllPackages(root).OrderBy(k => k.Key))
            {
                var l = kv.Value.Objects.Where(o => (o.Flags & 0x10) != 0).ToList();
                if (l.Count == 0) continue;
                Console.WriteLine($"{kv.Key}: {l.Count}: " + string.Join(" ", l.Take(8).Select(o => $"{o.Hash:X8}/{o.Type}/{(o.Resource >= 0 && o.Resource < kv.Value.Resources.Count ? kv.Value.Resources[o.Resource] : "-")}")));
            }
            return;
        }
        if (mode == "texfind")
        {
            // feeng9 texfind <hash|name>...: which FE bundle (FrontEndData's list) holds a texture
            var want = args.Skip(3).Select(a => a.Length == 8 && uint.TryParse(a, System.Globalization.NumberStyles.HexNumber, null, out var hx) ? hx : EaHash.HashUpper(a)).ToList();
            foreach (var f in new[] { "FRONTEND/FRONTA.BUN", "GLOBAL/RIVALS.BIN", "GLOBAL/CardTextures.bin", "FRONTEND/FrontB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameA.bun", "GLOBAL/InGameB.lzc", "GLOBAL/InGameSplitScreen.bun", "GLOBAL/HUDTEXRACE.BIN", "GLOBAL/HUDTEXDRAG.BIN", "GLOBAL/HUDS_Custom_00.bin", "LANGUAGES/LanguageTextures.bin", "LANGUAGES/English.bin" })
            {
                var path = Path.Combine(root, f);
                if (!File.Exists(path)) continue;
                var d = GameFiles.ReadBundle(path);
                foreach (var t in TexturePack.ReadAll(d, 0, d.Length, false))
                    if (want.Contains(t.Hash)) Console.WriteLine($"{f}: {t.Name} {t.Hash:X8} {t.Width}x{t.Height} usage {t.AlphaUsage} blend {t.AlphaBlend}");
            }
            return;
        }
        if (mode == "minimap")
        {
            // feeng9 minimap [file]: the 0x3A100 chunks of TRACKS\L2RA\MINI_MAP*.BIN (ChoppedMiniMapManager::Loader) and the
            // textures in each (LZ header + an embedded texture pack)
            var files = args.Length > 3 ? new[] { args[3] } : Directory.GetFiles(Path.Combine(root, "TRACKS", "L2RA"), "MINI_MAP*.BIN").Select(Path.GetFileName).ToArray();
            int shown = 0;
            foreach (var fn in files)
            {
                var d = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA", fn));
                var names = new List<string>();
                int chunks = 0;
                foreach (var c in Chunks.Read(d, 0, d.Length))
                {
                    if (c.Id != 0x3A100) { names.Add($"[chunk {c.Id:X}]"); continue; }
                    chunks++;
                    var b = Compression.IsCompressed(d, c.DataStart) ? Compression.Decompress(d, c.DataStart) : d[c.DataStart..c.End];
                    foreach (var t in TexturePack.ReadAll(b, 0, b.Length, false)) names.Add($"{t.Name} {t.Width}x{t.Height} u{t.AlphaUsage} b{t.AlphaBlend}");
                }
                if (args.Length > 3 || shown++ < 6 || fn.IndexOf("UNLOCK", StringComparison.OrdinalIgnoreCase) >= 0)
                    Console.WriteLine($"{fn}: {chunks} chops: {string.Join(", ", names.Take(70))}");
                else Console.WriteLine($"{fn}: {chunks} chops, first {names.FirstOrDefault()}");
            }
            return;
        }
        if (mode == "sa")
        {
            // feeng9 sa <pkg> <name|hash>...: every SA dword and the multi-image texture fields
            var pk = ProbeFeRest.AllPackages(root)[args[3]];
            for (int a = 4; a < args.Length; a++)
            {
                uint h = args[a].Length == 8 && uint.TryParse(args[a], System.Globalization.NumberStyles.HexNumber, null, out var hx) ? hx : EaHash.HashUpper(args[a]);
                foreach (var o in pk.Objects.Where(x => x.Hash == h))
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < FEObjectData.Dwords; i++) sb.Append($" {i}:{o.Data[i]}");
                    Console.WriteLine($"{args[a]} {o.Type} flags {o.Flags:X8} res {o.Resource}{sb}");
                    foreach (var kv in o.Extra) Console.WriteLine($"   {kv.Key}: {BitConverter.ToString(kv.Value.Take(16).ToArray())}");
                }
            }
            return;
        }
        if (mode == "trackinfo")
        {
            // TrackInfo table (chunk 0x34201, TrackInfo.hpp: 0x120 bytes each): name, number and the minimap calibration
            foreach (var f in new[] { "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/GLOBALMEMORYFILE.BIN", "GLOBAL/InGameA.bun", "GLOBAL/InGameB.lzc" })
            {
                var path = Path.Combine(root, f);
                if (!File.Exists(path)) continue;
                var d = GameFiles.ReadBundle(path);
                foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x34201))
                {
                    int size = c.End - c.DataStart;
                    Console.WriteLine($"{f}: chunk at {c.Start:X} size {size:X} ({size / 0x120} x 0x120, rem {size % 0x120})");
                    for (int o = c.DataStart; o + 0x120 <= c.End; o += 0x120)
                    {
                        string name = Chunks.CString(d, o, 32);
                        Console.WriteLine($"  #{Chunks.U16(d, o + 0x8A)} {name} dir {Chunks.CString(d, o + 0x20, 32)} region {Chunks.CString(d, o + 0x40, 8)} valid {d[o + 0x84]} calUL ({Chunks.F32(d, o + 0xAC)}, {Chunks.F32(d, o + 0xB0)}) width {Chunks.F32(d, o + 0xB4)} rot {Chunks.U16(d, o + 0xB8)} zoom {Chunks.F32(d, o + 0xC0)} zoomTL ({Chunks.F32(d, o + 0x110)}, {Chunks.F32(d, o + 0x114)}) zoomW {Chunks.F32(d, o + 0x118)}");
                    }
                }
            }
            return;
        }
        if (mode == "strings")
        {
            // FEString buffer lengths (Sb: FEPrintf / DoFEngPrintf 0x515D20 cuts the text to Sb - 1 characters when Sb < 0x400)
            // and flag 2 (RenderString 0x5A4390: the literal string instead of the label) on objects that carry a label
            var sb = new Dictionary<string, int>();
            int flag2Label = 0, strings = 0, shortBuf = 0;
            string only = args.Length > 3 ? args[3] : null;
            foreach (var kv in ProbeFeRest.AllPackages(root))
                foreach (var o in kv.Value.Objects)
                {
                    if (o.Type != FEObjectType.String) continue;
                    strings++;
                    int b = o.Extra.TryGetValue("Sb", out var raw) && raw.Length >= 4 ? BitConverter.ToInt32(raw, 0) : -1;
                    Inc(sb, "Sb " + (b < 0 ? "none" : b < 8 ? b.ToString() : b < 16 ? "8-15" : b < 32 ? "16-31" : b < 64 ? "32-63" : b < 256 ? "64-255" : b < 0x400 ? "256-1023" : ">=1024"));
                    if ((o.Flags & 2) != 0 && o.Label != 0) flag2Label++;
                    if (b >= 0 && b < 0x400 && o.Text != null && o.Text.Length > b - 1) shortBuf++;
                    if (only != null && kv.Key.Equals(only, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"{kv.Key} {o.Hash:X8} Sb {b} flags {o.Flags:X8} label {o.Label:X8} just {o.Justify} maxw {o.MaxWidth} text \"{o.Text}\"");
                }
            foreach (var kv in sb.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Key}: {kv.Value}");
            Console.WriteLine($"strings {strings}; flag 2 with a label {flag2Label}; package text longer than Sb - 1: {shortBuf}");
            return;
        }
        var hist = new Dictionary<string, int>();
        int pkgs = 0, noTarg = 0, noTargWithResp = 0, objs = 0, noInit = 0, scripts = 0, unsorted = 0, tracks = 0, noTo = 0, toDiff = 0, lenHigh = 0, toBig = 0;
        foreach (var (name, p) in RawPackages(root))
        {
            pkgs++;
            int end = Math.Min(p.Length, 8 + Chunks.I32(p, 4));
            bool targ = false, objResp = false;
            foreach (var (tag, o, s) in Sub(p, 8, end))
            {
                if (tag == "Targ") targ = true;
                if (tag != "ObjÌ") continue;
                foreach (var (t2, o2, s2) in Sub(p, o, o + s))
                {
                    if (t2 != "FObê") continue;
                    objs++;
                    bool init = false;
                    int otype = -1;
                    foreach (var (t3, o3, s3) in Sub(p, o2, o2 + s2))
                    {
                        if (t3 == "ObjD")
                            foreach (var (ft, fv, fl) in Tags(p, o3, o3 + s3))
                            {
                                if (ft == "Ot") otype = Chunks.I32(p, fv);
                                if (mode == "objtags") Inc(hist, $"type {otype} tag {ft} len {fl}");
                            }
                        if (t3 == "MsgR") objResp = true;
                        if (t3 != "Scrp") continue;
                        scripts++;
                        int running = 0, lastEv = int.MinValue;
                        bool haveTrack = false, haveTo = false, sorted = true;
                        uint flags = 0;
                        foreach (var (st, sv, sl) in Tags(p, o3, o3 + s3))
                        {
                            if (mode == "scripts") Inc(hist, "tag " + st + " len " + (st == "Kd" || st == "EV" || st == "Sn" ? "*" : sl.ToString()));
                            switch (st)
                            {
                                case "Sh":
                                    if (Chunks.U32(p, sv) == 0x001744B3) init = true;
                                    flags = Chunks.U32(p, sv + 8);
                                    Inc(hist, "script action " + (flags & 3) + " flags " + flags.ToString("X"));
                                    break;
                                case "FI":
                                    if (haveTrack && !haveTo) noTo++;
                                    haveTrack = true; haveTo = false; tracks++;
                                    int raw = Chunks.I32(p, sv + 4);
                                    if ((raw & 0xFF000000) != 0) lenHigh++;
                                    Inc(hist, $"track interp {p[sv + 2]} action {p[sv + 3]} type {p[sv]} size {p[sv + 1]}");
                                    running += p[sv + 1] >> 2;
                                    break;
                                case "To":
                                    haveTo = true;
                                    int to = Chunks.I32(p, sv);
                                    if (to > 255 || to < 0) toBig++;
                                    if (to != running - (p[sv - 4 - 8 + 1 + 0] >> 2)) { }   // (checked below)
                                    break;
                                case "EV":
                                    for (int k = sv; k + 12 <= sv + sl; k += 12)
                                    {
                                        int tt = Chunks.I32(p, k + 8);
                                        if (tt < lastEv) sorted = false;
                                        lastEv = tt;
                                        uint tg = Chunks.U32(p, k + 4);
                                        Inc(hist, "event target " + (tg >= 0xFFFFFFF0 || tg == 0 ? tg.ToString("X8") : "object"));
                                    }
                                    break;
                            }
                        }
                        if (haveTrack && !haveTo) noTo++;
                        if (!sorted) unsorted++;
                    }
                    if (!init) noInit++;
                }
            }
            if (!targ) { noTarg++; if (objResp) noTargWithResp++; }
        }
        // To value vs the running offset (the FI default): through the parsed packages
        foreach (var kv in ProbeFeRest.AllPackages(root))
            foreach (var o in kv.Value.Objects)
                foreach (var s in o.Scripts)
                {
                    int run = 0;
                    foreach (var t in s.Tracks) { if (t.Offset != run) toDiff++; run += t.ValueSize >> 2; }
                }
        foreach (var kv in hist.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Key}: {kv.Value}");
        Console.WriteLine($"packages {pkgs} (no Targ {noTarg}, of them with object MsgR {noTargWithResp}); objects {objs}, without INIT {noInit}; scripts {scripts}, unsorted events {unsorted}");
        Console.WriteLine($"tracks {tracks}: without To {noTo}, To > 255 {toBig}, FI length with high byte {lenHigh}, To != running offset {toDiff}");
    }
}
