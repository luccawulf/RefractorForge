using RefractorForge.Formats.Con;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Battlecraft's "Edit Object Spawn Template" fields (guide figure 24) live on the shared ObjectSpawner template, so
/// every spawn using it changes together. Reading them is easy; WRITING them safely is the part with teeth - only
/// lines a level already declares may be rewritten, or a map gains timing values it never had.
/// </summary>
public class SpawnTemplateTests
{
    private const string Spawns = """
        Object.create jeepspawner
        Object.absolutePosition 100/20/200
        Object.rotation 0/0/0
        Object.setOSId 1
        Object.setTeam 2
        """;

    private const string Templates = """
        ObjectTemplate.create ObjectSpawner jeepspawner
        ObjectTemplate.setObjectTemplate 1 kubelwagen
        ObjectTemplate.setObjectTemplate 2 willy
        ObjectTemplate.MinSpawnDelay 15
        ObjectTemplate.MaxSpawnDelay 45
        ObjectTemplate.SpawnDelayAtStart 5
        ObjectTemplate.TimeToLive 90
        ObjectTemplate.Distance 150
        ObjectTemplate.MaxNrOfObjectSpawned 2
        """;

    private static VehicleSpawnDef Parse()
        => Assert.Single(GameplayObjects.ParseVehicleSpawns(Spawns.Split('\n'), Templates.Split('\n')));

    [Fact]
    public void ReadsEveryTemplateFieldTheDialogShows()
    {
        var v = Parse();
        Assert.Equal("kubelwagen", v.Vehicle1);
        Assert.Equal("willy", v.Vehicle2);
        Assert.Equal(15, v.MinSpawnDelay);
        Assert.Equal(45, v.MaxSpawnDelay);
        Assert.Equal(5, v.SpawnDelayAtStart);
        Assert.Equal(90, v.TimeToLive);
        Assert.Equal(150, v.Distance);
        Assert.Equal(2, v.MaxNrOfObjectSpawned);
    }

    /// <summary>A field the level never declared keeps its default and must NOT appear after a save.</summary>
    [Fact]
    public void FieldsTheLevelNeverDeclaredAreNotInvented()
    {
        var v = Parse();
        Assert.Equal(10, v.DamageWhenLost);        // the default - this template has no DamageWhenLost line
        var outp = GameplayWriter.PatchVehicleSpawnTemplates(Templates.Split('\n'), new[] { v });
        Assert.DoesNotContain("DamageWhenLost", outp, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditedValuesAreWrittenBackAndReadTheSame()
    {
        var v = Parse() with { MinSpawnDelay = 7, MaxSpawnDelay = 8, TimeToLive = 30, Distance = 42, Vehicle2 = "sherman" };
        var outp = GameplayWriter.PatchVehicleSpawnTemplates(Templates.Split('\n'), new[] { v });
        var again = Assert.Single(GameplayObjects.ParseVehicleSpawns(Spawns.Split('\n'), outp.Split('\n')));
        Assert.Equal(7, again.MinSpawnDelay);
        Assert.Equal(8, again.MaxSpawnDelay);
        Assert.Equal(30, again.TimeToLive);
        Assert.Equal(42, again.Distance);
        Assert.Equal("sherman", again.Vehicle2);
        Assert.Equal("kubelwagen", again.Vehicle1);   // untouched
    }

    /// <summary>Lines belonging to other templates, and anything that is not a field we edit, survive verbatim.</summary>
    [Fact]
    public void OtherTemplatesAndUnknownLinesArePreserved()
    {
        const string two = Templates + """

            ObjectTemplate.create ObjectSpawner tankspawner
            ObjectTemplate.setObjectTemplate 1 tiger
            ObjectTemplate.MinSpawnDelay 99
            ObjectTemplate.someFutureField 3
            """;
        var v = Parse() with { MinSpawnDelay = 1 };
        var outp = GameplayWriter.PatchVehicleSpawnTemplates(two.Split('\n'), new[] { v });
        Assert.Contains("ObjectTemplate.MinSpawnDelay 99", outp);       // the other spawner is untouched
        Assert.Contains("ObjectTemplate.someFutureField 3", outp);      // unknown lines pass through
        Assert.Contains("ObjectTemplate.MinSpawnDelay 1", outp);        // ours is rewritten
    }

    /// <summary>BF1942 levels often carry a single SpawnDelay rather than the min/max pair. It reads as both, and
    /// keeps its own form on the way out rather than being converted.</summary>
    [Fact]
    public void SingleSpawnDelayFormIsPreserved()
    {
        const string single = """
            ObjectTemplate.create ObjectSpawner jeepspawner
            ObjectTemplate.setObjectTemplate 2 willy
            ObjectTemplate.SpawnDelay 25
            """;
        var v = Assert.Single(GameplayObjects.ParseVehicleSpawns(Spawns.Split('\n'), single.Split('\n')));
        Assert.Equal(25, v.MinSpawnDelay);
        Assert.Equal(25, v.MaxSpawnDelay);
        var outp = GameplayWriter.PatchVehicleSpawnTemplates(single.Split('\n'), new[] { v with { MinSpawnDelay = 12 } });
        Assert.Contains("ObjectTemplate.SpawnDelay 12", outp);
        Assert.DoesNotContain("MinSpawnDelay", outp);
    }
}
