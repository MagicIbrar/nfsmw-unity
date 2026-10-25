using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// A car's audio attributes (EAXCar::Attach): engineaudio at the engine-upgrade interval (UpgradeIntervals),
    /// its acceltrans, the shiftpattern of pvehicle ShiftSND and the turbosfx of TurboSND at the installed
    /// transmission/induction level.
    /// </summary>
    public sealed class CarAudioAttribs
    {
        public string GinAccel, GinDecel, EngineBank, EngineBankAux, SweetBank, ShiftBank, TurboBank;
        public bool HasTurbo, Tranny;
        /// <summary>The engineaudio collection the engine fields came from (class, key); (0, 0) without one.</summary>
        public uint EngineClass, EngineKey;
        /// <summary>EAXCar m_EngUGL / m_TransmissionUGL: AEMS_LEVEL2 and AEMS_LEVEL0 from the constructor (nothing in the
        /// decomp changes them).</summary>
        public int EngineUgl = 2, TransmissionUgl = 0;
        /// <summary>EAXCar::Attach's g_TurboInfo: the first car to attach picks the turbosfx every later car uses.</summary>
        static CarAudioAttribs _sharedTurbo;
        public static void ResetSharedTurbo() => _sharedTurbo = null;
        public int CarId, AemsVol = 32767, DecelAemsVol = 32767, GinsuAccelVol = 32767, GinsuDecelVol = 32767, MasterVol = 32767, VolShiftSweets = 25000;
        public float MinRpm = 1000, MaxRpm = 9000, AemsMixL = 0.5f, AemsMixS = 1f, GinsuMixL = 1f, GinsuMixS = 0.35f;
        public float DecelAemsMixL = 0.8f, DecelAemsMixS = 0.05f, DecelGinsuMixL = 0.8f, DecelGinsuMixS = 0.45f;
        public float AccelDeltaThr = 100f, DecelDeltaThr = 95f, AclNegL = 0.3f, AclNegS = 0.08f, DecelFadeIn, DecelFadeOut = 0.25f, LowPassCutoff = 24000;
        public int DecelMinRpm = 2000, DecelMaxRpm = 7500;
        public float[] RpmMap;
        // acceltrans
        public int IdlePeakT = 300, IdleResumeT = 1000, IdlePeakRpm = 6000, InterruptT = 700;
        public float IdlePeakVol = 0.5f;
        // shiftpattern
        public float DownShiftSoundDelay = 0.1f, UpShiftSoundDelay = 0.02f, UpEngagingAttackVol = 0.05f, DownReattachScale = 0.01f;
        public int LfoRpmAmp, LfoVolAmp, LfoRpmDecay, LfoVolDecay, LfoRpmFreq = 1, LfoVolFreq = 1, DownEngagingRiseT = 50, UpEngagingAttackT = 600,
            DownEngagingFallT = 50, UpVolShift = 22500, DownDisengageFallT = 100, DownDisengageFallRpm = 100, DownVolShift = 18000, DownEngagingRiseRpm = 400,
            DownEngagingFallRpm = 300;
        public (int rpm, int time)[] UpDisengageFall = { (-1200, 100) };
        public float[][] UpDisengageFallCurve = { null };
        public (int rpm, int time) UpEngage = (0, 100);
        public float[] UpEngageCurve;
        // turbosfx
        public int VolSpool = 26000, VolBlowoff1 = 26000, VolBlowoff2 = 26000, VolSputters;
        public float ChargeTime = 20f, LeakRate = 0.5f;

        static AttribCollection Ref(AttribDatabase db, AttribCollection c, string f, int i = 0)
        {
            var r = c?.Refs(f);
            return r != null && r.Length > i && db.Collections.TryGetValue(r[i], out var x) ? x : null;
        }

        /// <summary>UpgradeSpecs {RefSpec Item; u8 Level} array (ShiftSND/TurboSND): the last item whose Level ≤ cur.</summary>
        static AttribCollection Upgrade(AttribDatabase db, AttribCollection pv, string f, int cur)
        {
            if (pv == null || !pv.TryGet(f, out int at, out int count, out var def)) return null;
            var d = pv.Source.Data;
            int pick = 0;
            for (int n = 0; n < count; n++)
            {
                if (d[at + n * def.Size + 12] > cur) break;
                pick = n;
            }
            if (count == 0) return null;
            uint cls = Chunks.U32(d, at + pick * def.Size), key = Chunks.U32(d, at + pick * def.Size + 4);
            return db.Collections.TryGetValue((cls, key), out var c) ? c : null;
        }

        /// <summary>EAXCar.cpp UpgradeIntervals.</summary>
        static int UpgradeIntervals(int cur, int phys, int aud)
        {
            int baseUp = Mathf.Clamp(4 - phys, 0, 4), off = Mathf.Clamp(cur, 0, 4), r = 0;
            if (baseUp < 3)
            {
                if (baseUp > 0) r = baseUp + off >= 3 ? 1 : 0;
                else
                {
                    int n = baseUp + off;
                    r = n > 2 ? 2 : n > 0 ? 1 : 0;
                }
            }
            return Mathf.Clamp(r, 0, Mathf.Max(0, aud - 1));
        }

        static float[] Matrix(AttribCollection c, string f, int index = 0)
        {
            if (c == null || !c.TryGet(f, out int at, out int count, out _) || index >= count) return null;
            var m = new float[16];
            for (int i = 0; i < 16; i++) m[i] = Chunks.F32(c.Source.Data, at + index * 64 + i * 4);
            return m;
        }

        static int U(AttribCollection c, string f, int fb) => c != null && c.Has(f) ? c.Int(f) : fb;
        static float F(AttribCollection c, string f, float fb) => c != null && c.Has(f) ? c.Float(f) : fb;

        /// <param name="playerEngine">for AI racers (GenerateUpgradedEngine): the player's engine upgrade level, from which
        /// the AI car's engineaudio interval is taken (offset = player level - base, at least 0)</param>
        /// <param name="engineOverride">an engineaudio collection key replacing the car's own (CSTATEMGR_CarState::ResolveCarBanks
        /// → engineaudio::ChangeWithDefault(mapping.Finish)); 0 = none</param>
        public static CarAudioAttribs For(string carName, OwnedCar levels, int playerEngine = -1, uint engineOverride = 0)
        {
            var a = new CarAudioAttribs();
            var db = GameData.Attributes;
            var pv = db?.Get("pvehicle", carName.ToLowerInvariant());
            if (pv == null) return a;
            int eng = levels?.Engine ?? 0, trans = levels?.Transmission ?? 0, ind = levels?.Induction ?? 0;
            int engUps = pv.Has("engine_upgrades") ? pv.Int("engine_upgrades") : 0;
            if (playerEngine >= 0) eng = Mathf.Max(0, playerEngine - Mathf.Clamp(4 - engUps, 0, 4));
            var ea = Ref(db, pv, "engineaudio", UpgradeIntervals(eng, engUps, pv.Refs("engineaudio").Length));
            if (ea != null && engineOverride != 0 && engineOverride != ea.Key && db.Collections.TryGetValue((ea.ClassKey, engineOverride), out var ov)) ea = ov;
            if (ea != null)
            {
                a.EngineClass = ea.ClassKey;
                a.EngineKey = ea.Key;
                a.GinAccel = ea.StringKey("Filename_GinsuAccel");
                a.GinDecel = ea.StringKey("Filename_GinsuDecel");
                a.EngineBank = ea.StringKey("BankName_mainRAM");
                a.EngineBankAux = ea.StringKey("BankName_auxRAM");
                a.SweetBank = ea.StringKey("SweetBank");
                a.CarId = U(ea, "CarID", 0);
                a.AemsVol = U(ea, "AEMSVol", a.AemsVol);
                a.DecelAemsVol = U(ea, "DECEL_AEMSVol", a.DecelAemsVol);
                a.GinsuAccelVol = U(ea, "GINSUAccelVol", a.GinsuAccelVol);
                a.GinsuDecelVol = U(ea, "GinsuDecelVol", a.GinsuDecelVol);
                a.VolShiftSweets = U(ea, "Vol_ShiftSweets", a.VolShiftSweets);
                a.VolSputters = U(ea, "Vol_Sputters", a.VolSputters);
                if (ea.TryGet("Master_Vol", out int mv, out _, out _)) a.MasterVol = Chunks.U16(ea.Source.Data, mv);
                a.MinRpm = F(ea, "MinRPM", a.MinRpm);
                a.MaxRpm = F(ea, "MaxRPM", a.MaxRpm);
                a.AemsMixL = F(ea, "AEMSMix_L_RPM", a.AemsMixL);
                a.AemsMixS = F(ea, "AEMSMix_S_RPM", a.AemsMixS);
                a.GinsuMixL = F(ea, "GINSUMix_L_RPM", a.GinsuMixL);
                a.GinsuMixS = F(ea, "GINSUMix_S_RPM", a.GinsuMixS);
                a.DecelAemsMixL = F(ea, "DECEL_AEMSMix_L_RPM", a.DecelAemsMixL);
                a.DecelAemsMixS = F(ea, "DECEL_AEMSMix_S_RPM", a.DecelAemsMixS);
                a.DecelGinsuMixL = F(ea, "DECEL_GINSUMix_L_RPM", a.DecelGinsuMixL);
                a.DecelGinsuMixS = F(ea, "DECEL_GINSUMix_S_RPM", a.DecelGinsuMixS);
                a.AccelDeltaThr = Mathf.Max(1, F(ea, "AccelDeltaRPMThreshold", a.AccelDeltaThr));
                a.DecelDeltaThr = Mathf.Max(1, F(ea, "DecelDeltaRPMThreshold", a.DecelDeltaThr));
                a.AclNegL = F(ea, "Ginsu_ACL_Neg_L_RPM", a.AclNegL);
                a.AclNegS = F(ea, "Ginsu_ACL_Neg_S_RPM", a.AclNegS);
                a.DecelFadeIn = F(ea, "GINSU_DECEL_FADE_IN", a.DecelFadeIn);
                a.DecelFadeOut = F(ea, "GINSU_DECEL_FADE_OUT", a.DecelFadeOut);
                a.LowPassCutoff = F(ea, "GINSU_LowPassCutoff", a.LowPassCutoff);
                a.DecelMinRpm = U(ea, "GINSU_Decel_MinRPM", a.DecelMinRpm);
                a.DecelMaxRpm = U(ea, "GINSU_Decel_MaxRPM", a.DecelMaxRpm);
                a.Tranny = ea.Bool("Tranny");
                a.RpmMap = Matrix(ea, "PhysicsRPM_Map");
                var at = Ref(db, ea, "acceltrans");
                if (at != null)
                {
                    a.IdlePeakT = U(at, "AccelFromIdle_PEAK_T", a.IdlePeakT);
                    a.IdleResumeT = U(at, "AccelFromIdle_RESUME_T", a.IdleResumeT);
                    a.IdlePeakRpm = U(at, "AccelFromIdle_PEAK_RPM", a.IdlePeakRpm);
                    a.IdlePeakVol = F(at, "AccelFromIdle_PEAK_VOL", a.IdlePeakVol);
                    a.InterruptT = U(at, "AccelFromIdle_INTERUPT_T", a.InterruptT);
                }
            }
            int transUps = pv.Has("transmission_upgrades") ? pv.Int("transmission_upgrades") : 0;
            var sp = Upgrade(db, pv, "ShiftSND", 4 - transUps + trans);
            if (sp != null)
            {
                a.ShiftBank = sp.StringKey("BankName");
                a.DownShiftSoundDelay = F(sp, "Down_Shift_Sound_Delay", a.DownShiftSoundDelay);
                a.UpShiftSoundDelay = F(sp, "Up_Shift_Sound_Delay", a.UpShiftSoundDelay);
                a.UpEngagingAttackVol = F(sp, "Up_Engaging_Attack_Vol", a.UpEngagingAttackVol);
                a.DownReattachScale = F(sp, "Down_Reattach_Scale", a.DownReattachScale);
                a.LfoRpmAmp = U(sp, "LFO_RPM_Amp", 0); a.LfoVolAmp = U(sp, "LFO_Vol_Amp", 0);
                a.LfoRpmDecay = U(sp, "LFO_RPM_Decay_Time", 0); a.LfoVolDecay = U(sp, "LFO_Vol_Decay_Time", 0);
                a.LfoRpmFreq = U(sp, "LFO_RPM_Freq", 1); a.LfoVolFreq = U(sp, "LFO_Vol_Freq", 1);
                a.DownEngagingRiseT = U(sp, "Down_Engaging_Rise_T", a.DownEngagingRiseT);
                a.UpEngagingAttackT = U(sp, "Up_Engaging_Attack_T", a.UpEngagingAttackT);
                a.DownEngagingFallT = U(sp, "Down_Engaging_Fall_T", a.DownEngagingFallT);
                a.UpVolShift = U(sp, "Up_Vol_Shift", a.UpVolShift);
                a.DownDisengageFallT = U(sp, "Down_Disengage_Fall_T", a.DownDisengageFallT);
                a.DownDisengageFallRpm = U(sp, "Down_Disengage_Fall_RPM", a.DownDisengageFallRpm);
                a.DownVolShift = U(sp, "Down_Vol_Shift", a.DownVolShift);
                a.DownEngagingRiseRpm = U(sp, "Down_Engaging_Rise_RPM", a.DownEngagingRiseRpm);
                a.DownEngagingFallRpm = U(sp, "Down_Engaging_Fall_RPM", a.DownEngagingFallRpm);
                // stShiftPair {Int16 RPM, Int16 Time}
                if (sp.TryGet("Up_DisengageFall", out int fat, out int fc, out _) && fc > 0)
                {
                    a.UpDisengageFall = new (int, int)[fc];
                    a.UpDisengageFallCurve = new float[fc][];
                    for (int i = 0; i < fc; i++)
                    {
                        a.UpDisengageFall[i] = (Chunks.I16(sp.Source.Data, fat + i * 4), Chunks.I16(sp.Source.Data, fat + i * 4 + 2));
                        a.UpDisengageFallCurve[i] = Matrix(sp, "Up_DisengageFall_Curve", i);
                    }
                }
                if (sp.TryGet("Up_Engage", out int eat, out _, out _))
                    a.UpEngage = (Chunks.I16(sp.Source.Data, eat), Chunks.I16(sp.Source.Data, eat + 2));
                a.UpEngageCurve = Matrix(sp, "Up_Engage_Curve");
            }
            if (_sharedTurbo != null)
            {
                a.HasTurbo = _sharedTurbo.HasTurbo; a.TurboBank = _sharedTurbo.TurboBank; a.VolSpool = _sharedTurbo.VolSpool;
                a.VolBlowoff1 = _sharedTurbo.VolBlowoff1; a.VolBlowoff2 = _sharedTurbo.VolBlowoff2; a.ChargeTime = _sharedTurbo.ChargeTime;
                a.LeakRate = _sharedTurbo.LeakRate;
                return a;
            }
            _sharedTurbo = a;
            int indUps = pv.Has("induction_upgrades") ? pv.Int("induction_upgrades") : 0;
            var ts = Upgrade(db, pv, "TurboSND", 4 - indUps + ind);
            if (ts != null && ts.Name != "default")
            {
                a.HasTurbo = true;
                a.TurboBank = ts.StringKey("BankName");
                a.VolSpool = U(ts, "Vol_Spool", a.VolSpool);
                a.VolBlowoff1 = U(ts, "Vol_Blowoff1", a.VolBlowoff1);
                a.VolBlowoff2 = U(ts, "Vol_Blowoff2", a.VolBlowoff2);
                a.ChargeTime = Mathf.Max(0.1f, F(ts, "ChargeTime", a.ChargeTime));
                a.LeakRate = F(ts, "Leak_Rate", a.LeakRate);
            }
            return a;
        }
    }

    /// <summary>
    /// The engine/transmission sound controllers of one car (EAXCar's SFXCTLs), stepped at 60 Hz because the game's
    /// smoothing constants are per audio frame:
    /// <list type="bullet">
    /// <item>SFXCTL_Physics (sfxctl/SFXCTL_Physics.cpp): rpm % remapped through engineaudio PhysicsRPM_Map (Bezier y),
    /// to the 1000..10000 audio rpm space; throttle &gt; 30 % = accelerating; torque = throttle.</item>
    /// <item>SFXCTL_AccelTrans: a 1000 rpm / 0.8 vol "attack" on stepping on the throttle above 3000 rpm in 2nd+, a rev
    /// flare from idle in 1st (acceltrans PEAK/RESUME), interrupted when lifting.</item>
    /// <item>SFXCTL_Shifting: up-shift disengage fall along the shiftpattern Bezier stages, engage, post-shift RPM/volume
    /// LFO; down-shift fall/blip/fall/re-attach; brake-mash and reverse-whine flags.</item>
    /// <item>SFXCTL_Engine: clutch model below 2500 rpm, the shift LFOs, idle "compression" wobble, redlining at 9800
    /// audio rpm (engine/redline-sample crossfade), distance low-pass.</item>
    /// <item>SFXCTL_HybridMotor: rpm-delta average → accel/decel thresholds → engineaudio mix of AEMS, accel Ginsu and
    /// decel Ginsu (UpdateDualMixEng; PC players use CARSFX_DualGinsuEng, EnterWorld SFXIDs 0x53BFC @0x4F2E90).</item>
    /// </list>
    /// </summary>
    public sealed class CarSoundModel
    {
        public readonly CarAudioAttribs A;
        public readonly int State, Inst;
        public readonly bool Player, Dual;
        public SndObj Phys, Shift, AccelT, Engine, Hybrid, Tunnel;
        public SndPos CarPos, RearPos, RWheel, LWheel, LWind, RWind;

        // physics
        public int CurGear = 1, LastGear = 1;   // Gear enum: 0 reverse, 1 neutral, 2 first ...
        public float Throttle, OldThrottle, PhysTrq, PhysRpm = 1000f;
        public bool Accelerating;
        /// <summary>SFXCTL_NISReving state of this car's physics controller (<see cref="NisRevState"/>).</summary>
        public readonly NisRevState Nis = new NisRevState();
        float _setRpm = 1000f, _setTrq, _setThrottle;   // the values last handed to the EAXCar (SetPhysRPM/TRQ/Throttle)
        bool _setAccel;
        // engine
        public float EngRpm = 1000f, PrevEngRpm = 1000f, EngTrq;
        float _volLfo, _rpmLfo, _trqLfo, _aRpm, _aVol, _aTrq;
        public int EngineVol = 0x7FFF;
        public bool Redlining, WasRedlining;
        readonly InterpLine _redEng = new InterpLine(), _redSamp = new InterpLine();
        public float RedEng => _redEng.Value;
        public float RedSamp => _redSamp.Value;
        readonly RunningAverage _trqAvg = new RunningAverage(3);
        bool _clutch, _playCompression = true;
        readonly PathLine _compression = new PathLine();
        public int DistanceFilter = 24000;
        bool _preRace;
        float _mergeT, _redOffset;
        bool _redBounce;
        readonly RunningAverage _visAvg = new RunningAverage(2);
        static CarSpec spec(PlayerCar c) => c ? c.Spec : null;
        // accel trans
        public int AccelState;   // FX_ACCEL_STATE
        readonly InterpLine _atRpm = new InterpLine(), _atVol = new InterpLine(), _atTrq = new InterpLine();
        float _tLastAccelTrans = -10f;
        bool _atAcc, _atOldAcc;
        // shifting
        public int ShiftState, ShiftStageChanged;  // SHIFT_STAGE
        public bool NeedShiftGearSnd, NeedDisengageSnd, NeedEngageSnd, NeedAccelSnd, NeedDecelSnd, Whining, BrakeMashed;
        public bool Upshift;
        public float RpmAtShift;
        readonly InterpLine _shTrq = new InterpLine(), _shRpm = new InterpLine(), _shVol = new InterpLine(), _visual = new InterpLine();
        readonly InterpLine _lfoRpmDecay = new InterpLine(), _lfoVolDecay = new InterpLine();
        bool _lfoOn, _pendingShiftSnd;
        int _lfoRpmAmp, _lfoRpmFrq, _lfoVolAmp, _lfoVolFrq;
        float _shiftDelay, _tCurStage, _tLastMashed = -10f, _prevBrake;
        int _curStage;
        int _rpmOffset;
        readonly Graph _rpmGraph = new Graph(7);
        // hybrid
        readonly RunningAverage _avgDelta = new RunningAverage(4);
        float _prevPhysRpmForDelta, _curPhyDelta, _curAudDelta;
        public float GinsuScaledRpm, GinsuLpf = 24000f;
        public int EngVolAems, EngVolAccelGinsu, EngVolDecelGinsu, EngVolRedLine;
        float _mixAems, _mixAcc, _mixDec, _mixCut = 24000f;
        float _tSteady;
        int _steadyFrames, _prevDeltaOut;
        readonly Graph _decelXfade = new Graph(5);

        public float Time { get; private set; }
        float _acc;
        const float Step = 1f / 60f;

        public CarSoundModel(CarAudioAttribs a, int state, int inst, bool player, bool dual)
        {
            A = a;
            State = state;
            Inst = inst;
            Player = player;
            Dual = dual;
            var map = SndMixer.Instance.Map;
            SndObj Ctl(int o) => new SndObj(map, MixMap.ObjectId(true, SndMixer.StateOr(state, inst), Mathf.Max(0, inst), o));
            Phys = Ctl(0); Shift = Ctl(2); AccelT = Ctl(3); Engine = Ctl(4); Hybrid = Ctl(5); Tunnel = Ctl(6);
            CarPos = new SndPos(Ctl(7)); RearPos = new SndPos(Ctl(8)); RWheel = new SndPos(Ctl(11)); LWheel = new SndPos(Ctl(12));
            LWind = new SndPos(Ctl(13)); RWind = new SndPos(Ctl(14));
            _redEng.Initialize(1f, 1f, 1);
            _redSamp.Initialize(0f, 0f, 1);
            _compression.Initialize(0f, 0f, 1);
            _avgDelta.Flush(0);
            _trqAvg.Flush(0);
            // SetupSFX: decel Ginsu crossfade over its recorded range
            int span = a.DecelMaxRpm - a.DecelMinRpm;
            _decelXfade.X[1] = a.DecelMinRpm; _decelXfade.Y[1] = 0;
            _decelXfade.X[2] = a.DecelMinRpm + span * a.DecelFadeOut; _decelXfade.Y[2] = 1;
            _decelXfade.X[3] = a.DecelMaxRpm - span * a.DecelFadeIn; _decelXfade.Y[3] = 1;
            _decelXfade.X[4] = a.DecelMaxRpm; _decelXfade.Y[4] = 0;
            // SFXCTL_Engine::InitSFX: engine upgrade level 2 by default (m_nTrueEngineUpgradeLevel)
            Engine.Set(0, UpgradeDmix(a.EngineUgl));
            var race = RaceManager.Active;
            _preRace = player && race != null && race.Countdown;
            if (state == MixMap.AiRaceCar) Ai = new AiPhysicsModel();
            _deltaDesired.Flush(0f);
        }

        public static int GearOf(int carGear) => carGear < 0 ? 0 : carGear + 1;

        /// <summary>SFXCTL_Engine::InitSFX: DMIX input 0 by the AEMS engine upgrade level 0..3 (0, 10922, 21844, 0x7FFF).</summary>
        static int UpgradeDmix(int ugl) => ugl <= 0 ? 0 : ugl == 1 ? 10922 : ugl == 2 ? 21844 : 0x7FFF;

        /// <summary>EAXCar::SetFinalAudioRPM (SFXCTL_Engine::UpdateRPM): the rpm fraction the tachometer shows (the
        /// physics rpm before the race starts, merged back over 0.7 s after the countdown) [exposed for the HUD owner].</summary>
        public float FinalAudioRpm { get; private set; }

        public void Tick(PlayerCar car, float dt)
        {
            NeedShiftGearSnd = NeedDisengageSnd = NeedEngageSnd = NeedAccelSnd = NeedDecelSnd = false;
            if (dt <= 0 || !car) return;
            _acc += dt;
            int n = 0;
            while (_acc >= Step && n < 6) { _acc -= Step; n++; StepOnce(car, Step); }
            if (n == 6) _acc = 0;
        }

        /// <summary>SFXCTL_AIPhysics for AI racers (AIRACECAR state: creatable 0x30000), null otherwise.</summary>
        public readonly AiPhysicsModel Ai;
        float _tHoldDecel, _prevThrottleIn;   // _prevThrottleIn = m_OldDesiredSpeed
        NfsMw.Game.Police.CopCar _cop;
        readonly RunningAverage _deltaDesired = new RunningAverage(2);

        void StepOnce(PlayerCar car, float t)
        {
            Time += t;
            var spec = car.Spec;
            LastGear = CurGear;
            OldThrottle = Throttle;
            if (Ai != null)
            {
                // ---- SFXCTL_AIPhysics::UpdateParams (PC @0x4C8B40)
                Ai.Step(car, this, Time, t);
                CurGear = Ai.Gear;
                LastGear = Ai.LastGear;
                Accelerating = Ai.Accelerating;
                Throttle = Accelerating ? 100f : 0f;
                PhysRpm = Ai.Rpm;
                PhysTrq = Ai.Trq;
            }
            else
            {
                // ---- SFXCTL_Physics::UpdateParams (sfxctl/SFXCTL_Physics.cpp)
                CurGear = GearOf(car.Gear);
                if (car.AiControlled)
                {
                    // mControlSource == CONTROL_AI (cops): the frame delta of the AI's mDesiredSpeed averaged over 2; the
                    // throttle chases 100 while it is above DesiredVelDifThresh (-1) and no decel hold runs, else 0 (held
                    // HoldDecelDuration 0.5 s after a drop), by 50 a frame. mDesiredSpeed = the cop's drive speed (CopCar.DriveSpeed)
                    if (!_cop) _cop = car.GetComponent<NfsMw.Game.Police.CopCar>();
                    float desired = _cop ? _cop.DriveSpeed : car.SpeedMps;
                    _deltaDesired.Record(desired - _prevThrottleIn);
                    _prevThrottleIn = desired;
                    _deltaDesired.Recalculate();
                    if (_deltaDesired.Value > -1f && _tHoldDecel < 0f) Throttle = SndCurves.Smooth(Throttle, 100f, 50f);
                    else
                    {
                        if (_deltaDesired.Value < 0f) _tHoldDecel = 0.5f;
                        _tHoldDecel -= t;
                        Throttle = SndCurves.Smooth(Throttle, 0f, 50f);
                    }
                }
                else Throttle = Mathf.Clamp01(car.Input.Throttle) * 100f;
                if (Throttle > 30f) { if (!Accelerating) Accelerating = true; }
                else if (Accelerating) Accelerating = false;
                PhysTrq = SndCurves.Smooth(PhysTrq, Throttle, 100f);
                float idle = spec != null ? spec.IdleRpm : 800f, red = spec != null ? spec.RedLine : 7000f;
                float pct = red > idle ? Mathf.Clamp01((car.Rpm - idle) / (red - idle)) : 0f;
                float remap = A.RpmMap != null && Player ? SndCurves.Bezier(A.RpmMap, 0, pct).y : pct;
                PhysRpm = remap * 9000f + 1000f;
                if (car.Totaled || car.EngineBlown) PhysRpm = 1000f;
            }
            // ---- SFXCTL_Physics/AIPhysics::UpdateParams tail: while a NIS plays UpdateNIS(scene time, 0); AI racers during the
            // 3-2-1 (GameFlowSndState[3]) UpdateNIS(0, t). An early return leaves the car's values as last set.
            bool nis = NisRevManager.NisPlaying, rev321 = !nis && NisRevManager.Rev321 && State == MixMap.AiRaceCar;
            if (nis || rev321)
            {
                float rpm = PhysRpm;
                var np = NisPlayer.Active;
                if (Nis.Update(nis && np ? np.SceneTime : 0f, nis ? 0f : t, NisRevManager.CarIdOf(car), NisRevManager.Data, NisRevManager.CountdownLeft, _setRpm, ref rpm, out float trq))
                {
                    PhysRpm = rpm;
                    PhysTrq = trq;
                    Throttle = trq;
                    Accelerating = trq > 30f;
                }
                else { PhysRpm = _setRpm; PhysTrq = _setTrq; Throttle = _setThrottle; Accelerating = _setAccel; }
            }
            _setRpm = PhysRpm; _setTrq = PhysTrq; _setThrottle = Throttle; _setAccel = Accelerating;
            // ---- controllers in SFX_ID order: physics, shifting, accel trans, engine, hybrid
            UpdateShifting(car, t);
            UpdateAccelTrans(t);
            UpdateEngine(car, t);
            UpdateHybrid(car, t);
            MixerOutputs(car);
        }

        // ---------------------------------------------------------------- SFXCTL_Shifting
        bool ShiftActive => ShiftState != 0;
        bool IsDownShifting => ShiftState >= 4 && ShiftState <= 7;
        bool IsUpShifting => ShiftState >= 1 && ShiftState <= 3;

        void FillGraph(float[] m, int time, int rpm)
        {
            for (int i = 0; i < 7; i++)
            {
                float u = i / 6f;
                var (x, y) = m != null ? SndCurves.Bezier(m, 0, u) : (u, u);
                _rpmGraph.X[i] = x * time;
                _rpmGraph.Y[i] = y * rpm;
            }
        }

        void UpdateShifting(PlayerCar car, float t)
        {
            if (CurGear > LastGear) BeginUpShift();
            else if (CurGear < LastGear) BeginDownShift();
            if (Time > _shiftDelay && _pendingShiftSnd) { NeedShiftGearSnd = true; _pendingShiftSnd = false; }
            Whining = CurGear == 0;
            if (Player)
            {
                float brake = car.Input.Brake;
                if (brake >= 1f && !BrakeMashed && Time - _tLastMashed > 1f && _prevBrake == 0f && car.SpeedMps * 2.23699f > 5f) BrakeMashed = true;
                else if (BrakeMashed)
                {
                    _tLastMashed = Time;
                    if (brake == 0f) BrakeMashed = false;
                }
                _prevBrake = brake;
            }
            ShiftStageChanged = 0;
            if (ShiftState == 0) return;
            // UpdateRPM / UpdateTorque
            if (!_visual.Finished) _visual.Update(t, PhysRpm);
            switch (ShiftState)
            {
                case 1: case 2: case 4: case 5: case 6: _shRpm.Update(t); break;
                case 3: case 7: _shRpm.Update(t, PhysRpm); break;
            }
            switch (ShiftState)
            {
                case 1: case 4: case 5: case 6: _shTrq.Update(t); break;
                case 2: case 3: case 7: _shTrq.Update(t, PhysTrq); break;
            }
            _shVol.Update(t);
            PostShiftLfo(t);
            _tCurStage += t * 1000f;
            switch (ShiftState)
            {
                case 1: // SHFT_UP_DISENGAGE
                    if (_tCurStage > _rpmGraph.X[6])
                    {
                        _curStage++;
                        if (_curStage < A.UpDisengageFall.Length)
                        {
                            var st = A.UpDisengageFall[_curStage];
                            FillGraph(A.UpDisengageFallCurve[_curStage], st.time, st.rpm);
                            _tCurStage = t * 1000f;
                            _rpmOffset = (int)_shRpm.Value;
                            float r = Mathf.Clamp(_rpmOffset + _rpmGraph.Value(_tCurStage), 1000f, 10000f);
                            _shRpm.Initialize(r, r, 0);
                            break;
                        }
                        ShiftState = ShiftStageChanged = 2;
                        FillGraph(A.UpEngageCurve, A.UpEngage.time, A.UpEngage.rpm);
                        _tCurStage = 0;
                        _rpmOffset = (int)PhysRpm;
                        float r2 = Mathf.Clamp(_rpmOffset + _rpmGraph.Value(_tCurStage), 1000f, 10000f);
                        _shRpm.Initialize(r2, r2, 0);
                        _shTrq.Initialize(PhysTrq, PhysTrq, 0);
                        _shVol.Initialize(A.UpEngagingAttackVol, 0f, A.UpEngagingAttackT);
                        PostShiftLfoInit();
                        PostShiftLfo(t);
                        NeedEngageSnd = true;
                        return;
                    }
                    else
                    {
                        float r = Mathf.Clamp(_rpmOffset + _rpmGraph.Value(_tCurStage), 1000f, 10000f);
                        _shRpm.Initialize(r, r, 0);
                    }
                    break;
                case 2: // SHFT_UP_ENGAGING
                    if (_tCurStage > _rpmGraph.X[6]) { ShiftState = ShiftStageChanged = 3; return; }
                    _rpmOffset = (int)PhysRpm;
                    {
                        float r = Mathf.Clamp(_rpmOffset + _rpmGraph.Value(_tCurStage), 1000f, 10000f);
                        _shRpm.Initialize(r, r, 0);
                    }
                    break;
                case 3: // SHFT_UP_LFO
                    if ((!_lfoRpmDecay.Finished || !_lfoVolDecay.Finished) && _lfoOn) return;
                    CleanUpShift();
                    break;
                case 4: // SHFT_DOWN_DISENGAGE
                    if (_shRpm.Finished)
                    {
                        ShiftState = ShiftStageChanged = 5;
                        int len = A.DownEngagingRiseT;
                        if (CurGear < 3) len = (int)(len * 0.7f);
                        float low = EngRpm < 1500f ? 0f : 1f;
                        float target = Mathf.Clamp(PhysRpm + A.DownEngagingRiseRpm * low, 1000f, 10000f);
                        _shRpm.Initialize(EngRpm, target, len, SndCurve.EqPwrSq);
                        _shTrq.Initialize(EngTrq, 100f * low, 215);
                    }
                    break;
                case 5: // SHFT_DOWN_ENGAGING_RISE
                    if (_shRpm.Finished)
                    {
                        ShiftState = ShiftStageChanged = 6;
                        int len = A.DownEngagingFallT;
                        if (CurGear < 3) len = (int)(len * 0.7f);
                        float low = EngRpm < 1500f ? 0f : 1f;
                        _shRpm.Initialize(EngRpm, EngRpm - A.DownEngagingFallRpm * low, len);
                        _shTrq.Initialize(100f * low, 0f, 215);
                    }
                    break;
                case 6: // SHFT_DOWN_ENGAGING_FALL
                    if (_shRpm.Finished)
                    {
                        ShiftState = ShiftStageChanged = 7;
                        float at = Mathf.Clamp(Mathf.Abs((EngRpm - PhysRpm) * A.DownReattachScale), 0f, 800f);
                        _shRpm.Initialize(EngRpm, PhysRpm, (int)at, SndCurve.EqPwrSq);
                        _shTrq.Initialize(0f, PhysTrq, 60);
                    }
                    break;
                case 7:
                    if (_shRpm.Finished) CleanUpShift();
                    break;
            }
        }

        void BeginUpShift()
        {
            CleanUpShift();
            if (PhysRpm < 3000f) return;
            ShiftState = ShiftStageChanged = 1;
            Upshift = true;
            RpmAtShift = PrevEngRpm;
            SetEngRpm(RpmAtShift);
            var st = A.UpDisengageFall[0];
            FillGraph(A.UpDisengageFallCurve[0], st.time, st.rpm);
            _curStage = 0;
            _tCurStage = 0;
            _rpmOffset = (int)RpmAtShift;
            _shRpm.Initialize(RpmAtShift, RpmAtShift, 0);
            _shVol.Initialize(0f, 0f, 1);
            _shTrq.Initialize(PhysTrq, 0f, 100);
            float total = A.UpDisengageFall.Length == 1 ? A.UpDisengageFall[0].time : A.UpDisengageFall[0].time + A.UpDisengageFall[1].time;
            total += A.UpEngage.time;
            _visual.Initialize(RpmAtShift, RpmAtShift - 1200f, (int)total, SndCurve.EqPwrSq);
            _shiftDelay = Time + A.UpShiftSoundDelay;
            _pendingShiftSnd = true;
            NeedDisengageSnd = true;
        }

        void BeginDownShift()
        {
            if (CurGear == 1) return;
            CleanUpShift();
            ShiftState = ShiftStageChanged = 4;
            Upshift = false;
            RpmAtShift = PrevEngRpm;
            SetEngRpm(RpmAtShift);
            _shTrq.Initialize(PhysTrq, 0f, 0x32);
            float target = Mathf.Clamp(RpmAtShift - A.DownDisengageFallRpm, 1000f, 10000f);
            _shRpm.Initialize(RpmAtShift, target, A.DownDisengageFallT);
            _shVol.Initialize(0f, 0f, 1);
            _shiftDelay = Time + A.DownShiftSoundDelay;
            if (PhysRpm > 3000f) _pendingShiftSnd = true;
        }

        void PostShiftLfoInit()
        {
            if (CurGear >= 7) return;   // SIXTH_GEAR
            float scale = CurGear == 3 ? 0.85f : CurGear == 4 ? 0.7f : CurGear == 5 ? 0.55f : CurGear == 6 ? 0.3f : 1f;
            _lfoOn = true;
            _lfoRpmDecay.Initialize(A.LfoRpmAmp * scale, 0f, A.LfoRpmDecay);
            _lfoVolDecay.Initialize(A.LfoVolAmp, 0f, A.LfoVolDecay);
        }

        void PostShiftLfo(float t)
        {
            if (!_lfoOn) return;
            _lfoRpmDecay.Update(t);
            _lfoVolDecay.Update(t);
            if (_lfoRpmDecay.Finished) { CleanUpShift(); return; }
            _lfoRpmAmp = (int)_lfoRpmDecay.Value;
            _lfoRpmFrq = A.LfoRpmFreq;
            _lfoVolAmp = (int)_lfoVolDecay.Value;
            _lfoVolFrq = A.LfoVolFreq;
        }

        void CleanUpShift()
        {
            _lfoOn = false;
            _lfoRpmAmp = _lfoRpmFrq = _lfoVolAmp = _lfoVolFrq = 0;
            ShiftState = ShiftStageChanged = 0;
        }

        // ---------------------------------------------------------------- SFXCTL_AccelTrans
        void UpdateAccelTrans(float t)
        {
            if (State == MixMap.AiRaceCar) return;
            _atOldAcc = _atAcc;
            _atAcc = Accelerating;
            if (_atAcc && !_atOldAcc)
            {
                if (ShouldIdle()) BeginIdle();
                else if (ShouldAttack()) BeginAttack();
            }
            if (!_atAcc && _atOldAcc && AccelState == 0 && !ShiftActive && PhysRpm >= 6000f && CurGear >= 3) NeedDecelSnd = true;
            _atVol.Update(t);
            switch (AccelState)
            {
                case 2: _atRpm.Update(t); break;
                case 1: case 3: case 4: _atRpm.Update(t, PhysRpm); break;
            }
            if (AccelState != 0)
            {
                _atTrq.Update(t, PhysTrq);
                if (_atTrq.Finished) _atTrq.Initialize(PhysTrq, PhysTrq, 0);
                if (!Accelerating && AccelState != 4)
                {
                    _atRpm.Initialize(EngRpm, PhysRpm, A.InterruptT, SndCurve.EqPwrSq);
                    AccelState = 4;
                    _atVol.Initialize(_atVol.Value, 0f, A.InterruptT);
                }
                switch (AccelState)
                {
                    case 2:
                        if (_atRpm.Finished)
                        {
                            AccelState = 3;
                            _atRpm.Initialize(_atRpm.Value, PhysRpm, A.IdleResumeT);
                            _atVol.Initialize(_atVol.Value, 0f, A.IdleResumeT);
                        }
                        break;
                    case 1: case 3: case 4:
                        if (_atRpm.Finished) AccelState = 0;
                        break;
                }
            }
        }

        /// <summary>ShouldBeginAccelTrans_Idle (PC @0x4BCF70): ≤ 15 mph, throttle up ≥ 30 in a frame, no NIS reving (SFXCTL_Physics
        /// NISRevingEnabled: only ever cleared, SFXCTL_Physics.cpp:31), idle, not shifting, 1st gear, ≤ 1500 rpm, no NIS playing.</summary>
        bool ShouldIdle() => Speed <= 15f && Throttle - OldThrottle >= 30f && AccelState == 0 && !ShiftActive && CurGear == 2 && PhysRpm <= 1500f
            && !NisPlayer.Active;
        /// <summary>ShouldBeginAccelTrans (PC @0x4B4900): throttle up ≥ 30, idle, not shifting, ≥ 3000 rpm, 2 s since the last one,
        /// and 2nd gear or better unless the car IsAICar (every non-player state: AI racers never reach here, cops do).</summary>
        bool ShouldAttack() => Throttle - OldThrottle >= 30f && AccelState == 0 && !ShiftActive && PhysRpm >= 3000f && Time - _tLastAccelTrans >= 2f && (State != MixMap.PlayerCar || CurGear >= 3);
        float Speed { get; set; }

        void BeginAttack()
        {
            _atRpm.Initialize(PhysRpm + 1000f, PhysRpm, 500, SndCurve.EqPwrSq);
            AccelState = 1;
            _atVol.Initialize(0.8f, 0f, 200);
            NeedAccelSnd = true;
            _atTrq.Initialize(100f, 100f, 10);
            _tLastAccelTrans = Time;
        }

        void BeginIdle()
        {
            AccelState = 2;
            _tLastAccelTrans = Time;
            _atRpm.Initialize(PhysRpm, PhysRpm + A.IdlePeakRpm, A.IdlePeakT);
            _atVol.Initialize(0f, A.IdlePeakVol, A.IdlePeakT);
            _atTrq.Initialize(PhysTrq, PhysTrq, 10);
        }

        // ---------------------------------------------------------------- SFXCTL_Engine
        void SetEngRpm(float r) { PrevEngRpm = EngRpm; EngRpm = r; }

        bool _engineBlownSent, _totalledSent;
        float _baseRpm;

        void UpdateEngine(PlayerCar car, float t)
        {
            Speed = car.SpeedMps * 2.23699f;
            // SFXCTL_Engine::UpdateParams: a blown or sabotaged engine starts its moment stream once (blownengine /
            // engine_breakdown, at the listener); MessageVehicleDestroyed: the player's car totalled → totalled at the car
            bool sabotaged = car.PowerScale <= 0f;
            if ((car.EngineBlown || sabotaged) && !_engineBlownSent)
            {
                _engineBlownSent = true;
                MomentStream.Send(sabotaged ? MomentStream.KeyEngineBreakdown : MomentStream.KeyBlownEngine, Vector3.zero);
            }
            if (Player && car.Totaled && !_totalledSent)
            {
                _totalledSent = true;
                var at = car.transform.position;
                MomentStream.Send(MomentStream.KeyTotalled, at.sqrMagnitude < 1f ? Vector3.one : at);   // (0,0,0) means "at the listener"
            }
            if (!car.Totaled) _totalledSent = false;
            Engine.Set(2, A.MasterVol);
            _clutch = Player && AccelState == 0 && EngRpm <= 2500f;
            // LFOs
            int vAmp = 0, vFrq = 0, tAmp = 0, tFrq = 0, rAmp = 0, rFrq = 0;
            if (ShiftActive) { vAmp = _lfoVolAmp; vFrq = _lfoVolFrq; rAmp = _lfoRpmAmp; rFrq = _lfoRpmFrq; }
            else { _volLfo = _rpmLfo = _trqLfo = 0; _aVol = _aRpm = _aTrq = 16535f / 65536f; }
            rFrq = Mathf.Clamp(rFrq, 1, 10000); tFrq = Mathf.Clamp(tFrq, 1, 10000); vFrq = Mathf.Clamp(vFrq, 1, 10000);
            if (rAmp != 0) { _aRpm = Mathf.Repeat(_aRpm + t / (rFrq / 1000f), 1f); _rpmLfo = rAmp * Mathf.Sin(_aRpm * Mathf.PI * 2f); }
            if (tAmp != 0) { _aTrq = Mathf.Repeat(_aTrq + t / (tFrq / 1000f), 1f); _trqLfo = tAmp * Mathf.Sin(_aTrq * Mathf.PI * 2f); }
            if (vAmp != 0) { _aVol = Mathf.Repeat(_aVol + t / (vFrq / 1000f), 1f); _volLfo = vAmp * Mathf.Sin(_aVol * Mathf.PI * 2f); }
            // compression wobble
            Engine.Set(1, 0);
            _compression.Update(t);
            if (_playCompression)
            {
                int dur = Random.Range(0, 100) + 25;
                float d = Random.Range(0f, 100f) + 25f;
                _compression.Clear();
                _compression.AddStage(0f, d, dur, SndCurve.EqPwrSq);
                _compression.AddStage(d, 0f, dur, SndCurve.EqPwrSq);
                _playCompression = false;
                Engine.Set(1, 0x7FFF);
            }
            // UpdateRPM
            float cur;
            if (ShiftActive) cur = _shRpm.Value;
            else if (AccelState != 0) cur = _atRpm.Value;
            else cur = PhysRpm;
            if (_clutch && !ShiftActive) cur = SndCurves.Smooth(EngRpm, PhysRpm, 999f, 60f);
            _baseRpm = cur;
            float normal = cur + _rpmLfo + _compression.Value;
            SetEngRpm(normal + _rpmLfo);
            // UpdateTorque
            if (ShiftActive) _trqAvg.Flush(_shTrq.Value);
            else if (AccelState != 0) _trqAvg.Flush(_atTrq.Value);
            else _trqAvg.Record(PhysTrq);
            _trqAvg.Recalculate();
            EngTrq = _trqAvg.Value + _trqLfo;
            // UpdateVolume
            EngineVol = 0x7FFF;
            if (ShiftActive) EngineVol += (int)(EngineVol * _shVol.Value);
            EngineVol += (int)_volLfo;
            // UpdateFilterFX: distance low-pass from the 3D car position's camera distance
            float fdist = CarPos.Obj.Get(1) * 0.01f;
            float k = Mathf.Clamp01((fdist - 6.5f) / 44f);
            int db = MixShape.Curve(MixShape.DwnOneMinEqPwrSq, (int)(k * 32766f), true);
            DistanceFilter = Mathf.Clamp((int)(MixShape.Q15FromDb(db) * 0.7103183f + 725f), 0, 0x7FFF);
            // UpdateRedlining (not for AI racers)
            WasRedlining = Redlining;
            if (State != MixMap.AiRaceCar)
            {
                if (!Redlining)
                {
                    // PC UpdateRedlining (@0x4BC6A0) also needs EAXCar::CurGear != EAX_CarState+0x1FC (the transmission's
                    // GEAR_RATIO count - 1 = the top gear, set in the EAX_CarState ctor @0x4C9ED1 [?: attribute 0x07A7A3E5]):
                    // no redline in top gear (the GC decomp's GetTopGear is a SEVENTH_GEAR stub and is not tested)
                    var gr = car.Spec != null ? car.Spec.GearRatios : null;
                    int topGear = gr != null && gr.Length > 0 ? gr.Length - 1 : 8;
                    if (!ShiftActive && CurGear != topGear && EngRpm > 9800f)
                    {
                        Redlining = true;
                        float scale = new Slope(1f, 5f, 1f, 5f).Value(CurGear);
                        _redEng.Initialize(_redEng.Value, 0.15f, (int)(450f * _redEng.Value * scale));
                        _redSamp.Initialize(_redSamp.Value, 0.85f, (int)(120f * (1f - _redSamp.Value) * scale));
                    }
                }
                else if (EngRpm < 9800f || ShiftActive)
                {
                    Redlining = false;
                    _redEng.Initialize(_redEng.Value, 1f, (int)(50f * (1f - _redEng.Value)));
                    _redSamp.Initialize(_redSamp.Value, 0f, (int)(50f * _redSamp.Value));
                }
            }
            _redEng.Update(t);
            _redSamp.Update(t);
            var race = RaceManager.Active;
            if (_preRace && (race == null || !race.Countdown)) { _preRace = false; _mergeT = 0.7f; }   // MsgCountdownDone
            // the tachometer's rpm (UpdateRPM's VisualRPM → VisRpmAvg(2) → SetFinalAudioRPM)
            // UpdateRPM (@0x4BC870): VisualRPM defaults to the base rpm before the LFOs and the compression (local_8), not EngRpm
            float visual = ShiftActive && (ShiftState == 1 || ShiftState == 2) ? _visual.Value : AccelState == 1 ? PhysRpm : _baseRpm;
            // the exe's UpdateRPM runs before UpdateRedlining (UpdateParams @0x4BC420): it sees last frame's bIsRedlining
            if (WasRedlining && !(ShiftActive && (ShiftState == 1 || ShiftState == 2)) && AccelState != 1)
            {
                if (_redBounce) { _redOffset = SndCurves.Smooth(_redOffset, 200f, 50f); if (_redOffset == 200f) _redBounce = false; }
                else { _redOffset = SndCurves.Smooth(_redOffset, 0f, 50f); if (_redOffset == 0f) _redBounce = true; }
                visual -= _redOffset;
            }
            if (!WasRedlining && Redlining) { _redBounce = true; _redOffset = 0f; }
            _visAvg.Record(visual);
            _visAvg.Recalculate();
            float pct = (_visAvg.Value - 1000f) / 9000f;
            if (Player && spec(car) is var sp && sp != null)
            {
                float phys = sp.RedLine > sp.IdleRpm ? Mathf.Clamp01((car.Rpm - sp.IdleRpm) / (sp.RedLine - sp.IdleRpm)) : 0f;
                if (_preRace) pct = phys;
                else if (_mergeT > 0f)
                {
                    _mergeT = Mathf.Max(0f, _mergeT - t);
                    pct = (pct - phys) * ((0.7f - _mergeT) * 1.4285715f) + phys;
                }
            }
            FinalAudioRpm = pct;
        }

        // ---------------------------------------------------------------- SFXCTL_HybridMotor
        void UpdateHybrid(PlayerCar car, float t)
        {
            // UpdateDeltaRPM
            _curPhyDelta = PhysRpm - _prevPhysRpmForDelta;
            _curAudDelta = EngRpm - PrevEngRpm;
            _prevPhysRpmForDelta = PhysRpm;
            if (AccelState != 0) _avgDelta.Flush(AccelState == 1 ? _curPhyDelta : _curAudDelta);
            else if (ShiftActive)
            {
                if (ShiftState == 5) _avgDelta.Flush(_curAudDelta);
                else if (ShiftStageChanged == 2) _avgDelta.Flush(Mathf.Abs(_curPhyDelta));
                else if (ShiftState == 2 || ShiftState == 3) _avgDelta.Record(Mathf.Abs(_curAudDelta));
                else _avgDelta.Record(_curAudDelta);
            }
            else _avgDelta.Record(_curPhyDelta);
            _avgDelta.Recalculate();

            float aems = Mathf.Clamp(EngRpm, 1000f, 10000f);
            GinsuScaledRpm = (aems - 1000f) / 9000f * (A.MaxRpm - A.MinRpm) + A.MinRpm;
            float accelDecel = new Slope(0f, 1f, 0f, 35f).Value(EngTrq);
            float delta = _avgDelta.Value;
            float pAcc = Mathf.Clamp01(1f - (A.AccelDeltaThr - Mathf.Abs(delta + 10f)) / A.AccelDeltaThr);
            float pDec = Mathf.Clamp01(1f - (A.DecelDeltaThr - Mathf.Abs(delta + 10f)) / A.DecelDeltaThr);
            bool smoothing = true;
            // UpdateSingle/DualMixEng (PC @0x4BD160 / @0x4BD650): while EAX_CarState::IsShifting (+0x1EC, the driveline's shift
            // flag = PlayerCar.IsShifting, EngineRacer::IsShiftingGear) or the shift controller runs:
            // up-engaging (2), or down-reattach (7) while accelerating, set the mix without smoothing; else an accel-trans attack does
            if (ShiftActive || car.IsShifting) { if (ShiftState == 2 || (ShiftState == 7 && Accelerating)) smoothing = false; }
            else if (AccelState == 1) smoothing = false;
            float accAems = pAcc * (A.AemsMixL - A.AemsMixS) + A.AemsMixS;
            float accGinsu = pAcc * (A.GinsuMixL - A.GinsuMixS) + A.GinsuMixS;
            float decAems = pDec * (A.DecelAemsMixL - A.DecelAemsMixS) + A.DecelAemsMixS;
            float decGinsu = pDec * (A.DecelGinsuMixL - A.DecelGinsuMixS) + A.DecelGinsuMixS;
            float xf = _decelXfade.Value(GinsuScaledRpm);
            float decAcc = pDec * (A.AclNegL - A.AclNegL) + A.AclNegS;   // the game subtracts L from L (CARSFX decomp)
            if (Dual)
            {
                decAems = 1f - xf + xf * decAems;   // FILTER_SCALE
                decGinsu = xf * decGinsu;
            }
            else decGinsu = 0f;
            float accCut = Dual ? 25000f : DistanceFilter;
            float decCut = Mathf.Min(A.LowPassCutoff, DistanceFilter);
            float nAems = Mathf.Lerp(decAems, accAems, accelDecel);
            float nDec = Mathf.Lerp(decGinsu, 0f, accelDecel);
            float nAcc = Mathf.Lerp(decAcc, accGinsu, accelDecel);
            float nCut = Mathf.Lerp(decCut, accCut, accelDecel);
            if (smoothing && Dual)
            {
                // UpdateDualMixEng smooths cur→new by 0.2 (cutoff 6000); UpdateSingleMixEng's smooth(new, new) is a plain set
                _mixAems = SndCurves.Smooth(_mixAems, nAems, 0.2f);
                _mixAcc = SndCurves.Smooth(_mixAcc, nAcc, 0.2f);
                _mixDec = SndCurves.Smooth(_mixDec, nDec, 0.2f);
                _mixCut = SndCurves.Smooth(_mixCut, nCut, 6000f);
            }
            else { _mixAems = nAems; _mixAcc = nAcc; _mixDec = nDec; _mixCut = nCut; }
            GinsuLpf = Dual ? Mathf.Min(_mixCut, DistanceFilter)
                : 25000 - MixShape.Curve(MixShape.UpOneMinEqPwrSq, (int)(_mixCut * 1.3106799f), true);
            float f = EngineVol / 32767f;
            float aemsVol = Dual ? Mathf.Lerp(A.DecelAemsVol, A.AemsVol, accelDecel) : A.AemsVol;
            int vA = (int)(_mixAems * aemsVol * f), vAcc = (int)(_mixAcc * A.GinsuAccelVol * f), vDec = (int)(_mixDec * A.GinsuDecelVol * f);
            if (smoothing)
            {
                EngVolAems = SndCurves.Smooth(EngVolAems, vA, 7000);
                EngVolAccelGinsu = SndCurves.Smooth(EngVolAccelGinsu, vAcc, 7000);
                EngVolDecelGinsu = SndCurves.Smooth(EngVolDecelGinsu, vDec, 7000);
            }
            else { EngVolAems = vA; EngVolAccelGinsu = vAcc; EngVolDecelGinsu = vDec; }
            EngVolRedLine = (int)(A.AemsVol * f);
            // UpdateVolumeRedlining
            EngVolRedLine = (int)(EngVolRedLine * _redSamp.Value);
            EngVolAems = (int)(EngVolAems * _redEng.Value);
            EngVolAccelGinsu = (int)(EngVolAccelGinsu * _redEng.Value);
            EngVolDecelGinsu = (int)(EngVolDecelGinsu * _redEng.Value);
            // UpdateMixerOutputs
            bool on = false;
            if (Speed > 30f && Mathf.Abs(_avgDelta.Value) < 30f) on = Time > _tSteady + 3f;
            else _tSteady = Time;
            if (on)
            {
                if (_steadyFrames == 0) { _steadyFrames = Random.Range(60, 210); _playCompression = true; }
                _steadyFrames--;
            }
            else _tSteady = Time;
            Hybrid.Set(0, SndCurves.Smooth(Hybrid.Get(0), on ? 0x7FFF : 0, 983, 196));
            float pThr = Mathf.Clamp01(1f - (A.AccelDeltaThr - Mathf.Abs(_avgDelta.Last)) / A.AccelDeltaThr);
            int o = SndCurves.Smooth(Hybrid.Get(1), (int)(pThr * 32767f), 3000);
            if (!WasRedlining && Redlining) _prevDeltaOut = o / 2;
            if (!ShiftActive && car.WheelsOnGround > 0) Hybrid.Set(1, o);
            if (Redlining) Hybrid.Set(1, _prevDeltaOut);
        }

        // ---------------------------------------------------------------- DMIX inputs
        void MixerOutputs(PlayerCar car)
        {
            float mph = Speed;
            Phys.Set(0, Mathf.Clamp((int)(mph * 1092.2334f), 0, 0x7FFF));
            Phys.Set(1, Mathf.Clamp((int)(mph * 546.1167f), 0, 0x7FFF));
            Phys.Set(2, Mathf.Clamp((int)(mph * 327.66998f), 0, 0x7FFF));
            Phys.Set(3, Mathf.Clamp((int)(mph * 234.05f), 0, 0x7FFF));
            Phys.Set(4, Mathf.Clamp((int)((10000f - PhysRpm) * 3.6407778f), 0, 0x7FFF));
            Phys.Set(10, Accelerating ? 0x7FFF : 0);
            Phys.Set(5, (int)(car.WheelsOnGround / 4f * 32767f));
            int pov = Player ? SndMixer.PovType : 2;   // EAXCar::GetPOV (non-player m_PovType is never written on PC [?])
            int target = pov == 0 ? 0 : pov == 1 ? 4000 : 0x7FFF;
            Phys.Set(6, SndCurves.Smooth(Phys.Get(6), target, 0x3FFF));
            Phys.Set(8, 0);
            Phys.Set(7, 0);
            Shift.Set(0, ShiftState == 1 ? 0x7FFF : 0);
            Shift.Set(1, ShiftStageChanged == 2 ? 0x7FFF : 0);
            Shift.Set(2, IsDownShifting ? 0x7FFF : 0);
        }

        /// <summary>POV-dependent emitter positions (SFXCTL_Engine vCarPos, SFXCTL_3DRearPos, SFXCTL_Wheel, wind spheres).</summary>
        public void Positions(PlayerCar car, float dt, float sphereRadius)
        {
            var tr = car.transform;
            var vel = car.GetComponent<Rigidbody>() ? car.GetComponent<Rigidbody>().linearVelocity : Vector3.zero;
            int pov = Player ? SndMixer.PovType : 2;
            CarPos.Update(tr.position + tr.forward * (pov != 1 ? 0.2f : 2f), vel, dt);
            float off = 2f;
            if (pov != 0 && pov != 1 && SndMixer.Cam)
            {
                var a = new Vector2(tr.position.x, tr.position.z);
                var b = new Vector2(SndMixer.Cam.position.x, SndMixer.Cam.position.z);
                off = Mathf.Min(off, Vector2.Distance(a, b) - 1f);
            }
            RearPos.Update(tr.position - tr.forward * off, vel, dt);
            // SFXCTL_Wheel::GenerateWheelPosition: 5 m to each side, 3 m ahead (gfTireOffsetDist/gfTireFwdOffsetDist)
            RWheel.Update(tr.position + tr.right * 5f + tr.forward * 3f, Vector3.zero, dt);
            LWheel.Update(tr.position - tr.right * 5f + tr.forward * 3f, Vector3.zero, dt);
            // CARSFX_WindNoise: two points on a sphere shrinking with speed, swept back from straight ahead
            float v = Mathf.Clamp(vel.magnitude, 2f, 39.95f), ratio = v / 40f;
            float sep = (ratio * 12288f + 1280f) / 65536f * 360f;
            float radius = Mathf.Max((1f - ratio) * 65f, sphereRadius * 1.01f);
            LWind.Update(tr.position + Quaternion.AngleAxis(-sep, Vector3.up) * tr.forward * radius, Vector3.zero, dt);
            RWind.Update(tr.position + Quaternion.AngleAxis(sep, Vector3.up) * tr.forward * radius, Vector3.zero, dt);
        }
    }
}
