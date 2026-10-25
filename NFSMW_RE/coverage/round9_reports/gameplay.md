| source file | new status | unity files | note |
|---|---|---|---|
| Gameplay/GManager.cpp | Partial 0.95 | Gameplay/*, Triggers.cs, LuaDrive.cs, Systems.cs | icon pool (AllocIcon/FreeDisposable/RefreshZoneIcons/Hide/Restore), WarpToMarker/NotifyWorldService, EnableBin*; remaining accounted §18.5; left: per-section icon restore, warp camera reset/fade |
| Gameplay/GRaceDatabase.cpp | Partial 0.95 | RaceDatabase.cs, Career.cs | accessor-to-attribute table §18.5; UpdateRaceScore max fix; bin open-world heat; save layout = Profile; GRaceCustom = FE |
| Gameplay/GRaceStatus.cpp | Partial 0.93 | RaceManager*.cs, PursuitSystem.Script.cs | GetBin*Heat ported (§18.6); racer-info mapped; stock-car / load-wait plumbing remains |
| Gameplay/GActivity.cpp | Partial 0.95 | StategraphRuntime, LuaFlow | Register/UnregisterMessageHandlers, DeleteObjects mapped/replaced |
| Gameplay/GRuntimeInstance.cpp | Ported | LuaTriggers.cs | GetPosition/Direction 0x5E2970/0x5DACF0 = PointOf |
| Gameplay/GIcon.cpp | Partial 0.9 | Triggers.cs | ctor, pool, visibility; particles/geometry = EventMarkers [ours] |
| Gameplay/GTrigger.cpp | Partial 0.93 | Triggers.cs, LuaDrive.cs | Show/HideIcon; flares not ported |
| Gameplay/GObjectBlock.cpp | Partial 0.95 | GameplayScripts / GameplayState | vault object blocks replaced by the resident database |
| Gameplay/GHandler.cpp | Partial 0.95 | StategraphRuntime.cs | HandleMessage = Deliver; no FilterBlocks in data |
| Gameplay/GVault.cpp | Partial 0.9 | LuaFlow.cs | paging replaced (resident data) |
| Gameplay/GSpeedTrap.cpp | Ported | GameSession.cs | GetBounty/JumpMarker/NotifyTriggered |
| Gameplay/GMilestone.cpp | Ported | GameSession.cs, Career.cs | accessors, EnableBinMilestones |
| Gameplay/GTimer.cpp | Ported | GameplayState.cs | GetTime = GEventTimers |
| Gameplay/GInfractionManager.cpp | Ported | Pursuit.cs | PursuitStats infractions |
| Gameplay/GCharacter.cpp | Ported | ScriptedWorld.cs | DeleteObjects = plumbing |
| Gameplay/GMarker.cpp | Partial 0.9 | | GetShortcut = road owner (no retail shortcuts) |
| Gameplay/GState.cpp | Partial 0.95 | StategraphRuntime.cs | state-to-handler map = dictionaries |
| Lua/source/lapi.c | Partial 0.95 | Core/Lua/* | C API account lua_hooks §8.2; coroutines/debug/dump/GC-tuning unreachable |
| Lua/source/lstate.c | Partial 0.95 | Core/Lua/LuaState.cs | one state per runtime |
| Lua/LuaGameHooks.cpp | Partial 0.95 | LuaDrive.cs | SetFlag, Show/HideTriggerIcon, EResetProps; NoOps verified |
| Lua/LuaBindery.cpp | Partial 0.95 | LuaBindingTable.cs | binding tables |
| Lua/LuaRuntime.cpp | Partial 0.95 | LuaFlow.cs | debug dump / heap pager replaced |
| Lua/LuaAttributes.cpp | Partial 0.9 | AttribDatabase | Init registration only |
| Lua/LuaPostOffice.cpp | Partial 0.9 | StategraphRuntime.Post | subscriptions delivered directly |
| Sim/Activities/QuickGame.cpp | Partial 0.85 | GameFlow.cs | OnManageState/ShouldPauseInput (misc §6.5); HandleSkipFEOptions dead |
| Sim/Entities/LocalPlayer.cpp | Partial 0.8 | Input/Rumble.cs | DoFFB/CanDoFFB/OnCollision-FFB (no device in Unity) |
| Sim/Activities/GameplayActivity.cpp | Partial 0.8 | GameFlow.cs | no uncited functions remain |
| Sim/Activities/CareerGame.cpp | Partial 0.8 | GameFlow.cs | no uncited functions remain |
| Misc/GameFlow.cpp | Partial 0.95 | GameFlow.cs | loader stubs / HighAttribAlloc documented |
| Math/Common/SimRandom.cpp | Ported | Core/Sim/SimRandom.cs | 0x6272F0/0x633E70/0x627320/0x402850-90; 13 consumers wired |
| Misc/Rumble.cpp | Partial 0.95 | Input/Rumble.cs | DoFFB channels; no device by design |
| Input/Common/InputDefParser.cpp | Ported | Input/ActionQueue.cs, Core/InputActions.cs | 0x6479D0 over the controller attributes |
| Input/Common/actionqueue.cpp | Ported | Input/ActionQueue.cs | PLUGGED/UNPLUGGED/FLUSH = 135/136/137 |
| Libs/Support/Miscellaneous/Carp.cpp | Partial 0.95 | Core/Attrib.cs, EventSequencer.cs | hash32 = JenkinsHash; FastMem/VU0/tag resolvers replaced (misc §7) |
