using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The denoiser is what makes a path-traced bake affordable - Monte Carlo converges as the square root of the
/// sample count, so cleaning up by sampling alone costs about sixteen times the rays.
///
/// <para>The claim that has to be tested is not "it blurs" but that it follows the SURFACE rather than the
/// image. Texels that are neighbours in the atlas are routinely unrelated surfaces - that is what packing is -
/// so a plain blur would carry a sunlit roof's light onto the shaded wall packed beside it.</para>
/// </summary>
public class DenoiseTests
{
    private static (Heightmap, TerrainConfig) FlatGround()
        => (new Heightmap(16, 16), new TerrainConfig { MaterialSize = 16, WorldSize = 512, YScale = 1f });

    private static readonly Vec3 Sun = new(0f, 1f, 0f);      // straight down, so a roof is the only shadow

    /// <summary>
    /// Two quads that are NEIGHBOURS IN THE ATLAS but 100 m apart in the world - exactly what packing produces.
    /// The left half of the lightmap is one, the right half the other.
    /// </summary>
    private static MeshLibrary.Mesh TwoDistantQuads()
    {
        var pos = new[]
        {
            new Vector3(0, 0, 0),   new Vector3(8, 0, 0),   new Vector3(8, 0, 8),   new Vector3(0, 0, 8),
            new Vector3(100, 0, 0), new Vector3(108, 0, 0), new Vector3(108, 0, 8), new Vector3(100, 0, 8),
        };
        var lm = new[]
        {
            new Vector2(0.01f, 0.01f), new Vector2(0.49f, 0.01f), new Vector2(0.49f, 0.99f), new Vector2(0.01f, 0.99f),
            new Vector2(0.51f, 0.01f), new Vector2(0.99f, 0.01f), new Vector2(0.99f, 0.99f), new Vector2(0.51f, 0.99f),
        };
        // Wound so both face up (see AdvancedLightmapBakeTests: {0,2,1} would face them down).
        var part = new MeshLibrary.MaterialPart(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, Vector3.One, null, false);
        return new MeshLibrary.Mesh(pos, new Vector2[8], new[] { part }) { LightmapUvs = lm };
    }

    /// <summary>A roof covering only the SECOND quad, so it is shadowed and the first is not.</summary>
    private static RayScene RoofOverTheSecond() => RayScene.Build(new List<(Vector3, Vector3, Vector3)>
    {
        (new Vector3(96, 5, -4), new Vector3(112, 5, -4), new Vector3(112, 5, 12)),
        (new Vector3(96, 5, -4), new Vector3(112, 5, 12), new Vector3(96, 5, 12)),
    })!;

    private static Texture2D Bake(ObjectLightmapBaker.Advanced? adv, int size = 64)
    {
        var (hm, cfg) = FlatGround();
        return ObjectLightmapBaker.Bake(TwoDistantQuads(), Matrix4x4.Identity, hm, cfg, Sun, size,
                                        ambient: 0f, samples: 1, advanced: adv)!;
    }

    private static double Column(Texture2D t, float u)
    {
        int x = Math.Clamp((int)(u * t.Width), 0, t.Width - 1);
        double sum = 0; int n = 0;
        for (int y = 4; y < t.Height - 4; y++) { sum += t.Rgba[(y * t.Width + x) * 4]; n++; }
        return sum / n;
    }

    /// <summary>THE CLAIM. The two halves are adjacent in the atlas and 100 m apart in the world. Denoising must
    /// leave the lit half lit right up to the seam, and the shadowed half dark.</summary>
    [Fact]
    public void Denoising_does_not_bleed_across_a_chart_boundary()
    {
        var roof = RoofOverTheSecond();
        var plain = Bake(new ObjectLightmapBaker.Advanced(Scene: roof));
        var denoised = Bake(new ObjectLightmapBaker.Advanced(Scene: roof, DenoiseIterations: 4));

        // Sanity: the fixture really is lit on the left and shadowed on the right.
        Assert.True(Column(plain, 0.25f) > 240, $"the open quad should be lit, got {Column(plain, 0.25f):0.0}");
        Assert.True(Column(plain, 0.75f) < 15, $"the covered quad should be dark, got {Column(plain, 0.75f):0.0}");

        // Right at the seam - the last covered column of the lit half, the first of the dark half.
        Assert.True(Column(denoised, 0.47f) > 230,
            $"the lit half went dark at the seam ({Column(denoised, 0.47f):0.0}) - light bled across charts");
        Assert.True(Column(denoised, 0.53f) < 25,
            $"the dark half brightened at the seam ({Column(denoised, 0.53f):0.0}) - light bled across charts");
    }

    /// <summary>Off by default, and off means byte-identical - so no existing bake changes.</summary>
    [Fact]
    public void Denoising_is_off_by_default()
    {
        var roof = RoofOverTheSecond();
        Assert.Equal(Bake(new ObjectLightmapBaker.Advanced(Scene: roof)).Rgba,
                     Bake(new ObjectLightmapBaker.Advanced(Scene: roof, DenoiseIterations: 0)).Rgba);
    }

    /// <summary>
    /// The actual job, measured the only way that means anything: does denoising get the result CLOSER TO GROUND
    /// TRUTH? Comparing variance before and after would not - most of the variance in a lightmap is the shadow
    /// itself, which the filter is supposed to keep - so the reference here is the same bake at 400 samples, and
    /// the test is that a 5-sample bake plus denoising lands nearer to it than the 5-sample bake alone.
    /// </summary>
    [Fact]
    public void Denoising_moves_the_result_closer_to_a_converged_bake()
    {
        // A wall across the middle of the first quad. A 30-degree source spreads its penumbra over the whole
        // quad, so there is no fully-lit region to saturate at 255 and hide the estimator error.
        var wall = RayScene.Build(new List<(Vector3, Vector3, Vector3)>
        {
            (new Vector3(-6, 0, 4), new Vector3(14, 0, 4), new Vector3(14, 3, 4)),
            (new Vector3(-6, 0, 4), new Vector3(14, 3, 4), new Vector3(-6, 3, 4)),
        })!;

        var sun = new Vec3(0f, 0.7071f, -0.7071f);
        var (hm, cfg) = FlatGround();
        Texture2D Run(int samples, int iters) => ObjectLightmapBaker.Bake(
            TwoDistantQuads(), Matrix4x4.Identity, hm, cfg, sun, 64, ambient: 0f, samples: 1,
            advanced: new ObjectLightmapBaker.Advanced(
                Scene: wall, SunAngularDiameterDeg: 30f, SunSamples: samples, DenoiseIterations: iters))!;

        var reference = Run(400, 0);
        var noisy = Run(5, 0);
        var clean = Run(5, 2);

        // RMS error against the reference, over the first quad only (the second is 100 m away and fully lit).
        static double Rms(Texture2D a, Texture2D b)
        {
            double s = 0; int n = 0;
            for (int y = 6; y < a.Height - 6; y++)
                for (int x = 6; x < a.Width / 2 - 6; x++)
                {
                    double d = a.Rgba[(y * a.Width + x) * 4] - (double)b.Rgba[(y * a.Width + x) * 4];
                    s += d * d; n++;
                }
            return Math.Sqrt(s / n);
        }
        double before = Rms(noisy, reference);
        double after = Rms(clean, reference);

        Assert.True(before > 8.0, $"a 5-sample bake must actually be noisy to measure anything (RMS {before:0.0})");
        Assert.True(after < before * 0.8,
            $"denoising should move the bake closer to the converged one: RMS {before:0.0} -> {after:0.0}");

        // And it must do that WITHOUT rounding off the wall's contact shadow, which is genuinely sharp - the
        // penumbra is zero where an occluder meets the ground. Before the edge-stopping term existed, the filter
        // blurred exactly this and made the bake worse overall.
        int edge = -1;
        for (int y = 8; y < reference.Height - 8 && edge < 0; y++)
            if (reference.Rgba[(y * reference.Width + 16) * 4] > 200
                && reference.Rgba[((y + 4) * reference.Width + 16) * 4] < 40) edge = y;
        Assert.True(edge > 0, "the fixture should contain a sharp contact shadow to protect");
        Assert.True(clean.Rgba[(edge * clean.Width + 16) * 4] > 200,
            "the lit side of the contact shadow was blurred away");
        Assert.True(clean.Rgba[((edge + 4) * clean.Width + 16) * 4] < 60,
            "the dark side of the contact shadow was blurred away");
    }

    /// <summary>Reproducible, like the rest of the bake.</summary>
    [Fact]
    public void Denoising_is_deterministic()
    {
        var roof = RoofOverTheSecond();
        var opts = new ObjectLightmapBaker.Advanced(Scene: roof, SunAngularDiameterDeg: 4f, SunSamples: 16,
                                                     DenoiseIterations: 3);
        Assert.Equal(Bake(opts).Rgba, Bake(opts).Rgba);
    }
}
