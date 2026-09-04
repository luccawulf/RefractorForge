using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A map's two water bodies are edited apart and both have to reach the game: the surface through <c>water.*</c> in
/// Init.con (which the editor never used to write at all, so its colours stayed a viewport-only setting), the second
/// one through <c>waterBelowTerrain.*</c>.
/// </summary>
public class WaterBodiesTests
{
    private static readonly string[] Init =
    {
        "run Init/Terrain",
        "water.shallowcolor .1/.1/.1",
        "water.color .2/.3/.25",
        "water.deepcolor .15/.2/.15",
        "water.waterAlphaDepth .4",
        "water.waterShallowAlpha .325",
        "water.watercolordepth 1.125",
        "run Sounds/Environment",
    };

    [Fact]
    public void The_surface_colours_are_read_and_written_back()
    {
        var e = EnvironmentSettings.Parse(null, null, Init);
        Assert.Equal(0.2f, e.WaterColor.X, 3);
        Assert.Equal(0.15f, e.DeepColor.X, 3);
        Assert.Equal(0.325f, e.WaterAlpha, 3);

        // Untouched, the file is unchanged - an editor that writes nothing must not rewrite anything either.
        Assert.Equal(Init, e.PatchInitConLines(Init));

        e.WaterColor = new Vec3(0.5f, 0.4f, 0.3f);
        e.DeepColor = new Vec3(0.1f, 0.2f, 0.3f);
        e.WaterAlpha = 0.8f;
        e.WriteWater = true;
        var outLines = e.PatchInitConLines(Init);
        Assert.Contains("water.color 0.5/0.4/0.3", outLines);
        Assert.Contains("water.deepColor 0.1/0.2/0.3", outLines);
        Assert.Contains("water.waterShallowAlpha 0.8", outLines);
        // shallowColor is the level's until the editor is given one - it is a real setting, not a copy of the colour
        // (Saigon68 sets the two differently on purpose), and it is now shown in the panel rather than hidden
        Assert.Contains("water.shallowColor 0.1/0.1/0.1", outLines);   // the level's own value, re-emitted in the editor's number format
        Assert.Equal(0.1f, EnvironmentSettings.Parse(null, null, outLines).ShallowColor.X, 3);
        // and the keys it does not own are still left alone
        Assert.Contains("water.watercolordepth 1.125", outLines);
        Assert.Equal(0.5f, EnvironmentSettings.Parse(null, null, outLines).WaterColor.X, 3);
    }

    [Fact]
    public void The_second_body_keeps_its_own_colours_not_a_copy_of_the_surface()
    {
        var e = EnvironmentSettings.Parse(null, null, Init);
        Assert.False(e.WaterBelowEnabled);
        e.SeedBelowWaterFromSurface();                      // first time on: start from the river
        Assert.Equal(e.WaterColor.X, e.BelowColor.X, 3);

        e.BelowColor = new Vec3(0.05f, 0.06f, 0.04f);       // then diverge - a sewer is not a river
        e.BelowDeepColor = new Vec3(0.02f, 0.03f, 0.02f);
        e.BelowAlpha = 0.9f;
        e.WriteWaterBelow = true; e.WaterBelowEnabled = true;
        var outLines = e.PatchInitConLines(Init);
        Assert.Contains("waterBelowTerrain.color 0.05/0.06/0.04", outLines);
        Assert.Contains("waterBelowTerrain.deepColor 0.02/0.03/0.02", outLines);
        Assert.Contains("waterBelowTerrain.waterShallowAlpha 0.9", outLines);
        // and it owns its whole block, including the shallowColor the game actually paints - taking THAT from the
        // surface is what made a brown, half-transparent sewer come out opaque orange in game
        Assert.Contains(outLines, l => l.StartsWith("waterBelowTerrain.shallowColor "));
        Assert.Contains(outLines, l => l.StartsWith("waterBelowTerrain.waterAlphaDepth "));
        e.BelowShallowColor = new Vec3(0.05f, 0.06f, 0.04f);
        Assert.Contains("waterBelowTerrain.shallowColor 0.05/0.06/0.04", e.PatchInitConLines(Init));
        // the surface is untouched by a below-water edit
        Assert.Contains("water.color .2/.3/.25", outLines);

        var back = EnvironmentSettings.Parse(null, null, outLines);
        Assert.True(back.WaterBelowEnabled);
        Assert.True(back.HasBelowColors);
        Assert.Equal(0.05f, back.BelowColor.X, 3);
        Assert.Equal(0.9f, back.BelowAlpha, 3);
    }

    [Fact]
    public void An_authors_own_below_colours_are_never_re_seeded_from_the_surface()
    {
        var e = EnvironmentSettings.Parse(null, null, Init.Append("waterBelowTerrain.color .9/.8/.7").ToArray());
        Assert.True(e.HasBelowColors);
        e.SeedBelowWaterFromSurface();
        Assert.Equal(0.9f, e.BelowColor.X, 3);      // still theirs
    }

    [Fact]
    public void Switching_the_second_body_off_takes_its_block_out_again()
    {
        var e = EnvironmentSettings.Parse(null, null, Init);
        e.WriteWaterBelow = true; e.WaterBelowEnabled = true;
        var on = e.PatchInitConLines(Init);
        Assert.Contains(on, l => l.StartsWith("waterBelowTerrain."));
        e.WaterBelowEnabled = false;
        Assert.Equal(Init, e.PatchInitConLines(on));
    }

    [Fact]
    public void The_water_animation_sequence_is_read_from_the_shader()
    {
        // What the base archive names, and what the viewport plays.
        Assert.Equal("texture/Waterseq/test", WaterShader.SequenceOf(null));
        Assert.Equal("texture/Waterseq/test", WaterShader.SequenceOf(WaterShader.RetailText));
        var custom = WaterShader.RetailText.Replace("texture/Waterseq/test", "texture/Waterseq/mine");
        Assert.Equal("texture/Waterseq/mine", WaterShader.SequenceOf(custom));
        Assert.Null(WaterShader.SequenceOf("subshader \"WaterSetting\" \"x\"\n{\n\topacity 1;\n}\n"));
    }
}
