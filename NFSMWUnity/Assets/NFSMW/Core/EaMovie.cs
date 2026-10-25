using System;
using System.IO;
using System.Threading;

namespace NfsMw.Core
{
    /// <summary>
    /// EA movie container (MOVIES\*.vp6): a flat run of chunks {char[4] tag, u32 size including this 8-byte header}.
    /// 'MVhd' {char[4] codec "VP60" (stored "06PV"), u16 width, u16 height, u32 frame count, u32 largest chunk,
    /// u32 rate, u32 scale} (fps = rate / scale = 982047 / 32767 = 29.97), then 'SCHl' (EA audio header, "GSTR" variant),
    /// then interleaved 'MV0K' (VP6 key frame) / 'MV0F' (VP6 inter frame) and 'SCCl' {u32 BE block count} / 'SCDl'
    /// (audio block) chunks, ended by 'SCEl'.
    /// </summary>
    public sealed class EaMovieInfo
    {
        public string Codec;
        public int Width, Height, FrameCount, LargestChunk, Rate, Scale;
        public double Fps => Scale > 0 ? Rate / (double)Scale : 29.97;
        public double Duration => FrameCount / Fps;
        /// <summary>Audio: sample rate, channels, total samples per channel, EA header version; Channels 0 = no audio.</summary>
        public int AudioRate, AudioChannels, AudioSamples, AudioVersion, AudioCodec = -1;
        /// <summary>True when the SCHl header named no sample rate and the default was assumed.</summary>
        public bool AudioRateDefaulted;
        public bool AudioBigEndianBlocks;
        public long DataStart;
    }

    public static class EaMovieTags
    {
        public const uint MVhd = 'M' | 'V' << 8 | 'h' << 16 | (uint)'d' << 24;
        public const uint MV0K = 'M' | 'V' << 8 | '0' << 16 | (uint)'K' << 24;
        public const uint MV0F = 'M' | 'V' << 8 | '0' << 16 | (uint)'F' << 24;
        public const uint SCHl = 'S' | 'C' << 8 | 'H' << 16 | (uint)'l' << 24;
        public const uint SCCl = 'S' | 'C' << 8 | 'C' << 16 | (uint)'l' << 24;
        public const uint SCDl = 'S' | 'C' << 8 | 'D' << 16 | (uint)'l' << 24;
        public const uint SCEl = 'S' | 'C' << 8 | 'E' << 16 | (uint)'l' << 24;

        public static string Name(uint tag) => new string(new[] { (char)(tag & 0xFF), (char)(tag >> 8 & 0xFF), (char)(tag >> 16 & 0xFF), (char)(tag >> 24) });
    }

    /// <summary>Sequential chunk reader over an EA movie file.</summary>
    public sealed class EaMovieFile : IDisposable
    {
        readonly Stream _s;
        readonly byte[] _hdr = new byte[8];
        public readonly EaMovieInfo Info = new EaMovieInfo();

        /// <summary>
        /// Default rate for a GSTR audio header without a 0x84 (rate) field [?]: 48000 Hz. Evidence: in all 29 MW movies
        /// the SCHl sample count / 48000 equals the video length (e.g. blacklist_01: 1492080 samples, 932 frames at 29.97).
        /// </summary>
        public const int GstrDefaultRate = 48000;

        public EaMovieFile(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)) { }

        public EaMovieFile(Stream s)
        {
            _s = s;
            var buf = new byte[256];
            if (!Next(ref buf, out uint tag, out int size) || tag != EaMovieTags.MVhd || size < 24) throw new InvalidDataException("not an EA movie (no MVhd)");
            Info.Codec = new string(new[] { (char)buf[3], (char)buf[2], (char)buf[1], (char)buf[0] });
            Info.Width = buf[4] | buf[5] << 8;
            Info.Height = buf[6] | buf[7] << 8;
            Info.FrameCount = I32(buf, 8);
            Info.LargestChunk = I32(buf, 12);
            Info.Rate = I32(buf, 16);
            Info.Scale = I32(buf, 20);
            long at = _s.Position;
            if (Next(ref buf, out tag, out size) && tag == EaMovieTags.SCHl) ParseAudioHeader(buf, size);
            else _s.Position = at;
            Info.DataStart = _s.Position;
        }

        /// <summary>
        /// SCHl payload: "GSTR" u32 version, then tag/length/big-endian-value fields like EaStream (0x80 version,
        /// 0x82 channels, 0x84 rate, 0x85 samples, 0xA0 codec2, 0xFD/0xFE open sub-headers, 0xFF ends). MW movies carry
        /// 00 (u32 id), 06 (priority 0x65), FD, 80 = 3, 85 = samples, 82 = 2, FF: stereo EA-XA v2 with no rate field.
        /// </summary>
        void ParseAudioHeader(byte[] d, int size)
        {
            int p = 0;
            bool gstr = size >= 8 && d[0] == 'G' && d[1] == 'S' && d[2] == 'T' && d[3] == 'R';
            if (gstr) p = 8;
            else if (size >= 4 && d[0] == 'P' && d[1] == 'T') p = 4;
            Info.AudioBigEndianBlocks = gstr;
            Info.AudioChannels = 1;
            int rate = 0;
            while (p < size)
            {
                int t = d[p++];
                if (t == 0xFF) break;
                if (t == 0xFD || t == 0xFE || t == 0xFC) continue;
                if (p >= size) break;
                int n = d[p++], v = 0;
                for (int i = 0; i < n && p + i < size; i++) v = v << 8 | d[p + i];
                p += n;
                switch (t)
                {
                    case 0x80: Info.AudioVersion = v; break;
                    case 0x82: Info.AudioChannels = v; break;
                    case 0x84: rate = v; break;
                    case 0x85: Info.AudioSamples = v; break;
                    case 0xA0: Info.AudioCodec = v; break;
                }
            }
            Info.AudioRateDefaulted = rate == 0;
            Info.AudioRate = rate != 0 ? rate : gstr ? GstrDefaultRate : 22050;
        }

        /// <summary>Reads the next chunk's payload into buf (grown as needed). False at the end of the file.</summary>
        public bool Next(ref byte[] buf, out uint tag, out int size)
        {
            tag = 0;
            size = 0;
            if (ReadFully(_hdr, 0, 8) < 8) return false;
            tag = (uint)I32(_hdr, 0);
            int total = I32(_hdr, 4);
            if (total < 8) return false;
            size = total - 8;
            if (buf == null || buf.Length < size + 16) buf = new byte[size + 4096];
            if (ReadFully(buf, 0, size) < size) return false;
            return true;
        }

        public void Rewind() => _s.Position = Info.DataStart;

        int ReadFully(byte[] b, int o, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = _s.Read(b, o + got, n - got);
                if (r <= 0) break;
                got += r;
            }
            return got;
        }

        static int I32(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;

        public void Dispose() => _s.Dispose();
    }

    /// <summary>
    /// The movie soundtrack: SCDl blocks {u32 samples, u32 offset per channel from the end of that table, then each
    /// channel's EA-XA frames}; big-endian header words in GSTR movies, which makes it the "R3" revision of EA-XA
    /// (FFmpeg adpcm_ea_r3). Frames: byte {coef index hi nibble, shift lo nibble} + 14 bytes of nibbles = 28 samples,
    /// s = clip16(((nibble << (20 - shift)) + cur * c1 + prev * c2) >> 8), c1 = {0, 240, 460, 392}, c2 = {0, 0, -208, -220};
    /// a 0xEE frame holds two BE history words then 28 BE PCM samples. History carries from block to block. Integer
    /// arithmetic as the reference (Core/EaXa is the float variant used for the sound banks). Output: interleaved stereo.
    /// </summary>
    public sealed class EaMovieAudio
    {
        static readonly int[] C1 = { 0, 240, 460, 392, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        static readonly int[] C2 = { 0, 0, -208, -220, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        readonly EaMovieInfo _info;
        readonly int[] _cur, _prev;
        short[][] _ch = Array.Empty<short[]>();

        public EaMovieAudio(EaMovieInfo info)
        {
            _info = info;
            int n = Math.Max(1, info.AudioChannels);
            _cur = new int[n];
            _prev = new int[n];
        }

        public void Reset()
        {
            Array.Clear(_cur, 0, _cur.Length);
            Array.Clear(_prev, 0, _prev.Length);
        }

        int U32(byte[] d, int o) => _info.AudioBigEndianBlocks ? d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3] : d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;

        /// <summary>Decodes one SCDl payload into stereo[0 .. 2*samples); returns the sample-frame count.</summary>
        public int DecodeBlock(byte[] d, int size, ref float[] stereo)
        {
            if (size < 4) return 0;
            int chans = Math.Max(1, _info.AudioChannels);
            int n = U32(d, 0);
            if (n <= 0 || n > 1 << 20 || 4 + 4 * chans > size) return 0;
            if (_ch.Length < chans || _ch[0].Length < n)
            {
                _ch = new short[chans][];
                for (int c = 0; c < chans; c++) _ch[c] = new short[n];
            }
            int body = 4 + 4 * chans;
            for (int c = 0; c < chans; c++)
            {
                Array.Clear(_ch[c], 0, n);
                int start = body + U32(d, 4 + 4 * c);
                if (start < body || start >= size) continue;
                DecodeChannel(d, start, size, _ch[c], n, ref _cur[c], ref _prev[c]);
            }
            if (stereo == null || stereo.Length < n * 2) stereo = new float[n * 2];
            var l = _ch[0];
            var r = chans > 1 ? _ch[1] : _ch[0];
            for (int i = 0; i < n; i++)
            {
                stereo[i * 2] = l[i] / 32768f;
                stereo[i * 2 + 1] = r[i] / 32768f;
            }
            return n;
        }

        static void DecodeChannel(byte[] d, int p, int end, short[] o, int n, ref int cur, ref int prev)
        {
            int k = 0;
            for (int f = 0; f < n / 28 && p < end; f++)
            {
                int b = d[p++];
                if (b == 0xEE)
                {
                    if (p + 4 + 56 > end) break;
                    cur = (short)(d[p] << 8 | d[p + 1]);
                    prev = (short)(d[p + 2] << 8 | d[p + 3]);
                    p += 4;
                    for (int i = 0; i < 28; i++, p += 2) o[k++] = (short)(d[p] << 8 | d[p + 1]);
                    continue;
                }
                int c1 = C1[b >> 4], c2 = C2[b >> 4], shift = 20 - (b & 15);
                for (int i = 0; i < 28; i++)
                {
                    int nib;
                    if ((i & 1) != 0) nib = b & 15;
                    else
                    {
                        b = p < end ? d[p] : 0;
                        p++;
                        nib = b >> 4;
                    }
                    int next = ((nib ^ 8) - 8) << shift;
                    next += cur * c1 + prev * c2;
                    next >>= 8;
                    next = next < -32768 ? -32768 : next > 32767 ? 32767 : next;
                    prev = cur;
                    cur = next;
                    o[k++] = (short)next;
                }
            }
        }
    }

    /// <summary>
    /// Background decoding of an EA movie: one worker thread reads the chunks in file order, decodes the VP6 frames into a
    /// small pool of pictures and the audio into a ring buffer. The consumer asks for the picture due at a time and pulls
    /// audio as it plays. Unity-free (used by the game's MoviePlayer and the harness).
    /// </summary>
    public sealed class EaMovieStream : IDisposable
    {
        public sealed class Frame
        {
            public int Index;
            public double Time;
            public Vp6Picture Picture;
        }

        readonly string _path;
        readonly object _lock = new object();
        readonly Frame[] _frames;
        readonly System.Collections.Generic.Queue<Frame> _ready = new System.Collections.Generic.Queue<Frame>();
        readonly System.Collections.Generic.Stack<Frame> _free = new System.Collections.Generic.Stack<Frame>();
        readonly float[] _ring;
        int _ringRead, _ringCount;
        Thread _thread;
        volatile bool _stop;

        public EaMovieInfo Info { get; }
        public bool VideoDone { get; private set; }
        public bool AudioDone { get; private set; }
        public string Error { get; private set; }
        public int DecodedFrames { get; private set; }
        public double DecodeSeconds { get; private set; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Opens the movie and reads its headers (throws on a non-movie file); call Start to begin decoding.</summary>
        public EaMovieStream(string path, int poolSize = 8, double audioSeconds = 4)
        {
            _path = path;
            using (var f = new EaMovieFile(path)) Info = f.Info;
            Width = (Info.Width + 15) & ~15;
            Height = (Info.Height + 15) & ~15;
            _frames = new Frame[poolSize];
            for (int i = 0; i < poolSize; i++)
            {
                _frames[i] = new Frame { Picture = new Vp6Picture(Width, Height) };
                _free.Push(_frames[i]);
            }
            int rate = Math.Max(8000, Info.AudioRate);
            _ring = new float[(int)(rate * audioSeconds) * 2];
            if (Info.AudioChannels <= 0 || Info.AudioSamples <= 0) AudioDone = true;
        }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "EA movie decode", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        void Run()
        {
            var vp6 = new Vp6Decoder();
            byte[] buf = new byte[Math.Max(65536, Info.LargestChunk + 16)];
            float[] pcm = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var file = new EaMovieFile(_path);
                var audio = new EaMovieAudio(Info);
                int index = 0;
                while (!_stop)
                {
                    if (!file.Next(ref buf, out uint tag, out int size)) break;
                    if (tag == EaMovieTags.MV0K || tag == EaMovieTags.MV0F)
                    {
                        long t0 = watch.ElapsedTicks;
                        bool ok;
                        try { ok = vp6.Decode(buf, 0, size); }
                        catch (NotSupportedException e) { Error = e.Message; break; }
                        if (!ok)
                        {
                            // keep the timeline: a damaged frame repeats the last good picture
                            if (vp6.Picture == null) { index++; continue; }
                        }
                        DecodeSeconds += (watch.ElapsedTicks - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
                        Frame fr;
                        lock (_lock)
                        {
                            while (_free.Count == 0 && !_stop) Monitor.Wait(_lock, 50);
                            if (_stop) break;
                            fr = _free.Pop();
                        }
                        if (vp6.Width == Width && vp6.Height == Height) vp6.Picture.CopyTo(fr.Picture);
                        fr.Index = index;
                        fr.Time = index / Info.Fps;
                        index++;
                        lock (_lock)
                        {
                            _ready.Enqueue(fr);
                            DecodedFrames = index;
                        }
                    }
                    else if (tag == EaMovieTags.SCDl && Info.AudioChannels > 0)
                    {
                        int n = audio.DecodeBlock(buf, size, ref pcm);
                        WriteAudio(pcm, n * 2);
                    }
                }
            }
            catch (Exception e) { Error = e.GetType().Name + ": " + e.Message; }
            lock (_lock)
            {
                VideoDone = true;
                _writerDone = true;
                Monitor.PulseAll(_lock);
            }
        }

        bool _writerDone;

        void WriteAudio(float[] pcm, int count)
        {
            int o = 0;
            while (o < count && !_stop)
            {
                lock (_lock)
                {
                    while (_ringCount == _ring.Length && !_stop) Monitor.Wait(_lock, 50);
                    int space = _ring.Length - _ringCount;
                    int n = Math.Min(space, count - o);
                    int w = (_ringRead + _ringCount) % _ring.Length;
                    for (int i = 0; i < n; i++)
                    {
                        _ring[w] = pcm[o + i];
                        if (++w == _ring.Length) w = 0;
                    }
                    _ringCount += n;
                    o += n;
                }
            }
        }

        /// <summary>Interleaved stereo samples available right now.</summary>
        public int AudioBuffered { get { lock (_lock) return _ringCount; } }

        /// <summary>True when the decoder has finished and every audio sample has been read.</summary>
        public bool AudioDrained { get { lock (_lock) return _writerDone && _ringCount == 0; } }

        /// <summary>Copies up to count interleaved stereo samples (count even); returns how many were copied.</summary>
        public int ReadAudio(float[] dst, int offset, int count)
        {
            lock (_lock)
            {
                int n = Math.Min(count, _ringCount) & ~1;
                for (int i = 0; i < n; i++)
                {
                    dst[offset + i] = _ring[_ringRead];
                    if (++_ringRead == _ring.Length) _ringRead = 0;
                }
                _ringCount -= n;
                if (n > 0) Monitor.PulseAll(_lock);
                return n;
            }
        }

        /// <summary>Drops up to count samples (keeps audio aligned after an underrun was padded with silence).</summary>
        public int SkipAudio(int count)
        {
            lock (_lock)
            {
                int n = Math.Min(count, _ringCount) & ~1;
                _ringRead = (_ringRead + n) % _ring.Length;
                _ringCount -= n;
                if (n > 0) Monitor.PulseAll(_lock);
                return n;
            }
        }

        /// <summary>
        /// The newest decoded frame due at <paramref name="time"/> (seconds), or null when the next frame is not due yet
        /// (or not decoded). Frames passed over are recycled. Give the returned frame back with Release.
        /// </summary>
        public Frame Take(double time)
        {
            lock (_lock)
            {
                Frame best = null;
                while (_ready.Count > 0 && _ready.Peek().Time <= time)
                {
                    if (best != null) _free.Push(best);
                    best = _ready.Dequeue();
                }
                if (best != null) Monitor.PulseAll(_lock);
                return best;
            }
        }

        public void Release(Frame f)
        {
            if (f == null) return;
            lock (_lock)
            {
                _free.Push(f);
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>True once the decoder has ended and no decoded frame is waiting.</summary>
        public bool VideoDrained { get { lock (_lock) return VideoDone && _ready.Count == 0; } }

        public void Dispose()
        {
            _stop = true;
            lock (_lock) Monitor.PulseAll(_lock);
            _thread?.Join(1000);
        }
    }
}
