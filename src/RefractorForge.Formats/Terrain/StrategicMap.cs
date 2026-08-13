namespace RefractorForge.Formats.Terrain;

/// <summary>
/// The COARSE companion map a level ships next to its per-vehicle navmaps: <c>Pathfinding/&lt;Veh&gt;.raw</c>.
///
/// Refactor pathfinding is hierarchical. The fine <c>&lt;Veh&gt;Level&lt;L&gt;Map*.raw</c> grids (see
/// <see cref="CompressedSearchMap"/>) are what a local A* walks; on top of them sits a STRATEGIC grid, one cell per
/// 64x64 block of the fine map, and the strategic search hops cell to cell through the PORTALS stored here before
/// the local search fills in the detail. That is why editing a navmap without touching these leaves bots behaving
/// oddly: the passability changed underneath a region decomposition that still describes the old world.
///
/// Format (recovered from <c>dice::bf::ai::StrategicMap::save</c> in the shipped Linux dedicated server, which
/// carries full symbols, and then verified against every such file in the user's installs - 26/26 byte-exact):
/// <code>
///   int32 width;                       // = fineNavmapSide / 64
///   int32 height;
///   StrategicCell cells[height][width] // 16 bytes each, row-major (the engine indexes (y &lt;&lt; k) + x)
/// </code>
/// and one <c>StrategicCell</c>, from the accessors in the same binary:
/// <code>
///   [0x00..0x03]  one byte per portal slot - link/direction bits (2 bits per slot: 0x55, 0x11, 0x01, 0x03 seen)
///   [0x04..0x0B]  four (x, z) portal positions, ONE BYTE EACH, masked 0x3F -> 0..63 inside the cell's 64x64 block
///                 getPositionX(i) = cell[4 + i*2] &amp; 0x3F ; getPositionZ(i) = cell[5 + i*2] &amp; 0x3F
///   [0x0C]        flags: HIGH nibble = "portal slot i is used" (isUsed(i) = flags &amp; (0x10 &lt;&lt; i)).
///                 The low nibble is 0 in all 14,592 real cells examined.
///   [0x0D..0x0F]  padding. DICE wrote the struct raw, so these are UNINITIALISED MEMORY on disk - real files
///                 contain stack/heap crumbs here (0x0DD2DB, 0x3F8000 = a float 1.0f tail, ...). They carry no
///                 meaning, but they are preserved verbatim so a load/save round-trip stays byte-exact.
/// </code>
///
/// The sibling <c>&lt;Veh&gt;Info.raw</c> is NOT this structure - it is an ordinary <see cref="CompressedSearchMap"/>
/// CellMap (same codec as the fine maps; the engine writes both through <c>dice::bf::ai::CellMap</c>) holding, per
/// fine cell, which strategic region it belongs to.
/// </summary>
public sealed class StrategicMap
{
    /// <summary>Bytes per cell on disk.</summary>
    public const int CellSize = 16;

    /// <summary>Fine navmap cells per strategic cell along each axis.</summary>
    public const int BlockSide = 64;

    /// <summary>Number of portal slots a cell can hold.</summary>
    public const int PortalSlots = 4;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The cell records exactly as they sit on disk (Width*Height*16). Kept verbatim so the unused
    /// padding bytes survive a round-trip; the accessors below decode the meaningful fields.</summary>
    private readonly byte[] _cells;

    private StrategicMap(int width, int height, byte[] cells)
    {
        Width = width; Height = height; _cells = cells;
    }

    /// <summary>An empty map of the given size (all portals unused, padding zeroed).</summary>
    public StrategicMap(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        _cells = new byte[width * height * CellSize];
    }

    /// <summary>The strategic grid side that matches a fine navmap of <paramref name="fineSide"/> cells.</summary>
    public static int StrategicSideFor(int fineSide) => Math.Max(1, fineSide / BlockSide);

    /// <summary>True if <paramref name="data"/> is shaped like a strategic map (used to tell a
    /// <c>&lt;Veh&gt;.raw</c> from the compressed <c>&lt;Veh&gt;Info.raw</c>, which has a different header).</summary>
    public static bool LooksLikeStrategicMap(byte[] data)
    {
        if (data is null || data.Length < 8) return false;
        int w = BitConverter.ToInt32(data, 0), h = BitConverter.ToInt32(data, 4);
        return w > 0 && h > 0 && w <= 4096 && h <= 4096 && 8L + (long)w * h * CellSize == data.Length;
    }

    public static StrategicMap Load(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 8) throw new InvalidDataException("Strategic map is too short to hold its header.");
        int w = BitConverter.ToInt32(data, 0), h = BitConverter.ToInt32(data, 4);
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096)
            throw new InvalidDataException($"Implausible strategic map size {w}x{h}.");
        long need = 8L + (long)w * h * CellSize;
        if (data.Length != need)
            throw new InvalidDataException($"Strategic map is {data.Length} bytes; {w}x{h} needs exactly {need}.");
        var cells = new byte[w * h * CellSize];
        Buffer.BlockCopy(data, 8, cells, 0, cells.Length);
        return new StrategicMap(w, h, cells);
    }

    public byte[] Save()
    {
        var outBytes = new byte[8 + _cells.Length];
        BitConverter.GetBytes(Width).CopyTo(outBytes, 0);
        BitConverter.GetBytes(Height).CopyTo(outBytes, 4);
        Buffer.BlockCopy(_cells, 0, outBytes, 8, _cells.Length);
        return outBytes;
    }

    private int Base(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"cell ({x},{y}) is outside {Width}x{Height}.");
        return (y * Width + x) * CellSize;
    }

    /// <summary><c>StrategicCell::isUsed(slot)</c> - is this portal slot populated?</summary>
    public bool IsUsed(int x, int y, int slot)
    {
        if ((uint)slot >= PortalSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return (_cells[Base(x, y) + 0x0C] & (0x10 << slot)) != 0;
    }

    /// <summary>How many of the four portal slots this cell uses.</summary>
    public int UsedPortals(int x, int y)
    {
        int f = _cells[Base(x, y) + 0x0C], n = 0;
        for (int s = 0; s < PortalSlots; s++) if ((f & (0x10 << s)) != 0) n++;
        return n;
    }

    /// <summary><c>getPositionX/Z(slot)</c> - the portal's position INSIDE this cell's 64x64 block (0..63).</summary>
    public (int X, int Z) Portal(int x, int y, int slot)
    {
        if ((uint)slot >= PortalSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        int b = Base(x, y) + 4 + slot * 2;
        return (_cells[b] & 0x3F, _cells[b + 1] & 0x3F);
    }

    /// <summary>The portal position in FINE navmap cells (block origin + in-block offset).</summary>
    public (int X, int Z) PortalWorldCell(int x, int y, int slot)
    {
        var (px, pz) = Portal(x, y, slot);
        return (x * BlockSide + px, y * BlockSide + pz);
    }

    /// <summary>The raw link/direction byte for a slot (<c>cell[0x00 + slot]</c>). Not fully decoded: it holds two
    /// bits per slot in the real files. Exposed so callers can preserve or inspect it.</summary>
    public byte LinkBits(int x, int y, int slot)
    {
        if ((uint)slot >= PortalSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return _cells[Base(x, y) + slot];
    }

    /// <summary>Populate a portal slot: position within the block, plus the link byte to carry.
    /// Only the low 6 bits of each coordinate are stored, matching the engine's masks.</summary>
    public void SetPortal(int x, int y, int slot, int px, int pz, byte linkBits = 1)
    {
        if ((uint)slot >= PortalSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        if ((uint)px >= BlockSide || (uint)pz >= BlockSide)
            throw new ArgumentOutOfRangeException(nameof(px), "portal position must be 0..63 inside the block.");
        int b = Base(x, y);
        _cells[b + slot] = linkBits;
        _cells[b + 4 + slot * 2] = (byte)(px & 0x3F);
        _cells[b + 5 + slot * 2] = (byte)(pz & 0x3F);
        _cells[b + 0x0C] |= (byte)(0x10 << slot);
    }

    /// <summary>Drop every portal in a cell (and zero its record, padding included).</summary>
    public void ClearCell(int x, int y) => Array.Clear(_cells, Base(x, y), CellSize);

    /// <summary>The 16 raw bytes of one cell, for inspection/diagnostics.</summary>
    public ReadOnlySpan<byte> CellBytes(int x, int y) => _cells.AsSpan(Base(x, y), CellSize);
}
