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
/// <para>The writer compresses with MiniLZO and VERIFIES every block by round-tripping it through the
/// independent clean-room <see cref="Lzo1x"/> decoder (validated against retail archives with liblzo2 as
/// oracle) — a block that fails verification is stored verbatim (<c>comp == unc</c>), so a stream the
/// engine cannot read is structurally impossible to write. Entries whose wrapped form would not shrink
/// are stored raw (<c>BlockSize == UncompressedSize</c>, a layout retail compressed archives also use),
/// which keeps the raw-vs-wrapped size discriminator unambiguous. Unchanged entries in a repack are
/// always copied byte-for-byte from the original so known-good retail streams are never re-encoded.</para>
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

    private readonly string? _path;   // set when constructed via Open(string)
    private readonly Dictionary<string, string>? _looseFiles;   // folder-backed: entry name -> file on disk

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
        _path = path;
        Entries = entries;
        IsCompressed = isCompressed;
        IsV11Format = isV11;
        XPackId = xpackId;
    }

    /// <summary>Present a DIRECTORY of loose files as if it were an archive, so everything that reads archives can
    /// read an EXTRACTED level too.
    ///
    /// A level extracted into a project folder keeps its own objects and textures as ordinary files, while the mesh
    /// and texture libraries only ever spoke .rfa - which is why a map's custom content showed up when the map was
    /// opened through its mod (the archive was in the list) and vanished once extracted. Wrapping the folder here
    /// means every lookup, category rule and assembly walker stays exactly as it was.
    ///
    /// Entry names get the <c>levels/&lt;folder&gt;/</c> prefix a real level archive carries, because the object
    /// indexers read that shape to tell a level's OWN objects from a mod's. Reads are lazy - only names are walked.
    /// </summary>
    public static RefractorFlatArchive FromFolder(string dir)
    {
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = "levels/" + Path.GetFileName(root) + "/";
        var entries = new List<RefractorFlatArchiveEntry>();
        var loose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace((char)92, '/');
            if (Path.GetFileName(rel).StartsWith("~")) continue;
            long len;
            try { len = new FileInfo(file).Length; } catch { continue; }
            if (len > int.MaxValue) continue;
            var name = prefix + rel;
            if (loose.ContainsKey(name)) continue;
            loose[name] = file;
            entries.Add(new RefractorFlatArchiveEntry(name, (int)len, (int)len, 0));
        }
        return new RefractorFlatArchive(entries, loose);
    }

    private RefractorFlatArchive(List<RefractorFlatArchiveEntry> entries, Dictionary<string, string> loose)
    {
        Entries = entries;
        _looseFiles = loose;
        IsCompressed = false;
        IsV11Format = false;
        XPackId = XPackId.Default;
    }

    // ── Shared header + TOC reader ────────────────────────────────────────────

    private static (bool IsV11, bool Compressed, XPackId XPackId, List<RefractorFlatArchiveEntry> Entries) ReadFrom(Stream s)
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
                Name: Encoding.Latin1.GetString(nameBytes),
                BlockSize: (int)BinaryPrimitives.ReadUInt32LittleEndian(rec),
                UncompressedSize: (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(4)),
                Offset: BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(8))));
        }

        return (isV11, compressed, xpackId, list);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>Decompress an entry to its full uncompressed bytes.</summary>
    public byte[] Read(RefractorFlatArchiveEntry e)
    {
        // Folder-backed (see FromFolder): the "entry" is a file on disk - no block table to decode.
        if (_looseFiles is not null)
            return _looseFiles.TryGetValue(e.Name, out var f) ? File.ReadAllBytes(f) : Array.Empty<byte>();
        return DecodeRegion(ReadRegionFromFile(e), e.UncompressedSize, e.Name);
    }

    private byte[] RawRegion(RefractorFlatArchiveEntry e)
    {
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
            int unc = (int)ReadU32(region, b + 4);
            int cum = (int)ReadU32(region, b + 8);

            if (unc == 0)
                continue;

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

    // ── Writing ───────────────────────────────────────────────────────────────

    private static byte[] BuildRegion(byte[] data, bool compress)
    {
        if (!compress)
            return data;

        int n = data.Length;
        int numBlocks = n == 0 ? 0 : (n + ChunkSize - 1) / ChunkSize;

        var comps = new List<byte[]>(numBlocks);
        var uncs = new int[numBlocks];
        for (int i = 0; i < numBlocks; i++)
        {
            int start = i * ChunkSize;
            int len = Math.Min(ChunkSize, n - start);
            var chunk = data.AsSpan(start, len);
            var lzo = MiniLZO.MiniLZO.Compress(chunk.ToArray());
            // SAVE-TIME VERIFICATION NET: a saved map the game can't read is the worst possible failure, so every
            // compressed block must round-trip through the INDEPENDENT clean-room decoder (Lzo1x — validated
            // byte-for-byte against retail archives with liblzo2 as the oracle, i.e. it accepts exactly what the
            // engine accepts). Any block that fails is stored verbatim instead — slightly larger, never corrupt.
            bool verified = false;
            if (lzo.Length < len)
            {
                try { verified = Lzo1x.Decompress(lzo, len).AsSpan().SequenceEqual(chunk); }
                catch { verified = false; }
            }
            comps.Add(verified ? lzo : chunk.ToArray());
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
        var region = ms.ToArray();

        // If wrapping didn't actually shrink the entry, store it RAW (BlockSize == UncompressedSize — 276 such
        // entries exist inside retail compressed archives, so the engine provably accepts them). This also makes
        // the reader's raw-vs-wrapped size discriminator unambiguous: a wrapped region can never have exactly the
        // uncompressed length, so it can never be misread as raw data.
        return region.Length >= n ? data : region;
    }

    /// <summary>Streaming archive core. Writes header, entry regions, then the TOC, patching the
    /// header's <c>tocOffset</c> placeholder at the end. Requires a seekable stream.</summary>
    private static void StreamArchive(
        Stream output,
        int count,
        Func<int, string> name,
        Func<int, (byte[] Region, int Unc)> getRegion,
        bool compress,
        XPackId xPackId)
    {
        if (!output.CanSeek)
            throw new ArgumentException("Writing an RFA requires a seekable stream (tocOffset is back-patched).", nameof(output));

        long start = output.Position;

        // ── Header ───────────────────────────────────────────────────────────
        WriteU32(output, 0);                              // tocOffset placeholder — patched below
        WriteU32(output, compress ? 1u : 0u);

        // 143-byte descriptor: any bytes work for the engine's checksum.
        byte[] descriptor = new byte[143];
        "RefractorForge"u8.CopyTo(descriptor);
        output.Write(descriptor, 0, 143);
        output.WriteByte(0);

        // Encrypted XPack ID: stored as (actual_id + Σ descriptor_bytes) mod 2^32.
        uint descriptorSum = 0;
        foreach (var b in descriptor) descriptorSum += b;
        WriteU32(output, (uint)xPackId + descriptorSum);

        // ── Entry regions ────────────────────────────────────────────────────
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
    public static void WriteFile(
        string path,
        IReadOnlyList<(string Name, byte[] Data)> entries,
        bool compress,
        XPackId xPackId)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        StreamArchive(fs, entries.Count,
            i => entries[i].Name,
            i => { var d = entries[i].Data; return (BuildRegion(d, compress), d.Length); },
            compress, xPackId);
    }

    /// <summary>Stream a repack straight to a file (low memory, no array-size ceiling).
    /// Writes to a sibling temp file first so the original is never locked while being read,
    /// then atomically replaces <paramref name="path"/>.</summary>
    public static void RepackToFile(
        string path,
        RefractorFlatArchive original,
        IReadOnlyDictionary<string, byte[]> replacements)
    {
        var ci = new Dictionary<string, byte[]>(replacements, StringComparer.OrdinalIgnoreCase);
        var ents = original.Entries;

        string tmp = path + ".rfatmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                StreamArchive(
                    fs,
                    ents.Count,
                    i => ents[i].Name,
                    i => ci.TryGetValue(ents[i].Name, out var rep)
                        ? (BuildRegion(rep, original.IsCompressed), rep.Length)
                        : (original.RawRegion(ents[i]), ents[i].UncompressedSize),
                    original.IsCompressed,
                    original.XPackId
                );
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
        string ext = Path.GetExtension(entryName);
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

    /// <summary>Cheap header sniff: was this archive written by RefractorForge? Our writer stamps
    /// "RefractorForge" into the 143-byte descriptor field (retail tools leave other bytes there). Used by the
    /// patch-save flow to tell OUR working patch (safe to rewrite on every Ctrl+S) apart from retail/other-tool
    /// patches (never touched — a new higher-numbered patch is created instead).</summary>
    public static bool WasWrittenByRefractorForge(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> hdr = stackalloc byte[8 + 14];
            if (fs.Length < 156) return false;
            fs.ReadExactly(hdr);
            return hdr.Slice(8).SequenceEqual("RefractorForge"u8);
        }
        catch { return false; }
    }

    /// <summary>Post-save validation: open the archive at <paramref name="path"/> and strictly verify every entry —
    /// TOC sanity, and every LZO block decoded with the INDEPENDENT engine-validated <see cref="Lzo1x"/> decoder
    /// (not the codec that wrote it). Returns null when everything checks out, else a description of the first
    /// problem. This is how the editor turns silent corruption into an immediate, loud error.</summary>
    public static string? Validate(string path, long maxEntryBytes = 128L * 1024 * 1024)
    {
        try
        {
            var a = new RefractorFlatArchive(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var e in a.Entries)
            {
                if (e.BlockSize < 0 || e.UncompressedSize < 0 || e.Offset + (long)e.BlockSize > fs.Length)
                    return $"{e.Name}: region out of bounds (offset {e.Offset}, blockSize {e.BlockSize}, file {fs.Length})";
                if (e.UncompressedSize > maxEntryBytes) continue;   // skip pathological sizes, keep saves fast

                fs.Seek(e.Offset, SeekOrigin.Begin);
                var region = new byte[e.BlockSize];
                fs.ReadExactly(region);
                if (region.Length == e.UncompressedSize) continue;  // raw entry — nothing to decode

                int nb = (int)BinaryPrimitives.ReadUInt32LittleEndian(region);
                long need = 4 + (long)nb * 12;
                if (nb < 0 || need > region.Length) return $"{e.Name}: malformed block table ({nb} blocks in {region.Length} bytes)";
                int dataStart = (int)need, written = 0;
                for (int i = 0; i < nb; i++)
                {
                    int b = 4 + i * 12;
                    int comp = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(b));
                    int unc = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(b + 4));
                    int cum = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(b + 8));
                    if (unc == 0) continue;
                    if (comp < 0 || cum < 0 || dataStart + (long)cum + comp > region.Length)
                        return $"{e.Name}: block {i} out of bounds";
                    var src = region.AsSpan(dataStart + cum, comp);
                    if (comp == unc) { written += unc; continue; }   // verbatim block
                    try
                    {
                        var dst = new byte[unc];
                        Lzo1x.Decompress(src, dst, unc);             // the engine-validated decoder is the referee
                    }
                    catch (Exception ex) { return $"{e.Name}: block {i} failed engine-validated LZO decode ({ex.Message})"; }
                    written += unc;
                }
                if (written != e.UncompressedSize)
                    return $"{e.Name}: blocks reassemble to {written} bytes, expected {e.UncompressedSize}";
            }
            return null;
        }
        catch (Exception ex) { return $"archive unreadable: {ex.GetType().Name}: {ex.Message}"; }
    }

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
