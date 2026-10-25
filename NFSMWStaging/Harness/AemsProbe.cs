using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// aems <sub>: the AEMS module interpreter (Core/AemsVm.cs, AemsMixer.cs).
//   aems scan                         every bank: modules, interpreter support, node-call histogram
//   aems graph <bank> <class>         the node sequence of a class (call index, state offset)
//   aems run <bank> <class> <p0,p1,..> [ticks] [set:i=v@tick;...]   ticks the instance with a logging host
//   aems wav <bank> <class> <p0,..> <secs> <out.wav> [set:i=v@sec;...|ramp:i=a..b]  renders through AemsMixer
//   aems loops <bank>                 BNKl loop points vs sample counts
static class AemsProbe
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "scan";
        switch (sub)
        {
            case "scan": Scan(root); break;
            case "graph": Graph(root, args[3], args[4]); break;
            case "run": RunInst(root, args); break;
            case "wav": Wav(root, args); break;
            case "loops": Loops(root, args[3]); break;
            case "spchmask":
            {
                // SpeechScript.Choose on the presence-mask (flag 8) NIS_Select_* events vs the direct evaluation
                var sc = SpeechScript.Load(root, "NISAudio");
                foreach (var e in sc.Events) if ((e.Flags & 8) != 0) Console.WriteLine($"  flag8 event {e.Name}: spec {e.PresenceSpec:X8} masks [{string.Join(",", e.PresenceMask.Select(x => x.ToString("X")))}] sentences {e.Sentences.Length}");
                int same = 0, diff = 0, none = 0;
                foreach (var name in new[] { "GenericStart", "IntroNis01", "IntroNis02", "IntroNis05", "IntroNis10", "IntroNisDD", "IntroNisToll01", "OPMRivalIntro", "IntroNisBL01", "IntroNisBL06", "IntroNisBL06End", "IntroNisBL16", "ArrestM01", "ArrestF02b", "ArrestM23", "EndingNis01", "EndingNis04" })
                    foreach (bool bt in new[] { false, true })
                    {
                        var (ev, a) = NfsMw.Core.NisStreamSelect.Event(name, 3, bt);
                        if (ev == null) continue;
                        var ch = sc.Choose(ev, a, new Random(5), false);
                        var pk = NfsMw.Core.NisStreamSelect.Direct(sc, ev, a, 0);
                        string c1 = ch.Count > 0 ? $"bank {ch[0].BankId} take {ch[0].Take}" : "none", c2 = pk != null ? $"bank {pk.Value.BankId} take {pk.Value.Take}" : "none";
                        if (ch.Count == 0) none++; else if (pk != null && ch[0].BankId == pk.Value.BankId) same++; else diff++;
                        Console.WriteLine($"  {name}{(bt ? " [bt]" : "")}: {ev} choose {c1} | direct {c2}");
                    }
                Console.WriteLine($"same bank {same}, differ {diff}, choose none {none}");
                break;
            }
            case "mixobjs":
            {
                var mm = MixMap.Load(Path.Combine(root, "SOUND", "MIXMAPS", "MAPOUTPUT.mxb"));
                var counts = new int[MixMap.NumStates];
                for (int i = 0; i < counts.Length; i++) counts[i] = mm.HasState(i) ? 1 : 0;
                mm.Build(counts);
                int st2 = int.Parse(args[3]);
                Console.WriteLine("3D inputs: " + string.Join(", ", mm.D3Inputs(st2).Select(x => x.ToString("X8"))));
                for (int o = 0; o < 32; o++)
                {
                    var ds = mm.Describe(MixMap.ObjectId(false, st2, 0, o)).ToList();
                    if (ds.Count > 0) Console.WriteLine($"   obj {o}: " + string.Join(", ", ds.Select(x => $"s{x.slot}/t{x.type}")));
                }
                break;
            }
        }
    }

    static string P(string root, string rel) => Path.Combine(root, "SOUND", rel.Replace('/', Path.DirectorySeparatorChar));

    static void Scan(string root)
    {
        int banks = 0, mods = 0, ok = 0;
        var errs = new Dictionary<string, int>();
        foreach (var f in Directory.GetFiles(Path.Combine(root, "SOUND"), "*.abk", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var b = AemsBank.Load(f);
            if (b == null) continue;
            banks++;
            var rel = f.Substring(Path.Combine(root, "SOUND").Length + 1);
            bool brief = rel.StartsWith("ENGINE") && !rel.Contains("CAR_00_") && !rel.Contains("TRANNY") && !rel.Contains("WHINE_00") && !rel.Contains("SWTN_CAR_00");
            foreach (var m in b.Modules)
            {
                mods++;
                if (m.Supported) ok++;
                else { errs.TryGetValue(m.Error ?? "?", out int c); errs[m.Error ?? "?"] = c + 1; }
                if (!brief)
                    Console.WriteLine($"{rel,-34} {m.Name,-22} max{m.MaxInstances} pl{m.NumPlayers} cc{m.NumControllers} g{m.NumGlobals} f{m.NumFunctions} ops{m.Ops.Length} {(m.Supported ? "ok" : "FAIL " + m.Error)}");
            }
        }
        Console.WriteLine($"{banks} banks, {mods} modules, {ok} interpretable");
        foreach (var e in errs) Console.WriteLine($"  {e.Value} x {e.Key}");
    }

    static AemsModule Mod(string root, string bank, string cls, out AemsBank b)
    {
        b = AemsBank.Load(P(root, bank));
        if (b == null) { Console.WriteLine("no bank " + bank); return null; }
        var m = b.Find(cls);
        if (m == null) Console.WriteLine($"no class {cls}: " + string.Join(", ", b.Modules.Select(x => x.Name)));
        return m;
    }

    static readonly string[] NodeNames = {
        "ClassDtor","ClassData","Global","create","destroy","CallFunc","counter","random","shuffle","weighted","rangetrig","delaytrig",
        "stategen","merge","envelope","table","delayline","mux","demux","min","max","scale","add","sub","mul","div","mod","PLAYER",
        "osc","ramp","addmax","submin","mulmax","min2","max2","scale2","add2","Function","ControlClass","SetGlobal" };

    static void Graph(string root, string bank, string cls)
    {
        var m = Mod(root, bank, cls, out var b);
        if (m == null) return;
        Console.WriteLine($"{cls}: data {m.Data:X} size {m.DataSize:X} destroy {m.DestroyOffset:X} players [{string.Join(",", m.PlayerOffsets.Select(x => x.ToString("X")))}]");
        int esi = 0x18, i = 0;
        foreach (var op in m.Ops)
        {
            string s = op.Kind.ToString();
            switch (op.Kind)
            {
                case AemsModule.K.Call: s = $"call {NodeNames[op.Target]}({op.Target}) @{esi:X}"; break;
                case AemsModule.K.AddEsi: esi += op.Disp; s = $"esi += {op.Disp:X} -> {esi:X}"; break;
                case AemsModule.K.St: s = $"[{esi + op.Disp:X}] = r{op.R}"; break;
                case AemsModule.K.Ld: s = $"r{op.R} = [{esi + op.Disp:X}]"; break;
                case AemsModule.K.AddM: case AemsModule.K.SubM: case AemsModule.K.ImulM: case AemsModule.K.CmpM: s = $"{op.Kind} r{op.R}, [{esi + op.Disp:X}]"; break;
                case AemsModule.K.Jcc: s = $"j{op.Cond:X} -> {op.Target}"; break;
                case AemsModule.K.Push: continue;
                case AemsModule.K.Nop: continue;
            }
            Console.WriteLine($"  {i++,4} {s}");
        }
        // sample groups the players use
        var inst = AemsInstance.Create(m, new LogHost(), new int[0]);
        foreach (int po in m.PlayerOffsets)
        {
            int g = BitConverter.ToInt32(inst.Mem, po + 4);
            int n = g > 0 ? BitConverter.ToInt32(b.D, g) : 0;
            var slots = Enumerable.Range(0, Math.Max(0, Math.Min(n, 64))).Select(k => BitConverter.ToInt32(b.D, g + 8 + k * 12));
            int ni = inst.Mem[po + 0xE];
            var ins = Enumerable.Range(0, ni).Select(k => inst.Mem[po + 0x1C + k * 12]);
            Console.WriteLine($"  player @{po:X}: group {n} [{string.Join(",", slots)}] inputs [{string.Join(",", ins)}] outputs {inst.Mem[po + 0xF]}");
        }
        inst.Kill();
    }

    sealed class LogHost : IAemsHost
    {
        public int T;
        int _n = 1;
        readonly Dictionary<int, int> _left = new Dictionary<int, int>();
        public bool Quiet;
        public int Play(AemsBank bank, int patnum, AemsPlayOpts o)
        {
            int h = _n++;
            var s = AemsMixer.SoundsOf(bank);
            s.TryGetValue(patnum, out var bs);
            _left[h] = bs == null ? 0 : bs.Loops ? int.MaxValue : (int)(bs.Samples * 1000L / Math.Max(1, bs.SampleRate) / 20);
            if (!Quiet) Console.WriteLine($"  t{T} PLAY h{h} slot {patnum} pitch {o.PitchMult} vol {o.Vol} az {o.Azimuth} lp {o.LowPass} dry {o.Dry} {(bs == null ? "EMPTY" : bs.Loops ? "loop" : $"{bs.Samples * 1000 / Math.Max(1, bs.SampleRate)}ms")}");
            return h;
        }
        public void Stop(int h) { if (!Quiet) Console.WriteLine($"  t{T} STOP h{h}"); _left.Remove(h); }
        public void Pause(int h) { if (!Quiet) Console.WriteLine($"  t{T} PAUSE h{h}"); }
        public bool Alive(int h) => _left.TryGetValue(h, out int l) && l > 0;
        public void Set(int h, int type, int v) { if (!Quiet) Console.WriteLine($"  t{T} SET h{h} {type}={v}"); }
        public void Position(int h, out int frame, out int frames, out int rate, out bool sustain) { frame = 0; frames = 0; rate = 22050; sustain = false; }
        public int ControlClass(string cls, int id, int op, int[] p) { Console.WriteLine($"  t{T} CTL {cls} id{id} op{op} [{(p == null ? "" : string.Join(",", p))}]"); return op == 0 ? 7 : id; }
        public void Advance() { foreach (var k in _left.Keys.ToList()) if (_left[k] != int.MaxValue) _left[k]--; }
    }

    static int[] Params(string s) => s == "-" ? new int[0] : s.Split(',').Select(x => x.StartsWith("0x") ? Convert.ToInt32(x.Substring(2), 16) : int.Parse(x)).ToArray();

    // "i=v@t;i=v@t" (t in ticks for run, seconds for wav), or "ramp:i=a..b"
    static List<(int i, int v, float t)> Sets(string s)
    {
        var r = new List<(int, int, float)>();
        if (string.IsNullOrEmpty(s) || !s.StartsWith("set:")) return r;
        foreach (var part in s.Substring(4).Split(';'))
        {
            var a = part.Split('=', '@');
            r.Add((int.Parse(a[0]), int.Parse(a[1]), float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)));
        }
        return r;
    }

    static void RunInst(string root, string[] args)
    {
        var m = Mod(root, args[3], args[4], out var b);
        if (m == null) return;
        var host = new LogHost();
        var inst = AemsInstance.Create(m, host, Params(args[5]));
        if (inst == null) { Console.WriteLine("create failed: " + m.Error); return; }
        int ticks = args.Length > 6 ? int.Parse(args[6]) : 50;
        var sets = Sets(args.Length > 7 ? args[7] : null);
        bool trace = args.Contains("trace");
        if (trace) inst.Trace = s => Console.WriteLine("    " + s);
        for (int t = 0; t < ticks && inst.Alive; t++)
        {
            host.T = t;
            foreach (var (i, v, at) in sets)
            {
                if ((int)at != t) continue;
                if (i < 0) inst.Release(); else inst.SetParam(i, v);
                Console.WriteLine($"  t{t} param {i} = {v}");
            }
            inst.Tick();
            host.Advance();
            if (t % 10 == 0 || t == ticks - 1) Console.WriteLine($"  t{t}: {inst.Describe()}");
        }
        Console.WriteLine(inst.Alive ? "alive" : "destroyed");
    }

    static void Wav(string root, string[] args)
    {
        var m = Mod(root, args[3], args[4], out var b);
        if (m == null) return;
        var mixer = new AemsMixer();
        var p = Params(args[5]);
        var inst = AemsInstance.Create(m, mixer, p);
        float secs = float.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
        string outPath = args[7];
        string spec = args.Length > 8 ? args[8] : null;
        var sets = Sets(spec);
        int ri = -1, ra = 0, rb = 0;
        if (spec != null && spec.StartsWith("ramp:"))
        {
            var a = spec.Substring(5).Split('=');
            ri = int.Parse(a[0]);
            var ab = a[1].Split("..");
            ra = int.Parse(ab[0]); rb = int.Parse(ab[1]);
        }
        const int rate = 44100;
        int total = (int)(secs * rate), blk = rate / 50;   // one AEMS tick (20 ms) per block
        var outBuf = new float[total * 2];
        var buf = new float[blk * 2];
        for (int f = 0, t = 0; f < total; f += blk, t++)
        {
            float time = f / (float)rate;
            foreach (var (i, v, at) in sets) if (Math.Abs(at - time) < 0.0101f) { if (i < 0) inst.Release(); else inst.SetParam(i, v); }
            if (ri >= 0) inst.SetParam(ri, (int)(ra + (rb - ra) * (time / secs)));
            inst.Tick();
            Array.Clear(buf, 0, buf.Length);
            mixer.Render(buf, 2, rate);
            Array.Copy(buf, 0, outBuf, f * 2, Math.Min(buf.Length, outBuf.Length - f * 2));
            if (t % 25 == 0) Console.WriteLine($"  {time:F2}s voices {mixer.Live} {inst.Describe()}");
        }
        float peak = outBuf.Select(Math.Abs).DefaultIfEmpty(0).Max();
        using (var w = new BinaryWriter(File.Create(outPath)))
        {
            w.Write("RIFF".ToCharArray()); w.Write(36 + total * 4); w.Write("WAVEfmt ".ToCharArray());
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(total * 4);
            foreach (var x in outBuf) w.Write((short)Math.Max(-32768, Math.Min(32767, x * 32767)));
        }
        Console.WriteLine($"wrote {outPath}, peak {peak:F3}");
    }

    static void Loops(string root, string bank)
    {
        var b = AemsBank.Load(P(root, bank));
        foreach (var kv in AemsMixer.SoundsOf(b).OrderBy(k => k.Key))
            Console.WriteLine($"  slot {kv.Key}: {kv.Value.Samples} samples @{kv.Value.SampleRate} ch{kv.Value.Channels} loop {kv.Value.LoopStart}..{kv.Value.LoopEnd}");
    }
}
