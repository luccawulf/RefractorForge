using System.Drawing;
using RefractorForge.Archive;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The .sm and .raw previews.
///
/// The interesting part of the .raw path is that a headerless square map carries no statement of its own bit
/// depth — a 16-bit heightmap and an 8-bit index map are told apart purely by file length. Getting that wrong
/// does not fail, it draws something plausible and wrong, so both directions are pinned here.
/// </summary>
public class MeshPreviewTests
{
    private static byte[] Heightmap16(int side, Func<int, int, ushort> f)
    {
        var b = new byte[side * side * 2];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                ushort v = f(x, y);
                int o = (y * side + x) * 2;
                b[o] = (byte)(v & 0xFF);
                b[o + 1] = (byte)(v >> 8);
            }
        return b;
    }

    [Fact]
    public void A_16bit_square_is_read_as_a_heightmap()
    {
        // 256 x 256 x 2 bytes = 131,072. As an 8-bit map that would be a 362.04-a-side square, which is not a
        // whole number, so only one reading is possible.
        var data = Heightmap16(256, (x, y) => (ushort)(1000 + x * 40));

        var bmp = MeshPreview.RenderRaw(data, "Heightmap.raw", 512, out var info);

        Assert.NotNull(bmp);
        Assert.NotNull(info);
        Assert.True(info!.SixteenBit);
        Assert.Equal(256, info.Side);
        Assert.Equal(1000, info.Min);
        Assert.Equal(1000 + 255 * 40, info.Max);
        bmp!.Dispose();
    }

    [Fact]
    public void An_8bit_square_is_read_as_an_index_map()
    {
        // 256 x 256 = 65,536 bytes. As 16-bit that would be 32,768 samples, a 181.02-a-side square - not whole.
        var data = new byte[256 * 256];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 15);

        var bmp = MeshPreview.RenderRaw(data, "materialmap.raw", 512, out var info);

        Assert.NotNull(bmp);
        Assert.NotNull(info);
        Assert.False(info!.SixteenBit);
        Assert.Equal(256, info.Side);
        Assert.Equal(0, info.Min);
        Assert.Equal(14, info.Max);
        bmp!.Dispose();
    }

    [Fact]
    public void A_length_that_is_no_kind_of_square_is_declined_rather_than_guessed_at()
    {
        var data = new byte[12345];
        var bmp = MeshPreview.RenderRaw(data, "junk.raw", 256, out var info);
        Assert.Null(bmp);
        Assert.Null(info);
    }

    [Fact]
    public void Relief_shading_actually_varies_across_a_slope()
    {
        // A ramp must not come out as one flat colour: if the hillshade or the height ramp were broken, the
        // preview would still render and still look like a picture, just a useless one.
        var data = Heightmap16(64, (x, y) => (ushort)(2000 + y * 300));
        var bmp = MeshPreview.RenderRaw(data, "Heightmap.raw", 256, out _);
        Assert.NotNull(bmp);

        var seen = new HashSet<int>();
        for (int y = 0; y < bmp!.Height; y += 4)
            for (int x = 0; x < bmp.Width; x += 4)
                seen.Add(bmp.GetPixel(x, y).ToArgb());

        Assert.True(seen.Count > 8, $"expected a gradient, got {seen.Count} distinct colours");

        // Low ground is the dark end of the ramp, high ground the pale end.
        int top = Brightness(bmp.GetPixel(bmp.Width / 2, 1));
        int bottom = Brightness(bmp.GetPixel(bmp.Width / 2, bmp.Height - 2));
        Assert.True(bottom > top, $"the high edge should be lighter than the low edge ({bottom} vs {top})");
        bmp.Dispose();
    }

    private static int Brightness(Color c) => (c.R + c.G + c.B) / 3;

    [Fact]
    public void A_flat_map_still_renders_instead_of_dividing_by_its_own_zero_range()
    {
        // Every sample identical means max - min = 0. A preview must survive that.
        var data = Heightmap16(32, (_, _) => 4242);
        var bmp = MeshPreview.RenderRaw(data, "Heightmap.raw", 128, out var info);
        Assert.NotNull(bmp);
        Assert.Equal(4242, info!.Min);
        Assert.Equal(4242, info.Max);
        bmp!.Dispose();
    }

    [Fact]
    public void Garbage_offered_as_a_mesh_is_declined_and_does_not_throw()
    {
        var junk = new byte[512];
        new Random(7).NextBytes(junk);
        var bmp = MeshPreview.RenderMesh(junk, 200, 150, 35f, 20f, 1f, out var info);
        Assert.Null(bmp);
        Assert.Null(info);
    }

    [Fact]
    public void An_sm_and_a_raw_get_their_own_preview_kinds()
    {
        Assert.Equal(PreviewKind.Mesh, Preview.KindOf("standardMesh/willy_m1.sm"));
        Assert.Equal(PreviewKind.Image, Preview.KindOf("bf1942/levels/Berlin/Heightmap.raw"));
        Assert.Equal(PreviewKind.Image, Preview.KindOf("texture/wall.dds"));
        Assert.Equal(PreviewKind.Text, Preview.KindOf("Init.con"));
        Assert.Equal(PreviewKind.Audio, Preview.KindOf("sound/shot.wav"));
        Assert.Equal(PreviewKind.Binary, Preview.KindOf("something.bin"));
    }
}
