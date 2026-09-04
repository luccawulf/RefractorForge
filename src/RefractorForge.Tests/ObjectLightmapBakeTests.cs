using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The object lightmap is what BfVietnam's RaShaderPPLSTs1DifLmp.fx calls Prelight: a sun-visibility mask the shader
/// multiplies the sun term by. So the bake writes visibility, self-shadowed against the object's own mesh, and refuses
/// meshes whose second UV set is empty.
/// </summary>
public class ObjectLightmapBakeTests
{
    // A floor quad at y = 0 (lightmap left half) under a roof quad at y = 2 (right half). Both face up.
    private static MeshLibrary.Mesh RoofOverFloor(bool emptyUvs = false)
    {
        var pos = new Vector3[]
        {
            new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4),
            new(0, 2, 0), new(4, 2, 0), new(4, 2, 4), new(0, 2, 4),
        };
        var uv = new Vector2[8];
        var lm = emptyUvs
            ? new Vector2[8]
            : new Vector2[] { new(0.02f, 0.02f), new(0.48f, 0.02f), new(0.48f, 0.98f), new(0.02f, 0.98f),
                              new(0.52f, 0.02f), new(0.98f, 0.02f), new(0.98f, 0.98f), new(0.52f, 0.98f) };
        int[] idx = { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
        var part = new MeshLibrary.MaterialPart(idx, Vector3.One, null, false);
        return new MeshLibrary.Mesh(pos, uv, new[] { part }) { LightmapUvs = lm };
    }

    private static (Heightmap, TerrainConfig) FlatGround()
    {
        var hm = new Heightmap(16, 16);
        return (hm, new TerrainConfig { MaterialSize = 16, WorldSize = 64, YScale = 1f });
    }

    private static double Mean(Texture2D t, int x0, int x1)
    {
        long sum = 0; int n = 0;
        for (int y = 8; y < t.Height - 8; y++)
            for (int x = x0; x < x1; x++) { sum += t.Rgba[(y * t.Width + x) * 4]; n++; }
        return sum / (double)n;
    }

    [Fact]
    public void The_floor_under_a_roof_is_in_the_roofs_shadow_and_the_roof_is_lit()
    {
        var (hm, cfg) = FlatGround();
        var lmap = ObjectLightmapBaker.Bake(RoofOverFloor(), Matrix4x4.Identity, hm, cfg, new Vec3(0f, 1f, 0f), 64, ambient: 0f)!;
        Assert.NotNull(lmap);
        Assert.True(Mean(lmap, 4, 28) < 5, "floor texels are shadowed by the roof");
        Assert.True(Mean(lmap, 36, 60) > 250, "roof texels are lit");
    }

    [Fact]
    public void Without_self_shadowing_the_floor_is_lit_too()
    {
        var (hm, cfg) = FlatGround();
        var lmap = ObjectLightmapBaker.Bake(RoofOverFloor(), Matrix4x4.Identity, hm, cfg, new Vec3(0f, 1f, 0f), 64, ambient: 0f, selfShadow: false)!;
        Assert.True(Mean(lmap, 4, 28) > 250);
    }

    [Fact]
    public void Visibility_is_not_folded_with_the_suns_angle()
    {
        // A low sun: the roof is still fully visible to it, so the mask stays 1 - the engine applies N.L itself.
        var (hm, cfg) = FlatGround();
        var lmap = ObjectLightmapBaker.Bake(RoofOverFloor(), Matrix4x4.Identity, hm, cfg, new Vec3(0.9f, 0.2f, 0f), 64, ambient: 0f)!;
        Assert.True(Mean(lmap, 36, 60) > 250, $"got {Mean(lmap, 36, 60):0}");
    }

    [Fact]
    public void A_mesh_whose_second_uv_set_is_all_zero_gets_no_lightmap()
    {
        var (hm, cfg) = FlatGround();
        Assert.Null(ObjectLightmapBaker.Bake(RoofOverFloor(emptyUvs: true), Matrix4x4.Identity, hm, cfg, new Vec3(0f, 1f, 0f), 64));
    }

    [Fact]
    public void The_occluder_sees_the_roof_from_below_and_nothing_from_above()
    {
        var tris = new List<(Vector3, Vector3, Vector3)>
        {
            (new(0, 2, 0), new(4, 2, 4), new(4, 2, 0)),
            (new(0, 2, 0), new(0, 2, 4), new(4, 2, 4)),
        };
        var occ = MeshOccluder.Build(tris)!;
        Assert.True(occ.Occluded(new Vector3(2, 0, 2), Vector3.UnitY));
        Assert.True(occ.Occluded(new Vector3(0.5f, 0, 0.5f), Vector3.Normalize(new Vector3(0.3f, 1f, 0.3f))));
        Assert.False(occ.Occluded(new Vector3(2, 2, 2), Vector3.UnitY));                 // on the roof, looking up
        Assert.False(occ.Occluded(new Vector3(2, 0, 2), -Vector3.UnitY));                // looking down: nothing there
        Assert.False(occ.Occluded(new Vector3(2, 0, 2), Vector3.Normalize(new Vector3(1f, 0.2f, 0f))));   // grazing out the side
    }
}
