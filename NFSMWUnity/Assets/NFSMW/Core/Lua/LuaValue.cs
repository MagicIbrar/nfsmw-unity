using System;
using System.Collections.Generic;

namespace NfsMw.Core.Lua
{
    /// <summary>
    /// Lua 5.0.1 value types as speed.exe's embedded Lua has them (lobject.h). There is no thread type here: the game
    /// opens no coroutine library, so a script cannot create one (lua_resume is linked but unused by the scripts) [ours].
    /// </summary>
    public enum LuaType : byte { Nil, Boolean, LightUserData, Number, String, Table, Function, UserData }

    /// <summary>
    /// A Lua value (TObject). speed.exe's lua_Number is a 32-bit float (lundump header byte 4, test number 3.14159265e7f;
    /// the stack stores it with `fld/fstp dword`, e.g. the array __index at 0x61BF00), so numbers are kept as float and
    /// every arithmetic result is rounded to float, like the exe.
    /// </summary>
    public readonly struct LuaValue : IEquatable<LuaValue>
    {
        public readonly LuaType Type;
        public readonly float N;      // number, or 1/0 for booleans
        public readonly object O;     // string, LuaTable, LuaFunction, LuaUserData, light userdata payload

        LuaValue(LuaType t, float n, object o) { Type = t; N = n; O = o; }

        public static readonly LuaValue Nil = default;
        public static readonly LuaValue True = new LuaValue(LuaType.Boolean, 1, null);
        public static readonly LuaValue False = new LuaValue(LuaType.Boolean, 0, null);

        public static LuaValue Number(float n) => new LuaValue(LuaType.Number, n, null);
        public static LuaValue Bool(bool b) => b ? True : False;
        public static LuaValue String(string s) => s == null ? Nil : new LuaValue(LuaType.String, 0, s);
        public static LuaValue Table(LuaTable t) => t == null ? Nil : new LuaValue(LuaType.Table, 0, t);
        public static LuaValue Function(LuaFunction f) => f == null ? Nil : new LuaValue(LuaType.Function, 0, f);
        public static LuaValue UserData(LuaUserData u) => u == null ? Nil : new LuaValue(LuaType.UserData, 0, u);
        public static LuaValue Light(object p) => p == null ? Nil : new LuaValue(LuaType.LightUserData, 0, p);

        public static implicit operator LuaValue(float n) => Number(n);
        public static implicit operator LuaValue(int n) => Number(n);
        public static implicit operator LuaValue(bool b) => Bool(b);
        public static implicit operator LuaValue(string s) => String(s);
        public static implicit operator LuaValue(LuaTable t) => Table(t);
        public static implicit operator LuaValue(LuaFunction f) => Function(f);
        public static implicit operator LuaValue(LuaUserData u) => UserData(u);

        public bool IsNil => Type == LuaType.Nil;
        /// <summary>l_isfalse: nil or false.</summary>
        public bool IsFalse => Type == LuaType.Nil || (Type == LuaType.Boolean && N == 0);
        public bool IsTrue => !IsFalse;
        public bool IsNumber => Type == LuaType.Number;
        public bool IsString => Type == LuaType.String;
        public string Str => O as string;
        public LuaTable AsTable => O as LuaTable;
        public LuaFunction AsFunction => O as LuaFunction;
        public LuaUserData AsUserData => O as LuaUserData;

        /// <summary>Raw equality (luaO_rawequalObj): no metamethods.</summary>
        public bool Equals(LuaValue o)
        {
            if (Type != o.Type) return false;
            switch (Type)
            {
                case LuaType.Nil: return true;
                case LuaType.Number:
                case LuaType.Boolean: return N == o.N;
                case LuaType.String: return string.Equals((string)O, (string)o.O, StringComparison.Ordinal);
                default: return ReferenceEquals(O, o.O);
            }
        }

        public override bool Equals(object obj) => obj is LuaValue v && Equals(v);

        public override int GetHashCode()
        {
            switch (Type)
            {
                case LuaType.Nil: return 0;
                case LuaType.Number: return N == 0 ? 0 : N.GetHashCode();
                case LuaType.Boolean: return N == 0 ? 1 : 2;
                case LuaType.String: return StringComparer.Ordinal.GetHashCode((string)O);
                default: return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(O);
            }
        }

        /// <summary>The type name luaT_typenames gives (for error messages).</summary>
        public string TypeName => TypeNames[(int)Type];
        static readonly string[] TypeNames = { "nil", "boolean", "userdata", "number", "string", "table", "function", "userdata" };

        public override string ToString()
        {
            switch (Type)
            {
                case LuaType.Nil: return "nil";
                case LuaType.Boolean: return N != 0 ? "true" : "false";
                case LuaType.Number: return LuaNumber.ToString(N);
                case LuaType.String: return (string)O;
                case LuaType.UserData: return ((LuaUserData)O).ToString();
                default: return $"{TypeName}: {O}";
            }
        }
    }

    /// <summary>Full userdata: a host object with a metatable (luaS_newudata + AttachMetatable 0x6189D0).</summary>
    public sealed class LuaUserData
    {
        public readonly object Value;
        public LuaTable Metatable;
        public LuaUserData(object value, LuaTable mt) { Value = value; Metatable = mt; }
        public override string ToString() => Value?.ToString() ?? "userdata";
    }

    /// <summary>A Lua error (luaG_runerror / luaG_errormsg); the message carries the "chunk:line:" prefix like the exe's.</summary>
    public sealed class LuaException : Exception
    {
        public readonly LuaValue Value;
        public LuaException(string msg) : base(msg) { Value = LuaValue.String(msg); }
        public LuaException(LuaValue v) : base(v.ToString()) { Value = v; }
    }

    /// <summary>
    /// A Lua 5.0 table: an array part for the keys 1..n and a hash part (ltable.c). The exact node layout of the C
    /// implementation is not kept; only `next` order could differ and no game script iterates a table [ours].
    /// </summary>
    public sealed class LuaTable
    {
        readonly List<LuaValue> _arr = new List<LuaValue>();
        readonly Dictionary<LuaValue, LuaValue> _hash = new Dictionary<LuaValue, LuaValue>();
        readonly List<LuaValue> _order = new List<LuaValue>();   // hash keys in insertion order, for Next
        public LuaTable Metatable;

        public LuaTable() { }
        public LuaTable(int narray, int nhash) { if (narray > 0) _arr.Capacity = narray; }

        public int ArrayCount => _arr.Count;
        public int HashCount => _hash.Count;

        static bool ArrayIndex(in LuaValue k, out int i)
        {
            i = 0;
            if (k.Type != LuaType.Number) return false;
            float n = k.N;
            if (n < 1 || n > int.MaxValue || n != (float)Math.Floor(n)) return false;
            i = (int)n;
            return true;
        }

        public LuaValue Get(LuaValue k)
        {
            if (ArrayIndex(k, out int i) && i <= _arr.Count) return _arr[i - 1];
            if (k.Type == LuaType.Nil) return LuaValue.Nil;
            return _hash.TryGetValue(k, out var v) ? v : LuaValue.Nil;
        }

        public LuaValue Get(string k) => _hash.TryGetValue(LuaValue.String(k), out var v) ? v : LuaValue.Nil;
        public LuaValue Get(int i) => i >= 1 && i <= _arr.Count ? _arr[i - 1] : Get(LuaValue.Number(i));

        /// <summary>Raw set (luaH_set + setobj); errors like the exe for nil/NaN keys.</summary>
        public void Set(LuaValue k, LuaValue v)
        {
            if (k.Type == LuaType.Nil) throw new LuaException("table index is nil");
            if (k.Type == LuaType.Number && float.IsNaN(k.N)) throw new LuaException("table index is NaN");
            if (ArrayIndex(k, out int i))
            {
                if (i <= _arr.Count)
                {
                    _arr[i - 1] = v;
                    if (v.IsNil && i == _arr.Count) TrimArray();
                    return;
                }
                if (i == _arr.Count + 1 && !v.IsNil)
                {
                    if (_hash.Remove(k)) _order.Remove(k);
                    _arr.Add(v);
                    // pull the following integer keys out of the hash part
                    for (var nk = LuaValue.Number(_arr.Count + 1); _hash.TryGetValue(nk, out var nv); nk = LuaValue.Number(_arr.Count + 1))
                    {
                        _hash.Remove(nk);
                        _order.Remove(nk);
                        _arr.Add(nv);
                    }
                    return;
                }
            }
            if (v.IsNil)
            {
                if (_hash.Remove(k)) _order.Remove(k);
                return;
            }
            if (!_hash.ContainsKey(k)) _order.Add(k);
            _hash[k] = v;
        }

        public void Set(string k, LuaValue v) => Set(LuaValue.String(k), v);
        public void Set(int i, LuaValue v) => Set(LuaValue.Number(i), v);

        void TrimArray()
        {
            while (_arr.Count > 0 && _arr[_arr.Count - 1].IsNil) _arr.RemoveAt(_arr.Count - 1);
        }

        /// <summary>luaL_getn without an "n" field: the border of the array part.</summary>
        public int Length
        {
            get
            {
                var n = Get("n");
                if (n.IsNumber) return (int)n.N;
                return _arr.Count;
            }
        }

        /// <summary>luaH_next: the key after `k` (nil = first), false at the end.</summary>
        public bool Next(LuaValue k, out LuaValue nextKey, out LuaValue nextValue)
        {
            int start = 0;
            if (!k.IsNil)
            {
                if (ArrayIndex(k, out int i) && i <= _arr.Count) start = i;
                else
                {
                    int at = _order.IndexOf(k);
                    if (at < 0) throw new LuaException("invalid key for `next'");
                    start = _arr.Count + at + 1;
                }
            }
            for (int j = start; j < _arr.Count; j++)
                if (!_arr[j].IsNil) { nextKey = LuaValue.Number(j + 1); nextValue = _arr[j]; return true; }
            for (int j = Math.Max(0, start - _arr.Count); j < _order.Count; j++)
            {
                nextKey = _order[j];
                nextValue = _hash[nextKey];
                return true;
            }
            nextKey = nextValue = LuaValue.Nil;
            return false;
        }

        /// <summary>The array part's values 1..n (Table.Randomize shuffles them in place).</summary>
        public List<LuaValue> ArrayPart => _arr;
    }
}
