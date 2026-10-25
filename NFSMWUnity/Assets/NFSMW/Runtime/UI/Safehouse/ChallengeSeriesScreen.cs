using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Gameplay;
using UnityEngine;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>
    /// ChallengeSeries.fng — UIQRChallengeSeries (quickrace/uiQRChallengeSeries.cpp; PC ctor 0x7B5630, Setup 0x7AE950,
    /// RefreshHeader 0x7A4190, NotificationMessage 0x7AE680, factory entry 0x8F4368 → 0x570CC0). An ArrayScrollerMenu 4 wide
    /// × 3 high over EVENT_ICON_1..12 (fe_safehouse.md §5b):
    /// <list type="bullet">
    /// <item>List: bin 19's WorldRaces in array order (tollbooth N / pursuit N alternate, then burger king and the
    /// Collector's Edition race), filtered by UnlockSystem::IsEventAvailable (0x576A20, <see cref="QuickRaceUnlock"/>): 99.1.1
    /// never; 19.8.31 once easter egg 3 (burgerking) is unlocked [?: the HasBeenAwardedBKReward flag is not kept]; 19.9.70 listed
    /// (0x8F8718 = 1 in v1.3). IsRaceValidForMike (0x7A4590) is dead (MikeMannBuild 0x92612C is always 0 on PC).</item>
    /// <item>Texts (GetChallengeHeaderHash 0x57FDC0 / GetChallengeDescHash 0x57FDE0): CHALLENGE_SERIES_DESC_%02d_HEADER and
    /// CHALLENGE_SERIES_DESC_%02d of the race's LocalizationTag attribute (0xDB89AB5C); the description is printed with the
    /// goal (ThreshholdValue 0x4E90219D, ÷60 for the time challenges, "%$0.0f") twice.</item>
    /// <item>Locked = !IsTrackUnlocked(quick race) (score flag UnlockedQuickRace); checked = completed in quick race.</item>
    /// <item>Accept: locked → UI sound 7; else "Are you sure you want to launch this event?" (0x77CF03C5) Yes/No, then the
    /// transmission prompt (when TransmissionPromptOn) and RaceStarter::StartRace. Back → MainMenu.fng. Key 2 (PAD_BUTTON4,
    /// tollbooth / type 0 only): the tollbooth tutorial movie.</item>
    /// </list>
    /// Deviations [ours]: the exe's reward / length lines go to objects the PC package lacks (nothing shown, as the exe);
    /// the "View Tutorial" movie plays through MoviePlayer.TryPlay.
    /// </summary>
    public sealed class ChallengeSeriesScreen : FEMenuScreen
    {
        public override string Package => "ChallengeSeries";
        const int W = 4, H = 3;
        const uint MsgLaunch = 0xD05FC3A3, MsgNo = 0x34DC1BCF, MsgManual = 0x5F5E3886, MsgAuto = 0x1A2826E1;

        sealed class Row { public RaceEvent Ev; public int Tag; public uint Type; public bool Locked, Done; }
        readonly List<Row> _rows = new List<Row>();
        int _index, _top;
        uint _lastEvent = 0xFFFFFFFF;
        RaceEvent _pending;
        QRTrackMap _map;

        protected override void Setup()
        {
            SetLanguageHash(FrontEndData.Hash("TITLE"), 0xE04D3A4D);
            SetInvisible(0xBBF970CD);                       // "EVENT ID"
            _map = new QRTrackMap(View, Obj(0x0F365871));   // TRACK_MAP (UITrackMapStreamer member)
            foreach (var ev in GameSession.ChallengeEvents())
            {
                if (!QuickRaceUnlock.IsEventAvailable(ev)) continue;
                _rows.Add(new Row
                {
                    Ev = ev, Tag = LocalizationTag(ev), Type = string.IsNullOrEmpty(ev.MilestoneType) ? 0u : AttribDatabase.Hash(ev.MilestoneType),
                    Locked = !QuickRaceUnlock.IsTrackUnlocked(ev) && !GameSession.ChallengeUnlocked(ev),
                    Done = RaceDatabase.CheckFlags(ev, RaceIndex.CompletedQuickRace) || (GameSession.Profile?.HasWon(ev.Name) ?? false),
                });
            }
            _index = 0;   // SetInitialPosition(0)
            RefreshHeader();
        }

        /// <summary>GRaceParameters::GetLocalizationTag (0x5FB990): gameplay attribute LocalizationTag (0xDB89AB5C).</summary>
        internal static int LocalizationTag(RaceEvent ev)
        {
            AttribCollection c = null;
            GameSession.Career?.Db?.Collections.TryGetValue((AttribDatabase.Hash("gameplay"), ev.Key), out c);
            return c != null && c.Has("LocalizationTag") ? c.Int("LocalizationTag") : 0;
        }

        /// <summary>GetMilestoneIconHash (0x56DEA0, large): the milestone type's icon texture.</summary>
        static uint IconFor(uint type) => type switch
        {
            0x033FA23A => 0x950FCEBC, 0x2377E50D => 0xB4E6456B, 0x5392E4FD => 0x831B7EBE, 0xFD989A3A => 0x87807869, 0xEB45F99D => 0xC43959D2,
            0x850A64BC => 0x88E8DE9E, 0xA61CAC24 => 0x6784A80E, 0xCDF36FC2 => 0xE621B2EF, 0x4FC942CA => 0x0FE608E6, _ => 0x65818EE8,
        };

        /// <summary>UIQRChallengeSeries::RefreshHeader (0x7A4190) over the visible 4×3 window.</summary>
        void RefreshHeader()
        {
            int perPage = W * H;
            if (_index < _top) _top = _index / W * W;
            if (_index >= _top + perPage) _top = (_index / W - H + 1) * W;
            for (int i = 1; i <= perPage; i++)
            {
                var icon = Obj("EVENT_ICON_" + i);
                var check = Obj("CHECK_" + i);
                int k = _top + i - 1;
                bool has = k < _rows.Count;
                if (icon != null) View.SetVisible(icon, has);
                if (has)
                {
                    var r = _rows[k];
                    var tex = FrontEndData.Texture(IconFor(r.Type));
                    if (tex && icon != null) View.SetTexture(icon, tex);
                    SetScript(icon, k == _index ? FEHash.Highlight : FEHash.UnHighlight);
                    if (r.Locked) { SetTextureHash(FrontEndData.Hash("CHECK_" + i), 0x0018ED48); SetScript(check, FEHash.Appear); }
                    else if (r.Done) { SetTextureHash(FrontEndData.Hash("CHECK_" + i), 0x028FEADD); SetScript(check, FEHash.Appear); }
                    else SetScript(check, FEHash.Hide);
                }
                else SetScript(check, FEHash.Hide);
            }
            if (_rows.Count == 0) return;
            var row = _rows[_index];
            Printf(0x5A856A34, (_index + 1).ToString());
            Printf(0x2D4D22C8, _rows.Count.ToString());
            uint header = FrontEndData.Hash($"CHALLENGE_SERIES_DESC_{row.Tag:00}_HEADER");
            SetLanguageHash(0x0790CE49, header);
            uint evHash = AttribDatabase.Hash(row.Ev.Name ?? "");
            if (evHash == _lastEvent) return;
            _lastEvent = evHash;
            // cash / length lines → 0x0013C45E / 0x080C9DAA: absent from the PC package (nothing drawn, as the exe)
            View.PackageMessage(row.Type == 0 ? 0xB295482Eu : 0x0F7B54C7u);   // View Tutorial button for tollbooth challenges
            SetTextureHash(0xA018DE49, IconFor(row.Type));
            float g = row.Ev.Threshold;
            if (row.Type == 0x033FA23A || row.Type == 0x5392E4FD) g *= 1f / 60f;   // IsMilestoneTimeFormat: seconds → minutes
            string goal = K.Format("%$0.0f", g);
            string desc = Lang(FrontEndData.Hash($"CHALLENGE_SERIES_DESC_{row.Tag:00}"));
            if (desc.StartsWith("[")) desc = "";
            Printf(0x7B230D64, K.Format(desc, goal, goal));
            if (row.Locked)
            {
                View.PackageMessage(0xC5DD9D68);   // lock group SHOW
                int n = _index + 1, p = n / 5, m = n % 5;
                string text;
                if (n > 60) text = K.Format(Lang(0xCED8931E), n - 1);
                else if (m == 1 || m == 2) text = K.Format(Lang(0xCED8931E), 5 * p);
                else if (m == 3 || m == 4) text = K.Format(Lang(0xCED8931D), 5 * p + 1, 5 * p + 2);
                else text = K.Format(Lang(0xCED8931D), 5 * p - 2, 5 * p - 1);
                Printf(0x68215623, text);
            }
            else View.PackageMessage(0x38091FA1);   // lock group HIDE
            // UITrackMapStreamer::Init(race, TRACK_MAP): the event's TRACK_MAP_<id> texture, zoomed / panned to the race
            _map?.Init(row.Ev);
        }

        protected override void Feed(MenuInput i)
        {
            // ArrayScrollerMenu: directions move the highlight (ScrollVer / ScrollHor), the rest goes to the package
            int was = _index, n = _rows.Count;
            if (n > 0)
            {
                // ArrayScrollerMenu::ScrollHor (feArrayScrollerMenu.cpp): wraps inside the row
                int rowStart = _index / W * W, rowEnd = Mathf.Min(rowStart + W, n) - 1;
                if (i.Left) _index = _index > rowStart ? _index - 1 : rowEnd;
                if (i.Right) _index = _index < rowEnd ? _index + 1 : rowStart;
                if (i.Up && _index - W >= 0) _index -= W;
                if (i.Down && _index + W < n) _index += W;
            }
            if (_index != was) RefreshHeader();
            if (i.Accept) NotificationMessage(FEHash.ButtonPressed, null);
            if (i.Back) View.PadButton(FEHash.PadBack);
            if (i.Key2) View.PadButton(FEHash.PadButton4);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.ScreenTick:
                    _map?.UpdateAnimation();   // FEMSG_SCREEN_TICK → TrackMapStreamer.UpdateAnimation
                    return;
                case FEHash.ButtonPressed:
                    if (_rows.Count == 0) return;
                    var row = _rows[_index];
                    if (row.Locked) { NfsMw.Audio.FeSound.Play(NfsMw.Audio.FeSoundId.CommonWrong); return; }
                    _pending = row.Ev;
                    DialogInterface.ShowTwoButtons(this, "", DialogTitle.Alert, 0x70E01038, 0x417B25E4, MsgLaunch, MsgNo, MsgNo, true, 1, Lang(0x77CF03C5));
                    return;
                case MsgLaunch:
                    if (_pending == null) return;
                    if (FEDb.Data.Player(0).TransmissionPromptOn)
                    {
                        var ps = FEDb.Data.Player(0);
                        DialogInterface.ShowTwoButtons(this, "", DialogTitle.Confirmation, 0x317D3005, 0x8CD532A0, MsgManual, MsgAuto, MsgAuto, true,
                            ps.Transmission != 0 ? 0 : 1, Lang(0x6F5401D1));
                        return;
                    }
                    Start();
                    return;
                case MsgManual: FEDb.Data.Player(0).Transmission = 1; Start(); return;
                case MsgAuto: FEDb.Data.Player(0).Transmission = 0; Start(); return;
                case FEHash.PadButton4:
                    if (_rows.Count > 0 && _rows[_index].Type == 0)
                    {
                        SetInvisible(0x99344537);   // MASTERBLASTER hidden while the tutorial plays; 0xC3960EB9 on return → INIT
                        FE.Push(new TutorialScreen("tollbooth_tutorial") { Done = () => { if (View) { SetScript(0x99344537, FEHash.Init); SetVisible(0x99344537); } } });
                    }
                    return;
                case FEHash.PadBack:
                    // QueuePackageSwitch("MainMenu.fng")
                    FEDb.ClearGameMode(FEGameMode.Challenge);
                    FE.Defer(() => { if (FE.Top == this) FE.Replace(new MainMenuScreen()); });
                    return;
            }
        }

        /// <summary>AllocCustomRace(race) + SetStartupRace(QuickRace) + RaceStarter::StartRace (0x57ECA0).</summary>
        void Start()
        {
            var ev = _pending;
            _pending = null;
            if (ev == null) return;
            FEDb.Save();
            GameSession.QuickCar = new OwnedCar { Car = GameSession.Career.CarForPreset(ev.PlayerCar) ?? "gti", Engine = 1, Tires = 1 };
            GameSession.Mode = GameMode.ChallengeSeries;
            GameFlow.StartRace(ev);
        }
    }
}
