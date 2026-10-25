using System;

namespace NfsMw.Core
{
    /// <summary>
    /// EA Huffman codec ("HUFF" blocks), ported from speed.exe @0x64DCA0.
    /// Block header: "HUFF" 01 10 00 00, u32 uncompressed size, u32 compressed size; the stream follows.
    /// Stream: u16 type (0x30FB plain, 0x32FB delta, 0x34FB double delta; 0x8000 = 32-bit sizes,
    /// 0x100 = extra size field), uncompressed size, escape ("clue") byte, canonical code-length counts,
    /// the symbol list as move-to-front distances, then codes. The clue symbol escapes runs of the
    /// previous byte, literal bytes and the end marker.
    /// </summary>
    public static class EaHuff
    {
        public static bool IsHuff(byte[] src, int off) =>
            off + 16 <= src.Length && src[off] == (byte)'H' && src[off + 1] == (byte)'U' && src[off + 2] == (byte)'F' && src[off + 3] == (byte)'F';

        sealed class Bits
        {
            readonly byte[] _s;
            int _pos;
            ulong _buf;   // MSB-first window
            int _count;   // valid bits in _buf

            public Bits(byte[] s, int pos) { _s = s; _pos = pos; }

            void Fill(int need)
            {
                while (_count < need)
                {
                    ulong b = _pos < _s.Length ? _s[_pos] : 0u;
                    _pos++;
                    _buf |= b << (56 - _count);
                    _count += 8;
                }
            }

            public uint Peek(int n)
            {
                if (n == 0) return 0;
                Fill(n);
                return (uint)(_buf >> (64 - n));
            }

            public void Skip(int n)
            {
                Fill(n);
                _buf <<= n;
                _count -= n;
            }

            public uint Get(int n)
            {
                uint v = Peek(n);
                Skip(n);
                return v;
            }
        }

        // variable-length number (same code at every call site in the exe)
        static int GetNum(Bits br)
        {
            if (br.Peek(1) == 1) return (int)br.Get(3) - 4;
            int n = 2;
            uint top = br.Peek(16);
            if (top != 0)
            {
                int z = 0;
                while ((top & (0x8000u >> z)) == 0) z++;
                n += z;
                br.Skip(z + 1);
            }
            else
            {
                do { n++; } while (br.Get(1) == 0);
            }
            uint v = n < 17 ? br.Get(n) : (br.Get(n - 16) << 16) | br.Get(16);
            return (int)(v - 4 + (1u << (n & 31)));
        }

        public static byte[] Decompress(byte[] src, int off)
        {
            if (!IsHuff(src, off)) throw new InvalidOperationException("not a HUFF block");
            var br = new Bits(src, off + 16);

            uint type = br.Get(16);
            uint ulen;
            if ((type & 0x8000) != 0)
            {
                if ((type & 0x100) != 0) br.Skip(32);
                ulen = br.Get(32);
            }
            else
            {
                if ((type & 0x100) != 0) br.Skip(24);
                ulen = br.Get(24);
            }
            type &= ~0x100u;
            byte clue = (byte)br.Get(8);

            // canonical code-length counts
            var baseIdx = new int[64];
            var counts = new int[64];
            var limit = new uint[66];
            int code = 0, total = 0, bits = 1, num;
            uint cmp;
            do
            {
                baseIdx[bits] = code * 2 - total;
                num = GetNum(br);
                counts[bits] = num;
                code = code * 2 + num;
                total += num;
                cmp = num != 0 ? ((uint)code << ((16 - bits) & 31)) & 0xFFFF : 0;
                limit[bits + 1] = cmp;
                bits++;
            } while ((num == 0 || cmp != 0) && bits < 62);
            int maxBits = bits - 1;
            limit[bits] = 0xFFFFFFFF;

            // symbols in code order, sent as distances over the not-yet-used byte values
            var used = new bool[256];
            var symbols = new byte[Math.Max(total, 1)];
            byte c = 0xFF;
            for (int i = 0; i < total; i++)
            {
                int d = GetNum(br) + 1;
                do
                {
                    c++;
                    if (!used[c]) d--;
                } while (d != 0);
                used[c] = true;
                symbols[i] = c;
            }

            // 8-bit lookup: code length per prefix (0x40 = longer code, 0x60 = the clue)
            var lenTable = new byte[256];
            var symTable = new byte[256];
            for (int i = 0; i < 256; i++) lenTable[i] = 0x40;
            int clueLen = 0, p = 0, si = 0;
            for (int b = 1, shift = 7; b <= maxBits && shift >= 0; b++, shift--)
            {
                int size = 1 << shift;
                for (int k = 0; k < counts[b]; k++)
                {
                    byte ch = symbols[si++];
                    byte len = (byte)b;
                    if (ch == clue)
                    {
                        clueLen = b;
                        len = 0x60;
                    }
                    for (int j = 0; j < size && p + j < 256; j++)
                    {
                        lenTable[p + j] = len;
                        symTable[p + j] = ch;
                    }
                    p += size;
                }
            }

            var dst = new byte[ulen];
            int op = 0;
            while (true)
            {
                uint idx = br.Peek(8);
                int len = lenTable[idx];
                if (len <= 8)
                {
                    br.Skip(len);
                    if (op >= dst.Length) break;
                    dst[op++] = symTable[idx];
                    continue;
                }
                int nb;
                if (len == 0x60) nb = clueLen;
                else
                {
                    nb = 8;
                    uint peek16 = br.Peek(16);
                    do { nb++; } while (nb < 62 && limit[nb + 1] <= peek16);
                }
                int s = (int)br.Get(nb) - baseIdx[nb];
                if (s < 0 || s >= total) break;
                byte sym = symbols[s];
                if (sym != clue)
                {
                    if (op >= dst.Length) break;
                    dst[op++] = sym;
                    continue;
                }
                int run = GetNum(br);
                if (run == 0)
                {
                    if (br.Get(1) == 1) break; // end of stream
                    if (op >= dst.Length) break;
                    dst[op++] = (byte)br.Get(8);
                }
                else
                {
                    byte prev = op > 0 ? dst[op - 1] : (byte)0;
                    for (int i = 0; i < run && op < dst.Length; i++) dst[op++] = prev;
                }
            }

            if (type == 0x32FB || type == 0xB2FB)
            {
                byte sum = 0;
                for (int i = 0; i < dst.Length; i++) { sum += dst[i]; dst[i] = sum; }
            }
            else if (type == 0x34FB || type == 0xB4FB)
            {
                byte d1 = 0, sum = 0;
                for (int i = 0; i < dst.Length; i++) { d1 += dst[i]; sum += d1; dst[i] = sum; }
            }
            return dst;
        }
    }

    /// <summary>Compressed block dispatcher (speed.exe @0x65B5C0): JDLZ, HUFF or stored RAWW.</summary>
    public static class Compression
    {
        public static bool IsCompressed(byte[] d, int off) =>
            Jdlz.IsJdlz(d, off) || EaHuff.IsHuff(d, off) || IsRaw(d, off);

        static bool IsRaw(byte[] d, int off) =>
            off + 16 <= d.Length && d[off] == (byte)'R' && d[off + 1] == (byte)'A' && d[off + 2] == (byte)'W' && d[off + 3] == (byte)'W';

        public static byte[] Decompress(byte[] d, int off)
        {
            if (Jdlz.IsJdlz(d, off)) return Jdlz.Decompress(d, off);
            if (EaHuff.IsHuff(d, off)) return EaHuff.Decompress(d, off);
            if (IsRaw(d, off))
            {
                var r = new byte[Chunks.I32(d, off + 8)];
                Buffer.BlockCopy(d, off + 16, r, 0, r.Length);
                return r;
            }
            throw new InvalidOperationException("unknown compression");
        }
    }
}
