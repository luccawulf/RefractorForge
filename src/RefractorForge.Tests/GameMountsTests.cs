using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The engine's literal per-game mount list (<see cref="GameMounts"/>) and the archive order ModChain and ModWorkspace
/// build from it. Two bugs used to sit together here: archives were listed in directory order, so a first-wins library
/// kept <c>texture.rfa</c> over the <c>texture_001.rfa</c> the engine mounts ahead of it; and every <c>*.rfa</c> was
/// listed, including ones the executable never mounts (<c>objects_001</c>, BFV <c>standardMesh_001</c>, <c>_002</c>).
/// Fixing only the order would have let BFV_WW2Mod's unmounted <c>standardMesh_001</c> start winning, so both are
/// covered together.
/// </summary>
public class GameMountsTests : IDisposable
{
    private readonly string _root;

    public GameMountsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rf_mounts_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "Mods"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Mod(string name, params string[] initConLines)
    {
        var dir = Path.Combine(_root, "Mods", name);
        Directory.CreateDirectory(Path.Combine(dir, "Archives"));
        if (initConLines.Length > 0) File.WriteAllLines(Path.Combine(dir, "init.con"), initConLines);
        return dir;
    }

    /// <summary>An archive under a mod's Archives\ - a one-byte placeholder, or a real archive of text entries.</summary>
    private static string Rfa(string modDir, string rel, params (string Name, string Text)[] entries)
    {
        var p = Path.Combine(modDir, "Archives", rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        if (entries.Length == 0) File.WriteAllBytes(p, new byte[] { 0 });
        else RefractorFlatArchive.WriteFile(p, entries.Select(e => (e.Name, Encoding.Latin1.GetBytes(e.Text))).ToList(),
                                            compress: false, XPackId.None);
        return p;
    }

    private static string[] Leaves(IEnumerable<string> paths) => paths.Select(p => Path.GetFileName(p)).ToArray();

    // ---- The table ----

    [Theory]
    [InlineData("objects_001.rfa", false, false, false)]
    [InlineData("standardMesh_001.rfa", true, false, true)]
    [InlineData("StandardMesh_001.rfa", true, false, true)]
    [InlineData("menu_001.rfa", true, false, true)]
    [InlineData("animations_001.rfa", false, true, true)]
    [InlineData("texture_001.rfa", true, true, true)]
    [InlineData("texture_002.rfa", false, false, false)]
    [InlineData("sound_001.rfa", true, true, true)]
    [InlineData("music_001.rfa", false, false, false)]
    [InlineData("treeMesh.rfa", true, false, true)]
    [InlineData("effects.rfa", false, true, true)]
    [InlineData(@"backup\texture.rfa", false, false, false)]
    [InlineData(@"{base}\game.rfa", true, true, true)]
    [InlineData(@"{base}\levels\Berlin_003.rfa", true, true, true)]
    [InlineData(@"{base}/levels/Berlin.rfa", true, true, true)]
    [InlineData(@"{base}\levels\old\Berlin.rfa", false, false, false)]
    public void Only_the_literal_names_are_mounted(string rel, bool bf42, bool bfv, bool union)
    {
        Assert.Equal(bf42, GameMounts.Bf1942.IsMounted(rel.Replace("{base}", "bf1942")));
        Assert.Equal(bfv, GameMounts.Bfv.IsMounted(rel.Replace("{base}", "BfVietnam")));
        Assert.Equal(union, GameMounts.Union.IsMounted(rel.Replace("{base}", "bf1942")));
        Assert.Equal(union, GameMounts.Union.IsMounted(rel.Replace("{base}", "BfVietnam")));
    }

    [Theory]
    [InlineData(@"C:\g\Mods\M\Archives\objects.rfa", @"C:\g\Mods\M\Archives", @"C:\g\Mods\M\Archives")]
    [InlineData(@"C:\g\Mods\FHR\Archives\Archives\font.rfa", @"C:\g\Mods\FHR\Archives\Archives", @"C:\g\Mods\FHR\Archives")]
    [InlineData(@"C:\g\Mods\M\Archives\bf1942\levels\old\Wake.rfa", @"C:\g\Mods\M\Archives\bf1942\levels\old", @"C:\g\Mods\M\Archives")]
    [InlineData(@"C:\g\Mods\M\backup\objects.rfa", @"C:\g\Mods\M\backup", null)]
    [InlineData(@"C:\dl\Archives\bf1942\levels\Wake.rfa", @"C:\dl\Archives\bf1942\levels", @"C:\dl\Archives")]
    [InlineData(@"C:\dl\Archives\objects.rfa", @"C:\dl\Archives", @"C:\dl\Archives")]
    [InlineData(@"C:\dl\Archives\maps\Wake.rfa", @"C:\dl\Archives\maps", null)]
    [InlineData(@"C:\dl\maps\Wake.rfa", @"C:\dl\maps", null)]
    public void A_file_is_placed_under_the_Archives_folder_it_is_mounted_from(string file, string root, string? expected)
        => Assert.Equal(expected, GameMounts.ArchivesFolderOf(file, root));

    [Theory]
    [InlineData("objects/Vehicles/Land/Jeep/Objects.con", "Objects.rfa", "objects/")]
    [InlineData("standardMesh/jeep_hull_m1.sm", "standardMesh.rfa", "standardMesh/")]
    [InlineData("texture/jeep.dds", "texture.rfa", "texture/")]
    [InlineData("bf1942/game/Init.con", @"bf1942\Game.rfa", "bf1942/game/")]
    [InlineData("bf1942/levels/MyMap/Init.con", @"bf1942\levels\MyMap.rfa", "bf1942/levels/MyMap/")]
    [InlineData("treeMesh/palm.tm", "treeMesh.rfa", "treeMesh/")]
    public void Bf1942_entries_map_to_their_archive(string entry, string archive, string root)
    {
        var t = GameMounts.Bf1942.TargetOf(entry);
        Assert.NotNull(t);
        Assert.Equal(archive, t!.RelativePath);
        Assert.Equal(root, t.MountRoot);
    }

    [Theory]
    [InlineData("treeMesh/palm.tm")]           // BFV has no treeMesh archive
    [InlineData("shaders/x.pso")]
    [InlineData("readme.txt")]
    [InlineData("BfVietnam/levels/stray.txt")] // a file directly in levels/ belongs to no level
    public void Bfv_rejects_paths_no_archive_mounts(string entry) => Assert.Null(GameMounts.Bfv.TargetOf(entry));

    [Fact]
    public void Bfv_has_its_own_archives()
    {
        Assert.Equal("music.rfa", GameMounts.Bfv.TargetOf("music/US/track.wav")!.RelativePath);
        Assert.Equal("effects.rfa", GameMounts.Bfv.TargetOf("effects/particles.fx")!.RelativePath);
        Assert.Equal(@"BfVietnam\game.rfa", GameMounts.Bfv.TargetOf("BfVietnam/game/Init.con")!.RelativePath);
        Assert.True(GameMounts.Bfv.TargetOf("BfVietnam/levels/Hue/Init.con")!.IsLevel);
        Assert.False(GameMounts.Bfv.ArchiveByStem("sound")!.RetailCompressed);
        Assert.True(GameMounts.Bf1942.ArchiveByStem("sound")!.RetailCompressed);
        Assert.Equal("standardMesh_001.rfa", GameMounts.Bf1942.ArchiveByStem("standardMesh")!.PatchRelativePath);
        Assert.Null(GameMounts.Bfv.ArchiveByStem("standardMesh")!.PatchRelativePath);
    }

    // ---- Which game ----

    [Fact]
    public void The_game_is_told_by_executable_then_base_mod_folder()
    {
        var a = Path.Combine(_root, "a"); Directory.CreateDirectory(a);
        File.WriteAllBytes(Path.Combine(a, "BF1942.exe"), new byte[] { 0 });
        Assert.Equal(RefractorGame.BF1942, GameMounts.Detect(a));

        var b = Path.Combine(_root, "b"); Directory.CreateDirectory(Path.Combine(b, "Mods", "bf1942"));
        File.WriteAllBytes(Path.Combine(b, "BfVietnam.exe"), new byte[] { 0 });
        Assert.Equal(RefractorGame.BFV, GameMounts.Detect(b));      // the executable outranks a stray folder

        Mod("BfVietnam");
        Assert.Equal(RefractorGame.BFV, GameMounts.Detect(_root));
        Mod("bf1942");
        Assert.Null(GameMounts.Detect(_root));                      // both base mods and no executable: undecided
        Assert.Null(GameMounts.Detect(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Resolve_records_the_game_and_an_undecided_install_falls_back_to_the_chain()
    {
        Mod("bf1942");
        Mod("BfVietnam");
        var mine = Mod("MyMod", "game.addModPath Mods/MyMod/", "game.addModPath Mods/BfVietnam/");
        var r = ModChain.Resolve(_root, mine);
        Assert.Equal(Path.GetFullPath(_root).TrimEnd('\\'), r.GameRoot);
        Assert.Equal(RefractorGame.BFV, r.Game);                    // the install is ambiguous; the chain mounts BfVietnam
        Assert.Same(GameMounts.Bfv, GameMounts.For(r));

        // A chain built by hand, with no GameRoot, is still placed through its mounts.
        var hand = new ModChainResult();
        hand.Mounts.Add(new ModMount("bf1942", Path.Combine(_root, "Mods", "bf1942"), true, 0));
        Assert.Equal(RefractorGame.BF1942, GameMounts.Detect(hand));

        hand.Game = RefractorGame.BFV;                              // an explicit game wins
        Assert.Same(GameMounts.Bfv, GameMounts.For(hand));
    }

    // ---- Order and filtering over real folder layouts ----

    [Fact]
    public void Bf1942_chain_puts_mounted_patches_first_and_skips_what_the_exe_never_mounts()
    {
        var b = Mod("bf1942");
        foreach (var f in new[] { "Objects.rfa", "objects_001.rfa", "standardMesh.rfa", "StandardMesh_001.rfa", "texture.rfa",
                                  "texture_001.rfa", "texture_002.rfa", "animations.rfa", "animations_001.rfa", "menu.rfa",
                                  "menu_001.rfa", "sound.rfa", "sound_001.rfa", "treeMesh.rfa", "bf1942/Game.rfa",
                                  "bf1942/levels/Wake.rfa", "old/standardMesh.rfa" })
            Rfa(b, f);

        var chain = ModChain.Resolve(_root, b);
        Assert.Equal(RefractorGame.BF1942, chain.Game);

        var (mesh, tex) = ModChain.CollectArchives(chain);
        Assert.Equal(new[] { "Objects.rfa", "StandardMesh_001.rfa", "standardMesh.rfa", "treeMesh.rfa", "Game.rfa" }, Leaves(mesh));
        Assert.Equal(new[] { "texture_001.rfa", "texture.rfa" }, Leaves(tex));

        var (all, _) = ModChain.CollectArchives(chain, skipNonAsset: false);
        Assert.Equal(new[] { "animations.rfa", "menu_001.rfa", "menu.rfa", "Objects.rfa", "sound_001.rfa", "sound.rfa",
                             "StandardMesh_001.rfa", "standardMesh.rfa", "treeMesh.rfa", "Game.rfa" }, Leaves(all));

        var skipped = ModChain.UnmountedArchives(chain).Select(p => Path.GetRelativePath(Path.Combine(b, "Archives"), p)).ToArray();
        Assert.Equal(new[] { "animations_001.rfa", "objects_001.rfa", Path.Combine("old", "standardMesh.rfa"), "texture_002.rfa" },
                     skipped.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void Bfv_chain_skips_standardMesh_001_and_menu_001_and_treats_animations_001_as_non_asset()
    {
        var v = Mod("BfVietnam");
        foreach (var f in new[] { "objects.rfa", "standardMesh.rfa", "standardMesh_001.rfa", "texture.rfa", "texture_001.rfa",
                                  "animations.rfa", "animations_001.rfa", "menu.rfa", "menu_001.rfa", "treeMesh.rfa",
                                  "BfVietnam/game.rfa", "BfVietnam/levels/Hue.rfa", "BfVietnam/levels/Hue_001.rfa" })
            Rfa(v, f);

        var chain = ModChain.Resolve(_root, v);
        Assert.Equal(RefractorGame.BFV, chain.Game);

        var (mesh, tex) = ModChain.CollectArchives(chain);
        Assert.Equal(new[] { "objects.rfa", "standardMesh.rfa", "game.rfa" }, Leaves(mesh));
        Assert.Equal(new[] { "texture_001.rfa", "texture.rfa" }, Leaves(tex));
        var (all, _) = ModChain.CollectArchives(chain, skipNonAsset: false);
        Assert.Equal(new[] { "animations_001.rfa", "animations.rfa", "menu.rfa", "objects.rfa", "standardMesh.rfa", "game.rfa" }, Leaves(all));
        Assert.Equal(new[] { "menu_001.rfa", "standardMesh_001.rfa", "treeMesh.rfa" },
                     Leaves(ModChain.UnmountedArchives(chain)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray());

        // The workspace view is the same list, with levels: a level's patch ahead of the level.
        Assert.Equal(new[] { "animations_001.rfa", "animations.rfa", "menu.rfa", "objects.rfa", "standardMesh.rfa",
                             "texture_001.rfa", "texture.rfa", "game.rfa", "Hue_001.rfa", "Hue.rfa" },
                     Leaves(ModWorkspace.LayersFor(v)));
        Assert.Equal(Leaves(ModWorkspace.LayersFor(v)), Leaves(ModWorkspace.LayersForChain(chain).Select(l => l.Path)));
        Assert.DoesNotContain("Hue.rfa", Leaves(ModWorkspace.LayersFor(v, levelsToo: false)));
    }

    [Fact]
    public void Level_patches_stack_highest_first_and_a_numbered_name_alone_is_a_map()
    {
        var b = Mod("bf1942");
        foreach (var f in new[] { "Wake.rfa", "Wake_000.rfa", "Wake_003.rfa", "Wake_Evenings.rfa", "Kursk_1943.rfa", "backup/Wake.rfa" })
            Rfa(b, "bf1942/levels/" + f);

        var scan = GameMounts.Bf1942.Scan(b);
        Assert.Equal(new[] { "Kursk_1943.rfa", "Wake_003.rfa", "Wake_000.rfa", "Wake.rfa", "Wake_Evenings.rfa" },
                     Leaves(scan.Mounted.Select(a => a.Path)));
        Assert.Equal("Wake", scan.Mounted[1].Map);
        Assert.Equal(3, scan.Mounted[1].Patch);
        Assert.Equal("Kursk_1943", scan.Mounted[0].Map);               // no Kursk.rfa beside it: a map, not a patch
        Assert.Equal(-1, scan.Mounted[0].Patch);
        Assert.Single(scan.Unmounted);                                  // levels\backup\Wake.rfa is never mounted
        Assert.Empty(GameMounts.Bf1942.Scan(b, levelsToo: false).Mounted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_mod_over_its_base_resolves_textures_and_meshes_as_the_game_does(bool vietnam)
    {
        // Real archives, read back through the first-wins libraries. MyMod's standardMesh_001 is mounted by BF1942
        // and ignored by BFV; the base's texture_001 outranks its texture.rfa in both.
        var baseName = vietnam ? "BfVietnam" : "bf1942";
        var b = Mod(baseName);
        Rfa(b, "texture.rfa", ("texture/b.dds", "BASE-B"), ("texture/c.dds", "BASE-C"));
        Rfa(b, "texture_001.rfa", ("texture/a.dds", "PATCH-A"), ("texture/b.dds", "PATCH-B"));
        Rfa(b, "standardMesh.rfa", ("standardMesh/hut.sm", "BASE-HUT"));
        var mine = Mod("MyMod", "game.addModPath Mods/MyMod/", $"game.addModPath Mods/{baseName}/");
        Rfa(mine, "texture.rfa", ("texture/a.dds", "MOD-A"));
        Rfa(mine, "standardMesh.rfa", ("standardMesh/shed.sm", "MOD-SHED"));
        Rfa(mine, "standardMesh_001.rfa", ("standardMesh/hut.sm", "MOD-PATCH-HUT"));
        Rfa(mine, "objects_001.rfa", ("objects/x/objects.con", "rem never mounted"));

        var chain = ModChain.Resolve(_root, mine);
        Assert.Equal(vietnam ? RefractorGame.BFV : RefractorGame.BF1942, chain.Game);
        var (mesh, tex) = ModChain.CollectArchives(chain);

        var textures = TextureLibrary.Open(tex);
        string Tex(string n) => Encoding.Latin1.GetString(textures.ResolveRaw(n)!.Value.Bytes);
        Assert.Equal("MOD-A", Tex("texture/a"));                    // the mod outranks the base mod's patch
        Assert.Equal("PATCH-B", Tex("texture/b"));                  // the patch outranks its base
        Assert.Equal("BASE-C", Tex("texture/c"));

        var meshes = MeshLibrary.Open(mesh);
        Assert.True(meshes.TryGetMeshBytes("hut", out _, out var hut));
        Assert.Equal(vietnam ? "BASE-HUT" : "MOD-PATCH-HUT", Encoding.Latin1.GetString(hut));
        Assert.True(meshes.TryGetMeshBytes("shed", out _, out var shed));
        Assert.Equal("MOD-SHED", Encoding.Latin1.GetString(shed));
        Assert.DoesNotContain("objects_001.rfa", Leaves(mesh));
    }

    [Fact]
    public void An_unidentified_game_uses_the_union_patch_before_base_and_never_objects_001()
    {
        // No executable and no base mod anywhere: nothing says which game this is.
        var solo = Mod("Solo");
        foreach (var f in new[] { "objects.rfa", "objects_001.rfa", "standardMesh.rfa", "standardMesh_001.rfa", "texture.rfa",
                                  "texture_001.rfa", "texture_002.rfa", "animations_001.rfa", "menu_001.rfa", "treeMesh.rfa",
                                  "effects.rfa", "bf1942/levels/A.rfa", "BfVietnam/levels/B.rfa", "BfVietnam/levels/B_001.rfa" })
            Rfa(solo, f);

        var chain = ModChain.Resolve(_root, solo);
        Assert.Null(chain.Game);
        Assert.Same(GameMounts.Union, GameMounts.For(chain));

        var (mesh, tex) = ModChain.CollectArchives(chain);
        Assert.Equal(new[] { "effects.rfa", "objects.rfa", "standardMesh_001.rfa", "standardMesh.rfa", "treeMesh.rfa" }, Leaves(mesh));
        Assert.Equal(new[] { "texture_001.rfa", "texture.rfa" }, Leaves(tex));
        Assert.Equal(new[] { "objects_001.rfa", "texture_002.rfa" },
                     Leaves(ModChain.UnmountedArchives(chain)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray());

        var layers = Leaves(ModWorkspace.LayersFor(solo));
        Assert.Contains("animations_001.rfa", layers);
        Assert.Contains("menu_001.rfa", layers);
        Assert.Contains("A.rfa", layers);
        Assert.True(Array.IndexOf(layers, "B_001.rfa") < Array.IndexOf(layers, "B.rfa"));
    }

    [Fact]
    public void A_game_root_is_scanned_mod_by_mod_under_each_Archives_folder()
    {
        // The Archive app opens a game folder to browse everything: paths are then taken under each mod's Archives\.
        var b = Mod("bf1942");
        Rfa(b, "texture.rfa"); Rfa(b, "texture_001.rfa"); Rfa(b, "objects_001.rfa");
        var x = Mod("XPack1");
        Rfa(x, "Texture.rfa"); Rfa(x, "menu_001.rfa"); Rfa(x, "Bf1942/levels/Anzio.rfa");

        Assert.Equal(RefractorGame.BF1942, GameMounts.DetectFolder(_root));
        var layers = ModWorkspace.LayersFor(_root, levelsToo: false);
        Assert.Equal(new[] { "texture_001.rfa", "texture.rfa", "menu_001.rfa", "Texture.rfa" }, Leaves(layers));
        Assert.StartsWith(b, layers[0]);
        Assert.StartsWith(x, layers[3]);
        Assert.Contains("Anzio.rfa", Leaves(ModWorkspace.LayersFor(_root)));
    }

    [Fact]
    public void The_base_game_the_resolver_appends_itself_never_decides_the_game()
    {
        // Both base mods and no executable: the install cannot be told, so the base game Resolve appends to a mod that
        // lists only itself is a blind pick (bf1942). It must not make this a BF1942 chain - a BFV mod would lose its
        // effects, music and animations_001 and get a standardMesh_001 mounted - so the chain falls back to the union.
        Mod("bf1942");
        Mod("BfVietnam");
        var solo = Mod("Solo", "game.addModPath Mods/Solo/");
        foreach (var f in new[] { "effects.rfa", "music.rfa", "animations_001.rfa", "standardMesh.rfa", "standardMesh_001.rfa" })
            Rfa(solo, f);

        var chain = ModChain.Resolve(_root, solo);
        Assert.Contains(chain.Mounts, m => m.Name == "bf1942" && m.IsBaseGameFallback);
        Assert.Null(chain.Game);
        Assert.Same(GameMounts.Union, GameMounts.For(chain));
        Assert.Empty(ModChain.UnmountedArchives(chain));

        // A base mod that a dependency's init.con names is no guess: Top -> Dep -> BfVietnam is a BFV chain, even with
        // the resolver's own bf1942 appended below it.
        Mod("Dep", "game.addModPath Mods/Dep/", "game.addModPath Mods/BfVietnam/");
        var top = Mod("Top", "game.addModPath Mods/Top/", "game.addModPath Mods/Dep/");
        var inherited = ModChain.Resolve(_root, top);
        Assert.Contains(inherited.Mounts, m => m.Name == "BfVietnam" && !m.Listed && !m.IsBaseGameFallback);
        Assert.Equal(RefractorGame.BFV, inherited.Game);
    }

    [Fact]
    public void A_loose_folder_is_listed_whole_with_numbered_patches_ahead_of_their_base()
    {
        // A downloaded folder of map archives sits in no Archives tree, so the engine's list cannot place it; the
        // Archive app still opens it, and every archive in it must show, a patch ahead of the base beside it.
        var dl = Path.Combine(_root, "Download");
        Directory.CreateDirectory(Path.Combine(dl, "more"));
        foreach (var f in new[] { "Wake.rfa", "Wake_003.rfa", "MyMap.rfa", "Kursk_1943.rfa", @"more\texture.rfa", @"more\texture_002.rfa" })
            File.WriteAllBytes(Path.Combine(dl, f), new byte[] { 0 });

        var layers = ModWorkspace.LayersFor(dl);
        Assert.Equal(new[] { "Kursk_1943.rfa", "texture_002.rfa", "texture.rfa", "MyMap.rfa", "Wake_003.rfa", "Wake.rfa" }, Leaves(layers));
        Assert.Empty(GameMounts.Union.Scan(dl).Unmounted);
        Assert.All(GameMounts.Union.Scan(dl).Mounted, a => Assert.True(a.Archive is null && a.Map is null));

        // A folder that merely sits under some unrelated "Archives" folder is just as loose.
        var kept = Path.Combine(_root, "Archives", "Download");
        Directory.CreateDirectory(kept);
        foreach (var f in new[] { "Wake.rfa", "Wake_003.rfa" }) File.WriteAllBytes(Path.Combine(kept, f), new byte[] { 0 });
        Assert.Equal(new[] { "Wake_003.rfa", "Wake.rfa" }, Leaves(ModWorkspace.LayersFor(kept)));
    }

    [Fact]
    public void Archives_chosen_one_by_one_get_the_patches_their_game_mounts_ahead_of_them()
    {
        File.WriteAllBytes(Path.Combine(_root, "BF1942.exe"), new byte[] { 0 });
        var b = Mod("bf1942");
        foreach (var f in new[] { "Objects.rfa", "objects_001.rfa", "standardMesh.rfa", "StandardMesh_001.rfa", "texture.rfa",
                                  "texture_001.rfa", "texture_002.rfa", "bf1942/levels/Wake.rfa", "bf1942/levels/Wake_000.rfa",
                                  "bf1942/levels/Wake_003.rfa" })
            Rfa(b, f);
        string A(string rel) => Path.Combine(b, "Archives", rel.Replace('/', Path.DirectorySeparatorChar));

        // Base before patch, as a file dialog returns them; an unmounted objects_001 chosen by hand stays, but is
        // never added for Objects.rfa; a level brings its patches, highest first.
        var picked = new[] { A("texture.rfa"), A("texture_001.rfa"), A("standardMesh.rfa"), A("objects_001.rfa"), A("Objects.rfa"),
                             A("bf1942/levels/Wake.rfa") };
        Assert.Equal(new[] { "texture_001.rfa", "texture.rfa", "StandardMesh_001.rfa", "standardMesh.rfa", "objects_001.rfa",
                             "Objects.rfa", "Wake_003.rfa", "Wake_000.rfa", "Wake.rfa" },
                     Leaves(GameMounts.WithMountedPatches(picked)));
        Assert.Equal(new[] { "Wake_003.rfa" }, Leaves(GameMounts.WithMountedPatches(new[] { A("bf1942/levels/Wake_003.rfa") })));

        Assert.True(GameMounts.IsMountedFile(A("texture_001.rfa")));
        Assert.False(GameMounts.IsMountedFile(A("texture_002.rfa")));
        Assert.False(GameMounts.IsMountedFile(A("objects_001.rfa")));

        // BF Vietnam mounts no standardMesh_001, so it is never pulled in beside a chosen standardMesh.rfa.
        var other = Path.Combine(_root, "vietnam");
        Directory.CreateDirectory(Path.Combine(other, "Mods", "BfVietnam", "Archives"));
        File.WriteAllBytes(Path.Combine(other, "BfVietnam.exe"), new byte[] { 0 });
        var vsm = Path.Combine(other, "Mods", "BfVietnam", "Archives", "standardMesh.rfa");
        var vpatch = Path.Combine(other, "Mods", "BfVietnam", "Archives", "standardMesh_001.rfa");
        File.WriteAllBytes(vsm, new byte[] { 0 });
        File.WriteAllBytes(vpatch, new byte[] { 0 });
        Assert.Equal(new[] { vsm }, GameMounts.WithMountedPatches(new[] { vsm }));
        Assert.False(GameMounts.IsMountedFile(vpatch));

        // A loose file takes every numbered sibling as a patch, the way a loose folder is listed.
        var loose = Path.Combine(_root, "Picks");
        Directory.CreateDirectory(loose);
        foreach (var f in new[] { "texture.rfa", "texture_001.rfa", "texture_002.rfa" }) File.WriteAllBytes(Path.Combine(loose, f), new byte[] { 0 });
        Assert.Equal(new[] { "texture_002.rfa", "texture_001.rfa", "texture.rfa" },
                     Leaves(GameMounts.WithMountedPatches(new[] { Path.Combine(loose, "texture.rfa") })));
        Assert.True(GameMounts.IsMountedFile(Path.Combine(loose, "texture_002.rfa")));
    }

    [Fact]
    public void A_level_archive_leads_to_its_mods_chain()
    {
        Mod("bf1942");
        var mine = Mod("MyMod", "game.addModPath Mods/MyMod/", "game.addModPath Mods/bf1942/");
        var level = Rfa(mine, "bf1942/levels/Mine.rfa");
        var chain = ModChain.ForLevelArchive(level)!;
        Assert.Equal(new[] { "MyMod", "bf1942" }, chain.Mounts.Select(m => m.Name).ToArray());
        Assert.Equal(RefractorGame.BF1942, chain.Game);

        // A nested mod (Mods\Ballistik_FH\X_Flow) is its own mod, not its parent's.
        var nested = Path.Combine(_root, "Mods", "Ballistik_FH", "X_Flow");
        var nestedLevel = Rfa(nested, "bf1942/levels/Flow.rfa");
        Assert.Equal(nested, ModChain.ForLevelArchive(nestedLevel)!.Mounts[0].Path);

        // Outside a Mods tree: the mod alone, its game told from the folder.
        var outside = Path.Combine(_root, "Unpacked", "SomeMod");
        var outsideLevel = Path.Combine(outside, "Archives", "BfVietnam", "levels", "Hue.rfa");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideLevel)!);
        File.WriteAllBytes(outsideLevel, new byte[] { 0 });
        var alone = ModChain.ForLevelArchive(outsideLevel)!;
        Assert.Equal(outside, Assert.Single(alone.Mounts).Path);
        Assert.Null(alone.Game);

        Assert.Null(ModChain.ForLevelArchive(Path.Combine(_root, "Download", "Wake.rfa")));
    }

    [Fact]
    public void A_folder_inside_an_Archives_tree_is_placed_by_its_real_path()
    {
        // Picking Archives\bf1942\levels (or Archives\bf1942) on its own: each file is still where the engine mounts it.
        var b = Mod("bf1942");
        foreach (var f in new[] { "bf1942/Game.rfa", "bf1942/levels/Wake.rfa", "bf1942/levels/Wake_003.rfa", "bf1942/levels/old/Wake.rfa" })
            Rfa(b, f);
        var levels = Path.Combine(b, "Archives", "bf1942", "levels");

        Assert.Equal(RefractorGame.BF1942, GameMounts.DetectFolder(levels));
        Assert.Equal(new[] { "Wake_003.rfa", "Wake.rfa" }, Leaves(ModWorkspace.LayersFor(levels)));
        Assert.Equal(new[] { "Game.rfa", "Wake_003.rfa", "Wake.rfa" }, Leaves(ModWorkspace.LayersFor(Path.GetDirectoryName(levels)!)));
        var scan = GameMounts.Bf1942.Scan(levels);
        Assert.Equal(@"bf1942\levels\Wake_003.rfa", scan.Mounted[0].RelativePath);
        Assert.Equal(Path.Combine(levels, "old", "Wake.rfa"), Assert.Single(scan.Unmounted));
    }

    [Fact]
    public void An_Archives_folder_inside_a_mods_Archives_is_a_sub_folder_in_every_view()
    {
        // Mods\FHR\Archives\Archives\font.rfa is Archives\font.rfa to the engine, which mounts no such name. The mod on
        // its own, the whole game folder and the inner folder picked by itself must all say so.
        Mod("bf1942");
        var fhr = Mod("FHR");
        var inner = Rfa(fhr, "Archives/font.rfa");
        var objects = Rfa(fhr, "objects.rfa");

        Assert.Contains(inner, GameMounts.Bf1942.Scan(fhr).Unmounted);
        var whole = GameMounts.Bf1942.Scan(_root);
        Assert.Contains(inner, whole.Unmounted);
        Assert.DoesNotContain(inner, whole.Mounted.Select(a => a.Path));
        Assert.Contains(objects, whole.Mounted.Select(a => a.Path));
        var alone = GameMounts.Bf1942.Scan(Path.GetDirectoryName(inner)!);
        Assert.Empty(alone.Mounted);
        Assert.Equal(inner, Assert.Single(alone.Unmounted));
    }
}

/// <summary>The mount table against the retail files: the executables' own string tables, and the three resolutions
/// that were wrong before it (BF1942 mg42_r, BFV O_school, BF1942's 130 StandardMesh_001 overrides), plus BFV_WW2Mod's
/// unmounted standardMesh_001. Skipped with a reason where the clean installs are absent.</summary>
public class GameMountsInstallTests
{
    private static string[] Leaves(IEnumerable<string> paths) => paths.Select(p => Path.GetFileName(p)).ToArray();
    private static string Norm(string n) => n.Replace('\\', '/');

    /// <summary>The whitelist is not remembered: the archive names in the executable's own string table and the
    /// table's archives and mounted patches are the same set, both ways - an archive dropped from the table (aiMeshes,
    /// which carries BF1942 buildings) fails here just as one the executable never names does.</summary>
    [InstallFact(Installs.Bf1942Clean)]
    public void Bf1942_table_matches_the_executable() => TableMatchesExecutable(GameMounts.Bf1942, Installs.Bf1942Clean);

    [InstallFact(Installs.BfvOriginal)]
    public void Bfv_table_matches_the_executable() => TableMatchesExecutable(GameMounts.Bfv, Installs.BfvOriginal);

    private static void TableMatchesExecutable(GameMounts game, string root)
    {
        Assert.Equal(game.Game, GameMounts.Detect(root));
        var exe = Path.Combine(root, game.Executables[0]);
        Assert.True(File.Exists(exe), exe);
        var bytes = File.ReadAllBytes(exe);
        var text = Encoding.Latin1.GetString(bytes);
        foreach (var a in game.Archives)
        {
            var leaf = Path.GetFileName(a.RelativePath);
            Assert.True(text.Contains(leaf, StringComparison.OrdinalIgnoreCase), $"{game.Executables[0]} does not name {leaf}");
            var patch = a.Stem + "_001.rfa";
            Assert.True(a.PatchMounted == text.Contains(patch, StringComparison.OrdinalIgnoreCase),
                $"{game.Executables[0]}: {patch} named={text.Contains(patch, StringComparison.OrdinalIgnoreCase)}, table says {a.PatchMounted}");
        }
        Assert.DoesNotContain("objects_001.rfa", text, StringComparison.OrdinalIgnoreCase);

        // The other way: every archive the executable names is in the table. The mount list is its null-terminated
        // "<name>.rfa" strings; the only other .rfa strings are the Archives/objects.rfa fallback paths.
        var named = ExeArchiveNames(bytes).Where(n => !n.Contains("Archives/", StringComparison.OrdinalIgnoreCase))
                                          .Select(n => n.Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var table = game.Archives.Select(a => a.RelativePath)
                        .Concat(game.Archives.Select(a => a.PatchRelativePath).OfType<string>())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(named.Except(table, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(table.Except(named, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Every null-terminated printable string in a binary that ends in <c>.rfa</c>.</summary>
    private static List<string> ExeArchiveNames(byte[] bytes)
    {
        var names = new List<string>();
        var ext = ".rfa\0"u8;
        for (int i = 0; ;)
        {
            int k = bytes.AsSpan(i).IndexOf(ext);
            if (k < 0) return names;
            int dot = i + k, s = dot;
            while (s > 0 && bytes[s - 1] >= 0x20 && bytes[s - 1] < 0x7F) s--;
            if (s < dot && (s == 0 || bytes[s - 1] == 0)) names.Add(Encoding.ASCII.GetString(bytes, s, dot + 4 - s));
            i = dot + ext.Length;
        }
    }

    [InstallFact(Installs.Bf1942Clean)]
    public void Bf1942_mg42_r_resolves_from_texture_001_at_1024()
    {
        var chain = ModChain.ResolveByName(Installs.Bf1942Clean, "bf1942");
        Assert.Equal(RefractorGame.BF1942, chain.Game);
        var (_, tex) = ModChain.CollectArchives(chain);
        Assert.Equal(new[] { "texture_001.rfa", "texture.rfa" }, Leaves(tex));

        var lib = TextureLibrary.Open(tex);
        var copies = lib.AllCopies("texture/mg42_r");
        Assert.Equal("texture_001.rfa", Path.GetFileName(copies[0].Archive));   // the winner is listed first
        Assert.Equal(1024, copies[0].Width);
        Assert.Contains(copies, c => Path.GetFileName(c.Archive) == "texture.rfa" && c.Width == 256);   // the one it shadows
        Assert.Equal(1024, lib.Resolve("texture/mg42_r")!.Width);
    }

    [InstallFact(Installs.BfvOriginal)]
    public void Bfv_o_school_resolves_from_texture_001_at_1024()
    {
        foreach (var mod in new[] { "BfVietnam", "BFV_WW2Mod" })     // on its own, and under a mod that does not ship it
        {
            var chain = ModChain.ResolveByName(Installs.BfvOriginal, mod);
            Assert.Equal(RefractorGame.BFV, chain.Game);
            var (_, tex) = ModChain.CollectArchives(chain);
            var lib = TextureLibrary.Open(tex);
            var copies = lib.AllCopies("texture/o_school");
            Assert.Equal("texture_001.rfa", Path.GetFileName(copies[0].Archive));
            Assert.Equal(1024, copies[0].Width);
            Assert.Contains(copies, c => Path.GetFileName(c.Archive) == "texture.rfa" && c.Width == 512);
            Assert.Equal(1024, lib.Resolve("texture/o_school")!.Width);
        }
    }

    [InstallFact(Installs.Bf1942Clean)]
    public void Bf1942_StandardMesh_001_overrides_all_win()
    {
        var basePath = Path.Combine(Installs.Bf1942CleanArchives, "standardMesh.rfa");
        var patchPath = Path.Combine(Installs.Bf1942CleanArchives, "StandardMesh_001.rfa");
        var (mesh, _) = ModChain.CollectArchives(ModChain.ResolveByName(Installs.Bf1942Clean, "bf1942"));
        int ip = Array.FindIndex(mesh, p => p.Equals(patchPath, StringComparison.OrdinalIgnoreCase));
        int ib = Array.FindIndex(mesh, p => p.Equals(basePath, StringComparison.OrdinalIgnoreCase));
        Assert.True(ip >= 0 && ip < ib, $"StandardMesh_001 at {ip}, standardMesh at {ib}");

        var baseArc = new RefractorFlatArchive(basePath);
        var patch = new RefractorFlatArchive(patchPath);
        var baseNames = baseArc.Entries.Select(e => Norm(e.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = patch.Entries.Where(e => baseNames.Contains(Norm(e.Name))).ToList();
        Assert.Equal(130, overrides.Count);                          // 69 .sm + 61 .rs

        var lib = MeshLibrary.Open(mesh);
        var lost = new List<string>();
        foreach (var e in overrides)
        {
            var stem = Path.GetFileNameWithoutExtension(Norm(e.Name));
            var want = patch.Read(e);
            bool won = e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)
                ? lib.TryGetMeshBytes(stem, out _, out var sm) && sm.AsSpan().SequenceEqual(want)
                : lib.TryGetRsText(stem, out _, out var rs) && rs == Encoding.Latin1.GetString(want);
            if (!won) lost.Add(e.Name);
        }
        Assert.Empty(lost);
    }

    [InstallFact(Installs.BfvOriginal)]
    public void Bfv_WW2Mod_unmounted_standardMesh_001_is_skipped_and_does_not_win()
    {
        var chain = ModChain.ResolveByName(Installs.BfvOriginal, "BFV_WW2Mod");
        var arch = Path.Combine(Installs.BfvOriginal, "Mods", "BFV_WW2Mod", "Archives");
        var (mesh, _) = ModChain.CollectArchives(chain, skipNonAsset: false);
        Assert.DoesNotContain("standardMesh_001.rfa", Leaves(mesh), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(arch, "standardMesh_001.rfa"), ModChain.UnmountedArchives(chain), StringComparer.OrdinalIgnoreCase);

        // Its one entry, pacificfarm1_m2.rs, differs from the mod's mounted standardMesh.rfa copy - which is the one the
        // game draws with, so the one the library must hand back.
        var patch = new RefractorFlatArchive(Path.Combine(arch, "standardMesh_001.rfa"));
        var mounted = new RefractorFlatArchive(Path.Combine(arch, "standardMesh.rfa"));
        var e = Assert.Single(patch.Entries);
        var real = mounted.Entries.Single(x => Norm(x.Name).Equals(Norm(e.Name), StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(Encoding.Latin1.GetString(patch.Read(e)), Encoding.Latin1.GetString(mounted.Read(real)));

        var lib = MeshLibrary.Open(mesh);
        Assert.True(lib.TryGetRsText(Path.GetFileNameWithoutExtension(Norm(e.Name)), out _, out var text));
        Assert.Equal(Encoding.Latin1.GetString(mounted.Read(real)), text);
    }

    [InstallFact(Installs.Bf1942Clean)]
    public void Every_archive_collected_from_a_clean_Bf1942_mod_is_mounted() => AllCollectedAreMounted(Installs.Bf1942Clean, RefractorGame.BF1942);

    [InstallFact(Installs.BfvOriginal)]
    public void Every_archive_collected_from_a_clean_Bfv_mod_is_mounted() => AllCollectedAreMounted(Installs.BfvOriginal, RefractorGame.BFV);

    private static void AllCollectedAreMounted(string root, RefractorGame game)
    {
        var table = GameMounts.For(game);
        var mods = Directory.EnumerateDirectories(Path.Combine(root, "Mods")).Where(d => Directory.Exists(Path.Combine(d, "Archives"))).ToList();
        Assert.NotEmpty(mods);
        foreach (var modDir in mods)
        {
            // Nothing real is dropped: the one archive in either clean install the executable never reads is
            // BFV_WW2Mod's standardMesh_001. A table missing aiMeshes, ai, font, shaders or animations fails here.
            var expected = Path.GetFileName(modDir).Equals("BFV_WW2Mod", StringComparison.OrdinalIgnoreCase)
                ? new[] { Path.Combine(modDir, "Archives", "standardMesh_001.rfa") } : Array.Empty<string>();
            Assert.Equal(expected, table.Scan(modDir).Unmounted.ToArray(), StringComparer.OrdinalIgnoreCase);

            var chain = ModChain.Resolve(root, modDir);
            Assert.Equal(game, chain.Game);
            var (mesh, tex) = ModChain.CollectArchives(chain, skipNonAsset: false);
            var layers = ModWorkspace.LayersForChain(chain).Select(l => l.Path);
            foreach (var p in mesh.Concat(tex).Concat(layers))
            {
                var arch = chain.Mounts.Select(m => Path.Combine(m.Path, "Archives"))
                                .First(a => p.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Assert.True(table.IsMounted(Path.GetRelativePath(arch, p)), $"{Path.GetFileName(modDir)}: {p} is not mounted by {game}");
            }
            // A patch never follows its own base.
            foreach (var list in new[] { mesh, tex })
                for (int i = 0; i < list.Length; i++)
                {
                    var stem = Path.GetFileNameWithoutExtension(list[i]);
                    if (!stem.EndsWith("_001", StringComparison.OrdinalIgnoreCase)) continue;
                    var basePath = Path.Combine(Path.GetDirectoryName(list[i])!, stem[..^4] + ".rfa");
                    int ib = Array.FindIndex(list, x => x.Equals(basePath, StringComparison.OrdinalIgnoreCase));
                    Assert.True(ib < 0 || ib > i, $"{list[i]} must outrank {basePath}");
                }
        }
    }
}
