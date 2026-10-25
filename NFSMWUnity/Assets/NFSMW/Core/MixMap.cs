using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// The dynamic mixer map, SOUND\MIXMAPS\MAPOUTPUT*.mxb (NFSMixMap/NFSMixMapState, EAXSound/Dynamic_Mixer; PC
    /// NFSMixMap::ProcessMixMap @0x4F4270, MixMasterChannels @0x4E9B30). Every sound object writes up to 16 "DMIX
    /// inputs" (SetDMIX_Input: speeds, rpm deltas, slips, camera distance/azimuth, game-state flags ...) and reads up to
    /// 32 packed 16-bit outputs (GetDMixOutput: Q15 volumes, cents, low-pass frequencies, azimuths). In between the map
    /// is a fixed graph per mix state (eMM_MAIN, PLAYERCAR, AIRACECAR, COPCAR ...), instanced once per live object of
    /// that state:
    /// <list type="bullet">
    /// <item>header {MixMapID, NumStates (13), StateTableOffset, DynamicMapOffset}; the table holds one offset per
    /// state (-1 = none) to a state header {StateIndex, OffsetMixCtlData, Offset3DMixCtlData, OffsetSubMixData,
    /// OffsetMasterMixData, OffsetPresetData, OffsetEventCtlData, reserved} (offsets from the state header).</item>
    /// <item>mix controls {INPUTID, UScaleCntSwing, scale ids[(swing &gt;&gt; 16) &amp; 0x1F]}: curve (INPUTID bits
    /// 24-27, NFSMixShape) of an input, mapped to a dB range (swing low 16 bits: +offset or -range), times the
    /// product of the scale inputs (Q15).</item>
    /// <item>3D controls {INPUTID, per camera state {3DSTATEINFOID, CURVEID_DOPPLER, Q0..Q3 MinMax}}: distance
    /// rolloff with a curve per azimuth quadrant between min/max metres, plus Doppler cents (speed of sound in the
    /// low 16 bits).</item>
    /// <item>event controls {EVTCTLID, UScaleCntSwing, TriggerID, Param0..2, scales}: AR / ASR / ATR envelopes (stage
    /// times in 1/60 s, stage curve in bits 12-15) fired by a trigger input, swinging by the signed swing dB.</item>
    /// <item>sub channels {MIXCHID, UpperLowerSwing, input ids}: dB sums clamped to [low, high]; master channels
    /// {MIXCHID, MixData (high 16 bits = base dB on PC, read as a short @0x4E3190), SFXOBJID, input ids} plus a
    /// preset list {count | type &lt;&lt; 24, entries: slot &lt;&lt; 26 | 3D index &lt;&lt; 21 | dB offset (short);
    /// sign bit = azimuth}; type 0/4 Q15 volume, 1 cents, 2 low-pass Hz (cents x 25000), other 0..25000.</item>
    /// </list>
    /// Input ids: bits 29-31 type (0 mix control, 1 channel (bit 28 = sub), 2 sound object, 3 sound controller,
    /// 4 3D control, 5 event), bits 16-23 state, 11-15 instance, 4-10 object number, 0-3 slot (low byte = index for
    /// mixer items). An id from another state expands to every instance of that state.
    /// </summary>
    public sealed class MixMap
    {
        public const int NumStates = 13;
        public const int Main = 0, Music = 1, PlayerCar = 2, AiRaceCar = 3, CopCar = 4, Traffic = 5, Environment = 6, Collision = 7,
            DriveBy = 8, Plane = 9, Train = 10, Helicopter = 11, Truck = 12;

        /// <summary>Sound object (SndBase) id as the map addresses it.</summary>
        public static int ObjectId(bool controller, int state, int instance, int obj) =>
            (controller ? 0x60000000 : 0x40000000) | (state & 0xFF) << 16 | (instance & 0x1F) << 11 | (obj & 0x7F) << 4;

        // ---------------------------------------------------------------- file
        sealed class StateDef
        {
            public int Index;
            public CtlDef[] Ctls = Array.Empty<CtlDef>();
            public Def3D[] D3 = Array.Empty<Def3D>();
            public EvtDef[] Evts = Array.Empty<EvtDef>();
            public ChDef[] Subs = Array.Empty<ChDef>();
            public ChDef[] Masters = Array.Empty<ChDef>();
        }
        sealed class CtlDef { public int InputId, Swing; public int[] Scales; public int Offset, Ratio; }
        sealed class Def3D { public int InputId; public int[][] States; }
        sealed class EvtDef { public int Id, Swing, Trigger, P0, P1, P2; public int[] Scales; }
        sealed class ChDef { public int ChId, Data, Obj; public int[] Inputs; public int PresetType; public int[] Presets; }

        readonly StateDef[] _defs = new StateDef[NumStates];
        public int MapId { get; private set; }

        public static MixMap Load(string path) => Read(File.ReadAllBytes(path));

        public static MixMap Read(byte[] d)
        {
            var m = new MixMap { MapId = Chunks.I32(d, 0) };
            int n = Math.Min(NumStates, Chunks.I32(d, 4)), table = Chunks.I32(d, 8);
            for (int s = 0; s < n; s++)
            {
                int so = Chunks.I32(d, table + 4 * s);
                if (so < 0) continue;
                var def = new StateDef { Index = s };
                int I(int o) => Chunks.I32(d, o);
                int oCtl = I(so + 4), o3d = I(so + 8), oSub = I(so + 12), oMst = I(so + 16), oPre = I(so + 20), oEvt = I(so + 24);
                if (oCtl >= 0)
                {
                    int p = so + oCtl, cnt = I(p);
                    p += 16;
                    def.Ctls = new CtlDef[cnt];
                    for (int k = 0; k < cnt; k++)
                    {
                        var c = new CtlDef { InputId = I(p), Swing = I(p + 4) };
                        int ns = (c.Swing >> 16) & 0x1F;
                        c.Scales = new int[ns];
                        for (int j = 0; j < ns; j++) c.Scales[j] = I(p + 8 + 4 * j);
                        int tmp;
                        if ((c.Swing & 0x8000) == 0) { c.Offset = c.Swing & 0x7FFF; tmp = -c.Offset; }
                        else tmp = c.Swing | unchecked((int)0xFFFF0000);
                        c.Ratio = 0x7FFF - MixShape.Q15FromDb(tmp);
                        def.Ctls[k] = c;
                        p += 8 + 4 * ns;
                    }
                }
                if (o3d >= 0)
                {
                    int p = so + o3d, cnt = I(p) & 0xFF;
                    p += 16;
                    def.D3 = new Def3D[cnt];
                    for (int k = 0; k < cnt; k++)
                    {
                        var c = new Def3D { InputId = I(p) };
                        int ns = (c.InputId >> 24) & 0xF;
                        c.States = new int[ns][];
                        for (int j = 0; j < ns; j++)
                        {
                            c.States[j] = new int[6];
                            for (int q = 0; q < 6; q++) c.States[j][q] = I(p + 4 + 24 * j + 4 * q);
                        }
                        def.D3[k] = c;
                        p += 4 + 24 * ns;
                    }
                }
                if (oEvt >= 0)
                {
                    int p = so + oEvt, cnt = I(p);
                    p += 16;
                    def.Evts = new EvtDef[cnt];
                    for (int k = 0; k < cnt; k++)
                    {
                        var e = new EvtDef { Id = I(p), Swing = I(p + 4), Trigger = I(p + 8), P0 = I(p + 12), P1 = I(p + 16), P2 = I(p + 20) };
                        int ns = (e.Swing >> 16) & 0x1F;   // AddScaleIDs(stMixEvtParams) masks 0x1F like the mix controls
                        e.Scales = new int[ns];
                        for (int j = 0; j < ns; j++) e.Scales[j] = I(p + 24 + 4 * j);
                        // CreateEvtMixCtls: zero stage times become 1 tick
                        int type = (e.Id >> 24) & 0xF;
                        if (type != 3)
                        {
                            if ((e.P0 & 0xFFF) == 0) e.P0 |= 1;
                            if (type == 1 && (e.P1 & 0xFFF) == 0) e.P1 |= 1;
                            if ((e.P2 & 0xFFF) == 0) e.P2 |= 1;
                        }
                        def.Evts[k] = e;
                        p += 24 + 4 * ns;
                    }
                }
                if (oSub >= 0)
                {
                    int p = so + oSub, cnt = I(p);
                    p += 16;
                    def.Subs = new ChDef[cnt];
                    for (int k = 0; k < cnt; k++)
                    {
                        var c = new ChDef { ChId = I(p), Data = I(p + 4) };
                        int ni = (c.ChId >> 16) & 0xFF;
                        c.Inputs = new int[ni];
                        for (int j = 0; j < ni; j++) c.Inputs[j] = I(p + 8 + 4 * j);
                        def.Subs[k] = c;
                        p += 8 + 4 * ni;
                    }
                }
                if (oMst >= 0)
                {
                    int p = so + oMst, cnt = I(p);
                    p += 16;
                    int pp = so + oPre;
                    def.Masters = new ChDef[cnt];
                    for (int k = 0; k < cnt; k++)
                    {
                        var c = new ChDef { ChId = I(p), Data = I(p + 4), Obj = I(p + 8) };
                        int ni = (c.ChId >> 16) & 0xFF;
                        c.Inputs = new int[ni];
                        for (int j = 0; j < ni; j++) c.Inputs[j] = I(p + 12 + 4 * j);
                        int ph = I(pp), np = ph & 0x1F;
                        c.PresetType = (ph >> 24) & 0xF;
                        c.Presets = new int[np];
                        for (int j = 0; j < np; j++) c.Presets[j] = I(pp + 4 + 4 * j);
                        def.Masters[k] = c;
                        p += 12 + 4 * ni;
                        pp += 4 + 4 * np;
                    }
                }
                m._defs[s] = def;
            }
            return m;
        }

        public bool HasState(int s) => s >= 0 && s < NumStates && _defs[s] != null;

        /// <summary>The input ids of a state's 3D controls (debug: which SFXCTL object feeds the positions).</summary>
        public IEnumerable<int> D3Inputs(int s)
        {
            if (!HasState(s)) yield break;
            foreach (var d in _defs[s].D3) yield return d.InputId;
        }

        /// <summary>
        /// NFSMixMaster::CreateMainMainMap with another map file (EAXSound::StartNewGamePlay @0x4D0D70: MAPOUTPUTDRG for drag
        /// races, 2CR / 2DR split screen): the new map's state definitions replace these and the map is rebuilt with the same
        /// instance counts. Input blocks and existing output blocks are kept, so the objects bound to them keep working.
        /// </summary>
        public void Swap(MixMap other, int[] counts)
        {
            if (other == null) return;
            MapId = other.MapId;
            for (int s = 0; s < NumStates; s++) _defs[s] = other._defs[s];
            Build(counts);
        }

        // ---------------------------------------------------------------- runtime
        sealed class Curve { public int Id, Table, Q15 = 0x7FFF, Db; public Ref Input; }
        sealed class Ctl { public CtlDef Def; public Curve Curve; public Ref[] Scales; public int Out; }
        sealed class Ctl3D
        {
            public Def3D Def; public int[] Cur; public int[] Inputs;
            public int Azimuth, DbRolloff, Q15Rolloff = 0x7FFF, Doppler;
            public float PrevDist, PrevDeltaDist;
        }
        sealed class Evt { public EvtDef Def; public Ref Trigger; public Ref[] Scales; public float Elapsed, ResetTime; public int Reset, ResetLevel, Out, QOut = 0x7FFF; }
        sealed class Chan { public ChDef Def; public Ref[] Inputs; public Ctl3D[] D3; public int Out = -10000; public int[] Outputs; }
        sealed class Inst
        {
            public int State, Index;
            public Ctl[] Ctls; public Ctl3D[] D3; public Evt[] Evts; public Chan[] Subs, Masters;
        }

        /// <summary>A pointer into the graph (GetObjectPtr): an int read from a block or a mixer item.</summary>
        sealed class Ref
        {
            public int[] Block; public int Slot;
            public Func<int> Get;
            public int Value => Block != null ? Block[Slot] : Get != null ? Get() : 0;
        }

        readonly int[] _counts = new int[NumStates];
        readonly List<Inst>[] _inst = new List<Inst>[NumStates];
        readonly List<Curve> _curves = new List<Curve>();
        readonly Dictionary<int, Curve> _curveById = new Dictionary<int, Curve>();
        readonly Dictionary<int, int[]> _objOut = new Dictionary<int, int[]>();   // sound object -> its DMIX inputs (16 ints)
        readonly Dictionary<int, int[]> _objIn = new Dictionary<int, int[]>();    // sound object -> mixer outputs (16 packed ints)
        int _camState, _prevCamState;
        float _msDelta;
        static readonly int[] Dummy = new int[16];

        /// <summary>The DMIX input block (16 ints) a sound object writes. Always valid (unused objects get a block too).</summary>
        public int[] Inputs(int objectId)
        {
            int key = objectId & unchecked((int)0xE0FFFFF0);
            if (!_objOut.TryGetValue(key, out var b)) _objOut[key] = b = new int[16];
            return b;
        }

        /// <summary>The packed output block (16 ints, 32 x 16-bit slots) for a sound object, or null if the map has none.</summary>
        public int[] Outputs(int objectId) => _objIn.TryGetValue(objectId & unchecked((int)0xE0FFFFF0), out var b) ? b : null;

        /// <summary>Marks a sound object live (slot 15 bit 0 of its output block, SETSFXID): masters only mix live objects.</summary>
        public void Attach(int objectId, bool on)
        {
            var b = Outputs(objectId);
            if (b != null) b[15] = on ? 1 : 0;
        }

        /// <summary>GetDMixOutput: slot idx of a packed block as volume (Q15), pitch (x4096), frequency or azimuth.</summary>
        public static int Output(int[] block, int idx, int type)
        {
            if (block == null) return type == 1 ? 4096 : 0;
            int v = (block[(idx >> 1) & 0xF] >> ((idx & 1) << 4)) & 0xFFFF;
            switch (type)
            {
                case 1: return MixShape.IntPitchFromCents(v);
                case 3: return v;
                default: return v & 0x7FFF;
            }
        }

        public const int Vol = 0, Pitch = 1, Freq = 2, Azim = 3, Depth = 4;

        /// <summary>NFSMixMaster::InitMixMap: instantiates every state <paramref name="counts"/>[state] times.</summary>
        public void Build(int[] counts)
        {
            _curves.Clear();
            _curveById.Clear();
            // keep the output blocks objects already hold (a map swap re-binds into them)
            var reuse = new Dictionary<int, int[]>(_objIn);
            _objIn.Clear();
            for (int s = 0; s < NumStates; s++)
            {
                _counts[s] = _defs[s] != null && counts != null && s < counts.Length ? Math.Max(0, Math.Min(31, counts[s])) : 0;
                _inst[s] = new List<Inst>();
            }
            // pass 1: items
            for (int s = 0; s < NumStates; s++)
                for (int i = 0; i < _counts[s]; i++)
                {
                    var def = _defs[s];
                    var it = new Inst { State = s, Index = i };
                    it.Ctls = new Ctl[def.Ctls.Length];
                    for (int k = 0; k < it.Ctls.Length; k++)
                    {
                        var cd = def.Ctls[k];
                        int id = (cd.InputId & unchecked((int)0xFFFF07FF)) | i << 11;
                        if (!_curveById.TryGetValue(id, out var cv))
                        {
                            _curveById[id] = cv = new Curve { Id = id, Table = (id >> 24) & 0xF };
                            _curves.Add(cv);
                        }
                        it.Ctls[k] = new Ctl { Def = cd, Curve = cv };
                    }
                    it.D3 = new Ctl3D[def.D3.Length];
                    for (int k = 0; k < it.D3.Length; k++)
                        it.D3[k] = new Ctl3D { Def = def.D3[k], Cur = def.D3[k].States.Length > 0 ? def.D3[k].States[0] : new int[6] };
                    it.Evts = new Evt[def.Evts.Length];
                    for (int k = 0; k < it.Evts.Length; k++) it.Evts[k] = new Evt { Def = def.Evts[k] };
                    it.Subs = new Chan[def.Subs.Length];
                    for (int k = 0; k < it.Subs.Length; k++) it.Subs[k] = new Chan { Def = def.Subs[k] };
                    it.Masters = new Chan[def.Masters.Length];
                    for (int k = 0; k < it.Masters.Length; k++)
                    {
                        var md = def.Masters[k];
                        int outId = (md.Obj | i << 11) & unchecked((int)0xE0FFFFF0);
                        if (!_objIn.TryGetValue(outId, out var ob))
                        {
                            if (reuse.TryGetValue(outId, out ob)) System.Array.Clear(ob, 0, 15);
                            else ob = new int[16];
                            _objIn[outId] = ob;
                        }
                        it.Masters[k] = new Chan { Def = md, Outputs = ob };
                    }
                    _inst[s].Add(it);
                }
            // pass 2: connections (ConnectMixMap)
            foreach (var cv in _curves) cv.Input = Ptr(cv.Id, false);
            for (int s = 0; s < NumStates; s++)
                foreach (var it in _inst[s])
                {
                    foreach (var c in it.Ctls) c.Scales = Expand(c.Def.Scales, s, it.Index, false);
                    foreach (var c in it.D3)
                    {
                        int id = ((c.Def.InputId | it.Index << 11) & 0x1FFFFFFF) | 0x60000000;
                        c.Inputs = Inputs(id);
                    }
                    foreach (var e in it.Evts)
                    {
                        e.Trigger = Ptr(e.Def.Trigger | it.Index << 11, false);
                        e.Scales = Expand(e.Def.Scales, s, it.Index, false);
                    }
                    foreach (var c in it.Subs) c.Inputs = Expand(c.Def.Inputs, s, it.Index, true);
                    foreach (var c in it.Masters)
                    {
                        var fixedIds = new List<int>();
                        var d3 = new List<Ctl3D>();
                        foreach (int id in c.Def.Inputs)
                        {
                            if ((id & 0xE0000000) == 0x80000000) d3.Add(Find3D(id | it.Index << 11));
                            else fixedIds.Add(id);
                        }
                        c.Inputs = Expand(fixedIds.ToArray(), s, it.Index, true);
                        c.D3 = d3.ToArray();
                    }
                }
        }

        Ref[] Expand(int[] ids, int state, int inst, bool db)
        {
            var r = new List<Ref>();
            foreach (int raw in ids)
            {
                int st = (raw >> 16) & 0xFF, id = raw & unchecked((int)0xFFFF07FF);
                if (st == state) r.Add(Ptr(id | inst << 11, db));
                else
                    for (int n = 0; n < (st < NumStates ? _counts[st] : 0); n++) r.Add(Ptr(id | n << 11, db));
            }
            return r.ToArray();
        }

        Inst InstOf(int id)
        {
            int st = (id >> 16) & 0xFF, n = (id >> 11) & 0x1F;
            if (st >= NumStates || _inst[st] == null || _inst[st].Count == 0) return null;
            return _inst[st][Math.Min(n, _inst[st].Count - 1)];
        }

        Ctl3D Find3D(int id)
        {
            var it = InstOf(id);
            int k = id & 0xFF;
            return it != null && k < it.D3.Length ? it.D3[k] : null;
        }

        /// <summary>GetObjectPtr(id, usedB).</summary>
        Ref Ptr(int id, bool db)
        {
            int type = id & unchecked((int)0xE0000000);
            var it = InstOf(id);
            int k = id & 0xFF;
            switch ((uint)type)
            {
                case 0:
                    if (it == null || k >= it.Ctls.Length) break;
                    var c = it.Ctls[k];
                    return db ? new Ref { Get = () => c.Out } : new Ref { Get = () => c.Curve.Q15 };
                case 0x20000000:
                    if (it == null) break;
                    var chans = (id & 0x10000000) != 0 ? it.Subs : it.Masters;
                    if (k >= chans.Length) break;
                    var ch = chans[k];
                    return new Ref { Get = () => ch.Out };
                case 0x40000000:
                case 0x60000000:
                    return new Ref { Block = Inputs(id), Slot = id & 0xF };
                case 0x80000000:
                    var d = Find3D(id);
                    if (d == null) break;
                    return db ? new Ref { Get = () => d.DbRolloff } : new Ref { Get = () => d.Q15Rolloff };
                case 0xA0000000:
                    if (it == null || k >= it.Evts.Length) break;
                    var e = it.Evts[k];
                    return db ? new Ref { Get = () => e.Out } : new Ref { Get = () => e.QOut };
            }
            return new Ref { Block = Dummy, Slot = 0 };
        }

        /// <summary>
        /// ProcessMixMap(dt, camera state) (PC @0x4F4270): curves, mix controls (Q15 → dB + offset, x the scale product), then
        /// Update3DMixCtls (@0x4F18E0), UpdateEvtMixCtls (@0x4F17F0: AR @0x4EA230 / ASR @0x4EA0D0 / ATR @0x4E9E70), UpdateSubChannels
        /// (@0x4E30D0), UpdateMasterChannels (@0x4E3160), MixMasterChannels (@0x4E9B30) — all compared with the PC disassembly in
        /// round 9. Not reproduced: AddScaleIDs' fill index `n + m` (several scales from a multi-instance state overlap their
        /// pointers and the shared count byte is overwritten per instance) — no control or event in the four PC maps has more
        /// than one scale with an outside multi-instance state, so the port's plain expansion gives the same graph.
        /// </summary>
        public void Process(float dt, int camState)
        {
            _msDelta = dt * 1000f;
            _prevCamState = _camState;
            _camState = camState;
            foreach (var cv in _curves)
            {
                cv.Q15 = MixShape.Curve(cv.Table, cv.Input.Value, false);
                cv.Db = MixShape.DbFromQ15(cv.Q15);
            }
            for (int s = 0; s < NumStates; s++)
                foreach (var it in _inst[s])
                    foreach (var c in it.Ctls)
                    {
                        int q = ((0x7FFF - c.Curve.Q15) * c.Def.Ratio) >> 15;
                        int o = MixShape.DbFromQ15(0x7FFF - q) + c.Def.Offset;
                        int scale = 0x7FFF;
                        foreach (var r in c.Scales) scale = (r.Value * scale) >> 15;
                        c.Out = (scale * o) >> 15;
                    }
            for (int s = 0; s < NumStates; s++)
                foreach (var it in _inst[s])
                    foreach (var c in it.D3) Update3D(c, s);
            for (int s = 0; s < NumStates; s++)
                foreach (var it in _inst[s])
                    foreach (var e in it.Evts) UpdateEvent(e);
            for (int s = 0; s < NumStates; s++)
                foreach (var it in _inst[s])
                    foreach (var c in it.Subs)
                    {
                        int o = 0;
                        foreach (var r in c.Inputs) o += r.Value;
                        int up = (c.Def.Data >> 16) & 0x7FFF, dn = c.Def.Data | unchecked((int)0xFFFF0000);
                        c.Out = o > up ? up : o < dn ? dn : o;
                    }
            for (int s = 0; s < NumStates; s++)
                foreach (var it in _inst[s])
                    foreach (var c in it.Masters)
                    {
                        if ((c.Outputs[15] & 1) == 0) { c.Out = -10000; continue; }
                        int o = (short)(c.Def.Data >> 16);   // PC reads the high half (0x4E3190)
                        foreach (var r in c.Inputs) o += r.Value;
                        c.Out = o;
                    }
            for (int s = 0; s < NumStates; s++)
                foreach (var it in _inst[s])
                    foreach (var c in it.Masters) MixMaster(c);
        }

        static void Put(int[] block, int idx, int v)
        {
            int shift = (idx & 1) << 4, slot = (idx & 0x1F) >> 1;
            int mask = 0xFFFF << ((((idx & 0x1F) + 1) & 1) << 4);
            block[slot] = (block[slot] & mask) | (v & 0xFFFF) << shift;
        }

        void MixMaster(Chan c)
        {
            int type = c.Def.PresetType;
            var outs = c.Outputs;
            if ((outs[15] & 1) == 0)
            {
                if (c.Def.Presets.Length == 0) return;
                int o = type == 1 ? 0 : type == 2 ? 25000 : -10000;
                Put(outs, c.Def.Presets[0] >> 26, o);
                return;
            }
            int n = Math.Min(c.Def.Presets.Length, 0xF);
            for (int p = 0; p < n; p++)
            {
                int pr = c.Def.Presets[p];
                int idx = pr >> 26, i3 = (pr >> 21) & 0x1F;
                short vol = (short)pr;
                int o;
                if (c.D3.Length > 0 && i3 < c.D3.Length && c.D3[i3] != null)
                {
                    var d = c.D3[i3];
                    if (pr < 0) o = d.Azimuth & 0xFFFF;
                    else
                        switch (type)
                        {
                            case 0:
                            case 4:
                                o = Math.Max(-10000, Math.Min(0, d.DbRolloff + c.Out + vol));
                                o = MixShape.Q15FromDb(o);
                                break;
                            case 1:
                                o = d.Doppler + c.Out + vol;
                                if (o > 0x960) o = 0x960;
                                if (o < -0x12C0) o = 0;
                                break;
                            case 2:
                                o = Math.Max(-10000, Math.Min(0, c.Out + vol));
                                break;
                            default:
                                o = c.Out;
                                break;
                        }
                }
                else
                {
                    o = c.Out + vol;
                    switch (type)
                    {
                        case 0:
                        case 4:
                            o = MixShape.Q15FromDb(Math.Max(-10000, Math.Min(0, o)));
                            break;
                        case 1:
                            o = Math.Max(-0x12C0, Math.Min(0x960, o));
                            break;
                        case 2:
                            o = (int)(MixShape.PitchFromCents(Math.Max(-10000, Math.Min(0, o))) * 25000f);
                            break;
                        default:
                            o = Math.Max(0, Math.Min(25000, o));
                            break;
                    }
                }
                Put(outs, idx, o);
            }
        }

        const float DopplerSmoothing = 0.2f;

        void Update3D(Ctl3D c, int state)
        {
            if (_camState != _prevCamState || c.Cur == null)
            {
                int[] found = null;
                foreach (var sp in c.Def.States) if (((sp[0] >> 24) & 0xF) == _camState) { found = sp; break; }
                if (found == null) foreach (var sp in c.Def.States) if (((sp[0] >> 24) & 0xF) == 0) { found = sp; break; }
                if (found != null) c.Cur = found;
            }
            var inp = c.Inputs;
            var p = c.Cur;
            if ((inp[15] & 1) == 0 || p == null)
            {
                c.DbRolloff = -10000;
                c.Q15Rolloff = 0;
                c.Azimuth = 0;
                c.Doppler = 0;
                return;
            }
            int nid = p[0], tables = p[1];
            int distType = (nid >> 12) & 0xF, azimType = (nid >> 8) & 0xF;
            float dist = distType == 0 ? inp[1] * 0.01f : distType == 1 ? inp[0] * 0.01f : -1.00000006f;
            int azim = azimType == 0 ? inp[3] : azimType == 1 ? inp[2] : 0;
            azim &= 0xFFFF;
            c.Azimuth = azim;
            int quad = (azim >> 14) & 3, q1, q2, mn0, mx0, mn1, mx1, avg;
            int Q(int k) => p[2 + (k & 3)];
            switch (quad)
            {
                case 0: q1 = (tables >> 28) & 0xF; q2 = (tables >> 16) & 0xF; avg = azim; break;
                case 1: q1 = (tables >> 16) & 0xF; q2 = (tables >> 24) & 0xF; avg = azim - 0x4000; break;
                case 2: q1 = (tables >> 24) & 0xF; q2 = (tables >> 20) & 0xF; avg = azim - 0x8000; break;
                default: q1 = (tables >> 20) & 0xF; q2 = (tables >> 28) & 0xF; avg = azim - 0xC000; break;
            }
            mn0 = Q(quad) & 0x7FFF; mx0 = (Q(quad) >> 16) & 0x7FFF;
            mn1 = Q(quad + 1) & 0x7FFF; mx1 = (Q(quad + 1) >> 16) & 0x7FFF;
            float d0 = dist, d1 = dist;
            if (d0 > mx0 && d0 > mx1)
            {
                c.DbRolloff = -10000;
                c.Q15Rolloff = 0;
                c.Doppler = 0;
                return;
            }
            d0 = Math.Max(mn0, Math.Min(mx0, d0));
            d1 = Math.Max(mn1, Math.Min(mx1, d1));
            d0 = mx0 > mn0 ? (d0 - mn0) / (mx0 - mn0) : 0;
            d1 = mx1 > mn1 ? (d1 - mn1) / (mx1 - mn1) : 0;
            c.Q15Rolloff = MixShape.AzimShape(q1, q2, (int)(d0 * 32767f), (int)(d1 * 32767f), avg);
            c.DbRolloff = MixShape.DbFromQ15(c.Q15Rolloff);
            int vs = tables & 0xFFFF;
            if (vs != 0)
            {
                float cents = 0, cur, vel;
                if (_camState < 3 || _camState == 4)
                {
                    cur = inp[0] * 0.01f;
                    vel = inp[13] * 0.01f;
                    if (inp[15] < 0) inp[15] &= 0x7FFFFFFF;
                    else
                    {
                        float div = vs + vel;
                        if (div <= 0) div = vs;
                        cents = MixShape.CentsFromPitch(vs / div);
                    }
                }
                else
                {
                    cur = inp[1] * 0.01f;
                    vel = inp[14] * 0.01f;
                    if ((inp[15] & 0x40000000) == 0)
                    {
                        float div = vs + vel;
                        if (div <= 0) div = vs;
                        cents = MixShape.CentsFromPitch(vs / div);
                    }
                    else inp[15] &= unchecked((int)0xBFFFFFFF);
                }
                c.PrevDeltaDist = Math.Abs(cur - c.PrevDist);
                c.PrevDist = cur;
                c.Doppler += (int)((cents - c.Doppler) * DopplerSmoothing);
            }
        }

        void UpdateEvent(Evt e)
        {
            int trig = e.Trigger.Value;
            if (e.Elapsed == 0 && trig == 0)
            {
                e.Out = 0;
                e.QOut = 0x7FFF;
                e.Reset = 0;
                e.ResetLevel = -10000;
                return;
            }
            e.Elapsed += _msDelta;
            var d = e.Def;
            int swing = d.Swing & 0xFFFF;
            if ((d.Swing & 0x8000) != 0) swing |= unchecked((int)0xFFFF0000);
            float t0 = (d.P0 & 0xFFF) * 16.66667f, t1 = (d.P1 & 0xFFF) * 16.66667f, t2 = (d.P2 & 0xFFF) * 16.66667f;
            int c0 = (d.P0 >> 12) & 0xF, c2 = (d.P2 >> 12) & 0xF;
            int Stage(int curve, int ndt) => swing < 0 ? MixShape.Curve(curve, ndt, false) : 0x7FFF - MixShape.Curve(curve, ndt, false);
            switch ((d.Id >> 24) & 0xF)
            {
                case 0: // AR
                    if (e.Elapsed < t0) e.QOut = Stage(c0, (int)(e.Elapsed * 32767f / t0));
                    else if (e.Elapsed - t0 < t2) e.QOut = Stage(c2, (int)(32767f - (e.Elapsed - t0) * 32767f / t2));
                    else { e.Elapsed = 0; e.QOut = 0x7FFF; }
                    e.Out = (int)((32767f - e.QOut) / 32767f * swing);
                    break;
                case 1: // ASR
                    if (e.Elapsed < t0) e.QOut = Stage(c0, (int)(e.Elapsed * 32767f / t0));
                    else if (e.Elapsed - t0 > t1)
                    {
                        if (e.Elapsed - (t0 + t1) < t2) e.QOut = Stage(c2, (int)(32767f - (e.Elapsed - (t0 + t1)) * 32767f / t2));
                        else { e.Elapsed = 0; e.QOut = 0x7FFF; }
                    }
                    else e.QOut = 0;
                    e.Out = (int)((32767f - e.QOut) / 32767f * swing);
                    break;
                case 2: // ATR
                    UpdateAtr(e, swing, t0, t2, c0, c2, trig, Stage);
                    break;
                default: // LFO (stripped in the game)
                    break;
            }
            if (e.Scales.Length > 0)
            {
                int o = e.Out;
                foreach (var r in e.Scales) o = (o * r.Value) >> 15;
                e.Out = o;
            }
        }

        static void UpdateAtr(Evt e, int swing, float t0, float t2, int c0, int c2, int trig, Func<int, int, int> stage)
        {
            if (e.Elapsed < t0)
            {
                if (trig == 0)
                {
                    e.Reset = 1;
                    e.ResetLevel = e.Out;
                    e.Elapsed = t0;
                    return;
                }
                e.QOut = stage(c0, (int)(e.Elapsed * 32767f / t0));
            }
            else if (trig == 0)
            {
                if (e.ResetTime == 0) e.ResetTime = e.Elapsed;
                if (e.Elapsed - e.ResetTime > t2)
                {
                    e.Reset = 0;
                    e.ResetLevel = 0;
                    e.Elapsed = 0;
                    e.ResetTime = 0;
                    e.QOut = 0x7FFF;
                    return;
                }
                e.QOut = stage(c2, (int)(32767f - (e.Elapsed - e.ResetTime) * 32767f / t2));
            }
            else
            {
                if (trig == 1 && e.ResetTime != 0)
                {
                    e.Reset = 1;
                    e.Elapsed = 0;
                    e.ResetLevel = e.Out;
                    e.QOut = 0x7FFF;
                    e.ResetTime = 0;
                    return;
                }
                e.Reset = 0;
                e.ResetLevel = 0;
                e.ResetTime = 0;
                e.Elapsed = t0;
                e.QOut = 0;
            }
            float r = (32767f - e.QOut) / 32767f;
            if (e.Reset != 0)
                e.Out = trig == 0 ? (int)(r * e.ResetLevel) : (int)(r * (swing - e.ResetLevel)) + e.ResetLevel;
            else e.Out = (int)(r * swing);
        }

        /// <summary>Debug: every master channel's current output (dB) for an object, by preset slot.</summary>
        public IEnumerable<(int slot, int type, int db)> Describe(int objectId)
        {
            int key = objectId & unchecked((int)0xE0FFFFF0);
            for (int s = 0; s < NumStates; s++)
                if (_inst[s] != null)
                    foreach (var it in _inst[s])
                        foreach (var c in it.Masters)
                            if (((c.Def.Obj | it.Index << 11) & unchecked((int)0xE0FFFFF0)) == key)
                                foreach (int pr in c.Def.Presets) yield return (pr >> 26 & 0x1F, c.Def.PresetType, c.Out + (short)pr);
        }
    }
}
