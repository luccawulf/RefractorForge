using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

public class ProjectTests
{
    [Fact]
    public void RfProject_custom_round_trips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rf_proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var p = new RfProject
            {
                Name = "TestProj", Game = "BFVietnam", Mod = "bfvietnam", PatchNumber = "003",
                Mode = RfMode.Custom, ProjectFolder = dir, MapName = "FooMap", GameTestDir = @"D:\g",
                MeshArchives = { @"D:\g\StandardMesh.rfa", @"D:\g\objects.rfa" },
                TextureArchives = { @"D:\g\Texture.rfa" },
                LexiconFiles = { @"D:\g\lexiconAll.dat" },
                LevelArchives = { @"D:\g\levels\Foo.rfa" },
            };
            p.Save();
            var q = RfProject.Load(p.FilePath);
            Assert.Equal("TestProj", q.Name);
            Assert.Equal("BFVietnam", q.Game);
            Assert.Equal("003", q.PatchNumber);
            Assert.Equal(RfMode.Custom, q.Mode);
            Assert.Equal(2, q.MeshArchives.Count);
            Assert.Single(q.TextureArchives);
            Assert.Equal("FooMap", q.MapName);
            Assert.Equal(@"D:\g", q.GameTestDir);
            Assert.Equal(dir.TrimEnd('\\', '/'), q.ProjectFolder.TrimEnd('\\', '/'));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RfProject_default_round_trips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rf_proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var p = new RfProject { Name = "Def", Game = "BF1942", Mod = "DesertCombat", Mode = RfMode.Default, ProjectFolder = dir, GameRoot = @"D:\Games\BF1942", RunTestPacked = false };
            p.Save();
            var q = RfProject.Load(p.FilePath);
            Assert.Equal(RfMode.Default, q.Mode);
            Assert.Equal(@"D:\Games\BF1942", q.GameRoot);
            Assert.False(q.RunTestPacked);
            Assert.Equal("DesertCombat", q.Mod);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ExtractToFolder_strips_level_prefix_and_writes_files()
    {
        string rfa = Path.Combine(Path.GetTempPath(), "rf_extract_" + Guid.NewGuid().ToString("N")[..8] + ".rfa");
        string dest = Path.Combine(Path.GetTempPath(), "rf_extract_out_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var entries = new List<(string, byte[])>
            {
                ("bf1942/levels/TestMap/Heightmap.raw", new byte[] { 1, 2, 3, 4 }),
                ("bf1942/levels/TestMap/StaticObjects.con", Encoding.ASCII.GetBytes("object.create foo")),
                ("bf1942/levels/TestMap/Init/Terrain.con", Encoding.ASCII.GetBytes("GeometryTemplate.worldSize 1024")),
            };
            RefractorFlatArchive.WriteFile(rfa, entries, compress: true, xPackId: XPackId.Default);

            int n = LevelSaver.ExtractToFolder(new[] { rfa }, dest);
            Assert.Equal(3, n);
            Assert.True(File.Exists(Path.Combine(dest, "Heightmap.raw")), "Heightmap.raw lands at the folder root (prefix stripped)");
            Assert.True(File.Exists(Path.Combine(dest, "StaticObjects.con")), "StaticObjects.con at root");
            Assert.True(File.Exists(Path.Combine(dest, "Init", "Terrain.con")), "Init/Terrain.con sub-folder preserved");
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(dest, "Heightmap.raw")));
        }
        finally { try { File.Delete(rfa); } catch { } try { Directory.Delete(dest, true); } catch { } }
    }
}
