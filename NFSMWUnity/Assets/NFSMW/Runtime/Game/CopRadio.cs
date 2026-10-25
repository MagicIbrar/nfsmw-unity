using System.Collections.Generic;
using NfsMw.Audio;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The police scanner: plays what the speech AI (<see cref="SpeechAI"/>, the SoundAI port) decides, through the
    /// speech queue (<see cref="CopSpeechManager"/>, Speech::Manager + GameSpeech). Each event's sentence is chosen by
    /// the SPCH script (copspeech.evt) for its Csis parameters (speaker, call sign, car colour and make, speed, road,
    /// direction ...), its takes are decoded off the main thread and stitched back to back into one clip, played 2D
    /// through a band-limited, slightly distorted radio [ours: the filter chain]. Transmissions start and end with a
    /// radio chirp: GameSpeech::RadioChirp (PC @0x6FF8E0) plays a STICH_TYPE_STATIC stitch (start: id 4 + bRandom(6);
    /// end: bRandom(4) when bRandom(1.0) &gt; 0.67, else 7 + bRandom(15)) if the event's speech attribute RadioChirp is set
    /// and no chirp is still playing; GameSpeech::UpdateChirps keeps its volume at the speech object's mixer output 13.
    /// Bullhorn events (speech attribute Pan, GameSpeech flag 0x20) skip the radio filter: SFXObj_Speech::UpdateParams
    /// (CARSFX/SFXObj_Speech.cpp:45-70) raises DMIX input 2 and places its 3D controller (MAIN ctl 3,
    /// SFXCTL_3DVoiceActorPos) at the cop car closest to the camera, latched when the event starts and followed while
    /// it stays the closest (else the player's car); the stream's azimuth is the speech object's output 0.
    /// <para>Volume: SndMixer.SpeakerVolume of the speaking unit (GameSpeech::GetVolForSpeaker; 0.85 without the mixer);
    /// low-pass: the mixer's speech cutoff; the mixer is told when speech plays and with what clarity.</para>
    /// <para>Instances: one radio speaks (<see cref="Instance"/>). A bootstrap adds it to the PursuitSystem's object as
    /// soon as <c>Systems.Police</c> exists so the AI runs from the start; PursuitSystem.Radio may add a second
    /// component later, which forwards to the first.</para>
    /// <para><see cref="Say"/> (called by PursuitSystem): while the AI runs it owns the radio, so outside requests are
    /// hints only (a dodged roadblock) and return true for a known event; with <see cref="AiEnabled"/> off the event is
    /// queued directly with neutral parameters. <c>maxLines</c> is ignored (the script decides the sentence).</para>
    /// </summary>
    public sealed class CopRadio : MonoBehaviour, ISpeechOutput
    {
        static CopSpeech _speech;
        public static int LineCount => _speech?.Lines.Count ?? 0;

        /// <summary>The radio that speaks (the first one created).</summary>
        public static CopRadio Instance { get; private set; }
        public static CopSpeechManager Manager { get; private set; }
        public static SpeechAI AI { get; private set; }
        /// <summary>Let the speech AI decide what is said (default). Off: <see cref="Say"/> queues events directly.</summary>
        public static bool AiEnabled = true;
        /// <summary>Logs every event as it starts playing.</summary>
        public static bool LogSpeech;

        public static CopSpeech Speech
        {
            get
            {
                if (_speech == null)
                {
                    GameData.Mount();
                    try { _speech = CopSpeech.Load(GameData.Root); }
                    catch (System.Exception e) { Debug.LogWarning("NFSMW radio: " + e.Message); _speech = new CopSpeech(); }
                }
                return _speech;
            }
        }

        float SpeakerVol(SndMixer mix)
        {
            float v = mix.SpeakerVolume(_speaker);
            return _cellBoost && _speaker > 8 || _cellBoost && _speaker < 1 ? Mathf.Min(1f, v + v) : v;
        }

        AudioSource _voice, _bullhorn, _active;
        AudioLowPassFilter _lp;
        AudioClip _clip;
        // GameSpeech m_Chirper / m_ChirpParams (Vol 0, Az 0, Pitch 0x1000 until UpdateChirps sets the volume)
        StitchPlayer _chirper;
        int _chirpVol;
        // SFXObj_Speech: the bullhorn position (fPosition, bFresh, m_pClosestCar) and its 3D controller
        SndPos _actorPos;
        Vector3 _actorPosition;
        bool _fresh, _pan;
        PlayerCar _closestCar;
        bool _playing, _paused;
        int _speaker, _clarity;
        /// <summary>GameSpeech::GetVolForSpeaker (GameSpeech.cpp:539-547): a CellCall of bucket_01 / call_07 plays at twice the
        /// default speaker volume (slot 14), clamped.</summary>
        bool _cellBoost;
        bool _initFailed;

        // ---------------------------------------------------------------------------------------------- lifetime

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var go = new GameObject("NFSMW CopRadio Boot") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<CopRadioBoot>();
        }

        void Awake()
        {
            if (Instance == null) Become();
        }

        CopRadio Primary()
        {
            if (Instance == null) Become();
            return Instance;
        }

        void Become()
        {
            Instance = this;
            _voice = MakeSource("CopRadio Voice", true);
            _lp = _voice.GetComponent<AudioLowPassFilter>();
            _bullhorn = MakeSource("CopRadio Bullhorn", false);
            try { SndMixer.Ensure(); } catch (System.Exception) { }
        }

        AudioSource MakeSource(string name, bool radio)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            Setup(src);
            if (radio)
            {
                go.AddComponent<AudioHighPassFilter>().cutoffFrequency = 450f;
                go.AddComponent<AudioLowPassFilter>().cutoffFrequency = 3600f;
                go.AddComponent<AudioDistortionFilter>().distortionLevel = 0.35f;
            }
            else go.AddComponent<AudioLowPassFilter>().cutoffFrequency = 6000f;
            return src;
        }

        static void Setup(AudioSource s)
        {
            s.spatialBlend = 0f;
            s.playOnAwake = false;
            s.loop = false;
            s.volume = 0.85f;
            s.ignoreListenerPause = false;
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            if (Manager != null)
            {
                Manager.Reset();
                Manager.Output = null;
            }
            AI?.ResetPursuit(true);
            if (_clip) Destroy(_clip);
        }

        /// <summary>Loads the speech data and attributes and builds the manager and the AI once.</summary>
        void EnsureSystems()
        {
            if (Manager != null || _initFailed) return;
            try
            {
                var speech = Speech;
                if (speech.Script == null || speech.Script.EventNames.Count == 0) { _initFailed = true; return; }
                GameData.Mount();
                var db = GameData.Attributes;
                Manager = new CopSpeechManager(speech.Script, CopSpeechRules.LoadEvents(db), CopSpeechRules.LoadTune(db));
                AI = new SpeechAI(Manager);
                Manager.PlayStarted += e => { if (LogSpeech) Debug.Log($"NFSMW radio: {e} | {AI?.Describe()}"); };
            }
            catch (System.Exception e)
            {
                _initFailed = true;
                Debug.LogWarning("NFSMW radio: speech AI unavailable: " + e);
            }
        }

        // ---------------------------------------------------------------------------------------------- outside API

        /// <summary>An outside request for a speech event (by its csi name); false if the event has no lines.</summary>
        public bool Say(string speechEvent, int maxLines = 2)
        {
            var r = Primary();
            if (r != this) return r.Say(speechEvent, maxLines);
            EnsureSystems();
            if (Manager == null || !Manager.HasEvent(speechEvent)) return false;
            if (AiEnabled && AI != null) return AI.Hint(speechEvent);
            Manager.Schedule(speechEvent, NeutralArgs(speechEvent), null);
            return true;
        }

        /// <summary>The radio is talking or has events queued.</summary>
        public bool Busy
        {
            get
            {
                var r = Instance ?? this;
                return r == this ? Manager != null && (Manager.Busy || Manager.Pending > 0) : r.Busy;
            }
        }

        /// <summary>Parameters for an event asked for without context: the first speaker with lines, first enum values.</summary>
        static Dictionary<string, int> NeutralArgs(string ev)
        {
            var s = Manager.Script;
            var d = new Dictionary<string, int>();
            var e = s.GetEvent(ev);
            if (e == null) return d;
            var speakers = s.Speakers(ev);
            for (int i = 0; i < e.ParamNames.Length; i++)
            {
                string p = e.ParamNames[i];
                if (p == "speaker_id") d[p] = speakers.Count > 0 ? speakers[Random.Range(0, speakers.Count)] : 1;
                else d[p] = 1;
            }
            return d;
        }

        // ---------------------------------------------------------------------------------------------- frame

        void Update()
        {
            var r = Primary();
            if (r != this) return;
            EnsureSystems();
            if (Manager == null) return;
            Manager.Output = this;
            Manager.Now = Time.timeAsDouble;
            // EAXSound::StartNewGamePlay (EAXSound.cpp:1319): the two-player mix turns speech off (IsSpeechEnabled 0,
            // Speech::Manager::Init(SPEECH_SPLITSCREEN_MODE): ScheduleSpeechPartII and PlayStream drop everything)
            Manager.Enabled = !GameSession.SplitScreen;
            bool paused = SoundState.Paused;
            Manager.Paused = paused;
            if (paused != _paused)
            {
                _paused = paused;
                if (_active) { if (paused) _active.Pause(); else _active.UnPause(); }
            }
            if (_playing && !_paused && _active && !_active.isPlaying) _playing = false;

            var police = Systems.Police;
            if (AI != null && AiEnabled && police != null)
            {
                AI.Now = Manager.Now;
                try { AI.Tick(police, Time.deltaTime); }
                catch (System.Exception e) { Debug.LogException(e); }
            }
            try { Manager.Update(); }
            catch (System.Exception e) { Debug.LogException(e); }

            // mixer: volume per speaker, speech low-pass, ducking inputs
            var mix = SndMixer.Instance;
            float vol = mix ? StreamVolume(SpeakerVol(mix)) : 0.85f;
            if (_active) _active.volume = vol;
            if (_lp) _lp.cutoffFrequency = Mathf.Min(3600f, mix ? mix.SpeechLowPass : 22000f);
            if (mix)
            {
                mix.SetSpeechInputs(_playing, _clarity);
                UpdateSpeechObject(mix);
            }
            UpdateChirps(mix);
        }

        /// <summary>GameSpeech::Update (GameSpeech.cpp:247-252, PC @0x71DFF0): the stream volume is GetVolForSpeaker (a Q15 mixer
        /// output) × 0.01 × 32767, handed to SndStrmWrapper::SetVol, which scales by 1/127 (@0x4BAD20: × 0.007874016) into
        /// SNDSTRM_setvol: ×258 overall, so any non-zero mixer volume plays the speech at full level and only a zero output
        /// (NIS, pause) mutes it [?: SNDSTRM_setvol taken to clamp at 1.0].</summary>
        static float StreamVolume(float mixerVol) => Mathf.Min(1f, mixerVol * 32767f * 0.01f * 32767f / 127f);

        /// <summary>GameSpeech::UpdateChirps: the chirp's volume follows the speech object's output 13; a finished chirp goes.</summary>
        void UpdateChirps(SndMixer mix)
        {
            if (_chirper == null) return;
            _chirpVol = mix && mix.Speech.Mapped ? mix.Speech.Out(13, MixMap.Vol) : 0x6CCC;
            if (!_chirper.Update(_chirpVol, 0, _paused ? 0f : 1f)) { _chirper.Stop(); _chirper = null; }
        }

        /// <summary>SFXObj_Speech::UpdateParams + GameSpeech::Update's input 2 / azimuth: the bullhorn's position (MAIN ctl 3) and
        /// the cell-call input 4 (no cell calls in the port: 0).</summary>
        void UpdateSpeechObject(SndMixer mix)
        {
            var obj = mix.Speech;
            obj.Set(4, 0);
            obj.Set(2, _playing && _pan ? 0x7FFF : 0);
            if (_actorPos == null) _actorPos = new SndPos(new SndObj(mix.Map, MixMap.ObjectId(true, MixMap.Main, 0, 3)));
            bool input2 = obj.Get(2) != 0;
            if (!_fresh)
            {
                if (input2)
                {
                    var closest = ClosestCopCarToCamera();
                    if (closest) { _closestCar = closest; _actorPosition = closest.transform.position; _fresh = true; }
                    else
                    {
                        _closestCar = null;
                        var p = SndMixer.PlayerRef;
                        if (p) _actorPosition = p.transform.position;
                    }
                }
            }
            else if (!input2) { _fresh = false; _closestCar = null; }
            else if (_closestCar && ClosestCopCarToCamera() == _closestCar) _actorPosition = _closestCar.transform.position;
            _actorPos.Update(_actorPosition, Vector3.zero, Time.deltaTime);
            // GameSpeech::Update while playing: m_strm->SetAz(output 0) with flag 0x20, else 0
            if (_active == _bullhorn && _playing) _bullhorn.panStereo = _pan && obj.Mapped ? SndVoice.Pan(obj.Out(0, MixMap.Azim)) : 0f;
        }

        /// <summary>GetClosestCopCarToCamera (EAXSound): the cop car (an EAX car state with the cop context) nearest the view.</summary>
        static PlayerCar ClosestCopCarToCamera()
        {
            var cam = SndMixer.Cam;
            if (!cam) return null;
            PlayerCar best = null;
            float bd = float.MaxValue;
            foreach (var e in EngineAudio.All)
            {
                if (!e || !e.IsCop || !e.Car) continue;
                float d = (e.Car.transform.position - cam.position).sqrMagnitude;
                if (d < bd) { bd = d; best = e.Car; }
            }
            return best;
        }

        // ---------------------------------------------------------------------------------------------- ISpeechOutput

        public bool Playing => _playing;

        public double PlayedTime => _playing && _active && _clip ? _active.timeSamples / (double)Mathf.Max(1, _clip.frequency) : 0;

        public void Begin(ScheduledSpeech e)
        {
            Stop();
            int total = 0;
            foreach (var p in e.Pcm) total += p.Length;
            if (total == 0) return;
            var data = new float[total];
            int at = 0;
            foreach (var p in e.Pcm) { System.Array.Copy(p, 0, data, at, p.Length); at += p.Length; }
            if (_clip) Destroy(_clip);
            _clip = AudioClip.Create(e.Name, total, 1, e.SampleRate > 0 ? e.SampleRate : 24000, false);
            _clip.SetData(data, 0);
            _speaker = e.Actor?.SpeakerID ?? 0;
            _cellBoost = e.Name == CopSpeechManager.CellCall && e.Arg("cell_call_bucket") == 2 && e.Arg("cell_call_number") == 64;
            _clarity = Mathf.RoundToInt(Mathf.Clamp(e.Info.Clarity, 0, 10) * 0.1f * 32767f);
            _pan = e.Info.Pan;
            _active = e.Info.Pan ? _bullhorn : _voice;
            _bullhorn.panStereo = 0f;
            _active.clip = _clip;
            _active.volume = SndMixer.Instance ? StreamVolume(SpeakerVol(SndMixer.Instance)) : 0.85f;
            _active.Play();
            if (_paused) _active.Pause();
            _playing = true;
        }

        public void Stop()
        {
            if (_voice) _voice.Stop();
            if (_bullhorn) _bullhorn.Stop();
            _playing = false;
        }

        /// <summary>GameSpeech::RadioChirp (PC @0x6FF8E0): the caller has checked the event's RadioChirp attribute (flag 0x400);
        /// nothing while a chirp still plays.</summary>
        public void Chirp(int type)
        {
            if (_chirper != null) return;
            int id;
            if (type > 0) id = Random.value > 0.67f ? Random.Range(0, 4) : Random.Range(0, 15) + 7;
            else id = Random.Range(0, 6) + 4;
            var st = StitchPlayer.Get(2, id);   // STICH_TYPE_STATIC
            if (st == null) return;
            _chirper = new StitchPlayer(transform, 2, st, _chirpVol, 0);
            if (!_chirper.Playing) _chirper = null;
        }
    }

    /// <summary>Starts the police radio (and with it the speech AI) as soon as the police system exists.</summary>
    sealed class CopRadioBoot : MonoBehaviour
    {
        float _wait;

        void Update()
        {
            _wait -= Time.unscaledDeltaTime;
            if (_wait > 0f) return;
            _wait = 0.5f;
            var police = Systems.Police;
            if (police != null && CopRadio.Instance == null) police.gameObject.AddComponent<CopRadio>();
        }
    }
}
