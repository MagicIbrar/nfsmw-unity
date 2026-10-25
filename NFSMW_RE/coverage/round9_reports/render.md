| source file | new status | unity files | note |
|---|---|---|---|
| World/VehicleFragmentConn.cpp | Ported | Runtime/CarRender/CarFragments.cs, CarFragment.cs | ctor 0x7475C0, UpdateModel 0x75AE10, Update 0x75B130, FetchData; 434 IModel cap |
| World/CarSkin.cpp | Ported | Core/CarRenderData.cs, Runtime/CarRender/* | precomposite vinyls (0x9B09FC never written), P8 composite 0x748AD0, spinners proven dead; cache = optimisation |
| World/VehiclePartDamage.cpp | Ported | Runtime/CarRender/CarRenderInfo.cs | ManageGlassDamage 0x74C210, Reset 0x75C330; UnitTest no caller |
| World/VehicleRenderConn.cpp | Ported | Effects/CarEffects.cs, CarRender/* | CheckForRain 0x744C70 → SetSurface 0x738D50; DoFX 0x7457D0 |
| World/RaceParameters.cpp | Ported | Core/Track.cs | InitWithDefaults 0x733B00; others empty stubs on PC |
| World/Track.cpp | Ported | Core/Track.cs, Core/CollisionPack.cs | TopologyCoordinate::GetElevation 0x726F60 dead; caffeine = ret |
| World/TrackInfo.cpp | Ported | Core/Track.cs, Runtime/UI/Minimap.cs | 0x120-byte record, GetTrackInfo 0x722840 |
| World/TrackPositionMarker.cpp | Ported | Core/Track.cs, Core/NisContent.cs | Count 0x727170, Get 0x727280 |
| World/TrackPath.cpp | Ported | Core/RoadNetwork.cs, Core/Weather.cs, Core/TrackStreamerRules.cs, Police/PoliceZones.cs | zone table/cache = lookup accelerators, same first-match order |
| World/SimpleModelAnim.cpp | Ported | Runtime/Nis/SimpleModelAnim.cs | Init 0x73D2C0, Reset 0x73D300, Update 0x73D320, Animate 0x73D3C0 |
| Ecstasy/eModel.cpp | Ported | Runtime/MeshFactory.cs, CarBuilder.cs, CarRenderInfo.cs | eSmoothNormals dead (SetSmoothVertex 0x6BE570 = ret on PC); rest plumbing |
| EAGL4Anim/Skeleton.cpp | Ported | Core/EaglAnim.cs, Core/WorldAnim.cs, Nis/NisCharacter.cs | GetStillPose 0x49FF90, PoseSQTToGlobal 0x49FCF0 1:1; others dead by xref |
| EAGL4Anim/CsisEventChannel.cpp | N/A | Core/EaglAnim.cs | no type-24 channel in the 126 shipped banks |
| Camera/Camera.cpp | Ported | Camera/CameraNoise.cs, IceShot.cs, ChaseCamera.cs | integer-hash noise 0x467250..0x467420 |
| Camera/CameraMover.cpp | Ported | CameraNoise.cs, ChaseCamera.cs | ChopperNoise 0x470A60; HandheldNoise dead |
| Camera/Actions/CDActionDrive.cpp | Ported | ChaseCamera.cs, CameraNoise.cs | explosion shake 0x47308E, TriggerPulse 0x472B1D |
| Camera/CameraAI.cpp | Ported | CameraDirector.cs | JumpCamsAllowed 0x469E30 |
| Camera/Actions/CDActionIce.cpp | Ported | Core/IceCameras.cs, CameraNoise.cs, ChaseCamera.cs, NisPlayer.cs | bCarSpaceLag 0x4834B5; event 0x15 dead |
| Camera/Movers/TrackCop.cpp | Ported | Camera/TrackCameras.cs | FindPursuitVehiclePosition 0x475170; CopViewCameraMover dead |
| Camera/Movers/TrackCar.cpp | Ported | TrackCameras.cs | rack focus 0x475015 |
| Camera/Movers/SelectCar.cpp | Partial 0.9 | Camera/SelectCarCamera.cs | full mover; CarShowroom still on its own copy |
| Camera/Actions/CDActionShowcase.cpp | N/A | | registered 0x884DC0, never requested |
