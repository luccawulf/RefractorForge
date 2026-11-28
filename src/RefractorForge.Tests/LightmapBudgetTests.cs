using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>24-bit lightmaps cost the 32-bit game four times an 8-bit one; a level baked without a budget ran the
/// client out of memory (<see cref="LightmapBudget"/>).</summary>
public class LightmapBudgetTests
{
    private static Texture2D Flat(int side, byte r, byte g, byte b)
    {
        var px = new byte[side * side * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255; }
        return new Texture2D(side, side, px);
    }

    [Fact]
    public void A_grey_map_goes_back_to_eight_bits_and_a_coloured_one_stays()
    {
        var plan = LightmapBudget.Fit(new[] { Flat(64, 120, 122, 119), Flat(64, 200, 150, 90) }, long.MaxValue);
        Assert.False(plan[0].Colour);
        Assert.True(plan[1].Colour);
        Assert.Equal(0, plan[1].Halvings);
    }

    [Fact]
    public void The_largest_maps_are_halved_until_the_colour_maps_fit()
    {
        var maps = new[] { Flat(1024, 200, 150, 90), Flat(1024, 90, 150, 200), Flat(256, 200, 150, 90) };
        long full = 2L * 1024 * 1024 * 3 + 256 * 256 * 3;
        var plan = LightmapBudget.Fit(maps, full - 1);
        Assert.Equal(1, plan[0].Halvings);            // the first of the two largest gives way, and that is enough
        Assert.Equal(0, plan[1].Halvings);
        Assert.Equal(0, plan[2].Halvings);

        var tight = LightmapBudget.Fit(maps, 1);      // nothing fits: everything stops at the floor, nothing loops
        Assert.All(tight, c => Assert.True(c.Colour));
        Assert.Equal(1024 >> tight[0].Halvings, LightmapBudget.MinSide);
        Assert.Equal(256 >> tight[2].Halvings, LightmapBudget.MinSide);
    }

    [Fact]
    public void Halving_averages_four_texels_and_grey_keeps_brightness()
    {
        var t = new Texture2D(2, 2, new byte[] { 0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 255, 100, 100, 100, 255 });
        var h = LightmapBudget.Halve(t);
        Assert.Equal(1, h.Width);
        Assert.Equal(100, h.Rgba[0]);
        var g = LightmapBudget.Grey(Flat(4, 255, 0, 0));
        Assert.Equal(54, g.Rgba[0]);
        Assert.Equal(g.Rgba[0], g.Rgba[2]);
    }
}
