using RefractorForge.Formats.Con;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Battlecraft's "Show All / CQ / CTF / TDM" dropdown (guide figure 21). A level keeps a separate set of gameplay
/// .con files per mode, and an object exists in a mode only if that mode's files name it.
///
/// The rule that matters most here is the FAIL-OPEN one: where membership cannot be determined, everything stays
/// visible. A filter that wrongly hides a flag would have a mapper hunting for an object that is really there.
/// </summary>
public class GameplayModesTests
{
    private static string Cp(params string[] names)
        => string.Join("\n", names.Select(n => $"Object.create {n}\nObject.absolutePosition 1/2/3"));

    private static GameplayModes Build() => GameplayModes.Scan(new[]
    {
        ("Conquest", (IEnumerable<string>?)Cp("cpAirfield", "cpVillage").Split('\n'),
                     (IEnumerable<string>?)Cp("jeepSpawner", "tankSpawner").Split('\n'),
                     (IEnumerable<string>?)Cp("spawn1").Split('\n')),
        ("Ctf",      (IEnumerable<string>?)Cp("cpAirfield").Split('\n'),
                     (IEnumerable<string>?)Cp("jeepSpawner").Split('\n'),
                     (IEnumerable<string>?)Cp("spawn1", "spawn2").Split('\n')),
    });

    [Fact]
    public void ListsTheModesItFound()
    {
        var m = Build();
        Assert.Equal(new[] { "Conquest", "Ctf" }, m.Modes);
    }

    [Fact]
    public void MembershipIsPerModeAndPerKind()
    {
        var m = Build();
        Assert.True(m.InMode(GameplayModes.Kind.ControlPoint, "cpVillage", "Conquest"));
        Assert.False(m.InMode(GameplayModes.Kind.ControlPoint, "cpVillage", "Ctf"));      // CTF has no village flag
        Assert.True(m.InMode(GameplayModes.Kind.Vehicle, "tankSpawner", "Conquest"));
        Assert.False(m.InMode(GameplayModes.Kind.Vehicle, "tankSpawner", "Ctf"));
        Assert.True(m.InMode(GameplayModes.Kind.Soldier, "spawn2", "Ctf"));
        Assert.False(m.InMode(GameplayModes.Kind.Soldier, "spawn2", "Conquest"));
    }

    [Fact]
    public void NamesMatchCaseInsensitivelyLikeTheConFiles()
        => Assert.True(Build().InMode(GameplayModes.Kind.ControlPoint, "CPAIRFIELD", "conquest"));

    /// <summary>A kind a mode never declared, an unknown mode, and a level with no per-mode files must all show
    /// everything rather than hide it.</summary>
    [Fact]
    public void FailsOpenWhenMembershipIsUnknown()
    {
        var m = Build();
        Assert.True(m.InMode(GameplayModes.Kind.ControlPoint, "cpVillage", "SinglePlayer"));   // mode not scanned
        Assert.True(m.InMode(GameplayModes.Kind.ControlPoint, "cpVillage", ""));               // "Show All"
        Assert.True(GameplayModes.Empty.InMode(GameplayModes.Kind.Vehicle, "anything", "Ctf"));
    }

    /// <summary>A mode whose folder carries none of the three files is not a game mode at all.</summary>
    [Fact]
    public void FoldersWithoutGameplayFilesAreNotModes()
    {
        var m = GameplayModes.Scan(new (string, IEnumerable<string>?, IEnumerable<string>?, IEnumerable<string>?)[]
        {
            ("Conquest", Cp("cp1").Split((char)10), null, null),
            ("Textures", null, null, null),
        });
        Assert.Equal(new[] { "Conquest" }, m.Modes);
    }

    [Fact]
    public void ModesOfListsEveryModeAnObjectAppearsIn()
    {
        var m = Build();
        Assert.Equal(new[] { "Conquest", "Ctf" }, m.ModesOf(GameplayModes.Kind.ControlPoint, "cpAirfield"));
        Assert.Equal(new[] { "Conquest" }, m.ModesOf(GameplayModes.Kind.ControlPoint, "cpVillage"));
    }
}
