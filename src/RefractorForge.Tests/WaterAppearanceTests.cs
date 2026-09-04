using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Two ways the water came out looking nothing like what was set in the editor, both found in game on al_vietnas.
///
/// The surface mirrored the wrong sky: there are two cubemaps in the game whose names are one letter apart, and the
/// base archive's own levelWater.rs names the one no level uses. Starting a level's override by copying that file
/// therefore reflected a sky the map does not have - invisible until the reflectivity was turned up.
///
/// And the tunnel water ignored its own colour: its block inherited the surface's shallowColor and depth scales, so a
/// bright surface overrode it. A brown, half-transparent sewer came out opaque orange. The two bodies are meant to be
/// edited apart, so the second one now writes its whole block from its own values.
/// </summary>
public class WaterAppearanceTests
{
    private static string Rs(string cubemap) =>
        "subshader \"WaterSetting\" \"StandardMesh/Default\"\r\n{\r\n" +
        "\tsequence \"texture/Waterseq/test\";\r\n" +
        $"\tcubemap \"{cubemap}\";\r\n" +
        "\topacity 0.35;\r\n\treflectivity 0.5;\r\n\twaterScale 25;\r\n}\r\n";

    [Fact]
    public void A_level_reflecting_the_base_archives_sky_is_given_the_one_levels_actually_use()
    {
        // What every retail level names, and what the base file names - one letter apart, two different cubemaps.
        Assert.NotEqual(WaterShader.BaseCubemap, WaterShader.LevelCubemap);

        var s = WaterShader.Parse(Rs(WaterShader.BaseCubemap), WaterShader.SurfaceSubshader);
        Assert.Equal(WaterShader.LevelCubemap, s.Cubemap);

        // and saving puts it in the file, so a level already carrying the wrong one is repaired
        var patched = WaterShader.Patch(Rs(WaterShader.BaseCubemap), s);
        Assert.Contains($"cubemap \"{WaterShader.LevelCubemap}\";", patched);
        Assert.DoesNotContain(WaterShader.BaseCubemap, patched);
    }

    [Fact]
    public void A_level_that_names_its_own_sky_keeps_it()
    {
        const string mine = "texture/my_own_env.rcm";
        var s = WaterShader.Parse(Rs(mine), WaterShader.SurfaceSubshader);
        Assert.Equal(mine, s.Cubemap);
        Assert.Contains($"cubemap \"{mine}\";", WaterShader.Patch(Rs(mine), s));
    }

    [Fact]
    public void A_tunnel_blocks_written_from_scratch_reflects_the_level_sky()
    {
        // The below-terrain block is appended when a tunnel map lacks it; it must not carry the base file's sky either.
        var text = WaterShader.Patch(Rs(WaterShader.LevelCubemap),
                                     WaterShaderSettings.RetailDefault,
                                     WaterShaderSettings.BelowTerrainDefault);
        Assert.True(WaterShader.HasBelowTerrain(text));
        var below = WaterShader.Parse(text, WaterShader.BelowTerrainSubshader);
        Assert.Equal(WaterShader.LevelCubemap, below.Cubemap);
    }

    // ---- the colours -------------------------------------------------------------------------------------------

    // A surface deliberately at odds with the tunnel water: bright orange, fully opaque, reaching full colour at once.
    private static readonly string[] InitCon =
    {
        "water.color 0.8/0.65/0.05",
        "water.shallowColor 1.0/0.501961/0.0",
        "water.deepColor 0/0/0",
        "water.waterColorDepth 10.0",
        "water.waterAlphaDepth 1.0",
        "water.waterShallowAlpha 1",
    };

    private static EnvironmentSettings BrownTunnelWater()
    {
        var e = EnvironmentSettings.Parse(null, null, InitCon);
        e.WriteWater = true;
        e.WriteWaterBelow = true;
        e.WaterBelowEnabled = true;
        e.BelowColor = new Vec3(0.30f, 0.19f, 0.08f);       // brown
        e.BelowDeepColor = new Vec3(0.12f, 0.08f, 0.03f);
        e.BelowAlpha = 0.45f;                                // and see-through
        e.BelowShallowColor = new Vec3(0.20f, 0.14f, 0.06f); // what shallow tunnel water shows - the game uses THIS
        return e;
    }

    [Fact]
    public void The_tunnel_water_does_not_inherit_the_surfaces_colour()
    {
        var lines = BrownTunnelWater().PatchInitConLines(InitCon);
        var below = lines.Where(l => l.StartsWith("waterBelowTerrain.", StringComparison.OrdinalIgnoreCase)).ToList();

        // the bug: the surface's orange shallowColor was copied onto the second body and won
        Assert.DoesNotContain(below, l => l.Contains("1.000000/0.501961/0.000000"));
        Assert.DoesNotContain(below, l => l.Contains("0.501961"));
        Assert.Contains(below, l => l.StartsWith("waterBelowTerrain.shallowColor", StringComparison.OrdinalIgnoreCase)
                                    && l.Contains("0.2") && l.Contains("0.14"));
    }

    [Fact]
    public void The_tunnel_water_keeps_the_transparency_it_was_given()
    {
        var lines = BrownTunnelWater().PatchInitConLines(InitCon);

        // the alpha itself...
        Assert.Contains(lines, l => l.StartsWith("waterBelowTerrain.waterShallowAlpha", StringComparison.OrdinalIgnoreCase)
                                    && l.Contains("0.45"));
        // ...and the depth scales that decide how fast it reaches full opacity, which must not be the surface's 1.0/10
        var alphaDepth = lines.Single(l => l.StartsWith("waterBelowTerrain.waterAlphaDepth", StringComparison.OrdinalIgnoreCase));
        var colorDepth = lines.Single(l => l.StartsWith("waterBelowTerrain.waterColorDepth", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("1.0", alphaDepth.Split(' ')[1]);
        Assert.DoesNotContain("10", colorDepth.Split(' ')[1]);
    }

    [Fact]
    public void Depth_scales_are_read_back_exactly_as_the_level_states_them()
    {
        // Saigon68 mirrors its surface's depth scales onto its tunnel water exactly AND gives the tunnel a colour
        // unlike its own shallowColor, so neither "same as the surface" nor "different from its colour" tells you
        // whether a level meant it. Nothing is inferred - the values are read, shown, and written back.
        var stated = InitCon.Concat(new[]
        {
            "waterBelowTerrain.color 0.3/0.19/0.08",
            "waterBelowTerrain.waterAlphaDepth 1.0",     // identical to the surface's, exactly as Saigon68 does it
            "waterBelowTerrain.waterColorDepth 10.0",
        }).ToArray();

        var e = EnvironmentSettings.Parse(null, null, stated);
        Assert.Equal(1.0f, e.BelowAlphaDepth, 3);
        Assert.Equal(10.0f, e.BelowColorDepth, 3);
    }

    [Fact]
    public void The_surfaces_own_colour_is_what_shallow_water_shows()
    {
        // It is what you see in shallow water, so a level whose shallowColor disagreed with its colour rendered
        // nothing like the viewport - and the editor did not show it, so there was no way to tell why. Now it is
        // read, exposed, and written back.
        var e = EnvironmentSettings.Parse(null, null, InitCon);
        Assert.True(e.HasShallowColor);
        Assert.Equal(1.0f, e.ShallowColor.X, 3);          // the orange that was painting this level's water
        Assert.Equal(0.501961f, e.ShallowColor.Y, 3);

        e.WriteWater = true;
        e.ShallowColor = new Vec3(0.10f, 0.22f, 0.30f);   // as the panel's colour picker sets it
        var lines = e.PatchInitConLines(InitCon);

        var shallow = lines.Single(l => l.StartsWith("water.shallowColor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("0.1", shallow);
        Assert.DoesNotContain("0.501961", shallow);
        Assert.Equal(1, lines.Count(l => l.StartsWith("water.shallowColor", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void A_body_that_names_no_shallow_colour_takes_its_own_colour()
    {
        var bare = new[] { "water.color 0.2/0.3/0.25" };
        var e = EnvironmentSettings.Parse(null, null, bare);
        Assert.False(e.HasShallowColor);
        Assert.Equal(0.2f, e.ShallowColor.X, 3);
        Assert.Equal(0.25f, e.ShallowColor.Z, 3);
    }

    [Fact]
    public void What_is_written_is_what_the_editor_reads_back()
    {
        var lines = BrownTunnelWater().PatchInitConLines(InitCon);
        var back = EnvironmentSettings.Parse(null, null, lines);

        Assert.True(back.HasBelowColors);
        Assert.Equal(0.30f, back.BelowColor.X, 3);
        Assert.Equal(0.19f, back.BelowColor.Y, 3);
        Assert.Equal(0.45f, back.BelowAlpha, 3);
        Assert.Equal(0.12f, back.BelowDeepColor.X, 3);
        // and the surface is still the surface, its own colour untouched - the two bodies did not merge
        Assert.Equal(0.80f, back.WaterColor.X, 3);
        Assert.Equal(0.65f, back.WaterColor.Y, 3);

        // saving twice does not stack a second copy of the block
        var again = back.PatchInitConLines(lines);
        Assert.Equal(1, again.Count(l => l.StartsWith("waterBelowTerrain.color", StringComparison.OrdinalIgnoreCase)));
    }
}
