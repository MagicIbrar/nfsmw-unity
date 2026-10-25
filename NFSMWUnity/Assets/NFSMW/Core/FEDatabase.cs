using System.Collections.Generic;
using System;

namespace NfsMw.Core
{
    // The front end's settings database (cFrontendDatabase / UserProfile.TheOptionsSettings, Frontend/Database/
    // FEDatabase.hpp + .cpp, RaceDB.cpp) with the original fields, value ranges and defaults, plus the PC-only display
    // settings (the 132-byte video block at 0x9017B0 that speed.exe keeps in the registry as g_* values).
    // Unity-free: the runtime store (Runtime/UI/Safehouse/FEDb.cs) persists it and applies it.
    // Decomp paths below are relative to refs/nfsmw/src/Speed/Indep/Src/Frontend/ (FED = Database/FEDatabase.cpp).
    // See NFSMW_RE/specs/fe_safehouse.md §1.

    /// <summary>eFEGameModes (FEDatabase.hpp:22): bit flags of cFrontendDatabase::FEGameMode.</summary>
    [Flags]
    public enum FEGameMode : uint
    {
        None = 0, Career = 1, Challenge = 2, QuickRace = 4, Online = 8, Options = 16, Customize = 32, Lan = 64,
        ProfileManager = 128, CareerManager = 256, RapSheet = 512, ModeSelect = 1024, Trailers = 2048,
        CarLot = 32768, Safehouse = 65536, PostRival = 131072, BeatGame = 262144,
    }

    /// <summary>eOptionsCategory (FEDatabase.hpp:59). OC_PC_ADV_DISPLAY exists in the enum; PC uses OC_VIDEO for both pages.</summary>
    public enum OptionsCategory { Audio = 0, Video = 1, PcAdvDisplay = 2, Gameplay = 3, Player = 4, Controls = 5, EATrax = 6, Trailers = 7, Credits = 8, Online = 9 }

    /// <summary>GRace::Type (Gameplay/GRace.h:29): the index of cFrontendDatabase::TheQuickRaceSettings.</summary>
    public enum GRaceType { None = -1, P2P = 0, Circuit = 1, Drag = 2, Knockout = 3, Tollbooth = 4, SpeedTrap = 5, Checkpoint = 6, CashGrab = 7, Challenge = 8, JumpToSpeedTrap = 9, JumpToMilestone = 10, NumTypes = 11 }

    /// <summary>FEPlayerSettings (FEDatabase.hpp:128, FED:67-96), one per player.</summary>
    [Serializable]
    public sealed class FEPlayerSettings
    {
        public bool GaugesOn, PositionOn, LapInfoOn, ScoreOn, Rumble, LeaderboardOn, TransmissionPromptOn, DriveWithAnalog;
        /// <summary>eControllerConfig CC_CONFIG_1..5.</summary>
        public int Config;
        /// <summary>ePlayerSettingsCameras 0..6 (PSC_DEFAULT = 0 [?]); PODriveCam labels 0..4.</summary>
        public int CurCam;
        /// <summary>0 = shown (On), 4 = Off (POSplitTime toggles 0 ↔ 4); 1..3 are labels the toggle never reaches.</summary>
        public int SplitTimeType;
        /// <summary>0 = Auto, 1 = Manual (POTransmission).</summary>
        public int Transmission;
        public int Handling;

        public FEPlayerSettings() => Default();

        /// <summary>FEPlayerSettings::Default (FED:67).</summary>
        public void Default()
        {
            GaugesOn = PositionOn = LapInfoOn = ScoreOn = LeaderboardOn = TransmissionPromptOn = Rumble = DriveWithAnalog = true;
            Config = 0;
            SplitTimeType = 0;
            CurCam = 0;
            Transmission = 0;
            Handling = 1;
        }

        /// <summary>DefaultFromOptionsScreen (FED:87): keeps the analog, controller config and rumble choices.</summary>
        public void DefaultFromOptionsScreen()
        {
            bool analog = DriveWithAnalog, rumble = Rumble;
            int config = Config;
            Default();
            DriveWithAnalog = analog; Config = config; Rumble = rumble;
        }

        public FEPlayerSettings Clone() => (FEPlayerSettings)MemberwiseClone();
        public void CopyFrom(FEPlayerSettings o) { GaugesOn = o.GaugesOn; PositionOn = o.PositionOn; LapInfoOn = o.LapInfoOn; ScoreOn = o.ScoreOn; Rumble = o.Rumble; LeaderboardOn = o.LeaderboardOn; TransmissionPromptOn = o.TransmissionPromptOn; DriveWithAnalog = o.DriveWithAnalog; Config = o.Config; CurCam = o.CurCam; SplitTimeType = o.SplitTimeType; Transmission = o.Transmission; Handling = o.Handling; }
        /// <summary>operator== is a memcmp of the record (FED:83).</summary>
        public bool Same(FEPlayerSettings o) => o != null && GaugesOn == o.GaugesOn && PositionOn == o.PositionOn && LapInfoOn == o.LapInfoOn && ScoreOn == o.ScoreOn && Rumble == o.Rumble && LeaderboardOn == o.LeaderboardOn && TransmissionPromptOn == o.TransmissionPromptOn && DriveWithAnalog == o.DriveWithAnalog && Config == o.Config && CurCam == o.CurCam && SplitTimeType == o.SplitTimeType && Transmission == o.Transmission && Handling == o.Handling;
    }

    /// <summary>FEGameplaySettings (FEDatabase.hpp:156, FED:138).</summary>
    [Serializable]
    public sealed class FEGameplaySettings
    {
        public bool AutoSaveOn, RearviewOn, Damage, JumpCam;
        /// <summary>0 = mph (English), 1 = km/h: GOSpeedoUnits, uiQRTrackSelect.cpp:323.</summary>
        public int SpeedoUnits;
        /// <summary>0 Static (0xF75595F2), 1 Rotating (0xF4B00E99), 2 Off.</summary>
        public int RacingMiniMapMode, ExploringMiniMapMode;
        public uint MapItems;
        public int LastMapZoom, LastPursuitMapZoom, LastMapView;
        public float HighlightCam;

        public FEGameplaySettings() => Default();

        public void Default()
        {
            AutoSaveOn = RearviewOn = Damage = true;
            RacingMiniMapMode = 1;
            ExploringMiniMapMode = 0;
            MapItems = 0xFFFFFFFF;
            LastMapZoom = 1;
            LastPursuitMapZoom = 2;
            LastMapView = 0;
            JumpCam = true;
            HighlightCam = 127.5f;
            SpeedoUnits = Localize.DefaultSpeedoUnits;   // GetCurrentLanguage() == English (0) → 0, every other language → 1 (LanguageHasChanged 0x57E6B0)
        }

        public FEGameplaySettings Clone() => (FEGameplaySettings)MemberwiseClone();
        public void CopyFrom(FEGameplaySettings o) { AutoSaveOn = o.AutoSaveOn; RearviewOn = o.RearviewOn; Damage = o.Damage; JumpCam = o.JumpCam; SpeedoUnits = o.SpeedoUnits; RacingMiniMapMode = o.RacingMiniMapMode; ExploringMiniMapMode = o.ExploringMiniMapMode; MapItems = o.MapItems; LastMapZoom = o.LastMapZoom; LastPursuitMapZoom = o.LastPursuitMapZoom; LastMapView = o.LastMapView; HighlightCam = o.HighlightCam; }
        public bool Same(FEGameplaySettings o) => o != null && AutoSaveOn == o.AutoSaveOn && RearviewOn == o.RearviewOn && Damage == o.Damage && JumpCam == o.JumpCam && SpeedoUnits == o.SpeedoUnits && RacingMiniMapMode == o.RacingMiniMapMode && ExploringMiniMapMode == o.ExploringMiniMapMode && MapItems == o.MapItems && LastMapZoom == o.LastMapZoom && LastPursuitMapZoom == o.LastPursuitMapZoom && LastMapView == o.LastMapView && HighlightCam == o.HighlightCam;
    }

    /// <summary>Console FEVideoSettings (FEDatabase.hpp:181, FED:176): kept for the save layout; the PC pages use <see cref="PcVideoSettings"/>.</summary>
    [Serializable]
    public sealed class FEVideoSettings
    {
        public float FEScale = 1f, ScreenOffsetX, ScreenOffsetY;
        public bool WideScreen;
        public void Default() { FEScale = 1f; ScreenOffsetX = ScreenOffsetY = 0f; WideScreen = false; }
    }

    /// <summary>FEAudioSettings (FEDatabase.hpp:196, FED:187).</summary>
    [Serializable]
    public sealed class FEAudioSettings
    {
        public float MasterVol, SpeechVol, FEMusicVol, IGMusicVol, SoundEffectsVol, EngineVol, CarVol, AmbientVol, SpeedVol;
        /// <summary>0 Mono (0xC50FA35F), 1 Stereo (0x55DA8BF8), 2 Surround (0xF6FAFF24); WIN32 default 1.</summary>
        public int AudioMode;
        public int InteractiveMusicMode, EATraxMode, PlayState;

        public FEAudioSettings() => Default();

        public void Default()
        {
            MasterVol = 1f; SpeechVol = 1f; FEMusicVol = 0.8f; IGMusicVol = 0.8f; SoundEffectsVol = 1f;
            EngineVol = 1f; CarVol = 1f; AmbientVol = 1f; SpeedVol = 1f;
            AudioMode = 1;   // EA_PLATFORM_WIN32 (g_pEAXSound->GetDefaultPlatformAudioMode() [?])
            InteractiveMusicMode = 1; EATraxMode = 1; PlayState = 0;
        }

        public FEAudioSettings Clone() => (FEAudioSettings)MemberwiseClone();
        public void CopyFrom(FEAudioSettings o) { MasterVol = o.MasterVol; SpeechVol = o.SpeechVol; FEMusicVol = o.FEMusicVol; IGMusicVol = o.IGMusicVol; SoundEffectsVol = o.SoundEffectsVol; EngineVol = o.EngineVol; CarVol = o.CarVol; AmbientVol = o.AmbientVol; SpeedVol = o.SpeedVol; AudioMode = o.AudioMode; InteractiveMusicMode = o.InteractiveMusicMode; EATraxMode = o.EATraxMode; PlayState = o.PlayState; }
        /// <summary>WIN32 operator== is a memcmp (FED:211).</summary>
        public bool Same(FEAudioSettings o) => o != null && MasterVol == o.MasterVol && SpeechVol == o.SpeechVol && FEMusicVol == o.FEMusicVol && IGMusicVol == o.IGMusicVol && SoundEffectsVol == o.SoundEffectsVol && EngineVol == o.EngineVol && CarVol == o.CarVol && AmbientVol == o.AmbientVol && SpeedVol == o.SpeedVol && AudioMode == o.AudioMode && InteractiveMusicMode == o.InteractiveMusicMode && EATraxMode == o.EATraxMode && PlayState == o.PlayState;
    }

    /// <summary>
    /// The PC display block (0x9017B0, 33 dwords = 132 bytes, copied with `rep movsd` 0x21) and its capability twin
    /// (0x901838, the per-field maximum the D3D device allows, filled at 0x6D2130). Field = dword index (byte offset / 4).
    /// Registry names from the load at 0x6C1C80. The OptionsPCDisplay widgets read and write these slots directly.
    /// </summary>
    [Serializable]
    public sealed class PcVideoSettings
    {
        public const int CarEnvMap = 2;        // +0x08 g_CarEnvironmentMapEnable     — "Car Reflection Update Rate" slider 0..caps
        public const int CarEnvUpdate = 3;     // +0x0C g_CarEnvironmentMapUpdateData — "Car Reflection Detail" Low/High toggle
        public const int RoadReflection = 9;   // +0x24 g_RoadReflectionEnable         — "Road Reflection Detail" slider
        public const int MotionBlur = 11;      // +0x2C g_MotionBlurEnable (no widget on PC)
        public const int ParticleSystem = 15;  // +0x3C g_ParticleSystemEnable (no widget)
        public const int WorldLod = 17;        // +0x44 g_WorldLodLevel                — "World Level Of Detail" slider
        public const int CarLod = 18;          // +0x48 g_CarLodLevel                  — "Car Geometry Detail" Low/High
        public const int OverBright = 19;      // +0x4C g_OverBrightEnable             — "Over Bright" On/Off
        public const int Fsaa = 22;            // +0x58 g_FSAALevel                    — "Full Screen Anti-Aliasing" slider
        public const int Rain = 24;            // +0x60 g_RainEnable                   — "Rain Effect" On/Off
        public const int TextureFiltering = 26;// +0x68 g_TextureFiltering             — slider
        public const int Resolution = 27;      // +0x6C g_RacingResolution             — 0..5 = 640x480 .. 1600x1200
        public const int VSync = 29;           // +0x74 g_VSyncOn                      — On/Off
        public const int VisualTreatment = 30; // +0x78 g_VisualTreatment              — Low/High
        public const int ShadowDetail = 32;    // +0x80 g_ShadowDetail                 — slider
        public const int Count = 33;

        /// <summary>The block as stored. Initial contents = the exe's .data at 0x9017B0 (before the registry load).</summary>
        public int[] V = (int[])ExeInitial.Clone();

        public static readonly int[] ExeInitial =
        {
            1, 132, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0,
        };

        /// <summary>The resolution table at 0x93DBEC (labels 0xDCCBD59E .. 0x76667B12).</summary>
        public static readonly (int w, int h)[] Resolutions = { (640, 480), (800, 600), (1024, 768), (1280, 960), (1280, 1024), (1600, 1200) };

        public int this[int i] { get => V[i]; set => V[i] = value; }
        public PcVideoSettings Clone() => new PcVideoSettings { V = (int[])V.Clone() };
        public void CopyFrom(PcVideoSettings o) => Array.Copy(o.V, V, Count);
        public bool Same(PcVideoSettings o)
        {
            if (o == null) return false;
            for (int i = 0; i < Count; i++) if (V[i] != o.V[i]) return false;
            return true;
        }

        /// <summary>
        /// sub_6C1990: the "Level Of Detail" preset (g_PerformanceLevel 1..5) written over the block. The GPU vendor tweaks
        /// that follow it (0x6C1B65: ATI 0x1002 clears FSAA; sub_6C1780 clamps to the caps) are applied by <see cref="Clamp"/>.
        /// The last step zeroes WorldLod and CarLod on machines with less than 400 MB of RAM (GlobalMemoryStatus) — not done here.
        /// </summary>
        public void ApplyPreset(int level, PcVideoSettings caps)
        {
            int[] v = V;
            v[Resolution] = 0; v[CarEnvMap] = 0; v[CarEnvUpdate] = 0; v[4] = 1; v[5] = 0; v[8] = 1; v[RoadReflection] = 0;
            v[ParticleSystem] = 1; v[WorldLod] = 0; v[CarLod] = 0; v[Rain] = 1; v[MotionBlur] = 1; v[12] = 1; v[13] = 1;
            v[16] = 0; v[OverBright] = 0; v[21] = 0; v[23] = 1; v[Fsaa] = 0; v[TextureFiltering] = 0; v[10] = 0; v[VSync] = 0;
            v[VisualTreatment] = 0; v[ShadowDetail] = 0;
            if (level > 1) { v[4] = 2; v[5] = 1; v[WorldLod] = 1; }
            if (level > 2)
            {
                v[CarEnvMap] = 1; v[TextureFiltering] = 1; v[RoadReflection] = 1; v[10] = 1; v[ParticleSystem] = 1;
                v[WorldLod] = 2; v[23] = 1; v[16] = 1; v[13] = 1;
            }
            if (level > 3)
            {
                v[Rain] = 1; v[20] = 1; v[CarEnvUpdate] = 1;   // (__getUnkMultByGPU result discarded)
                v[Resolution] = 1; v[Fsaa] = 1; v[VisualTreatment] = 1; v[ShadowDetail] = 1; v[OverBright] = 1; v[12] = 1; v[MotionBlur] = 1;
            }
            if (level >= 4) { v[CarEnvUpdate] = 2; v[RoadReflection] = 2; v[CarLod] = 1; }
            if (caps != null && caps.V[VisualTreatment] != 0) v[VisualTreatment] = 1;
            v[20] = 0; v[16] = 0; v[21] = 0;
            Clamp(caps);
        }

        /// <summary>sub_6C1780 [?]: every slot limited to its capability maximum.</summary>
        public void Clamp(PcVideoSettings caps)
        {
            if (caps == null) return;
            foreach (int i in new[] { CarEnvMap, CarEnvUpdate, RoadReflection, WorldLod, CarLod, OverBright, Fsaa, Rain, TextureFiltering, VisualTreatment, ShadowDetail })
                if (V[i] > caps.V[i]) V[i] = caps.V[i];
            if (V[Resolution] < 0 || V[Resolution] >= Resolutions.Length) V[Resolution] = 0;
        }

        /// <summary>
        /// The capability block (0x901838) for a current D3D9 card [?]. 0x6D2130 sets CarEnvMap 3, CarEnvUpdate 1 and
        /// WorldLod 3 outright; the rest depend on the device (multisample levels, anisotropy, vendor ids) and are guesses
        /// for a modern GPU: road reflection 2, shadows 2, FSAA 2, texture filtering 2, every toggle 1.
        /// </summary>
        public static PcVideoSettings ModernCaps()
        {
            var c = new PcVideoSettings { V = new int[Count] };
            c.V[CarEnvMap] = 3; c.V[CarEnvUpdate] = 1; c.V[RoadReflection] = 2; c.V[WorldLod] = 3; c.V[CarLod] = 1; c.V[OverBright] = 1;
            c.V[Fsaa] = 2; c.V[Rain] = 1; c.V[TextureFiltering] = 2; c.V[VisualTreatment] = 1; c.V[ShadowDetail] = 2; c.V[VSync] = 1;
            return c;
        }
    }

    /// <summary>RaceSettings (Database/RaceDB.hpp:30, RaceDB.cpp:30): one per GRace::Type.</summary>
    [Serializable]
    public sealed class RaceSettings
    {
        public uint EventHash;
        /// <summary>The Unity event key (RaceEvent.Name) of EventHash [ours: the port keys events by name].</summary>
        public string EventName = "";
        public int NumLaps, TrackDirection, NumOpponents, AISkill, CopDensity, TrafficDensity;
        public bool IsLapKO, CatchUp, CopsOn;
        /// <summary>0..3 = region filter of Track_Select (RegionFilterBits; 3 = all [?] via kRaceRegion_NumRegions).</summary>
        public int RegionFilterBits;
        /// <summary>SelectedCar[2] as the port's car keys (FECarRecord handles in the game).</summary>
        public string[] SelectedCar = { "", "" };

        public RaceSettings() => Default();

        /// <summary>RaceSettings::Default (RaceDB.cpp:30).</summary>
        public void Default()
        {
            NumOpponents = 3; AISkill = 1; CopDensity = 1; TrafficDensity = 1; CatchUp = true; CopsOn = false;
            TrackDirection = 0; NumLaps = 2; IsLapKO = false; SelectedCar = new[] { "", "" }; RegionFilterBits = 3;
        }
    }

    /// <summary>GameCompletionStats (FEDatabase.hpp:599, FED:1366).</summary>
    public struct GameCompletionStats
    {
        public int Overall, Career, RapSheetRankings, Challenge, TotalChallengeRaces, CompletedChallengeRaces;
    }

    [Serializable]
    public sealed class JukeboxEntry { public int SongIndex; public int Playability; }

    /// <summary>
    /// FECareerRecord + FEImpoundData (Database/VehicleDB.hpp:170-230, VehicleDB.cpp:1216-1434) of one career car, keyed by
    /// its index in the profile's car list [ours: the exe keeps them in the car stable; MaxBusted lives on the car record].
    /// ImpoundedState: 0 none, 1 busted by Cross, 2 strike limit, 3 insufficient funds, 4 released (IsReleasable).
    /// </summary>
    [Serializable]
    public sealed class FECareerCarRecord
    {
        public int CarIndex;
        public int TimesBusted, ImpoundedState, DaysBeforeRelease, EvadeCount;
        public int Bounty, NumEvadedPursuits, NumBustedPursuits;
        /// <summary>FEInfractionsData counts (unserved / served) in GInfractionManager order: speeding, excessive speeding,
        /// reckless, ramming (assault), hit &amp; run, damage, resisting arrest, off road.</summary>
        public int[] Unserved = new int[8], Served = new int[8];
        public const int ImpoundNone = 0, ImpoundBustedByCross = 1, ImpoundStrikeLimit = 2, ImpoundInsufficientFunds = 3, ImpoundReleased = 4;
        public bool IsImpounded => ImpoundedState != ImpoundNone;
        public bool IsReleasable => ImpoundedState == ImpoundReleased;
    }

    [Serializable]
    public sealed class KeyBinding { public string Action; public int Primary, Secondary; }

    /// <summary>
    /// The persisted part of cFrontendDatabase + UserProfile.TheOptionsSettings: the options, the 11 quick race
    /// setting records, the PC display block and level, and the quick race "My Cars" list name keys.
    /// </summary>
    [Serializable]
    public sealed class FEDatabaseData
    {
        public int CurrentCategory;
        /// <summary>CareerSettings flag 0x4000 (SetPlayerHasBeatenTheGame 0x56D7E0): set by EQuitToFE (0x623F80) when the final
        /// epic chase 1.8.1 is won, before GameWon.fng [ours: kept with the profile slot's FE data].</summary>
        public bool PlayerHasBeatenTheGame;
        /// <summary>CareerSettings flag 0x80000 (HasDoneMapLoadigTip, set by TipTestFirstTimeOutOfSafeHouse 0x56AEC0) [ours: slot FE data].</summary>
        public bool HasDoneMapLoadingTip;
        public FEVideoSettings Video = new FEVideoSettings();
        public FEGameplaySettings Gameplay = new FEGameplaySettings();
        public FEAudioSettings Audio = new FEAudioSettings();
        public FEPlayerSettings Player1 = new FEPlayerSettings(), Player2 = new FEPlayerSettings();
        public PcVideoSettings PcVideo = new PcVideoSettings();
        /// <summary>g_PerformanceLevel (0x982C00), the "Level Of Detail" slider 1..5.</summary>
        public int PerformanceLevel = 3;
        public bool PcVideoInitialised;
        /// <summary>[ours] the player accepted the display page once: only then does the port push FSAA / filtering / v-sync into Unity at start.</summary>
        public bool PcVideoUserSet;
        public RaceSettings[] QuickRace = NewQuickRace();
        /// <summary>UserProfile::Playlist (JukeboxEntry[]: SongIndex, PlayabilityField 0 off / 1 FE / 2 in game / 3 both), the
        /// EA Trax jukebox order; empty = the default order with each song's DefPlay [hook: the music system reads it].</summary>
        public List<JukeboxEntry> Playlist = new List<JukeboxEntry>();
        /// <summary>The PC key bindings (Options_PC_Controller; live table 0x91F418, defaults 0x8F6D80): per action the primary
        /// and secondary key as UnityEngine.InputSystem.Key values (0 = unbound) [hook: the input readers apply them].</summary>
        public List<KeyBinding> KeyBindings = new List<KeyBinding>();
        /// <summary>The career cars' FECareerRecord / FEImpoundData (Runtime/Game/Shop.cs FEImpound reads and writes them).</summary>
        public List<FECareerCarRecord> CareerCars = new List<FECareerCarRecord>();
        /// <summary>FEPlayerCarDB SoldHistoryBounty / NumEvadedPursuits / NumBustedPursuits / Unserved / ServedInfractions
        /// (VehicleDB.cpp:414 BackupSoldCarHistory): what sold career cars had on record, kept in the stable totals.</summary>
        public int SoldHistoryBounty, SoldHistoryNumEvadedPursuits, SoldHistoryNumBustedPursuits;
        public int[] SoldHistoryUnserved = new int[8], SoldHistoryServed = new int[8];

        public FEPlayerSettings Player(int n) => n == 1 ? Player2 : Player1;

        /// <summary>OptionsSettings::Default (FED:245).</summary>
        public void DefaultOptions()
        {
            CurrentCategory = (int)OptionsCategory.Audio;
            Video.Default(); Audio.Default(); Gameplay.Default(); Player1.Default(); Player2.Default();
        }

        /// <summary>cFrontendDatabase::DefaultRaceSettings (FED:838).</summary>
        public static RaceSettings[] NewQuickRace()
        {
            var a = new RaceSettings[(int)GRaceType.NumTypes];
            for (int i = 0; i < a.Length; i++) a[i] = new RaceSettings();
            a[(int)GRaceType.P2P].NumLaps = 1;
            a[(int)GRaceType.Drag].NumLaps = 1;
            a[(int)GRaceType.SpeedTrap].NumLaps = 1;
            a[(int)GRaceType.Tollbooth].NumOpponents = 0;
            a[(int)GRaceType.Knockout].NumLaps = a[(int)GRaceType.Knockout].NumOpponents;
            a[(int)GRaceType.Tollbooth].NumLaps = 1;
            return a;
        }

        /// <summary>GetRaceNameHash (FED:1291).</summary>
        public static uint RaceNameHash(GRaceType t) => t switch
        {
            GRaceType.P2P => 0xB94FD70E, GRaceType.Circuit => 0x034FA2C1, GRaceType.Drag => 0x6F547E4C, GRaceType.Knockout => 0x4930F5FC,
            GRaceType.Tollbooth => 0xA15E4505, GRaceType.SpeedTrap => 0xEE1EDC76, GRaceType.Challenge => 0x213CC8D1, _ => 0x7818F85E,
        };

        /// <summary>GetRaceIconHash (FED:1322): MODE_ICON textures.</summary>
        public static uint RaceIconHash(GRaceType t) => t switch
        {
            GRaceType.P2P => 0x2521E5EB, GRaceType.Circuit => 0xE9638D3E, GRaceType.Drag => 0xAAAB31E9, GRaceType.Knockout => 0x3A015595,
            GRaceType.Tollbooth => 0x1A091045, GRaceType.SpeedTrap or GRaceType.JumpToSpeedTrap => 0x66C9A7B6, _ => 0,
        };

        /// <summary>FillCustomRace traffic density (FED:1035): 0, 10, 30, 50.</summary>
        public static int TrafficPercent(int density) => density switch { 1 => 10, 2 => 30, 3 => 50, _ => 0 };
    }
}
