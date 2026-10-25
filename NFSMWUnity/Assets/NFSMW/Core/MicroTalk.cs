// EA MicroTalk (UTK) decoder, ported to C# from vgmstream's src/coding/libs/utkdec.c:
//
// Copyright (c) 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm, Christopher Snowhill, NicknineTheEagle,
// bxaimc, Thealexbarney, CyberBotX, EdnessP, et al
//
// Permission to use, copy, modify, and distribute this software for any purpose with or without fee is hereby
// granted, provided that the above copyright notice and this permission notice appear in all copies.
//
// THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING
// ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL,
// DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
// WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE
// USE OR PERFORMANCE OF THIS SOFTWARE.

using System;

namespace NfsMw.Core
{
    /// <summary>
    /// EA MicroTalk 10:1 / 5:1 speech codec (SCHl codec2 4, e.g. SOUND\SPEECH\copspeech.big): 432-sample frames of
    /// 12 reflection coefficients and four 108-sample subframes of pitch + multi-pulse or RELP excitation, through a
    /// 12-tap LPC synthesis filter. In version-2 streams each frame starts with a byte; 0xEE adds raw PCM that
    /// overwrites part of the frame. State carries across SCDl blocks; the bit reader restarts at each block.
    /// </summary>
    public sealed class MicroTalk
    {
        static readonly float[] RcTable =
        {
            +0.000000f, -0.996776f, -0.990327f, -0.983879f, -0.977431f, -0.970982f, -0.964534f, -0.958085f,
            -0.951637f, -0.930754f, -0.904960f, -0.879167f, -0.853373f, -0.827579f, -0.801786f, -0.775992f,
            -0.750198f, -0.724405f, -0.698611f, -0.670635f, -0.619048f, -0.567460f, -0.515873f, -0.464286f,
            -0.412698f, -0.361111f, -0.309524f, -0.257937f, -0.206349f, -0.154762f, -0.103175f, -0.051587f,
            +0.000000f, +0.051587f, +0.103175f, +0.154762f, +0.206349f, +0.257937f, +0.309524f, +0.361111f,
            +0.412698f, +0.464286f, +0.515873f, +0.567460f, +0.619048f, +0.670635f, +0.698611f, +0.724405f,
            +0.750198f, +0.775992f, +0.801786f, +0.827579f, +0.853373f, +0.879167f, +0.904960f, +0.930754f,
            +0.951637f, +0.958085f, +0.964534f, +0.970982f, +0.977431f, +0.983879f, +0.990327f, +0.996776f,
        };

        static readonly byte[][] Codebooks =
        {
            new byte[]
            {
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 21,
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 25,
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 22,
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 0,
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 21,
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 26,
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 22,
                4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 2,
            },
            new byte[]
            {
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 27,
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 1,
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 28,
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 3,
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 27,
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 1,
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 28,
                4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 3,
            },
        };

        // {next model, code size, pulse value}
        static readonly (int model, int size, float pulse)[] Commands =
        {
            (1, 8, 0f), (1, 7, 0f), (0, 8, 0f), (0, 7, 0f), (0, 2, 0f), (0, 2, -1f), (0, 2, +1f), (0, 3, -1f),
            (0, 3, +1f), (1, 4, -2f), (1, 4, +2f), (1, 3, -2f), (1, 3, +2f), (1, 5, -3f), (1, 5, +3f), (1, 4, -3f),
            (1, 4, +3f), (1, 6, -4f), (1, 6, +4f), (1, 5, -4f), (1, 5, +4f), (1, 7, -5f), (1, 7, +5f), (1, 6, -5f),
            (1, 6, +5f), (1, 8, -6f), (1, 8, +6f), (1, 7, -6f), (1, 7, +6f),
        };

        public const int FrameSamples = 432;
        const int Samples = 324; // offset of the output samples in _sub (adaptive codebook before them)

        readonly bool _pcmFrames;
        bool _parsed, _reducedBandwidth;
        int _multipulseThreshold;
        readonly float[] _gains = new float[64], _rc = new float[12], _history = new float[12];
        readonly float[] _sub = new float[324 + 432];

        byte[] _d;
        int _p, _end;
        uint _bits;
        int _bitCount;

        public MicroTalk(bool pcmFrames) { _pcmFrames = pcmFrames; }

        /// <summary>Points the bit reader at new data (a new block); the decoder state is kept.</summary>
        public void SetBuffer(byte[] d, int start, int end)
        {
            _d = d;
            _p = start;
            _end = Math.Min(end, d.Length);
            _bitCount = 0;
        }

        public int Position => _p;

        byte ReadByte() => _p < _end ? _d[_p++] : (byte)0;

        short ReadS16() => (short)(ReadByte() << 8 | ReadByte());

        void InitBits()
        {
            if (_bitCount == 0) { _bits = ReadByte(); _bitCount = 8; }
        }

        int Peek(int count) => (int)(_bits & ((1u << count) - 1));

        int Read(int count)
        {
            int r = (int)(_bits & ((1u << count) - 1));
            _bits >>= count;
            _bitCount -= count;
            if (_bitCount < 8)
            {
                _bits |= (uint)ReadByte() << _bitCount;
                _bitCount += 8;
            }
            return r;
        }

        void ParseHeader()
        {
            _reducedBandwidth = Read(1) == 1;
            int thre = Read(4), gain = Read(4), mult = Read(6);
            _multipulseThreshold = 32 - thre;
            _gains[0] = 8.0f * (1 + gain);
            float m = 1.04f + mult * 0.001f;
            for (int i = 1; i < 64; i++) _gains[i] = _gains[i - 1] * m;
        }

        void DecodeExcitation(bool multipulse, float[] o, int at, int stride)
        {
            int i = 0;
            if (multipulse)
            {
                int model = 0;
                while (i < 108)
                {
                    int cmd = Codebooks[model][Peek(8)];
                    model = Commands[cmd].model;
                    Read(Commands[cmd].size);
                    if (cmd > 3)
                    {
                        o[at + i] = Commands[cmd].pulse;
                        i += stride;
                    }
                    else if (cmd > 1)
                    {
                        int count = 7 + Read(6);
                        if (i + count * stride > 108) count = (108 - i) / stride;
                        while (count-- > 0) { o[at + i] = 0; i += stride; }
                    }
                    else
                    {
                        int x = 7;
                        while (Read(1) != 0) x++;
                        if (Read(1) == 0) x = -x;
                        o[at + i] = x;
                        i += stride;
                    }
                }
            }
            else
            {
                while (i < 108)
                {
                    float v = 0;
                    int bits = 1;
                    switch (Peek(2))
                    {
                        case 1: v = -2f; bits = 2; break;
                        case 3: v = 2f; bits = 2; break;
                    }
                    Read(bits);
                    o[at + i] = v;
                    i += stride;
                }
            }
        }

        static void RcToLpc(float[] rc, float[] lpc)
        {
            var t1 = new float[12];
            var t2 = new float[12];
            for (int i = 10; i >= 0; i--) t2[i + 1] = rc[i];
            t2[0] = 1f;
            for (int i = 0; i < 12; i++)
            {
                float x = -(rc[11] * t2[11]);
                for (int j = 10; j >= 0; j--)
                {
                    x -= rc[j] * t2[j];
                    t2[j + 1] = x * rc[j] + t2[j];
                }
                t2[0] = x;
                t1[i] = x;
                for (int j = 0; j < i; j++) x -= t1[i - 1 - j] * lpc[j];
                lpc[i] = x;
            }
        }

        void Synthesis(int offset, int blocks)
        {
            var lpc = new float[12];
            RcToLpc(_rc, lpc);
            int p = Samples + offset;
            for (int i = 0; i < blocks; i++)
                for (int j = 0; j < 12; j++)
                {
                    float x = _sub[p];
                    int k = 0;
                    for (; k < j; k++) x += lpc[k] * _history[k - j + 12];
                    for (; k < 12; k++) x += lpc[k] * _history[k - j];
                    _history[11 - j] = x;
                    _sub[p++] = x;
                }
        }

        static void Interpolate(float[] e, int at)
        {
            for (int i = 0; i < 108; i += 2)
            {
                int c = at + i;
                float a = (e[c - 5] + e[c + 5]) * 0.01803268f;
                float b = (e[c - 3] + e[c + 3]) * 0.11459156f;
                float d = (e[c - 1] + e[c + 1]) * 0.59738597f;
                e[c] = a - b + d;
            }
        }

        void DecodeMain()
        {
            bool multipulse = false;
            var exc = new float[5 + 108 + 5];
            var delta = new float[12];
            InitBits();
            if (!_parsed) { ParseHeader(); _parsed = true; }
            for (int i = 0; i < 12; i++)
            {
                int idx;
                if (i == 0)
                {
                    idx = Read(6);
                    if (idx < _multipulseThreshold) multipulse = true;
                }
                else if (i < 4) idx = Read(6);
                else idx = 16 + Read(5);
                delta[i] = (RcTable[idx] - _rc[i]) * 0.25f;
            }
            for (int i = 0; i < 4; i++)
            {
                int lag = Read(8), pitchValue = Read(4), gainIndex = Read(6);
                float pitchGain = pitchValue / 15f;
                float fixedGain = _gains[gainIndex];
                if (!_reducedBandwidth) DecodeExcitation(multipulse, exc, 5, 1);
                else
                {
                    int align = Read(1), zero = Read(1);
                    DecodeExcitation(multipulse, exc, 5 + align, 2);
                    if (zero != 0)
                    {
                        for (int j = 0; j < 54; j++) exc[5 + (1 - align) + 2 * j] = 0;
                    }
                    else
                    {
                        for (int j = 0; j < 5; j++) { exc[j] = 0; exc[5 + 108 + j] = 0; }
                        Interpolate(exc, 5 + (1 - align));
                        fixedGain *= 0.5f;
                    }
                }
                for (int j = 0; j < 108; j++)
                {
                    int idx = 108 * i + 216 - lag + j;
                    if (idx < 0) idx = 0;
                    _sub[Samples + 108 * i + j] = fixedGain * exc[5 + j] + pitchGain * _sub[idx];
                }
            }
            for (int i = 0; i < 324; i++) _sub[i] = _sub[Samples + 108 + i];
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 12; j++) _rc[j] += delta[j];
                Synthesis(12 * i, i < 3 ? 1 : 33);
            }
        }

        /// <summary>Decodes one frame; the 432 samples go to output[at..] as -1..1 (fewer if count is smaller).</summary>
        public void DecodeFrame(float[] output, int at, int count)
        {
            if (_pcmFrames)
            {
                bool pcm = ReadByte() == 0xEE;
                DecodeMain();
                _p--;           // the reader fetched one byte ahead
                _bitCount = 0;
                if (pcm)
                {
                    int offset = ReadS16(), n = ReadS16();
                    if (offset >= 0 && offset <= 432 && n >= 0 && n <= 432 - offset)
                        for (int i = 0; i < n; i++) _sub[Samples + offset + i] = ReadS16();
                }
            }
            else DecodeMain();
            for (int i = 0; i < count && i < FrameSamples && at + i < output.Length; i++)
            {
                float s = _sub[Samples + i];
                s = s >= 0 ? s + 0.5f : s - 0.5f;
                output[at + i] = Math.Max(-32768f, Math.Min(32767f, (int)s)) / 32768f;
            }
        }
    }
}
