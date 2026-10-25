using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// Police radio speech, SOUND\SPEECH\copspeech.*. Formats and the SPCH sentence chooser are documented on
    /// <see cref="SpeechScript"/>; this class keeps the simple per-line view:
    /// <list type="bullet">
    /// <item>.idx: CLUMP_IDX_FILE, 579 16-byte items @0x58 {key, header size, header offset, .big offset}; each header
    /// is an SPCH bank {u16 id, u16 sub (speaker_id), u8 nAttr|0x80, u8 take count, ...} with take cues from +12, each
    /// a big-endian u16 x (unit) bytes from the line's .big offset to that take's SCHl stream (MicroTalk 24 kHz),
    /// entry stride 2 + nAttr.</item>
    /// <item><see cref="Events"/>: per event, every line (bank, all speakers) its sentences can use, in slot order.
    /// Use <see cref="Script"/>.Choose to pick the exact sentence for event parameters.</item>
    /// </list>
    /// </summary>
    public sealed class CopSpeech
    {
        public readonly List<long[]> Lines = new List<long[]>();   // per line: absolute .big offsets of its takes
        public readonly Dictionary<string, List<int>> Events = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The decoded .evt scripts (SPCH chooser).</summary>
        public SpeechScript Script = new SpeechScript();
        string _big;

        public static CopSpeech Load(string gameRoot)
        {
            string dir = Path.Combine(gameRoot, "SOUND", "SPEECH");
            var s = new CopSpeech { _big = Path.Combine(dir, "copspeech.big") };
            string idxPath = Path.Combine(dir, "copspeech.idx"), csiPath = Path.Combine(dir, "copspeech.csi"), evtPath = Path.Combine(dir, "copspeech.evt");
            if (!File.Exists(idxPath) || !File.Exists(s._big)) return s;
            var idx = File.ReadAllBytes(idxPath);
            int count = Chunks.I32(idx, 4);
            for (int i = 0; i < count && 0x58 + i * 16 + 16 <= idx.Length; i++)
            {
                int e = 0x58 + i * 16;
                int size = Chunks.I32(idx, e + 4), info = Chunks.I32(idx, e + 8);
                long baseOff = (uint)Chunks.I32(idx, e + 12);
                int stride = 2 + (idx[info + 4] & 0x7F), takes = idx[info + 5];
                var list = new List<long>();
                for (int t = 0; t < takes; t++)
                {
                    int p = info + 12 + t * stride;
                    if (p + 2 > info + size) break;
                    list.Add(baseOff + ((idx[p] << 8) | idx[p + 1]) * 0x100L);
                }
                s.Lines.Add(list.ToArray());
            }
            if (File.Exists(evtPath))
            {
                s.Script = SpeechScript.Load(gameRoot, "copspeech");
                foreach (var name in s.Script.EventNames)
                {
                    var e = s.Script.GetEvent(name);
                    var lines = new List<int>();
                    foreach (var sen in e.Sentences)
                        foreach (var sl in sen.Slots)
                            foreach (var bank in s.Script.Banks)
                                if (bank.Id == sl.BankId && !lines.Contains(bank.Line)) lines.Add(bank.Line);
                    s.Events[name] = lines;
                }
            }
            return s;
        }

        /// <summary>A take chosen by <see cref="SpeechScript.Choose"/>, decoded.</summary>
        public EaStream Take(SpeechSample sample) => Script.ReadTake(sample);

        /// <summary>One take of a line, decoded (mono, 24 kHz).</summary>
        public EaStream Take(int line, int take)
        {
            if (line < 0 || line >= Lines.Count || Lines[line].Length == 0) return null;
            var takes = Lines[line];
            long at = takes[Math.Abs(take) % takes.Length];
            using var f = File.OpenRead(_big);
            f.Position = at;
            var buf = new byte[(int)Math.Min(0x40000, f.Length - at)];
            int n = f.Read(buf, 0, buf.Length);
            if (n < buf.Length) Array.Resize(ref buf, n);
            return EaStream.Read(buf, 0, out _);
        }
    }
}
