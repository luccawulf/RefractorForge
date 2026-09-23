using System.Text;
using RefractorBridge.Level;
using RefractorBridge.Oracle;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates on the BF1942 -> Battlefield Vietnam level porter (RefractorBridge M3).
///
/// Most of a level port is carrying bytes across unchanged - heightmap, material map, terrain texture grid and
/// palette are identical formats in both games. What has to change is narrow: the archive paths, the paths
/// written INSIDE the scripts, the dialect, and the files BFV requires that BF1942 has no equivalent for.
/// These tests build a miniature BF1942 level archive so they run without either game installed, and add a
/// real-archive pass when Battlefield 1942 is present.
/// </summary>
public class BridgeLevelPorterTests : IDisposable
{
    private readonly string _dir;

    public BridgeLevelPorterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rbridge_port_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] L(string s) => Encoding.Latin1.GetBytes(s.Replace("\n", "\r\n"));

    /// <summary>A miniature but structurally honest BF1942 level.</summary>
    private RefractorFlatArchive MakeSourceLevel()
    {
        const string p = "bf1942/levels/TestMap/";
        var entries = new List<(string, byte[])>
        {
            (p + "Init.con", L("""
                renderer.globalAmbientColor .15/.15/.15
                renderer.diffuseColor .3/.3/.25
                renderer.fogLinearStart 50
                Game.setViewDistance 350
                """)),
            (p + "Init/Terrain.con", L("""
                GeometryTemplate.create patchTerrain terrainGeometry
                GeometryTemplate.file bf1942\levels\TestMap\Heightmap
                GeometryTemplate.materialMap bf1942\levels\TestMap\Materialmap
                GeometryTemplate.lodDistance 350
                Object.setName track
                """)),
            (p + "objects.con", L("""
                GeometryTemplate.create StandardMesh good_mesh
                GeometryTemplate.file good_mesh
                ObjectTemplate.create SimpleObject good_prop
                ObjectTemplate.geometry good_mesh
                ObjectTemplate.create SimpleObject ghost_prop
                ObjectTemplate.geometry missing_mesh
                """)),
            (p + "StaticObjects.con", L("""
                Object.create good_prop
                Object.absolutePosition 1/2/3
                Object.create ghost_prop
                Object.absolutePosition 4/5/6
                Object.create not_declared_anywhere
                Object.absolutePosition 7/8/9
                """)),
            (p + "Heightmap.raw", new byte[128]),
            (p + "materialmap.raw", new byte[64]),
            (p + "Menu/thumbnail.dds", new byte[16]),
            (p + "Textures/tx00x00.dds", new byte[32]),
            (p + "standardmesh/good_mesh.sm", new byte[8]),
            // Everything BFV never reads.
            (p + "PreCache.con", L("rem precache")),
            (p + "cullRadius.con", L("rem cull")),
            (p + "WaterShader.rs", L("rem water")),
            (p + "StaticObjects.con.bak", L("rem old")),
            (p + "Textures/ENVMAP_G_.rcm", new byte[8]),
        };

        string path = Path.Combine(_dir, "TestMap.rfa");
        RefractorFlatArchive.WriteFile(path, entries, compress: true, XPackId.None);
        return new RefractorFlatArchive(path);
    }

    private static PortedFile? Find(LevelPortResult r, string suffix) =>
        r.Files.FirstOrDefault(f => f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    // --- layout --------------------------------------------------------------------------------------------

    [Fact]
    public void Every_entry_moves_into_the_vietnam_level_tree()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");

        Assert.Equal("TestMap", r.SourceLevel);
        Assert.All(r.Files, f => Assert.StartsWith("BfVietnam/levels/PortedMap/", f.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Files_battlefield_vietnam_never_reads_are_dropped()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");

        foreach (string gone in new[] { "PreCache.con", "cullRadius.con", "WaterShader.rs", ".bak", ".rcm" })
            Assert.DoesNotContain(r.Files, f => f.Name.EndsWith(gone, StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(r.DroppedEntries);
    }

    [Fact]
    public void Names_are_matched_to_retail_casing()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");

        Assert.Contains(r.Files, f => f.Name.EndsWith("/MaterialMap.raw", StringComparison.Ordinal));
        Assert.Contains(r.Files, f => f.Name.EndsWith("/Menu/Thumbnail.dds", StringComparison.Ordinal));
    }

    [Fact]
    public void Terrain_payloads_are_carried_across_byte_for_byte()
    {
        var source = MakeSourceLevel();
        var r = LevelPorter.Port(source, "PortedMap");

        var heightmap = Find(r, "/Heightmap.raw");
        Assert.NotNull(heightmap);
        Assert.Equal(PortOrigin.Copied, heightmap!.Origin);
        Assert.Equal(128, heightmap.Data.Length);
    }

    // --- scripts -------------------------------------------------------------------------------------------

    [Fact]
    public void Paths_written_inside_scripts_follow_the_level_to_its_new_game_and_name()
    {
        // The terrain script names its own heightmap and material map by absolute archive path. Miss these and
        // the level loads someone else's terrain, or none.
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");
        string terrain = Encoding.Latin1.GetString(Find(r, "Init/Terrain.con")!.Data);

        Assert.Contains(@"BfVietnam\levels\PortedMap\Heightmap", terrain, StringComparison.Ordinal);
        Assert.Contains(@"BfVietnam\levels\PortedMap\Materialmap", terrain, StringComparison.Ordinal);
        Assert.DoesNotContain("bf1942", terrain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TestMap", terrain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_dialect_is_applied_to_every_script()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");
        string terrain = Encoding.Latin1.GetString(Find(r, "Init/Terrain.con")!.Data);
        string init = Encoding.Latin1.GetString(Find(r, "Init.con")!.Data);

        Assert.Contains("Object.Name track", terrain, StringComparison.Ordinal);       // renamed
        Assert.DoesNotContain("GeometryTemplate.lodDistance", terrain, StringComparison.Ordinal);  // dead in both
        Assert.Contains("renderer.fogstart 50", init, StringComparison.Ordinal);       // renamed
        Assert.DoesNotContain("globalAmbientColor", init, StringComparison.Ordinal);   // BF1942-only
    }

    [Fact]
    public void The_terrain_gets_the_wave_height_every_retail_level_sets()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");
        Assert.Contains("GeometryTemplate.waveHeight 1.0",
            Encoding.Latin1.GetString(Find(r, "Init/Terrain.con")!.Data), StringComparison.Ordinal);
    }

    [Fact]
    public void Dropping_the_bf1942_ambient_command_is_compensated_in_the_diffuse_term()
    {
        // BF1942 added globalAmbientColor ON TOP of diffuse; BFV has no such command, so a level inheriting
        // BF1942's tuned-down diffuse renders at a fraction of its light. All 83 retail levels sit 0.85-1.0.
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");
        string init = Encoding.Latin1.GetString(Find(r, "Init.con")!.Data);

        Assert.Contains("renderer.diffuseColor .9/.9/.9", init, StringComparison.Ordinal);
        Assert.DoesNotContain(".3/.3/.25", init, StringComparison.Ordinal);
    }

    [Fact]
    public void Lighting_compensation_can_be_turned_off()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap", new PortOptions { CompensateLighting = false });
        Assert.Contains(".3/.3/.25", Encoding.Latin1.GetString(Find(r, "Init.con")!.Data), StringComparison.Ordinal);
    }

    // --- growth --------------------------------------------------------------------------------------------

    [Fact]
    public void The_growth_pair_is_generated_because_every_retail_level_runs_it()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");

        foreach (string n in new[] { "growth/overGrowth.wst", "growth/overGrowthMap.raw",
                                     "growth/underGrowth.wst", "growth/underGrowthMap.raw" })
            Assert.Contains(r.Files, f => f.Name.EndsWith(n, StringComparison.OrdinalIgnoreCase)
                                          && f.Origin == PortOrigin.Generated);

        Assert.Equal(GrowthStubs.OverGrowthSide * GrowthStubs.OverGrowthSide, Find(r, "overGrowthMap.raw")!.Data.Length);
        Assert.Equal(GrowthStubs.UnderGrowthSide * GrowthStubs.UnderGrowthSide, Find(r, "underGrowthMap.raw")!.Data.Length);

        string init = Encoding.Latin1.GetString(Find(r, "Init.con")!.Data);
        Assert.Contains("run growth/overGrowth", init, StringComparison.Ordinal);
        Assert.Contains("run growth/underGrowth", init, StringComparison.Ordinal);
    }

    [Fact]
    public void A_growth_palette_points_at_its_own_level()
    {
        // materialMapFilename is an ENGINE property holding the level's path. Left pointing at another level,
        // the game silently grows THAT level's layout on this map.
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");
        string wst = Encoding.Latin1.GetString(Find(r, "growth/overGrowth.wst")!.Data);

        Assert.Contains(@"BfVietnam\levels\PortedMap\growth\overGrowthMap", wst, StringComparison.Ordinal);
        Assert.Equal(16, GrowthStubs.MaterialSlots.Length);
        foreach (string slot in GrowthStubs.MaterialSlots)
            Assert.Contains($"<{slot}>", wst, StringComparison.Ordinal);
    }

    // --- statics and geometry closure ----------------------------------------------------------------------

    [Fact]
    public void A_placement_whose_template_nothing_declares_is_dropped()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");
        string statics = Encoding.Latin1.GetString(Find(r, "StaticObjects.con")!.Data);

        Assert.Contains("good_prop", statics, StringComparison.Ordinal);
        Assert.DoesNotContain("not_declared_anywhere", statics, StringComparison.Ordinal);
        Assert.Equal(3, r.Statics.Total);
        Assert.Contains(r.Statics.Unresolved, u => u.Template == "not_declared_anywhere");
    }

    [Fact]
    public void A_template_whose_mesh_never_ships_is_reported_and_its_placements_dropped()
    {
        // The silent killer: ObjectTemplate.geometry resolves against GeometryTemplate NAMES, so naming an
        // undeclared one parses perfectly and null-derefs on CONSTRUCT - a mode-specific crash with no
        // bad-file symptom anywhere.
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");

        Assert.Contains(r.BrokenGeometry, b => b.StartsWith("ghost_prop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ghost_prop",
            Encoding.Latin1.GetString(Find(r, "StaticObjects.con")!.Data), StringComparison.Ordinal);
    }

    [Fact]
    public void Statics_can_be_left_untouched_when_asked()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap", new PortOptions { DropUnresolvedStatics = false });
        Assert.Contains("not_declared_anywhere",
            Encoding.Latin1.GetString(Find(r, "StaticObjects.con")!.Data), StringComparison.Ordinal);
    }

    [Fact]
    public void Retail_templates_resolve_through_the_census()
    {
        var census = StockCensus.Build(new[]
        {
            new RefractorBridge.Con.ConFile("stock.con",
                L("ObjectTemplate.create SimpleObject not_declared_anywhere\n")),
        });

        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap", new PortOptions { Census = census });

        // Now that "retail" owns the name, the placement is kept rather than dropped.
        Assert.Contains("not_declared_anywhere",
            Encoding.Latin1.GetString(Find(r, "StaticObjects.con")!.Data), StringComparison.Ordinal);
    }

    // --- the whole archive ---------------------------------------------------------------------------------

    [Fact]
    public void The_ported_level_writes_and_reads_back_intact()
    {
        var r = LevelPorter.Port(MakeSourceLevel(), "PortedMap");
        string outPath = Path.Combine(_dir, "PortedMap.rfa");

        RefractorFlatArchive.WriteFile(outPath, r.Files.Select(f => (f.Name, f.Data)).ToList(),
            compress: true, XPackId.None);

        var written = new RefractorFlatArchive(outPath);
        Assert.Equal(r.Files.Count, written.Entries.Count);

        foreach (var f in r.Files)
        {
            var e = written.Entries.First(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(f.Data, written.Read(e));
        }
    }

    [Fact]
    public void A_real_desert_combat_level_ports_and_reads_back()
    {
        const string real = @"D:\Games\EA GAMES\Battlefield 1942\Mods\DC_Final\Archives\bf1942\levels\DC_Al_Nas.rfa";
        if (!File.Exists(real)) return;

        var r = LevelPorter.Port(new RefractorFlatArchive(real), "Al_Nas_Bridge");

        Assert.Equal("DC_Al_Nas", r.SourceLevel);
        Assert.True(r.Files.Count > 200, $"only {r.Files.Count} files came across");
        Assert.True(r.Statics.Total > 500, "DC_Al_Nas has hundreds of placements");
        Assert.All(r.Files, f => Assert.StartsWith("BfVietnam/levels/Al_Nas_Bridge/", f.Name, StringComparison.Ordinal));

        // No script may still name the old game or the old level.
        foreach (var f in r.Files.Where(f => f.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase)))
        {
            string text = Encoding.Latin1.GetString(f.Data);
            Assert.DoesNotContain(@"bf1942\levels\DC_Al_Nas", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bf1942/levels/DC_Al_Nas", text, StringComparison.OrdinalIgnoreCase);
        }

        string outPath = Path.Combine(_dir, "Al_Nas_Bridge.rfa");
        RefractorFlatArchive.WriteFile(outPath, r.Files.Select(f => (f.Name, f.Data)).ToList(),
            compress: true, XPackId.None);
        Assert.Equal(r.Files.Count, new RefractorFlatArchive(outPath).Entries.Count);
    }
}
