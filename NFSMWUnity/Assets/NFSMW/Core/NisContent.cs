using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// BasicCharacterAnimEntityInfo (chunk 0x37040 inside the 0x80037020 scene, 0x90 bytes after the 0x11 pad;
    /// CBasicCharacterAnimEntity::Init 0x44C5E0, decomp Animation/AnimEntity_BasicCharacter.cpp). Every entity in the
    /// retail NIS bundles is type 1 (BasicCharacter): the arrest cops and perps (26 Arrest scenes, 64 entities).
    /// </summary>
    public sealed class NisCharacterInfo
    {
        public uint TypeId, InstanceHash, ParentHash, PlayFlags;
        public float[] LocalMatrix = Mat.Identity();   // scene space (game axes), row vectors
        public uint[] LodHashes = new uint[4];          // solid name hashes (bStringHash of e.g. "MCOP01")
        public uint TransAnim, QuatsAnim, ScaleAnim, SkelAnim, SkelName;
        public float PlaySpeed, PlayDelay;
        public uint WorldObjectType, LoopStart, LoopEnd;

        internal static NisCharacterInfo Read(byte[] d, int o) => new NisCharacterInfo
        {
            TypeId = Chunks.U32(d, o), InstanceHash = Chunks.U32(d, o + 4), ParentHash = Chunks.U32(d, o + 8), PlayFlags = Chunks.U32(d, o + 0xC),
            LocalMatrix = Mat.Read(d, o + 0x10),
            LodHashes = new[] { Chunks.U32(d, o + 0x50), Chunks.U32(d, o + 0x54), Chunks.U32(d, o + 0x58), Chunks.U32(d, o + 0x5C) },
            TransAnim = Chunks.U32(d, o + 0x60), QuatsAnim = Chunks.U32(d, o + 0x64), ScaleAnim = Chunks.U32(d, o + 0x68),
            SkelAnim = Chunks.U32(d, o + 0x6C), SkelName = Chunks.U32(d, o + 0x70),
            PlaySpeed = Chunks.F32(d, o + 0x74), PlayDelay = Chunks.F32(d, o + 0x78), WorldObjectType = Chunks.U32(d, o + 0x7C),
            LoopStart = Chunks.U32(d, o + 0x80), LoopEnd = Chunks.U32(d, o + 0x84),
        };

        /// <summary>
        /// CBasicCharacterAnimEntity::Init: the CAnimCtrl flags from mPlayFlags (0x40 -> 0x60 loop range, 0x20 -> loop,
        /// 0x10 ping-pong else 8 linear, 0x80/0x100/0x200/0x400 copied), plus 1 when skinned (a skeleton name is given).
        /// </summary>
        public uint CtrlFlags
        {
            get
            {
                uint f = 0, p = PlayFlags;
                if ((p & 0x40) != 0) f |= 0x60; else if ((p & 0x20) != 0) f |= 0x20;
                f |= (p & 0x10) != 0 ? 0x10u : 8u;
                f |= p & 0x780;
                return f == 0 ? (SkelName != 0 ? 0x29u : 0x2Au) : f | (SkelName != 0 ? 1u : 2u);
            }
        }

        /// <summary>CAnimCtrl time scale: mPlaySpeed (0 -> 1), halved for skeletal animation (the banks are 15 fps).</summary>
        public float TimeScale => (PlaySpeed == 0 ? 1f : PlaySpeed) * (SkelName != 0 && SkelAnim != 0 ? 0.5f : 1f);
    }

    /// <summary>One material of a skinned solid; vertices carry three weights and absolute bone indices.</summary>
    public sealed class SkinnedPart
    {
        public string MaterialName = "";
        public int EffectId;
        public uint Flags, TextureHash;
        /// <summary>eLightMaterial name hash (0 = DEFAULT).</summary>
        public uint LightMaterialHash;
        public byte[] Palette = new byte[16];
        public float[] Positions, Normals, Uvs, Weights;   // xyz, xyz, uv, w0 w1 w2 (game space, bind pose)
        public int[] Bones;                                 // 3 per vertex, skeleton bone indices
        public int[] Indices;
        public int VertexCount => Positions.Length / 3;
    }

    /// <summary>
    /// A character solid (0x80134000 list in the NIS bundle; same container as SolidList). Its materials use effect 2,
    /// worldbone.efx (embedded at speed.exe file offset 0x5310C0; vs_1_1 disassembled): 60-byte vertices
    /// {pos f32x3, normal f32x3, colour u32, uv f32x2, blend weights f32x3, blend indices f32x3}. A blend index picks
    /// one of the material's 16 palette bones (u8 at material +0x1E..+0x2D); the shader reads BlendMatrices[index * 4]
    /// and blends index order y, x, z with weights y, x, z.
    /// </summary>
    public sealed class SkinnedSolid
    {
        public string Name = "";
        public uint Hash;
        public uint[] TextureHashes = Array.Empty<uint>();
        /// <summary>Chunk 0x134013: the solid's light material name hashes (8 bytes each), indexed by material byte +0x1D.</summary>
        public uint[] LightMaterialHashes = Array.Empty<uint>();
        public readonly List<SkinnedPart> Parts = new List<SkinnedPart>();

        public static List<SkinnedSolid> ReadAll(byte[] d, int start, int end)
        {
            var list = new List<SkinnedSolid>();
            foreach (var c in Chunks.FindAll(d, start, end, SolidList.Id))
                foreach (var s in Chunks.Children(d, c))
                    if (s.Id == 0x80134010)
                    {
                        var solid = Read(d, s);
                        if (solid != null) list.Add(solid);
                    }
            return list;
        }

        static SkinnedSolid Read(byte[] d, Chunk chunk)
        {
            var solid = new SkinnedSolid();
            Chunk mesh = default;
            bool hasMesh = false;
            foreach (var c in Chunks.Children(d, chunk))
            {
                if (c.Id == 0x134011)
                {
                    int h = Chunks.Align(c.DataStart, 16);
                    solid.Hash = Chunks.U32(d, h + 0x10);
                    solid.Name = Chunks.CString(d, h + 0xA0, c.End - (h + 0xA0));
                }
                else if (c.Id == 0x134013)
                {
                    solid.LightMaterialHashes = new uint[c.Size / 8];
                    for (int i = 0; i < solid.LightMaterialHashes.Length; i++) solid.LightMaterialHashes[i] = Chunks.U32(d, c.DataStart + i * 8);
                }
                else if (c.Id == 0x134012)
                {
                    solid.TextureHashes = new uint[c.Size / 8];
                    for (int i = 0; i < solid.TextureHashes.Length; i++) solid.TextureHashes[i] = Chunks.U32(d, c.DataStart + i * 8);
                }
                else if (c.Id == 0x80134100) { mesh = c; hasMesh = true; }
            }
            if (!hasMesh || solid.Name.Length == 0) return null;

            int matOff = -1, matCount = 0, idxOff = -1, idxCount = 0;
            var buffers = new List<(int off, int size)>();
            var names = new List<string>();
            foreach (var c in Chunks.Children(d, mesh))
            {
                switch (c.Id)
                {
                    case 0x134900:
                    {
                        int h = Chunks.Align(c.DataStart, 16);
                        matCount = Chunks.I32(d, h + 0x10);
                        idxCount = Chunks.I32(d, h + 0x2C);
                        break;
                    }
                    case 0x134B02: matOff = Chunks.Align(c.DataStart, 16); break;
                    case 0x134B03: idxOff = Chunks.Align(c.DataStart, 16); break;
                    case 0x134B01:
                    {
                        int a = Chunks.Align(c.DataStart, 0x80);
                        if (c.End - a > 0) buffers.Add((a, c.End - a));
                        break;
                    }
                    case 0x134C02: names.Add(Chunks.CString(d, c.DataStart, c.Size)); break;
                }
            }
            if (matOff < 0 || idxOff < 0 || matCount <= 0) return solid;
            // buffer per run of materials with one effect (as SolidList / the mesh loader 0x6E3EE0)
            var matBuffer = new int[matCount];
            int cursor = 0;
            for (int m = 0; m < matCount; m++) matBuffer[m] = -1;
            for (int b = 0; b < buffers.Count && cursor < matCount; b++)
            {
                matBuffer[cursor++] = b;
                while (cursor < matCount && Chunks.I32(d, matOff + cursor * 0x68 + 0x30) == Chunks.I32(d, matOff + (cursor - 1) * 0x68 + 0x30))
                    matBuffer[cursor++] = b;
            }
            for (int m = 0; m < matCount; m++)
            {
                int b = matBuffer[m];
                if (b < 0) continue;
                int e = matOff + m * 0x68;
                int first = Chunks.I32(d, e + 0x44), count = Chunks.I32(d, e + 0x5C);
                if (count <= 0 || first + count > idxCount) continue;
                var part = new SkinnedPart
                {
                    MaterialName = m < names.Count ? names[m] : "",
                    EffectId = Chunks.I32(d, e + 0x30),
                    Flags = Chunks.U32(d, e + 0x38),
                };
                int slot = d[e + 0x18];
                if (slot < solid.TextureHashes.Length) part.TextureHash = solid.TextureHashes[slot];
                int lm = d[e + 0x1D];   // 0xFF = the DEFAULT light material
                if (lm < solid.LightMaterialHashes.Length) part.LightMaterialHash = solid.LightMaterialHashes[lm];
                Buffer.BlockCopy(d, e + 0x1E, part.Palette, 0, 16);
                bool skinned = part.EffectId == 2;
                int stride = skinned ? 60 : 36;   // [?] non-skinned parts: the 36-byte world layout bound to bone 0
                var remap = new Dictionary<int, int>();
                var order = new List<int>();
                part.Indices = new int[count - count % 3];
                for (int i = 0; i < part.Indices.Length; i++)
                {
                    int v = Chunks.U16(d, idxOff + (first + i) * 2);
                    if (!remap.TryGetValue(v, out int nv)) { nv = order.Count; remap[v] = nv; order.Add(v); }
                    part.Indices[i] = nv;
                }
                var (vb, vbSize) = buffers[b];
                int n = order.Count;
                part.Positions = new float[n * 3];
                part.Normals = new float[n * 3];
                part.Uvs = new float[n * 2];
                part.Weights = new float[n * 3];
                part.Bones = new int[n * 3];
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    int o = vb + order[i] * stride;
                    if (o + stride > vb + vbSize) { ok = false; break; }
                    for (int k = 0; k < 3; k++)
                    {
                        part.Positions[i * 3 + k] = Chunks.F32(d, o + k * 4);
                        part.Normals[i * 3 + k] = Chunks.F32(d, o + 12 + k * 4);
                    }
                    part.Uvs[i * 2] = Chunks.F32(d, o + 28);
                    part.Uvs[i * 2 + 1] = Chunks.F32(d, o + 32);
                    if (skinned)
                        for (int k = 0; k < 3; k++)
                        {
                            part.Weights[i * 3 + k] = Chunks.F32(d, o + 36 + k * 4);
                            int pi = (int)Chunks.F32(d, o + 48 + k * 4);   // worldbone.efx: frc/add truncates the index
                            part.Bones[i * 3 + k] = pi >= 0 && pi < 16 ? part.Palette[pi] : 0;
                        }
                    else part.Weights[i * 3] = 1;
                }
                if (ok) solid.Parts.Add(part);
            }
            return solid;
        }
    }

    /// <summary>
    /// Everything a NIS bundle (NIS\Scene_&lt;name&gt;_BundleB.bun) carries beyond the scene header: texture packs
    /// (0xB3300000), skeletons (0xE34009), character solids (0x80134000), animation banks (0xE34010, characters and the
    /// "ZPM" car/cop tracks), the event sequencer pack (0x3B811) and the character entities (0x37040). Read on demand,
    /// cached per scene. CAnimPlayer::Load (0x44CC90) streams the same bundle.
    /// </summary>
    public sealed class NisContent
    {
        public NisScene Scene;
        public readonly List<NisCharacterInfo> Characters = new List<NisCharacterInfo>();
        public readonly List<EaglSkeleton> Skeletons = new List<EaglSkeleton>();
        public readonly Dictionary<uint, EaglAnim> Anims = new Dictionary<uint, EaglAnim>();
        public readonly Dictionary<uint, SkinnedSolid> Solids = new Dictionary<uint, SkinnedSolid>();
        public readonly List<GameTexture> Textures = new List<GameTexture>();
        public EventSeqPack Events;

        static readonly Dictionary<string, NisContent> _cache = new Dictionary<string, NisContent>(StringComparer.OrdinalIgnoreCase);

        public static NisContent For(NisScene scene)
        {
            if (scene == null || string.IsNullOrEmpty(scene.File)) return null;
            lock (_cache)
            {
                if (_cache.TryGetValue(scene.File, out var c)) return c;
                try { c = Load(System.IO.File.ReadAllBytes(scene.File)); }
                catch (Exception) { c = new NisContent(); }
                c.Scene = scene;
                _cache[scene.File] = c;
                return c;
            }
        }

        public static NisContent Load(byte[] d)
        {
            var c = new NisContent();
            foreach (var ch in Chunks.Read(d, 0, d.Length))
            {
                switch (ch.Id)
                {
                    case 0xB3300000: TexturePack.Read(d, ch, c.Textures); break;
                    case 0xE34009: c.Skeletons.AddRange(EaglSkeleton.Read(d, ch.DataStart, ch.End)); break;
                    case 0xE34010:
                        foreach (var a in EaglAnimBankReader.Read(d, ch.DataStart, ch.End)) c.Anims[a.NameHash] = a;
                        break;
                    case SolidList.Id:
                        foreach (var s in SkinnedSolid.ReadAll(d, ch.Start, ch.End)) c.Solids[s.Hash] = s;
                        break;
                    case 0x3B811: c.Events = EventSeqPack.Read(d, ch.DataStart, ch.End); break;
                    case 0x80037020:
                        foreach (var e in Chunks.Children(d, ch))
                        {
                            if (e.Id != 0x37040) continue;
                            int o = e.DataStart;
                            while (o + 4 <= e.End && Chunks.U32(d, o) == 0x11111111) o += 4;
                            if (o + 0x90 <= e.End) c.Characters.Add(NisCharacterInfo.Read(d, o));
                        }
                        break;
                }
            }
            return c;
        }

        public EaglSkeleton Skeleton(uint nameHash)
        {
            foreach (var s in Skeletons) if (s.NameHash == nameHash) return s;
            return null;
        }

        public EaglAnim Anim(uint nameHash) => nameHash != 0 && Anims.TryGetValue(nameHash, out var a) ? a : null;

        /// <summary>WorldModel LOD A (mLODNameHash[0]), else the first LOD the bundle has.</summary>
        public SkinnedSolid Solid(NisCharacterInfo ch)
        {
            foreach (var h in ch.LodHashes)
                if (h != 0 && Solids.TryGetValue(h, out var s)) return s;
            return null;
        }
    }
    /// <summary>
    /// CAnimCandidateData (GLOBAL\InGameB.bun 0x80037050 → 0x37070, LoadSceneCandidateData; decomp Animation/
    /// AnimCandidates.hpp): race start/end candidates with their camera track, the moment-scene marker types
    /// (GetSceneMomentMarkerType → GetClosestMarker 0x4491E0, used by NISActivity::Load 0x6F51F0) and the special car
    /// lists (PrepareVehicles 0x6F2F60: ONE_COP, LOTS_OF_COPS, TRAFFIC_MIX1..9, ONE_SPORT_COP).
    /// </summary>
    public sealed class NisCandidateData
    {
        /// <summary>CRaceStartAnimCandidate (0xC): scene, camera track, flags, min/max cars, car config.</summary>
        public struct RaceStart { public uint Scene; public int CameraTrack, Flags, MinCars, MaxCars, CarConfig; }
        /// <summary>CRaceEndAnimCandidate (8): scene, camera track (+ flags).</summary>
        public struct RaceEnd { public uint Scene; public int CameraTrack, Flags; }

        public readonly List<RaceStart> RaceStarts = new List<RaceStart>();
        public readonly List<RaceEnd> RaceEnds = new List<RaceEnd>();
        /// <summary>CMomentAnim: scene hash → eMomentMarkerTypes (MarkerNames index).</summary>
        public readonly Dictionary<uint, int> MomentType = new Dictionary<uint, int>();
        /// <summary>CSpecialCarListAnimCandidate: scene hash → SpecialCarList (0 ONE_COP .. 11 ONE_SPORT_COP).</summary>
        public readonly Dictionary<uint, int> SpecialCarList = new Dictionary<uint, int>();

        /// <summary>MarkerNameList (0x8930B0): the TrackPositionMarker name of each moment marker type.</summary>
        public static readonly string[] MarkerNames =
        {
            "CameraCutMoment", "GasStationMoment", "FirehallMoment", "PoliceStationMoment", "WaterTowerMoment",
            "PorchCollapseMoment", "SignageCollapseMoment", "ScaffoldingCollapseMoment", "GazeboCollapseMoment",
            "FishMarketMoment", "BoatKnockdownMoment", "CopHitsObstructionMoment", "BustedMomentMarker", "TollboothMarker",
            "GarageMarker", "PoliceStationGroundMoment", "DriveInMoment", "TrailerParkMoment", "TorusRoofMoment",
            "AmphitheatreMoment", "StripMallMoment",
        };

        static NisCandidateData _loaded;
        static readonly object LoadLock = new object();

        /// <summary>The candidate data of GLOBAL\InGameB.bun (empty when it is missing).</summary>
        public static NisCandidateData Load(string gameRoot)
        {
            lock (LoadLock)
            {
                if (_loaded != null) return _loaded;
                var c = new NisCandidateData();
                try
                {
                    var path = System.IO.Path.Combine(gameRoot, "GLOBAL", "InGameB.bun");
                    if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine(gameRoot, "GLOBAL", "InGameB.lzc");
                    if (System.IO.File.Exists(path)) c.Read(GameFiles.ReadBundle(path));
                }
                catch (Exception) { }
                return _loaded = c;
            }
        }

        public void Read(byte[] d)
        {
            foreach (var top in Chunks.Read(d, 0, d.Length))
            {
                if (top.Id != 0x80037050) continue;
                foreach (var ch in Chunks.Read(d, top.DataStart, top.End))
                {
                    if (ch.Id != 0x37070 || ch.End - ch.DataStart < 0xE9C) continue;
                    int o = ch.DataStart;
                    int nStart = (int)Chunks.U32(d, o), nEnd = (int)Chunks.U32(d, o + 4);
                    int nMoment = (int)Chunks.U32(d, o + 0x14), nSpecial = (int)Chunks.U32(d, o + 0x18);
                    for (int i = 0; i < Math.Min(nStart, 8); i++)
                    {
                        int a = o + 0x1C + i * 0xC;
                        RaceStarts.Add(new RaceStart { Scene = Chunks.U32(d, a), CameraTrack = d[a + 4], Flags = d[a + 5], MinCars = d[a + 6], MaxCars = d[a + 7], CarConfig = d[a + 8] });
                    }
                    for (int i = 0; i < Math.Min(nEnd, 8); i++)
                    {
                        int a = o + 0x7C + i * 8;
                        RaceEnds.Add(new RaceEnd { Scene = Chunks.U32(d, a), CameraTrack = d[a + 4], Flags = d[a + 5] });
                    }
                    for (int i = 0; i < Math.Min(nMoment, 64); i++)
                    {
                        int a = o + 0xC9C + i * 8;
                        MomentType[Chunks.U32(d, a)] = (int)Chunks.U32(d, a + 4);
                    }
                    for (int i = 0; i < Math.Min(nSpecial, 55) && o + 0xE9C + i * 8 + 8 <= ch.End; i++)
                    {
                        int a = o + 0xE9C + i * 8;
                        SpecialCarList[Chunks.U32(d, a)] = (int)Chunks.U32(d, a + 4);
                    }
                }
            }
        }

        /// <summary>GetSceneMomentMarkerType: −1 when the scene has no marker type.</summary>
        public int MarkerType(string scene) => scene != null && MomentType.TryGetValue(EaHash.Hash(scene), out int t) ? t : -1;

        /// <summary>sub_6EA1C0: the scene's special car list, −1 (NONE) when it has none.</summary>
        public int CarList(string scene) => scene != null && SpecialCarList.TryGetValue(EaHash.Hash(scene), out int t) ? t : -1;
    }
    /// <summary>
    /// TrackPositionMarkers (chunk 0x34146, World/TrackPositionMarker.cpp; TRACKS2ra.bun): 0x30-byte records
    /// {bTNode, NameHash +8, Param +0xC, Position +0x10 (render space), bAngle +0x20, TrackNumber +0x24}. The NIS uses
    /// them through CAnimCandidateData::GetClosestMarker (BustedMomentMarker, the world-moment markers, TollboothMarker).
    /// </summary>
    public sealed class TrackMarkers
    {
        public struct Marker { public uint NameHash; public int Param, TrackNumber; public Vec3 Position; public ushort Angle; }

        public readonly List<Marker> All = new List<Marker>();
        static TrackMarkers _loaded;
        static readonly object LoadLock = new object();

        public static TrackMarkers Load(string gameRoot)
        {
            lock (LoadLock)
            {
                if (_loaded != null) return _loaded;
                var m = new TrackMarkers();
                try
                {
                    var path = System.IO.Path.Combine(gameRoot, "TRACKS", "L2RA.BUN");
                    if (System.IO.File.Exists(path))
                    {
                        var d = GameFiles.ReadBundle(path);
                        foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x34146))
                        {
                            int o = c.DataStart;
                            while (o + 4 <= c.End && Chunks.U32(d, o) == 0x11111111) o += 4;   // alignment padding
                            for (; o + 0x30 <= c.End; o += 0x30)
                                m.All.Add(new Marker
                                {
                                    NameHash = Chunks.U32(d, o + 8), Param = Chunks.I32(d, o + 0xC), Position = Vec3.Read(d, o + 0x10),
                                    Angle = Chunks.U16(d, o + 0x20), TrackNumber = Chunks.I32(d, o + 0x24),
                                });
                        }
                    }
                }
                catch (Exception) { }
                return _loaded = m;
            }
        }

        /// <summary>
        /// CAnimCandidateData::GetClosestMarker (0x4491E0): of the markers named <paramref name="name"/>, the one with the
        /// smallest distance + |angle difference| (turns) × AngleBonusValue 25. Position is render space (Z up).
        /// </summary>
        public bool Closest(string name, Vec3 position, ushort angle, out Marker best, out float score)
        {
            uint h = EaHash.Hash(name);
            best = default;
            score = -1f;
            bool found = false;
            foreach (var m in All)
            {
                if (m.NameHash != h) continue;
                ushort diff = (ushort)(m.Angle - angle);
                if (diff > 0x8000) diff = (ushort)(angle - m.Angle);
                float dx = m.Position.X - position.X, dy = m.Position.Y - position.Y, dz = m.Position.Z - position.Z;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) + diff * 1.5258789e-05f * 25f;
                if (!found || dist < score) { found = true; best = m; score = dist; }
            }
            return found;
        }
    }
}
