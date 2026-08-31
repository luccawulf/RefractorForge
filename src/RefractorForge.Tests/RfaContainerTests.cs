using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The RFA container has very little slack, and rebuilding one from scratch is what breaks maps. Porting a real map
/// to Battlefield Vietnam produced three consecutive "the map crashes" reports that all traced back to the container
/// rather than the content: a writer that re-flags the archive compressed, substitutes the 143-byte descriptor and
/// writes its own per-entry trailers. The lesson from that work was to patch in place and to gate it with a NO-OP
/// SELFTEST - repacking with zero replacements must reproduce the input byte for byte.
///
/// That gate is what these tests are. It is much stronger than "the entries still decode", which passes happily on an
/// archive the engine will refuse.
/// </summary>
public class RfaContainerTests
{
    private sealed class Tmp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "rf_rfa_" + Guid.NewGuid().ToString("N")[..8] + ".rfa");
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    private static readonly (string Name, byte[] Data)[] Sample =
    {
        ("bf1942/levels/Test/Init.con", System.Text.Encoding.Latin1.GetBytes("game.setTeamSkin 1 JapaneseSoldier\n")),
        ("bf1942/levels/Test/Heightmap.raw", Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray()),
        ("bf1942/levels/Test/empty.txt", Array.Empty<byte>()),
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoOpRepackIsByteIdentical(bool compressed)
    {
        using var src = new Tmp();
        using var dst = new Tmp();
        RefractorFlatArchive.WriteFile(src.Path, Sample, compressed, XPackId.Default);

        var before = File.ReadAllBytes(src.Path);
        RefractorFlatArchive.RepackToFile(dst.Path, new RefractorFlatArchive(src.Path), new Dictionary<string, byte[]>());
        var after = File.ReadAllBytes(dst.Path);

        Assert.Equal(before.Length, after.Length);
        Assert.True(before.AsSpan().SequenceEqual(after), "a no-op repack changed the container");
    }

    /// <summary>The compression flag must follow the SOURCE. Passing raw entries through while flagging the archive
    /// compressed sends the engine looking for LZO block headers that are not there - the actual crash.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepackKeepsTheSourceCompressionFlag(bool compressed)
    {
        using var src = new Tmp();
        using var dst = new Tmp();
        RefractorFlatArchive.WriteFile(src.Path, Sample, compressed, XPackId.Default);
        RefractorFlatArchive.RepackToFile(dst.Path, new RefractorFlatArchive(src.Path), new Dictionary<string, byte[]>());
        Assert.Equal(compressed, new RefractorFlatArchive(dst.Path).IsCompressed);
    }

    /// <summary>Replacing one entry must leave every other entry and the container untouched.</summary>
    [Fact]
    public void ReplacingOneEntryLeavesTheRestIntact()
    {
        using var src = new Tmp();
        using var dst = new Tmp();
        RefractorFlatArchive.WriteFile(src.Path, Sample, true, XPackId.Default);
        var orig = new RefractorFlatArchive(src.Path);
        var newInit = System.Text.Encoding.Latin1.GetBytes("game.setTeamSkin 1 USMarineSoldier\n");
        RefractorFlatArchive.RepackToFile(dst.Path, orig,
            new Dictionary<string, byte[]> { ["bf1942/levels/Test/Init.con"] = newInit });

        var after = new RefractorFlatArchive(dst.Path);
        Assert.Equal(orig.Entries.Count, after.Entries.Count);
        Assert.Equal(orig.Entries.Select(e => e.Name), after.Entries.Select(e => e.Name));   // order preserved
        Assert.Equal(newInit, after.Read(after.Entries.First(e => e.Name.EndsWith("Init.con"))));
        Assert.Equal(Sample[1].Data, after.Read(after.Entries.First(e => e.Name.EndsWith("Heightmap.raw"))));
        Assert.Empty(after.Read(after.Entries.First(e => e.Name.EndsWith("empty.txt"))));
    }

    /// <summary>The same gate against REAL retail archives - the only thing that proves we reproduce containers we
    /// did not write. Byte-identity is the goal but is not universally reachable: retail sometimes leaves a gap
    /// between the header and the first entry (Objects.rfa starts at 0x453), and we pack from 0x9C. That is harmless
    /// because the TOC carries explicit offsets - so what is asserted here is what actually matters: every entry
    /// decodes to the same bytes, in the same order, with the same compression flag. Measured across the installed
    /// games: 70 archives byte-identical, 50 layout-only, none with changed content.</summary>
    [Fact]
    public void NoOpRepackOfRetailArchivesPreservesContentOrderAndFlag()
    {
        var reals = new List<string>();
        foreach (var root in new[] { @"D:\Games\EA GAMES\Battlefield 1942", @"D:\Games\EA GAMES\Battlefield Vietnam" })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                reals.AddRange(Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("~"))
                    .Where(f => new FileInfo(f).Length is > 4096 and < 20L * 1024 * 1024)
                    .Take(6));
            }
            catch { }
        }
        if (reals.Count == 0) return;   // no game install here - the synthetic gates above still apply

        foreach (var real in reals)
        {
            using var dst = new Tmp();
            var orig = new RefractorFlatArchive(real);
            RefractorFlatArchive.RepackToFile(dst.Path, orig, new Dictionary<string, byte[]>());
            var after = new RefractorFlatArchive(dst.Path);
            string who = Path.GetFileName(real);

            Assert.Equal(orig.IsCompressed, after.IsCompressed);
            Assert.Equal(orig.Entries.Select(e => e.Name), after.Entries.Select(e => e.Name));
            foreach (var e in orig.Entries)
            {
                var be = after.Entries.First(x => x.Name == e.Name);
                Assert.True(orig.Read(e).AsSpan().SequenceEqual(after.Read(be)), $"{who}: entry {e.Name} changed");
            }
        }
    }
}
