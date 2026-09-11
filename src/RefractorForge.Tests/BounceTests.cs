using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Indirect light: the term that makes a surface pick up the colour of what is next to it.
///
/// <para>Tested at the physics level rather than through a baked map, because a sunlit texel saturates at 1 and
/// would hide the very thing being measured. What matters is that the light arriving is proportional to the
/// bouncing surface's albedo, falls off with distance, carries its colour, and is zero when nothing is there.</para>
/// </summary>
public class BounceTests
{
    private static (Heightmap, TerrainConfig, float) FlatGround()
    {
        var hm = new Heightmap(16, 16);
        var cfg = new TerrainConfig { MaterialSize = 16, WorldSize = 256, YScale = 1f };
        var (_, maxH) = TerrainShadow.HeightSpan(hm, cfg);
        return (hm, cfg, maxH);
    }

    // The sun points up and toward -z, so the face of a wall at z = 8 that looks toward -z is the sunlit one,
    // and a point on the ground at z < 8 both sees that face and is itself in sunlight.
    private static readonly Vec3 Sun = new(0f, 0.7071f, -0.7071f);

    /// <summary>A wall at z = 8, 6 m tall, with a given albedo.</summary>
    private static RayScene Wall(Vector3 albedo) => RayScene.Build(
        new List<(Vector3, Vector3, Vector3)>
        {
            (new Vector3(-10, 0, 8), new Vector3(10, 0, 8), new Vector3(10, 6, 8)),
            (new Vector3(-10, 0, 8), new Vector3(10, 6, 8), new Vector3(-10, 6, 8)),
        },
        new[] { albedo, albedo })!;

    private static Vector3 At(RayScene scene, Vector3 p, int samples = 256)
    {
        var (hm, cfg, maxH) = FlatGround();
        return LightSampling.Bounce(scene, null, hm, cfg, maxH, p, Vector3.UnitY, Sun,
                                    sunColour: Vector3.One, samples: samples, depth: 1, maxDist: 50f, seed: 4242);
    }

    /// <summary>THE POINT: a red wall sends back RED light. On BF1942 that colour reaches the game intact.</summary>
    [Fact]
    public void A_coloured_wall_tints_the_light_it_bounces()
    {
        var bounce = At(Wall(new Vector3(0.8f, 0.05f, 0.05f)), new Vector3(0f, 0.05f, 6f));
        Assert.True(bounce.X > 0.01f, $"a sunlit red wall should send back some light, got {bounce.X:0.0000}");
        Assert.True(bounce.X > bounce.Y * 4f && bounce.X > bounce.Z * 4f,
            $"the bounced light should be red, got ({bounce.X:0.000}, {bounce.Y:0.000}, {bounce.Z:0.000})");
    }

    /// <summary>Brighter surface, more light back - the estimator has to be proportional to albedo.</summary>
    [Fact]
    public void A_brighter_wall_bounces_more_light()
    {
        var dim = At(Wall(new Vector3(0.15f)), new Vector3(0f, 0.05f, 6f));
        var bright = At(Wall(new Vector3(0.75f)), new Vector3(0f, 0.05f, 6f));
        Assert.True(bright.X > dim.X * 3f, $"albedo 0.75 vs 0.15: {bright.X:0.000} vs {dim.X:0.000}");
    }

    /// <summary>Indirect light falls off with distance, because the wall subtends a smaller solid angle.</summary>
    [Fact]
    public void Bounced_light_falls_off_with_distance()
    {
        var wall = Wall(new Vector3(0.8f));
        float near = At(wall, new Vector3(0f, 0.05f, 7f)).X;
        float far = At(wall, new Vector3(0f, 0.05f, 1f)).X;
        Assert.True(near > far, $"1 m from the wall ({near:0.000}) should exceed 7 m away ({far:0.000})");
    }

    /// <summary>Nothing to bounce off, nothing comes back. A bounce term that leaks light into an empty scene
    /// would brighten every map for no reason.</summary>
    [Fact]
    public void An_empty_scene_bounces_nothing()
    {
        var (hm, cfg, maxH) = FlatGround();
        Assert.Equal(Vector3.Zero, LightSampling.Bounce(null, null, hm, cfg, maxH, new Vector3(0f, 0.05f, 6f),
            Vector3.UnitY, Sun, Vector3.One, 64, 1, 50f, 1));
    }

    /// <summary>A wall the sun never reaches has nothing to pass on. This is the check that the bounce is
    /// carrying real light rather than just "something is nearby" - an occlusion term in disguise.</summary>
    [Fact]
    public void A_shadowed_wall_bounces_nothing()
    {
        // A lid above the wall puts it entirely in shadow; the ground point still sees the wall perfectly well.
        var tris = new List<(Vector3, Vector3, Vector3)>
        {
            (new Vector3(-10, 0, 8), new Vector3(10, 0, 8), new Vector3(10, 6, 8)),
            (new Vector3(-10, 0, 8), new Vector3(10, 6, 8), new Vector3(-10, 6, 8)),
            (new Vector3(-12, 9, -6), new Vector3(12, 9, -6), new Vector3(12, 9, 12)),
            (new Vector3(-12, 9, -6), new Vector3(12, 9, 12), new Vector3(-12, 9, 12)),
        };
        var albedo = new[] { new Vector3(0.9f), new Vector3(0.9f), new Vector3(0.9f), new Vector3(0.9f) };
        var shaded = RayScene.Build(tris, albedo)!;

        var lit = At(Wall(new Vector3(0.9f)), new Vector3(0f, 0.05f, 6f));
        var dark = At(shaded, new Vector3(0f, 0.05f, 6f));
        Assert.True(lit.X > 0.01f, "the control must actually bounce something");
        Assert.True(dark.X < lit.X * 0.2f,
            $"a wall in shadow should pass on almost nothing: {dark.X:0.000} vs {lit.X:0.000}");
    }

    /// <summary>A second bounce adds light rather than losing it, and stays small - if depth 2 were dramatically
    /// brighter than depth 1 the estimator would be gaining energy, which is the classic path-tracer bug.</summary>
    [Fact]
    public void A_second_bounce_adds_a_little_and_does_not_run_away()
    {
        var (hm, cfg, maxH) = FlatGround();
        var wall = Wall(new Vector3(0.8f));
        var p = new Vector3(0f, 0.05f, 6f);
        float one = LightSampling.Bounce(wall, null, hm, cfg, maxH, p, Vector3.UnitY, Sun, Vector3.One, 192, 1, 50f, 9).X;
        float two = LightSampling.Bounce(wall, null, hm, cfg, maxH, p, Vector3.UnitY, Sun, Vector3.One, 192, 2, 50f, 9).X;
        Assert.True(two >= one, $"a second bounce must not remove light: {one:0.000} -> {two:0.000}");
        Assert.True(two < one * 2f, $"a second bounce should be a correction, not a doubling: {one:0.000} -> {two:0.000}");
    }

    /// <summary>Reproducible, like everything else in the bake.</summary>
    [Fact]
    public void The_bounce_is_deterministic()
    {
        var wall = Wall(new Vector3(0.8f));
        Assert.Equal(At(wall, new Vector3(0f, 0.05f, 6f), 128), At(wall, new Vector3(0f, 0.05f, 6f), 128));
    }
}
