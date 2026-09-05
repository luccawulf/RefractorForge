using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
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

/// <summary>The water mirrors whatever <c>.rcm</c> its shader names, and the two the game ships name the same
/// generic sky - so a map with its own skybox reflected clouds that were nowhere in it.</summary>
public class WaterCubeMapTests
{
    [Fact]
    public void The_faces_are_in_the_engines_own_order()
    {
        // Matched against both shipped .rcm files AND the editor's cube-map upload, which maps
        // GL +X,-X,+Y,-Y,+Z,-Z from faces _02,_04,_05,_06,_01,_03.
        var text = CubeMapFile.Text("Sky_OI");
        Assert.Contains("PositiveX = texture\\Sky_OI_02.dds", text);
        Assert.Contains("NegativeX = texture\\Sky_OI_04.dds", text);
        Assert.Contains("PositiveY = texture\\Sky_OI_05.dds", text);
        Assert.Contains("NegativeY = texture\\Sky_OI_06.dds", text);
        Assert.Contains("PositiveZ = texture\\Sky_OI_01.dds", text);
        Assert.Contains("NegativeZ = texture\\Sky_OI_03.dds", text);
        Assert.StartsWith("[CubeMap]", text);
    }

    [Fact]
    public void The_stock_sky_needs_no_level_copy()
    {
        Assert.True(CubeMapFile.IsStockSky("env_default"));
        Assert.True(CubeMapFile.IsStockSky("default_env"));
        Assert.True(CubeMapFile.IsStockSky(null));
        Assert.True(CubeMapFile.IsStockSky("  "));
        Assert.False(CubeMapFile.IsStockSky("Sky_OI"));
        Assert.False(CubeMapFile.IsStockSky("Sky_Bocage"));
    }

    [Fact]
    public void The_reference_and_the_path_agree()
    {
        Assert.Equal("texture/Sky_OI_env.rcm", CubeMapFile.RefFor("Sky_OI"));
        Assert.Equal("Texture/Sky_OI_env.rcm", CubeMapFile.RelPathFor("Sky_OI"));
    }

    [Fact]
    public void A_mesh_shaped_name_is_reduced_to_a_filename()
    {
        Assert.Equal("texture/Sky_OI_env.rcm", CubeMapFile.RefFor("some/path/Sky_OI"));
        Assert.Contains("texture\\SkyOI_01.dds", CubeMapFile.Text("Sky OI"));    // a space is dropped, not turned into _
    }

    [Fact]
    public void The_water_shader_can_be_pointed_at_it()
    {
        var s = WaterShaderSettings.RetailDefault with { Cubemap = CubeMapFile.RefFor("Sky_OI") };
        var text = WaterShader.Patch(null, s, null);
        Assert.Contains("cubemap \"texture/Sky_OI_env.rcm\"", text);
        Assert.DoesNotContain("env_default.rcm", text);
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
