using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// A batch of lightmap sample points - one per sub-sample of a texel - and, once shaded, what each one saw.
///
/// <para>This is the seam between the two halves of a bake. GATHERING (rasterise the mesh in lightmap space,
/// decide which triangle owns each texel, find the world position and normal of every sub-sample) is cheap,
/// sequential and order-sensitive. SHADING (every ray: sun, sky, bounce, lamps) is where all the time goes, and
/// every sample is independent of every other. Splitting them is what lets the shading run in parallel on the
/// CPU, and it is exactly the shape a GPU compute dispatch wants: flat arrays in, flat arrays out.</para>
/// </summary>
public sealed class SampleChunk
{
    public readonly int Capacity;
    public int Count;

    // Inputs, filled by the gatherer in raster order.
    public readonly int[] Texel;
    public readonly Vector3[] Position;
    public readonly Vector3[] Normal;
    /// <summary>The per-TEXEL seed the lamps have always used, kept so a night bake does not change.</summary>
    public readonly uint[] TexelSeed;
    /// <summary>The per-SUB-SAMPLE seed for the sun, sky and bounce. Different for each sub-sample of a texel, so
    /// they draw different directions instead of nine identical ones.</summary>
    public readonly uint[] SubSeed;

    // Outputs, filled by a shader.
    public readonly float[] Sun;
    public readonly float[] Sky;
    public readonly Vector3[] Bounce;
    public readonly Vector3[] Lamp;

    public SampleChunk(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        Texel = new int[Capacity];
        Position = new Vector3[Capacity];
        Normal = new Vector3[Capacity];
        TexelSeed = new uint[Capacity];
        SubSeed = new uint[Capacity];
        Sun = new float[Capacity];
        Sky = new float[Capacity];
        Bounce = new Vector3[Capacity];
        Lamp = new Vector3[Capacity];
    }

    public bool Full => Count >= Capacity;

    public void Add(int texel, Vector3 p, Vector3 n, uint texelSeed, uint subSeed)
    {
        int i = Count++;
        Texel[i] = texel; Position[i] = p; Normal[i] = n; TexelSeed[i] = texelSeed; SubSeed[i] = subSeed;
    }

    public void Clear() => Count = 0;
}

/// <summary>Everything a shader needs that is constant for the whole of one object's bake.</summary>
public sealed class ShadeContext
{
    public Heightmap Hm { get; init; } = null!;
    public TerrainConfig Cfg { get; init; } = null!;
    public float MaxH { get; init; }
    public Vec3 SunDir { get; init; }
    public Vector3 Sun { get; init; }
    /// <summary>The object's own geometry as the classic grid - the plain tiers' self-shadow test.</summary>
    public MeshOccluder? SelfGrid { get; init; }
    /// <summary>The object's own geometry as a BVH - the advanced tier's self-shadow test.</summary>
    public RayScene? SelfScene { get; init; }
    public ObjectLightmapBaker.Advanced? Advanced { get; init; }
    public LightRig? Rig { get; init; }
    public NightBake.Scene? Night { get; init; }
    public int LampSamples { get; init; }

    /// <summary>True when the sun is sampled as a disc rather than tested as a point.</summary>
    public bool SoftSun { get; init; }
    // How many of each ray this ONE sub-sample casts. The Ultra budget is per TEXEL; see ForSubSamples.
    public int SunPerSample { get; init; }
    public int SkyPerSample { get; init; }
    public int BouncePerSample { get; init; }

    /// <summary>Per-thread scratch reused across every chunk of this object's bake. A cursor over the whole
    /// level's night occluder is one int per triangle - about 3 MB on a big map - and allocating one per thread per
    /// CHUNK threw away gigabytes over a single large object. Scoped to one object rather than shared globally,
    /// because a cursor's ray counter is an int and a pool that lived for a whole map could eventually wrap it.</summary>
    public readonly ConcurrentBag<(MeshOccluder.Cursor? Grid, MeshOccluder.Cursor? Lamp)> CursorPool = new();

    /// <summary>
    /// Split a per-texel ray budget across a texel's sub-samples.
    ///
    /// <para>The budget used to be applied per SUB-SAMPLE: at Ultra's 3x3 that is nine sub-samples each casting
    /// the full 32 sun + 48 sky + 24 bounce rays, 1,152 rays per texel, and all nine sharing one seed so they drew
    /// the same directions. Divided across the sub-samples - each with its own seed - a texel gets the same number
    /// of distinct directions for an eighth of the rays, and the directions are now stratified across the texel
    /// as well as across the light. With one sub-sample nothing changes at all.</para>
    /// </summary>
    public static (int Sun, int Sky, int Bounce) ForSubSamples(ObjectLightmapBaker.Advanced? a, int subCount)
    {
        if (a is null) return (1, 0, 0);
        static int Split(int total, int min, int n)
            => total <= 0 ? 0 : n <= 1 ? total : Math.Max(min, (total + n - 1) / n);
        // At least two sun directions per sub-sample: SunVisibility treats ONE as "test the centre" - the hard
        // shadow - so a single direction would silently switch the penumbra off.
        return (Split(a.SunSamples, 2, subCount), Split(a.SkySamples, 1, subCount), Split(a.BounceSamples, 1, subCount));
    }
}

/// <summary>Something that can shade a chunk of sample points: the CPU here, a GPU in the viewer.</summary>
public interface ILightmapShader
{
    /// <summary>How many samples this shader wants per chunk - small for a CPU, large for a GPU.</summary>
    int PreferredChunk { get; }
    /// <summary>Fill <see cref="SampleChunk.Sun"/>, <c>Sky</c>, <c>Bounce</c> and <c>Lamp</c> for every sample.</summary>
    void Shade(SampleChunk chunk, ShadeContext ctx, CancellationToken cancel);
}

/// <summary>
/// Shades on the CPU, in parallel ACROSS SAMPLES.
///
/// <para>The bake used to parallelise across OBJECTS only - one object per worker - so a bake of a single object,
/// which is exactly what a preview is, ran on one core however many the machine had. Every sample is independent,
/// so they can all run at once; the results are identical to the sequential order because nothing a sample
/// computes depends on any other, and the accumulation that follows is still done in raster order.</para>
/// </summary>
public sealed class CpuLightmapShader : ILightmapShader
{
    public static readonly CpuLightmapShader Instance = new();
    public int PreferredChunk => 16384;

    public void Shade(SampleChunk c, ShadeContext x, CancellationToken cancel)
    {
        // Per-thread scratch for the two structures that keep any: the classic grid and the night occluder each
        // stamp "already tested" per triangle. Pooled on the context, so it is reused across every chunk.
        var pool = x.CursorPool;
        try
        {
            Parallel.For(0, c.Count,
                new ParallelOptions { CancellationToken = cancel },
                () => pool.TryTake(out var cur) ? cur : (Grid: x.SelfGrid?.NewCursor(), Lamp: x.Night?.NewCursor()),
                (i, _, cur) => { ShadeOne(c, i, x, cur.Grid, cur.Lamp); return cur; },
                cur => pool.Add(cur));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One sample - the per-texel work the bake has always done, moved here verbatim.</summary>
    internal static void ShadeOne(SampleChunk c, int i, ShadeContext x, MeshOccluder.Cursor? gridCur, MeshOccluder.Cursor? lampCur)
    {
        var wp = c.Position[i];
        var fn = c.Normal[i];
        uint subSeed = c.SubSeed[i];
        var adv = x.Advanced;

        // Visibility only. Which side of the face the sun is on is the shader's business (N.L); whether the
        // object's own roof or wall is in the way is ours.
        float sunVis;
        if (x.SoftSun)
            sunVis = LightSampling.SunVisibility(adv!.Scene, x.SelfScene, x.Hm, x.Cfg, x.MaxH, wp, x.SunDir,
                                                 adv.SunAngularDiameterDeg, x.SunPerSample, subSeed);
        else
            sunVis = (TerrainShadow.PointLit(wp.X, wp.Y, wp.Z, x.SunDir, x.Hm, x.Cfg, x.MaxH)
                      && (x.SelfGrid is null || !x.SelfGrid.Occluded(wp, x.Sun, gridCur!))
                      && (x.SelfScene is null || !x.SelfScene.Occluded(wp, x.Sun))
                      && (adv?.Scene is null || !adv.Scene.Occluded(wp, x.Sun))) ? 1f : 0f;

        float sky = 1f;
        if (adv is { SkySamples: > 0 } && x.SkyPerSample > 0)
            sky = LightSampling.SkyVisibility(adv.Scene, x.SelfScene, x.Hm, x.Cfg, x.MaxH, wp, fn,
                                              x.SkyPerSample, adv.AoRadius, subSeed);

        var bounce = Vector3.Zero;
        if (adv is { BounceSamples: > 0 } && x.BouncePerSample > 0)
            bounce = LightSampling.Bounce(adv.Scene, x.SelfScene, x.Hm, x.Cfg, x.MaxH, wp, fn, x.SunDir,
                                          adv.SunColour ?? Vector3.One, x.BouncePerSample,
                                          adv.BounceDepth, adv.BounceDistance, subSeed,
                                          adv.SunAngularDiameterDeg, Math.Max(1, adv.BounceShadowSamples));

        c.Sun[i] = sunVis;
        c.Sky[i] = sky;
        c.Bounce[i] = bounce;
        c.Lamp[i] = ShadeLamp(c, i, x, lampCur);
    }

    /// <summary>
    /// The placed lights for one sample. With a night scene they are shadowed by the whole level and softened over
    /// the lamp's size; without one, terrain-only occlusion as before. Seeded per TEXEL, exactly as they always were.
    /// Separate from the rest because the GPU kernel does not do lamps: any shader can call this for them.
    /// </summary>
    /// <param name="lampCur">A per-thread cursor over the night occluder. It must not be null when there is a
    /// night scene - a null cursor makes the lamp test skip object occlusion entirely, so every building would
    /// stop casting lamp shadows without any other sign that something was wrong.</param>
    public static Vector3 ShadeLamp(SampleChunk c, int i, ShadeContext x, MeshOccluder.Cursor? lampCur)
    {
        var rig = x.Rig;
        if (rig is null || rig.Lights.Count == 0) return Vector3.Zero;
        var wp = c.Position[i];
        var fn = c.Normal[i];
        if (x.Night is not null)
            return NightBake.Lamp(x.Night, wp, fn, rig, lampCur, x.LampSamples, ground: false, seed: c.TexelSeed[i]);

        float add = LightBake.Intensity(wp.X, wp.Y, wp.Z, rig, x.Hm, x.Cfg);
        float lndl = 0f;
        foreach (var l in rig.Lights)
        {
            if (!l.Enabled) continue;
            var toL = new Vector3(l.Position.X - wp.X, l.Position.Y - wp.Y, l.Position.Z - wp.Z);
            if (toL.LengthSquared() < 1e-8f) { lndl = 1f; break; }
            lndl = MathF.Max(lndl, MathF.Max(0f, Vector3.Dot(fn, Vector3.Normalize(toL))));
        }
        float v = add * (lndl * 0.85f + 0.15f);
        return new Vector3(v, v, v);
    }
}
