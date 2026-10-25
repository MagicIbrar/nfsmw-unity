using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// EA PathFinder 5.01.04 interactive-music map, SOUND\PFDATA\MW_Music.mpf ("PFDx", version 5.1; loaded by
    /// PATH_addmapfile @0x829D20). All little-endian on PC. Header:
    /// <list type="bullet">
    /// <item>+0 'xDFP', +4 u8 major 5, +5 u8 minor 1, +0xC u8 project id, +0xD u8 track count, +0xF u8 event count,
    /// +0x10 u8 remap-list count, +0x11 u8 named-value count, +0x12 u16 node count; +0x14..+0x34 nine section offsets
    /// (kept by the runtime at project +0x1C..+0x3C), +0x38 file size.</item>
    /// <item>s0: u16 per node (n+1 entries): node record offset / 4 (FUN_0082a1d0).</item>
    /// <item>s1 nodes: dw0 {s16 section number (1-based into s8; 0 = marker node, -1 = repeat-counter node, -2 = random
    /// node, -3 = post-event node), bits 16-20 track, 21-26 part, 27-31 repeat count}; dw1 {bits 0-11 remap list
    /// (1-based), 12-16 branch count, 17-19 marker mode (1 = random controller), 20-23 beats per bar, 24-31 bars};
    /// dw2 {u16 root node of the piece, bits 24-27 beat units}; dw3 {bits 0/1 sync flags; event id for -3 nodes};
    /// then branches {s8 min, s8 max, s16 node}: the next node by controller (intensity) value (FUN_0082fe00).</item>
    /// <item>s2: u16 per event: event record offset / 4. s3 events: {u32 queue time, u32 lifetime, u32 action time,
    /// u32 id (24 bits) | action count &lt;&lt; 24, u32 flags (bit 17 disabled)} then 12-byte actions {u32 track mask,
    /// u32 op (bits 8-14 type, 15 done, 16-17 operand A mode, 18-19 operand B mode, 20-22 if-sub-op, 23-25 compare,
    /// 26-28 nesting level), u32 data}, run by FUN_0082efb0.</item>
    /// <item>s4 named values: 20 bytes {char name[16], s32 value} ("rapsheet", "pursuitid", "partnode", "newnode",
    /// "ambstate"). s5: remap lists (u32 index table, then u32 pairs from &lt;&lt; 16 | to). s6/s7: track info
    /// (PATHTRACKINFO: MUS checksum 0x8CA5CEFA). s8 sections: {u32 MUS offset / 128, u32 duration ms}.</item>
    /// </list>
    /// </summary>
    public sealed class PathMap
    {
        public const uint Magic = 0x50464478;
        readonly byte[] _d;
        readonly int[] _s = new int[10];
        public int ProjectId, NumTracks, NumEvents, NumRemaps, NumNamed, NumNodes, NumSections;
        public uint MusChecksum;
        public string[] NamedValues = Array.Empty<string>();

        PathMap(byte[] d) { _d = d; }

        public static PathMap Load(string path) => Read(File.ReadAllBytes(path));

        public static PathMap Read(byte[] d)
        {
            if (d == null || d.Length < 0x48 || BitConverter.ToUInt32(d, 0) != Magic || d[4] != 5 || d[5] != 1) return null;
            var m = new PathMap(d);
            m.ProjectId = d[0xC];
            m.NumTracks = d[0xD];
            m.NumEvents = d[0xF];
            m.NumRemaps = d[0x10];
            m.NumNamed = d[0x11];
            m.NumNodes = BitConverter.ToUInt16(d, 0x12);
            for (int i = 0; i < 10; i++) m._s[i] = BitConverter.ToInt32(d, 0x14 + 4 * i);
            m.NumSections = (m._s[9] - m._s[8]) / 8;
            m.NamedValues = new string[m.NumNamed];
            for (int i = 0; i < m.NumNamed; i++)
            {
                int o = m._s[4] + 20 * i, n = 0;
                while (n < 16 && d[o + n] != 0) n++;
                m.NamedValues[i] = System.Text.Encoding.ASCII.GetString(d, o, n);
            }
            if (m.NumTracks > 0) m.MusChecksum = m.U32(BitConverter.ToInt32(d, m._s[6]) * 4 + 8);
            return m;
        }

        uint U32(int o) => BitConverter.ToUInt32(_d, o);

        // ------------------------------------------------------------------ nodes
        public bool ValidNode(int n) => n >= 0 && n <= NumNodes;
        int NodeAt(int n) => BitConverter.ToUInt16(_d, _s[0] + 2 * n) * 4;
        public uint W(int n, int w) => U32(NodeAt(n) + 4 * w);
        public int Section(int n) => (short)W(n, 0);
        public int Track(int n) => (int)(W(n, 0) >> 16 & 0x1F);
        public int Part(int n) => (int)(W(n, 0) >> 21 & 0x3F);
        public int RepeatCount(int n) => (int)(W(n, 0) >> 27);
        public int RemapIndex(int n) => (int)(W(n, 1) & 0xFFF);
        public int BranchCount(int n) => (int)(W(n, 1) >> 12 & 0x1F);
        public bool RandomMarker(int n) => (W(n, 1) & 0xE0000) == 0x20000;
        public int Beats(int n) => (int)(W(n, 1) >> 20 & 0xF);
        public int Bars(int n) => (int)(W(n, 1) >> 24);
        public int Root(int n) => (int)(W(n, 2) & 0xFFFF);
        public int Units(int n) => (int)(W(n, 2) >> 24 & 0xF);
        public (int min, int max, int node) Branch(int n, int k)
        {
            int o = NodeAt(n) + 16 + 4 * k;
            return ((sbyte)_d[o], (sbyte)_d[o + 1], BitConverter.ToInt16(_d, o + 2));
        }

        /// <summary>Byte offset of a section in the MUS file (FUN_0082f9f0: u32 x 128).</summary>
        public long SectionOffset(int s) => s >= 1 && s <= NumSections ? (long)U32(_s[8] + (s - 1) * 8) * 128 : -1;
        public int SectionDuration(int s) => s >= 1 && s <= NumSections ? (int)U32(_s[8] + (s - 1) * 8 + 4) : 0;

        /// <summary>FUN_0082ff00: the remap list of node <paramref name="from"/> turns <paramref name="to"/> into another node.</summary>
        public int Remap(int from, int to)
        {
            if (from < 0 || !ValidNode(from)) return to;
            int idx = RemapIndex(from);
            if (idx == 0) return to;
            int a = (int)U32(_s[5] + (idx - 1) * 4), b = (int)U32(_s[5] + idx * 4);
            for (int k = 0; k < b - a; k++)
            {
                uint v = U32((a + k) * 4);
                if (to == (int)v >> 16) to = (int)(v & 0xFFFF);
            }
            return to;
        }

        // ------------------------------------------------------------------ events
        public int EventOffset(int e) => BitConverter.ToUInt16(_d, _s[2] + 2 * e) * 4;
        public uint EventId(int e) => U32(EventOffset(e) + 0xC) & 0xFFFFFF;
        public int EventActions(int e) => _d[EventOffset(e) + 0xF];
        public uint EventWord(int e, int w) => U32(EventOffset(e) + 4 * w);
        public uint ActionWord(int e, int k) => U32(EventOffset(e) + 0x14 + 4 * k);

        /// <summary>FUN_0082bfd0: the last event whose id matches under the mask.</summary>
        public int FindEvent(uint id, uint mask)
        {
            for (int e = NumEvents - 1; e >= 0; e--)
                if ((U32(EventOffset(e) + 0xC) & mask & 0xFFFFFF) == (id & mask)) return e;
            return -1;
        }

        public int NamedInit(int i) => i >= 0 && i < NumNamed ? BitConverter.ToInt32(_d, _s[4] + 20 * i + 16) : 0;
    }

    /// <summary>
    /// The EA Trax song list (FEDatabase.cpp:606-636): audiosystem collection 0x7E4B0ED2 → LicensedMusic (an
    /// audiosystem) → PFMapping[] → music collections {SongName, Artist, Album, DefPlay "FE"/"IG"/"AL", PathEvent}.
    /// DefPlay sets the default playlist playability (FE = menus, IG = in game, AL = both).
    /// </summary>
    public sealed class SongInfo
    {
        public string Name, Artist, Album, DefPlay;
        public uint PathEvent;

        public static List<SongInfo> Load(AttribDatabase db)
        {
            var list = new List<SongInfo>();
            if (db == null) return list;
            uint audiosystem = AttribDatabase.Hash("audiosystem");
            if (!db.Collections.TryGetValue((audiosystem, 0x7E4B0ED2u), out var root)) return list;
            var lic = root.Refs("LicensedMusic");
            if (lic.Length == 0 || !db.Collections.TryGetValue(lic[0], out var licensed)) return list;
            foreach (var r in licensed.Refs("PFMapping"))
            {
                if (!db.Collections.TryGetValue(r, out var m)) { list.Add(null); continue; }
                list.Add(new SongInfo
                {
                    Name = m.StringKey("SongName"), Artist = m.StringKey("Artist"), Album = m.StringKey("Album"), DefPlay = m.StringKey("DefPlay"),
                    PathEvent = (uint)m.Int("PathEvent"),
                });
            }
            return list;
        }
    }

    /// <summary>PATHSTATUS (path.h) of the request a stream is playing.</summary>
    public struct PathStatus
    {
        public int PlayingNode, PlayingBar, PlayingBeat;
        public int NodeDuration, BarDuration, BeatDuration, TimeLeftInNode, TimeLeftInBar, TimeLeftInBeat;
    }

    /// <summary>The audio side of a PathFinder track (Path::IPathTrack / PathTrackSndStream, vtable 0x8C5F30).</summary>
    public interface IPathStream
    {
        /// <summary>+0x0C Stop: drop everything queued and playing.</summary>
        void Stop();
        /// <summary>+0x10 Pause.</summary>
        void Pause(bool on);
        /// <summary>+0x1C ReadyForNewRequest: fewer than maxrequests (3) requests queued.</summary>
        bool Ready { get; }
        /// <summary>+0x20 TimeRemaining: ms left of the n-th queued request (0 = the playing one), -1 past the end;
        /// n &lt; 0 = all of them.</summary>
        int TimeRemaining(int n);
        /// <summary>+0x24 TimeBuffered.</summary>
        int TimeBuffered { get; }
        /// <summary>+0x28 Play(node, offset, start beat, hold, duration): queues a MUS section after the others.</summary>
        int Play(int node, long musOffset, int startBeat, int hold, int durationMs);
        /// <summary>+0x30 SetVolume 0..127.</summary>
        void SetVolume(int v);
        /// <summary>The node of the playing request (-1 none), its position and length in ms.</summary>
        int PlayingNode { get; }
        int PlayingPosMs { get; }
    }

    /// <summary>
    /// The PathFinder runtime for one project (EA path library in speed.exe 0x828F90-0x830E30), with its tracks and the
    /// event queue. Service(now) is PATH's periodic service (FUN_0082e510): it runs queued events (FUN_0082c7b0 /
    /// FUN_0082c400, the action interpreter FUN_0082efb0), runs volume fades and pause timers, and keeps each track's
    /// stream fed: when less than the track latency (2500 ms, PATH_createstreamtrack) is queued it advances to the next
    /// node (FUN_00830440): the current node's branch for the controller value (FUN_0082fe00), through the node remap
    /// (FUN_0082ff00) and any marker / counter / random / event nodes (FUN_00830260), and plays it (FUN_0082ff80).
    /// </summary>
    public sealed class PathEngine
    {
        public readonly PathMap Map;
        public readonly uint ProjectBits;
        public uint Now { get; private set; }
        public readonly int[] Named;
        readonly bool[] _disabled;
        readonly Random _rng = new Random();

        /// <summary>SongProgressCallback(projectbits, node): a marker node was passed (node &lt; 0 = the piece ended).</summary>
        public Action<uint, int> Progress;
        /// <summary>EventActionCallback(trackhandle, id, parm) from action 0xD.</summary>
        public Action<uint, int, int> ActionCallback;

        public sealed class Track
        {
            public int Id, Ctrl, Scale = 100, Vol = 127, Node = -1, RepeatNode = -1, RepeatCount, Dry, Fx;
            public int Latency = 2500, FxBus;
            public bool Paused, Locked, Voice0 = true, HasRoot;
            public int Root = -1;
            public uint PauseAt, ResumeAt;
            // volume fade (+0x10..+0x1C)
            public uint FadeStart, FadeTime;
            public int FadeFrom = -1, FadeTo = -1, FadeCurve = -1;
            public IPathStream Stream;
            public uint Handle;
        }

        sealed class QEvent
        {
            public int Index, Count;
            public uint Queued, Lifetime, ActTime, Flags;
            public uint[] A;   // 3 words per action
        }

        public readonly Track[] Tracks;
        readonly List<QEvent> _queue = new List<QEvent>();

        public PathEngine(PathMap map)
        {
            Map = map;
            ProjectBits = 0x10000000u | 0x01000000u << map.ProjectId;
            Named = new int[map.NumNamed];
            for (int i = 0; i < Named.Length; i++) Named[i] = map.NamedInit(i);
            _disabled = new bool[map.NumEvents];
            Tracks = new Track[Math.Max(1, map.NumTracks)];
            for (int i = 0; i < Tracks.Length; i++) Tracks[i] = new Track { Id = i, Handle = ProjectBits | 1u << i };
        }

        public Track TrackFor(uint handle)
        {
            for (int i = 0; i < Tracks.Length; i++) if ((handle & 1u << i) != 0) return Tracks[i];
            return Tracks[0];
        }

        int Rand() => _rng.Next(0x8000);

        // ------------------------------------------------------------------ API (path.h)
        /// <summary>PATH_event @0x82C490: queue the event with this id (not when disabled).</summary>
        public int Event(uint id)
        {
            int e = Map.FindEvent(id & 0xFFFFFF, 0xFFFFFFFF);
            if (e < 0) return -43;
            if (_disabled[e]) return -8;
            return Post(e);
        }

        /// <summary>FUN_0082c1d0 + FUN_0082c020: copy the event into the queue (max 16) and number the if-nesting.</summary>
        int Post(int e)
        {
            if (_queue.Count >= 16) return -13;
            int n = Map.EventActions(e);
            var q = new QEvent { Count = n, A = new uint[n * 3], Lifetime = Map.EventWord(e, 1), Flags = Map.EventWord(e, 4) & ~0xFFu };
            for (int k = 0; k < n * 3; k++) q.A[k] = Map.ActionWord(e, k);
            int depth = 0, last = -1;
            for (int k = 0; k < n; k++)
            {
                uint a1 = q.A[k * 3 + 1];
                int level = depth, next = depth;
                if ((a1 & 0x7F00) == 0x100)
                {
                    switch (a1 >> 20 & 7)
                    {
                        case 0: case 1: last++; next = depth + 1; break;
                        case 2: case 3: level = last; break;
                        case 4: next = depth - 1; last--; level = next; break;
                        default: continue;
                    }
                }
                q.A[k * 3 + 1] = (a1 & ~0x1C000000u) | ((uint)level & 7) << 26;
                depth = next;
            }
            q.Queued = q.ActTime = Now;
            _queue.Add(q);
            return 0;
        }

        /// <summary>PATH_clearallevents @0x82C730.</summary>
        public void ClearAllEvents() => _queue.Clear();

        /// <summary>PATH_control @0x828F90: the controller (intensity) 0..127.</summary>
        public void Control(int tracks, int value) { if (value < 0x80) foreach (var t in Sel(tracks)) t.Ctrl = value; }

        /// <summary>PATH_volume @0x82D560: the track scale 0..100.</summary>
        public void Volume(int tracks, int scale)
        {
            scale = Math.Max(0, Math.Min(100, scale));
            foreach (var t in Sel(tracks)) { t.Scale = scale; if (t.FadeTo < 0) ApplyVolume(t); }
        }

        /// <summary>PATH_stop @0x829310.</summary>
        public void Stop(int tracks) { foreach (var t in Sel(tracks)) { t.Stream?.Stop(); t.Node = -1; } }

        /// <summary>PATH_pause @0x829140.</summary>
        public void Pause(int tracks, bool on) { foreach (var t in Sel(tracks)) { t.Stream?.Pause(on); t.Paused = on; } }

        /// <summary>PATH_setnamedvalue (@0x829640, case-insensitive).</summary>
        public void SetNamedValue(string name, int value)
        {
            for (int i = 0; i < Named.Length; i++) if (string.Equals(Map.NamedValues[i], name, StringComparison.OrdinalIgnoreCase)) Named[i] = value;
        }

        IEnumerable<Track> Sel(int tracks)
        {
            for (int i = 0; i < Tracks.Length; i++) if (((uint)tracks & 1u << i) != 0) yield return Tracks[i];
        }

        void ApplyVolume(Track t) => t.Stream?.SetVolume(t.Vol * t.Scale / 100);

        int Buffered(Track t)
        {
            if (t.Stream == null) return 0;
            int sum = 0;
            for (int i = 0; ; i++) { int r = t.Stream.TimeRemaining(i); if (r < 0) break; sum += r; }
            return sum;
        }

        /// <summary>PATH_trackstatus / FUN_0082e980: 1 stopped, 2 queueing, 3 paused, 4 ready for next, 5 playing, 6 fading.</summary>
        public int TrackStatus(Track t)
        {
            if (t.Paused) return 3;
            if (t.Node < 0) return 1;
            int s = 5;
            if (t.Stream != null && t.Stream.Ready && Buffered(t) <= t.Latency) s = 4;
            else if (t.Stream != null && t.Stream.TimeRemaining(-1) > t.Stream.TimeBuffered) s = 2;
            if (t.FadeTo >= 0) s = 6;
            return s;
        }

        /// <summary>The playing request's position as PATHSTATUS (the stream imp's GetPathStatus @0x82AF20) [?] bars/beats
        /// evenly divide the section.</summary>
        public PathStatus Status(Track t)
        {
            var st = new PathStatus { PlayingNode = -1 };
            if (t.Stream == null) return st;
            int n = t.Stream.PlayingNode;
            st.PlayingNode = n;
            if (n < 0 || !Map.ValidNode(n)) return st;
            int dur = Map.SectionDuration(Map.Section(n)), bars = Math.Max(1, Map.Bars(n)), beats = Math.Max(1, Map.Beats(n));
            int pos = Math.Min(dur, t.Stream.PlayingPosMs);
            st.NodeDuration = dur;
            st.BarDuration = dur / bars;
            st.BeatDuration = dur / (beats * bars);
            if (st.BarDuration > 0) { st.PlayingBar = pos / st.BarDuration; st.TimeLeftInBar = st.BarDuration - pos % st.BarDuration; }
            if (st.BeatDuration > 0) { st.PlayingBeat = pos % Math.Max(1, st.BarDuration) / st.BeatDuration; st.TimeLeftInBeat = st.BeatDuration - pos % st.BeatDuration; }
            st.TimeLeftInNode = dur - pos;
            return st;
        }

        // ------------------------------------------------------------------ service
        /// <summary>FUN_0082e510 at <paramref name="nowMs"/>.</summary>
        public void Service(uint nowMs)
        {
            Now = nowMs;
            RunEvents();
            foreach (var t in Tracks)
            {
                if (t.Stream == null) continue;
                bool playing = t.Node >= 0 && t.HasRoot;
                if (playing && t.FadeTo >= 0) UpdateFade(t);
                if (t.PauseAt != 0 && t.PauseAt <= Now) { t.Stream.Pause(true); t.Paused = true; t.PauseAt = 0; }
                if (t.ResumeAt != 0 && t.ResumeAt <= Now) { t.Stream.Pause(false); t.Paused = false; t.ResumeAt = 0; }
                if (playing && !t.Paused && t.Stream.Ready && Buffered(t) < t.Latency) Advance(t);
            }
            for (int guard = 0; guard < 16 && RunEvents() != 0; guard++) { }
        }

        /// <summary>FUN_0082d820: the volume fade (curve 1 linear, 2 quadratic, 3 0.04/x) [?] direction.</summary>
        void UpdateFade(Track t)
        {
            float f = t.FadeTime > 0 ? (Now - t.FadeStart) / (float)(t.FadeTime & 0xFFFF) : 1f;
            if (f >= 1f)
            {
                t.Vol = t.FadeTo;
                t.FadeFrom = t.FadeTo = t.FadeCurve = -1;
                ApplyVolume(t);
                return;
            }
            float g = t.FadeCurve switch { 2 => 1f - f * f, 3 => f > 0 ? Math.Min(1f, 0.04f / f) : 1f, _ => 1f - f };
            int v = (int)(t.FadeTo + (t.FadeFrom - t.FadeTo) * g);
            t.Stream.SetVolume(v * t.Scale / 100);
        }

        /// <summary>FUN_0082c7b0: run each queued event until it blocks; finished / expired events leave.</summary>
        int RunEvents()
        {
            int progress = 0;
            for (int i = 0; i < _queue.Count; i++)
            {
                var q = _queue[i];
                bool done = q.Index >= q.Count || (q.Lifetime != 0 && q.Queued + q.Lifetime < Now);
                if (!done) { progress += RunActions(q); done = q.Index >= q.Count; }
                if (done && _queue.Contains(q)) { _queue.Remove(q); i--; }
            }
            return progress;
        }

        /// <summary>FUN_0082c400.</summary>
        int RunActions(QEvent q)
        {
            if (Now < q.ActTime) return 0;
            int n = 0;
            while (q.Index < q.Count)
            {
                int k = q.Index;
                if ((q.A[k * 3 + 1] & 0x8000) == 0 && !Exec(q, k)) return n;
                if (q.Index != k) continue;   // a loop jumped back
                n++;
                q.Index++;
                q.ActTime = Now;
            }
            return n;
        }

        // ------------------------------------------------------------------ values (FUN_0082ea50 / FUN_0082ed40)
        int Get(int v, int mode, Track t, QEvent q)
        {
            if (mode == 2) return v >= 0 && v < Named.Length ? Named[v] : 0;
            if (mode != 1) return v;
            var st = Status(t);
            switch (v)
            {
                case 1: return t.Ctrl;
                case 2: return t.Node;
                case 3: return t.Node >= 0 && Map.ValidNode(t.Node) ? Map.Root(t.Node) : -1;
                case 4: return t.Node >= 0 && Map.ValidNode(t.Node) ? Map.Part(t.Node) : -1;
                case 5: return q != null ? (int)q.Lifetime : 0;
                case 6: return q != null ? (int)(q.Flags << 16) >> 28 : 0;
                case 7: return t.FxBus;
                case 8: return t.Dry;
                case 9: return t.Fx;
                case 10: return t.Voice0 ? 1 : 0;
                case 11: return Resolve(t.Node, Next(t.Node, t.Ctrl, false), t.Ctrl, false);
                case 12: return t.Locked ? 1 : 0;
                case 13: return st.NodeDuration;
                case 14: return t.Paused ? 1 : 0;
                case 15: return 0x1000;
                case 16: return st.PlayingNode;
                case 17: return TrackStatus(t);
                case 18: return Rand() / 0x17 & 0xFFFF;
                case 19: return (int)Now;
                case 20: return st.TimeLeftInBeat;
                case 21: return st.TimeLeftInBar;
                case 22: return st.TimeLeftInNode;
                case 23: return t.Vol;
                case 24: return 0x1000;
                case 25: return st.BarDuration;
                case 26: return st.BeatDuration;
            }
            return v;
        }

        void Set(int idx, int mode, int value, Track t, QEvent q)
        {
            if (mode == 2) { if (idx >= 0 && idx < Named.Length) Named[idx] = value; return; }
            if (mode != 1) return;
            switch (idx)
            {
                case 1: t.Ctrl = value; break;
                case 5: if (q != null) q.Lifetime = (uint)value; break;
                case 7: t.FxBus = value & 7; break;
                case 8: t.Dry = Math.Max(0, Math.Min(127, value)); break;
                case 9: t.Fx = Math.Max(0, Math.Min(127, value)); break;
                case 12: t.Locked = value != 0; break;
                case 14: t.Paused = value != 0; t.Stream?.Pause(t.Paused); break;
                case 23:
                    t.Vol = Math.Max(0, Math.Min(127, value));
                    t.FadeFrom = t.FadeTo = t.FadeCurve = -1;
                    ApplyVolume(t);
                    break;
            }
        }

        // ------------------------------------------------------------------ sequencing
        /// <summary>FUN_0082fe00: the branch of node <paramref name="n"/> for controller <paramref name="ctrl"/> (a counter
        /// node uses its remaining count instead), else the nearest range (FUN_0082fd90).</summary>
        int Next(int n, int ctrl, bool adv)
        {
            if (n < 0 || !Map.ValidNode(n)) return -1;
            var t = Tracks[Math.Min(Tracks.Length - 1, Map.Track(n))];
            if (t.RepeatNode == n)
            {
                int old = t.RepeatCount;
                ctrl = old & 0x7F;
                if ((int)(Map.W(n, 0) & 0xF8000000) > 0 && adv)
                {
                    t.RepeatCount = old - 1;
                    if (old == 0) t.RepeatNode = -1;
                }
            }
            int cnt = Map.BranchCount(n);
            for (int k = 0; k < cnt; k++)
            {
                var b = Map.Branch(n, k);
                if (ctrl >= b.min && ctrl <= b.max) { if (b.node >= 0) return b.node; break; }
            }
            if (cnt == 0) return -1;
            int best = 0, bestD = 0x7F;
            for (int k = 0; k < cnt; k++)
            {
                var b = Map.Branch(n, k);
                int d = Math.Min(Math.Abs(b.min - ctrl), Math.Abs(b.max - ctrl));
                if (d < bestD) { bestD = d; best = k; }
            }
            return Map.Branch(n, best).node;
        }

        /// <summary>FUN_00830260: from <paramref name="to"/> (remapped by <paramref name="from"/>) through control nodes to
        /// the next node with audio.</summary>
        int Resolve(int from, int to, int ctrl, bool adv)
        {
            if (to < 0) return -1;
            int n = Map.Remap(from, to);
            while (true)
            {
                if (n < 0 || !Map.ValidNode(n)) return -1;
                int sec = Map.Section(n);
                if (sec > 0) return Map.Remap(from, n);
                int c = ctrl;
                if (adv)
                {
                    var t = Tracks[Math.Min(Tracks.Length - 1, Map.Track(n))];
                    if (sec == 0)
                    {
                        if (Map.RandomMarker(n)) c = Rand() & 0x7F;
                        Progress?.Invoke(ProjectBits, n);
                    }
                    else if (sec == -1)
                    {
                        if ((Map.W(n, 0) & 0xF8000000) != 0 && n != t.RepeatNode) { t.RepeatCount = Map.RepeatCount(n); t.RepeatNode = n; }
                    }
                    else if (sec == -2) c = Rand() & 0x7F;
                    else if (sec == -3)
                    {
                        int e = Map.FindEvent(Map.W(n, 3) & 0xFFFFFF, 0xFFFFFFFF);
                        if (e >= 0) Post(e);
                    }
                }
                n = Next(n, c, adv);
                if (n < 0) break;
                n = Map.Remap(from, n);
                if (n < 0) break;
            }
            if (adv && from >= 0) Progress?.Invoke(ProjectBits, n);
            return -1;
        }

        /// <summary>FUN_00830440: queue the node after the last queued one.</summary>
        void Advance(Track t)
        {
            int next = Next(t.Node, t.Ctrl, true);
            if (next < 0) Progress?.Invoke(ProjectBits, next);
            else next = Resolve(t.Node, next, t.Ctrl, true);
            t.Node = next;
            PlayNode(t);
        }

        /// <summary>FUN_0082ff80: play the track's node (whole section, start beat 0).</summary>
        int PlayNode(Track t)
        {
            if (t.Node < 0 || !Map.ValidNode(t.Node))
            {
                t.HasRoot = false;
                t.Node = -1;
                return 0;
            }
            t.Root = Map.Root(t.Node);
            t.HasRoot = true;
            int s = Map.Section(t.Node);
            return t.Stream != null ? t.Stream.Play(t.Node, Map.SectionOffset(s), 0, 0, Map.SectionDuration(s)) : 0;
        }

        // ------------------------------------------------------------------ actions (FUN_0082efb0)
        bool Exec(QEvent q, int k)
        {
            uint a0 = q.A[k * 3];
            for (int ti = 0; ti < Tracks.Length && ti < 24; ti++)
            {
                if ((a0 & 1u << ti) == 0) continue;
                var t = Tracks[ti];
                if (t.Stream == null) continue;
                if ((a0 >> 28 & 1) != 0 && !t.Voice0) continue;
                ExecOn(q, k, t);
                if (q.Index != k) return false;
            }
            return (q.A[k * 3 + 1] & 0x8000) != 0;
        }

        void Done(QEvent q, int k) => q.A[k * 3 + 1] |= 0x8000;

        void ExecOn(QEvent q, int k, Track t)
        {
            uint op = q.A[k * 3 + 1], data = q.A[k * 3 + 2];
            int mA = (int)(op >> 16 & 3), mB = (int)(op >> 18 & 3);
            short lo = (short)(data & 0xFFFF);
            int hi = (int)data >> 16;
            switch (op >> 8 & 0x7F)
            {
                case 1: If(q, k, t, op, data, mA, mB); return;
                case 2:   // WAIT ms since the previous action
                {
                    int ms = Get(lo, mB, t, q);
                    if (hi > 0 && hi < ms) ms = 0;
                    if (ms <= (int)(Now - q.ActTime)) Done(q, k);
                    return;
                }
                case 3:   // SYNC to a beat/bar subdivision
                {
                    if (lo == 0)
                    {
                        var st = Status(t);
                        if (st.PlayingNode < 0) { Done(q, k); return; }
                        int wait = SyncWait((int)(data >> 16 & 0xF), 1 << (int)(data >> 20 & 0xF), (int)(data >> 24 & 0xF), st);
                        q.A[k * 3 + 2] = (data & 0xFFFF0000u) | (uint)(Math.Max(1, wait) & 0xFFFF);
                        q.ActTime = Now;
                    }
                    else if (lo <= (int)(Now - q.ActTime)) { Done(q, k); q.A[k * 3 + 2] |= 0xFFFF; }
                    return;
                }
                case 4: Branch(q, k, t, data, mA, mB); return;
                case 5:   // volume fade: target data&0xFF over B ms, curve bits 8-14
                    t.FadeStart = Now;
                    t.FadeTime = (uint)Get(hi, mB, t, q);
                    t.FadeFrom = t.Vol;
                    t.FadeTo = (sbyte)(data & 0xFF);
                    t.FadeCurve = (int)(data << 17) >> 25;
                    t.Vol = t.FadeTo;
                    Done(q, k);
                    return;
                case 6: case 7: case 0x10: case 0x11: case 0x12:   // FX / dry fades, bank loads, pitch / stretch: not rendered
                    Done(q, k);
                    return;
                case 8: Set((int)(data >> 16 & 0xFF), mA, Get(lo, mB, t, q), t, q); Done(q, k); return;
                case 9:
                {
                    uint id = data & 0xFFFFFF;
                    uint v = (uint)Get((int)id, mA, t, q);
                    int e = Map.FindEvent(v, v == id ? 0xFFFFFFFF : 0xFFFF);
                    if (e >= 0) Post(e);
                    Done(q, k);
                    return;
                }
                case 10: case 11: case 12:
                    if ((data & 0xFFFFFF) == 0) Array.Clear(_disabled, 0, _disabled.Length);
                    else { int e = Map.FindEvent(data & 0xFFFFFF, 0xFFFFFFFF); if (e >= 0) _disabled[e] = (op >> 24 & 1) != 0; }
                    Done(q, k);
                    return;
                case 0xD:
                    ActionCallback?.Invoke(t.Handle, Get(hi, mB, t, q), Get(lo, mA, t, q));
                    Done(q, k);
                    return;
                case 0xE:
                {
                    int idx = (int)(data & 0xFF);
                    int a = Get(idx, mA, t, q), b = Get(hi, mB, t, q), r = a;
                    switch (data >> 8 & 0xFF)
                    {
                        case 1: r = a + b; break;
                        case 2: r = a - b; break;
                        case 3: r = a * b; break;
                        case 4: r = b != 0 ? a / b : a; break;
                        case 5: r = b != 0 ? a % b : a; break;
                    }
                    Set(idx, mA, r, t, q);
                    Done(q, k);
                    return;
                }
                case 0xF:
                {
                    int ms = Get((int)(data & 0xFFFF), mB, t, q);
                    if (ms < 1) { t.Paused = (data >> 16 & 1) != 0; t.Stream.Pause(t.Paused); }
                    else if ((data & 0x10000) == 0) t.ResumeAt = Now + (uint)ms;
                    else t.PauseAt = Now + (uint)ms;
                    Done(q, k);
                    return;
                }
                default: Done(q, k); return;
            }
        }

        /// <summary>FUN_0082faf0: ms until the next sync point (every a x units/b beats from offset d) in the bar.</summary>
        int SyncWait(int a, int b, int d, PathStatus st)
        {
            int units = Map.ValidNode(st.PlayingNode) ? Map.Units(st.PlayingNode) : 0;
            if (units == 0) units = 1;   // [ours] a zero unit count would never advance
            float f = units / (float)b;
            int bar = st.BarDuration, elapsed = bar - st.TimeLeftInBar;
            int x = (int)(d * f * st.BeatDuration), step = (int)(st.BeatDuration * f * a), p = x;
            if (step > 0 && x < elapsed && x < bar)
                do { p += step; } while (p <= elapsed && p < bar);
            if (p > bar) p = bar + x;
            return p - elapsed;
        }

        /// <summary>Action 1: IF / ELSEIF / ELSE / ENDIF / WHILE (FUN_0082e850 skips, FUN_0082e8e0 loops).</summary>
        void If(QEvent q, int k, Track t, uint op, uint data, int mA, int mB)
        {
            uint sub = op >> 20 & 7;
            if (sub == 3) { Done(q, k); return; }
            if (sub == 4)
            {
                Done(q, k);
                LoopBack(q, k);
                return;
            }
            int a = Get((short)(data & 0xFFFF), mA, t, q), b = Get((int)data >> 16, mB, t, q);
            bool r;
            switch (op >> 23 & 7)
            {
                case 1: r = a == b; break;
                case 2: r = a != b; break;
                case 3: r = b < a; break;
                case 4: r = a < b; break;
                case 5: r = b <= a; break;
                case 6: r = a <= b; break;
                default: r = true; break;
            }
            if (mA == 1 && (short)(data & 0xFFFF) == 0x11 && b == 4 && TrackStatus(t) == 1) r = true;
            if (sub == 0)
            {
                // WHILE: false skips to past the ENDIF; true runs the body, the ENDIF jumps back
                if (!r) { Skip(q, k, false); Done(q, k); }
                else q.A[k * 3 + 1] &= ~0x8000u;
                return;
            }
            Skip(q, k, r);
            Done(q, k);
        }

        int Level(QEvent q, int k) => (int)(q.A[k * 3 + 1] >> 26 & 7);
        bool IsIf(QEvent q, int k) => (q.A[k * 3 + 1] & 0x7F00) == 0x100;
        uint Sub(QEvent q, int k) => q.A[k * 3 + 1] >> 20 & 7;

        /// <summary>FUN_0082e850: after an IF that evaluated <paramref name="taken"/>, mark the actions that must not run.</summary>
        void Skip(QEvent q, int k, bool taken)
        {
            int lvl = Level(q, k);
            bool skipDeeper = !taken;
            for (int j = k + 1; j < q.Count; j++)
            {
                if (Level(q, j) == lvl)
                {
                    if (IsIf(q, j) && Sub(q, j) == 4) { Done(q, j); return; }
                    if (!taken) return;
                    skipDeeper = true;
                    Done(q, j);
                }
                else if (skipDeeper) Done(q, j);
            }
        }

        /// <summary>FUN_0082e8e0: an ENDIF whose IF is a WHILE jumps back to it.</summary>
        void LoopBack(QEvent q, int k)
        {
            int lvl = Level(q, k);
            for (int j = k - 1; j >= 0; j--)
            {
                if (Level(q, j) != lvl || !IsIf(q, j)) continue;
                if (Sub(q, j) != 0) return;
                for (int m = j; m <= k; m++) q.A[m * 3 + 1] &= ~0x8000u;
                q.Index = j;
                q.ActTime = Now + 1;
                return;
            }
        }

        /// <summary>Action 4 BRANCHTO: flush (data bit 24) stops the stream now; the node (A) or the A-th marker of part
        /// B is resolved from the queued node and played after what is queued (blocks while the stream is full).</summary>
        void Branch(QEvent q, int k, Track t, uint data, int mA, int mB)
        {
            int target = Get((short)(data & 0xFFFF), mA, t, q);
            int part = Get((int)(data << 8) >> 24, mB, t, q);
            if (target < -1 || target > Map.NumNodes) target = -1;
            if (target >= 0 && part >= 0)
            {
                int found = -1, left = target;   // the target-th (1-based) marker node of the part
                for (int i = 0; i < Map.NumNodes; i++)
                    if (Map.Part(i) == part && Map.Section(i) == 0 && --left == 0) { found = i; break; }
                if (found < 0) { Done(q, k); return; }   // [ours] the exe leaves the action pending forever
                target = found;
            }
            if (target >= 0 && Map.Track(target) != t.Id) { Done(q, k); return; }
            if (t.Locked) { Done(q, k); return; }
            if ((data & 0x1000000) != 0) { t.Stream.Stop(); t.Node = -1; }
            if (!t.Stream.Ready) return;
            t.Node = Resolve(t.Node, target, t.Ctrl, true);
            PlayNode(t);
            Done(q, k);
        }
    }
}
