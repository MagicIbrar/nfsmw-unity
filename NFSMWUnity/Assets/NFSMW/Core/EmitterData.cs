using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>A UMath::Vector4 attribute value.</summary>
    public struct FxVec4
    {
        public float X, Y, Z, W;
        public FxVec4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public float this[int i] => i == 0 ? X : i == 1 ? Y : i == 2 ? Z : W;
        public static FxVec4 Read(byte[] d, int o) => new FxVec4(Chunks.F32(d, o), Chunks.F32(d, o + 4), Chunks.F32(d, o + 8), Chunks.F32(d, o + 12));
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###}, {W:0.###})";
    }

    /// <summary>EffectParticleAnimation (Ecstasy/GenTypes/EffectParticleAnimation.h): an N x N flip-book.</summary>
    public enum FxAnimation { None = 0, Grid2 = 2, Grid4 = 4, Grid8 = 8, Grid16 = 16 }

    /// <summary>EffectParticleConstraint (Ecstasy/GenTypes/EffectParticleConstraint.h): the plane a sprite lies in.</summary>
    public enum FxConstraint { None = 0, YZ = 3, XZ = 5, XY = 6, Camera = 8 }

    /// <summary>
    /// One emitter (attribute class emitterdata 0xB30B18AF, Generated/AttribSys/Classes/emitterdata.h; 205 collections in
    /// GLOBAL\attributes.bin). Every field is in the 0x118-byte layout. Colours are RGBA u32 (r in the top byte), 128 = 1.0
    /// under the modulate-2x particle blend [?]. Size and RelativeAngle (degrees) are four keys, like the colours; the
    /// four keys sit at KeyPositions / 3 of the particle's life and are joined by the cubic through all four
    /// (EmitterDataAttribWrapper::CalculateBases, EmitterSystem.cpp:333, speed.exe 0x4FE5E0: hermite_basis reduces to
    /// U^-1 x P with U rows [u^3 u^2 u 1], bMatrix.cpp:87).
    /// </summary>
    public sealed class EmitterData
    {
        public const uint ClassKey = 0xB30B18AF;

        public uint Key;
        public string Name;
        public FxVec4 VelocityStart, AccelDelta, RelativeAngle, VelocityDelta, Size, KeyPositions, VolumeExtent, AccelStart, VolumeCenter;
        public FxAnimation AnimType;
        public byte AnimFps;
        public bool AnimRandomStartFrame;
        /// <summary>ParticleTextureRecord.mEnum: the texture name hash (eTEG_ParticleTextures, e.g. FX_SMK06_BLEND).</summary>
        public uint TextureHash;
        public uint TextureIndex;
        public float InitialAngleRange, Drag, OnCycle, Life, NumParticlesVariance, NumParticles, OnCycleVariance, OffCycleVariance,
            FarClip, OffCycle, RotationVariance, LifeVariance, MotionInherit, Speed, StartDelay, SpreadAngle, SpeedVariance,
            MotionInheritVariance, Gravity;
        public uint Color1, Color2, Color3, Color4;
        public FxConstraint AxisConstraint;
        public bool MotionLive, RandomRotationDirection, SpreadAsDisc;
        public sbyte AlphaToKillAt;
        public bool NoKillAtAlpha, IsOneShot, StartDelayRandomVariance, EliminateUnnecessaryRandomness;
        /// <summary>XenonEffect: optional fuelcell_effect refs (next-gen sparks/contrails; PC DrawXenonEmitters 0x6C6770).</summary>
        public uint[] XenonEffects = Array.Empty<uint>();

        /// <summary>Cubic coefficients per channel, rows t^3, t^2, t, 1 (colour 0..1 RGBA; extra: x = half size, y = angle in bAngle units).</summary>
        public float[] ColourBasis, ExtraBasis;

        public static EmitterData Read(AttribCollection c)
        {
            var e = new EmitterData { Key = c.Key, Name = c.Name };
            e.VelocityStart = V4(c, "VelocityStart");
            e.AccelDelta = V4(c, "AccelDelta");
            e.RelativeAngle = V4(c, "RelativeAngle");
            e.VelocityDelta = V4(c, "VelocityDelta");
            e.Size = V4(c, "Size");
            e.KeyPositions = V4(c, "KeyPositions");
            e.VolumeExtent = V4(c, "VolumeExtent");
            e.AccelStart = V4(c, "AccelStart");
            e.VolumeCenter = V4(c, "VolumeCenter");
            if (c.TryGet("TextureAnimation", out int at, out _, out _))
            {
                e.AnimType = (FxAnimation)Chunks.I32(c.Source.Data, at);
                e.AnimFps = c.Source.Data[at + 4];
                e.AnimRandomStartFrame = c.Source.Data[at + 5] != 0;
            }
            if (c.TryGet("Texture", out at, out _, out _))
            {
                e.TextureHash = Chunks.U32(c.Source.Data, at);
                e.TextureIndex = Chunks.U32(c.Source.Data, at + 4);
            }
            e.InitialAngleRange = c.Float("InitialAngleRange");
            e.Drag = c.Float("Drag");
            e.Color1 = U(c, "Color1");
            e.Color2 = U(c, "Color2");
            e.Color3 = U(c, "Color3");
            e.Color4 = U(c, "Color4");
            e.OnCycle = c.Float("OnCycle");
            e.Life = c.Float("Life");
            e.NumParticlesVariance = c.Float("NumParticlesVariance");
            e.NumParticles = c.Float("NumParticles");
            e.AxisConstraint = (FxConstraint)c.Int("AxisConstraint");
            e.OnCycleVariance = c.Float("OnCycleVariance");
            e.OffCycleVariance = c.Float("OffCycleVariance");
            e.FarClip = c.Float("FarClip");
            e.OffCycle = c.Float("OffCycle");
            e.RotationVariance = c.Float("RotationVariance");
            e.MotionLive = c.Int("MotionLive") != 0;
            e.LifeVariance = c.Float("LifeVariance");
            e.MotionInherit = c.Float("MotionInherit");
            e.Speed = c.Float("Speed");
            e.StartDelay = c.Float("StartDelay");
            e.SpreadAngle = c.Float("SpreadAngle");
            e.RandomRotationDirection = c.Int("RandomRotationDirection") != 0;
            e.SpeedVariance = c.Float("SpeedVariance");
            e.MotionInheritVariance = c.Float("MotionInheritVariance");
            e.Gravity = c.Float("Gravity");
            e.SpreadAsDisc = c.Int("SpreadAsDisc") != 0;
            e.AlphaToKillAt = c.TryGet("AlphaToKillAt", out at, out _, out _) ? (sbyte)c.Source.Data[at] : (sbyte)0;
            e.NoKillAtAlpha = c.Bool("NoKillAtAlpha");
            e.IsOneShot = c.Bool("IsOneShot");
            e.StartDelayRandomVariance = c.Bool("StartDelayRandomVariance");
            e.EliminateUnnecessaryRandomness = c.Bool("EliminateUnnecessaryRandomness");
            if (c.Has("XenonEffect")) e.XenonEffects = c.Keys("XenonEffect");
            e.CalculateBases();
            return e;
        }

        static FxVec4 V4(AttribCollection c, string f) => c.TryGet(f, out int at, out _, out _) ? FxVec4.Read(c.Source.Data, at) : default;
        static uint U(AttribCollection c, string f) => c.TryGet(f, out int at, out _, out _) ? Chunks.U32(c.Source.Data, at) : 0;

        /// <summary>GetFloatColor (EmitterSystem.cpp:321): r = top byte.</summary>
        public static FxVec4 FloatColor(uint c) =>
            new FxVec4((c >> 24) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f);

        /// <summary>EmitterDataAttribWrapper::CalculateBases (EmitterSystem.cpp:333).</summary>
        public void CalculateBases()
        {
            float[] keys = { KeyPositions.X / 3f, KeyPositions.Y / 3f, KeyPositions.Z / 3f, KeyPositions.W / 3f };
            var colour = new[] { FloatColor(Color1), FloatColor(Color2), FloatColor(Color3), FloatColor(Color4) };
            // extra_control_matrix: x = Size * 0.5, y = RelativeAngle * 182.04445 (degrees -> bAngle)
            var extra = new FxVec4[4];
            for (int i = 0; i < 4; i++) extra[i] = new FxVec4(Size[i] * 0.5f, RelativeAngle[i] * 182.04445f, 0, 0);
            ColourBasis = HermiteBasis(keys, colour);
            ExtraBasis = HermiteBasis(keys, extra);
        }

        /// <summary>
        /// hermite_basis (bMatrix.cpp:87): Mf x iMf is the identity, so the basis is U^-1 x P, the cubic through the four
        /// keys. Coincident keys make U singular; fInvertMatrix's result for that case is not known, so the curve then
        /// falls back to linear segments between the keys [?].
        /// </summary>
        public static float[] HermiteBasis(float[] u, FxVec4[] p)
        {
            var m = new double[4, 8];
            for (int r = 0; r < 4; r++)
            {
                m[r, 0] = (double)u[r] * u[r] * u[r];
                m[r, 1] = (double)u[r] * u[r];
                m[r, 2] = u[r];
                m[r, 3] = 1;
                for (int c = 0; c < 4; c++) m[r, 4 + c] = r == c ? 1 : 0;
            }
            for (int col = 0; col < 4; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 4; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                if (Math.Abs(m[piv, col]) < 1e-9) return null;
                if (piv != col) for (int k = 0; k < 8; k++) { var t = m[col, k]; m[col, k] = m[piv, k]; m[piv, k] = t; }
                double inv = 1.0 / m[col, col];
                for (int k = 0; k < 8; k++) m[col, k] *= inv;
                for (int r = 0; r < 4; r++)
                {
                    if (r == col || m[r, col] == 0) continue;
                    double f = m[r, col];
                    for (int k = 0; k < 8; k++) m[r, k] -= f * m[col, k];
                }
            }
            var b = new float[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += m[r, 4 + k] * p[k][c];
                    b[r * 4 + c] = (float)s;
                }
            return b;
        }

        /// <summary>hermite_parameter (bMatrix.cpp:152): [t^3 t^2 t 1] x basis.</summary>
        public static FxVec4 Evaluate(float[] b, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return new FxVec4(
                t3 * b[0] + t2 * b[4] + t * b[8] + b[12],
                t3 * b[1] + t2 * b[5] + t * b[9] + b[13],
                t3 * b[2] + t2 * b[6] + t * b[10] + b[14],
                t3 * b[3] + t2 * b[7] + t * b[11] + b[15]);
        }

        /// <summary>The colour (0..1) at life fraction t.</summary>
        public FxVec4 ColourAt(float t) => ColourBasis != null ? Evaluate(ColourBasis, t) :
            Linear(t, FloatColor(Color1), FloatColor(Color2), FloatColor(Color3), FloatColor(Color4));

        /// <summary>x = half size (m), y = relative angle (bAngle units) at life fraction t.</summary>
        public FxVec4 ExtraAt(float t)
        {
            if (ExtraBasis != null) return Evaluate(ExtraBasis, t);
            var e = new FxVec4[4];
            for (int i = 0; i < 4; i++) e[i] = new FxVec4(Size[i] * 0.5f, RelativeAngle[i] * 182.04445f, 0, 0);
            return Linear(t, e[0], e[1], e[2], e[3]);
        }

        FxVec4 Linear(float t, FxVec4 a, FxVec4 b, FxVec4 c, FxVec4 d)
        {
            float[] k = { KeyPositions.X / 3f, KeyPositions.Y / 3f, KeyPositions.Z / 3f, KeyPositions.W / 3f };
            var p = new[] { a, b, c, d };
            if (t <= k[0]) return a;
            for (int i = 0; i < 3; i++)
            {
                if (t > k[i + 1]) continue;
                float span = k[i + 1] - k[i];
                float f = span > 1e-6f ? (t - k[i]) / span : 1f;
                return new FxVec4(p[i].X + (p[i + 1].X - p[i].X) * f, p[i].Y + (p[i + 1].Y - p[i].Y) * f,
                    p[i].Z + (p[i + 1].Z - p[i].Z) * f, p[i].W + (p[i + 1].W - p[i].W) * f);
            }
            return d;
        }

        public override string ToString() => Name ?? Key.ToString("X8");
    }

    /// <summary>
    /// An emitter group (attribute class emittergroup 0xABA86E60, emittergroup.h): up to six emitterdata refs, an optional
    /// intensity window per emitter (used only when there is one per emitter; an empty window means 0..1,
    /// EmitterGroup::SetEmitters EmitterSystem.cpp:560) and the far clip.
    /// </summary>
    public sealed class EmitterGroupData
    {
        public const uint ClassKey = 0xABA86E60;
        public uint Key;
        public string Name;
        public float FarClip;
        public EmitterData[] Emitters = Array.Empty<EmitterData>();
        public (float min, float max)[] Intensity = Array.Empty<(float, float)>();
        public override string ToString() => Name ?? Key.ToString("X8");
    }

    /// <summary>
    /// A world effect (attribute class effects 0xEBCEE74C, effects.h): the emittergroup it fires, the intensity
    /// quadratic (A + Bx + Cx^2 + Dx^3 clamped to 0..1, default x), the share of the owner's velocity the particles
    /// inherit and the cull distances (WorldConn.cpp:400-470, World_OneShotEffect).
    /// </summary>
    public sealed class EffectData
    {
        public const uint ClassKey = 0xEBCEE74C;
        public uint Key;
        public string Name;
        public uint GroupKey;
        public FxVec4 EmitterQuadratic = new FxVec4(0, 1, 0, 0);
        public float InheritVelocity, VisualCullDist, AudioCullDist;

        /// <summary>SolveEffectQuadratic (WorldConn.cpp:283).</summary>
        public float Intensity(float x)
        {
            var v = EmitterQuadratic;
            float y = v.X + v.Y * x + v.Z * x * x + v.W * x * x * x;
            return y < 0 ? 0 : y > 1 ? 1 : y;
        }
    }

    /// <summary>TireEffectRecord (MWAttribUserTypes.h:131): emittergroup ref (0xC RefSpec) and the Ramp window.</summary>
    public struct TireEffect
    {
        public uint GroupKey;
        public float MinSpeed, MaxSpeed;
    }

    /// <summary>EffectLinkageRecord (MWAttribUserTypes.h:41): surface ref, effects ref, min and max speed (0x20 bytes).</summary>
    public struct EffectLink
    {
        public uint SurfaceKey, EffectKey;
        public float MinSpeed, MaxSpeed;
    }

    /// <summary>A simsurface (0xFB111FEF, simsurface.h) reduced to its tyre effects: [0] dry, [1] rain, [2] flat tyre.</summary>
    public sealed class SurfaceFx
    {
        public const uint ClassKey = 0xFB111FEF;
        public uint Key, ParentKey;
        public string Name;
        public TireEffect[] Slip = Array.Empty<TireEffect>(), Slide = Array.Empty<TireEffect>(), Drive = Array.Empty<TireEffect>();
    }

    /// <summary>
    /// The visual effects of one car: ecar (0xA5B543B7, ecar.h) SlipFX/SkidFX (front, rear), TireSkidWidth[4] and the
    /// pipe/engine effect groups; pvehicle (0x4A97EC8F, pvehicle.h) collision effect links (Physics/Behaviors/Effects.cpp).
    /// </summary>
    public sealed class CarFx
    {
        public float[] SlipFX = { 1, 1 }, SkidFX = { 1, 1 };
        public float[] TireSkidWidth = { 0.25f, 0.25f, 0.25f, 0.25f };
        public uint NosEffect, DamageEffect, DeathEffect, EngineBlownEffect, MissShiftEffect;
        public EffectLink[] OnHitWorld = Array.Empty<EffectLink>(), OnScrapeWorld = Array.Empty<EffectLink>(),
            OnHitObject = Array.Empty<EffectLink>(), OnScrapeObject = Array.Empty<EffectLink>(),
            OnHitGround = Array.Empty<EffectLink>(), OnScrapeGround = Array.Empty<EffectLink>(),
            OnBottomOut = Array.Empty<EffectLink>(), OnBottomScrape = Array.Empty<EffectLink>();
    }

    /// <summary>
    /// WorldFXTrigger (EmitterSystem.h, 0x30 bytes): a proximity trigger that fires its library's group once per entry
    /// (probability out of 0xFFFF; 0xFFFF = always) and re-arms after ResetTime.
    /// </summary>
    public struct WorldFxTrigger
    {
        public float Radius, ResetTime;
        public Vec3 Position;
        public ushort Probability;
    }

    /// <summary>
    /// One EmitterLibrary (EmitterSystem.h, 0x50 bytes): group key, section number, the placement matrix (game space,
    /// rows = local axes then position), followed by its triggers.
    /// </summary>
    public sealed class EmitterPlacement
    {
        public uint GroupKey;
        public int Section;
        public float[] Matrix = new float[16];
        public WorldFxTrigger[] Triggers = Array.Empty<WorldFxTrigger>();
        public Vec3 Position => new Vec3(Matrix[12], Matrix[13], Matrix[14]);
    }

    /// <summary>
    /// Readers for the emitter attribute classes and the emitter library chunk.
    /// Chunk 0x0003BC00 BCHUNK_SPEED_EMITTER_LIBRARY (SpeedChunks.hpp:337; EmitterSystem::Loader EmitterSystem.cpp:1374):
    /// 16-byte aligned EmitterLibraryHeader {EndianSwapped, Version, NumEmitterLibraries, SectionNumber}, then per
    /// library 0x50 bytes {GroupKey, pad, u16 SectionNumber, u16 NumTriggers, EmitterGroup* (0), pad, bMatrix4} followed
    /// by NumTriggers x 0x30 WorldFXTrigger {next, prev, lib*, radius, bVector3 pos + w, reset time, u16 probability,
    /// u8 state, u8 flags, last end, pad}. Libraries with section 0xA28 (and no triggers) start at load and always pass
    /// the distance test (creation flag 0x20000); other sections start within FarClip + 20 m and 0.55 cos of the view
    /// (ServiceWorldEffects, EmitterSystem.cpp:216); section 0 is a placement looked up by key only.
    /// </summary>
    public static class EmitterReader
    {
        public const uint LibraryChunk = 0x0003BC00;
        public const int AlwaysOnSection = 0xA28;

        public static List<EmitterPlacement> ReadLibrary(byte[] d, Chunk c)
        {
            var r = new List<EmitterPlacement>();
            int o = Chunks.Align(c.DataStart, 16);
            // padding before the aligned header is zero-filled; skip forward if the data start is padded with 0x11111111
            while (o + 16 <= c.End && Chunks.U32(d, o) == 0x11111111) o += 4;
            if (o + 16 > c.End) return r;
            int n = Chunks.I32(d, o + 8);
            int p = o + 16;
            for (int i = 0; i < n && p + 0x50 <= c.End; i++)
            {
                var e = new EmitterPlacement
                {
                    GroupKey = Chunks.U32(d, p),
                    Section = Chunks.U16(d, p + 8),
                };
                int nt = Chunks.U16(d, p + 10);
                for (int k = 0; k < 16; k++) e.Matrix[k] = Chunks.F32(d, p + 0x10 + k * 4);
                p += 0x50;
                if (nt > 0 && p + nt * 0x30 <= c.End)
                {
                    e.Triggers = new WorldFxTrigger[nt];
                    for (int t = 0; t < nt; t++, p += 0x30)
                        e.Triggers[t] = new WorldFxTrigger
                        {
                            Radius = Chunks.F32(d, p + 0x0C),
                            Position = Vec3.Read(d, p + 0x10),
                            ResetTime = Chunks.F32(d, p + 0x20),
                            Probability = Chunks.U16(d, p + 0x24),
                        };
                }
                r.Add(e);
            }
            return r;
        }

        /// <summary>
        /// The emitter placements of every streamed section of a track (TRACKS\STREAM&lt;track&gt;.BUN; L2RA has 1,535 in 290
        /// sections, 42 with triggers). The library chunk is a top-level chunk of the section, so only the chunk headers
        /// are walked and the library chunks read.
        /// </summary>
        public static List<EmitterPlacement> ScanTrack(string gameRoot, string track = "L2RA")
        {
            var r = new List<EmitterPlacement>();
            using var ts = new TrackStream(gameRoot, track);
            using var f = System.IO.File.OpenRead(System.IO.Path.Combine(gameRoot, "TRACKS", "STREAM" + track + ".BUN"));
            var head = new byte[8];
            foreach (var s in ts.Sections)
            {
                long o = s.Offset, end = (long)s.Offset + s.Size;
                while (o + 8 <= end)
                {
                    f.Position = o;
                    if (f.Read(head, 0, 8) < 8) break;
                    uint id = BitConverter.ToUInt32(head, 0);
                    int size = BitConverter.ToInt32(head, 4);
                    if (size < 0 || o + 8 + size > end) break;
                    if (id == LibraryChunk && size > 0)
                    {
                        var d = new byte[8 + size];
                        Buffer.BlockCopy(head, 0, d, 0, 8);
                        f.Position = o + 8;
                        int got = 0;
                        while (got < size) { int n = f.Read(d, 8 + got, size - got); if (n <= 0) break; got += n; }
                        // chunk data is 16-byte aligned against the file, so keep the file offset's alignment
                        int pad = (int)((o - s.Offset) & 15);
                        var aligned = new byte[pad + d.Length];
                        Buffer.BlockCopy(d, 0, aligned, pad, d.Length);
                        r.AddRange(ReadLibrary(aligned, new Chunk(id, pad, size)));
                    }
                    o += 8 + size;
                }
            }
            return r;
        }

        /// <summary>Every emitter library chunk in a bundle (nested chunks included).</summary>
        public static List<EmitterPlacement> ReadAll(byte[] d, int start, int end)
        {
            var r = new List<EmitterPlacement>();
            foreach (var c in Chunks.Read(d, start, end))
            {
                if (c.Id == LibraryChunk) r.AddRange(ReadLibrary(d, c));
                else if (c.IsContainer) r.AddRange(ReadAll(d, c.DataStart, c.End));
            }
            return r;
        }
    }

    /// <summary>The emitter, group, effect, surface and car-effect tables of an attribute database.</summary>
    public sealed class FxDatabase
    {
        public readonly Dictionary<uint, EmitterData> Emitters = new Dictionary<uint, EmitterData>();
        public readonly Dictionary<uint, EmitterGroupData> Groups = new Dictionary<uint, EmitterGroupData>();
        public readonly Dictionary<uint, EffectData> Effects = new Dictionary<uint, EffectData>();
        public readonly Dictionary<uint, SurfaceFx> Surfaces = new Dictionary<uint, SurfaceFx>();
        readonly AttribDatabase _db;

        /// <summary>The null group key (Attrib "null" collection, rejected by TireState::Effect::LazyInit, CarRenderConn.cpp:139).</summary>
        public const uint NullKey = 0xEEC2271A;

        public FxDatabase(AttribDatabase db)
        {
            _db = db;
            foreach (var c in db.Collections.Values)
            {
                if (c.ClassKey == EmitterData.ClassKey) Emitters[c.Key] = EmitterData.Read(c);
            }
            foreach (var c in db.Collections.Values)
            {
                if (c.ClassKey == EmitterGroupData.ClassKey) Groups[c.Key] = ReadGroup(c);
                else if (c.ClassKey == EffectData.ClassKey) Effects[c.Key] = ReadEffect(c);
                else if (c.ClassKey == SurfaceFx.ClassKey) Surfaces[c.Key] = ReadSurface(c);
            }
        }

        EmitterGroupData ReadGroup(AttribCollection c)
        {
            var g = new EmitterGroupData { Key = c.Key, Name = c.Name, FarClip = c.Float("FarClip") };
            var list = new List<EmitterData>();
            foreach (var k in c.Keys("Emitters"))
                if (Emitters.TryGetValue(k, out var e)) list.Add(e);
            g.Emitters = list.ToArray();
            if (c.TryGet("IntensityRanges", out int at, out int n, out _))
            {
                g.Intensity = new (float, float)[n];
                for (int i = 0; i < n; i++) g.Intensity[i] = (Chunks.F32(c.Source.Data, at + i * 8), Chunks.F32(c.Source.Data, at + i * 8 + 4));
            }
            return g;
        }

        EffectData ReadEffect(AttribCollection c)
        {
            var e = new EffectData { Key = c.Key, Name = c.Name };
            var refs = c.Refs("emittergroup");
            if (refs.Length > 0) e.GroupKey = refs[0].key;
            if (c.TryGet("EmitterQuadratic", out int at, out _, out _)) e.EmitterQuadratic = FxVec4.Read(c.Source.Data, at);
            e.InheritVelocity = c.Float("InheritVelocity");
            e.VisualCullDist = c.Float("VisualCullDist");
            e.AudioCullDist = c.Float("AudioCullDist");
            return e;
        }

        SurfaceFx ReadSurface(AttribCollection c) => new SurfaceFx
        {
            Key = c.Key,
            ParentKey = c.ParentKey,
            Name = c.Name,
            Slip = TireEffects(c, "TireSlipEffects"),
            Slide = TireEffects(c, "TireSlideEffects"),
            Drive = TireEffects(c, "TireDriveEffects"),
        };

        static TireEffect[] TireEffects(AttribCollection c, string f)
        {
            if (!c.TryGet(f, out int at, out int n, out _)) return Array.Empty<TireEffect>();
            var r = new TireEffect[n];
            var d = c.Source.Data;
            for (int i = 0; i < n; i++)
            {
                int o = at + i * 0x14;
                r[i] = new TireEffect { GroupKey = Chunks.U32(d, o + 4), MinSpeed = Chunks.F32(d, o + 12), MaxSpeed = Chunks.F32(d, o + 16) };
            }
            return r;
        }

        /// <summary>An EffectLinkageRecord array field (pvehicle On*, smackable OnDetached).</summary>
        public static EffectLink[] Links(AttribCollection c, string f)
        {
            if (c == null || !c.TryGet(f, out int at, out int n, out _)) return Array.Empty<EffectLink>();
            var r = new EffectLink[n];
            var d = c.Source.Data;
            for (int i = 0; i < n; i++)
            {
                int o = at + i * 0x20;
                r[i] = new EffectLink
                {
                    SurfaceKey = Chunks.U32(d, o + 4),
                    EffectKey = Chunks.U32(d, o + 0x10),
                    MinSpeed = Chunks.F32(d, o + 0x18),
                    MaxSpeed = Chunks.F32(d, o + 0x1C),
                };
            }
            return r;
        }

        public EmitterGroupData Group(uint key) => Groups.TryGetValue(key, out var g) ? g : null;
        public EmitterGroupData Group(string name) => Group(AttribDatabase.Hash(name));
        public EffectData Effect(uint key) => Effects.TryGetValue(key, out var e) ? e : null;
        public SurfaceFx Surface(string name) => Surfaces.TryGetValue(AttribDatabase.Hash(name), out var s) ? s : null;

        /// <summary>The car's ecar (collection = model name, ChangeWithDefault) and pvehicle effect attributes.</summary>
        public CarFx Car(string model)
        {
            var fx = new CarFx();
            string name = model?.ToLowerInvariant() ?? "default";
            _db.Collections.TryGetValue((0xA5B543B7, AttribDatabase.Hash(name)), out var ecar);
            if (ecar == null) _db.Collections.TryGetValue((0xA5B543B7, AttribDatabase.Hash("default")), out ecar);
            if (ecar != null)
            {
                var s = ecar.Floats("SlipFX"); if (s.Length >= 2) fx.SlipFX = s;
                s = ecar.Floats("SkidFX"); if (s.Length >= 2) fx.SkidFX = s;
                s = ecar.Floats("TireSkidWidth"); if (s.Length >= 4) fx.TireSkidWidth = s;
                fx.NosEffect = Ref(ecar, "NOSEffect");
                fx.DamageEffect = Ref(ecar, "DamageEffect");
                fx.DeathEffect = Ref(ecar, "DeathEffect");
                fx.EngineBlownEffect = Ref(ecar, "EngineBlownEffect");
                fx.MissShiftEffect = Ref(ecar, "MissShiftEffect");
            }
            _db.Collections.TryGetValue((0x4A97EC8F, AttribDatabase.Hash(name)), out var pv);
            if (pv == null) _db.Collections.TryGetValue((0x4A97EC8F, AttribDatabase.Hash("default")), out pv);
            fx.OnHitWorld = Links(pv, "OnHitWorld");
            fx.OnScrapeWorld = Links(pv, "OnScrapeWorld");
            fx.OnHitObject = Links(pv, "OnHitObject");
            fx.OnScrapeObject = Links(pv, "OnScrapeObject");
            fx.OnHitGround = Links(pv, "OnHitGround");
            fx.OnScrapeGround = Links(pv, "OnScrapeGround");
            fx.OnBottomOut = Links(pv, "OnBottomOut");
            fx.OnBottomScrape = Links(pv, "OnBottomScrape");
            return fx;
        }

        static uint Ref(AttribCollection c, string f)
        {
            var r = c.Refs(f);
            return r.Length > 0 && r[0].key != NullKey ? r[0].key : 0;
        }

        /// <summary>
        /// EffectLookup::Init (Physics/Behaviors/Effects.cpp:34): the "default" record, overridden by the first record
        /// for the surface or its nearest ancestor.
        /// </summary>
        public EffectLink? Lookup(EffectLink[] links, uint surfaceKey)
        {
            EffectLink? best = null;
            uint def = AttribDatabase.Hash("default");
            foreach (var l in links) if (l.SurfaceKey == def) { best = l; break; }
            for (int guard = 0; surfaceKey != 0 && guard < 16; guard++)
            {
                foreach (var l in links) if (l.SurfaceKey == surfaceKey) return l;
                if (!_db.Collections.TryGetValue((SurfaceFx.ClassKey, surfaceKey), out var s)) break;
                surfaceKey = s.ParentKey;
            }
            return best;
        }
    }
}
