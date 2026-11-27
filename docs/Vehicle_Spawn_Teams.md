# Vehicle spawn teams: why a ship spawns the other team's soldiers

**The report (2026-11).** *"I set up an object spawn with a German SMSDresden ship in Battlefield 1918 and assigned
it to the Allies, but it still has Axis troops spawning in it."* Nothing was wrong with the spawner. The cause is
how Refractor assigns soldier spawns to teams, and it applies to every ship and carrier with spawn points of its
own, in Battlefield 1942, its mods and Battlefield Vietnam alike.

## How the engine decides

- A vehicle spawner (`ObjectTemplate.create ObjectSpawner`) gives a vehicle to a team: `ObjectTemplate.team` and
  `ObjectTemplate.setObjectTemplate <team> <vehicle>`. That decides **which vehicle** appears, and nothing else.
- A soldier spawn point (`ObjectTemplate.create SpawnPoint`) belongs to a **group**: `ObjectTemplate.setGroup N`.
- A group's team is declared with `spawnPointManager.group N` followed by `spawnPointManager.groupTeam T`.
- A control point owns a group through `spawnGroupId`, so its group changes hands with the flag. A group that no
  control point owns keeps the team it was declared with, whoever is in the vehicle.
- The spawn points built into a ship or a carrier (deck spawns, the carrier's island) are in exactly such groups,
  and those are declared once for the whole game in the mod's **`game/GlobalSpawnGroups.con`**, which the game runs
  at start-up. Both games ship one: the Enterprise's groups are team 2, the Shokaku's team 1.
- A level can add or redeclare groups in each game mode's `SpawnpointManagerSettings.con`, which runs after the
  global file (each mode's `.con` runs it first, then the soldier spawn templates and spawns, the object spawn
  templates, the control point templates, and on the host the object spawns and control points).

Battlefield 1918's `GlobalSpawnGroups.con` declares the SMSDresden's group 73 for team 1 (Axis): the ship carries
3 soldier spawn points in that group, and 2 built-in launch spawners (`SMSDresdenBarkasseSpawner`) set to team 1
as well. So an SMSDresden spawned for the Allies is an Allied ship whose deck spawns Axis soldiers and whose boats
are Axis boats. Changing the spawner's team cannot touch that, and neither can an `Init.con` edit.

Retail ran into the same thing and solved it with a second ship: Battlefield 1942's "Alternative Fletcher" is a
copy of the Fletcher whose spawn points use groups 80 and 81.

## What RefractorForge does

**The check.** For every vehicle spawn, the editor follows each vehicle it spawns (per team) through its
`addTemplate` children - read from the level and the mod's object scripts - to the spawn points and launch
spawners built into it, and compares their groups' teams (the global file, then the level's
`SpawnpointManagerSettings.con` for the mode being edited) with the team that gets the vehicle. A group that
nothing declares, or that is declared for team 0, is left alone: its team is not fixed.

- The Inspector shows an orange warning under the selected vehicle spawn, one line per problem: *"SMSDresden for
  Allies: its 3 soldier spawn point(s) are in spawn group 73, which bf1918/game/GlobalSpawnGroups.con gives to Axis - Axis
  soldiers spawn on it."* and *"SMSDresden for Allies: its 2 built-in spawner(s) SMSDresdenBarkasseSpawner spawn
  for Axis."*
- **Tools ▸ Check Map ▸ Validate Map** lists every such spawn under *Spawns*.

**The fix: Make a *team* copy of *vehicle*.** The button under the warning builds what DICE built for the Fletcher:

- A copy of the vehicle's root template and of every template on the way to the mismatched spawn points and
  spawners, each under the same name with `_T<team>` added (`SMSDresden_T2`). Every other part - hull, guns, LODs,
  effects - is shared with the original by name, which is how Refractor composes a vehicle anyway, so the copy is a
  few dozen lines, not the whole ship.
- In the copy, each `setGroup` points at a **new group** (numbered from 200, above every group already in use),
  and each built-in `ObjectSpawner`'s `team` is set to the new team.
- The new groups are declared for that team, keeping the original declaration's other lines (icon, flags), in
  **every game mode's `SpawnpointManagerSettings.con`**, and the templates go into every mode's
  `ObjectSpawnTemplates.con`. Each block opens with `rem RefractorForge team copy: SMSDresden_T2` and is written
  once; a file that already holds it is left alone.
- The spawner is switched to the copy for that team. It is one undo step, and Save writes it for a folder level
  and a packed `.rfa` alike.

The original vehicle is untouched, so other levels and the other team's spawns are unaffected.

## Related fixes in the same update

- A packed (`.rfa`) save never patched an existing spawner's vehicle lines, so changing which vehicle a team gets
  on a spawner that already existed was silently lost. It is patched now, as a folder save always did.
- A newly placed spawner writes a vehicle per team when the two teams get different vehicles.

Code: `Formats/Cons/SpawnGroups.cs` (`SpawnGroupTable`, `ObjectTemplateIndex`, `VehicleSpawnTeams`,
`TeamVehicleCopy`); tests: `SpawnGroupTeamTests`.
