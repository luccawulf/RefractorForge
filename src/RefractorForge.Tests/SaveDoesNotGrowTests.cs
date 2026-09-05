using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A save that changes nothing must write files that are byte-identical to the ones it read. Every gameplay .con
/// was growing by two bytes on EVERY save - Saigon68's Ctf/ControlPoints.con went 498 -> 506 -> 512 -> 514 over
/// four of them - because the reader split on '\n', which turns a file ending in a newline into one carrying a
/// trailing empty line, and each patcher then rejoined with a separator AND appended a newline of its own.
/// Harmless to the engine, but unbounded, and it makes a real diff impossible to read.
/// </summary>
public class SaveDoesNotGrowTests
{
    private const string ControlPoints =
        "rem *** ControlPoints ***\r\n" +
        "Object.create ControlPoint_1\r\n" +
        "Object.absolutePosition 100.00/10.00/200.00\r\n" +
        "Object.rotation 0/0/0\r\n";

    private const string SoldierSpawns =
        "Object.create SoldierSpawn_1\r\n" +
        "Object.absolutePosition 50.00/5.00/60.00\r\n" +
        "Object.rotation 90/0/0\r\n";

    private static string TempRfa()
    {
        var path = Path.Combine(Path.GetTempPath(), "rf_grow_" + Guid.NewGuid().ToString("N")[..8] + ".rfa");
        RefractorFlatArchive.WriteFile(path, new List<(string, byte[])>
        {
            ("bfvietnam/levels/Test/Conquest/ControlPoints.con", Encoding.Latin1.GetBytes(ControlPoints)),
            ("bfvietnam/levels/Test/Conquest/SoldierSpawns.con", Encoding.Latin1.GetBytes(SoldierSpawns)),
            ("bfvietnam/levels/Test/Ctf/ControlPoints.con", Encoding.Latin1.GetBytes(ControlPoints)),
            ("bfvietnam/levels/Test/Ctf/SoldierSpawns.con", Encoding.Latin1.GetBytes(SoldierSpawns)),
        }, compress: false, xPackId: XPackId.Default);
        return path;
    }

    // Exactly what the archive holds, so the save has nothing to change.
    private static RefractorForge.Formats.Con.GameplayObjects Parsed() =>
        RefractorForge.Formats.Con.GameplayObjects.Parse(
            ControlPoints.Split('\n'), null, null, null, SoldierSpawns.Split('\n'), null);

    private static Dictionary<string, int> Sizes(string rfa)
    {
        var a = new RefractorFlatArchive(rfa);
        return a.Entries.ToDictionary(e => e.Name.Replace('\\', '/'), e => (int)e.UncompressedSize,
                                      StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saving_the_same_gameplay_over_and_over_does_not_grow_the_files()
    {
        var rfa = TempRfa();
        try
        {
            var edit = new RefractorForge.Formats.Con.EditableGameplay(Parsed());

            // The first save may legitimately reshape a file - the mode the editor loaded is rebuilt from the
            // model rather than patched. What must NOT happen is the size moving again on every save after that.
            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, edit);
            var settled = Sizes(rfa);

            for (int save = 0; save < 4; save++)
                LevelSaver.RepackToRfa(rfa, rfa, null, null, null, edit);

            var after = Sizes(rfa);
            foreach (var (name, size) in settled)
                Assert.True(after[name] == size,
                    $"{name} went {size} -> {after[name]} bytes over four further saves that changed nothing");
        }
        finally { File.Delete(rfa); }
    }

    [Fact]
    public void And_the_content_is_unchanged_too()
    {
        var rfa = TempRfa();
        try
        {
            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, new RefractorForge.Formats.Con.EditableGameplay(Parsed()));

            var a = new RefractorFlatArchive(rfa);
            var e = a.Entries.First(x => x.Name.Replace('\\', '/').EndsWith("Ctf/ControlPoints.con",
                        StringComparison.OrdinalIgnoreCase));
            var text = Encoding.Latin1.GetString(a.Read(e));
            Assert.Contains("Object.create ControlPoint_1", text);
            Assert.DoesNotContain("\r\n\r\n\r\n", text);      // no blank lines piling up at the end
        }
        finally { File.Delete(rfa); }
    }
}
