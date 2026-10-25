using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// Per-event scheduling attributes of the police speech: attributes.bin class <c>speech</c> (key 0xC593DD47,
    /// Generated/AttribSys/Classes/speech.h), one collection per Csis interface named in lower case
    /// ("setup_attmptvehstp", parent "default"). Speech::Manager (EAXSound/Stream/SpeechManager.cpp) reads them in
    /// ScheduleSpeechPartII (priority, interrupt, DoNotDropout, RecallList, InitDelay), PostValidate (expiry, MaxPlayback,
    /// DeadAir, Interval, CullingRange, DepFollow/BackTime, OnScreenOnly, reqLOS, Min/MaxHeat, Min/MaxPlayerSpeed),
    /// IsEventDead (EnforceDeadAir) and GameSpeech::IssuePlayback (Clarity, Pan, RadioChirp). Distances are metres,
    /// times seconds, player speeds mph (SoundAI::GetPlayerSpeed).
    /// </summary>
    public sealed class SpeechEventInfo
    {
        public string Name;
        public int SpeechId;
        public float BackTime, Expiry = 5f, CullingRange = 300f, Interval = 20f, DeadAir, EnforceDeadAir = 2f, InitDelay;
        public float MinPlayerSpeed, MaxPlayerSpeed = 550f;
        public int Priority = 50, MaxPlayback = -1, MinHeat, MaxHeat = 10, Clarity = 10;
        public bool DoNotDropout, Interrupt, Interruptable = true, OnScreenOnly, ReqLOS, Pan, RadioChirp = true;
        /// <summary>DepFollow / RecallList as speech ids.</summary>
        public int[] DepFollow = Array.Empty<int>(), RecallList = Array.Empty<int>();
        public override string ToString() => $"{Name}#{SpeechId} prio {Priority}{(Interrupt ? " int" : "")} exp {Expiry}";
    }

    /// <summary>attributes.bin class <c>speechtune</c>, collection "default" (Generated/AttribSys/Classes/speechtune.h);
    /// the defaults are the PC 1.3 values.</summary>
    public sealed class SpeechTune
    {
        public float PlayerSmashSpeedMin = 10f, PlayerSmashSpeedMax = 30f;
        public float PursuitInactivityTimer0 = 2f, PursuitInactivityTimer1 = 150f;
        public float SpeechDropoffStart = 30f, SpeechDropoffEnd = 500f;
        public float OutcomeFailSpeed = 20f, MinIntensitySideswipe = 0.5f, SpeedDiffForBlowby = 60f, BlowbyInterval = 10f;
        public float HangTimeForCommentary = 0.9f, BURemindTime = 20f, AIRacerProximity = 145f, MinIntensityCopSmash = 0.85f;
        public float MaxRangeFor180 = 50f, MaxRangeForPrimaryBranch = 20f, MinIntensityTrafficSmash = 0.5f;
        public float MinPursuitDurationForBailouts = 60f, FlipTimeForCommentary = 1f, PriorityWeight = 0.15f, OutcomeTrackTime = 3f;
        public float RangeForSpotterBranch = 100f, RBOutcomeTimer = 1.5f, SpeedThreshFlyFlipIntensity = 100f, PursuitInitDelay = 2f;
        public float TimeWaitForSpotterReply = 6f, TimeConsideredLostNoLOS = 3f, RBPostOutcomeResetTime = 2f, MinHeightAirborne = 0.5f;
        public int MinHavocForSuspectBehavior = 200;
        public float MinSpeedConsideredStopped = 5f, MinTimeConsideredStopped = 2f, PursuitDurationHighIntensity = 150f;
        public float CrashSlowdownPct = 0.66f, MinHealthForCommentary = 0.33f, CollisionMinClosingVelSq = 5f, MaxTimeFor180 = 5f;
        public float SuspectOutrunRange = 120f, NoLOSCommentaryTime = 2f;
        public int HighIntensityMark = 2, MinContigFramesFor180 = 12;
    }

    /// <summary>
    /// Data side of the police speech AI (Speech/SoundAI.cpp, MiscSpeech.cpp): the speech/speechtune attributes, the
    /// road speech ids (RoadNames, Speech/MWRoadNames.h) of the AI road network and MiscSpeech::GetLocation.
    /// </summary>
    public static class CopSpeechRules
    {
        /// <summary>Every <c>speech</c> collection, by lower-case Csis interface name.</summary>
        public static Dictionary<string, SpeechEventInfo> LoadEvents(AttribDatabase db)
        {
            var res = new Dictionary<string, SpeechEventInfo>(StringComparer.OrdinalIgnoreCase);
            if (db == null) return res;
            uint cls = AttribDatabase.Hash("speech");
            foreach (var c in db.Collections.Values)
            {
                if (c.ClassKey != cls || c.Name == null) continue;
                var e = new SpeechEventInfo { Name = c.Name };
                e.SpeechId = I32(c, "SpeechID", 0);
                e.BackTime = c.Float("BackTime", 0, e.BackTime);
                e.Expiry = c.Float("expiry", 0, e.Expiry);
                e.CullingRange = c.Float("CullingRange", 0, e.CullingRange);
                e.Interval = c.Float("Interval", 0, e.Interval);
                e.DeadAir = c.Float("DeadAir", 0, e.DeadAir);
                e.EnforceDeadAir = c.Float("EnforceDeadAir", 0, e.EnforceDeadAir);
                e.InitDelay = c.Float("InitDelay", 0, e.InitDelay);
                e.MinPlayerSpeed = c.Float("MinPlayerSpeed", 0, e.MinPlayerSpeed);
                e.MaxPlayerSpeed = c.Float("MaxPlayerSpeed", 0, e.MaxPlayerSpeed);
                e.Priority = I16(c, "priority", e.Priority);
                e.MaxPlayback = I32(c, "MaxPlayback", e.MaxPlayback);
                e.MinHeat = I8(c, "MinHeat", e.MinHeat);
                e.MaxHeat = I8(c, "MaxHeat", e.MaxHeat);
                e.Clarity = U8(c, "Clarity", e.Clarity);
                e.DoNotDropout = c.Bool("DoNotDropout", e.DoNotDropout);
                e.Interrupt = c.Bool("interrupt", e.Interrupt);
                e.Interruptable = c.Bool("Interruptable", e.Interruptable);
                e.OnScreenOnly = c.Bool("OnScreenOnly", e.OnScreenOnly);
                e.ReqLOS = c.Bool("reqLOS", e.ReqLOS);
                e.Pan = c.Bool("Pan", e.Pan);
                e.RadioChirp = c.Bool("RadioChirp", e.RadioChirp);
                e.DepFollow = RefIds(db, c, "DepFollow");
                e.RecallList = RefIds(db, c, "RecallList");
                res[c.Name] = e;
            }
            return res;
        }

        public static SpeechTune LoadTune(AttribDatabase db)
        {
            var t = new SpeechTune();
            var c = db?.Get("speechtune", "default");
            if (c == null) return t;
            t.PlayerSmashSpeedMin = c.Float("PlayerSmashSpeedRange", 0, t.PlayerSmashSpeedMin);
            t.PlayerSmashSpeedMax = c.Float("PlayerSmashSpeedRange", 1, t.PlayerSmashSpeedMax);
            t.PursuitInactivityTimer0 = c.Float("PursuitInactivityTimer", 0, t.PursuitInactivityTimer0);
            t.PursuitInactivityTimer1 = c.Float("PursuitInactivityTimer", 1, t.PursuitInactivityTimer1);
            t.SpeechDropoffStart = c.Float("SpeechDropoffRamp", 0, t.SpeechDropoffStart);
            t.SpeechDropoffEnd = c.Float("SpeechDropoffRamp", 1, t.SpeechDropoffEnd);
            t.OutcomeFailSpeed = c.Float("OutcomeFailSpeed", 0, t.OutcomeFailSpeed);
            t.MinIntensitySideswipe = c.Float("MinIntensitySideswipe", 0, t.MinIntensitySideswipe);
            t.SpeedDiffForBlowby = c.Float("SpeedDiffForBlowby", 0, t.SpeedDiffForBlowby);
            t.BlowbyInterval = c.Float("BlowbyInterval", 0, t.BlowbyInterval);
            t.HangTimeForCommentary = c.Float("HangTimeForCommentary", 0, t.HangTimeForCommentary);
            t.BURemindTime = c.Float("BURemindTime", 0, t.BURemindTime);
            t.AIRacerProximity = c.Float("AIRacerProximity", 0, t.AIRacerProximity);
            t.MinIntensityCopSmash = c.Float("MinIntensityCopSmash", 0, t.MinIntensityCopSmash);
            t.MaxRangeFor180 = c.Float("MaxRangeFor180", 0, t.MaxRangeFor180);
            t.MaxRangeForPrimaryBranch = c.Float("MaxRangeForPrimaryBranch", 0, t.MaxRangeForPrimaryBranch);
            t.MinIntensityTrafficSmash = c.Float("MinIntensityTrafficSmash", 0, t.MinIntensityTrafficSmash);
            t.MinPursuitDurationForBailouts = c.Float("MinPursuitDurationForBailouts", 0, t.MinPursuitDurationForBailouts);
            t.FlipTimeForCommentary = c.Float("FlipTimeForCommentary", 0, t.FlipTimeForCommentary);
            t.PriorityWeight = c.Float("PriorityWeight", 0, t.PriorityWeight);
            t.OutcomeTrackTime = c.Float("OutcomeTrackTime", 0, t.OutcomeTrackTime);
            t.RangeForSpotterBranch = c.Float("RangeForSpotterBranch", 0, t.RangeForSpotterBranch);
            t.RBOutcomeTimer = c.Float("RBOutcomeTimer", 0, t.RBOutcomeTimer);
            t.SpeedThreshFlyFlipIntensity = c.Float("SpeedThreshFlyFlipIntensity", 0, t.SpeedThreshFlyFlipIntensity);
            t.PursuitInitDelay = c.Float("PursuitInitDelay", 0, t.PursuitInitDelay);
            t.TimeWaitForSpotterReply = c.Float("TimeWaitForSpotterReply", 0, t.TimeWaitForSpotterReply);
            t.TimeConsideredLostNoLOS = c.Float("TimeConsideredLostNoLOS", 0, t.TimeConsideredLostNoLOS);
            t.RBPostOutcomeResetTime = c.Float("RBPostOutcomeResetTime", 0, t.RBPostOutcomeResetTime);
            t.MinHeightAirborne = c.Float("MinHeightAirborne", 0, t.MinHeightAirborne);
            t.MinHavocForSuspectBehavior = I32(c, "MinHavocForSuspectBehavior", t.MinHavocForSuspectBehavior);
            t.MinSpeedConsideredStopped = c.Float("MinSpeedConsideredStopped", 0, t.MinSpeedConsideredStopped);
            t.MinTimeConsideredStopped = c.Float("MinTimeConsideredStopped", 0, t.MinTimeConsideredStopped);
            t.PursuitDurationHighIntensity = c.Float("PursuitDurationHighIntensity", 0, t.PursuitDurationHighIntensity);
            t.CrashSlowdownPct = c.Float("CrashSlowdownPct", 0, t.CrashSlowdownPct);
            t.MinHealthForCommentary = c.Float("MinHealthForCommentary", 0, t.MinHealthForCommentary);
            t.CollisionMinClosingVelSq = c.Float("CollisionMinClosingVelSq", 0, t.CollisionMinClosingVelSq);
            t.MaxTimeFor180 = c.Float("MaxTimeFor180", 0, t.MaxTimeFor180);
            t.SuspectOutrunRange = c.Float("SuspectOutrunRange", 0, t.SuspectOutrunRange);
            t.NoLOSCommentaryTime = c.Float("NoLOSCommentaryTime", 0, t.NoLOSCommentaryTime);
            t.HighIntensityMark = I8(c, "HighIntensityMark", t.HighIntensityMark);
            t.MinContigFramesFor180 = I8(c, "MinContigFramesFor180", t.MinContigFramesFor180);
            return t;
        }

        static int I32(AttribCollection c, string f, int fb) => c.TryGet(f, out int at, out _, out _) ? Chunks.I32(c.Source.Data, at) : fb;
        static int I16(AttribCollection c, string f, int fb) => c.TryGet(f, out int at, out _, out _) ? Chunks.I16(c.Source.Data, at) : fb;
        static int I8(AttribCollection c, string f, int fb) => c.TryGet(f, out int at, out _, out _) ? (sbyte)c.Source.Data[at] : fb;
        static int U8(AttribCollection c, string f, int fb) => c.TryGet(f, out int at, out _, out _) ? c.Source.Data[at] : fb;

        static int[] RefIds(AttribDatabase db, AttribCollection c, string f)
        {
            if (!c.Has(f)) return Array.Empty<int>();
            var refs = c.Refs(f);
            var ids = new List<int>(refs.Length);
            foreach (var (cls, key) in refs)
                if (db.Collections.TryGetValue((cls, key), out var dep)) ids.Add(I32(dep, "SpeechID", 0));
            return ids.ToArray();
        }

        // ------------------------------------------------------------------------------------------ roads

        /// <summary>
        /// The speech id (RoadNames, Speech/MWRoadNames.h; 0 untagged) of every WRoad in TRACKS\L2RA.BUN chunk 0x3B800
        /// ("RNrd" WRoad 8 bytes: u16 scale, u16 length, u8 shortcut, u8 min width, u16 speech id), indexed like
        /// <see cref="RoadNetwork.Segment.Road"/> (WRoadNav::GetRoadSpeechId).
        /// </summary>
        public static ushort[] RoadSpeechIds(byte[] bundle)
        {
            foreach (var ch in Chunks.Read(bundle, 0, bundle.Length))
            {
                if (ch.Id != 0x3B800) continue;
                int carp = Chunks.Align(ch.DataStart, 16);
                if (Chunks.U32(bundle, carp) != 0x43415250) break;
                int n = Chunks.I32(bundle, carp + 4);
                for (int i = 0; i < n; i++)
                {
                    int e = carp + 16 + i * 16;
                    if (bundle[e + 3] != 'R' || bundle[e + 2] != 'N' || bundle[e + 1] != 'r' || bundle[e] != 'd') continue;
                    uint a = Chunks.U32(bundle, e + 4);
                    int off = Chunks.I32(bundle, e + 12), at = (a & 2) != 0 ? e + off : off, size = (int)(a >> 8);
                    var ids = new ushort[size / 8];
                    for (int r = 0; r < ids.Length; r++) ids[r] = Chunks.U16(bundle, at + r * 8 + 6);
                    return ids;
                }
            }
            return Array.Empty<ushort>();
        }

        /// <summary>RoadNames: untagged = 0, Seaside_Interchange = 81 (SoundAI::IsHeadingValid upper bound), MAX_ROADNAMES = 107.</summary>
        public const int RoadUntagged = 0, RoadSeasideInterchange = 81, MaxRoadNames = 107;

        // MiscSpeech::GetLocation (MiscSpeech.cpp): RoadNames -> (location_region bit, Location_n). Regions: 1 college
        // town, 2 coastal, 4 coastal extra, 8 city. Roads 84, 87 and the offroad tags (88..106) have no location.
        static readonly (byte region, byte loc)[] LocationTable = BuildLocations();

        static (byte, byte)[] BuildLocations()
        {
            var t = new (byte, byte)[MaxRoadNames];
            for (int r = 1; r <= 30; r++) t[r] = (1, (byte)r);           // on_Highway99 .. on_Riverside_Drive
            for (int r = 31; r <= 61; r++) t[r] = (2, (byte)(r - 30));   // on_Bristol_Road .. by_Smugglers_Wharf
            t[62] = (4, 3);   // past_Omega_Industries: location = (Type_location)region = 4 = Location_3
            t[63] = (4, 1);
            t[64] = (8, 1);
            t[65] = (8, 3);
            t[66] = (8, 4);   // thru_Lyons_Tunnel: location = (Type_location)region = 8 = Location_4
            for (int r = 67; r <= 82; r++) t[r] = (8, (byte)(r - 62)); // over_Ironhorse_Bridge (5) .. on_I17 (20)
            t[83] = (1, 31);
            t[85] = (4, 4);
            t[86] = (8, 21);
            return t;
        }

        /// <summary>MiscSpeech::GetLocation: Csis location_region and location bit flags of a RoadNames id.</summary>
        public static bool GetLocation(int road, out int region, out int location)
        {
            region = location = 0;
            if (road <= 0 || road >= LocationTable.Length || LocationTable[road].region == 0) return false;
            region = LocationTable[road].region;
            location = 1 << (LocationTable[road].loc - 1);
            return true;
        }

        /// <summary>SoundAI::GetBattalionFromRoadID: speaker_battalion of the road's region (City 4, Coastal 2, Rosewood 1), -1 none.</summary>
        public static int BattalionFromRoad(int road)
        {
            if (!GetLocation(road, out int region, out _)) return -1;
            switch (region)
            {
                case 8: return 4;
                case 2: case 4: return 2;
                case 1: return 1;
                default: return -1;
            }
        }

        // ------------------------------------------------------------------------------------------ colour

        // car_color bits
        const int Red = 1, Black = 2, White = 4, Blue = 8, Green = 16, Orange = 32, Silver = 64, Gold = 128, Purple = 256, Brown = 512,
            Yellow = 1024, Pink = 2048, Beige = 4096;

        /// <summary>
        /// Csis car_color bit (Red 1 .. Beige 4096) of an RGB paint, by hue, saturation and brightness. The game takes the
        /// base paint part's SPEECHCOLOUR attribute (SoundAI::GetCustomized, ColourHashToSoundColourMap); the port's
        /// paint is an RGB, so it is classified instead [?].
        /// </summary>
        public static int NearestColour(float r, float g, float b)
        {
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), v = max, s = max > 1e-4f ? (max - min) / max : 0f;
            if (s < 0.18f || max - min < 0.06f) return v < 0.22f ? Black : v > 0.88f ? White : Silver;
            float d = max - min, h;
            if (max == r) h = 60f * (((g - b) / d) % 6f);
            else if (max == g) h = 60f * ((b - r) / d + 2f);
            else h = 60f * ((r - g) / d + 4f);
            if (h < 0f) h += 360f;
            if (v < 0.18f) return Black;
            if (h < 15f || h >= 340f) return v < 0.4f ? Brown : s < 0.5f && v > 0.7f ? Pink : Red;
            if (h < 40f) return v < 0.55f ? Brown : s < 0.4f ? Beige : Orange;
            if (h < 56f) return v < 0.45f ? Brown : s < 0.45f ? Beige : Gold;
            if (h < 72f) return v < 0.45f ? Green : Yellow;
            if (h < 170f) return Green;
            if (h < 255f) return Blue;
            if (h < 300f) return Purple;
            return Pink;
        }
    }
}
