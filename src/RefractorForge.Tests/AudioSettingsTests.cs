using RefractorForge.Formats.Sound;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The audio a video conversion produces - the .bik's own track and the sound object's wav - at a chosen sample rate
/// and channel count. The game's own sound.rfa settles what it accepts: its Sound/44kHz tier holds 854 files at
/// 44100 Hz (750 mono, 104 stereo, 16-bit) beside 22050 and even 11025 Hz ones, and its Sound/22khz tier is all
/// 22050 Hz 8-bit. So 44.1 kHz and stereo are both native, and the rate need not match the folder it sits in.
/// </summary>
public class AudioSettingsTests
{
    [Fact]
    public void The_bink_intermediate_defaults_to_the_games_low_tier()
    {
        var a = BinkEncoder.FfmpegArgs("in.mp4", "out.avi");
        Assert.Contains("-ar 22050 -ac 1", a);
        Assert.Contains("-c:v mjpeg", a);
        Assert.Contains("scale='min(iw,512)':-2", a);
    }

    [Fact]
    public void The_bink_intermediate_takes_the_rate_and_channels_asked_for()
    {
        Assert.Contains("-ar 44100 -ac 2", BinkEncoder.FfmpegArgs("in.mp4", "out.avi", 512, 44100, 2));
        Assert.Contains("-ar 44100 -ac 1", BinkEncoder.FfmpegArgs("in.mp4", "out.avi", 512, 44100, 1));
        // a channel count outside what PCM in a .bik can carry is clamped, never passed through
        Assert.Contains("-ac 2", BinkEncoder.FfmpegArgs("in.mp4", "out.avi", 512, 22050, 6));
        Assert.Contains("-ac 1", BinkEncoder.FfmpegArgs("in.mp4", "out.avi", 512, 22050, 0));
    }

    [Fact]
    public void No_width_cap_means_no_scale_filter()
    {
        Assert.DoesNotContain("-vf", BinkEncoder.FfmpegArgs("in.mp4", "out.avi", 0));
    }

    private static byte[] Wav(byte tag) => new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', tag, tag, tag, tag };

    [Fact]
    public void A_44_kHz_file_goes_to_the_high_tier_and_the_22_kHz_one_to_the_low()
    {
        var b = SoundObject.Build("screen", Wav(22), wav44kBytes: Wav(44));
        var low = b.Files.Single(f => f.RelPath.Equals("Sound/22khz/screen.wav", StringComparison.OrdinalIgnoreCase));
        var high = b.Files.Single(f => f.RelPath.Equals("Sound/44kHz/screen.wav", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(22, low.Bytes[4]);
        Assert.Equal(44, high.Bytes[4]);
    }

    [Fact]
    public void Without_a_44_kHz_file_both_tiers_get_the_same_one()
    {
        var b = SoundObject.Build("screen", Wav(22));
        var low = b.Files.Single(f => f.RelPath.Equals("Sound/22khz/screen.wav", StringComparison.OrdinalIgnoreCase));
        var high = b.Files.Single(f => f.RelPath.Equals("Sound/44kHz/screen.wav", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(low.Bytes, high.Bytes);
    }

    [Fact]
    public void Stereo_is_declared_in_the_script_when_chosen()
    {
        var mono = SoundObject.Build("s", Wav(1));
        var stereo = SoundObject.Build("s", Wav(1), stereo: true);
        string Script(SoundObject.Built b) => System.Text.Encoding.Latin1.GetString(b.Files.Single(f => f.RelPath.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase)).Bytes);
        Assert.DoesNotContain("stereo", Script(mono));
        Assert.Contains("stereo", Script(stereo));
    }
}
