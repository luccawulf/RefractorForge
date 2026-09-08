using System;
using System.IO;
using System.Linq;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Baking the procedural forest into ordinary static objects, so a published map does not depend on which
/// executable the player runs. How much overgrowth the engine plants is an .exe constant, not map data - the stock
/// BfVietnam.exe and the BfVietnam_Veg_* builds plant wildly different amounts - so the only forest every client
/// sees the same way is one made of real objects in the archive.
/// </summary>
public class OvergrowthToStaticTests
{
    // --- the _m2 impostor -> _M1 static template mapping the capture tool established -----------------------------
    [Theory]
    [InlineData("c05f_trees_m2", "C05F_Trees_M1")]
    [InlineData("c03f_trees_m2", "C03F_Trees_M1")]
    [InlineData("c07f_jungle_m2", "C07F_Jungle_M1")]
    [InlineData("c02f_trees_m2", "C02F_Trees_M1")]
    [InlineData("C05F_Trees_M1", "C05F_Trees_M1")]           // already a template: unchanged
    public void Geometry_maps_to_its_static_template(string geom, string expected)
        => Assert.Equal(expected, OvergrowthCapture.StaticTemplateFor(geom));

    [Fact]
    public void An_odd_name_survives_the_mapping()
    {
        Assert.Equal("", OvergrowthCapture.StaticTemplateFor(""));
        Assert.Equal("Palm", OvergrowthCapture.StaticTemplateFor("palm"));
    }

    // --- the captured dump format -------------------------------------------------------------------------------
    private static byte[] Dump(params (int type, float x, float y, float z, float m0, float m2)[] recs)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(recs.Length);
        foreach (var r in recs)
        {
            w.Write(r.type);
            var fl = new float[23];
            fl[0] = r.m0; fl[2] = r.m2;                       // rotation basis: yaw = atan2(fl2, fl0)
            fl[12] = r.x; fl[13] = r.y; fl[14] = r.z;         // row-major 4x4 translation
            fl[16] = 0.83f;                                   // uniform scale
            foreach (var f in fl) w.Write(f);
        }
        return ms.ToArray();
    }

    [Fact]
    public void A_dump_decodes_to_positions_yaw_and_scale()
    {
        var trees = OvergrowthCapture.LoadDump(Dump((3, 512.5f, 31.25f, 1000f, 1f, 0f),
                                                    (0, 10f, 2f, 20f, 0f, 1f)));
        Assert.Equal(2, trees.Count);
        Assert.Equal(3, trees[0].Type);
        Assert.Equal(512.5f, trees[0].X, 3);
        Assert.Equal(31.25f, trees[0].Y, 3);
        Assert.Equal(1000f, trees[0].Z, 3);
        Assert.Equal(0f, trees[0].YawDeg, 3);                 // atan2(0,1)
        Assert.Equal(0.83f, trees[0].Scale, 3);
        Assert.Equal(90f, trees[1].YawDeg, 3);                // atan2(1,0)
    }

    [Fact]
    public void A_file_that_is_not_a_dump_is_refused_clearly()
    {
        var ex = Assert.Throws<InvalidDataException>(() => OvergrowthCapture.LoadDump(new byte[] { 0xff, 0xff, 0xff, 0x7f, 1, 2 }));
        Assert.Contains("Not a tree dump", ex.Message);
        Assert.Throws<InvalidDataException>(() => OvergrowthCapture.LoadDump(Array.Empty<byte>()));
    }

    // --- the type index is the palette's types flattened in declaration order ------------------------------------
    private const string Wst = """
        <?xml version="1.0"?>
        <WRAPPER_TREE VERS="1.1">
          <overGrowth materialMapSideSize="256" viewdistance="450">
            <materials>
              <default><types>
              </types></default>
              <juicyGrass><types>
                <a geometryName="c05f_trees_m2" probability="0.5" scale="1"></a>
                <b geometryName="c03f_trees_m2" probability="0.3" scale="1"></b>
                <c geometryName="c07f_jungle_m2" probability="0.2" scale="1"></c>
              </types></juicyGrass>
              <wetDirt><types>
                <d geometryName="c02f_trees_m2" probability="1.0" scale="1"></d>
              </types></wetDirt>
            </materials>
          </overGrowth>
        </WRAPPER_TREE>
        """;

    /// <summary>Flaming Dart's dump reads 0/1/2 as juicyGrass's three types in order and 3 as wetDirt's - which is
    /// exactly "the palette's types, flattened, in declaration order".</summary>
    [Fact]
    public void A_captured_type_index_names_the_palettes_nth_type()
    {
        var pal = FoliagePalette.Parse(Wst);
        Assert.Equal(4, pal.FlatTypes.Count);
        Assert.Equal("c05f_trees_m2", pal.TypeByIndex(0)!.GeometryName);
        Assert.Equal("c03f_trees_m2", pal.TypeByIndex(1)!.GeometryName);
        Assert.Equal("c07f_jungle_m2", pal.TypeByIndex(2)!.GeometryName);
        Assert.Equal("c02f_trees_m2", pal.TypeByIndex(3)!.GeometryName);
        Assert.Null(pal.TypeByIndex(4));                       // a dump from another level: skipped, never guessed
        Assert.Null(pal.TypeByIndex(-1));
    }

    // --- switching the procedural forest off ---------------------------------------------------------------------
    [Fact]
    public void Emptying_the_types_stops_the_engine_planting_them_twice()
    {
        Assert.True(OvergrowthCapture.HasAnyTypes(Wst));
        var off = OvergrowthCapture.EmptyTypes(Wst);
        Assert.False(OvergrowthCapture.HasAnyTypes(off));
        Assert.DoesNotContain("c05f_trees_m2", off);
        // Everything that is not a type block survives - this must stay the level's own file.
        Assert.Contains("viewdistance=\"450\"", off);
        Assert.Contains("<juicyGrass>", off);
        Assert.Contains("<wetDirt>", off);
        Assert.Contains("materialMapSideSize=\"256\"", off);
        // And it still parses, with no types left.
        Assert.Equal(0, FoliagePalette.Parse(off).TypeCount);
    }

    // --- decal brightness ----------------------------------------------------------------------------------------
    private const string Rs = "subshader \"Sign_Material0\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\tmaterialDiffuse 1 1 1;\r\n\ttransparent false;\r\n\ttwosided true;\r\n\ttexture \"texture/sign\";\r\n}\r\n";

    [Fact]
    public void Brighten_adds_selfillum_after_materialDiffuse()
    {
        var outRs = RsWriter.Brighten(Rs, 0.65f);
        var lines = outRs.Split("\r\n").Select(l => l.Trim()).ToList();
        int d = lines.FindIndex(l => l.StartsWith("materialDiffuse"));
        Assert.Equal("selfillum 0.65 0.65 0.65;", lines[d + 1]);
        Assert.Contains("lighting true;", lines);              // untouched unless asked
        Assert.Contains("texture \"texture/sign\";", lines);   // everything else preserved
    }

    [Fact]
    public void Brighten_replaces_rather_than_stacks()
    {
        var once = RsWriter.Brighten(Rs, 0.4f);
        var twice = RsWriter.Brighten(once, 0.9f);
        Assert.Single(twice.Split("\r\n").Where(l => l.TrimStart().StartsWith("selfillum")));
        Assert.Contains("selfillum 0.9 0.9 0.9;", twice);
        Assert.DoesNotContain("0.4", twice);
    }

    [Fact]
    public void Zero_removes_the_selfillum_again()
    {
        var lit = RsWriter.Brighten(Rs, 0.8f);
        var back = RsWriter.Brighten(lit, 0f);
        Assert.DoesNotContain("selfillum", back);
        Assert.Contains("materialDiffuse 1 1 1;", back);
    }

    [Fact]
    public void Unlit_only_applies_at_full_and_only_when_asked()
    {
        Assert.Contains("lighting true;", RsWriter.Brighten(Rs, 1f, unlitAtFull: false));
        Assert.Contains("lighting true;", RsWriter.Brighten(Rs, 0.9f, unlitAtFull: true));
        var full = RsWriter.Brighten(Rs, 1f, unlitAtFull: true);
        Assert.Contains("lighting false;", full);
        Assert.Contains("lightingSpecular", RsWriter.Brighten(Rs.Replace("lighting true;", "lighting true;\r\n\tlightingSpecular false;"), 1f, unlitAtFull: true));
    }

    /// <summary>Multi-material shaders (an imported model) get one selfillum per subshader.</summary>
    [Fact]
    public void Every_subshader_in_the_file_is_lifted()
    {
        var two = Rs + Rs.Replace("Sign_Material0", "Sign_Material1");
        var outRs = RsWriter.Brighten(two, 0.5f);
        Assert.Equal(2, outRs.Split("\r\n").Count(l => l.TrimStart().StartsWith("selfillum")));
    }
}

/// <summary>
/// Brightening an object whose shader the level does NOT own. Saigon68's sewers are the case: their .rs is
/// standardMesh/O_sewers_A_M1.rs in the SHARED archive, so editing it would change the sewers on every map on the
/// install - but the level's own lightmap for that placement is 1024x1024 of solid ZERO, which is precisely why
/// they are dark (the engine has only renderer.LMambientColor left). Lifting the lightmap's floor is the in-map fix.
/// </summary>
public class LightmapFloorTests
{
    /// <summary>An 8-bit colour-mapped TGA with an identity grey ramp - the shape every shipped lightmap uses.</summary>
    private static byte[] Lightmap(int side, params byte[] pixels)
    {
        var f = new byte[18 + 256 * 3 + side * side];
        f[1] = 1; f[2] = 1;                       // colour-mapped, uncompressed colour-mapped image
        f[5] = 0; f[6] = 1; f[7] = 24;            // 256 entries, 24-bit
        f[12] = (byte)(side & 0xff); f[13] = (byte)(side >> 8);
        f[14] = (byte)(side & 0xff); f[15] = (byte)(side >> 8);
        f[16] = 8;
        for (int i = 0; i < 256; i++) { int o = 18 + i * 3; f[o] = f[o + 1] = f[o + 2] = (byte)i; }
        for (int i = 0; i < side * side; i++) f[18 + 256 * 3 + i] = pixels.Length == 1 ? pixels[0] : pixels[i];
        return f;
    }
    private static byte[] PixelsOf(byte[] file) => file[(18 + 256 * 3)..];

    [Fact]
    public void A_pitch_black_lightmap_is_lifted_to_the_floor()
    {
        var lifted = RefractorForge.Render.ObjectLightmaps.LiftFloor(Lightmap(4, 0), 96)!;
        Assert.All(PixelsOf(lifted), p => Assert.Equal(96, p));
    }

    [Fact]
    public void Anything_already_brighter_than_the_floor_is_left_alone()
    {
        var src = Lightmap(2, 0, 60, 200, 255);
        var lifted = RefractorForge.Render.ObjectLightmaps.LiftFloor(src, 100)!;
        Assert.Equal(new byte[] { 100, 100, 200, 255 }, PixelsOf(lifted));
    }

    [Fact]
    public void A_zero_floor_changes_nothing()
    {
        var src = Lightmap(2, 0, 60, 200, 255);
        Assert.Equal(PixelsOf(src), PixelsOf(RefractorForge.Render.ObjectLightmaps.LiftFloor(src, 0)!));
    }

    /// <summary>The header and palette must come back byte-identical - only index bytes may move.</summary>
    [Fact]
    public void The_header_and_palette_survive_untouched()
    {
        var src = Lightmap(4, 0);
        var lifted = RefractorForge.Render.ObjectLightmaps.LiftFloor(src, 128)!;
        Assert.Equal(src.Length, lifted.Length);
        Assert.Equal(src[..(18 + 256 * 3)], lifted[..(18 + 256 * 3)]);
        // ...and it still reads as a lightmap with detail.
        Assert.False(RefractorForge.Render.ObjectLightmaps.HasDetail(lifted));   // uniform, but valid
    }

    [Fact]
    public void A_file_that_is_not_an_8bit_lightmap_is_refused()
    {
        Assert.Null(RefractorForge.Render.ObjectLightmaps.LiftFloor(new byte[] { 1, 2, 3 }, 128));
        var rgb = Lightmap(2, 0); rgb[16] = 24;                 // 24-bit: not the shipped shape
        Assert.Null(RefractorForge.Render.ObjectLightmaps.LiftFloor(rgb, 128));
    }
}

/// <summary>
/// A .rs statement ends at its semicolon, NOT at the end of the line - and retail packs more than one onto a line.
/// The game's own <c>standardMesh/C02F_rice_M1.rs</c> ends
/// <c>materialSpecularPower 12.5;&lt;tab&gt;texture "texture/C04F_rice_grass";</c>, so a line-based reader saw the
/// specular power and never the texture: the rice paddies loaded untextured in the editor.
/// </summary>
public class RsStatementParsingTests
{
    // Byte-for-byte the shape of the shipped rice shader, tab and all.
    private const string Rice =
        "subshader \"C02F_rice_M1_Material0\" \"StandardMesh/Default\"\r\n{\r\n" +
        "\tlighting true;\r\n\tlightingSpecular true;\r\n\ttransparent false;\r\n\talphatestref 0.45;\r\n" +
        "\thighEndPerPixel true;\r\n\tmaterialDiffuse .5 .5 .5;\r\n\tmaterialAmbient  .4 .4 .4;\r\n" +
        "\tselfillum .3 .3 .3;\r\n\tmaterialSpecular 0.898039 0.898039 0.898039;\r\n" +
        "\tmaterialSpecularPower 12.5;\ttexture \"texture/C04F_rice_grass\";\r\n}\r\n";

    [Fact]
    public void A_texture_sharing_a_line_with_another_statement_is_still_found()
    {
        var set = RefractorForge.Render.RsShaderSet.Parse(Rice);
        Assert.True(set.Materials.ContainsKey("C02F_rice_M1_Material0"));
        Assert.Equal("texture/C04F_rice_grass", set.Materials["C02F_rice_M1_Material0"].Texture);
    }

    [Fact]
    public void The_rest_of_the_shader_still_reads_correctly()
    {
        var m = RefractorForge.Render.RsShaderSet.Parse(Rice).Materials["C02F_rice_M1_Material0"];
        Assert.Equal(0.5f, m.Diffuse.X, 3);
        Assert.False(m.Transparent);
    }

    /// <summary>A semicolon inside a quoted texture name must not split the statement.</summary>
    [Fact]
    public void Quotes_are_respected()
    {
        var set = RefractorForge.Render.RsShaderSet.Parse(
            "subshader \"M\" \"StandardMesh/Default\"\r\n{\r\n\ttexture \"texture/od;d\";\r\n}\r\n");
        Assert.Equal("texture/od;d", set.Materials["M"].Texture);
    }

    /// <summary>A trailing // comment is cut before splitting, as retail shaders carry them.</summary>
    [Fact]
    public void A_trailing_comment_is_ignored()
    {
        var set = RefractorForge.Render.RsShaderSet.Parse(
            "subshader \"M\" \"StandardMesh/Default\"\r\n{\r\n\ttexture \"texture/a\"; // the ground\r\n}\r\n");
        Assert.Equal("texture/a", set.Materials["M"].Texture);
    }

    /// <summary>One statement per line, the ordinary case, is unchanged.</summary>
    [Fact]
    public void The_normal_one_per_line_shape_still_parses()
    {
        var set = RefractorForge.Render.RsShaderSet.Parse(
            "subshader \"A\" \"StandardMesh/Default\"\r\n{\r\n\tlighting true;\r\n\ttexture \"texture/a\";\r\n}\r\n" +
            "subshader \"B\" \"StandardMesh/Default\"\r\n{\r\n\ttexture \"texture/b\";\r\n}\r\n");
        Assert.Equal("texture/a", set.Materials["A"].Texture);
        Assert.Equal("texture/b", set.Materials["B"].Texture);
    }
}
