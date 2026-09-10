using System;
using System.Linq;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// How dense the foliage preview is, and why OVERGROWTH is placed on the growth map's own grid.
///
/// The editor used to derive the overgrowth spacing from the palette's viewDistance, following the exe's
/// <c>spacing = viewDistance / (COUNT/2 - 1)</c>. That is genuinely what 0x75e110 computes, but it produced trees
/// 37 to 137 metres apart depending on the level and a few hundred instances where the game draws tens of
/// thousands. Measuring the shipped maps settled it: the overgrowth index map is a 4 m grid and retail levels
/// paint 24,000 to 48,000 tree-bearing cells per square kilometre, a figure that barely moves between levels -
/// Ho Chi Minh Trail 31,313, Khe Sanh 33,112, Operation Hastings 23,993. The viewDistance model predicted those
/// would differ by an order of magnitude; in the editor and in the game they do not.
///
/// The palette corroborates it. Within a material the per-type <c>probability</c> values sum to about 1.0, so
/// they choose BETWEEN types for a cell that is getting something rather than deciding whether it stays empty,
/// and <c>minRadiusDistToEquals</c> / <c>minRadiusDistToOthers</c> are 0.2 to 2 metres - the engine will stand two
/// trees a metre apart, which no hundred-metre patch grid can express.
///
/// UNDERGROWTH now follows the same rule against ITS map, which is a 1-2 m grid, so the same rate comes out four
/// times denser per square metre - a carpet, which is what the layer is. That IS millions of clumps map-wide, so
/// it is generated only around the camera, exactly as the engine keeps a patch grid around the player: Ia Drang
/// declares a 53 m undergrowth view distance and paints 86% of a 2048-cell map. The old patch model drew a couple
/// of hundred clumps inside that ring where the game draws thousands, which is what "undergrowth does not render
/// like it does in game" looked like.
/// </summary>
public class VegetationBuildTests
{
    private static FoliagePalette Pal(float viewDistance, bool over = true)
    {
        string tag = over ? "overGrowth" : "underGrowth";
        return FoliagePalette.Parse($"""
            <?xml version="1.0"?>
            <WRAPPER_TREE VERS="1.1">
              <{tag} materialMapSideSize="256" viewdistance="{viewDistance.ToString(System.Globalization.CultureInfo.InvariantCulture)}">
                <materials>
                  <dryGrass><types>
                    <t geometryName="c01f_trees_m2" probability="1.0" scale="CRDUNiform/0.700000/0.900000/false"></t>
                  </types></dryGrass>
                </materials>
              </{tag}>
            </WRAPPER_TREE>
            """);
    }

    /// <summary>Both layers are placed on the map they themselves paint, not on a distance guess.</summary>
    [Fact]
    public void Overgrowth_uses_the_growth_maps_own_cell_size()
    {
        // 1024 m over a 256-cell map is the 4 m grid every retail level actually paints on, whatever the
        // declared view distance happens to be.
        Assert.Equal(4f, OvergrowthFoliage.PatchMetersFor(Pal(450f), 1024f, over: true), 3);
        Assert.Equal(4f, OvergrowthFoliage.PatchMetersFor(Pal(150f), 1024f, over: true), 3);
        Assert.Equal(8f, OvergrowthFoliage.PatchMetersFor(Pal(450f), 2048f, over: true), 3);

        // Undergrowth reads its own map the same way; its view distance decides how FAR it is drawn, not how far
        // apart the clumps stand.
        Assert.Equal(4f, OvergrowthFoliage.PatchMetersFor(Pal(50f, over: false), 1024f, over: false), 3);
        Assert.Equal(17.5f, OvergrowthFoliage.PatchMetersFor(null, 1024f, over: false), 3);
    }

    /// <summary>The undergrowth ring: the palette's own declared distance, clamped to the band retail uses
    /// (35-61 m across the shipped maps, and Khe Sanh declares nothing at all).</summary>
    [Fact]
    public void Undergrowth_reaches_as_far_as_the_palette_says()
    {
        Assert.Equal(53f, OvergrowthFoliage.ViewMetersFor(Pal(53f, over: false), over: false), 3);
        Assert.Equal(OvergrowthFoliage.DefaultUnderView, OvergrowthFoliage.ViewMetersFor(Pal(0f, over: false), over: false), 3);
        Assert.Equal(OvergrowthFoliage.UnderViewMax, OvergrowthFoliage.ViewMetersFor(Pal(9000f, over: false), over: false), 3);
        Assert.Equal(OvergrowthFoliage.UnderViewMin, OvergrowthFoliage.ViewMetersFor(Pal(2f, over: false), over: false), 3);
        // Overgrowth answers 0 - "no ring, the whole map" - and the caller culls it against fog or world size.
        Assert.Equal(0f, OvergrowthFoliage.ViewMetersFor(Pal(0f), over: true), 3);
    }

    /// <summary>Generating a ring around the camera must place its plants in EXACTLY the spots a whole-map
    /// scatter would, or the grass would crawl every time the camera moved far enough to trigger a rebuild.</summary>
    [Fact]
    public void A_camera_window_is_a_subset_of_the_whole_map_scatter()
    {
        var growth = FullyPlanted(256);
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 1f };

        var all = OvergrowthFoliage.Scatter(growth, cfg, 4f, over: true);
        var ring = OvergrowthFoliage.Scatter(growth, cfg, 4f, over: true, centreX: 512f, centreZ: 512f, radius: 60f);

        Assert.NotEmpty(ring);
        Assert.True(ring.Count < all.Count / 10, $"a 60 m ring of a 1024 m map should be a small slice, got {ring.Count} of {all.Count}");
        var whole = new HashSet<(float, float)>(all.Select(i => (i.WorldX, i.WorldZ)));
        Assert.All(ring, i => Assert.Contains((i.WorldX, i.WorldZ), whole));

        // Everything it produced really is inside the ring (plus the one-cell margin the patch test allows).
        Assert.All(ring, i => Assert.True(MathF.Sqrt((i.WorldX - 512f) * (i.WorldX - 512f) + (i.WorldZ - 512f) * (i.WorldZ - 512f)) < 60f + 8f));

        // Two windows centred a little apart agree about the ground they share - the grass does not shuffle.
        var moved = OvergrowthFoliage.Scatter(growth, cfg, 4f, over: true, centreX: 522f, centreZ: 512f, radius: 60f);
        var movedSet = new HashSet<(float, float)>(moved.Select(i => (i.WorldX, i.WorldZ)));
        int shared = ring.Count(i => movedSet.Contains((i.WorldX, i.WorldZ)));
        Assert.True(shared > ring.Count / 2, $"the two windows overlap heavily, so most plants should be common; {shared} of {ring.Count}");
    }

    /// <summary>The ceiling is a hard stop, so a half-metre grid on a big map cannot lock the editor up.</summary>
    [Fact]
    public void The_instance_ceiling_is_obeyed()
    {
        var growth = FullyPlanted(256);
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 1f };
        Assert.Equal(500, OvergrowthFoliage.Scatter(growth, cfg, 4f, over: true, maxInstances: 500).Count);
    }

    /// <summary>Two levels whose declared view distances differ threefold must still preview at the same
    /// density, because that is what the shipped maps do. This is the assertion the old model failed.</summary>
    [Fact]
    public void Two_maps_with_different_view_distances_preview_at_the_same_density()
    {
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 1f };
        var near = new GrowthMaps { Over = Planted(256), OverSide = 256, OverPalette = Pal(150f) };
        var far = new GrowthMaps { Over = Planted(256), OverSide = 256, OverPalette = Pal(450f) };

        int a = OvergrowthFoliage.Scatter(near, cfg, OvergrowthFoliage.PatchMetersFor(near.OverPalette, 1024f), over: true).Count;
        int b = OvergrowthFoliage.Scatter(far, cfg, OvergrowthFoliage.PatchMetersFor(far.OverPalette, 1024f), over: true).Count;
        Assert.Equal(a, b);
        Assert.True(a > 25000, $"a fully painted 256-cell map should plant about half its cells, got {a}");
    }

    private static MaterialMap Planted(int side)
    {
        var map = new MaterialMap(side, side);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                map[x, y] = 2;
        return map;
    }

    [Fact]
    public void Only_low_medium_and_high_thin_the_patches()
    {
        Assert.Equal(1f, OvergrowthFoliage.KeepFractionFor(VegetationBuild.Stock));
        Assert.Equal(0.25f, OvergrowthFoliage.KeepFractionFor(VegetationBuild.VegLow));
        Assert.Equal(0.5f, OvergrowthFoliage.KeepFractionFor(VegetationBuild.VegMedium));
        Assert.Equal(0.75f, OvergrowthFoliage.KeepFractionFor(VegetationBuild.VegHigh));
        Assert.Equal(1f, OvergrowthFoliage.KeepFractionFor(VegetationBuild.VegUltra));
    }

    private static GrowthMaps FullyPlanted(int side)
    {
        var map = new MaterialMap(side, side);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                map[x, y] = 2;                      // dryGrass, the slot the test palette plants on
        return new GrowthMaps { Over = map, OverSide = side, OverPalette = Pal(450f) };
    }

    /// <summary>About half of the painted cells grow something - 0.51, measured against a capture of the running
    /// game. This is the number the whole preview hangs on.</summary>
    [Fact]
    public void About_half_the_painted_cells_grow_something()
    {
        var growth = FullyPlanted(256);
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 1f };
        float cell = OvergrowthFoliage.PatchMetersFor(growth.OverPalette, 1024f);

        int n = OvergrowthFoliage.Scatter(growth, cfg, cell, over: true).Count;
        int cells = 256 * 256;
        Assert.InRange(n / (double)cells, OvergrowthFoliage.CellOccupancy - 0.03, OvergrowthFoliage.CellOccupancy + 0.03);

        // Halving the density multiplier halves what is planted, so a mapper can preview thinner.
        int half = OvergrowthFoliage.Scatter(growth, cfg, cell, densityScale: 0.5f, over: true).Count;
        Assert.InRange(half / (double)n, 0.4, 0.6);
    }

    [Fact]
    public void Thinning_keeps_about_its_fraction_and_is_deterministic()
    {
        var growth = FullyPlanted(256);
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 1f };
        int full = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, over: true, keepFraction: 1f).Count;

        foreach (var keep in new[] { 0.25f, 0.5f, 0.75f })
        {
            int n = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, over: true, keepFraction: keep).Count;
            double got = n / (double)full;
            Assert.InRange(got, keep - 0.05, keep + 0.05);
        }

        // Same input, same output - the thinning must not crawl as the camera moves.
        var a = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, over: true, keepFraction: 0.5f);
        var b = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, over: true, keepFraction: 0.5f);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Select(i => (i.WorldX, i.WorldZ)), b.Select(i => (i.WorldX, i.WorldZ)));
    }
}
