using System;
using System.Globalization;
using System.Text;

namespace NfsMw.Core.Lua
{
    /// <summary>A Lua function prototype (Proto, lobject.h of Lua 5.0.1).</summary>
    public sealed class LuaProto
    {
        public string Source;
        public int LineDefined;
        public byte NumUpvalues, NumParams, IsVararg, MaxStackSize;
        public int[] LineInfo = Array.Empty<int>();
        public (string name, int startpc, int endpc)[] LocVars = Array.Empty<(string, int, int)>();
        public string[] UpvalueNames = Array.Empty<string>();
        public LuaValue[] K = Array.Empty<LuaValue>();
        public LuaProto[] P = Array.Empty<LuaProto>();
        public uint[] Code = Array.Empty<uint>();

        public int LineAt(int pc) => pc >= 0 && pc < LineInfo.Length ? LineInfo[pc] : 0;
    }

    public abstract class LuaFunction
    {
        public abstract string Name { get; }
    }

    /// <summary>A Lua closure (LClosure): prototype, upvalues and its environment table (cl->g).</summary>
    public sealed class LuaClosure : LuaFunction
    {
        public readonly LuaProto P;
        public readonly LuaUpValue[] Up;
        public LuaTable Env;
        public LuaClosure(LuaProto p, LuaTable env) { P = p; Up = new LuaUpValue[p.NumUpvalues]; Env = env; }
        public override string Name => P.Source ?? "?";
    }

    /// <summary>Arguments of a host function call: stack slots [Base, Base+Count).</summary>
    public readonly struct LuaArgs
    {
        public readonly LuaState L;
        public readonly int Base, Count;
        public LuaArgs(LuaState l, int b, int n) { L = l; Base = b; Count = n; }
        public LuaValue this[int i] => i >= 0 && i < Count ? L.Stack[Base + i] : LuaValue.Nil;

        /// <summary>lua_tonumber: numbers and numeric strings, else 0.</summary>
        public float Number(int i) => LuaState.ToNumber(this[i], out float n) ? n : 0f;
        public int Int(int i) => (int)Number(i);
        /// <summary>lua_toboolean.</summary>
        public bool Bool(int i) => this[i].IsTrue;
        /// <summary>lua_tostring: strings and numbers, else null.</summary>
        public string String(int i) { var v = this[i]; return v.IsString ? v.Str : v.IsNumber ? LuaNumber.ToString(v.N) : null; }
        public object UserData(int i) => this[i].Type == LuaType.UserData ? ((LuaUserData)this[i].O).Value : this[i].Type == LuaType.LightUserData ? this[i].O : null;
        public T Object<T>(int i) where T : class => UserData(i) as T;
    }

    /// <summary>
    /// A host function (CClosure). EA's binders (BindFunction / BindVoidFunction 0x61A270.., thunks like
    /// FunctionArg1Thunk 0x610D10) push one result, or none for a void binding.
    /// </summary>
    public sealed class LuaCFunction : LuaFunction
    {
        readonly string _name;
        public readonly Func<LuaArgs, LuaValue> F;
        public readonly bool Void;
        /// <summary>A raw function that pushes its own results and returns their count (BindRawFunction 0x61BA60).</summary>
        public readonly Func<LuaArgs, int> Raw;
        public LuaCFunction(string name, Func<LuaArgs, LuaValue> f, bool isVoid = false) { _name = name; F = f; Void = isVoid; }
        public LuaCFunction(string name, Func<LuaArgs, int> raw) { _name = name; Raw = raw; }
        public LuaCFunction(string name, Action<LuaArgs> f) { _name = name; F = a => { f(a); return LuaValue.Nil; }; Void = true; }
        public override string Name => _name;
    }

    /// <summary>An upvalue (UpVal): open while it points at a live stack slot, closed once the frame returns.</summary>
    public sealed class LuaUpValue
    {
        internal LuaState L;
        internal int Index;
        internal bool Open;
        internal LuaUpValue NextOpen;
        LuaValue _closed;
        public LuaValue Value
        {
            get => Open ? L.Stack[Index] : _closed;
            set { if (Open) L.Stack[Index] = value; else _closed = value; }
        }
        internal void Close() { _closed = L.Stack[Index]; Open = false; L = null; }
    }

    /// <summary>
    /// lundump.c of Lua 5.0.1 (speed.exe _LoadHeader 0x614A40, _LoadNumber 0x6144B0). Header: "\x1bLua", version 0x50,
    /// endianness 1, sizeof(int) 4, sizeof(size_t) 4, sizeof(Instruction) 4, SIZE_OP 6, SIZE_A 8, SIZE_B 9, SIZE_C 9,
    /// sizeof(lua_Number) 4, then the test number 3.14159265358979323846e7 as a float. The exe accepts only this layout
    /// ("virtual machine mismatch in %s: size of %s is %d but read %d" / "unknown number format in %s").
    /// </summary>
    public static class LuaUndump
    {
        const float TestNumber = 3.14159265358979323846e7f;

        public static LuaProto Load(byte[] b, string chunkName = "=?")
        {
            int o = 0;
            if (b.Length < 18 || b[0] != 0x1B || b[1] != (byte)'L' || b[2] != (byte)'u' || b[3] != (byte)'a')
                throw new LuaException($"bad signature in {chunkName}");
            o = 4;
            byte version = b[o++];
            if (version != 0x50) throw new LuaException($"{chunkName} version mismatch: read {version >> 4}.{version & 15}; expected 5.0");
            byte endian = b[o++];
            if (endian != 1) throw new LuaException($"big-endian chunk in {chunkName}");
            void Size(string what, int want) { int got = b[o++]; if (got != want) throw new LuaException($"virtual machine mismatch in {chunkName}: size of {what} is {want} but read {got}"); }
            Size("int", 4); Size("size_t", 4); Size("Instruction", 4);
            Size("OP", 6); Size("A", 8); Size("B", 9); Size("C", 9);
            Size("number", 4);
            float t = BitConverter.ToSingle(b, o); o += 4;
            if (t != TestNumber) throw new LuaException($"unknown number format in {chunkName}");
            var r = new Reader(b, o, chunkName);
            return r.Function(null);
        }

        sealed class Reader
        {
            readonly byte[] _b;
            int _o;
            readonly string _name;
            public Reader(byte[] b, int o, string name) { _b = b; _o = o; _name = name; }

            void Need(int n) { if (_o + n > _b.Length) throw new LuaException($"unexpected end of file in {_name}"); }
            byte U8() { Need(1); return _b[_o++]; }
            int I32() { Need(4); int v = BitConverter.ToInt32(_b, _o); _o += 4; return v; }
            uint U32() { Need(4); uint v = BitConverter.ToUInt32(_b, _o); _o += 4; return v; }
            float F32() { Need(4); float v = BitConverter.ToSingle(_b, _o); _o += 4; return v; }
            int Count() { int n = I32(); if (n < 0) throw new LuaException($"bad integer in {_name}"); return n; }

            /// <summary>LoadString: size_t length including the NUL (0 = NULL string).</summary>
            string Str()
            {
                int n = (int)U32();
                if (n == 0) return null;
                Need(n);
                var sb = new StringBuilder(n - 1);
                for (int i = 0; i < n - 1; i++) sb.Append((char)_b[_o + i]);   // Latin-1
                _o += n;
                return sb.ToString();
            }

            public LuaProto Function(string parentSource)
            {
                var f = new LuaProto();
                f.Source = Str() ?? parentSource;
                f.LineDefined = I32();
                f.NumUpvalues = U8();
                f.NumParams = U8();
                f.IsVararg = U8();
                f.MaxStackSize = U8();
                int n = Count();
                f.LineInfo = new int[n];
                for (int i = 0; i < n; i++) f.LineInfo[i] = I32();
                n = Count();
                f.LocVars = new (string, int, int)[n];
                for (int i = 0; i < n; i++) f.LocVars[i] = (Str(), I32(), I32());
                n = Count();
                if (n != 0 && n != f.NumUpvalues) throw new LuaException($"bad nupvalues in {_name}: read {n}; expected {f.NumUpvalues}");
                f.UpvalueNames = new string[n];
                for (int i = 0; i < n; i++) f.UpvalueNames[i] = Str();
                n = Count();
                f.K = new LuaValue[n];
                for (int i = 0; i < n; i++)
                {
                    byte t = U8();
                    switch (t)
                    {
                        case 3: f.K[i] = LuaValue.Number(F32()); break;     // LUA_TNUMBER
                        case 4: f.K[i] = LuaValue.String(Str() ?? ""); break; // LUA_TSTRING
                        case 0: f.K[i] = LuaValue.Nil; break;                // LUA_TNIL
                        default: throw new LuaException($"bad constant type ({t}) in {_name}");
                    }
                }
                n = Count();
                f.P = new LuaProto[n];
                for (int i = 0; i < n; i++) f.P[i] = Function(f.Source);
                n = Count();
                f.Code = new uint[n];
                for (int i = 0; i < n; i++) f.Code[i] = U32();
                return f;
            }
        }
    }

    /// <summary>
    /// Number ↔ string as the exe does it: lua_number2str is sprintf("%.14g") on the float promoted to double (the format
    /// string sits at 0x8A461C) with the MSVC runtime's 3-digit exponents; lua_str2number is strtod.
    /// </summary>
    public static class LuaNumber
    {
        public static string ToString(float f)
        {
            double d = f;
            if (double.IsNaN(d)) return "-1.#IND";          // MSVC printf [?]
            if (double.IsPositiveInfinity(d)) return "1.#INF";
            if (double.IsNegativeInfinity(d)) return "-1.#INF";
            if (d == 0) return (BitConverter.DoubleToInt64Bits(d) < 0) ? "-0" : "0";
            string e = d.ToString("E13", CultureInfo.InvariantCulture);   // d.ddddddddddddde+XXX (14 significant digits)
            int ep = e.IndexOf('E');
            int x = int.Parse(e.Substring(ep + 1), CultureInfo.InvariantCulture);
            if (x < -4 || x >= 14)
            {
                string m = e.Substring(0, ep);
                if (m.Contains(".")) m = m.TrimEnd('0').TrimEnd('.');
                return m + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("000", CultureInfo.InvariantCulture);
            }
            string s = d.ToString("F" + (13 - x), CultureInfo.InvariantCulture);
            if (s.Contains(".")) s = s.TrimEnd('0').TrimEnd('.');
            return s;
        }

        /// <summary>luaO_str2d: the whole string (bar surrounding blanks) must be a number.</summary>
        public static bool TryParse(string s, out float n)
        {
            n = 0;
            if (s == null) return false;
            const NumberStyles st = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign |
                                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
            if (!double.TryParse(s, st, CultureInfo.InvariantCulture, out double d)) return false;
            n = (float)d;
            return true;
        }
    }
}
