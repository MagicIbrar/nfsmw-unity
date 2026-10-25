using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Gameplay;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// GRaceDatabase::AllocCustomRace + cFrontendDatabase::FillCustomRace (FED:1016) for the port: a copy of the event
    /// with the quick race settings applied (laps, opponents, AI skill, catch up, traffic 0/10/30/50 %, direction, cops
    /// off), started with RaceStarter::StartRace (GameFlow.StartRace in the quick race mode).
    /// </summary>
    public static class QuickRaceStarter
    {
        /// <summary>The settings (and car) the last custom race was started with; AI / catch-up code may read it [hook].</summary>
        public static RaceSettings Current;

        public static RaceEvent Custom(RaceEvent ev, RaceSettings s)
        {
            var c = Clone(ev);
            if (s == null) return c;
            c.CopsInRace = s.CopsOn;
            c.Difficulty = s.AISkill switch { 0 => 0, 1 => 50, _ => 100 };   // GRace::Difficulty easy / medium / hard → DifficultyLevel buckets 33 / 66
            if (c.Looping || c.Type == RaceType.Circuit || c.Type == RaceType.Knockout) c.Laps = Mathf.Max(1, s.NumLaps);
            c.TrafficLevel = FEDatabaseData.TrafficPercent(s.TrafficDensity);
            if (s.TrackDirection == 1) c.Reversed = !ev.Reversed;   // SetReversed [?: the route itself is not mirrored by RaceManager]
            int n = Mathf.Clamp(s.NumOpponents, 0, 7);
            if (c.Opponents.Count > n)
            {
                var keep = c.Opponents.Take(n).ToList();
                c.Opponents.Clear();
                c.Opponents.AddRange(keep);
            }
            return c;
        }

        /// <summary>A field-by-field copy of a RaceEvent (readonly lists copied into the new instance's lists).</summary>
        public static RaceEvent Clone(RaceEvent ev)
        {
            var c = new RaceEvent();
            foreach (var f in typeof(RaceEvent).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var v = f.GetValue(ev);
                if (f.IsInitOnly && v is IList src && f.GetValue(c) is IList dst) { dst.Clear(); foreach (var x in src) dst.Add(x); }
                else if (f.IsInitOnly && v is IDictionary dsrc && f.GetValue(c) is IDictionary ddst) { ddst.Clear(); foreach (DictionaryEntry kv in dsrc) ddst[kv.Key] = kv.Value; }
                else if (!f.IsInitOnly) f.SetValue(c, v);
            }
            return c;
        }

        /// <summary>RaceStarter::StartRace for a quick race: the selected car(s), then the race.</summary>
        public static void Start(RaceEvent ev, RaceSettings s)
        {
            Current = s;
            GameSession.Mode = GameMode.QuickRace;
            GameSession.SplitScreen = FEDb.IsSplitScreen;
            GameFlow.StartRace(Custom(ev, s));
        }
    }

    /// <summary>
    /// UnlockSystem::IsEventAvailable (Careers/UnlockSystem.cpp:767; PC 0x576A20): 99.1.1 never; 21.1.1 / 21.2.1 / 21.2.2 / 19.9.70
    /// only on the Collector's Edition (IsCollectorsEdition 0x8F8718 = 1 in v1.3: always); 19.8.31 (Burger King) only once easter
    /// egg 3 is unlocked and CareerSettings flag 0x2000 (HasBeenAwardedBKReward) is clear [?: the port keeps no BK reward flag —
    /// taken as clear]; everything else yes.
    /// </summary>
    public static class QuickRaceUnlock
    {
        public const bool IsCollectorsEdition = true;   // 0x8F8718

        public static bool IsEventAvailable(RaceEvent ev)
        {
            string id = ev?.EventId;
            if (id == "99.1.1") return false;
            if (id == "21.1.1" || id == "21.2.1" || id == "21.2.2" || id == "19.9.70") return IsCollectorsEdition;
            if (id == RaceIndex.BurgerKing) return NfsMw.Controls.EasterEggs.Instance.IsEasterEggUnlocked(NfsMw.Controls.EasterEggs.Special.BurgerKing);
            return true;
        }

        /// <summary>UnlockSystem::IsTrackUnlocked(UNLOCK_QUICK_RACE) (0x576790 → QuickRaceUnlocker 0x5765A0): UnlockAllThings, the
        /// UnlockedQuickRace score flag, and 19.8.31 always.</summary>
        public static bool IsTrackUnlocked(RaceEvent ev) =>
            NfsMw.Controls.EasterEggs.UnlockAllThings || RaceDatabase.IsQuickRaceUnlocked(ev) || ev?.EventId == RaceIndex.BurgerKing;
    }

    /// <summary>
    /// UITrackMapStreamer (quickrace/uiTrackMapStreamer.cpp; PC 0x510980-0x52C355, ctor 0x55A280, Init 0x546400, SetMapLoaded
    /// 0x51DBC0, UpdateMap 0x510B00, ZoomToTrack 0x510D30, PanToTrack 0x510DA0) for Track_Select: the TRACK_MAP multi-image gets
    /// TRACK_MAP_&lt;event id, dots → '_'&gt; of TRACKS\L2RA\TrackMaps.bin (CalcMapTextureHash 0x510AC0; no race → TRACK_MAP),
    /// hidden until it is loaded; on load ZoomToTrack / PanToTrack: CalcBoundsForRace (0x510C10) = the race's bounding box
    /// (GRaceParameters::GetBoundingBox 0x5FCEA0 from the GRaceIndexData bytes +0x2C..0x2F that GenerateIndex 0x600FC4 stored as
    /// (int)(v·255) of the start / finish / checkpoint positions through Minimap::ConvertPos) grown by 0.125 on every side; the
    /// zoom (tCubic, 1 s, starting at 1) goes to the box size, the pan (starting at 0, 0) to its centre. UpdateAnimation (every
    /// SCREEN_TICK) seeks both cubics by the real frame time and UpdateMap sets the image's UV box to centre ± max(half sizes).
    /// The world → map conversion is the port's minimap fit (CareerScreenKit.MapUV) [?: TrackInfo calibration not parsed].
    /// </summary>
    public sealed class QRTrackMap
    {
        readonly FEView _view;
        readonly FEView.Node _node;
        readonly TrackMapStreamer _s;

        public QRTrackMap(FEView view, FEView.Node node)
        {
            _view = view;
            _node = node;
            if (node == null) return;
            _s = new TrackMapStreamer(view, node, "TRACK_MAP") { Duration = 1f };
            _s.SetZoom(1f);                    // ZoomCubic.SetVal(1, 1)
            _s.SetPan(Vector2.zero);           // PanCubic(0, 1.0f): value 0
            view.SetVisible(node, false);
        }

        /// <summary>Init (0x546400) + the load callbacks: texture, visibility, ZoomToTrack / PanToTrack.</summary>
        public void Init(RaceEvent ev)
        {
            if (_s == null) return;
            _view.SetInvisible(_node);
            string name = ev == null ? "TRACK_MAP" : "TRACK_MAP_" + (ev.EventId ?? "").Replace('.', '_');
            var tex = CareerScreenKit.TrackMap(name);
            if (tex == null) return;           // never loads: stays hidden
            _view.SetTexture(_node, tex);
            if (_node.Mask) _node.Image.material = null;
            _view.SetVisibleTree(_node);
            if (!Bounds(ev, out var tl, out var br)) { tl = Vector2.zero; br = Vector2.one; }
            // CalcBoundsForRace: ±0.125; ZoomTo(BR − TL) (square through UpdateMap's max), PanTo(centre)
            tl -= new Vector2(0.125f, 0.125f);
            br += new Vector2(0.125f, 0.125f);
            _s.ZoomTo(Mathf.Max(br.x - tl.x, br.y - tl.y));
            _s.PanTo((tl + br) * 0.5f);
        }

        /// <summary>GRaceParameters::GetBoundingBox: min / max of start, finish and checkpoints in map space, quantised as the index stores it.</summary>
        static bool Bounds(RaceEvent ev, out Vector2 tl, out Vector2 br)
        {
            var lo = new Vector2(float.MaxValue, float.MaxValue);
            var hi = new Vector2(float.MinValue, float.MinValue);
            tl = br = Vector2.zero;
            if (ev == null) return false;
            bool any = false;
            var pts = new List<RacePoint> { ev.Start, ev.Finish };
            pts.AddRange(ev.Checkpoints);
            foreach (var p in pts)
            {
                if (p == null) continue;
                var uv = CareerScreenKit.MapUV(Coords.ToUnity(p.Position));
                lo = Vector2.Min(lo, uv); hi = Vector2.Max(hi, uv); any = true;
            }
            if (!any) return false;
            tl = lo; br = hi;
            static float Q(float v) => (byte)(int)(v * 255f) / 255f;   // GenerateIndex: byte = (int)(v·255), read back × 1/255
            tl = new Vector2(Q(tl.x), Q(tl.y));
            br = new Vector2(Q(br.x), Q(br.y));
            return true;
        }

        /// <summary>UpdateAnimation (0x510C70): cPoint::SplineSeek by RealTimeElapsed, then UpdateMap.</summary>
        public void UpdateAnimation() => _s?.Update(Time.unscaledDeltaTime);
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// UIQRTrackSelect on Track_Select.fng (quickrace/uiQRTrackSelect.cpp; PC Setup 0x7BF460, BuildPresetTrackList 0x7BA5C0,
    /// TryToAddTrack 0x7AAC70, RefreshHeader 0x7AADE0, ScrollTracks 0x7AB370, ScrollRegions 0x7BA760, NotificationMessage
    /// 0x7C1490). The list: bin 21, then — the loop sets the counter to 0x10 after 21 and decrements — bins 15..1 (bin 16 is
    /// never visited), world races then boss races, each passing UnlockSystem::IsEventAvailable, not NeverInQuickRace, of the
    /// current RaceMode, not D-Day, in the region filter (0 College, 1 Coastal, 2 City, 3 all); locked = !IsTrackUnlocked(quick
    /// race filter 1; career 2 / online 4 unused here). IsRaceValidForMike (0x7A3140) is dead: MikeMannBuild (0x92612C) is 0 in
    /// the PC .data and only ActivateEasterEgg type 2 (0x65C376) writes it — with 0. Left / right scroll the tracks
    /// (circular; the map re-inits on a change), the L / R triggers the region; Enter (unlocked only) keeps the track in the
    /// race settings and goes to Track_Options (split screen sprint / drag / speedtrap skip to PressStart with one opponent
    /// and no cops). Back clears the startup race and returns to MainMenu_Sub (the mode select). SCREEN_TICK animates the
    /// track map (<see cref="QRTrackMap"/>). The FEngSetButtonTexture calls on 0x091C4A50 / 0x2D145BE3 (joy button art) are
    /// not reproduced [?].
    /// </summary>
    public sealed class TrackSelectScreen : FEScreen
    {
        public override string Package => "Track_Select";
        sealed class Track { public RaceEvent Ev; public bool Locked; public int Bin; }
        readonly List<Track> _tracks = new List<Track>();
        int _index = -1;
        QRTrackMap _map;

        Track Cur => _index >= 0 && _index < _tracks.Count ? _tracks[_index] : null;

        public override void Open()
        {
            uint title = FEDb.RaceMode switch
            {
                GRaceType.Circuit => 0x3DE80A85u, GRaceType.Drag => 0x136C5C90u, GRaceType.Knockout => 0xD6D65640u,
                GRaceType.P2P => 0xC2D85652u, GRaceType.Tollbooth => 0xE3AFADC9u, GRaceType.SpeedTrap => 0x3070453Au, _ => 0u,
            };
            SafehouseUi.SetLang(View, 0xB71B576D, title != 0 ? FEDb.L(title) : "");
            _map = new QRTrackMap(View, View.Find("TRACK_MAP"));
            Build();
            _map.Init(Cur?.Ev);   // BuildPresetTrackList: TrackMapStreamer.Init(pCurrentTrack, TrackMap)
            Refresh();
        }

        void Build()
        {
            _tracks.Clear();
            _index = -1;
            var c = GameSession.Career;
            if (c == null) return;
            var s = FEDb.QuickRace();
            int region = s.RegionFilterBits;
            var seen = new HashSet<string>();
            for (int bin = 21; bin > 0; bin--)
            {
                if (c.Bins.TryGetValue(bin, out var b))
                    foreach (var ev in b.WorldRaces.Concat(b.BossRaces)) TryAdd(ev, bin, region, s, seen);
                if (bin == 21) bin = 16;   // 0x7BA6DE: bin_num = 0x10, then the loop's decrement → 15
            }
            if (_index < 0 && _tracks.Count > 0) _index = 0;
        }

        void TryAdd(RaceEvent ev, int bin, int region, RaceSettings s, HashSet<string> seen)
        {
            if (ev == null || ev.Start == null || !seen.Add(ev.Name)) return;
            if (!QuickRaceUnlock.IsEventAvailable(ev)) return;
            if (ev.NeverInQuickRace || ev.DDayRace) return;
            if (ev.GRaceType != (int)FEDb.RaceMode) return;
            if (region != 3 && ev.RegionIndex != region) return;
            bool unlocked = QuickRaceUnlock.IsTrackUnlocked(ev);
            _tracks.Add(new Track { Ev = ev, Locked = !unlocked, Bin = bin });
            if (ev.Name == s.EventName) _index = _tracks.Count - 1;
        }

        /// <summary>RefreshHeader (uiQRTrackSelect.cpp:203).</summary>
        void Refresh()
        {
            var s = FEDb.QuickRace();
            uint regionText = s.RegionFilterBits switch { 0 => 0xA6850651u, 1 => 0xA5C20E7Du, 2 => 0x8663FAEFu, _ => 0x632DD19Bu };
            SafehouseUi.SetLang(View, 0x78008599, FEDb.L(regionText));
            SafehouseUi.SetLang(View, 0x4510987F, FEDb.L(regionText));
            SafehouseUi.SetLang(View, 0x6F25A248, (_index + 1).ToString());
            SafehouseUi.SetLang(View, 0xB2037BDC, _tracks.Count.ToString());
            SafehouseUi.SetLang(View, 0xB5154998, FEDb.L(FEDatabaseData.RaceNameHash(FEDb.RaceMode)));
            SafehouseUi.Visible(View, 0x6B67D70B, true);
            var t = Cur;
            if (t == null)
            {
                SafehouseUi.SetLang(View, 0x6F25A248, "0");
                SafehouseUi.SetLang(View, 0xB2037BDC, "0");
                SafehouseUi.SetLang(View, 0x5E7B09C9, "");
                SafehouseUi.SetLang(View, 0x0DFB7A2E, "");
                SafehouseUi.SetLang(View, 0xB5154999, "--");
                SafehouseUi.SetLang(View, 0xB515499C, FEDb.L(0x472AA00A));
                SafehouseUi.SetLang(View, 0x68215623, FEDb.L(0xF9C0519A));
                SafehouseUi.Visible(View, 0xE08434FC, false);
                return;
            }
            if (!t.Locked)
            {
                SafehouseUi.Visible(View, 0x6B67D70B, false);
                SafehouseUi.Visible(View, 0xE08434FC, true);
            }
            else
            {
                string fmt = FEDb.L(0xBD563BE5);
                int s1 = fmt.IndexOf("%s");
                if (s1 >= 0) fmt = fmt.Substring(0, s1) + SafehouseUi.RivalAka(t.Bin) + fmt.Substring(s1 + 2);
                SafehouseUi.SetLang(View, 0x68215623, fmt.Replace("%d", t.Bin.ToString()));
                SafehouseUi.Visible(View, 0xE08434FC, false);
            }
            // TRACKNAME_<event> when the string exists, else the event id
            string name = FrontEndData.Text(FrontEndData.Hash("TRACKNAME_" + t.Ev.EventId), null) ?? t.Ev.EventId;
            SafehouseUi.SetLang(View, 0x5E7B09C9, name);
            SafehouseUi.SetLang(View, 0x0DFB7A2E, name);
            SafehouseUi.Visible(View, 0xBBF970CD, false);
            bool kph = FEDb.Data.Gameplay.SpeedoUnits == 1;
            float len = RaceListScreen.LengthOf(t.Ev);
            SafehouseUi.SetLang(View, 0xB5154999, $"{Localize.Fixed(len * (kph ? 0.001f : 0.000621371f), 1, true)} {FEDb.L(kph ? 0x8569A26Au : 0x867DCFD9u)}");
            var info = RaceDatabase.ScoreInfo(t.Ev);
            var type = FEDb.ToGRace(t.Ev.Type);
            if (type == GRaceType.P2P || type == GRaceType.Circuit || type == GRaceType.Drag || type == GRaceType.Knockout || type == GRaceType.Tollbooth)
                SafehouseUi.SetLang(View, 0xB515499C, GameHud.PrintTime(info?.Best ?? 0f, 0));   // Timer(mBestTime).PrintToString(buf, 0)
            else if (type == GRaceType.SpeedTrap)
            {
                float best = info?.Best ?? 0;   // mBestSpeed, km/h
                float shown = kph ? best : best * 0.621371f;   // MPS2MPH(KPH2MPS())
                SafehouseUi.SetLang(View, 0x28462C64, FEDb.L(0x0512E823));
                SafehouseUi.SetLang(View, 0xB515499C, $"{Localize.Fixed(shown, 0, true)} {FEDb.L(kph ? 0x8569A25Fu : 0x8569AB44u)}");
            }
            else SafehouseUi.SetLang(View, 0xB515499C, FEDb.L(0x472AA00A));
            if (type == GRaceType.Circuit || type == GRaceType.Knockout) SafehouseUi.SetLang(View, 0x28462C64, FEDb.L(0xC5B5A177));
            var icon = FrontEndData.Texture(FEDatabaseData.RaceIconHash(type));
            if (icon) View.SetTexture(View.Find(0x08007B4C), icon);
        }

        /// <summary>Timer::PrintToString: m:ss.hh.</summary>
        public static string Time(float s) => $"{(int)(s / 60)}:{s % 60:00.00}";

        void ScrollRegions(int dir)
        {
            var s = FEDb.QuickRace();
            s.RegionFilterBits = (s.RegionFilterBits + dir + 4) % 4;
            Build();
            _map?.Init(Cur?.Ev);
            Refresh();
        }

        /// <summary>ScrollTracks (0x7AB370): circular; a new track re-inits the map and the header.</summary>
        void ScrollTracks(int dir)
        {
            if (_tracks.Count < 1) return;
            var old = Cur;
            _index = (_index + (dir < 0 ? _tracks.Count - 1 : 1)) % _tracks.Count;
            if (Cur != old) { _map?.Init(Cur?.Ev); Refresh(); }
        }

        public override void Tick(MenuInput input)
        {
            var keys = PcKeys.Read();
            _map?.UpdateAnimation();                 // FEMSG_SCREEN_TICK → TrackMapStreamer.UpdateAnimation
            if (input.Left) ScrollTracks(-1);
            if (input.Right) ScrollTracks(1);
            if (keys.PageLeft) ScrollRegions(-1);    // PAD_LTRIGGER
            if (keys.PageRight) ScrollRegions(1);    // PAD_RTRIGGER
            if (input.Accept)
            {
                var t = Cur;
                if (t == null || t.Locked) return;
                var s = FEDb.QuickRace();   // SetSelectedTrack: the RaceMode record (the list only holds RaceMode races)
                s.EventName = t.Ev.Name;
                s.EventHash = t.Ev.Key;
                if (FEDb.IsSplitScreen && (FEDb.RaceMode == GRaceType.Drag || FEDb.RaceMode == GRaceType.P2P || FEDb.RaceMode == GRaceType.SpeedTrap))
                {
                    s.NumOpponents = 1; s.CopsOn = false;
                    FE.Replace(new PressStartScreen(0));
                    return;
                }
                FE.Replace(new TrackOptionsScreen());
                return;
            }
            if (input.Back)
            {
                FEDb.QuickRace().EventName = "";
                FEDb.QuickRace().EventHash = 0;
                if (FEDb.Is(FEGameMode.Lan)) { FE.Pop(); return; }
                FE.Replace(new ModeSelectScreen());
            }
        }

        /// <summary>The race the settings point at (Track_Options / Car_Select read it).</summary>
        public static RaceEvent Selected()
        {
            var s = FEDb.QuickRace();
            var c = GameSession.Career;
            if (c == null || string.IsNullOrEmpty(s.EventName)) return null;
            return c.Events.Values.FirstOrDefault(e => e.Name == s.EventName);
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// UIQRTrackOptions on Track_Options.fng (quickrace/uiQRTrackOptions.cpp; iMaxWidgetsOnScreen 9). The widgets per
    /// mode (SetupCircuit .. SetupTollbooth): Track Direction (when the race can be reversed [?: never in the port]),
    /// Laps 1..8, Traffic Level None/Minimum/Moderate/Maximum, Opponents 1..(4 − players), Difficulty Easy/Medium/Hard,
    /// Catch Up; knockout greys Laps (= opponents). Enter starts the custom race setup → Car_Select (PressStart in split
    /// screen); Back → Track_Select; key 1 asks 0x8AEF5AE8 and restores DefaultRaceSettings.
    /// </summary>
    public sealed class TrackOptionsScreen : WidgetMenuScreen
    {
        public override string Package => "Track_Options";
        RaceEvent _race;
        Toggle _laps;

        public override void Open()
        {
            MaxWidgetsOnScreen = 9;
            InitWidgetMenu();
            _race = TrackSelectScreen.Selected();
            Setup();
        }

        RaceSettings S => FEDb.QuickRace();

        void Setup()
        {
            ClearWidgets();
            bool split = FEDb.IsSplitScreen;
            // GRaceParameters::GetCanBeReversed (0x5FAC80) reads GRaceIndexData flag 0x8; GenerateIndex (0x600E09) fills that flag
            // by calling GetCanBeReversed while the index is still null, whose fallback path only loads the vaults and returns
            // false (0x5FACC4) — so the flag is always 0 and the TrackDirection widget never appears on PC (reversed routes are
            // separate events, e.g. 10.4.2.r)
            const bool canReverse = false;
            switch (FEDb.RaceMode)
            {
                case GRaceType.P2P:
                    if (canReverse) Direction();
                    if (!split) { Traffic(); Opponents(); Skill(); CatchUp(); }
                    break;
                case GRaceType.Drag:
                    if (canReverse) Direction();
                    if (!split) { Opponents(); Skill(); CatchUp(); }
                    break;
                case GRaceType.Knockout:
                    if (canReverse) Direction();
                    Opponents();
                    Laps();
                    _laps.Enabled = false;
                    _laps.SetScript(FEView.Grey);
                    if (!split) Traffic();
                    Skill(); CatchUp();
                    break;
                case GRaceType.SpeedTrap:
                    if (canReverse) Direction();
                    if (!split) { Traffic(); Opponents(); Skill(); CatchUp(); }
                    break;
                case GRaceType.Tollbooth:
                    if (canReverse) Direction();
                    if (!split) Traffic();
                    break;
                default:
                    if (canReverse) Direction();
                    Laps();
                    if (!split) { Traffic(); Opponents(); Skill(); CatchUp(); }
                    break;
            }
            SetInitialOption(0);
            uint header = FEDb.Is(FEGameMode.Online) || FEDb.Is(FEGameMode.Lan) ? 0x7DADEE33u : (_race != null ? FEDb.ToGRace(_race.Type) : FEDb.RaceMode) switch
            {
                GRaceType.Circuit => 0xA6ED015Du, GRaceType.Drag => 0xEC86E188u, GRaceType.Knockout => 0x0CC959B8u,
                GRaceType.P2P => 0xB80BFC8Au, GRaceType.Tollbooth => 0x141EDFE1u, GRaceType.SpeedTrap => 0xF2745852u, _ => 0u,
            };
            foreach (var n in View.Nodes) if (n.Obj.Hash == 0x42ADB44C && n.Label != null) View.SetText(n, header != 0 ? FEDb.L(header) : "");
        }

        void Opponents() => AddToggle(dir =>
        {
            int max = 4 - FEDb.NumPlayers, o = S.NumOpponents;
            if (dir < 0) { o--; if (o < 1) o = max; }
            else if (dir > 0) { o++; if (o > max) o = 1; }
            S.NumOpponents = o;
            if (FEDb.RaceMode == GRaceType.Knockout) { S.NumLaps = o; _laps?.Draw(); }   // message 0x92B703B5 redraws
        }, w => w.Set(0x3384A679, S.NumOpponents.ToString()));

        void Skill() => AddToggle(dir =>
        {
            int k = S.AISkill + dir;
            S.AISkill = k < 0 ? 2 : k > 2 ? 0 : k;
        }, w => w.Set(0x4D156786, S.AISkill switch { 0 => 0x61973E01u, 1 => 0x3747F6D0u, _ => 0x6198E2EEu }));

        void CatchUp() => AddToggle(dir => { if (dir != 0) S.CatchUp = !S.CatchUp; },
            w => w.Set(0x8B8E913A, S.CatchUp ? 0x417B2604u : 0x70DFE5C2u));

        void Traffic() => AddToggle(dir =>
        {
            int t = S.TrafficDensity + dir;
            S.TrafficDensity = t < 0 ? 3 : t > 3 ? 0 : t;
        }, w => w.Set(0xEB9DFC09, S.TrafficDensity switch { 0 => 0x8CDC3937u, 1 => 0x73C615A3u, 2 => 0xA2CCA838u, _ => 0x61D1C5A5u }));

        void Laps() => _laps = AddToggle(dir =>
        {
            int l = S.NumLaps + dir;
            S.NumLaps = l < 1 ? 8 : l > 8 ? 1 : l;
        }, w => w.Set(0x48494E83, S.NumLaps.ToString()));

        void Direction() => AddToggle(dir => { if (dir != 0) S.TrackDirection = S.TrackDirection == 0 ? 1 : 0; },
            w => w.Set(0xA88FFEB4, S.TrackDirection == 0 ? 0xDE6EFF34u : 0xA1CD823Eu));

        public override void Tick(MenuInput input)
        {
            TickWidgets(input);
            if (input.Accept)
            {
                if (FEDb.IsSplitScreen) { S.NumOpponents = 1; S.CopsOn = false; FE.Replace(new PressStartScreen(0)); }
                else FE.Replace(new CarSelectScreen(CarSelectScreen.Mode.QuickRace));
                return;
            }
            if (input.Back) { FE.Replace(new TrackSelectScreen()); return; }
            if (input.Key1)
                FEDialog.ShowYesNo(FE, FEDb.L(0x8AEF5AE8), () =>
                {
                    // dialog_message_yes: cFrontendDatabase::DefaultRaceSettings (FED:838) — every record's RaceSettings::Default
                    // (RaceDB.cpp:30: the EventHash is not touched) with the per-type laps / opponents, then each widget's Draw
                    var fresh = FEDatabaseData.NewQuickRace();
                    var cur = FEDb.Data.QuickRace;
                    for (int i = 0; i < fresh.Length && i < cur.Length; i++)
                    {
                        fresh[i].EventHash = cur[i].EventHash;
                        fresh[i].EventName = cur[i].EventName;
                    }
                    FEDb.Data.QuickRace = fresh;
                    RefreshWidgets();
                });
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// UIQRBrief on Quick_Race_Brief.fng ("Quick Play", quickrace/uiQRBrief.cpp; PC ctor 0x7BF270, Setup 0x7BA1A0, RefreshHeader
    /// 0x7B42B0, NotificationMessage 0x7B4580): stock cars (filter 0xF0001) whose frontend UnlockedAt is within [bin − 1, bin + 3]
    /// and whose frontend collection is not the root "vehicles" (0xC1C32DAF), and every race that is not a challenge / jump /
    /// tollbooth, not 19.8.31, IsEventAvailable and quick-race unlocked. Setup queues PAD_BUTTON5 (key 1): a random car and track,
    /// 30 SCREEN_TICKs of the next car / track (circular) with GetRandomRaceOptions (FED:995), RefreshHeader and the
    /// UISND_QUICK_GAMBLE_BLIP (0x8B) each tick; the car renders only after the spin (DisableCarRendering, then
    /// CarViewer::SetRideInfo at the end with SetRandomPaint / SetRandomParts [?: random paint / parts not ported]), and a car
    /// still locked for the career shows the unlock group 0xFE8FDBF7 "…blacklist_rival_%02d_aka…" (0x4EF2A115). RefreshHeader also
    /// draws the TwoStageSliders ACCELERATION / TOPSPEED / HANDLING (0..1, range 160; value = preview = EstimatePerformance).
    /// Logos: GetManuLogoHash / GetLogoHash, GENERICPLACEHOLDER when the texture is missing. Enter races it with the car's NOS
    /// maxed; Back → MainMenu_Sub (the quick race menu; 0x7B45CE).
    /// </summary>
    public sealed class QuickRaceBriefScreen : FEScreen
    {
        public override string Package => "Quick_Race_Brief";
        public override bool ShowCar => true;
        readonly List<string> _cars = new List<string>();
        readonly List<RaceEvent> _tracks = new List<RaceEvent>();
        int _car, _track, _spin;
        readonly RaceSettings _settings = new RaceSettings { CopsOn = false };
        static readonly System.Random Rng = new System.Random();
        readonly TwoStageSlider _accel = new TwoStageSlider(), _topSpeed = new TwoStageSlider(), _handling = new TwoStageSlider();

        public override void Open()
        {
            var c = GameSession.Career;
            int bin = GameSession.Profile?.Bin ?? 16;
            if (c != null)
            {
                foreach (var e in c.CarLot)
                    if (e.UnlockedAt >= bin - 1 && e.UnlockedAt <= bin + 3) _cars.Add(e.Car);
                foreach (var ev in RaceDatabase.AllRaces())
                {
                    var t = FEDb.ToGRace(ev.Type);
                    if (t == GRaceType.None || t == GRaceType.Challenge || t == GRaceType.Tollbooth || ev.Start == null) continue;
                    if (ev.EventId == RaceIndex.BurgerKing) continue;
                    if (QuickRaceUnlock.IsEventAvailable(ev) && QuickRaceUnlock.IsTrackUnlocked(ev)) _tracks.Add(ev);
                }
            }
            if (_cars.Count == 0) _cars.Add("bmwm3gtr");
            _accel.Init(View, "ACCELERATION", 0f, 1f, 0f, 0f, 0f, 160f);
            _topSpeed.Init(View, "TOPSPEED", 0f, 1f, 0f, 0f, 0f, 160f);
            _handling.Init(View, "HANDLING", 0f, 1f, 0f, 0f, 0f, 160f);
            Spin();
        }

        void Spin()
        {
            _car = Rng.Next(_cars.Count);
            _track = _tracks.Count > 0 ? Rng.Next(_tracks.Count) : -1;
            _spin = 30;
            SafehouseUi.Message(View, 0xA05A328E);   // LOADERAPPEAR
            View.Play(View.Find(0xFE8FDBF7), FEView.Hide, true);
        }

        /// <summary>GetRandomRaceOptions (FED:995).</summary>
        void RandomOptions(RaceType type)
        {
            var s = _settings;
            s.CatchUp = true; s.CopDensity = Rng.Next(4); s.AISkill = 1; s.NumOpponents = Rng.Next(3) + 1;
            s.NumLaps = type == RaceType.Knockout ? s.NumOpponents : type == RaceType.Circuit ? Rng.Next(5) + 1 : 1;
            s.TrafficDensity = Rng.Next(4);
            s.TrackDirection = 0;   // bRandom(1) is always 0
        }

        void Refresh()
        {
            string car = _cars[_car];
            var placeholder = FrontEndData.TextureByName("GENERICPLACEHOLDER");
            var badge = CarLotScreen.Badge(car) ?? FrontEndData.TextureByName("GENERIC_LOGO_128") ?? placeholder;
            if (badge) View.SetTexture(View.Find(0x3E01AD1D), badge);
            var logo = FrontEndData.TextureByName("SECONDARY_LOGO_" + car.ToUpperInvariant()) ?? FrontEndData.TextureByName("GENERIC_LOGO_256") ?? placeholder;
            if (logo) View.SetTexture(View.Find(0xB05DD708), logo);
            UpdateSliders(car);
            if (_track < 0) return;
            var ev = _tracks[_track];
            var t = FEDb.ToGRace(ev.Type);
            SafehouseUi.SetLang(View, 0xB5154998, FEDb.L(FEDatabaseData.RaceNameHash(t)));
            var icon = FrontEndData.Texture(FEDatabaseData.RaceIconHash(t));
            if (icon) View.SetTexture(View.Find(0x2521E5EB), icon);
            SafehouseUi.SetLang(View, 0xB5154999, FrontEndData.Text(FrontEndData.Hash("TRACKNAME_" + ev.EventId), null) ?? ev.EventId);
            bool km = FEDb.Data.Gameplay.SpeedoUnits == 1;
            SafehouseUi.SetLang(View, 0xB515499A, $"{Localize.Fixed(RaceListScreen.LengthOf(ev) * 0.001f, 1, true)} {FEDb.L(km ? 0x8569A26Au : 0x867DCFD9u)}");   // (sic: km value with either unit)
            SafehouseUi.SetLang(View, 0xB515499B, t == GRaceType.Circuit || t == GRaceType.Knockout ? _settings.NumLaps.ToString() : "--");
            SafehouseUi.SetLang(View, 0xB515499C, _settings.NumOpponents.ToString());
            uint traffic = t == GRaceType.P2P || t == GRaceType.Drag ? 0x7F2F7AD6u : _settings.TrafficDensity switch { 1 => 0x73C615A3u, 2 => 0xA2CCA838u, 3 => 0x61D1C5A5u, _ => 0x8CDC3937u };
            SafehouseUi.SetLang(View, 0xB515499D, FEDb.L(traffic));
            // (sic) the AI skill is written to 0xB515499E and then overwritten by catch-up (uiQRBrief.cpp:103-110)
            SafehouseUi.SetLang(View, 0xB515499E, FEDb.L(_settings.CatchUp ? 0x417B2604u : 0x70DFE5C2u));
        }

        /// <summary>UpdateSliders (uiQRBrief.cpp:137): value and preview = the car's EstimatePerformance rating.</summary>
        void UpdateSliders(string car)
        {
            var c = new OwnedCar { Car = car };
            float a = CarSelectScreen.Rating(c, 0), t = CarSelectScreen.Rating(c, 1), h = CarSelectScreen.Rating(c, 2);
            _accel.SetValue(a); _accel.SetPreview(a); _accel.Draw();
            _topSpeed.SetValue(t); _topSpeed.SetPreview(t); _topSpeed.Draw();
            _handling.SetValue(h); _handling.SetPreview(h); _handling.Draw();
        }

        public override void Tick(MenuInput input)
        {
            if (_spin > 0)
            {
                _car = (_car + 1) % _cars.Count;
                if (_tracks.Count > 0) _track = (_track + 1) % _tracks.Count;
                if (_track >= 0) RandomOptions(_tracks[_track].Type);
                Refresh();
                _spin--;
                NfsMw.Audio.FeSound.Play(NfsMw.Audio.FeSoundId.QuickGambleBlip);   // PlayUISoundFX(0x8B)
                if (_spin == 0)
                    FE.Showroom.Show(_cars[_car], new Color(0.8f, 0.82f, 0.84f));   // CarViewer::SetRideInfo after the spin [?: random paint]
                if (_spin == 0 && _track >= 0)
                {
                    var lot = GameSession.Career.CarLot.FirstOrDefault(x => x.Car == _cars[_car]);
                    int bin = GameSession.Profile?.Bin ?? 16;
                    if (lot != null && lot.UnlockedAt < bin)
                    {
                        View.Play(View.Find(0xFE8FDBF7), FEView.Appear, true);
                        SafehouseUi.SetLang(View, 0xFE8FDBF7, SafehouseUi.UnlockByRival(lot.UnlockedAt + 1));
                    }
                }
                return;
            }
            if (input.Key1) { Spin(); return; }
            if (input.Accept && _track >= 0)
            {
                var car = new OwnedCar { Car = _cars[_car] };
                CarParts.Migrate(car);
                car.Nitrous = CarParts.NumPackages(car.Car, ShopRules.Nos);   // NOS at its max level
                GameSession.QuickCar = car;
                FEDb.NumPlayers = 1;
                QuickRaceStarter.Start(_tracks[_track], _settings);
                return;
            }
            if (input.Back) FE.Replace(new QuickRaceMenuScreen());
        }
    }
}
