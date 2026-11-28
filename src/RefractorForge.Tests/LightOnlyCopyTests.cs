using System.Numerics;
using System.Text.RegularExpressions;
using RefractorForge.Formats.Con;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The shader and manifest side of a lightmap-only copy (<see cref="LightmapReady.LightOnly"/>): what the fourth
/// calibration level measured is that <c>selfillum 1 1 1</c> on a 40-byte mesh leaves texture x (lightmap +
/// LMambient), and that <c>lighting false</c> ignores the lightmap altogether.
/// </summary>
public class LightOnlyCopyTests
{
    private const string Rs =
        "subshader \"a_Material0\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\tmaterialDiffuse .5 .5 .5;\r\n\tselfillum .3 .3 .3;\r\n\ttexture \"texture/x\";\r\n}\r\n\r\n" +
        "subshader \"a_Material1\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\ttexture \"texture/y\";\r\n}\r\n\r\n" +
        "subshader \"a_Glow\" \"StandardMesh/Default\"\r\n{\r\n\tlighting false;\r\n\ttexture \"texture/z\";\r\n}\r\n";

    [Fact]
    public void A_copys_shader_gets_selfillum_and_keeps_everything_else()
    {
        string made = LightmapReady.LightOnlyShader(Rs, new LightmapReady.LightOnly(false, true));
        var set = RsShaderSet.Parse(made);
        Assert.Equal(Vector3.One, set.Materials["a_Material0"].SelfIllum);
        Assert.Equal(Vector3.One, set.Materials["a_Material0"].Diffuse);
        Assert.Equal("texture/x", set.Materials["a_Material0"].Texture);
        Assert.Equal(Vector3.One, set.Materials["a_Material1"].SelfIllum);
        Assert.Equal("texture/y", set.Materials["a_Material1"].Texture);
        // An unlit material is not touched: lighting false draws full white whatever the lightmap says (measured).
        Assert.Equal(Vector3.Zero, set.Materials["a_Glow"].SelfIllum);
        Assert.True(set.Materials["a_Glow"].Unlit);
        Assert.Equal(2, Regex.Matches(made, @"(?m)^\s*selfillum\b").Count);   // one per lit material, none doubled
        Assert.Equal(3, Regex.Matches(made, "subshader").Count);
    }

    [Fact]
    public void A_copys_shader_loses_its_specular_and_its_normal_map()
    {
        // With them on the game drew every copy near the camera full white (Operation_Flaming_Dart, 2026-11-28).
        const string retail =
            "subshader \"h_Material0\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\tlightingSpecular true;\r\n\tmaterialDiffuse 1 1 1;\r\n" +
            "\tmaterialSpecular 0.7 0.7 0.7;\r\n\thighEndPerPixel true;\r\n\ttexture \"texture/h\";\r\n\tnormalmap \"texture/h_n\";\r\n}\r\n";
        string made = LightmapReady.LightOnlyShader(retail, new LightmapReady.LightOnly(false, true));
        Assert.Matches(@"(?m)^\s*lightingSpecular false;", made);
        Assert.DoesNotMatch(@"(?m)^\s*normalmap\b", made);
        Assert.DoesNotMatch(@"(?m)^\s*highEndPerPixel\b", made);
        Assert.Matches(@"(?m)^\s*texture ""texture/h"";", made);
        Assert.Equal("texture/h", RsShaderSet.Parse(made).Materials["h_Material0"].Texture);
        // An older recipe's copies are told apart by the manifest tag, so the window offers to rebuild them.
        Assert.NotEqual("selfillum", new LightmapReady.LightOnly(false, true).Tag);
    }

    [Fact]
    public void Running_it_twice_changes_nothing_more()
    {
        var mode = new LightmapReady.LightOnly(false, true);
        string once = LightmapReady.LightOnlyShader(Rs, mode);
        Assert.Equal(once, LightmapReady.LightOnlyShader(once, mode));
    }

    [Fact]
    public void The_shader_file_reader_knows_selfillum()
    {
        var set = RsShaderSet.Parse(Rs);
        Assert.Equal(new Vector3(0.3f), set.Materials["a_Material0"].SelfIllum);
        Assert.Equal(Vector3.Zero, set.Materials["a_Material1"].SelfIllum);
    }

    [Fact]
    public void The_mode_rides_the_copies_manifest()
    {
        var only = LightmapReady.LightOnly.FromTag(new LightmapReady.LightOnly(false, true).Tag);
        Assert.NotNull(only);
        Assert.True(only!.SelfIllum);
        Assert.False(only.SteerUp);
        var m = LightmapReady.ReadManifest("rem rf-lightmap-ready option lightonly selfillum\nrem rf-lightmap-ready placed a a_lm\n");
        Assert.True(m.Only is { SelfIllum: true });
        Assert.Equal("a_lm", m.Placed["a"]);
        Assert.Null(LightmapReady.ReadManifest("rem rf-lightmap-ready placed a a_lm\n").Only);
    }

    [Fact]
    public void The_engine_model_draws_a_selfillum_copy_from_its_lightmap_alone()
    {
        var c = new EngineLight.Colours(new Vector3(0.3f, 0.2f, 0.2f), new Vector3(0.1f, 0.2f, 0.2f), new Vector3(0.15f), new Vector3(0.1f));
        var toSun = Vector3.Normalize(new Vector3(-0.75f, 0.59f, 0.3f));
        var copy = new EngineLight.Material(Vector3.One, Vector3.One, Vector3.One) { VertexLit = true };
        var lm = new Vector3(0.5f, 0.25f, 0.375f);
        foreach (var n in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, toSun, -toSun })
            Assert.Equal(lm + c.LmAmbient, EngineLight.Lightmapped(lm, n, toSun, c, copy));
        // The palm material as shipped, read off the third level: (0.5 x lights + 0.3) x lightmap + LMambient.
        var strong = new EngineLight.Colours(new Vector3(1.25f, 0.9f, 0.9f), new Vector3(0.5f, 0.9f, 0.9f), new Vector3(0.15f), new Vector3(0.1f));
        var palm = new EngineLight.Material(new Vector3(0.5f), new Vector3(0.4f), new Vector3(0.3f)) { VertexLit = true };
        var shown = EngineLight.Lightmapped(Vector3.One, Vector3.UnitY, new Vector3(-0.7479881f, 0.5925421f, 0.29901105f), strong, palm);
        Assert.InRange(shown.X * 255f, 233.5f, 236.5f);      // measured 235
        Assert.InRange(shown.Y * 255f, 236.5f, 239.5f);      // measured 238
        Assert.Equal(c.LmAmbient, EngineLight.Lightmapped(Vector3.Zero, Vector3.UnitY, toSun, c, palm));
    }
}
