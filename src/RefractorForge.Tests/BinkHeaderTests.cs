using System.Buffers.Binary;
using RefractorForge.Formats.Video;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The Bink header: the games' own menu backgrounds read as retail made them, and what the games' player
/// (binkw32.dll 1.5J: it compares the signature against BIKf, BIKg, BIKh and BIKi) opens and refuses.
/// </summary>
public class BinkHeaderTests
{
    private static byte[] Header(string sig, int frames, int w, int h, int num, int den, uint flags = 0, int tracks = 0)
    {
        var b = new byte[44];
        for (int i = 0; i < 4; i++) b[i] = (byte)sig[i];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), frames);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), 500);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), frames);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), h);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), num);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), den);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), flags);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), tracks);
        return b;
    }

    [InstallFact(Installs.Bf1942Clean, Installs.BfvOriginal)]
    public void The_games_menu_backgrounds_read_as_retail_made_them()
    {
        var bf42 = BinkHeader.Read(Path.Combine(Installs.Bf1942Clean, @"Mods\bf1942\Movies\background.bik"));
        if (bf42 is not null)
        {
            Assert.Equal("BIKi", bf42.Signature);
            Assert.Equal((320, 180, 25.0, 1892, 0), (bf42.Width, bf42.Height, bf42.Fps, bf42.Frames, bf42.AudioTracks));
            Assert.True(bf42.GamePlays);
            Assert.Equal(75.68, bf42.Seconds, 2);
        }
        var bfv = BinkHeader.Read(Path.Combine(Installs.BfvOriginal, @"Mods\BfVietnam\Movies\background.bik"));
        if (bfv is not null)
        {
            Assert.Equal("BIKi", bfv.Signature);
            Assert.Equal((320, 240, 30.0, 1836), (bfv.Width, bfv.Height, bfv.Fps, bfv.Frames));
            Assert.StartsWith("BIKi 320 x 240, 30 fps, 1836 frames (61.2 s), no sound", bfv.Describe());
        }
        Assert.True(bf42 is not null || bfv is not null);
    }

    [Fact]
    public void The_games_player_opens_bink_1_revisions_f_to_i_and_nothing_else()
    {
        foreach (var sig in new[] { "BIKf", "BIKg", "BIKh", "BIKi" })
            Assert.True(BinkHeader.Read(Header(sig, 10, 64, 64, 30, 1), 1000)!.GamePlays, sig);
        var later = BinkHeader.Read(Header("BIKk", 10, 64, 64, 30, 1), 1000)!;
        Assert.True(later.IsBink1);
        Assert.False(later.GamePlays);
        var bink2 = BinkHeader.Read(Header("KB2j", 10, 1920, 1080, 60000, 1001), 1000)!;
        Assert.True(bink2.IsBink2);
        Assert.False(bink2.GamePlays);
        Assert.Equal(59.94, bink2.Fps, 2);

        var gray = BinkHeader.Read(Header("BIKi", 744, 320, 240, 15, 1, BinkHeader.GrayscaleFlag), 3_000_000)!;
        Assert.True(gray.Grayscale);
        Assert.Contains("grayscale", gray.Describe());

        Assert.Null(BinkHeader.Read(new byte[43], 43));                      // too short
        Assert.Null(BinkHeader.Read(Header("RIFF", 1, 1, 1, 1, 1), 44));     // not Bink
    }
}
