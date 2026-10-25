# NFS Most Wanted (2005) — Unity rebuild

A Unity rebuild of Need for Speed: Most Wanted (2005) that runs on **your installed game's data**.
Rockport, the cars, the menus, the career, the sounds and the cutscene data are read from the game folder at runtime.
No game assets are copied into this project or into the build.

**Status:** a playable offline career. It starts at the press-start and main menus and carries a new career through the
car lot into Rockport. From there it covers:

- Blacklist races with AI opponents
- traffic and police pursuits with roadblocks
- the pursuit challenges (milestones) and bounty
- the Blacklist boss races, the garage, paint, performance and visual upgrades (body kits, spoilers, rims, hoods,
  roof scoops, vinyls)
- the 70-event Challenge Series with its preset cars and unlocks
- two-player split-screen races and LAN races between PCs
- the HUD with minimap, race intros driven by the game's own NIS car animation, the arrest sequence and police radio
- an offline save

[What is still missing](#not-done) is listed at the end. `../NFSMW_RE/REPORT.md` describes how each format was reversed.

## Play

**Standalone:** `Build\NFSMWUnity\NFSMWUnity.exe`. `gameroot.txt` next to it holds the game folder, the one with
`speed.exe`, `TRACKS`, `CARS` and `GLOBAL`. Edit that file, or start with `-gameRoot "C:\path\to\NFSMW"`.

**Editor (6000.4.2f1):** open `Assets/Scenes/FreeRoam.unity` and press Play. The **NFSMW** menu recreates the scene.
With `StartInFrontEnd` cleared, the `NFS Most Wanted` object drops straight into free roam with the car it names.

| Key | Action |
|---|---|
| W / Up | throttle |
| S / Down | brake, reverse when stopped |
| A D / Left Right | steer |
| Space | handbrake |
| N or Left Ctrl | nitrous |
| X | speedbreaker (30 mph or more; press again to end it) |
| R | reset the car |
| Esc | safehouse menu (career) or pause; skips cutscenes |
| Enter | menus: accept. In free roam: start the race whose marker you are stopped in |

Gamepad: triggers, left stick, A handbrake, B nitrous, right shoulder speedbreaker, Select reset.

**Split screen:** player 1 drives with W A S D, Space (handbrake), Left Shift (nitrous) and R (reset), plus the first pad.
Player 2 uses the arrows, Right Ctrl, Right Shift and Backspace, plus the second pad.

**LAN:** one PC picks *LAN Race > Host*, a car and an event. The others pick *Join* and a car, then press Enter on the
host they find. Racing starts when the host presses Enter and every PC has loaded. It uses UDP port 47805, so allow it
through the firewall.

## The career

- **Front end.** The game's own FEng screens drawn from `FrontB.lzc` with its fonts, textures and localised strings:
  press start, main menu, safehouse, car lot, my cars, customize, paint, performance, race events, Blacklist rival,
  rap sheet, milestones, options and dialogs. Screens play their APPEAR scripts like the game, and the console
  button help is hidden on PC. The car turns on a turntable inside the game's own showroom sets from
  `FRONTEND\PLATFORMS`: the car lot garage, the customization shop and the safehouse.
- **Options.** Master and music volume, mph or km/h, display mode, resolution and v-sync, kept in PlayerPrefs.
- **Story opening** (the game's own career scripts in gameplay.bin, see `NFSMW_RE/specs/career_flow.md`). A new
  career starts in bin 16 with no car and no cash, and goes straight into the prologue in the BMW M3 GTR:
  - **16.1.0:** a rolling start against Razor. Mia calls when you reach her trigger, then the race fades out.
  - **16.2.2, 16.2.3 and 16.1.1:** each pays $10,000. The "2 Days Ago" card appears before 16.1.1.
  - **16.2.1:** the D-Day race. The engine is sabotaged, so you lose.
  - Then the car lot with the four bin-16 cars, a spawn at the Rosewood car lot, and Mia's SMS with a GPS route to the
    safehouse. Arriving plays the safehouse intro and opens Blacklist #15.

  The story movies play when converted copies are in `MOVIES`. Otherwise their subtitles (`SUBTITLES\`) run over black.
  Text messages use the game's `SMS_MESSAGE_<id>` strings.
- **Progression** (`GLOBAL\gameplay.bin`). There are 16 Blacklist bins with their world races and boss races. Each bin
  sets its required race wins, bounty and challenges. Car-lot prices and unlocks come from `FE_ATTRIB.bin`.
  Rival cars are their preset rides from `GlobalB`. Saves are JSON in `%USERPROFILE%\AppData\LocalLow\…\nfsmw_career.json`.
- **Races.** Circuit, sprint, lap knockout, tollbooth, speedtrap and drag events. They use the game's start grids,
  checkpoints and lap counts, routed over the AI road graph in `L2RA.BUN`, with the event's opponents.
  - AI uses the game's catch-up formula over race completion, scaled by the event's difficulty.
  - A race opens with its intro sequence and a 3-2-1-GO countdown, and ends with the HUD race-over message and the
    `PostRace_Results` standings, with a laps or stages page on key 2.
- **Free roam.** Rockport streams around you with traffic. The safehouses, car lots and customization shops are the
  game's menu-gate zones: drive in and press Enter. Race starts are their engage triggers (radius 25 m); Enter opens the
  event dialog. Esc opens the pause menu: Resume, Jump to Safe House, Performance Tuning, Options.
  C cycles the camera (bumper, hood, close, far); B looks back.
- **Police** (attribute tables `pursuitlevels`, see `NFSMW_RE/specs/pursuit.md`):
  - **Heat** climbs continuously, 120–600 s per level depending on career progress.
  - **Busted:** the meter fills while you are slower than the heat's bust speed with a cop within 15 m that has seen
    you. It drains otherwise.
  - **Cooldown:** starts after 7 s unseen. You escape after the heat's evade time (20–120 s).
  - **Bounty:** paid every 10 s per heat level, plus takedown bounty with a combo of up to ×3. Only paid on evading.
  - **Cops and roadblocks:** the cop type and roadblock and spike odds come from the heat row. Getting busted plays the
    police video-cam arrest sequence.
- **Milestones and bounty.** Each pursuit is measured: length, escape time, cops damaged, bounty, roadblocks and spike
  strips dodged, cost to state and infractions. Those stats are scored against the current bin's challenges and shown
  on the game's `PostRace_Pursuit` screen. Challenges pay their bounty and are required for the boss race.
- **HUD.** The single-player `HUD_SingleRace` package (InGameB chunk 0x30203), driven like the exe's widgets
  (`NFSMW_RE/specs/hud.md`):
  - **Tach:** the needle sweeps 66° + 228°·rpm/dial. The dial (7000–10000) and its marking texture come from
    `HUDS_Custom_00.bin`, and the redline arc is a masked multi-image.
  - **Arcs:** nitrous and heat.
  - **Race information:** position, lap or complete, time, a four-row leaderboard and the countdown scripts.
  - **Pursuit board:** cops, takedowns, bounty, timer, and the EVADE/BUSTED and COOLDOWN bars.
  - **Messages:** the generic message box.
  - **Minimap:** the 64-tile city map from `MINI_MAP.BIN` at the package's map ring, with route, GPS, opponent, cop
    and event icons.
- **Visual customization.** Body kits KIT01–04 (unlocked as the Blacklist falls) and the car's own hood styles come
  from its geometry. Spoilers (normal and carbon, in the car's family) mount on the body's SPOILER marker and roof
  scoops (single, dual, offset, carbon) on ROOF_SCOOP. Rims from 11 brands come in 17–20 inch sizes, fitted to the
  tyre. About 140 vinyls per car are drawn over the paint. Browsing previews each part on the showroom car.
- **Challenge Series.** The 70 events of bin 19, each in its preset car (`PlayerCarType` → GlobalB presets):
  - Tollbooth time trials have a countdown clock with checkpoint time bonuses.
  - Pursuit challenges have a rolling start, cops on the event's spawn points and a forced heat level. You win by
    reaching the target and then escaping.
  - Each win opens the next events (`UnlockRaces`) and adds its car to Quick Race and Free Roam.
- **Split screen** (main menu). Each player picks a car, then the race. The screen splits with the game's
  `HUD_Player1/2` and a minimap each, and streaming follows both cars.
- **LAN races** (main menu), described under Play above. Remote cars are drawn from their streamed positions and
  appear in the standings and results.
- **Audio.** Granular engine sound from each car's GIN recordings, plus skids, nitrous, wind, road noise, shifts,
  impacts, sirens and menu sounds from the `.abk` banks.
  - Arrest sequences play the decoded `NISAudio.big` streams.
  - During pursuits a police scanner plays `copspeech.big` lines, decoded from EA MicroTalk, for pursuit start, heat
    increases, roadblocks, lost visual and arrests.
- **Cutscenes.** Race intros move the cars along the scene's own decoded animation:
  - Generic intros end on the grid.
  - Blacklist intros are in world space with up to eight cops.
  - Camera shots are staged, since the scenes carry no camera paths.

## Handling

`PlayerCar` runs speed.exe's racer simulation (`../NFSMW_RE/specs/physics.md`) on raycast wheels at 120 Hz.

- **Engine.** It uses the `TORQUE` curve over IDLE..MAX_RPM, with turbo or supercharger boost, `ENGINE_BRAKING`,
  nitrous (capacity in seconds, `TORQUE_BOOST`, recharge between 50 and 100 mph) and the rev limiter at `RED_LINE`.
- **Driveline.** A clutch model feeds an automatic gearbox. Shift points are computed from the torque curve.
  Each shift cuts torque for `SHIFT_SPEED`×ratio, and there is no upshift while the wheels spin.
- **Differentials and suspension.**
  - Limited-slip differentials use the `DIFFERENTIAL` lock values.
  - Springs are progressive, dampers are digressive with blow-out, and sway bars connect each axle.
- **Tires.** The load-sensitive lateral tables sit inside a `STATIC_GRIP` friction circle, with `DYNAMIC_GRIP` sliding and
  wheel-spin dynamics. Brakes lock the rear first (`BRAKE_LOCK`), and the handbrake works on the rear only.
- **Steering and stability.**
  - Steering tables shrink the lock with speed and open it up off the throttle, with countersteer and Ackermann geometry.
  - `YAW_CONTROL` stabilises the rear, and `YAW_SPEED` scales the yaw response.
  - The drift state changes rear grip and adds a stabilising torque.
  - Aero gives drag (doubled off the throttle) and downforce that grows linearly with speed.
- **Speedbreaker.** It slows world time to ×0.25 and lasts 10 s on a full bar. It refills at 100 mph or more and while
  drifting. While it runs the car gets more steering lock, more front grip and extra down force.
- **Upgrades.** Each part interpolates the attribute values between the pvehicle's part references (stock → top) by
  level. That is why a stock GTI has no nitrous until it is bought.
- **Traffic.** Traffic uses the game's fixed traffic constants.
- **Smashable props.** Props with scenery flag bit 30 can be knocked over: railings, benches, fences, signs, meters,
  traffic lights and parked junk cars. Light ones fly off when hit, and heavy ones only above about 34 mph.

`./batch.sh NFSMWPhysicsLab.Run lab` runs the handling lab on a flat pad: launch, 60-0, cornering, slalom, handbrake
turn and speedbreaker per car and upgrade level. `NFSMW_ARGS="-labcars gti -labstage 3 -labtrace"` narrows it down.

## Not done

- **Music.** `SOUND\PFDATA\MW_Music.mus` in this copy is all zeros (its map `MW_Music.mpf` is intact). With an intact
  `.mus` in its place, the soundtrack plays automatically, but that path is untested.
- **Movies.** `MOVIES` is empty in this copy. With converted copies there (`name_english.mp4` or `.webm`, for example
  from `ffmpeg -i x.vp6 x.mp4`), the EA logo, intro, story and tutorial movies play at their points. That path is also
  untested.
- **NIS cameras and characters.** The scenes have no camera paths, so shots are staged. Arrest characters are not drawn.
- **Radio sentences.** The scanner speaks each event's fixed lines. The `.evt` scripts that also slot in your car,
  colour and street are not decoded.
- **Online.** The original EA online service no longer exists; LAN play replaces it.
- **Handling extras.**
  - Junkman parts and the seven tuning sliders: the sim has the hooks, but the profile has no such parts or sliders yet.
  - Surface grip for grass and sand (`simsurface`).
  - Smashable props do not yet add pursuit cost (`smackable.COST_TO_STATE`).

## Code

| Piece | File |
|---|---|
| Chunks, hashes, JDLZ, EA HUFF | `Core/{Chunks,Jdlz,EaHuff}.cs` |
| Texture packs, solids, scenery, scenery groups, stream index, cars | `Core/{TexturePack,SolidList,Track}.cs` |
| Attribute database (VLT), career data, milestones | `Core/{Attrib,Career}.cs` |
| FEng packages, fonts, strings | `Core/FEng.cs` |
| Road graph | `Core/RoadNetwork.cs` |
| EA-XAS / EA-XA, sound banks, GIN engines, SCHl streams | `Core/Audio.cs` |
| EA MicroTalk speech codec (ported from vgmstream, ISC licence), police speech index | `Core/{MicroTalk,CopSpeech}.cs` |
| NIS scene headers, EAGL4 animation banks | `Core/Nis.cs` |
| Streaming, car assembly, physics, camera, bootstrap | `Runtime/{WorldStreamer,CarBuilder,PlayerCar,ChaseCamera,GameBootstrap}.cs` |
| Front end, screens, HUD, minimap | `Runtime/UI/*.cs` |
| Career flow, races, AI, traffic, police, pursuits, NIS playback, audio, car parts, LAN, movies and music | `Runtime/Game/*.cs` |

All paths are under `Assets/NFSMW/`. `Core/` has no Unity dependencies. `../NFSMWStaging/Harness` runs the same
readers over the game files, for example
`dotnet run -c Release -- <gameRoot> world|cars|career|roads|minimap <file>|sgroups|gpdump <name>|fetree <package>`.

## Tests and previews

```
./runtests.sh [TestClass]
```

`runtests.sh` calls Unity.exe directly; the `unity` CLI can mistake other projects' editors for this one.

- **`FreeRoamTests.StreamsAndDrives`** starts free roam and drives with a wall-avoiding autopilot.
- **`CareerTests.CareerRaceRuns`** starts a fresh career and checks traffic. It then starts the first Blacklist race,
  checks that the intro sequence plays (saving `Preview/nis_intro.png`), and lets the player and AI drive it for 35 s.
- **`ChallengeTests`** runs the first tollbooth (the clock counts down) and the first pursuit challenge (cops on their markers).
- **`SplitScreenTests`** runs a two-player race on autopilot and saves `Preview/splitscreen.png`.
- **`PhysicsTests`** drives a GTI on a flat pad. It checks the ride height, 0-60 mph, 60-0, a handbrake turn, a steady
  corner, the speedbreaker (30 mph gate, ×0.25 time, 0.1/s drain) and that upgrades change the car.
- **`LanTests`** covers the protocol between a host and a client over loopback, then a host race with a streamed
  remote car.

**QA tour.** `Build\NFSMWUnity\NFSMWUnity.exe -qa -screen-fullscreen 0 -screen-width 1280 -screen-height 720` walks
the whole game unattended and then quits. It covers:

- the front end and career driving
- every safehouse and shop screen
- an event marker and a race
- a forced pursuit and arrest, and a pursuit challenge
- split screen and the LAN screens

Screenshots and `qa.log`, which lists any exceptions per step, go to `Build\NFSMWUnity\QA\`. The tour uses the save
slot `qa`.

Batch previews go through `./batch.sh <Class.Method>` and write into `Preview/`:

| Method | Renders or checks |
|---|---|
| `NFSMWSetup.BatchPreview` | world views |
| `NFSMWFlowPreview.Render` | every menu screen |
| `NFSMWFlowPreview.RideCheck` | wheel and body heights, with side views of a few cars |
| `NFSMWFlowPreview.Hud` | HUD and minimap in game |
| `NFSMWFlowPreview.NisCheck` | NIS headers, NIS audio and police speech decode |
| `NFSMWFlowPreview.Visual` | customised cars in the showroom |
