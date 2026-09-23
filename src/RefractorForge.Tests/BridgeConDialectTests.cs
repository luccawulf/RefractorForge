using System.Text;
using RefractorBridge.Con;
using RefractorBridge.Oracle;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The gates on RefractorBridge's .con dialect engine (the BF1942 -> Battlefield Vietnam converter's foundation).
///
/// Three properties matter, and all three have burned this port before:
///  1. the documented rewrite is the rewrite actually performed;
///  2. it is IDEMPOTENT - converting already-converted content must be a no-op, or the tool cannot be run twice
///     and cannot be trusted mid-pipeline;
///  3. it is byte-faithful - line endings survive exactly and comments are never edited. Text-mode reads silently
///     turned CRLF into LF on this project once, and Desert Combat writes "rem GeometryTemplate.file ..." lines
///     that an unanchored rewrite would happily edit inside.
/// </summary>
public class BridgeConDialectTests
{
    private const string Bf42Exe = @"D:\Games\EA GAMES\Battlefield 1942\BF1942.exe";
    private const string BfvExe = @"D:\Games\EA GAMES\Battlefield Vietnam\BfVietnam.exe";

    private static string Rewrite(string source, ConDialectOptions? options = null) =>
        Encoding.Latin1.GetString(ConDialect.Rewrite(source, "test.con", options).Bytes);

    // --- gate 1: the documented dialect --------------------------------------------------------------------

    [Fact]
    public void Rewrite_applies_the_documented_bf1942_to_bfv_dialect()
    {
        // Every line here is one the porting notes call out by name.
        string source = string.Join("\r\n", new[]
        {
            "rem --- BF1942 level head ---",
            "renderer.globalAmbientColor .15/.15/.15",
            "renderer.fogLinearStart 50",
            "renderer.fogLinearEnd 90",
            "shadow.shadowColor 0.55",
            "Game.setViewDistance 350",
            "Object.setName track",
            "water.texLayer1 somewater",
            "water.specularEnable 1",
            "water.color 0.5/0.5/0.5",
            "ObjectTemplate.hasResponsePhysics 1",
            "ObjectTemplate.lodDistance 100",
            "GeometryTemplate.lodDistance 350",
            "GeometryTemplate.setLodDistance 200",
            "renderer.beginGlobalCluster",
            "renderer.endGlobalCluster",
            "ShaderManager.setTextureParam envmap foo.rcm",
            "game.setActiveCombatArea 380 0 416 416",
            "",
        });

        string expected = string.Join("\r\n", new[]
        {
            "rem --- BF1942 level head ---",
            "renderer.fogstart 50",
            "renderer.fogend 90",
            "shadow.shadowColor 0/0/.075/.5",
            "Game.ViewDistance 350",
            "Object.Name track",
            "water.color 0.5/0.5/0.5",
            "ObjectTemplate.lodDistance 100",       // the ObjectTemplate one is real (720 retail uses) - untouched
            "GeometryTemplate.setLodDistance 200",  // the set- form is real (7589 retail uses) - untouched
            "game.setActiveCombatArea 380 0 416 416",
            "",
        });

        Assert.Equal(expected, Rewrite(source));
    }

    [Fact]
    public void Response_physics_is_dropped_by_default_and_mapped_only_on_request()
    {
        const string source = "ObjectTemplate.hasResponsePhysics 1\r\n";

        Assert.Equal("", Rewrite(source));
        Assert.Equal("ObjectTemplate.hasMobilePhysics 1\r\n",
            Rewrite(source, new ConDialectOptions { MapResponsePhysics = true }));
    }

    [Fact]
    public void A_shadow_colour_that_is_already_a_vector_is_left_alone()
    {
        const string source = "shadow.shadowColor 0/0/.075/.5\r\n";
        Assert.Equal(source, Rewrite(source));
    }

    // --- rules derived from the 20-mod corpus census ---------------------------------------------------------

    [Theory]
    // BF1942-only: in BF1942.exe, absent from BfVietnam.exe.
    [InlineData("game.addLanguageRunTimeDirectory bf1942/levels/x")]
    [InlineData("render.beginGlobalCluster")]
    [InlineData("render.endGlobalCluster")]
    [InlineData("kitTemplate.allowedAllied 1")]
    [InlineData("kitTemplate.allowedAxis 1")]
    // Dead in BOTH engines - no-ops the port simply inherits.
    [InlineData("NetworkableInfo.setHasOrientation 1")]
    [InlineData("NetworkableInfo.setIsControlledBy 1")]
    public void Commands_found_across_the_mod_corpus_are_dropped(string line)
    {
        // Every one of these was UNRULED on a first pass over 20 mods (~40,000 .con files), then settled by
        // asking both executables. With the rules in place that census reports zero unruled commands.
        var result = ConDialect.Rewrite(line + "\r\n", "Objects.con");

        Assert.Single(result.Changes);
        Assert.Equal("", Encoding.Latin1.GetString(result.Bytes));
    }

    [Fact]
    public void A_command_absent_from_both_engines_is_not_reported_as_a_porting_problem()
    {
        if (!File.Exists(Bf42Exe) || !File.Exists(BfvExe)) return;

        var bf42 = ExeSymbolTable.Load(Bf42Exe);
        var bfv = ExeSymbolTable.Load(BfvExe);

        // Pirates ships 'setPBlueictionMode' - setPredictionMode after a global Red->Blue search-and-replace.
        // It is broken in BF1942 too, so it is dead weight, not something BFV took away.
        var (verdict, _) = ConDialect.Classify("NetworkableInfo.setPBlueictionMode", bfv, bf42);
        Assert.Equal(CommandVerdict.DropDeadInBoth, verdict);

        // Whereas a command BF1942 really does implement and BFV really does not is the actionable kind.
        var (real, _) = ConDialect.Classify("shaderManager.setTextureParam", bfv, bf42);
        Assert.Equal(CommandVerdict.DropBf1942Only, real);
    }

    // --- gate 2: idempotence -------------------------------------------------------------------------------

    [Fact]
    public void Rewriting_twice_changes_nothing_the_second_time()
    {
        string source = string.Join("\r\n", new[]
        {
            "renderer.fogLinearStart 50",
            "shadow.shadowColor 0.55",
            "Object.setName track",
            "water.normalMap foo",
            "ObjectTemplate.hasResponsePhysics 1",
            "",
        });

        var first = ConDialect.Rewrite(source, "test.con");
        var second = ConDialect.Rewrite(first.Bytes, "test.con");

        Assert.NotEmpty(first.Changes);
        Assert.Empty(second.Changes);
        Assert.Equal(first.Bytes, second.Bytes);
    }

    [Fact]
    public void Content_with_nothing_to_change_comes_back_byte_identical()
    {
        // The no-op guarantee: a transform that cannot leave a file alone cannot be trusted to change one.
        byte[] source = Encoding.Latin1.GetBytes(string.Join("\r\n", new[]
        {
            "rem a perfectly ordinary BFV level",
            "renderer.fogstart 10",
            "renderer.fogend 150",
            "Game.ViewDistance 170",
            "game.setActiveCombatArea 316 358 1024 1024",
            "GeometryTemplate.waveHeight 1.0",
            "",
        }));

        var result = ConDialect.Rewrite(source, "Init.con");

        Assert.Empty(result.Changes);
        Assert.Equal(source, result.Bytes);
    }

    // --- gate 3: bytes and comments ------------------------------------------------------------------------

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Line_endings_survive_the_rewrite(string eol)
    {
        string source = $"renderer.fogLinearStart 50{eol}renderer.diffuseColor 0.9/0.9/0.9{eol}";
        string expected = $"renderer.fogstart 50{eol}renderer.diffuseColor 0.9/0.9/0.9{eol}";
        Assert.Equal(expected, Rewrite(source));
    }

    [Fact]
    public void Mixed_line_endings_are_preserved_per_line()
    {
        // A file that is CRLF in places and CR-only in others keeps each line exactly as it was.
        string source = "renderer.fogLinearStart 50\r\nrenderer.ambientColor .2\rrenderer.fogLinearEnd 90\n";
        string expected = "renderer.fogstart 50\r\nrenderer.ambientColor .2\rrenderer.fogend 90\n";
        Assert.Equal(expected, Rewrite(source));
    }

    [Fact]
    public void Commented_out_commands_are_never_edited()
    {
        // Desert Combat really does write "rem GeometryTemplate.file ..." - an unanchored rewrite edits inside it.
        string source = string.Join("\r\n", new[]
        {
            "rem renderer.fogLinearStart 50",
            "rem water.texLayer1 something",
            "beginRem",
            "shadow.shadowColor 0.55",
            "ObjectTemplate.hasResponsePhysics 1",
            "endRem",
            "renderer.fogLinearEnd 90",
            "",
        });

        string expected = string.Join("\r\n", new[]
        {
            "rem renderer.fogLinearStart 50",
            "rem water.texLayer1 something",
            "beginRem",
            "shadow.shadowColor 0.55",
            "ObjectTemplate.hasResponsePhysics 1",
            "endRem",
            "renderer.fogend 90",
            "",
        });

        Assert.Equal(expected, Rewrite(source));
    }

    [Fact]
    public void Indentation_and_arguments_are_preserved_across_a_rename()
    {
        const string source = "\t  Object.setName   track  \r\n";
        Assert.Equal("\t  Object.Name   track  \r\n", Rewrite(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("a\r\r\rb")]
    [InlineData("a\r\r\r\nb")]
    [InlineData("no terminator at all")]
    [InlineData("mixed\r\nis\rfine\ntoo\r\n")]
    public void The_line_splitter_round_trips_exactly(string text)
    {
        Assert.Equal(text, ConText.Join(ConText.Split(text)));
    }

    // --- review flags: the converter must NOT silently guess these -----------------------------------------

    [Fact]
    public void TreeMesh_is_reported_rather_than_rewritten()
    {
        // BFV registers no TreeMesh geometry type, so this object cannot load - but deleting the create line
        // would orphan every reference to it, so the tool reports and leaves it.
        const string source = "GeometryTemplate.create TreeMesh palm1_m1\r\n";
        var result = ConDialect.Rewrite(source, "geometries.con");

        Assert.Empty(result.Changes);
        Assert.Equal(source, Encoding.Latin1.GetString(result.Bytes));
        Assert.Contains(result.Reviews, r => r.Note.Contains("TreeMesh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_per_class_set_family_is_flagged_but_never_rewritten()
    {
        // BfVietnam.exe registers BOTH spellings, so only a per-class census of retail content can settle these.
        const string source = "ObjectTemplate.setTorque 100\r\n";
        var result = ConDialect.Rewrite(source, "objects.con");

        Assert.Empty(result.Changes);
        Assert.Equal(source, Encoding.Latin1.GetString(result.Bytes));
        Assert.Contains(result.Reviews, r => r.Note.Contains("per-class rename candidate"));
    }

    [Fact]
    public void Properties_where_bfv_keeps_the_set_form_are_not_flagged()
    {
        // setLodDistance has 7589 retail uses; flagging it would send someone to break working content.
        var result = ConDialect.Rewrite("GeometryTemplate.setLodDistance 200\r\n", "geometries.con");
        Assert.Empty(result.Reviews);
    }

    [Fact]
    public void A_two_argument_team_skin_is_flagged_for_its_missing_index()
    {
        var result = ConDialect.Rewrite("game.setTeamSkin 1 IraqSoldier\r\n", "Init.con");
        Assert.Contains(result.Reviews, r => r.Note.Contains("index 1..4"));

        // The three-argument BFV form is already correct and must not be flagged.
        Assert.Empty(ConDialect.Rewrite("game.setTeamSkin 1 1 NVAArmyA1\r\n", "Init.con").Reviews);
    }

    // --- M0: the oracle, against the real executables -------------------------------------------------------

    [Fact]
    public void The_executables_reproduce_the_documented_command_delta()
    {
        if (!File.Exists(Bf42Exe) || !File.Exists(BfvExe)) return;   // not a dev box with the games installed

        var bf42 = ExeSymbolTable.Load(Bf42Exe);
        var bfv = ExeSymbolTable.Load(BfvExe);

        Assert.True(bf42.IdentifierCount > 1000, $"BF1942.exe yielded only {bf42.IdentifierCount} identifiers");
        Assert.True(bfv.IdentifierCount > 1000, $"BfVietnam.exe yielded only {bfv.IdentifierCount} identifiers");

        // BF1942-only, every one of them a documented porting trap. Note these are looked up as BARE PROPERTY
        // NAMES: Refractor's table holds "setTextureParam", never "shaderManager.setTextureParam", and searching
        // for the dotted form finds nothing in either executable.
        foreach (string bf42Only in new[] { "setTextureParam", "globalAmbientColor", "texLayer1", "hasResponsePhysics" })
        {
            Assert.True(bf42.HasProperty(bf42Only), $"{bf42Only} should be in BF1942.exe");
            Assert.False(bfv.HasProperty(bf42Only), $"{bf42Only} should be ABSENT from BfVietnam.exe");
        }

        // Ports unchanged - the combat area is origin+size in both.
        Assert.True(bfv.HasProperty("setActiveCombatArea"));

        // The trap in the middle: BfVietnam.exe DOES carry the set- spelling, which is exactly why the converter
        // refuses to rewrite the setX family on the exe's word alone.
        Assert.True(bfv.HasProperty("setTorque"));
    }

    [Fact]
    public void Set_twins_are_computed_both_ways()
    {
        Assert.Equal("ObjectTemplate.torque", ExeSymbolTable.SetTwin("ObjectTemplate.setTorque"));
        Assert.Equal("ObjectTemplate.setTorque", ExeSymbolTable.SetTwin("ObjectTemplate.torque"));
        Assert.Equal("", ExeSymbolTable.SetTwin("nodot"));
        Assert.Equal("torque", ExeSymbolTable.SetTwinLeaf("setTorque"));
        Assert.Equal("setTorque", ExeSymbolTable.SetTwinLeaf("torque"));
    }

    [Fact]
    public void A_property_reached_only_through_a_dotted_form_is_still_found()
    {
        // The scanner's own regression: harvesting "Object.name" must record 'name' as a property too, not just
        // the dotted token. Missing that read 'setTorque' as absent from an engine that plainly carries it.
        if (!File.Exists(BfvExe)) return;

        var bfv = ExeSymbolTable.Load(BfvExe);
        Assert.True(bfv.HasDotted("Object.name"));
        Assert.True(bfv.HasProperty("name"));
    }
}
