using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// [ours] The mod folder. Nothing is ever written into the game's files: replacements live in the Mods folder and the
    /// readers swap them in as the game data is decoded.
    ///   Mods/textures/&lt;PackName&gt;/&lt;TextureName&gt;.png   replaces that texture in that pack only
    ///   Mods/textures/&lt;TextureName&gt;.png              replaces it in every pack (the pack folder wins)
    ///   ...&lt;TextureName&gt;@&lt;HASH&gt;.png                  the same, for one texture only (8 hex digits, the name hash):
    ///                                                TextureInfo keeps 24 characters of the name, so a few distinct
    ///                                                textures share a name (vinyls); @HASH wins over the plain name
    ///   Mods/models/&lt;SolidName&gt;.obj                  replaces the solid's parts (ModelOverride below)
    /// Names are matched case-insensitively. The folder is scanned once when Root is set (Refresh rescans); with no Root,
    /// or a missing folder, Active is false and the readers skip the lookup entirely. See MODDING.md.
    /// </summary>
    public static class Mods
    {
        sealed class Index
        {
            public readonly Dictionary<string, string> Textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        static string _root;
        static Index _index;
        static readonly object Lock = new object();
        static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, (DateTime time, int w, int h, int mips, byte[] data)> TextureCache =
            new Dictionary<string, (DateTime, int, int, int, byte[])>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where messages go (the Unity runtime points this at Debug.Log; the tools at the console).</summary>
        public static Action<string> Log = Console.WriteLine;

        /// <summary>True when Root names an existing folder.</summary>
        public static bool Active => _index != null;

        /// <summary>The mod folder, or null. Setting it scans the folder (a missing folder means no mods).</summary>
        public static string Root
        {
            get => _root;
            set
            {
                _root = string.IsNullOrEmpty(value) ? null : value;
                Refresh();
            }
        }

        /// <summary>Rescans Root (call after adding or removing files while running).</summary>
        public static void Refresh()
        {
            lock (Lock)
            {
                Logged.Clear();
                TextureCache.Clear();
                ModelOverride.ClearCache();
            }
            if (_root == null || !Directory.Exists(_root))
            {
                _index = null;
                return;
            }
            var ix = new Index();
            string tex = Path.Combine(_root, "textures");
            if (Directory.Exists(tex))
            {
                foreach (var f in Directory.GetFiles(tex, "*.png")) ix.Textures["/" + Path.GetFileNameWithoutExtension(f)] = f;
                foreach (var dir in Directory.GetDirectories(tex))
                {
                    string pack = Path.GetFileName(dir);
                    foreach (var f in Directory.GetFiles(dir, "*.png")) ix.Textures[pack + "/" + Path.GetFileNameWithoutExtension(f)] = f;
                }
            }
            string models = Path.Combine(_root, "models");
            if (Directory.Exists(models))
                foreach (var f in Directory.GetFiles(models, "*.obj")) ix.Models[Path.GetFileNameWithoutExtension(f)] = f;
            _index = ix;
            Log?.Invoke($"NFSMW mods: {_root} ({ix.Textures.Count} textures, {ix.Models.Count} models)");
        }

        /// <summary>The PNG replacing this texture, or null (pack folder first, then the shared folder; Name@HASH before Name).</summary>
        public static string TextureFile(string pack, string name, uint hash)
        {
            var ix = _index;
            if (ix == null || string.IsNullOrEmpty(name)) return null;
            string exact = name + "@" + hash.ToString("X8");
            string p;
            if (!string.IsNullOrEmpty(pack))
            {
                if (ix.Textures.TryGetValue(pack + "/" + exact, out p)) return p;
                if (ix.Textures.TryGetValue(pack + "/" + name, out p)) return p;
            }
            if (ix.Textures.TryGetValue("/" + exact, out p)) return p;
            return ix.Textures.TryGetValue("/" + name, out p) ? p : null;
        }

        /// <summary>The OBJ replacing this solid, or null.</summary>
        public static string ModelFile(string solidName)
        {
            var ix = _index;
            if (ix == null || string.IsNullOrEmpty(solidName)) return null;
            return ix.Models.TryGetValue(solidName, out var p) ? p : null;
        }

        /// <summary>Every override in the folder: (kind "texture"/"model", key "pack/name" or "name", file).</summary>
        public static IEnumerable<(string kind, string key, string file)> All()
        {
            var ix = _index;
            if (ix == null) yield break;
            foreach (var kv in ix.Textures) yield return ("texture", kv.Key.TrimStart('/'), kv.Value);
            foreach (var kv in ix.Models) yield return ("model", kv.Key, kv.Value);
        }

        internal static void LogOnce(string key, string message)
        {
            bool first;
            lock (Lock) first = Logged.Add(key);
            if (first) Log?.Invoke(message);
        }

        /// <summary>
        /// Called by TexturePack.Read for every decoded texture: swaps in Mods/textures/... when present. The texture
        /// becomes Bgra32 at the PNG's size with a full box-filtered mip chain; Hash, Name, PackName, TileUV, the alpha
        /// usage/blend and scroll fields are kept, so materials treat it exactly like the original.
        /// </summary>
        public static void ApplyTexture(GameTexture t)
        {
            if (t.Data == null) return; // index-only reads (withData = false)
            string file = TextureFile(t.PackName, t.Name, t.Hash);
            if (file == null) return;
            if (!LoadTexture(file, out int w, out int h, out int mips, out byte[] data, out string error))
            {
                LogOnce("!" + file, $"NFSMW mods: texture {t.Name} NOT replaced, {file}: {error}");
                return;
            }
            t.Format = TexFormat.Bgra32;
            t.FourCC = "21";
            t.Width = w;
            t.Height = h;
            t.MipCount = mips;
            t.Data = data;
            t.Palette = null;
            LogOnce(t.PackName + "/" + t.Name + "@" + t.Hash.ToString("X8") + "|" + file, $"NFSMW mods: texture {t.Name} <- {file}");
        }

        /// <summary>Decoded PNG with its mip chain, cached by path + modification time.</summary>
        public static bool LoadTexture(string file, out int w, out int h, out int mips, out byte[] data, out string error)
        {
            w = h = mips = 0;
            data = null;
            error = null;
            DateTime time;
            try { time = File.GetLastWriteTimeUtc(file); }
            catch (Exception e) { error = e.Message; return false; }
            lock (Lock)
            {
                if (TextureCache.TryGetValue(file, out var c) && c.time == time)
                {
                    (w, h, mips, data) = (c.w, c.h, c.mips, c.data);
                    return true;
                }
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (Exception e) { error = e.Message; return false; }
            if (!Png.Decode(bytes, out w, out h, out var top, out error)) return false;
            data = BuildMips(top, w, h, out mips);
            lock (Lock) TextureCache[file] = (time, w, h, mips, data);
            return true;
        }

        /// <summary>BGRA top level + every smaller level down to 1x1 (2x2 box filter, edges clamped), as TexturePack.LevelSize lays them out.</summary>
        public static byte[] BuildMips(byte[] top, int w, int h, out int mips)
        {
            mips = 1;
            int total = w * h * 4;
            for (int lw = w, lh = h; lw > 1 || lh > 1; mips++)
            {
                lw = Math.Max(1, lw >> 1);
                lh = Math.Max(1, lh >> 1);
                total += lw * lh * 4;
            }
            var data = new byte[total];
            Buffer.BlockCopy(top, 0, data, 0, w * h * 4);
            int src = 0, sw = w, sh = h, dst = w * h * 4;
            for (int m = 1; m < mips; m++)
            {
                int dw = Math.Max(1, sw >> 1), dh = Math.Max(1, sh >> 1);
                for (int y = 0; y < dh; y++)
                {
                    int y0 = Math.Min(sh - 1, y * 2), y1 = Math.Min(sh - 1, y * 2 + 1);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Math.Min(sw - 1, x * 2), x1 = Math.Min(sw - 1, x * 2 + 1);
                        int a = src + (y0 * sw + x0) * 4, b = src + (y0 * sw + x1) * 4;
                        int c = src + (y1 * sw + x0) * 4, d = src + (y1 * sw + x1) * 4;
                        int o = dst + (y * dw + x) * 4;
                        for (int k = 0; k < 4; k++) data[o + k] = (byte)((data[a + k] + data[b + k] + data[c + k] + data[d + k] + 2) >> 2);
                    }
                }
                src = dst;
                dst += dw * dh * 4;
                sw = dw;
                sh = dh;
            }
            return data;
        }
    }

    /// <summary>
    /// [ours] The OBJ frame. Game space is right-handed Z-up; OBJ (Blender, Max, Maya importers) is right-handed Y-up, so
    /// a game point (x, y, z) is written as (x, z, -y): a rotation, not a mirror, so nothing comes out flipped and Blender's
    /// default OBJ import (forward -Z, up Y) shows the model in game coordinates exactly. Normals turn the same way.
    /// Texture V is flipped (game UVs are Direct3D, V down; OBJ is V up): vt = (u, 1 - v).
    /// Materials are named "&lt;part index&gt;_&lt;MaterialName&gt;_&lt;texture name&gt;" (whitespace -> '_').
    /// </summary>
    public static class ObjFrame
    {
        public static void ToObj(float x, float y, float z, out float ox, out float oy, out float oz) { ox = x; oy = z; oz = -y; }
        public static void FromObj(float ox, float oy, float oz, out float x, out float y, out float z) { x = ox; y = -oz; z = oy; }
        public static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s, @"\s", "_");
        public static string MaterialName(int index, SolidPart part, string textureName) => $"{index}_{Clean(part.MaterialName)}_{Clean(textureName)}";
    }

    /// <summary>[ours] A parsed Wavefront OBJ (v with optional r g b, vt, vn, f, usemtl). Faces stay as polygons.</summary>
    public sealed class ObjModel
    {
        public sealed class Group
        {
            public string Material;
            public readonly List<int[]> Faces = new List<int[]>(); // per polygon: v0, vt0, vn0, v1, vt1, vn1, ... (0-based, -1 = absent)
        }

        public readonly List<float> Positions = new List<float>();   // OBJ space
        public readonly List<float> Colors = new List<float>();      // r g b per position (NaN = none)
        public readonly List<float> Uvs = new List<float>();
        public readonly List<float> Normals = new List<float>();
        public readonly List<Group> Groups = new List<Group>();
        public bool HasColors;

        public static ObjModel Parse(string text)
        {
            var m = new ObjModel();
            var byName = new Dictionary<string, Group>(StringComparer.Ordinal);
            Group cur = null;
            var ci = CultureInfo.InvariantCulture;
            var sep = new[] { ' ', '\t' };
            int lineNo = 0;
            foreach (var rawLine in text.Split('\n'))
            {
                lineNo++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tok = line.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                switch (tok[0])
                {
                    case "v":
                        if (tok.Length < 4) throw new FormatException($"line {lineNo}: v needs 3 numbers");
                        for (int i = 1; i <= 3; i++) m.Positions.Add(float.Parse(tok[i], ci));
                        if (tok.Length >= 7)
                        {
                            float r = float.Parse(tok[4], ci), g = float.Parse(tok[5], ci), b = float.Parse(tok[6], ci);
                            if (r > 1.001f || g > 1.001f || b > 1.001f) { r /= 255f; g /= 255f; b /= 255f; }
                            m.Colors.Add(r); m.Colors.Add(g); m.Colors.Add(b);
                            m.HasColors = true;
                        }
                        else { m.Colors.Add(float.NaN); m.Colors.Add(float.NaN); m.Colors.Add(float.NaN); }
                        break;
                    case "vt":
                        if (tok.Length < 2) throw new FormatException($"line {lineNo}: vt needs 2 numbers");
                        m.Uvs.Add(float.Parse(tok[1], ci));
                        m.Uvs.Add(tok.Length > 2 ? float.Parse(tok[2], ci) : 0f);
                        break;
                    case "vn":
                        if (tok.Length < 4) throw new FormatException($"line {lineNo}: vn needs 3 numbers");
                        for (int i = 1; i <= 3; i++) m.Normals.Add(float.Parse(tok[i], ci));
                        break;
                    case "usemtl":
                    {
                        string name = line.Substring(6).Trim();
                        if (!byName.TryGetValue(name, out cur))
                        {
                            cur = new Group { Material = name };
                            byName[name] = cur;
                            m.Groups.Add(cur);
                        }
                        break;
                    }
                    case "f":
                    {
                        if (tok.Length < 4) continue; // points and lines are not drawable
                        if (cur == null)
                        {
                            cur = new Group { Material = "" };
                            byName[""] = cur;
                            m.Groups.Add(cur);
                        }
                        var face = new int[(tok.Length - 1) * 3];
                        for (int i = 1; i < tok.Length; i++)
                        {
                            var parts = tok[i].Split('/');
                            face[(i - 1) * 3] = Ref(parts[0], m.Positions.Count / 3, lineNo);
                            face[(i - 1) * 3 + 1] = parts.Length > 1 && parts[1].Length > 0 ? Ref(parts[1], m.Uvs.Count / 2, lineNo) : -1;
                            face[(i - 1) * 3 + 2] = parts.Length > 2 && parts[2].Length > 0 ? Ref(parts[2], m.Normals.Count / 3, lineNo) : -1;
                        }
                        cur.Faces.Add(face);
                        break;
                    }
                }
            }
            m.Groups.RemoveAll(g => g.Faces.Count == 0);
            return m;
        }

        static int Ref(string s, int count, int lineNo)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            int r = i < 0 ? count + i : i - 1;
            if (r < 0 || r >= count) throw new FormatException($"line {lineNo}: index {s} out of range (have {count})");
            return r;
        }
    }

    /// <summary>
    /// [ours] Mods/models/&lt;SolidName&gt;.obj replaces a solid's parts (SolidList.ReadSolid calls Apply). Name, Hash,
    /// Matrix, Markers, NumPolys/Density and the texture tables are kept; bounds are recomputed (with the original's margin). One part per usemtl
    /// group. The material name picks the original part whose material settings (effect, flags, textures, light
    /// material) the new part takes: "&lt;i&gt;_&lt;MaterialName&gt;_..." as exported -> part i; a group naming another
    /// part's material borrows that part's; anything else uses part 0's. Normals come from the OBJ (smooth normals are
    /// generated when missing), vertex colours from "v x y z r g b" (alpha from the matched part), else the average
    /// colour of the original solid (its baked lighting). Polygons are fanned into triangles, and each part's winding is
    /// turned to match the original part's. Tangents are generated for the parts that had them (normal-mapped world
    /// effects 1/3); Uvs2/Extra stay null. Skinned NIS characters (NisContent's SkinnedSolid) are not covered.
    /// </summary>
    public static class ModelOverride
    {
        static readonly Dictionary<string, (DateTime time, ObjModel obj)> Cache = new Dictionary<string, (DateTime, ObjModel)>(StringComparer.OrdinalIgnoreCase);

        internal static void ClearCache() { lock (Cache) Cache.Clear(); }

        public static ObjModel Load(string file, out string error)
        {
            error = null;
            try
            {
                var time = File.GetLastWriteTimeUtc(file);
                lock (Cache)
                    if (Cache.TryGetValue(file, out var c) && c.time == time) return c.obj;
                var obj = ObjModel.Parse(File.ReadAllText(file));
                lock (Cache) Cache[file] = (time, obj);
                return obj;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        public static void Apply(Solid solid)
        {
            string file = Mods.ModelFile(solid.Name);
            if (file == null) return;
            var obj = Load(file, out string error);
            if (obj == null || obj.Groups.Count == 0)
            {
                Mods.LogOnce("!" + file, $"NFSMW mods: model {solid.Name} NOT replaced, {file}: {error ?? "no faces"}");
                return;
            }
            var parts = Build(solid, obj);
            if (parts.Count == 0) return;
            // bounds: the new geometry's box, keeping the margin the original header had around its own vertices
            bool had = Tight(solid.Parts, out var oMin, out var oMax);
            solid.Parts = parts;
            if (Tight(parts, out var nMin, out var nMax))
            {
                var a = solid.BoundsMin;
                var b = solid.BoundsMax;
                float Pad(float v) => had && v > 0 && v < 1e4f ? v : 0;
                solid.BoundsMin = new Vec3(nMin.X - Pad(oMin.X - a.X), nMin.Y - Pad(oMin.Y - a.Y), nMin.Z - Pad(oMin.Z - a.Z));
                solid.BoundsMax = new Vec3(nMax.X + Pad(b.X - oMax.X), nMax.Y + Pad(b.Y - oMax.Y), nMax.Z + Pad(b.Z - oMax.Z));
            }
            int v = 0, t = 0;
            foreach (var p in parts) { v += p.VertexCount; t += p.Indices.Length / 3; }
            Mods.LogOnce(solid.Name + "|" + file, $"NFSMW mods: model {solid.Name} <- {file} ({parts.Count} parts, {v} vertices, {t} triangles)");
        }

        /// <summary>Index of the original part a usemtl name maps to (see the class summary).</summary>
        public static int MatchPart(IList<SolidPart> original, string material)
        {
            if (original.Count == 0) return -1;
            string rest = material;
            int index = -1;
            int us = material.IndexOf('_');
            if (us > 0 && int.TryParse(material.Substring(0, us), NumberStyles.None, CultureInfo.InvariantCulture, out int i))
            {
                index = i;
                rest = material.Substring(us + 1);
            }
            bool Names(int k, string s)
            {
                string n = ObjFrame.Clean(original[k].MaterialName);
                return s.Equals(n, StringComparison.OrdinalIgnoreCase) || s.StartsWith(n + "_", StringComparison.OrdinalIgnoreCase);
            }
            if (index >= 0 && index < original.Count && Names(index, rest)) return index;
            for (int k = 0; k < original.Count; k++) if (original[k].MaterialName.Length > 0 && Names(k, rest)) return k;
            if (rest != material)
                for (int k = 0; k < original.Count; k++) if (original[k].MaterialName.Length > 0 && Names(k, material)) return k;
            if (index >= 0 && index < original.Count) return index;
            return 0;
        }

        public static List<SolidPart> Build(Solid solid, ObjModel obj)
        {
            var original = solid.Parts;
            var result = new List<SolidPart>();
            uint average = AverageColor(original);
            foreach (var g in obj.Groups)
            {
                int ti = MatchPart(original, g.Material);
                var tpl = ti >= 0 ? original[ti] : null;
                var part = new SolidPart
                {
                    MaterialName = tpl?.MaterialName ?? g.Material,
                    EffectId = tpl?.EffectId ?? 0,
                    Flags = tpl?.Flags ?? 0,
                    TextureHash = tpl?.TextureHash ?? 0,
                    TextureSlots = tpl != null ? (byte[])tpl.TextureSlots.Clone() : new byte[5],
                    LightMaterialHash = tpl?.LightMaterialHash ?? 0,
                };
                uint alpha = tpl != null ? AverageColor(new[] { tpl }) & 0xFF000000u : 0xFF000000u;

                // one vertex per distinct (v, vt, vn); polygons fanned
                var map = new Dictionary<(int, int, int), int>();
                var keys = new List<(int v, int t, int n)>();
                var idx = new List<int>();
                foreach (var f in g.Faces)
                {
                    int corners = f.Length / 3;
                    var c = new int[corners];
                    for (int k = 0; k < corners; k++)
                    {
                        var key = (f[k * 3], f[k * 3 + 1], f[k * 3 + 2]);
                        if (!map.TryGetValue(key, out int vi))
                        {
                            vi = keys.Count;
                            map[key] = vi;
                            keys.Add(key);
                        }
                        c[k] = vi;
                    }
                    for (int k = 1; k + 1 < corners; k++)
                    {
                        idx.Add(c[0]);
                        idx.Add(c[k]);
                        idx.Add(c[k + 1]);
                    }
                }
                // vertices in OBJ order (an exported, unedited OBJ gives back the original vertex order)
                var order = new int[keys.Count];
                for (int k = 0; k < order.Length; k++) order[k] = k;
                Array.Sort(keys.ToArray(), order);
                var rank = new int[order.Length];
                for (int k = 0; k < order.Length; k++) rank[order[k]] = k;
                keys.Sort();
                for (int k = 0; k < idx.Count; k++) idx[k] = rank[idx[k]];
                int n = keys.Count;
                part.Positions = new float[n * 3];
                part.Normals = new float[n * 3];
                part.Uvs = new float[n * 2];
                part.Colors = new uint[n];
                part.Indices = idx.ToArray();
                bool missingNormals = false;
                for (int k = 0; k < n; k++)
                {
                    var (v, t, nn) = keys[k];
                    ObjFrame.FromObj(obj.Positions[v * 3], obj.Positions[v * 3 + 1], obj.Positions[v * 3 + 2],
                        out part.Positions[k * 3], out part.Positions[k * 3 + 1], out part.Positions[k * 3 + 2]);
                    if (nn >= 0)
                        ObjFrame.FromObj(obj.Normals[nn * 3], obj.Normals[nn * 3 + 1], obj.Normals[nn * 3 + 2],
                            out part.Normals[k * 3], out part.Normals[k * 3 + 1], out part.Normals[k * 3 + 2]);
                    else missingNormals = true;
                    if (t >= 0)
                    {
                        part.Uvs[k * 2] = obj.Uvs[t * 2];
                        part.Uvs[k * 2 + 1] = 1f - obj.Uvs[t * 2 + 1];
                    }
                    float r = obj.Colors[v * 3];
                    if (float.IsNaN(r)) part.Colors[k] = average;
                    else
                        part.Colors[k] = alpha | (Byte(r) << 16) | (Byte(obj.Colors[v * 3 + 1]) << 8) | Byte(obj.Colors[v * 3 + 2]);
                }
                if (missingNormals) SmoothNormals(part, keys);

                // keep the original part's winding relative to its normals (MeshFactory re-derives it anyway)
                if (tpl != null && Math.Sign(Facing(tpl)) * Math.Sign(Facing(part)) < 0)
                    for (int k = 0; k + 2 < part.Indices.Length; k += 3)
                        (part.Indices[k + 1], part.Indices[k + 2]) = (part.Indices[k + 2], part.Indices[k + 1]);
                if (tpl?.Tangents != null) part.Tangents = ComputeTangents(part);
                if (part.Indices.Length > 0) result.Add(part);
            }
            return result;
        }

        static uint Byte(float f) => (uint)Math.Max(0, Math.Min(255, (int)Math.Round(f * 255f)));

        /// <summary>Per-channel average of every vertex colour (the baked lighting of world meshes).</summary>
        public static uint AverageColor(IEnumerable<SolidPart> parts)
        {
            double a = 0, r = 0, g = 0, b = 0;
            long n = 0;
            foreach (var p in parts)
            {
                if (p.Colors == null) continue;
                foreach (uint c in p.Colors)
                {
                    a += c >> 24; r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF;
                    n++;
                }
            }
            if (n == 0) return 0xFFFFFFFFu;
            return ((uint)Math.Round(a / n) << 24) | ((uint)Math.Round(r / n) << 16) | ((uint)Math.Round(g / n) << 8) | (uint)Math.Round(b / n);
        }

        /// <summary>Sum over triangles of (winding normal . vertex normals): positive = counter-clockwise faces the normals.</summary>
        public static double Facing(SolidPart p)
        {
            double s = 0;
            var P = p.Positions;
            var N = p.Normals;
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                int a = p.Indices[t] * 3, b = p.Indices[t + 1] * 3, c = p.Indices[t + 2] * 3;
                double ux = P[b] - P[a], uy = P[b + 1] - P[a + 1], uz = P[b + 2] - P[a + 2];
                double vx = P[c] - P[a], vy = P[c + 1] - P[a + 1], vz = P[c + 2] - P[a + 2];
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                s += cx * (N[a] + N[b] + N[c]) + cy * (N[a + 1] + N[b + 1] + N[c + 1]) + cz * (N[a + 2] + N[b + 2] + N[c + 2]);
            }
            return s;
        }

        // area-weighted face normals summed per OBJ position, so seams split by UVs stay smooth
        static void SmoothNormals(SolidPart part, List<(int v, int t, int n)> keys)
        {
            var acc = new Dictionary<int, double[]>();
            var P = part.Positions;
            for (int t = 0; t + 2 < part.Indices.Length; t += 3)
            {
                int ia = part.Indices[t], ib = part.Indices[t + 1], ic = part.Indices[t + 2];
                int a = ia * 3, b = ib * 3, c = ic * 3;
                double ux = P[b] - P[a], uy = P[b + 1] - P[a + 1], uz = P[b + 2] - P[a + 2];
                double vx = P[c] - P[a], vy = P[c + 1] - P[a + 1], vz = P[c + 2] - P[a + 2];
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                foreach (int vi in new[] { ia, ib, ic })
                {
                    if (!acc.TryGetValue(keys[vi].v, out var s)) acc[keys[vi].v] = s = new double[3];
                    s[0] += cx; s[1] += cy; s[2] += cz;
                }
            }
            for (int k = 0; k < keys.Count; k++)
            {
                if (keys[k].n >= 0 || !acc.TryGetValue(keys[k].v, out var s)) continue;
                double l = Math.Sqrt(s[0] * s[0] + s[1] * s[1] + s[2] * s[2]);
                if (l < 1e-12) { part.Normals[k * 3 + 2] = 1; continue; }
                part.Normals[k * 3] = (float)(s[0] / l);
                part.Normals[k * 3 + 1] = (float)(s[1] / l);
                part.Normals[k * 3 + 2] = (float)(s[2] / l);
            }
        }

        /// <summary>
        /// Tangent xyzw per vertex in game space (UV-derivative accumulation, Gram-Schmidt against the normal). w is the
        /// binormal sign as the 60-byte world vertices store it: w = sign(dot(cross(n, t), dP/dv)) with the stored V.
        /// Checked against the game's own tangents (ModTool verify-models over STREAML2RA X0, 89k vertices): mean dot 0.998,
        /// sign agrees 99.9%.
        /// </summary>
        public static float[] ComputeTangents(SolidPart p)
        {
            int n = p.VertexCount;
            var tan = new double[n * 3];
            var bit = new double[n * 3];
            var P = p.Positions;
            var U = p.Uvs;
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                int i0 = p.Indices[t], i1 = p.Indices[t + 1], i2 = p.Indices[t + 2];
                double x1 = P[i1 * 3] - P[i0 * 3], y1 = P[i1 * 3 + 1] - P[i0 * 3 + 1], z1 = P[i1 * 3 + 2] - P[i0 * 3 + 2];
                double x2 = P[i2 * 3] - P[i0 * 3], y2 = P[i2 * 3 + 1] - P[i0 * 3 + 1], z2 = P[i2 * 3 + 2] - P[i0 * 3 + 2];
                double s1 = U[i1 * 2] - U[i0 * 2], t1 = U[i1 * 2 + 1] - U[i0 * 2 + 1];
                double s2 = U[i2 * 2] - U[i0 * 2], t2 = U[i2 * 2 + 1] - U[i0 * 2 + 1];
                double det = s1 * t2 - s2 * t1;
                if (Math.Abs(det) < 1e-20) continue;
                double r = 1.0 / det;
                double tx = (t2 * x1 - t1 * x2) * r, ty = (t2 * y1 - t1 * y2) * r, tz = (t2 * z1 - t1 * z2) * r;
                double bx = (s1 * x2 - s2 * x1) * r, by = (s1 * y2 - s2 * y1) * r, bz = (s1 * z2 - s2 * z1) * r;
                foreach (int i in new[] { i0, i1, i2 })
                {
                    tan[i * 3] += tx; tan[i * 3 + 1] += ty; tan[i * 3 + 2] += tz;
                    bit[i * 3] += bx; bit[i * 3 + 1] += by; bit[i * 3 + 2] += bz;
                }
            }
            var result = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                double nx = p.Normals[i * 3], ny = p.Normals[i * 3 + 1], nz = p.Normals[i * 3 + 2];
                double tx = tan[i * 3], ty = tan[i * 3 + 1], tz = tan[i * 3 + 2];
                double d = nx * tx + ny * ty + nz * tz;
                tx -= nx * d; ty -= ny * d; tz -= nz * d;
                double l = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (l < 1e-12)
                {
                    // any vector perpendicular to the normal
                    if (Math.Abs(nx) < 0.9) { tx = 0; ty = -nz; tz = ny; } else { tx = nz; ty = 0; tz = -nx; }
                    l = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                    if (l < 1e-12) { tx = 1; ty = 0; tz = 0; l = 1; }
                }
                tx /= l; ty /= l; tz /= l;
                double cx = ny * tz - nz * ty, cy = nz * tx - nx * tz, cz = nx * ty - ny * tx;
                double w = cx * bit[i * 3] + cy * bit[i * 3 + 1] + cz * bit[i * 3 + 2] < 0 ? -1 : 1;
                result[i * 4] = (float)tx;
                result[i * 4 + 1] = (float)ty;
                result[i * 4 + 2] = (float)tz;
                result[i * 4 + 3] = (float)w;
            }
            return result;
        }

        static bool Tight(List<SolidPart> parts, out Vec3 min, out Vec3 max)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            foreach (var p in parts)
                for (int i = 0; i + 2 < p.Positions.Length; i += 3)
                {
                    x0 = Math.Min(x0, p.Positions[i]); x1 = Math.Max(x1, p.Positions[i]);
                    y0 = Math.Min(y0, p.Positions[i + 1]); y1 = Math.Max(y1, p.Positions[i + 1]);
                    z0 = Math.Min(z0, p.Positions[i + 2]); z1 = Math.Max(z1, p.Positions[i + 2]);
                }
            min = new Vec3(x0, y0, z0);
            max = new Vec3(x1, y1, z1);
            return x0 <= x1;
        }
    }
}
