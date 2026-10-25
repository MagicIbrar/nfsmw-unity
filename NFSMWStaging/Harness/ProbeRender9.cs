using System;
using System.IO;
using NfsMw.Core;

/// <summary>Round 9 world/car render probes: `render9 tireoff [car...]` dumps the raw ecar TireOffsets array.</summary>
static class ProbeRender9
{
    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        string sub = args.Length > 2 ? args[2] : "tireoff";
        if (sub == "tirebox")
        {
            for (int a = 3; a < args.Length; a++)
            {
                var asset = CarAsset.Load(root, args[a].ToUpperInvariant());
                foreach (var so in asset.Solids)
                    if (so.Name.Contains("TIRE_") || so.Name.Contains("BRAKE_A"))
                        Console.WriteLine($"{so.Name,-36} min {so.BoundsMin.X:F3} {so.BoundsMin.Y:F3} {so.BoundsMin.Z:F3} max {so.BoundsMax.X:F3} {so.BoundsMax.Y:F3} {so.BoundsMax.Z:F3}");
            }
            var w = CarAsset.Load(root, "WHEELS");
            int n = 0;
            foreach (var so in w.Solids)
                if (so.Name.EndsWith("_A") && n++ < 6)
                    Console.WriteLine($"{so.Name,-36} min {so.BoundsMin.X:F3} {so.BoundsMin.Y:F3} {so.BoundsMin.Z:F3} max {so.BoundsMax.X:F3} {so.BoundsMax.Y:F3} {so.BoundsMax.Z:F3}");
            return;
        }
        if (sub == "wheels")
        {
            // render9 wheels <car> <kit>: WheelRenderGeometry from the KIT00 front/rear tyre models
            string car = args[3].ToUpperInvariant();
            int kit = args.Length > 4 ? int.Parse(args[4]) : 0;
            var asset = CarAsset.Load(root, car);
            var ft = asset.Solids.Find(x => x.Name == car + "_KIT00_FRONT_TIRE_A");
            var rt = asset.Solids.Find(x => x.Name == car + "_KIT00_REAR_TIRE_A") ?? ft;
            float[] w = { ft.BoundsMax.Y - ft.BoundsMin.Y, rt.BoundsMax.Y - rt.BoundsMin.Y }, r = { (ft.BoundsMax.X - ft.BoundsMin.X) * 0.5f, (rt.BoundsMax.X - rt.BoundsMin.X) * 0.5f };
            var fm = ft.Marker("FRONT_BRAKE"); var rm = rt.Marker("REAR_BRAKE") ?? fm;
            float[] m = { fm != null ? fm[13] : 0f, rm != null ? rm[13] : 0f };
            var e = db.Get("ecar", car.ToLowerInvariant());
            if (e != null && e.TryGet("KitWheelOffsetFront", out int at, out int cnt, out _)) { var sb = ""; for (int i = 0; i < cnt; i++) sb += e.Source.Data[at + i] + " "; Console.WriteLine("KitWheelOffsetFront " + sb); }
            if (e != null && e.TryGet("KitWheelOffsetRear", out at, out cnt, out _)) { var sb = ""; for (int i = 0; i < cnt; i++) sb += e.Source.Data[at + i] + " "; Console.WriteLine("KitWheelOffsetRear " + sb); }
            var ws = new WheelRenderGeometry.Wheel[4];
            Console.WriteLine($"W {w[0]:F3}/{w[1]:F3} R {r[0]:F3}/{r[1]:F3} brake marker {m[0]:F3}/{m[1]:F3} ok={WheelRenderGeometry.Compute(db, car, kit, w, r, m, ws)}");
            for (int i = 0; i < 4; i++) Console.WriteLine($"  game wheel {i}: x {ws[i].X:F3} y {ws[i].Y:F3} rs {ws[i].RadiusScale:F3} ws {ws[i].WidthScale:F3} p {ws[i].Pivot:F3} brakeY {ws[i].BrakeY:F3}");
            return;
        }
        if (sub == "extrarear")
        {
            int n = 0, has = 0;
            foreach (var c in db.OfClass("ecar"))
            {
                n++;
                bool fieldInClass = c.Class != null && c.Class.Fields.ContainsKey(AttribDatabase.Hash("ExtraRearTireOffset"));
                if (c.Has("ExtraRearTireOffset")) { has++; Console.WriteLine($"{c.Name}: ExtraRearTireOffset {c.Float("ExtraRearTireOffset")}"); }
                else if (n == 1) Console.WriteLine($"field in ecar class: {fieldInClass}");
            }
            Console.WriteLine($"ecar collections {n}, with ExtraRearTireOffset {has}");
            return;
        }
        if (sub == "tireoff")
        {
            for (int a = 3; a < args.Length; a++)
            {
                var pv = db.Get("pvehicle", args[a]);
                var ch = db.Get("chassis", args[a]);
                if (ch != null) Console.WriteLine($"{args[a]}: chassis TRACK_WIDTH {string.Join(",", ch.Floats("TRACK_WIDTH"))} WHEEL_BASE {ch.Float("WHEEL_BASE")} FRONT_AXLE {ch.Float("FRONT_AXLE")}");
                var e = db.Get("ecar", args[a]);
                if (e == null) { Console.WriteLine(args[a] + ": no ecar"); continue; }
                foreach (var f in new[] { "TireOffsets", "TireSkidWidth", "TireSkidWidthKitScale", "KitWheelOffsetFront", "KitWheelOffsetRear", "ExtraRearTireOffset" })
                {
                    if (!e.TryGet(f, out int at, out int count, out var def)) { Console.WriteLine($"  {f}: -"); continue; }
                    var d = e.Source.Data;
                    int raw = def.IsArray ? at - 8 : at;
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < 24 && raw + i * 4 + 4 <= d.Length; i++) sb.Append(Chunks.F32(d, raw + i * 4).ToString("0.###")).Append(' ');
                    Console.WriteLine($"  {args[a]} {f} array={def.IsArray} size={def.Size} count={count} src={e.Source.Name} hdr={Chunks.U16(d, raw)},{Chunks.U16(d, raw + 2)},{Chunks.U16(d, raw + 4)},{Chunks.U16(d, raw + 6)} | {sb}");
                }
            }
        }
    }
}
