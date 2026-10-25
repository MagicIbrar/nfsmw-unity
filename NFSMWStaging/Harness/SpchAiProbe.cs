using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// spchai [explore]: checks the police speech AI's data side (Core/CopSpeechRules.cs): the speech/speechtune
// attributes and that every event the AI schedules, with the parameter sets it fills, picks a sentence.
static class SpchAiProbe
{
    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        if (args.Length > 2 && args[2] == "explore") { Explore(db); return; }
        Check(root, db);
    }

    // speakers: dispatch 1, heli 2, units 3..8, Cross 9
    static readonly int[] Units = { 3, 4, 5, 6, 7, 8 }, UnitsX = { 3, 4, 5, 6, 7, 8, 9 }, UnitsH = { 2, 3, 4, 5, 6, 7, 8, 9 }, Disp = { 1 };
    static readonly int[] Bits4 = { 1, 2, 4, 8 };
    static readonly (string, int[])[] None = new (string, int[])[0];
    static int[] Bits(int n) { var r = new int[n]; for (int i = 0; i < n; i++) r[i] = 1 << i; return r; }
    static (string, int[])[] P(params (string, int[])[] p) => p;
    static (string, int[]) V(string p, params int[] v) => (p, v);

    // (battalion, unit number) pairs as SoundAI::GetCallsign hands them out
    static IEnumerable<(int bat, int unit)> CallSigns()
    {
        foreach (var (b, n) in new[] { (1, 10), (2, 10), (4, 20), (8, 10), (16, 5), (32, 6) })
            for (int i = 0; i < n; i++) yield return (b, 1 << i);
        yield return (64, 1);
    }

    static void Check(string root, AttribDatabase db)
    {
        var s = SpeechScript.Load(root, "copspeech");
        var info = CopSpeechRules.LoadEvents(db);
        var tune = CopSpeechRules.LoadTune(db);
        Console.WriteLine($"speech attributes: {info.Count} collections; tune PursuitInitDelay {tune.PursuitInitDelay} HighIntensityMark {tune.HighIntensityMark} BURemindTime {tune.BURemindTime} SpeechDropoff {tune.SpeechDropoffStart}..{tune.SpeechDropoffEnd} MinHavoc {tune.MinHavocForSuspectBehavior} MinContig180 {tune.MinContigFramesFor180}");
        int missing = 0, idMismatch = 0;
        foreach (var n in s.EventNames)
        {
            if (!info.TryGetValue(n, out var e)) { missing++; Console.WriteLine($"  no speech attributes: {n}"); continue; }
            if (e.SpeechId != s.GetEvent(n).Id) { idMismatch++; Console.WriteLine($"  id {n}: attrib {e.SpeechId} evt {s.GetEvent(n).Id}"); }
        }
        Console.WriteLine($"events {s.EventNames.Count}: {missing} without attributes, {idMismatch} id mismatches");
        foreach (var n in new[] { "setup_vehiclereport", "staticroadblock_disprbupdate", "outcome_anticipatefail" })
            if (info.TryGetValue(n, out var e)) Console.WriteLine($"  {e}: dep [{string.Join(",", e.DepFollow)}] recall [{string.Join(",", e.RecallList)}] back {e.BackTime} interval {e.Interval}");

        // the player's make (pvehicle VerbalType, Csis car_type bit)
        var vt = new List<string>();
        foreach (var c in db.OfClass("pvehicle"))
        {
            if (c.Name == null || !c.TryGet("VerbalType", out int at, out _, out _)) continue;
            int v = Chunks.I32(c.Source.Data, at);
            var names = SpeechScript.EnumNames("car_type");
            int bit = 0; while (bit < 31 && (1 << bit) != v) bit++;
            vt.Add($"{c.Name}={(v == 0 ? "0" : bit < names.Length ? names[bit] : v.ToString())}");
        }
        Console.WriteLine($"pvehicle VerbalType ({vt.Count}): {string.Join(" ", vt.Take(60))}");
        foreach (var (r, g, b) in new[] { (0.8f, 0.82f, 0.84f), (0.1f, 0.1f, 0.12f), (0.9f, 0.1f, 0.05f), (0.95f, 0.95f, 0.2f), (0.2f, 0.3f, 0.9f) })
        {
            int col = CopSpeechRules.NearestColour(r, g, b), i = 0;
            while ((1 << i) != col) i++;
            Console.WriteLine($"  colour ({r},{g},{b}) -> {SpeechScript.EnumNames("car_color")[i]}");
        }

        // roads
        var bun = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        var ids = CopSpeechRules.RoadSpeechIds(bun);
        var net = RoadNetwork.Read(bun);
        var hist = new SortedDictionary<int, int>();
        foreach (var id in ids) hist[id] = hist.TryGetValue(id, out int c) ? c + 1 : 1;
        int located = ids.Count(i => CopSpeechRules.GetLocation(i, out _, out _));
        Console.WriteLine($"roads: {ids.Length} speech ids (net has {net.RoadInfos.Length} roads), {hist.Count} distinct, {located} with a location; ids: {string.Join(" ", hist.Select(kv => kv.Key + "x" + kv.Value))}");
        int segsNamed = net.Segs.Count(sg => sg.Road >= 0 && sg.Road < ids.Length && ids[sg.Road] > 0 && ids[sg.Road] < 81);
        Console.WriteLine($"segments on a heading-valid named road: {segsNamed}/{net.Segs.Length}");

        var tests = new List<(string ev, int[] spk, (string, int[])[] pars)>
        {
            ("Acknowledge", UnitsH.Concat(Disp).ToArray(), P(V("yes_no", 1, 2), V("intensity", 1, 2))),
            ("Interrupts_StaticInterrupt", null, None),
            ("Interrupts_InterruptRam", UnitsX, None),
            ("Interrupts_InterruptRamHigh", UnitsX, None),
            ("Interrupts_Interrupt", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_DriverHistory", Units.Concat(Disp).ToArray(), P(V("region", 2))),
            ("AnytimeEvents_HeatJump", UnitsH.Concat(Disp).ToArray(), P(V("heat_level", Bits4))),
            ("Backup_DispBackupReply", Disp, P(V("code", 2), V("yes_no", 1, 2), V("disp_backup_type", 32, 16, 64, 8), V("subject", -1))),
            ("Arrest_DispArrestReply", Disp, None),
            ("AnytimeEvents_DispPursuitUpdate", Disp, P(V("subject", -1))),
            ("AnytimeEvents_DispPursEscGen", Disp, P(V("num_suspects", 1))),
            ("AnytimeEvents_DispPursuitEscalation", Disp, P(V("address_group_type", 1, 2, 4, 8, 16), V("pursuit_type", Bits(5)), V("num_suspects", 1), V("direction", Bits4), V("loc", -1))),
            ("Backup_DispBackupUpdate", Disp, P(V("yes_no", 1, 2))),
            ("AnytimeEvents_DispBreakAway", Disp, P(V("direction", Bits4), V("loc", -1))),
            ("Setup_DispGoAhead", Disp, None),
            ("AnytimeEvents_DispTimeExpired", Disp, None),
            ("StaticRoadblock_DispRBUpdate", Disp, P(V("code", 2), V("roadblock_type", 1, 8), V("yes_no", 1, 2))),
            ("StaticRoadblock_DispRBReply", Disp, P(V("code", 2), V("roadblock_type", 1, 2, 4, 8), V("yes_no", 1, 2), V("subject", -1))),
            ("AnytimeEvents_DispJurisShift", Disp, P(V("jurisdiction", 1, 2))),
            ("Backup_DispBUETA", Disp, P(V("disp_backup_eta", Bits(6)))),
            ("Setup_DispVehDescrip", Disp, P(V("car_color", Bits(13)), V("car_type", Bits(31)))),
            ("Setup_DispNoVehDescrip", Disp, None),
            ("StaticRoadblock_DispSubRB", Disp, None),
            ("Setup_PrimaryEngage", UnitsH, P(V("speaker", -1))),
            ("Setup_AttmptVehStp", UnitsX, P(V("pursuit_type", Bits(5)), V("num_suspects", 1), V("speaker", -1))),
            ("Setup_Spotter", UnitsX, P(V("num_suspects", 1))),
            ("HeliSpecific_HeliSpotter", new[] { 2 }, None),
            ("Setup_SpotterReply", UnitsH, P(V("num_suspects", 1))),
            ("AnytimeEvents_Unit911Reply", UnitsH, None),
            ("Setup_ReInitPursuit", UnitsX, P(V("time_since_lost", 1, 2), V("num_suspects", 1))),
            ("Setup_VehicleReport", UnitsH, P(V("car_type", Bits(31)), V("car_color", Bits(13)), V("measurement", 1, 2, 4), V("speed", Bits(12)))),
            ("Setup_InitPursuit", UnitsX, P(V("num_suspects", 1))),
            ("Setup_LocationReport", UnitsH, P(V("num_suspects", 1), V("direction", Bits4), V("encounter", 1, 2), V("loc", -1))),
            ("Setup_SelfStrategy", UnitsX, P(V("self_strategy_type", 2, 4, 8, 16, 32, 64), V("code", 2))),
            ("HeliSpecific_HeliSelfStrategy", new[] { 2 }, P(V("heli_self_strategy_type", 1, 2))),
            ("AnytimeEvents_CallForEV", UnitsH, P(V("ev_type", 1, 2, 4))),
            ("Setup_InitialCallForBU", UnitsX, P(V("code", 2))),
            ("Backup_CallForBU", UnitsH, P(V("code", 2), V("backup_type", 1, 2, 4))),
            ("Backup_BUReminder", UnitsH, P(V("code", 2), V("backup_type", 1, 2, 4))),
            ("Backup_BUArrives", UnitsH, P(V("speaker", -1))),
            ("Backup_UnitBUReply", UnitsH, None),
            ("Backup_NegativeBUReply", UnitsH, None),
            ("RollingStrategy_InitStrategy", UnitsX, P(V("code", 2), V("rolling_strategy_type", 2, 4, 16, 32))),
            ("RollingStrategy_CallToPosition", UnitsX, P(V("intensity", 1, 2), V("position", Bits4), V("subject", -1))),
            ("RollingStrategy_CallToPositionRem", UnitsX, P(V("intensity", 1, 2))),
            ("RollingStrategy_StrategyExecute", UnitsX, P(V("intensity", 1, 2))),
            ("AnytimeEvents_IntentToRam", UnitsX, P(V("intensity", 1, 2))),
            ("Outcome_AnticipateSuccess", UnitsH, None),
            ("Outcome_AnticipateFail", UnitsH, None),
            ("AnytimeEvents_LostSuspect", UnitsH, P(V("intensity", 1, 2))),
            ("Arrest_Arrest", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_LostVisual", UnitsX, P(V("intensity", 1, 2))),
            ("HeliSpecific_HeliLostVisual", new[] { 2 }, P(V("heli_lost_visual", 1, 8))),
            ("AnytimeEvents_RegainVisual", UnitsH, P(V("intensity", 1, 2))),
            ("Outcome_OutcomeFail", UnitsH, P(V("intensity", 1, 2))),
            ("Outcome_StrategyReset", UnitsH, P(V("intensity", 1, 2), V("same_new", 1, 2))),
            ("Setup_Bullhorn", UnitsX, None),
            ("Arrest_BullhornArrest", UnitsX, P(V("intensity", 1, 2))),
            ("HeliSpecific_HeliBullhornArrest", new[] { 2 }, None),
            ("AnytimeEvents_SuspectBehaviour", UnitsH, P(V("num_suspects", 1))),
            ("Setup_SuspectConfirmed", UnitsX, P(V("num_suspects", 1))),
            ("AnytimeEvents_SuspectOutrun", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_SuspectUTurn", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_FocusChange", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_CollisionWorld", UnitsH, P(V("world_object_type", 1, 2, 4, 8, 16, 32, 64), V("num_units", 1, 2))),
            ("AnytimeEvents_CollWorld_Civi", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_CollWorld_Flip", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_CollWorld_Air", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_SuspectBrake", UnitsH, None),
            ("AnytimeEvents_UnitDisabled", UnitsH, P(V("self_other", 1, 2), V("intensity", 1, 2))),
            ("AnytimeEvents_PursuitUpdateRep", UnitsH, None),
            ("AnytimeEvents_Bailout", UnitsX, P(V("bailout_type", 1, 2, 4, 8, 16))),
            ("HeliSpecific_HeliBailout", new[] { 2 }, P(V("heli_bailout_type", 4, 8))),
            ("HeliSpecific_HeliIntentToBail", new[] { 2 }, P(V("heli_bailout_type", 4, 8))),
            ("AnytimeEvents_BailoutDeny", UnitsH, None),
            ("StaticRoadblock_CallForRB", UnitsH, P(V("code", 2), V("roadblock_type", 1, 2))),
            ("StaticRoadblock_CallForRB_sub", UnitsX, None),
            ("StaticRoadblock_RBReminder", UnitsH, P(V("code", 2))),
            ("StaticRoadblock_NegativeRBReply", UnitsX, None),
            ("StaticRoadblock_RBApproach", UnitsH, P(V("roadblock_type", 1, 2))),
            ("StaticRoadblock_RBEngage", UnitsH, P(V("roadblock_engage_type", 1, 2))),
            ("StaticRoadblock_RBAverted", UnitsH, None),
            ("StaticRoadblock_PursuitApproaching", UnitsH, P(V("num_suspects", 1))),
            ("Interrupts_InterruptRam_HO", UnitsX, P(V("intensity", 1, 2))),
            ("Interrupts_InterruptRam_TB", UnitsX, P(V("intensity", 1, 2))),
            ("Interrupts_InterruptRam_SS", UnitsX, P(V("intensity", 1, 2))),
            ("Interrupts_InterruptRam_RE", UnitsX, P(V("intensity", 1, 2))),
            ("AnytimeEvents_Spotted", UnitsH, P(V("intensity", 1, 2))),
            ("AnytimeEvents_DirectionHigh", UnitsH, P(V("direction", Bits4))),
            ("Backup_CallForSwarming", UnitsH, None),
            ("Setup_SpotterWanted", UnitsH, None),
            ("HeliSpecific_HeliSwarming", new[] { 2 }, None),
            ("ExtraCops_SwarmingReply", null, None),
            ("ExtraCops_SwarmingReplyFollow", null, None),
            ("ExtraCops_QuadrentForming", null, None),
            ("ExtraCops_SuspectPossiblyGone", null, None),
            ("ExtraCops_QuadrentMoving", null, None),
            ("ExtraCops_OtherLead", null, None),
            ("ExtraCops_PossibleSuspect", null, None),
            ("ExtraCops_WrongSuspect", null, None),
            ("ExtraCops_RBWarning", null, None),
            ("ExtraCops_RBPosition", null, P(V("spikebelt_position", 1, 2, 4))),
            ("ExtraCops_ExtraRBEngage", null, P(V("roadblock_engage_type", 1, 2))),
            ("ExtraCops_ExtraRBAverted", null, P(V("roadblock_engage_type", 1, 2))),
            // 911 flow, tunnel hazard and story calls (round 2)
            ("AnytimeEvents_Disp911Report", Disp, P(V("pursuit_type", 1, 2), V("num_suspects", 1), V("encounter", 1), V("direction", Bits4), V("address_group_type", 1, 2, 4, 16), V("loc", -1))),
            ("Setup_MoreDetails", new[] { 3, 4, 5, 6 }, None),
            ("HeliSpecific_HeliHazardAlert", new[] { 2 }, P(V("heli_hazard_alert_type", 4))),
            ("D_Day", null, None),
            ("DispIntroRace", null, None),
        };
        var locs = new List<(int region, int loc)>();
        for (int r = 1; r < CopSpeechRules.MaxRoadNames; r++) if (CopSpeechRules.GetLocation(r, out int rg, out int lc)) locs.Add((rg, lc));
        var signs = CallSigns().ToList();
        var rng = new Random(5);
        int totalBad = 0;

        // cartesian product; "subject"/"speaker" = every call sign, "loc" = every named road's location
        IEnumerable<Dictionary<string, int>> Expand(IEnumerable<Dictionary<string, int>> cur, string p, int[] vals)
        {
            foreach (var d in cur)
            {
                if (p == "subject" || p == "speaker")
                    foreach (var (b, u) in signs) yield return new Dictionary<string, int>(d) { [p + "_battalion"] = b, [p + "_call_sign_id"] = u };
                else if (p == "loc")
                    foreach (var (rg, lc) in locs) yield return new Dictionary<string, int>(d) { ["location_region"] = rg, ["location"] = lc };
                else
                    foreach (var v in vals) yield return new Dictionary<string, int>(d) { [p] = v };
            }
        }

        foreach (var (ev, spk, pars) in tests)
        {
            if (!s.HasEvent(ev)) { Console.WriteLine($"{ev}: NOT IN SCRIPT"); totalBad++; continue; }
            IEnumerable<Dictionary<string, int>> set = new[] { new Dictionary<string, int>() };
            if (spk != null) set = Expand(set, "speaker_id", spk);
            foreach (var (p, vals) in pars) set = Expand(set, p, vals);
            int n = 0, bad = 0;
            var fails = new Dictionary<string, int>();
            foreach (var a in set)
            {
                n++;
                var r = s.Choose(ev, a, rng, false);
                if (r != null && r.Count > 0) continue;
                bad++;
                // group failures by the parameter values that are not call signs / locations
                string key = string.Join(",", a.Where(kv => !kv.Key.EndsWith("call_sign_id") && kv.Key != "location").Select(kv => kv.Key + "=" + kv.Value));
                fails[key] = fails.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            totalBad += bad;
            Console.WriteLine($"{ev}: {n - bad}/{n} ok  speakers [{string.Join(",", s.Speakers(ev))}]");
            foreach (var kv in fails.OrderByDescending(k => k.Value).Take(6)) Console.WriteLine($"    fail x{kv.Value}: {kv.Key}");
        }
        Console.WriteLine($"total failing parameter sets: {totalBad}");

        // call signs a primary can say (the AI hands out battalion + unit bits)
        foreach (var ev in new[] { "Setup_AttmptVehStp", "Setup_PrimaryEngage", "Backup_BUArrives", "AnytimeEvents_DispPursuitUpdate" })
        {
            var bad = new List<string>();
            foreach (var (b, u) in signs)
            {
                bool any = false;
                foreach (int sp in ev.StartsWith("AnytimeEvents_Disp") ? Disp : new[] { 3, 4, 5 })
                {
                    var a = new Dictionary<string, int> { ["speaker_id"] = sp, ["pursuit_type"] = 1, ["num_suspects"] = 1 };
                    string pre = ev.StartsWith("AnytimeEvents_Disp") ? "subject" : "speaker";
                    a[pre + "_battalion"] = b;
                    a[pre + "_call_sign_id"] = u;
                    var r = s.Choose(ev, a, rng, false);
                    if (r != null && r.Count > 0) any = true;
                }
                if (!any) bad.Add($"{b}/{u}");
            }
            Console.WriteLine($"{ev}: call signs no primary can say: {(bad.Count == 0 ? "none" : string.Join(" ", bad))}");
        }
    }

    static readonly string[] TuneNames = { "PlayerSmashSpeedRange","PursuitInactivityTimer","SpeechDropoffRamp","OutcomeFailSpeed","MinIntensitySideswipe","SpeedDiffForBlowby","BlowbyInterval","HangTimeForCommentary","BURemindTime","AIRacerProximity","MinIntensityCopSmash","MaxRangeFor180","MaxRangeForPrimaryBranch","MinIntensityTrafficSmash","MinPursuitDurationForBailouts","FlipTimeForCommentary","PriorityWeight","OutcomeTrackTime","RangeForSpotterBranch","RBOutcomeTimer","SpeedThreshFlyFlipIntensity","PursuitInitDelay","TimeWaitForSpotterReply","TimeConsideredLostNoLOS","RBPostOutcomeResetTime","MinHeightAirborne","MinHavocForSuspectBehavior","MinSpeedConsideredStopped","MinTimeConsideredStopped","PursuitDurationHighIntensity","CrashSlowdownPct","MinHealthForCommentary","CollisionMinClosingVelSq","MaxTimeFor180","SuspectOutrunRange","NoLOSCommentaryTime","HighIntensityMark","MinContigFramesFor180","CacheDisplayCoords" };

    static void Explore(AttribDatabase db)
    {
        var cls = db.Classes.TryGetValue(AttribDatabase.Hash("speech"), out var c) ? c : null;
        Console.WriteLine($"speech class: {(cls == null ? "missing" : cls.Fields.Count + " fields, layout " + cls.LayoutSize)}");
        var names = new[] { "BackTime","Clarity","CollectionName","CullingRange","DeadAir","DepFollow","DoNotDropout","EnforceDeadAir","InitDelay","Interruptable","Interval","MaxHeat","MaxPlayback","MaxPlayerSpeed","MinHeat","MinPlayerSpeed","OnScreenOnly","Pan","RadioChirp","RecallList","RedundancyCheckIsGlobal","SpeechID","cache_OnCreate","cache_SysInit","expiry","interrupt","priority","reqLOS" };
        if (cls != null)
            foreach (var f in cls.Fields.Values)
                Console.WriteLine($"  field {names.FirstOrDefault(n => AttribDatabase.Hash(n) == f.Key) ?? f.Key.ToString("X8")} type {f.Type:X8} off {f.Offset} size {f.Size} cnt {f.Count} fl {f.Flags}");
        foreach (var col in db.OfClass("speech").OrderBy(x => x.Name))
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"{col.Name ?? col.Key.ToString("X8")} parent {col.ParentKey:X8}:");
            foreach (var n in names)
            {
                if (!col.TryGet(n, out int at, out int count, out var def)) continue;
                var d = col.Source.Data;
                string v;
                if (n == "DepFollow" || n == "RecallList") v = "[" + string.Join(",", col.Refs(n).Select(r => db.Names.TryGetValue(r.key, out var nm) ? nm : r.key.ToString("X8"))) + "]";
                else if (n == "CollectionName") v = col.Text(n);
                else if (def.Size == 1) v = ((sbyte)d[at]).ToString();
                else if (def.Size == 2) v = BitConverter.ToInt16(d, at).ToString();
                else if (n == "SpeechID" || n == "MaxPlayback") v = BitConverter.ToInt32(d, at).ToString();
                else v = BitConverter.ToSingle(d, at).ToString("0.###");
                sb.Append($" {n}={v}");
            }
            Console.WriteLine(sb);
        }
        foreach (var col in db.OfClass("speechtune"))
        {
            Console.WriteLine($"speechtune {col.Name ?? col.Key.ToString("X8")}");
            foreach (var f in col.Class.Fields.Values)
            {
                if (!col.TryGetKey(f.Key, out int at, out int count, out var def)) continue;
                var d = col.Source.Data;
                var vals = new List<string>();
                for (int i = 0; i < Math.Max(1, count); i++)
                    vals.Add(def.Size == 1 ? ((sbyte)d[at + i]).ToString() : def.Size == 4 && f.Type != AttribDatabase.Hash("EA::Reflection::Int32") ? BitConverter.ToSingle(d, at + i * 4).ToString("0.###") : BitConverter.ToInt32(d, at + i * 4).ToString());
                if (def.Size == 8) vals = new List<string> { BitConverter.ToSingle(d, at).ToString("0.###"), BitConverter.ToSingle(d, at + 4).ToString("0.###") };
                Console.WriteLine($"  {TuneNames.FirstOrDefault(n => AttribDatabase.Hash(n) == f.Key) ?? f.Key.ToString("X8")} type {f.Type:X8} size {def.Size} = {string.Join(", ", vals)}");
            }
        }
    }
}
