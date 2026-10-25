using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// spchevt [base] [outDir]: dumps every SPCH speech event (params, sentences, slots) and evaluates a few with sample
// args, optionally stitching the chosen takes into .wav files in outDir.
static class SpeechProbe
{
    public static void Run(string root, string[] args)
    {
        string baseName = args.Length > 2 ? args[2] : "copspeech";
        string outDir = args.Length > 3 ? args[3] : null;
        var s = SpeechScript.Load(root, baseName);
        Console.WriteLine($"{baseName}: {s.EventNames.Count} events, {s.Banks.Count} banks, project {s.ProjectId} dat {s.DatId}, big {s.BigPath}");

        // consistency: slot entries vs bank attrs, banks every slot can find
        int slots = 0, attrMismatch = 0, unresolved = 0;
        var refd = new HashSet<int>();
        foreach (var name in s.EventNames)
        {
            var e = s.GetEvent(name);
            foreach (var se in e.Sentences)
                foreach (var sl in se.Slots)
                {
                    slots++;
                    var banks = s.Banks.Where(b => b.Id == sl.BankId).ToList();
                    if (banks.Count == 0) unresolved++;
                    foreach (var b in banks) { refd.Add(b.Line); if (b.NumAttrs != sl.Entries.Length) attrMismatch++; }
                }
        }
        Console.WriteLine($"slots {slots}, slots with no bank {unresolved}, bank/slot attr count mismatches {attrMismatch}, banks referenced {refd.Count}/{s.Banks.Count}");
        var dup = s.Banks.GroupBy(b => (b.Id, b.Sub)).Where(g => g.Count() > 1).ToList();
        Console.WriteLine($"duplicate (id,sub) banks: {dup.Count}; partitioned banks {s.Banks.Count(b => b.Partitioned)}; ring sizes != take count {s.Banks.Count(b => b.RingSize != 0 && b.RingSize != b.TakeCount)}");

        // .csi cross-check: every .evt name is in the .csi with the same interface crc
        string csiPath = Path.Combine(Path.GetDirectoryName(s.BigPath), baseName + ".csi");
        if (File.Exists(csiPath))
        {
            var csi = File.ReadAllBytes(csiPath);
            int n = Chunks.U16(csi, 0xA), ok = 0;
            var map = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) { int no = Chunks.I32(csi, 0x2C + i * 12); map[Chunks.CString(csi, no, csi.Length - no)] = Chunks.I32(csi, 0x2C + i * 12 + 4); }
            foreach (var name in s.EventNames) if (map.TryGetValue(name, out int crc) && crc == s.GetEvent(name).InterfaceCrc) ok++;
            Console.WriteLine($".csi: {n} interfaces, {ok}/{s.EventNames.Count} event names+crcs match");
        }

        foreach (var name in s.EventNames)
        {
            Console.WriteLine(s.Describe(name));
            var sp = s.Speakers(name);
            Console.WriteLine($"  speakers [{string.Join(",", sp)}]");
        }

        if (baseName.StartsWith("NIS", StringComparison.OrdinalIgnoreCase))
        {
            var rng0 = new Random(1);
            foreach (var name in s.EventNames)
            {
                var e = s.GetEvent(name);
                var a = new Dictionary<string, int>();
                foreach (var pn in e.ParamNames) a[pn] = 1;
                Report(s, name, a, rng0, outDir);
            }
            return;
        }

        var rng = new Random(12345);
        int Ev(string ev, string p, string v) => SpeechScript.EnumValue(s.ParameterType(ev, p), v);
        var tests = new List<(string ev, Dictionary<string, int> a)>
        {
            ("Setup_VehicleReport", new Dictionary<string, int> { ["speaker_id"] = 3, ["car_color"] = Ev("Setup_VehicleReport", "car_color", "Red"), ["car_type"] = Ev("Setup_VehicleReport", "car_type", "Porsche"), ["speed"] = Ev("Setup_VehicleReport", "speed", "speeds_in_excess_of_120"), ["measurement"] = Ev("Setup_VehicleReport", "measurement", "imperial_only") }),
            ("Setup_VehicleReport", new Dictionary<string, int> { ["speaker_id"] = 4, ["car_color"] = Ev("Setup_VehicleReport", "car_color", "Black"), ["car_type"] = Ev("Setup_VehicleReport", "car_type", "BMW"), ["speed"] = Ev("Setup_VehicleReport", "speed", "speeds_in_excess_of_160"), ["measurement"] = Ev("Setup_VehicleReport", "measurement", "metric_only") }),
            ("Setup_InitPursuit", new Dictionary<string, int> { ["speaker_id"] = 3, ["num_suspects"] = 1 }),
            ("Acknowledge", new Dictionary<string, int> { ["speaker_id"] = 1 }),
            ("Arrest_Arrest", new Dictionary<string, int> { ["speaker_id"] = 3 }),
        };
        // also every event with all-ones params (speaker 3 / first available)
        foreach (var (ev, a) in tests) Report(s, ev, a, rng, outDir);
        Console.WriteLine("-- repeat avoidance: Setup_InitPursuit x6");
        for (int i = 0; i < 6; i++) Report(s, "Setup_InitPursuit", tests[2].a, rng, null);
        int okEv = 0;
        foreach (var name in s.EventNames)
        {
            var e = s.GetEvent(name);
            var sp = s.Speakers(name);
            var a = new Dictionary<string, int>();
            for (int k = 0; k < e.ParamNames.Length; k++) a[e.ParamNames[k]] = e.ParamTypes[k] == "int" ? (sp.Count > 0 ? sp[0] : 1) : 1;
            if (s.Choose(name, a, rng, false).Count > 0) okEv++; else Console.WriteLine($"  no sentence: {name} speakers [{string.Join(",", sp)}]");
        }
        Console.WriteLine($"events choosing a sentence with params = first value / first speaker: {okEv}/{s.EventNames.Count}");
    }

    static void Report(SpeechScript s, string ev, Dictionary<string, int> a, Random rng, string outDir)
    {
        var res = s.Choose(ev, a, rng);
        Console.WriteLine($"{ev}({string.Join(", ", a.Select(kv => kv.Key + "=" + kv.Value))}) -> S{s.LastSentence}: {string.Join(" + ", res.Select(r => $"line {r.Line} take {r.Take} @{r.BigOffset:X} {r.NumBytes}B (bank {r.BankId:X}/{r.Speaker})"))}");
        if (outDir == null || res.Count == 0) return;
        var all = new List<float>();
        int rate = 0;
        foreach (var r in res)
        {
            var st = s.ReadTake(r);
            if (st == null) { Console.WriteLine($"   take {r} did not decode"); continue; }
            rate = st.SampleRate;
            all.AddRange(st.Data[0]);
            Console.WriteLine($"   take {r}: {st.Samples} samples @ {st.SampleRate} Hz = {st.Length:0.00}s");
        }
        if (rate == 0) return;
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, $"{ev}_{DateTime.Now.Ticks % 100000}.wav");
        using var wav = new BinaryWriter(File.Create(path));
        wav.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wav.Write(36 + all.Count * 2); wav.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16); wav.Write((short)1); wav.Write((short)1);
        wav.Write(rate); wav.Write(rate * 2); wav.Write((short)2); wav.Write((short)16); wav.Write(System.Text.Encoding.ASCII.GetBytes("data")); wav.Write(all.Count * 2);
        foreach (var v in all) wav.Write((short)(Math.Clamp(v, -1f, 1f) * 32767));
        Console.WriteLine($"   -> {path} ({all.Count / (float)rate:0.00}s)");
    }
}
