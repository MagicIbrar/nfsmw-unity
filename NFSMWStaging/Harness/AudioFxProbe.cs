using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// audiofx <sub>: checks for the audio behaviour round 3 (AEMS patch tables, FE sounds, collisions, moments).
//   audiofx banks              every bank's Csis classes and the sample-select / key-map tables their data uses
//   audiofx fe                 FE_COMMON / FE_MB trigger → slot tables
//   audiofx effects [filter]   effects audio fields, their audioimpact / audioscrape collections
//   audiofx moments            aud_moment_strm collections and the NISAudio stream SoundFX_Select picks for each
//   audiofx nis                NISAudio scene streams per NIS name (NIS_Select_Start/Blacklist/End)
//   audiofx mixmain            MAIN-state object outputs (FE/HUD, SFX_Common, NIS, moment) in FE, pause and in game
static class AudioFxProbe
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "banks";
        switch (sub)
        {
            case "banks": Banks(root); break;
            case "fe": Fe(root); break;
            case "effects": Effects(root, args.Length > 3 ? args[3] : null); break;
            case "moments": Moments(root); break;
            case "nis": Nis(root); break;
            case "mixmain": MixMain(root); break;
            case "mixswap": MixSwap(root); break;
            case "nisbanks": NisBanks(root); break;
            case "pvbytes": PvBytes(root, args[3]); break;
            case "triggers": Triggers(root, args.Length > 3 ? int.Parse(args[3]) : 60); break;
        }
    }

    static void Banks(string root)
    {
        foreach (var f in Directory.GetFiles(Path.Combine(root, "SOUND"), "*.abk", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var rel = f.Substring(Path.Combine(root, "SOUND").Length + 1);
            if (rel.StartsWith("ENGINE") && !rel.Contains("CAR_00_") && !rel.Contains("TRANNY") && !rel.Contains("WHINE_00")) continue;
            var p = AemsPatch.Load(f);
            Console.WriteLine($"== {rel}");
            foreach (var m in p.Modules)
            {
                Console.WriteLine($"   {m.Name} ({m.Project:X4}/{m.ClassId:X4}) data 0x{m.Data:X} +0x{m.DataSize:X}");
                foreach (var s in m.Selects) Console.WriteLine($"      select [{s.Length}] {string.Join(",", s.Take(40))}");
                foreach (var k in m.Maps)
                    Console.WriteLine($"      map @0x{k.Offset:X} u{k.ElementBytes * 8} [{k.Values.Length}] key {k.Min}..{k.Max}: {string.Join(",", k.Values.Take(16).Select(v => k.ElementBytes == 2 ? ((short)v).ToString() : v.ToString()))}{(k.Values.Length > 16 ? " ..." : "")}");
            }
        }
    }

    static void Fe(string root)
    {
        var common = AemsPatch.Load(Path.Combine(root, "SOUND", "GLOBAL", "FE_COMMON_MB.abk")).Select("PlayCommonSample");
        var fe = AemsPatch.Load(Path.Combine(root, "SOUND", "FE", "FE_MB.abk")).Select("PlayFrontEndSample");
        var bc = SoundBank.Load(Path.Combine(root, "SOUND", "GLOBAL", "FE_COMMON_MB.abk"));
        var bf = SoundBank.Load(Path.Combine(root, "SOUND", "FE", "FE_MB.abk"));
        string Len(SoundBank b, int slot)
        {
            var s = b.Sounds.FirstOrDefault(x => x.Index == slot);
            return s == null ? "-" : $"{s.Samples / (float)s.SampleRate:0.00}s{(s.DataR != null ? " st" : "")}";
        }
        Console.WriteLine($"PlayCommonSample [{common?.Length}]");
        for (int i = 0; common != null && i < common.Length; i++) if (common[i] > 0) Console.WriteLine($"  trigger {i,3} -> slot {common[i],2} {Len(bc, common[i])}");
        Console.WriteLine($"PlayFrontEndSample [{fe?.Length}]");
        for (int i = 0; fe != null && i < fe.Length; i++) if (fe[i] > 0) Console.WriteLine($"  trigger {i + 23,3} -> slot {fe[i],2} {Len(bf, fe[i])}");
    }

    static AttribDatabase Db(string root) => AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));

    static string Name(AttribDatabase db, uint cls, uint key)
    {
        if (key == 0) return "-";
        return db.Collections.TryGetValue((cls, key), out var c) ? (c.Name ?? key.ToString("X8")) : key.ToString("X8") + "?";
    }

    static void Effects(string root, string filter)
    {
        var db = Db(root);
        var a = CollisionAudioData.Read(db);
        Console.WriteLine($"effects {a.Effects.Count}, audioimpact {a.Impacts.Count}, audioscrape {a.Scrapes.Count}, aud_moment_strm {a.Moments.Count}");
        string Ref((uint cls, uint key) r) => r.key == 0 ? "-" : Name(db, r.cls, r.key);
        foreach (var e in a.Effects.Values.OrderBy(x => x.Name))
        {
            if (filter != null && (e.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (e.Default.key == 0 && e.Front.key == 0 && e.Side.key == 0) continue;
            Console.WriteLine($"{e.Name}: quad ({e.A:0.##},{e.B:0.##},{e.C:0.##},{e.D:0.##}) cull {e.AudioCullDist} default {Ref(e.Default)} front {Ref(e.Front)} rear {Ref(e.Rear)} top {Ref(e.Top)} bottom {Ref(e.Bottom)} side {Ref(e.Side)}");
        }
        foreach (var i in a.Impacts.Values.OrderBy(x => x.Name))
            Console.WriteLine($"  impact {i.Name}: desc 0x{i.Description:X} levels [{string.Join("|", i.Levels.Select(l => string.Join(",", l)))}] vol [{string.Join(",", i.Volumes)}] streams [{string.Join(",", i.Streams.Select(s => Name(db, CollisionAudioData.MomentClass, s.moment) + ">=" + s.threshold))}]");
        foreach (var s in a.Scrapes.Values.OrderBy(x => x.Name))
            Console.WriteLine($"  scrape {s.Name}: desc 0x{s.Description:X} csis {s.CsisEffect}");
    }

    static void Moments(string root)
    {
        var db = Db(root);
        var a = CollisionAudioData.Read(db);
        var s = SpeechScript.Load(root, "NISAudio");
        var rng = new Random(1);
        foreach (var m in a.Moments.Values.OrderBy(x => x.Name))
        {
            var args = new Dictionary<string, int> { { "SoundFX_Type", m.Stream }, { "SoundFX_Param", m.Param } };
            var pick = s.Choose("SoundFX_Select", args, rng, false);
            string take = pick.Count > 0 ? $"line {pick[0].Line} take {pick[0].Take} @{pick[0].BigOffset} {pick[0].NumBytes} B" : "none";
            Console.WriteLine($"{m.Name ?? m.Key.ToString("X8")} (parent {Name(db, CollisionAudioData.MomentClass, m.Parent)}): type {m.Stream} param 0x{m.Param:X} volslot {m.VolSlot} prio {m.Priority} interrupt {m.CanInterrupt} positioned {m.IsPositioned} -> {take}");
        }
    }

    static void Nis(string root)
    {
        var s = SpeechScript.Load(root, "NISAudio");
        var rng = new Random(1);
        foreach (var name in new[] { "GenericStart", "IntroNis01", "IntroNis05", "IntroNisBL01", "IntroNisBL06", "IntroNisBL16", "ArrestM01", "ArrestF02b", "ArrestM23", "EndingNis01", "IntroNisToll01", "OPMRivalIntro" })
        {
            var (ev, args) = NfsMw.Core.NisStreamSelect.Event(name, 0, false);
            var pick = ev != null ? NfsMw.Core.NisStreamSelect.Pick(s, ev, args, 7) : null;
            Console.WriteLine($"{name}: {ev} {string.Join(" ", args.Select(kv => kv.Key + "=" + kv.Value))} -> {(pick != null ? $"line {pick.Value.Line} take {pick.Value.Take} @{pick.Value.BigOffset} {pick.Value.NumBytes} B" : "none")}");
        }
    }

    static void Triggers(string root, int maxSections)
    {
        using var t = new TrackStream(root);
        var ids = new Dictionary<uint, int>();
        var names = new Dictionary<uint, int>();
        int n = 0, secs = 0;
        foreach (var s in t.Sections)
        {
            if (s.IsShared || secs++ >= maxSections) continue;
            var d = t.ReadRaw(s);
            var l = EventTrigger.ReadAll(d, 0, d.Length);
            foreach (var e in l)
            {
                ids[e.EventId] = ids.TryGetValue(e.EventId, out var k) ? k + 1 : 1;
                if (e.EventId == EventTrigger.SoundEvent) names[e.NameHash] = names.TryGetValue(e.NameHash, out var q) ? q + 1 : 1;
                if (n++ < 12) Console.WriteLine($"{s.Name} sec {e.Section}: name {e.NameHash:X8} ev {e.EventId:X} param {e.Parameter} dir {e.TrackDirectionMask} pos ({e.X:0.#},{e.Y:0.#},{e.Z:0.#}) r {e.Radius:0.##}");
            }
        }
        Console.WriteLine($"{secs} sections, {n} triggers");
        foreach (var kv in ids) Console.WriteLine($"  event {kv.Key:X}: {kv.Value}");
        foreach (var kv in names) Console.WriteLine($"  sound name {kv.Key:X8}: {kv.Value}");
    }

    static void PvBytes(string root, string field)
    {
        var db = Db(root);
        var hist = new Dictionary<int, List<string>>();
        foreach (var c in db.OfClass("pvehicle"))
        {
            if (!c.TryGet(field, out int at, out _, out _)) continue;
            int v = c.Source.Data[at];
            if (!hist.TryGetValue(v, out var l)) hist[v] = l = new List<string>();
            l.Add(c.Name);
        }
        foreach (var kv in hist.OrderBy(k => k.Key)) Console.WriteLine($"{field}={kv.Key}: {kv.Value.Count} {string.Join(",", kv.Value.Take(12))}");
    }

    static void NisBanks(string root)
    {
        var s = SpeechScript.Load(root, "NISAudio");
        foreach (var b in s.Banks)
        {
            Console.WriteLine($"line {b.Line} id 0x{b.Id:X4} sub {b.Sub} attrs {b.NumAttrs} takes {b.TakeCount} masks [{string.Join(",", b.AttrMasks.Select(m => m.ToString("X")))}]");
            for (int t = 0; t < b.TakeCount; t++) Console.WriteLine($"   take {t}: attr [{string.Join(",", b.Attrs[t])}] @{b.BigOffset + b.TakeOffset(t, -1)} {b.TakeBytes(t)} B");
        }
    }

    static void MixSwap(string root)
    {
        var dir = Path.Combine(root, "SOUND", "MIXMAPS");
        var map = MixMap.Load(Path.Combine(dir, "MAPOUTPUT.mxb"));
        var counts = new[] { 1, 1, 2, 8, 16, 6, 5, 10, 6, 0, 0, 1, 2 };
        for (int s = 0; s < counts.Length; s++) if (!map.HasState(s)) counts[s] = 0;
        map.Build(counts);
        int eng = MixMap.ObjectId(false, MixMap.PlayerCar, 0, 2);
        map.Attach(eng, true);
        var outs = map.Outputs(eng);
        void Show(string l) { for (int k = 0; k < 20; k++) map.Process(1f / 60f, 0); Console.WriteLine($"{l} (map {map.MapId:X}): engine out {string.Join(" ", Enumerable.Range(0, 8).Select(i => MixMap.Output(outs, i, MixMap.Vol)))} same block {ReferenceEquals(outs, map.Outputs(eng))}"); }
        Show("MAPOUTPUT");
        foreach (var f in new[] { "MAPOUTPUTDRG.mxb", "MAPOUTPUT2CR.mxb", "MAPOUTPUT2DR.mxb", "MAPOUTPUT.mxb" })
        {
            var m2 = MixMap.Load(Path.Combine(dir, f));
            var states = Enumerable.Range(0, MixMap.NumStates).Where(m2.HasState).ToArray();
            map.Swap(m2, counts);
            map.Attach(eng, true);
            Show(f + " states [" + string.Join(",", states) + "]");
        }
    }

    static void MixMain(string root)
    {
        var map = MixMap.Load(Path.Combine(root, "SOUND", "MIXMAPS", "MAPOUTPUT.mxb"));
        var counts = new[] { 1, 1, 2, 8, 16, 6, 5, 10, 6, 0, 0, 1, 2 };
        for (int s = 0; s < counts.Length; s++) if (!map.HasState(s)) counts[s] = 0;
        map.Build(counts);
        for (int o = 0; o < 8; o++) map.Attach(MixMap.ObjectId(false, MixMap.Main, 0, o), true);
        var gs = map.Inputs(MixMap.ObjectId(true, MixMap.Main, 0, 1));
        var mv = map.Inputs(MixMap.ObjectId(true, MixMap.Main, 0, 0));
        uint[] actions = { 0xFFFFFFFF, 0x3F, 0x0, 0x1C, 0xBE, 0xBC, 0x479, 0x121, 0x200, 0x33, 0x838, 0x7F, 0x1, 0x7F, 0x38, 0x1000, 0xFFFFFFFF, 0xFFFFFFFF };
        foreach (var (label, ctl) in new[] { ("ingame", 1u << 2), ("pause", (1u << 2) | (1u << 1)), ("fe", 1u << 3), ("nis intro", (1u << 2) | (1u << 7)), ("nis arrest", (1u << 2) | (1u << 10)) })
        {
            uint sfx = 0;
            for (int n = 0; n < 18; n++) if ((ctl & (1u << n)) != 0) sfx |= actions[n];
            for (int i = 0; i <= 13; i++) gs[i] = (sfx & (1u << i)) != 0 ? 0x7FFF : 0;
            gs[15] = 1; mv[15] = 1;
            mv[6] = (ctl & 0x3483B) != 0 ? 0x7FFF : 0;
            for (int k = 0; k < 10; k++) map.Process(1f / 60f, 0);
            Console.WriteLine($"-- {label}");
            foreach (var o in new[] { 4, 5, 6, 7 })
            {
                var outs = map.Outputs(MixMap.ObjectId(false, MixMap.Main, 0, o));
                Console.WriteLine($"  obj {o}: {(outs == null ? "unmapped" : string.Join(" ", Enumerable.Range(0, 12).Select(i => MixMap.Output(outs, i, MixMap.Vol))))}");
            }
        }
    }
}
