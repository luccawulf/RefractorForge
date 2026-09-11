using System;
using System.Linq;
using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A light pool is the only way to put visible light on a surface in these games. The per-object lightmap cannot do
/// it — <c>RaShaderPPLSTs1DifLmp.fx</c> reads it as <c>Prelight = tex2D(...).b</c>, one scalar that MULTIPLIES the
/// sun, so it can only ever take light away — and the engine renders no dynamic lights at all. What works is an
/// unlit additively blended quad, the material all 41 of the game's muzzle-flash shaders use.
///
/// These tests pin the generated material to that shipped recipe statement for statement, because a single wrong or
/// missing state is the difference between a pool of light and a grey square sitting on the floor, and nothing short
/// of launching the game would tell us which we had.
/// </summary>
public class LightPoolTests
{
    private static string Text(DecalObject.Built b, string rel) =>
        Encoding.UTF8.GetString(b.Files.First(f => f.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase)).Bytes);

    /// <summary>
    /// Statement for statement against retail's <c>standardMesh/e_MuzzAK47_m1.rs</c>. Additive blending is what
    /// makes the quad ADD light rather than paint a patch of colour; `lighting false` + `selfillum 1 1 1` is what
    /// keeps it bright on a night map, which is the whole point; `depthWrite false` keeps it from punching a hole
    /// in whatever is drawn after it.
    /// </summary>
    [Fact]
    public void Glow_material_matches_the_shipped_muzzle_flash_recipe()
    {
        var rs = RsWriter.Write(new[] { RsWriter.Glow("pool_Material0", "lightpool_pool") });

        foreach (var expected in new[]
        {
            "lighting false;",
            "lightingSpecular false;",
            "materialSpecular 0 0 0;",
            "materialDiffuse 1 1 1;",
            "selfillum 1 1 1;",
            "opacity 1;",
            "transparent true;",
            "sortedBlend true;",
            "blendSrc sourcealpha;",
            "blendDest one;",
            "depthWrite false;",
            "twosided true;",
        })
            Assert.Contains(expected, rs);

        // The two grammar rules that silently kill a shader: a folder-qualified texture, and a ';' on every line.
        Assert.Contains("texture \"texture/lightpool_pool\";", rs);
        foreach (var line in rs.Split('\n').Select(l => l.Trim())
                               .Where(l => l.Length > 0 && l != "{" && l != "}" && !l.StartsWith("subshader")))
            Assert.EndsWith(";", line);
    }

    /// <summary>An ordinary material must not have picked up any of the glow states — the additive path is opt-in,
    /// and a decal or an imported model that started blending additively would wash out.</summary>
    [Fact]
    public void Ordinary_material_is_unchanged_by_the_new_states()
    {
        var rs = RsWriter.Write(new[] { new RsWriter.Material("m", "oak", new Vec3(1, 1, 1), AlphaTestRef: 0.5f, TwoSided: true) });
        Assert.DoesNotContain("blendDest", rs);
        Assert.DoesNotContain("selfillum", rs);
        Assert.DoesNotContain("sortedBlend", rs);
        Assert.DoesNotContain("materialSpecular", rs);
        Assert.DoesNotContain("opacity", rs);
        Assert.Contains("lighting true;", rs);
    }

    /// <summary>
    /// The falloff has to live in the ALPHA, not the colour. The blend is sourcealpha/one, so alpha is literally how
    /// much light each texel adds; putting the shape there keeps the lamp one colour from the hot centre to the rim,
    /// which is how a real pool of light looks. A gradient in the colour instead would go grey at the edge.
    /// </summary>
    [Fact]
    public void Gradient_fades_in_alpha_and_holds_one_colour()
    {
        const int n = 64;
        var colour = new Vec3(1f, 0.85f, 0.6f);
        var px = LightPool.Gradient(n, colour, brightness: 1f, softness: 0.6f);
        Assert.Equal(n * n * 4, px.Length);

        (byte R, byte G, byte B, byte A) At(int x, int y)
        { int i = (y * n + x) * 4; return (px[i], px[i + 1], px[i + 2], px[i + 3]); }

        var centre = At(n / 2, n / 2);
        var corner = At(0, 0);
        var mid = At(n / 2, n / 4);          // halfway out along the vertical

        Assert.True(centre.A > 240, $"centre should be at full strength, was {centre.A}");
        Assert.Equal(0, corner.A);           // outside the disc entirely
        Assert.InRange(mid.A, 1, 239);       // genuinely a ramp, not a hard disc

        // One colour everywhere, including where it has faded to nothing.
        foreach (var p in new[] { centre, mid, corner })
        {
            Assert.Equal(255, p.R);
            Assert.Equal(217, p.G);          // 0.85 * 255
            Assert.Equal(153, p.B);          // 0.60 * 255
        }

        // Radially symmetric: opposite sides of the centre match, so the disc is not half a texel off.
        for (int d = 1; d < n / 2; d += 5)
            Assert.Equal(At(n / 2 - d, n / 2).A, At(n / 2 + d - 1, n / 2).A);
    }

    /// <summary>Softness 0 is a hard disc, 1 fades from the middle. Both extremes have to stay usable rather than
    /// degenerating to a blank texture.</summary>
    [Fact]
    public void Softness_spans_hard_disc_to_full_fade()
    {
        const int n = 32;
        byte Alpha(byte[] p, int x, int y) => p[(y * n + x) * 4 + 3];

        var hard = LightPool.Gradient(n, new Vec3(1, 1, 1), 1f, softness: 0f);
        var soft = LightPool.Gradient(n, new Vec3(1, 1, 1), 1f, softness: 1f);

        // Just inside the rim: a hard disc is still bright there, a fully soft one has nearly gone.
        int nearRim = n / 2 + n / 3;
        Assert.True(Alpha(hard, nearRim, n / 2) > Alpha(soft, nearRim, n / 2));
        Assert.True(Alpha(hard, n / 2, n / 2) > 240 && Alpha(soft, n / 2, n / 2) > 240);   // both lit in the middle
    }

    /// <summary>Brightness scales what the pool adds, and must not wrap round when it is pushed past full.</summary>
    [Fact]
    public void Brightness_scales_and_clamps()
    {
        const int n = 16;
        var dim = LightPool.Gradient(n, new Vec3(1, 1, 1), brightness: 0.25f, softness: 0.5f);
        var blown = LightPool.Gradient(n, new Vec3(1, 1, 1), brightness: 4f, softness: 0.5f);
        int c = ((n / 2) * n + n / 2) * 4 + 3;
        Assert.InRange(dim[c], 50, 75);
        Assert.Equal(255, blown[c]);
    }

    /// <summary>The whole six-file object, and the states that matter, end to end.</summary>
    [Fact]
    public void Build_emits_the_full_object_with_an_additive_shader()
    {
        var built = LightPool.Build("Saigon68", "lightpool1", 8f, new Vec3(1f, 0.93f, 0.8f),
                                    brightness: 1f, softness: 0.6f, shape: LightPool.Shape.Floor,
                                    baseSub: "BfVietnam", textureSize: 64,
                                    encodeDds: rgba => rgba);          // stand in for the DDS encoder

        Assert.Equal("lightpool1", built.Template);
        foreach (var rel in new[]
        {
            "StandardMesh/lightpool1.sm", "StandardMesh/lightpool1.rs",
            "Texture/lightpool_lightpool1.dds",
            "Objects/lightpool1/Geometries.con", "Objects/lightpool1/Objects.con",
            "Objects/lightpool1/lightpool1.con",
        })
            Assert.Contains(built.Files, f => f.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase));

        var rs = Text(built, "StandardMesh/lightpool1.rs");
        Assert.Contains("blendDest one;", rs);
        Assert.Contains("selfillum 1 1 1;", rs);
        Assert.Contains("texture \"texture/lightpool_lightpool1\";", rs);

        // It must never be something you can walk into or shoot.
        Assert.Contains("ObjectTemplate.hasCollisionPhysics 0", Text(built, "Objects/lightpool1/Objects.con"));

        // The mesh path has to point at THIS game's mount root; a bf1942 path resolves to nothing in Vietnam.
        Assert.Contains("../BfVietnam/levels/Saigon68/StandardMesh/lightpool1", Text(built, "Objects/lightpool1/Geometries.con"));

        // A floor pool lies flat: every vertex at the same height, spread across X and Z.
        var pts = built.Mesh.SubMeshes.SelectMany(s => s.Positions).ToArray();
        Assert.True(pts.Max(p => p.Y) - pts.Min(p => p.Y) < 1e-3f, "a floor pool must be flat");
        Assert.True(pts.Max(p => p.X) - pts.Min(p => p.X) > 7f);
        Assert.True(pts.Max(p => p.Z) - pts.Min(p => p.Z) > 7f);
    }

    /// <summary>A glow stands upright, so it reads as a halo at the bulb rather than a mark on the floor.</summary>
    [Fact]
    public void Glow_shape_stands_upright()
    {
        var built = LightPool.Build("Test", "lampglow1", 4f, new Vec3(1, 1, 1), shape: LightPool.Shape.Glow,
                                    baseSub: "bf1942", encodeDds: rgba => rgba);
        var pts = built.Mesh.SubMeshes.SelectMany(s => s.Positions).ToArray();
        Assert.True(pts.Max(p => p.Y) - pts.Min(p => p.Y) > 3f, "a glow must stand up");
    }

    /// <summary>Lamp detection drives "put one under every lamp", so it has to catch the real templates and leave
    /// the rest of the level alone.</summary>
    [Fact]
    public void Lamp_detection_finds_lamps_and_nothing_else()
    {
        foreach (var lamp in new[] { "o_lamp_M1", "o_Lamp_M2", "e_streetlight", "telephone_light",
                                     "O_Lantern_01", "torch_wall", "candle_a", "lightbulb3" })
            Assert.True(LightPool.LooksLikeLamp(lamp), lamp);

        foreach (var notLamp in new[] { "o_huehouse_b", "sandbags_4m", "palmtree_01", "O_sewers_A_M1",
                                        "tanktrap_01", "", "  " })
            Assert.False(LightPool.LooksLikeLamp(notLamp), notLamp);

        var found = LightPool.FindLamps(new[]
        {
            ("o_lamp_M1", new Vec3(10, 4, 20)),
            ("o_huehouse_b", new Vec3(0, 0, 0)),
            ("telephone_light", new Vec3(-5, 6, 7)),
        });
        Assert.Equal(2, found.Count);
        Assert.Equal(new Vec3(10, 4, 20), found[0].Position);
    }

    /// <summary>Registration is shared with the decal path and must stay idempotent — making a second pool must add
    /// to Objects/objects.con, never replace what is already there.</summary>
    [Fact]
    public void Two_pools_both_register()
    {
        var a = LightPool.Build("Map", "lightpool1", 6f, new Vec3(1, 1, 1), encodeDds: r => r);
        var b = LightPool.Build("Map", "lightpool2", 6f, new Vec3(1, 1, 1), encodeDds: r => r);

        var oc = DecalObject.PatchObjectsCon(null, a.RunLine);
        oc = DecalObject.PatchObjectsCon(oc, b.RunLine);
        Assert.Contains("run lightpool1/lightpool1", oc);
        Assert.Contains("run lightpool2/lightpool2", oc);
        Assert.Equal(oc, DecalObject.PatchObjectsCon(oc, a.RunLine));    // adding the first again changes nothing
    }
}
