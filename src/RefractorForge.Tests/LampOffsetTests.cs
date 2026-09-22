using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Where a lamp's light goes: in its bulb, turned the way the lamp faces.
///
/// A lamp object's origin is at its base, and a street lamp hangs its bulb out on an arm, so neither the origin nor
/// the point above it is where the light belongs. The bulb was once stored as a fixed WORLD offset measured on one
/// lamp facing -89 degrees - right for every lamp facing that way and wrong for the rest: al_vietnas's 49 street
/// lamps face four directions, and on 34 of them the light landed up to 1.34 m off, on the pole side of the arm.
/// It is now the bulb's position in the lamp's own axes, read off its mesh, and every test below uses real
/// placements from that map.
/// </summary>
public class LampOffsetTests
{
    private const string Lamp = "dc_streetlamp2_m1";
    private static Vec3 Yaw(float degrees) => new(degrees, 0f, 0f);

    // The lamp whose light was placed by eye, and that light.
    private static readonly Vec3 MeasuredLamp = new(464.479f, 26.6187f, 343.854f);
    private static readonly Vec3 MeasuredGlow = new(465.1482f, 32.67572f, 343.854f);

    [Fact]
    public void The_bulb_read_off_the_mesh_agrees_with_the_light_placed_by_eye()
    {
        // Two independent sources: the mesh's shade apex, and a glow a mapper put in the head by hand. Turned by
        // that lamp's rotation, the first lands within 5 cm of the second.
        var a = LightPool.LightAnchor(Lamp, MeasuredLamp, Yaw(-89.125f));
        Assert.True(MathF.Abs(a.X - MeasuredGlow.X) < 0.05f, $"X {a.X} vs {MeasuredGlow.X}");
        Assert.True(MathF.Abs(a.Y - MeasuredGlow.Y) < 0.06f, $"Y {a.Y} vs {MeasuredGlow.Y}");
        Assert.True(MathF.Abs(a.Z - MeasuredGlow.Z) < 0.05f, $"Z {a.Z} vs {MeasuredGlow.Z}");
    }

    [Fact]
    public void The_lamp_in_the_screenshot_gets_its_light_in_the_head()
    {
        // 467.233/26.4469/243.528, rotation -89.125: the lamp the report came from. Its light used to sit over the
        // object's origin - between pole and head - at the preset's height.
        var a = LightPool.LightAnchor(Lamp, new Vec3(467.233f, 26.4469f, 243.528f), Yaw(-89.125f));
        Assert.Equal(467.904f, a.X, 2);
        Assert.Equal(32.551f, a.Y, 2);
        Assert.Equal(243.518f, a.Z, 2);
    }

    [Theory]
    [InlineData(-90f)]
    [InlineData(-270f)]     // = +90: the 18 lamps a fixed world offset put on the pole side, 1.34 m off
    [InlineData(0f)]
    [InlineData(-181.281f)]
    [InlineData(89.375f)]
    public void The_bulb_turns_with_the_lamp(float yaw)
    {
        // Whichever way the post faces, the bulb is the same 0.6706 m out along the arm and 6.1042 m up - and the
        // arm points where the lamp points, which a fixed offset cannot do.
        var p = new Vec3(100f, 20f, 100f);
        var a = LightPool.LightAnchor(Lamp, p, Yaw(yaw));
        float reach = MathF.Sqrt((a.X - p.X) * (a.X - p.X) + (a.Z - p.Z) * (a.Z - p.Z));
        Assert.Equal(0.6706f, reach, 3);
        Assert.Equal(p.Y + 6.1042f, a.Y, 3);

        // ...and in the direction the mesh's own transform turns local -Z: at yaw -90 that is world +X.
        var m = System.Numerics.Matrix4x4.CreateFromYawPitchRoll(yaw * MathF.PI / 180f, 0f, 0f);
        var dir = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0f, 0f, -1f), m);
        Assert.Equal(p.X + dir.X * 0.6706f, a.X, 3);
        Assert.Equal(p.Z + dir.Z * 0.6706f, a.Z, 3);
    }

    [Fact]
    public void Facing_the_other_way_puts_the_bulb_on_the_other_side()
    {
        // The bug in one line: -90 and +90 must not share a world offset.
        var p = new Vec3(0f, 0f, 0f);
        var east = LightPool.LightAnchor(Lamp, p, Yaw(-90f));
        var west = LightPool.LightAnchor(Lamp, p, Yaw(90f));
        Assert.True(east.X > 0.6f && west.X < -0.6f, $"-90 -> {east.X}, +90 -> {west.X}");
    }

    [Fact]
    public void A_scaled_lamp_has_its_bulb_scaled_with_it()
    {
        var a = LightPool.LightAnchor(Lamp, Vec3.Zero, Yaw(-90f), scale: 2f);
        Assert.Equal(12.2084f, a.Y, 3);
        Assert.Equal(1.3412f, a.X, 3);
    }

    [Fact]
    public void A_light_left_over_the_lamps_origin_is_still_that_lamps()
    {
        // What the editor placed before the bulb was measured - origin X/Z, at the preset's height. "Snap to bulbs"
        // has to recognise it to move it, so it must fall inside the match radius of the bulb.
        var origin = new Vec3(467.233f, 26.4469f + 6f, 243.528f);
        Assert.True(LightPool.LightBelongsTo(Lamp, new Vec3(467.233f, 26.4469f, 243.528f), Yaw(-89.125f), origin));
    }

    [Fact]
    public void A_light_raised_along_the_post_stays_that_posts()
    {
        var bulb = LightPool.LightAnchor(Lamp, MeasuredLamp, Yaw(-89.125f));
        Assert.True(LightPool.LightBelongsTo(Lamp, MeasuredLamp, Yaw(-89.125f), new Vec3(bulb.X, bulb.Y + 4f, bulb.Z)));
    }

    [Fact]
    public void Two_lamps_reaching_toward_each_other_never_claim_each_others_light()
    {
        // The closest pair on al_vietnas, measured at the BULB: 5.65 m apart at their bases, but their arms reach
        // toward each other, so their bulbs are only 4.30 m apart - which is what the match radius must respect.
        // (The old 2.5 m radius was over half of that.)
        var aPos = new Vec3(725.294f, 26.275f, 408.757f); var aRot = Yaw(-270f);
        var bPos = new Vec3(719.648f, 26.275f, 408.836f); var bRot = Yaw(-90f);
        var aBulb = LightPool.LightAnchor(Lamp, aPos, aRot);
        var bBulb = LightPool.LightAnchor(Lamp, bPos, bRot);
        float gap = MathF.Sqrt((aBulb.X - bBulb.X) * (aBulb.X - bBulb.X) + (aBulb.Z - bBulb.Z) * (aBulb.Z - bBulb.Z));

        Assert.True(gap > 4.2f && gap < 4.4f, $"the arms should reach toward each other: bulbs {gap:0.00} m apart");
        Assert.True(LightPool.MatchRadiusMetres < gap / 2f,
                    $"match radius {LightPool.MatchRadiusMetres} m lets bulbs {gap:0.00} m apart claim each other");
        Assert.False(LightPool.LightBelongsTo(Lamp, aPos, aRot, bBulb));
        Assert.False(LightPool.LightBelongsTo(Lamp, bPos, bRot, aBulb));
        Assert.True(LightPool.LightBelongsTo(Lamp, aPos, aRot, aBulb));
    }

    [Fact]
    public void The_bulb_is_matched_on_a_substring_so_mesh_suffixes_do_not_need_their_own_entry()
    {
        var bare = LightPool.OffsetFor("dc_streetlamp2");
        Assert.Equal(bare, LightPool.OffsetFor("dc_streetlamp2_m1"));
        Assert.Equal(bare, LightPool.OffsetFor("DC_StreetLamp2_M1"));      // the engine is case-insensitive
        Assert.True(LightPool.HasOffset("dc_streetlamp2_m1"));
    }

    [Fact]
    public void A_lamp_with_no_measured_bulb_reports_none()
    {
        // Callers fall back to the old behaviour - on the ground, at the preset's height - and "Snap to bulbs"
        // leaves such a lamp's light where it is.
        Assert.Equal(Vec3.Zero, LightPool.OffsetFor("o_lamp_M1"));
        Assert.False(LightPool.HasOffset("o_lamp_M1"));
        Assert.Equal(Vec3.Zero, LightPool.OffsetFor(""));
    }
}
