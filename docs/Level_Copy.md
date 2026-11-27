# Copying and renaming a level (File ▸ Copy Level To…)

**The question (2026-11):** *"Can you use the RF tool to rename a map, like with Battlecraft?"* Battlecraft cannot
rename a level either. Its own notes say so: if you wish to change the name of a level you can't, but *Copy Level
To* saves the exact level under a different name. RefractorForge now has the same command, and renaming a map is a
copy followed by deleting the old one yourself.

## Where a level's name lives

A level's name is not just its archive's file name:

- **The folder its entries sit under.** Every entry of a packed level is stored under
  `<game>/levels/<Name>/…` (`BfVietnam/levels/Operation_Irving/Init/Terrain.con`). The game finds the level's files
  by that folder, so a renamed `.rfa` whose entries still say the old name is the old level under a new file name.
- **Paths inside the level's own files.** Surveyed over 105 levels and their patches (153 archives): the 84 levels
  in a Battlefield Vietnam install's `BfVietnam` mod folder, retail and community, and the 21 of a clean
  Battlefield 1942 install. A level spells its own folder out in exactly these places:
  - `Init/Terrain.con` — `GeometryTemplate.file`, `.materialMap`, `.texBaseName`, `.detailTexName`
    (`BfVietnam\levels\Operation_Irving\Heightmap`), in every level;
  - the growth palettes — `materialMapFilename` in `Growth/overGrowth.wst` and `underGrowth.wst` (BFV);
  - `Init.con` — a `textureManager.alternativePath` or a `ShaderManager.setTextureParam envmap` pointing into the
    level's own `Texture(s)` folder;
  - `Menu/init.con` — a `game.setLoadPicture` that reaches back into the level's own Menu folder
    (`../../bf1942/levels/battle_of_britain/menu/texture/Load/Britain_Load.tga`).
- **Not paths, and left alone:** `rem` comments in the gameplay files, lexicon keys (`BRIEFING_AXIS_HINTS_BERLIN`),
  a `game.setLoadPicture Load/Operation_Irving.dds` that names a picture in the mod's menu archive (still there for
  the copy to use), and **file names**: `env_Marshall_island_01.dds` or `Remagen_StoneBridge_M1.sm` are names the
  level's scripts ask for, so renaming the file would break the reference.

## What the copy does

`Formats/LevelCopy.cs`:

- **The name** must be one word the engine can read in a `.con` line: letters, digits, `_` and `-`, under 64
  characters (`LevelCopy.NameProblem` says why not).
- **A packed level** (`CopyArchive`): the `.rfa` and every numeric patch beside it (`<Name>_001.rfa`…, ordered by
  number) are merged last-wins, as the game layers them, into one new `<NewName>.rfa` beside the original. Every
  entry under the level folder moves to `<game>/levels/<NewName>/`. Entries are copied as their stored (compressed)
  data, nothing re-compressed, so even a large level copies quickly; the archive keeps the original's game binding
  (XPack id). It is written to a temporary file, flushed to the disk and then renamed into place. The editor's own
  files kept beside the archive (`<Name>.<sidecar>`: placed lights, object groups, notes, growth view settings)
  are copied under the new name.
- **A folder level** (`CopyFolder`): a sibling folder `<NewName>`, written as `<NewName>.rfcopy` first and renamed
  when complete. Its `Backups` folder and its project manifest (`.rfproj`, which names the original folder) stay
  with the original.
- **Paths re-pointed:** in the level's text files (`.con`, `.inc`, `.wst`, `.ssc`, `.rs`, `.txt`, `.xml`…), every
  path segment `levels/<OldName>` — either slash, any case — followed by a separator, a quote, a space or the end of
  the line becomes `levels/<NewName>`. `levels\Berlin_Last_Night\` is a different level and is not touched when
  copying `Berlin`. The old name is taken from the archive's folder, and also from the path `Init/Terrain.con`
  actually uses when the two differ (a level copied by hand without fixing its paths is repaired by the copy).
  Everything else in every file is kept byte for byte.
- **Never overwrites:** an existing `<NewName>.rfa` or folder is refused, and so is a stray `<NewName>_001.rfa` the
  game would layer over the copy as a patch.

The result lists the files copied, the paths re-pointed and how many other mentions of the old name were left as
they are, in the Log window. **Open the copy afterwards** relaunches the editor on the new level.

## Checked

`LevelCopyTests`: a packed level with a patch (entries moved, the patch merged, the four kinds of path re-pointed,
the menu-archive load picture and file names kept, sidecars copied, the original untouched, a second copy to the
same name refused); a folder level; the name rules; and retail Battlefield Vietnam's `Ho_Chi_Minh_Trail.rfa` with
its `_001` patch, copied and loaded back through the editor's own loader under the new name with the same terrain
and the same objects.
