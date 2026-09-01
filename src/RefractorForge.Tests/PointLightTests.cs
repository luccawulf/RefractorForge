using RefractorForge.Formats;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Placed lights.
///
/// Worth stating plainly, because it decides what these are for: Refractor renders no dynamic point lights. A
/// frame capture of the running game shows thousands of DIRECTIONAL lights and zero point or spot lights, and a
/// BFV "streetlight" is an EffectBundle of additive glow sprites that emits nothing. A real night map —
/// DC_Basrah_Nights — gets its look from near-black renderer ambient/diffuse in Init.con plus level-local
/// textures, with 74 lamp props whose light is baked, not cast.
///
/// So a light here is authoring data: it lights the viewport so a rig can be aimed, and it is baked into the
/// lightmaps the engine does read. The falloff below is duplicated in GLSL for the viewport, so these tests are
/// what keeps the preview honest about what the bake will produce.
/// </summary>
public class PointLightTests
{
    private static PointLight L(float x, float y, float z, float radius = 10f, float intensity = 1f) =>
        new() { Position = new Vec3(x, y, z), Radius = radius, Intensity = intensity };

    [Fact]
    public void A_light_is_brightest_at_its_centre_and_reaches_exactly_zero_at_its_radius()
    {
        var l = L(0, 0, 0, radius: 10f, intensity: 2f);

        float centre = l.Attenuation(0, 0, 0);
        Assert.Equal(2f, centre, 3);                       // intensity, undimmed

        // Monotonically down, and off entirely at the radius — a light that stopped short of zero would end on
        // a visible circle, which is the thing that makes a baked rig look wrong.
        float prev = centre;
        for (float d = 1f; d < 10f; d += 1f)
        {
            float a = l.Attenuation(d, 0, 0);
            Assert.True(a < prev, $"attenuation should fall with distance (at {d} m it did not)");
            Assert.True(a > 0f, $"still inside the radius at {d} m");
            prev = a;
        }
        Assert.Equal(0f, l.Attenuation(10f, 0, 0));
        Assert.Equal(0f, l.Attenuation(10.001f, 0, 0));
        Assert.Equal(0f, l.Attenuation(1000f, 0, 0));
    }

    [Fact]
    public void Distance_is_measured_in_three_dimensions()
    {
        var l = L(0, 0, 0, radius: 10f);
        // A lamp on a pole must dim things below it by their true distance, not their map distance.
        Assert.Equal(l.Attenuation(6f, 0f, 0f), l.Attenuation(0f, 6f, 0f), 5);
        Assert.Equal(l.Attenuation(6f, 0f, 0f), l.Attenuation(3.6f, 4.8f, 0f), 5);   // 3-4-5 scaled
    }

    [Fact]
    public void A_disabled_or_dark_light_contributes_nothing()
    {
        var l = L(0, 0, 0);
        l.Enabled = false;
        Assert.Equal(0f, l.Attenuation(1f, 0, 0));

        l.Enabled = true;
        l.Intensity = 0f;
        Assert.Equal(0f, l.Attenuation(1f, 0, 0));

        l.Intensity = 1f;
        l.Radius = 0f;
        Assert.Equal(0f, l.Attenuation(0f, 0, 0));
    }

    [Fact]
    public void Falloff_changes_the_shape_without_moving_the_ends()
    {
        var sharp = L(0, 0, 0, radius: 20f); sharp.Falloff = 4f;
        var flat = L(0, 0, 0, radius: 20f); flat.Falloff = 1f;

        // Same at both ends; the exponent only decides how quickly it gets there.
        Assert.Equal(sharp.Attenuation(0, 0, 0), flat.Attenuation(0, 0, 0), 4);
        Assert.Equal(0f, sharp.Attenuation(20f, 0, 0));
        Assert.Equal(0f, flat.Attenuation(20f, 0, 0));
        Assert.True(flat.Attenuation(10f, 0, 0) > sharp.Attenuation(10f, 0, 0),
            "a lower exponent should keep more light at mid range");
    }

    [Fact]
    public void Colours_add_where_lights_overlap()
    {
        var rig = new LightRig();
        var red = L(0, 0, 0, radius: 10f);
        red.ColorR = 1f; red.ColorG = 0f; red.ColorB = 0f;
        var blue = L(4, 0, 0, radius: 10f);
        blue.ColorR = 0f; blue.ColorG = 0f; blue.ColorB = 1f;
        rig.Lights.Add(red);
        rig.Lights.Add(blue);

        var (r, g, b) = rig.Illuminate(2f, 0f, 0f);
        Assert.True(r > 0f && b > 0f, "both lights reach the midpoint");
        Assert.Equal(0f, g, 5);

        // Outside both, nothing.
        var (r2, g2, b2) = rig.Illuminate(100f, 0f, 0f);
        Assert.Equal(0f, r2); Assert.Equal(0f, g2); Assert.Equal(0f, b2);
    }

    [Fact]
    public void Occlusion_is_only_asked_about_for_lights_that_actually_reach_the_point()
    {
        // The bake ray-marches terrain per light, which dominates its cost, so a light that cannot reach a
        // point must never be traced for it.
        var rig = new LightRig();
        var near = L(0, 0, 0, radius: 10f); near.Name = "near";
        var far = L(500, 0, 0, radius: 10f); far.Name = "far";
        rig.Lights.Add(near);
        rig.Lights.Add(far);

        var asked = new List<string>();
        rig.Illuminate(1f, 0f, 0f, l => { asked.Add(l.Name); return true; });
        Assert.Equal(new[] { "near" }, asked);

        // And a light told not to cast shadows is never traced either.
        asked.Clear();
        near.CastsShadows = false;
        rig.Illuminate(1f, 0f, 0f, l => { asked.Add(l.Name); return true; });
        Assert.Empty(asked);
    }

    [Fact]
    public void Occluded_lights_drop_out_of_the_result()
    {
        var rig = new LightRig();
        rig.Lights.Add(L(0, 0, 0, radius: 10f));
        Assert.True(rig.Illuminate(1f, 0f, 0f, _ => true).R > 0f);
        Assert.Equal(0f, rig.Illuminate(1f, 0f, 0f, _ => false).R);
    }

    [Fact]
    public void The_shader_only_fits_a_few_lights_so_the_ones_that_reach_furthest_win()
    {
        // Sorting by distance to the light's REACH rather than its centre: a big lamp further off can matter
        // more than a small one nearby, and centre-distance sorting drops exactly the wrong one.
        var rig = new LightRig();
        var smallClose = L(0, 0, 30f, radius: 2f); smallClose.Name = "small-close";
        var bigFar = L(0, 0, 200f, radius: 300f); bigFar.Name = "big-far";
        rig.Lights.Add(smallClose);
        rig.Lights.Add(bigFar);

        var picked = rig.Nearest(0, 0, 0, 1);
        Assert.Single(picked);
        Assert.Equal("big-far", picked[0].Name);
    }

    [Fact]
    public void Disabled_lights_never_take_a_shader_slot()
    {
        var rig = new LightRig();
        var off = L(0, 0, 1f); off.Name = "off"; off.Enabled = false;
        var on = L(0, 0, 50f); on.Name = "on";
        rig.Lights.Add(off);
        rig.Lights.Add(on);

        var picked = rig.Nearest(0, 0, 0, 8);
        Assert.Single(picked);
        Assert.Equal("on", picked[0].Name);
    }

    [Fact]
    public void The_rig_round_trips_through_its_sidecar_and_leaves_no_empty_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rflights_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var rig = new LightRig { NightAmount = 0.8f };
            var l = L(12f, 3f, 45f, radius: 33f, intensity: 1.7f);
            l.Name = "Lamp A"; l.ColorR = 0.9f; l.ColorG = 0.7f; l.ColorB = 0.4f;
            l.Falloff = 1.5f; l.CastsShadows = false;
            rig.Lights.Add(l);
            rig.Save(dir);

            var back = LightRig.Load(dir);
            Assert.Single(back.Lights);
            var b = back.Lights[0];
            Assert.Equal("Lamp A", b.Name);
            Assert.Equal(new Vec3(12f, 3f, 45f), b.Position);
            Assert.Equal(33f, b.Radius);
            Assert.Equal(1.7f, b.Intensity);
            Assert.Equal(1.5f, b.Falloff);
            Assert.False(b.CastsShadows);
            Assert.Equal(0.8f, back.NightAmount, 4);

            // An empty rig removes the sidecar rather than leaving a stub in the level folder.
            new LightRig().Save(dir);
            Assert.False(File.Exists(LightRig.PathFor(dir)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_sidecar_is_editor_only_and_never_reaches_a_packed_archive()
    {
        // These have no meaning to the engine - they reach the game only once baked - so packing the file
        // would put an unknown file into a level for no reason.
        Assert.True(LevelSaver.IsEditorOnlyFile(LightRig.FileName));
        Assert.True(LevelSaver.IsEditorOnlyFile("RefractorForgeLights.json"));
        Assert.False(LevelSaver.IsEditorOnlyFile("Init.con"));
    }
}
