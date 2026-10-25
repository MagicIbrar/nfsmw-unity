using System;
using System.Collections.Generic;
using NfsMw.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NfsMw.Controls
{
    /// <summary>ActionData (Input/ActionData.h): {action id, value, device slot}.</summary>
    public struct ActionData
    {
        public int Id;
        public float Data;
        public int Slot;
        public ActionData(int id, float data, int slot) { Id = id; Data = data; Slot = slot; }
        public override string ToString() => $"{Id}={Data:0.##}@{Slot}";
    }

    /// <summary>
    /// InputMapEntry (InputDefParser.h; what ActionQueue::FetchCurrentValues 0x642C90 and IO_UpdateFromDevice 0x6448E0 read):
    /// one controller-config line — a device scalar mapped to an action. Type (+0x8): 0 analog (Ramp(value, Min, 1 − Max),
    /// an action with the value on every change), 1 / 2 digital press / release (value &gt; 0.5), 3 / 4 threshold press /
    /// release (value &gt; Min). PreviousValue / CurrentValue (+0x1C / +0x20) start at −1 (unknown; Flush resets them).
    /// </summary>
    public sealed class InputMapEntry
    {
        public const int Analog = 0, DigitalPress = 1, DigitalRelease = 2, ThresholdPress = 3, ThresholdRelease = 4;
        public int Type;
        public float Min, Max;          // +0xC, +0x10
        public int ActionId;            // +0x14
        public int ScalarIndex = -1;    // +0x18 (DeviceScalarIndex)
        public float PreviousValue = -1f, CurrentValue = -1f;
        public string Name;
        public override string ToString() => $"{Name ?? ActionId.ToString()} type {Type} scalar {ScalarIndex}";
    }

    /// <summary>InputDevice as the queues see it (IOModule::GetDevice(port)): a list of scalars (buttons 0/1, axes −1..1 / 0..1).</summary>
    public interface IInputDevice
    {
        int Port { get; }
        bool Connected { get; }
        int NumScalars { get; }
        float Scalar(int index);
        /// <summary>The scalar for a config's key name (the controller attribute's scalar names), −1 when unknown.</summary>
        int ScalarIndexOf(string scalarName);
        void Poll();
    }

    /// <summary>InputMapping (ctor 0x6479D0): the entries of one controller config bound to one device.</summary>
    public sealed class InputMapping
    {
        public readonly IInputDevice Device;
        public readonly List<InputMapEntry> Entries = new List<InputMapEntry>();
        public InputMapping(IInputDevice device) { Device = device; }
    }

    /// <summary>
    /// ActionQueue (Input/Common/actionqueue.cpp, PC 0x643FC0 / 0x648DB0 ctors, 0x6440B0 ReceiveAction, 0x6448E0
    /// IO_UpdateFromDevice, 0x642C90 FetchCurrentValues, 0x642D70 Flush, 0x644060 Enable): a 50-entry circular queue of
    /// ActionData fed from a controller config's mappings on one port. Up to 20 queues (MAX_ACTIONQ), each with a unique id.
    /// </summary>
    public sealed class ActionQueue
    {
        public const int MaxActionQ = 20, QueueSize = 50;
        /// <summary>ACTION_PLUGGED / ACTION_UNPLUGGED / ACTION_FLUSH: ids 135 / 136 / 137 of the exe's ActionID table (Core/InputActions, 0x8F6728).</summary>
        public const int ActionPlugged = NfsMw.Core.InputActions.Plugged, ActionUnplugged = NfsMw.Core.InputActions.Unplugged, ActionFlush = NfsMw.Core.InputActions.Flush;
        static readonly List<ActionQueue> List = new List<ActionQueue>();
        static float _lastAnyActionTime;
        public static float LastAnyActionTime => _lastAnyActionTime;
        public static IReadOnlyList<ActionQueue> All => List;

        readonly Queue<ActionData> _queue = new Queue<ActionData>();
        int _port = -1;
        bool _enabled = true, _connected;
        InputMapping _mappings;
        string _config;
        float _actionTime, _activationTime = -1f;
        public readonly int UniqueId;
        public string Name { get; private set; } = "Unnamed";
        public bool Required { get; set; }

        /// <summary>Who builds the mappings for (device, config) (IOModule's InputDefParser over the `controller` attribute) [ours: injected].</summary>
        public static Func<IInputDevice, string, InputMapping> MappingFactory;
        public static Func<int, IInputDevice> DeviceOfPort;

        public ActionQueue(bool required)
        {
            Required = required;
            UniqueId = AssignUniqueID();
            List.Add(this);
        }

        public ActionQueue(int port, string config, string queueName, bool required) : this(required)
        {
            _port = port;
            SetConfig(config, queueName);
        }

        public void Dispose() { List.Remove(this); _mappings = null; }

        public int Port => _port;
        public string Config => _config;
        public bool IsValid => _mappings != null;
        public bool IsActive => IsValid;
        public bool IsEnabled => _enabled;
        public bool IsConnected => _port >= 0 && _connected;
        public bool IsEmpty => _queue.Count == 0;
        public int Size => _queue.Count;
        public float LastActionTime => _actionTime;

        static ActionQueue FindActionQueue(int id) => List.Find(q => q.UniqueId == id);
        static int AssignUniqueID() { int id = 0; while (FindActionQueue(id) != null) id++; return id; }

        /// <summary>Init(port, config): new mappings for the device on the port (nothing when neither changed).</summary>
        void Init(int port, string config)
        {
            if (port == _port && config == _config && _mappings != null) return;
            _config = config;
            _port = port;
            _mappings = null;
            if (port >= 0)
            {
                var device = DeviceOfPort?.Invoke(port);
                if (device != null && config != null) _mappings = MappingFactory?.Invoke(device, config);
            }
            OnActivationChange();
        }

        public void SetPort(int port) => Init(port, _config);
        public void SetConfig(string config, string queueName) { Name = queueName; Init(_port, config); }

        /// <summary>Enable(b): a state change flushes the queue.</summary>
        public void Enable(bool b)
        {
            if (b) { if (!_enabled) { _enabled = true; Flush(); } }
            else if (_enabled) { _enabled = false; Flush(); }
            OnActivationChange();
        }

        void OnActivationChange()
        {
            bool active = _enabled && IsConnected && IsActive;
            if (active != (_activationTime >= 0)) _activationTime = active ? Time.realtimeSinceStartup : -1f;
        }

        /// <summary>IO_SetConnected: ACTION_PLUGGED / ACTION_UNPLUGGED through the queue.</summary>
        public void IO_SetConnected(bool plugged)
        {
            if (_port < 0 || _connected == plugged) return;
            var a = new ActionData(plugged ? ActionPlugged : ActionUnplugged, 0f, _port);
            ReceiveAction(ref a);
        }

        public void IO_Flush()
        {
            if (_port < 0) return;
            var a = new ActionData(ActionFlush, 0f, _port);
            ReceiveAction(ref a);
        }

        /// <summary>FetchCurrentValues (0x642C90): every entry's CurrentValue from its device scalar (−1 for a missing scalar).</summary>
        void FetchCurrentValues(IInputDevice device)
        {
            foreach (var e in _mappings.Entries)
            {
                if (e.ScalarIndex < 0 || e.ScalarIndex >= device.NumScalars) { e.PreviousValue = e.CurrentValue = -1f; continue; }
                float v = device.Scalar(e.ScalarIndex);
                switch (e.Type)
                {
                    case InputMapEntry.Analog: e.CurrentValue = Ramp(v, e.Min, 1f - e.Max); break;
                    case InputMapEntry.DigitalPress:
                    case InputMapEntry.DigitalRelease: e.CurrentValue = v < 0.5f ? 0f : 1f; break;
                    case InputMapEntry.ThresholdPress:
                    case InputMapEntry.ThresholdRelease: e.CurrentValue = v < e.Min ? 0f : 1f; break;
                }
            }
        }

        /// <summary>UMath::Ramp (0x401AE0).</summary>
        static float Ramp(float x, float a, float b) => b - a <= 1e-6f ? 0f : Mathf.Clamp01((x - a) / (b - a));

        /// <summary>
        /// IO_UpdateFromDevice (0x6448E0): FetchCurrentValues, then for every entry whose value is known and changed by more
        /// than 1e-6: an analog entry yields to another analog entry of the same action with a value at least as large (two
        /// axes on one action: the larger wins); type 0 fires on every change, 1 / 3 on the press edge (Current ≥ 0.5,
        /// Previous &lt; 0.5 and known), 2 / 4 on the release edge (Current &lt; 0.5, Previous ≥ 0.5). An action id of 0 is
        /// not queued. PreviousValue = CurrentValue afterwards.
        /// </summary>
        public void IO_UpdateFromDevice()
        {
            if (_mappings == null || !_enabled || _port < 0) return;
            var device = DeviceOfPort?.Invoke(_port) ?? _mappings.Device;
            if (device == null) return;
            FetchCurrentValues(device);
            var entries = _mappings.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                float cur = e.CurrentValue, prev = e.PreviousValue;
                if (cur < 0f) continue;                       // unknown (no scalar)
                if (Mathf.Abs(cur - prev) <= 1e-6f) continue;  // no change
                bool fire = true;
                if (e.Type == InputMapEntry.Analog)
                {
                    foreach (var o in entries)
                        if (o != e && o.ActionId == e.ActionId && o.Type == InputMapEntry.Analog && o.CurrentValue >= cur) { fire = false; break; }
                }
                if (fire)
                {
                    switch (e.Type)
                    {
                        case InputMapEntry.DigitalPress:
                        case InputMapEntry.ThresholdPress: fire = cur >= 0.5f && prev < 0.5f && prev >= 0f; break;
                        case InputMapEntry.DigitalRelease:
                        case InputMapEntry.ThresholdRelease: fire = cur < 0.5f && prev >= 0.5f; break;
                    }
                }
                if (fire && e.ActionId != 0)
                {
                    var a = new ActionData(e.ActionId, cur, device.Port);
                    ReceiveAction(ref a);
                }
                e.PreviousValue = cur;
            }
        }

        /// <summary>ReceiveAction (0x6440B0): only for this port; plugged / unplugged / flush act first; then queued (the oldest dropped when full [?]).</summary>
        public bool ReceiveAction(ref ActionData action)
        {
            if (action.Slot != _port) return false;
            if (action.Id != ActionUnplugged) _lastAnyActionTime = Time.realtimeSinceStartup;
            if (action.Id == ActionUnplugged) { Flush(); _connected = false; OnActivationChange(); }
            else if (action.Id == ActionPlugged) { Flush(); _connected = true; OnActivationChange(); }
            else if (action.Id == ActionFlush) Flush();
            _actionTime = Time.realtimeSinceStartup;
            if (_queue.Count >= QueueSize) _queue.Dequeue();
            _queue.Enqueue(action);
            return true;
        }

        public void PopAction() { if (_queue.Count > 0) _queue.Dequeue(); }

        /// <summary>Flush (0x642D70): the queue emptied, every mapping's previous / current value back to −1.</summary>
        public void Flush()
        {
            _queue.Clear();
            if (_mappings != null) foreach (var e in _mappings.Entries) e.PreviousValue = e.CurrentValue = -1f;
        }

        /// <summary>GetAction (0x633DC0): the oldest queued action (null when empty).</summary>
        public ActionData? GetAction() => _queue.Count > 0 ? _queue.Peek() : (ActionData?)null;

        public IEnumerable<ActionData> Pending => _queue;
    }

    /// <summary>
    /// The PC action ids of the `fe` controller config as EasterEggs.hpp names them (EasterEggButtons): L1 43, L2 40, R1 44,
    /// R2 38, LT 27, RT 28, SQ 41, TR 34, UP 25, DN 26 — the front-end queue's action numbering [?: the other ids].
    /// </summary>
    public static class FeAction
    {
        public const int Up = 25, Down = 26, Left = 27, Right = 28, Triangle = 34, R2 = 38, L2 = 40, Square = 41, L1 = 43, R1 = 44;
    }

    /// <summary>
    /// The port's input: the keyboard (port 0) and the gamepads as one scalar list per port [ours: the exe's InputDevice is
    /// a DirectInput device with its own scalar table]. Scalar names are the Key enum names (keyboard) and the gamepad
    /// control names; the `controller` attribute's bindings are the FE controls page's (ControllerScreen.KeyOf).
    /// </summary>
    public sealed class UnityInputDevice : IInputDevice
    {
        readonly List<Func<float>> _readers = new List<Func<float>>();
        readonly Dictionary<string, int> _names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly float[] _values;
        public int Port { get; }
        public bool Connected { get; private set; }
        public int NumScalars => _readers.Count;

        public UnityInputDevice(int port)
        {
            Port = port;
            // keyboard keys (names = Key enum names)
            foreach (Key k in Enum.GetValues(typeof(Key)))
            {
                if (k == Key.None || k == Key.IMESelected) continue;
                var key = k;
                _names[k.ToString()] = _readers.Count;
                _readers.Add(() => { var kb = Keyboard.current; if (kb == null) return 0f; var c = kb[key]; return c != null && c.isPressed ? 1f : 0f; });
            }
            Gamepad Pad() { var pads = Gamepad.all; return pads.Count == 0 ? null : Port < pads.Count ? pads[Port] : null; }
            void Add(string name, Func<Gamepad, float> f) { _names[name] = _readers.Count; _readers.Add(() => { var g = Pad(); return g != null ? f(g) : 0f; }); }
            Add("PAD_UP", g => g.dpad.up.isPressed || g.leftStick.ReadValue().y > 0.5f ? 1f : 0f);
            Add("PAD_DOWN", g => g.dpad.down.isPressed || g.leftStick.ReadValue().y < -0.5f ? 1f : 0f);
            Add("PAD_LEFT", g => g.dpad.left.isPressed || g.leftStick.ReadValue().x < -0.5f ? 1f : 0f);
            Add("PAD_RIGHT", g => g.dpad.right.isPressed || g.leftStick.ReadValue().x > 0.5f ? 1f : 0f);
            Add("PAD_BUTTON1", g => g.buttonSouth.isPressed ? 1f : 0f);    // accept
            Add("PAD_BUTTON2", g => g.buttonEast.isPressed ? 1f : 0f);     // back
            Add("PAD_BUTTON3", g => g.buttonWest.isPressed ? 1f : 0f);
            Add("PAD_BUTTON4", g => g.buttonNorth.isPressed ? 1f : 0f);
            Add("PAD_L1", g => g.leftShoulder.isPressed ? 1f : 0f);
            Add("PAD_R1", g => g.rightShoulder.isPressed ? 1f : 0f);
            Add("PAD_L2", g => g.leftTrigger.ReadValue());
            Add("PAD_R2", g => g.rightTrigger.ReadValue());
            Add("PAD_START", g => g.startButton.isPressed ? 1f : 0f);
            Add("PAD_SELECT", g => g.selectButton.isPressed ? 1f : 0f);
            Add("PAD_STICK_X", g => g.leftStick.ReadValue().x);
            Add("PAD_STICK_Y", g => g.leftStick.ReadValue().y);
            // the PC device's logical scalars that the `controller` configs name (InputMapping matches DeviceScalarName to the
            // record's mDeviceID hash32): the FE controls page's actions (keyboard: their bound keys, read live; the pad: the
            // defaults of PadDefault), FE_ACTION_* (the menu keys), SC_* (DirectInput scan codes)
            foreach (var act in ControllerScreen.Actions)
            {
                string an = act.name;
                Logical(an, () => KeyDown(ControllerScreen.KeyOf(an)) || KeyDown(ControllerScreen.KeyOf(an, true)), PadDefault(an));
            }
            Logical("FE_ACTION_UP", () => KeyDown(Key.UpArrow), g => g.dpad.up.isPressed || g.leftStick.ReadValue().y > 0.5f);
            Logical("FE_ACTION_DOWN", () => KeyDown(Key.DownArrow), g => g.dpad.down.isPressed || g.leftStick.ReadValue().y < -0.5f);
            Logical("FE_ACTION_LEFT", () => KeyDown(Key.LeftArrow), g => g.dpad.left.isPressed || g.leftStick.ReadValue().x < -0.5f);
            Logical("FE_ACTION_RIGHT", () => KeyDown(Key.RightArrow), g => g.dpad.right.isPressed || g.leftStick.ReadValue().x > 0.5f);
            Logical("FE_ACTION_ACCEPT", () => KeyDown(Key.Enter) || KeyDown(Key.NumpadEnter), g => g.buttonSouth.isPressed);
            Logical("FE_ACTION_CANCEL", () => KeyDown(Key.Escape), g => g.buttonNorth.isPressed);   // EasterEggs' TR = 34 (FRONTENDACTION_CANCEL)
            Logical("FE_ACTION_B0", null, null);
            Logical("FE_ACTION_B1", null, g => g.rightTrigger.ReadValue() > 0.5f);                // R2 = 38 (FRONTENDACTION_BUTTON1)
            Logical("FE_ACTION_B3", null, null);
            Logical("FE_ACTION_B4", null, g => g.buttonWest.isPressed);                           // SQ = 41 (FRONTENDACTION_BUTTON4)
            Logical("FE_ACTION_B5", null, null);
            Logical("FE_ACTION_START", null, g => g.leftTrigger.ReadValue() > 0.5f);              // L2 = 40 (FRONTENDACTION_BUTTON3)
            Logical("FE_ACTION_LTRIG", null, g => g.leftShoulder.isPressed);                      // L1 = 43
            Logical("FE_ACTION_RTRIG", null, g => g.rightShoulder.isPressed);                     // R1 = 44
            foreach (var (sc, key) in ScanCodes) Logical(sc, () => KeyDown(key), null);
            _values = new float[_readers.Count];
        }

        /// <summary>DirectInput DIK_* names the shipped configs use (SC_LEFT ...) to keys [the debug / ice / fly configs, plus drive's SC_LEFT / SC_RIGHT / SC_UP / SC_DOWN].</summary>
        static readonly (string, Key)[] ScanCodes =
        {
            ("SC_LEFT", Key.LeftArrow), ("SC_RIGHT", Key.RightArrow), ("SC_UP", Key.UpArrow), ("SC_DOWN", Key.DownArrow),
            ("SC_C", Key.C), ("SC_V", Key.V), ("SC_X", Key.X), ("SC_Z", Key.Z), ("SC_SPACE", Key.Space), ("SC_ESCAPE", Key.Escape),
            ("SC_BACK", Key.Backspace), ("SC_INSERT", Key.Insert), ("SC_F1", Key.F1), ("SC_LALT", Key.LeftAlt), ("SC_RALT", Key.RightAlt),
            ("SC_MINUS", Key.Minus), ("SC_EQUALS", Key.Equals), ("SC_LBRACKET", Key.LeftBracket), ("SC_RBRACKET", Key.RightBracket),
            ("SC_BACKSLASH", Key.Backslash), ("SC_PAGEUP", Key.PageUp), ("SC_PAGEDOWN", Key.PageDown), ("SC_ADD", Key.NumpadPlus),
            ("SC_SUBTRACT", Key.NumpadMinus), ("SC_DECIMAL", Key.NumpadPeriod), ("SC_NUMPADENTER", Key.NumpadEnter),
            ("SC_NUMPAD0", Key.Numpad0), ("SC_NUMPAD1", Key.Numpad1), ("SC_NUMPAD2", Key.Numpad2), ("SC_NUMPAD3", Key.Numpad3),
            ("SC_NUMPAD4", Key.Numpad4), ("SC_NUMPAD6", Key.Numpad6), ("SC_NUMPAD7", Key.Numpad7), ("SC_NUMPAD8", Key.Numpad8),
            ("SC_NUMPAD9", Key.Numpad9),
        };

        static bool KeyDown(Key k)
        {
            if (k == Key.None) return false;
            var kb = Keyboard.current;
            if (kb == null) return false;
            try { var c = kb[k]; return c != null && c.isPressed; } catch (Exception) { return false; }
        }

        /// <summary>The pad's default for an FE-controls action [ours: the port's pad layout; the exe's joystick page is per device].</summary>
        static Func<Gamepad, bool> PadDefault(string action)
        {
            switch (action)
            {
                case "HUDACTION_PAUSEREQUEST": return g => g.startButton.isPressed;
                case "HUDACTION_ENGAGE_EVENT": return g => g.buttonSouth.isPressed;
                default: return null;
            }
        }

        /// <summary>A logical scalar: 1 while its key (port 0 only: the keyboard) or its pad control is down.</summary>
        void Logical(string name, Func<bool> keyboard, Func<Gamepad, bool> pad)
        {
            if (_names.ContainsKey(name)) return;
            int port = Port;
            Gamepad Pad() { var pads = Gamepad.all; return pads.Count == 0 ? null : port < pads.Count ? pads[port] : null; }
            _names[name] = _readers.Count;
            _readers.Add(() =>
            {
                if (port == 0 && keyboard != null && keyboard()) return 1f;
                var g = pad != null ? Pad() : null;
                return g != null && pad(g) ? 1f : 0f;
            });
        }

        public void Poll()
        {
            Connected = Port == 0 ? Keyboard.current != null || Gamepad.all.Count > 0 : Port < Gamepad.all.Count;
            for (int i = 0; i < _readers.Count; i++) { try { _values[i] = _readers[i](); } catch (Exception) { _values[i] = 0f; } }
        }

        public float Scalar(int index) => index >= 0 && index < _values.Length ? _values[index] : 0f;
        public int ScalarIndexOf(string scalarName) => scalarName != null && _names.TryGetValue(scalarName, out int i) ? i : -1;
    }

    /// <summary>
    /// IOModule (Input/Common/iomodule.cpp; PC 0x63CBF0 Initialize, 0x645EC0 Update, 0x6450C0 UpdateAllDevices, 0x644E40
    /// CheckUnplugged): polls the devices once per frame and runs every queue's IO_UpdateFromDevice; a device's connection
    /// change is pushed to its port's queues (IO_SetConnected). The controller configs' entries come from the FE controls
    /// page (ControllerScreen.Actions / KeyOf: one primary and one secondary key per action) plus the gamepad defaults, the
    /// action ids of the `fe` config being EasterEggs' button numbers. Game actions use ids 100 + index [ours].
    /// </summary>
    public static class IOModule
    {
        static readonly Dictionary<int, UnityInputDevice> Devices = new Dictionary<int, UnityInputDevice>();
        static readonly Dictionary<int, bool> WasConnected = new Dictionary<int, bool>();
        public static bool Updating = true;

        public static void Initialize()
        {
            ActionQueue.DeviceOfPort = GetDevice;
            ActionQueue.MappingFactory = BuildMapping;
        }

        public static IInputDevice GetDevice(int port)
        {
            if (port < 0) return null;
            if (!Devices.TryGetValue(port, out var d)) { d = new UnityInputDevice(port); Devices[port] = d; }
            return d;
        }

        /// <summary>IOModule::Update: PollDevices, CheckUnplugged, then UpdateAllDevices (every queue reads its device).</summary>
        public static void Update()
        {
            if (!Updating) return;
            foreach (var kv in Devices)
            {
                kv.Value.Poll();
                bool was = WasConnected.TryGetValue(kv.Key, out var w) && w;
                if (kv.Value.Connected != was)
                {
                    WasConnected[kv.Key] = kv.Value.Connected;
                    foreach (var q in ActionQueue.All) if (q.Port == kv.Key) q.IO_SetConnected(kv.Value.Connected);
                }
            }
            foreach (var q in ActionQueue.All) q.IO_UpdateFromDevice();
        }

        /// <summary>An action's id: its index in the exe's ActionID table (getActionIdString, Core/InputActions), 0 when unknown.</summary>
        public static int GameActionId(string action) => Math.Max(0, NfsMw.Core.InputActions.IdOf(action));

        /// <summary>
        /// InputMapping::InputMapping(device, controller collection) (InputDefParser.cpp, 0x6479D0): for every action id 0..137
        /// with a name (getActionIdString), the `controller` collection's attribute of that name (Attrib::StringToKey) is a list
        /// of ControllerDataRecord {StringKey mDeviceID +0 (hash32 +8, char* +0xC), mUpdateType +0x10, mLowerDZ +0x14, mUpperDZ
        /// +0x18; 32 bytes}; a record with a non-empty device id whose hash32 is one of the device's scalar names becomes an
        /// InputMapEntry {action, update type, dead zones, scalar index, previous / current -1}. The configs are `fe`, `hud`,
        /// `drive`, `drive2`..`drive5` (children of drive), the `_analog` variants, `fly`, `ice`, `debug`, ... (attributes.bin
        /// class `controller`; harness `r9gp controller`).
        /// </summary>
        static InputMapping BuildMapping(IInputDevice device, string config)
        {
            var m = new InputMapping(device);
            var db = GameData.Attributes;
            var spec = db?.Get("controller", config);
            if (spec == null) { Debug.LogWarning($"NFSMW input: no controller config '{config}'"); return m; }
            for (int id = 0; id < NfsMw.Core.InputActions.Names.Length; id++)
            {
                string name = NfsMw.Core.InputActions.Names[id];
                if (name == null || !spec.TryGet(name, out int at, out int count, out var def)) continue;
                var src = spec.Source;
                int stride = def.Size > 0 ? def.Size : 32;
                for (int k = 0; k < count; k++)
                {
                    int b = at + k * stride;
                    int ptr = db.Ptr(src.Pack, b + 0xC);
                    string scalar = ptr >= 0 ? NfsMw.Core.Chunks.CString(src.Data, ptr, 64) : null;
                    if (string.IsNullOrEmpty(scalar)) continue;
                    int idx = device.ScalarIndexOf(scalar);
                    if (idx < 0) continue;
                    m.Entries.Add(new InputMapEntry
                    {
                        ActionId = id, Type = NfsMw.Core.Chunks.I32(src.Data, b + 0x10),
                        Min = NfsMw.Core.Chunks.F32(src.Data, b + 0x14), Max = NfsMw.Core.Chunks.F32(src.Data, b + 0x18),
                        ScalarIndex = idx, PreviousValue = -1f, CurrentValue = -1f, Name = name,
                    });
                }
            }
            return m;
        }
    }
}
