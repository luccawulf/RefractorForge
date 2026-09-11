using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Game.ViewDistance - how far the game draws at all - edited beside the fog. All 83 retail BfVietnam levels write it
/// as <c>Game.ViewDistance</c>; the console also takes <c>setViewDistance</c>, and that is what the patcher used to
/// ADD. On al_vietnas, whose own <c>Game.ViewDistance 250</c> sits below the renderer block, the added line landed
/// above it and the level's old value, read last, won - the edit did nothing. The two spellings are one setting now.
/// </summary>
public class ViewDistanceTests
{
    private static readonly string[] AlVietnas =
    {
        "renderer.globalAmbientColor 0.16/0.15/0.17",
        "game.setActiveCombatArea 327.295 45.8 615.728 432.821",
        "",
        "Game.ViewDistance 250",
        "Game.isTunnelMap 1",
    };

    [Fact]
    public void The_levels_own_line_is_rewritten_in_place()
    {
        var e = EnvironmentSettings.Parse(null, null, AlVietnas);
        Assert.True(e.HasViewDistance);
        Assert.Equal(250f, e.ViewDistance);
        e.ViewDistance = 400f; e.WriteViewDistance = true;
        var outLines = e.PatchInitConLines(AlVietnas);
        Assert.Equal("Game.ViewDistance 400", outLines[3]);
        Assert.Single(outLines, l => l.Contains("ViewDistance", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(outLines, e.PatchInitConLines(outLines));                 // saving again changes nothing
        Assert.Equal(400f, EnvironmentSettings.Parse(null, null, outLines).ViewDistance);
    }

    [Fact]
    public void The_other_spelling_keeps_its_own_spelling()
    {
        string[] lines = { "renderer.fogend 300", "game.setViewDistance 300" };
        var e = EnvironmentSettings.Parse(null, null, lines);
        e.ViewDistance = 600f; e.WriteViewDistance = true;
        Assert.Equal(new[] { "renderer.fogend 300", "game.setViewDistance 600" }, e.PatchInitConLines(lines));
    }

    [Fact]
    public void A_level_without_one_gets_retails_line_once()
    {
        string[] lines = { "renderer.ambientColor 0.1/0.1/0.1", "run Init/Terrain" };
        var e = EnvironmentSettings.Parse(null, null, lines);
        Assert.False(e.HasViewDistance);
        e.ViewDistance = 350f; e.WriteViewDistance = true;
        var outLines = e.PatchInitConLines(lines);
        Assert.Equal(new[] { "renderer.ambientColor 0.1/0.1/0.1", "Game.ViewDistance 350", "run Init/Terrain" }, outLines);
        Assert.Equal(outLines, e.PatchInitConLines(outLines));
    }

    [Fact]
    public void Untouched_it_stays_exactly_as_it_was()
    {
        var e = EnvironmentSettings.Parse(null, null, AlVietnas);
        e.ViewDistance = 999f;                                                   // not asked to write it
        Assert.Equal(AlVietnas, e.PatchInitConLines(AlVietnas));
    }
}
