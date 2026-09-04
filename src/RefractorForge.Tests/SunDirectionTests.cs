using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Aiming the sun in the editor is an edit to the level, not a preview: the lighting bakes already use that
/// direction, so unless <c>sky.sunLightDirectionVec</c> goes with them the game lights the map one way and the baked
/// shadows another. The line is rewritten in place, leaving the rest of SkyAndSun.con (skybox, rotation, clouds) alone.
/// </summary>
public class SunDirectionTests
{
    // Operation_Irving's, near enough: a sun line among the things a real SkyAndSun.con carries.
    private static readonly string[] SkyAndSun =
    {
        "rem *** sky and sun ***",
        "sky.active 1",
        "sky.sunLightDirectionVec 0.64/0.34/-0.68",
        "Sky.setRotAngle -45",
        "GeometryTemplate.create StandardMesh Sky_OI_m1",
        "GeometryTemplate.file Sky_OI_m1",
    };

    [Fact]
    public void An_untouched_sun_leaves_the_file_exactly_as_it_was()
    {
        var e = EnvironmentSettings.Parse(null, null, null);
        e.SunDirection = new Vec3(0.1f, 0.9f, 0.1f);      // parsed/edited in memory, but not claimed
        Assert.Equal(SkyAndSun, e.PatchSkyAndSunConLines(SkyAndSun));
    }

    [Fact]
    public void An_aimed_sun_replaces_the_direction_and_nothing_else()
    {
        var e = EnvironmentSettings.Parse(null, null, null);
        e.SunDirection = new Vec3(-0.5f, 0.8f, -0.35f);
        e.WriteSun = true;
        var outLines = e.PatchSkyAndSunConLines(SkyAndSun);

        Assert.Contains("sky.sunLightDirectionVec -0.5/0.8/-0.35", outLines);
        Assert.DoesNotContain("sky.sunLightDirectionVec 0.64/0.34/-0.68", outLines);
        Assert.Equal(1, outLines.Count(l => l.TrimStart().StartsWith("sky.sunLightDirectionVec")));
        // everything else the author had is still there, in order
        Assert.Contains("sky.active 1", outLines);
        Assert.Contains("Sky.setRotAngle -45", outLines);
        Assert.Contains("GeometryTemplate.file Sky_OI_m1", outLines);
        Assert.Equal(SkyAndSun.Length, outLines.Count);
    }

    [Fact]
    public void The_line_keeps_its_indentation()
    {
        string[] indented = { "\tsky.sunLightDirectionVec 0.1/0.2/0.3" };
        var e = EnvironmentSettings.Parse(null, null, null);
        e.SunDirection = new Vec3(1f, 0f, 0f); e.WriteSun = true;
        Assert.Equal("\tsky.sunLightDirectionVec 1/0/0", e.PatchSkyAndSunConLines(indented)[0]);
    }

    [Fact]
    public void A_level_that_never_declared_a_sun_gets_the_line_added_once()
    {
        string[] bare = { "sky.active 1" };
        var e = EnvironmentSettings.Parse(null, null, null);
        e.SunDirection = new Vec3(0f, 1f, 0f); e.WriteSun = true;
        var once = e.PatchSkyAndSunConLines(bare);
        Assert.Contains("sky.sunLightDirectionVec 0/1/0", once);
        // and saving again does not stack a second one
        var twice = e.PatchSkyAndSunConLines(once);
        Assert.Equal(1, twice.Count(l => l.TrimStart().StartsWith("sky.sunLightDirectionVec")));
    }

    [Fact]
    public void What_is_written_is_what_the_editor_reads_back()
    {
        // The editor stores the sun as azimuth + elevation; the round trip through the file has to land where it
        // started, or a saved sun would drift every time the level is opened and saved.
        foreach (var (azDeg, elDeg) in new[] { (0f, 45f), (90f, 20f), (-135f, 70f), (179f, 5f) })
        {
            float az = azDeg * MathF.PI / 180f, el = elDeg * MathF.PI / 180f;
            var dir = new Vec3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));

            var e = EnvironmentSettings.Parse(null, null, null);
            e.SunDirection = dir; e.WriteSun = true;
            var text = e.PatchSkyAndSunConLines(SkyAndSun);

            var back = EnvironmentSettings.Parse(text, null, null);
            float az2 = MathF.Atan2(back.SunDirection.X, back.SunDirection.Z) * 180f / MathF.PI;
            float el2 = MathF.Asin(Math.Clamp(back.SunDirection.Y, -1f, 1f)) * 180f / MathF.PI;
            Assert.Equal(azDeg, az2, 1);
            Assert.Equal(elDeg, el2, 1);
        }
    }
}
