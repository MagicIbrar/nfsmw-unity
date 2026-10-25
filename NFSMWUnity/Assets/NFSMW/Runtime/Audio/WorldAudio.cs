using System;
using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// cStichWrapper / cSampleWarpper (EAXSound/STICH_PlayBack.cpp): a stitch's samples from its bank (collision
    /// Stich_Collision_MB, woosh Stitch_Whoosh_MB, static Stitch_Static_MB; SND_SampleRef.SampleIndex n = bank slot
    /// n + 1, as the woosh bank's 23 slots for indices 0..22 show), each delayed by its Offset ms, at its Volume minus a
    /// random 0..|RND_Vol|, pitch Pitch/4096 minus a random 0..|RND_Pitch|, azimuth Params.Az + its own Az. Update
    /// re-applies the volume (local x Params.Vol, Q15) and azimuth every frame.
    /// </summary>
    public sealed class StitchPlayer
    {
        public static readonly string[] Banks = { "IG_GLOBAL/Stich_Collision_MB.abk", "IG_GLOBAL/Stitch_Whoosh_MB.abk", "IG_GLOBAL/Stitch_Static_MB.abk" };

        sealed class Sample
        {
            public StitchPlayer Owner; public SndVoice V; public int Vol, Az, Priority, Frame; public float Pitch, End; public bool Dead;
            public bool Live => !Dead && V != null && V.Src != null && (Time.time <= End || V.Playing);
            public void Kill() { Dead = true; if (V != null && V.Src) UnityEngine.Object.Destroy(V.Src.gameObject); }
        }

        /// <summary>The samples of each STICH_TYPE that are queued or playing (cSTICH_PlayBack::mQueuedSampleList[type] +
        /// cSampleWarpper::GetList(type)), in creation order.</summary>
        static readonly List<Sample>[] Live = { new List<Sample>(), new List<Sample>(), new List<Sample>() };
        /// <summary>cStichWrapper::Play: more than 25 queued + playing samples of a type prune the excess.</summary>
        const int MaxSamplesPerType = 25;

        readonly List<Sample> _s = new List<Sample>();
        public bool Playing { get; private set; }

        public static Stitch Get(int type, int index)
        {
            var l = SoundLibrary.Stitches;
            return type >= 0 && type < l.Length && index >= 0 && index < l[type].Count ? l[type][index] : null;
        }

        public StitchPlayer(Transform parent, int type, Stitch st, int vol, int azim)
        {
            if (st == null || type < 0 || type > 2) return;
            foreach (var s in st.Samples)
            {
                var clip = SoundLibrary.ClipAt(Banks[type], s.SampleIndex + 1);
                if (!clip) continue;
                int local = s.Volume - (s.RndVol != 0 ? UnityEngine.Random.Range(0, Math.Abs(s.RndVol)) : 0);
                int pitch = s.Pitch - (s.RndPitch != 0 ? UnityEngine.Random.Range(0, Math.Abs(s.RndPitch)) : 0);
                var v = new SndVoice(parent, "Stitch");
                float p = pitch > 0 ? pitch / 4096f : 1f;
                v.Once(clip, s.OffsetMs / 1000f);
                var smp = new Sample
                {
                    Owner = this, V = v, Vol = local, Az = s.Azimuth, Pitch = p, Priority = s.Priority, Frame = Time.frameCount,
                    End = Time.time + s.OffsetMs / 1000f + clip.length / Mathf.Max(0.05f, p) + 0.05f,
                };
                _s.Add(smp);
                Live[type].Add(smp);
            }
            Playing = _s.Count > 0;
            Prune(type);
            Update(vol, azim, 1f);
        }

        /// <summary>
        /// cStichWrapper::Play's voice limit (PC @0x4CF520; STICH_PlayBack.cpp:203-224): num = queued + playing samples of the
        /// type - 25; while some remain, cSTICH_PlayBack::Prune (@0x4CEB80) drops queued samples with Priority &lt;= p (the
        /// requests this frame, the new stitch's included), then cSampleWarpper::Prune (@0x4CECE0) kills playing ones with
        /// Priority &lt;= p, in list order; p runs 0..10. The port's "queued" samples are the ones created this frame.
        /// </summary>
        static void Prune(int type)
        {
            var l = Live[type];
            l.RemoveAll(x => !x.Live);
            int num = l.Count - MaxSamplesPerType;
            for (int prio = 0; num > 0 && prio <= 10; prio++)
            {
                for (int pass = 0; pass < 2 && num > 0; pass++)
                    for (int i = 0; i < l.Count && num > 0; i++)
                    {
                        var x = l[i];
                        bool queued = x.Frame == Time.frameCount;
                        if (queued != (pass == 0) || x.Priority > prio) continue;
                        x.Kill();
                        l.RemoveAt(i--);
                        num--;
                    }
            }
        }

        /// <summary>cStichWrapper::Update / cSampleWarpper::Update: vol Q15 (local x Params.Vol >> 15), azimuth bAngle
        /// (Params.Az + the sample's Az); returns false once every sample finished (or was pruned).</summary>
        public bool Update(int vol, int azim, float mul)
        {
            if (!Playing) return false;
            bool any = false;
            foreach (var x in _s)
            {
                if (x.Dead || x.V.Src == null) continue;
                if (!x.Live) { x.Kill(); continue; }
                any = true;
                x.V.Set(((x.Vol * vol) >> 15) / 32767f * mul, x.Pitch, (azim + x.Az) & 0xFFFF);
            }
            Playing = any;
            return any;
        }

        public void Stop()
        {
            foreach (var x in _s) x.Kill();
            _s.Clear();
            Playing = false;
        }
    }

    /// <summary>
    /// The global FX processor (Snd::GlobalFxProcessor with the FXEDIT patch of the reverb type; SFXObj_Reverb) as a
    /// send bus: <see cref="SendTap"/>s on the voices add their output x the send level into it, this source renders the
    /// reverb. The .fx patches are EA FX graphs (big-endian "FX" v11 module lists); the port reads their delay modules
    /// (type 7: ms, level) and low-pass modules (type 1: Hz) into a comb-filter reverb [?] module semantics guessed.
    /// </summary>
    public sealed class ReverbBus : MonoBehaviour
    {
        public static ReverbBus Instance { get; private set; }
        /// <summary>The send level (SFXCTL_Tunnel AEMS wet x SFXObj_Reverb mixer slot 0), 0..1.</summary>
        public static volatile float Wet;
        static readonly string[] Patches =
        {
            "City_Dense.fx", "Alley.fx", "City_Open.fx", "Garage.fx", "Garage_sml.fx", "Simple_Tunnel.fx",
            "Simple_Tunnel_Sml.fx", "Hills_Close.fx", "Hills.fx", "City_Open.fx", "City_Dense.fx", "Alley.fx",
        };
        static readonly Dictionary<int, (int[] ms, float[] fb, float lp)> Cache = new Dictionary<int, (int[], float[], float)>();

        readonly object _lock = new object();
        float[] _acc = new float[4096], _in = new float[4096];
        int _accFrames;
        float[][] _comb = Array.Empty<float[]>();
        int[] _combPos = Array.Empty<int>();
        float[] _fb = Array.Empty<float>();
        float _lpHz = 6000f, _lpL, _lpR;
        int _rate = 48000;
        public int Type { get; private set; } = -1;

        public static ReverbBus Ensure(Transform parent)
        {
            if (Instance) return Instance;
            var go = new GameObject("NFSMW Reverb");
            go.transform.SetParent(parent, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.loop = true;
            var b = go.AddComponent<ReverbBus>();
            b._rate = AudioSettings.outputSampleRate;
            src.clip = AudioClip.Create("reverb", 1, 2, b._rate, false);
            src.Play();
            Instance = b;
            b.SetType(8);
            return b;
        }

        static (int[] ms, float[] fb, float lp) Load(int type)
        {
            if (Cache.TryGetValue(type, out var r)) return r;
            var ms = new List<int>();
            var fb = new List<float>();
            float lp = 6000f;
            try
            {
                var path = Path.Combine(GameData.Root, "SOUND", "FXEDIT", Patches[Mathf.Clamp(type, 0, 11)]);
                if (File.Exists(path))
                {
                    var d = File.ReadAllBytes(path);
                    for (int i = 0x3C; i + 8 <= d.Length; i++)
                    {
                        if (d[i] == 7 && d[i + 6] == 0xCC && d[i + 7] == 0xCC)
                        {
                            int t = d[i + 2] << 8 | d[i + 3], g = d[i + 4] << 8 | d[i + 5];
                            if (t > 0) { ms.Add(Mathf.Clamp(t, 5, 400)); fb.Add(Mathf.Clamp(g / 400f, 0.05f, 0.85f)); }
                        }
                        else if (d[i] == 1 && d[i + 1] == 0 && d[i + 2] == 0xCC && d[i + 3] == 0xCC) lp = Mathf.Clamp(d[i + 4] << 8 | d[i + 5], 500, 20000);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("NFSMW reverb: " + e.Message); }
            if (ms.Count == 0) { ms.AddRange(new[] { 37, 53, 71, 97 }); fb.AddRange(new[] { 0.5f, 0.5f, 0.45f, 0.4f }); }
            r = (ms.ToArray(), fb.ToArray(), lp);
            Cache[type] = r;
            return r;
        }

        /// <summary>SFXObj_Reverb::ProcessUpdate: load the reverb type's patch (on the lead car's IsReadyForSwitch).</summary>
        public void SetType(int type)
        {
            if (type == Type) return;
            Type = type;
            var (ms, fb, lp) = Load(type);
            var comb = new float[ms.Length * 2][];
            var pos = new int[ms.Length * 2];
            var g = new float[ms.Length * 2];
            for (int i = 0; i < ms.Length; i++)
                for (int c = 0; c < 2; c++)
                {
                    int n = Mathf.Max(8, ms[i] * _rate / 1000 + c * 23 * (i + 1));
                    comb[i * 2 + c] = new float[n];
                    g[i * 2 + c] = fb[i];
                }
            lock (_lock) { _comb = comb; _combPos = pos; _fb = g; _lpHz = lp; }
        }

        /// <summary>A tap adds its block.</summary>
        public void Add(float[] data, int channels, float send)
        {
            if (send <= 0.0001f) return;
            int frames = data.Length / channels;
            lock (_lock)
            {
                if (_acc.Length < frames * 2) Array.Resize(ref _acc, frames * 2);
                _accFrames = Math.Max(_accFrames, frames);
                for (int f = 0; f < frames; f++)
                {
                    float l = data[f * channels], r = channels > 1 ? data[f * channels + 1] : l;
                    _acc[f * 2] += l * send;
                    _acc[f * 2 + 1] += r * send;
                }
            }
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            int frames = data.Length / channels;
            int n;
            lock (_lock)
            {
                if (_in.Length < _acc.Length) _in = new float[_acc.Length];
                n = Math.Min(frames, _accFrames);
                Array.Clear(_in, 0, _in.Length);
                Array.Copy(_acc, _in, n * 2);
                Array.Clear(_acc, 0, _acc.Length);
                _accFrames = 0;
                var comb = _comb;
                float a = 1f - Mathf.Exp(-2f * Mathf.PI * _lpHz / Math.Max(1, _rate));
                int pairs = comb.Length / 2;
                for (int f = 0; f < frames; f++)
                {
                    float il = _in[f * 2], ir = _in[f * 2 + 1], ol = 0, or = 0;
                    for (int k = 0; k < comb.Length; k++)
                    {
                        var buf = comb[k];
                        int p = _combPos[k];
                        float y = buf[p];
                        buf[p] = ((k & 1) == 0 ? il : ir) + y * _fb[k];
                        _combPos[k] = p + 1 >= buf.Length ? 0 : p + 1;
                        if ((k & 1) == 0) ol += y; else or += y;
                    }
                    if (pairs > 0) { ol /= pairs; or /= pairs; }
                    _lpL += (ol - _lpL) * a;
                    _lpR += (or - _lpR) * a;
                    if (channels >= 2) { data[f * channels] += _lpL; data[f * channels + 1] += _lpR; }
                    else data[f] += (_lpL + _lpR) * 0.5f;
                }
            }
        }
    }

    /// <summary>A voice's FX send into <see cref="ReverbBus"/> (after the voice's own filters).</summary>
    public sealed class SendTap : MonoBehaviour
    {
        public float Scale = 1f;
        void OnAudioFilterRead(float[] data, int channels)
        {
            var bus = ReverbBus.Instance;
            if (bus != null) bus.Add(data, channels, ReverbBus.Wet * Scale);
        }
    }

    /// <summary>
    /// SFXCTL_Tunnel (sfxctl/SFXCTL_Tunnel.cpp; PC UpdateParams @0x4C8E00, UpdateIsInTunnel @0x4C5C00,
    /// UpdateDriveBySnds @0x4BE4A0, UpdateOcclusion @0x4BE8E0, UpdateCityVerb @0x4BEA70, UpdateReflectionParams
    /// @0x4BEB40), controller 6 of every car state: TrackPathZones (tunnel 3, overpass 4/5, garage 7, dynamic 10; a zone
    /// with elevation counts only when the car is below it) set the in-tunnel inputs 0..3, the player's reverb (tunnel →
    /// Simple_Tunnel, overpass → Simple_Tunnel_Sml, else Garage; outside: the "Reverb" parameter map zone through
    /// ReverbZoneCrossMap, with its FX offset), faded out/in by g_REVERBFXMODULES and smoothed (inputs 5/6 = AEMS dry/wet);
    /// tunnel / overpass entry 0.4 s ahead and tunnel exit post drive-by wooshes; AI and cop cars test occlusion (input 4)
    /// against the player when exactly one of them is in a tunnel, every 0.5 s, by a world raycast.
    /// </summary>
    public sealed class TunnelCtl
    {
        public const int ZoneReset = 0;
        static readonly int[] ReverbZoneCrossMap = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        /// <summary>g_REVERBFXMODULES {GinsuWet, GinsuDry, AemsWet, AemsDry, FadeOut ms, FadeIn ms}: the PC table at .data 0x8F2140
        /// (read by SetCurrentReverbType @0x4B57E0 / UpdateReflectionParams @0x4BEB40). It differs from the GC decomp's
        /// Data/SND_ReverbFXParams.cpp in rows 5-7, 9 and 10 (Simple_Tunnel 0/-200/0/-200 300/120, Simple_Tunnel_Sml 0/-200/0/-200,
        /// Hills_Close / City_Open / City_Dense -900).</summary>
        static readonly int[,] Modules =
        {
            { -300, 0, -300, 0, 120, 120 }, { -300, 0, -300, 0, 120, 120 }, { -300, 0, -300, 0, 120, 120 }, { -300, 0, -300, 0, 120, 120 },
            { -300, 0, -300, 0, 120, 120 }, { 0, -200, 0, -200, 300, 120 }, { 0, -200, 0, -200, 120, 120 }, { -900, 0, -900, 0, 120, 120 },
            { -1200, 0, -1200, 0, 120, 120 }, { -900, 0, -900, 0, 120, 120 }, { -900, 0, -900, 0, 120, 120 }, { -300, 0, -300, 0, 120, 120 },
        };
        public static int PlayerZoneType;
        static List<TrackZone> _zones;
        static ParamMapLayer _reverb;
        static bool _reverbLoaded;

        readonly PlayerCar _car;
        readonly SndObj _ctl;
        readonly bool _player, _lead;
        public bool InTunnel { get; private set; }
        public int CurZone { get; private set; }
        int _future, _curReverbZone = -1, _prevReverbZone;
        float _tDriveBy, _tExit, _occTest = UnityEngine.Random.value, _reverbOffset;
        TrackZone _lastWoosh, _lastExit;
        bool _occluded, _fadingOut, _fadingIn, _ramping, _toggle;
        int _reverbType = 5, _target = 5;
        readonly InterpLine _ramp = new InterpLine();
        float _wetG, _dryG = 1, _wetA, _dryA = 1, _wetGT, _dryGT = 1, _wetAT, _dryAT = 1;
        public int GinsuWet, GinsuDry = 0x7F, AemsWet, AemsDry;
        public bool ReadyForSwitch { get; private set; }
        public int ReverbType => _reverbType;

        public TunnelCtl(PlayerCar car, SndObj ctl6, bool player, bool lead)
        {
            _car = car;
            _ctl = ctl6;
            _player = player;
            _lead = lead;
            _ramp.Initialize(0f, 0f, 1);
        }

        public static List<TrackZone> Zones
        {
            get
            {
                if (_zones != null) return _zones;
                _zones = new List<TrackZone>();
                try { _zones = TrackZone.Read(WorldBundle()); }
                catch (Exception e) { Debug.LogWarning("NFSMW audio: track zones: " + e.Message); }
                return _zones;
            }
        }

        static byte[] WorldBundle()
        {
            var w = GameBootstrap.Instance ? GameBootstrap.Instance.World : null;
            return w != null && w.Track != null ? w.Track.Bundle : GameFiles.ReadBundle(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN"));
        }

        static ParamMapLayer ReverbLayer
        {
            get
            {
                if (_reverbLoaded) return _reverb;
                _reverbLoaded = true;
                try { foreach (var l in ParamMapLayer.Read(WorldBundle())) if (l.NameHash == ParamMapLayer.Hash("Reverb")) _reverb = l; }
                catch (Exception e) { Debug.LogWarning("NFSMW audio: reverb map: " + e.Message); }
                return _reverb;
            }
        }

        /// <summary>GetTunnelType: a zone of the type containing the point, below its elevation (or without one).</summary>
        public static TrackZone Find(Vector3 p, int type)
        {
            foreach (var z in Zones)
                if (z.Type == type && z.Contains(p.x, p.z) && (z.Elevation == 0f || z.Elevation >= p.y)) return z;
            return null;
        }

        public void Update(float dt)
        {
            if (!_car) return;
            UpdateIsInTunnel();
            UpdateDriveBySnds(dt);
            if (_player) { UpdateCityVerb(); UpdateReflectionParams(dt); }
            if (!_player) UpdateOcclusion(dt);
            UpdateMixerOutputs();
        }

        void UpdateIsInTunnel()
        {
            var p = _car.transform.position;
            TrackZone z = null;
            int[] order = { TrackZone.Tunnel, TrackZone.Overpass, TrackZone.OverpassSmall, TrackZone.Garage, TrackZone.Dynamic };
            CurZone = ZoneReset;
            foreach (int t in order)
            {
                z = Find(p, t);
                if (z != null && t == TrackZone.Dynamic && z.VisitInfo == 1) z = null;
                if (z != null) { CurZone = t; break; }
            }
            bool inside = z != null;
            if (inside && !InTunnel)
            {
                InTunnel = true;
                if (_player)
                    SetCurrentReverbType(CurZone == TrackZone.Overpass || CurZone == TrackZone.OverpassSmall ? 6 : CurZone == TrackZone.Tunnel ? 5 : 3, 0);
            }
            if (!inside && InTunnel)
            {
                InTunnel = false;
                if (_player) EndTunnelVerb();
            }
            if (_lead) PlayerZoneType = CurZone;
        }

        void UpdateDriveBySnds(float dt)
        {
            _tDriveBy = Mathf.Max(0f, _tDriveBy - dt);
            _tExit = Mathf.Max(0f, _tExit - dt);
            var p = _car.transform.position;
            var fwd = Vector3.ProjectOnPlane(_car.transform.forward, Vector3.up).normalized;
            var future = p + fwd * (_car.ForwardSpeed * 0.4f);
            TrackZone z;
            _future = TrackZone.Tunnel;
            z = Find(future, TrackZone.Tunnel);
            if (z == null) { _future = TrackZone.Overpass; z = Find(future, TrackZone.Overpass); }
            if (z == null) { _future = TrackZone.OverpassSmall; z = Find(future, TrackZone.OverpassSmall); }
            if (z == null) _future = ZoneReset;
            float speed = _car.SpeedMps, wooshVol = WorldAudio.WooshVolVsVel(speed);
            if (!InTunnel)
            {
                if (_future != ZoneReset && ((_tDriveBy < 0.01f && _lastWoosh == z) || _lastWoosh != z) && wooshVol > 0.01f)
                {
                    _tDriveBy = 3f;
                    _lastWoosh = z;
                    WorldAudio.Instance?.RequestDriveBy(WorldAudio.DriveByTunnelIn, speed, new Vector3(future.x, p.y + 10f, future.z), z);
                }
                return;
            }
            if (_future == ZoneReset && CurZone == TrackZone.Tunnel && ((_tExit < 0.01f && _lastExit == z) || _lastExit != z) && wooshVol > 0.01f)
            {
                _tExit = 3f;
                _lastExit = z;
                WorldAudio.Instance?.RequestDriveBy(WorldAudio.DriveByTunnelOut, speed, new Vector3(future.x, p.y + 10f, future.z), null);
            }
        }

        void UpdateOcclusion(float dt)
        {
            _occTest -= dt;
            if (_occTest > 0f) return;
            _occTest = 0.5f;
            _occluded = false;
            if (PlayerZoneType == CurZone || (PlayerZoneType != TrackZone.Tunnel && CurZone != TrackZone.Tunnel)) return;
            var player = SndMixer.PlayerRef;
            if (!player) return;
            Vector3 from = player.transform.position + Vector3.up * 2f, to = _car.transform.position;
            float dist = Mathf.Min(100f, Vector3.Distance(from, to));
            var dir = (to - from).normalized;
            foreach (var hit in Physics.RaycastAll(from, dir, dist))
            {
                if (hit.collider.GetComponentInParent<PlayerCar>() != null) continue;
                if (hit.distance * hit.distance < dist * dist - 9f) { _occluded = true; return; }
            }
        }

        void UpdateMixerOutputs()
        {
            if (_ctl == null) return;
            for (int i = 0; i < 4; i++) _ctl.Set(i, 0);
            _ctl.Set(4, _occluded ? 0x7FFF : 0);
            if (_player) { _ctl.Set(5, AemsDry); _ctl.Set(6, AemsWet); }
            _ctl.In[15] = 1;
            if (!InTunnel) return;
            if (CurZone == TrackZone.Garage) _ctl.Set(2, 0x7FFF);
            else if (CurZone == TrackZone.Overpass || CurZone == TrackZone.OverpassSmall) _ctl.Set(1, 0x7FFF);
            else _ctl.Set(0, 0x7FFF);
            _ctl.Set(3, 0x7FFF);
        }

        void UpdateCityVerb()
        {
            int offset = 0;
            _prevReverbZone = _curReverbZone;
            var layer = ReverbLayer;
            if (layer != null)
            {
                var p = _car.transform.position;
                _curReverbZone = layer.GetInt(p.x, p.z, 0);
                offset = layer.GetInt(p.x, p.z, 2);
            }
            if (_curReverbZone < 0 || _curReverbZone >= 12) _curReverbZone = SoundState.Mode == SndGameMode.FrontEnd ? 0 : 9;
            int verb = ReverbZoneCrossMap[_curReverbZone];
            if (_curReverbZone != _prevReverbZone && !InTunnel) SetCurrentReverbType(verb, offset);
            else AdjustReverbOffset(offset);
        }

        void EndTunnelVerb() => SetCurrentReverbType(ReverbZoneCrossMap[Mathf.Clamp(_curReverbZone, 0, 11)], 0);

        static float Db(int hundredths) => MixShape.FloatFromDb(hundredths);

        void AdjustReverbOffset(int offset)
        {
            if (_fadingOut || _fadingIn) return;
            _reverbOffset = offset;
            _wetGT = Db(Mathf.Clamp(Modules[_reverbType, 0] + offset, -10000, 0));
            _wetAT = Db(Mathf.Clamp(Modules[_reverbType, 2] + offset, -10000, 0));
        }

        void SetCurrentReverbType(int type, int offset)
        {
            _fadingOut = true;
            _reverbOffset = offset;
            _target = type;
            _ramp.Initialize(0f, 1f, Modules[_reverbType, 4]);
            _wetGT = 0; _dryGT = 1; _wetAT = 0; _dryAT = 1;
        }

        void UpdateReflectionParams(float dt)
        {
            ReadyForSwitch = false;
            _ramp.Update(dt);
            if (_fadingOut)
            {
                if (_ramp.Finished && !_ramping)
                {
                    if (_lead) ReadyForSwitch = true;
                    _fadingIn = true;
                    _reverbType = _target;
                    _fadingOut = false;
                    _ramp.Initialize(0f, 1f, Modules[_target, 5]);
                    _wetG = _wetGT; _dryG = _dryGT; _wetA = _wetAT; _dryA = _dryAT;
                    _wetGT = Db(Mathf.Clamp(Modules[_reverbType, 0] + (int)_reverbOffset, -10000, 0));
                    _wetAT = Db(Mathf.Clamp(Modules[_reverbType, 2] + (int)_reverbOffset, -10000, 0));
                    _dryGT = Db(Modules[_reverbType, 1]);
                    _dryAT = Db(Modules[_reverbType, 3]);
                }
            }
            else if (_fadingIn && _ramp.Finished)
            {
                _fadingIn = false;
                _wetG = _wetGT; _dryG = _dryGT; _wetA = _wetAT; _dryA = _dryAT;
            }
            if (_fadingOut || _fadingIn)
            {
                float r = _ramp.Value;
                GinsuWet = (int)(((_wetGT - _wetG) * r + _wetG) * 32767f);
                GinsuDry = (int)(((_dryGT - _dryG) * r + _dryG) * 32767f);
                AemsWet = (int)(((_wetAT - _wetA) * r + _wetA) * 32767f);
                AemsDry = (int)(((_dryAT - _dryA) * r + _dryA) * 32767f);
            }
            else
            {
                _wetG = SndCurves.Smooth(_wetG, _wetGT, 0.25f);
                _dryG = SndCurves.Smooth(_dryG, _dryGT, 0.25f);
                _wetA = SndCurves.Smooth(_wetA, _wetAT, 0.25f);
                _dryA = SndCurves.Smooth(_dryA, _dryAT, 0.25f);
                GinsuWet = (int)(_wetG * 32767f); GinsuDry = (int)(_dryG * 32767f);
                AemsWet = (int)(_wetA * 32767f); AemsDry = (int)(_dryA * 32767f);
            }
            if (_toggle) { AemsWet = Mathf.Clamp(AemsWet + 1, 0, 0x7FFF); GinsuWet = Mathf.Clamp(GinsuWet + 1, 0, 0x7FFF); _toggle = false; }
            else _toggle = true;
            _ramping = !_ramp.Finished;
        }
    }

    /// <summary>
    /// The world's sound states: TRAFFIC (CSTATEMGR_TrafficCar: 6 states within 65 m of the camera; CARSFX_TrafficEngine,
    /// CARSFX_TrafficWoosh, CARSFX_TrafficSkids — the horn stays in Traffic.cs), HELICOPTER (SFXCTL_Helicopter +
    /// SFXObj_Helicopter), DRIVEBY (CSTATEMGR_DriveBy: 6 SFXObj_Woosh states fed by smackables near the player, tunnel
    /// entries/exits and AI drive-bys of cops and trucks), ENVIRONMENT (CSTATEMGR_Enviro: 5 states, the four fountains
    /// within 70 m) and the player's SFXObj_Reverb. Created with the mixer.
    /// </summary>
    public sealed class WorldAudio : MonoBehaviour, ISndUnit
    {
        public static WorldAudio Instance { get; private set; }
        public const int DriveByUnknown = 0, DriveByTree = 1, DriveByLamppost = 2, DriveBySmokable = 3, DriveByTunnelIn = 4, DriveByTunnelOut = 5,
            DriveByOverpassIn = 6, DriveByOverpassOut = 7, DriveByAiCar = 8, DriveByTraffic = 9, DriveByBridge = 10, DriveByPreCol = 11, DriveByCameraBy = 12;

        /// <summary>g_WooshVol_vs_Vel = Slope(0, 1, 18, 45) m/s (SFXObj_Woosh.cpp:14).</summary>
        public static float WooshVolVsVel(float v) => new Slope(0f, 1f, 18f, 45f).Value(v);

        SndMixer _mixer;

        public static WorldAudio Ensure(SndMixer mixer)
        {
            if (Instance) return Instance;
            var go = new GameObject("NFSMW World Audio");
            go.transform.SetParent(mixer.transform, false);
            var w = go.AddComponent<WorldAudio>();
            w._mixer = mixer;
            ReverbBus.Ensure(go.transform);
            mixer.Register(w);
            w.InitStates();
            return w;
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        static Vector3 Vel(Component c)
        {
            var rb = c ? c.GetComponent<Rigidbody>() : null;
            return rb ? rb.linearVelocity : Vector3.zero;
        }

        // ================================================================== drive-bys (STATEMGR_DriveBy, SFXObj_Woosh)
        sealed class Woosh
        {
            public int Inst, Type;
            public bool Attached;
            public float Closing, Intensity;
            public Vector3 Pos;
            public object Unique;
            public SndObj Obj, Ctl;
            public SndPos P;
            public StitchPlayer Stitch;
        }
        readonly List<Woosh> _wooshes = new List<Woosh>();
        static int _lastRandom;

        /// <summary>GetWooshBlockSizeParams (SFXObj_Woosh.cpp:16): stitch base (STICH_WHOOSH_TYPE), blocks, block size.</summary>
        public static (int b, int n, int s) WooshBlock(int type) => type switch
        {
            DriveByTree => (57, 3, 6),
            DriveByLamppost => (9, 3, 6),
            DriveByCameraBy => (21, 1, 6),
            DriveBySmokable => (33, 2, 6),
            DriveByTunnelIn => (75, 3, 3),
            DriveByTunnelOut or DriveByOverpassOut => (84, 2, 3),
            DriveByAiCar or DriveByTraffic => (45, 2, 6),
            DriveByPreCol => (27, 2, 3),
            _ => (0, 3, 3),
        };

        /// <summary>GEN_RND_OFFSET (EAXSoundEnums.hpp:45): base + block(intensity) x size + a rotating index.</summary>
        public static int GenRndOffset(float intensity, int b, int n, int s)
        {
            int r = _lastRandom % s;
            _lastRandom = r + 1;
            return b + (int)(intensity / 128f * n) * s + r;
        }

        /// <summary>CSTATEMGR_DriveBy::GetFreeState + CSTATE_DriveBy::Attach.</summary>
        public void RequestDriveBy(int type, float closing, Vector3 pos, object unique)
        {
            if (SoundState.Paused || _wooshes.Count == 0) return;
            foreach (var w in _wooshes)
                if (w.Attached && (Vector3.Distance(w.Pos, pos) < 0.5f || (unique != null && unique == w.Unique && w.Type == type))) return;
            Woosh free = null;
            foreach (var w in _wooshes) if (!w.Attached) { free = w; break; }
            if (free == null) foreach (var w in _wooshes) if (w.Type <= type) { Detach(free = w); break; }
            if (free == null) return;
            free.Type = type;
            free.Closing = closing;
            free.Pos = pos;
            free.Unique = unique;
            free.Attached = true;
            // SFXObj_Woosh::InitSFX (PC @0x4DD6F0)
            float intensity = Mathf.Clamp(Mathf.Clamp(WooshVolVsVel(closing), 0f, 0.99f) * 127f, 0f, 127f);
            if (SoundState.Mode == SndGameMode.PursuitBreaker) intensity = 60f;
            free.Intensity = intensity;
            var (b, n, s) = WooshBlock(type);
            if (type != DriveByTree && type != DriveByBridge && type != DriveByPreCol && type != DriveByUnknown) free.Obj.Set(13, 0x7FFF);
            int id = GenRndOffset(intensity, b, n, s);
            free.P.Update(pos, Vector3.zero, 0f);
            free.Stitch = new StitchPlayer(transform, 1, StitchPlayer.Get(1, id), 0, 0);
            free.Obj.Set(11, 0x7FFF);
            free.Obj.Set(12, 0);   // bClamp(intensity << 8, 0, 0x7FFF) >> 15 is always 0 (PC @0x4DD93C too)
        }

        void Detach(Woosh w)
        {
            w.Stitch?.Stop();
            w.Stitch = null;
            w.Attached = false;
            w.Unique = null;
        }

        static int WooshSlot(int type) => type switch
        {
            DriveByAiCar => 3, DriveByTunnelIn => 4, DriveByTunnelOut => 5, DriveByOverpassIn => 6, DriveByOverpassOut => 7,
            DriveBySmokable => 8, DriveByBridge => 9, DriveByLamppost => 2, DriveByCameraBy => 10, _ => 1,
        };

        float _smackTimer;

        /// <summary>TestAllSmackablesForWhoosh / TestSmackableForWoosh (STATEMGR_DriveBy.cpp:47-150).</summary>
        void TestSmackables()
        {
            var p = SndMixer.PlayerRef;
            var cam = SndMixer.Cam;
            if (!p || !cam || p.SpeedMps < 10f) return;
            var db = GameData.Attributes;
            if (db == null) return;
            var pv = Vel(p);
            foreach (var col in Physics.OverlapSphere(p.transform.position, 20f))
            {
                var sm = col.GetComponentInParent<Smackable>();
                if (!sm || !sm.isActiveAndEnabled || string.IsNullOrEmpty(sm.Kind)) continue;
                var attrs = db.Get("smackable", sm.Kind);
                if (attrs == null || !attrs.Bool("IsWooshable")) continue;
                var t = sm.transform;
                int type;
                if (t.up.y > 0.8f)
                {
                    if (new Vector3(pv.x, 0, pv.z).magnitude < 3f) continue;
                    var a = t.position; a.y = 0;
                    var b = p.transform.position; b.y = 0;
                    if ((a - b).sqrMagnitude > 144f) continue;
                    type = attrs.TryGet("WooshType", out int at, out _, out _) ? attrs.Source.Data[at] : DriveBySmokable;
                }
                else
                {
                    var cp = cam.position + cam.forward * 2f;
                    if ((t.position - cp).sqrMagnitude > 16f) continue;
                    type = DriveByCameraBy;
                }
                RequestDriveBy(type, p.SpeedMps, t.position, sm);
            }
        }

        // ---------------------------------------------------------------- TRIGGER_EVENT_SOUND (0x10007) drive-bys
        static TrackStream _trigTrack;
        static VisibleSections _trigVis;
        static bool _trigFailed;
        static readonly Dictionary<int, List<EventTrigger>> TrigCache = new Dictionary<int, List<EventTrigger>>();
        static readonly HashSet<int> TrigLoading = new HashSet<int>();

        /// <summary>
        /// CSTATEMGR_DriveBy::UpdateParams (STATEMGR_DriveBy.cpp:143, PC @0x4FA330): at 5 m/s or more, the player's position
        /// 0.15 s ahead is tested against the event triggers of the drivable section under it (emTriggerEventsInSection:
        /// inside a trigger's sphere, 3D). Each TRIGGER_EVENT_SOUND trigger asks for a woosh at its position with the player's
        /// speed as the closing velocity; GetFreeState drops repeats within 0.5 m of a playing one. Type: the PC compares the
        /// trigger's first dword (its NameHash, a per-instance name in this data) with LAMPPOST / PILLAR / TRAFFIC_LIGHT /
        /// FREEWAY_SIGN / TREE and defaults to DRIVE_BY_LAMPPOST (@0x4FA40F), so every MW trigger is a lamppost woosh; the
        /// type hashes sit in the Parameter field, which the exe does not read. Sections' trigger packs (chunk 0x80036000)
        /// are read on a worker thread the first time the car reaches them [ours].
        /// </summary>
        void TestTriggers()
        {
            var p = SndMixer.PlayerRef;
            if (!p || p.SpeedMps < 5f || _trigFailed) return;
            if (_trigVis == null)
            {
                try
                {
                    _trigTrack = new TrackStream(GameData.Root);
                    _trigVis = VisibleSections.Read(_trigTrack.Bundle);
                }
                catch (Exception e) { _trigFailed = true; Debug.LogWarning("NFSMW audio: event triggers: " + e.Message); return; }
            }
            var fut = p.transform.position + Vel(p) * 0.15f;
            int sec = _trigVis.SectionAt(fut.x, fut.z);
            if (sec < 0) return;
            List<EventTrigger> list;
            lock (TrigCache)
            {
                if (!TrigCache.TryGetValue(sec, out list))
                {
                    if (TrigLoading.Add(sec))
                    {
                        var track = _trigTrack;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            var l = new List<EventTrigger>();
                            try
                            {
                                var ss = track.Sections.Find(x => x.Number == sec);
                                if (ss != null) { var d = track.ReadRaw(ss); l = EventTrigger.ReadAll(d, 0, d.Length); }
                            }
                            catch (Exception) { }
                            lock (TrigCache) { TrigCache[sec] = l; TrigLoading.Remove(sec); }
                        });
                    }
                    return;
                }
            }
            var g = Coords.ToGame(fut);
            foreach (var t in list)
            {
                if (t.EventId != EventTrigger.SoundEvent) continue;
                float dx = g.x - t.X, dy = g.y - t.Y, dz = g.z - t.Z;
                if (dx * dx + dy * dy + dz * dz >= t.Radius * t.Radius) continue;
                int type = t.NameHash == EventTrigger.HashTree ? DriveByTree : DriveByLamppost;
                RequestDriveBy(type, p.SpeedMps, Coords.ToUnity(t.X, t.Y, t.Z), null);
            }
        }

        /// <summary>EAXAITunerCar::UpdatAIDriveBy (PC @0x4C3980) for cops and trucks: within 12 m of the player at a
        /// relative speed of at least 15 m/s.</summary>
        void TestAiDriveBys()
        {
            var p = SndMixer.PlayerRef;
            if (!p) return;
            var pv = Vel(p);
            foreach (var e in EngineAudio.All)
                if (e && e.IsCop && e.Car) AiDriveBy(e.Car, p, pv);
            foreach (var t in _traffic) if (t.Car && t.Tractor) AiDriveBy(t.Car, p, pv);
        }

        void AiDriveBy(PlayerCar car, PlayerCar player, Vector3 pv)
        {
            if (Vector3.Distance(car.transform.position, player.transform.position) > 12f) return;
            float rel = (Vel(car) - pv).magnitude;
            if (rel < 15f) return;
            RequestDriveBy(DriveByAiCar, rel, car.transform.position, car);
        }

        void UpdateWooshes()
        {
            foreach (var w in _wooshes)
            {
                w.Obj.Set(11, 0);
                w.Obj.Set(13, 0);
                w.Obj.In[15] = 1;
                if (!w.Attached) continue;
                w.P.Update(w.Pos, Vector3.zero, Time.deltaTime);
                int vol = w.Obj.Mapped ? w.Obj.Out(WooshSlot(w.Type), MixMap.Vol) : 0x5000;
                if (w.Stitch == null || !w.Stitch.Update(vol, w.Obj.Out(0, MixMap.Azim), SoundState.Paused ? 0f : 1f)) Detach(w);
            }
        }

        // ================================================================== traffic (CSTATEMGR_TrafficCar, CARSFX_Traffic*)
        sealed class TrafficSnd
        {
            public int Inst;
            public PlayerCar Car;
            public TrafficCar Ai;
            public bool Tractor;
            public int EngType, WooshType;
            public SndObj Eng, Woosh, Skid, Ctl;
            public SndPos P;
            /// <summary>The slot's AEMS voices (CARSFX_TrafficEngine FX_TRAFFIC, CARSFX_TrafficSkids FX_SKID).</summary>
            public AemsHost Aems;
            public CsisObj Engine, Skids;
            public StitchPlayer WooshStitch;
            public float LastWoosh = -10f;
            public readonly float[] SlipX = new float[4], SlipY = new float[4], Load = new float[4], Lat = new float[4];
            public bool Ground;
            // SFXObj_TruckFX on a TRUCK state (tractors)
            public TruckSnd Truck;
        }

        /// <summary>A TRUCK mix state (CSTATEMGR_Truck::EnterWorld: 2 states, SFXIDs 0x184001) used for its SFXObj_TruckFX (obj 19,
        /// 3D position = SFXCTL_3DCarPos ctl 7).</summary>
        sealed class TruckSnd
        {
            public int Inst;
            public SndObj Fx, Ctl;
            public SndPos P;
            public PlayerCar Car;
            public CsisObj Puff;
            public float Speed;
            public bool Stopped = true;
        }
        readonly List<TruckSnd> _trucks = new List<TruckSnd>();
        readonly List<TrafficSnd> _traffic = new List<TrafficSnd>();
        TrafficSystem _trafficSys;
        float _scanTimer;
        const string TrafficBank = "IG_GLOBAL/TRAFFIC_MB.abk", TrafficSkidBank = "SKIDS/SKID_SML_MB.abk";

        static int PvByte(string name, string field, int fb)
        {
            var c = GameData.Attributes?.Get("pvehicle", (name ?? "").ToLowerInvariant());
            return c != null && c.TryGet(field, out int at, out _, out _) ? c.Source.Data[at] : fb;
        }

        void UpdateTraffic(float dt)
        {
            var cam = SndMixer.Cam;
            if (!_trafficSys) _trafficSys = FindAnyObjectByType<TrafficSystem>();
            // detach: gone, inactive or out of the 65 m connect radius
            foreach (var t in _traffic)
            {
                if (t.Car == null) continue;
                bool keep = t.Car && t.Car.isActiveAndEnabled && cam && Vector3.Distance(t.Car.transform.position, cam.position) < 65f;
                if (!keep) DetachTraffic(t);
            }
            if (_trafficSys && cam)
                foreach (var car in _trafficSys.ActiveCars)
                {
                    if (!car || Vector3.Distance(car.transform.position, cam.position) >= 65f) continue;
                    bool have = false;
                    foreach (var t in _traffic) if (t.Car == car) { have = true; break; }
                    if (have) continue;
                    TrafficSnd free = null;
                    foreach (var t in _traffic) if (t.Car == null) { free = t; break; }
                    if (free == null) break;
                    AttachTraffic(free, car);
                }
            foreach (var t in _traffic) if (t.Car) TickTraffic(t, dt);
        }

        void AttachTraffic(TrafficSnd t, PlayerCar car)
        {
            t.Car = car;
            t.Ai = car.GetComponent<TrafficCar>();
            string name = t.Ai ? t.Ai.VehicleName : car.name;
            t.Tractor = t.Ai && t.Ai.Tractor;
            t.EngType = Mathf.Clamp(PvByte(name, "TrafficEngType", 0), 0, 15);
            t.WooshType = PvByte(name, "WooshType", DriveByTraffic);
            if (t.WooshType == 0) t.WooshType = DriveByTraffic;
            var holder = car.transform;
            // CARSFX_TrafficEngine::InitEngine: new FX_TRAFFIC(TrafficEngType, 0, 0, 0, 0, 25000, 0, 0x7FFF, 0). The TRAFFIC_MB patch
            // picks the loop and its pitch tables by the id (its sample group {1, 0, 2}: type 1 plays nothing, ids past the
            // group, e.g. 8 = garbage truck / fire engine, take the last entry with their own pitch-table entries)
            if (!t.Aems) t.Aems = AemsHost.Create(transform, "AEMS Traffic " + t.Inst);
            t.Engine = new CsisObj(t.Aems, "FX_TRAFFIC", new[] { t.EngType, 0, 0, 0, 0, 25000, 0, 0x7FFF, 0 }, TrafficBank);
            // CARSFX_TrafficSkids (CARSFX_Skids::InitSFX): FX_SKID in SKID_SML_MB
            var sk = new int[22];
            sk[18] = 25000; sk[20] = 0x7FFF;
            t.Skids = new CsisObj(t.Aems, "FX_SKID", sk, TrafficSkidBank);
            // the traffic car's own Effects behaviour (impacts / scrapes) [ours: attached with its sound state]
            CollisionEffects.ForCar(car, name);
            t.Eng.Attach(true); t.Woosh.Attach(true); t.Skid.Attach(true);
            if (t.Tractor) AttachTruck(t);
        }

        void DetachTraffic(TrafficSnd t)
        {
            t.Engine?.Release();
            t.Skids?.Release();
            t.Skids = null;
            DetachTruck(t);
            t.WooshStitch?.Stop();
            t.WooshStitch = null;
            t.Engine = null;
            t.Car = null;
            t.Ai = null;
            for (int i = 0; i < 16; i++) { t.Eng.In[i] = 0; t.Woosh.In[i] = 0; t.Skid.In[i] = 0; t.Ctl.In[i] = 0; }
            t.Eng.Attach(false); t.Woosh.Attach(false); t.Skid.Attach(false);
        }

        void TickTraffic(TrafficSnd t, float dt)
        {
            var car = t.Car;
            t.P.Update(car.transform.position, Vel(car), dt);
            // CARSFX_TrafficWoosh::UpdateParams: past the player at ≥10 m/s relative, within 10 m, 3 s apart
            t.Woosh.Set(3, 0);
            var p = SndMixer.PlayerRef;
            if (p && t.WooshStitch == null && dt > 0)
            {
                var a = Vel(p); var b = Vel(car);
                float rel = new Vector2(a.x - b.x, a.z - b.z).magnitude;
                bool inRadius = Vector3.Distance(p.transform.position, car.transform.position) < 10f;
                if (inRadius && rel > 10f && Time.time > t.LastWoosh + 3f)
                {
                    float scale = Mathf.Clamp01((rel - 10f) / 50f);
                    float intensity = Mathf.Clamp(Mathf.Clamp(scale, 0f, 0.99f) * 127f, 0f, 127f);
                    var (bb, n, s) = WooshBlock(t.WooshType);
                    t.WooshStitch = new StitchPlayer(car.transform, 1, StitchPlayer.Get(1, GenRndOffset(intensity, bb, n, s)), 0, 0);
                    t.LastWoosh = Time.time;
                    t.Woosh.Set(3, 0x7FFF);
                }
            }
            // SFXCTL_Wheel::UpdateTireParams on the traffic car's wheels (MW order 0 FL, 1 FR, 2 RR, 3 RL)
            t.Ground = false;
            for (int i = 0; i < 4; i++)
            {
                var w = car.WheelAudio(i);
                int k = w.front ? (w.left ? 0 : 1) : (w.left ? 3 : 2);
                // SoundCar::OnService's slip (PlayerCar.SoundWheelSlip: 0.2 x tolerated-slip band, skid = -GetWheelSkid)
                var ss = car.SoundWheelSlip(i);
                float lat = -ss.skid;
                t.SlipX[k] = w.contact ? Mathf.Clamp(ss.slip * 200f, -1023f, 1023f) : 0f;
                t.SlipY[k] = Mathf.Clamp(lat * 81f, -1023f, 1023f);
                t.Lat[k] = lat;
                t.Load[k] = new Slope(0f, 1023f, 4000f, 10000f).Value(w.load);
                if (w.contact) t.Ground = true;
            }
            // CARSFX_Skids::UpdateMixerOutputs from the patch's front/back FB and LR
            float fwd = 0, side = 0;
            if (t.Ground)
            {
                fwd = Mathf.Max(Mathf.Abs((int)((t.SlipX[1] + t.SlipX[0]) * 0.5f)), Mathf.Abs((int)((t.SlipX[3] + t.SlipX[2]) * 0.5f)));
                side = Mathf.Max(Mathf.Abs((int)((t.SlipY[1] + t.SlipY[0]) * 0.5f)), Mathf.Abs((int)((t.SlipY[3] + t.SlipY[2]) * 0.5f)));
            }
            int f5 = Mathf.Clamp((int)fwd << 5, 0, 0x7FFF), s5 = Mathf.Clamp((int)side << 5, 0, 0x7FFF);
            t.Skid.Set(0, f5);
            t.Skid.Set(2, f5);
            t.Skid.Set(1, SndCurves.Smooth(t.Skid.Get(1), s5, 500));
            t.Skid.Set(3, SndCurves.Smooth(t.Skid.Get(3), Mathf.Max(f5, s5), 3000));
            t.Ctl.In[15] = 1;
            if (t.Truck != null) TickTruck(t.Truck, dt);
        }

        void ApplyTraffic()
        {
            float paused = SoundState.Paused ? 0f : 1f;
            foreach (var t in _traffic)
            {
                if (t.Aems) t.Aems.Gain = paused;
                if (!t.Car || t.Engine == null) continue;
                // CARSFX_TrafficEngine::ProcessUpdate (CARSFX_TrafficFX.cpp:100-128): volume slot 1, azimuth 0, range = mph x
                // 7.3142858 (0..1024), pitch offset = slot 4 - 0x1000
                var e = t.Engine;
                e[1] = t.Eng.Out(1, MixMap.Vol);
                e[3] = t.Eng.Out(0, MixMap.Azim);
                e[4] = (int)Mathf.Clamp(t.Car.SpeedMps * 2.23699f * 7.3142858f, 0f, 1024f);
                e[2] = t.Eng.Out(4, MixMap.Pitch) - 0x1000;
                if (t.WooshStitch != null && !t.WooshStitch.Update(t.Woosh.Out(3, MixMap.Vol), t.Woosh.Out(0, MixMap.Azim), paused)) t.WooshStitch = null;
                // CARSFX_Skids::ProcessUpdate (as the player's, Sfx.cs ApplySkids)
                var c = t.Skids;
                if (c != null)
                {
                    c[0] = t.Skid.Out(5, MixMap.Vol);
                    c[1] = t.Skid.Out(7, MixMap.Vol);
                    c[2] = t.Skid.Out(6, MixMap.Vol);
                    c[21] = t.Skid.Out(9, MixMap.Vol);
                    c[5] = t.Skid.Out(8, MixMap.Pitch);
                    c[3] = t.Skid.Out(1, MixMap.Azim);
                    c[7] = (int)(t.Car.SpeedMps * 2.23699f);
                    c[12] = t.Ground ? (int)((t.SlipX[1] + t.SlipX[0]) * 0.5f) : 0;
                    c[13] = t.Ground ? (int)((t.SlipY[1] + t.SlipY[0]) * 0.5f) : 0;
                    c[14] = t.Ground ? (int)((t.Load[1] + t.Load[0]) * 0.5f) : 0;
                    c[15] = t.Ground ? (int)((t.SlipX[3] + t.SlipX[2]) * 0.5f) : 0;
                    c[16] = t.Ground ? (int)((t.SlipY[3] + t.SlipY[2]) * 0.5f) : 0;
                    c[17] = t.Ground ? (int)((t.Load[3] + t.Load[2]) * 0.5f) : 0;
                }
                if (t.Truck != null) ApplyTruck(t.Truck);
            }
        }

        // ---------------------------------------------------------------- trucks (CSTATEMGR_Truck, SFXObj_TruckFX)
        void AttachTruck(TrafficSnd t)
        {
            TruckSnd free = null;
            foreach (var k in _trucks) if (k.Car == null) { free = k; break; }
            if (free == null)
            {
                if (_trucks.Count >= 2) return;
                int inst = _mixer.Acquire(MixMap.Truck);
                if (inst < 0) return;
                var map = _mixer.Map;
                free = new TruckSnd { Inst = inst, Fx = new SndObj(map, MixMap.ObjectId(false, MixMap.Truck, inst, 19)), Ctl = new SndObj(map, MixMap.ObjectId(true, MixMap.Truck, inst, 7)) };
                free.P = new SndPos(free.Ctl);
                _trucks.Add(free);
            }
            free.Car = t.Car;
            free.Speed = 0f;
            free.Stopped = true;
            free.Fx.Attach(true);
            t.Truck = free;
        }

        void DetachTruck(TrafficSnd t)
        {
            var k = t.Truck;
            if (k == null) return;
            k.Puff?.Release();
            k.Puff = null;
            k.Car = null;
            for (int i = 0; i < 16; i++) { k.Fx.In[i] = 0; k.Ctl.In[i] = 0; }
            k.Fx.Attach(false);
            t.Truck = null;
        }

        /// <summary>SFXObj_TruckFX::UpdateParams (SFXObj_TruckFX.cpp:25-62): while the tractor shifts an air puff (FX_TRUCK_FX id
        /// 2..5 at random), when it brakes to a stop from moving a brake hiss (id 0..1); a new one only after the last ended.
        /// [ours] the tractor keeps its TRAFFIC state for the engine and woosh; only the truck FX use the TRUCK state.</summary>
        void TickTruck(TruckSnd k, float dt)
        {
            var car = k.Car;
            if (!car) return;
            k.P.Update(car.transform.position, Vel(car), dt);
            k.Ctl.In[15] = 1;
            var sim = car.GetComponent<NfsMw.Game.TrafficPhysics.TrafficVehicle>();
            float mph = car.SpeedMps * 2.23699f;
            if (k.Puff != null && !k.Puff.Alive) { k.Puff.Release(); k.Puff = null; }
            if (k.Puff == null && sim != null)
            {
                int az = k.Fx.Out(0, MixMap.Azim);
                if (sim.Engine != null && sim.Engine.IsShifting)
                    k.Puff = new CsisObj(_trucksAems, "FX_TRUCK_FX", new[] { UnityEngine.Random.Range(0, 4) + 2, 0, 0, az, 25000, 0, 0x7FFF, 0 }, TrafficBank);
                else if ((sim.Brake > 0.5f || sim.EBrake > 0f) && mph < 1f && k.Speed >= 1f && !k.Stopped)
                {
                    k.Puff = new CsisObj(_trucksAems, "FX_TRUCK_FX", new[] { UnityEngine.Random.Range(0, 2), 0, 0, az, 25000, 0, 0x7FFF, 0 }, TrafficBank);
                    k.Stopped = true;
                }
                if (k.Puff != null && !k.Puff.Alive) { k.Puff.Release(); k.Puff = null; }
            }
            if (k.Stopped && mph > k.Speed) k.Stopped = false;
            k.Speed = mph;
        }

        /// <summary>SFXObj_TruckFX::ProcessUpdate: volume slot 1, azimuth 0, pitch offset slot 2 - 0x1000.</summary>
        void ApplyTruck(TruckSnd k)
        {
            if (k.Puff == null) return;
            k.Puff[1] = k.Fx.Out(1, MixMap.Vol);
            k.Puff[3] = k.Fx.Out(0, MixMap.Azim);
            k.Puff[2] = k.Fx.Out(2, MixMap.Pitch) - 0x1000;
        }

        AemsHost _trucksAemsHost;
        AemsHost _trucksAems
        {
            get
            {
                if (!_trucksAemsHost) _trucksAemsHost = AemsHost.Create(transform, "AEMS Trucks");
                return _trucksAemsHost;
            }
        }

        // ================================================================== helicopter (SFXCTL_Helicopter, SFXObj_Helicopter)
        SndObj _heliObj, _heliCtl;
        SndPos _heliPos;
        CopHeli _heli;
        AemsHost _heliAems;
        CsisObj _heliCsis;
        float _heliSpeed, _heliDist;
        int _heliRot;
        const string HeliBank = "IG_GLOBAL/FX_Helicopter_MB.abk";

        void UpdateHeli(float dt)
        {
            if (_heliObj == null) return;
            if (_heli == null || !_heli.isActiveAndEnabled || _heli.Finished)
            {
                _heli = null;
                if (_scanTimer <= 0f) foreach (var h in FindObjectsByType<CopHeli>(FindObjectsSortMode.None)) if (h && !h.Finished) { _heli = h; break; }
                _heliObj.Attach(_heli != null);
            }
            if (_heli == null) { _heliCtl.In[15] = 0; return; }
            var pos = _heli.transform.position;
            _heliPos.Update(pos, _heli.Velocity, dt);
            var p = SndMixer.PlayerRef;
            if (p)
            {
                // SFXCTL_Helicopter::UpdateParams (PC @0x4BFA30)
                _heliDist = Vector3.Distance(pos, p.transform.position);
                float s = Vector3.Dot(_heli.Velocity, _heli.transform.forward) / 500f;
                s = s > 1f ? 1f : s < 0.25f ? 0.5f : s;
                _heliSpeed = s * 1023f;
                var p2h = (p.transform.position - pos).normalized;
                float dot = Mathf.Clamp(Vector3.Dot(p2h, _heli.transform.forward), -1f, 1f);
                _heliRot = (int)(Mathf.Acos(dot) / Mathf.PI * 32768f) >> 6;
            }
            _heliCtl.In[15] = 1;
        }

        /// <summary>SFXObj_Helicopter (CARSFX/SFXObj_Helicopter.cpp): InitSFX (the state attaching) makes Csis::FX_Helicopter
        /// (id 0, ..., low-pass 16000); ProcessUpdate while the heli simulates: azimuth slot 0, distance (≤ 100, negative when
        /// the azimuth is behind: azim - 0x4001 &lt; 0x7FFF unsigned), volume and FX dry slot 1, speed, pitch offset slot 2,
        /// rotation; otherwise volume and dry 0. Detach deletes it. The FX_Helicopter patch (IG_GLOBAL/FX_Helicopter_MB) runs
        /// the turbine loop and the blade slaps.</summary>
        void ApplyHeli()
        {
            if (_heliObj == null) return;
            if (!_heliAems) _heliAems = AemsHost.Create(transform, "AEMS Helicopter");
            _heliAems.Gain = SoundState.Paused ? 0f : 1f;
            if (_heli == null)
            {
                if (_heliCsis != null) { _heliCsis.Release(); _heliCsis = null; }
                return;
            }
            if (_heliCsis == null)
                _heliCsis = new CsisObj(_heliAems, "FX_Helicopter", new[] { 0, 0, 0, 0, 0, 0, 0, 16000, 0, 0, 0, 0 }, HeliBank);
            int nazim = _heliObj.Out(0, MixMap.Azim);
            int dist = Mathf.Min(100, (int)_heliDist);
            if ((uint)(nazim - 0x4001) < 0x7FFFu) dist = -dist;
            var c = _heliCsis;
            c[4] = nazim;
            c[6] = dist;
            c[3] = _heliObj.Out(1, MixMap.Vol);
            c[9] = _heliObj.Out(1, MixMap.Vol);
            c[5] = (int)_heliSpeed;
            c[1] = _heliObj.Out(2, MixMap.Pitch);
            c[11] = _heliRot;
        }

        // ================================================================== world objects (CSTATEMGR_Enviro, SFXObj_WorldObject)
        /// <summary>RegisterWorldObjects (STATEMGR_Enviro.cpp:16): four fountains, game (x, y, z) = Unity (x, z, y).</summary>
        static readonly Vector3[] Fountains = { new Vector3(4734f, 17f, 933f), new Vector3(3415f, 76f, 1024f), new Vector3(3550f, 92f, 1359f), new Vector3(3582f, 85f, 1079f) };
        sealed class EnvSnd { public SndObj Obj, Ctl; public SndPos P; public SndVoice V; public int Fountain = -1; }
        readonly List<EnvSnd> _env = new List<EnvSnd>();

        void UpdateEnv(float dt)
        {
            var p = SndMixer.PlayerRef;
            for (int f = 0; f < Fountains.Length; f++)
            {
                bool inRadius = p && Vector3.Distance(p.transform.position, Fountains[f]) < 70f;
                EnvSnd att = null;
                foreach (var e in _env) if (e.Fountain == f) att = e;
                if (att != null && !inRadius) { att.Fountain = -1; att.V?.Stop(); att.Obj.Attach(false); att.Ctl.In[15] = 0; }
                else if (att == null && inRadius)
                    foreach (var e in _env)
                        if (e.Fountain < 0)
                        {
                            e.Fountain = f;
                            e.Obj.Attach(true);
                            if (e.V == null) e.V = new SndVoice(transform, "Fountain", true);
                            // ENV_STATIC(WORLDOBJECT_FOUNTAIN = 4, ENV_COMMON): ENV_COMMON_MB's select table
                            // {0,1,0,0,2,3,4,0,0,0,0,5} → slot 2
                            var es = SoundLibrary.Patch("IG_GLOBAL/ENV_COMMON_MB.abk").Select("ENV_STATIC");
                            e.V.Loop(SoundLibrary.ClipAt("IG_GLOBAL/ENV_COMMON_MB.abk", es != null && es.Length > 4 ? es[4] : 2));
                            break;
                        }
            }
            foreach (var e in _env)
            {
                if (e.Fountain < 0) continue;
                e.P.Update(Fountains[e.Fountain], Vector3.zero, dt);
                e.Ctl.In[15] = 1;
                float lp = e.Obj.Out(3, MixMap.Freq);
                e.V.Set((e.Obj.Mapped ? e.Obj.Vol(1) : 0f) * (SoundState.Paused ? 0f : 1f), e.Obj.Pitch(2), e.Obj.Out(0, MixMap.Azim), lp > 0 ? lp : 22000f);
            }
        }

        // ================================================================== reverb object (SFXObj_Reverb, PLAYERCAR obj 16)
        void UpdateReverb()
        {
            var p = SndMixer.PlayerRef;
            var e = p ? p.GetComponent<EngineAudio>() : null;
            var tun = e != null ? e.Tunnel : null;
            if (tun == null || e.ReverbObj == null) { ReverbBus.Wet = 0f; return; }
            bool fe = SoundState.Mode == SndGameMode.FrontEnd;
            // UpdateParams: wet x mixer slot 0; ProcessUpdate: switch the patch on the lead car's IsReadyForSwitch
            float slot = e.ReverbObj.Mapped ? e.ReverbObj.Vol(0) : 1f;
            ReverbBus.Wet = fe ? 0f : tun.AemsWet / 32767f * slot;
            if (!fe && tun.ReadyForSwitch) ReverbBus.Instance?.SetType(tun.ReverbType);
        }

        // ================================================================== states / frame
        void InitStates()
        {
            var map = _mixer.Map;
            for (int i = 0; i < 6; i++)
            {
                int inst = _mixer.Acquire(MixMap.DriveBy);
                if (inst < 0) break;
                var w = new Woosh { Inst = inst, Obj = new SndObj(map, MixMap.ObjectId(false, MixMap.DriveBy, inst, 0)), Ctl = new SndObj(map, MixMap.ObjectId(true, MixMap.DriveBy, inst, 0)) };
                w.P = new SndPos(w.Ctl);
                w.Obj.Attach(true);
                _wooshes.Add(w);
            }
            for (int i = 0; i < 6; i++)
            {
                int inst = _mixer.Acquire(MixMap.Traffic);
                if (inst < 0) break;
                var t = new TrafficSnd
                {
                    Inst = inst,
                    Eng = new SndObj(map, MixMap.ObjectId(false, MixMap.Traffic, inst, 0)),
                    Woosh = new SndObj(map, MixMap.ObjectId(false, MixMap.Traffic, inst, 1)),
                    Skid = new SndObj(map, MixMap.ObjectId(false, MixMap.Traffic, inst, 3)),
                    Ctl = new SndObj(map, MixMap.ObjectId(true, MixMap.Traffic, inst, 0)),
                };
                t.P = new SndPos(t.Ctl);
                _traffic.Add(t);
            }
            int hi = _mixer.Acquire(MixMap.Helicopter);
            if (hi >= 0)
            {
                _heliObj = new SndObj(map, MixMap.ObjectId(false, MixMap.Helicopter, hi, 0));
                _heliCtl = new SndObj(map, MixMap.ObjectId(true, MixMap.Helicopter, hi, 1));
                _heliPos = new SndPos(_heliCtl);
            }
            for (int i = 0; i < 5; i++)
            {
                int inst = _mixer.Acquire(MixMap.Environment);
                if (inst < 0) break;
                var e = new EnvSnd { Obj = new SndObj(map, MixMap.ObjectId(false, MixMap.Environment, inst, 0)), Ctl = new SndObj(map, MixMap.ObjectId(true, MixMap.Environment, inst, 0)) };
                e.P = new SndPos(e.Ctl);
                _env.Add(e);
            }
        }

        public void Tick(float dt)
        {
            _scanTimer -= Time.unscaledDeltaTime;
            bool inGame = SoundState.Mode != SndGameMode.FrontEnd;
            if (inGame && dt > 0)
            {
                // CSTATEMGR_DriveBy::UpdateParams checks every frame; the port tests smackables at 4 Hz (WOOSHCHECK_FRAME_FREQ 4) [ours]
                _smackTimer -= dt;
                if (_smackTimer <= 0f) { _smackTimer = 0.067f; TestSmackables(); }
                TestTriggers();
                TestAiDriveBys();
            }
            EngineAudio.UpdateConnections();
            UpdateTraffic(dt);
            UpdateHeli(dt);
            UpdateEnv(dt);
            UpdateReverb();
            if (_scanTimer <= 0f) _scanTimer = 0.5f;
        }

        public void Apply(float dt)
        {
            UpdateWooshes();
            ApplyTraffic();
            ApplyHeli();
        }
    }
}
