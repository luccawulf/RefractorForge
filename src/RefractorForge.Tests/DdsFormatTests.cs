using System.Buffers.Binary;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// An uncompressed DDS says how its pixels are laid out in four bit masks, and the decoder used to ignore them: it
/// read every file as B8G8R8(A8). Retail ships 16-bit files - R5G6B5 <c>normalmap01/02.dds</c> in both games, A4R4G4B4
/// vehicle normal maps in BFV's <c>texture_001</c> - which came out as the wrong colours, and a single-level 16-bit
/// file read past the end of its buffer. And a DXT1 block in its three-colour mode keeps index 3 for transparent
/// black, which decoded opaque.
/// </summary>
public class DdsFormatTests
{
    private const uint Rgb = 0x40, AlphaPixels = 0x1, Alpha = 0x2, FourCc = 0x4, Luminance = 0x20000;

    /// <summary>A legacy-header DDS: 124-byte header, the pixel format given, then <paramref name="data"/>.</summary>
    internal static byte[] Dds(int w, int h, uint pfFlags, int bits, uint r, uint g, uint b, uint a, byte[] data,
                               int mips = 1, string? fourcc = null, uint caps2 = 0)
    {
        var d = new byte[128 + data.Length];
        "DDS "u8.CopyTo(d);
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(off), v);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | (mips > 1 ? 0x20000u : 0));
        U32(12, (uint)h); U32(16, (uint)w);
        U32(28, (uint)mips);
        U32(76, 32);
        U32(80, pfFlags);
        if (fourcc is not null) System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(d, 84);
        U32(88, (uint)bits);
        U32(92, r); U32(96, g); U32(100, b); U32(104, a);
        U32(108, 0x1000);
        U32(112, caps2);
        data.CopyTo(d, 128);
        return d;
    }

    private static byte[] U16s(params ushort[] px)
    {
        var d = new byte[px.Length * 2];
        for (int i = 0; i < px.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(i * 2), px[i]);
        return d;
    }

    private static (byte R, byte G, byte B, byte A) Px(Texture2D t, int i)
        => (t.Rgba[i * 4], t.Rgba[i * 4 + 1], t.Rgba[i * 4 + 2], t.Rgba[i * 4 + 3]);

    [Fact]
    public void R5G6B5_decodes_by_its_masks_even_with_a_single_level()
    {
        // Red, green, blue, white, black and a mid grey, 3 x 2, no mips: the old decoder read three bytes per two-byte
        // pixel and ran off the end of this buffer.
        var dds = Dds(3, 2, Rgb, 16, 0xF800, 0x07E0, 0x001F, 0, U16s(0xF800, 0x07E0, 0x001F, 0xFFFF, 0x0000, 0x8410));
        var t = DdsTexture.Decode(dds);
        Assert.Equal((3, 2), (t.Width, t.Height));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(t, 0));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), Px(t, 1));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Px(t, 2));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Px(t, 3));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Px(t, 4));
        var grey = Px(t, 5);                                           // 16/31, 32/63, 16/31
        Assert.InRange(grey.R, 131, 133); Assert.InRange(grey.G, 129, 131); Assert.InRange(grey.B, 131, 133);
    }

    [Fact]
    public void A4R4G4B4_carries_its_alpha()
    {
        var dds = Dds(2, 2, Rgb | AlphaPixels, 16, 0x0F00, 0x00F0, 0x000F, 0xF000, U16s(0xFF00, 0x80F0, 0x000F, 0x7777));
        var t = DdsTexture.Decode(dds);
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(t, 0));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)136), Px(t, 1));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)0), Px(t, 2));
        Assert.Equal(((byte)119, (byte)119, (byte)119, (byte)119), Px(t, 3));
    }

    [Fact]
    public void A1R5G5B5_and_X1R5G5B5()
    {
        var px = U16s(0xFC00, 0x001F);
        var a1 = DdsTexture.Decode(Dds(2, 1, Rgb | AlphaPixels, 16, 0x7C00, 0x03E0, 0x001F, 0x8000, px));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(a1, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)0), Px(a1, 1));
        var x1 = DdsTexture.Decode(Dds(2, 1, Rgb, 16, 0x7C00, 0x03E0, 0x001F, 0, px));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Px(x1, 1));   // no alpha mask: opaque
    }

    [Fact]
    public void Twenty_four_and_thirty_two_bit_layouts_follow_their_masks()
    {
        // R8G8B8, 3 x 1 (an odd row length): bytes are B, G, R.
        var rgb = DdsTexture.Decode(Dds(3, 1, Rgb, 24, 0xFF0000, 0xFF00, 0xFF, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        Assert.Equal(((byte)3, (byte)2, (byte)1, (byte)255), Px(rgb, 0));
        Assert.Equal(((byte)9, (byte)8, (byte)7, (byte)255), Px(rgb, 2));

        // A8B8G8R8 - bytes R, G, B, A - which the old decoder swapped to blue-for-red.
        var abgr = DdsTexture.Decode(Dds(1, 1, Rgb | AlphaPixels, 32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000, new byte[] { 10, 20, 30, 40 }));
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)40), Px(abgr, 0));

        // A8R8G8B8, the layout every retail 32-bit file and our own writer use: unchanged.
        var argb = DdsTexture.Decode(Dds(1, 1, Rgb | AlphaPixels, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000, new byte[] { 10, 20, 30, 40 }));
        Assert.Equal(((byte)30, (byte)20, (byte)10, (byte)40), Px(argb, 0));

        // X8R8G8B8: the fourth byte is padding, not alpha - a zero there is not a transparent pixel.
        var xrgb = DdsTexture.Decode(Dds(1, 1, Rgb, 32, 0xFF0000, 0xFF00, 0xFF, 0, new byte[] { 10, 20, 30, 0 }));
        Assert.Equal(((byte)30, (byte)20, (byte)10, (byte)255), Px(xrgb, 0));
    }

    [Fact]
    public void Luminance_and_alpha_only_formats()
    {
        var l8 = DdsTexture.Decode(Dds(2, 1, Luminance, 8, 0xFF, 0, 0, 0, new byte[] { 0, 200 }));
        Assert.Equal(((byte)200, (byte)200, (byte)200, (byte)255), Px(l8, 1));
        var a8l8 = DdsTexture.Decode(Dds(1, 1, Luminance | AlphaPixels, 16, 0xFF, 0, 0, 0xFF00, new byte[] { 90, 30 }));
        Assert.Equal(((byte)90, (byte)90, (byte)90, (byte)30), Px(a8l8, 0));
        var a8 = DdsTexture.Decode(Dds(2, 1, Alpha, 8, 0, 0, 0, 0xFF, new byte[] { 0, 77 }));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)77), Px(a8, 1));
    }

    [Fact]
    public void A_sixteen_bit_mip_chain_is_walked_by_its_own_pixel_size()
    {
        // 4 x 4 red, then 2 x 2 blue, then 1 x 1 green: asking for 2 px must land on the blue level.
        var data = U16s(Enumerable.Repeat((ushort)0xF800, 16).Concat(Enumerable.Repeat((ushort)0x001F, 4)).Append((ushort)0x07E0).ToArray());
        var dds = Dds(4, 4, Rgb, 16, 0xF800, 0x07E0, 0x001F, 0, data, mips: 3);
        var two = DdsTexture.Decode(dds, 2);
        Assert.Equal((2, 2), (two.Width, two.Height));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Px(two, 3));
        var one = DdsTexture.Decode(dds, 1);
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), Px(one, 0));
    }

    [Fact]
    public void Cut_short_pixel_data_is_an_error_not_a_read_past_the_end()
    {
        var full = Dds(4, 4, Rgb | AlphaPixels, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000, new byte[64]);
        Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(full[..^1]));
        var dxt = DxtEncoder.EncodeDxt1Flat(new Texture2D(8, 8, new byte[8 * 8 * 4]));
        Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(dxt[..^1]));
        Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(new byte[10]));

        // A header claiming a size whose byte count overflows: w x h x 4 wrapped past long for 2e9 square, so the
        // check passed and a 2 GB buffer was allocated for a 192-byte file; the int size of int.MaxValue square
        // wrapped to 4 bytes, and a DXT1 of that size wrapped (w + 3) / 4 to one block.
        foreach (int side in new[] { 2_000_000_000, int.MaxValue, 65536 })
        {
            var argb = Dds(side, side, Rgb | AlphaPixels, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000, new byte[64]);
            Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(argb));
            Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(argb, 256));
            var dxt1 = Dds(side, side, FourCc, 0, 0, 0, 0, 0, new byte[64], fourcc: "DXT1");
            Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(dxt1));
            Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(dxt1, 256));
        }
        // The limit is a side of DdsTexture.MaxSide, inclusive: a strip that long decodes, one pixel more does not.
        var strip = DdsTexture.Decode(Dds(DdsTexture.MaxSide, 1, Luminance, 8, 0xFF, 0, 0, 0, new byte[DdsTexture.MaxSide]));
        Assert.Equal((DdsTexture.MaxSide, 1), (strip.Width, strip.Height));
        Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(Dds(DdsTexture.MaxSide + 1, 1, Luminance, 8, 0xFF, 0, 0, 0, new byte[DdsTexture.MaxSide + 1])));
    }

    [Fact]
    public void Dxt1_index_3_in_three_colour_mode_is_transparent()
    {
        // One block, c0 <= c1 (three-colour mode): indices 0,1,2,3 across the first row, 3 everywhere else.
        var block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0), 0x001F);     // c0 blue
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 0xF800);     // c1 red   (c0 < c1)
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0xFFFFFFE4); // row 0: 0,1,2,3; rows 1-3: 3
        var t = DdsTexture.Decode(Dds(4, 4, FourCc, 0, 0, 0, 0, 0, block, fourcc: "DXT1"));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Px(t, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(t, 1));
        Assert.Equal((byte)255, Px(t, 2).A);                                    // the midpoint is opaque
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), Px(t, 3));
        Assert.Equal((byte)0, Px(t, 15).A);

        // The same indices in four-colour mode (c0 > c1): index 3 is a colour like any other.
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0), 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 0x001F);
        var four = DdsTexture.Decode(Dds(4, 4, FourCc, 0, 0, 0, 0, 0, block, fourcc: "DXT1"));
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal((byte)255, Px(four, i).A));
    }

    [Fact]
    public void Describe_reads_the_header_alone()
    {
        var dxt1 = DdsTexture.Describe(DxtEncoder.EncodeDxt1Mipped(new Texture2D(64, 32, new byte[64 * 32 * 4])))!;
        Assert.Equal((64, 32, 7, DdsPixelFormat.Dxt1), (dxt1.Width, dxt1.Height, dxt1.MipCount, dxt1.Format));
        Assert.True(dxt1.IsCompressed && dxt1.CanDecode && !dxt1.IsCube && !dxt1.IsDx10);
        Assert.Equal("DXT1", dxt1.Name);

        var dxt5 = DdsTexture.Describe(DxtEncoder.EncodeDxt5Mipped(new Texture2D(16, 16, new byte[16 * 16 * 4])))!;
        Assert.Equal(DdsPixelFormat.Dxt5, dxt5.Format);

        var argb = DdsTexture.Describe(DdsTexture.EncodeUncompressed(new Texture2D(8, 4, new byte[8 * 4 * 4])))!;
        Assert.Equal((DdsPixelFormat.Rgb, 32, 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u, 1),
                     (argb.Format, argb.BitCount, argb.RMask, argb.GMask, argb.BMask, argb.AMask, argb.MipCount));
        Assert.Equal("A8R8G8B8", argb.Name);

        // Only the header is needed: a file cut right after it still describes itself.
        var r5g6b5 = Dds(256, 256, Rgb, 16, 0xF800, 0x07E0, 0x001F, 0, Array.Empty<byte>(), mips: 9);
        var d = DdsTexture.Describe(r5g6b5)!;
        Assert.Equal((256, 256, 9, 16, "R5G6B5"), (d.Width, d.Height, d.MipCount, d.BitCount, d.Name));
        Assert.Equal("A4R4G4B4", DdsTexture.Describe(Dds(1, 1, Rgb | AlphaPixels, 16, 0x0F00, 0x00F0, 0x000F, 0xF000, new byte[2]))!.Name);
        Assert.Equal("X1R5G5B5", DdsTexture.Describe(Dds(1, 1, Rgb, 16, 0x7C00, 0x03E0, 0x001F, 0, new byte[2]))!.Name);
        Assert.Equal("X8R8G8B8", DdsTexture.Describe(Dds(1, 1, Rgb, 32, 0xFF0000, 0xFF00, 0xFF, 0, new byte[4]))!.Name);
        Assert.Equal("A8B8G8R8", DdsTexture.Describe(Dds(1, 1, Rgb | AlphaPixels, 32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000, new byte[4]))!.Name);
        Assert.Equal("L8", DdsTexture.Describe(Dds(1, 1, Luminance, 8, 0xFF, 0, 0, 0, new byte[1]))!.Name);
        Assert.Equal("A8L8", DdsTexture.Describe(Dds(1, 1, Luminance | AlphaPixels, 16, 0xFF, 0, 0, 0xFF00, new byte[2]))!.Name);
        Assert.Equal("A8", DdsTexture.Describe(Dds(1, 1, Alpha, 8, 0, 0, 0, 0xFF, new byte[1]))!.Name);

        // A mip count of 0 is one level; a cube map says so; a DX10 header is recognised and named.
        Assert.Equal(1, DdsTexture.Describe(Dds(1, 1, Rgb, 32, 0xFF0000, 0xFF00, 0xFF, 0, new byte[4], mips: 0))!.MipCount);
        Assert.True(DdsTexture.Describe(Dds(1, 1, Rgb, 32, 0xFF0000, 0xFF00, 0xFF, 0, new byte[4 * 6], caps2: 0x200 | 0xFC00))!.IsCube);
        var bc7 = Dds(4, 4, FourCc, 0, 0, 0, 0, 0, new byte[20 + 16], fourcc: "DX10");
        BinaryPrimitives.WriteUInt32LittleEndian(bc7.AsSpan(128), 98);           // DXGI_FORMAT_BC7_UNORM
        BinaryPrimitives.WriteUInt32LittleEndian(bc7.AsSpan(132), 3);            // TEXTURE2D
        var dx10 = DdsTexture.Describe(bc7)!;
        Assert.True(dx10.IsDx10);
        Assert.Equal(98, dx10.DxgiFormat);
        Assert.False(dx10.CanDecode);
        Assert.Contains("BC7", dx10.Name);
        var ex = Assert.Throws<InvalidDataException>(() => DdsTexture.Decode(bc7));
        Assert.Contains("BC7", ex.Message);

        Assert.Null(DdsTexture.Describe(new byte[200]));
        Assert.Null(DdsTexture.Describe("DDS "u8.ToArray()));
    }

    [Fact]
    public void Dx10_headers_of_the_classic_formats_decode()
    {
        // BC1 through a DX10 header is the same block as DXT1 - the pixels start 20 bytes later.
        var legacy = DxtEncoder.EncodeDxt1Flat(Checker(8));
        var dx10 = new byte[legacy.Length + 20];
        legacy.AsSpan(0, 128).CopyTo(dx10);
        "DX10"u8.CopyTo(dx10.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(128), 71);          // DXGI_FORMAT_BC1_UNORM
        BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(132), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(140), 1);
        legacy.AsSpan(128).CopyTo(dx10.AsSpan(148));
        Assert.Equal(DdsTexture.Decode(legacy).Rgba, DdsTexture.Decode(dx10).Rgba);
        Assert.Equal(DdsPixelFormat.Dxt1, DdsTexture.Describe(dx10)!.Format);

        // R8G8B8A8: bytes in R, G, B, A order.
        var rgba = Dds(1, 1, FourCc, 0, 0, 0, 0, 0, new byte[20 + 4], fourcc: "DX10");
        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(128), 28);          // DXGI_FORMAT_R8G8B8A8_UNORM
        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(132), 3);
        new byte[] { 1, 2, 3, 4 }.CopyTo(rgba, 148);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, DdsTexture.Decode(rgba).Rgba);
    }

    private static Texture2D Checker(int n)
    {
        var px = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++) { byte v = (byte)(((i % n) / 2 + (i / n) / 2) % 2 * 255); px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = (byte)(255 - v); px[i * 4 + 3] = 255; }
        return new Texture2D(n, n, px);
    }
}
