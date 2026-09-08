using System;
using System.Linq;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The retail exe and the BfVietnam_Veg_* builds do NOT plant the same amount of vegetation, and the editor's
/// captured density model (2.11 trees per occupied patch) was measured on the ULTRA build. Previewing a stock-exe
/// map with Ultra's grid put roughly eighty times too many trees on Saigon68 - "more overgrowth in the editor than
/// in game".
///
/// Constants disassembled from the shipped executables (2026-09-08):
///   BfVietnam.exe      COUNT 10 both layers; 0x75e110 computes spacing = viewDistance / (COUNT/2 - 1) = /4.
///   BfVietnam_Veg_*    COUNT 80 over / 34 under; 0x75e110 replaced by a hardcoded 12.5 (over) / 17.5 (under).
///   Low/Medium/High    additionally hash each patch and keep 1/4, 2/4, 3/4 of them. Ultra keeps all.
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

    /// <summary>Saigon68's real numbers: viewdistance 450 over, 50 under.</summary>
    [Fact]
    public void Stock_derives_the_patch_size_from_the_maps_own_view_distance()
    {
        Assert.Equal(112.5f, OvergrowthFoliage.PatchMetersFor(VegetationBuild.Stock, Pal(450f), over: true), 3);
        Assert.Equal(12.5f, OvergrowthFoliage.PatchMetersFor(VegetationBuild.Stock, Pal(50f, over: false), over: false), 3);
    }

    [Fact]
    public void Every_veg_build_uses_the_hardcoded_grid_whatever_the_map_says()
    {
        foreach (var b in new[] { VegetationBuild.VegLow, VegetationBuild.VegMedium, VegetationBuild.VegHigh, VegetationBuild.VegUltra })
        {
            Assert.Equal(12.5f, OvergrowthFoliage.PatchMetersFor(b, Pal(450f), over: true), 3);
            Assert.Equal(17.5f, OvergrowthFoliage.PatchMetersFor(b, Pal(50f, over: false), over: false), 3);
        }
    }

    /// <summary>A palette with no viewdistance has nothing to derive from, so it keeps the Veg grid.</summary>
    [Fact]
    public void Stock_falls_back_when_the_map_declares_no_view_distance()
    {
        Assert.Equal(12.5f, OvergrowthFoliage.PatchMetersFor(VegetationBuild.Stock, Pal(0f), over: true), 3);
        Assert.Equal(17.5f, OvergrowthFoliage.PatchMetersFor(VegetationBuild.Stock, null, over: false), 3);
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

    /// <summary>The whole point: on Saigon68's numbers the stock preview must be dramatically sparser than Ultra's.
    /// 112.5 m patches over a 1024 m world is 9x9; 12.5 m is 82x82 - about 80x fewer trees.</summary>
    [Fact]
    public void Stock_plants_far_fewer_trees_than_the_ultra_grid()
    {
        var growth = FullyPlanted(256);
        var cfg = new TerrainConfig { MaterialSize = 256, WorldSize = 1024, YScale = 1f };

        int ultra = OvergrowthFoliage.Scatter(growth, cfg, 12.5f, over: true).Count;
        int stock = OvergrowthFoliage.Scatter(growth, cfg,
            OvergrowthFoliage.PatchMetersFor(VegetationBuild.Stock, growth.OverPalette, over: true), over: true).Count;

        Assert.True(ultra > 10000, $"the Ultra grid should be dense, got {ultra}");
        Assert.True(stock < ultra / 50, $"stock should be far sparser: stock {stock} vs ultra {ultra}");
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
