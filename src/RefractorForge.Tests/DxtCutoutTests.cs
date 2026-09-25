using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// DXT1 keeps a cut-out: a texture whose alpha has holes (a fence, a grille, a leaf) encodes the blocks with holes in
/// DXT1's three-colour mode, whose fourth index is transparent, and decodes with the holes where they were - an encoder
/// that ignored alpha wrote every texel solid. Opaque textures keep the four-colour mode throughout.
/// </summary>
public class DxtCutoutTests
{
    private static Texture2D Picture(bool holes)
    {
        const int n = 32;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int o = (y * n + x) * 4;
                px[o] = (byte)(x * 8); px[o + 1] = (byte)(y * 8); px[o + 2] = 90;
                bool hole = holes && ((x / 3 + y / 3) % 3 == 0 || (x >= 8 && x < 16 && y >= 8 && y < 16));
                px[o + 3] = hole ? (byte)0 : (byte)255;
            }
        return new Texture2D(n, n, px);
    }

    [Fact]
    public void A_cut_out_keeps_its_holes_and_an_opaque_texture_stays_solid()
    {
        var src = Picture(holes: true);
        var back = DdsTexture.Decode(DxtEncoder.EncodeDxt1Mipped(src))!;
        Assert.Equal((32, 32), (back.Width, back.Height));
        int wrong = 0, colourOff = 0;
        for (int i = 0; i < 32 * 32; i++)
        {
            bool wasClear = src.Rgba[i * 4 + 3] == 0, isClear = back.Rgba[i * 4 + 3] == 0;
            if (wasClear != isClear) wrong++;
            if (!wasClear && Math.Abs(src.Rgba[i * 4] - back.Rgba[i * 4]) > 40) colourOff++;
        }
        Assert.Equal(0, wrong);
        Assert.True(colourOff < 16, $"{colourOff} texels far off in colour");
        // A whole transparent block stays transparent.
        Assert.Equal(0, back.Rgba[(9 * 32 + 9) * 4 + 3]);

        var solid = DdsTexture.Decode(DxtEncoder.EncodeDxt1Mipped(Picture(holes: false)))!;
        Assert.All(Enumerable.Range(0, 32 * 32), i => Assert.Equal(255, solid.Rgba[i * 4 + 3]));
    }
}
