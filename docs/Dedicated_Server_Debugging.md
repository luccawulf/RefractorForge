# Why a map loads in the editor and kills a dedicated server

Written after five rounds of static auditing failed to find a crash that a twenty-five second experiment found
immediately. The order of this document is the order the work should have happened in.

## 0. Find the log, then reproduce it. Do not read the map first.

**Logs.** `Mods/<mod>/Logs/{BfLog,Debug,Log}_<HOST>*.log` and `<install>/logs/`. A stale log still names real
defects. Ask which executable wrote it: the DEBUG build prints asserts that the retail build dies on silently
(`BfVietnam_DEBUG.exe` vs `BfVietnam.exe` - see the missing-mesh note in `Validated_Format_Facts.md`).

**Reproduce.** `bfvietnam_w32ded.exe` ships with the game, is headless (no `d3d9.dll` import) and gives a
crash / no-crash answer in about 25 seconds. That beats any amount of reading.

```
Mods/BfVietnam/settings/maplist.con        game.addLevel <level> GPM_CTF <mod>
                                           game.setCurrentLevel <level> GPM_CTF <mod>
Mods/BfVietnam/settings/ServerSettings.con game.serverGamePlayMode GPM_CTF
                                           bfvietnam_w32ded.exe +restart 1
```

- **The third argument of `addLevel` selects the MOD.** `+game <mod>` on the command line is ignored - it will
  quietly run the base game on Raceway and rewrite `maplist.con` while it does.
- **PASS** = the mod's `BfLog_<HOST>.log` contains a `GameStart:` section and a `Level:` line, and the process
  stays up. **FAIL** = it exits in ~1.5 s and the log stops after `SystemInfo`.
- Always run a **control** - a level you know works, in the same mode - before believing a result.
- The server rewrites `maplist.con`; back up every settings file you touch and restore it afterwards.

Bisect by editing one file and repacking (`RepackToFile`, never `WriteFile`). Strip the archive first
(`ServerSide.Strip`, `tiles strip`) - it drops textures, lightmaps and sounds, which a server never reads, and
turns a 283 MB repack into an 8 MB one without changing the outcome.

## 1. `water.*` must come AFTER `run Init/Terrain`

**The terrain creates the water object.** A `water.*` line above `run Init/Terrain` is applied to nothing and
kills the dedicated server during load - it exits before it logs `GameStart`.

Measured on BfVietnam 1.21. The echo mod's `al_vietnas` had one stray `water.color` at `Init.con` line 31 with
the terrain run at line 97; the server died 1.5 s in. Moving that single line below the terrain run fixed it, and
the value never mattered - three different colours crashed identically, and removing the line entirely passed.

Retail is unanimous: `Fall_of_Saigon` runs the terrain at line 97 and sets every water key at 99-103. The same
map's own earlier, working build had terrain at 96 and water at 100-105.

**RefractorForge used to write this defect.** `EnvironmentSettings.PatchInitConLines` inserted every setting the
file did not already have directly after the last `renderer.*` line, so a map whose `Init.con` had no water block
came out with all four water keys above the run chain. Fixed: water keys are placed after the last correctly
placed water line, else immediately after the terrain run, else at the end of the file; and a stray water line
that is already too high is moved down, so re-saving a broken map repairs it. Gate:
`src/RefractorForge.Tests/InitConWaterOrderTests.cs`.

The `waterBelowTerrain.*` block (tunnel water) is positioned relative to the `water.*` lines, so it inherits the
same rule.

## 2. Static audits: what they catch, and what they structurally cannot

These found four real defects in the same map. None of them was the crash - but all four were worth fixing, and
each is a class worth checking. Scripts live in the session scratchpad; the logic is simple enough to rewrite.

| check | what it finds | trap |
|---|---|---|
| every `run <file>` resolves to an archive entry | a whole block of the level never loaded | **`run` resolves against the running file's OWN folder first**, then the level root. And **run a retail control**: Con Thien misses 2, Hastings 6, Ho Chi Minh Trail 6, all benign (`growth/overGrowth` is missing from every shipped level). A miss is only a finding when retail has no counterpart. |
| every referenced `ObjectTemplate` is defined | `Object.create` naming a template that does not exist | check the level's own cons **plus** the mod's **plus** the base game's |
| every `GeometryTemplate.file` has a `.sm` **and a `.rs`** | a mesh whose shader does not exist - the retail exe has no guard and dies | checking only for the `.sm` walks straight past it. `Init/Terrain.con`'s `GeometryTemplate.file Heightmap` is a false positive |
| line endings | `StaticObjects.con` written with CR-only terminators is one 249 KB line to an LF reader | retail is 100% CRLF; scan `.con`/`.inc`/`.ssc`, not one file |
| per-mode kit resolution | see below | |

**The one they cannot catch is ORDERING.** Every reference in al_vietnas resolved; the file was present; it just
ran in the wrong order. Only execution shows that.

## 3. Walk the level per GAME MODE, not as a flat archive

The mode the server runs decides which half of a level's scripts execute. `Conquest.con` and `ctf.con` are
alternatives - CTF never runs `Conquest.con`. A template created only in the conquest chain does not exist in a
CTF round, and a flat "does everything resolve somewhere" audit sees nothing wrong, because the file *is* in the
archive.

al_vietnas declared 16 custom kits in `Init.con` and created them from `Conquest/CustomKits.con`, which only
`Conquest.con` ran:

```
GPM_CQ  (conquest) chain=Conquest.con  templates created: 258  kit slots: 16  UNRESOLVED: 0
GPM_CTF (ctf)      chain=ctf.con       templates created: 242  kit slots: 16  UNRESOLVED: 16
```

Read the server's `ServerSettings.con` and `maplist.con` **before** auditing the map, to know which mode matters.

## 4. Things that were ruled out, so nobody re-checks them

Each of these was a plausible theory, tested with the rig above, and disproved:

- **Archive size.** A server-stripped 8 MB copy died identically to the 283 MB original.
- **Level folder case.** `Al_Vietnas` vs `al_vietnas`: the old build passes under either requested name, the
  broken one fails under both. The engine's in-archive lookup is case-insensitive on every platform DICE shipped -
  retail relies on it (`run objects/Objects.con` against an entry named `Objects/`; Hastings ships 25 such
  mismatches).
- **Tunnel settings.** `isTunnelMap`, `useBelowGroundCulling`, `entryPointRadius`, `mapManager.addObjectMap`,
  `drawWaterBelowTerrain` - removing any or all changed nothing.
- **StaticObjects.con.** Emptying it entirely changed nothing.
