using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// fecust: what the customize screens (Runtime/UI/Safehouse/CustomizeExe.cs, VisualScreens.cs, CustomizeShopScreens.cs,
// ShopScreens.cs, CarSelectScreens.cs) rely on, checked against the game files:
//   fecust            objects per package, language hashes, icon / status textures
//   fecust objtypes   FEng object type histogram over every package (are list objects, types 4 / 6, ever used?)
static class ProbeFeCustomize
{
    static uint H(string s) => EaHash.HashUpper(s);

    public static void Run(string root, string[] args)
    {
        string mode = args.Length > 2 ? args[2] : "check";
        var pk = new Dictionary<string, FEPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { "FRONTEND/FrontB.lzc", "GLOBAL/InGameB.lzc", "GLOBAL/GlobalB.lzc" })
        {
            var path = Path.Combine(root, f);
            if (!File.Exists(path)) continue;
            foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(path))) pk[kv.Key] = kv.Value;
        }
        if (mode == "objtypes")
        {
            var hist = new Dictionary<int, int>();
            var where = new Dictionary<int, HashSet<string>>();
            foreach (var kv in pk)
                foreach (var o in kv.Value.Objects)
                {
                    hist.TryGetValue((int)o.Type, out int n); hist[(int)o.Type] = n + 1;
                    if (!where.TryGetValue((int)o.Type, out var set)) where[(int)o.Type] = set = new HashSet<string>();
                    if (set.Count < 6) set.Add(kv.Key);
                }
            foreach (var kv in hist.OrderBy(k => k.Key)) Console.WriteLine($"type {kv.Key,2} ({(FEObjectType)kv.Key}): {kv.Value,6} objects  e.g. {string.Join(", ", where[kv.Key])}");
            return;
        }
        if (mode == "responses")
        {
            // FEResponse id histogram over every object / package response list: which FEngine::ProcessResponses cases
            // (FEMessageResponse.h MR_*) the shipped packages can reach at all
            var hist = new Dictionary<uint, int>();
            var where = new Dictionary<uint, HashSet<string>>();
            void Count(string pkg, IEnumerable<FEMessageResponse> lists)
            {
                foreach (var l in lists)
                    foreach (var r in l.List)
                    {
                        hist.TryGetValue(r.Id, out int n); hist[r.Id] = n + 1;
                        if (!where.TryGetValue(r.Id, out var set)) where[r.Id] = set = new HashSet<string>();
                        if (set.Count < 5) set.Add(pkg);
                    }
            }
            foreach (var kv in pk)
            {
                Count(kv.Key, kv.Value.PackageResponses);
                foreach (var o in kv.Value.Objects) Count(kv.Key, o.MessageResponses);
            }
            foreach (var kv in hist.OrderBy(k => k.Key)) Console.WriteLine($"MR {kv.Key,4}: {kv.Value,7}  e.g. {string.Join(", ", where[kv.Key])}");
            // the pad messages FEngine::ProcessPadsForPackage can send besides the plain presses: does any response list
            // (object or package) answer them?
            var padMsgs = new (uint h, string name)[]
            {
                (0xC12E9E27u, "PAD_ACCEPT_RELEASED"), (0x936A6A7Fu, "BUTTON_RELEASED (accept on a button)"), (0xC2F8FCC8u, "PAD_BACK_RELEASED"),
                (0x447315AFu, "PAD_LTRIGGER_HELD"), (0x20AD4EB5u, "PAD_RTRIGGER_HELD"), (0x334C5493u, "control transferred (0x334C5493)"),
            };
            foreach (var (h, name) in padMsgs)
            {
                var pkgs = new List<string>();
                foreach (var kv in pk)
                {
                    bool hit = kv.Value.PackageResponses.Any(r => r.Msg == h) || kv.Value.Objects.Any(o => o.MessageResponses.Any(r => r.Msg == h));
                    if (hit) pkgs.Add(kv.Key);
                }
                Console.WriteLine($"msg {h:X8} {name}: {pkgs.Count} packages{(pkgs.Count > 0 ? "  e.g. " + string.Join(", ", pkgs.Take(6)) : "")}");
            }
            return;
        }

        var objects = new (string pkg, object[] ids)[]
        {
            ("CustomizeMain", new object[] { 0xB71B576Du, 0xCFFB7033u, 0xF2492598u, 0xDC6EE739u, 0x5AEC8D91u, 0x8D1559A4u, 0x7A6D2F71u, 0xC60ADCFDu, 0x5E7B09C9u, "option_1", "option_3", "OPTION_MASTER", "ICON_SCROLL_REGION", "HEAT_BASE_LED_1", "HEAT_BASE_LED_10", 0x5FFEE1D8u, 0xE637955Cu }),
            ("CustomizeCategory", new object[] { 0xB71B576Du, 0xCFFB7033u, 0xD0582FEBu, 0xF2492598u, 0x5AEC8D91u, 0x8D1559A4u, 0x7A6D2F71u, 0xC60ADCFDu, 0x5E7B09C9u, "option_1", "option_12", "ICON_SCROLL_REGION" }),
            ("CustomizeCategory_BACKROOM", new object[] { 0xB71B576Du, 0xCFFB7033u, 0x63CA8308u, 0x83E3CD39u, 0x23D918FEu, 0xF2492598u, "option_1" }),
            ("CustomizeGenericTop", new object[] { 0xB71B576Du, 0xCFFB7033u, 0xD0582FEBu, 0x5E7B09C9u, 0xF2492598u, "option_1", "option_12", 0x53639A10u }),
            ("CustomizeParts", new object[] { 0xB71B576Du, 0xCFFB7033u, 0xD0582FEBu, 0xEBC3E6B7u, 0xA6298E25u, 0x8D1559A4u, 0xDBB80EDDu, 0xC60ADCFDu, 0x7A6D2F71u, 0xA91EDA8Au, 0xB6B79052u, 0x6F25A248u, 0xB2037BDCu, 0x5E7B09C9u, 0xB94139F4u, 0xF2492598u, "option_1", "option_40", "ICON_SCROLL_REGION" }),
            ("CustomizeParts_BACKROOM", new object[] { 0xB71B576Du, 0x63CA8308u, 0x83E3CD39u, 0x5E7B09C9u, "option_1" }),
            ("Spoilers", new object[] { 0x78008599u, 0x5E7B09C9u, 0x2C3CC2D3u, 0x53639A10u, 0x2C526172u, "option_1" }),
            ("Rims", new object[] { 0xE6782841u, 0x5E7B09C9u, 0x2C526172u, 0x53639A10u, "option_1" }),
            ("Decals", new object[] { 0x889BACB6u, 0x5E7B09C9u, 0x53639A10u, 0x2C526172u, "option_1" }),
            ("Paint", new object[] { 0x78008599u, 0x2C526172u, 0x8D1559A4u, 0xF2492598u, 0xEBC3E6B7u, 0x6F25A248u, 0xB2037BDCu, "COLOR_1", "COLOR_40" }),
            ("CustomHUD", new object[] { 0xDEE8632Bu, 0x05D19F25u, 0xC0721EB9u, 0xD312F0CBu, 0x8FE2A217u, 0xC62AD685u, 0xB8F1F802u, 0xC5D551B7u, 0x5E7B09C9u, "option_1" }),
            ("CustomHUDColor", new object[] { 0xB893252Au, 0xDEE8632Bu, 0x05D19F25u, 0xC0721EB9u, "COLOR_1", "COLOR_10", "option_1", "option_3" }),
            ("Numbers", new object[] { 0x2A08BA92u, 0x1A88DC05u, 0x7A8355D9u, 0xD0582FEBu, 0xCFFB7033u, 0xB71B576Du }),
            ("CustomizePerformance", new object[] { "DETAIL_TEXT_LINE1", "DETAIL_TEXT_LINE3", "PERFORMANCE_DETAILS_ICON1", "BRAND_ICON_1", "BRAND_ICON_3", "BASE_ACCELERATION", "FILLBAR_ACCELERATION", "HANDLE_ACCELERATION", "BASE_HANDLING", "BASE_TOPSPEED", 0x5E7B09C9u, "option_1" }),
            ("ShoppingCart", new object[] { "PART_NAME_1", "PRICE_1", "TRADE_IN_1", "CHECK_ICON_1", "OPTION_BACKING_1", 0xA753C46Cu, 0xC128B184u, 0x842B0E89u, 0xD57C95E1u, 0x9EA22E0Bu, 0xD1497A06u, 0x34F7C0E8u, 0x18661565u, 0x8531E22Eu, 0xF2492598u }),
            ("ShoppingCart_BACKROOM", new object[] { "PART_NAME_1", "MARKER_GROUP_1", "MARKER_NUM_1", "MARKER_BLOOM_1", 0xEB957471u, 0x47DF0E21u, 0x8CDCB8EDu, 0xD3D3B1F4u }),
            ("Car_Select", new object[] { "BASE_ACCELERATION", "BASE_TOPSPEED", "BASE_HANDLING", "IMPOUND_STATE_1", "IMPOUND_STATE_5", 0x75721326u, 0x0BC7B91Fu, 0x64F3A49Cu, 0xCE18427Du, 0x5B8F2A45u, 0x5BC78037u, 0x48095518u, 0xF9A5CE86u, 0xEBF0016Eu, 0x39DC21F9u, 0xE998FE99u, 0xCC59B910u, 0xB8F9938Au, 0x322B18F9u, 0x7044A5A4u, 0x72E7EA88u, 0x9D974DF3u, "HEAT_BASE_LED_1" }),
        };
        int missing = 0;
        foreach (var (pkg, ids) in objects)
        {
            if (!pk.TryGetValue(pkg, out var p)) { Console.WriteLine($"{pkg}: PACKAGE MISSING"); missing++; continue; }
            var have = new HashSet<uint>(p.Objects.Select(o => o.Hash));
            var miss = ids.Where(i => !have.Contains(i is string s ? H(s) : (uint)i)).Select(i => i is string s ? s : ((uint)i).ToString("X8")).ToList();
            missing += miss.Count;
            Console.WriteLine($"{pkg,-26} {ids.Length - miss.Count}/{ids.Length}" + (miss.Count > 0 ? "  missing: " + string.Join(", ", miss) : ""));
        }

        var st = StringTable.Read(GameFiles.ReadBundle(Path.Combine(root, "LANGUAGES", "English.bin")));
        uint[] lang =
        {
            0x055dce1a, 0xbaef8282, 0xbfa7d7c4, 0x5d285ae7, 0xbfd5b50f, 0x10c3fe31, 0xe032d89e, 0xda129752, 0x9de6e6e1, 0x74d1887d,
            0x6134c218, 0x94e73021, 0xf868eb0b, 0x04d4a88d, 0x61e8f83c, 0x9853d9a6, 0x29aa74ba, 0x6e101aa7, 0x4ce19aa4, 0x05aa9137, 0x91997ee8, 0x05b1255c, 0xbb6812bb,
            0x055da70c, 0xbfa52c55, 0xe126ff53, 0xd32729a6, 0x955980bc, 0x6857e5ac, 0x78980a6b,
            0x56b51a0e, 0xf93f2d34, 0xf93f3134, 0x460d1369, 0x467a4015, 0x9bb17a11, 0xcca3063f, 0xc1bc1a86, 0x213085f9, 0x212e5429,
            0xd9228fc6, 0x1e8d885f, 0x1c619fd8, 0x9c1b8935, 0x7956f7b0, 0x2d5bff0f, 0x209a9158, 0xcd057d21,
            0x301dedd3, 0x48e6ca49, 0x34367c86, 0xddf80259, 0x8a7697d6, 0xb1f9b0c9, 0x7d212cfa, 0x7d212cff,
            0xd3a2fbe1, 0x3c27a989, 0x5692be6b, 0x7c50498c, 0x52012995, 0x8a4bfbf2, 0x8a4699e1, 0x830100f0, 0xc253ec92, 0x2f3ec04d, 0xd1e77ca1, 0xb7cbfcce, 0xc129562b, 0xd3efbefe, 0x2884658f, 0xd3f65323, 0x63a51aa2, 0xd3a2d4d3, 0xd413e189, 0xd2cbc510,
            0x60a662f5, 0x7177dc17, 0xb3100a3e, 0xb6763cde, 0xb715070a, 0x452b5481, 0x5415b874, 0xbe434a38, 0xedd14807, 0x69c270c4, 0x69c270c9,
            0xe167f7c8, 0xb1b0e8af, 0xd8ee1a80, 0x5198ba16, 0x5198ba17, 0x5198be57, 0x5198be58, 0x5198c299, 0x41f0a3a5, 0x436a98e9,
            0xbdb19a9f, 0x71d9e710, 0x8ebaa44b, 0x04810898, 0x0a984a42, 0x5dabcbc0, 0x028feadd, 0xa03a752f, 0x4ac68298, 0x358db897, 0x68342700, 0x93296e59, 0x78f1c602,
            0x8098a54c, 0x649f4a65, 0xe18ddce1, 0xe18ddce0, 0xe18ddcdf, 0x892cb612, 0x003b3e83, 0x1f242e03, 0x92fcdbf0, 0x9bb9ccc3,
            0xe5c1020c, 0x927db4fd, 0x8c96b853, 0x2f525e4f, 0xe74dedbb, 0x5317eb31, 0x704a6d50, 0x9a0ef8f9,
            0x281dee8a, 0xf9c73cc2, 0x2b65a216, 0x9db4df7d, 0xaefedad9, 0xcad5722e, 0xb715ae8f, 0xed4dd591, 0xe96fa0c5, 0x0cebda20, 0xbcae8539, 0x80e4f27c, 0x164bed94, 0x630931b6,
            0xbd8bac91, 0xbd8bac92, 0xbd8bac93, 0xbd8bac94, 0x4ef2a115, 0x53f13fd1, 0x6521e5c2, 0xa1161aaf,
        };
        var bad = lang.Where(h => st.Get(h) == null).ToList();
        Console.WriteLine($"language: {lang.Length - bad.Count}/{lang.Length} resolve" + (bad.Count > 0 ? "  missing: " + string.Join(", ", bad.Select(h => h.ToString("X8"))) : ""));
        foreach (var (type, level) in new[] { ("ENGINE", 1), ("TIRES", 3), ("TURBO", 2), ("SUPERCHARGER", 1), ("NITROUS", 2), ("CHASSIS", 3), ("BRAKES", 4), ("TRANSMISSION", 4) })
            Console.WriteLine($"PD_{type}_{level}_1..3: {string.Join(" / ", Enumerable.Range(1, 3).Select(l => st.Get($"PD_{type}_{level}_{l}") ?? "-"))}");
        for (int i = 1; i <= 6; i++) Console.Write($"PN_LEVEL_{i}={st.Get("PN_LEVEL_" + i)}  ");
        Console.WriteLine();

        // textures: the icon / status / marker hashes the screens set
        var tex = new HashSet<uint>();
        foreach (var f in new[] { "FRONTEND/FRONTA.BUN", "FRONTEND/FrontB.lzc", "GLOBAL/GLOBALA.BUN", "GLOBAL/GlobalB.lzc", "GLOBAL/HUDS_Custom_00.bin", "GLOBAL/HUDS_Custom_03.bin" })
        {
            var path = Path.Combine(root, f);
            if (!File.Exists(path)) { Console.WriteLine($"no {f}"); continue; }
            var d = GameFiles.ReadBundle(path);
            foreach (var t in TexturePack.ReadAll(d, 0, d.Length)) tex.Add(t.Hash);
        }
        Console.WriteLine($"textures loaded: {tex.Count}");
        var texIds = new (string what, uint hash)[]
        {
            ("status locked", 0xf0574bb2), ("status new", 0xcffb7033), ("installed", 0x696ae039), ("in cart", 0x1a777e25), ("cart unchecked", 0xe719881c), ("scroller bookend", 0x43b6310f),
            ("decal icon", 0x697b4ad4), ("perf icon", 0xb8c8c0d4), ("rims icon", 0x0294d2a3), ("spoiler icon", 0xbb034ea6), ("spoiler carbon", 0x4d1c18ba), ("spoiler br", 0xc51a4f62), ("spoiler br carbon", 0x611d142a),
            ("body kit icon", 0x028c24f6), ("body kit br", 0xaf393dba), ("hood icon", 0x028f7092), ("hood carbon", 0x68495926), ("hood br", 0xf375276e), ("hood br carbon", 0x2478e136),
            ("roof icon", 0x79165861), ("roof carbon", 0xfc618215), ("roof br", 0x25a4375e), ("roof br carbon", 0xcd6b4e26), ("hud icon", 0x028f88bc), ("hud br", 0x8ba602fc), ("tint icon", 0x3f23165c),
            ("main parts", 0x6e0ca66c), ("main perf", 0x3987d054), ("main visual", 0x3e31ba56), ("main parts br", 0x73272ed2), ("main perf br", 0xc61c8d3a), ("main visual br", 0xe69d4f7c),
            ("perf engine", 0xc15c94e6), ("perf trans", 0x01a29ffa), ("perf susp", 0x178475e7), ("perf nos", 0x9701bde4), ("perf tires", 0x06e8e477), ("perf brakes", 0xbaa23a28), ("perf turbo", 0x06ef789c), ("perf sc", 0x93603dfb),
            ("perf engine br", 0x4f424e0f), ("perf trans br", 0xd142d3e3), ("perf susp br", 0x00190eb6), ("perf nos br", 0x6fea04c8), ("perf tires br", 0x7373f1ef), ("perf brakes br", 0x4887f351), ("perf turbo br", 0x12fe30a5), ("perf sc br", 0x630071e4), ("induction marker", 0x8e284227),
            ("paint icon", 0xa3b76154), ("paint br", 0x0db89e17), ("vinyl icon", 0x55778e5a), ("vinyl br", 0xd35f04c0), ("rim paint icon", 0xd223f84a), ("decals icon", 0xda1dae54), ("decals br", 0xa9135927), ("numbers icon", 0x45a1c644),
            ("rim stock", 0x0f3990b6), ("vinyl stock", 0x21f3d114), ("5zigen", 0xb0da3de4), ("adr", 0xf224a729), ("bbs", 0xf224ab29), ("enkei", 0x0e38de9e), ("konig", 0x0ea60b4a), ("lowenhart", 0xafc6b9cb), ("racinghart", 0x27ebd095), ("oz", 0x6c2fa9db), ("volk", 0x36c53e8e), ("roja", 0x36c2d130),
            ("vinyl flame", 0xf8148554), ("vinyl tribal", 0x192d84da), ("vinyl stripe", 0xf7352706), ("vinyl racing flag", 0x1223cc89), ("vinyl national", 0xbc44bbcb), ("vinyl body", 0x694ca0ca), ("vinyl unique", 0x1b3a8dd3), ("vinyl contest", 0x1ba508fc),
            ("decal windshield", 0x52ded91d), ("decal rear", 0xac7937b4), ("decal left door", 0xda88b711), ("decal right door", 0xc9a967c4), ("decal left qp", 0x2c710c4d), ("decal right qp", 0xffa7d360), ("decal slot L1", 0xfe957f48), ("decal slot R1", 0x2e40eadb),
            ("hud needle", 0x70f56628), ("hud letters", 0xbf6682c9), ("hud face", 0xcc9e1ce4), ("HEAT_X1", H("HEAT_X1")), ("HEAT_X5", H("HEAT_X5")), ("TACH_FILL_00", H("TACH_FILL_00")), ("7000_LINES_00", H("7000_LINES_00")), ("TURBO_NEEDLE_03", H("TURBO_NEEDLE_03")),
            ("IMPOUNDED_ENG", H("IMPOUNDED_ENG")), ("GENERIC_LOGO_128", H("GENERIC_LOGO_128")), ("GENERIC_LOGO_256", H("GENERIC_LOGO_256")),
            ("brand stock tires", 0xad6a0504), ("brand stock brakes", 0xa1a5e9e5), ("brand stock trans", 0x98ed935e), ("brand stock engine", 0x7d0ac98f), ("brand stock induction", 0x9e8f71ad), ("castrol brand", 0x0b95d4df),
        };
        var missTex = texIds.Where(t => !tex.Contains(t.hash)).Select(t => $"{t.what} {t.hash:X8}").ToList();
        Console.WriteLine($"textures: {texIds.Length - missTex.Count}/{texIds.Length} found" + (missTex.Count > 0 ? "  missing: " + string.Join(", ", missTex) : ""));
        // the frontend p_<type>_<level> brand arrays
        var g = Path.Combine(root, "GLOBAL");
        var db = AttribDatabase.LoadAll(Path.Combine(g, "attributes.bin"), Path.Combine(g, "FE_ATTRIB.bin"));
        foreach (var car in new[] { "gti", "rx8", "fordgt" })
        {
            var fe = db.Get("frontend", car);
            if (fe == null) { Console.WriteLine($"{car}: no frontend collection"); continue; }
            var parts = new List<string>();
            foreach (var f in new[] { "p_engine_1", "p_engine_4", "p_tires_1", "p_induction_2", "p_nitrous_3", "p_brakes_4", "p_transmission_2", "p_suspension_3" })
                if (fe.TryGet(f, out int at, out int n, out _)) parts.Add(f + "=[" + string.Join(",", Enumerable.Range(0, n).Select(i => Chunks.U32(fe.Source.Data, at + i * 4)).Select(h => tex.Contains(h) ? "ok" : h == 0xFFFFFFFF ? "-" : h.ToString("X8"))) + "]");
                else parts.Add(f + "=?");
            Console.WriteLine($"{car}: {string.Join(" ", parts)}  UnlockedAt={fe.Int("UnlockedAt")} Cost={fe.Int("Cost")}");
        }
        Console.WriteLine(missing == 0 && bad.Count == 0 ? "OK" : $"{missing} objects + {bad.Count} strings missing");
    }
}
