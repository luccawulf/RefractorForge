using System;
using System.IO;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Codec for the Battlefield 1942 / Battlefield Vietnam AI pathfinding "search map" COMPRESSED .raw files —
/// the form the engine's <c>ai.loadMaps</c> actually reads (e.g. <c>Tank0Level2Map.raw</c>), as opposed to the
/// uncompressed editor form <c>*8Bit.raw</c>.
///
/// Reverse-engineered BYTE-EXACT from retail matched pairs (Marshall_island, Attack_on_Pearl_Harbor): every
/// retail file round-trips identically (see Demo <c>navcompress</c>). No Ghidra needed.
///
/// Layout (all int32 little-endian):
///   header: [blocksBits, blocksBits, cellBits, level, 0, 2, 0, -1]
///           blocksBits + cellBits == 11 (= log2 of the fixed 2048 finest grid); side == 64 * 2^blocksBits;
///           level is the map's level number (also == 5 - blocksBits for the standard set).
///   then, per 64x64 block in ROW-MAJOR order (y outer, x inner), an int32 descriptor:
///        0  -> uniform all-PASSABLE  (every cell 0x00 / "black" / bots CAN go)
///        1  -> uniform all-BLOCKED   (every cell 0xFF / "white" / bots CANNOT go)
///       -1  -> MIXED: a 512-byte bitmap follows (64x64 = 4096 bits, 1 bit/cell, LSB-first within each byte,
///              row-major; bit SET = blocked (0xFF), bit CLEAR = passable (0x00)).
///
/// The 8Bit form this converts to/from is 1 byte/cell, side*side, <c>0x00 = passable</c> / <c>0xFF = blocked</c>
/// (black = can go, white = cannot — the Cajunwolf/ED42 convention). Cell orientation is identity (no flip/transpose).
/// </summary>
public static class CompressedSearchMap
{
    public const int BlockSize = 64;
    private const int BlockCells = BlockSize * BlockSize;   // 4096
    private const int BitmapBytes = BlockCells / 8;          // 512
    public const byte Passable = 0x00;
    public const byte Blocked = 0xFF;

    /// <summary>blocksBits for a square 8Bit side (side must be 64 * 2^k).</summary>
    public static int BlocksBitsFor(int side)
    {
        int nb = side / BlockSize, b = 0;
        while ((1 << b) < nb) b++;
        return b;
    }

    /// <summary>Decode a compressed search map to its <paramref name="side"/>² 8Bit form
    /// (0x00 passable / 0xFF blocked). Also returns the header's level number.</summary>
    public static byte[] Decode(byte[] data, out int side, out int level)
    {
        if (data.Length < 32) throw new InvalidDataException("compressed search map too small for header.");
        int blocksBits = BitConverter.ToInt32(data, 0);
        level = BitConverter.ToInt32(data, 12);
        int nb = 1 << blocksBits;
        side = nb * BlockSize;
        var eight = new byte[side * side];
        int pos = 32;
        for (int by = 0; by < nb; by++)
            for (int bx = 0; bx < nb; bx++)
            {
                if (pos + 4 > data.Length) throw new InvalidDataException($"truncated at block ({bx},{by}).");
                int desc = BitConverter.ToInt32(data, pos); pos += 4;
                if (desc == -1)
                {
                    if (pos + BitmapBytes > data.Length) throw new InvalidDataException($"mixed block ({bx},{by}) bitmap truncated.");
                    for (int ly = 0; ly < BlockSize; ly++)
                        for (int lx = 0; lx < BlockSize; lx++)
                        {
                            int bit = ly * BlockSize + lx;
                            bool blocked = (data[pos + (bit >> 3)] & (1 << (bit & 7))) != 0;
                            eight[(by * BlockSize + ly) * side + (bx * BlockSize + lx)] = blocked ? Blocked : Passable;
                        }
                    pos += BitmapBytes;
                }
                else
                {
                    byte fill = desc == 1 ? Blocked : Passable;   // 1 = all blocked, 0 = all passable
                    for (int ly = 0; ly < BlockSize; ly++)
                        for (int lx = 0; lx < BlockSize; lx++)
                            eight[(by * BlockSize + ly) * side + (bx * BlockSize + lx)] = fill;
                }
            }
        return eight;
    }

    /// <summary>Encode an <paramref name="side"/>² 8Bit map (0x00 passable / 0xFF blocked) to the compressed
    /// .raw, collapsing uniform 64x64 blocks exactly as the retail tools do. <paramref name="level"/> is the
    /// map's level number (header field 3). Round-trips byte-identical with retail files.</summary>
    public static byte[] Encode(byte[] eight, int side, int level)
    {
        if (eight.Length != side * side) throw new ArgumentException($"8Bit length {eight.Length} != side^2 {side * side}.");
        if (side % BlockSize != 0) throw new ArgumentException($"side {side} is not a multiple of {BlockSize}.");
        int nb = side / BlockSize;
        int blocksBits = BlocksBitsFor(side);
        // cellBits = block size in FINEST units = log2(BlockSize) + level = 6 + level (NOT 11 - blocksBits; the
        // "11" only holds for a 2048-finest map like Marshall — bigger maps like Pearl Harbor use a larger total).
        int cellBits = 6 + level;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(blocksBits); w.Write(blocksBits); w.Write(cellBits); w.Write(level);
        w.Write(0); w.Write(2); w.Write(0); w.Write(-1);
        var bm = new byte[BitmapBytes];
        for (int by = 0; by < nb; by++)
            for (int bx = 0; bx < nb; bx++)
            {
                bool anyPass = false, anyBlock = false;
                for (int ly = 0; ly < BlockSize; ly++)
                    for (int lx = 0; lx < BlockSize; lx++)
                    {
                        if (eight[(by * BlockSize + ly) * side + (bx * BlockSize + lx)] == Blocked) anyBlock = true; else anyPass = true;
                    }
                if (!anyBlock) { w.Write(0); }            // uniform all-passable
                else if (!anyPass) { w.Write(1); }        // uniform all-blocked
                else
                {
                    w.Write(-1);
                    Array.Clear(bm, 0, BitmapBytes);
                    for (int ly = 0; ly < BlockSize; ly++)
                        for (int lx = 0; lx < BlockSize; lx++)
                            if (eight[(by * BlockSize + ly) * side + (bx * BlockSize + lx)] == Blocked)
                            { int bit = ly * BlockSize + lx; bm[bit >> 3] |= (byte)(1 << (bit & 7)); }
                    w.Write(bm);
                }
            }
        w.Flush();
        return ms.ToArray();
    }
}
