using RefractorForge.Formats.Validation;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Material index 7 is not a surface. The engine's own material table names it <c>deathMaterial</c>, and
/// <c>Game::isOutsideWorld(x, z)</c> - the one predicate behind the out-of-combat-area warning, the countdown and
/// <c>damageForBeingOutSideWorld</c> - reads <c>PatchTerrain::getMaterialAt(x, z) == 7</c> as "outside", exactly
/// as it reads a position past the combat-area rectangle. No Init.con setting reaches it.
/// <para>
/// Retail levels use it deliberately: it is the void around the island. Saigon68 paints 80 % of its map with it,
/// and every stock spawn sits on something else. A custom map that builds across that void without repainting
/// puts spawns on a surface the engine kills on - a Huey that "spawns in the right spot and blows up", a flag that
/// shows OUT OF COMBAT AREA to anyone who walks up to it - and nothing about the combat area, the tunnel map or
/// the objects will change that. Only the material map will.
/// </para>
/// </summary>
public static class DeathMaterial
{
    public const byte Index = 7;
    public const byte Water = 1;

    /// <summary>The material under a world position, the way the engine looks it up: cell = floor(x * cells / worldSize).</summary>
    public static byte At(MaterialMap map, TerrainConfig cfg, float wx, float wz)
    {
        float ws = cfg.WorldSize > 0 ? cfg.WorldSize : 1f;
        int cx = Math.Clamp((int)MathF.Floor(wx / ws * map.Width), 0, map.Width - 1);
        int cz = Math.Clamp((int)MathF.Floor(wz / ws * map.Height), 0, map.Height - 1);
        return (byte)(map[cx, cz] & 15);
    }

    public static bool IsDeath(MaterialMap map, TerrainConfig cfg, float wx, float wz) => At(map, cfg, wx, wz) == Index;

    /// <summary>How many cells inside the rectangle are deathMaterial, and how many cells the rectangle covers.</summary>
    public static (int Death, int Total) CountInside(MaterialMap map, TerrainConfig cfg, CombatArea area)
    {
        var (cx0, cz0, cx1, cz1) = Cells(map, cfg, area);
        int death = 0, total = 0;
        for (int z = cz0; z < cz1; z++)
            for (int x = cx0; x < cx1; x++)
            {
                total++;
                if ((map[x, z] & 15) == Index) death++;
            }
        return (death, total);
    }

    /// <summary>
    /// Replace every deathMaterial cell inside the rectangle with a real surface, leaving the cells outside it
    /// alone - they are the boundary. Below the water line the cell becomes water; above it, the nearest cell that
    /// already holds a land material, so roads, dirt and grass grow outward from what the mapper painted rather than
    /// everything turning into one flat colour. Returns the edit for undo, or null if nothing needed changing.
    /// </summary>
    public static MaterialEdit? Repaint(MaterialMap map, Heightmap? heightmap, TerrainConfig cfg, CombatArea area)
    {
        var (cx0, cz0, cx1, cz1) = Cells(map, cfg, area);
        if (cx1 <= cx0 || cz1 <= cz0) return null;
        int w = map.Width, h = map.Height;

        // Multi-source BFS from every land cell: the nearest land material for every cell on the map.
        var source = new byte[w * h];
        var dist = new int[w * h];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        for (int i = 0; i < w * h; i++)
        {
            byte m = (byte)(map.Samples[i] & 15);
            if (m != Index && m != Water) { source[i] = m; dist[i] = 0; queue.Enqueue(i); }
        }
        // A map with no land at all has nothing to grow from; dry grass is the retail default for open ground.
        byte fallback = 2;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, z = i / w;
            Visit(x + 1, z); Visit(x - 1, z); Visit(x, z + 1); Visit(x, z - 1);
            void Visit(int nx, int nz)
            {
                if (nx < 0 || nz < 0 || nx >= w || nz >= h) return;
                int j = nz * w + nx;
                if (dist[j] >= 0) return;
                dist[j] = dist[i] + 1; source[j] = source[i]; queue.Enqueue(j);
            }
        }

        int rw = cx1 - cx0, rh = cz1 - cz0;
        var before = new byte[rw * rh];
        var after = new byte[rw * rh];
        bool any = false;
        for (int z = cz0; z < cz1; z++)
            for (int x = cx0; x < cx1; x++)
            {
                byte cur = map[x, z];
                before[(z - cz0) * rw + (x - cx0)] = cur;
                byte next = cur;
                if ((cur & 15) == Index)
                {
                    next = GroundBelowWater(heightmap, cfg, map, x, z) ? Water : (dist[z * w + x] >= 0 ? source[z * w + x] : fallback);
                    any = true;
                }
                after[(z - cz0) * rw + (x - cx0)] = next;
            }
        if (!any) return null;
        var edit = new MaterialEdit(cx0, cz0, rw, rh, before, after);
        edit.Redo(map);
        return edit;
    }

    private static bool GroundBelowWater(Heightmap? hm, TerrainConfig cfg, MaterialMap map, int cx, int cz)
    {
        if (hm is null) return false;
        // The heightmap and the material map may differ in resolution; sample the height at the cell's centre.
        int hx = Math.Clamp((int)((cx + 0.5f) / map.Width * hm.Width), 0, hm.Width - 1);
        int hz = Math.Clamp((int)((cz + 0.5f) / map.Height * hm.Height), 0, hm.Height - 1);
        return cfg.HeightToMeters(hm[hx, hz]) < cfg.WaterLevel;
    }

    private static (int X0, int Z0, int X1, int Z1) Cells(MaterialMap map, TerrainConfig cfg, CombatArea area)
    {
        float ws = cfg.WorldSize > 0 ? cfg.WorldSize : 1f;
        int x0 = Math.Clamp((int)MathF.Floor(area.X / ws * map.Width), 0, map.Width);
        int z0 = Math.Clamp((int)MathF.Floor(area.Z / ws * map.Height), 0, map.Height);
        int x1 = Math.Clamp((int)MathF.Ceiling(area.X1 / ws * map.Width), 0, map.Width);
        int z1 = Math.Clamp((int)MathF.Ceiling(area.Z1 / ws * map.Height), 0, map.Height);
        return (x0, z0, x1, z1);
    }
}
