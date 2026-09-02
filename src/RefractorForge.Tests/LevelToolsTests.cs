using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Packaging;
using RefractorForge.Formats.Rfa;
using RefractorForge.Formats.Terrain;
using RefractorForge.Formats.Validation;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The headless layer behind the map-checking and authoring tools. Each check is for a mistake the engine
/// accepts silently, so these pin that each one is actually caught - and, as importantly, that a clean level
/// produces no false alarms, because a validator people learn to ignore is worse than none.
/// </summary>
public class LevelToolsTests
{
    // ---- shared fixtures ----

    private static (Heightmap, TerrainConfig) Flat(int side = 64, int world = 256, float metres = 20f)
    {
        var cfg = new TerrainConfig { MaterialSize = side, WorldSize = world, YScale = 1f };
        var hm = new Heightmap(side, side);
        ushort raw = cfg.MetersToRaw(metres);
        for (int i = 0; i < hm.Samples.Length; i++) hm.Samples[i] = raw;
        return (hm, cfg);
    }

    private static StaticObject Obj(string id, string tmpl, float x, float y, float z) =>
        new(tmpl) { Id = id, Position = new Vec3(x, y, z) };

    private static EditableGameplay Gameplay()
    {
        var gp = new EditableGameplay(GameplayObjects.Empty);
        gp.Add(GpKind.ControlPoint, new ControlPointDef("base_a", new Vec3(40, 20, 40), 25f, 1, Team: 1));
        gp.Add(GpKind.ControlPoint, new ControlPointDef("base_b", new Vec3(200, 20, 200), 25f, 2, Team: 2));
        gp.Add(GpKind.Soldier, new SoldierSpawnDef("sp_a", new Vec3(42, 20, 42), Vec3.Zero, Group: 1));
        gp.Add(GpKind.Soldier, new SoldierSpawnDef("sp_b", new Vec3(198, 20, 198), Vec3.Zero, Group: 2));
        gp.Add(GpKind.Vehicle, new VehicleSpawnDef("jeep", new Vec3(60, 20, 60), Vec3.Zero, "willy", 1, Team: 1));
        return gp;
    }

    // ---- combat area ----

    [Fact]
    public void Combat_area_reads_offsets_then_sizes_as_the_MDT_documents()
    {
        // Berlin's line. Read as min/max it would be an inverted rectangle; the documented meaning is a 512 m
        // square whose corner is at (1536, 1536).
        Assert.True(CombatArea.TryParse("game.setActiveCombatArea 1536 1536 512 512", out var a));
        Assert.Equal(1536f, a.X); Assert.Equal(512f, a.Width);
        Assert.Equal(2048f, a.X1);
        Assert.True(a.Contains(1700f, 1800f));
        Assert.False(a.Contains(100f, 100f));
        Assert.Equal(0f, a.DistanceOutside(1600f, 1600f));
        Assert.True(a.DistanceOutside(1500f, 1600f) > 30f);
    }

    [Fact]
    public void Combat_area_round_trips_through_Init_con_and_is_added_when_absent()
    {
        var init = new[] { "renderer.fogstart 100", "game.setTeamSkin 1 X", "game.setKit 1 0 A" };
        var patched = new CombatArea(0, 0, 1024, 1024).PatchInitConLines(init);
        Assert.Contains(patched, l => l.StartsWith("game.setActiveCombatArea 0 0 1024 1024"));
        Assert.Equal(4, patched.Count);
        Assert.NotNull(CombatArea.FromInitCon(patched));

        // Replaces in place, never duplicates.
        var again = new CombatArea(10, 20, 500, 500).PatchInitConLines(patched);
        Assert.Single(again, l => l.StartsWith("game.setActiveCombatArea"));
        Assert.Equal(10f, CombatArea.FromInitCon(again)!.Value.X);
    }

    [Fact]
    public void Environment_settings_parse_and_write_the_combat_area_with_the_lighting()
    {
        var env = EnvironmentSettings.Parse(null, null, new[] { "renderer.diffuseColor 1/1/1", "game.setActiveCombatArea 128 0 896 896" });
        Assert.True(env.HasCombatArea);
        Assert.Equal(128f, env.CombatArea!.Value.X);
        var outp = env.PatchInitConLines(new[] { "renderer.diffuseColor 0/0/0", "game.setActiveCombatArea 0 0 1 1" });
        Assert.Contains(outp, l => l.Contains("896"));
    }

    // ---- validator ----

    [Fact]
    public void A_clean_level_raises_no_errors()
    {
        var (hm, cfg) = Flat();
        var so = new StaticObjectsFile();
        so.Objects.Add(Obj("a", "hut", 30, 20, 30));
        so.Objects.Add(Obj("b", "tree", 50, 20, 50));
        var r = LevelValidator.Run(new LevelValidator.Inputs
        {
            Objects = so, Gameplay = Gameplay(), Heightmap = hm, Config = cfg,
            CombatArea = CombatArea.Whole(256), TemplateExists = _ => true,
        });
        Assert.Equal(0, r.Errors);
        Assert.Equal(0, r.Warnings);
    }

    [Fact]
    public void Floating_and_buried_objects_are_measured_from_the_template_bottom()
    {
        var (hm, cfg) = Flat(metres: 20f);
        var so = new StaticObjectsFile();
        so.Objects.Add(Obj("fl", "crate", 10, 24f, 10));       // origin 4 m up, bottom at origin -> floating
        so.Objects.Add(Obj("ok", "bridge", 20, 25f, 20));      // origin 5 m up but the mesh reaches 5 m down -> fine
        so.Objects.Add(Obj("bu", "crate", 30, 15f, 30));       // 5 m under
        var r = LevelValidator.Run(new LevelValidator.Inputs
        {
            Objects = so, Heightmap = hm, Config = cfg, TemplateExists = _ => true,
            Bounds = t => t == "bridge" ? (new Vec3(-5, -5, -5), new Vec3(5, 5, 5)) : (new Vec3(-1, 0, -1), new Vec3(1, 2, 1)),
        });
        Assert.Contains(r.Issues, i => i.Category == "Floating" && i.ObjectId == "fl");
        Assert.Contains(r.Issues, i => i.Category == "Buried" && i.ObjectId == "bu");
        Assert.DoesNotContain(r.Issues, i => i.ObjectId == "ok");
    }

    [Fact]
    public void Missing_templates_duplicate_ids_and_double_placements_are_errors_or_warnings()
    {
        var (hm, cfg) = Flat();
        var so = new StaticObjectsFile();
        so.Objects.Add(Obj("same", "hut", 10, 20, 10));
        so.Objects.Add(Obj("same", "hut", 10, 20, 10));       // duplicate id AND duplicate placement
        so.Objects.Add(Obj("gone", "not_a_real_thing", 50, 20, 50));
        var r = LevelValidator.Run(new LevelValidator.Inputs
        {
            Objects = so, Heightmap = hm, Config = cfg, TemplateExists = t => t == "hut",
        });
        Assert.Contains(r.Issues, i => i.Category == "Duplicate id");
        Assert.Contains(r.Issues, i => i.Category == "Duplicate object");
        Assert.Contains(r.Issues, i => i.Category == "Missing template" && i.ObjectId == "gone");
    }

    [Fact]
    public void Gameplay_checks_catch_orphan_flags_dead_spawners_and_out_of_area_spawns()
    {
        var (hm, cfg) = Flat();
        var gp = new EditableGameplay(GameplayObjects.Empty);
        gp.Add(GpKind.ControlPoint, new ControlPointDef("lonely", new Vec3(40, 20, 40), 25f, 7, Team: 1));   // group 7 has no spawn
        gp.Add(GpKind.ControlPoint, new ControlPointDef("far", new Vec3(250, 20, 250), 25f, 1, Team: 2));    // outside area
        gp.Add(GpKind.Soldier, new SoldierSpawnDef("sp", new Vec3(42, 20, 42), Vec3.Zero, Group: 1));
        gp.Add(GpKind.Vehicle, new VehicleSpawnDef("empty", new Vec3(60, 20, 60), Vec3.Zero, "", 1));           // nothing to spawn
        gp.Add(GpKind.Soldier, new SoldierSpawnDef("sunk", new Vec3(80, 10, 80), Vec3.Zero, Group: 1));       // 10 m under
        var r = LevelValidator.Run(new LevelValidator.Inputs
        {
            Gameplay = gp, Heightmap = hm, Config = cfg, CombatArea = new CombatArea(0, 0, 200, 200),
        });
        Assert.Contains(r.Issues, i => i.Message.Contains("'lonely'") && i.Message.Contains("spawn group 7"));
        Assert.Contains(r.Issues, i => i.Message.Contains("'far'") && i.Message.Contains("outside"));
        Assert.Contains(r.Issues, i => i.Message.Contains("'empty'") && i.Message.Contains("no vehicle"));
        Assert.Contains(r.Issues, i => i.Message.Contains("'sunk'") && i.Message.Contains("under the ground"));
    }

    // ---- reachability ----

    [Fact]
    public void A_wall_across_the_navmap_makes_the_far_flag_unreachable()
    {
        int side = 32; var grid = new byte[side * side];
        for (int z = 0; z < side; z++) grid[z * side + 16] = 0xFF;      // a wall down the middle
        var gp = new EditableGameplay(GameplayObjects.Empty);
        gp.Add(GpKind.Soldier, new SoldierSpawnDef("sp", new Vec3(20, 0, 128), Vec3.Zero, Group: 1));
        gp.Add(GpKind.ControlPoint, new ControlPointDef("near", new Vec3(60, 0, 128), 10f, 1));
        gp.Add(GpKind.ControlPoint, new ControlPointDef("beyond", new Vec3(220, 0, 128), 10f, 2));

        var r = Reachability.Check(grid, side, 256f, gp, "Infantry");
        Assert.DoesNotContain(r.Issues, i => i.Message.Contains("'near'"));
        Assert.Contains(r.Issues, i => i.Category == "Unreachable" && i.Message.Contains("'beyond'"));
        Assert.Contains(r.Issues, i => i.Category == "Islands");       // half the map is cut off
    }

    [Fact]
    public void A_spawn_on_a_blocked_kerb_is_snapped_to_the_nearest_passable_cell()
    {
        int side = 16; var grid = new byte[side * side];
        grid[8 * side + 8] = 0xFF;                                       // the one blocked cell
        var reached = Reachability.Flood(grid, side, new[] { (8, 8) }); // seeded exactly on it
        Assert.True(reached.Count(b => b) > 200, "the fill should still spread from beside the blocked cell");
    }

    // ---- performance budget ----

    [Fact]
    public void The_budget_flags_the_dense_cell_and_not_the_map()
    {
        var so = new StaticObjectsFile();
        for (int i = 0; i < 60; i++) so.Objects.Add(Obj($"c{i}", "cathedral", 100 + i * 0.5f, 0, 100));   // all in one cell
        so.Objects.Add(Obj("lone", "cathedral", 900, 0, 900));
        var (r, cells) = PerformanceBudget.Run(so, 1024f,
            t => new PerformanceBudget.TemplateCost(5000, 4L * 1024 * 1024, 3));
        Assert.Contains(r.Issues, i => i.Category == "Triangles" && i.Severity == IssueSeverity.Error);
        var dense = cells.OrderByDescending(c => c.Triangles).First();
        Assert.Equal(300_000, dense.Triangles);
        Assert.Equal(4L * 1024 * 1024, dense.TextureBytes);   // one template -> its textures counted once, not 60x
        Assert.Equal(2, cells.Count);
    }

    // ---- dependencies / split ----

    [Fact]
    public void Dependency_check_names_what_the_mod_chain_lacks()
    {
        var so = new StaticObjectsFile();
        so.Objects.Add(Obj("a", "hut", 0, 0, 0));
        so.Objects.Add(Obj("b", "ufo", 0, 0, 0));
        var gp = new EditableGameplay(GameplayObjects.Empty);
        gp.Add(GpKind.Vehicle, new VehicleSpawnDef("v", Vec3.Zero, Vec3.Zero, "tank_zz", 1));
        var r = DependencyCheck.Run(so, gp, new DependencyCheck.Resolvers
        {
            TemplateExists = t => t == "hut",
            UnresolvedTextures = t => t == "hut" ? new[] { "roof_missing.dds" } : Array.Empty<string>(),
        });
        Assert.Contains(r.Issues, i => i.Category == "Missing template" && i.Message.Contains("'ufo'"));
        Assert.Contains(r.Issues, i => i.Category == "Missing vehicle" && i.Message.Contains("'tank_zz'"));
        Assert.Contains(r.Issues, i => i.Category == "Missing texture" && i.Message.Contains("roof_missing"));
    }

    [Fact]
    public void Server_client_split_uses_the_same_rule_as_the_SSM_writer()
    {
        var (entries, sv, cl) = ServerClientSplit.Classify(new[]
        {
            ("Init.con", 100L), ("Textures/a.dds", 1000L), ("sound/x.wav", 500L), ("Conquest/ControlPoints.con", 50L),
        });
        Assert.Equal(150L, sv);
        Assert.Equal(1500L, cl);
        Assert.True(entries.First(e => e.Path == "Textures/a.dds").ClientOnly);
        Assert.False(entries.First(e => e.Path == "Init.con").ClientOnly);
    }

    // ---- diff ----

    [Fact]
    public void Diff_tells_a_nudge_from_a_delete_and_an_add()
    {
        var before = new StaticObjectsFile();
        before.Objects.Add(Obj("1", "hut", 10, 0, 10));
        before.Objects.Add(Obj("2", "tree", 50, 0, 50));
        before.Objects.Add(Obj("3", "rock", 90, 0, 90));
        var after = new StaticObjectsFile();
        after.Objects.Add(Obj("x", "hut", 12, 0, 10));        // nudged 2 m: same object, moved
        var rock = Obj("y", "rock", 90, 0, 90); rock.Rotation = new Vec3(90, 0, 0);          // rotated
        after.Objects.Add(rock);
        after.Objects.Add(Obj("z", "fence", 5, 0, 5));        // new; the tree is gone

        var d = LevelDiff.Compare(before, after);
        Assert.Equal(1, d.Added);
        Assert.Equal(1, d.Removed);
        Assert.Equal(1, d.Moved);
        Assert.Equal(1, d.Rotated);
        Assert.Contains(d.Changes, c => c.Kind == LevelDiff.Kind.Removed && c.Template == "tree");
        Assert.Contains(d.Changes, c => c.Kind == LevelDiff.Kind.Added && c.Template == "fence");
    }

    // ---- selection ops ----

    [Fact]
    public void Distribute_keeps_the_ends_and_spaces_the_middle_evenly()
    {
        var sel = new List<StaticObject> { Obj("a", "p", 0, 0, 0), Obj("b", "p", 3, 0, 0), Obj("c", "p", 4, 0, 0), Obj("d", "p", 30, 0, 0) };
        var moved = SelectionOps.DistributeEvenly(sel);
        Assert.Equal(2, moved.Count);
        Assert.Equal(10f, moved[0].Position.X, 3);
        Assert.Equal(20f, moved[1].Position.X, 3);
    }

    [Fact]
    public void Mirror_reflects_positions_across_the_centroid_and_flips_yaw()
    {
        var sel = new List<StaticObject>
        {
            Obj("a", "p", 0, 0, 0),
            Obj("b", "p", 10, 0, 0),
        };
        sel[0].Rotation = new Vec3(30, 0, 0);
        var m = SelectionOps.Mirror(sel, SelectionOps.MirrorAxis.X);
        Assert.Equal(10f, m[0].Position.X, 3);    // centroid 5 -> 0 becomes 10
        Assert.Equal(0f, m[1].Position.X, 3);
        Assert.Equal(-30f, m[0].Rotation.X, 3);
    }

    [Fact]
    public void Align_to_ground_tilts_by_the_slope_but_keeps_the_yaw()
    {
        var o = Obj("a", "p", 0, 0, 0); o.Rotation = new Vec3(45, 0, 0);
        // A slope rising toward +Z: the normal leans back toward -Z.
        var res = SelectionOps.AlignToGround(new[] { o }, (_, _) => new Vec3(0f, 0.9f, -0.436f), false, null);
        Assert.Single(res);
        Assert.Equal(45f, res[0].Rotation.X, 3);
        Assert.True(MathF.Abs(res[0].Rotation.Y) > 5f || MathF.Abs(res[0].Rotation.Z) > 5f, "some tilt was applied");
    }

    // ---- terrain tools ----

    [Fact]
    public void Erosion_lowers_a_spike_and_leaves_the_border_untouched()
    {
        var cfg = new TerrainConfig { MaterialSize = 32, WorldSize = 128, YScale = 1f };
        var hm = new Heightmap(32, 32);
        for (int i = 0; i < hm.Samples.Length; i++) hm.Samples[i] = cfg.MetersToRaw(10f);
        hm[16, 16] = cfg.MetersToRaw(40f);                       // a single spike
        float border = cfg.HeightToMeters(hm[0, 0]);

        var outp = Erosion.Run(hm, cfg, 0, 0, 32, 32, new Erosion.Params { Iterations = 30, Hydraulic = false });
        Assert.True(outp[16 * 32 + 16] < 40f - 3f, "thermal erosion should have knocked the spike down");
        Assert.True(outp[17 * 32 + 16] > 10f, "and put the material next to it");
        Assert.Equal(border, outp[0], 3);                        // the pinned ring
        Assert.Equal(border, outp[31 * 32 + 31], 3);
    }

    [Fact]
    public void A_river_carves_a_bed_that_never_runs_uphill_and_paints_its_banks()
    {
        var cfg = new TerrainConfig { MaterialSize = 64, WorldSize = 256, YScale = 1f };
        var hm = new Heightmap(64, 64);
        // Ground rising along +X, so a naive carve would leave the bed sloping.
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) hm[x, y] = cfg.MetersToRaw(20f + x * 0.3f);
        var path = new List<(float, float)> { (40f, 128f), (120f, 128f), (200f, 128f) };

        var res = RiverTool.Build(hm, cfg, path, new RiverTool.Params { Width = 24f, Depth = 4f, BankWidth = 8f, BankMaterial = 3, BedMaterial = 4 });

        Assert.True(res.TerrainRect.W > 0);
        float bedStart = cfg.HeightToMeters(hm[10, 32]), bedEnd = cfg.HeightToMeters(hm[50, 32]);
        Assert.True(MathF.Abs(bedStart - bedEnd) < 0.6f, $"a levelled bed ({bedStart:0.0} vs {bedEnd:0.0})");
        Assert.Contains(res.Paint, p => p.Material == 4);
        Assert.Contains(res.Paint, p => p.Material == 3);
        Assert.True(res.SuggestedWaterLevel < 20f + 10 * 0.3f, "water sits below the original bank");
    }

    [Fact]
    public void Time_of_day_presets_produce_a_unit_sun_vector_and_night_matches_Basrah()
    {
        foreach (var p in TimeOfDayPreset.All)
        {
            var d = p.SunDirection();
            Assert.Equal(1f, MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z), 3);
            Assert.True(d.Y > 0f, $"{p.Name}: the sun is above the horizon");
        }
        Assert.Equal(0.080f, TimeOfDayPreset.Night.GlobalAmbient.X, 3);
        Assert.Equal(130f, TimeOfDayPreset.Night.FogEnd);
    }

    // ---- patch saving ----

    // Retail ships level patches with GAPS - vanilla Bocage has _000, _003 and _006 and no others - so the next
    // patch must be one past the HIGHEST, not one past the count, and must never reuse a retail number.
    [Fact]
    public void Next_patch_number_follows_the_highest_existing_patch_even_with_gaps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rfpatch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var basePath = Path.Combine(dir, "Bocage.rfa");
            File.WriteAllBytes(basePath, new byte[] { 1 });
            foreach (var n in new[] { "000", "003", "006" })
                File.WriteAllBytes(Path.Combine(dir, $"Bocage_{n}.rfa"), new byte[] { 1 });

            Assert.Equal("Bocage_007.rfa", Path.GetFileName(LevelSaver.NextPatchPath(basePath)));

            // Asking from a patch resolves against the same base stem, not "Bocage_006_007".
            Assert.Equal("Bocage_007.rfa", Path.GetFileName(LevelSaver.NextPatchPath(Path.Combine(dir, "Bocage_006.rfa"))));

            // A level with no patches yet starts at _001, leaving _000 alone.
            var solo = Path.Combine(dir, "Wake.rfa");
            File.WriteAllBytes(solo, new byte[] { 1 });
            Assert.Equal("Wake_001.rfa", Path.GetFileName(LevelSaver.NextPatchPath(solo)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // A level edit whose file the base archive does not ship must still be written. Add-on maps that borrow a base
    // map's terrain often carry no MaterialMap.raw of their own; matching-only silently dropped the edit and still
    // reported the save as successful.
    [Fact]
    public void Editing_a_file_the_archive_does_not_ship_adds_it_instead_of_dropping_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rfadd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // A minimal add-on level: StaticObjects + Heightmap, but NO MaterialMap.raw.
            var basePath = Path.Combine(dir, "AddOn.rfa");
            RefractorFlatArchive.WriteFile(basePath, new (string, byte[])[]
            {
                ("bf1942/levels/AddOn/StaticObjects.con", System.Text.Encoding.Latin1.GetBytes("rem\r\n")),
                ("bf1942/levels/AddOn/Heightmap.raw", new byte[8 * 8 * 2]),
            }, compress: false, XPackId.Default);

            var material = new MaterialMap(8, 8);
            material.Samples[5] = 3;
            var outPath = Path.Combine(dir, "AddOn_001.rfa");
            var names = LevelSaver.WritePatchRfa(basePath, outPath, null, null, material, null);

            Assert.Contains(names, n => n.Replace('\\', '/').Equals("bf1942/levels/AddOn/MaterialMap.raw", StringComparison.OrdinalIgnoreCase));
            Assert.Null(RefractorFlatArchive.Validate(outPath));

            var written = new RefractorFlatArchive(outPath);
            var e = written.Entries.Single(x => x.Name.EndsWith("MaterialMap.raw", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, written.Read(e)[5]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- decal object ----

    [Fact]
    public void A_decal_object_is_a_readable_mesh_plus_the_template_files_the_engine_loads()
    {
        var built = DecalObject.Build("Test_Level", "poster 1", 2f, 3f, "my poster", new byte[] { 1, 2, 3 });
        Assert.Equal("poster_1", built.Template);
        var names = built.Files.Select(f => f.RelPath).ToList();
        Assert.Contains("StandardMesh/poster_1.sm", names);
        Assert.Contains("StandardMesh/poster_1.rs", names);
        Assert.Contains("Texture/my_poster.dds", names);
        Assert.Contains("Objects/poster_1/Objects.con", names);
        Assert.Contains("Objects/poster_1/Geometries.con", names);
        Assert.Equal("run poster_1/poster_1", built.RunLine);

        // The .sm must parse back with the project's own reader - that is the engine-facing contract.
        var sm = built.Files.First(f => f.RelPath.EndsWith(".sm")).Bytes;
        Assert.True(StandardMesh.TryParse(sm, out var parsed) && parsed is not null);
        Assert.Equal(4, parsed!.Lods[0][0].Faces.Length);    // two-sided quad = 4 triangles
        Assert.Equal(3f, parsed.BoundingBox[4], 3);           // maxY = height, origin at the base

        var geom = System.Text.Encoding.Latin1.GetString(built.Files.First(f => f.RelPath.EndsWith("Geometries.con")).Bytes);
        Assert.Contains("../bf1942/levels/Test_Level/StandardMesh/poster_1", geom);
    }

    [Fact]
    public void Decal_registration_adds_the_run_lines_once_and_leaves_the_rest_alone()
    {
        string oc = DecalObject.PatchObjectsCon(null, "run poster_1/poster_1");
        Assert.Contains("run poster_1/poster_1", oc);
        Assert.Equal(oc, DecalObject.PatchObjectsCon(oc, "run poster_1/poster_1").TrimEnd('\r', '\n') + "\r\n");

        string init = "renderer.fogstart 100\r\ngame.setKit 1 0 A\r\n";
        string p1 = DecalObject.PatchInitCon(init, "Test_Level");
        Assert.Contains("run Objects/Objects", p1);
        Assert.Contains("textureManager.alternativePath bf1942/levels/Test_Level/Texture", p1);
        Assert.StartsWith("renderer.fogstart 100", p1);
        Assert.Equal(p1, DecalObject.PatchInitCon(p1, "Test_Level"));   // idempotent
    }

    // The engine's shader parser is strict: every statement inside a subshader block takes a value and ends in
    // a semicolon, or it throws and the material never gets its texture. Verified against 6,229 shipped
    // subshaders, in which bare `transparent` / `twosided` and folder-less texture names never occur.
    [Fact]
    public void Decal_shader_matches_the_grammar_the_engine_parses()
    {
        var built = DecalObject.Build("Test_Level", "poster", 2f, 3f, "pic", new byte[] { 1, 2, 3 });
        var rs = System.Text.Encoding.Latin1.GetString(built.Files.First(f => f.RelPath.EndsWith(".rs")).Bytes);

        var body = rs.Split('{')[1].Split('}')[0];
        foreach (var line in body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            Assert.EndsWith(";", line);

        Assert.Contains("transparent false;", rs);      // booleans always carry a value
        Assert.Contains("twosided true;", rs);
        Assert.Contains("lighting true;", rs);
        Assert.Contains("texture \"texture/pic\";", rs);   // folder-qualified, as all 4,406 shipped references are
        Assert.DoesNotContain("\ttransparent\r", rs);
        Assert.DoesNotContain("\ttwosided\r", rs);

        // The subshader name is the binding key: it must equal the material name stored in the .sm.
        Assert.Contains("subshader \"poster_Material0\"", rs);
        Assert.True(StandardMesh.TryParse(built.Files.First(f => f.RelPath.EndsWith(".sm")).Bytes, out var sm));
        Assert.Equal("poster_Material0", sm!.Lods[0][0].Name);
    }

    // BF1942 and Battlefield Vietnam share no archive namespace, so a path written for one resolves to nothing
    // in the other and the object silently gets no mesh.
    [Fact]
    public void Decal_paths_follow_the_target_games_mount_root()
    {
        var bfv = DecalObject.Build("Ia_Drang", "poster", 1f, 1f, "pic", new byte[] { 1 }, baseSub: "BfVietnam");
        var geom = System.Text.Encoding.Latin1.GetString(bfv.Files.First(f => f.RelPath.EndsWith("Geometries.con")).Bytes);
        Assert.Contains("../BfVietnam/levels/Ia_Drang/StandardMesh/poster", geom);
        Assert.DoesNotContain("bf1942", geom);
        Assert.Contains("textureManager.alternativePath BfVietnam/levels/Ia_Drang/Texture",
                        DecalObject.PatchInitCon("renderer.fogstart 100\r\n", "Ia_Drang", "BfVietnam"));

        // The full 0..5 ramp every shipped Geometries.con writes; a truncated one culls the decal early.
        for (int i = 0; i <= 5; i++) Assert.Contains($"setLodDistance {i} ", geom);
    }

    // The texture manager drops any texture that is not power-of-two on both axes, and an object texture without
    // a mip chain aliases badly at distance.
    [Fact]
    public void Decal_texture_is_power_of_two_and_carries_a_mip_chain()
    {
        var odd = new Texture2D(300, 90, new byte[300 * 90 * 4]);
        var snapped = DdsTexture.ToPowerOfTwo(odd, 4, 1024);
        Assert.Equal(256, snapped.Width);
        Assert.Equal(64, snapped.Height);      // 90 is nearer 64 than 128
        Assert.Equal(512, DdsTexture.ToPowerOfTwo(new Texture2D(4000, 8, new byte[4000 * 8 * 4]), 4, 512).Width);

        var dds = DdsTexture.EncodeUncompressedMipped(snapped);
        Assert.Equal(9u, BitConverter.ToUInt32(dds, 28));                       // 256 -> 9 levels
        Assert.Equal(32u, BitConverter.ToUInt32(dds, 88));                     // still plain 32-bit ARGB
        Assert.Equal(0u, BitConverter.ToUInt32(dds, 84));                      // no FourCC: uncompressed
        int expect = 128; for (int w = 256, h = 64; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        { expect += w * h * 4; if (w == 1 && h == 1) break; }
        Assert.Equal(expect, dds.Length);
        Assert.True(DdsTexture.Decode(dds) is { Width: 256, Height: 64 });     // our own reader still round-trips it
    }

    // ---- annotations / groups / packaging ----

    [Fact]
    public void Annotations_round_trip_over_the_wire_and_into_the_relay_state()
    {
        var a = new Annotations();
        a.Add(new Vec3(1, 2, 3), "needs cover here", "lucas");
        var wire = a.ToWire();
        Assert.True(Annotations.TryParseWire(wire, out var json));

        var b = new Annotations();
        b.ApplyText(json);
        Assert.Single(b.Notes);
        Assert.Equal("needs cover here", b.Notes[0].Text);
        Assert.Equal(new Vec3(1, 2, 3), b.Notes[0].Position);

        var world = new CollabWorldState();
        Assert.True(world.ApplyOp(wire));
        Assert.Contains(world.SnapshotOps(), o => o.StartsWith("ANNOT "));
        Assert.True(LevelSaver.IsEditorOnlyFile(Annotations.FileName));
    }

    [Fact]
    public void Groups_hide_and_lock_by_id_and_prune_dead_ids()
    {
        var g = new ObjectGroups();
        var walls = g.Create("Walls"); walls.Ids.Add("w1"); walls.Ids.Add("w2"); walls.Hidden = true;
        var props = g.Create("Walls"); props.Ids.Add("p1"); props.Locked = true;
        Assert.Equal("Walls 2", props.Name);
        Assert.True(g.IsHidden("w1"));
        Assert.False(g.IsHidden("p1"));
        Assert.True(g.IsLocked("p1"));
        Assert.Equal(1, g.Prune(new[] { "w1", "p1" }));
        Assert.DoesNotContain("w2", walls.Ids);
        Assert.True(LevelSaver.IsEditorOnlyFile(ObjectGroups.FileName));
    }

    [Fact]
    public void The_package_holds_every_piece_and_a_readme_that_says_where_it_goes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rfpack_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string rfa = Path.Combine(dir, "My_Map.rfa"); File.WriteAllBytes(rfa, new byte[] { 1, 2, 3, 4 });
            string ssm = Path.Combine(dir, "My_Map_ssm.rfa"); File.WriteAllBytes(ssm, new byte[] { 9 });
            var inp = new LevelPackager.Inputs
            {
                LevelName = "My Map", ModName = "bf1942", Game = "BF1942", Author = "Lucas",
                ClientRfaPath = rfa, ServerRfaPath = ssm, MinimapPng = new byte[] { 0x89 }, ThumbnailPng = new byte[] { 0x89 },
            };
            string zip = Path.Combine(dir, "out.zip");
            var written = LevelPackager.Write(inp, zip);
            var names = written.Select(w => w.Entry).ToList();
            Assert.Contains("My_Map/My_Map.rfa", names);
            Assert.Contains("My_Map/server/My_Map_ssm.rfa", names);
            Assert.Contains("My_Map/minimap.png", names);
            Assert.Contains("My_Map/README.txt", names);
            Assert.Contains(@"Mods\bf1942\Archives\bf1942\levels", LevelPackager.Readme(inp));
            using var z = System.IO.Compression.ZipFile.OpenRead(zip);
            Assert.Equal(5, z.Entries.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
