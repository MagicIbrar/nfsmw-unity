using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NfsMw.Core.Lua
{
    /// <summary>
    /// bRandom (bWare/bMath.cpp, speed.exe 0x45DA30 int / 0x45D9A0 with a seed): r = seed % range, then
    /// seed' = t ^ (n ^ (t &lt;&lt; 27)) with n = seed ^ 0x1D872B41, t = n ^ (n &gt;&gt; 5). bDefaultSeed starts at 0x12345678 and
    /// SeedRandomNumber (Main.cpp) re-seeds it from the ticker (bSetRandomSeed = seed, then one draw). The game's one
    /// default seed is shared by every caller (traffic, AI, FE, …), so the scripts' draws cannot follow the exe's sequence;
    /// the generator and its use are the exe's [?: the sequence].
    /// </summary>
    public sealed class BRandom
    {
        public uint Seed;

        public BRandom(uint seed) { SetSeed(seed); }

        /// <summary>bSetRandomSeed (bMath.cpp): the value, then one draw.</summary>
        public void SetSeed(uint value) { Seed = value; Next(1); }

        /// <summary>bRandom(int range): 0 .. range-1 (0 for range 0).</summary>
        public uint Next(int range)
        {
            if (range == 0) return 0;
            uint result = Seed;
            uint next = result ^ 0x1D872B41;
            uint temp = next ^ (next >> 5);
            Seed = temp ^ (next ^ (temp << 27));
            return result % (uint)range;
        }
    }

    /// <summary>
    /// One object-state block (GManager::AllocObjectStateBlock 0x5F5F10, written by GActivity::SaveState 0x5F6BA0): the
    /// activity's current state as stringhash32 of its Name (0 = none), flags (bit 0 running, bit 1 the state is `done`,
    /// high word = the Lua table's size) and the serialized state table (none in `done`).
    /// </summary>
    public sealed class ActivityStateBlock
    {
        public uint Key;
        public uint StateHash;
        /// <summary>The state's Name [ours: the exe keeps only the hash].</summary>
        public string StateName;
        public int Flags;
        /// <summary>The serialized table (persistent map), or null.</summary>
        public byte[] Table;
        /// <summary>A transient block's table as a live copy [ours: the exe copies the bytes, userdata included].</summary>
        public LuaTable Live;
        public bool Running => (Flags & 1) != 0;
        public bool Done => (Flags & 2) != 0;
    }

    /// <summary>A saved activity state in the career save (SaveGameplayData's persistent object-state map) [ours: the format].</summary>
    [Serializable]
    public sealed class SavedActivityState
    {
        public string Activity;     // collection name
        public uint Key;
        public string State;
        public uint StateHash;
        public int Flags;
        public string Table;        // base64 of the serialized table ("" = none)
    }

    /// <summary>A GManager timer in the career save (SaveTimerInfo 0x5E4C40: 8 × {duration, active, elapsed, name[19]}).</summary>
    [Serializable]
    public sealed class SavedTimer
    {
        public string Name;
        public float Duration, Elapsed;
        public bool Active;
    }

    /// <summary>
    /// GManager's object-state maps (+0x12C persistent, +0x138 transient; ObjectStateBlockHeader map) and the saved timers:
    ///   * GActivity::SaveState (0x5F6BA0) writes an activity's block into the persistent map when the activity's
    ///     `Persistent` attribute (0xE4542E9B) is true, else into the transient map. It runs for every activity at
    ///     SaveGameplayData (0x5F7410, the career save), for a persistent activity at its destruction (dtor 0x5F7C10:
    ///     EndGameplay), for the bins at StartRaceFromInGame (0x5F77F0) and when the Lua heap is short (0x612E80).
    ///   * GActivity::LoadState (0x5F9480) reads it back whenever the activity has no state table (its construction,
    ///     ChangeState 0x5FA3C0, the state-table getter 0x5F9CC0): the current state and the table — not the running
    ///     flag. GActivity::Run (0x5F93D0) of an activity with a current state only registers that state's handlers (no
    ///     StateEnter), so GManager::StartActivities resumes the saved stategraphs.
    ///   * GActivity::Reset (Activity.Run 0x6048A0) clears the block (ClearObjectStateBlock 0x5F30F0).
    ///   * SaveGameplayData stores the persistent map (and the timers, milestones, speed traps, pending SMS, the free-roam
    ///     markers); LoadGameplayData (0x5F81D0) first runs ResetAllGameplayData (0x5F7730: both maps and the timers
    ///     cleared) and then restores them. The transient map lives as long as GManager (the session).
    /// The serializer (FUN_00617A80, reader FUN_00617F80) keeps pairs whose key is a boolean, number or string; values
    /// boolean, number, string, table — and userdata only in the transient map. [ours: the byte layout; the exe's packs
    /// small integers and dictionary strings.]
    /// </summary>
    public sealed class GameplayStateStore
    {
        public readonly Dictionary<uint, ActivityStateBlock> Persistent = new Dictionary<uint, ActivityStateBlock>();
        public readonly Dictionary<uint, ActivityStateBlock> Transient = new Dictionary<uint, ActivityStateBlock>();
        public readonly List<SavedTimer> Timers = new List<SavedTimer>();
        /// <summary>Sim::GetTime where the last runtime stopped: a new runtime of the same GManager goes on from it [ours:
        /// the exe's sim clock is not the Lua's; a loaded save starts at 0].</summary>
        public float SimTime;

        /// <summary>ResetAllGameplayData (0x5F7730): both maps and the timers (ResetTimers 0x5E4B10).</summary>
        public void ResetAll()
        {
            Persistent.Clear();
            Transient.Clear();
            Timers.Clear();
        }

        /// <summary>ClearObjectStateBlock (0x5F30F0): the activity's block in both maps.</summary>
        public void Clear(uint key)
        {
            Persistent.Remove(key);
            Transient.Remove(key);
        }

        /// <summary>FUN_005ED960: the block in the transient map, else the persistent one.</summary>
        public ActivityStateBlock Find(uint key) =>
            Transient.TryGetValue(key, out var b) ? b : Persistent.TryGetValue(key, out b) ? b : null;

        public void Put(ActivityStateBlock b, bool persistent)
        {
            // AllocObjectStateBlock: one block per key and map
            (persistent ? Persistent : Transient)[b.Key] = b;
        }

        // ------------------------------------------------------------------------------------------------------------
        // the career save

        /// <summary>SaveGameplayData's part for the scripts: the persistent map.</summary>
        public List<SavedActivityState> SavePersistent(Func<uint, string> nameOf)
        {
            var r = new List<SavedActivityState>();
            foreach (var b in Persistent.Values)
                r.Add(new SavedActivityState
                {
                    Activity = nameOf?.Invoke(b.Key) ?? "",
                    Key = b.Key,
                    State = b.StateName ?? "",
                    StateHash = b.StateHash,
                    Flags = b.Flags,
                    Table = b.Table != null ? Convert.ToBase64String(b.Table) : "",
                });
            return r;
        }

        /// <summary>LoadGameplayData: ResetAllGameplayData, then the persistent blocks and the timers.</summary>
        public void Load(IEnumerable<SavedActivityState> states, IEnumerable<SavedTimer> timers)
        {
            ResetAll();
            if (states != null)
                foreach (var s in states)
                {
                    if (s == null) continue;
                    byte[] t = null;
                    try { if (!string.IsNullOrEmpty(s.Table)) t = Convert.FromBase64String(s.Table); }
                    catch (FormatException) { t = null; }
                    Persistent[s.Key] = new ActivityStateBlock { Key = s.Key, StateHash = s.StateHash, StateName = s.State, Flags = s.Flags, Table = t };
                }
            if (timers != null)
                foreach (var tm in timers)
                    if (tm != null) Timers.Add(new SavedTimer { Name = tm.Name, Duration = tm.Duration, Elapsed = tm.Elapsed, Active = tm.Active });
        }

        // ------------------------------------------------------------------------------------------------------------
        // the table serializer

        const byte TBool = 1, TNumber = 3, TString = 4, TTable = 5;

        static bool KeyKept(in LuaValue k) => k.Type == LuaType.Boolean || k.Type == LuaType.Number || k.Type == LuaType.String;

        static bool ValueKept(in LuaValue v) => v.Type == LuaType.Boolean || v.Type == LuaType.Number || v.Type == LuaType.String || v.Type == LuaType.Table;

        /// <summary>FUN_00617A80 for the persistent map: booleans, numbers, strings and tables (userdata dropped).</summary>
        public static byte[] Serialize(LuaTable t)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                Write(w, t, 0);
                w.Flush();
                return ms.ToArray();
            }
        }

        static void Write(BinaryWriter w, LuaTable t, int depth)
        {
            if (depth < 32)
            {
                var k = LuaValue.Nil;
                while (t.Next(k, out var nk, out var v))
                {
                    k = nk;
                    if (!KeyKept(nk) || !ValueKept(v)) continue;
                    WriteValue(w, nk, depth);
                    WriteValue(w, v, depth);
                }
            }
            w.Write((byte)0);
        }

        static void WriteValue(BinaryWriter w, in LuaValue v, int depth)
        {
            switch (v.Type)
            {
                case LuaType.Boolean: w.Write(TBool); w.Write((byte)(v.IsTrue ? 1 : 0)); break;
                case LuaType.Number: w.Write(TNumber); w.Write(v.N); break;
                case LuaType.String:
                    {
                        w.Write(TString);
                        var b = Encoding.UTF8.GetBytes(v.Str ?? "");
                        w.Write((ushort)Math.Min(b.Length, ushort.MaxValue));
                        w.Write(b, 0, Math.Min(b.Length, ushort.MaxValue));
                        break;
                    }
                case LuaType.Table: w.Write(TTable); Write(w, v.AsTable, depth + 1); break;
            }
        }

        /// <summary>FUN_00617F80: the table back (null on a damaged block).</summary>
        public static LuaTable Deserialize(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data)))
                    return ReadTable(r);
            }
            catch (Exception) { return null; }
        }

        static LuaTable ReadTable(BinaryReader r)
        {
            var t = new LuaTable();
            while (true)
            {
                byte kt = r.ReadByte();
                if (kt == 0) return t;
                var k = ReadValue(r, kt);
                var v = ReadValue(r, r.ReadByte());
                if (!k.IsNil && !(k.Type == LuaType.Number && float.IsNaN(k.N))) t.Set(k, v);
            }
        }

        static LuaValue ReadValue(BinaryReader r, byte type)
        {
            switch (type)
            {
                case TBool: return LuaValue.Bool(r.ReadByte() != 0);
                case TNumber: return LuaValue.Number(r.ReadSingle());
                case TString: { int n = r.ReadUInt16(); return LuaValue.String(Encoding.UTF8.GetString(r.ReadBytes(n))); }
                case TTable: return LuaValue.Table(ReadTable(r));
                default: throw new InvalidDataException("bad state block");
            }
        }

        /// <summary>A transient block's copy of the table (userdata kept, nested tables copied once) [ours].</summary>
        public static LuaTable Copy(LuaTable t, Func<LuaValue, LuaValue> userdata = null, Dictionary<LuaTable, LuaTable> seen = null)
        {
            if (t == null) return null;
            seen ??= new Dictionary<LuaTable, LuaTable>();
            if (seen.TryGetValue(t, out var done)) return done;
            var c = new LuaTable();
            seen[t] = c;
            var k = LuaValue.Nil;
            while (t.Next(k, out var nk, out var v))
            {
                k = nk;
                if (!KeyKept(nk) || v.Type == LuaType.Function || v.IsNil) continue;
                var cv = v.Type == LuaType.Table ? LuaValue.Table(Copy(v.AsTable, userdata, seen))
                       : (v.Type == LuaType.UserData || v.Type == LuaType.LightUserData) && userdata != null ? userdata(v) : v;
                if (!cv.IsNil) c.Set(nk, cv);
            }
            return c;
        }
    }
}
