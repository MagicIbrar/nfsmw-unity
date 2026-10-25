using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

/// <summary>
/// In-game / common FE screens probe (fe_ingame.md): `feig tree <pkg>` dumps the object tree with raw flags
/// (FF_IsButton 0x10000000, game bit 0x1 invisible), text, label hash (resolved through English.bin), scripts with
/// length/chain, message responses and script events. `feig find <text>` lists packages containing a string.
/// `feig lang <hash...>` resolves language hashes.
/// </summary>
static class ProbeFeInGame
{
    static Dictionary<string, FEPackage> All(string root)
    {
        var all = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")));
        foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameB.lzc")))) all[kv.Key] = kv.Value;
        var ss = Path.Combine(root, "GLOBAL", "InGameSplitScreen.bun");
        if (File.Exists(ss)) foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(ss))) all.TryAdd(kv.Key, kv.Value);
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
        foreach (var s in new[] { "INIT", "SHOW", "HIDE", "APPEAR", "LEAVE", "HIGHLIGHT", "UNHIGHLIGHT", "GREY", "NORMAL", "PULSE", "PRESSED",
                     "ACTIVE", "INACTIVE", "FADEIN", "FADEOUT", "FADE_IN", "FADE_OUT", "BUTTON1", "BUTTON2", "BUTTON3", "SNAP", "UNSNAP",
                     "DIALOGSMALL", "DIALOGMEDIUM", "DIALOGLARGE", "HEADERTEXT", "MESSAGEBLURB", "DIALOG_SMALL", "DIALOG_MEDIUM", "DIALOG_LARGE",
                     "POS1", "POS2", "POS3", "POS4", "POS5", "POS6", "POS7", "POS8", "POS9", "POS10", "POS11", "POS12", "ON", "OFF", "BLINK", "FLASH",
                     "WIN", "LOSE", "WINNER", "LOSER", "EXIT", "ENTER", "OPEN", "CLOSE", "START", "STOP", "SELECT", "DISABLE", "ENABLE", "SLIDE",
                     "ZOOM", "FLIP", "SPIN", "TICK", "DONE", "RESET", "LOCKED", "UNLOCKED", "CHECK", "UNCHECK", "PAGE1", "PAGE2", "PAGE3" })
            names.TryAdd(EaHash.HashUpper(s), s);
        return names;
    }

    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "tree";
        if (mode == "lang")
        {
            var st = Lang(root);
            foreach (var h in args.Skip(3)) Console.WriteLine($"{h} = {st.Get(Convert.ToUInt32(h.Replace("0x", ""), 16)) ?? "?"}");
            return;
        }
        if (mode == "langfind")
        {
            var st = Lang(root);
            Console.WriteLine("(langfind needs a table iterator; use lang)");
            return;
        }
        if (mode == "tex")
        {
            var d = GameFiles.ReadBundle(Path.Combine(root, args[3]));
            foreach (var t in TexturePack.ReadAll(d, 0, d.Length)) Console.WriteLine($"{t.Hash:X8} {t.Name} {t.Width}x{t.Height}");
            return;
        }
        if (mode == "chunks")
        {
            foreach (var f in new[] { "FRONTEND/FrontB.lzc", "GLOBAL/InGameB.lzc" })
            {
                var d = GameFiles.ReadBundle(Path.Combine(root, f));
                foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x30210).Concat(Chunks.FindAll(d, 0, d.Length, 0x30203)))
                {
                    int at = c.DataStart + (c.Id == 0x30203 ? 0 : 4);
                    byte[] pk = Jdlz.IsJdlz(d, at) ? Jdlz.Decompress(d, at) : d.Skip(at).Take(c.End - at).ToArray();
                    FEPackage parsed;
                    try { parsed = FEPackage.Read(pk); } catch { continue; }
                    if (!parsed.Name.StartsWith(args[3], StringComparison.OrdinalIgnoreCase)) continue;
                    int end = 8 + BitConverter.ToInt32(pk, 4);
                    for (int o = 8; o + 8 <= end;)
                    {
                        string tag = System.Text.Encoding.GetEncoding(28591).GetString(pk, o, 4);
                        int sz = BitConverter.ToInt32(pk, o + 4);
                        Console.WriteLine($"{parsed.Name}: chunk '{tag}' {BitConverter.ToUInt32(pk, o):X8} size {sz}");
                        if (args.Length > 4 && tag.StartsWith(args[4]))
                        {
                            var sb = new System.Text.StringBuilder();
                            for (int k = o + 8; k < o + 8 + Math.Min(sz, 2048); k += 4) sb.Append($"{BitConverter.ToUInt32(pk, k):X8} ");
                            Console.WriteLine(sb);
                        }
                        o += 8 + sz;
                    }
                    return;
                }
            }
            return;
        }
        var all = All(root);
        if (mode == "scrflags")
        {
            var cnt = new Dictionary<uint, int>();
            foreach (var kv in all) foreach (var o in kv.Value.Objects) foreach (var sc in o.Scripts) { cnt.TryGetValue(sc.Flags, out var c); cnt[sc.Flags] = c + 1; }
            foreach (var kv in cnt) Console.WriteLine($"flags {kv.Key:X8}: {kv.Value}");
            return;
        }
        if (mode == "pckeys")
        {
            var nm = Names(root);
            string NN(uint h) => nm.TryGetValue(h, out var n) ? n : h.ToString("X8");
            var tally = new Dictionary<string, int>();
            foreach (var kv in all)
            {
                var pq = kv.Value;
                foreach (var o in pq.Objects)
                {
                    if (o.Resource < 0 || o.Resource >= pq.Resources.Count) continue;
                    var res = pq.Resources[o.Resource];
                    if (!res.StartsWith("PC_key", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var sib in pq.Objects.Where(x => x.Parent == o.Parent && x != o))
                    {
                        var click = sib.FindResponse(0x7EABCA56);
                        if (click == null) continue;
                        var what = string.Join(",", click.List.Where(c => c.Id == 1 || c.Id == 2).Select(c => (c.Id == 2 ? "game " : "post ") + NN(c.Param)));
                        string key = res + " -> " + what;
                        tally.TryGetValue(key, out var n); tally[key] = n + 1;
                        if (args.Length > 3) Console.WriteLine($"{kv.Key}: {key}");
                    }
                }
            }
            foreach (var kv in tally.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,4}  {kv.Key}");
            return;
        }
        if (mode == "list") { Console.WriteLine(string.Join(" ", all.Keys.OrderBy(k => k))); return; }
        if (mode == "find")
        {
            foreach (var kv in all)
                foreach (var o in kv.Value.Objects)
                    if (o.Text != null && o.Text.IndexOf(args[3], StringComparison.OrdinalIgnoreCase) >= 0)
                        Console.WriteLine($"{kv.Key}: {o.Hash:X8} '{o.Text}'");
            return;
        }
        var names = Names(root);
        var lang = Lang(root);
        string N(uint h) => names.TryGetValue(h, out var n) ? n : h.ToString("X8");
        var p = all[args[3]];
        Console.WriteLine($"== {p.Name}  resources: {string.Join(", ", p.Resources)}");
        void Walk(uint parent, int depth)
        {
            foreach (var o in p.Objects)
            {
                if (o.Parent != parent) continue;
                string ind = new string(' ', depth * 2);
                string label = o.Label != 0 ? $" L{o.Label:X8}='{lang.Get(o.Label)}'" : "";
                Console.WriteLine($"{ind}{N(o.Hash)} [{o.Hash:X8}] g{o.Guid:X} t{(int)o.Type} fl{o.Flags:X8} pos({o.Data.X:0.#},{o.Data.Y:0.#},{o.Data.Z:0.#}) size({o.Data.SizeX:0.##},{o.Data.SizeY:0.##}) rgba({o.Data.R:0},{o.Data.G:0},{o.Data.B:0},{o.Data.A:0}) '{o.Text}'{label} {(o.Resource >= 0 && o.Resource < p.Resources.Count ? p.Resources[o.Resource] : "")}");
                foreach (var sc in o.Scripts)
                {
                    Console.WriteLine($"{ind}    S {N(sc.Id)} len {sc.Length} chain {(sc.Chain != 0 ? N(sc.Chain) : "-")} tracks {sc.Tracks.Count}"
                        + (sc.Events.Count > 0 ? " ev " + string.Join(",", sc.Events.Select(e => $"@{e.time}:{N(e.message)}->{e.target:X}")) : ""));
                    if (args.Length > 4 && args[4] == "v")
                        foreach (var t in sc.Tracks)
                            Console.WriteLine($"{ind}       off 0x{t.Offset:X} " + string.Join(" | ", t.Keys.Select(k => k.time + ":" + string.Join(",", k.value.Select(v => v.ToString("0.##"))))));
                }
                foreach (var mr in o.MessageResponses) Console.WriteLine($"{ind}    R {N(mr.Msg)}: " + string.Join("; ", mr.List.Select(c => Cmd(c, N))));
                Walk(o.Guid, depth + 1);
            }
        }
        Walk(0, 0);
        foreach (var mr in p.PackageResponses) Console.WriteLine($"PKG R {N(mr.Msg)}: " + string.Join("; ", mr.List.Select(c => Cmd(c, N))));
    }

    static string Cmd(FEResponse c, Func<uint, string> N) => c.Id switch
    {
        0 => $"script {N(c.Param)}",
        1 => $"post {N(c.Param)} to {(c.Target == 0xFFFFFFFC ? "pkg" : c.Target == 0xFFFFFFFF ? "all?" : "g" + c.Target.ToString("X"))}",
        2 => $"game {N(c.Param)}",
        3 => $"sound {N(c.Param)}",
        256 => $"setbutton g{c.Param:X}",
        257 => $"input {c.Param}",
        258 => "recordbutton",
        259 => $"recallbutton g{c.Param:X}",
        260 => "dontnavigate",
        512 => $"switch {c.Str}",
        513 or 514 or 516 => $"push {c.Str}",
        515 => "pop",
        768 => $"if script=={N(c.Param)}",
        769 => $"if script!={N(c.Param)}",
        1280 => "else",
        1281 => "endif",
        _ => $"cmd{c.Id} {c.Param:X} {c.Target:X} {c.Str}",
    };

    static StringTable Lang(string root)
    {
        var path = Path.Combine(root, "LANGUAGES", "English.bin");
        return StringTable.Read(GameFiles.ReadBundle(path));
    }
}
