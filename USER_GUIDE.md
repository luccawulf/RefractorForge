# RefractorForge — User Guide

A native Windows map editor for **Battlefield 1942 (2002)** and **Battlefield Vietnam (2004)**.
RefractorForge opens a level, renders it in real time, and lets you edit terrain, textures, foliage,
objects, gameplay (flags / spawns), lighting and environment, then save back to the game's own files.

> **Beta software.** RefractorForge is under active development. It edits real game files — keep
> backups of any map you care about before saving over it.

This guide is also available inside the editor: **Help ▸ User Guide / Controls**.

---

## 1. Getting started

1. Launch `RefractorForge.Viewer.exe`.
2. **File ▸ Open Level / .rfa…** — pick either an *unpacked* level folder or one or more `.rfa`
   archives (base + patches). For a modded map, use **File ▸ Open Mod…** and pick the mod folder; the
   editor mounts the mod's archives plus its dependency chain.
3. Or **File ▸ New Map…** to start a fresh level (see §9).
4. Pick your engine in the Inspector's **Target game** dropdown (Battlefield 1942 or Battlefield
   Vietnam). This sets team names (Axis/Allies vs NVA/US) and enables/disables engine-specific
   features (e.g. overgrowth).

Your BF1942/BFV levels typically live under the game's `Mods\<mod>\Archives\…\levels` folders.

---

## 2. The interface

- **Menu bar** (top): File, Edit, Object, Tools, Terrain, View, Layer, Window, Collab, Help.
- **Tool ribbon** (below the menu): New / Open / Save · the six **mapper tabs** · Undo / Redo ·
  **Grid**, **Labels**, **Snap**, **Map** toggles.
- **Object Library** (left): a searchable tree of every placeable object, grouped by category
  (Structures, Vegetation, Land/Water/Air Vehicles, Stationary Weapons, Hand Weapons, Soldiers,
  Effects, Props, Gameplay, Sounds, …).
- **Mini-Map** (floating): top-down overview; **Refresh** to rebake. Toggle with the **Map** button.
- **Inspector** (right): context panel — the selected object's transform, the **Layers** visibility
  list, and the level's environment settings (water, sun, fog, sky, clouds).

### Mapper modes (the six ribbon tabs — hotkeys F1–F6)

| Mode | Key | What it does |
|---|---|---|
| **Terrain** | F1 | Sculpt & smooth the heightmap |
| **Material** | F2 | Paint the ground *material* type (gameplay surface: footsteps, collision) |
| **Object** | F3 | Place & edit objects, vehicles, control points, spawns |
| **Surface** | F4 | Paint the *visual* terrain textures |
| **Growth** | F5 | Paint under/overgrowth foliage |
| **AI Path** | F6 | Paint the AI pathfinding grid (passable / blocked) |

---

## 3. Camera & view controls

- **Right-mouse drag** — look around (yaw + pitch).
- **W A S D** — move; **Q / E** — down / up.
- **Hold Shift** — move faster.
- **Mouse wheel** — change fly speed (and, when a brush tool is active, resize the brush).
- **Fly speed** slider — in the Inspector's Camera section (0.1×–8×).
- **Grid / Labels / Map** toggles — ground grid, coordinate labels, mini-map.

---

## 4. Editing objects (Object mode, F3)

### Selecting & transforming
- **Left-click** an object to select; **Shift-click** to add to / toggle the selection.
- **Move tool** — drag the selection across the terrain.
- **Rotate tool** — drag to spin yaw.
- **Scale tool** — drag to scale.
- **Place tool** — with a template chosen in the Object Library, click the terrain to drop a copy.
- The Inspector shows **Position / Rotation / Scale** for the selection — drag, or **right-click a
  field to type an exact value**.

### Keyboard nudging (with object(s) selected)
- **Arrow keys** — move on X/Z in fine **0.5 m** steps.
- **Shift + arrows** — coarse step (one terrain sample).
- **Alt + Up/Down** — raise / lower (Y).
- **Alt + Left/Right** — rotate yaw (3°, or 15° with Shift).
- **Delete** — remove the selection · **Z / Y** — undo / redo · **F** — focus camera on selection.
- **Object ▸ Duplicate** clones the selection; **Object ▸ Drop to ground** snaps it to terrain height.

### Snapping
Turn on **Snap** in the ribbon to grid-snap placement/moves; set the step in the box beside it
(right-click to type an exact value).

### The Object Library & model viewer
- **Search** to filter; **single-click** a template to arm it for the Place tool.
- **Double-click** a template to open a **3D model viewer** (auto-rotates; drag to orbit, scroll to
  zoom) — handy for checking a mesh before placing it.

### Prefabs (multi-object stamps)
Select several objects, then **Edit ▸ Save Selection as Prefab…**. Saved prefabs appear in the Object
Library and stamp the whole group with one click.

---

## 5. Gameplay editing — flags & spawns (Object mode)

Control points, vehicle spawns and soldier spawns show as 3D markers (with the flag pole + cloth for
control points). To edit one:

- **Click** to select it (the Inspector shows its key fields), **or**
- **Double-click** it to open the full **Battlecraft-style dialog**:
  - **Edit Control Point** — name, control-point name, position, capture radius, team, area value,
    spawn group / object-spawner id, and (BF1942) the timing/behaviour flags: time to get / lose
    control, disable-if-enemy-inside, disable-when-losing, lose-control-when-enemy-close /
    when-not-close, unable-to-change-team, only-takable-by-team, has-collision-physics.
  - **Edit Object Spawn** (vehicle) — name, position, rotation, OS id, team.
  - **Edit Soldier Spawn** — name, spawn group, spawn id, spawn-as-paratrooper, position, rotation.
- Drag a marker to move it, or drag the rotation arc to spin a spawn's facing.
- **Spawn Links** (Layers) draws lines from each spawn to the control point that owns it.

Saving writes the instance files (ControlPoints / ObjectSpawns / SoldierSpawns `.con`) and patches the
template files **surgically** — fields you didn't touch are preserved byte-for-byte.

---

## 6. Terrain, textures & foliage

- **Terrain (F1)** — Sculpt (raise / lower / flatten) and Smooth. **Scroll** resizes the brush; pick a
  brush shape/falloff in the panel. Each drag is one undo step.
- **Material (F2)** — paint the gameplay material index (footstep sounds, surface type). Pick a slot
  from the material palette, then drag.
- **Surface (F4)** — paint the *visual* terrain textures. Paint from the 16-slot palette or from the
  **Texture Library** (bundled categories + your own — drop `.dds/.tga/.png/.bmp` into the
  `TerrainTextures` folder beside the exe). A **Hardness** slider controls the soft edge. Textures bake
  to DDS on save.
- **Growth (F5)** — paint under/overgrowth foliage from the level's `.wst` palette. The Tools menu has
  Save / Export Overgrowth and **Bake Overgrowth → StaticObjects.con**.
- **AI Path (F6)** — paint the pathfinding grid passable/blocked, per vehicle type. **File ▸ Generate
  AI Navmaps** regenerates them from the terrain.
- **Road tool** (Tools ▸ Road tool) — click to drop spline points, drag to shape; set width / edge
  softness / flatten + shoulder / texture orientation, then **Stamp Road** to paint it onto the terrain
  and material. Points persist so you can tweak and re-stamp.
- **Terrain ▸ Import / Export Heightmap.raw** — round-trip a 16-bit LE square `.raw`.

---

## 7. Environment & lighting (Inspector)

- **Water** — level, surface colour, deep colour, transparency; optional scrolling **Textured** water
  (BF1942) or procedural colour (BFV). Import water textures if the engine's built-ins aren't shipped.
- **Sun** — tick **Control sun manually** to drive azimuth / elevation; the real-time shadow map
  follows. **Sun Shadows (real-time)** in Layers toggles the display.
- **Fog** — colour, start / end distance.
- **Sky** — use the level's cubemap, set sky rotation, or **Import skybox…** (6 faces named `…_01`–`_06`).
- **Animated Clouds** — coverage, scale, drift X/Y, colour; import a cloud texture or mesh.
- **Lighting bakes** (write back to the game):
  - **File ▸ Bake Sun Shadows** — terrain sun-shadow lightmap (`LightmapShadowBits.lsb`).
  - **Tools ▸ Bake Object Lightmaps (from sun)** — per-object lightmaps (`ObjectLightMaps/*.tga`).
  - **File ▸ Generate Minimap** — the in-game map + thumbnail.
  - **Object Lightmaps** / **Sun Shadows** in Layers toggle their display.

---

## 8. Sound

Placed ambient sounds appear as markers with their audible (minDistance) rings. Select a sound emitter
to edit its `.ssc` (wav, volume, min distance, loop, stereo) in the Inspector. Tick **Sounds ▸ Play**
to preview: fly into a ring and the clip plays through once at distance-faded volume, then stops when
you leave.

---

## 9. New map (File ▸ New Map…)

Set the output folder, **material size** (256/512/1024/2048), **world size**, **Y scale** and **water
level**, then a terrain type — Flat, Rolling Hills, Mountains, Islands, or **Import .raw** — with the
relevant parameters (seed / roughness / min–max height, or the `.raw` file). Choose the **Game** and
tick **Playable** to seed Conquest flags, spawns and kits. **Create** restarts the editor on the new map.

---

## 10. Saving, testing & exporting

- **Save** (Ctrl+S) — write the level back (folder writers, or loose files beside a `.rfa`).
- **File ▸ Save as Patch .rfa…** — export only the changed files as an overlay `.rfa`.
- **File ▸ Test This Level (in-game)** (Ctrl+L) — save, then launch the game on this mod (pick the map
  in-game — the client can't be told which map to load from the command line).
- **File ▸ Import .obj…** — bring in a Wavefront model as a static object (and export it back as `.sm`).
- **Tools ▸ Convert TGA → DDS** (single or batch), **Validate map…** (missing-asset check),
  **Scatter Objects…** (random placement).

---

## 11. Collaboration (Collab menu)

Host or join a live session to edit the same map with others in real time. Edits broadcast to all
peers; connected peers are listed with a "jump to" link to their camera. **Collab ▸ Disconnect** ends it.

---

## 12. Keyboard reference

| Action | Key |
|---|---|
| Mapper modes | **F1**–**F6** |
| Save | **Ctrl+S** |
| Test level in-game | **Ctrl+L** |
| Duplicate selection | **Ctrl+D** (Object menu) |
| Drop to ground | **G** (Object menu) |
| Undo / Redo | **Z** / **Y** |
| Delete selection | **Delete** |
| Focus camera on selection | **F** |
| Move selection (fine 0.5 m) | **Arrow keys** |
| Move selection (coarse) | **Shift + Arrows** |
| Raise / lower selection | **Alt + Up / Down** |
| Rotate selection yaw | **Alt + Left / Right** |
| Camera move / up-down | **W A S D** / **Q E** |
| Camera fast | **Hold Shift** |
| Camera look | **Right-mouse drag** |
| Fly speed / brush size | **Mouse wheel** |
| Type an exact slider value | **Right-click the slider** |

---

*Built by Lucas Ludwiczak, with engineering assistance from Claude (Anthropic). Licensed under the
GNU General Public License v3.0 — see `LICENSE`.*
