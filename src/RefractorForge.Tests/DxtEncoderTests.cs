using System.Buffers.Binary;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The terrain tiles the game reads are DXT1 with a full mip chain (every retail tile: 256x256, 9 mips, 43,832 B).
/// The encoder has to produce exactly that shape, and its output has to decode back to the picture that went in.
/// </summary>
public class DxtEncoderTests
{
    private static Texture2D Gradient(int n)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int o = (y * n + x) * 4;
                px[o] = (byte)(x * 255 / (n - 1)); px[o + 1] = (byte)(y * 255 / (n - 1)); px[o + 2] = (byte)(128 + (x - y) / 2); px[o + 3] = 255;
            }
        return new Texture2D(n, n, px);
    }

    [Fact]
    public void A_256_tile_comes_out_the_size_and_shape_of_a_retail_tile()
    {
        var dds = DxtEncoder.EncodeDxt1Mipped(Gradient(256));
        Assert.Equal(43832, dds.Length);
        Assert.Equal(0xA1007u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(8)));      // flags
        Assert.Equal(256u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12)));
        Assert.Equal(256u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16)));
        Assert.Equal(32768u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(20)));      // linear size of the top level
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28)));          // mips
        Assert.Equal(0x4u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(80)));        // FOURCC
        Assert.Equal("DXT1", System.Text.Encoding.ASCII.GetString(dds, 84, 4));
        Assert.Equal(0x401008u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(108)));  // COMPLEX|TEXTURE|MIPMAP
    }

    [Fact]
    public void The_picture_survives_the_round_trip()
    {
        var src = Gradient(64);
        var back = DdsTexture.Decode(DxtEncoder.EncodeDxt1Mipped(src));
        Assert.Equal(64, back.Width); Assert.Equal(64, back.Height);
        long err = 0; int worst = 0;
        for (int i = 0; i < 64 * 64 * 4; i += 4)
            for (int c = 0; c < 3; c++)
            {
                int e = Math.Abs(src.Rgba[i + c] - back.Rgba[i + c]);
                err += e; if (e > worst) worst = e;
            }
        double mean = err / (64.0 * 64 * 3);
        Assert.True(mean < 4.0, $"mean error {mean:0.00}");
        Assert.True(worst < 40, $"worst error {worst}");
    }

    [Fact]
    public void A_flat_colour_is_exact_to_565_precision()
    {
        var px = new byte[16 * 16 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 100; px[i + 2] = 50; px[i + 3] = 255; }
        var back = DdsTexture.Decode(DxtEncoder.EncodeDxt1Mipped(new Texture2D(16, 16, px)));
        for (int i = 0; i < back.Rgba.Length; i += 4)
        {
            Assert.InRange(back.Rgba[i], 196, 204);
            Assert.InRange(back.Rgba[i + 1], 97, 103);
            Assert.InRange(back.Rgba[i + 2], 46, 54);
        }
    }

    [Fact]
    public void The_header_reader_tells_a_dxt_tile_from_an_uncompressed_one()
    {
        var dxt = DdsTexture.HeaderInfo(DxtEncoder.EncodeDxt1Mipped(Gradient(32)));
        Assert.Equal((32, true), dxt);
        var raw = DdsTexture.HeaderInfo(DdsTexture.EncodeUncompressed(Gradient(32)));
        Assert.Equal((32, false), raw);
    }
}
