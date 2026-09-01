using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Validation;

/// <summary>
/// Can a bot standing at each spawn actually walk to each control point?
///
/// A flood fill over the finest navmap, seeded at the spawns. The generator that writes those navmaps is
/// from-scratch and has never been byte-compared to the engine's own, so "the map generated fine" says nothing
/// about whether the flags are connected - and the only other way to find out is to launch the game and watch
/// bots stand still. This answers it headlessly.
///
/// The grid is the editor's world-grid navmap: 0x00 = passable, 0xFF = blocked, cell (x, z) covers world
/// (x·mpc, z·mpc) with mpc = worldSize / side, identity orientation.
/// </summary>
public static class Reachability
{
    /// <summary>Flood fill from a set of seed cells, four-connected. Returns the reached mask.</summary>
    public static bool[] Flood(byte[] grid, int side, IEnumerable<(int X, int Z)> seeds)
    {
        var reached = new bool[side * side];
        var queue = new Queue<int>();
        foreach (var (sx, sz) in seeds)
        {
            var (x, z) = NearestPassable(grid, side, sx, sz, 6);
            if (x < 0) continue;
            int i = z * side + x;
            if (reached[i]) continue;
            reached[i] = true;
            queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % side, z = i / side;
            Visit(x - 1, z); Visit(x + 1, z); Visit(x, z - 1); Visit(x, z + 1);
            void Visit(int nx, int nz)
            {
                if (nx < 0 || nz < 0 || nx >= side || nz >= side) return;
                int n = nz * side + nx;
                if (reached[n] || grid[n] != 0) return;
                reached[n] = true;
                queue.Enqueue(n);
            }
        }
        return reached;
    }

    /// <summary>
    /// A spawn is often placed on a doorstep or a kerb that the navmap marks blocked; look outward a few cells
    /// for the nearest passable one rather than declaring the spawn itself unreachable.
    /// </summary>
    public static (int X, int Z) NearestPassable(byte[] grid, int side, int x, int z, int maxRing)
    {
        if (x >= 0 && z >= 0 && x < side && z < side && grid[z * side + x] == 0) return (x, z);
        for (int r = 1; r <= maxRing; r++)
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    if (grid[nz * side + nx] == 0) return (nx, nz);
                }
        return (-1, -1);
    }

    public static (int X, int Z) ToCell(float wx, float wz, float worldSize, int side)
    {
        float mpc = worldSize / side;
        return (Math.Clamp((int)(wx / mpc), 0, side - 1), Math.Clamp((int)(wz / mpc), 0, side - 1));
    }

    /// <summary>
    /// The report. Per team: seed at that team's spawns (its own flags' spawn groups) and check every control
    /// point. A flag is only worth capturing if it can be reached, and only the reaching team's spawns matter.
    /// </summary>
    public static LevelReport Check(byte[] grid, int side, float worldSize, EditableGameplay gp, string vehicleLabel)
    {
        var r = new LevelReport($"Bot reachability ({vehicleLabel})");
        if (grid.Length != side * side) { r.Add(IssueSeverity.Error, "Navmap", "grid size does not match its side"); return r; }

        int passable = grid.Count(b => b == 0);
        if (passable == 0)
        {
            r.Add(IssueSeverity.Error, "Navmap", $"the {vehicleLabel} navmap has no passable cells at all");
            return r;
        }

        // All spawns together first: the level-wide picture.
        var allSeeds = gp.SoldierSpawns.Select(s => ToCell(s.Position.X, s.Position.Z, worldSize, side)).ToList();
        if (allSeeds.Count == 0) { r.Add(IssueSeverity.Error, "Navmap", "no soldier spawns to seed from"); return r; }
        var reached = Flood(grid, side, allSeeds);

        int reachedCells = reached.Count(b => b);
        r.Add(IssueSeverity.Info, "Coverage",
            $"{100.0 * reachedCells / passable:0}% of passable ground is connected to a spawn " +
            $"({reachedCells:N0} of {passable:N0} cells)");

        foreach (var cp in gp.ControlPoints)
        {
            var (cx, cz) = ToCell(cp.Position.X, cp.Position.Z, worldSize, side);
            var (px, pz) = NearestPassable(grid, side, cx, cz, 8);
            if (px < 0)
                r.Add(IssueSeverity.Error, "Unreachable",
                    $"'{cp.Name}' is surrounded by blocked cells - no bot can stand on it", cp.Position);
            else if (!reached[pz * side + px])
                r.Add(IssueSeverity.Error, "Unreachable",
                    $"'{cp.Name}' cannot be reached from any spawn on the {vehicleLabel} navmap", cp.Position);
        }

        // Spawns that sit in a sealed pocket: a bot spawned there stands still for the whole round.
        foreach (var s in gp.SoldierSpawns)
        {
            var (sx, sz) = ToCell(s.Position.X, s.Position.Z, worldSize, side);
            var (px, pz) = NearestPassable(grid, side, sx, sz, 6);
            if (px < 0)
                r.Add(IssueSeverity.Warning, "Spawn", $"'{s.Name}' has no passable ground nearby", s.Position);
        }

        // Islands: passable regions no spawn reaches. Small ones are just courtyards; a big one is a whole
        // district bots will never enter.
        int island = 0;
        for (int i = 0; i < grid.Length; i++) if (grid[i] == 0 && !reached[i]) island++;
        if (island > passable / 20)
            r.Add(IssueSeverity.Warning, "Islands",
                $"{100.0 * island / passable:0}% of passable ground is disconnected from every spawn");

        return r;
    }
}
