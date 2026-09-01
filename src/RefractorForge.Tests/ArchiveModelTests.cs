using RefractorForge.Archive;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The edit model behind the archive browser. The RFA codec itself is gated elsewhere; what is new here is the
/// pending-edit overlay and the two routes Save can take — repack-in-place when only contents changed, full
/// rewrite when the entry list changed.
///
/// These are the paths where a mistake would quietly hand someone a broken archive, which is the exact failure
/// this program exists to avoid, so each one is checked by reading the saved file back and comparing bytes rather
/// than by trusting that the call returned.
/// </summary>
public class ArchiveModelTests : IDisposable
{
    private readonly string _dir;

    public ArchiveModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rfarch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Make(string name, params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_dir, name);
        RefractorFlatArchive.WriteFile(path, entries, compress: true, XPackId.Default);
        return path;
    }

    private static byte[] Text(string s) => System.Text.Encoding.Latin1.GetBytes(s);

    /// <summary>Compressible content, so entries genuinely go down the LZO-wrapped path.</summary>
    private static byte[] Bulk(int n, byte seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(seed + (i % 17));
        return b;
    }

    [Fact]
    public void Opening_reports_the_entries_and_their_folders()
    {
        var path = Make("a.rfa",
            ("bf1942/levels/Berlin/Init.con", Text("game.setBar 1\r\n")),
            ("bf1942/levels/Berlin/Heightmap.raw", Bulk(4096, 3)),
            ("texture/roof.dds", Bulk(2048, 9)));

        using var m = new ArchiveModel();
        m.Open(path);

        Assert.True(m.IsOpen);
        Assert.Equal(3, m.Items.Count);
        Assert.False(m.IsDirty);

        var init = m.Find("bf1942/levels/Berlin/Init.con");
        Assert.NotNull(init);
        Assert.Equal("bf1942/levels/Berlin", init!.Folder);
        Assert.Equal("Init.con", init.FileName);
        Assert.Equal(Text("game.setBar 1\r\n"), m.Read(init));
    }

    [Fact]
    public void A_replacement_is_held_until_save_and_then_lands_byte_for_byte()
    {
        var path = Make("b.rfa",
            ("one.con", Text("original one")),
            ("two.con", Text("original two")),
            ("data/bulk.bin", Bulk(50_000, 5)));

        var replacement = Text("REPLACED, and rather longer than what was there before.");

        using (var m = new ArchiveModel())
        {
            m.Open(path);
            var one = m.Find("one.con")!;
            m.Replace(one, replacement);

            Assert.True(m.IsDirty);
            Assert.Equal(ArchiveModel.EntryState.Replaced, one.State);
            // Held in memory: the file on disk must not have moved yet.
            Assert.Equal(Text("original one"), new RefractorFlatArchive(path).ReadByName("one.con"));

            m.Save(path);
            Assert.False(m.IsDirty);
        }

        var after = new RefractorFlatArchive(path);
        Assert.Equal(3, after.Entries.Count);
        Assert.Equal(replacement, after.ReadByName("one.con"));
        Assert.Equal(Text("original two"), after.ReadByName("two.con"));      // untouched neighbour
        Assert.Equal(Bulk(50_000, 5), after.ReadByName("data/bulk.bin"));
        Assert.Null(RefractorFlatArchive.Validate(path));
    }

    [Fact]
    public void Adding_and_deleting_rewrites_the_entry_list()
    {
        var path = Make("c.rfa",
            ("keep.con", Text("keep me")),
            ("drop.con", Text("drop me")),
            ("also/keep.bin", Bulk(8192, 1)));

        using (var m = new ArchiveModel())
        {
            m.Open(path);
            m.Delete(m.Find("drop.con")!);
            m.Add("new/added.con", Text("brand new"));
            m.Save(path);
        }

        var after = new RefractorFlatArchive(path);
        var names = after.Entries.Select(e => e.Name).ToList();
        Assert.Contains("keep.con", names);
        Assert.Contains("also/keep.bin", names);
        Assert.Contains("new/added.con", names);
        Assert.DoesNotContain("drop.con", names);
        Assert.Equal(Text("brand new"), after.ReadByName("new/added.con"));
        Assert.Equal(Text("keep me"), after.ReadByName("keep.con"));
        Assert.Null(RefractorFlatArchive.Validate(path));
    }

    [Fact]
    public void Adding_a_name_that_already_exists_replaces_it_rather_than_duplicating()
    {
        // The container permits two entries with the same path and the engine simply takes one of them. That is
        // not a coin toss worth shipping into a map, so Add collapses onto the existing entry.
        var path = Make("d.rfa", ("dup.con", Text("first")));

        using var m = new ArchiveModel();
        m.Open(path);
        m.Add("dup.con", Text("second"));

        Assert.Single(m.Items);
        m.Save(path);

        var after = new RefractorFlatArchive(path);
        Assert.Single(after.Entries);
        Assert.Equal(Text("second"), after.ReadByName("dup.con"));
    }

    [Fact]
    public void Revert_restores_the_original_bytes_and_sizes()
    {
        var path = Make("e.rfa", ("x.con", Text("original")), ("y.con", Text("other")));

        using var m = new ArchiveModel();
        m.Open(path);
        var x = m.Find("x.con")!;
        int origSize = x.UncompressedSize;

        m.Replace(x, Text("a much longer replacement string"));
        Assert.True(m.IsDirty);

        m.Revert(x);
        Assert.False(m.IsDirty);
        Assert.Equal(ArchiveModel.EntryState.Unchanged, x.State);
        Assert.Equal(origSize, x.UncompressedSize);
        Assert.Equal(Text("original"), m.Read(x));

        // An added entry has no original to go back to, so reverting removes it.
        var added = m.Add("z.con", Text("new"));
        m.Revert(added);
        Assert.Null(m.Find("z.con"));
    }

    [Fact]
    public void Packing_a_folder_reproduces_every_file_exactly()
    {
        var src = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(src, "bf1942", "levels", "Test"));
        Directory.CreateDirectory(Path.Combine(src, "texture"));
        File.WriteAllBytes(Path.Combine(src, "bf1942", "levels", "Test", "Init.con"), Text("game.setMode conquest\r\n"));
        File.WriteAllBytes(Path.Combine(src, "texture", "wall.dds"), Bulk(65_536, 7));   // spans several blocks
        File.WriteAllBytes(Path.Combine(src, "readme.txt"), Text("hello"));

        string outPath = Path.Combine(_dir, "packed.rfa");
        ArchiveModel.PackFolder(src, outPath, compress: true, XPackId.Default);

        Assert.Null(RefractorFlatArchive.Validate(outPath));

        var a = new RefractorFlatArchive(outPath);
        Assert.Equal(3, a.Entries.Count);
        Assert.Equal(Text("game.setMode conquest\r\n"), a.ReadByName("bf1942/levels/Test/Init.con"));
        Assert.Equal(Bulk(65_536, 7), a.ReadByName("texture/wall.dds"));
        Assert.Equal(Text("hello"), a.ReadByName("readme.txt"));
    }

    [Fact]
    public void A_save_with_no_edits_leaves_every_entry_identical()
    {
        // The repack route copies untouched entries region-for-region, so a no-op save must be a no-op in
        // content terms. This is the property that stops a save from quietly rewriting things nobody asked about.
        var path = Make("f.rfa",
            ("a.con", Text("alpha")),
            ("b/c.bin", Bulk(40_000, 2)),
            ("d/e/f.dds", Bulk(70_000, 11)));

        var before = new RefractorFlatArchive(path).Entries
            .ToDictionary(e => e.Name, e => new RefractorFlatArchive(path).ReadByName(e.Name));

        using (var m = new ArchiveModel())
        {
            m.Open(path);
            m.Save(path);
        }

        var after = new RefractorFlatArchive(path);
        Assert.Equal(before.Count, after.Entries.Count);
        foreach (var e in after.Entries)
            Assert.Equal(before[e.Name], after.Read(e));
        Assert.Null(RefractorFlatArchive.Validate(path));
    }
}

internal static class ArchiveTestExtensions
{
    /// <summary>Read one entry by its archive path.</summary>
    public static byte[] ReadByName(this RefractorFlatArchive a, string name)
    {
        var e = a.Entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{name}' is not in the archive.");
        return a.Read(e);
    }
}
