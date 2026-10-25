using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace NfsMw.UI
{
    /// <summary>Menu navigation from keyboard and gamepad, with key repeat on held directions.</summary>
    public struct MenuInput
    {
        public bool Up, Down, Left, Right, Accept, Back, Key1, Key3, Delete;
        /// <summary>PC keys 2 / 4 and the shoulder buttons (FE PAD_LTRIGGER / PAD_RTRIGGER; keyboard PageUp / PageDown [?]).</summary>
        public bool Key2, Key4, LTrigger, RTrigger;
        public bool Any => Up || Down || Left || Right || Accept || Back || Key1 || Key3 || Delete || Key2 || Key4 || LTrigger || RTrigger;

        static float _held, _next;
        static int _dir;

        public static MenuInput Read()
        {
            var i = new MenuInput();
            var k = Keyboard.current;
            var g = Gamepad.current;
            int dir = 0;
            if (k != null)
            {
                if (k.upArrowKey.isPressed || k.wKey.isPressed) dir = 1;
                else if (k.downArrowKey.isPressed || k.sKey.isPressed) dir = 2;
                else if (k.leftArrowKey.isPressed || k.aKey.isPressed) dir = 3;
                else if (k.rightArrowKey.isPressed || k.dKey.isPressed) dir = 4;
                i.Accept = k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame || k.spaceKey.wasPressedThisFrame;
                i.Back = k.escapeKey.wasPressedThisFrame || k.backspaceKey.wasPressedThisFrame;
                i.Key1 = k.digit1Key.wasPressedThisFrame;
                i.Key3 = k.digit3Key.wasPressedThisFrame;
                i.Delete = k.deleteKey.wasPressedThisFrame;
                i.Key2 = k.digit2Key.wasPressedThisFrame;
                i.Key4 = k.digit4Key.wasPressedThisFrame;
                i.LTrigger = k.pageUpKey.wasPressedThisFrame;
                i.RTrigger = k.pageDownKey.wasPressedThisFrame;
            }
            if (g != null)
            {
                var s = g.leftStick.ReadValue() + g.dpad.ReadValue();
                if (dir == 0)
                {
                    if (s.y > 0.5f) dir = 1; else if (s.y < -0.5f) dir = 2; else if (s.x < -0.5f) dir = 3; else if (s.x > 0.5f) dir = 4;
                }
                i.Accept |= g.buttonSouth.wasPressedThisFrame || g.startButton.wasPressedThisFrame;
                i.Back |= g.buttonEast.wasPressedThisFrame;
                i.Key1 |= g.buttonWest.wasPressedThisFrame;
                i.Key3 |= g.buttonNorth.wasPressedThisFrame;
                i.LTrigger |= g.leftShoulder.wasPressedThisFrame;
                i.RTrigger |= g.rightShoulder.wasPressedThisFrame;
            }
            float now = Time.unscaledTime;
            bool fire = false;
            // FEngine::ProcessPadsForPackage (FEngine.cpp:603): a held direction repeats after FEFramesToTicks(20) = 320 ms,
            // then (FastRep) every 0x78 = 120 ms
            if (dir != _dir) { _dir = dir; _held = now; _next = now + 0.32f; fire = dir != 0; }
            else if (dir != 0 && now >= _next) { _next = now + 0.12f; fire = true; }
            if (fire)
            {
                i.Up = dir == 1; i.Down = dir == 2; i.Left = dir == 3; i.Right = dir == 4;
            }
            return i;
        }
    }

    /// <summary>One menu: an FEng package plus the code that fills it and reacts to input.</summary>
    public abstract class FEScreen
    {
        public FrontEnd FE;
        public FEView View;
        public abstract string Package { get; }
        public virtual bool ShowCar => false;
        /// <summary>The showroom set behind the car (FRONTEND\PLATFORMS file).</summary>
        public virtual string Platform => "CAR_LOT.BIN";
        /// <summary>The screen's FE_ATTRIB camera collection: mode prefix + package name (FEPkg_GarageMain).</summary>
        public virtual string CameraKey => CameraAngle switch
        {
            "career_safehouse" => "career_",
            "customization_shop" or "backroom" => "customize_",
            _ => "carlot_",
        } + Package.ToLowerInvariant();

        /// <summary>FE_ATTRIB camera angle pair (angle_entry_X → angle_final_X) for the showroom (0x7A1E70).</summary>
        public virtual string CameraAngle => Platform switch
        {
            "CAREER_SAFEHOUSE.BIN" => "career_safehouse",
            "CUSTOMIZATION_SHOP.BIN" => "customization_shop",
            "CUSTOMIZATION_SHOP_BACKROOM.BIN" => "backroom",
            _ => "car_lot",
        };
        /// <summary>Run the package on the FEng message engine (FEView engine mode, InGame/FEngEngine.cs).</summary>
        public virtual bool ExeEngine => false;
        /// <summary>Drawn over the screen below without hiding it (dialogs: cFEng pushes them on top of the parent package).</summary>
        public virtual bool Overlay => false;
        public virtual void Open() { }
        public virtual void Close() { }
        public virtual void Tick(MenuInput input) { }
        public virtual void Resume() { }

        protected void SetText(string name, string text) => View.SetText(name, text);

        /// <summary>Sets every string object whose authored text is original (labels often have a shadow copy).</summary>
        protected void SetAllText(string original, string text)
        {
            foreach (var n in View.Nodes) if (n.Label != null && n.Obj.Text == original) View.SetText(n, text);
        }

        protected FEView.Node FindText(string original)
        {
            foreach (var n in View.Nodes) if (n.Label != null && n.Obj.Text == original) return n;
            return null;
        }

        protected static string Money(int v) => "$" + v.Grouped();

        /// <summary>Hides package objects (by name hash) that this screen doesn't use.</summary>
        protected void Hide(params uint[] hashes)
        {
            foreach (var h in hashes) View.SetVisible(View.Find(h), false);
        }

        /// <summary>
        /// The shop packages (Paint, CustomizeParts, CustomizePerformance) share a price box and template parts the
        /// game fills or hides from code: heat meter, lock banner, trade-in row, DEFAULT STRING debug lines, the
        /// cart and showcase buttons. Shows cost / funds / total and labels the Enter button.
        /// </summary>
        protected void ShopFrame(int cost, string accept)
        {
            Hide(0xF2492598, 0xEBC3E6B7, 0xB6B79052,                  // heat meter, lock banner, trade-in row
                 0x8C4C1D77, 0x8C4C1D35, 0x8C4C1CF3, 0x5B9D88B9,      // DEFAULT STRING
                 0x18A4384F, 0xD6463100,                              // Showcase, View Cart
                 0x21DCC127, 0x2D4D22C8, 0xCFFB7033, 0xD0582FEB,      // black panel, n/20 counter, check / new marks
                 0x03C97E7F, 0x03C97EA0);                             // their frames
            var p = Game.GameSession.Profile;
            bool career = Game.GameSession.Mode == Game.GameMode.Career && p != null;
            Hide(career ? 0u : 0x8D1559A4);                            // no prices outside the career
            SetGroupText(0xDBB80EDD, career ? cost.Grouped() : "");
            SetGroupText(0xC60ADCFD, career ? p.Cash.Grouped() : "");
            SetGroupText(0x7A6D2F71, career ? (p.Cash - cost).Grouped() : "");
            SetGroupText(0x73DCB662, accept);
        }

        /// <summary>Sets every string under a group (text plus its drop shadow).</summary>
        protected void SetGroupText(uint group, string text)
        {
            var g = View.Find(group);
            if (g == null) return;
            if (g.Label != null) View.SetText(g, text);
            foreach (var d in View.Descendants(g)) View.SetText(d, text);
        }
    }

    /// <summary>Screen stack on a 640x480-scaled canvas, with the car showroom behind car-related screens.</summary>
    public sealed class FrontEnd : MonoBehaviour
    {
        public static FrontEnd Instance { get; private set; }
        public Canvas Canvas { get; private set; }
        public CarShowroom Showroom { get; private set; }
        readonly List<FEScreen> _stack = new List<FEScreen>();
        public bool IsOpen => _stack.Count > 0;
        public FEScreen Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
        /// <summary>The open screens, bottom first.</summary>
        public IReadOnlyList<FEScreen> Stack => _stack;
        public event Action Closed;

        public static FrontEnd Create(Camera captureCamera = null)
        {
            if (Instance) return Instance;
            FrontEndData.Load();
            var go = new GameObject("Front End");
            if (Application.isPlaying) DontDestroyOnLoad(go);
            Instance = go.AddComponent<FrontEnd>();
            Instance.Canvas = FrontEndCanvas.Create("FE Canvas", captureCamera);
            Instance.Canvas.transform.SetParent(go.transform, false);
            Instance.Showroom = new GameObject("Showroom").AddComponent<CarShowroom>();
            Instance.Showroom.transform.SetParent(go.transform, false);
            return Instance;
        }

        public T Push<T>(T screen) where T : FEScreen
        {
            if (Top != null && Top.View && !screen.Overlay) Show(Top.View, false);
            screen.FE = this;
            screen.View = FEView.Open(screen.Package, Canvas.transform, screen.ShowCar && Showroom ? Showroom.BackCanvas : null, true, screen.ExeEngine);
            _stack.Add(screen);
            screen.Open();
            UpdateShowroom();
            return screen;
        }

        public void Pop()
        {
            if (_stack.Count == 0) return;
            var s = Top;
            s.Close();
            Kill(s.View.gameObject);
            _stack.RemoveAt(_stack.Count - 1);
            if (Top != null)
            {
                Show(Top.View, true);
                Top.Resume();
            }
            UpdateShowroom();
            if (_stack.Count == 0) Closed?.Invoke();
            RunDeferred();
        }

        readonly List<Action> _deferred = new List<Action>();

        /// <summary>
        /// Runs an action once the current stack change has finished (end of Pop, or the next frame): dialog replies
        /// (~feDialogScreen's QueueGameMessage) must not push or pop while the dialog is being removed.
        /// </summary>
        public void Defer(Action a)
        {
            if (a != null) _deferred.Add(a);
        }

        void RunDeferred()
        {
            if (_running || _deferred.Count == 0) return;
            _running = true;
            try
            {
                while (_deferred.Count > 0)
                {
                    var a = _deferred[0];
                    _deferred.RemoveAt(0);
                    a();
                }
            }
            finally { _running = false; }
        }

        bool _running;

        static void Show(FEView v, bool on)
        {
            v.gameObject.SetActive(on);
            foreach (Transform t in v.transform.root) { }
            if (Instance && Instance.Showroom && Instance.Showroom.BackCanvas)
                foreach (Transform t in Instance.Showroom.BackCanvas)
                    if (t.name == v.name + " (back)") t.gameObject.SetActive(on);
        }

        static void Kill(Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        /// <summary>Removes the front end entirely (tests, scene teardown).</summary>
        public static void Shutdown()
        {
            if (!Instance) return;
            Instance.CloseAll();
            Kill(Instance.gameObject);
            Instance = null;
        }

        public void CloseAll()
        {
            while (_stack.Count > 0) Pop();
        }

        public T Replace<T>(T screen) where T : FEScreen
        {
            if (_stack.Count > 0)
            {
                var s = Top;
                s.Close();
                Kill(s.View.gameObject);
                _stack.RemoveAt(_stack.Count - 1);
            }
            return Push(screen);
        }

        public void UpdateShowroom()
        {
            if (Showroom)
            {
                Showroom.SetActive(Top != null && Top.ShowCar);
                if (Top != null && Top.ShowCar) { Showroom.UseSet(Top.Platform); Showroom.SetAngle(Top.CameraAngle, Top.CameraKey); }
            }
            UpdateBackdrop();
        }

        Camera _backdrop;

        /// <summary>
        /// With no city loaded (main menu, options, LAN lobby, loading) the menus sit on a plain dark backdrop instead
        /// of the empty scene's sky; in game the city stays visible behind the pause and safehouse menus.
        /// </summary>
        void UpdateBackdrop()
        {
            if (!Application.isPlaying) return;
            bool world = NfsMw.GameBootstrap.Instance != null && NfsMw.GameBootstrap.Instance.Player != null;
            bool on = IsOpen && (!world || Top is NfsMw.Game.LoadingScreen);
            if (!_backdrop)
            {
                if (!on) return;
                var go = new GameObject("Backdrop Camera");
                go.transform.SetParent(transform, false);
                _backdrop = go.AddComponent<Camera>();
                _backdrop.clearFlags = CameraClearFlags.SolidColor;
                _backdrop.backgroundColor = new Color(0.015f, 0.02f, 0.022f);
                _backdrop.cullingMask = 0;
                _backdrop.depth = 40;   // over the world camera, under the showroom (50)
            }
            _backdrop.enabled = on;
        }

        /// <summary>Feeds input to the top screen (Update does this every frame; tools call it directly).</summary>
        public void Step(MenuInput input)
        {
            Top?.Tick(input);
        }

        AudioSource _ui;

        void Click(MenuInput i)
        {
            if (!i.Any || !Application.isPlaying) return;
            if (_ui == null)
            {
                _ui = gameObject.AddComponent<AudioSource>();
                _ui.spatialBlend = 0;
                _ui.ignoreListenerPause = true;
            }
            var clip = NfsMw.Game.SoundLibrary.Clip("FE/FE_MB.abk", i.Accept ? 1 : i.Back ? 2 : 0);
            if (clip) _ui.PlayOneShot(clip, 0.5f);
        }

        void Update()
        {
            RunDeferred();
            var top = Top;
            UpdateBackdrop();
            if (top == null) return;
            var input = MenuInput.Read();
            // [round 9 FE hook] FEng-engine screens get their sounds from the packages' sound messages (MenuScreen::BaseNotifySound)
            if (input.Any && Application.isPlaying && !top.ExeEngine) NfsMw.Audio.FeSound.Input(input.Up, input.Down, input.Left, input.Right, input.Accept, input.Back);
            top.Tick(input);
        }
    }
}
