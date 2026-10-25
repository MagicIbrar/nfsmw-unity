using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// EA XAS v0 ADPCM as decoded by speed.exe @0x4E1070: 0x13-byte frames of 32 samples.
    /// Bytes 0-1: history 2 (high 12 bits, int16) with the coefficient index in the low nibble of byte 0;
    /// bytes 2-3: history 1 with the shift in the low nibble of byte 2; then 15 bytes of nibbles (high first):
    /// s = c1*s1 + c2*s2 + table[shift][nibble], table = signed nibble * 4096 >> shift (0 for shift 13..15).
    /// </summary>
    public static class EaXas
    {
        static readonly float[] C1 = { 0f, 0.9375f, 1.796875f, 1.53125f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        static readonly float[] C2 = { 0f, 0f, -0.8125f, -0.859375f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        public const int FrameBytes = 0x13, FrameSamples = 32;

        public static float[] DecodeMono(byte[] d, int offset, int samples)
        {
            var output = new float[samples];
            int frames = (samples + 31) / 32;
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                int p = offset + f * FrameBytes;
                if (p + FrameBytes > d.Length) break;
                float h2 = (d[p] & 0xF0) + (sbyte)d[p + 1] * 256f;
                float h1 = (d[p + 2] & 0xF0) + (sbyte)d[p + 3] * 256f;
                int ci = d[p] & 0xF, shift = d[p + 2] & 0xF;
                float c1 = C1[ci], c2 = C2[ci];
                Put(output, ref o, h2);
                Put(output, ref o, h1);
                for (int i = 0; i < 15; i++)
                {
                    byte b = d[p + 4 + i];
                    for (int k = 0; k < 2; k++)
                    {
                        int nib = k == 0 ? b >> 4 : b & 0xF;
                        float delta = shift >= 13 ? 0 : (((nib ^ 8) - 8) * 4096) >> shift;
                        float s = c1 * h1 + c2 * h2 + delta;
                        h2 = h1;
                        h1 = s;
                        Put(output, ref o, s);
                    }
                }
            }
            return output;

            static void Put(float[] buf, ref int i, float v)
            {
                if (i < buf.Length) buf[i++] = Math.Max(-1f, Math.Min(1f, v / 32768f));
            }
        }
    }

    /// <summary>
    /// EA-XA v2 ADPCM (speed.exe @0x828D60), used by the sound banks and SCHl streams: 15-byte frames of 28 samples,
    /// byte 0 = coefficient index (high nibble) and shift (low nibble), then 14 bytes of nibbles (high first); a frame
    /// starting 0xEE is raw: two big-endian history samples then 28 big-endian PCM samples (61 bytes).
    /// </summary>
    public static class EaXa
    {
        static readonly float[] C1 = { 0f, 0.9375f, 1.796875f, 1.53125f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        static readonly float[] C2 = { 0f, 0f, -0.8125f, -0.859375f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        public static float[] DecodeMono(byte[] d, int offset, int samples)
        {
            var output = new float[samples];
            float h1 = 0, h2 = 0;
            int o = 0;
            Decode(d, offset, output, ref o, samples, ref h1, ref h2);
            return output;
        }

        /// <summary>Decodes up to <paramref name="count"/> samples into output[o..], carrying the history (stream blocks).</summary>
        public static void Decode(byte[] d, int p, float[] output, ref int o, int count, ref float h1, ref float h2)
        {
            int end = Math.Min(output.Length, o + count);
            while (o < end && p < d.Length)
            {
                byte info = d[p];
                if (info == 0xEE)
                {
                    if (p + 61 > d.Length) break;
                    for (int i = 0; i < 28 && o < end; i++)
                        output[o++] = (short)(d[p + 5 + i * 2] << 8 | d[p + 6 + i * 2]) / 32768f;
                    h1 = (short)(d[p + 59] << 8 | d[p + 60]);
                    h2 = (short)(d[p + 57] << 8 | d[p + 58]);
                    p += 61;
                    continue;
                }
                if (p + 15 > d.Length) break;
                float c1 = C1[info >> 4], c2 = C2[info >> 4];
                int shift = info & 0xF;
                for (int i = 0; i < 14; i++)
                {
                    byte b = d[p + 1 + i];
                    for (int k = 0; k < 2; k++)
                    {
                        int nib = k == 0 ? b >> 4 : b & 0xF;
                        float delta = shift >= 13 ? 0 : (((nib ^ 8) - 8) * 4096) >> shift;
                        float s = c1 * h1 + c2 * h2 + delta;
                        s = Math.Max(-32768f, Math.Min(32767f, s));
                        h2 = h1;
                        h1 = s;
                        if (o < end) output[o++] = s / 32768f;
                    }
                }
                p += 15;
            }
        }
    }

    /// <summary>
    /// EA "SCHl" stream (SOUND\STREAMS\*.big, SOUND\SPEECH\*.big): 'SCHl' size, "PT" u16 platform, tag/length/big-endian
    /// value fields (0x80 version, 0x82 channels, 0x84 rate, 0x85 samples, 0xA0 codec2; 0xFD/0xFE open sub-headers,
    /// 0xFF ends), then 'SCCl' {block count}, 'SCDl' blocks {u32 samples, u32 offset per channel from the end of that
    /// table, then each channel's EA-XA v2 frames, history carried across blocks} and 'SCEl'. Decodes the default
    /// PC codec (EA-XA) and codec2 4 (MicroTalk, copspeech.big).
    /// </summary>
    public sealed class EaStream
    {
        public int Channels = 1, SampleRate = 22050, Samples, Codec2 = -1, Version;
        /// <summary>"GSTR" header variant (MW_Music.mus): block sample counts and channel offsets are big-endian.</summary>
        public bool BigEndianBlocks;
        public float[][] Data;
        public float Length => SampleRate > 0 ? Samples / (float)SampleRate : 0;

        /// <summary>Reads the header at d[o]; returns null if it is not an SCHl stream.</summary>
        public static EaStream ReadHeader(byte[] d, int o, out int headerEnd)
        {
            headerEnd = o;
            if (o + 12 > d.Length || d[o] != 'S' || d[o + 1] != 'C' || d[o + 2] != 'H' || d[o + 3] != 'l') return null;
            int size = Chunks.I32(d, o + 4);
            headerEnd = o + size;
            var s = new EaStream();
            int p = o + 12, end = Math.Min(d.Length, o + size);
            if (d[o + 8] == 'G' && d[o + 9] == 'S' && d[o + 10] == 'T' && d[o + 11] == 'R') { p = o + 16; s.BigEndianBlocks = true; }
            while (p < end)
            {
                int tag = d[p++];
                if (tag == 0xFF) break;
                if (tag == 0xFD || tag == 0xFE) continue;
                int n = d[p++], v = 0;
                for (int i = 0; i < n && p + i < end; i++) v = v << 8 | d[p + i];
                p += n;
                switch (tag)
                {
                    case 0x80: s.Version = v; break;
                    case 0x82: s.Channels = v; break;
                    case 0x84: s.SampleRate = v; break;
                    case 0x85: s.Samples = v; break;
                    case 0xA0: s.Codec2 = v; break;
                }
            }
            return s;
        }

        /// <summary>Decodes the stream starting at d[o]; <paramref name="next"/> is the first byte after its 'SCEl'.</summary>
        public static EaStream Read(byte[] d, int o, out int next)
        {
            var s = ReadHeader(d, o, out int p);
            next = p;
            if (s == null) return null;
            s.Data = new float[s.Channels][];
            for (int c = 0; c < s.Channels; c++) s.Data[c] = new float[s.Samples];
            var h1 = new float[s.Channels];
            var h2 = new float[s.Channels];
            int written = 0;
            bool decode = s.Codec2 < 0 || s.Codec2 == 0x0A;
            bool microTalk = s.Codec2 == 4;
            MicroTalk[] mt = null;
            if (microTalk)
            {
                mt = new MicroTalk[s.Channels];
                for (int c = 0; c < s.Channels; c++) mt[c] = new MicroTalk(s.Version >= 2); // v2+ PC: frames carry a PCM marker byte
            }
            while (p + 8 <= d.Length)
            {
                int size = Chunks.I32(d, p + 4);
                bool data = d[p] == 'S' && d[p + 1] == 'C' && d[p + 2] == 'D' && d[p + 3] == 'l';
                bool last = d[p] == 'S' && d[p + 1] == 'C' && d[p + 2] == 'E' && d[p + 3] == 'l';
                if (data && microTalk)
                {
                    int n = Chunks.I32(d, p + 8), table = p + 12, body = table + 4 * s.Channels;
                    for (int c = 0; c < s.Channels; c++)
                    {
                        // each channel's data opens with a flag byte (0x01 = stream start)
                        mt[c].SetBuffer(d, body + Chunks.I32(d, table + 4 * c) + 1, p + size);
                        for (int done = 0; done < n && written + done < s.Samples; done += MicroTalk.FrameSamples)
                            mt[c].DecodeFrame(s.Data[c], written + done, Math.Min(MicroTalk.FrameSamples, n - done));
                    }
                    written = Math.Min(s.Samples, written + n);
                }
                else if (data && decode)
                {
                    int n = s.Block(d, p + 8), table = p + 12, body = table + 4 * s.Channels;
                    for (int c = 0; c < s.Channels; c++)
                    {
                        int o2 = written;
                        EaXa.Decode(d, body + s.Block(d, table + 4 * c), s.Data[c], ref o2, n, ref h1[c], ref h2[c]);
                    }
                    written = Math.Min(s.Samples, written + n);
                }
                if (size <= 0) break;
                p += size;
                if (last) break;
            }
            next = p;
            return s;
        }

        int Block(byte[] d, int at) => BigEndianBlocks ? d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3] : Chunks.I32(d, at);

        /// <summary>Interleaved stereo: 6 channels are taken as 5.1 (L R C LFE Ls Rs), 1 is duplicated.</summary>
        public float[] Stereo()
        {
            var o = new float[Samples * 2];
            for (int i = 0; i < Samples; i++)
            {
                float l, r;
                if (Channels >= 6)
                {
                    float c = Data[2][i] * 0.707f, lfe = Data[3][i] * 0.5f;
                    l = Data[0][i] + c + lfe + Data[4][i] * 0.707f;
                    r = Data[1][i] + c + lfe + Data[5][i] * 0.707f;
                    l *= 0.6f;
                    r *= 0.6f;
                }
                else if (Channels >= 2) { l = Data[0][i]; r = Data[1][i]; }
                else l = r = Data[0][i];
                o[i * 2] = Math.Max(-1f, Math.Min(1f, l));
                o[i * 2 + 1] = Math.Max(-1f, Math.Min(1f, r));
            }
            return o;
        }
    }

    public sealed class BankSound
    {
        public int Index, SampleRate, Channels = 1, Samples, LoopStart = -1, LoopEnd = -1;
        public float[] Data;
        /// <summary>The second channel of a 2-channel sound (PT tag 0x89 = its data offset; the channels are stored
        /// one after the other), null for mono.</summary>
        public float[] DataR;
        public bool Loops => LoopEnd > LoopStart && LoopStart >= 0;
    }

    /// <summary>
    /// EA sound bank: ".abk" files ("ABKC") carry a "BNKl" bank: u16 version (5), u16 count, u32 size, 8 bytes,
    /// then count offsets relative to each entry (0 = empty) to "PT\0\0" headers of tag/length/big-endian value
    /// fields (0x82 channels, 0x84 sample rate, 0x85 samples, 0x86/0x87 loop, 0x88 data offset from the bank start),
    /// ending with 0xFF; data is EA-XA v2.
    /// </summary>
    public sealed class SoundBank
    {
        public readonly List<BankSound> Sounds = new List<BankSound>();

        public static SoundBank Load(string path) => Read(File.ReadAllBytes(path));

        public static SoundBank Read(byte[] d)
        {
            var bank = new SoundBank();
            int b = IndexOf(d, (byte)'B', (byte)'N', (byte)'K', (byte)'l');
            if (b < 0) return bank;
            int n = Chunks.U16(d, b + 6);
            for (int i = 0; i < n; i++)
            {
                int field = b + 20 + i * 4;
                if (field + 4 > d.Length) break;
                int off = Chunks.I32(d, field);
                if (off == 0) continue;
                int pt = field + off;
                if (pt + 4 > d.Length || d[pt] != 'P' || d[pt + 1] != 'T') continue;
                var s = new BankSound { Index = i, SampleRate = 22050 };
                int dataOff = -1, dataOffR = -1;
                int o = pt + 4;
                while (o < d.Length)
                {
                    int tag = d[o++];
                    if (tag == 0xFF) break;
                    if (tag == 0xFC || tag == 0xFD || tag == 0xFE) continue;
                    int len = d[o++];
                    long v = 0;
                    for (int k = 0; k < len && o + k < d.Length; k++) v = v << 8 | d[o + k];
                    o += len;
                    switch (tag)
                    {
                        case 0x82: s.Channels = (int)v; break;
                        case 0x84: s.SampleRate = (int)v; break;
                        case 0x85: s.Samples = (int)v; break;
                        case 0x86: s.LoopStart = (int)v; break;
                        case 0x87: s.LoopEnd = (int)v; break;
                        case 0x88: dataOff = (int)v; break;
                        case 0x89: dataOffR = (int)v; break;
                    }
                }
                if (dataOff < 0 || s.Samples <= 0) continue;
                s.Data = EaXa.DecodeMono(d, b + dataOff, s.Samples);
                if (s.Channels >= 2 && dataOffR > 0) s.DataR = EaXa.DecodeMono(d, b + dataOffR, s.Samples);
                bank.Sounds.Add(s);
            }
            return bank;
        }

        static int IndexOf(byte[] d, byte a, byte b2, byte c, byte e)
        {
            for (int i = 0; i + 3 < d.Length; i++)
                if (d[i] == a && d[i + 1] == b2 && d[i + 2] == c && d[i + 3] == e) return i;
            return -1;
        }
    }

    /// <summary>
    /// GIN ("Gnsu20") granular engine recording: header {"Gnsu", "20\0\0", float min rpm, float max rpm,
    /// u32 grain count, u32 table count, u32 samples, u32 sample rate}, grain start samples, a second table,
    /// padding to 16, then XAS v0 audio. The recording sweeps the rev range; grain i sits at rpm min+(max-min)*i/(n-1).
    /// </summary>
    public sealed class GinSound
    {
        public float MinRpm, MaxRpm;
        public int SampleRate;
        public int[] Grains = Array.Empty<int>();
        public float[] Samples = Array.Empty<float>();

        public static GinSound Load(string path) => Read(File.ReadAllBytes(path));

        public static GinSound Read(byte[] d)
        {
            if (d.Length < 0x20 || d[0] != 'G' || d[1] != 'n' || d[2] != 's' || d[3] != 'u') throw new InvalidDataException("not a GIN file");
            var g = new GinSound
            {
                MinRpm = Chunks.F32(d, 8),
                MaxRpm = Chunks.F32(d, 12),
                SampleRate = Chunks.I32(d, 0x1C),
            };
            int grains = Chunks.I32(d, 0x10), samples = Chunks.I32(d, 0x18);
            g.Grains = new int[grains];
            for (int i = 0; i < grains; i++) g.Grains[i] = Chunks.I32(d, 0x20 + i * 4);
            int frames = (samples + 31) / 32;
            int start = d.Length - frames * EaXas.FrameBytes;
            g.Samples = EaXas.DecodeMono(d, start, samples);
            return g;
        }
    }
}
