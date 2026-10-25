using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// The AEMS module runtime (EA "rwaudiocore" 2.09, decomp Libs/snd/9/source/library/cmn/saems.c, aemsdef.h; PC
    /// 0x8196C2-0x81A800). An ".abk" ModuleBank holds, per Csis class, compiled x86 "link" code plus an instance data
    /// image. The code is straight-line glue: it walks the node states of one instance (esi), calls each node's update
    /// function (call operands are func-fixup indices into sndaemsfuncs[40], PC table @0x90BC40) and copies the result
    /// into the inputs of the nodes downstream (<c>mov [esi+d], eax</c>), with a few inline add/sub/imul/min/max ops.
    /// This class interprets that code (the instruction subset the AEMS compiler emits, checked over every MW bank) and
    /// ports the 40 node functions; player nodes call an <see cref="IAemsHost"/> to play bank samples.
    /// <para>Timer: SNDAEMSI_timerupdate runs every module instance at the variable-timer rate; MW sets 50 Hz
    /// (@0x4C422F pushes 50.0 from 0x8EE64C to the setter @0x81B341, period = 1000/rate) so gVariableTimerPeriod
    /// (@0x9C1B54) = 20 ms.</para>
    /// <para>ModuleBank header (aemsdef.h ModuleBank; PC same): +0xA nummodules, +0x1C moduleoffset, +0x20 sfx bank
    /// offset, +0x30 func fixups, +0x34 static data fixups, +0x38 interface fixups {handleOffset, IDOffset, type}.
    /// Module (0x3C + 4 x (numPlayers + numClassControllers)): +0x4 class handle, +0x1E maxinstances, +0x20 numGlobals,
    /// +0x22 numFunctions, +0x24 numPlayers, +0x25 destructor, +0x26 class data, +0x27 class controllers, +0x28 code,
    /// +0x2C data, +0x30 data size, +0x34 destroy state offset, +0x3C player/controller state offsets.</para>
    /// </summary>
    public sealed class AemsBank
    {
        public readonly byte[] D;
        public readonly string Path;
        public readonly List<AemsModule> Modules = new List<AemsModule>();
        /// <summary>Interface names by handle file offset (Csis classes, global variables, functions).</summary>
        public readonly Dictionary<int, string> Interfaces = new Dictionary<int, string>();
        public int SfxBankOffset;

        AemsBank(byte[] d, string path) { D = d; Path = path; }

        public AemsModule Find(string cls)
        {
            foreach (var m in Modules) if (m.Name == cls) return m;
            return null;
        }

        static readonly Dictionary<string, AemsBank> Cache = new Dictionary<string, AemsBank>();

        /// <summary>Loads (cached) a bank; null if missing or not a module bank.</summary>
        public static AemsBank Load(string path)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var b)) return b;
                try { b = File.Exists(path) ? Read(File.ReadAllBytes(path), path) : null; }
                catch (Exception) { b = null; }
                Cache[path] = b;
                return b;
            }
        }

        internal int I32(int o) => o >= 0 && o + 4 <= D.Length ? BitConverter.ToInt32(D, o) : 0;

        public static AemsBank Read(byte[] d, string path = "")
        {
            if (d.Length < 0x40 || d[0] != 'A' || d[1] != 'B' || d[2] != 'K' || d[3] != 'C') return null;
            var b = new AemsBank(d, path);
            int nmod = BitConverter.ToUInt16(d, 0xA), modOff = b.I32(0x1C), funcFix = b.I32(0x30), intf = b.I32(0x38);
            b.SfxBankOffset = b.I32(0x20);
            if (intf > 0 && intf + 4 <= d.Length)
            {
                int n = b.I32(intf);
                for (int i = 0; i < n && i < 4096; i++)
                {
                    int h = b.I32(intf + 4 + i * 12), id = b.I32(intf + 8 + i * 12);
                    if (id <= 0 || id + 4 >= d.Length) continue;
                    int e = id + 4;
                    while (e < d.Length && d[e] != 0) e++;
                    b.Interfaces[h] = System.Text.Encoding.ASCII.GetString(d, id + 4, e - id - 4);
                }
            }
            var fix = new Dictionary<int, int>();
            if (funcFix > 0 && funcFix + 4 <= d.Length)
            {
                int n = b.I32(funcFix);
                for (int i = 0; i < n && funcFix + 8 + i * 4 <= d.Length; i++)
                {
                    int at = b.I32(funcFix + 4 + i * 4);
                    fix[at] = b.I32(at);
                }
            }
            int m = modOff;
            for (int i = 0; i < nmod && m > 0 && m + 0x3C <= d.Length; i++)
            {
                var mod = new AemsModule
                {
                    Bank = b,
                    Offset = m,
                    MaxInstances = BitConverter.ToInt16(d, m + 0x1E),
                    NumGlobals = BitConverter.ToUInt16(d, m + 0x20),
                    NumFunctions = BitConverter.ToUInt16(d, m + 0x22),
                    NumPlayers = d[m + 0x24],
                    HasDestructor = d[m + 0x25] != 0,
                    HasClassData = d[m + 0x26] != 0,
                    NumControllers = d[m + 0x27],
                    Code = b.I32(m + 0x28),
                    Data = b.I32(m + 0x2C),
                    DataSize = b.I32(m + 0x30),
                    DestroyOffset = b.I32(m + 0x34),
                };
                b.Interfaces.TryGetValue(m + 4, out mod.Name);
                mod.Name = mod.Name ?? "?";
                mod.PlayerOffsets = new int[mod.NumPlayers];
                for (int k = 0; k < mod.NumPlayers; k++) mod.PlayerOffsets[k] = b.I32(m + 0x3C + k * 4);
                mod.ControllerOffsets = new int[mod.NumControllers];
                for (int k = 0; k < mod.NumControllers; k++) mod.ControllerOffsets[k] = b.I32(m + 0x3C + (mod.NumPlayers + k) * 4);
                mod.Compile(fix);
                b.Modules.Add(mod);
                m += 0x3C + 4 * (mod.NumPlayers + mod.NumControllers);
            }
            return b;
        }
    }

    /// <summary>One compiled Csis class of a bank (AemsDef::Module).</summary>
    public sealed class AemsModule
    {
        public AemsBank Bank;
        public string Name;
        public int Offset, MaxInstances, NumGlobals, NumFunctions, NumPlayers, NumControllers, Code, Data, DataSize, DestroyOffset;
        public bool HasDestructor, HasClassData;
        public int[] PlayerOffsets, ControllerOffsets;
        /// <summary>False when the link code uses an instruction the interpreter does not know.</summary>
        public bool Supported;
        public string Error;
        internal Op[] Ops = Array.Empty<Op>();
        public int CurInstances;

        internal enum K : byte { Nop, Push, Call, St, StB, Ld, AddM, SubM, ImulM, CmpM, MovRR, AddRR, SubRR, CmpRR, ImulRR, AddEsi, Jcc, Jmp, Ret }

        internal struct Op
        {
            public K Kind;
            public byte R, R2, Cond;
            public int Disp, Target;
        }

        /// <summary>Decodes the link code (x86, the subset the AEMS compiler emits) into ops.</summary>
        internal void Compile(Dictionary<int, int> fix)
        {
            var d = Bank.D;
            var ops = new List<Op>();
            var addrToOp = new Dictionary<int, int>();
            var jumps = new List<(int op, int addr)>();
            int p = Code, end = Math.Min(d.Length, Data > Code ? Data : d.Length);
            try
            {
                while (p < end)
                {
                    addrToOp[p] = ops.Count;
                    byte b = d[p];
                    var op = new Op();
                    if (b == 0x56) { op.Kind = K.Push; p++; }
                    else if (b == 0x5E) { op.Kind = K.Nop; p++; }
                    else if (b == 0xC3) { op.Kind = K.Ret; p++; ops.Add(op); break; }
                    else if (b == 0x8B && d[p + 1] == 0x74 && d[p + 2] == 0x24 && d[p + 3] == 0x08) { op.Kind = K.Nop; p += 4; } // mov esi,[esp+8]
                    else if (b == 0xE8)
                    {
                        if (!fix.TryGetValue(p + 1, out int idx)) throw new Exception($"call without fixup @{p:X}");
                        op.Kind = K.Call; op.Target = idx; p += 5;
                    }
                    else if (b == 0x83 || b == 0x81)
                    {
                        byte rm = d[p + 1];
                        int imm = b == 0x83 ? (sbyte)d[p + 2] : BitConverter.ToInt32(d, p + 2);
                        int len = b == 0x83 ? 3 : 6;
                        if (rm == 0xC6) { op.Kind = K.AddEsi; op.Disp = imm; }
                        else if (rm == 0xC4) op.Kind = K.Nop;           // add esp, n (call cleanup)
                        else throw new Exception($"83/81 {rm:X2} @{p:X}");
                        p += len;
                    }
                    else if (b == 0x89 || b == 0x8B || b == 0x03 || b == 0x2B || b == 0x3B || (b == 0x0F && d[p + 1] == 0xAF))
                    {
                        int q = b == 0x0F ? p + 2 : p + 1;
                        byte modrm = d[q];
                        int mod = modrm >> 6, reg = (modrm >> 3) & 7, rm = modrm & 7;
                        int len = q + 1 - p, disp = 0;
                        if (mod == 3)
                        {
                            op.R = (byte)reg; op.R2 = (byte)rm;   // 89: rm <- reg; others: reg <- rm
                            op.Kind = b == 0x89 ? K.MovRR : b == 0x8B ? K.MovRR : b == 0x03 ? K.AddRR : b == 0x2B ? K.SubRR : b == 0x3B ? K.CmpRR : K.ImulRR;
                            if (b == 0x89) { op.R = (byte)rm; op.R2 = (byte)reg; }
                        }
                        else
                        {
                            if (rm != 6) throw new Exception($"modrm {modrm:X2} @{p:X}");
                            if (mod == 1) { disp = (sbyte)d[q + 1]; len++; }
                            else if (mod == 2) { disp = BitConverter.ToInt32(d, q + 1); len += 4; }
                            op.R = (byte)reg; op.Disp = disp;
                            op.Kind = b == 0x89 ? K.St : b == 0x8B ? K.Ld : b == 0x03 ? K.AddM : b == 0x2B ? K.SubM : b == 0x3B ? K.CmpM : K.ImulM;
                        }
                        p += len;
                    }
                    else if (b == 0xC6)
                    {
                        // mov byte ptr [esi+d], imm8 (clears a trigger byte)
                        byte modrm = d[p + 1];
                        int mod = modrm >> 6, len = 2, disp = 0;
                        if ((modrm & 0x3F) != 6 || mod == 3) throw new Exception($"C6 {modrm:X2} @{p:X}");
                        if (mod == 1) { disp = (sbyte)d[p + 2]; len++; }
                        else if (mod == 2) { disp = BitConverter.ToInt32(d, p + 2); len += 4; }
                        op.Kind = K.StB; op.Disp = disp; op.Target = d[p + len]; p += len + 1;
                    }
                    else if (b >= 0x70 && b <= 0x7F)
                    {
                        op.Kind = K.Jcc; op.Cond = (byte)(b & 0xF);
                        jumps.Add((ops.Count, p + 2 + (sbyte)d[p + 1])); p += 2;
                    }
                    else if (b == 0x0F && d[p + 1] >= 0x80 && d[p + 1] <= 0x8F)
                    {
                        op.Kind = K.Jcc; op.Cond = (byte)(d[p + 1] & 0xF);
                        jumps.Add((ops.Count, p + 6 + BitConverter.ToInt32(d, p + 2))); p += 6;
                    }
                    else if (b == 0xEB) { op.Kind = K.Jmp; jumps.Add((ops.Count, p + 2 + (sbyte)d[p + 1])); p += 2; }
                    else if (b == 0xE9) { op.Kind = K.Jmp; jumps.Add((ops.Count, p + 5 + BitConverter.ToInt32(d, p + 1))); p += 5; }
                    else throw new Exception($"opcode {b:X2} @{p:X}");
                    ops.Add(op);
                }
                foreach (var (o, a) in jumps)
                {
                    if (!addrToOp.TryGetValue(a, out int t)) throw new Exception($"jump into an instruction @{a:X}");
                    var x = ops[o]; x.Target = t; ops[o] = x;
                }
                Ops = ops.ToArray();
                Supported = Ops.Length > 0 && Ops[Ops.Length - 1].Kind == K.Ret;
            }
            catch (Exception e)
            {
                Supported = false;
                Error = e.Message;
            }
        }
    }

    /// <summary>Playback options of a bank voice (SNDPLAYOPTS as SNDplaysetdef @0x81D582 + the AEMS opt functions).</summary>
    public struct AemsPlayOpts
    {
        /// <summary>x4096 = 1.0 (0..0xFFFF).</summary>
        public int PitchMult, TimeMult;
        /// <summary>0..127 (value >> 8).</summary>
        public int Vol, FxWet, Dry;
        public int Azimuth;
        /// <summary>Hz, 0xFFFF = open.</summary>
        public int LowPass, HighPass;

        public static AemsPlayOpts Default => new AemsPlayOpts { PitchMult = 0x1000, TimeMult = 0x1000, Vol = 0x7F, Dry = 0x7F, FxWet = 0, LowPass = 0xFFFF, HighPass = 0 };
    }

    /// <summary>The sound system side of the player nodes (SNDBANK_play, SNDstop, SNDVOICEI_get, the bank* setters).</summary>
    public interface IAemsHost
    {
        /// <summary>SNDBANK_play(bank's sfx bank, patnum, opts): a handle ≥ 0, or -1.</summary>
        int Play(AemsBank bank, int patnum, AemsPlayOpts opts);
        void Stop(int handle);
        /// <summary>SNDAEMSI_playerpausebank: volume 0 and pitch 0.</summary>
        void Pause(int handle);
        /// <summary>SNDVOICEI_get(handle) ≥ 0: the voice still plays.</summary>
        bool Alive(int handle);
        /// <summary>playerinterfacefn[type](voice, value): 0 pitch, 1 time, 2 vol, 3 azimuth, 5 fx wet, 6 low-pass, 7 high-pass, 8 dry.</summary>
        void Set(int handle, int type, int value);
        /// <summary>Current frame, frame count, sample rate, and whether the sample sustains (loops).</summary>
        void Position(int handle, out int frame, out int frames, out int rate, out bool sustain);
        /// <summary>A ControlClass node: create (params), update (member data) or release another Csis class instance.
        /// Returns an id ≠ 0 for a live instance.</summary>
        int ControlClass(string cls, int id, int op, int[] parameters);
    }

    /// <summary>
    /// A live module instance (MODULEINSTANCE + the node states): SNDAEMSI_CreateModuleInstance copies the data image,
    /// the Csis class's member data feeds the ClassData outputs, the destructor sets the ClassDestructor trigger and the
    /// destroy node ends it. <see cref="Tick"/> = one timer update (20 ms).
    /// </summary>
    public sealed class AemsInstance
    {
        public const float TimerPeriodMs = 20f;
        public readonly AemsModule M;
        public readonly byte[] Mem;
        public readonly IAemsHost Host;
        public bool Alive { get; private set; }
        int _dtorOff = -1, _dataOff = -1, _numOutputs;
        readonly List<(int off, string name)> _globals = new List<(int, string)>();
        readonly int[] _regs = new int[8];
        static readonly Random Rng = new Random(0x5EED);
        /// <summary>Optional trace of node calls (harness).</summary>
        public Action<string> Trace;
        /// <summary>Csis global variables (SNDAEMSI_SetGlobalVariable): name → value.</summary>
        public static readonly Dictionary<string, int> Globals = new Dictionary<string, int>();

        AemsInstance(AemsModule m, IAemsHost host)
        {
            M = m;
            Host = host;
            Mem = new byte[Math.Max(m.DataSize, 0x18)];
            Buffer.BlockCopy(m.Bank.D, m.Data, Mem, 0, Math.Min(m.DataSize, m.Bank.D.Length - m.Data));
            Alive = true;
            // SNDAEMSI_CreateModuleInstance: destructor, globals, class data, functions follow the 0x18 header
            int p = 0x18;
            if (m.HasDestructor) { _dtorOff = p; p += 0x14; }
            for (int i = 0; i < m.NumGlobals; i++)
            {
                m.Bank.Interfaces.TryGetValue(m.Data + p, out var gname);
                _globals.Add((p, gname ?? ""));
                p += 0x1C;
            }
            if (m.HasClassData) { _dataOff = p; _numOutputs = Mem[p + 0x10]; }
            // the player handles start invalid (sampletype -1 is in the image)
            foreach (int po in m.PlayerOffsets) if (po + 0x1C <= Mem.Length) W(po + 8, -1);
        }

        /// <summary>Csis CreateInstance: a new instance of <paramref name="m"/> with the class parameters, or null
        /// (unsupported code, or maxinstances reached).</summary>
        public static AemsInstance Create(AemsModule m, IAemsHost host, int[] parameters)
        {
            if (m == null || !m.Supported || host == null) return null;
            if (m.MaxInstances > 0 && m.CurInstances >= m.MaxInstances) return null;
            var inst = new AemsInstance(m, host);
            m.CurInstances++;
            inst.SetParams(parameters);
            return inst;
        }

        public int NumParams => _numOutputs;

        /// <summary>SNDAEMSI_SetClassData: the member data (all parameters).</summary>
        public void SetParams(int[] parameters)
        {
            if (_dataOff < 0 || parameters == null) return;
            for (int i = 0; i < _numOutputs && i < parameters.Length; i++) W(_dataOff + 0x14 + i * 4, parameters[i]);
        }

        public void SetParam(int i, int v)
        {
            if (_dataOff >= 0 && i >= 0 && i < _numOutputs) W(_dataOff + 0x14 + i * 4, v);
        }

        public int GetParam(int i) => _dataOff >= 0 && i >= 0 && i < _numOutputs ? R(_dataOff + 0x14 + i * 4) : 0;

        /// <summary>Csis Release → SNDAEMSI_SetClassDestructor: the graph sees its destructor trigger and ends itself.</summary>
        public void Release()
        {
            if (!Alive) return;
            if (_dtorOff >= 0) W(_dtorOff + 0x10, 1);
            else Kill();
        }

        /// <summary>SNDAEMSI_updatedestroy (triggered, or the bank removed): stop every player, free the instance.</summary>
        public void Kill()
        {
            if (!Alive) return;
            Alive = false;
            M.CurInstances--;
            foreach (int po in M.PlayerOffsets)
                if (po + 0x1C <= Mem.Length && (sbyte)Mem[po + 0x10] >= 0) Host.Stop(R(po + 8));
            foreach (int co in M.ControllerOffsets)
                if (co + 0x10 <= Mem.Length && R(co + 8) != 0) { Host.ControlClass(Iface(co), R(co + 8), 2, null); W(co + 8, 0); }
        }

        string Iface(int memOff) => M.Bank.Interfaces.TryGetValue(M.Data + memOff, out var n) ? n : "";

        int R(int o) => o >= 0 && o + 4 <= Mem.Length ? BitConverter.ToInt32(Mem, o) : 0;
        void W(int o, int v)
        {
            if (o < 0 || o + 4 > Mem.Length) return;
            Mem[o] = (byte)v; Mem[o + 1] = (byte)(v >> 8); Mem[o + 2] = (byte)(v >> 16); Mem[o + 3] = (byte)(v >> 24);
        }
        float F(int o) => BitConverter.Int32BitsToSingle(R(o));
        void WF(int o, float v) => W(o, BitConverter.SingleToInt32Bits(v));
        short R16(int o) => o >= 0 && o + 2 <= Mem.Length ? BitConverter.ToInt16(Mem, o) : (short)0;
        void W16(int o, int v) { if (o >= 0 && o + 2 <= Mem.Length) { Mem[o] = (byte)v; Mem[o + 1] = (byte)(v >> 8); } }
        int BI32(int fileOff) => M.Bank.I32(fileOff);

        /// <summary>SNDI_ftoifast: fistp with the default round-to-nearest-even.</summary>
        static int FtoI(float f) => (int)Math.Round((double)f, MidpointRounding.ToEven);
        static int Clip(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>One SNDAEMSI_timerupdate pass over this instance: runs the link code.</summary>
        public void Tick()
        {
            if (!Alive) return;
            foreach (var (off, name) in _globals) W(off + 0x18, Globals.TryGetValue(name, out int gv) ? gv : 0);
            var ops = M.Ops;
            int esi = 0x18, arg = 0, cmpA = 0, cmpB = 0;
            var r = _regs;
            Array.Clear(r, 0, 8);
            for (int pc = 0, guard = 0; pc < ops.Length && guard < 100000; guard++)
            {
                var op = ops[pc++];
                switch (op.Kind)
                {
                    case AemsModule.K.Push: arg = esi; break;
                    case AemsModule.K.Call:
                        r[0] = Node(op.Target, arg);
                        if (!Alive) return;
                        break;
                    case AemsModule.K.St: W(esi + op.Disp, r[op.R]); break;
                    case AemsModule.K.StB: if (esi + op.Disp >= 0 && esi + op.Disp < Mem.Length) Mem[esi + op.Disp] = (byte)op.Target; break;
                    case AemsModule.K.Ld: r[op.R] = R(esi + op.Disp); break;
                    case AemsModule.K.AddM: r[op.R] += R(esi + op.Disp); break;
                    case AemsModule.K.SubM: r[op.R] -= R(esi + op.Disp); break;
                    case AemsModule.K.ImulM: r[op.R] *= R(esi + op.Disp); break;
                    case AemsModule.K.CmpM: cmpA = r[op.R]; cmpB = R(esi + op.Disp); break;
                    case AemsModule.K.MovRR: r[op.R] = r[op.R2]; break;
                    case AemsModule.K.AddRR: r[op.R] += r[op.R2]; break;
                    case AemsModule.K.SubRR: r[op.R] -= r[op.R2]; break;
                    case AemsModule.K.ImulRR: r[op.R] *= r[op.R2]; break;
                    case AemsModule.K.CmpRR: cmpA = r[op.R]; cmpB = r[op.R2]; break;
                    case AemsModule.K.AddEsi: esi += op.Disp; break;
                    case AemsModule.K.Jmp: pc = op.Target; break;
                    case AemsModule.K.Jcc: if (Cond(op.Cond, cmpA, cmpB)) pc = op.Target; break;
                    case AemsModule.K.Ret: pc = ops.Length; break;
                }
            }
        }

        static bool Cond(int c, int a, int b)
        {
            uint ua = (uint)a, ub = (uint)b;
            switch (c)
            {
                case 0x2: return ua < ub;       // jb
                case 0x3: return ua >= ub;      // jae
                case 0x4: return a == b;        // je
                case 0x5: return a != b;        // jne
                case 0x6: return ua <= ub;      // jbe
                case 0x7: return ua > ub;       // ja
                case 0xC: return a < b;         // jl
                case 0xD: return a >= b;        // jge
                case 0xE: return a <= b;        // jle
                case 0xF: return a > b;         // jg
                default: return false;
            }
        }

        /// <summary>sndaemsfuncs[idx](state) — saems.c, PC addresses in the table @0x90BC40.</summary>
        int Node(int idx, int s)
        {
            int v = NodeImpl(idx, s);
            Trace?.Invoke($"{M.Name} n{idx} @{s:X} -> {v}");
            return v;
        }

        int NodeImpl(int idx, int s)
        {
            switch (idx)
            {
                case 0: { int t = R(s + 0x10); W(s + 0x10, 0); return t; }        // ClassDestructor @0x8196C2
                case 1: return R(s + 0x14);                                         // ClassData @0x8196CE
                case 2: return R(s + 0x18);                                         // GlobalVariable @0x8196D6
                case 3: { int t = R(s); W(s, 0); return t; }                        // create @0x8196DE
                case 4: if (R(s + 0xC) != 0) Kill(); return 0;                      // destroy @0x8196E8
                case 5: return CallFunction(s);
                case 6: return Counter(s);
                case 7:                                                             // random @0x819A0F
                    if (R(s + 0xC) == 0) return R(s + 8);
                    { uint range = (uint)R(s + 4); int v = range == 0 ? 0 : (int)((uint)Rng.Next() % range); W(s + 8, v + R(s)); return R(s + 8); }
                case 8: return RandomShuffle(s);
                case 9: return RandomWeighted(s);
                case 10: return RangeTrig(s);
                case 11: return DelayTrig(s);
                case 12: return StateGen(s);
                case 13: { int n = Mem[s]; for (int i = 0; i < n; i++) if (R(s + 4 + i * 4) != 0) return 1; return 0; }   // merge @0x819BBE
                case 14: return Envelope(s);
                case 15: return Table(s);
                case 16: return DelayLine(s);
                case 17: { int c = R(s + 4); return c <= 0 || c > Mem[s] ? 0 : R(s + 4 + c * 4); }                     // mux @0x819E57
                case 18:                                                                                                    // demux @0x819E71
                    {
                        W(s + 8 + R16(s + 2) * 4, 0);
                        int c = R(s + 4);
                        if (c > 0 && c <= Mem[s]) { W(s + 8 + c * 4, R(s + 8)); W16(s + 2, c); }
                        return R(s + 0xC);
                    }
                case 19: { int n = Mem[s], m = R(s + 4); for (int i = 1; i < n; i++) m = Math.Min(m, R(s + 4 + i * 4)); return m; }
                case 20: { int n = Mem[s], m = R(s + 4); for (int i = 1; i < n; i++) m = Math.Max(m, R(s + 4 + i * 4)); return m; }
                case 21:                                                                                                    // scale @0x819F0B
                    {
                        int n = Mem[s];
                        float o = R(s + 8);
                        for (int i = 1; i < n; i++) o *= R(s + 8 + i * 4);
                        return FtoI(o * F(s + 4));
                    }
                case 22: { int n = Mem[s], o = R(s + 4); for (int i = 1; i < n; i++) o += R(s + 4 + i * 4); return o; }
                case 23: return R(s) - R(s + 4);
                case 24: return R(s) * R(s + 4);
                case 25: { int b = R(s + 4); return b != 0 ? R(s) / b : 0; }
                case 26: { int b = R(s + 4); return b != 0 ? R(s) % b : 0; }
                case 27: return Player(s);
                case 28: return Oscillator(s);
                case 29: return Ramp(s);
                case 30: { int n = Mem[s], o = R(s + 8); for (int i = 1; i < n; i++) o += R(s + 8 + i * 4); int mx = R(s + 4); return o > mx ? mx : o; }
                case 31: { int o = R(s + 4) - R(s + 8), mn = R(s); return o < mn ? mn : o; }
                case 32: { int o = R(s + 4) * R(s + 8), mx = R(s); return o > mx ? mx : o; }
                case 33: return Math.Min(R(s), R(s + 4));
                case 34: return Math.Max(R(s), R(s + 4));
                case 35: return FtoI((float)R(s + 4) * (float)R(s + 8) * F(s));     // scale2 @0x819F3A
                case 36: return R(s) + R(s + 4);
                case 37: { int t = Mem[s + 0x19]; Mem[s + 0x19] = 0; return t; }  // Function @0x81A7F4
                case 38: return ControlClass(s);
                case 39: return SetGlobal(s);
                default: return 0;
            }
        }

        int CallFunction(int s)   // UpdateCallFunction @0x819809: Csis functions are not used by MW's banks
        {
            int n = Mem[s + 9];
            int inp = Mem[s + 8] != 0 ? s + 0xC + n * 8 : s + 0xC;
            if (Mem[s + 8] != 0)
                for (int i = 0; i < n; i++) W(inp + 4 + i * 4, Clip(R(inp + 4 + i * 4), R(s + 0xC + i * 8), R(s + 0x10 + i * 8)));
            return 0;
        }

        int Counter(int s)        // @0x8199D1
        {
            int ov = R(s + 0x14);
            if (ov >= R(s) && ov <= R(s + 4)) return ov;
            if (R(s + 0x10) > 0)
            {
                int v = R(s + 8) + (sbyte)Mem[s + 0xC];
                if (v > R(s + 4)) v = R(s); else if (v < R(s)) v = R(s + 4);
                W(s + 8, v);
            }
            return R(s + 8);
        }

        int RandomShuffle(int s)  // @0x819A32
        {
            int inputs = s + (ushort)R16(s);
            if (R(inputs) == 0) return R(s + 0xC);
            int idx = (ushort)R16(s + 8), range = (ushort)R16(s + 0xA), avoid = (sbyte)Mem[s + 3];
            uint div = (uint)(range - avoid - idx);
            int swap = (div == 0 ? 0 : (int)((uint)Rng.Next() % div)) + idx;
            if (Mem[s + 2] == 1)
            {
                int cur = Mem[s + 0x10 + swap];
                Mem[s + 0x10 + swap] = Mem[s + 0x10 + idx];
                Mem[s + 0x10 + idx] = (byte)cur;
                W(s + 0xC, cur);
            }
            else
            {
                int cur = (ushort)R16(s + 0x10 + swap * 2);
                W16(s + 0x10 + swap * 2, R16(s + 0x10 + idx * 2));
                W16(s + 0x10 + idx * 2, cur);
                W(s + 0xC, cur);
            }
            W(s + 0xC, R(s + 0xC) + R(s + 4));
            idx++;
            W16(s + 8, idx);
            if ((ushort)idx >= range) { W16(s + 8, 0); Mem[s + 3] = 1; }
            else Mem[s + 3] = 0;
            return R(s + 0xC);
        }

        int RandomWeighted(int s) // @0x819ACE
        {
            if (R(s + 0x10) != 0)
            {
                int rnd = (int)((uint)Rng.Next() % 100u), table = R(s), total = 0, range = R(s + 8);
                for (int i = 0; i < range; i++)
                {
                    total += (sbyte)M.Bank.D[table + 0x10 + i];
                    if ((uint)total > (uint)rnd) { W(s + 0xC, R(s + 4) + i); break; }
                }
            }
            return R(s + 0xC);
        }

        int RangeTrig(int s)      // @0x819B13
        {
            int x = R(s + 0x14);
            if (x >= R(s) && x <= R(s + 4))
            {
                if (Mem[s + 0x10] == 0) { Mem[s + 0x10] = 1; Mem[s + 0x11] = 1; return 1; }
            }
            else if (x >= R(s + 8) && x <= R(s + 0xC)) Mem[s + 0x10] = 0;
            Mem[s + 0x11] = 0;
            return 0;
        }

        int DelayTrig(int s)      // @0x819B49
        {
            if (R(s + 8) != 0) WF(s, 0f);
            float t = F(s);
            if (t >= 0f)
            {
                if ((float)R(s + 0xC) <= t) { Mem[s + 4] = 1; WF(s, -1f); return 1; }
                WF(s, t + TimerPeriodMs);
            }
            Mem[s + 4] = 0;
            return 0;
        }

        int StateGen(int s)       // @0x819B90
        {
            int inputs = s + (ushort)R16(s), n = Mem[s + 2];
            for (int i = 0; i < n; i++)
                if (R(inputs + i * 4) != 0) { W(s + 4, R(s + 8 + i * 4)); break; }
            return R(s + 4);
        }

        void EnvSegment(int s)    // SNDAEMSI_envelopeprogramsegment @0x819BE2
        {
            int seg = Mem[s + 3];
            float dur = F(s + 0x18 + seg * 8);
            WF(s + 4, dur);
            WF(s + 8, (F(s + 0x1C + seg * 8) - F(s + 0xC)) / dur * TimerPeriodMs);
        }

        int Envelope(int s)       // @0x819C07
        {
            int control = R(s + (ushort)R16(s));
            int prev = (sbyte)Mem[s + 2];
            if (control == 1 && prev == 0)
            {
                WF(s + 0xC, F(s + 0x14));
                Mem[s + 3] = 0;
                EnvSegment(s);
            }
            else if (control == 3 && prev != 3 && Mem[s + 3] < R16(s + 0x12))
            {
                Mem[s + 3] = (byte)R16(s + 0x12);
                EnvSegment(s);
            }
            else if ((control == 1 || control == 3) && Mem[s + 3] < Mem[s + 0x10])
            {
                float rem = F(s + 4) - TimerPeriodMs;
                WF(s + 4, rem);
                if (rem <= 0f)
                {
                    int seg = Mem[s + 3];
                    WF(s + 0xC, F(s + 0x1C + seg * 8));
                    seg++;
                    Mem[s + 3] = (byte)seg;
                    if (seg < Mem[s + 0x10]) EnvSegment(s);
                    else WF(s + 0xC, 0f);
                }
                else WF(s + 0xC, F(s + 8) + F(s + 0xC));
            }
            else if (control != 2) WF(s + 0xC, 0f);
            Mem[s + 2] = (byte)control;
            return FtoI(F(s + 0xC));
        }

        int Table(int s)          // @0x819CC6
        {
            int input = R(s + 0xC);
            if (input == R(s + 4)) return R(s + 8);
            var d = M.Bank.D;
            int t = R(s);
            W(s + 4, input);
            if (t <= 0 || t + 0x10 > d.Length) return R(s + 8);
            int size = d[t], n = BitConverter.ToUInt16(d, t + 2), mn = BitConverter.ToInt32(d, t + 4), mx = BitConverter.ToInt32(d, t + 8);
            float res = BitConverter.ToSingle(d, t + 0xC);
            int ia = Clip(input, mn, mx) - mn;
            int Entry(int i)
            {
                i = Clip(i, 0, Math.Max(0, n - 1));
                int o = t + 0x10 + i * size;
                if (o + size > d.Length) return 0;
                return size == 2 ? BitConverter.ToInt16(d, o) : size == 1 ? (sbyte)d[o] : BitConverter.ToInt32(d, o);
            }
            if (res == 1f) W(s + 8, Entry(ia));
            else
            {
                float fi = ia * res;
                int a = FtoI(fi - 0.5f);   // SNDI_ftoitruncpositive on PC: fistp(x - 0.5)
                float frac = fi - a;
                int b = a + 1;
                if (b >= n) b = n - 1;
                float ea = Entry(a), eb = Entry(b);
                W(s + 8, FtoI(frac * (eb - ea) + ea));
            }
            return R(s + 8);
        }

        int DelayLine(int s)      // @0x819DC6
        {
            int inputs = s + (ushort)R16(s);
            int maxSlots = (ushort)R16(s + 2);
            if (R(inputs + 4) != R(s + 8))
            {
                W(s + 8, R(inputs + 4));
                if (R(inputs + 4) < 0) W(inputs + 4, 0);
                int off = FtoI(R(inputs + 4) / TimerPeriodMs);
                if (off >= maxSlots) off = maxSlots - 1;
                W16(s + 4, R16(s + 6) + off);
            }
            int ins = (ushort)R16(s + 4), outs = (ushort)R16(s + 6);
            if (ins >= maxSlots) { ins -= maxSlots; W16(s + 4, ins); }
            if (outs >= maxSlots) { outs = 0; W16(s + 6, 0); }
            W(s + 0xC + ins * 4, R(inputs));
            int v = R(s + 0xC + outs * 4);
            W16(s + 4, ins + 1);
            W16(s + 6, outs + 1);
            return v;
        }

        int Oscillator(int s)     // @0x81A61D
        {
            int period = R(s + 8);
            if (period <= 0) return 0;
            float inc = TimerPeriodMs / period, amp = R(s + 0xC), pct = F(s + 4);
            while (pct >= 1f) pct -= 1f;
            WF(s + 4, pct);
            float o;
            int wave = Mem[s];
            if (wave == 0) o = Sin(FtoI(pct * 1024f)) * amp * (1f / 65536f);
            else if (wave == 1) o = pct >= 0.5f ? amp : 0f;
            else if (wave == 2) o = pct * amp;
            else o = pct < 0.5f ? pct * 2f * amp : (1f - pct) * 2f * amp;
            WF(s + 4, pct + inc);
            return FtoI(o);
        }

        /// <summary>iSNDsin @0x81F81A: quarter-wave table (0x90BD38, 257 x u16 ≈ sin x 65536, capped 65535).</summary>
        static int Sin(int x)
        {
            int q = (x >> 8) & 3, i = x & 0xFF;
            int T(int k) => k >= 256 ? 65535 : Math.Min(65535, (int)Math.Round(Math.Sin(k * Math.PI * 2 / 1024) * 65536));
            switch (q)
            {
                case 0: return T(i);
                case 1: return T(256 - i);
                case 2: return -T(i);
                default: return -T(256 - i);
            }
        }

        int Ramp(int s)           // @0x81A707
        {
            int target = R(s + 0x18);
            float cur = F(s);
            if ((float)target == cur) return target;
            if (target != R(s + 8) || R(s + 0x10) != R(s + 0xC))
            {
                W(s + 8, target);
                int dur = R(s + 0x10);
                W(s + 0xC, dur);
                if (dur <= 0) { WF(s, target); return target; }
                WF(s + 4, (target - cur) / dur * TimerPeriodMs * (1f / 4096f));
            }
            float delta = F(s + 4);
            cur += R(s + 0x14) * delta;
            if (delta >= 0f) { if (cur > target) cur = target; }
            else if (cur < target) cur = target;
            WF(s, cur);
            return FtoI(cur);
        }

        int ControlClass(int s)   // UpdateControlClass @0x819875
        {
            bool clamp = Mem[s + 0xC] != 0;
            int n = Mem[s + 0xD];
            int inp = clamp ? s + 0x10 + n * 8 : s + 0x10;
            int id = R(s + 8);
            string cls = Iface(s);
            int[] Params()
            {
                var a = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int v = R(inp + 8 + i * 4);
                    if (clamp) { v = Clip(v, R(s + 0x10 + i * 8), R(s + 0x14 + i * 8)); W(inp + 8 + i * 4, v); }
                    a[i] = v;
                }
                return a;
            }
            if (R(inp + 4) != 0)
            {
                if (id == 0) return 0;
                Host.ControlClass(cls, id, 2, null);
                W(s + 8, 0);
                return 0;
            }
            if (R(inp) != 0)
            {
                if (id == 0) { id = Host.ControlClass(cls, 0, 0, Params()); W(s + 8, id); }
            }
            else if (id != 0) Host.ControlClass(cls, id, 1, Params());
            else return 0;
            return R(s + 8) != 0 ? 1 : 0;
        }

        int SetGlobal(int s)      // UpdateSetGlobalVariable @0x819995
        {
            int v = R(s + 0x14);
            if (R(s + 0x10) != v)
            {
                W(s + 0x10, v);
                Globals[Iface(s)] = Clip(v, R(s + 8), R(s + 0xC));
            }
            return 0;
        }

        // ---- the player node (SNDAEMSI_updateplayer @0x81A52C and the bank functions @0x81A330..0x81A52B)

        int Player(int s)
        {
            int ctl = Clip(R(s + 0x18), 0, 2);
            int prev0 = (sbyte)Mem[s + 0xC];
            int type = (sbyte)Mem[s + 0x10];
            if (ctl != prev0)
            {
                if (ctl == 0)
                {
                    if (type >= 0) { Host.Stop(R(s + 8)); ResetOutputs(s); }
                }
                else if (ctl == 1)
                {
                    if (type >= 0) Unpause(s);
                    else if (!(prev0 == 2 && (sbyte)Mem[s + 0xD] == 1))
                    {
                        int group = R(s + 4);
                        var d = M.Bank.D;
                        int count = group > 0 && group + 4 <= d.Length ? BitConverter.ToInt32(d, group) : 0;
                        if (count > 0)
                        {
                            int sel = R(s + 0x14);
                            if (sel >= count) sel = count - 1; else if (sel < 0) sel = 0;
                            int e = group + 4 + sel * 12;
                            Mem[s + 0x10] = d[e];
                            int h = (sbyte)d[e] >= 0 ? PlayBank(s, BitConverter.ToInt32(d, e + 4)) : -1;
                            W(s + 8, h);
                            if (h < 0) ResetOutputs(s);
                        }
                    }
                }
                else if (type >= 0) Host.Pause(R(s + 8));
                Mem[s + 0xD] = Mem[s + 0xC];
                Mem[s + 0xC] = (byte)ctl;
            }
            type = (sbyte)Mem[s + 0x10];
            int ret = ctl;
            if (ctl == 1 && type >= 0) ret = UpdateBank(s);
            if ((sbyte)Mem[s + 0x10] < 0) ret = 0;
            return ret;
        }

        int PlayBank(int s, int patnum)   // SNDAEMSI_playerplaybank @0x81A330
        {
            var o = AemsPlayOpts.Default;
            int n = Mem[s + 0xE];
            for (int i = 0; i < n; i++)
            {
                int ip = s + 0x1C + i * 12, v = R(ip + 8);
                switch (Mem[ip])
                {
                    case 0: o.PitchMult = Clip(v, 0, 0xFFFF); break;
                    case 1: o.TimeMult = Clip(v, 0x800, 0x2000); break;
                    case 2: o.Vol = Clip(v, 0, 0x7FFF) >> 8; break;
                    case 3: o.Azimuth = v; break;
                    case 5: o.FxWet = Clip(v, 0, 0x7FFF) >> 8; break;
                    case 6: o.LowPass = Clip(v, 0, 0xFFFF); break;
                    case 7: o.HighPass = Clip(v, 0, 0xFFFF); break;
                    case 8: o.Dry = Clip(v, 0, 0x7FFF) >> 8; break;
                }
                W(ip + 4, v);
            }
            return Host.Play(M.Bank, patnum, o);
        }

        void Unpause(int s)               // @0x81A3BC
        {
            int h = R(s + 8);
            if (!Host.Alive(h)) return;
            int pitch = 0x1000, vol = 0x7FFF, n = Mem[s + 0xE];
            for (int i = 0; i < n; i++)
            {
                int ip = s + 0x1C + i * 12;
                if (Mem[ip] == 0) { pitch = R(ip + 8); W(ip + 4, pitch); }
                else if (Mem[ip] == 2) { vol = R(ip + 8); W(ip + 4, vol); }
            }
            Host.Set(h, 0, Clip(pitch, 0, 0xFFFF));
            Host.Set(h, 2, Clip(vol, 0, 0x7FFF));
        }

        void ResetOutputs(int s)          // @0x81A42F
        {
            Mem[s + 0x10] = 0xFF;
            if (Mem[s + 0xF] != 0)
            {
                int o = s + 0x1C + Mem[s + 0xE] * 12;
                W(o, 0); W(o + 4, 0);
            }
        }

        int UpdateBank(int s)             // SNDAEMSI_playerupdatebank @0x81A451
        {
            int h = R(s + 8);
            if (!Host.Alive(h)) { ResetOutputs(s); return 0; }
            int n = Mem[s + 0xE];
            for (int i = 0; i < n; i++)
            {
                int ip = s + 0x1C + i * 12, v = R(ip + 8);
                if (v == R(ip + 4)) continue;
                int t = Mem[ip];
                switch (t)
                {
                    case 0: Host.Set(h, 0, Clip(v, 0, 0xFFFF)); break;
                    case 1: Host.Set(h, 1, Clip(v, 0x800, 0x2000)); break;
                    case 2: Host.Set(h, 2, Clip(v, 0, 0x7FFF)); break;
                    case 3: Host.Set(h, 3, v); break;
                    case 5: Host.Set(h, 5, Clip(v, 0, 0x7FFF) >> 8); break;
                    case 6: Host.Set(h, 6, Clip(v, 0, 0xFFFF)); break;
                    case 7: Host.Set(h, 7, Clip(v, 0, 0xFFFF)); break;
                    case 8: Host.Set(h, 8, Clip(v, 0, 0x7FFF) >> 8); break;
                }
                W(ip + 4, v);
            }
            if (Mem[s + 0xF] != 0)
            {
                int o = s + 0x1C + n * 12;
                Host.Position(h, out int frame, out int frames, out int rate, out bool sustain);
                float inv = 1000f / Math.Max(1, rate);
                W(o + 4, FtoI(frame * inv));
                W(o, sustain ? 0 : FtoI((frames - frame) * inv));
            }
            return 1;
        }

        /// <summary>Debug: the module's player states (sample select, play control, handle).</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < M.PlayerOffsets.Length; i++)
            {
                int s = M.PlayerOffsets[i];
                sb.Append($"P{i}[sel {R(s + 0x14)} ctl {R(s + 0x18)} type {(sbyte)Mem[s + 0x10]} h {R(s + 8)}");
                int n = Mem[s + 0xE];
                for (int k = 0; k < n; k++) sb.Append($" t{Mem[s + 0x1C + k * 12]}={R(s + 0x1C + k * 12 + 8)}");
                sb.Append("] ");
            }
            return sb.ToString();
        }
    }
}
