using System.Collections.Generic;

namespace NfsMw.Core.Lua
{
    /// <summary>
    /// One function the scripts can call (LuaBindery::BindToGameCode 0x61E750). The signature is the argument/result
    /// conversion of the EA binder template the exe registers it with (e.g. 0x61A5F0 = void(int, GRuntimeInstance*)):
    ///   v void, i int, f float, b bool, s const char*, I GRuntimeInstance* (gameplay instance userdata),
    ///   S ISimable* (racer/vehicle userdata). "b(I)" = returns bool, takes one instance. RaceStatus methods drop their
    ///   self (the GRaceStatus object); ISimable methods take theirs as the first argument.
    /// Kind: Query (reads game state; answered from the live game in every mode), Action (changes the game: recorded in
    /// Shadow mode, executed in Drive mode), Core (implemented by the stategraph runtime itself).
    /// </summary>
    public sealed class LuaBinding
    {
        public enum Kinds { Query, Action, Core }
        public readonly string Table, Name, Sig;
        public readonly uint Address;
        public readonly Kinds Kind;
        public readonly bool Method;          // GRaceStatus / ISimable methods called with ':' (self first)
        public string FullName => Table + (Method ? ":" : ".") + Name;
        public char Result => Sig[0];
        public string Args => Sig.Length > 3 ? Sig.Substring(2, Sig.Length - 3) : "";
        public LuaBinding(string table, string name, uint addr, string sig, Kinds kind, bool method = false)
        {
            Table = table; Name = name; Address = addr; Sig = sig; Kind = kind; Method = method;
        }
        public override string ToString() => $"{FullName} {Sig} @0x{Address:X6}";
    }

    /// <summary>
    /// Every binding of PC v1.3, its exe address and binder signature (BindToGameCode 0x61E750; addresses from the IDA
    /// symbol export of this exe). Table membership follows the registration order and the strings at 0x8A4980..0x8A539C;
    /// the scripts reference the ones marked used (the `lua scan` harness probe lists them).
    /// </summary>
    public static class LuaBindingTable
    {
        const LuaBinding.Kinds Q = LuaBinding.Kinds.Query, A = LuaBinding.Kinds.Action, C = LuaBinding.Kinds.Core;

        public static readonly LuaBinding[] All =
        {
            // Table / Activity / Audio / MiniMap / Bin / Math / Camera / NIS / Movie / Debug / Demo / HUD
            new LuaBinding("Table", "Randomize", 0x618AC0, "v(T)", C),                     // BindRawFunction: shuffles a Lua table in place
            new LuaBinding("Activity", "Run", 0x6048A0, "v(I)", C),                        // BlockUntilLoaded, GActivity::Reset, GActivity::Run
            new LuaBinding("Activity", "Suspend", 0x6048D0, "v(I)", C),                    // GActivity::Suspend 0x5F4730
            new LuaBinding("Audio", "SetFlag", 0x6048E0, "v(sb)", A),
            new LuaBinding("Audio", "IsCopSpeechPlaying", 0x604A00, "b()", Q),
            new LuaBinding("MiniMap", "AddEngagedRace", 0x604B50, "v(I)", A),
            new LuaBinding("Bin", "GetNumChallengesPassed", 0x604A10, "i(i)", Q),
            new LuaBinding("Bin", "GetNumRacesWon", 0x604A30, "i(i)", Q),
            new LuaBinding("Math", "RandomInt", 0x604CB0, "i(i)", C),                      // bRandom(n): 0..n-1
            new LuaBinding("Camera", "SetGenericCamera", 0x604A50, "v(ss)", A),
            new LuaBinding("NIS", "Play", 0x604B80, "v(Ississ)", A),
            new LuaBinding("Movie", "PlayHackE3FMV", 0x604C60, "v()", A),                  // empty on PC (ret)
            new LuaBinding("Debug", "Print", 0x604C70, "v(s)", C),
            new LuaBinding("Debug", "PrintInstance", 0x604C80, "v(I)", C),
            new LuaBinding("Debug", "ShowScreenMessage", 0x6121C0, "v(sf)", A),
            new LuaBinding("Debug", "Assert", 0x605410, "v(bs)", C),                       // empty in release (ret)
            new LuaBinding("Demo", "SetRaceCompleteForFE", 0x604C90, "v(I)", A),           // empty (ret)
            new LuaBinding("Demo", "StorePursuitRepForFE", 0x604CA0, "v(I)", A),           // empty (ret)
            new LuaBinding("HUD", "ShowMessage", 0x611320, "v(s)", A),
            new LuaBinding("HUD", "ShowTimeExtension", 0x6113E0, "v(f)", A),
            new LuaBinding("Platform", "IsNextGen", 0x604CC0, "b()", Q),                   // returns false on PC

            // Game
            new LuaBinding("Game", "SabotageEngine", 0x60AB60, "v(Sf)", A),
            new LuaBinding("Game", "BlowEngine", 0x60AB30, "v(S)", A),
            new LuaBinding("Game", "DetachCameraFromRacer", 0x611540, "v(S)", A),
            new LuaBinding("Game", "ChallengeComplete", 0x60E200, "v(S)", A),
            new LuaBinding("Game", "ClearAIControl", 0x612420, "v(i)", A),
            new LuaBinding("Game", "ForceAIControl", 0x6123B0, "v(i)", A),
            new LuaBinding("Game", "InitRacers", 0x61DBE0, "v(I)", A),
            new LuaBinding("Game", "WarpPlayerToTrigger", 0x611570, "v(I)", A),
            new LuaBinding("Game", "ResetTrigger", 0x604D10, "v(I)", A),
            new LuaBinding("Game", "SetPlayerStartPosition", 0x611670, "v(I)", A),
            new LuaBinding("Game", "SetRacerGoal", 0x611700, "v(iI)", A),
            new LuaBinding("Game", "SetRacerLapsLeft", 0x604DB0, "v(ii)", A),
            new LuaBinding("Game", "KnockoutRacer", 0x611440, "v(S)", A),
            new LuaBinding("Game", "TotalRacer", 0x611440, "v(S)", A),                     // same function as KnockoutRacer
            new LuaBinding("Game", "NotifyRacePlacement", 0x60AA00, "v(ISi)", C),          // posts MNotifyRacePlacement
            new LuaBinding("Game", "NotifySpeedTrapTriggered", 0x60A7B0, "v(IISf)", A),
            new LuaBinding("Game", "NotifyCheckpointReached", 0x6117F0, "v(Si)", A),
            new LuaBinding("Game", "NotifyLapFinished", 0x611850, "v(Si)", A),
            new LuaBinding("Game", "NotifyRaceFinished", 0x6119F0, "v(S)", A),
            new LuaBinding("Game", "SetRaceActivity", 0x604ED0, "v(I)", A),
            new LuaBinding("Game", "StartRace", 0x60DBD0, "v(I)", A),
            new LuaBinding("Game", "StartRaceTimers", 0x604EF0, "v()", A),
            new LuaBinding("Game", "SaveStartPositions", 0x604E90, "v()", A),
            new LuaBinding("Game", "RestoreStartPositions", 0x60AA50, "v()", A),
            new LuaBinding("Game", "EnterPostRaceFlow", 0x611F20, "v()", A),
            new LuaBinding("Game", "ShowOnlinePostRaceScreen", 0x605690, "b()", A),
            new LuaBinding("Game", "SetCopsEnabled", 0x604F40, "v(b)", A),
            new LuaBinding("Game", "NoNewPursuitsOrCops", 0x60AAB0, "v()", A),
            new LuaBinding("Game", "ForcePursuitStart", 0x60AAC0, "v(i)", A),
            new LuaBinding("Game", "EnterEngagableTrigger", 0x604F80, "v(I)", A),
            new LuaBinding("Game", "ExitEngagableTrigger", 0x604FD0, "v(I)", A),
            new LuaBinding("Game", "EnterGateZone", 0x611FB0, "v(Is)", A),
            new LuaBinding("Game", "ExitGateZone", 0x611FF0, "v(Is)", A),
            new LuaBinding("Game", "ShowRaceOverSummary", 0x6120C0, "v()", A),
            new LuaBinding("Game", "HideRaceOverSummary", 0x605020, "v()", A),             // empty (ret)
            new LuaBinding("Game", "SetTrafficSpeed", 0x605090, "v(Iff)", A),
            new LuaBinding("Game", "SpawnCop", 0x60A670, "v(Isbb)", A),
            new LuaBinding("Game", "SpawnCharacter", 0x604D40, "v(IIIf)", A),
            new LuaBinding("Game", "UnspawnCharacter", 0x604DA0, "v(I)", A),
            new LuaBinding("Game", "SendCharacterStimulus", 0x60A6E0, "v(Is)", A),
            new LuaBinding("Game", "JackKnife", 0x605030, "v(I)", A),
            new LuaBinding("Game", "SetAllStaging", 0x60DF70, "v(b)", A),
            new LuaBinding("Game", "ShowPauseMenu", 0x6050F0, "v()", A),
            new LuaBinding("Game", "AwardCash", 0x612130, "v(Sf)", A),                     // = AwardPoints 0x60E030
            new LuaBinding("Game", "AwardPoints", 0x60E030, "v(Sf)", A),
            new LuaBinding("Game", "ChallengeCompleted", 0x605140, "v()", A),
            new LuaBinding("Game", "UnlockRace", 0x612140, "v(I)", A),
            new LuaBinding("Game", "NotifyCountdownDone", 0x60E160, "v()", A),
            new LuaBinding("Game", "ResetCopsForRestart", 0x60AB10, "v()", A),
            new LuaBinding("Game", "ShowTriggerIcon", 0x604D20, "v(I)", A),
            new LuaBinding("Game", "HideTriggerIcon", 0x604D30, "v(I)", A),
            new LuaBinding("Game", "WarpToMarkerWhenRoaming", 0x605930, "v(I)", A),
            new LuaBinding("Game", "DoSpecialFinalization", 0x605890, "v(I)", A),
            new LuaBinding("Game", "IsActiveSpeedTrap", 0x611E80, "b(I)", Q),
            new LuaBinding("Game", "IsActiveMenuGate", 0x604E40, "b(I)", Q),
            new LuaBinding("Game", "GetSimTime", 0x604CD0, "f()", C),
            new LuaBinding("Game", "GetRacerIndex", 0x60DAE0, "i(S)", Q),
            new LuaBinding("Game", "GetRacerElement", 0x60DB60, "S(i)", Q),
            new LuaBinding("Game", "GetPlayerElement", 0x612370, "S(i)", Q),
            new LuaBinding("Game", "GetRacerCharacter", 0x604DE0, "I(i)", Q),
            new LuaBinding("Game", "GetNumRacers", 0x604E00, "i()", Q),
            new LuaBinding("Game", "GetSimableSpeedKmh", 0x604E10, "f(S)", Q),
            new LuaBinding("Game", "RacerIsHuman", 0x611E20, "b(i)", Q),
            new LuaBinding("Game", "PlayerIsLocal", 0x60A760, "b(S)", Q),
            new LuaBinding("Game", "IsRaceCompleted", 0x6051B0, "b(I)", Q),
            new LuaBinding("Game", "IsRaceUnlocked", 0x605170, "b(I)", Q),
            new LuaBinding("Game", "IsOnlineGame", 0x605460, "b()", Q),
            new LuaBinding("Game", "IsLANGame", 0x605480, "b()", Q),
            new LuaBinding("Game", "AllRacersDone", 0x6051F0, "b()", Q),
            new LuaBinding("Game", "AllHumanPlayersDone", 0x60E0A0, "b()", Q),
            new LuaBinding("Game", "SimableDistance", 0x60AB90, "f(SI)", Q),
            new LuaBinding("Game", "SimableAngle", 0x6053B0, "f(SI)", Q),
            new LuaBinding("Game", "AllowEngageEvents", 0x605640, "b()", Q),
            new LuaBinding("Game", "AllowMenuGates", 0x605650, "b()", Q),
            new LuaBinding("Game", "AllowEngageSafehouse", 0x605660, "b()", Q),
            new LuaBinding("Game", "DoSpecialSetup", 0x605830, "b(I)", A),                // true = the world must reload first
            new LuaBinding("Game", "GetPlayerBounty", 0x612300, "i()", Q),
            new LuaBinding("Game", "AwardPlayerBounty", 0x612220, "v(i)", A),
            new LuaBinding("Game", "JumpToCarLot", 0x605250, "v()", A),
            new LuaBinding("Game", "AbandonRace", 0x60DEB0, "v()", A),
            new LuaBinding("Game", "JumpToSafeHouse", 0x6052B0, "v()", A),
            new LuaBinding("Game", "SetTimer", 0x605300, "v(sf)", C),                     // GManager::SetTimer 0x5E4A40
            new LuaBinding("Game", "KillTimer", 0x605320, "v(s)", C),                     // GManager::KillTimer 0x5E4AD0
            new LuaBinding("Game", "ShowGPS", 0x605340, "v(b)", A),
            new LuaBinding("Game", "NavigatePlayerTo", 0x605360, "v(IIf)", A),
            new LuaBinding("Game", "NotifyActivityFinsihed", 0x605420, "v(I)", C),         // (sic) posts MNotifyFinished{Sender}
            new LuaBinding("Game", "SkipCareerIntro", 0x612490, "b()", Q),
            new LuaBinding("Game", "SetChanceOfRain", 0x6054A0, "v(f)", A),
            new LuaBinding("Game", "Fade", 0x605500, "v()", A),                            // Game_DoFade
            new LuaBinding("Game", "IntroduceRival", 0x605550, "v()", A),                  // empty on PC (ret)
            new LuaBinding("Game", "PlayTutorial", 0x6124E0, "v()", A),
            new LuaBinding("Game", "DoSafeHouseIntro", 0x605560, "v(I)", A),
            new LuaBinding("Game", "DoZoneMenuAction", 0x612030, "v(I)", A),
            new LuaBinding("Game", "SetCurrentTimeOfDay", 0x605780, "b(I)", A),            // binder 0x61A9F0 (symbol says (float)) [?]
            new LuaBinding("Game", "ReloadWorld", 0x6057E0, "v(I)", A),                    // binder 0x61A270 (symbol says ()) [?]
            new LuaBinding("Game", "IsCareerMode", 0x605610, "b()", Q),
            new LuaBinding("Game", "IsSplitScreen", 0x605630, "b()", Q),
            new LuaBinding("Game", "SetHasRapSheet", 0x605670, "v()", A),
            new LuaBinding("Game", "SetWorldHeat", 0x612660, "v(f)", A),
            new LuaBinding("Game", "ShowWinningPostRaceScreen", 0x60E240, "v()", A),
            new LuaBinding("Game", "ShowLosingPostRaceScreen", 0x60E230, "v()", A),
            new LuaBinding("Game", "PreventPlayerBeingBusted", 0x60AE00, "v()", A),
            new LuaBinding("Game", "CalculateRanking", 0x6058B0, "i(Si)", Q),

            // RaceStatus = the GRaceStatus object (FUN_0061ADF0(GRaceStatus::fObj, "GRaceStatus")), called as methods
            new LuaBinding("RaceStatus", "ClearRacers", 0x5E30A0, "v()", A, true),
            new LuaBinding("RaceStatus", "AddRacer", 0x601F90, "v(I)", A, true),
            new LuaBinding("RaceStatus", "AddAvailableEventToMap", 0x5DBF20, "v(II)", A, true),   // empty on PC
            new LuaBinding("RaceStatus", "AddSpeedTrapToMap", 0x5DBF30, "v(I)", A, true),         // empty on PC
            new LuaBinding("RaceStatus", "ClearCheckpoints", 0x5EEB40, "v()", A, true),
            new LuaBinding("RaceStatus", "AddCheckpoint", 0x5F5C20, "v(I)", A, true),
            new LuaBinding("RaceStatus", "SetNextCheckpointPos", 0x5FE660, "v(I)", A, true),
            new LuaBinding("RaceStatus", "SetRoaming", 0x5FE3E0, "v()", A, true),
            new LuaBinding("RaceStatus", "SetIsLoading", 0x5DB3E0, "v(b)", A, true),
            new LuaBinding("RaceStatus", "EnterSuddenDeath", 0x604030, "v()", A, true),
            new LuaBinding("RaceStatus", "AwardBonusTime", 0x5FE600, "v(f)", A, true),
            new LuaBinding("RaceStatus", "SetActivelyRacing", 0x604050, "v(b)", A, true),
            new LuaBinding("RaceStatus", "SetTaskTime", 0x604040, "v(f)", A, true),
            new LuaBinding("RaceStatus", "DisableBarriers", 0x5DBF00, "v()", A, true),
            new LuaBinding("RaceStatus", "EnableBinBarriers", 0x5E3020, "v()", A, true),
            new LuaBinding("RaceStatus", "NotifyScriptWhenLoaded", 0x5EEA60, "v()", A, true),
            new LuaBinding("RaceStatus", "SetHasBeenWon", 0x604080, "v(b)", A, true),

            // ISimable methods (FUN_0061B0B0("IsPlayer", ..))
            new LuaBinding("ISimable", "IsPlayer", 0x609F10, "b(S)", Q, true),      // self = the simable (ISimable::IsPlayer)
        };

        static Dictionary<string, LuaBinding> _byName;
        public static LuaBinding Find(string fullName)
        {
            if (_byName == null)
            {
                _byName = new Dictionary<string, LuaBinding>();
                foreach (var b in All) _byName[b.Table + "." + b.Name] = b;
            }
            return _byName.TryGetValue(fullName.Replace(':', '.'), out var r) ? r : null;
        }
    }
}
