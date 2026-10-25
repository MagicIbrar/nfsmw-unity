using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NfsMw.Core;

namespace NfsMw.Audio
{
    /// <summary>The speaker behind a scheduled speech event (EAXCharacter as Speech::Manager sees it).</summary>
    public interface ISpeechActor
    {
        int SpeakerID { get; }
        /// <summary>Metres to the player (EAXCharacter::GetDistance), for the CullingRange test.</summary>
        float Distance { get; }
        bool HasLOS { get; }
        /// <summary>IRenderable::InView of the actor's vehicle, for OnScreenOnly events.</summary>
        bool InView { get; }
    }

    /// <summary>Plays the stitched takes of an event (GameSpeech's stream channel + stitch chirper).</summary>
    public interface ISpeechOutput
    {
        /// <summary>Starts the event's decoded takes back to back (GameSpeech::IssuePlayback).</summary>
        void Begin(ScheduledSpeech e);
        /// <summary>Cuts playback (GameSpeech::ClearCompletedRequests).</summary>
        void Stop();
        /// <summary>The stream still has outstanding requests (playing, or paused mid-event).</summary>
        bool Playing { get; }
        /// <summary>Seconds of the current event played so far.</summary>
        double PlayedTime { get; }
        /// <summary>GameSpeech::RadioChirp: 0 at the start of a transmission, 1 at its end.</summary>
        void Chirp(int type);
    }

    /// <summary>One queued speech event (ScheduledSpeechEvent, EAXSound/Stream/SpeechModule.hpp) with its Csis arguments.</summary>
    public sealed class ScheduledSpeech
    {
        public int Id;
        public string Name;
        public SpeechEventInfo Info;
        public ISpeechActor Actor;
        public Dictionary<string, int> Args;
        /// <summary>speech.priority, +100 for interrupt events (ScheduleSpeechPartII).</summary>
        public int Priority;
        public double EntryTime, FinishTime;
        /// <summary>flags &amp; 2: the InitDelay has not elapsed yet.</summary>
        public bool Delayed;
        public long Seq;
        /// <summary>The event's slot in gSpeechCache's event pool (its address order: the game std::sorts mEvents[1] and
        /// mEvents[3] by pointer, SpeechManager.cpp:658/719, PC @0x70FBC0).</summary>
        public int Slot = -1;
        /// <summary>Takes chosen by SPCH_Choose (IndirectSpeechEvent); null until chosen.</summary>
        public List<SpeechSample> Samples;
        /// <summary>Decoded takes (mono), filled off the main thread; a null entry = a take that failed to load.</summary>
        public float[][] Pcm;
        public int SampleRate = 24000;
        internal int Decoded;
        internal bool Prepared;
        public bool Ready => Samples != null && Volatile.Read(ref Decoded) >= Samples.Count;
        public int Arg(string name, int fallback = 0) => Args != null && Args.TryGetValue(name, out int v) ? v : fallback;
        public int SpeakerId => Arg("speaker_id", Actor?.SpeakerID ?? 0);
        public override string ToString() => $"{Name} spk {SpeakerId} prio {Priority}";
    }

    /// <summary>
    /// The police speech queue and playback scheduler: Speech::Manager (EAXSound/Stream/SpeechManager.cpp) with the
    /// playback half of Speech::GameSpeech (GameSpeech.cpp). Events go through four lists like mEvents[4]: 0 new or
    /// deferred, 1 filtered (sentence to choose), 2 chosen and waiting to play, 3 interrupts.
    /// <list type="bullet">
    /// <item><see cref="Schedule"/> = ScheduleSpeechPartII: a queued event with the same id only takes the new actor
    /// and data; events said before may drop out with the pursuit-length probability (CalcProbPlayback, speechtune
    /// SpeechDropoffRamp) unless DoNotDropout; RecallList events are pulled; priority +100 for interrupts.</item>
    /// <item><see cref="Deduce"/> (SoundAI's 4 Hz observation task) = PreValidate / ServiceFilteredEvents /
    /// ServiceInterruptEvents: the sentence is chosen here (SpeechScript.Choose = SPCH_Choose + SPCH_Play) and its takes
    /// start decoding off the main thread (the game's sample requests / speech cache).</item>
    /// <item><see cref="Update"/> (every frame) = GameSpeech::Update + CheckNextEvent + GetNextEvent + PostValidate:
    /// the next prepared event by priority, entry time and arrival (sort_nested_priority) that passes expiry,
    /// MaxPlayback, DeadAir, Interval, CullingRange, DepFollow/BackTime, OnScreenOnly, reqLOS, heat and player-speed
    /// windows; interrupts (priority &gt; 100) cut the current event. History is touched 3 s into playback or at its
    /// end; EnforceDeadAir holds the channel silent after an event.</item>
    /// </list>
    /// Times are game seconds (WorldTimer), set through <see cref="Now"/> by the owner.
    /// </summary>
    public sealed class CopSpeechManager
    {
        public enum Val { Keep, Defer, Ditch, NotFound, Interrupt }

        sealed class History { public double Time; public int Count; public int Speakers; }

        public readonly SpeechScript Script;
        public readonly SpeechTune Tune;
        readonly Dictionary<string, SpeechEventInfo> _info;
        readonly Dictionary<int, SpeechEventInfo> _infoById = new Dictionary<int, SpeechEventInfo>();
        readonly Dictionary<int, History> _history = new Dictionary<int, History>();
        readonly List<int> _evtHistory = new List<int>();          // mEvtHistory: newest first, 10 kept
        readonly List<ScheduledSpeech>[] _q = { new List<ScheduledSpeech>(), new List<ScheduledSpeech>(), new List<ScheduledSpeech>(), new List<ScheduledSpeech>() };
        readonly List<ScheduledSpeech> _deferred = new List<ScheduledSpeech>();
        readonly Random _rng = new Random();
        readonly object _rngLock = new object();
        long _seq;

        /// <summary>Game time in seconds (WorldTimer).</summary>
        public double Now;
        public bool Enabled = true;
        /// <summary>g_EAXIsPaused: dead air reads 65535 while paused.</summary>
        public bool Paused;
        public ISpeechOutput Output;
        /// <summary>SoundAI::GetHeat (MinHeat/MaxHeat test).</summary>
        public Func<int> Heat = () => 1;
        /// <summary>SoundAI::GetPlayerSpeed, mph (Min/MaxPlayerSpeed test).</summary>
        public Func<float> PlayerSpeedMph = () => 0f;
        /// <summary>SoundAI::GetPursuitDuration, seconds or -1 (CalcProbPlayback).</summary>
        public Func<float> PursuitDuration = () => -1f;

        /// <summary>MNotifySpeechStatus on UCRC32_Gameplay: an event finished playing.</summary>
        public event Action<ScheduledSpeech> EventCompleted;
        /// <summary>kSPCH1_EventID_CellCall's script name.</summary>
        public const string CellCall = "CellCall";
        /// <summary>MNotifyCellCallStarted / MNotifyCellCallComplete on UCRC32_Gameplay (the race_bin stategraphs wait for them;
        /// the Lua host can post NotifyCellCallStarted / NotifyCellCallComplete from these).</summary>
        public static event Action CellCallStarted, CellCallCompleted;
        /// <summary>MNotifySpeechStatus on "PlayStart".</summary>
        public event Action<ScheduledSpeech> PlayStarted;

        // GameSpeech state
        public ScheduledSpeech Current { get; private set; }
        bool _busy, _forcingSilence, _touched, _requestsInQueue, _chirpFlag;
        double _lastEventTimestamp;
        float _probPlayback = 1f;
        public int LastSpeakerID { get; private set; }
        /// <summary>gSpeechCache's event slot pool: bNewSlotPool(slot size 0x80, 0xA0 slots) with OVERFLOW_IF_FULL cleared
        /// (SpeechCache.cpp:41; Cache::Init @0x70F500 pushes 0x80, 0xA0 and clears flag bit 0): ScheduleSpeechPartII returns
        /// null while all 160 slots (queued events and the playing one) are taken (@0x713B20: NumAllocated == Total).</summary>
        public const int MaxQueued = 160;
        /// <summary>
        /// The pool's free list (bSlotPool.cpp: FlushSlotPool chains slot 0 → 1 → ... so the first Malloc takes slot 0;
        /// Free pushes the slot back at the head). The slot index stands for the event's address in the two pointer sorts.
        /// The game never frees the events it drops out of mEvents[0] / mEvents[3] (ServiceInterruptEvents' vector
        /// assignments) — the port frees them [ours], so the pool only fills with live events.
        /// </summary>
        sealed class SlotPool
        {
            readonly int[] _next;
            int _head;
            public int Allocated { get; private set; }
            public SlotPool(int n)
            {
                _next = new int[n];
                for (int i = 0; i < n; i++) _next[i] = i + 1 < n ? i + 1 : -1;
                _head = 0;
            }
            public bool IsFull => _head < 0;
            public int Alloc()
            {
                if (_head < 0) return -1;
                int s = _head;
                _head = _next[s];
                Allocated++;
                return s;
            }
            public void Free(int s)
            {
                if (s < 0 || s >= _next.Length) return;
                _next[s] = _head;
                _head = s;
                Allocated--;
            }
        }
        readonly SlotPool _pool = new SlotPool(MaxQueued);
        static int BySlot(ScheduledSpeech a, ScheduledSpeech b) => a.Slot.CompareTo(b.Slot);

        /// <summary>operator delete(ScheduledSpeechEvent): the slot goes back to the pool.</summary>
        void Free(ScheduledSpeech e)
        {
            if (e == null || e.Slot < 0) return;
            _pool.Free(e.Slot);
            e.Slot = -1;
        }
        void FreeAll(List<ScheduledSpeech> l) { foreach (var e in l) Free(e); }

        public CopSpeechManager(SpeechScript script, Dictionary<string, SpeechEventInfo> info, SpeechTune tune)
        {
            Script = script ?? new SpeechScript();
            _info = info ?? new Dictionary<string, SpeechEventInfo>(StringComparer.OrdinalIgnoreCase);
            Tune = tune ?? new SpeechTune();
            foreach (var e in _info.Values) if (e.SpeechId != 0 && !_infoById.ContainsKey(e.SpeechId)) _infoById[e.SpeechId] = e;
        }

        // ----------------------------------------------------------------------------------------- lookups

        /// <summary>The <c>speech</c> attributes of an event (its own collection, else "default" with the .evt id).</summary>
        public SpeechEventInfo InfoFor(string name)
        {
            if (name != null && _info.TryGetValue(name, out var e)) return e;
            var ev = Script.GetEvent(name);
            if (ev == null) return null;
            _info.TryGetValue("default", out var def);
            var made = new SpeechEventInfo { Name = name.ToLowerInvariant(), SpeechId = ev.Id };
            if (def != null)
            {
                made.BackTime = def.BackTime; made.Expiry = def.Expiry; made.CullingRange = def.CullingRange; made.Interval = def.Interval;
                made.EnforceDeadAir = def.EnforceDeadAir; made.Priority = def.Priority; made.Interruptable = def.Interruptable;
            }
            _info[name] = made;
            return made;
        }

        /// <summary>Speech id (SPCHType_1_EventID) of an event name, -1 if unknown.</summary>
        public int IdOf(string name) => InfoFor(name)?.SpeechId ?? -1;

        public bool HasEvent(string name) => Script.HasEvent(name);

        // ----------------------------------------------------------------------------------------- history

        History Hist(int id)
        {
            if (!_history.TryGetValue(id, out var h)) _history[id] = h = new History();
            return h;
        }

        /// <summary>EventHistory::GetCount.</summary>
        public int Count(int id) => _history.TryGetValue(id, out var h) ? h.Count : 0;
        public int Count(string name) => Count(IdOf(name));
        /// <summary>EventHistory::GetTime (0 = never).</summary>
        public double TimeSaid(int id) => _history.TryGetValue(id, out var h) ? h.Time : 0;

        /// <summary>Manager::HasBeenSaid: among the last 10 events touched.</summary>
        public bool HasBeenSaid(int id) => _evtHistory.Contains(id);
        public bool HasBeenSaid(string name) => HasBeenSaid(IdOf(name));

        /// <summary>Manager::GetLastEventID (-1 none).</summary>
        public int LastEventId => _evtHistory.Count > 0 ? _evtHistory[0] : -1;

        /// <summary>Manager::ResetGlobalHistory.</summary>
        public void ResetGlobalHistory()
        {
            foreach (var h in _history.Values) { h.Count = 0; h.Speakers = 0; h.Time = 0; }
            _evtHistory.Clear();
        }

        // ----------------------------------------------------------------------------------------- queue queries

        /// <summary>Manager::IsQueued (indices 4 = every list).</summary>
        public bool IsQueued(int id, int index = 4)
        {
            int from = index == 4 ? 0 : index, to = index == 4 ? 4 : index + 1;
            for (int n = from; n < to; n++)
                foreach (var e in _q[n]) if (e.Id == id) return true;
            return false;
        }
        public bool IsQueued(string name) => IsQueued(IdOf(name));

        /// <summary>Manager::IsCopSpeechPlaying: the current event (kept through its EnforceDeadAir window) has this id.</summary>
        public bool IsPlaying(int id) => Current != null && Current.Id == id;
        public bool IsPlaying(string name) => IsPlaying(IdOf(name));

        /// <summary>Manager::IsCopSpeechBusy: GameSpeech busy (flag 8) or the stream not done.</summary>
        public bool Busy => _busy || (Output != null && Output.Playing);

        /// <summary>Queued plus playing events.</summary>
        public int Pending { get { int n = 0; foreach (var l in _q) n += l.Count; return n + (Current != null ? 1 : 0); } }

        /// <summary>Event-pool slots in use (gSpeechCache.GetEventPool()->CountAllocatedSlots()).</summary>
        public int SlotsAllocated => _pool.Allocated;

        /// <summary>GetTimeSinceLastEvent: now while playing, else the last activity of the channel.</summary>
        public double LastEventTime => Output != null && Output.Playing ? Now : _lastEventTimestamp;

        /// <summary>Manager m_deadair: seconds of radio silence (65535 when paused).</summary>
        public float DeadAir => Paused ? 65535f : (float)(Now - LastEventTime);

        // ----------------------------------------------------------------------------------------- scheduling

        /// <summary>Manager::ScheduleSpeechPartII. Returns the new event, or null when merged, dropped or unknown.</summary>
        public ScheduledSpeech Schedule(string name, Dictionary<string, int> args, ISpeechActor actor)
        {
            // speech off (IsSpeechEnabled / SpeechVol 0): a cell call posts started + complete at once (SpeechManager.cpp:69-77)
            if (!Enabled || SndMixer.SpeechVolumeSetting <= 0f)
            {
                if (name == CellCall) { CellCallStarted?.Invoke(); CellCallCompleted?.Invoke(); }
                return null;
            }
            if (!Script.HasEvent(name)) return null;
            if (_pool.IsFull) return null;   // gSpeechCache.GetEventPool()->IsFull() (SpeechManager.cpp:80)
            var info = InfoFor(name);
            int id = info.SpeechId;

            for (int n = 0; n < 4; n++)
                foreach (var e in _q[n])
                {
                    if (e.Id != id) continue;
                    bool objUpdated = (actor == null && e.Actor == null) || (actor != null && e.Actor != null && e.Actor != actor);
                    if (objUpdated)
                    {
                        e.EntryTime = Now;
                        e.Actor = actor;
                        // bMemCpy(this_event->GetData(), data, size): the Csis data is overwritten even after the sentence
                        // was chosen (the chosen takes stand; SPCH_Choose is not run again for an event with samples)
                        if (args != null) e.Args = args;
                    }
                    return null;
                }

            if (!info.DoNotDropout && !CanPlayback(info)) return null;

            foreach (int r in info.RecallList)
                if (IsQueued(r)) RecallSpeechEvent(r);

            var ev = new ScheduledSpeech
            {
                Id = id, Name = name, Info = info, Actor = actor, Args = args ?? new Dictionary<string, int>(),
                Priority = info.Interrupt ? info.Priority + 100 : info.Priority,
                EntryTime = Now, Delayed = info.InitDelay > 0f, Seq = ++_seq,
                Slot = _pool.Alloc(),   // new (size) ScheduledSpeechEvent: gSpeechCache.Alloc from the event pool
            };
            _q[0].Add(ev);
            return ev;
        }

        /// <summary>Manager::CanPlayback: a repeated event plays with probability mProbPlayback (the PriorityWeight
        /// offset is computed but unused in the game).</summary>
        bool CanPlayback(SpeechEventInfo info)
        {
            if (Count(info.SpeechId) == 0) return true;
            return Rand() <= _probPlayback;
        }

        double Rand() => NfsMw.Core.SimRandom.RandFloatRange(1f);   // CanPlayback (0x704535): sim_prob = Sim::GetRandom().SimRandom_FloatRange(1)

        /// <summary>Manager::CalcProbPlayback: 1 before SpeechDropoffRamp.x seconds of pursuit, 0 after .y, linear between.</summary>
        void CalcProbPlayback()
        {
            float d = PursuitDuration(), x = Tune.SpeechDropoffStart, y = Tune.SpeechDropoffEnd;
            if (d >= x && d <= y) _probPlayback = 1f - (d - x) / (y - x);
            else if (d < x) _probPlayback = 1f;
            else _probPlayback = 0f;
        }

        /// <summary>Manager::RecallSpeechEvent: drops every queued event with this id.</summary>
        public bool RecallSpeechEvent(int id)
        {
            bool found = false;
            foreach (var l in _q)
                if (l.RemoveAll(e => { if (e.Id != id) return false; Free(e); return true; }) > 0) found = true;
            return found;
        }

        /// <summary>Manager::ClearPlayback: every queued event goes (the current one keeps playing).</summary>
        public void ClearPlayback()
        {
            foreach (var l in _q) { FreeAll(l); l.Clear(); }
        }

        /// <summary>Manager::FlushSpeechForActor: queued and current events forget the actor.</summary>
        public int FlushSpeechForActor(ISpeechActor actor)
        {
            if (actor == null) return 0;
            int n = 0;
            foreach (var l in _q)
                foreach (var e in l)
                    if (e.Actor == actor) { e.Actor = null; n++; }
            if (Current != null && Current.Actor == actor) Current.Actor = null;
            return n;
        }

        /// <summary>Speech::Module::ReleaseResource (GameSpeech::ClearCompletedRequests): stops the stream and drops the
        /// current event without completing it.</summary>
        public void ReleaseResource()
        {
            Output?.Stop();
            Free(Current);   // delete this->m_currEvent
            Current = null;
            _busy = _forcingSilence = _touched = _requestsInQueue = false;
        }

        // ----------------------------------------------------------------------------------------- Deduce (4 Hz)

        /// <summary>Manager::Deduce: validate new events, choose sentences, service interrupts.</summary>
        public void Deduce()
        {
            var q0 = _q[0];
            _deferred.Clear();
            for (int i = 0; i < q0.Count; i++)
            {
                var e = q0[i];
                switch (PreValidate(e))
                {
                    case Val.Interrupt: _q[3].Add(e); break;
                    case Val.Keep: _q[1].Add(e); break;
                    case Val.Defer: _deferred.Add(e); break;
                    default: Free(e); break;   // kDitchEvt / kEvtNotFound: delete this_event
                }
            }
            q0.Clear();
            // (the game's erase-then-++ over deferredEvents skips the entry after a ditched one, SpeechManager.cpp:812-815 /
            // PC @0x714410; a deferred event can only be ditched through flag 1, which the port has no equivalent of)
            foreach (var d in _deferred)
            {
                if (PostValidate(d, 1) == Val.Ditch) { Free(d); continue; }
                if (!d.Info.Interrupt) q0.Insert(0, d);
                else _q[3].Add(d);
            }
            _deferred.Clear();
            ServiceFilteredEvents();
            while (!ServiceInterruptEvents()) { }
        }

        Val PreValidate(ScheduledSpeech e)
        {
            if (e.Delayed)
            {
                if (Now - e.EntryTime < e.Info.InitDelay) return Val.Defer;
                e.EntryTime = Now;
                e.Delayed = false;
            }
            if (e.Info.Interrupt)
            {
                var r = PostValidate(e, 0xFFFFFFFF);
                return r == Val.Keep ? Val.Interrupt : r;
            }
            return Val.Keep;
        }

        /// <summary>IndirectSpeechEvent: SPCH_Choose + SPCH_Play for the event's Csis data; NotFound when no sentence fits.</summary>
        Val Indirect(ScheduledSpeech e)
        {
            if (e.Samples != null) return Val.Keep;
            List<SpeechSample> chosen;
            lock (_rngLock) chosen = Script.Choose(e.Name, e.Args, _rng, true);
            if (chosen == null || chosen.Count == 0) return Val.NotFound;
            e.Samples = chosen;
            e.Pcm = new float[chosen.Count][];
            StartDecode(e);
            return Val.Keep;
        }

        void StartDecode(ScheduledSpeech e)
        {
            var script = Script;
            Task.Run(() =>
            {
                for (int i = 0; i < e.Samples.Count; i++)
                {
                    try
                    {
                        var s = script.ReadTake(e.Samples[i]);
                        if (s != null && s.Data != null && s.Data.Length > 0 && s.Samples > 0)
                        {
                            var d = s.Data[0];
                            if (d.Length > s.Samples) Array.Resize(ref d, s.Samples);
                            e.Pcm[i] = d;
                            if (i == 0) e.SampleRate = s.SampleRate;
                        }
                    }
                    catch (Exception) { e.Pcm[i] = null; }
                    Interlocked.Increment(ref e.Decoded);
                }
            });
        }

        /// <summary>Manager::ServiceFilteredEvents (PC @0x7142D0): std::sort of the pointers (slot order), then every event
        /// without samples chooses its sentence: NOTFOUND deletes it, ALLOCATE (SPCH_Play got no sample slot: not modelled)
        /// would re-queue it at the head of [0], else it joins [2]. An event that already has samples is left behind when
        /// [1] is cleared (unreachable: [1] only holds fresh events).</summary>
        void ServiceFilteredEvents()
        {
            var q1 = _q[1];
            if (q1.Count == 0) return;
            if (q1.Count > 1) q1.Sort(BySlot);
            foreach (var e in q1)
            {
                if (e.Samples != null) { Free(e); continue; }
                if (Indirect(e) == Val.NotFound) { Free(e); continue; }
                _q[2].Add(e);
            }
            q1.Clear();
        }

        /// <summary>ServiceInterruptEvents: the top interrupt replaces any waiting interrupt at the head of the ready
        /// list when the current event allows it (Interruptable, forced silence, or a lower-priority interrupt).</summary>
        bool ServiceInterruptEvents()
        {
            var q3 = _q[3];
            if (q3.Count == 0) return true;
            // std::sort of the event pointers (PC @0x70FBC0 from ServiceInterruptEvents @0x714000): the pool's address order
            if (q3.Count > 1) q3.Sort(BySlot);
            var top = q3[0];
            bool interruptable = true;
            if (Current != null)
            {
                interruptable = Current.Info.Interruptable;
                if (_forcingSilence) interruptable = true;   // GameSpeech flag 4
                if (Current.Info.Interrupt && top.Priority > Current.Priority) interruptable = true;
            }
            if (interruptable)
            {
                // mEvents[0] = mEvents[1] (SpeechManager.cpp:681, PC @0x714136): an assignment, the events in [0] are dropped
                // (never reached: ServiceFilteredEvents emptied [1] just before)
                if (_q[1].Count > 0) { FreeAll(_q[0]); _q[0].Clear(); _q[0].AddRange(_q[1]); _q[1].Clear(); }
                if (Indirect(top) == Val.NotFound) { Free(top); q3.RemoveAt(0); return false; }
                // remove_if(InteruptedAndNotDelayed): waiting interrupts that are past their InitDelay are deleted
                _q[2].RemoveAll(x => { if (!(x.Info.Interrupt && !x.Delayed)) return false; Free(x); return true; });
                _q[2].Insert(0, top);
                q3.RemoveAt(0);
            }
            else
            {
                // mEvents[0] = mEvents[3] (SpeechManager.cpp:709, PC @0x714272 vector assignment): the deferred events in [0]
                // are dropped (the game leaks their pool slots; the port frees them [ours])
                FreeAll(_q[0]);
                _q[0].Clear();
                _q[0].AddRange(q3);
                q3.Clear();
                return true;
            }
            // mEvents[3].clear(): the other interrupts are dropped (leaked in the game; freed here [ours])
            FreeAll(q3);
            q3.Clear();
            return true;
        }

        // ----------------------------------------------------------------------------------------- validation

        /// <summary>Manager::PostValidate (mask bits: 1 expiry, 2 MaxPlayback, 4 DeadAir, 8 Interval, 0x10 culling,
        /// 0x20 DepFollow, 0x40 OnScreenOnly, 0x80 reqLOS, 0x100 heat, 0x200 player speed).</summary>
        public Val PostValidate(ScheduledSpeech e, uint mask)
        {
            if (e == null) return Val.Ditch;
            var ev = e.Info;
            if (e.Delayed)
            {
                if (Now - e.EntryTime < ev.InitDelay) return Val.Defer;
                e.EntryTime = Now;
                e.Delayed = false;
            }
            if (Now - e.EntryTime > ev.Expiry && (mask & 1) != 0) return Val.Ditch;
            if (ev.MaxPlayback > -1 && (mask & 2) != 0 && Count(e.Id) > ev.MaxPlayback) return Val.Ditch;
            if ((mask & 4) != 0 && ev.DeadAir > 0f && DeadAir < ev.DeadAir) return Val.Defer;
            if ((mask & 8) != 0 && _history.TryGetValue(e.Id, out var h) && h.Count > 0 && Now - h.Time < ev.Interval) return Val.Defer;
            if (e.Actor != null && (mask & 0x10) != 0 && e.Actor.Distance > ev.CullingRange) return Val.Defer;
            if (ev.DepFollow.Length > 0 && (mask & 0x20) != 0)
            {
                bool pass = false;
                if (ev.BackTime > 0f)
                {
                    foreach (int dep in ev.DepFollow)
                    {
                        int count = Count(dep);
                        if ((Now - TimeSaid(dep) < ev.BackTime && count > 0) || IsPlaying(dep)) { pass = true; break; }
                    }
                }
                else if (_evtHistory.Count > 0)
                {
                    int last = _evtHistory[0];
                    foreach (int dep in ev.DepFollow)
                        if (dep == last || IsPlaying(dep)) { pass = true; break; }
                }
                if (!pass) return Val.Defer;
            }
            if (ev.OnScreenOnly && (mask & 0x40) != 0 && e.Actor != null && !e.Actor.InView) return Val.Defer;
            if (ev.ReqLOS && (mask & 0x80) != 0 && e.Actor != null && !e.Actor.HasLOS) return Val.Defer;
            int heat = Heat();
            if ((heat > ev.MaxHeat || heat < ev.MinHeat) && (mask & 0x100) != 0) return Val.Defer;
            float ps = Math.Abs(PlayerSpeedMph());
            if ((ps < ev.MinPlayerSpeed || ps > ev.MaxPlayerSpeed) && (mask & 0x200) != 0) return Val.Defer;
            return Val.Keep;
        }

        /// <summary>Manager::IsEventDead: seconds of EnforceDeadAir still owed after the event, or -1.</summary>
        float IsEventDead(ScheduledSpeech e)
        {
            float enforce = e.Info.EnforceDeadAir;
            if (enforce <= 0f) return -1f;
            float elapsed = (float)(Now - e.FinishTime);
            return elapsed >= enforce ? -1f : enforce - elapsed;
        }

        /// <summary>ScheduledSpeechEvent::sort_nested_priority (EaxSoundTypes.cpp:238): priority descending, then entry time
        /// ascending, then frameindex (always 0: m_frameindex is never advanced, so such pairs are "equal" to std::sort and
        /// their order is whatever introsort leaves; the port keeps arrival order [?]).</summary>
        static int NestedPriority(ScheduledSpeech a, ScheduledSpeech b)
        {
            if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
            if (a.EntryTime != b.EntryTime) return a.EntryTime.CompareTo(b.EntryTime);
            return a.Seq.CompareTo(b.Seq);
        }

        /// <summary>Manager::GetNextEvent.</summary>
        ScheduledSpeech GetNextEvent()
        {
            var q2 = _q[2];
            if (q2.Count == 0) return null;
            if (q2.Count > 1 && q2[0].Priority <= 100) q2.Sort(NestedPriority);
            for (int i = 0; i < q2.Count; i++)
            {
                var next = q2[i];
                if (!next.Prepared)
                {
                    if (next.Ready)
                    {
                        next.Prepared = true;
                        if (!next.Delayed) next.EntryTime = Now;
                    }
                    else
                    {
                        var keep = PostValidate(next, 1);
                        if (next.Samples == null || next.Samples.Count == 0 || keep == Val.Ditch) { q2.RemoveAt(i); Free(next); return null; }
                        return null;   // waits on its takes (interrupt or not)
                    }
                }
                var r = PostValidate(next, 0xFFFFFFFF);
                if (r == Val.Ditch) { q2.RemoveAt(i); Free(next); return null; }
                if (r == Val.Keep) return next;
            }
            return null;
        }

        // ----------------------------------------------------------------------------------------- playback (per frame)

        /// <summary>Manager::Update + GameSpeech::Update.</summary>
        public void Update()
        {
            CalcProbPlayback();
            if (Output == null) return;
            if (Output.Playing)
            {
                _busy = true;
                _requestsInQueue = true;
                if (Output.PlayedTime > 3.0 && !_touched && Current != null)
                {
                    NotifyEventCompletion(Current, false);
                    _touched = true;
                }
                CheckNextEvent();
                _lastEventTimestamp = Now;
                return;
            }
            if (_requestsInQueue)
            {
                if (Current != null)
                {
                    if (Current.FinishTime == 0)
                    {
                        Current.FinishTime = Now;
                        if (!_touched) { NotifyEventCompletion(Current, false); _touched = true; }
                        NotifyEventCompletion(Current, true);
                        if (_chirpFlag) Output.Chirp(1);   // RadioChirp(1): the flag of the last issued event
                    }
                    if (Current == null || IsEventDead(Current) < 0f)
                    {
                        _requestsInQueue = false;
                        _lastEventTimestamp = Now;
                        Free(Current);   // delete this->m_currEvent
                        Current = null;
                        _forcingSilence = false;
                        _busy = false;
                    }
                    else
                    {
                        _forcingSilence = true;
                        _busy = true;
                        CheckNextEvent();
                    }
                }
                else
                {
                    _requestsInQueue = false;
                    _forcingSilence = false;
                    _busy = false;
                }
            }
            else CheckNextEvent();
        }

        void CheckNextEvent()
        {
            var next = GetNextEvent();
            if (next == null) return;
            // GameSpeech::CheckNextEvent: IssuePlayback then RadioChirp(0); the chirp flag (0x400) comes from the issued event's
            // RadioChirp attribute even when its takes failed and it was dropped
            if (next.Priority <= 100)
            {
                if (_busy) return;
                IssuePlayback(next);
            }
            else
            {
                ReleaseResource();
                IssuePlayback(next);
            }
            _chirpFlag = next.Info.RadioChirp;
            if (_chirpFlag) Output.Chirp(0);
        }

        void IssuePlayback(ScheduledSpeech next)
        {
            _q[2].Remove(next);   // Manager::Expire
            bool ok = next.Pcm != null && next.Pcm.Length > 0;
            if (ok) foreach (var p in next.Pcm) if (p == null) { ok = false; break; }
            PlayStarted?.Invoke(next);
            if (next.Name == CellCall) CellCallStarted?.Invoke();   // MNotifyCellCallStarted (GameSpeech.cpp:340)
            if (!ok)
            {
                if (next.Name == CellCall) CellCallCompleted?.Invoke();   // GameSpeech.cpp:391
                // a take that failed to load (err_count): the event completes unheard and is deleted (GameSpeech.cpp:387-396;
                // the game leaves its pointer in mEvents[2], the port takes it out)
                EventCompleted?.Invoke(next);
                Free(next);
                return;
            }
            Current = next;
            next.FinishTime = 0;
            _touched = false;
            _busy = true;
            _requestsInQueue = true;
            Output.Begin(next);
        }

        /// <summary>Manager::NotifyEventCompletion: history on the first call (3 s in or at the end), the completion
        /// message on the second.</summary>
        void NotifyEventCompletion(ScheduledSpeech e, bool complete)
        {
            int speaker = e.Actor?.SpeakerID ?? 0;
            if (speaker > 1) LastSpeakerID = speaker;
            if (!complete)
            {
                var h = Hist(e.Id);
                h.Time = Now;
                h.Count++;
                if (speaker >= 0 && speaker < 10) h.Speakers |= 1 << speaker;
                _evtHistory.Insert(0, e.Id);
                if (_evtHistory.Count > 10) _evtHistory.RemoveAt(_evtHistory.Count - 1);
                return;
            }
            EventCompleted?.Invoke(e);
            if (e.Name == CellCall) CellCallCompleted?.Invoke();   // MNotifyCellCallComplete (SpeechManager.cpp:957)
        }

        /// <summary>Everything off: queues, current event, history (SoundAI::MessageRestart + ResetGlobalHistory).</summary>
        public void Reset()
        {
            ClearPlayback();
            ReleaseResource();
            ResetGlobalHistory();
            _lastEventTimestamp = Now;
        }

        /// <summary>A short description of the queues for debugging.</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Current != null ? "playing " + Current : "idle");
            for (int n = 0; n < 4; n++)
            {
                if (_q[n].Count == 0) continue;
                sb.Append($" | q{n}:");
                foreach (var e in _q[n]) sb.Append(' ').Append(e.Name);
            }
            return sb.ToString();
        }
    }
}
