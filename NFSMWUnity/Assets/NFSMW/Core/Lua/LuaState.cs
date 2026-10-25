using System;
using System.Collections.Generic;
using System.Text;

namespace NfsMw.Core.Lua
{
    /// <summary>
    /// A Lua 5.0.1 virtual machine in C#, following lvm.c/ldo.c of the reference implementation (MIT) as linked into
    /// speed.exe ("Lua 5.0.1" at 0x8A3EC8; _luaV_execute 0x60E9D0, _luaD_precall 0x60E7D0, _luaV_gettable 0x60BF20,
    /// _luaV_settable 0x60BF90, _Arith 0x60C200, _luaV_concat 0x6128D0, _luaV_lessthan 0x608230, _luaV_equalval 0x608360).
    ///
    /// Instruction layout (5.0): OP bits 0-5, C bits 6-14, B bits 15-23, A bits 24-31; Bx = bits 6-23, sBx = Bx − 131071;
    /// an RK operand ≥ 250 (MAXSTACK) names constant K[x − 250]. 35 opcodes (MOVE … CLOSURE). Numbers are float32.
    ///
    /// The game opens no standard library (no base/string/table/math/coroutine names are in the exe): globals are only
    /// the EA bindings (LuaBindery::BindToGameCode 0x61E750), the per-dispatch `ChangeState`, and E* event constructors
    /// created on first use by the globals' __index (LuaRuntime::HandleGlobalIndex 0x61B970).
    /// </summary>
    public sealed class LuaState
    {
        public LuaValue[] Stack = new LuaValue[256];
        public int Top;
        public readonly LuaTable Globals = new LuaTable();
        LuaUpValue _openUp;          // sorted by stack index, highest first
        int _cDepth;
        const int MaxCCalls = 200;   // LUA_MAXCCALLS
        const int MaxStack = 250;    // MAXSTACK (RK split)
        const int MaxTagLoop = 100;  // MAXTAGLOOP

        /// <summary>Counts executed instructions (for the harness) [ours].</summary>
        public long Instructions;
        /// <summary>Opcodes executed, by opcode number (for the harness coverage report) [ours].</summary>
        public readonly long[] OpCounts = new long[35];

        // the current Lua frame, for error positions
        LuaClosure _curCl;
        int _curPc;

        public static readonly string[] OpNames =
            "MOVE LOADK LOADBOOL LOADNIL GETUPVAL GETGLOBAL GETTABLE SETGLOBAL SETUPVAL SETTABLE NEWTABLE SELF ADD SUB MUL DIV POW UNM NOT CONCAT JMP EQ LT LE TEST CALL TAILCALL RETURN FORLOOP TFORLOOP TFORPREP SETLIST SETLISTO CLOSE CLOSURE".Split(' ');

        // ---------------------------------------------------------------------------------------------------------
        // public API

        /// <summary>lua_load of a precompiled chunk: the main function as a closure over the globals.</summary>
        public LuaClosure Load(LuaProto p) => new LuaClosure(p, Globals);

        public LuaClosure Load(byte[] chunk, string name) => Load(LuaUndump.Load(chunk, name));

        /// <summary>lua_call: calls f with the arguments, returns every result.</summary>
        public LuaValue[] Call(LuaValue f, params LuaValue[] args)
        {
            int fi = Top;
            Ensure(fi + args.Length + 1);
            Stack[fi] = f;
            for (int i = 0; i < args.Length; i++) Stack[fi + 1 + i] = args[i];
            Top = fi + 1 + args.Length;
            int savedTop = fi;
            try
            {
                CallAt(fi, args.Length, -1);
                int n = Top - fi;
                var r = new LuaValue[n];
                Array.Copy(Stack, fi, r, 0, n);
                return r;
            }
            finally
            {
                CloseUpvalues(savedTop);
                for (int i = savedTop; i < Top && i < Stack.Length; i++) Stack[i] = LuaValue.Nil;
                Top = savedTop;
            }
        }

        public void Register(string name, LuaFunction f) => Globals.Set(name, LuaValue.Function(f));

        /// <summary>A global table of functions (luaL_openlib), created if missing.</summary>
        public LuaTable Library(string name)
        {
            var v = Globals.Get(name);
            if (v.Type == LuaType.Table) return v.AsTable;
            var t = new LuaTable();
            Globals.Set(name, t);
            return t;
        }

        public void Push(LuaValue v)
        {
            Ensure(Top + 1);
            Stack[Top++] = v;
        }

        // ---------------------------------------------------------------------------------------------------------
        // conversions

        /// <summary>luaV_tonumber: a number, or a string that converts (luaO_str2d).</summary>
        public static bool ToNumber(in LuaValue v, out float n)
        {
            if (v.Type == LuaType.Number) { n = v.N; return true; }
            if (v.Type == LuaType.String && LuaNumber.TryParse(v.Str, out n)) return true;
            n = 0;
            return false;
        }

        /// <summary>luaV_tostring: strings, and numbers converted with "%.14g".</summary>
        static bool ToStr(in LuaValue v, out string s)
        {
            if (v.Type == LuaType.String) { s = v.Str; return true; }
            if (v.Type == LuaType.Number) { s = LuaNumber.ToString(v.N); return true; }
            s = null;
            return false;
        }

        // ---------------------------------------------------------------------------------------------------------
        // errors

        /// <summary>luaG_runerror: the message with the current chunk and line (luaO_chunkid + currentline).</summary>
        public LuaException Error(string msg)
        {
            if (_curCl != null)
            {
                // the PC chunks carry no line info (stripped), so the instruction index is added [ours]
                int line = _curCl.P.LineAt(_curPc - 1);
                msg = line > 0 ? $"{ChunkId(_curCl.P.Source)}:{line}: {msg}" : $"{ChunkId(_curCl.P.Source)}:pc{_curPc - 1}: {msg}";
            }
            return new LuaException(msg);
        }

        static string ChunkId(string source)
        {
            if (string.IsNullOrEmpty(source)) return "?";
            if (source[0] == '=' || source[0] == '@') return source.Substring(1);
            string first = source.Split('\n')[0];
            return $"[string \"{(first.Length > 40 ? first.Substring(0, 40) + "..." : first)}\"]";
        }

        LuaException TypeError(in LuaValue v, string op) => Error($"attempt to {op} a {v.TypeName} value");

        // ---------------------------------------------------------------------------------------------------------
        // metatables / tag methods

        static LuaTable MetatableOf(in LuaValue v)
        {
            switch (v.Type)
            {
                case LuaType.Table: return ((LuaTable)v.O).Metatable;
                case LuaType.UserData: return ((LuaUserData)v.O).Metatable;
                default: return null;
            }
        }

        static LuaValue TM(in LuaValue v, string ev)
        {
            var mt = MetatableOf(v);
            return mt == null ? LuaValue.Nil : mt.Get(ev);
        }

        LuaValue CallTMRes(LuaValue f, LuaValue a, LuaValue b)
        {
            int fi = Top;
            Ensure(fi + 3);
            Stack[fi] = f; Stack[fi + 1] = a; Stack[fi + 2] = b;
            Top = fi + 3;
            CallAt(fi, 2, 1);
            var r = Stack[fi];
            Top = fi;
            return r;
        }

        void CallTM(LuaValue f, LuaValue a, LuaValue b, LuaValue c)
        {
            int fi = Top;
            Ensure(fi + 4);
            Stack[fi] = f; Stack[fi + 1] = a; Stack[fi + 2] = b; Stack[fi + 3] = c;
            Top = fi + 4;
            CallAt(fi, 3, 0);
            Top = fi;
        }

        /// <summary>luaV_gettable: t[k] with __index (functions or tables), "loop in gettable" after 100 steps.</summary>
        public LuaValue GetTable(LuaValue t, LuaValue k)
        {
            for (int loop = 0; loop < MaxTagLoop; loop++)
            {
                LuaValue tm;
                if (t.Type == LuaType.Table)
                {
                    var h = (LuaTable)t.O;
                    var v = h.Get(k);
                    if (!v.IsNil) return v;
                    if (h.Metatable == null) return v;
                    tm = h.Metatable.Get("__index");
                    if (tm.IsNil) return v;
                }
                else
                {
                    tm = TM(t, "__index");
                    if (tm.IsNil) throw TypeError(t, "index");
                }
                if (tm.Type == LuaType.Function) return CallTMRes(tm, t, k);
                t = tm;
            }
            throw Error("loop in gettable");
        }

        /// <summary>luaV_settable: t[k] = v with __newindex.</summary>
        public void SetTable(LuaValue t, LuaValue k, LuaValue v)
        {
            for (int loop = 0; loop < MaxTagLoop; loop++)
            {
                LuaValue tm;
                if (t.Type == LuaType.Table)
                {
                    var h = (LuaTable)t.O;
                    if (h.Metatable == null || !h.Get(k).IsNil || (tm = h.Metatable.Get("__newindex")).IsNil)
                    {
                        try { h.Set(k, v); }
                        catch (LuaException e) { throw Error(e.Message); }
                        return;
                    }
                }
                else
                {
                    tm = TM(t, "__newindex");
                    if (tm.IsNil) throw TypeError(t, "index");
                }
                if (tm.Type == LuaType.Function) { CallTM(tm, t, k, v); return; }
                t = tm;
            }
            throw Error("loop in settable");
        }

        /// <summary>luaV_equalval: raw equality, then __eq when both operands share it (tables / userdata).</summary>
        public bool ValuesEqual(LuaValue a, LuaValue b)
        {
            if (a.Type != b.Type) return false;
            if (a.Equals(b)) return true;
            if (a.Type != LuaType.Table && a.Type != LuaType.UserData) return false;
            var tm = CompTM(MetatableOf(a), MetatableOf(b), "__eq");
            if (tm.IsNil) return false;
            return CallTMRes(tm, a, b).IsTrue;
        }

        /// <summary>get_compTM: the metamethod only if both metatables give the same one.</summary>
        static LuaValue CompTM(LuaTable mt1, LuaTable mt2, string ev)
        {
            if (mt1 == null) return LuaValue.Nil;
            var tm1 = mt1.Get(ev);
            if (tm1.IsNil) return LuaValue.Nil;
            if (mt1 == mt2) return tm1;
            if (mt2 == null) return LuaValue.Nil;
            var tm2 = mt2.Get(ev);
            return tm1.Equals(tm2) ? tm1 : LuaValue.Nil;
        }

        bool LessThan(LuaValue l, LuaValue r)
        {
            if (l.Type == LuaType.Number && r.Type == LuaType.Number) return l.N < r.N;
            if (l.Type == LuaType.String && r.Type == LuaType.String) return string.CompareOrdinal(l.Str, r.Str) < 0;
            if (l.Type == r.Type)
            {
                var tm = CompTM(MetatableOf(l), MetatableOf(r), "__lt");
                if (!tm.IsNil) return CallTMRes(tm, l, r).IsTrue;
            }
            throw OrderError(l, r);
        }

        bool LessEqual(LuaValue l, LuaValue r)
        {
            if (l.Type == LuaType.Number && r.Type == LuaType.Number) return l.N <= r.N;
            if (l.Type == LuaType.String && r.Type == LuaType.String) return string.CompareOrdinal(l.Str, r.Str) <= 0;
            if (l.Type == r.Type)
            {
                var tm = CompTM(MetatableOf(l), MetatableOf(r), "__le");
                if (!tm.IsNil) return CallTMRes(tm, l, r).IsTrue;
                tm = CompTM(MetatableOf(l), MetatableOf(r), "__lt");
                if (!tm.IsNil) return !CallTMRes(tm, r, l).IsTrue;
            }
            throw OrderError(l, r);
        }

        LuaException OrderError(in LuaValue l, in LuaValue r)
        {
            // luaG_ordererror
            return l.TypeName == r.TypeName ? Error($"attempt to compare two {l.TypeName} values") : Error($"attempt to compare {l.TypeName} with {r.TypeName}");
        }

        static readonly string[] ArithEvents = { "__add", "__sub", "__mul", "__div", "__pow" };

        /// <summary>Arith (0x60C200): numbers (or numeric strings), else the binary metamethod; `^` calls the global __pow.</summary>
        LuaValue Arith(int op, LuaValue b, LuaValue c)
        {
            if (ToNumber(b, out float x) && ToNumber(c, out float y))
            {
                switch (op)
                {
                    case 0: return LuaValue.Number(x + y);
                    case 1: return LuaValue.Number(x - y);
                    case 2: return LuaValue.Number(x * y);
                    case 3: return LuaValue.Number(x / y);
                    default:
                        {
                            var f = Globals.Get("__pow");
                            if (f.Type != LuaType.Function) throw Error("`__pow' (`^' operator) is not a function");
                            return CallTMRes(f, b, c);
                        }
                }
            }
            var tm = TM(b, ArithEvents[op]);
            if (tm.IsNil) tm = TM(c, ArithEvents[op]);
            if (tm.IsNil)
            {
                // luaG_aritherror: blame the operand that is not a number
                var bad = ToNumber(b, out _) ? c : b;
                throw TypeError(bad, "perform arithmetic on");
            }
            return CallTMRes(tm, b, c);
        }

        // ---------------------------------------------------------------------------------------------------------
        // stack / upvalues

        void Ensure(int size)
        {
            if (size <= Stack.Length) return;
            if (size > 1 << 20) throw Error("stack overflow");
            int n = Stack.Length;
            while (n < size) n *= 2;
            Array.Resize(ref Stack, n);
        }

        LuaUpValue FindUpvalue(int index)
        {
            LuaUpValue prev = null, p = _openUp;
            while (p != null && p.Index > index) { prev = p; p = p.NextOpen; }
            if (p != null && p.Index == index) return p;
            var u = new LuaUpValue { L = this, Index = index, Open = true, NextOpen = p };
            if (prev == null) _openUp = u; else prev.NextOpen = u;
            return u;
        }

        void CloseUpvalues(int level)
        {
            while (_openUp != null && _openUp.Index >= level)
            {
                var u = _openUp;
                _openUp = u.NextOpen;
                u.Close();
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // calls

        /// <summary>
        /// luaD_call: the function at `fi` with `nargs` arguments after it; the results end up at fi.. (nresults ≥ 0:
        /// padded/truncated; −1 = LUA_MULTRET, Top = fi + count).
        /// </summary>
        public void CallAt(int fi, int nargs, int nresults)
        {
            if (++_cDepth >= MaxCCalls)
            {
                _cDepth--;
                throw Error(_cDepth >= MaxCCalls + (MaxCCalls >> 3) ? "error in error handling" : "C stack overflow");
            }
            var savedCl = _curCl;
            int savedPc = _curPc;
            try
            {
                var f = Stack[fi];
                if (f.Type != LuaType.Function)
                {
                    // tryfuncTM: __call gets the object as its first argument
                    var tm = TM(f, "__call");
                    if (tm.Type != LuaType.Function) throw TypeError(f, "call");
                    Ensure(fi + nargs + 2);
                    for (int i = fi + nargs; i >= fi; i--) Stack[i + 1] = Stack[i];
                    Stack[fi] = tm;
                    nargs++;
                    f = tm;
                }
                int n;
                if (f.O is LuaClosure cl) n = CallLua(cl, fi, nargs);
                else n = CallHost((LuaCFunction)f.O, fi, nargs);
                // results are at fi..fi+n-1
                if (nresults >= 0)
                {
                    Ensure(fi + nresults);
                    for (int i = n; i < nresults; i++) Stack[fi + i] = LuaValue.Nil;
                    Top = fi + nresults;
                }
                else Top = fi + n;
            }
            finally
            {
                _cDepth--;
                _curCl = savedCl;
                _curPc = savedPc;
            }
        }

        int CallHost(LuaCFunction f, int fi, int nargs)
        {
            int b = fi + 1;
            Top = b + nargs;
            Ensure(Top + 20);
            var args = new LuaArgs(this, b, nargs);
            int n;
            if (f.Raw != null)
            {
                n = f.Raw(args);
                if (n > Top - b) n = Top - b;
            }
            else
            {
                LuaValue r;
                try { r = f.F(args); }
                catch (LuaException) { throw; }
                catch (Exception e) { throw Error($"{f.Name}: {e.GetType().Name}: {e.Message}"); }
                if (f.Void) n = 0;
                else { Stack[Top++] = r; n = 1; }
            }
            // move the results down to the function's slot (luaD_poscall)
            int src = Top - n;
            for (int i = 0; i < n; i++) Stack[fi + i] = Stack[src + i];
            return n;
        }

        int CallLua(LuaClosure cl, int fi, int nargs)
        {
            var p = cl.P;
            int b = fi + 1;
            Ensure(b + p.MaxStackSize + 20);
            int np = p.NumParams;
            for (int i = nargs; i < np; i++) Stack[b + i] = LuaValue.Nil;
            int filled = np;
            if (p.IsVararg != 0)
            {
                // adjust_varargs (5.0): the extra arguments go into the table `arg` {…, n = count} after the fixed ones
                int extra = Math.Max(0, nargs - np);
                var t = new LuaTable(extra, 1);
                for (int i = 0; i < extra; i++) t.Set(i + 1, Stack[b + np + i]);
                t.Set("n", LuaValue.Number(extra));
                Stack[b + np] = LuaValue.Table(t);
                filled = np + 1;
            }
            for (int i = filled; i < p.MaxStackSize + 1; i++) Stack[b + i] = LuaValue.Nil;
            Top = b + p.MaxStackSize;
            return Execute(cl, b);
        }

        // ---------------------------------------------------------------------------------------------------------
        // the interpreter (luaV_execute)

        int Execute(LuaClosure cl, int b)
        {
            var p = cl.P;
            var code = p.Code;
            var k = p.K;
            int pc = 0;
            int frameTop = b + p.MaxStackSize;
            _curCl = cl;
            while (true)
            {
                if (pc >= code.Length) throw Error("bad code");
                uint i = code[pc++];
                _curPc = pc;
                Instructions++;
                int op = (int)(i & 0x3F);
                int a = (int)(i >> 24);
                int c = (int)((i >> 6) & 0x1FF);
                int bb = (int)((i >> 15) & 0x1FF);
                int bx = (int)((i >> 6) & 0x3FFFF);
                int sbx = bx - 131071;
                int ra = b + a;
                if (op < OpCounts.Length) OpCounts[op]++;
                switch (op)
                {
                    case 0: // MOVE
                        Stack[ra] = Stack[b + bb];
                        break;
                    case 1: // LOADK
                        Stack[ra] = k[bx];
                        break;
                    case 2: // LOADBOOL
                        Stack[ra] = LuaValue.Bool(bb != 0);
                        if (c != 0) pc++;
                        break;
                    case 3: // LOADNIL
                        for (int r = a; r <= bb; r++) Stack[b + r] = LuaValue.Nil;
                        break;
                    case 4: // GETUPVAL
                        Stack[ra] = cl.Up[bb].Value;
                        break;
                    case 5: // GETGLOBAL
                        {
                            var v = GetTable(LuaValue.Table(cl.Env), k[bx]);
                            Stack[b + a] = v;
                            break;
                        }
                    case 6: // GETTABLE
                        {
                            var v = GetTable(Stack[b + bb], RK(c, b, k));
                            Stack[b + a] = v;
                            break;
                        }
                    case 7: // SETGLOBAL
                        SetTable(LuaValue.Table(cl.Env), k[bx], Stack[ra]);
                        break;
                    case 8: // SETUPVAL
                        cl.Up[bb].Value = Stack[ra];
                        break;
                    case 9: // SETTABLE
                        SetTable(Stack[ra], RK(bb, b, k), RK(c, b, k));
                        break;
                    case 10: // NEWTABLE (B = array size as a "floating point byte", C = log2 hash size)
                        Stack[ra] = LuaValue.Table(new LuaTable(FbToInt(bb), c == 0 ? 0 : 1 << c));
                        break;
                    case 11: // SELF
                        {
                            var o = Stack[b + bb];
                            Stack[b + a + 1] = o;
                            var v = GetTable(o, RK(c, b, k));
                            Stack[b + a] = v;
                            break;
                        }
                    case 12: // ADD
                    case 13: // SUB
                    case 14: // MUL
                    case 15: // DIV
                    case 16: // POW
                        {
                            var x = RK(bb, b, k);
                            var y = RK(c, b, k);
                            LuaValue r;
                            if (x.Type == LuaType.Number && y.Type == LuaType.Number && op != 16)
                                r = op == 12 ? LuaValue.Number(x.N + y.N) : op == 13 ? LuaValue.Number(x.N - y.N) : op == 14 ? LuaValue.Number(x.N * y.N) : LuaValue.Number(x.N / y.N);
                            else r = Arith(op - 12, x, y);
                            Stack[b + a] = r;
                            break;
                        }
                    case 17: // UNM
                        {
                            var x = Stack[b + bb];
                            LuaValue r;
                            if (ToNumber(x, out float n)) r = LuaValue.Number(-n);
                            else
                            {
                                var tm = TM(x, "__unm");
                                if (tm.IsNil) throw TypeError(x, "perform arithmetic on");
                                r = CallTMRes(tm, x, LuaValue.Nil);
                            }
                            Stack[b + a] = r;
                            break;
                        }
                    case 18: // NOT
                        Stack[ra] = LuaValue.Bool(Stack[b + bb].IsFalse);
                        break;
                    case 19: // CONCAT
                        {
                            Top = b + c + 1;
                            Concat(c - bb + 1, b + c);
                            Stack[b + a] = Stack[b + bb];
                            Top = frameTop;
                            break;
                        }
                    case 20: // JMP
                        pc += sbx;
                        break;
                    case 21: // EQ
                    case 22: // LT
                    case 23: // LE
                        {
                            var x = RK(bb, b, k);
                            var y = RK(c, b, k);
                            bool r = op == 21 ? ValuesEqual(x, y) : op == 22 ? LessThan(x, y) : LessEqual(x, y);
                            if (r != (a != 0)) pc++;
                            else pc += SBx(code[pc]) + 1;   // dojump on the following JMP
                            break;
                        }
                    case 24: // TEST: if l_isfalse(R(B)) == C then skip, else R(A) = R(B) and take the following jump
                        {
                            var x = Stack[b + bb];
                            if ((x.IsFalse ? 1 : 0) == c) pc++;
                            else
                            {
                                Stack[ra] = x;
                                pc += SBx(code[pc]) + 1;
                            }
                            break;
                        }
                    case 25: // CALL
                        {
                            int nres = c - 1;
                            if (bb != 0) Top = ra + bb;
                            CallAt(ra, Top - ra - 1, nres);
                            _curCl = cl;
                            if (nres >= 0) Top = frameTop;
                            break;
                        }
                    case 26: // TAILCALL: close this frame's upvalues, call, return everything
                        {
                            if (bb != 0) Top = ra + bb;
                            CloseUpvalues(b);
                            CallAt(ra, Top - ra - 1, -1);
                            _curCl = cl;
                            int n = Top - ra;
                            for (int j = 0; j < n; j++) Stack[b - 1 + j] = Stack[ra + j];
                            return n;
                        }
                    case 27: // RETURN
                        {
                            if (bb != 0) Top = ra + bb - 1;
                            CloseUpvalues(b);
                            int n = Top - ra;
                            for (int j = 0; j < n; j++) Stack[b - 1 + j] = Stack[ra + j];
                            return n;
                        }
                    case 28: // FORLOOP (5.0: the loop variable is R(A) itself)
                        {
                            if (Stack[ra].Type != LuaType.Number) throw Error("`for' initial value must be a number");
                            if (!ToNumber(Stack[ra + 1], out float limit)) throw Error("`for' limit must be a number");
                            if (!ToNumber(Stack[ra + 2], out float step)) throw Error("`for' step must be a number");
                            Stack[ra + 1] = LuaValue.Number(limit);
                            Stack[ra + 2] = LuaValue.Number(step);
                            float idx = Stack[ra].N + step;
                            if (step > 0 ? idx <= limit : idx >= limit)
                            {
                                pc += sbx;
                                Stack[ra] = LuaValue.Number(idx);
                            }
                            break;
                        }
                    case 29: // TFORLOOP
                        {
                            int nvar = c + 1;
                            int cb = ra + nvar + 2;
                            Ensure(cb + 3 + nvar);
                            Stack[cb] = Stack[ra];
                            Stack[cb + 1] = Stack[ra + 1];
                            Stack[cb + 2] = Stack[ra + 2];
                            Top = cb + 3;
                            CallAt(cb, 2, nvar);
                            _curCl = cl;
                            Top = frameTop;
                            int dst = ra + 2;
                            for (int j = nvar - 1; j >= 0; j--) Stack[dst + j] = Stack[cb + j];
                            if (Stack[dst].IsNil) pc++;
                            else pc += SBx(code[pc]) + 1;
                            break;
                        }
                    case 30: // TFORPREP (compatibility: a table iterates with the global `next`)
                        {
                            if (Stack[ra].Type == LuaType.Table)
                            {
                                Stack[ra + 1] = Stack[ra];
                                Stack[ra] = Globals.Get("next");
                            }
                            pc += sbx;
                            break;
                        }
                    case 31: // SETLIST
                    case 32: // SETLISTO
                        {
                            if (Stack[ra].Type != LuaType.Table) throw Error("SETLIST on a non-table");
                            var h = (LuaTable)Stack[ra].O;
                            int n;
                            if (op == 31) n = (bx & 31) + 1;          // LFIELDS_PER_FLUSH = 32
                            else { n = Top - ra - 1; Top = frameTop; }
                            int bc = bx & ~31;
                            for (; n > 0; n--) h.Set(bc + n, Stack[ra + n]);
                            break;
                        }
                    case 33: // CLOSE
                        CloseUpvalues(ra);
                        break;
                    case 34: // CLOSURE
                        {
                            var np = p.P[bx];
                            var ncl = new LuaClosure(np, cl.Env);
                            for (int j = 0; j < np.NumUpvalues; j++)
                            {
                                uint u = code[pc++];
                                int ub = (int)((u >> 15) & 0x1FF);
                                if ((u & 0x3F) == 4) ncl.Up[j] = cl.Up[ub];          // GETUPVAL: share the enclosing upvalue
                                else ncl.Up[j] = FindUpvalue(b + ub);                 // MOVE: capture a local
                            }
                            Stack[ra] = LuaValue.Function(ncl);
                            break;
                        }
                    default:
                        throw Error($"bad opcode {op}");
                }
            }
        }

        LuaValue RK(int x, int b, LuaValue[] k) => x >= MaxStack ? k[x - MaxStack] : Stack[b + x];

        static int SBx(uint i) => (int)((i >> 6) & 0x3FFFF) - 131071;

        /// <summary>luaO_fb2int: the "floating point byte" eeeeexxx → (xxx|8)·2^(eeeee−1), or xxx when eeeee is 0.</summary>
        static int FbToInt(int x)
        {
            int e = (x >> 3) & 31;
            return e == 0 ? x : ((x & 7) + 8) << (e - 1);
        }

        /// <summary>luaV_concat: `total` values ending at stack index `last`, folded from the right.</summary>
        void Concat(int total, int last)
        {
            do
            {
                int top = last + 1;
                int n = 2;
                if (!ToStr(Stack[top - 2], out _) || !ToStr(Stack[top - 1], out _))
                {
                    var x = Stack[top - 2];
                    var y = Stack[top - 1];
                    var tm = TM(x, "__concat");
                    if (tm.IsNil) tm = TM(y, "__concat");
                    if (tm.IsNil) throw TypeError(ToStr(x, out _) ? y : x, "concatenate");
                    Stack[top - 2] = CallTMRes(tm, x, y);
                }
                else
                {
                    // as many strings/numbers in a row as there are
                    n = 1;
                    while (n < total && ToStr(Stack[top - n - 1], out _)) n++;
                    var sb = new StringBuilder();
                    for (int j = n; j > 0; j--)
                    {
                        ToStr(Stack[top - j], out string s);
                        sb.Append(s);
                    }
                    Stack[top - n] = LuaValue.String(sb.ToString());
                }
                total -= n - 1;
                last -= n - 1;
            } while (total > 1);
        }
    }
}
