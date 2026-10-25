using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>Event list on SafeHouseRaceSheet.fng [ours]: quick races, the Challenge Series list kept for tests and the LAN race
    /// list. The career race sheet of the exe is <see cref="SafehouseRaceSheetScreen"/> below.</summary>
    public sealed class RaceListScreen : FEScreen
    {
        public enum Kind { Career, QuickRace, Challenge, Lan }
        public override string Package => "SafeHouseRaceSheet";
        readonly Kind _kind;
        List<RaceEvent> _events;
        int _index;

        public RaceListScreen(Kind kind) { _kind = kind; }

        public override void Open()
        {
            var career = GameSession.Career;
            _events = _kind switch
            {
                Kind.Career => GameSession.AvailableEvents(),
                Kind.Challenge => GameSession.ChallengeEvents(),
                _ => career.Bins.Values.Where(b => b.Index <= 15).SelectMany(b => b.WorldRaces.Concat(b.BossRaces)).Where(e => e.Start != null).ToList(),
            };
            SetText("TITLE", _kind == Kind.Career ? "blacklist race" : _kind == Kind.Challenge ? "challenge series" : _kind == Kind.Lan ? "lan race" : "quick race");
            View.SetVisible(View.Find("TRACK_MAP"), false);
            View.SetText(View.TextOf(View.Find("NAME_GROUP")), _kind == Kind.Career ? $"Blacklist #{GameSession.Profile.Bin}" : "");
            View.SetVisible(View.Find(0xF91A59F6), false);   // the bounty box sharing NAME_GROUP's spot
            View.SetVisible(View.Find(0xBBF970CD), false);   // 'EVENT ID' debug label
            Show();
        }

        void Show()
        {
            for (int i = 1; i <= 12; i++)
            {
                var n = View.Find("EVENT_ICON_" + i);
                if (n == null) continue;
                int idx = (_index / 12) * 12 + i - 1;
                bool has = idx < _events.Count;
                View.SetVisible(n, has);
                if (!has) continue;
                var e = _events[idx];
                var tex = FrontEndData.TextureByName(IconName(e.Type));
                if (tex) View.SetTexture(n, tex);
                bool won = GameSession.Profile != null && GameSession.Profile.HasWon(e.Name);
                n.Data.A = idx == _index ? 255 : won ? 90 : 160;
                n.Data.SizeX = n.Data.SizeY = idx == _index ? 64 : 52;
            }
            if (_events.Count == 0)
            {
                SetAllText("Circuit Race", "No events");
                SetAllText("Track Name here that is long", "");
                return;
            }
            var ev = _events[_index];
            bool locked = _kind == Kind.Challenge && !GameSession.ChallengeUnlocked(ev);
            string what = ev.TypeName + (ev.BossRace ? " (Rival)" : "");
            string where = Place(ev);
            if (_kind == Kind.Challenge)
            {
                what = (locked ? "LOCKED - " : "") + ev.TypeName + " " + ev.EventId;
                where = ev.Type == RaceType.Pursuit ? ChallengeGoal(ev) : CarNames.Display(GameSession.Career.CarForPreset(ev.PlayerCar));
            }
            SetAllText("Circuit Race", what);
            SetAllText("Track Name here that is long", where);
            CareerScreenKit.Text(View, 0x763F4B5B, Localize.Fixed(ev.Cash, 0, true));   // cash value group (FFBBBF24 / 03A80495), as SafehouseRaceSheetScreen
            SetAllText("Medium", ev.CopsInRace ? "High" : ev.TrafficLevel > 30 ? "Medium" : "Low");
            SetAllText("2:10:00", GameSession.Profile != null && GameSession.Profile.HasWon(ev.Name) ? "Won" : "--");
            SetAllText("123 mph", "--");
            SetAllText("187 mph", "--");
            var icon = View.Find(0xF97EC5D5);
            var modeTex = FrontEndData.TextureByName(IconName(ev.Type));
            if (icon != null && modeTex) View.SetTexture(icon, modeTex);
            // detail rows by event kind: scripts 0DBE7A1F (laps + best time), 20 (distance + average speed), 21 (top speed)
            uint mode = Laps(ev) ? 0x0DBE7A1Fu : ev.Type == RaceType.SpeedTrap || ev.Type == RaceType.Drag ? 0x0DBE7A21u : 0x0DBE7A20u;
            foreach (uint g in new uint[] { 0x6716A779, 0x8A971BC7, 0xB0F98EEE, 0xEDE07C9B, 0x5947E716 })
            {
                var n = View.Find(g);
                View.SetVisible(n, n != null && View.Play(n, mode));
            }
            View.SetText(View.Find(0x0018B36F), ev.Laps.ToString());
            View.SetText(View.Find(0x080C9DAA), $"{LengthOf(ev) / 1000f:0.0} km");
            // event counter: current / total
            foreach (var d in View.Descendants(View.Find(0x5A856A34))) View.SetText(d, (_index + 1).ToString());
            foreach (var d in View.Descendants(View.Find(0x2D4D22C8))) View.SetText(d, _events.Count.ToString());
        }

        static bool Laps(RaceEvent ev) => ev.Type == RaceType.Circuit || ev.Type == RaceType.Knockout;

        /// <summary>Route length in metres: the event's own value, or start -> checkpoints -> finish (times laps).</summary>
        public static float LengthOf(RaceEvent ev)
        {
            if (ev.Length > 1f) return ev.Length;
            var pts = new List<Vec3>();
            if (ev.Start != null) pts.Add(ev.Start.Position);
            pts.AddRange(ev.Checkpoints.Select(c => c.Position));
            if (ev.Finish != null) pts.Add(ev.Finish.Position);
            if (Laps(ev) && pts.Count > 1) pts.Add(pts[0]);
            float d = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
                d += Mathf.Sqrt(dx * dx + dy * dy);
            }
            return d * (Laps(ev) ? Mathf.Max(1, ev.Laps) : 1);
        }

        public static string Place(RaceEvent ev) => $"{Cap(ev.Region)} {ev.EventId}".Trim();
        public static string LengthLabel(RaceEvent ev) => Laps(ev) ? "Laps:" : "Length:";
        public static string LengthText(RaceEvent ev) => Laps(ev) ? ev.Laps.ToString() : $"{LengthOf(ev) / 1000f:0.0} km";

        /// <summary>A pursuit challenge's goal in words.</summary>
        public static string ChallengeGoal(RaceEvent ev)
        {
            var m = new Milestone { Type = ev.MilestoneType, GoalEasy = ev.Threshold };
            return ev.MilestoneType == "pursuit_evasion_time" ? $"Escape within {m.Format(ev.Threshold)}" : $"{m.Title} {m.Format(ev.Threshold)}, then escape";
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>The event type's icon texture.</summary>
        public static string IconName(RaceType t) => t switch
        {
            RaceType.Sprint => "MODE_ICON_SPRINT", RaceType.Knockout => "MODE_ICON_LAP_KNOCKOUT", RaceType.Drag => "MODE_ICON_DRAG",
            RaceType.Tollbooth => "TOLLBOOTH_ICON", RaceType.SpeedTrap => "MINIMAP_ICON_SPEEDTRAP", RaceType.Pursuit => "PERSUIT_ICON",
            _ => "MODE_ICON_CIRCUIT",
        };

        public override void Tick(MenuInput input)
        {
            if (_events.Count > 0)
            {
                if (input.Left) { _index = (_index + _events.Count - 1) % _events.Count; Show(); }
                if (input.Right) { _index = (_index + 1) % _events.Count; Show(); }
                if (input.Up) { _index = Mathf.Max(0, _index - 3); Show(); }
                if (input.Down) { _index = Mathf.Min(_events.Count - 1, _index + 3); Show(); }
                if (input.Accept)
                {
                    var ev = _events[_index];
                    if (_kind == Kind.Lan) { FE.Push(new LanLobbyScreen(ev)); return; }
                    if (_kind == Kind.Challenge)
                    {
                        if (!GameSession.ChallengeUnlocked(ev)) { FE.Push(new MessageScreen("Locked", "Win the earlier challenges to open this one")); return; }
                        GameSession.QuickCar = new OwnedCar { Car = GameSession.Career.CarForPreset(ev.PlayerCar) ?? "gti", Engine = 1, Tires = 1 };
                    }
                    if (_kind != Kind.Career) GameSession.Mode = _kind == Kind.Challenge ? GameMode.ChallengeSeries : GameMode.QuickRace;
                    GameFlow.StartRace(ev);
                    return;
                }
            }
            if (input.Back) FE.Pop();
        }
    }

    /// <summary>
    /// SafeHouseRaceSheet.fng / InGameRaceSheet.fng — UISafehouseRaceSheet (career/uiRepSheetRaceEvents.cpp; PC ctor 0x55A760,
    /// Setup 0x547E30, RefreshHeader 0x547730, NotificationMessage 0x551530, AddRace 0x51F120, NotifySoundMessage 0x5116F0) on an
    /// ArrayScrollerMenu 3 × 3 (EVENT_ICON_1..9; the package's 10..12 are never slots). Arg ≠ 0 = in game.
    /// <list type="bullet">
    /// <item>ctor: title 0xBDE82FCC ← 0x2F32A021 "RACE EVENTS" (in game) / 0x84E4A54C "Race Events"; TRACK_MAP = the track map
    /// streamer (front end: GarageMainScreen::CancelCameraPush).</item>
    /// <item>Setup (currentEvents is always true — ToggleList is empty): the world races of iCurrentViewBin in order, minus
    /// JumpToSpeedTrap / JumpToMilestone (AddRace: icon GetRaceIconHash, name GetRaceNameHash), every one unlocked, checked =
    /// score flag 2 (CheckRaceScoreFlags, PC — the decomp's IsCareerRaceComplete); desc label 0x9BA78BA2; first position 0.</item>
    /// <item>RefreshHeader: 0x5A856A34 / 0x2D4D22C8 "%d" position / count; 0x78008599 ← 0xC948EF80; 0xB514E2D8 / 0xF91A59F6
    /// "%s %$d" Bounty (GetTotalBounty) / Cash; 0x0013C45E "%.0f" cash value; 0x0018B36F "%d" laps; 0x080C9DAA "%$0.1f %s"
    /// length m × 0.001 (× 0.621504 for miles, flt_89B054) KMs 0x8569A26A / Miles 0x867DCFD9; 0x0F2CD475 ← TRACKNAME_;
    /// 0x9B21 ← 0x61D1C5A5 / 0x73C615A3 by GetCopsEnabled; 0x1C8FC866, 0x7AF67920, 0xBBF970CD hidden; 0x8FD41BB4 best time
    /// (P2P / circuit / drag / knockout / tollbooth: Timer::PrintToString, 0 → 0x472AA00A "--:--.--"; else 0x472AA00A);
    /// 0xEBD7F926 / 0xDE9145FB "%$0.2f %s" top / average speed in KM/H 0x8569A25F or MPH 0x8569AB44; 0x763F4B5B "%$0.0f" cash;
    /// 0xF97EC5D5 ← the race icon; MEDAL_THUMB_n hidden, locked → 0x0018ED48, checked → 0x028FEADD; the map re-inits when the
    /// selection moves.</item>
    /// <item>Keys: directions scroll (then RefreshHeader); accept on an unlocked race → Yes / No 0x77CF03C5 (PC label; first
    /// button No) → in game ERaceSheetOff + StartRaceFromInGame, front end AllocCustomRace + SetStartupRace(career) +
    /// RaceStarter::StartRace; accept with nothing chosen plays UISND_COMMON_WRONG; back → (InGame)ReputationOverview (Arg 1 in
    /// game); L / R triggers ToggleList (empty); SCREEN_TICK animates the map.</item>
    /// </list>
    /// </summary>
    public sealed class SafehouseRaceSheetScreen : FEMenuScreen
    {
        readonly bool _inGame;
        ArrayScroller _list;
        QRTrackMap _map;
        int _currentIndex;
        RaceEvent _theRace;
        const uint MsgYes = 0xD05FC3A3, MsgNo = 0x34DC1BCF;

        public SafehouseRaceSheetScreen(bool inGame = false) { _inGame = inGame; }
        // GarageMainScreen renders the safehouse garage behind every safehouse FE package (the exe is in its front end there);
        // the in-game variants sit over the world [ours: the port keeps the city loaded]
        public override bool ShowCar => !_inGame;
        public override string Platform => "CAREER_SAFEHOUSE.BIN";

        public override string Package => _inGame && FrontEndData.Packages.ContainsKey("InGameRaceSheet") ? "InGameRaceSheet" : "SafeHouseRaceSheet";

        sealed class RaceDatum { public RaceEvent Race; public bool Locked = true, Checked; }

        protected override void Setup()
        {
            _theRace = null;
            _list = new ArrayScroller(View, 3, 3);
            for (int i = 0; i < 9; i++)
            {
                var img = Obj(HashF("EVENT_ICON_" + (i + 1)));
                if (img != null) _list.AddSlot(img);
            }
            _map = new QRTrackMap(View, Obj(0x0F365871));
            SetLanguageHash(0xBDE82FCC, _inGame ? 0x2F32A021u : 0x84E4A54Cu);
            SetupList();
        }

        static GRaceType TypeOf(RaceEvent ev) => ev.GRaceType >= 0 ? (GRaceType)ev.GRaceType : FEDb.ToGRace(ev.Type);

        /// <summary>AddRace (0x51F120): every type but the two jump types.</summary>
        bool AddRace(RaceEvent ev)
        {
            var t = TypeOf(ev);
            if (t == GRaceType.JumpToSpeedTrap || t == GRaceType.JumpToMilestone) return false;
            _list.AddDatum(new ArrayScroller.Datum { Tag = new RaceDatum { Race = ev }, Hash = FEDatabaseData.RaceIconHash(t), Desc = FEDatabaseData.RaceNameHash(t) });
            return true;
        }

        void SetupList()
        {
            _list.ClearData();
            var career = GameSession.Career;
            if (career != null && career.Bins.TryGetValue(RepSheetMainScreen.ViewBin, out var bin))
                foreach (var ev in bin.WorldRaces)
                    if (AddRace(ev))
                    {
                        var d = (RaceDatum)_list.DatumAt(_list.NumDatum - 1).Tag;
                        d.Locked = false;
                        if (Game.Gameplay.RaceDatabase.CheckFlags(ev, RaceIndex.CompletedCareer)) d.Checked = true;
                    }
            _list.DescLabel = 0x9BA78BA2;
            if (_list.Current != null) _map.Init(((RaceDatum)_list.Current.Tag).Race);
            _list.SetInitialPosition(0);
            _currentIndex = 0;
            RefreshHeader();
        }

        void RefreshHeader()
        {
            _list.RefreshHeader();
            Printf(0x5A856A34, (_list.CurrentIndex + 1).ToString());
            Printf(0x2D4D22C8, _list.NumDatum.ToString());
            SetLanguageHash(0x78008599, 0xC948EF80);
            var p = GameSession.Profile;
            Printf(0xB514E2D8, Lang(0xCE6B99B1) + " " + Localize.Group(RapStable.TotalBounty()));
            Printf(0xF91A59F6, Lang(0x073B79E0) + " " + Localize.Group(p?.Cash ?? 0));
            var cur = _list.Current;
            if (cur == null) return;
            var race = ((RaceDatum)cur.Tag).Race;
            Printf(0x0013C45E, Localize.Fixed(race.Cash, 0, false));
            bool metric = FEDb.Data.Gameplay.SpeedoUnits == 1;
            float len = race.Length * 0.001f * (metric ? 1f : 0.621504f);
            Printf(0x0018B36F, race.Laps.ToString());
            Printf(0x080C9DAA, Localize.Fixed(len, 1, true) + " " + Lang(metric ? 0x8569A26Au : 0x867DCFD9u));
            Printf(0x0F2CD475, EngageEventScreen.TrackName(race));
            SetLanguageHash(0x9B21, race.CopsInRace ? 0x61D1C5A5u : 0x73C615A3u);
            SetInvisible(0x1C8FC866);
            SetInvisible(0x7AF67920);
            SetInvisible(0xBBF970CD);
            var info = Game.Gameplay.RaceDatabase.ScoreInfo(race);
            var t = TypeOf(race);
            bool timed = t == GRaceType.P2P || t == GRaceType.Circuit || t == GRaceType.Drag || t == GRaceType.Knockout || t == GRaceType.Tollbooth;
            float best = info?.Best ?? 0f;
            Printf(0x8FD41BB4, timed && best != 0f ? GameHud.PrintTime(best, 0) : Lang(0x472AA00A));
            // MPS2KPH / MPS2MPH of the saved speeds (m/s)
            float k = metric ? 3.6f : 2.23693629f;
            string su = Lang(metric ? 0x8569A25Fu : 0x8569AB44u);
            Printf(0xEBD7F926, Localize.Fixed((info?.TopSpeed ?? 0f) * k, 2, true) + " " + su);
            Printf(0xDE9145FB, Localize.Fixed((info?.AverageSpeed ?? 0f) * k, 2, true) + " " + su);
            Printf(0x763F4B5B, Localize.Fixed(race.Cash, 0, true));
            SetTextureHash(0xF97EC5D5, FEDatabaseData.RaceIconHash(t));
            for (int i = 0; i < _list.NumSlots; i++)
            {
                var d = _list.DatumAt(i + _list.Start)?.Tag as RaceDatum;
                uint check = HashF("MEDAL_THUMB_" + (i + 1));
                SetInvisible(check);
                if (d == null) continue;
                if (d.Locked) { SetVisible(check); SetTextureHash(check, 0x0018ED48); }
                else if (d.Checked) { SetVisible(check); SetTextureHash(check, 0x028FEADD); }
            }
            if (_currentIndex != _list.CurrentIndex)
            {
                _map.Init(race);
                _currentIndex = _list.CurrentIndex;
            }
        }

        /// <summary>ArrayScrollerMenu: directions move the highlight (ScrollVer / ScrollHor) then RefreshHeader; accept presses
        /// the highlighted slot; back goes to the package; the triggers' ToggleList is empty.</summary>
        protected override void Feed(MenuInput i)
        {
            if (i.Up) { _list.ScrollVer(false); RefreshHeader(); }
            if (i.Down) { _list.ScrollVer(true); RefreshHeader(); }
            if (i.Left) { _list.ScrollHor(false); RefreshHeader(); }
            if (i.Right) { _list.ScrollHor(true); RefreshHeader(); }
            if (i.Accept) NotificationMessage(FEHash.ButtonPressed, null);
            if (i.Back) View.PadButton(FEHash.PadBack);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.ScreenTick:
                    _map.UpdateAnimation();
                    break;
                case FEHash.ButtonPressed:
                {
                    // RaceDatum::NotificationMessage (0x5116D0): an unlocked datum becomes theRace
                    var d = _list.Current?.Tag as RaceDatum;
                    if (d != null && !d.Locked) _theRace = d.Race;
                    if (_theRace == null) { NfsMw.Audio.FeSound.Play(NfsMw.Audio.FeSoundId.CommonWrong); break; }
                    DialogInterface.ShowTwoButtons(this, _inGame ? "InGameDialog.fng" : "", DialogTitle.Alert, 0x70E01038, 0x417B25E4,
                        MsgYes, MsgNo, MsgNo, true, 1, Lang(0x77CF03C5));
                    break;
                }
                case MsgYes:
                    if (_theRace != null)
                    {
                        var ev = _theRace;
                        FE.CloseAll();          // ERaceSheetOff (in game) / the front end goes (RaceStarter::StartRace)
                        GameFlow.StartRace(ev); // GManager::StartRaceFromInGame / AllocCustomRace + SetStartupRace(career)
                    }
                    break;
                case FEHash.PadBack:
                    QueuePackagePop();   // QueuePackageSwitch((InGame|SafeHouse)ReputationOverview.fng): the overview pushed this sheet
                    break;
            }
        }
    }

    // RapSheetScreen (RapSheetMain and its pages) lives in RapSheetScreens.cs.

}
