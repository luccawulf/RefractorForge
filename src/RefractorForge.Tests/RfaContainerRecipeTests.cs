using System.Buffers.Binary;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>The container rules a from-scratch archive has to follow for BOTH games to read it (see
/// docs of RefractorFlatArchive): in a compressed archive every entry is a block table and every block a valid LZO
/// stream - never raw, never verbatim - the table is closed by a u32 0 tail, and the descriptor can be borrowed from a
/// retail archive. Also the reading side: a compressed entry whose LZO streams happen to add up to exactly its own
/// size is decoded, not handed out raw.</summary>
public class RfaContainerRecipeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rfa_recipe_" + Guid.NewGuid().ToString("N"));
    public RfaContainerRecipeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string P(string name) => Path.Combine(_dir, name);

    private static byte[] Random(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static byte[] Text(int n) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = (byte)"ObjectTemplate.setPosition 0/1.5/-2\r\n"[i % 37]; return b; }

    private static byte[] RegionOf(string path, RefractorFlatArchiveEntry e)
    {
        using var fs = File.OpenRead(path);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        var r = new byte[e.BlockSize];
        fs.ReadExactly(r);
        return r;
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(4)] [InlineData(18)] [InlineData(19)] [InlineData(273)]
    [InlineData(528)] [InlineData(32768)] [InlineData(32769)] [InlineData(100_000)]
    public void EncodeLiteral_is_a_valid_stream_in_both_codecs(int n)
    {
        var data = Random(n, n);
        var block = data.AsSpan(0, Math.Min(n, 32768)).ToArray();
        var lit = Lzo1x.EncodeLiteral(block);
        Assert.True(lit.Length > block.Length);                                   // never mistaken for verbatim
        Assert.Equal(block, Lzo1x.Decompress(lit, block.Length));
        Assert.Equal(block, MiniLZO.MiniLZO.Decompress(lit, block.Length));
    }

    [Fact]
    public void Compressed_archives_never_store_raw_entries_or_verbatim_blocks()
    {
        var entries = new List<(string, byte[])>();
        foreach (int n in new[] { 1, 3, 4, 18, 19, 32768, 32769, 100_000 })
        {
            entries.Add(($"objects/random{n}.bin", Random(n, n)));
            entries.Add(($"objects/text{n}.con", Text(n)));
        }
        var path = P("wrapped.rfa");
        RefractorFlatArchive.WriteFile(path, entries, compress: true, XPackId.Default);

        var a = new RefractorFlatArchive(path);
        Assert.True(a.IsCompressed);
        foreach (var e in a.Entries)
        {
            var data = entries.First(x => x.Item1 == e.Name).Item2;
            var region = RegionOf(path, e);
            Assert.NotEqual(e.UncompressedSize, region.Length);                   // never raw, never raw-sized

            int nb = (int)BinaryPrimitives.ReadUInt32LittleEndian(region);
            Assert.Equal((data.Length + 32767) / 32768, nb);
            int dataStart = 4 + nb * 12;
            for (int i = 0; i < nb; i++)
            {
                int comp = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(4 + i * 12));
                int unc = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(8 + i * 12));
                int cum = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(12 + i * 12));
                Assert.NotEqual(unc, comp);                                           // never a verbatim block
                var src = region.AsSpan(dataStart + cum, comp);
                var expect = data.AsSpan(i * 32768, unc).ToArray();
                Assert.Equal(expect, Lzo1x.Decompress(src, unc));
                Assert.Equal(expect, MiniLZO.MiniLZO.Decompress(src.ToArray(), unc));
            }
            Assert.Equal(data, a.Read(e));
        }

        var report = RefractorFlatArchive.Inspect(path, expectedPrefix: "objects/");
        Assert.True(report.IsValid, string.Join("; ", report.Errors));
        Assert.Empty(report.Warnings);
        Assert.True(report.HasTableTail);
    }

    [Fact]
    public void Uncompressed_archives_store_every_entry_raw()
    {
        var path = P("raw.rfa");
        var noise = Random(70_000, 1);
        RefractorFlatArchive.WriteFile(path, new[] { ("sound/44khz/a.wav", noise) }, compress: false, XPackId.Default);
        var a = new RefractorFlatArchive(path);
        var e = Assert.Single(a.Entries);
        Assert.Equal(e.UncompressedSize, e.BlockSize);
        Assert.Equal(noise, a.Read(e));
        Assert.True(RefractorFlatArchive.Inspect(path, "sound/").IsValid);
    }

    [Fact]
    public void New_archive_has_table_tail_and_given_descriptor()
    {
        var descriptor = new byte[144];
        for (int i = 0; i < 143; i++) descriptor[i] = (byte)(i * 7 + 3);
        var path = P("desc.rfa");
        RefractorFlatArchive.WriteFile(path, new[] { ("texture/a.dds", Text(500)) }, true, XPackId.Default, descriptor);

        var a = new RefractorFlatArchive(path);
        Assert.Equal(XPackId.Default, a.XPackId);                                  // ID + Σdescriptor decodes back
        Assert.Equal(descriptor, a.Descriptor);
        Assert.True(a.HasTableTail);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)));
        Assert.Null(RefractorFlatArchive.Validate(path));

        var stamped = P("stamp.rfa");
        RefractorFlatArchive.WriteFile(stamped, new[] { ("texture/a.dds", Text(500)) }, true, XPackId.Default);
        Assert.True(RefractorFlatArchive.WasWrittenByRefractorForge(stamped));
        Assert.True(new RefractorFlatArchive(stamped).HasTableTail);
    }

    [Fact]
    public void Validate_rejects_prefix_violation()
    {
        var path = P("mixed.rfa");
        RefractorFlatArchive.WriteFile(path, new[] { ("objects/a/Objects.con", Text(40)), ("texture/b.dds", Text(40)) }, true, XPackId.Default);
        Assert.Null(RefractorFlatArchive.Validate(path));
        var why = RefractorFlatArchive.Validate(path, expectedPrefix: "objects/");
        Assert.NotNull(why);
        Assert.Contains("texture/b.dds", why);
    }

    // ---- hand-built containers: the legacy forms and the same-size LZO case ------------------------------

    /// <summary>A standard header + the given regions + TOC + tail, exactly as the engine reads it.</summary>
    private string HandBuilt(string name, bool compressed, params (string Name, byte[] Region, int Unc)[] entries)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        U32(0); U32(compressed ? 1u : 0u);
        ms.Write(new byte[144]);
        U32((uint)XPackId.Default);                                                  // Σ of a zero descriptor is 0
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

    [Fact]
    public void Same_size_lzo_entries_decode_as_lzo()
    {
        // Find data whose one-block LZO region is EXACTLY its own length (4 + 12 + comp == n) - the shape of retail
        // entries such as BFV M91Deploy.baf that the old size test handed out as raw bytes.
        byte[]? data = null, lzo = null;
        var tail = Random(100, 42);
        for (int k = 1; k < 400 && data is null; k++)
        {
            var d = new byte[k + tail.Length];
            Array.Fill(d, (byte)'A', 0, k);
            tail.CopyTo(d, k);
            var c = MiniLZO.MiniLZO.Compress(d);
            if (16 + c.Length == d.Length) { data = d; lzo = c; }
        }
        Assert.NotNull(data);
        var region = Region((lzo!, data!.Length));
        Assert.Equal(data.Length, region.Length);

        var path = HandBuilt("samesize.rfa", compressed: true, ("objects/x.baf", region, data.Length));
        var a = new RefractorFlatArchive(path);
        Assert.Equal(data, a.Read(a.Entries[0]));
        Assert.True(RefractorFlatArchive.Inspect(path).IsValid);

        // In an UNCOMPRESSED archive the same bytes are raw - the flag decides.
        var raw = HandBuilt("samesize_raw.rfa", compressed: false, ("objects/x.baf", region, data.Length));
        var b = new RefractorFlatArchive(raw);
        Assert.Equal(region, b.Read(b.Entries[0]));
    }

    [Fact]
    public void Legacy_raw_in_compressed_still_reads_is_flagged_and_heals_on_repack()
    {
        var data = Text(300);
        var path = HandBuilt("legacy_raw.rfa", compressed: true, ("objects/a/Objects.con", data, data.Length));
        var a = new RefractorFlatArchive(path);
        Assert.Equal(data, a.Read(a.Entries[0]));

        var report = RefractorFlatArchive.Inspect(path);
        Assert.False(report.IsValid);
        Assert.Equal(1, report.RawEntriesInCompressed);

        var healed = P("healed.rfa");
        RefractorFlatArchive.RepackToFile(healed, a, new Dictionary<string, byte[]>());
        var h = new RefractorFlatArchive(healed);
        Assert.Equal(data, h.Read(h.Entries[0]));
        Assert.True(RefractorFlatArchive.Inspect(healed).IsValid, string.Join("; ", RefractorFlatArchive.Inspect(healed).Errors));
    }

    [Fact]
    public void Legacy_verbatim_block_still_reads_is_flagged_and_heals_on_repack()
    {
        var data = Random(5000, 9);                                   // random bytes: not a valid LZO stream
        var region = Region((data, data.Length));
        var path = HandBuilt("legacy_verbatim.rfa", compressed: true, ("objects/a/tex.dds", region, data.Length));
        var a = new RefractorFlatArchive(path);
        Assert.Equal(data, a.Read(a.Entries[0]));

        var report = RefractorFlatArchive.Inspect(path);
        Assert.Equal(1, report.VerbatimBlocks);
        Assert.False(report.IsValid);

        var healed = P("healed2.rfa");
        RefractorFlatArchive.RepackToFile(healed, a, new Dictionary<string, byte[]>());
        var h = new RefractorFlatArchive(healed);
        Assert.Equal(data, h.Read(h.Entries[0]));
        Assert.True(RefractorFlatArchive.Inspect(healed).IsValid);
    }

    [Fact]
    public void Wrapped_entry_in_uncompressed_archive_is_an_error()
    {
        var data = Text(300);
        var region = Region((MiniLZO.MiniLZO.Compress(data), data.Length));
        var path = HandBuilt("wrapped_in_raw.rfa", compressed: false, ("objects/a.con", region, data.Length));
        var report = RefractorFlatArchive.Inspect(path);
        Assert.Equal(1, report.WrappedEntriesInUncompressed);
        Assert.False(report.IsValid);
    }

    // ---- retail: the reader must agree with a per-block reference on every entry ------------------------

    [InstallFact(Installs.BfvOriginalArchives, Installs.Bf1942CleanArchives)]
    public void Every_clean_retail_entry_decodes_and_every_archive_passes_inspection()
    {
        foreach (var dir in new[] { Installs.BfvOriginalArchives, Installs.Bf1942CleanArchives })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in new[] { "objects.rfa", "menu.rfa", "animations.rfa", "ai.rfa", "aiMeshes.rfa" })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) continue;
                var a = new RefractorFlatArchive(path);
                string prefix = Path.GetFileNameWithoutExtension(file) + "/";
                var report = RefractorFlatArchive.Inspect(path, prefix);
                Assert.True(report.IsValid, $"{path}: {string.Join("; ", report.Errors)}");
                Assert.True(report.HasTableTail, path);
                foreach (var e in a.Entries)
                {
                    var bytes = a.Read(e);
                    Assert.Equal(e.UncompressedSize, bytes.Length);
                    if (!a.IsCompressed || e.UncompressedSize == 0) continue;
                    // Reference: decode the block table with the engine-validated codec, block by block.
                    var region = RegionOf(path, e);
                    int nb = (int)BinaryPrimitives.ReadUInt32LittleEndian(region), ds = 4 + nb * 12, w = 0;
                    var refBytes = new byte[e.UncompressedSize];
                    for (int i = 0; i < nb; i++)
                    {
                        int comp = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(4 + i * 12));
                        int unc = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(8 + i * 12));
                        int cum = (int)BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(12 + i * 12));
                        Lzo1x.Decompress(region.AsSpan(ds + cum, comp), refBytes.AsSpan(w, unc), unc);
                        w += unc;
                    }
                    Assert.True(bytes.AsSpan().SequenceEqual(refBytes), $"{path}:{e.Name}");
                }
            }
        }
    }
}
