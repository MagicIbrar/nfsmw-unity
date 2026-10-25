using System;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// A "Gnsu20" granular engine recording (SOUND\ENGINE\GIN_*.gin), bound the way GinsuSynthData::BindToData does
    /// (EAXSound/Ginsu/ginsudata.cpp): header {char id[4] "Gnsu", char ver[2] "20", u16 flags, float minFrequency,
    /// float maxFrequency, int segCount, int cycleCount, int sampleCount, int sampleRate}, then segCount+1 sample
    /// positions for evenly spaced "frequencies" (MW uses rpm units: min..max rpm of the recorded sweep), cycleCount+1
    /// sample positions of every engine firing cycle (pitch marks), then XAS v0 blocks (0x13 bytes per 32 samples,
    /// EaXas). Decoding is block-wise and unclamped until the 16-bit store, exactly as DecodeBlock/GetSamples.
    /// </summary>
    public sealed class GinsuSynthData
    {
        public float MinFrequency { get; private set; }
        public float MaxFrequency { get; private set; }
        public int SampleRate { get; private set; }
        public int SampleCount { get; private set; }
        int _segCount, _cycleCount;
        int[] _freqPos = Array.Empty<int>(), _cyclePos = Array.Empty<int>();
        byte[] _data;
        int _sampleData;
        float _minPeriod;
        readonly float[] _block = new float[32];
        int _curBlock = -1;

        static readonly float[] F0 = { 0f, 0.9375f, 1.796875f, 1.53125f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        static readonly float[] F1 = { 0f, 0f, -0.8125f, -0.859375f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        public static GinsuSynthData Load(string path) => Bind(File.ReadAllBytes(path));

        /// <summary>BindToData; null if the file is not a version-2 Gnsu recording.</summary>
        public static GinsuSynthData Bind(byte[] d)
        {
            if (d.Length < 0x20 || d[0] != 'G' || d[1] != 'n' || d[2] != 's' || d[3] != 'u' || d[4] != '2') return null;
            var g = new GinsuSynthData
            {
                MinFrequency = Chunks.F32(d, 8),
                MaxFrequency = Chunks.F32(d, 12),
                _segCount = Chunks.I32(d, 0x10),
                _cycleCount = Chunks.I32(d, 0x14),
                SampleCount = Chunks.I32(d, 0x18),
                SampleRate = Chunks.I32(d, 0x1C),
                _data = d,
            };
            if (g._segCount < 0 || g._cycleCount < 0 || 0x20 + 4L * (g._segCount + g._cycleCount + 2) > d.Length) return null;
            g._freqPos = new int[g._segCount + 1];
            for (int i = 0; i <= g._segCount; i++) g._freqPos[i] = Chunks.I32(d, 0x20 + i * 4);
            int cyc = 0x20 + (g._segCount + 1) * 4;
            g._cyclePos = new int[g._cycleCount + 1];
            for (int i = 0; i <= g._cycleCount; i++) g._cyclePos[i] = Chunks.I32(d, cyc + i * 4);
            g._sampleData = cyc + (g._cycleCount + 1) * 4;
            int minPeriod = g.SampleCount;
            for (int i = 0; i < g._cycleCount; i++) minPeriod = Math.Min(minPeriod, g._cyclePos[i + 1] - g._cyclePos[i]);
            g._minPeriod = Math.Max(1, minPeriod);
            // the data must hold every block GetSamples can ask for
            int blocks = (g.SampleCount + 31) / 32;
            if (g._sampleData + (long)blocks * 0x13 > d.Length) g.SampleCount = (int)Math.Max(0, (d.Length - g._sampleData) / 0x13 * 32);
            return g;
        }

        void DecodeBlock(int block)
        {
            int s = _sampleData + block * 0x13;
            _curBlock = block;
            var d = _data;
            _block[0] = (d[s] & 0xF0) + (sbyte)d[s + 1] * 0x100;
            _block[1] = (d[s + 2] & 0xF0) + (sbyte)d[s + 3] * 0x100;
            int filt = d[s] & 0xF, shift = d[s + 2] & 0xF;
            float f0 = F0[filt], f1 = F1[filt];
            int o = 2;
            for (int i = 0; i < 15; i++)
            {
                byte b = d[s + 4 + i];
                _block[o] = Nib(shift, b >> 4) + f0 * _block[o - 1] + f1 * _block[o - 2];
                _block[o + 1] = Nib(shift, b & 0xF) + f0 * _block[o] + f1 * _block[o - 1];
                o += 2;
            }
        }

        /// <summary>xatablef[shift][nibble]: signed nibble x 4096 >> shift, 0 for shifts 13..15.</summary>
        static float Nib(int shift, int n) => shift >= 13 ? 0f : (((n ^ 8) - 8) * 4096) >> shift;

        static short ToShort(float x)
        {
            int v = (int)x;
            return v >= 0x8000 ? (short)0x7FFF : v < -0x8000 ? (short)-0x8000 : (short)v;
        }

        /// <summary>GetSamples: fails (writes nothing) if the range runs outside the recording.</summary>
        public bool GetSamples(int start, int count, short[] dest, int destAt)
        {
            if (start < 0 || start + count - 1 >= SampleCount) return false;
            int block = start >> 5;
            if (block != _curBlock) DecodeBlock(block);
            int idx = start & 0x1F;
            for (int i = 0; i < count; i++)
            {
                dest[destAt + i] = ToShort(_block[idx]);
                if (++idx == 0x20)
                {
                    DecodeBlock(_curBlock + 1);
                    idx = 0;
                }
            }
            return true;
        }

        public int FrequencyToSample(float freq)
        {
            if (_segCount < 1) return 0;
            if (freq <= MinFrequency) return _freqPos[0];
            if (freq >= MaxFrequency) return _freqPos[_segCount];
            float seg = _segCount * (freq - MinFrequency) / (MaxFrequency - MinFrequency);
            int i = Ginsu.IntFloor(seg);
            float a = seg - i;
            return Ginsu.IntRound(_freqPos[i] + a * (_freqPos[i + 1] - _freqPos[i]));
        }

        public int CycleToSample(float cycle)
        {
            if (_cycleCount < 1) return 0;
            if (cycle <= 0) return _cyclePos[0];
            if (cycle >= _cycleCount) return _cyclePos[_cycleCount];
            int i = Ginsu.IntFloor(cycle);
            float a = cycle - i;
            return Ginsu.IntRound(_cyclePos[i] + a * (_cyclePos[i + 1] - _cyclePos[i]));
        }

        public float CyclePeriod(float cycle)
        {
            if (_cycleCount < 1) return 0;
            if (_cycleCount < 3) return Math.Max(1, _cyclePos[_cycleCount] - _cyclePos[0]) / (float)_cycleCount;
            float startP, endP;
            int i = Ginsu.IntFloor(cycle);
            if (i < 1)
            {
                if (i < 0) { cycle = 0; i = 0; }
                startP = _cyclePos[1] - _cyclePos[0];
                endP = (_cyclePos[2] - _cyclePos[0]) * 0.5f;
            }
            else if (i >= _cycleCount - 1)
            {
                if (i >= _cycleCount) { i = _cycleCount - 1; cycle = _cycleCount; }
                startP = (_cyclePos[_cycleCount] - _cyclePos[_cycleCount - 2]) * 0.5f;
                endP = _cyclePos[_cycleCount] - _cyclePos[_cycleCount - 1];
            }
            else
            {
                startP = (_cyclePos[i + 1] - _cyclePos[i - 1]) * 0.5f;
                endP = (_cyclePos[i + 2] - _cyclePos[i]) * 0.5f;
            }
            return startP + (cycle - i) * (endP - startP);
        }

        public float SampleToCycle(int sample)
        {
            if (_cycleCount <= 0) return 0;
            if (sample <= _cyclePos[0]) return 0;
            if (sample >= _cyclePos[_cycleCount]) return _cycleCount;
            int low = 0, high = _cycleCount, guess;
            for (int guard = 0; ; guard++)
            {
                for (; ; guard++)
                {
                    guess = low + Ginsu.IntFloor((float)(sample - _cyclePos[low]) / (_cyclePos[high] - _cyclePos[low]) * (high - low));
                    guess = Math.Max(low, Math.Min(_cycleCount - 1, guess));
                    if (sample >= _cyclePos[guess] || guard > 64) break;
                    int newLow = guess - Ginsu.IntCeil((_cyclePos[guess] - sample) / _minPeriod);
                    high = guess;
                    if (newLow > low) low = newLow;
                }
                if (sample < _cyclePos[guess + 1] || guard > 64) break;
                int newHigh = guess + Ginsu.IntCeil((sample - _cyclePos[guess]) / _minPeriod) + 1;
                low = guess + 1;
                if (newHigh < high) high = newHigh;
            }
            float s1 = _cyclePos[guess], s2 = _cyclePos[guess + 1];
            return (sample - s1) / Math.Max(1f, s2 - s1) + guess;
        }
    }

    /// <summary>
    /// GinsuSynthesis (EAXSound/Ginsu/ginsusynth.cpp; speed.exe CARSFX_*GinsuEng): plays the recording at its own
    /// sample rate in 11 ms packets, jumping whole firing cycles forward/back so the position follows the target rpm,
    /// with a 0.5 ms crossfade at each jump and at most one jump per 11 ms ("no jump" window). UpdateFrequency sets the
    /// target position for a frequency and spreads the move over latency/11 ms packets. The sound driver's packet
    /// player is replaced by <see cref="Read"/>, which pulls packets as they are consumed (two silent packets first,
    /// as StartSynthesis queues).
    /// </summary>
    public sealed class GinsuSynthesis
    {
        const float NoJumpDuration = 0.011f, PacketDuration = 0.011f, OverlapDuration = 0.0005f;
        readonly object _lock = new object();
        GinsuSynthData _data;
        int _noJumpSize, _packetSize, _overlapSize;
        int _playbackPos, _currentPos, _targetPos, _packetCountdown, _noJumpRemaining;
        float _currentCycle;
        short[] _packet = Array.Empty<short>();
        readonly short[] _buff = new short[256];
        int _packetLen, _packetAt, _silentPackets;
        public bool Running { get; private set; }
        public int SampleRate => _data?.SampleRate ?? 0;

        public bool SetSynthData(GinsuSynthData data)
        {
            if (data == null || data.SampleRate == 0) return false;
            lock (_lock)
            {
                _data = data;
                _noJumpSize = Ginsu.IntRound(data.SampleRate * NoJumpDuration);
                _packetSize = Ginsu.IntRound(data.SampleRate * PacketDuration);
                _overlapSize = Math.Min(256, Ginsu.IntRound(data.SampleRate * OverlapDuration));
                _packet = new short[_packetSize + _overlapSize + 2];
            }
            return true;
        }

        public void StartSynthesis(float startFreq)
        {
            lock (_lock)
            {
                if (_data == null) return;
                _playbackPos = _data.FrequencyToSample(startFreq);
                _packetCountdown = 1;
                _noJumpRemaining = _noJumpSize;
                _currentPos = _targetPos = _playbackPos;
                _currentCycle = _data.SampleToCycle(_playbackPos);
                _silentPackets = 2;
                _packetLen = _packetAt = 0;
                Running = true;
            }
        }

        public void StopSynthesis() { lock (_lock) Running = false; }

        /// <summary>UpdateFrequency(targetFreq, latency ms).</summary>
        public void UpdateFrequency(float targetFreq, float latencyMs)
        {
            if (_data == null) return;
            int sample = _data.FrequencyToSample(targetFreq);
            lock (_lock)
            {
                _targetPos = sample;
                _packetCountdown = Ginsu.IntFloor(latencyMs * 0.09090909f) + 1;
            }
        }

        /// <summary>GetCurrentPitch: firing frequency (Hz) at the current position.</summary>
        public float CurrentPitch
        {
            get
            {
                var d = _data;
                if (d == null || !Running) return 0;
                float p = d.CyclePeriod(_currentCycle + 0.5f);
                return p > 0 ? d.SampleRate / p : 0;
            }
        }

        /// <summary>Fills dest[0..count) with samples at the recording's rate (-1..1).</summary>
        public void Read(float[] dest, int count)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_packetAt >= _packetLen)
                    {
                        if (!Running || _data == null) { Array.Clear(dest, i, count - i); return; }
                        if (_silentPackets > 0)
                        {
                            _silentPackets--;
                            Array.Clear(_packet, 0, _packetSize);
                            _packetLen = _packetSize;
                        }
                        else _packetLen = HandlePacketRelease();
                        _packetAt = 0;
                        if (_packetLen <= 0) { _packetLen = 0; dest[i] = 0; continue; }
                    }
                    dest[i] = _packet[_packetAt++] / 32768f;
                }
            }
        }

        /// <summary>HandlePacketRelease: builds the next packet, returns its length.</summary>
        int HandlePacketRelease()
        {
            var d = _data;
            var samples = _packet;
            _currentCycle = d.SampleToCycle(_currentPos);
            float change = (float)(_targetPos - _currentPos) / _packetCountdown;
            float jumpTime = 0;
            int jumpDist = 0;
            if (_noJumpRemaining < _packetSize)
            {
                float playbackCycle = d.SampleToCycle(_playbackPos);
                float playbackRate = _packetSize / Math.Max(1f, d.CyclePeriod(playbackCycle));
                float currentRate = change / Math.Max(1f, d.CyclePeriod(_currentCycle));
                float posDiff = _currentCycle - playbackCycle;
                float rateDiff = currentRate - playbackRate;
                float nojumpTime = (float)_noJumpRemaining / _packetSize;
                float nojumpDist = posDiff + rateDiff * nojumpTime;
                float packetDist = posDiff + rateDiff;
                if (Ginsu.IntFloor(nojumpDist) != Ginsu.IntFloor(packetDist))
                {
                    jumpDist = nojumpDist < packetDist ? Ginsu.IntCeil(nojumpDist) : Ginsu.IntFloor(nojumpDist);
                    jumpTime = rateDiff != 0 ? (jumpDist - posDiff) / rateDiff : 1f;
                }
                else
                {
                    float maxDist = Ginsu.IntCeil(Math.Abs(rateDiff * 0.99999994f));
                    if (Math.Abs(nojumpDist) > maxDist)
                    {
                        jumpTime = nojumpTime;
                        jumpDist = nojumpDist < 0 ? Ginsu.IntCeil(nojumpDist) : Ginsu.IntFloor(nojumpDist);
                    }
                }
            }
            if (jumpDist == 0) jumpTime = 1f;
            jumpTime = Math.Max(0f, Math.Min(1f, jumpTime));
            int sampleCount = Ginsu.IntRound(jumpTime * _packetSize);
            d.GetSamples(_playbackPos, sampleCount, samples, 0);
            _playbackPos += sampleCount;
            _noJumpRemaining -= sampleCount;
            if (_noJumpRemaining < 0) _noJumpRemaining = 0;
            if (jumpDist != 0)
            {
                int dest = sampleCount;
                d.GetSamples(_playbackPos, _overlapSize, samples, dest);
                float cycle = d.SampleToCycle(_playbackPos);
                _playbackPos = d.CycleToSample(cycle + jumpDist);
                d.GetSamples(_playbackPos, _overlapSize, _buff, 0);
                float blend = 0, step = 1f / _overlapSize;
                for (int i = 0; i < _overlapSize; i++)
                {
                    samples[dest + i] = (short)Ginsu.IntRound(samples[dest + i] + blend * (_buff[i] - samples[dest + i]));
                    blend += step;
                }
                sampleCount += _overlapSize;
                _playbackPos += _overlapSize;
                int count = _packetSize - sampleCount;
                _noJumpRemaining = _noJumpSize - _overlapSize;
                if (count > 0)
                {
                    d.GetSamples(_playbackPos, count, samples, sampleCount);
                    sampleCount = _packetSize;
                    _playbackPos += count;
                    _noJumpRemaining -= count;
                }
            }
            // keep the read head inside the recording (the original relies on the targets staying in range)
            _playbackPos = Math.Max(0, Math.Min(d.SampleCount - _packetSize - _overlapSize - 1, _playbackPos));
            _currentPos = Ginsu.IntRound(_currentPos + change);
            if (_packetCountdown > 1) _packetCountdown--;
            return sampleCount;
        }
    }

    /// <summary>ginsuhelper.h rounding.</summary>
    public static class Ginsu
    {
        public static int IntRound(float x) => x > 0 ? (int)(x + 0.5f) : (int)(x - 0.5f);

        public static int IntCeil(float x)
        {
            if (x > 0)
            {
                int i = (int)x;
                return i == x ? i : i + 1;
            }
            return (int)x;
        }

        public static int IntFloor(float x)
        {
            if (x >= 0) return (int)x;
            int i = (int)x;
            return i == x ? i : i - 1;
        }
    }
}
