using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A mesh whose lightmap-UV slot carries no unwrap cannot hold a varying map, so it gets a FLAT one — and the value
/// chosen for it is very visible. Testing the sun at the object's origin alone answers "does the terrain shade this
/// spot", which on open ground is always yes: every such object came out at 255 and glowed beside the ones that
/// could be unwrapped, whose baked maps average 26-80 across retail Saigon68. <see cref="ObjectLightmapBaker.AverageLit"/>
/// instead samples the surface against the terrain AND the mesh's own geometry, so the flat value is the level a
/// real bake would have averaged.
/// </summary>
public class AverageLitTests
{
    private static (Heightmap, TerrainConfig) FlatGround()
    {
        var hm = new Heightmap(16, 16);
        return (hm, new TerrainConfig { MaterialSize = 16, WorldSize = 64, YScale = 1f });
    }

    /// <summary>A single upward-facing quad on open ground: nothing shadows it, so it is fully lit.</summary>
    private static MeshLibrary.Mesh OpenQuad()
    {
        var pos = new Vector3[] { new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4) };
        int[] idx = { 0, 2, 1, 0, 3, 2 };
        var part = new MeshLibrary.MaterialPart(idx, Vector3.One, null, false);
        return new MeshLibrary.Mesh(pos, new Vector2[4], new[] { part }) { LightmapUvs = new Vector2[4] };
    }

    /// <summary>A floor with a roof over it: half the surface is in the mesh's OWN shadow.</summary>
    private static MeshLibrary.Mesh RoofOverFloor()
    {
        var pos = new Vector3[]
        {
            new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4),
            new(0, 2, 0), new(4, 2, 0), new(4, 2, 4), new(0, 2, 4),
        };
        int[] idx = { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
        var part = new MeshLibrary.MaterialPart(idx, Vector3.One, null, false);
        return new MeshLibrary.Mesh(pos, new Vector2[8], new[] { part }) { LightmapUvs = new Vector2[8] };
    }

    [Fact]
    public void An_open_surface_is_fully_lit()
    {
        var (hm, cfg) = FlatGround();
        float lit = ObjectLightmapBaker.AverageLit(OpenQuad(), Matrix4x4.Identity, hm, cfg, new Vec3(0f, 1f, 0f));
        Assert.True(lit > 0.99f, $"an unshaded quad should be fully lit, got {lit}");
    }

    /// <summary>The whole point: self-shadowing pulls the value DOWN, which is what a real baked map records and
    /// what the old origin-only test could never see.</summary>
    [Fact]
    public void Self_shadowing_pulls_the_value_below_fully_lit()
    {
        var (hm, cfg) = FlatGround();
        float lit = ObjectLittHelper(hm, cfg);
        // Two of the four triangles (the floor) sit under the roof, so roughly half the surface is dark.
        Assert.InRange(lit, 0.2f, 0.8f);
        Assert.True(lit < 0.99f, "a mesh that shadows itself must not read as fully lit");
    }

    private static float ObjectLittHelper(Heightmap hm, TerrainConfig cfg) =>
        ObjectLightmapBaker.AverageLit(RoofOverFloor(), Matrix4x4.Identity, hm, cfg, new Vec3(0f, 1f, 0f));

    /// <summary>Degenerate input must return something usable rather than throwing inside a bake.</summary>
    [Fact]
    public void An_empty_mesh_is_safe()
    {
        var (hm, cfg) = FlatGround();
        var empty = new MeshLibrary.Mesh(new Vector3[0], new Vector2[0],
            new[] { new MeshLibrary.MaterialPart(new int[0], Vector3.One, null, false) });
        float lit = ObjectLightmapBaker.AverageLit(empty, Matrix4x4.Identity, hm, cfg, new Vec3(0f, 1f, 0f));
        Assert.InRange(lit, 0f, 1f);
    }
}
