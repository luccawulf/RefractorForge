using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A save must not be able to leave a level half on the disk. A power cut cannot be simulated here, so these pin the
/// shape that makes it safe - the repack still lands whole under the level's name with no temp file left behind,
/// and a backup copy is a real, complete copy - while <see cref="DurableFile"/> carries the flush that the
/// 2026-09-11 crash showed was missing (the rename survived, 71 MB of data and the table of contents did not).
/// </summary>
public class DurableSaveTests
{
    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), "rf_durable_" + Guid.NewGuid().ToString("N") + "_" + name);

    [Fact]
    public void A_repack_replaces_the_level_whole_and_leaves_no_temp_file()
    {
        string src = Temp("src.rfa"), dst = Temp("dst.rfa");
        try
        {
            RefractorFlatArchive.WriteFile(src, new List<(string, byte[])>
            {
                ("levels/x/Init.con", "run Init/Terrain\r\n"u8.ToArray()),
                ("levels/x/StandardMesh/levelWater.rs", "reflectivity 0.4;\r\n"u8.ToArray()),
            }, compress: false, xPackId: XPackId.Default);
            File.Copy(src, dst);
            var arch = new RefractorFlatArchive(src);
            RefractorFlatArchive.RepackToFile(dst, arch, new Dictionary<string, byte[]>
            {
                ["levels/x/StandardMesh/levelWater.rs"] = "reflectivity 0.25;\r\n"u8.ToArray(),
            });
            Assert.False(File.Exists(dst + ".rfatmp"));
            Assert.Null(RefractorFlatArchive.Validate(dst));
            var back = new RefractorFlatArchive(dst);
            Assert.Equal(2, back.Entries.Count);
            Assert.Equal("reflectivity 0.25;\r\n"u8.ToArray(), back.Read(back.Entries.Single(e => e.Name.EndsWith("levelWater.rs"))));
        }
        finally { File.Delete(src); File.Delete(dst); }
    }

    [Fact]
    public void A_durable_copy_is_a_complete_copy()
    {
        string src = Temp("a.bin"), dst = Temp("b.bin");
        try
        {
            var bytes = new byte[3 * 1024 * 1024 + 17];
            new Random(7).NextBytes(bytes);
            File.WriteAllBytes(src, bytes);
            DurableFile.Copy(src, dst);
            Assert.Equal(bytes, File.ReadAllBytes(dst));
            Assert.Throws<IOException>(() => DurableFile.Copy(src, dst));          // never clobbers unless asked
            DurableFile.Copy(src, dst, overwrite: true);
        }
        finally { File.Delete(src); File.Delete(dst); }
    }
}
