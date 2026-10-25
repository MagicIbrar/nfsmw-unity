using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>One layer of the sun sprite (World/Sun.hpp SunLayer, 0x24 bytes).</summary>
    public sealed class SunLayer
    {
        public int Texture;            // +0x00 SUN_TEXTURE: 0 centre, 1 halo, 2 major rays, 3 minor rays, 4 ring
        public int AlphaType;          // +0x04 SUN_ALPHA (0 blend, 1 add); not read by the PC renderer [?]
        public float IntensityScale;   // +0x08
        public float Size;             // +0x0C, in 640x480 screen units
        public float OffsetX, OffsetY; // +0x10 / +0x14
        public byte[] Colour = new byte[4]; // +0x18
        public int Angle;              // +0x1C, 16-bit bAngle units
        public float SweepAngleAmount; // +0x20
    }

    /// <summary>A sun definition (World/Sun.hpp SunChunkInfo, 0x110 bytes, Version 2).</summary>
    public sealed class SunInfo
    {
        public int Version;
        public uint NameHash;
        public string Name;
        public Vec3 Position, CarShadowPosition;
        public readonly SunLayer[] Layers = new SunLayer[6];

        /// <summary>The sun texture names in SUN_TEXTURE order (SunTrackLoader 0x503790; Generated/Hash.hpp STRINGHASH_SUN*).</summary>
        public static readonly string[] TextureNames = { "SUNCENTER", "SUNHALO", "SUNMAJORRAYS", "SUNMINORRAYS", "SUNRING" };

        /// <summary>
        /// Chunk 0x34202 (BCHUNK_SUN_INFOS, GLOBAL\GLOBALB): LoaderSun 0x4FD330 takes the data from the chunk data start
        /// rounded up to 16 and counts (end - start) / 0x110 entries. Offsets here are relative to the bundle start,
        /// which the game loads 16-aligned.
        /// </summary>
        public static List<SunInfo> ReadAll(byte[] d)
        {
            var list = new List<SunInfo>();
            Walk(d, 0, d.Length, 0, list);
            return list;
        }

        static void Walk(byte[] d, int o, int end, int depth, List<SunInfo> list)
        {
            while (o + 8 <= end)
            {
                uint id = BitConverter.ToUInt32(d, o);
                int size = BitConverter.ToInt32(d, o + 4);
                if (size < 0 || o + 8 + size > end) return;
                if (id == 0x34202)
                {
                    int start = (o + 8 + 15) & ~15, stop = o + 8 + size;
                    for (int p = start; p + 0x110 <= stop; p += 0x110) list.Add(Read(d, p));
                }
                else if ((id & 0x80000000u) != 0 && depth < 4) Walk(d, o + 8, o + 8 + size, depth + 1, list);
                o += 8 + size;
            }
        }

        static SunInfo Read(byte[] d, int p)
        {
            var s = new SunInfo
            {
                Version = BitConverter.ToInt32(d, p),
                NameHash = BitConverter.ToUInt32(d, p + 4),
                Name = Chunks.CString(d, p + 8, 24),
                Position = Vec3.Read(d, p + 0x20),
                CarShadowPosition = Vec3.Read(d, p + 0x2C),
            };
            for (int i = 0; i < 6; i++)
            {
                int q = p + 0x38 + i * 0x24;
                var l = new SunLayer
                {
                    Texture = BitConverter.ToInt32(d, q),
                    AlphaType = BitConverter.ToInt32(d, q + 4),
                    IntensityScale = BitConverter.ToSingle(d, q + 8),
                    Size = BitConverter.ToSingle(d, q + 0xC),
                    OffsetX = BitConverter.ToSingle(d, q + 0x10),
                    OffsetY = BitConverter.ToSingle(d, q + 0x14),
                    Angle = BitConverter.ToInt32(d, q + 0x1C),
                    SweepAngleAmount = BitConverter.ToSingle(d, q + 0x20),
                };
                Array.Copy(d, q + 0x18, l.Colour, 0, 4);
                s.Layers[i] = l;
            }
            return s;
        }

        public override string ToString() => $"{Name} v{Version} pos {Position}";
    }
}
