using System;
using System.Collections.Generic;
using System.Linq;

namespace NfsMw.Core.Lua
{
    /// <summary>
    /// A gameplay instance as the scripts see it: the GRuntimeInstance userdata (metatable "GRuntimeInstance",
    /// LuaAttributes::BindAccessors 0x61C180). One per gameplay collection; the host maps them to its triggers, markers,
    /// characters and activities.
    /// </summary>
    public sealed class GInstance
    {
        public readonly AttribCollection Record;
        internal LuaUserData Ud;
        internal GInstance(AttribCollection c) { Record = c; }
        public string Name => Record.Name ?? Record.Key.ToString("X8");
        public uint Key => Record.Key;
        /// <summary>The part after the last '/' (child names such as "call_from_mia").</summary>
        public string LocalName { get { var n = Name; int i = n.LastIndexOf('/'); return i >= 0 ? n.Substring(i + 1) : n; } }
        public override string ToString() => Name;
    }

    /// <summary>A LuaAttribArrayInfo userdata {instance, attribute key}: 1-based elements and `.Count` (0x61BF00).</summary>
    public sealed class GAttribArray
    {
        public readonly GInstance Owner;
        public readonly string Field;
        internal GAttribArray(GInstance o, string f) { Owner = o; Field = f; }
        public override string ToString() => $"{Owner.Name}.{Field}[]";
    }

    /// <summary>
    /// What the stategraph runtime needs from the game. Bindings of kind Query/Action arrive in Call with arguments already
    /// converted by the binder signature (int, float, bool, string, GInstance, the host's own simable objects, or null).
    /// E* event constructors arrive in Event when the event executes (they are deferred like the exe's Event queue).
    /// </summary>
    public interface ILuaGameHost
    {
        object Call(LuaBinding binding, object[] args);
        void Event(string name, object[] args);
        void Log(string line);
    }

    /// <summary>
    /// A running stategraph (GActivity): its states and handlers (GatherStatesAndHandlers 0x5F9270), current state
    /// (+0x28), running flag (+0x3C) and the per-instance Lua state table (registry[CollectionName], 0x5F9CC0).
    /// </summary>
    public sealed class GActivity
    {
        public readonly GInstance Instance;
        public readonly List<ScriptState> States;
        public readonly Dictionary<ScriptState, List<ScriptHandler>> Handlers = new Dictionary<ScriptState, List<ScriptHandler>>();
        public ScriptState Current { get; internal set; }
        public bool Running { get; internal set; }
        /// <summary>+0x3D: the Lua state table exists (set by the getter 0x5F9CC0 / LoadState, cleared by Reset and a
        /// releasing SaveState).</summary>
        public bool HasTable { get; internal set; }
        /// <summary>The `Persistent` attribute (0xE4542E9B): its state goes into the career save (GActivity::SaveState).</summary>
        public readonly bool Persistent;
        internal LuaCFunction ChangeStateFn;
        internal GActivity(GInstance i, List<ScriptState> states) { Instance = i; States = states; Persistent = i.Record.Bool("Persistent"); }
        public string Name => Instance.Name;
        public override string ToString() => $"{Name} [{Current?.Name ?? "-"}]{(Running ? "" : " (stopped)")}";
    }

    /// <summary>
    /// The stategraph runtime of speed.exe on the C# Lua VM: GManager's activities, LuaPostOffice routing, the Event
    /// queue (Hermes::Message::Post 0x654AF0 creates an EDeliverMessage event; ChangeState posts EChangeState 0x621070),
    /// GEventTimers, and the Lua side of the bindings (GRuntimeInstance / LuaAttribArrayInfo / ISimable userdata,
    /// the globals' __index for E* events, binder argument conversion).
    /// </summary>
    public sealed class StategraphRuntime
    {
        public readonly LuaState L = new LuaState();
        public readonly GameplayScripts Data;
        public ILuaGameHost Host;
        /// <summary>Sim::GetTime (Game.GetSimTime, the NotifySimTick SimTime).</summary>
        public float SimTime;
        public int Errors;
        public readonly List<string> ErrorLog = new List<string>();
        public int ErrorLogLimit = 200;
        /// <summary>The handler being run, for logs [ours].</summary>
        public ScriptHandler CurrentHandler { get; private set; }
        public GActivity CurrentActivity { get; private set; }
        /// <summary>After every state change (StateEnter delivered) [ours: Shadow logging, the harness].</summary>
        public event Action<GActivity, ScriptState> StateChanged;
        /// <summary>Every handler that has run (harness coverage) [ours].</summary>
        public readonly HashSet<ScriptHandler> Executed = new HashSet<ScriptHandler>();

        readonly Dictionary<uint, GInstance> _instances = new Dictionary<uint, GInstance>();
        readonly Dictionary<uint, GActivity> _activities = new Dictionary<uint, GActivity>();
        /// <summary>LuaPostOffice registrations (RegisterHandler 0x61FD00): message id → activities, in registration order.</summary>
        readonly Dictionary<uint, List<GActivity>> _post = new Dictionary<uint, List<GActivity>>();
        readonly Queue<Action> _events = new Queue<Action>();
        readonly LuaTable _mtInstance = new LuaTable(), _mtArray = new LuaTable(), _mtSimable = new LuaTable();
        readonly Dictionary<object, LuaUserData> _simables = new Dictionary<object, LuaUserData>();
        readonly LuaTable _registry = new LuaTable();     // per-activity state tables by CollectionName
        /// <summary>bRandom on bDefaultSeed (Math.RandomInt 0x604CB0, Table.Randomize 0x618AC0). Starts at the exe's initial
        /// 0x12345678 (the harness is repeatable); the game re-seeds it like SeedRandomNumber [ours: the seed].</summary>
        public readonly BRandom Random = new BRandom(0x12345678);
        /// <summary>GManager's object-state maps and saved timers (survive this runtime: GameplayActivity's Lua is torn down
        /// at EndGameplay, GManager is not) — see GameplayStateStore.</summary>
        public GameplayStateStore Store = new GameplayStateStore();

        // GManager timers (8 × GEventTimer at +0x144): {duration, active, elapsed, hash, name}
        sealed class Timer { public string Name; public float Duration, Elapsed; public bool Active; }
        readonly Timer[] _timers = Enumerable.Range(0, 8).Select(_ => new Timer()).ToArray();

        // ---- message kinds (Hermes UCrc32 of the message class names; ids read from the handler records) -----------
        public const uint StateEnter = 0x5E1AA4C2, StateExit = 0x9C020C14, NotifyTimer = 0x029084E4, NotifyVehicleDestroyed = 0x04FD4A1B,
            NotifyKnockedOut = 0x0D4D38E5, NotifyFinished = 0x0EF58DC9, NotifyRacePlacement = 0x122C89F1, CareerIntroDone = 0x21F36738,
            NotifyCellCallComplete = 0x23B3ABD0, NotifySpeechStatus = 0x24624AED, NotifyChallengePassed = 0x26BB525D,
            NotifyPlayerRep = 0x2F1DBF5A, NotifySimTick = 0x2F80BB9F, LoadingComplete = 0x319915CD, EnteringGameplay = 0x32F2E00B,
            NotifyRaceTimeSecTick = 0x3E6D010D, TriggerEnter = 0x459EABF5, FlowReadyForOutro = 0x51E7B945, NotifyMessageDone = 0x52584CC7,
            NotifyEngineBlown = 0x5BC34E1C, NotifyCellCallStarted = 0x5E2D52FC, NotifySpeedTrap = 0x5F72B9E8,
            NotifyMilestoneReached = 0x5F7D24A3, NisComplete = 0x6638715C, RestartRace = 0x6CE66AA0, EnterFreeRoam = 0x7FCF45CF,
            DeclineEnterCareerEvent = 0x8870DE98, NotifyMovieFinished = 0x89BB36FE, PerpEscaped = 0x8CB52A16, TriggerExit = 0xA72E4B1B,
            NotifyRaceTimeExpired = 0xAA228814, AcceptEnterCareerEvent = 0xAF4C4648, NotifyOnlineRaceOver = 0xB16576C0,
            NotifyRaceAbandoned = 0xC1E81A41, NotifyMilestoneProgress = 0xC9EABFC6, PerpBusted = 0xCE28AB70, EnterSafehouse = 0xD8E54190,
            CountdownDone = 0xD98FA287, IceCameraFinished = 0xDCDEAB15;

        public static readonly Dictionary<uint, string> MessageNames = new Dictionary<uint, string>
        {
            [StateEnter] = "StateEnter", [StateExit] = "StateExit", [NotifyTimer] = "NotifyTimer", [NotifyVehicleDestroyed] = "NotifyVehicleDestroyed",
            [NotifyKnockedOut] = "NotifyKnockedOut", [NotifyFinished] = "NotifyFinished", [NotifyRacePlacement] = "NotifyRacePlacement",
            [CareerIntroDone] = "CareerIntroDone", [NotifyCellCallComplete] = "NotifyCellCallComplete", [NotifySpeechStatus] = "NotifySpeechStatus",
            [NotifyChallengePassed] = "NotifyChallengePassed", [NotifyPlayerRep] = "NotifyPlayerRep", [NotifySimTick] = "NotifySimTick",
            [LoadingComplete] = "LoadingComplete", [EnteringGameplay] = "EnteringGameplay", [NotifyRaceTimeSecTick] = "NotifyRaceTimeSecTick",
            [TriggerEnter] = "TriggerEnter", [FlowReadyForOutro] = "FlowReadyForOutro", [NotifyMessageDone] = "NotifyMessageDone",
            [NotifyEngineBlown] = "NotifyEngineBlown", [NotifyCellCallStarted] = "NotifyCellCallStarted", [NotifySpeedTrap] = "NotifySpeedTrap",
            [NotifyMilestoneReached] = "NotifyMilestoneReached", [NisComplete] = "NISComplete", [RestartRace] = "RestartRace",
            [EnterFreeRoam] = "EnterFreeRoam", [DeclineEnterCareerEvent] = "DeclineEnterCareerEvent", [NotifyMovieFinished] = "NotifyMovieFinished",
            [PerpEscaped] = "PerpEscaped", [TriggerExit] = "TriggerExit", [NotifyRaceTimeExpired] = "NotifyRaceTimeExpired",
            [AcceptEnterCareerEvent] = "AcceptEnterCareerEvent", [NotifyOnlineRaceOver] = "NotifyOnlineRaceOver",
            [NotifyRaceAbandoned] = "NotifyRaceAbandoned", [NotifyMilestoneProgress] = "NotifyMilestoneProgress", [PerpBusted] = "PerpBusted",
            [EnterSafehouse] = "EnterSafehouse", [CountdownDone] = "CountdownDone", [IceCameraFinished] = "ICECameraFinished",
        };

        public static string MessageName(uint id) => MessageNames.TryGetValue(id, out var n) ? n : id.ToString("X8");

        // ---------------------------------------------------------------------------------------------------------

        public StategraphRuntime(GameplayScripts data, ILuaGameHost host)
        {
            Data = data;
            Host = host;
            InstallMetatables();
            InstallBindings();
            // every handler chunk defines its global function (the exe loads them as the GHandlers are created)
            foreach (var h in data.Handlers)
            {
                if (h.Proto == null) continue;
                try { L.Call(LuaValue.Function(L.Load(h.Proto))); }
                catch (LuaException e) { ReportError(h, null, e.Message); }
            }
        }

        public void Log(string s) => Host?.Log(s);

        void ReportError(ScriptHandler h, GActivity a, string msg)
        {
            Errors++;
            string line = $"error in {h?.Name ?? "?"}{(a != null ? " (" + a.Name + ")" : "")}: {msg}";
            if (ErrorLog.Count < ErrorLogLimit) ErrorLog.Add(line);
            Log(line);
        }

        // ---------------------------------------------------------------------------------------------------------
        // instances and userdata

        public GInstance Instance(AttribCollection c)
        {
            if (c == null) return null;
            if (!_instances.TryGetValue(c.Key, out var g))
            {
                g = new GInstance(c);
                g.Ud = new LuaUserData(g, _mtInstance);
                _instances[c.Key] = g;
            }
            return g;
        }

        public GInstance Instance(uint key) => Instance(Data.Get(key));
        public GInstance Instance(string name) => Instance(Data.Get(name));

        public LuaValue Push(object o)
        {
            switch (o)
            {
                case null: return LuaValue.Nil;
                case LuaValue v: return v;
                case bool b: return LuaValue.Bool(b);
                case int i: return LuaValue.Number(i);
                case uint u: return LuaValue.Number(u);
                case float f: return LuaValue.Number(f);
                case double d: return LuaValue.Number((float)d);
                case string s: return LuaValue.String(s);
                case GInstance g: return LuaValue.UserData(g.Ud);
                case AttribCollection c: return LuaValue.UserData(Instance(c).Ud);
                case GActivity a: return LuaValue.UserData(a.Instance.Ud);
                case LuaTable t: return LuaValue.Table(t);
                default: return LuaValue.UserData(Simable(o));
            }
        }

        /// <summary>An ISimable userdata for a host object (one per object, so raw equality holds).</summary>
        public LuaUserData Simable(object host)
        {
            if (!_simables.TryGetValue(host, out var u)) _simables[host] = u = new LuaUserData(host, _mtSimable);
            return u;
        }

        static GInstance AsInstance(in LuaValue v) => v.Type == LuaType.UserData ? ((LuaUserData)v.O).Value as GInstance : null;

        /// <summary>The binder's argument conversion (luaL_checkudata-like 0x615AA0 for userdata, lua_tonumber, lua_toboolean, lua_tostring).</summary>
        object Arg(char t, in LuaValue v)
        {
            switch (t)
            {
                case 'i': return LuaState.ToNumber(v, out float n) ? (int)n : 0;
                case 'f': return LuaState.ToNumber(v, out float f) ? f : 0f;
                case 'b': return v.IsTrue;
                case 's': return v.IsString ? v.Str : v.IsNumber ? LuaNumber.ToString(v.N) : null;
                case 'I': return AsInstance(v);
                case 'S': return v.Type == LuaType.UserData && ((LuaUserData)v.O).Metatable == _mtSimable ? ((LuaUserData)v.O).Value : null;
                default: return v;
            }
        }

        LuaValue Result(char t, object r)
        {
            switch (t)
            {
                case 'v': return LuaValue.Nil;
                case 'b': return LuaValue.Bool(r is bool b ? b : r != null && !(r is int i0 && i0 == 0));
                case 'i': return LuaValue.Number(r is int i ? i : r is float f ? (int)f : r is bool bb ? (bb ? 1 : 0) : 0);
                case 'f': return LuaValue.Number(r is float f2 ? f2 : r is int i2 ? i2 : r is double d ? (float)d : 0f);
                default: return Push(r);
            }
        }

        void InstallMetatables()
        {
            // GRuntimeInstance: __index (0x61BB60), __newindex (0x61BD60: no setter is registered, so writes are dropped), __eq (0x61BE80)
            _mtInstance.Set("__index", new LuaCFunction("GRuntimeInstance.__index", a => InstanceIndex(AsInstance(a[0]), a[1])));
            _mtInstance.Set("__newindex", new LuaCFunction("GRuntimeInstance.__newindex", a => { }));
            _mtInstance.Set("__eq", new LuaCFunction("GRuntimeInstance.__eq", a => LuaValue.Bool(AsInstance(a[0]) != null && AsInstance(a[0]) == AsInstance(a[1]))));
            // LuaAttribArrayInfo: __index (0x61BF00), __newindex (0x604150), __eq (0x61C0F0: same instance and attribute)
            _mtArray.Set("__index", new LuaCFunction("LuaAttribArrayInfo.__index", a => ArrayIndex(a[0].AsUserData?.Value as GAttribArray, a[1])));
            _mtArray.Set("__newindex", new LuaCFunction("LuaAttribArrayInfo.__newindex", a => { }));
            _mtArray.Set("__eq", new LuaCFunction("LuaAttribArrayInfo.__eq", a =>
            {
                var x = a[0].AsUserData?.Value as GAttribArray;
                var y = a[1].AsUserData?.Value as GAttribArray;
                return LuaValue.Bool(x != null && y != null && x.Owner == y.Owner && x.Field == y.Field);
            }));
            // ISimable: methods (IsPlayer) through __index [?: the exe binds them with FUN_0061B0B0]
            var methods = new LuaTable();
            _mtSimable.Set("__index", methods);
            _mtSimable.Set("__eq", new LuaCFunction("ISimable.__eq", a => LuaValue.Bool(Equals(a[0].AsUserData?.Value, a[1].AsUserData?.Value))));   // [ours]
            // globals: E* names become event constructors on first use (LuaRuntime::HandleGlobalIndex 0x61B970)
            var gmt = new LuaTable();
            gmt.Set("__index", new LuaCFunction("globals.__index", a =>
            {
                string name = a[1].IsString ? a[1].Str : null;
                if (name == null || name.Length == 0 || name[0] != 'E') return LuaValue.Nil;
                var f = EventConstructor(name);
                L.Globals.Set(name, f);
                return f;
            }));
            L.Globals.Metatable = gmt;
            L.Globals.Set("_ISimableMethods", LuaValue.Nil);
            _simableMethods = methods;
        }

        LuaTable _simableMethods;

        /// <summary>
        /// GRuntimeInstance __index (0x61BB60): the attribute of that name (Attrib::Instance::Get, parents included) as a
        /// value of its type (the type table at 0x8F5DC0: numbers, Bool, Text/Char/StringKey strings, GCollectionKey /
        /// RefSpec → the connected instance); array attributes → LuaAttribArrayInfo; no such attribute → the connected
        /// instance by that name (GetConnectedInstance 0x5E2850: entries the class does not define, e.g. a race's
        /// `call_from_mia` child trigger); otherwise nil. Vectors have no handler → nil.
        /// </summary>
        LuaValue InstanceIndex(GInstance g, LuaValue key)
        {
            if (g == null) throw L.Error("bad argument #1 to `__index' (GRuntimeInstance expected)");
            string name = key.IsString ? key.Str : key.IsNumber ? LuaNumber.ToString(key.N) : null;
            if (name == null) return LuaValue.Nil;
            var c = g.Record;
            if (c.TryGet(name, out int at, out int count, out var def))
            {
                if (def.IsArray) return LuaValue.UserData(new LuaUserData(new GAttribArray(g, name), _mtArray));
                return Element(g, name, def, 0);
            }
            var conn = Connected(c, AttribDatabase.Hash(name)) ?? ChildNamed(c, name);
            return conn != null ? Push(conn) : LuaValue.Nil;
        }

        LuaValue ArrayIndex(GAttribArray arr, LuaValue key)
        {
            if (arr == null) throw L.Error("bad argument #1 to `__index' (LuaAttribArrayInfo expected)");
            var c = arr.Owner.Record;
            if (!c.TryGet(arr.Field, out _, out int count, out var def)) return LuaValue.Nil;
            if (LuaState.ToNumber(key, out float n))
            {
                int idx = (int)n - 1;           // __ftol2, dec: 1-based
                if (idx < 0 || idx >= count) return LuaValue.Nil;   // [?] past the end: Attrib returns the default data area
                return Element(arr.Owner, arr.Field, def, idx);
            }
            if (key.IsString && key.Str == "Count") return LuaValue.Number(count);
            return LuaValue.Nil;
        }

        static readonly uint TFloat = AttribDatabase.Hash("EA::Reflection::Float"), TDouble = AttribDatabase.Hash("EA::Reflection::Double"),
            TInt32 = AttribDatabase.Hash("EA::Reflection::Int32"), TUInt32 = AttribDatabase.Hash("EA::Reflection::UInt32"),
            TInt16 = AttribDatabase.Hash("EA::Reflection::Int16"), TUInt16 = AttribDatabase.Hash("EA::Reflection::UInt16"),
            TInt8 = AttribDatabase.Hash("EA::Reflection::Int8"), TUInt8 = AttribDatabase.Hash("EA::Reflection::UInt8"),
            TChar = AttribDatabase.Hash("EA::Reflection::Char"), TBool = AttribDatabase.Hash("EA::Reflection::Bool"),
            TText = AttribDatabase.Hash("EA::Reflection::Text"), TKey = AttribDatabase.Hash("GCollectionKey"),
            TRef = AttribDatabase.Hash("Attrib::RefSpec"), TStringKey = AttribDatabase.Hash("Attrib::StringKey");

        LuaValue Element(GInstance g, string field, AttribField def, int index)
        {
            var c = g.Record;
            uint t = def.Type;
            if (!c.TryGet(field, out int at, out _, out _)) return LuaValue.Nil;
            var d = c.Source.Data;
            if (t == TFloat) return LuaValue.Number(c.Float(field, index));
            if (t == TInt32) return LuaValue.Number(c.Int(field, index));
            if (t == TUInt32) return LuaValue.Number((uint)c.Int(field, index));
            if (t == TInt16) return LuaValue.Number(Chunks.I16(d, at + index * 2));
            if (t == TUInt16) return LuaValue.Number(Chunks.U16(d, at + index * 2));
            if (t == TInt8) return LuaValue.Number((sbyte)d[at + index]);
            if (t == TUInt8) return LuaValue.Number(d[at + index]);
            if (t == TDouble) return LuaValue.Number((float)BitConverter.ToDouble(d, at + index * 8));
            if (t == TBool) return LuaValue.Bool(d[at + index] != 0);
            if (t == TChar) return LuaValue.String(((char)d[at + index]).ToString());
            if (t == TText) return LuaValue.String(c.Text(field, index) ?? "");
            if (t == TStringKey) return LuaValue.String(c.StringKey(field, index) ?? "");
            if (t == TKey || t == TRef)
            {
                var keys = c.Keys(field);
                if (index >= keys.Length) return LuaValue.Nil;
                // GetConnectedInstance(field, index): only collections that exist become instances [?: the exe also
                // needs a runtime instance, which every activity/trigger/marker/character record has]
                var target = Data.Get(keys[index]);
                return target != null ? Push(target) : LuaValue.Nil;
            }
            return LuaValue.Nil;
        }

        /// <summary>
        /// A connection by attribute key (GManager::ConnectInstanceReferences 0x5E4210: every GCollectionKey entry of the
        /// collection, the class-defined and the extra ones, except Children). Extra entries are inline 4-byte keys.
        /// </summary>
        AttribCollection Connected(AttribCollection c, uint key)
        {
            for (var x = c; x != null; x = x.Parent)
            {
                if (!x.Entries.TryGetValue(key, out int off)) continue;
                var d = x.Data;
                if (off + 4 <= d.Length)
                {
                    var t = Data.Get(Chunks.U32(d, off));
                    if (t != null) return t;
                }
                if (off + 12 <= d.Length && Chunks.U16(d, off + 2) > 0)
                {
                    var t = Data.Get(Chunks.U32(d, off + 8));
                    if (t != null) return t;
                }
                return null;
            }
            return null;
        }

        /// <summary>
        /// A child by its local name (FUN_005E43D0: every `Children` entry is connected under StringToKey of the part of its
        /// collection name after the last '/' or '', index 0), e.g. a race's `traffic1` character.
        /// </summary>
        AttribCollection ChildNamed(AttribCollection c, string name)
        {
            foreach (var k in c.Keys("Children"))
            {
                var ch = Data.Get(k);
                if (ch?.Name == null) continue;
                string n = ch.Name;
                int i = Math.Max(n.LastIndexOf('/'), n.LastIndexOf((char)92));   // '/' or '\'
                if (string.Equals(i >= 0 ? n.Substring(i + 1) : n, name, StringComparison.Ordinal)) return ch;
            }
            return null;
        }

        /// <summary>Every instance an activity connects to (its GCollectionKey attributes and extra entries) [ours: for trigger activation].</summary>
        public IEnumerable<GInstance> Connections(GInstance g)
        {
            var seen = new HashSet<uint>();
            uint children = AttribDatabase.Hash("Children");
            for (var x = g.Record; x != null; x = x.Parent)
                foreach (var kv in x.Entries)
                {
                    if (kv.Key == children || !seen.Add(kv.Key)) continue;
                    var d = x.Data;
                    if (x.Class != null && x.Class.Fields.TryGetValue(kv.Key, out var def))
                    {
                        if (def.Type != TKey && def.Type != TRef) continue;
                        if (!g.Record.TryGetKey(kv.Key, out int at, out int count, out _)) continue;
                        var src = g.Record.Source;
                        int stride = def.Size >= 12 ? def.Size : 4, ko = def.Size >= 12 ? 4 : 0;
                        for (int i = 0; i < count; i++)
                        {
                            var t = Data.Get(Chunks.U32(src.Data, at + i * stride + ko));
                            if (t != null) yield return Instance(t);
                        }
                    }
                    else
                    {
                        if (kv.Value + 4 > d.Length) continue;
                        var t = Data.Get(Chunks.U32(d, kv.Value));
                        if (t != null && t.Key != x.Key) yield return Instance(t);
                    }
                }
        }

        /// <summary>The instance's Children (the `Children` GCollectionKey array).</summary>
        public IEnumerable<GInstance> Children(GInstance g) => g.Record.Keys("Children").Select(k => Data.Get(k)).Where(c => c != null).Select(Instance);

        // ---------------------------------------------------------------------------------------------------------
        // bindings

        LuaCFunction EventConstructor(string name)
        {
            // E*_MakeEvent_LuaBinding: builds the event from the arguments; it executes when the Event queue runs
            return new LuaCFunction(name, a =>
            {
                var args = new object[a.Count];
                for (int i = 0; i < a.Count; i++)
                {
                    var v = a[i];
                    args[i] = v.Type == LuaType.UserData ? ((LuaUserData)v.O).Value : v.IsNumber ? (object)v.N : v.IsString ? v.Str : v.Type == LuaType.Boolean ? (object)v.IsTrue : null;
                }
                _events.Enqueue(() => Host?.Event(name, args));
            });
        }

        void InstallBindings()
        {
            foreach (var b in LuaBindingTable.All)
            {
                var bind = b;
                LuaCFunction fn;
                if (b.Table == "Table" && b.Name == "Randomize") fn = new LuaCFunction("Table.Randomize", (Func<LuaArgs, int>)TableRandomize);
                else
                {
                    string sig = b.Args;
                    int self = b.Method && b.Table != "ISimable" ? 1 : 0;
                    fn = new LuaCFunction(b.FullName, a =>
                    {
                        var args = new object[sig.Length];
                        for (int i = 0; i < sig.Length; i++) args[i] = Arg(sig[i], a[i + self]);
                        object r = bind.Kind == LuaBinding.Kinds.Core ? CoreCall(bind, args) : Host?.Call(bind, args);
                        return Result(bind.Result, r);
                    }, b.Result == 'v');
                }
                if (b.Table == "ISimable") _simableMethods.Set(b.Name, fn);
                else L.Library(b.Table).Set(b.Name, fn);
            }
        }

        /// <summary>
        /// Table_Randomize (0x618AC0, a raw binding): n = luaL_getn(t); n passes of: for i = 1 .. n-1, j = bRandom(n) + 1,
        /// and when j != i swap t[i] and t[j] (luaH_getnum / newkey). Nothing for a non-table or n &lt; 1.
        /// </summary>
        int TableRandomize(LuaArgs a)
        {
            var t = a.Count > 0 ? a[0].AsTable : null;
            if (t == null) return 0;
            int n = t.Length;
            for (int pass = n; pass > 0; pass--)
                for (int i = 1; i < n; i++)
                {
                    int j = (int)Random.Next(n) + 1;
                    if (j == i) continue;
                    var x = t.Get(i);
                    var y = t.Get(j);
                    t.Set(i, y);
                    t.Set(j, x);
                }
            return 0;
        }

        object CoreCall(LuaBinding b, object[] args)
        {
            switch (b.Table + "." + b.Name)
            {
                case "Activity.Run":
                    // the host may answer false: do not restart it (Shadow: the C# flow already runs that race) [ours]
                    if (args[0] is GInstance ri && !(Host?.Call(b, args) is bool run && !run)) Run(ri);
                    return null;
                case "Activity.Suspend":
                    if (args[0] is GInstance si && _activities.TryGetValue(si.Key, out var sa)) Suspend(sa);
                    return null;
                case "Math.RandomInt":
                    return (int)Random.Next((int)args[0]) + 1;   // 0x604CB0: bRandom(n) + 1 (1 for n = 0)
                case "Debug.Print":
                    Log("print: " + args[0]);
                    return null;
                case "Debug.PrintInstance":
                    Log("print: " + args[0]);
                    return null;
                case "Debug.Assert":
                    return null;
                case "Game.GetSimTime":
                    return SimTime;
                case "Game.SetTimer":
                    SetTimer((string)args[0], (float)args[1]);
                    return null;
                case "Game.KillTimer":
                    KillTimer((string)args[0]);
                    return null;
                case "Game.NotifyActivityFinsihed":
                    // Game_NotifyFinished 0x605420: MNotifyFinished(GCollectionKey(activity)).Post
                    Host?.Call(b, args);
                    Post(NotifyFinished, ("Sender", args[0]));
                    return null;
                case "Game.NotifyRacePlacement":
                    // 0x60AA00: MNotifyRacePlacement(activity, racer, placing).Post
                    Host?.Call(b, args);
                    Post(NotifyRacePlacement, ("Activity", args[0]), ("Racer", args[1]), ("Placing", args[2]));
                    return null;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // timers

        /// <summary>GManager::SetTimer 0x5E4A40: restart a timer of that name, else take a free slot (8).</summary>
        public bool SetTimer(string name, float seconds)
        {
            if (name == null) return false;
            var t = _timers.FirstOrDefault(x => x.Name == name);
            if (t == null) t = _timers.FirstOrDefault(x => !x.Active);
            if (t == null) return false;
            t.Name = name;
            t.Duration = seconds;
            t.Elapsed = 0;
            t.Active = true;
            return true;
        }

        public void KillTimer(string name)
        {
            foreach (var t in _timers) if (t.Name == name) { t.Active = false; t.Elapsed = 0; }
        }

        public IEnumerable<(string name, float left)> Timers => _timers.Where(t => t.Active).Select(t => (t.Name, t.Duration - t.Elapsed));

        /// <summary>GManager::SaveTimerInfo (0x5E4C40): the 8 slots {duration, active, elapsed, name}.</summary>
        public void SaveTimers(List<SavedTimer> into)
        {
            into.Clear();
            foreach (var t in _timers) into.Add(new SavedTimer { Name = t.Name, Duration = t.Duration, Elapsed = t.Elapsed, Active = t.Active });
        }

        /// <summary>GManager::LoadTimerInfo (0x5E4C90): the slots back (the name hash recomputed).</summary>
        public void LoadTimers(IList<SavedTimer> from)
        {
            for (int i = 0; i < _timers.Length; i++)
            {
                var s = from != null && i < from.Count ? from[i] : null;
                _timers[i].Name = s?.Name;
                _timers[i].Duration = s?.Duration ?? 0;
                _timers[i].Elapsed = s?.Elapsed ?? 0;
                _timers[i].Active = s != null && s.Active && s.Name != null;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // activities

        public GActivity Activity(GInstance g)
        {
            if (g == null) return null;
            if (!_activities.TryGetValue(g.Key, out var a))
            {
                a = new GActivity(g, Data.StatesOf(g.Record));
                foreach (var s in a.States) a.Handlers[s] = Data.HandlersFor(s, g.Record);
                var act = a;
                a.ChangeStateFn = new LuaCFunction("ChangeState", args =>
                {
                    // ChangeStateFromScript / FUN_005ED7D0: GetStateByName, then EChangeState is queued
                    string name = args[0].IsString ? args[0].Str : null;
                    var st = act.States.FirstOrDefault(s => s.Name == name);
                    _events.Enqueue(() => ChangeState(act, st));
                });
                _activities[g.Key] = a;
                LoadState(a);   // the GActivity constructor (0x5F9C30) restores a saved state
            }
            return a;
        }

        public IEnumerable<GActivity> Activities => _activities.Values;
        public IEnumerable<GActivity> RunningActivities => _activities.Values.Where(a => a.Running);

        public GActivity Run(string name) => Run(Instance(name));

        /// <summary>
        /// Activity.Run (0x6048A0) = GActivity::Reset (0x5F4780: stop, drop the state table and the saved block) then
        /// GActivity::Run (0x5F93D0, <see cref="Start"/>): it starts at `initial`.
        /// </summary>
        public GActivity Run(GInstance g)
        {
            var a = Activity(g);
            if (a == null) return null;
            Reset(a);
            Start(a);
            return a;
        }

        /// <summary>
        /// GActivity::Run (0x5F93D0) without the Reset — what GManager::StartActivities (0x5F9E70 / 0x5F95F0), EnterBin
        /// (0x602BB0) and StartRaceFromInGame call: nothing when running; else mark running (ActivateReferencedTriggers),
        /// and with a current state (a suspended activity, or one LoadState restored) only register that state's
        /// handlers — no StateEnter — otherwise EChangeState("initial").
        /// </summary>
        public GActivity Start(GActivity a)
        {
            if (a == null || a.Running) return a;
            a.Running = true;
            if (a.Current != null)
            {
                Register(a);
                return a;
            }
            var initial = a.States.FirstOrDefault(s => s.Name == "initial");
            _events.Enqueue(() => ChangeState(a, initial));
            return a;
        }

        /// <summary>GActivity::Reset (0x5F4780): stop (triggers off, handlers unregistered), registry[name] = nil, the
        /// table flag cleared, ClearObjectStateBlock(key), no current state.</summary>
        public void Reset(GActivity a)
        {
            if (a.Running) Suspend(a);
            _registry.Set(GameplayScripts.CollectionName(a.Instance.Record), LuaValue.Nil);
            a.HasTable = false;
            Store.Clear(a.Instance.Key);
            a.Current = null;
        }

        /// <summary>
        /// GActivity::SaveState (0x5F6BA0): with a state table, a block {stringhash32(state Name), flags (running, state
        /// `done`), the table — not in `done`} in the persistent map for a `Persistent` activity, else the transient one;
        /// `release` then drops the table (registry[name] = nil, +0x3D = 0).
        /// </summary>
        public void SaveState(GActivity a, bool release)
        {
            if (a == null || !a.HasTable) return;
            string key = GameplayScripts.CollectionName(a.Instance.Record);
            bool done = a.Current != null && a.Current.Name == "done";
            var b = new ActivityStateBlock
            {
                Key = a.Instance.Key,
                StateName = a.Current?.Name,
                StateHash = a.Current?.Name is string sn && sn.Length > 0 ? AttribDatabase.Hash(sn) : 0,   // stringhash32 0x5CC240
                Flags = (a.Running ? 1 : 0) | (done ? 2 : 0),
            };
            var tv = _registry.Get(key);
            if (!done && tv.Type == LuaType.Table)
            {
                if (a.Persistent) b.Table = GameplayStateStore.Serialize(tv.AsTable);
                else b.Live = GameplayStateStore.Copy(tv.AsTable);
            }
            Store.Put(b, a.Persistent);
            if (release)
            {
                _registry.Set(key, LuaValue.Nil);
                a.HasTable = false;
            }
        }

        /// <summary>
        /// GActivity::LoadState (0x5F9480): the saved block, if any — the current state whose stringhash32(Name) matches
        /// (handlers are not registered here) and the state table (+0x3D set). The running flag is not restored.
        /// </summary>
        void LoadState(GActivity a)
        {
            var b = Store.Find(a.Instance.Key);
            if (b == null) return;
            if (b.StateHash != 0)
            {
                var st = a.States.FirstOrDefault(s => s.Name != null && AttribDatabase.Hash(s.Name) == b.StateHash);
                if (st != null) a.Current = st;
            }
            // a transient block may come from an earlier runtime: its instances are this runtime's by key, other userdata
            // (simables) only if this runtime made them [ours: the exe copies the raw userdata bytes]
            var t = b.Live != null ? GameplayStateStore.Copy(b.Live, RemapUserData) : GameplayStateStore.Deserialize(b.Table);
            if (t != null)
            {
                _registry.Set(GameplayScripts.CollectionName(a.Instance.Record), LuaValue.Table(t));
                a.HasTable = true;
            }
        }

        LuaValue RemapUserData(LuaValue v)
        {
            var u = v.AsUserData;
            if (u?.Value is GInstance g) { var mine = Instance(g.Record); return mine != null ? LuaValue.UserData(mine.Ud) : LuaValue.Nil; }
            if (u != null && u.Value != null && _simables.TryGetValue(u.Value, out var s) && ReferenceEquals(s, u)) return v;
            return v.Type == LuaType.LightUserData ? v : LuaValue.Nil;
        }

        /// <summary>GManager::SaveGameplayData (0x5F7410)'s first loop: every activity's SaveState(false) [ours: the
        /// activities this runtime made; one never made has no table].</summary>
        public void SaveAll()
        {
            foreach (var a in _activities.Values) SaveState(a, false);
            SaveTimers(Store.Timers);
        }

        /// <summary>
        /// The end of gameplay (GameplayActivity's destructor → the vaults' objects destroyed): every `Persistent` activity
        /// pages its state into the persistent map (GActivity dtor 0x5F7C10: SaveState(true)); GManager keeps the maps and
        /// the timers for the next gameplay session.
        /// </summary>
        public void Shutdown()
        {
            foreach (var a in _activities.Values)
                if (a.Persistent) SaveState(a, true);
            SaveTimers(Store.Timers);
            Store.SimTime = SimTime;
        }

        /// <summary>A new runtime over a store (GameplayActivity created again; GManager's maps and timers survive).</summary>
        public void Attach(GameplayStateStore store)
        {
            Store = store ?? new GameplayStateStore();
            LoadTimers(Store.Timers);
            SimTime = Store.SimTime;
        }

        /// <summary>GActivity::Suspend (0x5F4730): stop, deactivate triggers, unregister the handlers (state kept).</summary>
        public void Suspend(GActivity a)
        {
            if (!a.Running) return;
            a.Running = false;
            Unregister(a);
        }

        /// <summary>GActivity::ChangeState (0x5FA3C0): StateExit to the old state, swap handlers, StateEnter to the new one.</summary>
        void ChangeState(GActivity a, ScriptState next)
        {
            if (!a.Running) return;   // [?] a stopped activity ignores a queued change
            if (!a.HasTable) LoadState(a);   // 0x5FA3CB
            if (next == a.Current) return;
            if (a.Current != null) Deliver(a, StateExit, null);
            Unregister(a);
            if (!a.HasTable) LoadState(a);   // 0x5FA400
            a.Current = next;
            if (next != null)
            {
                Register(a);
                Deliver(a, StateEnter, null);
            }
            StateChanged?.Invoke(a, next);
        }

        void Register(GActivity a)
        {
            if (a.Current == null || !a.Handlers.TryGetValue(a.Current, out var hs)) return;
            foreach (var h in hs)
            {
                if (!_post.TryGetValue(h.MessageId, out var list)) _post[h.MessageId] = list = new List<GActivity>();
                if (!list.Contains(a)) list.Add(a);
            }
        }

        void Unregister(GActivity a)
        {
            foreach (var list in _post.Values) list.Remove(a);
        }

        // ---------------------------------------------------------------------------------------------------------
        // messages

        /// <summary>Hermes::Message::Post: queued as EDeliverMessage, delivered to every registered activity when the queue runs.</summary>
        public void Post(uint kind, params (string field, object value)[] fields)
        {
            _events.Enqueue(() =>
            {
                if (!_post.TryGetValue(kind, out var list) || list.Count == 0) return;
                foreach (var a in list.ToArray()) Deliver(a, kind, fields);
            });
        }

        /// <summary>Queue an arbitrary action in the event order (host events) [ours].</summary>
        public void Enqueue(Action a) => _events.Enqueue(a);

        /// <summary>
        /// GActivity::HandleMessage (0x5F9DA0): every handler of the current state for this message (FilterBlocks are
        /// unused by the PC data) is called as handler(r0 = the activity instance, r1 = the message table, r2 = the
        /// state table) after the global ChangeState is bound to this activity (BuildActivityTables 0x5F9D30).
        /// </summary>
        void Deliver(GActivity a, uint kind, (string field, object value)[] fields)
        {
            if (!a.Running || a.Current == null || !a.Handlers.TryGetValue(a.Current, out var hs)) return;
            bool built = false;
            LuaValue self = default, msg = default, state = default;
            var stateAtStart = a.Current;
            foreach (var h in hs)
            {
                if (h.MessageId != kind || h.Proto == null) continue;
                if (!built)
                {
                    built = true;
                    self = Push(a.Instance);
                    var t = new LuaTable();
                    if (fields != null) foreach (var (f, v) in fields) t.Set(f, Push(v));
                    msg = LuaValue.Table(t);
                    state = StateTable(a);
                    L.Globals.Set("ChangeState", LuaValue.Function(a.ChangeStateFn));
                }
                var fn = L.Globals.Get(h.Name);
                if (fn.Type != LuaType.Function) { ReportError(h, a, "handler function missing"); continue; }
                var prevH = CurrentHandler; var prevA = CurrentActivity;
                CurrentHandler = h; CurrentActivity = a;
                Executed.Add(h);
                try { L.Call(fn, self, msg, state); }
                catch (LuaException e) { ReportError(h, a, e.Message); }
                finally { CurrentHandler = prevH; CurrentActivity = prevA; }
                // GActivity::HandleMessage (0x5F9DA0) walks the state's handler vector to its end, also after a Suspend
                // inside a handler; a Reset (Activity.Run of itself) erases that vector in the exe — we stop [?]
                if (a.Current != stateAtStart) break;
            }
        }

        /// <summary>Immediate state change, bypassing the queue (harness / debugging) [ours].</summary>
        public void ForceState(GActivity a, ScriptState s)
        {
            a.Running = true;
            ChangeState(a, s);
        }

        /// <summary>Immediate delivery to one activity, bypassing the queue (harness / debugging) [ours].</summary>
        public void DeliverNow(GActivity a, uint kind, params (string field, object value)[] fields) => Deliver(a, kind, fields);

        /// <summary>The state table getter (0x5F9CC0): LoadState first when there is none, else registry[name], created on
        /// first use.</summary>
        LuaValue StateTable(GActivity a)
        {
            if (!a.HasTable) LoadState(a);
            string key = GameplayScripts.CollectionName(a.Instance.Record);
            var v = _registry.Get(key);
            if (v.Type != LuaType.Table) { v = LuaValue.Table(new LuaTable()); _registry.Set(key, v); }
            a.HasTable = true;
            return v;
        }

        /// <summary>The activity's Lua state table (r2) for inspection [ours].</summary>
        public LuaTable StateOf(GActivity a) => StateTable(a).AsTable;

        // ---------------------------------------------------------------------------------------------------------
        // the frame

        /// <summary>Runs queued events (EChangeState / EDeliverMessage / E* events) until none are left.</summary>
        public int Pump(int max = 10000)
        {
            int n = 0;
            while (_events.Count > 0 && n < max)
            {
                var e = _events.Dequeue();
                n++;
                try { e(); }
                catch (LuaException ex) { ReportError(CurrentHandler, CurrentActivity, ex.Message); }
            }
            if (_events.Count > 0) Log($"event queue still has {_events.Count} events after {max}");
            return n;
        }

        public int PendingEvents => _events.Count;

        /// <summary>One sim tick: the GEventTimers (repeating: NotifyTimer each time the duration elapses), NotifySimTick, the queue.</summary>
        public void Tick(float dt)
        {
            SimTime += dt;
            foreach (var t in _timers)
            {
                if (!t.Active) continue;
                t.Elapsed += dt;
                if (t.Elapsed >= t.Duration)
                {
                    Post(NotifyTimer, ("Name", t.Name));
                    t.Elapsed -= t.Duration;
                    if (t.Duration <= 0) t.Elapsed = 0;
                }
            }
            if (_post.TryGetValue(NotifySimTick, out var l) && l.Count > 0) Post(NotifySimTick, ("SimTime", SimTime));
            Pump();
        }
    }
}
