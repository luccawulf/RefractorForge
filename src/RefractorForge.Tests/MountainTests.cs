using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Shape gates for the terrain sculptor. "Looks nice" is not directly testable, but the things that make a
/// generated mountain look WRONG are: a hard edge where it meets the existing ground, a perfectly circular
/// footprint, a smooth cone with no detail, and digging a trench into terrain it was supposed to sit on. Each of
/// those is checked here, plus a hillshaded preview written to disk for an actual look.
/// </summary>
public class MountainTests
{
    private static (Heightmap Hm, TerrainConfig Cfg) Flat(ushort fill = 2000)
    {
        var cfg = new TerrainConfig { WorldSize = 2048, MaterialSize = 512, YScale = 1f };
        var hm = new Heightmap(512, 512);
        for (int y = 0; y < 512; y++)
            for (int x = 0; x < 512; x++)
                hm[x, y] = fill;
        return (hm, cfg);
    }

    [Fact]
    public void Outside_the_footprint_the_terrain_is_untouched()
    {
        var (hm, cfg) = Flat();
        MountainGenerator.Raise(hm, cfg, 1024, 1024, radius: 200, peakMetres: 80, seed: 1);

        // Well clear of the widest the ridge warp can reach.
        Assert.Equal(2000, hm[10, 10]);
        Assert.Equal(2000, hm[500, 500]);
        Assert.Equal(2000, hm[256, 20]);
    }

    [Fact]
    public void It_never_digs_into_the_ground_it_sits_on()
    {
        var (hm, cfg) = Flat();
        MountainGenerator.Raise(hm, cfg, 1024, 1024, 250, 80, seed: 3, roughness: 1f, ridges: 7);

        for (int y = 0; y < 512; y++)
            for (int x = 0; x < 512; x++)
                Assert.True(hm[x, y] >= 2000, $"cell {x},{y} was cut below the original ground");
    }

    [Fact]
    public void The_summit_reaches_roughly_the_height_asked_for()
    {
        var (hm, cfg) = Flat();
        var (x0, y0, w, h) = MountainGenerator.Raise(hm, cfg, 1024, 1024, 250, 80, seed: 5);
        float peak = MountainGenerator.PeakHeight(hm, cfg, x0, y0, w, h);

        // Ground is 2000 raw at yScale 1 = 7.8 m; the summit should sit ~80 m above that, give or take the
        // fractal detail that deliberately varies it.
        float baseM = 2000 * cfg.YScale / 256f;
        Assert.InRange(peak - baseM, 65f, 105f);
    }

    [Fact]
    public void The_rim_fades_to_nothing_rather_than_ending_in_a_cliff()
    {
        var (hm, cfg) = Flat();
        MountainGenerator.Raise(hm, cfg, 1024, 1024, 250, 80, seed: 7);

        // Biggest step between neighbouring cells anywhere in the map. A hard edge would show up as one huge
        // jump; real slopes climb gradually. Cells are 4 m apart here.
        int maxStep = 0;
        for (int y = 1; y < 511; y++)
            for (int x = 1; x < 511; x++)
                maxStep = Math.Max(maxStep, Math.Abs(hm[x, y] - hm[x - 1, y]));

        float maxStepM = maxStep * cfg.YScale / 256f;
        Assert.True(maxStepM < 6f, $"a {maxStepM:0.#} m step between adjacent cells is a cliff, not a slope");
    }

    [Fact]
    public void The_returned_rect_is_wide_enough_to_hold_the_whole_mountain()
    {
        // The rect is what gets shipped over the wire, and it also bounds the loop - so if it is too small the
        // mountain is CLIPPED, not merely cropped, and the cut shows up as a step. The lean divides the distance
        // on the gentle flank, which stretches the reach well past the nominal radius; that is easy to under-
        // estimate, so assert the invariant directly: the outermost ring of the rect must still be bare ground.
        // Swept over seeds because whether it clips at all depends on which way that seed leans.
        for (int seed = 1; seed <= 12; seed++)
        {
            var (hm, cfg) = Flat();
            var (x0, y0, w, h) = MountainGenerator.Raise(hm, cfg, 1024, 1024, 250, 80, seed);

            for (int xx = 0; xx < w; xx++)
            {
                Assert.True(hm[x0 + xx, y0] == 2000, $"seed {seed}: mountain clipped at the rect's top edge");
                Assert.True(hm[x0 + xx, y0 + h - 1] == 2000, $"seed {seed}: clipped at the bottom edge");
            }
            for (int yy = 0; yy < h; yy++)
            {
                Assert.True(hm[x0, y0 + yy] == 2000, $"seed {seed}: clipped at the left edge");
                Assert.True(hm[x0 + w - 1, y0 + yy] == 2000, $"seed {seed}: clipped at the right edge");
            }
        }
    }

    [Fact]
    public void The_footprint_is_not_a_circle_and_the_surface_is_not_a_smooth_cone()
    {
        var (hm, cfg) = Flat();
        MountainGenerator.Raise(hm, cfg, 1024, 1024, 250, 80, seed: 11, roughness: 0.35f, ridges: 5);

        // Walk a ring at a fixed distance from the centre. On a cone every sample is identical; ridges and
        // gullies should make it vary a lot.
        float sp = cfg.HorizontalSpacing;
        var ring = new List<int>();
        for (int i = 0; i < 72; i++)
        {
            double a = i * Math.PI / 36.0;
            int gx = (int)((1024 + Math.Cos(a) * 160) / sp);
            int gy = (int)((1024 + Math.Sin(a) * 160) / sp);
            ring.Add(hm[gx, gy]);
        }
        float spreadM = (ring.Max() - ring.Min()) * cfg.YScale / 256f;
        Assert.True(spreadM > 8f, $"only {spreadM:0.#} m of variation around the ring - that is a cone");

        // A round footprint would give the same reach in every direction. Compare the two axes.
        int ReachAlong(int dx, int dy)
        {
            int n = 0;
            for (int i = 1; i < 200; i++)
            {
                int gx = (int)(1024 / sp) + dx * i, gy = (int)(1024 / sp) + dy * i;
                if (gx < 0 || gy < 0 || gx >= 512 || gy >= 512) break;
                if (hm[gx, gy] > 2000) n = i;
            }
            return n;
        }
        var reaches = new[] { ReachAlong(1, 0), ReachAlong(0, 1), ReachAlong(-1, 0), ReachAlong(0, -1) };
        Assert.True(reaches.Max() - reaches.Min() >= 2, "the footprint is perfectly circular");
    }

    [Fact]
    public void Zero_ridges_and_zero_roughness_gives_a_clean_smooth_hill()
    {
        var (hm, cfg) = Flat();
        MountainGenerator.Raise(hm, cfg, 1024, 1024, 200, 40, seed: 2, roughness: 0f, ridges: 0);

        // Still not a cylinder: it must rise toward the middle.
        float sp = cfg.HorizontalSpacing;
        int c = (int)(1024 / sp);
        Assert.True(hm[c, c] > hm[c + 20, c]);
        Assert.True(hm[c + 20, c] > hm[c + 40, c]);
    }

    [Fact]
    public void The_same_seed_gives_the_same_mountain_and_a_different_seed_does_not()
    {
        var (a, cfg) = Flat();
        var (b, _) = Flat();
        var (c, _) = Flat();
        MountainGenerator.Raise(a, cfg, 1024, 1024, 250, 80, seed: 99);
        MountainGenerator.Raise(b, cfg, 1024, 1024, 250, 80, seed: 99);
        MountainGenerator.Raise(c, cfg, 1024, 1024, 250, 80, seed: 100);

        Assert.Equal(a.Samples, b.Samples);
        Assert.NotEqual(a.Samples, c.Samples);
    }

    [Fact]
    public void The_wire_rect_round_trips_through_the_collab_encoding()
    {
        var (hm, cfg) = Flat();
        var (x0, y0, w, h) = MountainGenerator.Raise(hm, cfg, 1024, 1024, 250, 80, seed: 13);

        var b64 = MountainGenerator.EncodeRect(hm, x0, y0, w, h);
        var buf = Convert.FromBase64String(b64);
        Assert.Equal(w * h * 2, buf.Length);

        // Decode exactly the way CollabWorldState.ApplyOp and the Viewer's ApplyRemoteTerrain do.
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int o = (yy * w + xx) * 2;
                Assert.Equal(hm[x0 + xx, y0 + yy], (ushort)(buf[o] | (buf[o + 1] << 8)));
            }
    }

    [Fact]
    public void Preview_render()
    {
        var (hm, cfg) = Flat();
        MountainGenerator.Raise(hm, cfg, 1024, 1024, 250, 85, seed: 20250831, roughness: 0.38f, ridges: 5);

        string dir = Environment.GetEnvironmentVariable("RF_PREVIEW_DIR") ?? Path.GetTempPath();
        WriteHillshadeBmp(hm, cfg, Path.Combine(dir, "mountain_preview.bmp"));
        Assert.True(File.Exists(Path.Combine(dir, "mountain_preview.bmp")));
    }

    /// <summary>Hillshade the heightmap to a 24-bit BMP so the shape can actually be looked at.</summary>
    private static void WriteHillshadeBmp(Heightmap hm, TerrainConfig cfg, string path)
    {
        int w = hm.Width, h = hm.Height;
        float sp = cfg.HorizontalSpacing, ys = cfg.YScale / 256f;
        int rowPad = (4 - (w * 3) % 4) % 4;
        int dataSize = (w * 3 + rowPad) * h;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0x4D42); bw.Write(54 + dataSize); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((ushort)1); bw.Write((ushort)24);
        bw.Write(0); bw.Write(dataSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);

        // Sun from the north-west, the convention that makes relief read correctly to the eye.
        var sun = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-0.6f, 0.65f, -0.47f));
        for (int y = 0; y < h; y++)                       // BMP rows run bottom-up
        {
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(x - 1, 0), xp = Math.Min(x + 1, w - 1);
                int ym = Math.Max(y - 1, 0), yp = Math.Min(y + 1, h - 1);
                float gx = (hm[xp, y] - hm[xm, y]) * ys / (2f * sp);
                float gy = (hm[x, yp] - hm[x, ym]) * ys / (2f * sp);
                var n = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-gx, 1f, -gy));
                float lit = Math.Clamp(System.Numerics.Vector3.Dot(n, sun), 0f, 1f);
                float elev = Math.Clamp((hm[x, y] * ys - 7f) / 90f, 0f, 1f);
                float shade = 0.25f + 0.75f * lit;
                byte r = (byte)(255 * shade * (0.42f + 0.58f * elev));
                byte g = (byte)(255 * shade * (0.50f + 0.35f * elev));
                byte b = (byte)(255 * shade * (0.36f + 0.50f * elev));
                bw.Write(b); bw.Write(g); bw.Write(r);
            }
            for (int p = 0; p < rowPad; p++) bw.Write((byte)0);
        }
    }
}
