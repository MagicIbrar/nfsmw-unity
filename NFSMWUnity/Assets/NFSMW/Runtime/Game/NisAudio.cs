using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// SOUND\STREAMS\NISAudio.big: 142 SCHl streams (44.1 kHz EA-XA, mostly 5.1) in four sets. NISAudio.idx holds
    /// u32 1, u32 set count, then from 0x58 one 16-byte entry per set {0x0100000N, u32, u32, offset in the .big};
    /// NISAudio.csi names them SoundFX_Select, NIS_Select_Blacklist, NIS_Select_End and NIS_Select_Start. Which stream
    /// a scene uses is chosen by the .evt event scripts (not decoded); set 1 opens with 13 streams whose lengths match
    /// the 13 arrest scenes, which is what the busted sequence plays.
    /// </summary>
    public static class NisAudio
    {
        const int ArrestSet = 1, ArrestCount = 13;
        static List<(long start, long end)>[] _sets;

        static string Big => Path.Combine(GameData.Root, "SOUND", "STREAMS", "NISAudio.big");

        static void Index()
        {
            if (_sets != null) return;
            GameData.Mount();
            var idx = Path.Combine(GameData.Root, "SOUND", "STREAMS", "NISAudio.idx");
            _sets = new List<(long, long)>[0];
            if (!File.Exists(idx) || !File.Exists(Big)) return;
            var d = File.ReadAllBytes(idx);
            int count = Chunks.I32(d, 4);
            var starts = new List<long>();
            for (int i = 0; i < count && 0x58 + i * 16 + 16 <= d.Length; i++) starts.Add((uint)Chunks.I32(d, 0x58 + i * 16 + 12));
            using var f = File.OpenRead(Big);
            starts.Add(f.Length);
            _sets = new List<(long, long)>[count];
            for (int s = 0; s < count; s++) _sets[s] = Walk(f, starts[s], starts[s + 1]);
        }

        /// <summary>Stream extents between two offsets, following the block sizes (streams are padded apart).</summary>
        static List<(long, long)> Walk(FileStream f, long pos, long end)
        {
            var list = new List<(long, long)>();
            var head = new byte[8];
            var scan = new byte[0x1000];
            while (pos + 8 <= end)
            {
                f.Position = pos;
                f.Read(head, 0, 8);
                if (head[0] != 'S' || head[1] != 'C' || head[2] != 'H' || head[3] != 'l')
                {
                    int n = f.Read(scan, 0, (int)System.Math.Min(scan.Length, end - pos - 8));
                    int at = -1;
                    for (int i = 0; i + 4 <= n; i++)
                        if (scan[i] == 'S' && scan[i + 1] == 'C' && scan[i + 2] == 'H' && scan[i + 3] == 'l') { at = i; break; }
                    if (at < 0) break;
                    pos += 8 + at;
                    continue;
                }
                long start = pos;
                while (pos + 8 <= end)
                {
                    f.Position = pos;
                    f.Read(head, 0, 8);
                    int size = head[4] | head[5] << 8 | head[6] << 16 | head[7] << 24;
                    bool last = head[0] == 'S' && head[1] == 'C' && head[2] == 'E' && head[3] == 'l';
                    if (size <= 0) { pos = end; break; }
                    pos += size;
                    if (last) break;
                }
                list.Add((start, pos));
            }
            return list;
        }

        static AudioClip Clip(string name, (long start, long end) at)
        {
            var buf = new byte[at.end - at.start];
            using (var f = File.OpenRead(Big))
            {
                f.Position = at.start;
                f.Read(buf, 0, buf.Length);
            }
            var s = EaStream.Read(buf, 0, out _);
            if (s == null || s.Samples == 0) return null;
            var clip = AudioClip.Create(name, s.Samples, 2, s.SampleRate, false);
            clip.SetData(s.Stereo(), 0);
            return clip;
        }

        /// <summary>One of the arrest scene streams, at random; null if the archive is missing.</summary>
        public static AudioClip Arrest()
        {
            try
            {
                Index();
                if (_sets.Length <= ArrestSet || _sets[ArrestSet].Count == 0) return null;
                int n = Random.Range(0, Mathf.Min(ArrestCount, _sets[ArrestSet].Count));
                return Clip($"NIS_Arrest_{n}", _sets[ArrestSet][n]);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"NFSMW nis audio: {e.Message}");
                return null;
            }
        }

        public static int StreamCount
        {
            get
            {
                Index();
                int n = 0;
                foreach (var s in _sets) n += s.Count;
                return n;
            }
        }
    }
}
