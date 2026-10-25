using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NfsMw.Core;

// movie probe: parse every MOVIES\*.vp6, decode frames, time the VP6 decoder, write PNG snapshots / raw YUV.
// usage: Harness <root> movie [outDir] [maxFrames] [filter]          headers + full decode timing + PNGs
//        Harness <root> movie yuv <file.vp6> <frames> <out.yuv>       raw yuv420p (display order) for comparisons
//        Harness <root> movie cmp <file.vp6> <ref.yuv> <frames>        per-frame diff against a reference yuv420p
//        Harness <root> movie stream <file.vp6> [seconds]              threaded EaMovieStream at 1x speed (real-time check)
//        Harness <root> movie audio <file.vp6> <out.wav>               decoded soundtrack as 16-bit wav
//        Harness <root> movie md5 <file.vp6>                           per-frame md5 (compare with ffmpeg -f framemd5)
static class MovieProbe
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "";
        if (sub == "yuv") { Yuv(args[3], int.Parse(args[4]), args[5]); return; }
        if (sub == "cmp") { Cmp(args[3], args[4], int.Parse(args[5])); return; }
        if (sub == "stream") { Stream(args[3], args.Length > 4 ? double.Parse(args[4]) : 10); return; }
        if (sub == "audio") { Audio(args[3], args[4]); return; }
        if (sub == "md5") { Md5(args[3]); return; }
        if (sub == "fe") { Fe(root); return; }
        string outDir = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "nfsmw_movies");
        int maxFrames = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
        string filter = args.Length > 4 ? args[4] : "";
        Directory.CreateDirectory(outDir);
        var dir = Path.Combine(root, "MOVIES");
        double totalSec = 0;
        long totalFrames = 0;
        foreach (var path in Directory.GetFiles(dir, "*.vp6").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (filter.Length > 0 && !name.Contains(filter)) continue;
            using var f = new EaMovieFile(path);
            var info = f.Info;
            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            byte[] buf = null;
            var vp6 = new Vp6Decoder();
            var audio = new EaMovieAudio(info);
            float[] pcm = null;
            long audioSamples = 0;
            int frames = 0, bad = 0, keys = 0;
            var watch = new Stopwatch();
            double worst = 0;
            var snaps = new HashSet<int> { 0, 1, 30, 100, 300, info.FrameCount / 2, info.FrameCount - 1 };
            string err = null;
            while (f.Next(ref buf, out uint tag, out int size))
            {
                string t = EaMovieTags.Name(tag);
                counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
                if (order.Count < 14) order.Add(t);
                if (tag == EaMovieTags.MV0K || tag == EaMovieTags.MV0F)
                {
                    if (frames >= maxFrames) continue;
                    if (tag == EaMovieTags.MV0K) keys++;
                    long t0 = watch.ElapsedTicks;
                    watch.Start();
                    bool ok;
                    try { ok = vp6.Decode(buf, 0, size); }
                    catch (Exception e) { err = e.Message; ok = false; }
                    watch.Stop();
                    double ms = (watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
                    if (frames > 0) worst = Math.Max(worst, ms);
                    if (!ok) bad++;
                    if (ok && snaps.Contains(frames)) Png(Path.Combine(outDir, $"{name}_{frames:D5}.png"), vp6.Picture);
                    frames++;
                    if (err != null) break;
                }
                else if (tag == EaMovieTags.SCDl) audioSamples += audio.DecodeBlock(buf, size, ref pcm);
            }
            double sec = watch.ElapsedTicks / (double)Stopwatch.Frequency;
            totalSec += sec;
            totalFrames += frames;
            Console.WriteLine($"{name}: {info.Codec} {info.Width}x{info.Height} {info.FrameCount} frames @ {info.Fps:0.000} ({info.Duration:0.0}s), largest chunk {info.LargestChunk}; " +
                              $"audio {info.AudioChannels}ch {info.AudioRate}Hz{(info.AudioRateDefaulted ? " (default)" : "")} v{info.AudioVersion} codec2 {info.AudioCodec} samples {info.AudioSamples} ({info.AudioSamples / (double)Math.Max(1, info.AudioRate):0.00}s), decoded {audioSamples}");
            Console.WriteLine($"   chunks {string.Join(" ", counts.Select(kv => kv.Key + "=" + kv.Value))}; order {string.Join(",", order)}");
            Console.WriteLine($"   vp6 sub {vp6.SubVersion} coded {vp6.Width}x{vp6.Height} display {vp6.DisplayWidth}x{vp6.DisplayHeight}; decoded {frames} ({keys} key, {bad} bad{(err != null ? ", error " + err : "")}, approx {vp6.ApproximatedFrames}) " +
                              $"in {sec:0.00}s = {(frames > 0 ? sec * 1000 / frames : 0):0.00} ms/frame (worst {worst:0.0} ms), {frames / Math.Max(1e-9, sec):0} fps vs {info.Fps:0.00} needed");
        }
        Console.WriteLine($"total: {totalFrames} frames in {totalSec:0.0}s = {totalSec * 1000 / Math.Max(1, totalFrames):0.00} ms/frame; PNGs in {outDir}");
    }

    static Vp6Decoder _dbg;
    static IEnumerable<Vp6Picture> Frames(string file, int n)
    {
        using var f = new EaMovieFile(file);
        var vp6 = _dbg = new Vp6Decoder();
        byte[] buf = null;
        int k = 0;
        while (k < n && f.Next(ref buf, out uint tag, out int size))
        {
            if (tag != EaMovieTags.MV0K && tag != EaMovieTags.MV0F) continue;
            if (!vp6.Decode(buf, 0, size)) Console.WriteLine($"frame {k}: decode failed");
            if (Environment.GetEnvironmentVariable("VP6DBG") != null)
                Console.WriteLine($"  [{k}] {(vp6.KeyFrame ? "key" : "inter")} q{vp6.Quantizer} huff {vp6.UsesHuffman} golden {vp6.Golden} types {string.Join(",", vp6.TypeCounts)}");
            k++;
            yield return vp6.Picture;
        }
    }

    /// <summary>Writes yuv420p in display order (top row first: the coded picture is bottom-up).</summary>
    static void Yuv(string file, int n, string outPath)
    {
        using var o = File.Create(outPath);
        foreach (var p in Frames(file, n)) WriteFlipped(o, p);
    }

    static void WriteFlipped(Stream o, Vp6Picture p)
    {
        for (int y = p.Height - 1; y >= 0; y--) o.Write(p.Y, y * p.Width, p.Width);
        for (int y = p.ChromaHeight - 1; y >= 0; y--) o.Write(p.U, y * p.ChromaWidth, p.ChromaWidth);
        for (int y = p.ChromaHeight - 1; y >= 0; y--) o.Write(p.V, y * p.ChromaWidth, p.ChromaWidth);
    }

    /// <summary>The movie screens' FEng packages (object rectangles) and the tutorial subtitle strings.</summary>
    static void Fe(string root)
    {
        var all = new Dictionary<string, FEPackage>(StringComparer.OrdinalIgnoreCase);
        StringTable strings = null;
        foreach (var f in new[] { "FRONTEND/FRONTA.BUN", "FRONTEND/FrontB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameA.bun", "GLOBAL/InGameB.lzc", "LANGUAGES/English.bin" })
        {
            var path = Path.Combine(root, f);
            if (!File.Exists(path)) continue;
            var d = GameFiles.ReadBundle(path);
            foreach (var kv in FEPackage.ReadBundle(d)) all[kv.Key] = kv.Value;
            if (f.StartsWith("LANGUAGES")) strings = StringTable.Read(d);
        }
        var names = new Dictionary<uint, string>();
        var exe = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(root, "speed.exe")));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(exe, @"[A-Za-z0-9_ .%\-]{2,64}")) names.TryAdd(EaHash.HashUpper(m.Value), m.Value);
        foreach (var kv in all.Where(kv => kv.Key.IndexOf("movie", StringComparison.OrdinalIgnoreCase) >= 0 || kv.Key.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) >= 0 || kv.Key.IndexOf("FMV", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            Console.WriteLine($"== {kv.Key}: resources {string.Join(",", kv.Value.Resources)}");
            foreach (var o in kv.Value.Objects)
            {
                string n = names.TryGetValue(o.Hash, out var nn) ? nn : o.Hash.ToString("X8");
                string label = o.Label != 0 && strings != null && strings.TryGet(o.Label, out var ls) ? ls : "";
                if ((int)o.Type == 7 || n == "C6AFDD7E" || n == "Group" || n == "11643E")
                    foreach (var sc in o.Scripts)
                        foreach (var tr in sc.Tracks)
                            Console.WriteLine($"      script {(names.TryGetValue(sc.Id, out var sn2) ? sn2 : sc.Id.ToString("X8"))} track to {tr.Offset} keys {string.Join(" ", tr.Keys.Select(k => k.time + ":" + string.Join("/", k.value.Select(v => v.ToString("0.##")))))}");
                float ax = o.Data.X, ay = o.Data.Y;
                for (uint par = o.Parent; par != 0 && kv.Value.ByGuid.TryGetValue(par, out var po); par = po.Parent) { ax += po.Data.X; ay += po.Data.Y; }
                Console.WriteLine($"   {n} guid {o.Guid:X} abs ({ax:0.#},{ay:0.#}) justify {o.Justify} type {(int)o.Type} parent {o.Parent:X} flags {o.Flags:X8} pos ({o.Data.X:0.#},{o.Data.Y:0.#}) size ({o.Data.SizeX:0.#},{o.Data.SizeY:0.#}) rgba ({o.Data.R:0},{o.Data.G:0},{o.Data.B:0},{o.Data.A:0}) text '{o.Text}' label {o.Label:X} '{label}' scripts {string.Join(",", o.Scripts.Select(sc => (names.TryGetValue(sc.Id, out var sn) ? sn : sc.Id.ToString("X8")) + "/" + sc.Length + "ms"))}");
            }
        }
        if (strings == null) return;
        foreach (var file in Directory.GetFiles(Path.Combine(root, "SUBTITLES")))
        {
            var d = File.ReadAllBytes(file);
            var lines = new List<string>();
            for (int o = 0; o + 8 <= d.Length; o += 8)
            {
                int t = d[o] | d[o + 1] << 8;
                if (t == 0xFFFF) break;
                uint h = BitConverter.ToUInt32(d, o + 4);
                string a = strings.TryGet(h, out var s0) ? s0 : null;
                string ta = strings.TryGet(EaHash.Hash("_A", h), out var s1) ? s1 : null, tb = strings.TryGet(EaHash.Hash("_B", h), out var s2) ? s2 : null;
                lines.Add($"{t / 10.0:0.0}s {h:X8} '{a}' A '{ta}' B '{tb}'");
            }
            Console.WriteLine($"-- {Path.GetFileName(file)}: {lines.Count} entries");
            foreach (var l in lines.Take(4)) Console.WriteLine("     " + l);
        }
        foreach (var k in new[] { "TUTORIAL_DRAG", "TUTORIAL_SPEEDTRAPRACE", "TUTORIAL_TOLLBOOTH", "TUTORIAL_BOUNTY", "TUTORIAL_PURSUIT" })
            Console.WriteLine($"{k}_LABEL: '{(strings.TryGet(EaHash.Hash("_LABEL", EaHash.HashUpper(k)), out var t2) ? t2 : null)}'");
    }

    /// <summary>Per-frame MD5 of the yuv420p picture in display order (same as ffmpeg -f framemd5 -pix_fmt yuv420p).</summary>
    static void Md5(string file)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        int k = 0;
        foreach (var p in Frames(file, int.MaxValue))
        {
            var ms = new MemoryStream();
            WriteFlipped(ms, p);
            Console.WriteLine($"{k++} {Convert.ToHexString(md5.ComputeHash(ms.ToArray())).ToLowerInvariant()}");
        }
    }

    static void Cmp(string file, string refPath, int n)
    {
        var r = File.ReadAllBytes(refPath);
        int k = 0, exact = 0;
        foreach (var p in Frames(file, n))
        {
            var ms = new MemoryStream();
            WriteFlipped(ms, p);
            var mine = ms.ToArray();
            long off = (long)k * mine.Length;
            if (off + mine.Length > r.Length) { Console.WriteLine($"reference ends at frame {k}"); break; }
            int diff = 0, maxd = 0, first = -1;
            double se = 0;
            for (int i = 0; i < mine.Length; i++)
            {
                int d = Math.Abs(mine[i] - r[off + i]);
                if (d != 0) { diff++; if (first < 0) first = i; }
                maxd = Math.Max(maxd, d);
                se += d * d;
            }
            double psnr = se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * mine.Length / se);
            if (diff == 0) exact++;
            if (diff != 0 && exact == k)
            {
                // first damaged macroblock in bitstream order (the coded picture is bottom-up)
                int w = p.Width, h = p.Height, firstMb = -1;
                for (int mb = 0; mb < (w / 16) * (h / 16) && firstMb < 0; mb++)
                {
                    int mx = mb % (w / 16), my = mb / (w / 16);
                    for (int yy = 0; yy < 16 && firstMb < 0; yy++)
                        for (int xx = 0; xx < 16; xx++)
                        {
                            int cy = my * 16 + yy, cx = mx * 16 + xx, dy = h - 1 - cy;
                            if (mine[dy * w + cx] != r[off + dy * w + cx]) { firstMb = mb; break; }
                        }
                }
                Console.WriteLine($"   first bad luma MB (coded order) {firstMb}: row {firstMb / (w / 16)} col {firstMb % (w / 16)} {_dbg.DebugMb(firstMb)}");
                for (int q = Math.Max(0, firstMb - 3); q < firstMb + 3; q++) Console.WriteLine($"     mb {q}: {_dbg.DebugMb(q)}");
            }
            if (diff != 0 || k < 3 || k % 100 == 0)
                Console.WriteLine($"frame {k}: {(diff == 0 ? "bit-exact" : $"{diff} bytes differ (max {maxd}, first at {first}), PSNR {psnr:0.0} dB")}");
            k++;
        }
        Console.WriteLine($"{exact}/{k} frames bit-exact");
    }

    static void Stream(string file, double seconds)
    {
        using var s = new EaMovieStream(file);
        s.Start();
        var clock = Stopwatch.StartNew();
        var audioBuf = new float[4096];
        int shown = 0, late = 0, lastIndex = -1;
        double audioPos = 0;
        while (clock.Elapsed.TotalSeconds < seconds && !(s.VideoDrained && s.AudioDrained))
        {
            double t = clock.Elapsed.TotalSeconds;
            // consume audio at 1x like a sound card
            while (audioPos < t * s.Info.AudioRate) { int n = s.ReadAudio(audioBuf, 0, 2048); audioPos += 1024; if (n == 0) break; }
            var fr = s.Take(t);
            if (fr != null)
            {
                if (fr.Index != lastIndex + 1) late += fr.Index - lastIndex - 1;
                lastIndex = fr.Index;
                shown++;
                s.Release(fr);
            }
            System.Threading.Thread.Sleep(2);
        }
        double el = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"{Path.GetFileName(file)}: {el:0.0}s wall, shown {shown} frames (last index {lastIndex}, expected ~{(int)(el * s.Info.Fps)}), skipped {late}, " +
                          $"decoder {s.DecodedFrames} frames in {s.DecodeSeconds:0.00}s = {s.DecodeSeconds * 1000 / Math.Max(1, s.DecodedFrames):0.00} ms/frame, audio buffered {s.AudioBuffered}, error {s.Error ?? "none"}");
    }

    static void Audio(string file, string outPath)
    {
        using var f = new EaMovieFile(file);
        var a = new EaMovieAudio(f.Info);
        byte[] buf = null;
        float[] pcm = null;
        var all = new List<short>();
        while (f.Next(ref buf, out uint tag, out int size))
            if (tag == EaMovieTags.SCDl)
            {
                int n = a.DecodeBlock(buf, size, ref pcm);
                for (int i = 0; i < n * 2; i++) all.Add((short)Math.Max(-32768, Math.Min(32767, pcm[i] * 32768)));
            }
        using var o = new BinaryWriter(File.Create(outPath));
        int rate = f.Info.AudioRate, bytes = all.Count * 2;
        o.Write("RIFF".ToCharArray()); o.Write(36 + bytes); o.Write("WAVEfmt ".ToCharArray()); o.Write(16); o.Write((short)1); o.Write((short)2);
        o.Write(rate); o.Write(rate * 4); o.Write((short)4); o.Write((short)16); o.Write("data".ToCharArray()); o.Write(bytes);
        foreach (var v in all) o.Write(v);
        Console.WriteLine($"{all.Count / 2} samples at {rate} Hz -> {outPath}");
    }

    /// <summary>RGB PNG (BT.601 limited range, the picture flipped upright).</summary>
    public static void Png(string path, Vp6Picture p)
    {
        int w = p.Width, h = p.Height;
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++)
        {
            int sy = h - 1 - y, o = y * (w * 3 + 1);
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                int Y = p.Y[sy * w + x] - 16, U = p.U[(sy >> 1) * p.ChromaWidth + (x >> 1)] - 128, V = p.V[(sy >> 1) * p.ChromaWidth + (x >> 1)] - 128;
                int r = (298 * Y + 409 * V + 128) >> 8, g = (298 * Y - 100 * U - 208 * V + 128) >> 8, b = (298 * Y + 516 * U + 128) >> 8;
                raw[o++] = (byte)Math.Clamp(r, 0, 255);
                raw[o++] = (byte)Math.Clamp(g, 0, 255);
                raw[o++] = (byte)Math.Clamp(b, 0, 255);
            }
        }
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        uint crc = 0xFFFFFFFF;
        foreach (var b in td) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1; }
        var c = new byte[4]; Be(c, 0, (int)~crc); s.Write(c);
    }
}
