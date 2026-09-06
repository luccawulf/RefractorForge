# Importing a 3D model into BF1942 / Battlefield Vietnam

**Status: a textured prop — OBJ, or anything Blender reads — can be brought in, simplified to a triangle budget
with distance LODs, packaged and saved into a map. The in-game look and the collision are the user's gate and
have not been run yet.**

*Tools ▸ Import 3D Model...* takes a model from outside and writes it into the map as a level-local static object.
Nothing goes into the mod: the mesh, its textures and the four `.con` files that register it all live inside the
level archive, so the map is still one file you can hand to somebody.

## Formats: OBJ directly, everything else through Blender

OBJ + MTL needs nothing. FBX, glTF/GLB, `.blend`, STL, PLY, USD and Alembic are converted by **Blender itself**,
run in the background (`Formats/Mesh/BlenderBridge.cs`): the importer writes a small Python script, runs
`blender --background --python-exit-code 3 --python <script> -- <source> model.obj manifest.json` in a scratch
folder, and reads the OBJ that comes out. Blender is the converter; nothing here parses those formats, which is
the point — an FBX reader is a career, and Blender's is maintained by people who do nothing else. Blender is found
from `RF_BLENDER`, the usual install folders (newest version wins) or PATH; without it, OBJ still works.

The script does four things the importer relies on:

- gives every object and material a name the OBJ grammar can carry (no spaces, nothing exotic);
- saves every texture a material's Base Color reaches as a **PNG beside the OBJ** (`Image.save_render` works
  whether the image is packed, generated or on disk) and then **writes the `.mtl`'s `map_Kd` lines itself** from
  the material→PNG map it built. That last step is not optional: the FBX and glTF importers load embedded textures
  as *packed* images, and Blender's OBJ exporter writes a packed image's path as a bare file name that resolves
  against the drive root — the `.mtl` came out pointing at `../../../../../../../crate_diffuse.png`;
- exports **Y-up / -Z-forward** with modifiers applied and faces triangulated, so the dialog's default "Y up"
  is right for anything that came through here;
- writes `manifest.json` — the parts (name, parent, position, size, materials) — for the day the importer builds
  multi-part vehicles.

Verified by `BlenderBridgeTests`, which has Blender author a textured cube, saves it as `.blend`, FBX (textures
embedded) and GLB, and converts all three. It runs only where Blender is installed.

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

**Where the texture is, versus where the file says it is.** A model from a store names its textures by the paths
of its author's machine — the first real import, a Jeep Renegade FBX, said
`D:/Jose Bronze/Documents/3dvenda/.../car_jeep_ren.jpg` while the file sat in a `Jeep_Renegade_2016/` subfolder
beside the FBX. Both ends now hunt for a missing texture by name near the model — the model's folder and its
subfolders three deep, matching without regard to case, and accepting the same stem in any other image format:
the Blender script before it saves (`find_image`), and the editor for an OBJ's `.mtl` (`ObjMtl.ResolveTexture`).
When a texture still cannot be found, the manifest lists it under `missing_textures` with the path the file gave,
the `.mtl` carries no dead path, and the dialog says which file to go and find. An `.mtl` `map_Kd` path may
contain spaces and is parsed as the rest of the line after the options, not the last token.

**The picture is upside down unless V is turned over.** OBJ's texture origin is bottom-left (V grows up the
picture); the engine's — and the editor's, which draws retail meshes correctly and whose decal quad was checked
in game — is top-left. `MeshFit` flips V once (`V' = 1 − V`, its own inverse) on import; `MeshFitOptions.FlipV`
turns it off for a mesh that already speaks the engine's convention. The plain *Import .obj* path does the same.

## Triangle budget and LODs

Measured across 719 shipped BF1942 meshes: a **hero mesh is 1,100–2,200 triangles** (bf109 fuselage 1,382; Sherman
hull 1,146; B17 fuselage 2,235) and a whole multi-part vehicle 2,000–6,000. The dialog's **Triangle budget**
(default 2,000; 0 keeps everything) is what LOD 0 keeps; a denser model is simplified to it on import by
`Formats/Mesh/MeshDecimator.cs` — quadric-error edge collapse with two choices that make it safe for game art:
half-edge collapses only (every surviving vertex is one the author placed; no UV is ever interpolated across a
hand-painted sheet) and UV seams, open borders and material boundaries pinned (the texture cannot tear and two
materials cannot pull apart). A collapse that would fold a triangle over is refused.

**Extra LODs** (default 2) are coarser copies at half the triangles each, written into the same `.sm`
(`StandardMeshWriter` now writes `numLods > 1`; the bounding box is LOD 0's, since a coarser copy's own box is
slightly smaller and would cull the whole object early).

### The LOD ramp, measured

How `GeometryTemplate.setLodDistance` relates to a mesh's LOD count was checked against BfVietnam's `objects.rfa`
+ `standardMesh.rfa` rather than guessed: **the ramp's length never depends on how many LODs the mesh has.** 362
single-LOD static meshes carry the full six-entry ramp `0 / 50 / 100 / 200 / 400 / 800`; six-LOD weapon meshes
carry three entries `0 / 3 / 30`; wheels carry four; 84 six-LOD meshes carry none. Entry *i* is where LOD *i*
takes over, clamped to the last LOD the mesh actually has, and the final entry is where the object stops drawing
(the decal work established that last part in game). So the importer writes exactly retail's static shape, scaled
by the dialog's draw distance (default 800 m): a model with three LODs switches at 50 and 100 m and culls at 800.

A material section cannot address more than 65,535 vertices, and the writer used to throw. `MeshFit.SplitOversizedSections`
now splits it into several sections instead, which share the material name and so share one subshader.

## Collision — experimental

`Solid (bake collision)` writes a collision section built from the mesh's own triangles. The section's structure is
fully decoded **except its BSP**, which is written empty (see `SM_Collision_RE.md`). Whether the engine rebuilds the
BSP from the faces at load or requires a real one **is the open question, and only an in-game test answers it**. If
the empty BSP works, imports are solid for free. Beyond 32,767 vertices no section is written and the object's
`HasCollisionPhysics` goes to 0 rather than promising solidity the `.sm` cannot deliver — but first the coarser
LODs are tried in order, and a LOD-1 collision on a dense model is a far better outcome than none.

## Not built yet

- **Multi-part objects** — vehicles and weapons. OBJ cannot carry a part hierarchy and `ObjMesh` ignores `g`/`o`
  groups. The Blender bridge already writes `manifest.json` with every part's name, parent, position, size and
  materials; what remains is exporting one OBJ per part and generating the `Objects.con` hierarchy
  (`addTemplate` / `setPosition` / `setRotation`, `RotationalBundle` for wheels and turrets) from it.
- **Collada (`.dae`)** is in the list but Blender 5 dropped its Collada importer; the branch exists for older
  Blenders. Export glTF or FBX instead.
- **BF2 / BF2142 meshes.** Everything is structured as `<any source> -> ObjMesh -> .sm`, so a `.staticMesh` /
  `.bundledMesh` reader is one more front-end. Their meshes are far denser than these games use and their normal
  maps and second UV sets have nowhere to go in a `vf 1041` mesh.
