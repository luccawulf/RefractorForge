# BF1942 / BFV skeletal animation formats (`.ske` / `.baf` / `.skn`)

Clean-room notes for the Refractor skeletal-animation files shipped in `animations.rfa`, recovered from the
unstripped Linux dedicated-server binary (`bf1942_lnxded`, namespace `dice::anim`, plus
`dice::ref2::geom::AnimatedMeshTemplate`). Where a convention was in doubt, the server's own code was **run** on test
inputs (a small x87 interpreter over its bytes) rather than read by eye. C# readers and writers live in
`src/RefractorForge.Formats/Animation/`; `AnimationFormatsTests` is the gate: every `.ske`, `.skn` and `.baf` of both
clean installs (BF1942 with both expansions: 85 / 120 / 1,553; BFV: 84 / 123 / 3,227) parses to its last byte and is
written back byte for byte.

All formats are **little-endian**; floats are IEEE-754 32-bit. Strings are `u16 length` (INCLUDING the trailing NUL)
followed by that many bytes.

## Matrices

A matrix is a `float[16]` laid out exactly as the engine's `BaseMatrix4<float>` holds it: basis vectors at `[0..2]`,
`[4..6]`, `[8..10]`, translation at `[12..14]`, `[15] = 1`. A point transforms as
`x' = x*m[0] + y*m[4] + z*m[8] + m[12]` (and likewise for y', z') - `Skin::getBlendedVertices`' own arithmetic.
Read as column-major with column vectors this is also a GLSL `mat4`.

- `BaseMatrix4::mult(R, A, B)` (0x08062440) = apply A, then B. `SkeletalMath.Mul(B, A)` gives its bytes.
- `Skeleton::transform` (0x083420f0): `world = mult(local, parentWorld)`; a root takes an optional matrix passed in.
- `BaseQuaternion::toMat` (0x08226180) = `SkeletalMath.FromQuatTrans`, byte for byte, including `s = 2/|q|²`: any
  non-zero length of quaternion gives the same rotation.

## `.ske` — Skeleton (rest pose) — `dice::anim::Skeleton` (0x08340800)

```
u32 version            (1 in every retail file; the engine also reads the quaternion form below)
u32 boneCount
per bone:
    u16  nameLen        (includes the trailing NUL)
    char name[nameLen]  (the engine lowercases it)
    u16  parentIndex    (0xFFFF = root; otherwise a prior bone - parent before child)
    version 1: f32 m[12]  three rows [R00 R01 R02 Tx][R10 R11 R12 Ty][R20 R21 R22 Tz];
                          in memory [f0 f4 f8 0 | f1 f5 f9 0 | f2 f6 f10 0 | f3 f7 f11 1]
    otherwise: f32 q[4] (x,y,z,w) + f32 pos[3]   (toMat + translation)
```

## `.baf` — BoneAnimation (clip), version 3 — `dice::anim::BoneAnimation`

```
u32 version            (== 3)
u16 numBones
per bone: u16 nameLen(incl NUL); char name[nameLen]
u32 frameCount         (only the low 16 bits are used; no retail clip sets the others)
u8  S                  (quantization shift)
per bone:
    u16 streamCount    (total int16 in this bone's blob = sum of the 7 channel lengths)
    7 channels in fixed order [qx, qy, qz, qw, tx, ty, tz], each:
        u16    L
        int16  data[L]
```

Each channel is a **run-length keyframe stream**. A header is one `int16`: bits 0..6 = frames covered, bit 7 set =
constant run (one value follows; next header 2 on), clear = literal run (one value per frame follows; next header
`span + 1` on); the high byte is that stride. Sampling frame `f` (`CompressedAnim::GetValue` 0x0832fd30) walks the
headers to the run covering `f`.

**Quantization.** `GetValue` divides **every** channel by `(1<<S)-1`. DICE's exporter wrote quaternions at 15 bits
(× 32767) whatever S is, and translations × `((1<<S)-1)`, choosing a smaller S when a bone moves more than a metre (86
retail clips have S 9..14). The engine reads those quaternions 2^(15-S) too long and gets the right rotation anyway,
because `toMat` divides by |q|². `BoneAnimation` reads rotations at 15 bits, so every rotation of every retail frame is
a unit quaternion (to 5e-4), and writes the way DICE did (`Create`, `ChooseQuantBits`).

**Runs.** DICE's encoder never wrote a constant run under 6 or over 126 frames, nor a literal run over 125. It split
runs on the float values before quantizing, so two constant runs can carry the same int16 and short equal stretches
sit inside literals: re-encoding retail frames reproduces 98.2 % of channels byte for byte in both games. A retail
clip therefore round-trips by keeping its streams; `EncodeChannel` (maximal runs, long ones split so every piece is at
least 6) never makes a retail channel more than 2 int16 longer.

Playback: `applyOnSkeleton` takes normalized clip time (0..1) and slerps between the two bracketing frames; bones a
clip does not animate keep their `.ske` rest local matrix.

## `.skn` — Skin, version 1 — `dice::anim::Skin` (0x08342ee0)

```
u32 version            (1; the loader also accepts 2)
u32 vertexCount
per vertex:
    f32 pos[3]                         (matched against the mesh - see Binding)
    u8  influenceCount                 (1..6 in retail)
    per influence (18 bytes):
        u16 localBoneIdx               (index into the bone-name table below)
        f32 weight
        f32 bindPos[3]                 (the vertex in that bone's world frame at bind time)
u16 boneNameCount
per name: u16 len(incl NUL); char name[len]
```

**Influences kept.** Of a vertex's stored influences the constructor keeps those of weight **0.01** or more, heaviest
first (ties in file order), accepting at most four; when none is that heavy, the first four share 1/n equally. Then it
keeps **two**, with `w0 = 1 - w1` (`Skin.EngineInfluences`). The hardware vertex (`VertexShader2BoneVertex`, 64 bytes)
holds two bind positions and bone indices `3 + 3*palette` (shader constants; 28 bones a block at most).

**Skinning.** `pos = Σ w_i · (BoneWorld_i · bindPos_i)` (`Skin::getBlendedVertices`; BoneWorld at `Bone+0x48`, bones
232 bytes apart). There is no inverse-bind matrix: the drawn shape is the bind positions carried by the animated bones.
`Skin.Build` makes bind positions `BoneWorld_rest⁻¹ · pos`, so a new skin draws its mesh exactly where it is at rest.

## Binding a skin to its mesh

The `.skn` has **no triangles, UVs or materials** - the mesh is the companion `.sm`, loaded by `AnimatedMeshTemplate`.
`fillHwSkinnedBlock` (0x0837df00) matches **every `.sm` vertex, of every LOD and material**, to a skin vertex by
position: the skin's positions go into 4096 buckets by `((int)(x*10) + (int)(y*10) + (int)(z*10)) & 0xFFF`
(truncated), and `VertexHashTable::findIndex` (0x08381140) looks in the vertex's bucket, the one below, then the one
above, taking the first skin vertex closer than **1e-4 m** (`SkinBinding`). A mesh vertex that finds none is drawn
**rigid** - weight one on palette slot 0, the object's own matrix, where the `.sm` has it.

So a skin is welded (one vertex per position; a mesh's UV and normal seams share it), and it only has to cover the
vertices that should move. Retail: soldiers and flags are bound whole (GerBody 740/740 mesh vertices from 534 skin
vertices, Ger2Face 414/414, flag 20/20); DICE's track skins are partial (Sherman 120/289, Tiger 26/94, the M110's front
wrap rigid) and some bind nothing at all (Chi-Ha, T95, LVT4) - those treads ride rigid.
