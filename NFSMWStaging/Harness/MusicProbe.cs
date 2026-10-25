using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// music <sub>: checks for the PathFinder interactive-music port (Core/PathMusic.cs).
//   music map                 header, counts, named values, songs and whether their events exist
//   music sim [song#]         runs the PATH engine on a simulated stream: a song, then a pursuit with intensity changes
//   music wav <song#> <secs> <out.wav>   renders the engine's section sequence to a wav (decodes MW_Music.mus)
static class MusicProbe
{
    sealed class SimStream : IPathStream
    {
        public readonly List<(int node, int dur)> Queue = new List<(int, int)>();
        public int Pos;
        public bool PausedFlag;
        public readonly List<int> Played = new List<int>();
        public void Stop() { Queue.Clear(); Pos = 0; }
        public void Pause(bool on) => PausedFlag = on;
        public bool Ready => Queue.Count < 3;
        public int TimeRemaining(int n)
        {
            if (n < 0) return Queue.Sum(q => q.dur) - Pos;
            if (n >= Queue.Count) return -1;
            return n == 0 ? Queue[0].dur - Pos : Queue[n].dur;
        }
        public int TimeBuffered => 1 << 20;
        public int Play(int node, long off, int beat, int hold, int dur) { Queue.Add((node, Math.Max(1, dur))); return 0; }
        public void SetVolume(int v) { }
        public int PlayingNode => Queue.Count > 0 ? Queue[0].node : -1;
        public int PlayingPosMs => Pos;
        public void Tick(int ms)
        {
            if (PausedFlag) return;
            while (ms > 0 && Queue.Count > 0)
            {
                if (Pos == 0) Played.Add(Queue[0].node);
                int left = Queue[0].dur - Pos, d = Math.Min(left, ms);
                Pos += d; ms -= d;
                if (Pos >= Queue[0].dur) { Queue.RemoveAt(0); Pos = 0; }
            }
        }
    }

    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "map";
        var map = PathMap.Load(Path.Combine(root, "SOUND", "PFDATA", "MW_Music.mpf"));
        if (map == null) { Console.WriteLine("no map"); return; }
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        var songs = SongInfo.Load(db);
        switch (sub)
        {
            case "map": Map(map, songs); break;
            case "sim": Sim(map, songs, args.Length > 3 ? int.Parse(args[3]) : 0); break;
            case "mix":
            {
                var mm = MixMap.Load(Path.Combine(root, "SOUND", "MIXMAPS", "MAPOUTPUT.mxb"));
                var counts = new int[MixMap.NumStates];
                counts[MixMap.Main] = 1; counts[MixMap.Music] = 1;
                mm.Build(counts);
                int id = MixMap.ObjectId(false, MixMap.Music, 0, 1);
                mm.Attach(id, true);
                for (int o = 0; o < 8; o++) mm.Attach(MixMap.ObjectId(false, MixMap.Main, 0, o), true);
                for (int st2 = 0; st2 < 2; st2++)
                    for (int o = 0; o < 16; o++)
                    {
                        int oid = MixMap.ObjectId(false, st2, 0, o);
                        var ds = mm.Describe(oid).ToList();
                        if (ds.Count > 0) Console.WriteLine($"state {st2} obj {o}: " + string.Join(", ", ds.Select(x => $"s{x.slot}/t{x.type}")));
                    }
                foreach (var dsc in mm.Describe(id)) Console.WriteLine($"  slot {dsc.slot} type {dsc.type} db {dsc.db}");
                var inp = mm.Inputs(id);
                var mv = mm.Inputs(MixMap.ObjectId(true, MixMap.Main, 0, 0));
                var gs = mm.Inputs(MixMap.ObjectId(true, MixMap.Main, 0, 1));
                for (int k = 0; k < 6; k++) mv[k] = (int)((1 - 0.8f) * 32767);
                mv[15] = 1; gs[15] = 1; inp[15] = 1;
                foreach (var (name, i6, i7, i8, gsb) in new[] { ("licensed", 1, 0, 0, 0), ("interactive", 0, 1, 0, 0), ("ambience", 0, 0, 1, 0), ("licensed+pause", 1, 0, 0, 0x3F), ("licensed+NIS", 1, 0, 0, 0x479), ("FE", 1, 0, 0, 0x1C), ("ingame", 1, 0, 0, 0x0) })
                {
                    inp[6] = i6 * 0x7FFF; inp[7] = i7 * 0x7FFF; inp[8] = i8 * 0x7FFF;
                    for (int b = 0; b < 14; b++) gs[b] = (gsb & (1 << b)) != 0 ? 0x7FFF : 0;
                    for (int f = 0; f < 200; f++) mm.Process(1 / 60f, 0);
                    var outs = mm.Outputs(id);
                    Console.WriteLine($"{name}: vol " + string.Join(" ", Enumerable.Range(0, 10).Select(i => MixMap.Output(outs, i, MixMap.Vol))) + " freq9 " + MixMap.Output(outs, 9, MixMap.Freq));
                }
                break;
            }
            case "states":
            {
                var mm = MixMap.Load(Path.Combine(root, "SOUND", "MIXMAPS", "MAPOUTPUT.mxb"));
                var counts = new int[MixMap.NumStates];
                for (int i = 0; i < counts.Length; i++) counts[i] = mm.HasState(i) ? 1 : 0;
                mm.Build(counts);
                for (int st2 = 0; st2 < MixMap.NumStates; st2++)
                {
                    Console.WriteLine($"state {st2} present {mm.HasState(st2)}");
                    for (int o = 0; o < 32; o++)
                    {
                        int oid = MixMap.ObjectId(false, st2, 0, o);
                        var ds = mm.Describe(oid).ToList();
                        if (ds.Count > 0) Console.WriteLine($"   obj {o}: " + string.Join(", ", ds.Select(x => $"s{x.slot}/t{x.type}")));
                    }
                }
                break;
            }
            case "stitch":
            {
                var lists = StitchBundle.Read(GameFiles.ReadBundle(Path.Combine(root, "GLOBAL", "InGameB.lzc")));
                for (int ty = 0; ty < 3; ty++)
                {
                    Console.WriteLine($"type {ty}: {lists[ty].Count}");
                    foreach (int idx in new[] { 0, 1, 45, 51, 57, 75, 84 })
                        if (idx < lists[ty].Count && lists[ty][idx] != null)
                        {
                            var st = lists[ty][idx];
                            Console.WriteLine($"  [{idx}] vol {st.Volume} n {st.Samples.Length}: " + string.Join(" | ", st.Samples.Select(x => $"s{x.SampleIndex} v{x.Volume} p{x.Pitch} off{x.OffsetMs} az{x.Azimuth} rv{x.RndVol} rp{x.RndPitch}")));
                        }
                }
                break;
            }
            case "zones":
            {
                var bun = GameFiles.ReadBundle(Path.Combine(root, "TRACKS", "L2RA.BUN"));
                var zones = TrackZone.Read(bun);
                foreach (var g in zones.GroupBy(z => z.Type).OrderBy(g => g.Key)) Console.WriteLine($"zone type {g.Key}: {g.Count()} elev>0 {g.Count(z => z.Elevation != 0)}");
                var layers = ParamMapLayer.Read(bun);
                foreach (var l in layers) Console.WriteLine($"layer {l.NameHash:X8} sets {l.Sets} fields {l.Fields} quad {l.Left},{l.Top}..{l.Right},{l.Bottom}");
                var amb = layers.FirstOrDefault(l => l.NameHash == ParamMapLayer.Hash("Ambience"));
                var rev = layers.FirstOrDefault(l => l.NameHash == ParamMapLayer.Hash("Reverb"));
                var hist = new Dictionary<string, int>();
                for (float x = -5000; x < 5000; x += 100) for (float y = -1000; y < 9000; y += 100)
                {
                    string k = $"amb {amb?.GetInt(x, y, 0)} rev {rev?.GetInt(x, y, 0)}/{rev?.GetInt(x, y, 2)}";
                    hist[k] = hist.TryGetValue(k, out int c) ? c + 1 : 1;
                }
                foreach (var kv in hist.OrderByDescending(k => k.Value).Take(25)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
                Console.WriteLine($"zones bbox x {zones.Min(z => z.MinX)}..{zones.Max(z => z.MaxX)} y {zones.Min(z => z.MinY)}..{zones.Max(z => z.MaxY)}");
                var t = zones.FirstOrDefault(z => z.Type == 3);
                if (t != null) Console.WriteLine($"tunnel 0: bbox {t.MinX},{t.MinY}..{t.MaxX},{t.MaxY} elev {t.Elevation} pts {t.Px.Length}");
                break;
            }
            case "smack":
            {
                int n = 0;
                foreach (var c in db.OfClass("smackable"))
                {
                    if (!c.Has("IsWooshable") && !c.Has("WooshType")) continue;
                    if (n++ < 12) Console.WriteLine($"{c.Name}: IsWooshable {(c.Has("IsWooshable") ? c.Bool("IsWooshable").ToString() : "-")} WooshType {(c.TryGet("WooshType", out int at, out _, out _) ? c.Source.Data[at].ToString() : "-")}");
                }
                Console.WriteLine($"{n} smackables with woosh fields");
                break;
            }
            case "attr":
                foreach (var c in db.OfClass("music").Take(2))
                {
                    Console.WriteLine(c);
                    foreach (var f in new[] { "SongName", "Artist", "Album", "DefPlay", "PathEvent" })
                        if (c.TryGet(f, out int at, out int cnt, out var def))
                        {
                            Console.Write($"  {f} at {at:X} cnt {cnt} size {def.Size} flags {def.Flags} type {def.Type:X8} raw ");
                            for (int i = 0; i < Math.Min(16, def.Size); i++) Console.Write($"{c.Source.Data[at + i]:X2}");
                            for (int k = 0; k < 16; k += 4) Console.Write($" ptr{k}={db.Ptr(c.Source.Pack, at + k)}");
                            Console.WriteLine();
                        }
                }
                break;
            case "wav": Wav(root, map, songs, int.Parse(args[3]), float.Parse(args[4]), args[5]); break;
        }
    }

    static void Map(PathMap map, List<SongInfo> songs)
    {
        Console.WriteLine($"project {map.ProjectId} tracks {map.NumTracks} events {map.NumEvents} remaps {map.NumRemaps} nodes {map.NumNodes} sections {map.NumSections} mus {map.MusChecksum:X8}");
        Console.WriteLine("named: " + string.Join(", ", map.NamedValues));
        for (int i = 0; i < songs.Count; i++)
        {
            var s = songs[i];
            if (s == null) { Console.WriteLine($"  song {i}: (missing)"); continue; }
            int e = map.FindEvent(s.PathEvent & 0xFFFFFF, 0xFFFFFFFF);
            Console.WriteLine($"  song {i}: {s.Artist} - {s.Name} [{s.Album}] {s.DefPlay} event {s.PathEvent:X8} -> EV{e}");
        }
        int bad = 0;
        for (int n = 0; n < map.NumNodes; n++)
        {
            int sec = map.Section(n);
            if (sec > 0 && map.SectionOffset(sec) < 0) bad++;
            for (int k = 0; k < map.BranchCount(n); k++) if (map.Branch(n, k).node > map.NumNodes) bad++;
        }
        Console.WriteLine($"bad refs: {bad}");
    }

    static PathEngine Engine(PathMap map, SimStream st, List<string> log)
    {
        var eng = new PathEngine(map);
        eng.Tracks[0].Stream = st;
        eng.Progress = (p, n) => log.Add($"progress node {n}");
        eng.ActionCallback = (h, id, parm) => log.Add($"callback {id} {parm}");
        return eng;
    }

    static void Step(PathEngine eng, SimStream st, ref uint now, int ms, List<string> log, string label)
    {
        int start = st.Played.Count;
        for (int t = 0; t < ms; t += 20) { now += 20; st.Tick(20); eng.Service(now); }
        var nodes = st.Played.Skip(start).ToList();
        Console.WriteLine($"[{label}] t={now / 1000f:0.0}s status {eng.TrackStatus(eng.Tracks[0])} ctrl {eng.Tracks[0].Ctrl} played {nodes.Count}: {string.Join(" ", nodes.Take(40))}");
        foreach (var l in log) Console.WriteLine("    " + l);
        log.Clear();
    }

    static void Sim(PathMap map, List<SongInfo> songs, int song)
    {
        var st = new SimStream();
        var log = new List<string>();
        var eng = Engine(map, st, log);
        uint now = 0;
        eng.Event(0x01C53FC7);   // PF_EVT_STARTSCREEN (splash)
        Step(eng, st, ref now, 6000, log, "splash");
        var s = songs.Count > song ? songs[song] : null;
        if (s != null) { Console.WriteLine($"song {s.Artist} - {s.Name}"); eng.Event(s.PathEvent); }
        Step(eng, st, ref now, 20000, log, "song");
        // interactive: pursuit id 1, MusicFlow START then states
        eng.SetNamedValue("PursuitID", 1);
        eng.Event(0x016E7282);   // PF_EVT_START
        Step(eng, st, ref now, 8000, log, "start");
        eng.Control(1, 20);
        Step(eng, st, ref now, 8000, log, "ctrl 20");
        eng.Control(1, 100);
        Step(eng, st, ref now, 8000, log, "ctrl 100");
        eng.Event(0x01E9222F);   // LOSE
        Step(eng, st, ref now, 8000, log, "lose");
        eng.Event(0x01518C15);   // WIN
        Step(eng, st, ref now, 8000, log, "win");
        eng.Event(0x01B39639);   // ELUDE
        Step(eng, st, ref now, 8000, log, "elude");
        eng.Event(0x01E2814E);   // ENDWIN
        Step(eng, st, ref now, 15000, log, "endwin");
        eng.Event(0x011BBC15);   // ambience zone 0
        Step(eng, st, ref now, 10000, log, "ambience");
    }

    static void Wav(string root, PathMap map, List<SongInfo> songs, int song, float secs, string outPath)
    {
        var st = new SimStream();
        var log = new List<string>();
        var eng = Engine(map, st, log);
        uint now = 0;
        eng.Event(songs[song].PathEvent);
        var mus = File.OpenRead(Path.Combine(root, "SOUND", "PFDATA", "MW_Music.mus"));
        var pcm = new List<short>();
        int rate = 0;
        for (int t = 0; t < secs * 1000; t += 20) { now += 20; st.Tick(20); eng.Service(now); }
        foreach (int node in st.Played)
        {
            long off = map.SectionOffset(map.Section(node));
            var buf = new byte[Math.Min(1 << 20, mus.Length - off)];
            mus.Position = off;
            mus.Read(buf, 0, buf.Length);
            var s = EaStream.Read(buf, 0, out _);
            if (s == null) continue;
            rate = s.SampleRate;
            foreach (var v in s.Stereo()) pcm.Add((short)(Math.Max(-1f, Math.Min(1f, v)) * 32767));
        }
        using var w = new BinaryWriter(File.Create(outPath));
        w.Write(0x46464952); w.Write(36 + pcm.Count * 2); w.Write(0x45564157); w.Write(0x20746D66); w.Write(16);
        w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write(0x61746164); w.Write(pcm.Count * 2);
        foreach (var v in pcm) w.Write(v);
        Console.WriteLine($"{st.Played.Count} sections, {pcm.Count / 2 / (float)Math.Max(1, rate):0.0} s at {rate} Hz: {string.Join(" ", st.Played.Take(30))}");
    }
}
