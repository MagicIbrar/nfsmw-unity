using System;
using System.IO;
using System.IO.Compression;

namespace NfsMw.Core
{
    /// <summary>
    /// [ours] Minimal PNG codec for the mod system (Mods.cs) and the mod tool. Pixels are BGRA bytes, top row first, the
    /// same layout as a TexFormat.Bgra32 GameTexture. Decodes non-interlaced greyscale, RGB, palette (with tRNS), grey+alpha
    /// and RGBA at 1/2/4/8/16 bits (16-bit keeps the high byte). zlib data is inflated with DeflateStream after skipping the
    /// 2-byte zlib header (Unity's .NET profile has no ZLibStream).
    /// </summary>
    public static class Png
    {
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool Decode(byte[] file, out int width, out int height, out byte[] bgra, out string error)
        {
            width = height = 0;
            bgra = null;
            error = null;
            try
            {
                return DecodeCore(file, out width, out height, out bgra, out error);
            }
            catch (Exception e)
            {
                error = "corrupt PNG: " + e.Message;
                return false;
            }
        }

        static bool DecodeCore(byte[] f, out int width, out int height, out byte[] bgra, out string error)
        {
            width = height = 0;
            bgra = null;
            error = null;
            if (f.Length < 8) { error = "not a PNG"; return false; }
            for (int i = 0; i < 8; i++) if (f[i] != Signature[i]) { error = "not a PNG"; return false; }

            int depth = 0, colorType = 0, interlace = 0;
            byte[] palette = null, trns = null;
            var idat = new MemoryStream();
            int o = 8;
            while (o + 8 <= f.Length)
            {
                int len = BE32(f, o);
                string type = System.Text.Encoding.ASCII.GetString(f, o + 4, 4);
                int data = o + 8;
                if (len < 0 || data + len > f.Length) { error = "truncated PNG"; return false; }
                switch (type)
                {
                    case "IHDR":
                        width = BE32(f, data);
                        height = BE32(f, data + 4);
                        depth = f[data + 8];
                        colorType = f[data + 9];
                        interlace = f[data + 12];
                        break;
                    case "PLTE": palette = Slice(f, data, len); break;
                    case "tRNS": trns = Slice(f, data, len); break;
                    case "IDAT": idat.Write(f, data, len); break;
                }
                if (type == "IEND") break;
                o = data + len + 4; // skip CRC
            }
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) { error = $"bad size {width}x{height}"; return false; }
            if (interlace != 0) { error = "interlaced PNGs are not supported (save without interlacing)"; return false; }
            int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0) { error = $"unsupported colour type {colorType}"; return false; }
            if (depth != 1 && depth != 2 && depth != 4 && depth != 8 && depth != 16) { error = $"unsupported bit depth {depth}"; return false; }
            if (depth < 8 && colorType != 0 && colorType != 3) { error = $"bad bit depth {depth} for colour type {colorType}"; return false; }
            if (colorType == 3 && palette == null) { error = "palette PNG without PLTE"; return false; }

            int bitsPerPixel = channels * depth;
            int stride = (width * bitsPerPixel + 7) / 8;
            int bpp = Math.Max(1, bitsPerPixel / 8);
            var raw = new byte[(stride + 1) * height];
            idat.Position = 2; // zlib header (CMF, FLG); the Adler-32 trailer is ignored
            using (var z = new DeflateStream(idat, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < raw.Length) { error = "truncated image data"; return false; }
            }

            // undo the per-row filters in place
            var prev = new byte[stride];
            var cur = new byte[stride];
            bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int ro = y * (stride + 1);
                int filter = raw[ro];
                Buffer.BlockCopy(raw, ro + 1, cur, 0, stride);
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                    int v = cur[x];
                    switch (filter)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) >> 1; break;
                        case 4:
                        {
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            break;
                        }
                    }
                    cur[x] = (byte)v;
                }
                for (int x = 0; x < width; x++)
                {
                    byte r, g, bl, al = 255;
                    int d = (y * width + x) * 4;
                    switch (colorType)
                    {
                        case 0:
                        {
                            int s = Sample(cur, x, depth);
                            r = g = bl = (byte)s;
                            if (trns != null && trns.Length >= 2 && RawSample(cur, x, depth) == BE16(trns, 0)) al = 0;
                            break;
                        }
                        case 3:
                        {
                            int idx = RawSample(cur, x, depth);
                            r = g = bl = 0;
                            if (idx * 3 + 2 < palette.Length) { r = palette[idx * 3]; g = palette[idx * 3 + 1]; bl = palette[idx * 3 + 2]; }
                            if (trns != null && idx < trns.Length) al = trns[idx];
                            break;
                        }
                        default:
                        {
                            int step = depth / 8, px = x * channels * step;
                            if (colorType == 4)
                            {
                                r = g = bl = cur[px];
                                al = cur[px + step];
                            }
                            else
                            {
                                r = cur[px];
                                g = cur[px + step];
                                bl = cur[px + 2 * step];
                                if (colorType == 6) al = cur[px + 3 * step];
                                else if (trns != null && trns.Length >= 6 && depth == 8 && r == trns[1] && g == trns[3] && bl == trns[5]) al = 0;
                            }
                            break;
                        }
                    }
                    bgra[d] = bl;
                    bgra[d + 1] = g;
                    bgra[d + 2] = r;
                    bgra[d + 3] = al;
                }
                var t = prev; prev = cur; cur = t;
            }
            return true;
        }

        // sub-byte samples are packed MSB first; 16-bit samples keep the high byte
        static int RawSample(byte[] row, int x, int depth)
        {
            switch (depth)
            {
                case 8: return row[x];
                case 16: return (row[x * 2] << 8) | row[x * 2 + 1];
                default:
                {
                    int bit = x * depth, shift = 8 - depth - (bit & 7);
                    return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
                }
            }
        }

        static int Sample(byte[] row, int x, int depth)
        {
            int v = RawSample(row, x, depth);
            return depth switch { 16 => v >> 8, 8 => v, _ => v * 255 / ((1 << depth) - 1) };
        }

        /// <summary>8-bit RGBA PNG from BGRA bytes (top row first). alpha = false writes RGB.</summary>
        public static byte[] Encode(int width, int height, byte[] bgra, bool alpha = true)
        {
            int ch = alpha ? 4 : 3, stride = width * ch;
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int ro = y * (stride + 1);
                raw[ro] = 0;
                for (int x = 0; x < width; x++)
                {
                    int s = (y * width + x) * 4, d = ro + 1 + x * ch;
                    raw[d] = bgra[s + 2];
                    raw[d + 1] = bgra[s + 1];
                    raw[d + 2] = bgra[s];
                    if (alpha) raw[d + 3] = bgra[s + 3];
                }
            }
            // Up filter on every row: cheap and usually smaller than none
            for (int y = height - 1; y > 0; y--)
            {
                int ro = y * (stride + 1), po = (y - 1) * (stride + 1);
                raw[ro] = 2;
                for (int x = 1; x <= stride; x++) raw[ro + x] = (byte)(raw[ro + x] - raw[po + x]);
            }

            var z = new MemoryStream();
            z.WriteByte(0x78);
            z.WriteByte(0x9C);
            using (var ds = new DeflateStream(z, CompressionLevel.Optimal, true)) ds.Write(raw, 0, raw.Length);
            uint adler = Adler32(raw);
            z.WriteByte((byte)(adler >> 24));
            z.WriteByte((byte)(adler >> 16));
            z.WriteByte((byte)(adler >> 8));
            z.WriteByte((byte)adler);

            var png = new MemoryStream();
            png.Write(Signature, 0, 8);
            var ihdr = new byte[13];
            PutBE32(ihdr, 0, width);
            PutBE32(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = (byte)(alpha ? 6 : 2);
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", z.ToArray());
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            PutBE32(head, 0, data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = Crc(head, 4, 4, 0xFFFFFFFFu);
            crc = Crc(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
            var c = new byte[4];
            PutBE32(c, 0, (int)crc);
            s.Write(c, 0, 4);
        }

        static uint[] _crcTable;

        static uint Crc(byte[] d, int off, int len, uint crc)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                _crcTable = t;
            }
            for (int i = 0; i < len; i++) crc = _crcTable[(crc ^ d[off + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static uint Adler32(byte[] d)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < d.Length; i++)
            {
                a = (a + d[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static int BE32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        static int BE16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

        static void PutBE32(byte[] d, int o, int v)
        {
            d[o] = (byte)(v >> 24);
            d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8);
            d[o + 3] = (byte)v;
        }

        static byte[] Slice(byte[] d, int o, int n)
        {
            var r = new byte[n];
            Buffer.BlockCopy(d, o, r, 0, n);
            return r;
        }
    }
}
