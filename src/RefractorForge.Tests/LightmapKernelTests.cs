using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The GPU lightmap kernel, checked on the CPU.
///
/// <para>This project cannot run GPU work headlessly, so the parts of a GPU port most likely to be wrong - the
/// buffer layout, absolute indices across two BVHs packed into one set of arrays, the traversal stack, the
/// terrain indexing - are proven here instead. <see cref="LightmapKernel"/> reads ONLY the packed arrays and must
/// agree BIT FOR BIT with the objects it was packed from. The GLSL is then a transliteration of a function whose
/// data handling is already known to be right; what is left to verify on the GPU is arithmetic, not bookkeeping.</para>
/// </summary>
public class LightmapKernelTests
{
    // ---- fixture: a room with a holed roof, on hills, beside a second building ------------------------------

    private static (MeshLibrary.Mesh Mesh, Matrix4x4 World) Room()
    {
        var pos = new List<Vector3>(); var lm = new List<Vector2>(); var idx = new List<int>();
        int cell = 0; const int grid = 4;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int b0 = pos.Count;
            pos.Add(a); pos.Add(b); pos.Add(c); pos.Add(d);
            float cx = (cell % grid) / (float)grid, cy = (cell / grid) / (float)grid, w = 1f / grid, m = 0.02f;
            lm.Add(new(cx + m, cy + m)); lm.Add(new(cx + w - m, cy + m));
            lm.Add(new(cx + w - m, cy + w - m)); lm.Add(new(cx + m, cy + w - m));
            idx.AddRange(new[] { b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 });
            cell++;
        }
        float s = 10f, h = 4f;
        Quad(new(0, 0, 0), new(s, 0, 0), new(s, 0, s), new(0, 0, s));
        Quad(new(0, 0, 0), new(0, h, 0), new(s, h, 0), new(s, 0, 0));
        Quad(new(s, 0, 0), new(s, h, 0), new(s, h, s), new(s, 0, s));
        Quad(new(s, 0, s), new(s, h, s), new(0, h, s), new(0, 0, s));
        Quad(new(0, 0, s), new(0, h, s), new(0, h, 0), new(0, 0, 0));
        Quad(new(0, h, 0), new(s, h, 0), new(s, h, 3.5f), new(0, h, 3.5f));
        Quad(new(0, h, 6.5f), new(s, h, 6.5f), new(s, h, s), new(0, h, s));
        var part = new MeshLibrary.MaterialPart(idx.ToArray(), Vector3.One, null, false);
        var mesh = new MeshLibrary.Mesh(pos.ToArray(), new Vector2[pos.Count], new[] { part }) { LightmapUvs = lm.ToArray() };
        return (mesh, Matrix4x4.CreateTranslation(95f, 20f, 95f));
    }

    private static List<(Vector3, Vector3, Vector3)> WorldTris(MeshLibrary.Mesh m, Matrix4x4 w)
    {
        var t = new List<(Vector3, Vector3, Vector3)>();
        foreach (var p in m.Parts)
            for (int i = 0; i + 2 < p.Indices.Length; i += 3)
                t.Add((Vector3.Transform(m.Positions[p.Indices[i]], w),
                       Vector3.Transform(m.Positions[p.Indices[i + 1]], w),
                       Vector3.Transform(m.Positions[p.Indices[i + 2]], w)));
        return t;
    }

    /// <summary>Real relief, so the terrain march walks dozens of steps instead of clearing on the first.</summary>
    private static (Heightmap, TerrainConfig) Hills()
    {
        var hm = new Heightmap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                hm[x, y] = (ushort)Math.Clamp(400 + 300 * Math.Sin(x * 0.21) * Math.Cos(y * 0.17) + 250 * Math.Sin((x + y) * 0.09), 0, 1000);
        return (hm, new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 16f });
    }

    /// <summary>The level: the room itself plus a neighbouring block, with COLOURED albedo so a mix-up between
    /// the two BVHs' albedo would show up in the bounce.</summary>
    private static RayScene Level(List<(Vector3, Vector3, Vector3)> room)
    {
        var tris = new List<(Vector3, Vector3, Vector3)>(room);
        var alb = new List<Vector3>();
        foreach (var _ in room) alb.Add(new Vector3(0.7f, 0.6f, 0.5f));
        void Box(Vector3 lo, Vector3 hi, Vector3 colour)
        {
            Vector3 P(float x, float y, float z) => new(x, y, z);
            var f = new (Vector3, Vector3, Vector3)[]
            {
                (P(lo.X, lo.Y, hi.Z), P(hi.X, lo.Y, hi.Z), P(hi.X, hi.Y, hi.Z)), (P(lo.X, lo.Y, hi.Z), P(hi.X, hi.Y, hi.Z), P(lo.X, hi.Y, hi.Z)),
                (P(lo.X, lo.Y, lo.Z), P(lo.X, hi.Y, lo.Z), P(hi.X, hi.Y, lo.Z)), (P(lo.X, lo.Y, lo.Z), P(hi.X, hi.Y, lo.Z), P(hi.X, lo.Y, lo.Z)),
                (P(hi.X, lo.Y, lo.Z), P(hi.X, hi.Y, lo.Z), P(hi.X, hi.Y, hi.Z)), (P(hi.X, lo.Y, lo.Z), P(hi.X, hi.Y, hi.Z), P(hi.X, lo.Y, hi.Z)),
                (P(lo.X, lo.Y, lo.Z), P(lo.X, lo.Y, hi.Z), P(lo.X, hi.Y, hi.Z)), (P(lo.X, lo.Y, lo.Z), P(lo.X, hi.Y, hi.Z), P(lo.X, hi.Y, lo.Z)),
                (P(lo.X, hi.Y, lo.Z), P(lo.X, hi.Y, hi.Z), P(hi.X, hi.Y, hi.Z)), (P(lo.X, hi.Y, lo.Z), P(hi.X, hi.Y, hi.Z), P(hi.X, hi.Y, lo.Z)),
            };
            foreach (var t in f) { tris.Add(t); alb.Add(colour); }
        }
        Box(new(108f, 20f, 92f), new(114f, 29f, 112f), new(0.8f, 0.15f, 0.1f));    // a red neighbour to bounce off
        Box(new(90f, 20f, 108f), new(106f, 26f, 114f), new(0.1f, 0.3f, 0.8f));    // and a blue one
        return RayScene.Build(tris, alb)!;
    }

    private static readonly Vec3 Sun = new(0.35f, 0.55f, -0.76f);

    private static ObjectLightmapBaker.Advanced Ultra(RayScene level, int bounceDepth = 1, float aoRadius = 2.5f) => new(
        Scene: level, SunAngularDiameterDeg: 2.5f, SunSamples: 12, SkySamples: 16, AoRadius: aoRadius,
        AoStrength: 0.6f, SkyFill: 0.12f, BounceSamples: 8, BounceDepth: bounceDepth, BounceDistance: 30f,
        SunColour: new Vector3(0.9f, 0.85f, 0.7f), DenoiseIterations: 0);

    private static void AssertSameBake(Func<ILightmapShader?, Texture2D?> bake)
    {
        var cpu = bake(null);
        var kernel = bake(new LightmapKernelShader());
        Assert.NotNull(cpu);
        Assert.NotNull(kernel);
        int diff = 0;
        for (int i = 0; i < cpu!.Rgba.Length; i++) if (cpu.Rgba[i] != kernel!.Rgba[i]) diff++;
        Assert.True(diff == 0, $"the kernel reading only the packed arrays disagreed with the CPU shader on {diff} bytes");
    }

    // ---- the end-to-end gate ----------------------------------------------------------------------------------

    /// <summary>THE GATE: a real bake shaded through the packed arrays is byte-identical to the CPU shader.</summary>
    [Theory]
    [InlineData(1, 1, 2.5f)]   // one sub-sample, contact-darkening AO
    [InlineData(3, 1, 2.5f)]   // Ultra's 3x3, with the per-texel budget split across sub-samples
    [InlineData(1, 2, 2.5f)]   // a second bounce - the non-recursive depth-2 path
    [InlineData(1, 1, 0f)]     // unbounded sky, which also marches the terrain per sky ray
    public void A_bake_through_the_packed_arrays_is_byte_identical(int subSamples, int bounceDepth, float aoRadius)
    {
        var (mesh, world) = Room();
        var (hm, cfg) = Hills();
        var level = Level(WorldTris(mesh, world));
        AssertSameBake(shader => ObjectLightmapBaker.Bake(mesh, world, hm, cfg, Sun, 64, ambient: 0f,
            samples: subSamples, advanced: Ultra(level, bounceDepth, aoRadius), shader: shader));
    }

    /// <summary>Colour mode and lamps with a night scene - the lamp term stays on the CPU even when the rest goes
    /// through the kernel, and it must still get a real cursor, or buildings stop casting lamp shadows.</summary>
    [Fact]
    public void Colour_and_lamps_survive_the_kernel_path()
    {
        var (mesh, world) = Room();
        var (hm, cfg) = Hills();
        var roomTris = WorldTris(mesh, world);
        var level = Level(roomTris);
        var rig = new LightRig();
        rig.Lights.Add(new PointLight { Position = new Vec3(100f, 23f, 100f), Radius = 12f, SourceSize = 0.4f,
                                        ColorR = 1f, ColorG = 0.7f, ColorB = 0.4f });
        var night = NightBake.Build(hm, cfg, roomTris);
        AssertSameBake(shader => ObjectLightmapBaker.Bake(mesh, world, hm, cfg, Sun, 64, ambient: 0f, samples: 2,
            advanced: Ultra(level), rig: rig, night: night, colour: true, lampSamples: 3, shader: shader));
    }

    // ---- the packing itself -----------------------------------------------------------------------------------

    /// <summary>Both BVHs traverse to the same answers as the RayScene objects they were packed from - including
    /// the SELF scene, whose every index had to be rebased past the level's.</summary>
    [Fact]
    public void Packed_traversal_agrees_with_the_bvh_it_came_from()
    {
        var (mesh, world) = Room();
        var (hm, cfg) = Hills();
        var roomTris = WorldTris(mesh, world);
        var level = Level(roomTris);
        var self = RayScene.Build(roomTris)!;
        var sc = LightmapGpuScene.Build(level, self, hm, cfg, 999f);

        Assert.True(sc.RootSelf >= sc.LevelNodes, "the self scene's root must sit after the level's nodes");

        var rng = new Random(11);
        int disagree = 0, hits = 0;
        for (int i = 0; i < 3000; i++)
        {
            var o = new Vector3(85f + (float)rng.NextDouble() * 35f, 18f + (float)rng.NextDouble() * 14f,
                                85f + (float)rng.NextDouble() * 35f);
            var d = Vector3.Normalize(new Vector3((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5),
                                                  (float)(rng.NextDouble() - 0.5)));
            float max = rng.Next(3) == 0 ? float.MaxValue : 2f + (float)rng.NextDouble() * 30f;

            bool a = level.Occluded(o, d, max), b = LightmapKernel.Occluded(sc, sc.RootLevel, o, d, max);
            bool c = self.Occluded(o, d, max), e = LightmapKernel.Occluded(sc, sc.RootSelf, o, d, max);
            if (a != b || c != e) disagree++;
            if (a) hits++;

            bool ta = level.Trace(o, d, out var h1, max);
            bool tb = LightmapKernel.Trace(sc, sc.RootLevel, o, d, max, out var dist, out var pt, out var nrm, out var alb);
            if (ta != tb) disagree++;
            else if (ta && (h1.Distance != dist || h1.Point != pt || h1.Normal != nrm || h1.Albedo != alb)) disagree++;
        }
        Assert.True(hits > 200, $"the rays must actually hit something for this to mean anything ({hits})");
        Assert.True(disagree == 0, $"packed traversal disagreed with the BVH on {disagree} rays");
    }

    /// <summary>The terrain march reads the packed heights, pre-converted to metres - and must land on the same
    /// cells, and the same answer, as the march over the heightmap itself.</summary>
    [Fact]
    public void The_packed_terrain_march_agrees_with_the_heightmap()
    {
        var (hm, cfg) = Hills();
        var (_, maxH) = TerrainShadow.HeightSpan(hm, cfg);
        var sc = LightmapGpuScene.Build(null, null, hm, cfg, maxH);
        var rng = new Random(5);
        int disagree = 0, shadowed = 0;
        for (int i = 0; i < 4000; i++)
        {
            float x = (float)rng.NextDouble() * 256f, z = (float)rng.NextDouble() * 256f;
            float y = (float)rng.NextDouble() * maxH;
            var sun = new Vec3((float)(rng.NextDouble() - 0.5), 0.05f + (float)rng.NextDouble(), (float)(rng.NextDouble() - 0.5));
            bool a = TerrainShadow.PointLit(x, y, z, sun, hm, cfg, maxH);
            bool b = LightmapKernel.PointLit(sc, x, y, z, sun);
            if (a != b) disagree++;
            if (!a) shadowed++;
        }
        Assert.True(shadowed > 300, $"the hills must actually shadow things for this to test anything ({shadowed})");
        Assert.True(disagree == 0, $"the packed terrain march disagreed on {disagree} rays");
    }

    /// <summary>An object with no level scene around it still works - a missing BVH is a root of -1, not a crash.</summary>
    [Fact]
    public void A_missing_scene_is_handled()
    {
        var (mesh, world) = Room();
        var (hm, cfg) = Hills();
        AssertSameBake(shader => ObjectLightmapBaker.Bake(mesh, world, hm, cfg, Sun, 48, ambient: 0f, samples: 1,
            advanced: new ObjectLightmapBaker.Advanced(Scene: null, SunAngularDiameterDeg: 2.5f, SunSamples: 8,
                                                       SkySamples: 8, BounceSamples: 4), shader: shader));
    }

    /// <summary>The GPU uploads the level ONCE and then only each object's own BVH as a tail after it. That only
    /// works if the separately built tail is exactly the tail a full pack would contain - same absolute indices,
    /// same root - so a per-object upload can never disagree with the whole-scene one the kernel was proven on.</summary>
    [Fact]
    public void A_separately_built_tail_matches_the_tail_of_a_full_pack()
    {
        var (mesh, world) = Room();
        var (hm, cfg) = Hills();
        var roomTris = WorldTris(mesh, world);
        var level = Level(roomTris);
        var self = RayScene.Build(roomTris)!;

        var full = LightmapGpuScene.Build(level, self, hm, cfg, 999f);
        var levelOnly = LightmapGpuScene.Build(level, null, hm, cfg, 999f);
        var tail = LightmapGpuScene.BuildTail(self, levelOnly.LevelNodes, levelOnly.LevelTris, levelOnly.LevelIndex);

        Assert.Equal(full.RootSelf, tail.RootSelf);
        Assert.Equal(full.NodeBounds[(levelOnly.LevelNodes * LightmapGpuScene.FloatsPerNode)..], tail.NodeBounds);
        Assert.Equal(full.NodeLinks[(levelOnly.LevelNodes * LightmapGpuScene.IntsPerNode)..], tail.NodeLinks);
        Assert.Equal(full.Tris[(levelOnly.LevelTris * LightmapGpuScene.FloatsPerTri)..], tail.Tris);
        Assert.Equal(full.Index[levelOnly.LevelIndex..], tail.Index);
        // ...and the level part of the full pack is exactly the level-only pack.
        Assert.Equal(levelOnly.NodeBounds, full.NodeBounds[..levelOnly.NodeBounds.Length]);
        Assert.Equal(levelOnly.Tris, full.Tris[..levelOnly.Tris.Length]);
    }
}
