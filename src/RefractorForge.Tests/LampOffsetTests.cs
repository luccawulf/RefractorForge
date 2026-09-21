using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The measured bulb offsets for lamp objects.
///
/// A lamp object's origin is at its base, so a light placed at the placement lands in the dirt at the foot of the
/// post; a street lamp also hangs its bulb out on an arm, so the bulb is not even above the origin. Each offset is
/// measured once from a light positioned by eye on a real lamp, and these tests pin it to those coordinates so a
/// later edit cannot quietly move every light in every level.
/// </summary>
public class LampOffsetTests
{
    // Measured in al_vietnas: the lamp, and the glow placed on it.
    private static readonly Vec3 LampPos = new(464.479f, 26.6187f, 343.854f);
    private static readonly Vec3 GlowPos = new(465.1482f, 32.67572f, 343.854f);

    [Fact]
    public void The_street_lamp_offset_reproduces_the_placement_it_was_measured_from()
    {
        var off = LightPool.OffsetFor("dc_streetlamp2_m1");
        var placed = new Vec3(LampPos.X + off.X, LampPos.Y + off.Y, LampPos.Z + off.Z);

        Assert.Equal(GlowPos.X, placed.X, 3);
        Assert.Equal(GlowPos.Y, placed.Y, 3);
        Assert.Equal(GlowPos.Z, placed.Z, 3);
    }

    [Fact]
    public void The_offset_is_matched_on_a_substring_so_mesh_suffixes_do_not_need_their_own_entry()
    {
        var bare = LightPool.OffsetFor("dc_streetlamp2");
        Assert.Equal(bare, LightPool.OffsetFor("dc_streetlamp2_m1"));
        Assert.Equal(bare, LightPool.OffsetFor("DC_StreetLamp2_M1"));      // the engine is case-insensitive
        Assert.True(LightPool.HasOffset("dc_streetlamp2_m1"));
    }

    [Fact]
    public void A_lamp_with_no_measured_offset_reports_none()
    {
        // Callers use this to fall back to the old behaviour - on the ground, at the preset's height - rather
        // than silently placing the light at the object's base.
        Assert.Equal(Vec3.Zero, LightPool.OffsetFor("o_lamp_M1"));
        Assert.False(LightPool.HasOffset("o_lamp_M1"));
        Assert.Equal(Vec3.Zero, LightPool.OffsetFor(""));
    }

    [Fact]
    public void A_lamps_own_light_is_recognised_as_its_own()
    {
        // "Same lamp type" and the "Under lamp objects" skip both ask this question, so a light the editor placed
        // must always be matched back to the lamp it came from - otherwise selecting a street silently finds
        // nothing, or the button gives every lamp a second light.
        Assert.True(LightPool.LightBelongsTo("dc_streetlamp2_m1", LampPos, GlowPos));

        // ...and it stays its own after being raised or lowered along the post, which the height controls do.
        Assert.True(LightPool.LightBelongsTo("dc_streetlamp2_m1", LampPos, new Vec3(GlowPos.X, GlowPos.Y + 4f, GlowPos.Z)));
    }

    [Fact]
    public void A_lamp_never_claims_its_neighbours_light()
    {
        // The two closest glows on al_vietnas are 5.71 m apart. The match radius has to stay well inside that or
        // one post would answer for the next one along, and editing "all of this type" would move the wrong lights.
        var a = new Vec3(490.074f, 14.07383f, 67.8605f);
        var b = new Vec3(495.786f, 14.07383f, 67.7664f);
        float gap = MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        Assert.True(gap > 5f && gap < 6f, $"the measured spacing moved: {gap}");
        Assert.True(LightPool.MatchRadiusMetres < gap / 2f,
                    $"match radius {LightPool.MatchRadiusMetres} m would let lamps {gap:0.00} m apart claim each other");
        Assert.False(LightPool.LightBelongsTo("dc_streetlamp2_m1", a, LightPool.LightAnchor("dc_streetlamp2_m1", b)));
    }

    [Fact]
    public void The_anchor_is_where_the_light_is_placed()
    {
        // One rule, used by the placement AND by the matcher - they cannot drift apart.
        var anchor = LightPool.LightAnchor("dc_streetlamp2_m1", LampPos);
        Assert.Equal(GlowPos.X, anchor.X, 3);
        Assert.Equal(GlowPos.Y, anchor.Y, 3);
        Assert.Equal(GlowPos.Z, anchor.Z, 3);
    }

    [Fact]
    public void The_offset_lifts_the_light_to_the_lamp_head()
    {
        // Sanity on the magnitude rather than the exact number: a street lamp's bulb is several metres up and
        // out to one side, never at the base.
        var off = LightPool.OffsetFor("dc_streetlamp2_m1");
        Assert.True(off.Y > 4f && off.Y < 10f, $"bulb height {off.Y} is not street-lamp sized");
        Assert.True(MathF.Abs(off.X) > 0.1f, "the bulb hangs out on an arm, so the offset is not purely vertical");
    }
}
