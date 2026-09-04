# RefractorForge — User Guide

A native Windows map editor for **Battlefield 1942 (2002)** and **Battlefield Vietnam (2004)**.
RefractorForge opens a level, renders it in real time, and lets you edit terrain, textures, foliage,
objects, gameplay (flags / spawns), lighting and environment, then save back to the game's own files.

> **Beta software.** RefractorForge is under active development. It edits real game files — keep
> backups of any map you care about before saving over it.

This guide is also available inside the editor: **Help ▸ User Guide / Controls**.

---

## 1. Getting started

1. Launch `RefractorForge.exe`.
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

- **Menu bar** (top): File, Edit, Object, Tools, Terrain, View, Layer, Window, Collab, Help. Every command has
  one home: file and project work under **File**, undo/delete/prefabs under **Edit**, selection actions under
  **Object**, the bakes under **Tools ▸ Lighting**, navmaps under **Tools ▸ AI**, the checks under **Tools ▸
  Check Map**, heightmap import/export and the generated material/surface maps under **Terrain**, render and
  camera modes under **View**, display toggles under **Layer**, panels under **Window**.
- **Tool ribbon** (below the menu): New / Open / Save · the six **mapper tabs** · Undo / Redo ·
  **Grid**, **Labels**, **Snap**, **Map** toggles.
- **Object Library** (left): a searchable tree of every placeable object, grouped by category
  (Structures, Vegetation, Land/Water/Air Vehicles, Stationary Weapons, Hand Weapons, Soldiers,
  Effects, Props, Gameplay, Sounds, …).
- **Mini-Map** (floating): top-down overview; **Refresh** to rebake. Toggle with the **Map** button. It opens
  below the mapper's option row, and can be dragged anywhere.
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
Everything works the Battlecraft way: **click on the object itself and drag**. No gizmo handle has to be hit
first, and an object that is not yet selected becomes the selection with the same click (**Shift** adds it).
- **Left-click** an object to select; **Shift-click** to add to / toggle the selection.
- **Move tool** — click any object and drag it across the terrain. The **X / Y / Z** buttons in the ribbon
  constrain the drag to one world axis; the axis handles still work if you prefer them.
- **Rotate tool** — click any object and drag **sideways** to spin it (2 px = 1°; with **Snap** on it turns in
  15° steps). Hold **Ctrl** to pitch or **Alt** to roll with an up/down drag; the **X / Y / Z** buttons give
  Battlecraft's per-axis up/down rotation. The rings remain for fine work.
- **Scale tool** — click any object and drag away from its centre to grow it, toward it to shrink it.
- **Place tool** — with a template chosen in the Object Library, click the terrain to drop a copy.
- **Nudge tool** — like Move but gentler, and it keeps the object's own height (Ctrl = finer).
- Locked objects (everything a level ships is locked) select but do not move: **Object ▸ Unlock**.
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

### Decal objects (Tools ▸ Create Decal Object…)

Refractor has no decal primitive — the posters, signs and scorch marks in retail maps are ordinary objects with a
flat mesh. The dialog makes one from an **Image…** (PNG / TGA / DDS / BMP) and registers it as a level-local
object: its `.sm`, `.rs`, `.con`s and texture ship inside the map, and the mod needs nothing. It reads the image's
resolution and shows it with the reduced aspect (`1024 x 512 px (2:1)`); with **Preserve aspect ratio** on (the
default) the **Size** slider sets the width and the height follows. Non-power-of-two images are resized, because
the engine silently drops them.

**Video (.bik)…** does the same with a Bink movie: the `.bik` is copied to the mod's `Movies` folder and the
object's shader points at it — the trick the mod's own movie screens use — so it plays in the game, with its
sound. The editor shows the first frame when FFmpeg is on the PATH (otherwise a placeholder, 4:3 assumed).

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

### Copy / paste ground (the stamp) — Surface mapper

The brush repeats its texture every few metres, which is the wrong tool for moving a *stretch of ground*: a
100 m capture painted with a 100 m brush comes out as that capture squeezed into every tile. The **stamp** is the
other thing: one copy of a square of painted ground, kept at its real size, pasted back 1:1.

1. Tick **Capture mode**, set **Capture size (m)** (up to 1024 m) and the resolution, and click the terrain. That
   square is now the stamp (its thumbnail and size show in the panel). Untick **Save as .dds file** if you only
   want to paste it, and the editor drops you straight into paste mode.
2. **Paste mode** — click where the centre should go. **Scale x**, **Rotate 90**, **Opacity** and **Edge feather**
   shape the paste; the square on the ground shows exactly what will be covered. **Z** undoes a paste.
3. **Export stamp…** writes the picture as a `.dds` plus a `.stamp.json` holding its ground size; **Import
   stamp…** on any other map reads both back (a `_100m` suffix in the file name works too, and **Stamp size**
   can always be typed). Ctrl+S bakes pasted ground into the level's terrain tiles like any other paint.

### Overgrowth and undergrowth (Inspector ▸ Layers, BFV)

**Overgrowth Trees** and **Undergrowth** each scatter the map's own `.wst` definition onto the terrain as a
preview of what the game grows. A freshly opened map starts from what the game itself generates — patches every
12.5 m (trees) and 17.5 m (undergrowth) at density ×1.0 — and **Map defaults** takes a slider back there. The
**Map definition** line under each opens the map's palette: per terrain material, which geometries grow and how
likely each is. **Tools ▸ Save Overgrowth Settings** keeps both layers' sliders for that map.

---

## 7. Environment & lighting (Inspector)

- **Water** — level, surface colour, deep colour, transparency; optional scrolling **Textured** water
  (BF1942) or procedural colour (BFV). Import water textures if the engine's built-ins aren't shipped.
- **Water shader** (BFV) — **Reflectivity** is how much of the sky cubemap the surface mirrors (retail ships
  0.18 on Fall of Saigon, 0.25 on Con Thien, 0.3 on Ho Chi Minh Trail; the base game 0.2), **Opacity** the
  surface's own. They are written to `StandardMesh/levelWater.rs` inside the level on save — the override the
  retail levels ship. The viewport mirrors the level's sky cubemap in the water, so the slider reads live.
- **Sun** — tick **Control sun manually** to drive azimuth / elevation; the real-time shadow map
  follows. **Sun Shadows (real-time)** in Layers toggles the display.
- **Fog** — colour, start / end distance.
- **Sky** — use the level's cubemap, set sky rotation, or **Import skybox…** (6 faces named `…_01`–`_06`).
- **Animated Clouds** — coverage, scale, drift X/Y, colour; import a cloud texture or mesh.
- **Lighting bakes** (Tools ▸ Lighting — everything the game reads for light):
  - **Bake Lightmaps (sun + placed lights)** — the one-button bake: the terrain sun-shadow
    (`LightmapShadowBits.lsb`), every object's lightmap (`ObjectLightMaps/*.tga` — sun, terrain shadow *and*
    your placed lights) and the placed lights' colour in the ground texture. All of it shows in the viewport at
    once, before anything is saved; **Save** then writes it all. Set the sun and the lights first.
  - The parts on their own: **Bake Sun Shadows (terrain)**, **Bake Object Lightmaps**, **Bake Placed Lights
    into Ground Texture**. **Show the level's baked terrain shadow (.lsb)** displays the shadow the level
    already ships.
  - What an object lightmap *is*, from the game's own shader (`effects/RaShaderPPLSTs1DifLmp.fx`): a **sun-visibility
    mask**. The game draws `texture × saturate(2 × (mask × sunColour × N·L + LMambientColor))`, so the map only says
    where the sun reaches; the sun's angle is applied live, and `renderer.LMambientColor` (0.2–0.35 in every
    retail BFV level) is what a shadowed texel keeps. The bake writes exactly that: 1 where the sun reaches the
    surface, 0 where the terrain **or the object itself** is in the way (ceilings under roofs, bunker interiors),
    plus your placed lights as extra visibility. The viewport draws the same formula.
  - Not every mesh can carry one. BfVietnam's props, sandbags, clutter and small walls ship the lightmap-UV slot
    *empty* (0,0 on every vertex; the game's own generator unwraps those itself). The bake skips them and says how
    many: they stay sun-lit in the editor and in the game, and a placed light cannot reach them - only the pool
    on the ground under them shows. Buildings, huts, bunkers, the big walls and the tunnel meshes carry real
    unwraps and take the lights.
  - A light placed *after* a bake still shows on a lightmapped object in the viewport (combined by maximum with
    the map, so a light that is already baked is not doubled); bake again to ship it.
  - **Layer ▸ Object Lightmaps** / **Sun Shadows (real-time)** toggle the display.
  - **Tools ▸ Generate Minimap** — the in-game map + thumbnail.
- **Placed lights** (Inspector ▸ PLACED LIGHTS). Refractor has no dynamic point lights, so a placed light is
  authoring data: it lights the viewport live so you can aim it, then **Bake into ground** burns it into the
  terrain texture *exactly as the viewport shows it* — the pool goes in as a ratio to the ground around it, so
  the ground keeps its own detail and colour inside the pool. After a bake the live lights switch off on the
  ground (**Live on ground**), because the pool is in the texture now; what you see then is what the game
  shows. **Z** undoes a bake; **Ctrl+S** writes the tiles. Two things to know:
  - The **Night preview** slider is editor-only: it shows the level's *real* light level (the ambient and
    diffuse in its Init.con) instead of the editor's always-readable lighting, so the pools read as they will
    in the game. Apply the **Night preset** first — on a daylight level there is nothing to darken — and bake
    with it applied, since the preset is what goes to Init.con.
  - A bake never blackens the ground: the pool goes in as a ratio to the level's own scene light, with a floor,
    so ground the lights do not reach keeps its texture at the level's brightness.
  - **Bake all** on the panel is the one-button bake above, for when the lights are placed.
  - A lamp against daylight barely registers — in the game as in the bake. Night maps are where lights live.
  - **Bake strength** scales the whole rig without re-aiming it.

---

## 7b. Tunnels (Battlefield Vietnam 1.2) — Window ▸ Tunnels

BFV 1.2 added an underground layer (Operation Cedar Falls' tunnels, Saigon68's sewers). Battlecraft's tunnel
tool was two half-hidden pieces — a hole brush in the Terrain mapper and "Generate Underground Map" in the
in-game-map dialog — and it wrote `game.isTunnelMap 0` into every custom map, which is why tunnels made with
it never worked. Here it is one window, and it writes the switches the way the retail maps have them.

How the game does it (all four parts are needed):
1. **A hole** is a terrain cell whose height is *exactly 0*. With `Game.isTunnelMap 1` the engine draws and
   collides nothing on a cell that touches one, and a soldier can drop through. Paint them with the **Hole**
   brush (Terrain mapper; **Fill hole** closes them again), or place the entrances and press **Punch holes
   under entrances**, which sets the cell under each one the way Cedar Falls is built.
2. **The tunnel** is an ordinary placed object from the Object Library's **Tunnels** category — `o_tunnelsA`
   (Cedar Falls' complex) or `o_sewers_A_M1` (Saigon68's sewers). Its template says `isBelowGround 1`; its
   corridors sit below the surface, so lower the camera or switch the terrain to wireframe (**J**) to see it.
3. **The entrances** — `o_Tunnel_Hut_m1`, `o_Tunnel_Hole_m1`, `o_tunnel_Bunker_M1`, `o_tunnel_ladder_m1` — carry
   `isEntryPoint`; a soldier within `Game.entryPointRadius` (3.5 m retail) of one can pass the terrain. Line
   their shafts up with the tunnel's.
4. **The underground map**: `mapManager.addObjectMap <template> <MapName> x/z/w/h` binds `Textures/<MapName>.dds`
   as the minimap while the player is inside that object. **Generate underground map(s)** renders each tunnel
   object top-down (floors light, walls dark, north up) and writes the line for it.

**Tunnel water.** The terrain owns *two* water bodies. `PatchTerrain::getWaterLevel` uses the second one —
`GeometryTemplate.waterBelowLevel` — for any point below the surface or on a hole, but only when
`GeometryTemplate.drawWaterBelowTerrain 1` is set; without it the river fills every corridor at the river's
height, which is why a tunnel under water "hits the water everywhere". Tick **TUNNEL WATER** in the window and
drag the level: it writes the two Terrain.con lines and a `waterBelowTerrain.*` colour block into Init.con (a
mirror of the level's `water.*` lines — exactly what Saigon68 ships, the one retail level that uses it). Keep the
level below the tunnel floor for a dry tunnel, or in a sewer for wading; the viewport draws the second plane.
Cedar Falls does without it by keeping its whole tunnel above the river. Maps that crashed on this had the
colour block without the Terrain.con switch, or a `waterBelowTerrain.level` — there is none: the level is
`waterBelowLevel` in Terrain.con, the colours are in Init.con, in that order.

**Where the switch happens.** **Layer ▸ Tunnel entry points** draws each entrance's `entrance` child as a sphere
of `Game.entryPointRadius`: the soldier goes underground when they are touching the entrance object *and* inside
that sphere (Cedar Falls 3.5 m, Saigon68 5 m). A sphere under the water means the soldier swims before the
switch; the window says so.

The window shows every entrance with whether a hole is under it, and every tunnel object with whether a map
is registered, so the list tells you what is left. `Game.isTunnelMap`, `Game.useBelowGroundCulling` and the
radius are written to Init.con on save (a Hole brush stroke switches the system on for you). Retail levels
that use this: Operation Cedar Falls (`TunnelsAMap`), Saigon68 (`SewersAMap`). The complete engine mapping —
every relevant line of both levels, what `checkObjectVsObject` does, and what Battlecraft leaves out — is in
`docs/BFV_Tunnel_System.md`.

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

- **Save** (Ctrl+S) — **writes the level itself.** A folder level goes back to its folder; a level opened from
  a `.rfa` is written back into *that* `.rfa`. The archive is replaced through a temp file and every entry of the
  result is decoded again before the save reports success, and **File ▸ Auto-backup on save** keeps a timestamped
  copy under `%AppData%\RefractorForge\Backups` first.
  - One thing to watch: a `<Level>_NNN.rfa` sitting beside your map is mounted *over* it by the engine, so its
    copy of a file wins over what you just saved. Save names any it finds in the log — delete or rename them and
    your edits appear.
- **File ▸ Save as Patch .rfa…** — export only the changed files as an overlay `.rfa` that the engine mounts over
  the map. For shipping an update on top of a map without touching it. A patch is not a separate map: in game you
  still launch the base level.
- **File ▸ Test This Level (in-game)** (Ctrl+L) — save, then launch the game on this mod (pick the map
  in-game — the client can't be told which map to load from the command line).
- **File ▸ Import .obj…** — bring in a Wavefront model as a static object (and export it back as `.sm`).
- **Tools ▸ Convert TGA → DDS** (single or batch), **Tools ▸ Check Map ▸ Validate Map** (missing-asset check)
  and its siblings (bot reachability, performance budget, dependencies, server/client files, compare), **Tools ▸
  Scatter Objects…** (random placement).

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
