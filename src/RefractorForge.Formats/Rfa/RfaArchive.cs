using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RefractorForge.Formats.Rfa;

/// <summary>One file inside an RFA archive (a directory-table entry).</summary>
public sealed class RfaEntry
{
    public required string Name { get; init; }
    /// <summary>Total on-disk size of this file's data region (descriptor table + all compressed blocks).</summary>
    public required int BlockSize { get; init; }
    /// <summary>Decompressed size of the whole file.</summary>
    public required int UncompressedSize { get; init; }
    /// <summary>Byte offset of this file's data region within the archive.</summary>
    public required int Offset { get; init; }
    public override string ToString() => $"{Name} ({UncompressedSize}B)";
}

/// <summary>
/// Reader for Refractor "RFA" archives (Battlefield 1942 / Vietnam Archives/*.rfa).
/// </summary>
/// <remarks>
/// <para>Container layout, reverse-engineered and verified byte-for-byte against the retail
/// <c>objects.rfa</c> (3808 files) and <c>standardMesh.rfa</c> (3994 files):</para>
/// <code>
/// header           : u32 tocOffset
/// TOC @ tocOffset  : u32 fileCount, then per file:
///                      u32 nameLen, char[nameLen] name (latin-1),
///                      u32 blockSize, u32 uncompressedSize, u32 offset, u32 x3 (reserved)
/// data @ offset    : u32 numBlocks,
///                    numBlocks x { u32 compSize, u32 uncSize, u32 cumulativeCompOffset },
///                    then every block's compressed bytes concatenated.
/// </code>
/// <para>Each block is LZO1X-compressed (see <see cref="Lzo1x"/>), except a block stored verbatim
/// when <c>compSize == uncSize</c>. Large files are split into 32 KiB uncompressed chunks; a
/// single-block file has <c>numBlocks==1</c> and <c>cumulativeCompOffset==0</c>.</para>
/// <para>Decoding is exact and total — every entry of both retail archives round-trips to the
/// stored uncompressed length and matches the <c>liblzo2</c> reference.</para>
/// </remarks>
public sealed class RfaArchive
{
    private readonly byte[] _data;
    public IReadOnlyList<RfaEntry> Entries { get; }

    private RfaArchive(byte[] data, List<RfaEntry> entries) { _data = data; Entries = entries; }

    public static RfaArchive Open(string path) => Load(File.ReadAllBytes(path));

    public static RfaArchive Load(byte[] data)
    {
        int tocOffset = (int)U32(data, 0);
        int p = tocOffset;
        int count = (int)U32(data, p); p += 4;
        var list = new List<RfaEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int nameLen = (int)U32(data, p); p += 4;
            string name = Encoding.Latin1.GetString(data, p, nameLen); p += nameLen;
            int blockSize = (int)U32(data, p);
            int uncomp    = (int)U32(data, p + 4);
            int offset    = (int)U32(data, p + 8);
            p += 24; // blockSize, unc, offset, + 3 reserved u32
            list.Add(new RfaEntry { Name = name, BlockSize = blockSize, UncompressedSize = uncomp, Offset = offset });
        }
        return new RfaArchive(data, list);
    }

    /// <summary>Decompress an entry to its full uncompressed bytes.</summary>
    public byte[] Read(RfaEntry e) => DecodeRegion(_data.AsSpan(e.Offset, e.BlockSize), e.UncompressedSize, e.Name);

    /// <summary>Decompress one entry's on-disk data <paramref name="region"/> (the bytes from the entry's offset
    /// for its whole blockSize) to its <paramref name="uncompressedSize"/> bytes. Factored out of <see cref="Read"/>
    /// so a streaming reader can decode an entry pulled straight from a file by offset — which is how archives larger
    /// than a managed array (the whole file can't be loaded into one <c>byte[]</c>) are verified/read.</summary>
    public static byte[] DecodeRegion(ReadOnlySpan<byte> region, int uncompressedSize, string? name = null)
    {
        // Some archives (notably uncompressed BF1942 level archives) store each entry verbatim with
        // no block wrapper at all: the data region is exactly UncompressedSize raw bytes. These are
        // identified by BlockSize == UncompressedSize. (A block-wrapped region is u32 numBlocks +
        // descriptors + payload, so its on-disk size never equals the decompressed size.)
        if (region.Length == uncompressedSize)
            return region.ToArray();

        int numBlocks = (int)U32(region, 0);
        int descBase = 4;
        int dataStart = descBase + numBlocks * 12;

        var result = new byte[uncompressedSize];
        int written = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int b = descBase + i * 12;
            int comp = (int)U32(region, b);
            int unc  = (int)U32(region, b + 4);
            int cum  = (int)U32(region, b + 8);
            // A block with unc==0 produces no output — some RFA writers append a trailing 0-length terminator
            // block (e.g. Dambuster/materialmap.raw: 8 full 32768-byte blocks + a 9th comp=4/unc=0 marker). Its
            // few compressed bytes are an LZO end-of-stream token the engine never decodes (the per-block loop
            // sees unc==0 and skips it). Feeding it to the decoder with dstLen==0 read garbage opcodes and threw
            // "back-reference before start of output", which made ~8 real maps fail to open entirely. Skip it.
            if (unc == 0) continue;
            var srcSpan = region.Slice(dataStart + cum, comp);
            var dstSpan = result.AsSpan(written, unc);
            if (comp == unc)
                srcSpan.CopyTo(dstSpan);               // stored verbatim
            else
                Lzo1x.Decompress(srcSpan, dstSpan, unc); // LZO1X block
            written += unc;
        }
        if (written != uncompressedSize)
            throw new InvalidDataException($"'{name}': reassembled {written} bytes, expected {uncompressedSize}.");
        return result;
    }

    /// <summary>The entry's data region exactly as stored on disk (descriptor table + compressed blocks, or the
    /// raw bytes for a whole-verbatim entry). Used to copy UNCHANGED entries through a repack verbatim, so their
    /// known-good retail LZO is never re-encoded (the engine is strict about the LZO stream it accepts).</summary>
    public byte[] RawRegion(RfaEntry e) => _data.AsSpan(e.Offset, e.BlockSize).ToArray();

    /// <summary>Per-block <c>(compressedSize, uncompressedSize)</c> for an entry, as stored on disk; empty for a
    /// whole-entry-verbatim entry (<c>BlockSize == UncompressedSize</c>). Diagnostic: lets callers see which
    /// block form retail archives use (LZO when comp&lt;unc, stored-verbatim when comp==unc).</summary>
    public IReadOnlyList<(int Comp, int Unc)> BlockSizes(RfaEntry e)
    {
        if (e.BlockSize == e.UncompressedSize) return Array.Empty<(int, int)>();
        int off = e.Offset, numBlocks = (int)U32(_data, off);
        var list = new List<(int, int)>(numBlocks);
        for (int i = 0; i < numBlocks; i++) { int b = off + 4 + i * 12; list.Add(((int)U32(_data, b), (int)U32(_data, b + 4))); }
        return list;
    }

    /// <summary>Try to decompress an entry, returning false instead of throwing on malformed data.</summary>
    public bool TryRead(RfaEntry e, out byte[] result)
    {
        try { result = Read(e); return true; }
        catch { result = Array.Empty<byte>(); return false; }
    }

    private static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
    private static uint U32(ReadOnlySpan<byte> d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(p));

    /// <summary>One directory entry read straight from disk. <see cref="Offset"/> is <see cref="long"/> because a
    /// large archive's last region can sit past <see cref="int.MaxValue"/> even though the per-entry sizes don't.</summary>
    public readonly record struct TocEntry(string Name, int BlockSize, int UncompressedSize, long Offset);

    /// <summary>Read just an archive's directory (the small table at the tail) without loading the whole file —
    /// the only way to enumerate an archive too big to fit in a single managed <c>byte[]</c>. Region offsets are
    /// kept as <see cref="long"/>; pair with <see cref="DecodeRegion"/> after seeking to <see cref="TocEntry.Offset"/>
    /// to decode entries from a multi-GiB file.</summary>
    public static IReadOnlyList<TocEntry> ReadToc(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> u4 = stackalloc byte[4];
        fs.ReadExactly(u4);
        long tocOffset = BinaryPrimitives.ReadUInt32LittleEndian(u4);   // u32 in the file, widened so >2 GiB seeks
        fs.Seek(tocOffset, SeekOrigin.Begin);
        fs.ReadExactly(u4);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(u4);

        var list = new List<TocEntry>(count);
        Span<byte> rec = stackalloc byte[24];   // blockSize, uncSize, offset, + 3 reserved u32
        for (int i = 0; i < count; i++)
        {
            fs.ReadExactly(u4);
            int nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(u4);
            var nameBytes = new byte[nameLen];   // heap (not stackalloc): this runs in a loop over every entry
            fs.ReadExactly(nameBytes);
            string name = Encoding.Latin1.GetString(nameBytes);
            fs.ReadExactly(rec);
            int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec);
            int uncomp    = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(4));
            long offset   = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(8));
            list.Add(new TocEntry(name, blockSize, uncomp, offset));
        }
        return list;
    }
}
