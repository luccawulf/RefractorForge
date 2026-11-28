using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The realistic bake end to end on scenes small enough to reason about: the ground in colour
/// (<see cref="GroundLightBake"/>), an object's lightmap solved against the engine's formula
/// (<see cref="ObjectLightmapBaker.BakePhysical"/>), placed lamps (<see cref="LampLight"/>), the preview picture
/// (<see cref="LightPreview"/>) and the level settings the result is written to.
/// </summary>
public class RealisticLightingTests
{
    private static void Near(float want, float got, float tol) => Assert.InRange(got, want - tol, want + tol);

    private static DaylightModel Day(Vector3 toSun, float sky = 0.2f, float peak = 1.05f)
        => new Daylight { ToSun = Vector3.Normalize(toSun), SkyStrength = sky, Peak = peak, GroundAlbedo = new Vector3(0.18f) }.Build();

    // ---- the ground ----------------------------------------------------------------------------------------

    // Flat ground at 10 m with a 20 m block standing in the middle, 256 m across.
    private static (Heightmap Hm, TerrainConfig Cfg) BlockOnPlain()
    {
        var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 1f };
        var hm = new Heightmap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                hm[x, y] = cfg.MetersToRaw(x >= 28 && x <= 35 && y >= 28 && y <= 35 ? 30f : 10f);
        return (hm, cfg);
    }

    [Fact]
    public void The_ground_takes_a_shadow_that_is_darker_and_bluer_than_the_sunlit_ground_beside_it()
    {
        var (hm, cfg) = BlockOnPlain();
        var built = LightScene.Build(hm, cfg, null, null, null);
        var day = Day(new Vector3(1f, 1f, 0f));                         // sun to the east, 45 degrees up
        var res = GroundLightBake.Bake(built.Scene!, day, hm, cfg, built.TerrainStride, 128, new LightTracer.Budget(6, 48, 1))!;

        Assert.InRange(res.ShadowShare, 0.004, 0.2);
        // The block is at x 112..144; with the sun in the east its shadow lies to the WEST of it, about 20 m long.
        var shade = GroundLightBake.DisplayAt(res, day, hm, cfg, 102f, 128f);
        var open = GroundLightBake.DisplayAt(res, day, hm, cfg, 40f, 40f);
        var east = GroundLightBake.DisplayAt(res, day, hm, cfg, 165f, 128f);
        float ratio = EngineLight.Luma(shade) / EngineLight.Luma(open);
        Assert.InRange(ratio, 0.3f, 0.8f);                               // where retail paints its shadows, not a hole
        Assert.True(shade.Z / shade.X > open.Z / open.X * 1.08f, "shade is lit by the sky, so it is bluer than sunlight");
        Assert.True(EngineLight.Luma(east) > EngineLight.Luma(open) * 0.85f, "no shadow on the sunward side");
        // Open, level ground is what the daylight model says open ground is.
        Near(EngineLight.Luma(day.ToDisplay(day.OpenGroundIrradiance)), EngineLight.Luma(open), 0.05f);
    }

    [Fact]
    public void Applying_the_bake_always_starts_from_the_ground_as_painted()
    {
        var (hm, cfg) = BlockOnPlain();
        var built = LightScene.Build(hm, cfg, null, null, null);
        var day = Day(new Vector3(1f, 1f, 0f));
        var res = GroundLightBake.Bake(built.Scene!, day, hm, cfg, built.TerrainStride, 128, new LightTracer.Budget(4, 24, 1))!;

        var painted = new byte[96 * 96 * 4];
        for (int i = 0; i < painted.Length; i += 4) { painted[i] = 150; painted[i + 1] = 130; painted[i + 2] = 90; painted[i + 3] = 255; }
        var original = new Texture2D(96, 96, painted);
        var once = new Texture2D(96, 96, new byte[painted.Length]);
        var twice = new Texture2D(96, 96, new byte[painted.Length]);
        GroundLightBake.Apply(once, original, res, day, hm, cfg);
        GroundLightBake.Apply(twice, original, res, day, hm, cfg);
        GroundLightBake.Apply(twice, original, res, day, hm, cfg);       // a second bake replaces, it does not stack
        Assert.Equal(once.Rgba, twice.Rgba);
        Assert.Equal(painted[0], original.Rgba[0]);                      // and the painted ground is never touched

        // The shadow is in the picture: a texel west of the block is darker than one in the open.
        int Px(float wx, float wz) => ((int)(wz / 256f * 96) * 96 + (int)(wx / 256f * 96)) * 4;
        Assert.True(once.Rgba[Px(102f, 128f)] < once.Rgba[Px(40f, 40f)] - 20);

        // With a floor, nothing is drawn darker than it - the ground under the block's edge included.
        var floored = new Texture2D(96, 96, new byte[painted.Length]);
        GroundLightBake.Apply(floored, original, res, day, hm, cfg, floor: new Vector3(0.6f));
        for (int i = 0; i < floored.Rgba.Length; i += 4) Assert.True(floored.Rgba[i] >= (int)(150 * 0.6f) - 1);
    }

    [Fact]
    public void The_shadow_bits_carry_the_same_shadow_the_ground_was_given()
    {
        var (hm, cfg) = BlockOnPlain();
        var built = LightScene.Build(hm, cfg, null, null, null);
        var day = Day(new Vector3(1f, 1f, 0f));
        var res = GroundLightBake.Bake(built.Scene!, day, hm, cfg, built.TerrainStride, 128, new LightTracer.Budget(4, 8, 0))!;
        var bits = GroundLightBake.ToShadowBits(res, gridDim: 2, tilePx: 64);
        Assert.Equal(2, bits.GridDim);
        long set = 0, all = 0;
        foreach (var t in bits.Tiles) { var r = t.ToRaster(); foreach (var b in r) { if (b != 0) set++; all++; } }
        Assert.Equal(128L * 128L, all);
        Assert.InRange(set / (double)all, res.ShadowShare - 0.02, res.ShadowShare + 0.02);   // the bit is SHADOW
        // And it packs and unpacks to itself, as every .lsb must.
        var again = LightmapShadowBits.Decode(bits.Encode());
        Assert.Equal(bits.Encode(), again.Encode());
    }

    // ---- an object's lightmap ----------------------------------------------------------------------------------

    // A floor 8 m square (lightmap left half) with a roof over its western half, 3 m up (lightmap right half). Both
    // wound the way Refractor winds: clockwise seen from above, so cross(b-a, c-a) points down and "out" is up.
    private static MeshLibrary.Mesh FloorAndRoof(bool emptyUvs = false)
    {
        var pos = new Vector3[]
        {
            new(0, 0, 0), new(8, 0, 0), new(0, 0, 8), new(8, 0, 8),
            new(0, 3, 0), new(4, 3, 0), new(0, 3, 8), new(4, 3, 8),
        };
        var lm = emptyUvs
            ? new Vector2[8]
            : new Vector2[] { new(0.02f, 0.02f), new(0.48f, 0.02f), new(0.02f, 0.98f), new(0.48f, 0.98f),
                              new(0.52f, 0.02f), new(0.98f, 0.02f), new(0.52f, 0.98f), new(0.98f, 0.98f) };
        int[] idx = { 0, 1, 2, 1, 3, 2, 4, 5, 6, 5, 7, 6 };
        return new MeshLibrary.Mesh(pos, new Vector2[8], new[] { new MeshLibrary.MaterialPart(idx, Vector3.One, null, false) }) { LightmapUvs = lm };
    }

    private static RayScene SceneOf(MeshLibrary.Mesh m, float groundHalf = 200f)
    {
        var tris = new List<(Vector3, Vector3, Vector3)>();
        var alb = new List<Vector3>();
        var idx = m.Parts[0].Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3) { tris.Add((m.Positions[idx[t]], m.Positions[idx[t + 1]], m.Positions[idx[t + 2]])); alb.Add(new Vector3(0.3f)); }
        // Open ground all round, a little below the floor.
        Vector3 a = new(-groundHalf, -0.05f, -groundHalf), b = new(groundHalf, -0.05f, -groundHalf), c = new(-groundHalf, -0.05f, groundHalf), d = new(groundHalf, -0.05f, groundHalf);
        tris.Add((a, b, c)); alb.Add(new Vector3(0.18f)); tris.Add((b, d, c)); alb.Add(new Vector3(0.18f));
        return RayScene.Build(tris, alb)!;
    }

    private static double MeanOf(Texture2D t, float u0, float u1, float v0 = 0.1f, float v1 = 0.9f)
    {
        double s = 0; int n = 0;
        for (int y = (int)(v0 * t.Height); y < (int)(v1 * t.Height); y++)
            for (int x = (int)(u0 * t.Width); x < (int)(u1 * t.Width); x++) { s += t.Rgba[(y * t.Width + x) * 4]; n++; }
        return s / n / 255.0;
    }

    [Fact]
    public void A_traced_lightmap_makes_the_engine_draw_the_traced_light()
    {
        var mesh = FloorAndRoof();
        var scene = SceneOf(mesh);
        var day = Day(new Vector3(0.15f, 1f, 0.1f));                    // nearly overhead, so the roof shades what is under it
        var colours = day.Fit().Colours;
        var ph = new ObjectLightmapBaker.Physical(scene, day, colours, new LightTracer.Budget(16, 96, 2));
        var res = ObjectLightmapBaker.BakePhysical(mesh, Matrix4x4.Identity, 96, 2, ph)!;

        // The floor's lightmap is the left half: its western half (u 0.02..0.25) lies under the roof.
        double under = MeanOf(res.Map, 0.05f, 0.20f), open = MeanOf(res.Map, 0.32f, 0.45f), roofTop = MeanOf(res.Map, 0.58f, 0.92f);
        Assert.True(under < open * 0.6, $"under the roof {under:0.00} against open floor {open:0.00}");
        Assert.True(under > 0.01, "shade is not written as black: the sky and the bounce reach it, and the engine's lights are scaled to match");
        Assert.True(roofTop > 0.5, $"the roof's top is in full sun ({roofTop:0.00})");
        // The point of the solve: what the engine draws from this map is what was traced, to within a few percent.
        Assert.True(res.MeanError < 0.03f, $"mean distance from the traced light {res.MeanError:0.000}");
        Assert.True(res.AtCeiling < 0.05f, $"{res.AtCeiling:P0} of an upward-facing mesh is out of the lights' reach");

        // Check one texel by hand: open floor, facing up.
        float p = (float)open;
        var drawn = EngineLight.Lightmapped(p, Vector3.UnitY, day.ToSun, colours);
        var traced = LightTracer.Gather(scene, day, new Vector3(6.5f, 0.02f, 4f), Vector3.UnitY, new LightTracer.Budget(32, 512, 2), 9);
        Near(MathF.Min(1f, EngineLight.Luma(day.ToDisplay(traced.Irradiance(day, Vector3.UnitY)))), EngineLight.Luma(drawn), 0.06f);
    }

    [Fact]
    public void A_lightmap_only_copy_is_solved_per_channel_and_the_map_is_the_light()
    {
        // selfillum 1 on a mesh without a tangent frame: the game's vertex colour clamps at 1, so the lightmap is
        // all the light there is - on every face - and the engine draws exactly what was traced.
        var plain = FloorAndRoof();
        var mesh = plain with { Parts = plain.Parts.Select(p => p with { SelfIllum = Vector3.One }).ToArray(), VertexLit = true };
        var scene = SceneOf(mesh);
        var day = Day(new Vector3(0.15f, 1f, 0.1f));
        var colours = day.Fit().Colours;
        var ph = new ObjectLightmapBaker.Physical(scene, day, colours, new LightTracer.Budget(16, 96, 2), VertexLit: true);
        var res = ObjectLightmapBaker.BakePhysical(mesh, Matrix4x4.Identity, 96, 2, ph)!;
        Assert.True(res.Colour, "a per-vertex mesh's map carries colour");
        Assert.True(res.MeanError < 0.005f, $"mean distance from the traced light {res.MeanError:0.0000}");
        Assert.True(res.AtCeiling < 0.01f, $"{res.AtCeiling:P0} out of reach above");

        // By hand, in the open: map + LMambient is the traced light, whatever the two lights are.
        var traced = LightTracer.Gather(scene, day, new Vector3(6.5f, 0.02f, 4f), Vector3.UnitY, new LightTracer.Budget(32, 512, 2), 9);
        float want = MathF.Min(1f, EngineLight.Luma(day.ToDisplay(traced.Irradiance(day, Vector3.UnitY))));
        float open = (float)MeanOf(res.Map, 0.32f, 0.45f);
        Near(want, open + EngineLight.Luma(colours.LmAmbient), 0.06f);

        // The same mesh under lights a tenth as strong draws the same picture: the lights no longer matter.
        var dim = colours with { Sun = colours.Sun * 0.1f, Back = colours.Back * 0.1f };
        var res2 = ObjectLightmapBaker.BakePhysical(mesh, Matrix4x4.Identity, 96, 2, ph with { Colours = dim })!;
        Near(open, (float)MeanOf(res2.Map, 0.32f, 0.45f), 0.01f);
    }

    [Fact]
    public void A_mesh_with_no_unwrap_gets_no_map_and_one_value_that_knows_where_it_stands()
    {
        var blank = FloorAndRoof(emptyUvs: true);
        var scene = SceneOf(blank);
        var day = Day(new Vector3(0.15f, 1f, 0.1f));
        var ph = new ObjectLightmapBaker.Physical(scene, day, day.Fit().Colours, new LightTracer.Budget(8, 64, 1));
        Assert.Null(ObjectLightmapBaker.BakePhysical(blank, Matrix4x4.Identity, 64, 1, ph));

        float here = ObjectLightmapBaker.AveragePhysical(blank, Matrix4x4.Identity, ph);
        Assert.InRange(here, 0.05f, 1f);
        // The same object put under a big slab: one value still, and a much lower one.
        var tris = new List<(Vector3, Vector3, Vector3)>();
        var idx = blank.Parts[0].Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3) tris.Add((blank.Positions[idx[t]], blank.Positions[idx[t + 1]], blank.Positions[idx[t + 2]]));
        Vector3 a = new(-60, 6, -60), b = new(60, 6, -60), c = new(-60, 6, 60), d = new(60, 6, 60);
        tris.Add((a, b, c)); tris.Add((b, d, c));
        var covered = ph with { Scene = RayScene.Build(tris)! };
        float under = ObjectLightmapBaker.AveragePhysical(blank, Matrix4x4.Identity, covered);
        Assert.True(under < here * 0.5f, $"under a slab {under:0.00}, in the open {here:0.00}");
    }

    [Fact]
    public void Tracing_the_soft_light_on_every_other_texel_gives_the_same_map()
    {
        var mesh = FloorAndRoof();
        var scene = SceneOf(mesh);
        var day = Day(new Vector3(0.15f, 1f, 0.1f));
        var ph = new ObjectLightmapBaker.Physical(scene, day, day.Fit().Colours, new LightTracer.Budget(16, 128, 2));
        var dense = ObjectLightmapBaker.BakePhysical(mesh, Matrix4x4.Identity, 96, 2, ph with { SoftStride = 1 })!;
        var sparse = ObjectLightmapBaker.BakePhysical(mesh, Matrix4x4.Identity, 96, 2, ph with { SoftStride = 2 })!;
        double diff = 0; int n = 0, worst = 0;
        for (int i = 0; i < dense.Map.Rgba.Length; i += 4)
        {
            int d = Math.Abs(dense.Map.Rgba[i] - sparse.Map.Rgba[i]);
            diff += d; n++; worst = Math.Max(worst, d);
        }
        Assert.True(diff / n / 255.0 < 0.01, $"mean difference {diff / n / 255.0:0.0000}");
        Assert.True(worst < 60, $"worst texel differs by {worst} of 255");
        // And it is still the traced light the engine draws.
        Assert.True(sparse.MeanError < 0.03f, $"mean distance from the traced light {sparse.MeanError:0.000}");
    }

    [Fact]
    public void Older_bakes_are_taken_back_out_of_the_ground_by_their_records()
    {
        // Sand-coloured ground; an older bake darkened a band of it to half (the shadow record) and lit a pool that
        // clips to white in its middle (the pool record, stored as 255 / gain).
        const int n = 64;
        var painted = new byte[n * n * 4];
        var rnd = new Random(5);
        for (int i = 0; i < painted.Length; i += 4)
        {
            int j = rnd.Next(-6, 7);
            painted[i] = (byte)(170 + j); painted[i + 1] = (byte)(140 + j); painted[i + 2] = (byte)(95 + j); painted[i + 3] = 255;
        }
        var shadow = new byte[n * n * 4]; var pool = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int o = (y * n + x) * 4;
                byte k = (byte)(x < 16 ? 128 : 255);                                   // the west band: x0.5
                shadow[o] = shadow[o + 1] = shadow[o + 2] = k; shadow[o + 3] = 255;
                float d = MathF.Sqrt((x - 44) * (x - 44) + (y - 32) * (y - 32));
                float gain = 1f + MathF.Max(0f, 1f - d / 12f) * 3f;                     // up to x4 at the centre
                byte f = (byte)Math.Clamp((int)(255f / gain + 0.5f), 1, 255);
                pool[o] = pool[o + 1] = pool[o + 2] = f; pool[o + 3] = 255;
            }
        var shadowTex = new Texture2D(n, n, shadow); var poolTex = new Texture2D(n, n, pool);
        var baked = new Texture2D(n, n, (byte[])painted.Clone());
        LightBake.MultiplyFactorIntoAtlas(baked, shadowTex);
        LightBake.ApplyPoolFactor(baked, poolTex);
        Assert.Equal(255, baked.Rgba[(32 * n + 44) * 4]);                               // the pool's middle clipped

        var back = GroundLightBake.Unbaked(baked, shadowTex, poolTex);
        Assert.Equal(painted.Length, back.Rgba.Length);
        double err = 0; int worst = 0;
        for (int i = 0; i < painted.Length; i += 4)
        {
            int d = Math.Abs(painted[i] - back.Rgba[i]);
            err += d; worst = Math.Max(worst, d);
        }
        Assert.True(err / (n * n) < 3.0, $"mean error {err / (n * n):0.0} levels");
        // The clipped middle comes back ground-coloured, not as the dark blot plain division leaves (255 / 4 = 64).
        Assert.InRange(back.Rgba[(32 * n + 44) * 4], 150, 190);
        Assert.True(worst < 30, $"worst texel {worst} levels off");
        // With no records there is nothing to take out.
        Assert.Equal(baked.Rgba, GroundLightBake.Unbaked(baked, null, null).Rgba);
    }

    // ---- lamps -------------------------------------------------------------------------------------------------

    private static LightRig OneLamp(float x = 0f, float y = 6f, float z = 0f)
        => new() { Lights = { new PointLight { Position = new Vec3(x, y, z), Radius = 20f, Intensity = 1.2f, SourceSize = 0.3f } } };

    [Fact]
    public void A_lamp_lights_what_it_can_see_and_a_fence_throws_its_own_shadow()
    {
        var rig = OneLamp();
        var up = Vector3.UnitY;
        // Open air: bright under the lamp, nothing past its reach, nothing on a face turned away.
        Assert.True(EngineLight.Luma(LampLight.Display(null, rig, new Vector3(1, 0, 0), up, 4, true, 1)) > 0.3f);
        Assert.Equal(Vector3.Zero, LampLight.Display(null, rig, new Vector3(30, 0, 0), up, 4, true, 1));
        Assert.Equal(Vector3.Zero, LampLight.Display(null, rig, new Vector3(1, 0, 0), -up, 4, true, 1));
        // The lamps flagged off for the ground do not reach the ground - and still bounce (null = every lamp).
        rig.Lights[0].OnGround = false;
        Assert.Equal(Vector3.Zero, LampLight.Display(null, rig, new Vector3(1, 0, 0), up, 4, true, 1));
        Assert.True(EngineLight.Luma(LampLight.Display(null, rig, new Vector3(1, 0, 0), up, 4, null, 1)) > 0.3f);
        rig.Lights[0].OnGround = true;

        // A solid sheet between lamp and ground: dark. The same sheet as a half-open cut-out: about half.
        Vector3 a = new(-9, 3, -9), b = new(9, 3, -9), c = new(-9, 3, 9), d = new(9, 3, 9);
        var tris = new List<(Vector3, Vector3, Vector3)> { (a, b, c), (b, d, c) };
        Assert.Equal(Vector3.Zero, LampLight.Display(RayScene.Build(tris), rig, new Vector3(1, 0, 0), up, 8, true, 1));
        var px = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) px[(y * 32 + x) * 4 + 3] = (byte)((((x / 2) + (y / 2)) & 1) == 0 ? 255 : 0);
        var mask = RayScene.AlphaMask.From(32, 32, px, 0.5f)!;
        var cuts = new List<RayScene.Cutout?>
        {
            new RayScene.Cutout(mask, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)),
            new RayScene.Cutout(mask, new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)),
        };
        var fence = RayScene.Build(tris, null, cuts)!;
        double sum = 0; int n = 0;
        float full = EngineLight.Luma(LampLight.Display(null, rig, new Vector3(1, 0, 0), up, 1, true, 1));
        for (float x = -3; x <= 3; x += 0.21f) for (float z = -3; z <= 3; z += 0.21f)
        {
            float f = EngineLight.Luma(LampLight.Display(null, rig, new Vector3(x, 0, z), up, 1, true, 1));
            float g = EngineLight.Luma(LampLight.Display(fence, rig, new Vector3(x, 0, z), up, 16, true, (uint)n));
            if (f > 0.05f) { sum += g / f; n++; }
        }
        Assert.InRange(sum / n, 0.3, 0.7);
        Assert.True(full > 0f);
    }

    [Fact]
    public void A_lamp_is_not_shadowed_by_the_fixture_it_hangs_in()
    {
        // A slab 10 cm above the lamp and the lamp head's own shade just beside it: neither stands between the bulb
        // and the floor, and a sheet 20 cm BELOW the bulb - inside the fixture's allowance - does not either.
        var rig = OneLamp(0, 6, 0);
        Vector3 a = new(-0.4f, 5.8f, -0.4f), b = new(0.4f, 5.8f, -0.4f), c = new(-0.4f, 5.8f, 0.4f), d = new(0.4f, 5.8f, 0.4f);
        var cup = RayScene.Build(new List<(Vector3, Vector3, Vector3)> { (a, b, c), (b, d, c) })!;
        Assert.True(EngineLight.Luma(LampLight.Display(cup, rig, new Vector3(0, 0, 0), Vector3.UnitY, 8, true, 3)) > 0.3f);
        // A real ceiling a metre under it does block it.
        Vector3 e = new(-9, 5f, -9), f = new(9, 5f, -9), g = new(-9, 5f, 9), h = new(9, 5f, 9);
        var slab = RayScene.Build(new List<(Vector3, Vector3, Vector3)> { (e, f, g), (f, h, g) })!;
        Assert.Equal(Vector3.Zero, LampLight.Display(slab, rig, new Vector3(0, 0, 0), Vector3.UnitY, 8, true, 3));
    }

    [Fact]
    public void Lamp_light_bounces_off_the_ground_it_falls_on()
    {
        // Night: next to no sun or sky. A ceiling-like face 3 m over lamp-lit ground gets its light from the pool.
        var night = new Daylight { ToSun = Vector3.Normalize(new Vector3(0.4f, 0.7f, 0.3f)), SkyStrength = 0.3f, Peak = 0.2f, GroundAlbedo = new Vector3(0.3f) }.Build();
        Vector3 a = new(-200, 0, -200), b = new(200, 0, -200), c = new(-200, 0, 200), d = new(200, 0, 200);
        var tris = new List<(Vector3, Vector3, Vector3)> { (a, b, c), (b, d, c) };
        var ground = RayScene.Build(tris, tris.Select(_ => new Vector3(0.3f)).ToList())!;
        var rig = OneLamp(0, 6, 0);
        var budget = new LightTracer.Budget(4, 512, 1);
        var without = LightTracer.Gather(ground, night, new Vector3(2, 3, 0), -Vector3.UnitY, budget, 4);
        var with = LightTracer.Gather(ground, night, new Vector3(2, 3, 0), -Vector3.UnitY, budget, 4, rig);
        Assert.True(EngineLight.Luma(with.Indirect) > EngineLight.Luma(without.Indirect) * 2f,
                    $"with the lamp {EngineLight.Luma(with.Indirect):0.0000}, without {EngineLight.Luma(without.Indirect):0.0000}");
        // And a lamp joins the moonlight as light does: on the screen's scale it lifts dark ground a lot and bright
        // ground a little, never by simply adding.
        var lampOnScreen = new Vector3(0.5f);
        var dark = night.ToDisplay(night.FromDisplay(new Vector3(0.1f)) + night.FromDisplay(lampOnScreen));
        var bright = night.ToDisplay(night.FromDisplay(new Vector3(0.8f)) + night.FromDisplay(lampOnScreen));
        Assert.InRange(dark.X, 0.5f, 0.52f);
        Assert.InRange(bright.X, 0.8f, 0.93f);
    }

    // ---- the preview -------------------------------------------------------------------------------------------

    [Fact]
    public void The_preview_renders_the_level_and_the_same_picture_every_time()
    {
        var (hm, cfg) = BlockOnPlain();
        var built = LightScene.Build(hm, cfg, null, null, null, keepSources: true);
        var prep = LightPreview.Prepare(built, null, null, null, cfg)!;
        var day = Day(new Vector3(1f, 1f, 0.2f));
        var view = new LightPreview.Camera(new Vector3(60, 40, 60), Vector3.Normalize(new Vector3(1, -0.35f, 1)), Vector3.UnitY, 60f, 48, 32);
        var a = new byte[48 * 32 * 4]; var b = new byte[48 * 32 * 4];
        LightPreview.Render(prep, day, view, a, new LightTracer.Budget(2, 8, 1));
        LightPreview.Render(prep, day, view, b, new LightTracer.Budget(2, 8, 1));
        Assert.Equal(a, b);
        // Sky at the top, ground at the bottom, and they are not the same colour; nothing is left unwritten.
        int top = 24 * 4, bottom = (31 * 48 + 24) * 4;
        Assert.Equal(255, a[top + 3]);
        Assert.True(a[top + 2] > a[top], "the sky is blue");
        Assert.True(Math.Abs(a[top] - a[bottom]) + Math.Abs(a[top + 2] - a[bottom + 2]) > 20);
        // A coarse pass fills whole blocks.
        var coarse = new byte[48 * 32 * 4];
        LightPreview.Render(prep, day, view, coarse, new LightTracer.Budget(1, 4, 0), step: 4);
        Assert.Equal(coarse[0], coarse[4]); Assert.Equal(coarse[0], coarse[48 * 4]);
        Assert.Equal(255, coarse[^1]);
    }

    [Fact]
    public void The_preview_puts_things_on_the_same_side_of_the_picture_as_the_mirrored_viewport()
    {
        // The editor's viewport reflects the view left-right (Camera.MirrorX) so it matches the game; the preview
        // must too, or it shows the scene flipped. The block sits at x 112..144, z 112..144: look at it from the
        // south-west with it off to one side and see which half of the picture it lands in.
        var (hm, cfg) = BlockOnPlain();
        var built = LightScene.Build(hm, cfg, null, null, null, keepSources: true);
        var prep = LightPreview.Prepare(built, null, null, null, cfg)!;
        var day = Day(new Vector3(0.2f, 1f, 0.2f));
        var editor = new RefractorForge.Render.Camera { Position = new Vector3(100f, 25f, 40f), Yaw = 0f, Pitch = -0.1f,
                                                        Aspect = 1.5f, MirrorX = true };
        var clip = Vector4.Transform(new Vector4(128f, 28f, 128f, 1f), editor.ViewProjection);
        bool blockOnRight = clip.X / clip.W > 0f;

        const int w = 60, h = 40;
        var view = new LightPreview.Camera(editor.Position, editor.Forward, Vector3.UnitY, editor.FovY * 180f / MathF.PI, w, h, editor.MirrorX);
        var px = new byte[w * h * 4];
        LightPreview.Render(prep, day, view, px, new LightTracer.Budget(1, 0, 0));
        // The block stands above the horizon line the plain makes; count its (non-sky) pixels in each half of a row
        // a little above the middle, where the sky is everywhere else.
        int row = h / 2 - 4, left = 0, right = 0;
        var sky = (px[(row * w) * 4], px[(row * w) * 4 + 2]);
        for (int x = 0; x < w; x++)
        {
            int o = (row * w + x) * 4;
            bool notSky = Math.Abs(px[o] - px[(row * w + (x < w / 2 ? w - 1 : 0)) * 4]) + Math.Abs(px[o + 2] - px[(row * w + (x < w / 2 ? w - 1 : 0)) * 4 + 2]) > 30;
            if (notSky) { if (x < w / 2) left++; else right++; }
        }
        Assert.True(left + right > 0, "the block is in the picture");
        Assert.Equal(blockOnRight, right > left);
    }

    // ---- the level's settings ----------------------------------------------------------------------------------

    private static readonly string[] Init =
    {
        "rem *** a level ***",
        "game.setDefaultGameMode GPM_CQ",
        "renderer.diffuseColor .975/1/.95",
        "renderer.ambientColor .1/.1/.1",
        "renderer.SecondaryDiffuseColor .55/.55/.525",
        "renderer.LMambientColor .25",
        "renderer.specularColor .9/.9/.7",
        "shadow.shadowColor .05/.05/.0375/.6",
        "run Init/Terrain",
    };

    [Fact]
    public void The_second_light_and_the_moving_shadow_colour_are_read_and_left_alone_unless_asked()
    {
        var e = EnvironmentSettings.Parse(null, null, Init);
        Assert.True(e.HasSecondaryDiffuse);
        Near(0.55f, e.SecondaryDiffuseColor.X, 1e-4f); Near(0.525f, e.SecondaryDiffuseColor.Z, 1e-4f);
        Assert.True(e.HasShadowColor);
        Near(0.0375f, e.ShadowColor.Z, 1e-5f); Near(0.6f, e.ShadowAlpha, 1e-5f);
        // The single-number form retail also writes.
        var one = EnvironmentSettings.Parse(null, null, new[] { "renderer.SecondaryDiffuseColor .4" });
        Near(0.4f, one.SecondaryDiffuseColor.Y, 1e-5f);

        // A save that did not ask for them leaves those three lines exactly as the level wrote them.
        var same = e.PatchInitConLines(Init);
        Assert.Equal(Init.Length, same.Count);
        Assert.Equal(Init[4], same[4]);
        Assert.Equal(Init[5], same[5]);
        Assert.Equal(Init[7], same[7]);
    }

    [Fact]
    public void A_bake_s_light_colours_are_written_in_place_or_added_beside_the_others()
    {
        var e = EnvironmentSettings.Parse(null, null, Init);
        e.SecondaryDiffuseColor = new Vec3(0.3f, 0.35f, 0.45f);
        e.LMAmbientColor = new Vec3(0.2f, 0.21f, 0.18f);
        e.WriteLightColours = true;
        e.ShadowAlpha = 0.48f; e.WriteShadowColor = true;
        var outLines = e.PatchInitConLines(Init);
        Assert.Equal(Init.Length, outLines.Count);
        Assert.Equal("renderer.SecondaryDiffuseColor 0.3/0.35/0.45", outLines[4]);
        Assert.Equal("renderer.LMambientColor 0.2/0.21/0.18", outLines[5]);
        Assert.Equal("shadow.shadowColor 0.05/0.05/0.0375/0.48", outLines[7]);
        Assert.StartsWith("renderer.diffuseColor ", outLines[2]);          // the sun's own line keeps its place

        // A level that never had them gets them next to its renderer lines, above the terrain.
        var bare = new[] { "game.setDefaultGameMode GPM_CQ", "renderer.diffuseColor .9/.9/.9", "run Init/Terrain" };
        var b = EnvironmentSettings.Parse(null, null, bare);
        b.SecondaryDiffuseColor = new Vec3(0.3f, 0.3f, 0.3f); b.LMAmbientColor = new Vec3(0.2f, 0.2f, 0.2f); b.WriteLightColours = true;
        var added = b.PatchInitConLines(bare);
        Assert.Equal(5, added.Count);
        Assert.Equal("renderer.diffuseColor 0.9/0.9/0.9", added[1]);        // a declared key is written back in the editor's own form
        Assert.Contains("renderer.SecondaryDiffuseColor 0.3/0.3/0.3", added);
        Assert.Contains("renderer.LMambientColor 0.2/0.2/0.2", added);
        Assert.Equal("run Init/Terrain", added[^1]);
    }

    [Fact]
    public void The_daylight_settings_ride_the_lights_sidecar()
    {
        var rig = new LightRig { Daylight = new DaylightSettings { SkyStrength = 0.45f, Brightness = 0.3f, Baked = true } };
        var back = LightRig.FromJson(rig.ToJson());
        Assert.NotNull(back.Daylight);
        Near(0.45f, back.Daylight!.SkyStrength, 1e-6f);
        Near(0.3f, back.Daylight.Brightness, 1e-6f);
        Assert.True(back.Daylight.Baked);
        // A sidecar from before this existed opens with none.
        Assert.Null(LightRig.FromJson("{\"Lights\":[]}").Daylight);
    }

    // ---- Battlefield 1942 --------------------------------------------------------------------------------------

    [Fact]
    public void Bf1942_formulas_give_what_its_calibration_level_showed()
    {
        // rf_calib42: a 24-bit lightmap of level 64 tinted (1, .5, .75) came out 129 / 64 / 97, and 128 at 253 / 129 / 192.
        var a = EngineLight.Bf1942.Lightmapped(new Vector3(1f, 0.5f, 0.75f) * (64f / 255f)) * 255f;
        Assert.InRange(a.X, 126f, 131f); Assert.InRange(a.Y, 62f, 66f); Assert.InRange(a.Z, 94f, 99f);
        Assert.Equal(Vector3.One, EngineLight.Bf1942.Lightmapped(new Vector3(0.75f)));
        // A mesh with no lightmap channel under a red sun of 0.5 and ambients of 0.25 green + 0.25 blue: 255 / 127 / 127
        // facing the sun, 0 / 127 / 127 facing away, and half the red with materialDiffuse 0.5.
        var sun = Vector3.Normalize(new Vector3(-0.75f, 0.46f, 0.46f));
        var dif = new Vector3(0.5f, 0f, 0f); var amb = new Vector3(0f, 0.25f, 0.25f);
        Assert.True((new Vector3(1f, 0.5f, 0.5f) - EngineLight.Bf1942.Dynamic(sun, sun, dif, amb, Vector3.One)).Length() < 1e-4f);
        Assert.Equal(new Vector3(0f, 0.5f, 0.5f), EngineLight.Bf1942.Dynamic(-sun, sun, dif, amb, Vector3.One));
        Assert.Equal(0.5f, EngineLight.Bf1942.Dynamic(sun, sun, dif, amb, new Vector3(0.5f)).X, 3);
        // The solve is its inverse, and never writes more than the game can show.
        var t = new Vector3(0.3f, 0.6f, 1.4f);
        Assert.Equal(Vector3.Min(t, Vector3.One), EngineLight.Bf1942.Lightmapped(EngineLight.Bf1942.SolveLightmap(t)));
    }

    [Fact]
    public void A_bf1942_lightmap_is_half_the_traced_light()
    {
        var mesh = FloorAndRoof();
        var scene = SceneOf(mesh);
        var day = Day(new Vector3(0.15f, 1f, 0.1f));
        var ph = new ObjectLightmapBaker.Physical(scene, day, default, new LightTracer.Budget(16, 96, 2)) { Bf1942 = true };
        var res = ObjectLightmapBaker.BakePhysical(mesh, Matrix4x4.Identity, 96, 2, ph)!;

        Assert.True(res.Colour, "a Battlefield 1942 map is written in colour");
        double under = MeanOf(res.Map, 0.05f, 0.20f), open = MeanOf(res.Map, 0.32f, 0.45f), roofTop = MeanOf(res.Map, 0.58f, 0.92f);
        Assert.True(under < open * 0.6, $"under the roof {under:0.00} against open floor {open:0.00}");
        Assert.True(under > 0.005, "shade holds the sky and the bounce, not black");
        // The roof's top sees the whole sky and the sun: the map there is half of what the open daylight shows.
        float want = MathF.Min(1f, day.OpenDisplay(Vector3.UnitY).X) * 0.5f;
        Assert.InRange(roofTop, want - 0.04, want + 0.04);
        // Nothing above 0.5 (128): the game doubles it, and more would only clip.
        for (int i = 0; i < res.Map.Rgba.Length; i += 4)
            Assert.True(res.Map.Rgba[i] <= 128 && res.Map.Rgba[i + 1] <= 128 && res.Map.Rgba[i + 2] <= 128);
        Assert.True(res.MeanError < 0.03f, $"mean distance from the traced light {res.MeanError:0.000}");
    }

    [Fact]
    public void The_bf1942_fit_puts_unlightmapped_objects_near_the_daylight()
    {
        var day = Day(Vector3.Normalize(new Vector3(-0.75f, 0.46f, 0.46f)));
        var (dif, amb) = day.FitBf1942();
        Assert.True(EngineLight.Luma(amb) > 0.01f && EngineLight.Luma(dif) > 0.05f);
        Assert.True(MathF.Max(dif.X, MathF.Max(dif.Y, dif.Z)) <= 0.61f, "written at half of what the screen shows");
        // Ground in the open and a wall turned from the sun both land near what the daylight says.
        foreach (var n in new[] { Vector3.UnitY, Vector3.Normalize(new Vector3(0.75f, 0f, -0.46f)), day.ToSun })
        {
            float got = EngineLight.Luma(EngineLight.Bf1942.Dynamic(n, day.ToSun, dif, amb, Vector3.One));
            float want = MathF.Min(1f, EngineLight.Luma(day.OpenDisplay(n)));
            Assert.InRange(got, want - 0.2f, want + 0.2f);
        }
    }
}
