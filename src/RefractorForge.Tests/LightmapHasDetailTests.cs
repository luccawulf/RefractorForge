using System.Collections.Generic;
using System.Linq;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A bake must keep a lightmap that holds real lighting and must be free to replace a blank one.
///
/// Getting this wrong is not theoretical: a level ended up with 293 flat white maps written by a buggy bake, and
/// because the "don't destroy what the level ships" guard could not tell them from DICE's own baked maps, every
/// later bake preserved them. The objects stayed blank however many times they were re-baked.
/// </summary>
public class LightmapHasDetailTests
{
    /// <summary>An 8-bit colour-mapped TGA, the shape every shipped object lightmap uses.</summary>
    private static byte[] Tga(IEnumerable<byte> indices, int w = 4, int h = 4)
    {
        var head = new byte[18];
        head[1] = 1;                       // colour-map type
        head[2] = 1;                       // uncompressed colour-mapped
        head[5] = 0; head[6] = 1;          // 256 palette entries
        head[7] = 24;                      // 24-bit palette
        head[12] = (byte)(w & 0xFF); head[13] = (byte)(w >> 8);
        head[14] = (byte)(h & 0xFF); head[15] = (byte)(h >> 8);
        head[16] = 8;                      // 8 bpp
        var pal = new byte[256 * 3];
        for (int i = 0; i < 256; i++) { pal[i * 3] = (byte)i; pal[i * 3 + 1] = (byte)i; pal[i * 3 + 2] = (byte)i; }
        return head.Concat(pal).Concat(indices).ToArray();
    }

    [Fact]
    public void A_uniform_map_is_a_placeholder()
    {
        Assert.False(ObjectLightmaps.HasDetail(Tga(Enumerable.Repeat((byte)255, 16))));   // the white placeholder
        Assert.False(ObjectLightmaps.HasDetail(Tga(Enumerable.Repeat((byte)0, 16))));     // an all-black one
        Assert.False(ObjectLightmaps.HasDetail(Tga(Enumerable.Repeat((byte)131, 16))));   // a computed flat level
    }

    [Fact]
    public void A_map_with_any_variation_is_real_lighting()
    {
        var px = Enumerable.Repeat((byte)200, 16).ToArray();
        px[7] = 40;                                                    // one shadowed texel is enough
        Assert.True(ObjectLightmaps.HasDetail(Tga(px)));
    }

    /// <summary>Anything we cannot read cheaply counts as detailed — refusing to overwrite is the safe direction.</summary>
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Unreadable_input_is_treated_as_real(byte[] junk)
        => Assert.True(ObjectLightmaps.HasDetail(junk));

    [Fact]
    public void A_dds_is_treated_as_real()
    {
        var dds = new byte[200];
        dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
        Assert.True(ObjectLightmaps.HasDetail(dds));
    }

    /// <summary>A truncated file (header and palette but no pixels) must not be read past its end.</summary>
    [Fact]
    public void A_map_with_no_pixel_data_is_safe()
        => Assert.True(ObjectLightmaps.HasDetail(Tga(new byte[0])));
}
