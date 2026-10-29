# Battlefield Vietnam undergrowth, reverse-engineered

This companion to `docs/BFV_Overgrowth_RE.md` covers undergrowth (summary: `docs/BFV_Growth_Placement.md`). Source is `BfVietnam.exe` (ImageBase
0x400000), read with capstone plus the lifted C, read-only. Every address is a VA.

Tags:
- **[V]** verified by reading the instructions.
- **[I]** inferred from code plus data.
- **[?]** unknown.

Reference implementation: `tools/bfv_growth/undergrowth_place.py`. It imports the shared helpers
(RNG, distributions, `.wst`/`.sm` readers, terrain) from `overgrowth_place.py`.

## TL;DR

1. **Generation is lazy and per display patch.** Undergrowth is generated per display patch, only
   while the patch is near the camera, and is never stored; it is regenerated whenever a patch slot
   is reused. The patch side is `ps = 256 / floor(256 / (viewDistance/4))`. On Irving
   (`viewdistance="60"`) that is 256/17 = **15.0588 m**. That equals **7.53 cells** of the
   1024² UnderGrowthMap (2 m cells) and 3.76 heightmap posts. It is exactly 17 patches per 256 m
   terrain-texture tile and 136 x 136 patches over the map. [V]
2. **Seed and loop.** The seed is `(pz*4711 + px*13 + 23) & 0x7fffffff`, where (px, pz) is the patch
   index and the patch origin is `(px*ps, pz*ps)`. The generator is the same `vf14` loop as
   overgrowth:
   - budget A = trunc(1024*densityScale) = **1024 vertices**, budget B = **4096 indices**;
   - `n_iter = 128`, so at most 64 instances per patch;
   - same roulette, spacing, scale and yaw draws;
   - no import, and no world objects or collision. [V]
3. **Which patches exist.** The game keeps a **10 x 10 ring** of patch slots, indices cx-4..cx+5
   around `cx = trunc(camX/ps)`. A slot is filled and drawn only while
   `D = sqrt(min over the 4 patch corners of dx^2 + (0.2*dy)^2 + dz^2) < viewDistance` (60 m).
   - Alpha is 1 up to 0.7*60 = 42 m, then falls linearly to 0 at 60 m (`fadeRange` 0.3).
   - When the eye is within 1 m of the ground, near patches fade further (`closeToGroundFade*`).
   - `camDistOffset` is overgrowth-only and is **never read** anywhere (dead property). [V]
4. **Rendering** uses fixed-function D3D with no lighting. It runs after terrain, overgrowth,
   objects and water; patches are drawn back-to-front.
   - Colour = `saturate(4 x atlasTexel.rgb x terrainColourTexel.rgb)`. The atlas is the level's
     `texture/F_undergrowthOI`; the terrain texel comes from the 256 m terrain tile under the patch.
   - Alpha = `atlas.a x patchAlpha`. Blend is SrcAlpha / InvSrcAlpha, with Z-write on.
   - Alpha test: `alphaRef = min(patchAlpha*255, 100)`, with ALPHAFUNC GREATEREQUAL set globally.
   - Geometry: only **LOD0, material 0** of each type's `.sm`, with no LOD switching and no wind.
     [V for the code; I for some inherited device state]
5. **The `.wst`** has no attributes beyond overgrowth's. The root sets `materialMapSideSize` and
   `viewdistance`; each type sets geometryName, probability, minRadiusDistToEquals/Others,
   normalScale and scale (optional `rotation`). Attribute names are matched case-insensitively, so
   lowercase `viewdistance` works.
6. **`Game.hidableUndergrowthMaterials 2,3`** is used only by bot visibility. It does not touch
   rendering. [V]

## 1. Manager, patches and seed

| Item | Value / behaviour | Address | Conf. |
|---|---|---|---|
| Object | `UnderGrowthManager` (0x98 bytes) at `[0xdc8098]`, created in 0x426b10 | 0x426c96 | V |
| vtable 0xbaa1ac | vf1 init = `GrowthManager::vf1` 0x760760; vf2 render = `GrowthManager::vf2` 0x75f3a0; vf3 = `UnderGrowthManager::vf3` 0x71fda0; vf5 = 0x71f940; vf7 = 0x71f970 (`regeneratedPatches`, only sets the force flag `+0x38`); vf9 update = `GrowthManager::vf9` 0x75f070; vf10 = `createUnderGrowthPatch` 0x71fdf0; vf11 = `GrowthManager::vf11` 0x75e140; vf12, vf13, vf14 = shared 0x75e5e0 / 0x75e750 / 0x760950 | vtable dump | V |
| Defaults | Base ctor 0x7600e0 sets N=`+0x10`=10, fadeRange `+0x20`=0.3, densityScale `+0x28`=1, closeToGroundFadeHeight `+0x2c`=1.0, closeToGroundFadeAlpha `+0x30`=0.4, closeToGroundFadeDist `+0x34`=12.0, side `+0x84`=512. The UnderGrowth ctor 0x71fe20 then sets map `level/growth/underGrowthMap`, snap size `+0x94`=256.0, viewDistance 70 (so ps = 256/floor(256/17.5) = 18.2857 m) and `+0x38`=1. | 0x7600f9..0x760188, 0x71fe28..0x71fe84 | V |
| `.wst` load | `run growth/underGrowth` (Init.con) reads `Growth/underGrowth.wst`. Property names are looked up with `_stricmp` (`WrapperStringTree::setProperty` 0x7852b0, then 0x783570 / 0x781b30), so `viewdistance="60"` hits `viewDistance`. Its setter is `UnderGrowthManager::vf3`. | 0x7852d4, 0x783598 | V (the `run` to `.wst` path is I, as for overgrowth) |
| Patch size | `UnderGrowthManager::vf3(v)`: no-op if `v == current`. Otherwise `GrowthManager::vf3` (`ps0 = v/(N/2-1)`), then `ps = 256/floor(256/ps0)` and `+0x38 = 1`. Irving: 60/4 = 15, floor(256/15) = 17, ps = 15.058824 (float32). No world-size snap (that is overgrowth's). The 256 is the terrain colour-tile size, so patches never straddle a tile (see section 3). | 0x71fda3..0x71fdde | V |
| Init (`vf1` 0x760760) | Creates N x N `UnderGrowthPatch` slots. `+0x88 = side/world` (1024/2048 = 0.5). Builds the 4-bit CompressedMap and loads `<name>.raw`. Calls `vf3(viewDistance)`, which is a no-op because the value is unchanged. Resolves type records (0x760460). Unlike overgrowth, nothing is overridden after the `.wst`: fadeRange and closeToGroundFade keep their defaults. | 0x760760..0x760945 | V |
| Material lookup | `vf8` 0x75e1e0: `xi = ftol(x*0.5) % 1024`, `zi = ftol(z*0.5) % 1024` (unsigned div), `mat = raw[zi*1024 + xi] & 15`. Row = z, column = x. 2 m map cells. | 0x75e1e0, CompressedMap 0xa87710 / 0xa87560 | V (orientation also checked on data: water cells sit below waterLevel 76 % of the time vs 31..49 % for flipped or transposed variants, and juicyGrass is 100 % above water) |
| Ring | `GrowthManager::vf9`: `cx = ftol(camX/ps)`, `cz = ftol(camZ/ps)`. Slot (i, j) holds `px = N/2 - ((N*100 + i + cx) mod N) + cx`, and pz likewise, i.e. patches cx-4..cx+5. A slot whose (px, pz) changed, or with `+0x38` set, is repositioned: origin = `(px*ps, h(centre), pz*ps)`, with y = terrain height at the patch centre. | 0x75f0aa..0x75f20e | V |
| Visibility / fill | `D = geom_0075e4c0`: min over the 4 corners at the origin's y of `dx^2+(0.2*dy)^2+dz^2`, square-rooted. `visible = D/viewDistance < 1`. If visible and not yet generated: `rng = (pz*4711 + px*13 + 23) & 0x7fffffff`, then `vf11` (generate) and `vf12` (build vertex buffer), then mark generated. Instances are not kept, so a slot that moves away and back regenerates identically. | 0x75f211..0x75f363, 0x75f2f0..0x75f322 | V |
| Budget | `GrowthManager::vf11` 0x75e140 calls `vf14(A = ftol(patch->vf5() * densityScale), B = patch->vf6(), &patch.origin, &tmp)`. `vf5`/`vf6` go through thunks 0x71e330/0x71e340 to `RaStreamDataEditable` vf1/vf2, which read `+0x184`/`+0x188`. Those hold ctor args 1 and 2 (0xa4c2a3/0xa4c2a9), i.e. 0x400 and 0x1000 from `UnderGrowthPatch` ctor 0x71f987 (stride 0x1c). So A = 1024 is compared against vertex counts and B = 4096 against index counts, and `n_iter = A/8 = 128`. | 0x75e151..0x75e176 | V |
| Generation | Same `vf14` (0x760950): x and z jitter over the whole patch, then roulette `u`, budget check (stop at the first misfit), scale, yaw, spacing within this patch only, accept (+2 iterations). Same RNG (Lehmer x630360016 mod 2^31-1), same (0,1] mapping, same `vf13` transform (yaw, normalScale tilt, y = terrain height). See `overgrowth_re.md` sections 2..6. | as overgrowth | V |
| Spacing scope | The patch's own temporary vector, so there is no cross-patch test and no static-object import. | 0x75f305..0x75f32f | V |
| Records | Type names are `..._m1`, so the record's "m2" half and its "m1 twin" are the same mesh. R = bbox corner radius of that `.sm`; costA/costB = its LOD0 material0 vertex/index counts. The ObjectTemplate must exist (`F_undergrowth*_M1`, `HasCollisionPhysics 0`), but no object is ever created. | 0x760460, 0x75f9a0 | V |

## 2. The `.wst` side (Irving `Growth/underGrowth.wst`)

**Root attributes:** `materialMapSideSize="1024"`, `viewdistance="60"`. Nothing else is set, so
fadeRange 0.3, densityScale 1, closeToGroundFade 1.0 / 0.4 / 12 and enable 1 keep their defaults.

**Properties a `.wst` *could* set on the manager** (registration 0x73e160) [V]:
`enable`, `drawDebugCorners`, `fadeRange`, `viewDistance`, `densityScale`,
`closeToGroundFadeHeight/Alpha/Dist`, `regeneratedPatches`, `materialMapFilename`,
`materialMapSideSize`, `reloadMaterialMap`. The closeDist\*, draw\*, estObjMaxRadius,
camDistOffset, import/export and culler\* properties belong to OverGrowthManager only.

**Per-type attributes:** `geometryName`, `minRadiusDistToEquals`, `minRadiusDistToOthers`,
`normalScale`, `probability`, `scale="CRDUniform/a/b/false"`.
- The only other registered type property is `rotation` (default CRDUniform -180..180); it is
  unused here.
- There are **no colour, alpha, wind or density-per-type attributes**: the Type wrapper,
  registered as `UnderGrowthType` (0x73e926), has exactly these 7. [V]
- Semantics are identical to overgrowth. Probabilities are not normalised; here they sum to
  0.1..1.0, so many candidates place nothing.

**Irving's types** (R in metres, cost = verts/indices):

| Type | R | Cost |
|---|---|---|
| G_m1 | 1.048 | 54/108 |
| G02 | 1.350 | 15/27 |
| G03 | 1.281 | 28/54 |
| G04 | 1.899 | 20/60 |
| G05 | 0.917 | 37/81 |
| G06 | 1.315 | 54/108 |
| G07 | 2.255 | 12/30 |
| G08 | 2.451 | 20/60 |
| T | 1.097 | 91/132 |
| T02 | 3.932 | 496/690 |
| o_undergrowthR_m1 | 1.020 | 88/264 |

Note that the `water` material (1) has a type (G_m1, p 0.1), so some clumps sit under water.

**`Game.hidableUndergrowthMaterials 2,3`:** `Game::hidableUndergrowthMaterials` 0x4ff010 stores a
`std::list<int>` at `Game+0x104`. The only readers of the UnderGrowth manager outside growth code
are `BotMain::vf7`/`vf8` (0x976d62.., 0x97929b..). They look up the undergrowth material under a
position and test it against that list, which sets a "hidden" flag for bot spotting. The proximity
effect `UnderGrowthGroup::vf1` (0x700193) uses the same material lookup to pick a MaterialEffect.
Neither touches rendering. [V]

## 3. Rendering

**Patch object.** `UnderGrowthPatch` (0x1b8 bytes, ctor 0x71f980) is a
`RaStreamDataEditable(0x400 verts, 0x1000 indices, stride 0x1c)` with a GrowthPatch at `+0x18c`.
Its shader settings come from `StandardMesh/undergrowth/undergrowth`, via the ShaderManager and
`RaShaderSettings`, copied to `+0x20`. [V]

That resolves to the subshader `undergrowth` in `undergrowth.rs`. Irving's level copy
`Standardmesh/underGrowth.rs` sets `texture "texture/F_undergrowthOI"`; the archive copy uses
`texture/F_undergrowth`. Both atlases are 256x2048, DXT3/5. [I: level files override archive files]

**Vertex build** (`UnderGrowthPatch::add` 0x71fa90). For each instance (from `GrowthManager::vf12`
0x75e5e0):
- It takes the type's StandardMeshTemplate, **first LOD, first material only** (0x71faf7,
  `sub_0071e630`).
- It scales the instance matrix columns by (s, s, s) (0x71e760) and transforms each vertex
  (0x49b340, row-vector convention).
- It writes a **28-byte vertex**: POSITION float3, TEXCOORD0 float2 (the mesh UV from `.sm` vertex
  offset 0x18), TEXCOORD1 float2. The declaration is in `RaShaderUnderGrowth` ctor 0xa6ce40:
  `{0, 0, FLOAT3, POSITION}`, `{0, 12, FLOAT2, TEXCOORD0}`, `{0, 20, FLOAT2, TEXCOORD1}`.
- **TEXCOORD1** = `((worldX - tileX0)/256, (worldZ - tileZ0)/256)`. The tile origin is
  `(trunc(patch.x/256) mod 8)*256`; 256 = 4.0 x `PatchTerrain+0x300` (64 posts per tile), and the
  4.0 is a hard-coded constant at 0xb44a78 (0x71fb80..0x71fc28).
- There are no normals and no vertex colours. [V]

**Per-draw state** (`UnderGrowthPatch::draw` 0x71fce0, then `RaShaderUnderGrowth::vf8` 0xa6cef0):
- The patch world matrix is a pure translation to the patch origin (`vf7` 0xa6cde0
  `SetTransform(WORLD)`).
- `textureFactor = (1, 1, 1, patchAlpha)`.
- `alphaRef = clamp(ftol(patchAlpha*255), 0, 100)` (0x71fd0b..0x71fd2f).
- `TerrainMap` = the colour texture of terrain tile `((x/256) mod 8, (z/256) mod 8)`; tiles are
  stored z-major (0xa6ce00 / 0xa6cca0).
- `DiffuseMap` = the patch material's texture (`vf3` 0xa6cd70).

Handle names were confirmed in `RaShader::vf2` 0xa4bd40: `+0x68` alphaRef, `+0xa0` textureFactor,
`+0xb4` TerrainMap, `+0x5c` DiffuseMap. [V]

**Effect `effects/RaShaderUndergrowth.fx`**, technique `undergrowth`. The vertex and pixel shaders
are NULL; the HLSL path is commented out.
- **Fixed-function stages:**
  - Stage 0: DiffuseMap, sampled LINEAR with AddressU CLAMP and AddressV WRAP. Colour =
    SelectArg1(texture); alpha = texture.a x tfactor.
  - Stage 1: TerrainMap, sampled POINT for min/mip and LINEAR for mag, with CLAMP. Colour =
    **Modulate4x**(terrain, current); alpha = current.
- **Blend:** `AlphaBlendEnable true`, `SrcAlpha` / `InvSrcAlpha`.
- **Depth and alpha test:** `ZWriteEnable = depthWrite`, which is true by default (RaCommon) and
  never set by this shader. `AlphaRef` is as above.
- **Lighting** cannot affect the colour, because no stage reads diffuse. [V from the file]
- **Alpha test:** ALPHATESTENABLE=1 and ALPHAFUNC=GREATEREQUAL are set globally at device init
  (`ShaderManager` 0xa49c92..0xa49cbd). D3DX `Begin(..., 0)` saves and restores state, so the test
  should be active. [I]
- **Effect on pixels:** a fully faded-in patch discards texels with `a < 100/255`. As a patch
  fades out, `ref = alpha*255` keeps only fully opaque texels, which are then blended at `alpha`.
- **Not set by the pass:** cull mode and fog are inherited. [?] Use double-sided rendering in the
  viewer. Fog is irrelevant in practice: Irving's fog starts at 100 m and undergrowth ends at 60 m.

**Patch alpha** (computed each frame):
- **Distance fade:** `r = D/viewDistance`, then `alpha = 1` if `r <= 1 - fadeRange`, else
  `clamp((1 - r)/fadeRange, 0, 1)`. For Irving that is fully opaque to 42 m and 0 at 60 m
  (0x75f270..0x75f2de). [V]
- **Close-to-ground factor:** `g = clamp(|camY - terrainH(cam)| / 1.0, 0.4, 1)` (0x75f428..0x75f496)
  and `f = clamp(patchCentreDist3D / 12, g, 1)`, then `alpha *= f` (0x75f684..0x75f6c5). Standing
  eyes (about 1.7 m) give g = 1, so there is no effect. A prone eye at 0.5 m gives near patches
  (centre < 6 m) alpha 0.5, ramping to 1 at 12 m. [V]

**Draw-time culling** (`GrowthManager::vf2`):
- Drawn only if the patch is generated and visible (`+4`) and non-empty (`+6 == 0`).
- `D <= camera->vf14()` (far distance).
- Not occluded by terrain (`PatchTerrain_00721dc0` with a sphere at the patch centre, radius
  `0.71*ps + 10`).
- The AABB `[x - 0.1ps, y - 100, z - 0.1ps]..[x + 1.1ps, y + 100, z + 1.1ps]` is in the frustum.

[V for the calls; I for the exact semantics of `camera->vf14` and of the occlusion test]

**Order:**
- The visible list is sorted far to near by patch-centre distance (comparator 0x75e0e0) and drawn
  in that order. [V for the comparator; I for the empty second list]
- Inside a patch, instances are in generation order.
- Frame order (`sub_004a6fa0`): Terrain queue, then overgrowth submit plus the OverGrowth queue,
  then objects, then the Water queue (0x4a7b1c), then **undergrowth submit plus the UnderGrowth
  queue** (0x4a7b2e..0x4a7b54), then the "blending" queue (0x4a7ba4). The queue `[0xe17324]` and
  the shader `[0xe173c0]` were created in 0xa3bee7..0xa3bf68. [V]

**Viewer recipe:**
- Per instance: the `.sm` LOD0 material0 mesh, transformed by
  `M = diag(s) x rows(yaw, up) + pos`, with `rows()` as in the script.
- Texture with `F_undergrowth<level>.dds`, AddressU clamp and AddressV wrap.
- Multiply by 4 x the terrain colour at that world xz, clamped to 1.
- Alpha-test, then blend with `atlas.a x patchAlpha`; write depth.
- Draw patches far to near, after opaque geometry and water.

## 4. Script (`undergrowth_place.py`) and density sanity

- `--mode game` emulates the ring and visibility for a camera at (x, y, z); y defaults to terrain
  + 1.7 m.
- `--mode radius --radius R` takes every patch whose nearest corner is within R.
- The script prints counts per type and, with `--patches`, a per-patch line: seed, distance,
  alpha, instances, verts/indices used, iterations, stop reason.
- `--json` writes instances (patch, type, material, pos, scale, yaw, up, matrix rows, patchAlpha)
  plus a patch list.

Results for Irving:

- **Camera at the hangar** (805, 502), game mode: 69 patches within 60 m and 772 instances. The
  hangar area is mostly drySand / sandRoad / default in the undergrowth map, so it is sparse; the
  juicyGrass strip to the west is dense.
- **Camera at (760, 520), in juicyGrass:** 69 patches and 1165 instances. Juicy patches carry 35..49
  instances and stop on the 1024-vertex budget.
- **Whole-map sample** (every 4th patch in x and z, 1156 patches, 30,289 instances, so about 485k
  map-wide):

  | Material | Mean instances per patch | Mean verts used | Notes |
  |---|---|---|---|
  | juicyGrass | 43.6 | 1002/1024 | 98 % budget-stopped (about 0.19 clumps/m²) |
  | dryGrass | 18.0 | 554 | |
  | rock | 19 | — | |
  | drySand | 10.9 | — | often a single T02 whose `minRadiusDistToOthers` 10 (≈ 40 m) blocks everything else in its patch |
  | default | 0.6 | — | |

- **Invariants hold:** verts ≤ 1024, indices ≤ 4096, ≤ 64 instances per patch (the script asserts
  this). The vertex budget always binds before the index budget.

## Open questions

1. **The `run growth/underGrowth` path to the `.wst` loader [I].** It must be this way, since
   undergrowth renders with the `.wst` content.
2. **Inherited device state [?].** Cull mode, fog and texcoord-index state are not set by the
   pass. Alpha-test enable is set globally at init [I].
3. **`camera->vf14` (far cull) and the terrain-occlusion test [?].** They affect what is drawn, not
   what is generated.
4. **Hard-coded 4.0 in the terrain-UV scale [V].** On maps whose heightmap post spacing is not 4 m,
   the grass tint UV would be stretched (a plausible engine bug). Irving is 4 m, so it is correct
   there.
5. **Negative patch indices near the map's low edges [V]** take the C semantics the script copies:
   a masked seed and unsigned-wrapped map lookups. Only the area outside the map is affected.
6. **Level texture override [I].** `Standardmesh/underGrowth.rs` overriding the archive's is
   inferred from the level-over-archive mount order, not traced.
