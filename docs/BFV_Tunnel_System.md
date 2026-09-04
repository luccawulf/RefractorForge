# The Battlefield Vietnam 1.2 tunnel system, fully mapped

Everything below comes from three sources, cross-checked: the two retail-shipped levels that use the system
(`Operation_Cedar_Falls.rfa`, DICE's; `Saigon68.rfa`, the sewer map), the object templates in `objects.rfa`, and
the engine itself — strings in `BfVietnam.exe` and decompiled functions in the Linux server binary
(`bfv_linded.static`, which ships with symbol names). Battlecraft Vietnam's tunnel tool writes some of this and
gets the rest wrong; the last section says exactly what.

## 1. The four parts

A working tunnel needs all four. Each one is something the engine reads; none is optional.

### 1a. Holes in the terrain — heightmap samples of exactly 0

The engine has no hole primitive. A **terrain vertex whose height sample is exactly 0** is the hole: with
`Game.isTunnelMap 1`, every triangle touching that vertex is neither drawn nor collided, and a soldier drops
through. The vertex is the one **nearest the entrance point** (the object's `entrance` child — see 1c), not the
object's origin.

| level | entrance | hole vertices (grid, 4 m cells) | terrain around |
|---|---|---|---|
| Cedar Falls | `o_Tunnel_Hut_m1` (958.4, 1110.1) | (239,278) (240,278) | 25.3 m |
| Cedar Falls | `o_Tunnel_Hole_m1` (1075.8, 1168.5) | (269,292) | 17–22 m |
| Cedar Falls | `o_tunnel_Bunker_M1` (1147.6, 895.5) | (287,224) | 15.3 m |
| Saigon68 | `O_sewers_stairs_m1` (356.1, 445.7) | (89,111) (90,111) | 10.3 m |
| Saigon68 | `O_sewers_stairs02_m1` (468.5, 314.5) | (115,79) (116,79) | 10.3 m |
| Saigon68 | `o_sewers_hut_M1` (671.9, 413.4) | (172,107) (172,108) | 10 m |
| Saigon68 | `o_sewers_drainage_M1` (672.6, 323.7) | (170,82) (171,82) | 11–13 m |

Proof that height 0 is special and not merely "a deep pit": `PatchTerrain::getWaterLevel(x, z, y)` in the engine
tests `terrainHeight(x, z) == 0` as one of its two "this point is underground" conditions (§3). Ordinary maps
also carry 0-samples (Landing Zone Albany has 41 along a river bank), so the hole behaviour is gated on
`isTunnelMap`; RefractorForge only cuts the terrain in the viewport when the level has it on.

### 1b. The tunnel — an ordinary object flagged `isBelowGround`

```
ObjectTemplate.create Bundle o_tunnelsA
ObjectTemplate.geometry o_tunnelsA_M1
ObjectTemplate.hasMap 1
ObjectTemplate.HasCollisionPhysics 1
ObjectTemplate.isBelowGround 1
```

`o_tunnelsA` (Cedar Falls) and `o_sewers_A_M1` (Saigon68) are the two retail tunnel meshes. Both are placed
at ground height with their corridors modelled below the origin (`o_tunnelsA_M1` spans y −16.7…+4.2 m;
`o_sewers_A_M1` y −6.4…+10). `isBelowGround 1` lets the engine cull it with `useBelowGroundCulling`;
`hasMap 1` says an underground map is bound to it (1d).

### 1c. Entrances — `isEntryPoint` and the `entrance` child

```
ObjectTemplate.create SimpleObject entrance
ObjectTemplate.isEntryPoint 1

ObjectTemplate.create Bundle o_Tunnel_Hut_m1
ObjectTemplate.geometry o_Tunnel_Hut_m1
ObjectTemplate.HasCollisionPhysics 1
ObjectTemplate.isEntryPoint 1
ObjectTemplate.addTemplate entrance
ObjectTemplate.setPosition .4/3/-1.4
```

The entrance templates: `o_Tunnel_Hut_m1` (entrance at +3 m, the trapdoor in the raised floor),
`o_Tunnel_Hole_m1` (three entrances at −9.1 m, the crater floor), `o_tunnel_Bunker_M1` (−10.6 m),
`o_tunnel_ladder_m1` (flagged itself, no child), and for sewers `O_sewers_stairs_m1` (+7.5),
`O_sewers_stairs02_m1` (+11), `o_sewers_hut_M1` (+14.2), `o_sewers_drainage_M1` (+12.4).

**How the switch actually fires** — `dice::ref2::world::ResponsePhysics::calculateTunnelSpecifics`, called from
`checkObjectVsObject` for the local player's soldier only:

1. The soldier must be **in collision contact** with the entrance object (any part of its bundle).
2. The engine walks the object's children; for each whose template carries the entry-point flag it takes the
   plain 3D distance from the soldier to that child.
3. If that distance is `< Game.entryPointRadius` (Cedar Falls 3.5, Saigon68 5), the soldier is switched to the
   below-ground state (terrain no longer collides, the below-terrain water applies, the underground map shows).

Two consequences. The entrance points are invisible in the game and in Battlecraft, so aligning them is
guesswork unless the editor draws them — RefractorForge does (Layer ▸ Tunnel entry points). And the soldier
has to be able to *reach* that sphere: if the river's surface lies across the shaft above it, they swim first.

`SoldierEntry` (`ObjectTemplate.create EntryPoint SoldierEntry`, `setEntryRadius 3`) is the soldier's own
entry-point object, attached in `CommonSoldierData.inc`; it is the same class vehicles use for seats.

### 1d. The underground map — `mapManager.addObjectMap`

```
mapManager.addObjectMap o_tunnelsA TunnelsAMap 886/871/328/327        (Cedar Falls)
mapManager.addObjectMap o_sewers_A_M1 SewersAMap 341.5/202.5/362/362  (Saigon68)
```

Template name, texture name (`Textures/<name>.dds` in the level, 512², DXT1 in retail, uncompressed works),
then `x/z/width/height` in world metres: the placed mesh's footprint. Texture x runs east, texture top is
north. One map per template; several instances of a template share it. Retail maps are hand-painted; a
top-down render of the mesh (what Battlecraft's "Generate Underground Map" did, and what RefractorForge's
Tunnels window does) gives the same corridors.

## 2. Init.con — the switches

```
Game.isTunnelMap 1
Game.useBelowGroundCulling 1
Game.entryPointRadius 3.5
```

`isTunnelMap` is the master switch; **Battlecraft writes `game.isTunnelMap 0` into every map it saves**, which
is the single most common reason a custom tunnel "does nothing". `useBelowGroundCulling` stops the surface
world being drawn while the camera is below ground (both retail tunnel maps set it). Related engine properties,
all on `Game`/`renderer`, that retail maps leave at their defaults: `occludeUndergroundDepth`,
`occludeUndergroundDepthFactor`, `occludeUndergroundDistanceMin/Max/Factor`, `controlPointUndergroundIcon`.
Console: `ShowEntryPoints`, `debugEntryPoints`.

Gameplay: spawn points inside the tunnel need
```
ObjectTemplate.allowSpawningBelowGround 1
```
on their `SpawnPoint` template (Cedar Falls sets it on 15 spawns, Saigon68 on 8). Control points down there
are ordinary control points.

## 3. The second water level — this is the part Battlecraft never writes

`PatchTerrain` owns **two** water bodies. Decompiled, `PatchTerrain::getWaterLevel(x, z, y)` — the function
`BFSoldier::updateSwimming` asks before starting `Lb_StartSwim` — does this:

```
if (template.drawWaterBelowTerrain)
    if (y < terrainHeight(x, z)  ||  terrainHeight(x, z) == 0)     // under the surface, or on a hole
        return waterBelowTerrain.level
return water.level
```

So with the flag off, the river fills every hole and every corridor at the river's height, and a soldier meets
it on the way down. With it on, everything under the surface uses its own level. Saigon68 is the only shipped
level that sets it, in `Init/Terrain.con`:

```
GeometryTemplate.drawWaterBelowTerrain 1
GeometryTemplate.waterLevel 7.5
GeometryTemplate.waterBelowLevel -7.1000
```

and in `Init.con`, right after the `water.*` block, a full colour block for the second body:

```
waterBelowTerrain.shallowColor 0.2/.1/.01
waterBelowTerrain.deepColor 0.5/.3/.01
waterBelowTerrain.waterAlphaDepth 0.400000
waterBelowTerrain.waterShallowAlpha 0.3
waterBelowTerrain.waterColorDepth 7.5
waterBelowTerrain.color .15/.15/.1
```

`waterBelowTerrain` is a console object registered beside `water` on the PatchTerrain template; it takes the
same properties as `water` and nothing else. Two things that crash or misbehave:

- `waterBelowTerrain.*` lines are only meaningful once `GeometryTemplate.drawWaterBelowTerrain 1` exists on the
  terrain template, and Init.con runs `run Init/Terrain` *before* the water blocks — keep that order.
- There is no `waterBelowTerrain.level`; the level is `GeometryTemplate.waterBelowLevel` in Terrain.con.

Cedar Falls, which does not set any of this, simply keeps its entire tunnel above the river (lowest floor
3.7 m, water 4.0 m). Saigon68 uses it to flood the sewers 1.8 m deep (floor −11.7, water −7.1). For a dry
tunnel under a high river, set `waterBelowLevel` below the tunnel floor.

RefractorForge writes the whole set from Window ▸ Tunnels ▸ *Tunnel water*: the two Terrain.con lines and a
`waterBelowTerrain.*` block mirroring the level's own `water.*` colours.

## 4. Checklist for a custom map

1. `Game.isTunnelMap 1`, `Game.useBelowGroundCulling 1`, `Game.entryPointRadius 3.5` in Init.con, above
   `run Init/Terrain`. (Delete Battlecraft's two dummy `mapManager.addObjectMap … tunnelmap 0/0/256/256` lines.)
2. Tunnel mesh (`o_tunnelsA` / `o_sewers_A_M1`) placed at ground height; entrances placed so their `entrance`
   children sit inside the tunnel's shafts.
3. A 0-height vertex under each entrance point (Tunnels ▸ *Punch holes under entrances*, or the Hole brush).
4. Water: either the whole tunnel above the river (Cedar Falls), or `drawWaterBelowTerrain 1` +
   `waterBelowLevel` below the floor + the `waterBelowTerrain.*` block (Saigon68).
5. `mapManager.addObjectMap <tunnel template> <MapName> x/z/w/h` + `Textures/<MapName>.dds`.
6. `allowSpawningBelowGround 1` on every spawn point inside.

## 5. What Battlecraft Vietnam does, for the record

Its "tunnel tool" is three unrelated pieces: the terrain mapper's *Tunnel Tool (',')* (a hole brush that writes
0-samples), *Use Underground Culling* in the Water Settings dialog (writes `useBelowGroundCulling`), and *Generate
Underground Map* in the in-game-map dialog (renders `tunnelmap.dds` and writes two `addObjectMap` lines with the
rectangle in **heightmap cells** rather than metres). It hard-codes `game.isTunnelMap 0`, and it knows nothing of
`drawWaterBelowTerrain`, `waterBelowLevel` or `waterBelowTerrain.*`. That is the whole gap.
