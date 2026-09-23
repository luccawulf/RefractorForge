# RefractorBridge — a standalone BF1942 → Battlefield Vietnam mod converter

*Design & scoping plan. Working name `RefractorBridge` (CLI: `rbridge`). Not built yet — this is the map.*

Both games are **Refractor 2**. Their file formats and `.con` language are ~90% shared; the deltas
that matter are few, specific, and mostly silent (the game renders wrong or dies on load rather than
warning). This plan turns the one-off, in-game-verified `Al_Vietnas` port (and the `FlyingDeLorean` +
11-vehicle fleet port) into a **reusable, offline-gated converter** for the whole BF1942 mod corpus.

## 0. Feasibility verdict — proven, not speculative

- **Whole BF1942 content trees already run in BFV.** 6 of your 17 BFV mods do it today:
  `BFV_WW2MOD_X`, `BFV_WW2Mod`, `Battlegroup42`, `EoD`, `PoE`, `PoE_Stunt`. This is the ceiling proof —
  nothing about the engine forbids it.
- **You have already done it end-to-end.** `Al_Vietnas` (DC's `DC_Al_Nas` → BFV echo mod) loads and
  plays in game: terrain, 551 static placements over 97 templates, sky, sound, CTF. `FlyingDeLorean`
  + a 10-vehicle DC fleet were ported into it as live, driveable, replicated vehicles.
- **The recipe is written down.** `Al Nas to Vietnam/docs/BF1942_to_BFV_PORTING.md` +
  `work/analysis/bfv_delta.md` are a near-complete spec, each claim tagged *proven* / *inferred*.
- **The low-level code exists.** `RefractorForge.Formats` already reads/writes everything a converter
  touches (see §4 reuse table).

The open question was never "is it possible" — it's "can it be a *tool* instead of a hand port." Yes,
with the discipline in §2. Corpus in reach: **209 BF1942 mods**, hundreds of maps, thousands of objects.

## 1. Scope — four layers, each usable on its own

| Layer | Input → Output | In-game rounds | Notes |
|---|---|---|---|
| **A. `.con` dialect engine + oracles + offline gate** | any `.con`/tree → classified + rewritten | 0 (fully offline) | The shared foundation. B, C, D all call it. |
| **B. Level/map porter** | BF1942 level `.rfa` → self-contained BFV level `.rfa` | few | Mostly static geometry. The `Al_Vietnas` path. |
| **C. Object/vehicle porter** | BF1942 object tree → BFV mod object folders | many | A live simulated tree; the hard one (§7). |
| **D. Whole-mod orchestration** | BF1942 mod → BFV mod | — | Runs B over every level + C over the shared object/vehicle pools, dedupes, writes one mod tree. |

Build order is **A → B → D(maps) → C → D(full)**. A is the reusable heart; B produces a droppable file
fast; D-over-maps is then just a loop; C is a separate, deeper effort that D folds in last.

## 2. Guiding principles (the method that actually worked, distilled from the port)

1. **The executables are the oracle.** Refractor stores property names in a contiguous NUL-separated table,
   so `strings BF1942.exe` vs `strings BfVietnam.exe` answers "does BFV implement this?" definitively.
   Never guess a command's validity when the exe can be asked.
   **The table holds BARE PROPERTY NAMES (`setTextureParam`), not dotted `Class.property` strings** — searching
   for `shaderManager.setTextureParam` finds zero hits in *either* binary. Dotted strings do exist but are
   serialization forms such as `"Object.name "`. Measured, and matching the porting notes exactly:
   `setTextureParam` 1/0, `hasResponsePhysics` 3/0, `texLayer1` 1/0, `setActiveCombatArea` 1/1, `setTorque` 1/1.
   *(Caveats: absence is the only strong signal, and even then only as "in BF1942, absent from BFV" — presence
   can be an accident, since `normalMap`/`specularEnable`/`lightDirection` appear in BfVietnam.exe as D3D
   render-state names while being no part of BFV's water object. And it never says which class exposes a name.)*
2. **Per-class stock census beats the exe for `ObjectTemplate`/`Object`/`GeometryTemplate` properties.**
   Refractor registers those per class. `BfVietnam.exe` lists `ObjectTemplate.setStrength`, yet on the
   `Spring` class stock uses `strength` 128× and `setStrength` 0×. **When the two oracles disagree,
   per-class stock usage wins.** So the converter needs a *prebuilt census of BFV stock* (§3), not just
   the exe.
3. **Every transform is gated offline before any in-game test.** Reference resolution, surviving
   BF1942-only commands, `.rs` semicolons, geometry-name closure, NetworkableInfo closure, asset
   presence, byte-exact RFA round-trip. An in-game test is expensive and slow; the gate is free.
4. **Parse vs construct is the highest-value split.** Templates parse at load in every mode; an object
   only *constructs* when a mode spawns it. "Loads in Conquest, crashes in CTF" ≠ CTF files are wrong —
   CTF spawns something Conquest doesn't. The tool should be able to emit a build that *ships all
   content but spawns nothing* (parse-only) to bisect.
5. **Measure the base rate before calling something a bug.** In a port *everything* differs from stock,
   so "differs from stock" is nearly free evidence. Census the stock's class/range first (forward refs,
   `cullRadiusScale`, `hasDynamicShadow`, collision verts, gear band, mass ratio — all have stock bands).
6. **Never guess where a collision is silent.** Flattening any namespace (textures, meshes) can *swap*
   content without any audit failing. Detect by **hashing**: any basename with >1 distinct hash needs
   disambiguation before flattening.
7. **Bytes, not text.** Read/write `.con`/`.rs` as bytes (latin-1). Python universal-newlines silently
   eats CRLF; a general RFA rewriter silently corrupts the container (§7 R1). Gate every writer with a
   **no-op self-test**: transform-with-zero-changes must reproduce the input byte-for-byte.
8. **One variable per in-game test; log every prune and substitution** so the user can review and revert.

## 3. Data the tool carries or builds once

- **Two exe symbol tables** (the oracles). Harvest bare property names (plus dotted serialization forms as a
  secondary signal) from `BF1942.exe` and `BfVietnam.exe`. Paths default to `D:\Games\EA GAMES\...`;
  overridable with `--bf42` / `--bfv`. **DONE — `Oracle/ExeSymbolTable.cs`.**
- **BFV stock census** — build once by scanning every stock BFV archive (`Mods/BfVietnam/Archives/*`
  + `objects.rfa`, `standardMesh.rfa`, `texture.rfa`, `sound.rfa`) and all 83 stock levels:
  - per-class property usage counts (for the setX↔X decision, principle 2);
  - the set of stock template names (so the tool never *redefines* a name BFV owns — that replaces
    stock globally, §7 C3);
  - stock value bands: `cullRadiusScale` (≤5), `hasDynamicShadow` (≤3), collision verts (median 66,
    driven max ~497), gearbox band (`gearUp−gearDown` ≥0.3), mass by class, `hasCollisionPhysics` per
    class. Emit as `census/bfv_stock.json`.
- **The dialect delta**, baked in as rule tables (from `bfv_delta.md`, all *proven*):
  - **DROP** (BF1942-only or dead-in-both engine commands):
    `shaderManager.setTextureParam`, `renderer.globalAmbientColor`, `renderer.animatedMeshAmbientColor`,
    `renderer.beginGlobalCluster`, `renderer.endGlobalCluster`, `renderer.fogLinearStart/End` (→rename),
    the 12-line BF1942 layered-water block (`water.texLayer1/2, normalMap, scrollDirection*, scrollLayer*,
    tileLayer*, tileNormalmap, scrollNormalmap, specularStreakFactor, specularEnable, specularColor,
    lightDirection, addBlendEnable, envMapEnable, envMapColor`), `GeometryTemplate.lodDistance` (the bare
    form, dead on patchTerrain in both — keep `setLodDistance`), `game.defaultStartPos`,
    `spawnPointManager.groupStatus`, `ObjectTemplate.exitTimer/unableToChangeTeam/DamageWhenLost`,
    `ObjectTemplate.hasResponsePhysics/setHasResponsePhysics` (→`hasMobilePhysics` or drop),
    the `TSun` LensFlare block (its textures exist in no archive; no stock BFV level defines a LensFlare).
  - **RENAME** (confirmed engine-level): `fogLinearStart→fogstart`, `fogLinearEnd→fogend`,
    `Game.setViewDistance→Game.ViewDistance`, `Object.setName→Object.Name`,
    `hasResponsePhysics→hasMobilePhysics`.
  - **RE-ARITY / value fix**: `shadow.shadowColor <f>` (1) → `<r/g/b/a>` (stock `0/0/.075/.5`);
    `game.setTeamSkin <team> <skin>` (2) → `<team> <index> <skin>` (3, idx 1..4) — **flag, don't guess**;
    `game.setKit` index range 0..5 → 0..7 — **flag**; `game.setActiveCombatArea` unchanged (origin+size).
  - **ADD (BFV-required, absent from BF1942 levels)**: `Game.setLoadMusicFilename`,
    `renderer.SecondaryDiffuseColor`, `renderer.LMambientColor`, `renderer.standardmeshminintensity`,
    `renderer.fogstart/fogend`, `Game.ViewDistance`, `game.setTeamInsignia`+`setTeamInsigniaName`,
    the `if v_arg1 == host / Game.spawnPlayers 1 / endIf` guard, `run growth/overGrowth`+`underGrowth`,
    `GeometryTemplate.waveHeight 1.0` on the terrain, `Object.Name track`.
  - **LIGHTING fix-ups (§7 L)**: raise `renderer.diffuseColor` to the stock 0.85–1.0 band; scale
    `materialDiffuse` by `1/min` clamped at 1; add `alphaTestRef 0.5` where `transparent true` sits on a
    texture with a real alpha channel.
  - **The `setX→X` per-class family** — **NOT a blanket rule.** Applied only where the §3 census says
    stock uses `X` many times and `setX` zero times *on that class*, confirmed against the exe, arity
    checked. Ship the confirmed list from the port (`setNetworkableInfo, setStrength, setDamping,
    setTorque, setEngineType, setDifferential, setInputFire, setAcceleration, setMinRotation…`, plus the
    amphibious group `setHullHeight, setFloatMaxLift/MinLift, setWingLift, setFlapLift, setPositionOffset,
    setHasCollisionPhysics, setHasTurretIcon, setAutomatic{Yaw,Pitch}Stabilization`) as a *seed*, but
    re-run the census per vehicle — a rename map from one vehicle is not a rename map (proven twice).
  - **Substitution table (prefer over deletion, log every use)**: `AnimatedGeFlag/AnimatedSoFlag →
    AnimatedUSFlag/AnimatedNVAFlag/AnimatedVCFlag/AnimatedARVNFlag`; `DestroyerSonar → JetRadar`;
    `env cubemap → Texture/env_default_0N.dds` (6 faces); missing muzzle/effect textures → nearest stock.

## 4. Reuse vs build (against the current RefractorForge.Formats)

| Need | Reuse (exists) | Build new |
|---|---|---|
| RFA read / decode / raw passthrough | `Rfa/RfaArchive` | — |
| RFA write / in-place repack | `Rfa/RfaWriter` (`Build`, `RepackToFile`) | **in-place byte-preserving patcher w/ `--selftest`** (§7 R1) |
| LZO1X | `Rfa/Lzo1x` | — |
| `.sm` read / write | `Rfa/StandardMesh`, `StandardMeshWriter` | — |
| `.tm` read | `Rfa/TreeMesh` | **`tm→sm` regroup** (drop sprite group 2; groups 0/1/3 → `ObjMesh` → `StandardMeshWriter`) + emit `.rs` |
| `.rs` material parse | `Render/RsShaderSet` | `.rs` **writer** with the semicolon gate; `.sm` `*_MaterialN` ↔ `.rs` subshader audit |
| heightmap / materialmap / growth / terrain cfg | `Terrain/*` | growth **stub** emitter (zero index maps + 16-slot `.wst`) |
| static objects / gameplay `.con` | `Cons/*` | — |
| DDS decode + uncompressed write | `Render/DdsTexture` | **mip-cap** (drop top mips, byte-range copy, no re-encode) |
| minimap / thumbnail | `Render/Minimap` | — |
| geometry / Vec3 | `Geometry/Vec3` | — |

**Standalone but not isolated:** `RefractorBridge` is its own exe/project, referencing
`RefractorForge.Formats` (and `.Render` for `RsShaderSet`/`DdsTexture`/`Minimap`). Ship self-contained
(`dotnet publish -r win-x64`). This keeps it decoupled from the editor (your delivery choice) while
reusing the byte-exact format code instead of re-implementing it (the Al Nas `tools/` re-implemented RFA
and paid for it in §7 R1-class bugs).

## 5. Module layout

```
RefractorBridge/                     (new project, refs Formats + Render)
  Program.cs                         CLI dispatch
  Oracle/ExeSymbolTable.cs           load+cache a Refractor exe's Class.property literals
  Oracle/StockCensus.cs              build/load census/bfv_stock.json (per-class usage, names, bands)
  Con/ConDialect.cs                  DROP/RENAME/REARITY/ADD rules; Analyze() + Rewrite() (bytes, CRLF-safe, rem-aware)
  Con/ConTree.cs                     walk a level/mod tree or an RFA; run chain resolution; forward-ref aware
  Rfa/InPlacePatcher.cs              byte-preserving patch + --selftest (the R1 fix)
  Mesh/TmToSm.cs                     TreeMesh -> StandardMesh + .rs
  Mesh/DdsCap.cs                     mip-cap
  Assets/AssetHarvester.cs          collect from .rs AND emitters AND sound (@RTD expansion); hash-dedup; flatten w/ collision detection
  Level/LevelPorter.cs              layer B
  Object/ObjectPorter.cs            layer C (census-driven rename, closure, audits)
  Mod/ModConverter.cs               layer D
  Verify/PortVerifier.cs            the offline gate
```

**CLI surface**

```
rbridge oracle build [--bf42 <exe>] [--bfv <exe>]     # cache both symbol tables
rbridge census build [--bfv-mods <dir>]               # build census/bfv_stock.json
rbridge condelta  <fileOrDirOrRfa>...                  # classify commands (BF1942-only / dead / soft / needs-rewrite)
rbridge conconvert <inConOrDir> <outDir>               # apply high-confidence dialect rewrite, print change log
rbridge portlevel <bf1942_level.rfa> <out.rfa> [--mod echo] [--name X]   # layer B
rbridge portobject <bf1942_obj_tree> <out_mod_objects> [--census ...]    # layer C
rbridge portmod   <bf1942_mod_dir> <out_bfv_mod_dir>                     # layer D
rbridge verify    <built_level_or_mod>                 # offline gate only
```

Every `port*` runs `verify` automatically and refuses to emit on a hard failure unless `--force`.

## 6. Milestones (each ends at an offline gate; ⚑ = a USER in-game test)

- **M0 — Oracles ✅ DONE** (`Oracle/ExeSymbolTable.cs`, `rbridge oracle`). All six spot-checks reproduce the
  documented delta against the real binaries.
- **M0b — Retail census ✅ DONE** (`Oracle/StockCensus.cs`, `rbridge census build|judge|class|name`).
  Censused **100 retail archives — 4,821 `.con` files, 336,818 commands → 73 classes, 9,070 template names,
  60 value ranges.** It independently reproduces every documented conclusion from the shipped content:
  `Spring.strength` **129×** with `setStrength` **0×**, `Engine.torque` 84×, `engineType` 83×,
  `differential` 84× — all *RenameToShortForm*; `StandardMesh.setLodDistance` **7,395×** → *KeepSetForm*,
  which is the guard that stops a blanket "strip set" rule breaking working content.

  The decision is **two-tier**, and the second tier was forced by the data. Per-class evidence alone missed
  `setNetworkableInfo`: retail sets *neither* spelling on `PlayerControlObject`, yet uses `networkableInfo`
  **1,182× across six classes** and `setNetworkableInfo` nowhere — and a template with no NetworkableInfo never
  replicates, so the server owns the vehicle and no client is ever told it exists. So: per-class evidence first,
  then whole-corpus evidence for a class retail never uses — **guarded by the mirror read**, that a set- form
  retail uses *anywhere* is a spelling BFV keeps and must not be rewritten on a no-evidence class.

  Wired into the rewriter behind an explicit `--use-census` (it is the biggest change the converter can make,
  so never automatic). Measured effect: **FHSW 182,590 changes applied / 12,219 left for a human, FH 43,314 /
  5,187, bg42 72,623 / 8,595, EoD 16,617 / 1,993** — roughly **89–94 % of the setX family decided**, each
  rewrite carrying its evidence (`on class Engine: retail uses 'torque' 84x and 'setTorque' never`).
  13 gate tests, incl. attribution, `activeSafe`, comment-skipping, both tiers, the keep-set guard, JSON
  round-trip and census-driven idempotence.
- **M1 — `.con` dialect engine ✅ DONE** (`Con/ConDialect.cs`, `Con/ConText.cs`, `Con/ConSource.cs`;
  `rbridge condelta` / `conconvert`). 24 gate tests in `RefractorForge.Tests/BridgeConDialectTests.cs`:
  documented-rewrite match, idempotence, byte-identical no-op, CRLF/CR/LF and mixed endings preserved,
  `rem`/`beginRem` never edited, and the review flags (TreeMesh, setX family, team-skin arity) proven to
  report rather than rewrite. **Validated on the real ground truth**: reading `DC_Al_Nas.rfa` straight out of
  `DC_Final`, it independently re-derives every documented finding (42 `hasResponsePhysics`, the 12 layered-water
  lines, `globalAmbientColor`, `setTextureParam`, the global-cluster pair, `GeometryTemplate.lodDistance` in
  `Init/Terrain.con`, 47 `DamageWhenLost`, 30 `groupStatus`, and all 4 renames). After `conconvert`,
  re-analysing the output reports **no BF1942-only engine commands** and `--strict` exits 0.
- **M2 — RFA container gate ✅ DONE** (`Rfa/RepackSelfTest.cs`, `rbridge rfacheck`). **No new patcher was
  needed** — `RefractorFlatArchive.RepackToFile` already carries kept entries across as *raw regions*, so nothing
  is re-compressed. What M2 added is the proof. One correction to the original plan: **whole-file byte-identity is
  the wrong gate.** The repacker lays regions down in TOC order, and real archives do not always store them that
  way (`berlin_003.rfa` lists `AI.con` first while its data sits 2,505 bytes in), so a faithful repack of such an
  archive is legitimately reordered. The gate therefore checks *faithfulness* — flag + 148-byte blob, entry
  names/order/sizes, **raw region bytes**, and decoded payloads — and reports byte-identity as information.
  Measured: `128_planes.rfa` (314 MB, 1,257 entries, uncompressed — the documented crash case) and `DC_Al_Nas.rfa`
  (456 entries, LZO) both come back **byte-identical**; `berlin_003`/`Coral_Sea_003`/`Hue.rfa` come back faithful
  and reordered. 8 gate tests, including "replacing one entry leaves every other entry alone".
- **M3 mesh piece — TreeMesh → StandardMesh ✅ DONE** (`Mesh/TmToSm.cs`, `rbridge tmconvert` / `tminfo`).
  A regrouping, not a resample: same vertices and triangles, reorganised into StandardMesh sections, with the
  **collision hull carried across** (trees are solid in game) and the **sprite/billboard group dropped** (BFV
  has no imposter shader). The `.rs` is modelled on retail BFV's own `C01F_Trees_M1.rs` rather than invented —
  note it uses `transparent false` **with** `alphatestref`, not `transparent true`, plus `selfillum`; alpha-
  *blending* a leaf card is what makes ported foliage look like glass. Measured: **109 of 111** base-BF1942
  `.tm` converted and re-parsed (78 with collision hulls, 55 sprite materials / 3,532 triangles dropped), and
  **35 of 35** in Forgotten Hope. The 2 skips are `jungle_bush_M1` and `Jungle_tree15c_M1`, which parse cleanly
  and genuinely contain *nothing but* billboards — a content decision (substitute or drop), not a failure.
  8 gate tests incl. the whole retail archive, exact triangle-count preservation, `.sm`↔`.rs` material-name
  closure, and the semicolon rule. **Open, needs the game to settle:** triangle winding is carried through
  unflipped; if converted foliage renders inside-out, `--reverse-winding` is the one-flag fix.
- **M3a — Level porter, skeleton ✅ DONE** (`Level/LevelPorter.cs`, `Level/GrowthStubs.cs`, `rbridge portlevel`).
  Reads a BF1942 level `.rfa` and writes a BFV one: every entry moved from `bf1942/levels/<Old>/` to
  `BfVietnam/levels/<New>/`, **the paths written inside the scripts moved with them** (the terrain script names
  its own heightmap/material map/texture base by absolute archive path), the dialect applied to every script,
  retail casing restored (`MaterialMap.raw`, `Menu/Thumbnail.dds`), `GeometryTemplate.waveHeight 1.0` added,
  the growth pair generated (empty 256²/1024² index maps + 16-slot palettes, with `materialMapFilename`
  pointed at the *new* level — it is an engine property, and left stale the game grows another level's
  layout), and the files BFV never reads dropped (PreCache, texturePreCache, WaterShader.rs, cullRadius.con,
  `.bak`, `.rcm`).

  **Lighting is compensated, not second-guessed**: when `renderer.globalAmbientColor` is dropped (BFV has no
  such command) the diffuse term is raised into retail's 0.85–1.0 band, because the converter removed that
  light itself. Off with `CompensateLighting = false`.

  **The geometry-closure audit is the part that earns its keep.** `ObjectTemplate.geometry` resolves against
  GeometryTemplate *names*, not files, so a template whose geometry is never declared — or whose mesh the level
  does not ship — parses perfectly, loads clean, passes every name-only audit, and null-derefs when something
  finally *constructs* it. On DC_Al_Nas it found 13, including control points pointing at an undeclared
  `flagbase_m1` and DC's own literal `geometry 'Nothinghere'`. Resolving both directions tightened the kept
  statics from 156 to 149. 17 gate tests, on a synthetic level and the real DC one.

  Measured: DC_Al_Nas → 313 entries / 15.9 MB, Berlin, Bocage and Interstate's 2005 all port and pass the
  write-then-re-read check.

- **M3b — Object embedding ✅ DONE** (`Level/AssetPool.cs`, `Level/ObjectEmbedder.cs`, `Mesh/DdsCap.cs`,
  `rbridge portlevel … --embed <mod>[,<mod>…]`). Lifts the objects a level places out of the mod's shared
  archives and ships them inside the level, in the folder shape the engine actually loads
  (`Objects/<Name>/{<Name>,objects,geometries}.con` + a chaining `Objects/Objects.con`, with Init.con given
  the `run`). Closure follows `addTemplate` / `setObjectTemplate` / `projectileTemplate` transitively and is
  bounded by an exclusion list. Geometry is retargeted to `../standardMesh/<basename>`, TreeMesh geometry is
  converted in place, and the `.rs` materials and the textures they name come along.

  **The result — every placement, not a handful:**

  | level | before | after | entries |
  |---|---|---|---|
  | Berlin | 1/326 | **326/326** | 506 |
  | Bocage | 0/284 | **284/284** | 721 |
  | Stalingrad | — | **649/649** | 572 |
  | DC_Al_Nas | 149/729 | **729/729** | 981 |
  | Eagles_Nest | — | **513/513** | 755 |
  | EoD A_Shau | — | **171/171** | 368 |
  | Interstate 2005 | 315/2003 | **1910/2003** | 1264 |

  Three things this shook out, each one a documented trap that bit for real:
  - **Texture size is the whole ballgame on an upscaled install.** Berlin's first embed was **679 MB** — 101
    textures totalling 692 MB, one sandbag at 22 MB, because the source install carries an AI-upscaled pack.
    `DdsCap` drops top mip levels (a byte slice plus a header edit, *never* a re-encode) and the same level
    becomes **23.8 MB**. Cube maps and textures with no mip chain are left alone rather than re-encoded.
  - **Mods inherit.** DC_Final's levels place Desert Combat's objects; with only DC_Final in the pool
    DC_Al_Nas kept 487/729, with `--embed DC_Final,DesertCombat` it keeps 729/729.
  - **A level that ships its own `Objects/` and `texture/` shares a namespace with what is embedded beside it**,
    and the engine is case-insensitive, so `Texture/x.dds` and `texture/x.dds` are one entry. The level's own
    copy now wins (an embedded one overwriting it is a *silent content swap* — nothing missing, nothing
    dangling, every audit green, wrong texture drawn), and `Objects/Objects.con` **merges** rather than losing
    a side. The write-then-re-read gate is what caught this; DC_Al_Nas's index now carries its own `nx_M-923`
    and camels alongside the embedded vegetation.

  13 gate tests, including the real decoder re-reading a capped texture.
- **M3 — Level porter B, remaining.** Object-folder layout
  (`Objects/<Name>/{<Name>,objects,geometries}.con`), asset harvest+embed+flatten-with-collision-check,
  `tm→sm`, DDS cap, growth stubs, minimap/thumbnail. `PortVerifier` gate: 0 undefined templates, 0
  unresolved geometry/mesh/texture/run/sound, 0 surviving BF1942-only commands, all `.rs` semicolons,
  RFA round-trips byte-exact. ⚑ Then the user loads it in game.
- **M4 — Mod-over-maps D (1).** Loop B over every level in a mod; share/dedupe the object + texture
  pools across levels; write one BFV mod tree. Gate as M3, per level. ⚑
- **M5 — Object/vehicle porter C (large, iterative).** Census-driven `setX→X`; transitive
  geometry/NetworkableInfo/addTemplate closure with a bounded exclusion list; the audits (collision
  verts, gear band, mass ratio, `cullRadiusScale`, `hasDynamicShadow`, passenger-PCO→BodyInfo, parent-
  class checks); parse-only build mode for bisection. Gates as above + the §7-C audits. ⚑ many rounds.
- **M6 — Full mod D + report (1).** C folded into D; a per-mod HTML/markdown report of every drop,
  substitution, rename, downsize, and audit flag, so a human can review before shipping. ⚑

Rough order-of-magnitude only; C (M5) is where the real time lives and is inherently in-game-bound.

## 7. Risk register — the traps, each mapped to a mitigation

**Container (R)**
- **R1 RFA rewrite corrupts the archive.** A from-scratch writer flags the archive compressed, zeroes
  the 148-byte header blob, substitutes 12-byte trailers → engine reads block headers that aren't there
  and dies (3 "map crashes" reports were only this). → Use `RepackToFile`/in-place patch that preserves
  flag, blob, trailers, order, tail; **no-op self-test** gates it. BFV loads both raw and LZO archives —
  only the flag must match the contents.

**Structure (S)**
- **S1 Flattened template file doesn't load.** One folder per object, mirroring the game's own object
  archives; `Objects/Objects.con` chains `run <Name>/<Name>`; `Init.con` also runs it. → `LevelPorter`
  emits exactly that layout.
- **S2 `GeometryTemplate.file` path forms.** Must all be rewritten to `../standardMesh/<basename>`, and
  the *filename must not select which files get rewritten* (a `parts.con` carries geometry too). Sources
  vary: `\CamaroZ28\...` (Interstate), bare name (BF1942). → Rewrite **every** `.con`; then assert every
  `GeometryTemplate.file` uses the `../standardMesh/` form **and** resolves to a shipped `.sm`. Check the
  literal path form separately from mesh presence (basenaming before the check turns the bug into a
  false pass). Null geometry only derefs on **construct** → mode-specific crash, no bad-file symptom.
- **S3 growth is effectively mandatory** (83/83 stock `run` both). → emit zero index maps + 16-slot `.wst`.
- **S4 drop the BF1942-only level files** `PreCache.con`, `texturePreCache.dat`, `WaterShader.rs`,
  `cullRadius.con` (present in 17/83 but never `run`), `*.con.bak`.

**Objects/vehicles (C)** — layer C, the hard one
- **C1 Silent property drop.** `setNetworkableInfo` (→ no replication: server owns the vehicle, no client
  ever sees it), `setStrength/setTorque/setInputFire` (→ inert). Nothing warns. → census-driven rename
  (§2.2), never a blanket strip (breaks `setEntryRadius`, `setLodDistance`, `setMinimapIcon`…).
- **C2 Dangling refs that kill on construct, invisibly:** `ObjectTemplate.geometry X` resolves by
  GeometryTemplate **name** not file (declare it if the mesh exists, else delete the ref too);
  `networkableInfo X` with no `NetworkableInfo.createNewInfo X`. → resolve both directions, declare all
  infos rather than reasoning which matter. Anchor the regexes (DC writes `rem GeometryTemplate.file`).
- **C3 Never redefine a name BFV owns** (`Tracer_Projectile`, `Browning_Projectile`) — a level loads
  after stock, so your copy *replaces* stock globally. → drop your def, let stock stand; refs resolve by name.
- **C4 Dependency closure or you ship amputated vehicles** (a prune once removed `TOW`, `50cal_Projectile`
  — the things the vehicle *is*). → transitive closure of referenced-and-undefined templates, **bounded**
  by an exclusion list (following every ref dragged in an AC-130 + 27 MB the map never spawns).
- **C5 Magnitudes, not just names.** `cullRadiusScale` ≤5 stock (port had 512 on projectiles),
  `hasDynamicShadow` ≤3, collision verts (median 66), gearbox band ≥0.3, mass by class, passenger
  PCO → **BodyInfo** not turret info, parent-class checks (`Turbulence`→Engine, `MusicPlayer`→Complex).
  → audit each against the census band; report, don't silently "fix".
- **C6 The 5-ton-truck lag is UNSOLVED** in the source notes (occupancy-only, camera-still). C's audits
  are hygiene, not a guarantee of smoothness — flag it as a known open class.

**Assets (A)**
- **A1 Collect from every namer:** `.rs` materials **and** emitter/particle `setTexture` **and** sound.
  A `.rs`-only collector ships untextured (flat-white) effects with "0 unresolved".
- **A2 `@RTD` sound trap.** `load @ROOT/Sound/@RTD/x.wav` → `@RTD` = the rate folder (`22khz`/`44kHz`).
  A path-char regex omits `@`, truncating to `RTD/x.wav`, shipping every wav one dir too deep — silent,
  total, and every "did we ship a wav per ref" audit passes. → compare against the **expanded** path;
  strip a leading `RTD/`. Resolve a wav across **all** archives and **all** rate folders; ship a
  `loadSoundScript` only if every wav resolves, else drop the line. Resolve patch-archive-first (a stub
  104-byte wav overridden by `_001`).
- **A3 Flatten = silent content swap.** `wheel.dds` exists in `DPV/` and `Pickup/`; a `basename→one path`
  index ships the wrong tyre, no audit fails. → hash-group by basename; any basename with >1 distinct
  hash gets disambiguated (`<folder>_<name>`) before flattening. Leave names the base game already ships
  alone. A capped texture legitimately mismatches its source — compare the collision set, not hashes.
- **A4 `.sm` material closure.** A `.sm` binds sections to `*_MaterialN` by name; a name the `.rs` omits
  renders untextured (DC's `STRYKER_Interior`, `Tow_Dummy`). → audit `*_MaterialN` (from `.sm`) vs
  `subshader "…"` (from `.rs`); clone a sibling subshader under the missing name.

**Lighting/materials (L)** — why a ported map looks wrong (all §5 of the port doc)
- **L1 Too dark:** BF1942 `renderer.globalAmbientColor` is gone in BFV, so a level inheriting BF1942's
  tuned-down `diffuseColor .3` renders at a fraction of light (stock is 0.85–1.0). *Dominant term, check
  first.* **L2 Materials too dark:** scale `materialDiffuse` by `1/min`, clamp 1. **L3 See-through
  walls:** `transparent true` alpha-*blends* in BFV → add `alphaTestRef 0.5` on textures with a real
  alpha channel.

**Text (T)**
- **T1 `.rs` missing semicolon** → loads on a dedicated server (no renderer), crashes the client
  mid-load. Gate every `.rs` line for a trailing `;`. **T2 bare-LF** in `.con` → parse failure; read/write
  bytes, don't let the runtime normalize newlines.

## 8. Diagnostic infrastructure to bake in (so the tool debugs itself)

- **Parse-only build mode** (`--parse-only`): ship all content, spawn nothing. Halves the search space
  in one in-game round (parse fault vs construct fault).
- **Server-vs-client is the fault-domain split:** a dedicated server parses `.con` logic but never loads
  materials/meshes/textures. The report should say which faults a headless dedicated-server load would
  catch (the CLAUDE.md rig: `bfvietnam_w32ded.exe`, ~25 s).
- **Substitution/prune log** for every non-1:1 decision, reviewable before ship.
- **Base-rate columns** in every audit: "you: 512, stock band: ≤5" beats "512 looks high".

## 9. Corpus census — RESULTS (20 mods, ~40,000 `.con` files)

Run with `rbridge condelta <Objects.rfa> --summary` over DesertCombat, DC_Final, FH, FHSW, interstate, EoD,
bg42, pr1942, XPack1, XPack2, Eastern_Front, bf1918, bfheroes, Empires, silentheroes, GC_Redux, Pirates,
Stunts, wwiireality, historia_bellorvm.

**1. The dialect delta generalises — it was not Desert-Combat-specific.** A first pass left only *three*
recurring unruled commands, each settled by asking both executables, now rules:

| command | evidence | action |
|---|---|---|
| `game.addLanguageRunTimeDirectory` | BF1942 ✓ / BFV ✗ — **19 of 20 mods** | drop |
| `NetworkableInfo.setHasOrientation` | absent from **both** — 17 of 20 mods | drop while cleaning |
| `NetworkableInfo.setIsControlledBy` | absent from **both** — 9 of 20 mods | drop while cleaning |
| `render.beginGlobalCluster` / `endGlobalCluster` | BF1942 ✓ / BFV ✗ — the `render.` spelling | drop |
| `kitTemplate.allowedAllied` / `allowedAxis` | BF1942 ✓ / BFV ✗ | drop |

**After adding those five rules the census reports ZERO unruled commands across all 20 mods.** The remaining
one-offs are typos in the mods' own content, and the analyzer now says so rather than crying wolf: Pirates ships
`setPBlueictionMode` (that is `setPredictionMode` after a global Red→Blue search-and-replace) and GC_Redux ships
`weaponTemplate.deviationcorrection`, both absent from *both* engines — dead weight the port inherits, not
something BFV took away. Worth noting the oracle is **more precise than `grep`** here: `deviationCorrection`
matches as a substring of a longer identifier, so a text search calls it present while the identifier-level
oracle correctly calls it absent.

**2. TreeMesh is the real structural problem, and it is concentrated.** Geometry declarations per mod:
FH **190**, FHSW **185**, pr1942 **112**, wwiireality **112**, interstate **109**, bf1918 68, bg42 62,
EoD 49, DesertCombat 40, silentheroes 20, GC_Redux 19, XPack2 12, XPack1 6, bfheroes 4, Eastern_Front 1 —
and **zero** for DC_Final, Empires, Pirates, Stunts, historia_bellorvm. So the vegetation converter is
essential for the FH family and irrelevant for several others; map-only mods with no `.tm` are the easiest
first targets.

**3. The `setX→X` family is enormous — and is now mostly solved.** Per mod: FHSW **151,010** lines,
bg42 69,223, FH 39,031, bf1918 19,282, GC_Redux 17,433, EoD 15,683. `StockCensus` (M0b, above) now decides
**~89–94 %** of them from retail evidence; the remainder are reported with the reason the census could not
settle them, and left exactly as written.

### Still open
- **Which mods to target first.** Map-only, `.tm`-free mods are the cheapest; the mods with existing BFV
  precedents (WW2MOD_X, Battlegroup42, PoE, EoD) are the best cross-checks.
- **Vehicle-heavy vs map-only:** map-only mods finish at M4; vehicle mods wait for M5.

## 9b. VALIDATED ENGINE CONSTRAINTS — learned in game, 2026-09-20

These came out of five in-game rounds on Berlin and Bocage, read off the **debug executable's own log**
(`Mods/BfVietnam/Logs/Debug_*.log`). None of them are in the Al Nas notes; several contradict a reasonable
reading of them. **This is the part the eventual software has to encode.**

### The two oracles that beat everything else

1. **The debug exe names the file, the line and the assert.** It is the single best diagnostic in this whole
   project. Read it FIRST. Retail swallows all of it.
2. **The engine hands you the setX answer for free**, per class, at runtime:
   `Error: Core: Don't use 'set' on properties any longer, instead use: ObjectTemplate.minimapIcon`
   (also `attachToListener`, `pcoId`, `controlPointIcon`, `scopeIcon`, `sniperSight`, `ticketIcon`,
   `teamFlagIcon`, the soldier icons, `hasCollisionPhysics`, and `game.customGame*`). That is more
   authoritative than either the exe string table or the retail census, because it is the live registry.
   **A future tool should parse these lines out of a log and feed them back as rules.**

### Hard engine rules (each cost a broken run to learn)

| rule | evidence | consequence if broken |
|---|---|---|
| **A level `.rfa` may contain NOTHING outside `BfVietnam/levels/<Level>/`** | `IoFile: Error loading file list` + `extractFilePath animations/` vs `m_archivePath BfVietnam/Levels/Bocage_E/` | the **whole archive** is rejected — the game does not start at all |
| **BFV asserts on a missing terrain patch texture; BF1942 tolerates it** | `GeomPatchTerrain/Patch.cpp(188)` ×60 | level will not load. BF1942's Berlin ships 4 of 64 tiles and is fine in its own game |
| **A duplicate `create` is rejected AND deactivates the active template** | `Failed to create geometryTemplate:Ammobox_m1 because it already exists!`; `... Deactivates active template!` | every following line in that file fails (102 + 117 errors from ~6 duplicates) |
| **Vehicles are never "placed"** | `WorldObjTemplBF: Cannt find "Willy"` | an embedder that walks only `StaticObjects.con` ships the scenery and no vehicles; they are named by `setObjectTemplate` in the game-mode files |
| **Skeletons/skins are named outright** | `ObjectTemplate.createSkeleton animations/Colt.ske`, `GeometryTemplate.setSkin animations/X.skn` | keying them off the mesh name finds none of them |
| **A level archive apparently cannot supply skeletons at all** | present at `<level>/animations/`, still "not found"; archive-root is forbidden (rule 1) | UNSOLVED — they likely belong in a mod-level `animations.rfa`, i.e. a companion archive, not the level |

### Where the cheap rigs stop working

* **The headless dedicated server has NO RENDERER**, so it never loads textures, materials or meshes. It is
  excellent for parse/construct faults and **structurally blind** to asset faults. ~17 rounds of server
  bisecting "eliminated" everything while the real fault (missing terrain tiles) was invisible to it.
* **Control first, always.** Porting retail Hue through the *entire* pipeline and watching it load proved the
  archive writer, repathing, dialect engine and growth generation were all sound in one run, and moved the
  search to BF1942 content. That single control was worth more than a dozen subtractive tests.
* **Removing a definition creates danglers**, so the test measures a NEW fault. Remove the reference and the
  definition together, or neither. (Cost two useless rounds: dropping `run objects/Objects` left 326
  placements undefined; dropping the Flag templates left their references dangling.)
* **Check the installed artifact's mtime against the log's** before concluding a fix failed.

### Oracle gotchas worth encoding

* The exe table holds **bare property names** (`setTextureParam`), never `Class.property`. Searching the
  dotted form returns zero in both binaries.
* A **twin fallback is misleading for rename decisions**: `HasCommand` reports `setPivotPosition` and
  `setMinimapIcon` as "present in BFV" via their twins when the exe plainly lacks them. Use exact lookups.
* Retail saying nothing is **not** the engine saying no: BFV ships no cloud blocks, so a corpus-only rule
  "proved" `Cloud.setName` → `Cloud.name`, and the exe carries `setName`. Confirm corpus-only renames
  against the exe.
* `WriteFile` is the new-archive API and silently loses four container fields (descriptor, per-entry
  trailers, TOC tail, compressed flag). Always repack from a donor.

### 9b-1. Patch archives — SOLVED (2026-09-20)

**A BF1942 level is not one archive. It is a base archive plus numbered patch archives, and each later one
replaces entries in the earlier ones.** Reading only the base silently uses stale content.

* **Naming**: `<Level>_NNN.rfa` beside `<Level>.rfa`. The numbers are **game patch versions**, so they are
  neither contiguous nor bounded — retail levels ship `_000`, `_003`, `_006`; others use `_001`, `_009`.
  A hard-coded `_001.._003` list (which the asset pool had) misses most of them.
* **Order**: ascending, later wins; the base loses to all of them. **Proven by content, not inference** —
  Berlin's `Menu/init.con` exists three times and grows each step:
  base ends at `setLoadPicture Load/Eastern.tga` → `_003` adds `game.setMapId "BF1942"` → `_006` repoints
  `setLoadPicture` at `Menu/Berlin.tga`, *which `_006` itself ships*.
* **Scale**: of Berlin's 266 distinct entries, **21 overlap and 18 differ** — `Init.con`, every AI file,
  every Conquest/TDM/SinglePlayer file, `Menu/init.con`. `_006` also carries content the base does not have
  at all: the 8.4 MB `texture/Sky_Berlin_0N.dds` set and the menu art.
* **Universal**: every retail BF1942 level checked (Berlin, Bocage, Guadalcanal, Midway, Omaha_Beach, Kursk,
  Stalingrad) has the same `_000 → _003 → _006` chain. 68 of the 309 level archives in the `bf1942` mod are
  patches. **Retail BFV uses the convention too** (14 patch archives).
* **Case varies between archives** — `Bf1942/Levels/berlin/AI.con` in `_003` vs `bf1942/levels/Berlin/AI.con`
  in the base — so the merge map must be case-insensitive, as the engine is.
* **It fixes an error we had already seen.** The very first debug log showed
  `Error loading texture: "Menu/Texture/Load/Eastern"`; that is the *base* `Menu/init.con` pointing at art
  `_006` replaced. Merging the patches resolves it without touching the converter's texture logic.

Implemented: `LevelPorter.PatchArchivePaths` (enumerates and sorts by number) + `PortOptions.PatchArchives`
(entries resolved across base + patches, later winning), and `AssetPool.StandardSearchPaths` generalised to
enumerate whatever `_NNN` archives exist, highest first. Verified: Berlin's `AI.con` is now 1,970 bytes (the
`_003` version) where the pre-fix port shipped 1,512 (the base), and `Menu/init.con` is `_006`'s, repathed.

### 9b-2. Mod inheritance and mod archives — SOLVED (2026-09-20)

**"Which version of this file does the engine see?" is the central problem of porting, not a detail.** It has
two halves — patch archives (§9b-1) and mod inheritance — and both are *declared*, so nothing needs guessing.

**Inheritance is spelled out in `Mods/<mod>/init.con`:**

```
game.addModPath Mods/DC_Final/          <- first listed = highest priority
game.addModPath Mods/DesertCombat/
game.addModPath Mods/BF1942/
game.customGameFlushArchives 0
```

* **Chains are enumerated in full**, never transitively — `Eastern_Front` lists all four of
  `Eastern_Front → DC_Final → DesertCombat → BF1942` itself. So a reader just follows the list.
* **Follow the order literally.** It is *not* always "own mod first, base game last": `pr1942` declares
  `pr1942 → bf1942 → pr1942/XPack1`, putting a **nested** path *below* the base game.
* **Case varies** — FHSW writes `game.addmodPath`. Match case-insensitively.
* Observed depths: most mods 2 (`<mod> → bf1942`), FHSW and DC_Final 3, Eastern_Front 4.
* Battlefield Vietnam uses the identical mechanism (`echo → BfVietnam`).

**Mod-level archives follow the same `_NNN` rule as levels, and patches both ADD and REPLACE:**

| pair | overlapping entries | differing |
|---|---|---|
| `texture.rfa` vs `texture_001.rfa` | 0 of 1,676 | — (purely additive, 898 new) |
| `standardMesh.rfa` vs `StandardMesh_001.rfa` | 0 of 2,902 | — (purely additive) |
| `sound.rfa` vs `sound_001.rfa` | 51 | **46 replaced**, every one larger |

That last row is the porting notes' own warning made concrete: a placeholder in the base archive, the real
clip in the patch. Resolve patch-archive-first or the level ships silence.

Implemented: `AssetPool.ModChain` reads the chain from `init.con` (falling back to `{mod, bf1942}`), and
`StandardSearchPaths` walks it, each mod's patch archives highest-number-first then its base. Effect on
DC_Al_Nas with only `--embed DC_Final`: the chain resolves to `DC_Final → DesertCombat → BF1942`
automatically, the pool grows to 7,715 templates / 2,643 meshes / 98 skeletons, the `DC_Al_Nas_001` level
patch is picked up, and **729/729 placements resolve** — where the first attempt at this level, before any
of this, kept 149.

### Still open questions for the research

1. **Skeletons** — companion archive vs mod `animations.rfa`. Untested.
3. The remaining per-class `setX` family (`setInputId`, `setAmomBar*`, `setKitTeam`, `startoneffects`) where
   the exe carries the set spelling but the engine rejects it — the log-mining oracle above is the answer.
4. `Couldn't decompress block (-6)` ×3, and textures that resolve in no archive (`RUBBLE_5`).
5. Terrain tile **generation** for sources with an incomplete grid.

## 10. First code increment (when tokens allow)

**M0 + M1 together** — the `.con` dialect engine, because A is the foundation B/C/D all import, and it is
100% offline-verifiable today against `DC_Al_Nas.rfa` (the exact ground truth) and the two real exes.
Concretely: `ExeSymbolTable`, `ConDialect.Analyze/Rewrite`, `condelta`/`conconvert`, and three gate
tests (documented-rewrite match, idempotence, CRLF/rem safety). That alone replaces `con_delta.py` with a
typed, tested, RFA-aware tool and de-risks everything downstream.

---
*Ground truth for every claim: `Al Nas to Vietnam/docs/BF1942_to_BFV_PORTING.md` and
`work/analysis/bfv_delta.md`. Reuse targets verified against `RefractorForge.Formats` on 2026-09-19.*
