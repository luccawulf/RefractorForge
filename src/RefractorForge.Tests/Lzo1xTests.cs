using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

public class Lzo1xTests
{
    // ── Compress / Decompress ─────────────────────────────────────────────────

    [Theory]
    [InlineData("zeros",    40_000, 0)]
    [InlineData("runs",     40_000, 1)]
    [InlineData("periodic", 40_000, 2)]
    [InlineData("text",     20_000, 3)]
    public void Compress_RoundTrips_PatternData(string _, int length, int pattern)
    {
        var data = MakePattern(length, pattern);
        var compressed = Lzo1x.Compress(data);
        var back = Lzo1x.Decompress(compressed, data.Length);
        Assert.Equal(data, back);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(300)]
    [InlineData(32768)]
    public void Compress_RoundTrips_RandomData(int length)
    {
        var data = RandomBytes(length, seed: length);
        var compressed = Lzo1x.Compress(data);
        var back = Lzo1x.Decompress(compressed, data.Length);
        Assert.Equal(data, back);
    }

    [Fact]
    public void Compress_ShrinksHighlyCompressibleData()
    {
        long totalIn = 0, totalOut = 0;
        foreach (var (data, _) in CompressiblePatterns())
        {
            totalIn  += data.Length;
            totalOut += Lzo1x.Compress(data).Length;
        }
        Assert.True(totalOut < totalIn / 2, $"Expected >50% compression, got {totalOut}/{totalIn}");
    }

    [Fact]
    public void Compress_EmptyInput_ProducesDecodableStream()
    {
        var compressed = Lzo1x.Compress(Array.Empty<byte>());
        var back = Lzo1x.Decompress(compressed, 0);
        Assert.Empty(back);
    }

    // ── EncodeLiteralBlock ────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(272)]
    [InlineData(273)]
    [InlineData(274)]
    [InlineData(1000)]
    [InlineData(32767)]
    [InlineData(32768)]
    public void EncodeLiteralBlock_RoundTrips(int length)
    {
        var src = RandomBytes(length, seed: length);
        var encoded = Lzo1x.EncodeLiteralBlock(src);
        var decoded = Lzo1x.Decompress(encoded, length);
        Assert.Equal(src, decoded);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] MakePattern(int length, int pattern)
    {
        var data = new byte[length];
        switch (pattern)
        {
            case 0: break;  // zeros
            case 1: for (int i = 0; i < length; i++) data[i] = (byte)(i / 500); break;
            case 2: for (int i = 0; i < length; i++) data[i] = (byte)("ABCDEFGH"[i % 8]); break;
            case 3:
                var phrase = "the quick brown fox jumps over the lazy dog. "u8.ToArray();
                for (int i = 0; i < length; i++) data[i] = phrase[i % phrase.Length];
                break;
        }
        return data;
    }

    private static IEnumerable<(byte[] Data, string Label)> CompressiblePatterns()
    {
        yield return (MakePattern(40_000, 0), "zeros");
        yield return (MakePattern(40_000, 1), "runs");
        yield return (MakePattern(40_000, 2), "periodic");
        yield return (MakePattern(20_000, 3), "text");
    }

    internal static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
