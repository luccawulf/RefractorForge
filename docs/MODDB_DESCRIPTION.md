RefractorForge

A free, open-source map editor for Battlefield 1942 (2002) and Battlefield Vietnam (2004), written from
scratch. It reads the games' own .rfa archives directly, so it works with any mod you already have
installed - no exporting, no conversion, no setup.

Single self-contained .exe. No .NET install required. 64-bit Windows.

This is a BETA. It reads and writes real game files, so back up any map you care about before saving
over it.


START HERE - USE THE ORANGE "OPEN MOD" BUTTON

When RefractorForge opens you get a startup screen with four buttons. Use the orange "Open Mod" button.
It is by far the easiest way in:

1. Click Open Mod
2. Pick the mod folder (e.g. Battlefield 1942\Mods\bf1942, or Mods\Interstate, Mods\DesertCombat...)
3. Pick the map from that mod's list

That's it. Picking the mod first lets the editor resolve the mod's whole dependency chain - so a mod that
inherits from another (FHSW to FH to bf1942) loads everything, and the map comes up with all its objects,
vehicles and textures in place.

If you instead open a bare map .rfa on its own, it has no object or texture library behind it and half the
map will look empty. Open Mod avoids that entirely.

The other buttons: Open Project reopens a .rfproj you saved earlier, Open Level Folder opens an
already-extracted map folder, and New Map creates a fresh one.

There is also an English / 日本語 toggle on the startup screen.


CONTROLS

Camera:
- Move and strafe - W A S D
- Up and down - E and Q
- Look around - right-mouse drag
- Go faster - hold Shift
- Fly speed - mouse wheel
- Focus on selection - F
- Battlecraft-style camera - F7 (flies toward wherever you are looking)

Editing:
- Select / add to selection - left-click / Shift+click
- Move selection - arrow keys (hold Shift for coarse steps)
- Raise and lower - Alt + Up/Down
- Rotate - Alt + Left/Right
- Drop to ground - G
- Duplicate - Ctrl+D
- Delete - Delete
- Undo and redo - Z and Y
- Save - Ctrl+S
- Test in-game - Ctrl+L

Modes - the six tabs across the top, or F1 to F6:
F1 Terrain, F2 Material, F3 Object, F4 Surface, F5 Growth, F6 AI Path

Brush size is the mouse wheel in any paint mode. Right-click a slider to type an exact value.


WHAT IT DOES

- Terrain - sculpt, smooth and flatten; import and export Heightmap.raw
- Painting - ground materials, visual surface textures, under/overgrowth foliage, and the AI pathfinding grid
- Objects and gameplay - place, move, rotate and delete statics, control points, vehicle and soldier spawns,
  with Battlecraft-style property dialogs. Drag straight from the object list into the world.
- Object library - browse everything the mod ships, plus any custom objects the map itself contains, and
  double-click any of them to inspect the model in a 3D viewer
- Rendering - water, sky, fog, real-time sun shadows, baked object lightmaps, particle effect preview
- Baking - minimap and menu thumbnail, terrain shadows (LightmapShadowBits.lsb), per-object lightmaps
- AI - loads the level's shipped pathmaps so you can paint them, and rebuilds the strategic maps on save
- Saving - patch-first (Map_001.rfa), so the retail archive is never modified in place. Also
  server-side-mod (SSM) patches and plain folder saves. Every game mode a level ships gets updated.
- Japanese UI - fully translated. lang\ja.json is plain text keyed by the English string, so anyone can fix
  a phrase or add a language without rebuilding anything.
- Collaboration - experimental real-time multi-user editing over LAN or internet


KNOWN LIMITATIONS

- In-game validation is incomplete. Objects, lighting and saving have been exercised heavily, but the AI
  pathfinding regeneration has not been confirmed by watching bots path on an edited map. Treat AI output
  as experimental.
- The bundled TerrainTextures folder holds placeholders only - drop your own tileable textures into those
  category folders and they show up in the Texture Library.
- Collaboration has automated test coverage but limited real-world use.


CREDITS

Created by LuccaWulf, with engineering assistance from Claude (Anthropic).
Licensed under the GNU GPL v3. Source and full documentation on GitHub:
https://github.com/luccawulf/RefractorForge

Bundled FFmpeg is a GPL build; see ffmpeg\FFMPEG_NOTICE.txt for attribution and source.

Battlefield 1942 and Battlefield Vietnam are trademarks of their respective owners. This is an independent,
unofficial tool and is not affiliated with or endorsed by EA or DICE.
