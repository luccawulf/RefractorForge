using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Cancelling a bake has to stop the bake, not just stop starting new ones.
///
/// <para>The Cancel button appeared dead at Ultra quality, and the reason was structural: the caller runs one
/// object per <c>Parallel.For</c> iteration, and <c>Parallel.For</c> only consults its cancellation token
/// BETWEEN iterations. One iteration is one whole object, which at Ultra can be minutes on a large mesh - so the
/// token was honoured only after the thing the user wanted to interrupt had finished.</para>
///
/// <para>These count the CHUNKS the bake shades rather than timing it. The first version compared wall-clock
/// times, and once the bake itself used every core it became flaky whenever the rest of the suite ran alongside
/// it - two timings taken under different contention say nothing reliable. Counting work done is exact.</para>
/// </summary>
public class BakeCancellationTests
{
    /// <summary>Wraps the real CPU shader, counts chunks, and can pull the plug after a given number.</summary>
    private sealed class CountingShader : ILightmapShader
    {
        private readonly CancellationTokenSource? _cancelAfter;
        private readonly int _limit;
        public int Chunks;
        public CountingShader(CancellationTokenSource? cancelAfter = null, int limit = int.MaxValue)
        { _cancelAfter = cancelAfter; _limit = limit; }
        public int PreferredChunk => 256;                      // small, so a modest mesh is many chunks
        public void Shade(SampleChunk chunk, ShadeContext ctx, CancellationToken cancel)
        {
            CpuLightmapShader.Instance.Shade(chunk, ctx, cancel);
            if (Interlocked.Increment(ref Chunks) >= _limit) _cancelAfter?.Cancel();
        }
    }

    private static MeshLibrary.Mesh Floor()
    {
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(16, 0, 0), new Vector3(16, 0, 16), new Vector3(0, 0, 16) };
        var lm = new[] { new Vector2(0.001f, 0.001f), new Vector2(0.999f, 0.001f),
                         new Vector2(0.999f, 0.999f), new Vector2(0.001f, 0.999f) };
        var part = new MeshLibrary.MaterialPart(new[] { 0, 1, 2, 0, 2, 3 }, Vector3.One, null, false);
        return new MeshLibrary.Mesh(pos, new Vector2[4], new[] { part }) { LightmapUvs = lm };
    }

    private static (Heightmap, TerrainConfig) Ground()
        => (new Heightmap(16, 16), new TerrainConfig { MaterialSize = 16, WorldSize = 512, YScale = 1f });

    private static readonly Vec3 Sun = new(0f, 0.7071f, -0.7071f);

    /// <summary>A token that is already cancelled means no shading at all.</summary>
    [Fact]
    public void An_already_cancelled_bake_shades_nothing()
    {
        var (hm, cfg) = Ground();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var shader = new CountingShader();

        var result = ObjectLightmapBaker.Bake(Floor(), Matrix4x4.Identity, hm, cfg, Sun, 128,
                                              ambient: 0f, samples: 1, cancel: cts.Token, shader: shader);
        Assert.Null(result);
        Assert.Equal(0, shader.Chunks);
    }

    /// <summary>THE REGRESSION: cancelling PART WAY THROUGH one object stops that object. Uncancelled, this mesh
    /// is dozens of chunks; cancelled after the first, the bake must stop there rather than finish them all.</summary>
    [Fact]
    public void Cancelling_mid_bake_stops_that_object()
    {
        var (hm, cfg) = Ground();

        var full = new CountingShader();
        Assert.NotNull(ObjectLightmapBaker.Bake(Floor(), Matrix4x4.Identity, hm, cfg, Sun, 128,
                                                ambient: 0f, samples: 1, shader: full));
        Assert.True(full.Chunks > 10, $"the fixture must span many chunks to be worth interrupting ({full.Chunks})");

        using var cts = new CancellationTokenSource();
        var cut = new CountingShader(cts, limit: 1);
        var result = ObjectLightmapBaker.Bake(Floor(), Matrix4x4.Identity, hm, cfg, Sun, 128,
                                              ambient: 0f, samples: 1, cancel: cts.Token, shader: cut);

        Assert.Null(result);
        Assert.True(cut.Chunks <= 2,
            $"after cancelling on the first chunk the bake shaded {cut.Chunks} of {full.Chunks} - it kept going");
    }

    /// <summary>Without a token nothing changes - neither the result nor the work done.</summary>
    [Fact]
    public void A_bake_with_no_token_is_unaffected()
    {
        var (hm, cfg) = Ground();
        var a = ObjectLightmapBaker.Bake(Floor(), Matrix4x4.Identity, hm, cfg, Sun, 64, ambient: 0f, samples: 2);
        var b = ObjectLightmapBaker.Bake(Floor(), Matrix4x4.Identity, hm, cfg, Sun, 64, ambient: 0f, samples: 2,
                                         cancel: CancellationToken.None);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Rgba, b!.Rgba);
    }

    /// <summary>A shader is a plug: swapping in a different one must not change the answer when it shades the same
    /// way. This is the seam the GPU shader will plug into, so it is worth pinning now.</summary>
    [Fact]
    public void A_different_shader_with_the_same_maths_gives_the_same_bytes()
    {
        var (hm, cfg) = Ground();
        var viaDefault = ObjectLightmapBaker.Bake(Floor(), Matrix4x4.Identity, hm, cfg, Sun, 96, ambient: 0f, samples: 2);
        var viaWrapper = ObjectLightmapBaker.Bake(Floor(), Matrix4x4.Identity, hm, cfg, Sun, 96, ambient: 0f, samples: 2,
                                                  shader: new CountingShader());
        Assert.Equal(viaDefault!.Rgba, viaWrapper!.Rgba);
    }
}
