using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// One event of an event list (CARP::EventStaticData {u32 event id, u32 size, u32 data offset from this record,
    /// pad}). The id is the Jenkins lookup2 hash of the event class name (e.g. "ENISCarRoll"); the static data starts
    /// with the id again and continues with the fields of the class's StaticData (decomp Generated/Events/*.hpp,
    /// field offsets from +4). String fields ("const char *") are tag references to 'cS' records of the pack.
    /// </summary>
    public sealed class SeqEvent
    {
        public uint Id;
        public string Name;     // known class name or null
        internal EventSeqPack Pack;
        internal int Data;      // offset of the static data in Pack.Bytes
        public int Size;

        public float F(int offset) => Data + offset + 4 <= Pack.Bytes.Length ? Chunks.F32(Pack.Bytes, Data + offset) : 0;
        public uint U(int offset) => Data + offset + 4 <= Pack.Bytes.Length ? Chunks.U32(Pack.Bytes, Data + offset) : 0;
        public int I(int offset) => (int)U(offset);
        public string S(int offset) => Pack.String(U(offset));

        public override string ToString() => Name ?? Id.ToString("X8");
    }

    /// <summary>
    /// The event sequencer data of a NIS (chunk 0x3B811: LoaderEventSequence, decomp Main/Common/EventSequencer.cpp).
    /// It is a serialized UGroup 'CARP' {u32 tag, u32 flags (bit1 embedded, bits 5.. group count), u32 data count,
    /// u32 offset in 16-byte units} followed by UData records {u32 tag (type << 16 | index for indexed records),
    /// u32 flags (bit0 indexed, bit1 embedded, bits 8.. size), u32 count/name hash, u32 offset from the record}.
    /// Record types (two-character, high half of the tag): 'eE' engine, 'eS' system, 'eT' state, 'eA' action,
    /// 'el' event list, 'cS' static string, 'iN' engine name, plus the 'Strs' string table. References between
    /// records are such tags (CARP::ResolveTagReferences turns them into pointers at load).
    /// </summary>
    public sealed class EventSeqPack
    {
        public const ushort TypeEngine = 0x6545, TypeSystem = 0x6553, TypeState = 0x6554, TypeAction = 0x6541,
            TypeEventList = 0x656C, TypeString = 0x6353, TypeName = 0x694E;

        public byte[] Bytes;
        readonly Dictionary<uint, int> _records = new Dictionary<uint, int>();   // tag -> data offset
        public readonly List<EventSeqEngineData> Engines = new List<EventSeqEngineData>();

        public int Record(uint tag) => _records.TryGetValue(tag, out int o) ? o : -1;
        public string String(uint tag) { int o = Record(tag); return o < 0 ? null : Chunks.CString(Bytes, o, 256); }

        public static EventSeqPack Read(byte[] d, int start, int end)
        {
            int g = start;
            while (g + 16 <= end && !(d[g] == (byte)'P' && d[g + 1] == (byte)'R' && d[g + 2] == (byte)'A' && d[g + 3] == (byte)'C')) g++;
            if (g + 16 > end) return null;
            var p = new EventSeqPack { Bytes = new byte[end - g] };
            Buffer.BlockCopy(d, g, p.Bytes, 0, end - g);
            var b = p.Bytes;
            uint flags = Chunks.U32(b, 4);
            int groups = (int)(flags >> 5), count = Chunks.I32(b, 8), arr = Chunks.I32(b, 12) * 16;
            int data0 = arr + groups * 16;
            for (int i = 0; i < count && data0 + i * 16 + 16 <= b.Length; i++)
            {
                int u = data0 + i * 16;
                uint tag = Chunks.U32(b, u), uf = Chunks.U32(b, u + 4);
                if ((uf & 2) == 0) continue;   // only embedded records occur in the NIS packs
                int o = u + Chunks.I32(b, u + 12);
                if (o >= 0 && o < b.Length) p._records[tag] = o;
            }
            foreach (var kv in p._records)
                if ((kv.Key >> 16) == TypeEngine)
                {
                    var e = EventSeqEngineData.Read(p, kv.Value);
                    if (e != null) p.Engines.Add(e);
                }
            return p;
        }

        // ---- record readers (CARP.h layouts) ----

        internal EventSeqSystemData System(uint tag)
        {
            int o = Record(tag);
            if (o < 0) return null;
            var s = new EventSeqSystemData { InitialState = Chunks.U32(Bytes, o), DefaultState = Chunks.U32(Bytes, o + 4) };
            int n = Chunks.I32(Bytes, o + 8);
            for (int i = 0; i < n; i++)
                s.States[Chunks.U32(Bytes, o + 12 + i * 4)] = State(Chunks.U32(Bytes, o + 12 + n * 4 + i * 4));
            return s;
        }

        EventSeqStateData State(uint tag)
        {
            int o = Record(tag);
            if (o < 0) return null;
            var st = new EventSeqStateData { HasFilter = Chunks.U32(Bytes, o + 4) != 0 };
            int n = Chunks.I32(Bytes, o);
            for (int i = 0; i < n; i++)
            {
                int r = o + 8 + n * 4 + i * 8;
                st.Responses[Chunks.U32(Bytes, o + 8 + i * 4)] = (Chunks.U32(Bytes, r), Action(Chunks.U32(Bytes, r + 4)));
            }
            return st;
        }

        EventSeqAction Action(uint tag)
        {
            int o = Record(tag);
            if (o < 0) return null;
            var a = new EventSeqAction { Name = Chunks.U32(Bytes, o), Duration = Chunks.F32(Bytes, o + 12) };
            int nt = Chunks.I32(Bytes, o + 4), nti = Chunks.I32(Bytes, o + 8);
            a.Tags = new uint[nt];
            a.Times = new float[nti];
            a.Lists = new List<SeqEvent>[nt + nti];
            for (int i = 0; i < nt; i++) a.Tags[i] = Chunks.U32(Bytes, o + 16 + i * 4);
            for (int i = 0; i < nti; i++) a.Times[i] = Chunks.F32(Bytes, o + 16 + nt * 4 + i * 4);
            for (int i = 0; i < nt + nti; i++) a.Lists[i] = EventList(Chunks.U32(Bytes, o + 16 + (nt + nti) * 4 + i * 4));
            return a;
        }

        List<SeqEvent> EventList(uint tag)
        {
            var list = new List<SeqEvent>();
            int o = Record(tag);
            if (o < 0) return list;
            int n = Chunks.I32(Bytes, o);
            for (int i = 0; i < n && o + 16 + i * 16 + 16 <= Bytes.Length; i++)
            {
                int e = o + 16 + i * 16;
                var ev = new SeqEvent { Id = Chunks.U32(Bytes, e), Size = Chunks.I32(Bytes, e + 4), Data = e + Chunks.I32(Bytes, e + 8), Pack = this };
                ev.Name = SeqEventNames.Name(ev.Id);
                list.Add(ev);
            }
            return list;
        }
    }

    public sealed class EventSeqEngineData
    {
        public string Name;
        public uint[] SystemIds = Array.Empty<uint>();
        public EventSeqSystemData[] Systems = Array.Empty<EventSeqSystemData>();

        internal static EventSeqEngineData Read(EventSeqPack p, int o)
        {
            var b = p.Bytes;
            int n = Chunks.I32(b, o + 4);
            if (n < 0 || n > 64) return null;
            var e = new EventSeqEngineData { Name = p.String(Chunks.U32(b, o)), SystemIds = new uint[n], Systems = new EventSeqSystemData[n] };
            for (int i = 0; i < n; i++)
            {
                e.SystemIds[i] = Chunks.U32(b, o + 8 + i * 4);
                e.Systems[i] = p.System(Chunks.U32(b, o + 8 + n * 4 + i * 4));
            }
            return e;
        }
    }

    public sealed class EventSeqSystemData
    {
        public uint InitialState, DefaultState;
        public readonly Dictionary<uint, EventSeqStateData> States = new Dictionary<uint, EventSeqStateData>();
        public EventSeqStateData Find(uint id) => States.TryGetValue(id, out var s) ? s : null;
    }

    public sealed class EventSeqStateData
    {
        public bool HasFilter;   // StimulusFilter (query expression); none of the NIS packs has one [checked by harness nisevents]
        public readonly Dictionary<uint, (uint endState, EventSeqAction action)> Responses = new Dictionary<uint, (uint, EventSeqAction)>();
    }

    /// <summary>CARP::EventSeqAction {name, tag count, time count, duration (&lt; 0: endless), tags[], times[], lists[tags + times]}.</summary>
    public sealed class EventSeqAction
    {
        public uint Name;
        public float Duration;
        public uint[] Tags;
        public float[] Times;
        public List<SeqEvent>[] Lists;
    }

    /// <summary>Stimulus and tag names: Jenkins lookup2 hashes of the strings (EventSequencer::StringToID = stringhash32).</summary>
    public static class SeqIds
    {
        public static readonly uint Create = AttribDatabase.Hash("create"), Trigger = AttribDatabase.Hash("trigger"),
            Start = AttribDatabase.Hash("start"), End = AttribDatabase.Hash("end"), Stop = AttribDatabase.Hash("stop"),
            Pause = AttribDatabase.Hash("pause"), Resume = AttribDatabase.Hash("resume"), Unload = AttribDatabase.Hash("unload"),
            StartStimulus = 0xA6A56EA1,   // NISActivity::StartEvents: ProcessStimulus(0xa6a56ea1 = "START")
            GlobalSystem = 0x8DA18577;    // NISActivity::SetDynamicData: "GLOBAL" gets the activity handle
    }

    /// <summary>The event classes the NIS packs use, by id (all 34 ids of the retail packs resolve; harness nisevents).</summary>
    public static class SeqEventNames
    {
        static Dictionary<uint, string> _names;
        public static readonly string[] Known =
        {
            "ENISPixelate", "ENISBurnout", "ENISCarPitch", "ENISTimeOfDay", "ENISCarRoll", "ENISLights", "ENISCarShake",
            "ENISCopCarDoors", "ENISNitro", "ENISOverlayMessage", "ENISPlayEffect", "ENISBrakelock", "ENISReattach", "ENISDetach",
            "ENISFakeFar", "ENISNeutralRev", "ENISMotionBlur", "ENISNukeSmack", "ENISRain", "ENISAeroDynamics", "ENISHideCharacter",
            "EPlayObjectEffect", "ECameraPhotoFinish", "ENISSteering", "ENISCarDamageReset", "ENISVisualLook", "ENISWolrdGeometry",
            "ECellCall", "EStopObjectEffect", "ESpawnExplosion", "ENISRoadNoise", "ENISScreenFlash", "ENISStopEffects", "EDDaySpeech",
            "ENISCopLights", "ENISDetail", "ENISFreeze", "ENISWorldAnimTrigger", "EPlayEndNIS", "EPlayRaceNIS", "ETriggerMomentNIS",
            "EFadeScreenOn", "EFadeScreenOff",
        };

        public static string Name(uint id)
        {
            if (_names == null)
            {
                var n = new Dictionary<uint, string>();
                foreach (var s in Known) n[AttribDatabase.Hash(s)] = s;
                _names = n;
            }
            return _names.TryGetValue(id, out var name) ? name : null;
        }
    }

    /// <summary>
    /// EventSequencer::Engine + System, ported from decomp Main/Common/EventSequencer.cpp with the PC
    /// System::ProcessStimulus (0x645A40: an idle system invokes the stimulus at once; a busy one queues it, QUEUE_SHALLOW
    /// only into an empty queue, QUEUE_ALLOW while the 4-entry queue has room). Times are the caller's clock in seconds
    /// (Sim::GetTime for the NIS). Fired event lists go to <see cref="OnEvent"/> with the firing system's id.
    /// </summary>
    public sealed class EventSeqEngine
    {
        public enum QueueMode { Disable, Shallow, Allow, Flush, Abort }

        public readonly EventSeqEngineData Data;
        public readonly SystemRt[] Systems;
        public Action<uint, SeqEvent> OnEvent;
        public float Rate = 1f;
        float _last = float.NegativeInfinity;

        public EventSeqEngine(EventSeqEngineData data, float time, float rate = 1f)
        {
            Data = data;
            Rate = rate;
            Systems = new SystemRt[data.Systems.Length];
            for (int i = 0; i < Systems.Length; i++) Systems[i] = new SystemRt(this, data.SystemIds[i], data.Systems[i], time, rate);
            ProcessStimulus(SeqIds.Create, time, QueueMode.Allow);   // EventSequencer::Create
        }

        public bool AnySystemInAction() { foreach (var s in Systems) if (s.InAction) return true; return false; }

        public bool ProcessStimulus(uint stimulus, float time, QueueMode mode)
        {
            bool r = false;
            foreach (var s in Systems) r |= s.ProcessStimulus(stimulus, time, mode);
            return r;
        }

        public bool FireEventTag(uint tag) { bool r = false; foreach (var s in Systems) r |= s.FireEventTag(tag); return r; }
        public void Reset(float time) { _last = time; foreach (var s in Systems) s.Reset(time, Rate); }
        public void Complete(float time, bool flush) { foreach (var s in Systems) s.Complete(time, flush); }
        public void Stop(float time, bool flush) { foreach (var s in Systems) s.Stop(time, flush); }
        public void Pause(float time) { foreach (var s in Systems) s.Pause(time); }
        public void Resume(float time) { foreach (var s in Systems) s.Resume(time); }

        /// <summary>EventSequencer::Update: every system in action advances (only when the clock moved forward).</summary>
        public void Update(float time)
        {
            if (time <= _last) return;
            foreach (var s in Systems) if (s.InAction) s.Update(time);
            _last = time;
        }

        public sealed class SystemRt
        {
            readonly EventSeqEngine _engine;
            public readonly uint Id;
            readonly EventSeqSystemData _sys;
            EventSeqStateData _state;
            EventSeqAction _action;
            float _actionRate, _actionTime, _actionLast, _pausedAt, _queueDuration;
            public uint CurrentState { get; private set; }
            uint _endState, _queueEndState;
            readonly uint[] _queue = new uint[4];

            internal SystemRt(EventSeqEngine engine, uint id, EventSeqSystemData sys, float time, float rate)
            {
                _engine = engine;
                Id = id;
                _sys = sys;
                InternalReset(time, rate);
            }

            public bool InAction => _action != null;
            public bool Paused => _pausedAt >= 0;
            public EventSeqAction Action => _action;
            public float ActionTime => _actionTime;

            EventSeqStateData FindState(uint id) => _sys?.Find(id);

            void SetState(float time, uint state)
            {
                if (_sys == null) return;
                if (InAction) Stop(time, true);
                _state = FindState(state);
                if (_state == null) { _state = FindState(_sys.DefaultState); CurrentState = _sys.DefaultState; }
                else CurrentState = state;
            }

            void InternalReset(float time, float rate)
            {
                _action = null;
                _pausedAt = -1;
                _actionTime = 0;
                _actionRate = rate;
                _endState = _queueEndState = 0;
                _actionLast = -1;
                _queueDuration = 0;
                if (_sys == null) return;
                _state = FindState(_sys.InitialState);
                if (_state == null) { _state = FindState(_sys.DefaultState); CurrentState = _sys.DefaultState; }
                else CurrentState = _sys.InitialState;
                Array.Clear(_queue, 0, 4);
            }

            public void Reset(float time, float rate)
            {
                InternalReset(time, rate);
                ProcessStimulus(SeqIds.Create, time, QueueMode.Allow);
            }

            void Flush()
            {
                Array.Clear(_queue, 0, 4);
                _queueEndState = _endState;
                _queueDuration = InAction ? _action.Duration : 0;
            }

            public void Stop(float time, bool flush) => TerminateAction(SeqIds.Stop, time, flush);

            public void Complete(float time, bool flush)
            {
                if (InAction) FireTimedEvents(_actionLast, _action.Duration);
                TerminateAction(SeqIds.Stop, time, flush);
            }

            public void Pause(float time)
            {
                if (Paused) return;
                _pausedAt = time;
                FireEventTag(SeqIds.Pause);
            }

            public void Resume(float time)
            {
                if (!Paused) return;
                _actionLast += time - _pausedAt;
                _pausedAt = -1;
                FireEventTag(SeqIds.Resume);
            }

            public bool ProcessStimulus(uint stimulus, float time, QueueMode mode)
            {
                if (_sys == null) return false;
                if (mode == QueueMode.Abort) { if (InAction) Stop(time, true); }
                else if (mode == QueueMode.Flush && _queue[0] != 0) Flush();
                if (!InAction) return InvokeStimulus(stimulus, time);
                if (mode == QueueMode.Shallow) { if (_queue[0] != 0) return false; }
                else if (mode == QueueMode.Allow) { if (_queue[3] != 0) return false; }
                else return false;
                int entry = 0;
                while (_queue[entry] != 0) entry++;
                uint currentEnd = _queue[0] != 0 ? _queueEndState : _endState;
                var endState = FindState(currentEnd) ?? FindState(_sys.DefaultState);
                if (endState == null || !endState.Responses.TryGetValue(stimulus, out var resp)) return false;
                _queue[entry] = stimulus;
                _queueEndState = resp.endState;
                _queueDuration = resp.action != null && resp.action.Duration >= 0 ? _queueDuration + resp.action.Duration : float.PositiveInfinity;
                return true;
            }

            public bool FireEventTag(uint tag)
            {
                if (!InAction) return false;
                int i = Array.IndexOf(_action.Tags, tag);
                if (i < 0) return false;
                Fire(_action.Lists[i]);
                return true;
            }

            /// <summary>System::Update (0x644BC0): advance the action clock, fire the timed lists passed, end the action at its duration.</summary>
            internal void Update(float time)
            {
                if (!(_actionLast < time) || Paused || _action == null) return;
                float prev = _actionTime;
                if (_actionLast < 0) _actionTime = 0;
                else _actionTime += (time - _actionLast) * _actionRate;
                FireTimedEvents(prev, _actionTime);
                if (_action != null && _action.Duration >= 0 && _actionTime >= _action.Duration) TerminateAction(SeqIds.End, time, false);
                else _actionLast = time;
            }

            void TerminateAction(uint tag, float time, bool flush)
            {
                if (flush) Flush();
                if (InAction)
                {
                    FireEventTag(tag);
                    _queueDuration = _action.Duration >= 0 ? _queueDuration - _action.Duration : 0;
                    _action = null;
                    _pausedAt = -1;
                    SetState(time, _endState);
                }
                if (_queue[0] != 0)
                {
                    uint next = _queue[0];
                    _queue[0] = _queue[1]; _queue[1] = _queue[2]; _queue[2] = _queue[3]; _queue[3] = 0;
                    InvokeStimulus(next, time);
                }
                else _queueDuration = 0;
            }

            bool InvokeStimulus(uint stimulus, float time)
            {
                // ExecuteFilter: no NIS state carries a StimulusFilter, the stimulus passes unchanged
                if (_state == null || !_state.Responses.TryGetValue(stimulus, out var resp) || resp.action == null) return false;
                _endState = resp.endState;
                _action = resp.action;
                _actionTime = -1;
                _actionLast = -1;
                FireEventTag(SeqIds.Start);
                Update(time);
                return true;
            }

            /// <summary>System::FireTimedEvents (0x644350): the lists whose time is in (start, end] (a list exactly at start fires once, on entry).</summary>
            void FireTimedEvents(float start, float end)
            {
                var a = _action;
                if (a == null) return;
                int i = LowerBound(a.Times, start);
                if (i >= a.Times.Length) return;
                if (a.Times[i] == start) i++;
                for (; i < a.Times.Length && a.Times[i] <= end; i++)
                {
                    Fire(a.Lists[a.Tags.Length + i]);
                    if (_action != a) return;
                }
            }

            static int LowerBound(float[] t, float v)
            {
                int i = 0;
                while (i < t.Length && t[i] < v) i++;
                return i;
            }

            void Fire(List<SeqEvent> list)
            {
                if (list == null || _engine.OnEvent == null) return;
                foreach (var e in list) _engine.OnEvent(Id, e);
            }
        }
    }
}
