using NfsMw.Audio;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NfsMw.UI
{
    /// <summary>
    /// ControllerUnplugged.fng (GLOBAL bundle) — ControllerUnplugged (MenuScreens/ControllerUnplugged.cpp; PC create 0x570EA0,
    /// ctor 0x5493A0, NotificationMessage 0x549420). The PC build differs from the decomp: the text is label 0x54EEFEC2 (not
    /// 0x54EEF4C5) printed with port + 1 into 0xB244CF71, and START released (0xEBFCDA65) only pops the error package
    /// (PopErrorPackage(port) 0x542610 — no EPause); port −1 (boot) → BootFlowManager::JumpToHead.
    /// Who shows it: cFEngJoyInput::CheckUnplugged (0x559280, from HandleJoy) — for player 1's (and in split screen player 2's)
    /// joystick port, ActionQueue::IsConnected (0x626D70: port ≥ 0 and the device's connected flag +0x280) false →
    /// FEManager::WantControllerError(port) (0x54ECE0; skipped in an online / LAN race); FEManager::Update (0x56435D) then pushes
    /// the package with cFEng::PushErrorPackage (0x542530: error priority, RequestPauseSimulation, PauseAllSystems = NIS pause +
    /// SoundPause + SNDSTATE_ERROR) unless a NIS / movie holds it or it is already up. A keyboard never disconnects, so on PC
    /// this is the DirectInput pad case only.
    /// Port: <see cref="Monitor"/> watches the Input System for a gamepad / joystick that was in use being disconnected
    /// [?: the exe's per-player joystick port assignment is the input owner's; any used pad counts as player 1's, port 0].
    /// </summary>
    public sealed class ControllerUnpluggedScreen : FEMenuScreen
    {
        public override string Package => "ControllerUnplugged";
        public override bool Overlay => true;
        const uint MsgStartReleased = 0xEBFCDA65, Label = 0x54EEFEC2, TextObj = 0xB244CF71;
        const string PauseWho = "ControllerUnplugged.fng";
        readonly int _port;
        static bool _wasPaused;

        public ControllerUnpluggedScreen(int port) { _port = port; }

        protected override void Setup()
        {
            Printf(TextObj, CareerScreenKit.Format(Lang(Label), _port + 1));
            // cFEng::PushErrorPackage → RequestPauseSimulation + PauseAllSystems
            _wasPaused = true;
            GameFlow.RequestPauseSimulation(PauseWho);
            SoundState.Set(true, SndCtl.Error);
        }

        protected override void OnClose()
        {
            if (!_wasPaused) return;
            _wasPaused = false;
            SoundState.Set(false, SndCtl.Error);
            GameFlow.RequestUnPauseSimulation(PauseWho);
        }

        protected override void Feed(MenuInput i)
        {
            base.Feed(i);
            // START released on the pad (or the keyboard's Enter / Esc, the PC "start") [?: the exe takes it from the port]
            var g = Gamepad.current;
            if ((g != null && g.startButton.wasReleasedThisFrame) || i.Accept || i.Back) QueueGameMessage(MsgStartReleased);
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg != MsgStartReleased) return;
            // port −1 = the boot flow's (JumpToHead); the port has no boot-time controller check
            QueuePackagePop();
            Monitor.Clear(_port);
        }

        /// <summary>FEManager's mWantControllerError + the Update push, over the Input System's device changes.</summary>
        public static class Monitor
        {
            static bool _want, _hooked;

            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
            static void Hook()
            {
                if (_hooked) return;
                _hooked = true;
                InputSystem.onDeviceChange += OnDeviceChange;
            }

            static void OnDeviceChange(InputDevice d, InputDeviceChange c)
            {
                if (!(d is Gamepad || d is Joystick)) return;
                if (c == InputDeviceChange.Disconnected || c == InputDeviceChange.Removed)
                {
                    if (d.lastUpdateTime <= 0) return;   // never used: not a player's controller
                    // WantControllerError: in an online / LAN race the flag is not set
                    if (GameSession.Lan != null && GameFlow.InGameplay) return;
                    _want = true;
                    Push();
                }
            }

            /// <summary>FEManager::ClearControllerError (0x50EA10).</summary>
            public static void Clear(int port) => _want = false;

            /// <summary>FEManager::Update (0x56435D): push while wanted, not already up, no NIS / movie playing.</summary>
            static void Push()
            {
                if (!_want || NisPlayer.Active) return;
                var fe = FrontEnd.Create();
                foreach (var s in fe.Stack) if (s is ControllerUnpluggedScreen) return;
                fe.Push(new ControllerUnpluggedScreen(0));
            }
        }
    }
}
