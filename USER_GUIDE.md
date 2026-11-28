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
- **Sounds layer** (Inspector ▸ Layers, or Layer ▸ Sounds): every sound on the map shows its source, an inner ring
  where it is at full volume and an outer ring where it falls silent. The label reads out what you would hear standing
  at the camera — a percentage and a little meter that fills as you approach, or *(while looked at)* for a sound tied
  to its object being drawn. It covers the level's own emitters and sounds carried by an object, such as a video screen.
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
- **Place tool** — with a template chosen in the Object Library, click to drop a copy: on the terrain, or **on top
  of the static object under the cursor** — a crate on a rooftop, a lamp on a table, sandbags on a bunker. The
  green marker shows where it will land. Dragging a template out of the library does the same. (The point on the
  object is read from the picture itself, so it is the surface you can see; prefabs and gameplay markers still
  go onto the terrain.)
- **Nudge tool** — like Move but gentler, and it keeps the object's own height (Ctrl = finer).
- Locked objects (everything a level ships is locked) select but do not move: **Object ▸ Unlock**.
- The Inspector shows **Position / Rotation / Scale** for the selection — drag, or **right-click a
  field to type an exact value**. An object scaled per axis (`Object.geometry.scale x/y/z`; retail BFV levels
  have thousands) keeps it on save and shows **Scale per axis**; setting Scale makes it one value again.

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

### Free models from Poly Haven and Poly Pizza (Tools ▸ Find Free Models…)

Search two free model libraries from inside the editor and bring a model straight into the map:

- **Poly Haven** — about 500 CC0 models (scanned props, furniture, rocks, plants), real-world size, no account. The
  catalogue shows as soon as the window opens, most downloaded first; type words to narrow it (every word must
  match the name or a tag) or pick a **Category**.
- **Poly Pizza** — about 10,000 low-poly models, the size these games want, CC0 or CC-BY. It needs a free API key:
  make a free account at poly.pizza, create a key on its API settings page (**Get a key…** opens it) and paste it
  into **API key**. The key is kept in this PC's editor settings; **Forget key** removes it. **CC0 only** and
  **Hide animated** narrow the results. Models whose licence forbids changes (CC-BY-ND) are never offered, because
  the import simplifies and converts every model.

1. Click a model to see it on the right: author, licence, triangle count, size and tags. **Open the model's page**
   shows it on the library's site.
2. **Download and import** (or double-click the tile). Only the files the editor uses are downloaded (for Poly
   Haven, the model and its colour texture, not its normal and roughness maps), and they are kept in
   `%LocalAppData%\RefractorForge\ModelCache`, so a second import does not download again. **Texture size** picks
   Poly Haven's 1k or 2k set; the import makes textures 512 px by default, so 1k is plenty.
3. **Import 3D Model** opens with the model loaded, in metres and standing on its base. Set the size, the triangle
   budget (most Poly Haven models are several thousand triangles; 2,000 is the default) and collision as for any
   import, then **Add to level** and click to place it.
4. The credit goes into the map: **`ModelCredits.txt`** inside the level gets the model's name, author, source page
   and licence (CC-BY asks for exactly that, and it is kept for CC0 models too). **Tools ▸ Package Level…** repeats
   it in the readme.

Models come in through the editor's own glTF reader, so Blender is not needed; one with compressed geometry (Draco)
is converted with Blender if it is installed. Cut-out textures (leaves, fences) and double-sided surfaces keep those
settings. Animated models come in as static objects in their rest pose.

### Collision meshes and AI meshes (Inspector ▸ Layers)

- **Collision (wireframe)** draws the collision meshes of the placed objects. A mesh can carry two: **COL01**, the
  coarse hull, and **COL02**, the detailed one. **Sections** shows **COL01** (green), **COL02** (orange) or
  **Both**, and the colours never change meaning. In-game tests show COL02 is what soldiers and vehicles collide
  with, although the Max toolkit labels COL01 "simple (soldiers, vehicles)" and COL02 "complex (projectiles)".
- **AI meshes** shows which objects have an AI mesh, the simplified mesh in `aiMeshes/` that bots use, and counts
  them (*N with, M without*). Cyan: the AI mesh, where it lies. A red box: a solid object (its mesh has
  collision) with none. The game pairs them by name: the object's geometry name with its last part made `_a1`
  (`barack_m1` → `barack_a1`).
- Select an object and the Inspector says what its mesh carries — *Collision: COL01 312 faces, COL02 1,204 faces*
  — and which AI mesh file it gets (or *AI mesh: none*).

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
object's shader points at it — the trick the mod's own movie screens use — so it plays in the game. A video keeps
**its own aspect ratio**, always: only the size is adjustable, because a stretched movie is nobody's intention.

**Convert a video to .bik…** turns an mp4 (or anything else FFmpeg reads) into the Bink file the game plays,
audio included. Bink can only be *written* by RAD's own compressor, so this needs the free
[RAD Video Tools](http://www.radgametools.com/bnkdown.htm) installed; FFmpeg prepares the video for it. It runs in
the background — the editor stays usable and shows the elapsed time and size as it goes — and a long video can take
minutes. **Video size** caps the width (512 px by default): the file ships inside your map, and Bink is not a small
format — the game's own `background.bik` is 320×240 and about 5 MB a minute, while an untouched 720p minute is
nearly 80 MB.

**Sound from the video** attaches the movie's audio to the decal object itself, so it plays from the middle of the
screen. **When it plays** picks between the two things the engine can do:

- **Always — fades with distance** writes `autoPlaySound`, the ambient every retail speaker and generator uses: it
  starts with the level and rises as you approach. **Volume**, **Full volume within** and **Silent beyond** shape it.
- **Only while you look at it** leaves that line out, so the sound follows the object being *drawn* — it snaps on at
  full volume when the screen comes into view and stops when it leaves. Because it lasts exactly as long as the screen
  is drawn, **Stops beyond** caps the draw distance, and with it how far away the sound can be heard; without that a
  screen is audible from across the map the moment you glance at it.

**Stereo** is offered because the format takes it (276 of the game's own scripts declare it), but a stereo sample is
not placed in the world the way a mono one is — leave it off for a screen you can walk around.

### Importing a sound (Tools ▸ Import Sound (MP3/WAV)…)

Adds an ambient sound to the map as a placeable object. Pick an **mp3** (or wav, ogg, m4a, flac…) — the game plays
`.wav`, so anything else is converted on the way in — set its **Volume**, the radius it stays at **full volume**
within, the distance it goes **silent** beyond, and whether it **loops**; the sound is placed where the camera is
looking and can be dragged like any object. What ships is exactly what a retail ambient emitter is made of: the wav
under `Sound/22khz` and `Sound/44kHz`, a `.ssc` script with a Distance→Volume ramp, a `.con` that makes it
placeable, and the `run` line in `Sounds/Environment.con`.

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

### Game modes — Conquest, CTF, TDM, Single Player, Co-op

A level keeps a separate set of gameplay files for every game mode it supports (`Conquest/`, `Ctf/`, `TDM/`,
`SinglePlayer/`, `Coop/`, or a mod's own folder). Two dropdowns in the Object mapper's ribbon deal with them:

- **Game type** — which mode's flags and spawns are *shown* (and clickable): **Show All**, or one mode.
- **Edit** — which mode's flags and spawns you *edit*. The editor loads that mode's own files, and **Save** writes
  that mode in full. Flags and spawns it shares with the other modes (the same name in their files) have their
  moves mirrored into them, but nothing is ever added to or removed from another mode: a flag you add while editing
  CTF exists only in CTF, and Conquest's own set is never replaced by CTF's.
- A level opens on **Conquest** (or its first mode). Switching asks to save first when there is unsaved work, and
  starts a fresh undo history. It is not available during a live collaboration session.

### When a ship or carrier spawns the wrong team

A vehicle spawner's **team** decides which vehicle spawns for which side. The soldiers who spawn *on* a ship or a
carrier are a separate matter: they come from spawn points built into the vehicle's own object, and those belong to
a **spawn group** whose team is set once for the whole game, in the mod's `game/GlobalSpawnGroups.con`. That is why
an SMSDresden given to the Allies in Battlefield 1918 still fills with Axis troops: its spawn points are in group
73, and that file gives group 73 to the Axis (team 1).

The editor checks every vehicle spawn against those files:

- A spawn with the problem shows an orange warning in the Inspector, saying which spawn points, which group and
  which file gives it to the other team. **Tools ▸ Check Map ▸ Validate Map** lists it under *Spawns*.
- The fix is the button under the warning, **Make a *team* copy of *vehicle***. It adds a copy of the vehicle to this
  level (for example `SMSDresden_T2`) whose spawn points are in a new spawn group of that team, declares the group
  in every game mode's `SpawnpointManagerSettings.con`, and switches the spawner to the copy. Mesh, physics and
  weapons are shared with the original. Save writes it into every game mode.
- Retail Battlefield 1942 solved its second Fletcher the same way (spawn groups 80 and 81). Details:
  `docs/Vehicle_Spawn_Teams.md`.

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
  AI Navmaps** regenerates them from the terrain. For a bridge deck, a ship or a platform high above the
  ground, paint on a level plane instead (below).
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

### Pathmaps on bridges, ships and platforms (AI Path ▸ Paint on a level plane)

A pathmap is a flat, top-down grid, but the brush normally lands on the terrain under the cursor. Under a high
bridge, beside a ship floating over deep water, or below a platform in the sky, that point is far below what you
are looking at, and seen at an angle it is somewhere else entirely, so the deck cannot be painted where it is.

1. In the AI Path mapper (F6), tick **Paint on a level plane**. A flat plane appears showing the pathmap (green
   passable, white blocked, with the cell grid once the cells are big enough to see) and the brush sits on it.
2. Put the plane on the deck: **Pick height**, then click the deck, bridge or platform in the view (the plane moves
   to that height, centred there), or **Fit to selected** to centre and size it on the selected object. Fine-tune
   with **X / height / Z**, or **PgUp / PgDn** (1 m; Shift: 10 m). **Size (m)** or **Whole map** sets its extent.
3. Paint as usual. Strokes land on the plane, straight above the cells they change; outside the plane nothing is
   painted.

The pathmap is still 2-D: a cell painted on the plane is the same cell as the ground beneath it, so where a
bridge crosses a road, the deck and the road share their cells for each vehicle type.

### Overgrowth and undergrowth (Inspector ▸ Layers, BFV)

**Overgrowth Trees** and **Undergrowth** are the Battlefield Vietnam engine's own growth, generated with the
game's exact algorithm, reversed from `BfVietnam.exe`: the same random generator and seeds, the same cells and
patches, the same per-cell budgets and spacing. Tree positions match a capture from the running game exactly, so
what you see is where the game plants every tree and tuft.

- **Game build** — which executable to match. The stock `BfVietnam.exe` sizes its tree cells from the map's view
  distance; the `BfVietnam_Veg_*` builds use 12.5 m cells (a far denser forest), and their **Low / Medium / High**
  settings keep a quarter, half or three quarters of the cells (**Ultra** keeps all).
- **Density comes from the game, not from painting.** Each tree cell has a budget of 2048 vertices and stops at the
  first tree that no longer fits, so a full cell grows nothing more however much of it you paint; the lines under
  the layer show the trees, the cells and how many are full. Placed trees whose mesh is listed in
  `standardMesh/overGrowthGeometries.wst` are taken into their cell first, as the game does (*placed trees taken
  over*).
- **Whole map** (trees) — off, only the cells the game draws from where the camera is (inside the `.wst` view
  distance); on, every cell, for an overview.
- **Show from above** (undergrowth) — the game draws grass only in small patches around the camera, faded towards
  its view distance, so from high up there is none, exactly as in game. Tick it to see every patch around the point
  under the camera at full strength while looking down.
- **Map definition** opens the map's palette: per terrain material, which geometries grow there and how likely
  each is. **Tools ▸ Save Overgrowth Settings** remembers these choices for the map.
- **Tools ▸ Overgrowth ▸ Convert Overgrowth to Static Objects** places the generated trees as ordinary objects —
  position, rotation and scale as the game generates them — so every player sees the same forest whatever
  executable they run. The reverse-engineering notes are in `docs/BFV_Growth_Placement.md`.

---

## 7. Environment & lighting (Inspector)

- **Water** — a map can have **two** bodies and each is edited on its own:
  - **Surface water** — level, colour, deep colour, transparency (written to the level's `water.*` lines), and on
    BfVietnam **Reflectivity** and **Opacity** from `levelWater.rs`. The viewport plays the game's own water
    **animation** (the sequence of normal maps `levelWater.rs` names), so the ripple and the sky reflection you see
    are the ones the game draws — flat coloured water in the editor was never what the game showed.
  - **Tunnel water (below the terrain)** — the second body a BFV tunnel map has, with its own level, colour, deep
    colour, transparency, reflectivity and opacity. Switching it on writes everything the engine needs, including
    the `WaterSettingBelowTerrain` subshader (without which the level crashes on load). It starts from the surface
    water's look and then goes its own way.
  - BF1942's scrolling **Textured** water is still there for 1942 maps; import the textures when the level
    references engine built-ins its archive doesn't ship.
- **Water shader** (BFV) — **Reflectivity** is how much of the sky cubemap the surface mirrors (retail ships
  0.18 on Fall of Saigon, 0.25 on Con Thien, 0.3 on Ho Chi Minh Trail; the base game 0.2), **Opacity** the
  surface's own. They are written to `StandardMesh/levelWater.rs` inside the level on save — the override the
  retail levels ship. The viewport mirrors the level's sky cubemap in the water, so the slider reads live.
- **Sun** — tick **Control sun manually** to aim it by azimuth / elevation. It relights the terrain and objects,
  recasts the real-time shadow map, is what every lighting bake uses, and **on save it is written to the level's
  `SkyAndSun.con`** — so the game's own lighting agrees with the shadows you baked. The panel says so once you have
  moved it; **Reset sun to level** hands the direction back to the level and stops the editor writing it.
  **Sun Shadows (real-time)** in Layers toggles the display.
- **Fog** — colour, start / end distance, and the **view distance** (`Game.ViewDistance`): how far the game draws
  the world at all, a separate setting from the fog, which only shades what is inside it. Retail maps use
  250–900 m. Keep the fog end at or inside the view distance — past it, the map ends in a hard line where the
  drawing stops, and the panel says so. **Save fog to level** writes both into Init.con, keeping whichever
  spelling the level already uses.
- **Sky** — use the level's cubemap, set sky rotation, or **Import skybox…** (6 faces named `…_01`–`_06`).
- **Animated Clouds** — coverage, scale, drift X/Y, colour; import a cloud texture or mesh.
- **Lighting bakes** (Tools ▸ Lighting — everything the game reads for light):
  - **Bake Lightmaps (sun + placed lights)** — the one-button bake: the terrain sun-shadow
    (`LightmapShadowBits.lsb`), every object's lightmap (`ObjectLightMaps/*.tga` — sun, terrain shadow *and*
    your placed lights) and the placed lights' colour in the ground texture. All of it shows in the viewport at
    once, before anything is saved; **Save** then writes it all. Set the sun and the lights first.
  - The parts on their own: **Bake Sun Shadows**, **Bake Object Lightmaps**, **Bake Placed Lights into Ground
    Texture**.
  - **Sun shadows** are bake, then save — nothing else to switch on. The bake casts the terrain and every placed
    object and darkens the shadow **into the ground texture**, which is where the game draws a sun shadow (the
    `.lsb` it also writes changes nothing you can see on the ground). The viewport then shows the ground exactly as
    the game will. **Shadow darkness** is how much darker shadowed ground gets: 0.50 matches retail maps, 0 is none;
    moving it after a bake re-darkens the baked shadow at once, without baking again. The Lighting panel says what
    shadow the ground carries, and so does the log on every save. **Unbake sun shadows** takes it back off — a map
    saved after an Unbake has no sun shadow in game, and the save says so.
  - What an object lightmap *is*, from the game's own shader (`effects/RaShaderPPLSTs1DifLmp.fx`): a **sun-visibility
    mask**. The game draws `texture × saturate(2 × (mask × sunColour × N·L + LMambientColor))`, so the map only says
    where the sun reaches; the sun's angle is applied live, and `renderer.LMambientColor` (0.2–0.35 in every
    retail BFV level) is what a shadowed texel keeps. The bake writes exactly that: 1 where the sun reaches the
    surface, 0 where the terrain **or the object itself** is in the way (ceilings under roofs, bunker interiors),
    plus your placed lights as extra visibility. The viewport draws the same formula.
  - **Lightmap quality** (Lighting panel): **Draft** (1 sample, 256 px), **Good** (2×2, 512 px), **High** (3×3,
    1024 px - the engine's limit; 2048 crashes the game) and **Ultra**, an offline-renderer bake at the same 1024 px:
    a **soft sun** (the sun is a disc, so a shadow is sharp where it touches and widens with distance), **ambient
    occlusion** (**Contact shadow**), sky-weighted **Shadow fill** so a shadow is not pure black however open it
    is, one bounce of **coloured indirect light** (real colour in BF1942; BfVietnam's shader reads one channel, so
    there it is brightness only) and a denoiser that stops at edges. **Sun size** 0.53° is the real sun; larger
    reads as haze, and around 1-1.5° is a good start for interiors lit through a window. Ultra takes minutes per
    object on the CPU - **Bake selected only (preview)** bakes just the selected object and keeps every other map.
  - **On a Battlefield Vietnam level the top entry is Realistic** instead of Ultra (also in **Tools ▸ Lighting ▸
    Lightmap quality**). With it chosen, **Bake Object Lightmaps** bakes every object by the Realistic Lighting
    solve - the window's sun, sky, bounce and lamps at the window's quality - and sets the level's four light colours
    to the ones the maps were solved against, since the maps are only right with those. The ground is left as it
    is; the window's own **Bake realistic lighting** does the ground too. **Realistic Lighting settings...** opens
    the window. BF1942 keeps Ultra.
  - **Bake on the GPU** runs Ultra's rays on a dedicated graphics card (OpenGL 4.3). It is off until **Test GPU**
    has baked a small scene on the card and on the CPU and found them to agree; the result line shows the card,
    the speed-up and how far apart they were. It only runs on a *dedicated* card: on a machine with two GPUs,
    Windows starts the editor on the integrated one unless **RefractorForge.exe** is set to *High performance* in
    Windows Settings ▸ System ▸ Display ▸ Graphics (or the NVIDIA Control Panel), and the panel says so. Placed
    lamps and the Draft/Good/High tiers stay on the CPU; if anything goes wrong mid-bake the rest of it finishes on
    the CPU.
  - Not every mesh can carry a lightmap. Most of BfVietnam's props, sandbags, clutter and small walls - and nine
    in ten of BF1942's meshes - have no lightmap channel, or ship it *empty* (0,0 on every vertex). The engine has
    no way to make one: its generator needs exporter `.samples` files that ship in no archive. The bake skips
    them and says how many: they stay sun-lit in the editor and in the game, and a placed light cannot reach them.
    Buildings, huts, bunkers, the big walls and the tunnel meshes carry real unwraps and take the lights.
  - **Lightmap-ready objects…** (Lighting panel, or Tools ▸ Lightmap-Ready Objects…) fixes that for the whole map.
    It lists every object type whose meshes have no usable unwrap - count, kind, what it would change, the map size
    and disk cost - and for each type you tick it makes a **copy inside this level**:
    - the mesh is unwrapped here: an empty slot is filled in place (the 64-byte layout BfVietnam's own lit buildings
      use), otherwise the vertex is widened to the 40-byte layout both games' lightmapped meshes use;
    - the object's definition is copied **line for line** - an ammo box keeps its supply depots, a medic box keeps
      healing - with only the mesh names changed, down through every LOD mesh; parts placed at an offset are left
      alone, as the bake leaves them;
    - every placement of that type is pointed at the copy (one undo step). Then **Bake Object Lightmaps** as usual.
  - **Light the copies from their lightmaps alone** (BfVietnam only, on by default). The game lights an object with
    its two directional lights and uses the lightmap only to dim them, so a wall side-on to the sun, a ceiling, or
    anything lit by a placed lamp cannot show what was baked. With this on, a copy is drawn as
    **texture x lightmap** on every face - measured in the game:
    - every level of detail goes on the 40-byte vertex (a 64-byte mesh keeps its unwrap and gives up its tangent
      frame), and the shader gets `selfillum 1 1 1` with specular and the normal map switched off;
    - objects that already have a lightmap are listed too, because they need the same copy. Tick **Show foliage**
      and **Tick all shown** to convert every object on the map;
    - bake afterwards with the **Realistic** quality: such a copy's lightmap is solved per colour channel and
      written as a 24-bit file, so a lamp's colour and the light inside a hangar reach the game;
    - a 24-bit lightmap costs the game about four times the memory of an 8-bit one, and a level with too many
      does not load. The bake keeps them within 500 MB: a map with no colour in it is written 8-bit, and the
      largest of the rest are halved until they fit. The bake summary says how many were reduced;
    - a level whose copies were made the older way shows **Rebuild the existing copies**;
    - what it costs: the copies lose their specular highlight and normal map, and about 3 in 100 retail meshes
      (ones that will not unwrap, and triangle strips) cannot be converted and stay as they were;
    - Battlefield 1942 is not covered yet: its copies are made as before.
  - **Light volumes** (Tools ▸ Lighting ▸ Light Volumes…, BfVietnam only) fake what the game does not have: fog
    you can shape, and darkness that covers a soldier standing in an unlit room. A volume is a box filled with
    nearly transparent planes; their tint adds up the further you look through the box and covers whatever stands
    inside it (measured in the game: planes at 3 % each left half of what was behind twenty of them).
    - **Add at the camera** or **Add around the selected objects**. With one object selected the box takes that
      object's own turn and fits its mesh, so a hangar standing at an angle is covered exactly; **Rotation** turns
      a box by hand.
    - Pick a **Preset**: *Dark interior (day)* or *(night)* and *Deep shadow* darken unlit air and stay clear at
      doors and under lamps; *Lamp shaft (soft / strong)* adds the lamps' own light to the air under them;
      *Thin mist* and *Fog bank* are an even veil in the level's fog colour. The plane spacing follows the box's
      size. **Advanced** holds the numbers behind a preset for anyone who wants them.
    - **Bake** traces the light in the air of the box - sun, sky and your placed lamps - and writes it into the
      planes; the volume fades out toward its sides and top. Save writes it into the level as an ordinary object.
    - Every plane is drawn over everything behind it, so a volume costs fill rate: keep them to interiors and
      lamps rather than a whole map. The window shows the plane count before you bake.
    
    Everything goes into this level's archive (`Objects/RF_LightmapReady/`, `StandardMesh/<mesh>_lm_m1.sm`);
    nothing outside the map changes, and **Point all back at the originals** undoes it for every type. Foliage and
    effect quads start unticked - trees are read from their shaders (every BfVietnam vegetation material has
    `alphatestref` and `selfillum`) and run to hundreds of placements. A type defined twice with different contents
    (retail's rope bridge), a mesh with no shader, or one that will not unwrap is listed with the reason and never
    written. On al_vietnas: 85 structure types, 819 objects - including the clutter inside buildings - and BfVietnam's
    dedicated server loads the result. Whether the widened ones draw lit is the in-game check.
  - A light placed *after* a bake still shows on a lightmapped object in the viewport (combined by maximum with
    the map, so a light that is already baked is not doubled); bake again to ship it.
  - The files go out the way the game files them: one map per **LOD mesh** (`O_HueHouse_B_M1_<x>-<y>-<z>.tga`
    and `_M2_` for the far LOD, never one named after the placed template), plus `Objectlightmaps/Palette.pal`
    when the level has none. Painted or light-baked ground goes back as **DXT1 terrain tiles with mipmaps at
    the shipped size** (256² in every retail level) - the only form the game's terrain loads.
  - **Layer ▸ Object Lightmaps** / **Sun Shadows (real-time)** toggle the display.
  - **Black ground in game?** A build of this editor before v0.13.1 saved terrain tiles as 1024² uncompressed DDS
    with no mipmaps, which the engine's terrain cannot draw. Opening such a map now says so, and the next save
    re-encodes every tile as DXT1 + mipmaps at the shipped size — the form every retail tile is in.
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

### Realistic lighting (Window ▸ Realistic Lighting, BfVietnam)

One bake for the whole level's light. You describe the light as it is — a sun, a sky, your placed lamps — and the
editor **ray-traces** it: the sun through its disc (so shadows soften with distance), the sky as a coloured dome,
and both again after they bounce off the ground and the walls. It then works out what the game needs in order to
draw that light, and writes all of it.

**How the game lights a level** (read off its shaders and its exe — `docs/Realistic_Lighting.md` has the detail):

- **The ground has no light at all at draw time.** The game draws the ground tile times the detail texture and
  nothing else, so whatever lighting the ground has is *in its tiles*. That is why the bake can put real colour
  there: blue-tinted shade, warm bounce at the foot of a sunlit wall, soft tree shadows, slopes turned from the sun.
- **Objects are lit by two lights and a constant.** The sun (`renderer.diffuseColor`), a **second light from the
  opposite compass side at the same height** (`renderer.SecondaryDiffuseColor` — the engine aims it itself, and every
  retail level sets it), and an ambient (`renderer.LMambientColor` for lightmapped objects, `renderer.ambientColor`
  for the rest). An object's lightmap is one number per texel that scales the two lights:
  `texture x (lightmap x (sun + second light) + ambient)`.
- So the bake does not write "can this texel see the sun". It traces how much light arrives, and **solves for the
  lightmap value that makes the game's own formula draw it** — and for the four light colours that let it.

**1. The light**

- **Clear day / Hazy day / Overcast / Golden hour / Moonlit night** — starting points for the colours, the sky's
  strength and the brightness. They do not move the sun.
- **Sun bearing / Sun height** — where the sun or the moon stands. Moving it is an edit to the level (save writes
  `SkyAndSun.con`).
- **Sun colour**, **Sky colour** (overhead and at the horizon) — the sky's colour is the colour of shade.
- **Sky strength** — how much light the open sky gives against the sun: about 0.2 is a clear day with dark, crisp
  shadows, 0.45 is haze, above 1 is overcast. This one slider is most of the difference between a harsh noon and a
  soft day.
- **Brightness** — how bright a surface facing the sun is drawn. About 1.05 by day; 0.25–0.35 for a night level.
- **Bounced light** — light off the ground and the walls; 1 is what the textures' own colours give.
- **Interior brightness** — the engine draws every ceiling at one fixed level and nothing darker than it. Lower
  gives deeper interiors.
- **Lamp headroom** (only with placed lights, only when the light is dim) — a lightmap can only take the level's
  light *away*, so for a lamp to show on a wall the level's two lights are kept bright and the moon is written low
  in the map. Higher lets lamps light buildings fully, and makes soldiers and vehicles that bright too.

**2. Preview** — **Render preview** ray-traces what the camera sees now, with this light, in a few seconds. Nothing
is baked or changed; it is there to tune the light by looking. It shows the light the bake *aims for*, not what the
game will draw — the game has no ray tracer.

**Live traced view (GPU)** — tick **Live traced view in the viewport (GPU)** under the preview, or use
**View ▸ Traced light (GPU)**, and the main viewport itself shows this light, ray-traced on the graphics card every
frame: the sun and its soft shadows, the sky, two bounces, your lamps, and leaves and fences casting the shadow of
their texture. It follows the camera and every slider in this window; each change starts the picture again, grainy
for a moment, and it clears up while you hold still (it stops adding samples at 2048). Markers and gizmos stay on top,
so you can keep selecting things.

- Only offered on a **dedicated graphics card** (NVIDIA, AMD Radeon RX, Intel Arc). On a laptop with two GPUs set
  RefractorForge.exe to *High performance* in Windows Settings ▸ System ▸ Display ▸ Graphics. The window says why
  when it is not offered.
- The first time it is turned on, the editor checks the card's answer against its own on a small view and refuses
  the mode if they disagree; the window then shows the numbers.
- **Traced resolution**: *Auto* starts at full and drops to a half or a quarter when a frame keeps the GPU too long;
  *Full*, *Half* and *Quarter* fix it. The window shows the size, the samples so far and the GPU time a frame.
- It traces the level as it was when the mode was turned on. After you move objects, sculpt or paint, the window
  says so — press **Rebuild traced scene**.
- It is a picture of the light, like the preview: what the game draws comes from the bake.

**3. Bake** — **Bake realistic lighting** writes:

1. the **ground tiles**, in colour, from the ground as painted (a copy is kept before the first bake, so baking
   twice is baking once, and **Revert Ground Texture to Original** still works). Tiles are saved large enough to
   hold the bake's detail — a level that ships 256 px tiles on a 4 x 4 grid is saved at 512 px;
2. every object's **lightmap**, for each object whose mesh has an unwrap. An object without one gets no file -
   the game ignores a lightmap on such a mesh and lights the object itself (make it lightmap-ready first if it
   matters). The maps are written through the level's own lightmap palette, which is how the game reads them;
3. the **shadow bits** (`LightmapShadowBits.lsb`), with trees and fences casting;
4. the level's **light colours** in Init.con — `diffuseColor`, `SecondaryDiffuseColor`, `ambientColor`,
   `LMambientColor` — and `shadow.shadowColor`, so the moving shadows of soldiers and vehicles are as dark as the
   shade baked under them.

It replaces what earlier lighting bakes put in the ground. **Z** undoes the ground; **Ctrl+S** writes everything.

After a bake **Viewport shows the game's lighting** is on: the viewport draws the level with the game's own formula,
so what you fly through is what the game shows. Turn it off for the editor's always-readable light.

**Placed lights** (Window ▸ Night Lighting places them) are traced with everything else: their pools on the ground
in colour, their shadows — a lamp behind a chain-link fence throws the fence's shadow — and their bounce onto the
walls and eaves beside them. They join the moonlight the way light does, so a lamp over lit ground lifts it a little
and a lamp in darkness is exactly as bright as you set it. On a level lit by lamps the two engine lights take the
lamps' colour, because a lightmap can scale the engine's light but not recolour it: otherwise a sodium lamp would
light a wall moon-blue.

**What the engine cannot show**, whatever is baked — the summary after a bake counts the first two:

- A face that looks **down** (a ceiling, the underside of an awning) is reached by neither light and shows the
  ambient alone.
- Nothing lightmapped can be **darker than the ambient**, so the deepest interiors are a little brighter than traced.
- A lamp's **colour** shows on the ground; on objects only its brightness does (the lightmap is one channel).
- **Soldiers and vehicles** are lit by the level's two lights only. A lamp does not light them as they walk under it.
- Objects with **no lightmap channel at all** stay lit by the two lights, unshadowed. *Lightmap-ready objects…*
  gives them one.

**Battlefield 1942** follows its own rules, measured in the game on a calibration level of its own. An object with a
lightmap is drawn as its texture x 2 x the lightmap and takes no other light, so the bake writes the traced light
itself into the maps, in colour (24-bit) - none of the limits above apply to it: ceilings, deep interiors and a
lamp's colour all come out as traced. Everything without a lightmap unwrap - most props on a retail level, soldiers,
vehicles - is lit by the level's sun and its two ambients, unshadowed; the bake sets those to the pair closest to
the daylight. The ground is baked the same way as on BfVietnam.

**The live traced view and your graphics card.** On a dedicated card with hardware ray tracing (an RTX or
RX 6000-class card) the traced view runs on Vulkan ray queries: the same light, traced by the card's ray-tracing
hardware. Measured on an RTX 4060 laptop with a 746,000-triangle level and a 4K window: 32-39 frames a second at
full resolution and 85-100 at half, where the older OpenGL kernel managed about five. *Auto* resolution drops to
half when a frame takes longer than 20 ms. The window's traced-view line says which one is running; without such a
card the OpenGL kernel is used as before.

**Seeing the light while you set it.** Picking a preset or moving a slider in the window lights the viewport
with that sun and sky at once (*Show this light in the viewport*). It is a quick stand-in - one sun and the sky's
fill, no bounce - and it hides the baked lightmaps while it is on. A finished bake, or ticking *Viewport shows the
game's lighting*, returns the viewport to the baked picture.

**An older Battlefield 1942 custom map that will not start.** A level made before patch 1.4 has no
`game.setMapId` line in its `Menu/init.con`, and the game then loads none of the mod's objects for it: it stops with
"template not found" or simply drops out when the map starts. *Tools > Check Map > Validate Map* reports it, and
*Add Missing Map Id* in the same menu adds the line; save, and the map loads.

**Bake Object Lightmaps** offers *Realistic lighting* as its top quality on both games: the object lightmaps alone,
by this window's light, with the ground left as it is.

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
   object top-down (floors light, walls dark, north up) and writes the line for it. The binding is by template
   NAME, so it follows the tunnel when **Lightmap-ready objects** points it at a level copy (and back again), and
   the line is written after the level's own `run objects/...`, where such a copy exists.
5. **Spawns in the tunnel** need `ObjectTemplate.allowSpawningBelowGround 1` on their template, spelled exactly:
   the game lifts every other spawn below the terrain up to the surface. **Check Map** names each underground
   spawn that is missing it — and a misspelling of it, which the game ignores without a word.

**Tunnel water.** The terrain owns *two* water bodies. `PatchTerrain::getWaterLevel` uses the second one —
`GeometryTemplate.waterBelowLevel` — for any point below the surface or on a hole, but only when
`GeometryTemplate.drawWaterBelowTerrain 1` is set; without it the river fills every corridor at the river's
height, which is why a tunnel under water "hits the water everywhere". Tick **TUNNEL WATER** in the window and
drag the level: it writes the two Terrain.con lines and a `waterBelowTerrain.*` colour block into Init.con (a
mirror of the level's `water.*` lines — exactly what Saigon68 ships, the one retail level that uses it). Keep the
level below the tunnel floor for a dry tunnel, or in a sewer for wading; the viewport draws the second plane.
Cedar Falls does without it by keeping its whole tunnel above the river.

The second body also needs its own **shader**: `StandardMesh/levelWater.rs` with a `WaterSettingBelowTerrain`
subshader. The base game's file has only the surface one, so a map that sets the flag without shipping that file
**crashes as the level loads**. The editor writes it with the flag (and re-checks on every save); *Tunnel water
reflectivity* and *opacity* in the same window set how that body looks, starting from Saigon68's values.

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
  copy under `%AppData%\RefractorForge\Backups` first. Both are flushed to the disk before the map is replaced, so
  a PC that goes down moments after a save can no longer leave the level with its data missing.
  - One thing to watch: a `<Level>_NNN.rfa` sitting beside your map is mounted *over* it by the engine, so its
    copy of a file wins over what you just saved. Save names any it finds in the log — delete or rename them and
    your edits appear.
- **Closing with unsaved work** — closing the window, Alt+F4, File ▸ Exit, switching language or going back to
  the startup screen all ask first when anything has changed since the last save: **Save and exit**, **Exit
  without saving**, or **Cancel**. Everything the undo history knows about counts (objects, gameplay, terrain,
  material and foliage strokes), as do queued level files and the environment edits that Ctrl+S writes back.
- **File ▸ Save as Patch .rfa…** — export only the changed files as an overlay `.rfa` that the engine mounts over
  the map. For shipping an update on top of a map without touching it. A patch is not a separate map: in game you
  still launch the base level.
- **File ▸ Copy Level To…** — the same level under a new name, as Battlecraft's *Copy Level To* does (below).
- **File ▸ Test This Level (in-game)** (Ctrl+L) — save, then launch the game on this mod (pick the map
  in-game — the client can't be told which map to load from the command line).
- **File ▸ Import .obj…** — bring in a Wavefront model as a static object (and export it back as `.sm`).
- **Tools ▸ Convert TGA → DDS** (single or batch), **Tools ▸ Check Map ▸ Validate Map** (missing-asset check)
  and its siblings (bot reachability, performance budget, dependencies, server/client files, compare), **Tools ▸
  Scatter Objects…** (random placement).

### Copy Level To… and renaming a map

A level's name is more than its file name: every file inside a packed level sits under its folder
(`bfvietnam/levels/<name>/…`), and the level's own files spell that folder out. Battlecraft cannot rename a level in
place for that reason, and offers *Copy Level To* instead; RefractorForge does the same.

1. **File ▸ Copy Level To…**, type the **New name** (letters, digits, `_` and `-`; no spaces — the game reads a
   level path as one word). The dialog shows where the copy will be written: beside the original, as
   `<name>.rfa` for a packed level or a `<name>` folder for a folder level.
2. **Copy.** Every file moves to the new level folder, and the paths that point at the level's own folder are
   re-pointed: `Init/Terrain.con` (heightmap, material map, texture and detail paths), the growth `.wst` files'
   `materialMapFilename`, and the `Init.con` / `Menu/init.con` lines that point inside the level
   (`textureManager.alternativePath`, `ShaderManager.setTextureParam envmap`, a `game.setLoadPicture` into its own
   Menu folder). Measured over 105 retail and community levels of both games, those are the only places. File names are left
   alone, and so are mentions that are not paths (comments, briefing keys, a load picture in the menu archive).
3. A packed level's patch archives (`<name>_001.rfa`…) are merged into the copy, and the editor's own files kept
   beside it (lights, groups, notes, growth view) are copied under the new name. A folder copy leaves out its
   `Backups` folder and the project file, which belong to the original.
4. **Open the copy afterwards** relaunches the editor on it.

Nothing is ever overwritten: a name that is already taken is refused. **To rename a map**, copy it to the new name,
check the copy, then delete the old `.rfa` (or folder) yourself. Unsaved changes are not in the copy — it is made
from the files on disk — so the dialog offers **Save first**. Details: `docs/Level_Copy.md`.

---

## 11. Collaboration (Collab menu)

Host or join a live session to edit the same map with others in real time. Edits broadcast to all
peers; connected peers are listed with a "jump to" link to their camera. **Collab ▸ Disconnect** ends it.

### Working on one map at different times (central server, Map sync)

A **central server** (Collab ▸ Collaborate… ▸ Connect to central server) holds the map, so people do not have
to be online together: work offline in your own `.rfa`, connect later, and the editor sorts out what changed
where. Your map stays one packed `.rfa` the whole time.

- **On connecting**, the editor does not replace your map with the server's. It compares both against the
  version your `.rfa` last agreed with the server (a small record, `RF_Sync.txt`, that it keeps inside the `.rfa`;
  the game ignores it) and opens **Map sync**:
  - **New on the server** — who changed what since you last had it, and when ("Bob: 30 changes — objects,
    terrain — 2 h ago").
  - **Your changes not on the server** — what you did offline.
  - **Changed on both sides** — the same object, terrain area or file changed differently by you and by someone
    else. Choose **Keep the server's** or **Keep mine**.
- **Download changes** takes the server's changes into your map and saves them straight into your `.rfa`. Your own
  changes stay.
- **Upload my changes** sends yours. If the server has anything you have not taken yet, it is downloaded first,
  so nothing of anyone else's is written over.
- Once both are done the map is **in step**, and it works like a live session: your edits go straight to the
  server and everyone else's come in as they make them. Collab ▸ shows where you stand; if something of yours has
  not reached the server, **Upload my changes** is offered again.
- **What is compared:** objects one by one, the terrain and material/foliage maps in 32×32-cell areas, the gameplay
  layer, water, lighting and placed lights, notes, imported objects — and **every other file the editor writes into
  the `.rfa`**: ground-texture tiles, lighting bakes (object lightmaps, `.lsb`), sounds, decals, `Init.con` (fog,
  view distance, tunnel and start-camera settings), sky and cloud settings, AI navmaps. Those files reach the
  server when you save; a settings file that arrives reloads the fog, lighting and water panels.
- **Objects keep an id** inside the `.rfa` from the first sync on (a `rem rfid:` comment line per object — the game
  ignores it). An ordinary save of a map that has never been synced is unchanged.
- **A copy saved before this existed** has no record, so the server cannot tell it is the same map. Map sync asks:
  **Compare my copy with the server's map** (your objects are lined up with the server's by what they are, and
  the comparison is made against the map as it started on the server), or **Replace mine with the server's map**
  (yours is kept as `.before-download`).
- **Download the current map as one .rfa** (Collab menu) gives the server's map with everything in it — every file,
  and the record — so whoever opens it is already in step.
- The server needs to be the version that shipped with this editor: an older one still works live, but cannot
  say what changed while you were away, so every difference is shown as a choice.

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
| Raise / lower the AI Path level plane | **PgUp / PgDn** (Shift: 10 m) |

---

*Built by Lucas Ludwiczak, with engineering assistance from Claude (Anthropic). Licensed under the
GNU General Public License v3.0 — see `LICENSE`.*
