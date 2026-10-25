using System;

namespace NfsMw.Core
{
    /// <summary>
    /// JDLZ, the LZ variant NFS MW uses for .lzc files and compressed texture blocks.
    /// Header: "JDLZ" 02 10 00 00, u32 uncompressed size, u32 compressed size (header included).
    /// Two flag streams: flags1 bit=1 means back-reference, flags2 selects its encoding.
    /// </summary>
    public static class Jdlz
    {
        public static bool IsJdlz(byte[] src, int off) =>
            off + 16 <= src.Length && src[off] == (byte)'J' && src[off + 1] == (byte)'D' && src[off + 2] == (byte)'L' && src[off + 3] == (byte)'Z';

        public static byte[] Decompress(byte[] src, int off = 0)
        {
            if (!IsJdlz(src, off)) throw new InvalidOperationException("not a JDLZ block");
            int outLen = Chunks.I32(src, off + 8);
            int inEnd = off + Chunks.I32(src, off + 12);
            var dst = new byte[outLen];
            int ip = off + 16, op = 0;
            int f1 = 1, f2 = 1;
            while (ip < inEnd && op < outLen)
            {
                if (f1 == 1) f1 = src[ip++] | 0x100;
                if (f2 == 1) f2 = src[ip++] | 0x100;
                if ((f1 & 1) != 0)
                {
                    int b0 = src[ip], b1 = src[ip + 1];
                    ip += 2;
                    int length, dist;
                    if ((f2 & 1) != 0)
                    {
                        length = (b1 | ((b0 & 0xF0) << 4)) + 3;
                        dist = (b0 & 0x0F) + 1;
                    }
                    else
                    {
                        dist = (b1 | ((b0 & 0xE0) << 3)) + 17;
                        length = (b0 & 0x1F) + 3;
                    }
                    if (length > outLen - op) length = outLen - op;
                    for (int i = 0; i < length; i++, op++) dst[op] = dst[op - dist];
                    f2 >>= 1;
                }
                else
                {
                    dst[op++] = src[ip++];
                }
                f1 >>= 1;
            }
            if (op != outLen) throw new InvalidOperationException($"JDLZ short output {op}/{outLen}");
            return dst;
        }
    }
}
