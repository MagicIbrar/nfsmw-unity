using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// physcore rb|dmg|pv <car>: named dumps of rigidbodyspecs / damagespecs / pvehicle physics fields (physics agent 1).
static class ProbePhysicsCore
{
    static readonly string[] RbVec = { "COLLISION_BOX_PAD", "DRAG", "WORLD_MOMENT_SCALE", "OBJ_MOMENT_SCALE", "GROUND_ELASTICITY", "OBJ_ELASTICITY", "DRAG_ANGULAR", "WALL_ELASTICITY", "GROUND_MOMENT_SCALE", "CG" };
    static readonly string[] RbArr = { "OBJ_FRICTION", "WALL_FRICTION", "GROUND_FRICTION" };
    static readonly string[] RbF = { "GRAVITY", "NATURAL_ANGULAR_DAMPING", "SLEEP_VELOCITY" };
    static readonly string[] RbB = { "NO_GROUND_COLLISIONS", "IMMOBILE_OBJECT_COLLISIONS", "NO_WORLD_COLLISIONS", "INSTANCE_COLLISIONS_3D", "NO_OBJ_COLLISIONS" };
    static readonly string[] DmRec = { "DZ_LFRONT", "DZ_FRONT", "DZ_BOTTOM", "DZ_LEFT", "DZ_RREAR", "DZ_LREAR", "DZ_RIGHT", "DZ_REAR", "DZ_TOP", "DZ_RFRONT" };
    static readonly string[] DmF = { "SHOCK_TIME", "HP_THRESHOLD", "SUPPRESS_DIST", "SHOCK_FORCE", "FORCE", "HIT_POINTS" };

    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        string what = args.Length > 2 ? args[2] : "rb";
        if (what == "rb")
            foreach (var c in db.OfClass("rigidbodyspecs"))
            {
                var sb = new System.Text.StringBuilder(c.ToString());
                foreach (var f in RbVec) if (c.TryGet(f, out int at, out _, out _)) sb.Append($"\n   {f}=({V(c, at, 4)})");
                foreach (var f in RbArr) if (c.Has(f)) sb.Append($"\n   {f}=[{string.Join(",", c.Floats(f).Select(x => x.ToString("0.###")))}]");
                foreach (var f in RbF) if (c.Has(f)) sb.Append($"\n   {f}={c.Float(f, 0, 0):0.###}");
                foreach (var f in RbB) if (c.TryGet(f, out int at, out _, out _)) sb.Append($"\n   {f}={c.Source.Data[at]}");
                Console.WriteLine(sb);
            }
        else if (what == "dmg")
            foreach (var c in db.OfClass("damagespecs"))
            {
                var sb = new System.Text.StringBuilder(c.ToString());
                foreach (var f in DmRec) if (c.TryGet(f, out int at, out _, out _)) sb.Append($"  {f}=({V(c, at, 2)})");
                foreach (var f in DmF) if (c.Has(f)) sb.Append($"  {f}={c.Float(f, 0, 0):0.###}");
                Console.WriteLine(sb);
            }
        else if (what == "pv")
        {
            foreach (var c in db.OfClass("pvehicle"))
            {
                if (args.Length > 3 && c.Name != args[3]) continue;
                var sb = new System.Text.StringBuilder(c.ToString());
                foreach (var f in new[] { "MASS", "TENSOR_SCALE", "HandlingRating", "RandomOpponent" })
                    if (c.Has(f)) sb.Append($"  {f}=[{string.Join(",", c.Floats(f).Select(x => x.ToString("0.###")))}]");
                foreach (var f in new[] { "rigidbodyspecs", "damagespecs" })
                    if (c.TryGet(f, out int at, out _, out _)) sb.Append($"  {f}={Chunks.U32(c.Source.Data, at):X8}/{Chunks.U32(c.Source.Data, at + 4):X8}");
                Console.WriteLine(sb);
            }
        }
    }

    static string V(AttribCollection c, int at, int n) =>
        string.Join(",", Enumerable.Range(0, n).Select(k => Chunks.F32(c.Source.Data, at + 4 * k).ToString("0.###")));
}
