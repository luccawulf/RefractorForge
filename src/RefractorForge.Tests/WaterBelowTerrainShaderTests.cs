using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A level with <c>GeometryTemplate.drawWaterBelowTerrain 1</c> must ship <c>StandardMesh/levelWater.rs</c> carrying a
/// <c>WaterSettingBelowTerrain</c> subshader. The base archive has only <c>WaterSetting</c>, so without it the engine
/// asserts in WaterPatch.cpp and the level crashes as it creates the terrain object - which is exactly what happened
/// on the first in-game test of the tunnel water.
/// </summary>
public class WaterBelowTerrainShaderTests
{
    // The base game's standardMesh/levelWater.rs, byte for byte: one subshader, and no below-terrain block.
    private const string BaseFile =
        "subshader \"WaterSetting\" \"StandardMesh/Default\"\r\n{\r\n\tsequence \"texture/Waterseq/test\";\r\n" +
        "\tcubemap \"texture/default_env.rcm\";\r\n\tsequenceCycleTime 2;\r\n\tsequenceFrameCnt 30;\r\n\topacity 0.35;\r\n" +
        "\tmaterialDiffuse .281 .266 .205;\r\n\treflectivity 0.20;\r\n\tuvSpeed 0 0 1;\r\n\twaterScale 25;\r\n\twaterFade 0;\r\n}\r\n";

    // Saigon68's, the one retail level that has the pair.
    private const string Saigon68 =
        "subshader \"WaterSetting\" \"StandardMesh/Default\"\r\n{\r\n\tsequence \"texture/Waterseq/test\";\r\n" +
        "\tcubemap \"texture/env_default.rcm\";\r\n\tsequenceCycleTime 2;\r\n\tsequenceFrameCnt 30;\r\n\topacity 0.75;\r\n" +
        "\tmaterialDiffuse 1 1 1;\r\n\treflectivity 0.6;\r\n\tuvSpeed 0 0 .2;\r\n\twaterScale 30;\r\n\twaterFade 0;\r\n}\r\n\r\n" +
        "subshader \"WaterSettingBelowTerrain\" \"StandardMesh/Default\"\r\n{\r\n\tsequence \"texture/Waterseq/test\";\r\n" +
        "\tcubemap \"texture/env_default.rcm\";\r\n\tsequenceCycleTime 2;\r\n\tsequenceFrameCnt 30;\r\n\topacity 0.85;\r\n" +
        "\tmaterialDiffuse 1 1 1;\r\n\treflectivity 0.1;\r\n\tuvSpeed 0 0 .05;\r\n\twaterScale 30;\r\n\twaterFade 0;\r\n}\r\n";

    [Fact]
    public void The_base_game_file_has_no_below_terrain_block_which_is_why_the_level_crashed()
    {
        Assert.False(WaterShader.HasBelowTerrain(BaseFile));
        Assert.False(WaterShader.HasBelowTerrain(null));
        Assert.True(WaterShader.HasBelowTerrain(Saigon68));
    }

    [Fact]
    public void Asking_for_tunnel_water_appends_the_missing_subshader_to_the_base_file()
    {
        var text = WaterShader.Patch(BaseFile, WaterShaderSettings.RetailDefault, WaterShaderSettings.BelowTerrainDefault);
        Assert.True(WaterShader.HasBelowTerrain(text));
        Assert.Contains("subshader \"WaterSettingBelowTerrain\" \"StandardMesh/Default\"", text);

        var below = WaterShader.Parse(text, WaterShader.BelowTerrainSubshader);
        Assert.Equal(0.10f, below.Reflectivity, 3);      // Saigon68's values
        Assert.Equal(0.85f, below.Opacity, 3);
        Assert.Equal(0.05f, below.ScrollSpeed, 3);
        Assert.Equal(30f, below.WaterScale, 3);

        // The surface block is untouched, and still exactly one of each.
        Assert.Equal(0.20f, WaterShader.Parse(text).Reflectivity, 3);
        Assert.Equal(1, Count(text, "subshader \"WaterSetting\" "));
        Assert.Equal(1, Count(text, "subshader \"WaterSettingBelowTerrain\""));
    }

    [Fact]
    public void The_two_blocks_are_told_apart_by_name_not_by_prefix()
    {
        // "WaterSetting" is a prefix of "WaterSettingBelowTerrain": reading or writing one must never hit the other.
        Assert.Equal(0.6f, WaterShader.Parse(Saigon68).Reflectivity, 3);
        Assert.Equal(0.1f, WaterShader.Parse(Saigon68, WaterShader.BelowTerrainSubshader).Reflectivity, 3);

        // As the editor does it: read the file's own values, move only the two the sliders touch.
        var text = WaterShader.Patch(Saigon68,
            WaterShader.Parse(Saigon68) with { Reflectivity = 0.42f, Opacity = 0.5f },
            WaterShader.Parse(Saigon68, WaterShader.BelowTerrainSubshader) with { Reflectivity = 0.33f, Opacity = 0.9f });
        Assert.Equal(0.42f, WaterShader.Parse(text).Reflectivity, 3);
        Assert.Equal(0.5f, WaterShader.Parse(text).Opacity, 3);
        Assert.Equal(0.33f, WaterShader.Parse(text, WaterShader.BelowTerrainSubshader).Reflectivity, 3);
        Assert.Equal(0.9f, WaterShader.Parse(text, WaterShader.BelowTerrainSubshader).Opacity, 3);
        Assert.Equal(1, Count(text, "subshader \"WaterSettingBelowTerrain\""));
        // The author's drift is kept for each body (re-emitted in our own number format: .2 -> 0.2).
        Assert.Contains("\tuvSpeed 0 0 0.2;", text);
        Assert.Contains("\tuvSpeed 0 0 0.05;", text);
        Assert.Equal(0.2f, WaterShader.Parse(text).ScrollSpeed, 3);
        Assert.Equal(0.05f, WaterShader.Parse(text, WaterShader.BelowTerrainSubshader).ScrollSpeed, 3);
    }

    [Fact]
    public void A_surface_only_edit_never_removes_a_below_terrain_block_the_level_already_had()
    {
        var text = WaterShader.Patch(Saigon68, WaterShaderSettings.RetailDefault with { Reflectivity = 0.5f });
        Assert.True(WaterShader.HasBelowTerrain(text));
        Assert.Equal(0.1f, WaterShader.Parse(text, WaterShader.BelowTerrainSubshader).Reflectivity, 3);
    }

    [Fact]
    public void A_level_with_no_override_at_all_still_gets_both_blocks()
    {
        var text = WaterShader.Patch(null, WaterShaderSettings.RetailDefault, WaterShaderSettings.BelowTerrainDefault);
        Assert.True(WaterShader.HasBelowTerrain(text));
        Assert.Contains("sequence \"texture/Waterseq/test\";", text);
        Assert.Equal(0.85f, WaterShader.Parse(text, WaterShader.BelowTerrainSubshader).Opacity, 3);
        // Idempotent: patching the result again does not add a second copy.
        var again = WaterShader.Patch(text, WaterShaderSettings.RetailDefault, WaterShaderSettings.BelowTerrainDefault);
        Assert.Equal(1, Count(again, "subshader \"WaterSettingBelowTerrain\""));
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
