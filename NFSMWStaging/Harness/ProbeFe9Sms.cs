using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// Round 9 slice D probe: `fe9sms resp <pkg>` — package-level and object message responses with command ids.
static class ProbeFe9Sms
{
    public static void Run(string root, string[] args)
    {
        var all = new System.Collections.Generic.Dictionary<string, FEPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { "FRONTEND/FrontB.lzc", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameB.lzc" })
            foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, f)))) all.TryAdd(kv.Key, kv.Value);
        if (args[2] == "pushes")
        {
            foreach (var kv in all)
            {
                foreach (var r in kv.Value.PackageResponses) foreach (var c in r.List) if (c.Str != null) Console.WriteLine($"{kv.Key} PKG {r.Msg:X8} {c.Id} '{c.Str}'");
                foreach (var o in kv.Value.Objects) foreach (var r in o.MessageResponses) foreach (var c in r.List) if (c.Str != null) Console.WriteLine($"{kv.Key} {o.Hash:X8} {r.Msg:X8} {c.Id} '{c.Str}'");
            }
            return;
        }
        var p = all[args[3]];
        void Dump(string who, FEMessageResponse r)
        {
            Console.WriteLine($"{who} msg {r.Msg:X8}: " + string.Join("; ", r.List.Select(c => $"{c.Id}:{c.Param:X8}->{c.Target:X8}{(c.Str != null ? " '" + c.Str + "'" : "")}")));
        }
        foreach (var r in p.PackageResponses) Dump("PKG", r);
        if (args.Length > 4 && args[4] == "all")
            foreach (var o in p.Objects) foreach (var r in o.MessageResponses) Dump($"{o.Hash:X8}", r);
    }
}
