using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Saigon68 came back from the game with its Huey dying out of bounds over its own map, and an in-game map whose
/// icons sat nowhere near the art. Both trace to one combat area the editor had written into a level that retail
/// ships without one - and which the editor then had no way to take back out. These cover the way out, plus the two
/// save bugs found alongside it.
/// </summary>
public class CombatAreaRemovalTests
{
    private static string[] Init(params string[] extra)
    {
        var head = new[] { "renderer.diffuseColor 1/1/1", "game.setActiveCombatArea 323.699 45.466 616.299 430.962", "Game.ViewDistance 250" };
        return [.. head, .. extra];
    }

    [Fact]
    public void A_null_area_alone_leaves_the_level_line_untouched()
    {
        // Null means "the editor is not writing one", NOT "delete it". Every level loaded without touching the
        // combat area has a null here at some point, and a save must not quietly strip the level's own setting.
        var e = EnvironmentSettings.Parse(null, null, Init());
        e.CombatArea = null;
        Assert.Contains(e.PatchInitConLines(Init()), l => l.Contains("setActiveCombatArea"));
    }

    [Fact]
    public void Removing_deletes_the_line_entirely()
    {
        var e = EnvironmentSettings.Parse(null, null, Init());
        e.CombatArea = null;
        e.RemoveCombatArea = true;
        var outLines = e.PatchInitConLines(Init());
        Assert.DoesNotContain(outLines, l => l.Contains("setActiveCombatArea"));
        Assert.Contains("Game.ViewDistance 250", outLines);          // and nothing else goes with it
        Assert.Contains("renderer.diffuseColor 1/1/1", outLines);
    }

    [Fact]
    public void Removing_is_idempotent_and_safe_on_a_level_that_never_had_one()
    {
        var bare = new[] { "renderer.diffuseColor 1/1/1", "Game.ViewDistance 250" };
        var e = EnvironmentSettings.Parse(null, null, bare);
        e.RemoveCombatArea = true;
        Assert.Equal(bare, e.PatchInitConLines(bare));
        Assert.Equal(bare, e.PatchInitConLines(e.PatchInitConLines(bare)));
    }

    [Fact]
    public void Removing_then_declaring_again_writes_the_new_one()
    {
        var e = EnvironmentSettings.Parse(null, null, Init());
        e.CombatArea = null; e.RemoveCombatArea = true;
        e.CombatArea = new RefractorForge.Formats.Validation.CombatArea(0, 0, 1024, 1024);
        e.RemoveCombatArea = false;                                  // what "Declare one" does
        Assert.Contains("game.setActiveCombatArea 0 0 1024 1024", e.PatchInitConLines(Init()));
    }

    // ---- the blank-line creep ---------------------------------------------------------------------------------
    // Saigon68's Init.con had grown eleven trailing blank lines, two per save, because splitting text that ends in
    // a newline yields a phantom empty element which the patcher passed through as a line - and then a newline was
    // appended on top. This is the Viewer's round trip, reproduced.
    private static string Roundtrip(EnvironmentSettings e, string text)
    {
        var norm = text.Replace("\r\n", "\n");
        bool trailingNewline = norm.EndsWith("\n", StringComparison.Ordinal);
        var lines = norm.Split('\n');
        if (trailingNewline && lines.Length > 0) System.Array.Resize(ref lines, lines.Length - 1);
        return string.Join("\r\n", e.PatchInitConLines(lines)) + (trailingNewline ? "\r\n" : "");
    }

    [Fact]
    public void Saving_an_untouched_init_con_repeatedly_does_not_grow_it()
    {
        const string text = "renderer.diffuseColor 1/1/1\r\ngame.setActiveCombatArea 0 0 1024 1024\r\nGame.ViewDistance 250\r\n";
        var e = EnvironmentSettings.Parse(null, null, text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
        var once = Roundtrip(e, text);
        var twice = Roundtrip(e, once);
        var thrice = Roundtrip(e, twice);
        Assert.Equal(once, twice);
        Assert.Equal(once, thrice);
        Assert.EndsWith("Game.ViewDistance 250\r\n", once);
    }

    [Fact]
    public void Trailing_blank_lines_a_level_already_has_are_preserved_not_multiplied()
    {
        // The fix must not go the other way and start trimming a mapper's file either.
        const string text = "renderer.diffuseColor 1/1/1\r\nGame.ViewDistance 250\r\n\r\n\r\n";
        var e = EnvironmentSettings.Parse(null, null, new[] { "renderer.diffuseColor 1/1/1" });
        var once = Roundtrip(e, text);
        Assert.Equal(text, once);
        Assert.Equal(once, Roundtrip(e, once));
    }

    [Fact]
    public void A_file_with_no_trailing_newline_does_not_gain_one()
    {
        const string text = "renderer.diffuseColor 1/1/1\r\nGame.ViewDistance 250";
        var e = EnvironmentSettings.Parse(null, null, new[] { "renderer.diffuseColor 1/1/1" });
        Assert.Equal(text, Roundtrip(e, text));
    }
}
