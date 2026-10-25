using System;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// SixDaysLater.fng — uiSixDaysLater (InGame/uiSixDaysLater.cpp; PC ctor 0x58E820, tick 0x56AA70, factory 0x591C10).
    /// Pushed by EShowMessageScreen(id) (destructor 0x625090: QueuePackagePush("SixDaysLater.fng", id)) from the career
    /// script (IntroMessageID). The text 0xB769701E gets DDAY_TIMELAPSE_&lt;id + 1&gt; ("Six Days Earlier...", "2 Days Ago..."),
    /// and EFadeScreenOff(15_IN) is sent. The package runs the background's APPEAR (300 ms) then SHOW (4.2 s), whose 200 ms
    /// event 0xEC5A6043 plays the text's APPEAR then ANIMATE (0x5A8E4EBE, 4 s). Each tick: once the text's APPEAR is set the
    /// image 0x53D9EB7E (Number2) plays APPEAR; when ANIMATE is set and no longer running → QueuePackagePop and
    /// MNotifyMessageDone (the Lua NotifyMessageDone). About 4.7 s, no skip. Hook [ours]: <see cref="Done"/> is raised at
    /// the pop (Story / LuaDrive can push this screen in place of their own card).
    /// </summary>
    public sealed class SixDaysLaterScreen : FEMenuScreen
    {
        public override string Package => "SixDaysLater";
        public override bool Overlay => true;
        const uint Text = 0xB769701E, Number = 0x53D9EB7E, Animate = 0x5A8E4EBE;
        readonly int _id;
        bool _numberShown;
        public Action Done;

        public SixDaysLaterScreen(int id, Action done = null) { _id = id; Done = done; }

        protected override void Setup()
        {
            SetLanguageHash(Text, FrontEndData.Hash($"DDAY_TIMELAPSE_{_id + 1}"));
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg != FEHash.ScreenTick) return;
            var text = Obj(Text);
            if (text == null) return;
            if (!_numberShown && IsScriptSet(text, FEHash.Appear)) { _numberShown = true; SetScript(Number, FEHash.Appear); }
            if (IsScriptSet(text, Animate) && !View.IsScriptRunning(text, Animate))
            {
                var done = Done;
                Done = null;
                QueuePackagePop();
                done?.Invoke();
            }
        }
    }
}
