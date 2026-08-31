using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>The Init.con lighting round-trip. Init.con carries real gameplay next to the four renderer light
/// lines, so the patcher has to rewrite those lines and leave the entire rest of the file alone - these check
/// exactly that, on the shape both shipped games actually use.</summary>
public class LightSettingsTests
{
    // Operation_Irving's Init.con: globalAmbientColor is deliberately commented out, and there is gameplay above
    // and below the renderer block that must survive untouched.
    static readonly string[] Bfv =
    {
        "rem *** Operation Irving ***",
        "rem",
        "rem renderer.globalAmbientColor .2/.2/.2",
        "game.setDefaultGameMode GPM_CQ",
        "renderer.diffuseColor .975/1/.95",
        "renderer.ambientColor .1/.1/.1",
        "renderer.SecondaryDiffuseColor .55/.55/.525",
        "renderer.LMambientColor .25",
        "renderer.specularColor .9/.9/.7",
        "renderer.standardmeshminintensity 0.4",
        "",
        "run Init/SkyAndSun",
        "run Init/Terrain",
        "water.color .281/.266/.205",
    };

    [Fact]
    public void Parses_the_four_light_colours_and_ignores_commented_out_keys()
    {
        var e = EnvironmentSettings.Parse(null, null, Bfv);

        Assert.True(e.HasDiffuse);
        Assert.Equal(0.975f, e.DiffuseColor.X, 4);
        Assert.Equal(1f, e.DiffuseColor.Y, 4);
        Assert.Equal(0.95f, e.DiffuseColor.Z, 4);

        Assert.True(e.HasAmbient);
        Assert.Equal(0.1f, e.AmbientColor.X, 4);

        Assert.True(e.HasSpecular);
        Assert.Equal(0.9f, e.SpecularColor.X, 4);
        Assert.Equal(0.7f, e.SpecularColor.Z, 4);

        // A rem'd line is a comment, not a setting.
        Assert.False(e.HasGlobalAmbient);
    }

    [Fact]
    public void Patch_rewrites_only_the_light_lines_and_leaves_everything_else_byte_identical()
    {
        var e = EnvironmentSettings.Parse(null, null, Bfv);
        e.DiffuseColor = new RefractorForge.Formats.Geometry.Vec3(0.5f, 0.4f, 0.3f);

        var outLines = e.PatchInitConLines(Bfv);

        Assert.Equal(Bfv.Length, outLines.Count);                       // nothing added, nothing dropped
        Assert.Equal("renderer.diffuseColor 0.5/0.4/0.3", outLines[4]);
        Assert.Equal("rem renderer.globalAmbientColor .2/.2/.2", outLines[2]);   // the comment is untouched

        // Every non-light line survives verbatim - including the renderer keys we do not manage.
        for (int i = 0; i < Bfv.Length; i++)
        {
            if (i is 4 or 5 or 8) continue;   // diffuse / ambient / specular are the managed ones
            Assert.Equal(Bfv[i], outLines[i]);
        }
    }

    [Fact]
    public void A_key_the_level_never_declared_is_added_only_once_the_editor_owns_it()
    {
        var e = EnvironmentSettings.Parse(null, null, Bfv);

        // Untouched: the editor must not invent a globalAmbientColor for a level that shipped without one.
        Assert.DoesNotContain(e.PatchInitConLines(Bfv), l => l.StartsWith("renderer.globalAmbientColor"));

        // The user sets it -> exactly one line appears, and it lands inside the renderer block rather than at EOF.
        e.GlobalAmbientColor = new RefractorForge.Formats.Geometry.Vec3(0.2f, 0.2f, 0.25f);
        e.HasGlobalAmbient = true;
        var outLines = e.PatchInitConLines(Bfv);

        Assert.Equal(Bfv.Length + 1, outLines.Count);
        int at = outLines.FindIndex(l => l.StartsWith("renderer.globalAmbientColor"));
        Assert.Equal("renderer.globalAmbientColor 0.2/0.2/0.25", outLines[at]);
        Assert.True(at < outLines.FindIndex(l => l.StartsWith("run Init/SkyAndSun")));
    }

    [Fact]
    public void Patched_output_reparses_to_the_same_values()
    {
        var e = EnvironmentSettings.Parse(null, null, Bfv);
        e.GlobalAmbientColor = new RefractorForge.Formats.Geometry.Vec3(0.16f, 0.15f, 0.17f);
        e.AmbientColor = new RefractorForge.Formats.Geometry.Vec3(0.12f, 0.1f, 0.08f);
        e.DiffuseColor = new RefractorForge.Formats.Geometry.Vec3(0.5f, 0.47f, 0.4f);
        e.SpecularColor = new RefractorForge.Formats.Geometry.Vec3(0.3f, 0.3f, 0.3f);
        e.HasGlobalAmbient = true;

        var round = EnvironmentSettings.Parse(null, null, e.PatchInitConLines(Bfv));

        Assert.Equal(0.16f, round.GlobalAmbientColor.X, 4);
        Assert.Equal(0.12f, round.AmbientColor.X, 4);
        Assert.Equal(0.47f, round.DiffuseColor.Y, 4);
        Assert.Equal(0.3f, round.SpecularColor.Z, 4);

        // Re-patching a file we already wrote is a no-op.
        Assert.Equal(e.PatchInitConLines(Bfv), round.PatchInitConLines(e.PatchInitConLines(Bfv)));
    }

    [Fact]
    public void A_level_with_no_renderer_block_at_all_gets_the_lines_at_the_top()
    {
        var bare = new[] { "rem a bare Init.con", "run Init/Terrain" };
        var e = new EnvironmentSettings { HasDiffuse = true };

        var outLines = e.PatchInitConLines(bare);

        Assert.Equal("renderer.diffuseColor 0.975/1/0.95", outLines[0]);
        Assert.Equal(bare[0], outLines[1]);
        Assert.Equal(bare[1], outLines[2]);
    }
}
