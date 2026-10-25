using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// anim9 : every EAGL4 anim bank (0xE34010) and skeleton (0xE34009) in the game folder.
//         Histogram of anim / channel types (is ANIM_CSISEVENT 24 ever shipped? CsisEventChannel.cpp), the SQT slots the
//         channels write (slot 3 / 11 = the bone-length scale GetStillPose 0x49FF90 seeds and PoseSQTToGlobal 0x49FCF0
//         multiplies into column 0), and each skeleton's SkeletonData +0xC (bone length scale pointer).
static class ProbeAnim9
{
    public static void Run(string root, string[] args)
    {
        var animTypes = new Dictionary<int, int>();
        var chanTypes = new Dictionary<int, int>();
        var slots = new Dictionary<int, int>();
        int banks = 0, skels = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(f).ToUpperInvariant();
            if (ext != ".BUN" && ext != ".BIN") continue;
            byte[] d;
            try { d = File.ReadAllBytes(f); } catch { continue; }
            List<Chunk> found;
            try { found = Chunks.FindAll(d, 0, d.Length, 0x00E34010).ToList(); } catch { continue; }
            foreach (var c in found)
            {
                banks++;
                var raw = RawTypes(d, c.DataStart, c.End);
                foreach (var t in raw.anim) animTypes[t] = animTypes.TryGetValue(t, out var n) ? n + 1 : 1;
                foreach (var t in raw.chan) chanTypes[t] = chanTypes.TryGetValue(t, out var n) ? n + 1 : 1;
                foreach (var a in EaglAnimBankReader.Read(d, c.DataStart, c.End))
                {
                    if (a.UnknownChannelTypes.Count > 0) Console.WriteLine($"  unknown channel types {string.Join(",", a.UnknownChannelTypes)}: {Path.GetFileName(f)} {a.Name} frames {a.NumFrames}");
                    foreach (var ch in a.Channels)
                    {
                        IEnumerable<int> dst = Enumerable.Empty<int>();
                        if (ch is EaglDeltaChannel dc) dst = dc.DofIndices.SelectMany(i => Enumerable.Range(i, dc.ValuesPerIndex));
                        else if (ch is EaglDeltaFChannel fc) dst = fc.DofIndices.SelectMany(i => Enumerable.Range(i, fc.Components)).Concat(fc.ConstIndices.SelectMany(i => Enumerable.Range(i, fc.Components)));
                        foreach (int s in dst)
                        {
                            int k = s % 12;
                            slots[k] = slots.TryGetValue(k, out var n) ? n + 1 : 1;
                            if (k == 3 || k == 11) Console.WriteLine($"  slot {k} written: {Path.GetFileName(f)} {a.Name} type {ch.Type} sqt[{s}]");
                        }
                    }
                }
            }
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x00E34009))
            {
                var elf = EaglElf.Parse(d, c.DataStart, c.End);
                if (elf == null) continue;
                foreach (var (name, value, _) in elf.Symbols)
                    if (name.StartsWith("__Skeleton:::", StringComparison.Ordinal) && value >= 0 && value + 16 <= elf.Data.Length)
                    {
                        skels++;
                        int p = Chunks.I32(elf.Data, value + 12);
                        if (p != 0 || args.Length > 2) Console.WriteLine($"  skeleton {name.Substring(13)} in {Path.GetFileName(f)}: bones {Chunks.I32(elf.Data, value + 8)} flags {Chunks.U16(elf.Data, value + 2):X4} +0xC = {p:X}");
                    }
            }
        }
        Console.WriteLine($"{banks} banks, {skels} skeletons");
        Console.WriteLine("anim types: " + string.Join(" ", animTypes.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}")));
        Console.WriteLine("compound channel types: " + string.Join(" ", chanTypes.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}")));
        Console.WriteLine("sqt slots written (index % 12): " + string.Join(" ", slots.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}")));
    }

    static (List<int> anim, List<int> chan) RawTypes(byte[] d, int start, int end)
    {
        var anim = new List<int>(); var chan = new List<int>();
        var elf = EaglElf.Parse(d, start, end);
        if (elf == null) return (anim, chan);
        var data = elf.Data;
        int hdr = -1;
        foreach (var (name, value, _) in elf.Symbols) if (name.StartsWith("__AnimationBank", StringComparison.Ordinal)) hdr = value;
        if (hdr < 0 || hdr + 0x18 > data.Length) return (anim, chan);
        int count = Chunks.I32(data, hdr + 4), anims = Chunks.I32(data, hdr + 0x10);
        for (int i = 0; i < count && anims + i * 4 + 4 <= data.Length; i++)
        {
            int a = Chunks.I32(data, anims + i * 4);
            if (a <= 0 || a + 0xC > data.Length) continue;
            int t = Chunks.U16(data, a);
            anim.Add(t);
            if (t != 15) continue;
            int n = Chunks.U16(data, a + 8);
            for (int k = 0; k < n && a + 12 + k * 4 + 4 <= data.Length; k++)
            {
                int o = Chunks.I32(data, a + 12 + k * 4);
                if (o > 0 && o + 2 <= data.Length) chan.Add(Chunks.U16(data, o));
            }
        }
        return (anim, chan);
    }
}
