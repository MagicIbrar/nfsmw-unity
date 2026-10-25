| source file | new status | unity files | note |
|---|---|---|---|
| Physics/Common/PVehicle.cpp | Ported | Physics/VehicleMechanics.cs, PlayerCar.PVehicle.cs, Game/CarReset.cs, TrafficPhysics/VehicleSpecs.cs | LookupBehaviorSignature 0x67F0D0 + AI table 0x8F8890; data-driven RESET and racer behaviours; mid-life switching = component swap [ours] |
| Physics/Behaviors/RigidBody.cpp | Ported | CarContacts.cs, Physics/CollisionBounds.cs, WorldCollision.cs, Core/WTrigger.cs | ground point cloud, ONESIDED/side rule, GetTriggerFlags 0x699F60/0x69AFD0; one-packet react, Separate, frame skip = documented PhysX deviations |
| Physics/Common/SmokeableInfo.cpp | Ported | Runtime/Smackable.cs, CollisionBounds.cs | spawners without collection get no smackable; SceneryBox |
| Physics/Common/Smackable.cpp | Ported | Runtime/Smackable.cs | per-prop rigidbodyspecs, drag 0x6972D0, ShouldSleep 0x66A310, simplified-prop flags 0x684DF0 |
| Physics/Behaviors/SimpleRigidBody.cpp | Ported | Runtime/Smackable.cs | flags 0x20B; trigger flags 0x40 dead |
| Physics/SmackableTrigger.cpp | Ported | Runtime/Smackable.cs | ctor 0x667E70 |
| Physics/Behaviors/SuspensionSimple.cpp | Ported | TrafficPhysics/SuspensionSimple.cs, Chassis.cs | catch-up cheat, linear downforce, !IsModeling pause, jump stabiliser |
| Physics/Common/Bounds.cpp | Ported | Core/CollisionGeometry.cs, CollisionBounds.cs, Articulation.cs | PCloud parse; joints in Articulation |
| Physics/PhysicsUpgrades.cpp | Ported | Physics/PhysicsInfo.cs, CarSpec.cs, Gameplay/PerformanceRatings.cs | MatchPerformance 0x67C310 |
| Physics/Behaviors/SimpleChopper.cpp | Ported | Game/Police/Helicopter.cs | OnTaskSimulate 0x6A2430, pitch/roll 0x6A2030, chopperspecs default |
| Physics/Behaviors/DrawVehicle.cpp | Partial 0.95 | CarRender/CarFragments.cs | IModel cap 434 not enforced |
| Physics/Behaviors/Effects.cpp | Ported | Effects/CarEffects.cs | DoHit/DoScrape/bottom-out/OnDetached |
| Physics/Behaviors/DamageVehicle.cpp | Ported | PlayerCar.cs, CarDamage.cs, TrafficPhysics/VehicleDamage.cs | shock, Destroy, ResetDamage |
| Physics/Behaviors/DrawCar.cpp | Ported | CarRenderInfo.cs, CarRenderConn.cs, PlayerCar.PVehicle.cs | packet incl. reset flashing |
| Physics/Behaviors/DrawHeli.cpp | Partial 0.9 | Game/Police/Helicopter.cs | wash 0x693990; shadow scale not passed |
| Physics/Behaviors/SoundCar.cpp | Partial 0.9 | Audio/CarSound.cs, PlayerCar.PVehicle.cs | ToleratedSlip/SoundWheelSlip added, audio not using yet |
| Physics/Common/Wheel.cpp | Partial 0.85 | PlayerCar.cs, TrafficPhysics/Chassis.cs | player raycast kept (physics.md §27.5); Chassis on WWorldPos |
| Physics/Behaviors/DamageCopCar.cpp | Ported | TrafficPhysics/VehicleDamage.cs | CheckUpright 6 s → Destroy |
| Physics/Behaviors/RBCop.cpp | Ported | CarContacts.cs | forwards to RBVehicle (vtable 0x8AD008) |
| Physics/Behaviors/EngineTraffic.cpp | Ported | TrafficPhysics/EngineTraffic.cs | 0x6A3480 |
| Physics/Behaviors/RBTractor.cpp | Ported | Articulation.cs, TrafficVehicle.cs | pose + SetVehicleOnGround; 2 no-ops |
| Physics/Behaviors/SuspensionTraffic.cpp | Ported | TrafficPhysics/SuspensionTraffic.cs | verified 1:1 |
| Physics/PhysicsTunings.cpp | Ported | CarSpec.cs, UI/TuningScreen.cs | Lower/UpperLimit |
| Physics/Behaviors/SoundHeli.cpp | Ported | Audio/WorldAudio.cs | connection only |
| Physics/Behaviors/SuspensionTrailer.cpp | Ported | TrafficPhysics/SuspensionTrailer.cs | verified 1:1 |
| Physics/Behaviors/DamageRacer.cpp | Ported | Police/Roadblock.cs, PlayerCar.cs | puncture → blown 0.5 s |
| Physics/Behaviors/DamageHeli.cpp | Ported | Game/Police/Helicopter.cs | empty DoAutoDestruct |
| World/Common/WCollisionMgr.cpp | Ported | Core/WCollisionQueries.cs, WorldCollision.cs | GetTriList, barrier queries; Collide(instances) dead |
| World/Common/WWorldMath.cpp | Ported | Core/WWorldMath.cs | IntersectCircle 0x767400 |
| World/Common/WWorldPos.cpp | Ported | Core/WCollisionQueries.cs, Physics/WorldQueries.cs | face-list search, SetTolerance |
| World/Common/WCollider.cpp | Ported | Core/WCollider.cs | Refresh 0x789EC0 |
| World/Common/WTrigger.cpp | Ported | Core/WTrigger.cs | activation test, UpdateBox |
| World/Common/WCollisionAssets.cpp | Ported | Core/CollisionPack.cs, WorldQueries.cs | OBB query objects |
| World/Common/WWorld.cpp | N/A | WCollisionQueries, WorldCollision | WGrid/asset loading replaced by streaming |
| World/HeliRenderConn.cpp | Partial 0.9 | Game/Police/Helicopter.cs | model offset 0x739420; shadow scale not passed |
| World/HeliSheet.cpp | Ported | Core/HeliSheet.cs, Police/HeliSheets.cs | GetElevation/FindHeliPoly 1:1 |
| World/Common/WCollision.cpp | Ported | Core/CollisionPack.cs | |
| World/Common/WCollisionPack.cpp | Ported | Core/CollisionPack.cs | 'co' objects parsed |
