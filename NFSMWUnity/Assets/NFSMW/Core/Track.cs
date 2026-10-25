using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>One record of the streaming index (chunk 0x34110 in TRACKS\L2RA.BUN, 0x5C bytes each).</summary>
    public sealed class StreamSection
    {
        public string Name;     // "D46": row letter + number
        public int Number;      // letter index * 100 + number, e.g. D46 = 446
        public long Offset;     // in STREAML2RA.BUN
        public int Size;
        public bool IsShared => Number >= 2400; // X0 (shared solids), Y0 (shared textures), Z0
        public override string ToString() => $"{Name} @{Offset:X} {Size}";
    }

    public sealed class SceneryInfo
    {
        public string Name;
        public uint[] SolidHashes = new uint[4]; // LOD A..D; 0 = none
        public float Radius;
    }

    public sealed class SceneryInstance
    {
        public Vec3 Min, Max, Position;
        public float[] Rotation = new float[9]; // row-major, rows are the local X/Y/Z axes in world space
        public uint Flags;
        public int InfoIndex;
    }

    public sealed class SceneryGroup
    {
        public int SectionNumber;
        public List<SceneryInfo> Infos = new List<SceneryInfo>();
        public List<SceneryInstance> Instances = new List<SceneryInstance>();
    }

    public sealed class TrackSectionData
    {
        public StreamSection Section;
        public List<GameTexture> Textures;
        public List<Solid> Solids;
        public List<SceneryGroup> Scenery;
        public List<SmokeableSpawner> Smokeables = new List<SmokeableSpawner>();
    }

    /// <summary>
    /// A breakable prop (chunk 0x34027; decomp Physics/SmokeableInfo.hpp): 0x40-byte records after an 8-byte pad and a
    /// 16-byte header whose +12 is the count. Orientation and position are in the sim's axes (-game Y, game Z, game X);
    /// Model/Collision/Attributes are stringhash32 (= the attribute Jenkins hash) of the model name and of the
    /// `smackable` collection; OverrideInfo indexes the scenery override table and names the static instance the prop
    /// replaces; ExcludeFlags are view cull flags like a scenery instance's.
    /// </summary>
    public sealed class SmokeableSpawner
    {
        public float QX, QY, QZ, QW;
        public Vec3 SimPosition;
        public uint Model, Collision, Attributes, OverrideInfo, UniqueId, ExcludeFlags;

        public static List<SmokeableSpawner> ReadAll(byte[] d, int start, int end, int depth = 0)
        {
            var list = new List<SmokeableSpawner>();
            foreach (var c in Chunks.Read(d, start, end))
            {
                if ((c.Id & 0x80000000) != 0 && depth < 4) list.AddRange(ReadAll(d, c.DataStart, c.End, depth + 1));
                if (c.Id != 0x34027) continue;
                int o = c.DataStart;
                while (o < c.End && d[o] == 0x11) o++;
                if (o + 16 > c.End) continue;
                int n = (int)Chunks.U32(d, o + 12);
                o += 16;
                for (int i = 0; i < n && o + 0x40 <= c.End; i++, o += 0x40)
                    list.Add(new SmokeableSpawner
                    {
                        QX = Chunks.F32(d, o), QY = Chunks.F32(d, o + 4), QZ = Chunks.F32(d, o + 8), QW = Chunks.F32(d, o + 12),
                        SimPosition = new Vec3(Chunks.F32(d, o + 16), Chunks.F32(d, o + 20), Chunks.F32(d, o + 24)),
                        Model = Chunks.U32(d, o + 32), Collision = Chunks.U32(d, o + 36), Attributes = Chunks.U32(d, o + 40),
                        OverrideInfo = Chunks.U32(d, o + 44), UniqueId = Chunks.U32(d, o + 48), ExcludeFlags = Chunks.U32(d, o + 52),
                    });
            }
            return list;
        }
    }

    /// <summary>
    /// Scenery chunk 0x80034100 (loader @0x7311F0): 0x34101 header (+0x0C section number),
    /// 0x34102 SceneryInfo[0x48] (name[24], 4 solid hashes, 4 runtime pointers, radius),
    /// 0x34103 SceneryInstance[0x40] (16-aligned: bbox min/max, flags, position, 3x3 rotation as s16/8192, info index).
    /// </summary>
    public static class Scenery
    {
        public const uint Id = 0x80034100;

        public static List<SceneryGroup> ReadAll(byte[] d, int start, int end)
        {
            var list = new List<SceneryGroup>();
            foreach (var c in Chunks.FindAll(d, start, end, Id))
            {
                var g = new SceneryGroup();
                foreach (var s in Chunks.Children(d, c))
                {
                    if (s.Id == 0x34101) g.SectionNumber = Chunks.I32(d, s.DataStart + 0x0C);
                    else if (s.Id == 0x34102)
                    {
                        for (int o = s.DataStart; o + 0x48 <= s.End; o += 0x48)
                        {
                            var info = new SceneryInfo { Name = Chunks.CString(d, o, 24), Radius = Chunks.F32(d, o + 0x38) };
                            for (int k = 0; k < 4; k++) info.SolidHashes[k] = Chunks.U32(d, o + 0x18 + k * 4);
                            g.Infos.Add(info);
                        }
                    }
                    else if (s.Id == 0x34103)
                    {
                        for (int o = Chunks.Align(s.DataStart, 16); o + 0x40 <= s.End; o += 0x40)
                        {
                            var inst = new SceneryInstance
                            {
                                Min = Vec3.Read(d, o),
                                Max = Vec3.Read(d, o + 0x0C),
                                Flags = Chunks.U32(d, o + 0x18),
                                Position = Vec3.Read(d, o + 0x20),
                                InfoIndex = Chunks.U16(d, o + 0x3E),
                            };
                            for (int k = 0; k < 9; k++) inst.Rotation[k] = Chunks.I16(d, o + 0x2C + k * 2) / 8192f;
                            g.Instances.Add(inst);
                        }
                    }
                }
                list.Add(g);
            }
            return list;
        }
    }

    /// <summary>The streamed world: TRACKS\L2RA.BUN (index + global data) and TRACKS\STREAML2RA.BUN (sections).</summary>
    public sealed class TrackStream : IDisposable
    {
        public readonly string Root;
        public readonly List<StreamSection> Sections = new List<StreamSection>();
        public readonly byte[] Bundle;
        readonly FileStream _stream;

        public TrackStream(string gameRoot, string track = "L2RA")
        {
            Root = gameRoot;
            Bundle = File.ReadAllBytes(Path.Combine(gameRoot, "TRACKS", track + ".BUN"));
            foreach (var c in Chunks.Read(Bundle, 0, Bundle.Length))
            {
                if (c.Id != 0x34110) continue;
                for (int o = c.DataStart; o + 0x5C <= c.End; o += 0x5C)
                {
                    Sections.Add(new StreamSection
                    {
                        Name = Chunks.CString(Bundle, o, 8),
                        Number = Chunks.I32(Bundle, o + 8),
                        Offset = Chunks.U32(Bundle, o + 0x14),
                        Size = Chunks.I32(Bundle, o + 0x18),
                    });
                }
            }
            _stream = File.OpenRead(Path.Combine(gameRoot, "TRACKS", "STREAM" + track + ".BUN"));
        }

        public StreamSection Find(string name) => Sections.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public byte[] ReadRaw(StreamSection s)
        {
            var buf = new byte[s.Size];
            lock (_stream)
            {
                _stream.Position = s.Offset;
                int got = 0;
                while (got < buf.Length)
                {
                    int n = _stream.Read(buf, got, buf.Length - got);
                    if (n <= 0) throw new EndOfStreamException(s.Name);
                    got += n;
                }
            }
            return buf;
        }

        public TrackSectionData Load(StreamSection s, bool textures = true, bool solids = true, bool scenery = true)
        {
            var d = ReadRaw(s);
            return new TrackSectionData
            {
                Section = s,
                Textures = textures ? TexturePack.ReadAll(d, 0, d.Length) : new List<GameTexture>(),
                Solids = solids ? SolidList.ReadAll(d, 0, d.Length) : new List<Solid>(),
                Scenery = scenery ? Scenery.ReadAll(d, 0, d.Length) : new List<SceneryGroup>(),
                Smokeables = scenery ? SmokeableSpawner.ReadAll(d, 0, d.Length) : new List<SmokeableSpawner>(),
            };
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>A car folder: CARS\NAME\GEOMETRY.BIN (solids) and TEXTURES.BIN (JDLZ texture pack).</summary>
    public sealed class CarAsset
    {
        public string Name;
        public List<Solid> Solids;
        public List<GameTexture> Textures;

        public static CarAsset Load(string gameRoot, string car)
        {
            string dir = Path.Combine(gameRoot, "CARS", car);
            var geo = File.ReadAllBytes(Path.Combine(dir, "GEOMETRY.BIN"));
            string texPath = Path.Combine(dir, "TEXTURES.BIN");
            var tex = File.Exists(texPath) ? File.ReadAllBytes(texPath) : Array.Empty<byte>();
            return new CarAsset
            {
                Name = car,
                Solids = SolidList.ReadAll(geo, 0, geo.Length),
                Textures = TexturePack.ReadAll(tex, 0, tex.Length),
            };
        }
    }

    /// <summary>Reads a bundle that may be stored whole-file JDLZ compressed (.lzc).</summary>
    public static class GameFiles
    {
        public static byte[] ReadBundle(string path)
        {
            var d = File.ReadAllBytes(path);
            return Jdlz.IsJdlz(d, 0) ? Jdlz.Decompress(d, 0) : d;
        }
    }
}

namespace NfsMw.Core
{
    /// <summary>
    /// Named scenery groups (TRACKS\L2RA.BUN). Chunk 0x34108 is the override table, 6 bytes each {u16 scenery section
    /// number (0x34101 +0x0C, not the stream index number), u16 instance index in that section's 0x34103 array, u16
    /// flags}; chunk 0x34109 the groups {u32 0x0B, u32 0x0B, u32 bStringHash(name), u16 group number, u16 count,
    /// u32 flags, u16 override index[count], padded to 4}. gameplay.bin names them per Blacklist bin (BARRIER_LOCKED_n,
    /// the closed-off city areas) and per event (BARRIER_SPLINE_n race-route barriers, a leading '*' = flipped art).
    /// </summary>
    /// <summary>
    /// The visible-section manager (L2RA.BUN chunk 0x80034150): drivable-section boundary polygons (0x34152: 8-byte
    /// node, u16 section, u8 point count, u8 flag, bbox min/max, centre, then the points, world X/Y) and, per drivable
    /// section, the sections drawn from inside it (0x34153: 12-byte node, u16 section, u8 most, u8 max, u16 count,
    /// u16[max] section numbers, 4-aligned). The lists include the far LOD and panorama sections, which are drawn
    /// only from the places that list them.
    /// </summary>
    public sealed class VisibleSections
    {
        public sealed class Boundary
        {
            public int Section;
            public float MinX, MinY, MaxX, MaxY;
            /// <summary>+0x1C Centre; +0xB PanoramaBoundary.</summary>
            public float CentreX, CentreY;
            public bool Panorama;
            public float[] Points;   // x0, y0, x1, y1, ...

            /// <summary>
            /// VisibleSectionBoundary::GetDistanceOutside 0x7247E0: max when outside the box grown by max, 0 inside the
            /// polygon, else the distance to the nearest edge (bDistToLine 0x460000, segment distance) capped at max.
            /// </summary>
            public float DistanceOutside(float x, float y, float max)
            {
                if (x < MinX - max || x > MaxX + max || y < MinY - max || y > MaxY + max) return max;
                if (Contains(x, y)) return 0f;
                float best = max;
                int n = Points.Length / 2;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float ax = Points[i * 2], ay = Points[i * 2 + 1], bx = Points[j * 2], by = Points[j * 2 + 1];
                    float ex = bx - ax, ey = by - ay, l2 = ex * ex + ey * ey;
                    float t = l2 > 0f ? ((x - ax) * ex + (y - ay) * ey) / l2 : 0f;
                    t = t < 0f ? 0f : t > 1f ? 1f : t;
                    float dx = x - (ax + ex * t), dy = y - (ay + ey * t);
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (d < best) best = d;
                }
                return best;
            }

            public bool Contains(float x, float y)
            {
                if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
                bool inside = false;
                int n = Points.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    float xi = Points[i * 2], yi = Points[i * 2 + 1], xj = Points[j * 2], yj = Points[j * 2 + 1];
                    if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
                }
                return inside;
            }
        }

        public readonly List<Boundary> Boundaries = new List<Boundary>();
        public readonly Dictionary<int, int[]> Visible = new Dictionary<int, int[]>();
        /// <summary>The 0x34153 drivable sections in file order (DrivableSectionList).</summary>
        public readonly List<int> DrivableOrder = new List<int>();
        readonly Dictionary<int, Boundary> _bySection = new Dictionary<int, Boundary>();
        /// <summary>ScenerySectionLODOffset (0x34151 +0; 10 when absent).</summary>
        public int LodOffset = 10;

        /// <summary>
        /// A VisibleSectionOverlay (chunk 0x34158: {i32 3, i32 3, char Name[40], i32 NumEntries @0x30, entries of 6 bytes @0x34:
        /// u8 AddRemove (1 add, 0 remove), u8 pad, i16 DrivableSection, i16 Section}). L2RA has FlyBy (500 entries) and
        /// E3Demo (a header with a garbage count: no effect). CAnimScene::Init activates the one named by the NIS scene
        /// header's SeeulatorOverlayName (ActivateOverlay 0x731D80) and DestroyAnimScene undoes it (UnactivateOverlay 0x72DFF0).
        /// </summary>
        public sealed class Overlay
        {
            public string Name;
            public readonly List<(bool add, int drivable, int section)> Entries = new List<(bool, int, int)>();
        }

        public readonly List<Overlay> Overlays = new List<Overlay>();
        readonly List<(bool add, int drivable, int section)> _undo = new List<(bool, int, int)>();
        public string ActiveOverlay { get; private set; }

        /// <summary>
        /// VisibleSectionManager::ActivateOverlay (0x731D80 → 0x72C8E0): per entry, the drivable section's visible list gains
        /// the section if absent (add) or loses it if present (remove), re-sorted ascending; the inverse goes into the
        /// "Undo" overlay. Returns true when a list changed. The exe also disables the preculler [ours: not ported].
        /// </summary>
        public bool ApplyOverlay(string name)
        {
            UndoOverlay();
            if (string.IsNullOrEmpty(name)) return false;
            Overlay ov = null;
            foreach (var o in Overlays) if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) { ov = o; break; }
            if (ov == null) return false;
            ActiveOverlay = ov.Name;
            bool changed = false;
            foreach (var e in ov.Entries) changed |= ApplyEntry(e.add, e.drivable, e.section, _undo);
            return changed;
        }

        /// <summary>UnactivateOverlay (0x72DFF0): applies the undo list.</summary>
        public bool UndoOverlay()
        {
            bool changed = false;
            for (int i = _undo.Count - 1; i >= 0; i--) changed |= ApplyEntry(_undo[i].add, _undo[i].drivable, _undo[i].section, null);
            _undo.Clear();
            ActiveOverlay = null;
            return changed;
        }

        bool ApplyEntry(bool add, int drivable, int section, List<(bool, int, int)> undo)
        {
            if (!Visible.TryGetValue(drivable, out var list)) return false;   // FindDrivableSection(number): no such drivable section
            int at = Array.IndexOf(list, section);
            if (add == (at >= 0)) return false;
            var l = new List<int>(list);
            if (add) l.Add(section); else l.RemoveAt(at);
            l.Sort();
            Visible[drivable] = l.ToArray();
            undo?.Add((!add, drivable, section));
            return true;
        }

        /// <summary>FindBoundary(section_number) (VisibleSection.cpp:637 ff.): the boundary polygon of a section.</summary>
        public Boundary BoundaryOf(int section) => _bySection.TryGetValue(section, out var b) ? b : null;

        bool IsDrivableNumber(int n)
        {
            int letter = n / 100, sub = n % 100;
            return letter >= 1 && letter <= 20 && sub > 0 && sub < LodOffset;
        }

        /// <summary>
        /// VisibleSectionManager::FindDrivableSection 0x72CA00: the first drivable section (file order) whose boundary
        /// holds the point; else the closest drivable boundary (FindClosestBoundary 0x729B20) if under 0.1 m away; else -1.
        /// </summary>
        public int FindDrivableSection(float x, float y)
        {
            foreach (var n in DrivableOrder)
            {
                var b = BoundaryOf(n);
                if (b != null && b.Contains(x, y)) return n;
            }
            Boundary closest = null;
            float best = 9999999f;
            foreach (var b in Boundaries)
            {
                if (!IsDrivableNumber(b.Section)) continue;
                float d = b.DistanceOutside(x, y, best);
                if (closest == null || d < best || (d == best && b.Section < closest.Section)) { best = d; closest = b; }
            }
            if (closest != null && best < 0.1f && Visible.ContainsKey(closest.Section)) return closest.Section;
            return -1;
        }

        public static VisibleSections Read(byte[] d)
        {
            var v = new VisibleSections();
            foreach (var top in Chunks.Read(d, 0, d.Length))
            {
                if (top.Id != 0x80034150) continue;
                foreach (var c in Chunks.Read(d, top.DataStart, top.End))
                {
                    if (c.Id == 0x34152)
                        for (int o = c.DataStart; o + 36 <= c.End;)
                        {
                            int n = d[o + 10];
                            var b = new Boundary
                            {
                                Section = Chunks.U16(d, o + 8),
                                MinX = Chunks.F32(d, o + 12), MinY = Chunks.F32(d, o + 16),
                                MaxX = Chunks.F32(d, o + 20), MaxY = Chunks.F32(d, o + 24),
                                CentreX = Chunks.F32(d, o + 28), CentreY = Chunks.F32(d, o + 32),
                                Panorama = d[o + 11] != 0,
                                Points = new float[n * 2],
                            };
                            for (int i = 0; i < n * 2; i++) b.Points[i] = Chunks.F32(d, o + 36 + i * 4);
                            v.Boundaries.Add(b);
                            if (!v._bySection.ContainsKey(b.Section)) v._bySection[b.Section] = b;
                            o += 36 + 8 * n;
                        }
                    else if (c.Id == 0x34151 && c.Size >= 4) v.LodOffset = Chunks.I32(d, c.DataStart);
                    else if (c.Id == 0x34158 && c.Size >= 0x34)
                    {
                        int o = c.DataStart;
                        var ov = new Overlay { Name = System.Text.Encoding.ASCII.GetString(d, o + 8, 40).Split('\0')[0] };
                        int n = Chunks.I32(d, o + 0x30);
                        if (n < 0 || o + 0x34 + n * 6 > c.End) n = 0;   // E3Demo: a header with a garbage count
                        for (int i = 0; i < n; i++)
                        {
                            int e = o + 0x34 + i * 6;
                            ov.Entries.Add((d[e] != 0, (short)Chunks.U16(d, e + 2), (short)Chunks.U16(d, e + 4)));
                        }
                        v.Overlays.Add(ov);
                    }
                    else if (c.Id == 0x34153)
                        for (int o = c.DataStart; o + 18 <= c.End;)
                        {
                            int sec = Chunks.U16(d, o + 12), max = d[o + 15], num = Chunks.U16(d, o + 16);
                            var list = new int[num];
                            for (int i = 0; i < num; i++) list[i] = Chunks.U16(d, o + 18 + i * 2);
                            v.Visible[sec] = list;
                            v.DrivableOrder.Add(sec);
                            o = (o + 18 + max * 2 + 3) & ~3;
                        }
                }
            }
            return v;
        }

        /// <summary>The drivable section under a world point (game X/Y), or -1.</summary>
        public int SectionAt(float x, float y)
        {
            foreach (var b in Boundaries) if (b.Contains(x, y)) return b.Section;
            return -1;
        }
    }

    public sealed class SceneryGroupTable
    {
        public readonly Dictionary<uint, List<(int section, int instance)>> Groups = new Dictionary<uint, List<(int, int)>>();
        public readonly Dictionary<(int section, int instance), List<uint>> ByInstance = new Dictionary<(int, int), List<uint>>();
        /// <summary>SceneryOverrideInfo flags (u16 at +4): they replace the instance's low 16 flag bits (Scenery.cpp:155).</summary>
        public readonly Dictionary<(int section, int instance), ushort> OverrideFlags = new Dictionary<(int, int), ushort>();
        /// <summary>The override table in order (SceneryOverrideInfo, 6 bytes: section, instance, flags), by index.</summary>
        public readonly List<(int section, int instance)> Overrides = new List<(int, int)>();

        public static SceneryGroupTable Read(byte[] d)
        {
            var t = new SceneryGroupTable();
            int ov = -1, ovEnd = 0, gr = -1, grEnd = 0;
            foreach (var c in Chunks.Read(d, 0, d.Length))
            {
                if (c.Id == 0x34108) { ov = c.DataStart; ovEnd = c.End; }
                else if (c.Id == 0x34109) { gr = c.DataStart; grEnd = c.End; }
            }
            if (ov >= 0)
                for (int o = ov; o + 6 <= ovEnd; o += 6) t.Overrides.Add((Chunks.U16(d, o), Chunks.U16(d, o + 2)));
            if (ov < 0 || gr < 0) return t;
            for (int o = gr; o + 20 <= grEnd;)
            {
                uint hash = Chunks.U32(d, o + 8);
                int n = Chunks.U16(d, o + 14);
                var list = new List<(int, int)>(n);
                for (int i = 0; i < n; i++)
                {
                    int at = ov + Chunks.U16(d, o + 20 + i * 2) * 6;
                    if (at + 6 > ovEnd) continue;
                    var key = (Chunks.U16(d, at), Chunks.U16(d, at + 2));
                    t.OverrideFlags[key] = Chunks.U16(d, at + 4);
                    list.Add(key);
                    if (!t.ByInstance.TryGetValue(key, out var gl)) t.ByInstance[key] = gl = new List<uint>(1);
                    gl.Add(hash);
                }
                t.Groups[hash] = list;
                o += 20 + ((n * 2 + 3) & ~3);
            }
            return t;
        }

        /// <summary>Group hash for a gameplay.bin barrier name ('*' flip marker dropped).</summary>
        public static uint Hash(string name) => EaHash.HashUpper(name.TrimStart('*'));
    }
}

namespace NfsMw.Core
{
    /// <summary>
    /// TrackInfo (World/TrackInfo.cpp, round 9): chunk BCHUNK_TRACK_INFOS 0x34201 in GLOBAL\GlobalB.lzc, 0x120-byte records
    /// (LoaderTrackInfo: table [0x9A3A14], count [0x9A3A18] = size / 0x120). GetTrackInfo (0x722840) returns the first
    /// record whose s16 TrackNumber (+0x8A) equals the number, else null (14 callers: WorldMap::Setup, Minimap::SetupMinimap,
    /// GManager::CalcMapCoordsForMarker, GRaceParameters::GetBoundingBox, TrackLoader / RegionLoader, the race starters).
    /// Layout (TrackInfo.hpp, PC = GC): Name[32], TrackDirectory[32] +0x20, RegionName[8] +0x40, RegionDirectory[32] +0x48,
    /// LocationNumber +0x68, LocationDirectory[16] +0x6C, LocationName +0x7C, DriftType +0x80, IsValid +0x84, Point2Point
    /// +0x85, ReverseVersionExists +0x86, IsPerformanceTuning +0x88, TrackNumber s16 +0x8A, SameAsTrackNumber s16 +0x8C,
    /// SunInfoNameHash +0x90, UsageFlags +0x94, TrackLength +0x98, TimeToBeat fwd/rev +0x9C/+0xA0, ScoreToBeat +0xA4/+0xA8,
    /// TrackMapCalibrationUpperLeft +0xAC, MapWidthMetres +0xB4, Rotation / StartLine / FinishLine bAngles +0xB8..+0xBC,
    /// ZoomFactor +0xC0, difficulties +0xC4/+0xC8, OverrideStartingRouteForAI s16[2][4] +0xCC, shortcut seconds +0xDC,
    /// drift seconds +0xDE/+0xE0, MaxTrafficCars s8[4][2] +0xE4, TrafficAllowedNearStartLine +0xEC, CarRaceStartConfig +0xEE.
    /// (The UI keeps its own calibration reader, Runtime/UI/Minimap.cs; this is the shared record for new readers.)
    /// </summary>
    public sealed class TrackInfo
    {
        public const uint ChunkId = 0x34201;
        public const int RecordSize = 0x120;
        public string Name, TrackDirectory, RegionName, RegionDirectory, LocationDirectory;
        public int LocationNumber, LocationName, DriftType, TrackNumber, SameAsTrackNumber;
        public bool IsValid, Point2Point, ReverseVersionExists, IsPerformanceTuning;
        public uint SunInfoNameHash, UsageFlags, TrackLength;
        public float TimeToBeatForwardsMs, TimeToBeatReverseMs, MapUpperLeftX, MapUpperLeftY, MapWidthMetres, MapZoomFactor;
        public ushort MapRotation, MapStartLineAngle, MapFinishLineAngle;

        static List<TrackInfo> _table;
        static readonly object Lock = new object();

        public static List<TrackInfo> Read(byte[] d)
        {
            var r = new List<TrackInfo>();
            foreach (var c in Chunks.FindAll(d, 0, d.Length, ChunkId))
                for (int o = c.DataStart; o + RecordSize <= c.End; o += RecordSize)
                    r.Add(new TrackInfo
                    {
                        Name = Chunks.CString(d, o, 32), TrackDirectory = Chunks.CString(d, o + 0x20, 32), RegionName = Chunks.CString(d, o + 0x40, 8),
                        RegionDirectory = Chunks.CString(d, o + 0x48, 32), LocationNumber = Chunks.I32(d, o + 0x68),
                        LocationDirectory = Chunks.CString(d, o + 0x6C, 16), LocationName = Chunks.I32(d, o + 0x7C), DriftType = Chunks.I32(d, o + 0x80),
                        IsValid = d[o + 0x84] != 0, Point2Point = d[o + 0x85] != 0, ReverseVersionExists = d[o + 0x86] != 0, IsPerformanceTuning = d[o + 0x88] != 0,
                        TrackNumber = (short)Chunks.U16(d, o + 0x8A), SameAsTrackNumber = (short)Chunks.U16(d, o + 0x8C),
                        SunInfoNameHash = Chunks.U32(d, o + 0x90), UsageFlags = Chunks.U32(d, o + 0x94), TrackLength = Chunks.U32(d, o + 0x98),
                        TimeToBeatForwardsMs = Chunks.F32(d, o + 0x9C), TimeToBeatReverseMs = Chunks.F32(d, o + 0xA0),
                        MapUpperLeftX = Chunks.F32(d, o + 0xAC), MapUpperLeftY = Chunks.F32(d, o + 0xB0), MapWidthMetres = Chunks.F32(d, o + 0xB4),
                        MapRotation = Chunks.U16(d, o + 0xB8), MapStartLineAngle = Chunks.U16(d, o + 0xBA), MapFinishLineAngle = Chunks.U16(d, o + 0xBC),
                        MapZoomFactor = Chunks.F32(d, o + 0xC0),
                    });
            return r;
        }

        /// <summary>TrackInfo::GetTrackInfo (0x722840): first record with this TrackNumber, null when none.</summary>
        public static TrackInfo Get(string gameRoot, int trackNumber)
        {
            lock (Lock)
            {
                if (_table == null)
                {
                    try
                    {
                        string p = Path.Combine(gameRoot ?? "", "GLOBAL", "GlobalB.lzc");
                        _table = File.Exists(p) ? Read(GameFiles.ReadBundle(p)) : new List<TrackInfo>();
                    }
                    catch (Exception) { _table = new List<TrackInfo>(); }
                }
                foreach (var t in _table) if (t.TrackNumber == trackNumber) return t;
                return null;
            }
        }
    }

    /// <summary>
    /// The track position marker queries (World/TrackPositionMarker.cpp, round 9) over the records Core/NisContent.cs
    /// TrackMarkers reads (chunk 0x34146 in TRACKS\L2RA.BUN; LoaderTrackPositionMarkers 0x7270B0 links each 0x30-byte record
    /// into the list [0x9AA494] in file order, then calls sub_5F19D0). A marker whose TrackNumber is 0 matches every track.
    /// </summary>
    public static class TrackPositionMarkers
    {
        /// <summary>GetNumTrackPositionMarkers (0x727170): markers named nameHash; query track 0 = any marker.</summary>
        public static int Count(TrackMarkers m, int trackNumber, uint nameHash)
        {
            int n = 0;
            foreach (var x in m.All)
                if (x.NameHash == nameHash && (trackNumber == 0 || x.TrackNumber == trackNumber)) n++;
            return n;
        }

        /// <summary>GetTrackPositionMarker (0x727280): the index-th marker named nameHash whose own track number is 0 or
        /// equals trackNumber (the test is on the marker, unlike the count).</summary>
        public static bool Get(TrackMarkers m, int trackNumber, uint nameHash, int index, out TrackMarkers.Marker marker)
        {
            int k = 0;
            foreach (var x in m.All)
            {
                if (x.NameHash != nameHash || (x.TrackNumber != 0 && x.TrackNumber != trackNumber)) continue;
                if (k++ == index) { marker = x; return true; }
            }
            marker = default;
            return false;
        }
    }

    /// <summary>
    /// TheRaceParameters (0x904FB8, World/RaceParameters.cpp; round 9): the Underground-era race parameter block MW still
    /// initialises. InitWithDefaults (0x733B00; RaceStarter::StartRace 0x57ECA0, BeginGameFlowLoadingFrontEnd 0x664B80,
    /// sub_7402A0) zeroes 0x84 bytes and sets the values below, while the races themselves run from GRaceParameters
    /// (gameplay, Runtime/Game/RaceManager). Every other RaceParameters / RunTimePlayerSettings method is an empty stub on PC
    /// (AddDriverInfo 0x733BD0 is ret 4, the getters 0x733C20 / 0x733C30 return 0, the rest are nullsubs without callers).
    /// PC readers of the block: TrackLoader (+0x10 / +0x11 demo flags), the camera / audio flags +0x41..+0x43
    /// (CubicCameraMover::Update 0x47D4B2, SFXCTL_AIPhysics::GenDeltaRPM 0x4B5042, CARSFX_Skids::InitSFX 0x4DBDEA),
    /// RigidBody::CanCollideWith (+0x50) and the online manager; the single-player race starters write only track /
    /// traffic / lap / car counts, so the flag readers see the defaults.
    /// </summary>
    public sealed class RaceParameters
    {
        public int TrackNumber, TrackDirection, TrafficDensity, RaceType, Point2Point, NumLapsInRace, NumPlayerCars, NumAICars;
        public float TrafficOncoming, RollingStartSpeed;
        public bool AIDemoMode, ReplayDemoMode, DamageEnabled, DragRace, DriftRace, ShortRace, Burnout, OnlineRace, CarShow, GamebreakerOn;
        public byte PlayerStartPosition0, PlayerStartPosition1;
        public int HandlingMode, FinishLineNumber, MaxCops, CopStrength, Boost, OpponentStrength, AIDifficultyModifier;
        public float DriftOpponentScoreMultiplier, BoostScale0, BoostScale1;
        public int PlayerJoyport0, PlayerJoyport1, PlayerDriverNumber0, PlayerDriverNumber1;

        public static readonly RaceParameters The = Defaults();

        /// <summary>InitWithDefaults (0x733B00).</summary>
        public static RaceParameters Defaults() => new RaceParameters
        {
            TrackNumber = 2000, TrackDirection = 0, TrafficDensity = 3, TrafficOncoming = 0.5f, RaceType = 0, Point2Point = 0,
            RollingStartSpeed = 0f, NumLapsInRace = 2, NumPlayerCars = 1, NumAICars = 7, PlayerStartPosition0 = 7, PlayerStartPosition1 = 8,
            DamageEnabled = true, HandlingMode = 1, FinishLineNumber = 0, GamebreakerOn = true, PlayerJoyport0 = -1, PlayerJoyport1 = -1,
            MaxCops = 0, CopStrength = 0, DriftOpponentScoreMultiplier = 1f, Boost = 1, OpponentStrength = 0, BoostScale0 = 1f, BoostScale1 = 1f,
            AIDifficultyModifier = 2, PlayerDriverNumber0 = -1, PlayerDriverNumber1 = -1,
        };
    }
}
