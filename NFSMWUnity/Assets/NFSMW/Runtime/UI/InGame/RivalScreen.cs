using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// SafeHouseRivalChallenge.fng / InGameRivalChallenge.fng — uiRepSheetRival (MenuScreens/Safehouse/career/
    /// uiRepSheetRival.cpp). Arg: 0 front end, 1 in game (from the in-game Blacklist), 2 in game mid rival flow (a boss
    /// race won with races left), 3 in game one-off (a boss-race zone). The rival art (photo, tag, background) streams
    /// in; the DEFEATED stamp image 0x7FE4020F / 0x26869897 uses GetDefeatedTexture (by language) and shows once it is in. The
    /// ctor's EFadeScreenOff(0x0161A918) and CarViewer::HideAllCars in post-rival mode have no port counterpart (the screen
    /// shows no car) [?]. Package messages: mid flow
    /// 0x34297CB0, post-rival mode 0x0B21A45F + DEFEATED 0xB4C144B1, otherwise CALLFRAMES 0xAF922178; the row layout by
    /// boss-race count (≥5 0xE7177701, 4 0x9A1D3A40, 3 0x4D22FD7F, else 0x0028C0BE) and CROSSOUT_n for each race won.
    /// Rows: EVENT_ICON_n (GetRaceIconHash), EVENT NAME_n (GetRaceNameHash), DATA_n (TRACKNAME_), RIVAL_BEST_DATA_n (the
    /// rival's best time). Accept: mid flow → back to the game (ERaceSheetOff + outro); post-rival mode → the rival flow
    /// goes on; otherwise the bin's first boss race starts. Back (not mid flow): one-off → unpause; otherwise switch to
    /// the Blacklist overview.
    /// </summary>
    public sealed class RivalScreen : FEMenuScreen
    {
        readonly int _arg;
        readonly int _postRivalBin;
        RaceEvent _launch;
        bool InGameArg => _arg != 0;
        bool MidFlow => _arg == 2;
        bool OneOff => _arg == 3;
        bool PostRival => _postRivalBin > 0;

        public override string Package => InGameArg && FrontEndData.Packages.ContainsKey("InGameRivalChallenge") ? "InGameRivalChallenge" : "SafeHouseRivalChallenge";

        /// <summary>Default: the current rival; in game it is the one-off (boss-race zone) form, else the front-end form.</summary>
        public RivalScreen() : this(InGame ? 3 : 0) { }

        public RivalScreen(int arg)
        {
            _arg = arg;
            if (arg == 0 || arg == 3) RepSheetMainScreen.ViewBin = Mathf.Clamp(GameSession.Profile?.Bin ?? 15, 1, 15);
        }

        /// <summary>Post-rival mode (uiRepSheetRivalFlow stage 0): the beaten rival with the DEFEATED stamp.</summary>
        public static RivalScreen PostRivalMode(int beatenBin) => new RivalScreen(beatenBin, true);

        RivalScreen(int beatenBin, bool postRival) : this(0)
        {
            _postRivalBin = beatenBin;
            RepSheetMainScreen.ViewBin = beatenBin;
        }

        protected override void Setup()
        {
            int bin = RepSheetMainScreen.ViewBin;
            CareerScreenKit.RivalArt(View, bin, InGameArg);
            // the DEFEATED stamp: invisible until its streamed texture is in
            var stamp = Obj(0x7FE4020F);
            var stampBg = Obj(0x26869897);
            View.SetInvisible(stamp);
            View.SetInvisible(stampBg);
            var tex = FrontEndData.Texture(GetDefeatedTexture());
            if (tex != null)
            {
                View.SetTexture(stamp, tex); View.SetTexture(stampBg, tex);
                View.SetVisibleTree(stamp); View.SetVisibleTree(stampBg);
            }
            if (InGameArg && MidFlow) View.PackageMessage(0x34297CB0);
            else if (PostRival)
            {
                View.PackageMessage(0x0B21A45F);
                View.PackageMessage(0xB4C144B1);
            }
            else View.PackageMessage(0xAF922178);
            RefreshHeader();
        }

        /// <summary>uiRepSheetRival::GetDefeatedTexture (PC 0x511820, jump table of 13 languages): French 0x087B81CD, German
        /// 0x087B846E, Italian 0x087B8ECE, Spanish 0x087BB8D4, Dutch 0x087B79BD, Swedish 0x087BB9BF, Danish 0x087B7723, Polish
        /// 0x087BABFB, Finnish 0x087B80AD; English, Korean, Chinese, Japanese and Thai 0x087B7D0A (unlike uiRepSheetMain's
        /// table, which has its own Asian stamps).</summary>
        static uint GetDefeatedTexture() => Localize.CurrentLanguage switch
        {
            Language.French => 0x087B81CD, Language.German => 0x087B846E, Language.Italian => 0x087B8ECE, Language.Spanish => 0x087BB8D4,
            Language.Dutch => 0x087B79BD, Language.Swedish => 0x087BB9BF, Language.Danish => 0x087B7723, Language.Polish => 0x087BABFB,
            Language.Finnish => 0x087B80AD, _ => 0x087B7D0A,
        };

        void RefreshHeader()
        {
            var career = GameSession.Career;
            if (career == null || !career.Bins.TryGetValue(RepSheetMainScreen.ViewBin, out var bin)) { CareerScreenKit.BountyCash(View); return; }
            int n = bin.BossRaces.Count;
            View.PackageMessage(n >= 5 ? 0xE7177701u : n == 4 ? 0x9A1D3A40u : n == 3 ? 0x4D22FD7Fu : 0x0028C0BEu);
            var p = GameSession.Profile;
            for (int i = 0; i < n; i++)
            {
                var race = bin.BossRaces[i];
                if (_launch == null) _launch = race;
                if (p != null && p.HasWon(race.Name)) View.PackageMessage(FrontEndData.Hash($"CROSSOUT_{i + 1}"));
                SetupRace(i + 1, race);
            }
            CareerScreenKit.BountyCash(View);
        }

        void SetupRace(int num, RaceEvent race)
        {
            uint icon = EngageEventScreen.RaceIconHash(race.Type);
            if (icon != 0) SetTextureHash(FrontEndData.Hash($"EVENT_ICON_{num}"), icon);
            SetLanguageHash(FrontEndData.Hash($"EVENT NAME_{num}"), EngageEventScreen.RaceNameHash(race.Type));
            Printf(FrontEndData.Hash($"DATA_{num}"), EngageEventScreen.TrackName(race));
            Printf(FrontEndData.Hash($"RIVAL_BEST_DATA_{num}"), FETimer.Print(RivalBestTime(race)));
        }

        /// <summary>GRaceParameters::GetRivalBestTime [?]: the event's RivalBestTime attribute.</summary>
        static float RivalBestTime(RaceEvent race)
        {
            AttribCollection c = null;
            GameSession.Career?.Db?.Collections.TryGetValue((AttribDatabase.Hash("gameplay"), race.Key), out c);
            return c != null && c.Has("RivalBestTime") ? c.Float("RivalBestTime") : 0;
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.PadAccept:
                    if (MidFlow) { FE.CloseAll(); return; }             // ERaceSheetOff + MFlowReadyForOutro
                    if (PostRival) { QueuePackagePop(); return; }       // EEnterBin(bin − 1) + the rival flow from stage 1
                    if (_launch != null)
                    {
                        var ev = _launch;
                        FE.CloseAll();                                  // ERaceSheetOff (in game)
                        GameFlow.StartRace(ev);                         // StartRaceFromInGame / RaceStarter::StartRace
                    }
                    return;
                case FEHash.PadBack:
                    if (MidFlow) return;
                    if (OneOff) { FE.CloseAll(); return; }              // EUnPause
                    if (!PostRival) FE.Replace(new RepSheetMainScreen(InGameArg));
                    return;
            }
        }
    }
}
