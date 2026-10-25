using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NfsMw.Core;

namespace NfsMw.ModTool
{
    /// <summary>
    /// [ours] NFSMW mod tool. Exports the game's textures (PNG) and models (OBJ + MTL), installs edited files into the Mods
    /// folder the rebuild reads (Core/Mods.cs), and lists what the Mods folder overrides. It only reads the game folder.
    /// See MODDING.md at the project root.
    /// </summary>
    static class Program
    {
        const string DefaultRoot = @"C:\Program Files (x86)\EA GAMES\Need for Speed Most Wanted";
        const string OldRoot = @"C:\Program Files\EA GAMES\Need for Speed Most Wanted";
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        static readonly Dictionary<string, string> Opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> Pos = new List<string>();
        static readonly HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--with-mods", "--force" };

        const string Usage = @"NFSMW mod tool  (game data is only read; replacements go into the Mods folder)

  list <gamefile> [filter] [--with-mods]   textures and solids in a game file
  export-textures <gamefile> <outdir> [filter]
                                           every texture to <outdir>/<Pack>/<Name>.png + textures.json
  export-models <gamefile> <outdir> [filter]
                                           every solid to <outdir>/<Solid>.obj/.mtl + models.json
                                           (textures the same file holds go to <outdir>/textures/...)
  import <file.png|file.obj> [--name X] [--pack P] [--from <gamefile>] [--force]
                                           checks the file and copies it into the Mods folder
  mods                                     what the Mods folder overrides
  diff <gamefile> [filter]                 reads the file without and with the Mods folder: what each override changes
  verify-models <gamefile> [filter]        OBJ export -> import round trip check (no files written)

  <gamefile> is relative to the game folder: GLOBAL/GLOBALB.LZC, CARS/BMWM3GTRE46/TEXTURES.BIN, a folder such as
  CARS/BMWM3GTRE46 (all its .BIN files), or a world section TRACKS/STREAML2RA.BUN:<section> (list TRACKS/STREAML2RA.BUN
  shows the sections). filter = case-insensitive part of a name.

  --game <folder>   the game folder (else gameroot.txt, NFSMW_ROOT, the default install path)
  --mods <folder>   the Mods folder (else NFSMW_MODS, else NFSMWUnity/Mods when run from the project tree,
                    else Mods next to ModTool). The built player reads Mods next to its exe.";

        static int Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    if (Flags.Contains(args[i])) Opt[args[i]] = "1";
                    else if (i + 1 < args.Length) Opt[args[i]] = args[++i];
                    else { Console.Error.WriteLine($"{args[i]} needs a value"); return 2; }
                }
                else Pos.Add(args[i]);
            }
            if (Pos.Count == 0) { Console.WriteLine(Usage); return 1; }
            Mods.Log = s => Console.WriteLine("  " + s);
            try
            {
                switch (Pos[0].ToLowerInvariant())
                {
                    case "list": Need(2); return List(Pos[1], Arg(2));
                    case "export-textures": Need(3); return ExportTextures(Pos[1], Pos[2], Arg(3));
                    case "export-models": Need(3); return ExportModels(Pos[1], Pos[2], Arg(3));
                    case "import": Need(2); return Import(Pos[1]);
                    case "mods": return ListMods();
                    case "verify-models": Need(2); return VerifyModels(Pos[1], Arg(2));
                    case "diff": Need(2); return Diff(Pos[1], Arg(2));
                    case "png-check": // decoder test: re-encodes what the rebuild would load from a PNG
                        Need(3);
                        if (!Png.Decode(File.ReadAllBytes(Pos[1]), out int pw, out int ph, out var ppx, out string perr)) { Console.Error.WriteLine(perr); return 1; }
                        File.WriteAllBytes(Pos[2], Png.Encode(pw, ph, ppx));
                        Console.WriteLine($"{pw}x{ph}");
                        return 0;
                    default: Console.WriteLine(Usage); return 1;
                }
            }
            catch (UsageException e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        sealed class UsageException : Exception { public UsageException(string m) : base(m) { } }

        static void Need(int n) { if (Pos.Count < n) throw new UsageException(Usage); }
        static string Arg(int i) => i < Pos.Count ? Pos[i] : null;
        static bool Match(string name, string filter) => filter == null || (name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        // ---------------------------------------------------------------- folders

        static string _root;

        /// <summary>Like GameData.ResolveRoot: --game, gameroot.txt, NFSMW_ROOT, the default install, the old copy.</summary>
        static string GameRoot()
        {
            if (_root != null) return _root;
            var c = new List<string>();
            if (Opt.TryGetValue("--game", out var g)) c.Add(g);
            var proj = ProjectDir();
            var dirs = new List<string> { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            if (proj != null) { dirs.Add(proj); dirs.Add(Path.Combine(proj, "Build", "NFSMWUnity")); }
            foreach (var d in dirs)
            {
                var f = Path.Combine(d, "gameroot.txt");
                if (File.Exists(f)) c.Add(File.ReadAllText(f).Trim());
            }
            var env = Environment.GetEnvironmentVariable("NFSMW_ROOT");
            if (!string.IsNullOrEmpty(env)) c.Add(env);
            c.Add(DefaultRoot);
            c.Add(OldRoot);
            foreach (var r in c)
                if (!string.IsNullOrEmpty(r) && Directory.Exists(Path.Combine(r, "TRACKS")) && Directory.Exists(Path.Combine(r, "GLOBAL")))
                    return _root = Path.GetFullPath(r);
            throw new UsageException("NFS Most Wanted data not found: pass --game <folder> (the one with GLOBAL, TRACKS, CARS).");
        }

        /// <summary>NFSMWUnity when the tool runs from inside the project tree (bin folder or cwd), else null.</summary>
        static string ProjectDir()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                {
                    var p = Path.Combine(d.FullName, "NFSMWUnity");
                    if (Directory.Exists(Path.Combine(p, "Assets"))) return p;
                    if (d.Name.Equals("NFSMWUnity", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(d.FullName, "Assets"))) return d.FullName;
                }
            return null;
        }

        static string ModsDir()
        {
            string m = Opt.TryGetValue("--mods", out var o) ? o : Environment.GetEnvironmentVariable("NFSMW_MODS");
            if (string.IsNullOrEmpty(m))
            {
                var proj = ProjectDir();
                m = proj != null ? Path.Combine(proj, "Mods") : Path.Combine(AppContext.BaseDirectory, "Mods");
            }
            return Path.GetFullPath(m);
        }

        /// <summary>Refuses any output inside the game folder: the tool never writes there.</summary>
        static void NotInGame(string dir)
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string root = GameRoot().TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UsageException($"refusing to write inside the game folder ({dir}). Pick a folder outside {GameRoot()}.");
        }

        static readonly char[] Bad = Path.GetInvalidFileNameChars();
        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var b = new StringBuilder(s.Length);
            foreach (char ch in s) b.Append(Array.IndexOf(Bad, ch) >= 0 ? '_' : ch);
            return b.ToString();
        }

        // ---------------------------------------------------------------- game files

        sealed class Source
        {
            public string Label;
            public byte[] Data;
        }

        /// <summary>The decoded buffers a &lt;gamefile&gt; names (a file, a folder of .BIN/.BUN/.LZC, or STREAMxxx.BUN:section).</summary>
        static List<Source> Open(string spec, bool listSections = false)
        {
            string root = GameRoot();
            string file = spec, section = null;
            int colon = spec.LastIndexOf(':');
            if (colon > 1) { file = spec.Substring(0, colon); section = spec.Substring(colon + 1); }
            string path = Path.IsPathRooted(file) ? file : Path.Combine(root, file);
            var list = new List<Source>();
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.GetFiles(path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string ext = Path.GetExtension(f).ToUpperInvariant();
                    if (ext == ".BIN" || ext == ".BUN" || ext == ".LZC")
                        list.Add(new Source { Label = Path.GetFileName(f), Data = GameFiles.ReadBundle(f) });
                }
                return list;
            }
            if (!File.Exists(path)) throw new UsageException($"not found: {path}");
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("STREAM", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".BUN", StringComparison.OrdinalIgnoreCase))
            {
                string track = name.Substring(6);
                using var ts = new TrackStream(root, track);
                if (section == null)
                {
                    if (listSections)
                    {
                        Console.WriteLine($"{ts.Sections.Count} sections in {file} (use {file}:<section>):");
                        foreach (var row in ts.Sections.GroupBy(s => s.Name.TrimEnd("0123456789".ToCharArray())))
                            Console.WriteLine("  " + string.Join(" ", row.Select(s => s.Name)));
                        return null;
                    }
                    throw new UsageException($"{file} is the streamed world: name a section, e.g. {file}:{ts.Sections[ts.Sections.Count / 2].Name} (list {file} shows them)");
                }
                var sec = ts.Find(section) ?? throw new UsageException($"no section {section} in {file}");
                list.Add(new Source { Label = $"{Path.GetFileName(path)}:{sec.Name}", Data = ts.ReadRaw(sec) });
                return list;
            }
            list.Add(new Source { Label = Path.GetFileName(path), Data = GameFiles.ReadBundle(path) });
            return list;
        }

        static List<GameTexture> Textures(List<Source> src, bool withData = true)
        {
            var all = new List<GameTexture>();
            foreach (var s in src) all.AddRange(TexturePack.ReadAll(s.Data, 0, s.Data.Length, withData));
            return all;
        }

        static List<Solid> Solids(List<Source> src)
        {
            var all = new List<Solid>();
            foreach (var s in src) all.AddRange(SolidList.ReadAll(s.Data, 0, s.Data.Length));
            return all;
        }

        static void UseMods(bool on)
        {
            Mods.Root = on ? ModsDir() : null;
            if (on && !Mods.Active) Console.WriteLine($"(no Mods folder at {ModsDir()})");
        }

        // ---------------------------------------------------------------- list

        static int List(string spec, string filter)
        {
            UseMods(Opt.ContainsKey("--with-mods"));
            var src = Open(spec, true);
            if (src == null) return 0;
            var tex = Textures(src).Where(t => Match(t.Name, filter)).ToList();
            var sol = Solids(src).Where(s => Match(s.Name, filter)).ToList();
            Console.WriteLine($"{tex.Count} textures");
            foreach (var t in tex)
            {
                string mod = Mods.TextureFile(t.PackName, t.Name, t.Hash) != null ? "  [mod]" : "";
                Console.WriteLine($"  {t.Name,-32} {t.PackName,-24} {t.Width,5}x{t.Height,-5} {t.Format,-7} mips {t.MipCount,2}  alpha {t.AlphaUsage}/{t.AlphaBlend}{mod}");
            }
            Console.WriteLine($"{sol.Count} solids");
            foreach (var s in sol)
            {
                int v = s.Parts.Sum(p => p.VertexCount), tr = s.Parts.Sum(p => p.Indices.Length / 3);
                string mod = Mods.ModelFile(s.Name) != null ? "  [mod]" : "";
                Console.WriteLine($"  {s.Name,-40} parts {s.Parts.Count,3}  verts {v,6}  tris {tr,6}{mod}");
            }
            return 0;
        }

        // ---------------------------------------------------------------- textures

        static int ExportTextures(string spec, string outDir, string filter)
        {
            NotInGame(outDir);
            UseMods(false);
            var src = Open(spec);
            var tex = Textures(src).Where(t => Match(t.Name, filter)).ToList();
            Directory.CreateDirectory(outDir);
            var meta = new List<object>();
            int ok = 0, failed = 0, hashed = 0;
            var shared = SharedNames(tex);
            foreach (var t in tex)
            {
                var px = TextureDecode.ToBgra(t, out string error);
                string rel = Path.Combine(Safe(t.PackName), Safe(FileName(t, shared)) + ".png");
                if (FileName(t, shared) != t.Name) hashed++;
                if (px == null)
                {
                    Console.WriteLine($"  skipped {t.PackName}/{t.Name}: {error}");
                    failed++;
                    continue;
                }
                if (Safe(t.Name) != t.Name || Safe(t.PackName) != t.PackName)
                    Console.WriteLine($"  note: {t.PackName}/{t.Name} has characters a file name cannot hold; it cannot be overridden by file name");
                string file = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, Png.Encode(t.Width, t.Height, px));
                ok++;
                meta.Add(new
                {
                    name = t.Name,
                    hash = $"0x{t.Hash:X8}",
                    pack = t.PackName,
                    file = rel.Replace('\\', '/'),
                    width = t.Width,
                    height = t.Height,
                    format = t.Format.ToString(),
                    fourcc = t.FourCC,
                    mips = t.MipCount,
                    alphaUsage = t.AlphaUsage,
                    alphaBlend = t.AlphaBlend,
                    tileUV = t.TileUV,
                    scrollType = t.ScrollType,
                });
            }
            WriteJson(Path.Combine(outDir, "textures.json"), new { source = spec, textures = meta });
            var dupes = tex.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(t => t.PackName).Distinct().Count() > 1).Select(g => g.Key).ToList();
            Console.WriteLine($"exported {ok} textures to {outDir}" + (failed > 0 ? $", {failed} skipped" : ""));
            if (hashed > 0)
                Console.WriteLine($"  {hashed} textures share their (24-character) name with another in the same pack: they are saved as <Name>@<HASH>.png; keep that name when importing");
            if (dupes.Count > 0)
                Console.WriteLine($"  {dupes.Count} names appear in several packs (e.g. {string.Join(", ", dupes.Take(3))}): import those with --pack to replace one copy only");
            return failed > 0 && ok == 0 ? 1 : 0;
        }

        /// <summary>"pack/name" keys used by more than one distinct texture (TextureInfo keeps only 24 characters of a name).</summary>
        static HashSet<string> SharedNames(List<GameTexture> tex) =>
            new HashSet<string>(tex.GroupBy(t => (t.PackName + "/" + t.Name).ToUpperInvariant()).Where(g => g.Select(t => t.Hash).Distinct().Count() > 1).Select(g => g.Key));

        /// <summary>The override file name for a texture: Name, or Name@HASH when the name alone is ambiguous in its pack.</summary>
        static string FileName(GameTexture t, HashSet<string> shared) =>
            shared.Contains((t.PackName + "/" + t.Name).ToUpperInvariant()) ? $"{t.Name}@{t.Hash:X8}" : t.Name;

        static void WriteJson(string file, object o) =>
            File.WriteAllText(file, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));

        // ---------------------------------------------------------------- models

        /// <summary>Texture name lookup for materials: the file's own textures, then the shared packs that usually hold the rest.</summary>
        sealed class TextureNames
        {
            readonly Dictionary<uint, GameTexture> _own = new Dictionary<uint, GameTexture>();
            Dictionary<uint, string> _shared;
            readonly string _spec;

            public TextureNames(List<GameTexture> own, string spec)
            {
                foreach (var t in own) if (!_own.ContainsKey(t.Hash)) _own[t.Hash] = t;
                _spec = spec;
            }

            /// <summary>Targets the rebuild fills at run time (car skin compositor, decals, numbers): no file holds them.</summary>
            IEnumerable<string> RuntimeNames()
            {
                foreach (var g in new[] { "CARSKIN", "GLOBAL_SKIN1", "ROOF_SKIN", "SPOILER_SKIN1", "DUMMY_NUMBER_LEFT", "DUMMY_NUMBER_RIGHT" }) yield return g;
                for (int i = 1; i <= 6; i++) yield return "DUMMY_DECAL" + i;
                var parts = _spec.Replace('\\', '/').Split('/');
                int c = Array.FindIndex(parts, x => x.Equals("CARS", StringComparison.OrdinalIgnoreCase));
                if (c >= 0 && c + 1 < parts.Length)
                {
                    string car = parts[c + 1].ToUpperInvariant();
                    foreach (var suffix in new[] { "_SKIN1", "_SKIN1B", "_SKIN1_N", "_WHEEL", "_SPOILER_SKIN1", "_ROOF_SKIN" }) yield return car + suffix;
                }
            }

            public GameTexture Own(uint hash) => _own.TryGetValue(hash, out var t) ? t : null;

            public string Name(uint hash)
            {
                if (hash == 0) return "none";
                if (_own.TryGetValue(hash, out var t)) return t.Name;
                if (_shared == null)
                {
                    _shared = new Dictionary<uint, string>();
                    var files = new List<string> { "CARS/TEXTURES.BIN", "GLOBAL/GLOBALB.LZC", "GLOBAL/InGameB.lzc" };
                    if (_spec.IndexOf("STREAM", StringComparison.OrdinalIgnoreCase) >= 0 && _spec.Contains(':'))
                        files.Insert(0, _spec.Substring(0, _spec.LastIndexOf(':')) + ":Y0");
                    foreach (var f in files)
                    {
                        try
                        {
                            foreach (var g in Textures(Open(f)))
                                if (!_shared.ContainsKey(g.Hash)) _shared[g.Hash] = g.Name;
                        }
                        catch (Exception e) when (e is UsageException || e is IOException) { }
                    }
                }
                if (_shared.TryGetValue(hash, out var n)) return n;
                foreach (var guess in RuntimeNames())
                    if (EaHash.Hash(guess) == hash) return _shared[hash] = guess;
                return $"0x{hash:X8}";
            }
        }

        static string F(float f) => f.ToString(CI);

        /// <summary>One solid as OBJ text (frame: ObjFrame) and its MTL. texturePath maps a part to its map_Kd, or null.</summary>
        static void WriteObj(Solid s, TextureNames names, Func<SolidPart, string> texturePath, out string obj, out string mtl)
        {
            var o = new StringBuilder();
            var m = new StringBuilder();
            o.Append("# NFSMW solid ").Append(s.Name).Append($" hash 0x{s.Hash:X8}\n");
            o.Append("# frame: OBJ = (game x, game z, -game y), Y up; vt = (u, 1 - v); v x y z r g b = baked vertex colour\n");
            o.Append("# one usemtl per part: <part index>_<material>_<texture>; keep these names when editing\n");
            o.Append("mtllib ").Append(Safe(s.Name)).Append(".mtl\n");
            o.Append("o ").Append(s.Name).Append('\n');
            int baseV = 1;
            for (int pi = 0; pi < s.Parts.Count; pi++)
            {
                var p = s.Parts[pi];
                int n = p.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    ObjFrame.ToObj(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2], out float x, out float y, out float z);
                    uint c = p.Colors[i];
                    o.Append("v ").Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(z)).Append(' ')
                        .Append((((c >> 16) & 0xFF) / 255.0).ToString("0.######", CI)).Append(' ')
                        .Append((((c >> 8) & 0xFF) / 255.0).ToString("0.######", CI)).Append(' ')
                        .Append(((c & 0xFF) / 255.0).ToString("0.######", CI)).Append('\n');
                }
                for (int i = 0; i < n; i++)
                    o.Append("vt ").Append(F(p.Uvs[i * 2])).Append(' ').Append(F(1f - p.Uvs[i * 2 + 1])).Append('\n');
                for (int i = 0; i < n; i++)
                {
                    ObjFrame.ToObj(p.Normals[i * 3], p.Normals[i * 3 + 1], p.Normals[i * 3 + 2], out float x, out float y, out float z);
                    o.Append("vn ").Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(z)).Append('\n');
                }
                string texName = names.Name(p.TextureHash);
                string mat = ObjFrame.MaterialName(pi, p, texName);
                o.Append("g ").Append(mat).Append('\n');
                o.Append("usemtl ").Append(mat).Append('\n');
                // counter-clockwise towards the normals, as OBJ tools expect (the rotation into the OBJ frame keeps winding)
                bool flip = ModelOverride.Facing(p) < 0;
                for (int t = 0; t + 2 < p.Indices.Length; t += 3)
                {
                    int a = p.Indices[t] + baseV, b = p.Indices[t + 1] + baseV, c = p.Indices[t + 2] + baseV;
                    if (flip) (b, c) = (c, b);
                    o.Append("f ").Append(a).Append('/').Append(a).Append('/').Append(a).Append(' ')
                        .Append(b).Append('/').Append(b).Append('/').Append(b).Append(' ')
                        .Append(c).Append('/').Append(c).Append('/').Append(c).Append('\n');
                }
                baseV += n;

                m.Append("newmtl ").Append(mat).Append('\n');
                m.Append($"# effect {p.EffectId} flags 0x{p.Flags:X8} texture 0x{p.TextureHash:X8}\n");
                m.Append("Kd 1 1 1\n");
                string tp = texturePath(p);
                if (tp != null) m.Append("map_Kd ").Append(tp).Append('\n');
                else if (texName.EndsWith("SKIN1") || texName.EndsWith("SKIN1B") || texName.StartsWith("DUMMY_"))
                    m.Append("# texture ").Append(texName).Append(" is built at run time (paint, vinyls, decals); no file to export or replace\n");
                else m.Append("# texture ").Append(texName).Append(" is not in this game file\n");
                m.Append('\n');
            }
            obj = o.ToString();
            mtl = m.ToString();
        }

        static int ExportModels(string spec, string outDir, string filter)
        {
            NotInGame(outDir);
            UseMods(false);
            var src = Open(spec);
            var tex = Textures(src);
            var names = new TextureNames(tex, spec);
            var shared = SharedNames(tex);
            var sol = Solids(src).Where(s => Match(s.Name, filter)).ToList();
            Directory.CreateDirectory(outDir);
            var written = new HashSet<uint>();
            var meta = new List<object>();
            foreach (var s in sol)
            {
                string TexPath(SolidPart p)
                {
                    var t = names.Own(p.TextureHash);
                    if (t == null) return null;
                    string rel = $"textures/{Safe(t.PackName)}/{Safe(FileName(t, shared))}.png";
                    if (written.Add(t.Hash))
                    {
                        var px = TextureDecode.ToBgra(t, out string err);
                        if (px == null) { Console.WriteLine($"  texture {t.Name}: {err}"); return null; }
                        string file = Path.Combine(outDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllBytes(file, Png.Encode(t.Width, t.Height, px));
                    }
                    return rel;
                }
                WriteObj(s, names, TexPath, out string obj, out string mtl);
                File.WriteAllText(Path.Combine(outDir, Safe(s.Name) + ".obj"), obj);
                File.WriteAllText(Path.Combine(outDir, Safe(s.Name) + ".mtl"), mtl);
                if (Safe(s.Name) != s.Name) Console.WriteLine($"  note: {s.Name} has characters a file name cannot hold; it cannot be overridden by file name");
                meta.Add(new
                {
                    name = s.Name,
                    hash = $"0x{s.Hash:X8}",
                    file = Safe(s.Name) + ".obj",
                    boundsMin = new[] { s.BoundsMin.X, s.BoundsMin.Y, s.BoundsMin.Z },
                    boundsMax = new[] { s.BoundsMax.X, s.BoundsMax.Y, s.BoundsMax.Z },
                    parts = s.Parts.Select((p, i) => new
                    {
                        index = i,
                        objMaterial = ObjFrame.MaterialName(i, p, names.Name(p.TextureHash)),
                        material = p.MaterialName,
                        texture = names.Name(p.TextureHash),
                        textureHash = $"0x{p.TextureHash:X8}",
                        effect = p.EffectId,
                        flags = $"0x{p.Flags:X8}",
                        vertices = p.VertexCount,
                        triangles = p.Indices.Length / 3,
                        tangents = p.Tangents != null,
                    }).ToList(),
                });
            }
            WriteJson(Path.Combine(outDir, "models.json"), new { source = spec, frame = "OBJ (x, y, z) = game (x, z, -y); vt v = 1 - game v", models = meta });
            Console.WriteLine($"exported {sol.Count} models and {written.Count} textures to {outDir}");
            return 0;
        }

        // ---------------------------------------------------------------- import / mods

        static int Import(string file)
        {
            if (!File.Exists(file)) throw new UsageException($"not found: {file}");
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string name = Opt.TryGetValue("--name", out var n) ? n : Path.GetFileNameWithoutExtension(file);
            Opt.TryGetValue("--pack", out var pack);
            Opt.TryGetValue("--from", out var from);
            bool force = Opt.ContainsKey("--force");
            string mods = ModsDir();
            NotInGame(mods);
            UseMods(false);
            string dest;
            if (ext == ".png")
            {
                if (!Png.Decode(File.ReadAllBytes(file), out int w, out int h, out var px, out string err))
                    throw new UsageException($"{file}: {err}");
                bool alpha = false;
                for (int i = 3; i < px.Length; i += 4) if (px[i] != 255) { alpha = true; break; }
                Console.WriteLine($"{file}: {w}x{h}{(alpha ? " with alpha" : "")}");
                if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0) Console.WriteLine("  warning: not a power of two; it works, but mip maps and tiling look best at powers of two");
                if (from != null)
                {
                    string plain = name;
                    uint? hash = null;
                    int at = name.LastIndexOf('@');
                    if (at > 0 && uint.TryParse(name.Substring(at + 1), NumberStyles.HexNumber, CI, out uint hv)) { plain = name.Substring(0, at); hash = hv; }
                    var hits = Textures(Open(from)).Where(t => string.Equals(t.Name, plain, StringComparison.OrdinalIgnoreCase) && (hash == null || t.Hash == hash)).ToList();
                    if (hits.Count == 0 && !force) throw new UsageException($"no texture named {name} in {from} (use --name, or --force to install anyway)");
                    if (pack != null && hits.Count > 0 && !hits.Any(t => string.Equals(t.PackName, pack, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!force) throw new UsageException($"{name} is not in pack {pack} (it is in {string.Join(", ", hits.Select(t => t.PackName).Distinct())})");
                    }
                    foreach (var t in hits.Where(t => pack == null || string.Equals(t.PackName, pack, StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine($"  replaces {t.PackName}/{t.Name}: {t.Width}x{t.Height} {t.Format}, alpha usage {t.AlphaUsage}");
                        if (t.Width != w || t.Height != h)
                        {
                            if (string.Equals(t.PackName, "VINYL", StringComparison.OrdinalIgnoreCase))
                                Console.WriteLine($"  warning: vinyls and their _MASK are composited at {t.Width}x{t.Height}; at another size the skin compositor skips the vinyl");
                            else if (t.Name.EndsWith("POS") || t.Name.EndsWith("NEG"))
                                Console.WriteLine("  warning: environment cube faces must all be the same square size");
                            else Console.WriteLine($"  warning: size differs from the original {t.Width}x{t.Height} (fine for most textures; FE/HUD art may expect the original aspect)");
                        }
                        if (alpha && t.AlphaUsage == 0 && t.AlphaBlend == 0) Console.WriteLine("  note: the original draws opaque; the alpha channel is kept but not used for transparency");
                    }
                    if (pack == null && hits.Select(t => t.PackName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                        Console.WriteLine($"  note: {name} is in several packs; without --pack every copy is replaced");
                }
                dest = pack != null ? Path.Combine(mods, "textures", Safe(pack), Safe(name) + ".png") : Path.Combine(mods, "textures", Safe(name) + ".png");
            }
            else if (ext == ".obj")
            {
                if (pack != null) Console.WriteLine("  note: --pack applies to textures only");
                var obj = ObjModel.Parse(File.ReadAllText(file));
                int tris = 0, polys = 0;
                foreach (var g in obj.Groups)
                    foreach (var f in g.Faces) { tris += f.Length / 3 - 2; polys++; }
                Console.WriteLine($"{file}: {obj.Positions.Count / 3} positions, {obj.Uvs.Count / 2} uvs, {obj.Normals.Count / 3} normals, {polys} faces ({tris} triangles), {obj.Groups.Count} materials{(obj.HasColors ? ", vertex colours" : "")}");
                if (obj.Groups.Count == 0) throw new UsageException("no faces in the OBJ");
                if (obj.Uvs.Count == 0) Console.WriteLine("  warning: no texture coordinates; the texture will be sampled at one point");
                if (obj.Normals.Count == 0) Console.WriteLine("  note: no normals; smooth normals will be generated");
                if (from != null)
                {
                    var s = Solids(Open(from)).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (s == null && !force) throw new UsageException($"no solid named {name} in {from} (use --name, or --force to install anyway)");
                    if (s != null)
                    {
                        Console.WriteLine($"  replaces {s.Name}: {s.Parts.Count} parts, {s.Parts.Sum(p => p.VertexCount)} vertices, {s.Parts.Sum(p => p.Indices.Length / 3)} triangles");
                        var parts = ModelOverride.Build(s, obj);
                        for (int i = 0; i < obj.Groups.Count && i < parts.Count; i++)
                        {
                            int k = ModelOverride.MatchPart(s.Parts, obj.Groups[i].Material);
                            Console.WriteLine($"    usemtl {obj.Groups[i].Material,-40} -> settings of part {k} ({s.Parts[k].MaterialName}, effect {s.Parts[k].EffectId}): {parts[i].VertexCount} vertices, {parts[i].Indices.Length / 3} triangles");
                            if (parts[i].VertexCount > 65535) Console.WriteLine("    warning: over 65535 vertices in one part (the original game could not draw this; the rebuild can)");
                        }
                        if (!obj.HasColors) Console.WriteLine($"  no vertex colours: the solid's average colour 0x{ModelOverride.AverageColor(s.Parts):X8} is used");
                    }
                }
                dest = Path.Combine(mods, "models", Safe(name) + ".obj");
            }
            else throw new UsageException("import takes a .png (texture) or .obj (model)");

            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (!Path.GetFullPath(file).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) File.Copy(file, dest, true);
            Console.WriteLine($"installed {dest}");
            return 0;
        }

        static int ListMods()
        {
            string dir = ModsDir();
            Mods.Root = dir;
            if (!Mods.Active) { Console.WriteLine($"no Mods folder at {dir}"); return 0; }
            var all = Mods.All().OrderBy(x => x.kind).ThenBy(x => x.key, StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine($"{dir}: {all.Count} overrides");
            foreach (var (kind, key, file) in all)
            {
                string info = "";
                if (kind == "texture")
                    info = Mods.LoadTexture(file, out int w, out int h, out _, out _, out string err) ? $"{w}x{h}" : "BROKEN: " + err;
                else
                {
                    var obj = ModelOverride.Load(file, out string err);
                    info = obj != null ? $"{obj.Groups.Count} materials, {obj.Positions.Count / 3} positions" : "BROKEN: " + err;
                }
                string scope = kind == "texture" ? (key.Contains('/') ? $"pack {key.Substring(0, key.IndexOf('/'))}" : "every pack") : "";
                Console.WriteLine($"  {kind,-7} {key,-48} {info,-28} {scope}");
            }
            return 0;
        }

        // ---------------------------------------------------------------- diff

        /// <summary>The file read as the rebuild reads it, without and with the Mods folder, for every overridden name.</summary>
        static int Diff(string spec, string filter)
        {
            UseMods(false);
            var src = Open(spec);
            var tex0 = Textures(src);
            var sol0 = Solids(src).ToDictionary(x => x.Hash);
            UseMods(true);
            if (!Mods.Active) return 1;
            var tex1 = Textures(src);
            var sol1 = Solids(src);
            int n = 0;
            for (int i = 0; i < tex1.Count && i < tex0.Count; i++)
            {
                var a = tex0[i];
                var b = tex1[i];
                if (!Match(a.Name, filter) || Mods.TextureFile(a.PackName, a.Name, a.Hash) == null) continue;
                n++;
                Console.WriteLine($"texture {a.PackName}/{a.Name} 0x{a.Hash:X8}: {a.Width}x{a.Height} {a.Format} mips {a.MipCount} -> {b.Width}x{b.Height} {b.Format} mips {b.MipCount} (usable {TexturePack.UsableMips(b)}, {b.Data.Length} bytes); alpha {b.AlphaUsage}/{b.AlphaBlend} tile {b.TileUV}");
            }
            foreach (var b in sol1)
            {
                if (!Match(b.Name, filter) || Mods.ModelFile(b.Name) == null || !sol0.TryGetValue(b.Hash, out var a)) continue;
                n++;
                int va = a.Parts.Sum(p => p.VertexCount), vb = b.Parts.Sum(p => p.VertexCount);
                int ta = a.Parts.Sum(p => p.Indices.Length / 3), tb = b.Parts.Sum(p => p.Indices.Length / 3);
                Console.WriteLine($"model {a.Name}: {a.Parts.Count} parts, {va} vertices, {ta} triangles -> {b.Parts.Count} parts, {vb} vertices, {tb} triangles; bounds {a.BoundsMin}-{a.BoundsMax} -> {b.BoundsMin}-{b.BoundsMax}");
                double err = 0;
                bool same = a.Parts.Count == b.Parts.Count;
                for (int i = 0; same && i < a.Parts.Count; i++)
                {
                    same = a.Parts[i].VertexCount == b.Parts[i].VertexCount && a.Parts[i].Indices.Length == b.Parts[i].Indices.Length;
                    for (int k = 0; same && k < a.Parts[i].Positions.Length; k++) err = Math.Max(err, Math.Abs(a.Parts[i].Positions[k] - b.Parts[i].Positions[k]));
                }
                for (int i = 0; i < b.Parts.Count; i++)
                {
                    var p = b.Parts[i];
                    Console.WriteLine($"    part {i}: {p.MaterialName} effect {p.EffectId} texture 0x{p.TextureHash:X8}, {p.VertexCount} vertices, {p.Indices.Length / 3} triangles{(p.Tangents != null ? ", tangents" : "")}, colour[0] 0x{(p.Colors.Length > 0 ? p.Colors[0] : 0):X8}");
                }
                if (same) Console.WriteLine($"    same layout as the original; max position difference {err:G3}");
            }
            Console.WriteLine($"{n} overrides apply to {spec}");
            return 0;
        }

        // ---------------------------------------------------------------- verify

        /// <summary>Exports each solid to OBJ text, parses it back and rebuilds the parts as the runtime would; compares.</summary>
        static int VerifyModels(string spec, string filter)
        {
            UseMods(false);
            var src = Open(spec);
            var names = new TextureNames(Textures(src), spec);
            var sol = Solids(src).Where(s => Match(s.Name, filter)).ToList();
            int bad = 0;
            double worstPos = 0, worstUv = 0, worstN = 0;
            long tanVerts = 0, tanSignAgree = 0, tanNaN = 0, tanClose = 0;
            double tanDot = 0;
            foreach (var s in sol)
            {
                WriteObj(s, names, _ => null, out string objText, out _);
                var obj = ObjModel.Parse(objText);
                var parts = ModelOverride.Build(s, obj);
                var problems = new List<string>();
                if (parts.Count != s.Parts.Count) problems.Add($"parts {parts.Count} != {s.Parts.Count}");
                for (int i = 0; i < Math.Min(parts.Count, s.Parts.Count); i++)
                {
                    var a = s.Parts[i];
                    var b = parts[i];
                    if (a.VertexCount != b.VertexCount) { problems.Add($"part {i} vertices {b.VertexCount} != {a.VertexCount}"); continue; }
                    if (a.Indices.Length != b.Indices.Length) problems.Add($"part {i} triangles {b.Indices.Length / 3} != {a.Indices.Length / 3}");
                    if (a.EffectId != b.EffectId || a.TextureHash != b.TextureHash || a.Flags != b.Flags) problems.Add($"part {i} material settings differ");
                    for (int k = 0; k < a.Positions.Length; k++)
                    {
                        worstPos = Math.Max(worstPos, Math.Abs(a.Positions[k] - b.Positions[k]));
                        worstN = Math.Max(worstN, Math.Abs(a.Normals[k] - b.Normals[k]));
                    }
                    for (int k = 0; k < a.Uvs.Length; k++) worstUv = Math.Max(worstUv, Math.Abs(a.Uvs[k] - b.Uvs[k]));
                    int colorDiff = 0;
                    for (int k = 0; k < a.Colors.Length; k++) if ((a.Colors[k] & 0xFFFFFF) != (b.Colors[k] & 0xFFFFFF)) colorDiff++;
                    if (colorDiff > 0) problems.Add($"part {i} {colorDiff} colours differ");
                    // same triangles with the same winding (as vertex triples, any rotation)
                    var ta = new HashSet<(int, int, int)>();
                    for (int t = 0; t + 2 < a.Indices.Length; t += 3) ta.Add(Canon(a.Indices[t], a.Indices[t + 1], a.Indices[t + 2]));
                    int miss = 0;
                    for (int t = 0; t + 2 < b.Indices.Length; t += 3) if (!ta.Contains(Canon(b.Indices[t], b.Indices[t + 1], b.Indices[t + 2]))) miss++;
                    if (miss > 0) problems.Add($"part {i} {miss} triangles differ");
                    if ((a.Tangents == null) != (b.Tangents == null)) problems.Add($"part {i} tangents presence differs");
                    if (a.Tangents != null && b.Tangents != null)
                        for (int v = 0; v < a.VertexCount; v++)
                        {
                            double dot = a.Tangents[v * 4] * b.Tangents[v * 4] + a.Tangents[v * 4 + 1] * b.Tangents[v * 4 + 1] + a.Tangents[v * 4 + 2] * b.Tangents[v * 4 + 2];
                            if (double.IsNaN(dot)) { tanNaN++; continue; }
                            tanVerts++;
                            tanDot += dot;
                            if (dot > 0.9) tanClose++;
                            if (Math.Sign(a.Tangents[v * 4 + 3]) == Math.Sign(b.Tangents[v * 4 + 3])) tanSignAgree++;
                        }
                }
                if (problems.Count > 0)
                {
                    bad++;
                    Console.WriteLine($"  {s.Name}: {string.Join("; ", problems)}");
                }
            }
            Console.WriteLine($"{sol.Count} solids round-tripped, {bad} with differences; max error position {worstPos:G3}, uv {worstUv:G3}, normal {worstN:G3}");
            if (tanVerts > 0)
                Console.WriteLine($"generated tangents vs the game's: {tanVerts} vertices, mean dot {tanDot / tanVerts:0.000}, within 25 degrees {100.0 * tanClose / tanVerts:0.0}%, binormal sign agrees {100.0 * tanSignAgree / tanVerts:0.0}%" + (tanNaN > 0 ? $" ({tanNaN} original tangents are NaN)" : ""));
            return bad > 0 ? 1 : 0;
        }

        static (int, int, int) Canon(int a, int b, int c)
        {
            if (a <= b && a <= c) return (a, b, c);
            if (b <= a && b <= c) return (b, c, a);
            return (c, a, b);
        }
    }
}
