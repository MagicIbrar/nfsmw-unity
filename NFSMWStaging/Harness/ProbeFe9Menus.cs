using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// Round 9 front-end menus probe: `fe9menus tree <pkg>` (every FE bundle incl. GLOBAL), `fe9menus lang <hash..>`,
// `fe9menus hist` (the language histogram decode check).
static class ProbeFe9Menus
{
    static Dictionary<string, FEPackage> All(string root)
    {
        var all = new Dictionary<string, FEPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { "FRONTEND/FrontB.lzc", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameB.lzc", "GLOBAL/InGameSplitScreen.bun", "GLOBAL/THINSCREEN_GLOBAL.BUN" })
        {
            var p = Path.Combine(root, f);
            if (!File.Exists(p)) continue;
            foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(p))) all.TryAdd(kv.Key, kv.Value);
        }
        return all;
    }

    static Dictionary<uint, string> Names(string root)
    {
        var exe = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(root, "speed.exe")));
        var names = new Dictionary<uint, string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(exe, @"[A-Za-z0-9_ .%\-]{2,64}"))
        {
            var s = m.Value;
            if (s.Contains("%d")) for (int i = 0; i <= 40; i++) names.TryAdd(EaHash.HashUpper(s.Replace("%d", i.ToString())), s.Replace("%d", i.ToString()));
            else names.TryAdd(EaHash.HashUpper(s), s);
        }
        return names;
    }

    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "hist";
        if (mode == "tree")
        {
            var all = All(root);
            var names = Names(root);
            var p = all[args[3]];
            void Walk(uint parent, int depth)
            {
                foreach (var o in p.Objects)
                {
                    if (o.Parent != parent) continue;
                    string n = names.TryGetValue(o.Hash, out var nn) ? nn : o.Hash.ToString("X8");
                    bool hidden = (o.Flags & 0x10000000) != 0;
                    Console.WriteLine($"{new string(' ', depth * 2)}{n} [{o.Hash:X8}] t{(int)o.Type}{(hidden ? " HIDDEN" : "")} pos({o.Data.X:0.#},{o.Data.Y:0.#}) size({o.Data.SizeX:0.#},{o.Data.SizeY:0.#}) rgba({o.Data.R:0},{o.Data.G:0},{o.Data.B:0},{o.Data.A:0}) fl{o.Flags:X} j{o.Justify:X} mw{o.MaxWidth} ld{o.Leading} '{(o.Text ?? "").Replace("\n", "\\n")}' {(o.Resource >= 0 && o.Resource < p.Resources.Count ? p.Resources[o.Resource] : "")} scripts {string.Join(",", o.Scripts.Select(sc => names.TryGetValue(sc.Id, out var sn) ? sn : sc.Id.ToString("X8")))}");
                    Walk(o.Guid, depth + 1);
                }
            }
            Walk(0, 0);
            return;
        }
        if (mode == "events")
        {
            var all = All(root);
            var p = all[args[3]];
            uint want = args.Length > 4 ? Convert.ToUInt32(args[4], 16) : 0;
            foreach (var o in p.Objects)
                foreach (var sc in o.Scripts)
                    foreach (var ev in sc.Events)
                        if (want == 0 || ev.message == want) Console.WriteLine($"{o.Hash:X8} script {sc.Id:X8} len {sc.Length} t={ev.time} msg {ev.message:X8} -> {ev.target:X8}");
            return;
        }
        if (mode == "named")
        {
            var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
            foreach (var nm in args.Skip(3)) Console.WriteLine($"{nm}: {(db.Named(nm) != null ? "yes" : "no")}");
            return;
        }
        if (mode == "tex")
        {
            string[] files = { "FRONTEND/FRONTA.BUN", "GLOBAL/RIVALS.BIN", "GLOBAL/CardTextures.bin", "FRONTEND/FrontB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameA.bun", "GLOBAL/InGameB.lzc", "GLOBAL/InGameSplitScreen.bun", "GLOBAL/HUDTEXRACE.BIN", "GLOBAL/HUDTEXDRAG.BIN", "GLOBAL/HUDS_Custom_00.bin", "LANGUAGES/LanguageTextures.bin" };
            var want = args.Skip(3).Select(a => EaHash.HashUpper(a)).ToHashSet();
            foreach (var f in files)
            {
                var p = Path.Combine(root, f);
                if (!File.Exists(p)) continue;
                var bd = GameFiles.ReadBundle(p);
                foreach (var t in TexturePack.ReadAll(bd, 0, bd.Length, false))
                    if (want.Contains(t.Hash)) Console.WriteLine($"{t.Name} {t.Hash:X8} in {f} blend {t.AlphaBlend} usage {t.AlphaUsage} fmt {t.Format}");
            }
            Console.WriteLine("done");
            return;
        }
        var lang = StringTable.Read(GameFiles.ReadBundle(Path.Combine(root, "LANGUAGES", "English.bin")));
        if (mode == "lang")
        {
            foreach (var a in args.Skip(3))
            {
                uint h = a.StartsWith("0x") ? Convert.ToUInt32(a.Substring(2), 16) : EaHash.HashUpper(a);
                Console.WriteLine($"{a} {h:X8} = {(lang.TryGet(h, out var s) ? "'" + s + "'" : "(missing)")}");
            }
            return;
        }
        // hist: strings whose decoding uses the histogram (™ “ ” – ‘)
        int n = 0;
        foreach (uint h in new uint[] { 0x9BB9CCC3 })
            Console.WriteLine($"{h:X8} = '{lang.Get(h)}'");
        var d = GameFiles.ReadBundle(Path.Combine(root, "LANGUAGES", "English.bin"));
        foreach (var c in Chunks.Read(d, 0, d.Length))
        {
            if (c.Id != 0x39000) continue;
            var hist = WideCharHistogram.Read(d, c.DataStart, c.End);
            Console.WriteLine($"histogram entries {hist?.NumEntries}");
            int b = c.DataStart, cnt = Chunks.I32(d, b + 4), table = b + Chunks.I32(d, b + 8);
            for (int i = 0; i < cnt; i++)
            {
                uint h = Chunks.U32(d, table + i * 8);
                string s = lang.Get(h);
                if (s != null && s.Any(ch => ch > 0x2000) && n++ < 12) Console.WriteLine($"  {h:X8}: {s}");
            }
        }
        Console.WriteLine($"Group(1234567) = {Localize.Group(1234567)}, Fixed(12345.678,1,$) = {Localize.Fixed(12345.678, 1, true)}, percent '{Localize.GetLocalizedPercentSign()}', language {Localize.CurrentLanguage}");
    }
}
