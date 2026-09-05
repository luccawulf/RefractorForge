using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Sound;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// An imported sound has to arrive as the things a retail ambient emitter is made of. The reference is al_vietnas's
/// own <c>rivermid</c>: a SimpleObject with a loadSoundScript line, a .ssc that loads
/// <c>@ROOT/Sound/@RTD/&lt;file&gt;.wav</c> with minDistance/volume and a Distance-&gt;Volume ramp, and the wav under
/// the sample-rate folders <c>@RTD</c> stands for.
/// </summary>
public class SoundObjectTests
{
    private static readonly byte[] Wav = Encoding.ASCII.GetBytes("RIFF....WAVEfmt ").Concat(new byte[64]).ToArray();

    private static string TextOf(SoundObject.Built b, string suffix)
        => Encoding.Latin1.GetString(b.Files.First(f => f.RelPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Bytes);

    [Fact]
    public void It_writes_the_wav_the_script_and_the_object()
    {
        var b = SoundObject.Build("jungle birds", Wav, volume: 0.5f, minDistance: 30f, maxDistance: 140f);
        Assert.Equal("jungle_birds", b.Template);
        Assert.Equal("run jungle_birds.con", b.RunLine);

        var paths = b.Files.Select(f => f.RelPath).ToArray();
        Assert.Contains("Sound/22khz/jungle_birds.wav", paths);
        Assert.Contains("Sound/44kHz/jungle_birds.wav", paths);   // @RTD is the quality folder: ship both
        Assert.Contains("Sounds/jungle_birds.ssc", paths);
        Assert.Contains("Sounds/jungle_birds.con", paths);
        Assert.All(b.Files.Where(f => f.RelPath.EndsWith(".wav")), f => Assert.Equal(Wav, f.Bytes));
    }

    [Fact]
    public void The_script_carries_the_near_and_far_volume_the_way_the_retail_ones_do()
    {
        var ssc = TextOf(SoundObject.Build("river", Wav, volume: 0.4f, minDistance: 50f, maxDistance: 150f), ".ssc");
        Assert.DoesNotContain("#templateLevel", ssc);   // a per-quality block would leave other settings with nothing
        Assert.Contains("newPatch", ssc);
        Assert.Contains("load @ROOT/Sound/@RTD/river.wav", ssc);
        Assert.Contains("loop", ssc);
        Assert.Contains("minDistance 50", ssc);
        Assert.Contains("volume 0.4", ssc);
        // The falloff: full at minDistance, silent at maxDistance.
        Assert.Contains("controlDestination Volume", ssc);
        Assert.Contains("controlSource Distance", ssc);
        Assert.Contains("envelope Ramp", ssc);
        Assert.Contains("\tparam 50\r\n\tparam 150\r\n\tparam 1\r\n\tparam -1", ssc);
    }

    [Fact]
    public void The_object_is_a_placeable_SimpleObject_that_loads_the_script()
    {
        var con = TextOf(SoundObject.Build("radio", Wav, minDistance: 10f, maxDistance: 80f), ".con");
        Assert.Contains("ObjectTemplate.create SimpleObject radio", con);
        Assert.Contains("ObjectTemplate.loadSoundScript radio.ssc", con);
        // No triggerRadius: it is an AreaObject property and the engine rejects it on a SimpleObject.
        Assert.DoesNotContain("triggerRadius", con);
        Assert.Contains("ObjectTemplate.saveInSeparateFile 1", con);
    }

    [Fact]
    public void A_non_looping_sound_leaves_the_loop_flag_out()
    {
        var ssc = TextOf(SoundObject.Build("shout", Wav, loop: false), ".ssc");
        Assert.DoesNotContain("\r\nloop\r\n", ssc);
    }

    [Fact]
    public void Silence_can_never_come_before_full_volume()
    {
        // A far distance under the near one would make a backwards ramp; it is pushed past it instead.
        var ssc = TextOf(SoundObject.Build("odd", Wav, minDistance: 60f, maxDistance: 10f), ".ssc");
        Assert.Contains("\tparam 60\r\n\tparam 61\r\n", ssc);
    }

    [Theory]
    [InlineData("My Sound!", "My_Sound")]
    [InlineData("  spaced  out  ", "spaced__out")]
    [InlineData("123", "s123")]
    [InlineData("", "sound")]
    [InlineData("!!!", "sound")]
    public void Names_come_out_as_something_a_con_parser_can_read(string given, string expected)
        => Assert.Equal(expected, SoundObject.Sanitize(given));

    [Fact]
    public void The_run_line_is_added_once_however_often_the_level_is_saved()
    {
        var first = SoundObject.PatchEnvironmentCon(null, "run birds.con");
        Assert.Contains("Sound.3d.occludeByDistanceFactor", first);      // a level with no sound layer gets the preamble
        Assert.Contains("run birds.con", first);
        var again = SoundObject.PatchEnvironmentCon(first, "run birds.con");
        Assert.Equal(first, again);
        var two = SoundObject.PatchEnvironmentCon(again, "run river.con");
        Assert.Contains("run birds.con", two);
        Assert.Contains("run river.con", two);
    }

    [Fact]
    public void A_video_decal_can_carry_its_own_sound_script()
    {
        var b = DecalObject.Build("screen", "screen", 4f, 3f, "decal_screen", null,
                                  textureRef: "Mods/echo/Movies/intro.bik", soundScript: "screen.ssc", soundRadius: 60f);
        var obj = Encoding.Latin1.GetString(b.Files.First(f => f.RelPath.EndsWith("Objects.con")).Bytes);
        Assert.Contains("ObjectTemplate.create SimpleObject screen", obj);
        Assert.Contains("ObjectTemplate.loadSoundScript screen.ssc", obj);
        Assert.DoesNotContain("triggerRadius", obj);

        // A decal with no sound is unchanged - no stray lines.
        var quiet = DecalObject.Build("poster", "poster", 2f, 1f, "decal_poster", new byte[128]);
        var qobj = Encoding.Latin1.GetString(quiet.Files.First(f => f.RelPath.EndsWith("Objects.con")).Bytes);
        Assert.DoesNotContain("loadSoundScript", qobj);
    }

    [Fact]
    public void The_script_for_an_object_local_sound_goes_beside_that_object()
    {
        // The engine resolves loadSoundScript relative to the .con that declared the template. A video decal's
        // template lives in Objects/<name>/Objects.con, so its script must be Objects/<name>/<name>.ssc - putting it
        // in Sounds/ made the game report "File not found: .../objects/video5/video5.ssc" and the screen played mute.
        Assert.Equal("Objects/video5/video5.ssc", SoundObject.ScriptPathFor("video5", "video5"));
    }
}
