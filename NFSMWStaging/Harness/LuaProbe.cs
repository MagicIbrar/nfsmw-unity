using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;

// `lua scan`: what the stategraph bytecode references (globals, binding names and argument counts, message fields).
// `lua run`: loads every chunk into the C# Lua VM and runs every handler (see LuaRunProbe).
static class LuaProbe
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "run";
        if (sub == "selftest") { LuaSelfTest.Run(); return; }
        if (sub == "drive") { LuaDriveProbe.Run(root, args); return; }
        var gs = GameplayScripts.Load(root);
        if (sub == "scan") Scan(gs, args);
        else if (sub == "entries")
        {
            var c = gs.Get(args[3]);
            var names = new Dictionary<uint, string>();
            foreach (var n in new[] { "traffic1", "trigger1", "spawn", "finish_marker", "start_marker", "call_from_mia", "kill_engine", "Children", "racestart" }) names[AttribDatabase.Hash(n)] = n;
            Console.WriteLine($"hash Children {AttribDatabase.Hash("Children"):X8}");
            foreach (var k in c.Keys("Children")) { var ch = gs.Get(k); Console.WriteLine($"  child {k:X8} {ch?.Name} parent {ch?.Parent?.Name}"); }
            for (var x = c; x != null; x = x.Parent)
            {
                Console.WriteLine($"== {x.Name}");
                foreach (var kv in x.Entries)
                {
                    bool cls = x.Class != null && x.Class.Fields.ContainsKey(kv.Key);
                    uint v = kv.Value + 4 <= x.Data.Length ? Chunks.U32(x.Data, kv.Value) : 0;
                    var t = gs.Get(v);
                    Console.WriteLine($"  {kv.Key:X8} {(names.TryGetValue(kv.Key, out var nm) ? nm : "")} {(cls ? "class" : "extra")} -> {v:X8} {t?.Name}");
                }
            }
        }
        else if (sub == "chain")
        {
            // parent chains of every distinct template used by children, with whether they carry trigger geometry
            var seen = new HashSet<string>();
            foreach (var c in gs.All)
            {
                if (c.Parent == null || c.Bool("Template")) continue;
                string chain = string.Join(" < ", Enumerable.Repeat(c, 1).Concat(Ancestors(c)).Skip(1).Select(x => x.Name));
                if (!seen.Add(chain)) continue;
                Console.WriteLine($"{chain}   dims={c.Has("Dimensions")} radius={c.Has("Radius")} pos={c.Has("Position")} autostart={c.Bool("AutoStart")}");
            }
            foreach (var c in gs.All.Where(x => x.Bool("AutoStart") && !x.Bool("Template"))) Console.WriteLine("autostart: " + c.Name);
        }
        else if (sub == "dis") { foreach (var h in gs.Handlers.Where(x => x.Name == args[3])) Dis(h.Proto, ""); }
        else if (sub == "order") HandlerOrder(gs, root);
        else if (sub == "ice")
        {
            // Camera.SetGenericCamera('Cinematics', 'Challenge') = ECinematicMoment: the GENERIC ICE group's tracks
            var ice = IceCameras.Load(root);
            var tracks = ice.Find(3, args.Length > 3 ? args[3] : "Cinematics") ?? new List<IceCameras.Track>();
            foreach (var t in tracks) Console.WriteLine($"  {t.Name,-24} start {t.Start:0.00} length {t.Length:0.00} s keys {t.Keys.Count}");
        }
        else if (sub == "persist")
        {
            // GActivity::SaveState 0x5F6BA0 / LoadState 0x5F9480: `Persistent` activities keep their state in the save game
            foreach (var c in gs.All.Where(c => c.Bool("Persistent"))) Console.WriteLine($"Persistent: {c.Name} template={c.Bool("Template")} autostart={c.Bool("AutoStart")} freeroamonly={c.Bool("FreeRoamOnly")}");
            foreach (var c in gs.All.Where(c => c.Bool("AutoStart") && !c.Bool("Template"))) Console.WriteLine($"AutoStart: {c.Name} persistent={c.Bool("Persistent")} freeroamonly={c.Bool("FreeRoamOnly")}");
        }
        else LuaRunProbe.Run(root, gs, args);
    }

    static void Scan(GameplayScripts gs, string[] args)
    {
        Console.WriteLine($"handlers {gs.Handlers.Count}, states {gs.States.Count}, load errors {gs.Handlers.Count(h => h.LoadError != null)}");
        foreach (var h in gs.Handlers.Where(h => h.LoadError != null)) Console.WriteLine($"  ERR {h.Name}: {h.LoadError}");
        var ops = new long[35];
        var globals = new SortedDictionary<string, int>();
        var calls = new SortedDictionary<string, SortedSet<string>>();
        var msgFields = new SortedDictionary<string, SortedSet<string>>();
        var selfFields = new SortedSet<string>();
        var stateFields = new SortedSet<string>();
        var defined = new List<string>();
        int filters = 0;
        var msgNames = new SortedDictionary<uint, SortedSet<string>>();
        foreach (var h in gs.Handlers)
        {
            if (h.Proto == null) continue;
            string suffix = h.Name.Substring(h.Name.IndexOf("_handler_", StringComparison.Ordinal) + 9);
            while (suffix.Length > 2 && char.IsDigit(suffix[suffix.Length - 1])) suffix = suffix.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd('_');
            if (!msgNames.TryGetValue(h.MessageId, out var mn)) msgNames[h.MessageId] = mn = new SortedSet<string>();
            mn.Add(suffix);
            if (h.Filters.Count > 0)
            {
                filters++;
                Console.WriteLine($"  filters {h.Name} passAll={h.FilterPassAll}: " + string.Join(" | ", h.Filters.Select(f => BitConverter.ToString(f))));
            }
            // main chunk: what global does it define?
            foreach (uint ins in h.Proto.Code)
                if ((ins & 63) == 7) defined.Add((string)h.Proto.K[(ins >> 6) & 0x3FFFF].Str);
            foreach (var f in Walk(h.Proto))
            {
                var reg = new Dictionary<int, string>();
                for (int pc = 0; pc < f.Code.Length; pc++)
                {
                    uint i = f.Code[pc];
                    int op = (int)(i & 63), a = (int)(i >> 24), c = (int)((i >> 6) & 0x1FF), b = (int)((i >> 15) & 0x1FF), bx = (int)((i >> 6) & 0x3FFFF);
                    ops[op]++;
                    string K(int x) => x >= 250 && f.K[x - 250].IsString ? f.K[x - 250].Str : null;
                    switch (op)
                    {
                        case 5:
                            {
                                string g = f.K[bx].Str;
                                globals[g] = globals.TryGetValue(g, out int n) ? n + 1 : 1;
                                reg[a] = g;
                                break;
                            }
                        case 6:
                        case 11:
                            {
                                string key = K(c);
                                string src = reg.TryGetValue(b, out var s) ? s : null;
                                string r = null;
                                if (key != null && src != null) r = src + (op == 11 ? ":" : ".") + key;
                                if (key != null && f == h.Proto.P.FirstOrDefault() && src == null)
                                {
                                    if (b == 0) selfFields.Add(key);
                                    if (b == 1) { if (!msgFields.TryGetValue(suffix, out var set)) msgFields[suffix] = set = new SortedSet<string>(); set.Add(key); }
                                    if (b == 2) stateFields.Add(key);
                                }
                                reg.Remove(a);
                                if (op == 11) reg.Remove(a + 1);
                                if (r != null) reg[a] = r;
                                break;
                            }
                        case 0:
                            if (reg.TryGetValue(b, out var mv)) reg[a] = mv; else reg.Remove(a);
                            break;
                        case 25:
                        case 26:
                            {
                                if (reg.TryGetValue(a, out var fn))
                                {
                                    bool self = fn.Contains(":");
                                    string argc = b == 0 ? "var" : (b - 1 - (self ? 1 : 0)).ToString();
                                    if (!calls.TryGetValue(fn, out var set)) calls[fn] = set = new SortedSet<string>();
                                    set.Add(argc + (c == 1 ? "" : "->r"));
                                }
                                for (int x = a; x < a + 3; x++) reg.Remove(x);
                                break;
                            }
                        default:
                            reg.Remove(a);
                            break;
                    }
                }
            }
        }
        Console.WriteLine($"handlers with filters: {filters}");
        int bad = defined.Count(d => !gs.Handlers.Any(h => h.Name == d));
        Console.WriteLine($"globals defined by main chunks: {defined.Count}, not matching a handler name: {bad}");
        Console.WriteLine("opcodes: " + string.Join(" ", Enumerable.Range(0, 35).Where(o => ops[o] > 0).Select(o => $"{LuaState.OpNames[o]}={ops[o]}")));
        Console.WriteLine("globals read: " + string.Join(" ", globals.Select(kv => $"{kv.Key}({kv.Value})")));
        Console.WriteLine("calls:");
        foreach (var kv in calls) Console.WriteLine($"  {kv.Key}  args {string.Join(",", kv.Value)}");
        Console.WriteLine("message ids:");
        foreach (var kv in msgNames) Console.WriteLine($"  {kv.Key:X8} {string.Join(",", kv.Value)}");
        Console.WriteLine("r1 (message) fields by handler message:");
        foreach (var kv in msgFields) Console.WriteLine($"  {kv.Key}: {string.Join(" ", kv.Value)}");
        Console.WriteLine("r0 (activity) fields: " + string.Join(" ", selfFields));
        Console.WriteLine("r2 (state table) fields: " + string.Join(" ", stateFields));
        var cls = gs.Db.Classes[AttribDatabase.Hash("gameplay")];
        foreach (var f in selfFields)
        {
            uint hk = AttribDatabase.Hash(f);
            if (cls.Fields.TryGetValue(hk, out var fd)) Console.WriteLine($"  field {f}: type {(gs.Db.Names.TryGetValue(fd.Type, out var tn) ? tn : fd.Type.ToString("X8"))} flags {fd.Flags} count {fd.Count}");
            else Console.WriteLine($"  field {f}: NOT A CLASS FIELD (hash {hk:X8})");
        }
        if (args.Length > 3)
        {
            var actName = args[3];
            var act = gs.Get(actName);
            Console.WriteLine($"activity {actName}: {(act == null ? "missing" : act.Name)}");
            if (act != null)
                foreach (var st in gs.StatesOf(act))
                    Console.WriteLine($"  state {st.Name} ({st.Record.Name}): {string.Join(", ", gs.HandlersFor(st, act).Select(h => h.Name))}");
        }
    }

    /// <summary>A plain Lua 5.0 listing (luac -l style).</summary>
    public static void Dis(LuaProto f, string ind)
    {
        Console.WriteLine($"{ind}function <{f.Source}> params {f.NumParams} ups {f.NumUpvalues} stack {f.MaxStackSize} vararg {f.IsVararg}");
        string K(int x) => x >= 250 ? (f.K[x - 250].IsString ? "\"" + f.K[x - 250].Str + "\"" : f.K[x - 250].ToString()) : "r" + x;
        for (int pc = 0; pc < f.Code.Length; pc++)
        {
            uint i = f.Code[pc];
            int op = (int)(i & 63), a = (int)(i >> 24), c = (int)((i >> 6) & 0x1FF), b = (int)((i >> 15) & 0x1FF), bx = (int)((i >> 6) & 0x3FFFF), sbx = bx - 131071;
            string o = LuaState.OpNames[op];
            string t;
            switch (o)
            {
                case "LOADK": case "GETGLOBAL": case "SETGLOBAL": t = $"r{a} {K(bx + 250)}"; break;
                case "JMP": t = $"-> {pc + 1 + sbx}"; break;
                case "FORLOOP": case "TFORPREP": t = $"r{a} -> {pc + 1 + sbx}"; break;
                case "CLOSURE": t = $"r{a} proto{bx}"; break;
                case "GETTABLE": case "SELF": t = $"r{a} r{b} {K(c)}"; break;
                case "SETTABLE": case "ADD": case "SUB": case "MUL": case "DIV": case "POW": case "EQ": case "LT": case "LE": t = $"{a} {K(b)} {K(c)}"; break;
                default: t = $"{a} {b} {c}"; break;
            }
            Console.WriteLine($"{ind}  {pc,3} {o,-9} {t}");
        }
        for (int i = 0; i < f.P.Length; i++) Dis(f.P[i], ind + "  ");
    }

    static IEnumerable<AttribCollection> Ancestors(AttribCollection c) { for (var x = c.Parent; x != null; x = x.Parent) yield return x; }

    /// <summary>
    /// `lua order`: the handlers that share a state of one activity, with the VPAK vault each lives in. The exe creates the
    /// GHandlers per vault in export order (GObjectBlock::CreateObjects_GHandler_ 0x5F55D0 over FUN_005F4090) and links them
    /// at the tail of the ring list (GRuntimeInstance ctor 0x5E8130), the resident gpcore vault first (InitializeVaults),
    /// transient vaults as they load; FindInstances_GHandler_ walks that ring.
    /// </summary>
    static void HandlerOrder(GameplayScripts gs, string root)
    {
        var d = File.ReadAllBytes(Path.Combine(root, "GLOBAL", "gameplay.bin"));
        int count = Chunks.I32(d, 4), names = Chunks.I32(d, 8);
        var vaults = new List<(string name, int lo, int hi)>();
        for (int i = 0; i < count; i++)
        {
            int e = 16 + i * 20;
            string n = Chunks.CString(d, names + Chunks.I32(d, e), 64);
            int binSize = Chunks.I32(d, e + 4), vltSize = Chunks.I32(d, e + 8), binOff = Chunks.I32(d, e + 12), vltOff = Chunks.I32(d, e + 16);
            vaults.Add((n + "(bin)", binOff, binOff + binSize));
            vaults.Add((n + "(vlt)", vltOff, vltOff + vltSize));
        }
        Console.WriteLine($"{count} vaults: " + string.Join(" ", vaults.Where((v, i) => i % 2 == 0).Select(v => v.name.Replace("(bin)", ""))));
        string VaultOf(AttribCollection c)
        {
            int at = c.Entries.Count > 0 ? c.Entries.Values.Min() : c.Layout;
            var v = vaults.FirstOrDefault(x => at >= x.lo && at < x.hi);
            return v.name ?? "?";
        }
        foreach (var c in gs.All.Where(c => !c.Bool("Template") && (c.Bool("AutoStart") || (c.Name ?? "").StartsWith("race_bin_") && !(c.Name ?? "").Contains("/"))))
            Console.WriteLine($"  activity {c.Name,-48} vault {VaultOf(c)} autostart={c.Bool("AutoStart")} freeroamonly={c.Bool("FreeRoamOnly")} persistent={c.Bool("Persistent")}");
        int shared = 0, mixed = 0, inverted = 0;
        foreach (var act in gs.All.Where(c => !c.Bool("Template")))
        {
            var states = gs.StatesOf(act);
            if (states.Count == 0) continue;
            foreach (var st in states)
            {
                var hs = gs.HandlersFor(st, act);
                foreach (var grp in hs.GroupBy(h => h.MessageId).Where(g => g.Count() > 1))
                {
                    shared++;
                    var list = grp.ToList();
                    var vs = list.Select(h => VaultOf(h.Record)).ToList();
                    if (vs.Distinct().Count() > 1) mixed++;
                    // template handlers (owner = an ancestor) before the instance's own?
                    bool instFirst = list.FindIndex(h => h.OwnerKey == act.Key) >= 0 && list.FindIndex(h => h.OwnerKey == act.Key) < list.FindLastIndex(h => h.OwnerKey != act.Key);
                    if (instFirst) inverted++;
                    if (instFirst || vs.Distinct().Count() > 1 || args_verbose)
                        Console.WriteLine($"  {act.Name} [{st.Name}] {StategraphRuntime.MessageName(grp.Key)}: " + string.Join(", ", list.Select((h, i) => $"{h.Name}<{gs.Get(h.OwnerKey)?.Name}>@{vs[i]}")));
                }
            }
        }
        Console.WriteLine($"{shared} (activity, state, message) groups with several handlers; {mixed} span vaults; {inverted} have an instance handler before a template one");
    }
    static bool args_verbose = Environment.GetCommandLineArgs().Contains("verbose");

    public static IEnumerable<LuaProto> Walk(LuaProto p)
    {
        yield return p;
        foreach (var c in p.P) foreach (var x in Walk(c)) yield return x;
    }
}
