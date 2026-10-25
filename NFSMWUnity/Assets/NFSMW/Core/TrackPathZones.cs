using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>One TrackPathZone (World/TrackPath.hpp:68-84, 0x244 bytes in memory, variable on disc).</summary>
    public sealed class TrackPathZone
    {
        public int Type;
        /// <summary>Position / Direction (game X/Y; for hiding zones Position is not inside the polygon, so it is a
        /// reset or reference point rather than a centre).</summary>
        public float PosX, PosY, DirX, DirY;
        /// <summary>Game Z (Unity y). 0 = any height (AIPerpVehicle::Update, SFXCTL_Tunnel::GetTunnelType).</summary>
        public float Elevation;
        public float MinX, MinY, MaxX, MaxY;
        /// <summary>Data[4]: hiding zones keep bStringHash("Car") / ("Heli") in Data[0] to hide from one kind only.</summary>
        public uint[] Data = new uint[4];
        public float[] Xs, Ys;

        /// <summary>TrackPathZone::IsPointInside for a game X/Y point (Unity x, z): bbox, then an even-odd polygon test.</summary>
        public bool Contains(float x, float y)
        {
            if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
            bool inside = false;
            for (int i = 0, j = Xs.Length - 1; i < Xs.Length; j = i++)
            {
                if ((Ys[i] > y) != (Ys[j] > y) && x < (Xs[j] - Xs[i]) * (y - Ys[i]) / (Ys[j] - Ys[i]) + Xs[i]) inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>
    /// TrackPathManager zones (World/TrackPath.hpp; LoaderTrackPath) from TRACKS\L2RA.BUN chunk 0x3414A
    /// (BCHUNK_SPEED_TRACK_PATH_ZONES, inside 0x80034147). Records are packed back to back after 0x11 alignment
    /// padding: {i32 type, f32 pos x/y, f32 dir x/y, f32 elevation, i8 source, i8 cached, i16 visit, u32 user,
    /// f32 bbox min x/y, max x/y @0x20, u32 Data[4] @0x30, u16 point count @0x40, u16 record size @0x42,
    /// (x, y) points @0x44}. PC 1.3 L2RA counts: tunnel 70, overpass 143, small overpass 178, streamer 211,
    /// garage 8, hidden 34, traffic pattern 11, dynamic 14, neighbourhood 8, jump cam 22, no cop spawn 3,
    /// pursuit start 3 (harness <c>copzones</c>).
    /// </summary>
    public static class TrackPathZones
    {
        // eTrackPathZoneType (World/TrackPath.hpp:9-26)
        public const int Reset = 0, ResetToPoint = 1, GuidedReset = 2, Tunnel = 3, Overpass = 4, OverpassSmall = 5, StreamerPrediction = 6,
            Garage = 7, Hidden = 8, TrafficPattern = 9, Dynamic = 10, Neighbourhood = 11, JumpCam = 12, NoCopSpawn = 13, PursuitStart = 14, Count = 15;

        /// <summary>bStringHash("Car") and ("Heli"): a hiding zone's Data[0] (AIVehicle.cpp:2067-2068).</summary>
        public static readonly uint CarHash = EaHash.Hash("Car"), HeliHash = EaHash.Hash("Heli");

        public static List<TrackPathZone> Parse(byte[] d)
        {
            var all = new List<TrackPathZone>();
            if (d == null) return all;
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x3414A))
            {
                int o = c.DataStart;
                while (o < c.End && d[o] == 0x11) o++;
                for (int guard = 0; o + 0x44 <= c.End && guard < 100000; guard++)
                {
                    int n = Chunks.U16(d, o + 0x40), size = Chunks.U16(d, o + 0x42);
                    if (o + 0x44 + n * 8 > c.End) break;
                    var z = new TrackPathZone
                    {
                        Type = Chunks.I32(d, o),
                        PosX = Chunks.F32(d, o + 4), PosY = Chunks.F32(d, o + 8),
                        DirX = Chunks.F32(d, o + 0xC), DirY = Chunks.F32(d, o + 0x10),
                        Elevation = Chunks.F32(d, o + 0x14),
                        MinX = Chunks.F32(d, o + 0x20), MinY = Chunks.F32(d, o + 0x24), MaxX = Chunks.F32(d, o + 0x28), MaxY = Chunks.F32(d, o + 0x2C),
                        Xs = new float[n], Ys = new float[n],
                    };
                    for (int i = 0; i < 4; i++) z.Data[i] = Chunks.U32(d, o + 0x30 + i * 4);
                    for (int i = 0; i < n; i++) { z.Xs[i] = Chunks.F32(d, o + 0x44 + i * 8); z.Ys[i] = Chunks.F32(d, o + 0x48 + i * 8); }
                    all.Add(z);
                    if (size <= 0) break;
                    o += size;
                }
            }
            return all;
        }

        public static List<TrackPathZone> Load(string gameRoot) =>
            Parse(GameFiles.ReadBundle(Path.Combine(gameRoot, "TRACKS", "L2RA.BUN")));
    }
}
