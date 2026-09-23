using System.Text;
using RefractorBridge.Rfa;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The container gate for the BF1942 -> Battlefield Vietnam converter (RefractorBridge M2).
///
/// The most expensive failure in the hand port was container corruption rather than content: a writer that
/// re-flags the archive, zeroes the 148-byte header blob or rebuilds the block descriptors produces a file the
/// engine dies on, and three consecutive "the map crashes" reports were entirely that. Before the converter is
/// allowed to write an archive, repacking one with ZERO changes has to carry it across untouched.
///
/// These tests run against the real game archives when they are installed and skip when they are not, so a
/// checkout without Battlefield 1942 still passes.
/// </summary>
public class BridgeArchiveRepackTests
{
    private const string Bf42Levels = @"D:\Games\EA GAMES\Battlefield 1942\Mods\bf1942\Archives\bf1942\levels";

    private static string? Level(string name)
    {
        string p = Path.Combine(Bf42Levels, name);
        return File.Exists(p) ? p : null;
    }

    [Theory]
    [InlineData("Solomon_Islands_001.rfa")]
    [InlineData("berlin_003.rfa")]
    [InlineData("Coral_Sea_003.rfa")]
    [InlineData("Battle_of_the_Bulge_003.rfa")]
    public void A_zero_change_repack_carries_the_archive_across_untouched(string archive)
    {
        if (Level(archive) is not { } path) return;                 // games not installed on this machine

        var proof = RepackSelfTest.Run(path);

        Assert.Null(proof.Failure);
        Assert.True(proof.HeaderPreserved, "the compressed flag and 148-byte header blob must survive");
        Assert.True(proof.TableOfContentsPreserved, "entry names, order and sizes must survive");
        Assert.True(proof.RawRegionsPreserved, "data regions must be carried RAW - nothing re-compressed");
        Assert.True(proof.EveryEntryDecodesIdentically, "every entry must still decode to the same bytes");
        Assert.True(proof.Faithful);
    }

    [Fact]
    public void An_archive_already_in_table_order_comes_back_byte_for_byte()
    {
        // Solomon_Islands_001 is a raw (uncompressed) archive whose data regions are already laid out in TOC
        // order, so there is nothing to relay and the output is literally the input.
        if (Level("Solomon_Islands_001.rfa") is not { } path) return;

        var proof = RepackSelfTest.Run(path);

        Assert.True(proof.Faithful);
        Assert.True(proof.ByteIdentical);
        Assert.False(proof.DataRegionsReordered);
        Assert.False(proof.Compressed);
    }

    [Fact]
    public void An_archive_stored_out_of_table_order_is_relaid_but_not_altered()
    {
        // berlin_003 lists AI.con first while its data sits 2,505 bytes in, with another entry's data at the
        // front. The repacker writes regions in TOC order, so the file is physically reordered and functionally
        // identical. Demanding byte-identity here would fail an archive for a difference the engine cannot see -
        // which is exactly why the gate checks faithfulness instead.
        if (Level("berlin_003.rfa") is not { } path) return;

        var proof = RepackSelfTest.Run(path);

        Assert.True(proof.Faithful);
        Assert.True(proof.DataRegionsReordered);
        Assert.False(proof.ByteIdentical);
    }

    [Fact]
    public void Replacing_one_entry_leaves_every_other_entry_alone()
    {
        // The patcher's real job for the level porter: swap a rewritten .con in and touch nothing else.
        if (Level("Coral_Sea_003.rfa") is not { } path) return;

        var original = new RefractorFlatArchive(path);
        var target = original.Entries.First(e => e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase));
        byte[] replacement = Encoding.Latin1.GetBytes("rem rewritten by RefractorBridge\r\nrenderer.fogstart 10\r\n");

        var before = original.Entries.ToDictionary(e => e.Name, e => original.Read(e), StringComparer.Ordinal);

        string tmp = Path.Combine(Path.GetTempPath(), $"rbridge_replace_{Environment.ProcessId}.rfa");
        try
        {
            RefractorFlatArchive.RepackToFile(tmp, original,
                new Dictionary<string, byte[]> { [target.Name] = replacement });

            var after = new RefractorFlatArchive(tmp);
            Assert.Equal(original.Entries.Count, after.Entries.Count);

            foreach (var e in after.Entries)
            {
                byte[] got = after.Read(e);
                if (string.Equals(e.Name, target.Name, StringComparison.Ordinal))
                    Assert.Equal(replacement, got);
                else
                    Assert.Equal(before[e.Name], got);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void A_fully_uncompressed_archive_survives_a_repack()
    {
        // The documented crash: an uncompressed source flagged as compressed on the way out sends the engine
        // looking for block headers that do not exist. 128_planes is BF1942's fully-raw archive, 314 MB over
        // 1,257 entries - the worst case for both the flag and the size.
        if (Level("128_planes.rfa") is not { } path) return;

        var proof = RepackSelfTest.Run(path);

        Assert.True(proof.Faithful);
        Assert.False(proof.Compressed);
        Assert.True(proof.ByteIdentical);
    }
}
