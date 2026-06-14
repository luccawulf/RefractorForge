# RefractorForge — How to Build & Run the Native Editor

One native Windows program (`RefractorForge.Viewer.exe`) that renders on your GPU. It is
**framework-dependent** (the .NET runtime is not baked into the `.exe`).

On first launch it **asks you to pick the level folder and the two `.rfa` files through normal Windows
dialogs**, then remembers them — so after the first time you just double-click and go. No editing of
`.bat` files required.

---

## Which .NET to install

| You want to… | Install | Where |
|---|---|---|
| **Run** the editor | **.NET Desktop Runtime 8.0 (x64)** | <https://dotnet.microsoft.com/en-us/download/dotnet/8.0> → *".NET Desktop Runtime 8.0.x" → Windows x64* |
| **Build** it from source | **.NET SDK 8.0 (x64)** | same page → *".NET SDK 8.0.x"*, or `winget install Microsoft.DotNet.SDK.8` |

Notes:
- The "**Desktop** Runtime" (not the plain ".NET Runtime") is required now, because the file pickers use
  Windows Forms. The **SDK 8** you already have includes it, so on your machine you're covered.
- It must be **.NET 8**. .NET 10 alone won't run it (8 and 10 coexist fine).
- Check: PowerShell → `dotnet --list-runtimes` should show `Microsoft.WindowsDesktop.App 8.0.x`.

> The GPU/windowing code is written without me being able to compile it here, so if a build throws a
> `error CS...`, copy the text and send it. The shared core it builds on is test-green.

---

## Step 1 — Unzip

Unzip `RefractorForge.zip` somewhere simple, e.g. `C:\RefractorForge`. You'll get `RefractorForge.sln`,
the `src\` folder, and the helper scripts `quick-test.bat`, `build.bat`, `run.bat`.

**PowerShell note:** to run a script type `.\build.bat` (with the leading `.\`), or double-click it in
Explorer. Typing just `build.bat` fails in PowerShell.

---

## Step 2 — Build & run

**Fastest (build + run together):** double-click **`quick-test.bat`** (or `.\quick-test.bat`).

**Or make a reusable `.exe`:** double-click **`build.bat`** once, then **`run.bat`** to launch.
`build.bat` prints where the `.exe` landed:
```
...\src\RefractorForge.Viewer\bin\Release\net8.0-windows\publish\RefractorForge.Viewer.exe
```

Either way, the **first launch pops a few dialogs**:
1. *Select the extracted level folder* — the folder containing `Heightmap.raw`, `StaticObjects.con`,
   and `Init\Terrain.con` (e.g. `D:\Games\Operation_Irving`). **A folder, not a `.rfa`.**
2. *Select standardMesh.rfa*
3. *Select objects.rfa*
4. *Select texture.rfa* — object textures (huts, palms, vehicles). **Cancel to skip** and objects stay
   flat-shaded. If `texture_001.rfa` sits in the same folder it's picked up automatically. (If you
   don't have it handy, you can also drop `texture.rfa` next to `standardMesh.rfa` and it's found
   automatically — no dialog needed.)

Those choices are saved to `refractorforge.json` next to the exe, so subsequent launches skip straight
to the level. **To pick a different level later, run `run.bat --pick`** (or `quick-test.bat --pick`).

The console confirms what loaded:
```
Loaded D:\Games\Operation_Irving: 512^2 terrain, worldSize 2048, 842 objects.
Opened mesh library from 2 archive(s).
Object meshes: 70 templates, 672 instances; 170 mesh-less markers.
```

---

## Manual commands (optional, PowerShell from the project root)

```powershell
# Build + run, with the picker:
dotnet run --project src\RefractorForge.Viewer -c Release

# Force the picker even if a level is remembered:
dotnet run --project src\RefractorForge.Viewer -c Release -- --pick

# Skip the picker by passing paths explicitly:
dotnet run --project src\RefractorForge.Viewer -c Release -- "<LEVEL folder>" "<standardMesh.rfa>" "<objects.rfa>"

# Produce the standalone app:
dotnet publish src\RefractorForge.Viewer -c Release
```

If no level is selected (you cancel the dialog), it opens a generated demo terrain so you can still
confirm the window works.

---

## Controls (summary — full list in USER_GUIDE.md)

Right-mouse drag = look · **W A S D / Q E** = move · **Shift** = faster · **scroll = zoom** ·
**F** = focus selected · left-click = select · arrows = nudge · **Delete** · **Z/Y** = undo/redo ·
**F5** = save.

---

## Troubleshooting

**Build fails with `error CS...`** — copy the text after "BUILD FAILED" and send it.

**App won't start: "You must install .NET / Microsoft.WindowsDesktop.App 8.0.x not found"** — install
the **.NET Desktop Runtime 8.0 (x64)** (table above).

**First build errors on "restore" / "NU...."** — no internet to reach NuGet; connect for the first
build (packages cache afterward).

**Picked the wrong level / want to switch** — run `run.bat --pick` (or delete `refractorforge.json`
next to the exe).

**Window opens on demo terrain instead of my map** — you cancelled the folder dialog, or the saved
folder no longer exists. Run with `--pick` and choose the level folder (the one with `Heightmap.raw`).

**Terrain shows but no objects** — make sure you picked both `standardMesh.rfa` and `objects.rfa`. The
console line `... 672 instances` confirms meshes loaded.

---

## What renders now vs. next

**Now:** GPU **textured terrain** (the level's real DDS tiles, baked to an atlas, with water blending)
+ the real object meshes **with their textures** (huts, temples, vegetation, vehicles) and alpha-tested
foliage; mesh-less emitters as markers; click-select (tinted), nudge, undo/redo, F5 save; GUI level +
texture picker. The terrain bakes in ~1 s at launch.

**Now also:** **reads `.rfa` levels directly** — point the picker at a packed level `.rfa` and it loads
Terrain.con / Heightmap.raw / StaticObjects.con and the terrain tiles straight from the archive in
memory (verified byte-identical to the extracted folder: same 842 objects, same terrain atlas). Editing
a `.rfa` level currently saves a loose `StaticObjects.con` beside the archive (write-back into the `.rfa`
is a later step).

**Now also:** the **Battlecraft-style UI is built into the .exe** with Dear ImGui (package
`Silk.NET.OpenGL.Extensions.ImGui`, rendered into the same GL window). Menu bar, toolbar (Select/Move/
Rotate/Scale/Place/Paint/Sculpt/Smooth + Undo/Redo/Save + Grid/Snap), left **Object Library** (the real
14-category catalog with search), 3D viewport, right **Inspector** (live-edits the selected object's
Position/Rotation/Scale with full undo), and a status bar. Existing controls (camera, click-select,
arrow-nudge, Del, Z/Y, F, F5) still work and are suppressed while the pointer/keyboard is over a panel.

**Now also:** **object placement from the Object Library** — pick the Place tool, choose a template,
and click the terrain to drop it. A ray-vs-terrain ground-pick (`TerrainPick`, bilinear heightmap
sampling in the exact world mapping the renderer uses; validated against the real level) finds the
surface point under the cursor, a green preview marker shows where it lands, and `AddObject` records it
with full undo. The placed object is auto-selected for immediate Inspector tweaking.

**Now also:** an in-viewport **translate gizmo** (Move tool) — three world-axis handles at the selected
object; hover highlights an axis, left-drag slides along it, release commits one undoable `MoveObject`.
The drag math (ray-to-axis closest-point + axis picking, in `Gizmo`) is unit-tested exact; handles are
drawn over the scene by reusing the marker shader as `GL_LINES`.

**Now also:** **rotate and scale gizmos** (tool-selected). Rotate = three rings (yaw=Rotation.X about Y,
pitch=Rotation.Y about X, roll=Rotation.Z about Z, matching the renderer's YawPitchRoll); drag sweeps an
angle (wrap-accumulated) into that Euler channel. Scale = a uniform handle dragged radially in screen
space. Both commit one undoable command on release. Ring pick / swept-angle / screen-projection math in
`Gizmo` is unit-tested; rings/handles reuse the marker shader (`GL_LINE_LOOP`/points).

**Now also:** **multi-select** — shift-click toggles objects into a set (the primary/last-clicked anchors
the gizmo + Inspector). Move gizmo and arrow-nudge translate the whole group by one delta; rotate/scale
apply the same change to each about its own center; Delete removes all. Each group action is one undo via
the new `CompositeCommand` (unit-tested: a 2-object composite applies and reverts cleanly). The object
highlight now tints the whole set (primary brighter).

**Now also:** **terrain height sculpting** is wired into the viewer. The Sculpt tool raises (Shift lowers)
and Smooth averages, both driving the pre-existing, unit-tested `TerrainEditor`/`TerrainStroke` over the
live heightmap; the terrain VBO is re-uploaded each frame a dab lands. A whole drag coalesces into one
`TerrainEdit`, wrapped by `TerrainStrokeCommand` so it undoes/redoes on the **same Z/Y stack** as object
edits. A ground ring previews the brush; the wheel resizes it; the Inspector exposes radius + strength.

The two-`texture.rfa` startup is fixed: the texture step is now **multi-select** (pick `texture.rfa` and
`texture_001.rfa` together), and the chosen archives are unioned with any `texture*.rfa` siblings found
near the level/mesh archives. The remembered paths (`refractorforge.json`) now store the full list.

**Now also:** **left-right flip fixed** — a view-only `Camera.MirrorX` reflects clip-space X so the editor
matches the game/Battlecraft; data, saved files, picking and gizmos are untouched (they all flow through
the same matrix). And a **gameplay layer**: a new tested `GameplayObjects` parser reads the `Conquest/`
files (ControlPoints + templates for the capture **radius**, ObjectSpawns + templates for the vehicle each
spawner makes — `beginrem` blocks honoured, 23 active of 27 on Irving — and SoldierSpawns). The viewer
loads them (folder + `.rfa`) and draws control-point capture rings + labels, vehicle-spawn markers +
vehicle-name labels, and soldier markers, with the **LAYERS** list turned into real visibility toggles
(Vehicles now lit). This is why no vehicles showed before: they were never in StaticObjects.con.

**Now also: texture-material painting is done.** A `MaterialStrokeCommand` (riding the shared undo stack)
and a `MaterialMap.FromBytes` loader were added and validated in-container (paint a region, undo/redo
restores it, callback fires). The viewer loads `MaterialMap.raw` (folder + `.rfa`), uploads it as an R8
texture, and the terrain shader tints by material index through a 16-colour palette **while the Paint
tool is active**. The Paint tool has live brush painting (begin/drag/commit), a 16-swatch material picker
plus radius/hardness in the Inspector, and a brush ring tinted to the active material. Every prior
regression still passes.

**Next, in order:** (1) **drag from the library + make gameplay objects selectable/movable** (place
vehicles/control points/soldier spawns, edit the capture radius); (2) **save edits to disk** (sculpted
`Heightmap.raw`, the painted `MaterialMap.raw`, gameplay `.con` writers, and a C# `.rfa` writer);
(3) wire the already-built **collaboration** (`EditWire`) into the exe.
