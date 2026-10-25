using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// The NIS/SFX stream channel (Speech module 0 = Speech::SED_NISSFX, Stream/NISSFXModule.cpp; PC 0x6FFB70..): one
    /// stream at a time from SOUND\STREAMS\NISAudio.big, chosen by the NISAudio event scripts (SPCH, read with
    /// <see cref="SpeechScript"/>). Users:
    /// <list type="bullet">
    /// <item>SFXObj_NISStream (MAIN obj 5): race-start / Blacklist / safehouse NIS (STRM_NIS_RACE_START, mixer output 1)
    /// and busted/ending NIS (STRM_NIS_BUSTED, output 2), picked by <see cref="NisStreamSelect"/>; input 6 = a start NIS
    /// is playing in full, 7 = an end NIS.</item>
    /// <item>SFXObj_MomentStrm (MAIN obj 6, <see cref="MomentStream"/>): gameplay moments (STRM_SFX_MOMENT), volume =
    /// output VolSlot, azimuth = output 0 when IsPositioned.</item>
    /// </list>
    /// QueStream asks SPCH for the take, the stream is buffered, and its callback starts it (the NIS start waits for the
    /// animation too). Here the take is decoded on a worker thread and plays once ready [ours: whole-take decode instead of
    /// the 3-block stream]. Volume = output &gt;&gt; 8 (0..127) as SetVol takes it, linear [?].
    /// </summary>
    public static class NisStreams
    {
        public enum Kind { None = -1, RaceStart = 0, ButtonThrough = 1, Busted = 2, Thunder = 3, Collision = 4, Moment = 5 }

        static SpeechScript _script;
        static AudioSource _src;
        static Task<EaStream> _pending;
        static int _requestId;
        static Action _onReady;
        static bool _hold;
        public static Kind Type { get; private set; } = Kind.None;
        /// <summary>The stream is decoded and waiting for <see cref="Start"/> (a NIS whose animation is not ready yet).</summary>
        public static bool Ready => _pending == null && _src && _src.clip && !_src.isPlaying && _hold;
        public static bool IsPlaying => _src && _src.isPlaying;
        /// <summary>Milliseconds played (EAXS_StreamChannel::GetCurrentTime, the NIS clock callback).</summary>
        public static int TimeMs => _src && _src.clip ? (int)(_src.time * 1000f) : -1;
        public static float Length => _src && _src.clip ? _src.clip.length : 0f;
        public static float Azimuth { get; private set; }
        static float _volume;
        internal static SndObj NisObj, MomentObj;

        static SpeechScript Script
        {
            get
            {
                if (_script == null) { GameData.Mount(); _script = SpeechScript.Load(GameData.Root, "NISAudio"); }
                return _script;
            }
        }

        /// <summary>The take for a NISAudio event (<see cref="NisStreamSelect.Pick"/>).</summary>
        public static SpeechSample? Pick(string ev, Dictionary<string, int> args) => NisStreamSelect.Pick(Script, ev, args, UnityEngine.Random.Range(0, int.MaxValue));

        static EaStream Decode(string big, long offset, int bytes)
        {
            try
            {
                using (var f = File.OpenRead(big))
                {
                    int n = (int)Math.Min(bytes > 0 ? bytes : 1 << 24, f.Length - offset);
                    var buf = new byte[n];
                    f.Position = offset;
                    int got = 0;
                    while (got < n) { int r = f.Read(buf, got, n - got); if (r <= 0) break; got += r; }
                    return EaStream.Read(buf, 0, out _);
                }
            }
            catch (Exception) { return null; }
        }

        static AudioSource Source()
        {
            if (_src) return _src;
            var go = new GameObject("NFSMW NIS Stream");
            if (SndMixer.Instance) go.transform.SetParent(SndMixer.Instance.transform, false);
            else if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
            _src = go.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.loop = false;
            return _src;
        }

        /// <summary>SED_NISSFX::QueStream: purge what is queued, choose the take and start decoding it. With
        /// <paramref name="hold"/> the stream waits for <see cref="Start"/> (SFXObj_NISStream::StartNIS).</summary>
        public static bool Queue(Kind kind, string ev, Dictionary<string, int> args, bool hold, Action onReady = null)
        {
            if (!Application.isPlaying) return false;
            var src = Source();
            // SED_NISSFX::QueStream (PC @0x6FFC50): while a request is still queued (not yet played), thunder / collision /
            // moment requests (> STRM_NIS_BUSTED) are refused and leave it be; a NIS request purges it
            bool queued = _pending != null || (_hold && src.clip && !src.isPlaying);
            if (queued && kind > Kind.Busted) return true;
            var pick = Pick(ev, args);
            if (src.isPlaying && (kind == Kind.RaceStart || kind == Kind.Moment))
            {
                src.Stop();
                // a moment cutting a playing stream: SFXObj_NISStream::NISActivityDone
                if (kind == Kind.Moment && NisObj != null) { NisObj.Set(6, 0); NisObj.Set(7, 0); }
            }
            if (pick == null) return false;
            Type = kind;
            _hold = hold;
            _onReady = onReady;
            int id = ++_requestId;
            string big = Script.BigPath;
            var sample = pick.Value;
            _pending = Task.Run(() => Decode(big, sample.BigOffset, sample.NumBytes));
            _pendingId = id;
            return true;
        }
        static int _pendingId;

        /// <summary>SED_NISSFX::PlayStream: release a held stream.</summary>
        public static void Start()
        {
            _hold = false;
            if (_pending == null && _src && _src.clip && !_src.isPlaying) _src.Play();
        }

        /// <summary>PurgeSpeech / stream Stop.</summary>
        public static void Stop()
        {
            _requestId++;
            _pending = null;
            _hold = false;
            if (_src) _src.Stop();
            Type = Kind.None;
            if (NisObj != null) { NisObj.Set(6, 0); NisObj.Set(7, 0); }
        }

        // ------------------------------------------------------------------ NIS (SFXObj_NISStream)

        /// <summary>
        /// SFXObj_NISStream::QueueNISStream (CARSFX/SFXObj_NISStream.cpp): queue the stream of NIS scene
        /// <paramref name="animName"/> (camera track for GenericStart; button-through = skipped, plays the End section) and
        /// raise the NIS inputs. Plays at once unless <paramref name="waitForAnimation"/>; then call <see cref="Start"/>.
        /// Returns false if the scene has no stream.
        /// </summary>
        public static bool QueueNis(string animName, int cameraTrack = 0, bool buttonThrough = false, bool waitForAnimation = false)
        {
            var (ev, args) = NisStreamSelect.Event(animName, cameraTrack, buttonThrough);
            if (ev == null) return false;
            bool end = NisStreamSelect.IsEnd(animName);
            bool ok = Queue(end ? Kind.Busted : buttonThrough ? Kind.ButtonThrough : Kind.RaceStart, ev, args, waitForAnimation);
            if (ok && NisObj != null)
            {
                if (end) NisObj.Set(7, 0x7FFF);
                else NisObj.Set(6, buttonThrough ? 0 : 0x7FFF);
            }
            return ok;
        }

        /// <summary>The decoded clip of a NIS scene's stream (synchronous; for callers that play it themselves), or null.</summary>
        public static AudioClip Clip(string animName, int cameraTrack = 0, bool buttonThrough = false)
        {
            var (ev, args) = NisStreamSelect.Event(animName, cameraTrack, buttonThrough);
            if (ev == null) return null;
            var pick = Pick(ev, args);
            if (pick == null) return null;
            var s = Decode(Script.BigPath, pick.Value.BigOffset, pick.Value.NumBytes);
            return s != null && s.Samples > 0 ? MakeClip(animName, s) : null;
        }

        static AudioClip MakeClip(string name, EaStream s)
        {
            var clip = AudioClip.Create("NIS_" + name, s.Samples, 2, s.SampleRate, false);
            clip.SetData(s.Stereo(), 0);
            return clip;
        }

        /// <summary>SED_NISSFX::Update: finish decodes, start unheld streams, volumes per stream type.</summary>
        internal static void Update(float dt)
        {
            if (_pending != null && _pending.IsCompleted)
            {
                var t = _pending;
                _pending = null;
                if (_pendingId == _requestId && t.Status == TaskStatus.RanToCompletion && t.Result != null && t.Result.Samples > 0)
                {
                    var src = Source();
                    src.clip = MakeClip(Type.ToString(), t.Result);
                    if (!_hold) src.Play();
                    _onReady?.Invoke();
                    _onReady = null;
                }
            }
            if (!_src) return;
            int vol = 127;
            float az = 0;
            switch (Type)
            {
                case Kind.Moment:
                    vol = MomentStream.Volume >> 8;
                    az = MomentStream.Positioned && MomentObj != null ? SndVoice.Pan(MomentObj.Out(0, MixMap.Azim)) : 0f;
                    break;
                case Kind.RaceStart:
                case Kind.ButtonThrough:
                    vol = NisObj != null && NisObj.Mapped ? NisObj.Out(1, MixMap.Vol) >> 8 : 127;
                    break;
                case Kind.Busted:
                    vol = NisObj != null && NisObj.Mapped ? NisObj.Out(2, MixMap.Vol) >> 8 : 127;
                    break;
                default:
                    vol = 100;
                    break;
            }
            _volume = Mathf.Clamp(vol, 0, 127) / 127f;
            _src.volume = _volume;
            _src.panStereo = az;
            Azimuth = az;
            // SFXObj_NISStream::StopStream when the scene ends or is skipped (the port has no button-through re-queue) [ours]
            if ((Type == Kind.RaceStart || Type == Kind.ButtonThrough) && !NisPlayer.Active && _src.isPlaying && _src.time > 0.5f) Stop();
            if (!_src.isPlaying && _pending == null && !_hold && Type != Kind.None)
            {
                // the stream ended: NISActivityDone
                Type = Kind.None;
                if (NisObj != null) { NisObj.Set(6, 0); NisObj.Set(7, 0); }
            }
        }
    }

    /// <summary>
    /// SFXObj_MomentStrm (CARSFX/SFXObj_MomentStrm.cpp; MAIN obj 6, its 3D controller SFXCTL_3DMomentPos = MAIN ctl 2):
    /// MGamePlayMoment("MomentStrm") → an `aud_moment_strm` collection → Csis::SoundFX_Select(stream, param) → a
    /// NISAudio take on the NIS/SFX channel (<see cref="NisStreams"/>). ShouldStreamPlay: not the same moment again within
    /// 2 s (tollbooth and window may repeat), only while racing when a race is set up, not over a NIS stream, and only over
    /// a playing moment of lower priority (a queued one must also be CanInterupt). A positioned moment follows its car
    /// (car2car/collision) or stays put, and is dropped beyond 120 m of the player; a moment at the origin follows the
    /// player. Moments under pursuit_breaker_moments raise input 5 when they start.
    /// <para>Senders ported: blown engine / sabotage (SFXCTL_Engine::UpdateParams), totalled (MNotifyVehicleDestroyed for
    /// the player), the speedbreaker start/end (ReceivePursuitBreaker: start_music with interactive music else start_fx;
    /// the end stops a playing start stream), impact sweeteners (SFXObj_Collision::InitSFX StreamSweetner). The world
    /// pursuit-breaker markers (g_MomentMappings, TrackPositionMarkers) are not loaded [?].</para>
    /// </summary>
    public static class MomentStream
    {
        public const uint KeyTotalled = 0xC565AC30, KeyBlownEngine = 0xBC2DFA2F, KeyEngineBreakdown = 0xBAE41D1B, KeyCar2Car = 0x0D6D4198,
            KeyCollision = 0xA6E3EF3E, KeyTollbooth = 0x5AEFC6A8, KeyWindow = 0xA80F0E08, KeyPursBreakStartFx = 0x14D32C41,
            KeyPursBreakStartMusic = 0x2DF96F7D, KeyPursuitBreakerMoments = 0x5A3E90B0;

        static CollisionAudioData _data;
        static uint _cur;
        static float _retrigger;
        static Vector3 _pos;
        static bool _userPos;
        static Transform _follow;
        static int _volSlot;
        public static bool Positioned { get; private set; }
        static SndPos _3d;
        static bool _pbInput;

        public static CollisionAudioData Data
        {
            get
            {
                if (_data == null) _data = CollisionAudioData.Read(GameData.Attributes);
                return _data;
            }
        }

        /// <summary>The moment's volume (output VolSlot of MAIN obj 6, Q15).</summary>
        internal static int Volume => NisStreams.MomentObj != null && NisStreams.MomentObj.Mapped ? NisStreams.MomentObj.Out(_volSlot, MixMap.Vol) : 0x7FFF;

        static bool ShouldPlay(uint key)
        {
            if (_cur == key && _retrigger > 0f && key != KeyTollbooth && key != KeyWindow) return false;
            var race = RaceManager.Active;
            if (race != null && race.Countdown) return false;   // GetRaceParameters() && !GetActivelyRacing() [?: countdown as "not racing"]
            if (NisStreams.Type != NisStreams.Kind.Moment && NisStreams.Type != NisStreams.Kind.None && NisStreams.IsPlaying) return false;
            if (!Data.Moments.TryGetValue(key, out var m)) return false;
            if (_cur != 0 && Data.Moments.TryGetValue(_cur, out var c) && c.Priority > m.Priority) return false;
            return true;
        }

        /// <summary>MGamePlayMoment(pos, ..., carsID, key).Send("MomentStrm") → SFXObj_MomentStrm::ReceiveMoment.
        /// <paramref name="follow"/> = the car for car2car/collision moments; position zero = the player's car.</summary>
        public static void Send(uint key, Vector3 position, Transform follow = null)
        {
            if (!Application.isPlaying || !ShouldPlay(key)) return;
            var m = Data.Moments[key];
            var args = new Dictionary<string, int> { { "SoundFX_Type", m.Stream }, { "SoundFX_Param", m.Param } };
            if (!NisStreams.Queue(NisStreams.Kind.Moment, "SoundFX_Select", args, false, OnStart)) return;
            _cur = key;
            _pos = position;
            _userPos = position.sqrMagnitude < 1f;
            _follow = key == KeyCar2Car || key == KeyCollision ? follow : null;
            _volSlot = m.VolSlot;
            Positioned = m.IsPositioned;
        }

        /// <summary>CBPlayMomentStream: the stream starts; a pursuit-breaker moment raises input 5.</summary>
        static void OnStart()
        {
            _retrigger = 2f;
            if (Data.Moments.TryGetValue(_cur, out var m) && m.Parent == KeyPursuitBreakerMoments) _pbInput = true;
        }

        /// <summary>g_MomentMappings (SFXObj_MomentStrm.cpp:15-31): eMomentMarkerTypes (NisCandidateData.MarkerNames index) →
        /// aud_moment_strm collection (bStringHash of its name).</summary>
        static readonly (int marker, string moment)[] MomentMappings =
        {
            (1, "expl_gas_station"), (2, "comm_tower_crash"), (15, "comm_tower_crash"), (4, "wtr_tower_crash"), (5, "porch"), (6, "gate"),
            (7, "scaffold_crash_big"), (8, "gazebo_crash"), (9, "fish_sign_crash"), (10, "boat_fall"), (17, "trailer_park"),
            (19, "amphitheatre"), (20, "stripmall"), (16, "drive_in"), (18, "torus"),
        };
        static List<(Vector3 pos, uint key)> _momentPositions;

        /// <summary>SFXObj_MomentStrm::InitSFX (:93-116): every TrackPositionMarker named after a g_MomentMappings marker type,
        /// with its moment key (positions in Unity space; the marker's render x/y are the game's sim z/-x).</summary>
        static List<(Vector3 pos, uint key)> MomentPositions()
        {
            if (_momentPositions != null) return _momentPositions;
            var list = new List<(Vector3, uint)>();
            try
            {
                var markers = TrackMarkers.Load(GameData.Root);
                foreach (var (type, moment) in MomentMappings)
                {
                    if (type < 0 || type >= NisCandidateData.MarkerNames.Length) continue;
                    uint nameHash = EaHash.Hash(NisCandidateData.MarkerNames[type]);
                    uint key = AttribDatabase.Hash(moment);
                    foreach (var m in markers.All)
                        if (m.NameHash == nameHash) list.Add((Coords.ToUnity(m.Position), key));
                }
            }
            catch (System.Exception) { }
            return _momentPositions = list;
        }

        /// <summary>
        /// SFXObj_MomentStrm::ReceiveMoment (:221-236) for the world-animation "unpause" MGamePlayMoment (a pursuit breaker's
        /// animation starting): the moment whose marker lies within 25 m on both horizontal axes of the animation (|Δsim x| and
        /// |Δsim z| &lt; 25 = Unity z / x) replaces the "unpause" key; no marker → nothing plays.
        /// </summary>
        public static void WorldAnimUnpaused(Vector3 position)
        {
            if (!Application.isPlaying) return;
            uint key = 0;
            foreach (var (pos, k) in MomentPositions())
                if (Mathf.Abs(position.x - pos.x) < 25f && Mathf.Abs(position.z - pos.z) < 25f) key = k;   // the last match wins, like the loop
            if (key == 0) return;
            Send(key, position);
        }

        /// <summary>SFXObj_MomentStrm::ReceivePursuitBreaker (the speedbreaker on/off).</summary>
        public static void PursuitBreaker(bool start)
        {
            if (start)
            {
                bool interactive = MusicSystem.Instance && MusicSystem.Instance.MusicType == MusicSystem.Interactive;
                Send(interactive ? KeyPursBreakStartMusic : KeyPursBreakStartFx, Vector3.zero);
            }
            else if (_cur == KeyPursBreakStartFx || _cur == KeyPursBreakStartMusic) { NisStreams.Stop(); _cur = 0; }
        }

        /// <summary>SFXObj_MomentStrm::UpdateParams / ProcessUpdate.</summary>
        internal static void Update(float dt)
        {
            var obj = NisStreams.MomentObj;
            if (obj != null) { obj.Set(5, _pbInput ? 0x7FFF : 0); obj.In[15] = 1; }
            _pbInput = false;
            _retrigger = Mathf.Max(0f, _retrigger - dt);
            var player = SndMixer.PlayerRef;
            if (_userPos && player) _pos = player.transform.position;
            if (_follow) _pos = _follow.position;
            if (_3d == null && SndMixer.Instance) _3d = new SndPos(new SndObj(SndMixer.Instance.Map, MixMap.ObjectId(true, MixMap.Main, 0, 2)));
            _3d?.Update(_pos, Vector3.zero, dt);
            if (_cur == 0) return;
            if (NisStreams.Type != NisStreams.Kind.Moment || (!NisStreams.IsPlaying && !NisStreams.Ready && NisStreams.Type == NisStreams.Kind.None)) { _cur = 0; _follow = null; return; }
            if (!_userPos && player && Vector3.Distance(_pos, player.transform.position) > 120f) { _cur = 0; NisStreams.Stop(); }
        }
    }
}
