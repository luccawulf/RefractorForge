# Battlefield Vietnam growth placement (overgrowth, undergrowth)

**Status (2026-10-29).** Overgrowth placement is reversed end to end from the **stock** `BfVietnam.exe`
(ImageBase 0x00400000), reimplemented in `tools/bfv_growth/overgrowth_place.py`, and checked visually
against an in-game screenshot of Operation Irving's US base: with the level's own `_M1` meshes placed at the
generated positions, the palms land where the game puts them (the row along the shore west of the hangar,
the cluster on the slope to its north-east). It has **not** yet been compared tree for tree with a capture.
Undergrowth: the shared code is identified (below); its own details are being verified
(`docs/BFV_Undergrowth_RE.md` when done).

The full evidence, every claim tagged verified / inferred / unknown with its address, is
`docs/BFV_Overgrowth_RE.md`. This page is the editor-facing summary.

## What it means for RefractorForge

`OvergrowthFoliage` (Formats/Terrain) is a **statistical** scatter today: the painted 4 m map as the grid,
about 2.1 trees per occupied patch (measured on the Veg-Ultra build), the probability roulette, uniform yaw
and scale, the patch seed `cellY*4711 + cellX*13 + 23`. The reversed algorithm is **exact** for the stock exe,
so for stock maps it can replace the density model and the editor can preview (or bake, see
`OvergrowthCapture`) the stock game's forest without a capture.

Where it disagrees with the 2026-09-08 notes in `OvergrowthFoliage.cs` (decide with a capture, see
Validation):

1. **Patch layout.** The notes describe a COUNT x COUNT (10) window of patches kept around the camera,
   viewDistance/4 apart. This pass found `OverGrowthManager` generating the **whole map once, at level
   load**, in square cells of viewDistance/4 rounded so a whole number of cells fits the map (Irving,
   viewDistance 550: 14 x 14 cells of 146.2857 m). The camera-windowed generator is undergrowth's
   (`GrowthManager::vf11`, lazily per display patch). Both readings agree on viewDistance/4 and on the seed.
2. **Density** is set by a **per-cell geometry budget**, not a per-patch count: 2048 vertices and 8192
   indices (one patch draw buffer). Each placed instance subtracts its `_m2` mesh's vertex and index counts
   (first LOD, first material), and the first candidate whose type no longer fits **ends the cell**, even if a
   cheaper type would. About 10 palm clusters per 146 m cell on Irving.
3. **Spacing** is a flat XZ test against instances already in the **same cell only** (neighbours never
   checked): two of the same geometry are rejected closer than `R * minRadiusDistToEquals * scale` summed over
   both, different geometries use `minRadiusDistToOthers`; `R` is the radius of the `_m2` mesh's bounding box
   about its origin (`c02f_trees_m2`: 20.66 m).

The `BfVietnam_Veg_*.exe` builds (COUNT 80/34, hard-coded 12.5/17.5 m, the tier hash at 0x760950, as the
notes record) were not re-examined here: the loop below is the stock exe's.

## The algorithm (stock BfVietnam.exe)

1. **Cells.** Whole map at load; cell size from `viewDistance` as above. Video options never change
   `densityScale` or `viewDistance`.
2. **Seed per cell:** `(23 + 13*i + 4711*j) & 0x7fffffff`, `i` the x index, `j` the z index.
3. **RNG:** multiply-mod, modulus 2^31 - 1, effective multiplier 630360016 (0x4e0990). A draw becomes a
   value in (0, 1] as `(((r | 0x80) >> 7) + 1) * 2^-24`.
4. **Loop** (0x760950): 256 iterations (2048 / 8); a placed instance uses 2, a rejected one 1. Per candidate,
   in this order: x, then z, uniform over the cell square; the type, walking the material's `probability`
   list in `.wst` order and taking the first with `u < p`, else `u -= p` and on (no normalising, so a list
   summing below 1 leaves a chance of nothing); the scale, `CRDUniform/a/b` = `a + (b - a) * U`; the yaw,
   `-180 + 360 * U` degrees.
5. **Material** at the candidate: `OverGrowthMap.raw`, row = z, column = x; the `.wst` `<materials>` children
   follow the engine's material table (0 default, 1 water, 2 dryGrass, 3 juicyGrass, 4 dryDirt, 5 wetDirt,
   ... 7 deathMaterial ... 15 pavelRoad).
6. **Budget and spacing** as above.
7. **Orientation:** forward `(sin yaw, 0, cos yaw)`; up = `normalize(n.x * normalScale, n.y, n.z *
   normalScale)` from the terrain normal; y = the terrain height at (x, z) by the engine's triangle
   interpolation (checked against Irving's hand-placed trees: median error 0.00005 m).
8. **`importSceneObjects`:** only statics whose mesh is listed in `standardMesh/overGrowthGeometries.wst`
   are pulled into their cell before generation; they use the cell's budget first and act as spacing
   obstacles; any that do not fit stay plain statics. Buildings do not block growth: only the growth map
   decides.
9. **Drawing:** far away, each cell draws its instances' `_m2` meshes as one batch, with no fade
   (`fadeRange` is forced to 0), so cells pop in at viewDistance. Every instance is also a real `_m1` object
   with collision (`HasCollisionPhysics 1`), drawn to about viewDistance x 0.2 x the quality factor
   (0.7 / 1.0 / 1.3).

## Validation

Capture the stock exe with the bfvVegetationCapture tool on a level, read `tree_dump.bin` with
`OvergrowthCapture.Read`, and compare it with `overgrowth_place.py --json`: the same instance count, and
positions equal to float precision. Operation Irving is the level this pass was worked on.

## Irving reference numbers

1449 generated instances (908 `c02f_trees_m2`, 370 `c08f_jungle_m2`, 139 `c01f_trees_m2`, 29
`o_rocks01_m2`, 3 `c04f_trees_m2`) plus 147 statics pulled into overgrowth (26 left plain because their cell
was full). Around the US base hangar (world 805, 502): 19 generated within 120 m, the nearest a palm 63 m
west; nothing within about 50 m, where the hangar stands on dirt road and default material.

## Undergrowth (status)

From the code it shares with overgrowth: the same loop, RNG, roulette, budget and spacing. It generates
**lazily per display patch** around the camera (`GrowthManager::vf11` 0x75e140), seeded
`(pz*4711 + px*13 + 23)`; the budget comes from the patch buffer `RaStreamDataEditable(0x400, 0x1000,
0x1c)` (0x71f987): 1024 x densityScale vertices and 4096 indices (which getter is which is inferred);
viewDistance defaults to 70 and `ps = 256 / floor(256 / (vd / 4))` (0x71fe20, 0x71fda0); no import and no
close-range objects. Being verified; this section will be replaced by `docs/BFV_Undergrowth_RE.md`.

## Files

- `docs/BFV_Overgrowth_RE.md`: the full report (layouts, addresses, confidence tags, open questions).
- `tools/bfv_growth/overgrowth_place.py`: the reference implementation (Python 3 + numpy).
  `python -I tools/bfv_growth/overgrowth_place.py --level <level dir> --meshes <standardMesh dir>
  --objects <objects dir> [--cx 805 --cz 502 --radius 120] [--json out.json]` prints the count per type and
  the instances near a point; `--json` writes every instance (cell, geometry, position, scale, yaw, up,
  rotation rows).

Open questions (details in the report): whether `.wst` materials are matched by name or by order (the same
on Irving), the generic loader behind `run growth/overGrowth`, a camera factor in the near/far switch, the
`CRDExponential` / `CRDNormal` distributions (no retail level uses them), the rock mesh's counts, and whether
the dedicated server creates the collision objects too.
