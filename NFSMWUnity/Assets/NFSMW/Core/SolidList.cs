using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>One drawable piece of a solid: a material with its own vertices and triangle list.</summary>
    public sealed class SolidPart
    {
        public string MaterialName;
        public int EffectId;
        public uint Flags;
        public uint TextureHash;          // diffuse (texture slot 0); 0 when the material has none
        public byte[] TextureSlots = new byte[5];
        /// <summary>eLightMaterial name hash (material +0x1D indexes the solid's 0x134013 table; 0xFF = default). [car_render.md]</summary>
        public uint LightMaterialHash;
        public float[] Positions;         // xyz, game space (Z up)
        public float[] Normals;
        public float[] Uvs;
        public uint[] Colors;             // D3DCOLOR (ARGB in a u32)
        /// <summary>Second UV set at +36 of 44-byte vertices (sky dome, effect 19: skybox.efx reads TEXCOORD1); else null.</summary>
        public float[] Uvs2;
        /// <summary>
        /// Tangent xyzw (game space, w = +-1 binormal sign) at +44 of the 60-byte vertices of effects 1 and 3 (worldreflect /
        /// worldnormalmap declare dcl_tangent; +36 holds a second UV pair they do not read); else null. world_render.md §2.
        /// </summary>
        public float[] Tangents;
        /// <summary>The floats after +36 of each vertex when SolidList.CaptureExtra is set (probes only); else null.</summary>
        public float[] Extra;
        public int ExtraPerVertex;
        public int[] Indices;             // triangle list into this part's vertices
        public int VertexCount => Positions.Length / 3;
    }

    public sealed class Solid
    {
        public string Name;
        public uint Hash;
        public Vec3 BoundsMin, BoundsMax;
        /// <summary>eSolid NumPolys (+0x14) and Density (+0x9C): the scenery LOD test uses both.</summary>
        public int NumPolys;
        public float Density;
        public float[] Matrix = new float[16];
        public uint[] TextureHashes = Array.Empty<uint>();
        /// <summary>Chunk 0x134013: the light material name hashes the parts index (8 bytes each, like 0x134012).</summary>
        public uint[] LightMaterialHashes = Array.Empty<uint>();
        public List<SolidPart> Parts = new List<SolidPart>();
        /// <summary>Attachment points (chunk 0x13401A): name hash -> 4x4 row-major matrix in the solid's space.</summary>
        public List<(uint hash, float[] matrix)> Markers = new List<(uint, float[])>();

        public float[] Marker(string name)
        {
            uint h = EaHash.Hash(name);
            foreach (var m in Markers) if (m.hash == h) return m.matrix;
            return null;
        }

        public override string ToString() => $"{Name} parts={Parts.Count}";
    }

    /// <summary>
    /// Solid list, chunk 0x80134000 (LoadSolid @0x5070E0, mesh loader @0x6E3EE0 in speed.exe):
    ///   80134001 list header (0x134002 file/section names, 0x134003 hash list, 0x134004 per-solid offsets)
    ///   80134010 solid: 0x134011 header (16-aligned), 0x134012 texture hashes, 0x134013 shader hashes,
    ///                   0x13401A markers (80 bytes: u32 name hash e.g. SPOILER, ROOF_SCOOP, LEFT_HEADLIGHT, 12 bytes,
    ///                   4x4 matrix with the position in the last row), 80134100 mesh
    ///   80134100 mesh:  0x134900 header (+0x10 material count, +0x2C index count),
    ///                   0x134B02 materials[0x68], 0x134B03 u16 indices, 0x134B01 vertex buffers (128-aligned),
    ///                   0x134C02 material names.
    /// Each vertex buffer goes to the next material; following materials with the same effect id share it.
    /// Indices are relative to the material's buffer.
    /// </summary>
    public static class SolidList
    {
        public const uint Id = 0x80134000;
        /// <summary>Probes: keep the raw floats past +36 of every vertex in SolidPart.Extra.</summary>
        public static bool CaptureExtra;

        // vertex stride by effect id (the game reads it from the effect's vertex declaration)
        static readonly Dictionary<int, int> KnownStride = new Dictionary<int, int>
        {
            { 0, 36 }, { 1, 60 }, { 3, 60 }, { 4, 36 }, { 5, 36 }, { 6, 36 }, { 19, 44 },
        };

        public static List<Solid> ReadAll(byte[] data, int start, int end)
        {
            var list = new List<Solid>();
            foreach (var c in Chunks.FindAll(data, start, end, Id))
                foreach (var s in Chunks.Children(data, c))
                    if (s.Id == 0x80134010)
                    {
                        var solid = ReadSolid(data, s);
                        if (solid != null) list.Add(solid);
                    }
            return list;
        }

        public static Solid ReadSolid(byte[] d, Chunk chunk)
        {
            var solid = new Solid();
            foreach (var c in Chunks.Children(d, chunk))
            {
                switch (c.Id)
                {
                    case 0x134011:
                    {
                        int h = Chunks.Align(c.DataStart, 16);
                        solid.Hash = Chunks.U32(d, h + 0x10);
                        solid.BoundsMin = Vec3.Read(d, h + 0x20);
                        solid.BoundsMax = Vec3.Read(d, h + 0x30);
                        for (int i = 0; i < 16; i++) solid.Matrix[i] = Chunks.F32(d, h + 0x40 + i * 4);
                        solid.Name = Chunks.CString(d, h + 0xA0, c.End - (h + 0xA0));
                        solid.NumPolys = (short)Chunks.U16(d, h + 0x14);
                        solid.Density = Chunks.F32(d, h + 0x9C);
                        break;
                    }
                    case 0x134012:
                    {
                        solid.TextureHashes = new uint[c.Size / 8];
                        for (int i = 0; i < solid.TextureHashes.Length; i++)
                            solid.TextureHashes[i] = Chunks.U32(d, c.DataStart + i * 8);
                        break;
                    }
                    case 0x134013:
                    {
                        solid.LightMaterialHashes = new uint[c.Size / 8];
                        for (int i = 0; i < solid.LightMaterialHashes.Length; i++)
                            solid.LightMaterialHashes[i] = Chunks.U32(d, c.DataStart + i * 8);
                        break;
                    }
                    case 0x13401A:
                        for (int o = Chunks.Align(c.DataStart, 16); o + 80 <= c.End; o += 80)
                        {
                            var m = new float[16];
                            for (int i = 0; i < 16; i++) m[i] = Chunks.F32(d, o + 16 + i * 4);
                            solid.Markers.Add((Chunks.U32(d, o), m));
                        }
                        break;
                    case 0x80134100:
                        ReadMesh(d, c, solid);
                        break;
                }
            }
            if (solid.Name == null) return null;
            if (Mods.Active) ModelOverride.Apply(solid); // [ours] Mods/models/<name>.obj replacement
            return solid;
        }

        static void ReadMesh(byte[] d, Chunk mesh, Solid solid)
        {
            int matOff = -1, matCount = 0, idxOff = -1, idxCount = 0;
            var buffers = new List<(int off, int size)>();
            var names = new List<string>();
            foreach (var c in Chunks.Children(d, mesh))
            {
                switch (c.Id)
                {
                    case 0x134900:
                    {
                        int h = Chunks.Align(c.DataStart, 16);
                        matCount = Chunks.I32(d, h + 0x10);
                        idxCount = Chunks.I32(d, h + 0x2C);
                        break;
                    }
                    case 0x134B02: matOff = Chunks.Align(c.DataStart, 16); break;
                    case 0x134B03: idxOff = Chunks.Align(c.DataStart, 16); break;
                    case 0x134B01:
                    {
                        int a = Chunks.Align(c.DataStart, 0x80);
                        if (c.End - a > 0) buffers.Add((a, c.End - a));
                        break;
                    }
                    case 0x134C02: names.Add(Chunks.CString(d, c.DataStart, c.Size)); break;
                }
            }
            if (matOff < 0 || idxOff < 0 || matCount <= 0) return;

            // buffer per material, as the mesh loader assigns them
            var matBuffer = new int[matCount];
            var effect = new int[matCount];
            for (int m = 0; m < matCount; m++)
            {
                matBuffer[m] = -1;
                effect[m] = Chunks.I32(d, matOff + m * 0x68 + 0x30);
            }
            int cursor = 0;
            for (int b = 0; b < buffers.Count && cursor < matCount; b++)
            {
                matBuffer[cursor++] = b;
                while (cursor < matCount && effect[cursor] == effect[cursor - 1]) matBuffer[cursor++] = b;
            }

            // stride per buffer: known table, else derived from the highest index used
            var stride = new int[buffers.Count];
            var maxIndex = new int[buffers.Count];
            for (int m = 0; m < matCount; m++)
            {
                int b = matBuffer[m];
                if (b < 0) continue;
                int e = matOff + m * 0x68;
                int start = Chunks.I32(d, e + 0x44), count = Chunks.I32(d, e + 0x5C);
                for (int i = 0; i < count && start + i < idxCount; i++)
                    maxIndex[b] = Math.Max(maxIndex[b], Chunks.U16(d, idxOff + (start + i) * 2));
                if (KnownStride.TryGetValue(effect[m], out int s)) stride[b] = s;
            }
            for (int b = 0; b < buffers.Count; b++)
                if (stride[b] == 0) stride[b] = Math.Max(36, buffers[b].size / (maxIndex[b] + 1) & ~3);

            for (int m = 0; m < matCount; m++)
            {
                int b = matBuffer[m];
                if (b < 0) continue;
                int e = matOff + m * 0x68;
                int start = Chunks.I32(d, e + 0x44), count = Chunks.I32(d, e + 0x5C);
                if (count <= 0 || start + count > idxCount) continue;
                var part = new SolidPart
                {
                    MaterialName = m < names.Count ? names[m] : "",
                    EffectId = effect[m],
                    Flags = Chunks.U32(d, e + 0x38),
                };
                Buffer.BlockCopy(d, e + 0x18, part.TextureSlots, 0, 5);
                int slot = part.TextureSlots[0];
                if (slot < solid.TextureHashes.Length) part.TextureHash = solid.TextureHashes[slot];
                int lm = d[e + 0x1D];
                if (lm < solid.LightMaterialHashes.Length) part.LightMaterialHash = solid.LightMaterialHashes[lm];

                // gather the vertices this material references and remap to 0..n-1
                var remap = new Dictionary<int, int>();
                var order = new List<int>();
                part.Indices = new int[count - count % 3];
                for (int i = 0; i < part.Indices.Length; i++)
                {
                    int v = Chunks.U16(d, idxOff + (start + i) * 2);
                    if (!remap.TryGetValue(v, out int nv))
                    {
                        nv = order.Count;
                        remap[v] = nv;
                        order.Add(v);
                    }
                    part.Indices[i] = nv;
                }
                var (vbOff, vbSize) = buffers[b];
                int st = stride[b];
                int n = order.Count;
                part.Positions = new float[n * 3];
                part.Normals = new float[n * 3];
                part.Uvs = new float[n * 2];
                part.Colors = new uint[n];
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    int o = vbOff + order[i] * st;
                    if (o + 36 > vbOff + vbSize) { ok = false; break; }
                    part.Positions[i * 3] = Chunks.F32(d, o);
                    part.Positions[i * 3 + 1] = Chunks.F32(d, o + 4);
                    part.Positions[i * 3 + 2] = Chunks.F32(d, o + 8);
                    part.Normals[i * 3] = Chunks.F32(d, o + 12);
                    part.Normals[i * 3 + 1] = Chunks.F32(d, o + 16);
                    part.Normals[i * 3 + 2] = Chunks.F32(d, o + 20);
                    part.Colors[i] = Chunks.U32(d, o + 24);
                    part.Uvs[i * 2] = Chunks.F32(d, o + 28);
                    part.Uvs[i * 2 + 1] = Chunks.F32(d, o + 32);
                    if (st >= 60 && (part.EffectId == 1 || part.EffectId == 3) && o + 60 <= vbOff + vbSize)
                    {
                        part.Tangents ??= new float[n * 4];
                        for (int e2 = 0; e2 < 4; e2++) part.Tangents[i * 4 + e2] = Chunks.F32(d, o + 44 + e2 * 4);
                    }
                    if (CaptureExtra && st > 36 && o + st <= vbOff + vbSize)
                    {
                        int k = (st - 36) / 4;
                        part.ExtraPerVertex = k;
                        part.Extra ??= new float[n * k];
                        for (int e2 = 0; e2 < k; e2++) part.Extra[i * k + e2] = Chunks.F32(d, o + 36 + e2 * 4);
                    }
                    if (st >= 44 && part.EffectId == 19 && o + 44 <= vbOff + vbSize)
                    {
                        part.Uvs2 ??= new float[n * 2];
                        part.Uvs2[i * 2] = Chunks.F32(d, o + 36);
                        part.Uvs2[i * 2 + 1] = Chunks.F32(d, o + 40);
                    }
                }
                if (ok) solid.Parts.Add(part);
            }
        }
    }
}
