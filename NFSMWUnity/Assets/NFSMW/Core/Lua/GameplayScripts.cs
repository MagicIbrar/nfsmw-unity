using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NfsMw.Core.Lua
{
    /// <summary>
    /// One `messagehandler` record of gameplay.bin (GHandler, created by GObjectBlock::CreateObjects_GHandler_ 0x5F55D0):
    /// handler_owner (the activity or template it belongs to), stateref (the GState), message_id (the Hermes message kind),
    /// FilterBlocks / FilterModePassAll (GHandler::MessagePassesFilters 0x5DD410) and `bytecode` (Attrib::Blob {u32 size,
    /// ptr} → EA HUFF → Lua 5.0 bytecode defining the global function named after the record).
    /// </summary>
    public sealed class ScriptHandler
    {
        public AttribCollection Record;
        public string Name;
        public uint StateKey, OwnerKey, MessageId;
        public byte[] Bytecode;
        public LuaProto Proto;
        public string LoadError;
        /// <summary>The raw FilterBlocks entries (each: u8 message offset, u8 argument offset, u8 size, u8 resolved, query fn,
        /// u32 query hash, argument bytes).</summary>
        public readonly List<byte[]> Filters = new List<byte[]>();
        public bool FilterPassAll;
        public override string ToString() => Name;
    }

    /// <summary>A `state` record (GState, CreateObjects_GState_ 0x5F5730): `Name` and `templateref` (the owning stategraph).</summary>
    public sealed class ScriptState
    {
        public AttribCollection Record;
        public string Name;
        public uint TemplateKey;
        public override string ToString() => Name + "@" + (Record?.Name ?? "?");
    }

    /// <summary>
    /// The stategraph data of gameplay.bin: every handler (with its decoded chunk) and every state, and the exe's rules
    /// for which belong to an activity (GActivity::GatherStatesAndHandlers 0x5F9270).
    /// </summary>
    public sealed class GameplayScripts
    {
        public AttribDatabase Db;
        public readonly List<AttribCollection> All = new List<AttribCollection>();
        public readonly Dictionary<uint, AttribCollection> ByKey = new Dictionary<uint, AttribCollection>();
        public readonly List<ScriptHandler> Handlers = new List<ScriptHandler>();
        public readonly List<ScriptState> States = new List<ScriptState>();
        readonly Dictionary<string, AttribCollection> _byName = new Dictionary<string, AttribCollection>(StringComparer.OrdinalIgnoreCase);

        // field hashes the exe reads by key (names recovered with the Jenkins hash)
        public static readonly uint KStateRef = AttribDatabase.Hash("stateref");            // 0x918C796E
        public static readonly uint KHandlerOwner = AttribDatabase.Hash("handler_owner");   // 0x857FE432
        public static readonly uint KTemplateRef = AttribDatabase.Hash("templateref");      // 0xA0697302
        public static readonly uint KFilterBlocks = AttribDatabase.Hash("FilterBlocks");    // 0x56E1436D
        public static readonly uint KFilterPassAll = AttribDatabase.Hash("FilterModePassAll"); // 0x0D038CFA

        public static GameplayScripts Load(string root)
        {
            var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"));
            return Load(db);
        }

        public static GameplayScripts Load(AttribDatabase db)
        {
            var s = new GameplayScripts { Db = db };
            foreach (var c in db.OfClass("gameplay"))
            {
                s.All.Add(c);
                s.ByKey[c.Key] = c;
                if (c.Name != null) s._byName[c.Name] = c;
            }
            uint handlerTemplate = AttribDatabase.Hash("messagehandler"), stateTemplate = AttribDatabase.Hash("state");
            foreach (var c in s.All)
            {
                if (s.DerivesFrom(c, handlerTemplate) && c.Key != handlerTemplate && c.Has("bytecode")) s.Handlers.Add(s.ReadHandler(c));
                else if (s.DerivesFrom(c, stateTemplate) && c.Key != stateTemplate) s.States.Add(new ScriptState { Record = c, Name = c.Text("Name") ?? c.Name, TemplateKey = First(c, "templateref") });
            }
            return s;
        }

        public AttribCollection Get(uint key) => ByKey.TryGetValue(key, out var c) ? c : null;
        public AttribCollection Get(string name) => name != null && _byName.TryGetValue(name, out var c) ? c : null;

        static uint First(AttribCollection c, string field)
        {
            var k = c.Keys(field);
            return k.Length > 0 ? k[0] : 0;
        }

        /// <summary>The collection or one of its parents is `key` (GRuntimeInstance::IsDerivedFromTemplate 0x5E28B0).</summary>
        public bool DerivesFrom(AttribCollection c, uint key)
        {
            for (var x = c; x != null; x = x.Parent) if (x.Key == key) return true;
            return false;
        }

        ScriptHandler ReadHandler(AttribCollection c)
        {
            var h = new ScriptHandler
            {
                Record = c,
                Name = c.Name,
                StateKey = First(c, "stateref"),
                OwnerKey = First(c, "handler_owner"),
                MessageId = (uint)c.Int("message_id"),
                FilterPassAll = c.Bool("FilterModePassAll"),
            };
            try
            {
                if (c.TryGet("bytecode", out int at, out _, out _))
                {
                    var src = c.Source;
                    int size = Chunks.I32(src.Data, at);
                    int p = src.Db.Ptr(src.Pack, at + 4);
                    if (p >= 0 && size > 0)
                    {
                        h.Bytecode = Compression.Decompress(src.Data, p);
                        h.Proto = LuaUndump.Load(h.Bytecode, "=" + c.Name);
                    }
                    else h.LoadError = "no bytecode blob";
                }
            }
            catch (Exception e) { h.LoadError = e.Message; }
            try
            {
                // FilterBlocks: an array of Attrib::Blob {size, ptr}
                if (c.TryGet("FilterBlocks", out int fat, out int fcount, out var def))
                {
                    var src = c.Source;
                    int stride = def.Size > 0 ? def.Size : 8;
                    for (int i = 0; i < fcount; i++)
                    {
                        int size = Chunks.I32(src.Data, fat + i * stride);
                        int p = src.Db.Ptr(src.Pack, fat + i * stride + 4);
                        if (p < 0 || size <= 0 || p + size > src.Data.Length) continue;
                        var blk = new byte[size];
                        Array.Copy(src.Data, p, blk, 0, size);
                        h.Filters.Add(blk);
                    }
                }
            }
            catch (Exception) { }
            return h;
        }

        /// <summary>
        /// An AutoStart activity whose vault a career at bin `bin` has loaded (harness `lua order`): the gpcore ones (menu
        /// gates, speedtraps, milestones, race_bin_16/drive_to_safe_house — there is no race_bin_16 vault) and the current
        /// bin's race_bin_NN/engage, in the race_bin_NN vault GRaceBin keeps loaded (EnterBin 0x602BB0 → LoadVaultSync;
        /// the previous bin's vault goes). race_bin_opm's live in vaults a career never loads. GManager's
        /// StartActivitiesCall1 (0x5F95F0) runs every one of them (AutoStart, and FreeRoamOnly only without a startup race).
        /// </summary>
        public static bool AutoStartLoaded(AttribCollection c, int bin)
        {
            if (c == null || !c.Bool("AutoStart") || c.Bool("Template") || c.Name == null || c.Name.StartsWith("race_bin_opm")) return false;
            if (!c.Name.StartsWith("race_bin_")) return true;
            return c.Name.Length > 11 && int.TryParse(c.Name.Substring(9, 2), out int b) && (b == bin || b == 16);
        }

        /// <summary>The gameplay layout's first field (CollectionName), which the exe uses as the activity's name.</summary>
        public static string CollectionName(AttribCollection c) => c.Text("CollectionName") ?? c.Name;

        /// <summary>
        /// The states of an activity (FUN_005E2D70): a state belongs to it when its templateref is the activity or one of
        /// the activity's ancestors (its stategraph template).
        /// </summary>
        public List<ScriptState> StatesOf(AttribCollection activity)
        {
            var r = new List<ScriptState>();
            foreach (var st in States)
                for (var x = activity; x != null; x = x.Parent)
                    if (st.TemplateKey == x.Key) { r.Add(st); break; }
            return r;
        }

        /// <summary>
        /// The handlers of one state of an activity (FUN_005E2E70): stateref is the state, and handler_owner is the activity
        /// itself, or an owner with the same CollectionName (layout field 0), or one of the activity's ancestors. Template and
        /// instance handlers both apply, in the GHandler ring order: GatherStatesAndHandlers (0x5F9270) fills each state's
        /// vector with FUN_005F5B90, which walks the GHandler ring (DAT_0091DFEC) from its head; GRuntimeInstance's ctor
        /// (0x5E8130) links each new instance at the tail, and CreateObjects_GHandler_ (0x5F55D0) makes them in the vault's
        /// export order (FUN_005F4090). Every messagehandler is in gpcore (`lua order`), so that is gameplay.bin's ExpN
        /// order — the order AttribDatabase keeps (an instance's handler may come before its template's: 40 such groups).
        /// </summary>
        public List<ScriptHandler> HandlersFor(ScriptState state, AttribCollection activity)
        {
            var r = new List<ScriptHandler>();
            string actName = CollectionName(activity);
            foreach (var h in Handlers)
            {
                if (h.StateKey != state.Record.Key) continue;
                if (h.OwnerKey == activity.Key) { r.Add(h); continue; }
                var owner = Get(h.OwnerKey);
                if (owner != null && actName != null && CollectionName(owner) == actName) { r.Add(h); continue; }
                for (var x = activity; x != null; x = x.Parent)
                    if (x.Key == h.OwnerKey) { r.Add(h); break; }
            }
            return r;
        }
    }
}
