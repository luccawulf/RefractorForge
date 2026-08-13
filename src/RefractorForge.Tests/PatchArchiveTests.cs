using RefractorForge.Formats;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The engine mounts <c>&lt;Map&gt;_NNN.rfa</c> over <c>&lt;Map&gt;.rfa</c>, and a level's terrain textures often live
/// in one of those patches. The viewer's load path expanded them; CREATING A PROJECT did not, so a project made from
/// a base .rfa extracted an incomplete level and its ground rendered from the wrong tiles. Both paths now share
/// <see cref="LevelSaver.WithPatchArchives"/>, and these gates pin its ordering rules.
/// </summary>
public class PatchArchiveTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "rf_patch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Touch(string dir, params string[] names)
    {
        foreach (var n in names) File.WriteAllBytes(Path.Combine(dir, n), Array.Empty<byte>());
    }

    [Fact]
    public void PatchesFollowTheirBaseInNumericOrder()
    {
        var d = NewDir();
        try
        {
            Touch(d, "Wake.rfa", "Wake_003.rfa", "Wake_000.rfa", "Wake_001.rfa");
            var got = LevelSaver.WithPatchArchives(new[] { Path.Combine(d, "Wake.rfa") })
                                .Select(Path.GetFileName).ToArray();
            // Base first, then ascending: last-wins merging must apply higher patches last.
            Assert.Equal(new[] { "Wake.rfa", "Wake_000.rfa", "Wake_001.rfa", "Wake_003.rfa" }, got);
        }
        finally { Directory.Delete(d, true); }
    }

    /// <summary>Levels carry anywhere from zero to many patches and do not pad to a fixed width. Sorting the text
    /// rather than the number would mount _10 before _2 and let the lower-numbered patch win the merge.</summary>
    [Fact]
    public void UnpaddedPatchNumbersSortNumerically()
    {
        var d = NewDir();
        try
        {
            Touch(d, "Map.rfa", "Map_10.rfa", "Map_2.rfa", "Map_1.rfa", "Map_003.rfa");
            var got = LevelSaver.WithPatchArchives(new[] { Path.Combine(d, "Map.rfa") })
                                .Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "Map.rfa", "Map_1.rfa", "Map_2.rfa", "Map_003.rfa", "Map_10.rfa" }, got);
        }
        finally { Directory.Delete(d, true); }
    }

    /// <summary>Zero patches is the common case and must be left exactly alone.</summary>
    [Fact]
    public void LevelWithNoPatchesIsUnchanged()
    {
        var d = NewDir();
        try
        {
            Touch(d, "Akina_Mountain.rfa", "SomethingElse.rfa");
            var got = LevelSaver.WithPatchArchives(new[] { Path.Combine(d, "Akina_Mountain.rfa") })
                                .Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "Akina_Mountain.rfa" }, got);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void UnrelatedAndLookalikeArchivesAreNotPulledIn()
    {
        var d = NewDir();
        try
        {
            // Wake_Evenings is a DIFFERENT map, not a patch of Wake - the suffix must be purely numeric.
            Touch(d, "Wake.rfa", "Wake_001.rfa", "Wake_Evenings.rfa", "Wake_a_new_day.rfa", "Midway.rfa");
            var got = LevelSaver.WithPatchArchives(new[] { Path.Combine(d, "Wake.rfa") })
                                .Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "Wake.rfa", "Wake_001.rfa" }, got);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void AlreadyPickedPatchesAreNotDuplicated()
    {
        var d = NewDir();
        try
        {
            Touch(d, "Wake.rfa", "Wake_001.rfa");
            var picked = new[] { Path.Combine(d, "Wake.rfa"), Path.Combine(d, "Wake_001.rfa") };
            var got = LevelSaver.WithPatchArchives(picked).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "Wake.rfa", "Wake_001.rfa" }, got);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void MissingDirectoryIsNotFatal()
    {
        var got = LevelSaver.WithPatchArchives(new[] { @"Z:\no\such\place\Ghost.rfa" });
        Assert.Single(got);   // the input survives; only the sibling scan is skipped
    }
}
