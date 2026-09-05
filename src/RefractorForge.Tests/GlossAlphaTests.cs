using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The assault Huey rendered nearly invisible while the transport was fine. The two share an identical glass
/// material, so the shader was not the difference - the assault carries a second mesh, <c>ve_huey_cockpit</c>, whose
/// interior materials declare <c>glossMapInAlphaDiffuse true</c>. That flag says the texture's alpha channel is a
/// SPECULAR GLOSS MASK; the texture is called <c>ve_hueycockpit</c>, which tripped the "cockpit" rule in the cutout
/// name list, and the gloss mask was then read as coverage - so the inside of the aircraft, and with it the
/// aircraft, faded out. 398 of BFVietnam's 1994 shaders set the flag; BF1942's older ones never do.
/// </summary>
public class GlossAlphaTests
{
    // Trimmed from standardMesh/ve_huey_cockpit_m1.rs, keeping the shape exactly: comments, casing and all.
    private const string CockpitRs = """
subshader "ve_huey_cockpit_m1_Material0" "StandardMesh/Default"
{
	lighting true;
	materialDiffuse 0.2 0.2 0.2;
	glossmapinalphadiffuse true;
	texture "texture/ve_hueycockpit";
}

subshader "ve_huey_cockpit_m1_Material2" "StandardMesh/Default"
{
	lighting true;
	transparent true;
	Opacity 0.2;
	sortedBlend true;
	depthWrite false;
	reflectivity 0.8;
	cubemap "texture/env_default.rcm";
	texture "texture/ve_cockpitwindow";
}
""";

    [Fact]
    public void The_gloss_flag_is_read()
    {
        var set = RsShaderSet.Parse(CockpitRs);
        var interior = set.Materials["ve_huey_cockpit_m1_Material0"];
        Assert.True(interior.GlossInAlpha);
        Assert.False(interior.Transparent);
        Assert.True(interior.DepthWrite);          // an opaque material writes depth
    }

    [Fact]
    public void Glass_carries_its_authored_opacity_and_its_depth_write_setting()
    {
        var glass = RsShaderSet.Parse(CockpitRs).Materials["ve_huey_cockpit_m1_Material2"];
        Assert.True(glass.Transparent);
        Assert.False(glass.GlossInAlpha);
        Assert.False(glass.DepthWrite);
        Assert.Equal(0.2f, glass.Opacity!.Value, 3);
    }

    [Fact]
    public void An_opacity_with_a_trailing_comment_still_parses()
    {
        // The assault fuselage writes: Opacity .2;//minimun opacity
        var set = RsShaderSet.Parse("""
subshader "m" "StandardMesh/Default"
{
	transparent true;
	Opacity .2;//minimun opacity
	texture "texture/Ve_Hueyatk_win";
}
""");
        Assert.Equal(0.2f, set.Materials["m"].Opacity!.Value, 3);
    }

    [Fact]
    public void A_material_that_says_nothing_keeps_the_old_defaults()
    {
        // BF1942's shaders never mention any of these, and its behaviour must not shift under them.
        var plain = RsShaderSet.Parse("""
subshader "m" "StandardMesh/Default"
{
	lighting true;
	texture "texture/willy3_z";
}
""").Materials["m"];
        Assert.False(plain.GlossInAlpha);
        Assert.True(plain.DepthWrite);
        Assert.Null(plain.Opacity);
        Assert.False(plain.Transparent);
        Assert.Null(plain.AlphaTestRef);
    }
}
