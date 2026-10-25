using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;

// `wtrigger [selftest|list [bin]]`: the WTrigger port (Core/WTrigger.cs; World/Common/WTrigger.cpp, GTrigger ctor 0x601790)
// over gameplay.bin's trigger records — the shapes the ctor derives (box / cylinder / plane), and synthetic rigid bodies
// driven through them (enter at once, the fire-on-exit list, the velocity look-ahead, directional and one-shot flags).
static class WTriggerProbe
{
    static Vec3 Frame(Vec3 g) => new Vec3(g.X, g.Z, g.Y);   // the Unity frame: x = game X, y = game Z (up), z = game Y

    public static void Run(string root, string[] args)
    {
        string what = args.Length > 2 ? args[2] : "selftest";
        var career = CareerData.Load(root);
        var db = career.Db;
        uint trigger = AttribDatabase.Hash("trigger");
        var trigs = db.Collections.Values.Where(c => c.Name != null && DerivesFrom(db, c, trigger) && c.Has("Position")).OrderBy(c => c.Name).ToList();
        if (what == "list")
        {
            string bin = args.Length > 3 ? args[3] : null;
            int shown = 0;
            foreach (var c in trigs)
            {
                if (bin != null && !c.Name.Contains(bin)) continue;
                var t = WTrigger.FromGameplayRecord(c, Frame);
                Console.WriteLine($"{c.Name,-60} {t.Shape,-8} w {t.Width,6:0.0} l {t.Length,6:0.0} h {t.Height,6:0.0} r {t.Radius,6:0.0} flags {t.Flags:X7}{(c.Has("FireOnExit") && c.Bool("FireOnExit") ? " FireOnExit" : "")}");
                if (++shown >= 400) break;
            }
            return;
        }
        int fails = 0;
        void Check(bool ok, string s) { Console.WriteLine((ok ? "ok   " : "FAIL ") + s); if (!ok) fails++; }

        var shapes = trigs.Select(c => WTrigger.FromGameplayRecord(c, Frame)).ToList();
        int boxes = shapes.Count(t => t.Shape == WTrigger.ShapeKind.Box && t.Length != 1f), cyl = shapes.Count(t => t.Shape == WTrigger.ShapeKind.Cylinder), planes = shapes.Count(t => t.Shape == WTrigger.ShapeKind.Box && t.Length == 1f && t.Height == 100f);
        Console.WriteLine($"{trigs.Count} trigger records: {boxes} boxes, {cyl} radius (cylinder) triggers, {planes} planes");
        Check(trigs.Count > 500 && cyl > 100 && boxes > 100, "gameplay.bin trigger shapes derived");
        // a radius trigger: an engage trigger (Radius 25) → cylinder 25, height 100, width/length 50
        var engage = trigs.FirstOrDefault(c => c.Name.Contains("/engage/engagetrigger"));
        if (engage != null)
        {
            var t = WTrigger.FromGameplayRecord(engage, Frame);
            Check(t.Shape == WTrigger.ShapeKind.Cylinder && Math.Abs(t.Radius - engage.Float("Radius")) < 1e-3 && Math.Abs(t.Height - 4 * t.Radius) < 1e-3, $"{engage.Name}: radius {t.Radius} height {t.Height}");
        }
        // a box trigger (Dimensions): width = y, length = x, height = 2 z
        var box = trigs.FirstOrDefault(c => c.Has("Dimensions") && c.Vector3("Dimensions").Z > 0);
        if (box != null)
        {
            var d = box.Vector3("Dimensions");
            var t = WTrigger.FromGameplayRecord(box, Frame);
            Check(t.Shape == WTrigger.ShapeKind.Box && t.Width == d.Y && t.Length == d.X && Math.Abs(t.Height - 2 * d.Z) < 1e-4, $"{box.Name}: dims {d} → w {t.Width} l {t.Length} h {t.Height} r {t.Radius:0.00}");
        }

        // synthetic: a box 10 wide, 2 long, 20 high at the origin facing +z (game: +Y); a car driving through it along +z
        var mgr = new WTriggerManager();
        var fired = new List<(string name, int stim)>();
        mgr.Fire = (w, sim, stim) => fired.Add((w.Name, stim));
        var tb = WTrigger.Box(new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(5, 10, 1), WTrigger.GameplayFlags);
        tb.Name = "box";
        mgr.AddTrigger(tb);
        var car = new WTriggerBody { Simable = "car", Position = new Vec3(0, 0.5f, -30), Velocity = new Vec3(0, 0, 40), HalfExtents = new Vec3(1, 0.7f, 2.3f), Right = new Vec3(1, 0, 0), Up = new Vec3(0, 1, 0), Forward = new Vec3(0, 0, 1), Radius = 2.6f, OwnedByPlayer = true, TriggerFlags = WTrigger.FlagActivateCars };
        float dt = 1f / 30f;
        int enters = 0, exits = 0, insideTicks = 0;
        for (int i = 0; i < 60; i++)
        {
            fired.Clear();
            mgr.Update(dt, new[] { car });
            foreach (var f in fired) { if (f.stim == 1) insideTicks++; else exits++; }
            if (fired.Any(f => f.stim == 1) && enters == 0) enters = i + 1;
            car.Position = V.Add(car.Position, V.Scale(car.Velocity, dt));
        }
        Check(enters > 0 && insideTicks >= 1 && exits == 1, $"box crossing at 40 m/s / 30 Hz: entered at step {enters}, {insideTicks} inside ticks, {exits} exit (fire-on-exit list)");
        // a miss beside the box (x = 8 > half width 5 + car half width 1)
        car.Position = new Vec3(8, 0.5f, -30); fired.Clear(); int hits = 0;
        for (int i = 0; i < 60; i++) { mgr.Update(dt, new[] { car }); hits += fired.Count; fired.Clear(); car.Position = V.Add(car.Position, V.Scale(car.Velocity, dt)); }
        Check(hits == 0, "a car 8 m to the side of a 10 m wide box misses it");
        // directional: moving against the heading does not fire
        tb.Flags |= WTrigger.FlagDirectional;
        car.Position = new Vec3(0, 0.5f, 30); car.Velocity = new Vec3(0, 0, -40); fired.Clear(); hits = 0;
        for (int i = 0; i < 60; i++) { mgr.Update(dt, new[] { car }); hits += fired.Count; fired.Clear(); car.Position = V.Add(car.Position, V.Scale(car.Velocity, dt)); }
        Check(hits == 0, "a directional trigger ignores a car driving against its heading");
        tb.Flags &= ~WTrigger.FlagDirectional;
        // cylinder (radius 25, height 100) with a simple body (traffic) at the edge
        var tc = WTrigger.Cylinder(new Vec3(100, 0, 0), new Vec3(0, 0, 1), 25f, 100f, WTrigger.GameplayFlags); tc.Name = "cyl";
        mgr.AddTrigger(tc);
        var srb = new WTriggerBody { Simable = "traffic", Position = new Vec3(100, 0, -26f), Velocity = new Vec3(0, 0, 0), Radius = 1.2f, Simple = true, TriggerFlags = WTrigger.FlagActivateCars };
        fired.Clear(); mgr.Update(dt, new[] { srb });
        Check(fired.Any(f => f.name == "cyl"), "a simple body 26 m from a radius-25 cylinder is inside (its own 1.2 m radius added; CheckCollideSRB strict <)");
        srb.Position = new Vec3(100, 0, -40); fired.Clear(); mgr.Update(dt, new[] { srb }); mgr.Update(dt, new[] { srb });
        Check(fired.Count(f => f.name == "cyl" && f.stim == 2) == 1, "leaving the cylinder fires the exit once");
        // one shot: disabled after the first fire
        var one = WTrigger.Cylinder(new Vec3(-100, 0, 0), new Vec3(0, 0, 1), 10f, 40f, WTrigger.GameplayFlags | WTrigger.FlagOneShot); one.Name = "one";
        mgr.AddTrigger(one);
        var c2 = new WTriggerBody { Simable = "car2", Position = new Vec3(-100, 0, 0), Radius = 2f, HalfExtents = new Vec3(1, 0.7f, 2.3f), Right = new Vec3(1, 0, 0), Up = new Vec3(0, 1, 0), Forward = new Vec3(0, 0, 1), OwnedByPlayer = true, TriggerFlags = WTrigger.FlagActivateCars };
        fired.Clear(); mgr.Update(dt, new[] { c2 }); mgr.Update(dt, new[] { c2 }); mgr.Update(dt, new[] { c2 });
        Check(fired.Count(f => f.name == "one") == 1 && !one.Enabled, "a OneShot trigger fires once and disables itself");
        Console.WriteLine(fails == 0 ? "wtrigger selftest: all checks passed" : $"wtrigger selftest: {fails} FAILED");
    }

    static bool DerivesFrom(AttribDatabase db, AttribCollection c, uint template)
    {
        for (var p = c; p != null; p = p.Parent) if (p.Key == template) return true;
        return false;
    }
}
