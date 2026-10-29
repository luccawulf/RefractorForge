# Battlefield Vietnam overgrowth placement, reverse-engineered

Source: `D:\Games\EA GAMES\Battlefield Vietnam Original Files\BfVietnam.exe` (ImageBase 0x400000),
read with capstone plus the lifted C in `bfv-native-code\lifted\client\`. Read-only throughout.
Every address is a VA in that exe. Confidence tags:

- **[V]**: verified by reading the instructions.
- **[I]**: inferred from the code plus game data.
- **[?]**: unknown.

Reference implementation: `tools/bfv_growth/overgrowth_place.py` (summary: `docs/BFV_Growth_Placement.md`). It reproduces the whole algorithm
for Operation_Irving and writes per-instance JSON with `--json`.

## TL;DR

1. **Overgrowth is generated for the whole map once, when the level loads.** It is *not* built
   lazily around the camera. The map is cut into a W x H grid of square **cells**, each `ps` metres
   on a side. `ps` comes from the `.wst` `viewDistance`; on Irving `ps` = 2048/14 = 146.2857 m and
   the grid is 14 x 14. Each cell has its own RNG seed, `(23 + 13*i + 4711*j) & 0x7fffffff`, where
   i is the x cell index and j the z cell index. The output is therefore identical on every client
   and every regeneration. [V]
2. **The RNG** is a Lehmer generator, mod 2^31-1, with multiplier 630360016 (applied as two Carta
   steps, x24112 then x26143). Each draw becomes `U = (((r|0x80)>>7)+1) * 2^-24`, in (0, 1]. [V]
3. **Per cell**, the loop runs `(densityScale*2048)/8` = 256 iterations. Each candidate draws, in
   this order:
   1. x jitter.
   2. z jitter.
   3. `u`, a roulette draw over the material's `probability` list. The list is not normalised, and
      `u < p` selects that type.
   4. scale (`CRDUniform` gives `a + (b-a)*U`).
   5. yaw (default `-180 + 360*U` degrees).

   A placed candidate costs 2 iterations, a rejected one costs 1. [V]
4. **Density is capped by a vertex/index budget**, not just by the iteration count. Each cell gets
   2048*densityScale vertices and 8192 indices, which is the size of one patch's dynamic buffer.
   Every placed instance subtracts its `_m2` mesh's LOD0/material0 vertex and index counts. The
   first candidate whose type no longer fits **ends the cell**. On Irving this gives about 10 trees
   per 146 m cell, 1449 generated instances in total. [V]
5. **Spacing** is a 2D (XZ) test against instances already in **the same cell only**:
   - Same geometry record: reject if `d < R_a*eq_a*s_a + R_b*eq_b*s_b`.
   - Different records: the same test, with `minRadiusDistToOthers` in place of the `eq` factors.

   `R` is the `_m2` mesh's bbox corner radius about its origin, `s` is the instance scale, and
   `eq` is `minRadiusDistToEquals`. [V]
6. **`importSceneObjects`:** static objects whose geometry is listed in
   `standardMesh/overGrowthGeometries.wst` are pulled into their cell before generation. They use up
   the cell's budget first and act as spacing obstacles, with radius
   `R(M1, after geometry.scale) * scale.y`. Buildings such as the hangar do **not** block
   overgrowth. Only the overgrowth material map decides where it can grow. [V]

## 1. Patch / cell layout and regeneration

| Item | Value / behaviour | Address | Conf. |
|---|---|---|---|
| Managers | `UnderGrowthManager` at `[0xdc8098]`, `OverGrowthManager` at `[0xda8040]`, both created in 0x426b10 | 0x426c96, 0x426cbb | V |
| Defaults (OverGrowth) | N=`+0x10`=10 patches per side, viewDistance `+0x24`=500, densityScale `+0x28`=1.0, materialMapSideSize `+0x84`=512, map name `level/growth/overGrowthMap` | ctor 0x71e850, base ctor 0x7600e0 | V |
| Level init | `Game` load runs `Level/Init.con` (which does `run growth/overGrowth`, i.e. parses the `.wst`), then `Level/StaticObjects.con`, then calls `overGrowth->vf1` | 0x4fc715, 0x4fd43c, 0x4fdc4f | V (the `.wst` load via `run` is I) |
| `vf1` (init) | `GrowthManager::vf1` 0x760760: world size from terrain (`PatchTerrain+0x314`). `+0x88 = side/world`. Builds and loads the 4-bit `CompressedMap` from `<name>.raw`. Calls `vf3(viewDistance)`. Resolves every type to a geometry record. Then `OverGrowthManager::vf1` 0x71c450 loads `StandardMesh/overGrowthGeometries.wst` into material 16 (`dummy`), forces `fadeRange=0` and `closeToGroundFadeAlpha=1`, and tail-calls `vf7` (regenerate). | 0x760760, 0x71c450 | V |
| Cell size | `ps = viewDistance/(N/2-1)` (0x75e110), then `ps = world/floor(world/ps)`, `W = H = ftol(world/ps + 0.5)` (0x71dc90). Irving: 550/4 = 137.5, floor(2048/137.5) = 14, so ps = 146.28572 (float32) and the grid is 14 x 14. | 0x75e110, 0x71dc90..0x71dd6b | V |
| Cells | Array at `+0xb0c`, W*H records of 40 bytes: `+0` origin x = i*ps, `+4` height at the centre, `+8` origin z = j*ps, `+0xc` instance vector (100-byte instances), `+0x1c` count of imported instances, `+0x20` budget A = ftol(densityScale*2048), `+0x24` budget B = 0x2000 | `vf7` 0x71f5a2..0x71f6e7 | V |
| Regenerate (`vf7` 0x71f4c0) | (a) `exportSceneObjects` (0x71d380): puts previously imported statics back and clears everything. (b) Allocates cells. (c) If `importSceneObjects`, runs import (0x71ef40). (d) Generates every cell (0x71cc40). (e) Creates a real world object (from the `_m1` ObjectTemplate) for each instance and registers it in the BinTreeCuller. (f) Sets `+0x38=1` to force a patch rebuild. | 0x71f4c0..0x71f938 | V |
| Triggers | Level init; the console method `overGrowth.regeneratedPatches` (registered 0x73e416, thunk 0x592810, which is `vf7`). Setting `viewDistance` or `densityScale` (0x71dc90 / 0x71dd80) deletes the cells *without* regenerating them. | 0x73e416, 0x71dce0, 0x71ddd0 | V |
| Determinism | The seed depends only on (i, j). Inputs are the material map, `.wst`, meshes and static objects. No time, camera or client state is involved, and `densityScale` is never set by the video options (no xrefs). Video quality only sets `closeDistLodFactor` to 0.7, 1.0 or 1.3 (0x509b76 / 0x509adc / 0x509a44), which affects drawing only. | 0x71ccc3..0x71cccb, 0x509980 | V |
| Display patches | A 10 x 10 toroidal ring: slot (a, b) holds `px = cx + N/2 - ((a + cx + 100N) mod N)`, with `cx = trunc(camX/ps)` (pz the same way). That covers patches cx-4..cx+5. A slot whose index changed (or `+0x38` set) is refilled only when visible: `sqrt(min over its 4 corners of dx^2+(0.2*dy)^2+dz^2) < viewDistance` (0x75e4c0 / 0x75e460). The refill seeds the RNG with `(pz*4711+px*13+23)&0x7fffffff`; overgrowth ignores that seed because its `vf11` (0x71e9e0) just copies cell `(px mod W, pz mod H)` (vector copy 0x71ddf0). Outside the map, cells therefore repeat. | `GrowthManager::vf9` 0x75f070, 0x75f2f0..0x75f322 | V |

## 2-5. Generation algorithm (numbered)

For each cell, j (z index) outer and i (x index) inner, as in `vf7_helper` 0x71cc40:

1. Set `rng.state = (23 + 4711*j + 13*i) & 0x7fffffff`. The RNG state lives at `manager+0x18`;
   its ctor value 0x759dc150 is always overwritten. [V] 0x71cc5e, 0x71ccc3, 0x71cce5, 0x71ccfb
2. Call `vf14(A = cell.budgetA, B = cell.budgetB, &cell.origin, &cell.instances)`. That is
   `GrowthManager::vf14` 0x760950. [V] 0x71ccce..0x71ccdb
3. Set `n_iter = A/8` (C division) and `it = 0`. While `it < n_iter` and the loop has not been
   stopped: [V] 0x760956..0x760963, 0x760c3f..0x760c4e
   1. **Position** (0x75e7f0): `x = origin.x + float(k1*ps)*2^-24`, then
      `z = origin.z + float(k2*ps)*2^-24`, where `k = ((next()|0x80)>>7)+1`. Draw order is x, then
      z. Then `y = terrain.height(x, z)` (`[0xda21a0]->vf19`) and
      `mat = materialMap(x, z)` (`vf8` 0x75e1e0). [V]
   2. **Material lookup:** `xi = ftol(x*side/world) % side` and `zi = ftol(z*side/world) % side`
      (unsigned `div`), then `mat = raw[zi*side + xi] & 0xF`. Row = z, column = x. The map is a
      4-bit `CompressedMap`: load 0xa87710 sets (col, row) and get 0xa87560 / 0xa874c0 reads it
      back. [V]
   3. **Type roulette:** `u = k3*2^-24`. This draw is **always** taken, even when the material has
      no types. Walk `materials[mat].types` in `.wst` order: if `u < type.probability`, select it;
      otherwise `u -= probability` (stored as float). The list is not normalised, so probabilities
      summing below 1 leave a "nothing" chance. A type whose record failed to resolve (`idx < 0`) is
      skipped *without* subtracting. If nothing is selected, `it += 1` and continue. [V]
      0x7609b0..0x7609f5, 0x760a04..0x760a0f, 0x760b0d..0x760b19
   4. **Budget:** `rec = records[type.idx]`. If `rec.costA > A` or `rec.costB > B`, set the stop
      flag (`it += 1`, loop exits). The cell ends **at the first non-fitting choice**, even if a
      cheaper type could still fit. [V] 0x760a2f..0x760a59, 0x760c61
   5. **Scale:** `s = type.scale.sample(rng)`, one draw for CRDUniform. The instance scale is
      (s, s, s). [V] 0x760a63..0x760acd
   6. **Radii:** `R = rec.m2geom->vf9()` (fall back to the m1 geometry if m2 is missing). Then:
      - `r = R*s` (culling radius, `+0x50`).
      - `rEq = (R*eq)*s` (`+0x54`).
      - `rOth = (R*oth)*s` (`+0x58`).

      Also set `generated = 1` (`+0x5c`). [V] 0x760ad4..0x760b6c
   7. **Transform** (`vf13` 0x75e750):
      1. Draw `yaw = type.rotation.sample(rng)`. **This draw happens before the spacing test, so
         rejected candidates consume it too.**
      2. Build `M = rotY(yaw)`.
      3. Set the up row to `(n.x*normalScale, n.y, n.z*normalScale)`, where n is the terrain
         normal at (x, z) (`[0xda21a0]->vf22`).
      4. Orthonormalise (0x6a0940).
      5. Translation = (x, y, z).

      [V]
   8. **Spacing** against every instance already in this cell's vector (imported ones included):
      `d2 = dx*dx + dz*dz`. If `other.recIdx == cand.recIdx`, `R2 = (cand.rEq + other.rEq)^2`,
      otherwise `R2 = (cand.rOth + other.rOth)^2`. Reject if `d2 < R2` (`it += 1`). No
      neighbouring-cell test is done. [V] 0x760b77..0x760bfe
   9. **Accept:** `A -= rec.costA`, `B -= rec.costB`, append the instance to the cell vector
      (0x71ea60), and `it += 2`. The extra `inc [esp+0x18]` is at 0x760c37. [V] 0x760c00..0x760c37

RNG draws per candidate:

- Material with no types: 3 draws.
- Type chosen but over budget: 3 draws, then the cell stops.
- Otherwise: 3 + 1 (scale) + 1 (yaw) = 5 draws, whether the candidate is placed or rejected.

A random-sign flag of `true` would add one draw before each of the scale and yaw values (0x5eff03);
no retail level uses it.

### Pseudo-code (precise)

```python
M31 = 2**31 - 1
def rng_next(s):                    # Random::next 0x4e0990 (two Carta/Schrage steps)
    s = s * 24112 % M31
    return s * 26143 % M31          # == s*630360016 % M31
def k(rng):  rng.s = rng_next(rng.s); return ((rng.s | 0x80) >> 7) + 1   # 1..2^24
def U(rng):  return k(rng) * 2**-24                                     # (0,1]

def sample(dist, rng):              # RandomDistribution::sample 0x5eff00 / 0x5efea0
    typ, a, b, sym = dist           # "CRDUniform/.8/1.2/false" -> (1, .8, 1.2, False)
    sign = (1 if U(rng) > 0.5 else -1) if sym else 1
    if typ == 0: v = a                                   # CRDNone
    elif typ == 1: v = a + (b - a) * k(rng) * 2**-24     # CRDUniform, Random::uniform 0x4e0a40
    elif typ == 2: v = exponential(a)                    # 0x5ef910 (unused by retail levels)
    else: v = gaussian() * b + a                         # 0x5efd90 (unused by retail levels)
    return v * sign

# setup (vf1 0x760760, vf3 0x75e110 + 0x71dc90)
ps = f32(viewDistance / (N//2 - 1)); ps = f32(world / floor(world / ps))
W = H = int(world / ps + 0.5); mscale = side / world
records = {}                                  # sub_00760460, keyed by exact (case-sensitive) name
for mat in materials: for t in mat.types: t.idx = record(t.geometryName)

cells = {(i, j): Cell(x=f32(i*ps), z=f32(j*ps), A=int(densityScale*2048), B=8192, inst=[])}
if importSceneObjects: import_scene_objects(cells)   # section 7
for j in range(H):
  for i in range(W):
    c = cells[i, j]; rng.s = (23 + 13*i + 4711*j) & 0x7fffffff
    A, B, it, n_iter = c.A, c.B, 0, c.A // 8
    while it < n_iter:
        x = f32(f32(k(rng) * ps) * 2**-24 + c.x)
        z = f32(f32(k(rng) * ps) * 2**-24 + c.z)
        mat = matmap[int(z * mscale) % side][int(x * mscale) % side] & 15
        u = U(rng); t = None
        for ty in materials[mat].types:
            if u < ty.probability:
                if ty.idx < 0: continue
                t = ty; break
            u = f32(u - ty.probability)
        if t is None: it += 1; continue
        rec = records[t.idx]
        if rec.costA > A or rec.costB > B: break          # whole cell stops
        s = sample(t.scale, rng)
        R = rec.radius
        cand = Inst(rec=t.idx, x=x, z=z, s=s, rEq=R*t.eq*s, rOth=R*t.oth*s)
        cand.yaw = sample(t.rotation, rng)                 # default (1, -180, 180, False)
        if any((x-o.x)**2 + (z-o.z)**2 <
               ((cand.rEq + o.rEq) if o.rec == cand.rec else (cand.rOth + o.rOth))**2
               for o in c.inst):
            it += 1; continue
        A -= rec.costA; B -= rec.costB; c.inst.append(cand); it += 2
# per accepted instance: y = height(x,z); up = normalize(n.x*ns, n.y, n.z*ns); M = rows below
```

## 3. The RNG [V]

- **`Random::next` (0x4e0990).** `state = (state*24112) mod (2^31-1)`, then
  `state = (state*26143) mod (2^31-1)`, and the new state is returned. Each step is Carta's
  split-multiply with one conditional `+0x7fffffff`, so the result is exact; the combined multiplier
  is 630360016. A state of 0 would stick at 0, but no cell seed is 0.
- **Uniform (0,1] mapping**, used everywhere: 0x75e809 (x), 0x75e82f (z), 0x7609b5 (type `u`),
  0x4e0a41 (uniform), 0x5eff11 (sign). The formula is `((r | 0x80) >> 7) + 1` times 2^-24, where
  2^-24 is the float at 0xb5d414.
- **Seeds.**
  - Overgrowth cells: `(23 + 13*i + 4711*j) & 0x7fffffff` (0x71cc5e `mov ebp,0x17`, `+0xd` per i at
    0x71cce5, `+0x1267` per j at 0x71ccfb).
  - Display patches use the same formula with patch indices (0x75f2f0..0x75f313). That matters
    only for undergrowth.
- **Reseed points:** once per cell, immediately before `vf14`. The state is shared, so each cell
  consumes its own sequence.
- **Distribution enum.** The `ContRandDist` name table at 0xd0d558 (registered 0x893017) maps
  `CRDNone`=0, `CRDUniform`=1, `CRDExponential`=2, `CRDNormal`=3. The `RandDist` string format is
  `type/a/b/sign`, separated by `/` (0x893089..0x8930c1).
- **Type defaults** (0x75e410):
  - probability 1.0.
  - scale = CRDNone with a = 1.0, i.e. a constant scale of 1.0.
  - **rotation = CRDUniform with a = -180, b = 180.**
  - minRadiusDistToEquals, minRadiusDistToOthers and normalScale are all 1.0.
  - The record index starts at -1.

  The `.wst` property offsets on Type are geometryName `+0`, probability `+0x1c`, scale `+0x20`,
  rotation `+0x30`, eq `+0x40`, oth `+0x44`, normalScale `+0x48` (registration 0x73e94c..0x73ea8e).
  The `rotation` attribute exists, but no retail level sets it.

## 4. Position [V]

- **Jitter:** independently uniform over the **whole cell square**, `[origin, origin+ps]` on each
  axis. There is no per-map-texel stratification. The candidates are random samples, not one per
  map cell. Formula: `x = origin.x + (k*ps)*2^-24`, rounded to float32 at each store.
- **Cell to world:** cell (i, j) covers x in [i*ps, (i+1)*ps) and z in [j*ps, (j+1)*ps). The world
  origin is the terrain's (0, 0) corner. The x index uses multiplier 13 and the z index 4711. The
  material map row is z and the column is x; the heightmap is the same (`HeightMap::getSample`
  0x745930 returns `data[(z&mask)<<shift | (x&mask)]`). I checked the orientation against Irving's
  static trees: terrain height at their (x, z) matches their y with median error 0.00005 m. With z
  flipped the error is 20 m.
- **Height:** `PatchTerrain::vf23` (0x722c10). Samples are the raw u16 heightmap times
  `yScale/256` (`+0x31c`, 0x72337a). Interpolation is triangular per 4 m cell:
  - When `(ix+iz)` is odd the diagonal runs (ix+1, iz) to (ix, iz+1): use triangle (00, 10, 01) if
    `fx + fz < 1`, else (11, 10, 01).
  - When it is even the diagonal runs (00) to (11): use triangle (00, 01, 11) if `fx <= fz`, else
    (00, 10, 11).
  - The vertical offset is 0. A result of exactly 0.0 triggers a template fallback (0x7230af),
    which does not matter on land.
- **Normal:** the face normal of the same triangle (cross product at 0x723086, then normalised). A
  smooth-normal variant (0x722a10) is used when `PatchTerrain+0x354` is set.

## 5. Spacing [V]

- **Units:** metres. **Radius:** `R` is the bounding-sphere radius of the `_m2` mesh about its
  **origin**: the maximum over the 8 bbox corners of |corner| (`Object_006852d0`). The bbox is the
  6 floats at `.sm` offset 8 (`StandardMeshTemplate::vf38` 0x708200). `StandardMesh+0x5c` is copied
  from it (0x74ab15) and returned by `vf9` (0x736e70).
- **Formula:** the instance's own radius is `R * factor * s` (factor = eq or oth). The two
  instances' radii are summed. The engine's tooltip says "The closest radius distance between
  similar objects, it's scaled with the scale" (string 0xbacbb8).
- **"Equal"** means the same per-geometry record index (0x760bb6..0x760bcf). Record names are
  compared **case-sensitively** (0x7604f1 `repe cmpsb`, 0x76052c). `estObjMaxRadius` plays no part
  in generation; it is a draw-distance pad.
- **Scope:** only the cell's own vector, so instances can touch across cell borders. Imported
  statics in that cell are included.
- **Example (Irving c02f_trees_m2):** R = 20.66 m. In dryGrass (eq .5, oth .25), two c02f clusters
  must be at least about 20.7*s m apart. Against a c08f the distance is about
  5.2*s + 15.0*0.25*s'.

## 6. Orientation and size [V]

- **Yaw:** `type.rotation` sample in degrees, default uniform over [-180, 180].
  `BFSoldier_0065a400` multiplies by 0.0174533 (0xcd6d90), then `fsincos` (0x4f8e0b). It builds
  row-vector matrix rows `right = (cos, 0, -sin)`, `up = (0, 1, 0)`, `fwd = (sin, 0, cos)`. Positive
  yaw turns +Z toward +X.
- **normalScale:** the up row becomes `(n.x*ns, n.y, n.z*ns)` (0x75e791..0x75e7be). Then
  `QuadTreeCuller_006a0940` normalises up and fwd, sets `right = up x fwd`, then `fwd = right x up`.
  The last two are not re-normalised. `ns` = 0 keeps trees vertical and 1 follows the terrain.
  Irving uses 0.1 to 0.3 for trees and 0.8 for rocks.
- **scale:** `CRDUniform/a/b/sign` gives `s = a + (b-a)*U`, applied uniformly. The 4th field
  `false` means no random sign (`true` would draw U and negate the value when U <= 0.5). The scale
  goes into the instance `+0x44..+0x4c` and is applied as geometry scale (`IScaleable::setScale`),
  not in the matrix.

## 7. importSceneObjects [V] (0x71ef40, gated by `+0xafc`)

1. Build a case-insensitive (`_stricmp`) set of `geometryName`s from the **last** material
   (`dummy`, filled from `StandardMesh/overGrowthGeometries.wst`: about 140 `_m1` names covering
   trees, jungle, bushes, rocks and clutter). 0x71ef8f..0x71effe
2. Enumerate all objects from `ObjectManager::vf11` (0x66fc30). It walks a `std::list` (assumed to
   be in creation order, i.e. `StaticObjects.con` order [I]) plus children. For each object whose
   geometry is an `IStandardMesh` and whose template name is in the set:
   - cell = `(trunc(x/ps) mod W, trunc(z/ps) mod H)` (0x71f0d9..0x71f13f).
   - name2 = the template name with its last char changed to `2` when the second-to-last char is
     `m`/`M`, e.g. `C02F_Trees_M1` becomes `C02F_Trees_M2`. Then `recIdx = record(name2)`.
   - The instance takes the object's matrix and scale, `rEq = rOth = r = R_geom * scale.y`, and
     `generated = 0` (0x71f1e4..0x71f2b0).
   - `R_geom` is the object's own (M1) mesh radius **after** `Object.geometry.scale`: setScale
     0x748860 scales the bbox and recomputes `+0x5c`. Uniformly scaled statics therefore end up
     with roughly s^2*R. [V for setScale; I that the `Object.geometry.scale` console command routes
     to it]
3. Per cell, in vector order: charge `costA/costB` of each imported instance's record; at the first
   one that does not fit, stop. The vector is truncated, and the rest stay ordinary static objects
   that are **not** obstacles. `+0x1c` = number kept. Kept objects are removed and later re-created
   by overgrowth from the same `_m1` template and transform, so nothing changes visually.
   0x71f306..0x71f42a
4. Generation then starts with the reduced budgets (which also reduces `n_iter = A/8`). The kept
   statics act as spacing obstacles. Ordinary buildings never block overgrowth.

On Irving, 147 statics are kept as overgrowth and 26 are left as plain objects because of budget.
Import cuts generated instances from 1562 to 1449; 1445 of those 1449 also appear in a run without
import.

## 8. Rendering basics

- **Far (patches):** `GrowthManager::vf12` (0x75e5e0) appends each instance's `_m2` geometry into
  the patch's `RaStreamDataEditable(0x800 verts, 0x2000 idx, stride 0x20)` (OverGrowthPatch ctor
  0x71e200), using shader `StandardMesh/overgrowth/overgrowth`. Positions are local to the patch:
  `fmod(x + 100*ps, ps)` and `y - patch.y`. Patches are drawn when the corner distance is below
  `viewDistance` (550). `fadeRange` is forced to 0, so patches pop in at the edge instead of fading
  (0x71c468, 0x75f287..0x75f2de). [V]
- **Near (objects):** every instance gets a real `SimpleObject` from the `_m1` ObjectTemplate at
  regeneration (0x71f7fd..0x71f895). The `_m1` name is the m2 name with its last char set to `1`.
  The objects are added to the ObjectManager and a BinTreeCuller (0x71f8e5). `vf9` (0x71cd20) sets
  `closeDist = camera->vf9() * closeDistPercentage(0.2) * closeDistLodFactor(0.7/1.0/1.3) *
  viewDistance`, about 110 m at medium. [V] `vf15` (0x71ec00) tests
  `|cam-inst|^2 < (closeDist + r)^2` per instance and maintains a per-patch list at `+0x1bc`; close
  instances are drawn as objects with a `closeDistFade` alpha (0x71d1de..). [V for the test and
  the alpha; I that the list at `+0x1bc` is what removes close instances from the far batch]
  - `camera->vf9` is not identified. [?]
- **Collision:** yes. The `_m1` templates have `ObjectTemplate.HasCollisionPhysics 1`
  (`objects/Vegetation/Common/*_M1/Objects.con`). They live in the ObjectManager and are created
  locally on each client. [V for the object creation, I for in-game collision]
- **Not placement-relevant:** `camDistOffset`, `cull*` flags, `drawDebugCorners` (draws vertical
  lines at the patch corners, 0x75f773..), `closeToGroundFade*`.

## Data layouts (for reference)

- **OverGrowthManager (0xb10 bytes; GrowthManager base fields first):**

  | Offset | Field |
  |---|---|
  | `+4..+0xc` | camera position |
  | `+0x10` | N |
  | `+0x14` | ps |
  | `+0x18` | RNG |
  | `+0x1c` | enable |
  | `+0x1d` | drawDebugCorners |
  | `+0x20` | fadeRange |
  | `+0x24` | viewDistance |
  | `+0x28` | densityScale |
  | `+0x2c/+0x30/+0x34` | closeToGroundFadeHeight/Alpha/Dist |
  | `+0x38` | force-rebuild |
  | `+0x3c` | patch pointers |
  | `+0x44` | `vector<Material*>` (17) |
  | `+0x54` | `vector<Record>` (128 bytes each) |
  | `+0x60/+0x64` | world x/z |
  | `+0x68` | materialMapFilename |
  | `+0x84` | side |
  | `+0x88` | side/world |
  | `+0x8c` | CompressedMap |
  | `+0x94` | BinTreeCuller |
  | `+0xacc` | closeDistLodFactor |
  | `+0xae4` | closeDist |
  | `+0xae8` | closeDistPercentage |
  | `+0xaec` | closeDistFade |
  | `+0xaf0` | camDistOffset |
  | `+0xaf4/5` | drawClose/drawFar |
  | `+0xaf8` | estObjMaxRadius |
  | `+0xafc` | importSceneObjects |
  | `+0xafd..+0xb01` | culler flags |
  | `+0xb04/+0xb08` | W/H |
  | `+0xb0c` | cells |

  Source: property registration 0x73e160.
- **Material (0x2c bytes):** name string, then `vector<Type*>` at `+0x20`.
- **Record (128 bytes; 0x760460 / 0x75f9a0):** two 64-byte halves, for the m2 name and its m1 twin.
  Each half holds: name, `+0x1c` geometry (StandardMesh instance), `+0x20` costA =
  `StandardMeshTemplate+0xa0` = LOD0 material0 **vertex** count, `+0x24` costB = `+0xa4` = LOD0
  material0 **index** count (vf40 0x70bb07, vf41 0x7093dc..0x7093fc), and `+0x28` ObjectTemplate.
- **Instance (100 bytes):**

  | Offset | Field |
  |---|---|
  | `+0` | recIdx |
  | `+4..+0x43` | 4x4 matrix (translation at `+0x34`) |
  | `+0x44` | scale xyz |
  | `+0x50` | r |
  | `+0x54` | rEq |
  | `+0x58` | rOth |
  | `+0x5c` | generated |
  | `+0x60` | object |

## Irving numbers (from `overgrowth_place.py`)

- **Grid:** viewDistance 550, ps 146.28572 m, 14 x 14 cells. densityScale 1.0, budgets 2048/8192.
  Seeds 23 + 13i + 4711j.
- **Records (R in metres, cost = verts/indices):**

  | Record | R | Cost |
  |---|---|---|
  | c02f_trees_m2 | 20.661 | 197/369 |
  | c08f_jungle_m2 | 15.004 | 122/252 |
  | c01f_trees_m2 | 27.082 | 210/396 |
  | o_rocks01_m2 | 5.187 | 64/192 |
  | c04f_trees_m2 | 17.479 | 98/189 |
- **Generated instances (1449):** c02f 908, c08f 370, c01f 139, rocks 29, c04f 3. That is 1 to 13
  per cell, over 155 cells; the budget is nearly exhausted in 125 cells.
- **Near the US hangar (805, 502):** 19 generated instances within 120 m. The nearest is a c02f at
  (742.1, 502.2), 63 m west.
  - The hangar sits on dirtRoad/default material, and neither has types, so nothing generates
    within about 50 m.
  - Palms come from the dryGrass strips south (z 420..435, x 790..880), west (x 725..760) and north
    (z 555..595).
  - 13 imported static objects also sit within 120 m; these are already in `StaticObjects.con`.
  - The full list with yaw and scale is printed by the script.
- **Robustness checks:** none of these changed a single generated instance on Irving:
  - FPU 24-bit vs 53-bit emulation.
  - Case-insensitive vs case-sensitive record names.
  - Forward vs reversed import order.

For the viewer: render `StaticObjects.con` as you already do, plus the generated instances
(`--json` gives per instance: cell, `geometry` (the `_m2` name; use the `_m1` twin mesh for close
range), `pos` (y = terrain), `scale`, `yaw`, `up`, matrix rows).

## Open questions

1. **Material matching in the `.wst` [I].** The `.wst` `<materials>` children are matched to the
   17 built-in materials **by name**, not by order. Evidence: the ctor creates a named `dummy`
   material at index 16, and import reads the last material. Irving lists all 16 in canonical
   order, so either way gives the same result here.
2. **How `run growth/overGrowth` reaches the `.wst` [I].** It presumably goes through the wrapper
   loader (`wst`/`wbt`, 0x780d80); there is no `.con`.
3. **ObjectManager list order [I].** It is assumed to be `StaticObjects.con` order. It matters only
   when imported statics overflow a cell budget, and it does not matter on Irving.
4. **Geometry template name case [I].** Template names are assumed to keep their case: lightmap
   names such as `O_SBunker01_m1_...` do. If so, imported `C02F_Trees_M1` gets its own record
   rather than sharing `c02f_trees_m2`'s, which changes only the eq/oth choice between imported and
   generated instances. No effect on Irving.
5. **FPU precision control [?].** D3D's default 24-bit mode vs 53-bit could flip a borderline
   comparison. No effect on Irving in either mode.
6. **Second regeneration [I].** If a level regenerates twice (only via the console or lightmap
   tool path 0x440560), exported statics are re-appended to the ObjectManager, so import order can
   change. Normal client load runs `vf1` once.
7. **`camera->vf9` [?].** The factor in `closeDist` is unidentified. It affects only the near/far
   switch, not positions.
8. **CRDExponential/CRDNormal [?].** Not implemented (0x5ef910 / 0x5efd90). No retail BFV
   `overGrowth.wst` uses them; all use `CRDUniform/../../false` and none sets `rotation` or
   `densityScale`.
9. **`.sm` collision blocks [?].** For meshes with collision blocks, the script finds the LOD0
   header by pattern instead of parsing the collision section (vf39 0x708280). o_rocks01_m2 gives
   64/192 this way (1 LOD, stride 64); this is plausible but not cross-checked.
10. **Dedicated-server collision [?].** Whether the dedicated server also creates the collision
    objects (client/server collision consistency) was not checked.

## Undergrowth (shared code, how it differs)

UnderGrowth uses the same `vf14` loop, RNG, roulette, budget and spacing. The differences:

- It does **not** override `vf11`. `GrowthManager::vf11` (0x75e140) generates **per display patch,
  lazily**, seeded with `(pz*4711 + px*13 + 23)`.
- The budgets come from the patch buffer `RaStreamDataEditable(0x400, 0x1000, 0x1c)` (0x71f987):
  A = 1024*densityScale vertices, B = 4096 indices. [I: `vf11` uses the patch's `vf5`/`vf6`
  capacity getters; that `vf5` is the vertex count and `vf6` the index count is inferred]
- viewDistance defaults to 70, and `ps = 256/floor(256/(vd/4))` (0x71fe20, 0x71fda0).
- No import, and no close-range objects.
