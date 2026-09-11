using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A sun shadow reaches the game only by being multiplied into the ground tiles - and the tiles are the only copy of
/// the ground art. Without a record of what was multiplied in, a second bake stacked on the first (x0.5 then x0.25),
/// a new sun left the old sun's shadows painted beside the new ones, and nothing could take a merge off again. The
/// merge's factor map is now kept with the level and divided back out before the next one is applied.
/// </summary>
public class GroundShadowRecordTests
{
    private static Texture2D Ground(int n = 64)
    {
        var rgba = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            rgba[i * 4] = (byte)(60 + i * 7 % 150); rgba[i * 4 + 1] = (byte)(90 + i * 3 % 120);
            rgba[i * 4 + 2] = (byte)(40 + i * 11 % 100); rgba[i * 4 + 3] = 255;
        }
        return new Texture2D(n, n, rgba);
    }

    /// <summary>A visibility map (255 lit, 0 shadow) with a shadowed block.</summary>
    private static Texture2D Vis(int x0, int y0, int x1, int y1, int n = 64)
    {
        var rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                byte v = x >= x0 && x < x1 && y >= y0 && y < y1 ? (byte)0 : (byte)255;
                int i = (y * n + x) * 4; rgba[i] = v; rgba[i + 1] = v; rgba[i + 2] = v; rgba[i + 3] = 255;
            }
        return new Texture2D(n, n, rgba);
    }

    private static int MaxDiff(Texture2D a, Texture2D b)
    {
        int d = 0;
        for (int i = 0; i < a.Rgba.Length; i++) if (i % 4 != 3) d = Math.Max(d, Math.Abs(a.Rgba[i] - b.Rgba[i]));
        return d;
    }

    private static double MeanRed(Texture2D t, int x0, int y0, int x1, int y1)
    {
        long s = 0; int n = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { s += t.Rgba[(y * t.Width + x) * 4]; n++; }
        return s / (double)n;
    }

    private static Texture2D Copy(Texture2D t) => new(t.Width, t.Height, (byte[])t.Rgba.Clone());

    [Fact]
    public void A_merge_divides_back_out_to_within_rounding()
    {
        var original = Ground();
        var g = Copy(original);
        var k = LightBake.ShadowFactorMap(Vis(10, 10, 40, 40), 0.5f);
        LightBake.MultiplyFactorIntoAtlas(g, k);
        Assert.True(MeanRed(g, 15, 15, 35, 35) < MeanRed(original, 15, 15, 35, 35) * 0.6, "the shadowed block is darkened");
        LightBake.DivideFactorOutOfAtlas(g, k);
        Assert.True(MaxDiff(original, g) <= 2, $"the ground comes back (off by {MaxDiff(original, g)})");
    }

    /// <summary>The editor's sequence for a second bake of the same sun: out with the old, in with the new. The
    /// ground ends where ONE bake leaves it - the old code multiplied again and the shadow went to x0.25.</summary>
    [Fact]
    public void Baking_twice_replaces_the_shadow_instead_of_stacking_it()
    {
        var once = Copy(Ground());
        var k = LightBake.ShadowFactorMap(Vis(10, 10, 40, 40), 0.5f);
        LightBake.MultiplyFactorIntoAtlas(once, k);

        var twice = Copy(once);
        LightBake.DivideFactorOutOfAtlas(twice, k);
        LightBake.MultiplyFactorIntoAtlas(twice, LightBake.ShadowFactorMap(Vis(10, 10, 40, 40), 0.5f));
        Assert.True(MaxDiff(once, twice) <= 2, $"a second bake matches the first (off by {MaxDiff(once, twice)})");

        var stacked = Copy(once);
        LightBake.MultiplyFactorIntoAtlas(stacked, k);                         // what it used to do
        Assert.True(MeanRed(stacked, 15, 15, 35, 35) < MeanRed(twice, 15, 15, 35, 35) * 0.6);
    }

    /// <summary>Move the sun and bake again: the old shadow comes OFF the ground and the new one goes on.</summary>
    [Fact]
    public void A_new_sun_takes_the_old_shadow_off_the_ground()
    {
        var original = Ground();
        var g = Copy(original);
        var oldK = LightBake.ShadowFactorMap(Vis(5, 5, 25, 25), 0.5f);
        LightBake.MultiplyFactorIntoAtlas(g, oldK);
        var newK = LightBake.ShadowFactorMap(Vis(35, 35, 60, 60), 0.5f);
        LightBake.DivideFactorOutOfAtlas(g, oldK);
        LightBake.MultiplyFactorIntoAtlas(g, newK);

        Assert.InRange(MeanRed(g, 8, 8, 22, 22), MeanRed(original, 8, 8, 22, 22) - 2, MeanRed(original, 8, 8, 22, 22) + 2);
        Assert.True(MeanRed(g, 38, 38, 57, 57) < MeanRed(original, 38, 38, 57, 57) * 0.6);
    }

    /// <summary>The record is written into the level and read back after a reopen, so it has to survive the file
    /// exactly - including which way up its rows are.</summary>
    [Fact]
    public void The_record_survives_the_file_it_is_saved_in()
    {
        var k = LightBake.ShadowFactorMap(Vis(3, 40, 20, 60), 0.37f);
        var back = TgaTexture.Decode(TgaTexture.EncodeGrayColormapped(k))!;
        Assert.Equal((k.Width, k.Height), (back.Width, back.Height));
        for (int i = 0; i < k.Width * k.Height; i++) Assert.Equal(k.Rgba[i * 4], back.Rgba[i * 4]);
    }

    [Fact]
    public void A_neutral_record_changes_nothing()
    {
        Assert.True(LightBake.IsNeutralFactor(null));
        Assert.True(LightBake.IsNeutralFactor(new Texture2D(1, 1, new byte[] { 255, 255, 255, 255 })));
        Assert.False(LightBake.IsNeutralFactor(LightBake.ShadowFactorMap(Vis(1, 1, 3, 3), 0.5f)));
        var g = Ground(); var before = Copy(g);
        LightBake.MultiplyFactorIntoAtlas(g, new Texture2D(1, 1, new byte[] { 255, 255, 255, 255 }));
        Assert.Equal(0, MaxDiff(before, g));
    }
}
