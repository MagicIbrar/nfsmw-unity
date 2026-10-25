using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// The audio half of the world effects (Attrib classes `effects` 0xEBCEE74C, `audioimpact` 0xFBFFB107, `audioscrape`
    /// 0x11B47832, `aud_moment_strm` 0xD2410816), as WorldConn.cpp (World_OneShotEffect, WorldEffectConn::Update,
    /// ChooseAudioAttributes) and SoundCollision.cpp (Sound::CollisionEvent) read them.
    /// </summary>
    public sealed class CollisionAudioData
    {
        public const uint EffectsClass = 0xEBCEE74C, ImpactClass = 0xFBFFB107, ScrapeClass = 0x11B47832, MomentClass = 0xD2410816;

        /// <summary>SoundCollision.hpp SD_* description bits.</summary>
        public const int SdPrimary = 1, SdSmokable = 2, SdWall = 4, SdTwoCar = 8, SdCar = 0x10, SdRollover = 0x20, SdBottomOut = 0x40,
            SdFront = 0x80, SdSide = 0x100, SdScrape = 0x200, SdEvent = 0x400;

        /// <summary>An `effects` collection's audio fields.</summary>
        public sealed class EffectAudio
        {
            public uint Key;
            public string Name;
            /// <summary>AudioQuadratic (A + Bx + Cx² + Dx³, clamped 0..1); World_OneShotEffect defaults it to (0, 1, 0, 0).</summary>
            public float A, B = 1, C, D;
            public float AudioCullDist;
            /// <summary>AudioFX_DEFAULT/FRONT/REAR/TOP/BOTTOM/SIDE refs: (class, collection) or (0, 0).</summary>
            public (uint cls, uint key) Default, Front, Rear, Top, Bottom, Side;

            /// <summary>SolveEffectQuadratic (WorldConn.cpp:283).</summary>
            public float Intensity(float x)
            {
                float y = A + B * x + C * x * x + D * x * x * x;
                return y < 0 ? 0 : y > 1 ? 1 : y;
            }

            /// <summary>
            /// ChooseAudioAttributes (WorldConn.cpp:159): with the owner's matrix, a normal against its forward axis
            /// (dot &lt; -0.707 front, &gt; 0.707 rear), else against its up axis (&lt; -0.707 top, &gt; 0.707 bottom),
            /// else side; an empty zone ref falls back to AudioFX_DEFAULT. Inputs are dot products already taken.
            /// </summary>
            public (uint cls, uint key) Choose(bool haveMatrix, float dotFront, float dotUp)
            {
                if (haveMatrix)
                {
                    var z = dotFront < -0.707f ? Front : dotFront > 0.707f ? Rear : dotUp < -0.707f ? Top : dotUp > 0.707f ? Bottom : Side;
                    if (z.key != 0 && z.cls != 0) return z;
                }
                return Default;
            }
        }

        /// <summary>An `audioimpact` collection (stitched impact): STITCH_LEVEL_0..3 (STICH_COLLISION_TYPE ids), Volumes
        /// {Vol1..Vol4} (Int16), StreamSweetner {RefSpec aud_moment_strm, u8 Threshold}[], DESCRIPTION bits.</summary>
        public sealed class Impact
        {
            public uint Key;
            public string Name;
            public int Description;
            public readonly int[][] Levels = new int[4][];
            public readonly int[] Volumes = { 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF };
            public (uint moment, int threshold)[] Streams = Array.Empty<(uint, int)>();
        }

        /// <summary>An `audioscrape` collection: CSIS_EFFECT (the Csis class it plays, "FX_Scrape") and DESCRIPTION bits.</summary>
        public sealed class Scrape
        {
            public uint Key;
            public string Name, CsisEffect;
            public int Description;
        }

        /// <summary>An `aud_moment_strm` collection: the NISAudio SoundFX_Select(stream, param) bits, the MAIN obj 6 volume
        /// slot (eVOL_MOMENT), priority, interrupt and positioning flags, and its parent (pursuit_breaker_moments raises
        /// SFXObj_MomentStrm input 5).</summary>
        public sealed class Moment
        {
            public uint Key, Parent;
            public string Name;
            public int Stream, Param, VolSlot, Priority;
            public bool CanInterrupt, IsPositioned;
        }

        public readonly Dictionary<uint, EffectAudio> Effects = new Dictionary<uint, EffectAudio>();
        public readonly Dictionary<uint, Impact> Impacts = new Dictionary<uint, Impact>();
        public readonly Dictionary<uint, Scrape> Scrapes = new Dictionary<uint, Scrape>();
        public readonly Dictionary<uint, Moment> Moments = new Dictionary<uint, Moment>();

        /// <summary>GetCollisionDescription (SoundCollision.cpp): the DESCRIPTION strings' UCrc32 cases.</summary>
        public static int DescriptionBit(string s)
        {
            switch ((s ?? "").ToUpperInvariant())
            {
                case "SMOKABLE": return SdSmokable;
                case "WALL": return SdWall;
                case "TWO_CAR": return SdTwoCar;
                case "CAR": return SdCar;
                case "FRONT": return SdFront;
                case "BOTTOMOUT": return SdBottomOut;
                case "ROLLOVER": return SdRollover;
                case "EVENT": return SdEvent;
                case "SIDE": return SdSide;
                default: return 0;
            }
        }

        static (uint, uint) Ref(AttribCollection c, string f)
        {
            var r = c.Refs(f);
            return r.Length > 0 ? r[0] : (0u, 0u);
        }

        static int Description(AttribCollection c)
        {
            int d = 0;
            if (!c.TryGet("DESCRIPTION", out _, out int n, out _)) return 0;
            for (int i = 0; i < n; i++) d |= DescriptionBit(c.StringKey("DESCRIPTION", i));
            return d;
        }

        public static CollisionAudioData Read(AttribDatabase db)
        {
            var r = new CollisionAudioData();
            if (db == null) return r;
            foreach (var c in db.Collections.Values)
            {
                if (c.ClassKey == EffectsClass)
                {
                    var e = new EffectAudio { Key = c.Key, Name = c.Name };
                    if (c.TryGet("AudioQuadratic", out int at, out _, out _))
                    {
                        var d = c.Source.Data;
                        e.A = Chunks.F32(d, at); e.B = Chunks.F32(d, at + 4); e.C = Chunks.F32(d, at + 8); e.D = Chunks.F32(d, at + 12);
                    }
                    e.AudioCullDist = c.Float("AudioCullDist");
                    e.Default = Ref(c, "AudioFX_DEFAULT"); e.Front = Ref(c, "AudioFX_FRONT"); e.Rear = Ref(c, "AudioFX_REAR");
                    e.Top = Ref(c, "AudioFX_TOP"); e.Bottom = Ref(c, "AudioFX_BOTTOM"); e.Side = Ref(c, "AudioFX_SIDE");
                    r.Effects[c.Key] = e;
                }
                else if (c.ClassKey == ImpactClass)
                {
                    var im = new Impact { Key = c.Key, Name = c.Name, Description = Description(c) };
                    for (int l = 0; l < 4; l++)
                    {
                        string f = "STITCH_LEVEL_" + l;
                        if (c.TryGet(f, out int at, out int n, out _))
                        {
                            im.Levels[l] = new int[n];
                            for (int i = 0; i < n; i++) im.Levels[l][i] = Chunks.I32(c.Source.Data, at + i * 4);
                        }
                        else im.Levels[l] = Array.Empty<int>();
                    }
                    if (c.TryGet("Volumes", out int va, out _, out _))
                        for (int i = 0; i < 4; i++) im.Volumes[i] = Chunks.I16(c.Source.Data, va + i * 2);
                    if (c.TryGet("StreamSweetner", out int sa, out int sn, out var sdef))
                    {
                        int stride = sdef != null && sdef.Size >= 13 ? sdef.Size : 16;
                        im.Streams = new (uint, int)[sn];
                        for (int i = 0; i < sn; i++)
                            im.Streams[i] = (Chunks.U32(c.Source.Data, sa + i * stride + 4), c.Source.Data[sa + i * stride + 12]);
                    }
                    r.Impacts[c.Key] = im;
                }
                else if (c.ClassKey == ScrapeClass)
                    r.Scrapes[c.Key] = new Scrape { Key = c.Key, Name = c.Name, CsisEffect = c.StringKey("CSIS_EFFECT"), Description = Description(c) };
                else if (c.ClassKey == MomentClass)
                {
                    var m = new Moment
                    {
                        Key = c.Key, Name = c.Name, Parent = c.Parent != null ? c.Parent.Key : 0,
                        Stream = c.Int("stream"), Param = c.Int("param"), VolSlot = c.Int("VolSlot"),
                        CanInterrupt = c.Bool("CanInterupt"), IsPositioned = c.Bool("IsPositioned"),
                    };
                    if (c.TryGet("strmpriority", out int pa, out _, out _)) m.Priority = (sbyte)c.Source.Data[pa];
                    r.Moments[c.Key] = m;
                }
            }
            return r;
        }
    }
}
