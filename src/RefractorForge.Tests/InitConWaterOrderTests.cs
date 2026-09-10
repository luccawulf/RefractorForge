using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A <c>water.*</c> line must never be written above <c>run Init/Terrain</c>.
///
/// The terrain is what creates the water object, so a water setting placed before it is applied to nothing and
/// KILLS THE DEDICATED SERVER during load. This was measured, not reasoned: the echo mod's al_vietnas carried one
/// stray <c>water.color</c> at Init.con line 31 with the terrain run at line 97, and bfvietnam_w32ded.exe exited
/// 1.5 seconds in, before it logged GameStart. Moving that one line below the terrain run fixed it - and the value
/// was irrelevant, three different colours all crashed identically.
///
/// The editor WROTE that line. <see cref="EnvironmentSettings.PatchInitConLines"/> inserted every setting the file
/// did not already have straight after the last <c>renderer.*</c> line, which on that map is line 30. So every map
/// saved with water edits, whose Init.con had no water block yet, came out unbootable on a server.
///
/// Retail is unanimous about the order: Fall_of_Saigon runs the terrain at line 97 and sets water at 99-103.
/// </summary>
public class InitConWaterOrderTests
{
    /// <summary>al_vietnas's shape: a renderer block, then the run chain much further down.</summary>
    private static List<string> LevelInitCon() => new()
    {
        "rem **** Initialize level specific rendering settings.",
        "Game.setLoadMusicFilename \"music/music6\"",
        "textureManager.alternativePath Texture",
        "renderer.diffuseColor 0.901961/0.901961/0.92549",
        "renderer.ambientColor 0.121569/0.101961/0.078431",
        "renderer.globalAmbientColor 0.16/0.15/0.17",
        "game.setActiveCombatArea 327 45 616 433",
        "Game.ViewDistance 250",
        "",
        "run Init/SkyAndSun",
        "run Init/Terrain",
        "",
        "run Sounds/Environment",
        "run objects/Objects",
    };

    private static EnvironmentSettings WithWater()
    {
        var e = new EnvironmentSettings
        {
            WaterColor = new Vec3(0.25f, 0.05f, 0f),
            ShallowColor = new Vec3(0.25f, 0.11f, 0f),
            DeepColor = new Vec3(0.25f, 0.05f, 0f),
            WaterAlpha = 1f,
        };
        e.WriteWater = true;
        return e;
    }

    private static int IndexOfPrefix(List<string> lines, string prefix) =>
        lines.FindIndex(l => l.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The regression: water added to a level that had none lands BELOW the terrain run.</summary>
    [Fact]
    public void Water_added_to_a_level_that_had_none_goes_below_the_terrain_run()
    {
        var outLines = WithWater().PatchInitConLines(LevelInitCon());

        int terrain = IndexOfPrefix(outLines, "run Init/Terrain");
        Assert.True(terrain >= 0, "the terrain run must survive the patch");

        var water = outLines.Select((l, i) => (l, i))
                            .Where(x => x.l.TrimStart().StartsWith("water.", StringComparison.OrdinalIgnoreCase))
                            .ToList();
        Assert.NotEmpty(water);
        Assert.All(water, x => Assert.True(x.i > terrain,
            $"'{x.l.Trim()}' was written at line {x.i + 1}, ABOVE the terrain run at line {terrain + 1} - " +
            "that shape kills the dedicated server on load"));
    }

    /// <summary>Non-water settings still go with the renderer block, where they read naturally.</summary>
    [Fact]
    public void Other_settings_still_sit_with_the_renderer_block()
    {
        var e = new EnvironmentSettings { FogEnabled = true, FogColor = new Vec3(0.1f, 0.2f, 0.3f), FogStart = 50f, FogEnd = 200f };
        e.WriteFog = true;
        var outLines = e.PatchInitConLines(LevelInitCon());

        int lastRenderer = outLines.FindLastIndex(l => l.TrimStart().StartsWith("renderer.", StringComparison.OrdinalIgnoreCase));
        int terrain = IndexOfPrefix(outLines, "run Init/Terrain");
        int fog = IndexOfPrefix(outLines, "renderer.vertexFogEnable");
        Assert.True(fog >= 0 && fog <= lastRenderer && fog < terrain,
            "fog belongs in the renderer block, above the run chain");
    }

    /// <summary>A map already broken this way is REPAIRED on save, not preserved.</summary>
    [Fact]
    public void A_stray_water_line_above_the_terrain_run_is_moved_down()
    {
        var lines = LevelInitCon();
        lines.Insert(6, "water.color 0.25098/0.05098/0");     // exactly al_vietnas's defect
        Assert.True(IndexOfPrefix(lines, "water.color") < IndexOfPrefix(lines, "run Init/Terrain"),
            "the fixture must start out broken");

        var outLines = WithWater().PatchInitConLines(lines);

        int terrain = IndexOfPrefix(outLines, "run Init/Terrain");
        Assert.All(outLines.Select((l, i) => (l, i)).Where(x => x.l.TrimStart().StartsWith("water.", StringComparison.OrdinalIgnoreCase)),
                   x => Assert.True(x.i > terrain, $"'{x.l.Trim()}' is still above the terrain run"));
        // and it is not duplicated
        Assert.Single(outLines.Where(l => l.TrimStart().StartsWith("water.color", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Water keys the file already has, correctly placed, are rewritten in place - not moved or copied.</summary>
    [Fact]
    public void Correctly_placed_water_lines_are_edited_where_they_are()
    {
        var lines = LevelInitCon();
        int at = IndexOfPrefix(lines, "run Init/Terrain") + 1;
        lines.Insert(at, "water.color 0.9/0.9/0.9");

        var outLines = WithWater().PatchInitConLines(lines);

        var colours = outLines.Where(l => l.TrimStart().StartsWith("water.color", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(colours);
        Assert.DoesNotContain("0.9/0.9/0.9", colours[0]);      // rewritten with the edited value
        Assert.True(IndexOfPrefix(outLines, "water.color") > IndexOfPrefix(outLines, "run Init/Terrain"));
    }

    /// <summary>A level whose Init.con never loads terrain (a menu//test con) still gets valid output.</summary>
    [Fact]
    public void A_file_with_no_terrain_run_puts_water_at_the_end()
    {
        var lines = new List<string> { "renderer.ambientColor 0.1/0.1/0.1", "Game.ViewDistance 250" };
        var outLines = WithWater().PatchInitConLines(lines);
        Assert.Contains(outLines, l => l.TrimStart().StartsWith("water.color", StringComparison.OrdinalIgnoreCase));
        int water = IndexOfPrefix(outLines, "water.color");
        int renderer = outLines.FindLastIndex(l => l.TrimStart().StartsWith("renderer.", StringComparison.OrdinalIgnoreCase));
        Assert.True(water > renderer, "with no terrain run, the end of the file is the safe place");
    }

    /// <summary>The tunnel water block rides on the same rule - it is inserted relative to the water lines.</summary>
    [Fact]
    public void The_tunnel_water_block_also_lands_below_the_terrain_run()
    {
        var e = WithWater();
        e.WriteWaterBelow = true;
        e.WaterBelowEnabled = true;
        var outLines = e.PatchInitConLines(LevelInitCon());

        int terrain = IndexOfPrefix(outLines, "run Init/Terrain");
        var below = outLines.Select((l, i) => (l, i))
                            .Where(x => x.l.TrimStart().StartsWith("waterBelowTerrain.", StringComparison.OrdinalIgnoreCase))
                            .ToList();
        Assert.NotEmpty(below);
        Assert.All(below, x => Assert.True(x.i > terrain, $"'{x.l.Trim()}' is above the terrain run"));
    }
}
