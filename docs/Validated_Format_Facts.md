# Validated Format Facts

Hard-won, reverse-engineered facts about the Refractor-engine map formats used by **Battlefield 1942
(2002)** and **Battlefield Vietnam (2004)** that RefractorForge reads and writes. Everything here has
been validated against real retail maps; where noted, the format round-trips **byte-exact**.

Companion notes: [RFA archive format](RFA_Format_Notes.md) ·
[StandardMesh collision](SM_Collision_RE.md) · [skeletal animation](Skeletal_Animation_Format.md).

All multi-byte integers are little-endian unless stated otherwise.

## Terrain heightmap (`Heightmap.raw`)

- Headerless **16-bit little-endian** grid. The side length equals `materialSize` (the grid is square).
- Height in metres = `raw * yScale / 256` — an 8.8 fixed-point sample scaled by `yScale`.
- Terrain parameters are parsed from `Init/Terrain.con` (by `TerrainConfig`): `worldSize`,
  `materialSize`, `yScale`, `waterLevel`, `seaFloorLevel`, `waveHeight`.
- Sample spacing in metres = `worldSize / materialSize` (e.g. `2048 / 512 = 4 m` per sample).

## Terrain texture tiles: one per 64 x 64 heightmap samples

- The ground texture is `Textures/txCCxRR.dds` (zero-padded, DXT1 with mips), found through
  `GeometryTemplate.texBaseName`. **The tile grid is the heightmap side / 64 along each axis** — nothing in
  `Terrain.con` states it. It is NOT "256 m per tile": that only holds at 4 m sample spacing.
- Measured on 848 textured BF1942 levels (Interstate, stock, DC Final, FH, XPack1/2, FHSW, GC): 685 match exactly;
  156 ship a smaller block inside that grid, placed by `texOffsetX/Y` (e.g. `easter`: offset 4, tiles 0..7 of a
  16-grid; naval maps texture only the middle); 7 ship a stray extra row/column or keep the heightmap in a patch.
- Examples: `SwimmingShores` 1024 heightmap over 4 km = 16x16 tiles of 256 m; `big_map` 1024 over 32 km = 16x16 of
  2 km; `128_planes` 2048 over 32 km = 32x32 of 1 km; `40_thousand_feet` a 128 heightmap = 2x2.
- Tile size is free: 256, 512, 1024, 2048, 4096 px all ship (retail `Tobruk` uses 4096 px tiles; most Interstate maps
  1024 px on 256 m tiles = 4 px per metre). Both games load textures up to 4096 px.
- A tile's first texel row is its SOUTH edge (world Z low), like the heightmap's first row.

## Coordinate system & rotation

- `+X` = east, `+Z` = north, `+Y` = up. No axis mirroring.
- Object rotation is stored as Euler degrees, where **X = yaw, Y = pitch, Z = roll**.

## Reference maps (validated numbers)

Useful known-good values for testing loaders and math:

| Map | Engine | materialSize | worldSize | yScale | waterLevel | seaFloor | waveHeight |
|---|---|---|---|---|---|---|---|
| Operation_Irving | BF Vietnam | 512 | 2048 (4:1) | 0.35 | 30 | 0 | 1.0 |
| 128_planes | BF 1942 | 2048 (heightmap) | 32768 | 10 | −1436 | — | — |

**Operation_Irving (BFV)** — 842 static objects across 84 templates; US forces south, NVA north. Sun and
sky come from `Init/SkyAndSun.con`: `sky.sunLightDirectionVec 0.64 / 0.34 / -0.68`, `Sky.setRotAngle -45`,
skybox mesh `Sky_OI_m1` with six `env_default_0N.dds` cubemap faces, and `Terrain.ShadowAmbient 80/80/80`.

**128_planes (BF1942)** — a fully **uncompressed** `.rfa` with a large 2048² heightmap. A good stress test
for big maps and for the uncompressed-archive code path (see RFA note below).

## Foliage / growth maps

- `UnderGrowthMap.raw` is 1024²; `OverGrowthMap.raw` is 512².
- These are **discrete index maps** — each byte is a palette index in the range **0–14**, *not* a 0–255
  density. Indices correlate with the terrain material.
- The `.wst` palette (XML) describes the available growth types. RefractorForge parses it for display and
  copies it through **verbatim** on save; painting mutates only the `.raw` index maps.
- Gotcha: `overGrowth.wst` ships with a stray leading space before `<?xml` — trim it before parsing.

## Minimap

- Produced by a CPU top-down render — real texture-atlas colour, falling back to the material palette and
  then a flat colour, plus hill-shading and a water tint. North-up, east-right.
- Written as **uncompressed BGRA DDS**: `Textures/InGameMap.dds` (512²) and `Menu/Thumbnail.dds` (256²).
- It is a literal render of the true water line and terrain, not the stylised retail map art.

## Terrain sun shadows & lightmaps

- **Cast-shadow bake.** `TerrainShadow.Bake` ray-marches the sun direction against the heightmap to build a
  shadow-visibility map, UV-aligned to the terrain atlas — texel `(x, y)` maps to world
  `(x/size · worldSize, y/size · worldSize)` — so the terrain shader samples shadow and ground with the same
  UV. Exported as `TerrainShadow.dds`.
- **Engine lightmap (`Textures/LightmapShadowBits.lsb`).** A run-length-encoded format: a 12-byte header
  (`width`, `height`, `1`) followed by token pairs flagged by bit 13. RefractorForge decodes *and re-encodes
  it byte-exact*, and writes it back to the game — validated against real BFV `.lsb` files
  (`Formats/Terrain/LightmapShadowBits.cs`).
- **Per-object lightmaps** are a separate system: `ObjectLightMaps/*.tga`, named
  `<template>_<x>-<y>-<z>.tga` (the world position is truncated to integers). 8-bpp paletted TGAs carrying an
  embedded grayscale ramp — 4,088 of the 4,152 shipped maps are exactly that (the other 64 are 24-bit truecolour).
- **1024² is the hard ceiling for an object lightmap, and 2048² CRASHES THE GAME.** Measured across all 35 retail
  Battlefield Vietnam levels, the 4,152 object lightmaps are **256² (68%), 128² (15%), 512² (15%) and 1024² (1.6%)
  — and not one is larger**. The four levels that use 1024s are `Saigon68`, `Fall_of_Saigon`,
  `Operation_Cedar_Falls` and `Landing_Zone_Albany`, so that size is proven in the shipping engine. A 2048 option
  was offered in the editor for exactly one build and the user's game crashed on the map that used it; it was
  removed. The distribution reads like a taste until you try to exceed it — it is a limit. The editor's bake tops
  out at 1024 (`ApplyLightmapQuality`, quality "High").
- **Bake quality is sub-samples, not just size.** The sun test is binary — a texel is lit or it is not — so one
  sample per texel puts a hard staircase on every shadow edge. `ObjectLightmapBaker.Bake(samples:)` averages an
  N×N grid inside each texel, which is what turns that step into a gradient. The same applies to
  `TerrainShadow.Bake(samples:)`, which additionally samples the heightmap **bilinearly**: sampling it nearest
  made every shadow edge follow the heightmap's own grid (one height per 8×8 block under a 2048² shadow map),
  which is most of what "jagged shadows" meant.

## RFA archives (summary)

The container and its LZO1X payload are fully decoded (`Rfa/RefractorFlatArchive.cs`, `Rfa/Lzo1x.cs`); the code is
the reference, not the older byte-layout notes in RFA_Format_Notes.md.

- **The header flag decides the entry form.** In an uncompressed archive (flag 0, e.g. `128_planes`, BFV
  `sound.rfa`) every entry is raw bytes. In a compressed one (flag 1) EVERY entry is a block table of LZO1X streams
  - the engine decodes them all. An entry stored raw, or a block stored verbatim, inside a flag-1 archive is read as
  a broken stream ("Couldn't decompress block"); a garbled `.con` then leaves templates undefined and BF Vietnam
  crashes with a Runtime Error. A census of 9,207 archives found no shipped archive doing either. Incompressible
  blocks are written as literal-only LZO streams, as retail does.
- A compressed entry whose streams add up to exactly its own size exists in retail (875 of them, e.g. BFV
  `M91Deploy.baf`): the size alone does not mean raw.
- Every entry name must start with the archive's own path under `Archives\` minus `.rfa` and `_NNN`; one violation and
  the engine rejects the whole archive ("Error loading file list").
- The table is closed by a `u32 0` in every clean retail archive; new archives write it.
- **Each executable mounts a LITERAL archive list** (`GameMounts.cs`, from the string tables in BF1942.exe and
  BfVietnam.exe; `GameMountsInstallTests` re-checks it against both). A `_001` patch exists only for BF1942
  `standardMesh`/`texture`/`sound`/`menu` and BFV `texture`/`sound`/`animations`, and outranks its base; there is no
  `objects_001`, BFV `standardMesh_001`/`menu_001`, or any `_002`, and nothing in a sub-folder but `<base mod>\game.rfa`
  and `<base mod>\levels\<Map>[_NNN].rfa` (a level's patches stack highest number first); an `Archives\Archives\`
  inside a mod is only a sub-folder (FHR's font.rfa there is never read). `ModChain.CollectArchives`,
  `ModWorkspace.LayersFor` and the editor's own load path (`LibraryArchives`, a level opened directly or by Open Mod)
  follow it: BF1942 `mg42_r` resolves from `texture_001` (1024 px, not the 256 px base), BF1942's 130
  `StandardMesh_001` overrides win, and BFV_WW2Mod's unmounted `standardMesh_001` stays out.
- **In-game proof (RefractorDevelopmentKit gate G0, both games, 2026-09-23):** archives written from scratch this way
  load, compressed and uncompressed, with a retail `ai.rfa` descriptor and XPack ID Default.

## StandardMesh collision: COL02 is the one that stops you (in-game)

A mesh may carry two collision sections, COL01 (coarse) then COL02 (detailed). In RefractorDevelopmentKit gate G0.3 a
stone arch whose COL01 was a solid box and whose COL02 was the arch's own shape could be walked AND driven through -
in both games. Soldier and vehicle movement collide with COL02; a coarse COL01 does not close a gap. So an object's
real shape belongs in COL02. Still open: whether a mesh with ONLY a COL01 section (what `ModelObject.Build` writes
from the "collision" checkbox) blocks movement at all.

## Verifying these facts

The headless `RefractorForge.Demo` harness and `RefractorForge.TerrainTests` exercise these formats — e.g.
`rfaroundtrip` (archive byte-exactness), `lsbroundtrip` (lightmap byte-exactness), `foliageedit`, `minimap`,
and `shadowbake`. See [BUILD_AND_RUN.md](BUILD_AND_RUN.md) for the commands.


## Out of bounds: the combat area is only half of it - material 7 is `deathMaterial`

Cracked from `BfVietnam.exe` (static disassembly, 2026-09-06) after a custom map kept reporting OUT OF COMBAT AREA
hundreds of metres inside every rectangle it was given.

`Game::isOutsideWorld(x, z)` - `Game` vtable slot 23, impl `0x004F9060`, reached through the Game singleton at
`[0xD45950]` - decides "outside" in two steps:

1. Past the rectangle. The rectangle is `game.setActiveCombatArea x z w h` when set (flag `Game+0x119`, rect
   `+0x11C..+0x128`; `setActiveCombatArea` also sets the flag), otherwise `(0, 0)`-`(worldSizeX, worldSizeZ)` from
   the terrain. Offset is measured from the south-west corner; `Operation_Game_Warden` keeps its whole US base
   inside only under that reading.
2. **Then, even inside the rectangle**: `PatchTerrain::getMaterialAt(x, z) == 7` (terrain vtable slot 21, impl
   `0x00721C50`; cell = `floor(x * cells / worldSize)`, a 4-bit bitmap at `PatchTerrain+0x3A0` loaded straight from
   `MaterialMap.raw` - **no palette is consulted**). The engine's own material table is `default, water, dryGrass,
   juicyGrass, dryDirt, wetDirt, mud, deathMaterial, gravel, muddyWater, drySand, wetSand, rock, sandRoad,
   dirtRoad, pavelRoad`: **index 7 is `deathMaterial`.**

Everything downstream hangs off that one predicate: `ObjectTemplate.damageForBeingOutSideWorld × dt` is applied
when it returns 1 (`0x0069729F`), and the HUD elements are literally `Outside`, `OutsideText`, `OutsideTime`.

Retail uses material 7 on purpose. Of the 84 stock BFV levels, 47 paint it across 60-90 % of the map - it is the
void around the playable island (retail Saigon68: 79.9 %) - and every stock spawn sits on something else. A custom
map that lays content across that void **without repainting the material map** inherits a kill zone that no
Init.con setting can remove: a vehicle "spawns in the right place and blows up", a flag shows OUT OF COMBAT AREA to
whoever walks up to it, and the transport pad 58 m away is fine because it happens to sit on index 14.

RefractorForge: `Formats/Terrain/DeathMaterial.cs` (engine-exact lookup, count inside an area, `Repaint` - water
below the water line, nearest painted land material elsewhere, boundary left alone, undoable); the map check flags
every control point, vehicle spawner and soldier spawn on 7; the Combat Area panel shows the count and offers
*Repaint deathMaterial inside area*; the painter labels index 7.

---

## Lamps cannot be lit by a lightmap — an object lightmap only ever SUBTRACTS light

A lamp hung in a tunnel lights nothing beneath it, and no amount of painting the per-object lightmap changes that.
This is not a bug, it is the shader. `effects/RaShaderPPLSTs1DifLmp.fx` (from `effects.rfa`, BfVietnam) reads the
map as **one scalar taken from the blue channel**, which then **multiplies the sun**:

```hlsl
float  Prelight          = tex2D(LightMapSampler, VsData.Tex2).b;      // ONE channel, not colour
float3 LightDOT3         = LightMaterialColor.rgb * saturate(dot(TN, LD))
                         + secondaryLightColor    * saturate(dot(TN, BL));
float3 FinalDiffuseLight = (Prelight * LightDOT3) + LightAmbient;
FinalColor.rgb           = saturate(2 * FinalDiffuseLight) * TD + (Prelight * specular * gloss * ...);
```

So a lightmap texel can only scale light the sun is already delivering. It can never be **warmer** than the sun,
never **brighter** than the sun, and never present **where the sun is not** — which is exactly a tunnel. Painting a
warm pool into an object lightmap is a no-op there. The colour is thrown away too: only `.b` is sampled.

Two corollaries worth knowing:

* **The per-vertex variant is different.** `RaShaderPVLS1DifLmp.fx` is fixed-function and uses the lightmap as
  **full RGB** (`ColorOp[0] = MultiplyAdd, Arg1 = Texture, Arg2 = Diffuse, Arg0 = tFactor(.5)`), then modulates the
  diffuse map. Which path a material takes is not ours to choose reliably, so nothing should depend on it.
* **Retail's own object lightmaps are grey.** Measured across Operation_Irving and Saigon68: `maxChroma == 0` on
  every one. They are sun-visibility masks and nothing else.

**Dynamic lights do not exist.** A frame capture of a running BfVietnam (`BFVDynamicLights.log`, build 8) shows
`LIGHTING on/off = 0/2189` — fixed-function lighting is disabled on every single draw — with the game setting 24
directional lights and **zero point or spot lights**. Injecting D3D lights via `SetLight`/`LightEnable` reaches
nothing; that route was tried and measured, and it is a dead end.

### What DOES put light on a surface: an additive quad

41 shipped materials — every muzzle flash, `standardMesh/e_MuzzAK47_m1.rs` among them — use this, and the `.rs`
grammar accepts all of it on an ordinary StandardMesh:

```
lighting false;          lightingSpecular false;   materialSpecular 0 0 0;
materialDiffuse 1 1 1;   selfillum 1 1 1;          opacity 1;
transparent true;        sortedBlend true;
blendSrc sourcealpha;    blendDest one;            depthWrite false;   twosided true;
texture "texture/<name>";
```

`blendSrc sourcealpha` + `blendDest one` is **additive**: the quad can only ever brighten what is behind it.
`lighting false` + `selfillum 1 1 1` make it immune to the level's ambient, so it stays bright on a night map.
Laid flat under a lamp it is a pool of light; stood upright at the bulb it is a halo. It needs no lightmap UV, no
mesh edit, works on terrain and on meshes alike, in both games, at any detail setting.

RefractorForge: `Formats/Cons/LightPool.cs` builds it through the same six-file level-local recipe as
`DecalObject`; `RsWriter.Glow` writes the material; the Lights panel has *Make light pool*, *Put one under this
light* and *Put one under every lamp object* (the last two place without a terrain raycast, so they work inside a
tunnel where a click would hit the terrain overhead). Tuning measured, not guessed: the falloff must have **no flat
core** — a plateau of even 30 % of the radius blows out to a white disc — so it is `pow(smoothstep_down(t), 0.6 +
softness*2.4)`, and the falloff lives in the **alpha**, not the colour, so the lamp keeps one hue from centre to
rim. Pools **stack** additively, so overlapping lamps need the brightness eased.

### How El Alamein Nights (BF1942, BFHeroes) gets its night look

Worth recording because it is the reference for "convincing night lighting", and it is *not* doing what BFV can do:

* 501 per-object lightmaps as **1024² DXT1 `.dds`** (699,192 bytes each = 128-byte header + a full mip chain),
  and they are **coloured** — mean RGB 22,40,74, max chroma 116-140. Moonlight blue with warm hot spots. BF1942's
  lightmap path uses the colour; BFV's per-pixel path would throw all but the blue away.
* `Init.con`: `globalAmbientColor 0.1`, `ambientColor 0.1`, `diffuseColor 0.1`, `specularColor 0.4`,
  `animatedMeshAmbientColor 0.18`, `fogColorVec 0.016/0.160/0.285`, `fogStart 25`, `fogEnd 300`.
* Only 11 objects (`telephone_light_m1`) keep the older 256² 24-bpp `.tga` form.

---

## A mesh with no lightmap unwrap must get NO lightmap file — a blank one turns the game WHITE

This section previously said the opposite, and shipping that turned a user's map into a solid white screen. The
correction matters more than the original observation, so it is recorded in full.

A debug build of BfVietnam asserts when a lightmap-channel mesh has no map — **260** times in one Saigon68 run:

```
Module: RendRa
File: C:\dice\trunk\vietnam\RendRa\RaShaderPVLS1DifLmp.cpp
Line: 84
Expression: m_LightMapD3DH
```

`m_LightMapD3DH` is the lightmap's D3D texture handle, and it is null because no map exists. It is tempting to read
that as *"the engine requires a file for every lightmap-channel mesh"*. **It does not.** The RETAIL client handles
the absence perfectly well — it lights the object dynamically — and that is how these levels have always looked.

Supplying a file to satisfy the assertion is actively harmful. A mesh with no unwrap can only be given a FLAT map,
which sets `Prelight` to 1 across the whole object, and the shader

```hlsl
FinalColor.rgb = saturate(2 * (Prelight * LightDOT3 + LightAmbient)) * TD
```

then saturates: **the entire scene renders solid white.** Absence is the correct state; a blank is not a safe
default.

**The general lesson: a debug assertion is a diagnostic, not a contract.** Before changing behaviour because a
debug build complains, check what the retail client does — it is what players run. Note also that
`Log_*_Server.log` and `Debug_*_Server.log` are only written when HOSTING from the debug exe, so their absence
does not mean the game was not run.

### Telling a real lightmap from a blank

A guard that refuses to overwrite "what the level already ships" cannot, on its own, tell DICE's baked map from a
placeholder — so it will faithfully preserve blanks through every re-bake. `ObjectLightmaps.HasDetail()` settles
it: a shipped object lightmap is an 8-bit colour-mapped TGA, one palette index per texel, so *flat* is simply every
index being the same byte — no decode, no palette. Flat = placeholder (replaceable, and removable); any variation =
real lighting (keep). Retail sizes are 128/256/512/1024; the largest across all 97 levels and 4,008 maps is 1024,
and 2048 crashes the game.

### The lightmap file name is a LEAF, never a path

`GeometryTemplate.file` is free to carry a relative path, and Saigon68's `city_dumpster1` really is
`../standardMesh/city_dumpster1`. Used verbatim as the lightmap file name it produced

```
BfVietnam/levels/Saigon68/ObjectLightMaps/../standardMesh/city_dumpster1_449-10-173.tga
```

— 15 entries that normalise straight **out** of the folder the engine reads, so those objects had no lightmap and
joined the assertions above. Every retail level stores leaf names only. `ObjectLightmaps.FileBase` does this
reduction and both the bake and the unbake path go through it.

The same `../standardMesh/` shape breaks shader lookups when a level-local object's geometry reference carries it —
the same run logged `Couldn't load shader "StandardMesh/../standardMesh/explFire_Test_m1/explFire_Test_m1_Material0",
loading default shader instead`.

### Folder case does not matter

Retail is inconsistent with itself and all of it works: `Saigon68/ObjectLightmaps`,
`Operation_Irving/objectlightmaps`. Case is not the cause of a missing lightmap — look for the name or the folder
escape instead.

## Init.con: `water.*` must come AFTER `run Init/Terrain`

The terrain is what creates the water object, so a `water.*` line above `run Init/Terrain` applies to nothing and
**kills the dedicated server during load** — it exits before it logs `GameStart`. Measured on BfVietnam 1.21: one
stray `water.color` at line 31 with the terrain run at line 97 killed `bfvietnam_w32ded.exe` 1.5 s in; moving that
single line below the terrain run fixed it, and the value was irrelevant (three different colours crashed alike).
Retail agrees — `Fall_of_Saigon` runs the terrain at 97 and sets water at 99–103.

`EnvironmentSettings.PatchInitConLines` used to add any missing setting straight after the last `renderer.*` line,
which wrote exactly this defect into every map that had no water block yet. Now water keys go below the terrain
run, and a stray one already too high is moved down. Gate: `src/RefractorForge.Tests/InitConWaterOrderTests.cs`.

**How to prove a server-side fault at all: `docs/Dedicated_Server_Debugging.md`.** `bfvietnam_w32ded.exe` is
headless and answers in ~25 s, which beats reading the map — five rounds of static auditing missed this because a
static audit cannot see ordering.
