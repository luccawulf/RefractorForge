using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The tunnel water level went into a file the game never reads, or into no file at all. Two separate causes, both
/// silent: retail Saigon68 ships BACKUP_SAIGON_TERRAIN/Terrain.con at the SAME depth as Init/Terrain.con and the
/// saver took whichever came first in the archive; and a level can ship no Terrain.con of its own at all (echo's
/// Saigon68 is gameplay .con files and a menu image), where patching an existing file matches nothing.
/// </summary>
public class TunnelWaterSaveTests
{
    private static string TempRfa(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "rf_twl_" + Guid.NewGuid().ToString("N")[..8] + ".rfa");
        RefractorFlatArchive.WriteFile(path,
            entries.Select(e => (e.Name, (byte[])Encoding.Latin1.GetBytes(e.Text))).ToList(),
            compress: false, xPackId: XPackId.Default);
        return path;
    }

    private static string? ReadEntry(string rfa, string endsWith)
    {
        var a = new RefractorFlatArchive(rfa);
        var e = a.Entries.FirstOrDefault(x => x.Name.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
        return e is null ? null : Encoding.Latin1.GetString(a.Read(e));
    }

    private const string TerrainCon =
        "GeometryTemplate.create Terrain terrain\r\n" +
        "GeometryTemplate.worldSize 1024\r\n" +
        "GeometryTemplate.yScale 0.350000\r\n" +
        "GeometryTemplate.drawWaterBelowTerrain 1\r\n" +
        "GeometryTemplate.waterLevel 7.5\r\n" +
        "GeometryTemplate.waterBelowLevel -5.8\r\n";

    private static TerrainConfig Cfg(float below) => new()
    {
        MaterialSize = 512, WorldSize = 1024, YScale = 0.35f, WaterLevel = 7.5f,
        DrawWaterBelowTerrain = true, WaterBelowLevel = below, WriteWaterBelow = true,
    };

    [Fact]
    public void A_backup_copy_at_the_same_depth_does_not_win_over_the_Init_one()
    {
        // Both are four levels deep, so "shallowest" cannot separate them - the game reads Init/, per `run Init/Terrain`.
        var rfa = TempRfa(
            ("bfvietnam/levels/Saigon68/BACKUP_SAIGON_TERRAIN/Terrain.con", TerrainCon),
            ("bfvietnam/levels/Saigon68/Init/Terrain.con", TerrainCon));
        try
        {
            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, null, terrainConfig: Cfg(-12.25f));

            var a = new RefractorFlatArchive(rfa);
            string Body(string dir) => Encoding.Latin1.GetString(a.Read(
                a.Entries.First(e => e.Name.Replace('\\', '/').Contains(dir, StringComparison.OrdinalIgnoreCase))));

            Assert.Contains("waterBelowLevel -12.25", Body("/Init/"));
            Assert.Contains("waterBelowLevel -5.8", Body("BACKUP_SAIGON_TERRAIN"));   // left exactly as it was
        }
        finally { File.Delete(rfa); }
    }

    [Fact]
    public void Patching_matches_nothing_when_the_level_ships_no_Terrain_con()
    {
        // The behaviour that hid the bug, and the reason the write has to be an ADD.
        var rfa = TempRfa(("bfvietnam/levels/Saigon68/Init.con", "run Init/Terrain\r\n"));
        try
        {
            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, null, terrainConfig: Cfg(-12.25f));
            Assert.Null(ReadEntry(rfa, "Terrain.con"));
        }
        finally { File.Delete(rfa); }
    }

    [Fact]
    public void Adding_it_carries_the_water_levels_and_keeps_the_rest_of_the_layered_file()
    {
        var rfa = TempRfa(("bfvietnam/levels/Saigon68/Init.con", "run Init/Terrain\r\n"));
        try
        {
            // What the editor does: patch the lines it found through the mount chain, then ADD the result.
            var body = string.Join("\r\n", Cfg(-12.25f).PatchConLines(TerrainCon.Replace("\r\n", "\n").Split('\n'))) + "\r\n";
            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, null,
                newEntries: new[] { ("Init/Terrain.con", Encoding.Latin1.GetBytes(body)) });

            var written = ReadEntry(rfa, "Terrain.con");
            Assert.NotNull(written);
            Assert.Contains("waterBelowLevel -12.25", written);
            Assert.Contains("drawWaterBelowTerrain 1", written);
            Assert.Contains("GeometryTemplate.worldSize 1024", written);      // the rest of the file survived
            Assert.DoesNotContain("waterBelowLevel -5.8", written);
        }
        finally { File.Delete(rfa); }
    }

    [Fact]
    public void A_negative_tunnel_level_survives_the_round_trip()
    {
        // Saigon68's real value is NEGATIVE (-5.8 against a 7.5 surface); a formatter that lost the sign would put
        // the water back above the ground.
        var patched = Cfg(-12.25f).PatchConLines(TerrainCon.Replace("\r\n", "\n").Split('\n')).ToArray();
        var back = TerrainConfig.Parse(patched);
        Assert.Equal(-12.25f, back.WaterBelowLevel!.Value, 3);
        Assert.Equal(7.5f, back.WaterLevel, 3);
        Assert.True(back.DrawWaterBelowTerrain);
    }
}
