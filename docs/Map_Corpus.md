# What a Battlefield map actually is: measurements from 325 shipped levels

Measured 2026-09-06 by reading every `.rfa` under both games' `levels` folders on this machine and recording one
row per map. Read-only; no map was modified. The raw table is `corpus.csv` (one row per map, 27 columns).

**What this corpus is.** 242 Battlefield 1942 levels and 83 Battlefield Vietnam levels *as installed here*. DICE
shipped roughly thirty maps per game, so the great majority of these are **community maps**, not retail. That is
worth knowing before treating any figure as "how DICE did it" — but for the purpose these numbers exist for
(giving a generator sane defaults, and giving the map check sane thresholds) the wider set is the better sample:
it is what people actually build and play. Where retail and community diverge sharply, the range columns show it.

Six levels could not be read: four are add-on maps that ship gameplay only and layer over a base map's terrain
(`Coop_Coral_Sea`, `Do_The_Harlem_Shake_I`), and two have a heightmap that disagrees with their declared size
(`Battle_Of_The_Atlantic` declares 1024² but ships half the bytes). The loader is right to refuse those.

## The numbers a map is built from

| | BF1942 (242) | BFV (83) |
|---|---|---|
| World size | **1024 or 2048** — 111 each, 92% of all maps | **1024 or 2048** — 57 / 26 |
| Relief (lowest to highest ground) | 82 m median, 20–153 m typical | 84 m median, 14–230 m typical |
| Mean slope | 7.0° | 10.0° |
| Ground steeper than 30° | 5.8% | 6.3% |
| Dry land (above the water line) | 90% | 86% |
| Control points | **5** (2–8) | **5** (2–8) |
| Nearest-CP spacing | 204 m | 172 m |
| Vehicle spawners | 30 | 16 |
| Soldier spawns | 24 | 12 |
| **Soldier spawn to its nearest CP** | **26 m** (10–63 m) | **21 m** (10–57 m) |
| Static objects | 790 | 309 |
| Distinct templates | 55 | 37 |
| Objects per km² | 288 | 151 |
| Vehicles per control point | 5.5 | 3.7 |
| Soldier spawns per control point | 4.7 | 3.0 |

## Flag count follows map size, and both games agree

| World | BF1942 | BFV |
|---|---|---|
| 1024 m | 5 CPs, 163 m apart (×108) | 4 CPs, 157 m apart (×57) |
| 2048 m | 6 CPs, 245 m apart (×110) | 6 CPs, 218 m apart (×25) |
| 4096 m | 6 CPs, 429 m apart (×18) | — |

**Metres of world per control point comes out at 341 m in BOTH games** — the same median from two separately
built sets of maps. Flag count grows far more slowly than area: quadrupling the map adds roughly one flag and
doubles the walk between them. A generator that scales flags with area will produce something that feels nothing
like either game.

## Two findings that change how the editor should behave

**deathMaterial is not an edge case.** 131 of 242 BF1942 maps and 46 of 83 BFV maps paint material 7 at all — and
on the maps that use it, the **median share is 60% (BF1942) and 65% (BFV)**. It is the single most-painted index
in both games. This is the corpus confirming from the other direction what disassembly showed: material 7 is how
these games fence a map, so anything built across it is out of bounds and no `Init.con` setting will help. See
`Validated_Format_Facts.md`.

**A combat area is usually the whole world.** 83–86% of maps declare `setActiveCombatArea`, but the median
declared rectangle is **100% of the terrain** — the line is present and does nothing. Sub-rectangles are the
minority, and on BFV the 10–90% band is 100%–100%: a smaller box is rare enough to be worth a second look when
one appears. That matches what the tunnel maps showed.

## The material palette in practice

Share of maps that paint each index at all, and the median share of the map where they do:

| | BF1942 | BFV |
|---|---|---|
| 0 default | 97 maps, 0.5% | **47 maps, 58%** |
| 3 juicyGrass | 156 maps, 6.0% | 46 maps, 14% |
| **7 deathMaterial** | **138 maps, 60%** | **46 maps, 65%** |
| 12 rock | 141 maps, 2.3% | 26 maps, 1.3% |
| 11 wetSand | 109 maps, 3.7% | 31 maps, 1.9% |
| 1 water | 48 maps, 4.6% | 26 maps, 3.8% |
| roads (13/14/15) | 54 / 97 / 65 maps, ~0.5–1% each | 16 / 21 / 21 maps, ~1% each |

The two games paint differently: BF1942 names its ground (juicyGrass, rock, sand), while **BFV leaves 58% of a
typical map on index 0** and paints only what needs to differ. Roads are always a fraction of a percent — a road
is a thin ribbon, and any generator painting more than ~2% in road materials has made them far too wide.

## What this is good for

- **Defaults for a generated map**: 1024 m, 5 flags about 160 m apart, ~25 soldier spawns clustered within 25 m
  of their flag, ~28 vehicles, ~800 objects for BF1942 or ~300 for BFV, 80–90% dry land, 80 m of relief.
- **Thresholds for the map check**: flag spacing far outside 70–540 m, spawns more than ~60 m from any flag,
  object density above ~1100/km², or road materials over a few percent are all worth flagging as unusual.
- **Sanity limits**: nothing here justifies a 32768 m world (two maps) or 260 vehicle spawners (one map).

## Reproducing it

The harvester is a small console program against `RefractorForge.Formats` + `.Render` — it opens each archive with
`LevelArchive.FromRfa` and reads `TerrainConfig`, the heightmap, the material map, the gameplay objects and
`EnvironmentSettings`. It is kept in the scratchpad rather than the repo because it points at absolute install
paths; the measurements it produced are what matter and they are above.
