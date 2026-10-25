using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// nis chars [scene]   : NIS characters (entities -> skeleton, skeletal anim, skinned solid, textures) + a posed bbox
// nis events [scene]  : event sequencer packs (engines, systems, states, actions, decoded events) + a dry run
// nis world           : TRACKS\L2RA world animation trees, their anims, and which stream section has each model
static class ProbeNis
{
    public static void Run(string root, string[] args)
    {
        string sub = args.Length > 2 ? args[2] : "chars";
        string only = args.Length > 3 ? args[3] : null;
        NisLibrary.Load(root);
        switch (sub)
        {
            case "chars": Chars(only); break;
            case "events": Events(only); break;
            case "world": World(root); break;
            case "worldmodels": WorldModels(root); break;
            case "hdr": Headers(root, only); break;
        }
    }

    // nis hdr [scene] : scene headers (SceneType, StartFrame, VanishFrame, HaveCarAnimation, overlay), the channels present,
    //                   the special car list and the first/last pose of car1 (CAnimScene::Init frame rules)
    static void Headers(string root, string only)
    {
        var cand = NisCandidateData.Load(root);
        foreach (var scene in Scenes(only))
        {
            var chans = new List<string>();
            for (int i = 1; i <= 8; i++) { if (scene.HasTrack("Car" + i)) chans.Add("Car" + i); if (scene.HasTrack("Cop" + i)) chans.Add("Cop" + i); }
            string pose = "";
            if (scene.PoseFull("Car1", 0f, out var p0, out var q0) && scene.PoseFull("Car1", scene.Length, out var p1, out _))
                pose = $" car1 ({p0.X:0.0},{p0.Y:0.0},{p0.Z:0.0}) q({q0[0]:0.00},{q0[1]:0.00},{q0[2]:0.00},{q0[3]:0.00}) -> ({p1.X:0.0},{p1.Y:0.0},{p1.Z:0.0})";
            Console.WriteLine($"{scene.Name,-18} type {scene.SceneType} layout {scene.HaveLayout} cars {scene.HaveCarAnimation}/{scene.Actors} frames {scene.StartFrame}..{scene.Frames} ({scene.StartTime:0.00}-{scene.Length:0.00} s) overlay '{scene.OverlayName}' list {cand.CarList(scene.Name)} marker {cand.MarkerType(scene.Name)} [{string.Join(",", chans)}]{pose}");
        }
    }

    static IEnumerable<NisScene> Scenes(string only) =>
        NisLibrary.Scenes.Values.Where(s => only == null || s.Name.StartsWith(only, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name);

    static void Chars(string only)
    {
        int ok = 0, total = 0;
        foreach (var scene in Scenes(only))
        {
            var c = NisContent.For(scene);
            if (c.Characters.Count == 0) continue;
            var texHashes = new HashSet<uint>(c.Textures.Select(t => t.Hash));
            Console.WriteLine($"{scene.Name} '{scene.Description}' frames {scene.Frames}: {c.Characters.Count} chars, {c.Skeletons.Count} skels [{string.Join(",", c.Skeletons.Select(s => s.Name + "/" + s.Bones.Length))}], {c.Solids.Count} solids [{string.Join(",", c.Solids.Values.Select(s => s.Name))}], {c.Anims.Count} anims, {c.Textures.Count} tex");
            foreach (var ch in c.Characters)
            {
                total++;
                var sk = c.Skeleton(ch.SkelName);
                var an = c.Anim(ch.SkelAnim);
                var so = c.Solid(ch);
                int parts = so?.Parts.Count ?? 0, texOk = so?.Parts.Count(p => texHashes.Contains(p.TextureHash)) ?? 0;
                int verts = so?.Parts.Sum(p => p.VertexCount) ?? 0, maxBone = so?.Parts.Max(p => p.Bones.Max()) ?? 0;
                string pose = "";
                if (sk != null && an != null && so != null)
                {
                    ok++;
                    var sqt = sk.StillPose();
                    var g = new float[sk.Bones.Length][];
                    var skin = new float[sk.Bones.Length][];
                    foreach (float frame in new[] { 0f, an.Length * 0.5f, an.Length })
                    {
                        sqt = sk.StillPose();
                        an.Eval(frame, sqt);
                        sk.PoseGlobal(sqt, g);
                        sk.Skin(g, skin);
                        float minX = 1e9f, minY = 1e9f, minZ = 1e9f, maxX = -1e9f, maxY = -1e9f, maxZ = -1e9f;
                        foreach (var p in so.Parts)
                            for (int i = 0; i < p.VertexCount; i++)
                            {
                                float x = 0, y = 0, z = 0;
                                for (int k = 0; k < 3; k++)
                                {
                                    float w = p.Weights[i * 3 + k];
                                    if (w == 0) continue;
                                    var v = Mat.Point(skin[p.Bones[i * 3 + k]], p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]);
                                    x += v.X * w; y += v.Y * w; z += v.Z * w;
                                }
                                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                            }
                        pose += $" f{frame:0}: x {minX:0.00}..{maxX:0.00} y {minY:0.00}..{maxY:0.00} z {minZ:0.00}..{maxZ:0.00};";
                    }
                }
                var m = ch.LocalMatrix;
                Console.WriteLine($"  ent {ch.InstanceHash:X8} flags {ch.PlayFlags:X} ctrl {ch.CtrlFlags:X} ts {ch.TimeScale} at ({m[12]:0.00},{m[13]:0.00},{m[14]:0.00}) skel {(sk != null ? sk.Name : $"MISSING {ch.SkelName:X8}")} anim {(an != null ? $"{an.Name} {an.NumFrames}f ch[{string.Join(",", an.Channels.Select(x => x.Type))}]{(an.UnknownChannelTypes.Count > 0 ? " unknown " + string.Join(",", an.UnknownChannelTypes) : "")}" : $"MISSING {ch.SkelAnim:X8}")} solid {(so != null ? $"{so.Name} {parts} parts {verts} v tex {texOk}/{parts} maxbone {maxBone}" : $"MISSING {ch.LodHashes[0]:X8}")}");
                if (pose.Length > 0) Console.WriteLine("   " + pose);
            }
        }
        Console.WriteLine($"characters resolved: {ok}/{total}");
        // car tracks through the general decoder vs the old one
        int same = 0, cmp = 0;
        foreach (var scene in Scenes(only))
        {
            var c = NisContent.For(scene);
            foreach (var kv in scene.Tracks)
            {
                var a = c.Anim(EaHash.Hash(kv.Key));
                if (a == null || a.Channels.Count == 0 || !(a.Channels[0] is EaglDeltaChannel dc)) continue;
                cmp++;
                bool eq = dc.Keys.Length == kv.Value.Keys.Length;
                for (int k = 0; eq && k < dc.Keys.Length; k++)
                    for (int i = 0; i < kv.Value.Dofs; i++) if (Math.Abs(dc.Keys[k][i] - kv.Value.Keys[k][i]) > 1e-4f) { eq = false; break; }
                if (eq) same++;
            }
        }
        Console.WriteLine($"car/cop tracks matching the old decoder: {same}/{cmp}");
    }

    static void Events(string only)
    {
        var counts = new Dictionary<string, int>();
        int filters = 0, unknown = 0;
        foreach (var scene in Scenes(only))
        {
            var c = NisContent.For(scene);
            if (c.Events == null) continue;
            foreach (var e in c.Events.Engines)
            {
                Console.WriteLine($"{scene.Name}: engine '{e.Name}' {e.Systems.Length} systems");
                for (int i = 0; i < e.Systems.Length; i++)
                {
                    var s = e.Systems[i];
                    if (s == null) { Console.WriteLine($"  sys {e.SystemIds[i]:X8} MISSING"); continue; }
                    Console.WriteLine($"  sys {e.SystemIds[i]:X8} init {s.InitialState:X8} states {s.States.Count}");
                    foreach (var st in s.States)
                    {
                        if (st.Value == null) continue;
                        if (st.Value.HasFilter) filters++;
                        foreach (var r in st.Value.Responses)
                        {
                            var a = r.Value.action;
                            Console.WriteLine($"    state {st.Key:X8} on {r.Key:X8} -> {r.Value.endState:X8} action {a?.Name:X8} dur {a?.Duration:0.00} tags [{string.Join(",", a?.Tags.Select(t => t.ToString("X8")) ?? new string[0])}] times [{string.Join(",", a?.Times.Select(t => t.ToString("0.00")) ?? new string[0])}]");
                            if (a == null) continue;
                            for (int l = 0; l < a.Lists.Length; l++)
                                foreach (var ev in a.Lists[l])
                                {
                                    if (ev.Name == null) unknown++;
                                    string key = ev.Name ?? ev.Id.ToString("X8");
                                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                                    Console.WriteLine($"      [{(l < a.Tags.Length ? "tag " + a.Tags[l].ToString("X8") : "t=" + a.Times[l - a.Tags.Length].ToString("0.00"))}] {Describe(ev)}");
                                }
                        }
                    }
                }
                // dry run: create, START, update at 30 Hz to the scene length
                var fired = new List<string>();
                var eng = new EventSeqEngine(e, 0) { OnEvent = (sys, ev) => fired.Add($"{sys:X8}:{ev}") };
                eng.Reset(0);
                eng.ProcessStimulus(SeqIds.StartStimulus, 0, EventSeqEngine.QueueMode.Allow);
                for (float t = 0; t <= scene.Length + 1; t += 1 / 30f) eng.Update(t);
                Console.WriteLine($"  dry run: {fired.Count} events fired, still in action: {eng.AnySystemInAction()}");
            }
        }
        Console.WriteLine($"event types: {string.Join(", ", counts.OrderByDescending(k => k.Value).Select(k => k.Key + " " + k.Value))}");
        Console.WriteLine($"states with filters: {filters}, unknown event ids: {unknown}");
    }

    static string Describe(SeqEvent e)
    {
        switch (e.Name)
        {
            case "ENISOverlayMessage": return $"{e} '{e.S(4)}' '{e.S(8)}'";
            case "ENISHideCharacter": return $"{e} '{e.S(4)}' show {e.I(8)}";
            case "ENISPlayEffect": return $"{e} '{e.S(4)}' at ({e.F(8):0.0},{e.F(12):0.0},{e.F(16):0.0}) mag ({e.F(20):0.0},{e.F(24):0.0},{e.F(28):0.0})";
            case "EPlayObjectEffect": return $"{e} '{e.S(4)}' pos {e.U(8):X8} int {e.F(12):0.00} id {e.U(16):X8} track {e.U(20)}";
            case "ENISWolrdGeometry": return $"{e} '{e.S(4)}' enable {e.I(8)}";
            case "ENISPixelate": return $"{e} on {e.I(4)} {e.F(8):0}x{e.F(12):0}";
            case "ENISLights": return $"{e} cop {e.U(4)} head {e.U(8)}";
            case "ENISCopCarDoors": return $"{e} door {e.I(4)} pos {e.F(8):0.00} len {e.F(12):0.00} end {e.F(16):0.00}";
            default:
                var f = new List<string>();
                for (int o = 4; o + 4 <= Math.Min(e.Size, 40); o += 4) f.Add(Math.Abs(e.F(o)) > 1e-6f && Math.Abs(e.F(o)) < 1e6f ? e.F(o).ToString("0.###") : e.U(o).ToString());
                return $"{e} {string.Join(" ", f)}";
        }
    }

    static void World(string root)
    {
        var bun = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        var set = WorldAnimSet.Load(bun);
        Console.WriteLine($"{set.Trees.Count} trees, {set.Anims.Count} anims");
        using var stream = new TrackStream(root);
        foreach (var t in set.Trees)
        {
            var i = t.Instance;
            var sec = stream.Sections.Find(s => s.Number == i.Section);
            var solids = sec != null ? stream.Load(sec, false, true, true) : null;
            Console.WriteLine($"tree {t.Hash:X8} section {i.Section} ({sec?.Name}) flags {i.PlayFlags:X} speed {i.Speed:0.#####} delay {i.MasterDelay}/{i.LoopDelay} at ({i.Matrix[12]:0.0},{i.Matrix[13]:0.0},{i.Matrix[14]:0.0})");
            foreach (var e in t.Entities)
            {
                var solid = solids?.Solids.Find(s => s.Hash == e.ModelHash);
                bool inScenery = solids != null && solids.Scenery.Any(g => g.Infos.Any(inf => inf.SolidHashes.Contains(e.ModelHash)));
                var m = new float[16];
                var sqt = new float[24];
                string pose = set.Pose(e, 0, sqt, m) ? $"pose0 ({m[12]:0.0},{m[13]:0.0},{m[14]:0.0})" : "no anim";
                Console.WriteLine($"  ent {e.InstanceHash:X8} root {e.IsRoot} model {e.ModelHash:X8} {(solid != null ? solid.Name + " in section" : "not in section")}{(inScenery ? " (also a scenery info)" : "")} anim {e.AnimHash:X8} flags {e.ContentFlags} len {set.Length(e):0} {pose}");
            }
        }
    }

    static void WorldModels(string root)
    {
        var bun = File.ReadAllBytes(Path.Combine(root, "TRACKS", "L2RA.BUN"));
        var set = WorldAnimSet.Load(bun);
        var want = new HashSet<uint>(set.Trees.SelectMany(t => t.Entities).Select(e => e.ModelHash).Where(h => h != 0));
        foreach (var so in SolidList.ReadAll(bun, 0, bun.Length)) if (want.Contains(so.Hash)) Console.WriteLine($"L2RA.BUN has {so.Name} {so.Hash:X8}");
        using var stream = new TrackStream(root);
        int scanned = 0, failed = 0, solidsSeen = 0;
        var names = new HashSet<string>();
        foreach (var sec in stream.Sections)
        {
            TrackSectionData data;
            try { data = stream.Load(sec, false, true, true); scanned++; } catch (Exception) { failed++; continue; }
            solidsSeen += data.Solids.Count;
            foreach (var so in data.Solids)
            {
                if (want.Contains(so.Hash)) Console.WriteLine($"section {sec.Name} ({sec.Number}) has {so.Name} {so.Hash:X8}");
                string n = so.Name.ToUpperInvariant();
                if (n.Contains("CRANE") || n.Contains("BLIMP") || n.Contains("AIRLINER") || n.Contains("SAILBOAT") || n.Contains("CARGO")) names.Add(so.Name + " " + so.Hash.ToString("X8") + " @" + sec.Name);
            }
            foreach (var g in data.Scenery)
                foreach (var inst in g.Instances)
                {
                    var info = inst.InfoIndex >= 0 && inst.InfoIndex < g.Infos.Count ? g.Infos[inst.InfoIndex] : null;
                    if (info != null && info.SolidHashes.Any(want.Contains)) Console.WriteLine($"  section {sec.Name} scenery instance of {info.Name} at {inst.Position} flags {inst.Flags:X8}");
                }
        }
        Console.WriteLine($"scanned {scanned} sections ({failed} failed), {solidsSeen} solids; wanted {string.Join(",", want.Select(h => h.ToString("X8")))}");
        foreach (var n in names.OrderBy(x => x)) Console.WriteLine("  " + n);
    }
}
