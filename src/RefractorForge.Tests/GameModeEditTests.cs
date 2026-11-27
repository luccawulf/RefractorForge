using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The editor can edit any game mode's gameplay, not only Conquest's: the chosen mode is loaded from its own files and
/// a save writes THAT mode in full, while the other modes are only patched (moves of the objects they share), exactly
/// as Conquest edits always were. A CTF-only flag added in CTF must not leak into Conquest, and Conquest's own set
/// must not be overwritten by CTF's.
/// </summary>
public class GameModeEditTests : IDisposable
{
    private const string NL = "\r\n";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_modeedit_" + Guid.NewGuid().ToString("N"));
    public GameModeEditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string Cps(params (string Name, string Pos)[] cps)
        => string.Join(NL, cps.SelectMany(c => new[] { "Object.create " + c.Name, "Object.absolutePosition " + c.Pos })) + NL;

    private static readonly string Conquest = Cps(("SHARED", "100/1/100"), ("CQ_ONLY", "5/5/5"));
    private static readonly string Ctf = Cps(("SHARED", "100/1/100"), ("CTF_ONLY", "9/9/9"));

    private static void EditCtf(EditableGameplay ed)
    {
        // Move the shared flag and add a new CTF flag.
        int shared = ed.ControlPoints.ToList().FindIndex(c => c.Name == "SHARED");
        ed.SetPos(GpKind.ControlPoint, shared, new Vec3(321f, 1f, 100f));
        ed.ControlPoints.Add(new ControlPointDef("CTF_NEW", new Vec3(50f, 2f, 60f), 30f, 0));
    }

    [Fact]
    public void A_folder_save_writes_the_edited_mode_and_only_patches_the_others()
    {
        foreach (var (mode, text) in new[] { ("Conquest", Conquest), ("Ctf", Ctf) })
        {
            Directory.CreateDirectory(Path.Combine(_dir, mode));
            File.WriteAllText(Path.Combine(_dir, mode, "ControlPoints.con"), text);
        }

        var ctf = GameplayObjects.LoadFolder(_dir, "Ctf");
        Assert.Equal(new[] { "SHARED", "CTF_ONLY" }, ctf.ControlPoints.Select(c => c.Name));
        var ed = new EditableGameplay(ctf);
        EditCtf(ed);
        LevelSaver.SaveFolder(_dir, null, null, null, null, ed, gameplayMode: "Ctf");

        var ctfText = File.ReadAllText(Path.Combine(_dir, "Ctf", "ControlPoints.con"));
        Assert.Contains("CTF_ONLY", ctfText);
        Assert.Contains("CTF_NEW", ctfText);                 // the new flag landed in CTF
        Assert.Contains("321", ctfText);
        var cqText = File.ReadAllText(Path.Combine(_dir, "Conquest", "ControlPoints.con"));
        Assert.Contains("CQ_ONLY", cqText);                  // Conquest kept its own flag
        Assert.DoesNotContain("CTF_NEW", cqText);            // and did not get CTF's new one
        Assert.DoesNotContain("CTF_ONLY", cqText);
        Assert.Contains("321", cqText);                      // the shared flag's move was mirrored
    }

    [Fact]
    public void A_packed_save_writes_the_edited_mode_and_only_patches_the_others()
    {
        const string prefix = "bf1942/levels/Foo/";
        var rfa = Path.Combine(_dir, "Foo.rfa");
        RefractorFlatArchive.WriteFile(rfa, new List<(string, byte[])>
        {
            (prefix + "StaticObjects.con", Encoding.Latin1.GetBytes("rem" + NL)),
            (prefix + "Conquest/ControlPoints.con", Encoding.Latin1.GetBytes(Conquest)),
            (prefix + "Ctf/ControlPoints.con", Encoding.Latin1.GetBytes(Ctf)),
        }, compress: true, XPackId.Default);

        var ctf = GameplayObjects.LoadArchiveMode(new[] { rfa }, "Ctf");
        Assert.Equal(new[] { "SHARED", "CTF_ONLY" }, ctf.ControlPoints.Select(c => c.Name));
        var ed = new EditableGameplay(ctf);
        EditCtf(ed);
        var outRfa = Path.Combine(_dir, "Foo_001.rfa");
        LevelSaver.RepackToRfa(rfa, outRfa, null, null, null, ed, gameplayMode: "Ctf");

        var arch = new RefractorFlatArchive(outRfa);
        string Read(string rel) => Encoding.Latin1.GetString(arch.Read(arch.Entries.Single(e => e.Name.Replace('\\', '/') == prefix + rel)));
        var ctfText = Read("Ctf/ControlPoints.con");
        Assert.Contains("CTF_NEW", ctfText);
        Assert.Contains("CTF_ONLY", ctfText);
        var cqText = Read("Conquest/ControlPoints.con");
        Assert.Contains("CQ_ONLY", cqText);
        Assert.DoesNotContain("CTF_NEW", cqText);
        Assert.Contains("321", cqText);
    }

    [Fact]
    public void No_mode_named_keeps_the_old_conquest_behaviour()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Conquest"));
        Assert.Equal(Path.Combine(_dir, "Conquest"), GameplayObjects.GameplayDir(_dir, null));
        Assert.Equal(Path.Combine(_dir, "Conquest"), GameplayObjects.GameplayDir(_dir, "NoSuchMode"));
    }
}
