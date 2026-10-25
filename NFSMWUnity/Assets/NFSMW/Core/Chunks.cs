using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>One EA bChunk: [u32 id][u32 size][payload]. Ids with bit 31 set hold child chunks.</summary>
    public readonly struct Chunk
    {
        public readonly uint Id;
        public readonly int Start;
        public readonly int Size;

        public Chunk(uint id, int start, int size)
        {
            Id = id;
            Start = start;
            Size = size;
        }

        public int DataStart => Start + 8;
        public int End => Start + 8 + Size;
        public bool IsContainer => (Id & 0x80000000u) != 0;

        public override string ToString() => $"{Id:X8} @{Start:X} size={Size}";
    }

    public static class Chunks
    {
        /// <summary>Siblings in [start, end). Padding chunks (id 0) are skipped; a truncated chunk ends the walk.</summary>
        public static IEnumerable<Chunk> Read(byte[] data, int start, int end)
        {
            if (end > data.Length) end = data.Length;
            int off = start;
            while (off + 8 <= end)
            {
                uint id = U32(data, off);
                int size = (int)U32(data, off + 4);
                if (size < 0 || off + 8 + size > end) yield break;
                if (id != 0) yield return new Chunk(id, off, size);
                off += 8 + size;
            }
        }

        public static IEnumerable<Chunk> Children(byte[] data, Chunk parent) => Read(data, parent.DataStart, parent.End);

        /// <summary>All chunks with the given id, searching containers recursively.</summary>
        public static IEnumerable<Chunk> FindAll(byte[] data, int start, int end, uint id)
        {
            foreach (var c in Read(data, start, end))
            {
                if (c.Id == id) yield return c;
                else if (c.IsContainer)
                    foreach (var inner in FindAll(data, c.DataStart, c.End, id))
                        yield return inner;
            }
        }

        /// <summary>The game aligns payloads in memory (16 for headers, 128 for GPU buffers); buffers here start aligned.</summary>
        public static int Align(int offset, int alignment) => (offset + alignment - 1) & ~(alignment - 1);

        public static uint U32(byte[] d, int o) => BitConverter.ToUInt32(d, o);
        public static int I32(byte[] d, int o) => BitConverter.ToInt32(d, o);
        public static ushort U16(byte[] d, int o) => BitConverter.ToUInt16(d, o);
        public static short I16(byte[] d, int o) => BitConverter.ToInt16(d, o);
        public static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);

        public static string CString(byte[] d, int o, int max)
        {
            int n = 0;
            while (n < max && o + n < d.Length && d[o + n] != 0) n++;
            return System.Text.Encoding.ASCII.GetString(d, o, n);
        }
    }

    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vec3 Read(byte[] d, int o) => new Vec3(Chunks.F32(d, o), Chunks.F32(d, o + 4), Chunks.F32(d, o + 8));
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    /// <summary>bStringHash: h = h*33 + c, seeded with 0xFFFFFFFF. Asset names are hashed upper-case.</summary>
    public static class EaHash
    {
        public static uint Hash(string s, uint seed = 0xFFFFFFFFu)
        {
            uint h = seed;
            foreach (char c in s) h = unchecked(h * 33 + (byte)c);
            return h;
        }

        public static uint HashUpper(string s) => Hash(s.ToUpperInvariant());
    }
}
