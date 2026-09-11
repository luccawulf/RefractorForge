using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// <see cref="RayScene"/> replaces the uniform grid for bakes that cast hundreds of rays per texel, so the thing
/// worth proving is not that it finds hits - it is that it finds exactly the SAME hits as
/// <see cref="MeshOccluder"/>, which has been shadowing real maps for months. A differential test against the
/// trusted implementation catches a whole class of BVH bug (a mis-partitioned node, a slab test off by an
/// epsilon, a stack that drops a subtree) that hand-built cases walk straight past.
/// </summary>
public class RaySceneTests
{
    private static List<(Vector3, Vector3, Vector3)> Soup(int n, int seed)
    {
        var rng = new Random(seed);
        var tris = new List<(Vector3, Vector3, Vector3)>(n);
        for (int i = 0; i < n; i++)
        {
            // Clustered, like a real map: buildings in groups rather than uniform noise, which is exactly the
            // distribution a uniform grid handles worst and a SAH split handles well.
            var centre = new Vector3(rng.Next(0, 8) * 40f, rng.Next(0, 3) * 6f, rng.Next(0, 8) * 40f);
            Vector3 P() => centre + new Vector3(
                (float)(rng.NextDouble() - 0.5) * 12f,
                (float)(rng.NextDouble() - 0.5) * 8f,
                (float)(rng.NextDouble() - 0.5) * 12f);
            tris.Add((P(), P(), P()));
        }
        return tris;
    }

    /// <summary>THE GATE: on the same soup and the same rays, the BVH and the grid must never disagree.</summary>
    [Fact]
    public void It_agrees_with_the_uniform_grid_on_every_ray()
    {
        var tris = Soup(4000, seed: 12345);
        var bvh = RayScene.Build(tris)!;
        var grid = MeshOccluder.Build(tris)!;
        var cur = grid.NewCursor();

        var rng = new Random(999);
        int disagree = 0, hits = 0, tested = 0;
        for (int i = 0; i < 4000; i++)
        {
            var o = new Vector3((float)rng.NextDouble() * 320f, (float)rng.NextDouble() * 20f, (float)rng.NextDouble() * 320f);
            var d = Vector3.Normalize(new Vector3(
                (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)));
            if (!float.IsFinite(d.X) || d.LengthSquared() < 0.5f) continue;

            bool a = bvh.Occluded(o, d);
            bool b = grid.Occluded(o, d, cur);
            tested++;
            if (a) hits++;
            if (a != b) disagree++;
        }

        Assert.True(tested > 3000, "the ray generator should not be rejecting most directions");
        Assert.True(hits > 200, $"the test is only meaningful if rays actually hit things (hits={hits})");
        Assert.True(disagree == 0, $"BVH and grid disagreed on {disagree} of {tested} rays");
    }

    /// <summary>The same, for the bounded form a lamp uses - the ray must STOP at the light.</summary>
    [Fact]
    public void It_agrees_with_the_grid_on_bounded_rays()
    {
        var tris = Soup(2500, seed: 4242);
        var bvh = RayScene.Build(tris)!;
        var grid = MeshOccluder.Build(tris)!;
        var cur = grid.NewCursor();

        var rng = new Random(7);
        int disagree = 0, tested = 0;
        for (int i = 0; i < 3000; i++)
        {
            var o = new Vector3((float)rng.NextDouble() * 320f, (float)rng.NextDouble() * 20f, (float)rng.NextDouble() * 320f);
            var d = Vector3.Normalize(new Vector3(
                (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)));
            if (!float.IsFinite(d.X)) continue;
            float max = 5f + (float)rng.NextDouble() * 60f;

            if (bvh.Occluded(o, d, max) != grid.Occluded(o, d, max, cur)) disagree++;
            tested++;
        }
        Assert.True(disagree == 0, $"BVH and grid disagreed on {disagree} of {tested} bounded rays");
    }

    /// <summary>A closest-hit trace returns the NEARER of two walls - the thing the grid cannot do at all, and
    /// what an indirect bounce depends on.</summary>
    [Fact]
    public void Trace_returns_the_nearest_surface()
    {
        var tris = new List<(Vector3, Vector3, Vector3)>
        {
            Quad(z: 10f), Quad(z: 25f),
        };
        var scene = RayScene.Build(tris, new[] { new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f) })!;

        Assert.True(scene.Trace(new Vector3(0f, 0f, 0f), Vector3.UnitZ, out var hit));
        Assert.Equal(10f, hit.Distance, 2);
        Assert.Equal(1f, hit.Albedo.X, 3);                       // the near wall's colour, not the far one's
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitZ) < 0f, "the normal must face the incoming ray");

        // From beyond the far wall, looking back, the far wall is now the nearest.
        Assert.True(scene.Trace(new Vector3(0f, 0f, 40f), -Vector3.UnitZ, out var back));
        Assert.Equal(15f, back.Distance, 2);
        Assert.Equal(1f, back.Albedo.Z, 3);
    }

    private static (Vector3, Vector3, Vector3) Quad(float z)
        => (new Vector3(-5f, -5f, z), new Vector3(5f, -5f, z), new Vector3(0f, 5f, z));

    /// <summary>A miss is a miss - no hit behind the ray, none past its limit.</summary>
    [Fact]
    public void It_does_not_hit_behind_the_origin_or_past_the_limit()
    {
        var scene = RayScene.Build(new List<(Vector3, Vector3, Vector3)> { Quad(z: 10f) })!;
        Assert.False(scene.Occluded(new Vector3(0f, 0f, 0f), -Vector3.UnitZ));       // wall is the other way
        Assert.False(scene.Occluded(new Vector3(0f, 0f, 0f), Vector3.UnitZ, 5f));    // stops short
        Assert.True(scene.Occluded(new Vector3(0f, 0f, 0f), Vector3.UnitZ, 20f));
        Assert.False(scene.Trace(new Vector3(0f, 0f, 0f), Vector3.UnitZ, out _, 5f));
    }

    /// <summary>The surface a point sits on must not shadow that point - the `skip` the sun bake has always used.</summary>
    [Fact]
    public void A_surface_does_not_occlude_itself()
    {
        var scene = RayScene.Build(new List<(Vector3, Vector3, Vector3)> { Quad(z: 0f) })!;
        Assert.False(scene.Occluded(new Vector3(0f, 0f, 0f), Vector3.UnitZ));
    }

    /// <summary>An empty scene is null rather than an object that answers nothing - the same contract MeshOccluder has.</summary>
    [Fact]
    public void An_empty_scene_builds_to_null()
        => Assert.Null(RayScene.Build(new List<(Vector3, Vector3, Vector3)>()));

    /// <summary>Traversal writes nothing, so one scene serves any number of threads with no per-thread state.
    /// That is the difference that lets a path-traced bake scale; the grid needs an int per triangle per thread.</summary>
    [Fact]
    public void One_scene_is_safe_to_share_across_threads()
    {
        var tris = Soup(3000, seed: 555);
        var scene = RayScene.Build(tris)!;

        var single = new bool[2000];
        var rays = new (Vector3 O, Vector3 D)[2000];
        var rng = new Random(31337);
        for (int i = 0; i < rays.Length; i++)
        {
            var o = new Vector3((float)rng.NextDouble() * 320f, (float)rng.NextDouble() * 20f, (float)rng.NextDouble() * 320f);
            var d = Vector3.Normalize(new Vector3(
                (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)));
            rays[i] = (o, d);
            single[i] = scene.Occluded(o, d);
        }

        var parallelResult = new bool[rays.Length];
        System.Threading.Tasks.Parallel.For(0, rays.Length, i => parallelResult[i] = scene.Occluded(rays[i].O, rays[i].D));
        Assert.Equal(single, parallelResult);
    }
}
