using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates for painting a road into the ground texture. The failure modes here are silent and geometric — a patch
/// that covers the wrong world rect lands the road somewhere else on the map, and a hard-edged band reads as a
/// painted stripe rather than a worn track — so both are asserted rather than eyeballed.
/// </summary>
public class RoadRasterTests
{
    static List<RoadSample> Straight(float x0, float z0, float x1, float z1, float half, int n = 32)
    {
        var l = new List<RoadSample>();
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            l.Add(new RoadSample(x0 + (x1 - x0) * t, 0f, z0 + (z1 - z0) * t, half, len * t));
        }
        return l;
    }

    static byte AlphaAt(RoadPatch p, float worldX, float worldZ)
    {
        int px = (int)((worldX - p.WorldX) / p.WorldW * p.Width);
        int py = (int)((worldZ - p.WorldZ) / p.WorldH * p.Height);
        if (px < 0 || py < 0 || px >= p.Width || py >= p.Height) return 0;
        return p.Rgba[(py * p.Width + px) * 4 + 3];
    }

    [Fact]
    public void The_patch_covers_the_road_and_its_shoulders()
    {
        var patch = RoadRaster.Paint(Straight(500, 500, 700, 500, 5f), (200, 180, 140));

        // The world rect must contain every sample plus the half-width, or the road gets clipped.
        Assert.True(patch.WorldX <= 500 - 5f);
        Assert.True(patch.WorldX + patch.WorldW >= 700 + 5f);
        Assert.True(patch.WorldZ <= 500 - 5f);
        Assert.True(patch.WorldZ + patch.WorldH >= 500 + 5f);
        Assert.Equal(patch.Width * patch.Height * 4, patch.Rgba.Length);
    }

    [Fact]
    public void The_centreline_is_opaque_and_well_outside_is_untouched()
    {
        var patch = RoadRaster.Paint(Straight(500, 500, 700, 500, 6f), (200, 180, 140));

        Assert.Equal(255, AlphaAt(patch, 600, 500));      // dead centre
        Assert.Equal(255, AlphaAt(patch, 520, 500));
        Assert.Equal(0, AlphaAt(patch, 600, 500 + 8f));   // beyond the half-width
        Assert.Equal(0, AlphaAt(patch, 600, 500 - 8f));
    }

    [Fact]
    public void The_edge_fades_instead_of_stopping_dead()
    {
        var patch = RoadRaster.Paint(Straight(500, 500, 700, 500, 10f), (200, 180, 140), shoulder: 0.4f);

        // Walking out from the centre, coverage must fall monotonically and pass through partial values - a hard
        // edge would jump 255 -> 0 with nothing between, and would read as a painted stripe.
        byte prev = 255;
        bool sawPartial = false;
        for (float off = 0; off <= 10f; off += 0.5f)
        {
            byte a = AlphaAt(patch, 600, 500 + off);
            Assert.True(a <= prev, $"coverage rose at {off} m from the centre");
            if (a > 0 && a < 255) sawPartial = true;
            prev = a;
        }
        Assert.True(sawPartial, "the shoulder never produced a partial coverage value");
    }

    [Fact]
    public void A_bend_is_painted_along_its_whole_length()
    {
        // An L: the corner is where a naive per-segment rasteriser leaves a gap.
        var pts = new List<(float X, float Y, float Z, float HalfW)>
        {
            (400, 0, 400, 4f), (600, 0, 400, 4f), (600, 0, 600, 4f),
        };
        var samples = RoadSpline.Resample(pts, 2f);
        var patch = RoadRaster.Paint(samples, (200, 180, 140));

        // Assert against the CURVE, not against the control polyline: a centripetal Catmull-Rom through an L bows
        // the corner, so the midpoint of a leg is not on the road and testing there would be testing the wrong
        // thing. What must hold is that the painted band is continuous - every sample covered, and every gap
        // BETWEEN consecutive samples covered too, which is where a per-segment rasteriser leaves holes.
        for (int i = 0; i < samples.Count; i++)
        {
            Assert.True(AlphaAt(patch, samples[i].X, samples[i].Z) > 0,
                        $"sample {i} at {samples[i].X:0.#}/{samples[i].Z:0.#} was not painted");
            if (i > 0)
            {
                float mx = (samples[i - 1].X + samples[i].X) * 0.5f;
                float mz = (samples[i - 1].Z + samples[i].Z) * 0.5f;
                Assert.True(AlphaAt(patch, mx, mz) > 0, $"a gap between samples {i - 1} and {i}");
            }
        }
        // And the curve really does turn the corner: it must reach both ends of the L.
        Assert.True(samples.Any(s => s.Z > 550f), "the road never reached the second leg");
    }

    [Fact]
    public void The_patch_never_runs_off_the_map()
    {
        // A road hard against the west edge: the rect must clamp to the world rather than going negative.
        var patch = RoadRaster.Paint(Straight(3, 500, 200, 500, 8f), (200, 180, 140), worldSize: 2048f);
        Assert.True(patch.WorldX >= 0f);
        Assert.True(patch.WorldZ >= 0f);
        Assert.True(patch.WorldX + patch.WorldW <= 2048f + 0.01f);
    }

    [Fact]
    public void The_wire_form_round_trips()
    {
        var patch = RoadRaster.Paint(Straight(500, 500, 560, 500, 4f), (10, 20, 30));
        var wire = RoadRaster.ToWire(patch);
        var p = wire.Split(' ');

        Assert.Equal("ATLAS", p[0]);
        Assert.Equal(8, p.Length);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(patch.WorldX, float.Parse(p[1], ci), 2);
        Assert.Equal(patch.WorldZ, float.Parse(p[2], ci), 2);
        Assert.Equal(patch.WorldW, float.Parse(p[3], ci), 2);
        Assert.Equal(patch.WorldH, float.Parse(p[4], ci), 2);
        Assert.Equal(patch.Width, int.Parse(p[5], ci));
        Assert.Equal(patch.Height, int.Parse(p[6], ci));
        Assert.Equal(patch.Rgba, Convert.FromBase64String(p[7]));
        // The wire is space-delimited by fixed position, so no field may contain a space.
        Assert.DoesNotContain(' ', p[7]);
    }

    [Fact]
    public void A_road_needs_at_least_one_sample()
    {
        Assert.Throws<ArgumentException>(() => RoadRaster.Paint(new List<RoadSample>(), (1, 2, 3)));
    }
}
