using System.Numerics;
using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Making placed objects lightmap-able by giving the level a COPY of each object type on a patched mesh. The copy is
/// the whole point: BfVietnam's ammo box is a Bundle carrying two supply depots, and rebuilding it as a plain prop
/// switched resupply off. These pin the copy line for line, the engine's script rules it depends on (comments,
/// template families, <c>.active</c>), the LOD walk the bake shares, and the names staying put across runs.
/// </summary>
public class LightmapReadyTests
{
    // ---- fixtures ----------------------------------------------------------------------------------------------

    /// <summary>A 32-byte box with no lightmap channel, like most of the corpus.</summary>
    private static byte[] Box(string material = "crate_Material0")
    {
        var p = new List<Vector3>();
        var idx = new List<ushort>();
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int b0 = p.Count;
            p.Add(a); p.Add(b); p.Add(c); p.Add(d);
            foreach (var t in new[] { (0, 1, 2), (0, 2, 3) })
            { idx.Add((ushort)(b0 + t.Item3)); idx.Add((ushort)(b0 + t.Item2)); idx.Add((ushort)(b0 + t.Item1)); }
        }
        float x = 1.5f, y = 2f, z = 2.5f;
        Face(new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z));
        Face(new(x, -y, z), new(-x, -y, z), new(-x, y, z), new(x, y, z));
        Face(new(-x, -y, z), new(-x, -y, -z), new(-x, y, -z), new(-x, y, z));
        Face(new(x, -y, -z), new(x, -y, z), new(x, y, z), new(x, y, -z));
        Face(new(-x, y, -z), new(x, y, -z), new(x, y, z), new(-x, y, z));
        Face(new(-x, -y, z), new(x, -y, z), new(x, -y, -z), new(-x, -y, -z));

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)10); w.Write(new byte[4]);
        w.Write(-x); w.Write(-y); w.Write(-z); w.Write(x); w.Write(y); w.Write(z);
        w.Write((byte)0);
        w.Write((uint)0);
        w.Write((uint)1); w.Write((uint)1);
        var nm = Encoding.Latin1.GetBytes(material);
        w.Write((uint)nm.Length); w.Write(nm); w.Write(new byte[12]);
        w.Write(4u); w.Write((uint)1041); w.Write((uint)32);
        w.Write((uint)p.Count); w.Write((uint)idx.Count); w.Write((uint)0);
        foreach (var v in p)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            var n = Vector3.Normalize(v);
            w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
            w.Write(0f); w.Write(0f);
        }
        foreach (var i in idx) w.Write(i);
        w.Write((uint)0); w.Write((uint)0);
        w.Flush();
        return ms.ToArray();
    }

    private static string Rs(string material, bool foliage = false)
        => $"subshader \"{material}\" \"StandardMesh/Default\"\r\n{{\r\n\tlighting true;\r\n"
           + (foliage ? "\talphatestref 0.5;\r\n\tselfillum .3 .3 .3;\r\n" : "")
           + "\ttexture \"texture/x\";\r\n}\r\n";

    /// <summary>Retail's ammo box, verbatim in shape: a Bundle with two supply depots, and a <c>beginrem</c> block at
    /// the end holding two commented-out <c>ObjectTemplate.create SupplyDepot</c> definitions.</summary>
    private const string AmmoObjects =
        "rem\r\nrem *** Ammobox ***\r\nrem\r\n" +
        "ObjectTemplate.create Bundle USAmmobox\r\n" +
        "ObjectTemplate.saveInSeparateFile 1\r\n" +
        "ObjectTemplate.geometry O_USAmmo_m1\r\n" +
        "rem ObjectTemplate.aiTemplate Ammobox_m1 // BFV temp change\r\n" +
        "ObjectTemplate.hasCollisionPhysics 1\r\n" +
        "objectTemplate.cullRadiusScale 3.0\r\n" +
        "ObjectTemplate.addTemplate AmmoboxSupplyDepot\r\n" +
        "ObjectTemplate.setPosition 0/0/0\r\n" +
        "ObjectTemplate.setRotation 0/0/0\r\n" +
        "ObjectTemplate.addTemplate AmmoboxVehicleSupplyDepot\r\n" +
        "ObjectTemplate.setPosition 0/0/0\r\n" +
        "ObjectTemplate.setRotation 0/0/0\r\n\r\n" +
        "beginrem\r\n" +
        "ObjectTemplate.create SupplyDepot AmmoboxSupplyDepot\r\n" +
        "ObjectTemplate.radius 3\r\n" +
        "endrem\r\n";

    private const string AmmoGeometries = "GeometryTemplate.create StandardMesh O_USAmmo_M1\r\nGeometryTemplate.file O_USAmmo_M1\r\n";

    private static LightmapReady.PatchedMesh Patch(string geometry, string copy)
        => new(geometry, copy, new byte[] { 1, 2, 3 }, "rem shader");

    private static string Text(LightmapReady.Output o, string suffix)
        => Encoding.Latin1.GetString(o.Files.First(f => f.RelPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Bytes);

    // ---- reading scripts the way the engine does -----------------------------------------------------------------

    [Fact]
    public void Commented_out_templates_are_not_definitions()
    {
        var ts = TemplateScripts.Parse(new[] { ("a.con", AmmoObjects) });
        Assert.NotNull(ts.Object("USAmmobox"));
        Assert.Null(ts.Object("AmmoboxSupplyDepot"));                // only defined inside beginrem..endrem
        Assert.DoesNotContain(ts.Object("USAmmobox")!.Lines, l => l.Contains("aiTemplate"));   // a rem line
        Assert.DoesNotContain(ts.Object("USAmmobox")!.Lines, l => l.Contains("radius"));       // the commented block
    }

    [Fact]
    public void A_selector_created_inside_a_bundle_does_not_end_the_bundle()
    {
        var ts = TemplateScripts.Parse(new[] { ("o.con",
            "ObjectTemplate.create Bundle Bridge\r\n" +
            "ObjectTemplate.addTemplate lodBridge\r\n" +
            "LodSelectorTemplate.create DistanceSelector Bridge_Selector\r\n" +
            "LodSelectorTemplate.addLodDistance 100\r\n" +
            "ObjectTemplate.hasCollisionPhysics 1\r\n") });
        var b = ts.Object("Bridge")!;
        Assert.Contains("ObjectTemplate.hasCollisionPhysics 1", b.Lines);
        Assert.DoesNotContain(b.Lines, l => l.StartsWith("LodSelectorTemplate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LodSelectorTemplate.addLodDistance 100", ts.Find("LodSelectorTemplate", "Bridge_Selector")!.Lines);
    }

    [Fact]
    public void Active_adds_to_a_definition_made_in_another_file()
    {
        var ts = TemplateScripts.Parse(new[]
        {
            ("objects.con", "ObjectTemplate.create SimpleObject crate\r\nObjectTemplate.geometry crate_m1\r\n"),
            ("physics.con", "ObjectTemplate.active crate\r\nObjectTemplate.hasCollisionPhysics 1\r\n"),
        });
        Assert.Equal(new[] { "ObjectTemplate.geometry crate_m1", "ObjectTemplate.hasCollisionPhysics 1" }, ts.Object("crate")!.Lines);
    }

    [Fact]
    public void A_second_create_conflicts_only_when_it_says_something_different()
    {
        const string one = "ObjectTemplate.create SimpleObject crate\r\nObjectTemplate.geometry crate_m1\r\n";
        var same = TemplateScripts.Parse(new[] { ("mod.con", one), ("level.con", one) });
        Assert.False(same.Object("crate")!.ConflictingCreates);

        var differs = TemplateScripts.Parse(new[] { ("mod.con", one), ("level.con", one.Replace("crate_m1", "barrel_m1")) });
        Assert.True(differs.Object("crate")!.ConflictingCreates);

        // Retail's own rope bridge: a SimpleObject and a Bundle under one name, in one file.
        var bridge = TemplateScripts.Parse(new[] { ("o.con",
            "ObjectTemplate.create SimpleObject O_ROPEBRIDGE01\r\nObjectTemplate.geometry O_ROPEBRIDGE01_m1\r\n" +
            "ObjectTemplate.create Bundle O_ROPEBRIDGE01\r\nObjectTemplate.addTemplate lodO_ROPEBRIDGE01\r\n") });
        Assert.True(bridge.Object("O_ROPEBRIDGE01")!.ConflictingCreates);
    }

    // ---- the copy ---------------------------------------------------------------------------------------------

    /// <summary>The one that matters: an ammo box copied as an ammo box. Every line it had, in order, with only the
    /// mesh renamed - its supply depots are referenced exactly as before, and the commented-out definitions at the
    /// end of the file do not leak into the copy as an unterminated <c>beginrem</c>.</summary>
    [Fact]
    public void The_ammo_box_copy_keeps_its_supply_depots_line_for_line()
    {
        var ts = TemplateScripts.Parse(new[] { ("g.con", AmmoGeometries), ("o.con", AmmoObjects) });
        var patches = new Dictionary<string, LightmapReady.PatchedMesh> { ["O_USAmmo_M1"] = Patch("O_USAmmo_M1", "O_USAmmo_lm_M1") };
        var o = LightmapReady.Emit(ts, new[] { "USAmmobox" }, patches, "al_vietnas", "BfVietnam");

        Assert.Equal("USAmmobox_lm", o.Placed["usammobox"]);
        var objs = Text(o, "RF_LightmapReady/Objects.con");
        string body = objs[objs.IndexOf("ObjectTemplate.create Bundle USAmmobox_lm", StringComparison.Ordinal)..].Split("\r\n\r\n")[0];
        var expected = new[]
        {
            "ObjectTemplate.create Bundle USAmmobox_lm",
            "ObjectTemplate.saveInSeparateFile 1",
            "ObjectTemplate.geometry O_USAmmo_lm_M1",
            "ObjectTemplate.hasCollisionPhysics 1",
            "objectTemplate.cullRadiusScale 3.0",
            "ObjectTemplate.addTemplate AmmoboxSupplyDepot",
            "ObjectTemplate.setPosition 0/0/0",
            "ObjectTemplate.setRotation 0/0/0",
            "ObjectTemplate.addTemplate AmmoboxVehicleSupplyDepot",
            "ObjectTemplate.setPosition 0/0/0",
            "ObjectTemplate.setRotation 0/0/0",
        };
        Assert.Equal(expected, body.Split("\r\n"));
        Assert.DoesNotContain("beginrem", objs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SupplyDepot AmmoboxSupplyDepot", objs);
    }

    /// <summary>A Bundle holding a LodObject holding two LOD meshes: the whole chain is copied, each LOD on its own
    /// patched mesh - the game files a lightmap per LOD. A part placed at an OFFSET is not an alternative of its
    /// parent: the bake leaves it alone and so does the copy, referencing the original.</summary>
    [Fact]
    public void A_lod_chain_is_copied_down_to_every_patched_mesh_and_offset_parts_are_left_alone()
    {
        var ts = TemplateScripts.Parse(new[] { ("o.con",
            "ObjectTemplate.create Bundle Hut\r\n" +
            "ObjectTemplate.addTemplate lodHut\r\n" +
            "ObjectTemplate.addTemplate Hut_flag\r\n" +
            "ObjectTemplate.setPosition 0/4/0\r\n" +
            "ObjectTemplate.create LodObject lodHut\r\n" +
            "ObjectTemplate.addTemplate Hut_m1\r\n" +
            "ObjectTemplate.addTemplate Hut_m2\r\n" +
            "ObjectTemplate.create SimpleObject Hut_m1\r\nObjectTemplate.geometry Hut_m1\r\n" +
            "ObjectTemplate.create SimpleObject Hut_m2\r\nObjectTemplate.geometry Hut_m2\r\n" +
            "ObjectTemplate.create SimpleObject Hut_flag\r\nObjectTemplate.geometry flag_m1\r\n") });

        Assert.Equal(new[] { "Hut_m1", "Hut_m2" }, LightmapReady.LodGeometries(ts, "Hut"));

        var patches = new Dictionary<string, LightmapReady.PatchedMesh>
        {
            ["Hut_m1"] = Patch("Hut_m1", "Hut_lm_m1"), ["Hut_m2"] = Patch("Hut_m2", "Hut_lm_m2"),
            ["flag_m1"] = Patch("flag_m1", "flag_lm_m1"),
        };
        var o = LightmapReady.Emit(ts, new[] { "Hut" }, patches, "L", "BfVietnam");
        var objs = Text(o, "RF_LightmapReady/Objects.con");
        Assert.Contains("ObjectTemplate.create Bundle Hut_lm\r\nObjectTemplate.addTemplate lodHut_lm\r\nObjectTemplate.addTemplate Hut_flag\r\nObjectTemplate.setPosition 0/4/0", objs);
        Assert.Contains("ObjectTemplate.create LodObject lodHut_lm\r\nObjectTemplate.addTemplate Hut_lm_m1\r\nObjectTemplate.addTemplate Hut_lm_m2", objs);
        Assert.Contains("ObjectTemplate.create SimpleObject Hut_lm_m1\r\nObjectTemplate.geometry Hut_lm_m1", objs);
        Assert.Contains("ObjectTemplate.create SimpleObject Hut_lm_m2\r\nObjectTemplate.geometry Hut_lm_m2", objs);
        Assert.DoesNotContain("Hut_flag_lm", objs);
    }

    [Fact]
    public void A_template_that_cannot_be_copied_faithfully_is_skipped_with_the_reason()
    {
        var ts = TemplateScripts.Parse(new[] { ("o.con",
            "ObjectTemplate.create SimpleObject Bridge\r\nObjectTemplate.geometry bridge_m1\r\n" +
            "ObjectTemplate.create Bundle Bridge\r\nObjectTemplate.addTemplate lodBridge\r\n" +
            "ObjectTemplate.create SimpleObject crate\r\nObjectTemplate.geometry crate_m1\r\n") });
        var patches = new Dictionary<string, LightmapReady.PatchedMesh> { ["bridge_m1"] = Patch("bridge_m1", "bridge_lm_m1") };
        var o = LightmapReady.Emit(ts, new[] { "Bridge", "crate", "ghost" }, patches, "L", "BfVietnam");
        Assert.Empty(o.Placed);
        Assert.Contains(o.Skipped, s => s.StartsWith("Bridge:") && s.Contains("more than once"));
        Assert.Contains(o.Skipped, s => s.StartsWith("crate:") && s.Contains("patched"));
        Assert.Contains(o.Skipped, s => s.StartsWith("ghost:") && s.Contains("no definition"));
    }

    [Fact]
    public void The_geometry_copy_points_into_the_level_and_keeps_its_other_settings()
    {
        var ts = TemplateScripts.Parse(new[] { ("g.con",
            "GeometryTemplate.create StandardMesh stecrate1_M1\r\nGeometryTemplate.file ../standardMesh/stecrate1_M1\r\n" +
            "GeometryTemplate.setLodDistance 1 20\r\n"), ("o.con", "ObjectTemplate.create SimpleObject stecrate1_M1\r\nObjectTemplate.geometry stecrate1_M1\r\n") });
        Assert.Equal("../standardMesh/stecrate1_M1", LightmapReady.GeometryFile(ts, "stecrate1_M1"));
        var o = LightmapReady.Emit(ts, new[] { "stecrate1_M1" },
            new Dictionary<string, LightmapReady.PatchedMesh> { ["stecrate1_M1"] = Patch("stecrate1_M1", "stecrate1_lm_M1") }, "al_vietnas", "BfVietnam");
        Assert.Equal("GeometryTemplate.create StandardMesh stecrate1_lm_M1\r\n" +
                     "GeometryTemplate.file ../BfVietnam/levels/al_vietnas/StandardMesh/stecrate1_lm_M1\r\n" +
                     "GeometryTemplate.setLodDistance 1 20\r\n\r\n", Text(o, "RF_LightmapReady/Geometries.con"));
        Assert.Contains(o.Files, f => f.RelPath == "StandardMesh/stecrate1_lm_M1.sm");
        Assert.Contains(o.Files, f => f.RelPath == "StandardMesh/stecrate1_lm_M1.rs");
        // Geometries first, as the level's own object folders run them.
        Assert.Equal("run Geometries\r\nrun Objects\r\n", Text(o, "RF_LightmapReady/RF_LightmapReady.con"));
    }

    /// <summary>A second run reads the first run's manifest and must write the SAME names - including the LodObject
    /// inside a copy, which a manifest of placed types alone would mistake for somebody else's template.</summary>
    [Fact]
    public void A_second_run_keeps_every_name_the_first_one_chose()
    {
        const string hut =
            "ObjectTemplate.create Bundle Hut\r\nObjectTemplate.addTemplate lodHut\r\n" +
            "ObjectTemplate.create LodObject lodHut\r\nObjectTemplate.addTemplate Hut_m1\r\n" +
            "ObjectTemplate.create SimpleObject Hut_m1\r\nObjectTemplate.geometry Hut_m1\r\n";
        var ts = TemplateScripts.Parse(new[] { ("o.con", hut) });
        var patches = new Dictionary<string, LightmapReady.PatchedMesh> { ["Hut_m1"] = Patch("Hut_m1", "Hut_lm_m1") };
        var first = LightmapReady.Emit(ts, new[] { "Hut" }, patches, "L", "BfVietnam");
        var manifest = LightmapReady.ReadManifest(Text(first, "RF_LightmapReady/Objects.con"));
        Assert.Equal("Hut_lm", manifest.Placed["Hut"]);
        Assert.Equal("lodHut_lm", manifest.Copies["lodHut"]);
        Assert.Equal("Hut_lm_m1", manifest.Geometries["Hut_m1"]);

        // The level now defines the copies too - exactly the situation a re-scan after a save sees.
        var ts2 = TemplateScripts.Parse(new[] { ("o.con", hut), ("rf.con", Text(first, "RF_LightmapReady/Objects.con")) });
        var second = LightmapReady.Emit(ts2, new[] { "Hut" }, patches, "L", "BfVietnam", manifest);
        Assert.Equal(Text(first, "RF_LightmapReady/Objects.con"), Text(second, "RF_LightmapReady/Objects.con"));
        Assert.Equal("Hut_lm_m1", LightmapReady.GeometryCopyName(ts2, "Hut_m1", manifest));
    }

    [Fact]
    public void A_name_the_mod_already_uses_is_numbered_past()
    {
        var ts = TemplateScripts.Parse(new[] { ("o.con",
            "ObjectTemplate.create SimpleObject crate\r\nObjectTemplate.geometry crate_m1\r\n" +
            "ObjectTemplate.create SimpleObject crate_lm\r\nObjectTemplate.geometry other_m1\r\n" +
            "GeometryTemplate.create StandardMesh crate_lm_m1\r\nGeometryTemplate.file other\r\n") });
        // Numbered BEFORE the LOD tag - "crate_lm_m12" would read to the lightmap matcher as LOD twelve.
        Assert.Equal("crate_lm2_m1", LightmapReady.GeometryCopyName(ts, "crate_m1"));
        var o = LightmapReady.Emit(ts, new[] { "crate" },
            new Dictionary<string, LightmapReady.PatchedMesh> { ["crate_m1"] = Patch("crate_m1", "crate_lm2_m1") }, "L", "BfVietnam");
        Assert.Equal("crate_lm2", o.Placed["crate"]);
    }

    [Fact]
    public void The_level_scripts_are_patched_once()
    {
        const string init = "run Init/Terrain\r\nrun Init/SkyAndSun\r\n";
        string once = LightmapReady.PatchInitCon(init);
        Assert.EndsWith("run Objects/Objects\r\n", once);
        Assert.Equal(once, LightmapReady.PatchInitCon(once));
        Assert.Equal("run objects/Objects\r\n", LightmapReady.PatchInitCon("run objects/Objects\r\n"));   // already there, any case

        string oc = LightmapReady.PatchObjectsCon("run echo1/echo1\r\n");
        Assert.EndsWith(LightmapReady.RunLine + "\r\n", oc);
        Assert.Equal(oc, LightmapReady.PatchObjectsCon(oc));
    }

    // ---- the gutter floor -------------------------------------------------------------------------------------

    /// <summary>Our unwrap writes every UV as a whole number of texels over the size it packed for, so that size can
    /// be read back and the bake never shrinks the gutters below the dilation's reach.</summary>
    [Fact]
    public void The_bake_size_floor_is_read_back_from_our_own_unwrap()
    {
        StandardMesh.TryParse(Box(), out var sm);
        var r = LightmapUnwrapper.Unwrap(sm!);
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r.Status);
        var uvs = r.Lod0Plan!.Where(p => p is not null).SelectMany(p => p!.Uv2).Select(t => new Vector2(t.U, t.V)).ToList();
        Assert.Equal(r.Diagnostics.MinBakeSize, LightmapUnwrapper.RecoverMinBakeSize(uvs));
    }

    [Fact]
    public void An_unwrap_we_did_not_make_has_no_floor()
    {
        Assert.Null(LightmapUnwrapper.RecoverMinBakeSize(new[] { new Vector2(0.1234567f, 0.7654321f), new Vector2(0.333f, 0.9f) }));
        Assert.Null(LightmapUnwrapper.RecoverMinBakeSize(new[] { Vector2.Zero, Vector2.Zero }));
        Assert.Null(LightmapUnwrapper.RecoverMinBakeSize(null));
    }

    // ---- re-pointing placements ---------------------------------------------------------------------------------

    /// <summary>Only the template changes: the position keeps its original text, so re-saving does not rewrite every
    /// number in the object - a delete-and-add would.</summary>
    [Fact]
    public void Retemplating_changes_only_the_template_and_undoes()
    {
        var f = StaticObjectsFile.Parse(new[] { "Object.create usammobox", "Object.absolutePosition 510.50000/13.2/236", "Object.rotation 90/0/0" });
        var o = f.Objects[0];
        var cmd = new RetemplateObject(o.Id, "usammobox_lm");
        cmd.Apply(f);
        Assert.Equal("usammobox_lm", o.Template);
        Assert.Equal("510.50000/13.2/236", o.PositionSource);
        cmd.Undo(f);
        Assert.Equal("usammobox", o.Template);

        Assert.True(EditWire.IsObjectOp(cmd.ToWire()));
        var back = Assert.IsType<RetemplateObject>(EditWire.Parse(cmd.ToWire()));
        Assert.Equal((o.Id, "usammobox_lm"), (back.Id, back.To));
    }

    // ---- the planner --------------------------------------------------------------------------------------------

    private static TemplateScripts CrateScripts() => TemplateScripts.Parse(new[] { ("o.con",
        "GeometryTemplate.create StandardMesh crate_m1\r\nGeometryTemplate.file crate_m1\r\n" +
        "ObjectTemplate.create SimpleObject crate\r\nObjectTemplate.geometry crate_m1\r\nObjectTemplate.hasCollisionPhysics 1\r\n") });

    private static int Px(float extent, int floor) => Math.Max(64, floor);

    [Fact]
    public void A_32_byte_prop_is_offered_as_a_widen_and_patches_to_a_real_unwrap()
    {
        var ts = CrateScripts();
        var meshes = new Dictionary<string, LightmapReadyPlanner.MeshSource?> { ["crate_m1"] = new(Box(), Rs("crate_Material0")) };
        var rows = LightmapReadyPlanner.Plan(new[] { ("crate", 3) }, ts, LightmapReady.Manifest.Empty, meshes, Px, out int undefined);
        var row = Assert.Single(rows);
        Assert.Equal(0, undefined);
        Assert.True(row.Selectable && row.DefaultOn && row.Widens);
        Assert.Equal(LightmapReadyPlanner.Kind.Structure, row.Kind);
        Assert.Equal(1, row.MapsPerObject);

        var patches = LightmapReadyPlanner.BuildPatches(new[] { "crate" }, ts, LightmapReady.Manifest.Empty, meshes, out var failures);
        Assert.Empty(failures);
        var p = patches["crate_m1"];
        Assert.Equal("crate_lm_m1", p.Copy);
        Assert.True(StandardMesh.TryParse(p.Sm, out var sm));
        Assert.Equal((byte)1, sm!.QFlag);                                    // "carries a real unwrap", as on 371 of 371 retail meshes
        Assert.All(sm.Lods[0], m =>
        {
            Assert.Equal(40u, m.VertexByteSize);
            Assert.Contains(m.LightmapUvs, uv => uv.U != 0f || uv.V != 0f);
        });

        // Patched, it counts as unwrapped: the same type is no longer offered.
        var again = new Dictionary<string, LightmapReadyPlanner.MeshSource?> { ["crate_m1"] = new(p.Sm, Rs("crate_Material0")) };
        Assert.Empty(LightmapReadyPlanner.Plan(new[] { ("crate", 3) }, ts, LightmapReady.Manifest.Empty, again, Px, out _));
    }

    /// <summary>Every BfVietnam vegetation material carries alphatestref AND selfillum; that, not a name, is the test.</summary>
    [Fact]
    public void Foliage_is_read_from_the_shader_and_starts_unticked()
    {
        var ts = CrateScripts();
        var meshes = new Dictionary<string, LightmapReadyPlanner.MeshSource?> { ["crate_m1"] = new(Box(), Rs("crate_Material0", foliage: true)) };
        var row = Assert.Single(LightmapReadyPlanner.Plan(new[] { ("crate", 3) }, ts, LightmapReady.Manifest.Empty, meshes, Px, out _));
        Assert.Equal(LightmapReadyPlanner.Kind.Foliage, row.Kind);
        Assert.True(row.Selectable);
        Assert.False(row.DefaultOn);
    }

    /// <summary>The game client has no guard for a mesh with no shader - it dies - and the dedicated server does not
    /// read shaders, so only this check stands between a copy and a crash.</summary>
    [Fact]
    public void A_mesh_without_a_shader_is_never_patched()
    {
        var ts = CrateScripts();
        var meshes = new Dictionary<string, LightmapReadyPlanner.MeshSource?> { ["crate_m1"] = new(Box(), null) };
        var row = Assert.Single(LightmapReadyPlanner.Plan(new[] { ("crate", 3) }, ts, LightmapReady.Manifest.Empty, meshes, Px, out _));
        Assert.False(row.Selectable);
        Assert.Contains("shader", row.Problem);
        Assert.Empty(LightmapReadyPlanner.BuildPatches(new[] { "crate" }, ts, LightmapReady.Manifest.Empty, meshes, out _));
    }

    // ---- the library sees the copies before they are saved -----------------------------------------------------

    [Fact]
    public void Queued_copies_resolve_through_the_mesh_library_before_a_save()
    {
        var ts = CrateScripts();
        var meshes = new Dictionary<string, LightmapReadyPlanner.MeshSource?> { ["crate_m1"] = new(Box(), Rs("crate_Material0")) };
        var patches = LightmapReadyPlanner.BuildPatches(new[] { "crate" }, ts, LightmapReady.Manifest.Empty, meshes, out _);
        var o = LightmapReady.Emit(ts, new[] { "crate" }, patches, "L", "BfVietnam");

        string dir = Path.Combine(Path.GetTempPath(), "rf_lmready_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var lib = MeshLibrary.Open(dir);
            lib.AddScripts("Objects/RF_LightmapReady/Geometries.con", Text(o, "Geometries.con"));
            lib.AddScripts("Objects/RF_LightmapReady/Objects.con", Text(o, "RF_LightmapReady/Objects.con"));
            var p = patches["crate_m1"];
            lib.AddMeshFile(p.Copy, p.Sm, p.Rs);

            var names = lib.LodGeometryNames(o.Placed["crate"]);
            string name = Assert.Single(names);
            Assert.EndsWith("/StandardMesh/crate_lm_m1", name);                     // the file path the game resolves
            Assert.Equal("crate_lm_m1", ObjectLightmaps.FileBase(name));              // and the name its lightmaps get
            Assert.True(lib.TryGet(name, out var mesh));
            Assert.NotNull(mesh.LightmapUvs);
            Assert.True(lib.TryGetMeshBytes(name, out _, out var bytes));
            Assert.Equal(p.Sm, bytes);
            Assert.True(lib.TryGetRsText(name, out _, out var rs));
            Assert.Equal(p.Rs, rs);
            Assert.Contains(lib.ConScripts(), s => s.Source.EndsWith("RF_LightmapReady/Objects.con"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
