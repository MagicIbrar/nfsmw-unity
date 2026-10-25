using System.Collections.Generic;
using System.IO;
using NfsMw.Audio;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>Sounds from the game's EA banks (SOUND\**\*.abk) as AudioClips, cached per bank and index.</summary>
    public static class SoundLibrary
    {
        static readonly Dictionary<string, SoundBank> Banks = new Dictionary<string, SoundBank>();
        static readonly Dictionary<(string, int), AudioClip> Clips = new Dictionary<(string, int), AudioClip>();
        static readonly Dictionary<(string, int), AudioClip> Slots = new Dictionary<(string, int), AudioClip>();

        public static SoundBank Bank(string rel)
        {
            if (Banks.TryGetValue(rel, out var b)) return b;
            GameData.Mount();
            var path = Path.Combine(GameData.Root, "SOUND", rel);
            try { b = File.Exists(path) ? SoundBank.Load(path) : new SoundBank(); }
            catch (System.Exception e) { Debug.LogWarning($"NFSMW sound: {rel}: {e.Message}"); b = new SoundBank(); }
            Banks[rel] = b;
            return b;
        }

        /// <summary>The n-th sound present in the bank (empty slots skipped).</summary>
        public static AudioClip Clip(string rel, int n)
        {
            if (Clips.TryGetValue((rel, n), out var c)) return c;
            var bank = Bank(rel);
            if (bank.Sounds.Count == 0) return null;
            var s = bank.Sounds[Mathf.Clamp(n, 0, bank.Sounds.Count - 1)];
            c = Make(rel, s);
            Clips[(rel, n)] = c;
            return c;
        }

        /// <summary>
        /// The sound in bank slot <paramref name="slot"/> (the BNKl table index; slot 0 is empty in every MW bank, and an
        /// AEMS sample id n plays slot n+1 [?]). Null if the slot is empty.
        /// </summary>
        public static AudioClip ClipAt(string rel, int slot)
        {
            if (Slots.TryGetValue((rel, slot), out var c)) return c;
            var bank = Bank(rel);
            BankSound s = null;
            foreach (var x in bank.Sounds) if (x.Index == slot) { s = x; break; }
            c = s != null ? Make(rel, s) : null;
            Slots[(rel, slot)] = c;
            return c;
        }

        static AudioClip Make(string rel, BankSound s)
        {
            string name = $"{Path.GetFileNameWithoutExtension(rel)}_{s.Index}";
            if (s.DataR != null && s.DataR.Length == s.Data.Length)
            {
                // a 2-channel bank sound (PT 0x89): interleave
                var st = new float[s.Data.Length * 2];
                for (int i = 0; i < s.Data.Length; i++) { st[i * 2] = s.Data[i]; st[i * 2 + 1] = s.DataR[i]; }
                var c2 = AudioClip.Create(name, Mathf.Max(1, s.Data.Length), 2, Mathf.Max(1000, s.SampleRate), false);
                c2.SetData(st, 0);
                return c2;
            }
            var c = AudioClip.Create(name, Mathf.Max(1, s.Data.Length), 1, Mathf.Max(1000, s.SampleRate), false);
            c.SetData(s.Data, 0);
            return c;
        }

        static readonly Dictionary<string, AemsPatch> Patches = new Dictionary<string, AemsPatch>();

        /// <summary>The bank's AEMS patch tables (<see cref="AemsPatch"/>): sample-select and key-map tables per Csis class.</summary>
        public static AemsPatch Patch(string rel)
        {
            if (Patches.TryGetValue(rel, out var p)) return p;
            GameData.Mount();
            p = AemsPatch.Load(Path.Combine(GameData.Root ?? "", "SOUND", rel));
            Patches[rel] = p;
            return p;
        }

        public static int Count(string rel) => Bank(rel).Sounds.Count;

        /// <summary>Highest occupied slot of a bank.</summary>
        public static int LastSlot(string rel)
        {
            int m = 0;
            foreach (var s in Bank(rel).Sounds) m = Mathf.Max(m, s.Index);
            return m;
        }

        static List<Stitch>[] _stitches;

        /// <summary>The stitch tables (GLOBAL\InGameB, chunk 0x8003B500): [type][index].</summary>
        public static List<Stitch>[] Stitches
        {
            get
            {
                if (_stitches != null) return _stitches;
                GameData.Mount();
                _stitches = new[] { new List<Stitch>(), new List<Stitch>(), new List<Stitch>() };
                foreach (var f in new[] { "InGameB.lzc", "InGameB.bun" })
                {
                    var path = Path.Combine(GameData.Root, "GLOBAL", f);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var s = StitchBundle.Read(GameFiles.ReadBundle(path));
                        if (s[0].Count > 0) { _stitches = s; break; }
                    }
                    catch (System.Exception e) { Debug.LogWarning("NFSMW stitches: " + e.Message); }
                }
                return _stitches;
            }
        }
    }

    /// <summary>
    /// A car's non-engine sounds, the CARSFX objects of its mix state (EAXSound/CARSFX): gear shifts and
    /// sweeteners (CARSFX_Shift), turbo spool and blow-off (CARSFX_Turbo), nitrous (CARSFX_Nitrous), tyre skids
    /// (CARSFX_Skids), road noise per side and surface (CARSFX_RoadNoise), wind (CARSFX_WindNoise) and impacts
    /// (SFXObj_Collision with the stitch tables). Volumes, pitches and pans come from the mix map. The banks' AEMS
    /// patches run in the AEMS interpreter (<see cref="AemsHost"/>): the Csis classes FX_SHIFTING_01 (gear clunk, brake
    /// mash), CAR_SWTN (shift sweeteners), CAR_WHINE (gear whine), CAR_Sputter (spark chatter, its CAR_SputOutput blips
    /// feed back into the mixer), FX_TURBO_01 (spool, blow-off), FX_NITROUS / FX_PURGE, FX_SKID and FX_WIND get the
    /// parameters the CARSFX objects set; road noise still plays the select-table loops.
    /// </summary>
    public sealed class CarSfx : MonoBehaviour, ISndUnit
    {
        public PlayerCar Car;
        public bool Player;
        EngineAudio _engine;
        CarSoundModel M => _engine ? _engine.Model : null;
        SndObj _shiftObj, _turboObj, _nosObj, _skidObj, _roadObj, _windObj;
        Transform _holder;
        SndVoice _transL, _transR;
        readonly SndVoice[] _road = new SndVoice[2];
        /// <summary>CARSFX_RoadNoise::m_pWetRoad[2]: FX_ROADNOISE(FXROADNOISE_LOOP_WETROAD 4, ...) per side, created in InitSFX
        /// (PC @0x4DAB10: the 0x30-byte Csis instance {4, 0 x 6, 25000, 0, 0x7FFF, 0}) and kept for the car's life.</summary>
        readonly SndVoice[] _wetRoad = new SndVoice[2];
        AemsHost _fx;
        CsisObj _shiftGear, _brakePedal, _disengage, _engage, _accelSnd, _decelSnd, _whine, _sputter, _spool, _blowoff, _nos, _purge, _skidCsis, _wind;
        SndObj _sparkObj;
        int _blipVol;
        readonly InterpLine _blowRampDown = new InterpLine();
        bool _hasTurbo, _hasNos, _hasWind, _hasShift;
        string _shiftBank, _sweetBank, _turboBank, _skidBank, _nosBank = "NOS/Nitrous_00_MB.abk";
        const string RoadBank = "IG_GLOBAL/ROADNOISE_00_MB.abk", WindBank = "IG_GLOBAL/WIND_00_MB.abk", CollisionBank = "IG_GLOBAL/Stich_Collision_MB.abk";
        // turbo
        float _spoolCharge, _spoolPercent, _tSpoolDuck;
        int _turboState, _blowoffId, _blowoffVol, _volSpool;
        bool _reachedPeak;
        // nitrous
        int _nosState;
        bool _nosEmptyFlag, _nosWasEmpty;
        // road noise
        readonly int[] _loopId = { -2, -2 };
        readonly uint[] _prevSurface = new uint[2];
        int _lVol, _rVol, _lPitch, _rPitch;
        static readonly Dictionary<uint, (int loop, int skid, int on, int off)> SurfaceAudio = new Dictionary<uint, (int, int, int, int)>();
        // skids
        readonly float[] _slipX = new float[4], _slipY = new float[4], _load = new float[4], _latSkid = new float[4];
        bool _leftGround, _rightGround;
        int _skidType;

        public static CarSfx Attach(PlayerCar car, bool player)
        {
            var s = car.gameObject.AddComponent<CarSfx>();
            s.Car = car;
            s.Player = player;
            s.Setup();
            return s;
        }

        void Setup()
        {
            _engine = GetComponent<EngineAudio>();
            var mixer = SndMixer.Ensure();
            _holder = new GameObject("Sfx").transform;
            _holder.SetParent(transform, false);
            if (_engine == null || _engine.Model == null) return;
            var a = _engine.Model.A;
            int st = SndMixer.StateOr(_engine.MixState, _engine.MixInstance), inst = Mathf.Max(0, _engine.MixInstance);
            SndObj O(int o)
            {
                var x = new SndObj(mixer.Map, MixMap.ObjectId(false, st, inst, o));
                if (_engine.MixInstance >= 0) x.Attach(true);
                return x;
            }
            _shiftObj = O(3); _turboObj = O(4); _nosObj = O(5); _skidObj = O(7); _roadObj = O(8); _windObj = O(9);
            // the physics Effects behaviour's audio (impacts / scrapes through the world effects)
            CollisionEffects.ForCar(Car, _engine.CarName);
            if (Player) { _bottomObj = O(13); _preColObj = O(18); _rainObj = O(11); _windWeatherObj = O(12); }
            _shiftBank = string.IsNullOrEmpty(a.ShiftBank) ? "SHIFTING/GEAR_MED_Base.abk" : "SHIFTING/" + a.ShiftBank;
            _sweetBank = string.IsNullOrEmpty(a.SweetBank) ? null : "ENGINE/" + a.SweetBank;
            _turboBank = a.HasTurbo && !string.IsNullOrEmpty(a.TurboBank) ? "TURBO/" + a.TurboBank : null;
            bool drift = false;   // FX_SKID SkidType 1 = drift race (TheRaceParameters.IsDriftRace): MW has none
            _skidType = drift ? 1 : 0;
            _skidBank = drift ? "SKIDS/SKIDS_DRIFT_BIG_MB.abk" : "SKIDS/SKID_BIG_MB.abk";
            _volSpool = a.VolSpool;
            _fx = AemsHost.Create(_holder, "AEMS Fx");
            // CARSFX_Skids::InitSFX: new FX_SKID(0, 0, 0, 0, SkidType, 0 x 13, 25000, 0, 0x7FFF, 0)
            var sk = new int[22];
            sk[4] = _skidType; sk[18] = 25000; sk[20] = 0x7FFF;
            _skidCsis = new CsisObj(_fx, "FX_SKID", sk, _skidBank);
            if (!_engine.IsCop)
            {
                _hasShift = true;
                // CARSFX_Turbo::InitSFX → PlaySpl(0, 0, 0, 0, 0): FX_TURBO_01(0, 0 x slot 1, 0, azimuth, 0, rpm)
                if (_turboBank != null)
                {
                    _hasTurbo = true;
                    _spool = new CsisObj(_fx, "FX_TURBO_01", new[] { 0, 0, 0, _turboObj.Out(0, MixMap.Azim), 0, (int)_engine.Model.PhysRpm }, _turboBank);
                }
                if (Player)
                {
                    _hasNos = _hasWind = true;
                    for (int i = 0; i < 2; i++) _road[i] = new SndVoice(_holder, i == 0 ? "RoadL" : "RoadR");
                    for (int i = 0; i < 2; i++)
                    {
                        _wetRoad[i] = new SndVoice(_holder, i == 0 ? "WetRoadL" : "WetRoadR");
                        _wetRoad[i].Loop(SoundLibrary.ClipAt(RoadBank, RoadLoopSlot(4)));
                        _wetRoad[i].Set(0f, 1f, 0);
                    }
                    _transL = new SndVoice(_holder, "RoadTransL");
                    _transR = new SndVoice(_holder, "RoadTransR");
                    // CARSFX_WindNoise::InitSFX: new FX_WIND(0x1000, 0 x 8, 25000, 0, 0x7FFF, 0, 0xF)
                    _wind = new CsisObj(_fx, "FX_WIND", new[] { 0x1000, 0, 0, 0, 0, 0, 0, 0, 0, 25000, 0, 0x7FFF, 0, 0xF }, WindBank);
                    // CARSFX_SparkChatter (PLAYERCAR obj 6): new CAR_Sputter(CarID, this, 0 x 9) in the sweetener bank; the patch's
                    // CAR_SputOutput instances report the blip volume back (SparkChatCreate/UpdateCallBack)
                    if (_sweetBank != null)
                    {
                        _sparkObj = O(6);
                        _fx.Mixer.OnControlClass = SputOutput;
                        _sputter = new CsisObj(_fx, "CAR_Sputter", new[] { a.CarId, GetInstanceID(), 0, 0, 0, 0, 0, 0, 0, 0, 0 }, _sweetBank);
                    }
                }
            }
            mixer.Register(this);
        }

        void OnDestroy()
        {
            var m = SndMixer.Instance;
            if (!m) return;
            m.Unregister(this);
        }

        // ------------------------------------------------------------------ tick (DMIX inputs)
        public void Tick(float dt)
        {
            var m = M;
            if (!Car || m == null || Car.Spec == null) return;
            ReadWheels();
            // CARSFX_Shift::UpdateMixerOutputs
            _shiftObj.Set(7, m.ShiftState == 1 ? 0x7FFF : 0);
            // CARSFX_Skids::UpdateMixerOutputs
            float ffb = (_slipX[0] + _slipX[1]) * 0.5f, bfb = (_slipX[2] + _slipX[3]) * 0.5f;
            float flr = (_slipY[0] + _slipY[1]) * 0.5f, blr = (_slipY[2] + _slipY[3]) * 0.5f;
            if (!_leftGround && !_rightGround) ffb = bfb = flr = blr = 0;
            int fwd = Mathf.Clamp((int)Mathf.Max(Mathf.Abs(ffb), Mathf.Abs(bfb)) << 5, 0, 0x7FFF);
            _skidObj.Set(0, fwd);
            _skidObj.Set(2, fwd);
            int side = Mathf.Clamp((int)Mathf.Max(Mathf.Abs(flr), Mathf.Abs(blr)) << 5, 0, 0x7FFF);
            _skidObj.Set(1, SndCurves.Smooth(_skidObj.Get(1), side, 500));
            _skidObj.Set(3, SndCurves.Smooth(_skidObj.Get(3), Mathf.Max(fwd, side), 3000));
            if (dt > 0)
            {
                UpdateTurbo(m, dt);
                UpdateNitrous(m);
                if (Player) { GenerateRoadNoise(); UpdateBottomOut(dt); UpdatePreCol(dt); }
            }
            if (Player) { UpdateRain(dt); UpdateWindWeather(); }
        }

        /// <summary>SFXCTL_Wheel::UpdateTireParams (sfxctl/SFXCTL_Wheel.cpp): slip x 200 / skid x 81 → ±1023, load
        /// 4000..10000 N → 0..1023; a side touches the ground while either of its tyres does. GenerateTerrainTypes: a side's
        /// terrain changes only when its front and rear tyre agree, a blown tyre makes it "blown_tire".</summary>
        void ReadWheels()
        {
            if (!_tires) Car.TryGetComponent(out _tires);
            _leftGround = _rightGround = false;
            for (int i = 0; i < 4; i++)
            {
                var w = Car.WheelAudio(i);
                // MW wheel order: 0 FL, 1 FR, 2 RR, 3 RL
                int k = w.front ? (w.left ? 0 : 1) : (w.left ? 3 : 2);
                // SFXCTL_Wheel reads the slip SoundCar::OnService sends (SoundCar.cpp:162-172): PlayerCar.SoundWheelSlip
                // (the longitudinal slip moved toward 0 by 0.2 x ToleratedSlip; skid = -GetWheelSkid, the lateral speed)
                var ss = Car.SoundWheelSlip(i);
                float lat = -ss.skid;
                _slipX[k] = w.contact ? Mathf.Clamp(ss.slip * 200f, -1023f, 1023f) : 0f;
                _slipY[k] = Mathf.Clamp(lat * 81f, -1023f, 1023f);
                _latSkid[k] = lat;
                _load[k] = new Slope(0f, 1023f, 4000f, 10000f).Value(w.load);
                if (w.contact) { if (w.left) _leftGround = true; else _rightGround = true; }
                _prevTire[k] = _tire[k];
                _tire[k] = w.surface;
                bool blown = (Car.BlownTires & (1 << i)) != 0;
                _justBlown[k] = blown && !_wasBlown[k];
                _wasBlown[k] = blown;
                // EAX_CarState::DidTireJustPucture (EAXCarState.hpp:294): mBlownState 1 (TIRE_DAMAGE_PUNCTURED, DamageRacer::Puncture)
                bool punct = _tires ? _tires.State(i) == NfsMw.Game.Police.TireState.Tire.Punctured : false;
                _justPunct[k] = punct && !_wasPunct[k];
                _wasPunct[k] = punct;
            }
            _prevSurface[0] = _surfL;
            _prevSurface[1] = _surfR;
            if (_wasBlown[1] || _wasBlown[2]) _surfR = BlownTire;
            else if (_tire[1] == _tire[2]) _surfR = _tire[1];
            if (_wasBlown[0] || _wasBlown[3]) _surfL = BlownTire;
            else if (_tire[0] == _tire[3]) _surfL = _tire[0];
        }
        uint _surfL, _surfR;
        readonly uint[] _tire = new uint[4], _prevTire = new uint[4];
        readonly bool[] _wasBlown = new bool[4], _justBlown = new bool[4], _wasPunct = new bool[4], _justPunct = new bool[4];
        NfsMw.Game.Police.TireState _tires;
        static readonly uint BlownTire = AttribDatabase.Hash("blown_tire");

        static (int loop, int skid, int on, int off) Surface(uint key)
        {
            if (SurfaceAudio.TryGetValue(key, out var v)) return v;
            v = (5, 0, -1, -1);
            var db = GameData.Attributes;
            if (db != null)
                foreach (var c in db.OfClass("simsurface"))
                    if (c.Key == key)
                    {
                        v = (c.Has("Aud_Roadnoise_LOOP") ? c.Int("Aud_Roadnoise_LOOP") : 5, c.Has("Aud_Skid_Type") ? c.Int("Aud_Skid_Type") : 0,
                             c.Has("Aud_RoadNoise_TransON") ? c.Int("Aud_RoadNoise_TransON") : -1, c.Has("Aud_RoadNoise_TransOFF") ? c.Int("Aud_RoadNoise_TransOFF") : -1);
                        break;
                    }
            SurfaceAudio[key] = v;
            return v;
        }

        // ------------------------------------------------------------------ CARSFX_Turbo
        /// <summary>CARSFX_Turbo::UpdateParams / UpdateBlowOff / UpdateSpool / PlayBlowoff (CARSFX_Turbo.cpp:118-299).</summary>
        void UpdateTurbo(CarSoundModel m, float t)
        {
            if (!_hasTurbo) return;   // cop cars build no turbo
            var a = m.A;
            float turbo = m.EngTrq > 20f ? m.EngTrq * 0.01f : 0f;
            // UpdateBlowOff: the ramp starts down (150 ms) when the throttle comes back outside a shift
            if (_blowoff != null)
            {
                _blowRampDown.Update(t);
                if (m.Throttle > 20f && _blowRampDown.Value == 1f && m.ShiftState == 0) _blowRampDown.Initialize(1f, 0f, 150);
                if (BlowOffDone) StopBlowOff();
            }
            // UpdateSpool (per audio frame: scale the charge by 60 Hz frames)
            float frames = t * 60f;
            if (!SoundState.Paused)
            {
                if (turbo > 0.01f) _spoolCharge += turbo * frames;
                else _spoolCharge -= a.LeakRate * frames;
            }
            float rpmScale = Mathf.Clamp01((m.PhysRpm - 1000f) * 0.01f) * 0.6f + 0.4f;
            _spoolCharge = Mathf.Clamp(_spoolCharge, 0f, a.ChargeTime * rpmScale);
            if (Mathf.Approximately(_spoolCharge, a.ChargeTime * rpmScale) && !_reachedPeak) { _reachedPeak = true; _tSpoolDuck = 0; }
            else if (!Mathf.Approximately(_spoolCharge, a.ChargeTime) && _reachedPeak) _reachedPeak = false;
            _spoolPercent = _spoolCharge / a.ChargeTime;
            _volSpool = a.VolSpool;
            if (_reachedPeak)
            {
                _tSpoolDuck += t * 1000f;
                // m_SpoolDuck: hold T_TURBO_SUSTAIN 1000 ms, then cos-table index 0 → 307.2 over T_TURBO_DUCK_POWER_DROP 2000 ms
                int idx = _tSpoolDuck < 1000f ? 0 : (int)Mathf.Clamp((_tSpoolDuck - 1000f) / 2000f * 307.2f, 0f, 307.2f);
                _volSpool = (_volSpool * MixShape.CosTable[idx]) >> 15;
            }
            switch (_turboState)
            {
                case 0: if (m.EngTrq > 20f) _turboState = 1; break;
                case 1:
                    if (m.EngTrq < 20f)
                    {
                        _turboState = 2;
                        PlayBlowoff(m);
                        _spoolCharge = 0;
                    }
                    break;
                case 2: if (BlowOffDone) _turboState = 0; break;
            }
        }

        bool BlowOffDone => _blowoff == null || !_blowoff.Alive || _blowRampDown.Value == 0f;

        void StopBlowOff() { _blowoff?.Release(); _blowoff = null; }

        /// <summary>PlayBlowoff: id 1, or 2/3 at random above 75 % spool; FX_TURBO_01(id, 0, spool x 1024, 0, rotation, rpm).</summary>
        void PlayBlowoff(CarSoundModel m)
        {
            if (_blowoff != null) return;
            _blowoffId = _spoolPercent > 0.75f ? Random.Range(2, 4) : 1;
            _blowoffVol = (int)((_blowoffId == 1 ? m.A.VolBlowoff1 : m.A.VolBlowoff2) * _spoolPercent);
            _blowoff = new CsisObj(_fx, "FX_TURBO_01", new[] { _blowoffId, 0, (int)(_spoolPercent * 1024f), 0, 0, (int)m.PhysRpm }, _turboBank);
            _blowRampDown.Initialize(1f, 1f, 1);
        }

        // ------------------------------------------------------------------ CARSFX_Nitrous
        /// <summary>CARSFX_Nitrous::UpdateParams / Play / Stop / PlayPurge (CARSFX_Nitrous.cpp:50-160): FX_NITROUS(0, slot 1, azimuth,
        /// 0, 0, 25000, 0, 0x7FFF, 0) while the nitrous fires, NIT_STOP = 1 when it ends (the patch plays its stop tail);
        /// FX_PURGE(0, 0, 0, 0, 25000, 0, 0x7FFF, 0) when the bottle empties (the physics' NOS-empty flag: the port raises it
        /// once when the charge runs out [ours]).</summary>
        void UpdateNitrous(CarSoundModel m)
        {
            if (!_hasNos) return;
            bool firing = Car.NosFiring;
            if (_nosState == 0 && firing)
            {
                _nosState = 1;
                _nos?.Release();
                _nos = new CsisObj(_fx, "FX_NITROUS", new[] { 0, _nosObj.Out(1, MixMap.Vol), _nosObj.Out(0, MixMap.Azim), 0, 0, 25000, 0, 0x7FFF, 0 }, _nosBank);
                _nosObj.Set(1, NisPlayer.Active ? 0 : 0x7FFF);
            }
            else if (_nosState == 1 && !firing)
            {
                _nosState = 0;
                _nosObj.Set(1, 0);
                if (_nos != null) _nos[3] = 1;   // SetNIT_STOP(1)
            }
            bool empty = Car.Nos <= 0.001f;
            if (empty && !_nosWasEmpty) _nosEmptyFlag = true;
            _nosWasEmpty = empty;
            if (_nosEmptyFlag && _purge == null)
            {
                _nosEmptyFlag = false;
                _purge = new CsisObj(_fx, "FX_PURGE", new[] { 0, 0, 0, 0, 25000, 0, 0x7FFF, 0 }, _nosBank);
                _nosObj.Set(2, NisPlayer.Active ? 0 : 0x7FFF);
            }
        }

        // ------------------------------------------------------------------ CARSFX_RoadNoise
        static readonly Graph RoadVolGraph = MakeRoadGraph();
        static Graph MakeRoadGraph()
        {
            var g = new Graph(5);
            float[] x = { 0, 60, 100, 150, 175 }, y = { 0, 28000, 32500, 24500, 18000 };
            for (int i = 0; i < 5; i++) { g.X[i] = x[i]; g.Y[i] = y[i]; }
            return g;
        }

        void GenerateRoadNoise()
        {
            float mph = Car.SpeedMps * 2.23699f;
            float baseVol = (int)RoadVolGraph.Value(mph);
            float lSlip = new Vector2(_slipX[0] + _slipX[3], _slipY[0] + _slipY[3]).magnitude / 200f;   // back to m/s-ish
            float rSlip = new Vector2(_slipX[1] + _slipX[2], _slipY[1] + _slipY[2]).magnitude / 200f;
            float rv = baseVol + baseVol * Mathf.Min(rSlip * 0.01f, 0.15f);
            rv += rv * 0.1f;
            float lv = baseVol + baseVol * Mathf.Min(lSlip * 0.01f, 0.15f);
            _lVol = (int)Mathf.Min(lv, 32000f);
            _rVol = (int)Mathf.Min(rv, 32000f);
            float p = new Slope(1500f, 4500f, 0f, 100f).Value(mph);
            _lPitch = (int)Mathf.Min(p + p * Mathf.Min(lSlip * 0.01f, 0.2f), 6000f);
            _rPitch = (int)Mathf.Min(p + p * Mathf.Min(rSlip * 0.01f, 0.2f), 6000f);
        }

        static int LoopToVolSlot(int id) => id switch { 0 => 5, 1 => 6, 2 => 7, 3 => 8, 4 => 9, 7 => 11, 8 => 12, _ => 10 };

        // ------------------------------------------------------------------ apply (ProcessUpdate)
        public void Apply(float dt)
        {
            var m = M;
            if (!Car || m == null) return;
            float paused = (SoundState.Paused ? 0f : 1f) * (_engine ? _engine.ConnectGain : 1f);
            if (_fx) _fx.Gain = paused;   // [ours] for g_EAXIsPaused: the CARSFX objects zero their Csis volumes
            ApplyShift(m);
            ApplySkids();
            ApplyTurbo(m);
            ApplyNitrous();
            ApplySparkChatter(m);
            if (Player) { ApplyRoad(paused); ApplyWind(); }
            if (Player) ApplyBottomOutPreCol(paused);
            if (Player) ApplyWeather();
        }

        // ------------------------------------------------------------------ CARSFX_Rain (PLAYERCAR obj 11) / CARSFX_WindWeather (obj 12)
        SndObj _rainObj, _windWeatherObj;
        CsisObj _rain, _windWeather;
        bool _rainFading, _weatherStreamQueued;
        float _rainIntensity, _prevRainIntensity, _rainFadeLeft, _thunderTime, _thunderDelta, _weatherSpeedScale = 1f;
        int _blockStrmTest;
        const string RainBank = "IG_GLOBAL/FX_Rain_MB.abk";
        /// <summary>aud_moment_strm key_thunder_distant / key_thunder_close (CARSFX_Rain::QueueWeatherStream).</summary>
        const uint KeyThunderDistant = 0x016C4FA1, KeyThunderClose = 0xD2C5E045;

        /// <summary>
        /// CARSFX_Rain::UpdateParams (CARSFX/CARSFX_Rain.cpp; created for the player by EnterWorld SFXIDs 0x53BFC bit 11): the
        /// view's rain intensity (eView::Precipitation→GetRainIntensity = Rain.Intensity) above 0.01 starts
        /// FX_Weather(hood_Rain 0, substract 0, volume 0, width 0, pitch 0, rain_on_off 0, 25000, 0, 0x7FFF, 0) from
        /// IG_GLOBAL\FX_Rain_MB; at or below it the patch gets rain_on_off = 1 and is deleted 10 s later. Thunder: above 0.25,
        /// with no world data streaming, once m_fThunderTime passes 25 + bRandom(20) s a thunder_distant (intensity &lt; 0.5) or
        /// thunder_close moment stream is queued while the NIS/SFX channel is free; the block counter then lets the next one
        /// through only after it wraps past 32. UpdateMixerOutputs: input 2 = 0x7FFF while raining and not fading out.
        /// </summary>
        void UpdateRain(float t)
        {
            if (_rainObj == null) return;
            _prevRainIntensity = _rainIntensity;
            _rainIntensity = NfsMw.World.Rain.Instance != null ? NfsMw.World.Rain.Intensity : 0f;
            if (_rainIntensity > 0.01f) { if (_rain == null) PlayRain(); }
            else if (_rain != null && !_rainFading) StopRain();
            _thunderTime += t;
            bool streaming = false;   // IsWorldDataStreaming(0) [?: the port's WorldStreamer exposes no loading backlog]
            if (_rainIntensity > 0.25f && !streaming && _thunderTime > _thunderDelta)
            {
                if (_blockStrmTest == 0)
                {
                    _thunderTime = 0f;
                    _weatherStreamQueued = false;
                    QueueWeatherStream();
                    _thunderDelta = Random.Range(0, 20) + 25f;   // g_pEAXSound->Random(20) + 25
                    _blockStrmTest++;
                }
                else if (++_blockStrmTest > 32) _blockStrmTest = 0;
            }
            if (_rainFading)
            {
                _rainFadeLeft -= t;
                if (_rainFadeLeft < 0f) DestroyRain();
            }
            _rainObj.Set(2, _rain != null && !_rainFading ? 0x7FFF : 0);
        }

        void PlayRain()
        {
            _rain = new CsisObj(_fx, "FX_Weather", new[] { 0, 0, 0, 0, 0, 0, 25000, 0, 0x7FFF, 0 }, RainBank);
            _rainFading = false;
        }

        /// <summary>CARSFX_Rain::Stop: rain_on_off = 1 (the patch's own fade), the object is destroyed after 10 s.</summary>
        void StopRain()
        {
            if (_rain == null) return;
            _rainFadeLeft = 10f;
            _rainFading = true;
            _rain[5] = 1;
        }

        void DestroyRain()
        {
            _rain?.Release();
            _rain = null;
            _rainFading = false;
        }

        /// <summary>CARSFX_Rain::QueueWeatherStream: MGamePlayMoment(zero, key_thunder_*) to MomentStrm while Speech module 0 (the
        /// NIS/SFX stream) is done playing.</summary>
        void QueueWeatherStream()
        {
            if (_weatherStreamQueued || NisStreams.IsPlaying) return;
            MomentStream.Send(_rainIntensity < 0.5f ? KeyThunderDistant : KeyThunderClose, Vector3.zero);
            _weatherStreamQueued = true;
        }

        /// <summary>CARSFX_WindWeather::UpdateParams (CARSFX_WindNoise.cpp:223-262; EnterWorld bit 12): FX_WIND_Weather(volume 0,
        /// pitch 0, width 0, intensity 0) from WIND_00_MB as soon as the object runs (no weather gate: the patch and the mixer's
        /// obj 12 slot 0 decide); WeatherWindVolSlope(1 → 0.25 over 0..70 mph).</summary>
        void UpdateWindWeather()
        {
            if (_windWeatherObj == null) return;
            if (_windWeather == null) _windWeather = new CsisObj(_fx, "FX_WIND_Weather", new[] { 0, 0, 0, 0 }, WindBank);
            _weatherSpeedScale = new Slope(1f, 0.25f, 0f, 70f).Value(Car.SpeedMps * 2.23699f);
        }

        /// <summary>CARSFX_Rain::ProcessUpdate: volume = output 1 with the hood or bumper camera ("inside"), else output 0;
        /// Pitch_Offset 0, Hood_Rain by the camera, Hood_Rain_Vol_Substract 0, Width 7000. CARSFX_WindWeather::ProcessUpdate:
        /// volume = output 0 × WeatherSpeedScale, Width 4000, Intensity 0.</summary>
        void ApplyWeather()
        {
            if (_rain != null && _rainObj != null)
            {
                bool inside = SndMixer.IsHoodCameraOn || SndMixer.IsBumperCameraOn;   // CARSFX_Rain.cpp:149-151
                _rain[2] = _rainObj.Out(inside ? 1 : 0, MixMap.Vol);
                _rain[4] = 0;
                _rain[0] = inside ? 1 : 0;
                _rain[1] = 0;
                _rain[3] = 7000;
            }
            if (_windWeather != null && _windWeatherObj != null)
            {
                _windWeather[0] = (int)(_windWeatherObj.Out(0, MixMap.Vol) * _weatherSpeedScale);
                _windWeather[2] = 4000;
                _windWeather[3] = 0;
            }
        }

        /// <summary>CARSFX_Shift::UpdateParams (CARSFX_Shifting.cpp:118-370): the gear clunk (FX_SHIFTING_01 id AEMS_SHIFTING_UP 1 /
        /// DOWN 0 at Up/Down_Vol_Shift x cos(512 - scale x 512) x slot 1/2, camera = bumper view), the sweeteners (CAR_SWTN
        /// Disengage id 0 at ≥ 7000 rpm x slot 4, Engage id 1 at RPM_AtShift ≥ 7000 x slot 3, Accel id 1 x slot 5, Decel id 0 x
        /// slot 6; Vol_ShiftSweets x ShiftingAttackVolSlope(26000..32767 over 4000..7000 rpm)), the gear whine (CAR_WHINE,
        /// slot 8 x rpm / max rpm) and the brake mash (FX_SHIFTING_01 id 2 at slot 10). One-shots are dropped once their
        /// patch has ended (GetRefCount() below 2).</summary>
        void ApplyShift(CarSoundModel m)
        {
            if (!_hasShift) return;
            int az = _shiftObj.Out(0, MixMap.Azim);
            int cam = SndMixer.PovType == 0 ? 1 : 0;   // CameraView = GetPOV() == 0 (CARSFX_Shifting.cpp:310/323)
            float rpm = m.PhysRpm;
            Drop(ref _decelSnd); Drop(ref _accelSnd); Drop(ref _disengage); Drop(ref _engage);
            if (m.NeedShiftGearSnd)
            {
                int vol = m.Upshift ? m.A.UpVolShift : m.A.DownVolShift;
                float k = Mathf.Clamp01(((int)rpm - 1500f) / 6500f) * 0.9f + 0.1f;
                vol = (vol * MixShape.CosTable[(int)(512f - k * 512f)]) >> 15;
                vol = (vol * _shiftObj.Out(m.Upshift ? 1 : 2, MixMap.Vol)) >> 15;
                _shiftGear?.Release();
                _shiftGear = new CsisObj(_fx, "FX_SHIFTING_01", new[] { m.Upshift ? 1 : 0, vol, 0x1000, az, 0, cam }, _shiftBank);
            }
            if (_sweetBank != null)
            {
                if (m.NeedDisengageSnd && rpm >= 7000f) _disengage = Swtn(m, 0, Sweet(m, rpm, 4), rpm, az);
                if (m.NeedEngageSnd && m.RpmAtShift >= 7000f) _engage = Swtn(m, 1, Sweet(m, m.RpmAtShift, 3), rpm, az);
                if (m.NeedAccelSnd) _accelSnd = Swtn(m, 1, Sweet(m, m.RpmAtShift, 5), rpm, az);
                if (m.NeedDecelSnd) _decelSnd = Swtn(m, 0, Sweet(m, m.RpmAtShift, 6), rpm, az);
            }
            if (m.Whining)
            {
                // CAR_WHINE(0, 0, 0, 0, 0, 0, 25000, 0, 0x7FFF): CAR_WHINE_00.abk
                if (_whine == null) _whine = new CsisObj(_fx, "CAR_WHINE", new[] { 0, 0, 0, 0, 0, 0, 25000, 0, 0x7FFF }, WhineBank);
                float maxRpm = Car.Spec != null ? Car.Spec.RedLine : 0f;
                float wv = maxRpm > 0f ? _shiftObj.Out(8, MixMap.Vol) * (rpm / maxRpm) : 0f;
                _whine[2] = (int)wv;
                _whine[3] = az;
                _whine[1] = (int)rpm;
            }
            else if (_whine != null) { _whine.Release(); _whine = null; }
            if (m.BrakeMashed)
            {
                if (_brakePedal == null) _brakePedal = new CsisObj(_fx, "FX_SHIFTING_01", new[] { 2, _shiftObj.Out(10, MixMap.Vol), 0x1000, az, 0, cam }, _shiftBank);
            }
            else if (_brakePedal != null) { _brakePedal.Release(); _brakePedal = null; }
        }

        static void Drop(ref CsisObj o) { if (o != null && !o.Alive) { o.Release(); o = null; } }

        int Sweet(CarSoundModel m, float atRpm, int slot) => (((m.A.VolShiftSweets * (int)ShiftAttackVol.Value(atRpm)) >> 15) * _shiftObj.Out(slot, MixMap.Vol)) >> 15;

        /// <summary>new Csis::CAR_SWTN(SampleID, CarID, physics rpm, volume, azimuth, 0, 0) in the engineaudio SweetBank.</summary>
        CsisObj Swtn(CarSoundModel m, int id, int vol, float rpm, int az) => new CsisObj(_fx, "CAR_SWTN", new[] { id, m.A.CarId, (int)rpm, vol, az, 0, 0 }, _sweetBank);

        static readonly Slope ShiftAttackVol = new Slope(26000f, 32767f, 4000f, 7000f);
        const string WhineBank = "ENGINE/CAR_WHINE_00.abk";

        /// <summary>CARSFX_Skids::ProcessUpdate (CARSFX_Skids.cpp:104-150): FX_SKID volumes slots 5/7/6, wet 9, pitch 8, azimuth 1,
        /// surface = max(Aud_Skid_Type) of the sides, speed (mph), under/oversteer (Chassis::Calculate*Factor, 0..1 truncated),
        /// front/back slip and load (SFXCTL_Wheel m_NormWheelSlip / m_fLoad averages) while a side touches the ground.</summary>
        void ApplySkids()
        {
            var c = _skidCsis;
            if (c == null) return;
            c[0] = _skidObj.Out(5, MixMap.Vol);
            c[1] = _skidObj.Out(7, MixMap.Vol);
            c[2] = _skidObj.Out(6, MixMap.Vol);
            c[21] = _skidObj.Out(9, MixMap.Vol);
            c[5] = _skidObj.Out(8, MixMap.Pitch);
            c[3] = _skidObj.Out(1, MixMap.Azim);
            c[11] = Mathf.Max(Surface(_surfL).skid, Surface(_surfR).skid);
            c[7] = (int)(Car.SpeedMps * 2.23699f);
            c[9] = (int)SteerFactor(true);
            c[10] = (int)SteerFactor(false);
            bool ground = _leftGround || _rightGround;
            c[12] = ground ? (int)((_slipX[1] + _slipX[0]) * 0.5f) : 0;
            c[13] = ground ? (int)((_slipY[1] + _slipY[0]) * 0.5f) : 0;
            c[14] = ground ? (int)((_load[1] + _load[0]) * 0.5f) : 0;
            c[15] = ground ? (int)((_slipX[3] + _slipX[2]) * 0.5f) : 0;
            c[16] = ground ? (int)((_slipY[3] + _slipY[2]) * 0.5f) : 0;
            c[17] = ground ? (int)((_load[3] + _load[2]) * 0.5f) : 0;
        }

        /// <summary>Chassis::CalculateUndersteerFactor / CalculateOversteerFactor (Physics/Behaviors/Chassis.cpp:50-105): the mean
        /// lateral skid speed of the front pair (understeer, only while it opposes the steering) or the rear pair over the car
        /// speed, at most 1. The skid speeds come from the port's slip-angle reconstruction [?].</summary>
        float SteerFactor(bool under)
        {
            float speed = Car.SpeedMps;
            if (Car.ForwardSpeed <= 0f || speed <= 1f) return 0f;
            int a = under ? 0 : 3, b = under ? 1 : 2;
            float skid = (_latSkid[a] + _latSkid[b]) * 0.5f;
            if (under && Car.Input.Steer * skid >= 0f) return 0f;
            return Mathf.Min(Mathf.Abs(skid) / speed, 1f);
        }

        /// <summary>CARSFX_Turbo::ProcessUpdate: blow-off volume = BlowoffVol x slot 2 (id 1) / 3 x the ramp, spool volume =
        /// vol_Spool x slot 1, PSI = spool x 1024, rotation = torque x 10.24, rpm.</summary>
        void ApplyTurbo(CarSoundModel m)
        {
            int az = _turboObj.Out(0, MixMap.Azim);
            if (_blowoff != null)
            {
                int vol = (_blowoffVol * _turboObj.Out(_blowoffId == 1 ? 2 : 3, MixMap.Vol)) >> 15;
                _blowoff[3] = az;
                _blowoff[1] = (int)(vol * _blowRampDown.Value);
            }
            if (_spool != null)
            {
                _spool[1] = (_volSpool * _turboObj.Out(1, MixMap.Vol)) >> 15;
                _spool[2] = (int)(_spoolPercent * 1024f);
                _spool[3] = az;
                _spool[4] = (int)(m.PhysTrq * 10.24f);
                _spool[5] = (int)m.PhysRpm;
            }
        }

        /// <summary>CARSFX_Nitrous::ProcessUpdate: FX_NITROUS volume slot 1, azimuth 0, pitch slot 3, low-pass slot 4; FX_PURGE
        /// volume slot 2, pitch 0x1000; both dropped once their patch has ended.</summary>
        void ApplyNitrous()
        {
            if (!_hasNos) return;
            if (NisPlayer.Active && (_nos != null || _purge != null)) { _nosObj.Set(1, 0); _nosObj.Set(2, 0); }
            if (_nos != null)
            {
                if (!_nos.Alive) { _nos.Release(); _nos = null; }
                else
                {
                    _nos[1] = _nosObj.Out(1, MixMap.Vol);
                    _nos[2] = _nosObj.Out(0, MixMap.Azim);
                    _nos[4] = _nosObj.Out(3, MixMap.Pitch);
                    _nos[5] = _nosObj.Out(4, MixMap.Freq);
                }
            }
            if (_purge != null)
            {
                if (!_purge.Alive) { _purge.Release(); _purge = null; }
                else
                {
                    _purge[0] = _nosObj.Out(2, MixMap.Vol);
                    _purge[1] = _nosObj.Out(0, MixMap.Azim);
                    _purge[3] = 0x1000;
                }
            }
        }

        /// <summary>CARSFX_SparkChatter::ProcessUpdate / UpdateMixerOutputs (CARSFX_SparkChatter.cpp:110-154): CAR_Sputter gets the
        /// azimuth, rpm, torque x 10.24, VOL = slot 1 x engineaudio Vol_Sputters, the accelerating and shifting flags; the blip
        /// volume its CAR_SputOutput reports goes to DMIX input 0 (input 2 = 0x7FFF while it is non-zero).</summary>
        void ApplySparkChatter(CarSoundModel m)
        {
            if (_sputter == null || _sparkObj == null) return;
            var c = _sputter;
            c[4] = _sparkObj.Out(0, MixMap.Azim);
            c[5] = 0;
            c[6] = 0;
            c[8] = 0;
            c[2] = (int)m.PhysRpm;
            c[7] = (int)(m.PhysTrq * 10.24f);
            c[3] = (_sparkObj.Out(1, MixMap.Vol) * m.A.VolSputters) >> 15;
            c[9] = m.Accelerating ? 1 : 0;
            c[10] = m.ShiftState != 0 ? 1 : 0;
            SparkMixerOutputs();
        }

        void SparkMixerOutputs()
        {
            if (_sparkObj == null) return;
            if (_blipVol != 0) { _sparkObj.Set(2, 0x7FFF); _sparkObj.Set(0, _blipVol); }
            else _sparkObj.Set(2, 0);
        }

        /// <summary>SparkChatCreate/Update/DestroyCallBack: a CAR_SputOutput instance made by the CAR_Sputter patch (its
        /// ControlClass node); member data {volume, car_class, car_id}: the blip volume.</summary>
        int SputOutput(string cls, int id, int op, int[] p)
        {
            if (cls != "CAR_SputOutput") return op == 0 ? 1 : id;
            if (op == 2) { SparkMixerOutputs(); return 0; }
            if (p != null && p.Length > 0) _blipVol = p[0];
            return op == 0 ? 1 : id;
        }

        /// <summary>CARSFX_RoadNoise::ProcessUpdate (CARSFX/CARSFX_Roadnoise.cpp): per side, a terrain change swaps the
        /// FX_ROADNOISE loop (Aud_Roadnoise_LOOP; ROADNOISE_00_MB select table loop n → slot n+1) and raises input 0 —
        /// a change to the same loop ends the update for the frame, as the exe's early return does; a tyre whose terrain
        /// changed loop plays the new surface's TransON, else the old one's TransOFF (none if either is DONTPLAY = 4); a
        /// blown tyre plays BLOWN. FX_ROADNOISE_TRANS: transition n → its select table (slots 12..15), volume
        /// GetRoadNoiseTransitionVol (curb 15500, spike 26000, blown 0x7FFF) x mixer slot 2 (spike 13, blown 14), pitch
        /// Slope(3600..4400 over 12..60 mph). Loop volume = generated x the loop's mixer slot (MapLoopToVolume), 0 off the ground.</summary>
        void ApplyRoad(float paused)
        {
            _roadObj.Set(0, 0);
            for (int n = 0; n < 2; n++)
            {
                uint surf = n == 0 ? _surfL : _surfR, prevSurf = _prevSurface[n];
                bool ground = n == 0 ? _leftGround : _rightGround;
                var info = Surface(surf);
                var trans = n == 0 ? _transL : _transR;
                int az = _roadObj.Out(n, MixMap.Azim);
                if (surf != prevSurf)
                {
                    if (_loopId[n] == info.loop) return;
                    _loopId[n] = info.loop;
                    _roadObj.Set(0, 0x7FFF);
                    if (info.loop >= 0 && info.loop <= 7) _road[n].Loop(SoundLibrary.ClipAt(RoadBank, RoadLoopSlot(info.loop)));
                    else _road[n].Stop();
                }
                int a = n == 0 ? 0 : 1, b = n == 0 ? 3 : 2, transTire = -1;
                foreach (int t in new[] { a, b })
                    if (_tire[t] != _prevTire[t] && Surface(_tire[t]).loop != Surface(_prevTire[t]).loop) transTire = t;
                if (ground && transTire >= 0)
                {
                    int on = Surface(_tire[transTire]).on, off = Surface(_prevTire[transTire]).off;
                    if (on != 4 && off != 4)
                    {
                        if (on >= 0) PlayTransition(trans, on, az);
                        else if (off >= 0) PlayTransition(trans, off, az);
                    }
                }
                if (_justPunct[a] || _justPunct[b]) PlayTransition(trans, 2, az);   // FXROADNOISE_TRANSITION_SPIKESTRIP
                if (_justBlown[a] || _justBlown[b]) PlayTransition(trans, 3, az);   // FXROADNOISE_TRANSITION_BLOWN
                int gen = n == 0 ? _lVol : _rVol, pitch = n == 0 ? _lPitch : _rPitch;
                int v = (gen * _roadObj.Out(LoopToVolSlot(_loopId[n]), MixMap.Vol)) >> 15;
                if (!ground) v = 0;
                // ENISRoadNoise → Tweak_DisableRoadNoise (0x9B09B0): CarRenderConn::UpdateRoadNoise (@0x745C70) skips the
                // road-noise service packet while it is set, so the sound side gets no surface/speed: loops silent
                if (NisEventRunner.DisableRoadNoise != 0) v = 0;
                _road[n].Set(v / 32767f * paused, pitch / 4096f, az);
                // m_pWetRoad (CARSFX_Roadnoise.cpp:272-279): generated x RoadNoiseVolumes[4] (24000, EAXVolumes.cpp) x mixer slot 3
                // (eVOL_ROADNOISE_WET_ROAD), the side's pitch and azimuth; not gated by the ground contact or the NIS tweak
                if (_wetRoad[n] != null)
                {
                    int wv = (gen * 24000) >> 15;
                    wv = (wv * _roadObj.Out(3, MixMap.Vol)) >> 15;
                    _wetRoad[n].Set(wv / 32767f * paused, pitch / 4096f, az);
                }
                if (trans.Playing && (SoundState.Paused || NisEventRunner.DisableRoadNoise != 0)) trans.Set(0f, 1f, az);
            }
        }

        static int RoadLoopSlot(int loop)
        {
            var sel = SoundLibrary.Patch(RoadBank).Select("FX_ROADNOISE");
            return sel != null && loop >= 0 && loop < sel.Length ? sel[loop] : loop + 1;
        }

        void PlayTransition(SndVoice v, int id, int az)
        {
            int vol = id == 2 ? 26000 : id == 3 ? 0x7FFF : 15500;   // EAXVolumes.cpp GetRoadNoiseTransitionVol
            vol = (vol * _roadObj.Out(id == 2 ? 13 : id == 3 ? 14 : 2, MixMap.Vol)) >> 15;
            var sel = SoundLibrary.Patch(RoadBank).Select("FX_ROADNOISE_TRANS");
            int slot = sel != null && id < sel.Length ? sel[id] : 12 + id;
            v.Once(SoundLibrary.ClipAt(RoadBank, slot));
            v.Set(vol / 32767f * (SoundState.Paused ? 0f : 1f), new Slope(3600f, 4400f, 12f, 60f).Value(Car.SpeedMps * 2.23699f) / 4096f, az);
        }

        /// <summary>CARSFX_WindNoise::UpdateParams / UpdateCSISParams (CARSFX_WindNoise.cpp:107-191): left/right/rumble volume =
        /// mixer slots 2/3/4 x the speed weight (speed clamped to 2..40 m/s, / 40 x 32767), pitch slot 5, azimuths slots 0/1,
        /// intensity = the speed ratio x 1040 (the patch's crossfade), FX dry 0x7FFF.</summary>
        void ApplyWind()
        {
            if (_wind == null) return;
            float v = Car.SpeedMps;
            if (v < 2f) v = 2f; else if (v > 40f) v = 40f - 0.05f;
            float ratio = v / 40f;
            int weighted = (int)(ratio * 32767f);
            _wind[1] = (_windObj.Out(2, MixMap.Vol) * weighted) >> 15;
            _wind[2] = (_windObj.Out(3, MixMap.Vol) * weighted) >> 15;
            _wind[8] = (_windObj.Out(4, MixMap.Vol) * weighted) >> 15;
            _wind[0] = _windObj.Out(5, MixMap.Pitch);
            _wind[3] = _windObj.Out(0, MixMap.Azim);
            _wind[4] = _windObj.Out(1, MixMap.Azim);
            _wind[5] = (int)(ratio * 1040f);
            _wind[11] = 0x7FFF;
        }

        // ------------------------------------------------------------------ CARSFX_BottomOut (jump landings)
        SndObj _bottomObj, _preColObj;
        readonly StitchPlayer[] _land = new StitchPlayer[3];
        readonly float[] _landIntensity = new float[3];
        bool _frontTouched = true, _rearTouched = true, _rightTouched = true, _leftTouched = true, _leaning;
        float _frontHang, _rearHang;
        static readonly int[] JumpLandingVolumes = { 13000, 15000, 24000, 0x7FFF };

        /// <summary>CARSFX_BottomOut::UpdateParams (CARSFX_BottomOut.cpp:118-255): the front or rear pair landing after more
        /// than 0.12 s in the air (hard after 0.7 s, or any pair landing while the car leans past 0.8) plays a collision
        /// stitch: soft GEN_RND_OFFSET(i, 81, 4, 4), hard (i, 93, 1, 4); intensity = 4 x JumpLandingIntensity (Slope 0..0.65
        /// to 0..1) of the landing pair's load (port: share of the car weight [?]) x 63.5. The physics bottom-out message
        /// is not produced by the port.</summary>
        void UpdateBottomOut(float dt)
        {
            if (_bottomObj == null) return;
            var c = new bool[4];
            var load = new float[4];
            for (int i = 0; i < 4; i++)
            {
                var w = Car.WheelAudio(i);
                int k = w.front ? (w.left ? 0 : 1) : (w.left ? 3 : 2);
                c[k] = w.contact;
                load[k] = w.load;
            }
            bool tf = c[0] && c[1], tr = c[1] && c[2], tb = c[2] && c[3], tl = c[3] && c[0];
            int onGround = (c[0] ? 1 : 0) + (c[1] ? 1 : 0) + (c[2] ? 1 : 0) + (c[3] ? 1 : 0);
            if (!_leaning && Vector3.Dot(Car.transform.up, Vector3.up) < 0.8f && onGround < 1) _leaning = true;
            bool front = tf && _frontHang > 0.12f && !_frontTouched, back = tb && _rearHang > 0.12f && !_rearTouched;
            bool play = false, hard = false;
            if (front || (back && !_leaning)) { play = true; hard = (front && _frontHang > 0.7f) || (back && _rearHang > 0.7f); }
            if (_leaning && ((tf && !_frontTouched) || (tb && !_rearTouched) || (tr && !_rightTouched) || (tl && !_leftTouched))) { play = true; hard = true; }
            if (play)
            {
                var rb = Car.GetComponent<Rigidbody>();
                float weight = Mathf.Max(1f, (rb ? rb.mass : 1400f) * 9.81f);
                float z = (front ? load[0] + load[1] : load[2] + load[3]) / weight;
                float intensity = 4f * new Slope(0f, 1f, 0f, 0.65f).Value(z) * 63.5f;
                LandJumpPlay(intensity, hard);
            }
            if (onGround == 4 && _leaning) _leaning = false;   // GetWheelsOnGround() == 1.0 (a fraction) [?]
            if (tf) { _frontTouched = true; _frontHang = 0; } else { _frontTouched = false; _frontHang += dt; }
            if (tb) { _rearTouched = true; _rearHang = 0; } else { _rearTouched = false; _rearHang += dt; }
            _rightTouched = tr;
            _leftTouched = tl;
        }

        void LandJumpPlay(float intensity, bool hard)
        {
            int idx = -1;
            for (int n = 0; n < 3; n++) if (_land[n] == null) idx = n;
            if (idx < 0) return;
            float fi = Mathf.Clamp(intensity, 0f, 127f);
            int id = hard ? WorldAudio.GenRndOffset(fi, 93, 1, 4) : WorldAudio.GenRndOffset(fi, 81, 4, 4);
            _land[idx] = new StitchPlayer(_holder, 0, StitchPlayer.Get(0, id), 0, 0);
            _landIntensity[idx] = intensity;
            _bottomObj.Set(1, 0x7FFF);
            _bottomObj.Set(3, (int)intensity << 8);
        }

        // ------------------------------------------------------------------ CARSFX_PreColWoosh
        StitchPlayer _preCol;
        readonly InterpLine _preFade = new InterpLine();
        bool _goingToCollide, _bailAll;
        float _preReset, _preActive;

        /// <summary>CARSFX_PreColWoosh (CARSFX_PreColWoosh.cpp:58-153): a woosh while a barrier is detected ahead, cut over
        /// 130 ms on impact or after 0.4 s. FRONT_BARRIER comes from the game's barrier probe; the port casts a ray 0.4 s
        /// ahead of the car [ours].</summary>
        void UpdatePreCol(float dt)
        {
            if (_preColObj == null) return;
            bool barrier = false;
            float v = Car.ForwardSpeed;
            if (v > 10f)
                foreach (var hit in Physics.RaycastAll(Car.transform.position + Vector3.up * 0.6f, Car.transform.forward, v * 0.4f))
                    if (!hit.collider.GetComponentInParent<PlayerCar>() && Vector3.Dot(hit.normal, -Car.transform.forward) > 0.7f) { barrier = true; break; }
            if (barrier && !_goingToCollide && !_bailAll && _preReset == 0f) { _preActive = 0f; _goingToCollide = true; _preFade.Initialize(1f, 1f, 1); }
            _preFade.Update(dt);
            _preReset = Mathf.Max(0f, _preReset - dt);
            _preColObj.Set(2, 0);
            if (_goingToCollide)
            {
                _preColObj.Set(2, 0x7FFF);
                _preActive += dt;
                if (_preCol == null)
                {
                    float intensity = Mathf.Clamp(Mathf.Clamp(WorldAudio.WooshVolVsVel(Car.SpeedMps), 0f, 0.99f) * 127f, 0f, 127f);
                    var (b, n, s) = WorldAudio.WooshBlock(WorldAudio.DriveByPreCol);
                    _preCol = new StitchPlayer(_holder, 1, StitchPlayer.Get(1, WorldAudio.GenRndOffset(intensity, b, n, s)), 0, 0);
                }
                if (!barrier || _preActive > 0.4f) BailOnWoosh();
            }
            if (_bailAll && _preFade.Value < 0.01f) { _preCol?.Stop(); _preCol = null; _bailAll = false; }
        }

        void BailOnWoosh()
        {
            _bailAll = true;
            _goingToCollide = false;
            _preFade.Initialize(_preFade.Value, 0f, 130);
            _preReset = 0.4f;
        }

        void ApplyBottomOutPreCol(float paused)
        {
            if (_bottomObj != null)
            {
                for (int n = 0; n < 3; n++)
                {
                    if (_land[n] == null) continue;
                    int vol = (JumpLandingVolumes[Mathf.Clamp((int)_landIntensity[n] >> 5, 0, 3)] * _bottomObj.Out(1, MixMap.Vol)) >> 15;
                    if (!_land[n].Update(vol, _bottomObj.Out(0, MixMap.Azim), paused) || SoundState.Paused) { _land[n].Stop(); _land[n] = null; }
                }
                _bottomObj.Set(1, 0);
                _bottomObj.Set(2, 0);
            }
            if (_preCol != null && _preColObj != null)
                if (!_preCol.Update((int)(_preColObj.Out(1, MixMap.Vol) * _preFade.Value), _preColObj.Out(0, MixMap.Azim), paused)) _preCol = null;
        }
    }
}
