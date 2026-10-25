using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;

// fesafehouse: checks what the safehouse / main front end screens (Runtime/UI/Safehouse) rely on:
//  - every object hash a screen touches exists in its package (FrontB.lzc / THINSCREEN_GLOBAL.BUN),
//  - every language hash resolves in English.bin,
//  - the PC display presets (sub_6C1990) per level and the FE database defaults.
static class FeSafehouseProbe
{
    static uint H(string s) => EaHash.HashUpper(s);

    public static void Run(string root)
    {
        var pk = FEPackage.ReadBundle(GameFiles.ReadBundle(Path.Combine(root, "FRONTEND", "FrontB.lzc")));
        var thin = Path.Combine(root, "GLOBAL", "THINSCREEN_GLOBAL.BUN");
        if (File.Exists(thin)) foreach (var kv in FEPackage.ReadBundle(GameFiles.ReadBundle(thin))) pk[kv.Key] = kv.Value;
        var st = StringTable.Read(GameFiles.ReadBundle(Path.Combine(root, "LANGUAGES", "English.bin")));

        var objects = new (string pkg, object[] ids)[]
        {
            ("Options", new object[] { 0xA753C46Cu, 0xC128B184u, 0x06745352u, 0x42ADB44Cu, 0x08007B4Cu, 0x8A41F5B9u, 0x53BF826Du, "OPTION_NAME_1", "OPTION_DATA_1", "LEFT_ARROW_1", "RIGHT_ARROW_1", "BASE_SLIDER_1", "FILLBAR_SLIDER_1", "OPTION_NAME_10" }),
            ("OptionsPCDisplay", new object[] { 0xA753C46Cu, 0xC128B184u, 0x06745352u, 0xCE757C8Au, "OPTION_NAME_13", "BASE_SLIDER_13", "FILLBAR_SLIDER_13", "LEFT_ARROW_13" }),
            ("Track_Options", new object[] { 0xA753C46Cu, 0xC128B184u, 0x06745352u, 0x42ADB44Cu, "OPTION_NAME_1", "OPTION_DATA_1" }),
            ("Track_Select", new object[] { 0xB71B576Du, 0x78008599u, 0x4510987Fu, 0x6F25A248u, 0xB2037BDCu, 0xB5154998u, 0x6B67D70Bu, 0xE08434FCu, 0x5E7B09C9u, 0x0DFB7A2Eu, 0xB5154999u, 0xB515499Cu, 0x68215623u, 0x28462C64u, 0x08007B4Cu, "TRACK_MAP" }),
            ("Car_Select", new object[] { 0x3E01AD1Du, 0xB05DD708u, 0xD6D32016u, 0x79D6E45Cu, 0xE3FE27FEu, 0x1930B057u, 0x20C83C31u, 0xAA9834BCu, 0xE3B271B8u, 0xD0F7C7CCu, 0x7379349Bu, 0x2D25B2C4u, 0x36C1E04Du, 0x6F25A248u, 0xB2037BDCu, 0xDC18C4D4u, 0xB94139F4u, 0x39DC21F9u, 0x72E7EA88u, 0x322B18F9u }),
            ("MyCarsManager", new object[] { "CAR_ICON_1", "CAR_ICON_10", 0xB271B295u, 0xB05DD708u, 0xBFA25765u, 0x6F25A248u, 0xB2037BDCu }),
            ("MainMenu", new object[] { "NAME_GROUP", "GAME STATS GROUP", "GAME_COMPLETED_DATA", "CAREER_COMPLETED_DATA", "RAP_SHEET_ITEMS_DATA_2", "CHALLENGE_SERIES_DATA_1", 0xB514E2D8u, "option_1", "OPTION_MASTER" }),
            ("MainMenu_Sub", new object[] { "PLAYER_MASTER", "CAREER_DATA", "BOUNTY_DATA", "CASH_DATA", 0x03C458C1u, 0xB5C74226u, 0xC87422F7u, 0xEB406FECu }),
            ("CustomizeMain", new object[] { 0xDC6EE739u, "option_1" }),
            ("Quick_Race_Brief", new object[] { 0x3E01AD1Du, 0xB05DD708u, 0xB5154998u, 0x2521E5EBu, 0xB5154999u, 0xB515499Au, 0xB515499Bu, 0xB515499Cu, 0xB515499Du, 0xB515499Eu, 0xFE8FDBF7u }),
            ("Dialog", new object[] { 0xB8A7C6CCu, 0xB8A7C6CDu, 0xB8A7C6CEu, 0xF9363F30u, 0xFB8B67D1u, 0xFDE09072u, 0x1E2640FAu, 0x42ADB44Cu, 0x07F9DCA9u, 0x0D4F4069u, 0xFAC88427u }),
            ("PressStart", new object[] { 0xB244CF71u }),
            ("MW_LS_Splash", new object[] { 0x534CC377u, 0xC4DF3FF2u }),
            ("Loading", new object[] { 0x06D91704u }),
            ("Loading_Tips", new object[] { 0xC5FBC710u, 0x0D555245u, 0xC9D77CB6u, 0x3248E720u }),
        };
        int missing = 0;
        foreach (var (pkg, ids) in objects)
        {
            if (!pk.TryGetValue(pkg, out var p)) { Console.WriteLine($"{pkg}: PACKAGE MISSING"); missing++; continue; }
            var have = new HashSet<uint>(p.Objects.Select(o => o.Hash));
            var miss = ids.Where(i => !have.Contains(i is string s ? H(s) : (uint)i)).Select(i => i is string s ? s : ((uint)i).ToString("X8")).ToList();
            missing += miss.Count;
            Console.WriteLine($"{pkg,-18} {ids.Length - miss.Count}/{ids.Length}" + (miss.Count > 0 ? "  missing: " + string.Join(", ", miss) : ""));
        }

        uint[] lang =
        {
            0x5815A2B5, 0xCC8CB746, 0x54020A7A, 0x1AFD5BE6, 0xD6DB74DE, 0x74FB90A8, 0xBCB18F38, 0x19A8C0AF, 0xB24AAE58,   // main menu
            0xE76CD783, 0xE8E24508, 0xD0CF6EE1, 0xF760EABE, 0x04DC6DB5, 0xDCFB6B36, 0x0905101F, 0x4ECA678C,               // options main
            0xFD487543, 0x218E4B08, 0x9E5FB82A, 0x418E681D, 0xDF21EAC2, 0xA3DBB390, 0x2881AB87, 0xC50FA35F, 0x55DA8BF8, 0xF6FAFF24,
            0x05A66554, 0xC96D57C9, 0xE7A04885, 0xFD068897, 0x6CEB9CB6, 0xA271A8E9, 0x9032DF3F, 0x1052ED5B, 0x5F560D76, 0xBDE7DACF,
            0xF2E2D84A, 0xD41A8B13, 0x70A7555E, 0x1DE776AA, 0x1459065C, 0x9C88B5C8, 0x07770CE7, 0xE9CB802F, 0x8AEF5AE8,
            0xD1056C88, 0xF26A5CBF, 0x1582ADFF, 0x85A6CE05, 0x01E19173, 0xFBD74FC5, 0xAF70E736, 0xC6269082, 0x9FA5EC9E, 0xF4B00E99, 0xF75595F2,
            0xD31407E7, 0x8CD532A0, 0x317D3005, 0xF6CCDC5F, 0xC3E9AE58, 0x414F19D7, 0x5AE3441F, 0x1EA4CEC2, 0x916039B4, 0xAC148579,
            0x82CD8F92, 0x084BC378, 0xC03F9F19, 0xC93FBFB5, 0x417B2604, 0x70DFE5C2, 0x16FDEF36, 0xF6BBD534, 0xCE32B239, 0xF6BBC9AB,
            0x3932C2E4, 0x48478029, 0x01CCE8C2, 0xC055165F,                                                                // headers
            0x8FFF61F2, 0xE072DB21, 0x17E18F87, 0x6005281E, 0x0018ECFF, 0xE596C4A3, 0x01BD185C, 0x80B9FF9B, 0x00D5F627, 0xAC22F27E, 0x001C8ACE, 0xEB694C0C,
            0xCDF0CC03, 0xB5E8F82F, 0x25BBD4C3, 0x841D518A, 0x1F203817, 0x43C825ED, 0x034FA2C1, 0xB94FD70E, 0x6F547E4C, 0x4930F5FC, 0xEE1EDC76,
            0x3DE80A85, 0x136C5C90, 0xD6D65640, 0xC2D85652, 0x3070453A, 0xA6850651, 0xA5C20E7D, 0x8663FAEF, 0x632DD19B, 0x472AA00A, 0xBD563BE5,
            0x8569A26A, 0x867DCFD9, 0x0512E823, 0xC5B5A177, 0x3384A679, 0x4D156786, 0x61973E01, 0x3747F6D0, 0x6198E2EE, 0x8B8E913A,
            0xEB9DFC09, 0x8CDC3937, 0x73C615A3, 0xA2CCA838, 0x61D1C5A5, 0x48494E83, 0xA88FFEB4, 0xDE6EFF34, 0xA1CD823E, 0xA6ED015D, 0xB80BFC8A,
            0xD9D6B954, 0xEE053562, 0x2414DE28, 0xD8A058F7, 0x58BBED2A, 0x0DA87B01, 0x4EF2A115, 0x40FA955D, 0x41030A1B, 0x74317CBC,
            0x5B9D89D0, 0x889D822E, 0x1A294DAD, 0x8C451EBA, 0xB4A40135, 0x9A772BD6, 0x6F5401D1, 0xA9950B93, 0x7010BBF2, 0x17574B0E, 0x915F4D26,
            0x4F68196E, 0xC2598BD8, 0xC9847935, 0x29AE0714, 0x892CB612, 0x003B3E83, 0x1F242E03, 0x92FCDBF0,
            0x2BD146D3, 0x6FD91524, 0xDBE419D4, 0x60249A74, 0x417B2601, 0x70E01038, 0x417B25E4, 0xCF538E1C,
        };
        var bad = lang.Where(h => st.Get(h) == null).ToList();
        Console.WriteLine($"language: {lang.Length - bad.Count}/{lang.Length} resolve" + (bad.Count > 0 ? "  missing: " + string.Join(", ", bad.Select(h => h.ToString("X8"))) : ""));

        var caps = PcVideoSettings.ModernCaps();
        string[] names = { "CarEnvMap", "CarEnvUpd", "Road", "WorldLod", "CarLod", "OverBr", "Fsaa", "Rain", "TexFilt", "Res", "VSync", "VT", "Shadow" };
        int[] idx = { PcVideoSettings.CarEnvMap, PcVideoSettings.CarEnvUpdate, PcVideoSettings.RoadReflection, PcVideoSettings.WorldLod, PcVideoSettings.CarLod, PcVideoSettings.OverBright, PcVideoSettings.Fsaa, PcVideoSettings.Rain, PcVideoSettings.TextureFiltering, PcVideoSettings.Resolution, PcVideoSettings.VSync, PcVideoSettings.VisualTreatment, PcVideoSettings.ShadowDetail };
        Console.WriteLine("level " + string.Join(" ", names.Select(n => n.PadLeft(9))));
        for (int level = 1; level <= 5; level++)
        {
            var v = new PcVideoSettings();
            v.ApplyPreset(level, caps);
            Console.WriteLine($"  {level}   " + string.Join(" ", idx.Select(i => v[i].ToString().PadLeft(9))));
        }
        var db = new FEDatabaseData();
        Console.WriteLine($"defaults: audio FE {db.Audio.FEMusicVol} IG {db.Audio.IGMusicVol} mode {db.Audio.AudioMode}; speedo {db.Gameplay.SpeedoUnits}; racing map {db.Gameplay.RacingMiniMapMode}; " +
                          $"QR laps P2P {db.QuickRace[0].NumLaps} circuit {db.QuickRace[1].NumLaps} KO {db.QuickRace[3].NumLaps} tollbooth opp {db.QuickRace[4].NumOpponents}");
        Console.WriteLine(missing == 0 && bad.Count == 0 ? "OK" : $"{missing} objects, {bad.Count} strings missing");
    }
}
