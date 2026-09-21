using System;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The record a light-pool bake leaves behind, and the stacking it exists to stop.
///
/// The ground tiles are the only copy of the ground art, so a bake that keeps no record of what it multiplied in
/// can only ever multiply again: baking twice at the same strength made every pool twice as bright, with no way
/// back but the undo stack. The shadow merge had already solved this by keeping its factors; these tests hold the
/// pool version to the same contract - remove inverts apply, and a second bake REPLACES the first.
/// </summary>
public class PoolFactorTests
{
    private const int Size = 64;
    private const int SceneSize = 16;

    // Deliberately dark ground: with a night scene the multiplier runs to ~6x, and starting at 20 keeps the result
    // well clear of 255 so the round-trip is testing the arithmetic rather than the clamp.
    private static Texture2D Ground()
    {
        var px = new byte[Size * Size * 4];
        for (int i = 0; i < Size * Size; i++)
        {
            px[i * 4 + 0] = (byte)(18 + (i % 7));
            px[i * 4 + 1] = (byte)(20 + (i % 5));
            px[i * 4 + 2] = (byte)(22 + (i % 3));
            px[i * 4 + 3] = 255;
        }
        return new Texture2D(Size, Size, px);
    }

    // A pool in the middle of the map, falling off to nothing at the edges.
    private static Texture2D Pool()
    {
        var px = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float dx = (x + 0.5f) / Size - 0.5f, dy = (y + 0.5f) / Size - 0.5f;
                float t = MathF.Sqrt(dx * dx + dy * dy) / 0.5f;
                byte a = (byte)Math.Clamp((int)(255 * Math.Max(0f, 1f - t)), 0, 255);
                int o = (y * Size + x) * 4;
                px[o + 0] = a; px[o + 1] = (byte)(a * 0.8f); px[o + 2] = (byte)(a * 0.5f); px[o + 3] = 255;
            }
        return new Texture2D(Size, Size, px);
    }

    private static float[] NightScene()
    {
        var s = new float[SceneSize * SceneSize * 3];
        for (int i = 0; i < s.Length; i++) s[i] = 0.2f;
        return s;
    }

    private static double MeanLuma(Texture2D t)
    {
        double sum = 0;
        for (int i = 0; i < t.Width * t.Height; i++)
            sum += 0.299 * t.Rgba[i * 4] + 0.587 * t.Rgba[i * 4 + 1] + 0.114 * t.Rgba[i * 4 + 2];
        return sum / (t.Width * t.Height);
    }

    private static int MaxChannelDiff(Texture2D a, Texture2D b)
    {
        int worst = 0;
        for (int i = 0; i < a.Width * a.Height; i++)
            for (int c = 0; c < 3; c++)
                worst = Math.Max(worst, Math.Abs(a.Rgba[i * 4 + c] - b.Rgba[i * 4 + c]));
        return worst;
    }

    private static Texture2D Factor() =>
        LightBake.PoolFactorMap(Pool(), NightScene(), SceneSize, 1f);

    [Fact]
    public void Removing_a_pool_puts_the_ground_back()
    {
        // The whole basis of "replace, don't stack": the map has to invert itself.
        var ground = Ground();
        var original = new Texture2D(Size, Size, (byte[])ground.Rgba.Clone());
        var f = Factor();

        LightBake.ApplyPoolFactor(ground, f);
        Assert.True(MeanLuma(ground) > MeanLuma(original) * 1.5, "the pool did not brighten the ground at all");

        LightBake.ApplyPoolFactor(ground, f, remove: true);
        Assert.True(MaxChannelDiff(ground, original) <= 3,
                    $"removing the pool left the ground {MaxChannelDiff(ground, original)} levels off");
    }

    [Fact]
    public void A_second_bake_replaces_the_first_instead_of_stacking()
    {
        var f = Factor();

        var once = Ground();
        LightBake.ApplyPoolFactor(once, f);

        // What BakeLightsToGround now does on a re-bake: take the previous pools off, then put the new ones on.
        var twice = Ground();
        LightBake.ApplyPoolFactor(twice, f);
        LightBake.ApplyPoolFactor(twice, f, remove: true);
        LightBake.ApplyPoolFactor(twice, f);

        Assert.True(MaxChannelDiff(once, twice) <= 3,
                    $"baking twice differs from baking once by {MaxChannelDiff(once, twice)} levels");
    }

    [Fact]
    public void Without_the_removal_a_second_bake_really_does_stack()
    {
        // Guards the test above from being vacuous: if the removal were a no-op, the assertion could not fail.
        var f = Factor();

        var once = Ground();
        LightBake.ApplyPoolFactor(once, f);

        var stacked = Ground();
        LightBake.ApplyPoolFactor(stacked, f);
        LightBake.ApplyPoolFactor(stacked, f);

        Assert.True(MeanLuma(stacked) > MeanLuma(once) * 1.3,
                    $"stacking should be much brighter: once {MeanLuma(once):0.0}, stacked {MeanLuma(stacked):0.0}");
    }

    [Fact]
    public void Ground_the_lights_never_reach_is_left_exactly_alone()
    {
        // A factor of 255 is "no light here". The corners of this pool get none, and a bake must not shift them
        // by even one level - that is what would make repeated bakes drift the whole map.
        var ground = Ground();
        var original = new Texture2D(Size, Size, (byte[])ground.Rgba.Clone());
        LightBake.ApplyPoolFactor(ground, Factor());

        foreach (var (x, y) in new[] { (0, 0), (Size - 1, 0), (0, Size - 1), (Size - 1, Size - 1) })
        {
            int o = (y * Size + x) * 4;
            Assert.Equal(original.Rgba[o + 0], ground.Rgba[o + 0]);
            Assert.Equal(original.Rgba[o + 1], ground.Rgba[o + 1]);
            Assert.Equal(original.Rgba[o + 2], ground.Rgba[o + 2]);
        }
    }

    [Fact]
    public void A_bake_with_no_light_leaves_a_neutral_record()
    {
        // IsNeutralFactor is what makes "no record" and "a record that changes nothing" the same thing, so an
        // unlit bake does not leave something behind for the next bake to divide out.
        var dark = new Texture2D(Size, Size, new byte[Size * Size * 4]);
        var f = LightBake.PoolFactorMap(dark, NightScene(), SceneSize, 1f);
        Assert.True(LightBake.IsNeutralFactor(f));
    }
}
