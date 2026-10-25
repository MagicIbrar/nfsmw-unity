using System;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>eDialogTitle (feDialogBox.hpp).</summary>
    public enum DialogTitle { None = 0, Alert = 1, Info = 2, Confirmation = 3, FatalError = 4, Countdown = 5 }

    /// <summary>feDialogConfig (feDialogBox.hpp): one dialog request.</summary>
    public sealed class FEDialogConfig
    {
        public string Blurb = "";
        public DialogTitle Title = DialogTitle.Info;
        public uint Button1Text, Button1Message, Button2Text, Button2Message, Button3Text, Button3Message, CancelMessage;
        public int FirstButton;
        /// <summary>from_pkg: the screen that gets the reply (cFEng::QueueGameMessage(ReturnWithMessage, ParentPackage)).</summary>
        public FEScreen Parent;
        /// <summary>"" / null = InGameDialog in game, Dialog in the front end; "animating", "3button", "3buttons_stacked" as in ShowDialog.</summary>
        public string DialogPackage;
        public int NumButtons;
        public bool Dismissable;
        public float Countdown;
        public int Handle;
        /// <summary>[ours] the reply also goes to this callback (for screens that are not on the FEng engine).</summary>
        public Action<uint> OnReply;
    }

    /// <summary>
    /// DialogInterface (Frontend/MenuScreens/Common/feDialogBox.cpp; PC: feDialogConfig ctor 0x56C5E0, DismissDialog
    /// 0x56C6E0, feDialogScreen::NotificationMessage 0x57EE60). One dialog at a time: showing a new one dismisses the old.
    /// </summary>
    public static class DialogInterface
    {
        public const uint MsgOk = 0x34DC1BEC, MsgYes = 0xD05FC3A3, MsgNo = 0x34DC1BCF, MsgCancel = 0x1FAB5998, MsgCancelled = 0xB4EDEB6D;
        static int _nextHandle = 4;
        static DialogScreen _current;

        public static DialogScreen Current => _current != null && !_current.IsClosed ? _current : null;

        internal static int NextHandle() => _nextHandle++;
        internal static void SetCurrent(DialogScreen d) => _current = d;

        /// <summary>ShowDialog's package choice.</summary>
        internal static string ResolvePackage(string pkg)
        {
            if (!string.IsNullOrEmpty(pkg))
            {
                if (pkg == "animating") pkg = "OL_Dialog";
                else if (pkg == "3button") pkg = "Dialog";
                else if (pkg == "3buttons_stacked") pkg = "OL_Dialog_Stacked_Buttons";
                if (pkg.EndsWith(".fng", StringComparison.OrdinalIgnoreCase)) pkg = pkg.Substring(0, pkg.Length - 4);
            }
            else pkg = FEMenuScreen.InGame ? "InGameDialog" : "Dialog";
            FrontEndData.Load();
            return FrontEndData.Packages.ContainsKey(pkg) ? pkg : "GenericDialog_ThreeButton";
        }

        public static int ShowDialog(FEDialogConfig conf)
        {
            if (Current != null) DismissDialog(Current.Config.Handle);
            var fe = FrontEnd.Create();
            _current = fe.Push(new DialogScreen(conf));
            return conf.Handle;
        }

        /// <summary>DismissDialog: pops the dialog package when it is on top, then broadcasts 0xD731D75E.</summary>
        public static void DismissDialog(int handle)
        {
            var d = Current;
            if (d == null || d.Config.Handle != handle) return;
            d.Dismiss();
            _current = null;
        }

        static FEDialogConfig Conf(FEScreen from, string dlg, DialogTitle title, string blurb) =>
            new FEDialogConfig { Parent = from, DialogPackage = dlg, Title = title, Blurb = blurb ?? "" };

        public static int ShowMessage(FEScreen from, string dlg, DialogTitle title, string blurb)
        {
            var c = Conf(from, dlg, title, blurb);
            c.NumButtons = 0;
            c.Dismissable = false;
            return ShowDialog(c);
        }

        public static int ShowOk(FEScreen from, string dlg, DialogTitle title, string blurb, Action<uint> reply = null)
        {
            var c = Conf(from, dlg, title, blurb);
            c.Button1Text = FEHash.LabelOk; c.Button1Message = MsgOk; c.CancelMessage = MsgCancelled;
            c.FirstButton = 0; c.NumButtons = 1; c.Dismissable = true; c.OnReply = reply;
            return ShowDialog(c);
        }

        public static int ShowOkCancel(FEScreen from, string dlg, DialogTitle title, string blurb, Action<uint> reply = null)
        {
            var c = Conf(from, dlg, title, blurb);
            c.FirstButton = 1; c.Dismissable = true; c.NumButtons = 2;
            c.Button1Text = FEHash.LabelOk; c.Button1Message = MsgOk;
            c.Button2Text = FEHash.LabelCancel; c.Button2Message = MsgCancel;
            c.CancelMessage = MsgCancelled; c.OnReply = reply;
            return ShowDialog(c);
        }

        public static int ShowOneButton(FEScreen from, string dlg, DialogTitle title, uint text, uint pressed, uint cancel, bool dismissable, string blurb, Action<uint> reply = null)
        {
            var c = Conf(from, dlg, title, blurb);
            c.NumButtons = 1; c.Button1Text = text; c.Button1Message = pressed; c.CancelMessage = cancel;
            c.FirstButton = 0; c.Dismissable = dismissable; c.OnReply = reply;
            return ShowDialog(c);
        }

        /// <summary>ShowTwoButtons(.., cancel_message, dismissable, first_button, blurb).</summary>
        public static int ShowTwoButtons(FEScreen from, string dlg, DialogTitle title, uint text1, uint text2, uint msg1, uint msg2,
            uint cancel, bool dismissable, int firstButton, string blurb, Action<uint> reply = null)
        {
            var c = Conf(from, dlg, title, blurb);
            c.NumButtons = 2; c.Button1Text = text1; c.Button1Message = msg1; c.Button2Text = text2; c.Button2Message = msg2;
            c.CancelMessage = cancel; c.FirstButton = firstButton; c.Dismissable = dismissable; c.OnReply = reply;
            return ShowDialog(c);
        }

        public static int ShowThreeButtons(FEScreen from, string dlg, DialogTitle title, uint text1, uint text2, uint text3,
            uint msg1, uint msg2, uint msg3, uint cancel, int firstButton, string blurb, Action<uint> reply = null)
        {
            var c = Conf(from, dlg, title, blurb);
            c.NumButtons = 3; c.Button1Text = text1; c.Button1Message = msg1; c.Button2Text = text2; c.Button2Message = msg2;
            c.Button3Text = text3; c.Button3Message = msg3; c.CancelMessage = cancel; c.FirstButton = firstButton;
            c.Dismissable = true; c.OnReply = reply;
            return ShowDialog(c);
        }

        public static int ShowTwoButtonCountdown(FEScreen from, string dlg, uint text1, uint text2, uint msg1, uint msg2, uint cancel,
            int firstButton, float seconds, string blurb, Action<uint> reply = null)
        {
            var c = Conf(from, dlg, DialogTitle.Countdown, blurb);
            c.NumButtons = 2; c.Button1Text = text1; c.Button1Message = msg1; c.Button2Text = text2; c.Button2Message = msg2;
            c.CancelMessage = cancel; c.FirstButton = firstButton; c.Dismissable = true; c.Countdown = seconds; c.OnReply = reply;
            return ShowDialog(c);
        }
    }

    /// <summary>
    /// feDialogScreen (feDialogBox.cpp:66-245): Dialog.fng / InGameDialog.fng over the parent package. BuildFromConfig fills
    /// the blurb (0x1E2640FA), the three button labels (0xF9363F30 / 0xFB8B67D1 / 0xFDE09072; unused buttons are disabled
    /// and hidden), the size message (strlen · font height &lt; 2200 small, &lt; 4400 medium, else large) and the header /
    /// icon by title. INIT_COMPLETE highlights FirstButton; BUTTON_PRESSED on Button1..3 returns its message; PAD_BACK
    /// returns the cancel message when dismissable. On closing, the reply goes to the parent.
    /// </summary>
    public class DialogScreen : FEMenuScreen
    {
        public readonly FEDialogConfig Config;
        uint _return;
        uint _lastButton;
        float _start;
        public override string Package => Config.DialogPackage;
        public override bool Overlay => true;
        public bool IsClosed => Closed;

        public DialogScreen(FEDialogConfig conf)
        {
            Config = conf;
            conf.DialogPackage = DialogInterface.ResolvePackage(conf.DialogPackage);
            if (conf.Handle == 0) conf.Handle = DialogInterface.NextHandle();
        }

        protected override void Setup()
        {
            DialogInterface.SetCurrent(this);
            _return = 0;
            _start = Time.realtimeSinceStartup;
            BuildFromConfig();
        }

        void BuildFromConfig()
        {
            const uint blurb = 0x1E2640FA, b1 = 0xF9363F30, b2 = 0xFB8B67D1, b3 = 0xFDE09072;
            Printf(blurb, Config.Title == DialogTitle.Countdown ? CareerScreenKit.Format(Config.Blurb, (int)Config.Countdown) : CareerScreenKit.Format(Config.Blurb));
            switch (Config.NumButtons)
            {
                case 3:
                    SetLanguageHash(b1, Config.Button1Text); SetLanguageHash(b2, Config.Button2Text); SetLanguageHash(b3, Config.Button3Text);
                    break;
                case 2:
                    View.DisableButton(Obj(FEHash.Button3)); SetInvisible(FEHash.Button3); Printf(b3, "");
                    SetLanguageHash(b1, Config.Button1Text); SetLanguageHash(b2, Config.Button2Text);
                    break;
                case 1:
                    View.DisableButton(Obj(FEHash.Button3)); View.DisableButton(Obj(FEHash.Button2));
                    SetInvisible(FEHash.Button3); SetInvisible(FEHash.Button2);
                    Printf(b3, ""); Printf(b2, "");
                    SetLanguageHash(b1, Config.Button1Text);
                    break;
                default:
                    View.DisableButton(Obj(FEHash.Button1)); View.DisableButton(Obj(FEHash.Button2)); View.DisableButton(Obj(FEHash.Button3));
                    SetInvisible(FEHash.Button1); SetInvisible(FEHash.Button2); SetInvisible(FEHash.Button3);
                    SetInvisible(0x07F9DCA9);
                    Printf(b1, ""); Printf(b2, ""); Printf(b3, "");
                    break;
            }
            // FindFont(0x545570C6)->GetHeight() · strlen(blurb)
            var font = FrontEndData.FontFor("FONT_MW_BODY.ffn");
            float lines = (Config.Blurb?.Length ?? 0) * (font != null ? font.LineHeight : 20);
            View.PackageMessage(lines < 2200 ? 0x79B0C1C7u : lines < 4400 ? 0xA13ADCAFu : 0x792BC959u);
            switch (Config.Title)
            {
                case DialogTitle.Alert:
                case DialogTitle.FatalError:
                    SetLanguageHash(FEHash.HeaderText, InGame ? 0x2BD146D3u : 0x6FD91524u);
                    SetTextureHash(0x0D4F4069, 0x6948E2B3); SetTextureHash(0xFAC88427, 0x6948E2B3);
                    break;
                case DialogTitle.Confirmation:
                    SetLanguageHash(FEHash.HeaderText, 0x60249A74);
                    SetTextureHash(0x0D4F4069, 0x39949433); SetTextureHash(0xFAC88427, 0x39949433);
                    break;
                default:
                    SetLanguageHash(FEHash.HeaderText, 0xDBE419D4);
                    SetTextureHash(0x0D4F4069, 0x1A7AFE27); SetTextureHash(0xFAC88427, 0x1A7AFE27);
                    break;
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.InitComplete:
                    if (Config.NumButtons != 0)
                    {
                        View.SetCurrentButton(Config.FirstButton == 0 ? FEHash.Button1 : Config.FirstButton == 1 ? FEHash.Button2 : FEHash.Button3);
                        _lastButton = (uint)Config.FirstButton;
                    }
                    break;
                case FEHash.ScreenTick:
                    if (Config.Title == DialogTitle.Countdown)
                    {
                        int elapsed = (int)(Time.realtimeSinceStartup - _start);
                        Printf(0x1E2640FA, CareerScreenKit.Format(Config.Blurb, (int)Config.Countdown - elapsed));
                        if (elapsed >= Config.Countdown) NotificationMessage(FEHash.PadBack, null);
                    }
                    break;
                case FEHash.PadBack:
                    if (Config.Dismissable) { _return = Config.CancelMessage; DialogInterface.DismissDialog(Config.Handle); }
                    break;
                case FEHash.PadUp:
                    if (obj == null) break;
                    if (obj.Obj.Hash == FEHash.Button2 && Config.NumButtons == 3) View.SetCurrentButton(FEHash.Button1);
                    else if (obj.Obj.Hash == FEHash.Button3 && Config.NumButtons != 1) View.SetCurrentButton(FEHash.Button2);
                    break;
                case FEHash.PadDown:
                    if (obj == null) break;
                    if (obj.Obj.Hash == FEHash.Button1) View.SetCurrentButton(FEHash.Button2);
                    else if (obj.Obj.Hash == FEHash.Button2) View.SetCurrentButton(FEHash.Button3);
                    break;
                case FEHash.ButtonPressed:
                    if (obj == null || Config.NumButtons == 0) break;
                    if (obj.Obj.Hash == FEHash.Button1) { _return = Config.Button1Message; DialogInterface.DismissDialog(Config.Handle); }
                    else if (obj.Obj.Hash == FEHash.Button2) { _return = Config.Button2Message; DialogInterface.DismissDialog(Config.Handle); }
                    else if (obj.Obj.Hash == FEHash.Button3) { _return = Config.Button3Message; DialogInterface.DismissDialog(Config.Handle); }
                    break;
            }
        }

        /// <summary>
        /// Presses button n (0-based) at once, as BUTTON_PRESSED on ButtonN would (tools / the QA tour; works before the
        /// package's INIT_COMPLETE).
        /// </summary>
        public void Press(int n)
        {
            if (Closed || n < 0 || n >= Config.NumButtons) return;
            _return = n == 0 ? Config.Button1Message : n == 1 ? Config.Button2Message : Config.Button3Message;
            DialogInterface.DismissDialog(Config.Handle);
            if (!Closed) Dismiss();
        }

        /// <summary>DismissDialog's pop (QueuePackagePop(1)) and the 0xD731D75E broadcast.</summary>
        internal void Dismiss()
        {
            if (Closed) return;
            var fe = FE;
            if (fe != null && fe.Top == this) fe.Pop();
            else OnClose();
            if (View) View.QueueMessage(0xD731D75E, null, FEView.ToBroadcast);
        }

        bool _replied;

        /// <summary>~feDialogScreen: QueueGameMessage(ReturnWithMessage, ParentPackage) (after the pop completes).</summary>
        protected override void OnClose()
        {
            if (_replied) return;
            _replied = true;
            uint ret = _return;
            var parent = Config.Parent as FEMenuScreen;
            var reply = Config.OnReply;
            var fe = FE ?? FrontEnd.Instance;
            void Deliver()
            {
                if (ret != 0xFFFFFFFF) parent?.QueueGameMessage(ret);
                reply?.Invoke(ret);
            }
            if (fe != null) fe.Defer(Deliver); else Deliver();
        }
    }

    /// <summary>
    /// OK / Cancel confirmation (DialogInterface::ShowTwoButtons(.., "InGameDialog.fng" in game / "Dialog.fng" in the front end,
    /// dialog_alert, OK 0x417B2601, Cancel 0x1A294DAD, .., first_dialog_button2, blurb), as the pause menu (0x51DA30) and the
    /// post-race restart use it. ok runs after the dialog has closed, when OK was chosen. [ours] A non-empty title goes on
    /// the blurb's first line: the original dialogs have no custom titles.
    /// </summary>
    public sealed class ConfirmScreen : DialogScreen
    {
        public ConfirmScreen(string title, string body, Action ok) : base(new FEDialogConfig
        {
            Blurb = string.IsNullOrEmpty(title) ? body : title + "^" + body,
            Title = DialogTitle.Alert, NumButtons = 2, Dismissable = true, FirstButton = 1,
            Button1Text = FEHash.LabelOk, Button1Message = DialogInterface.MsgOk,
            Button2Text = FEHash.LabelCancel, Button2Message = DialogInterface.MsgCancel,
            CancelMessage = DialogInterface.MsgCancelled,
            OnReply = r => { if (r == DialogInterface.MsgOk) ok?.Invoke(); },
        }) { }
    }

    /// <summary>A one-button OK message (DialogInterface::ShowOk, dialog_info). [ours] The title goes on the blurb's first line.</summary>
    public sealed class MessageScreen : DialogScreen
    {
        public MessageScreen(string title, string body) : base(new FEDialogConfig
        {
            Blurb = string.IsNullOrEmpty(title) ? body : string.IsNullOrEmpty(body) ? title : title + "^" + body,
            Title = DialogTitle.Info, NumButtons = 1, Dismissable = true, FirstButton = 0,
            Button1Text = FEHash.LabelOk, Button1Message = DialogInterface.MsgOk, CancelMessage = DialogInterface.MsgCancelled,
        }) { }
    }
}
