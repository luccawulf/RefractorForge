namespace RefractorForge.Formats.Terrain;

/// <summary>
/// The second half of the strategic pathfinding pair: <c>Pathfinding/&lt;Veh&gt;Info.raw</c>.
///
/// It is a <c>dice::bf::ai::CellMap</c> like the fine navmaps, but a DIFFERENT variant of the same container:
/// <b>2 bits per cell</b> in blocks of <b>32x32 cells</b>, rather than 1 bit per cell in 64x64 blocks. Each value
/// 0..3 names which sub-region of its strategic cell that area belongs to — the "strategic cell info number" the
/// engine reads back through <c>StrategicMap::getStrategicCellInfoNo</c>, which is also the slot index into the
/// four portals stored per cell in <see cref="StrategicMap"/>.
///
/// Resolution: one strategic cell covers 64x64 FINE navmap cells but only 32x32 info cells, so this map runs at
/// half the fine resolution and <c>Side == strategicSide * 32</c>.
///
/// Layout (all int32 little-endian), same shape as <see cref="CompressedSearchMap"/> but with the block geometry
/// above:
/// <code>
///   header[8]: [blocksBits, blocksBits, cellBits, level, 1, 2, 0, -1]
///   then per 32x32 block, row-major (y outer, x inner), an int32 descriptor:
///        v >= 0 -> the whole block is region (v &amp; 3)
///        -1     -> MIXED: 256 bytes follow = 1024 cells x 2 bits, LSB-first, row-major
/// </code>
/// The 256-byte payload is what pinned this down: it is half the fine map's 512, which only works out as
/// 1024 cells at 2 bits — and decoding on that basis yields values strictly in 0..3 with
/// <c>side/32 == strategicWidth</c> on every shipped file.
/// </summary>
public static class StrategicInfoMap
{
    /// <summary>Bits per cell.</summary>
    public const int CellBits = 2;

    /// <summary>Header field 2 in every shipped file. The block edge is derived from it and the level.</summary>
    public const int BaseCellBits = 6;

    /// <summary>Cells per block edge for a given level: <c>1 &lt;&lt; (cellBits - level)</c>.
    /// Level 1 -> 32 (a 256-byte payload), level 3 -> 8 (16 bytes). This is what the first cut of this codec got
    /// wrong: it assumed a fixed 32 and so could not read the coarser levels at all.</summary>
    public static int BlockSideFor(int cellBits, int level)
    {
        int s = cellBits - level;
        if (s < 1 || s > 10) throw new InvalidDataException($"implausible block side from cellBits {cellBits}, level {level}.");
        return 1 << s;
    }

    private static int PayloadFor(int blockSide) => blockSide * blockSide * CellBits / 8;

    /// <summary>Highest region index a cell can name (2 bits).</summary>
    public const int MaxRegion = 3;

    /// <summary>Decode to one byte per cell, each 0..3. <paramref name="side"/> is the square side in info cells.</summary>
    public static byte[] Decode(byte[] data, out int side, out int level)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 32) throw new InvalidDataException("strategic info map too small for its header.");
        int blocksBits = BitConverter.ToInt32(data, 0);
        int cellBits = BitConverter.ToInt32(data, 8);
        level = BitConverter.ToInt32(data, 12);
        if ((uint)blocksBits > 12) throw new InvalidDataException($"implausible blocksBits {blocksBits}.");
        int nb = 1 << blocksBits;
        int blockSide = BlockSideFor(cellBits, level);
        int payload = PayloadFor(blockSide);
        side = nb * blockSide;

        var cells = new byte[side * side];
        int blockCells = blockSide * blockSide;
        int pos = 32;
        for (int by = 0; by < nb; by++)
            for (int bx = 0; bx < nb; bx++)
            {
                if (pos + 4 > data.Length) throw new InvalidDataException($"truncated at block ({bx},{by}).");
                int desc = BitConverter.ToInt32(data, pos); pos += 4;
                if (desc == -1)
                {
                    if (pos + payload > data.Length)
                        throw new InvalidDataException($"mixed block ({bx},{by}) payload truncated.");
                    for (int i = 0; i < blockCells; i++)
                    {
                        int v = (data[pos + (i >> 2)] >> ((i & 3) * 2)) & 3;
                        int ly = i / blockSide, lx = i % blockSide;
                        cells[(by * blockSide + ly) * side + (bx * blockSide + lx)] = (byte)v;
                    }
                    pos += payload;
                }
                else
                {
                    byte fill = (byte)(desc & 3);
                    for (int ly = 0; ly < blockSide; ly++)
                    {
                        int o = (by * blockSide + ly) * side + bx * blockSide;
                        for (int lx = 0; lx < blockSide; lx++) cells[o + lx] = fill;
                    }
                }
            }
        if (pos != data.Length)
            throw new InvalidDataException($"strategic info map has {data.Length - pos} trailing byte(s).");
        return cells;
    }

    /// <summary>Encode one-byte-per-cell region indices (0..3) back to the packed form, collapsing uniform blocks
    /// exactly as the engine does so shipped files round-trip byte-identical.</summary>
    public static byte[] Encode(byte[] cells, int side, int level)
    {
        if (cells is null) throw new ArgumentNullException(nameof(cells));
        if (cells.Length != side * side)
            throw new ArgumentException($"cell count {cells.Length} != side^2 {side * side}.");
        int blockSide = BlockSideFor(BaseCellBits, level);
        if (side % blockSide != 0)
            throw new ArgumentException($"side {side} is not a multiple of the level-{level} block side {blockSide}.");

        int nb = side / blockSide;
        int blocksBits = 0;
        while ((1 << blocksBits) < nb) blocksBits++;
        if ((1 << blocksBits) != nb)
            throw new ArgumentException($"side {side} is not {blockSide} * a power of two.");

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // cellBits is 6 in every shipped file; the block side comes from it and the level, not from blocksBits.
        w.Write(blocksBits); w.Write(blocksBits); w.Write(BaseCellBits); w.Write(level);
        w.Write(1); w.Write(2); w.Write(0); w.Write(-1);

        int blockCells = blockSide * blockSide;
        var payload = new byte[PayloadFor(blockSide)];
        for (int by = 0; by < nb; by++)
            for (int bx = 0; bx < nb; bx++)
            {
                byte first = cells[(by * blockSide) * side + bx * blockSide];
                bool uniform = true;
                for (int ly = 0; ly < blockSide && uniform; ly++)
                {
                    int o = (by * blockSide + ly) * side + bx * blockSide;
                    for (int lx = 0; lx < blockSide; lx++)
                        if (cells[o + lx] != first) { uniform = false; break; }
                }
                if (uniform) { w.Write((int)(first & 3)); continue; }

                w.Write(-1);
                Array.Clear(payload);
                for (int i = 0; i < blockCells; i++)
                {
                    int ly = i / blockSide, lx = i % blockSide;
                    int v = cells[(by * blockSide + ly) * side + (bx * blockSide + lx)] & 3;
                    payload[i >> 2] |= (byte)(v << ((i & 3) * 2));
                }
                w.Write(payload);
            }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Cheap shape check: does this look like an info map rather than a strategic table or a fine map?</summary>
    public static bool LooksLikeInfoMap(byte[] data)
    {
        if (data is null || data.Length < 32) return false;
        try { Decode(data, out _, out _); return true; }
        catch { return false; }
    }
}
