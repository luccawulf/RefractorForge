using System;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A terrain sun-shadow is only visible in game once it is painted into the ground TILES.
///
/// Proven twice over on real data: a deliberately unmissable LightmapShadowBits.lsb - a 128 m checkerboard putting
/// half the world in shadow - rendered no differently in game, while retail Fall_of_Saigon's own terrain tiles are
/// about half as bright exactly where its .lsb flags shadow (correlation -0.5 to -0.6 across three tiles, and
/// ~0 under a swapped tile orientation). So the .lsb is a record the engine keeps for its own purposes, and
/// mergeTerrainShadows - darkening the ground art - is what a player actually sees.
/// </summary>
public class GroundShadowMergeTests
{
    private static Texture2D Flat(int size, byte v)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255; }
        return new Texture2D(size, size, px);
    }

    /// <summary>Left half fully sunlit (255), right half fully shadowed (0) - the shape Bake produces.</summary>
    private static Texture2D HalfShadow(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte v = x < size / 2 ? (byte)255 : (byte)0;
                int o = (y * size + x) * 4;
                px[o] = px[o + 1] = px[o + 2] = v; px[o + 3] = 255;
            }
        return new Texture2D(size, size, px);
    }

    private static (int lit, int shadowed) Halves(Texture2D atlas)
    {
        int w = atlas.Width, h = atlas.Height;
        long a = 0, b = 0; int na = 0, nb = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (x < w / 4) { a += atlas.Rgba[o]; na++; }              // well inside the lit half
                else if (x > w * 3 / 4) { b += atlas.Rgba[o]; nb++; }     // well inside the shadowed half
            }
        return ((int)(a / Math.Max(na, 1)), (int)(b / Math.Max(nb, 1)));
    }

    [Fact]
    public void Shadowed_ground_is_darkened_and_lit_ground_is_untouched()
    {
        var atlas = Flat(64, 200);
        LightBake.MultiplyShadowIntoAtlas(atlas, HalfShadow(64), shadowLevel: 0.5f);
        var (lit, shadowed) = Halves(atlas);
        Assert.Equal(200, lit);                       // the sun side must not move at all
        Assert.InRange(shadowed, 98, 102);            // the shadow side keeps half
    }

    [Fact]
    public void The_darkness_control_does_what_it_says()
    {
        foreach (var level in new[] { 0.25f, 0.5f, 0.75f })
        {
            var atlas = Flat(64, 200);
            LightBake.MultiplyShadowIntoAtlas(atlas, HalfShadow(64), level);
            var (lit, shadowed) = Halves(atlas);
            Assert.Equal(200, lit);
            Assert.InRange(shadowed, (int)(200 * level) - 2, (int)(200 * level) + 2);
        }
    }

    [Fact]
    public void A_level_of_one_is_a_no_op()
    {
        var atlas = Flat(32, 173);
        var before = (byte[])atlas.Rgba.Clone();
        LightBake.MultiplyShadowIntoAtlas(atlas, HalfShadow(32), shadowLevel: 1f);
        Assert.Equal(before, atlas.Rgba);
    }

    /// <summary>The shadow map and the atlas need not share a size - it samples by normalised position.</summary>
    [Fact]
    public void A_smaller_shadow_map_still_lands_on_the_right_half()
    {
        var atlas = Flat(128, 200);
        LightBake.MultiplyShadowIntoAtlas(atlas, HalfShadow(32), shadowLevel: 0.5f);
        var (lit, shadowed) = Halves(atlas);
        Assert.Equal(200, lit);
        Assert.InRange(shadowed, 98, 102);
    }

    /// <summary>The raw multiply compounds - it has no idea what the ground already carries, and this pins that it
    /// stays a plain multiply. Stacking is prevented one level up, and explicitly: the editor keeps the factor map of
    /// the merge it made and divides it back out before the next one (see <see cref="GroundShadowRecordTests"/>).</summary>
    [Fact]
    public void Merging_twice_compounds_by_design()
    {
        var atlas = Flat(64, 200);
        LightBake.MultiplyShadowIntoAtlas(atlas, HalfShadow(64), 0.5f);
        LightBake.MultiplyShadowIntoAtlas(atlas, HalfShadow(64), 0.5f);
        var (_, shadowed) = Halves(atlas);
        Assert.InRange(shadowed, 48, 52);
    }

    [Fact]
    public void Alpha_is_left_alone()
    {
        var atlas = Flat(16, 200);
        LightBake.MultiplyShadowIntoAtlas(atlas, HalfShadow(16), 0.4f);
        for (int i = 3; i < atlas.Rgba.Length; i += 4) Assert.Equal(255, atlas.Rgba[i]);
    }
}
