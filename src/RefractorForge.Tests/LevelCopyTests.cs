using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// File > Copy Level To (Battlecraft's answer to renaming a map): every entry moves to the new level folder and the
/// level's own paths are re-pointed - nothing else changes, and nothing is ever overwritten.
/// </summary>
public class LevelCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_levelcopy_" + Guid.NewGuid().ToString("N"));
    public LevelCopyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static byte[] T(string s) => Encoding.Latin1.GetBytes(s);
    private static string S(byte[] b) => Encoding.Latin1.GetString(b);

    private static readonly byte[] Raw = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();

    private static List<(string, byte[])> FooLevel(string p) => new()
    {
        (p + "Init.con", T("run Init/Terrain\r\ntextureManager.alternativePath BfVietnam/levels/Foo/Texture\r\n")),
        (p + "Init/Terrain.con", T("GeometryTemplate.file BfVietnam\\levels\\Foo\\Heightmap\r\n" +
                                  "GeometryTemplate.materialMap BfVietnam\\levels\\Foo\\Materialmap\r\n" +
                                  "GeometryTemplate.texBaseName BfVietnam\\levels\\foo\\Textures\\Tx\r\n" +
                                  "rem another map: BfVietnam\\levels\\Foo_Night\\Heightmap\r\n")),
        (p + "Growth/overGrowth.wst", T(" <?xml version=\"1.0\"?>\r\n<overGrowth materialMapFilename=\"BfVietnam\\levels\\Foo\\growth\\overgrowthmap\" viewdistance=\"400\">\r\n")),
        (p + "Menu/init.con", T("game.setLoadPicture Load/Foo.dds\r\n")),
        (p + "StaticObjects.con", T("Object.create tree_m1\r\n")),
        (p + "Heightmap.raw", Raw),
        (p + "Textures/env_Foo_01.dds", T("DDS cube face")),
    };

    [Fact]
    public void A_packed_level_moves_to_the_new_folder_with_its_paths_re_pointed()
    {
        var rfa = Path.Combine(_dir, "Foo.rfa");
        RefractorFlatArchive.WriteFile(rfa, FooLevel("BfVietnam/levels/Foo/"), compress: true, XPackId.Default);
        // A patch beside it wins over the base, as the game layers it.
        RefractorFlatArchive.WriteFile(Path.Combine(_dir, "Foo_001.rfa"),
            new List<(string, byte[])> { ("BfVietnam/levels/Foo/StaticObjects.con", T("Object.create patched_m1\r\n")) }, compress: true, XPackId.Default);
        File.WriteAllText(Path.Combine(_dir, "Foo.RefractorForgeNotes.json"), "{}");

        var r = LevelCopy.CopyArchive(rfa, "Bar");

        Assert.Equal(Path.Combine(_dir, "Bar.rfa"), r.Destination);
        Assert.Equal("Foo", r.OldName);
        var copy = new RefractorFlatArchive(r.Destination);
        Assert.All(copy.Entries, e => Assert.StartsWith("BfVietnam/levels/Bar/", e.Name.Replace('\\', '/')));
        byte[] Read(string rel) => copy.Read(copy.Entries.Single(e => e.Name.Replace('\\', '/').Equals("BfVietnam/levels/Bar/" + rel, StringComparison.OrdinalIgnoreCase)));

        var terrain = S(Read("Init/Terrain.con"));
        Assert.Contains(@"GeometryTemplate.file BfVietnam\levels\Bar\Heightmap", terrain);
        Assert.Contains(@"GeometryTemplate.materialMap BfVietnam\levels\Bar\Materialmap", terrain);
        Assert.Contains(@"GeometryTemplate.texBaseName BfVietnam\levels\Bar\Textures\Tx", terrain);   // any case
        Assert.Contains(@"levels\Foo_Night\Heightmap", terrain);                                      // another map: untouched
        Assert.Contains(@"materialMapFilename=""BfVietnam\levels\Bar\growth\overgrowthmap""", S(Read("Growth/overGrowth.wst")));
        Assert.StartsWith(" <?xml", S(Read("Growth/overGrowth.wst")));                                // byte for byte otherwise
        Assert.Contains("alternativePath BfVietnam/levels/Bar/Texture", S(Read("Init.con")));
        Assert.Equal("game.setLoadPicture Load/Foo.dds\r\n", S(Read("Menu/init.con")));                 // not a level path
        Assert.Equal(Raw, Read("Heightmap.raw"));
        Assert.Equal("DDS cube face", S(Read("Textures/env_Foo_01.dds")));                             // file names keep theirs
        Assert.Equal("Object.create patched_m1\r\n", S(Read("StaticObjects.con")));                     // the patch merged in
        Assert.Equal(5, r.References);
        Assert.True(r.OtherMentions >= 1);
        Assert.True(File.Exists(Path.Combine(_dir, "Bar.RefractorForgeNotes.json")));

        // The original is untouched, and a second copy to the same name is refused rather than overwriting.
        Assert.Contains("levels\\Foo\\Heightmap", S(new RefractorFlatArchive(rfa).Read(new RefractorFlatArchive(rfa).Entries.Single(e => e.Name.EndsWith("Terrain.con")))));
        Assert.Throws<IOException>(() => LevelCopy.CopyArchive(rfa, "Bar"));
    }

    [Fact]
    public void A_folder_level_is_copied_beside_itself()
    {
        var src = Path.Combine(_dir, "Foo");
        foreach (var (name, data) in FooLevel(""))
        {
            var f = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, data);
        }
        Directory.CreateDirectory(Path.Combine(src, "Backups"));
        File.WriteAllText(Path.Combine(src, "Backups", "old.con"), "x");
        File.WriteAllText(Path.Combine(src, "Foo.rfproj"), "{}");

        var r = LevelCopy.CopyFolder(src, "Bar");

        Assert.Equal(Path.Combine(_dir, "Bar"), r.Destination);
        Assert.Contains(@"BfVietnam\levels\Bar\Heightmap", File.ReadAllText(Path.Combine(r.Destination, "Init", "Terrain.con")));
        Assert.Equal(Raw, File.ReadAllBytes(Path.Combine(r.Destination, "Heightmap.raw")));
        Assert.False(Directory.Exists(Path.Combine(r.Destination, "Backups")));
        Assert.False(File.Exists(Path.Combine(r.Destination, "Foo.rfproj")));
        Assert.False(Directory.Exists(r.Destination + ".rfcopy"));
        Assert.Throws<IOException>(() => LevelCopy.CopyFolder(src, "Bar"));
    }

    [Theory]
    [InlineData("Bar", true)]
    [InlineData("My_Map-2", true)]
    [InlineData("My Map", false)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    public void Level_names_are_one_word(string name, bool ok) => Assert.Equal(ok, LevelCopy.NameProblem(name) is null);

    [InstallFact(Installs.BfvOriginalArchives)]
    public void A_retail_level_with_a_patch_copies_and_loads_under_its_new_name()
    {
        var levels = Path.Combine(Installs.BfvOriginalArchives, "BfVietnam", "Levels");
        var src = Path.Combine(_dir, "Ho_Chi_Minh_Trail.rfa");
        File.Copy(Path.Combine(levels, "Ho_Chi_Minh_Trail.rfa"), src);
        File.Copy(Path.Combine(levels, "Ho_Chi_Minh_Trail_001.rfa"), Path.Combine(_dir, "Ho_Chi_Minh_Trail_001.rfa"));

        var r = LevelCopy.CopyArchive(src, "RF_CopyTest");

        var copy = new RefractorFlatArchive(r.Destination);
        Assert.DoesNotContain(copy.Entries, e => e.Name.Contains("Ho_Chi_Minh_Trail/", StringComparison.OrdinalIgnoreCase)
                                               || e.Name.Contains("Ho_Chi_Minh_Trail\\", StringComparison.OrdinalIgnoreCase));
        var terrain = S(copy.Read(copy.Entries.Single(e => e.Name.Replace('\\', '/').EndsWith("/Init/Terrain.con", StringComparison.OrdinalIgnoreCase))));
        Assert.Contains(@"levels\RF_CopyTest\Heightmap", terrain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ho_Chi_Minh_Trail\\", terrain, StringComparison.OrdinalIgnoreCase);

        var orig = LevelArchive.FromRfa(LevelSaver.WithPatchArchives(new[] { src }));
        var moved = LevelArchive.FromRfa(r.Destination);
        Assert.Equal(orig.Config.WorldSize, moved.Config.WorldSize);
        Assert.Equal(orig.Heightmap.Width, moved.Heightmap.Width);
        Assert.Equal(orig.StaticObjects.Objects.Count, moved.StaticObjects.Objects.Count);
    }
}
