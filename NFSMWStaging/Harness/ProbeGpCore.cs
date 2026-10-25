using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

/// <summary>
/// gameplay_core round 2 probes (gameplay_core.md §9-§13): `gpcore unlocks` (race unlock graph per bin: engage triggers
/// with InitiallyUnlocked, CellChallengeRace, cell_engage), `gpcore smack` (smackable collections with their fields),
/// `gpcore races` (every race event's index fields as GRaceParameters::GenerateIndex reads them).
/// </summary>
static class ProbeGpCore
{
    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "unlocks";
        var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"));
        var gp = db.OfClass("gameplay").ToList();
        var byKey = new Dictionary<uint, AttribCollection>();
        foreach (var c in gp) byKey[c.Key] = c;
        string N(uint k) => byKey.TryGetValue(k, out var c) ? (c.Name ?? k.ToString("X8")) : k.ToString("X8");
        string Ks(AttribCollection c, string f) { try { return c.Has(f) ? string.Join(",", c.Keys(f).Select(N)) : "-"; } catch { return "?"; } }
        bool Own(AttribCollection c, string f) => c.TryGetKey(AttribDatabase.Hash(f), out _, out _, out _) && c.Source == c;

        if (mode == "types")
        {
            // CareerData's RaceType against GetRaceType (EventIconType), plus engage-trigger distance to the start grid
            var career = CareerData.Load(root);
            foreach (var bin in career.Bins.Values)
                foreach (var e in bin.WorldRaces.Concat(bin.BossRaces))
                {
                    RaceType want = e.GRaceType switch
                    {
                        RaceIndex.P2P => RaceType.Sprint, RaceIndex.Circuit => RaceType.Circuit, RaceIndex.Drag => RaceType.Drag,
                        RaceIndex.Knockout => RaceType.Knockout, RaceIndex.Tollbooth => RaceType.Tollbooth, RaceIndex.SpeedTrap => RaceType.SpeedTrap,
                        RaceIndex.Challenge => RaceType.Pursuit, _ => RaceType.Other,
                    };
                    float dist = e.Engage != null && e.Start != null ? Dist(e.Engage.Position, e.Start.Position) : -1;
                    bool bad = want != e.Type;
                    if (bad || dist > 30 || args.Length > 3)
                        Console.WriteLine($"{(bad ? "TYPE " : "")}{e.Name} {e.EventId} type {e.Type} icon {e.GRaceType} engage {(e.Engage == null ? "-" : dist.ToString("0") + " m")} unlocked {e.EngageInitiallyUnlocked} diff {e.DifficultyTier} cops {e.CopDensityTier} region {e.RegionIndex}");
                }
            var acts = career.RaceActivities();
            Console.WriteLine($"race activities {acts.Count}: QR-unlocked {acts.Count(e => e.InitiallyUnlockedQuickRace)}, online {acts.Count(e => e.InitiallyUnlockedOnline)}, challenge {acts.Count(e => e.InitiallyUnlockedChallenge)}, dday {acts.Count(e => e.DDayRace)}, never QR {acts.Count(e => e.NeverInQuickRace)}");
            foreach (var bin in career.Bins.Values)
                Console.WriteLine($"bin {bin.Index}: {bin.EngageTriggers.Count} engage triggers, cell race {bin.CellChallengeRace:X8} sms {bin.SmsCellChallenge}");
        }
        else if (mode == "unlocks")
        {
            foreach (var bin in gp.Where(c => c.Parent?.Name == "race_bin").OrderByDescending(c => c.Int("BinIndex")))
            {
                Console.WriteLine($"== {bin.Name} bin {bin.Int("BinIndex")} Entry={Ks(bin, "EntryActivity")} EntryCall={(bin.Has("EntryCellCallID") ? bin.Int("EntryCellCallID") : 0)} CellRace={Ks(bin, "CellChallengeRace")} SMSCell={(bin.Has("SMSCellChallenge") ? bin.Int("SMSCellChallenge") : -9)} SMSRival={(bin.Has("SMSRivalChallenge") ? bin.Int("SMSRivalChallenge") : -9)}");
                Console.WriteLine($"   World: {Ks(bin, "WorldRaces")}");
                Console.WriteLine($"   Boss : {Ks(bin, "BossRaces")}");
                foreach (var k in bin.Keys("Children"))
                {
                    if (!byKey.TryGetValue(k, out var ch)) continue;
                    if (ch.Has("RaceTriggers"))
                        foreach (var tk in ch.Keys("RaceTriggers"))
                            if (byKey.TryGetValue(tk, out var t))
                                Console.WriteLine($"   engage {t.Name?.Substring(t.Name.LastIndexOf('/') + 1)} -> {Ks(t, "TargetActivity")} unlocked={t.Bool("InitiallyUnlocked")}{(Own(t, "InitiallyUnlocked") ? "" : "(inh)")}");
                }
            }
            foreach (var c in gp.Where(c => c.Name != null && (c.Parent?.Name == "cell_engage" || c.Parent?.Name == "completion_unlock" || c.Parent?.Name == "reputation_unlock" || c.Parent?.Name == "world_engage")))
                Console.WriteLine($"-- {c.Parent.Name} {c.Name}: Gate={Ks(c, "GateActivity")} Target={Ks(c, "TargetActivity")} Targets={Ks(c, "TargetActivities")} RaceList={Ks(c, "RaceList")} Ring={(c.Has("RingTime") ? c.Float("RingTime") : 0)} Delay={(c.Has("DelayTime") ? c.Float("DelayTime") : 0)} Rep={(c.Has("ReputationRequired") ? c.Int("ReputationRequired") : 0)} Auto={c.Bool("AutoStart")}");
        }
        else if (mode == "races")
        {
            string[] f = { "EventID", "EventIconType", "KnockoutsPerLap", "IsLoopingRace", "TimeLimit", "IsPursuitRace", "PursuitRace", "CashValue", "Reputation", "NumLaps", "RaceLength", "Region", "CopDensity", "TrafficLevel", "DifficultyLevel", "IsBossRace", "NeverInQuickRace", "InitiallyUnlockedQuickRace", "InitiallyUnlockedOnline", "InitiallyUnlockedChallenge", "CanBeReversed", "DDayRace", "CopsInRace", "ScriptedCopsInRace", "RankPlayersByDistance", "CollectorsEditionRace", "ChallengeSeriesRace", "RivalBestTime", "UseWorldHeatInRace", "MaxHeatLevel", "ForceHeatLevel", "MaxRaceHeatLevel" };
            foreach (var c in gp.Where(c => c.Name != null && c.Has("EventID") && !string.IsNullOrEmpty(SafeText(c, "EventID"))))
            {
                var parts = new List<string>();
                foreach (var x in f)
                {
                    if (!c.Has(x)) continue;
                    string v;
                    try
                    {
                        var t = c.Class.Fields[AttribDatabase.Hash(x)].Type;
                        v = t == AttribDatabase.Hash("EA::Reflection::Text") ? c.Text(x) : t == AttribDatabase.Hash("EA::Reflection::Bool") ? (c.Bool(x) ? "T" : "F") :
                            t == AttribDatabase.Hash("EA::Reflection::Float") ? c.Float(x).ToString("0.##") : c.Int(x).ToString();
                    }
                    catch { v = "?"; }
                    parts.Add($"{x}={v}{(Own(c, x) ? "" : "*")}");
                }
                Console.WriteLine($"{c.Name} [{c.Parent?.Name}] " + string.Join(" ", parts));
            }
        }
        else if (mode == "evseq")
        {
            // gpcore evseq <eventnames.txt>: the world event sequencer packs of L2RA.BUN (chunk 0x3B811) with their
            // engines, systems, stimuli and the events each action fires (pursuit breakers: EBreakerStopCops etc.)
            var names = new Dictionary<uint, string>();
            if (args.Length > 3) foreach (var n in File.ReadAllLines(args[3])) names[AttribDatabase.Hash(n.Trim())] = n.Trim();
            foreach (var s in new[] { "create", "trigger", "start", "START", "stop", "pause", "resume", "unload", "smacked", "SMACKED", "hit", "detached", "breaker", "explode", "GLOBAL", "destroyed", "default", "idle", "init", "on", "off" })
                names[AttribDatabase.Hash(s)] = "'" + s + "'";
            string Nm(uint h) => names.TryGetValue(h, out var n) ? n : h.ToString("X8");
            var d = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN"));
            int k = 0;
            int at = 0;
            while (true)
            {
                int i = IndexOf(d, new byte[] { 0x11, 0xB8, 0x03, 0x00 }, at);
                if (i < 0) break;
                at = i + 1;
                int size = BitConverter.ToInt32(d, i + 4);
                if (size <= 0 || i + 8 + size > d.Length) continue;
                var pack = EventSeqPack.Read(d, i + 8, i + 8 + size);
                if (pack == null) continue;
                k++;
                foreach (var e in pack.Engines)
                {
                    Console.WriteLine($"== pack {k} @0x{i:X} engine '{e.Name}' systems {e.Systems.Length}");
                    for (int si = 0; si < e.Systems.Length; si++)
                    {
                        var sys = e.Systems[si];
                        if (sys == null) continue;
                        Console.WriteLine($"   system {Nm(e.SystemIds[si])} initial {Nm(sys.InitialState)}");
                        foreach (var st in sys.States)
                            foreach (var r in st.Value.Responses)
                            {
                                var a = r.Value.action;
                                Console.WriteLine($"     state {Nm(st.Key)} on {Nm(r.Key)} -> {Nm(r.Value.endState)} action {(a == null ? "-" : Nm(a.Name) + " dur " + a.Duration.ToString("0.##"))}");
                                if (a?.Lists == null) continue;
                                for (int li = 0; li < a.Lists.Length; li++)
                                {
                                    string when = li < (a.Tags?.Length ?? 0) ? "tag " + Nm(a.Tags[li]) : "t=" + (a.Times != null && li - (a.Tags?.Length ?? 0) < a.Times.Length ? a.Times[li - (a.Tags?.Length ?? 0)].ToString("0.##") : "?");
                                    foreach (var ev in a.Lists[li] ?? new List<SeqEvent>())
                                    {
                                        var vals = new List<string>();
                                        for (int o = 4; o < Math.Min(ev.Size, 32); o += 4) { float f = ev.F(o); vals.Add(Math.Abs(f) > 1e-4 && Math.Abs(f) < 1e6 ? f.ToString("0.###") : ev.U(o).ToString("X")); }
                                        Console.WriteLine($"         [{when}] {Nm(ev.Id)} ({string.Join(",", vals)}){(Nm(ev.Id) == "EMomentStrm" ? " str=" + ev.S(4) : "")}");
                                    }
                                }
                            }
                    }
                }
            }
        }
        else if (mode == "smack")
        {
            var all = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
            var cls = all.Classes.TryGetValue(0xCE70D7DB, out var sc) ? sc : null;
            foreach (var c in all.Collections.Values.Where(c => c.ClassKey == 0xCE70D7DB))
            {
                var parts = new List<string>();
                if (c.Class != null)
                    foreach (var kv in c.Class.Fields)
                    {
                        if (!c.TryGetKey(kv.Key, out int at, out int count, out _)) continue;
                        bool inh = c.Source != c;
                        string fn = all.Names.TryGetValue(kv.Key, out var n) ? n : kv.Key.ToString("X8");
                        var src = c.Source.Data;
                        var vals = new List<string>();
                        for (int i = 0; i < Math.Max(Math.Min(count, 4), kv.Key == 0x5AAB860F ? 4 : 0); i++)
                        {
                            float fv = BitConverter.ToSingle(src, at + i * 4);
                            int iv = BitConverter.ToInt32(src, at + i * 4);
                            vals.Add(Math.Abs(fv) > 1e-4 && Math.Abs(fv) < 1e7 ? fv.ToString("0.###") : iv.ToString("X"));
                        }
                        parts.Add($"{fn}{(inh ? "*" : "")}=[{string.Join(",", vals)}]");
                    }
                Console.WriteLine($"{c.Name ?? c.Key.ToString("X8")} parent {c.Parent?.Name}: " + string.Join(" ", parts));
            }
        }
    }

    static float Dist(Vec3 a, Vec3 b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static int IndexOf(byte[] d, byte[] p, int from)
    {
        for (int i = from; i <= d.Length - p.Length; i++)
        {
            int j = 0;
            while (j < p.Length && d[i + j] == p[j]) j++;
            if (j == p.Length) return i;
        }
        return -1;
    }

    static string SafeText(AttribCollection c, string f) { try { return c.Text(f); } catch { return null; } }
}
