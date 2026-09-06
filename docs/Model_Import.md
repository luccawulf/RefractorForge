# Importing a 3D model into BF1942 / Battlefield Vietnam

**Status: phase 1 done — a textured prop can be built, packaged and saved into a map. The in-game look is the
user's gate and has not been run yet.**

*Tools ▸ Import 3D Model...* takes a model from outside and writes it into the map as a level-local static object.
Nothing goes into the mod: the mesh, its textures and the four `.con` files that register it all live inside the
level archive, so the map is still one file you can hand to somebody.

## What gets written

Exactly the recipe `DecalObject` already proved loads, generalised from one quad with one texture to a mesh with as
many material sections as it has:

```
StandardMesh/<Name>.sm            the geometry (+ collision, see below)
StandardMesh/<Name>.rs            one subshader per material
Texture/<texture>.dds             each texture, power-of-two, with mips
Objects/<Name>/Objects.con        ObjectTemplate.create SimpleObject + geometry + HasCollisionPhysics
Objects/<Name>/Geometries.con     GeometryTemplate -> ../<base>/levels/<Level>/StandardMesh/<Name>
Objects/<Name>/<Name>.con         run Objects / run Geometries
```

plus two patches to files the level already has, both idempotent and shared with the decal path:
`Objects/objects.con` gains `run <Name>/<Name>`, and `Init.con` gains `run Objects/Objects` +
`textureManager.alternativePath <base>/levels/<Level>/Texture`. **If the level ships no `Init.con` the import
refuses** rather than queueing files that could never load.

## The three corrections that make an import usable

A model from outside arrives wrong in three independent ways, so they are three separate choices in the dialog
(`Formats/Mesh/MeshFit.cs`):

- **Up axis.** Blender's world is Z-up; Refractor is Y-up with -Z forward. The conversion is `(x, y, z) -> (x, z, -y)`,
  a proper rotation, so normals come along and the triangle winding still means what it did. Blender's *OBJ
  exporter* already converts, so an OBJ is usually Y-up and an FBX or `.blend` brought in another way is not.
- **Scale.** "Fit the height" for anything you know the height of — a building, a tree, a figure. "Fit the longest
  side" for a vehicle or a gun, which are defined by their length. A reported scale near 0.01 or 100 means the
  source file's units were not what you thought.
- **Origin.** The origin is the point you place and the point the object rotates about. Anything standing on the
  terrain wants it on the ground (the default: centred horizontally, lowest point at y=0).

## Material names are global

The `.sm` binds a section to a shader by **material name**, through one registry shared by everything loaded. An OBJ
whose author called a material `wood` would fight every mod that also has a `wood`, so on import every material is
renamed to DICE's convention: `<Name>_Material0`, `_Material1`, … Sections that shared a source material keep
sharing one name and one subshader.

## The `.rs` grammar (this was broken and is worth knowing)

The engine's shader parser is stricter than ours in two ways, and `RsShaderSet.Write` honoured neither, so every
shader the OBJ export had ever produced was probably inert:

- **Every statement ends in a semicolon.** Miss one and the parser throws, the subshader is never registered, and
  the material silently falls back to untextured.
- **The texture reference is folder-qualified** — `texture "texture/foo"`. All 4,406 references across the shipped
  shaders are; a bare name resolves at the archive root instead of the object's texture folder.

Emitting now lives in one place, `Formats/Mesh/RsWriter.cs`, which `DecalObject` and the importer both use.
`RsShaderSet.Parse` is deliberately lenient about both rules, so a writer→reader round-trip proves nothing —
`ModelImportTests.Rs_writer_emits_the_grammar_the_engine_requires` checks the grammar directly instead.

One thing that reads like a detail and is not: an `alphaTestRef` is written **only** for a cut-out material. Its
presence beside `transparent` is exactly what tells the engine "test, don't blend", so putting one on a solid
surface mislabels it for the engine and for our own viewer.

## Textures

Any image the editor can load → resized to a power of two (the texture manager **silently drops** anything else) →
mip chain. Uncompressed 32-bit by default, which is what the proven decal path ships; DXT5 is a checkbox, a quarter
the size and what retail uses. The texture name is prefixed with the template so two imports cannot fight over one
file.

## Triangle budget

Measured across 719 shipped BF1942 meshes: a **hero mesh is 1,100–2,200 triangles** (bf109 fuselage 1,382; Sherman
hull 1,146; B17 fuselage 2,235) and a whole multi-part vehicle 2,000–6,000. The dialog warns past 6,000. Decimation
inside the editor is phase 2; until then, decimate in Blender.

A material section cannot address more than 65,535 vertices, and the writer used to throw. `MeshFit.SplitOversizedSections`
now splits it into several sections instead, which share the material name and so share one subshader.

## Collision — experimental

`Solid (bake collision)` writes a collision section built from the mesh's own triangles. The section's structure is
fully decoded **except its BSP**, which is written empty (see `SM_Collision_RE.md`). Whether the engine rebuilds the
BSP from the faces at load or requires a real one **is the open question, and only an in-game test answers it**. If
the empty BSP works, imports are solid for free. Beyond 32,767 vertices no section is written and the object's
`HasCollisionPhysics` goes to 0 rather than promising solidity the `.sm` cannot deliver.

## Not built yet

- **Other formats.** OBJ only. FBX / `.blend` / glTF / DAE go through Blender in phase 2
  (`blender --background --python`), with OBJ still working with no dependency.
- **Decimation** to a target triangle count, and a real multi-LOD ramp (`StandardMeshWriter` hardcodes `numLods 1`,
  so all six LOD distances currently point at the one mesh).
- **Multi-part objects** — vehicles and weapons. OBJ cannot carry a part hierarchy and `ObjMesh` ignores `g`/`o`
  groups, so this needs the Blender script to emit one OBJ per part plus a JSON manifest of names, parents and
  pivots, and an `Objects.con` hierarchy generated from that.
- **BF2 / BF2142 meshes.** Everything is structured as `<any source> -> ObjMesh -> .sm`, so a `.staticMesh` /
  `.bundledMesh` reader is one more front-end. Their meshes are far denser than these games use and their normal
  maps and second UV sets have nowhere to go in a `vf 1041` mesh.
