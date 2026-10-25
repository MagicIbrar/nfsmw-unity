using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    public enum TexFormat
    {
        Unknown,
        Dxt1,
        Dxt3,
        Dxt5,
        Bgra32, // D3DFMT_A8R8G8B8 (21), bytes B,G,R,A
        P8,     // 8-bit indices + 256 BGRA palette (41)
    }

    public sealed class GameTexture
    {
        public uint Hash;
        public uint ClassHash;
        public string Name;
        public int Width, Height, MipCount;
        public TexFormat Format;
        public string FourCC;
        public byte AlphaUsage;   // TextureInfo +0x55 AlphaUsageType: 0 none, 1 punch-through (alpha test), 2 modulated
        public byte AlphaBlend;   // +0x56 AlphaBlendType: 0 src copy (opaque), 1 blend, 2 additive, 3 subtractive, 4 overbright
        /// <summary>TextureInfo +0x52 ScrollType (0 none, 1 smooth, 2 snap, 3 offset/scale) and the raw int16 +0x58 time
        /// step, +0x5A/+0x5C S/T speeds, +0x5E/+0x60 offsets and +0x62/+0x64 scales (fixed point, see TextureScroll).</summary>
        public byte ScrollType;
        public short ScrollTimeStep, ScrollSpeedS, ScrollSpeedT, OffsetS, OffsetT, ScaleS, ScaleT;
        public byte TileUV;       // +0x4F: bit 0 = wrap U, bit 1 = wrap V
        public byte RenderOrder;  // +0x51
        public byte[] Data;       // top level first, then smaller mips
        public byte[] Palette;    // P8 only
        public string PackName;

        public override string ToString() => $"{Name} {Width}x{Height} {Format} mips={MipCount}";
    }

    /// <summary>
    /// Texture pack (TPK), chunk 0xB3300000:
    ///   B3310000 header: 33310001 pack info, 33310002 hash list, 33310003 JDLZ block table (compressed packs),
    ///                    33310004 TextureInfo[0x7C], 33310005 format records[0x20]
    ///   B3320000 data:   33320002 pixels, 128-byte aligned
    /// Compressed packs store each texture as one JDLZ/HUFF block: pixels, then its TextureInfo, then its format record.
    /// </summary>
    public static class TexturePack
    {
        public const uint Id = 0xB3300000;
        const int InfoSize = 0x7C, FormatSize = 0x20;

        /// <summary>All packs in [start, end). withData = false only fills hash/name/size (for indexing).</summary>
        public static List<GameTexture> ReadAll(byte[] data, int start, int end, bool withData = true)
        {
            var list = new List<GameTexture>();
            foreach (var c in Chunks.FindAll(data, start, end, Id)) Read(data, c, list, withData);
            return list;
        }

        public static void Read(byte[] d, Chunk tpk, List<GameTexture> output, bool withData = true)
        {
            string packName = "";
            int infoOff = -1, infoCount = 0, fmtOff = -1, blockOff = -1, blockCount = 0, pixels = -1;
            foreach (var c in Chunks.Children(d, tpk))
            {
                if (c.Id == 0xB3310000)
                {
                    foreach (var h in Chunks.Children(d, c))
                    {
                        switch (h.Id)
                        {
                            case 0x33310001: packName = Chunks.CString(d, h.DataStart + 4, 28); break;
                            case 0x33310003: blockOff = h.DataStart; blockCount = h.Size / 24; break;
                            case 0x33310004: infoOff = h.DataStart; infoCount = h.Size / InfoSize; break;
                            case 0x33310005: fmtOff = h.DataStart; break;
                        }
                    }
                }
                else if (c.Id == 0xB3320000)
                {
                    foreach (var p in Chunks.Children(d, c))
                        if (p.Id == 0x33320002) pixels = Chunks.Align(p.DataStart, 0x80);
                }
            }

            for (int i = 0; i < infoCount && pixels >= 0; i++)
            {
                var t = ParseInfo(d, infoOff + i * InfoSize, fmtOff >= 0 ? fmtOff + i * FormatSize : -1, d, pixels, false, withData);
                if (t == null) continue;
                t.PackName = packName;
                if (Mods.Active) Mods.ApplyTexture(t); // [ours] Mods/textures/... replacement
                output.Add(t);
            }

            for (int i = 0; i < blockCount; i++)
            {
                int e = blockOff + i * 24;
                if (!withData)
                {
                    output.Add(new GameTexture { Hash = Chunks.U32(d, e), PackName = packName });
                    continue;
                }
                int off = Chunks.I32(d, e + 4);
                // block offsets are relative to the pack chunk; for stand-alone files that is the file start
                int at = tpk.Start + off;
                if (!Compression.IsCompressed(d, at)) at = off;
                if (!Compression.IsCompressed(d, at)) continue;
                byte[] blob = Compression.Decompress(d, at);
                int info = blob.Length - FormatSize - InfoSize;
                // the info keeps its offset in the uncompressed pack; in the block the pixels start at 0
                var t = ParseInfo(blob, info, info + InfoSize, blob, 0, true, true);
                if (t == null) continue;
                t.PackName = packName;
                if (Mods.Active) Mods.ApplyTexture(t); // [ours] Mods/textures/... replacement
                output.Add(t);
            }
        }

        static GameTexture ParseInfo(byte[] d, int o, int fmt, byte[] pixelSrc, int pixelBase, bool pixelsAtBase, bool withData)
        {
            var t = new GameTexture
            {
                Name = Chunks.CString(d, o + 0x0C, 24),
                Hash = Chunks.U32(d, o + 0x24),
                ClassHash = Chunks.U32(d, o + 0x28),
                Width = Chunks.U16(d, o + 0x44),
                Height = Chunks.U16(d, o + 0x46),
                MipCount = Math.Max(1, (int)d[o + 0x4E]),
                AlphaUsage = d[o + 0x55],
                AlphaBlend = d[o + 0x56],
                TileUV = d[o + 0x4F],
                ScrollType = d[o + 0x52],
                ScrollTimeStep = (short)Chunks.U16(d, o + 0x58),
                ScrollSpeedS = (short)Chunks.U16(d, o + 0x5A),
                ScrollSpeedT = (short)Chunks.U16(d, o + 0x5C),
                OffsetS = (short)Chunks.U16(d, o + 0x5E),
                OffsetT = (short)Chunks.U16(d, o + 0x60),
                ScaleS = (short)Chunks.U16(d, o + 0x62),
                ScaleT = (short)Chunks.U16(d, o + 0x64),
                RenderOrder = d[o + 0x51],
            };
            int dataOff = Chunks.I32(d, o + 0x30);
            int palOff = Chunks.I32(d, o + 0x34);
            int dataSize = Chunks.I32(d, o + 0x38);
            int palSize = Chunks.I32(d, o + 0x3C);
            if (t.Width == 0 || t.Height == 0 || dataSize <= 0) return null;

            uint four = fmt >= 0 ? Chunks.U32(d, fmt + 0x14) : 0;
            t.FourCC = four > 0xFF ? System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(four)) : four.ToString();
            t.Format = four switch
            {
                0x31545844 => TexFormat.Dxt1, // "DXT1"
                0x33545844 => TexFormat.Dxt3,
                0x35545844 => TexFormat.Dxt5,
                21 => TexFormat.Bgra32,
                41 => TexFormat.P8,
                _ => TexFormat.Unknown,
            };

            if (!withData) return t;
            int src = pixelsAtBase ? pixelBase : pixelBase + dataOff;
            if (src < 0 || src + dataSize > pixelSrc.Length) return null;
            t.Data = new byte[dataSize];
            Buffer.BlockCopy(pixelSrc, src, t.Data, 0, dataSize);
            if (pixelsAtBase) palOff -= dataOff; // palette keeps its distance from the pixels
            if (t.Format == TexFormat.P8 && palSize > 0 && pixelBase + palOff + palSize <= pixelSrc.Length)
            {
                t.Palette = new byte[palSize];
                Buffer.BlockCopy(pixelSrc, pixelBase + palOff, t.Palette, 0, palSize);
            }
            return t;
        }

        /// <summary>Bytes for one mip level of a w x h image.</summary>
        public static int LevelSize(TexFormat f, int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            switch (f)
            {
                case TexFormat.Dxt1: return Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8;
                case TexFormat.Dxt3:
                case TexFormat.Dxt5: return Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16;
                case TexFormat.Bgra32: return w * h * 4;
                case TexFormat.P8: return w * h;
                default: return 0;
            }
        }

        /// <summary>How many mips the stored data really holds (some textures under-report or truncate).</summary>
        public static int UsableMips(GameTexture t)
        {
            int total = 0, mips = 0;
            for (int i = 0; i < t.MipCount; i++)
            {
                int s = LevelSize(t.Format, t.Width >> i, t.Height >> i);
                if (s == 0 || total + s > t.Data.Length) break;
                total += s;
                mips++;
            }
            return mips;
        }
    }
}
