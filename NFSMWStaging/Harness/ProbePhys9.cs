using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// phys9 chopper|pvfields <car>: round-9 physics probes (physics agent).
//   chopper   the copheli pvehicle's chopperspecs / rigidbodyspecs / damagespecs as SimpleChopper reads them
static class ProbePhys9
{
    static readonly string[] ChopperFields =
    {
        "AIR_RESISTANCE_SCALE", "YAW_STRENGTH_FRONT", "YAW_DAMP", "YAW_LIMIT_FRONT", "PITCH_ALIGN_SCALE", "AIR_RESISTANCE",
        "PITCH_STOP_SPEED", "YAW_BOOST_LIMIT", "ROLL_SPEED_MIN", "ROLL_SLOW_DOWN_RATE", "ROLL_START_SPEED", "PITCH_ANG",
        "YAW_STRENGTH_REAR", "ROLL_ANG", "MAX_SPEED_MPS", "STRAFE_SCALEY", "STRAFE_SCALEX", "YAW_LIMIT_REAR", "TURN_BOOST_SPEED",
        "PITCH_SLOW_DOWN_RATE", "DRIVE_SPEED", "ROLL_ALIGN_SCALE", "SAME_ALIGN_SCALE", "SCALE_STEERING",
    };

    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        string what = args.Length > 2 ? args[2] : "chopper";
        if (what == "chopper") Chopper(db, args.Length > 3 ? args[3] : "copheli");
        else if (what == "mech") Mech(db);
        else if (what == "trigflags") TrigFlags();
        else if (what == "pclouds") PClouds(root);
        else if (what == "pcdump") PcDump(root, args.Length > 3 ? args[3] : "BMWM3GTRE46");
        else if (what == "pvfields") PvFields(db, args.Length > 3 ? args[3] : "copheli");
    }

    static void Chopper(AttribDatabase db, string car)
    {
        var pv = db.Get("pvehicle", car);
        Console.WriteLine($"pvehicle/{car}: {pv}");
        if (pv == null) return;
        uint cls = AttribDatabase.Hash("chopperspecs");
        Console.WriteLine($"chopperspecs class hash {cls:X8}");
        foreach (var c in db.Collections.Values.Where(c => c.Class != null && c.Class.Key == cls))
        {
            Console.WriteLine($"chopperspecs/{c.Name ?? c.Key.ToString("X8")} parent {c.Parent?.Name}");
            foreach (var f in ChopperFields)
                if (c.TryGet(f, out int at, out int n, out _))
                    Console.WriteLine($"   {f} = {string.Join(",", Enumerable.Range(0, Math.Max(1, Math.Min(n, 4))).Select(k => Chunks.F32(c.Source.Data, at + 4 * k).ToString("0.####")))}");
        }
        foreach (var r in new[] { "chopperspecs", "rigidbodyspecs", "damagespecs", "engine", "MODEL", "MASS", "TENSOR_SCALE" })
        {
            try { var refs = pv.Refs(r); Console.WriteLine($" {r}: refs {string.Join(",", refs.Select(x => $"{x.cls:X8}/{x.key:X8}"))}"); } catch { }
            try { Console.WriteLine($" {r}: float {pv.Float(r):0.###} text '{pv.Text(r)}'"); } catch { }
        }
    }

    static void PvFields(AttribDatabase db, string car)
    {
        var pv = db.Get("pvehicle", car);
        if (pv?.Class == null) { Console.WriteLine("no pvehicle"); return; }
        for (var c = pv; c != null; c = c.Parent)
            foreach (var f in c.Class.Fields)
            {
                if (!c.TryGetKey(f.Key, out int at, out int n, out _)) continue;
                string fn = db.Names.TryGetValue(f.Key, out var nm) ? nm : f.Key.ToString("X8");
                var vals = Enumerable.Range(0, Math.Min(n, 8)).Select(k => Chunks.U32(c.Source.Data, at + 4 * k).ToString("X8"));
                Console.WriteLine($"{c.Name}: {fn} [{n}] {string.Join(" ", vals)}");
            }
    }
    static readonly string[] Behaviors =
    {
        "AIVehicle", "AIVehicleCopCar", "AIVehicleEmpty", "AIVehicleHelicopter", "AIVehicleHuman", "AIVehicleRacecar", "AIVehicleTraffic",
        "DamageCopCar", "DamageDragster", "DamageHeli", "DamageRacer", "DamageVehicle", "DrawCopCar", "DrawHeli", "DrawNISCar", "DrawRaceCar",
        "DrawTraffic", "EffectsCar", "EffectsFragment", "EffectsPlayer", "EffectsSmackable", "EffectsVehicle", "EngineDragster", "EngineRacer",
        "EngineSpline", "EngineTraffic", "InputNIS", "InputPlayer", "InputPlayerDrag", "PInput", "RBCop", "RBTractor", "RBTrailer", "RBVehicle",
        "ResetCar", "RigidBody", "SimpleChopper", "SimpleRigidBody", "SoundCop", "SoundHeli", "SoundRacer", "SoundTraffic", "SpikeStrip",
        "SuspensionRacer", "SuspensionSimple", "SuspensionSpline", "SuspensionTraffic", "SuspensionTrailer",
    };
    static readonly string[] Mechanics = { "AI", "AUDIO", "DAMAGE", "DRAW", "EFFECTS", "ENGINE", "INPUT", "RESET", "RIGIDBODY", "SUSPENSION" };

    /// mech: every pvehicle's BEHAVIOR_MECHANIC_* behaviour names (own or inherited), grouped by identical rows
    static void Mech(AttribDatabase db)
    {
        var names = new Dictionary<uint, string>();
        foreach (var b in Behaviors) names[AttribDatabase.Hash(b)] = b;
        var rows = new SortedDictionary<string, List<string>>();
        foreach (var c in db.OfClass("pvehicle"))
        {
            var parts = new List<string>();
            foreach (var m in Mechanics)
            {
                uint key = AttribDatabase.Hash("BEHAVIOR_MECHANIC_" + m);
                string v = "-";
                for (var x = c; x != null; x = x.Parent)
                    if (x.TryGetKey(key, out int at, out int n, out _) && n > 0)
                    {
                        v = x.StringKey("BEHAVIOR_MECHANIC_" + m) ?? "(null)";
                        if (n > 1) v += $"[{n}]";
                        break;
                    }
                parts.Add(m + "=" + v);
            }
            string row = string.Join(" ", parts);
            if (!rows.TryGetValue(row, out var l)) rows[row] = l = new List<string>();
            l.Add(c.Name ?? c.Key.ToString("X8"));
        }
        foreach (var kv in rows) Console.WriteLine($"{kv.Key}\n    {kv.Value.Count}: {string.Join(",", kv.Value.Take(30))}");
    }

    /// pcdump <MODEL>: the bytes after a vehicle collection's bounds array (PCloudHeader / PCloud / vertices)
    static void PcDump(string root, string model)
    {
        var d = File.ReadAllBytes(Path.Combine(root, "GLOBAL", "GLOBALB.BUN"));
        uint want = AttribDatabase.Hash(model);
        foreach (var pack in Chunks.FindAll(d, 0, d.Length, CollisionGeometry.ChunkPack))
            foreach (var c in Chunks.Children(d, pack))
            {
                if (c.Id != CollisionGeometry.ChunkCollection) continue;
                int o = c.DataStart;
                while (o < c.End && d[o] == 0x11) o++;
                if (Chunks.U32(d, o) != want) continue;
                int n = Chunks.I32(d, o + 4);
                int after = o + 16 + n * 0x30;
                Console.WriteLine($"{model}: chunk {c.DataStart:X}-{c.End:X}, {n} bounds, tail at {after:X} ({c.End - after} bytes)");
                for (int k = after; k < Math.Min(c.End, after + 0x200); k += 16)
                {
                    var sb = new System.Text.StringBuilder($"{k - after:X4}: ");
                    for (int j = 0; j < 16 && k + j < c.End; j += 4) sb.Append($"{Chunks.U32(d, k + j):X8} ({Chunks.F32(d, k + j):0.###})  ");
                    Console.WriteLine(sb);
                }
            }
    }

    /// pclouds: per vehicle collection, the ground primitives (MeshVsGround clouds, PrimVsGround boxes) and the cloud's
    /// lowest point against the lowest PrimVsWorld/Objects box bottom (both from the body origin)
    static void PClouds(string root)
    {
        var g = CollisionGeometry.Read(File.ReadAllBytes(Path.Combine(root, "GLOBAL", "GLOBALB.BUN")));
        var adb = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        var names = new Dictionary<uint, string>();
        foreach (var pv in adb.OfClass("pvehicle")) { var m = pv.StringKey("MODEL"); if (m != null) names[AttribDatabase.Hash(m)] = m; }
        int with = 0;
        foreach (var col in g.ByName.Values)
        {
            var r = col.Bounds[0];
            int meshes = 0, gboxes = 0; float low = 99, boxLow = 99;
            foreach (var b in col.Bounds)
            {
                if ((b.Flags & CollisionGeometry.BoundFlags.MeshVsGround) != 0 && b.PointCloud >= 0 && b.PointCloud < col.PointClouds.Length)
                { meshes++; foreach (var v in col.PointClouds[b.PointCloud]) low = Math.Min(low, v.Y); }
                if ((b.Flags & CollisionGeometry.BoundFlags.PrimVsGround) != 0) gboxes++;
                if ((b.Flags & CollisionGeometry.BoundFlags.Box) != 0 && (b.Flags & (CollisionGeometry.BoundFlags.PrimVsWorld | CollisionGeometry.BoundFlags.PrimVsObjects)) != 0)
                    boxLow = Math.Min(boxLow, (b.Equals(r) ? 0 : b.Position.Y) - Math.Abs(b.HalfDims.Y));
            }
            if (meshes > 0) with++;
            Console.WriteLine($"{(names.TryGetValue(col.Name, out var nm) ? nm : col.Name.ToString("X8"))} clouds {col.PointClouds.Length} ({string.Join("/", col.PointClouds.Select(c => c.Length))}) meshNodes {meshes} groundBoxes {gboxes} cloudLow {low:0.###} boxLow {boxLow:0.###} pivotY {r.Pivot.Y:0.###}");
        }
        Console.WriteLine($"{with} of {g.ByName.Count} have a MeshVsGround cloud");
    }

    /// trigflags: RBVehicle::GetTriggerFlags against the gameplay triggers' flags (ProcessRB's (fFlags >> 8) & flags)
    static void TrigFlags()
    {
        var t = new WTrigger { Flags = WTrigger.GameplayFlags | WTrigger.FlagActivated };
        bool ok = true;
        void Check(string what, bool got, bool want) { Console.WriteLine($"{(got == want ? "ok  " : "FAIL")} {what}: {got}"); ok &= got == want; }
        Check("player car 0x64", t.Activates(WTrigger.VehicleTriggerFlags(true, false, false)), true);
        Check("AI racer 0x68", t.Activates(WTrigger.VehicleTriggerFlags(false, false, false)), true);
        Check("cop 0x20068", t.Activates(WTrigger.VehicleTriggerFlags(false, true, false)), true);
        Check("asleep 0", t.Activates(WTrigger.VehicleTriggerFlags(true, false, true)), false);
        Check("smackable 0x40 (SimpleRigidBody)", t.Activates(0x40), false);
        Check("legacy 0x10000 stand-in", t.Activates(WTrigger.FlagActivateCars), true);
        Console.WriteLine(ok ? "trigflags: all checks passed" : "trigflags: FAILED");
    }
}
