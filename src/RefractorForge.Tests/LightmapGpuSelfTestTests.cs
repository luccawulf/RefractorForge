using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The GPU self-test is only worth running if its pass mark means something in BOTH directions: a shader that
/// computes the right thing must pass, and a shader that computes the wrong thing must fail. A bar loose enough to
/// pass everything would certify a broken GPU; one too tight would reject every honest floating-point difference.
/// </summary>
public class LightmapGpuSelfTestTests
{
    /// <summary>The reference kernel reads only the packed arrays the GPU gets, and matches the CPU exactly - so it
    /// is the stand-in for a CORRECT GPU, and it has to clear the bar with nothing to spare needed.</summary>
    [Fact]
    public void A_correct_shader_passes()
    {
        var s = LightmapGpuSelfTest.Build();
        var cpu = LightmapGpuSelfTest.Bake(s, null)!;
        var kernel = LightmapGpuSelfTest.Bake(s, new LightmapKernelShader())!;
        var cmp = LightmapGpuSelfTest.Compare(cpu, kernel);
        Assert.Equal(0, cmp.Max);
        Assert.True(cmp.Passed);
    }

    /// <summary>A real bug must not slip under the bar. A sun turned the wrong way is the kind of mistake a GPU
    /// port makes - one sign, one swizzle - and it has to fail loudly.</summary>
    [Fact]
    public void A_wrong_sun_fails()
    {
        var s = LightmapGpuSelfTest.Build();
        var right = LightmapGpuSelfTest.Bake(s, null)!;
        var wrong = LightmapGpuSelfTest.Bake(s with { Sun = new Vec3(-0.35f, 0.55f, 0.76f) }, null)!;
        Assert.False(LightmapGpuSelfTest.Compare(right, wrong).Passed,
                     "a bake lit from the opposite side must not pass as 'close enough'");
    }

    /// <summary>A shader that gets the shadows right but drops a whole term - no bounce, say - must also fail.
    /// That is a subtler failure than a flipped sun, and exactly what a half-working GPU kernel looks like.</summary>
    [Fact]
    public void A_missing_term_fails()
    {
        var s = LightmapGpuSelfTest.Build();
        var right = LightmapGpuSelfTest.Bake(s, null)!;
        var noSky = LightmapGpuSelfTest.Bake(s with { Advanced = s.Advanced with { SkySamples = 0, SkyFill = 0f } }, null)!;
        var cmp = LightmapGpuSelfTest.Compare(right, noSky);
        Assert.False(cmp.Passed, $"dropping the sky term must not pass (mean {cmp.MeanAbs:0.00}, over-24 {cmp.FractionOver24:P1})");
    }

    /// <summary>Honest floating-point noise must pass. Two CPU bakes whose random directions differ only in their
    /// seed are as different as two correct implementations of the same estimator can reasonably be.</summary>
    [Fact]
    public void Honest_sampling_noise_passes()
    {
        var s = LightmapGpuSelfTest.Build();
        var a = LightmapGpuSelfTest.Bake(s, null)!;
        // Same estimator, very slightly different disc - the size of change a GPU's own tan() might introduce.
        var b = LightmapGpuSelfTest.Bake(s with { Advanced = s.Advanced with { SunAngularDiameterDeg = 2.5001f } }, null)!;
        var cmp = LightmapGpuSelfTest.Compare(a, b);
        Assert.True(cmp.Passed, $"a 0.0001-degree change must pass (mean {cmp.MeanAbs:0.00}, max {cmp.Max}, over-24 {cmp.FractionOver24:P2})");
    }
}
