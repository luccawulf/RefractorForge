using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A level can carry SEVERAL full sets of identically named terrain tiles. Saigon68 ships a
/// <c>BACKUP_SAIGON_TERRAIN/</c> folder holding its own tx00x00.dds..tx03x03.dds beside the real
/// <c>Textures/</c> set, and matching tiles by bare file name picked the backup: the editor displayed the backup's
/// ground, and every texture paint and every sun-shadow merge was written back into the BACKUP folder. The terrain
/// the game draws never changed, which is why baked shadows were visible in the editor and absent in game.
///
/// The engine resolves tiles through <c>GeometryTemplate.texBaseName</c>. So do we now.
/// </summary>
public class TerrainTileFolderTests
{
    private const string TerrainCon = """
        GeometryTemplate.create patchTerrain terrainGeometry
        GeometryTemplate.materialSize 256
        GeometryTemplate.worldSize 1024
        GeometryTemplate.yScale 0.7
        GeometryTemplate.texBaseName BfVietnam\levels\Saigon68\Textures\Tx
        GeometryTemplate.detailTexName BfVietnam\levels\Saigon68\Textures\Detail
        """;

    [Fact]
    public void TexBaseName_and_its_folder_are_parsed()
    {
        var cfg = TerrainConfig.Parse(TerrainCon.Split('\n'));
        Assert.Equal(@"BfVietnam\levels\Saigon68\Textures\Tx", cfg.TexBaseName);
        Assert.Equal("BfVietnam/levels/Saigon68/Textures", cfg.TileFolder);
    }

    [Fact]
    public void A_level_with_no_texBaseName_has_no_tile_folder()
        => Assert.Null(TerrainConfig.Parse(new[] { "GeometryTemplate.worldSize 1024" }).TileFolder);

    private static Texture2D Flat(int size, byte v)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255; }
        return new Texture2D(size, size, px);
    }

    /// <summary>The tile keeps the path it was loaded from, so a re-split names that same entry.</summary>
    [Fact]
    public void A_tile_is_re_emitted_under_the_path_it_came_from()
    {
        var tt = TerrainTexture.FromTileBytes(
            new[] { ("BfVietnam/levels/Saigon68/Textures/tx00x00.dds", DxtEncoder.EncodeDxt1Mipped(Flat(256, 120))) },
            1024f)!;
        var names = tt.SplitToTiles(tt.BakeAtlas(256)).Select(t => t.fileName).ToList();
        Assert.Equal(new[] { "BfVietnam/levels/Saigon68/Textures/tx00x00.dds" }, names);
    }

    /// <summary>The end-to-end guard: a painted tile must land in Textures/, never in the backup folder that
    /// holds a file of exactly the same name.</summary>
    [Fact]
    public void A_painted_tile_is_saved_into_Textures_not_into_a_backup_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_tile_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var real = DxtEncoder.EncodeDxt1Mipped(Flat(256, 200));
            var backup = DxtEncoder.EncodeDxt1Mipped(Flat(256, 40));
            var rfa = Path.Combine(dir, "Map.rfa");
            RefractorFlatArchive.WriteFile(rfa, new List<(string, byte[])>
            {
                ("BfVietnam/levels/Saigon68/Init/Terrain.con",                     Encoding.Latin1.GetBytes(TerrainCon)),
                // The backup comes FIRST, which is what made archive order decide the winner.
                ("BfVietnam/levels/Saigon68/BACKUP_SAIGON_TERRAIN/tx00x00.dds",    backup),
                ("BfVietnam/levels/Saigon68/Textures/tx00x00.dds",                 real),
            }, compress: true, xPackId: XPackId.Default);

            var painted = DxtEncoder.EncodeDxt1Mipped(Flat(256, 99));
            var outRfa = Path.Combine(dir, "Out.rfa");
            LevelSaver.WritePatchRfa(rfa, outRfa, null, null, null, null,
                extraFiles: new[] { ("BfVietnam/levels/Saigon68/Textures/tx00x00.dds", painted) });

            // WritePatchRfa writes only what changed, so the test is: the patch aims at Textures/, and the backup
            // folder is not in it at all. Before the fix the single tile in here was the BACKUP one.
            var after = new RefractorFlatArchive(outRfa);
            var names = after.Entries.Select(e => e.Name.Replace('\\', '/')).ToList();
            Assert.Contains("BfVietnam/levels/Saigon68/Textures/tx00x00.dds", names);
            Assert.DoesNotContain(names, n => n.Contains("BACKUP_SAIGON_TERRAIN", StringComparison.OrdinalIgnoreCase));
            var e2 = after.Entries.First(e => e.Name.Replace('\\', '/').EndsWith("Textures/tx00x00.dds", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(painted, after.Read(e2));
            Assert.NotEqual(backup, after.Read(e2));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A bare leaf still resolves, so levels that predate the qualified names keep saving.</summary>
    [Fact]
    public void A_bare_leaf_name_still_finds_its_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_tile_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var rfa = Path.Combine(dir, "Map.rfa");
            RefractorFlatArchive.WriteFile(rfa, new List<(string, byte[])>
            {
                ("BfVietnam/levels/Saigon68/Textures/tx00x00.dds", Encoding.UTF8.GetBytes("old")),
            }, compress: true, xPackId: XPackId.Default);

            var outRfa = Path.Combine(dir, "Out.rfa");
            LevelSaver.WritePatchRfa(rfa, outRfa, null, null, null, null,
                extraFiles: new[] { ("tx00x00.dds", Encoding.UTF8.GetBytes("new")) });

            var after = new RefractorFlatArchive(outRfa);
            var e = after.Entries.First(x => x.Name.Replace('\\', '/').EndsWith("Textures/tx00x00.dds", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("new", Encoding.UTF8.GetString(after.Read(e)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
