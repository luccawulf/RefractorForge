using System.Buffers.Binary;
using System.Text;

namespace RefractorForge.Formats.Rfa;

// ── Archive ──────────────────────────────────────────────────────────────────

/// <summary>
/// Reader and writer for Refractor Flat Archive (<c>.rfa</c>) files
/// (Battlefield 1942 / Vietnam <c>Archives/*.rfa</c>).
/// </summary>
/// <remarks>
/// <para>Two header variants are supported on read:</para>
/// <list type="bullet">
///   <item><b>Standard (v1.0):</b> <c>u32 tocOffset, u32 compressed, 143 descriptor bytes,
///   u8 unknown, u32 encryptedXPackId</c> — 156 bytes total. The XPack ID is stored as
///   <c>actualId + Σ(descriptorBytes)</c> so a simple checksum obscures the constant.</item>
///   <item><b>v1.1:</b> 28-byte ASCII prefix <c>"Refractor2 FlatArchive 1.1  "</c> followed by
///   <c>u32 tocOffset, u32 compressed</c> — no XPack field.</item>
/// </list>
/// <para>Archives are always written in standard format (v1.0). Each entry's data is either a
/// raw byte region (<c>BlockSize == UncompressedSize</c> → verbatim, no LZO) or a block-wrapped
/// region (<c>u32 numBlocks</c>, then per-block descriptors and LZO1X payloads).</para>
/// <para>The writer uses real LZO1X compression (via <see cref="Lzo1x.Compress"/>). Blocks that
/// do not compress are stored verbatim (<c>comp == unc</c>) so the engine copies them directly
/// without invoking the decompressor. Unchanged entries in a repack are always copied byte-for-byte
/// from the original so their known-good retail LZO streams are never re-encoded.</para>
/// <para>All offset arithmetic uses <see cref="long"/> so streaming writes of multi-GiB archives
/// (e.g. uncompressed <c>texture.rfa</c> ≈ 2.3 GiB) work without exceeding the managed-array
/// size limit. The on-disk container uses <c>u32</c> offsets, which caps archives at ~4 GiB.</para>
/// </remarks>
public sealed class RefractorFlatArchive
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>Uncompressed bytes per block (matches retail archives).</summary>
    private const int ChunkSize = 32768;

    private static ReadOnlySpan<byte> V11Signature
        => "Refractor2 FlatArchive 1.1  "u8;   // exactly 28 bytes

    // ── State ────────────────────────────────────────────────────────────────

    private readonly byte[]? _data;   // set when constructed via Load(byte[])
    private readonly string? _path;   // set when constructed via Open(string)

    public IReadOnlyList<RefractorFlatArchiveEntry> Entries { get; }

    /// <summary>Whether entry blocks are LZO-compressed, as recorded in the archive header.</summary>
    public bool IsCompressed { get; }

    /// <summary><c>true</c> for Refractor2 v1.1 format archives. These have an extended 28-byte
    /// ASCII prefix and no XPack ID field.</summary>
    public bool IsV11Format { get; }

    /// <summary>The expansion-pack binding from the header.
    /// Always <see cref="XPackId.Default"/> for v1.1 archives (they carry no XPack field).</summary>
    public XPackId XPackId { get; }

    public RefractorFlatArchive(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (isV11, isCompressed, xpackId, entries) = ReadFrom(fs);
        _data = null;
        _path = path;
        Entries = entries;
        IsCompressed = isCompressed;
        IsV11Format = isV11;
        XPackId = xpackId;
    }

    // ── Shared header + TOC reader ────────────────────────────────────────────

    private static (bool IsV11, bool Compressed, XPackId XPackId, List<RefractorFlatArchiveEntry> Entries)
        ReadFrom(Stream s)
    {
        Span<byte> u4 = stackalloc byte[4];
        Span<byte> sig = stackalloc byte[28];

        s.ReadExactly(sig);
        bool isV11 = sig.SequenceEqual(V11Signature);
        if (!isV11) s.Seek(0, SeekOrigin.Begin);

        s.ReadExactly(u4); uint tocOffset = BinaryPrimitives.ReadUInt32LittleEndian(u4);
        s.ReadExactly(u4); bool compressed = BinaryPrimitives.ReadUInt32LittleEndian(u4) == 1;

        XPackId xpackId = XPackId.Default;
        if (!isV11)
        {
            // 143-byte descriptor → checksum for XPack ID, then 1 unknown byte, then encrypted ID.
            Span<byte> desc = stackalloc byte[143];
            s.ReadExactly(desc);
            s.ReadByte();
            uint sum = 0; foreach (var b in desc) sum += b;
            s.ReadExactly(u4);
            xpackId = (XPackId)(BinaryPrimitives.ReadUInt32LittleEndian(u4) - sum);
        }

        s.Seek(tocOffset, SeekOrigin.Begin);
        s.ReadExactly(u4);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(u4);

        var list = new List<RefractorFlatArchiveEntry>(count);
        Span<byte> rec = stackalloc byte[24];
        for (int i = 0; i < count; i++)
        {
            s.ReadExactly(u4);
            var nameBytes = new byte[(int)BinaryPrimitives.ReadUInt32LittleEndian(u4)];
            s.ReadExactly(nameBytes);
            s.ReadExactly(rec);
            list.Add(new RefractorFlatArchiveEntry(
                Name:             Encoding.Latin1.GetString(nameBytes),
                BlockSize:        (int)BinaryPrimitives.ReadUInt32LittleEndian(rec),
                UncompressedSize: (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(4)),
                Offset:                BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(8))));
        }

        return (isV11, compressed, xpackId, list);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>Decompress an entry to its full uncompressed bytes.</summary>
    public byte[] Read(RefractorFlatArchiveEntry e)
    {
        if (_data is not null)
            return DecodeRegion(_data.AsSpan((int)e.Offset, e.BlockSize), e.UncompressedSize, e.Name);
        return DecodeRegion(ReadRegionFromFile(e), e.UncompressedSize, e.Name);
    }

    private byte[] RawRegion(RefractorFlatArchiveEntry e)
    {
        if (_data is not null)
            return _data.AsSpan((int)e.Offset, e.BlockSize).ToArray();
        return ReadRegionFromFile(e);
    }

    private byte[] ReadRegionFromFile(RefractorFlatArchiveEntry e)
    {
        using var fs = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        var buf = new byte[e.BlockSize];
        fs.ReadExactly(buf);
        return buf;
    }

    private static byte[] DecodeRegion(ReadOnlySpan<byte> region, int uncompressedSize, string? name = null)
    {
        if (region.Length == uncompressedSize)
            return region.ToArray();

        int numBlocks = (int)ReadU32(region, 0);
        int descBase = 4;
        int dataStart = descBase + numBlocks * 12;

        var result = new byte[uncompressedSize];
        int written = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int b = descBase + i * 12;
            int comp = (int)ReadU32(region, b);
            int unc  = (int)ReadU32(region, b + 4);
            int cum  = (int)ReadU32(region, b + 8);
            // Skip trailing 0-length terminator blocks some writers append (e.g. Dambuster/materialmap.raw:
            // 8 real blocks + a 9th comp=4/unc=0 marker). Feeding unc==0 to the decoder caused crashes.
            if (unc == 0) continue;
            var src = region.Slice(dataStart + cum, comp);
            var dst = result.AsSpan(written, unc);
            if (comp == unc)
                src.CopyTo(dst);
            else
            {
                var dstArray = MiniLZO.MiniLZO.Decompress(src.ToArray(), unc);
                dstArray.CopyTo(dst);
            }
            written += unc;
        }
        if (written != uncompressedSize)
            throw new InvalidDataException($"'{name}': reassembled {written} bytes, expected {uncompressedSize}.");
        return result;
    }

    // ── TOC-only streaming read ───────────────────────────────────────────────

    /// <summary>Read just an archive's directory table without loading the whole file — the only viable
    /// approach for archives too large to fit in a single managed <c>byte[]</c>.</summary>
    public static IReadOnlyList<RefractorFlatArchiveEntry> ReadToc(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadFrom(fs).Entries;
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    private static byte[] BuildRegion(byte[] data, bool compress)
    {
        if (!compress)
            return data;

        int n = data.Length;
        int numBlocks = n == 0 ? 0 : (n + ChunkSize - 1) / ChunkSize;

        var comps = new List<byte[]>(numBlocks);
        var uncs  = new int[numBlocks];
        for (int i = 0; i < numBlocks; i++)
        {
            int start = i * ChunkSize;
            int len = Math.Min(ChunkSize, n - start);
            var chunk = data.AsSpan(start, len);
            var lzo = MiniLZO.MiniLZO.Compress(chunk.ToArray());
            // If LZO grows the block (incompressible data), store the raw chunk instead.
            comps.Add(lzo.Length < len ? lzo : chunk.ToArray());
            uncs[i] = len;
        }

        using var ms = new MemoryStream();
        WriteU32(ms, (uint)numBlocks);
        int cum = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            WriteU32(ms, (uint)comps[i].Length);
            WriteU32(ms, (uint)uncs[i]);
            WriteU32(ms, (uint)cum);
            cum += comps[i].Length;
        }
        foreach (var c in comps) ms.Write(c, 0, c.Length);
        return ms.ToArray();
    }

    /// <summary>Streaming archive core. Writes header, entry regions, then the TOC, patching the
    /// header's <c>tocOffset</c> placeholder at the end. Requires a seekable stream.</summary>
    private static void StreamArchive(Stream output, int count,
        Func<int, string> name, Func<int, (byte[] Region, int Unc)> getRegion,
        WriteOptions options)
    {
        if (!output.CanSeek)
            throw new ArgumentException("Writing an RFA requires a seekable stream (tocOffset is back-patched).", nameof(output));

        long start = output.Position;

        // ── Header ───────────────────────────────────────────────────────────
        WriteU32(output, 0);                              // tocOffset placeholder — patched below
        WriteU32(output, options.Compress ? 1u : 0u);

        // 143-byte descriptor: any bytes work for the engine's checksum.
        byte[] descriptor = new byte[143];
        "RefractorForge"u8.CopyTo(descriptor);
        output.Write(descriptor, 0, 143);
        output.WriteByte(0);

        // Encrypted XPack ID: stored as (actual_id + Σ descriptor_bytes) mod 2^32.
        uint descriptorSum = 0;
        foreach (var b in descriptor) descriptorSum += b;
        WriteU32(output, (uint)options.XPackId + descriptorSum);

        // ── Entry regions ────────────────────────────────────────────────────
        var offsets    = new long[count];
        var blockSizes = new int[count];
        var names      = new byte[count][];
        var uncs       = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = output.Position - start;
            var (region, unc) = getRegion(i);
            output.Write(region, 0, region.Length);
            blockSizes[i] = region.Length;
            names[i]      = Encoding.Latin1.GetBytes(name(i));
            uncs[i]       = unc;
        }

        // ── TOC ──────────────────────────────────────────────────────────────
        long tocOffset = output.Position - start;
        if (tocOffset > uint.MaxValue)
            throw new NotSupportedException(
                $"Archive is {tocOffset:N0} bytes — the u32 container offsets cap it at {uint.MaxValue:N0} bytes.");

        WriteU32(output, (uint)count);
        for (int i = 0; i < count; i++)
        {
            WriteU32(output, (uint)names[i].Length);
            output.Write(names[i], 0, names[i].Length);
            WriteU32(output, (uint)blockSizes[i]);
            WriteU32(output, (uint)uncs[i]);
            WriteU32(output, (uint)offsets[i]);
            WriteU32(output, 0); WriteU32(output, 0); WriteU32(output, 0);   // reserved
        }

        // ── Patch tocOffset ───────────────────────────────────────────────────
        long end = output.Position;
        output.Position = start;
        WriteU32(output, (uint)tocOffset);
        output.Position = end;
    }

    // ── Public write API ─────────────────────────────────────────────────────

    /// <summary>Stream an archive of ordered <c>(name, bytes)</c> entries directly to a file.</summary>
    public static void WriteFile(string path, IReadOnlyList<(string Name, byte[] Data)> entries,
        WriteOptions? options = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        options ??= WriteOptions.Default;
        StreamArchive(fs, entries.Count,
            i => entries[i].Name,
            i => { var d = entries[i].Data; return (BuildRegion(d, options.Compress), d.Length); },
            options);
    }

    /// <summary>Stream a repack straight to a file (low memory, no array-size ceiling).
    /// Writes to a sibling temp file first so the original is never locked while being read,
    /// then atomically replaces <paramref name="path"/>.</summary>
    public static void RepackToFile(string path, RefractorFlatArchive original,
        IReadOnlyDictionary<string, byte[]> replacements, WriteOptions? options = null)
    {
        options ??= new WriteOptions { Compress = original.IsCompressed, XPackId = original.XPackId };
        var ci = new Dictionary<string, byte[]>(replacements, StringComparer.OrdinalIgnoreCase);
        var ents = original.Entries;

        string tmp = path + ".rfatmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                StreamArchive(fs, ents.Count, i => ents[i].Name,
                    i => ci.TryGetValue(ents[i].Name, out var rep)
                        ? (BuildRegion(rep, options.Compress), rep.Length)
                        : (original.RawRegion(ents[i]), ents[i].UncompressedSize),
                    options);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ── Entry filtering ──────────────────────────────────────────────────────

    private static readonly HashSet<string> ClientOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".bik", ".dds", ".tga", ".wav" };

    private static readonly HashSet<string> ClientOnlyFileNames = new(StringComparer.OrdinalIgnoreCase)
        { "palette.pal", "envmap_g_.rcm", "lightmapshadowbits.lsb", "terrainpalette.pal", "textureprecache.dat" };

    /// <summary>Returns <c>true</c> when the entry is client-only (visuals, audio, precomputed light)
    /// and should be excluded from a dedicated-server archive.</summary>
    public static bool IsClientOnlyEntry(string entryName)
    {
        string ext  = Path.GetExtension(entryName);
        string file = Path.GetFileName(entryName);
        return ClientOnlyExtensions.Contains(ext) || ClientOnlyFileNames.Contains(file);
    }

    /// <summary>Decompress and return all entries that are <b>not</b> client-only, ready to pass to
    /// <see cref="Build"/> or <see cref="WriteFile"/> for producing a dedicated-server archive.</summary>
    public List<(string Name, byte[] Data)> ReadServerEntries()
        => Entries
            .Where(e => !IsClientOnlyEntry(e.Name))
            .Select(e => (e.Name, Read(e)))
            .ToList();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static uint ReadU32(ReadOnlySpan<byte> d, int p)
        => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(p));

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }
}
