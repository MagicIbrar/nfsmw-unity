| source file | new status | unity files | note |
|---|---|---|---|
| FEng/FEKeyTrack.cpp | Ported | Core/FEng.cs, UI/InGame/FEngEngine.cs, UI/FEView.cs | GetKeyAt/GetDeltaKeyAt; per-node base keys; key editing 0x5B5900/0x5C0830-0x5C0B70/0x5C1F20 no xrefs (dead) |
| FEng/FEMessageResponse.cpp | Ported | Core/FEng.cs, FEngEngine.cs | FindConditionBranchTarget 0x5B0140; unused MR ids dead by data scan |
| FEng/FEPackage.cpp | Ported | FEngEngine.cs, FEView.cs | Update/UpdateObject/UpdateObjectTracks/IssueScriptMessages 0x5C1B30/0x5C17E0/0x5BEC90/0x5BC530; library refs dead |
| FEng/FEObject.cpp | Ported | FEngEngine.cs, FEView.cs | SetScript 0x5B8400/0x5BC160, SetTrackValue, Clone 0x5C31A0 |
| FEng/FEKeyInterpLinear.cpp | Ported | Core/FEng.cs | clamp/loop; ping-pong/interp 0 dead by data |
| FEng/FEMsgTargetList.cpp | Ported | Core/FEng.cs, FEngEngine.cs | Targ in all 199 packages; runtime builders dead |
| FEng/FEString.cpp | Ported | FEngEngine.cs, FEView.cs, Core/FEng.cs | Printf Sb cut, SetLanguageHash, flag-2 literal |
| FEng/FEScript.cpp | Ported | Core/FEng.cs | FindTrack 0x5B2910 |
| FEng/FEGroup.cpp | Ported | FEView.cs | tree walks |
| FEng/FEButtonMap.cpp | Ported | FEngEngine.cs | wrap mode only 0 (0x5C13F1) |
| Frontend/HUD/FEPkg_Hud.cpp | Partial 0.95 | GameHud.cs, HudFeatures.cs, HudExtras.cs | ctor/SetHudFeatures/Determine/WideScreen/custom HUD exact; FadeAll caller [?], RaceOverMessage pending bit |
| Frontend/HUD/FeMinimap.cpp | Ported | Runtime/UI/Minimap.cs | 0x59DB50/0x579900/0x5675D0/0x579E00 |
| Frontend/HUD/FeHudElement.cpp | Ported | HudFeatures.cs | Register* + Toggle 0x5793F0 |
| Frontend/HUD/FePursuitBoard.cpp | Ported | HudWidgets.cs, HudPursuitFeed.cs, GameHud.cs | Update 0x57AEE0 + feeder 0x6F17F2 |
| Frontend/FERenderObject.cpp | Ported | FEView.cs, FEngRender.cs, UIMultiImage.shader | clipping dead (0x585320) |
| Frontend/FEngRender.cpp | Partial 0.95 | FEView.cs, FEText.cs, FEngRender.cs | colour compose, alpha-0 skip, squeeze, flag 0x10; movie objects draw no video surface |
