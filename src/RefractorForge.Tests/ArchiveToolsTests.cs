using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The archive tool's working parts beyond pack/unpack: diffing two archives, seeing a whole mod as one file
/// system, finding who references an asset (and which assets nobody does), stripping for a server, and
/// scaffolding a mod. Each is the MDT tool it replaces, on the archive codec that round-trips.
/// </summary>
public class ArchiveToolsTests : IDisposable
{
    private readonly string _dir;
    public ArchiveToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rftools_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] T(string s) => Encoding.Latin1.GetBytes(s);
    private string Make(string name, params (string, byte[])[] entries)
    {
        var p = Path.Combine(_dir, name);
        RefractorFlatArchive.WriteFile(p, entries, compress: false, XPackId.Default);
        return p;
    }

    // ---- diff ----

    [Fact]
    public void Diff_reports_added_removed_changed_and_identical_by_content()
    {
        var a = Make("a.rfa",
            ("bf1942/levels/M/Init.con", T("run Init/Terrain\r\n")),
            ("bf1942/levels/M/StaticObjects.con", T("Object.create hut\r\n")),
            ("bf1942/levels/M/Gone.con", T("x")));
        var b = Make("b.rfa",
            ("bf1942/levels/M/Init.con", T("run Init/Terrain\r\n")),               // identical
            ("bf1942/levels/M/StaticObjects.con", T("Object.create HUT\r\n")),      // same length, different bytes
            ("bf1942/levels/M/New.con", T("y")));

        var d = ArchiveDiff.Compare(a, b);
        Assert.Equal(1, d.Same);
        Assert.Equal(1, d.Changed);
        Assert.Equal(1, d.OnlyInA);
        Assert.Equal(1, d.OnlyInB);
        Assert.False(d.Identical);
        Assert.Contains(d.Lines, l => l.Name.EndsWith("Gone.con") && l.Kind == ArchiveDiff.Kind.OnlyInA);
        Assert.Contains("~  bf1942/levels/M/StaticObjects.con", d.ToReport());

        // Matching is case-insensitive, like the engine's file system.
        var c = Make("c.rfa", ("BF1942/Levels/M/INIT.CON", T("run Init/Terrain\r\n")));
        var d2 = ArchiveDiff.Compare(Make("a2.rfa", ("bf1942/levels/M/Init.con", T("run Init/Terrain\r\n"))), c);
        Assert.True(d2.Identical);
    }

    // ---- workspace ----

    [Fact]
    public void Workspace_resolves_first_layer_wins_and_lists_what_it_overrode()
    {
        var patch = Make("texture_001.rfa", ("texture/roof.dds", T("NEW")));
        var basePath = Make("texture.rfa", ("texture/roof.dds", T("OLD")), ("texture/wall.dds", T("W")));
        var mod = Make("objects.rfa", ("objects/hut/objects.con", T("ObjectTemplate.create SimpleObject hut\r\n")));

        using var ws = ModWorkspace.Open(new[] { (patch, "mymod"), (basePath, "mymod"), (mod, "mymod") });
        Assert.Equal(3, ws.Files.Count);

        var roof = ws.Find("texture/roof.dds")!;
        Assert.Equal(0, roof.LayerIndex);                        // the patch wins
        Assert.Equal(new[] { 1 }, roof.Overridden);              // and it overrode the base
        Assert.Equal("NEW", Encoding.Latin1.GetString(ws.Read(roof)));
        Assert.Equal("OLD", Encoding.Latin1.GetString(ws.ReadFrom(roof, 1)!));

        Assert.Empty(ws.Find("texture/wall.dds")!.Overridden);
        Assert.NotNull(ws.Find("TEXTURE/WALL.DDS"));             // case-insensitive lookup
    }

    [Fact]
    public void Layers_for_a_mod_put_numbered_patches_above_their_base()
    {
        var modDir = Path.Combine(_dir, "Mods", "M");
        var arc = Path.Combine(modDir, "Archives");
        Directory.CreateDirectory(Path.Combine(arc, "bf1942", "levels"));
        foreach (var f in new[] { "texture.rfa", "texture_001.rfa", "texture_003.rfa", "objects.rfa" })
            File.WriteAllBytes(Path.Combine(arc, f), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(arc, "bf1942", "levels", "Wake.rfa"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(arc, "bf1942", "levels", "Wake_002.rfa"), new byte[] { 1 });

        var layers = ModWorkspace.LayersFor(modDir).Select(Path.GetFileName).ToList();
        int i3 = layers.IndexOf("texture_003.rfa"), i1 = layers.IndexOf("texture_001.rfa"), i0 = layers.IndexOf("texture.rfa");
        Assert.True(i3 < i1 && i1 < i0, string.Join(",", layers));
        Assert.True(layers.IndexOf("Wake_002.rfa") < layers.IndexOf("Wake.rfa"));

        Assert.DoesNotContain("Wake.rfa", ModWorkspace.LayersFor(modDir, levelsToo: false).Select(Path.GetFileName));
    }

    // ---- references / unused ----

    [Fact]
    public void References_are_found_by_base_name_across_shaders_scripts_and_mesh_strings()
    {
        var objects = Make("objects.rfa",
            ("standardMesh/AmmoBox_m1.rs", T("subshader \"AmmoBox_m1_Material0\" \"StandardMesh/Default\"\r\n{\r\n\ttexture \"texture/AmmoBox_H\";\r\n}\r\n")),
            ("objects/Vehicles/Willy/objects.con", T("ObjectTemplate.create PlayerControlObject Willy\r\nObjectTemplate.addTemplate WillyEngine\r\n")),
            ("objects/Vehicles/Willy/sounds.con", T("Sound.addSound \"sound/engine/willy_idle.wav\"\r\n")),
            // A mesh: binary noise with an embedded texture name, the way .sm material sections carry them.
            ("standardMesh/Willy_m1.sm", new byte[] { 1, 2, 3, 0 }.Concat(T("texture/Pacific/Kubel1_Z")).Concat(new byte[] { 0, 9, 9 }).ToArray()));

        var refs = AssetReferences.Build(new[] { new RefractorFlatArchive(objects) });
        Assert.Contains("standardMesh/AmmoBox_m1.rs", refs.ReferencesTo("texture/Pacific/AmmoBox_H.dds"));   // folder + ext ignored
        Assert.Contains("objects/Vehicles/Willy/sounds.con", refs.ReferencesTo("willy_idle.wav"));
        Assert.Contains("standardMesh/Willy_m1.sm", refs.ReferencesTo("Kubel1_Z.dds"));                       // from the binary
        Assert.False(refs.IsReferenced("texture/nobody_uses_this.dds"));
    }

    [Fact]
    public void Unused_assets_are_the_ones_nothing_names_minus_convention_loaded_files()
    {
        var objects = Make("objects.rfa",
            ("standardMesh/box.rs", T("texture \"texture/used_tex\";\r\n")),
            ("objects/thing/sounds.con", T("Sound.addSound \"sound/used_snd.wav\"\r\n")));
        var textures = Make("texture.rfa",
            ("texture/used_tex.dds", new byte[100]),
            ("texture/orphan_tex.dds", new byte[5000]),
            ("bf1942/levels/M/Textures/Tx00x00.dds", new byte[300]),    // terrain tile: loaded by convention
            ("menu/texture/logo.dds", new byte[200]));                   // menu art: by layout
        var sounds = Make("sound.rfa",
            ("sound/used_snd.wav", new byte[100]),
            ("sound/orphan_snd.wav", new byte[9000]));

        var refs = AssetReferences.Build(new[] { new RefractorFlatArchive(objects) });
        var unused = refs.UnusedAssets(new[] { ("texture.rfa", new RefractorFlatArchive(textures)), ("sound.rfa", new RefractorFlatArchive(sounds)) });

        var names = unused.Select(u => u.Name).ToList();
        Assert.Contains("texture/orphan_tex.dds", names);
        Assert.Contains("sound/orphan_snd.wav", names);
        Assert.DoesNotContain("texture/used_tex.dds", names);
        Assert.DoesNotContain("sound/used_snd.wav", names);
        Assert.DoesNotContain("bf1942/levels/M/Textures/Tx00x00.dds", names);
        Assert.DoesNotContain("menu/texture/logo.dds", names);
        Assert.Equal("sound/orphan_snd.wav", unused[0].Name);       // biggest first
    }

    // ---- server-side strip ----

    [Fact]
    public void Server_strip_keeps_scripts_and_terrain_and_drops_client_content()
    {
        var src = Make("Wake.rfa",
            ("bf1942/levels/Wake/Init.con", T("run Init/Terrain\r\n")),
            ("bf1942/levels/Wake/Heightmap.raw", new byte[512]),
            ("bf1942/levels/Wake/StaticObjects.con", T("Object.create hut\r\n")),
            ("bf1942/levels/Wake/Textures/Tx00x00.dds", new byte[4096]),
            ("bf1942/levels/Wake/Sounds/amb.wav", new byte[4096]),
            ("bf1942/levels/Wake/Menu/Briefing.dds", new byte[2048]));
        var outp = Path.Combine(_dir, "ssm", "Wake.rfa");
        Directory.CreateDirectory(Path.GetDirectoryName(outp)!);

        var o = ServerSide.Strip(src, outp);
        Assert.True(o.EntriesAfter < o.EntriesBefore);
        Assert.True(o.BytesAfter < o.BytesBefore);
        var names = new RefractorFlatArchive(outp).Entries.Select(e => e.Name).ToList();
        Assert.Contains("bf1942/levels/Wake/Init.con", names);
        Assert.Contains("bf1942/levels/Wake/Heightmap.raw", names);
        Assert.Contains("bf1942/levels/Wake/StaticObjects.con", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".dds") || n.EndsWith(".wav"));
        Assert.Null(RefractorFlatArchive.Validate(outp));

        // Dry run over a folder touches nothing and still reports the saving.
        var dry = ServerSide.StripFolder(_dir, Path.Combine(_dir, "never"), dryRun: true);
        Assert.Contains(dry, r => r.Source == src && !r.Written && r.EntriesAfter == 3);
        Assert.False(Directory.Exists(Path.Combine(_dir, "never")));
    }

    // ---- mod scaffold ----

    [Fact]
    public void A_new_mod_gets_the_retail_init_con_shape_and_its_archive_folders()
    {
        var root = Path.Combine(_dir, "Game");
        var made = ModScaffold.Create(root, new ModScaffold.Spec("My Mod!", "My Mod", "0.1", "https://example.org", false, new[] { "bf1942" }));

        var modDir = Path.Combine(root, "Mods", "My_Mod");
        Assert.True(Directory.Exists(Path.Combine(modDir, "Archives", "bf1942", "levels")));
        var init = File.ReadAllText(Path.Combine(modDir, "init.con"));
        Assert.Contains("game.CustomGameName My Mod", init);
        Assert.Contains("game.addModPath Mods/My_Mod/", init);
        Assert.Contains("game.addModPath Mods/bf1942/", init);
        Assert.Contains("game.setCustomGameVersion 0.1", init);
        Assert.Contains("game.setCustomGameUrl \"https://example.org\"", init);
        Assert.Contains(made, p => p.EndsWith("init.con"));

        Assert.Throws<IOException>(() => ModScaffold.Create(root, new ModScaffold.Spec("My_Mod", "", "", "", false, Array.Empty<string>())));
    }

    // ---- object cloner ----

    [Fact]
    public void Cloning_renames_every_template_named_after_the_object_and_the_references_to_them()
    {
        var files = new[]
        {
            ("objects/Vehicles/Land/Willy/Objects.con",
             "ObjectTemplate.create PlayerControlObject Willy\r\nObjectTemplate.addTemplate WillyEngine\r\nObjectTemplate.addTemplate WillySeat\r\n" +
             "ObjectTemplate.create Engine WillyEngine\r\nObjectTemplate.setNetworkableInfo WillyBodyInfo\r\n" +
             "ObjectTemplate.create Seat WillySeat\r\nrem the willys of the world\r\n"),
            ("objects/Vehicles/Land/Willy/Geometries.con",
             "GeometryTemplate.create StandardMesh Willy_m1\r\nGeometryTemplate.file Willy_m1\r\n"),
            ("objects/Vehicles/Land/Willy/Willy.con", "run Objects\r\nrun Geometries\r\n"),
        };
        var plan = ObjectCloner.Build("Willy", "Radcar", files);

        Assert.Equal("Radcar", plan.Templates["Willy"]);
        Assert.Equal("RadcarEngine", plan.Templates["WillyEngine"]);
        Assert.Equal("RadcarSeat", plan.Templates["WillySeat"]);
        Assert.False(plan.Templates.ContainsKey("Willy_m1"));    // geometry keeps the original mesh

        var obj = plan.Files.First(f => f.NewPath.EndsWith("Objects.con"));
        Assert.Equal("objects/Vehicles/Land/Radcar/Objects.con", obj.NewPath);
        Assert.Contains("ObjectTemplate.create PlayerControlObject Radcar\r\n", obj.Text);
        Assert.Contains("ObjectTemplate.addTemplate RadcarEngine", obj.Text);
        Assert.Contains("ObjectTemplate.create Seat RadcarSeat", obj.Text);
        Assert.Contains("rem the willys of the world", obj.Text);   // prose is not a template name

        var geo = plan.Files.First(f => f.NewPath.EndsWith("Geometries.con"));
        Assert.Contains("GeometryTemplate.file Willy_m1", geo.Text);   // still the original mesh

        Assert.Equal("run Vehicles/Land/Radcar/Radcar", ObjectCloner.RunLine(plan));
    }
}
