using RefractorForge.Formats.Sound;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The sound script's SHAPE, measured against the game's own 1226 scripts after a video decal's settings were ignored
/// in game. "#templateLevel HIGH" opens a per-quality block (359 of the 361 retail scripts using it carry HIGH,
/// MEDIUM and LOW blocks), so a script with only that block hands a player on another setting nothing; "rem" appears
/// in no retail script; long clips are streamed (retail never loads more than 0.8 MB, and streams its 8-10 MB radio
/// loops); the radios and speakers carry priority 11.
/// </summary>
public class SoundScriptShapeTests
{
    private static string Script(SoundObject.Built b) =>
        System.Text.Encoding.Latin1.GetString(b.Files.Single(f => f.RelPath.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase)).Bytes);

    private static byte[] Clip(int bytes) { var b = new byte[bytes]; b[0] = (byte)'R'; return b; }

    [Fact]
    public void No_quality_block_and_no_rem_so_the_settings_apply_at_every_sound_setting()
    {
        var s = Script(SoundObject.Build("screen", Clip(4096), volume: 0.5f, minDistance: 8f, maxDistance: 60f));
        Assert.DoesNotContain("#templateLevel", s);
        Assert.DoesNotContain("\nrem", s);
        Assert.StartsWith("newPatch", s);                 // exactly where the retail radios and speakers begin
        Assert.Contains("*** screen (RefractorForge) ***", s);
        Assert.Contains("minDistance 8", s);
        Assert.Contains("volume 0.5", s);
        Assert.Contains("\tparam 8\r\n\tparam 60\r\n\tparam 1\r\n\tparam -1", s);
    }

    [Fact]
    public void A_short_clip_is_loaded_and_a_long_one_is_streamed()
    {
        Assert.Contains("load @ROOT/Sound/@RTD/blip.wav", Script(SoundObject.Build("blip", Clip(200_000))));
        Assert.Contains("stream @ROOT/Sound/@RTD/movie.wav", Script(SoundObject.Build("movie", Clip(SoundObject.StreamAbove + 1))));
        Assert.DoesNotContain("load @ROOT", Script(SoundObject.Build("movie", Clip(SoundObject.StreamAbove + 1))));
    }

    [Fact]
    public void A_streamed_clip_keeps_its_distance_ramp()
    {
        // 26 retail scripts combine stream with a Distance->Volume ramp, so the silence-beyond distance still holds.
        var s = Script(SoundObject.Build("movie", Clip(SoundObject.StreamAbove + 1), minDistance: 10f, maxDistance: 45f));
        Assert.Contains("stream", s);
        Assert.Contains("controlSource Distance", s);
        Assert.Contains("\tparam 10\r\n\tparam 45", s);
    }

    [Fact]
    public void A_looping_ambient_takes_the_radios_priority_and_a_one_shot_stays_low()
    {
        Assert.Contains("priority 11", Script(SoundObject.Build("loop", Clip(4096), loop: true)));
        Assert.Contains("priority -7", Script(SoundObject.Build("once", Clip(4096), loop: false)));
    }
}
