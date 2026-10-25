using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// Which NISAudio event (SOUND\STREAMS\NISAudio.evt, evaluated by <see cref="SpeechScript"/>) a NIS scene plays:
    /// SFXObj_NISStream::QueueNISStream (CARSFX/SFXObj_NISStream.cpp) over GenerateNISAnimHashMap's 68 entries.
    /// <list type="bullet">
    /// <item>Entries 0..17 (race intros, flythrough, D-Day, toll, safehouse, OPM): NIS_Select_Start(scene, track,
    /// section), track only for GenericStart (entry 0: camera track n → Track_n, 0 → Track00), else Track00.</item>
    /// <item>18..35 (Blacklist intros; BL06End/BL07End carry the BL06/BL07 bits): NIS_Select_Blacklist(track, section,
    /// blacklist). BL01's bit has no take in NISAudio (bank 0x4025 attribute mask 0x3FFFE): no stream.</item>
    /// <item>36..67 (arrests, endings): NIS_Select_End(scene, Track00, Complete), the busted stream type.</item>
    /// </list>
    /// Section = Complete, or End when the player skips (button-through). Bit values are the NISAudio.hpp enums.
    /// GetCsisEventIndex (PC @0x4E0760) compares bStringHash(name) (the decomp reads the name-pointer field); the PC table
    /// (@0x4DFE30) has the same 68 entries and values.
    /// </summary>
    public static class NisStreamSelect
    {
        public const int SectionComplete = 1, SectionStart = 2, SectionEnd = 4;
        public const int Track00 = 8192;

        static readonly (int value, string name)[] Map =
        {
            (262144, "GenericStart"), (1, "IntroNis01"), (2, "IntroNis02"), (4, "IntroNis03"), (8, "IntroNis04"), (16, "IntroNis05"),
            (32, "IntroNis06"), (64, "IntroNis07"), (128, "IntroNis08"), (256, "IntroNis09"), (512, "IntroNis10"), (4096, "Flythrough"),
            (16384, "IntroNisDD"), (524288, "IntroNisDDEnd"), (131072, "IntroNisToll01"), (8192, "GarageEnter"), (8192, "GarageExit"),
            (32768, "OPMRivalIntro"),
            (1, "IntroNisBL01"), (2, "IntroNisBL02"), (4, "IntroNisBL03"), (8, "IntroNisBL04"), (16, "IntroNisBL05"), (32, "IntroNisBL06"),
            (32, "IntroNisBL06End"), (64, "IntroNisBL07"), (64, "IntroNisBL07End"), (128, "IntroNisBL08"), (256, "IntroNisBL09"),
            (512, "IntroNisBL10"), (1024, "IntroNisBL11"), (2048, "IntroNisBL12"), (4096, "IntroNisBL13"), (8192, "IntroNisBL14"),
            (16384, "IntroNisBL15"), (32768, "IntroNisBL16"),
            (128, "ArrestF02"), (256, "ArrestF06"), (512, "ArrestF07"), (1024, "ArrestF14"), (16384, "ArrestF18"), (2048, "ArrestF23"),
            (1, "ArrestM01"), (2, "ArrestM04"), (4, "ArrestM06"), (32768, "ArrestM07"), (8, "ArrestM14"), (16, "ArrestM16"), (32, "ArrestM19"),
            (64, "ArrestM23"),
            (128, "ArrestF02b"), (256, "ArrestF06b"), (512, "ArrestF07b"), (1024, "ArrestF14b"), (16384, "ArrestF18b"), (2048, "ArrestF23b"),
            (1, "ArrestM01b"), (2, "ArrestM04b"), (4, "ArrestM06b"), (32768, "ArrestM07b"), (8, "ArrestM14b"), (16, "ArrestM16b"),
            (32, "ArrestM19b"), (64, "ArrestM23b"),
            (131072, "EndingNis01"), (262144, "EndingNis02"), (524288, "EndingNis03"), (1048576, "EndingNis04"),
        };

        /// <summary>
        /// The NISAudio take for an event: SPCH_Choose (<see cref="SpeechScript.Choose"/>, committed: the take enters its
        /// bank's repeat ring as when the stream plays). The NIS_Select_* events carry the presence-mask flag (8); their
        /// presence spec sits right after the parameter tests, ((nTests + 1) x 3) rounded DOWN to 4 (0x832FD4), which the
        /// evaluator used to round up, so every sentence failed the mask test (fixed 2026-10-24).
        /// </summary>
        public static SpeechSample? Pick(SpeechScript s, string ev, Dictionary<string, int> args, int seed)
        {
            if (s == null || ev == null) return null;
            var r = s.Choose(ev, args, new System.Random(seed));
            return r.Count > 0 ? r[0] : (SpeechSample?)null;
        }

        /// <summary>Debug (harness): the one-slot evaluation the port used before the presence-mask fix: takes of the slot's
        /// bank whose attributes are allowed by the slot entries and set in the parameters, one drawn by the seed.</summary>
        public static SpeechSample? Direct(SpeechScript s, string ev, Dictionary<string, int> args, int seed)
        {
            var e = s?.GetEvent(ev);
            if (e == null || e.Sentences.Length == 0) return null;
            var p = new int[e.NumParams + 1];
            for (int k = 0; k < e.ParamNames.Length && k + 1 < p.Length; k++)
                if (args.TryGetValue(e.ParamNames[k], out int v)) p[k + 1] = v;
            foreach (var se in e.Sentences)
            {
                if (se.Slots.Length == 0) continue;
                var sl = se.Slots[0];
                SpeechScript.Bank bank = null;
                foreach (var b in s.Banks) if (b.Id == sl.BankId) { bank = b; break; }
                if (bank == null) continue;
                var cands = new List<int>();
                for (int t = 0; t < bank.TakeCount; t++)
                {
                    bool ok = true;
                    for (int a = 0; a < bank.NumAttrs && a < sl.Entries.Length && ok; a++)
                    {
                        uint bit = 1u << bank.Attrs[t][a];
                        var en = sl.Entries[a];
                        if ((en.Mask & bit) == 0) ok = false;
                        else if (en.Kind != 0 && en.Kind < p.Length && (p[en.Kind] & bit) == 0) ok = false;
                    }
                    if (ok) cands.Add(t);
                }
                if (cands.Count == 0) continue;
                int take = cands[(int)((uint)seed % (uint)cands.Count)];
                return new SpeechSample
                {
                    Line = bank.Line, Take = take, BigOffset = bank.BigOffset + bank.TakeOffset(take, -1), NumBytes = bank.TakeBytes(take),
                    BankId = bank.Id, Speaker = bank.Sub,
                };
            }
            return null;
        }

        /// <summary>GetCsisEventIndex by name (case-insensitive), -1 if the scene has no stream.</summary>
        public static int Index(string animName)
        {
            if (string.IsNullOrEmpty(animName)) return -1;
            for (int i = 0; i < Map.Length; i++) if (string.Equals(Map[i].name, animName, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>true for the busted/ending scenes (STRM_NIS_BUSTED, NIS mixer output 2), false for starts (output 1).</summary>
        public static bool IsEnd(string animName) => Index(animName) >= 36;

        /// <summary>The event and its Csis arguments for a scene (event null if the scene has none).</summary>
        public static (string ev, Dictionary<string, int> args) Event(string animName, int cameraTrack, bool buttonThrough)
        {
            var args = new Dictionary<string, int>();
            int i = Index(animName);
            if (i < 0) return (null, args);
            int track = Track00;
            if (i == 0 && cameraTrack >= 1 && cameraTrack <= 13) track = 1 << (cameraTrack - 1);
            if (i < 18)
            {
                args["NIS_Scene_Start"] = Map[i].value;
                args["NIS_Track"] = track;
                args["NIS_Section"] = buttonThrough ? SectionEnd : SectionComplete;
                return ("NIS_Select_Start", args);
            }
            if (i < 36)
            {
                // the BL06End/BL07End → button-through BL06/BL07 remap sits inside the CSISindex < 18 branch, dead code on
                // PC too (@0x4E7E94): they play Black06/Black07 like the scenes they end
                args["NIS_Track"] = track;
                args["NIS_Section"] = buttonThrough ? SectionEnd : SectionComplete;
                args["NIS_Blacklist"] = Map[i].value;
                return ("NIS_Select_Blacklist", args);
            }
            args["NIS_Scene_End"] = Map[i].value;
            args["NIS_Track"] = Track00;
            args["NIS_Section"] = SectionComplete;
            return ("NIS_Select_End", args);
        }
    }
}
