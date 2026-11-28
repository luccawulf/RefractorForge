using System.Numerics;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>The lattice volume (<see cref="LightVolume"/>): one quad per plane, one atlas tile per plane.</summary>
public class LightVolumeTests
{
    [Fact]
    public void A_volume_is_one_quad_per_plane_each_inside_its_own_tile()
    {
        var size = new Vector3(4f, 3f, 2f);
        var built = LightVolume.Build("v_m1", size, 1f, p => new Vector4(0, 0, 0, 0.1f));
        // 3 across the depth, 5 along it, 3 level (none on the floor).
        Assert.Equal(3 + 5 + 3, built.Planes);
        var sm = StandardMesh.Parse(built.Sm);
        var mat = sm.Lods[0][0];
        Assert.Equal(built.Planes * 4, mat.NumVertices);
        Assert.Equal(built.Planes * 2, mat.Faces.Length);
        Assert.Equal(32u, mat.VertexByteSize);
        // Every quad's UVs stay half a texel inside one tile.
        for (int k = 0; k < built.Planes; k++)
        {
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var (u, v) = mat.Uvs[k * 4 + i];
                u0 = MathF.Min(u0, u); u1 = MathF.Max(u1, u); v0 = MathF.Min(v0, v); v1 = MathF.Max(v1, v);
            }
            float tu0 = u0 * built.Atlas.Width, tu1 = u1 * built.Atlas.Width, tv0 = v0 * built.Atlas.Height, tv1 = v1 * built.Atlas.Height;
            Assert.Equal((int)tu0 / LightVolume.Tile, (int)(tu1 - 0.01f) / LightVolume.Tile);
            Assert.Equal((int)tv0 / LightVolume.Tile, (int)(tv1 - 0.01f) / LightVolume.Tile);
            Assert.Equal(LightVolume.Tile - 1f, tu1 - tu0, 3);
            Assert.Equal(LightVolume.Tile - 1f, tv1 - tv0, 3);
        }
        Assert.Equal(0, built.Atlas.Width & (built.Atlas.Width - 1));
    }

    [Fact]
    public void A_tile_holds_the_field_across_its_plane()
    {
        // Opacity rising with height: the first plane (across the depth) has it rising down its tile's rows.
        var size = new Vector3(2f, 4f, 2f);
        var built = LightVolume.Build("v_m1", size, 1f, p => new Vector4(0, 0, 0, p.Y / 4f));
        var px = built.Atlas.Rgba;
        int w = built.Atlas.Width;
        Assert.Equal(0, px[3]);                                                   // tile 0, row 0: the floor
        Assert.Equal(255, px[((LightVolume.Tile - 1) * w) * 4 + 3]);               // tile 0, last row: the top
        Assert.InRange(px[((LightVolume.Tile / 2) * w + 5) * 4 + 3], 120, 136);
    }

    [Fact]
    public void A_volume_fades_to_nothing_at_its_faces_but_not_at_its_floor()
    {
        var size = new Vector3(10f, 6f, 10f);
        Assert.Equal(1f, LightVolume.EdgeFade(new Vector3(0, 0, 0), size, 1.5f));
        Assert.Equal(0f, LightVolume.EdgeFade(new Vector3(5f, 2f, 0), size, 1.5f));
        Assert.Equal(0f, LightVolume.EdgeFade(new Vector3(0, 6f, 0), size, 1.5f));
        Assert.InRange(LightVolume.EdgeFade(new Vector3(4.25f, 2f, 0), size, 1.5f), 0.49f, 0.51f);
        // Seventeen planes of the alpha that density asks for leave what the density says.
        float a = LightVolume.PlaneAlpha(0.09f, 0.5f);
        Assert.Equal(MathF.Exp(-0.09f * 8.5f), MathF.Pow(1f - a, 17), 3);
    }
}
