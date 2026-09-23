using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
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
/// <para>Archives are always written in standard format (v1.0). In an UNCOMPRESSED archive (header flag 0) every
/// entry is a raw byte region. In a COMPRESSED archive (flag 1) every entry is a block-wrapped region
/// (<c>u32 numBlocks</c>, then per-block descriptors and LZO1X payloads) - the engine LZO-decodes every entry of a
/// compressed archive, so a raw entry or a verbatim block in one arrives corrupt. A census of 9,207 archives found
/// no shipped archive that does either; the ones that did were exactly the builds that crashed BF Vietnam
/// ("Couldn't decompress block", then a Runtime Error once a garbled .con left templates undefined).</para>
/// <para>The writer compresses with MiniLZO and VERIFIES every block by round-tripping it through the
/// independent clean-room <see cref="Lzo1x"/> decoder (validated against retail archives with liblzo2 as
/// oracle). A block that does not shrink, or fails verification, is written as a literal-only LZO stream
/// (<see cref="Lzo1x.EncodeLiteral"/>) - the form retail itself uses for incompressible blocks. Unchanged entries
/// in a repack are copied byte-for-byte from the original, so known-good retail streams are never re-encoded;
/// the one exception is an entry an older writer stored raw inside a compressed archive, which a repack heals.</para>
/// <para>A zero-length entry keeps its historical form (an empty region) in both kinds of archive: no retail
/// compressed archive contains one, so there is no proven encoding to switch to.</para>
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

    /// <summary>The file this archive was read from, or null for one presented from a folder or built in memory.
    /// Tools show it so a person can see WHICH layer of a mod chain a file actually came from.</summary>
    public string? SourcePath => _path;
    private readonly Dictionary<string, string>? _looseFiles;   // folder-backed: entry name -> file on disk
    // The source archive's own container bytes, kept verbatim so a repack can reproduce them rather than
    // substituting our own. Porting work on a real BFV map showed how little slack the container has: a rebuilt
    // archive that changed these is where three "the map crashes" reports came from. Preserving them also makes a
    // no-op repack byte-identical, which is a far stronger gate than "the entries still decode".
    private readonly byte[]? _descriptor;                       // the 143-byte blob + its trailing byte
    private readonly Dictionary<string, byte[]>? _entryTrailers; // entry name -> its 12 TOC trailer bytes
    private readonly byte[]? _tocTail;                           // the 4 bytes after the entry table

    public IReadOnlyList<RefractorFlatArchiveEntry> Entries { get; }

    private Dictionary<string, RefractorFlatArchiveEntry>? _byName;   // built on the first lookup, then read-only

    /// <summary>Entry names compared the way the engine's paths compare: any case, and <c>/</c> the same as
    /// <c>\</c> - retail archives store either (BF1942's <c>objects\...</c> beside <c>texture/...</c>), and a tool
    /// that writes the other one addresses the same file.</summary>
    public static IEqualityComparer<string> NameComparer { get; } = new EntryNameComparer();

    /// <summary>The entry stored under <paramref name="name"/>, compared by <see cref="NameComparer"/> - one
    /// dictionary lookup, where every caller used to walk <see cref="Entries"/>. An archive that stores one name
    /// twice answers with the first in its table. Safe to call from several threads.</summary>
    public bool TryGetEntry(string name, [MaybeNullWhen(false)] out RefractorFlatArchiveEntry entry)
    {
        var byName = _byName;
        if (byName is null)
        {
            byName = new Dictionary<string, RefractorFlatArchiveEntry>(Entries.Count, NameComparer);
            foreach (var e in Entries) byName.TryAdd(e.Name, e);
            byName = Interlocked.CompareExchange(ref _byName, byName, null) ?? byName;
        }
        return byName.TryGetValue(name, out entry);
    }

    private sealed class EntryNameComparer : IEqualityComparer<string>
    {
        private static char Fold(char c) => c == (char)92 ? '/' : char.ToUpperInvariant(c);

        public bool Equals(string? a, string? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i] && Fold(a[i]) != Fold(b[i])) return false;
            return true;
        }

        public int GetHashCode(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) h = (h ^ Fold(c)) * 16777619;
            return (int)h;
        }
    }

    /// <summary>Whether entry blocks are LZO-compressed, as recorded in the archive header.</summary>
    public bool IsCompressed { get; }

    /// <summary><c>true</c> for Refractor2 v1.1 format archives. These have an extended 28-byte
    /// ASCII prefix and no XPack ID field.</summary>
    public bool IsV11Format { get; }

    /// <summary>The expansion-pack binding from the header.
    /// Always <see cref="XPackId.Default"/> for v1.1 archives (they carry no XPack field).</summary>
    public XPackId XPackId { get; }

    /// <summary>The 144 descriptor bytes after the header flag (the 143-byte blob plus its trailing byte), or null
    /// for a v1.1 archive or one presented from a folder. A new archive can borrow a retail archive's descriptor
    /// through the <see cref="WriteFile(string, IReadOnlyList{ValueTuple{string, byte[]}}, bool, XPackId, byte[])"/>
    /// overload.</summary>
    public byte[]? Descriptor => _descriptor is null ? null : (byte[])_descriptor.Clone();

    /// <summary>Whether the entry table is closed by the four extra bytes every retail archive carries.</summary>
    public bool HasTableTail => _tocTail is { Length: 4 };

    public RefractorFlatArchive(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var (isV11, isCompressed, xpackId, entries, descriptor, trailers, tail) = ReadFrom(fs);
        _path = path;
        Entries = entries;
        _descriptor = descriptor;
        _entryTrailers = trailers;
        _tocTail = tail;
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

    private static (bool IsV11, bool Compressed, XPackId XPackId, List<RefractorFlatArchiveEntry> Entries,
                    byte[]? Descriptor, Dictionary<string, byte[]> Trailers, byte[]? Tail) ReadFrom(Stream s)
    {
        Span<byte> u4 = stackalloc byte[4];
        Span<byte> sig = stackalloc byte[28];

        s.ReadExactly(sig);
        bool isV11 = sig.SequenceEqual(V11Signature);
        if (!isV11) s.Seek(0, SeekOrigin.Begin);

        s.ReadExactly(u4); uint tocOffset = BinaryPrimitives.ReadUInt32LittleEndian(u4);
        s.ReadExactly(u4); bool compressed = BinaryPrimitives.ReadUInt32LittleEndian(u4) == 1;

        XPackId xpackId = XPackId.Default;
        byte[]? descriptor = null;
        if (!isV11)
        {
            // 143-byte descriptor → checksum for XPack ID, then 1 unknown byte, then encrypted ID.
            Span<byte> desc = stackalloc byte[143];
            s.ReadExactly(desc);
            int descTail = s.ReadByte();
            descriptor = new byte[144];
            desc.CopyTo(descriptor);
            descriptor[143] = (byte)(descTail < 0 ? 0 : descTail);
            uint sum = 0; foreach (var b in desc) sum += b;
            s.ReadExactly(u4);
            xpackId = (XPackId)(BinaryPrimitives.ReadUInt32LittleEndian(u4) - sum);
        }

        s.Seek(tocOffset, SeekOrigin.Begin);
        s.ReadExactly(u4);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(u4);

        var list = new List<RefractorFlatArchiveEntry>(count);
        var trailers = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Span<byte> rec = stackalloc byte[24];
        for (int i = 0; i < count; i++)
        {
            s.ReadExactly(u4);
            var nameBytes = new byte[(int)BinaryPrimitives.ReadUInt32LittleEndian(u4)];
            s.ReadExactly(nameBytes);
            s.ReadExactly(rec);
            var entryName = Encoding.Latin1.GetString(nameBytes);
            list.Add(new RefractorFlatArchiveEntry(
                Name: entryName,
                BlockSize: (int)BinaryPrimitives.ReadUInt32LittleEndian(rec),
                UncompressedSize: (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(4)),
                Offset: BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(8))));
            trailers[entryName] = rec.Slice(12).ToArray();   // the 12 bytes after the offset
        }

        // SOME archives carry four more bytes after the table and some end right there - Kharkov has them,
        // Font.rfa does not. Read them only if they exist, and write back only what we read, or a repack gains or
        // loses 4 bytes. The no-op selftest caught this in both directions.
        byte[]? tail = null;
        try { var t = new byte[4]; s.ReadExactly(t); tail = t; } catch { tail = null; }

        return (isV11, compressed, xpackId, list, descriptor, trailers, tail);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>Decompress an entry to its full uncompressed bytes.</summary>
    public byte[] Read(RefractorFlatArchiveEntry e)
    {
        // Folder-backed (see FromFolder): the "entry" is a file on disk - no block table to decode.
        if (_looseFiles is not null)
            return _looseFiles.TryGetValue(e.Name, out var f) ? File.ReadAllBytes(f) : Array.Empty<byte>();
        return DecodeRegion(ReadRegionFromFile(e), e.UncompressedSize, e.Name, IsCompressed);
    }

    /// <summary>
    /// The first <paramref name="maxBytes"/> bytes of an entry - all of it when it is shorter - decoding only the
    /// blocks those bytes lie in: a DDS or WAV header, or the first screen of a hex view, from a 90 MB entry costs
    /// one 32 KiB block, not the file. For any entry <see cref="Read"/> reads, the result is the start of what it
    /// returns. A damaged block past the head is never touched, so the head of a broken entry still reads.
    /// </summary>
    public byte[] ReadHead(RefractorFlatArchiveEntry e, int maxBytes)
    {
        int n = Math.Min(Math.Max(maxBytes, 0), Math.Max(e.UncompressedSize, 0));
        if (n == 0) return Array.Empty<byte>();
        if (_looseFiles is not null)
        {
            if (!_looseFiles.TryGetValue(e.Name, out var f)) return Array.Empty<byte>();
            using var lf = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[(int)Math.Min(n, lf.Length)];
            lf.ReadExactly(head);
            return head;
        }
        if (n >= e.UncompressedSize) return Read(e);

        using var fs = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (e.BlockSize == e.UncompressedSize)
        {
            // Raw bytes in an uncompressed archive. In a compressed one a region of exactly the entry's size is usually
            // a block table that happens to add up to it, and only the whole region says which (DecodeRegion): take
            // the head of the full read there. Such entries are rare; see DecodeRegion.
            if (IsCompressed) return Read(e).AsSpan(0, n).ToArray();
            fs.Seek(e.Offset, SeekOrigin.Begin);
            var raw = new byte[n];
            fs.ReadExactly(raw);
            return raw;
        }

        // A block table: its descriptors, then the payload of each block up to the one the head ends in.
        if (e.BlockSize < 4) throw new InvalidDataException($"'{e.Name}': region of {e.BlockSize} bytes has no block table");
        fs.Seek(e.Offset, SeekOrigin.Begin);
        Span<byte> u4 = stackalloc byte[4];
        fs.ReadExactly(u4);
        long numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(u4);
        long dataStart = 4 + numBlocks * 12;
        if (dataStart > e.BlockSize) throw new InvalidDataException($"'{e.Name}': malformed block table ({numBlocks} blocks in {e.BlockSize} bytes)");
        var table = new byte[numBlocks * 12];
        fs.ReadExactly(table);

        var result = new byte[n];
        int written = 0;
        for (int i = 0; i < numBlocks && written < n; i++)
        {
            int comp = (int)ReadU32(table, i * 12), unc = (int)ReadU32(table, i * 12 + 4), cum = (int)ReadU32(table, i * 12 + 8);
            if (unc == 0) continue;
            if (comp < 0 || unc < 0 || cum < 0 || dataStart + (long)cum + comp > e.BlockSize || written + (long)unc > e.UncompressedSize)
                throw new InvalidDataException($"'{e.Name}': block {i} out of bounds");
            var src = new byte[comp];
            fs.Seek(e.Offset + dataStart + cum, SeekOrigin.Begin);
            fs.ReadExactly(src);
            int take = Math.Min(unc, n - written);
            // A block that ends inside the head decodes straight into it; the one the head stops in, into a scratch block.
            byte[]? partial = take < unc ? new byte[unc] : null;
            Span<byte> dst = partial is null ? result.AsSpan(written, unc) : partial;
            if (!TryDecodeBlock(src, dst, out var why)) throw new InvalidDataException($"'{e.Name}': block {i} {why}");
            partial?.AsSpan(0, take).CopyTo(result.AsSpan(written));
            written += take;
        }
        if (written != n) throw new InvalidDataException($"'{e.Name}': the blocks hold {written} bytes, expected at least {n}");
        return result;
    }

    private byte[] RawRegion(RefractorFlatArchiveEntry e)
    {
        return ReadRegionFromFile(e);
    }

    private byte[] ReadRegionFromFile(RefractorFlatArchiveEntry e)
    {
        using var fs = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        var buf = new byte[e.BlockSize];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>Decode one entry's region. The header flag decides what a region whose size equals the entry's
    /// uncompressed size is: in an uncompressed archive it is always raw bytes, but in a compressed archive it is
    /// usually a block table whose LZO streams happen to add up to exactly that size (875 such entries across
    /// retail and mod archives, e.g. BFV <c>M91Deploy.baf</c>: 4 + 12 + 45 = 61). Reading those as raw handed out a
    /// block table instead of the file, so the table is tried first and raw is only the fallback for the entries
    /// older writers stored raw.</summary>
    private static byte[] DecodeRegion(ReadOnlySpan<byte> region, int uncompressedSize, string? name, bool archiveCompressed)
    {
        if (region.Length == uncompressedSize)
        {
            if (archiveCompressed && IsBlockTable(region, uncompressedSize)
                && TryDecodeBlocks(region, uncompressedSize, out var decoded, out _))
                return decoded;
            return region.ToArray();
        }

        if (!TryDecodeBlocks(region, uncompressedSize, out var result, out var why))
            throw new InvalidDataException($"'{name}': {why}");
        return result;
    }

    /// <summary>Whether <paramref name="region"/> is shaped exactly like a block table for an entry of
    /// <paramref name="unc"/> bytes: one descriptor per block of at most 32 KiB, sizes adding up to the entry, and
    /// the payloads filling the region to its last byte. Strict on purpose - it is what tells an LZO entry that
    /// happens to be its own size apart from raw bytes.</summary>
    private static bool IsBlockTable(ReadOnlySpan<byte> region, int unc)
    {
        if (region.Length < 4 || unc <= 0) return false;
        long nb = ReadU32(region, 0);
        if (nb < 1 || nb > (unc + ChunkSize - 1) / ChunkSize + 1) return false;
        long dataStart = 4 + nb * 12;
        if (dataStart > region.Length) return false;
        long uncSum = 0, compSum = 0;
        for (int i = 0; i < nb; i++)
        {
            int b = 4 + i * 12;
            long comp = ReadU32(region, b), u = ReadU32(region, b + 4), cum = ReadU32(region, b + 8);
            if (u < 1 || u > ChunkSize || comp < 1) return false;
            if (dataStart + cum + comp > region.Length) return false;
            uncSum += u; compSum += comp;
        }
        return uncSum == unc && dataStart + compSum == region.Length;
    }

    /// <summary>Decode a block-wrapped region. A block whose stored size equals its uncompressed size is tried as
    /// LZO first (retail has streams that land on exactly their output size, e.g. four in BFV menu.rfa) and only
    /// copied verbatim when it is not a valid stream - the form older writers produced.</summary>
    private static bool TryDecodeBlocks(ReadOnlySpan<byte> region, int uncompressedSize, out byte[] result, out string why)
    {
        result = Array.Empty<byte>();
        if (region.Length < 4) { why = $"region of {region.Length} bytes has no block table"; return false; }
        int numBlocks = (int)ReadU32(region, 0);
        long dataStart = 4 + (long)numBlocks * 12;
        if (numBlocks < 0 || dataStart > region.Length) { why = $"malformed block table ({numBlocks} blocks in {region.Length} bytes)"; return false; }

        var buf = new byte[uncompressedSize];
        int written = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int b = 4 + i * 12;
            int comp = (int)ReadU32(region, b);
            int unc = (int)ReadU32(region, b + 4);
            int cum = (int)ReadU32(region, b + 8);
            if (unc == 0) continue;
            if (comp < 0 || unc < 0 || cum < 0 || dataStart + (long)cum + comp > region.Length || written + (long)unc > uncompressedSize)
            { why = $"block {i} out of bounds"; return false; }

            var src = region.Slice((int)dataStart + cum, comp);
            var dst = buf.AsSpan(written, unc);
            if (!TryDecodeBlock(src, dst, out var blockWhy)) { why = $"block {i} {blockWhy}"; return false; }
            written += unc;
        }
        if (written != uncompressedSize) { why = $"reassembled {written} bytes, expected {uncompressedSize}"; return false; }
        result = buf; why = "";
        return true;
    }

    /// <summary>One block into <paramref name="dst"/> (its uncompressed size). A stored size equal to the output size
    /// is tried as an LZO stream first and copied verbatim only when it is not one (see <see cref="TryDecodeBlocks"/>).</summary>
    private static bool TryDecodeBlock(ReadOnlySpan<byte> src, Span<byte> dst, out string why)
    {
        why = "";
        if (src.Length == dst.Length)
        {
            try { Lzo1x.Decompress(src, dst, dst.Length); }
            catch { src.CopyTo(dst); }
            return true;
        }
        try { MiniLZO.MiniLZO.Decompress(src.ToArray(), dst.Length).CopyTo(dst); }
        catch (Exception ex) { why = $"failed LZO decode ({ex.Message})"; return false; }
        return true;
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    private static byte[] BuildRegion(byte[] data, bool compress)
    {
        int n = data.Length;
        if (!compress || n == 0)
            return data;

        int numBlocks = (n + ChunkSize - 1) / ChunkSize;
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
            // engine accepts). A block that fails, or does not shrink, is written as a literal-only stream, which
            // every LZO decoder reads. Never verbatim: the engine would decode those bytes as LZO.
            bool verified = false;
            if (lzo.Length < len)
            {
                try { verified = Lzo1x.Decompress(lzo, len).AsSpan().SequenceEqual(chunk); }
                catch { verified = false; }
            }
            comps.Add(verified ? lzo : Lzo1x.EncodeLiteral(chunk));
            uncs[i] = len;
        }

        // A wrapped region that comes out exactly the entry's own size is legal, but readers older than the
        // flag-aware one took that size to mean "raw". Spend a few bytes to keep them reading it right.
        if (4 + 12 * numBlocks + comps.Sum(c => (long)c.Length) == n)
        {
            int last = numBlocks - 1, start = last * ChunkSize;
            comps[last] = Lzo1x.EncodeLiteral(data.AsSpan(start, n - start));
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

    /// <summary>Whether an existing region can be copied into a compressed archive unchanged. False for an entry an
    /// older writer stored raw, or with a verbatim block - both of which the engine would LZO-decode into garbage.</summary>
    private static bool IsValidCompressedRegion(ReadOnlySpan<byte> region, int unc)
    {
        if (unc == 0) return true;   // an empty entry keeps its historical form (see the class remarks)
        if (!IsBlockTable(region, unc)) return false;
        int nb = (int)ReadU32(region, 0), dataStart = 4 + nb * 12;
        for (int i = 0; i < nb; i++)
        {
            int b = 4 + i * 12;
            int comp = (int)ReadU32(region, b), u = (int)ReadU32(region, b + 4), cum = (int)ReadU32(region, b + 8);
            if (comp != u) continue;
            try { Lzo1x.Decompress(region.Slice(dataStart + cum, comp), u); }
            catch { return false; }
        }
        return true;
    }

    /// <summary>Streaming archive core. Writes header, entry regions, then the TOC, patching the
    /// header's <c>tocOffset</c> placeholder at the end. Requires a seekable stream.</summary>
    private static void StreamArchive(
        Stream output,
        int count,
        Func<int, string> name,
        Func<int, (byte[] Region, int Unc)> getRegion,
        bool compress,
        XPackId xPackId,
        byte[]? sourceDescriptor = null,
        Func<int, byte[]?>? sourceTrailer = null,
        byte[]? tocTail = null)
    {
        if (!output.CanSeek)
            throw new ArgumentException("Writing an RFA requires a seekable stream (tocOffset is back-patched).", nameof(output));

        long start = output.Position;

        // ── Header ───────────────────────────────────────────────────────────
        WriteU32(output, 0);                              // tocOffset placeholder — patched below
        WriteU32(output, compress ? 1u : 0u);

        // 143-byte descriptor: any bytes work for the engine's checksum. When repacking we write the SOURCE's
        // bytes back rather than our own, so the container comes out exactly as it went in; a brand-new archive
        // (a patch) still gets the RefractorForge stamp that WasWrittenByRefractorForge looks for.
        byte[] descriptor = new byte[143];
        byte descriptorTail = 0;
        if (sourceDescriptor is { Length: >= 143 })
        {
            Array.Copy(sourceDescriptor, descriptor, 143);
            if (sourceDescriptor.Length >= 144) descriptorTail = sourceDescriptor[143];
        }
        else "RefractorForge"u8.CopyTo(descriptor);
        output.Write(descriptor, 0, 143);
        output.WriteByte(descriptorTail);

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
            // The 12-byte trailer is opaque and NOT a constant: retail archives carry both 0x001321E0 (Bocage,
            // BFV Crossroads) and plain zeros (Wake_003), and both ship and load. Preserve the source bytes when
            // repacking; zeros for a new archive, which retail itself does.
            var trailer = sourceTrailer?.Invoke(i);
            if (trailer is { Length: 12 }) output.Write(trailer, 0, 12);
            else { WriteU32(output, 0); WriteU32(output, 0); WriteU32(output, 0); }
        }

        // The four bytes that close the table: the source's own when repacking (see ReadFrom), u32 0 for a new
        // archive - every clean retail archive in both games carries it, and the DICE server-strip tool reads it as
        // the end-of-table marker.
        if (tocTail is { Length: 4 }) output.Write(tocTail, 0, 4);

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
        => WriteFile(path, entries, compress, xPackId, descriptor: null);

    /// <summary>Stream a new archive to a file, optionally with the 144 descriptor bytes of another archive
    /// (<see cref="Descriptor"/>) in place of the RefractorForge stamp - e.g. a retail <c>ai.rfa</c>'s, the header a
    /// community packer copies onto thousands of shipped archives.</summary>
    public static void WriteFile(
        string path,
        IReadOnlyList<(string Name, byte[] Data)> entries,
        bool compress,
        XPackId xPackId,
        byte[]? descriptor)
    {
        if (descriptor is not null && descriptor.Length < 143)
            throw new ArgumentException("A descriptor is 143 bytes plus an optional trailing byte.", nameof(descriptor));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        StreamArchive(fs, entries.Count,
            i => entries[i].Name,
            i => { var d = entries[i].Data; return (BuildRegion(d, compress), d.Length); },
            compress, xPackId,
            sourceDescriptor: descriptor,
            tocTail: new byte[4]);
        fs.Flush(flushToDisk: true);                      // see DurableFile
    }

    /// <summary>Stream a repack straight to a file (low memory, no array-size ceiling).
    /// Writes to a sibling temp file first so the original is never locked while being read,
    /// then atomically replaces <paramref name="path"/>. Names in <paramref name="replacements"/> that the
    /// archive already has REPLACE those entries in place, under the name as stored; names it does not have are
    /// appended. Names are matched by <see cref="NameComparer"/> - <c>objects/x</c> replaces a stored
    /// <c>objects\x</c> rather than growing a second copy beside it - and two replacements that name one entry are
    /// refused.</summary>
    /// <param name="drop">Optional: return <c>true</c> for an entry name that should NOT be carried over. Used to
    /// retire entries an archive can never load — a name like <c>ObjectLightMaps/../standardMesh/foo.tga</c>, which
    /// normalises out of its own folder, is dead weight the engine cannot read. Kept entries are still copied as raw
    /// regions, so dropping costs nothing and re-compresses nothing.</param>
    public static void RepackToFile(
        string path,
        RefractorFlatArchive original,
        IReadOnlyDictionary<string, byte[]> replacements,
        Func<string, bool>? drop = null)
    {
        var ci = new Dictionary<string, byte[]>(replacements.Count, NameComparer);
        foreach (var (name, bytes) in replacements)
            if (!ci.TryAdd(name, bytes))
                throw new ArgumentException($"Two replacements name the entry '{name}' (names compare in any case, with either slash).", nameof(replacements));
        IReadOnlyList<RefractorFlatArchiveEntry> ents = drop is null
            ? original.Entries
            : original.Entries.Where(e => !drop(e.Name)).ToList();

        // A name the archive does not carry yet is APPENDED, not dropped. A repack IS a save, and a save has to
        // be able to add a file: a level-local object - a decal's mesh, shader, texture and its four .con files -
        // is nothing but new names. Dropping them silently produced an archive that validated, reported success,
        // and had a StaticObjects.con placing an object whose every file was missing.
        var known = new HashSet<string>(NameComparer);
        foreach (var e in ents) known.Add(e.Name);
        var added = new List<string>();
        foreach (var k in ci.Keys) if (!known.Contains(k)) added.Add(k);
        added.Sort(StringComparer.OrdinalIgnoreCase);        // deterministic order; entries are addressed by name

        string tmp = path + ".rfatmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                StreamArchive(
                    fs,
                    ents.Count + added.Count,
                    i => i < ents.Count ? ents[i].Name : added[i - ents.Count],
                    i =>
                    {
                        if (i >= ents.Count)
                        {
                            var fresh = ci[added[i - ents.Count]];
                            return (BuildRegion(fresh, original.IsCompressed), fresh.Length);
                        }
                        if (ci.TryGetValue(ents[i].Name, out var rep))
                            return (BuildRegion(rep, original.IsCompressed), rep.Length);
                        var region = original.RawRegion(ents[i]);
                        int unc = ents[i].UncompressedSize;
                        // Heal what older writers left behind: an entry stored raw (or with a verbatim block) inside
                        // a compressed archive is read by the engine as a broken LZO stream. Re-wrap it once; every valid
                        // entry is still copied untouched.
                        if (original.IsCompressed && !IsValidCompressedRegion(region, unc))
                            return (BuildRegion(DecodeRegion(region, unc, ents[i].Name, archiveCompressed: true), true), unc);
                        return (region, unc);
                    },
                    original.IsCompressed,
                    original.XPackId,
                    original._descriptor,
                    i => i < ents.Count && original._entryTrailers is { } t && t.TryGetValue(ents[i].Name, out var tr) ? tr : null,
                    original._tocTail
                );
            // On the DISK before it takes the level's name. Without this a crash shortly after a save left the
            // rename in place and the data - the table of contents last of all - never written (DurableFile).
            DurableFile.FlushToDisk(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>The same repack, with the entries to leave out named rather than tested: each name in
    /// <paramref name="remove"/> drops the entry stored under it, matched by <see cref="NameComparer"/> like the
    /// replacements are - so an editor that shows <c>objects/x</c> can delete a stored <c>objects\x</c> by the
    /// name it shows. A name that is also a replacement is removed and added back under that name.</summary>
    public static void RepackToFile(
        string path,
        RefractorFlatArchive original,
        IReadOnlyDictionary<string, byte[]> replacements,
        IEnumerable<string> remove)
    {
        var gone = new HashSet<string>(remove, NameComparer);
        Func<string, bool>? drop = gone.Count == 0 ? null : gone.Contains;
        RepackToFile(path, original, replacements, drop: drop);
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

    /// <summary>Decompress and return all entries that are <b>not</b> client-only. For a dedicated-server archive
    /// use <see cref="ServerSide.Strip"/>, which keeps the source's container instead of writing a new one.</summary>
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
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
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
    public static string? Validate(string path, long maxEntryBytes = 128L * 1024 * 1024, string? expectedPrefix = null)
    {
        var report = Inspect(path, expectedPrefix, maxEntryBytes);
        return report.Errors.Count == 0 ? null : report.Errors[0];
    }

    /// <summary>Read an archive's container facts and check every entry the way the engine will read it: the header
    /// flag, descriptor, XPack ID and table tail; per entry, whether its region has the form the flag demands and
    /// whether every LZO block decodes identically in BOTH codecs (the clean-room <see cref="Lzo1x"/>, validated
    /// against liblzo2, and MiniLZO). With <paramref name="expectedPrefix"/> every entry name must also start with
    /// that mount path - one that does not makes the engine reject the whole archive ("Error loading file list").
    /// Errors are things the game will choke on; warnings are departures from every retail archive that nobody has
    /// seen break a load.</summary>
    public static ArchiveReport Inspect(string path, string? expectedPrefix = null, long maxEntryBytes = 128L * 1024 * 1024)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        RefractorFlatArchive a;
        try { a = new RefractorFlatArchive(path); }
        catch (Exception ex)
        {
            errors.Add($"archive unreadable: {ex.GetType().Name}: {ex.Message}");
            return new ArchiveReport(path, false, false, XPackId.Default, null, false, 0, 0, 0, 0, 0, 0, errors, warnings);
        }

        int raw = 0, verbatim = 0, wrappedInUncompressed = 0, prefixViolations = 0, empty = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            foreach (var e in a.Entries)
            {
                if (expectedPrefix is not null && !e.Name.Replace('\\', '/').StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (prefixViolations++ == 0)
                        errors.Add($"{e.Name}: outside the archive's mount path '{expectedPrefix}' - the engine rejects the whole archive");
                }
                if (e.BlockSize < 0 || e.UncompressedSize < 0 || e.Offset + (long)e.BlockSize > fs.Length)
                {
                    errors.Add($"{e.Name}: region out of bounds (offset {e.Offset}, blockSize {e.BlockSize}, file {fs.Length})");
                    continue;
                }
                if (e.UncompressedSize == 0) { empty++; continue; }
                if (e.UncompressedSize > maxEntryBytes) continue;   // skip pathological sizes, keep saves fast

                fs.Seek(e.Offset, SeekOrigin.Begin);
                var region = new byte[e.BlockSize];
                fs.ReadExactly(region);

                if (!a.IsCompressed)
                {
                    if (region.Length != e.UncompressedSize && wrappedInUncompressed++ == 0)
                        errors.Add($"{e.Name}: block-wrapped inside an uncompressed archive - the engine reads it as raw bytes");
                    continue;
                }

                if (!IsBlockTable(region, e.UncompressedSize))
                {
                    if (raw++ == 0)
                        errors.Add($"{e.Name}: stored raw inside a compressed archive - the engine LZO-decodes it into garbage");
                    continue;
                }
                string? problem = CheckBlocks(region, e.UncompressedSize, ref verbatim);
                if (problem is not null) errors.Add($"{e.Name}: {problem}");
            }
        }
        catch (Exception ex) { errors.Add($"archive unreadable: {ex.GetType().Name}: {ex.Message}"); }

        if (raw > 1) errors.Add($"{raw} entries in all are stored raw inside the compressed archive");
        if (verbatim > 0) errors.Add($"{verbatim} block(s) stored verbatim inside the compressed archive - the engine LZO-decodes them into garbage");
        if (prefixViolations > 1) errors.Add($"{prefixViolations} entries in all sit outside '{expectedPrefix}'");
        if (empty > 0 && a.IsCompressed) warnings.Add($"{empty} zero-byte entr{(empty == 1 ? "y" : "ies")} - no retail compressed archive has one");
        if (!a.IsV11Format && !a.HasTableTail) warnings.Add("no 4-byte table tail - every clean retail archive has one");
        if (!a.IsV11Format && a.XPackId != XPackId.Default) warnings.Add($"XPack ID is {a.XPackId}, not Default");

        return new ArchiveReport(path, a.IsCompressed, a.IsV11Format, a.XPackId, a._descriptor, a.HasTableTail,
            a.Entries.Count, raw, verbatim, wrappedInUncompressed, prefixViolations, empty, errors, warnings);
    }

    /// <summary>Decode every block of a block table in both codecs and compare. A block whose stored size equals
    /// its output size must still be a valid stream (verbatim blocks are counted, not decoded).</summary>
    private static string? CheckBlocks(byte[] region, int uncompressedSize, ref int verbatim)
    {
        int nb = (int)ReadU32(region, 0), dataStart = 4 + nb * 12;
        for (int i = 0; i < nb; i++)
        {
            int b = 4 + i * 12;
            int comp = (int)ReadU32(region, b), unc = (int)ReadU32(region, b + 4), cum = (int)ReadU32(region, b + 8);
            var src = region.AsSpan(dataStart + cum, comp);
            byte[] ours;
            try { ours = Lzo1x.Decompress(src, unc); }        // the engine-validated decoder is the referee
            catch (Exception ex)
            {
                if (comp == unc) { verbatim++; continue; }
                return $"block {i} failed engine-validated LZO decode ({ex.Message})";
            }
            byte[] theirs;
            try { theirs = MiniLZO.MiniLZO.Decompress(src.ToArray(), unc); }
            catch (Exception ex) { return $"block {i} decodes in Lzo1x but not in MiniLZO ({ex.Message})"; }
            if (!ours.AsSpan().SequenceEqual(theirs)) return $"block {i} decodes differently in Lzo1x and MiniLZO";
        }
        return null;
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
