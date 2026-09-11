using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The two integrals that make a bake look rendered rather than stencilled: a sun with angular size (so shadows
/// have a penumbra) and sky visibility (so creases and undersides darken).
///
/// <para>These assert the BEHAVIOUR that distinguishes them from what the bake did before - that a penumbra is
/// monotonic across the shadow edge, and that an occluded point sees less sky than an open one - rather than
/// exact numbers, which would just re-state the implementation.</para>
/// </summary>
public class LightSamplingTests
{
    private static (Heightmap, TerrainConfig, float) FlatGround()
    {
        var hm = new Heightmap(16, 16);
        var cfg = new TerrainConfig { MaterialSize = 16, WorldSize = 256, YScale = 1f };
        var (_, maxH) = TerrainShadow.HeightSpan(hm, cfg);
        return (hm, cfg, maxH);
    }

    /// <summary>A wall standing on the ground, spanning x in [-20,20] at z = 0, from y = 0 to y = 10.</summary>
    private static RayScene Wall()
    {
        var tris = new List<(Vector3, Vector3, Vector3)>
        {
            (new Vector3(-20, 0, 0), new Vector3(20, 0, 0), new Vector3(20, 10, 0)),
            (new Vector3(-20, 0, 0), new Vector3(20, 10, 0), new Vector3(-20, 10, 0)),
        };
        return RayScene.Build(tris)!;
    }

    // Straight up, so the wall's shadow falls along +z from its foot... with a tilted sun it falls to one side.
    private static readonly Vec3 Sun = new(0f, 0.7071f, -0.7071f);   // 45 degrees, pointing toward -z

    /// <summary>
    /// THE POINT OF ALL THIS: across a shadow edge the sun term must vary CONTINUOUSLY, not jump 0 to 1.
    /// Walking away from the wall, visibility must never decrease, and somewhere in the middle it must be
    /// strictly between fully shadowed and fully lit - that intermediate band IS the penumbra.
    /// </summary>
    [Fact]
    public void An_area_sun_produces_a_monotonic_penumbra()
    {
        var (hm, cfg, maxH) = FlatGround();
        var wall = Wall();

        var vis = new List<float>();
        for (float z = 8f; z <= 13f; z += 0.25f)
            vis.Add(LightSampling.SunVisibility(wall, null, hm, cfg, maxH, new Vector3(0f, 0.05f, z),
                                                Sun, angularDiameterDeg: 4f, samples: 64, seed: 12345));

        for (int i = 1; i < vis.Count; i++)
            Assert.True(vis[i] >= vis[i - 1] - 0.02f,
                $"sun visibility fell from {vis[i - 1]:0.000} to {vis[i]:0.000} moving out of the shadow");

        Assert.True(vis[0] < 0.05f, $"deep in the shadow should be dark, got {vis[0]:0.000}");
        Assert.True(vis[^1] > 0.95f, $"well clear of the wall should be lit, got {vis[^1]:0.000}");
        Assert.Contains(vis, v => v > 0.05f && v < 0.95f);      // a real gradient, not a step
    }

    /// <summary>One sample, or no angular size, is the old binary test - so nothing that relies on the hard
    /// shadow changes behaviour until it asks for a soft one.</summary>
    [Fact]
    public void A_point_sun_is_still_binary()
    {
        var (hm, cfg, maxH) = FlatGround();
        var wall = Wall();
        var shadowed = new Vector3(0f, 0.05f, 5f);
        var open = new Vector3(0f, 0.05f, 40f);

        foreach (var (deg, n) in new[] { (0f, 64), (4f, 1) })
        {
            Assert.Equal(0f, LightSampling.SunVisibility(wall, null, hm, cfg, maxH, shadowed, Sun, deg, n, 1));
            Assert.Equal(1f, LightSampling.SunVisibility(wall, null, hm, cfg, maxH, open, Sun, deg, n, 1));
        }
    }

    /// <summary>A bigger sun means a wider penumbra - the control has to actually control something.</summary>
    [Fact]
    public void A_larger_angular_diameter_widens_the_penumbra()
    {
        var (hm, cfg, maxH) = FlatGround();
        var wall = Wall();

        int Band(float deg)
        {
            int n = 0;
            for (float z = 6f; z <= 20f; z += 0.25f)
            {
                float v = LightSampling.SunVisibility(wall, null, hm, cfg, maxH, new Vector3(0f, 0.05f, z), Sun, deg, 64, 7);
                if (v > 0.05f && v < 0.95f) n++;
            }
            return n;
        }
        Assert.True(Band(8f) > Band(1f), "an 8-degree sun should have a wider soft band than a 1-degree one");
    }

    /// <summary>Sky visibility is what darkens a crease. A point in the corner where two walls meet sees far
    /// less sky than the same surface out in the open - that difference is the whole ambient-occlusion effect.</summary>
    [Fact]
    public void A_crease_sees_less_sky_than_open_ground()
    {
        var (hm, cfg, maxH) = FlatGround();
        // Two walls meeting at the origin, forming an inside corner over the ground.
        var tris = new List<(Vector3, Vector3, Vector3)>
        {
            (new Vector3(0, 0, -10), new Vector3(0, 0, 10), new Vector3(0, 10, 10)),
            (new Vector3(0, 0, -10), new Vector3(0, 10, 10), new Vector3(0, 10, -10)),
            (new Vector3(-10, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0)),
            (new Vector3(-10, 0, 0), new Vector3(10, 10, 0), new Vector3(-10, 10, 0)),
        };
        var scene = RayScene.Build(tris)!;

        float corner = LightSampling.SkyVisibility(scene, null, hm, cfg, maxH,
            new Vector3(0.3f, 0.05f, 0.3f), Vector3.UnitY, samples: 128, maxDist: 0f, seed: 5);
        float open = LightSampling.SkyVisibility(scene, null, hm, cfg, maxH,
            new Vector3(30f, 0.05f, 30f), Vector3.UnitY, samples: 128, maxDist: 0f, seed: 5);

        Assert.True(open > 0.9f, $"open ground should see nearly the whole sky, got {open:0.000}");
        Assert.True(corner < 0.55f, $"an inside corner should be well occluded, got {corner:0.000}");
    }

    /// <summary>Under a roof, almost no sky - which is what stops an interior reading as brightly as a courtyard.</summary>
    [Fact]
    public void A_point_under_an_overhang_sees_almost_no_sky()
    {
        var (hm, cfg, maxH) = FlatGround();
        var roof = RayScene.Build(new List<(Vector3, Vector3, Vector3)>
        {
            (new Vector3(-10, 3, -10), new Vector3(10, 3, -10), new Vector3(10, 3, 10)),
            (new Vector3(-10, 3, -10), new Vector3(10, 3, 10), new Vector3(-10, 3, 10)),
        })!;

        float under = LightSampling.SkyVisibility(roof, null, hm, cfg, maxH,
            new Vector3(0f, 0.05f, 0f), Vector3.UnitY, samples: 128, maxDist: 0f, seed: 11);
        Assert.True(under < 0.15f, $"under a 20 m roof should be nearly closed, got {under:0.000}");
    }

    /// <summary>A bounded radius is contact darkening, not sky visibility: the same roof 3 m up stops mattering
    /// once rays only look 1 m. Both are useful and they are the same integral with a different reach.</summary>
    [Fact]
    public void A_bounded_radius_only_sees_nearby_geometry()
    {
        var (hm, cfg, maxH) = FlatGround();
        var roof = RayScene.Build(new List<(Vector3, Vector3, Vector3)>
        {
            (new Vector3(-10, 3, -10), new Vector3(10, 3, -10), new Vector3(10, 3, 10)),
            (new Vector3(-10, 3, -10), new Vector3(10, 3, 10), new Vector3(-10, 3, 10)),
        })!;

        float near = LightSampling.SkyVisibility(roof, null, hm, cfg, maxH,
            new Vector3(0f, 0.05f, 0f), Vector3.UnitY, samples: 128, maxDist: 1f, seed: 11);
        Assert.True(near > 0.9f, $"a 1 m reach should not notice a roof 3 m up, got {near:0.000}");
    }

    /// <summary>Two runs of the same bake must produce the same file, or no regression test on a bake is possible.</summary>
    [Fact]
    public void Sampling_is_deterministic()
    {
        var (hm, cfg, maxH) = FlatGround();
        var wall = Wall();
        var p = new Vector3(0f, 0.05f, 10.5f);
        float a = LightSampling.SunVisibility(wall, null, hm, cfg, maxH, p, Sun, 4f, 32, 424242);
        float b = LightSampling.SunVisibility(wall, null, hm, cfg, maxH, p, Sun, 4f, 32, 424242);
        Assert.Equal(a, b);

        float s1 = LightSampling.SkyVisibility(wall, null, hm, cfg, maxH, p, Vector3.UnitY, 64, 0f, 99);
        float s2 = LightSampling.SkyVisibility(wall, null, hm, cfg, maxH, p, Vector3.UnitY, 64, 0f, 99);
        Assert.Equal(s1, s2);
    }

    /// <summary>Neighbouring texels must not share a sample pattern, or a soft edge bands into visible rings
    /// instead of dithering. Different seeds have to give different (but still valid) answers mid-penumbra.</summary>
    [Fact]
    public void Neighbouring_texels_use_different_sample_patterns()
    {
        var (hm, cfg, maxH) = FlatGround();
        var wall = Wall();
        var p = new Vector3(0f, 0.05f, 10.4f);
        var seen = new HashSet<float>();
        for (uint s = 0; s < 12; s++)
            seen.Add(LightSampling.SunVisibility(wall, null, hm, cfg, maxH, p, Sun, 6f, 16, s));
        Assert.True(seen.Count > 1, "every seed produced the identical value - the pattern is not being rotated");
    }
}
