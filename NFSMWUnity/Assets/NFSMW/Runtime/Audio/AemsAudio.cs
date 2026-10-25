using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// The AEMS runtime of one sound owner (a car, the helicopter, a traffic slot): its Csis class instances run the
    /// banks' compiled patches (<see cref="AemsInstance"/>, 50 Hz = SNDAEMSI_timerupdate at gVariableTimerPeriod 20 ms)
    /// and their player nodes play bank samples on an <see cref="AemsMixer"/> rendered in OnAudioFilterRead. Lives on
    /// its own child object (a car's own AudioSource belongs to EngineAudio).
    /// </summary>
    public sealed class AemsHost : MonoBehaviour
    {
        public readonly AemsMixer Mixer = new AemsMixer();
        readonly List<CsisObj> _objs = new List<CsisObj>();
        float _acc;
        int _rate;
        /// <summary>Owner gain (pause / attach ramps) on top of the patch volumes [ours].</summary>
        public float Gain { get => Mixer.Gain; set => Mixer.Gain = value; }

        public static AemsHost Create(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.dopplerLevel = 0f;
            src.loop = true;
            src.volume = 1f;
            var h = go.AddComponent<AemsHost>();
            h._rate = AudioSettings.outputSampleRate;
            // a silent clip keeps the source (and so the filter chain) running
            src.clip = AudioClip.Create(name, 1, 1, Mathf.Max(8000, h._rate), false);
            src.Play();
            go.AddComponent<SendTap>();
            return h;
        }

        static readonly Dictionary<string, AemsBank> Banks = new Dictionary<string, AemsBank>();

        /// <summary>A bank under SOUND (relative path, e.g. "SKIDS/SKID_BIG_MB.abk"); null if absent.</summary>
        public static AemsBank Bank(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (Banks.TryGetValue(rel, out var b)) return b;
            GameData.Mount();
            b = AemsBank.Load(Path.Combine(GameData.Root ?? "", "SOUND", rel.Replace('/', Path.DirectorySeparatorChar)));
            Banks[rel] = b;
            return b;
        }

        internal void Add(CsisObj o) { if (!_objs.Contains(o)) _objs.Add(o); }

        void Update()
        {
            // SNDAEMSI_timerupdate runs on the sound timer, independent of the game's time scale
            _acc += Time.unscaledDeltaTime;
            if (_acc > 0.1f) _acc = 0.1f;
            while (_acc >= AemsInstance.TimerPeriodMs * 0.001f)
            {
                _acc -= AemsInstance.TimerPeriodMs * 0.001f;
                for (int i = _objs.Count - 1; i >= 0; i--)
                    if (!_objs[i].Tick()) _objs.RemoveAt(i);
            }
        }

        void OnAudioFilterRead(float[] data, int channels) => Mixer.Render(data, channels, _rate);

        void OnDestroy()
        {
            foreach (var o in _objs) o.Kill();
            _objs.Clear();
            Mixer.StopAll();
        }
    }

    /// <summary>
    /// A Csis class instance (Csis::Class::CreateInstance on the generated SND_GEN classes): one module instance in
    /// every given bank that compiles the class (the bank's graph decides whether it answers, e.g. CAR checks car_class
    /// and sPU_or_EE). Set = SetXxx + CommitMemberData; Release = delete (the destructor trigger).
    /// </summary>
    public sealed class CsisObj
    {
        public readonly string Class;
        readonly List<AemsInstance> _inst = new List<AemsInstance>();
        readonly int[] _p;
        bool _released;

        public CsisObj(AemsHost host, string cls, int[] parameters, params string[] banks)
        {
            Class = cls;
            _p = (int[])parameters.Clone();
            if (!host) return;
            foreach (var rel in banks)
            {
                var b = AemsHost.Bank(rel);
                var m = b?.Find(cls);
                var i = m != null ? AemsInstance.Create(m, host.Mixer, _p) : null;
                if (i != null) _inst.Add(i);
            }
            if (_inst.Count > 0) host.Add(this);
        }

        /// <summary>At least one module instance is alive (its graph has not destroyed itself).</summary>
        public bool Alive
        {
            get
            {
                foreach (var i in _inst) if (i.Alive) return true;
                return false;
            }
        }

        public int this[int index]
        {
            get => index >= 0 && index < _p.Length ? _p[index] : 0;
            set
            {
                if (index < 0 || index >= _p.Length || _p[index] == value) return;
                _p[index] = value;
                foreach (var i in _inst) i.SetParam(index, value);
            }
        }

        public void Release()
        {
            if (_released) return;
            _released = true;
            foreach (var i in _inst) i.Release();
        }

        internal void Kill() { foreach (var i in _inst) i.Kill(); }

        internal bool Tick()
        {
            bool any = false;
            foreach (var i in _inst)
            {
                if (!i.Alive) continue;
                i.Tick();
                any |= i.Alive;
            }
            return any;
        }
    }
}
