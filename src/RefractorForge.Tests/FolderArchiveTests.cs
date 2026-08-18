using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// An extracted level keeps its own objects and textures as ordinary files, while the mesh and texture libraries
/// only ever spoke .rfa - so a map's custom content was visible when the map was opened through its mod and gone
/// once extracted into a project. <see cref="RefractorFlatArchive.FromFolder"/> presents a directory through the
/// same entry API, so every lookup, category rule and assembly walker works unchanged.
/// </summary>
public class FolderArchiveTests
{
    private static string NewLevelDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "rf_lvl_" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    /// <summary>Entry names carry the levels/&lt;folder&gt;/ prefix a real level archive has, because the object
    /// indexers read that shape to tell a level's OWN objects from a mod's.</summary>
    [Fact]
    public void FolderEntriesLookLikeLevelArchiveEntries()
    {
        var dir = NewLevelDir("MyMap");
        try
        {
            Write(dir, "objects/Car/Objects.con", "ObjectTemplate.create Bundle Car");
            var arc = RefractorFlatArchive.FromFolder(dir);
            var e = Assert.Single(arc.Entries);
            Assert.Equal("levels/MyMap/objects/Car/Objects.con", e.Name);
            Assert.Equal("ObjectTemplate.create Bundle Car", System.Text.Encoding.Latin1.GetString(arc.Read(e)));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    /// <summary>The whole point: a level's own objects are found with NO archive present at all.</summary>
    [Fact]
    public void MeshLibraryReadsAnExtractedLevelsObjects()
    {
        var dir = NewLevelDir("MyMap");
        try
        {
            Write(dir, "objects/Willy/Objects.con", "ObjectTemplate.create PlayerControlObject Willy");
            Write(dir, "objects/Willy/Geometries.con", "GeometryTemplate.create StandardMesh Willy_m1");
            var lib = MeshLibrary.Open(dir);
            Assert.Contains("Willy", lib.AssembledTemplateNames, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(MeshLibrary.MapObjectsCategory, lib.CategoryOf["willy"]);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    /// <summary>Levels nest to whatever depth they like. DC Basrah Nights keeps
    /// Objects/Buildings/Common/BN-clouds1_m1/objects.con, and reading the FIRST folder under objects/ would file the
    /// category name ("Buildings") as though it were an object.</summary>
    [Fact]
    public void DeeplyNestedMapObjectsUseTheirOwnFolderName()
    {
        var dir = NewLevelDir("DC_Basrah_Nights");
        try
        {
            Write(dir, "Objects/Buildings/Common/BN-clouds1_m1/objects.con", "ObjectTemplate.create Bundle BN-clouds1_m1");
            Write(dir, "Objects/Buildings/Common/BN-clouds1_m1/geometries.con", "GeometryTemplate.create StandardMesh BN-clouds1_m1");
            var lib = MeshLibrary.Open(dir);
            Assert.Contains("BN-clouds1_m1", lib.AssembledTemplateNames, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(MeshLibrary.MapObjectsCategory, lib.CategoryOf["bn-clouds1"]);
            Assert.False(lib.CategoryOf.ContainsKey("buildings"), "the category folder must not be filed as an object");
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void MissingFolderIsNotFatal()
    {
        var lib = MeshLibrary.Open(Path.Combine(Path.GetTempPath(), "rf_no_such_" + Guid.NewGuid().ToString("N")[..8]));
        Assert.Equal(0, lib.MeshCount);
    }
}
