using System.Collections.Generic;
using System.IO;
using NfsMw.Audio;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// A car's engine sound, as EAXCar + CARSFX_DualGinsuEng / CARSFX_SingleGinsuEng / CARSFX_AEMSEngine
    /// (EAXSound/CARSFX/CARSFX_Engine.cpp; PC SetGinsuParams @0x4EEA00): <see cref="CarSoundModel"/> runs the
    /// SFXCTL controllers, the mix map turns their outputs into volumes, pitch, pan and low-pass, and this component
    /// renders them:
    /// <list type="bullet">
    /// <item>Ginsu: the engineaudio Filename_GinsuAccel/Decel recordings resynthesised by <see cref="GinsuSynthesis"/>
    /// at the rpm frequency HybridMotor scales into the car's MinRPM..MaxRPM (UpdateFrequency(freq, 60 ms)); below
    /// the recording's lowest rpm the pitch drops instead (fPitchBelowMinFreq). Volume = EngVolAccel/DecelGinsu x
    /// mixer slot 2 (SNDvol 0..127), pitch x mixer slot 4, low-pass = min(GinsuLPF, mixer slot 5). The player's car
    /// uses both recordings (PC SFXIDs 0x53BFC), AI racers only the accel one, cops none.</item>
    /// <item>The AEMS engine layer: a Csis::CAR instance (ENGINES_AEMS2.h CARStruct) runs the CAR patch of the car's
    /// engine banks through the AEMS interpreter (<see cref="AemsHost"/>): the patch crossfades its rpm loops, pitches
    /// them and plays the limiter from MAX_RPM. Parameters as CARSFX_GinsuEngine::SetEngineParams (player, Ginsu AI) or
    /// CARSFX_AEMSEngine::SetEngineParams (cops, AEMS AI; PC InitSFX @0x4DC360: car_class = clamp(CarID, 0, 99),
    /// sPU_or_EE = clamp(member, 0, 2)). Bank: only engineaudio BankName_mainRAM (the "_EE" bank) is loaded, and the
    /// load sets the member to 1 before InitSFX: CARSFX_DualGinsuEng / SingleGinsuEng::SetupLoadData (PC @0x4E8A40 /
    /// @0x4E8930) always, CARSFX_AEMSEngine::SetupLoadData (@0x4E8880) only when BankName_auxRAM is non-empty (else no bank
    /// and no CAR). The EE patch keeps itself alive only for sPU_or_EE = 1 (harness `aems run`: the SPU patch with 1 and
    /// the EE patch with 0 destroy themselves). The player's car with engineaudio Tranny adds Csis::CAR_TRANNY
    /// (CAR_TRANNY.abk; car_class clamped 0..60, InitializeEngine @0x4EE1C0).</item>
    /// </list>
    /// The renderer runs in OnAudioFilterRead on the car's own AudioSource (2D: the mixer supplies pan and rolloff);
    /// other car sounds sit on child objects.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class EngineAudio : MonoBehaviour, ISndUnit
    {
        public PlayerCar Car;
        /// <summary>Extra gain on everything this component renders (the mix map sets the real levels).</summary>
        public float Volume = 1f;
        /// <summary>Run the AEMS CAR layer (see the class notes); off leaves only the Ginsu synth.</summary>
        public static bool UseAemsLayer = true;
        public CarSoundModel Model { get; private set; }
        public bool IsPlayer { get; private set; }
        /// <summary>The pvehicle collection name the car's audio attributes come from.</summary>
        public string CarName { get; private set; }
        public bool IsCop { get; private set; }
        public int MixState { get; private set; } = -1;
        public int MixInstance { get; private set; } = -1;
        /// <summary>Every live car engine (cops for the AI drive-bys, WorldAudio).</summary>
        public static readonly List<EngineAudio> All = new List<EngineAudio>();
        /// <summary>The car state's SFXCTL_Tunnel (controller 6; AI and cops: occlusion and tunnel inputs).</summary>
        public TunnelCtl Tunnel { get; private set; }
        /// <summary>
        /// CSTATEMGR_CarState::UpdateParams (PC @0x4E6330): a car only sounds while its state is attached. AI racers and
        /// cops attach in list order while within 135 m of the camera and a state is free (AI: min(opponents, 4), cops: 4,
        /// EnterWorld @0x4F3000 / @0x4F3350), and detach outside the radius. The port keeps one mixer instance per car and
        /// gates the voices instead [ours].
        /// </summary>
        public bool Connected { get; private set; } = true;
        float _connGain = 1f;

        public static void UpdateConnections()
        {
            var cam = SndMixer.Cam;
            int cops = 0, ai = 0;
            foreach (var e in All) if (e && e.Connected && !e.IsPlayer) { if (e.IsCop) cops++; else ai++; }
            foreach (var e in All)
            {
                if (!e || e.IsPlayer || !e.Car) continue;
                bool inRadius = cam && Vector3.Distance(e.Car.transform.position, cam.position) < 135f;
                if (e.Connected && !inRadius) { e.Connected = false; if (e.IsCop) cops--; else ai--; }
                else if (!e.Connected && inRadius && (e.IsCop ? cops < 4 : ai < 4)) { e.Connected = true; if (e.IsCop) cops++; else ai++; }
            }
        }

        /// <summary>The attach gain other car sounds multiply by (a short ramp instead of a cut) [ours].</summary>
        public float ConnectGain => _connGain;

        /// <summary>The player's SFXObj_Reverb (PLAYERCAR obj 16).</summary>
        public SndObj ReverbObj { get; private set; }

        SndObj _engObj;
        GinsuSynthesis _acc, _dec;
        GinsuSynthData _accData, _decData;
        float _minFreq;
        int _rate;
        // audio-thread parameters
        volatile float _accVol, _decVol, _pitchAcc, _pitchDec, _lpf = 22000f, _panL = 0.707f, _panR = 0.707f;
        float _lpState, _fracA, _fracD;
        float[] _bufA = new float[4096], _bufD = new float[4096];
        float _lastA, _lastD, _nextA, _nextD;
        AemsHost _aems;
        CsisObj _car, _tranny;
        /// <summary>SFXCTL_Engine::m_Rotation: MIN_EXHVIEW_ROTATION, never changed (SFXCTL_Engine.cpp:84).</summary>
        const int EngineRotation = 150;
        /// <summary>CARSFX_Engine.cpp TRANNY_SCALER.</summary>
        const float TrannyScaler = 15f;
        static readonly Dictionary<string, GinsuSynthData> Cache = new Dictionary<string, GinsuSynthData>();

        /// <summary>Player or AI racer engine (volume ≥ 0.5 marks the local player, as GameBootstrap passes it).</summary>
        public static EngineAudio Attach(PlayerCar car, string carName, float volume = 0.55f) => Create(car, carName, volume >= 0.5f, false);

        /// <summary>A cop car's engine: the COPCAR mix state's CARSFX_AEMSEngine (EnterWorld SFXIDs 0x20081 @0x4F3350).</summary>
        public static EngineAudio AttachCop(PlayerCar car, string carName) => Create(car, carName, false, true);

        static EngineAudio Create(PlayerCar car, string carName, bool player, bool cop)
        {
            if (!car) return null;
            var mixer = SndMixer.Ensure();
            var levels = player ? GameSession.ActiveCar : null;
            if (player) CarAudioAttribs.ResetSharedTurbo();   // a new world: the first car attached picks g_TurboInfo
            if (player) EngineBanks.Reset();                  // LoadInGameSoundBanks → CSTATEMGR_CarState::ResetCarBanks (@0x4F30D0)
            // AI racers take their engineaudio interval from the player's engine level (GenerateUpgradedEngine)
            int upgrade = player || cop ? -1 : GameSession.ActiveCar?.Engine ?? 0;
            var attribs = CarAudioAttribs.For(carName, levels, upgrade);
            int state = player ? MixMap.PlayerCar : cop ? MixMap.CopCar : MixMap.AiRaceCar;
            int inst = mixer.Acquire(state);
            var src = car.GetComponent<AudioSource>();
            if (!src) src = car.gameObject.AddComponent<AudioSource>();
            src.spatialBlend = 0f;
            src.loop = true;
            src.playOnAwake = false;
            src.dopplerLevel = 0f;
            src.volume = 1f;
            var ea = car.gameObject.AddComponent<EngineAudio>();
            ea.Car = car;
            ea.CarName = carName;
            ea.IsPlayer = player;
            ea.IsCop = cop;
            ea.Connected = player;
            ea._connGain = player ? 1f : 0f;
            ea.MixState = state;
            ea.MixInstance = inst;
            ea._upgrade = upgrade;
            ea._banksResolved = player;
            bool dual = player;
            ea.Model = new CarSoundModel(attribs, state, inst, player, dual);
            int engineObj = cop ? 0 : player ? 2 : 1;
            ea._engObj = new SndObj(mixer.Map, MixMap.ObjectId(false, SndMixer.StateOr(state, inst), Mathf.Max(0, inst), engineObj));
            if (inst >= 0) ea._engObj.Attach(true);
            ea._aiDecided = player || cop;
            if (!cop)
            {
                ea._accData = Gin(attribs.GinAccel);
                if (dual) ea._decData = Gin(attribs.GinDecel);
                if (ea._accData == null) { ea._accData = Gin(Files(carName).Item1); }
                if (ea._accData != null)
                {
                    ea._acc = new GinsuSynthesis();
                    ea._acc.SetSynthData(ea._accData);
                    ea._acc.StartSynthesis(ea._accData.MinFrequency);
                    ea._minFreq = ea._accData.MinFrequency;
                }
                if (ea._decData != null)
                {
                    ea._dec = new GinsuSynthesis();
                    ea._dec.SetSynthData(ea._decData);
                    ea._dec.StartSynthesis(ea._decData.MinFrequency);
                }
            }
            ea.SetupAems(attribs);
            if (inst >= 0)
            {
                var ctl6 = new SndObj(mixer.Map, MixMap.ObjectId(true, state, inst, 6));
                ea.Tunnel = new TunnelCtl(car, ctl6, player, player && inst == 0);
                if (player)
                {
                    ea.ReverbObj = new SndObj(mixer.Map, MixMap.ObjectId(false, state, inst, 16));
                    ea.ReverbObj.Attach(true);
                }
            }
            // the FX send of the engine (after this component's OnAudioFilterRead) [ours: one global send level]
            if (player) car.gameObject.AddComponent<SendTap>();
            All.Add(ea);
            ea._rate = AudioSettings.outputSampleRate;
            src.clip = AudioClip.Create("engine", 1, 1, ea._rate, false);
            src.Play();
            mixer.Register(ea);
            return ea;
        }

        static GinsuSynthData Gin(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string path = Path.IsPathRooted(file) ? file : Path.Combine(GameData.Root, "SOUND", "ENGINE", file);
            if (!File.Exists(path)) return null;
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var g)) return g;
                try { g = GinsuSynthData.Load(path); }
                catch (System.Exception e) { Debug.LogWarning("NFSMW engine audio: " + path + ": " + e.Message); g = null; }
                Cache[path] = g;
                return g;
            }
        }

        /// <summary>(accel, decel) GIN paths for a car from its engineaudio collection (stock level).</summary>
        public static (string, string) Files(string carName)
        {
            var db = GameData.Attributes;
            var pv = db?.Get("pvehicle", carName.ToLowerInvariant());
            if (pv == null) return (null, null);
            var refs = pv.Refs("engineaudio");
            if (refs.Length == 0 || !db.Collections.TryGetValue((refs[0].cls, refs[0].key), out var ea)) return (null, null);
            string dir = Path.Combine(GameData.Root, "SOUND", "ENGINE");
            string acc = ea.StringKey("Filename_GinsuAccel"), dec = ea.StringKey("Filename_GinsuDecel");
            acc = string.IsNullOrEmpty(acc) ? null : Path.Combine(dir, acc);
            dec = string.IsNullOrEmpty(dec) ? null : Path.Combine(dir, dec);
            if (acc != null && !File.Exists(acc)) acc = null;
            if (dec != null && !File.Exists(dec)) dec = null;
            return (acc, dec);
        }

        void SetupAems(CarAudioAttribs a)
        {
            if (!UseAemsLayer || string.IsNullOrEmpty(a.EngineBank)) return;
            // CARSFX_AEMSEngine::SetupLoadData: no BankName_auxRAM, no bank load (the cop engine stays silent)
            if (IsCop && string.IsNullOrEmpty(a.EngineBankAux)) return;
            _aems = AemsHost.Create(transform, "AEMS Engine");
            // new Csis::CAR(CarID, 0 x 11, SPU_or_EE, 0 x 13): SPU_or_EE = 1 from SetupLoadData; the BankName_mainRAM bank only
            var p = new int[26];
            p[0] = Mathf.Clamp(a.CarId, 0, 99);
            p[12] = 1;
            _car = new CsisObj(_aems, "CAR", p, "ENGINE/" + a.EngineBank);
            // CARSFX_GinsuEngine::InitializeEngine: the local player's car with engineaudio Tranny
            if (IsPlayer && a.Tranny)
                _tranny = new CsisObj(_aems, "CAR_TRANNY", new[] { Mathf.Clamp(a.CarId, 0, 60), 0, 0, 0, 0, 25000, 0, 0x7FFF, 0 }, "ENGINE/CAR_TRANNY.abk");
        }

        void OnDestroy()
        {
            // EAXSound::DestroyEAXCar → CSTATEMGR_CarState::DestroyCar (@0x4F3200)
            if (_banksResolved && !IsPlayer && Model != null) EngineBanks.Destroy(this, Model.A.EngineKey);
            All.Remove(this);
            var m = SndMixer.Instance;
            if (!m) return;
            m.Unregister(this);
            if (MixInstance >= 0) m.Release(MixState, MixInstance);
        }

        bool _aiDecided, _aiGinsu = true, _retired;
        /// <summary>CARSFX_AEMSEngine's m_iAIPitchOffset: a static counter stepping 500 mod 3000, minus 1500.</summary>
        static int _aiPitchCounter;
        readonly int _aiPitchOffset = (_aiPitchCounter = (_aiPitchCounter + 500) % 3000) - 1500;

        /// <summary>
        /// CSTATEMGR_AICar::QueueSlots (PC @0x4E5C80) / EnterWorld (@0x4F3000): AI racers get the single-Ginsu engine
        /// (SFX set 0x92, obj 1) only when the race needs at most two engine slots (opponents + remote, +1 for cops outside
        /// quick races); otherwise, and in free roam, the AEMS engine (0x91, obj 0). Decided once the race's entrants exist.
        /// </summary>
        void DecideAiEngine()
        {
            var race = RaceManager.Active;
            if (race != null && race.Entrants.Count == 0) return;   // still spawning
            _aiDecided = true;
            int n = 4;
            if (race != null)
            {
                n = 0;
                foreach (var e in race.Entrants) if (!e.Human) n++;
                if (GameSession.Mode != GameMode.QuickRace) n++;
                n = Mathf.Min(n, 4);
            }
            _aiGinsu = race != null && n <= 2;
            // CARSFX_AEMSEngine::SetupLoadData (@0x4E8880): an AEMS AI engine without BankName_auxRAM loads no bank
            if (!_aiGinsu && _car != null && string.IsNullOrEmpty(Model.A.EngineBankAux)) { _car.Release(); _car = null; }
            if (!_aiGinsu)
            {
                var m = SndMixer.Instance;
                if (m && MixInstance >= 0)
                {
                    _engObj.Attach(false);
                    _engObj = new SndObj(m.Map, MixMap.ObjectId(false, MixState, MixInstance, 0));
                    _engObj.Attach(true);
                }
            }
        }

        /// <summary>[ours] CarSpawner gives every spawned car a racer engine; traffic cars (WorldAudio's TRAFFIC states) and
        /// cop cars (CopAudio's COPCAR states) drop it once they are known.</summary>
        internal bool Misclassified() => !IsPlayer && !IsCop && Car && (Car.Traffic || Car.GetComponent<NfsMw.Game.Police.CopCar>());

        int _upgrade = -1;
        bool _banksResolved;

        /// <summary>
        /// EAXSound::CommitAssets → CSTATEMGR_CarState::ResolveCarBanks (PC @0x4F9020) for every AI racer / cop engine that has
        /// not been resolved yet and is known to be one (the first tick after spawning; the exe's batch is the cars added since
        /// the last commit). A car mapped to another engineaudio rebuilds its model, Ginsu and AEMS engine from it.
        /// </summary>
        static void ResolvePendingBanks()
        {
            var pending = new List<EngineAudio>();
            foreach (var e in All)
                if (e && !e._retired && !e.IsPlayer && !e._banksResolved && e.Car && e.Model != null && !e.Misclassified()) pending.Add(e);
            if (pending.Count == 0) return;
            // CopsCanBeInGame = !FEDatabase->IsQuickRaceMode() (FEGameMode bit 2 at FEDatabase+0x12C, @0x4F9083)
            var keys = EngineBanks.Resolve(pending, GameSession.Mode != GameMode.QuickRace);
            for (int i = 0; i < pending.Count; i++)
            {
                var e = pending[i];
                e._banksResolved = true;
                if (keys[i] != 0 && keys[i] != e.Model.A.EngineKey) e.Remap(keys[i]);
            }
        }

        /// <summary>engineaudio::ChangeWithDefault(mapping.Finish): the car's engine attributes come from another engineaudio.</summary>
        void Remap(uint engineKey)
        {
            var attribs = CarAudioAttribs.For(CarName, null, _upgrade, engineKey);
            Model = new CarSoundModel(attribs, MixState, MixInstance, false, false);
            if (!IsCop)
            {
                var acc = Gin(attribs.GinAccel);
                if (acc != null)
                {
                    var g = new GinsuSynthesis();
                    g.SetSynthData(acc);
                    g.StartSynthesis(acc.MinFrequency);
                    _accData = acc;
                    _minFreq = acc.MinFrequency;
                    _acc = g;
                }
            }
            if (_car != null) { _car.Release(); _car = null; }
            if (_tranny != null) { _tranny.Release(); _tranny = null; }
            if (_aems) Destroy(_aems.gameObject);
            _aems = null;
            SetupAems(attribs);
        }

        void Retire()
        {
            _retired = true;
            var sfx = GetComponent<CarSfx>();
            if (sfx) Destroy(sfx);
            if (_aems) Destroy(_aems.gameObject);
            _acc = _dec = null;
            Destroy(this);
        }

        public void Tick(float dt)
        {
            if (_retired) return;
            if (Misclassified()) { Retire(); return; }
            if (!_banksResolved) ResolvePendingBanks();
            if (!_aiDecided) DecideAiEngine();
            if (!Car || Car.Spec == null) return;
            Model.Tick(Car, dt);
            Model.Positions(Car, dt, 2.5f);
            if (dt > 0) Tunnel?.Update(dt);
        }

        public void Apply(float dt)
        {
            if (_retired || !Car || Model == null) return;
            var m = Model;
            bool mapped = _engObj.Mapped && MixInstance >= 0;
            float pitchMul = mapped ? _engObj.Pitch(4) : 1f;
            int azim = mapped ? _engObj.Out(0, MixMap.Azim) : 0;
            float pan = SndVoice.Pan(azim);
            _panL = Mathf.Sqrt(0.5f * (1f - pan));
            _panR = Mathf.Sqrt(0.5f * (1f + pan));
            _connGain = Mathf.MoveTowards(_connGain, Connected ? 1f : 0f, dt * 8f);
            float paused = (SoundState.Paused ? 0f : 1f) * _connGain;
            // SetGinsuParams
            float ginsuMix = mapped ? _engObj.Vol(2) : 0f;
            float freq = m.GinsuScaledRpm, below = 1f;
            if (_acc != null)
            {
                if (freq < _minFreq) { below = freq / _minFreq; freq = _minFreq; }
                _acc.UpdateFrequency(freq, 60f);
                _dec?.UpdateFrequency(freq, 60f);
            }
            int accVol = Mathf.Clamp(((m.EngVolAccelGinsu * (int)(ginsuMix * 32767f)) >> 15) >> 8, 0, 0x7F);
            int decVol = Mathf.Clamp(((m.EngVolDecelGinsu * (int)(ginsuMix * 32767f)) >> 15) >> 8, 0, 0x7F);
            if (!_aiGinsu) accVol = decVol = 0;   // CARSFX_AEMSEngine: no Ginsu layer
            // SNDCTRL_drylevel(m_GinsuDryVol >> 8): the tunnel controller's dry level on the Ginsu voices (0..127)
            float dry = Tunnel != null && IsPlayer ? Mathf.Clamp(Tunnel.GinsuDry >> 8, 0, 0x7F) / 127f : 1f;
            _accVol = accVol / 127f * Volume * paused * dry;
            _decVol = decVol / 127f * Volume * paused * dry;
            float rateRatio = (_accData != null ? _accData.SampleRate : 32000) / (float)Mathf.Max(8000, _rate);
            _pitchAcc = below * pitchMul * rateRatio;
            _pitchDec = below * pitchMul * ((_decData != null ? _decData.SampleRate : 32000) / (float)Mathf.Max(8000, _rate));
            float lpf = m.GinsuLpf;
            if (mapped && m.Dual) lpf = Mathf.Min(lpf, Mathf.Max(100, _engObj.Out(5, MixMap.Freq)));
            _lpf = Mathf.Clamp(lpf, 100f, 24000f);
            if (_car != null) SetCarParams(m, mapped, pitchMul, azim, paused);
        }

        /// <summary>CARSFX_AEMSEngine::SetEngineParams (CARSFX_Engine.cpp:140-175) for cops and AEMS AI, else
        /// CARSFX_GinsuEngine::SetEngineParams (:355-400) with the CAR_TRANNY update.</summary>
        void SetCarParams(CarSoundModel m, bool mapped, float pitchMul, int azim, float paused)
        {
            int dmix = mapped ? _engObj.Out(1, MixMap.Vol) : 0;
            _aems.Gain = Volume * paused;
            var c = _car;
            if (IsCop || !_aiGinsu)
            {
                float scaled = (m.EngRpm - 1000f) * ((m.A.MaxRpm - m.A.MinRpm) / 9000f) + m.A.MinRpm;
                int curPitch = (int)((_aiPitchOffset + 16000) * pitchMul - 16000f);
                int tmp = (Model.EngineVol * dmix) >> 15;
                tmp = (tmp * 0x7FFF) >> 15;
                c[4] = tmp - 0x7FFF;
                c[5] = tmp - 0x7FFF;
                c[1] = (int)scaled;
                c[16] = curPitch;
                _engObj.Set(1, dmix);
            }
            else
            {
                int tmp = (m.EngVolAems * dmix) >> 15;
                tmp = (tmp * 0x7FFF) >> 15;
                c[4] = tmp - 0x7FFF;
                c[5] = tmp - 0x7FFF;
                c[25] = (m.EngVolRedLine * dmix) >> 15;
                c[1] = (int)m.GinsuScaledRpm;
                c[16] = (int)(pitchMul * 16383f) - 0x3FFF;
            }
            c[3] = (int)(m.EngTrq * 10.24f);
            c[17] = EngineRotation;
            c[15] = azim;
            if (_tranny == null) return;
            var t = _tranny;
            t[1] = (int)(Car.ForwardSpeed * TrannyScaler);
            t[2] = mapped ? _engObj.Out(3, MixMap.Vol) : 0;
            t[3] = azim;
            t[4] = mapped ? _engObj.Out(4, MixMap.Pitch) : 4096;
            t[5] = mapped ? _engObj.Out(5, MixMap.Freq) : 25000;
            t[6] = Tunnel != null ? Tunnel.AemsWet : 0;
            t[7] = Tunnel != null ? Tunnel.AemsDry : 0x7FFF;
            t[8] = m.ShiftState != 0 ? 1 : 0;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            var acc = _acc;
            if (acc == null) return;
            var dec = _dec;
            int frames = data.Length / channels;
            float pa = _pitchAcc, pd = _pitchDec, va = _accVol, vd = _decVol;
            // buffer index 0/1 = the two samples carried from the previous block; the block reads up to index used+1
            int usedA = (int)(frames * pa + _fracA), usedD = dec != null ? (int)(frames * pd + _fracD) : 0;
            if (usedA + 2 > _bufA.Length) _bufA = new float[(usedA + 2) * 2];
            if (usedD + 2 > _bufD.Length) _bufD = new float[(usedD + 2) * 2];
            _bufA[0] = _lastA;
            _bufA[1] = _nextA;
            if (usedA > 0) { acc.Read(TmpA(usedA), usedA); System.Array.Copy(_tmpA, 0, _bufA, 2, usedA); }
            if (dec != null)
            {
                _bufD[0] = _lastD;
                _bufD[1] = _nextD;
                if (usedD > 0) { dec.Read(TmpD(usedD), usedD); System.Array.Copy(_tmpD, 0, _bufD, 2, usedD); }
            }
            float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * _lpf / Mathf.Max(8000, _rate));
            float posA = _fracA, posD = _fracD;
            float l = _panL, r = _panR;
            for (int i = 0; i < frames; i++)
            {
                int ia = (int)posA;
                float fa = posA - ia;
                float s = (_bufA[ia] + (_bufA[ia + 1] - _bufA[ia]) * fa) * va;
                posA += pa;
                if (dec != null)
                {
                    int id = (int)posD;
                    float fd = posD - id;
                    s += (_bufD[id] + (_bufD[id + 1] - _bufD[id]) * fd) * vd;
                    posD += pd;
                }
                _lpState += alpha * (s - _lpState);
                float o = _lpState;
                if (channels >= 2)
                {
                    data[i * channels] = o * l;
                    data[i * channels + 1] = o * r;
                    for (int c = 2; c < channels; c++) data[i * channels + c] = 0;
                }
                else data[i] = o;
            }
            // carry the fractional position and the two samples around it
            _fracA = posA - usedA;
            _lastA = _bufA[usedA];
            _nextA = _bufA[usedA + 1];
            if (dec != null)
            {
                _fracD = posD - usedD;
                _lastD = _bufD[usedD];
                _nextD = _bufD[usedD + 1];
            }
        }

        float[] _tmpA = new float[4096], _tmpD = new float[4096];
        float[] TmpA(int n) { if (_tmpA.Length < n) _tmpA = new float[n * 2]; return _tmpA; }
        float[] TmpD(int n) { if (_tmpD.Length < n) _tmpD = new float[n * 2]; return _tmpD; }
    }
}
