# BF1942 / BFV skeletal animation formats (`.ske` / `.baf` / `.skn`)

Clean-room notes for the Refractor skeletal-animation files shipped in `animations.rfa`. Recovered from the
unstripped Linux dedicated-server binary (`bf1942_lnxded`, namespace `dice::anim`) and verified byte-exact by
re-parsing every relevant sample. C# parsers live in `src/RefractorForge.Formats/Animation/`; the regression
gate is `dotnet run --project src/RefractorForge.Demo -c Release -- skeletal <animations.rfa>`
("SKELETAL TESTS PASSED").

A stock `animations.rfa` holds ~1154 `.baf` (clips), 64 `.ske` (skeletons), 88 `.skn` (skinned meshes) and
some `.con`. All formats are **little-endian**; floats are IEEE-754 32-bit. Each engine constructor loads the
whole file into a buffer and walks it with a cursor (`memcpy(dst, cursor, N); cursor += N`), i.e. a plain
`readBytes(N)`. Strings are `u16 length` (INCLUDING the trailing NUL) followed by that many NUL-terminated bytes.

## `.ske` — Skeleton (rest/bind pose) — `dice::anim::Skeleton`

```
u32 version            (== 1)
u32 boneCount
per bone:
    u16  nameLen        (includes the trailing NUL)
    char name[nameLen]  (NUL-terminated ASCII; engine lowercases + trims trailing spaces)
    u16  parentIndex    (0xFFFF = root; otherwise a prior bone ordinal — parent-before-child)
    f32  m[12]          (local-to-parent bind matrix)
```

The 12 floats store the rotation matrix **rows** `(f0,f1,f2) (f4,f5,f6) (f8,f9,f10)` with the translation
interleaved as `(f3, f7, f11)`. To a column-major `float[16]` (element(row,col) = `M[col*4+row]`, translation
in `M[12..14]`) the rotation must be **transposed**: column `c = (f[c], f[c+4], f[c+8])`.

> Both the row and column interpretations are orthonormal, so orthonormality cannot tell them apart — only the
> resulting geometry can. The soldier skeleton is a 3ds-Max **Biped, authored Z-up** (UP = −Z): the transposed
> mapping composes `UsSoldier.ske` into a correct standing humanoid (head Z≈−1.61, foot Z≈−0.12, height 1.72 m),
> the raw mapping scrambles it.

World matrices are composed parent-before-child: `world = parentWorld * local`.

## `.baf` — BoneAnimation (clip), version 3, **quantized** — `dice::anim::BoneAnimation`

```
u32 version            (== 3)
u16 numBones
per bone: u16 nameLen(incl NUL); char name[nameLen]
u32 frameCount         (only the low 16 bits are used)
u8  S                  (quantization shift; divisor = (1<<S)-1, = 32767 for the universal S=15)
per bone:
    u16 streamCount    (total int16 in this bone's blob = sum of the 7 channel lengths)
    7 channels in fixed order [qx, qy, qz, qw, tx, ty, tz], each:
        u16    L
        int16  data[L]
```

Each channel is a **run-length keyframe stream**. A keyframe header is one `int16`: its low byte is a control
byte (`span = ctrl & 0x7F` = frames covered; high bit set = constant run, clear = per-frame run), its high byte
is the `int16` stride to the next header. The sampled value sits one `int16` after the header. Sampling frame
`f` (`CompressedAnim::GetValue`):

```
idx = 0; rem = f
hdr = blob[base+idx]; span = hdr & 0x7F
while rem > span-1: rem -= span; idx += (hdr>>8)&0xFF; hdr = blob[base+idx]; span = hdr & 0x7F
valueIdx = (hdr & 0x80) ? idx : idx + rem
value    = blob[base + valueIdx + 1] / ((1<<S)-1)        # dequantize: i16 / 32767
```

Channels 0–3 are a quaternion `(x,y,z,w)`, channels 4–6 a translation `(x,y,z)` in metres. Dequantized
quaternions are unit-length (verified to ≤1e-4 across all bones/frames, including a 166-frame clip). At runtime
`applyOnSkeleton` samples the two bracketing keyframes, **slerps** the rotation and **lerps** the translation,
builds the bone's local matrix, and re-composes the world matrices. Bones a clip does not animate keep their
`.ske` rest local matrix.

## `.skn` — Skin (skinned mesh weights), version 1 — `dice::anim::Skin`

```
u32 version            (== 1; the loader also accepts 2)
u32 vertexCount
per vertex:
    f32 pos[3]                         (bind-pose position, model space)
    u8  influenceCount                 (N; 1..4 in stock data — the runtime trims to 3 in memory)
    per influence (18 bytes):
        u16 localBoneIdx               (index into the bone-name table below)
        f32 weight                     (may be 0.0; per-vertex weights sum to 1.0)
        f32 bindPosLocal[3]            (the vertex position in that bone's local space)
u16 boneNameCount
per name: u16 len(incl NUL); char name[len]
```

The `.skn` carries **no triangle list, UVs, or materials** — topology comes from the companion `.sm` geometry
(the `.skn` vertex order lines up with it). `localBoneIdx` indexes the skin's local `boneNames[]`; resolve each
name (case-insensitive) to a skeleton bone to skin: `worldPos = Σ weight · boneWorld · bindPosLocal`.

## Runtime matrix conventions

Matrices are **column-major** `float[16]` with column-vector convention (`world = parentWorld * local`), which
is also exactly what a GLSL `mat4` expects, so a matrix uploads to a shader verbatim. See
`SkeletalMath` (`Mul(B,A) = B*A`, reproducing `BaseMatrix4::mult`; `FromQuatTrans` with `s = 2/(x²+y²+z²+w²)`,
reproducing `BaseQuaternion::toMat`). Quaternion component order is `(x, y, z, w)`.

> **Convention note:** `Skeleton.Load` transposes the on-disk `.ske` rotation into this column-major basis (so
> the soldier composes into a correct Z-up humanoid), and `SkeletalMath.FromQuatTrans` therefore *also* stores
> the transpose of the engine's `toMat`. Both must share the same basis or layered clip bones invert/float.