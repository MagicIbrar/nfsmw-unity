using System.IO;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// InGamePhotoMaster.fng — PhotoFinishScreen (MenuScreens/InGame/PhotoFinish.cpp; PC ctor 0x58E630, dtor 0x57D880,
    /// NotificationMessage 0x5A7EA0, Setup 0x5A2AD0). Pushed by FE_ShowPostRaceScreen (0x60AC20) when the player won
    /// (Arg = FERESULTTYPE_RACE). After 0.75 s of cinematic slowdown the sim pauses and the ICE photo-finish camera plays;
    /// 0.75 s later the handler's call script runs, the photo (HUDTEXTURESPHOTOFINISH.BIN: PHOTOFINISH_RIVAL in a career
    /// boss race, else the race's PhotoFinishTexture) and its text group (SPRINT_GROUP, RIVAL_GROUP or TOLL_BOOTH_GROUP)
    /// appear. Accept continues (career: unpause, a boss race goes through the rival flow; quick race: back to the front
    /// end), key 2 (PAD_BUTTON4) shows PostRace_Results over InGameBackground, key 1 (PAD_BUTTON5) asks to restart.
    /// The slowdown and the ICE camera (DefaultFinish / PhotoFinishCamera) go through ChaseCamera; [?] the world freezes when the
    /// screen opens.
    /// </summary>
    public sealed class PhotoFinishScreen : FEMenuScreen
    {
        const uint Photo = 0x286A9CD4, Handler = 0x47FF4E7C, Call = 0x0013C37B, MsgRestart = 0xE1A57D51, MsgCancel = 0xB4623F67;
        public static bool RestartSelected;

        readonly RaceManager _race;
        readonly RaceEvent _event;
        readonly bool _won;
        string _photo;
        float _slowdownAt = -1, _iceAt = -1;

        public override string Package => "InGamePhotoMaster";

        public PhotoFinishScreen(RaceManager race, bool won)
        {
            _race = race;
            _event = race?.Event;
            _won = won;
        }

        bool CareerBoss => GameSession.Mode == GameMode.Career && _event != null && _event.BossRace;

        protected override void Setup()
        {
            _photo = CareerBoss ? "PHOTOFINISH_RIVAL" : PhotoTexture(_event);
            // SillyTextureStreamerManager::Load: the image stays invisible until its texture is in
            var img = Obj(Photo);
            View.SetInvisible(img);
            var tex = Load(_photo);
            if (tex != null && img != null) { View.SetTexture(img, tex); View.SetVisibleTree(img); }
            _slowdownAt = Time.realtimeSinceStartup;
            ChaseCamera.StartCinematicSlowdown(0.75f);   // CameraAI::StartCinematicSlowdown(EVIEW_PLAYER1, 0.75) (PhotoFinish.cpp ctor)
            NfsMw.Audio.MiscSound.Play(2);   // the photo-finish camera (MMiscSound 2 = FX_Camera, PhotoFinish.cpp:309)
        }

        /// <summary>GRaceParameters::GetPhotoFinishTexture (0x5FCC60) from the event's gameplay attributes.</summary>
        static string PhotoTexture(RaceEvent ev)
        {
            AttribCollection c = null;
            if (ev != null) GameSession.Career?.Db?.Collections.TryGetValue((AttribDatabase.Hash("gameplay"), ev.Key), out c);
            if (c != null && c.Has("PhotoFinishTexture")) { var t = c.Text("PhotoFinishTexture"); if (!string.IsNullOrEmpty(t)) return t.ToUpperInvariant(); }
            // [?] fallback by race type (the four pictures in the pack)
            return ev?.Type == RaceType.Drag ? "PHOTOFINISH_DRAG" : ev?.Type == RaceType.Tollbooth ? "PHOTOFINISH_TOOBOOTH" : "PHOTOFINISH_SPRINT";
        }

        static TexturePackLoader _pack;

        static Texture2D Load(string name)
        {
            _pack ??= new TexturePackLoader("GLOBAL/HUDTEXTURESPHOTOFINISH.BIN");
            return _pack.Get(name);
        }

        protected override void OnClose()
        {
            NfsMw.Audio.SoundState.SetGameFlow(NfsMw.Audio.SoundState.FlowFeCamera, false);   // dtor (0x57D91C): ESndGameState(7, off)
            ChaseCamera.IceUseRealTime = false;   // ICEManager::SetUseRealTime(false) when the photo finish leaves
            if (RestartSelected)
            {
                RestartSelected = false;
                var ev = _event;
                (FE ?? FrontEnd.Instance)?.Defer(() => PostRaceResultsScreen.Restart(ev));
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.PadAccept:
                    if (IsScriptSet(Photo, FEHash.Hide)) return;
                    if (View.IsScriptRunning(Obj(Photo), FEHash.Appear)) return;
                    if (GameSession.Mode == GameMode.Career)
                    {
                        if (_event != null && _event.BossRace)
                        {
                            // the rival flow: all boss races done → SafeHouseRivalChallenge; Razor's (bin 1) last race → outro;
                            // otherwise the in-game rival sheet (ERaceSheetOn(2)) [ours: the race flow's AfterResults runs it]
                            FE.CloseAll();
                            return;
                        }
                        FE.CloseAll();   // EUnPause + MFlowReadyForOutro
                        return;
                    }
                    GameFlow.QuitToMainMenu();   // EQuitToFE(GARAGETYPE_MAIN_FE) (challenge-mode autosave not ported)
                    return;
                case FEHash.PadButton4:
                    View.PackageMessage(FrontEndData.Hash("PAD_BUTTON4_CB"));
                    if (!(FE.Top is InGameBackgroundScreen)) FE.Push(new InGameBackgroundScreen());
                    FE.Push(new PostRaceResultsScreen(_race, _won, true));
                    return;
                case FEHash.PadButton5:
                    DialogInterface.ShowTwoButtons(this, "InGameDialog.fng", DialogTitle.Alert, FEHash.LabelOk, FEHash.LabelCancel,
                        MsgRestart, MsgCancel, MsgCancel, true, 1, Lang(0x4D3399A8));
                    return;
                case MsgRestart:
                    View.PackageMessage(FrontEndData.Hash("PAD_BUTTON5_CB"));
                    PostRaceResultsScreen.Restart(_event);   // EUnPause + (destructor) ERestartRace, run at once [see PostRaceResultsScreen]
                    return;
                case MsgCancel:
                    View.PackageMessage(FEHash.EnableInput);
                    return;
                case FEHash.ScreenTick:
                    float now = Time.realtimeSinceStartup;
                    if (_slowdownAt >= 0 && now - _slowdownAt >= 0.75f)
                    {
                        // HideEverySingleHud, RequestPauseSimulation, ECinematicMoment("Cinematics", PhotoFinishCamera or DefaultFinish)
                        // the sim is paused under the photo finish: ICEManager::SetUseRealTime(true) keeps the ICE camera moving
                        ChaseCamera.IceUseRealTime = true;
                        ChaseCamera.PlayGenericCamera("Cinematics", string.IsNullOrEmpty(_event?.PhotoFinishCamera) ? "DefaultFinish" : _event.PhotoFinishCamera);
                        _slowdownAt = -1;
                        _iceAt = now;
                        return;
                    }
                    if (_iceAt >= 0 && now - _iceAt >= 0.75f)
                    {
                        _iceAt = -1;
                        NfsMw.Audio.SoundState.SetGameFlow(NfsMw.Audio.SoundState.FlowFeCamera, true);   // 0x5A86AD: ESndGameState(7, on) with the photo
                        if (!IsScriptSet(Handler, Call)) SetScript(Handler, Call);
                        uint group = _photo == "PHOTOFINISH_TOOBOOTH" ? FrontEndData.Hash("TOLL_BOOTH_GROUP")
                            : _photo == "PHOTOFINISH_RIVAL" ? FrontEndData.Hash("RIVAL_GROUP") : FrontEndData.Hash("SPRINT_GROUP");
                        SetScript(group, FEHash.Appear);
                        if (!IsScriptSet(Photo, FEHash.Appear)) SetScript(Photo, FEHash.Appear);
                        Fill();
                    }
                    return;
            }
        }

        /// <summary>PhotoFinishScreen::Setup (0x5A2AD0): "time @ speed units", the bonus time, the cash line.</summary>
        void Fill()
        {
            var me = _race?.Entrants.FirstOrDefault(e => e.Human && !e.Remote);
            if (me == null) return;
            var st = RaceStatsTracker.For(me);
            uint unitsLabel = Metric ? 0x8569A25Fu : 0x8569AB44u;
            string units = Lang(unitsLabel);
            float speed = Metric ? st.FinishingSpeed * 3.6f : st.FinishingSpeed * 2.23699f;
            float points = Metric ? me.Points : me.Points / 3.6f * 2.23699f;
            float cash = _event?.Cash ?? 0;
            string time = FETimer.Print(me.FinishTime);
            string timeAndSpeed = $"{time} {Lang(0x474)} {speed.Grouped()} {units}";
            uint cashHash;
            bool speedtrap = _event?.Type == RaceType.SpeedTrap;
            if (IsScriptSet(FrontEndData.Hash("TOLL_BOOTH_GROUP"), FEHash.Appear))
            {
                Printf(0x8BB39726, $"{speed.Grouped()} {units}");
                Printf(0x424BB244, timeAndSpeed);
                Printf(0x8A7F929C, "+" + FETimer.Print(Mathf.Max(0, _race.TimeLeft)));
                cashHash = 0x42423E94;
            }
            else if (IsScriptSet(FrontEndData.Hash("RIVAL_GROUP"), FEHash.Appear))
            {
                Printf(0x37BEA03B, speedtrap ? $"{Lang(0x7F54569D)}: {points.Grouped()} {units}" : timeAndSpeed);
                cashHash = 0x9F4DF5BB;
            }
            else
            {
                Printf(0xAB6AAFDD, speedtrap ? $"{Lang(0x7F54569D)}: {points.Grouped()} {units}" : timeAndSpeed);
                cashHash = 0x3D1773DD;
            }
            if (cash > 0 && GameSession.Mode != GameMode.QuickRace && GameSession.Mode != GameMode.FrontEnd) Printf(cashHash, $"{Lang(0xB7F2B3C8)}: {cash.Grouped()}");
            else SetInvisible(cashHash);
            if (_event?.EventId == "19.8.31")
                DialogInterface.ShowOneButton(this, "", DialogTitle.Alert, FEHash.LabelOk, DialogInterface.MsgCancel, DialogInterface.MsgCancel, true, Lang(0x4C54B7EA));
        }
    }

    /// <summary>Loads one texture pack on demand and converts the requested textures (a streamed pack such as the photos).</summary>
    public sealed class TexturePackLoader
    {
        readonly string _path;
        System.Collections.Generic.List<GameTexture> _all;
        readonly System.Collections.Generic.Dictionary<string, Texture2D> _done = new System.Collections.Generic.Dictionary<string, Texture2D>();

        public TexturePackLoader(string relativePath) { _path = relativePath; }

        public Texture2D Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_done.TryGetValue(name, out var t)) return t;
            try
            {
                if (_all == null)
                {
                    GameData.Mount();
                    var p = Path.Combine(GameData.Root, _path);
                    if (!File.Exists(p)) return null;
                    var d = GameFiles.ReadBundle(p);
                    _all = TexturePack.ReadAll(d, 0, d.Length).ToList();
                }
                var g = _all.FirstOrDefault(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
                if (g == null) return null;
                t = TextureLibrary.ToUnity(g);
                _done[name] = t;
                return t;
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW FE texture pack " + _path + ": " + e.Message); return null; }
        }
    }
}
