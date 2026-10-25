using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// PursuitData (FEPKg_PostRace.hpp; PopulateData 0x56A440, ClearData 0x56A540): one finished pursuit as the results
    /// screens read it — length, cops damaged / destroyed, spike strips and roadblocks dodged, cost to state, the pending
    /// bounty (normal and from cop destruction) and the milestones completed during it.
    /// </summary>
    public sealed class PursuitData
    {
        public float Length;
        public int CopsDamaged, CopsDestroyed, SpikesDodged, RoadblocksDodged, CostToState, RepNormal, RepCopDestruction;
        public bool ExitToSafehouse;
        public readonly List<Milestone> Milestones = new List<Milestone>();
        public PursuitStats Stats;

        public static PursuitData From(PursuitStats s, int bounty, List<Milestone> done)
        {
            var d = new PursuitData { Stats = s };
            if (s != null)
            {
                d.Length = s.Length; d.CopsDamaged = s.CopsDamaged; d.CopsDestroyed = s.CopsDestroyed;
                d.SpikesDodged = s.SpikesDodged; d.RoadblocksDodged = s.RoadblocksDodged; d.CostToState = s.CostToState;
            }
            // GetPendingRepPointsNormal / FromCopDestruction [?]: the pursuit system keeps one bounty; all of it is "normal"
            d.RepNormal = bounty;
            if (done != null) d.Milestones.AddRange(done.Take(32));
            return d;
        }
    }

    /// <summary>
    /// PostRace_MilestoneRewards.fng — PostRaceMilestonesScreen (FEPKg_PostRace.cpp:1230-1405; PC ctor 0x58E490,
    /// NotificationMessage 0x5A7D20, StartBountyAnimations 0x58E520, StartMilestoneAnimations 0x5A28E0,
    /// StartAnimations 0x57D750). EShowMilestones (0x641FE0) pushes it after a pursuit. The big icon (0x14564FB9)
    /// APPEARs with each award; when APPEAR has run it plays the award's POSn script, which flies it into its slot and
    /// sends 0x0D3C3DE7 to start the next one: the pursuit bounty, the cop-destruction bounty (if any cop was destroyed),
    /// then each milestone done in the pursuit; at the end the summary groups move (POS2). Accept → the pursuit table.
    /// </summary>
    public sealed class PostRaceMilestonesScreen : FEMenuScreen
    {
        const uint BigIcon = 0x14564FB9, Desc = 0xE526D0D2, Earned = 0xE1045A4F, Total = 0x324F7792, NextAward = 0x0D3C3DE7;
        readonly PursuitData _data;
        float _bounty;
        bool _copBountyShown;
        int _index = -1;
        uint _script;

        public override string Package => "PostRace_MilestoneRewards";
        public PostRaceMilestonesScreen(PursuitData data) { _data = data; }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.InitComplete:
                    StartBountyAnimations(false);
                    break;
                case NextAward:
                    if (!_copBountyShown)
                    {
                        _copBountyShown = true;
                        if (_data.CopsDestroyed > 0) { StartBountyAnimations(true); return; }
                    }
                    // a pursuit race that is not the final chase shows its one challenge (:1259), else the bin's milestones
                    if (PursuitResultScreen.ChallengeRace(out _)) { StartChallengeAnimations(); break; }
                    StartMilestoneAnimations();
                    break;
                case FEHash.ScreenTick:
                    var icon = Obj(BigIcon);
                    if (IsScriptSet(icon, FEHash.Appear) && !View.IsScriptRunning(icon, FEHash.Appear)) SetScript(icon, _script);
                    break;
                case FEHash.PadAccept:
                    // QueuePackagePop(1); EShowResults(FERESULTTYPE_PURSUIT): the stack never empties in between
                    FE.Replace(new PursuitResultScreen(_data));
                    break;
            }
        }

        void StartMilestoneAnimations()
        {
            _index++;
            if (_index < _data.Milestones.Count)
            {
                var m = _data.Milestones[_index];
                int tag = MilestonesScreen.Tag(m.Type);
                string header = tag > 0 ? CareerScreenKit.L($"BLACKLIST_PURSUIT_MILESTONES_{tag:00}_SHORT", m.Title) : m.Title;
                StartAnimations(true, AttribDatabase.Hash(m.Type), m.Bounty, $"{header}: {MilestoneDescription(m.Type, m.Goal, m.Goal, false)}");
                return;
            }
            StartMilestoneDoneAnimations();
        }

        /// <summary>
        /// StartChallengeAnimations (:1298): once, the pursuit race's challenge — GManager::GetBestValue(type) against the
        /// goal, headed by the CHALLENGE_SERIES header of the race's LocalizationTag, worth the race's Reputation.
        /// </summary>
        bool StartChallengeAnimations()
        {
            _index++;
            if (_index < 1 && PursuitResultScreen.ChallengeRace(out var ev))
            {
                float cur = _data.Stats != null ? _data.Stats.Value(ev.MilestoneType) : 0, goal = ev.Threshold;
                string desc = MilestoneDescription(ev.MilestoneType, cur, goal, false);
                StartAnimations(true, AttribDatabase.Hash(ev.MilestoneType ?? ""), ev.Reputation, $"{PursuitResultScreen.ChallengeHeader(ev)}: {desc}");
                return true;
            }
            StartMilestoneDoneAnimations();
            return false;
        }

        void StartBountyAnimations(bool copDestruction)
        {
            if (!copDestruction) StartAnimations(false, 0x033FA23A, _data.RepNormal, Lang(0x4D64888D));
            else StartAnimations(false, 0x4FC942CA, _data.RepCopDestruction, $"{Lang(0x23F6E732)}: {_data.CopsDestroyed.Grouped()}");
        }

        /// <summary>StartAnimations (0x57D750): icon texture, description, "+bounty" line (hidden at 0) and the running total.</summary>
        void StartAnimations(bool isMilestone, uint typeKey, float bounty, string description)
        {
            _bounty += bounty;
            _script = AnimationScript(isMilestone, typeKey);
            SetTextureHash(BigIcon, IconHash(typeKey));
            Printf(Desc, description);
            if (bounty > 0) SetVisible(Earned); else SetInvisible(Earned);
            Printf(Earned, $"{Lang(0x29B1B96A)}: {bounty.Grouped()}");
            Printf(Total, $"{Lang(0x5CCF949A)}: {_bounty.Grouped()}");
            SetScript(BigIcon, FEHash.Appear);
        }

        void StartMilestoneDoneAnimations()
        {
            SetScript(BigIcon, FEHash.Hide);
            SetScript(Desc, FEHash.Leave);
            SetScript(Earned, FEHash.Leave);
            SetScript(0x962B9C62, FrontEndData.Hash("POS2"));
            SetScript(0xEC85C7E4, FrontEndData.Hash("POS2"));
        }

        /// <summary>SetMilestoneAnimationScriptHash (0x56A670).</summary>
        static uint AnimationScript(bool isMilestone, uint type) => type switch
        {
            0x2377E50D => FrontEndData.Hash("POS1"),
            0x033FA23A => FrontEndData.Hash(isMilestone ? "POS7" : "POS0"),
            0xA61CAC24 => FrontEndData.Hash("POS2"),
            0xFD989A3A => FrontEndData.Hash("POS3"),
            0xEB45F99D => FrontEndData.Hash("POS4"),
            0xCDF36FC2 => FrontEndData.Hash("POS5"),
            0x850A64BC => FrontEndData.Hash("POS6"),
            0x4FC942CA => FrontEndData.Hash("POS00"),
            0x5392E4FD => FrontEndData.Hash("POS8"),
            _ => 0,
        };

        /// <summary>cFrontendDatabase::GetMilestoneIconHash (FEDatabase.cpp:1154) by the attribute key of the type.</summary>
        public static uint IconHash(uint k)
        {
            switch (k)
            {
                case 0x850A64BC: return 0x88E8DE9E;
                case 0x3FD1884D: case 0x4FC942CA: return 0x0FE608E6;
                case 0xFD989A3A: return 0x87807869;
                case 0x5392E4FD: return 0x831B7EBE;
                case 0x033FA23A: return 0x950FCEBC;
                case 0xEB45F99D: return 0xC43959D2;
                case 0x9E3EBB78: return 0x3FFE9EC9;
                case 0xCDF36FC2: return 0xE621B2EF;
                case 0xA61CAC24: return 0x6784A80E;
                case 0x2377E50D: return 0xB4E6456B;
            }
            return 0x950FCEBC;
        }

        /// <summary>
        /// cFrontendDatabase::SetMilestoneDescriptionString (FEDatabase.cpp:1218): time types (0x033FA23A, 0x5392E4FD) print
        /// Timer strings (flags 4), others "%$0.0f"; showCurrVal → "cur/goal" with cur clamped to the goal.
        /// </summary>
        public static string MilestoneDescription(string type, float cur, float goal, bool showCur)
        {
            if (showCur && cur > goal) cur = goal;
            uint k = type == null ? 0xFFFFFFFF : AttribDatabase.Hash(type);
            if (k == 0x033FA23A || k == 0x5392E4FD)
                return showCur ? $"{FETimer.Print(cur, FETimer.ShowUnused)}/{FETimer.Print(goal, FETimer.ShowUnused)}" : FETimer.Print(goal, FETimer.ShowUnused);
            return showCur ? $"{cur.Grouped()}/{goal.Grouped()}" : goal.Grouped();
        }
    }

    /// <summary>
    /// PostRace_Pursuit.fng — PostRacePursuitScreen (FEPKg_PostRace.cpp:1000-1228; PC ctor 0x5A9430, Initialize 0x5A7910,
    /// SetupPursuit 0x5A2610, SetupInfractions 0x5A2050, SetupMilestones 0x5A2390, NotificationMessage 0x5A7AE0). An
    /// ArrayScrollerMenu of 1×9 LINEn_GROUP slots (COLUMN1_DATAn name, COLUMN2_DATAn value, CHECKn / EMPTYn). Pages:
    /// PURSUIT (duration, damaged / immobilized police vehicles, spike strips, roadblocks, cost to state, unserved
    /// infractions), INFRACTIONS (eight check rows) and MILESTONES (this rival's milestones, ticked when done in this
    /// pursuit, greyed when already awarded). PAD_BUTTON4 (key 2) cycles PURSUIT → INFRACTIONS → MILESTONES; accept
    /// unpauses. Kept as PursuitResultScreen: the pursuit system pushes it with the stats, and it opens with
    /// PostRace_MilestoneRewards as EShowMilestones does.
    /// </summary>
    public sealed class PursuitResultScreen : FEMenuScreen
    {
        enum Mode { Pursuit, Infractions, Milestones }
        enum Kind { Number, Time, MilestoneNumber, MilestoneTime, MilestoneTimeRemaining, Check }
        enum CheckType { Off, On, Greyed }

        sealed class Datum
        {
            public Kind Type;
            public uint Name;
            public string NameText;
            public float Number, Goal;
            public CheckType Checked;
        }

        const uint RaceButton = 0x5CED1D04, Title = 0x2D691760;
        readonly PursuitData _data;
        readonly bool _rewardsDone;
        Mode _mode = Mode.Pursuit;
        readonly List<Datum> _rows = new List<Datum>();

        public override string Package => "PostRace_Pursuit";

        /// <summary>The pursuit system's call: shows PostRace_MilestoneRewards first (EShowMilestones), then this table.</summary>
        public PursuitResultScreen(PursuitStats s, int bounty, List<Milestone> done) : this(PursuitData.From(s, bounty, done), false) { }

        public PursuitResultScreen(PursuitData data) : this(data, true) { }

        PursuitResultScreen(PursuitData data, bool rewardsDone) { _data = data; _rewardsDone = rewardsDone; }

        protected override void Setup()
        {
            if (!_rewardsDone && FrontEndData.Packages.ContainsKey("PostRace_MilestoneRewards"))
            {
                View.gameObject.SetActive(false);
                var fe = FE;
                var data = _data;
                fe.Defer(() => { if (fe.Top == this) fe.Replace(new PostRaceMilestonesScreen(data)); });
                return;
            }
            Initialize();
        }

        /// <summary>
        /// GRaceParameters::GetIsPursuitRace &amp;&amp; !cFrontendDatabase::IsFinalEpicChase: the active race is a pursuit
        /// (Challenge Series) race other than the story's final chase (RaceIndex.FinalEpicChase 1.8.1).
        /// </summary>
        internal static bool ChallengeRace(out RaceEvent ev)
        {
            ev = RaceManager.Active?.Event;
            return ev != null && (ev.PursuitRace || ev.Type == RaceType.Pursuit) && ev.EventId != RaceIndex.FinalEpicChase;
        }

        /// <summary>cFrontendDatabase::GetChallengeHeaderHash(LocalizationTag): CHALLENGE_SERIES_DESC_%02d_HEADER.</summary>
        internal static string ChallengeHeader(RaceEvent ev)
        {
            int tag = ChallengeSeriesScreen.LocalizationTag(ev);
            return CareerScreenKit.L($"CHALLENGE_SERIES_DESC_{tag:00}_HEADER", ev.Name ?? "");
        }

        bool PursuitRace => ChallengeRace(out _);
        /// <summary>GameFlowManager::IsInFrontend: the table shown in the front end after a Challenge Series pursuit.</summary>
        static bool InFrontEnd => GameFlow.IsInFrontEnd;

        void Initialize()
        {
            _rows.Clear();
            if (_mode == Mode.Infractions)
            {
                SetLanguageHash(Title, 0xFB415E78);
                // :1035: in the front end "Done" (0x7448870B); in game 0x74413352, or 0x9145A5F2 in a pursuit race
                SetLanguageHash(RaceButton, InFrontEnd ? 0x7448870Bu : PursuitRace ? 0x9145A5F2u : 0x74413352u);
                SetupInfractions();
            }
            else if (_mode == Mode.Milestones)
            {
                SetLanguageHash(Title, PursuitRace ? 0x334FA7FBu : 0x578B767Bu);
                SetLanguageHash(RaceButton, 0x7448870B);
                SetupMilestones();
            }
            else
            {
                SetLanguageHash(Title, 0xFEA872D4);
                SetLanguageHash(RaceButton, 0xAEAEB62F);
                SetupPursuit();
            }
            RefreshHeader();
        }

        /// <summary>SetupInfractions (0x5A2050): GInfractionManager::DidInfractionOccur per type, in the enum order.</summary>
        void SetupInfractions()
        {
            uint[] labels = { 0x4B0FF103, 0x8EC1A6EC, 0x370F331D, 0x8462A784, 0xDFF254BA, 0x7DBD5B34, 0x2B1DE2A9, 0xB0EF5C12 };
            var occurred = new bool[labels.Length];
            if (_data.Stats != null)
                foreach (var name in _data.Stats.Infractions)
                {
                    int t = RapSheetRecord.TypeOf(name);
                    if (t >= 0 && t < occurred.Length) occurred[t] = true;
                }
            for (int i = 0; i < labels.Length; i++)
                _rows.Add(new Datum { Type = Kind.Check, Name = labels[i], Checked = occurred[i] ? CheckType.On : CheckType.Off });
        }

        /// <summary>
        /// SetupMilestones (0x5A2390): in a pursuit race one row for its challenge (best value vs goal, ticked when met);
        /// otherwise the current bin's milestones (GManager::GetFirstMilestone(false, bin)).
        /// </summary>
        void SetupMilestones()
        {
            if (ChallengeRace(out var ev))
            {
                uint k = AttribDatabase.Hash(ev.MilestoneType ?? "");
                var kind = k == 0x5392E4FD ? Kind.Time : k == 0x033FA23A ? Kind.MilestoneTime : Kind.MilestoneNumber;
                float cur = _data.Stats != null ? _data.Stats.Value(ev.MilestoneType) : 0, goal = ev.Threshold;
                _rows.Add(new Datum { Type = kind, NameText = ChallengeHeader(ev), Number = cur, Goal = goal, Checked = cur >= goal ? CheckType.On : CheckType.Off });
                return;
            }
            var bin = GameSession.Mode == GameMode.Career ? GameSession.CurrentBin : null;
            var p = GameSession.Profile;
            if (bin == null) return;
            foreach (var m in bin.Milestones)
            {
                bool time = AttribDatabase.Hash(m.Type) == 0x033FA23A || AttribDatabase.Hash(m.Type) == 0x5392E4FD;
                var kind = AttribDatabase.Hash(m.Type) == 0x5392E4FD ? Kind.Time : time ? Kind.MilestoneTime : Kind.MilestoneNumber;
                var check = _data.Milestones.Contains(m) ? CheckType.On : p != null && p.Milestones.Contains(m.Name) ? CheckType.Greyed : CheckType.Off;
                int tag = MilestonesScreen.Tag(m.Type);
                float cur = _data.Stats != null ? _data.Stats.Value(m.Type) : 0;
                _rows.Add(new Datum
                {
                    Type = kind, NameText = tag > 0 ? CareerScreenKit.L($"BLACKLIST_PURSUIT_MILESTONES_{tag:00}_SHORT", m.Title) : m.Title,
                    Number = cur, Goal = m.Goal, Checked = check,
                });
            }
        }

        /// <summary>SetupPursuit (0x5A2610).</summary>
        void SetupPursuit()
        {
            _rows.Add(new Datum { Type = Kind.Time, Name = 0x4D64888D, Number = _data.Length });
            _rows.Add(new Datum { Type = Kind.Number, Name = 0xA999F6E2, Number = _data.CopsDamaged });
            _rows.Add(new Datum { Type = Kind.Number, Name = 0x23F6E732, Number = _data.CopsDestroyed });
            _rows.Add(new Datum { Type = Kind.Number, Name = 0x0291C816, Number = _data.SpikesDodged });
            _rows.Add(new Datum { Type = Kind.Number, Name = 0x29DABA15, Number = _data.RoadblocksDodged });
            _rows.Add(new Datum { Type = Kind.Number, Name = 0xD9BB7D2D, Number = _data.CostToState });
            _rows.Add(new Datum { Type = Kind.Number, Name = 0xB7DFFF96, Number = _data.Stats?.Infractions.Count ?? 0 });
        }

        /// <summary>ArrayScroller::RefreshHeader + PursuitResultsArraySlot::Update (FEPKg_PostRace.cpp:885).</summary>
        void RefreshHeader()
        {
            for (int i = 0; i < 9; i++)
            {
                var line = Obj($"LINE{i + 1}_GROUP");
                var name = Obj($"COLUMN1_DATA{i + 1}");
                var number = Obj($"COLUMN2_DATA{i + 1}");
                var check = Obj($"CHECK{i + 1}");
                var empty = Obj($"EMPTY{i + 1}");
                var d = i < _rows.Count ? _rows[i] : null;
                if (d == null) { View.SetInvisible(line); continue; }
                // ArraySlot::Update: visible, INIT unless already set (the menu is not selectable [?])
                View.SetVisibleTree(line);
                if (!IsScriptSet(line, FEHash.Init)) SetScript(line, FEHash.Init);
                SetScript(check, FEHash.Hide);
                SetScript(empty, FEHash.Hide);
                SetScript(line, FEHash.Init);
                if (name != null)
                {
                    SetScript(name, FEHash.Init);
                    Printf(name, d.NameText ?? Lang(d.Name));
                }
                switch (d.Type)
                {
                    case Kind.Number:
                        Printf(number, ((int)Mathf.Max(0, d.Number)).Grouped());
                        SetScript(number, FEHash.Init);
                        break;
                    case Kind.Time:
                        Printf(number, FETimer.Print(Mathf.Max(0, d.Number)));
                        SetScript(number, FEHash.Init);
                        break;
                    case Kind.MilestoneNumber:
                        // SetMilestoneDescriptionString(out, -1, cur, goal, false): the goal only [decomp; type -1 → "%$0.0f"]
                        Printf(number, PostRaceMilestonesScreen.MilestoneDescription(null, d.Number, d.Goal, false));
                        SetScript(number, FEHash.Init);
                        break;
                    case Kind.MilestoneTime: case Kind.MilestoneTimeRemaining:
                        Printf(number, PostRaceMilestonesScreen.MilestoneDescription(null, d.Number, d.Goal, true));
                        SetScript(number, FEHash.Init);
                        break;
                    case Kind.Check:
                        // [?] the decomp (UNSOLVED) sets GREY in both branches; a committed infraction shows the tick here
                        SetScript(number, FEHash.Hide);
                        SetScript(check, d.Checked == CheckType.On ? FEHash.Show : FEHash.Grey);
                        break;
                }
                if (d.Type != Kind.Check)
                {
                    if (d.Checked == CheckType.On) SetScript(check, FEHash.Show);
                    else if (d.Checked == CheckType.Greyed) SetScript(check, FEHash.Grey);
                    else SetScript(check, FEHash.Hide);
                }
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.PadAccept:
                    if (InFrontEnd)
                    {
                        // :1196: QueuePackageSwitch to ChallengeSeries.fng in quick-race mode, else Car_Select.fng
                        if (GameSession.Mode != GameMode.Career) FE.Replace(new ChallengeSeriesScreen());
                        else FE.Replace(new CarSelectScreen(CarSelectScreen.Mode.Career));
                        break;
                    }
                    var race = RaceManager.Active;
                    if (race != null && race.Event != null && (race.Event.PursuitRace || race.Event.Type == RaceType.Pursuit) && race.PlayerEntrant != null && race.PlayerEntrant.Finished)
                        GameFlow.QuitToMainMenu();   // EQuitToFE (challenge autosave not ported)
                    else FE.CloseAll();              // EUnPause
                    break;
                case FEHash.PadButton4:
                    // :1217: the front end only has PURSUIT ↔ INFRACTIONS; in game PURSUIT → INFRACTIONS → MILESTONES
                    if (InFrontEnd) _mode = _mode == Mode.Infractions ? Mode.Pursuit : Mode.Infractions;
                    else _mode = _mode == Mode.Infractions ? Mode.Milestones : _mode == Mode.Milestones ? Mode.Pursuit : Mode.Infractions;
                    Initialize();
                    break;
            }
        }
    }
}
