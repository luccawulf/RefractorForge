# StandardMesh (.sm) collision section — reverse-engineering notes

Status: **FULL STRUCTURE DECODED** (every field, from BfMeshView's own source `modStdMesh.bas`, confirmed by a
byte-exact round-trip of all 1800 sections). Reading + a wireframe overlay shipped; a writer exists. The ONE thing
still unknown is the **BSP node semantics** (`qdata`/`zdata`) — and BfMeshView doesn't know them either (it
`Seek`s past the qblock; its col writer is marked "broken"). So we can write a col with an **empty BSP** and
**test in-game** whether BFV rebuilds it.

### Field structure (authoritative — `stdmeshcol` in BfMeshView's modStdMesh.bas; matches our byte-exact decode)
```
u1  Long  = 0xEB97C2FA              (not a magic — it's a constant field)
u2  Long  = 5
vertnum Long ; vert[vertnum]  : colvert { float3 v ; Single w }                 (16 B)
facenum Long ; face[facenum]  : colface { i16 v1,v2,v3 ; u8 matid ; u8 flags }  (8 B)
qnum Long ; qdata[qnum]       : colq   { i16 i1,i2,flags,u1 ; f32 u2,u3,u4 ; i32 u5,u6,u7 }  (32 B) -- BSP, opaque
u3 Long ; flags Long ; ustr String*24   ("DShape1VertexBuffer" or zeros)
znum Long ; zdata[znum] : Long           -- index list, opaque
u4 Integer (2 B)
```
Size check (bullet, 3v/1t): `8+4+48+4+8+4+32+4+4+24+4+4+2 = 150` ✓. The earlier "w / separator / tail" guesses are
now exact: `w` = colvert.w (Single), the tri "separator" = `matid`+`flags` bytes, the tail = the qblock + ustr +
zblock. `qnum` is a real BSP that grows with shape complexity (bullet 1, flat quad 2, 3-D box-10t → 139 nodes).
- `StandardMesh.TryParseCollision` → verts + triangles; **100% (1793/1793)** of `standardMesh.rfa`. Viewer renders
  a green wireframe overlay (**Layers → Collision**; `MeshLibrary.TryGetCollision`).
- `StandardMesh.TryParseCollisionFull` (verts+w, tris+sep, raw tail) + `StandardMeshWriter.WriteCollisionSection`
  **round-trip every real section byte-exact: 1793/1793 (standardMesh) + 7/7 (objects)**. So the header + vertex
  block + triangle block are write-correct; the BSP/DShape **tail is preserved verbatim** (not yet generatable).
- Gates: `objsm` (synthetic parse + round-trip) + the `smcol` survey prints the live parse + round-trip rates.
Tooling: `smcol <meshArchive.rfa> [meshName] [rows]` (survey = rates; named = per-word + strings dump).
**To make imports collidable, the remaining work is generating a tail for new geometry** (the structure is mapped
below; it's a content-variable serialized DShape — best cross-referenced against `bfmeshview253`).

## Where it sits in the .sm
After the header (`u32 version`, 4 unknown, 6×`f32` bbox, `u8` qflag for v10) comes:
```
u32 numCollisionMeshes
  per mesh: u32 sizeOfSection ; byte[sizeOfSection]   <-- the section decoded below
u32 numLods ...
```
The reader used to skip each section; it now captures it. Lots of meshes have collision — survey
`standardMesh.rfa`: `0_CL_box01_A1` (4v/2f, 210 B, 1 section) and `bullet_m1` (3v/1f, 150 B, 2 sections) are
the smallest, cleanest samples. Note `_A1` boxes are a flat quad (bbox minY==maxY).

## Code: read, round-trip, generate
- `StandardMesh.TryParseCollision` → verts + triangles (for the viewer overlay). 100% on 1793/1793 sections.
- `StandardMesh.TryParseCollisionFull` + `StandardMeshWriter.WriteCollisionSection` round-trip a section **byte-exact**
  (1793/1793 + 7/7) — header/verts/faces are write-correct, the qblock/zblock kept verbatim.
- `StandardMeshWriter.BuildCollisionSection(verts, tris)` / `BuildObjCollision(obj)` **generate** a col from raw
  geometry with an **EMPTY BSP** (`qnum=0`, `znum=0`, `ustr`=zeros, `matid/flags=0`, `w`=x). It parses back through
  our reader and embeds cleanly in a `.sm` (`StandardMeshWriter.Write(obj, col)`; `Consumed==Total`). The Viewer's
  `.obj` export has an opt-in **"include collision (experimental)"** checkbox that writes it + `HasCollisionPhysics 1`.

## THE open question: does an empty BSP work in-game?
Every real section has `qnum>0` (bullet 1, box-10t → 139). We don't know if BFV **rebuilds** the BSP from the faces
at load (→ empty BSP works, imports become solid) or **requires** a pre-built one (→ empty BSP = no collision, or a
load crash). **This is the user's in-game test** of an experimental export. If it needs a real BSP, that's the last
crack — and it's genuinely unsolved: even **BfMeshView skips the qblock** (`Seek #ff, 1 + skip` in `ReadStdMeshCol`)
and its col writer is flagged "broken", so there's no reference for the node format. Cracking it would mean RE'ing
the `colq` node semantics (i1,i2 indices; u2,u3,u4 floats = split plane?; u5,u6,u7 indices = children/faces) + the
`zblock` index list, then writing a BSP builder. (`3dsToSm.exe` is the only known tool that *generates* a col.)

## Path B (.con-level collision primitive) — INVESTIGATED, DEAD END
Searched the BFV object archives (`objects.rfa`) with the `congrep`/`conblock` Demo probes. Every collidable
static object is simply:
```
ObjectTemplate.create SimpleObject <name>
ObjectTemplate.geometry <name>
ObjectTemplate.HasCollisionPhysics 1
```
— nothing else. `physicsType` / `setCollisionMesh` / `geometry.collision` / `collisionPart` / `lodCollision` all
return **0 hits**; the only `collisionMesh` is `SkeletonCollisionMesh` (animated soldiers, not static props). And
the collidable geometries' `.sm` files **do carry col data** (e.g. `o_Hue_wall_m1.sm` = col×2). So **static
collision is always baked into the `.sm` col section** — there is NO `.con` primitive to emit instead.
Confirmed by `3dsToSm`'s readme: collision is authored as `COL01`/`COL02`-named meshes that the converter writes
into the `.sm` col section.

So `HasCollisionPhysics 1` is necessary but not sufficient: without col data in the `.sm`, the object has no
collision. The exporter now writes `HasCollisionPhysics 1` (collision-ready) + a `rem` note, but real collision
still requires the col section.

## The only real path: A — crack + write the DShape col
Finish open questions 1-3, then write a collision section. Validate by round-tripping a real section byte-exact
first (like RFA/DDS/.lsb were), then generate a box-from-bbox col for an import. **Interim workflow for the user:**
model `COL01`/`COL02` meshes and run the toolkit's `3dsToSm` (it bakes col), or accept no collision.

Current `.obj`→`.sm` export writes `numCollisionMeshes 0` (valid, but you walk through the mesh) +
`HasCollisionPhysics 1` in the `.con` stub.
