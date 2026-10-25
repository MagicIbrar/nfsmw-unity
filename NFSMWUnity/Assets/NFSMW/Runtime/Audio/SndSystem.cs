using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>eSNDCTLSTATE (EAXSound/SoundPause.h).</summary>
    public enum SndCtl
    {
        Off = 0, Pause, InGame, FE, FEUpscreen, FESmsMessage, NisStory, NisIntro, Nis321, NisBlk, NisArrest, Fmv, StopMusic,
        GameStartRace, MiniLoad, FadeOut, Error, SystemHud,
    }

    /// <summary>eSndGameMode (EAXAudioParams.hpp). PursuitBreaker is what the speedbreaker sets (LocalPlayer::SetGameBreaker
    /// @0x6E9AA0 → EPursuitBreaker @0x641670 saves the mode and writes 10).</summary>
    public enum SndGameMode { None = 0, FrontEnd, DragRace, DriftRace, SmokeShow, StreetRace, ChallengeRace, FreeRoam, CarShow, LoadingScreen, PursuitBreaker }

    /// <summary>
    /// The sound control states (EAXSound.cpp SetSoundControlState, PC @0x4B9080): each active control state ORs its
    /// g_CtlStateActions mask (PC .data 0x8EE570, same values as the decomp) into g_ActiveSFXStates, which the main mix
    /// state's SFXCTL_GameState feeds to the mix map (one DMIX input per bit) to duck or mute whole groups: NIS and
    /// arrest scenes silence the cars, pause mutes the world, and so on. g_EAXIsPaused (@0x4AE8B0) = any of 0x3483B.
    /// </summary>
    public static class SoundState
    {
        static readonly uint[] Actions =
        {
            0xFFFFFFFF, 0x3F, 0x0, 0x1C, 0xBE, 0xBC, 0x479, 0x121, 0x200, 0x33, 0x838, 0x7F, 0x1, 0x7F, 0x38, 0x1000, 0xFFFFFFFF, 0xFFFFFFFF,
        };
        public static uint ActiveCtl { get; private set; }
        public static uint ActiveSfx { get; private set; }
        public static bool Paused => (ActiveCtl & 0x3483B) != 0;
        public static SndGameMode Mode { get; private set; } = SndGameMode.FreeRoam;
        public static SndGameMode PrevMode { get; private set; } = SndGameMode.FreeRoam;
        static SndGameMode _beforeBreaker = SndGameMode.FreeRoam;

        public static void Set(bool on, SndCtl state)
        {
            uint bit = 1u << (int)state;
            if (((ActiveCtl & bit) != 0) == on) return;
            ActiveCtl = on ? ActiveCtl | bit : ActiveCtl & ~bit;
            uint sfx = 0;
            for (int n = 0; n < 18; n++) if ((ActiveCtl & (1u << n)) != 0) sfx |= Actions[n];
            ActiveSfx = sfx;
        }

        public static bool Has(SndCtl state) => (ActiveCtl & (1u << (int)state)) != 0;

        /// <summary>
        /// GameFlowSndState[15] (@0x911EE4): the ESndGameState(index, on) event (ctor @0x6253D0 writes the slot; Lua
        /// Audio_SetFlag @0x6048E0 maps its name table @0x8F5D78: eNIS_Start 2, eNIS_321GO 3, eNIS_Busted 4, eNIS_EndofRace 5,
        /// eFE_PostRaceScreen 6, eFE_CameraScreen 7, eLOAD_GeneralLoading 10, eLOAD_BustedLoading 11; FMV screens use 1, the
        /// SMS screen 13). Readers: SFXObj_FEHUD::UpdateMixerOutputs copies it into MAIN obj 7's inputs, SFXObj_PFEATrax holds
        /// the music off while 4/5/6/7/11 are set, EAXSound::Update runs Speech::Manager::Deduce while 13 is set,
        /// SFXCTL_Physics reads 3. Cleared whole on the pause-menu path of EAXSound::Update (EAXSound.cpp:958).
        /// </summary>
        public static readonly int[] GameFlow = new int[15];
        public const int FlowNisStart = 2, FlowNis321Go = 3, FlowNisBusted = 4, FlowNisEndOfRace = 5, FlowFePostRace = 6, FlowFeCamera = 7,
            FlowLoadGeneral = 10, FlowLoadBusted = 11, FlowSms = 13;

        /// <summary>ESndGameState(state, turningOn).</summary>
        public static void SetGameFlow(int index, bool on)
        {
            if (index < 0 || index >= GameFlow.Length) return;
            GameFlow[index] = on ? 1 : 0;
        }

        public static void SetMode(SndGameMode m)
        {
            if (m == Mode) return;
            PrevMode = Mode;
            Mode = m;
        }

        /// <summary>EPursuitBreaker(on): the speedbreaker's audio mode switch.</summary>
        public static void SetBreaker(bool on)
        {
            if (on == (Mode == SndGameMode.PursuitBreaker)) return;
            if (on) { _beforeBreaker = Mode; PrevMode = Mode; Mode = SndGameMode.PursuitBreaker; }
            else { PrevMode = Mode; Mode = _beforeBreaker; }
        }
    }

    /// <summary>One sound object or controller in the mix map (SndBase): its DMIX input block and packed output block.</summary>
    public sealed class SndObj
    {
        public readonly int Id;
        public readonly int[] In;
        int[] _out;
        readonly MixMap _map;
        public SndObj(MixMap map, int id) { _map = map; Id = id; In = map.Inputs(id); _out = map.Outputs(id); }
        public void Set(int slot, int v) => In[slot] = v;
        public int Get(int slot) => In[slot];
        /// <summary>GetDMixOutput: 0 vol (Q15), 1 pitch (x4096), 2 freq (Hz), 3 azimuth.</summary>
        /// <summary>SndBase::GetDMixOutput (PC @0x4EB7D0): DMX_AZIM reads 0 in mono (AudioSettings.AudioMode 0, AUDIO_MODE_MONO).
        /// Without an output block the exe returns 0 for every type; the port keeps pitch 1.0 there [ours].</summary>
        public int Out(int slot, int type) => _out == null && type == MixMap.Pitch ? 4096
            : type == MixMap.Azim && SndMixer.AudioMode == 0 ? 0 : MixMap.Output(_out, slot, type);
        public float Vol(int slot) => Out(slot, MixMap.Vol) / 32767f;
        public float Pitch(int slot) => Out(slot, MixMap.Pitch) / 4096f;
        public bool Mapped => _out != null;
        public void Attach(bool on) => _map.Attach(Id, on);
    }

    /// <summary>A per-frame participant of <see cref="SndMixer"/> (an EAXCar-like state: controllers, then sounds).</summary>
    public interface ISndUnit
    {
        /// <summary>UpdateParams + UpdateMixerOutputs: game state into DMIX inputs (dt 0 while paused).</summary>
        void Tick(float dt);
        /// <summary>ProcessUpdate: mixer outputs onto the voices (real frame time).</summary>
        void Apply(float dt);
    }

    /// <summary>
    /// The dynamic mixer driver (EAXSound::Update / NFSMixMaster): loads SOUND\MIXMAPS\MAPOUTPUT.mxb, hands out mix
    /// state instances (one PLAYERCAR per player, one AIRACECAR per racer, one COPCAR per cop ...), runs the MAIN
    /// state's SFXCTL_MasterVol and SFXCTL_GameState every frame from the game's state (pause, NIS, countdown,
    /// speedbreaker, camera view), then every unit's controllers, the map, and every unit's sounds. All audio is 2D:
    /// the map's 3D controls supply distance rolloff, azimuth (applied as stereo pan) and Doppler, as the game does.
    /// </summary>
    public sealed class SndMixer : MonoBehaviour
    {
        public static SndMixer Instance { get; private set; }
        public MixMap Map { get; private set; }
        /// <summary>The camera the 3D maths use (SndCamera); Camera.main when null.</summary>
        public static Transform Listener;
        public static float MasterVolume = 1f, SfxVolume = 1f, CarVolume = 1f, SpeechVolume = 1f;
        /// <summary>FEAudioSettings.AudioMode (eSndAudioMode: 0 mono, 1 stereo, 2 Pro Logic), read every frame.</summary>
        public static int AudioMode { get; private set; } = 1;
        /// <summary>FEAudioSettings.SpeechVol as last read (ScheduleSpeechPartII drops everything at 0).</summary>
        public static float SpeechVolumeSetting { get; private set; } = 1f;
        /// <summary>The FE / in-game music sliders as last read from the options store.</summary>
        public static float FeMusicVolume { get; private set; } = 1f;
        public static float IgMusicVolume { get; private set; } = 1f;
        /// <summary>Global pitch/low-pass applied on top of every voice [?] (unused unless set).</summary>
        public int CamState { get; private set; }

        /// <summary>Instances per mix state. The exe's EnterWorld counts (PC): MAIN 1, MUSIC 1, PLAYERCAR 1 (2 split), AIRACECAR
        /// ≤4, COPCAR 4 (@0x4F3350), TRAFFIC 6 (@0x4E5C00), ENVIRONMENT 5 (@0x4E61A0), COLLISION 10 (@0x4E5D30), DRIVEBY 6
        /// (@0x4E6050), HELICOPTER 1, TRUCK 2. The port keeps 8 AI racers and 16 cops each with its own state (the exe
        /// connects the nearest cars within 135 m to its 4) [ours].</summary>
        static readonly int[] Counts = { 1, 1, 2, 8, 16, 6, 5, 10, 6, 0, 0, 1, 2 };
        readonly List<int>[] _free = new List<int>[MixMap.NumStates];
        readonly List<ISndUnit> _units = new List<ISndUnit>();
        SndObj _masterVol, _gameState, _speechObj, _common, _fehud;
        public SndObj Speech => _speechObj;
        Vector3 _camPrev;
        bool _camPrevValid;
        /// <summary>SndCamera::m_v3WorldCamVel (Camera::GetVelocityPosition): the listener camera's velocity, m/s, from its
        /// position change over the frame [ours: the camera's own velocity term].</summary>
        public static Vector3 CamVelocity { get; private set; }
        bool _wasBreaker;
        int[] _buildCounts;
        int _mapIndex;
        static readonly string[] MapFiles = { "MAPOUTPUT.mxb", "MAPOUTPUTDRG.mxb", "MAPOUTPUT2CR.mxb", "MAPOUTPUT2DR.mxb" };
        readonly MixMap[] _maps = new MixMap[4];

        /// <summary>
        /// EAXSound::StartNewGamePlay (PC @0x4D0D70) → NFSMixMaster::CreateMainMainMap(szMixMapFiles[i]) (@0x4F1EC0): a drag
        /// race loads MAPOUTPUTDRG (split screen 2DR), any other race or free roam MAPOUTPUT (split screen 2CR). The port
        /// swaps the map in place when the wanted file changes.
        /// </summary>
        void UpdateMapFile(RaceManager race)
        {
            bool split = GameSession.SplitScreen;
            bool drag = race != null && race.Event != null && race.Event.Type == RaceType.Drag;
            int want = drag ? (split ? 3 : 1) : split ? 2 : 0;
            if (want == _mapIndex || _buildCounts == null) return;
            if (_maps[want] == null)
            {
                try
                {
                    var path = Path.Combine(GameData.Root ?? "", "SOUND", "MIXMAPS", MapFiles[want]);
                    _maps[want] = File.Exists(path) ? MixMap.Load(path) : null;
                }
                catch (System.Exception e) { Debug.LogWarning("NFSMW mixer: " + MapFiles[want] + ": " + e.Message); }
                if (_maps[want] == null) { _mapIndex = want; return; }
            }
            Map.Swap(_maps[want], _buildCounts);
            _mapIndex = want;
        }

        public static SndMixer Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("NFSMW Audio");
            DontDestroyOnLoad(go);
            return go.AddComponent<SndMixer>();
        }

        void Awake()
        {
            Instance = this;
            GameData.Mount();
            try
            {
                var path = Path.Combine(GameData.Root ?? "", "SOUND", "MIXMAPS", "MAPOUTPUT.mxb");
                Map = File.Exists(path) ? MixMap.Load(path) : null;
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW mixer: " + e.Message); Map = null; }
            if (Map == null) Map = MixMap.Read(EmptyMap());
            var counts = new int[MixMap.NumStates];
            for (int s = 0; s < MixMap.NumStates; s++)
            {
                counts[s] = Map.HasState(s) ? Counts[s] : 0;
                _free[s] = new List<int>();
                for (int i = counts[s] - 1; i >= 0; i--) _free[s].Add(i);
            }
            Map.Build(counts);
            _buildCounts = counts;
            _free[MixMap.Main].Clear();
            _masterVol = new SndObj(Map, MixMap.ObjectId(true, MixMap.Main, 0, 0));
            _gameState = new SndObj(Map, MixMap.ObjectId(true, MixMap.Main, 0, 1));
            _speechObj = new SndObj(Map, MixMap.ObjectId(false, MixMap.Main, 0, 1));
            _common = new SndObj(Map, MixMap.ObjectId(false, MixMap.Main, 0, 4));   // SFX_Common (0x40)
            _fehud = new SndObj(Map, MixMap.ObjectId(false, MixMap.Main, 0, 7));    // SFXObj_FEHUD (0x70)
            NisStreams.NisObj = new SndObj(Map, MixMap.ObjectId(false, MixMap.Main, 0, 5));     // SFXObj_NISStream (0x50)
            NisStreams.MomentObj = new SndObj(Map, MixMap.ObjectId(false, MixMap.Main, 0, 6));  // SFXObj_MomentStrm (0x60)
            for (int o = 0; o < 8; o++) Map.Attach(MixMap.ObjectId(false, MixMap.Main, 0, o), true);
            WorldAudio.Ensure(this);
        }

        static byte[] EmptyMap()
        {
            var d = new byte[16 + 4 * MixMap.NumStates];
            System.BitConverter.GetBytes(MixMap.NumStates).CopyTo(d, 4);
            System.BitConverter.GetBytes(16).CopyTo(d, 8);
            for (int s = 0; s < MixMap.NumStates; s++) System.BitConverter.GetBytes(-1).CopyTo(d, 16 + 4 * s);
            return d;
        }

        /// <summary>A free instance of a mix state (-1 if all are taken: the object then plays unmixed at its defaults).</summary>
        public int Acquire(int state)
        {
            var f = _free[state];
            if (f.Count == 0) return -1;
            int i = f[f.Count - 1];
            f.RemoveAt(f.Count - 1);
            return i;
        }

        public void Release(int state, int inst)
        {
            if (inst < 0 || _free[state].Contains(inst)) return;
            // clear the instance's object blocks so a reuse starts silent
            for (int o = 0; o < 32; o++)
            {
                var a = Map.Inputs(MixMap.ObjectId(false, state, inst, o));
                var b = Map.Inputs(MixMap.ObjectId(true, state, inst, o));
                System.Array.Clear(a, 0, a.Length);
                System.Array.Clear(b, 0, b.Length);
                Map.Attach(MixMap.ObjectId(false, state, inst, o), false);
            }
            _free[state].Add(inst);
        }

        /// <summary>The state an object lives in: its own, or (no free instance) the empty PLANE state whose objects never get mixer output.</summary>
        public static int StateOr(int state, int inst) => inst >= 0 ? state : MixMap.Plane;

        public void Register(ISndUnit u) { if (!_units.Contains(u)) _units.Add(u); }
        public void Unregister(ISndUnit u) => _units.Remove(u);

        public static Transform Cam => Listener ? Listener : Camera.main ? Camera.main.transform : null;

        /// <summary>The player car the 3D maths measure "distance to player car" from (SndCamera world car).</summary>
        public static PlayerCar PlayerRef => GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;

        void LateUpdate()
        {
            float real = Time.unscaledDeltaTime;
            if (real <= 0) return;
            UpdateStates();
            var camT = Cam;
            if (camT)
            {
                if (_camPrevValid && !SoundState.Paused) CamVelocity = (camT.position - _camPrev) / real;
                _camPrev = camT.position;
                _camPrevValid = true;
            }
            CopAudio.Scan();
            float dt = SoundState.Paused ? 0f : Mathf.Min(real, 0.1f);
            UpdateMain();
            NisRevManager.Update();
            for (int i = _units.Count - 1; i >= 0; i--)
            {
                var u = _units[i];
                if (u == null || (u is Object o && !o)) { _units.RemoveAt(i); continue; }
                u.Tick(dt);
            }
            FeSound.UpdateMixerInputs(_fehud);
            MiscSound.Inputs(_common);
            MomentStream.Update(dt);
            CollisionAudio.Tick(dt);
            Map.Process(real, CamState);
            for (int i = 0; i < _units.Count; i++) _units[i].Apply(real);
            MiscSound.Apply(_common, _fehud);
            NisStreams.Update(real);
            CollisionAudio.Apply(real);
        }

        /// <summary>Drives the control states from the port's public game state (the original sets them from FE screens, NIS and the HUD).</summary>
        void UpdateStates()
        {
            var boot = GameBootstrap.Instance;
            bool inGame = boot && boot.Ready;
            var fe = NfsMw.UI.FrontEnd.Instance;
            bool menu = fe && fe.IsOpen;
            SoundState.Set(inGame, SndCtl.InGame);
            SoundState.Set(menu && inGame, SndCtl.Pause);
            SoundState.Set(menu && !inGame, SndCtl.FE);
            var nis = NisPlayer.Active;
            // the NIS states (NisStory / NisIntro / NisBlk / NisArrest) are set by NisSound from NisPlayer's READY_TO_PLAY and
            // cleared by its NISFinished (NISActivity::UpdateLoading @0x6F7230 / EAXSound::NISFinished @0x4C1C60)
            if (!nis) NisSound.NoActivity();
            NisSound.Poll(nis);
            var race = RaceManager.Active;
            SoundState.Set(race != null && race.Countdown, SndCtl.Nis321);
            var p = PlayerRef;
            bool breaker = p && p.SpeedBreakerOn;
            if (breaker != _wasBreaker)
            {
                SoundState.SetBreaker(breaker);
                _wasBreaker = breaker;
                // SFXObj_MomentStrm::ReceivePursuitBreaker (MPursuitBreaker): MMiscSound 3 on start, 4 on end
                MiscSound.Play(breaker ? 3 : 4);
                MomentStream.PursuitBreaker(breaker);
            }
            // StartNewGamePlay: drag race → SND_DRAGRACE (2), challenge race → 6, other races → SND_STREETRACE (5), roaming → 7
            if (!breaker)
            {
                var mode = !inGame ? SndGameMode.FrontEnd : race == null ? SndGameMode.FreeRoam
                    : race.Event != null && race.Event.Type == RaceType.Drag ? SndGameMode.DragRace
                    : GameSession.Mode == GameMode.ChallengeSeries ? SndGameMode.ChallengeRace : SndGameMode.StreetRace;
                SoundState.SetMode(mode);
            }
            UpdateMapFile(inGame ? race : null);
            UpdateCameras();
        }

        // SndCamera statics (SndCamera.cpp:13-33; PC m_CurCamState @0x9142B4, m_PrevCamState @0x9142BC, m_CamAction @0x914768,
        // m_NewCamAction @0x9145D8). Statics in the exe too: they survive a new game (never reset).
        static CameraAction _sndCamAction = CameraAction.Drive, _sndNewCamAction = CameraAction.Drive;
        /// <summary>The player tuner car's EAXCar::m_PovType (PC +0xC4, the POVTypes of the camera: 0 bumper, 1 hood, 2 close ...)
        /// and m_IsDriveCamera (+0xC8), from EAXTunerCar::UpdatePov.</summary>
        public static int PovType { get; private set; } = 2;
        public static bool IsDriveCamera { get; private set; }
        /// <summary>EAXCar::IsBumperCameraOn (@0x4D5C20) / IsHoodCameraOn (@0x4D5C00): drive camera and POV 0 / 1.</summary>
        public static bool IsBumperCameraOn => IsDriveCamera && PovType == 0;
        public static bool IsHoodCameraOn => IsDriveCamera && PovType == 1;
        /// <summary>SndCamera::m_PrevCamState[0].</summary>
        public int PrevCamState { get; private set; }

        /// <summary>
        /// SndCamera::UpdateCameras (PC @0x4D5D40, called from EAXSound::Update @0x4D0865; SndCamera.cpp:47-130). The state is
        /// latched only when the director's action changes: CDActionDrive → by the player tuner car's POV (EAXTunerCar::UpdatePov
        /// @0x4B9FE0, EAXCar+0xC4: 0 bumper → DMIX_NFS_BUMPER_CAM 1, 1 hood → DMIX_NFS_INCAR_CAM 2, else DMIX_DEFAULT_CAM 0);
        /// CDActionTrackCar → DMIX_NFS_COLLISION_CAM 5 when the car's health is 0 else DMIX_NFS_JUMP_CAM 3; CDActionIce →
        /// DMIX_NFS_NIS_CAM 4; any other action (TrackCop) keeps the state. Switching POV while the drive action stays does
        /// not change the state (exe behaviour), and the first drive action of a session keeps DEFAULT (both keys start as
        /// "CDActionDrive"). The port polls the director's action here instead of a call from Director::SetAction [ours: hook-free].
        /// </summary>
        void UpdateCameras()
        {
            var p = PlayerRef;
            var cam = Cam ? Cam.GetComponent<ChaseCamera>() : null;
            // EAXTunerCar::UpdatePov (PC @0x4B9FE0, every EAXTunerCar::UpdateParams @0x4AFD00): view 1's camera mover →
            // m_IsDriveCamera = IsDriveCamera(), m_PovType = the anchor's GetPOVType() (7 without an anchor; kept when no mover)
            if (cam) { IsDriveCamera = cam.Director.Action == CameraAction.Drive; PovType = cam.Pov; }
            else IsDriveCamera = false;
            if (!p || !cam) return;   // no player tuner car / no view camera: nothing updates
            _sndNewCamAction = cam.Director.Action;   // SetNewSndCamAction (@0x4D5CE0) from Director::SetAction / SelectAction
            PrevCamState = CamState;
            if (_sndCamAction == _sndNewCamAction) return;
            _sndCamAction = _sndNewCamAction;
            switch (_sndCamAction)
            {
                case CameraAction.Drive:
                    int pov = cam.Pov;   // EAXTunerCar::UpdatePov: the drive anchor's GetPOVType()
                    CamState = pov == 0 ? 1 : pov == 1 ? 2 : 0;
                    break;
                case CameraAction.TrackCar: CamState = p.Destroyed ? 5 : 3; break;
                case CameraAction.Ice: CamState = 4; break;
            }
        }

        /// <summary>
        /// SFXCTL_MasterVol::UpdateParams (PC @0x4B59F0) and SFXCTL_GameState::UpdateMixerOutputs. Inputs 0..5 =
        /// 1 - Master x bus x eqpwr(Master) for the FEAudioSettings buses (GetMastered*Vol return the raw sliders): 0 FE music,
        /// 1 in-game music, 2 speech, 3 sound effects, 4 and 5 car; EngineVol, AmbientVol and SpeedVol drive no input. The
        /// options store is FEDb.Data.Audio (the original options screen's). The master slider stays on
        /// AudioListener.volume (FEDb.Apply), so the mixer's master term is 1 here [ours: one master, applied once].
        /// </summary>
        void UpdateMain()
        {
            float fe = Settings.Music, ig = Settings.Music, speech = SpeechVolume, sfx = SfxVolume, car = CarVolume;
            try
            {
                var a = NfsMw.UI.FEDb.Data?.Audio;
                if (a != null) { fe = a.FEMusicVol; ig = a.IGMusicVol; speech = a.SpeechVol; sfx = a.SoundEffectsVol; car = a.CarVol; AudioMode = a.AudioMode; }
            }
            catch (System.Exception) { }
            float master = 1f;
            float curve = MixShape.Curve(MixShape.UpEqPwr, (int)(master * 32767f), false) / 32767f;
            int Inv(float v) => (int)((1f - master * Mathf.Clamp01(v) * curve) * 32767f);
            _masterVol.Set(0, Inv(fe));
            _masterVol.Set(1, Inv(ig));
            _masterVol.Set(2, Inv(speech));
            _masterVol.Set(3, Inv(sfx));
            _masterVol.Set(4, Inv(car));
            _masterVol.Set(5, Inv(car));
            FeMusicVolume = fe;
            SpeechVolumeSetting = speech;
            IgMusicVolume = ig;
            _masterVol.Set(6, SoundState.Paused ? 0x7FFF : 0);
            _masterVol.Set(7, SoundState.Has(SndCtl.Fmv) ? 0x7FFF : 0);
            _masterVol.Set(8, SoundState.Mode == SndGameMode.PursuitBreaker ? 0x7FFF : 0);
            _masterVol.Set(9, CamState == 3 ? 0x7FFF : 0);
            _masterVol.Set(10, 0);
            for (int i = 0; i <= 13; i++) _gameState.Set(i, (SoundState.ActiveSfx & (1u << i)) != 0 ? 0x7FFF : 0);
            _masterVol.In[15] = 1;
            _gameState.In[15] = 1;
        }

        /// <summary>The cop speech object's inputs (SFXObj_Speech): 0 speech playing, 2 positional, 3 clarity.</summary>
        public void SetSpeechInputs(bool playing, int clarity)
        {
            _speechObj.Set(0, playing ? 0x7FFF : 0);
            _speechObj.Set(3, clarity);
        }

        /// <summary>GameSpeech::GetVolForSpeaker: the mixer volume for a speaker id (1 dispatch, 2 heli, 3..8 units).</summary>
        public float SpeakerVolume(int speaker)
        {
            int slot = speaker switch { 1 => 11, 2 => 10, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 7 => 7, 8 => 8, _ => 14 };
            return _speechObj.Mapped ? _speechObj.Vol(slot) : 0.85f;
        }

        /// <summary>Low-pass cutoff the mixer gives speech (GameSpeech::Update slot 12).</summary>
        public float SpeechLowPass => _speechObj.Mapped ? Mathf.Max(200, _speechObj.Out(12, MixMap.Freq)) : 22000f;
    }

    /// <summary>
    /// SFXCTL_3DObjPos::GenerateSinglePlayerMix / UpdateDoppler: writes an emitter's distance (cm) and azimuth (bAngle)
    /// to the player car and to the camera, the closing speeds (cm/s) and the live flag into its DMIX inputs.
    /// The game's axes: azimuth 0 = straight ahead, increasing clockwise seen from above [?].
    /// </summary>
    public sealed class SndPos
    {
        public readonly SndObj Obj;
        float _distCar, _distCam, _velCar, _velCam;
        public SndPos(SndObj o) { Obj = o; }

        static int Azim(Vector3 from, Vector3 dir, Vector3 to)
        {
            var d = new Vector2(to.x - from.x, to.z - from.z);
            var f = new Vector2(dir.x, dir.z);
            if (d.sqrMagnitude < 1e-6f || f.sqrMagnitude < 1e-6f) return 0;
            d.Normalize();
            f.Normalize();
            float ang = Mathf.Acos(Mathf.Clamp(Vector2.Dot(d, f), -1f, 1f));   // 0..pi
            int a = (int)(ang / Mathf.PI * 32768f) & 0xFFFF;
            // right of the reference direction: second half of the circle (~angle)
            float cross = f.x * d.y - f.y * d.x;
            if (cross > 0) a = (~a) & 0xFFFF;
            return a;
        }

        public void Update(Vector3 pos, Vector3 vel, float dt)
        {
            var cam = SndMixer.Cam;
            var car = SndMixer.PlayerRef;
            if (!cam)
            {
                Obj.In[15] &= ~1;
                return;
            }
            Obj.In[15] |= 1;
            Vector3 carPos = car ? car.transform.position : cam.position, carFwd = car ? car.transform.forward : cam.forward;
            Obj.Set(3, Azim(cam.position, cam.forward, pos));
            Obj.Set(1, (int)(Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(cam.position.x, cam.position.z)) * 100f));
            Obj.Set(2, Azim(carPos, carFwd, pos));
            Obj.Set(0, (int)(Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(carPos.x, carPos.z)) * 100f));
            Obj.Set(11, 0);
            if (dt <= 0) return;
            // SFXCTL_3DObjPos::UpdateDoppler (PC @0x4BF2E0): skipped while paused; 3D distances to the player car and the camera,
            // closing speed = |object velocity - car / camera velocity|, negative while the distance shrinks; a sign change
            // raises input 15 bit 31 (car) / bit 30 (camera) so the mixer skips that frame's Doppler (Update3DMixCtls)
            Vector3 carVel = car && car.GetComponent<Rigidbody>() ? car.GetComponent<Rigidbody>().linearVelocity : Vector3.zero;
            float dCar = Vector3.Distance(carPos, pos), dCam = Vector3.Distance(cam.position, pos);
            float vCar = (vel - carVel).magnitude, vCam = (vel - SndMixer.CamVelocity).magnitude;
            if (dCar < _distCar) vCar = -vCar;
            if (dCam < _distCam) vCam = -vCam;
            if ((_velCar < 0f && vCar > 0f) || (_velCar > 0f && vCar < 0f)) Obj.In[15] |= unchecked((int)0x80000000);
            if ((_velCam < 0f && vCam > 0f) || (_velCam > 0f && vCam < 0f)) Obj.In[15] |= 0x40000000;
            _distCar = dCar;
            _distCam = dCam;
            _velCar = vCar;
            _velCam = vCam;
            Obj.Set(13, (int)(vCar * 100f));
            Obj.Set(14, (int)(vCam * 100f));
        }
    }

    /// <summary>
    /// One playing sound as the game's AEMS voices are driven: 2D, with volume (Q15), pitch (x4096), azimuth
    /// (bAngle → stereo pan) and a low-pass cutoff from the mixer. Lives on a child of its owner (the car object's own
    /// AudioSource belongs to EngineAudio's OnAudioFilterRead).
    /// </summary>
    public sealed class SndVoice
    {
        public readonly AudioSource Src;
        readonly AudioLowPassFilter _lp;
        public SndVoice(Transform parent, string name, bool lowPass = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Src = go.AddComponent<AudioSource>();
            Src.playOnAwake = false;
            Src.spatialBlend = 0f;
            Src.dopplerLevel = 0f;
            Src.volume = 0f;
            if (lowPass) { _lp = go.AddComponent<AudioLowPassFilter>(); _lp.cutoffFrequency = 22000f; }
            go.AddComponent<SendTap>();
        }

        public bool Playing => Src && Src.isPlaying;

        public void Loop(AudioClip clip)
        {
            if (!Src || !clip) return;
            if (Src.clip == clip && Src.isPlaying) return;
            Src.clip = clip;
            Src.loop = true;
            Src.time = Random.Range(0f, clip.length * 0.9f);
            Src.Play();
        }

        public void Once(AudioClip clip, float delay = 0f)
        {
            if (!Src || !clip) return;
            Src.clip = clip;
            Src.loop = false;
            if (delay > 0) Src.PlayDelayed(delay); else Src.Play();
        }

        public void Stop() { if (Src) Src.Stop(); }

        /// <summary>vol 0..1, pitch multiplier, azimuth bAngle (0 = front), low-pass Hz.</summary>
        public void Set(float vol, float pitch, int azim = 0, float lowPass = 22000f)
        {
            if (!Src) return;
            Src.volume = Mathf.Clamp01(vol);
            Src.pitch = Mathf.Clamp(pitch, 0.05f, 3f);
            Src.panStereo = Pan(azim);
            if (_lp) _lp.cutoffFrequency = Mathf.Clamp(lowPass, 100f, 22000f);
        }

        /// <summary>bAngle azimuth (clockwise from straight ahead, see SndPos) to stereo pan: sin of the angle, right positive.</summary>
        public static float Pan(int azim) => Mathf.Clamp(Mathf.Sin(azim / 65536f * Mathf.PI * 2f) * 0.8f, -1f, 1f);
    }
}
