using System.Text;
using System.Text.RegularExpressions;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The invariant behind every "my map came back wrong" report: <b>saving a level you have not edited must change
/// nothing.</b> Anything a save path alters on its own is corruption, because the user did not ask for it.
///
/// This is the gate that would have caught the duplicate-spawn bug on the day it was written. Retail Bocage
/// declares eight <c>AAGunSpawner</c> instances in one file; the instance patcher keyed them by template name,
/// kept only the last, and wrote its position over all eight - so pressing save with no edits collapsed them into
/// a single stack and the map spawned vehicles on top of each other. Every save path is checked here, because the
/// bug lived in code shared by all of them and only showed up in the game modes the editor was not displaying.
/// </summary>
public class ZeroEditSaveTests : IDisposable
{
    private readonly string _dir;
    public ZeroEditSaveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rfzero_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Prefix = "bf1942/levels/TestMap/";

    /// <summary>One placed instance as the engine reads it: what it is, where, and how it is set up.</summary>
    private sealed record Placed(string Template, string Position, string? Rotation, string? Team, string? OsId);

    /// <summary>Compare two placements by VALUE, not by text. Retail writes "1.52588e-005" where our writer emits
    /// "0.000015" - the same number in a different shape. Re-formatting a float is not corruption; moving,
    /// dropping or duplicating an instance is, and that is what this has to stay sensitive to.</summary>
    private static bool SamePlacement(Placed a, Placed b)
        => a.Template.Equals(b.Template, StringComparison.OrdinalIgnoreCase)
        && SameVec(a.Position, b.Position) && SameVec(a.Rotation, b.Rotation)
        && a.Team == b.Team && a.OsId == b.OsId;

    private static bool SameVec(string? a, string? b)
    {
        if (a is null || b is null) return a == b;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        var x = a.Split('/'); var y = b.Split('/');
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
        {
            if (!float.TryParse(x[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fa) ||
                !float.TryParse(y[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fb))
                return false;
            if (MathF.Abs(fa - fb) > 0.001f) return false;      // 1 mm / one thousandth of a degree
        }
        return true;
    }

    /// <summary>Position rounded to millimetres, so "collapsed onto the same spot" is judged by value too.</summary>
    private static string PosKey(Placed p)
    {
        var s = p.Position.Split('/');
        var parts = s.Select(v => float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)
                                  ? f.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : v);
        return p.Template.ToLowerInvariant() + "@" + string.Join('/', parts);
    }

    /// <summary>Parse a gameplay .con the way the engine runs it - in order, duplicates and all.</summary>
    private static List<Placed> ParsePlacements(string text)
    {
        var outp = new List<Placed>();
        string? tmpl = null, pos = null, rot = null, team = null, osid = null;
        void Flush() { if (tmpl is not null && pos is not null) outp.Add(new Placed(tmpl, pos, rot, team, osid)); }
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            var sp = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length == 0) continue;
            switch (sp[0].ToLowerInvariant())
            {
                case "object.create":
                    Flush();
                    tmpl = sp.Length > 1 ? sp[1] : null; pos = rot = team = osid = null;
                    break;
                case "object.absoluteposition": pos = sp.Length > 1 ? sp[1] : null; break;
                case "object.rotation": rot = sp.Length > 1 ? sp[1] : null; break;
                case "object.setteam": team = sp.Length > 1 ? sp[1] : null; break;
                case "object.setosid": osid = sp.Length > 1 ? sp[1] : null; break;
            }
        }
        Flush();
        return outp;
    }

    /// <summary>A level shaped like a real one: four game modes with DIFFERENT spawn counts, vehicle spawners that
    /// repeat a template many times over (the case that broke), and uniquely named flags and soldier spawns.</summary>
    private static string ModeFiles(int heavyTanks, int aaGuns, out string soldiers, out string flags)
    {
        var sb = new StringBuilder();
        sb.Append("if v_arg1 == host\r\nrem ----- Host\r\n");
        for (int i = 0; i < heavyTanks; i++)
            sb.Append("rem\r\nObject.create HeavytankSpawner \r\n")
              .Append($"Object.absolutePosition {100 + i * 7}/20.5/{200 + i * 3}\r\n")
              .Append($"Object.rotation {i * 11}/0/1.52588e-005\r\nObject.setOSId {i + 1}\r\nObject.setTeam {(i % 2) + 1}\r\n");
        for (int i = 0; i < aaGuns; i++)
            sb.Append("rem\r\nObject.create AAGunSpawner \r\n")
              .Append($"Object.absolutePosition {500 + i * 13}/22.1/{800 + i * 5}\r\n")
              .Append($"Object.rotation {i * 5}/0/1.52588e-005\r\nObject.setTeam {(i % 2) + 1}\r\n");
        sb.Append("endIf\r\n");

        var sol = new StringBuilder();
        for (int i = 0; i < 6; i++)
            sol.Append($"rem\r\nObject.create AxisSpawnPoint_{i} \r\nObject.absolutePosition {300 + i}/34/{400 + i}\r\nObject.rotation {i * 3}/0/1.52588e-005\r\n");
        soldiers = sol.ToString();

        var fl = new StringBuilder();
        foreach (var n in new[] { "AXISBASE_Cpoint", "ALLIESBase_Cpoint", "Village_Cpoint" })
            fl.Append($"rem\r\nObject.create {n} \r\nObject.absolutePosition 590/33.99/1078\r\nObject.rotation 0/0/1.52588e-005\r\n");
        flags = fl.ToString();
        return sb.ToString();
    }

    private string MakeLevelArchive()
    {
        var entries = new List<(string, byte[])>();
        byte[] B(string s) => Encoding.Latin1.GetBytes(s);

        // Four modes, deliberately different sizes - the mismatch is what the patcher has to cope with.
        foreach (var (mode, tanks, aa) in new[] { ("Conquest", 3, 8), ("Ctf", 3, 8), ("SinglePlayer", 2, 5), ("TDM", 4, 9) })
        {
            var spawns = ModeFiles(tanks, aa, out var soldiers, out var flags);
            entries.Add(($"{Prefix}{mode}/ObjectSpawns.con", B(spawns)));
            entries.Add(($"{Prefix}{mode}/SoldierSpawns.con", B(soldiers)));
            entries.Add(($"{Prefix}{mode}/ControlPoints.con", B(flags)));
            entries.Add(($"{Prefix}{mode}/ControlPointTemplates.con", B("ObjectTemplate.create ControlPoint AXISBASE_Cpoint\r\nObjectTemplate.radius 40\r\n")));
            entries.Add(($"{Prefix}{mode}/SoldierSpawnTemplates.con", B("ObjectTemplate.create SpawnPoint AxisSpawnPoint_0\r\nObjectTemplate.setGroup 1\r\n")));
            entries.Add(($"{Prefix}{mode}/ObjectSpawnTemplates.con", B("ObjectTemplate.create ObjectSpawner HeavytankSpawner\r\nObjectTemplate.setObjectTemplate 1 Tiger\r\n")));
        }

        var so = new StringBuilder();
        for (int i = 0; i < 12; i++)   // one template placed many times, as every real map does
            so.Append($"Object.create hedge_m1\r\nObject.absolutePosition {10 + i * 4}/12.5/{20 + i * 6}\r\nObject.rotation {i}/0/0\r\n");
        entries.Add(($"{Prefix}StaticObjects.con", B(so.ToString())));

        entries.Add(($"{Prefix}Heightmap.raw", Enumerable.Range(0, 64 * 64).SelectMany(i => BitConverter.GetBytes((ushort)(i % 500))).ToArray()));
        entries.Add(($"{Prefix}MaterialMap.raw", Enumerable.Range(0, 64 * 64).Select(i => (byte)(i % 7)).ToArray()));
        entries.Add(($"{Prefix}Init/Terrain.con", B("Terrain.worldSize 1024\r\nTerrain.materialSize 64\r\nTerrain.yScale 1\r\nTerrain.waterLevel 30\r\n")));
        entries.Add(($"{Prefix}Init.con", B("rem level init\r\nrun Init/Terrain\r\n")));

        var path = Path.Combine(_dir, "TestMap.rfa");
        RefractorFlatArchive.WriteFile(path, entries, compress: false, XPackId.Default);
        return path;
    }

    /// <summary>Read every gameplay file out of an archive as ordered placements.</summary>
    private static Dictionary<string, List<Placed>> Gameplay(RefractorFlatArchive a)
    {
        var d = new Dictionary<string, List<Placed>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in a.Entries)
        {
            var n = e.Name.Replace('\\', '/');
            if (!Regex.IsMatch(n, @"(ObjectSpawns|SoldierSpawns|ControlPoints|StaticObjects)\.con$", RegexOptions.IgnoreCase)) continue;
            d[n] = ParsePlacements(Encoding.Latin1.GetString(a.Read(e)));
        }
        return d;
    }

    private static void AssertUnchanged(Dictionary<string, List<Placed>> before, Dictionary<string, List<Placed>> after)
    {
        foreach (var (name, want) in before)
        {
            Assert.True(after.ContainsKey(name), $"{name} disappeared from the save");
            var got = after[name];
            Assert.True(want.Count == got.Count,
                $"{name}: instance count changed {want.Count} -> {got.Count} on a save with no edits");
            for (int i = 0; i < want.Count; i++)
                Assert.True(SamePlacement(want[i], got[i]),
                    $"{name}[{i}] changed on a save with no edits:\n  was {want[i]}\n  now {got[i]}");

            // The specific corruption: distinct instances collapsing onto one spot.
            int distinctBefore = want.Select(PosKey).Distinct().Count();
            int distinctAfter = got.Select(PosKey).Distinct().Count();
            Assert.True(distinctBefore == distinctAfter,
                $"{name}: {distinctBefore - distinctAfter} instance(s) collapsed onto another's position");
        }
    }

    /// <summary>Load the level exactly as the editor does, with nothing touched.</summary>
    private (StaticObjectsFile So, EditableGameplay Gp, Heightmap Hm, MaterialMap Mm) LoadUnedited(string rfa)
    {
        var a = new RefractorFlatArchive(rfa);
        string[] L(string suffix) => Encoding.Latin1
            .GetString(a.Read(a.Entries.First(e => e.Name.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))))
            .Split('\n');

        var so = StaticObjectsFile.Parse(L("StaticObjects.con"));
        var gp = new EditableGameplay(GameplayObjects.Parse(
            L("Conquest/ControlPoints.con"), L("Conquest/ControlPointTemplates.con"),
            L("Conquest/ObjectSpawns.con"), L("Conquest/ObjectSpawnTemplates.con"),
            L("Conquest/SoldierSpawns.con"), L("Conquest/SoldierSpawnTemplates.con")));
        var hmBytes = a.Read(a.Entries.First(e => e.Name.EndsWith("Heightmap.raw", StringComparison.OrdinalIgnoreCase)));
        var hm = Heightmap.FromBytes(hmBytes, 64, 64);
        var mm = new MaterialMap(64, 64);
        a.Read(a.Entries.First(e => e.Name.EndsWith("MaterialMap.raw", StringComparison.OrdinalIgnoreCase))).CopyTo(mm.Samples, 0);
        return (so, gp, hm, mm);
    }

    [Fact]
    public void Patch_save_with_no_edits_changes_nothing()
    {
        var rfa = MakeLevelArchive();
        var before = Gameplay(new RefractorFlatArchive(rfa));
        var (so, gp, hm, mm) = LoadUnedited(rfa);

        var outPath = Path.Combine(_dir, "TestMap_001.rfa");
        LevelSaver.WritePatchRfa(rfa, outPath, so, hm, mm, gp);
        Assert.Null(RefractorFlatArchive.Validate(outPath));

        // The patch only carries what it rewrote, so compare the files it did write.
        var patched = Gameplay(new RefractorFlatArchive(outPath));
        AssertUnchanged(before.Where(kv => patched.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), patched);
        Assert.NotEmpty(patched);
    }

    [Fact]
    public void Repack_with_no_edits_changes_nothing()
    {
        var rfa = MakeLevelArchive();
        var before = Gameplay(new RefractorFlatArchive(rfa));
        var (so, gp, hm, mm) = LoadUnedited(rfa);

        var outPath = Path.Combine(_dir, "Repacked.rfa");
        LevelSaver.RepackToRfa(rfa, outPath, so, hm, mm, gp);
        Assert.Null(RefractorFlatArchive.Validate(outPath));

        AssertUnchanged(before, Gameplay(new RefractorFlatArchive(outPath)));

        // A repack keeps the whole level, not just the edited part.
        var after = new RefractorFlatArchive(outPath);
        Assert.Equal(new RefractorFlatArchive(rfa).Entries.Count, after.Entries.Count);
    }

    [Fact]
    public void Folder_save_with_no_edits_changes_nothing()
    {
        var rfa = MakeLevelArchive();
        var before = Gameplay(new RefractorFlatArchive(rfa));

        var folder = Path.Combine(_dir, "extracted");
        LevelSaver.ExtractToFolder(new[] { rfa }, folder);
        var (so, gp, hm, mm) = LoadUnedited(rfa);

        LevelSaver.SaveFolder(folder, so, Path.Combine(folder, "StaticObjects.con"), hm, mm, gp);

        var after = new Dictionary<string, List<Placed>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(folder, "*.con", SearchOption.AllDirectories))
        {
            if (!Regex.IsMatch(Path.GetFileName(f), @"(ObjectSpawns|SoldierSpawns|ControlPoints|StaticObjects)\.con$", RegexOptions.IgnoreCase)) continue;
            var rel = Prefix + Path.GetRelativePath(folder, f).Replace('\\', '/');
            after[rel] = ParsePlacements(File.ReadAllText(f));
        }
        AssertUnchanged(before, after);
    }

    /// <summary>The exact shape that broke: many instances of ONE template in a mode the editor did not load.</summary>
    [Fact]
    public void Repeated_templates_in_an_unloaded_game_mode_survive_a_no_edit_save()
    {
        var rfa = MakeLevelArchive();
        var (so, gp, hm, mm) = LoadUnedited(rfa);
        var outPath = Path.Combine(_dir, "TestMap_002.rfa");
        LevelSaver.WritePatchRfa(rfa, outPath, so, hm, mm, gp);

        var a = new RefractorFlatArchive(outPath);
        foreach (var mode in new[] { "Conquest", "Ctf", "SinglePlayer", "TDM" })
        {
            var e = a.Entries.FirstOrDefault(x => x.Name.Replace('\\', '/').EndsWith($"{mode}/ObjectSpawns.con", StringComparison.OrdinalIgnoreCase));
            if (e is null) continue;
            var placed = ParsePlacements(Encoding.Latin1.GetString(a.Read(e)));
            var aa = placed.Where(p => p.Template.Equals("AAGunSpawner", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(aa.Count == aa.Select(p => p.Position).Distinct().Count(),
                $"{mode}: {aa.Count} AA guns collapsed onto {aa.Select(p => p.Position).Distinct().Count()} position(s)");
        }
    }
}
