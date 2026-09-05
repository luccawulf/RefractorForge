using RefractorForge.Formats.Sound;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A sound heard by POSITION rather than by whether its object is on screen. Hanging the sound on the visible decal
/// (a SimpleObject with autoPlaySound) ties it to the object being DRAWN - in game it was audible only while the
/// screen was in view, and stopped at the draw distance, whatever the script said. Every retail level does a
/// placed ambient as an AreaObject instead: triggerRadius, an addLinePoint polygon, run from Sounds/Environment.con
/// and placed in StaticObjects.con (Hue does exactly this with river1 / river3 / island1). An AreaObject has no
/// geometry, so nothing can cull it.
/// </summary>
public class AreaSoundTests
{
    private static string TextOf(SoundObject.Built b, string ext) =>
        System.Text.Encoding.Latin1.GetString(b.Files.Single(f => f.RelPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).Bytes);

    private static byte[] Clip(int n = 4096) { var b = new byte[n]; b[0] = (byte)'R'; return b; }

    [Fact]
    public void The_emitter_is_an_AreaObject_with_a_trigger_radius_and_an_area()
    {
        var con = TextOf(SoundObject.BuildArea("screen_snd", Clip(), minDistance: 10f, maxDistance: 60f), ".con");
        Assert.Contains("ObjectTemplate.create AreaObject screen_snd", con);
        Assert.Contains("ObjectTemplate.saveInSeparateFile 1", con);
        Assert.Contains("ObjectTemplate.triggerRadius 60", con);
        Assert.Contains("ObjectTemplate.loadSoundScript screen_snd.ssc", con);
        // no geometry and no autoPlaySound: there is nothing to draw, so nothing to cull
        Assert.DoesNotContain("geometry", con);
        Assert.DoesNotContain("autoPlaySound", con);
        Assert.DoesNotContain("SimpleObject", con);
    }

    [Fact]
    public void The_area_is_a_closed_polygon_around_the_objects_own_origin()
    {
        var con = TextOf(SoundObject.BuildArea("s", Clip(), minDistance: 12f, maxDistance: 40f), ".con");
        var pts = con.Split("\r\n").Where(l => l.StartsWith("ObjectTemplate.addLinePoint")).ToList();
        Assert.Equal(5, pts.Count);                       // four corners, closed back to the first
        Assert.Equal(pts[0], pts[^1]);
        Assert.Contains("addLinePoint 12/12", pts[0]);    // relative to the placement, so it lands where it is put
        Assert.Contains(pts, l => l.Contains("-12/-12"));
    }

    [Fact]
    public void The_trigger_radius_stays_in_the_range_the_engine_documents()
    {
        // MDT: triggerRadius is an activation distance, 10..275.
        Assert.Contains("triggerRadius 10", TextOf(SoundObject.BuildArea("a", Clip(), minDistance: 1f, maxDistance: 4f), ".con"));
        Assert.Contains("triggerRadius 275", TextOf(SoundObject.BuildArea("b", Clip(), minDistance: 10f, maxDistance: 900f), ".con"));
    }

    [Fact]
    public void It_ships_its_script_both_wav_tiers_and_a_run_line_for_Environment_con()
    {
        var b = SoundObject.BuildArea("river", Clip(), wav44kBytes: Clip(8192));
        var paths = b.Files.Select(f => f.RelPath).ToArray();
        Assert.Contains("Sounds/river.con", paths);
        Assert.Contains("Sounds/river.ssc", paths);
        Assert.Contains("Sound/22khz/river.wav", paths);
        Assert.Contains("Sound/44kHz/river.wav", paths);
        Assert.Equal("run river.con", b.RunLine);
        Assert.Equal("river", b.Template);
    }

    [Fact]
    public void Its_script_is_the_same_shape_as_the_object_ambients()
    {
        var ssc = TextOf(SoundObject.BuildArea("river", Clip(), volume: 0.5f, minDistance: 8f, maxDistance: 45f), ".ssc");
        Assert.DoesNotContain("#templateLevel", ssc);
        Assert.StartsWith("newPatch", ssc);
        Assert.Contains("minDistance 8", ssc);
        Assert.Contains("volume 0.5", ssc);
        Assert.Contains("priority 11", ssc);
        Assert.Contains("\tparam 8\r\n\tparam 45\r\n\tparam 1\r\n\tparam -1", ssc);
    }

    [Fact]
    public void A_long_clip_is_still_streamed()
    {
        Assert.Contains("stream @ROOT/Sound/@RTD/movie_snd.wav",
                        TextOf(SoundObject.BuildArea("movie_snd", Clip(SoundObject.StreamAbove + 1)), ".ssc"));
    }

    [Fact]
    public void The_run_line_reaches_Environment_con_once_however_often_it_is_saved()
    {
        var b = SoundObject.BuildArea("river", Clip());
        var once = SoundObject.PatchEnvironmentCon(null, b.RunLine);
        var twice = SoundObject.PatchEnvironmentCon(once, b.RunLine);
        Assert.Equal(1, twice.Split('\n').Count(l => l.Trim().Equals("run river.con", StringComparison.OrdinalIgnoreCase)));
    }
}
