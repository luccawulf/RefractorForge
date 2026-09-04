using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The conversion itself needs FFmpeg and RAD's compressor, so it is exercised by hand
/// (<c>scratchpad/rfx bik &lt;src&gt; &lt;out.bik&gt;</c>, verified on a 60 s 720p mp4 -> a real BIKi with its audio).
/// What is pinned here is the part that went wrong in the field: refusing cleanly when the tools or the source are
/// not there, rather than starting work it cannot finish.
/// </summary>
public class BinkEncoderTests
{
    private static string Missing => Path.Combine(Path.GetTempPath(), "rf_not_here_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Without_the_tools_it_says_so_and_writes_nothing()
    {
        var dst = Missing + ".bik";
        var r = BinkEncoder.Convert(Missing, Missing, Missing, dst, null, out var error);
        Assert.Equal(BinkEncoder.Result.NoTools, r);
        Assert.Contains("RAD", error);
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void A_source_that_is_not_there_is_refused_before_anything_starts()
    {
        // Real tool paths where they exist, so the check that fires is the source one.
        var ff = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        var rad = BinkEncoder.FindRadVideo();
        if (!File.Exists(ff) || rad is null) return;      // nothing to prove on a machine without them
        var dst = Missing + ".bik";
        var r = BinkEncoder.Convert(ff, rad, Missing + ".mp4", dst, null, out var error);
        Assert.Equal(BinkEncoder.Result.SourceUnreadable, r);
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void The_installed_tools_are_found_where_RAD_puts_them()
    {
        var rad = BinkEncoder.FindRadVideo();
        if (rad is null) return;                          // not installed here; the editor tells the user so
        Assert.EndsWith(".exe", rad);
        Assert.Contains("RADVideo", rad);
        Assert.True(File.Exists(rad));
    }
}
