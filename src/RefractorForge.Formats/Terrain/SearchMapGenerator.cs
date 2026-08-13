namespace RefractorForge.Formats.Terrain;

/// <summary>One static object's pathfinding footprint in world space: a blocking disc plus its height.</summary>
public readonly record struct ObjectFootprint(float WorldX, float WorldZ, float Radius, float Height);

/// <summary>How a vehicle treats water/slope/objects when its AI search map is generated.</summary>
public enum NavMode { Land, Water, Fly }

/// <summary>
/// One AI search-map's passability parameters — mirrors an <c>ai.addSearchMap</c> entry in
/// <c>AIpathFinding.con</c> (see <see cref="Con.NewMapGameplay.AiPathFindingCon"/>).
/// </summary>
public sealed record SearchMapParams(
    string Name, int MapNum, NavMode Mode,
    float WaterDepth, float MaxSlopeDeg, float Brush, float LowClip, float HiClip,
    IReadOnlyList<int> LevelSet)
{
    /// <summary>The base name without the trailing map number (e.g. "Tank0" -> "Tank"), used for the
    /// per-vehicle companion files <c>&lt;base&gt;.raw</c> / <c>&lt;base&gt;Info.raw</c>.</summary>
    public string BaseName => Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    /// <summary>The stock 7 BFV search maps (Operation_Irving values), matching the AIpathFinding.con writer.
    /// Per the retail folders (Marshall_island, Pearl Harbor): LAND vehicles ship levels 0,1,2 (fine, for precise
    /// building navigation); WATER vehicles ship levels 2,3,4,5 (coarser — open water needs less detail). The
    /// finest level (0) = the map's pathmap size; each level halves the resolution.</summary>
    public static IReadOnlyList<SearchMapParams> Standard { get; } = new[]
    {
        new SearchMapParams("Tank0",         0, NavMode.Land,  0f,    30f, 3.0f, 0.3f, 2.5f, new[] { 0, 1, 2 }),
        new SearchMapParams("Infantry1",     1, NavMode.Land,  1.5f,  40f, 1.0f, 0.4f, 2.0f, new[] { 0, 1, 2 }),
        new SearchMapParams("Boat2",         2, NavMode.Water, 1.4f,  30f, 4.0f, 0.3f, 2.5f, new[] { 2, 3, 4, 5 }),
        new SearchMapParams("LandingCraft3", 3, NavMode.Water, 1.4f,  30f, 4.0f, 0.3f, 2.5f, new[] { 2, 3, 4, 5 }),
        new SearchMapParams("Car4",          4, NavMode.Land,  0f,    35f, 3.0f, 0.3f, 2.5f, new[] { 0, 1, 2 }),
        new SearchMapParams("Heli5",         5, NavMode.Fly,   0f,    20f, 4.0f, 2.0f, 4.5f, new[] { 0, 1, 2 }),
        new SearchMapParams("Amphibius6",    6, NavMode.Land,  5000f, 30f, 3.0f, 0.3f, 2.5f, new[] { 0, 1, 2 }),
    };
}

/// <summary>
/// Generates the AI navmaps (the cracked <c>&lt;Name&gt;Level&lt;L&gt;Map8Bit.raw</c> files) from terrain +
/// per-vehicle params + static-object footprints. This is the engine's GENERATE-from-terrain step
/// (createSearchMaps), which is what a fresh or edited map needs.
///
/// IMPORTANT (see the pathfinding RE notes): retail navmaps are then HAND-EDITED on top (the
/// <c>loadEdited8BitMaps</c> path), so they are NOT byte-reproducible from level data — calibration against
/// Operation_Irving shows the dry-land "blocked" structure is designer intent + engine internals, with water
/// the only robust terrain signal (~70% terrain-only ceiling). This generator produces the clean,
/// terrain-derived base, which is the correct output for an editor.
///
/// Format (validated vs Operation_Irving): one byte per cell, <c>0xFF</c> = passable, <c>0x00</c> = blocked.
/// Three hierarchical levels per vehicle; level L side = <c>materialSize &lt;&lt; (2 - L)</c> (L0 finest = ms*4,
/// L2 coarsest = ms). The grid is TRANSPOSED vs the heightmap (rot90: nav(x,y) samples world grid (y,side-1-x));
/// flip <see cref="NavOrientation"/> if an in-game test shows the AI mirrored.
/// </summary>
public static class SearchMapGenerator
{

    /// <summary>nav(x,y) -> world-grid mapping code. **0 = IDENTITY, the validated default**: the engine stores
    /// navmaps in direct world order (row = Z, col = X). Retail BOAT maps correlate ~95% underwater→passable ONLY
    /// under identity, consistently across BF1942 + BFV (the old rot90 guess wrote saved navmaps rotated 90°).
    /// Kept as one switch point should a future in-game test ever need a flip. 0=identity 1=yx 5=rot90 6=rot270 7=yx+rot180.</summary>
    public static int NavOrientation { get; set; } = 0;

    /// <summary>Upper bound on a navmap level's side. Without it, a large map (e.g. 32 km / materialSize 2048)
    /// would make an 8192² L0 navmap (~67 MB/file, ~1 GB for the set) and take tens of seconds to generate —
    /// so the finest level is capped here. Unchanged for materialSize ≤ 512 (L0 ≤ 2048 already), so Operation_Irving
    /// and the gates are byte-identical.</summary>
    public const int MaxLevelSide = 2048;

    /// <summary>The map's finest pathmap side (Level 0): materialSize*4, capped at <see cref="MaxLevelSide"/>.
    /// Maps to the small/medium/large convention (ms 256/512/1024 -> 1024/2048/4096).</summary>
    public static int FinestSide(int materialSize) => Math.Min(materialSize * 4, MaxLevelSide);

    /// <summary>Side (cells) of a vehicle map at the given level = FinestSide >> level (L0 finest, each level
    /// halves). At least 64 (one 64x64 block) for a valid compressed map; smaller would be invalid.</summary>
    public static int LevelSide(int materialSize, int level) => FinestSide(materialSize) >> level;

    /// <summary>The 8Bit filename for a vehicle map + level, e.g. <c>Tank0Level2Map8Bit.raw</c>.</summary>
    public static string FileName(SearchMapParams p, int level) => $"{p.Name}Level{level}Map8Bit.raw";

    /// <summary>The compressed (engine-form) filename for a vehicle map + level, e.g. <c>Tank0Level2Map.raw</c>
    /// (what <c>ai.loadMaps</c> reads).</summary>
    public static string CompressedFileName(SearchMapParams p, int level) => $"{p.Name}Level{level}Map.raw";

    /// <summary>Bilinearly-sampled terrain height (metres) at world (wx,wz); clamps to bounds.</summary>
    public static float SampleHeight(TerrainConfig cfg, Heightmap hm, float wx, float wz)
    {
        float sp = cfg.HorizontalSpacing;
        float fx = Math.Clamp(wx / sp, 0f, hm.Width - 1.0001f);
        float fz = Math.Clamp(wz / sp, 0f, hm.Height - 1.0001f);
        int x0 = (int)fx, z0 = (int)fz, x1 = Math.Min(x0 + 1, hm.Width - 1), z1 = Math.Min(z0 + 1, hm.Height - 1);
        float tx = fx - x0, tz = fz - z0;
        float h00 = cfg.HeightToMeters(hm[x0, z0]), h10 = cfg.HeightToMeters(hm[x1, z0]);
        float h01 = cfg.HeightToMeters(hm[x0, z1]), h11 = cfg.HeightToMeters(hm[x1, z1]);
        return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
    }

    /// <summary>
    /// Build one vehicle's blocking map at one level in WORLD-GRID order (gx -> +X column, gy -> +Z row):
    /// one byte per cell, <c>0x00</c> = passable / <c>0xFF</c> = blocked. This is the editor-facing form — same
    /// orientation as the terrain + material map, so the AI Path painter can edit it and drape it as an overlay.
    /// The on-disk nav rotation is applied only by <see cref="EmitNav"/> when writing. <paramref name="objs"/>
    /// may be null/empty (terrain-only).
    /// </summary>
    public static byte[] GenerateGrid(TerrainConfig cfg, Heightmap hm, SearchMapParams p, int level,
                                      IReadOnlyList<ObjectFootprint>? objs = null)
    {
        int side = LevelSide(cfg.MaterialSize, level);
        float mpc = (float)cfg.WorldSize / side;          // metres per nav cell at this level
        float water = cfg.WaterLevel;

        // sampled height per world-aligned cell (gx -> +X / column, gy -> +Z / row).
        var heights = new float[side, side];
        for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
                heights[gx, gy] = SampleHeight(cfg, hm, (gx + 0.5f) * mpc, (gy + 0.5f) * mpc);

        // 1) local terrain passability per vehicle mode.
        var blocked = new bool[side, side];
        for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
            {
                float depth = water - heights[gx, gy];    // > 0 underwater
                blocked[gx, gy] = p.Mode switch
                {
                    NavMode.Water => depth < p.WaterDepth * 0.5f,                           // boats need enough water to float
                    NavMode.Fly   => Slope(heights, gx, gy, side, mpc) > p.MaxSlopeDeg,     // helis: only true cliffs
                    _             => depth > p.WaterDepth                                    // land: too-deep water...
                                     || Slope(heights, gx, gy, side, mpc) > p.MaxSlopeDeg,  // ...or too steep
                };
            }

        // 2) static-object footprints block ground vehicles (taller than lowClip).
        if (p.Mode != NavMode.Fly && objs is { Count: > 0 })
            foreach (var o in objs)
            {
                if (o.Height < p.LowClip) continue;
                int cx = (int)(o.WorldX / mpc), cy = (int)(o.WorldZ / mpc);
                int rc = Math.Max(0, (int)MathF.Round(o.Radius / mpc));
                int rc2 = rc * rc;
                for (int dy = -rc; dy <= rc; dy++)
                    for (int dx = -rc; dx <= rc; dx++)
                    {
                        if (dx * dx + dy * dy > rc2) continue;
                        int x = cx + dx, y = cy + dy;
                        if (x >= 0 && y >= 0 && x < side && y < side) blocked[x, y] = true;
                    }
            }

        // 3) brush clearance: dilate the blocked set by the vehicle's brush radius (separable max-filter).
        int br = Math.Max(0, (int)MathF.Round(p.Brush / mpc));
        if (br > 0) blocked = Dilate(blocked, side, br);

        // 4) emit in world-grid order. POLARITY (Cajunwolf/ED42 + retail ground truth): 0x00 = black = PASSABLE
        //    (bots CAN go); 0xFF = white = BLOCKED (cannot go).
        var grid = new byte[side * side];
        for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
                grid[gy * side + gx] = blocked[gx, gy] ? (byte)0xFF : (byte)0x00;
        return grid;
    }

    /// <summary>Apply the file nav orientation to a WORLD-GRID map (<see cref="GenerateGrid"/>'s form),
    /// producing the on-disk 8Bit cell order.</summary>
    public static byte[] EmitNav(byte[] grid, int side)
    {
        var outp = new byte[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                var (gx, gy) = NavToGrid(NavOrientation, x, y, side);
                outp[y * side + x] = grid[gy * side + gx];
            }
        return outp;
    }

    /// <summary>Inverse of <see cref="EmitNav"/>: recover the WORLD-GRID map from an on-disk nav-oriented 8Bit map,
    /// so a saved/loaded pathmap can be shown in the same orientation the AI Path painter uses.</summary>
    public static byte[] UnemitNav(byte[] navOriented, int side)
    {
        var grid = new byte[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                var (gx, gy) = NavToGrid(NavOrientation, x, y, side);
                grid[gy * side + gx] = navOriented[y * side + x];
            }
        return grid;
    }

    /// <summary>
    /// Generate one vehicle's binary 8Bit map at one level, in nav (on-disk) orientation. Equivalent to
    /// <c>EmitNav(GenerateGrid(...))</c> and byte-identical to the original generator. <paramref name="objs"/>
    /// may be null/empty (terrain-only, e.g. a freshly generated map).
    /// </summary>
    public static byte[] Generate(TerrainConfig cfg, Heightmap hm, SearchMapParams p, int level,
                                  IReadOnlyList<ObjectFootprint>? objs = null)
        => EmitNav(GenerateGrid(cfg, hm, p, level, objs), LevelSide(cfg.MaterialSize, level));

    /// <summary>Conservatively downsample a WORLD-GRID binary map (0x00/0xFF) to a smaller side: a coarse cell is
    /// BLOCKED if ANY covered fine cell is blocked. <paramref name="srcSide"/> must be a whole multiple of dstSide.</summary>
    public static byte[] DownsampleBlocked(byte[] gridFine, int srcSide, int dstSide)
    {
        if (dstSide == srcSide) return (byte[])gridFine.Clone();
        int f = srcSide / dstSide;
        var dst = new byte[dstSide * dstSide];
        for (int dy = 0; dy < dstSide; dy++)
            for (int dx = 0; dx < dstSide; dx++)
            {
                bool blk = false;
                for (int sy = dy * f; sy < (dy + 1) * f && !blk; sy++)
                    for (int sx = dx * f; sx < (dx + 1) * f; sx++)
                        if (gridFine[sy * srcSide + sx] == 0xFF) { blk = true; break; }
                dst[dy * dstSide + dx] = blk ? (byte)0xFF : (byte)0x00;
            }
        return dst;
    }

    /// <summary>From one hand-edited WORLD-GRID finest map, produce the 8Bit + compressed files for every level in
    /// the vehicle's level set (downsample to each level, then nav-orient + compress), PLUS the strategic companion
    /// pair. Pairs with the AI Path painter.
    ///
    /// The companions matter: the engine's search hops between 64x64 blocks through the portals in
    /// <c>&lt;Veh&gt;.raw</c> before running a local A*, so writing new level maps without rebuilding
    /// <c>&lt;Veh&gt;.raw</c> / <c>&lt;Veh&gt;Info.raw</c> leaves the coarse layer describing the terrain as it was
    /// BEFORE the edit. Emitting them here means every path that writes a navmap keeps the pair consistent.</summary>
    public static IReadOnlyList<(string FileName, byte[] Data)> EncodeVehicleLevels(SearchMapParams p, byte[] gridFinest, int finestSide)
    {
        var list = new List<(string, byte[])>();
        foreach (int lvl in p.LevelSet)
        {
            int side = finestSide >> lvl;
            if (side < CompressedSearchMap.BlockSize) continue;
            var grid = DownsampleBlocked(gridFinest, finestSide, side);
            var eight = EmitNav(grid, side);
            list.Add((FileName(p, lvl), eight));
            list.Add((CompressedFileName(p, lvl), CompressedSearchMap.Encode(eight, side, lvl)));
        }
        // The companions are derived from the world-grid map (the painter's own orientation), not from a
        // nav-oriented level map, so they describe the same world the level files do.
        if (finestSide >= StrategicMapGenerator.BlockFine && finestSide % StrategicMapGenerator.BlockFine == 0)
            foreach (var f in StrategicMapGenerator.EncodeCompanions(p, gridFinest, finestSide))
                list.Add((f.FileName, f.Data));
        return list;
    }

    /// <summary>Write one hand-edited vehicle's full level set into <c>&lt;levelDir&gt;/Pathfinding/</c>
    /// (both 8Bit + compressed). Other vehicles' files are left untouched. Returns the file count.</summary>
    public static int WriteVehicleEditedFolder(string levelDir, SearchMapParams p, byte[] gridFinest, int finestSide)
    {
        string dir = Path.Combine(levelDir, "Pathfinding");
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (var (file, data) in EncodeVehicleLevels(p, gridFinest, finestSide))
        {
            File.WriteAllBytes(Path.Combine(dir, file), data);
            n++;
        }
        return n;
    }

    static float Slope(float[,] h, int gx, int gy, int side, float mpc)
    {
        int xm = Math.Max(0, gx - 1), xp = Math.Min(side - 1, gx + 1);
        int ym = Math.Max(0, gy - 1), yp = Math.Min(side - 1, gy + 1);
        float dzx = (h[xp, gy] - h[xm, gy]) / ((xp - xm) * mpc);
        float dzy = (h[gx, yp] - h[gx, ym]) / ((yp - ym) * mpc);
        return MathF.Atan(MathF.Sqrt(dzx * dzx + dzy * dzy)) * 180f / MathF.PI;
    }

    static bool[,] Dilate(bool[,] src, int side, int r)
    {
        var tmp = new bool[side, side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                bool v = false;
                for (int dx = -r; dx <= r && !v; dx++) { int xx = x + dx; if (xx >= 0 && xx < side && src[xx, y]) v = true; }
                tmp[x, y] = v;
            }
        var dst = new bool[side, side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                bool v = false;
                for (int dy = -r; dy <= r && !v; dy++) { int yy = y + dy; if (yy >= 0 && yy < side && tmp[x, yy]) v = true; }
                dst[x, y] = v;
            }
        return dst;
    }

    /// <summary>nav(x,y) -> world grid (gx,gy) for the given orientation code (n = grid side).</summary>
    static (int gx, int gy) NavToGrid(int ori, int x, int y, int n) => ori switch
    {
        0 => (x, y),   // identity: on-disk nav is already world order (row=Z, col=X) — the validated default
        1 => (y, x),
        5 => (y, n - 1 - x),
        6 => (n - 1 - y, x),
        _ => (n - 1 - y, n - 1 - x),
    };

    /// <summary>nav(x,y) -> world grid (gx -> +X, gy -> +Z) under the active <see cref="NavOrientation"/>.
    /// Lets callers (tests, a viewer overlay) map a nav-file cell back to a terrain location.</summary>
    public static (int gx, int gy) GridForNav(int x, int y, int side) => NavToGrid(NavOrientation, x, y, side);

    /// <summary>Generate every vehicle's search maps — BOTH the engine compressed form (<c>...Map.raw</c>, what
    /// <c>ai.loadMaps</c> reads) AND the editor 8Bit form (<c>...Map8Bit.raw</c>) — for each level in the vehicle's
    /// level set (land 0-2, water 2-5), skipping any level whose grid would be smaller than one 64-cell block.</summary>
    public static IReadOnlyList<(string FileName, byte[] Data)> GenerateAll(
        TerrainConfig cfg, Heightmap hm, IReadOnlyList<ObjectFootprint>? objs = null,
        IReadOnlyList<SearchMapParams>? maps = null)
    {
        maps ??= SearchMapParams.Standard;
        var list = new List<(string, byte[])>();
        foreach (var p in maps)
            foreach (int lvl in p.LevelSet)
            {
                int side = LevelSide(cfg.MaterialSize, lvl);
                if (side < CompressedSearchMap.BlockSize) continue;     // too small for a valid (1-block) level
                var eight = Generate(cfg, hm, p, lvl, objs);
                list.Add((FileName(p, lvl), eight));                                                      // editor 8Bit
                list.Add((CompressedFileName(p, lvl), CompressedSearchMap.Encode(eight, side, lvl)));     // engine compressed
            }
        return list;
    }

    /// <summary>Generate + write all 8Bit maps to <c>&lt;levelDir&gt;/Pathfinding/</c>. Returns the file count.</summary>
    public static int WriteFolder(string levelDir, TerrainConfig cfg, Heightmap hm,
                                  IReadOnlyList<ObjectFootprint>? objs = null)
    {
        string dir = Path.Combine(levelDir, "Pathfinding");
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (var (file, data) in GenerateAll(cfg, hm, objs))
        {
            File.WriteAllBytes(Path.Combine(dir, file), data);
            n++;
        }
        return n;
    }
}
