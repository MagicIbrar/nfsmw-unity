# Modding the NFS Most Wanted rebuild

The rebuild reads Most Wanted's data from your own install while it runs. To mod it, you export the original
textures and models into standard formats (PNG and OBJ), edit them, and put the results in a **Mods folder**. The
rebuild then loads your files in place of the originals.

**The game's files are never changed.** The mod tool only reads the game folder and refuses to write anywhere inside
it. To undo a mod, delete its file from the Mods folder (or delete the whole folder).

## Where the Mods folder is

| Run | Mods folder |
|---|---|
| Built player | `Mods` next to `NFSMWUnity.exe` (for example `NFSMWUnity/Build/NFSMWUnity/Mods`) |
| Unity editor | `NFSMWUnity/Mods`, next to `Assets` |
| Either | `-mods "C:\path\to\Mods"` on the command line overrides both |

The rebuild scans the folder once at start-up. Restart after adding or removing files. Every replacement it applies
is written to the log once, for example
`NFSMW mods: texture BMWM3GTRE46_BADGING <- ...\Mods\textures\CARTEXTURES\BMWM3GTRE46_BADGING.png`. If there is no
Mods folder, the game behaves exactly as before: the only cost is one check that the folder exists.

## Naming rules

```
Mods/
  textures/
    <PackName>/<TextureName>.png        replaces the texture in that pack only
    <TextureName>.png                   replaces it in every pack that has it
    ...<TextureName>@<HASH>.png         either of the above, for one texture only (see below)
  models/
    <SolidName>.obj                     replaces that model (solid) wherever it is loaded
```

- Names are not case-sensitive. A pack folder beats the shared folder, and a `@HASH` file beats a plain name.
- `<PackName>` and `<TextureName>` are the names that `list` and `export-textures` print, for example
  `CARTEXTURES/BMWM3GTRE46_BADGING`, `GLOBAL/LICENSEPLATE` or `Region/ARC_PETROTECHSIGN`. The export folder layout is
  already `<PackName>/<TextureName>.png`, so an exported file can be copied straight across.
- The game stores only the first 24 characters of a texture name, so a few different textures share one name. Vinyls
  and some world textures are affected: `BMWM3GTRE46_BODYFLAME01` is two textures. The exporter saves those as
  `<Name>@<HASH>.png`, where HASH is the 8-hex-digit name hash. Keep that file name so only that texture is replaced.
- Several world packs are named `Region`, so a file in `textures/Region/` applies to the streamed world wherever that
  texture appears.
- `<SolidName>` is the name that `list` and `export-models` print, for example `BMWM3GTRE46_KIT00_BODY_A`.

## The mod tool

`NFSMWStaging/ModTool` is a .NET 8 console program. It is built from the same `Core` code the game uses, so it decodes
the files exactly as the rebuild does.

```
cd NFSMWStaging/ModTool
dotnet build -c Release
bin/Release/net8.0/ModTool.exe            (prints the help)
```

| Command | What it does |
|---|---|
| `list <gamefile> [filter] [--with-mods]` | lists the textures (name, pack, size, format, mips, alpha) and solids (parts, vertices, triangles) in a game file. With `--with-mods`, entries replaced by the Mods folder are marked `[mod]`. |
| `export-textures <gamefile> <outdir> [filter]` | writes every texture to `<outdir>/<Pack>/<Name>.png` (RGBA, top mip), plus `textures.json` (name, hash, pack, size, original format, mips, alpha usage and blend, tiling) |
| `export-models <gamefile> <outdir> [filter]` | writes every solid to `<outdir>/<Solid>.obj` and `.mtl`, plus `models.json`. Textures held by the same game file go to `<outdir>/textures/...`, and the MTL's `map_Kd` points at them, so the model opens textured. |
| `import <file.png\|file.obj> [--name X] [--pack P] [--from <gamefile>] [--force]` | checks the file and copies it to the right place in the Mods folder. `--name` defaults to the file name. `--pack` (textures only) limits the replacement to one pack. With `--from`, the tool checks that the name exists in that game file and reports the original's size, format and alpha (textures) or how each OBJ material maps onto the original parts (models). `--force` installs the file even if the name is not found. |
| `mods` | lists what the Mods folder overrides, and flags broken files |
| `diff <gamefile> [filter]` | reads a game file twice, without and with the Mods folder, and shows what each override changes (size, format, mips; parts, vertices, triangles, bounds) |
| `verify-models <gamefile> [filter]` | runs every solid through OBJ export and re-import in memory and reports any differences. No files are written. |

Common options:

- `--game <folder>` is the game folder. Without it, the tool tries `gameroot.txt` in the current folder, next to the
  tool, in `NFSMWUnity` and in `NFSMWUnity/Build/NFSMWUnity`, then the `NFSMW_ROOT` environment variable, then the
  default install path.
- `--mods <folder>` is the Mods folder. Without it, the tool uses `NFSMW_MODS`, then `NFSMWUnity/Mods` when it runs
  inside the project tree, then `Mods` next to the tool. To mod the built player, pass
  `--mods NFSMWUnity/Build/NFSMWUnity/Mods`.

A `<gamefile>` path is relative to the game folder:

| What | `<gamefile>` |
|---|---|
| One car (geometry and textures) | `CARS/BMWM3GTRE46` (a folder means all of its `.BIN` files) |
| Shared car textures, wheels | `CARS/TEXTURES.BIN`, `CARS/WHEELS` |
| Front end, HUD, global effects | `GLOBAL/GLOBALB.LZC`, `GLOBAL/InGameB.lzc`, `GLOBAL/HUDTEXRACE.BIN`, `FRONTEND/FrontB.lzc` |
| One section of the streamed world | `TRACKS/STREAML2RA.BUN:<section>`, for example `TRACKS/STREAML2RA.BUN:D46`. Run `list TRACKS/STREAML2RA.BUN` to see the sections. Most world textures are in section `Y0` and many shared world models are in `X0`. |

## Examples

### A car texture

```
> ModTool export-textures CARS/BMWM3GTRE46 C:\nfsmods\bmw
exported 290 textures to C:\nfsmods\bmw
  78 textures share their (24-character) name with another in the same pack: they are saved as <Name>@<HASH>.png; keep that name when importing

  (edit C:\nfsmods\bmw\CARTEXTURES\BMWM3GTRE46_BADGING.png in any image editor and keep the alpha channel)

> ModTool import C:\nfsmods\bmw\CARTEXTURES\BMWM3GTRE46_BADGING.png --pack CARTEXTURES --from CARS/BMWM3GTRE46
C:\nfsmods\bmw\CARTEXTURES\BMWM3GTRE46_BADGING.png: 512x512 with alpha
  replaces CARTEXTURES/BMWM3GTRE46_BADGING: 256x256 Dxt3, alpha usage 2
  warning: size differs from the original 256x256 (fine for most textures; FE/HUD art may expect the original aspect)
installed ...\NFSMWUnity\Mods\textures\CARTEXTURES\BMWM3GTRE46_BADGING.png

> ModTool diff CARS/BMWM3GTRE46
texture CARTEXTURES/BMWM3GTRE46_BADGING 0x23EF5D6C: 256x256 Dxt3 mips 1 -> 512x512 Bgra32 mips 10 (usable 10, 1398100 bytes); alpha 2/1 tile 0
```

### A world texture

World textures are mostly in section `Y0`:

```
> ModTool list TRACKS/STREAML2RA.BUN:Y0 PETROTECH
  ARC_PETROTECHSIGN                Region                     128x128   Dxt1    mips  5  alpha 0/0
> ModTool export-textures TRACKS/STREAML2RA.BUN:Y0 C:\nfsmods\world PETROTECH
> ModTool import C:\nfsmods\world\Region\ARC_PETROTECHSIGN.png --from TRACKS/STREAML2RA.BUN:Y0
installed ...\Mods\textures\ARC_PETROTECHSIGN.png
```

The shared `LICENSEPLATE` texture in `GLOBAL/GLOBALB.LZC` works the same way, with
`import LICENSEPLATE.png --from GLOBAL/GLOBALB.LZC`.

### A car part model

```
> ModTool export-models CARS/BMWM3GTRE46 C:\nfsmods\bmwmodels BODY_A
exported 1 models and 4 textures to C:\nfsmods\bmwmodels

  (open BMWM3GTRE46_KIT00_BODY_A.obj in Blender: File > Import > Wavefront (.obj) with the default axes;
   edit; File > Export > Wavefront (.obj), with "Materials" and "Vertex colors" ticked if you want the
   baked colours kept, and the default axes again)

> ModTool import C:\nfsmods\bmwmodels\BMWM3GTRE46_KIT00_BODY_A.obj --from CARS/BMWM3GTRE46
...: 9343 positions, 9343 uvs, 9343 normals, 8295 faces (8295 triangles), 16 materials, vertex colours
  replaces BMWM3GTRE46_KIT00_BODY_A: 16 parts, 9343 vertices, 8295 triangles
    usemtl 0_BODY_ALUMINUM_BMWM3GTRE46_MISC         -> settings of part 0 (BODY_ALUMINUM, effect 4): 374 vertices, 294 triangles
    ...
installed ...\Mods\models\BMWM3GTRE46_KIT00_BODY_A.obj
```

A car has one solid per kit, part and LOD: `_A` is the closest LOD and `_B` to `_D` are further away. Replace `_A`
for the showroom and close-up views, and the others too if the change should hold at a distance.

## Models in detail

**Coordinate frame.** The game is right-handed with Z up. OBJ files are written right-handed with Y up:

- position: OBJ `(X, Y, Z)` = game `(x, z, -y)`. Import does the inverse: game `(x, y, z)` = OBJ `(X, -Z, Y)`.
  This is a rotation, not a mirror, so nothing comes out flipped. With the default OBJ axes (forward -Z, up Y),
  Blender shows the model in the game's own coordinates.
- normals: rotated the same way.
- texture coordinates: OBJ `vt (u, 1 - v)`, because the game's V runs down the image and OBJ's runs up.
- vertex colours: `v x y z r g b` (0 to 1). This is the game's baked vertex lighting, which world meshes depend on.
  The importer also accepts 0 to 255.
- Positions are written as shortest round-trip floats. An unedited export and import reproduces every part's vertices,
  triangles, UVs, normals and colours exactly (`verify-models`).

**Materials.** Each original part (one material with its own texture and shader) is one `usemtl` group named
`<part index>_<MaterialName>_<texture name>`, for example `12_BADGING_BMWM3GTRE46_BADGING`. On import, each `usemtl`
group becomes one part:

- a name as exported takes the settings of that part: shader effect, flags, texture slots and light material
- a group renamed to another part's name, with or without the index, borrows that part's settings. This is how to
  give new geometry an existing material.
- any other name takes part 0's settings

A group cannot bring in a new texture. Use an existing part's material and replace that material's texture with a PNG
instead. Texture names such as `BMWM3GTRE46_SKIN1` are built while the game runs (paint and vinyls), so no file holds
them and they cannot be replaced by a PNG.

**What import fills in.**

- Polygons are split into triangles.
- Missing normals are generated as smooth, area-weighted normals.
- Missing UVs become (0, 0).
- Without vertex colours, every vertex gets the average colour of the original model. White would make baked-lit
  world meshes too bright.
- Each part keeps the original part's front-face winding.
- Tangents are generated for the normal-mapped world shaders that use them. They match the game's own tangents on
  99.7% of vertices.
- The model's name, hash, placement matrix, markers (headlight and spoiler mounts and so on) and LOD numbers are kept.
  Its bounding box is recomputed with the original's margin.

## Limits

- **Skinned characters are not covered.** The NIS cutscene characters (`NisContent` skinned solids) use a different
  loader, which ignores `Mods/models`. Their textures can still be replaced.
- **Duplicate texture names.** Without `@HASH` or a pack folder, a file replaces every texture with that name in every
  pack. `export-textures` and `import --from` point these cases out.
- **Fixed sizes.** Some textures must keep their size:
  - vinyls and their `_MASK` are composited at 512x512, and at another size the vinyl is skipped
  - environment cube faces (`...POS` / `...NEG`) must all be the same square size
  - front-end and HUD art is laid out for the original aspect ratio

  Other textures can be any size. Powers of two give the best mip maps and tiling.
- **Opaque textures stay opaque.** Whether a texture is drawn opaque, alpha-tested or blended still comes from the
  original texture's settings (`alpha` in `list`). Painting alpha into a texture that was opaque does not make it
  transparent.
- **Texture formats.** Replacement textures load uncompressed (BGRA32 with generated mips), so they use more memory
  than the original DXT textures.
- **PNG support.** 8-bit and 16-bit greyscale, RGB, palette (with transparency), grey+alpha and RGBA. Interlaced PNGs
  are refused: save without interlacing.
- **Collision is unchanged.** World collision comes from separate collision data, so a reshaped world model keeps
  its old collision.
- **Restart to see changes.** Files added while the game runs are picked up after a restart.

## Keeping game content out of the repository

Exported textures and models are the game's content and must not be committed. Keep export folders outside the
project, and when a git repository is set up, ignore these:

```
# .gitignore
Mods/
NFSMWUnity/Mods/
NFSMWUnity/Build/NFSMWUnity/Mods/
exports/
NFSMWStaging/*/bin/
NFSMWStaging/*/obj/
*.obj
*.mtl
```

## Code

- `NFSMWUnity/Assets/NFSMW/Core/Mods.cs`: the Mods folder index and the texture override (`Mods`), the OBJ frame
  (`ObjFrame`), the OBJ parser (`ObjModel`) and the model override (`ModelOverride`). It has no Unity dependency.
- `NFSMWUnity/Assets/NFSMW/Core/Png.cs`: the PNG decoder and encoder.
- Hooks: `TexturePack.Read` calls `Mods.ApplyTexture` on every decoded texture, and `SolidList.ReadSolid` calls
  `ModelOverride.Apply`. Both are skipped when there is no Mods folder.
- `NFSMWUnity/Assets/NFSMW/Runtime/GameData.cs` (`ResolveMods`) finds the folder and sends log messages to `Debug.Log`.
- `NFSMWStaging/ModTool`: the command-line tool.
