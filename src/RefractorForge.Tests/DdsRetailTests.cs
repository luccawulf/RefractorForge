using System.Collections.Concurrent;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Every DDS the two games ship, levels included, read the way a texture browser reads them: the header described,
/// the top level decoded whole. The retail 16-bit files are all normal maps, so decoded by their masks each texel is a
/// unit vector - read as B8G8R8 they were not.
/// </summary>
public class DdsRetailTests
{
    /// <summary>The retail mods of the clean installs (BF1942 with both expansions), every archive under them.</summary>
    private static IEnumerable<string> RetailArchives()
    {
        foreach (var dir in new[]
                 {
                     Installs.Bf1942CleanArchives,
                     Installs.Bf1942Clean + @"\Mods\XPack1\Archives",
                     Installs.Bf1942Clean + @"\Mods\XPack2\Archives",
                     Installs.BfvOriginalArchives,
                 })
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.rfa", SearchOption.AllDirectories)) yield return f;
    }

    [InstallFact(Installs.Bf1942CleanArchives, Installs.BfvOriginalArchives)]
    public void Every_retail_dds_describes_itself_and_decodes()
    {
        var failures = new ConcurrentBag<string>();
        var sixteenBit = new ConcurrentBag<(string Name, double Length)>();
        int total = 0;
        Parallel.ForEach(RetailArchives(), path =>
        {
            var a = new RefractorFlatArchive(path);
            foreach (var e in a.Entries)
            {
                if (!e.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) continue;
                Interlocked.Increment(ref total);
                var bytes = a.Read(e);
                var info = DdsTexture.Describe(bytes);
                if (info is null || !info.CanDecode) { failures.Add($"{path}|{e.Name}: {info?.Name ?? "not a DDS"}"); continue; }
                try
                {
                    var t = DdsTexture.Decode(bytes);
                    if (t.Width != info.Width || t.Height != info.Height) failures.Add($"{e.Name}: decoded {t.Width} x {t.Height}, header {info.Width} x {info.Height}");
                    if (info.BitCount == 16) sixteenBit.Add((e.Name, MeanNormalLength(t)));
                }
                catch (Exception ex) { failures.Add($"{path}|{e.Name}: {ex.GetType().Name} {ex.Message}"); }
            }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(20)));
        Assert.True(total > 3000, $"only {total} textures found");

        // normalmap01/02 (R5G6B5) in both games, 14 vehicle maps (A4R4G4B4) in BFV texture_001.
        Assert.True(sixteenBit.Count >= 2, $"{sixteenBit.Count} 16-bit textures");
        foreach (var (name, length) in sixteenBit)
            Assert.True(length is > 0.9 and < 1.1, $"{name}: mean normal length {length:0.000}");
    }

    /// <summary>The mean length of the texels read as normals (each channel 0..255 mapped to -1..1).</summary>
    private static double MeanNormalLength(Texture2D t)
    {
        double sum = 0;
        int n = t.Width * t.Height;
        for (int i = 0; i < n; i++)
        {
            double x = t.Rgba[i * 4] / 127.5 - 1, y = t.Rgba[i * 4 + 1] / 127.5 - 1, z = t.Rgba[i * 4 + 2] / 127.5 - 1;
            sum += Math.Sqrt(x * x + y * y + z * z);
        }
        return sum / n;
    }
}
