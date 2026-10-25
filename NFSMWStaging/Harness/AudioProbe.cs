using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// audio <sub> ...: checks for the audio behaviour port.
//   audio attr <car>          engineaudio / shiftpattern / turbosfx / acceltrans values for a car
//   audio mix [file]          builds the mix map and prints player-car outputs for a few input sets
//   audio ginsu <car> [out]   runs the Ginsu synth over an rpm ramp; reports pitch tracking, optional wav
static class AudioProbe
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "mix";
        switch (sub)
        {
            case "attr": Attr(root, args[3]); break;
            case "mix": Mix(root, args.Length > 3 ? args[3] : "MAPOUTPUT.mxb"); break;
            case "ginsu": GinsuRamp(root, args[3], args.Length > 4 ? args[4] : null); break;
            case "pitch": Pitch(root, args[3]); break;
            case "keys": Keys(root, args[3], args.Skip(4).ToArray()); break;
        }
    }

    static AttribDatabase Db(string root) => AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));

    static AttribCollection Ref(AttribDatabase db, AttribCollection c, string field, int index = 0)
    {
        var r = c.Refs(field);
        if (r.Length <= index) return null;
        return db.Collections.TryGetValue(r[index], out var x) ? x : null;
    }

    static void Dump(AttribDatabase db, AttribCollection c, string title)
    {
        Console.WriteLine($"== {title}: {c?.Name}");
        if (c?.Class == null) return;
        foreach (var f in c.Class.Fields.Values)
        {
            string name = db.Names.TryGetValue(f.Key, out var n) ? n : f.Key.ToString("X8");
            if (!c.TryGetKey(f.Key, out int at, out int count, out var def)) continue;
            var d = c.Source.Data;
            var vals = new List<string>();
            int words = Math.Max(1, def.Size / 4);
            for (int i = 0; i < Math.Min(count, 8); i++)
            {
                var w = new List<string>();
                for (int k = 0; k < Math.Min(words, 16); k++)
                {
                    int o = at + i * def.Size + k * 4;
                    if (o + 4 > d.Length) break;
                    float fv = Chunks.F32(d, o);
                    int iv = Chunks.I32(d, o);
                    w.Add(Math.Abs(fv) > 1e-6 && Math.Abs(fv) < 1e7 ? fv.ToString("0.###") : iv.ToString());
                }
                vals.Add(string.Join(",", w));
            }
            Console.WriteLine($"  {name} [{count}{(def.IsArray ? " arr" : "")} size {def.Size}] = {string.Join(" | ", vals)}");
        }
    }

    static void Attr(string root, string car)
    {
        var db = Db(root);
        var pv = db.Get("pvehicle", car.ToLowerInvariant());
        Console.WriteLine($"{car}: engine_upgrades {pv.Int("engine_upgrades")} transmission_upgrades {pv.Int("transmission_upgrades")} induction_upgrades {pv.Int("induction_upgrades")}");
        for (int i = 0; i < pv.Refs("engineaudio").Length; i++) Dump(db, Ref(db, pv, "engineaudio", i), "engineaudio " + i);
        var ea = Ref(db, pv, "engineaudio");
        if (ea != null) Dump(db, Ref(db, ea, "acceltrans"), "acceltrans");
        foreach (var fld in new[] { "ShiftSND", "TurboSND" })
        {
            if (!pv.TryGet(fld, out int at, out int count, out var def)) { Console.WriteLine(fld + ": none"); continue; }
            for (int i = 0; i < count; i++)
            {
                var d = pv.Source.Data;
                uint cls = Chunks.U32(d, at + i * def.Size), key = Chunks.U32(d, at + i * def.Size + 4);
                int level = d[at + i * def.Size + 12];
                db.Collections.TryGetValue((cls, key), out var c);
                Dump(db, c, $"{fld}[{i}] level {level}");
            }
        }
    }

    static void Mix(string root, string file)
    {
        var map = MixMap.Load(Path.Combine(root, "SOUND", "MIXMAPS", file));
        var counts = new int[MixMap.NumStates];
        counts[MixMap.Main] = 1; counts[MixMap.Music] = 1; counts[MixMap.PlayerCar] = 1; counts[MixMap.AiRaceCar] = 2; counts[MixMap.CopCar] = 2;
        map.Build(counts);
        // live objects: player car engine (2), shift (3), turbo (4), nitrous (5), skids (7), road (8), wind (9)
        foreach (int o in new[] { 2, 3, 4, 5, 6, 7, 8, 9, 13 }) map.Attach(MixMap.ObjectId(false, MixMap.PlayerCar, 0, o), true);
        foreach (int o in new[] { 1, 2, 4, 5, 6, 7 }) map.Attach(MixMap.ObjectId(false, MixMap.Main, 0, o), true);
        var phys = map.Inputs(MixMap.ObjectId(true, MixMap.PlayerCar, 0, 0));
        var pos = map.Inputs(MixMap.ObjectId(true, MixMap.PlayerCar, 0, 7));
        var hyb = map.Inputs(MixMap.ObjectId(true, MixMap.PlayerCar, 0, 5));
        var eng = map.Inputs(MixMap.ObjectId(true, MixMap.PlayerCar, 0, 4));
        var mv = map.Inputs(MixMap.ObjectId(true, MixMap.Main, 0, 0));
        foreach (var p in new[] { 8, 11, 12, 13, 14 }) map.Inputs(MixMap.ObjectId(true, MixMap.PlayerCar, 0, p))[15] = 1;
        pos[15] = 1;
        for (int k = 0; k < 6; k++) mv[k] = 0;   // master volumes: 0 = no attenuation (1 - vol)
        foreach (var (mph, rpm, nis) in new[] { (0f, 1000f, false), (60f, 6000f, false), (120f, 9000f, false), (60f, 6000f, true) })
        {
            phys[0] = Math.Min(0x7FFF, (int)(mph * 1092.2334f));
            phys[1] = Math.Min(0x7FFF, (int)(mph * 546.1167f));
            phys[2] = Math.Min(0x7FFF, (int)(mph * 327.67f));
            phys[3] = Math.Min(0x7FFF, (int)(mph * 234.05f));
            phys[4] = Math.Max(0, Math.Min(0x7FFF, (int)((10000 - rpm) * 3.6407778f)));
            phys[5] = 0x7FFF;
            phys[10] = 0x7FFF;
            pos[0] = 150; pos[1] = 600; pos[2] = 0x8000; pos[3] = 0x8000;
            eng[2] = 32767;
            var gs = map.Inputs(MixMap.ObjectId(true, MixMap.Main, 0, 1));
            for (int b = 0; b < 14; b++) gs[b] = 0;
            if (nis) for (int b = 0; b < 14; b++) if ((0x479 & (1 << b)) != 0) gs[b] = 0x7FFF;   // SNDSTATE_NIS_STORY -> g_CtlStateActions[6] = 0x479
            for (int f = 0; f < 120; f++) map.Process(1 / 60f, 0);
            Console.WriteLine($"-- {mph} mph, rpm {rpm}{(nis ? ", NIS" : "")}");
            foreach (int o in new[] { 2, 3, 4, 5, 7, 8, 9 })
            {
                var outs = map.Outputs(MixMap.ObjectId(false, MixMap.PlayerCar, 0, o));
                if (outs == null) { Console.WriteLine($"  obj {o}: no outputs"); continue; }
                var s = new List<string>();
                for (int i = 0; i < 16; i++) s.Add(MixMap.Output(outs, i, MixMap.Azim).ToString());
                Console.WriteLine($"  obj {o}: {string.Join(" ", s)}");
            }
            var spch = map.Outputs(MixMap.ObjectId(false, MixMap.Main, 0, 1));
            if (spch != null) Console.WriteLine($"  speech: {string.Join(" ", Enumerable.Range(0, 8).Select(i => MixMap.Output(spch, i, MixMap.Azim)))}");
        }
    }

    // StringKey / values of named fields in every collection of a class
    static void Keys(string root, string cls, string[] fields)
    {
        var db = Db(root);
        foreach (var c in db.OfClass(cls))
            Console.WriteLine($"{c.Name}: " + string.Join(" ", fields.Select(f => f + "=" + (c.StringKey(f) ?? (c.Has(f) ? c.Int(f).ToString() : "-")))));
    }

    // fundamental of each bank sample by normalised autocorrelation over 30..1500 Hz (rate 22050 when unset)
    static void Pitch(string root, string rel)
    {
        var bank = SoundBank.Load(Path.Combine(root, "SOUND", rel));
        foreach (var s in bank.Sounds)
        {
            var x = s.Data;
            int rate = s.SampleRate, n = Math.Min(x.Length, rate);
            int a0 = Math.Min(x.Length - n, x.Length / 3);
            double best = 0; int lagBest = 0;
            for (int lag = rate / 1500; lag <= rate / 30 && lag < n / 2; lag++)
            {
                double c = 0, e1 = 0, e2 = 0;
                for (int i = 0; i < n - lag; i += 2) { c += x[a0 + i] * x[a0 + i + lag]; e1 += x[a0 + i] * x[a0 + i]; e2 += x[a0 + i + lag] * x[a0 + i + lag]; }
                double r = c / Math.Sqrt(e1 * e2 + 1e-12);
                if (r > best + 0.02) { best = r; lagBest = lag; }
            }
            double rms = Math.Sqrt(x.Average(v => (double)v * v));
            Console.WriteLine($"  #{s.Index} {s.Samples} @ {rate} loop {s.LoopStart}-{s.LoopEnd} f0 {(lagBest > 0 ? rate / (double)lagBest : 0):0.0} Hz (r {best:0.00}) rms {rms:0.000}");
        }
    }

    static void GinsuRamp(string root, string car, string outWav)
    {
        var db = Db(root);
        var pv = db.Get("pvehicle", car.ToLowerInvariant());
        var ea = Ref(db, pv, "engineaudio");
        string dir = Path.Combine(root, "SOUND", "ENGINE");
        var acc = GinsuSynthData.Load(Path.Combine(dir, ea.StringKey("Filename_GinsuAccel")));
        Console.WriteLine($"{car}: accel {ea.StringKey("Filename_GinsuAccel")} {acc.MinFrequency:0}..{acc.MaxFrequency:0} @ {acc.SampleRate} Hz, attrib rpm {ea.Float("MinRPM")}..{ea.Float("MaxRPM")}");
        var syn = new GinsuSynthesis();
        syn.SetSynthData(acc);
        syn.StartSynthesis(acc.MinFrequency);
        int rate = acc.SampleRate, total = rate * 6;
        var all = new float[total];
        var buf = new float[rate / 60];
        int o = 0;
        for (int frame = 0; o < total; frame++)
        {
            float t = frame / 60f;
            float target = t < 3 ? acc.MinFrequency + (acc.MaxFrequency - acc.MinFrequency) * (t / 3f) : acc.MaxFrequency - (acc.MaxFrequency - acc.MinFrequency) * ((t - 3) / 3f);
            syn.UpdateFrequency(target, 60f);
            int n = Math.Min(buf.Length, total - o);
            syn.Read(buf, n);
            Array.Copy(buf, 0, all, o, n);
            o += n;
            if (frame % 30 == 0) Console.WriteLine($"  t {t:0.0}s target {target:0} rpm, synth {syn.CurrentPitch * 120f:0} rpm");
        }
        float peak = all.Max(Math.Abs);
        double rms = Math.Sqrt(all.Average(x => (double)x * x));
        int jumps = 0;
        for (int i = 1; i < all.Length; i++) if (Math.Abs(all[i] - all[i - 1]) > 0.5f) jumps++;
        Console.WriteLine($"  peak {peak:0.00} rms {rms:0.000} large steps {jumps}");
        if (outWav != null)
        {
            using var w = new BinaryWriter(File.Create(outWav));
            w.Write("RIFF".ToCharArray()); w.Write(36 + all.Length * 2); w.Write("WAVEfmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data".ToCharArray()); w.Write(all.Length * 2);
            foreach (var s in all) w.Write((short)(Math.Clamp(s, -1f, 1f) * 32767));
            Console.WriteLine("  wrote " + outWav);
        }
    }
}
