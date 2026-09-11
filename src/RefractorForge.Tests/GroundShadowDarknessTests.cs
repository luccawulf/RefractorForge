using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// "Shadow darkness" used to set the level shadowed ground KEPT, so raising it made shadows fainter: al_vietnas was
/// baked at 0.75 and its shadows went into the ground at x0.75, about as faint as any retail map's (x0.45-0.78), while
/// the editor drew them far darker. It is now a real darkness, and it can be changed after a bake without baking again -
/// the merge record already holds the shadow.
/// </summary>
public class GroundShadowDarknessTests
{
    /// <summary>A visibility map (255 lit, 0 shadow) with a shadowed block and a half-lit penumbra strip beside it.</summary>
    private static Texture2D Vis(int n = 64)
    {
        var rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                byte v = x >= 10 && x < 30 && y >= 10 && y < 30 ? (byte)0 : x >= 30 && x < 34 && y >= 10 && y < 30 ? (byte)128 : (byte)255;
                int i = (y * n + x) * 4; rgba[i] = v; rgba[i + 1] = v; rgba[i + 2] = v; rgba[i + 3] = 255;
            }
        return new Texture2D(n, n, rgba);
    }

    private static Texture2D Ground(int n = 64)
    {
        var rgba = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            rgba[i * 4] = (byte)(90 + i * 7 % 140); rgba[i * 4 + 1] = (byte)(100 + i * 3 % 120);
            rgba[i * 4 + 2] = (byte)(70 + i * 11 % 100); rgba[i * 4 + 3] = 255;
        }
        return new Texture2D(n, n, rgba);
    }

    private static Texture2D Copy(Texture2D t) => new(t.Width, t.Height, (byte[])t.Rgba.Clone());

    private static int MaxDiff(Texture2D a, Texture2D b)
    {
        int d = 0;
        for (int i = 0; i < a.Rgba.Length; i++) if (i % 4 != 3) d = Math.Max(d, Math.Abs(a.Rgba[i] - b.Rgba[i]));
        return d;
    }

    [Fact]
    public void The_darkest_texel_of_a_record_is_the_darkness_it_was_baked_at()
    {
        Assert.InRange(LightBake.FactorDarkness(LightBake.ShadowFactorMap(Vis(), 1f - 0.5f)), 0.49f, 0.51f);
        Assert.InRange(LightBake.FactorDarkness(LightBake.ShadowFactorMap(Vis(), 1f - 0.25f)), 0.24f, 0.26f);
        // al_vietnas' old record: darkest texel 191, i.e. the ground kept x0.75.
        var aln = new Texture2D(1, 2, new byte[] { 191, 191, 191, 255, 255, 255, 255, 255 });
        Assert.InRange(LightBake.FactorDarkness(aln), 0.24f, 0.26f);
    }

    [Fact]
    public void Coverage_counts_only_the_darkened_ground()
    {
        // 20x20 block + 4x20 penumbra of a 64x64 map.
        Assert.InRange(LightBake.FactorCoverage(LightBake.ShadowFactorMap(Vis(), 0.5f)), 480.0 / 4096 - 1e-6, 480.0 / 4096 + 1e-6);
        Assert.Equal(0, LightBake.FactorCoverage(new Texture2D(1, 1, new byte[] { 255, 255, 255, 255 })));
    }

    /// <summary>Re-darkening a record gives the record a fresh bake at that darkness would have made - penumbra and
    /// all - so the slider can move after a bake instead of costing another one.</summary>
    [Fact]
    public void Rescaling_a_record_matches_baking_again_at_the_new_darkness()
    {
        var baked = LightBake.ShadowFactorMap(Vis(), 1f - 0.25f);
        foreach (float d in new[] { 0.5f, 0.7f, 0.1f })
        {
            var rescaled = LightBake.RescaleFactor(baked, d);
            var fresh = LightBake.ShadowFactorMap(Vis(), 1f - d);
            Assert.True(MaxDiff(rescaled, fresh) <= 2, $"darkness {d}: off by {MaxDiff(rescaled, fresh)}");
        }
    }

    [Fact]
    public void Darkness_zero_is_no_shadow_at_all()
        => Assert.True(LightBake.IsNeutralFactor(LightBake.RescaleFactor(LightBake.ShadowFactorMap(Vis(), 0.5f), 0f)));

    /// <summary>The editor's sequence when the slider moves after a bake: divide the old record out of the ground,
    /// multiply the rescaled one in. The ground ends where a bake at the new darkness would have left it.</summary>
    [Fact]
    public void Retuning_the_ground_lands_where_a_bake_at_that_darkness_would()
    {
        var original = Ground();
        var old = LightBake.ShadowFactorMap(Vis(), 1f - 0.25f);
        var g = Copy(original);
        LightBake.MultiplyFactorIntoAtlas(g, old);

        LightBake.DivideFactorOutOfAtlas(g, old);
        LightBake.MultiplyFactorIntoAtlas(g, LightBake.RescaleFactor(old, 0.5f));

        var direct = Copy(original);
        LightBake.MultiplyFactorIntoAtlas(direct, LightBake.ShadowFactorMap(Vis(), 0.5f));
        Assert.True(MaxDiff(g, direct) <= 3, $"off by {MaxDiff(g, direct)}");
        // ...and a higher darkness really is darker: the shadowed block now keeps about half, not three quarters.
        int i = (20 * 64 + 20) * 4;
        Assert.InRange(g.Rgba[i] / (double)original.Rgba[i], 0.47, 0.53);
    }
}
