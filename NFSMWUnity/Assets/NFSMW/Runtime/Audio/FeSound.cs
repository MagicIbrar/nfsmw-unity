using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>eMenuSoundTriggers (EAXSound/AemsDef.hpp). Below <see cref="CommonMaxNum"/> = EAXCommon (FE_COMMON_MB
    /// PlayCommonSample), above = EAXFrontEnd (FE_MB PlayFrontEndSample, id - 23).</summary>
    public enum FeSoundId
    {
        None = -1, CommonUp = 0, CommonDown = 1, CommonLeft = 2, CommonRight = 3, CommonSelect = 4, CommonBack = 5, CommonStartPause = 6,
        CommonWrong = 7, CommonDlgboxIn = 10, CommonDlgboxOut = 11, CommonScrollStart = 12, CommonCoolDownFlip = 12, HudEngageMailPopup = 13,
        HudEngagePhone = 14, HudEngageDpad = 15, HudEngageMail = 16, MilestoneRewardSymbolOn = 17, MilestoneRewardSymbolMove = 18,
        EnterTrigger = 19, MapLockTarget = 20, MapZoomIn = 21, MapZoomOut = 22, CommonMaxNum = 23, CarselBassloopReserved00 = 24,
        CarselBassloopReserved01 = 25, CarselCamrotate = 26, CarselCamrotateReserved00 = 27, CarselCamrotateReserved01 = 28,
        CustInstPaint = 38, CustPaintColourLeft = 39, CustPaintColourRight = 40, CustPaintTypeLeft = 41, CustPaintTypeRight = 42,
        UgnewKbtype = 46, UgnewEnter = 47, UgnewDelete = 48, CustInstExhaust = 49, CustInstGeneric = 50, CustInstTurbo = 51,
        CustInstNos = 52, CustInstTransmission = 53, CustInstTires = 54, EaMsgrOpen = 74, EaMsgrLogoff = 75, EaMsgrChatReq = 85,
        EaMsgrMailReceive = 86, EaMsgrChallengeReq = 87, MainMenu = 88, MainSub = 89, BustedScreen = 89, Impounded = 90,
        RapsheetLogin = 103, RapsheetLogin2 = 104, RapsheetMain = 105, RapsheetSummary = 106, RapsheetVehicle = 107,
        RapsheetSelect = 108, RapsheetBackup = 109, RapsheetMoveBarUp = 110, RapsheetMoveBarDown = 111, RapsheetCts = 112,
        RapsheetInfrac = 113, RapsheetPd = 114, RapsheetRankings = 115, RapsheetRankingDetail = 116, RapsheetTep = 117,
        RapsheetExit = 118, MainMenuEnter = 123, MainMenuExit = 124, MainSubEnter = 125, MainSubExit = 126, McMainEnter = 127,
        BlacklistEnter = 128, BlacklistExit = 129, BioEnter = 130, BioExit = 131, BioToRivalcarExit = 132, CustMainEnter = 133,
        CustEnter = 134, CustExit = 135, ShowcaseEnter = 136, ShowcaseExit = 137, QuickraceBriefEnter = 138, QuickGambleBlip = 139,
        RacesheetEnter = 140, RacesheetExit = 141, TrackSelectEnter = 142, QuickBriefExit = 143, QuickRaceCarOn = 144,
        CarSelToShowcase = 145, ChalSerEnter = 146, CarSelectEnter = 147, SafehMarkCongratsOff = 148, RandomizeButton = 149,
        SafehMarkEnter = 150, SafehMarkSpinCoin = 151, ChalSerExit = 152, MapReposition = 153, OptionMenuEnter = 154,
        OptionMenuExit = 155, RivalBioOff = 156, BioEnter2 = 157, RivBioCloudOn = 158, RivBioLogoFlyIn = 159, FrontendMaxNum = 160,
    }

    /// <summary>
    /// Front-end and HUD sounds: EAXSound::PlayUISoundFX (PC @0x4AE8F0) → EAXCommon::Play (@0x4B9900: FE_COMMON_MB
    /// PlayCommonSample(id, vol = SFXObj_FEHUD mixer output 1, pitch 0x1000, azimuth 0)) or EAXFrontEnd::Play (@0x4B9470:
    /// FE_MB PlayFrontEndSample(id - 23, vol = output 2); triggers 103..118 except 110/111 use PlayFrontEndSample_RS, one at a
    /// time and stoppable). The sample per id comes from each bank's own patch (<see cref="AemsPatch"/> sample-select table):
    /// common ids 0..22 → FE_COMMON slots {1,0,2,0,3,4,0,5,...} (down and right are silent: the FEng "down" and "right"
    /// messages map to UISND_COMMON_UP / UISND_COMMON_LEFT anyway).
    /// <para><see cref="Notify"/> is MenuScreen::BaseNotifySound (PC @0x5724D0, table read from the exe: it differs from the GC
    /// decomp in places, e.g. 0x08464444 → 0x6F): FEng packages post a "sound" message hash, this maps it to a trigger.
    /// SFXObj_FEHUD is the MAIN state's object 7 (SFXObj_FEHUD.cpp); UISND_ENTER_TRIGGER raises its DMIX input 4 for a frame
    /// (EAXCommon::Update clears it).</para>
    /// </summary>
    public static class FeSound
    {
        const string CommonBank = "GLOBAL/FE_COMMON_MB.abk", FeBank = "FE/FE_MB.abk";

        /// <summary>MenuScreen::BaseNotifySound (PC @0x5724D0): FEng message hash → eMenuSoundTriggers.</summary>
        static readonly Dictionary<uint, int> NotifyTable = new Dictionary<uint, int>
        {
            {0x00B06E92, 140}, {0x01CD9276, 114}, {0x0217C99F, 146}, {0x08464444, 111}, {0x0A888DDC, 107},
            {0x0BDDC0D2, 142}, {0x145B13C0, 88}, {0x14C293B4, 131}, {0x14C85C88, 126}, {0x1ABBE49B, 103},
            {0x1B325F9D, 138}, {0x1CA2354C, 148}, {0x1CFAD52B, 20}, {0x1E9AE519, 42}, {0x299148C8, 87}, {0x2AC2CD24, 47},
            {0x2DD4FE18, 38}, {0x2E21E843, 54}, {0x2E287C68, 51}, {0x2E70DC92, 127}, {0x3159062B, 40}, {0x334F3B79, 27},
            {0x334F3B7A, 28}, {0x3A62513E, 111}, {0x3B7FAC4C, 112}, {0x3B7FF2AB, 117}, {0x40927E58, 116},
            {0x43BE891A, 26}, {0x44D43738, 145}, {0x4688F23F, 115}, {0x46BB84C6, 41}, {0x478FFB47, 89}, {0x480C9A58, 5},
            {0x480DF13F, 0}, {0x4810A91B, 28}, {0x48122792, 2}, {0x48ED7349, 144}, {0x4949060A, 106}, {0x4A7ACC5A, 5},
            {0x4A7C2341, 0}, {0x4A7E0941, 133}, {0x4A805994, 2}, {0x4AC5E165, 2}, {0x4B680587, 155}, {0x4BEAF019, 50},
            {0x4E1DA7E5, 85}, {0x4E7CCC80, 18}, {0x4F072ECA, 157}, {0x4FB99CCD, 26}, {0x4FB99CCE, 41}, {0x4FBFB02B, 74},
            {0x50B3FA79, 135}, {0x52F29A56, 118}, {0x52F69B81, 105}, {0x54EBD897, 123}, {0x56D0CB98, 39},
            {0x58AA827A, 88}, {0x59E562D8, 109}, {0x5A0FDF7C, 24}, {0x5A0FDF7D, 25}, {0x5C928AC2, 14}, {0x610FB233, 7},
            {0x610FB234, 7}, {0x610FB235, 7}, {0x610FB236, 7}, {0x610FB237, 7}, {0x610FB238, 7}, {0x63E5ED6F, 150},
            {0x65A03426, 17}, {0x672DFAFD, 134}, {0x6A95F040, 15}, {0x6A9AA0EA, 16}, {0x6B267E15, 113}, {0x6B283007, 10},
            {0x6BC1CD5D, 10}, {0x6E437A7B, 159}, {0x7238782D, 104}, {0x7358A1F0, 74}, {0x746D7CDB, 152}, {0x7B6B89D7, 4},
            {0x7C002DCA, 153}, {0x7D35DA25, 12}, {0x7F63059B, 136}, {0x802009F9, 48}, {0x80F21152, 156},
            {0x81DA4B22, 108}, {0x84D07AF9, 115}, {0x8C40ED95, 89}, {0x8E2658C6, 112}, {0x8E269F25, 117},
            {0x8F7385AB, 110}, {0x90429475, 46}, {0x905FB857, 12}, {0x91970159, 21}, {0x958E0BD3, 151},
            {0x97B397B0, 114}, {0x997DD91D, 158}, {0x9AFA53A7, 3}, {0x9B59E056, 7}, {0x9F032957, 137}, {0xA07F7EA9, 132},
            {0xA3CC3462, 107}, {0xA4F871DB, 75}, {0xA6362B4B, 139}, {0xAAB576AB, 74}, {0xAB7A5FC7, 105},
            {0xAC57BDB2, 19}, {0xAD0FBB98, 130}, {0xADCE9EEC, 125}, {0xB11FB113, 147}, {0xB1BF9795, 103},
            {0xB205316C, 0}, {0xB25C8563, 86}, {0xB4FF8CD3, 124}, {0xB6471CC6, 90}, {0xB86367CB, 154}, {0xB8AFE646, 53},
            {0xBA0159B3, 129}, {0xC45AF53B, 25}, {0xC477473A, 22}, {0xC4A06D0F, 89}, {0xC989909F, 10}, {0xCAF4D21E, 116},
            {0xD15F7512, 109}, {0xD792FF31, 110}, {0xD93BCF6E, 141}, {0xDC49A694, 6}, {0xDDD9C41E, 49}, {0xDEDF0B4C, 75},
            {0xE28CAC90, 106}, {0xE2A0904F, 113}, {0xE3FB93BE, 11}, {0xE7DAFAEE, 0}, {0xE9B28A67, 104},
            {0xEA2747C5, 149}, {0xED78878C, 52}, {0xEE0EEA7E, 143}, {0xEE54C987, 13}, {0xF4B32D4D, 11},
            {0xF9545D5C, 108}, {0xFA274177, 128}, {0xFABBBF40, 11}, {0xFC543E69, 4},
        };

        /// <summary>FEng sound message hashes (Frontend/FEngHashes/SoundHashes.hpp).</summary>
        public const uint MsgUp = 0xB205316C, MsgDown = 0x480DF13F, MsgLeft = 0x48122792, MsgRight = 0x4AC5E165, MsgAccept = 0x7B6B89D7,
            MsgBack = 0x480C9A58, MsgLTrigger = 0x6B283007, MsgRTrigger = 0xF4B32D4D;

        static GameObject _holder;
        static readonly List<AudioSource> Pool = new List<AudioSource>();
        static AudioSource _rapSheet;
        static int _rapSheetId = -1;
        static int _enterFrame = -1;

        /// <summary>The mixer volume for the common (slot 1), FE (slot 2) or radar (slot 3) bus: SFXObj_FEHUD's output
        /// (MAIN obj 7); full scale when the map has no output for it [ours].</summary>
        static int BusVolume(int slot)
        {
            var m = SndMixer.Instance;
            if (!m || m.Map == null) return 0x7FFF;
            var o = new SndObj(m.Map, MixMap.ObjectId(false, MixMap.Main, 0, 7));
            return o.Mapped ? o.Out(slot, MixMap.Vol) : 0x7FFF;
        }

        static AudioSource Voice()
        {
            if (!_holder)
            {
                _holder = new GameObject("NFSMW FE Sound");
                if (Application.isPlaying) Object.DontDestroyOnLoad(_holder);
                Pool.Clear();
                _rapSheet = null;
            }
            foreach (var s in Pool) if (s && !s.isPlaying) return s;
            if (Pool.Count >= 8) return Pool[0];
            var src = _holder.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.ignoreListenerPause = true;
            Pool.Add(src);
            return src;
        }

        /// <summary>The bank slot a trigger plays (0 = none).</summary>
        public static int SlotFor(int trigger)
        {
            if (trigger < 0 || trigger >= (int)FeSoundId.FrontendMaxNum) return 0;
            bool common = trigger < (int)FeSoundId.CommonMaxNum;
            var sel = SoundLibrary.Patch(common ? CommonBank : FeBank).Select(common ? "PlayCommonSample" : "PlayFrontEndSample");
            int id = common ? trigger : trigger - (int)FeSoundId.CommonMaxNum;
            return sel != null && id < sel.Length ? sel[id] : 0;
        }

        public static void Play(FeSoundId id) => Play((int)id);

        /// <summary>EAXSound::PlayUISoundFX.</summary>
        public static void Play(int trigger)
        {
            if (trigger < 0 || trigger >= (int)FeSoundId.FrontendMaxNum || !Application.isPlaying) return;
            bool common = trigger < (int)FeSoundId.CommonMaxNum;
            if (trigger == (int)FeSoundId.EnterTrigger) _enterFrame = Time.frameCount;
            int slot = SlotFor(trigger);
            if (slot <= 0) return;
            var clip = SoundLibrary.ClipAt(common ? CommonBank : FeBank, slot);
            if (!clip) return;
            float vol = BusVolume(common ? 1 : 2) / 32767f;
            bool rapSheet = !common && trigger >= 103 && trigger <= 118 && trigger != 110 && trigger != 111;
            AudioSource src;
            if (rapSheet)
            {
                // PlayFrontEndSample_RS: the previous rap-sheet sound is deleted first
                if (_rapSheet && _rapSheet.isPlaying) _rapSheet.Stop();
                src = _rapSheet && _holder ? _rapSheet : (_rapSheet = Voice());
                _rapSheetId = trigger;
            }
            else src = Voice();
            src.clip = clip;
            src.volume = Mathf.Clamp01(vol);
            src.pitch = 1f;
            src.Play();
        }

        /// <summary>EAXSound::StopUISoundFX → EAXFrontEnd::Stop: only the playing rap-sheet sound can be stopped.</summary>
        public static void Stop(int trigger)
        {
            if (trigger == _rapSheetId && _rapSheet) { _rapSheet.Stop(); _rapSheetId = -1; }
        }

        /// <summary>MenuScreen::BaseNotifySound: plays the trigger an FEng sound message maps to (false if none).</summary>
        public static bool Notify(uint fengMessage)
        {
            if (!NotifyTable.TryGetValue(fengMessage, out int t)) return false;
            Play(t);
            return true;
        }

        /// <summary>
        /// [ours] The port's menus read keys directly instead of running FEng button scripts; this posts the FEng sound
        /// message each generic menu action would send (up/down/left/right/accept/back) through <see cref="Notify"/>.
        /// </summary>
        public static void Input(bool up, bool down, bool left, bool right, bool accept, bool back)
        {
            if (accept) Notify(MsgAccept);
            else if (back) Notify(MsgBack);
            else if (up) Notify(MsgUp);
            else if (down) Notify(MsgDown);
            else if (left) Notify(MsgLeft);
            else if (right) Notify(MsgRight);
        }

        /// <summary>SFXObj_FEHUD::UpdateMixerOutputs (@0x4DDDE0): the 15 GameFlowSndState flags are copied into MAIN obj 7's
        /// DMIX inputs 0..14 (SFXObj_FEHUD.cpp:21); DMIX input 4 also carries UISND_ENTER_TRIGGER for the frame of an
        /// EAXCommon::Play [?: the copy would overwrite it in the game's order, kept OR-ed as round 3 read it from the exe].</summary>
        internal static void UpdateMixerInputs(SndObj fehud)
        {
            if (fehud == null) return;
            var gf = SoundState.GameFlow;
            for (int i = 0; i < gf.Length && i < 15; i++) fehud.Set(i, gf[i] != 0 ? 0x7FFF : 0);
            if (_enterFrame == Time.frameCount) fehud.Set(4, 0x7FFF);
            fehud.In[15] = 1;
        }
    }

    /// <summary>
    /// MMiscSound ("Snd" channel): EAXCommon::MsgPlayMiscSound (PC @0x4B9810; id 0 = FX_Radar from FX_Misc_MB, vol =
    /// SFXObj_FEHUD output 3: the HUD radar detector's ping, FeRadarDetector.cpp:102) and SFX_Common::MsgPlayMiscSound
    /// (SFX_Common.cpp, MAIN obj 4: DMIX input id = 0x7FFF; 1 FX_UVES(0), 2 FX_Camera (photo finish, PhotoFinish.cpp:309),
    /// 3 FX_UVES(2) pursuit-breaker start, 4 FX_UVES(1) end — sent by SFXObj_MomentStrm::ReceivePursuitBreaker). Volumes
    /// from SFX_Common's outputs 0 camera, 2 UVES, 3 breaker start, 4 breaker end. FX_UVES type → FX_Misc_MB slot by the
    /// bank's select table {2,3,4}; FX_Radar → slot 5; FX_Camera → FX_Camera.abk slot 1.
    /// </summary>
    public static class MiscSound
    {
        const string MiscBank = "IG_GLOBAL/FX_Misc_MB.abk", CameraBank = "IG_GLOBAL/FX_Camera.abk";
        static GameObject _holder;
        static AudioSource _radar, _camera, _uves, _start, _end;
        static readonly bool[] Pending = new bool[5];

        static AudioSource Src(ref AudioSource s)
        {
            if (!_holder)
            {
                _holder = new GameObject("NFSMW Misc Sound");
                if (Application.isPlaying) Object.DontDestroyOnLoad(_holder);
                _radar = _camera = _uves = _start = _end = null;
            }
            if (!s)
            {
                s = _holder.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
            }
            return s;
        }

        static int UvesSlot(int type)
        {
            var sel = SoundLibrary.Patch(MiscBank).Select("FX_UVES");
            return sel != null && type >= 0 && type < sel.Length ? sel[type] : type + 2;
        }

        /// <summary>MMiscSound(id).Send("Snd").</summary>
        public static void Play(int id)
        {
            if (!Application.isPlaying || id < 0 || id > 4) return;
            Pending[id] = true;
            switch (id)
            {
                case 0:
                {
                    var s = Src(ref _radar);
                    var sel = SoundLibrary.Patch(MiscBank).Select("FX_Radar");
                    s.clip = SoundLibrary.ClipAt(MiscBank, sel != null && sel.Length > 0 ? sel[0] : 5);
                    s.volume = 0f;
                    s.Play();
                    break;
                }
                case 1:
                    if (_uves && _uves.isPlaying) break;
                    Src(ref _uves).clip = SoundLibrary.ClipAt(MiscBank, UvesSlot(0));
                    _uves.volume = 0f;
                    _uves.Play();
                    break;
                case 2:
                    if (_camera && _camera.isPlaying) break;
                    Src(ref _camera).clip = SoundLibrary.ClipAt(CameraBank, 1);
                    _camera.volume = 0f;
                    _camera.Play();
                    break;
                case 3:
                    Src(ref _start).Stop();
                    _start.clip = SoundLibrary.ClipAt(MiscBank, UvesSlot(2));
                    _start.volume = 0f;
                    _start.Play();
                    break;
                case 4:
                    Src(ref _end).Stop();
                    _end.clip = SoundLibrary.ClipAt(MiscBank, UvesSlot(1));
                    _end.volume = 0f;
                    _end.Play();
                    break;
            }
        }

        /// <summary>SFX_Common's DMIX inputs (the message id raised for one frame), before the mixer runs.</summary>
        internal static void Inputs(SndObj common)
        {
            if (common == null) return;
            for (int i = 0; i < 5; i++) { common.Set(i, Pending[i] ? 0x7FFF : 0); Pending[i] = false; }
            common.In[15] = 1;
        }

        /// <summary>SFX_Common::ProcessUpdate / EAXCommon radar volume, each frame after the mixer ran.</summary>
        internal static void Apply(SndObj common, SndObj fehud)
        {
            float V(SndObj o, int slot) => o != null && o.Mapped ? o.Vol(slot) : 1f;
            if (_radar) _radar.volume = V(fehud, 3);
            if (_camera) _camera.volume = V(common, 0);
            if (_uves) _uves.volume = V(common, 2);
            if (_start) _start.volume = V(common, 3);
            if (_end) _end.volume = V(common, 4);
        }
    }
}
