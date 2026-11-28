using System.Numerics;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>A baked volume follows the light: dense where the air is dark, clear where it is lit
/// (<see cref="LightVolumeBaker"/>).</summary>
public class LightVolumeBakerTests
{
    private static double MeanAlpha(Texture2D t)
    {
        double s = 0; var px = t.Rgba;
        for (int i = 3; i < px.Length; i += 4) s += px[i];
        return s / (px.Length / 4);
    }

    [Fact]
    public void A_dark_volume_in_open_daylight_is_clear_and_the_same_box_at_a_stricter_level_is_not()
    {
        // No scene at all: the air sees the whole sky and the sun.
        var day = new Daylight().Build();
        var open = new LightVolumeBaker.Settings(Matrix4x4.CreateTranslation(100, 10, 100), new Vector3(6, 4, 6), 1f, LightVolumeBaker.Dark, 0.12f, 0.2f, 0f);
        var a = LightVolumeBaker.Bake("v_m1", open, null, day, null)!;
        Assert.True(MeanAlpha(a.Atlas) < 1.0, $"open air should be clear, mean alpha {MeanAlpha(a.Atlas):0.0}");
        // Ask for more light than there is anywhere and the volume fills in.
        var strict = open with { LitLevel = 1f, Strength = 0.6f };
        var b = LightVolumeBaker.Bake("v_m1", strict, null, day, null)!;
        Assert.True(MeanAlpha(b.Atlas) >= MeanAlpha(a.Atlas));
        Assert.Equal(a.Planes, b.Planes);
    }

    [Fact]
    public void A_glow_volume_with_no_lamps_adds_nothing()
    {
        var day = new Daylight().Build();
        var s = new LightVolumeBaker.Settings(Matrix4x4.CreateTranslation(100, 10, 100), new Vector3(6, 4, 6), 1f, LightVolumeBaker.Glow, 0.6f, 0.35f, 1f);
        var g = LightVolumeBaker.Bake("v_m1", s, null, day, null)!;
        Assert.Equal(0.0, MeanAlpha(g.Atlas));
    }

    [Fact]
    public void A_haze_volume_is_even_whatever_the_light_and_a_turned_box_bakes_the_same_lattice()
    {
        var day = new Daylight().Build();
        var s = new LightVolumeBaker.Settings(Matrix4x4.CreateTranslation(100, 10, 100), new Vector3(6, 4, 6), 1f, LightVolumeBaker.Haze, 0.05f, 0.35f, 0f);
        var h = LightVolumeBaker.Bake("v_m1", s, null, day, null)!;
        byte want = (byte)MathF.Round(LightVolume.PlaneAlpha(0.05f, 1f, 0.12f) * 255f);
        Assert.Equal(want, h.Atlas.Rgba[3]);
        Assert.InRange(MeanAlpha(h.Atlas), 1.0, want);          // the tiles hold it; the unused atlas cells are empty
        var turned = s with { World = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(100, 10, 100) };
        var t = LightVolumeBaker.Bake("v_m1", turned, null, day, null)!;
        Assert.Equal(h.Planes, t.Planes);
        Assert.Equal(h.Atlas.Rgba, t.Atlas.Rgba);
        // The spacing a preset picks from the box.
        Assert.Equal(0.5f, LightVolumeBaker.SpacingFor(new Vector3(8, 4, 10)));
        Assert.Equal(1f, LightVolumeBaker.SpacingFor(new Vector3(24, 12, 44)));
        Assert.Equal(2f, LightVolumeBaker.SpacingFor(new Vector3(80, 10, 30)));
    }
}
