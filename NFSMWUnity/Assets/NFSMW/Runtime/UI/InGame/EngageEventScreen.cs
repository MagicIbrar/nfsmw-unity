using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// EngageEventDialog.fng — nsEngageEventDialog::EngageEventDialog (MenuScreens/Career/FEPkg_EngageEventDialog.cpp; PC
    /// ctor 0x597DB0, NotificationMessage 0x56B470, accept / decline 0x56B400 / 0x56B430). Event icon (0xAD9E232A,
    /// GetRaceIconHash), event type (0xA01B9361, GetRaceNameHash) and track name (0xF601F2D4, TRACKNAME_&lt;event id&gt;),
    /// laps (0x644AB208), distance (0xBCE13923, "%$0.1f" km or miles), cop probability (0x762F1D7A Maximum / Minimum),
    /// best time (0x8FD41BB4) / average (0x35D1AB83) / top speed (0xDE9145FB) from the save, cash (0x45276F1F) and the
    /// race's track map (TRACK_MAP_&lt;event id&gt;). Buttons: Accept 0xD72F002A → MAcceptEnterCareerEvent, Decline 0x694B896E
    /// (selected at the start by the package) and back → MDeclineEnterCareerEvent.
    /// </summary>
    public sealed class EngageEventScreen : FEMenuScreen
    {
        const uint Accept = 0xD72F002A, Decline = 0x694B896E;
        readonly RaceEvent _ev;
        TrackMapStreamer _map;

        public override string Package => FrontEndData.Packages.ContainsKey("EngageEventDialog") ? "EngageEventDialog" : "GenericDialog_ThreeButton";
        public EngageEventScreen(RaceEvent ev) { _ev = ev; }

        /// <summary>cFrontendDatabase::GetRaceIconHash (FEDatabase.cpp:1322).</summary>
        public static uint RaceIconHash(RaceType t) => t switch
        {
            RaceType.Sprint => 0x2521E5EB, RaceType.Circuit => 0xE9638D3E, RaceType.Drag => 0xAAAB31E9,
            RaceType.Knockout => 0x3A015595, RaceType.Tollbooth => 0x1A091045, RaceType.SpeedTrap => 0x66C9A7B6, _ => 0,
        };

        /// <summary>cFrontendDatabase::GetRaceNameHash (FEDatabase.cpp:1291).</summary>
        public static uint RaceNameHash(RaceType t) => t switch
        {
            RaceType.Sprint => 0xB94FD70E, RaceType.Circuit => 0x034FA2C1, RaceType.Drag => 0x6F547E4C, RaceType.Knockout => 0x4930F5FC,
            RaceType.Tollbooth => 0xA15E4505, RaceType.SpeedTrap => 0xEE1EDC76, _ => 0x213CC8D1,
        };

        /// <summary>CalcLanguageHash(prefix, race) (RaceDB.cpp:23): prefix + EventID with '.' → '_' (FixDot; TRACKNAME_15_1_1 = Ironwood Estates in English.bin).</summary>
        public static string LanguageKey(string prefix, RaceEvent ev) => prefix + (ev?.EventId ?? "").Replace('.', '_');

        /// <summary>The TRACKNAME_ label of an event ([ours] its event id when the label is missing).</summary>
        public static string TrackName(RaceEvent ev) => FrontEndData.Text(FrontEndData.Hash(LanguageKey("TRACKNAME_", ev)), ev?.EventId ?? "");

        protected override void Setup()
        {
            if (_ev == null) return;
            uint icon = RaceIconHash(_ev.Type);
            if (icon != 0) SetTextureHash(0xAD9E232A, icon);
            SetLanguageHash(0xA01B9361, RaceNameHash(_ev.Type));
            Printf(0xF601F2D4, TrackName(_ev));
            Printf(0x644AB208, _ev.Laps.ToString());
            float meters = _ev.Length;   // GRaceParameters::GetRaceLengthMeters (RaceLength)
            Printf(0xBCE13923, $"{Localize.Fixed(meters * (Metric ? 0.001f : 0.000625f), 1, true)} {Lang(Metric ? 0x8569A26Au : 0x867DCFD9u)}");
            SetLanguageHash(0x762F1D7A, _ev.CopsInRace ? 0x61D1C5A5u : 0x73C615A3u);
            var info = GameSession.Profile?.RaceScores?.FirstOrDefault(s => s.Race == _ev.Name);
            bool timed = _ev.Type == RaceType.Sprint || _ev.Type == RaceType.Circuit || _ev.Type == RaceType.Drag || _ev.Type == RaceType.Knockout || _ev.Type == RaceType.Tollbooth;
            Printf(0x8FD41BB4, timed ? FETimer.Print(info?.Best ?? 0) : Lang(0x472AA00A));
            string units = Lang(Metric ? 0x8569A25Fu : 0x8569AB44u);
            float avg = (info?.AverageSpeed ?? 0) * (Metric ? 3.6f : 2.23699f), top = (info?.TopSpeed ?? 0) * (Metric ? 3.6f : 2.23699f);
            Printf(0x35D1AB83, $"{avg.Grouped()} {units}");
            Printf(0xDE9145FB, $"{top.Grouped()} {units}");
            Printf(0x45276F1F, _ev.Cash.Grouped());
            // UITrackMapStreamer::Init(parms, TRACK_MAP): TRACK_MAP_<event> from TrackMaps.bin (invisible until loaded)
            var map = Obj(FrontEndData.Hash("TRACK_MAP"));
            View.SetInvisible(map);
            var tex = CareerScreenKit.TrackMap(LanguageKey("TRACK_MAP_", _ev));
            if (tex != null && map != null)
            {
                _map = new TrackMapStreamer(View, map, LanguageKey("TRACK_MAP_", _ev));
                View.SetVisibleTree(map);
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.ScreenTick:
                    _map?.Update(Time.unscaledDeltaTime);
                    break;
                case FEHash.PadBack:
                    QueuePackagePop();   // MDeclineEnterCareerEvent
                    break;
                case FEHash.ButtonPressed:
                    if (obj == null) break;
                    if (obj.Obj.Hash == Accept)
                    {
                        // MAcceptEnterCareerEvent: the career starts the event
                        var ev = _ev;
                        FE.CloseAll();
                        GameFlow.StartRace(ev);
                    }
                    else if (obj.Obj.Hash == Decline) QueuePackagePop();
                    break;
            }
        }
    }
}
