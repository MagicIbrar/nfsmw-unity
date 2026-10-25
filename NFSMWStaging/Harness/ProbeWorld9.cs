using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// world9 [n]: round-9 world collision checks (physics agent's world fork).
//   'co' objects in the packs; WCollider tri-list WWorldPos vs the instance-list WWorldPos at n random face points;
//   GetWorldNormal over a collider's barrier list vs CheckHitWorld's instance-list barriers; IntersectCircle cases.
static class ProbeWorld9
{
    public static void Run(string root, string[] args)
    {
        int n = args.Length > 2 && int.TryParse(args[2], out int k) ? k : 2000;
        var data = File.ReadAllBytes(Path.Combine(root, "TRACKS", "STREAML2RA.BUN"));
        var packs = CollisionPack.ReadAll(data, 0, data.Length);
        var w = new WCollisionQueries();
        foreach (var p in packs) w.Add(p);
        Console.WriteLine($"{packs.Count} packs, {w.InstanceCount} instances, 'co' objects in packs: {packs.Sum(p => p.Objects.Count)}");

        // face centroids as test points
        var pts = new List<Vec3>();
        var rng = new Random(9);
        foreach (var p in packs)
            foreach (var inst in p.Instances)
            {
                if (inst.Article == null) continue;
                foreach (var st in inst.Article.Strips)
                    for (int i = 0; i + 2 < st.Verts.Length; i++)
                        if (rng.Next(400) == 0)
                        {
                            var a = inst.ToWorldSim(st.Verts[i].Local); var b = inst.ToWorldSim(st.Verts[i + 1].Local); var c = inst.ToWorldSim(st.Verts[i + 2].Local);
                            pts.Add(new Vec3((a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f + 0.3f, (a.Z + b.Z + c.Z) / 3f));
                        }
            }
        pts = pts.OrderBy(_ => rng.Next()).Take(n).ToList();
        int both = 0, same = 0, onlyInst = 0, onlyTri = 0; float maxDy = 0;
        var col = WCollider.Create(WCollider.ColliderShape.Sphere, WCollider.TypeAll, 0);
        foreach (var pt in pts)
        {
            var a = new WWorldPos(0.025f);
            a.FindClosestFace(w, pt, false);
            col.Refresh(w, pt, 3f, false);
            var b = new WWorldPos(0.025f);
            b.FindClosestFace(col.Tris, pt, false);
            if (a.OnValidFace && b.OnValidFace)
            {
                both++;
                float dy = Math.Abs(a.HeightAtPoint(pt) - b.HeightAtPoint(pt));
                if (dy < 1e-3f) same++;
                maxDy = Math.Max(maxDy, dy);
            }
            else if (a.OnValidFace) onlyInst++;
            else if (b.OnValidFace) onlyTri++;
        }
        Console.WriteLine($"{pts.Count} points: both found {both} (same height {same}, max |dy| {maxDy:0.###}), instance-only {onlyInst}, tri-list-only {onlyTri}");

        // barriers: a 6 m segment through each point in a random XZ direction
        int hitsA = 0, hitsB = 0, agree = 0;
        foreach (var pt in pts.Take(Math.Min(500, pts.Count)))
        {
            double ang = rng.NextDouble() * Math.PI * 2;
            var s0 = new Vec3(pt.X - 3f * (float)Math.Cos(ang), pt.Y + 0.5f, pt.Z - 3f * (float)Math.Sin(ang));
            var s1 = new Vec3(pt.X + 3f * (float)Math.Cos(ang), pt.Y + 0.5f, pt.Z + 3f * (float)Math.Sin(ang));
            bool ha = w.CheckHitWorld(s0, s1, out var hA, 2);
            col.Refresh(w, pt, 6f, false);
            bool hb = WCollisionQueries.GetBarrierNormal(col.Barriers, s0, s1, 0, out var hB);
            if (ha) hitsA++;
            if (hb) hitsB++;
            if (ha && hb && Math.Abs(hA.Point.X - hB.Point.X) < 1e-3f && Math.Abs(hA.Point.Z - hB.Point.Z) < 1e-3f) agree++;
        }
        Console.WriteLine($"barrier segments: instance-list hits {hitsA}, collider-list hits {hitsB}, same point {agree}");

        // IntersectCircle
        bool i1 = WWorldMath.IntersectCircle(-5, 0, 5, 0, 0, 0, 1, out float u1, out float u2);
        bool i2 = WWorldMath.IntersectCircle(-5, 2, 5, 2, 0, 0, 1, out _, out _);
        bool i3 = WWorldMath.IntersectCircle(0.1f, 0, 0.2f, 0, 0, 0, 1, out float u3, out _);
        Console.WriteLine($"IntersectCircle: through {i1} u {u1:0.###}/{u2:0.###} (0.4/0.6), miss {i2} (False), inside {i3} u {u3} (True 0)");
        WCollider.Destroy(col);
    }
}
