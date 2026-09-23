using System.Text;
using RefractorForge.Formats;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using RefractorForge.Viewer;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The archives the editor's own load path hands its first-wins mesh and texture libraries (<see cref="LibraryArchives"/>).
/// ModChain.CollectArchives already listed a chain in engine order, but the editor then undid it: opening a level
/// directly globbed each mod's Archives\ in directory order (texture.rfa ahead of texture_001.rfa, standardMesh.rfa ahead
/// of StandardMesh_001.rfa), and every picked mesh archive pulled in all its numbered siblings behind it - so an
/// unmounted BFV standardMesh_001, an objects_001 or a _002 was back, ranked above every lower mod.
/// </summary>
public class LibraryArchivesTests : IDisposable
{
    private readonly string _root;

    public LibraryArchivesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rf_libarc_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string Rfa(string modDir, string rel, params (string Name, string Text)[] entries)
    {
        var p = Path.Combine(modDir, "Archives", rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var list = entries.Length > 0 ? entries : new[] { ("readme/" + Path.GetFileNameWithoutExtension(p) + ".txt", "x") };
        RefractorFlatArchive.WriteFile(p, list.Select(e => (e.Item1, Encoding.Latin1.GetBytes(e.Item2))).ToList(), compress: false, XPackId.None);
        return p;
    }

    private void Exe(string name) => File.WriteAllBytes(Path.Combine(_root, name), new byte[] { 0 });

    private static string[] Leaves(IEnumerable<string> paths) => paths.Select(p => Path.GetFileName(p)).ToArray();

    private static string Tex(string[] tex, string name) => Encoding.Latin1.GetString(TextureLibrary.Open(tex).ResolveRaw(name)!.Value.Bytes);

    private static string Mesh(string[] mesh, string name)
    {
        Assert.True(MeshLibrary.Open(mesh).TryGetMeshBytes(name, out _, out var bytes), name);
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>The BF1942 base mod: both mounted patches, plus two archives the executable never reads.</summary>
    private string Bf1942Base()
    {
        Exe("BF1942.exe");
        var b = Mod("bf1942");
        Rfa(b, "texture.rfa", ("texture/gun.dds", "BASE-GUN"));
        Rfa(b, "texture_001.rfa", ("texture/gun.dds", "PATCH-GUN"));
        Rfa(b, "texture_002.rfa", ("texture/gun.dds", "NEVER-GUN"));
        Rfa(b, "standardMesh.rfa", ("standardMesh/hut.sm", "BASE-HUT"));
        Rfa(b, "StandardMesh_001.rfa", ("standardMesh/hut.sm", "PATCH-HUT"));
        Rfa(b, "objects_001.rfa", ("standardMesh/hut.sm", "NEVER-HUT"));
        Rfa(b, "bf1942/levels/Berlin.rfa", ("bf1942/levels/Berlin/Init.con", "rem"));
        return b;
    }

    [Fact]
    public void A_level_opened_directly_gets_its_mod_chain_in_engine_order()
    {
        var b = Bf1942Base();
        var level = Path.Combine(b, "Archives", "bf1942", "levels", "Berlin.rfa");

        var (mesh, tex) = LibraryArchives.For(Array.Empty<string>(), Array.Empty<string>(), new[] { level }, level,
                                              Path.GetDirectoryName(level), includeInherited: true);

        Assert.Equal(new[] { "Berlin.rfa", "texture_001.rfa", "texture.rfa" }, Leaves(tex));
        Assert.Equal("PATCH-GUN", Tex(tex, "texture/gun"));
        Assert.True(Array.IndexOf(Leaves(mesh), "StandardMesh_001.rfa") < Array.IndexOf(Leaves(mesh), "standardMesh.rfa"));
        Assert.Equal("PATCH-HUT", Mesh(mesh, "hut"));
        Assert.DoesNotContain("objects_001.rfa", Leaves(mesh));
        Assert.DoesNotContain("texture_002.rfa", Leaves(tex));
    }

    [Fact]
    public void Hand_picked_archives_get_their_mounted_patch_ahead_and_no_unmounted_sibling()
    {
        // The classic Open Level: the user multi-selects archives, which a file dialog hands back alphabetically -
        // base before patch - and the level is a folder with no mod around it.
        var b = Bf1942Base();
        var arch = Path.Combine(b, "Archives");
        var folder = Path.Combine(_root, "Extracted", "Berlin");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Terrain.con"), "rem");

        var (mesh, tex) = LibraryArchives.For(new[] { Path.Combine(arch, "standardMesh.rfa") },
                                              new[] { Path.Combine(arch, "texture.rfa"), Path.Combine(arch, "texture_001.rfa") },
                                              Array.Empty<string>(), folder, folder, includeInherited: true);

        Assert.Equal(new[] { "StandardMesh_001.rfa", "standardMesh.rfa", "Berlin" }, Leaves(mesh));
        Assert.Equal(new[] { "Berlin", "texture_001.rfa", "texture.rfa" }, Leaves(tex));
        Assert.Equal("PATCH-HUT", Mesh(mesh, "hut"));
        Assert.Equal("PATCH-GUN", Tex(tex, "texture/gun"));
    }

    [Fact]
    public void An_opened_mod_keeps_its_chain_and_an_unmounted_bfv_standardMesh_001_stays_out()
    {
        Exe("BfVietnam.exe");
        var v = Mod("BfVietnam");
        Rfa(v, "standardMesh.rfa", ("standardMesh/shed.sm", "BASE-SHED"));
        Rfa(v, "texture.rfa", ("texture/o_school.dds", "BASE-O"));
        Rfa(v, "texture_001.rfa", ("texture/o_school.dds", "PATCH-O"));
        Rfa(v, "animations.rfa", ("animations/soldier.ske", "BASE-SKE"));
        Rfa(v, "animations_001.rfa", ("animations/soldier.ske", "PATCH-SKE"));
        var ww2 = Mod("WW2", "game.addModPath Mods/WW2/", "game.addModPath Mods/BfVietnam/");
        Rfa(ww2, "standardMesh.rfa", ("standardMesh/hut.sm", "MOD-HUT"));
        Rfa(ww2, "standardMesh_001.rfa", ("standardMesh/shed.sm", "NEVER-SHED"));
        Rfa(ww2, "objects_001.rfa", ("standardMesh/hut.sm", "NEVER-HUT"));
        var level = Rfa(ww2, "BfVietnam/levels/Wake.rfa", ("BfVietnam/levels/Wake/Init.con", "rem"));

        // File > Open Mod: the chain's own lists become the picks, and the level is opened from the mod.
        var chain = ModChain.Resolve(_root, ww2);
        var (picksMesh, picksTex) = ModChain.CollectArchives(chain);
        var (mesh, tex) = LibraryArchives.For(picksMesh, picksTex, new[] { level }, level, Path.GetDirectoryName(level), includeInherited: true);

        Assert.DoesNotContain("standardMesh_001.rfa", Leaves(mesh), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("objects_001.rfa", Leaves(mesh), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("BASE-SHED", Mesh(mesh, "shed"));      // the base game's, not the unmounted file's
        Assert.Equal("MOD-HUT", Mesh(mesh, "hut"));
        Assert.Equal("PATCH-O", Tex(tex, "texture/o_school"));
        // Skinned meshes are rest-posed from the animations archives, so they stay in - patch first.
        var leaves = Leaves(mesh);
        Assert.True(Array.IndexOf(leaves, "animations_001.rfa") >= 0
                    && Array.IndexOf(leaves, "animations_001.rfa") < Array.IndexOf(leaves, "animations.rfa"));

        // Opened directly instead, the same level finds the same chain.
        var (direct, directTex) = LibraryArchives.For(Array.Empty<string>(), Array.Empty<string>(), new[] { level }, level,
                                                      Path.GetDirectoryName(level), includeInherited: true);
        Assert.DoesNotContain("standardMesh_001.rfa", Leaves(direct), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("BASE-SHED", Mesh(direct, "shed"));
        Assert.Equal("PATCH-O", Tex(directTex, "texture/o_school"));
    }
}

/// <summary>The editor's load path against the clean installs: a retail level opened directly, and a mod opened with
/// File > Open Mod, resolve exactly what the game draws. Skipped with a reason where the installs are absent.</summary>
public class LibraryArchivesInstallTests
{
    private static string[] Leaves(IEnumerable<string> paths) => paths.Select(p => Path.GetFileName(p)).ToArray();
    private static string Norm(string n) => n.Replace('\\', '/');

    private static (string[] Mesh, string[] Tex) OpenLevel(string levelRfa, string[]? meshPicks = null, string[]? texPicks = null)
    {
        var levels = LevelSaver.WithPatchArchives(new[] { levelRfa });     // what the editor's load path mounts
        return LibraryArchives.For(meshPicks ?? Array.Empty<string>(), texPicks ?? Array.Empty<string>(), levels, levelRfa,
                                   Path.GetDirectoryName(levelRfa), includeInherited: true);
    }

    [InstallFact(Installs.Bf1942Clean)]
    public void A_Bf1942_level_opened_directly_draws_mg42_r_at_1024_and_every_StandardMesh_001_override()
    {
        var (mesh, tex) = OpenLevel(Path.Combine(Installs.Bf1942CleanArchives, "bf1942", "levels", "Berlin.rfa"));

        var lib = TextureLibrary.Open(tex);
        var copies = lib.AllCopies("texture/mg42_r");
        Assert.Equal("texture_001.rfa", Path.GetFileName(copies[0].Archive));
        Assert.Equal(1024, lib.Resolve("texture/mg42_r")!.Width);

        var patch = new RefractorFlatArchive(Path.Combine(Installs.Bf1942CleanArchives, "StandardMesh_001.rfa"));
        var baseNames = new RefractorFlatArchive(Path.Combine(Installs.Bf1942CleanArchives, "standardMesh.rfa"))
            .Entries.Select(e => Norm(e.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = patch.Entries.Where(e => baseNames.Contains(Norm(e.Name))).ToList();
        Assert.Equal(130, overrides.Count);
        var meshes = MeshLibrary.Open(mesh);
        var lost = overrides.Where(e =>
        {
            var stem = Path.GetFileNameWithoutExtension(Norm(e.Name));
            var want = patch.Read(e);
            return !(e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)
                ? meshes.TryGetMeshBytes(stem, out _, out var sm) && sm.AsSpan().SequenceEqual(want)
                : meshes.TryGetRsText(stem, out _, out var rs) && rs == Encoding.Latin1.GetString(want));
        }).Select(e => e.Name).ToList();
        Assert.Empty(lost);
    }

    [InstallFact(Installs.BfvOriginal)]
    public void Bfv_WW2Mod_opened_either_way_leaves_its_standardMesh_001_out_and_draws_o_school_at_1024()
    {
        var root = Installs.BfvOriginal;
        var arch = Path.Combine(root, "Mods", "BFV_WW2Mod", "Archives");
        var level = Path.Combine(arch, "BfVietnam", "Levels", "Wake.rfa");
        var chain = ModChain.ResolveByName(root, "BFV_WW2Mod");
        var (picksMesh, picksTex) = ModChain.CollectArchives(chain);

        var mounted = new RefractorFlatArchive(Path.Combine(arch, "standardMesh.rfa"));
        var farm = mounted.Entries.Single(e => Norm(e.Name).EndsWith("/pacificfarm1_m2.rs", StringComparison.OrdinalIgnoreCase));
        foreach (var (mesh, tex) in new[] { OpenLevel(level), OpenLevel(level, picksMesh, picksTex) })
        {
            Assert.DoesNotContain("standardMesh_001.rfa", Leaves(mesh), StringComparer.OrdinalIgnoreCase);
            Assert.True(MeshLibrary.Open(mesh).TryGetRsText("pacificfarm1_m2", out _, out var rs));
            Assert.Equal(Encoding.Latin1.GetString(mounted.Read(farm)), rs);
            var lib = TextureLibrary.Open(tex);
            Assert.Equal("texture_001.rfa", Path.GetFileName(lib.AllCopies("texture/o_school")[0].Archive));
            Assert.Equal(1024, lib.Resolve("texture/o_school")!.Width);
        }
    }
}
