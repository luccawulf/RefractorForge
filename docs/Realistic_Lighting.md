# Realistic lighting — how the bake works and what it is solved against

The editor's **Window ▸ Realistic Lighting** traces a level's light (a sun, a sky, placed lamps, and all of it
bounced) and then works out what Battlefield Vietnam needs in order to draw that light. This note records the engine
facts it rests on, the method, and how far each part has been verified.

Everything in *What the game does* was read statically from `BfVietnam.exe` (ImageBase 0x00400000) and the shaders in
`effects.rfa`. **None of it has yet been confirmed with an in-game calibration shot** — see *Verification*.

## What the game does

### Terrain: no light at draw time

`effects/RaShaderTerrain.fx` ships one live technique, fixed-function:

```
stage 0: SelectArg1  Texture        (the colour tile, DiffuseMap)
stage 1: Modulate2x  Texture x Current   (the detail texture)
```

Its lit pixel shader (`detailPixelShaderBlinn`, a DOT3 against a detail bump map) is present and commented out
(`pixelshader = NULL`). So the ground is `tile x detail x 2` and nothing else: no sun, no ambient, no lightmap. The
tiles are the lighting — which is why every retail level's tiles already carry painted shadows (x0.45–0.78 of the lit
ground beside them), and why `LightmapShadowBits.lsb` changes nothing you can see on the ground.

### Objects: two lights and a constant

`RaShaderPPLSTs1DifLmp.fx` (lightmapped) and `RaShaderPPLSTs1Dif.fx` / `RaCommon.fx` (not) both end in:

```
LightDOT3 = LightMaterialColor.rgb * sat(dot(TN, LD)) + secondaryLightColor * sat(dot(TN, BL))
colour    = saturate(2 * (Prelight * LightDOT3 + LightAmbient)) * texture + Prelight * specular
```

`Prelight` is the lightmap's **blue** channel (absent in the non-lightmapped shader, i.e. 1), `TN` the normal map's
normal, `LD` / `BL` the two light directions in tangent space.

What is uploaded into those names is in the exe, in the routine at **0xa4b5a0..0xa4ba08** (the effect handles are
cached at 0xa4bd..: +0x84 `invXformLightDir`, +0x88 `invXformLightDir2`, +0x8c `LightMaterialColor`, +0x9c
`LightAmbient`, +0xbc `secondaryLightColor`; `ID3DXEffect::SetVector` is vtable +0x88):

| shader name | what the exe sets |
|---|---|
| `invXformLightDir` | light 0's direction, rotated into object space, negated — toward the sun |
| `invXformLightDir2` | light 0's direction **with x and z negated and y kept**, then the same rotate and negate |
| `LightMaterialColor.rgb` | light 0 colour x material diffuse x **0.5** |
| `secondaryLightColor` | light 1 colour x material diffuse x **0.5** |
| `LightAmbient` (0xa6d2f0, 0xa6dc90) | (material ambient x renderer ambient + material emissive) x **0.5** |

The two `LightAmbient` uploads read the renderer's ambient at +0x118 and +0x124: `ambientColor` and `LMambientColor`,
the second in the variant that also sets up the lightmap. The console commands store their values unscaled
(`renderer.diffuseColor` -> +0x5c, `SecondaryDiffuseColor` -> +0xb8, `ambientColor` -> +0x74, `LMambientColor` -> +0xfc
of the scene object).

So the halves cancel the shader's doubling, and what multiplies the texture is

```
saturate( P * matDiffuse * (sun * sat(N.L) + back * sat(N.B)) + matAmbient * ambient + selfIllum )
```

with `B` the sun mirrored to the opposite compass bearing at the same height: `B = (-L.x, L.y, -L.z)`.

Consequences:

- Reading the shader without the upload gives a formula **twice as bright**, with no back-light direction. That is
  what the editor assumed until 2026-11; it would clip every retail wall (Operation_Irving: sun .975, back .55,
  LMambient .25).
- Every wall is reached by the sun **or** the back light, so a lightmap shades every wall. Only a face that looks
  **down** has neither and shows the ambient alone.
- The lightmap is one number. It scales the two lights and cannot recolour them: shade has the ambient's colour, for
  the whole level, and a lamp lights a lightmapped wall in `diffuseColor`'s colour.
- Every retail BFV level sets `SecondaryDiffuseColor` (Hue .317, Irving .55, Saigon68 .25/.27/.29, Cedar Falls
  .30/.36/.38).

Other shaders: overgrowth is fixed-function `Lighting = true` (texture x vertex diffuse from the D3D lights);
undergrowth is unlit, `saturate(4 x atlas x terrain colour)`; `RaShaderShadowPatch.fx` is the projected moving shadow,
alpha from `shadow.shadowColor`.

`Render/EngineLight.cs` is this section as code; the viewport's "game's lighting" mode is the same formula in GLSL.

### Measured in the game (2026-11-27)

Everything above was read out of files. The calibration level (below) put it in front of the game, and two
screenshots - `rf_calib` and `rf_calib_safe`, 3840 x 2400 - were read back patch by patch:

| what | result |
|---|---|
| the per-pixel formula (a tangent-frame mesh, 24-bit lightmap) | **all 49 patches within 0.3 of 255** |
| the back light | 0.3 degrees from the sun mirrored in azimuth |
| the net factor | 1, not 2: the uploaded halves cancel the shader's doubling |
| which lightmap channel | blue, on a tangent-frame mesh |
| a sun colour of 1.25 | carried through to the final clamp on a tangent-frame mesh |
| `materialDiffuse` | scales the two lights, not the ambient |
| terrain | `tile x detail x 2` to the count, and no trace of the red sun on it |

It also found four things nothing in the files had said:

1. **A mesh without a tangent frame is lit differently.** 32- and 40-byte meshes are drawn by the fixed-function
   shaders. The engine does override the shader file's 0.5 texture factor with `LMambientColor`, so the shape of
   the formula is the same - but the two lights are summed per vertex and **clamped to 1 before** the lightmap
   multiplies them, and the lightmap multiplies them **channel by channel**. A coloured lightmap therefore colours
   the light on those meshes (it showed `(v, v/2, 3v/4)` exactly), and on a tangent-frame mesh it is read as its
   blue channel alone. `EngineLight.Material.VertexLit`; with it both paths match to 0.2 - 0.3 of 255.
2. **An 8-bit lightmap is read through the level's `Objectlightmaps/Palette.pal`, not through the colour map in the
   .tga**, and held in a 5:6:5 texture. A board whose file carried a recoloured palette was identical to its grey
   twin; index 64 was drawn as 41 (Hue's palette says 47, and 47 through five bits is 41), 128 as 132, 191 as 189.
   The palettes are not ramps: 24 retail levels share a curve with a toe (64 -> 47, 96 -> 73), Operation Irving's
   is black up to index 80, and Battlecraft-made levels carry a tinted ramp from about 25 to 185. The editor had
   been writing "index = light" with a linear ramp inside each file - which the game never reads. A **24-bit**
   lightmap is accepted, read as written and kept at 8 bits. `LightmapPalette` now picks the index the level's
   palette SHOWS as the value meant, dithered across the 32 levels a 5-bit texture has; a whole-level bake on a
   level whose palette cannot say it (not grey, or not reaching both ends) replaces the palette with the plain
   ramp; and the editor reads existing maps the same way the game does.
3. **A lightmap file on a mesh with no unwrap is ignored.** Both the 32-byte board and the 64-byte one with an empty
   lightmap channel were lit exactly as the boards with no file at all. The bake no longer writes those files.
4. **An object with no lightmap is lit by the formula in the open, and dimmed in terrain shadow.** The first level
   showed such boards at 39% and 46% of the two lights and that looked like a new rule. It was not: that level's
   terrain shadow map had been written with its flag set everywhere, by this project's own mistake - the codec
   called the flag "lit". `rf_calib2` (screenshot 232, twelve boards) crossed the candidates and settled it:

   | where the board stands | sun and back light it gets | ambient |
   |---|---|---|
   | shadow-map flag clear, over any ground colour | **all of it** - `EngineLight.Dynamic`, to the count | `ambientColor`, whole |
   | flag set, ground nearby black | 0.46 (the first level, over Hue's own dark ground: 0.39) | whole |
   | flag set, ground nearby dark grey / mid grey / white | 0.58 / 0.71 / 1.0 | whole |
   | a lightmapped board, either place | unchanged: the shadow map does not touch it | `LMambientColor` |

   So the flag means **shadow**, which the retail files confirm: on every level tried the ground texture under a
   set flag is about half as bright as under a clear one (Hue 50 against 94, Quang Tri 41 against 82, Fall of
   Saigon 28 against 52) - and only with the raster read row 0 = south, column 0 = west; the other three
   orientations show no difference, which settles the orientation too. The editor's two bakes already wrote
   shadow as a set flag; `LightmapShadowBits.ShadowFlag` / `ToShadowMask` are now named for what they are.

   **The rule, from the exe** (a debugger breakpoint on the routine gave its caller; the rest is disassembly of
   `bfvstock.exe`):

   - Every frame, for every object with no lightmap, `0x691860` takes the object's bounding sphere (radius `r`,
     centre `C`) and a global direction `S`, and samples the terrain at four points: `C + A`, `P - A`, `P + B`,
     `P - B`, with `P = C - 0.5 r S`, `A` a quarter radius along the object's Z axis and `B` a quarter radius along
     its X axis. It averages the four.
   - One terrain sample (`0x7243a0`) is **255** when the point is not in shadow - the shadow bits say so, or it is
     off the map, or higher above the ground than `Terrain.ShadowSamplingCullY`, or the tile is not a DXT1 texture.
     In shadow it is **the brightness of the terrain tile itself**: the mean colour of the 4 x 4 DXT1 block under
     the point, weighed 0.3 / 0.59 / 0.11.
   - `0x74a050` eases toward that over `Terrain.ShadowBorderFadeTime` and sets
     `intensity = standardMeshMinIntensity + (1 - standardMeshMinIntensity) x value / 255`, which scales the two
     lights and leaves the ambient alone. (`renderer.standardMeshMinIntensity`: 0.2 unless the level sets it;
     Hue sets 0.3.) An object that has a lightmap returns at the first line.
   - `Terrain.ShadowAmbient` is not part of it. That Hue's 100 matched the first level's 0.39 was a coincidence:
     0.3 + 0.7 x 33 / 255 is 0.3906, and so is 100 / 256.

   So an object with no lightmap standing in terrain shadow is lit by the sun and back light in proportion to
   **how bright the ground texture is around it** - never less than `standardMeshMinIntensity`. 

   **Statics do it once, differently** (found by tracing the third caller of the terrain sampler). The per-frame
   routine above is the one soldiers and vehicles go through. A static object gets its value once, when `0x673700`
   walks the level's objects: `0x673570` takes the sun vector, drops its height and normalises it, and `0x6722a0`
   makes **five** points on an arc **1.1 radii** from the object's position toward the sun, turned -22.5, -13.5,
   -4.5, +4.5 and +13.5 degrees about the vertical. Each is one terrain sample as above, except that **a sample over
   128 counts as 255**; the five are averaged and set with no fade. The radius is the mesh's own (`0x6852d0`): the distance from the
   mesh **origin** to the farthest corner of its bounding box - 4.88 m for a board, not its 3.8 m half-diagonal
   (an object with children takes the larger of that and each child's reach). The sampler widens the tile's 5:6:5
   colours by shifting alone (`0x7216d0`), so white reads 248 / 252 / 248 and a flat 128 grey stays just under the
   128 line. Run on the photographed level's own shadow bits and tiles (`tools/lightcal shares`), with nothing
   fitted, the rule gives 0.536 / 0.447 / 0.577 / 0.715 / 1.0 for the five front boards; the screenshot showed
   0.542 / 0.457 / 0.577 / 0.709 / 1.0.

   In the editor: `EngineLight.StaticShare` (`StaticSamplePoints`, `StaticRadius`, `TileColour`, `TerrainSample`,
   `StaticTerrain`, `LightShare`). With Game lighting on, the viewport gives every placed object that has no
   lightmap this share of the two lights, from the shadow bits a save would write and the ground as drawn; it
   follows a bake, an unbake, a moved object and a repainted ground. Not modelled: the child-object radius, the
   `ShadowSamplingCullY` height cut-off, and the per-frame four-sample rule for soldiers and vehicles (the editor
   has none to draw).

   What it means for a bake: the ground texture and the shadow bits together decide how bright soldiers, vehicles
   and unlightmapped props are. Where the bits say shadow they follow the ground - dark where the baked ground is
   dark, bright in a lamp pool - and where the bits say sunlit they get the level's full light whatever the ground
   looks like.
   A stray lightmap file on a mesh with no unwrap changes nothing here either.

## The method

1. **Describe the light as light** (`Render/Daylight.cs`): a sun (direction, colour, angular size), a sky (two-colour
   dome, and a *strength*: open-sky light on level ground as a fraction of the sun's on a facing surface), the
   ground's mean reflectance. Linear units; the sun delivers luma 1.
2. **Trace it** (`Render/LightTracer.cs`, in the scene `Render/LightScene.cs` builds): the terrain is triangles in the
   same BVH as every object, so a ray finds the nearest surface and that surface has a colour — outdoors, bounce is
   mostly the ground. Alpha-tested materials are tested per ray against a reduced mask of their texture; blended glass
   and the `portal_black` window planes are left out. Sun through its disc; hemisphere rays for sky and for paths
   followed two bounces with a shadow ray to the sun at each. Placed lamps are added in the same scene
   (`Render/LampLight.cs`): direct, shadowed, and at every path vertex so their light bounces.
3. **Linear to screen**: `display = (exposure x linear) ^ (1/2.2)`, exposure chosen so an open surface facing the sun
   is drawn at *Brightness*. The engine multiplies gamma-encoded numbers and never converts, so this is done once, on
   the result. It is what makes clear-sky shade come out near 0.5 of sunlit (linear 0.2), where retail paints it.
4. **Solve for the engine** —
   - *Ground* (`Render/GroundLightBake.cs`): tile = ground as painted x display light, RGB. The sun's visibility is
     traced at the ground picture's size, sky and bounce at half that and filtered; the slope's facing comes from the
     smoothed height-field. Nothing is drawn darker than `LMambientColor`.
   - *Lightmaps* (`ObjectLightmapBaker.BakePhysical`): per texel, average the linear light, convert, and invert the
     engine formula: `P = (luma(display) - luma(ambient)) / luma(sun * N.L + back * N.B)`, clamped to 0..1. Sky and
     bounce are traced on every second texel and filled between by world distance and facing.
   - *Light colours* (`DaylightModel.Fit`): `LMambientColor` is the floor (ground bounce onto a downward face, capped
     at 0.30); `diffuseColor` and `SecondaryDiffuseColor` are searched so that with the lightmap at full every open
     facing is at least as bright as it should be — a lightmap only takes light away — and otherwise as little over as
     possible; `ambientColor` is what is left for an object with no lightmap. Hues are read off the surface each
     colour exists for; only strengths are searched. `shadow.shadowColor`'s alpha is set to the open-shade ratio.
   - A mesh with no unwrap gets no map: the game ignores one (measured), and lights the object itself.
   - A mesh without a tangent frame is solved against the clamped form of the formula (see "Measured in the game").
   - The maps are written through the level's palette (`LightmapPalette`), so the game shows the value solved for.

On a level lit by lamps the fit keeps both lights at least as strong as *Lamp headroom* and gives them the lamps'
colour; the moon is then written low in the maps. The cost is that soldiers and vehicles, which nothing takes light
away from, are that bright too.

## Verification

Done, headlessly:

- The traced light against cases with a known answer (open sky, ground bounce under a downward face, a penumbra, a
  cut-out's coverage) — `LightTracerTests`.
- The solve: on a synthetic floor-and-roof mesh the engine formula applied to the baked map is within 0.03 (display
  luma) of the trace — `RealisticLightingTests`.
- On real levels, a picture rendered twice from one camera — the trace, and the engine formula over the baked ground
  and maps — differs on lightmapped objects by **0.035** on retail Hue (day), **0.037** on al_vietnas (day) and
  **0.025** (night, 49 street lamps).
- The viewport's GLSL validates with `glslangValidator`.

Not done:

- **The rise of an unlightmapped object's light with ground brightness, in terrain shadow** (see "Measured in the
  game", point 4). The viewport draws such objects as if in the open.
- `renderer.diffuseColor` above 1 (the fit goes to 1.25 for a low sun) is outside what retail levels use. The engine
  halves it before the shader, whose constants stop at 1, so 2 is the arithmetic limit; overgrowth, lit by the same
  light through fixed-function, will be brighter on its sun side.
- Battlefield 1942 has its own rules and its own section below; nothing above is assumed for it.

## Checking it in the game: the calibration level

`LightCalibration` (Render) builds a level whose one screenshot is a measurement of the formula. It is a copy of a
retail level (`LevelCopy`, so the container, the game mode files and the terrain are ones the game already loads)
with every static object taken out and one row of test boards put in, both teams' main-base spawn points moved to
stand 24 m south of the boards facing them, and the level's light set to one channel per term:

```
renderer.diffuseColor           1.25/0/0     the sun is red (and above 1, to see what the engine does with that)
renderer.SecondaryDiffuseColor  0/1/0        the back light is green
renderer.LMambientColor         0/0/0.25     the lightmapped ambient is a quarter blue
renderer.ambientColor           0/0/0.5      the plain ambient is half blue
```

A board is a black plate (a black texture stays black under any light, so the plate's outline can be found in a
picture without knowing the formula) with 49 white patches and one gap. The patches' triangles all face the camera;
their vertex NORMALS are authored and point round the whole sphere, and each patch's four vertices carry one
lightmap coordinate, the centre of a flat cell, so its lightmap value is exactly one of 0, 64, 128, 191, 255:

| rows | patches | what they answer |
|---|---|---|
| 0 - 2 | normals at 45 deg up, level and 45 deg down, eight bearings each, plus up, down, toward the sun, toward the predicted back light, away from each, and one square to both - all at lightmap 128 | where each light really is (solved from the patches with no direction assumed), and its strength |
| 3 | the sun-facing normal and the back-facing normal, lightmap 0 to 255 | whether the lightmap scales the light linearly, what is left at 0, where it clips |
| 4 | straight down and straight up at lightmap 0 and 255; three patches on a `materialDiffuse 0.5` material | whether the ambient is scaled by the lightmap; what the material colour multiplies |

The same board is written in each vertex layout the engine draws a lit mesh from, because the layout picks the
shader, and the two shader families are NOT the same arithmetic:

- **with a tangent frame** (64-byte, what most retail buildings are) - `RaShaderPPLSTs1Dif[Lmp].fx`, the per-pixel
  formula this document describes;
- **without one** (32 or 40-byte: every imported model, every mesh the editor widens to hold a lightmap, and 19
  retail meshes) - `RaShaderPVLS1Dif[Lmp].fx`, which is fixed-function: `Lighting = true`, and for the lightmapped
  one `ColorOp = MultiplyAdd(lightmap, vertex light, textureFactor)` with `textureFactor` declared as 0.5. Read
  literally that is `0.5 + lightmap.rgb x vertex light` - a floor of one half instead of `LMambientColor`, and
  all three lightmap channels in use. Whether the engine overrides that 0.5, and what colours it gives the
  fixed-function lights, is not something the shader file says. Boards A to D measure it.

| board | mesh | lightmap | question |
|---|---|---|---|
| A | 40-byte | grey, the retail form | the fixed-function lightmapped formula |
| B | 40-byte | none | the same mesh lit as a moving object is |
| C | 40-byte | colour: each level written (v, v/2, 3v/4) | which channel is read - or all three, which would make coloured lamp light on objects possible |
| D | 32-byte, no lightmap channel | one flat value, 128 | is a lightmap applied to a mesh with no unwrap? (the bake writes these) |
| E - H | 64-byte, the same four cases | | the per-pixel path; H has an empty channel and a clear qflag, as 3 in 4 retail meshes do |

The ground in front of the boards carries a black mat with five swatches painted into the terrain tile (64, 128,
191, 255 grey and one colour) and the level's detail texture is replaced by a flat one, so `tile x detail x 2` can
be read off too. The terrain shadow map is set to "sunlit" everywhere so that the darkening of unlightmapped
objects in terrain shadow is not part of what is measured, and fog starts past the boards.

`rf_calib` has all eight boards. The tangent-frame mesh and the 24-bit lightmap are forms this editor had never
written, so `rf_calib_safe` has boards A to D only, with the coloured lightmap as a recoloured palette, in case the
client rejects one of them. The 40-byte board is byte for byte what `StandardMeshRewriter`'s promotion writes
(`LightCalibrationTests`), and both levels start on `bfvietnam_w32ded.exe`.

```
dotnet run -c Release --project tools/lightcal -- build <Hue.rfa> <outDir> rf_calib [--safe]
dotnet run -c Release --project tools/lightcal -- synth <outDir>/rf_calib.calibration.json expected.png
dotnet run -c Release --project tools/lightcal -- read  <ScreenShotN.bmp> <outDir>/rf_calib.calibration.json --out annotated.png
```

`read` finds the plates, checks each is a board (lit patches on a grid, black between them - the HUD has black
panels too), and prints for every board the measured colour of each patch, the two lights solved from the fan, the
ramps, and the distance from the editor's model (`LightCalibration.Predict`). `synth` draws what that model expects.

## Limits that are the engine's, not the bake's

- Downward faces show the ambient alone.
- Nothing lightmapped is darker than `LMambientColor`.
- A lamp's colour is on the ground only.
- Dynamic objects are lit by the two lights, unshadowed by lamps.
- Per-object lightmaps top out at 1024 px (2048 crashes); terrain tiles are DXT1 with mipmaps.

## Lightmap-only copies (measured 2026-11-28, calibration levels 3 and 4)

The engine lights an object with its two directional lights and lets the lightmap only scale them, so a wall side-on
to the sun's bearing, a ceiling, or anything a placed lamp lights cannot show what a bake traced. Two screenshots
settled how to get past that with data alone:

- A mesh WITHOUT a tangent frame (32/40-byte vertex) is drawn fixed-function. Its vertex colour is
  `sat(materialDiffuse x (sun x sat(N.L) + back x sat(N.B)) + selfillum)`, from the stored normal, and the game draws
  `texture x sat(lightmap.rgb x vertexColour + LMambientColor)`. `materialAmbient` does nothing there.
- So `selfillum 1 1 1` makes the vertex colour 1 and the object shows `texture x (lightmap + LMambient)` on every
  face, with any normals and any light colours (board A of level 4: 26 / 90 / 154 / 217 / 255 under lights of 0.3 and
  0.1; a coloured 24-bit map comes through per channel). Steering every normal straight up under lights that clamp
  does the same (level 3) but needs the level's lights raised; it is not used.
- `lighting false` ignores the lightmap and draws full white. A 64-byte (tangent-frame) mesh with `selfillum 1`
  follows the lightmap only at a distance and is white up close, so a copy must be on the 40-byte vertex.

Tools > Lighting > Lightmap-Ready Objects, "Light the copies from their lightmaps alone" (BfVietnam, on by default)
makes such copies: every LOD rewritten to 40 bytes (a 64-byte mesh keeps its unwrap and drops its tangent frame),
`materialDiffuse 1 1 1` and `selfillum 1 1 1` in every lit subshader. The Realistic bake then solves a per-vertex
mesh per channel - map = (traced light - LMambient) / vertex colour - and writes it as a 24-bit TGA. 1,931 of 1,990
retail meshes convert (`lightcal onlyscan`); the rest (19 that will not unwrap, 6 triangle strips) stay as they are.

Two things found on the first full level (Operation Flaming Dart):

- With `lightingSpecular true` and a `normalmap` left in a copy's shader, every copy NEAR the camera drew full white and
  only distant ones showed their lightmaps. Copies now get `lightingSpecular false` and lose `normalmap` and
  `highEndPerPixel`.
- 864 MB of 24-bit lightmaps (288 at 1024 px) ran the client out of memory while loading (null effect from
  `effects/particles.fx` at 2 GB). The bake holds 24-bit maps to 500 MB (`LightmapBudget`): colourless maps go back to
  8-bit, the largest of the rest are halved. 500 MB is a working value, not a measured ceiling.

## Battlefield 1942 (measured 2026-11-28, calibration levels rf_calib42 and rf_calib42t)

Nothing measured on BfVietnam was assumed. `lightcal build <El_Alamein.rfa> <out> <name> --bf1942` builds a copy of
El Alamein with six boards, the sun (`diffuseColor`) red, `ambientColor` green and `globalAmbientColor` blue; the
game's screenshot was read back with `lightcal read`. BF1942's exe has no `SecondaryDiffuseColor` and no
`LMambientColor`.

| What | Result in the game |
|---|---|
| 40-byte mesh, 24-bit lightmap | texture x 2 x lightmap per channel, clipped. Level 64 tinted (1, .5, .75) read 129 / 64 / 97. No light is added: the sun's direction, the three colours and `materialDiffuse` change nothing; lightmap 0 is black. |
| 40-byte mesh, 8-bit lightmap | the same, through the LEVEL's `ObjectLightmaps/Palette.pal` at its full eight bits (entries 34 / 86 / 137 read 68 / 172 / 253). The file's own colour map is ignored. |
| 40-byte mesh, `selfillum 1` | no different: texture x 2 x lightmap. |
| 32-byte mesh (no lightmap channel) | a lightmap file is ignored. texture x 2 x (materialDiffuse x diffuseColor x N.L + ambientColor + globalAmbientColor); the sun fitted 0.1 degrees from `sunLightDirectionVec` as written; no second light; `materialDiffuse` does not scale the ambients. |
| 40-byte mesh with NO lightmap file | one flat colour (154 / 145 / 148) on every face. Origin unknown; not modelled. |
| Terrain | takes none of the three colours (the ground chart read as painted x detail x 2). |
| TreeMesh (swaying trees) | ignores a lightmap file: trees with no file, a black one and a red one looked alike. |

What follows from it (`EngineLight.Bf1942`, `ObjectLightmapBaker.Physical.Bf1942`, `DaylightModel.FitBf1942`):

- The lightmap is half of what the screen should show, per channel, written 24-bit; there is nothing to fit and no
  floor, so ceilings, interiors and a lamp's colour come out as traced. A map with no colour in it goes to 8-bit
  through a plain-ramp palette the whole-level bake writes, UNDITHERED: BfVietnam keeps the palette 5:6:5 and a
  dither hides its steps, but BF1942 keeps all eight bits and showed the dither as a grid of dots.
- `diffuseColor`, `ambientColor` and `globalAmbientColor` matter only to what has no lightmap - props without an
  unwrap, soldiers, vehicles, trees. The bake sets the pair closest to the open daylight (written at half of what the
  screen shows) and shares the ambient between the two keys in the level's existing proportion.
- The same 500 MB budget holds the 24-bit maps; BF1942's own ceiling has not been measured.

Not yet known: whether the game darkens the terrain at draw time from `LightmapShadowBits.lsb` (the calibration
levels had it cleared). Known fault: a lightmap-ready COPY of an object that adds child templates (`stebarbwire_m1`
with `e_Barbwire`, `mediclocker` with `mediclockerRepairpoint`) is refused by the dedicated server with "template not
found"; copies of objects without children load. `tools/lightcal/ded-load-check-bf1942.ps1` loads a level on the
headless server and reads its error box.
