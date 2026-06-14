# RFA Archive Format — Reverse-Engineering Notes

Reverse-engineered from the real Battlefield Vietnam archives (`objects.rfa`, 3808 files) with no
reference implementation available. The **container** is fully decoded and validated against every
file; the **payload compression** is ~85% decoded (everything except one short-match opcode class).

All multi-byte integers are little-endian.

## 1. Container (FULLY DECODED — validated on all 3808 files)

```
offset 0x00 : u32  tocOffset        (e.g. 0x0017CA4B for objects.rfa)
offset 0x04 : u32  version = 1
... contiguous data blocks ...
@ tocOffset : u32  fileCount        (3808)
              repeat fileCount times:
                u32   nameLen
                char  name[nameLen]          (e.g. "objects/.../Geometries.con")
                u32   blockSize              (total size of the data block, incl. its 16B header)
                u32   uncompressedSize
                u32   offset                 (start of this file's data block in the archive)
                u32   reserved = 0
                u32   reserved = 0
                u32   constant = 0x0229EEF8  (same for every entry; good sanity check)
```

Each file's **data block** at `offset`:

```
u32 flag            (1 for 3806/3808 files; one file =3, one =6 — special, rare)
u32 compressedSize
u32 uncompressedSize
u32 = 0
byte payload[compressedSize]
```

`blockSize == 16 + compressedSize`, and blocks are stored contiguously in `offset` order.
Implemented and validated in `RefractorForge.Formats/Rfa/RfaArchive.cs` (listing + raw extraction
work for 100% of files).

## 2. Payload compression — a custom byte-aligned LZ77 (literals stored verbatim)

Not zlib/deflate (literals appear as plain ASCII in the stream), not stock FastLZ, not RefPack/QFS
(a 27-byte literal run and the first-byte command both violate those formats).

### Solved opcodes

| Element | Encoding | Status |
|---|---|---|
| **Literal run** | opcode `b` (≥0x12) → emit `b − 0x11` literal bytes that follow | ✅ confirmed (8/8 pure-literal files + every leading run) |
| **End marker** | `11 00 00` | ✅ confirmed |
| **Long match** | `b0 b1 b2`: `len = b0 − 0x1E`; `dist = ((b2<<6) | (b1>>2)) + 1` | ✅ confirmed across dozens of samples (len 12–29, dist 33–72) |
| **Trailing literals** (run immediately after a match) | byte `t` → emit `t + 0x12` literals | ✅ confirmed (clean ladder 0x07→25 … 0x1F→49) |

Worked example — `objects/.../O_10_c99_m1/Geometries.con` (66→85 bytes), decompresses to:
```
GeometryTemplate.create StandardMesh O_10_c99_m1\r\n
GeometryTemplate.file O_10_c99_m1\r\n
```
Byte layout: `25`·[20 lit "GeometryTemplate.cre"]·`58 00 00`(SHORT match → "ate")·[trailing-lit op]·
[" StandardMesh "+name+CRLF]·`2f c0 00`(long match → "GeometryTemplate.", len17 d49)·[lit "file"]·
`2x xx 00`(long match → " "+name+CRLF)·`11 00 00`(end).

### Remaining unknown — the short-match opcode

A short copy (e.g. `58 00 00` → length 3, distance 7, used for the "ate" in "create") does **not**
fit the long-match formula (`0x58 − 0x1E = 58 ≠ 3`). It is a distinct opcode class whose bit layout
isn't yet pinned down, because only one short-match instance (len3/dist7) recurs in the sampled
files. Likewise the small 4-byte "file" trailing run (`0x01`) doesn't fit `t + 0x12`, suggesting the
short class also carries small literal counts.

**To finish:** harvest more short-match samples (varied len/dist) by decoding files where the long
+ literal rules leave a gap, then solve the 1–2 remaining bitfields. The `TryDecodeLz` decoder in
`RfaArchive.cs` already implements every confirmed rule and deliberately returns `false` on the
short-match opcode rather than guessing — so finishing the codec is a localized change.

## 3. What works today in code

- `RfaArchive.Open(path)` → enumerate every entry (name, sizes, offset). 100% reliable.
- `ReadRawBlock(entry)` → raw compressed payload. 100% reliable.
- `TryDecompress(entry, out bytes)` → decodes the fully-understood (literal) streams, reports
  `false` on streams using the unfinalized short-match opcode (no silent corruption).

Once the short-match opcode is solved, the same reader unlocks `standardMesh.rfa`, whose `.sm`
files contain the real object geometry — the input to replacing the editor's proxy boxes with
actual meshes.

---

## 4. Update — codec re-derivation (session of 2026-05-28)

A backtracking parser run against 297 `Geometries.con` streams (whose output is deterministic from
the mesh name, giving a perfect oracle) pinned down the match opcodes and corrected the
literal-count rule. Confirmed:

| Opcode | Bytes | Length | Distance | Trailing literals ("run") |
|---|---|---|---|---|
| **Long match (LM)** | `b0 b1 b2`, `0x1F≤b0≤0x3F` | `b0 − 0x1E` | `((b2<<6) \| (b1>>2)) + 1` | `b1 & 3` |
| **Short match T1 (SM2)** | `b0 b1`, `0x40≤b0≤0xFF` | `(b0>>5) + 1` | `((b1<<3) \| ((b0&0x1F)>>2)) + 1` | `b0 & 3` |
| **Short match T2 (BM2)** | `b0 b1`, `0x01≤b0≤0x1E` | `2` (fixed) | `4*b1 + (b0>>2) + 1` | `b0 & 3` |
| **End** | `0x11 0x00 0x00` | — | — | — |

Each match carries a 0–3 literal "run" in its low 2 bits (RefPack-like): that many raw bytes are
emitted immediately after the match. This explains the `GeometryTemplate.` + `f` + match-`il` + `e`
composite that earlier looked like bare single-byte literals.

**Literal-run count corrected:** mid-stream literal-run opcode count = **`byte + 3`**
(`0x08→11, 0x09→12, 0x0a→13, 0x0d→16, 0x0e→17, 0x01→4`), NOT the `byte + 0x12` the first pass
assumed. The initial run is still `byte − 0x11` (`0x25→20`, `0x16→5`) — a separate regime.

**Remaining unknown (the last ~20%):** the *disambiguation state machine* for the low byte range
`0x01–0x1E`, which is overloaded between a short literal-run, a Type-2 match, and a possible
extended long-match (`len = b0 + 33`, seen as `03 e0 00` → len 36). A deterministic decoder needs
the exact rule the encoder uses to pick among these per position. Tractable; needs a few more
oracle iterations. Container + all match formulas + run field + literal counts are solid and
sufficient to decode the majority of streams.

---

## 5. Decoder state machine + encoder disassembly (session 2026-05-28, cont.)

### Decoder, derived from the oracle (single-mesh `Geometries.con`: **280/297 byte-exact**)

```
INIT (opos 0, always a literal run):
    i0 = next byte
    if i0 == 0x00:  count = next_byte + 0x12      (long form)
    else:           count = i0 - 0x11
    emit `count` raw literals

loop until len(out) == uncLen:
    b0 = next byte
    0x20            -> EXTENDED long match (3 B): len=b1+33, dist=(b2>>2)+1, run=b2&3
    0x21..0x3F      -> long match (3 B):          len=b0-0x1E, dist=((b2<<6)|(b1>>2))+1, run=b1&3
    0x40..0xFF      -> short match  (2 B):         len=(b0>>5)+1, dist=((b1<<3)|((b0&0x1F)>>2))+1, run=b0&3
    0x00..0x1E      -> len-2 match OR literal run (see disambiguation)
    after every match: emit `run` (0..3) trailing literals
```

Disambiguation for `b0` in `0x00..0x1E` (the unresolved core):
- `b0 != 0`: if `b1 < 0x20` it's a len-2 match (`dist = 4*b1 + (b0>>2) + 1`); else a short
  literal run (`count = b0 + 3`, first literal = `b1`). Works for text; **fails on binary**.
- `b0 == 0`: long literal run (`count = b1 + 0x12`) unless that run can't fit the remaining
  compressed bytes, in which case it's a len-2 match. (`00 09` is a run in O_10 but a match in
  TemplePiece.)

### Encoder found in `bcv.exe` (VA 0x444560–0x4486xx) — confirms the model

`bcv.exe`'s LZ **compressor** (writes to `esi`) was located and disassembled. It confirms, beyond
doubt, the parts of the model that don't depend on disambiguation:
- **Literal runs:** count 0–3 → `or [esi-2], al` (the run is OR'd into the *previous* opcode's low
  2 bits — confirms the run field); count 4–18 → one byte `count-3` (confirms short form
  `count = byte+3`, opcodes `0x01..0x0F`); count >18 → `0x00` marker then `count-0x12` (confirms the
  long form), with a multi-marker extension for very long runs.
- **Matches:** length band `len<=8` emits a 2-byte short match with `len-1` in the top 3 bits;
  `len 3..33` emits `(len-2)|0x20` → opcodes `0x21..0x3F` (confirms `len = b0-0x1E`); `len>33`
  emits a `0x20` marker + extended length (confirms the extended-match escape); far distances set
  the `0x10` bit.

**Caveat that matters:** `bcv.exe`'s built-in codec stores the distance bytes in the opposite order
from `objects.rfa`, and encodes len-2 matches as 2-byte short matches rather than in the
`0x00..0x1E` low range. `objects.rfa` clearly does use the low range for len-2 matches. So the
shipped game archives were produced by a *different* codec variant than Battlecraft's own, and
`bcv.exe`'s routine cannot be lifted verbatim — though it validates the literal/run/length rules.

### Status & honest gap
Container 100%; single-mesh StandardMesh `.con` 280/297 exact. Not solved: (a) an exact,
lookahead-free rule for the `0x00..0x1E` match-vs-literal split that holds for **binary** `.sm`
mesh data (current rule is text-only); (b) richer multi-mesh/material `.con`, which a full
backtracking decoder still cannot recover — implying at least one more opcode/structural element
beyond the table above. Closing these needs the *game's* decompressor (not Battlecraft's) or
substantially more oracle-guided work.
