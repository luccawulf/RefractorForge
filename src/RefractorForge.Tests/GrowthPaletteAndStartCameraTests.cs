using System.Linq;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Two settings the editor was getting wrong on real maps: the growth palette it could not read at all, and the
/// pre-spawn camera it never wrote.
/// </summary>
public class GrowthPaletteTests
{
    // Khe_Sahn and Lang_Vei ship this shape: <wetDirt> opened, </juicyGrass> closing it, and a duplicated dryDirt.
    // XDocument throws on it; the game loads it; the editor showed those maps with no trees at all.
    private const string MisNested = """
 <?xml version="1.0"?>
<WRAPPER_TREE VERS = "1.1">
    <overGrowth materialMapSideSize = "512" viewDistance = "550" importSceneObjects = "true">
        <materials>
            <default><types></types></default>
            <water><types></types></water>
            <dryGrass><types>
                <c02f_trees_m2 geometryName = "c02f_trees_m2" probability = ".7" scale = "CRDUniform/.8/1.2/false"></c02f_trees_m2>
            </types></dryGrass>
            <juicyGrass><types></types></juicyGrass>
            <dryDirt><types></types></dryDirt>
            <wetDirt>
                <types>
                    <c07f_jungle_m2 geometryName = "c07f_jungle_m2" probability = ".8"></c07f_jungle_m2>
                </types>
            </juicyGrass>
            <dryDirt><types></types></dryDirt>
            <mud><types>
                <c01f_trees_m2 geometryName = "c01f_trees_m2" probability = "1"></c01f_trees_m2>
            </types></mud>
        </materials>
    </overGrowth>
</WRAPPER_TREE>
""";

    [Fact]
    public void A_wst_that_closes_a_tag_with_the_wrong_name_still_loads()
    {
        var pal = FoliagePalette.Parse(MisNested);
        Assert.True(pal.IsOver);
        Assert.Equal(512, pal.MaterialMapSideSize);
        Assert.Equal(550f, pal.ViewDistance, 3);
        Assert.Equal(3, pal.TypeCount);
        Assert.Contains("c07f_jungle_m2", pal.DistinctGeometries);
    }

    [Fact]
    public void A_duplicated_material_does_not_shift_every_slot_after_it()
    {
        var pal = FoliagePalette.Parse(MisNested);
        // Counting down the list, index 6 lands on the SECOND dryDirt (which grows nothing) instead of mud.
        Assert.Equal("dryDirt", pal.Materials[6].Name);
        // Resolving by the engine's material name gets it right.
        Assert.Equal("mud", pal.SlotForIndex(6)!.Name);
        Assert.Equal("wetDirt", pal.SlotForIndex(5)!.Name);
        Assert.Equal("dryGrass", pal.SlotForIndex(2)!.Name);
    }

    [Fact]
    public void An_element_name_that_is_not_a_legal_xml_name_is_accepted()
    {
        // Dogs_of_War has one whose name starts with "(". The name never mattered - geometryName does.
        var pal = FoliagePalette.Parse("""
<WRAPPER_TREE><overGrowth><materials>
  <dryGrass><types>
    <(copy)of_tree geometryName = "c02f_trees_m2" probability = ".5"></(copy)of_tree>
  </types></dryGrass>
</materials></overGrowth></WRAPPER_TREE>
""");
        Assert.Equal(1, pal.TypeCount);
        Assert.Equal("c02f_trees_m2", pal.Materials[0].Types[0].GeometryName);
    }

    [Fact]
    public void A_palette_whose_names_are_not_the_engines_still_resolves_by_position()
    {
        // Operation_Flaming_Dart renames slot 0 to "a".
        var pal = FoliagePalette.Parse("""
<WRAPPER_TREE><overGrowth><materials>
  <a><types></types></a>
  <water><types></types></water>
</materials></overGrowth></WRAPPER_TREE>
""");
        Assert.Equal("a", pal.SlotForIndex(0)!.Name);
        Assert.Equal("water", pal.SlotForIndex(1)!.Name);
        Assert.Null(pal.SlotForIndex(9));
    }

    [Fact]
    public void The_original_text_is_preserved_for_writing_back()
    {
        Assert.Equal(MisNested, FoliagePalette.Parse(MisNested).RawXml);
    }
}

public class StartCameraTests
{
    private static string[] Init(params string[] extra) => new[]
    {
        "game.setMode gpm_cq",
        "renderer.diffuseColor 1/1/1",
        "run Sounds/Environment",
    }.Concat(extra).ToArray();

    [Fact]
    public void The_pre_spawn_camera_is_read_per_team()
    {
        var e = EnvironmentSettings.Parse(null, null, Init(
            "game.setBeforeSpawnCameraPosition 1 498.28/56.171/406.15",
            "game.setBeforeSpawnCameraPosition 2 100/20/300",
            "game.setBeforeSpawnCameraRotation 1 225/-15/0"));

        Assert.Equal(498.28f, e.StartCameraPosition[1].X, 2);
        Assert.Equal(300f, e.StartCameraPosition[2].Z, 2);
        Assert.Equal(225f, e.StartCameraRotation[1].X, 2);
    }

    [Fact]
    public void A_rem_d_camera_line_is_left_alone()
    {
        // Retail keeps the previous position commented out just above the live one; a mapper reads those as history.
        var e = EnvironmentSettings.Parse(null, null, Init(
            "rem game.setBeforeSpawnCameraPosition 1 842.669/60.9473/1316.73",
            "game.setBeforeSpawnCameraPosition 1 989.1/40.395/1180.6"));
        Assert.Equal(989.1f, e.StartCameraPosition[1].X, 2);

        e.SetStartCamera(new Vec3(10f, 20f, 30f), new Vec3(45f, 5f, 0f));
        var outLines = e.PatchInitConLines(Init(
            "rem game.setBeforeSpawnCameraPosition 1 842.669/60.9473/1316.73",
            "game.setBeforeSpawnCameraPosition 1 989.1/40.395/1180.6"));
        Assert.Contains(outLines, l => l.StartsWith("rem game.setBeforeSpawnCameraPosition 1 842.669"));
        Assert.DoesNotContain(outLines, l => l.Trim().StartsWith("game.setBeforeSpawnCameraPosition 1 989.1"));
    }

    [Fact]
    public void Setting_it_rewrites_every_team_in_place_and_adds_the_missing_one()
    {
        var e = EnvironmentSettings.Parse(null, null, Init("game.setBeforeSpawnCameraPosition 1 1/2/3"));
        e.SetStartCamera(new Vec3(500f, 60f, 400f), new Vec3(-90f, 12f, 0f));

        var outLines = e.PatchInitConLines(Init("game.setBeforeSpawnCameraPosition 1 1/2/3"));
        var pos = outLines.Where(l => l.StartsWith("game.setBeforeSpawnCameraPosition")).ToList();
        var rot = outLines.Where(l => l.StartsWith("game.setBeforeSpawnCameraRotation")).ToList();

        Assert.Equal(2, pos.Count);                                  // team 1 rewritten, team 2 added
        Assert.Equal(2, rot.Count);
        Assert.All(pos, l => Assert.Contains("500", l));
        Assert.Contains(pos, l => l.StartsWith("game.setBeforeSpawnCameraPosition 1 "));
        Assert.Contains(pos, l => l.StartsWith("game.setBeforeSpawnCameraPosition 2 "));
        Assert.All(rot, l => Assert.Contains("-90", l));
    }

    [Fact]
    public void A_level_that_never_set_one_is_untouched_until_the_editor_sets_it()
    {
        var e = EnvironmentSettings.Parse(null, null, Init());
        Assert.DoesNotContain(e.PatchInitConLines(Init()), l => l.Contains("BeforeSpawnCamera"));
    }

    [Fact]
    public void It_round_trips()
    {
        var e = EnvironmentSettings.Parse(null, null, Init());
        e.SetStartCamera(new Vec3(123.5f, 45.25f, 678.75f), new Vec3(30f, -10f, 0f));
        var back = EnvironmentSettings.Parse(null, null, e.PatchInitConLines(Init()));

        Assert.Equal(123.5f, back.StartCameraPosition[1].X, 2);
        Assert.Equal(45.25f, back.StartCameraPosition[2].Y, 2);
        Assert.Equal(-10f, back.StartCameraRotation[1].Y, 2);
    }
}
