using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NfsMw.Game
{
    [Serializable]
    public sealed class OwnedCar
    {
        public string Car;              // CARS folder / pvehicle name, e.g. "gallardo"
        public float R = 0.8f, G = 0.82f, B = 0.84f;
        /// <summary>
        /// Installed performance package per type (Physics::Upgrades::Package.Part[7]): 0 = stock .. the car's pvehicle
        /// `*_upgrades` (older saves held 0..3 stages; CarParts.Migrate clamps them to the car's levels).
        /// </summary>
        public int Engine, Transmission, Tires, Suspension, Brakes, Nitrous, Induction;
        /// <summary>Package.Junkman: bit 1 &lt;&lt; Physics::Upgrades::Type of each installed Junkman part (VehicleDB.hpp).</summary>
        public int Junkman;
        public int PerfFormat = 1;
        public int Heat;                // 0..5, persistent per car like the game
        /// <summary>
        /// FECareerRecord.VehicleHeat (float, 1..5 meter): customization multiplies it (customization.md §6). Kept next
        /// to the int <see cref="Heat"/> the pursuit code uses; -1 = not set yet (taken from Heat). See CarParts.HeatOf.
        /// </summary>
        public float VehicleHeat = -1;
        /// <summary>Times this car may be busted before impound (FECareerRecord MaxBusted, default 3; impound box marker +1).</summary>
        public int MaxBusted = 3;
        /// <summary>
        /// FECustomizationRecord.InstalledPartIndices by slot: CarPartDB part names per CAR_SLOT_ID (World/CarPartID.h).
        /// Slots not listed are stock. The legacy fields below are what CarBuilder renders and are kept in step.
        /// </summary>
        public List<int> PartSlots = new List<int>();
        public List<string> PartNames = new List<string>();
        /// <summary>FECustomizationRecord.Tunings[3] x 7 floats (Physics::Tunings, -1..+1 [?]) and ActiveTuning.</summary>
        public float[] Tunings = new float[21];
        public int ActiveTuning;
        public bool HoodCF;
        // visual parts (0 / "" = stock)
        public int Kit, Hood, Spoiler, Roof;          // Spoiler -1 = removed
        public bool SpoilerCF, RoofCF;
        public string RoofType = "";                  // "", "DUAL", "OFFSET"
        public string RimBrand = "";
        public int RimStyle, RimSize = 18;
        public string Vinyl = "";

        public Color Paint
        {
            get => new Color(R, G, B);
            set { R = value.r; G = value.g; B = value.b; }
        }
    }

    /// <summary>GRaceSaveInfo (GRaceDatabase.h): one event's score flags and records.</summary>
    [Serializable]
    public sealed class RaceScore
    {
        public string Race;            // event collection name (the game keys by the event hash)
        public int Flags;              // RaceIndex.Completed* / Unlocked* bits
        /// <summary>mHighScores: best race time (sprint, drag, tollbooth, jumps), best lap (circuit, knockout), or points
        /// (speedtrap, cash grab); 0 = none.</summary>
        public float Best;
        public float TopSpeed, AverageSpeed;   // udecfix16 in the save (m/s here)
    }

    /// <summary>Offline career save (JSON in persistentDataPath).</summary>
    [Serializable]
    public sealed class Profile
    {
        public string Name = "Player";
        public int Cash = 30000;
        public int Bounty;
        public int Bin = 15;                        // current Blacklist rival (15 .. 1); 0 = career done
        public int CurrentCar;
        public List<OwnedCar> Cars = new List<OwnedCar>();
        public List<string> RacesWon = new List<string>();     // event collection names
        public List<string> Milestones = new List<string>();        // completed challenge collection names
        public List<string> BestKeys = new List<string>();           // best pursuit stat per milestone type
        public List<float> BestValues = new List<float>();
        public List<int> BossesBeaten = new List<int>();
        public List<string> ChallengesUnlocked = new List<string>(); // Challenge Series events opened by UnlockRaces
        public List<string> BonusCars = new List<string>();          // preset cars won in the Challenge Series
        public bool CareerStarted;
        // story position (Story.Prologue / CarLot / ToSafehouse / Free), finished story events (EventIDs),
        // the free-roam spawn (last safehouse used), and the SMS inbox
        public string Stage = "career";
        public List<string> EventsDone = new List<string>();
        public bool HasSpawn;
        public float SpawnX, SpawnY, SpawnZ, SpawnRot;
        public List<int> Inbox = new List<int>();
        /// <summary>
        /// GRaceStatus fCatchUpAdaptiveBonus (-1..1), kept in the career save as a signed 16-bit fraction
        /// (SyncronizeAdaptiveBonus 0x5DC1A0); Gameplay/AdaptiveDifficulty.cs updates it after career races.
        /// </summary>
        public float AdaptiveBonus;
        // rap sheet
        public int Pursuits, PursuitsEvaded, Busted, CopsDisabled, Infractions, RoadblocksDodged;
        public float TotalDistance, BestTopSpeed;
        /// <summary>
        /// Owned markers (FEMarkerManager.OwnedMarkers, UnlockSystem.cpp:830-1099), one entry per marker, by the
        /// gameplay reward's name: the backroom part id (`UpgradePartID`: brakes, chassis, engine, induction, nos, tires,
        /// transmission, bodykit, decals, hood, hud, paint, rims, roofscoop, spoiler, vinyls) or the marker type
        /// (`RewardMarkerType`: out_of_jail_free, add_impound_box, release_car_from_impound). Cash and pink slips are
        /// paid out when picked and never stored. Helpers: ShopMarkers (GameSession.cs).
        /// </summary>
        public List<string> Markers = new List<string>();
        /// <summary>
        /// TheUnlockData[57] (UnlockSystem.hpp:12-21) by eUnlockableEntity: career unlock level, "new" level and times
        /// the shop was entered since (cleared after 4). Filled from each bin's BaselineUnlocks (customization.md §3.4).
        /// </summary>
        public List<int> UnlockLevels = new List<int>(), UnlockNew = new List<int>(), UnlockSeen = new List<int>();
        /// <summary>The bin whose BaselineUnlocks were applied last (0 = none yet).</summary>
        public int UnlocksAppliedBin;
        /// <summary>
        /// CareerSettings.SMSMessages flags (FEDatabase.hpp:264): Inbox holds every SMS received in arrival order (the
        /// sort order); read ones are also in SmsRead (READ = 4, else UNREAD = 2); deleted ones in SmsDeleted (flags 0).
        /// </summary>
        public List<int> SmsRead = new List<int>(), SmsDeleted = new List<int>();
        /// <summary>The police record behind the Rap Sheet and Infractions screens (RapSheetScreens.cs).</summary>
        public UI.RapSheetRecord RapSheet = new UI.RapSheetRecord();
        /// <summary>
        /// GRaceDatabase score infos (GRaceSaveInfo, 0x10 bytes each: race hash, ScoreFlags, best time / lap / points by
        /// race type, top and average speed), kept by event collection name; see Gameplay/RaceDatabase.cs. The career
        /// completed flag (2) mirrors <see cref="RacesWon"/>.
        /// </summary>
        public List<RaceScore> RaceScores = new List<RaceScore>();
        /// <summary>GRaceDatabase::ClearRaceScores (0x600B20) ran on this save (the initial quick race / online unlocks).</summary>
        public bool RaceScoresInitialized;
        /// <summary>
        /// cFrontendDatabase flags +0x12C bit 17 (IsPostRivalMode 0x50E560): set by EQuitToFE (0x623F80) when the last boss
        /// race of a bin is won (PhotoFinishScreen quits to SafeHouseRivalChallenge.fng), cleared by uiRepSheetRivalFlow::Next.
        /// </summary>
        public bool PostRivalMode;
        /// <summary>Career flags +0xAC: the tutorial movies seen (0x40 drag, 0x80 speedtrap, 0x100 tollbooth; Game.PlayTutorial 0x6124E0).</summary>
        public int TutorialsSeen;
        /// <summary>CareerSettings flag 0x4000 (SetPlayerHasBeatenTheGame 0x56D7E0, from EQuitToFE's game-won branch 0x624105).</summary>
        public bool BeatenTheGame;
        /// <summary>CareerSettings flag 0x80000 (HasDoneMapLoadigTip; set by LoadingTips' TipTestFirstTimeOutOfSafeHouse 0x56AEC0).</summary>
        public bool HasDoneMapLoadingTip;
        /// <summary>GameplaySettings: LastMapView (0 navigation, 1 event), LastMapZoom, LastPursuitMapZoom, MapItems (-1 = all).</summary>
        public int MapView, MapZoom, MapPursuitZoom = 1, MapItems = -1;
        /// <summary>
        /// GManager::SaveGameplayData (0x5F7410): the persistent object-state map — the saved stategraphs (race_bin_NN,
        /// speedtraps, milestones: current state and Lua state table, GActivity::SaveState 0x5F6BA0) — and the 8 GManager
        /// timers (SaveTimerInfo 0x5E4C40). Written by LuaFlow on every save; empty in older saves (the scripts start over).
        /// </summary>
        public List<NfsMw.Core.Lua.SavedActivityState> ScriptStates = new List<NfsMw.Core.Lua.SavedActivityState>();
        public List<NfsMw.Core.Lua.SavedTimer> ScriptTimers = new List<NfsMw.Core.Lua.SavedTimer>();
        /// <summary>GManager's pending SMS list (+0x2A8, SaveGameplayData / LoadSMSInfo 0x5F50C0): messages not yet put in
        /// the inbox (they wait for CanPlaySMS).</summary>
        public List<int> SmsPending = new List<int>();

        /// <summary>Raised before the profile is written: GManager::SaveGameplayData's parts held outside the profile
        /// (LuaFlow's script states, the pending SMS) are copied in [ours: the hook].</summary>
        public static event Action<Profile> Saving;

        public OwnedCar Car => Cars.Count == 0 ? null : Cars[Mathf.Clamp(CurrentCar, 0, Cars.Count - 1)];

        /// <summary>Save slot name (tests use their own so they never touch the player's career).</summary>
        public static string Slot = "career";

        public static string PathFor(string slot = null) => System.IO.Path.Combine(Application.persistentDataPath, $"nfsmw_{slot ?? Slot}.json");

        public static Profile Load(string slot = null)
        {
            try
            {
                var p = PathFor(slot);
                if (File.Exists(p)) return JsonUtility.FromJson<Profile>(File.ReadAllText(p)) ?? new Profile();
            }
            catch (Exception e) { Debug.LogWarning("NFSMW: profile load failed: " + e.Message); }
            return new Profile();
        }

        public void Save(string slot = null)
        {
            try { Saving?.Invoke(this); }
            catch (Exception e) { Debug.LogWarning("NFSMW: profile save hook failed: " + e.Message); }
            try { File.WriteAllText(PathFor(slot), JsonUtility.ToJson(this, true)); }
            catch (Exception e) { Debug.LogWarning("NFSMW: profile save failed: " + e.Message); }
        }

        public bool Owns(string car) => Cars.Exists(c => string.Equals(c.Car, car, StringComparison.OrdinalIgnoreCase));
        public bool HasWon(string eventName) => RacesWon.Contains(eventName);

        public float Best(string type)
        {
            int i = BestKeys.IndexOf(type);
            return i >= 0 && i < BestValues.Count ? BestValues[i] : 0;
        }

        /// <summary>Keeps the better of the stored and new value (smaller for times to beat).</summary>
        public void RecordBest(string type, float v, bool biggerIsBetter)
        {
            int i = BestKeys.IndexOf(type);
            if (i < 0) { BestKeys.Add(type); BestValues.Add(v); return; }
            if (biggerIsBetter ? v > BestValues[i] : BestValues[i] <= 0 || v < BestValues[i]) BestValues[i] = v;
        }
    }
}
