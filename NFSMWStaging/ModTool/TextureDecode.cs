using System;
using NfsMw.Core;

namespace NfsMw.ModTool
{
    /// <summary>[ours] Top mip of a GameTexture to BGRA bytes (top row first, as stored): DXT1/3/5, A8R8G8B8, P8 + palette.</summary>
    static class TextureDecode
    {
        public static byte[] ToBgra(GameTexture t, out string error)
        {
            error = null;
            int w = t.Width, h = t.Height;
            var o = new byte[w * h * 4];
            if (t.Data == null) { error = "no pixel data"; return null; }
            int need = TexturePack.LevelSize(t.Format, w, h);
            if (need == 0) { error = $"unsupported format {t.FourCC}"; return null; }
            if (t.Data.Length < need) { error = $"truncated data ({t.Data.Length} < {need})"; return null; }
            switch (t.Format)
            {
                case TexFormat.Bgra32:
                    Buffer.BlockCopy(t.Data, 0, o, 0, w * h * 4);
                    break;
                case TexFormat.P8:
                    if (t.Palette == null) { error = "P8 without palette"; return null; }
                    for (int i = 0; i < w * h; i++)
                    {
                        int p = t.Data[i] * 4;
                        if (p + 3 >= t.Palette.Length) continue;
                        Buffer.BlockCopy(t.Palette, p, o, i * 4, 4); // palette entries are B,G,R,A like the pixels
                    }
                    break;
                default:
                {
                    int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
                    int bs = t.Format == TexFormat.Dxt1 ? 8 : 16;
                    var block = new byte[16 * 4];
                    for (int by = 0; by < bh; by++)
                        for (int bx = 0; bx < bw; bx++)
                        {
                            int s = (by * bw + bx) * bs;
                            DecodeBlock(t.Format, t.Data, s, block);
                            for (int y = 0; y < 4; y++)
                                for (int x = 0; x < 4; x++)
                                {
                                    int px = bx * 4 + x, py = by * 4 + y;
                                    if (px >= w || py >= h) continue;
                                    Buffer.BlockCopy(block, (y * 4 + x) * 4, o, (py * w + px) * 4, 4);
                                }
                        }
                    break;
                }
            }
            return o;
        }

        static void DecodeBlock(TexFormat f, byte[] d, int s, byte[] outBgra)
        {
            int colorAt = f == TexFormat.Dxt1 ? s : s + 8;
            int c0 = d[colorAt] | (d[colorAt + 1] << 8), c1 = d[colorAt + 2] | (d[colorAt + 3] << 8);
            var pal = new int[16]; // 4 x (b, g, r, a)
            Rgb565(c0, pal, 0);
            Rgb565(c1, pal, 4);
            bool fourColor = f != TexFormat.Dxt1 || c0 > c1;
            for (int k = 0; k < 3; k++)
            {
                if (fourColor)
                {
                    pal[8 + k] = (2 * pal[k] + pal[4 + k] + 1) / 3;
                    pal[12 + k] = (pal[k] + 2 * pal[4 + k] + 1) / 3;
                }
                else
                {
                    pal[8 + k] = (pal[k] + pal[4 + k]) / 2;
                    pal[12 + k] = 0;
                }
            }
            pal[3] = pal[7] = pal[11] = 255;
            pal[15] = fourColor ? 255 : 0;
            uint bits = BitConverter.ToUInt32(d, colorAt + 4);
            for (int i = 0; i < 16; i++)
            {
                int c = (int)((bits >> (i * 2)) & 3);
                for (int k = 0; k < 4; k++) outBgra[i * 4 + k] = (byte)pal[c * 4 + k];
            }

            if (f == TexFormat.Dxt3)
            {
                for (int i = 0; i < 16; i++)
                {
                    int a = (d[s + i / 2] >> ((i & 1) * 4)) & 0xF;
                    outBgra[i * 4 + 3] = (byte)(a * 17);
                }
            }
            else if (f == TexFormat.Dxt5)
            {
                int a0 = d[s], a1 = d[s + 1];
                var ap = new int[8];
                ap[0] = a0;
                ap[1] = a1;
                if (a0 > a1) for (int i = 1; i < 7; i++) ap[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
                else
                {
                    for (int i = 1; i < 5; i++) ap[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
                    ap[6] = 0;
                    ap[7] = 255;
                }
                ulong abits = 0;
                for (int i = 0; i < 6; i++) abits |= (ulong)d[s + 2 + i] << (8 * i);
                for (int i = 0; i < 16; i++) outBgra[i * 4 + 3] = (byte)ap[(int)((abits >> (3 * i)) & 7)];
            }
        }

        static void Rgb565(int c, int[] p, int o)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            p[o] = (b << 3) | (b >> 2);
            p[o + 1] = (g << 2) | (g >> 4);
            p[o + 2] = (r << 3) | (r >> 2);
        }
    }
}
