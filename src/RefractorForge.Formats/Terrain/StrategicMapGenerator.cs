namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Builds the strategic pathfinding pair (<see cref="StrategicMap"/> + <see cref="StrategicInfoMap"/>) from a
/// vehicle's fine navmap, so that painting a navmap no longer leaves the coarse layer describing the old world.
///
/// The engine searches in two stages: a strategic hop between 64x64 blocks through the portals in
/// <c>&lt;Veh&gt;.raw</c>, then a local A* inside. For that to work, each block must be split into its genuinely
/// CONNECTED passable areas - two halves of a block separated by a wall are different regions even though they
/// share a cell - and every cell must know which region it is in (<c>&lt;Veh&gt;Info.raw</c>).
///
/// This is a faithful reimplementation, not a byte-identical clone: DICE's own labelling order is unknown, so a
/// regenerated pair will not match a shipped one byte for byte. What it does guarantee is internal consistency - every
/// portal sits on a passable cell of the region it names, every passable cell's info value indexes a used portal slot,
/// and each cell's link table (<see cref="StrategicMap.Links"/>) holds exactly the region contacts across its north and
/// east edges - which is what the strategic search walks: a region pair with no link bit is not a route, however open
/// the ground between them.
/// </summary>
public static class StrategicMapGenerator
{
    /// <summary>Fine navmap cells per strategic cell edge.</summary>
    public const int BlockFine = StrategicMap.BlockSide;   // 64

    /// <summary>The level the companion pair is written at. Retail ships land vehicles (levels 0,1,2) with a
    /// level-1 info map and water vehicles (2,3,4,5) with level 3 - in both cases the SECOND level of the set.</summary>
    public static int CompanionLevel(SearchMapParams p)
        => p.LevelSet.Count > 1 ? p.LevelSet[1] : (p.LevelSet.Count > 0 ? p.LevelSet[0] : 1);

    public sealed record Result(StrategicMap Table, byte[] InfoCells, int InfoSide, int Level);

    /// <summary>
    /// Decompose <paramref name="fine"/> (one byte per cell, <c>0x00</c> passable / <c>0xFF</c> blocked, side
    /// <paramref name="fineSide"/>) into the strategic pair for vehicle <paramref name="p"/>.
    /// </summary>
    /// <param name="infoLevel">The level of the info map: the search type's level + 1 (<c>ai.addSearchType Name map level</c>).
    /// Null takes <see cref="CompanionLevel"/> (the second level of the map's set).</param>
    public static Result Generate(byte[] fine, int fineSide, SearchMapParams p, int? infoLevel = null)
    {
        if (fine is null) throw new ArgumentNullException(nameof(fine));
        if (fine.Length != fineSide * fineSide)
            throw new ArgumentException($"fine map is {fine.Length} bytes, expected {fineSide}^2.");
        if (fineSide % BlockFine != 0)
            throw new ArgumentException($"fine side {fineSide} is not a multiple of {BlockFine}.");

        int level = infoLevel ?? CompanionLevel(p);
        int strategicSide = fineSide / BlockFine;
        int blockSide = StrategicInfoMap.BlockSideFor(StrategicInfoMap.BaseCellBits, level);
        int infoSide = strategicSide * blockSide;
        int scale = BlockFine / blockSide;            // fine cells per info cell (2 at level 1, 8 at level 3)
        if (scale < 1) throw new InvalidDataException($"level {level} is too fine for a {BlockFine}-cell block.");

        var table = new StrategicMap(strategicSide, strategicSide);
        var info = new byte[infoSide * infoSide];

        var label = new int[BlockFine * BlockFine];   // per-block scratch, reused
        var queue = new int[BlockFine * BlockFine];
        var regionCells = new List<List<int>>();
        // Each block's cells by portal slot (-1: blocked or in a region too small for a slot), kept for the links.
        var slots = new sbyte[strategicSide * strategicSide][];

        for (int by = 0; by < strategicSide; by++)
            for (int bx = 0; bx < strategicSide; bx++)
            {
                Array.Fill(label, -1);
                regionCells.Clear();

                // 4-connected flood fill over the passable cells of this 64x64 block
                for (int start = 0; start < label.Length; start++)
                {
                    if (label[start] != -1) continue;
                    int sy = start / BlockFine, sx = start % BlockFine;
                    if (fine[(by * BlockFine + sy) * fineSide + (bx * BlockFine + sx)] != CompressedSearchMap.Passable)
                        continue;

                    int id = regionCells.Count;
                    var cells = new List<int>();
                    int head = 0, tail = 0;
                    queue[tail++] = start;
                    label[start] = id;
                    while (head < tail)
                    {
                        int cur = queue[head++];
                        cells.Add(cur);
                        int cy = cur / BlockFine, cx = cur % BlockFine;
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                            int ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                            if ((uint)nx >= BlockFine || (uint)ny >= BlockFine) continue;
                            int ni = ny * BlockFine + nx;
                            if (label[ni] != -1) continue;
                            if (fine[(by * BlockFine + ny) * fineSide + (bx * BlockFine + nx)] != CompressedSearchMap.Passable)
                                continue;
                            label[ni] = id;
                            queue[tail++] = ni;
                        }
                    }
                    regionCells.Add(cells);
                }

                // A cell can carry only four regions, so keep the four largest and drop the slivers. Anything
                // dropped becomes unreachable to the STRATEGIC search, which is the same thing the engine's own
                // four-slot limit implies; the local search still crosses it inside a block.
                var order = Enumerable.Range(0, regionCells.Count)
                                      .OrderByDescending(i => regionCells[i].Count)
                                      .Take(StrategicMap.PortalSlots)
                                      .ToList();
                var slotOf = new int[regionCells.Count];
                Array.Fill(slotOf, -1);
                for (int s = 0; s < order.Count; s++) slotOf[order[s]] = s;

                // Portal = the region cell nearest that region's centroid: a well-connected interior cell rather
                // than an arbitrary corner, so the local search starts somewhere sensible.
                for (int s = 0; s < order.Count; s++)
                {
                    var cells = regionCells[order[s]];
                    long sx = 0, sy = 0;
                    foreach (var c in cells) { sx += c % BlockFine; sy += c / BlockFine; }
                    double cxm = (double)sx / cells.Count, cym = (double)sy / cells.Count;
                    int best = cells[0];
                    double bestD = double.MaxValue;
                    foreach (var c in cells)
                    {
                        double dx = c % BlockFine - cxm, dy = c / BlockFine - cym;
                        double dd = dx * dx + dy * dy;
                        if (dd < bestD) { bestD = dd; best = c; }
                    }
                    table.SetPortal(bx, by, s, best % BlockFine, best / BlockFine);
                }

                var slotMap = new sbyte[BlockFine * BlockFine];
                for (int i = 0; i < slotMap.Length; i++) slotMap[i] = (sbyte)(label[i] >= 0 ? slotOf[label[i]] : -1);
                slots[by * strategicSide + bx] = slotMap;

                // Info cells: each covers scale x scale fine cells; take the most common region among them.
                var tally = new int[StrategicMap.PortalSlots];
                for (int iy = 0; iy < blockSide; iy++)
                    for (int ix = 0; ix < blockSide; ix++)
                    {
                        Array.Clear(tally);
                        for (int fy = 0; fy < scale; fy++)
                            for (int fx = 0; fx < scale; fx++)
                            {
                                int l = label[(iy * scale + fy) * BlockFine + (ix * scale + fx)];
                                if (l >= 0 && slotOf[l] >= 0) tally[slotOf[l]]++;
                            }
                        int bestSlot = 0, bestN = 0;
                        for (int s = 0; s < tally.Length; s++) if (tally[s] > bestN) { bestN = tally[s]; bestSlot = s; }
                        info[(by * blockSide + iy) * infoSide + (bx * blockSide + ix)] = (byte)bestSlot;
                    }
            }

        // Links: region s of a cell touches region t of its northern (y+1) neighbour where a passable cell of s on the
        // block's top row faces a passable cell of t on the neighbour's bottom row (bit 4s+t); the same on the east
        // edge (bit 16+4s+t). South and west links live in the neighbour's table.
        for (int by = 0; by < strategicSide; by++)
            for (int bx = 0; bx < strategicSide; bx++)
            {
                var here = slots[by * strategicSide + bx];
                uint links = 0;
                if (by + 1 < strategicSide)
                {
                    var north = slots[(by + 1) * strategicSide + bx];
                    for (int x = 0; x < BlockFine; x++)
                    {
                        int s = here[(BlockFine - 1) * BlockFine + x], t = north[x];
                        if (s >= 0 && t >= 0) links |= 1u << (4 * s + t);
                    }
                }
                if (bx + 1 < strategicSide)
                {
                    var east = slots[by * strategicSide + bx + 1];
                    for (int y = 0; y < BlockFine; y++)
                    {
                        int s = here[y * BlockFine + BlockFine - 1], t = east[y * BlockFine];
                        if (s >= 0 && t >= 0) links |= 1u << (16 + 4 * s + t);
                    }
                }
                table.SetLinks(bx, by, links);
            }

        return new Result(table, info, infoSide, level);
    }

    /// <summary>The companion pair as writable files. The engine names the pair after the SEARCH TYPE that paths on the
    /// map (<c>ai.addSearchType Infantery 1 0</c> reads <c>Infantery.raw</c> / <c>InfanteryInfo.raw</c>, whatever the
    /// map is called) and reads the info map at that type's level + 1: pass <paramref name="typeName"/> and
    /// <paramref name="typeLevel"/> from the level's AIpathFinding.con. Without them the pair is named after the map
    /// (<c>&lt;Base&gt;.raw</c>) at <see cref="CompanionLevel"/> - right for retail's usual spellings only.</summary>
    public static IReadOnlyList<(string FileName, byte[] Data)> EncodeCompanions(
        SearchMapParams p, byte[] fine, int fineSide, string? typeName = null, int? typeLevel = null)
    {
        var r = Generate(fine, fineSide, p, typeLevel is { } tl ? tl + 1 : null);
        var name = string.IsNullOrWhiteSpace(typeName) ? p.BaseName : typeName;
        return new[]
        {
            (name + ".raw", r.Table.Save()),
            (name + "Info.raw", StrategicInfoMap.Encode(r.InfoCells, r.InfoSide, r.Level)),
        };
    }
}
