using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NfsMw.Core
{
    /// <summary>
    /// One recorded take chosen by <see cref="SpeechScript.Choose"/>: what SPCH hands the game's SampleRequestCallback
    /// (SPCHType_SampleRequestData {bankNum, sampleOffset, numBytes, eventSpec, channel, subID, datID, interruptFlag}).
    /// </summary>
    public struct SpeechSample
    {
        /// <summary>.idx entry index (= SPCH bank handle, = <see cref="CopSpeech.Lines"/> index).</summary>
        public int Line;
        /// <summary>Sample (take) index inside the bank.</summary>
        public int Take;
        /// <summary>Absolute offset of the take's SCHl stream in &lt;base&gt;.big (bank .big offset + sampleOffset).</summary>
        public long BigOffset;
        /// <summary>Byte length of the take in the .big (next cue - this cue, in bank units).</summary>
        public int NumBytes;
        /// <summary>Bank header id (u16 +0) and sub id (u16 +2; the speaker_id for per-speaker banks, -1 if none).</summary>
        public int BankId, Speaker;
        /// <summary>Slot index in the chosen sentence.</summary>
        public int Slot;
        /// <summary>interruptFlag: set on the first sample when the event record has flag 0x20.</summary>
        public bool Interrupt;
        public override string ToString() => $"{Line}:{Take}";
    }

    /// <summary>
    /// EA "SPCH" speech-event scripts (SOUND\SPEECH\copspeech.evt/.csi/.idx/.big, SOUND\STREAMS\NISAudio.*) and an
    /// evaluator that picks the sentence (sequence of recorded takes) like the SPCH library statically linked into
    /// speed.exe (PC 1.3) does. Reversed functions: SPCH_Choose 0x8324DD, SPCH_Play 0x8325C8, SPCH_AddBank 0x832ACE,
    /// SPCH_AddEventDB 0x832CE2 (-&gt; 0x832C64 check/register, 0x83493F Csis client hookup, 0x8348DA Csis client = AddEvent),
    /// AddEvent 0x832289/0x83215E, event queue pick 0x832336, sentence choose 0x834314 (via 0x834519 + reparm retry),
    /// rule eval 0x834B82/0x834D07, weighted order 0x8335F7 (weights 0x8335D2, table @0x90D250), slot build 0x8338C4,
    /// bank resolve 0x833169 (searches 0x8327FF/0x832853), take collect 0x833382 (attr test 0x833087, repeat ring
    /// 0x8329BE/0x83298B), take pick 0x833C45 (+0x833A21/0x833AE7/0x833784 var path), play 0x834169 (sample requests
    /// 0x833DD7, offsets 0x8349FA), keyed anti-repeat random 0x8346FF.
    /// <para><b>.idx</b> (CLUMP_IDX_FILE, Clump.h): u32 numtypes (1), u32 numbanks[20], u32 count @0x54, then
    /// 16-byte CLUMP_ITEMs @0x58 {u32 key = type&lt;&lt;24 | n, u32 header size, u32 header offset in the .idx, u32 bank
    /// offset in the .big}. GameSpeech::LoadBanks hands each header to SPCH_AddBank in key order, so the returned bank
    /// handle = item index ("line"); the game's SampleRequestCallback reads big offset = item.sample + sampleOffset.</para>
    /// <para><b>Bank header</b> (SPCH_AddBank keeps a table sorted by (id, sub)): u16 id, u16 sub (speaker_id for
    /// per-speaker banks, ffff none), u8 nAttr | 0x80 "partitioned", u8 nSamples, u8 repeat-ring size, u8 unit-1
    /// (offsets are in (unit)*0x100 bytes), u16 end (bank length in units), u16 nGroups (mode-2 sub banks, 0 here);
    /// @0x0C nSamples x {u16 BE cue, u8 attr[nAttr]} (attr = bit index of the take's value, e.g. car_color Red = 0);
    /// then aligned u32 attrMask[nAttr] (union of the takes' attrs), u8 nPartitions (+ ceil(n/8) enable bitmap if 0x80;
    /// at AddBank partition seed % n is enabled, takes are disabled once played [0x832A5C, flag @0x90D24C = 1]),
    /// u8 ringPos + u8 ring[size] (recently played takes, initialised ff), then mode-2 group bitmaps.</para>
    /// <para><b>.evt</b> (event DB): u8 3, u8 0x12, u16 ?, u32 Csis table offset, u8 projectID, u8 datID, u8 ?, u8
    /// registered(runtime), u32 crc?, u16 nEvents @0x10, u8 nGlobalVars, u8 0, u16 duration filter (100), u16 low
    /// priority limit (500), u16 recordOffset[nEvents]/4 @0x18, aligned u8 globalVars[] (ff). At the Csis table offset:
    /// nEvents x 0x20 {u32 name offset (from the end of this table), u16 systemCrc 0x5BA7, u16 interfaceCrc, 24 bytes
    /// runtime FunctionClient}, then the names. Entry i names record i; AddEventDB subscribes a Csis client per entry
    /// whose call copies the event's Csis struct (record +0x0B u32 fields) behind the EventSpec into params[1..N].</para>
    /// <para><b>Event record</b>: u16 eventID, u16 expiry ms, u16 priority, u8 nSentences, u8 nRules, u8 maxRepeat |
    /// nParamTests&lt;&lt;4, u8 probability %, u8 flags (8 = presence mask, 0x10 = follow-up list, 0x20 = interrupt,
    /// 1 = duration filter, 2/4 = queue), u8 nParams, u16 sentenceOffset[]/4, aligned rules {u8 ruleId, u8 paramIndex,
    /// u8 type}[nRules], aligned rule value/care bitsets [nSentences][ceil(nRules/8)] x2 (BE, rule 0 = bit 31),
    /// param tests {u8, u8 paramIndex, u8}[n] (the rule and test blocks advance by ((n + 1) x 3) &amp; ~3, 0x831BEB/0x832FD4),
    /// [flag 8: u32 2-bit presence spec + u16 mask per sentence],
    /// [flag 0x10: u8 n, pad, u16 eventIDs]. The copspeech/NISAudio data use none of rules/tests/flags/vars [?untested].</para>
    /// <para><b>Sentence</b> (record + off*4): u8 weight (w&amp;31 * 4^(w&gt;&gt;5)), u8 probability %, u8 nSlots&lt;&lt;2 |
    /// filter-mode restriction, u8 nVars, u8 nParamMasks, 3 x u8 ?, u8 slotOffset[nSlots]/4 (aligned), u32
    /// paramMask[nParamMasks], u32 vars[nVars] (runtime). <b>Slot</b> (sentence + off*4): u16 bankId, u8 paramIndex,
    /// u8 mode (0 bank by id, 1 by (id, params[paramIndex]) = per-speaker bank, 2 by id + group params[paramIndex]),
    /// i8 nEntries, 3 x u8 ?, entries {u32 allowedAttrMask, u8 kind (0 any, ff read var, fe set var, else param index:
    /// take attr bit must be set in params[kind]), u8 rule index (ff), u8 var index (ff; bit 7 = sentence-local), u8 0}
    /// one per bank attr.</para>
    /// <para><b>Choose</b> (0x834314): reject if the same event already played maxRepeat times in a row; evaluate rules
    /// through TestSentenceRule (game: always 0 = false); order sentences by weighted random without replacement
    /// (weight-0 sentences last); take the first sentence that passes its probability roll, param masks, rule bitsets,
    /// presence mask, and whose every slot resolves a bank and has at least one matching take. Per slot the candidates
    /// are the matching takes not in the bank's repeat ring (else the least recently played one), and one is drawn with
    /// the keyed anti-repeat random (key = bank). Playing pushes each take into its bank's ring.</para>
    /// <para>Game-side callbacks (Speech::Manager, SpeechManager.cpp): TestSentenceRuleCallback returns 0,
    /// SetSentenceRuleCallback does nothing, EventRuleCallback returns kSPCH_EventRule_OK, ReparmCallback returns 0
    /// (= one retry of the choose). These are the defaults of the hooks below. SPCH_SetFilter is never called, so the
    /// channel filter mode is 0 (duration filter off, sentences with mode restriction 1/3 never chosen).</para>
    /// <para>The .csi ('MOIR', u16 2, u16 1, u32 count @8, u16 systemCrc @0x10, 12-byte entries @0x2C {name offset,
    /// u32 interfaceCrc, 0} sorted by crc) is the Csis interface list the game subscribes; it carries no parameter
    /// layout, so parameter names come from the generated COPSPEECH.hpp / NISAudio.hpp structs embedded below.</para>
    /// <para>Not modelled: the per-channel event queue (expiry/priority/follow-ups, 0x832336) and the game's exact
    /// RNG (an additive 6-word generator 0x83458F seeded from the game seed) - System.Random is used instead.</para>
    /// </summary>
    public sealed class SpeechScript
    {
        // ---------------------------------------------------------------- decoded data
        public sealed class Bank
        {
            public int Line, Id, Sub, NumAttrs, RingSize, UnitBytes, End, Groups, Partitions;
            public bool Partitioned;
            public long BigOffset;
            public int[] Cue;            // per take, in units
            public byte[][] Attrs;       // per take
            public uint[] AttrMasks;
            internal byte[] Ring; internal int RingPos;
            internal byte[] Enabled;     // partition bitmap (Partitioned only)
            internal byte[] GroupBits;   // mode-2 group bitmaps
            public int TakeCount => Cue.Length;
            public long TakeOffset(int take, int group) => (long)Cue[take] * UnitBytes + (group >= 0 ? (long)UnitBytes * End * group : 0);
            public int TakeBytes(int take) => ((take + 1 < Cue.Length ? Cue[take + 1] : End) - Cue[take]) * UnitBytes;
        }

        public struct Entry { public uint Mask; public byte Kind, Rule, Var; }
        public sealed class Slot { public int BankId, ParamIndex, Mode; public Entry[] Entries; }
        public sealed class Sentence { public int RawWeight, Weight, Probability, ModeRestrict, NumVars; public uint[] ParamMasks; public Slot[] Slots; }
        public struct Rule { public byte RuleId, ParamIndex, Type; }

        public sealed class Event
        {
            public string Name;
            public int Index, Id, Expiry, Priority, MaxRepeat, Probability, Flags, NumParams, InterfaceCrc;
            public Rule[] Rules;
            public uint[] RuleValue, RuleCare;   // per sentence, rule 0 = bit 31
            public int[] ParamTests;             // param index per test
            public uint PresenceSpec;
            public ushort[] PresenceMask;
            public ushort[] FollowUps;
            public Sentence[] Sentences;
            public string[] ParamNames, ParamTypes;
        }

        public readonly List<Event> Events = new List<Event>();
        public readonly List<Bank> Banks = new List<Bank>();      // by line
        public IReadOnlyList<string> EventNames => _names;
        public int ProjectId, DatId, DurationFilter;
        public string BigPath;

        readonly List<string> _names = new List<string>();
        readonly Dictionary<string, Event> _byName = new Dictionary<string, Event>(StringComparer.OrdinalIgnoreCase);
        readonly List<Bank> _sorted = new List<Bank>();           // SPCH bank table, sorted by (id, sub)
        byte[] _globalVars = new byte[0];

        // ---------------------------------------------------------------- game callbacks (defaults = Speech::Manager)
        /// <summary>(eventId, ruleId, parmValue, datId) -&gt; &gt;0 true, 0 false, &lt;0 unknown. Game: always 0.</summary>
        public Func<int, int, int, int, int> TestSentenceRule = (e, r, v, d) => 0;
        /// <summary>(eventId, ruleId, parmValue, datId), called on play. Game: no-op.</summary>
        public Action<int, int, int, int> SetSentenceRule;
        /// <summary>(eventId) -&gt; false drops the event (kSPCH_EventRule_Delete). Game: always OK.</summary>
        public Func<int, bool> EventRule = e => true;
        /// <summary>(iteration, params) -&gt; &lt;0 stop, &gt;=0 retry the choose (params may be edited), &gt;0 keep going. Game: 0.</summary>
        public Func<int, int[], int> Reparm = (i, p) => 0;

        // ---------------------------------------------------------------- runtime state
        int _lastEventId = -1, _repeatCount;
        readonly int[] _rndVal = new int[32], _rndKey = new int[32];
        int _rndPos;
        /// <summary>Index of the sentence picked by the last successful <see cref="Choose"/> (-1 if none).</summary>
        public int LastSentence { get; private set; } = -1;

        static readonly int[] WeightScale = { 1, 4, 16, 64, 256, 1024, 4096, 16384 };   // speed.exe @0x90D250

        public bool HasEvent(string eventName) => eventName != null && _byName.ContainsKey(eventName);
        public Event GetEvent(string eventName) => eventName != null && _byName.TryGetValue(eventName, out var e) ? e : null;

        public string[] Parameters(string eventName)
        {
            var e = GetEvent(eventName);
            return e == null ? new string[0] : (string[])e.ParamNames.Clone();
        }

        /// <summary>Csis enum type of a parameter ("car_color", "speed", ..., or "int").</summary>
        public string ParameterType(string eventName, string param)
        {
            var e = GetEvent(eventName);
            if (e == null) return null;
            int i = Array.FindIndex(e.ParamNames, n => string.Equals(n, param, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? null : e.ParamTypes[i];
        }

        /// <summary>Csis enum value (a bit flag) by type and value name, e.g. ("car_color", "Red") = 1; 0 if unknown.</summary>
        public static int EnumValue(string type, string name)
        {
            if (type != null && Enums.TryGetValue(type, out var names))
                for (int i = 0; i < names.Length; i++)
                    if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return 1 << i;
            return 0;
        }

        /// <summary>Value names of a Csis enum type (the i-th has value 1 &lt;&lt; i).</summary>
        public static string[] EnumNames(string type) => type != null && Enums.TryGetValue(type, out var n) ? (string[])n.Clone() : new string[0];

        public Bank FindBank(int id, int sub)
        {
            int i = FindIdSub(id, sub);
            return i < 0 ? null : _sorted[i];
        }

        /// <summary>speaker_id values (bank sub ids) for which some sentence of the event resolves all its banks.</summary>
        public List<int> Speakers(string eventName)
        {
            var res = new List<int>();
            var e = GetEvent(eventName);
            if (e == null) return res;
            var subs = new SortedSet<int>();
            foreach (var b in Banks) if (b.Sub != 0xFFFF) subs.Add(b.Sub);
            foreach (int sp in subs)
            {
                bool any = false;
                foreach (var s in e.Sentences)
                {
                    bool ok = true;
                    foreach (var sl in s.Slots)
                    {
                        if (sl.Mode == 1 && sl.ParamIndex == 1) { if (FindIdSub(sl.BankId, sp) < 0) { ok = false; break; } }
                        else if (sl.Mode == 1) { ok = false; break; }
                        else if (FindId(sl.BankId) < 0) { ok = false; break; }
                    }
                    if (ok && s.Slots.Length > 0) { any = true; break; }
                }
                if (any) res.Add(sp);
            }
            return res;
        }

        // ---------------------------------------------------------------- loading
        public static SpeechScript Load(string gameRoot, string baseName = "copspeech")
        {
            var s = new SpeechScript();
            if (string.IsNullOrEmpty(gameRoot)) return s;
            string dir = Path.Combine(gameRoot, "SOUND", "SPEECH");
            if (!File.Exists(Path.Combine(dir, baseName + ".evt"))) dir = Path.Combine(gameRoot, "SOUND", "STREAMS");
            string evtPath = Path.Combine(dir, baseName + ".evt"), idxPath = Path.Combine(dir, baseName + ".idx");
            s.BigPath = Path.Combine(dir, baseName + ".big");
            try
            {
                if (File.Exists(idxPath)) s.ReadIdx(File.ReadAllBytes(idxPath));
                if (File.Exists(evtPath)) s.ReadEvt(File.ReadAllBytes(evtPath), baseName);
            }
            catch (Exception)
            {
                if (s.Events.Count == 0) { s._names.Clear(); s._byName.Clear(); }
            }
            return s;
        }

        static int U16(byte[] d, int o) => o >= 0 && o + 2 <= d.Length ? d[o] | d[o + 1] << 8 : 0;
        static int U8(byte[] d, int o) => o >= 0 && o < d.Length ? d[o] : 0;
        static uint U32(byte[] d, int o) => o >= 0 && o + 4 <= d.Length ? (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24) : 0;
        static int A4(int x) => (x + 3) & ~3;

        void ReadIdx(byte[] d)
        {
            int count = (int)U32(d, 0x54);
            for (int i = 0; i < count && 0x58 + i * 16 + 16 <= d.Length; i++)
            {
                int e = 0x58 + i * 16;
                int size = (int)U32(d, e + 4), h = (int)U32(d, e + 8);
                var b = new Bank { Line = i, BigOffset = U32(d, e + 12) };
                Banks.Add(b);
                if (size < 12 || h < 0 || h + size > d.Length) { b.Cue = new int[0]; b.Attrs = new byte[0][]; b.AttrMasks = new uint[0]; b.Ring = new byte[0]; continue; }
                b.Id = U16(d, h); b.Sub = U16(d, h + 2);
                b.NumAttrs = d[h + 4] & 0x7F; b.Partitioned = (d[h + 4] & 0x80) != 0;
                int ns = d[h + 5];
                b.RingSize = d[h + 6]; b.UnitBytes = (d[h + 7] + 1) * 0x100;
                b.End = U16(d, h + 8); b.Groups = U16(d, h + 10);
                int es = b.NumAttrs + 2;
                b.Cue = new int[ns]; b.Attrs = new byte[ns][];
                for (int t = 0; t < ns; t++)
                {
                    int p = h + 12 + t * es;
                    b.Cue[t] = U8(d, p) << 8 | U8(d, p + 1);
                    b.Attrs[t] = new byte[b.NumAttrs];
                    for (int a = 0; a < b.NumAttrs; a++) b.Attrs[t][a] = (byte)U8(d, p + 2 + a);
                }
                int m = h + A4(12 + ns * es);
                b.AttrMasks = new uint[b.NumAttrs];
                for (int a = 0; a < b.NumAttrs; a++) b.AttrMasks[a] = U32(d, m + a * 4);
                int part = m + b.NumAttrs * 4;
                b.Partitions = U8(d, part);
                int ring = part;
                if (b.Partitioned)
                {
                    b.Enabled = new byte[(ns + 7) / 8];
                    for (int k = 0; k < b.Enabled.Length; k++) b.Enabled[k] = (byte)U8(d, part + 1 + k);
                    ring = part + 1 + b.Enabled.Length;
                }
                b.RingPos = U8(d, ring);
                b.Ring = new byte[b.RingSize];
                for (int k = 0; k < b.RingSize; k++) b.Ring[k] = (byte)U8(d, ring + 1 + k);
                int grp = ring + (b.RingSize != 0 ? 1 + b.RingSize : 0);
                if (b.Groups != 0xFFFF && b.Groups > 0)
                {
                    b.GroupBits = new byte[(ns * b.Groups + 7) / 8];
                    for (int k = 0; k < b.GroupBits.Length; k++) b.GroupBits[k] = (byte)U8(d, grp + k);
                }
            }
            // SPCH_AddBank: insertion into a table sorted by (id, sub); handle = order of addition
            for (int i = 0; i < Banks.Count; i++)
            {
                var b = Banks[i];
                if (b.Cue.Length == 0 && b.End == 0) continue;
                int pos = _sorted.Count;
                while (pos > 0 && (_sorted[pos - 1].Id > b.Id || (_sorted[pos - 1].Id == b.Id && _sorted[pos - 1].Sub >= b.Sub))) pos--;
                _sorted.Insert(pos, b);
            }
            ResetBanks(0);
        }

        /// <summary>SPCH_AddBank's partition setup (0x832A5C) for a game seed, plus clearing the repeat rings.</summary>
        public void ResetBanks(int gameSeed)
        {
            foreach (var b in Banks)
            {
                if (b.Ring != null) for (int k = 0; k < b.Ring.Length; k++) b.Ring[k] = 0xFF;
                b.RingPos = 0;
                if (!b.Partitioned || b.Enabled == null || b.Partitions == 0) continue;
                int n = b.Cue.Length, parts = b.Partitions, part = (int)((uint)gameSeed % (uint)parts);
                int a = PartStart(part, n, parts), e = PartStart(part + 1, n, parts);
                for (int t = a; t < e && t < n; t++) b.Enabled[t >> 3] |= (byte)(1 << (t & 7));
            }
            _lastEventId = -1; _repeatCount = 0;
            for (int i = 0; i < 32; i++) { _rndVal[i] = -1; _rndKey[i] = -1; }
            _rndPos = 0;
            for (int i = 0; i < _globalVars.Length; i++) _globalVars[i] = 0xFF;
        }

        static int PartStart(int k, int n, int parts)   // 0x832A3E
        {
            int q = n / parts, r = n % parts;
            return r < k ? q * k + r : (q + 1) * k;
        }

        void ReadEvt(byte[] d, string baseName)
        {
            if (d.Length < 0x18 || d[0] != 3 || d[1] != 0x12) return;
            int tableOff = (int)U32(d, 4);
            ProjectId = d[8]; DatId = d[9];
            int nev = U16(d, 0x10), nVars = d[0x12];
            DurationFilter = U16(d, 0x14);
            int gv = 0x18 + A4(nev * 2);
            _globalVars = new byte[nVars];
            for (int i = 0; i < nVars; i++) _globalVars[i] = 0xFF;
            var table = baseName != null && baseName.StartsWith("NIS", StringComparison.OrdinalIgnoreCase) ? NisStructs : CopStructs;
            for (int i = 0; i < nev; i++)
            {
                int r = U16(d, 0x18 + i * 2) * 4;
                var e = new Event { Index = i };
                string name = null;
                if (tableOff > 0 && tableOff + nev * 0x20 <= d.Length)
                {
                    int t = tableOff + i * 0x20;
                    int no = tableOff + nev * 0x20 + (int)U32(d, t);
                    e.InterfaceCrc = U16(d, t + 6);
                    if (no >= 0 && no < d.Length) name = Chunks.CString(d, no, d.Length - no);
                }
                ParseRecord(d, r, e);
                e.Name = string.IsNullOrEmpty(name) ? "Event_" + e.Id : name;
                if (!table.TryGetValue(e.Name, out var pars) || pars.Length != e.NumParams)
                {
                    pars = new string[e.NumParams];
                    for (int k = 0; k < pars.Length; k++) pars[k] = "p" + (k + 1) + ":int";
                }
                e.ParamNames = new string[pars.Length]; e.ParamTypes = new string[pars.Length];
                for (int k = 0; k < pars.Length; k++)
                {
                    int c = pars[k].IndexOf(':');
                    e.ParamNames[k] = c < 0 ? pars[k] : pars[k].Substring(0, c);
                    e.ParamTypes[k] = c < 0 ? pars[k] : pars[k].Substring(c + 1);
                }
                Events.Add(e);
                _names.Add(e.Name);
                _byName[e.Name] = e;
            }
        }

        static void ParseRecord(byte[] d, int r, Event e)
        {
            e.Id = U16(d, r); e.Expiry = U16(d, r + 2); e.Priority = U16(d, r + 4);
            int ns = U8(d, r + 6), nr = U8(d, r + 7), b8 = U8(d, r + 8);
            e.MaxRepeat = b8 & 15; e.Probability = U8(d, r + 9); e.Flags = U8(d, r + 10); e.NumParams = (sbyte)U8(d, r + 11);
            if (e.NumParams < 0) e.NumParams = 0;
            int R = r + 12 + A4(ns * 2);
            e.Rules = new Rule[nr];
            for (int k = 0; k < nr; k++) e.Rules[k] = new Rule { RuleId = (byte)U8(d, R + 3 * k), ParamIndex = (byte)U8(d, R + 3 * k + 1), Type = (byte)U8(d, R + 3 * k + 2) };
            int B = R + (((nr + 1) * 3) & ~3), nb = (nr + 7) / 8;   // 0x831BEB: (nRules + 1) x 3 rounded DOWN to 4
            e.RuleValue = new uint[ns]; e.RuleCare = new uint[ns];
            if (nb > 0 && nb < 5)
                for (int s = 0; s < ns; s++)
                    for (int k = 0; k < nb; k++)
                    {
                        e.RuleValue[s] |= (uint)U8(d, B + nb * s + k) << (24 - 8 * k);
                        e.RuleCare[s] |= (uint)U8(d, B + nb * ns + nb * s + k) << (24 - 8 * k);
                    }
            int C = B + A4(nb * ns * 2), npt = b8 >> 4;
            e.ParamTests = new int[npt];
            for (int k = 0; k < npt; k++) e.ParamTests[k] = U8(d, C + 3 * k + 1);
            int D = C + (((npt + 1) * 3) & ~3);   // 0x832FD4 / 0x832FAB / 0x831C3B: rounded down, not up
            e.PresenceMask = new ushort[ns];
            if ((e.Flags & 8) != 0)
            {
                e.PresenceSpec = U32(d, D);
                for (int s = 0; s < ns; s++) e.PresenceMask[s] = (ushort)U16(d, D + 4 + 2 * s);
            }
            e.FollowUps = new ushort[0];
            if ((e.Flags & 0x10) != 0)
            {
                int F = D + ((e.Flags & 8) != 0 ? A4(ns * 2 + 4) : 0), n = U8(d, F);
                e.FollowUps = new ushort[n];
                for (int k = 0; k < n; k++) e.FollowUps[k] = (ushort)U16(d, F + 2 + 2 * k);
            }
            e.Sentences = new Sentence[ns];
            for (int s = 0; s < ns; s++)
            {
                int so = r + U16(d, r + 12 + 2 * s) * 4;
                int w = U8(d, so), b2 = U8(d, so + 2), nsl = b2 >> 2, npm = U8(d, so + 4);
                var sen = new Sentence
                {
                    RawWeight = w, Weight = (w & 31) * WeightScale[w >> 5], Probability = U8(d, so + 1),
                    ModeRestrict = b2 & 3, NumVars = U8(d, so + 3), ParamMasks = new uint[npm], Slots = new Slot[nsl],
                };
                for (int k = 0; k < npm; k++) sen.ParamMasks[k] = U32(d, so + 8 + A4(nsl) + 4 * k);
                for (int k = 0; k < nsl; k++)
                {
                    int cp = so + U8(d, so + 8 + k) * 4;
                    int ne = (sbyte)U8(d, cp + 4);
                    var sl = new Slot { BankId = U16(d, cp), ParamIndex = U8(d, cp + 2), Mode = U8(d, cp + 3), Entries = new Entry[Math.Max(0, ne)] };
                    for (int j = 0; j < sl.Entries.Length; j++)
                    {
                        int ep = cp + 8 + 8 * j;
                        sl.Entries[j] = new Entry { Mask = U32(d, ep), Kind = (byte)U8(d, ep + 4), Rule = (byte)U8(d, ep + 5), Var = (byte)U8(d, ep + 6) };
                    }
                    sen.Slots[k] = sl;
                }
                e.Sentences[s] = sen;
            }
        }

        // ---------------------------------------------------------------- bank table searches (0x8327FF / 0x832853)
        int FindId(int id)
        {
            int lo = 0, hi = _sorted.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2, v = _sorted[mid].Id;
                if (v == id) return mid;
                if (v < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        int FindIdSub(int id, int sub)
        {
            sub &= 0xFFFF;
            int lo = 0, hi = _sorted.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var b = _sorted[mid];
                if (b.Id == id && b.Sub == sub) return mid;
                if (b.Id < id || (b.Id == id && b.Sub < sub)) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        // ---------------------------------------------------------------- random (0x8346FF)
        static int Base(Random rng, int n) => n <= 0 ? 0 : (int)(((long)rng.Next(0x10000) * n) >> 16);

        /// <summary>Random in [0,n); with a key, retries (up to 32 times) values drawn for that key among its last
        /// min(n/2,10) draws, keeping a 32-entry (value, key) history like 0x8346FF.</summary>
        int KeyedRandom(Random rng, int n, int key)
        {
            int window = Math.Min(n / 2, 10);
            int v = Base(rng, n);
            if (key == -1) return v;
            int best = -1, bestV = v;
            for (int attempt = 0; ; )
            {
                int found = -1, matched = 0, pos = _rndPos, limit = Math.Min(window, 32);
                for (int scanned = 0; ; )
                {
                    if (matched >= limit) break;
                    if (_rndKey[pos] == key) { if (_rndVal[pos] == v) { found = matched; break; } matched++; }
                    pos = pos - 1 < 0 ? 31 : pos - 1;
                    if (++scanned > 32) break;
                }
                bool done = false;
                if (found == -1) { done = true; bestV = v; }
                else if (best < found) { best = found; bestV = v; }
                if (++attempt > 31) done = true;
                if (done)
                {
                    _rndPos = (_rndPos + 1) & 31;
                    _rndVal[_rndPos] = bestV; _rndKey[_rndPos] = key;
                    return v;
                }
                v = Base(rng, n);
            }
        }

        // ---------------------------------------------------------------- choose
        sealed class SlotState { public int Table = -1, Group = -1, Start, Count, Chosen; public bool Resolved; }

        sealed class Build
        {
            public Sentence S;
            public SlotState[] Slots;
            public readonly List<int> Cands = new List<int>(); // take indices, max 200
            public uint[] Vars;
        }

        /// <summary>Picks a sentence for the event like SPCH_Choose + SPCH_Play would and returns its takes in play
        /// order (empty if the event is unknown or every sentence is rejected). args: parameter name -&gt; Csis value
        /// (enum bit flags, e.g. car_color Red = 1; speaker_id 1..9); missing = 0. With commit (default) the takes
        /// enter their banks' repeat rings and the event's repeat count, as when the game plays them.</summary>
        public List<SpeechSample> Choose(string eventName, IReadOnlyDictionary<string, int> args, Random rng) => Choose(eventName, args, rng, true);

        public List<SpeechSample> Choose(string eventName, IReadOnlyDictionary<string, int> args, Random rng, bool commit)
        {
            var result = new List<SpeechSample>();
            LastSentence = -1;
            var e = GetEvent(eventName);
            if (e == null) return result;
            if (rng == null) rng = new Random();
            var p = new int[e.NumParams + 1];
            p[0] = e.Id | DatId << 16 | ProjectId << 24;                       // EventSpec {u16 id, u8 datID, u8 projectID}
            if (args != null)
                for (int k = 0; k < e.ParamNames.Length; k++)
                    if (args.TryGetValue(e.ParamNames[k], out int v)) p[k + 1] = v;
            // AddEvent (0x83215E): dropped unless rand(100) <= probability; then EventRuleCallback (0x8324DD)
            if (KeyedRandom(rng, 100, -1) > e.Probability) return result;
            if (EventRule != null && !EventRule(e.Id)) return result;
            // 0x834519: choose, then reparm retries while the callback says so
            var b = TryChoose(e, p, rng, out int si);
            if (b == null && Reparm != null)
                for (int i = 0; ; i++)
                {
                    int r = Reparm(i, p);
                    if (r >= 0) b = TryChoose(e, p, rng, out si);
                    if (b != null || r <= 0) break;
                }
            if (b == null) return result;
            LastSentence = si;
            for (int k = 0; k < b.Slots.Length; k++)
            {
                var st = b.Slots[k];
                var bank = _sorted[st.Table];
                int take = b.Cands[st.Chosen];
                result.Add(new SpeechSample
                {
                    Line = bank.Line, Take = take, BigOffset = bank.BigOffset + bank.TakeOffset(take, st.Group), NumBytes = bank.TakeBytes(take),
                    BankId = bank.Id, Speaker = bank.Sub == 0xFFFF ? -1 : bank.Sub, Slot = k, Interrupt = k == 0 && (e.Flags & 0x20) != 0,
                });
            }
            if (commit) Play(e, b, p);
            return result;
        }

        Build TryChoose(Event e, int[] p, Random rng, out int chosenSentence)   // 0x834314
        {
            chosenSentence = -1;
            if (e.MaxRepeat != 0 && e.Id == _lastEventId && _repeatCount >= e.MaxRepeat) return null;
            const int filterMode = 0;                                         // SPCH_SetFilter is never called
            int filter = 0;
            // rules (0x834B82): pass 1 rules bound to a param, pass 2 the others (parmValue 0); bit 31 = rule 0
            uint results = 0, unknown = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < e.Rules.Length; k++)
                {
                    var ru = e.Rules[k];
                    if ((ru.ParamIndex != 0) != (pass == 0)) continue;
                    uint bit = 1u << (31 - k);
                    int val = pass == 0 && ru.ParamIndex < p.Length ? p[ru.ParamIndex] : 0;
                    int r = TestSentenceRule == null ? -1 : TestSentenceRule(e.Id, ru.RuleId, val, DatId);
                    if (r < 0) unknown |= bit; else if (r > 0) results |= bit;
                }
            int n = e.Sentences.Length;
            if (n == 0) return null;
            // weighted order without replacement (0x8335F7); weight-0 sentences go last in file order
            var order = new List<int>(n);
            var w = new int[n];
            int total = 0;
            for (int k = 0; k < n; k++) { w[k] = e.Sentences[k].Weight; total += w[k]; }
            while (total > 0)
            {
                int r = KeyedRandom(rng, total, -1), k = 0;
                for (; k < n; k++) { r -= w[k]; if (r < 0) break; }
                if (k >= n) break;
                order.Add(k); total -= w[k]; w[k] = 0;
            }
            for (int k = 0; k < n; k++) if (e.Sentences[k].Weight == 0) order.Add(k);
            int presence = (e.Flags & 8) != 0 ? Presence(e, p) : 0;
            foreach (int si in order)
            {
                var s = e.Sentences[si];
                if (KeyedRandom(rng, 100, -1) >= s.Probability) continue;
                if (!ParamTestsPass(e, s, p)) continue;
                uint care = e.RuleCare[si] & ~unknown;
                if ((care & results) != (e.RuleValue[si] & care)) continue;
                if ((e.Flags & 8) != 0 && e.PresenceMask[si] != presence) continue;
                var b = new Build { S = s, Slots = new SlotState[s.Slots.Length], Vars = new uint[s.NumVars] };
                for (int k = 0; k < b.Slots.Length; k++) b.Slots[k] = new SlotState();
                if (BuildSlots(b, p, filterMode) && PickTakes(b, rng, filter)) { chosenSentence = si; return b; }
            }
            return null;
        }

        static int Presence(Event e, int[] p)   // 0x83423F
        {
            int m = 0;
            for (int k = 0; k < 16; k++)
            {
                uint spec = (e.PresenceSpec >> (2 * k)) & 3;
                int v = k + 1 < p.Length ? p[k + 1] : 0;
                if ((spec == 1 && v != 0) || (spec == 2 && v != -1)) m |= 1 << k;
            }
            return (ushort)m;
        }

        static bool ParamTestsPass(Event e, Sentence s, int[] p)   // 0x83428E
        {
            for (int k = 0; k < e.ParamTests.Length && k < s.ParamMasks.Length; k++)
            {
                uint mask = s.ParamMasks[k];
                int pi = e.ParamTests[k];
                if (mask != 0 && ((uint)(pi < p.Length ? p[pi] : 0) & mask) == 0) return false;
            }
            return true;
        }

        bool BuildSlots(Build b, int[] p, int filterMode)   // 0x8338C4
        {
            var s = b.S;
            if (s.Slots.Length >= 13) return false;
            if (s.ModeRestrict == 1 ? filterMode != 2 : s.ModeRestrict == 2 ? filterMode == 2 : s.ModeRestrict != 0) return false;
            for (int passes = 200; passes > 0; )
            {
                passes--;
                bool done = true;
                for (int k = 0; k < s.Slots.Length; k++)
                {
                    var sl = s.Slots[k];
                    bool waits = false;
                    foreach (var en in sl.Entries)
                        if (en.Kind == 0xFF && en.Var != 0xFF && (en.Var & 0x80) != 0 && (en.Var & 0x7F) < b.Vars.Length && b.Vars[en.Var & 0x7F] == 0) { waits = true; break; }
                    if (waits) { done = false; continue; }
                    var st = b.Slots[k];
                    if (st.Resolved) continue;
                    st.Start = b.Cands.Count;
                    if (!ResolveBank(sl, p, st)) return false;
                    int c = Collect(b, sl, st, p);
                    if (c == 0) return false;
                    st.Count = c; st.Resolved = true;
                }
                if (done) return passes != 0;
            }
            return false;
        }

        bool ResolveBank(Slot sl, int[] p, SlotState st)   // 0x833169
        {
            int pv = sl.ParamIndex < p.Length ? p[sl.ParamIndex] : 0;
            st.Group = -1;
            switch (sl.Mode)
            {
                case 0: st.Table = FindId(sl.BankId); break;
                case 1: st.Table = FindIdSub(sl.BankId, pv); break;
                case 2:
                    {
                        int t = FindId(sl.BankId);
                        st.Table = -1;
                        if (t >= 0 && _sorted[t].Groups != 0xFFFF && (uint)pv < (uint)_sorted[t].Groups) { st.Table = t; st.Group = pv; }
                        break;
                    }
                default: st.Table = -1; break;
            }
            return st.Table >= 0;
        }

        int RingAge(Bank bank, int take)   // 0x8329BE: 1 = most recently played, 0 = not in the ring
        {
            int n = bank.RingSize;
            if (n == 0) return 0;
            int pos = bank.RingPos;
            for (int i = 0; i < n; i++)
            {
                if (--pos < 0) pos = n - 1;
                if (bank.Ring[pos] == take) return i + 1;
            }
            return 0;
        }

        bool TakeMatches(Build b, Slot sl, byte[] attrs, int[] p)   // 0x833087
        {
            for (int j = 0; j < sl.Entries.Length; j++)
            {
                int a = j < attrs.Length ? attrs[j] : 0xFF;
                if (a > 31) return false;
                var en = sl.Entries[j];
                uint bit = 1u << a;
                if ((en.Mask & bit) == 0) return false;
                if (en.Kind == 0 || en.Kind == 0xFE) continue;
                uint have;
                if (en.Kind == 0xFF)
                {
                    if ((en.Var & 0x80) == 0)
                    {
                        if (en.Var >= _globalVars.Length || _globalVars[en.Var] != a) return false;
                        continue;
                    }
                    have = (en.Var & 0x7F) < b.Vars.Length ? b.Vars[en.Var & 0x7F] : 0;
                }
                else have = en.Kind < p.Length ? (uint)p[en.Kind] : 0;
                if ((have & bit) == 0) return false;
            }
            return true;
        }

        void SetLocalVars(Build b, Slot sl, byte[] attrs)   // 0x83300E
        {
            for (int j = 0; j < sl.Entries.Length; j++)
            {
                var en = sl.Entries[j];
                if (en.Kind == 0xFE && en.Var != 0xFF && (en.Var & 0x80) != 0 && (en.Var & 0x7F) < b.Vars.Length && j < attrs.Length && attrs[j] < 32)
                    b.Vars[en.Var & 0x7F] |= 1u << attrs[j];
            }
        }

        int Collect(Build b, Slot sl, SlotState st, int[] p)   // 0x833382
        {
            bool setsVar = false;
            foreach (var en in sl.Entries) if (en.Kind == 0xFE) { setsVar = true; break; }
            var bank = _sorted[st.Table];
            for (int j = 0; j < bank.NumAttrs && j < sl.Entries.Length; j++)
            {
                int kind = sl.Entries[j].Kind;
                if (kind != 0 && kind != 0xFF && kind != 0xFE && ((kind < p.Length ? (uint)p[kind] : 0) & bank.AttrMasks[j]) == 0) return 0;
            }
            int count = 0, bestAge = 0, best = -1;
            for (int i = 0; i < bank.TakeCount; i++)
            {
                if (!TakeMatches(b, sl, bank.Attrs[i], p)) continue;
                if (st.Group >= 0)
                {
                    int bit = i + bank.TakeCount * st.Group;
                    if (bank.GroupBits == null || st.Group >= bank.Groups || (bank.GroupBits[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                }
                if (bank.Partitioned && (bank.Enabled == null || (bank.Enabled[i >> 3] & (1 << (i & 7))) == 0)) continue;
                int age = RingAge(bank, i);
                if (age < 1)
                {
                    if (b.Cands.Count < 200) { b.Cands.Add(i); count++; }
                    if (setsVar) SetLocalVars(b, sl, bank.Attrs[i]);
                }
                else if (bestAge < age) { bestAge = age; best = i; }
            }
            if (count == 0 && best != -1)
            {
                if (b.Cands.Count < 200) { b.Cands.Add(best); count = 1; }
                if (setsVar) SetLocalVars(b, sl, bank.Attrs[best]);
            }
            return count;
        }

        bool PickTakes(Build b, Random rng, int filter)   // 0x833C45
        {
            if (filter < 1 && b.S.NumVars == 0)
            {
                foreach (var st in b.Slots)
                {
                    if (st.Count == 0) return false;
                    st.Chosen = st.Start + KeyedRandom(rng, st.Count, st.Table);
                }
                return true;
            }
            // shuffle each slot's candidates (0x833A21), then walk combinations odometer-style (0x833AE7/0x833A7D)
            foreach (var st in b.Slots)
                for (int u = st.Count; u > 1; u--)
                {
                    int k = KeyedRandom(rng, u, -1);
                    int t = b.Cands[st.Start + u - 1]; b.Cands[st.Start + u - 1] = b.Cands[st.Start + k]; b.Cands[st.Start + k] = t;
                }
            foreach (var st in b.Slots) st.Chosen = st.Start;
            if (b.Slots.Length == 0) return true;
            for (int guard = 0; guard < 100000; guard++)
            {
                bool ok = (filter == 0 || Duration(b) <= filter) && (b.S.NumVars == 0 || VarsConsistent(b));
                if (ok) return true;
                int k = b.Slots.Length - 1;
                for (; k >= 0; k--)
                {
                    var st = b.Slots[k];
                    if (++st.Chosen < st.Start + st.Count) break;
                    st.Chosen = st.Start;
                }
                if (k < 0) return false;
            }
            return false;
        }

        int Duration(Build b)   // 0x833562: bytes * 100 / SPCH_GetSampleDataRate(22050, 16, MicroTalk) = 4410
        {
            long bytes = 0;
            foreach (var st in b.Slots) bytes += _sorted[st.Table].TakeBytes(b.Cands[st.Chosen]);
            return (int)(bytes * 100 / 4410);
        }

        bool VarsConsistent(Build b)   // 0x833784
        {
            Array.Clear(b.Vars, 0, b.Vars.Length);
            var done = new bool[b.Slots.Length];
            for (int pass = 0; pass < 64; pass++)
            {
                bool all = true;
                for (int k = 0; k < b.Slots.Length; k++)
                {
                    if (done[k]) continue;
                    done[k] = true;
                    var sl = b.S.Slots[k];
                    var attrs = _sorted[b.Slots[k].Table].Attrs[b.Cands[b.Slots[k].Chosen]];
                    for (int j = 0; j < sl.Entries.Length && j < attrs.Length; j++)
                    {
                        var en = sl.Entries[j];
                        if (en.Var == 0xFF || (en.Var & 0x80) == 0 || (en.Var & 0x7F) >= b.Vars.Length || attrs[j] > 31) continue;
                        int vi = en.Var & 0x7F;
                        if (en.Kind == 0xFF)
                        {
                            if (b.Vars[vi] == 0) { all = false; done[k] = false; }
                            else if ((b.Vars[vi] & (1u << attrs[j])) == 0) return false;
                        }
                        else if (en.Kind == 0xFE) b.Vars[vi] = 1u << attrs[j];
                    }
                }
                if (all) return true;
            }
            return false;
        }

        void Play(Event e, Build b, int[] p)   // 0x834169
        {
            if (SetSentenceRule != null)
            {
                // 0x834AF6: rules of type 0/3 whose param the sentence uses
                foreach (var ru in e.Rules)
                    if ((ru.Type == 0 || ru.Type == 3) && SentenceUsesParam(b.S, ru.ParamIndex))
                        SetSentenceRule(e.Id, ru.RuleId, ru.ParamIndex < p.Length ? p[ru.ParamIndex] : 0, DatId);
                // 0x833CD9: slot entries bound to a rule report the chosen take's attr bit
                for (int k = 0; k < b.Slots.Length; k++)
                {
                    var sl = b.S.Slots[k];
                    var attrs = _sorted[b.Slots[k].Table].Attrs[b.Cands[b.Slots[k].Chosen]];
                    for (int j = 0; j < sl.Entries.Length && j < attrs.Length; j++)
                        if (sl.Entries[j].Rule != 0xFF && sl.Entries[j].Rule < e.Rules.Length && attrs[j] < 32)
                            SetSentenceRule(e.Id, e.Rules[sl.Entries[j].Rule].RuleId, 1 << attrs[j], DatId);
                }
            }
            // 0x833B81: slot entries that set a global var store the chosen take's attr
            for (int k = 0; k < b.Slots.Length; k++)
            {
                var sl = b.S.Slots[k];
                var attrs = _sorted[b.Slots[k].Table].Attrs[b.Cands[b.Slots[k].Chosen]];
                for (int j = 0; j < sl.Entries.Length && j < attrs.Length; j++)
                    if (sl.Entries[j].Kind == 0xFE && (sl.Entries[j].Var & 0x80) == 0 && sl.Entries[j].Var < _globalVars.Length)
                        _globalVars[sl.Entries[j].Var] = attrs[j];
            }
            // 0x833DD7: request + repeat ring push (0x83298B) + disable played take of partitioned banks
            for (int k = 0; k < b.Slots.Length; k++)
            {
                var bank = _sorted[b.Slots[k].Table];
                int take = b.Cands[b.Slots[k].Chosen];
                if (bank.Partitioned && bank.Enabled != null) bank.Enabled[take >> 3] &= (byte)~(1 << (take & 7));
                if (bank.RingSize != 0)
                {
                    if (bank.RingPos < bank.RingSize) bank.Ring[bank.RingPos] = (byte)take;
                    bank.RingPos = bank.RingPos + 1 >= bank.RingSize ? 0 : bank.RingPos + 1;
                }
            }
            if (e.Id == _lastEventId) _repeatCount++; else { _lastEventId = e.Id; _repeatCount = 1; }
        }

        static bool SentenceUsesParam(Sentence s, int param)   // 0x834A70
        {
            foreach (var sl in s.Slots)
            {
                if (sl.ParamIndex == param) return true;
                foreach (var en in sl.Entries) if (en.Kind == param) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- helpers
        /// <summary>Decodes one chosen take from the .big (mono MicroTalk SCHl).</summary>
        public EaStream ReadTake(SpeechSample s)
        {
            if (string.IsNullOrEmpty(BigPath) || !File.Exists(BigPath)) return null;
            using var f = File.OpenRead(BigPath);
            if (s.BigOffset < 0 || s.BigOffset >= f.Length) return null;
            f.Position = s.BigOffset;
            int len = (int)Math.Min(s.NumBytes > 0 ? s.NumBytes : 0x40000, f.Length - s.BigOffset);
            var buf = new byte[len];
            int got = 0;
            while (got < len) { int r = f.Read(buf, got, len - got); if (r <= 0) break; got += r; }
            return EaStream.Read(buf, 0, out _);
        }

        /// <summary>Human-readable dump of an event's sentences (slots as [bank/param attr tests]).</summary>
        public string Describe(string eventName)
        {
            var e = GetEvent(eventName);
            if (e == null) return eventName + ": (none)";
            var sb = new StringBuilder();
            sb.Append($"{e.Name} id={e.Id} prio={e.Priority} expiry={e.Expiry} prob={e.Probability} flags={e.Flags:X} maxRepeat={e.MaxRepeat} params({string.Join(", ", e.ParamNames)})");
            if (e.Rules.Length > 0) sb.Append($" rules={e.Rules.Length}");
            if (e.FollowUps.Length > 0) sb.Append($" follow=[{string.Join(",", e.FollowUps)}]");
            for (int si = 0; si < e.Sentences.Length; si++)
            {
                var s = e.Sentences[si];
                sb.Append($"\n  S{si} w={s.Weight} p={s.Probability}{(s.ModeRestrict != 0 ? " mode=" + s.ModeRestrict : "")}{(s.NumVars != 0 ? " vars=" + s.NumVars : "")}:");
                foreach (var sl in s.Slots)
                {
                    sb.Append(" [").Append(sl.BankId.ToString("X"));
                    if (sl.Mode == 1) sb.Append("/").Append(ParamLabel(e, sl.ParamIndex));
                    else if (sl.Mode == 2) sb.Append("#").Append(ParamLabel(e, sl.ParamIndex));
                    foreach (var en in sl.Entries)
                    {
                        sb.Append(' ');
                        sb.Append(en.Kind == 0 ? "*" : en.Kind == 0xFF ? "var?" : en.Kind == 0xFE ? "var=" : ParamLabel(e, en.Kind));
                        sb.Append('&').Append(en.Mask.ToString("X"));
                        if (en.Var != 0xFF) sb.Append(" v").Append(en.Var.ToString("X"));
                        if (en.Rule != 0xFF) sb.Append(" r").Append(en.Rule);
                    }
                    sb.Append(']');
                }
            }
            return sb.ToString();
        }

        static string ParamLabel(Event e, int k) => k >= 1 && k <= e.ParamNames.Length ? e.ParamNames[k - 1] : "p" + k;

        // ---------------------------------------------------------------- generated Csis knowledge
        static Dictionary<string, string[]> _cop, _nis, _enums;
        static Dictionary<string, string[]> CopStructs => _cop ??= Structs(CopStructTable);
        static Dictionary<string, string[]> NisStructs => _nis ??= Structs(NisStructTable);
        static Dictionary<string, string[]> Enums
        {
            get
            {
                if (_enums != null) return _enums;
                var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in EnumTable)
                {
                    var parts = line.Split(' ');
                    var names = new string[parts.Length - 1];
                    Array.Copy(parts, 1, names, 0, names.Length);
                    d[parts[0]] = names;
                }
                return _enums = d;
            }
        }

        static Dictionary<string, string[]> Structs(string[] table)
        {
            var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in table)
            {
                var parts = line.Split(' ');
                var pars = new string[parts.Length - 1];
                for (int i = 1; i < parts.Length; i++) pars[i - 1] = parts[i].IndexOf(':') < 0 ? parts[i] + ":" + parts[i] : parts[i];
                d[parts[0]] = pars;
            }
            return d;
        }

        static readonly string[] CopStructTable =
        {
            // copspeech: Csis structs (event param1..N), from SND_GEN/COPSPEECH.hpp
            "Setup_InitPursuit speaker_id:int num_suspects",
            "Setup_SuspectConfirmed speaker_id:int num_suspects",
            "Setup_ReInitPursuit speaker_id:int time_since_lost num_suspects",
            "Setup_VehicleReport speaker_id:int car_color car_type speed measurement",
            "Setup_VehicleReportTag speaker_id:int car_color car_type",
            "Setup_DispVehDescrip speaker_id:int car_color car_type",
            "Setup_DispVehDescripVinyls speaker_id:int car_color car_type",
            "Setup_DispNoVehDescrip speaker_id:int",
            "Setup_DispCustPaint speaker_id:int car_type",
            "Setup_MoreDetails speaker_id:int",
            "Setup_LocationReport speaker_id:int num_suspects direction encounter location_region location",
            "Setup_BullhornPrefix speaker_id:int",
            "Setup_Bullhorn speaker_id:int",
            "Setup_SelfStrategy speaker_id:int code self_strategy_type",
            "Setup_InitialCallForBU speaker_id:int code",
            "Setup_InitialCallForBU_MS speaker_id:int",
            "Backup_CallForBU speaker_id:int code backup_type",
            "Backup_UnitBUReply speaker_id:int",
            "Backup_DispBackupReply speaker_id:int yes_no subject_battalion subject_call_sign_id code disp_backup_type",
            "Backup_CallForSwarming speaker_id:int",
            "Backup_DispBUETA speaker_id:int disp_backup_eta",
            "Backup_DispHeliBUETA speaker_id:int disp_backup_eta",
            "Backup_BUReminder speaker_id:int code backup_type",
            "Backup_NegativeBUReply speaker_id:int yes_no subject_battalion subject_call_sign_id",
            "Backup_DispBackupUpdate speaker_id:int yes_no",
            "Backup_BUArrives speaker_id:int speaker_battalion speaker_call_sign_id",
            "StaticRoadblock_CallForRB speaker_id:int code roadblock_type",
            "StaticRoadblock_RBReminder speaker_id:int code",
            "StaticRoadblock_NegativeRBReply speaker_id:int",
            "StaticRoadblock_DispRBReply speaker_id:int code roadblock_type yes_no subject_battalion subject_call_sign_id",
            "StaticRoadblock_DispRBUpdate speaker_id:int code yes_no roadblock_type",
            "StaticRoadblock_PursuitApproaching speaker_id:int num_suspects",
            "StaticRoadblock_RBApproach speaker_id:int roadblock_type",
            "StaticRoadblock_RBEngage speaker_id:int roadblock_engage_type",
            "StaticRoadblock_RBAverted speaker_id:int",
            "StaticRoadblock_CallForRB_sub speaker_id:int",
            "StaticRoadblock_DispSubRB speaker_id:int",
            "Projectile_CallForSafety speaker_id:int code projectile_type",
            "Projectile_ProjectileLaunch speaker_id:int projectile_type",
            "Projectile_ProjectileHit speaker_id:int num_units",
            "Projectile_ProjectileMiss speaker_id:int projectile_type num_units",
            "RollingStrategy_InitStrategy speaker_id:int code rolling_strategy_type",
            "RollingStrategy_CallToPosition speaker_id:int intensity position subject_battalion subject_call_sign_id",
            "RollingStrategy_CallToPositionRem speaker_id:int intensity",
            "RollingStrategy_StrategyExecute speaker_id:int intensity",
            "Outcome_AnticipateFail speaker_id:int",
            "Outcome_AnticipateSuccess speaker_id:int",
            "Outcome_OutcomeFail speaker_id:int intensity",
            "Outcome_StrategyReset speaker_id:int same_new intensity",
            "Arrest_BullhornArrest speaker_id:int intensity",
            "Arrest_Arrest speaker_id:int intensity",
            "Arrest_DispArrestReply speaker_id:int",
            "AnytimeEvents_CollisionWorld speaker_id:int world_object_type num_units",
            "AnytimeEvents_CollWorld_Civi speaker_id:int intensity",
            "AnytimeEvents_CollWorld_Spin speaker_id:int intensity",
            "AnytimeEvents_CollWorld_Air speaker_id:int intensity",
            "AnytimeEvents_CollWorld_Flip speaker_id:int intensity",
            "AnytimeEvents_DispPursuitUpdate speaker_id:int subject_battalion subject_call_sign_id",
            "AnytimeEvents_PursuitUpdateRep speaker_id:int",
            "AnytimeEvents_Disp911Report speaker_id:int pursuit_type num_suspects encounter direction location location_region address_group_type",
            "AnytimeEvents_Disp911CsPnt speaker_id:int address_group_type pursuit_type num_suspects encounter direction location_region location car_type",
            "AnytimeEvents_Disp911NoDescrip speaker_id:int address_group_type pursuit_type num_suspects encounter direction location_region location car_type",
            "AnytimeEvents_Unit911Reply speaker_id:int",
            "AnytimeEvents_SuspectUTurn speaker_id:int intensity",
            "AnytimeEvents_SuspectOutrun speaker_id:int intensity",
            "AnytimeEvents_LostVisual speaker_id:int intensity",
            "AnytimeEvents_RegainVisual speaker_id:int intensity",
            "AnytimeEvents_LostSuspect speaker_id:int intensity",
            "AnytimeEvents_DispBreakAway speaker_id:int location_region location direction",
            "AnytimeEvents_DispTimeExpired speaker_id:int",
            "AnytimeEvents_DispPursuitEscalation speaker_id:int address_group_type pursuit_type num_suspects direction location location_region",
            "AnytimeEvents_DispPursEscGen speaker_id:int num_suspects",
            "AnytimeEvents_UnitDisabled speaker_id:int intensity self_other",
            "AnytimeEvents_CallForEV speaker_id:int ev_type",
            "AnytimeEvents_DispEVReply speaker_id:int",
            "AnytimeEvents_IntentToRam speaker_id:int intensity",
            "AnytimeEvents_Bailout speaker_id:int bailout_type",
            "AnytimeEvents_BailoutDeny speaker_id:int",
            "AnytimeEvents_FocusChange speaker_id:int intensity",
            "AnytimeEvents_SuspectBehaviour speaker_id:int num_suspects",
            "AnytimeEvents_DriverHistory speaker_id:int region",
            "AnytimeEvents_OffroadMoment speaker_id:int offroad_moment_id first_subsequent",
            "AnytimeEvents_Spotted speaker_id:int intensity",
            "AnytimeEvents_SuspectBrake speaker_id:int",
            "AnytimeEvents_WeatherReport speaker_id:int",
            "AnytimeEvents_HeatJump speaker_id:int heat_level",
            "AnytimeEvents_DirectionHigh speaker_id:int direction",
            "AnytimeEvents_DispJurisShift speaker_id:int jurisdiction",
            "HeliSpecific_HeliSelfStrategy speaker_id:int heli_self_strategy_type",
            "HeliSpecific_HeliLostVisual speaker_id:int heli_lost_visual",
            "HeliSpecific_HeliIntentToBail speaker_id:int heli_bailout_type",
            "HeliSpecific_HeliBailout speaker_id:int heli_bailout_type",
            "HeliSpecific_HeliSwarming speaker_id:int",
            "HeliSpecific_HeliSpotter speaker_id:int",
            "HeliSpecific_HeliHazardAlert speaker_id:int heli_hazard_alert_type",
            "HeliSpecific_HeliQuadrent speaker_id:int",
            "HeliSpecific_HeliQuadrentMoving speaker_id:int",
            "HeliSpecific_HeliBullhornArrest speaker_id:int",
            "E3_Events_E3_Setup",
            "Interrupts_Interrupt speaker_id:int intensity",
            "Interrupts_InterruptRam speaker_id:int",
            "Interrupts_InterruptRam_RE speaker_id:int intensity",
            "Interrupts_InterruptRam_HO speaker_id:int intensity",
            "Interrupts_InterruptRam_SS speaker_id:int intensity",
            "Interrupts_InterruptRam_TB speaker_id:int intensity",
            "Interrupts_InterruptRamHigh speaker_id:int",
            "Interrupts_StaticInterrupt",
            "Interrupts_RegainVisualInterrupt speaker_id:int intensity",
            "CellCall cell_call_bucket cell_call_number",
            "ExtraCops_SwarmingReply",
            "ExtraCops_SuperPursuitReply",
            "ExtraCops_SwarmingReplyFollow",
            "ExtraCops_QuadrentForming",
            "ExtraCops_SuspectPossiblyGone",
            "ExtraCops_QuadrentMoving",
            "ExtraCops_OtherLead",
            "ExtraCops_PossibleSuspect",
            "ExtraCops_WrongSuspect",
            "ExtraCops_SuspectGone",
            "ExtraCops_RBWarning",
            "ExtraCops_RBPosition spikebelt_position",
            "ExtraCops_ExtraRBEngage roadblock_engage_type",
            "ExtraCops_ExtraRBAverted roadblock_engage_type",
            "Cross_CrossBUReply speaker_id:int",
            "Cross_CrossFailReply speaker_id:int on_off_scene",
            "Cross_CrossRBFailReply speaker_id:int on_off_scene",
            "Cross_CrossPursuitEsc speaker_id:int",
            "Cross_CrossSelfStrategy speaker_id:int",
            "Cross_CrossMultiStrategy speaker_id:int",
            "Cross_CrossBailoutDeny_sub speaker_id:int",
            "D_Day",
            "DispIntroRace",
            "Acknowledge speaker_id:int intensity yes_no",
            "Setup_Spotter speaker_id:int num_suspects",
            "Setup_SpotterWanted speaker_id:int",
            "Setup_SpotterReply speaker_id:int num_suspects",
            "Setup_AttmptVehStp speaker_id:int pursuit_type num_suspects speaker_battalion speaker_call_sign_id",
            "Setup_DispGoAhead speaker_id:int",
            "Setup_PrimaryEngage speaker_id:int speaker_battalion speaker_call_sign_id",
        };

        static readonly string[] NisStructTable =
        {
            // NISAudio: Csis structs (event param1..N), from SND_GEN/NISAudio.hpp
            "NIS_Select_Start NIS_Scene_Start NIS_Track NIS_Section",
            "SoundFX_Select SoundFX_Type SoundFX_Param",
            "NIS_Select_Blacklist NIS_Track NIS_Section NIS_Blacklist",
            "NIS_Select_End NIS_Scene_End NIS_Track NIS_Section",
        };

        static readonly string[] EnumTable =
        {
            // copspeech enums (value of the i-th name = 1 << i)
            "code Use_10_code dont_use_10_code",
            "backup_type all_available_units Air_support Rhino_Unit generic_more_backup",
            "bailout_type Generic_low_intensity Heavy_traffic Road_conditions Damage_Sustained Generic_high_intensity",
            "disp_backup_type No_available_units 1_unit all_available_units Air_Support Rhino_Unit Generic_ground_units Super_Pursuit",
            "encounter first_encounter subsequent_encounter",
            "ev_type multiple_units_down gas_station_fire heli_down",
            "heli_lost_visual In_tunnel behind_building Under_trees Generic Restricted_Airspace",
            "intensity Normal High",
            "num_suspects one_suspect multiple_suspects",
            "num_units one_unit_in_pursuit multiple_units_in_pursuit",
            "position Right_Side Left_Side Ahead Behind",
            "pursuit_type Generic_Speeder Possible_Wanted Hit_and_Run Reckless Unit_Rammed",
            "region College_Town Coastal City Alpine",
            "roadblock_type Roadblock_Generic_ Spikes Heli_Roadblock_disp_only_ Multiple_Roadblocks_disp_only_",
            "roadblock_engage_type roadblock spikes",
            "rolling_strategy_type Offset Rolling_Roadblock Box Rolling_Chicane Herding Pit Smokescreen Rolling_Spikes",
            "same_new same_strategy new_strategy",
            "self_strategy_type Offset Rolling_Roadblock Herding Ram Side_Ram Pit Self_Pit Rolling_Spikes Smokescreen",
            "world_object_type generic guardrail train semi gas_station spike_belt low_heat_collision",
            "yes_no Yes_True No_False",
            "speaker_battalion Rosewood Coastal City Alpine Super_Pursuit Rhino_Units Air_Support All_Units Dispatch",
            "subject_battalion Rosewood Coastal City Alpine Super_Pursuit Rhino_Units Air_Support All_Units",
            "speaker_call_sign_id CallSign01 CallSign02 CallSign03 CallSign04 CallSign05 CallSign06 CallSign07 CallSign08 CallSign09 CallSign10 CallSign11 CallSign12 CallSign13 CallSign14 CallSign15 CallSign16 CallSign17 CallSign18 CallSign19 CallSign20 All_units none",
            "subject_call_sign_id CallSign01 CallSign02 CallSign03 CallSign04 CallSign05 CallSign06 CallSign07 CallSign08 CallSign09 CallSign10 CallSign11 CallSign12 CallSign13 CallSign14 CallSign15 CallSign16 CallSign17 CallSign18 CallSign19 CallSign20 district_units none pursuit_units cover_units all_units",
            "time_since_lost lost_recently lost_for_X_time",
            "self_other context_about_self context_about_other_unit",
            "heli_bailout_type flight_conditions low_ammo fuel_low damage_sustained",
            "offroad_moment_id gas_station campus golf_course hospital strip_mall stadium park trailer_park junkyard boatyard refinery trainyard boardwalk beach subway hotel museum police_station hydro_plant construction_yard bus_station drive_in_theatre penitentiary fishery",
            "first_subsequent first_time subsequent_time",
            "address_group_type college_town city coastal alpine generic_any_",
            "heli_self_strategy_type offset_folloe heli_roadblock ram smokescreen rolling_spikes",
            "heli_hazard_alert_type windy_roads approaching_highway approaching_tunnel approaching_city approaching_airport approaching_blimp",
            "heat_level 2 3 4 5 6 7 8 9 10",
            "jurisdiction state federal",
            "disp_backup_eta 15sec 30sec 1min 1min30sec 2min 2min_",
            "spikebelt_position left center right",
            "car_type McLaren Porsche FordGT Viper AstonMartin Corvette BMW Mercedes Audi Mitsubishi Mustang Lotus Subaru Camaro GTO Nissan Mazda Renault Lexus Mini Volkswagen SUV Pickup Pagani Lamborghini Chrysler Opel Sedan SportsCar Supra Cadillac",
            "direction Northbound Southbound Eastbound Westbound",
            "car_color Red Black White Blue Green Orange Silver Gold Purple Brown Yellow Pink Beige",
            "speed over_speed_limit speeds_in_excess_of_100 speeds_in_excess_of_120 speeds_in_excess_of_140 speeds_in_excess_of_160 speeds_in_excess_of_180 speeds_in_excess_of_200 speeds_in_excess_of_220 speeds_in_excess_of_240 speeds_in_excess_of_260 speeds_in_excess_of_280 speeds_in_excess_of_300",
            "location Location_1 Location_2 Location_3 Location_4 Location_5 Location_6 Location_7 Location_8 Location_9 Location_10 Location_11 Location_12 Location_13 Location_14 Location_15 Location_16 Location_17 Location_18 Location_19 Location_20 Location_21 Location_22 Location_23 Location_24 Location_25 Location_26 Location_27 Location_28 Location_29 Location_30 Location_31",
            "location_region college_town coastal coastal_extra city",
            "measurement generic imperial_only metric_only",
            "projectile_type Smokescreen Rolling_Spikes",
            "cell_call_bucket situational_call bucket_01 bucket_02 bucket_03 bucket_04 bucket_05 bucket_06 bucket_07 bucket_08 bucket_09 bucket_10 bucket_11 bucket_12 bucket_13 bucket_14 bucket_15 bucket_16",
            "cell_call_number call_01 call_02 call_03 call_04 call_05 call_06 call_07 call_08 call_09 call_10",
            "on_off_scene on_scene off_scene",
            // NISAudio enums (value of the i-th name = 1 << i)
            "NIS_Scene_Start IntroNis01 IntroNis02 IntroNis03 IntroNis04 IntroNis05 IntroNis06 IntroNis07 IntroNis08 IntroNis09 IntroNis10 IntroNis11 dummy flythrough safehouse dday e3 opm toll generic ddayend",
            "NIS_Track Track01 Track02 Track03 Track04 Track05 Track06 Track07 Track08 Track09 Track10 Track11 Track12 Track13 Track00 Enter Exit Run Generic Track14 Track15",
            "NIS_Scene_End Arrest_M01 Arrest_M04 Arrest_M06 Arrest_M14 Arrest_M16 Arrest_M19 Arrest_M23 Arrest_F02 Arrest_F06 Arrest_F07 Arrest_F14 Arrest_F23 E3 opm Arrest_F18 Arrest_M07 generic end01 end02 end03 end04",
            "NIS_Section Complete Start End",
            "SoundFX_Type Pursbreaker Crash Thunder engine Car GamePlayMoment jump game",
            "SoundFX_Param distant sweetener blow total expl_gas_station truck_jackknife boatfall trck_log_dump stripmall scaffolding_big drive_in water_tower porch_collapse comm_tower fish_market_sign patio_collapse start_music end_music start_effects end_effects cam break amphitheatre gate_signage trailer_park torus_shop gazebo uves zoom camera police_tower",
            "NIS_Blacklist Black01 Black02 Black03 Black04 Black05 Black06 Black07 Black08 Black09 Black10 Black11 Black12 Black13 Black14 Black15 Black16 Black06end Black07end generic",
        };
    }
}
