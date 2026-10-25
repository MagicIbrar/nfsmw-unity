using System;
using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw
{
    /// <summary>Game install location and the data every system shares (attribute database, global textures).</summary>
    public static class GameData
    {
        /// <summary>The retail installer's default folders (64-bit, then 32-bit Windows). [ours] Any other install is set with
        /// gameroot.txt, -gameRoot or NFSMW_ROOT (README.md, "Point it at your game").</summary>
        public const string DefaultRoot = @"C:\Program Files (x86)\EA GAMES\Need for Speed Most Wanted";
        public const string OldRoot = @"C:\Program Files\EA GAMES\Need for Speed Most Wanted";

        public static string Root { get; private set; }
        public static AttribDatabase Attributes { get; private set; }
        public static TextureLibrary Textures { get; private set; }

        /// <summary>
        /// "-gameRoot &lt;path&gt;" on the command line, then gameroot.txt next to the player (or the project),
        /// then the NFSMW_ROOT environment variable, then the fallback. The folder must contain speed.exe's data (GLOBAL, TRACKS, CARS).
        /// </summary>
        public static string ResolveRoot(string fallback = DefaultRoot)
        {
            var candidates = new List<string>();
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-gameRoot", StringComparison.OrdinalIgnoreCase)) candidates.Add(args[i + 1]);
            foreach (var dir in new[] { Path.GetDirectoryName(Application.dataPath), Application.dataPath })
            {
                var file = Path.Combine(dir ?? ".", "gameroot.txt");
                if (File.Exists(file)) candidates.Add(File.ReadAllText(file).Trim());
            }
            var env = Environment.GetEnvironmentVariable("NFSMW_ROOT");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            candidates.Add(fallback);
            candidates.Add(OldRoot);
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && Directory.Exists(Path.Combine(c, "TRACKS")) && Directory.Exists(Path.Combine(c, "GLOBAL")))
                    return c;
            throw new DirectoryNotFoundException("NFS Most Wanted data not found. Put the path of your installed game (the folder with GLOBAL, TRACKS and CARS) in gameroot.txt next to the player, pass -gameRoot <path>, or set NFSMW_ROOT.");
        }

        static bool _modsResolved;

        /// <summary>
        /// [ours] The mod folder (Core/Mods.cs, MODDING.md): "-mods &lt;path&gt;" on the command line, else Mods next to the
        /// player exe (in the editor: next to the project, NFSMWUnity/Mods). A missing folder means no mods.
        /// </summary>
        public static void ResolveMods()
        {
            if (_modsResolved) return;
            _modsResolved = true;
            string dir = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-mods", StringComparison.OrdinalIgnoreCase)) dir = args[i + 1];
            dir ??= Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Mods");
            Mods.Log = Debug.Log;
            Mods.Root = dir;
        }

        public static void Mount(string root = null)
        {
            ResolveMods();
            root ??= ResolveRoot();
            if (Root == root && Attributes != null) return;
            Root = root;
            // FE_ATTRIB.bin (front-end camera angles, rap sheet, car part menus) shares attributes.bin's class layouts
            Attributes = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "FE_ATTRIB.bin"));
            Textures = new TextureLibrary();
            // global packs shared by cars, HUD and world effects
            foreach (var f in new[] { "GLOBAL/GLOBALB.lzc", "GLOBAL/InGameB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/InGameA.bun", "CARS/TEXTURES.BIN" })
            {
                var path = Path.Combine(root, f);
                if (!File.Exists(path)) continue;
                var d = GameFiles.ReadBundle(path);
                Textures.Add(TexturePack.ReadAll(d, 0, d.Length));
            }
            Debug.Log($"NFSMW: mounted {root} ({Attributes.Collections.Count} attribute collections, {Textures.Count} global textures)");
        }
    }
}
