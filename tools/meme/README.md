# tools/meme - the Meme 2.0 menu format reference

The research tools behind `docs/Meme_Format.md` (the spec of the extensionless `menu/` files in `menu.rfa`, the whole
menu and HUD system of BF1942 and BF Vietnam) and behind the C# library `src/RefractorForge.Formats/Menu/`.

| file | what it is |
|---|---|
| `meme.py` | The reference reader and writer (Python 3, no dependencies): parse, serialize with the table modes auto / keep / rebuild, `new` / `null` / `ref`, walk, lint, dump, the old table-less format, the Tree grammar. The C# library is a port of it; when the two disagree, this is the oracle. |
| `meme_schema.json` | The full class table: per game (BF1942 194 entries, BFV 199) each class's tokens and engine labels, per field its IStream slot, the base class it expects, the sibling role of "Next node", handle kinds, enum names and the one engine constraint; per class its base, engine serialize / vtable addresses, which tracers agree, the MDT header's verdict and the corpus counts. Built during the research from four independent angles plus the corpus; kept here with all its provenance. |
| `selftest.py` | The reference's own tests: negative controls (a wrong layout must make retail instances opaque), edits, the Tree grammar, the old format, the writer guards, lint. |
| `make_resource.py` | Writes `src/RefractorForge.Formats/Menu/MemeSchema.json`, the part of `meme_schema.json` an editor needs, which the C# library embeds. |

## Regenerating the C# resource

After any change to `meme_schema.json`:

```
python tools/meme/make_resource.py
dotnet test src/RefractorForge.Tests -c Release --filter "FullyQualifiedName~Meme"
```

Commit the regenerated `MemeSchema.json` with the schema change. Never edit the resource by hand.

## Running the Python tools

The corpus commands read an extracted corpus - read-only copies of the Meme entries of the menu archives:

- `MEME_CORPUS`: a folder with `BF1942/` and `BFV/`, each `<mod>/menu/<file>` (retail: `bf1942`, `XPack1`, `XPack2`,
  `BfVietnam`, from the clean installs).
- `MEME_COMMUNITY`: a folder with `BF1942c/` and `BFVc/`, each `<mod>-<archive>/menu/<file>` (every `menu*.rfa` of every
  non-retail mod of the main installs).

```
set MEME_CORPUS=...\corpus
set MEME_COMMUNITY=...\community
python tools/meme/selftest.py                 # "SELFTEST PASSED"
python tools/meme/meme.py corpus              # the corpus table of docs/Meme_Format.md
python tools/meme/meme.py dump <file> BFV     # a file's tree
python tools/meme/meme.py lint <file> BF1942  # editor-level findings
```

`dump`, `check` and `lint` take any single file and need no corpus. The C# tests do not need the extraction at all:
they read the archives of the installs directly (`MemeCorpusTests`, reported as skipped when an install is absent).
