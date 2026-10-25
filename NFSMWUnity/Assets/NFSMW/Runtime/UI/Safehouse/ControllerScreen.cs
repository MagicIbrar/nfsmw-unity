using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NfsMw.UI
{
    /// <summary>
    /// Options_PC_Controller.fng / Pause_PC_Controller.fng — UIOptionsPCController (PC only: CreatePausePCController 0x571D00,
    /// ctor 0x545EB0, Setup 0x52A7B0, Populate 0x52AC70, RefreshHeader 0x52AB90, NotificationMessage 0x545F70, capture
    /// 0x52AF70, assign 0x6285F0, clear 0x628560, defaults 0x51D6E0 / 0x6284D0, key names 0x628230). An ArrayScrollerMenu 2 wide
    /// × 8 high over the 19 action rows of the live binding table (0x91F418; keyboard mode shows 18, the joystick mode adds
    /// Pause): slot n = KEY_BOX_P/Sn (the button), KEY_TEXT_n (action label = bStringHash of the action name), KEY_TEXT_P/Sn
    /// ("%s" key name, "---" unbound). Accept / click (0x936A6A7F) starts a capture: 0xB2D837B0 = the action, 0x3BBE5E51 = the
    /// current key, 0xD707AE8D (popup SHOW) and 0x13FD3296 (input off) broadcast; the next key is assigned (Esc cancels),
    /// then 0x9B2BA546 (popup HIDE) and 0x8CB81F09. Assign rules: a secondary with an empty primary fills the primary; the
    /// same key in the other column clears that column; every other action loses the key (its secondary moves up).
    /// Key 2 / PAD_BUTTON4 (0x2DCC19C8) clears the cell (a primary takes its secondary); key 1 / PAD_BUTTON5 asks 0x8AEF5AE8
    /// then restores the defaults (0x8F6D80). The device header 8A41F5B9 hides without a joystick, the rumble row 9CCF9099
    /// in keyboard mode; 0x4E11CF90 = the device name. Back → 0x587C018B → EXIT_COMPLETE → Pause_Main (pop) or MainMenu_Sub.
    /// Port: bindings live in FEDb.Data.KeyBindings as UnityEngine.InputSystem.Key values [hook: the drive / HUD input
    /// readers apply them]; the keyboard only (no joystick device: the triggers are ignored as in the exe). Key names are
    /// the Key enum names with the DirectInput spellings for the common keys [?: DirectInput's exact strings].
    /// </summary>
    public sealed class ControllerScreen : FEMenuScreen
    {
        public override string Package => _fromPause ? "Pause_PC_Controller" : "Options_PC_Controller";
        const int W = 2, H = 8;
        const uint MsgCell = 0x936A6A7F, MsgClear = 0x2DCC19C8, MsgDefaults = 0xD05FC3A3, LeaveScreen = 0x587C018B;
        readonly bool _fromPause;
        int _index;      // row * 2 + col
        int _top;        // first visible row
        bool _capturing;
        string _device = "Keyboard";
        FEScrollBar _bar;
        List<KeyBinding> _backup;

        public ControllerScreen(bool fromPause = false) { _fromPause = fromPause; }

        /// <summary>The action table (0x91F418 names, 0x8F6D80 keyboard defaults): label hash = bStringHash(name).</summary>
        public static readonly (string name, Key primary, Key secondary)[] Actions =
        {
            ("GAME_ACTION_GAS", Key.UpArrow, Key.None), ("GAME_ACTION_BRAKE", Key.DownArrow, Key.None),
            ("GAME_ACTION_STEERLEFT", Key.LeftArrow, Key.None), ("GAME_ACTION_STEERRIGHT", Key.RightArrow, Key.None),
            ("GAME_ACTION_HANDBRAKE", Key.Space, Key.None), ("GAME_ACTION_GAMEBREAKER", Key.RightCtrl, Key.G),
            ("GAME_ACTION_NOS", Key.LeftAlt, Key.X), ("GAME_ACTION_SHIFTDOWN", Key.LeftCtrl, Key.None),
            ("GAME_ACTION_SHIFTUP", Key.LeftShift, Key.None), ("GAME_ACTION_RESET", Key.R, Key.None),
            ("HUDACTION_ENGAGE_EVENT", Key.Enter, Key.None), ("HUDACTION_PAD_DOWN", Key.B, Key.None),
            ("HUDACTION_PAD_LEFT", Key.M, Key.None), ("HUDACTION_PAD_RIGHT", Key.Tab, Key.None),
            ("FE_ACTION_B2", Key.T, Key.None), ("CAM_ACTION_CHANGE", Key.C, Key.None),
            ("CAM_ACTION_LOOKBACK", Key.L, Key.None), ("CAM_ACTION_PULLBACK", Key.P, Key.None),
            ("HUDACTION_PAUSEREQUEST", Key.Escape, Key.None),
        };
        const int KeyboardRows = 18;   // the Pause row is joystick-only

        /// <summary>FEDb.Data.KeyBindings, filled with the defaults when empty (0x628410).</summary>
        public static List<KeyBinding> Bindings()
        {
            var list = FEDb.Data.KeyBindings;
            if (list.Count != Actions.Length)
            {
                list.Clear();
                foreach (var a in Actions) list.Add(new KeyBinding { Action = a.name, Primary = (int)a.primary, Secondary = (int)a.secondary });
            }
            return list;
        }

        /// <summary>The bound key of an action for the input readers [hook]: Primary, else Secondary, else Key.None.</summary>
        public static Key KeyOf(string action, bool secondary = false)
        {
            foreach (var b in Bindings())
                if (b.Action == action) return (Key)(secondary ? b.Secondary : b.Primary);
            return Key.None;
        }

        /// <summary>IDirectInputDevice8::GetObjectInfo names for the common keys [?], else the Key enum name.</summary>
        public static string KeyName(Key k)
        {
            if (k == Key.None) return "---";
            switch (k)
            {
                case Key.UpArrow: return "Up"; case Key.DownArrow: return "Down"; case Key.LeftArrow: return "Left"; case Key.RightArrow: return "Right";
                case Key.LeftCtrl: return "Left Ctrl"; case Key.RightCtrl: return "Right Ctrl"; case Key.LeftShift: return "Left Shift";
                case Key.RightShift: return "Right Shift"; case Key.LeftAlt: return "Left Alt"; case Key.RightAlt: return "Right Alt";
                case Key.Enter: return "Enter"; case Key.NumpadEnter: return "Num Enter"; case Key.Escape: return "Esc"; case Key.Space: return "Space";
                case Key.Backspace: return "Backspace"; case Key.Tab: return "Tab"; case Key.CapsLock: return "Caps Lock";
                case Key.PageUp: return "Page Up"; case Key.PageDown: return "Page Down"; case Key.Home: return "Home"; case Key.End: return "End";
                case Key.Insert: return "Insert"; case Key.Delete: return "Delete";
            }
            string s = k.ToString();
            if (s.StartsWith("Digit")) return s.Substring(5);
            if (s.StartsWith("Numpad")) return "Num " + s.Substring(6);
            return s;
        }

        protected override void Setup()
        {
            var list = Bindings();
            _backup = new List<KeyBinding>();
            foreach (var b in list) _backup.Add(new KeyBinding { Action = b.Action, Primary = b.Primary, Secondary = b.Secondary });
            _bar = new FEScrollBar(View, "ScrollBar");
            var kb = Keyboard.current;
            _device = kb != null ? (string.IsNullOrEmpty(kb.displayName) ? "Keyboard" : kb.displayName) : "Keyboard";
            RefreshHeader();
            Populate();
        }

        /// <summary>RefreshHeader (0x52AB90): no joystick → device group HIDE, rumble row hidden; device name.</summary>
        void RefreshHeader()
        {
            SetScript(0x8A41F5B9, FEHash.Hide);
            SetInvisible(0x9CCF9099);
            Printf(0x4E11CF90, _device);
        }

        static uint Label(int row) => EaHash.Hash(Actions[row].name);   // bStringHash (case-sensitive, 0x460BF0)

        /// <summary>Populate + the slots' Update (0x51D3A0): labels, key names, the current button.</summary>
        void Populate()
        {
            var list = Bindings();
            int row = _index / W;
            if (row < _top) _top = row;
            if (row >= _top + H) _top = row - H + 1;
            for (int n = 1; n <= H; n++)
            {
                int r = _top + n - 1;
                bool has = r < KeyboardRows;
                var text = Obj("KEY_TEXT_" + n);
                var boxP = Obj("KEY_BOX_P" + n);
                var boxS = Obj("KEY_BOX_S" + n);
                var keyP = Obj("KEY_TEXT_P" + n);
                var keyS = Obj("KEY_TEXT_S" + n);
                foreach (var o in new[] { text, boxP, boxS, keyP, keyS })
                    if (o != null) { if (has) View.SetVisibleTree(o); else View.SetInvisible(o); }
                if (!has) continue;
                SetLanguageHash(text, Label(r));
                Printf(keyP, KeyName((Key)list[r].Primary));
                Printf(keyS, KeyName((Key)list[r].Secondary));
                SetScript(boxP, FEHash.Init);
                SetScript(boxS, FEHash.Init);
            }
            _bar.Update(H, KeyboardRows, _top + 1, row + 1);
            var box = Obj((_index % W == 0 ? "KEY_BOX_P" : "KEY_BOX_S") + (row - _top + 1));
            if (box != null && View.CurrentButton != box) View.SetCurrentButton(box);
        }

        protected override void Feed(MenuInput i)
        {
            if (_capturing) return;   // input processing is off while the popup shows
            int was = _index, row = _index / W, col = _index % W;
            if (i.Up && row > 0) row--;
            if (i.Down && row < KeyboardRows - 1) row++;
            // ArrayScroller::ScrollHor (feArrayScrollerMenu.cpp:113): left / right wrap inside the row (index ± 1, a row edge
            // adds / subtracts the width); ScrollVer with the package's ARRAY_SCROLL_REGION stops at the first / last row
            if (i.Left) col = col == 0 ? W - 1 : col - 1;
            if (i.Right) col = col == W - 1 ? 0 : col + 1;
            _index = row * W + col;
            if (_index != was) Populate();
            if (i.Accept) NotificationMessage(MsgCell, null);
            if (i.Back) View.PadButton(FEHash.PadBack);
            if (i.Key1) View.PadButton(FEHash.PadButton5);
            if (i.Key2) View.PadButton(FEHash.PadButton4);
        }

        public override void Tick(MenuInput input)
        {
            base.Tick(input);
            if (_capturing && View) Capture();
        }

        /// <summary>The capture poll (0x6349B0): Esc cancels, any other key is assigned.</summary>
        void Capture()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (!kb.anyKey.wasPressedThisFrame) return;
            Key pressed = Key.None;
            foreach (var c in kb.allKeys) if (c.wasPressedThisFrame) { pressed = c.keyCode; break; }
            if (pressed == Key.None) return;
            if (pressed != Key.Escape) Assign(_index / W, _index % W == 1, pressed);
            _capturing = false;
            View.QueueMessage(0x9B2BA546, null, FEView.ToBroadcast);
            View.QueueMessage(0x8CB81F09, null, FEView.ToBroadcast);
            Populate();
        }

        /// <summary>The assign rules (0x6285F0).</summary>
        static void Assign(int row, bool secondary, Key key)
        {
            var list = Bindings();
            var b = list[row];
            int k = (int)key;
            if (secondary)
            {
                if (b.Primary == 0) b.Primary = k;
                else if (b.Primary == k) b.Secondary = 0;
                else b.Secondary = k;
            }
            else
            {
                b.Primary = k;
                if (b.Secondary == k) b.Secondary = 0;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (i == row) continue;
                var o = list[i];
                if (o.Primary == k) { o.Primary = o.Secondary; o.Secondary = 0; }
                else if (o.Secondary == k) o.Secondary = 0;
            }
        }

        bool Changed()
        {
            var list = Bindings();
            for (int i = 0; i < list.Count && i < _backup.Count; i++)
                if (list[i].Primary != _backup[i].Primary || list[i].Secondary != _backup[i].Secondary) return true;
            return false;
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case MsgCell:
                    if (_capturing) break;
                    _capturing = true;
                    SetLanguageHash(0xB2D837B0, Label(_index / W));
                    var b = Bindings()[_index / W];
                    Printf(0x3BBE5E51, KeyName((Key)(_index % W == 0 ? b.Primary : b.Secondary)));
                    View.QueueMessage(0xD707AE8D, null, FEView.ToBroadcast);
                    View.QueueMessage(0x13FD3296, null, FEView.ToBroadcast);
                    break;
                case FEHash.PadButton4:
                case MsgClear:
                {
                    var c = Bindings()[_index / W];
                    if (_index % W == 0) { c.Primary = c.Secondary; c.Secondary = 0; } else c.Secondary = 0;
                    Populate();
                    break;
                }
                case FEHash.PadButton5:
                    DialogInterface.ShowTwoButtons(this, "", DialogTitle.Alert, 0x70E01038, 0x417B25E4, MsgDefaults, FEView.MsgPadBack, FEView.MsgPadBack, true, 1, Lang(0x8AEF5AE8));
                    break;
                case MsgDefaults:
                    FEDb.Data.KeyBindings.Clear();
                    Bindings();
                    Populate();
                    break;
                case FEHash.PadBack:
                    View.PackageMessage(LeaveScreen);
                    break;
                case FEHash.PadStart:
                    if (_fromPause) View.PackageMessage(LeaveScreen);   // EUnPause [?: the pause menu closes]
                    break;
                case FEHash.ExitComplete:
                    FEDb.OptionsDirty = FEDb.OptionsDirty || Changed();
                    if (FEDb.OptionsDirty) FEDb.Save();
                    FE.Defer(() =>
                    {
                        if (FE.Top != this) return;
                        if (_fromPause) FE.Pop(); else FE.Replace(new OptionsMainScreen(false));
                    });
                    break;
            }
        }
    }
}
