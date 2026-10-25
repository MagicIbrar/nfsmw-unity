using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using Entrant = NfsMw.Game.RaceManager.Entrant;

namespace NfsMw.UI
{
    /// <summary>
    /// PostRace_Results.fng — PostRaceResultsScreen (MenuScreens/InGame/FEPKg_PostRace.cpp; PC ctor 0x5A6EC0, Setup 0x5A19C0,
    /// SetupResults 0x5964A0, SetupRacerStats 0x5970E0, SetupLapStats 0x5972E0, NotificationMessage 0x5A7120).
    /// Three pages: RESULTS (standings: position / name / time, or speed in a speedtrap), STATS (the shown racer's numbers)
    /// and LAPSTATS (stages / laps / speedtraps / tollbooths). PAD_BUTTON4 (key 2) cycles the pages (tollbooth races
    /// toggle LAPSTATS ↔ STATS and start on LAPSTATS), the L / R triggers pick the racer on the STATS pages, PAD_BUTTON5
    /// (key 1) asks to restart (dialog 0x4D3399A8), accept leaves (the package's 0x001335F0 exit, then EXIT_COMPLETE).
    /// Shown by FE_ShowPostRaceScreen (0x60AC20) when the player did not win; a win shows InGamePhotoMaster first and
    /// this screen over InGameBackground when key 2 is pressed there.
    /// </summary>
    public sealed class PostRaceResultsScreen : FEMenuScreen
    {
        enum Mode { Results, Stats, LapStats, Count }

        const uint RaceButton = 0x5CED1D04, Title = 0x2D691760, Head1 = 0x586AB4A6, Head2 = 0x44AC8987, Head3 = 0x30EE5E68,
            NameGroup = 0x8A41F5B9, NameText = 0xEB43CCB0, Handler = 0x47FF4E7C, ButtonBar = 0x57EFB2FB, RestartGroup = 0x445A862B,
            MsgRestart = 0xE1A57D51, MsgRestartCancel = 0xB4623F67, MsgQuitToFreeRoam = 0x30ED2368;
        const int MaxSlotsLeftSide = 11;

        readonly RaceManager _race;
        readonly bool _won, _fromPhoto;
        readonly RaceEvent _event;
        readonly List<Entrant> _racers;
        readonly Entrant _player;
        readonly RaceType _type;
        int _winner = -1, _current = -1;
        Mode _mode = Mode.Results;

        public override string Package => "PostRace_Results";

        public PostRaceResultsScreen(RaceManager race, bool won) : this(race, won, false) { }

        /// <summary>fromPhoto: pushed by the photo-finish screen's PAD_BUTTON4 (EShowResults), over InGameBackground.</summary>
        public PostRaceResultsScreen(RaceManager race, bool won, bool fromPhoto)
        {
            _race = race;
            _won = won;
            _fromPhoto = fromPhoto;
            _event = race?.Event;
            _racers = race != null ? race.Entrants.ToList() : new List<Entrant>();
            _type = _event?.Type ?? RaceType.Sprint;
            if (_type == RaceType.Tollbooth) _mode = Mode.LapStats;
            for (int i = 0; i < _racers.Count; i++)
                if (_racers[i].Human && !_racers[i].Remote && _current == -1) { _player = _racers[i]; _current = i; break; }
            if (_current < 0) _current = 0;
        }

        /// <summary>FE_ShowPostRaceScreen (0x60AC20): a won race (not split screen, not LAN) goes to the photo finish first.</summary>
        bool RouteToPhoto => _won && !_fromPhoto && GameSession.Lan == null && !GameSession.SplitScreen && _event != null;

        protected override void Setup()
        {
            if (RouteToPhoto)
            {
                View.gameObject.SetActive(false);
                var fe = FE;
                var race = _race;
                fe.Defer(() => { if (fe.Top == this) fe.Replace(new PhotoFinishScreen(race, true)); });
                return;
            }
            SetupPages();
        }

        // ---- data (GRaceStatus / GRacerInfo over RaceManager) --------------------------------------------------

        static string NameOf(Entrant e) => e.Human && !e.Remote ? (GameSession.Profile?.Name ?? e.Name) : e.Name;
        bool Lan => GameSession.Lan != null;
        float RaceTimeElapsed => _race != null ? _race.RaceTime : 0;
        static string SpeedUnits => Lang(Metric ? 0x8569A25Fu : 0x8569AB44u);
        static float MpsToShown(float mps) => Metric ? mps * 3.6f : mps * 2.23699f;
        /// <summary>MPS2MPH(KPH2MPS(x)) when imperial: the point totals are km/h.</summary>
        static float KphToShown(float kph) => Metric ? kph : kph / 3.6f * 2.23699f;
        static string N0(float v) => v.Grouped();

        /// <summary>GRaceStatus::GetLapTime(lap, racer, false): that lap's own time (0 = not done).</summary>
        static float LapTime(Entrant e, int lap)
        {
            if (lap >= e.Laps.Count) return 0;
            return e.Laps[lap] - (lap > 0 ? e.Laps[lap - 1] : 0);
        }

        static int LapPosition(Entrant e, int lap) => lap < e.LapPlaces.Count ? e.LapPlaces[lap] : 0;

        // ---- StatsPanel (FEPKg_PostRace.cpp:40-90) ---------------------------------------------------------------

        int _widget;
        readonly List<Action> _draw = new List<Action>();
        string _racerName = "";

        void ResetPanel() { _draw.Clear(); _widget = 1; _racerName = ""; }

        FEView.Node Cur(string name) => View.Find(HashF(name + _widget));

        void AddStat(Action draw)
        {
            SetScript(HashF($"LINE{_widget}_GROUP"), FEHash.Init);
            _draw.Add(draw);
            _widget++;
        }

        void AddInfoStat(uint title, uint info)
        {
            var t = Cur("COLUMN1_DATA"); var d = Cur("COLUMN2_DATA");
            AddStat(() => { SetLanguageHash(t, title); SetLanguageHash(d, info); });
        }

        void AddGenericStat(float value, uint title, uint units, Func<float, string> format)
        {
            var t = Cur("COLUMN1_DATA"); var d = Cur("COLUMN2_DATA");
            AddStat(() =>
            {
                SetLanguageHash(t, title);
                Printf(d, units != 0 ? $"{format(value)} {Lang(units)}" : format(value));
            });
        }

        void AddTimerStat(float seconds, uint title)
        {
            var t = Cur("COLUMN1_DATA"); var d = Cur("COLUMN2_DATA");
            AddStat(() => { SetLanguageHash(t, title); Printf(d, FETimer.Print(seconds)); });
        }

        /// <summary>StatsPanel::Draw (0x57D570): the racer-name tab (8A41F5B9) when there is more than one racer.</summary>
        void DrawPanel(int numPlayers)
        {
            if (numPlayers > 1 && !string.IsNullOrEmpty(_racerName))
            {
                if (!IsScriptSet(NameGroup, FEHash.Appear)) SetScript(NameGroup, FEHash.Appear);
                Printf(NameText, _racerName);
            }
            else if (!IsScriptSet(NameGroup, FEHash.Hide)) SetScript(NameGroup, FEHash.Hide);
            foreach (var d in _draw) d();
        }

        // ---- Setup (0x5A19C0) -----------------------------------------------------------------------------------

        void SetupPages()
        {
            _winner = _racers.FindIndex(r => r.Finished && r.Place == 1);
            for (int i = 0; i < MaxSlotsLeftSide; i++)
            {
                SetScript(HashF($"LINE{i + 1}_GROUP"), FEHash.Hide);
                uint col2 = HashF($"COLUMN2_DATA{i + 1}");
                if (_mode == Mode.Stats || (_mode == Mode.LapStats && _type == RaceType.Tollbooth)) SetInvisible(col2);
                else SetVisible(col2);
            }
            SetInvisible(Head1); SetInvisible(Head2); SetInvisible(Head3);
            ResetPanel();
            switch (_mode)
            {
                case Mode.Results: SetupResults(); break;
                case Mode.Stats: if (_current < _racers.Count) SetupRacerStats(_racers[_current]); break;
                case Mode.LapStats: if (_current < _racers.Count) SetupLapStats(_racers[_current]); break;
            }
            DrawPanel(_racers.Count);
            if (!Lan && !IsScriptSet(RestartGroup, FEHash.Appear)) SetScript(RestartGroup, FEHash.Appear);
        }

        void SetupResults()
        {
            SetVisible(Head1); SetVisible(Head2); SetVisible(Head3);
            switch (_type)
            {
                case RaceType.Sprint: case RaceType.Circuit: case RaceType.Drag: case RaceType.Knockout:
                    SetLanguageHash(Head1, 0x96B05F47); SetLanguageHash(Head2, 0xCE678AD3); SetLanguageHash(Head3, 0xB67DA102);
                    break;
                case RaceType.SpeedTrap:
                    SetLanguageHash(Head1, 0x96B05F47); SetLanguageHash(Head2, 0xCE678AD3); SetLanguageHash(Head3, 0x7540FB04);
                    break;
            }
            SetLanguageHash(Title, 0xFF115FFF);
            SetLanguageHash(RaceButton, 0xD0B8AA33);
            // racers in ranking order [the decomp's lookup loop is garbled; the exe walks the rankings 1..n]
            var ordered = _racers.OrderBy(r => r.Place <= 0 ? 99 : r.Place).ToList();
            foreach (var info in ordered)
            {
                var name = Cur("COLUMN2_DATA"); var data = Cur("COLUMN3_DATA"); var pos = Cur("COLUMN1_DATA");
                if (_type == RaceType.SpeedTrap)
                {
                    float total = KphToShown(info.Points);
                    AddStat(() =>
                    {
                        // GenericResult::Draw (0x5952B0)
                        Printf(name, NameOf(info));
                        if (!info.Finished && !(_race != null && _race.SpeedTrapRace)) SetLanguageHash(data, FEHash.LabelNA);
                        else Printf(data, $"{N0(total)} {SpeedUnits}");
                        Printf(pos, info.Place.ToString());
                    });
                }
                else
                {
                    AddStat(() =>
                    {
                        // RaceResultStat::Draw
                        Printf(name, NameOf(info));
                        if (info.EngineBlown) SetLanguageHash(data, 0x01E66364);
                        else if (info.Totalled) SetLanguageHash(data, 0xB7B75185);
                        else if (info.Out) SetLanguageHash(data, 0x5D82DBA2);
                        else if (!info.Finished) SetLanguageHash(data, FEHash.LabelNA);
                        else Printf(data, FETimer.Print(info.FinishTime));
                        Printf(pos, info.Place.ToString());
                    });
                }
            }
        }

        // ---- STATS page ----------------------------------------------------------------------------------------

        void SetupRacerStats(Entrant info)
        {
            SetLanguageHash(Title, 0x4E706980);
            switch (_type)
            {
                case RaceType.Sprint: case RaceType.Drag: SetLanguageHash(RaceButton, 0x1135F776); break;
                case RaceType.Circuit: case RaceType.Knockout: SetLanguageHash(RaceButton, 0x7B8F45DF); break;
                case RaceType.SpeedTrap: SetLanguageHash(RaceButton, 0xAEF51E9D); break;
                case RaceType.Tollbooth: SetLanguageHash(RaceButton, 0xAC23368C); break;
            }
            _racerName = NameOf(info);
            var st = RaceStatsTracker.For(info);
            switch (_type)
            {
                case RaceType.Sprint:
                    TopSpeed(st); AverageSpeed(st);
                    if (Lan) NosUsed(st); else { TimeBehind(info); TrafficCollisions(st); }
                    break;
                case RaceType.Circuit: case RaceType.Knockout:
                    TopSpeed(st); AverageSpeed(st);
                    if (Lan) NosUsed(st); else { TimeBehind(info); LapVariance(info); TrafficCollisions(st); }
                    break;
                case RaceType.Drag:
                    ZeroToSixty(st); QuarterMile(st); PerfectShifts(st);
                    if (Lan) NosUsed(st); else { TimeBehind(info); TrafficCollisions(st); }
                    break;
                case RaceType.SpeedTrap:
                    AccumulatedSpeed(info); SpeedVariance(info); SpeedBehind(info);
                    break;
                case RaceType.Tollbooth:
                    TopSpeed(st); AverageSpeed(st); TrafficCollisions(st);
                    break;
            }
        }

        static string F0(float v) => N0(v);

        void NosUsed(RacerStats st)
        {
            float nos = Metric ? st.PoundsNos * 0.4536f : st.PoundsNos;
            if (RaceTimeElapsed > 0 && st.Ready) AddGenericStat(nos, 0x114E759F, Metric ? 0xC173DE1Fu : 0xC173E1BBu, F0);
            else AddInfoStat(0x114E759F, FEHash.LabelNA);
        }

        void TopSpeed(RacerStats st)
        {
            if (RaceTimeElapsed > 0 && st.Ready) AddGenericStat(MpsToShown(st.TopSpeed), 0x0EF34382, Metric ? 0x8569A25Fu : 0x8569AB44u, F0);
            else AddInfoStat(0x0EF34382, FEHash.LabelNA);
        }

        void AverageSpeed(RacerStats st)
        {
            if (RaceTimeElapsed > 0 && st.Ready) AddGenericStat(MpsToShown(st.AverageSpeed), 0x57F4140A, Metric ? 0x8569A25Fu : 0x8569AB44u, F0);
            else AddInfoStat(0x57F4140A, FEHash.LabelNA);
        }

        void TimeBehind(Entrant info)
        {
            int me = _racers.IndexOf(info);
            if (_winner >= 0 && _winner != me) AddTimerStat(Mathf.Abs(_racers[_winner].FinishTime - info.FinishTime), 0xAB44ED8B);
            else AddInfoStat(0xAB44ED8B, FEHash.LabelNA);
        }

        void LapVariance(Entrant info)
        {
            if (info.LapsCompleted > 1)
            {
                // GetBestLapTime / GetWorstLapTime (0x5FFFD0 / 0x6000A0) over the completed laps
                var laps = Enumerable.Range(0, info.LapsCompleted).Select(l => LapTime(info, l)).Where(t => t > 0).ToList();
                float best = laps.Count > 0 ? laps.Min() : 0, worst = laps.Count > 0 ? laps.Max() : 0;
                AddTimerStat(worst - best, 0x4121E8C4);
            }
            else AddInfoStat(0x4121E8C4, FEHash.LabelNA);
        }

        void TrafficCollisions(RacerStats st)
        {
            if (RaceTimeElapsed > 0) AddGenericStat(st.TrafficHits, 0x094BFDFC, 0, F0);
            else AddInfoStat(0x094BFDFC, FEHash.LabelNA);
        }

        /// <summary>The "0 to 60 mph" time is measured at 26.82 m/s in both unit systems; metric only relabels it "0 to 100 km/h".</summary>
        void ZeroToSixty(RacerStats st)
        {
            uint label = Metric ? 0xB8CF16FCu : 0xCCBC22B3u;
            if (st.ZeroToSixty > 0 && st.Ready) AddTimerStat(st.ZeroToSixty, label);
            else AddInfoStat(label, FEHash.LabelNA);
        }

        void QuarterMile(RacerStats st)
        {
            uint label = Metric ? 0x1C6F2A82u : 0x49FD5DCBu;
            if (st.QuarterMile > 0 && st.Ready) AddTimerStat(st.QuarterMile, label);
            else AddInfoStat(label, FEHash.LabelNA);
        }

        void PerfectShifts(RacerStats st)
        {
            if (RaceTimeElapsed > 0 && st.Ready) AddGenericStat(st.PerfectShifts, 0x680AC597, 0, F0);
            else AddInfoStat(0x680AC597, FEHash.LabelNA);
        }

        void AccumulatedSpeed(Entrant info)
        {
            if (RaceTimeElapsed > 0) AddGenericStat(KphToShown(info.Points), 0xD57E02AB, Metric ? 0x8569A25Fu : 0x8569AB44u, F0);
            else AddInfoStat(0xD57E02AB, FEHash.LabelNA);
        }

        /// <summary>GetBestSpeedTrapSpeed / GetWorstSpeedTrapSpeed (0x5DC3E0 / 0x5DC530); shown as worst − best, "%.2f".</summary>
        void SpeedVariance(Entrant info)
        {
            var speeds = info.TrapSpeeds.Where(s => s > 0).ToList();
            float best = speeds.Count > 0 ? speeds.Max() : 0, worst = speeds.Count > 0 ? speeds.Min() : 0;
            if (best > 0 && worst > 0)
                AddGenericStat(KphToShown(worst - best), 0x6EEABE8C, Metric ? 0x8569A25Fu : 0x8569AB44u, v => v.ToString("0.00"));
            else AddInfoStat(0x6EEABE8C, FEHash.LabelNA);
        }

        void SpeedBehind(Entrant info)
        {
            if (_winner >= 0) AddGenericStat(KphToShown(Mathf.Abs(_racers[_winner].Points - info.Points)), 0x2E54B7ED, Metric ? 0x8569A25Fu : 0x8569AB44u, F0);
            else AddInfoStat(0x2E54B7ED, FEHash.LabelNA);
        }

        // ---- LAPSTATS page (0x5972E0) ----------------------------------------------------------------------------

        void SetupLapStats(Entrant info)
        {
            SetLanguageHash(RaceButton, 0x8159A0B2);
            switch (_type)
            {
                case RaceType.Sprint: case RaceType.Drag:
                    SetLanguageHash(Title, 0x34BA50FF);
                    SetVisible(Head1); SetVisible(Head2); SetVisible(Head3);
                    SetLanguageHash(Head1, 0xE8B7D527); SetLanguageHash(Head2, 0x96B05F47); SetLanguageHash(Head3, 0xB67DA102);
                    break;
                case RaceType.Circuit: case RaceType.Knockout:
                    SetLanguageHash(Title, 0x9C8D7FE8);
                    SetVisible(Head1); SetVisible(Head2); SetVisible(Head3);
                    SetLanguageHash(Head1, 0x0000BF9C); SetLanguageHash(Head2, 0x96B05F47); SetLanguageHash(Head3, 0xB67DA102);
                    break;
                case RaceType.SpeedTrap:
                    SetLanguageHash(Title, 0xECD0E6A6);
                    SetVisible(Head1); SetVisible(Head2); SetVisible(Head3);
                    SetLanguageHash(Head1, 0xEE1EDC76); SetLanguageHash(Head2, 0x96B05F47); SetLanguageHash(Head3, 0x7540FB04);
                    break;
                case RaceType.Tollbooth:
                    SetLanguageHash(Title, 0xD10A8EA2);
                    SetVisible(Head1); SetVisible(Head3);
                    SetLanguageHash(Head1, 0xA15E4505); SetLanguageHash(Head3, 0xB67DA102);
                    SetLanguageHash(RaceButton, 0xD0B8AA33);
                    break;
            }
            _racerName = NameOf(info);
            switch (_type)
            {
                case RaceType.Sprint: case RaceType.Drag:
                    for (int k = 0; k < 4; k++) StageStat(k, info.SplitTimes[k], info.SplitRanks[k]);
                    StageStat(4, info.Finished ? info.FinishTime : 0, info.Place);
                    break;
                case RaceType.Circuit: case RaceType.Knockout:
                    for (int lap = 0; lap < Mathf.Max(1, _event?.Laps ?? 1); lap++)
                    {
                        int p = LapPosition(info, lap);
                        if (info.Out && p < 2) p = -1;
                        LapStat(lap + 1, LapTime(info, lap), p);
                    }
                    break;
                case RaceType.SpeedTrap:
                    // SortCheckPointRankings (0x5DBD80): position = rank by speed through that trap
                    for (int t = 0; t < (_event?.SpeedTraps.Count ?? info.TrapSpeeds.Count); t++)
                    {
                        float s = t < info.TrapSpeeds.Count ? info.TrapSpeeds[t] : 0;
                        int rank = 1 + _racers.Count(r => r != info && t < r.TrapSpeeds.Count && r.TrapSpeeds[t] > s);
                        SpeedStat(t + 1, s / 3.6f, rank);
                    }
                    break;
                case RaceType.Tollbooth:
                    int booths = Mathf.Max(info.BoothTimes.Count, _event?.Checkpoints.Count(c => c.TimeBonus > 0) ?? 0);
                    for (int b = 0; b < booths; b++) TollboothStat(b + 1, b < info.BoothTimes.Count ? info.BoothTimes[b] : 0, 1);
                    TollboothStat(booths + 1, info.Finished ? Mathf.Max(0, _race?.TimeLeft ?? 0) : 0, 1);
                    break;
            }
        }

        /// <summary>StageStat::Draw (0x5956F0): "%d%%" of the race (20, 40, 60, 80, 100), split position and time.</summary>
        void StageStat(int stage, float time, int pos)
        {
            var title = Cur("COLUMN1_DATA"); var data = Cur("COLUMN3_DATA"); var position = Cur("COLUMN2_DATA");
            AddStat(() =>
            {
                Printf(title, $"{(stage + 1) * 20}%");
                if (time == 0) { SetLanguageHash(position, FEHash.LabelNA); SetLanguageHash(data, FEHash.LabelNA); return; }
                Printf(position, pos.ToString());
                Printf(data, FETimer.Print(time));
            });
        }

        /// <summary>LapStat::Draw (0x595560): position -1 = K.O.</summary>
        void LapStat(int lap, float time, int pos)
        {
            var title = Cur("COLUMN1_DATA"); var data = Cur("COLUMN3_DATA"); var position = Cur("COLUMN2_DATA");
            AddStat(() =>
            {
                Printf(title, lap.ToString());
                if (pos == -1) { SetLanguageHash(position, 0x5D82DBA2); SetLanguageHash(data, 0x5D82DBA2); }
                else if (time == 0) { SetLanguageHash(position, FEHash.LabelNA); SetLanguageHash(data, FEHash.LabelNA); }
                else { Printf(position, pos.ToString()); Printf(data, FETimer.Print(time)); }
            });
        }

        /// <summary>SpeedStat::Draw (0x595800): the speed is taken as m/s (× 3.6 metric, × 2.23699 imperial), "%0.0f".</summary>
        void SpeedStat(int trap, float mps, int pos)
        {
            var title = Cur("COLUMN1_DATA"); var data = Cur("COLUMN3_DATA"); var position = Cur("COLUMN2_DATA");
            AddStat(() =>
            {
                Printf(title, trap.ToString());
                if (mps == 0) { SetLanguageHash(position, FEHash.LabelNA); SetLanguageHash(data, FEHash.LabelNA); return; }
                Printf(position, pos.ToString());
                Printf(data, MpsToShown(mps).ToString("0"));
            });
        }

        /// <summary>
        /// TollboothStat::Draw (0x595930): position −1.0 → K.O. (the decomp reads 1.0; the exe compares with −1.0 at
        /// 0x890DA8), time 0 → "--", else "+" and the time left when that booth was passed.
        /// </summary>
        void TollboothStat(int booth, float time, int pos)
        {
            var title = Cur("COLUMN1_DATA"); var data = Cur("COLUMN3_DATA"); var position = Cur("COLUMN2_DATA");
            AddStat(() =>
            {
                Printf(title, booth.ToString());
                if (pos == -1) { SetLanguageHash(position, 0x5D82DBA2); SetLanguageHash(data, 0x5D82DBA2); }
                else if (time == 0) { SetLanguageHash(position, FEHash.LabelNA); SetLanguageHash(data, FEHash.LabelNA); }
                else { Printf(position, pos.ToString()); Printf(data, "+" + FETimer.Print(time)); }
            });
        }

        // ---- NotificationMessage (0x5A7120) ---------------------------------------------------------------------

        bool PlayerDone => _player != null && (_player.Finished || _player.Totalled || _player.EngineBlown || _player.Out || _player.Busted);

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.InitComplete:
                    // LAN / online only (:683): the single-player group 0x812A09D4 hides, the LAN group 0x05D85A9F appears
                    if (!Lan) return;
                    SetScript(0x812A09D4, FEHash.Hide);
                    SetScript(0x05D85A9F, FEHash.Appear);
                    return;
                case FEHash.PadLTrigger:
                    if (_mode == Mode.Results) return;
                    if (--_current < 0) _current = _racers.Count - 1;
                    SetupPages();
                    return;
                case FEHash.PadRTrigger:
                    if (_mode == Mode.Results) return;
                    if (++_current >= _racers.Count) _current = 0;
                    SetupPages();
                    return;
                case FEHash.PadButton4:
                    if (_type == RaceType.Tollbooth) _mode = _mode == Mode.LapStats ? Mode.Stats : Mode.LapStats;
                    else { _mode++; if (_mode == Mode.Count) _mode = Mode.Results; }
                    SetupPages();
                    return;
                case FEHash.PadButton5:
                    if (Lan) return;
                    DialogInterface.ShowTwoButtons(this, "InGameDialog.fng", DialogTitle.Alert, FEHash.LabelOk, FEHash.LabelCancel,
                        MsgRestart, MsgRestartCancel, MsgRestartCancel, true, 1, Lang(0x4D3399A8));
                    return;
                case MsgRestart:
                    // EUnPause; under the photo finish the restart waits for its destructor (mRestartSelected)
                    // under the photo finish the exe sets mRestartSelected and the photo's destructor sends ERestartRace; here the
                    // restart runs at once so the race being left is still active for its adaptive-difficulty update
                    Restart(_event);
                    return;
                case MsgRestartCancel:
                    View.PackageMessage(FEHash.EnableInput);
                    return;
                case FEHash.PadAccept:
                    if (IsScriptSet(ButtonBar, FEHash.Hide)) return;
                    if (GameSession.Mode == GameMode.Career && _event != null && _event.BossRace && PlayerDone && _player.Place != 1)
                    {
                        DialogInterface.ShowTwoButtons(this, "InGameDialog.fng", DialogTitle.Alert, FEHash.LabelOk, FEHash.LabelCancel,
                            MsgQuitToFreeRoam, MsgRestartCancel, MsgRestartCancel, true, 1, Lang(0x9887EB98));
                        return;
                    }
                    goto case MsgQuitToFreeRoam;
                case MsgQuitToFreeRoam:
                    if (!IsScriptSet(Handler, FEHash.ScriptForward)) SetScript(Handler, FEHash.ScriptForward);
                    return;
                case FEHash.ExitComplete:
                    if (Lan) return;
                    if (_fromPhoto)
                    {
                        // back to the photo: pop this screen and InGameBackground
                        var fe = FE;
                        QueuePackagePop();
                        if (fe.Top is InGameBackgroundScreen) fe.Pop();
                        return;
                    }
                    bool career = GameSession.Mode == GameMode.Career && (!(_event?.DDayRace ?? false) || NfsMw.Game.Lua.LuaFlow.Drives);   // [Lua Drive: the D-Day script goes on]
                    // career: RaceAbandoned for a finished player happens in RaceManager.AfterResults; then EUnPause
                    if (career || !PlayerDone) { FE.CloseAll(); return; }
                    GameFlow.QuitToMainMenu();   // EQuitToFE(GARAGETYPE_MAIN_FE)
                    return;
            }
        }

        /// <summary>
        /// NotifySoundMessage (:840): the trigger sounds (0x4A805994 / 0x9AFA53A7) are silent with one racer or on the RESULTS
        /// page, and those plus 0x7B6B89D7 while the button bar (0x57EFB2FB) is hidden.
        /// </summary>
        protected override bool NotifySoundMessage(uint msg, FEView.Node obj)
        {
            if (msg == 0x4A805994 || msg == 0x9AFA53A7)
            {
                if (_racers.Count <= 1 || _mode == Mode.Results) return false;
            }
            else if (msg != 0x7B6B89D7) return true;
            return !IsScriptSet(ButtonBar, FEHash.Hide);
        }

        /// <summary>
        /// ERestartRace (0x6307F0) through RaceManager.RestartRace: the adaptive-difficulty Restart update on the race being
        /// left, RaceManager.Restarted, then the race again without its intro movie (its loading closes the front end).
        /// </summary>
        internal static void Restart(RaceEvent ev)
        {
            if (ev != null) RaceManager.RestartRace(ev);
            else FrontEnd.Instance?.CloseAll();
        }
    }

    /// <summary>InGameBackground.fng: the plain backdrop pushed under the results when they are opened from the photo finish.</summary>
    public sealed class InGameBackgroundScreen : FEScreen
    {
        public override string Package => FrontEndData.Packages.ContainsKey("InGameBackground") ? "InGameBackground" : "BLACK_FADE_OUT";
    }
}
