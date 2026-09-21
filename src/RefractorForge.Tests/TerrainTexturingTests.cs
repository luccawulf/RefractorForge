using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Rule-based terrain texturing: the measurements a rule tests (slope, altitude, flow, distance to the shore) and the
/// promises the generator makes to a map - it never paints the out-of-bounds material, it can leave hand-painted work
/// alone, and the same terrain with the same seed gives the same map every time.
/// </summary>
public class TerrainTexturingTests
{
    // YScale 4 => height in metres is raw * 4 / 256, so a 16-bit map reaches 1024 m and nothing here saturates
    // (at YScale 1 the ceiling is 256 m, which silently flattened the ramps these tests are built on).
    private static TerrainConfig Cfg(int size = 64, int world = 512, float water = 10f) => new()
    { MaterialSize = size, WorldSize = world, YScale = 4f, WaterLevel = water };

    /// <summary>A heightmap from a function of the cell, in METRES, converted through the level's own YScale.</summary>
    private static Heightmap Make(int size, Func<int, int, float> meters, TerrainConfig? cfg = null)
    {
        float yScale = cfg?.YScale ?? 4f;
        var hm = new Heightmap(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                hm[x, y] = (ushort)Math.Clamp(meters(x, y) * 256f / yScale, 0f, 65535f);
        return hm;
    }

    [Fact]
    public void Slope_is_measured_in_degrees_from_the_ground()
    {
        // 8 m per cell horizontally (512/64), rising 8 m per cell = a 45 degree ramp.
        var cfg = Cfg();
        var ta = TerrainAnalysis.From(cfg, Make(64, (x, _) => 20f + x * 8f));
        int mid = 32 * 64 + 32;
        Assert.Equal(45f, ta.SlopeDeg[mid], 1f);

        var flat = TerrainAnalysis.From(cfg, Make(64, (_, _) => 40f));
        Assert.Equal(0f, flat.SlopeDeg[mid], 0.01f);
    }

    [Fact]
    public void Altitude_is_zero_at_the_water_line_and_one_at_the_peak()
    {
        var cfg = Cfg(water: 10f);
        var ta = TerrainAnalysis.From(cfg, Make(64, (x, _) => 10f + x));      // 10 m (water) .. 73 m
        Assert.Equal(0f, ta.Altitude[32 * 64 + 0], 0.01f);
        Assert.Equal(1f, ta.Altitude[32 * 64 + 63], 0.01f);
        Assert.Equal(63f, ta.PeakAboveWater, 0.5f);
    }

    [Fact]
    public void Water_runs_downhill_and_piles_up_in_the_valley()
    {
        // A V-shaped valley along x = 32: every cell drains toward the middle column.
        var ta = TerrainAnalysis.From(Cfg(), Make(64, (x, y) => 30f + Math.Abs(x - 32) * 2f - y * 0.5f));
        float valley = ta.Flow[40 * 64 + 32];
        float hillside = ta.Flow[40 * 64 + 10];
        Assert.True(valley > hillside * 1.5f, $"valley {valley:0.000} should carry far more water than the side {hillside:0.000}");
        Assert.InRange(valley, 0f, 1f);
    }

    [Fact]
    public void Convexity_separates_a_ridge_from_a_gully()
    {
        var ridge = TerrainAnalysis.From(Cfg(), Make(64, (x, _) => 40f - Math.Abs(x - 32) * 3f));
        var gully = TerrainAnalysis.From(Cfg(), Make(64, (x, _) => 40f + Math.Abs(x - 32) * 3f));
        int top = 32 * 64 + 32;
        Assert.True(ridge.Convexity[top] > 0.3f, $"ridge line should bulge, got {ridge.Convexity[top]:0.00}");
        Assert.True(gully.Convexity[top] < -0.3f, $"gully floor should hollow, got {gully.Convexity[top]:0.00}");
    }

    [Fact]
    public void Shore_distance_is_in_metres_and_is_infinite_on_a_map_with_no_water()
    {
        var cfg = Cfg();                                                       // water line 10 m, 8 m per cell
        var ta = TerrainAnalysis.From(cfg, Make(64, (x, _) => x < 10 ? 5f : 12f + x));
        Assert.Equal(0f, ta.ShoreMeters[32 * 64 + 5], 0.01f);                  // under water
        int firstDry = Enumerable.Range(0, 64).First(x => ta.ShoreMeters[32 * 64 + x] > 0f);
        Assert.Equal(8f, ta.ShoreMeters[32 * 64 + firstDry], 0.5f);            // one cell (8 m) from the water
        Assert.Equal(40f, ta.ShoreMeters[32 * 64 + firstDry + 4], 1f);         // five cells in

        var dry = TerrainAnalysis.From(cfg, Make(64, (_, _) => 50f));
        Assert.Equal(float.MaxValue, dry.ShoreMeters[0]);
    }

    [Fact]
    public void A_rule_with_no_limits_paints_the_whole_map_and_later_rules_paint_over_it()
    {
        var cfg = Cfg();
        var hm = Make(64, (x, _) => 20f + x * 8f);                             // a 45 degree ramp everywhere
        var rules = new List<MaterialRule>
        {
            new() { Name = "base", Material = 1 },
            new() { Name = "cliffs", Material = 9, MinSlopeDeg = 40f, EdgeSoftness = 0f },
        };
        var map = TerrainTexturing.Generate(cfg, hm, rules);
        Assert.Equal(9, map[32, 32]);                                          // the steep rule wins where it applies

        var justBase = TerrainTexturing.Generate(cfg, hm, new List<MaterialRule> { rules[0] });
        for (int y = 0; y < 64; y += 7)
            for (int x = 0; x < 64; x += 7)
                Assert.Equal(1, justBase[x, y]);
    }

    [Fact]
    public void The_out_of_bounds_material_is_never_painted_and_can_be_kept()
    {
        var cfg = Cfg();
        var hm = Make(64, (_, _) => 40f);
        var existing = new MaterialMap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                existing[x, y] = (byte)(x < 8 ? DeathMaterial.Index : 12);     // void strip, and paved road elsewhere

        // A rule that asks for deathMaterial is ignored outright.
        var map = TerrainTexturing.Generate(cfg, hm, new List<MaterialRule>
        { new() { Material = 1 }, new() { Material = DeathMaterial.Index } });
        for (int i = 0; i < 64; i++) Assert.NotEqual(DeathMaterial.Index, map[i, i]);

        // And the existing void + roads survive when asked.
        var kept = TerrainTexturing.Generate(cfg, hm, new List<MaterialRule> { new() { Material = 1 } },
                                             new TexturingOptions { KeepOutOfBounds = true, KeepRoads = true }, existing);
        Assert.Equal(DeathMaterial.Index, kept[3, 20]);
        Assert.Equal(12, kept[40, 20]);

        var overwritten = TerrainTexturing.Generate(cfg, hm, new List<MaterialRule> { new() { Material = 1 } },
                                                    new TexturingOptions { KeepOutOfBounds = false, KeepRoads = false }, existing);
        Assert.Equal(1, overwritten[3, 20]);
        Assert.Equal(1, overwritten[40, 20]);
    }

    [Fact]
    public void The_same_terrain_and_seed_give_the_same_map_and_a_different_seed_moves_the_dither()
    {
        var cfg = Cfg();
        var hm = Make(64, (x, y) => 12f + x * 0.7f + y * 0.3f);
        var rules = TexturingPresets_Tropical();

        var a = TerrainTexturing.Generate(cfg, hm, rules, new TexturingOptions { Seed = 7 });
        var b = TerrainTexturing.Generate(cfg, hm, rules, new TexturingOptions { Seed = 7 });
        var c = TerrainTexturing.Generate(cfg, hm, rules, new TexturingOptions { Seed = 8 });
        Assert.Equal(a.Samples, b.Samples);
        Assert.NotEqual(a.Samples, c.Samples);
    }

    private static List<MaterialRule> TexturingPresets_Tropical() => TexturingStyle.TropicalIsland().Build();

    [Theory]
    [InlineData("Tropical island")]
    [InlineData("River delta")]
    [InlineData("Highland")]
    [InlineData("Desert")]
    [InlineData("Temperate")]
    public void Every_style_paints_a_varied_map_of_real_materials(string name)
    {
        var cfg = Cfg(128, 1024, 15f);
        // An island: a hill in the middle, water around the edges.
        var hm = Make(128, (x, y) =>
        {
            float dx = (x - 64) / 64f, dy = (y - 64) / 64f;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            return 60f * MathF.Max(0f, 1f - d) + 5f + (x % 7) * 0.4f + (y % 5) * 0.3f;
        });

        var style = TexturingStyle.ByName(name);
        Assert.Equal(name, style.Name);
        var map = TerrainTexturing.Generate(cfg, hm, style.Build());

        var used = new HashSet<int>();
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                int m = map[x, y];
                Assert.NotEqual(DeathMaterial.Index, m);
                Assert.InRange(m, 0, 15);
                used.Add(m);
            }
        Assert.True(used.Count >= 4, $"{name} used only {used.Count} materials: {string.Join(",", used.OrderBy(v => v))}");
        // The ground under the water is painted like land - retail levels put the "Water" material on 0.00% of
        // submerged ground, because the engine draws the water surface over it.
        Assert.DoesNotContain(TexturingStyle.MatWater, used);
        int submerged = 0, sand = 0;
        var ta = TerrainAnalysis.From(cfg, hm);
        for (int i = 0; i < ta.AboveWater.Length; i++)
            if (ta.AboveWater[i] < -1f) { submerged++; if (map[i % 128, i / 128] != 0) sand++; }
        Assert.True(submerged > 100, "the test island should have a sea bed to paint");
        Assert.True(sand > submerged / 2, $"{name} left most of its sea bed unpainted ({sand}/{submerged})");
    }

    [Fact]
    public void Turning_the_knobs_changes_the_map_the_way_the_label_says()
    {
        var cfg = Cfg(128, 1024, 15f);
        var hm = Make(128, (x, y) =>
        {
            float dx = (x - 64) / 64f, dy = (y - 64) / 64f;
            return 60f * MathF.Max(0f, 1f - MathF.Sqrt(dx * dx + dy * dy)) + 5f;
        });

        int Count(MaterialMap m, int mat)
        {
            int c = 0;
            for (int y = 0; y < m.Height; y++) for (int x = 0; x < m.Width; x++) if (m[x, y] == mat) c++;
            return c;
        }

        var narrow = TexturingStyle.TropicalIsland(); narrow.BeachMeters = 4f;
        var wide = TexturingStyle.TropicalIsland(); wide.BeachMeters = 60f;
        Assert.True(Count(TerrainTexturing.Generate(cfg, hm, wide.Build()), TexturingStyle.MatDrySand)
                  > Count(TerrainTexturing.Generate(cfg, hm, narrow.Build()), TexturingStyle.MatDrySand),
                    "a wider beach should paint more sand");

        // Highland is the style that paints steep ground as rock (the others use dirt, the way retail does).
        var gentle = TexturingStyle.Highland(); gentle.CliffDeg = 20f;
        var steep = TexturingStyle.Highland(); steep.CliffDeg = 60f;
        Assert.True(Count(TerrainTexturing.Generate(cfg, hm, gentle.Build()), TexturingStyle.MatRock)
                  > Count(TerrainTexturing.Generate(cfg, hm, steep.Build()), TexturingStyle.MatRock),
                    "calling gentler ground a cliff should paint more rock");
    }
}
