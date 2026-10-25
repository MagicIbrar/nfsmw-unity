using System;
using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// The PathFinder stream track (PathTrackSndStream, vtable 0x8C5F30; Play @0x82B4A0 hands SNDSTRM the MUS offset x
    /// 128): queued MUS sections (EA-XA stereo, 36 kHz in MW_Music.mus) played back to back without gaps, at most
    /// maxrequests (3) queued. Each request is decoded when queued (the engine queues ~2.5 s ahead), resampled
    /// linearly to the output rate in OnAudioFilterRead, with the track volume (0..127, linear [?]) and the low-pass
    /// SNDSTRM_lowpass gets (one-pole [?]). Lives on its own child object.
    /// </summary>
    public sealed class MusicStream : MonoBehaviour, IPathStream
    {
        sealed class Req { public int Node, Frames, Rate; public float[] Pcm; }

        readonly object _lock = new object();
        readonly List<Req> _q = new List<Req>();
        double _pos;
        bool _paused;
        int _outRate = 48000;
        volatile float _gain, _lpHz = 22000f;
        float _cur, _lpL, _lpR;
        FileStream _mus;
        long[] _ends;
        PathMap _map;
        public bool HasAudio { get; private set; }

        public static MusicStream Create(Transform parent, PathMap map, string musPath)
        {
            var go = new GameObject("MusicStream");
            go.transform.SetParent(parent, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.loop = true;
            var s = go.AddComponent<MusicStream>();
            s._map = map;
            s._outRate = AudioSettings.outputSampleRate;
            try
            {
                if (File.Exists(musPath))
                {
                    s._mus = new FileStream(musPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    // a blanked copy (all zeros): no 'SCHl' where section 1 starts
                    long first = map.SectionOffset(1);
                    var probe = new byte[4];
                    s._mus.Position = first;
                    s.HasAudio = s._mus.Read(probe, 0, 4) == 4 && probe[0] == 'S' && probe[1] == 'C' && probe[2] == 'H' && probe[3] == 'l';
                }
            }
            catch (Exception e) { Debug.LogWarning("NFSMW music: " + e.Message); }
            // section ends: the next larger section offset
            var offs = new List<long>();
            for (int i = 1; i <= map.NumSections; i++) offs.Add(map.SectionOffset(i));
            offs.Sort();
            s._ends = offs.ToArray();
            src.clip = AudioClip.Create("music", 1, 2, s._outRate, false);
            src.Play();
            if (!s.HasAudio) Debug.Log("NFSMW music: MW_Music.mus holds no audio in this copy; the music logic runs silent");
            return s;
        }

        void OnDestroy() { _mus?.Dispose(); _mus = null; }

        float[] Decode(long off, out int rate)
        {
            rate = 0;
            if (!HasAudio || _mus == null || off < 0) return null;
            int i = Array.BinarySearch(_ends, off);
            long end = i >= 0 && i + 1 < _ends.Length ? _ends[i + 1] : Math.Min(_mus.Length, off + (2 << 20));
            int len = (int)Math.Min(4 << 20, Math.Max(0, end - off));
            if (len <= 0) return null;
            var buf = new byte[len];
            _mus.Position = off;
            int n = _mus.Read(buf, 0, len);
            var s = EaStream.Read(buf, 0, out _);
            if (s == null || s.Samples == 0 || n <= 0) return null;
            rate = s.SampleRate;
            return s.Stereo();
        }

        // ------------------------------------------------------------------ IPathStream
        public void Stop() { lock (_lock) { _q.Clear(); _pos = 0; } }
        public void Pause(bool on) => _paused = on;
        public bool Ready { get { lock (_lock) return _q.Count < 3; } }
        public int TimeBuffered => 1 << 20;

        public int TimeRemaining(int n)
        {
            lock (_lock)
            {
                if (n < 0)
                {
                    int sum = 0;
                    for (int i = 0; i < _q.Count; i++) sum += Ms(_q[i], i == 0 ? _pos : 0);
                    return sum;
                }
                return n < _q.Count ? Ms(_q[n], n == 0 ? _pos : 0) : -1;
            }
        }

        static int Ms(Req r, double pos) => (int)((r.Frames - pos) * 1000.0 / Math.Max(1, r.Rate));

        public int Play(int node, long musOffset, int startBeat, int hold, int durationMs)
        {
            float[] pcm = null;
            int rate = 0;
            try { pcm = Decode(musOffset, out rate); }
            catch (Exception e) { Debug.LogWarning("NFSMW music: section decode: " + e.Message); }
            // no audio: a silent request of the section's length keeps the timing
            if (pcm == null) { rate = 1000; pcm = new float[Math.Max(2, durationMs) * 2]; }
            lock (_lock) _q.Add(new Req { Node = node, Pcm = pcm, Frames = pcm.Length / 2, Rate = rate });
            return 0;
        }

        public void SetVolume(int v) => _gain = Mathf.Clamp(v, 0, 127) / 127f;
        public void SetLowPass(float hz) => _lpHz = Mathf.Clamp(hz, 100f, 22000f);
        public int PlayingNode { get { lock (_lock) return _q.Count > 0 ? _q[0].Node : -1; } }
        public int PlayingPosMs { get { lock (_lock) return _q.Count > 0 ? (int)(_pos * 1000.0 / Math.Max(1, _q[0].Rate)) : 0; } }

        void OnAudioFilterRead(float[] data, int channels)
        {
            int frames = data.Length / channels;
            float target = _paused ? 0f : _gain, a = 1f - Mathf.Exp(-2f * Mathf.PI * _lpHz / Math.Max(1, _outRate));
            float cur = _cur, step = (target - cur) / Math.Max(1, frames);
            lock (_lock)
            {
                for (int f = 0; f < frames; f++)
                {
                    float l = 0, r = 0;
                    if (!_paused && _q.Count > 0)
                    {
                        var q = _q[0];
                        int i = (int)_pos;
                        float frac = (float)(_pos - i);
                        float l0 = q.Pcm[i * 2], r0 = q.Pcm[i * 2 + 1], l1, r1;
                        if (i + 1 < q.Frames) { l1 = q.Pcm[i * 2 + 2]; r1 = q.Pcm[i * 2 + 3]; }
                        else if (_q.Count > 1) { l1 = _q[1].Pcm[0]; r1 = _q[1].Pcm[1]; }
                        else { l1 = l0; r1 = r0; }
                        l = l0 + (l1 - l0) * frac;
                        r = r0 + (r1 - r0) * frac;
                        _pos += q.Rate / (double)_outRate;
                        if (_pos >= q.Frames)
                        {
                            _pos -= q.Frames;
                            _q.RemoveAt(0);
                            if (_q.Count > 0) _pos = _pos * _q[0].Rate / Math.Max(1, q.Rate);
                            else _pos = 0;
                        }
                    }
                    _lpL += (l - _lpL) * a;
                    _lpR += (r - _lpR) * a;
                    cur += step;
                    float ol = _lpL * cur, or = _lpR * cur;
                    if (channels >= 2) { data[f * channels] = ol; data[f * channels + 1] = or; for (int c = 2; c < channels; c++) data[f * channels + c] = 0; }
                    else data[f] = (ol + or) * 0.5f;
                }
            }
            _cur = target;
        }
    }

    /// <summary>
    /// Speech::MusicFlow (Speech/MusicFlow.cpp, owned by SoundAI): the interactive pursuit music's state machine.
    /// Started by MNotifyMusicFlow "Init" (StartInteractiveMusic), it sends PF_EVT_START 0.5 s later, then moves
    /// between Neutral / Lose / Win / Elude by pursuit distance, player speed against the car's top speed, crashes,
    /// cop rams, cooldown and collapse, posting PF_EVT_NEUTRAL/LOSE/WIN/ELUDE/ENDWIN (10..14) and the intensity
    /// (0..127) as the path controller every frame. PartUpdate (the marker nodes) maps back to states.
    /// Reads SoundAI through the cop-speech port's SpeechAI (CopRadio.AI) [read-only].
    /// </summary>
    public sealed class MusicFlow
    {
        public const int kTransition = -1, kNeutral = 0, kLose = 1, kWin = 2, kElude = 3, kWaiting = 333, kTerminal = 999;
        public int State { get; private set; } = kTransition;
        readonly MusicSystem _sys;
        bool _startDelay, _restrained = true, _requestedSwap;
        float _timer, _boostTimer, _tPiece, _elapsed, _pieceElapsed, _intensity, _avgForm, _avgLos, _avgSpeed, _avgDist, _topSpeed = 160f;
        int _busy, _currentPart = -1;

        public MusicFlow(MusicSystem sys) { _sys = sys; }

        static float Now => Time.time;
        static SpeechAI AI => CopRadio.AI;

        int PursuitState
        {
            get
            {
                if (AI != null) return AI.PursuitState;
                var p = Systems.Police;
                return p == null || !p.InPursuit ? SpeechAI.kInactive : p.InCooldown ? SpeechAI.kSearching : SpeechAI.kActive;
            }
        }
        float PlayerSpeed => AI != null ? AI.PlayerSpeed : SndMixer.PlayerRef ? SndMixer.PlayerRef.SpeedMps * 2.23699f : 0f;
        float PursuitDist => AI != null ? AI.PursuitDist : 0f;
        float TimeLastCrashed => AI != null ? AI.TimeLastCrashed : 65535f;
        float TimeLastNailedCop => AI != null ? AI.TimeLastNailedCop : 65535f;
        float PerpLostTime => AI != null ? AI.PerpLostTime : 0f;
        int Focus => AI != null ? AI.Focus : SpeechAI.kPursuitFlow;
        bool InCooldown => Systems.Police != null && Systems.Police.InPursuit && Systems.Police.InCooldown;
        bool Collapse => Systems.Police != null && Systems.Police.IsCollapseActive;
        float TimeUntilBusted => Systems.Police != null ? Systems.Police.TimeUntilBusted : 0f;
        bool HighIntensity => AI != null && AI.IsHighIntensity;

        public void MessageNewPart(int part)
        {
            _currentPart = part;
            switch (part)
            {
                case 4: base_ChangeStateTo(kNeutral); break;
                case 1: base_ChangeStateTo(kLose); break;
                case 3: base_ChangeStateTo(kWin); break;
                case 2: base_ChangeStateTo(kElude); break;
                case 5: case 6: case 7: case -1: case 0: case 8: base_ChangeStateTo(kTerminal); break;
            }
        }

        /// <summary>SpeechFlow::ChangeStateTo (no event).</summary>
        void base_ChangeStateTo(int s) => State = s;

        public void MessageInitFlow(int startEvent)
        {
            ChangeStateTo(kWaiting);
            var p = SndMixer.PlayerRef;
            _topSpeed = p && p.Spec != null && p.Spec.TopSpeed > 1f ? p.Spec.TopSpeed * 2.23699f : 160f;
            _timer = Now;
            _startDelay = true;
        }

        public void MessageTerminate() => ChangeStateTo(kTerminal);
        public void MessageDone() => Reset();

        public void Reacquire()
        {
            if (State == kWin || State == kElude)
            {
                if (PursuitDist <= 50f) { ChangeStateTo(kLose); _intensity = 0.5f; }
                else { ChangeStateTo(kNeutral); _intensity = 0.8f; }
            }
            else if (State == kNeutral) _intensity = 1f;
        }

        public void Update()
        {
            if (_sys.MusicVolume <= 0f || !_sys.InteractiveMusicMode)
            {
                if (State != kTransition) Reset();
                return;
            }
            if (State != kTransition)
            {
                _elapsed = Now - _timer;
                _pieceElapsed = Now - _tPiece;   // PC +0x48 = (now - mTPiece +0x20) x 0.00025 (MusicFlow::Update @0x71F4A0)
                var ai = AI;
                _avgForm = _avgForm * 0.97f + (ai != null ? ai.CopsInFormation.Count : 0) * 0.03f;
                _avgLos = _avgLos * 0.97f + (ai != null ? ai.NumCopsWithLOS : 0) * 0.03f;
                _avgSpeed = _avgSpeed * 0.95f + PlayerSpeed * 0.05f;
                _avgDist = _avgDist * 0.97f + (PursuitDist > 0f ? PursuitDist : _avgDist) * 0.03f;
            }
            switch (State)
            {
                case kNeutral: Neutral(); break;
                case kLose: Lose(); break;
                case kWin: Win(); break;
                case kElude: Elude(); break;
                case kTerminal: Terminal(); break;
                case kWaiting: Waiting(); break;
            }
            if (_requestedSwap && State != kWaiting && State != kTerminal && State != kTransition) _requestedSwap = false;
            if (State != kTransition) _sys.SendPathControl((int)(_intensity * 127f));
        }

        float UpdateIntensity(float adj)
        {
            _intensity = Mathf.Clamp01(_intensity * 0.99f + adj * 0.01f);
            return _intensity;
        }

        /// <summary>
        /// PC-only (@0x700E80, called from Neutral and Win when the pursuit piece has played 600 s; absent from the GC decomp):
        /// MControlPathfinder(false, 0, 0, 0).Send(UCrc32("Swap")) → SFXObj_PFEATrax::MessageSwapInteractive; mTPiece = now,
        /// mRequestedSwap = true, ChangeStateTo(kWaiting). Waiting keeps resetting mTPiece until the next state clears the request.
        /// </summary>
        void RequestSwap()
        {
            _sys.MessageSwapInteractive();
            _tPiece = Now;
            _requestedSwap = true;
            ChangeStateTo(kWaiting);
        }

        // NB the IDA export names these by the decomp's order, not the PC's: the state switch in Update (@0x71F4A0) runs
        // state 0 → 0x71B1A0 ("Win"), 1 → 0x71B4E0 (Lose), 2 → 0x71B740 ("Neutral"), 3 → 0x704B30 (Elude), 333 → 0x700E10
        // ("Terminal"), 999 → 0x700ED0 ("Waiting"); the bodies match the port's Neutral/Lose/Win/Elude/Waiting/Terminal.
        void Waiting()
        {
            if (_startDelay && _elapsed > 0.5f)
            {
                _startDelay = false;
                _sys.SendPathEvent(17);
                _tPiece = Now;
            }
            if (_requestedSwap) _tPiece = Now;
        }

        // the decomp's "mBusy = cond ? mBusy++ : 0" keeps the old value while the condition holds
        void Neutral()
        {
            bool cooldown = InCooldown;
            _busy = cooldown ? 0 : (_elapsed < 30f || _restrained) ? _busy : 0;
            if (_pieceElapsed > 600f) { RequestSwap(); return; }   // PC Neutral (state 0) @0x71B1A0, not in the decomp
            UpdateIntensity(_avgDist < 32f ? 1f : 0f);
            UpdateIntensity(TimeLastCrashed >= 2.5f ? _intensity : 1f);
            UpdateIntensity(TimeLastNailedCop >= 2.5f ? _intensity : 1f);
            if (_restrained) _intensity = Mathf.Clamp(_intensity, 0f, 0.5f);
            if (PursuitState == SpeechAI.kInactive) { ChangeStateTo(kTerminal); return; }
            if (_restrained && !cooldown)
            {
                if (_elapsed <= 30f && Focus != SpeechAI.kStrategyFlow) return;
                _restrained = cooldown;
                _intensity = 1f;
                _timer = Now;
            }
            if (PlayerSpeed < _topSpeed * 0.125f)
            {
                if (Collapse) { _intensity = 0.86f; ChangeStateTo(kLose); return; }
                if (PursuitState == SpeechAI.kSearching && PerpLostTime > 4f && TimeLastCrashed > 4f) { ChangeStateTo(kElude); return; }
            }
            if (_busy != 0 || PursuitState == SpeechAI.kInactive) return;
            if (cooldown) ChangeStateTo(PlayerSpeed >= _topSpeed * 0.35f ? kWin : kElude);
            else
            {
                if (_avgSpeed < _topSpeed * 0.125f && PursuitState == SpeechAI.kSearching) { ChangeStateTo(kElude); return; }
                if (_avgSpeed > _topSpeed * 0.5f) { ChangeStateTo(kWin); return; }
                if (_avgDist >= 32f) return;
                ChangeStateTo(kLose);
                _intensity = 0.35f;
            }
        }

        void Lose()
        {
            _busy = _elapsed < 45f ? _busy : 0;
            if (PursuitState == SpeechAI.kInactive) { ChangeStateTo(kTerminal); return; }
            if (PlayerSpeed < _topSpeed * 0.125f && TimeUntilBusted > 0.5f && HighIntensity) _intensity = 1f;
            else
            {
                if (_intensity >= 0.93f) _intensity = 0.8f;
                UpdateIntensity(PlayerSpeed < _topSpeed * 0.375f ? 0.86f : 0f);
                UpdateIntensity(TimeLastCrashed >= 2.5f ? _intensity : 0.86f);
                UpdateIntensity(TimeLastNailedCop >= 2.5f ? _intensity : 0.86f);
            }
            if ((PursuitState == SpeechAI.kSearching || InCooldown) && PerpLostTime > 4f)
            {
                ChangeStateTo(PlayerSpeed < _topSpeed * 0.35f ? kElude : kWin);
                _intensity = 1f;
            }
            else
            {
                if (_busy != 0) return;
                if (_avgSpeed <= _topSpeed * 0.5f && PerpLostTime <= 5f && _elapsed <= 90f) return;
                ChangeStateTo(kNeutral);
                _intensity = 0.65f;
            }
        }

        void Win()
        {
            _busy = _elapsed < 45f ? _busy : 0;
            if (_pieceElapsed > 600f) { RequestSwap(); return; }   // PC Win (state 2) @0x71B740, not in the decomp
            UpdateIntensity(_avgSpeed > _topSpeed * 0.5f ? 1f : 0f);
            if (TimeLastCrashed < 2f || TimeLastNailedCop < 2f) _intensity = 0.25f;
            if (TimeLastCrashed > 10f && TimeLastNailedCop > 10f && PlayerSpeed > _topSpeed * 0.8f && Now - _boostTimer > 10f)
            {
                _intensity *= 1.15f;
                _boostTimer = Now;
            }
            if (PursuitState == SpeechAI.kInactive) { ChangeStateTo(kTerminal); return; }
            if (PlayerSpeed < _topSpeed * 0.125f && Collapse) { ChangeStateTo(kLose); return; }
            if (_avgSpeed < _topSpeed * 0.125f && (PursuitState == SpeechAI.kSearching || InCooldown)) { ChangeStateTo(kElude); return; }
            if (_busy != 0 || _elapsed <= 90f) return;
            ChangeStateTo(kNeutral);
        }

        void Elude()
        {
            _busy = _elapsed < 5f ? _busy : 0;
            if (PursuitState == SpeechAI.kInactive) { ChangeStateTo(kTerminal); return; }
            UpdateIntensity(_avgSpeed > 10f ? 1f : 0f);
            if (PursuitState == SpeechAI.kActive)
            {
                if (PursuitDist <= 50f) { ChangeStateTo(kLose); return; }
                ChangeStateTo(kNeutral);
                _intensity = 0.8f;
                return;
            }
            if (_avgSpeed > _topSpeed * 0.45f) ChangeStateTo(kWin);
        }

        void Terminal()
        {
            if (_currentPart < 5 || _currentPart > 7) Reset();
        }

        /// <summary>MusicFlow::ChangeStateTo: enters Waiting and posts the state's event unless that part already plays.</summary>
        public void ChangeStateTo(int s)
        {
            if (s == State) return;
            State = kWaiting;
            int ev = -1;
            _timer = Now;
            _busy = 1;
            switch (s)
            {
                case kNeutral: if (_currentPart != 4) ev = 10; break;
                case kLose: if (_currentPart != 1) ev = 11; break;
                case kWin: if (_currentPart != 3) ev = 12; break;
                case kElude: _avgSpeed = 0f; if (_currentPart != 2) ev = 13; break;
                case kTerminal: if ((_currentPart < 5 || _currentPart > 7) && Focus != SpeechAI.kTerminal && Focus != -1) ev = 14; break;
            }
            if (ev != -1) _sys.SendPathEvent(ev);
        }

        public void Reset()
        {
            _timer = 0;
            _elapsed = _intensity = _avgForm = _avgLos = _avgSpeed = _avgDist = 0f;
            _requestedSwap = false;
            _restrained = true;
            State = kTransition;
        }
    }

    /// <summary>
    /// The music object (SFXObj_PFEATrax, EAXSound/CARSFX/SFXObj_Pathfinder.cpp; PC UpdateParams @0x4E79A0,
    /// ProcessUpdate @0x4F6EA0, StartLicensedMusic @0x4F6C80) and its SFXCTL_Pathfinder, in the MUSIC mix state.
    /// One PathFinder project on PC: MW_Music.mpf/.mus hold the licensed songs (EA Trax), the four interactive pursuit
    /// pieces (chosen by the named value "PursuitID", PC: InitSFX @0x4DFAA0 and the swap in UpdateParams) and the
    /// ambience beds. Music types: licensed (EA Trax playlist, events = the songs' PathEvent), interactive (pursuit:
    /// MusicFlow drives PF_EVT_* events and the intensity), ambience (AmbientCrossMap by the "Ambience" parameter map
    /// zone under the player; FE: garage zones), splash. Volumes: MUSIC mixer slots 0/1 (FE/IG licensed), 3
    /// (interactive), 4/5 (FE/IG ambience), low-pass slot 9; the speedbreaker sweeps a low-pass 65535→630→1800 Hz
    /// (UpdatePursuitBreaker). Song progress (marker nodes) clears the start mute and feeds MusicFlow's parts through
    /// PFXMAP (PC table @0x8EE7E8).
    /// </summary>
    public sealed class MusicSystem : MonoBehaviour, ISndUnit
    {
        public static MusicSystem Instance { get; private set; }

        public const int Licensed = 0, Interactive = 1, Ambience = 2, Splash = 3;
        const int TraxUninit = -2, TraxOff = -1, TraxFE = 0, TraxIG = 1;
        const uint EvStop = 0x01C3FA91, EvStartScreen = 0x01C53FC7, EvStart = 0x016E7282;
        const int PursuitToLicDelay = 40;   // PURSUIT_TO_LIC_DELAY

        /// <summary>AmbientCrossMap (SFXObj_Pathfinder.cpp:108): ambient zone → path event.</summary>
        static readonly uint[] AmbientCrossMap =
        {
            0x011BBC15, 0x01E40616, 0x01919B1B, 0x01C505B7, 0x0134209F, 0x012F7671, 0x0127205F,
            0x01B2D374, 0x019509F2, 0x0146EDA3, 0x0196E300, 0x017690D2, 0x01E6FF17, 0x01E45B3F,
        };

        /// <summary>PFXMAP (PC .data 0x8EE7E8): per pursuit piece, node params 0..8 then events 9..20.</summary>
        static readonly uint[,] PfxMap =
        {
            { 0x199, 0x6A, 0x132, 0xD5, 0x0, 0x189, 0x17F, 0x18F, 0x19C, 0x1C3FA91, 0x1C0DC6F, 0x1E9222F, 0x1518C15, 0x1B39639, 0x1E2814E, 0x14C6876, 0x1C53FC7, 0x16E7282, 0x190B6DC, 0x122E859, 0x12BBA48 },
            { 0x2E1, 0x217, 0x297, 0x265, 0x1AB, 0x2CD, 0x2C1, 0x2D7, 0x2E4, 0x1C3FA91, 0x1C0DC6F, 0x1E9222F, 0x1518C15, 0x1B39639, 0x1E2814E, 0x14C6876, 0x1C53FC7, 0x16E7282, 0x190B6DC, 0x122E859, 0x12BBA48 },
            { 0x456, 0x33C, 0x3DF, 0x379, 0x2F3, 0x442, 0x436, 0x44C, 0x459, 0x1C3FA91, 0x1C0DC6F, 0x1E9222F, 0x1518C15, 0x1B39639, 0x1E2814E, 0x14C6876, 0x1C53FC7, 0x16E7282, 0x190B6DC, 0x122E859, 0x12BBA48 },
            { 0x5BD, 0x4A9, 0x577, 0x50D, 0x467, 0x5A9, 0x59F, 0x5B3, 0x5C0, 0x1C3FA91, 0x1C0DC6F, 0x1E9222F, 0x1518C15, 0x1B39639, 0x1E2814E, 0x14C6876, 0x1C53FC7, 0x16E7282, 0x190B6DC, 0x122E859, 0x12BBA48 },
        };

        sealed class Trax { public uint TraxMask, PlayBits = 0xFFFFFFFF; public int NumEnabled, LastPlaylistSong = -1, PlayTrackIndex = -1, PBMode = -1; }

        // audio settings (AudioSettings::Default: InteractiveMusicMode 1, EATraxMode 1, PlayState 0)
        public bool InteractiveMusicMode = true, EATraxMode = true;
        public int PlayState;
        /// <summary>GetCurMusicVolume: FE / IG music volume (the port has one music slider).</summary>
        /// <summary>The music slider for where we are (FEAudioSettings FEMusicVol in the front end, IGMusicVol in game).</summary>
        public float MusicVolume => Mathf.Clamp01(SoundState.Mode == SndGameMode.FrontEnd ? SndMixer.FeMusicVolume : SndMixer.IgMusicVolume);

        public PathMap Map { get; private set; }
        public PathEngine Engine { get; private set; }
        public MusicStream Stream { get; private set; }
        public MusicFlow Flow { get; private set; }
        public List<SongInfo> Songs { get; private set; } = new List<SongInfo>();
        /// <summary>SummonChyron (song title card): fired when a licensed song starts [ours: the HUD may subscribe].</summary>
        public static event Action<SongInfo> Chyron;
        public SongInfo NowPlaying { get; private set; }
        public int MusicType => _type;

        readonly Trax[] _trax = { new Trax(), new Trax() };
        int[] _playability = Array.Empty<int>();
        SndObj _obj;
        ParamMapLayer _ambLayer;
        bool _ambLoaded;
        // SFXObj_PFEATrax members
        int _traxState = TraxOff, _type = Splash, _prevType = Splash, _ambZone = -1, _proj, _curPart = -1, _volume;
        uint _flags = 0x40, _cur, _prev = 0xFFFFFFFF;
        bool _skip, _clearSkip, _failed, _queueNext;
        float _ambStart;
        int _status = 1;
        PathLine _filterFade;
        int _filterFreq = 0xFFFF;
        bool _breaker, _prevBreaker, _inFE = true, _started;
        float _clock, _skipTime;
        bool _wasPursuit, _wasArrest, _wasCountdown, _set321Pending;
        int _prevFocus = -1;

        public static MusicSystem Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("NFSMW Music");
            DontDestroyOnLoad(go);
            return go.AddComponent<MusicSystem>();
        }

        void Awake()
        {
            Instance = this;
            GameData.Mount();
            string dir = Path.Combine(GameData.Root ?? "", "SOUND", "PFDATA");
            try { Map = PathMap.Load(Path.Combine(dir, "MW_Music.mpf")); }
            catch (Exception e) { Debug.LogWarning("NFSMW music: MW_Music.mpf: " + e.Message); }
            if (Map == null) { Debug.Log("NFSMW music: no MW_Music.mpf"); enabled = false; return; }
            Engine = new PathEngine(Map);
            Stream = MusicStream.Create(transform, Map, Path.Combine(dir, "MW_Music.mus"));
            Engine.Tracks[0].Stream = Stream;
            Engine.Progress = SongProgress;
            Engine.ActionCallback = EventAction;
            Flow = new MusicFlow(this);
            try { Songs = SongInfo.Load(GameData.Attributes); } catch (Exception e) { Debug.LogWarning("NFSMW music: songs: " + e.Message); }
            InitPlaylist();
            InitializeEATrax(true);
            var mixer = SndMixer.Ensure();
            _obj = new SndObj(mixer.Map, MixMap.ObjectId(false, MixMap.Music, 0, 1));   // SFXObj_PFEATrax 0x1010010: MUSIC obj 1
            _obj.Attach(true);
            mixer.Register(this);
            _proj = UnityEngine.Random.Range(1, 5) & 3;
            EnterWorld(true);
            RaceManager.Restarted += OnRaceRestarted;
        }

        /// <summary>ERestartRace → EAXSound::ReStartRace (only while actively racing, not in a challenge race): the
        /// race's music starts over with the next 3-2-1.</summary>
        void OnRaceRestarted(RaceEvent ev)
        {
            if (Engine == null || GameSession.Mode == GameMode.ChallengeSeries) return;
            RestartRace();
            _flags &= ~1u;
        }

        void OnDestroy()
        {
            RaceManager.Restarted -= OnRaceRestarted;
            if (SndMixer.Instance) SndMixer.Instance.Unregister(this);
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ EA Trax playlist
        /// <summary>FEDatabase default playlist: playability from DefPlay (FE 1, IG 2, AL 3).</summary>
        void InitPlaylist()
        {
            _playability = new int[Songs.Count];
            for (int i = 0; i < Songs.Count; i++)
            {
                var s = Songs[i];
                _playability[i] = s == null ? 0 : s.DefPlay == "FE" ? 1 : s.DefPlay == "IG" ? 2 : s.DefPlay == "AL" ? 3 : 0;
            }
        }

        /// <summary>InitializeEATrax @0x4DF330.</summary>
        void InitializeEATrax(bool reset)
        {
            foreach (var t in _trax) { t.PBMode = PlayState; t.TraxMask = 0; t.NumEnabled = 0; }
            for (int n = 0; n < _playability.Length && n < 28; n++)
            {
                int p = _playability[n];
                if ((p & 1) != 0) { _trax[0].TraxMask |= 1u << n; _trax[0].NumEnabled++; }
                if ((p & 2) != 0) { _trax[1].TraxMask |= 1u << n; _trax[1].NumEnabled++; }
            }
            foreach (var t in _trax) { t.PlayBits = t.TraxMask; if (reset) t.LastPlaylistSong = -1; }
        }

        Trax CurTrax => _traxState >= 0 ? _trax[_traxState] : _trax[0];

        /// <summary>GenNextMusicTrackID @0x4F4CD0: in order (PlayState 0) or a random unplayed song.</summary>
        void GenNextMusicTrackID()
        {
            var t = CurTrax;
            t.PlayTrackIndex = -1;
            t.TraxMask &= 0x0FFFFFFF;
            if (_traxState == TraxOff || t.TraxMask == 0) return;
            int max = _playability.Length;
            if (t.PlayBits == 0 && t.NumEnabled != 0) { t.PlayBits = t.TraxMask & 0x0FFFFFFF; t.LastPlaylistSong = -1; }
            if (t.PBMode == 0)
            {
                for (int n = t.LastPlaylistSong + 1; n < max; n++)
                {
                    if ((t.PlayBits & 1u << n) == 0) continue;
                    t.PlayTrackIndex = n;
                    t.PlayBits ^= 1u << n;
                    if (t.PlayBits == 0) { t.PlayBits = t.TraxMask & 0x0FFFFFFF; t.PlayBits ^= 1u << n; }
                    return;
                }
                if (t.NumEnabled == 0) return;
                t.PlayBits = t.TraxMask & 0x0FFFFFFF;
                t.LastPlaylistSong = -1;
                GenNextMusicTrackID();
                return;
            }
            int count = 0;
            for (int n = 0; n < max; n++) if ((t.PlayBits & 1u << n) != 0) count++;
            int pick = count > 0 ? UnityEngine.Random.Range(0, count) : 0, k = 0;
            for (int n = 0; n < max; n++)
            {
                if ((t.PlayBits & 1u << n) == 0) continue;
                if (k++ != pick) continue;
                t.PlayTrackIndex = n;
                t.PlayBits ^= 1u << n;
                if (t.PlayBits == 0) { t.PlayBits = t.TraxMask & 0x0FFFFFFF; t.PlayBits ^= 1u << n; }
                return;
            }
        }

        // ------------------------------------------------------------------ world entry (constructor + SetupSFX + InitSFX)
        void EnterWorld(bool frontEnd)
        {
            Engine.Stop(-1);
            Engine.ClearAllEvents();
            _inFE = frontEnd;
            _type = _prevType = Splash;
            _cur = 0;
            _prev = 0xFFFFFFFF;
            _ambZone = -1;
            _flags = 0x40;
            _skip = _clearSkip = _failed = _queueNext = false;
            _ambStart = 0;
            _started = false;
            Flow.Reset();
            // SetupSFX
            _traxState = GenEATraxState();
            _flags &= ~8u;
            // InitSFX (PC @0x4DFAA0): PursuitID = piece + 1
            Engine.SetNamedValue("PursuitID", _proj + 1);
            if (!frontEnd) _type = Licensed;
            _flags |= 4;
            if (frontEnd) MessageStartPathfinder(0);   // FEManager::StartFE → PlayFEMusic
        }

        int GenEATraxState() => Settings.Master <= 0f ? TraxOff : _inFE ? TraxFE : TraxIG;

        int GenMusicType()
        {
            if (_traxState == TraxIG)
            {
                if (MusicVolume > 0f) return IsPursuitActive ? Interactive : Licensed;
                return Ambience;
            }
            if (_traxState == TraxFE) return MusicVolume > 0f ? Licensed : Ambience;
            return Ambience;
        }

        static bool IsPursuitActive
        {
            get
            {
                var ai = CopRadio.AI;
                if (ai != null) return ai.PursuitState == SpeechAI.kActive || ai.PursuitState == SpeechAI.kSearching;
                return Systems.Police != null && Systems.Police.InPursuit;
            }
        }

        bool IsMusicActive => Flow.State != MusicFlow.kTransition;

        // ------------------------------------------------------------------ messages
        /// <summary>MessageStartPathfinder ("Pathfinder5"): 0 = pick the music type; -1 = stop the licensed song; else a
        /// song's path event (EA Trax jukebox preview).</summary>
        public void MessageStartPathfinder(uint ev)
        {
            if (_traxState == TraxOff) { StopMusic(); return; }
            if (_type == Splash && ev == 0xFFFFFFFF) return;
            if (ev == 0) _type = GenMusicType();
            else if (ev == 0xFFFFFFFF)
            {
                if (MusicVolume > 0f && EATraxMode && CurTrax.TraxMask != 0 && _type == Licensed && !_skip) { Engine.ClearAllEvents(); Engine.Stop(-1); }
            }
            else
            {
                if (_trax[0].TraxMask == 0) _trax[0].TraxMask = 0x80000000;
                Engine.ClearAllEvents();
                _type = GenMusicType();
                _cur = _prev = ev;
                StartLicensedMusic(ev);
                _skip = true;
                _clearSkip = false;
            }
        }

        /// <summary>MessageSendPathEvent ("Event", PF_EVT_* index).</summary>
        public void SendPathEvent(int n)
        {
            uint curr = _cur;
            if (_type == Licensed || _type == Ambience || _type == Splash)
            {
                if (n == 9) _cur = EvStop;
                else if (n == 16) _cur = EvStartScreen;
                else if (n > 9) return;
            }
            else if (n >= 0 && n < 21) _cur = PfxMap[_proj, n];
            if (_cur == curr) return;
            _prev = curr;
            _queueNext = true;
            if (_type == Ambience) Engine.Pause(-1, false);
            DoSendPathEvent();
            if (n == 19 || n == 20) _flags |= 0x10;   // SwapInteractiveProjects (PC @0x4DF100 sets 0x10 only)
        }

        /// <summary>SFXObj_PFEATrax::MessageSwapInteractive (PC @0x4DFC40, handler of MControlPathfinder "Swap"): the current
        /// path event = PFXMAP[m_InteractiveProj][PF_EVT_INITSWAP 18], the previous 0, and the active project's queue_next set,
        /// so the next update posts it (MusicFlow's 600 s piece swap).</summary>
        public void MessageSwapInteractive()
        {
            _cur = PfxMap[_proj, 18];
            _prev = 0;
            _queueNext = true;
        }

        /// <summary>MessageSendPathControl: the intensity while interactive.</summary>
        public void SendPathControl(int intensity)
        {
            if (_type != Interactive) return;
            Engine.Control(-1, Mathf.Clamp(intensity, 0, 127));
        }

        void SongProgress(uint proj, int node)
        {
            // SongProgressCallback (PC @0x4B6360): CrossMapNodeParam → MusicFlow part, then PartUpdate
            for (int i = 0; i < 21; i++)
                if (PfxMap[_proj, i] == (uint)node) { Flow.MessageNewPart(i); break; }
            _curPart = node;
            _clearSkip = true;
            _flags &= ~0x100u;
        }

        void EventAction(uint track, int id, int parm)
        {
            // EventActionCallback (PC @0x4B6440)
            switch (id)
            {
                case 0: Engine.SetNamedValue("pursuitid", _proj); break;
                case 2: _ambStart = Time.time; Flow.MessageDone(); break;   // InteractiveDone
                case 3: SendPathEvent(parm == 0 ? 0x13 : 0x14); break;
            }
        }

        // ------------------------------------------------------------------ SFXObj_PFEATrax
        void DoSendPathEvent()
        {
            if (_traxState == TraxUninit || (_flags & 4) == 0) return;
            if (_cur != _prev)
            {
                _failed = false;
                _prev = _cur;
                if (Engine.Event(_cur) != 0) { _prev = 0; _failed = true; return; }
                Engine.Volume(-1, 0);
                if (_cur == EvStartScreen) { _prevType = _type; _type = Splash; }
            }
            _queueNext = false;
        }

        void StopMusic()
        {
            _traxState = TraxOff;
            Engine.ClearAllEvents();
            Engine.Stop(-1);
            _queueNext = false;
        }

        public void RestartRace()
        {
            if (_type == Interactive || _type == Licensed)
            {
                Engine.Stop(-1);
                Engine.ClearAllEvents();
                _type = Licensed;
                _failed = false;
            }
            _ambStart = 0;
        }

        void StartLicensedMusic(uint pathEvent)
        {
            var mode = SoundState.Mode;
            if (mode != SndGameMode.FrontEnd && mode != SndGameMode.FreeRoam)
            {
                bool challenge = mode == SndGameMode.ChallengeRace;
                if ((_flags & 1) == 0 || (!challenge && (_flags & 0x200) != 0)) { if (!challenge) return; }
                else if (!challenge) goto start;
                if (InteractiveMusicMode) return;
            }
        start:
            if (!_started) { _started = true; if ((_flags & 2) == 0) _flags |= 0x10; }
            _prevType = _type;
            _type = Licensed;
            Engine.ClearAllEvents();
            _prev = _cur;
            var t = CurTrax;
            if (t.PlayTrackIndex != -1) t.LastPlaylistSong = t.PlayTrackIndex;
            uint use;
            if (pathEvent != 0)
            {
                bool found = false;
                use = pathEvent;
                for (int n = 0; n < Songs.Count; n++)
                    if (Songs[n] != null && Songs[n].PathEvent == pathEvent) { found = true; t.PlayTrackIndex = n; use = Songs[n].PathEvent; _flags &= ~0x40u; _prev = 0; }
                if (!found) { t.PlayTrackIndex = 0; t.PlayBits ^= 1; }
            }
            else
            {
                GenNextMusicTrackID();
                if (t.PlayTrackIndex < 0 || t.PlayTrackIndex >= Songs.Count || Songs[t.PlayTrackIndex] == null) return;
                use = Songs[t.PlayTrackIndex].PathEvent;
                _flags &= ~0x40u;
                _prev = 0;
            }
            _cur = use;
            _queueNext = true;
            if (_status == 3) Engine.Pause(-1, false);
            if (_cur != _prev) { _skip = true; _clearSkip = false; }
        }

        void StartAmbience(uint ev)
        {
            _prevType = _type;
            _type = Ambience;
            Engine.ClearAllEvents();
            if (_status == 3) Engine.Pause(-1, false);
            _prev = _cur;
            _queueNext = true;
            _cur = ev;
            _skip = true;
            _clearSkip = false;
        }

        void StartInteractiveMusic(uint ev)
        {
            if ((_flags & 0x80C) != 4) return;
            Engine.SetNamedValue("PursuitID", _proj + 1);   // PC @0x4DF1C0
            _prevType = _type;
            _type = Interactive;
            Engine.ClearAllEvents();
            if (_status == 3) Engine.Pause(-1, false);
            Flow.MessageInitFlow((int)ev);
            _flags |= 0x100;
        }

        bool TestToPursuit()
        {
            if (!InteractiveMusicMode || MusicVolume == 0f || (_flags & 0x802) != 0) return false;
            if (!IsPursuitActive) return false;
            _cur = 0;
            StartInteractiveMusic(EvStart);
            return true;
        }

        bool TestToLicensed(bool start)
        {
            if (_skip || (_flags & 0x800) != 0) return false;
            if (MusicVolume > 0f && EATraxMode && CurTrax.TraxMask != 0)
            {
                if (SoundState.Mode == SndGameMode.FrontEnd)
                {
                    if (start) { Engine.Stop(-1); StartLicensedMusic(0); }
                    return true;
                }
                var race = RaceManager.Active;
                if (race != null && race.Event != null && (race.Event.DDayRace || race.Event.Type == RaceType.Drag)) return false;   // GetIsDDayRace || GRaceStatus::IsDragRace (0x4DF56D)
                if (_type == Ambience)
                {
                    float playing = _ambStart > 0 ? Time.time - _ambStart : 0f;
                    if (playing <= PursuitToLicDelay) return false;
                    if (start) StartLicensedMusic(0);
                    _ambStart = 0;
                    return true;
                }
                if (start) StartLicensedMusic(0);
                return true;
            }
            if (_type == Interactive && MusicVolume > 0f && !InteractiveMusicMode)
            {
                Engine.Stop(-1);
                if (EATraxMode) { _cur = 0; StartLicensedMusic(0); return true; }
            }
            return false;
        }

        bool TestToAmbience()
        {
            if ((_flags & 0x800) != 0) return false;
            UpdateAmbience();
            if (_type == Interactive)
            {
                if (!IsMusicActive || MusicVolume <= 0f || !InteractiveMusicMode) { StartAmbience(AmbientCrossMap[Mathf.Clamp(_ambZone, 0, 13)]); return true; }
                return false;
            }
            if (_type == Licensed)
            {
                if (MusicVolume != 0f && EATraxMode && CurTrax.TraxMask != 0) return false;
                Engine.Stop(-1);
                StartAmbience(AmbientCrossMap[Mathf.Clamp(_ambZone, 0, 13)]);
                return true;
            }
            return false;
        }

        void UpdateInGame()
        {
            if (_failed) return;
            if ((SoundState.ActiveSfx & 1) != 0) return;
            if ((_flags & 0x800) == 0)
            {
                if ((_flags & 0x404) != 4 || _status == 3 || _skip) return;
            }
            switch (_type)
            {
                case Ambience:
                    if (TestToPursuit() || TestToLicensed(true)) return;
                    if (_status == 1)
                    {
                        if (_cur == _prev) _cur = 0;
                        UpdateAmbience();
                        StartAmbience(AmbientCrossMap[Mathf.Clamp(_ambZone, 0, 13)]);
                        return;
                    }
                    UpdateAmbience();
                    if (AmbientCrossMap[Mathf.Clamp(_ambZone, 0, 13)] != _cur) StartAmbience(AmbientCrossMap[Mathf.Clamp(_ambZone, 0, 13)]);
                    break;
                case Licensed:
                    if (TestToPursuit() || TestToAmbience()) return;
                    if (TestToLicensed(false)) { if (_status == 1) StartLicensedMusic(0); }
                    else { UpdateAmbience(); StartAmbience(AmbientCrossMap[Mathf.Clamp(_ambZone, 0, 13)]); }
                    break;
                case Interactive:
                    if (!InteractiveMusicMode || MusicVolume == 0f)
                    {
                        Engine.Stop(-1);
                        if (TestToLicensed(true) || TestToAmbience()) return;
                    }
                    else if (TestToAmbience()) return;
                    if (_status != 1) return;
                    _cur = 0;
                    if (IsMusicActive) return;
                    StartInteractiveMusic(EvStart);
                    break;
            }
        }

        /// <summary>UpdateAmbience @0x4DF6D0: the "Ambience" map zone under the player in game; FE: the garage's zone.</summary>
        void UpdateAmbience()
        {
            if (_traxState == TraxIG)
            {
                int zone = 0;
                var p = SndMixer.PlayerRef;
                var layer = AmbienceLayer();
                if (p && layer != null) zone = layer.GetInt(p.transform.position.x, p.transform.position.z, 0);
                if (_ambZone == zone && _status != 1) return;
                if (zone < 0 || zone > 13) return;
                _ambZone = zone;
            }
            else _ambZone = 11;   // GARAGETYPE default; customization 10, safehouse 9, car lot 12, rap sheet 13 [?] not told apart
        }

        ParamMapLayer AmbienceLayer()
        {
            if (_ambLoaded) return _ambLayer;
            _ambLoaded = true;
            try
            {
                var w = GameBootstrap.Instance ? GameBootstrap.Instance.World : null;
                var d = w != null && w.Track != null ? w.Track.Bundle : GameFiles.ReadBundle(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN"));
                foreach (var l in ParamMapLayer.Read(d)) if (l.NameHash == ParamMapLayer.Hash("Ambience")) _ambLayer = l;
            }
            catch (Exception e) { Debug.LogWarning("NFSMW music: ambience map: " + e.Message); }
            return _ambLayer;
        }

        /// <summary>UpdatePursuitBreaker @0x4DF7E0.</summary>
        void UpdatePursuitBreaker(float dt)
        {
            if (_breaker && !_prevBreaker)
            {
                if (_filterFade == null)
                {
                    _filterFade = new PathLine();
                    _filterFade.Clear();
                    _filterFade.AddStage(65535f, 630f, 1000, SndCurve.EqPwrSq);
                    _filterFade.AddLinkedStage(1800f, 1000, SndCurve.EqPwrSq);
                }
            }
            else if (!_breaker && _prevBreaker)
            {
                float curr = _filterFade != null ? _filterFade.Value : 1800f;
                if (_filterFade == null) _filterFade = new PathLine();
                _filterFade.Clear();
                _filterFade.AddStage(curr, 65535f, 1000, SndCurve.EqPwrSq);
            }
            if (_filterFade != null && _filterFade.Finished && !_breaker) _filterFade = null;
            _filterFade?.Update(dt);
            _filterFreq = _filterFade != null ? (int)_filterFade.Value : 0xFFFF;
        }

        // ------------------------------------------------------------------ per frame
        public void Tick(float dt)
        {
            if (Engine == null) return;
            bool fe = SoundState.Mode == SndGameMode.FrontEnd;
            if (fe != _inFE) EnterWorld(fe);
            GameHooks();
            _status = Engine.TrackStatus(Engine.Tracks[0]);
            // UpdateParams: mixer inputs by music type
            bool playing = _status == 5 || _status == 6 || _status == 2;
            _obj.Set(6, playing && _type == Licensed ? 0x7FFF : 0);
            _obj.Set(7, playing && (_type == Interactive || _type == Splash) ? 0x7FFF : 0);
            _obj.Set(8, playing && _type == Ambience ? 0x7FFF : 0);
            _obj.In[15] = 1;
            if (_status == 3) return;
            if ((_flags & 0x10) != 0 && !fe && (_flags & 0x802) == 0)
            {
                _proj = (_proj + 1) & 3;
                Engine.SetNamedValue("PursuitID", _proj + 1);
                _flags &= ~0x10u;
            }
            // SFXObj_Pathfinder.cpp:989-997: while EATRAX_OFF, the music stays off (UpdateInGame runs its flow instead) as long as a
            // GameFlowSndState of eNIS_Busted 4, eNIS_EndofRace 5, eFE_PostRaceScreen 6, eFE_CameraScreen 7 or eLOAD_BustedLoading 11 is set
            var gf = SoundState.GameFlow;
            bool flowHold = gf[4] != 0 || gf[5] != 0 || gf[7] != 0 || gf[6] != 0 || gf[11] != 0;
            if (_traxState == TraxOff && !flowHold) _traxState = GenEATraxState();
            else UpdateInGame();
            _prevBreaker = _breaker;
            _breaker = SoundState.Mode == SndGameMode.PursuitBreaker;
            UpdatePursuitBreaker(Time.unscaledDeltaTime);
            if (!fe && dt > 0) Flow.Update();
        }

        /// <summary>The game messages the music listens to, from the port's state: busted (SoundAI::MessagePerpBusted →
        /// event 15), evaded while roaming (AICopManager::PursuitIsEvaded → 14), reacquired (SoundAI::AttemptReattachPursuit →
        /// MusicFlow::Reacquire), race restart and the 3-2-1 (Set321).</summary>
        void GameHooks()
        {
            var police = Systems.Police;
            bool pursuit = police != null && police.InPursuit;
            bool arrest = SoundState.Has(SndCtl.NisArrest) || (police != null && police.IsPerpBusted);
            if (arrest && !_wasArrest && IsMusicActive) SendPathEvent(15);
            if (_wasPursuit && !pursuit && !arrest && RaceManager.Active == null && IsMusicActive) SendPathEvent(14);
            _wasArrest = arrest;
            _wasPursuit = pursuit;
            var ai = CopRadio.AI;
            int focus = ai != null ? ai.Focus : -1;
            if (_prevFocus == SpeechAI.kLost && focus == SpeechAI.kPursuitFlow && ai.PursuitState == SpeechAI.kActive) Flow.Reacquire();
            _prevFocus = focus;
            var race = RaceManager.Active;
            bool countdown = race != null && race.Countdown;
            if (countdown && !_wasCountdown) { RestartRace(); _flags &= ~1u; _set321Pending = true; }
            // Set321(true) as the countdown shows its first digit "3" (not at GO; per the race-manager RE)
            if (countdown && _set321Pending && race.CountdownLeft < 4f) { _flags |= 1; _set321Pending = false; }
            if (!countdown && _wasCountdown) { _flags |= 1; _set321Pending = false; }
            _wasCountdown = countdown;
            if (NisPlayer.Active) _flags |= 0x200; else _flags &= ~0x200u;
        }

        /// <summary>ProcessUpdate @0x4F6EA0, then PATH's service.</summary>
        public void Apply(float dt)
        {
            if (Engine == null) return;
            _clock += Time.unscaledDeltaTime * 1000f;
            if (_clearSkip) { _clearSkip = false; _skip = false; }
            // [ours] a start whose marker never reports (missing data) would stay muted for good
            if (_skip) { _skipTime += Time.unscaledDeltaTime; if (_skipTime > 6f) _skip = false; } else _skipTime = 0f;
            Engine.Service((uint)_clock);
            if ((_flags & 0x404) != 4) return;
            if (_queueNext && (_flags & 4) != 0) { DoSendPathEvent(); return; }
            if (_type == Licensed && (_flags & 0x40) == 0)
            {
                if (_skip) return;
                if (_cur != EvStartScreen && _cur != EvStop) NotifyChyron();
            }
            if (_skip) return;
            _status = Engine.TrackStatus(Engine.Tracks[0]);
            if (_status != 5 && _status != 2) return;
            int slot = -1;
            if (_traxState == TraxIG) slot = _type == Licensed || _type == Splash ? 1 : _type == Interactive ? 3 : _type == Ambience ? 5 : -1;
            else if (_traxState == TraxFE) slot = _type == Licensed || _type == Splash ? 0 : _type == Ambience ? 4 : -1;
            int nvol = slot < 0 ? 0 : _obj.Mapped ? _obj.Out(slot, MixMap.Vol) * 100 >> 15 : (int)(MusicVolume * 80f);
            _volume = nvol;
            Engine.Volume(-1, nvol);
            float lp = _obj.Mapped ? _obj.Out(9, MixMap.Freq) : 22000f;
            if (lp <= 0) lp = 22000f;
            Stream.SetLowPass(Mathf.Min(lp, _filterFreq));
        }

        void NotifyChyron()
        {
            var t = CurTrax;
            if (t.PlayTrackIndex < 0 || t.PlayTrackIndex >= Songs.Count) return;
            NowPlaying = Songs[t.PlayTrackIndex];
            _flags |= 0x40;
            if (NowPlaying != null) { Debug.Log($"NFSMW music: {NowPlaying.Artist} - {NowPlaying.Name}"); Chyron?.Invoke(NowPlaying); }
        }
    }
}
