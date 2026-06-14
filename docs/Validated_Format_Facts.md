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
  `<template>_<x>-<y>-<z>.tga` (the world position is truncated to integers). They are 256², 8-bpp paletted
  TGAs carrying an embedded grayscale ramp.

## RFA archives (summary)

The container is fully decoded and the LZO1X-style payload is ~85% decoded; archives round-trip byte-exact.
One quirk worth flagging here: a data block whose `blockSize == uncompressedSize` is stored **uncompressed**
and must be returned verbatim — decoding it as compressed crashes on otherwise-valid uncompressed maps (e.g.
BF1942's `128_planes`). Full byte layout in **[RFA_Format_Notes.md](RFA_Format_Notes.md)**.

## Verifying these facts

The headless `RefractorForge.Demo` harness and `RefractorForge.TerrainTests` exercise these formats — e.g.
`rfaroundtrip` (archive byte-exactness), `lsbroundtrip` (lightmap byte-exactness), `foliageedit`, `minimap`,
and `shadowbake`. See [BUILD_AND_RUN.md](BUILD_AND_RUN.md) for the commands.
