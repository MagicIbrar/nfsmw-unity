using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.InputSystem;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>
    /// HUD_SingleRace widgets GameHud did not drive (specs/fe_career_screens.md §5, hud.md §4.8, 4.13, 4.16–4.19):
    /// Hud_TimeExtension (tollbooth messages and the tollbooth counter), Hud_RadarDetector, Hud_CostToState,
    /// Hud_MilestoneBoard, Hud_Infractions and the leaderboard's second page; plus the HUD's free-roam pad actions
    /// (map, SMS, Blacklist) and the rap-sheet pursuit watcher. GameHud creates one for the single-player HUD and ticks it
    /// after its own update.
    /// </summary>
    public sealed class HudExtras
    {
        readonly FEView _v;
        readonly PlayerCar _car;
        static readonly uint Leave = K.Leave;

        readonly HudGenericMessage _msg;

        public HudExtras(FEView view, PlayerCar car, HudGenericMessage msg)
        {
            _v = view;
            _car = car;
            _msg = msg;
            // CTS_GROUP gets HIDE in its ctor; the milestone and infraction groups start hidden (their INIT chains to HIDE)
            Play(0xD60FC075, FEView.Hide);
            foreach (var n in new[] { "MILESTONE_INFO_GROUP", "MILESTONE_ICON_GROUP", "GENERIC_ICONS", "TOTAL_INFRACTIONS_TEXT" }) Play(FrontEndData.Hash(n), FEView.Hide);
            for (int i = 1; i <= 4; i++) Play(FrontEndData.Hash("INFRACTION_GROUP_" + i), FEView.Hide);
            Play(0xBDA30587, FEView.Hide);
            Play(0xC0F39F88, FEView.Hide);
            // Reputation ctor (FeReputation.cpp:8; created in the career context only): HIDE on its group EA903012
            if (GameSession.Mode == GameMode.Career) _v.SetScript(N(0xEA903012), FEView.Hide, true);
        }

        FEView.Node N(uint h) => _v.Find(h);
        FEView.Node N(string name) => _v.Find(name);

        void Play(uint h, uint script, bool show = true)
        {
            var n = N(h);
            if (n == null) return;
            if (show) _v.SetVisible(n, true);
            _v.Play(n, script);
        }

        /// <summary>FEngIsScriptSet (scripts stay set after their end, as in the exe).</summary>
        bool Playing(FEView.Node n, uint script) => _v.IsScriptSet(n, script);

        /// <summary>The exe's per-frame widget counters (radar fill +0.1, CTS / Reputation 120 frames) at 60 Hz frames [?: the
        /// PC frame rate they were tuned for], so the port's render rate does not speed them up.</summary>
        float _tickAcc;
        int _frames;

        public void Tick(RaceManager race)
        {
            _tickAcc += Time.unscaledDeltaTime * 60f;
            _frames = Mathf.Min(8, (int)_tickAcc);
            _tickAcc -= _frames;
            RapSheetRecord.Watch();
            var police = Systems.Police;
            bool pursuit = police != null && police.InPursuit && race == null;
            // FEngHud::Update runs each widget's Update only while its feature bits are on (the TimeExtension gates itself
            // after counting the tollbooths)
            TimeExtension(race);
            if (On(0x200000)) Radar(race, police, pursuit);
            if (On(0x1000)) CostToState(police, pursuit);
            if (On(0x1000) && GameSession.Mode == GameMode.Career) Reputation();
            if (On(0x400000000UL)) MilestoneBoard(police, pursuit);
            if (On(0x200000000UL)) Infractions(police, pursuit);
            if (On(0x18)) Leaderboard(race);
        }

        // ---- Hud_TimeExtension (FeTimeExtension.cpp; PC Update 0x57B780, mask 0x02000000) ------------------------------

        float _lastTimeLeft = -1, _timeToShow, _timeExtensionAt = -1, _nextTollboothAt = -1;
        uint _teScript;
        int _tollsPassed;
        /// <summary>RaceInformation's mPlayerTollboothNumber / mNumTollbooths [?: the feeder counts time-bonus jumps and the
        /// race's time-bonus checkpoints].</summary>
        public int TollsPassed => _tollsPassed;
        public int TollsTotal { get; private set; }
        RaceManager _tollRace;
        static readonly uint TimerIcon = EaHash.HashUpper("TIMER_ICON");

        /// <summary>
        /// TimeExtension::Update (tollbooth races only): a pending time bonus waits for the message line to be free, then
        /// "TIME BONUS!\n+&lt;time&gt;" (0x1C074E14; script 0x8AB83EDB, TIMER_ICON with 0x609F6B15, Priority_3) and starts a
        /// 2 s timer, after which "NEXT TOLLBOOTH\n&lt;time left&gt;" (0x171471B4) is requested every frame (single frame,
        /// Priority_3) for 2 s. With 10 s or less left: "HURRY!\n&lt;time&gt;" (0x862A0519) every frame, single frame,
        /// Priority_5, script 0x821E6378 (10..5 s) then 0x4F79CBA2 (≤ 5 s); over 10 s a hurry script is dropped.
        /// RequestTimeExtensionMessage (a bonus): zooms a showing message out (E1C034FC) and clears the script.
        /// [?] The feeders are not decompiled: the bonus is seen as a jump of RaceManager.TimeLeft, the "lap time" is the
        /// time left. Times print with Timer::PrintToString(4) (GameHud.PrintTime), untrimmed as in the exe.
        /// The tollbooths passed / total (RaceInformation's counter, GameHud) are counted here.
        /// </summary>
        void TimeExtension(RaceManager race)
        {
            bool toll = race != null && race.Event != null && race.Event.Type == RaceType.Tollbooth;
            if (!toll)
            {
                _lastTimeLeft = -1;
                _tollRace = null;
                return;
            }
            if (_tollRace != race) { _tollRace = race; _tollsPassed = 0; _lastTimeLeft = race.TimeLeft; _timeToShow = 0; _timeExtensionAt = _nextTollboothAt = -1; _teScript = 0; }
            int total = Mathf.Max(1, race.Event.Checkpoints.Count(c => c.TimeBonus > 0));
            if (total <= 1) total = Mathf.Max(1, race.Event.Checkpoints.Count);
            TollsTotal = total;
            if (race.Countdown) { _lastTimeLeft = race.TimeLeft; return; }

            float left = race.TimeLeft;
            if (_lastTimeLeft >= 0 && left > _lastTimeLeft + 0.5f)
            {
                // RequestTimeExtensionMessage(player, timeToShow)
                _tollsPassed++;
                if (_msg.Showing) _msg.ZoomOut();
                _timeToShow = _lastTimeLeft;
                _teScript = 0;
            }
            _lastTimeLeft = left;
            if (!On(0x02000000)) return;

            if (_timeToShow > 0f)
            {
                if (!_msg.Showing)
                {
                    _timeExtensionAt = Time.time;
                    _msg.Request($"{K.L(0x1C074E14, "TIME BONUS!")}\n+{GameHud.PrintTime(left - _timeToShow, 4)}", false, HudGenericMessage.ScriptNormal, TimerIcon, 0x609F6B15, HudGenericMessage.Priority3);
                    _timeToShow = 0f;
                }
                return;
            }
            if (_timeExtensionAt >= 0 && Time.time - _timeExtensionAt >= 2f)
            {
                _timeExtensionAt = -1;
                _nextTollboothAt = Time.time;
                return;
            }
            float lap = left;
            if (lap <= 0f) return;
            if (lap > 10f && (_teScript == 0x821E6378 || _teScript == 0x4F79CBA2)) _teScript = 0;
            if (lap <= 10f)
            {
                string text = $"{K.L(0x862A0519, "HURRY")}!\n{GameHud.PrintTime(lap, 4)}";
                if (lap <= 5f) { if (_teScript == 0 || _teScript == 0x821E6378) _teScript = 0x4F79CBA2; }
                else if (_teScript == 0) _teScript = 0x821E6378;
                _msg.Request(text, true, _teScript, 0, 0, HudGenericMessage.Priority5);
            }
            else if (_nextTollboothAt >= 0)
            {
                if (Time.time - _nextTollboothAt >= 2f) { _teScript = 0; _nextTollboothAt = -1; }
                else
                {
                    _teScript = HudGenericMessage.ScriptNormal;
                    _msg.Request($"{K.L(0x171471B4, "NEXT TOLLBOOTH")}\n{GameHud.PrintTime(left, 4)}", true, _teScript, TimerIcon, 0x609F6B15, HudGenericMessage.Priority3);
                }
            }
        }

        /// <summary>FEngHud::Update runs a widget only while its feature bits are on.</summary>
        public System.Func<ulong, bool> On = _ => true;

        // ---- Hud_RadarDetector (FeRadarDetector.cpp) -------------------------------------------------------------

        float _led = 0.3f, _cycleStart = -1;

        /// <summary>if (!FEngIsScriptSet(n, s)) FEngSetScript(n, s, true) — scripts only, visibility is the feature bits'.</summary>
        void SetIfNot(FEView.Node n, uint script)
        {
            if (n != null && !_v.IsScriptSet(n, script)) _v.SetScript(n, script, true);
        }

        /// <summary>FEngSetMultiImageBottomRightUVs(img, uv, 0) (PC 0x524C00): layer 0's (the first mask's) bottom-right UV,
        /// keeping its top-left.</summary>
        void LedUVs(FEView.Node n, float u, float v)
        {
            if (n != null) _v.SetMultiImageUVs(n, 0, n.Data.M1U0, n.Data.M1V0, u, v);
        }

        /// <summary>
        /// RadarDetector::Update (FeRadarDetector.cpp; PC 0x566170): group 0x062743F5 and backing 0x839E7D77 APPEAR out of a
        /// pursuit or in cooldown, else LEAVE (HIDE when still in INIT); the rear-view backing 0x9EE06631 only with the
        /// mirror camera (not ported: HIDE). With a target: LED fill +0.1 a frame up to 1, back to 0.3 every
        /// 1.5·max(range, TWK_RadarDetectorMinThreshold) s (beep = MMiscSound(0)); both LED multi-images get layer-0 BR-UV
        /// (fill, 1); the arrow turns to the bearing; a speed-trap camera plays SPEEDTRAP (0xFA44E387) on arrow, icon and
        /// LEDs, else INIT. No target: UV (0, 1), arrow HIDE, icon INIT.
        /// [?] The caller of SetTarget is not decompiled: the target here is the nearest cop car within 250 m (range =
        /// distance / 250) or, failing that, a speed-trap camera within 250 m; TWK_RadarDetectorMinThreshold is taken as 0.1.
        /// </summary>
        void Radar(RaceManager race, PursuitSystem police, bool pursuit)
        {
            var group = N(0x062743F5);
            var backing = N(0x839E7D77);
            var mirrorBacking = N(0x9EE06631);
            if (group == null) return;
            bool cooling = police != null && police.InCooldown;
            SetIfNot(mirrorBacking, FEView.Hide);
            if (!pursuit || cooling)
            {
                SetIfNot(backing, FEView.Appear);
                SetIfNot(group, FEView.Appear);
            }
            else
            {
                if (_v.IsScriptSet(backing, FEView.Init)) _v.Play(backing, FEView.Hide);
                else if (!_v.IsScriptSet(backing, Leave) && !_v.IsScriptSet(backing, FEView.Hide)) _v.Play(backing, Leave);
                if (_v.IsScriptSet(group, FEView.Init)) _v.Play(group, FEView.Hide);
                else if (!_v.IsScriptSet(group, Leave) && !_v.IsScriptSet(backing, FEView.Hide)) _v.Play(group, Leave);
            }
            // the target (SetTarget feeder, see summary)
            float range = 0, bearing = 0;
            bool camera = false;
            if (_car)
            {
                float best = 250f;
                Vector3? at = null;
                if (police != null)
                    foreach (var c in police.CopCars)
                    {
                        if (!c) continue;
                        float d = Vector3.Distance(c.transform.position, _car.transform.position);
                        if (d < best) { best = d; at = c.transform.position; }
                    }
                if (at == null && GameSession.Mode == GameMode.Career && GameSession.CurrentBin != null)
                    foreach (var t in GameSession.CurrentBin.SpeedTraps)
                    {
                        if (t.Point == null) continue;
                        var p = Coords.ToUnity(t.Point.Position);
                        float d = Vector3.Distance(p, _car.transform.position);
                        if (d < best) { best = d; at = p; camera = true; }
                    }
                if (at != null)
                {
                    range = best / 250f;
                    var to = at.Value - _car.transform.position;
                    var f = _car.transform.forward;
                    bearing = Mathf.DeltaAngle(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
                }
            }
            var leds = new[] { N(0x69AA01E7), N(0x9F59065A) };
            var arrow = N("Radar_DirectionArrow");
            var icon = N("Radar_Icon");
            if (range > 0 && !(pursuit && !cooling))
            {
                const float minThreshold = 0.1f;
                float r = Mathf.Max(range, minThreshold);
                if (_cycleStart < 0) _cycleStart = Time.time;
                _led = Mathf.Min(1f, _led + 0.1f * _frames);
                if (Time.time - _cycleStart > r * 1.5f) { _cycleStart = Time.time; _led = 0.3f; NfsMw.Audio.MiscSound.Play(0); }   // the ping
                foreach (var l in leds) LedUVs(l, _led, 1f);
                _v.SetRotationZ(arrow, bearing);
                uint s = camera ? 0xFA44E387u : FEView.Init;
                SetIfNot(arrow, s); SetIfNot(icon, s);
                foreach (var l in leds) SetIfNot(l, s);
                return;
            }
            _cycleStart = -1;
            foreach (var l in leds) LedUVs(l, 0f, 1f);
            if (arrow != null && !_v.IsScriptSet(arrow, FEView.Hide)) _v.Play(arrow, FEView.Hide);
            if (icon != null && !_v.IsScriptSet(icon, FEView.Init)) _v.Play(icon, FEView.Init);
        }

        // ---- Hud_Reputation (FeReputation.cpp; PC ctor 0x59D2A0, Update 0x5669B0, mask 0x1000) ------------------------

        int _repCareer, _repFrames;

        /// <summary>
        /// Reputation::SetReputationCareer (a change shows the board for 0x78 = 120 frames) and Update: while frames are left
        /// the title gets 0x7D0171E4, 9B0AC8CA "%$d" of the value, APPEAR on EA903012 (unless set); afterwards LEAVE once.
        /// [?] The feeder (LocalPlayer::UpdateHud) is not traced: the value is the career profile's bounty.
        /// </summary>
        void Reputation()
        {
            int rep = GameSession.Profile?.Bounty ?? 0;
            if (rep != _repCareer) { _repCareer = rep; _repFrames = 0x78; }
            var grp = N(0xEA903012);
            if (_repFrames > 0)
            {
                _repFrames = Mathf.Max(0, _repFrames - _frames);
                _v.SetLanguageHash(N(0x41A55ECF), 0x7D0171E4);
                _v.Printf(N(0x9B0AC8CA), K.Format("%$d", _repCareer));
                if (!_v.IsScriptSet(grp, FEView.Appear)) _v.SetScript(grp, FEView.Appear, true);
            }
            else if (_v.IsScriptSet(grp, FEView.Appear)) _v.SetScript(grp, Leave, true);
        }

        // ---- Hud_CostToState (FeCostToState.cpp) ------------------------------------------------------------------

        int _ctsShown, _ctsFrames;
        bool _ctsAppeared;

        void CostToState(PursuitSystem police, bool pursuit)
        {
            if (!pursuit)
            {
                if (_ctsShown != 0 || _ctsAppeared) { _ctsShown = 0; _ctsFrames = 0; if (_ctsAppeared) Play(0xD60FC075, Leave, false); _ctsAppeared = false; }
                return;
            }
            int v = police.Stats.CostToState;
            if (v > _ctsShown) { _ctsShown = v; _ctsFrames = 120; }
            if (_ctsFrames > 0)
            {
                _ctsFrames = Mathf.Max(0, _ctsFrames - _frames);
                K.Text(_v, 0x64247241, K.L(0x3DD874C5, "PURSUIT CTS:"));
                K.Text(_v, 0x3FF5F33C, _ctsShown.Grouped());
                if (!_ctsAppeared) { _ctsAppeared = true; Play(0xD60FC075, FEView.Appear); }
                if (_ctsFrames == 0) { _ctsAppeared = false; Play(0xD60FC075, Leave, false); }
            }
        }

        // ---- Hud_MilestoneBoard (FeMilestoneBoard.cpp) ------------------------------------------------------------

        bool _msShown;
        int _msSel = -1;
        float _msNext;
        readonly float[] _msLast = new float[4];

        void MilestoneBoard(PursuitSystem police, bool pursuit)
        {
            var bin = GameSession.Mode == GameMode.Career ? GameSession.CurrentBin : null;
            var list = bin?.Milestones.Take(4).ToList();
            var p = GameSession.Profile;
            bool on = pursuit && list != null && list.Count > 0 && p != null;
            if (!on)
            {
                if (_msShown) { _msShown = false; Play(FrontEndData.Hash("MILESTONE_INFO_GROUP"), Leave, false); Play(FrontEndData.Hash("MILESTONE_ICON_GROUP"), Leave, false); }
                _msSel = -1;
                return;
            }
            if (!_msShown)
            {
                _msShown = true;
                Play(FrontEndData.Hash("MILESTONE_INFO_GROUP"), FEView.Appear);
                Play(FrontEndData.Hash("MILESTONE_ICON_GROUP"), FEView.Appear);
                for (int i = 0; i < 4; i++) _msLast[i] = -1;
            }
            var s = police.Stats;
            bool Done(Milestone m) => p.Milestones.Contains(m.Name) || m.Met(s.Value(m.Type));
            K.Text(_v, 0x894662C5, list.Count(Done).ToString());
            var incomplete = Enumerable.Range(0, list.Count).Where(i => !Done(list[i])).ToList();
            for (int i = 0; i < 4; i++)
            {
                var icon = N("MILSTONE_ICON_" + (i + 1));
                var back = N("MILESTONE_ICON_BACKING_" + (i + 1));
                if (i >= list.Count) { _v.Play(icon, FEView.Hide); _v.SetVisible(icon, false); _v.SetVisible(back, false); continue; }
                var m = list[i];
                var tex = FrontEndData.Texture(MilestonesScreen.IconFor(m.Type));
                if (icon != null)
                {
                    if (tex) _v.SetTexture(icon, tex);
                    _v.SetVisible(icon, true);
                    icon.Data.A = Done(m) ? 255 : 128;
                }
                _v.SetVisible(back, true);
                // a value change on an incomplete, non-time milestone selects it and pulses (FeMilestoneBoard)
                float val = s.Value(m.Type);
                bool time = m.Type == "pursuit_length" || m.Type == "pursuit_evasion_time";
                if (_msLast[i] >= 0 && val != _msLast[i] && !time && !Done(m))
                {
                    _msSel = i;
                    _msNext = Time.time + 5f;
                    _v.Play(back, K.Pulse);
                    Play(0x5C697702, K.Pulse);
                    Play(0xF4405EC0, 0xD6C950A0);
                }
                _msLast[i] = val;
            }
            if (incomplete.Count > 0)
            {
                if (_msSel < 0 || !incomplete.Contains(_msSel)) { _msSel = incomplete[0]; _msNext = Time.time + 5f; }
                else if (incomplete.Count > 1 && Time.time >= _msNext)
                {
                    // cycle every 5 s: AFF37F61 out, D6C950A0 in on the details
                    _msSel = incomplete[(incomplete.IndexOf(_msSel) + 1) % incomplete.Count];
                    _msNext = Time.time + 5f;
                    Play(0xF4405EC0, 0xD6C950A0);
                }
                var m = list[_msSel];
                float cur = Mathf.Min(s.Value(m.Type), m.Goal);
                bool timeType = m.Type == "pursuit_length" || m.Type == "pursuit_evasion_time";
                K.Text(_v, 0xC3E48FBF, timeType ? $"{GameHud.TimeString(m.Type == "pursuit_evasion_time" ? police.PursuitTime : cur).Trim()}/{GameHud.TimeString(m.Goal).Trim()}" : $"{cur.Grouped()}/{m.Goal.Grouped()}");
                _v.SetVisible(N(0xF4405EC0), true);
                _v.SetVisible(N(0x5C697702), true);
                for (int i = 0; i < list.Count; i++)
                {
                    var back = N("MILESTONE_ICON_BACKING_" + (i + 1));
                    if (back == null || Playing(back, K.Pulse)) continue;
                    uint want = i == _msSel ? FEView.Highlight : FEView.Init;
                    if (!Playing(back, want)) _v.Play(back, want);
                }
            }
            else
            {
                _v.SetVisible(N(0xF4405EC0), false);
                _v.SetVisible(N(0x5C697702), false);
            }
        }

        // ---- Hud_Infractions (FeInfractions.cpp) ------------------------------------------------------------------

        PursuitStats _infStats;
        int _infSeen;
        readonly float[] _infUntil = new float[4];
        bool _infIcons;

        void Infractions(PursuitSystem police, bool pursuit)
        {
            if (police != null && police.Stats != _infStats) { _infStats = police.Stats; _infSeen = 0; }
            if (_infStats != null && _infStats.Infractions.Count > _infSeen)
            {
                for (; _infSeen < _infStats.Infractions.Count; _infSeen++)
                {
                    string name = _infStats.Infractions[_infSeen];
                    int t = RapSheetRecord.TypeOf(name);
                    string text = t >= 0 ? K.L(RapSheetRecord.Types[t].label, name) : name;
                    // the first INFRACTION_GROUP_i currently free gets APPEAR and the text
                    int slot = System.Array.FindIndex(_infUntil, u => u <= Time.time);
                    if (slot < 0) slot = 0;
                    var g = N("INFRACTION_GROUP_" + (slot + 1));
                    if (g != null)
                    {
                        foreach (var d in _v.Descendants(g)) _v.SetText(d, text);
                        _v.SetVisible(g, true);
                        _v.Play(g, FEView.Appear);
                    }
                    _infUntil[slot] = Time.time + 4f;
                }
                Play(FrontEndData.Hash("GENERIC_ICONS"), FEView.Appear);
                Play(0xBDA30587, FEView.Appear);
                Play(0xC0F39F88, FEView.Appear);
                _infIcons = true;
                var total = N("TOTAL_INFRACTIONS_TEXT");
                _v.SetText(total, _infStats.Infractions.Count.ToString());
                _v.SetVisible(total, true);
                _v.Play(total, FEView.Show);
            }
            for (int i = 0; i < 4; i++)
                if (_infUntil[i] > 0 && Time.time > _infUntil[i])
                {
                    _infUntil[i] = 0;
                    Play(FrontEndData.Hash("INFRACTION_GROUP_" + (i + 1)), Leave, false);
                }
            if (_infIcons && _infUntil.All(u => u <= 0))
            {
                // GENERIC_ICONS leaves once all four rows are back in HIDE
                _infIcons = false;
                Play(FrontEndData.Hash("GENERIC_ICONS"), Leave, false);
                Play(0xBDA30587, Leave, false);
                Play(0xC0F39F88, Leave, false);
                Play(FrontEndData.Hash("TOTAL_INFRACTIONS_TEXT"), FEView.Hide, false);
            }
        }

        // ---- Hud_LeaderBoard (FeLeaderBoard.cpp; PC 0x58C160) ----------------------------------------------------

        int _framesToToggle = 90;
        bool _showTimes;

        /// <summary>
        /// LeaderBoard::Update: LeaderBoardGroup INIT; every 90 frames all rows play LEAVE, and when a LEAVE has finished
        /// they APPEAR and the page flips. Page A: racer name (LBData_i) and position (LeaderText_i, "%$d"); page B:
        /// BUSTED / K.O., the speedtrap total, "-----" for the player, else ±(pct gap · race length) in M or FT. Rows past the
        /// racer count (all when ≤ 1 racer) are invisible; the player's backing plays Highlight, the others INIT.
        /// [?] The leaderboard option (P+0x85) is taken as on.
        /// </summary>
        float _frameAcc;

        void Leaderboard(RaceManager race)
        {
            // LeaderBoard::Update (FeLeaderBoard.cpp:38): runs whenever the widget's feature bits are on — 0x10 (split times
            // on) is set out of races too, so in free roam it runs with no racers and makes every row invisible (before
            // round 9 the port returned early there and the package's WWWW rows showed)
            var group = N("LeaderBoardGroup");
            if (group == null || !FEDb.Data.Player(0).LeaderboardOn) return;
            if (!_v.IsScriptSet(group, FEView.Init)) _v.SetScript(group, FEView.Init, true);
            var order = race != null ? race.Entrants.OrderBy(e => e.Place <= 0 ? 99 : e.Place).Take(4).ToList() : new List<RaceManager.Entrant>();
            int numRacers = race != null ? race.Entrants.Count : 0;
            var text = new FEView.Node[4];
            for (int i = 0; i < 4; i++) text[i] = N("LBData_" + (i + 1));
            // mNumFramesBeforeTogglingPlayerTimes counts the exe's frames (90 = 1.5 s at its 60 Hz) [?: the port counts
            // 60 Hz frames so a faster render rate does not restart LEAVE before the 160 ms APPEAR has run, which left the
            // rows squashed]
            _frameAcc += Time.unscaledDeltaTime * 60f;
            int frames = Mathf.Min(8, (int)_frameAcc);
            _frameAcc -= frames;
            for (int f = 0; f < frames; f++)
            {
                if (--_framesToToggle > 0) continue;
                bool toggle = false;
                for (int i = 0; i < numRacers && i < 4; i++)
                {
                    if (_framesToToggle == 0) _v.SetScript(text[i], Leave, true);
                    else if (_v.IsScriptSet(text[i], Leave) && !_v.IsScriptRunning(text[i], Leave) && !_v.IsScriptSet(text[i], FEView.Appear))
                    {
                        _v.SetScript(text[i], FEView.Appear, true);
                        toggle = true;
                    }
                }
                if (toggle) { _framesToToggle = 90; _showTimes = !_showTimes; }
            }
            var me = race?.Entrants.Find(e => e.Car == _car);
            int playerIndex = me != null ? order.IndexOf(me) : -1;
            bool metric = Settings.Kmh;
            int clearFrom = numRacers;
            if (numRacers > 1)
            {
                float raceLength = race.RouteLength * (metric ? 1f : 3.2808f);
                for (int i = 0; i < numRacers && i < 4; i++)
                {
                    var e = order[i];
                    if (_showTimes)
                    {
                        string t;
                        if (e.Busted) t = K.L(0x532B5186, "BUSTED");
                        else if (e.Out) t = K.L(0x5D82DBA2, "K.O.");
                        else if (race.SpeedTrapRace) t = $"{(metric ? e.Points : e.Points / 3.6f * 2.23699f).Grouped()} {K.L(metric ? 0x8569A25Fu : 0x8569AB44u, metric ? "KM/H" : "MPH")}";
                        else if (i == playerIndex) t = "-----";
                        else
                        {
                            float mine = me != null ? me.PctRaceComplete : 0;
                            string unit = metric ? K.L(0xE2078322, "M") : K.L(0x2E8496B1, "FT");
                            float diff = Mathf.Abs(e.PctRaceComplete - mine) * 0.01f * raceLength;
                            t = $"{(e.PctRaceComplete >= mine ? "+" : "-")}{diff.Grouped()} {unit}";
                        }
                        _v.Printf(text[i], t);
                    }
                    else
                    {
                        _v.Printf(text[i], (e.Human && !e.Remote ? (GameSession.Profile?.Name ?? e.Name) : e.Name).ToUpperInvariant());
                        _v.Printf(N("LeaderText_" + (i + 1)), e.Place.Grouped());
                    }
                }
            }
            else clearFrom = 0;
            // rows past the racer count: FEngSetInvisible (the exe never makes them visible again here; the widget's
            // Toggle does when its bits change)
            for (int i = clearFrom; i <= 3; i++)
            {
                _v.SetInvisible(text[i]);
                _v.SetInvisible(N("LeaderText_" + (i + 1)));
                _v.SetInvisible(N("OL_ICON_" + (i + 2)));
                _v.SetInvisible(N("LBBacking_" + (i + 1)));
            }
            for (int i = 0; i <= 3; i++)
            {
                var back = N("LBBacking_" + (i + 1));
                uint want = i == playerIndex ? FEView.Highlight : FEView.Init;
                if (back != null && !_v.IsScriptSet(back, want)) _v.SetScript(back, want, true);
            }
        }

        // ---- HUD pad actions (FEPkg_Hud.cpp:827-841) ---------------------------------------------------------------

        /// <summary>
        /// FEngHud::JoyHandle (FEPkg_Hud.cpp:762): only while the HUD shows anything (CurrentHudFeatures != 0) and in roaming
        /// play mode: HUDACTION_PAD_LEFT → world map (EWorldMapOn), PAD_RIGHT → SMS inbox (EShowSMS(-1)), PAD_DOWN → in-game
        /// Blacklist (ERaceSheetOn(0)) unless D-Day. PC keys [?]: M, T and L; a gamepad's d-pad left / right / down.
        /// (Before round 9 the SMS and Blacklist also needed the free-roam story stage and the Blacklist no pursuit [ours].)
        /// [?] The SMS inbox still needs a career profile (the port has no SMS data outside it).
        /// </summary>
        public static void HudActions(bool hudOn = true)
        {
            if (!hudOn || RaceManager.Active != null || NisPlayer.Active || StoryCard.Showing) return;
            if (GameBootstrap.Instance == null || GameBootstrap.Instance.Player == null) return;
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            bool map = (kb != null && kb.mKey.wasPressedThisFrame) || (pad != null && pad.dpad.left.wasPressedThisFrame);
            bool sms = (kb != null && kb.tKey.wasPressedThisFrame) || (pad != null && pad.dpad.right.wasPressedThisFrame);
            bool sheet = (kb != null && kb.lKey.wasPressedThisFrame) || (pad != null && pad.dpad.down.wasPressedThisFrame);
            if (!map && !sms && !sheet) return;
            bool career = GameSession.Mode == GameMode.Career && GameSession.Profile != null;
            if ((sms && !career) || (sheet && MoviePlayer.IsDDay())) return;
            var fe = FrontEnd.Create();
            if (map) fe.Push(new WorldMapScreen());
            else if (sms) fe.Push(new SmsInboxScreen());
            else if (sheet) fe.Push(new RepSheetMainScreen(true));
        }
    }
}
