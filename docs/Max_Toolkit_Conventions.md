# The Battlefield toolkit's model conventions (3ds Max, `.3ds`, and what the `.sm` really holds)

Read out of the Mod Development Toolkit's own tools - Rexman's MAXScript exporter (`BF Maxscript\bfTool_maxZipScript.mzp`,
a ZIP of 30 readable `.ms` files) and DICE's standalone `3D Model Conversion\3dsToSm.exe` - and checked against the
meshes they produced. None of it needs 3ds Max installed; it explains what a Max user's files mean and lets the editor
produce the same `.sm` from any modelling tool.

## There is no file format for "a model with its collision and shadow" - there is a naming convention

Both the exporter (`parseSceneNames` in `functions/_SM_Export.ms`) and `3dsToSm.exe` read the roles off the OBJECT NAMES
in the scene. First three letters, lower-cased, then the digits that follow:

| name | role |
|---|---|
| `LOD01` .. `LODnn` | visible detail levels, highest first |
| `COL01` | simple collision (soldiers, vehicles) |
| `COL02` | complex collision (projectiles) |
| `shadow` | the real-time shadow mesh |
| `bbox` / `bounds` | an optional geometric bounding box (else LOD01's) |

Objects sharing a role and number are merged. A single unnamed object exports as `LOD01`. Material IDs on the collision
faces select the engine's damage/effect material. `3dsToSm`'s `settings.txt` shows the rest of the pipeline:
`setScale 0.1`, `setStride 32`, `setversion 10`, `setLightmapped 0`.

RefractorForge implements this in `Formats/Mesh/MeshParts.cs`: any model file whose objects follow the convention (OBJ `o`/`g`,
a `.3ds` object, or anything Blender converts - it keeps object names) is written with its own LODs, both collision
sections, its shadow and its box. Two forgiving differences: a role with no number is 01, and objects the convention does
not name are folded into LOD01 and listed in the dialog rather than dropped.

## Axes and winding

The exporter writes a vertex as `x, z, y` - Max's Y and Z swapped, a reflection, not a rotation. Every retail mesh went
through that, so a `.3ds` or a Max scene is landed the same way (`UpAxis.ZSwap` in `MeshFit`) and faces the way its
author saw it.

**The engine reads a triangle clockwise from outside.** Measured on every retail mesh checked (`0_CL_box01_A1`, `bullet_m1`,
`DC_roadbarrier1_m1`, ...): the geometric normal of the parsed face order opposes the stored vertex normals on 100% of
faces. OBJ, Blender and Max all wind counter-clockwise in their own right-handed frames, so an import is turned over
(`MeshFitOptions.FrontFaces`), and the Max reflection does that turn by itself. Before this every imported model was
written back-facing; the editor draws both sides, so it never showed.

## The collision section's BSP is one node per face ("SimpleBSP")

`docs/SM_Collision_RE.md` had the section fully decoded except the tail. `WriteSimpleBsp` in the exporter is the tail:

```
u32 nodeCount (= faces) ; u32 0 ; u32 faces
per face: f32 nx, ny, nz ; u32 0 ; u32 a, b, c ; u32 materialId        (32 bytes)
char[24] "SimpleBSP tree method  \0"
u32 faces ; u32 0 .. faces-1 ; u16 0
```

The vertex's fourth float is X with its low 16 bits overwritten by the material id. The node normal is the LEFT-hand
normal of the (clockwise) face - outward. Confirmed byte-for-byte against Saigon68's Desert Combat props
(`DC_roadbarrier1_m1`, `VSS_Crate` ...) and three retail meshes; DICE's own `DShape1VertexBuffer` sections carry the same
per-face nodes with a real tree in the counts. `StandardMeshWriter.BuildCollisionSection` writes it. 266 retail sections
are exactly this trivial shape.

Material ids: `game/materialManagerdefine.con` names terrain 0-15 and stairs 96-98; the "Basic Materials" 79-95 are bare
numbers, described in `Formats/Cons/CollisionMaterials.cs` by the retail meshes that use them (88 stone, 81 wood, 92
brick/plaster, 85 metal, ...).

## The shadow block

After the LODs: `u32 hasShadow; [u32 1; u32 1; material header "<mesh>_"; 32-byte vertices with only a position; faces
in the SAME order as the parsed visible order]; u32 portalChunkSize`. The exporter explodes the shadow first (three
vertices per triangle). `0, 0` is how 699 of BfVietnam's 1,997 meshes end. `StandardMesh.Shadow` reads it,
`StandardMeshWriter` writes it.

## Lightmaps (from `functions/Lightmaps.ms` - for the object-lightmap work)

- The second UV set is **map channel 3**, produced in Max by `Unwrap_UVW` + `flattenMapNoParams()` - an automatic
  flatten. The `.sm` visible header carries vertex format **9233** and a 40-byte stride when lightmapped; both UV sets are
  written V-flipped (`-(v - 1)`).
- Object lightmaps were rendered by Max's Render To Texture (`INodeBakeProperties`, a `LightingMap` element on
  `bakeChannel 3`, auto-sized), which is why textures with alpha had to resolve in Max - the cut-outs cast shadows.
- The file name is `"_" + posX + "-" + posZ + "-" + posY` with each coordinate cast to integer, so a negative coordinate
  yields a double dash (`..._747--4-132.tga`). That is what DICE's toolchain wrote; it is not a bug.
- The 24-bit render was converted to an 8-bit palettised TGA (greyscale ramp palette) taking the first byte of each
  pixel.

## Tools worth knowing are on disk

`D:\Games\EA GAMES\Battlefield Mod Development Toolkit\`: `3dsToSm.exe` (its `_bin\DUMP_3DS.exe` crashes on this
machine, so it is not used as an oracle), `bfmeshview253`, `gmax` (no Battlefield game pack, so it cannot export), and
the MAXScript source. `.max` files themselves are OLE2 compound documents with proprietary internals and are not read.
