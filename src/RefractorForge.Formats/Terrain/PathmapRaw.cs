namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Reads a Battlefield AI pathfinding search-map <c>.raw</c> for DISPLAY — the native equivalent of the community
/// <c>Import_Pathfind</c> + <c>raw2tga</c> tools. Handles both on-disk forms: the engine COMPRESSED map
/// (<c>&lt;Veh&gt;Level&lt;L&gt;Map.raw</c>, RLE per 64² block — decoded via <see cref="CompressedSearchMap"/>) and
/// the editor 8Bit map (<c>&lt;Veh&gt;Level&lt;L&gt;Map8Bit.raw</c>, one byte per cell). Returns the square 8Bit
/// grid (0x00 = passable, 0xFF = blocked) a viewer can draw directly.
/// </summary>
public static class PathmapRaw
{
    /// <summary>
    /// Decode a pathmap <c>.raw</c> to its square 8Bit grid. Detection: a filename containing "8bit" (or, when the
    /// name is unknown, a byte count that is an exact block-aligned square) reads as the uncompressed 8Bit form;
    /// a name ending in "map.raw" (or any other input) is decompressed with <see cref="CompressedSearchMap.Decode"/>.
    /// </summary>
    public static byte[] Load(byte[] data, string? nameHint, out int side)
    {
        string n = (nameHint ?? string.Empty).ToLowerInvariant();
        bool is8Bit = n.Contains("8bit");
        bool isCompressedName = !is8Bit && n.EndsWith("map.raw");
        int sq = IsqrtExact(data.Length);

        if (is8Bit)
        {
            side = sq;
            if (side <= 0) throw new FormatException($"8Bit pathmap of {data.Length} bytes is not a square grid.");
            return Normalize(data);
        }
        if (isCompressedName)
            return CompressedSearchMap.Decode(data, out side, out _);

        // Unknown name: an exact 64-aligned square is the 8Bit form; otherwise assume compressed.
        if (sq > 0 && sq % CompressedSearchMap.BlockSize == 0)
        {
            side = sq;
            return Normalize(data);
        }
        return CompressedSearchMap.Decode(data, out side, out _);
    }

    /// <summary>Force every cell to the canonical 0x00 passable / 0xFF blocked (retail 8Bit already is; any
    /// non-zero is treated as blocked so a stray value still displays sensibly).</summary>
    private static byte[] Normalize(byte[] data)
    {
        var eight = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) eight[i] = data[i] == 0 ? (byte)0x00 : (byte)0xFF;
        return eight;
    }

    /// <summary>Exact integer square root, or 0 when <paramref name="n"/> is not a perfect square.</summary>
    private static int IsqrtExact(int n)
    {
        if (n <= 0) return 0;
        int s = (int)Math.Round(Math.Sqrt(n));
        for (int k = s - 1; k <= s + 1; k++) if (k > 0 && (long)k * k == n) return k;
        return 0;
    }

    /// <summary>
    /// Load one vehicle's EXISTING navmap from the level, as the WORLD-GRID map the AI Path painter edits
    /// (0x00 pass / 0xFF block, row = Z / col = X, resampled to <paramref name="targetSide"/>). This is the
    /// "respect what the map already ships" path: retail and hand-tuned navmaps carry designer intent that a
    /// terrain regeneration would throw away, so the painter seeds from these and only generates when a level
    /// has none. <paramref name="readByLeafName"/> abstracts the storage (level folder or mounted .rfa chain);
    /// it receives leaf names like <c>Tank0Level0Map8Bit.raw</c> and returns the bytes or null.
    /// Levels are tried finest-first (land = L0, water vehicles ship L2 as their finest); the editor 8Bit form
    /// is preferred over the compressed engine form of the same level. Null when the level has no map at all.
    /// </summary>
    public static byte[]? LoadVehicleWorldGrid(Func<string, byte[]?> readByLeafName, SearchMapParams p, int targetSide)
    {
        foreach (int lvl in p.LevelSet.OrderBy(x => x))
        {
            foreach (var leaf in new[] { $"{p.Name}Level{lvl}Map8Bit.raw", $"{p.Name}Level{lvl}Map.raw" })
            {
                byte[]? data;
                try { data = readByLeafName(leaf); } catch { data = null; }
                if (data is null || data.Length == 0) continue;
                try
                {
                    var eight = Load(data, leaf, out int side);
                    var world = SearchMapGenerator.UnemitNav(eight, side);
                    return ResampleWorldGrid(world, side, targetSide);
                }
                catch { /* a malformed file falls through to the next candidate / generation */ }
            }
        }
        return null;
    }

    /// <summary>Resample a square world-grid blocking map to another side. Integer downscale keeps the
    /// conservative any-blocked-blocks rule; anything else is nearest-neighbour (fine for a paint seed).</summary>
    public static byte[] ResampleWorldGrid(byte[] grid, int side, int targetSide)
    {
        if (side == targetSide) return grid;
        if (side > targetSide && side % targetSide == 0)
            return SearchMapGenerator.DownsampleBlocked(grid, side, targetSide);
        var dst = new byte[targetSide * targetSide];
        for (int y = 0; y < targetSide; y++)
        {
            int sy = (int)((long)y * side / targetSide);
            for (int x = 0; x < targetSide; x++)
                dst[y * targetSide + x] = grid[sy * side + (int)((long)x * side / targetSide)];
        }
        return dst;
    }
}
