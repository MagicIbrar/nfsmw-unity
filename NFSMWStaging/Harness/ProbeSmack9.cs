using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// smack9 specs: every smackable collection's rigidbodyspecs RefSpec (resolved to its collection, "default" when missing)
static class ProbeSmack9
{
    public static void Run(string root, string[] args)
    {
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        uint rbCls = 0x7C90BB38;
        var count = new SortedDictionary<string, List<string>>();
        int n = 0;
        foreach (var c in db.OfClass("smackable"))
        {
            n++;
            string name = "(none)";
            for (var x = c; x != null; x = x.Parent)
                if (x.TryGet("rigidbodyspecs", out int at, out _, out _))
                {
                    uint cls = Chunks.U32(x.Source.Data, at), key = Chunks.U32(x.Source.Data, at + 4);
                    name = db.Collections.TryGetValue((rbCls, key), out var rb) ? (rb.Name ?? key.ToString("X8")) : $"missing {cls:X8}/{key:X8}";
                    break;
                }
            if (!count.TryGetValue(name, out var l)) count[name] = l = new List<string>();
            l.Add(c.Name ?? c.Key.ToString("X8"));
        }
        Console.WriteLine($"{n} smackable collections");
        foreach (var kv in count) Console.WriteLine($"{kv.Key}: {kv.Value.Count}  {string.Join(",", kv.Value.Take(12))}");
    }
}
