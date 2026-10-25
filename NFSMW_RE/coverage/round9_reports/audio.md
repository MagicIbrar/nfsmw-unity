| source file | new status | unity files | note |
|---|---|---|---|
| EAXSound/Dynamic_Mixer/NFSMixMap.cpp | Ported | Core/MixMap.cs | 0x4F4270, 0x4F18E0, 0x4F17F0, 0x4EA230.., 0x4E9B30 compared; allocation replaced by Build |
| EAXSound/Dynamic_Mixer/NFSMixShape.cpp | Ported | Core/SoundMath.cs | tables/shapes match |
| EAXSound/Dynamic_Mixer/NFSMixMapState.cpp | N/A | Core/MixMap.cs | state/channel creation replaced by MixMap.Build |
| EAXSound/Dynamic_Mixer/NFSMixMaster.cpp | Ported | Core/MixMap.cs, SndSystem.cs | map files and swap 0x4F1EC0 |
| EAXSound/STICH_PlayBack.cpp | Ported | Runtime/Audio/WorldAudio.cs | 25-voice limit + pruning 0x4CF520/0x4CEB80/0x4CECE0; cStitchLoop unreachable |
| EAXSound/STICH_Loader.cpp | Ported | Core/Stitch.cs | 0x4C42F0 |
| EAXSound/States/STATE_Base.cpp | N/A | Runtime/Audio/SndSystem.cs | object creation/attach plumbing replaced by SndMixer units |
| EAXSound/States/Registration.cpp | N/A | Runtime/Audio/SndSystem.cs | registration plumbing |
| EAXSound/States/Managers/STATEMGR_Base.cpp | N/A | Runtime/Audio/SndSystem.cs | plumbing |
| EAXSound/States/Managers/STATEMGR_CarState.cpp | Ported | Runtime/Audio/EngineBanks.cs, EngineAudio.cs | ResolveCarBanks 0x4F9020, DestroyCar, ResetCarBanks |
| EAXSound/States/Managers/STATEMGR_PlayerCar.cpp | Ported | SndSystem.cs | 0x4B5E50/0x4B5D70/0x4BF2E0 |
| EAXSound/sfxctl/SFXCTL_3DObjPos.cpp | Ported | SndSystem.cs (SndPos) | camera velocity, sign flags |
| EAXSound/States/Managers/STATEMGR_* (DriveBy, Enviro, Collision, AICar, TrafficCar, CopCar, Truck, Music, Main, Helicopter) | Ported | WorldAudio.cs, EngineAudio.cs | constant scan clean |
| EAXSound/States/STATE_* (DriveBy, Helicopter, Collision, Main, Music) | Ported | WorldAudio.cs | constant scan clean |
| EAXSound/CARSFX/CARSFX_Engine.cpp | Ported | EngineAudio.cs | main bank only, sPU_or_EE = 1, tranny clamp |
| EAXSound/CARSFX/CARSFX_Roadnoise.cpp | Ported | Runtime/Game/Sfx.cs | wet-road loop 0x4DAB10, spike-strip transition |
| EAXSound/sfxctl/SFXCTL_Engine.cpp | Ported | CarSound.cs | top-gear redline (key 0x07A7A3E5 [?]), visual rpm |
| EAXSound/sfxctl/SFXCTL_HybridMotor.cpp | Ported | CarSound.cs | 0x4BD160/0x4BD650/0x4BDC30 |
| EAXSound/sfxctl/SFXCTL_Shifting.cpp | Ported | CarSound.cs | 0x4BBC10/0x4BBF10/0x4C4DB0 |
| EAXSound/sfxctl/SFXCTL_Tunnel.cpp | Ported | WorldAudio.cs | 12 functions; PC reverb table 0x8F2140 |
| EAXSound/sfxctl/SFXCTL_Helicopter.cpp | Ported | WorldAudio.cs | 0x4BFA30 |
| EAXSound/CARSFX/SFXObj_Helicopter.cpp | Ported | WorldAudio.cs | 0x4F0840 |
| EAXSound/sfxctl/SFXCTL_Wheel.cpp | Ported | Sfx.cs, WorldAudio.cs | slip from PlayerCar.SoundWheelSlip |
| EAXSound/sfxctl/SFXCTL_Physics.cpp | Ported | CarSound.cs | constant scan clean |
| EAXSound/sfxctl/SFXCTL_Pathfinder5.cpp | Ported | Music.cs | |
| EAXSound/sfxctl/SFXCTL_NISReving.cpp | Ported | NisRevManager.cs | countdown from GameFlow[3] |
| EAXSound/sfxctl/SFXCTL_MasterVol.cpp | Ported | SndSystem.cs | |
| EAXSound/sfxctl/SFXCTL.cpp | Ported | SndSystem.cs | |
| EAXSound/SndCamera.cpp | Ported | SndSystem.cs | 0x4D5D40 latch; 0x4D5CE0 = polling the director |
| EAXSound/EAXTunerCar.cpp | Ported | SndSystem.cs | UpdatePov 0x4B9FE0; bottom-out message has no PC sender |
| EAXSound/EAXCar.cpp | Ported | CarSound.cs | Attach 0x4E4FD0; 0x4D5C00/0x4D5C20 |
| EAXSound/EAXAITunerCar.cpp | Ported | AiEngine.cs | tables 0x8963EC.. |
| EAXSound/EAXAIUtils.cpp | Ported | WorldAudio.cs | 0x896524 |
| EAXSound/EAXFrontEnd.cpp | Ported | FeSound.cs | |
| EAXSound/CARSFX/SFXObj_FEHUD.cpp | Ported | FeSound.cs | |
| EAXSound/SoundCollision.cpp | Ported | CollisionAudio.cs | |
| EAXSound/CARSFX/SFXObj_Collision.cpp | Ported | CollisionAudio.cs | |
| EAXSound/Stream/EAXS_StreamChannel.cpp | N/A | NisStreams.cs, CopRadio.cs | stream plumbing replaced by async decode |
| EAXSound/Stream/SpeechModule.cpp | N/A | SpeechManager.cs | bank offsets replaced by the script's .big offsets |
| EAXSound/Stream/NISSFXModule.cpp | Ported | NisStreams.cs | QueStream refusal, moment cut 0x6FFC50 |
| EAXSound/CARSFX/SFXObj_Pathfinder.cpp | Ported | Music.cs | MessageSwapInteractive 0x4DFC40 |
| EAXSound/EAXVolumes.cpp | Ported | Sfx.cs | 0x4B0B70 |
| EAXSound/CARSFX/* and SFXObj_* (others in remaining_round9) | Ported | CarSound.cs, Sfx.cs, WorldAudio.cs | constant scan clean |
| Speech/SoundAI.cpp | Ported | SpeechAI.cs | GetCustomized; split screen off |
| Speech/SpeechCache.cpp | N/A | SpeechManager.cs | sample cache replaced; readiness gate kept |
| Speech/MusicFlow.cpp | Ported | Music.cs | 600 s swap |
| Speech/MiscSpeech.cpp | Ported | SpeechAI.cs | SMSCellCall 0x71D860, MapSMSToSPCHEnums 0x701540 |
