using System.Text;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

public class RefractorFlatArchiveTests
{
    // ── Build / Load round-trip ───────────────────────────────────────────────

    [Fact]
    public void Build_Load_EntryCountPreserved()
    {
        using var tmp = BuildTempArchive(SyntheticEntries(), compress: true, xPackId: XPackId.Default);
        Assert.Equal(SyntheticEntries().Count, tmp.Archive.Entries.Count);
    }

    [Fact]
    public void Build_Load_ContentIdentical()
    {
        var entries = SyntheticEntries();
        using var tmp = BuildTempArchive(entries, compress: true, xPackId: XPackId.Default);
        for (int i = 0; i < entries.Count; i++)
            Assert.Equal(entries[i].Data, tmp.Archive.Read(tmp.Archive.Entries[i]));
    }

    [Fact]
    public void Build_Load_EmptyEntry_RoundTrips()
    {
        var entries = new List<(string Name, byte[] Data)> { ("empty.txt", Array.Empty<byte>()) };
        using var tmp = BuildTempArchive(entries, compress: true, xPackId: XPackId.Default);
        Assert.Equal(Array.Empty<byte>(), tmp.Archive.Read(tmp.Archive.Entries[0]));
    }

    [Fact]
    public void Build_Load_MultiBlockEntry_RoundTrips()
    {
        // > 32 KiB forces multiple blocks
        var big = RandomBytes(90_000, seed: 42);
        var entries = new List<(string Name, byte[] Data)> { ("big.bin", big) };
        using var tmp = BuildTempArchive(entries, compress: true, xPackId: XPackId.Default);
        Assert.Equal(big, tmp.Archive.Read(tmp.Archive.Entries[0]));
    }

    // ── Uncompressed write ────────────────────────────────────────────────────

    [Fact]
    public void Build_Uncompressed_ContentIdentical()
    {
        var entries = SyntheticEntries();
        using var tmp = BuildTempArchive(entries, compress: false, xPackId: XPackId.Default);
        Assert.False(tmp.Archive.IsCompressed);
        for (int i = 0; i < entries.Count; i++)
            Assert.Equal(entries[i].Data, tmp.Archive.Read(tmp.Archive.Entries[i]));
    }

    // ── XPack ID ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(XPackId.Default)]
    [InlineData(XPackId.RoadToRome)]
    [InlineData(XPackId.SecretWeapons)]
    [InlineData(XPackId.None)]
    public void Build_XPackId_RoundTrips(XPackId id)
    {
        using var tmp = BuildTempArchive(SyntheticEntries(), compress: true, xPackId: id);
        Assert.Equal(id, tmp.Archive.XPackId);
    }

    // ── IsCompressed flag ─────────────────────────────────────────────────────

    [Fact]
    public void Build_Uncompressed_SetsIsCompressedFalse()
    {
        using var tmp = BuildTempArchive(SyntheticEntries(), compress: false, xPackId: XPackId.Default);
        Assert.False(tmp.Archive.IsCompressed);
    }

    // ── v1.1 header ───────────────────────────────────────────────────────────

    [Fact]
    public void Load_V11Header_ReadsEntries()
    {
        // Craft a minimal v1.1 archive by hand:
        //   28-byte signature | u32 tocOffset | u32 compressed | <no data regions> | TOC
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("Refractor2 FlatArchive 1.1  "));  // 28 bytes
        long tocPos = 28 + 4 + 4;   // signature + tocOffset placeholder + compressed flag
        WriteU32(ms, (uint)tocPos);  // tocOffset
        WriteU32(ms, 1);             // compressed = true
        WriteU32(ms, 1);             // entry count = 1
        var name = Encoding.Latin1.GetBytes("test/file.txt");
        WriteU32(ms, (uint)name.Length);
        ms.Write(name);
        WriteU32(ms, 0); WriteU32(ms, 0); WriteU32(ms, 0);   // blockSize=0, unc=0, offset=0
        WriteU32(ms, 0); WriteU32(ms, 0); WriteU32(ms, 0);   // reserved

        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, ms.ToArray());
            var archive = new RefractorFlatArchive(tmp);
            Assert.True(archive.IsV11Format);
            Assert.Single(archive.Entries);
            Assert.Equal("test/file.txt", archive.Entries[0].Name);
        }
        finally { File.Delete(tmp); }
    }

    // ── Entry lookup ──────────────────────────────────────────────────────────

    [Fact]
    public void Entry_lookup_case_insensitive()
    {
        var entries = new List<(string, byte[])> { ("Folder/File.txt", new byte[] { 1 }) };
        using var tmp = BuildTempArchive(entries, compress: true, xPackId: XPackId.Default);
        Assert.NotNull(tmp.Archive.Entries.FirstOrDefault(e => e.Name.Equals("folder/file.txt", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(tmp.Archive.Entries.FirstOrDefault(e => e.Name.Equals("does/not/exist.txt", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Repack ────────────────────────────────────────────────────────────────

    [Fact]
    public void Repack_SubstitutedEntry_HasNewContent()
    {
        using var tmp = BuildTestArchive();
        var newBytes = Encoding.Latin1.GetBytes("Object.create Bar\r\n");
        string repacked = Path.GetTempFileName();
        try
        {
            RefractorFlatArchive.RepackToFile(repacked, tmp.Archive,
                new Dictionary<string, byte[]> { ["Init/StaticObjects.con"] = newBytes });
            var r = new RefractorFlatArchive(repacked);
            var entry = r.Entries.First(e => e.Name.EndsWith("StaticObjects.con"));
            Assert.Equal(newBytes, r.Read(entry));
        }
        finally { File.Delete(repacked); }
    }

    [Fact]
    public void Repack_UnchangedEntries_DecodeIdentically()
    {
        using var orig = BuildTestArchive();
        var newBytes = Encoding.Latin1.GetBytes("Object.create Bar\r\n");
        string repacked = Path.GetTempFileName();
        try
        {
            RefractorFlatArchive.RepackToFile(repacked, orig.Archive,
                new Dictionary<string, byte[]> { ["Init/StaticObjects.con"] = newBytes });
            var r = new RefractorFlatArchive(repacked);
            foreach (var e in orig.Archive.Entries.Where(e => !e.Name.EndsWith("StaticObjects.con")))
            {
                var re = r.Entries.First(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(orig.Archive.Read(e), r.Read(re));
            }
        }
        finally { File.Delete(repacked); }
    }

    [Fact]
    public void Repack_PreservesXPackId()
    {
        using var tmp = BuildTempArchive(SyntheticEntries(), compress: true, xPackId: XPackId.RoadToRome);
        string repacked = Path.GetTempFileName();
        try
        {
            RefractorFlatArchive.RepackToFile(repacked, tmp.Archive, new Dictionary<string, byte[]>());
            Assert.Equal(XPackId.RoadToRome, new RefractorFlatArchive(repacked).XPackId);
        }
        finally { File.Delete(repacked); }
    }

    // ── IsClientOnlyEntry ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("textures/ground.dds",       true)]
    [InlineData("sounds/fire.wav",           true)]
    [InlineData("movies/intro.bik",          true)]
    [InlineData("terrain/surface.tga",       true)]
    [InlineData("LightmapShadowBits.lsb",    true)]
    [InlineData("TerrainPalette.pal",        true)]
    [InlineData("Init.con",                  false)]
    [InlineData("StaticObjects.con",         false)]
    [InlineData("Heightmap.raw",             false)]
    public void IsClientOnlyEntry_ClassifiesCorrectly(string name, bool expected)
    {
        Assert.Equal(expected, RefractorFlatArchive.IsClientOnlyEntry(name));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Writes entries to a temp file and opens it. Dispose to delete the file.</summary>
    private static TempArchive BuildTempArchive(
        IReadOnlyList<(string Name, byte[] Data)> entries, bool compress, XPackId xPackId)
    {
        string path = Path.GetTempFileName();
        RefractorFlatArchive.WriteFile(path, entries, compress, xPackId);
        return new TempArchive(path, new RefractorFlatArchive(path));
    }

    private static TempArchive BuildTestArchive() => BuildTempArchive(SyntheticEntries(), compress: true, xPackId: XPackId.Default);

    private sealed class TempArchive(string path, RefractorFlatArchive archive) : IDisposable
    {
        public string Path { get; } = path;
        public RefractorFlatArchive Archive { get; } = archive;
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    private static List<(string Name, byte[] Data)> SyntheticEntries()
    {
        var rng = new Random(7);
        var list = new List<(string Name, byte[] Data)>();
        for (int i = 0; i < 8; i++)
        {
            var d = new byte[500 + i * 300];
            rng.NextBytes(d);
            list.Add(($"folder/file{i:00}.dat", d));
        }
        list.Add(("Init/StaticObjects.con",
            Encoding.Latin1.GetBytes(string.Concat(Enumerable.Repeat("object.create foo\r\nobject.absolutePosition 1/2/3\r\n", 20)))));
        list.Add(("empty.txt", Array.Empty<byte>()));
        return list;
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    internal static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
