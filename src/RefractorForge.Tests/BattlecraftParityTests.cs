using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates for the save behaviour that was diverging from Battlecraft: an edit reaching only the game mode the
/// editor loaded from, and the support files (cullRadius / PreCache / StrategicAreas) never being written at all.
/// The reference for the file shapes is a Battlecraft-authored level (Eastern_Front\Kharkov_Day2), which ships
/// parallel Conquest/Ctf/TDM folders holding the same objects at identical coordinates but DIFFERENT sets.
/// </summary>
public class BattlecraftParityTests
{
    private const string NL = "\r\n";

    private static GameplayObjects Gp(params (string Name, float X)[] cps)
    {
        var list = new List<ControlPointDef>();
        foreach (var (n, x) in cps)
            list.Add(new ControlPointDef(n, new Vec3(x, 10f, 20f), 40f, 0));
        return new GameplayObjects(list, new List<VehicleSpawnDef>(), new List<SoldierSpawnDef>());
    }

    // ---- game-mode propagation -------------------------------------------------------------------------

    [Fact]
    public void Patching_moves_known_objects_and_leaves_everything_else_alone()
    {
        // shaped like a real Ctf/ControlPoints.con: comments, an object we edited, and one only this mode has
        var src = string.Join(NL,
            "rem *** CTF flags ***",
            "Object.create AXIS_CONTROLPOINT_CITY",
            "Object.absolutePosition 662.291/102.996/502.494",
            "rem",
            "Object.create CTF_ONLY_FLAG",
            "Object.absolutePosition 1/2/3",
            "");

        var gp = Gp(("AXIS_CONTROLPOINT_CITY", 700f));
        var outText = GameplayWriter.PatchInstanceTransforms(src.Split(NL), gp);

        Assert.Contains("Object.absolutePosition 700/10/20", outText);        // the edited flag moved
        Assert.Contains("Object.create CTF_ONLY_FLAG", outText);              // the mode-only flag survives
        Assert.Contains("Object.absolutePosition 1/2/3", outText);            // and was NOT touched
        Assert.Contains("rem *** CTF flags ***", outText);                    // comments preserved
        Assert.DoesNotContain("662.291", outText);                            // old position gone
    }

    [Fact]
    public void Patching_never_adds_or_removes_instances()
    {
        var src = string.Join(NL, "Object.create ONLY_HERE", "Object.absolutePosition 5/5/5", "");
        // the edit knows about a flag this mode does not have - it must not be introduced
        var outText = GameplayWriter.PatchInstanceTransforms(src.Split(NL), Gp(("SOMEWHERE_ELSE", 1f)));

        Assert.Equal(1, CountOf(outText, "Object.create "));
        Assert.Contains("Object.create ONLY_HERE", outText);
        Assert.DoesNotContain("SOMEWHERE_ELSE", outText);
        Assert.Contains("Object.absolutePosition 5/5/5", outText);
    }

    [Fact]
    public void Save_reaches_every_game_mode_folder()
    {
        string dir = NewLevel();
        try
        {
            foreach (var mode in new[] { "Conquest", "Ctf", "TDM" })
            {
                Directory.CreateDirectory(Path.Combine(dir, mode));
                File.WriteAllText(Path.Combine(dir, mode, "ControlPoints.con"),
                    string.Join(NL, "Object.create SHARED_FLAG", "Object.absolutePosition 100/1/100", ""));
            }
            // CTF also has a flag of its own, which must survive untouched
            File.AppendAllText(Path.Combine(dir, "Ctf", "ControlPoints.con"),
                string.Join(NL, "Object.create CTF_ONLY", "Object.absolutePosition 9/9/9", ""));

            var found = LevelSaver.GameModeDirs(dir);
            Assert.Equal(3, found.Count);

            var gameplay = new EditableGameplay(Gp(("SHARED_FLAG", 555f)));
            LevelSaver.SaveFolder(dir, null, null, null, null, gameplay);

            foreach (var mode in new[] { "Conquest", "Ctf", "TDM" })
            {
                var text = File.ReadAllText(Path.Combine(dir, mode, "ControlPoints.con"));
                Assert.Contains("555", text);          // the move reached this mode
                Assert.DoesNotContain("100/1/100", text);
            }
            // and CTF kept its own flag rather than being overwritten with Conquest's set
            var ctf = File.ReadAllText(Path.Combine(dir, "Ctf", "ControlPoints.con"));
            Assert.Contains("CTF_ONLY", ctf);
            Assert.Contains("9/9/9", ctf);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Packed_rfa_save_reaches_every_game_mode_entry()
    {
        // The editor's default save writes a patch .rfa, so the multi-mode fix has to hold there too: a packed
        // level carries one ControlPoints.con PER MODE and only the first was ever replaced.
        string dir = NewLevel();
        string baseRfa = Path.Combine(dir, "map.rfa"), outRfa = Path.Combine(dir, "map_001.rfa");
        try
        {
            const string prefix = "bf1942/levels/Foo/";
            byte[] Con(params string[] lines) => System.Text.Encoding.Latin1.GetBytes(string.Join(NL, lines) + NL);

            var entries = new List<(string, byte[])>
            {
                (prefix + "StaticObjects.con", Con("rem")),
                (prefix + "Conquest/ControlPoints.con", Con("Object.create SHARED", "Object.absolutePosition 1/1/1")),
                (prefix + "Ctf/ControlPoints.con", Con("Object.create SHARED", "Object.absolutePosition 1/1/1",
                                                       "Object.create CTF_ONLY", "Object.absolutePosition 7/7/7")),
                (prefix + "TDM/ControlPoints.con", Con("Object.create SHARED", "Object.absolutePosition 1/1/1")),
            };
            RefractorForge.Formats.Rfa.RefractorFlatArchive.WriteFile(
                baseRfa, entries, compress: true, xPackId: RefractorForge.Formats.Rfa.XPackId.Default);

            var gameplay = new EditableGameplay(Gp(("SHARED", 909f)));
            LevelSaver.RepackToRfa(baseRfa, outRfa, null, null, null, gameplay);

            var outArch = new RefractorForge.Formats.Rfa.RefractorFlatArchive(outRfa);
            string Read(string suffix)
            {
                var e = outArch.Entries.First(x => x.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                return System.Text.Encoding.Latin1.GetString(outArch.Read(e));
            }

            Assert.Contains("909", Read("Conquest/ControlPoints.con"));
            Assert.Contains("909", Read("TDM/ControlPoints.con"));

            var ctf = Read("Ctf/ControlPoints.con");
            Assert.Contains("909", ctf);            // the shared flag moved here too
            Assert.Contains("CTF_ONLY", ctf);       // and CTF's own flag survived
            Assert.Contains("7/7/7", ctf);
        }
        finally { Cleanup(dir); }
    }

    // ---- support files ---------------------------------------------------------------------------------

    [Fact]
    public void CullRadius_appends_only_the_missing_templates()
    {
        var existing = string.Join(NL,
            "REM *** Buildings & Objects ***",
            "Objecttemplate.active willys",
            "objectTemplate.cullRadiusScale 9",     // a mapper's deliberate tuning
            "").Split(NL);

        var text = LevelSupportFiles.AppendMissingCullRadius(existing, new[] { "willys", "sandbag" });
        Assert.NotNull(text);
        Assert.Contains("objectTemplate.cullRadiusScale 9", text);   // tuning preserved
        Assert.Equal(1, CountOf(text!, "Objecttemplate.active willys"));   // not duplicated
        Assert.Contains("Objecttemplate.active sandbag", text);
        Assert.Contains("objectTemplate.cullRadiusScale 5", text);   // the retail default for the new one

        // nothing missing -> no write at all
        Assert.Null(LevelSupportFiles.AppendMissingCullRadius(text!.Split(NL), new[] { "willys", "sandbag" }));
    }

    [Fact]
    public void PreCache_appends_create_delete_pairs_for_new_templates()
    {
        var existing = string.Join(NL, "Rem", "Object.active __BF_NONE__", "Object.create M16", "Object.delete", "").Split(NL);
        var text = LevelSupportFiles.AppendMissingPreCache(existing, new[] { "M16", "Sherman" });

        Assert.NotNull(text);
        Assert.Equal(1, CountOf(text!, "Object.create M16"));
        Assert.Contains("Object.create Sherman", text);
        Assert.Equal(2, CountOf(text!, "Object.delete"));            // one pair per template
        Assert.Null(LevelSupportFiles.AppendMissingPreCache(text!.Split(NL), new[] { "M16", "Sherman" }));
    }

    [Fact]
    public void Strategic_areas_are_generated_from_the_control_points()
    {
        var gp = Gp(("AXIS_BASE", 700f), ("RUSSIAN_BASE", 200f), ("CITY", 450f));
        var text = LevelSupportFiles.BuildStrategicAreas(gp.ControlPoints);

        // one area per flag, in the retail's "create <name> x1/z1 x2/z2 <value>" shape
        Assert.Equal(3, CountOf(text, "aiStrategicArea.create "));
        Assert.Contains("aiStrategicArea.create AXIS_BASE ", text);
        Assert.Equal(3, CountOf(text, "aiStrategicArea.setActive "));
        Assert.Contains("AIStrategicArea.setOrderPosition Tank ", text);
        Assert.Contains("AIStrategicArea.setOrderPosition Infantry ", text);
        Assert.Contains("aiStrategicArea.vehicleSearchRadius ", text);

        // CITY sits between the two bases, so its nearest neighbour must be a real other area, never itself
        foreach (var line in text.Split(NL))
            if (line.StartsWith("AIStrategicArea.addNeighbour"))
                Assert.Contains(line.Split(' ')[1], new[] { "AXIS_BASE", "RUSSIAN_BASE", "CITY" });
        Assert.DoesNotContain("addNeighbour" + NL, text);
    }

    [Fact]
    public void Support_files_are_written_on_save_and_a_hand_authored_ai_file_is_left_alone()
    {
        string dir = NewLevel();
        try
        {
            var aiDir = Path.Combine(dir, "ai");
            Directory.CreateDirectory(aiDir);
            const string handAuthored = "rem my own areas" + NL;
            File.WriteAllText(Path.Combine(aiDir, "StrategicAreas.con"), handAuthored);

            var gameplay = new EditableGameplay(Gp(("FLAG_A", 10f)));
            var written = LevelSaver.UpdateSupportFiles(dir, null, gameplay.ToImmutable());

            // an existing StrategicAreas.con is never regenerated over
            Assert.Equal(handAuthored, File.ReadAllText(Path.Combine(aiDir, "StrategicAreas.con")));
            Assert.DoesNotContain(written, w => w.EndsWith("StrategicAreas.con"));

            // but a level without one gets it
            string dir2 = NewLevel();
            try
            {
                LevelSaver.UpdateSupportFiles(dir2, null, gameplay.ToImmutable());
                Assert.True(File.Exists(Path.Combine(dir2, "ai", "StrategicAreas.con")));
            }
            finally { Cleanup(dir2); }
        }
        finally { Cleanup(dir); }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static string NewLevel()
    {
        var d = Path.Combine(Path.GetTempPath(), "rf_bcparity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string d) { try { Directory.Delete(d, true); } catch { } }
}
