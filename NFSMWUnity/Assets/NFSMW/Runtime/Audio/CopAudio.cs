using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// A cop car's siren: the AI side (AIVehiclePursuit.cpp siren state: bleeps while the pursuit is being called
    /// in, yelp/scream at high intensity, wail otherwise, alternating by the pursuitlevels Siren* periods; die when
    /// destroyed; wail in a roadblock) and CARSFX_Siren (EAXSound/CARSFX/CARSFX_Siren.cpp: the state is resampled
    /// every 0..3 s, volume = mixer slot 1, pan slot 0, pitch slot 3). The cop's lights decide whether it runs at all:
    /// the pursuit system's own siren AudioSource is muted and only read for that (its volume follows the lights).
    /// SIREN_MB sample per state [?]: wail 3, yelp 1, scream 2, die 5.
    /// </summary>
    public sealed class CopSiren : MonoBehaviour, ISndUnit
    {
        public enum State { Off = -1, Wail = 0, Yelp = 1, Scream = 2, Die = 3 }
        const string Bank = "IG_GLOBAL/SIREN_MB.abk";
        static readonly int[] Slot = { 3, 1, 2, 5 };

        public CopCar Cop;
        SndObj _obj;
        SndVoice _voice;
        EngineAudio _engine;
        State _ai = State.Off, _snd = State.Off, _prevSnd = State.Off;
        float _tSiren0, _tSiren1, _tSiren2, _tState, _tDeath, _variation = -1;
        bool _init;
        float _time;

        public static CopSiren Attach(CopCar cop, EngineAudio engine)
        {
            var s = cop.gameObject.AddComponent<CopSiren>();
            s.Cop = cop;
            var mixer = SndMixer.Ensure();
            int inst = engine ? engine.MixInstance : -1;
            s._obj = new SndObj(mixer.Map, MixMap.ObjectId(false, SndMixer.StateOr(MixMap.CopCar, inst), Mathf.Max(0, inst), 17));
            if (engine && engine.MixInstance >= 0) s._obj.Attach(true);
            s._voice = new SndVoice(cop.Car ? cop.Car.transform : cop.transform, "CopSiren");
            s._engine = engine;
            s._tState = Random.Range(6f, 8f);
            mixer.Register(s);
            return s;
        }

        void OnDestroy() { if (SndMixer.Instance) SndMixer.Instance.Unregister(this); }

        static float Attr(string field, float fb)
        {
            var police = Systems.Police;
            int heat = police != null ? Mathf.Clamp(police.Heat, 1, 6) : 1;
            var c = GameData.Attributes?.Get("pursuitlevels", "heat_0" + heat);
            return c != null && c.Has(field) ? c.Float(field) : fb;
        }

        public void Tick(float dt)
        {
            if (!Cop) return;
            _time += dt;
            var police = Systems.Police;
            bool lights = Cop.SirenSource ? Cop.SirenSource.volume > 0.01f : Cop.InPursuit;
            if (Cop.SirenSource) Cop.SirenSource.mute = true;
            // AIVehiclePursuit::Update siren part
            if (Cop.Destroyed) _ai = State.Die;
            else if (police == null || !Cop.InPursuit || !police.InPursuit) _ai = lights && Cop.Roadblock != null ? State.Wail : State.Off;
            else
            {
                float duration = police.PursuitTime;
                bool high = police.Heat >= 2 || duration >= 150f;    // SoundAI::IsHighIntensity (speechtune HighIntensityMark 2, 150 s)
                if (duration < 5f)
                {
                    if (_variation < 0) _variation = Attr("SirenInitMinPeriod", 1f) + Random.Range(0f, Attr("SirenInitVariation", 1f));
                    if (_time - _tSiren0 > _variation && _ai == State.Off) { _tSiren1 = _time; _ai = State.Yelp; }
                    if (_ai == State.Yelp && _time - _tSiren1 > 0.5f)
                    {
                        _ai = State.Off;
                        _variation = Attr("SirenInitMinPeriod", 1f) + Random.Range(0f, Attr("SirenInitVariation", 1f));
                        _tSiren0 = _time;
                    }
                }
                else
                {
                    float tStart = _time - _tSiren0;
                    bool initializing = tStart < 6f;
                    if (!initializing && _init) { _tSiren1 = _time; _ai = high ? State.Yelp : State.Wail; }
                    _init = initializing;
                    if (initializing) _ai = !high ? State.Yelp : tStart < 3f ? State.Scream : State.Yelp;
                    else if (Cop.InFormation && police.Busting > 0)
                    {
                        if (high)
                        {
                            float tScream = _time - _tSiren2;
                            if (_ai != State.Scream && tScream > Attr("SirenScreamPeriod", 4f)) { _ai = State.Scream; _tSiren2 = _time; }
                            else if (tScream > Attr("SirenMaxScreamTime", 2f)) { _ai = State.Yelp; _tSiren2 = _time; }
                        }
                        else _ai = State.Yelp;
                        _tSiren1 = _time;
                    }
                    else
                    {
                        float tConst = _time - _tSiren1;
                        float wail = Attr("SirenWailPeriod", 8f), yelp = Attr("SirenMaxYelpTime", 2f);
                        if (tConst > wail + yelp) { _ai = State.Wail; _tSiren1 = _time; }
                        else if (tConst > wail) _ai = State.Yelp;
                    }
                }
                if (!lights) _ai = State.Off;
                if (Cop.Roadblock != null && lights) _ai = State.Wail;
            }
        }

        public void Apply(float dt)
        {
            if (!Cop || _voice == null) return;
            bool enabled = _ai != State.Off && !(_snd == State.Die && _time - _tDeath > 10f);
            if (!enabled)
            {
                _obj.Set(5, 0);
                _voice.Stop();
                _snd = State.Off;
                return;
            }
            _obj.Set(5, 0x7FFF);
            // UpdateSirenState: hold the sounding state 0..3 s unless the AI screams
            _tState -= dt;
            _prevSnd = _snd;
            if (_tState < 0 || _ai == State.Scream || _snd == State.Off)
            {
                _tState = Random.Range(0f, 3f);
                _snd = _ai;
            }
            if (_snd == State.Die && _prevSnd != State.Die) _tDeath = _time;
            if (_snd != State.Die) _tDeath = _time;
            if (_snd == State.Off) { _voice.Stop(); return; }
            var clip = SoundLibrary.ClipAt(Bank, Slot[(int)_snd]);
            if (_snd == State.Die) { if (_prevSnd != State.Die) _voice.Once(clip); }
            else _voice.Loop(clip);
            float vol = _obj.Mapped ? _obj.Vol(1) : 0f;
            float pitch = _obj.Mapped ? _obj.Pitch(3) : 1f;
            float gain = _engine ? _engine.ConnectGain : 1f;
            _voice.Set(vol * (SoundState.Paused ? 0f : 1f) * gain, pitch, _obj.Out(0, MixMap.Azim));
        }
    }

    /// <summary>Gives every cop car the game's COPCAR sound state: AEMS engine, skids and siren.</summary>
    public static class CopAudio
    {
        static float _next;

        /// <summary>Called by <see cref="SndMixer"/> twice a second: attaches audio to cop cars that have none.</summary>
        public static void Scan()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            foreach (var cop in Object.FindObjectsByType<CopCar>(FindObjectsSortMode.None))
            {
                if (!cop || !cop.Car || cop.Car.GetComponent<EngineAudio>()) continue;
                string name = cop.Car.Spec != null ? cop.Car.Spec.Name : cop.Car.name;
                var engine = EngineAudio.AttachCop(cop.Car, name);
                CarSfx.Attach(cop.Car, false);
                CopSiren.Attach(cop, engine);
            }
        }
    }
}
