using System.Collections.Generic;
using System.Linq;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.InputSystem;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>
    /// The career SMS store (FEDatabase.hpp:264–330 CareerSettings.SMSMessages) over the profile: Profile.Inbox is every
    /// message received in arrival order (= GetSMSSortOrder), SmsRead the READ ones, SmsDeleted the deleted ones.
    /// Labels SMS_MESSAGE_%d, _FROM, _SUBJECT (specs/fe_career_screens.md §2).
    /// </summary>
    public static class SmsStore
    {
        /// <summary>SMSMessage::IsVoice: false for 110, 111, 120–130 and 132–137 (text messages), true for the rest.</summary>
        public static bool IsVoice(int id) => !(id == 110 || id == 111 || (id >= 120 && id <= 130) || (id >= 132 && id <= 137));

        public static bool Exists(int id) => FrontEndData.Strings != null && FrontEndData.Strings.Get($"SMS_MESSAGE_{id}") != null;

        /// <summary>Valid messages with READ or UNREAD, oldest first (uiSMS.cpp:82 sorts ascending by sortOrder).</summary>
        public static List<int> Messages(bool voice)
        {
            var p = GameSession.Profile;
            if (p == null) return new List<int>();
            return p.Inbox.Where(id => !p.SmsDeleted.Contains(id) && Exists(id) && IsVoice(id) == voice).Distinct().ToList();
        }

        public static bool Unread(int id) => GameSession.Profile != null && !GameSession.Profile.SmsRead.Contains(id);
        public static bool AnyUnread(bool voice) => Messages(voice).Any(Unread);

        public static void MarkRead(int id)
        {
            var p = GameSession.Profile;
            if (p != null && !p.SmsRead.Contains(id)) { p.SmsRead.Add(id); p.Save(); }
        }

        /// <summary>Delete: flags = 0. The id stays in Inbox so story checks still see it as received.</summary>
        public static void Delete(int id)
        {
            var p = GameSession.Profile;
            if (p != null && !p.SmsDeleted.Contains(id)) { p.SmsDeleted.Add(id); p.Save(); }
        }

        public static string From(int id) => K.L($"SMS_MESSAGE_{id}_FROM", "");
        public static string Subject(int id) => K.L($"SMS_MESSAGE_{id}_SUBJECT", "");
        public static string Body(int id) => K.L($"SMS_MESSAGE_{id}", "");
    }

    /// <summary>
    /// SMS_MailBoxes.fng — uiSMS (MenuScreens/InGame/uiSMS.cpp; PC create 0x570DE0, Setup 0x548AE0, RefreshHeader 0x531AD0,
    /// NotificationMessage 0x5534F0, AddSMSDatum 0x51FDF0, AddSMSSlot 0x531990, SMSSlot::Update 0x51FD60, ScrollBoxes 0x549130)
    /// on the FEng engine: an ArrayScrollerMenu 1 × 6 (selectable) over MESSAGE_GROUP_n (subject MESSAGE_TEXT_n by language
    /// hash, NEW_MESSAGE_ICON_n when the datum was UNREAD when added). Voice / Text tabs (left / right = ScrollBoxes: the flag
    /// flips and Setup runs again); last_msg[2] remembers each tab's message (0xFF = none, reset per screen). Setup: every valid
    /// READ / UNREAD message sorted by SortOrder (oldest first), the tab's ones listed; new indicators 0x19161CCC (voice) /
    /// 0x0D6FD6F9 (text) — HIGHLIGHT on the shown tab, SHOW on the other, else HIDE. RefreshHeader: tab selector 0x4A2EEBC8
    /// POS1 / POS2, tab titles 0x8A6AD1C1 / 0x8F2FAD70 HIGHLIGHT / UNHIGHLIGHT, no data → cursor 0x07890734 HIDE (the cursor
    /// otherwise follows the current button through the groups' HIGHLIGHT responses). The package forwards the pads to the game.
    /// BUTTON_PRESSED on a row → handler 0x47FF4E7C READ (its script ends in EXIT_COMPLETE) → push SMS_Message.fng; key 1
    /// (PAD_BUTTON5) with a valid message → InGameDialog Yes / No 0x8C3C2171 (first button No): yes deletes the current one
    /// (flags 0) and re-runs Setup, no / cancel post 0x8CB81F09 (input back on); back pops at once (QueuePackagePop(0)).
    /// 0x775CE5DF (SMS_Message's back broadcasts it; the handler forwards it) re-runs Setup.
    /// Arg = a message (EShowSMS(id)): auto-play — the first SCREEN_TICK after INIT_COMPLETE plays READ and pushes it; without it
    /// 0x2CF801C2 plays APPEAR. The ctor's auto-save check (HIDE_INCOMING / SMS_MAILBOX while the memory card auto-saves) is N/A
    /// (the port saves its files synchronously).
    /// </summary>
    public sealed class SmsInboxScreen : FEMenuScreen
    {
        public override string Package => "SMS_MailBoxes";
        const uint Handler = 0x47FF4E7C, MsgRefresh = 0x775CE5DF, MsgInputOn = 0x8CB81F09, MsgYes = 0xD05FC3A3, MsgNo = 0x34DC1BCF,
            MsgCancel = 0x1FAB5998, LangYes = 0x70E01038, LangNo = 0x417B25E4;
        const int W = 1, H = 6;
        readonly int[] _lastMsg = { -1, -1 };   // last_msg[2]; -1 = 0xFF
        readonly Dictionary<int, bool> _checked = new Dictionary<int, bool>();
        readonly List<FEView.Node> _icons = new List<FEView.Node>(), _texts = new List<FEView.Node>();
        bool _voice = true, _autoPlay, _initCompleted;
        uint _buttonPressed;
        int _theMsg = -1;
        readonly int _autoOpen;
        ArrayScroller _scroller;
        FEScrollBar _scrollBar;

        /// <summary>autoOpen ≥ 0: EShowSMS(id) — the Arg message, played once the inbox is up.</summary>
        public SmsInboxScreen(int autoOpen = -1) { _autoOpen = autoOpen; }

        protected override void Setup()
        {
            FrontEndData.Load();
            _scroller = new ArrayScroller(View, W, H);
            _scrollBar = new FEScrollBar(View, "scrollbar");
            for (int i = 1; i <= W * H; i++) AddSMSSlot(i);
            SetupList();
            _theMsg = _autoOpen >= 0 && SmsStore.Exists(_autoOpen) ? _autoOpen : -1;
            if (_theMsg < 0)
            {
                _theMsg = CurrentMsg;
                SetScript(0x2CF801C2, FEHash.Appear);
            }
            else _autoPlay = true;
            if (_theMsg >= 0)
            {
                // (sic) bVoiceMsg changes without another Setup: an auto-played text message shows over the voice tab
                _voice = SmsStore.IsVoice(_theMsg);
                _lastMsg[_voice ? 0 : 1] = _theMsg;
            }
        }

        int CurrentMsg => _scroller?.Current?.Tag is int id ? id : -1;

        /// <summary>AddSMSSlot (0x531990): MESSAGE_GROUP_%d / NEW_MESSAGE_ICON_%d / MESSAGE_TEXT_%d.</summary>
        void AddSMSSlot(int i)
        {
            _scroller.AddSlot(Obj("MESSAGE_GROUP_" + i));
            _icons.Add(Obj("NEW_MESSAGE_ICON_" + i));
            _texts.Add(Obj("MESSAGE_TEXT_" + i));
        }

        /// <summary>uiSMS::Setup (0x548AE0).</summary>
        void SetupList()
        {
            _scroller.ClearData();
            _checked.Clear();
            bool newVoice = SmsStore.AnyUnread(true), newText = SmsStore.AnyUnread(false);
            foreach (int id in SmsStore.Messages(_voice)) AddSMSDatum(id);
            SetScript(0x19161CCC, newVoice ? (_voice ? FEHash.Highlight : FEHash.Show) : FEHash.Hide);
            SetScript(0x0D6FD6F9, newText ? (_voice ? FEHash.Show : FEHash.Highlight) : FEHash.Hide);
            int last = _lastMsg[_voice ? 0 : 1], index = 0;
            for (int i = 0; i < _scroller.NumDatum; i++) if ((int)_scroller.DatumAt(i).Tag == last) index = i;
            _scroller.SetInitialPosition(index);
            UpdateScrollbar();
            SyncButton();
            _theMsg = CurrentMsg;
            RefreshHeader();
        }

        /// <summary>AddSMSDatum (0x51FDF0): the first one fills the tab's empty last_msg; UNREAD → checked (the NEW icon).</summary>
        void AddSMSDatum(int id)
        {
            int tab = _voice ? 0 : 1;
            if (_lastMsg[tab] < 0) _lastMsg[tab] = id;
            _scroller.AddDatum(new ArrayScroller.Datum { Tag = id });
            _checked[id] = SmsStore.Unread(id);
        }

        /// <summary>ArrayScroller::SetSelection on a selectable array: the slot becomes the current button.</summary>
        void SyncButton()
        {
            var slot = _scroller.CurrentSlot;
            if (slot != null && View.CurrentButton != slot) View.SetCurrentButton(slot, true);
        }

        /// <summary>ArrayScroller::UpdateScrollbar: Update(height, rows of data, top row + 1, selected row + 1).</summary>
        void UpdateScrollbar()
        {
            int n = _scroller.NumDatum;
            _scrollBar.Update(H, (n - 1) / W + 1, _scroller.Start / W + 1, Mathf.Max(0, _scroller.CurrentIndex) / W + 1);
        }

        /// <summary>uiSMS::RefreshHeader (0x531AD0) with SMSSlot::Update (0x51FD60).</summary>
        void RefreshHeader()
        {
            _scroller.RefreshHeader();
            for (int i = 0; i < _scroller.NumSlots; i++)
            {
                var d = _scroller.DatumAt(_scroller.Start + i);
                if (d == null) continue;
                int id = (int)d.Tag;
                if (i < _texts.Count) View.SetText(_texts[i], FrontEndData.Localized(FrontEndData.Hash($"SMS_MESSAGE_{id}_SUBJECT")));
                if (i < _icons.Count)
                {
                    if (_checked.TryGetValue(id, out var c) && c) View.SetVisibleTree(_icons[i]);
                    else View.SetInvisible(_icons[i]);
                }
            }
            SetScript(0x4A2EEBC8, _voice ? K.Pos1 : K.Pos2);
            SetScript(0x8A6AD1C1, _voice ? FEHash.Highlight : FEHash.UnHighlight);
            SetScript(0x8F2FAD70, _voice ? FEHash.UnHighlight : FEHash.Highlight);
            if (_scroller.NumDatum < 1) SetScript(0x07890734, FEHash.Hide);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            // ArrayScrollerMenu::NotificationMessage first: the current SMSDatum takes BUTTON_PRESSED (the_msg), directions scroll
            switch (msg)
            {
                case FEHash.ButtonPressed:
                    if (CurrentMsg >= 0) _theMsg = CurrentMsg;
                    break;
                case FEHash.PadUp: case FEHash.PadDown:
                    _scroller.ScrollVer(msg == FEHash.PadDown);
                    UpdateScrollbar();
                    SyncButton();
                    RefreshHeader();
                    break;
                case FEHash.PadLeft: case FEHash.PadRight:
                    _scroller.ScrollHor(msg == FEHash.PadRight);   // one column: stays
                    SyncButton();
                    RefreshHeader();
                    break;
            }
            switch (msg)
            {
                case FEHash.ScreenTick:
                    if (!_initCompleted) break;
                    if (_theMsg >= 0 && _autoPlay)
                    {
                        SetScript(Handler, K.Read);
                        FE.Push(new SmsMessageScreen(_theMsg));
                    }
                    _initCompleted = false;
                    break;
                case FEHash.InitComplete:
                    _initCompleted = true;
                    break;
                case MsgRefresh:
                    _autoPlay = false;
                    if (_theMsg < 0 || !SmsStore.Messages(SmsStore.IsVoice(_theMsg)).Contains(_theMsg)) _lastMsg[0] = _lastMsg[1] = -1;
                    SetupList();
                    break;
                case FEHash.ButtonPressed:
                    if (_scroller.Current == null) { View.PackageMessage(MsgInputOn); break; }
                    _buttonPressed = FEHash.ButtonPressed;
                    SetScript(Handler, K.Read);
                    break;
                case FEHash.PadUp: case FEHash.PadDown:
                    if (CurrentMsg >= 0) _lastMsg[_voice ? 0 : 1] = CurrentMsg;
                    break;
                case FEHash.PadLeft: case FEHash.PadRight:
                    _voice = !_voice;   // ScrollBoxes (0x549130)
                    SetupList();
                    break;
                case FEHash.PadButton5:
                    if (_theMsg < 0 || !SmsStore.Messages(SmsStore.IsVoice(_theMsg)).Contains(_theMsg)) { View.PackageMessage(MsgInputOn); break; }
                    DialogInterface.ShowTwoButtons(this, "InGameDialog.fng", DialogTitle.Alert, LangYes, LangNo, MsgYes, MsgNo, MsgNo, true, 1, Lang(0x8C3C2171));
                    break;
                case MsgYes:
                    View.PackageMessage(MsgInputOn);
                    if (CurrentMsg < 0) break;
                    SmsStore.Delete(CurrentMsg);
                    SetupList();
                    break;
                case MsgNo:
                case MsgCancel:
                    View.PackageMessage(MsgInputOn);
                    break;
                case FEHash.ExitComplete:
                    if (_buttonPressed != FEHash.ButtonPressed || _scroller.Current == null) break;
                    _buttonPressed = 0;
                    FE.Push(new SmsMessageScreen(_theMsg));
                    break;
                case FEHash.PadBack:
                    _buttonPressed = FEHash.PadBack;
                    QueuePackagePop();
                    break;
            }
        }
    }

    /// <summary>
    /// SMS_Message.fng — uiSMSMessage (MenuScreens/InGame/uiSMSMessage.cpp; PC ctor 0x553810, dtor 0x511EF0, Setup 0x549150,
    /// NotificationMessage 0x531D40, NotifySoundMessage 0x511FA0) on the FEng engine. Setup: CTextScroller over MESSAGE_TEXT_1..10
    /// (width = MESSAGE_TEXT_1's MaxWidth, its font, scroll bar "scrollbar") with SMS_MESSAGE_%d; text messages hide the replay
    /// prompts 0x2A631207 / 0x914614E5; header From 0xFECED617 / Subject 0x2C167533 by language hash. Messages: every one goes
    /// to the scroller (PAD_UP / PAD_DOWN scroll a line); INIT_COMPLETE — a voice message with its UNREAD flag plays its call
    /// (MiscSpeech::SMSCellCall 0x531DDA), flags = READ (4); PAD_BUTTON4 (key 2) replays a voice message's call (0x531DAE, after
    /// Speech ClearPlayback); PAD_BUTTON5 (key 1) → InGameDialog Yes / No 0x8C3C2171 (first No); yes → flags 0 (deleted) and
    /// PAD_BACK to the package (its back plays the leave, broadcasts 0x775CE5DF to the inbox, ends in EXIT_COMPLETE);
    /// EXIT_COMPLETE → QueuePackagePop(1). No accept handling (Enter does nothing; Esc = Done). [?] The cop-speech
    /// ReleaseResource / ClearPlayback in Setup and EXIT_COMPLETE are the audio owner's (no port API); the replay prompt's
    /// sound 0x610FB237 is silenced for text messages (FE sound layer).
    /// </summary>
    public sealed class SmsMessageScreen : FEMenuScreen
    {
        public override string Package => "SMS_Message";
        const uint MsgYes = 0xD05FC3A3, MsgNo = 0x34DC1BCF;
        readonly int _id;
        TextScroller _scroller;

        public SmsMessageScreen(int id) { _id = id; }

        protected override void Setup()
        {
            // uiSMSMessage ctor (0x553892): ESndGameState(13, on), FEUpscreen control state off, FESmsMessage on
            NfsMw.Audio.SoundState.SetGameFlow(NfsMw.Audio.SoundState.FlowSms, true);
            NfsMw.Audio.SoundState.Set(false, NfsMw.Audio.SndCtl.FEUpscreen);
            NfsMw.Audio.SoundState.Set(true, NfsMw.Audio.SndCtl.FESmsMessage);
            var line1 = Obj("MESSAGE_TEXT_1");
            _scroller = new TextScroller();
            _scroller.Initialise(View, line1 != null ? (int)line1.Obj.MaxWidth : 0, 10, "MESSAGE_TEXT_%d", line1?.Label?.Font);
            _scroller.UseScrollBar(new FEScrollBar(View, "scrollbar"));
            _scroller.SetTextHash(FrontEndData.Hash($"SMS_MESSAGE_{_id}"));
            if (!SmsStore.IsVoice(_id)) { SetInvisible(0x2A631207); SetInvisible(0x914614E5); }
            SetLanguageHash(0xFECED617, FrontEndData.Hash($"SMS_MESSAGE_{_id}_FROM"));     // RefreshHeader
            SetLanguageHash(0x2C167533, FrontEndData.Hash($"SMS_MESSAGE_{_id}_SUBJECT"));
        }

        /// <summary>uiSMSMessage dtor (0x511F3A): the sound states back.</summary>
        protected override void OnClose()
        {
            NfsMw.Audio.SoundState.SetGameFlow(NfsMw.Audio.SoundState.FlowSms, false);
            NfsMw.Audio.SoundState.Set(true, NfsMw.Audio.SndCtl.FEUpscreen);
            NfsMw.Audio.SoundState.Set(false, NfsMw.Audio.SndCtl.FESmsMessage);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            _scroller?.HandleNotificationMessage(msg);
            switch (msg)
            {
                case FEHash.InitComplete:
                    // [?] the port's unread list stands in for SMS_FLAG_UNREAD (2)
                    if (SmsStore.IsVoice(_id) && SmsStore.Unread(_id)) NfsMw.Audio.SpeechAI.SMSCellCall(_id);
                    SmsStore.MarkRead(_id);
                    break;
                case FEHash.ExitComplete:
                    QueuePackagePop();
                    break;
                case FEHash.PadButton4:
                    if (SmsStore.IsVoice(_id)) NfsMw.Audio.SpeechAI.SMSCellCall(_id);
                    break;
                case FEHash.PadButton5:
                    DialogInterface.ShowTwoButtons(this, "InGameDialog.fng", DialogTitle.Alert, 0x70E01038, 0x417B25E4, MsgYes, MsgNo, MsgNo, true, 1, Lang(0x8C3C2171));
                    break;
                case MsgYes:
                    SmsStore.Delete(_id);
                    View.PackageMessage(FEHash.PadBack);
                    break;
            }
        }
    }
}
