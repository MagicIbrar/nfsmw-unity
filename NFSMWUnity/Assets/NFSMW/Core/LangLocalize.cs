using System;
using System.Text;

namespace NfsMw.Core
{
    /// <summary>eLanguages (Localization/Localize.hpp:13).</summary>
    public enum Language
    {
        None = -1, English = 0, French = 1, German = 2, Italian = 3, Spanish = 4, Dutch = 5, Swedish = 6, Danish = 7,
        Korean = 8, Chinese = 9, Japanese = 10, Thai = 11, Polish = 12, Finnish = 13, Largest = 14, Labels = 15,
    }

    /// <summary>
    /// WideCharHistogram (Localization/WideCharHistogram.cpp, PC 0x56BE80-0x56C05F): the LANGUAGES\*.bin strings are
    /// "packed" — bytes below 0x80 are ASCII, a byte ≥ 0x80 indexes the chunk's histogram table (u32 NumEntries, then
    /// u16 EntryTable[]). English.bin's table maps 0x81..0x85 to ™ “ ” – ‘ and 0xA0 / 0xA9 / 0xAE / 0xC5 / 0xE5 / 0xE9 /
    /// 0xF6 to themselves; an entry 1..0x7F starts a two-byte code (EntryTable[entry·128 + next − 0x80]).
    /// DisableWideStringHistogram (0x91CF88) is a BSS zero on PC, so the histogram is always used.
    /// </summary>
    public sealed class WideCharHistogram
    {
        readonly ushort[] _entries;
        public int NumEntries => _entries.Length;

        WideCharHistogram(ushort[] e) { _entries = e; }

        /// <summary>LoaderLanguage (0x57E960): pWideCharHistogram = chunk data + header.HistogramTablePos (null when 0).</summary>
        public static WideCharHistogram Read(byte[] d, int chunkData, int chunkEnd)
        {
            int pos = BitConverter.ToInt32(d, chunkData);
            if (pos <= 0 || chunkData + pos + 4 > chunkEnd) return null;
            int at = chunkData + pos;
            int n = BitConverter.ToInt32(d, at);
            if (n <= 0 || at + 4 + n * 2 > chunkEnd) return null;
            var e = new ushort[n];
            for (int i = 0; i < n; i++) e[i] = BitConverter.ToUInt16(d, at + 4 + i * 2);
            return new WideCharHistogram(e);
        }

        ushort Entry(int i) => i >= 0 && i < _entries.Length ? _entries[i] : (ushort)0;   // [ours] bounds

        /// <summary>
        /// UnpackString (PC 0x56BFC0 — differs from the GameCube decomp): byte &lt; 0x80 → itself; else e = EntryTable[b]:
        /// e == 0 → the raw byte; e ≥ 0x80 → e; e 1..0x7F → a second byte n (n &lt; 0x80 → error), EntryTable[e·128 + n −
        /// 0x80]; a zero result is an error and prints '_' (0x5F). Stops after the terminating zero.
        /// </summary>
        public string Unpack(byte[] d, int start, int end, out bool error)
        {
            error = false;
            var sb = new StringBuilder();
            int i = start;
            while (i < end)
            {
                int b = d[i++];
                int r;
                if (b < 0x80) r = b;
                else
                {
                    r = Entry(b);
                    if (r < 0x80)
                    {
                        if (r == 0) r = b;
                        else
                        {
                            int n = i < end ? d[i++] : 0;
                            r = n < 0x80 ? 0 : Entry(r * 0x80 + n - 0x80);
                        }
                    }
                    if (r == 0) { error = true; r = 0x5F; }
                }
                if (r == 0) break;
                sb.Append((char)r);
            }
            return sb.ToString();
        }

        /// <summary>
        /// PackString (PC 0x56BE80, WideStringToPackedString 0x57E8A0): 0xFF80.. folds to 0x80..0xFF (+0x100); &lt; 0x80 →
        /// the byte; else the first EntryTable index ≥ 0x80 holding the char: &lt; 0x100 → one byte, else the two-byte form
        /// (the lead byte j ≥ 0x80 whose entry equals index / 128, then index % 128 + 0x80); not found → error (skipped).
        /// </summary>
        public byte[] Pack(string s, int size, out bool error)
        {
            error = false;
            var o = new System.Collections.Generic.List<byte>();
            int k = 0;
            while (o.Count < size)
            {
                int c = k < s.Length ? s[k] : 0;
                k++;
                if (c >= 0xFF80) c = (c + 0x100) & 0xFFFF;
                if (c < 0x80) o.Add((byte)c);
                else
                {
                    int idx = 0x80;
                    while (idx < NumEntries && _entries[idx] != c) idx++;
                    if (idx == NumEntries) error = true;
                    else if (idx < 0x100) o.Add((byte)idx);
                    else
                    {
                        int j = 0x80;
                        for (; j < 0x100; j++)
                            if (Entry(j) == idx / 0x80)
                            {
                                o.Add((byte)j);
                                if (o.Count < size) o.Add((byte)(idx % 0x80 + 0x80));
                                break;
                            }
                        if (j == 0x100) error = true;
                    }
                }
                if (c == 0) break;
            }
            return o.ToArray();
        }
    }

    /// <summary>
    /// Localization/Localize.cpp (PC 0x56B7B0-0x56BD39, 0x57E680-0x57E9C8): the language table, the current language,
    /// the localised percent sign and the bPrintf locale (decimal / group char) the language sets.
    /// </summary>
    public static class Localize
    {
        public struct LanguageInfo
        {
            public Language Language;
            public string Name, Filename;
            /// <summary>bPrintfLocaleInfo {decimal_char, group_char, group_len} (bPrintf.hpp:10).</summary>
            public char Decimal, Group;
            public int GroupLen;
            public LanguageInfo(Language l, string n, string f, char dec, char grp, int len) { Language = l; Name = n; Filename = f; Decimal = dec; Group = grp; GroupLen = len; }
        }

        /// <summary>LanguageInfoTable (PC 0x8F40F8, 10 × {language, name, filename, font names 0x8F4060, locale}): the
        /// locale bytes are read from the exe (Spanish has group_len 0 — no thousands separators).</summary>
        public static readonly LanguageInfo[] LanguageInfoTable =
        {
            new LanguageInfo(Language.English, "ENGLISH", "LANGUAGES\\ENGLISH.BIN", '.', ',', 3),
            new LanguageInfo(Language.French, "FRENCH", "LANGUAGES\\FRENCH.BIN", ',', ' ', 3),
            new LanguageInfo(Language.German, "GERMAN", "LANGUAGES\\GERMAN.BIN", ',', '.', 3),
            new LanguageInfo(Language.Italian, "ITALIAN", "LANGUAGES\\ITALIAN.BIN", ',', '.', 3),
            new LanguageInfo(Language.Spanish, "SPANISH", "LANGUAGES\\SPANISH.BIN", ',', ' ', 0),
            new LanguageInfo(Language.Dutch, "DUTCH", "LANGUAGES\\DUTCH.BIN", ',', '.', 3),
            new LanguageInfo(Language.Swedish, "SWEDISH", "LANGUAGES\\SWEDISH.BIN", ',', ' ', 3),
            new LanguageInfo(Language.Polish, "POLISH", "LANGUAGES\\POLISH.BIN", ',', ' ', 3),
            new LanguageInfo(Language.Finnish, "FINNISH", "LANGUAGES\\FINNISH.BIN", ',', ' ', 3),
            new LanguageInfo(Language.Danish, "DANISH", "LANGUAGES\\DANISH.BIN", ',', '.', 3),
        };

        /// <summary>GetLanguageInfo (0x56B7B0): the table row, else null.</summary>
        public static LanguageInfo? GetLanguageInfo(Language l)
        {
            foreach (var i in LanguageInfoTable) if (i.Language == l) return i;
            return null;
        }

        /// <summary>GetLanguageName (0x56B7E0): the row's name, else "UNKNOWN".</summary>
        public static string GetLanguageName(Language l) => GetLanguageInfo(l)?.Name ?? "UNKNOWN";

        static Language _current = Language.None;

        /// <summary>
        /// GetCurrentLanguage (0x56BB20). PC start-up (regeditthang 0x6CB680) reads HKLM\Software\Electronic Arts\EA
        /// Games\Need for Speed Most Wanted\Language and maps it through LanguageConversionTable (0x8FB578: "English US" /
        /// "English UK" → English, French, German, Italian, Spanish, Swedish, Danish, Dutch, Korean, Chinese, Japanese,
        /// Thai, Polish, Finnish); PC_GetOSLanguage (0x6CBB00) returns English when nothing matched. Only languages with a
        /// LanguageInfoTable row can load (SetCurrentLanguage finds no row for Korean / Chinese / Japanese / Thai) [?: the
        /// port then stays on English].
        /// </summary>
        public static Language CurrentLanguage
        {
            get
            {
                if (_current == Language.None) _current = ReadRegistryLanguage();
                return _current;
            }
        }

        /// <summary>SetCurrentLanguage (0x57E6F0) without the resource reload (the FE data is loaded once in the port).</summary>
        public static void SetCurrentLanguage(Language l) { if (GetLanguageInfo(l) != null) _current = l; }

        /// <summary>LanguageConversionTable (0x8FB578): registry name, eLanguages, region (CurrentLanguage 0x982D24, read by
        /// BuildRegion::__getLanguage 0x6CBB40).</summary>
        static readonly (string reg, Language lang, int region)[] LanguageConversionTable =
        {
            ("English US", Language.English, 0), ("English UK", Language.English, 5), ("French", Language.French, 6),
            ("German", Language.German, 7), ("Italian", Language.Italian, 8), ("Spanish", Language.Spanish, 9),
            ("Swedish", Language.Swedish, 1), ("Danish", Language.Danish, 1), ("Dutch", Language.Dutch, 1), ("Korean", Language.Korean, 2),
            ("Chinese (Traditional)", Language.Chinese, 4), ("Chinese (Simplified)", Language.Chinese, 11),
            ("Japanese", Language.Japanese, 3), ("Thai", Language.Thai, 12), ("Polish", Language.Polish, 1), ("Finnish", Language.Finnish, 1),
        };

        static int _region = -1;

        /// <summary>BuildRegion region code (0 when no registry language matched: PC_GetOSLanguage's English fallback).</summary>
        public static int Region { get { if (_current == Language.None) _current = ReadRegistryLanguage(); return Math.Max(0, _region); } }

        /// <summary>BuildRegion::IsAmerica (0x64A010): region 0.</summary>
        public static bool IsAmerica => Region == 0;

        /// <summary>BuildRegion::IsPal / IsEurope (0x64A110 / 0x64A020): region 1, 5, 6, 7, 8 or 9.</summary>
        public static bool IsPal { get { int r = Region; return r == 1 || r == 5 || r == 6 || r == 7 || r == 8 || r == 9; } }

        static Language ReadRegistryLanguage()
        {
            string value = null;
            // [ours] Microsoft.Win32.Registry is not in the .NET Standard 2.1 profile: reached through reflection (Mono's
            // mscorlib has it on Windows); anything missing leaves English
            try
            {
                var reg = Type.GetType("Microsoft.Win32.Registry, mscorlib") ?? Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry");
                var lm = reg?.GetField("LocalMachine")?.GetValue(null);
                var key = lm?.GetType().GetMethod("OpenSubKey", new[] { typeof(string) })?.Invoke(lm, new object[] { @"Software\Electronic Arts\EA Games\Need for Speed Most Wanted" });
                value = key?.GetType().GetMethod("GetValue", new[] { typeof(string) })?.Invoke(key, new object[] { "Language" }) as string;
                (key as IDisposable)?.Dispose();
            }
            catch (Exception) { value = null; }
            if (value != null)
                foreach (var (reg, lang, region) in LanguageConversionTable)
                    if (string.Equals(reg, value, StringComparison.OrdinalIgnoreCase))
                    {
                        _region = region;
                        return GetLanguageInfo(lang) != null ? lang : Language.English;
                    }
            _region = 0;
            return Language.English;
        }

        /// <summary>GetLocalizedPercentSign (0x57E680): " %" for Danish, Finnish, French, German and Swedish, else "%".</summary>
        public static string GetLocalizedPercentSign()
        {
            var l = CurrentLanguage;
            return l == Language.Danish || l == Language.Finnish || l == Language.French || l == Language.German || l == Language.Swedish ? " %" : "%";
        }

        /// <summary>LanguageHasChanged (0x57E6B0): GameplaySettings.SpeedoUnits = 0 for English, 1 otherwise.</summary>
        public static int DefaultSpeedoUnits => CurrentLanguage == Language.English ? 0 : 1;

        static LanguageInfo Locale => GetLanguageInfo(CurrentLanguage) ?? LanguageInfoTable[0];

        /// <summary>bPrintf "%$d" (bPrintf.cpp:385): digits grouped by the locale's group_len with its group char.</summary>
        public static string Group(long v)
        {
            var loc = Locale;
            bool neg = v < 0;
            ulong n = neg ? (ulong)(-v) : (ulong)v;
            string digits = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (loc.GroupLen <= 0) return (neg ? "-" : "") + digits;
            var sb = new StringBuilder();
            int c = 0;
            for (int i = digits.Length - 1; i >= 0; i--)
            {
                if (c > 0 && c % loc.GroupLen == 0) sb.Insert(0, loc.Group);
                sb.Insert(0, digits[i]);
                c++;
            }
            if (neg) sb.Insert(0, '-');
            return sb.ToString();
        }

        /// <summary>bPrintf "%$0.Nf" / "%0.Nf" (bPrintf.cpp:528): with '$' the integer part is grouped and the locale's
        /// decimal char is used; without it '.' (invariant).</summary>
        public static string Fixed(double v, int precision, bool group)
        {
            string s = Math.Round(v, Math.Max(0, precision), MidpointRounding.AwayFromZero)
                .ToString("F" + Math.Max(0, precision), System.Globalization.CultureInfo.InvariantCulture);
            if (!group) return s;
            int dot = s.IndexOf('.');
            string ip = dot < 0 ? s : s.Substring(0, dot), fp = dot < 0 ? "" : s.Substring(dot + 1);
            bool neg = ip.StartsWith("-");
            long iv = long.Parse(neg ? ip.Substring(1) : ip, System.Globalization.CultureInfo.InvariantCulture);
            string g = (neg ? "-" : "") + Group(iv);
            return dot < 0 ? g : g + Locale.Decimal + fp;
        }

        /// <summary>WideToCharString (0x56BB30): characters 0..0xFF kept, the rest dropped, up to destlen − 1.</summary>
        public static string WideToCharString(string src, int destlen)
        {
            if (src == null) return "";
            var sb = new StringBuilder();
            foreach (char ch in src)
            {
                if (sb.Length >= destlen - 1) break;
                if (ch <= 0xFF) sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>GetLocalizedString's fallback label (0x56BC10): "FENG: Default string error." in English.bin.</summary>
        public const uint DefaultStringError = 0x9BB9CCC3;
    }
}

namespace NfsMw.Core
{
    /// <summary>bPrintf "%$d" as an extension (Localize.Group): the language's group char instead of the OS culture's.</summary>
    public static class LocalizeExt
    {
        public static string Grouped(this int v) => Localize.Group(v);
        public static string Grouped(this long v) => Localize.Group(v);
        public static string Grouped(this uint v) => Localize.Group(v);
        public static string Grouped(this float v) => Localize.Group((long)Math.Round(v, MidpointRounding.AwayFromZero));
        public static string Grouped(this double v) => Localize.Group((long)Math.Round(v, MidpointRounding.AwayFromZero));
    }
}
