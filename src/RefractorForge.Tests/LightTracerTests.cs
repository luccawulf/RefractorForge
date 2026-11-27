using System.Numerics;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The traced light (<see cref="LightTracer"/>) against cases with a known answer, and the scene it travels in
/// (<see cref="LightScene"/>, <see cref="RayScene"/>'s cut-outs).
/// </summary>
public class LightTracerTests
{
    private static readonly DaylightModel Day = new Daylight
    {
        ToSun = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.45f)), SkyStrength = 0.25f, GroundAlbedo = new Vector3(0.2f),
    }.Build();

    private static readonly LightTracer.Budget Fine = new(SunSamples: 32, HemiSamples: 2048, Bounces: 2);

    private static void Near(float want, float got, float tol) => Assert.InRange(got, want - tol, want + tol);

    // A horizontal square in Refractor's winding (clockwise seen from above), centred on (cx, cz).
    private static void Quad(List<(Vector3, Vector3, Vector3)> t, float cx, float y, float cz, float half)
    {
        Vector3 a = new(cx - half, y, cz - half), b = new(cx + half, y, cz - half), c = new(cx - half, y, cz + half), d = new(cx + half, y, cz + half);
        t.Add((a, b, c)); t.Add((b, d, c));
    }

    [Fact]
    public void In_open_air_the_traced_sky_is_the_analytic_one()
    {
        // Facing up: the whole dome, none of the ground. Facing down: the reverse. A wall: half of each.
        foreach (var n in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, Vector3.Normalize(new Vector3(0.3f, 0.5f, -0.8f)) })
        {
            var l = LightTracer.Gather(null, Day, Vector3.Zero, n, Fine, 11);
            Assert.Equal(1f, MathF.Max(l.Sun, Vector3.Dot(n, Day.ToSun) > 0 ? 0f : 1f));   // nothing shadows the sun
            Assert.Equal(Vector3.Zero, l.Indirect);
            float want = EngineLight.Luma(Day.OpenIrradiance(n) - Day.SunIrradiance * MathF.Max(0f, Vector3.Dot(n, Day.ToSun)));
            Near(want, EngineLight.Luma(l.Sky), 0.01f + want * 0.03f);
            // And the whole of it is what the daylight model says an open surface receives.
            Near(EngineLight.Luma(Day.OpenIrradiance(n)), EngineLight.Luma(l.Irradiance(Day, n)), 0.01f + want * 0.03f);
        }
    }

    [Fact]
    public void A_face_looking_down_at_sunlit_ground_is_lit_by_the_ground_s_bounce()
    {
        // A very large plane of albedo 0.2 under a point that looks straight down: every ray lands on the plane,
        // which is in full sun under the whole sky, so what comes back is albedo x what open ground receives.
        var tris = new List<(Vector3, Vector3, Vector3)>();
        Quad(tris, 0, 0, 0, 5000f);
        var scene = RayScene.Build(tris, tris.Select(_ => new Vector3(0.2f)).ToList())!;
        var l = LightTracer.Gather(scene, Day, new Vector3(0, 2f, 0), -Vector3.UnitY, Fine, 5);
        Assert.Equal(0f, l.Sun);
        Near(0f, EngineLight.Luma(l.Sky), 1e-4f);
        Near(0.2f * EngineLight.Luma(Day.OpenGroundIrradiance), EngineLight.Luma(l.Indirect), 0.012f);

        // With no bounces allowed the ground returns nothing, and the light is simply missing - never invented.
        var none = LightTracer.Gather(scene, Day, new Vector3(0, 2f, 0), -Vector3.UnitY, Fine with { Bounces = 0 }, 5);
        Assert.Equal(Vector3.Zero, none.Indirect);
    }

    [Fact]
    public void A_roof_takes_the_sun_and_most_of_the_sky_and_its_shadow_has_a_soft_edge()
    {
        var tris = new List<(Vector3, Vector3, Vector3)>();
        Quad(tris, 0, 0, 0, 500f);          // ground
        Quad(tris, 0, 3f, 0, 4f);           // an 8 m roof, 3 m up
        var scene = RayScene.Build(tris, tris.Select(_ => new Vector3(0.2f)).ToList())!;
        var b = new LightTracer.Budget(64, 512, 1);

        // Where the roof's shadow falls: straight under it, pushed away from the sun by its height.
        var under = new Vector3(0, 0, 0) - new Vector3(Day.ToSun.X, 0, Day.ToSun.Z) * (3f / Day.ToSun.Y);
        var shade = LightTracer.Gather(scene, Day, under, Vector3.UnitY, b, 3);
        var open = LightTracer.Gather(scene, Day, new Vector3(100, 0, 100), Vector3.UnitY, b, 3);
        Assert.Equal(0f, shade.Sun);
        Assert.Equal(1f, open.Sun);
        Assert.True(EngineLight.Luma(shade.Sky) < EngineLight.Luma(open.Sky) * 0.85f, "the roof hides part of the sky");
        Assert.True(EngineLight.Luma(shade.Sky) > EngineLight.Luma(open.Sky) * 0.2f, "but not all of it");

        // Walk out across the shadow's edge: the sun comes back gradually, not at a step. Along the direction the
        // shadow is thrown the 8 m roof's edge lies 4 m further on than its centre.
        var along = Vector3.Normalize(new Vector3(-Day.ToSun.X, 0, -Day.ToSun.Z));
        float span = 4f / MathF.Max(MathF.Abs(along.X), MathF.Abs(along.Z));
        int partial = 0; float last = 0f;
        for (float d = span - 0.3f; d <= span + 0.3f; d += 0.005f)
        {
            float s = LightTracer.SunVisibility(scene, Day, under + along * d, 64, 1);
            if (s > 0.02f && s < 0.98f) partial++;
            Assert.True(s >= last - 0.15f, "the sun does not flicker back out on the way");
            last = s;
        }
        Assert.Equal(1f, last);
        Assert.True(partial >= 3, $"a penumbra a few centimetres wide at 3 m; saw {partial} partial steps");
    }

    [Fact]
    public void A_cut_out_casts_the_shadow_of_its_texture_not_of_its_triangles()
    {
        // A sheet 2 m up whose texture is a checkerboard of surface and hole, 8 cells across.
        var px = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                px[(y * 64 + x) * 4 + 3] = (byte)((((x / 8) + (y / 8)) & 1) == 0 ? 255 : 0);
        var mask = RayScene.AlphaMask.From(64, 64, px, 0.5f)!;
        Near(0.5f, mask.Coverage, 0.01f);

        Vector3 a = new(-4, 2, -4), b = new(4, 2, -4), c = new(-4, 2, 4), d = new(4, 2, 4);
        var tris = new List<(Vector3, Vector3, Vector3)> { (a, b, c), (b, d, c) };
        var cuts = new List<RayScene.Cutout?>
        {
            new RayScene.Cutout(mask, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)),
            new RayScene.Cutout(mask, new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)),
        };
        var holes = RayScene.Build(tris, null, cuts)!;
        var solid = RayScene.Build(tris)!;
        Assert.True(holes.HasCutouts);
        Assert.False(solid.HasCutouts);

        int open = 0, n = 0;
        for (float x = -3.5f; x <= 3.5f; x += 0.09f)
            for (float z = -3.5f; z <= 3.5f; z += 0.09f)
            {
                n++;
                Assert.True(solid.Occluded(new Vector3(x, 0, z), Vector3.UnitY));      // the sheet as plywood
                if (!holes.Occluded(new Vector3(x, 0, z), Vector3.UnitY)) open++;
            }
        Near(0.5f, open / (float)n, 0.04f);

        // A texture with no holes below the threshold is not a cut-out at all.
        for (int i = 3; i < px.Length; i += 4) px[i] = 200;
        Assert.Null(RayScene.AlphaMask.From(64, 64, px, 0.5f));
    }

    [Fact]
    public void A_ray_knows_which_side_of_a_surface_it_met()
    {
        var tris = new List<(Vector3, Vector3, Vector3)>();
        Quad(tris, 0, 0, 0, 10f);
        var scene = RayScene.Build(tris)!;
        Assert.True(scene.Trace(new Vector3(0, 5, 0), -Vector3.UnitY, out var top));
        Assert.False(top.BackFace);
        Assert.True(top.Normal.Y > 0.99f);
        Assert.True(scene.Trace(new Vector3(0, -5, 0), Vector3.UnitY, out var under));
        Assert.True(under.BackFace);
        Assert.True(under.Normal.Y < -0.99f);
    }

    [Fact]
    public void The_terrain_in_the_scene_is_the_terrain_ground_samples_are_placed_on()
    {
        var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 1f };
        var hm = new Heightmap(64, 64);
        var rnd = new Random(3);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                hm[x, y] = (ushort)(2000 + 600 * MathF.Sin(x * 0.3f) + 500 * MathF.Cos(y * 0.23f) + rnd.Next(0, 80));

        // A ground picture that is red on the west half and blue on the east.
        var px = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int i = (y * 32 + x) * 4;
                px[i] = (byte)(x < 16 ? 200 : 20); px[i + 1] = 20; px[i + 2] = (byte)(x < 16 ? 20 : 200); px[i + 3] = 255;
            }
        var built = LightScene.Build(hm, cfg, new Texture2D(32, 32, px), null, null);
        Assert.NotNull(built.Scene);
        Assert.Equal(63 * 63 * 2, built.Stats.TerrainTriangles);
        Assert.Equal(1, built.TerrainStride);

        for (int i = 0; i < 300; i++)
        {
            float wx = 2f + (float)rnd.NextDouble() * 240f, wz = 2f + (float)rnd.NextDouble() * 240f;
            var (h, n) = LightScene.Ground(hm, cfg, wx, wz, built.TerrainStride);
            Assert.True(built.Scene!.Trace(new Vector3(wx, 500f, wz), -Vector3.UnitY, out var hit, skip: 0f));
            Near(h, hit.Point.Y, 2e-3f);
            Assert.False(hit.BackFace);                               // seen from above it is the front
            Assert.True(Vector3.Dot(n, hit.Normal) > 0.999f);
            // A point lifted a hair off that surface sees the sky straight up, and the colour under it is the picture's.
            Assert.False(built.Scene.Occluded(new Vector3(wx, h, wz) + n * 0.01f, Vector3.UnitY));
            if (wx < 120f) Assert.True(hit.Albedo.X > hit.Albedo.Z * 5f);
            if (wx > 136f) Assert.True(hit.Albedo.Z > hit.Albedo.X * 5f);
        }
    }

    [Fact]
    public void A_large_terrain_is_thinned_to_its_triangle_budget()
    {
        Assert.Equal(1, LightScene.TerrainStrideFor(new Heightmap(1025, 1025)));
        Assert.Equal(2, LightScene.TerrainStrideFor(new Heightmap(2049, 2049)));
        Assert.Equal(4, LightScene.TerrainStrideFor(new Heightmap(4097, 4097)));
    }

    [Fact]
    public void Glass_and_portal_planes_let_light_through_and_cut_outs_are_tested()
    {
        var tex = new Texture2D(4, 4, new byte[64]);
        MeshLibrary.MaterialPart Part(bool alphaTest = false, bool blend = false, string? name = null, Texture2D? t = null, bool additive = false)
            => new(new[] { 0, 1, 2 }, Vector3.One, t, alphaTest, blend, name, Additive: additive);
        Assert.Equal(LightScene.Surface.Solid, LightScene.SurfaceOf(Part()));
        Assert.Equal(LightScene.Surface.None, LightScene.SurfaceOf(Part(blend: true)));
        Assert.Equal(LightScene.Surface.None, LightScene.SurfaceOf(Part(additive: true)));
        Assert.Equal(LightScene.Surface.None, LightScene.SurfaceOf(Part(alphaTest: true, name: "texture/portal_black", t: tex)));
        Assert.Equal(LightScene.Surface.Cutout, LightScene.SurfaceOf(Part(alphaTest: true, name: "texture/palm_leaf", t: tex)));
        Assert.Equal(LightScene.Surface.None, LightScene.SurfaceOf(Part(alphaTest: true, name: "texture/missing")));
    }
}
