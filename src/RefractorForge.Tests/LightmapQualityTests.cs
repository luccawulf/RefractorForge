using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The quality tiers. These exist because a tier that cannot be SELECTED is indistinguishable from a feature
/// that does not work: the Ultra tier shipped once with the viewer still clamping the setting to <c>0..2</c>, so
/// choosing it snapped back to High, every bake ran at the old settings, and the only symptom was "the lightmaps
/// look the same as last time". A clamp inside a UI handler is not reachable from a test; a tier table is.
/// </summary>
public class LightmapQualityTests
{
    /// <summary>THE REGRESSION: every declared tier must survive being asked for.</summary>
    [Fact]
    public void Every_tier_is_reachable()
    {
        for (int t = 0; t < LightmapQuality.Count; t++)
            Assert.Equal(t, LightmapQuality.For(t).Tier);
        Assert.Equal(LightmapQuality.Ultra, LightmapQuality.For(LightmapQuality.Ultra).Tier);
    }

    /// <summary>Ultra is the only tier that turns the offline terms on, and it really does turn them on -
    /// a tier that advertises soft sun and ambient occlusion while sampling once is the same bug wearing a
    /// different hat.</summary>
    [Fact]
    public void Only_ultra_enables_the_offline_terms()
    {
        foreach (int t in new[] { LightmapQuality.Draft, LightmapQuality.Good, LightmapQuality.High })
        {
            var q = LightmapQuality.For(t);
            Assert.False(q.Advanced, $"{q.Name} must not enable the offline terms");
            Assert.Equal(1, q.SunSamples);
            Assert.Equal(0, q.SkySamples);
            Assert.Equal(0, q.BounceSamples);
            Assert.Equal(0, q.DenoiseIterations);
        }

        var u = LightmapQuality.For(LightmapQuality.Ultra);
        Assert.True(u.Advanced);
        Assert.True(u.SunSamples > 1, "a soft sun needs more than one direction, or there is no penumbra");
        Assert.True(u.SkySamples > 0, "ambient occlusion needs hemisphere rays");
        Assert.True(u.BounceSamples > 0, "indirect light needs bounce rays");
        Assert.True(u.DenoiseIterations > 0, "path-traced terms without a denoiser would just be noisy");
    }

    /// <summary>
    /// Draft, Good and High must keep the exact numbers they have always had. They produced the reference bake
    /// the user confirmed looks right AND loads in game; changing them would silently change every map ever baked
    /// with this editor.
    /// </summary>
    [Theory]
    [InlineData(LightmapQuality.Draft, 1, 16f, 256)]
    [InlineData(LightmapQuality.Good, 2, 24f, 512)]
    [InlineData(LightmapQuality.High, 3, 40f, 1024)]
    public void The_classic_tiers_are_unchanged(int tier, int subSamples, float texelsPerMetre, int maxSize)
    {
        var q = LightmapQuality.For(tier);
        Assert.Equal(subSamples, q.SubSamples);
        Assert.Equal(texelsPerMetre, q.TexelsPerMetre);
        Assert.Equal(maxSize, q.MaxSize);
    }

    /// <summary>1024 is the engine's ceiling - 2048 was tried and CRASHED the game, measured across all 35 retail
    /// levels where 4,152 object lightmaps top out at 1024 and none is larger. No tier may exceed it.</summary>
    [Fact]
    public void No_tier_exceeds_the_engine_ceiling()
    {
        for (int t = 0; t < LightmapQuality.Count; t++)
            Assert.True(LightmapQuality.For(t).MaxSize <= LightmapQuality.EngineMaxSize,
                        $"{LightmapQuality.For(t).Name} would bake above the engine's 1024px limit");
    }

    /// <summary>A setting restored from disk that is out of range should give a working editor, not a crash -
    /// and must not silently land somewhere surprising.</summary>
    [Fact]
    public void An_out_of_range_tier_clamps_rather_than_throwing()
    {
        Assert.Equal(LightmapQuality.Draft, LightmapQuality.For(-5).Tier);
        Assert.Equal(LightmapQuality.Count - 1, LightmapQuality.For(99).Tier);
    }

    /// <summary>Every tier has a name for the picker; a blank one would render an unselectable empty row.</summary>
    [Fact]
    public void Every_tier_has_a_name()
    {
        for (int t = 0; t < LightmapQuality.Count; t++)
            Assert.False(string.IsNullOrWhiteSpace(LightmapQuality.For(t).Name));
    }
}
