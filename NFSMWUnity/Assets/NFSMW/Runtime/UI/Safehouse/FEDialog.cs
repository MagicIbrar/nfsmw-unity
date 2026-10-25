using System;

namespace NfsMw.UI
{
    /// <summary>
    /// The safehouse screens' callback-style front for the one dialog implementation, DialogScreen (InGame/Dialogs.cs:
    /// feDialogScreen on Dialog.fng / InGameDialog.fng, run on the FEng engine). Each button's action runs after the dialog
    /// has closed (~feDialogScreen queues ReturnWithMessage to the parent). The signatures are those the safehouse code
    /// already calls; the box itself is no longer drawn here.
    /// </summary>
    public sealed class FEDialog : DialogScreen
    {
        public enum Title { None, Info, Alert, Confirmation, Countdown, FatalError }

        const uint Msg1 = 0xFE000001, Msg2 = 0xFE000002, Msg3 = 0xFE000003, MsgCancel = 0xFE000004;

        FEDialog(FEDialogConfig conf) : base(conf) { }

        static DialogTitle Map(Title t) => t switch
        {
            Title.None => DialogTitle.None, Title.Alert => DialogTitle.Alert, Title.Confirmation => DialogTitle.Confirmation,
            Title.Countdown => DialogTitle.Countdown, Title.FatalError => DialogTitle.FatalError, _ => DialogTitle.Info,
        };

        static FEDialog Show(FrontEnd fe, Title title, string blurb, int first, bool dismissable, float countdown,
            Action cancelled, params (uint text, Action pressed)[] buttons)
        {
            var conf = new FEDialogConfig
            {
                Blurb = blurb ?? "", Title = Map(title), NumButtons = buttons.Length, FirstButton = first,
                Dismissable = dismissable, Countdown = countdown, CancelMessage = MsgCancel,
            };
            if (buttons.Length > 0) { conf.Button1Text = buttons[0].text; conf.Button1Message = Msg1; }
            if (buttons.Length > 1) { conf.Button2Text = buttons[1].text; conf.Button2Message = Msg2; }
            if (buttons.Length > 2) { conf.Button3Text = buttons[2].text; conf.Button3Message = Msg3; }
            conf.OnReply = r =>
            {
                if (r == Msg1) buttons[0].pressed?.Invoke();
                else if (r == Msg2) buttons[1].pressed?.Invoke();
                else if (r == Msg3) buttons[2].pressed?.Invoke();
                else if (r == MsgCancel) cancelled?.Invoke();
            };
            var cur = DialogInterface.Current;
            if (cur != null) DialogInterface.DismissDialog(cur.Config.Handle);
            return (fe ?? FrontEnd.Create()).Push(new FEDialog(conf));
        }

        /// <summary>ShowOk (feDialogBox.cpp:346): one "OK" (0x417B2601) button, dismissable [back runs ok too: ours].</summary>
        public static FEDialog ShowOk(FrontEnd fe, Title title, string blurb, Action ok = null) =>
            Show(fe, title, blurb, 0, true, 0, ok, (0x417B2601, ok));

        /// <summary>ShowOneButton(.., button, pressed msg, blurb): the cancel message is the pressed message (feDialogBox.cpp:440).</summary>
        public static FEDialog ShowOneButton(FrontEnd fe, Title title, uint button, string blurb, Action pressed = null) =>
            Show(fe, title, blurb, 0, true, 0, pressed, (button, pressed));

        /// <summary>ShowTwoButtons(..., b1 text, b2 text, msg1, msg2, cancel msg, first button, blurb): dismissable.</summary>
        public static FEDialog ShowTwoButtons(FrontEnd fe, Title title, uint b1, uint b2, Action on1, Action on2, Action cancelled, int first, string blurb) =>
            Show(fe, title, blurb, first, true, 0, cancelled, (b1, on1), (b2, on2));

        /// <summary>The common yes / no question (LANGUAGE_COMMON_YES 0x70E01038, _NO 0x417B25E4), "No" first (first_dialog_button2).</summary>
        public static FEDialog ShowYesNo(FrontEnd fe, string blurb, Action yes, Action no = null, Title title = Title.Alert) =>
            ShowTwoButtons(fe, title, 0x70E01038, 0x417B25E4, yes, no, no, 1, blurb);

        public static FEDialog ShowThreeButtons(FrontEnd fe, Title title, uint b1, uint b2, uint b3, Action on1, Action on2, Action on3, Action cancelled, int first, string blurb) =>
            Show(fe, title, blurb, first, true, 0, cancelled, (b1, on1), (b2, on2), (b3, on3));

        /// <summary>ShowTwoButtonCountdown (feDialogBox.cpp:503): the blurb is a format with the seconds left (%d); time out = back.</summary>
        public static FEDialog ShowTwoButtonCountdown(FrontEnd fe, uint b1, uint b2, Action on1, Action on2, float seconds, string blurbFormat) =>
            Show(fe, Title.Countdown, blurbFormat, 0, true, seconds, on2, (b1, on1), (b2, on2));
    }
}
