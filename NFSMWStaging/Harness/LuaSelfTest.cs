using System;
using System.Collections.Generic;
using NfsMw.Core.Lua;

// `lua selftest`: hand-assembled Lua 5.0 functions for the opcodes and semantics gameplay.bin does not exercise
// (MUL, DIV, POW's missing __pow, NOT, SETLIST, CLOSE, varargs, TFORLOOP, TAILCALL, string coercion, %.14g, float32).
static class LuaSelfTest
{
    const int MOVE = 0, LOADK = 1, LOADBOOL = 2, LOADNIL = 3, GETUPVAL = 4, GETGLOBAL = 5, GETTABLE = 6, SETGLOBAL = 7, SETUPVAL = 8,
        SETTABLE = 9, NEWTABLE = 10, SELF = 11, ADD = 12, SUB = 13, MUL = 14, DIV = 15, POW = 16, UNM = 17, NOT = 18, CONCAT = 19,
        JMP = 20, EQ = 21, LT = 22, LE = 23, TEST = 24, CALL = 25, TAILCALL = 26, RETURN = 27, FORLOOP = 28, TFORLOOP = 29,
        TFORPREP = 30, SETLIST = 31, SETLISTO = 32, CLOSE = 33, CLOSURE = 34;

    static uint ABC(int op, int a, int b, int c) => (uint)op | ((uint)c << 6) | ((uint)b << 15) | ((uint)a << 24);
    static uint ABx(int op, int a, int bx) => (uint)op | ((uint)bx << 6) | ((uint)a << 24);
    static uint AsBx(int op, int a, int sbx) => ABx(op, a, sbx + 131071);
    static int K(int i) => 250 + i;

    static LuaProto P(int stack, LuaValue[] k, uint[] code, int nparams = 0, int ups = 0, int vararg = 0, params LuaProto[] protos) =>
        new LuaProto { Source = "=selftest", MaxStackSize = (byte)stack, K = k, Code = code, NumParams = (byte)nparams, NumUpvalues = (byte)ups, IsVararg = (byte)vararg, P = protos };

    static int _fail, _pass;

    static void Check(string name, Func<LuaValue[]> run, string expect)
    {
        string got;
        try { got = string.Join(",", Array.ConvertAll(run(), v => v.Type == LuaType.String ? "'" + v.Str + "'" : v.ToString())); }
        catch (LuaException e) { got = "error: " + e.Message; }
        bool ok = got == expect;
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}: {got}{(ok ? "" : "   (expected " + expect + ")")}");
    }

    public static void Run()
    {
        var L = new LuaState();
        LuaValue[] Go(LuaProto p, params LuaValue[] args) => L.Call(LuaValue.Function(L.Load(p)), args);

        // arithmetic: (7 * 3) / 2, -x, not
        Check("MUL DIV UNM NOT", () => Go(P(4, new LuaValue[] { 7, 3, 2 }, new[]
        {
            ABx(LOADK, 0, 0), ABx(LOADK, 1, 1), ABC(MUL, 0, 0, 1), ABC(DIV, 0, 0, K(2)), ABC(UNM, 1, 0, 0), ABC(NOT, 2, 0, 0), ABC(RETURN, 0, 4, 0),
        })), "10.5,-10.5,false");
        // float32 numbers: 16777216 + 1 == 16777216, 0.1 prints as the float it is
        Check("float32", () => Go(P(3, new LuaValue[] { 16777216f, 1, "x", 0.1f }, new[]
        {
            ABx(LOADK, 0, 0), ABC(ADD, 0, 0, K(1)), ABx(LOADK, 1, 2), ABx(LOADK, 2, 3), ABC(CONCAT, 1, 1, 2), ABC(RETURN, 0, 3, 0),
        })), "16777216,'x0.10000000149012'");
        // %.14g with MSVC exponents, string → number coercion in arithmetic
        Check("number format", () => Go(P(4, new LuaValue[] { 1e20f, "", 3f, "10", 5 }, new[]
        {
            ABx(LOADK, 0, 1), ABx(LOADK, 1, 0), ABx(LOADK, 2, 2), ABC(CONCAT, 0, 0, 2), ABx(LOADK, 1, 3), ABC(ADD, 1, 1, K(4)), ABC(RETURN, 0, 3, 0),
        })), "'1.0000000200409e+0203',15");   // 1e20f is 100000002004087734272
        // `^` needs the global __pow (no math library in the game)
        Check("POW without __pow", () => Go(P(2, new LuaValue[] { 2, 3 }, new[] { ABx(LOADK, 0, 0), ABC(POW, 0, 0, K(1)), ABC(RETURN, 0, 2, 0) })),
            "error: selftest:pc1: `__pow' (`^' operator) is not a function");
        // SETLIST: {10, 20, 30}[2]
        Check("NEWTABLE SETLIST", () => Go(P(5, new LuaValue[] { 10, 20, 30, 2 }, new[]
        {
            ABC(NEWTABLE, 0, 3, 0), ABx(LOADK, 1, 0), ABx(LOADK, 2, 1), ABx(LOADK, 3, 2), ABx(SETLIST, 0, 2), ABC(GETTABLE, 1, 0, K(3)), ABC(RETURN, 1, 2, 0),
        })), "20");
        // closures sharing an upvalue: local x = 0; local f = function() x = x + 1 return x end; f(); return f()
        var inc = P(2, new LuaValue[] { 1 }, new[] { ABC(GETUPVAL, 0, 0, 0), ABC(ADD, 0, 0, K(0)), ABC(SETUPVAL, 0, 0, 0), ABC(RETURN, 0, 2, 0) }, ups: 1);
        Check("CLOSURE GETUPVAL SETUPVAL", () => Go(P(4, new LuaValue[] { 0 }, new[]
        {
            ABx(LOADK, 0, 0), ABx(CLOSURE, 1, 0), ABC(MOVE, 0, 0, 0), ABC(MOVE, 2, 1, 0), ABC(CALL, 2, 1, 1), ABC(MOVE, 2, 1, 0), ABC(CALL, 2, 1, 2), ABC(RETURN, 2, 2, 0),
        }, protos: inc)), "2");
        // CLOSE: a closure made in a loop body keeps the value of its own iteration
        var get = P(1, new LuaValue[0], new[] { ABC(GETUPVAL, 0, 0, 0), ABC(RETURN, 0, 2, 0) }, ups: 1);
        Check("CLOSE", () => Go(P(4, new LuaValue[] { 5, 7 }, new[]
        {
            ABx(LOADK, 0, 0), ABx(CLOSURE, 1, 0), ABC(MOVE, 0, 0, 0), ABC(CLOSE, 0, 0, 0), ABx(LOADK, 0, 1), ABC(MOVE, 2, 1, 0), ABC(CALL, 2, 1, 2), ABC(MOVE, 1, 2, 0), ABC(RETURN, 0, 3, 0),
        }, protos: get)), "7,5");
        // varargs: function(...) return arg.n, arg[2] end (1, 2, 3)
        Check("vararg arg table", () => Go(P(3, new LuaValue[] { "n", 2 }, new[]
        {
            ABC(GETTABLE, 1, 0, K(0)), ABC(GETTABLE, 2, 0, K(1)), ABC(RETURN, 1, 3, 0),
        }, vararg: 1), 1, 2, 3), "3,2");
        // numeric for: s = 0; for i = 1, 4 do s = s + i end
        Check("FORLOOP", () => Go(P(5, new LuaValue[] { 0, 1, 4 }, new[]
        {
            ABx(LOADK, 0, 0), ABx(LOADK, 1, 1), ABx(LOADK, 2, 2), ABx(LOADK, 3, 1), ABC(SUB, 1, 1, 3), AsBx(JMP, 0, 1),
            ABC(ADD, 0, 0, 1), AsBx(FORLOOP, 1, -2), ABC(RETURN, 0, 2, 0),
        })), "10");
        // generic for over a host iterator: for k, v in it, nil, 0 do s = s + v end (v = 10, 20, 30)
        L.Register("it", new LuaCFunction("it", (Func<LuaArgs, int>)(a =>
        {
            int i = (int)a.Number(1) + 1;
            if (i > 3) { a.L.Push(LuaValue.Nil); return 1; }
            a.L.Push(i); a.L.Push(i * 10); return 2;
        })));
        Check("TFORLOOP", () => Go(P(8, new LuaValue[] { "it", 0 }, new[]
        {
            ABx(LOADK, 0, 1), ABx(GETGLOBAL, 1, 0), ABC(LOADNIL, 2, 2, 0), ABx(LOADK, 3, 1), AsBx(JMP, 0, 1),
            ABC(ADD, 0, 0, 4), ABC(TFORLOOP, 1, 0, 1), AsBx(JMP, 0, -3), ABC(RETURN, 0, 2, 0),
        })), "60");
        // TAILCALL: return it(nil, 1)
        Check("TAILCALL", () => Go(P(4, new LuaValue[] { "it", 1 }, new[]
        {
            ABx(GETGLOBAL, 0, 0), ABC(LOADNIL, 1, 1, 0), ABx(LOADK, 2, 1), ABC(TAILCALL, 0, 3, 0), ABC(RETURN, 0, 0, 0),
        })), "2,20");
        // comparisons: 1 < 2, "a" <= "b", 1 < "x" is an error
        Check("LT LE", () => Go(P(3, new LuaValue[] { 1, 2, "a", "b" }, new[]
        {
            ABC(LOADBOOL, 0, 0, 0), ABC(LT, 0, K(0), K(1)), AsBx(JMP, 0, 1), ABC(LOADBOOL, 0, 1, 0),
            ABC(LOADBOOL, 1, 0, 0), ABC(LE, 0, K(2), K(3)), AsBx(JMP, 0, 1), ABC(LOADBOOL, 1, 1, 0), ABC(RETURN, 0, 3, 0),
        })), "true,true");
        Check("compare error", () => Go(P(2, new LuaValue[] { 1, "x" }, new[] { ABC(LT, 0, K(0), K(1)), AsBx(JMP, 0, 0), ABC(RETURN, 0, 1, 0) })),
            "error: selftest:pc0: attempt to compare number with string");
        // TEST: a and b / a or b
        Check("TEST and/or", () => Go(P(3, new LuaValue[] { 5 }, new[]
        {
            ABC(LOADNIL, 0, 0, 0), ABC(TEST, 1, 0, 1), AsBx(JMP, 0, 1), ABx(LOADK, 1, 0), ABC(RETURN, 1, 2, 0),
        })), "5");
        // __index / __newindex / __eq metamethods (as the GRuntimeInstance userdata uses them)
        {
            var mt = new LuaTable();
            mt.Set("__index", new LuaCFunction("idx", a => LuaValue.String("got " + a.String(1))));
            mt.Set("__eq", new LuaCFunction("eq", a => LuaValue.True));
            L.Globals.Set("u1", LuaValue.UserData(new LuaUserData(1, mt)));
            L.Globals.Set("u2", LuaValue.UserData(new LuaUserData(2, mt)));
            Check("userdata __index __eq", () => Go(P(3, new LuaValue[] { "u1", "u2", "Name" }, new[]
            {
                ABx(GETGLOBAL, 0, 0), ABx(GETGLOBAL, 1, 1), ABC(LOADBOOL, 2, 0, 0), ABC(EQ, 0, 0, 1), AsBx(JMP, 0, 1), ABC(LOADBOOL, 2, 1, 0),
                ABC(GETTABLE, 1, 0, K(2)), ABC(RETURN, 1, 3, 0),
            })), "'got Name',true");
        }
        // undump round trip of a real header (the exe's check of sizes and the test number)
        Check("undump header", () =>
        {
            var b = new List<byte> { 0x1B, (byte)'L', (byte)'u', (byte)'a', 0x50, 1, 4, 4, 4, 6, 8, 9, 9, 4 };
            b.AddRange(BitConverter.GetBytes(3.14159265358979323846e7f));
            void I(int v) => b.AddRange(BitConverter.GetBytes(v));
            I(0); I(0); b.AddRange(new byte[] { 0, 0, 0, 2 });   // source NULL, line, nups, params, vararg, stack
            I(0); I(0); I(0);                                    // lines, locals, upvalues
            I(1); b.Add(3); b.AddRange(BitConverter.GetBytes(42f)); // constants: 42
            I(0);                                                // protos
            I(2); b.AddRange(BitConverter.GetBytes(ABx(LOADK, 0, 0))); b.AddRange(BitConverter.GetBytes(ABC(RETURN, 0, 2, 0)));
            return L.Call(LuaValue.Function(L.Load(b.ToArray(), "=t")));
        }, "42");
        // bRandom (bMath.cpp 0x45DA30) on bDefaultSeed 0x12345678 without the bSetRandomSeed draw: the first results
        Check("bRandom", () =>
        {
            var r = new BRandom(0) { Seed = 0x12345678 };
            return new LuaValue[] { r.Next(100), r.Next(100), r.Next(0), r.Next(7) };
        }, BRandomExpect());
        // GActivity::SaveState's table serializer (FUN_00617A80): persistent blocks keep booleans, numbers, strings and
        // tables, drop userdata and functions; nested tables survive
        Check("state table round trip", () =>
        {
            var t = new LuaTable();
            t.Set("Count", 3); t.Set("On", true); t.Set("Name", "rival_locked"); t.Set(1, 0.1f);
            var inner = new LuaTable(); inner.Set(2, "x"); t.Set("Inner", inner);
            t.Set("Ud", LuaValue.UserData(new LuaUserData(5, new LuaTable())));
            t.Set("Fn", new LuaCFunction("f", a => LuaValue.Nil));
            var back = GameplayStateStore.Deserialize(GameplayStateStore.Serialize(t));
            return new[] { back.Get("Count"), back.Get("On"), back.Get("Name"), back.Get(1), back.Get("Inner").AsTable.Get(2), LuaValue.Bool(back.Get("Ud").IsNil && back.Get("Fn").IsNil) };
        }, "3,true,'rival_locked',0.10000000149012,'x',true");
        Console.WriteLine($"selftest: {_pass} ok, {_fail} failed");
    }

    static string BRandomExpect()
    {
        // the reference: bMath.cpp's bRandom(range, seed), written out
        uint seed = 0x12345678;
        uint Next(int range)
        {
            if (range == 0) return 0;
            uint result = seed, next = result ^ 0x1D872B41, temp = next ^ (next >> 5);
            seed = temp ^ (next ^ (temp << 27));
            return result % (uint)range;
        }
        return string.Join(",", new[] { Next(100), Next(100), Next(0), Next(7) });
    }
}
