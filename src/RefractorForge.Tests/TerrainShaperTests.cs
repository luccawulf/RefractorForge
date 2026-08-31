using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates for the ground-preparation edits. The thing that makes these look hand-done is a missing skirt — a pad
/// with vertical sides, a channel that ends in a trench wall — so every one of them is checked for blending back
/// into the terrain as well as for doing its job.
/// </summary>
public class TerrainShaperTests
{
    const int Side = 128;

    static (Heightmap Hm, TerrainConfig Cfg) Map(Func<int, int, float> metres)
    {
        var cfg = new TerrainConfig { WorldSize = Side * 4, MaterialSize = Side, YScale = 1f, WaterLevel = 5f };
        var hm = new Heightmap(Side, Side);
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
                hm[x, y] = cfg.MetersToRaw(metres(x, y));
        return (hm, cfg);
    }

    static float H(Heightmap hm, TerrainConfig cfg, float wx, float wz) => SiteFinder.HeightAt(hm, cfg, wx, wz);

    [Fact]
    public void Flatten_levels_the_core_and_leaves_the_far_ground_alone()
    {
        // A slope, so "flat" is a real change.
        var (hm, cfg) = Map((x, y) => 20f + x * 0.8f);
        float far = H(hm, cfg, 400, 250);

        TerrainShaper.Flatten(hm, cfg, 250, 250, radius: 60f);

        // The core is level: sample a cross and require a tiny spread.
        float a = H(hm, cfg, 250, 250), b = H(hm, cfg, 270, 250), c = H(hm, cfg, 250, 270), d = H(hm, cfg, 232, 250);
        float spread = MathF.Max(MathF.Max(a, b), MathF.Max(c, d)) - MathF.Min(MathF.Min(a, b), MathF.Min(c, d));
        Assert.True(spread < 1.5f, $"the pad still varies by {spread:0.##} m");

        Assert.Equal(far, H(hm, cfg, 400, 250), 2);   // well outside: untouched
    }

    [Fact]
    public void Flatten_eases_out_instead_of_leaving_a_mesa()
    {
        var (hm, cfg) = Map((x, y) => 20f + x * 0.8f);
        TerrainShaper.Flatten(hm, cfg, 250, 250, radius: 60f, skirt: 0.4f);

        // Step between neighbouring samples along the slope must stay small - a vertical-sided pad would show up
        // as one big jump at the rim.
        float sp = cfg.HorizontalSpacing;
        float worst = 0f;
        for (float x = 150; x < 350; x += sp)
            worst = MathF.Max(worst, MathF.Abs(H(hm, cfg, x + sp, 250) - H(hm, cfg, x, 250)));
        Assert.True(worst < 4f, $"a {worst:0.#} m step at the pad edge is a cliff, not a graded skirt");
    }

    [Fact]
    public void Flatten_can_be_given_an_explicit_height()
    {
        var (hm, cfg) = Map((x, y) => 20f + x * 0.8f);
        TerrainShaper.Flatten(hm, cfg, 250, 250, radius: 50f, targetHeight: 42f);
        Assert.Equal(42f, H(hm, cfg, 250, 250), 1);
    }

    [Fact]
    public void Smooth_reduces_roughness_without_moving_the_average()
    {
        // Alternating spikes: the definition of rough.
        var (hm, cfg) = Map((x, y) => 40f + ((x + y) % 2 == 0 ? 6f : -6f));

        float Roughness()
        {
            float sp = cfg.HorizontalSpacing, sum = 0f; int n = 0;
            for (float x = 200; x < 300; x += sp)
                for (float z = 200; z < 300; z += sp) { sum += MathF.Abs(H(hm, cfg, x + sp, z) - H(hm, cfg, x, z)); n++; }
            return sum / n;
        }
        float Mean()
        {
            float sp = cfg.HorizontalSpacing, sum = 0f; int n = 0;
            for (float x = 200; x < 300; x += sp)
                for (float z = 200; z < 300; z += sp) { sum += H(hm, cfg, x, z); n++; }
            return sum / n;
        }

        float before = Roughness(), meanBefore = Mean();
        TerrainShaper.Smooth(hm, cfg, 250, 250, radius: 80f, passes: 3);
        float after = Roughness(), meanAfter = Mean();

        Assert.True(after < before * 0.6f, $"roughness only went {before:0.##} -> {after:0.##}");
        // Smoothing must not sink or raise the ground overall.
        Assert.Equal(meanBefore, meanAfter, 1);
    }

    [Fact]
    public void Smooth_leaves_ground_outside_the_radius_untouched()
    {
        var (hm, cfg) = Map((x, y) => 40f + ((x + y) % 2 == 0 ? 6f : -6f));
        float far = H(hm, cfg, 60, 60);
        TerrainShaper.Smooth(hm, cfg, 400, 400, radius: 60f, passes: 3);
        Assert.Equal(far, H(hm, cfg, 60, 60), 3);
    }

    [Fact]
    public void CarveChannel_cuts_along_the_path_and_not_beside_it()
    {
        var (hm, cfg) = Map((x, y) => 60f);
        var path = new List<(float X, float Z)> { (150, 250), (350, 250) };

        TerrainShaper.CarveChannel(hm, cfg, path, width: 20f, depth: 8f);

        Assert.True(H(hm, cfg, 250, 250) < 54f, "the channel was not cut");
        Assert.Equal(60f, H(hm, cfg, 250, 300), 1);    // well to the side: untouched
        Assert.Equal(60f, H(hm, cfg, 250, 200), 1);
    }

    [Fact]
    public void CarveChannel_follows_the_ground_rather_than_digging_to_a_plane()
    {
        // On a slope, a channel of fixed depth should stay a fixed depth - not level out.
        var (hm, cfg) = Map((x, y) => 20f + x * 0.5f);
        var path = new List<(float X, float Z)> { (150, 250), (350, 250) };
        float beforeA = H(hm, cfg, 180, 250), beforeB = H(hm, cfg, 320, 250);

        TerrainShaper.CarveChannel(hm, cfg, path, width: 16f, depth: 6f);

        Assert.Equal(beforeA - 6f, H(hm, cfg, 180, 250), 1);
        Assert.Equal(beforeB - 6f, H(hm, cfg, 320, 250), 1);
    }

    [Fact]
    public void The_returned_rect_contains_everything_that_changed()
    {
        var (hm, cfg) = Map((x, y) => 30f);
        var before = (ushort[])hm.Samples.Clone();

        var (x0, y0, w, h) = TerrainShaper.Flatten(hm, cfg, 250, 250, radius: 55f, targetHeight: 50f);

        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
                if (hm[x, y] != before[y * Side + x])
                {
                    Assert.InRange(x, x0, x0 + w - 1);
                    Assert.InRange(y, y0, y0 + h - 1);
                }
    }

    [Fact]
    public void Degenerate_input_is_refused_rather_than_throwing()
    {
        var (hm, cfg) = Map((x, y) => 30f);
        Assert.Equal((0, 0, 0, 0), TerrainShaper.Flatten(hm, cfg, 250, 250, radius: 0f));
        Assert.Equal((0, 0, 0, 0), TerrainShaper.Smooth(hm, cfg, 250, 250, radius: 40f, passes: 0));
        Assert.Equal((0, 0, 0, 0), TerrainShaper.CarveChannel(hm, cfg, new List<(float, float)> { (10, 10) }, 8f, 3f));
    }
}
