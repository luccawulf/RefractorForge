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
/// This is a faithful reimplementation, not a byte-identical clone: DICE's own labelling order and their use of
/// the not-fully-decoded link bytes are unknown, so a regenerated pair will not match a shipped one byte for byte.
/// What it does guarantee is internal consistency - every portal sits on a passable cell of the region it names,
/// and every passable cell's info value indexes a used portal slot - which is what the search actually relies on.
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
    public static Result Generate(byte[] fine, int fineSide, SearchMapParams p)
    {
        if (fine is null) throw new ArgumentNullException(nameof(fine));
        if (fine.Length != fineSide * fineSide)
            throw new ArgumentException($"fine map is {fine.Length} bytes, expected {fineSide}^2.");
        if (fineSide % BlockFine != 0)
            throw new ArgumentException($"fine side {fineSide} is not a multiple of {BlockFine}.");

        int level = CompanionLevel(p);
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

        return new Result(table, info, infoSide, level);
    }

    /// <summary>The companion pair as writable files, named the way the level ships them
    /// (<c>&lt;Base&gt;.raw</c> and <c>&lt;Base&gt;Info.raw</c>).</summary>
    public static IReadOnlyList<(string FileName, byte[] Data)> EncodeCompanions(
        SearchMapParams p, byte[] fine, int fineSide)
    {
        var r = Generate(fine, fineSide, p);
        return new[]
        {
            (p.BaseName + ".raw", r.Table.Save()),
            (p.BaseName + "Info.raw", StrategicInfoMap.Encode(r.InfoCells, r.InfoSide, r.Level)),
        };
    }
}
