using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// An underground map is bound to ONE template by name - <c>mapManager.addObjectMap o_sewers_A_M1 SewersAMap ...</c> -
/// and the map only appears while the player is inside an object of that template.
///
/// al_vietnas (2026-09-11): the lightmap-ready tool pointed the sewer's placement at a level-local copy,
/// <c>o_sewers_a_lm_m1</c> (its lights child had been patched), and the map line went on naming the original, which
/// nothing in the level placed any more. The binding now follows the placements, and it is written after the level's
/// own <c>run objects/...</c>, because a copy is a LEVEL template that does not exist before that line runs.
/// </summary>
public class UndergroundMapBindingTests
{
    private static readonly LightmapReady.Manifest Manifest = LightmapReady.ReadManifest(
        "rem rf-lightmap-ready placed o_sewers_a_m1 o_sewers_a_lm_m1\r\n" +
        "rem rf-lightmap-ready copy O_sewer_lights_m1 O_sewer_lights_lm_m1\r\n");

    private static readonly EnvironmentSettings.ObjectMap Sewers = new("o_sewers_A_M1", "SewersAMap", 442.97f, 98.995f, 362f, 362f);

    private static HashSet<string> Placed(params string[] t) => new(t, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_map_follows_its_tunnel_to_the_lightmap_ready_copy()
    {
        var maps = LightmapReady.RebindObjectMaps(new[] { Sewers }, Placed("o_sewers_a_lm_m1", "o_sewers_stairs_m1"), Manifest, out bool changed);
        Assert.True(changed);
        var m = Assert.Single(maps);
        Assert.Equal("o_sewers_a_lm_m1", m.Template);
        Assert.Equal(("SewersAMap", 442.97f, 98.995f, 362f, 362f), (m.MapName, m.X, m.Z, m.Width, m.Height));   // only the name moves
    }

    [Fact]
    public void Pointing_the_copies_back_brings_the_map_back_to_the_original()
    {
        var onCopy = Sewers with { Template = "o_sewers_a_lm_m1" };
        var maps = LightmapReady.RebindObjectMaps(new[] { onCopy }, Placed("o_sewers_A_M1"), Manifest, out bool changed);
        Assert.True(changed);
        Assert.Equal("o_sewers_a_m1", Assert.Single(maps).Template, ignoreCase: true);
    }

    [Fact]
    public void A_map_on_a_placed_template_or_one_no_copy_explains_is_left_alone()
    {
        var cedar = new EnvironmentSettings.ObjectMap("o_tunnelsA", "TunnelsAMap", 886, 871, 328, 327);
        var maps = LightmapReady.RebindObjectMaps(new[] { Sewers, cedar }, Placed("o_sewers_A_M1"), Manifest, out bool changed);
        Assert.False(changed);
        Assert.Equal(new[] { Sewers, cedar }, maps);
    }

    [Fact]
    public void Binding_both_the_original_and_its_copy_keeps_one_map()
    {
        var onCopy = Sewers with { Template = "o_sewers_a_lm_m1", MapName = "Other" };
        var maps = LightmapReady.RebindObjectMaps(new[] { Sewers, onCopy }, Placed("o_sewers_a_lm_m1"), Manifest, out bool changed);
        Assert.True(changed);
        var m = Assert.Single(maps);
        Assert.Equal(("o_sewers_a_lm_m1", "SewersAMap"), (m.Template, m.MapName));
    }

    /// <summary>al_vietnas' Init.con, cut down: the map bound up in the tunnel block, the level's objects run near the
    /// end. Rewritten, the one map line lands after the objects run - where the copy exists - and nowhere else.</summary>
    [Fact]
    public void The_map_line_is_written_after_the_levels_own_objects()
    {
        string[] init =
        {
            "Game.isTunnelMap 1",
            "game.useBelowGroundCulling 1",
            "Game.entryPointRadius 5",
            "",
            "mapManager.addObjectMap o_sewers_A_M1 SewersAMap 0/0/1024/1024",
            "run Init/Terrain",
            "run growth/underGrowth",
            "run objects/Objects",
            "rem RefractorForge level-local objects",
        };
        var e = EnvironmentSettings.Parse(null, null, init);
        var maps = LightmapReady.RebindObjectMaps(e.ObjectMaps, Placed("o_sewers_a_lm_m1"), Manifest, out _);
        e.ObjectMaps.Clear();
        e.ObjectMaps.AddRange(maps.Select(m => m with { X = 442.97f, Z = 98.995f, Width = 362f, Height = 362f }));
        e.WriteTunnel = true;
        var outLines = e.PatchInitConLines(init);

        int objects = outLines.FindIndex(l => l == "run objects/Objects");
        Assert.Equal("mapManager.addObjectMap o_sewers_a_lm_m1 SewersAMap 442.97/98.995/362/362", outLines[objects + 1]);
        Assert.Single(outLines, l => l.StartsWith("mapManager.addObjectMap"));
        Assert.Equal(outLines, e.PatchInitConLines(outLines));        // idempotent: saving again moves nothing
        var again = EnvironmentSettings.Parse(null, null, outLines);
        Assert.Equal("o_sewers_a_lm_m1", Assert.Single(again.ObjectMaps).Template);
    }
}
