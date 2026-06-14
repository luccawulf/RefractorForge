# RefractorForge

**A from-scratch, open-source native Windows map editor for Battlefield 1942 (2002) and Battlefield
Vietnam (2004).**

> ⚠️ **Beta software.** RefractorForge is under active development and not yet feature-complete. It
> reads and writes real game files — **always keep a backup of any map you care about before saving
> over it.** Expect rough edges.

**DOWNLOAD** - https://www.mediafire.com/file/oqr1hub1e9if6f3/RefractorForge_BETA.zip/file

*See below for build instructions!*

RefractorForge opens a level, renders it in real time (terrain, real object meshes, water, sky,
lighting), and lets you edit it the way Battlecraft does — but without Battlecraft's structural limits
(the 1024-template / 2048-object / 4096-metre walls are artifacts of one fixed-size 2004 in-memory
struct; they don't exist in the map *files*, which are plain-text `.con` plus raw heightmaps). A tool
that works directly on those files inherits none of those limits.

It ships as a single self-contained `RefractorForge.Viewer.exe` and reads the game's own assets at
runtime, with a dark, Battlecraft-style Dear ImGui interface.

## Features

- **Real-time 3D editor** — terrain mesh, real StandardMesh / TreeMesh object geometry, textured/
  procedural water, skybox, fog, animated clouds, and a free-fly camera.
- **Objects** — place, move, rotate, scale, duplicate, delete, multi-select, prefab stamps, and a
  double-click 3D model viewer. Lossless save (untouched objects stay byte-verbatim).
- **Gameplay (Conquest)** — control-point flags, vehicle spawns and soldier spawns with full,
  Battlecraft-style edit dialogs (double-click a marker), including the BF1942 control-point fields.
- **Terrain & painting** — heightmap sculpt/smooth, gameplay material painting, visual surface texture
  painting (with a custom texture library), under/overgrowth foliage, a spline **road tool**, and AI
  pathfinding ("search map") painting + generation.
- **Lighting** — real-time sun control + shadow mapping, plus **bake-to-game**: terrain sun shadows
  (`LightmapShadowBits.lsb`, byte-exact), per-object lightmaps, and minimap generation.
- **Environment** — water colour/level/textures, sun azimuth/elevation, fog, skybox import, animated
  clouds, weather preview.
- **Sound** — preview placed ambient sounds as you fly through their rings.
- **Pipeline** — RFA archive read/write (LZO1X), `.con` parsing/writing, new-map creation, save-as-
  patch `.rfa`, `.obj` import, TGA→DDS conversion, map validation, and real-time **collaboration**
  (multi-user editing).

## Documentation

- **[USER_GUIDE.md](USER_GUIDE.md)** — full controls + feature walkthrough.
- In the app: **Help ▸ User Guide / Controls**.
- **For contributors** — reverse-engineered file-format notes in [`docs/`](docs/):
  [validated format facts](docs/Validated_Format_Facts.md),
  [RFA archive](docs/RFA_Format_Notes.md),
  [StandardMesh collision](docs/SM_Collision_RE.md),
  [skeletal animation](docs/Skeletal_Animation_Format.md).

## Build & run

Requires the **.NET 8 SDK** (Windows).

```
dotnet build -c Release                                    # build the whole solution
dotnet run --project src/RefractorForge.Viewer -c Release  # launch the editor
```

The editor prompts for a level (folder or `.rfa`) on first launch, or use **File ▸ Open Level / .rfa…**
/ **File ▸ Open Mod…**. There's also a headless validation harness:

```
dotnet run --project src/RefractorForge.Demo -c Release -- <subcommand> …
dotnet run --project src/RefractorForge.TerrainTests -c Release          # terrain-math regression tests
```

> **Note:** game content is **not** included in this repository (it's copyrighted). RefractorForge
> reads the assets from your own installed copy of Battlefield 1942 / Battlefield Vietnam at runtime.
> Bink video playback (`.bik`) additionally needs FFmpeg, which you supply locally.

## Project layout

- `src/RefractorForge.Formats` — file formats: RFA (LZO1X), `.con` parsing/writing, terrain, editing/undo. Zero external deps.
- `src/RefractorForge.Render` — engine-agnostic geometry, mesh/texture libraries, minimap, terrain shadow.
- `src/RefractorForge.Viewer` — the editor app (Silk.NET OpenGL + WinForms host + Dear ImGui).
- `src/RefractorForge.Demo` / `src/RefractorForge.TerrainTests` — headless validation harnesses.

## License

Licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).
Copyright © 2026 Lucas Ludwiczak.

## Acknowledgments

Created by **Lucas Ludwiczak**, an experienced BF1942/BFV modder, with substantial engineering
assistance from **Claude (Anthropic)** — design direction, file-format reverse-engineering, testing,
and the in-game validation that made it work were the author's.

Built on the open-source [Silk.NET](https://github.com/dotnet/Silk.NET),
[ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) and [NAudio](https://github.com/naudio/NAudio)
(all MIT-licensed). Battlefield 1942 and Battlefield Vietnam are trademarks of their respective owners;
this project is an independent, unofficial tool and is not affiliated with or endorsed by them.
