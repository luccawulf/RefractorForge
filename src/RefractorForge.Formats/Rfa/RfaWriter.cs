using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Writer for Refractor "RFA" archives — the inverse of <see cref="RfaArchive"/>. It reproduces the
/// exact container the engine reads: a 156-byte header (<c>u32 tocOffset</c>, <c>u32 version=1</c>,
/// then 148 bytes the engine ignores), per-file data regions, then the directory table.
/// </summary>
/// <remarks>
/// <para>Blocks are emitted as <b>literal-only LZO1X v0</b> streams: one literal run for the (≤32 KiB)
/// chunk followed by the end-of-stream marker <c>0x11 00 00</c>. That is a fully valid LZO1X stream —
/// it round-trips through this project's byte-exact <see cref="Lzo1x"/> decoder (and therefore through
/// the <c>liblzo2</c>-compatible decoder the engine uses) — so archives load correctly. The trade-off
/// is size: literal encoding does not shrink data (output ≈ input + ~0.4%). Match-finding compression
/// can be layered on later without changing the container.</para>
/// <para>The per-entry trailing field that retail split sets use for archive linking is written as 0,
/// which is correct for a standalone level archive.</para>
/// <para><b>Large archives.</b> All cross-entry offset accounting is <see cref="long"/>, and the writer
/// can stream straight to a <see cref="Stream"/> (<see cref="WriteTo(Stream, IReadOnlyList{ValueTuple{string, byte[]}})"/> /
/// <see cref="WriteFile"/>) instead of building one giant <c>byte[]</c>. That matters because a single
/// managed array caps out near 2 GiB, yet the container's <c>u32</c> offsets address up to ~4 GiB: the
/// uncompressed base BF1942 <c>texture.rfa</c> is ~2.3 GiB and literal-re-encodes past the array ceiling,
/// so it can only be packed by streaming. The in-memory <see cref="Build"/> / <see cref="Repack"/> helpers
/// remain for the common small-patch case and throw a clear error rather than overflowing if the output
/// would exceed what a <c>byte[]</c> can hold.</para>
/// </remarks>
public static class RfaWriter
{
    private const int HeaderSize = 156;
    private const int ChunkSize = 32768;   // uncompressed bytes per block, matching retail archives

    /// <summary>Encode one ≤32 KiB chunk as a literal-only LZO1X v0 stream.</summary>
    public static byte[] EncodeLiteralBlock(ReadOnlySpan<byte> block)
    {
        int n = block.Length;
        var o = new List<byte>(n + 8);
        if (n <= 3)
        {
            o.Add((byte)(n + 17));                 // initial-run form: first byte >=18 => copy (b-17) literals
        }
        else if (n <= 18)
        {
            o.Add((byte)(n - 3));                  // t in [1..15], state==0 literal run => copy (t+3) literals
        }
        else
        {
            o.Add(0);                              // t==0 => extended literal run
            int m = n - 18;                        // length-3 == 15 + 255*z0 + z ; encode (n-18) as zeros + final
            while (m > 255) { o.Add(0); m -= 255; }
            o.Add((byte)m);                        // m stays in [1..255]
        }
        for (int i = 0; i < n; i++) o.Add(block[i]);
        o.Add(0x11); o.Add(0x00); o.Add(0x00);     // end-of-stream marker (distance 16384 => break)
        return o.ToArray();
    }

    /// <summary>Build a file's on-disk data region: <c>u32 numBlocks</c>, the block descriptor table, then the
    /// concatenated blocks. Every block is emitted as a <b>literal-only LZO1X stream</b> (<see cref="EncodeLiteralBlock"/>).
    /// Retail BFV archives ship exactly this form for incompressible blocks (24–110 per archive), so the engine's
    /// strict <c>liblzo2</c> provably accepts it. We deliberately do NOT use match-finding compression here: an
    /// LZO encoder that our own decoder round-trips can still emit matches that <c>liblzo2</c> rejects, which
    /// corrupts the saved archive in-game. (Unchanged entries are copied through verbatim in <see cref="Repack"/>,
    /// so this larger encoding only ever applies to the few files actually edited.)</summary>
    private static byte[] BuildRegion(byte[] data)
    {
        int n = data.Length;
        int numBlocks = n == 0 ? 0 : (n + ChunkSize - 1) / ChunkSize;

        var comps = new List<byte[]>(numBlocks);
        var uncs = new int[numBlocks];
        for (int i = 0; i < numBlocks; i++)
        {
            int start = i * ChunkSize;
            int len = Math.Min(ChunkSize, n - start);
            comps.Add(EncodeLiteralBlock(data.AsSpan(start, len)));   // literal-only: always liblzo2-valid
            uncs[i] = len;
        }

        using var ms = new MemoryStream();
        WriteU32(ms, (uint)numBlocks);
        int cum = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            WriteU32(ms, (uint)comps[i].Length);   // compressed size
            WriteU32(ms, (uint)uncs[i]);            // uncompressed size
            WriteU32(ms, (uint)cum);                // cumulative offset of this block within the data area
            cum += comps[i].Length;
        }
        foreach (var c in comps) ms.Write(c, 0, c.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// Streaming archive core: write a complete RFA to a seekable stream, pulling each entry's region only when
    /// it is its turn to be written so we never hold the whole archive (or even all regions) in memory at once.
    /// </summary>
    /// <remarks>All offset arithmetic is <see cref="long"/>; the values are stored as the container's <c>u32</c>
    /// fields. The header's <c>tocOffset</c> is written as a placeholder and patched at the end, which is why the
    /// stream must be seekable. Offsets are taken relative to <paramref name="output"/>'s starting position, so the
    /// archive may begin part-way into a stream.</remarks>
    private static void StreamArchive(Stream output, int count,
        Func<int, string> name, Func<int, (byte[] Region, int Unc)> getRegion)
    {
        if (!output.CanSeek)
            throw new ArgumentException("RFA writing needs a seekable stream — the header's tocOffset is patched last.", nameof(output));

        long start = output.Position;
        WriteU32(output, 0);                       // tocOffset placeholder (patched after the regions are sized)
        WriteU32(output, 1);                       // version
        output.Write(new byte[HeaderSize - 8], 0, HeaderSize - 8);

        var offsets = new long[count];
        var blockSizes = new int[count];
        var names = new byte[count][];
        var uncs = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = output.Position - start;
            var (region, unc) = getRegion(i);
            output.Write(region, 0, region.Length);
            blockSizes[i] = region.Length;
            names[i] = Encoding.Latin1.GetBytes(name(i));
            uncs[i] = unc;
        }

        long tocOffset = output.Position - start;
        if (tocOffset > uint.MaxValue)
            throw new NotSupportedException($"RFA archive is {tocOffset:N0} bytes; the container's u32 offsets cap it at {uint.MaxValue:N0}.");

        WriteU32(output, (uint)count);
        for (int i = 0; i < count; i++)
        {
            WriteU32(output, (uint)names[i].Length);
            output.Write(names[i], 0, names[i].Length);
            WriteU32(output, (uint)blockSizes[i]);   // blockSize (whole data region)
            WriteU32(output, (uint)uncs[i]);         // uncompressed size
            WriteU32(output, (uint)offsets[i]);      // offset of the data region
            WriteU32(output, 0); WriteU32(output, 0); WriteU32(output, 0);
        }

        long end = output.Position;
        output.Position = start;
        WriteU32(output, (uint)tocOffset);           // patch the real TOC offset
        output.Position = end;
    }

    /// <summary>Stream an archive of ordered (name, bytes) entries straight to <paramref name="output"/>.</summary>
    public static void WriteTo(Stream output, IReadOnlyList<(string Name, byte[] Data)> entries)
        => StreamArchive(output, entries.Count, i => entries[i].Name,
                         i => (BuildRegion(entries[i].Data), entries[i].Data.Length));

    /// <summary>Stream an archive whose entry data is produced on demand (so the caller need not hold it all at
    /// once). <paramref name="data"/> is invoked exactly once per index, in order.</summary>
    public static void WriteTo(Stream output, int count, Func<int, string> name, Func<int, byte[]> data)
        => StreamArchive(output, count, name, i => { var d = data(i); return (BuildRegion(d), d.Length); });

    /// <summary>Assemble a complete archive from ordered (name, bytes) entries into a single <c>byte[]</c>.
    /// Convenient for small archives; for output that would exceed the ~2 GiB a managed array can hold use
    /// <see cref="WriteFile"/> / <see cref="WriteTo(Stream, IReadOnlyList{ValueTuple{string, byte[]}})"/> instead.</summary>
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        using var ms = new MemoryStream();
        WriteTo(ms, entries);
        return ms.ToArray();
    }

    /// <summary>Stream an archive of ordered (name, bytes) entries directly to a file — never materializes the
    /// whole archive in memory, so it packs multi-GiB archives the <c>byte[]</c>-returning <see cref="Build"/> can't.</summary>
    public static void WriteFile(string path, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        WriteTo(fs, entries);
    }

    /// <summary>Re-pack an existing archive into <paramref name="output"/>, substituting some entries' bytes
    /// (matched by exact name, case-insensitive). Changed entries are re-encoded (literal-only, liblzo2-safe);
    /// every UNCHANGED entry's original on-disk region is copied through verbatim — so untouched files keep their
    /// exact retail bytes and are never put at risk by our encoder. Names and order are preserved.</summary>
    public static void RepackTo(Stream output, RfaArchive original, IReadOnlyDictionary<string, byte[]> replacements)
    {
        var ci = new Dictionary<string, byte[]>(replacements, StringComparer.OrdinalIgnoreCase);
        var ents = original.Entries;
        StreamArchive(output, ents.Count, i => ents[i].Name,
            i => ci.TryGetValue(ents[i].Name, out var rep)
                ? (BuildRegion(rep), rep.Length)                              // edited: re-encode (literal-only)
                : (original.RawRegion(ents[i]), ents[i].UncompressedSize));   // untouched: verbatim passthrough
    }

    /// <summary>In-memory <see cref="RepackTo"/> (see its remarks). For a huge base archive use
    /// <see cref="RepackToFile"/>, which streams and so isn't bounded by the managed-array size limit.</summary>
    public static byte[] Repack(RfaArchive original, IReadOnlyDictionary<string, byte[]> replacements)
    {
        using var ms = new MemoryStream();
        RepackTo(ms, original, replacements);
        return ms.ToArray();
    }

    /// <summary>Stream a repack straight to a file (low memory, no array-size ceiling).</summary>
    public static void RepackToFile(string path, RfaArchive original, IReadOnlyDictionary<string, byte[]> replacements)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        RepackTo(fs, original, replacements);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }
}
