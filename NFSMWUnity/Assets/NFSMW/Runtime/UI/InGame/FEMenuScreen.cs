using System;
using System.Collections.Generic;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>FEng message / script hashes used by the in-game screens (Frontend/FEngHashes/*.hpp).</summary>
    public static class FEHash
    {
        public const uint Init = 0x001744B3, Show = 0x001CA7C0, Hide = 0x0016A259, Appear = 0x5079C8F8, Leave = 0x033113AC,
            LeaveAll = 0x97BDB465, Highlight = 0x249DB7B7, UnHighlight = 0x7AB5521A, Grey = 0x00163C76, Pulse = 0x03826A28,
            InitComplete = 0x35F8620B, ExitComplete = 0xE1FDE1D1, EnableInput = 0xC6341FF6, BackDelay = 0xCB1AA27F,
            Forward = 0xADE14EC3, ScriptForward = 0x001335F0, ScreenTick = 0xC98356BA,
            ButtonPressed = 0x0C407210, PadAccept = 0x406415E3, PadBack = 0x911AB364, PadStart = 0xB5AF2461,
            PadUp = 0x72619778, PadDown = 0x911C0A4B, PadLeft = 0x9120409E, PadRight = 0xB5971BF1,
            PadLTrigger = 0x5073EF13, PadRTrigger = 0xD9FEEC59,
            PadButton0 = 0xC519BFBF, PadButton1 = 0xC519BFC0, PadButton2 = 0xC519BFC1, PadButton3 = 0xC519BFC2,
            PadButton4 = 0xC519BFC3, PadButton5 = 0xC519BFC4,
            Button1 = 0xB8A7C6CC, Button2 = 0xB8A7C6CD, Button3 = 0xB8A7C6CE, HeaderText = 0x42ADB44C,
            // language labels that many screens share
            LabelOk = 0x417B2601, LabelCancel = 0x1A294DAD, LabelNA = 0x0FC1BF40;
    }

    /// <summary>
    /// MenuScreen (Frontend/MenuScreens/Common/FEMenuScreen.cpp) on the FEng engine: the package's script events and
    /// responses reach NotificationMessage (target 0xFFFFFFFF), keyboard / pad / mouse input is fed to the package as FEng
    /// pad messages while the screen has control, and FEMSG_SCREEN_TICK arrives every frame.
    /// PC keys → FE pad (from the PC key glyphs' click responses in the packages, `feig pckeys`): Enter = accept,
    /// Esc = back, 1 = PAD_BUTTON5, 2 = PAD_BUTTON4; arrows = directions; PageUp / PageDown and the shoulders =
    /// L / R trigger [?] (the PC binding table for these is not read).
    /// </summary>
    public abstract class FEMenuScreen : FEScreen
    {
        public override bool ExeEngine => true;
        protected bool Closed { get; private set; }

        public override void Open()
        {
            View.GameMessage += OnGameMessage;
            View.SoundMessage += (m, o) => BaseNotifySound(m, o);
            View.PackageCommand += OnPackageCommand;
            View.MessagesEnabled = true;
            // MenuScreen ctor (PC 0x586329): FEngSetButtonTexture on L1 0x6B364F8B ("L1" 0x5BC), R1 0x79354351 ("R1" 0x682) and
            // the Esc key image 0x1BB3C514 — FEngSetButtonTexture 0x515BB0 swaps PC_key_esc for 0xEC617269 in French
            if (Core.Localize.CurrentLanguage == Core.Language.French) View.SetTextureHash(View.Find(0x1BB3C514), 0xEC617269);
            Setup();
            if (!Closed) View.ProcessMessages();
        }

        public override void Close()
        {
            Closed = true;
            if (View) View.MessagesEnabled = false;
            OnClose();
        }

        /// <summary>The constructor work of the original screen (runs once the package is built).</summary>
        protected virtual void Setup() { }
        protected virtual void OnClose() { }
        /// <summary>MenuScreen::NotificationMessage(msg, obj, param1, param2).</summary>
        protected virtual void NotificationMessage(uint msg, FEView.Node obj) { }
        /// <summary>
        /// MenuScreen::BaseNotifySound (PC 0x5724D0): a package sound message → its eMenuSoundTriggers (the PC table is
        /// NfsMw.Audio.FeSound's), the screen's NotifySoundMessage may drop it, then EAXSound::PlayUISoundFX. FrontEnd's
        /// generic key sounds are not played for FEng-engine screens (FrontEnd.Update hook), so these are their only sounds.
        /// </summary>
        void BaseNotifySound(uint msg, FEView.Node obj)
        {
            if (Closed || !NotifySoundMessage(msg, obj)) return;
            NfsMw.Audio.FeSound.Notify(msg);
        }

        /// <summary>MenuScreen::NotifySoundMessage(msg, maybe): false silences the sound (the default keeps it).</summary>
        protected virtual bool NotifySoundMessage(uint msg, FEView.Node obj) => true;

        void OnGameMessage(uint msg, FEView.Node obj)
        {
            if (!Closed) NotificationMessage(msg, obj);
        }

        /// <summary>MR_SwitchToPackage / MR_Push* / MR_PopPackage from a response: only the pop is generic.</summary>
        protected virtual void OnPackageCommand(uint command, string package)
        {
            if (command == Core.FEResponse.PopPackage) QueuePackagePop();
        }

        /// <summary>cFEng::QueueGameMessage(msg, this package): delivered to NotificationMessage on the next message pass.</summary>
        public void QueueGameMessage(uint msg)
        {
            if (View) View.QueueMessage(msg, null, FEView.ToGame);
        }

        /// <summary>cFEng::QueuePackagePop(1) for this screen.</summary>
        protected void QueuePackagePop()
        {
            if (Closed || FE == null) return;
            if (FE.Top == this) FE.Pop();
            else Closed = true;
        }

        public override void Tick(MenuInput input)
        {
            if (Closed || !View) return;
            if (FE.Top == this && View.InputEnabled)
            {
                Feed(input);
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m != null) View.Mouse(CareerScreenKit.Mouse(View), m.leftButton.isPressed, m.rightButton.isPressed);
            }
            View.ProcessMessages();
            if (!Closed && View) NotificationMessage(FEHash.ScreenTick, null);
        }

        /// <summary>FEngine::ProcessPadsForPackage with the keyboard / pad state of this frame.</summary>
        protected virtual void Feed(MenuInput i)
        {
            if (i.Up) View.PadDirection(0);
            if (i.Right) View.PadDirection(2);
            if (i.Down) View.PadDirection(4);
            if (i.Left) View.PadDirection(6);
            if (i.Accept) View.PadAccept();
            if (i.Back) View.PadButton(FEHash.PadBack);
            if (i.Key1) View.PadButton(FEHash.PadButton5);
            if (i.Key2) View.PadButton(FEHash.PadButton4);
            if (i.LTrigger) View.PadButton(FEHash.PadLTrigger);
            if (i.RTrigger) View.PadButton(FEHash.PadRTrigger);
        }

        // ---- FEngInterface helpers -------------------------------------------------------------------------------

        protected FEView.Node Obj(uint hash) => View.Find(hash);
        protected FEView.Node Obj(string name) => View.Find(name);

        /// <summary>FEngSetScript(pkg, obj, script, restart).</summary>
        protected void SetScript(uint obj, uint script, bool restart = true) => View.SetScript(obj, script, restart);
        protected void SetScript(FEView.Node obj, uint script, bool restart = true) => View.SetScript(obj, script, restart);
        protected bool IsScriptSet(uint obj, uint script) => View.IsScriptSet(obj, script);
        protected bool IsScriptSet(FEView.Node obj, uint script) => View.IsScriptSet(obj, script);

        /// <summary>FEPrintf(pkg, obj, ...): the string, or every string under a group.</summary>
        protected void Printf(uint obj, string text) => Printf(View.Find(obj), text);
        protected void Printf(FEView.Node n, string text)
        {
            if (n == null) return;
            if (n.Label != null) View.SetText(n, text);
            else if (n.Obj.Type == Core.FEObjectType.Group)
                foreach (var d in View.Descendants(n)) if (d.Label != null) View.SetText(d, text);
        }

        /// <summary>FEngSetLanguageHash(pkg, obj, label): the localised string (a group: all of its strings).</summary>
        protected void SetLanguageHash(uint obj, uint label) => Printf(obj, Lang(label));
        protected void SetLanguageHash(FEView.Node obj, uint label) => Printf(obj, Lang(label));

        /// <summary>GetLocalizedString(label) (0x56BC10): unknown labels give the 0x9BB9CCC3 string ("FENG: Default string error.").</summary>
        public static string Lang(uint label) => FrontEndData.Localized(label);

        protected void SetVisible(uint obj) => View.SetVisibleTree(View.Find(obj));
        protected void SetInvisible(uint obj) => View.SetInvisible(View.Find(obj));

        /// <summary>FEngSetTextureHash(image, texture hash).</summary>
        protected void SetTextureHash(uint obj, uint tex)
        {
            var n = View.Find(obj);
            var t = FrontEndData.Texture(tex);
            if (n?.Image != null && t != null) { n.Image.texture = t; }
        }

        /// <summary>FEngHashString("%s%d", name, n) / FEngHashString("LINE%d_GROUP", n).</summary>
        protected static uint HashF(string name) => FrontEndData.Hash(name);

        /// <summary>
        /// TheGameFlowManager.IsInGame(): the world is loaded and no front-end (safehouse / car lot / main menu) screen is
        /// open — the port keeps the city loaded under the safehouse menus, where the exe is in its front end.
        /// </summary>
        public static bool InGame
        {
            get
            {
                if (GameBootstrap.Instance == null || GameBootstrap.Instance.Player == null) return false;
                var fe = FrontEnd.Instance;
                if (fe != null)
                    foreach (var s in fe.Stack)
                        if (s.ShowCar || s.Package == "MainMenu_Sub" || s.Package == "MainMenu") return false;
                return true;
            }
        }

        /// <summary>Speed units option (FEDatabase GameplaySettings.SpeedoUnits): 1 = metric.</summary>
        public static bool Metric => Settings.Kmh;
    }
}
