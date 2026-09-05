using System.Linq;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>The sky rotation the editor shows had been a viewport-only preview: the level kept whatever
/// <c>sky.setRotAngle</c> it shipped. All 83 retail levels with a sky declare one, so it is rewritten in place -
/// the same treatment the sun already got.</summary>
public class SkyRotationTests
{
    private static string[] Sky(params string[] extra) => new[]
    {
        "GeometryTemplate.create StandardMesh Sky_OI_m1",
        "Sky.initSky",
    }.Concat(extra).ToArray();

    [Fact]
    public void The_angle_is_read_from_the_level()
    {
        var e = EnvironmentSettings.Parse(Sky("sky.setRotAngle -45"), null, null);
        Assert.Equal(-45f, e.SkyRotationAngle, 3);
    }

    [Fact]
    public void Nothing_is_rewritten_until_the_editor_turns_the_sky()
    {
        var e = EnvironmentSettings.Parse(Sky("sky.setRotAngle -45"), null, null);
        e.SkyRotationAngle = 90f;                                  // changed, but not marked for writing
        Assert.Contains(e.PatchSkyAndSunConLines(Sky("sky.setRotAngle -45")), l => l.Trim() == "sky.setRotAngle -45");
    }

    [Fact]
    public void Turning_it_rewrites_the_line_in_place_and_keeps_the_rest_of_the_file()
    {
        var e = EnvironmentSettings.Parse(Sky("sky.setRotAngle -45"), null, null);
        e.SkyRotationAngle = 90f;
        e.WriteSkyRotation = true;

        var outLines = e.PatchSkyAndSunConLines(Sky("sky.setRotAngle -45", "sky.changeOfsSkyHeight 0"));
        Assert.Single(outLines.Where(l => l.TrimStart().StartsWith("sky.setRotAngle", System.StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(outLines, l => l.Trim() == "sky.setRotAngle 90");
        Assert.Contains(outLines, l => l.Trim() == "Sky.initSky");                    // untouched
        Assert.Contains(outLines, l => l.Trim() == "sky.changeOfsSkyHeight 0");
    }

    [Fact]
    public void A_level_that_never_declared_one_gets_the_line()
    {
        var e = EnvironmentSettings.Parse(Sky(), null, null);
        e.SkyRotationAngle = 30f;
        e.WriteSkyRotation = true;
        Assert.Contains(e.PatchSkyAndSunConLines(Sky()), l => l.Trim() == "sky.setRotAngle 30");
    }

    [Fact]
    public void It_round_trips()
    {
        var e = EnvironmentSettings.Parse(Sky("sky.setRotAngle -45"), null, null);
        e.SkyRotationAngle = 137.5f;
        e.WriteSkyRotation = true;
        var back = EnvironmentSettings.Parse(e.PatchSkyAndSunConLines(Sky("sky.setRotAngle -45")).ToArray(), null, null);
        Assert.Equal(137.5f, back.SkyRotationAngle, 2);
    }
}

/// <summary>Undoing a bake means writing "nothing is in shadow", not deleting the file: the level keeps the entry it
/// shipped and every save path already knows how to write one.</summary>
public class UnbakeTests
{
    [Fact]
    public void An_unshadowed_map_has_no_shadow_anywhere()
    {
        var lsb = TerrainShadow.UnshadowedLsb(gridDim: 2, tilePx: 64);
        var vis = lsb.ToVisibility(out int side);
        Assert.Equal(128, side);
        Assert.Equal(128 * 128, vis.Length);
        Assert.All(vis, b => Assert.Equal(0, b));
    }

    [Fact]
    public void It_round_trips_through_the_engines_own_encoding()
    {
        // The whole point is that the game reads it, so it has to survive encode/decode like any baked one.
        var lsb = TerrainShadow.UnshadowedLsb(gridDim: 2, tilePx: 64);
        var bytes = lsb.Encode();
        var back = LightmapShadowBits.Decode(bytes);
        Assert.Equal(lsb.GridDim, back.GridDim);
        Assert.All(back.ToVisibility(out _), b => Assert.Equal(0, b));
        Assert.Equal(bytes, back.Encode());
    }

    [Fact]
    public void It_differs_from_a_real_bake()
    {
        // Guards the polarity: if "unbaked" happened to encode the same as a baked map, the feature would be a no-op.
        var hm = new Heightmap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                hm.Samples[y * 64 + x] = (ushort)(x < 32 ? 0 : 40000);      // a tall wall to cast a shadow
        var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 1f, WaterLevel = 0 };
        var baked = TerrainShadow.BakeToLsb(hm, cfg, new RefractorForge.Formats.Geometry.Vec3(1f, 0.15f, 0f),
                                            gridDim: 1, tilePx: 64);
        Assert.Contains(baked.ToVisibility(out _), b => b != 0);
        Assert.All(TerrainShadow.UnshadowedLsb(1, 64).ToVisibility(out _), b => Assert.Equal(0, b));
    }
}
