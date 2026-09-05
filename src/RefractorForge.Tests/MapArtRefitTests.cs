using System.Buffers.Binary;
using RefractorForge.Formats.Validation;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The in-game map image and the combat area are one setting in two files: the engine stretches the image over the
/// rectangle, so moving the rectangle moves every icon relative to the art. Saigon68 hit this twice - remove the
/// area and the map came right, draw a new one and it broke again - because nothing re-cut the image. Re-cutting
/// keeps hand-drawn art (grid, icons, painted boundary) that a fresh terrain render would throw away.
/// </summary>
public class MapArtRefitTests
{
    // Four quadrants, four flat colours: red at +X/+Z, green at -X/+Z, blue at -X/-Z, white at +X/-Z. North-up, so
    // +Z is the TOP of the image.
    private static Texture2D Quadrants(int size = 64)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool east = x >= size / 2, north = y < size / 2;
                var (r, g, b) = north ? (east ? (255, 0, 0) : (0, 255, 0)) : (east ? (255, 255, 255) : (0, 0, 255));
                int i = (y * size + x) * 4;
                px[i] = (byte)r; px[i + 1] = (byte)g; px[i + 2] = (byte)b; px[i + 3] = 255;
            }
        return new Texture2D(size, size, px);
    }

    private static (int R, int G, int B) Middle(Texture2D t)
    {
        int i = ((t.Height / 2) * t.Width + t.Width / 2) * 4;
        return (t.Rgba[i], t.Rgba[i + 1], t.Rgba[i + 2]);
    }

    [Fact]
    public void Refitting_to_the_same_rectangle_changes_nothing()
    {
        var whole = CombatArea.Whole(1024);
        var src = Quadrants();
        var same = Minimap.Refit(src, whole, whole, src.Width);
        Assert.Equal(src.Rgba, same.Rgba);
    }

    [Fact]
    public void Cutting_down_to_one_quadrant_fills_the_image_with_it()
    {
        var whole = CombatArea.Whole(1024);
        var src = Quadrants();
        // +X/+Z is red and is the TOP-RIGHT of a north-up image.
        Assert.Equal((255, 0, 0), Middle(Minimap.Refit(src, whole, new CombatArea(512, 512, 512, 512), 64)));
        // -X/-Z is blue, bottom-left.
        Assert.Equal((0, 0, 255), Middle(Minimap.Refit(src, whole, new CombatArea(0, 0, 512, 512), 64)));
        // +X/-Z is white, bottom-right.
        Assert.Equal((255, 255, 255), Middle(Minimap.Refit(src, whole, new CombatArea(512, 0, 512, 512), 64)));
        // -X/+Z is green, top-left.
        Assert.Equal((0, 255, 0), Middle(Minimap.Refit(src, whole, new CombatArea(0, 512, 512, 512), 64)));
    }

    [Fact]
    public void A_cut_and_the_matching_widen_bring_the_picture_back()
    {
        // Not pixel-exact - it is a resample - but the quadrant colours must survive the round trip, which is what
        // says the mapping is its own inverse rather than merely reversible-looking.
        var whole = CombatArea.Whole(1024);
        var src = Quadrants(128);
        var cut = Minimap.Refit(src, whole, new CombatArea(256, 256, 512, 512), 128);
        var back = Minimap.Refit(cut, new CombatArea(256, 256, 512, 512), whole, 128);
        Assert.Equal((255, 0, 0), Middle(Minimap.Refit(back, whole, new CombatArea(512, 512, 512, 512), 32)));
        Assert.Equal((0, 0, 255), Middle(Minimap.Refit(back, whole, new CombatArea(0, 0, 512, 512), 32)));
    }

    [Fact]
    public void An_area_reaching_past_the_source_repeats_the_edge_rather_than_wrapping()
    {
        // A combat area may start negative (Faid_Pass ships -65). Wrapping would fold the far corner of the map in.
        var whole = CombatArea.Whole(1024);
        var src = Quadrants();
        var t = Minimap.Refit(src, whole, new CombatArea(-400, -400, 300, 300), 32);
        Assert.Equal((0, 0, 255), Middle(t));           // clamped into the -X/-Z quadrant, which is blue
    }

    [Fact]
    public void A_non_square_area_is_honoured_on_both_axes()
    {
        var whole = CombatArea.Whole(1024);
        var src = Quadrants();
        // Full width, top half only: the image should be green on the left and red on the right, no blue or white.
        var t = Minimap.Refit(src, whole, new CombatArea(0, 512, 1024, 512), 64);
        int left = (32 * 64 + 8) * 4, right = (32 * 64 + 56) * 4;
        Assert.Equal(255, t.Rgba[left + 1]);            // green
        Assert.Equal(255, t.Rgba[right]);               // red
        for (int i = 0; i < t.Rgba.Length; i += 4)
            Assert.False(t.Rgba[i] < 40 && t.Rgba[i + 1] < 40 && t.Rgba[i + 2] > 200, "blue leaked in from the south");
    }

    // ---- the shipped format -----------------------------------------------------------------------------------

    [Fact]
    public void Menu_art_is_written_as_dxt1_with_no_mip_chain()
    {
        // 81 of the 84 BFV levels that ship an ingamemap.dds are 512x512 DXT1 with no mips (131,200 bytes) and
        // none are uncompressed - which is what the editor had been writing, at eight times the size.
        var dds = DxtEncoder.EncodeDxt1Flat(Quadrants(512));
        Assert.Equal(131200, dds.Length);
        Assert.Equal("DDS ", System.Text.Encoding.ASCII.GetString(dds, 0, 4));
        Assert.Equal("DXT1", System.Text.Encoding.ASCII.GetString(dds, 84, 4));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28)));    // mip count
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(108)));  // TEXTURE, not COMPLEX|MIPMAP
        Assert.Equal(8320, DxtEncoder.EncodeDxt1Flat(Quadrants(128)).Length);        // and the thumbnail size
    }

    [Fact]
    public void That_dxt1_still_decodes_to_the_picture_that_went_in()
    {
        var back = DdsTexture.Decode(DxtEncoder.EncodeDxt1Flat(Quadrants(64)));
        Assert.Equal(64, back.Width);
        Assert.Equal(64, back.Height);
        int tr = (16 * 64 + 48) * 4, bl = (48 * 64 + 16) * 4;
        Assert.True(back.Rgba[tr] > 200 && back.Rgba[tr + 1] < 60, "top-right should still be red");
        Assert.True(back.Rgba[bl + 2] > 200 && back.Rgba[bl] < 60, "bottom-left should still be blue");
    }
}
