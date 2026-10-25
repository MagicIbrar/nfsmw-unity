using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NfsMw.UI
{
    /// <summary>
    /// The runtime cFrontendDatabase (Frontend/Database/FEDatabase.cpp): the persisted settings (Core/FEDatabase.cs), the
    /// session state the safehouse screens pass to each other (FEGameMode flags, RaceMode, iNumPlayers, the options
    /// category, last button per package) and the "My Cars" quick race garage. Applies the settings to Unity and to the
    /// older <see cref="Settings"/> statics the HUD and audio read. Saved per profile slot as JSON [ours: the game keeps
    /// it in the memory card / profile save].
    /// </summary>
    public static class FEDb
    {
        [Serializable]
        sealed class SaveFile
        {
            public FEDatabaseData Db = new FEDatabaseData();
            /// <summary>The quick race "My Cars" (FE_CAR_FILTER_LIST_QUICK_RACE, at most 20, MyCarsManager.cpp:121).</summary>
            public List<OwnedCar> MyCars = new List<OwnedCar>();
        }

        static SaveFile _file;
        public static FEDatabaseData Data { get { Load(); return _file.Db; } }
        public static List<OwnedCar> MyCars { get { Load(); return _file.MyCars; } }

        // ---- session state (cFrontendDatabase, not saved) ---------------------------------------------------------
        public static FEGameMode GameMode;
        /// <summary>cFrontendDatabase::RaceMode (GRace::Type of the quick race being set up).</summary>
        public static GRaceType RaceMode = GRaceType.Circuit;
        public static int NumPlayers = 1;
        public static bool OptionsDirty;
        /// <summary>UIOptionsScreen::PlayerToEdit.</summary>
        public static int PlayerToEdit;
        /// <summary>The PC display capability block (0x901838) [?: modern card].</summary>
        public static readonly PcVideoSettings Caps = PcVideoSettings.ModernCaps();
        /// <summary>FEngGetLastButton / FEngSetLastButton: the option index each icon menu last had.</summary>
        static readonly Dictionary<string, int> _lastButton = new Dictionary<string, int>();

        public static void SetGameMode(FEGameMode m) => GameMode |= m;
        public static void ClearGameMode(FEGameMode m) => GameMode &= ~m;
        public static void ResetGameMode() => GameMode = FEGameMode.None;
        public static bool Is(FEGameMode m) => (GameMode & m) != 0;
        public static bool IsSplitScreen => NumPlayers == 2 && Is(FEGameMode.QuickRace);

        // ---- settings the other systems read (hooks: these owners call them; the FE only stores the values) ----------
        /// <summary>PlayerSettings.Transmission (FED:67): 1 = manual, 0 = automatic (default) [hook: PlayerCar gearbox].</summary>
        public static bool ManualTransmission(int player = 0) => Data.Player(player).Transmission == 1;
        /// <summary>PlayerSettings.CurCam (ePlayerSettingsCameras, 0 = default chase) [hook: ChaseCamera start view].</summary>
        public static int DriveCamera(int player = 0) => Data.Player(player).CurCam;
        /// <summary>
        /// PlayerSettings::ScrollDriveCam (FEDatabase.cpp:115): CurCam steps 0..6 circularly, skipping cameras
        /// IsPlayerCameraSelectable rejects; dir 0 leaves it.
        /// </summary>
        public static void ScrollDriveCam(FEPlayerSettings p, int dir)
        {
            if (p == null || dir == 0) return;
            int cam = p.CurCam;
            for (int guard = 0; guard < 8; guard++)
            {
                cam += dir > 0 ? 1 : -1;
                if (cam > 6) cam = 0; else if (cam < 0) cam = 6;
                if (IsPlayerCameraSelectable(cam)) break;
            }
            p.CurCam = cam;
        }

        /// <summary>GetPOVTypeFromPlayerCamera (VehicleDB.cpp:45): ePlayerSettingsCameras → camerainfo collection name.</summary>
        static readonly string[] PovNames = { "bumper", "hood", "close", "far", "superfar", "drift", "pursuit" };

        /// <summary>
        /// IsPlayerCameraSelectable (VehicleDB.cpp:66): the player car model's ecar CameraInfo_* collection (its own
        /// &lt;model&gt;_&lt;cam&gt; variant when one exists, else the default) → camerainfo SELECTABLE[0] (index 1 in the
        /// two-player view). The camera enum order PSC_BUMPER..PSC_PURSUIT is taken as the camerainfo names [?]; a car
        /// without the attribute keeps the five labelled cameras (0..4) selectable.
        /// </summary>
        public static bool IsPlayerCameraSelectable(int cam)
        {
            if (cam < 0 || cam >= PovNames.Length) return false;
            var db = GameData.Attributes;
            string model = GameSession.ActiveCar?.Car?.ToLowerInvariant();
            var c = (model != null ? db?.Get("camerainfo", model + "_" + PovNames[cam]) : null) ?? db?.Get("camerainfo", PovNames[cam]);
            if (c != null && c.TryGet("SELECTABLE", out int at, out int count, out var def) && count > 0)
            {
                int idx = GameSession.SplitScreen && count > 1 ? 1 : 0;
                int size = def.Size > 0 ? def.Size : 1;
                byte[] d = c.Source.Data;
                int off = at + idx * size;
                if (off >= 0 && off + size <= d.Length)
                {
                    int v = size >= 4 ? Chunks.I32(d, off) : d[off];
                    if (v != 0) return true;
                    // every camerainfo reads 0 here [?: the SELECTABLE layout]; fall back to the labelled cameras
                }
            }
            return cam <= 4;
        }

        /// <summary>GameplaySettings.Damage (on by default) [hook: car visual damage].</summary>
        public static bool DamageOn => Data.Gameplay.Damage;
        /// <summary>The minimap mode in use (Minimap::Update): racing in a race, else exploring; 0 static, 1 rotating, 2 off.</summary>
        public static int MiniMapMode(bool racing) => racing ? Data.Gameplay.RacingMiniMapMode : Data.Gameplay.ExploringMiniMapMode;
        public static int LastButton(string package) => _lastButton.TryGetValue(package, out var i) ? i : 0;
        public static void SetLastButton(string package, int index) => _lastButton[package] = index;

        /// <summary>GetQuickRaceSettings (FED:979): NumTypes means "the current RaceMode".</summary>
        public static RaceSettings QuickRace(GRaceType t = GRaceType.NumTypes)
        {
            var a = Data.QuickRace;
            int i = t >= GRaceType.NumTypes ? (int)RaceMode : (int)t;
            return a[Mathf.Clamp(i, 0, a.Length - 1)];
        }

        static string PathFor() => Path.Combine(Application.persistentDataPath, $"nfsmw_fedb_{Profile.Slot}.json");

        public static void Load()
        {
            if (_file != null) return;
            try
            {
                var p = PathFor();
                if (File.Exists(p)) _file = JsonUtility.FromJson<SaveFile>(File.ReadAllText(p));
            }
            catch (Exception e) { Debug.LogWarning("NFSMW: FE database load failed: " + e.Message); }
            _file ??= new SaveFile();
            _file.Db ??= new FEDatabaseData();
            _file.MyCars ??= new List<OwnedCar>();
            var d = _file.Db;
            if (d.QuickRace == null || d.QuickRace.Length != (int)GRaceType.NumTypes) d.QuickRace = FEDatabaseData.NewQuickRace();
            if (d.PcVideo?.V == null || d.PcVideo.V.Length != PcVideoSettings.Count) d.PcVideo = new PcVideoSettings();
            if (!d.PcVideoInitialised)
            {
                // first boot: the exe picks g_PerformanceLevel from the card and runs the preset (sub_6C1B60) [?: level 3]
                d.PcVideo.ApplyPreset(d.PerformanceLevel, Caps);
                d.PcVideoInitialised = true;
            }
            foreach (var c in _file.MyCars) CarParts.Migrate(c);
        }

        /// <summary>Drops the cached file (the profile slot changed).</summary>
        public static void Reload() { _file = null; Load(); }

        public static void Save()
        {
            Load();
            try { File.WriteAllText(PathFor(), JsonUtility.ToJson(_file, true)); }
            catch (Exception e) { Debug.LogWarning("NFSMW: FE database save failed: " + e.Message); }
            OptionsDirty = false;
        }

        // ---- applying the settings ----------------------------------------------------------------------------------

        /// <summary>
        /// Pushes the options into the running game: speedometer units, music volume, v-sync, anti-aliasing, texture
        /// filtering and the racing resolution. The other volumes (speech, car, SFX) and the display detail switches are
        /// read from <see cref="Data"/> by their systems [hook: Audio/ and the renderer read FEDb.Data].
        /// </summary>
        public static void Apply(bool display = false)
        {
            var d = Data;
            Settings.Load();
            Settings.Kmh = d.Gameplay.SpeedoUnits == 1;
            Settings.Music = Mathf.Clamp01(d.Audio.IGMusicVol);
            Settings.Master = Mathf.Clamp01(d.Audio.MasterVol);
            AudioListener.volume = Settings.Master;
            var v = d.PcVideo;
            if (display) d.PcVideoUserSet = true;
            if (d.PcVideoUserSet)
            {
                Settings.VSync = v[PcVideoSettings.VSync] != 0;
                QualitySettings.vSyncCount = Settings.VSync ? 1 : 0;
                QualitySettings.antiAliasing = v[PcVideoSettings.Fsaa] switch { 0 => 0, 1 => 2, 2 => 4, _ => 8 };
                QualitySettings.anisotropicFiltering = v[PcVideoSettings.TextureFiltering] switch
                {
                    0 => AnisotropicFiltering.Disable, 1 => AnisotropicFiltering.Enable, _ => AnisotropicFiltering.ForceEnable,
                };
            }
            if (display)
            {
                var r = PcVideoSettings.Resolutions[Mathf.Clamp(v[PcVideoSettings.Resolution], 0, PcVideoSettings.Resolutions.Length - 1)];
                Settings.Width = r.w;
                Settings.Height = r.h;
                Settings.Apply(true);
            }
            Settings.Save();
        }

        /// <summary>Is a racing resolution entry available (the 0x93DBEC table keeps only modes the adapter lists) [?].</summary>
        public static bool ResolutionAvailable(int i)
        {
            if (i < 0 || i >= PcVideoSettings.Resolutions.Length) return false;
            if (Application.isEditor || Application.isBatchMode) return true;
            var (w, h) = PcVideoSettings.Resolutions[i];
            var modes = Screen.resolutions;
            return modes.Length == 0 || modes.Any(m => m.width == w && m.height == h);
        }

        // ---- shared lookups ------------------------------------------------------------------------------------------

        /// <summary>Localised text by label hash; "^" is the strings' line break.</summary>
        public static string L(uint hash, string fallback = "") => (FrontEndData.Text(hash, fallback) ?? fallback).Replace('^', '\n');

        public static GRaceType ToGRace(RaceType t) => t switch
        {
            RaceType.Sprint => GRaceType.P2P, RaceType.Circuit => GRaceType.Circuit, RaceType.Drag => GRaceType.Drag,
            RaceType.Knockout => GRaceType.Knockout, RaceType.Tollbooth => GRaceType.Tollbooth, RaceType.SpeedTrap => GRaceType.SpeedTrap,
            RaceType.Pursuit => GRaceType.Challenge, _ => GRaceType.None,
        };

        /// <summary>
        /// cFrontendDatabase::GetDefaultCar (0x57FB50, FED:882): frontend/default's default_car RefSpec (FE_ATTRIB: pvehicle
        /// m3gtre46careerstart, harness `fe9data defcar`) → the first stable car of that vehicle, else none (0) [ours: the quick
        /// race garage's car key, "" for none].
        /// </summary>
        public static string GetDefaultCar()
        {
            var fe = GameSession.Career?.Db?.Get("frontend", "default");
            if (fe == null) return "";
            foreach (var r in fe.Refs("default_car"))
                if (GameSession.Career.Db.Collections.TryGetValue(r, out var pv) && pv.Name != null)
                    foreach (var c in MyCars) if (c != null && string.Equals(c.Car, pv.Name, StringComparison.OrdinalIgnoreCase)) return c.Car;
            return "";
        }

        /// <summary>NotifyDeleteCar (0x58FF30, FED:854): every quick race setting whose SelectedCar[0] is the car gets the default car;
        /// a match on SelectedCar[1] also writes SelectedCar[0] (sic, the exe's slip).</summary>
        public static void NotifyDeleteCar(string car)
        {
            string def = GetDefaultCar();
            foreach (var st in Data.QuickRace)
            {
                if (st.SelectedCar[0] == car) st.SelectedCar[0] = def;
                if (st.SelectedCar.Length > 1 && st.SelectedCar[1] == car) st.SelectedCar[0] = def;
            }
        }

        // ---- HighScoresDatabase (Database/RaceDB.cpp) ----------------------------------------------------------------

        /// <summary>ePursuitDetailTypes (RaceDB.hpp:139) → the FE_ATTRIB frontend collections under "rapsheet" (CalcPursuitRank
        /// 0x5A3C30, RaceDB.cpp:152); single-pursuit ranks use "&lt;key&gt;_in_pursuit".</summary>
        public static readonly string[] PursuitRankKeys =
            { "pursuit_length", "cops_involved", "cops_damaged", "cops_destroyed", "tire_spikes_dodged", "roadblocks_dodged", "helis_involved", "cost_to_state", "total_infractions", "bounty" };
        public const int PdLength = 0, PdCopsInvolved = 1, PdCopsDamaged = 2, PdCopsDestroyed = 3, PdSpikes = 4, PdRoadblocks = 5, PdHelis = 6, PdCostToState = 7, PdInfractions = 8, PdBounty = 9, PdNumTypes = 10;

        static float PursuitValue(PursuitRecord r, int type) => r == null ? 0f : type switch
        {
            PdLength => r.Length, PdCopsInvolved => r.CopsInvolved, PdCopsDamaged => r.CopsDamaged, PdCopsDestroyed => r.CopsDestroyed,
            PdSpikes => r.Spikes, PdRoadblocks => r.Roadblocks, PdHelis => r.Helicopters, PdCostToState => r.Cts,
            PdInfractions => r.Infractions, PdBounty => r.Bounty, _ => 0f,
        };

        /// <summary>
        /// CareerPursuitScores::GetValue (0x5999F0, RaceDB.cpp:348): infractions = the stable's unserved + served totals
        /// (FEImpound.TotalNumInfractions), bounty = FEPlayerCarDB::GetTotalBounty (the player's bounty: Profile.Bounty, which the
        /// port keeps in one place [?]), else the career sum (the port's rap sheet Lifetime record; length in seconds).
        /// </summary>
        public static float CareerPursuitScore(int type)
        {
            var p = GameSession.Profile;
            if (type == PdInfractions) return FEImpound.TotalNumInfractions(true) + FEImpound.TotalNumInfractions(false);
            if (type == PdBounty) return p?.Bounty ?? 0;
            return PursuitValue(p?.RapSheet?.Lifetime, type);
        }

        /// <summary>
        /// HighScoresDatabase::CalcPursuitRank (0x5A3C30): rank 16 unless the collection has 15 RapSheetRanks (floats, best first);
        /// else the first i with value ≥ RapSheetRanks[i] (pursuit length compared as times) gives rank i + 1. career = the career
        /// score, else the best single pursuit (BestPursuitRankings: the port's rap sheet Best record).
        /// </summary>
        public static int CalcPursuitRank(int type, bool careerRank)
        {
            if (type < 0 || type >= PdNumTypes) return 16;
            var c = GameSession.Career?.Db?.Get("frontend", careerRank ? PursuitRankKeys[type] : PursuitRankKeys[type] + "_in_pursuit");
            if (c == null || !c.TryGet("RapSheetRanks", out _, out int count, out _) || count != 15) return 16;
            float v = careerRank ? CareerPursuitScore(type) : PursuitValue(GameSession.Profile?.RapSheet?.Best, type);
            bool isTime = type == PdLength;
            var ranks = c.Floats("RapSheetRanks");
            for (int i = 0; i < 15 && i < ranks.Length; i++)
            {
                bool found = isTime ? v >= ranks[i] : (int)v >= (int)ranks[i];
                if (found) return i + 1;
            }
            return 16;
        }

        /// <summary>
        /// GetGameCompletionStats (FED:1366): career = (career races + milestones done) / (all of them), challenge =
        /// completed / total Challenge Series races, rap sheet = Σ max(0, 15 − CalcPursuitRank(type, career)) over the ten
        /// pursuit types / 140; overall = 0.7 / 0.2 / 0.1 of them (each truncated to a byte).
        /// </summary>
        public static GameCompletionStats CompletionStats()
        {
            var s = new GameCompletionStats();
            var c = GameSession.Career;
            var p = GameSession.Profile;
            if (c == null) return s;
            float total = 0, done = 0, ms = 0, msDone = 0;
            for (int i = 1; i < 17; i++)
            {
                if (!c.Bins.TryGetValue(i, out var bin)) continue;
                foreach (var e in bin.BossRaces.Concat(bin.WorldRaces)) { total++; if (p != null && p.HasWon(e.Name)) done++; }
                foreach (var m in bin.Milestones) { ms++; if (p != null && p.Milestones.Contains(m.Name)) msDone++; }
            }
            // every Challenge Series race but the Burger King one (19.8.31); CE races count on the Collector's Edition (the PC v1.3)
            var challenge = GameSession.ChallengeEvents().Where(e => e.EventId != RaceIndex.BurgerKing).ToList();
            s.TotalChallengeRaces = challenge.Count;
            s.CompletedChallengeRaces = p == null ? 0 : challenge.Count(e => p.HasWon(e.Name));
            s.Career = total + ms > 0 ? (int)((done + msDone) / (total + ms) * 100f) : 0;
            s.Challenge = s.TotalChallengeRaces > 0 ? (int)((float)s.CompletedChallengeRaces / s.TotalChallengeRaces * 100f) : 0;
            float rap = 0;
            for (int t = 0; t < PdNumTypes; t++) { int move = 15 - CalcPursuitRank(t, true); if (move >= 0) rap += move; }
            s.RapSheetRankings = (int)(rap / 140f * 100f);
            s.Overall = (int)(s.Career * 0.7f + s.Challenge * 0.2f + s.RapSheetRankings * 0.1f);
            return s;
        }

        /// <summary>
        /// The loading packages live in GLOBAL/THINSCREEN_GLOBAL.BUN (Loading, Loading_Controller, Loading_Tips, PC_Loading),
        /// which FrontEndData does not read; this adds them (packages, textures, fonts) once.
        /// </summary>
        public static void EnsureLoadingPackages()
        {
            FrontEndData.Load();
            if (FrontEndData.Packages.ContainsKey("Loading_Tips")) return;
            var path = Path.Combine(GameData.Root, "GLOBAL", "THINSCREEN_GLOBAL.BUN");
            if (!File.Exists(path)) return;
            try
            {
                var d = GameFiles.ReadBundle(path);
                FrontEndData.Textures.Add(TexturePack.ReadAll(d, 0, d.Length));
                foreach (var kv in FEPackage.ReadBundle(d)) FrontEndData.Packages[kv.Key] = kv.Value;
            }
            catch (Exception e) { Debug.LogWarning("NFSMW: loading packages: " + e.Message); }
        }
    }

    /// <summary>
    /// The PC front end keys beyond MenuInput (the packages' PC_key_* prompts): 1 = PAD_BUTTON5, 2 = PAD_BUTTON4,
    /// 3 = PAD_BUTTON0 (Showcase), 4 = PAD_START (View Cart), Q = quit (main menu). Gamepad: X = 1, Y = 3 as MenuInput;
    /// LB = 2, RB = 4 [ours].
    /// </summary>
    public struct PcKeys
    {
        public bool Key2, Key4, Q, PageLeft, PageRight;

        public static PcKeys Read()
        {
            var k = new PcKeys();
            var kb = Keyboard.current;
            var g = Gamepad.current;
            if (kb != null)
            {
                k.Key2 = kb.digit2Key.wasPressedThisFrame;
                k.Key4 = kb.digit4Key.wasPressedThisFrame;
                k.Q = kb.qKey.wasPressedThisFrame;
                k.PageLeft = kb.pageUpKey.wasPressedThisFrame || kb.commaKey.wasPressedThisFrame;
                k.PageRight = kb.pageDownKey.wasPressedThisFrame || kb.periodKey.wasPressedThisFrame;
            }
            if (g != null)
            {
                k.Key2 |= g.leftShoulder.wasPressedThisFrame;
                k.Key4 |= g.rightShoulder.wasPressedThisFrame;
                k.PageLeft |= g.leftTrigger.wasPressedThisFrame;
                k.PageRight |= g.rightTrigger.wasPressedThisFrame;
            }
            return k;
        }
    }
}
