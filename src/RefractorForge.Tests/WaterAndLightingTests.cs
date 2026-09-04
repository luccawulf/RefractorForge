using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The second water body of a tunnel map, the lightmap ambient the engine adds, the water shader's reflectivity,
/// and a video decal - each pinned to the retail file that proves the shape.
/// </summary>
public class WaterAndLightingTests
{
    // Saigon68's Init/Terrain.con, the one retail-shipped level that declares the tunnel water.
    private static readonly string[] Saigon68Terrain =
    {
        "GeometryTemplate.create patchTerrain terrainGeometry",
        "GeometryTemplate.worldSize 1024",
        "GeometryTemplate.yScale 0.350000",
        "rem ",
        "GeometryTemplate.drawWaterBelowTerrain 1",
        "GeometryTemplate.waterLevel 7.5",
        "GeometryTemplate.waterBelowLevel -7.1000",
        "GeometryTemplate.seaFloorLevel 0",
        "GeometryTemplate.waveHeight 1",      // the patcher has always re-emitted the three water numbers in its own format
    };

    [Fact]
    public void Saigon68_declares_a_second_water_level_below_the_terrain()
    {
        var cfg = TerrainConfig.Parse(Saigon68Terrain);
        Assert.True(cfg.DrawWaterBelowTerrain);
        Assert.Equal(-7.1f, cfg.WaterBelowLevel!.Value, 3);
        Assert.Equal(7.5f, cfg.WaterLevel, 3);
        // Untouched, the patcher hands the file back as it was.
        Assert.Equal(Saigon68Terrain, cfg.PatchConLines(Saigon68Terrain));
    }

    [Fact]
    public void Switching_tunnel_water_on_adds_the_pair_after_waterLevel_and_off_removes_it()
    {
        string[] plain = { "GeometryTemplate.worldSize 1024", "GeometryTemplate.waterLevel 20", "GeometryTemplate.seaFloorLevel 0" };
        var cfg = TerrainConfig.Parse(plain);
        Assert.False(cfg.DrawWaterBelowTerrain);
        cfg.DrawWaterBelowTerrain = true; cfg.WaterBelowLevel = 2.5f; cfg.WriteWaterBelow = true;
        var on = cfg.PatchConLines(plain).ToArray();
        Assert.Equal(new[]
        {
            "GeometryTemplate.worldSize 1024",
            "GeometryTemplate.waterLevel 20",
            "GeometryTemplate.drawWaterBelowTerrain 1",
            "GeometryTemplate.waterBelowLevel 2.5",
            "GeometryTemplate.seaFloorLevel 0",
        }, on);
        Assert.Equal(on, cfg.PatchConLines(on).ToArray());              // idempotent

        cfg.DrawWaterBelowTerrain = false;
        Assert.Equal(plain, cfg.PatchConLines(on).ToArray());            // and gone again
    }

    [Fact]
    public void The_below_terrain_water_colours_mirror_the_surface_water_the_way_Saigon68_ships_them()
    {
        string[] init =
        {
            "run Init/Terrain",
            "water.shallowColor 0.2/.1/.01",
            "water.deepColor 0.5/.3/.01",
            "water.waterAlphaDepth 0.400000",
            "water.texLayer1 texture/water01",          // a BF1942-style line: not a colour, never mirrored
            "run Sounds/Environment",
        };
        var e = EnvironmentSettings.Parse(null, null, init);
        Assert.False(e.WaterBelowEnabled);
        e.WriteWaterBelow = true; e.WaterBelowEnabled = true;
        var outLines = e.PatchInitConLines(init);
        Assert.Equal(new[]
        {
            "run Init/Terrain",
            "water.shallowColor 0.2/.1/.01",
            "water.deepColor 0.5/.3/.01",
            "water.waterAlphaDepth 0.400000",
            "waterBelowTerrain.shallowColor 0.2/.1/.01",
            "waterBelowTerrain.deepColor 0.5/.3/.01",
            "waterBelowTerrain.waterAlphaDepth 0.400000",
            "water.texLayer1 texture/water01",
            "run Sounds/Environment",
        }, outLines);
        Assert.True(EnvironmentSettings.Parse(null, null, outLines).WaterBelowEnabled);

        e.WaterBelowEnabled = false;
        Assert.Equal(init, e.PatchInitConLines(outLines));
    }

    [Fact]
    public void LMambientColor_parses_in_both_its_shapes()
    {
        var one = EnvironmentSettings.Parse(null, null, new[] { "renderer.LMambientColor .2" });
        Assert.True(one.HasLMAmbient);
        Assert.Equal(0.2f, one.LMAmbientColor.X, 3); Assert.Equal(0.2f, one.LMAmbientColor.Z, 3);
        var three = EnvironmentSettings.Parse(null, null, new[] { "renderer.LMambientColor .25/.25/.25" });
        Assert.Equal(0.25f, three.LMAmbientColor.Y, 3);
        Assert.Equal(0.25f, EnvironmentSettings.Parse(null, null, Array.Empty<string>()).LMAmbientColor.X, 3);   // the retail default
    }

    // ---- the water shader ----

    private const string HoChiMinhTrail =
        "subshader \"WaterSetting\" \"StandardMesh/Default\"\r\n{\r\n\tsequence \"texture/Waterseq/test\";\r\n" +
        "\tcubemap \"texture/env_default.rcm\";\r\n\tsequenceCycleTime 2;\r\n\tsequenceFrameCnt 30;\r\n\topacity 0.5;\r\n" +
        "\tmaterialDiffuse 1 1 1;\r\n\treflectivity .3;\r\n\tuvSpeed 0 0 0.15;\r\n\twaterScale 25;\r\n\twaterFade 0;\r\n}\r\n";

    [Fact]
    public void Reflectivity_reads_out_of_a_retail_override_and_writes_back_in_place()
    {
        var s = WaterShader.Parse(HoChiMinhTrail);
        Assert.Equal(0.3f, s.Reflectivity, 3);
        Assert.Equal(0.5f, s.Opacity, 3);
        Assert.Equal(0.15f, s.ScrollSpeed, 3);
        Assert.Equal(1f, s.Diffuse.X, 3);

        var patched = WaterShader.Patch(HoChiMinhTrail, s with { Reflectivity = 0.6f, Opacity = 0.9f });
        Assert.Contains("\treflectivity 0.6;", patched);
        Assert.Contains("\topacity 0.9;", patched);
        Assert.Contains("\tcubemap \"texture/env_default.rcm\";", patched);   // everything else untouched
        Assert.Contains("\tuvSpeed 0 0 0.15;", patched);
        Assert.Equal(0.6f, WaterShader.Parse(patched).Reflectivity, 3);
    }

    [Fact]
    public void A_level_with_no_override_starts_from_the_base_shader_and_a_trimmed_one_is_completed()
    {
        Assert.Equal(0.2f, WaterShader.Parse(null).Reflectivity, 3);
        var fromBase = WaterShader.Patch(null, WaterShaderSettings.RetailDefault with { Reflectivity = 0.45f });
        Assert.Contains("\treflectivity 0.45;", fromBase);
        Assert.Contains("sequence \"texture/Waterseq/test\";", fromBase);

        const string trimmed = "subshader \"WaterSetting\" \"StandardMesh/Default\"\n{\n\topacity 0.5;\n}\n";
        var completed = WaterShader.Patch(trimmed, WaterShaderSettings.RetailDefault with { Reflectivity = 0.33f });
        Assert.Contains("\treflectivity 0.33;", completed);
        Assert.EndsWith("}\n", completed);
        Assert.Equal(0.33f, WaterShader.Parse(completed).Reflectivity, 3);
    }

    // ---- a video decal ----

    [Fact]
    public void A_video_decal_points_its_shader_at_the_movie_and_ships_no_picture()
    {
        var b = DecalObject.Build("Test_Map", "screen", 4f, 3f, "decal_screen", null, textureRef: "Mods/echo/Movies/intro.bik", baseSub: "BfVietnam");
        Assert.DoesNotContain(b.Files, f => f.RelPath.StartsWith("Texture/"));
        var rs = Encoding.Latin1.GetString(b.Files.First(f => f.RelPath.EndsWith(".rs")).Bytes);
        Assert.Contains("texture \"Mods/echo/Movies/intro.bik\";", rs);
        Assert.Equal(5, b.Files.Count);
        // And the ordinary form is unchanged.
        var pic = DecalObject.Build("Test_Map", "poster", 2f, 1f, "decal_poster", new byte[128], baseSub: "BfVietnam");
        Assert.Contains(pic.Files, f => f.RelPath == "Texture/decal_poster.dds");
        Assert.Contains("texture \"texture/decal_poster\";", Encoding.Latin1.GetString(pic.Files.First(f => f.RelPath.EndsWith(".rs")).Bytes));
    }
}
