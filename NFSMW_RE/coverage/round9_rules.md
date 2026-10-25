# Round 9 (final 11 %) — rules for every agent

Project: Need for Speed: Most Wanted (2005, PC, speed.exe v1.3) rebuilt in Unity 1:1, reading the original game files
at runtime. Root `D:\need for speed most wanted 2005`. Game data `D:\need for speed most wanted 2005\nfsmwfullinstall`
(GameData.DefaultRoot). Unity project `NFSMWUnity` (built-in RP, GAMMA colour space, LDR camera).

## Goal
Take every file of your area in `NFSMW_RE/coverage/remaining_round9.md` to fully ported, biggest first: compare the
Unity code against the exe function by function (symbols.json + decomp.sh / capstone) and port whatever behaviour is
missing. If a function is genuinely dead on PC (no caller, unreachable from shipped data, GC/PS2-only, memory
management Unity replaces), prove it (xrefs, data scan) and record it — that counts as done. Code that PhysX/Unity
replaces by design counts only if you document the exact deviation.

## Coverage — DO NOT edit NFSMW_RE/coverage/coverage.json or coverage.md
Concurrent read-modify-write of that file lost rows last round. Instead end your report with a table:
`source file | new status (Ported / Partial x.xx / N/A) | unity files | one-line note (addresses, what was done or why dead)`.
The main session writes the coverage rules from it.

## Tools
- `NFSMW_RE/tools/decomp.sh 0xADDR` (REA/Ghidra bridge, shared, may be slow — retry once). Disassembly: python +
  pefile + capstone on `<your speed.exe>` (plain `python`; never name a script dis.py).
- `refs/nfsmw/config/SPEED_EXE_1_3/symbols.json` = IDA export of this exact exe. Decomp (READ-ONLY, never run anything
  from refs/): `refs/nfsmw/src/Speed/Indep/Src/` (GameCube-based; the PC exe wins).
- Harness `NFSMWStaging/Harness` (`dotnet build -c Release`; `dotnet bin/Release/net8.0/Harness.dll "D:/need for speed most wanted 2005/nfsmwfullinstall" <mode>`).
  New probes go in your own file + one `case` line in Program.cs (others may add lines too: re-read before editing).
- `cd NFSMWUnity && ./check.sh` compiles all C# (not shaders). Must end with 0 errors in Runtime, Editor and Tests in
  your files (errors in other agents' in-progress files: ignore, mention).

## Hard rules
- NEVER launch Unity, `batch.sh`, `runtests.sh` or the built player — only the main session runs Unity.
- NEVER run any .exe in the game folders (nfsmwfullinstall has third-party nfsmwres.exe / lua5.1.dll: ignore them).
  Never modify, copy or convert game files. Never copy from backup folders. `refs/` is read-only. Don't kill processes.
- Edit only the files your brief lists; one-line hooks elsewhere are allowed only when unavoidable, and must be listed
  in your report.
- Keep public members other systems read. The play tests (Tests/*.cs) and the QA tour (Runtime/QaTour.cs) must keep
  working: read them first.
- 1:1 with the exe even when it looks rough (no prettifying). Code style: cite exe addresses / decomp file:line,
  `[?]` for guesses, `[ours]` for own additions; match the surrounding comment density.
- Known gotchas: Unity forbids native objects (Material, MaterialPropertyBlock, RenderTexture, CommandBuffer) in
  MonoBehaviour field initializers (create in Awake/OnEnable); collisions can have contactCount 0 (guard GetContact(0));
  WorldStreamer.Sections is filled by worker threads (enumerate under lock(Sections)); RIDE_HEIGHT is in inches;
  car renderers live on layer 29 (CarEnvironment.CarLayer); input code uses namespace NfsMw.Controls (an NfsMw.Input
  namespace would shadow UnityEngine.Input); cars carry EngineAudio's OnAudioFilterRead, so other AudioSources go on
  a child object. Bash heredocs break on C# quoting: write scripts to your scratch folder.

## Deliver
1. Specs updated (addresses, what you ported, deviations, [?] unknowns, dead-code proofs).
2. check.sh 0 errors in your files.
3. Final report: the coverage table above; files changed; one-line hooks elsewhere; unverified items; a numbered
   "what to check in play" list.
