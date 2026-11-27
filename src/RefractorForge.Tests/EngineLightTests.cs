using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The game's own light formula (<see cref="EngineLight"/>) and the daylight it is fitted to (<see cref="Daylight"/>).
/// The numbers in the first group come from BfVietnam.exe and its shaders; if one of these fails, the editor has
/// stopped agreeing with the game about what a lightmap value means.
/// </summary>
public class EngineLightTests
{
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.64f, 0.34f, -0.68f));   // Operation_Irving
    private static readonly EngineLight.Colours Irving = new(
        new Vector3(0.975f, 1f, 0.95f), new Vector3(0.55f, 0.55f, 0.525f), new Vector3(0.1f), new Vector3(0.25f));

    private static void Near(float want, float got, float tol = 1e-4f) => Assert.InRange(got, want - tol, want + tol);

    [Fact]
    public void The_back_light_is_the_sun_mirrored_to_the_opposite_bearing_at_the_same_height()
    {
        var b = EngineLight.BackLight(Sun);
        Near(-Sun.X, b.X); Near(Sun.Y, b.Y); Near(-Sun.Z, b.Z);
        // So level ground sees both lights equally, and a wall sees exactly one of them.
        Near(Vector3.Dot(Vector3.UnitY, Sun), Vector3.Dot(Vector3.UnitY, b));
        var wall = Vector3.Normalize(new Vector3(Sun.X, 0, Sun.Z));
        Assert.True(Vector3.Dot(wall, Sun) > 0 && Vector3.Dot(wall, b) < 0);
        Assert.True(Vector3.Dot(-wall, Sun) < 0 && Vector3.Dot(-wall, b) > 0);
    }

    [Fact]
    public void A_lightmapped_surface_is_the_lightmap_times_both_lights_plus_the_ambient_with_no_doubling()
    {
        // A wall facing the sun's bearing on Irving, lightmap at full: sun * N.L + LMambient, and nothing from the back.
        var wall = Vector3.Normalize(new Vector3(Sun.X, 0, Sun.Z));
        float ndl = Vector3.Dot(wall, Sun);
        var lit = EngineLight.Lightmapped(1f, wall, Sun, Irving);
        Near(MathF.Min(1f, 0.975f * ndl + 0.25f), lit.X);
        // The same wall with the lightmap at zero keeps the ambient and nothing else.
        var dark = EngineLight.Lightmapped(0f, wall, Sun, Irving);
        Near(0.25f, dark.X); Near(0.25f, dark.Y); Near(0.25f, dark.Z);
        // Half the lightmap is half the LIGHTS, not half the result.
        var half = EngineLight.Lightmapped(0.5f, wall, Sun, Irving);
        Near(0.5f * 0.975f * ndl + 0.25f, half.X);
        // The wall facing away is lit by the back light alone.
        var back = EngineLight.Lightmapped(1f, -wall, Sun, Irving);
        Near(0.55f * ndl + 0.25f, back.X);
    }

    [Fact]
    public void A_face_that_looks_down_shows_the_ambient_whatever_its_lightmap_says()
    {
        var down = -Vector3.UnitY;
        Assert.Equal(EngineLight.Lightmapped(0f, down, Sun, Irving), EngineLight.Lightmapped(1f, down, Sun, Irving));
        Near(0.25f, EngineLight.Lightmapped(1f, down, Sun, Irving).X);
    }

    [Fact]
    public void An_object_without_a_lightmap_uses_the_other_ambient_and_the_material_colours_scale_their_own_terms()
    {
        var up = Vector3.UnitY;
        var d = EngineLight.Dynamic(up, Sun, Irving);
        Near(MathF.Min(1f, (0.975f + 0.55f) * Sun.Y + 0.1f), d.X);
        var m = new EngineLight.Material(new Vector3(0.5f), new Vector3(0.8f), new Vector3(0.3f));
        var dm = EngineLight.Dynamic(up, Sun, Irving, m);
        Near(MathF.Min(1f, 0.5f * (0.975f + 0.55f) * Sun.Y + 0.8f * 0.1f + 0.3f), dm.X);
    }

    [Fact]
    public void Solving_for_the_lightmap_gives_back_the_value_that_was_drawn()
    {
        var rnd = new Random(7);
        int tried = 0;
        for (int i = 0; i < 400; i++)
        {
            var n = Vector3.Normalize(new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 1.2f - 0.2f, (float)rnd.NextDouble() * 2 - 1));
            float p = (float)rnd.NextDouble();
            // Only where the lights reach the face and nothing clips - elsewhere the value is not recoverable by design.
            if (EngineLight.Luma(EngineLight.Direct(n, Sun, Irving)) < 0.1f) continue;
            var drawn = p * EngineLight.Direct(n, Sun, Irving) + Irving.LmAmbient;
            if (MathF.Max(drawn.X, MathF.Max(drawn.Y, drawn.Z)) > 1f) continue;
            Near(p, EngineLight.SolvePrelight(drawn, n, Sun, Irving), 1e-3f);
            tried++;
        }
        Assert.True(tried > 100);
    }

    [Fact]
    public void A_target_out_of_reach_clamps_and_says_by_how_much()
    {
        var wall = Vector3.Normalize(new Vector3(Sun.X, 0, Sun.Z));
        var m = EngineLight.Material.Default;
        Assert.Equal(0f, EngineLight.SolvePrelight(new Vector3(0.1f), wall, Sun, Irving));      // darker than the ambient
        Assert.Equal(1f, EngineLight.SolvePrelight(new Vector3(5f), wall, Sun, Irving));        // brighter than both lights
        Near(0.1f - 0.25f, EngineLight.OutOfReach(new Vector3(0.1f), wall, Sun, Irving, m));
        Assert.Equal(0f, EngineLight.OutOfReach(new Vector3(0.5f), wall, Sun, Irving, m));
        // A ceiling can only ever be the ambient: anything brighter is out of reach by the whole difference.
        Near(0.6f - 0.25f, EngineLight.OutOfReach(new Vector3(0.6f), -Vector3.UnitY, Sun, Irving, m));
    }

    // ---- daylight ----------------------------------------------------------------------------------------------

    private static DaylightModel Day(float elevDeg = 45f, float sky = 0.2f, float peak = 1.05f)
    {
        float e = elevDeg * MathF.PI / 180f;
        return new Daylight { ToSun = new Vector3(MathF.Cos(e) * 0.6f, MathF.Sin(e), MathF.Cos(e) * 0.8f), SkyStrength = sky, Peak = peak }.Build();
    }

    [Fact]
    public void The_sky_delivers_its_strength_to_level_ground_and_the_sun_delivers_one()
    {
        var d = Day(sky: 0.37f);
        Near(0.37f, EngineLight.Luma(d.SkyIrradianceUp), 1e-3f);
        Near(1f, EngineLight.Luma(d.SunIrradiance), 1e-4f);
        // Level ground sees the whole sky and none of the ground: the direct sum and the integral agree.
        var up = d.OpenIrradiance(Vector3.UnitY);
        Near(EngineLight.Luma(d.OpenGroundIrradiance), EngineLight.Luma(up), 0.01f);
        // A face that looks straight down sees only the ground.
        var down = d.OpenIrradiance(-Vector3.UnitY);
        Near(EngineLight.Luma(d.GroundRadiance) * MathF.PI, EngineLight.Luma(down), 0.01f);
    }

    [Fact]
    public void A_surface_facing_the_sun_is_drawn_at_the_peak_and_display_and_linear_are_inverses()
    {
        var d = Day(peak: 0.9f);
        Near(0.9f, EngineLight.Luma(d.OpenDisplay(d.ToSun)), 0.02f);
        var lin = new Vector3(0.3f, 0.2f, 0.05f);
        var back = d.FromDisplay(d.ToDisplay(lin));
        Near(lin.X, back.X, 1e-4f); Near(lin.Y, back.Y, 1e-4f); Near(lin.Z, back.Z, 1e-4f);
    }

    [Theory]
    [InlineData(20f, 0.2f)]
    [InlineData(45f, 0.2f)]
    [InlineData(70f, 0.2f)]
    [InlineData(45f, 0.5f)]
    [InlineData(35f, 1.2f)]
    public void Open_shade_lands_where_retail_levels_paint_their_shadows(float elev, float sky)
    {
        // Every retail level's tiles carry shadows at 0.45-0.78 of the lit ground beside them (clear to hazy);
        // overcast runs higher. Linear light alone would put clear-sky shade near 0.2.
        var f = Day(elev, sky).Fit();
        Assert.InRange(f.OpenShade, 0.40f, 0.92f);
        Near(1f - f.OpenShade, f.ShadowAlpha, 1e-4f);
    }

    [Theory]
    [InlineData(20f, 0.2f, 0.15f)]
    [InlineData(35f, 0.2f, 0.15f)]
    [InlineData(50f, 0.2f, 0.15f)]
    [InlineData(70f, 0.2f, 0.15f)]
    [InlineData(50f, 0.45f, 0.15f)]
    // Overcast is where the engine is weakest: the light comes from everywhere, and it has two directions and a
    // constant to say so with. The fit raises the constant, and still lands further off than under a clear sky.
    [InlineData(35f, 1.2f, 0.22f)]
    public void The_fitted_colours_let_the_engine_reach_open_daylight(float elev, float sky, float shortBy)
    {
        var d = Day(elev, sky);
        var c = d.Fit().Colours;
        // Inside what the engine accepts.
        foreach (var v in new[] { c.Sun, c.Back, c.Ambient, c.LmAmbient })
            Assert.True(v.X >= 0 && v.Y >= 0 && v.Z >= 0 && MathF.Max(v.X, MathF.Max(v.Y, v.Z)) <= DaylightModel.SunLimit + 1e-3f);
        Assert.True(MathF.Max(c.Sun.X, MathF.Max(c.Sun.Y, c.Sun.Z)) <= DaylightModel.DefaultMaxSun + 1e-3f);
        // The floor sits below open shade: a lightmap must be able to tell an interior from the open air.
        Assert.True(EngineLight.Luma(c.LmAmbient) < EngineLight.Luma(d.OpenDisplay(-Vector3.Normalize(new Vector3(d.ToSun.X, 0, d.ToSun.Z)))));

        // With the lightmap at full, surfaces in the open are close to what they should be - and where a light
        // reaches a face strongly, never far short of it, because a lightmap cannot add what is missing.
        double worstShort = 0, sumAbs = 0; int n = 0;
        for (int i = 0; i < 600; i++)
        {
            float y = 1f - (i + 0.5f) / 600f * 1.05f;                    // straight up to a little below level
            float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y)), a = i * 2.399963f;
            var nrm = new Vector3(MathF.Cos(a) * r, y, MathF.Sin(a) * r);
            float want = MathF.Min(1f, EngineLight.Luma(d.OpenDisplay(nrm)));
            float got = EngineLight.Luma(EngineLight.Lightmapped(1f, nrm, d.ToSun, c));
            float lights = EngineLight.Luma(EngineLight.Direct(nrm, d.ToSun, c));
            if (lights > 0.35f) worstShort = Math.Max(worstShort, want - got);
            sumAbs += Math.Abs(got - want); n++;
        }
        Assert.True(worstShort < shortBy, $"a well-lit facing falls {worstShort:0.00} short of open daylight");
        Assert.True(sumAbs / n < 0.16, $"mean distance from open daylight {sumAbs / n:0.00}");
    }

    [Fact]
    public void A_night_level_can_keep_its_sun_bright_enough_for_lamps()
    {
        var night = Day(55f, 0.3f, peak: 0.3f);
        var plain = night.Fit().Colours;
        var lamps = night.Fit(minSun: 0.6f).Colours;
        Assert.True(EngineLight.Luma(plain.Sun) < 0.45f);
        Assert.True(EngineLight.Luma(lamps.Sun) >= 0.59f);
        // The moonlit wall is then written LOW in the map, which is what leaves a lamp its headroom.
        var wall = Vector3.Normalize(new Vector3(night.ToSun.X, 0.2f, night.ToSun.Z));
        float p = EngineLight.SolvePrelight(night.OpenDisplay(wall), wall, night.ToSun, lamps);
        Assert.InRange(p, 0.02f, 0.6f);
    }
}
