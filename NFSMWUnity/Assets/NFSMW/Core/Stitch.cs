using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>One sample of a stitch (SND_SampleRef, STICH_Struct.h, 16 bytes).</summary>
    public struct StitchSample
    {
        public int SampleIndex, Type, Volume, Pitch, OffsetMs, Azimuth, RndVol, RndPitch, Priority, RollOff;
    }

    /// <summary>A stitched sound (SND_Stich): several bank samples layered with offsets, volumes and random spreads.</summary>
    public sealed class Stitch
    {
        public uint NameHash;
        public int Volume, Index, Type, RndPitch, RndVol;
        public StitchSample[] Samples;
    }

    /// <summary>
    /// The sound stitch bundle (STICH_Loader.cpp LoaderSoundStichs): chunk 0x8003B500 in GLOBAL\InGameB holds
    /// 0x3B502 chunks in pairs — a 0x14-byte SND_Stich {u32 NameHash, u16 Volume, u16 StichIndex, s8 eStichType,
    /// u8 Num_SampleRefs, s16 RND_Pitch, u16 RND_Vol, ptr} then the sample refs {u8 SampleIndex, s8 eStichType, u16
    /// Volume, u16 Pitch, u16 Offset, u16 Az, u16 RND_Vol, s16 RND_Pitch, u8 Priority, u8 eRollOffType}. MW PC has 608
    /// collision stitches (STITCH_COL.h STICH_COLLISION_TYPE: car-car front 0-15, side 16-31, smackable 32-40, wall
    /// front 41-56, wall side 57-72, rollover 73-80, bottom-out 81-96, ...), samples from IG_GLOBAL\Stich_Collision_MB.
    /// </summary>
    public static class StitchBundle
    {
        public const uint BundleId = 0x8003B500, DataId = 0x3B502;

        public static List<Stitch>[] Read(byte[] d)
        {
            var lists = new[] { new List<Stitch>(), new List<Stitch>(), new List<Stitch>() };
            foreach (var b in Chunks.FindAll(d, 0, d.Length, BundleId))
            {
                Stitch pending = null;
                foreach (var c in Chunks.Children(d, b))
                {
                    int o = c.DataStart;
                    if (pending == null && c.Id == DataId && c.Size == 0x14)
                    {
                        pending = new Stitch
                        {
                            NameHash = Chunks.U32(d, o),
                            Volume = Chunks.U16(d, o + 4),
                            Index = Chunks.U16(d, o + 6),
                            Type = (sbyte)d[o + 8],
                            RndPitch = Chunks.I16(d, o + 10),
                            RndVol = Chunks.U16(d, o + 12),
                            Samples = new StitchSample[d[o + 9]],
                        };
                        continue;
                    }
                    if (pending != null)
                    {
                        for (int i = 0; i < pending.Samples.Length && o + i * 16 + 16 <= c.End; i++)
                        {
                            int p = o + i * 16;
                            pending.Samples[i] = new StitchSample
                            {
                                SampleIndex = d[p], Type = (sbyte)d[p + 1], Volume = Chunks.U16(d, p + 2), Pitch = Chunks.U16(d, p + 4),
                                OffsetMs = Chunks.U16(d, p + 6), Azimuth = Chunks.U16(d, p + 8), RndVol = Chunks.U16(d, p + 10),
                                RndPitch = Chunks.I16(d, p + 12), Priority = d[p + 14], RollOff = d[p + 15],
                            };
                        }
                        if (pending.Type >= 0 && pending.Type < 3)
                        {
                            var l = lists[pending.Type];
                            while (l.Count <= pending.Index) l.Add(null);
                            l[pending.Index] = pending;
                        }
                        pending = null;
                    }
                }
            }
            return lists;
        }
    }
}
