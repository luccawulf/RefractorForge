using System.Buffers.Binary;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// What a browser needs from an archive besides reading a whole entry: finding one by name the way the engine names
/// it (any case, either slash), reading just the head of a large one (a DDS header, a WAV format chunk, a hex view)
/// without decoding the rest, saving over a stored <c>objects\x</c> with <c>objects/x</c> without growing a
/// duplicate, and stripping a server copy that keeps the source's container.
/// </summary>
public class ArchiveLookupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rfa_lookup_" + Guid.NewGuid().ToString("N"));
    public ArchiveLookupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string P(string name) => Path.Combine(_dir, name);

    private static byte[] Random(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static byte[] Text(int n) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = (byte)"ObjectTemplate.setPosition 0/1.5/-2\r\n"[i % 37]; return b; }

    /// <summary>Text with a counter in it: compresses, and every 32 KiB block differs from the last.</summary>
    private static byte[] Numbered(int n)
    {
        var b = new byte[n];
        for (int i = 0, line = 0; i < n; line++)
            foreach (char c in $"rem line {line}\r\n") { if (i >= n) break; b[i++] = (byte)c; }
        return b;
    }

    private string Written(string name, bool compress, params (string Name, byte[] Data)[] entries)
    {
        var path = P(name);
        RefractorFlatArchive.WriteFile(path, entries, compress, XPackId.Default);
        return path;
    }

    // ---- TryGetEntry ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_entry_is_found_in_any_case_with_either_slash()
    {
        var a = new RefractorFlatArchive(Written("a.rfa", true,
            (@"objects\Vehicles\Land\M113\Objects.con", Text(40)), ("texture/ve_M113.dds", Random(64, 1))));

        Assert.True(a.TryGetEntry("objects/vehicles/land/m113/objects.con", out var con));
        Assert.Equal(@"objects\Vehicles\Land\M113\Objects.con", con.Name);             // the stored name, as stored
        Assert.True(a.TryGetEntry(@"TEXTURE\VE_m113.DDS", out var dds));
        Assert.Same(a.Entries[1], dds);
        Assert.False(a.TryGetEntry("texture/ve_M113.dd", out _));
        Assert.False(a.TryGetEntry("", out _));

        Assert.True(RefractorFlatArchive.NameComparer.Equals(@"a\B/c", "A/b\\C"));
        Assert.Equal(RefractorFlatArchive.NameComparer.GetHashCode(@"a\B/c"), RefractorFlatArchive.NameComparer.GetHashCode("A/b\\C"));
        Assert.False(RefractorFlatArchive.NameComparer.Equals("a/b", "a/b/"));
    }

    [Fact]
    public void A_folder_presented_as_an_archive_finds_its_files_too()
    {
        var level = Directory.CreateDirectory(P("MyLevel")).FullName;
        Directory.CreateDirectory(Path.Combine(level, "Init"));
        File.WriteAllBytes(Path.Combine(level, "Init", "Terrain.con"), Text(100));
        var a = RefractorFlatArchive.FromFolder(level);
        Assert.True(a.TryGetEntry(@"LEVELS\mylevel\init\terrain.con", out var e));
        Assert.Equal(Text(100), a.Read(e));
        Assert.Equal(Text(100)[..7], a.ReadHead(e, 7));
    }

    // ---- ReadHead ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_head_of_an_entry_is_the_start_of_what_Read_returns(bool compress)
    {
        var big = Numbered(200_000);                                   // seven blocks when compressed
        var a = new RefractorFlatArchive(Written("h.rfa", compress, ("objects/big.con", big), ("objects/empty.con", Array.Empty<byte>()),
                                                 ("objects/small.con", Text(10))));
        var e = a.Entries[0];
        foreach (int n in new[] { 1, 128, 32767, 32768, 32769, 100_000, 199_999 })
            Assert.Equal(big.AsSpan(0, n).ToArray(), a.ReadHead(e, n));
        Assert.Equal(big, a.ReadHead(e, 200_000));
        Assert.Equal(big, a.ReadHead(e, int.MaxValue));
        Assert.Empty(a.ReadHead(e, 0));
        Assert.Empty(a.ReadHead(e, -5));
        Assert.Empty(a.ReadHead(a.Entries[1], 16));
        Assert.Equal(Text(10), a.ReadHead(a.Entries[2], 16));
    }

    [Fact]
    public void Only_the_blocks_the_head_lies_in_are_decoded()
    {
        var big = Numbered(200_000);
        var path = Written("damaged.rfa", true, ("objects/big.con", big));
        var e = new RefractorFlatArchive(path).Entries[0];

        // Break the last block's LZO stream on disk: a whole read fails, the head never touches it.
        var file = File.ReadAllBytes(path);
        int nb = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)e.Offset));
        int last = (int)e.Offset + 4 + (nb - 1) * 12;
        int comp = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(last));
        int cum = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(last + 8));
        int at = (int)e.Offset + 4 + nb * 12 + cum;
        for (int i = 0; i < comp; i++) file[at + i] = 0xFF;
        File.WriteAllBytes(path, file);

        var a = new RefractorFlatArchive(path);
        Assert.ThrowsAny<Exception>(() => a.Read(a.Entries[0]));
        Assert.Equal(big.AsSpan(0, 40_000).ToArray(), a.ReadHead(a.Entries[0], 40_000));
    }

    [Fact]
    public void Legacy_and_same_size_regions_give_the_same_head_as_Read()
    {
        var data = Text(3000);
        var raw = Random(5000, 3);
        var path = HandBuilt("legacy.rfa", compressed: true,
            ("objects/raw.con", data, data.Length),                                  // stored raw in a compressed archive
            ("objects/verbatim.dds", Region((raw, raw.Length)), raw.Length));       // a verbatim block
        var a = new RefractorFlatArchive(path);
        foreach (var e in a.Entries)
            foreach (int n in new[] { 1, 100, e.UncompressedSize - 1 })
                Assert.Equal(a.Read(e).AsSpan(0, n).ToArray(), a.ReadHead(e, n));

        // A block-wrapped entry inside an uncompressed archive (older writers): Read decodes it, and so does the head.
        var lzo = MiniLZO.MiniLZO.Compress(data);
        var wrapped = HandBuilt("wrapped.rfa", compressed: false, ("objects/w.con", Region((lzo, data.Length)), data.Length));
        var w = new RefractorFlatArchive(wrapped);
        Assert.Equal(data.AsSpan(0, 50).ToArray(), w.ReadHead(w.Entries[0], 50));
    }

    // ---- RepackToFile: names with the other slash ---------------------------------------------------------------

    [Fact]
    public void A_replacement_with_the_other_slash_replaces_the_stored_entry()
    {
        var src = Written("src.rfa", true, (@"objects\a\Objects.con", Text(100)), (@"objects\a\Geometries.con", Text(50)));
        var a = new RefractorFlatArchive(src);
        var outPath = P("out.rfa");
        RefractorFlatArchive.RepackToFile(outPath, a, new Dictionary<string, byte[]>
        {
            ["Objects/A/objects.con"] = Text(7),                    // stored as objects\a\Objects.con
            ["objects/a/new.con"] = Text(9),                        // not there: appended under this name
        });
        var b = new RefractorFlatArchive(outPath);
        Assert.Equal(new[] { @"objects\a\Objects.con", @"objects\a\Geometries.con", "objects/a/new.con" }, b.Entries.Select(e => e.Name));
        Assert.Equal(Text(7), b.Read(b.Entries[0]));
        Assert.Equal(Text(50), b.Read(b.Entries[1]));
        Assert.Equal(Text(9), b.Read(b.Entries[2]));
        Assert.Null(RefractorFlatArchive.Validate(outPath));
    }

    [Fact]
    public void Entries_named_for_removal_are_matched_with_either_slash()
    {
        var src = Written("src2.rfa", true, (@"objects\a\Objects.con", Text(100)), (@"objects\a\old.con", Text(50)), ("objects/b.con", Text(20)));
        var a = new RefractorFlatArchive(src);
        var outPath = P("out2.rfa");
        RefractorFlatArchive.RepackToFile(outPath, a, new Dictionary<string, byte[]> { ["objects/a/objects.con"] = Text(3) },
                                          remove: new[] { "OBJECTS/A/OLD.CON", @"objects\b.con" });
        var b = new RefractorFlatArchive(outPath);
        Assert.Equal(new[] { @"objects\a\Objects.con" }, b.Entries.Select(e => e.Name));
        Assert.Equal(Text(3), b.Read(b.Entries[0]));
    }

    [Fact]
    public void Two_replacements_for_one_entry_are_refused()
    {
        var a = new RefractorFlatArchive(Written("src3.rfa", false, (@"objects\x.con", Text(5))));
        var ex = Assert.Throws<ArgumentException>(() => RefractorFlatArchive.RepackToFile(P("out3.rfa"), a,
            new Dictionary<string, byte[]> { ["objects/x.con"] = Text(1), [@"OBJECTS\X.CON"] = Text(2) }));
        Assert.Contains("objects", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(P("out3.rfa")));
    }

    // ---- ServerSide.Strip keeps the container --------------------------------------------------------------------

    [Fact]
    public void A_server_copy_keeps_the_sources_container_and_copies_kept_entries_untouched()
    {
        // A descriptor that is not ours, as every retail and community archive has.
        var descriptor = new byte[144];
        "Refractor2 archive by DICE"u8.CopyTo(descriptor);
        descriptor[143] = 7;
        var src = P("Level.rfa");
        RefractorFlatArchive.WriteFile(src, new (string, byte[])[]
        {
            ("bfvietnam/levels/Level/Init/Terrain.con", Text(4000)),
            ("bfvietnam/levels/Level/Textures/InGameMap.dds", Random(70_000, 1)),
            ("bfvietnam/levels/Level/sound/ambient.wav", Random(9000, 2)),
            ("bfvietnam/levels/Level/StaticObjects.con", Numbered(90_000)),
            ("bfvietnam/levels/Level/Textures/LightmapShadowBits.lsb", Random(100, 3)),
        }, compress: true, XPackId.Default, descriptor);

        var outPath = P("server.rfa");
        var o = ServerSide.Strip(src, outPath);
        Assert.Equal((5, 2, true), (o.EntriesBefore, o.EntriesAfter, o.Written));

        var a = new RefractorFlatArchive(src);
        var s = new RefractorFlatArchive(outPath);
        Assert.Equal(a.Descriptor, s.Descriptor);
        Assert.False(RefractorFlatArchive.WasWrittenByRefractorForge(outPath));
        Assert.Equal(a.XPackId, s.XPackId);
        Assert.Equal(a.IsCompressed, s.IsCompressed);
        Assert.True(s.HasTableTail);
        Assert.Equal(new[] { "bfvietnam/levels/Level/Init/Terrain.con", "bfvietnam/levels/Level/StaticObjects.con" }, s.Entries.Select(e => e.Name));
        foreach (var e in s.Entries)
        {
            Assert.True(a.TryGetEntry(e.Name, out var orig));
            Assert.Equal(RegionOf(src, orig), RegionOf(outPath, e));          // copied as stored, not re-encoded
        }
        Assert.Null(RefractorFlatArchive.Validate(outPath));
    }

    [InstallFact(Installs.BfvOriginalArchives)]
    public void A_retail_level_strips_with_its_container_and_every_kept_region_intact()
    {
        var src = Path.Combine(Installs.BfvOriginalArchives, "bfvietnam", "levels", "Operation_Irving.rfa");
        var outPath = P("Operation_Irving.rfa");
        var o = ServerSide.Strip(src, outPath);

        var a = new RefractorFlatArchive(src);
        var s = new RefractorFlatArchive(outPath);
        Assert.Equal(a.Descriptor, s.Descriptor);
        Assert.Equal(a.HasTableTail, s.HasTableTail);
        Assert.Equal(a.Entries.Count(e => !RefractorFlatArchive.IsClientOnlyEntry(e.Name)), s.Entries.Count);
        Assert.Equal(s.Entries.Count, o.EntriesAfter);
        Assert.True(o.BytesAfter < o.BytesBefore);
        Assert.DoesNotContain(s.Entries, e => RefractorFlatArchive.IsClientOnlyEntry(e.Name));

        // The table trailers and regions, byte for byte: the server copy is the source minus the client-only files.
        var srcToc = TocTrailers(src);
        var outToc = TocTrailers(outPath);
        foreach (var e in s.Entries)
        {
            Assert.True(a.TryGetEntry(e.Name, out var orig));
            Assert.Equal(RegionOf(src, orig), RegionOf(outPath, e));
            Assert.Equal(srcToc[e.Name], outToc[e.Name]);
        }
        Assert.Null(RefractorFlatArchive.Validate(outPath));
    }

    [InstallFact(Installs.BfvOriginalArchives)]
    public void The_head_of_every_retail_texture_is_the_start_of_the_texture()
    {
        foreach (var name in new[] { "texture.rfa", "texture_001.rfa", "objects.rfa" })
        {
            var a = new RefractorFlatArchive(Path.Combine(Installs.BfvOriginalArchives, name));
            foreach (var e in a.Entries.Where((_, i) => i % 7 == 0))
            {
                var whole = a.Read(e);
                int n = Math.Min(whole.Length, 148);
                Assert.Equal(whole.AsSpan(0, n).ToArray(), a.ReadHead(e, 148));
                Assert.True(a.TryGetEntry(e.Name.Replace('/', '\\').ToUpperInvariant(), out var found) && found.Offset == e.Offset);
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static byte[] RegionOf(string path, RefractorFlatArchiveEntry e)
    {
        using var fs = File.OpenRead(path);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        var r = new byte[e.BlockSize];
        fs.ReadExactly(r);
        return r;
    }

    /// <summary>Each entry's 12 table bytes after its offset, read straight off the file.</summary>
    private static Dictionary<string, byte[]> TocTrailers(string path)
    {
        var b = File.ReadAllBytes(path);
        int p = (int)BinaryPrimitives.ReadUInt32LittleEndian(b);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
            var name = System.Text.Encoding.Latin1.GetString(b, p, len); p += len;
            d[name] = b.AsSpan(p + 12, 12).ToArray(); p += 24;
        }
        return d;
    }

    /// <summary>A standard header + the given regions + TOC + tail, exactly as the engine reads it.</summary>
    private string HandBuilt(string name, bool compressed, params (string Name, byte[] Region, int Unc)[] entries)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        U32(0); U32(compressed ? 1u : 0u);
        ms.Write(new byte[144]);
        U32((uint)XPackId.Default);
        var offsets = new List<long>();
        foreach (var e in entries) { offsets.Add(ms.Position); ms.Write(e.Region); }
        long toc = ms.Position;
        U32((uint)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            var n = System.Text.Encoding.Latin1.GetBytes(entries[i].Name);
            U32((uint)n.Length); ms.Write(n);
            U32((uint)entries[i].Region.Length); U32((uint)entries[i].Unc); U32((uint)offsets[i]);
            U32(0); U32(0); U32(0);
        }
        U32(0);
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)toc);
        var path = P(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Region(params (byte[] Payload, int Unc)[] blocks)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        U32((uint)blocks.Length);
        int cum = 0;
        foreach (var (p, u) in blocks) { U32((uint)p.Length); U32((uint)u); U32((uint)cum); cum += p.Length; }
        foreach (var (p, _) in blocks) ms.Write(p);
        return ms.ToArray();
    }
}
