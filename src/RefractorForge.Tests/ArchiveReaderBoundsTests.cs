using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The archive reader against files that are not archives, or whose table lies: every number the table gives - its
/// offset, the entry count, each name length, each entry's region - is checked against the file's length before it
/// sizes an allocation or a loop. A bad file is refused with an <see cref="InvalidDataException"/> that names it,
/// quickly and without allocating from the bad numbers. (Before: a 224-byte file whose table claimed 0x7fffffff
/// entries asked for a 2 GiB list and the whole content browser died out of memory.)
/// </summary>
public class ArchiveReaderBoundsTests
{
    private static byte[] SmallArchive(bool compress = true)
    {
        var entries = new List<(string Name, byte[] Data)>
        {
            ("objects/a/Objects.con", Encoding.Latin1.GetBytes("ObjectTemplate.create SimpleObject a\r\n")),
            ("objects/a/Geometries.con", Encoding.Latin1.GetBytes("GeometryTemplate.create StandardMesh a\r\n")),
            ("objects/a/empty.con", Array.Empty<byte>()),
        };
        var path = Path.GetTempFileName();
        try
        {
            RefractorFlatArchive.WriteFile(path, entries, compress, XPackId.Default);
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }

    private static uint U32(byte[] b, long at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at));
    private static void Put(byte[] b, long at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)at), v);

    /// <summary>Where the first table record starts (its name length), in a standard-header archive.</summary>
    private static long FirstRecord(byte[] b) => U32(b, 0) + 4;

    /// <summary>Open <paramref name="bytes"/> as an archive from a temp file named <paramref name="name"/>.</summary>
    private static RefractorFlatArchive Open(byte[] bytes, string name, out string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_bounds_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return new RefractorFlatArchive(path);
    }

    /// <summary>Opening <paramref name="bytes"/> fails with InvalidDataException naming the file, fast, allocating
    /// nothing like what the bad numbers asked for.</summary>
    private static InvalidDataException Refused(byte[] bytes, string name)
    {
        string path = "";
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidDataException>(() => Open(bytes, name, out path));
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        try
        {
            Assert.Contains(name, ex.Message);
            Assert.Contains(path, ex.Message);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"refusing took {sw.ElapsedMilliseconds} ms");
            Assert.True(allocated < 4 << 20, $"refusing allocated {allocated} bytes");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
        return ex;
    }

    [Fact]
    public void The_small_archive_the_cases_start_from_reads()
    {
        var a = Open(SmallArchive(), "good.rfa", out var path);
        try
        {
            Assert.Equal(3, a.Entries.Count);
            Assert.Equal("objects/a/Objects.con", a.Entries[0].Name);
            Assert.StartsWith("ObjectTemplate.create", Encoding.Latin1.GetString(a.Read(a.Entries[0])));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void A_table_claiming_more_entries_than_the_file_holds_is_refused()
    {
        var b = SmallArchive();
        Put(b, U32(b, 0), 0x7fffffff);
        var ex = Refused(b, "bigcount.rfa");
        Assert.Contains("2147483647 entries", ex.Message);

        // One more than fits is as wrong as two billion.
        b = SmallArchive();
        long after = b.Length - U32(b, 0) - 4;
        Put(b, U32(b, 0), (uint)(after / 28 + 1));
        Refused(b, "onemore.rfa");
    }

    [Theory]
    [InlineData(0x7ffffff0u)]
    [InlineData(0xffffffffu)]
    [InlineData(100_000u)]
    public void A_name_longer_than_the_rest_of_the_file_is_refused(uint length)
    {
        var b = SmallArchive();
        Put(b, FirstRecord(b), length);
        var ex = Refused(b, "hugename.rfa");
        Assert.Contains($"{length}-byte name", ex.Message);
    }

    [Fact]
    public void An_entry_running_past_the_end_of_the_file_is_refused()
    {
        var b = SmallArchive();
        long rec = FirstRecord(b);
        long sizes = rec + 4 + U32(b, rec);                     // after the name: block size, unpacked size, offset
        Put(b, sizes + 8, (uint)b.Length);                        // offset at the very end, a region after it
        Assert.Contains("runs past the end", Refused(b, "offset.rfa").Message);

        b = SmallArchive();
        Put(b, sizes, (uint)b.Length);                            // a region longer than the file
        Assert.Contains("runs past the end", Refused(b, "size.rfa").Message);

        b = SmallArchive();
        Put(b, sizes + 8, 0xfffffff0);                            // u32 offset + size must not wrap into range
        Refused(b, "wrap.rfa");

        b = SmallArchive();
        Put(b, sizes + 4, 0x80000000);                            // an unpacked size past 2 GiB
        Assert.Contains("over 2 GiB", Refused(b, "unc.rfa").Message);
    }

    [Fact]
    public void A_table_offset_past_the_end_of_the_file_is_refused()
    {
        var b = SmallArchive();
        Put(b, 0, (uint)b.Length - 3);
        Assert.Contains("table offset", Refused(b, "toc.rfa").Message);
        Put(b, 0, 0xffffffff);
        Refused(b, "toc2.rfa");
    }

    [Fact]
    public void Files_that_are_not_archives_are_refused()
    {
        Assert.Contains("too short", Refused(Array.Empty<byte>(), "empty.rfa").Message);
        Assert.Contains("too short", Refused(Encoding.ASCII.GetBytes("not an archive"), "short.rfa").Message);
        Refused(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("rem this is a text file, not an archive\r\n", 40))), "notarc.rfa");
        // A PNG renamed: its signature reads as a table offset of 0x474E5089.
        var png = new byte[400];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        Refused(png, "picture.rfa");
        // A v1.1 signature with nothing after it.
        Refused(Encoding.ASCII.GetBytes("Refractor2 FlatArchive 1.1  "), "v11.rfa");
    }

    [Fact]
    public void Every_truncation_of_an_archive_is_refused_or_read_never_crashes()
    {
        // Cut anywhere: either the table is whole (only the optional four tail bytes went) or the reader says the file
        // is not a readable archive. No EndOfStream, no OutOfMemory, no overflow.
        foreach (bool compress in new[] { true, false })
        {
            var full = SmallArchive(compress);
            int opened = 0;
            for (int cut = 0; cut < full.Length; cut++)
            {
                var b = full.AsSpan(0, cut).ToArray();
                string? path = null;
                try
                {
                    var a = Open(b, $"cut{cut}.rfa", out path);
                    opened++;
                    Assert.Equal(3, a.Entries.Count);
                }
                catch (InvalidDataException ex) { Assert.Contains($"cut{cut}.rfa", ex.Message); }
                finally { if (path is not null) Directory.Delete(Path.GetDirectoryName(path)!, true); }
            }
            Assert.Equal(4, opened);         // only the cuts inside the four optional tail bytes still open
        }
    }

    [Fact]
    public void Garbage_in_the_table_is_refused_or_read_never_crashes()
    {
        // The unpacked size is the one number the file's length cannot bound, so reading is part of the check: an
        // entry either reads or is refused, and neither costs more than the file is worth.
        var full = SmallArchive();
        long toc = U32(full, 0);
        var rng = new Random(1942);
        int reads = 0, refusedReads = 0;
        for (int round = 0; round < 400; round++)
        {
            var b = (byte[])full.Clone();
            int hits = 1 + rng.Next(4);
            for (int h = 0; h < hits; h++) b[toc + rng.Next((int)(b.Length - toc))] = (byte)rng.Next(256);
            string? path = null;
            try
            {
                var a = Open(b, $"garbage{round}.rfa", out path);
                foreach (var e in a.Entries)
                {
                    Assert.InRange(e.BlockSize, 0, b.Length);
                    Assert.True(e.Offset + (long)e.BlockSize <= b.Length);
                    Assert.True(e.UncompressedSize >= 0);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    try { a.Read(e); reads++; }
                    catch (InvalidDataException) { refusedReads++; }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.True(allocated < 1 << 20, $"round {round}: reading '{e.Name}' (unpacked {e.UncompressedSize}) allocated {allocated} bytes");
                }
            }
            catch (InvalidDataException) { }
            finally { if (path is not null) Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
        Assert.True(reads > 0 && refusedReads > 0, $"{reads} reads, {refusedReads} refused");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unpacked_size_its_blocks_cannot_hold_is_refused_before_it_is_allocated(bool compress)
    {
        // The table's unpacked size is bounded only by 2 GiB, so it is checked against the entry's own block table:
        // a 1.5 GB claim for a 37-byte entry used to allocate 1.5 GB on the first read, and Inspect called the archive
        // valid because it skips decoding anything over its size limit.
        var b = SmallArchive(compress);
        long rec = FirstRecord(b);
        long sizes = rec + 4 + U32(b, rec);
        Put(b, sizes + 4, 1_500_000_000);
        var a = Open(b, "liar.rfa", out var path);
        try
        {
            var e = a.Entries[0];
            Assert.Equal(1_500_000_000, e.UncompressedSize);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var ex = Assert.Throws<InvalidDataException>(() => a.Read(e));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1 << 20, $"the refused read allocated {allocated} bytes");
            Assert.Contains("objects/a/Objects.con", ex.Message);
            Assert.Contains("1500000000", ex.Message);

            var report = RefractorFlatArchive.Inspect(path);
            Assert.False(report.IsValid);
            Assert.Contains(report.Errors, x => x.Contains("objects/a/Objects.con") && x.Contains("1500000000"));
            // The other entries still read.
            Assert.StartsWith("GeometryTemplate", Encoding.Latin1.GetString(a.Read(a.Entries[1])));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [InstallFact(Installs.Bf1942Clean, Installs.BfvOriginal)]
    public void Every_archive_of_the_clean_installs_still_opens()
    {
        // The checks refuse nothing real: every retail archive's table passes them.
        int n = 0;
        foreach (var root in new[] { Installs.Bf1942Clean, Installs.BfvOriginal }.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories))
            {
                var a = new RefractorFlatArchive(file);
                Assert.True(a.Entries.Count >= 0);
                n++;
            }
        Assert.True(n > 100, $"only {n} archives found");
    }
}
