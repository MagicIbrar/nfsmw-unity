using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>Front end resources: FEng packages, UI textures, fonts, localised strings and object names.</summary>
    public static class FrontEndData
    {
        public static Dictionary<string, FEPackage> Packages { get; private set; }
        public static TextureLibrary Textures { get; private set; }
        public static StringTable Strings { get; private set; }
        public static readonly Dictionary<uint, FEFont> Fonts = new Dictionary<uint, FEFont>();
        /// <summary>upper-case bStringHash -> name, from speed.exe's string table (object, script and message names).</summary>
        public static readonly Dictionary<uint, string> Names = new Dictionary<uint, string>();

        static readonly Dictionary<string, string> FontAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FONT_MW_BODY"] = "FONT_MW_BODY",
            ["FONT_MW_TITLE"] = "font_mw_title",
        };

        public static bool Loaded => Packages != null;
        /// <summary>GetLanguageName(GetCurrentLanguage()) (eLanguages table 0x8F40F8, Core/LangLocalize.cs).</summary>
        public static string LanguageName => Localize.GetLanguageName(Localize.CurrentLanguage);

        /// <summary>LanguageInfo.Filename of the current language (LoadLanguageResources 0x56B950), relative to the data root.</summary>
        static string LanguageFile => (Localize.GetLanguageInfo(Localize.CurrentLanguage)?.Filename ?? "LANGUAGES/ENGLISH.BIN").Replace('\\', '/');

        public static void Load()
        {
            if (Loaded) return;
            GameData.Mount();
            string root = GameData.Root;
            Textures = new TextureLibrary();
            Packages = new Dictionary<string, FEPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in new[] { "FRONTEND/FRONTA.BUN", "GLOBAL/RIVALS.BIN", "GLOBAL/CardTextures.bin", "FRONTEND/FrontB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/InGameA.bun", "GLOBAL/InGameB.lzc", "GLOBAL/InGameSplitScreen.bun", "GLOBAL/HUDTEXRACE.BIN", "GLOBAL/HUDTEXDRAG.BIN", "GLOBAL/HUDS_Custom_00.bin", "LANGUAGES/LanguageTextures.bin", LanguageFile })
            {
                var path = Path.Combine(root, f);
                if (!File.Exists(path)) continue;
                var d = GameFiles.ReadBundle(path);
                Textures.Add(TexturePack.ReadAll(d, 0, d.Length));
                foreach (var kv in FEPackage.ReadBundle(d)) Packages[kv.Key] = kv.Value;
                foreach (var font in FEFont.ReadAll(d, 0, d.Length))
                    if (font.Glyphs.Count > 0) Fonts[font.Hash] = font;   // FEngFont::FontHash: the name without its language suffix (0x585BA0)
                if (f == LanguageFile) Strings = StringTable.Read(d);
            }
            if (Strings == null && LanguageFile != "LANGUAGES/ENGLISH.BIN")
            {
                // [ours] the registry language's file is missing: English, as the install's default
                var en = Path.Combine(root, "LANGUAGES/English.bin");
                if (File.Exists(en)) Strings = StringTable.Read(GameFiles.ReadBundle(en));
            }
            Strings ??= new StringTable();
            LoadNames(Path.Combine(root, "speed.exe"));
            Debug.Log($"NFSMW FE: {Packages.Count} packages, {Textures.Count} UI textures, {Fonts.Count} fonts, {Strings.Count} strings, {Names.Count} names");
        }

        static void LoadNames(string exe)
        {
            if (!File.Exists(exe)) return;
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(exe));
            foreach (Match m in Regex.Matches(text, @"[A-Za-z0-9_ .%\-]{2,64}"))
            {
                var s = m.Value;
                if (s.Contains("%d") || s.Contains("%02d") || s.Contains("%i"))
                {
                    for (int i = 0; i <= 40; i++)
                        AddName(s.Replace("%02d", i.ToString("00")).Replace("%d", i.ToString()).Replace("%i", i.ToString()));
                }
                else AddName(s);
            }
        }

        static void AddName(string s)
        {
            uint h = EaHash.HashUpper(s);
            if (!Names.ContainsKey(h)) Names[h] = s;
        }

        public static uint Hash(string name) => EaHash.HashUpper(name);

        public static FEFont FontFor(string resource)
        {
            var name = Path.GetFileNameWithoutExtension(resource ?? "");
            if (FontAlias.TryGetValue(name, out var alias)) name = alias;
            if (Fonts.TryGetValue(EaHash.HashUpper(name), out var f)) return f;
            foreach (var kv in Fonts) return kv.Value;
            return null;
        }

        public static Texture2D Texture(string resource)
        {
            if (string.IsNullOrEmpty(resource) || resource.StartsWith("(")) return null;
            uint h = EaHash.HashUpper(Path.GetFileNameWithoutExtension(resource));
            return Track(h, Textures.Get(h));
        }

        public static Texture2D Texture(uint hash) => Track(hash, Textures.Get(hash));

        static readonly Dictionary<Texture, GameTexture> SourceByTexture = new Dictionary<Texture, GameTexture>();

        static Texture2D Track(uint hash, Texture2D t)
        {
            if (t != null && !SourceByTexture.ContainsKey(t)) { var src = Textures.Source(hash); if (src != null) SourceByTexture[t] = src; }
            return t;
        }

        /// <summary>The TPK record of an FE texture handed out here (blend / alpha-usage types for FEView's blend state).</summary>
        public static GameTexture SourceOf(Texture t) => t != null && SourceByTexture.TryGetValue(t, out var s) ? s : null;

        /// <summary>By full texture name; falls back to the 24-character names stored in the packs.</summary>
        public static Texture2D TextureByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            uint h = EaHash.HashUpper(name);
            if (Textures.Contains(h)) return Track(h, Textures.Get(h));
            string up = name.ToUpperInvariant();
            string prefix = up.Length > 23 ? up.Substring(0, 23) : up;
            uint found = 0;
            int matches = 0;
            foreach (var t in Textures.All)
                if (t.Name != null && (t.Name == up || up.Length > 23 && t.Name.StartsWith(prefix)))
                {
                    if (t.Name == up) return Track(t.Hash, Textures.Get(t.Hash));
                    if (t.Hash != found) { found = t.Hash; matches++; }
                }
            // a cut-off name only counts when it is unambiguous (many share e.g. "CARSELECT_MANUFACTURER_")
            return matches == 1 ? Track(found, Textures.Get(found)) : null;
        }

        /// <summary>Localised text for a label hash, or the fallback.</summary>
        public static string Text(uint label, string fallback) => Strings != null && Strings.TryGet(label, out var s) ? s : fallback;

        /// <summary>GetLocalizedString (0x56BC10): the label's string, else the 0x9BB9CCC3 one ("FENG: Default string error.").</summary>
        public static string Localized(uint label) => Text(label, null) ?? Text(Localize.DefaultStringError, "");

        /// <summary>DoesStringExist (0x56BC00).</summary>
        public static bool DoesStringExist(uint label) => Strings != null && Strings.TryGet(label, out _);
        public static string Text(string label) => Strings?.Get(label) ?? label;
    }
}
