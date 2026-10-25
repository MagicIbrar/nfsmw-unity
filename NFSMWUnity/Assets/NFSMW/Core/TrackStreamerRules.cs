using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// TrackStreamingSection (World/TrackStreamer.hpp:46, 0x5C bytes, chunk 0x34110 in TRACKS\L2RA.BUN; loader
    /// TrackStreamer::Loader 0x72BD60).
    /// </summary>
    public sealed class StreamingSectionRecord
    {
        public string Name;
        public int Number;              // +0x08 int16
        public int FileType;            // +0x10 0 permanent, 1 region
        public int FileOffset, Size, CompressedSize, PermSize;   // +0x14 +0x18 +0x1C +0x20
        public int SectionPriority;     // +0x24: added to the loading priority (AssignLoadingPriority 0x728620)
        public float CentreX, CentreY, Radius;                   // +0x28 +0x2C +0x30 (game X/Y)
        public override string ToString() => $"{Name}({Number}) size {Size} prio {SectionPriority}";
    }

    /// <summary>LoadingSection (World/VisibleSection.hpp:225, 0x4C bytes, chunk 0x34155 inside 0x80034150).</summary>
    public sealed class LoadingSectionRecord
    {
        public string Name;
        public bool Default;
        public int[] Drivable;          // up to 16
        public int[] Extra;             // up to 8
        public override string ToString() => $"{Name} drivable [{string.Join(",", Drivable)}] extra [{string.Join(",", Extra)}]";
    }

    /// <summary>One position the streamer follows (StreamingPositionEntry, TrackStreamer.hpp:82; PC stride 0x64 at
    /// TrackStreamer+0xC0). Game space: X/Y ground plane, Elevation = game Z.</summary>
    public sealed class StreamingPosition
    {
        public float X, Y, Elevation, VelX, VelY, DirX, DirY;
        public bool PositionSet, FollowingCar;
        public int CurrentZone, PredictedZone, PredictedZoneValidTime = -1;
        public float Speed => (float)Math.Sqrt(VelX * VelX + VelY * VelY);
    }

    /// <summary>
    /// speed.exe's TrackStreamer decisions (World/TrackStreamer.cpp, exe 0x723600-0x73182B), Unity-free: which
    /// sections a zone needs, the zone prediction from the camera's speed and heading, the loading priorities,
    /// the jettison order and the loading-bar test. Memory pools, disc bundles and hole filling are not modelled
    /// (see NFSMW_RE/specs/world_render.md, "TrackStreamer").
    /// </summary>
    public sealed class TrackStreamerRules
    {
        public readonly List<StreamingSectionRecord> Sections = new List<StreamingSectionRecord>();
        public readonly Dictionary<int, StreamingSectionRecord> ByNumber = new Dictionary<int, StreamingSectionRecord>();
        public readonly List<LoadingSectionRecord> LoadingSections = new List<LoadingSectionRecord>();
        /// <summary>TrackStreamingBarrier (0x10 bytes, chunk 0x34112): a prediction ray crossing one is not used.</summary>
        public readonly List<(float ax, float ay, float bx, float by)> Barriers = new List<(float, float, float, float)>();
        /// <summary>TRACK_PATH_ZONE_STREAMER_PREDICTION (type 6) zones.</summary>
        public readonly List<TrackPathZone> PredictionZones = new List<TrackPathZone>();
        /// <summary>ScenerySectionLODOffset (VisibleSectionManagerInfo +0, chunk 0x34151; default 10 at 0x903164).</summary>
        public int LodOffset = 10;
        /// <summary>KeepSectionTable (TrackStreamer +0x850, 4 x int16; AddKeepSection).</summary>
        public readonly int[] KeepSections = new int[4];
        public VisibleSections Visibility;

        /// <summary>Y0, X0, Z0: always in the set (DetermineStreamingSections 0x72DD70: 0x9C4, 0x960, 0xA28).</summary>
        public static readonly int[] Permanent = { 2500, 2400, 2600 };
        /// <summary>400 mph in m/s (0x8B2B10 = 178.8109): above it there is no prediction / loading bar.</summary>
        public const float MaxPredictSpeed = 178.8109f;
        /// <summary>The camera's own entries: PC has 2 (StreamingPositionEntries[2]).</summary>
        public const int NumPositions = 2;
        /// <summary>NumSectionsLoading &lt; 2 (StartLoadingSections 0x72E8F0).</summary>
        public const int MaxSectionsLoading = 2;
        /// <summary>InitMemoryPool(0x7EF4000 + CarLoaderPoolSizes[0] (50000) * 1024) from LoadGlobalChunks 0x664A24.</summary>
        public const long MemoryPoolSize = 0x7EF4000L + 50000L * 1024L;

        public static TrackStreamerRules Read(byte[] bundle, VisibleSections vis)
        {
            var r = new TrackStreamerRules { Visibility = vis };
            foreach (var c in Chunks.Read(bundle, 0, bundle.Length))
            {
                if (c.Id == 0x34110)
                    for (int o = c.DataStart; o + 0x5C <= c.End; o += 0x5C)
                    {
                        var s = new StreamingSectionRecord
                        {
                            Name = Chunks.CString(bundle, o, 8),
                            Number = Chunks.I16(bundle, o + 8),
                            FileType = Chunks.I32(bundle, o + 0x10),
                            FileOffset = Chunks.I32(bundle, o + 0x14),
                            Size = Chunks.I32(bundle, o + 0x18),
                            CompressedSize = Chunks.I32(bundle, o + 0x1C),
                            PermSize = Chunks.I32(bundle, o + 0x20),
                            SectionPriority = Chunks.I32(bundle, o + 0x24),
                            CentreX = Chunks.F32(bundle, o + 0x28),
                            CentreY = Chunks.F32(bundle, o + 0x2C),
                            Radius = Chunks.F32(bundle, o + 0x30),
                        };
                        r.Sections.Add(s);
                        r.ByNumber[s.Number] = s;
                    }
                else if (c.Id == 0x34112)
                    for (int o = c.DataStart; o + 16 <= c.End; o += 16)
                        r.Barriers.Add((Chunks.F32(bundle, o), Chunks.F32(bundle, o + 4), Chunks.F32(bundle, o + 8), Chunks.F32(bundle, o + 12)));
                else if (c.Id == 0x80034150)
                    foreach (var s in Chunks.Read(bundle, c.DataStart, c.End))
                    {
                        if (s.Id == 0x34151 && s.Size >= 4) r.LodOffset = Chunks.I32(bundle, s.DataStart);
                        else if (s.Id == 0x34155)
                            for (int o = s.DataStart; o + 0x4C <= s.End; o += 0x4C)
                            {
                                int nd = Math.Min(16, Math.Max(0, (int)Chunks.I16(bundle, o + 0x18)));
                                int ne = Math.Min(8, Math.Max(0, (int)Chunks.I16(bundle, o + 0x3A)));
                                var l = new LoadingSectionRecord
                                {
                                    Name = Chunks.CString(bundle, o + 8, 15),
                                    Default = bundle[o + 0x17] != 0,
                                    Drivable = new int[nd],
                                    Extra = new int[ne],
                                };
                                for (int i = 0; i < nd; i++) l.Drivable[i] = Chunks.I16(bundle, o + 0x1A + i * 2);
                                for (int i = 0; i < ne; i++) l.Extra[i] = Chunks.I16(bundle, o + 0x3C + i * 2);
                                r.LoadingSections.Add(l);
                            }
                    }
            }
            foreach (var z in TrackPathZones.Parse(bundle))
                if (z.Type == TrackPathZones.StreamerPrediction) r.PredictionZones.Add(z);
            return r;
        }

        // ---- section numbers (VisibleSection.hpp:25-72; PC letter tests 'A'..'T' at 0x72C869 / 0x730DFB) --------
        public static char Letter(int n) => (char)('A' - 1 + n / 100);
        public static bool IsRegular(int n) { char c = Letter(n); return c >= 'A' && c <= 'T'; }
        public static bool IsTexture(int n) { char c = Letter(n); return c == 'Y' || c == 'W'; }
        public static bool IsLibrary(int n) { char c = Letter(n); return c == 'X' || c == 'U'; }
        public bool IsDrivable(int n) => IsRegular(n) && n % 100 > 0 && n % 100 < LodOffset;
        public bool IsLod(int n) => n % 100 >= LodOffset && n % 100 < LodOffset * 2;
        public int LodOf(int drivable) => drivable + LodOffset;

        /// <summary>FindLoadingSection (VisibleSection.cpp:347): the first loading section listing the drivable section.</summary>
        public LoadingSectionRecord FindLoadingSection(int drivable)
        {
            foreach (var l in LoadingSections)
                if (Array.IndexOf(l.Drivable, drivable) >= 0) return l;
            return null;
        }

        /// <summary>
        /// The sections one zone streams (DetermineStreamingSections 0x72DD70 per position): the loading section that
        /// holds the zone (GetSectionsToLoad 0x72C750: every drivable section's visible list, then the extra sections
        /// and the LOD section of each drivable extra), or else the zone's own visible list. Max 0x180.
        /// </summary>
        public List<int> ZoneSections(int zone)
        {
            var list = new List<int>();
            if (zone <= 0 || Visibility == null) return list;
            void Add(int n) { if (n != 0 && !list.Contains(n) && list.Count < 0x180) list.Add(n); }
            var l = FindLoadingSection(zone);
            if (l == null)
            {
                if (Visibility.Visible.TryGetValue(zone, out var vis)) foreach (var n in vis) list.Add(n);
                return list;
            }
            foreach (var d in l.Drivable)
                if (Visibility.Visible.TryGetValue(d, out var vis)) foreach (var n in vis) Add(n);
            foreach (var e in l.Extra)
            {
                Add(e);
                if (IsDrivable(e)) Add(LodOf(e));
            }
            return list;
        }

        /// <summary>The whole streaming set: Y0, X0, Z0, the keep sections, then each position's zone sections. The bit
        /// per position says which entries need it (TrackStreamingSection.CurrentlyVisible).</summary>
        public Dictionary<int, int> DetermineStreamingSections(IList<StreamingPosition> positions)
        {
            var set = new Dictionary<int, int>();
            int all = (1 << Math.Max(1, Math.Min(positions.Count, NumPositions))) - 1;
            foreach (var n in Permanent) set[n] = all;
            foreach (var n in KeepSections) if (n != 0) set[n] = all;
            for (int p = 0; p < positions.Count; p++)
            {
                if (positions[p].CurrentZone <= 0) continue;
                foreach (var n in ZoneSections(positions[p].CurrentZone))
                    set[n] = (set.TryGetValue(n, out int bits) ? bits : 0) | (1 << Math.Min(p, 31));
            }
            return set;
        }

        /// <summary>
        /// GetPredictedZone 0x72DB80. For every streamer-prediction zone holding the position whose elevation rule
        /// passes (Elevation &gt; 0: camera at or above it; &lt; 0: camera at or below |Elevation|; 0: any), the point
        /// 1.5 s ahead (at most 100 m; none above 400 mph) is tested: if its drivable section is one of the zone's
        /// Data[0..3], that section is the prediction, unless the ray crosses a streaming barrier. Otherwise the
        /// drivable section under the position, else 0.
        /// </summary>
        public int GetPredictedZone(StreamingPosition e)
        {
            int predicted = 0;
            bool used = false;
            float px = e.X, py = e.Y;
            float speed = e.Speed;
            foreach (var z in PredictionZones)
            {
                if (!z.Contains(e.X, e.Y)) continue;
                float el = z.Elevation;
                if (el > 0f && e.Elevation < el) continue;
                if (el < 0f && -el < e.Elevation) continue;
                if (speed > MaxPredictSpeed) { px = e.X; py = e.Y; }
                else if (speed * 1.5f > 100f) { px = e.X + e.VelX * (100f / speed); py = e.Y + e.VelY * (100f / speed); }
                else { px = e.X + e.VelX * 1.5f; py = e.Y + e.VelY * 1.5f; }
                int sec = Visibility.FindDrivableSection(px, py);
                if (sec <= 0) continue;
                for (int n = 0; n < 4; n++)
                {
                    if (z.Data[n] == 0) break;
                    if ((int)z.Data[n] == sec) { used = true; predicted = sec; break; }
                }
            }
            if (used && !(Math.Abs(px - e.X) <= 0.001f && Math.Abs(py - e.Y) <= 0.001f))
                foreach (var b in Barriers)
                    if (SegmentsIntersect(b.ax, b.ay, b.bx, b.by, e.X, e.Y, px, py)) { used = false; predicted = 0; }
            if (used) return predicted;
            int here = Visibility.FindDrivableSection(e.X, e.Y);
            return here > 0 ? here : predicted;
        }

        /// <summary>DetermineCurrentZones 0x72DF20: true when a position's zone changed (the switch is immediate; the
        /// valid-time counter never delays it on PC).</summary>
        public bool DetermineCurrentZones(IList<StreamingPosition> positions, int[] zones)
        {
            bool different = false;
            for (int p = 0; p < positions.Count; p++)
            {
                var e = positions[p];
                int zone = e.PositionSet ? GetPredictedZone(e) : -1;
                if (zone == e.PredictedZone) e.PredictedZoneValidTime++;
                else
                {
                    e.PredictedZoneValidTime = e.PredictedZoneValidTime == -1 ? 1000 : 1;
                    e.PredictedZone = zone;
                }
                if (zone != e.CurrentZone)
                {
                    if (e.PredictedZoneValidTime < 0) zone = e.CurrentZone;
                    if (zone != e.CurrentZone) different = true;
                }
                zones[p] = zone;
            }
            return different;
        }

        /// <summary>
        /// GetLoadingPriority 0x728430 (0 = most urgent, 2 = least). Distance from the point 1 s ahead to the section's
        /// boundary (999 cap), shortened by up to 67% for sections straight ahead at speed:
        /// d * (1 - (90 - clamp(angle, 20, 90)) / 70 * min(speed / 60, 1) * 0.67) / 75. Stationary: 0. For a jettison
        /// the speed is taken as 100 and the heading is the camera direction instead of the velocity.
        /// </summary>
        public int GetLoadingPriority(StreamingSectionRecord s, StreamingPosition e, bool jettison)
        {
            var b = Visibility?.BoundaryOf(BoundarySection(s.Number));
            if (b == null) return 0;
            float speed = jettison ? 100f : e.Speed;
            if (!jettison && !(speed >= 1f)) return 0;
            float qx = e.X + e.VelX, qy = e.Y + e.VelY;
            float dist = b.DistanceOutside(qx, qy, 999f);
            float hx = jettison ? e.DirX : e.VelX, hy = jettison ? e.DirY : e.VelY;
            Normalize(ref hx, ref hy);
            float vx = s.CentreX - qx, vy = s.CentreY - qy;
            Normalize(ref vx, ref vy);
            float dot = hx * vx + hy * vy;
            float speedFactor = Math.Min(speed * 0.016666668f, 1f);
            // (0x4000 - bASin(dot)) * 360/65536: the exe's 16-bit angle, i.e. acos in degrees
            float angle = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, dot))) * 180.0 / Math.PI);
            angle = Math.Max(20f, Math.Min(90f, angle));
            float adjusted = dist * (1f - (90f - angle) * 0.014285714f * speedFactor * 0.67f);
            int prio = (int)(adjusted * 0.013333334f);
            return prio <= 0 ? 0 : Math.Min(prio, 2);
        }

        /// <summary>AssignLoadingPriority 0x728620: best priority over the positions that need it * 100000 +
        /// SectionPriority (lower loads first; StartLoadingSections takes the lowest, disc bundles first).</summary>
        public int LoadingPriority(StreamingSectionRecord s, IList<StreamingPosition> positions, int bits)
        {
            int best = 99;
            for (int p = 0; p < positions.Count && p < 31; p++)
                if ((bits >> p & 1) != 0) best = Math.Min(best, GetLoadingPriority(s, positions[p], false));
            return best * 100000 + s.SectionPriority;
        }

        /// <summary>The loading phase a section belongs to (eLoadingPhase: textures, then geometry libraries, then
        /// regular sections; the permanent Y0/X0/W0/U0/Z0 go with the first phase, AllocateSectionMemory).</summary>
        public static int Phase(int n)
        {
            if (n == 2500 || n == 2400 || n == 2300 || n == 2100 || n == 2600) return 0;
            return IsTexture(n) ? 0 : IsLibrary(n) ? 1 : 2;
        }

        /// <summary>
        /// ChooseSectionToJettison 0x72C410 (only when the pool is out of memory): texture/library packs 2 (Y0/W0/X0 1),
        /// regular sections 100 + 10 x jettison priority; +1 when not loaded yet; the highest goes first.
        /// </summary>
        public int DiscardPriority(StreamingSectionRecord s, IList<StreamingPosition> positions, bool loaded)
        {
            int d = 0, n = s.Number;
            if (IsTexture(n) || IsLibrary(n)) d = n == 2500 || n == 2300 || n == 2400 ? 1 : 2;
            else if (IsRegular(n))
            {
                int lp = GetLoadingPriority(s, positions[0], true);
                if (positions.Count > 1) lp = Math.Min(lp, GetLoadingPriority(s, positions[1], true));
                d = lp * 10 + 100;
            }
            if (d != 0 && !loaded) d++;
            return d;
        }

        /// <summary>
        /// CheckLoadingBar 0x730D20: while loading (and not a far load) and the camera follows a car below 400 mph,
        /// the closest not-yet-activated drivable or LOD section in the set, at distance outside its boundary minus
        /// 0.2 s of approach. Under 5 m the game pauses the simulation behind a screen fade (0x64B770). Returns that
        /// distance (999 when none).
        /// </summary>
        public float LoadingBarDistance(IList<StreamingPosition> positions, IEnumerable<int> notActivated)
        {
            float closest = 999f;
            var pending = new List<int>(notActivated);
            foreach (var e in positions)
            {
                if (!e.PositionSet || !e.FollowingCar) break;
                if (e.Speed > MaxPredictSpeed) break;
                foreach (var n in pending)
                {
                    if (!IsRegular(n) || !(IsDrivable(n) || IsLod(n))) continue;
                    var b = Visibility?.BoundaryOf(BoundarySection(n));
                    if (b == null) continue;
                    float d1 = b.DistanceOutside(e.X, e.Y, 999f);
                    float d2 = b.DistanceOutside(e.X + e.VelX * 0.01f, e.Y + e.VelY * 0.01f, 999f);
                    float approach = (d1 - d2) * 100f * 0.2f;
                    closest = Math.Min(closest, d1 - approach);
                }
            }
            return closest;
        }

        /// <summary>GetBoundarySectionNumber (VisibleSection.cpp:211): LOD sections use their drivable section's boundary
        /// (PC has no 2-player remap).</summary>
        public int BoundarySection(int n) => IsLod(n) ? n - LodOffset : n;

        static void Normalize(ref float x, ref float y)
        {
            float l = (float)Math.Sqrt(x * x + y * y);
            if (l > 0f) { x /= l; y /= l; }
        }

        /// <summary>DoLinesIntersect 0x722980 (TrackPath.cpp:13): parametric test, ends inclusive, parallel = no.</summary>
        public static bool SegmentsIntersect(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
        {
            float den = (bx - ax) * (dy - cy) - (by - ay) * (dx - cx);
            if (den == 0f) return false;
            float r = ((ay - cy) * (dx - cx) - (ax - cx) * (dy - cy)) / den;
            if (!(r >= 0f && r <= 1f)) return false;
            float s = ((ay - cy) * (bx - ax) - (ax - cx) * (by - ay)) / den;
            return s >= 0f && s <= 1f;
        }
    }
}
