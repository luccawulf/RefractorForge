using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates for "where can I build?". The interesting one is the hedgerow case: judging a site by its single
/// steepest cell rejects a perfectly level field for one ditch crossing it, which is exactly what happened on
/// Bocage - a 90 m patch with 1.7 m of height spread was thrown out for a 13.4 degree bank.
/// </summary>
public class SiteFinderTests
{
    const int Side = 128;

    static (Heightmap Hm, TerrainConfig Cfg) Map(Func<int, int, float> metres)
    {
        var cfg = new TerrainConfig { WorldSize = 1024, MaterialSize = Side, YScale = 1f, WaterLevel = 5f };
        var hm = new Heightmap(Side, Side);
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
                hm[x, y] = cfg.MetersToRaw(metres(x, y));
        return (hm, cfg);
    }

    [Fact]
    public void A_flat_plateau_is_found_and_a_hillside_is_not()
    {
        // Left half flat at 40 m, right half climbing steeply.
        var (hm, cfg) = Map((x, y) => x < Side / 2 ? 40f : 40f + (x - Side / 2) * 3f);

        var sites = SiteFinder.Find(hm, cfg, radius: 60f, maxSlopeDeg: 12f, maxSpread: 3f, max: 4);

        Assert.NotEmpty(sites);
        foreach (var s in sites)
        {
            Assert.True(SiteFinder.Meets(s, 3f, 0.05f), $"returned a site that does not meet the limits: {s}");
            Assert.True(s.X < cfg.WorldSize * 0.5f, $"site at X={s.X:0} is on the hillside, not the plateau");
        }
    }

    [Fact]
    public void One_ditch_across_a_level_field_does_not_disqualify_it()
    {
        // 4 m cells, like a real 2 km/512 level - the ratio of cell size to patch size is what decides how much
        // of a patch one bank occupies, so it has to match reality for this test to mean anything.
        var cfg = new TerrainConfig { WorldSize = Side * 4, MaterialSize = Side, YScale = 1f, WaterLevel = 5f };
        var field = new Heightmap(Side, Side);
        var hill = new Heightmap(Side, Side);
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                field[x, y] = cfg.MetersToRaw(y == Side / 2 ? 44f : 40f);   // level, one bank across it
                hill[x, y] = cfg.MetersToRaw(40f + y * 1.0f);               // an evenly steep hillside
            }

        // Search a band that STRADDLES the bank, so every candidate contains it. Left unconstrained the finder
        // quite correctly prefers a patch of the same field that misses the bank altogether, and the test would
        // be measuring clean ground rather than the case it is named after.
        float bankZ = Side / 2 * cfg.HorizontalSpacing;
        var flat = SiteFinder.Find(field, cfg, radius: 100f, maxSlopeDeg: 12f, maxSpread: 6f, max: 1,
                                   minZ: bankZ - 100f, maxZ: bankZ + 100f)[0];
        var slope = SiteFinder.Find(hill, cfg, radius: 100f, maxSlopeDeg: 12f, maxSpread: 1000f, max: 1)[0];

        // The bank makes the WORST cell steep - a max-slope rule would throw this field out...
        Assert.True(flat.MaxSlopeDeg > 12f, "the test map should contain a genuinely steep cell");
        // ...but it is a sliver of the patch, and the measure has to tell that apart from real slope.
        Assert.True(flat.SteepFraction < 0.10f, $"{flat.SteepFraction:P1} of the level field counted as steep");
        Assert.True(slope.SteepFraction > 0.9f, $"only {slope.SteepFraction:P1} of a hillside counted as steep");
        Assert.True(SiteFinder.Meets(flat, 6f, 0.10f), "a field crossed by one bank should still pass");
        Assert.False(SiteFinder.Meets(slope, 6f, 0.10f), "a hillside must not pass");
    }

    [Fact]
    public void Ground_under_the_water_line_is_never_returned()
    {
        // Everything below water except one dry island in the corner.
        var (hm, cfg) = Map((x, y) => x < 40 && y < 40 ? 30f : 1f);

        var sites = SiteFinder.Find(hm, cfg, radius: 50f, maxSlopeDeg: 20f, maxSpread: 40f,
                                    avoidWater: true, waterClearance: 1f, max: 10);

        foreach (var s in sites)
            Assert.True(s.Height > cfg.WaterLevel, $"site at {s.X:0}/{s.Z:0} sits at {s.Height:0.#} m, under the {cfg.WaterLevel} m water line");
    }

    [Fact]
    public void When_nothing_meets_the_limits_the_flattest_ground_is_still_reported()
    {
        // A continuous slope: no patch anywhere is flat, so a strict search has no true answer.
        var (hm, cfg) = Map((x, y) => 20f + x * 1.5f);

        var strict = SiteFinder.Find(hm, cfg, radius: 60f, maxSlopeDeg: 1f, maxSpread: 0.5f, max: 3);

        // Best-effort rather than an empty list - "no" tells the caller nothing about what IS there.
        Assert.NotEmpty(strict);
        Assert.All(strict, s => Assert.False(SiteFinder.Meets(s, 0.5f, 0.05f)));
        // And it is still RANKED: the flattest available comes first.
        for (int i = 1; i < strict.Count; i++)
            Assert.True(strict[i - 1].HeightSpread <= strict[i].HeightSpread);
    }

    [Fact]
    public void Results_are_distinct_places_not_the_same_patch_nine_times()
    {
        var (hm, cfg) = Map((x, y) => 40f);   // uniformly flat: every candidate is equally good

        var sites = SiteFinder.Find(hm, cfg, radius: 60f, maxSlopeDeg: 12f, maxSpread: 1f, max: 5);

        Assert.True(sites.Count > 1);
        for (int i = 0; i < sites.Count; i++)
            for (int j = i + 1; j < sites.Count; j++)
            {
                float dx = sites[i].X - sites[j].X, dz = sites[i].Z - sites[j].Z;
                Assert.True(MathF.Sqrt(dx * dx + dz * dz) >= 60f, "two returned sites overlap heavily");
            }
    }

    [Fact]
    public void The_search_can_be_restricted_to_a_region()
    {
        var (hm, cfg) = Map((x, y) => 40f);

        var sites = SiteFinder.Find(hm, cfg, radius: 50f, maxSlopeDeg: 12f, maxSpread: 1f, max: 6,
                                    minX: 600f, minZ: 600f, maxX: 900f, maxZ: 900f);

        Assert.NotEmpty(sites);
        Assert.All(sites, s =>
        {
            Assert.InRange(s.X, 600f, 900f);
            Assert.InRange(s.Z, 600f, 900f);
        });
    }

    [Fact]
    public void Probe_reports_height_slope_and_whether_it_is_wet()
    {
        var (hm, cfg) = Map((x, y) => x < Side / 2 ? 2f : 50f);

        var wet = SiteFinder.Probe(hm, cfg, 100f, 500f);
        Assert.True(wet.UnderWater);
        Assert.Equal(3f, wet.DepthBelowWater, 1);      // water 5 m, ground 2 m
        Assert.Equal(2f, wet.Height, 1);

        var dry = SiteFinder.Probe(hm, cfg, 900f, 500f);
        Assert.False(dry.UnderWater);
        Assert.Equal(0f, dry.DepthBelowWater);
        Assert.Equal(50f, dry.Height, 1);

        // Right on the step between them the slope must be large.
        var edge = SiteFinder.Probe(hm, cfg, cfg.WorldSize * 0.5f, 500f);
        Assert.True(edge.SlopeDeg > 45f, $"slope at the cliff read {edge.SlopeDeg:0.#} deg");
    }
}
