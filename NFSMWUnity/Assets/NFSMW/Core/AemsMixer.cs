using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// The bank-voice side of AEMS (<see cref="IAemsHost"/>): plays BNKl patches for the player nodes and mixes them
    /// to stereo. SNDBANK_play(patnum) plays bank slot <c>patnum</c> (the sample-group entries hold BNKl indices; slot 0
    /// is empty in every MW bank). Voice parameters follow SNDAEMSI_bank* (@0x819FBD..0x81A1AE): pitch x4096 (applied
    /// to the sample rate), volume Q15 linear (programmedVol = v/32767), azimuth bAngle → equal-power pan
    /// [ours: the port's 2D pan law], low/high-pass cutoffs in Hz as one-pole filters [?: the PC driver's filter
    /// shapes], dry level 0..127 as a gain, FX wet 0..127 kept for the reverb send. A sustained sample (PT loop
    /// points 0x86/0x87) loops between them until stopped and reports timeleft 0.
    /// Thread use: the game thread calls the IAemsHost methods, the audio thread <see cref="Render"/>; both lock.
    /// </summary>
    public sealed class AemsMixer : IAemsHost
    {
        sealed class Voice
        {
            public int Handle;
            public BankSound S;
            public double Pos;
            public float Pitch = 1f, Vol = 1f, Dry = 1f, Wet, PanL = 0.7071f, PanR = 0.7071f;
            public float LowPass = 65535f, HighPass;
            public float LpL, LpR, HpL, HpR, HpInL, HpInR;
            public bool Paused, Done;
        }

        readonly List<Voice> _voices = new List<Voice>();
        readonly object _lock = new object();
        int _next = 1;
        /// <summary>Gain on everything this mixer renders (mute while paused, attach ramps) [ours].</summary>
        public volatile float Gain = 1f;
        /// <summary>Most recent average FX wet level of the live voices (0..1), for a reverb send [ours].</summary>
        public float WetLevel { get; private set; }
        /// <summary>Called when a ControlClass node creates/updates/releases another Csis class (op 0/1/2).</summary>
        public Func<string, int, int, int[], int> OnControlClass;

        static readonly Dictionary<AemsBank, Dictionary<int, BankSound>> Sounds = new Dictionary<AemsBank, Dictionary<int, BankSound>>();

        /// <summary>The bank's BNKl sounds by slot (decoded once per bank).</summary>
        public static Dictionary<int, BankSound> SoundsOf(AemsBank bank)
        {
            lock (Sounds)
            {
                if (Sounds.TryGetValue(bank, out var m)) return m;
                m = new Dictionary<int, BankSound>();
                try { foreach (var s in SoundBank.Read(bank.D).Sounds) m[s.Index] = s; }
                catch (Exception) { }
                Sounds[bank] = m;
                return m;
            }
        }

        public int Live { get { lock (_lock) return _voices.Count; } }

        public int Play(AemsBank bank, int patnum, AemsPlayOpts o)
        {
            if (bank == null || !SoundsOf(bank).TryGetValue(patnum, out var s) || s.Data == null || s.Data.Length == 0) return -1;
            var v = new Voice { S = s, Pitch = o.PitchMult / 4096f, Vol = o.Vol / 127f, Dry = o.Dry / 127f, Wet = o.FxWet / 127f, LowPass = o.LowPass, HighPass = o.HighPass };
            SetPan(v, o.Azimuth);
            lock (_lock)
            {
                v.Handle = _next++;
                if (_next < 1) _next = 1;
                _voices.Add(v);
            }
            return v.Handle;
        }

        static void SetPan(Voice v, int azim)
        {
            // [ours] the 2D pan law of SndVoice.Pan: sin(azimuth) x 0.8, equal power
            float pan = (float)Math.Max(-1, Math.Min(1, Math.Sin(azim / 65536.0 * Math.PI * 2) * 0.8));
            v.PanL = (float)Math.Sqrt(0.5 * (1 - pan));
            v.PanR = (float)Math.Sqrt(0.5 * (1 + pan));
        }

        Voice Get(int h)
        {
            foreach (var v in _voices) if (v.Handle == h) return v;
            return null;
        }

        public void Stop(int handle)
        {
            lock (_lock) { var v = Get(handle); if (v != null) _voices.Remove(v); }
        }

        public void StopAll() { lock (_lock) _voices.Clear(); }

        public void Pause(int handle)
        {
            lock (_lock) { var v = Get(handle); if (v != null) { v.Vol = 0f; v.Pitch = 0f; } }
        }

        public bool Alive(int handle)
        {
            lock (_lock) { var v = Get(handle); return v != null && !v.Done; }
        }

        public void Set(int handle, int type, int value)
        {
            lock (_lock)
            {
                var v = Get(handle);
                if (v == null) return;
                switch (type)
                {
                    case 0: v.Pitch = value / 4096f; break;
                    case 2: v.Vol = value / 32767f; break;
                    case 3: SetPan(v, value); break;
                    case 5: v.Wet = value / 127f; break;
                    case 6: v.LowPass = value; break;
                    case 7: v.HighPass = value; break;
                    case 8: v.Dry = value / 127f; break;
                }
            }
        }

        public void Position(int handle, out int frame, out int frames, out int rate, out bool sustain)
        {
            lock (_lock)
            {
                var v = Get(handle);
                frame = v != null ? (int)v.Pos : 0;
                frames = v != null ? v.S.Samples : 0;
                rate = v != null ? v.S.SampleRate : 22050;
                sustain = v != null && v.S.Loops;
            }
        }

        public int ControlClass(string cls, int id, int op, int[] parameters) => OnControlClass != null ? OnControlClass(cls, id, op, parameters) : (op == 0 ? 1 : 0);

        /// <summary>Mixes every live voice into <paramref name="data"/> (adds; interleaved, <paramref name="channels"/>).</summary>
        public void Render(float[] data, int channels, int outRate)
        {
            if (outRate <= 0 || channels <= 0) return;
            int frames = data.Length / channels;
            float gain = Gain;
            float wetSum = 0f; int wetN = 0;
            lock (_lock)
            {
                for (int vi = _voices.Count - 1; vi >= 0; vi--)
                {
                    var v = _voices[vi];
                    if (v.Done) { _voices.RemoveAt(vi); continue; }
                    var s = v.S;
                    var L = s.Data;
                    var Rr = s.DataR ?? s.Data;
                    int n = L.Length;
                    bool loop = s.Loops && s.LoopEnd < n;
                    int ls = loop ? s.LoopStart : 0, le = loop ? s.LoopEnd + 1 : n;   // [?] 0x87 = last loop sample
                    double step = s.SampleRate * (double)v.Pitch / outRate;
                    float g = v.Vol * v.Dry * gain;
                    float lpA = v.LowPass >= 20000f ? 1f : 1f - (float)Math.Exp(-2 * Math.PI * Math.Max(20f, v.LowPass) / outRate);
                    float hpA = v.HighPass <= 10f ? 0f : (float)Math.Exp(-2 * Math.PI * v.HighPass / outRate);
                    wetSum += v.Wet * v.Vol; wetN++;
                    double pos = v.Pos;
                    for (int i = 0; i < frames; i++)
                    {
                        int ia = (int)pos;
                        if (ia >= n) { v.Done = true; break; }
                        int ib = ia + 1;
                        if (loop && ib >= le) ib = ls;
                        if (ib >= n) ib = n - 1;
                        float f = (float)(pos - ia);
                        float a = L[ia] + (L[ib] - L[ia]) * f;
                        float b = Rr[ia] + (Rr[ib] - Rr[ia]) * f;
                        v.LpL += lpA * (a - v.LpL); v.LpR += lpA * (b - v.LpR);
                        a = v.LpL; b = v.LpR;
                        if (hpA > 0f)
                        {
                            float ha = hpA * (v.HpL + a - v.HpInL), hb = hpA * (v.HpR + b - v.HpInR);
                            v.HpInL = a; v.HpInR = b; v.HpL = ha; v.HpR = hb; a = ha; b = hb;
                        }
                        if (channels >= 2)
                        {
                            data[i * channels] += a * g * v.PanL * 1.4142f;
                            data[i * channels + 1] += b * g * v.PanR * 1.4142f;
                        }
                        else data[i] += (a + b) * 0.5f * g;
                        pos += step;
                        if (loop && pos >= le) pos = ls + (pos - le) % Math.Max(1, le - ls);
                    }
                    v.Pos = pos;
                }
            }
            WetLevel = wetN > 0 ? Math.Min(1f, wetSum / wetN) : 0f;
        }
    }
}
