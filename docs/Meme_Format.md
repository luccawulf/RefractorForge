# Refractor "Meme 2.0" menu files (BF1942 and BF Vietnam)

The files inside `menu.rfa` are Meme 2.0 object trees: the whole menu and HUD system of both games (screens, buttons,
HUD gauges, the variables that drive them, the actions buttons run). This is the format spec. The reference
reader and writer is `meme.py`, and the class table it reads is `meme_schema.json`, both in `tools/meme/` of this
repository. The C# library the tools use, `RefractorForge.Formats.Menu`, is a faithful port of `meme.py` (section 13).

Everything below was measured with `meme.py` on the whole corpus, or read from the two executables
(`BF1942.exe` from the Clean install and `BfVietnam.exe` from Original Files, both ImageBase 0x00400000, only ever read).
Each statement is marked:

- **[data]**: holds on every instance in the corpus.
- **[engine]**: read from the disassembly.
- **[engine+data]**: both.
- **[inferred]**: neither proves it outright.

No game or GL app was launched.

The research ran as independent **angles** - two engine tracers (`engine-bf1942`, `engine-bfv`), a static extractor of
the serialize routines and DICE's MDT header (`mdt-defs`), and a data-only solver (`empirical`) - which a **merge**
reconciled into this spec; three **verification lenses** (plausibility, engine, writer) then re-checked it. The words
angle, lens and merge below refer to those passes; their working scripts stay with the research notes and are not part
of the repository.

## 1. Status in one paragraph

Every retail menu file of both games (94 BF1942, 92 BFV) parses with every byte accounted for and is written back
byte-exact. Every community file is written back byte-exact too: 1,214 BF1942 files from 91 menu archives of 89 mods,
and 291 BFV files from 8 archives of 8 mods. These were all the menu archives of both main installs: 162 + 12
readable, 1 not an archive. All but two community files are also fully decoded, and both exceptions are damaged data,
not open layouts:

- one hand-corrupted record (21 bytes)
- 78 bytes that the engine itself never reads

That is 1,462,445 records in total. Of the class layouts, 101 are proven by real data, meaning every instance consumes
exactly its payload and its values are plausible. The other schema entries (97 of 194 for BF1942, 98 of 199 for BFV)
come from the engines only, because no file uses them. Four independent sources agree on every layout they share
(and a fifth, the verify-engine lens's own tracer, reproduces all 194 + 199 entries: tokens, types, labels, slots):

- two static tracers of `serialize()`, in two separate angles
- a third tracer run on both exes
- a data-only solver

DICE's own MDT header (`bfMenuObjDefs.h`, 119 structs) has three wrong layouts (PointerXData, PointerYData,
BfLocalePictureButtonNode) and declares one class that neither exe registers, BfScrollPictureNode2 (section 11).
The other 115 structs agree with the engine token for token.

## 2. Where the files live

- The files are entries of `Mods/<mod>/Archives/menu.rfa` under `menu/`, and they have no extension. Examples:
  `menu/InGame` (the HUD), `menu/MainMenu`, `menu/ButtonNavigationLayer`.
- The entry name is the object name of the file's root object. A by-name reference to that name loads the file
  (section 6). [engine]
- Magic: byte 0 is 0x0C, and bytes 1..12 are `MemeFile 2.0`.
- A mod's `menu.rfa` overlays the base game per file. [inferred from the loader and file names; the load order across
  archives was not traced]

## 3. Framing

```
file      := string_table  u16 root_class_index  root_payload
string_table := { u8 len (1..255) ; len bytes latin1 }*  0x00
record    := u32 size ; u16 name_index ; u16 class_index ; payload[size - 8]
```

**String table** [engine+data]

- `strs[0]` is the magic `MemeFile 2.0`.
- A u16 index `i` in the body names `strs[i]`. Because `strs[0]` is the magic, the first real string is index 1, and
  index 0 means none / empty. ClassIStream::readString is BFV 0x0087A290.
- Limit: the engine's ClassIStream holds `table[512]` with no bounds check in the fill loop (BFV 0x0087A1E7). So a file
  must keep **at most 511 strings after the magic**. The largest file in the whole corpus has 465 (BFV `InGame`).
  `meme.py` refuses to write more.

**Root** [engine+data]

- The root is **not** a record. It is a u16 class index, followed straight away by that class's payload, which runs
  exactly to EOF.
- There is no size, no name and no terminator.
- Roots in the corpus: `NameNode` (1,371 files) or `PathNode` (322 files).

**Record** (every nested object) [engine+data]

- `size` counts from the size field itself, so the next record starts at `record_start + size`.
- `class_index == 0` means there is no object:
  - `name_index == 0`: **null pointer**.
  - `name_index != 0`: **by-name reference** (section 6).
  - Both are 8 bytes in every retail file.
- The reader copies the object name into a 0x80-byte buffer and the class name into a 0x40-byte buffer (BFV
  0x00874F6D / 0x00874F8B, BF1942 0x007ED54D / 0x007ED56B). Keep names under 128 bytes and class names under 64.
  The corpus maxima are 67 and 42. [engine]
- Class names are stored in full, e.g. `dice::meme::BfButtonNode`. The only other namespace is
  `dice::ref2::menu::Ref2System` (registered by both exes), which never appears in a file.
- The class lookup (BF1942 0x007EDEA0, BFV 0x008753F0) compares the full name first, then the part after the last
  `:` (the ClassInfo keeps that offset: `strrchr(name, ':') + 1`, BF1942 0x007EDF60). So a bare `BfButtonNode` also
  resolves, but a foreign prefix such as `foo::BfButtonNode` does not. No file uses either form. [engine] `meme.py`
  follows the same rule (`engine_class_name`, section 8) and keeps a record with an unresolvable class string opaque; an
  earlier version keyed classes by the part after the last `::` and accepted the foreign prefix.

**The size field is authoritative** [engine]

- The record reader (BFV 0x00874F10, BF1942 0x007ED4F0) reads the header and then the payload. Afterwards it
  compares the stream position with `start + size`. On a mismatch it logs "reading differs from saved file" and seeks
  to `start + size`.
- An unknown class is logged, gives a null pointer, and the record is skipped.
- A classless record longer than 8 bytes therefore hides dead bytes. One community file has exactly that (section 12).

## 4. Payload tokens

Properties carry no names, tags or type codes. Each class's `serialize()` reads a fixed positional sequence through
virtual `IStream` slots, so the token list **is** the class layout.

| token | bytes on disk | IStream slot (both exes) | used by |
|---|---|---|---|
| `O` | a nested record (section 3) | +0x60..+0x88 = typed reads, see below | almost every class |
| `LIST` | records until a null record; the null record is written and ends the list | +0x58 | ActionListAction, DataListData (+ConcatenateListData, NodeListNode: engine-only) |
| `f32` | 4, IEEE little-endian | +0x34 | 151 fields |
| `i32` | 4, little-endian, signed | +0x3C "Int", and +0x5C enum (a binary stream stores the value) | IntData, event types, sizes... |
| `u8` | 1 | +0x1C / +0x20 | event "Input index" |
| `bool` | 1, 0/1 | +0x38 | 23 fields |
| `s8` | u8 length + latin1 bytes, no NUL | +0x4C texture, +0x50 font, +0x54 sound handle | pictures, fonts, sounds |
| `str32` | u32 length + latin1 bytes | +0x40 | StringData (CR/LF allowed) |
| `wstr32` | u32 length **in characters** + 2*length bytes UTF-16LE | +0x44 | WstringData |
| `TREE` | recursive, bool-prefixed (section 5.3) | own reader | Tree, NavigateTree only |
| (`u16`, `u32as16`, `sidx`) | 2 / 4 / 2 | +0x24 / +0x48 / +0xA0 | no class; listed for completeness |

The typed `O` reads say which base class a slot wants:

| slot | wants |
|---|---|
| +0x64 | Event |
| +0x68 | Action |
| +0x6C | Data |
| +0x70 | Effect |
| +0x74 | Function |
| +0x78 / +0x84 / +0x88 | Node |
| +0x7C | Style |
| +0x80 | Tree |
| +0x60 | any Object |

The loader does **not** enforce the slot type. The record reader only checks isKindOf(Object) (BFV 0x008752D0), and the
typed readers drop the class argument (0x00879280). [engine]

In the data, the slot type is respected by all of 1.46 M records except one: the MDT `Default` template, which puts a
StringData into a TransformNode's Next node. [data] `meme.py` reports such cases as `TYPE` warnings and never fails on
them.

Signedness: 4-byte ints are signed. IntData holds -1 thousands of times, and also -705. The `+0x2C/+0x30/+0x34` readers
share one routine, so the byte count is certain and the signedness is semantic.

## 5. Object model

### 5.1 Classes

- BF1942.exe registers **190** file classes, plus `System`, `Tree` and `NavigateTree`.
- BfVietnam.exe registers **195**. These are the same 190 with identical payloads [engine: all three tracers], plus 5
  BFV-only classes:
  - `BfControlPointGauge`
  - `BfLocalePictureButtonNode`
  - `BfNewCreditsListNode`
  - `BfPictureButtonNode`
  - `BfTriangleButtonsNode`
- On top of those, **both** exes register `System`, `Ref2System` (`dice::ref2::menu::Ref2System`), `Tree` and
  `NavigateTree`, so the schema has 194 BF1942 and 199 BFV entries. BF1942's Ref2System: ClassInfo 0x009BFE30, vtable
  0x00914A08, serialize 0x007F8570 (empty). Both also register the stream classes `IStream`, `OStream` and
  `ClassIStream`, which are not file objects. [engine; verify-engine lens]
- Base classes come from BFV RTTI; BF1942 has no RTTI and is assumed identical for the shared classes. The main
  families are Node (drawn things and containers), Data (variables and values), Action, Event, Effect, Style and Function.
- The serialize slot differs by exe:
  - BFV: node vtable +0x34, and the load wrapper at +0x38 defaults to 0x00874B30, which calls +0x34.
  - BF1942: node vtable +0x30, and +0x34 is the thunk 0x007ED120.
  - Some classes override the load slot with post-load runtime work, and none of those overrides reads extra bytes.
    [engine]

### 5.2 Names are identity: variables, shared objects, first definition wins

- A **named** record is registered in the menu system's global registry under its name. [engine]
- Data objects are named with their **variable path**, e.g. `Ammo/PrimaryAmmo`, `Locale/Valid`, `Game/ShowScoreBoard`.
  Every Data record with the same name is the same variable.
- The record reader looks the name up first (findObject: BFV 0x00868A70, BF1942 0x007EB740). If the name is already
  registered, **the existing object is used and this record's payload is skipped**. This holds within a file and across
  files, because the registry is global. [engine, both exes; re-read by the merge at BFV 0x00874FBA and BF1942 0x007ED59A]
- DICE's writer inlines a full copy of a shared object every time it is referenced:
  - Retail has 21,735 (BF1942) and 22,480 (BFV) repeat definitions, and **all are byte-identical copies**. [data]
  - Community files have 967 repeats whose payload differs from the first copy. In game, only the first copy counts.
- **Editor rule:** treat same-named records as one object. Show and edit the first definition, and propagate an edit to
  every copy, so that a value change sticks whichever copy the engine meets first.

### 5.3 LIST and TREE

- `LIST`: the reader calls readObject until the resolved pointer is null (BFV 0x0087A6F0, BF1942 0x007F9130; the list
  member's own +0x50, BFV 0x00AC4130 / BF1942 0x007F5800, calls that loop and then resets its cursor). [engine]
  - A by-name reference that resolves is therefore a list item, not a terminator. No corpus list contains one (0 of
    all lists).
  - The pointer is also null, and the list therefore **ends**, for a record of an unregistered class (logged, skipped
    by size) and for a by-name reference that names neither a registered object nor a loadable menu file. The records
    after it would then be read as the parent's next fields. **Editor rule:** never put an unknown class or a dangling
    reference inside a LIST.
  - `meme.py` ends a list only at a real null record, and keeps a non-canonical terminator, if one ever appears. It
    treats an unknown-class record or a dangling reference inside a list as an item; no corpus file has either.
- `TREE` (Tree / NavigateTree only; BFV 0x0087B190 -> 0x0087AEB0 / 0x0087AAD0 / 0x0087AF70; BF1942 0x007F8DD0 ->
  0x007F8AD0) [engine, hand-decoded independently by two angles]. The grammar:
  ```
  TREE0 := u8 flag ; if flag: NODE TREE0(child) TREE1(next) TREE2(prev)
  TREE1 := u8 flag ; if flag: NODE TREE0(child) TREE1(next)
  TREE2 := u8 flag ; if flag: NODE TREE0(child) TREE2(prev)
  ```
  - Tree nodes carry no payload.
  - NavigateTree nodes are `[O Description (Data), O Action]`.
  - No corpus file contains either class. `meme.py` round-trips a synthetic NavigateTree for both games.

### 5.4 Special values

- `BfVerticalScrollExNode."Version <do not edit>"` must be **1**. Any other value fails the read:
  - BF1942 0x007D8532: `cmp ecx,[0x0095AC54]` (the constant is 1).
  - BFV 0x0055A492: `cmp ecx,[0x00CC2524]` (the constant is 1; nothing writes it).
  - [engine]
- What a failed read does depends on the record (both record readers, BF1942 0x007ED4F0 / BFV 0x00874F10):
  - a **named** record: "Error reading from istream" is logged, the half-read object stays registered, and reading
    goes on at start + size;
  - an **unnamed** record: the error propagates to its parent's serialize, and so on up to the nearest named ancestor
    (which is then logged and skipped as above) or, if there is none, the root, and the whole file load fails. [engine]
- Three classes log a deprecation warning when loaded (BF1942 strings 0x00940A50 / 0x00942D30 / 0x009420A8; their
  load slots then read the normal payload): `CullActionNode`, `SetVariableSinusAction` ("should be replaced with
  SetVariableSineAction") and `ShowEffectNode` ("use TransitionalEffectNode instead"). An editor should not offer them
  for new objects. [engine]
- `BoolData."Value <do not edit>"` is 0/1 in all 108,141 instances. [data]
- The enum fields (Appendix B) store the enum **value**. Every value in the corpus is a named one, for example:
  - TypeEvent event types {1, 9, 10, 13, 14, 15, 16, 17} = ButtonPress, PointerIn, PointerOut, PreShow, PostShow,
    PreHide, PostHide, PreWaitShow.
  - ButtonEvent button types {1, 5, 7, 18} = Select, Leftwards, Rightwards, Enter.
  - The name tables come from BfVietnam.exe. The same strings exist in BF1942.exe, but its tables were not traced.

## 6. By-name references and how menus link [engine+data]

- A classless record with a name is resolved with findObject. If the name is not registered, the engine calls
  `System::loadFile(name)` (BFV 0x0087508A -> 0x00874DC0, BF1942 0x007ED3A0). That loads the menu file of that name,
  whose root object is registered under the file name.
- This is how screens chain together: PathNode / SetPathAction targets, layers and navigation menus.
- Every retail reference names a menu file of the same folder: 552 in BF1942 and 489 in BFV, all 552 + 489. The most
  common targets are `SecondNavigationLayer`, `ButtonNavigationLayer`, `MainNavigationLayer` and `MenuLayer`.
- In the community corpus, 7,742 references name a file. One names nothing: `colortry` in Stargate_BFM
  `BattlefieldMenu`, which resolves to null in game.

## 7. What the tree means: the display list [engine; BfVietnam.exe traced by the merge]

**"Next node" is a continuation, not a child.** The first `O` of every Node subclass is its "Next node". The one
exception is `BfGaugeNode`, whose serialize does not chain to Node's and which appears in no file. The draw routines
treat "Next node" as follows:

- `Node::draw` (0x00873DF0) draws the node itself (vtable +0x70), then calls `next->draw` with the **same** rect and
  render state. For ordinary nodes, the next node is therefore a sibling that inherits the parent's rect. A walk that
  treats it as a child degenerates into one long chain; this is the "sibling-not-child trap".
- `TransformNode::draw` (0x00872090) draws `Transformed node` in `(parent.x + X, parent.y + Y, W, H)`, then continues
  its chain in the parent rect. X and Y accumulate; W and H replace.
- `CullNode::draw` (0x00AC0990) draws nothing itself. It reads its runtime visibility (+0x18):
  - at 1.0 or above, it draws its **continuation** unchanged
  - between 0 and 1, it draws the continuation with alpha multiplied by the visibility (presumably the In time /
    Out time fade; the link to those fields was not traced)
  - at 0 or below, it draws nothing
  - **A CullNode hides everything after it in the chain.**
- `EffectNode::draw` (0x008701C0) pushes its Effect onto the render state and then runs `Node::draw`. **The effect
  applies to the rest of the chain.**
- `SplitNode::draw` (0x00873620) draws `Split node` as a separate chain with the same rect and state, then continues.
  This is how a Cull or Effect is scoped to a sub-list.

**Editor rule:** show a chain as siblings, but show a CullNode or EffectNode as enclosing the siblings that follow it.

MDT MenuEditor 1.30 lists Next nodes "after the previous object" for the same reason.

**Coordinates** [engine]

- The UI root rect is {-400, -300, 800, 600}:
  - BFV: System ctor 0x00494AC0 / 0x00869240 (meme_tree.py).
  - BF1942: 0x0045EDE4 -> 0x00603770 -> 0x007EB4F0, and TransformNode::draw 0x007E9240 behaves the same
    (engine-bf1942 angle).
- In other words, the numbers in the files are a **top-left 800x600 canvas**, and `engine = canvas - (400, 300)`.
  Check: the BFV crosshair transform is (390, 290, 20, 20), which is exactly centred.
- `BfTransformNode` takes X and Y from Data objects (animated) and W/H as floats. `BfTransformNodeSize` is the
  reverse.

## 8. The string table order (what a byte-exact writer must do)

- The engine never cares about the order: it only indexes into the table.
- **DICE's writer emits the strings in first-use order of a depth-first write:**
  1. the root class
  2. then, for each record in file order, its object name followed by its class name, each at first use
  - Payload strings are not in the table.
  - [data] All 186 retail files follow this rule exactly, and so do 1,296 of the 1,505 community files. Of the other
    209, three are first-use tables with unused strings at the end, and two cannot be classified because of their
    damaged bytes.
- Third-party tools do not. The MDT's MenuEditor 1.30 readme says it keeps the loaded name list and that "unused names
  are left" (its TODO). The remaining 204 community files carry:
  - an older or other table kept as it was loaded, with new names added in the tool's own creation order
  - often unused strings (up to 64 in one file)
  - some tools pre-seed a fixed list first (e.g. NameNode, EffectNode, ColorEffect)
  - None of these orders can be reconstructed as a single rule.
- `meme.py serialize(mf, table=...)`:
  - `keep` writes the table as read (order, unused entries) and appends any new strings. It is byte-exact for every
    unchanged file (1,693 / 1,693).
  - `rebuild` writes a fresh first-use table. It is byte-exact for every first-use file, and it is what DICE's writer
    would produce after an edit. It is refused if the tree holds opaque bytes whose indices cannot be remapped.
  - `auto` (the default) keeps the table when every needed string is already in it. So an unchanged file, or one whose
    edits only touch values, is byte-exact whatever its order. Otherwise:
    - a file that was first-use ordered gets `rebuild`
    - a tool-ordered file gets `keep` + append (MenuEditor's behaviour)
    - a table over 511 strings is rebuilt if possible, and refused if not.
  - Sizes are always recomputed from content.
  - Tests (`selftest.py`): a value edit changes only its 4 bytes; a rename to a new string gives a valid first-use
    table; an inserted TransformNode adds exactly 32 bytes; every edit re-parses to the same tree; undoing an edit is
    byte-exact again.

**What the writer refuses** (`ValueError`; each would otherwise have been written as a file that means something
else in game, or used to crash / hang the writer). None of them occurs in any corpus file, so the corpus stays
byte-exact. [engine where cited; found by the writer verification lens, which checked the writer's output with an
independent strict byte walker]

- more than 511 strings after the magic. The table fill loop stores one pointer per string plus a NULL terminator into
  `table[512]` at the top of loadFile's 0x81C-byte frame (BFV 0x00874DC0 / 0x0087A1E7, BF1942 0x007ED3A0 / 0x007F7E47,
  identical); a 512th string puts the terminator on the return address.
- an object or reference name of 128+ bytes, a class name of 64+ bytes, **including the root class** (BFV 0x00874C4C
  reads it into 0x40 bytes). ClassIStream::readString (BFV 0x0087A290) copies the table string with an unbounded
  strcpy, so a longer name overflows the stack buffer.
- a NUL inside a name, an s8 handle, a str32 or a wstr32. The engine keeps all of them as C strings (str32 BFV
  0x00878E80 and wstr32 0x00878F20 allocate len+1 and keep only the pointer), so the text would end at the NUL.
- a null record (or None) inside a LIST: the reader ends the list there and every later field would shift.
- a u8 / bool outside 0..255 (it used to be masked with `& 0xFF`) or not an integer (1.7 used to become 1), an i32
  that is not an integer in -2^31..2^32-1, a float that does not fit f32. (i32 2^31..2^32-1 is written as the same 32 bits and reads back negative.) A bool of 2..255 is written as
  is; the engine reads any non-zero byte as 1 (BFV 0x00878E00), and `lint` reports it.
- a record that is its own descendant (a cycle; it used to hang the writer) - `walk()` raises too. A record shared by
  two slots (a DAG) is fine: it is written as two inline copies, as DICE does.
- a hand-built `Record(cls)` with neither fields nor raw payload (it used to be written as a header-only record), and a
  record with fields but no class (it used to become a by-name reference followed by dead bytes).
- `BfVerticalScrollExNode` Version other than 1 (the engine fails the read). `new()` now fills engine constraints, so
  `new(game, 'BfVerticalScrollExNode')` has Version 1 (it had 0).
- `to_old_format()` of a tree with opaque payloads (their nested headers hold table indices).
- a TREE node dict the grammar cannot hold: a `prev` under a next-node or a `next` under a prev-node (section 5.3). It
  used to be dropped silently (1,200 random representable Tree / NavigateTree shapes per run round-trip exactly).
- a decoded object whose class is not registered by the file's game (e.g. a BFV-only `BfPictureButtonNode` pasted into
  a BF1942 file: the engine reads a null pointer and skips it), whose class string the engine cannot resolve, or whose
  field list differs from the schema layout (a hand-dropped or retyped field used to be written and read back as other
  values). Opaque records are still written verbatim. Class strings [engine]: the lookup (BFV 0x008753F0) compares the
  full registered name (`dice::meme::PictureNode`), then, in a second pass, each registered name from the offset after
  its last ':' (class-info ctor BFV 0x008754B9), so a bare `PictureNode` also resolves, but `x::PictureNode` does not.
  Every corpus file spells the full name. `meme.engine_class_name()` implements the rule; the reader now keeps a record
  with an unresolvable class string opaque (it used to decode `x::PictureNode` as a PictureNode), so whatever `parse`
  decodes, `serialize` can write back.

Also accepted now: `None` in an O slot is written as a null pointer (it used to crash `walk()` / `first_use()`), and a
TREE node dict with `flag` 0 is written with flag 1 (0 would have ended the tree there).

**Editor-level checks (`meme.lint(mf)`)** - things the writer cannot refuse because the file is well-formed:
`DUPLICATE_DIFFERS` (a later copy of a named record differs from the first, which is the only one the engine uses:
0 in retail, 967 in the community corpus, the same count as analyze.py's byte comparison), `REF_BEFORE_DEF` (a
by-name reference to a name this file only defines later: the engine then tries to load a menu FILE of that name),
`PATH_ALIAS` (names equal up to / versus \: BFV registerObject 0x00869020 splits a name into folder and leaf at
its last / or \, so `A/B` and `A\B` are one object; how the folder part compares was not traced, so deeper
paths may be over-reported),
`BOOL_NOT_01`, and `DEPTH` (nesting over 1,000 levels; retail and community never exceed 278). The engine reads
nesting recursively on its 1 MB main stack at an estimated ~0x180 bytes per level [inferred: readObject 0x00874F10 has
a 0xCC-byte frame, plus the load / serialize / typed-read frames], and a Next-node chain nests one level per node, so
a very long sibling chain is a stack-overflow risk in game that no file format rule catches.

## 9. Old format (no string table) [engine; no sample exists]

- If the magic check fails, `loadFile` logs "Trying old format" and re-reads the stream with the plain IStream:
  - The root class name is an inline u8-length string instead of a table index.
  - Every record header is `u32 size ; u8-len object name ; u8-len class name`.
- Both exes contain the "Trying old format" string.
- No corpus entry is in this format. The merge's own extraction looked for entries starting with a u8 length and
  `dice::` in every community menu archive and found none.
- `meme.py` reads and writes it. Every corpus file converted to it parses back to the identical tree.

## 10. Per-game differences

- **Layouts:** none for the 190 shared classes. Three tracers on the two exes agree token-for-token, and the 93 classes
  seen in the files of both games decode identically (101 distinct classes are seen overall).
- **Class set:** BFV adds the 5 classes listed in section 5.1. A BF1942 file that uses one of them loads as an unknown
  class in BF1942: logged, null, skipped. `meme.py` keeps such a record as opaque.
- **Labels:** one label text differs. BfListBoxData "Create new action/string" in BF1942 reads "Create dice_new ..." in
  BFV.
- **Serialize vtable slot:** +0x30 in BF1942, +0x34 in BFV.

## 11. Where the sources disagreed, and what decided

| topic | disagreement | decided by |
|---|---|---|
| PointerXData / PointerYData | MDT header: `[O Data]`; engines: empty payload (BFV 0x00A7D100, BF1942 0x007F8570) | data: all 9,329 instances (retail + community, both games) are 8-byte records; the MDT layout breaks 14 retail files (negative control). Engine wins |
| BfLocalePictureButtonNode | MDT: `Byte Unk1, Byte Unk2`; engine: `s8 Picture, s8 Mouse over picture` | engine (slot +0x4C). All 681 BFV instances (154 retail) hold empty strings, identical bytes (0x00) either way |
| BfScrollPictureNode2 | declared in the MDT header only | neither exe registers it and no file uses it: excluded |
| LIST terminator | meme_tree.py: any classless record ends a list; engine: only a null pointer | engine (0x0087A6F0); no corpus list contains a reference, so no parse result changes |
| u32 vs i32 | meme_tree / engine-bf1942 label 4-byte ints `u32` | data (IntData -1, -705): `i32`. Same bytes |
| empirical solve | typed 5 zero-valued fields differently: CullNode In/Out time, BfBinkNode Fade time, BfNewListBoxNode sounds/background colour, BfEditNodeInt Focus, SetPathAction bool | engine types (f32 / s8 / bool). For all but one field the bytes are identical and the data cannot tell them apart because the values are 0. The exception is CullNode *In time*: one community instance (BF1942c `XWWII-menu/menu/InGame`, record at 0x14C95) holds `00 00 FA 44`, which is 2000.0 as f32 and 1,157,234,688 as i32, so the data sides with the engine's f32 there |
| u16as32 width | meme_tree.py's parser reads IStream +0x48 as 2 bytes | engine (BF1942 0x007F5AF0): 4 bytes, stored as u16. No class uses it |
| serialize slot | meme_tree: +0x34 | right for BFV only; BF1942 is +0x30 |
| Read entry | meme_tree used serialize; the reader calls the load slot | same bytes: every override calls serialize and then does runtime work only (engine-bfv) |

## 12. Known unknowns and oddities (honest list)

**Engine-only layouts**

- 97 BF1942 and 98 BFV schema entries appear in no file. These counts include the abstract bases, `System`,
  `Ref2System`, `Tree` and `NavigateTree`. Their layouts come from the disassembly: three tracers agree, and the
  MDT agrees wherever it defines them. Nothing has tested them against data.
- The practically relevant ones are:
  - SoundAction `[s8 Sound (sound handle)]` and VolumedSoundAction `[s8 Sound, f32 Volume (0-1)]`, which close
    meme_tree's gap
  - ModifyVariableAction, SliderNode, EditNode, IfElseEventActionNode, NodeListNode
  - the 11 Navigate*Actions
  - the focus / transition effect nodes
- Four BFV classes have no BFV instance but are proven by BF1942 files with the identical layout: AlphaFadeEffect,
  BfScrollPictureNode, BfSelectButtonNode and VariableEffectNode.

**Unconfirmed engine details**

- **Tree / NavigateTree:** only the hand-decoded grammar and a synthetic round-trip back them.
- **Enum names for BF1942:** taken from BFV. The strings are present in BF1942.exe, but its enum tables were not traced.

**Odd values in the data**

- **BfNewListBoxNode "Frame color" RGB = (107, 380, 36)** in 402 instances; the other 506 are (1, 1, 1). The bytes are
  consistent floats. What the engine does with values above 1 was not traced.
- **Zero-only fields** (measured over every file of every group): CullNode *Out time*, BfBinkNode *Fade time*,
  BfScrollPictureNode *Y offset*, BfNewListBoxNode's 3 sounds (empty), 4 background colour floats and *Frame color
  alpha*, BfLocalePictureButtonNode *Picture* / *Mouse over picture* (empty), BfEditNodeInt *Focus*, SetPathAction
  "Paint outnode over innode", and the events' "Input index" are always 0 (or empty), so the data only proves their
  width. Their type comes from the engine (the serialize read-out, and for u8 / enum the menu IStream: BFV vtable
  0x00BD8374, +0x1C reads 1 byte, +0x5C forwards to +0x3C, 4 bytes). CullNode *In time* is 0 in all but one of 42,312
  instances; that one is 2000.0 as f32 (see section 11), which confirms the engine's f32.

**The two community files not fully decoded**

- `FW1918-menu/menu/testi`, a hand-hex-edited test file. A PictureNode's null child reads `08 00 00 00 00 08 00 00`,
  i.e. name index 0x0800 in a 6-string table, and its `s8` says 8 for the 12-character `menu/aaa.tga`. It is kept as
  one opaque 21-byte record, and the file is still written back byte-exact. The same file's second PictureNode is
  well-formed.
- `wasteland2042-menu/menu/SingleplayerNavigation`. The root's Next node is a **null record of size 86**: a record whose
  class index was zeroed but whose 78-byte payload was left in place. The engine resolves null and seeks past it, so
  those bytes are dead. They are kept verbatim.

**Other oddities**

- `FHE-menu/menu/InGame`: a PictureNode texture string `s3\x01` (modder data).
- `MC1942_FX1/Archives/MENU.rfa` (1,388 bytes) is not an RFA archive at all, so it could not be read.

## 13. Reference implementation

**C# (`src/RefractorForge.Formats/Menu/`, namespace `RefractorForge.Formats.Menu`)** - a port of `meme.py`, checked
against it file by file on the whole corpus (the same counts, warnings, table rules and lint findings for all 1,693
files) and by the tests in `src/RefractorForge.Tests/Meme*Tests.cs` (retail and community read straight from the
installs' archives):

```csharp
var mf = MemeFile.Parse(bytes, MemeGame.Bfv);          // never throws on a bad record (kept opaque)
var t = mf.Walk().First(r => r.Short == "TransformNode");
t["X"] = 400f;                                         // fields by engine label, type-checked
MemeEdits.InsertAfter(t, MemeRecord.New(MemeGame.Bfv, "PictureNode", "", ("Picture", "menu/foo.tga")));
MemeEdits.SetEverywhere(mf, mf.CopiesOf("Game/ShowHud")[0], "Value <do not edit>", 0);   // every same-named copy
byte[] outBytes = mf.ToBytes();                        // MemeTableMode.Auto | Keep | Rebuild; sizes recomputed
string json = MemeJson.ToJson(mf);                     // lossless: MemeJson.FromJson(json).ToBytes() == outBytes
```

Where it differs from `meme.py`: class strings resolve by the engine's own rule (`MemeSchema.Find`: the full registered
name, else a bare name), so `dice::ref2::menu::Ref2System` decodes as Ref2System where `meme.py` keeps it opaque, and
`dice::meme::Ref2System` stays opaque where `meme.py` decodes it (neither occurs in any file); a BfListBoxData whose
field labels are the other game's spelling is written (the bytes are the same); a LIST terminator that is not a null
record and a tree node whose fields differ from its node layout are refused.

**Python (`tools/meme/meme.py`)**:

```python
import meme
mf = meme.parse(data, 'BFV')            # or 'BF1942'; never raises on a bad record (kept opaque)
root = mf.root                            # Record: .cls (full name), .short, .name, .fields, .kind
t = next(r for r in mf.walk() if r.short == 'TransformNode')
t['X'] += 10                              # fields by engine label
t['Next node'] = meme.new('BFV', 'PictureNode', Picture='menu/foo.tga')
out = meme.serialize(mf)                  # table='auto' | 'keep' | 'rebuild'; sizes recomputed
```

- `Record.kind` is one of:
  - `object`
  - `opaque`: unknown class, or the payload does not fit; the raw payload bytes are kept
  - `null`
  - `ref`
- `mf.warnings` lists plausibility notes; they are never fatal:
  - `IMPLAUSIBLE`: bool not 0/1, float not finite or outside 1e-6..1e7, control characters
  - `TYPE`: the child's class does not fit the slot
  - `CLASSLESS_SIZE`
  - `TRAILING`
- `mf.table_rule` describes the table as read.
- `meme.lint(mf)` returns editor-level findings (section 8); `python meme.py lint <file> <game>` prints them.

Command line:

- `python meme.py corpus` runs the table below and writes `corpus_results.json` and `corpus_counts.json`.
- `python meme.py dump <file> <game>` prints a file's tree.
- `python selftest.py` runs the edit tests, the negative controls and the Tree / old-format tests.
- `python make_resource.py` regenerates `src/RefractorForge.Formats/Menu/MemeSchema.json`, the class table the C#
  library embeds, from `meme_schema.json`.
- The corpus commands read an extracted corpus: `MEME_CORPUS` (retail, `BF1942/` and `BFV/`) and `MEME_COMMUNITY`
  (`BF1942c/` and `BFVc/`); see `tools/meme/README.md`.
- Research scripts, not in the repository: `build_schema.py` regenerated `meme_schema.json` from the four angles plus
  the corpus counts, `make_spec.py` the tables below, `analyze.py` the duplicate-payload comparison,
  `extract_community.py` the community extraction.

Corpus sources:

- Retail: every Meme entry of the clean installs' menu archives (BF1942 `bf1942/Archives/menu.rfa` 91,
  `XPack1/Archives/Menu.rfa` 1, `XPack2/Archives/Menu.rfa` 2; BFV `BfVietnam/Archives/menu.rfa` 92; XPack1's
  `menu_001.rfa` holds none).
- Community: `extract_community.py`, a read-only pull of every menu*.rfa of every non-retail mod in both main installs.
  - It was complete where the research's shared extraction was still running (63 mod folders, 773 files, when last
    checked).
  - Every file both extractions hold is byte-identical: 773/773 for BF1942c at the last check, and 291/291 against
    the engine-bfv angle's BFVc.

<!-- BEGIN GENERATED (make_spec.py) -->

### Corpus result (meme.py corpus)

| group | game schema | files | parsed | full byte coverage | byte-exact rewrite (table kept) | byte-exact with a rebuilt first-use table | records | decoded objects | null | by-name refs | opaque records (bytes) | engine-dead bytes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BF1942 | BF1942 | 94 | 94 | 94 | 94 | 94 | 63730 | 55468 | 7710 | 552 | 0 (0) | 0 |
| BFV | BFV | 92 | 92 | 92 | 92 | 92 | 66918 | 57736 | 8693 | 489 | 0 (0) | 0 |
| BF1942c | BF1942 | 1214 | 1214 | 1212 | 1214 | 1011 | 1112806 | 953650 | 152953 | 6202 | 1 (21) | 78 |
| BFVc | BFV | 291 | 291 | 291 | 291 | 285 | 218885 | 187790 | 29554 | 1541 | 0 (0) | 0 |
| MDT | BF1942 | 2 | 2 | 2 | 2 | 1 | 106 | 90 | 16 | 0 | 0 (0) | 0 |

### Class table (both games; payload tokens are identical wherever a class exists in both exes)

Token notation: `O(Node)` nested record whose slot asks for a Node, `s8(texture)` handle string, `i32(enum)` enum value (Appendix B), *italic* = the engine's own debug label.

| class | base | payload, in order | BF1942.exe serialize | BfVietnam.exe serialize | instances BF1942 / BF1942c / MDT | instances BFV / BFVc | MDT header | evidence |
|---|---|---|---|---|---|---|---|---|
| Action | Object | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| ActionFunction | Function | O(Action) *Action* | 0x007E1B60 | 0x0086AD80 | 2 / 190 / 0 | 2 / 12 | agrees | data + engine |
| ActionListAction | Action | LIST *Action list* | 0x007F0AB0 | 0x00AC19D0 | 1185 / 17301 / 1 | 1537 / 4838 | agrees | data + engine |
| ActionNode | Node | O(Node) *Next node* · O(Action) *Action* | 0x007E36B0 | 0x0086BE10 | 13 / 640 / 0 | 16 / 60 | agrees | data + engine |
| AddData | OperatorData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 14 / 346 / 0 | 34 / 168 | agrees | data + engine |
| AlphaFadeEffect | Effect | *(empty)* | 0x007F8570 | 0x00A7D100 | 2 / 164 / 0 | 0 / 0 | agrees | data + engine ; engine + other game's data |
| AndData | LogicalData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 179 / 3677 / 0 | 311 / 1059 | agrees | data + engine |
| AnyKeyEvent | Event | u8 *Input index (0 == all indexes)* · i32(enum) *Event type* | 0x007E18F0 | 0x0086AB20 | 27 / 266 / 0 | 30 / 88 | agrees | data + engine |
| BfAddSubEffectNode | EffectNode | O(Node) *Next node* · O(Data) *Value* · O(Data) *End value* · O(Data) *Start time* · O(Data) *Start percentage* · O(Data) *End time* · O(Data) *Delay* · O(Data) *Up* · O(Data) *Go* | 0x007D54D0 | 0x00556880 | 43 / 610 / 0 | 45 / 145 | agrees | data + engine |
| BfAddSubNextEffectNode | EffectNode | O(Node) *Next node* · O(Data) *Value* · O(Data) *End value* · O(Data) *Start time* · O(Data) *Start percentage* · O(Data) *End time* · O(Data) *Delay* · O(Data) *Up* · O(Action) *Next action* · O(Data) *Go* | 0x007D5A70 | 0x00557170 | 123 / 2212 / 0 | 113 / 376 | agrees | data + engine |
| BfAnimationNode | Node | O(Node) *Next node* · O(Data) *Picture name (without extension)* · i32 *Frames* · bool *Start* · bool *Looping* | 0x007DB1B0 | 0x00560B50 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BfBinkNode | Node | O(Node) *Next node* · O(Data) *Bink filename (without extension)* · O(Data) *Background picture* · f32 *Fade time* · O(Data) *Play* | 0x007DB510 | 0x00561430 | 1 / 30 / 0 | 1 / 3 | agrees | data + engine |
| BfButtonNode | Node | O(Node) *Next node* · s8(texture) *Picture* · s8(texture) *Mouse over picture* · O(Action) *Action* · f32 *Width* · f32 *Height* | 0x007D9B50 | 0x0055CD20 | 161 / 3924 / 0 | 105 / 333 | agrees | data + engine |
| BfCenterStyle | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 26 / 300 / 0 | 43 / 129 | agrees | data + engine |
| BfColorData | Data | f32 *Red* · f32 *Green* · f32 *Blue* · f32 *Alpha* | 0x007DDE20 | 0x00565060 | 4 / 214 / 0 | 6 / 24 | agrees | data + engine |
| BfColorFillNode | EffectNode | O(Node) *Next node* · s8(texture) *Picture* · O(Data) *Data* · O(Data) *Maximum value* · bool *Horizontal align* · bool *Fill order* · O(Data) *Color* · O(Data) *Fill color* | 0x007DD9F0 | 0x00564890 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BfControlPointGauge | Node | O(Node) *Next node* · s8(texture) *Quarter Circle* · O(Data) *Gauge Position* · O(Data) *Owning Team* · O(Data) *Taking Team* | not registered | 0x0055F1C0 | - | 1 / 6 | agrees | data + engine |
| BfCreditsNode | Node | O(Node) *Next node* · O(Data) *Value* · f32 *End value* · f32 *Reset value* · f32 *Time* · O(Data) *Go* | 0x007D8280 | 0x0055A050 | 6 / 120 / 2 | 2 / 10 | agrees | data + engine |
| BfCrosshairNode | Node | O(Node) *Next node* · O(Data) *Radius* · O(Data) *Thickness* · O(Data) *outline thickness* · O(Data) *Deviation* · O(Data) *color* · O(Data) *outline* | 0x007DB870 | 0x00561580 | 2 / 107 / 0 | 3 / 12 | agrees | data + engine |
| BfEditData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 31 / 610 / 0 | 39 / 136 | agrees | data + engine |
| BfEditNode | Node | O(Node) *Next node* · s8(font) *Font* · O(Data) *String* · i32 *Max characters(-1 = no limit)* · O(Action) *Select action* · O(Data) *Editbox data* · bool *Focus* | 0x007DFF10 | 0x00567D20 | 16 / 372 / 0 | 18 / 62 | agrees | data + engine |
| BfEditNodeInt | BfEditNode | O(Node) *Next node* · s8(font) *Font* · O(Data) *Int* · O(Data) *String* · i32 *Min value (-1 = no limit)* · i32 *Max value (-1 = no limit)* · O(Action) *Select action* · O(Data) *Editbox data* · bool *Focus* | 0x007DFFF0 | 0x00567E00 | 22 / 340 / 0 | 32 / 111 | agrees | data + engine |
| BfEditNodeNew | Node | O(Node) *Next node* · s8(font) *Font* · O(Data) *String* · i32 *Max characters(-1 = no limit)* · i32 *Index* · i32 *Max index* · O(Data) *Current index* | 0x007E01D0 | 0x00567F00 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BfFixedSliderNode | SliderNode | O(Node) *Next node* · O(Node) *Cursor node* · O(Data) *Data* · f32 *Minimum value* · f32 *Maximum value* · f32 *Number visible* · f32 *Interval* | 0x007D73A0 | 0x00559110 | 2 / 33 / 0 | 1 / 4 | agrees | data + engine |
| BfGaugeNode | Node | O(Data) *color* · O(Data) *background color* · O(Data) *Data* · O(Data) *Maximum value* · bool *Horizontal align* | 0x007DDFD0 | 0x00565210 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BfLeftOutlineStyle | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 5 / 564 / 0 | 5 / 30 | agrees | data + engine |
| BfListBoxData | ListBoxData | O(Action) *Create new action* · O(Data) *Create new string* · O(Data) *Profile name* | 0x007CF9F0 | 0x00569000 | 26 / 700 / 0 | 41 / 141 | agrees | data + engine |
| BfListBoxNode | Node | O(Node) *Next node* | 0x007EC100 | 0x00873F50 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| BfLocaleData | Data | O(Data) *Valid* | 0x007D8D00 | 0x0055B830 | 1096 / 15782 / 1 | 746 / 2452 | agrees | data + engine |
| BfLocaleNode | Node | O(Node) *Next node* · O(Data) *Locale* | 0x007D9200 | 0x0055BE00 | 3 / 133 / 0 | 3 / 12 | agrees | data + engine |
| BfLocalePictureButtonNode | Node | O(Node) *Next node* · s8(texture) *Picture* · s8(texture) *Mouse over picture* · O(Action) *Action* · f32 *Start X* · f32 *Total Movement* · f32 *Width* · f32 *Height* · O(Data) *Is Selected* · O(Data) *Current X* · O(Data) *Picture Name* · O(Data) *Picture Path* | not registered | 0x0055DFC0 | - | 154 / 527 | WRONG: MDT [O u8 u8 O f32 f32 f32 f32 O O O O] vs engine [O s8 s8 O f32 f32 f32 f32 O O O O] | data + engine |
| BfLocaleStringData | Data | O(Data) *Locale* · O(Data) *Valid* · O(Data) *String Id* | 0x007D90C0 | 0x0055BC30 | 1093 / 15649 / 1 | 743 / 2440 | agrees | data + engine |
| BfMultiplyColorEffect | ColorEffect | O(Data) *Red* · O(Data) *Green* · O(Data) *Blue* · O(Data) *Alpha* | 0x007D5900 | 0x00556EE0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| BfMultiplyColorEffect2 | ColorEffect | O(Data) *Alpha* | 0x007D5A30 | 0x00557130 | 74 / 1298 / 0 | 33 / 106 | agrees | data + engine |
| BfNavigationButtonNode | BfButtonNode | O(Node) *Next node* · s8(texture) *Picture* · s8(texture) *Mouse over picture* · s8(texture) *Clicked picture* · O(Action) *Action* · bool *MouseOver button* · i32 *Index* · O(Data) *Current mouseover index* · O(Data) *Current clicked index* · O(Data) *MouseOver active* · f32 *Width* · f32 *Height* | 0x007D9DB0 | 0x0055CF80 | 246 / 2690 / 0 | 88 / 264 | agrees | data + engine |
| BfNewCreditsListNode | Node | O(Node) *Next node* · O(Style) *Style* · O(Style) *HeadingStyle* · O(Data) *Go* · O(Data) *CurrentPosition* · O(Data) *Step* · O(Data) *StartValue* · O(Data) *EndValue* | not registered | 0x0055A1C0 | - | 1 / 4 | agrees | data + engine |
| BfNewListBoxNode | BfListBoxNode | O(Node) *Next node* · O(Data) *Listbox data* · s8(font) *Font* · O(Action) *Select action* · O(Action) *Focus action* · s8(sound) *Step sound* · s8(sound) *Select sound* · s8(sound) *Failed select sound* · f32 *Row height* · bool *IsSelectable* · bool *Border or not* · f32 *Background color red* · f32 *Background color green* · f32 *Background color blue* · f32 *Background color alpha* · f32 *Scrollbar width* · f32 *Frame color red* · f32 *Frame color green* · f32 *Frame color blue* · f32 *Frame color alpha* · f32 *Select color red* · f32 *Select color green* · f32 *Select color blue* · f32 *Select color alpha* · bool *Show tooltip* · f32 *Scrollbar offset from border* | 0x007CFEB0 | 0x00569390 | 26 / 700 / 0 | 41 / 141 | agrees | data + engine |
| BfOccupiedVehicleData | Data | *(empty)* | 0x007DD510 | 0x00563D50 | 6 / 572 / 0 | 9 / 54 | agrees | data + engine |
| BfOccupiedVehicleNode | Node | O(Node) *Next node* · i32 *Position* · bool *Draw debug pic* · O(Data) *BfOccupiedVehicleData* | 0x007DD680 | 0x00563FE0 | 6 / 572 / 0 | 9 / 54 | agrees | data + engine |
| BfOutlineStyle | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 66 / 1190 / 0 | 101 / 310 | agrees | data + engine |
| BfPictureButtonNode | Node | O(Node) *Next node* · s8(texture) *Picture* · s8(texture) *Mouse over picture* · O(Action) *Action* · f32 *Start X* · f32 *Total Movement* · f32 *Width* · f32 *Height* · O(Data) *Is Selected* · O(Data) *Current X* | not registered | 0x0055DD00 | - | 0 / 0 | - | engine only |
| BfPictureFillNode | EffectNode | O(Node) *Next node* · s8(texture) *Picture* · s8(texture) *Fill picture* · O(Data) *Data* · O(Data) *Maximum value* · bool *Horizontal align* · bool *Fill order* | 0x007DC170 | 0x00561EA0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BfRectangle | Node | O(Node) *Next node* · f32 *Thickness* | 0x007E2DD0 | 0x005706E0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| BfScrollPictureNode | EffectNode | O(Node) *Next node* · s8(texture) *Scroll picture* · O(Data) *Data* · O(Data) *Maximum value* · i32 *Y offset* · i32 *Size* · O(Data) *Var size* · O(Data) *Maintain value* · bool *From bottom* | 0x007DC460 | 0x00562190 | 6 / 570 / 0 | 0 / 0 | agrees | data + engine ; engine + other game's data |
| BfSelectButtonNode | BfButtonNode | O(Node) *Next node* · s8(texture) *Picture* · s8(texture) *Mouse over picture* · s8(texture) *Clicked picture* · O(Action) *Action* · i32 *Index* · O(Data) *Current clicked index* · f32 *Width* · f32 *Height* | 0x007DA1F0 | 0x0055D3C0 | 0 / 80 / 0 | 0 / 0 | agrees | data + engine ; engine + other game's data |
| BfSliderNode | SliderNode | O(Node) *Next node* · O(Node) *Cursor node* · O(Data) *Data* · f32 *Minimum value* · f32 *Maximum value* · f32 *Number visible* | 0x007D7250 | 0x00558FC0 | 30 / 402 / 0 | 37 / 120 | agrees | data + engine |
| BfStyle | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 53 / 877 / 2 | 30 / 118 | agrees | data + engine |
| BfStyle2 | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 13 / 755 / 0 | 8 / 34 | agrees | data + engine |
| BfTextNode | Node | O(Node) *Next node* · O(Data) *String* · O(Style) *Style* · O(Data) *Type* | 0x007D5240 | 0x005566A0 | 71 / 1726 / 0 | 106 / 340 | agrees | data + engine |
| BfTransformNode | TransformNode | O(Node) *Next node* · O(Data) *X* · O(Data) *Y* · f32 *Width* · f32 *Height* · O(Node) *Transformed node* | 0x007DADA0 | 0x005605E0 | 43 / 1334 / 2 | 41 / 198 | agrees | data + engine |
| BfTransformNodeSize | TransformNode | O(Node) *Next node* · f32 *X* · f32 *Y* · O(Data) *Width* · O(Data) *Height* · O(Node) *Transformed node* | 0x007DAF00 | 0x00560880 | 7 / 325 / 0 | 3 / 18 | agrees | data + engine |
| BfTriangleButtonsNode | Node | O(Node) *Next node* · s8(texture) *Top Triangle Selected* · s8(texture) *Bottom Triangle Selected* · O(Action) *Action* · f32 *Width* · f32 *Height* · O(Data) *Selected Team* | not registered | 0x0055ECB0 | - | 4 / 16 | agrees | data + engine |
| BfVariablePictureFillNode | EffectNode | O(Node) *Next node* · s8(texture) *Picture* · s8(texture) *Fill picture* · O(Data) *Data* · O(Data) *Maximum value* · i32 *Size* · bool *Horizontal align* · bool *Fill order* | 0x007DC260 | 0x00561F90 | 35 / 2963 / 0 | 31 / 173 | agrees | data + engine |
| BfVariablePictureFillNode2 | EffectNode | O(Node) *Next node* · O(Data) *Picture* · O(Data) *Fill picture* · O(Data) *Data* · O(Data) *Maximum value* · O(Data) *Size* · bool *Horizontal align* · bool *Fill order* | 0x007DC350 | 0x00562080 | 2 / 184 / 0 | 3 / 18 | agrees | data + engine |
| BfVariablePictureNode | Node | O(Node) *Next node* · O(Data) *picture str* · O(Data) *redraw* | 0x007DA940 | 0x00560060 | 4 / 376 / 0 | 57 / 345 | agrees | data + engine |
| BfVariableTimeoutActionNode | ActionNode | O(Node) *Next node* · O(Action) *Action* · O(Data) *Timeout time* | 0x007DD190 | 0x005639D0 | 3 / 168 / 0 | 3 / 12 | agrees | data + engine |
| BfVariableTimeoutActionNode2 | BfVariableTimeoutActionNode | O(Node) *Next node* · O(Action) *Action* · O(Data) *Current time* · O(Data) *Timeout time* | 0x007DD260 | 0x00563AA0 | 11 / 418 / 0 | 17 / 57 | agrees | data + engine |
| BfVerticalScrollExNode | BfVerticalScrollNode | O(Node) *Next node* · i32(must be 1) *Version <do not edit>* · O(Data) *Text* · O(Style) *Style* · f32 *Scroll speed* · O(Data) *Scroll speed multiplier* · O(Data) *Restart toggle* | 0x007D84F0 | 0x0055A450 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BfVerticalScrollNode | Node | O(Node) *Next node* · O(Data) *Text* · s8(font) *Font* · f32 *Scroll speed* · O(Data) *Scroll speed multiplier* · O(Data) *Restart toggle* | 0x007D8450 | 0x0055A3B0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BlendFuncEffect | Effect | i32(enum) *Source blend func* · i32(enum) *Destination blend func* | 0x007EE470 | 0x00ABEDC0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| BoolData | Data | bool *Value <do not edit>* | 0x007E34C0 | 0x008694B0 | 4823 / 80619 / 15 | 5246 / 17438 | agrees | data + engine |
| ButtonEvent | TypeEvent | u8 *Input index (0 == all indexes)* · i32(enum) *Event type* · i32(enum) *Button type* | 0x007E16A0 | 0x0086A8E0 | 358 / 4647 / 1 | 622 / 1902 | agrees | data + engine |
| CallFunctionAction | Action | O(Function) *Function* | 0x007E43F0 | 0x0086CB50 | 799 / 13770 / 0 | 1039 / 3362 | agrees | data + engine |
| CallFunctionVariableAction | CallFunctionAction | O(Function) *Function* · O(Data) *Result data* | 0x007E4450 | 0x0086CBB0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| CenterAlignedStyle | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 504 / 8843 / 1 | 360 / 1122 | agrees | data + engine |
| ClipEffect | Effect | f32 *X* · f32 *Y* · f32 *Width* · f32 *Height* | 0x007EE080 | 0x00ABE9D0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| ClipNode | Node | O(Node) *Next node* | 0x007EC100 | 0x00873F50 | 7 / 402 / 1 | 11 / 57 | agrees | data + engine |
| ColorEffect | Effect | f32 *Red* · f32 *Green* · f32 *Blue* · f32 *Alpha* | 0x007E5210 | 0x0086DF30 | 1054 / 23567 / 1 | 522 / 2042 | agrees | data + engine |
| ConcatenateListData | Data | LIST *Data list* | 0x007EF5D0 | 0x00AC0080 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| CullActionNode | ActionNode | O(Node) *Next node* · O(Action) *Action* · u8 *Event type (OBS; DO NOT USE!, OLD CLASS)* · i32 *Event data(OBS; DO NOT USE!, OLD CLASS)* | 0x007E3980 | 0x0086C0F0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| CullEventActionNode | ActionNode | O(Node) *Next node* · O(Action) *Action* · O(Event) *Event* | 0x007E3A80 | 0x0086C1E0 | 914 / 12424 / 2 | 1151 / 3598 | agrees | data + engine |
| CullEventTimeoutActionNode | CullEventActionNode | O(Node) *Next node* · O(Action) *Action* · O(Event) *Event* · f32 *Timeout time* | 0x007E3CD0 | 0x0086C430 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| CullNode | Node | O(Node) *Next node* · O(Data) *Data* · f32 *In time* · f32 *Out time* | 0x007F00D0 | 0x00AC0B70 | 916 / 35295 / 7 | 1225 / 4869 | agrees | data + engine |
| CullVariableActionNode | ActionNode | O(Node) *Next node* · O(Action) *Action* · O(Data) *Variable* | 0x007E3B30 | 0x0086C290 | 26 / 552 / 0 | 22 / 76 | agrees | data + engine |
| CullVariableAndEventActionNode | ActionNode | O(Node) *Next node* · O(Action) *Action* · O(Data) *Variable* · O(Event) *Event* | 0x007E3BF0 | 0x0086C350 | 256 / 2891 / 0 | 322 / 979 | agrees | data + engine |
| CullVariableNode | Node | O(Node) *Next node* · O(Data) *Variable* · O(Data) *Data* | 0x007F2530 | 0x0087E6C0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| Data | Object | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| DataListData | Data | LIST *Data list* | 0x007EF5D0 | 0x00AC0080 | 160 / 1568 / 0 | 164 / 476 | agrees | data + engine |
| DisableNode | Node | O(Node) *Next node* · O(Data) *Disable data* | 0x007E2100 | 0x0086B320 | 11 / 139 / 0 | 38 / 120 | agrees | data + engine |
| DivData | OperatorData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 14 / 1197 / 0 | 15 / 72 | agrees | data + engine |
| DragWindowNode | WindowNode | O(Node) *Next node* · f32 *X* · f32 *Y* · f32 *Width* · f32 *Height* · O(Node) *Transformed node* | 0x007E9510 | 0x00872310 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| DynamicIndexPointer | Pointer | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| EditNode | Node | O(Node) *Next node* · s8(font) *Font* · O(Data) *String* · i32 *Max characters(-1 = no limit)* | 0x007E2730 | 0x0086B9A0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| Effect | Object | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| EffectNode | Node | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 1439 / 33396 / 1 | 757 / 2874 | agrees | data + engine |
| EqualData | LogicalData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 526 / 18260 / 2 | 637 / 2478 | agrees | data + engine |
| Event | Object | u8 *Input index (0 == all indexes)* | 0x007F5F50 | 0x008784F0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| ExclusiveOrData | LogicalData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| ExtendedButtonEvent | TypeEvent | u8 *Input index (0 == all indexes)* · i32(enum) *Event type* · i32(enum) *Button type* · i32 *Repeat count* | 0x007E1700 | 0x0086A940 | 54 / 574 / 0 | 53 / 161 | agrees | data + engine |
| ExtendedFocusToPressedEffectNode | FocusToPressedEffectNode | O(Node) *Next node* · O(Effect) *Effect* · f32 *Min value* · f32 *Max value* | 0x007E7DE0 | 0x00870900 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| ExtendedNormalToFocusEffectNode | NormalToFocusEffectNode | O(Node) *Next node* · O(Effect) *Effect* · f32 *Min value* · f32 *Max value* | 0x007E7DE0 | 0x00870900 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| FloatData | Data | f32 *Value <do not edit>* | 0x007E46F0 | 0x0086CE70 | 2849 / 65102 / 6 | 2451 / 8407 | agrees | data + engine |
| FloatRefData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 1 / 29 / 0 | 1 / 3 | agrees | data + engine |
| FocusEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| FocusLockedData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| FocusNode | Node | O(Node) *Next node* · O(Data) *Focus X* · O(Data) *Focus Y* | 0x007F31A0 | 0x00AC3700 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| FocusToPressedEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| Function | Object | *(empty)* | 0x007F8570 | 0x00A7D100 | 797 / 13580 / 0 | 1037 / 3350 | agrees | data + engine |
| HideDisableEffect | Effect | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| IfElseEventActionNode | ActionNode | O(Node) *Next node* · O(Action) *Action* · O(Data) *Variable* · O(Action) *Else Action* · O(Event) *Event* | 0x007E3790 | 0x0086BEF0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| IndexCountData | Data | O(Data) *Data* | 0x007EF590 | 0x00ABF6C0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| IndexDataData | Data | O(Data) *Data* · O(Data) *Index* | 0x007EF4B0 | 0x00ABFF90 | 160 / 1568 / 0 | 164 / 476 | agrees | data + engine |
| IntData | Data | i32 *Value <do not edit>* | 0x007E9BA0 | 0x00872990 | 5154 / 103010 / 10 | 5444 / 18477 | agrees | data + engine |
| LastInputIndexAction | Action | O(Data) *Last input index* | 0x007EE550 | 0x00ABEEB0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| LessData | LogicalData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 129 / 1801 / 0 | 59 / 183 | agrees | data + engine |
| LessEqualData | LogicalData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 16 / 1032 / 0 | 76 / 291 | agrees | data + engine |
| ListBoxData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| LocaleStringData | StringData | i32 *Int* | 0x00662B10 | 0x008835E0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| LockPointerAction | Action | O(Data) *Input index data* | 0x007F4020 | 0x00ABE4B0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| LogicalData | Data | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| ModifyVariableAction | Action | O(Data) *Variable* · f32 *Modify value* · f32 *Limit value* | 0x007E8830 | 0x00871530 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| MoveClipEffect | ClipEffect | f32 *X* · f32 *Y* · f32 *Width* · f32 *Height* · f32 *Move length* · f32 *Move direction* | 0x007EE130 | 0x00ABEA80 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| MoveEffect | Effect | O(Data) *Move length* · O(Data) *Move direction* | 0x007F2940 | 0x00AC2FD0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| MulData | OperatorData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 3 / 310 / 0 | 4 / 21 | agrees | data + engine |
| MultiplyColorEffect | ColorEffect | f32 *Red* · f32 *Green* · f32 *Blue* · f32 *Alpha* | 0x007E5210 | 0x0086DF30 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| MultiplyVertexColorEffect | VertexColorEffect | f32 *Top left red* · f32 *Top left green* · f32 *Top left blue* · f32 *Top left alpha* · f32 *Top right red* · f32 *Top right green* · f32 *Top right blue* · f32 *Top right alpha* · f32 *Bottom right red* · f32 *Bottom right green* · f32 *Bottom right blue* · f32 *Bottom right alpha* · f32 *Bottom left red* · f32 *Bottom left green* · f32 *Bottom left blue* · f32 *Bottom left alpha* | 0x007E5430 | 0x0086E240 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NameNode | Node | O(Node) *Next node* | 0x007EC100 | 0x00873F50 | 74 / 992 / 2 | 72 / 231 | agrees | data + engine |
| NavigateAction | Action | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateChildAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateChildAndRunAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateFirstSiblingAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateLastSiblingAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateNextSiblingAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateParentAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateParentAndRunAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigatePreviousSiblingAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateRunAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateRunAndChildAction | NavigateAction | O(Tree) *Action tree* | 0x007F0C40 | 0x00AC1B60 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateSiblingIndexAction | NavigateAction | O(Tree) *Action tree* · i32 *Index* | 0x007F0E10 | 0x00AC1D30 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateTree | Tree | TREE of NavigateTreeObject [O Description O Action] | custom (see Tree) | custom (see Tree) | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NavigateViewNode | Node | O(Node) *Next node* · O(Tree) *Navigate tree* · s8(font) *Font* | 0x007F0580 | 0x00AC1110 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| Node | Object | O(Node) *Next node* | 0x007EC100 | 0x00873F50 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NodeListNode | Node | O(Node) *Next node* · LIST *Node list* · O(Data) *Index* · f32 *Item height* · s8(sound) *Select sound* · s8(sound) *Up down sound* | 0x00662E90 | 0x00883990 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NormalToFocusEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| NotData | LogicalData | O(Data) *Data* | 0x007EF590 | 0x00ABF6C0 | 135 / 4823 / 0 | 331 / 1234 | agrees | data + engine |
| NotEqualData | LogicalData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 251 / 4851 / 2 | 162 / 602 | agrees | data + engine |
| NotFocusEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| Object | RuntimeObject | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| OperatorData | Data | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| OrData | LogicalData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 122 / 2843 / 1 | 169 / 570 | agrees | data + engine |
| PathNode | NameNode | O(Node) *Next node* | 0x007EC100 | 0x00873F50 | 21 / 233 / 0 | 21 / 63 | agrees | data + engine |
| PictureNode | Node | O(Node) *Next node* · s8(texture) *Picture* | 0x007EAF90 | 0x008737E0 | 1243 / 36529 / 0 | 1950 / 6559 | agrees | data + engine ; data + engine (1 corrupt-file instance kept opaque) |
| PlayerInputIndexData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| Pointer | Object | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| PointerXData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 315 / 3082 / 0 | 323 / 937 | WRONG: MDT [O] vs engine [] | data + engine |
| PointerYData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 316 / 3092 / 0 | 324 / 940 | WRONG: MDT [O] vs engine [] | data + engine |
| PopPathAction | SetPathAction | O(Node) *Stack path node* · f32 *In time* · f32 *Out time* · f32 *In wait time* · f32 *Out wait time* · bool *Paint outnode over innode* | 0x007E6FF0 | 0x0086FD50 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| PushPathAction | SetPathAction | O(Node) *Stack path node* · O(Node) *Destination node* · f32 *In time* · f32 *Out time* · f32 *In wait time* · f32 *Out wait time* · bool *Paint outnode over innode* | 0x007E6F20 | 0x0086FC80 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| Ref2System | System | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| RemoveEventAction | Action | *(empty)* | 0x007F8570 | 0x00A7D100 | 57 / 548 / 0 | 63 / 175 | agrees | data + engine |
| RightAlignedStyle | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 420 / 4838 / 1 | 168 / 719 | agrees | data + engine |
| RotateAroundCoordinateEffect | RotateEffect | O(Data) *Start angle* · O(Data) *Angle multiplyer* · O(Data) *X* · O(Data) *Y* | 0x007E2570 | 0x0086B7C0 | 1 / 95 / 0 | 1 / 6 | agrees | data + engine |
| RotateEffect | Effect | O(Data) *Start angle* · O(Data) *Angle multiplyer* | 0x007E23A0 | 0x0086B5E0 | 18 / 1678 / 0 | 18 / 108 | agrees | data + engine |
| RowDistanceStyle | Style | s8(font) *Font handle* · f32 *Row distance* | 0x007E4A60 | 0x0086D1F0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| ScaleNode | Node | O(Node) *Next node* · O(Data) *Width scale* · O(Data) *Height scale* | 0x006632F0 | 0x00883DF0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SetFocusAction | Action | O(Data) *x* · O(Data) *y* | 0x007F1750 | 0x00AC2760 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SetPathAction | Action | O(Node) *Path node* · O(Node) *Destination node* · f32 *In time* · f32 *Out time* · f32 *In wait time* · f32 *Out wait time* · bool *Paint outnode over innode* | 0x007E6E50 | 0x0086FBB0 | 253 / 2756 / 0 | 225 / 709 | agrees | data + engine |
| SetStringAction | Action | O(Data) *Variable* · O(Data) *Data* | 0x007E86F0 | 0x008713F0 | 5 / 198 / 0 | 5 / 18 | agrees | data + engine |
| SetVariableAction | Action | O(Data) *Variable* · O(Data) *Data* | 0x007E86F0 | 0x008713F0 | 2293 / 30343 / 4 | 2796 / 8572 | agrees | data + engine |
| SetVariableArrayAction | SetVariableAction | O(Data) *Variable* · O(Data) *Data* · O(Data) *Index* | 0x007E8BE0 | 0x00871910 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SetVariableSineAction | SetVariableSoftAction | O(Data) *Variable* · O(Data) *Data* · f32 *Speed* · f32 *Braking distance* | 0x007E8A20 | 0x00871750 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SetVariableSinusAction | SetVariableSineAction | O(Data) *Variable* · O(Data) *Data* · f32 *Speed* · f32 *Braking distance* | 0x007E8A20 | 0x00871750 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SetVariableSoftAction | SetVariableAction | O(Data) *Variable* · O(Data) *Data* · f32 *Speed* | 0x007E8920 | 0x00871650 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| ShowEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| SinMoveEffect | MoveEffect | O(Data) *Move length* · O(Data) *Move direction* | 0x007F2940 | 0x00AC2FD0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SingleLineSizeStyle | SizeStyle | s8(font) *Font handle* · f32 *Size constant* | 0x007E4A20 | 0x0086D1B0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SingleLineStyle | Style | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SizeStyle | Style | s8(font) *Font handle* · f32 *Size constant* | 0x007E4A20 | 0x0086D1B0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| SliderNode | Node | O(Node) *Next node* · O(Node) *Cursor node* · O(Data) *Data* · f32 *Minimum value* · f32 *Maximum value* · f32 *Number visible* | 0x007EA560 | 0x00872DB0 | 0 / 0 / 0 | 0 / 0 | agrees | engine only |
| SoundAction | Action | s8(sound) *Sound* | 0x007EFC80 | 0x00AC0700 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SplitAction | Action | O(Action) *Action 1* · O(Action) *Action 2* | 0x007F15F0 | 0x00AC25E0 | 6 / 145 / 0 | 6 / 21 | agrees | data + engine |
| SplitEffect | Effect | O(Effect) *Effect 1* · O(Effect) *Effect 2* | 0x007F0480 | 0x00AC0FF0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| SplitNode | Node | O(Node) *Next node* · O(Node) *Split node* | 0x007EAEA0 | 0x008736F0 | 952 / 23052 / 3 | 922 / 3342 | agrees | data + engine |
| StackPathNode | PathNode | O(Node) *Next node* | 0x007EC100 | 0x00873F50 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| StickyNode | Node | O(Node) *Next node* · O(Data) *Variable* · O(Data) *Data* | 0x007F2530 | 0x0087E6C0 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| StringData | Data | str32 *String <do not edit>* | 0x007E9ED0 | 0x00869790 | 1630 / 27329 / 5 | 1417 / 5176 | agrees | data + engine |
| StringRefData | Data | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| Style | Object | s8(font) *Font handle* | 0x007F1C50 | 0x00875D40 | 807 / 16615 / 0 | 692 / 2472 | agrees | data + engine |
| SubData | OperatorData | O(Data) *Data 1* · O(Data) *Data 2* | 0x007EE8B0 | 0x0086A200 | 9 / 556 / 0 | 8 / 39 | agrees | data + engine |
| System | Object | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| TextNode | Node | O(Node) *Next node* · O(Data) *String* · O(Style) *Style* | 0x007EA3F0 | 0x00872C40 | 1823 / 32256 / 4 | 1299 / 4586 | agrees | data + engine |
| TimeoutActionNode | ActionNode | O(Node) *Next node* · O(Action) *Action* · f32 *Timeout time* | 0x007E38C0 | 0x0086C030 | 29 / 1838 / 1 | 20 / 89 | agrees | data + engine |
| ToggleVariableAction | Action | O(Data) *Variable* | 0x007E87A0 | 0x008714A0 | 27 / 584 / 1 | 79 / 250 | agrees | data + engine |
| TransformNode | Node | O(Node) *Next node* · f32 *X* · f32 *Y* · f32 *Width* · f32 *Height* · O(Node) *Transformed node* | 0x007E9510 | 0x00872310 | 4769 / 95073 / 9 | 5480 / 18621 | agrees | data + engine |
| TransitionalEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| TransitionalHideEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| TransitionalShowEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* | 0x007E7590 | 0x00870290 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| TranslateNode | Node | O(Node) *Next node* · O(Data) *X* · O(Data) *Y* | 0x007E9920 | 0x00872710 | 109 / 2331 / 0 | 47 / 228 | agrees | data + engine |
| Tree | Object | TREE of TreeObject [no payload] | custom (see Tree) | custom (see Tree) | 0 / 0 / 0 | 0 / 0 | - | engine only |
| TypeEvent | Event | u8 *Input index (0 == all indexes)* · i32(enum) *Event type* | 0x007E1A30 | 0x0086AC80 | 731 / 9828 / 1 | 768 / 2426 | agrees | data + engine |
| UnlockPointerAction | Action | *(empty)* | 0x007F8570 | 0x00A7D100 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| VariableColorEffect | Effect | O(Data) *Red* · O(Data) *Green* · O(Data) *Blue* · O(Data) *Alpha* | 0x00601920 | 0x0087E210 | 292 / 6758 / 0 | 183 / 612 | agrees | data + engine |
| VariableEffectNode | EffectNode | O(Node) *Next node* · O(Effect) *Effect* · O(Data) *Effect level* | 0x007E76F0 | 0x008703F0 | 2 / 164 / 0 | 0 / 0 | agrees | data + engine ; engine + other game's data |
| VariablePictureNode | Node | O(Node) *Next node* · O(Data) *First part* · O(Data) *Middle part* · O(Data) *Last part* | 0x00601B60 | 0x0087E390 | 37 / 2700 / 0 | 69 / 350 | agrees | data + engine |
| VertexColorEffect | Effect | f32 *Top left red* · f32 *Top left green* · f32 *Top left blue* · f32 *Top left alpha* · f32 *Top right red* · f32 *Top right green* · f32 *Top right blue* · f32 *Top right alpha* · f32 *Bottom right red* · f32 *Bottom right green* · f32 *Bottom right blue* · f32 *Bottom right alpha* · f32 *Bottom left red* · f32 *Bottom left green* · f32 *Bottom left blue* · f32 *Bottom left alpha* | 0x007E5430 | 0x0086E240 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| VolumedSoundAction | SoundAction | s8(sound) *Sound* · f32 *Volume (0-1)* | 0x007EFCE0 | 0x00AC0760 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| WindowNode | TransformNode | O(Node) *Next node* · f32 *X* · f32 *Y* · f32 *Width* · f32 *Height* · O(Node) *Transformed node* | 0x007E9510 | 0x00872310 | 0 / 0 / 0 | 0 / 0 | - | engine only |
| WstringData | Data | wstr32 *Wstring <do not edit>* | 0x007EA0B0 | 0x00869930 | 12967 / 131461 / 0 | 14045 / 39741 | agrees | data + engine |
| ZoomNode | Node | O(Node) *Next node* · O(Data) *Z* | 0x007E1D60 | 0x0086AF80 | 0 / 0 / 0 | 0 / 0 | - | engine only |

### Appendix B - enum values (the value is stored; names from BfVietnam.exe)

- **AnyKeyEvent.Event type** (BfVietnam.exe 0x0086AA90): 1=pressed, 2=released, 3=released outside
- **BlendFuncEffect.Source blend func** (BfVietnam.exe 0x008758E0): 0=ERROR, 1=Zero, 2=One, 3=Source color, 4=One minus source color, 5=Dest color, 6=One minus dest color, 7=Source alpha, 8=One minus source alpha, 9=Dest alpha, 10=One minus dest alpha, 11=Source alpha saturate
- **BlendFuncEffect.Destination blend func** (BfVietnam.exe 0x008758E0): same table as BlendFuncEffect.Source blend func
- **ButtonEvent.Event type** (BfVietnam.exe 0x00878550): 0=No event, 1=ButtonPress, 2=ButtonRelease, 3=ButtonReleaseOutside, 4=UnicodePress, 5=UnicodeRelease, 6=UnicodeReleaseOutside, 7=GainFocus, 8=LostFocus, 9=PointerIn, 10=PointerOut, 11=PointerMove, 12=PointerMoveOut, 13=PreShow, 14=PostShow, 15=PreHide, 16=PostHide, 17=PreWaitShow, 18=PreWaitHide, 19=Quit, 20=Disable, 21=Enable, 22=Update
- **ButtonEvent.Button type** (BfVietnam.exe 0x00879D00): 0=No button, 1=Select, 2=Cancel, 3=Back, 4=Edit, 5=Leftwards, 6=Upwards, 7=Rightwards, 8=Downwards, 9=Home, 10=End, 11=PageUp, 12=PageDown, 13=Insert, 14=Delete, 15=Backspace, 16=Tab, 17=Start, 18=Enter, 19=Scroll up, 20=Scroll down
- **ExtendedButtonEvent.Event type** (BfVietnam.exe 0x00878550): same table as ButtonEvent.Event type
- **ExtendedButtonEvent.Button type** (BfVietnam.exe 0x00879D00): same table as ButtonEvent.Button type
- **TypeEvent.Event type** (BfVietnam.exe 0x00878550): same table as ButtonEvent.Event type

<!-- END GENERATED -->
