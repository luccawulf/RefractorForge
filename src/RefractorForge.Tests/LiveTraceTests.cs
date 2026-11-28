using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The live traced view's kernel (<see cref="LiveTraceKernel"/>, the C# copy of the GPU compute shader) over its
/// packed scene (<see cref="LiveTraceScene"/>): it must find exactly what <see cref="RayScene"/> finds, cut-outs
/// included, and its samples must average to the light <see cref="LightTracer"/> and <see cref="LampLight"/> trace.
/// </summary>
public class LiveTraceTests
{
    private static void Near(float want, float got, float tol) => Assert.InRange(got, want - tol, want + tol);

    private static DaylightModel Day(Vector3 toSun, float sky = 0.2f, float peak = 1.05f)
        => new Daylight { ToSun = Vector3.Normalize(toSun), SkyStrength = sky, Peak = peak, GroundAlbedo = new Vector3(0.18f) }.Build();

    // Flat ground at 10 m with a 20 m block in the middle, plus a 10 m leaf card standing at x = 80 whose texture is
    // a checkerboard of holes - so rays can pass through half of it.
    private static LightScene.Built Scene()
    {
        var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 1f };
        var hm = new Heightmap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                hm[x, y] = cfg.MetersToRaw(x >= 28 && x <= 35 && y >= 28 && y <= 35 ? 30f : 10f);
        var terrain = LightScene.Build(hm, cfg, null, null, null);
        var flat = terrain.Scene!.ExportFlat();

        var tris = new List<(Vector3, Vector3, Vector3)>();
        var alb = new List<Vector3>();
        var cut = new List<RayScene.Cutout?>();
        for (int t = 0; t < flat.A.Length; t++)
        {
            tris.Add((flat.A[t], flat.A[t] + flat.E1[t], flat.A[t] + flat.E2[t]));
            alb.Add(flat.Albedo![t]); cut.Add(null);
        }
        int terrainTris = tris.Count;
        var px = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++) px[(y * 8 + x) * 4 + 3] = (byte)(((x + y) & 1) == 0 ? 255 : 0);
        var mask = RayScene.AlphaMask.From(8, 8, px, 0.5f)!;
        Vector3 a = new(80, 10, 100), b = new(80, 10, 140), c = new(80, 30, 100), d = new(80, 30, 140);
        tris.Add((a, b, c)); alb.Add(new Vector3(0.2f, 0.4f, 0.1f)); cut.Add(new RayScene.Cutout(mask, new(0, 0), new(1, 0), new(0, 1)));
        tris.Add((b, d, c)); alb.Add(new Vector3(0.2f, 0.4f, 0.1f)); cut.Add(new RayScene.Cutout(mask, new(1, 0), new(1, 1), new(0, 1)));
        var scene = RayScene.Build(tris, alb, cut);
        return new LightScene.Built(scene, new LightScene.Stats(terrainTris, 2, 2, 1, 1), 1);
    }

    [Fact]
    public void The_packed_scene_finds_exactly_what_the_cpu_scene_finds_cut_outs_included()
    {
        var built = Scene();
        var s = LiveTraceScene.From(built)!;
        Assert.Equal(2, s.Cuts.Length / 8);
        Assert.Equal(2, s.MaskBits.Length);              // one 8 x 8 mask, shared by both triangles: 64 bits
        Assert.Equal(-1f, s.Tris[15]);                   // a terrain triangle is solid
        Assert.Equal(0f, s.Tris[s.TerrainTris * 16 + 15]);

        var rng = new Random(7);
        int hits = 0, throughCard = 0;
        for (int i = 0; i < 4000; i++)
        {
            // Rays from the west toward the leaf card and the block, and some from above.
            var o = i % 2 == 0 ? new Vector3(40f, 12f + (float)rng.NextDouble() * 22f, 95f + (float)rng.NextDouble() * 50f)
                               : new Vector3((float)rng.NextDouble() * 256f, 60f, (float)rng.NextDouble() * 256f);
            var dir = Vector3.Normalize(i % 2 == 0 ? new Vector3(1f, (float)rng.NextDouble() * 0.2f - 0.1f, (float)rng.NextDouble() * 0.4f - 0.2f)
                                                   : new Vector3((float)rng.NextDouble() - 0.5f, -1f, (float)rng.NextDouble() - 0.5f));
            bool cpu = built.Scene!.Trace(o, dir, out var hit, skip: 0.02f);
            bool gpu = LiveTraceKernel.Trace(s, o, dir, float.MaxValue, 0.02f, out int tri, out float dist, out var p, out var n, out var al);
            Assert.Equal(cpu, gpu);
            if (cpu)
            {
                hits++;
                Assert.Equal(hit.Triangle, tri);
                Assert.Equal(hit.Distance, dist);
                Assert.Equal(hit.Normal, n);
                Assert.Equal(hit.Albedo, al);
                if (i % 2 == 0 && hit.Point.X > 81f && o.Y > 11f && o.Z > 101f && o.Z < 139f) throughCard++;
            }
            Assert.Equal(built.Scene.Occluded(o, dir), LiveTraceKernel.Occluded(s, o, dir, float.MaxValue, 0.02f));
            Assert.Equal(built.Scene.Occluded(o, dir, 30f, 0.01f), LiveTraceKernel.Occluded(s, o, dir, 30f, 0.01f));
        }
        Assert.True(hits > 3000);
        Assert.True(throughCard > 100, $"rays passed through the card's holes {throughCard} times");
    }

    [Fact]
    public void A_pixels_samples_average_to_the_light_the_cpu_tracer_traces()
    {
        var built = Scene();
        var s = LiveTraceScene.From(built)!;
        var day = Day(new Vector3(1f, 0.8f, 0.3f));
        var cam = new Camera { Position = new Vector3(60f, 30f, 60f), Yaw = 0.6f, Pitch = -0.45f, Aspect = 1.5f, MirrorX = true };
        var p = LiveTraceKernel.Params.From(day, null, cam.ViewProjection, cam.Position, 48, 32, null, 256f);

        // Pixels on open ground, on the ground in the block's shade, and on the block itself.
        int checkedPixels = 0;
        foreach (var (px, py) in new[] { (24, 6), (10, 10), (30, 14), (24, 20), (40, 4) })
        {
            LiveTraceKernel.PrimaryRay(p, px + 0.5f, py + 0.5f, out var o, out var dir);
            if (!built.Scene!.Trace(o, dir, out var hit, skip: 0f)) continue;
            var start = hit.Point + hit.Normal * 0.02f;
            var want = LightTracer.Gather(built.Scene, day, start, hit.Normal, new LightTracer.Budget(256, 4096, 2), 11u).Irradiance(day, hit.Normal);

            var sum = Vector3.Zero;
            const int frames = 6000;
            for (uint f = 0; f < frames; f++)
            {
                var smp = LiveTraceKernel.Shade(s, p, px, py, f, null);
                Assert.True(smp.Distance > 0f);
                Near(Vector3.Distance(hit.Point, cam.Position), smp.Distance, 1e-3f);
                sum += smp.Light;
            }
            var got = sum / frames;
            float lw = EngineLight.Luma(want), lg = EngineLight.Luma(got);
            Assert.True(MathF.Abs(lg - lw) <= 0.03f * lw + 0.004f, $"pixel {px},{py}: traced {lw:0.0000}, live {lg:0.0000}");
            checkedPixels++;
        }
        Assert.True(checkedPixels >= 4);
    }

    [Fact]
    public void A_lamps_light_averages_to_what_the_bake_puts_down()
    {
        var built = Scene();
        var s = LiveTraceScene.From(built)!;
        var night = new Daylight { ToSun = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.2f)), SkyStrength = 0.2f, Peak = 0.3f,
                                   GroundAlbedo = new Vector3(0.18f) }.Build();
        var rig = new LightRig();
        rig.Lights.Add(new PointLight { Position = new Vec3(60f, 16f, 120f), Radius = 30f, Intensity = 1.2f, SourceSize = 0.4f,
                                        ColorR = 1f, ColorG = 0.7f, ColorB = 0.4f, Falloff = 1.5f });
        rig.Lights.Add(new PointLight { Position = new Vec3(70f, 20f, 128f), Radius = 25f, Intensity = 1f, Kind = 1, ConeDeg = 80f,
                                        SpotPitchDeg = -90f, ColorR = 0.6f, ColorG = 0.8f, ColorB = 1f, OnObjects = false });
        var p = LiveTraceKernel.Params.From(night, rig, Matrix4x4.Identity, Vector3.Zero, 1, 1, null, 256f, bounces: 0);
        Assert.Equal(2, p.Lamps.Length);

        // The lamps alone at points on the ground and on the leaf card: the kernel's direct term against the bake's.
        foreach (var (q, n, ground) in new[] { (new Vector3(66f, 10f, 122f), Vector3.UnitY, true),
                                               (new Vector3(72f, 10f, 128f), Vector3.UnitY, true),
                                               (new Vector3(79.98f, 14f, 118f), -Vector3.UnitX, false) })
        {
            var want = LampLight.Display(built.Scene, rig, q, n, 512, ground, 3u);
            var sum = Vector3.Zero;
            const int n1 = 8000;
            for (uint i = 0; i < n1; i++) sum += LampOnly(s, p, q, n, ground, i);
            var got = sum / n1;
            Assert.True(EngineLight.Luma(want) > 0.02f);
            Near(EngineLight.Luma(want), EngineLight.Luma(got), 0.03f * EngineLight.Luma(want) + 0.003f);
        }
    }

    // The kernel's lamp term on its own, through the same code the pixel uses (reflection keeps it private).
    private static Vector3 LampOnly(LiveTraceScene s, LiveTraceKernel.Params p, Vector3 q, Vector3 n, bool ground, uint seed)
    {
        var m = typeof(LiveTraceKernel).GetMethod("Lamps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (Vector3)m.Invoke(null, new object[] { s, p, q, n, ground ? 1 : 2, seed })!;
    }

    [Fact]
    public void The_rasterised_colour_is_used_only_where_it_shows_the_same_surface_as_the_trace()
    {
        var built = Scene();
        var s = LiveTraceScene.From(built)!;
        var day = Day(new Vector3(1f, 0.8f, 0.3f));
        // Looking east at the leaf card from 40 m away.
        var cam = new Camera { Position = new Vector3(40f, 20f, 120f), Yaw = MathF.PI / 2f, Pitch = 0f, Aspect = 1f, MirrorX = true };
        var p = LiveTraceKernel.Params.From(day, null, cam.ViewProjection, cam.Position, 8, 8, null, 256f);
        // Find a pixel that lands on the card.
        for (int py = 0; py < 8; py++)
            for (int px = 0; px < 8; px++)
            {
                var plain = LiveTraceKernel.Shade(s, p, px, py, 0, null);
                if (plain.Distance < 0f || MathF.Abs(plain.Distance - 40f) > 3f) continue;
                var red = new Vector3(1f, 0f, 0f);
                Assert.Equal(red, LiveTraceKernel.Shade(s, p, px, py, 0, new LiveTraceKernel.Raster(red, true, plain.Distance + 0.1f)).Albedo);
                // Raster shows something 5 m nearer than the trace does: not the same surface, keep the triangle's colour.
                Assert.NotEqual(red, LiveTraceKernel.Shade(s, p, px, py, 0, new LiveTraceKernel.Raster(red, true, plain.Distance - 5f)).Albedo);
                return;
            }
        Assert.Fail("no pixel landed on the leaf card");
    }
}
