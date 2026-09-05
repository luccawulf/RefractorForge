using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Turning the sky saved nothing on some maps. A level can ship no <c>SkyAndSun.con</c> of its own and still HAVE a
/// sky - Init.con runs one out of a layered archive - and echo's Saigon68 is exactly that: an archive of gameplay
/// .con files and a menu image, nothing else. The editor's sky settings ride the OVERRIDE path, which matches an
/// existing entry by name and does nothing at all when there is none, without a word. So the file has to be ADDED.
/// </summary>
public class SkyAndSunSaveTests
{
    private static string TempRfa(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "rf_sky_" + Guid.NewGuid().ToString("N")[..8] + ".rfa");
        RefractorFlatArchive.WriteFile(path,
            entries.Select(e => (e.Name, (byte[])Encoding.Latin1.GetBytes(e.Text))).ToList(),
            compress: false, xPackId: XPackId.Default);
        return path;
    }

    private static string? Read(string rfa, string endsWith)
    {
        var a = new RefractorFlatArchive(rfa);
        var e = a.Entries.FirstOrDefault(x => x.Name.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
        return e is null ? null : Encoding.Latin1.GetString(a.Read(e));
    }

    [Fact]
    public void An_override_matches_nothing_when_the_level_ships_no_SkyAndSun()
    {
        // The behaviour that hid the bug: this is why the write has to be an ADD, not an override.
        var rfa = TempRfa(("bfvietnam/levels/Saigon68/Init.con", "run Init/SkyAndSun\r\n"));
        try
        {
            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, null,
                extraFiles: new[] { ("SkyAndSun.con", Encoding.Latin1.GetBytes("sky.setRotAngle 90\r\n")) });
            Assert.Null(Read(rfa, "SkyAndSun.con"));
        }
        finally { File.Delete(rfa); }
    }

    [Fact]
    public void Adding_it_puts_the_file_under_the_levels_own_prefix()
    {
        var rfa = TempRfa(("bfvietnam/levels/Saigon68/Init.con", "run Init/SkyAndSun\r\n"));
        try
        {
            var env = EnvironmentSettings.Parse(
                new[] { "GeometryTemplate.create StandardMesh Sky_OI_m1", "Sky.initSky", "sky.setRotAngle -45" }, null, null);
            env.SkyRotationAngle = 90f;
            env.WriteSkyRotation = true;
            var body = string.Join("\r\n", env.PatchSkyAndSunConLines(
                new[] { "GeometryTemplate.create StandardMesh Sky_OI_m1", "Sky.initSky", "sky.setRotAngle -45" })) + "\r\n";

            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, null,
                newEntries: new[] { ("Init/SkyAndSun.con", Encoding.Latin1.GetBytes(body)) });

            var written = Read(rfa, "SkyAndSun.con");
            Assert.NotNull(written);
            Assert.Contains("sky.setRotAngle 90", written);
            Assert.Contains("Sky_OI_m1", written);                 // the level's own skybox survived
            Assert.DoesNotContain("sky.setRotAngle -45", written);

            var name = new RefractorFlatArchive(rfa).Entries
                .First(e => e.Name.EndsWith("SkyAndSun.con", StringComparison.OrdinalIgnoreCase)).Name
                .Replace('\\', '/');
            Assert.Contains("levels/Saigon68/Init/SkyAndSun.con", name, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(rfa); }
    }

    [Fact]
    public void A_level_that_HAS_the_file_is_rewritten_in_place_not_duplicated()
    {
        var rfa = TempRfa(
            ("bfvietnam/levels/Hue/Init.con", "run Init/SkyAndSun\r\n"),
            ("bfvietnam/levels/Hue/Init/SkyAndSun.con", "Sky.initSky\r\nsky.setRotAngle -45\r\n"));
        try
        {
            LevelSaver.RepackToRfa(rfa, rfa, null, null, null, null,
                extraFiles: new[] { ("SkyAndSun.con", Encoding.Latin1.GetBytes("Sky.initSky\r\nsky.setRotAngle 12\r\n")) });

            var a = new RefractorFlatArchive(rfa);
            Assert.Single(a.Entries.Where(e => e.Name.EndsWith("SkyAndSun.con", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("sky.setRotAngle 12", Read(rfa, "SkyAndSun.con"));
        }
        finally { File.Delete(rfa); }
    }
}

/// <summary>
/// How a level changes what its water reflects. NOT by shipping a cube map of its own - that crashes the map on
/// its first drawn frame however correctly the .rcm is written, because the engine reads the .rcm from the base
/// archive and never looks inside a level. It works by shipping the level's OWN copies of the six faces that the
/// stock cube map already names, which DO resolve level-first through the ordinary texture resolver.
/// Operation_Flaming_Dart does exactly this: six Texture/env_default_0N.dds inside the level, no .rcm, and its
/// levelWater.rs still says cubemap "texture/env_default.rcm".
/// </summary>
public class WaterCubeMapTests
{
    [Fact]
    public void The_water_keeps_naming_the_stock_cube_map()
    {
        // The shader must NOT be repointed - that is what broke it. Only the faces change.
        var text = WaterShader.Patch(null, WaterShaderSettings.RetailDefault, null);
        Assert.Contains("cubemap \"texture/env_default.rcm\"", text);
    }

    [Fact]
    public void The_faces_a_level_overrides_are_the_stock_names()
    {
        Assert.Equal("env_default", CubeMapFile.StockFaceBase);
        Assert.Equal("Texture/env_default_01.dds", CubeMapFile.StockFaceRelPath(1));
        Assert.Equal("Texture/env_default_06.dds", CubeMapFile.StockFaceRelPath(6));
    }

    [Fact]
    public void A_level_on_the_stock_sky_needs_no_override_at_all()
    {
        Assert.True(CubeMapFile.IsStockSky("env_default"));
        Assert.True(CubeMapFile.IsStockSky("default_env"));
        Assert.True(CubeMapFile.IsStockSky(null));
        Assert.True(CubeMapFile.IsStockSky("  "));
        Assert.False(CubeMapFile.IsStockSky("Sky_OI"));
        Assert.False(CubeMapFile.IsStockSky("Sky_HCMT2"));
    }

    [Fact]
    public void The_face_order_is_still_the_engines_own()
    {
        // Kept because the numbering is what makes an override land on the right face: the stock .rcm maps
        // _01=+Z, _02=+X, _03=-Z, _04=-X, _05=+Y up, _06=-Y down, and a skybox numbers its faces the same way -
        // so <sky>_0N copies straight onto env_default_0N.
        var text = CubeMapFile.Text("env_default");
        Assert.Contains(@"PositiveZ = texture\env_default_01.dds", text);
        Assert.Contains(@"PositiveX = texture\env_default_02.dds", text);
        Assert.Contains(@"NegativeY = texture\env_default_06.dds", text);
    }
}

/// <summary>Switching a map's skybox writes the mesh name into the SkyBox declaration's own
/// <c>GeometryTemplate.file</c> - and only that one, since a SkyAndSun.con can declare a cloud mesh too.</summary>
public class SkyBoxSwitchTests
{
    private static string[] Sky(params string[] extra) => new[]
    {
        "TextureManager.mipmaps 0",
        "GeometryTemplate.create StandardMesh SkyBox",
        "GeometryTemplate.file Sky_HCMT2_m1",
        "Sky.initSky",
    }.Concat(extra).ToArray();

    [Fact]
    public void Nothing_changes_until_a_sky_is_chosen()
    {
        var e = EnvironmentSettings.Parse(Sky(), null, null);
        e.SkyBoxMesh = "Sky_Stalingrad_M1";                      // set, but not marked for writing
        Assert.Contains(e.PatchSkyAndSunConLines(Sky()), l => l.Trim() == "GeometryTemplate.file Sky_HCMT2_m1");
    }

    [Fact]
    public void Choosing_one_rewrites_the_skybox_mesh()
    {
        var e = EnvironmentSettings.Parse(Sky(), null, null);
        Assert.Equal("Sky_HCMT2_m1", e.SkyBoxMesh);
        e.SkyBoxMesh = "Sky_Stalingrad_M1";
        e.WriteSkyBoxMesh = true;

        var outLines = e.PatchSkyAndSunConLines(Sky()).ToList();
        Assert.Contains(outLines, l => l.Trim() == "GeometryTemplate.file Sky_Stalingrad_M1");
        Assert.DoesNotContain(outLines, l => l.Trim() == "GeometryTemplate.file Sky_HCMT2_m1");
        Assert.Contains(outLines, l => l.Trim() == "Sky.initSky");
        Assert.Equal("Sky_Stalingrad_M1", EnvironmentSettings.Parse(outLines.ToArray(), null, null).SkyBoxMesh);
    }

    [Fact]
    public void A_second_mesh_in_the_same_file_is_left_alone()
    {
        // The cloud system declares its own GeometryTemplate; only the SkyBox one is the skybox.
        var withCloud = Sky("GeometryTemplate.create StandardMesh CloudLayer", "GeometryTemplate.file cloud");
        var e = EnvironmentSettings.Parse(withCloud, null, null);
        e.SkyBoxMesh = "Sky_Stalingrad_M1";
        e.WriteSkyBoxMesh = true;

        var outLines = e.PatchSkyAndSunConLines(withCloud).ToList();
        Assert.Contains(outLines, l => l.Trim() == "GeometryTemplate.file Sky_Stalingrad_M1");
        Assert.Single(outLines.Where(l => l.TrimStart().StartsWith("GeometryTemplate.file Sky_", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>The surface body's own depth scales were parsed and discarded, so the editor could not show the
/// level's real colour ramp - which is how an orange deepColor stayed invisible in the editor while the game
/// showed an orange river. Saigon68 carries waterColorDepth 7.5 and deepColor 0.498/0.298/0.008.</summary>
public class SurfaceWaterDepthTests
{
    private static string[] Init(params string[] extra) => new[] { "renderer.diffuseColor 1/1/1" }.Concat(extra).ToArray();

    [Fact]
    public void The_surface_depth_scales_are_kept()
    {
        var e = EnvironmentSettings.Parse(null, null, Init(
            "water.waterColorDepth 7.5", "water.waterAlphaDepth 0.4"));
        Assert.Equal(7.5f, e.ColorDepth, 3);
        Assert.Equal(0.4f, e.AlphaDepth, 3);
    }

    [Fact]
    public void A_level_that_names_neither_gets_the_retail_default()
    {
        var e = EnvironmentSettings.Parse(null, null, Init());
        Assert.Equal(20f, e.ColorDepth, 3);
        Assert.Equal(20f, e.AlphaDepth, 3);
    }

    [Fact]
    public void The_deep_colour_is_read_and_kept()
    {
        // It is real and it is visible in game; treating it as decorative is what let an orange river through.
        var e = EnvironmentSettings.Parse(null, null, Init("water.deepColor 0.498039/0.298039/0.007843"));
        Assert.Equal(0.498039f, e.DeepColor.X, 4);
        Assert.Equal(0.007843f, e.DeepColor.Z, 4);
    }
}

/// <summary>
/// game.setActiveCombatArea is two OFFSETS and one SIZE, not a free rectangle. The MDT is explicit: "the last two
/// numbers are the X and Z scales (which are always the same, since all maps are square)" and it OVERRIDES
/// GeometryTemplate.WorldSize - so a non-square pair scales the overhead map differently in X and Z and the minimap
/// comes out wrong. All 71 retail levels that declare one keep it square; DC's Al Nas is "380 0 416 416".
/// </summary>
/// <summary>
/// The combat area is not just a fence: <c>game.setActiveCombatArea</c>'s last two numbers REPLACE worldSize, and
/// the engine stretches <c>ingamemap.dds</c> across that rectangle. Proven against DC's Al Nas, whose shipped map
/// image lines up river-for-river with a render windowed to its 380 0 416 416 area and not at all with a
/// whole-world render. So the generated map image has to be windowed the same way.
/// </summary>
public class CombatAreaMapScalingTests
{
    [Fact]
    public void The_retail_shape_round_trips()
    {
        Assert.True(RefractorForge.Formats.Validation.CombatArea.TryParse("game.setActiveCombatArea 380 0 416 416", out var a));
        Assert.Equal(380f, a.X, 3);
        Assert.Equal(0f, a.Z, 3);
        Assert.Equal(416f, a.Width, 3);
        Assert.Equal(416f, a.Height, 3);
        Assert.Equal("game.setActiveCombatArea 380 0 416 416", a.ToConLine());
    }

    [Fact]
    public void A_non_square_area_survives_a_save_unchanged()
    {
        // The engine accepts an uneven pair - Fy_Pool_Day ships "98 856 60 68" - so the editor must not quietly
        // square it. It warns instead; silently rewriting an author's numbers is worse than an odd-looking map.
        var lines = new[] { "renderer.diffuseColor 1/1/1", "game.setActiveCombatArea 98 856 60 68" };
        var e = EnvironmentSettings.Parse(null, null, lines);
        var line = e.PatchInitConLines(lines).Single(l => l.StartsWith("game.setActiveCombatArea", StringComparison.OrdinalIgnoreCase));
        var n = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("60", n[3]);
        Assert.Equal("68", n[4]);
    }

    // A landmark you can actually see in the output. Height alone is no good - the minimap hill-shades from the
    // SLOPE, so a plateau renders identically to a plain - so the marker is the material index instead: the
    // +X/+Z quadrant is palette 7 (red) and the rest palette 4 (blue).
    private static (Heightmap Hm, TerrainConfig Cfg, MaterialMap Mat) Landmark()
    {
        var cfg = new TerrainConfig { WorldSize = 1024, MaterialSize = 64, YScale = 1f, WaterLevel = -1000f };
        var hm = new Heightmap(64, 64);
        var mat = new MaterialMap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                mat[x, y] = (byte)(x >= 32 && y >= 32 ? 7 : 4);
        return (hm, cfg, mat);
    }

    [Fact]
    public void A_null_area_still_renders_the_whole_world()
    {
        var (hm, cfg, mat) = Landmark();
        var whole = Minimap.Render(64, hm, cfg, null, mat, flipNorthUp: true, area: null);
        var explicitWhole = Minimap.Render(64, hm, cfg, null, mat, flipNorthUp: true,
                                area: RefractorForge.Formats.Validation.CombatArea.Whole(cfg.WorldSize));
        Assert.Equal(explicitWhole.Rgba, whole.Rgba);
    }

    [Fact]
    public void Windowing_to_a_quadrant_fills_the_image_with_that_quadrant()
    {
        var (hm, cfg, mat) = Landmark();
        // The red quadrant is world X 512..1024, Z 512..1024. Ask for exactly it and the image should be red all
        // over; ask for the opposite quadrant and it should be blue all over. That is the whole claim: these four
        // numbers pick which piece of the world the image covers.
        var red = Minimap.Render(32, hm, cfg, null, mat, flipNorthUp: true,
                      area: new RefractorForge.Formats.Validation.CombatArea(512f, 512f, 512f, 512f));
        var blue = Minimap.Render(32, hm, cfg, null, mat, flipNorthUp: true,
                       area: new RefractorForge.Formats.Validation.CombatArea(0f, 0f, 512f, 512f));
        Assert.True(MeanR(red) > MeanB(red), $"the +X/+Z window should be red (R {MeanR(red):0}, B {MeanB(red):0})");
        Assert.True(MeanB(blue) > MeanR(blue), $"the origin window should be blue (R {MeanR(blue):0}, B {MeanB(blue):0})");

        // and a whole-world render is a mix of both, which is exactly the bug: it was being written as the
        // in-game map on levels whose combat area is only part of the terrain.
        var whole = Minimap.Render(32, hm, cfg, null, mat, flipNorthUp: true, area: null);
        Assert.NotEqual(whole.Rgba, red.Rgba);
        Assert.NotEqual(whole.Rgba, blue.Rgba);
    }

    [Fact]
    public void An_area_reaching_outside_the_terrain_is_clamped_not_wrapped()
    {
        // Faid_Pass ships a negative offset (-65). SampleUv wraps on its own, which would fold the red quadrant at
        // the far corner into a window that sits entirely in the blue one, so Minimap has to clamp before sampling.
        var (hm, cfg, mat) = Landmark();
        var t = Minimap.Render(32, hm, cfg, null, mat, flipNorthUp: true,
                    area: new RefractorForge.Formats.Validation.CombatArea(-256f, -256f, 256f, 256f));
        Assert.True(MeanB(t) > MeanR(t), $"a clamped window belongs in the blue quadrant (R {MeanR(t):0}, B {MeanB(t):0})");
    }

    private static float MeanR(Texture2D t) => Channel(t, 0);
    private static float MeanB(Texture2D t) => Channel(t, 2);

    private static float Channel(Texture2D t, int off)
    {
        double sum = 0;
        for (int i = off; i < t.Rgba.Length; i += 4) sum += t.Rgba[i];
        return (float)(sum / (t.Rgba.Length / 4));
    }
}
