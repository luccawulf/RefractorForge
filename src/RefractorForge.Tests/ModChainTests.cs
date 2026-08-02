using RefractorForge.Formats;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Mod mount-chain resolution. Cases are modelled on REAL chains found in a live BF1942 install:
///   FHSW  -> "game.addmodPath Mods/FHSW/" (itself, lowercase verb) + Mods/FH/ + Mods/Bf1942/  (case differs from disk)
///   FCD   -> 7 mounts deep;  HTroop -> 12 mounts deep, including the NESTED mount "Mods/HT_Data/EoD"
///   Ballistik_FH -> mounts its own sub-folder "Mods/Ballistik_FH/X_Flow";  NorwegianRes -> "Mods/fh" (case)
/// </summary>
public class ModChainTests : IDisposable
{
    private readonly string _root;

    public ModChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rf_modchain_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "Mods"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Create Mods\&lt;name&gt; (name may be a sub-path like "HT_Data/EoD") with an optional init.con.</summary>
    private string Mod(string name, params string[] initConLines)
    {
        var dir = Path.Combine(_root, "Mods", name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "Archives"));
        if (initConLines.Length > 0) File.WriteAllLines(Path.Combine(dir, "init.con"), initConLines);
        return dir;
    }

    private static string[] Names(ModChainResult r) => r.Mounts.Select(m => m.Name).ToArray();

    [Fact]
    public void Fhsw_style_chain_resolves_in_order_without_self_duplication()
    {
        Mod("bf1942");
        Mod("FH", "game.addModPath Mods/FH/", "game.addModPath Mods/BF1942/");
        var fhsw = Mod("FHSW",
            "game.SetCustomGameName FHSW",
            "game.addmodPath Mods/FHSW/",     // lowercase verb + SELF reference, exactly as shipped
            "game.addModPath Mods/FH/",
            "game.addModPath Mods/Bf1942/",   // case differs from the real folder "bf1942"
            "game.customGameFlushArchives 0");

        var r = ModChain.Resolve(_root, fhsw);

        Assert.Equal(new[] { "FHSW", "FH", "bf1942" }, Names(r));
        Assert.All(r.Mounts, m => Assert.True(m.Listed, "every FHSW mount is explicitly listed"));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Mini_mod_inherits_the_dependency_its_author_forgot_to_list()
    {
        // THE REPORTED BUG: a mini-mod lists only itself + FHSW. FH (~3 GB of objects) is never named, so one-level
        // parsing silently loses it and objects fail to resolve.
        Mod("bf1942");
        Mod("FH", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");
        Mod("FHSW", "game.addModPath Mods/FHSW/", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");
        var mini = Mod("MyMiniMod", "game.addModPath Mods/MyMiniMod/", "game.addModPath Mods/FHSW/");

        var withInherit = ModChain.Resolve(_root, mini);
        Assert.Contains("FH", Names(withInherit));                       // <- the fix
        Assert.Equal(new[] { "MyMiniMod", "FHSW", "FH", "bf1942" }, Names(withInherit));
        Assert.True(withInherit.Mounts.Single(m => m.Name == "FH").Listed == false, "FH is INHERITED, not listed");

        // Opting out reproduces the old one-level behaviour (base game still appended as a safety net).
        var oneLevel = ModChain.Resolve(_root, mini, includeInherited: false);
        Assert.DoesNotContain("FH", Names(oneLevel));
    }

    [Fact]
    public void Inherited_dependency_outranks_the_base_game()
    {
        // REGRESSION: the natural mini-mod init.con names the base game explicitly:
        //     [MyMod, FHSW, Bf1942]
        // FHSW pulls in FH. If FH were appended after the whole explicit list it would sit BELOW bf1942, and because
        // the mesh/texture libraries are first-wins, vanilla bf1942 meshes would shadow FH's replacements.
        // FH must land between FHSW and bf1942.
        Mod("bf1942");
        Mod("FH", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");
        Mod("FHSW", "game.addModPath Mods/FHSW/", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");
        var mini = Mod("MyMod", "game.addModPath Mods/MyMod/", "game.addModPath Mods/FHSW/", "game.addModPath Mods/Bf1942/");

        var r = ModChain.Resolve(_root, mini);

        Assert.Equal(new[] { "MyMod", "FHSW", "FH", "bf1942" }, Names(r));
        var names = Names(r);
        Assert.True(Array.IndexOf(names, "FH") < Array.IndexOf(names, "bf1942"),
                    "FH must outrank the base game or vanilla assets shadow it");
    }

    [Fact]
    public void Explicit_list_stays_authoritative_inherited_is_appended_last()
    {
        // Real hazard: FHSW0.42's init.con points at Mods/FHSW/ — a DIFFERENT (0.73) folder. Inheriting it must
        // never outrank what the author actually listed, or the editor shows objects the game will not mount.
        Mod("bf1942");
        Mod("FH", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");
        Mod("FHSW", "game.addModPath Mods/FHSW/", "game.addModPath Mods/FH/");      // the 0.73 build
        Mod("FHSW042", "game.addModPath Mods/FHSW042/", "game.addModPath Mods/FHSW/", "game.addModPath Mods/FH/");
        var fcd = Mod("FCD", "game.addModPath Mods/FCD/", "game.addModPath Mods/FHSW042/", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");

        var r = ModChain.Resolve(_root, fcd);
        var names = Names(r);

        // The author's explicit mods keep their exact order and outrank everything inherited...
        Assert.Equal(new[] { "FCD", "FHSW042", "FH" }, names.Take(3).ToArray());
        // ...so the inherited DIFFERENT-VERSION mod can only fill gaps: below every explicit mod, and above just
        // the base game (which must always remain the last resort).
        Assert.Equal(new[] { "FHSW", "bf1942" }, names.TakeLast(2).ToArray());
        Assert.False(r.Mounts.Single(m => m.Name == "FHSW").Listed);
        Assert.True(Array.IndexOf(names, "FHSW") > Array.IndexOf(names, "FH"), "an unlisted version never outranks an explicit mount");
    }

    [Fact]
    public void Nested_and_subfolder_mounts_resolve()
    {
        // HTroop mounts "Mods/HT_Data/EoD"; Ballistik_FH mounts its own sub-folder "Mods/Ballistik_FH/X_Flow".
        Mod("bf1942");
        Mod("HT_Data/EoD");
        Mod("Ballistik_FH/X_Flow");
        var b = Mod("Ballistik_FH",
            "game.addModPath Mods/Ballistik_FH/X_Flow",
            "game.addModPath Mods/Ballistik_FH",
            "game.addModPath Mods/HT_Data/EoD",
            "game.addModPath Mods/bf1942");

        var r = ModChain.Resolve(_root, b);
        Assert.Equal(new[] { "Ballistik_FH", "X_Flow", "EoD", "bf1942" }, Names(r));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Cycles_are_safe()
    {
        Mod("bf1942");
        Mod("A", "game.addModPath Mods/A/", "game.addModPath Mods/B/");
        Mod("B", "game.addModPath Mods/B/", "game.addModPath Mods/A/");   // mutual reference

        var r = ModChain.Resolve(_root, Path.Combine(_root, "Mods", "A"));
        Assert.Equal(new[] { "A", "B", "bf1942" }, Names(r));
        Assert.Equal(r.Mounts.Count, r.Mounts.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Syntax_variety_is_tolerated()
    {
        Mod("bf1942");
        Mod("Dep");
        var m = Mod("Weird",
            "rem this is a comment line",
            "// another comment",
            "; and another",
            "GAME.ADDMODPATH \"Mods/Dep/\"",              // upper-case verb + quoted + trailing slash
            "game.addmodpath Mods\\bf1942",               // lowercase verb + backslash + no trailing slash
            "game.addModPath   Mods/Dep/   // trailing comment",
            "game.setCustomGameName Weird");
        var r = ModChain.Resolve(_root, m);
        Assert.Equal(new[] { "Weird", "Dep", "bf1942" }, Names(r));
    }

    [Fact]
    public void Missing_dependencies_are_reported_not_swallowed()
    {
        Mod("bf1942");
        var m = Mod("NeedsMissing", "game.addModPath Mods/NeedsMissing/", "game.addModPath Mods/NotInstalledMod/");
        var r = ModChain.Resolve(_root, m);

        Assert.DoesNotContain("NotInstalledMod", Names(r));
        Assert.Contains(r.Missing, s => s.Contains("NotInstalledMod", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mod_without_init_con_still_gets_the_base_game()
    {
        Mod("bf1942");
        var solo = Mod("Solo");   // no init.con at all
        var r = ModChain.Resolve(_root, solo);
        Assert.Equal(new[] { "Solo", "bf1942" }, Names(r));
    }

    [Fact]
    public void Deep_chain_resolves_completely()
    {
        Mod("bf1942");
        for (int i = 1; i <= 11; i++) Mod("M" + i);
        var lines = new List<string> { "game.addModPath Mods/Deep/" };
        for (int i = 1; i <= 11; i++) lines.Add($"game.addModPath Mods/M{i}/");
        lines.Add("game.addModPath Mods/bf1942/");
        var deep = Mod("Deep", lines.ToArray());

        var r = ModChain.Resolve(_root, deep);
        Assert.Equal(13, r.Mounts.Count);                       // Deep + M1..M11 + bf1942
        Assert.Equal("Deep", r.Mounts[0].Name);
        Assert.Equal("bf1942", r.Mounts[^1].Name);
    }

    [Fact]
    public void CollectArchives_preserves_precedence_and_filters()
    {
        Mod("bf1942");
        var fh = Mod("FH", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");
        var fhsw = Mod("FHSW", "game.addModPath Mods/FHSW/", "game.addModPath Mods/FH/", "game.addModPath Mods/bf1942/");

        void Rfa(string modDir, string rel) {
            var p = Path.Combine(modDir, "Archives", rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[] { 0 });
        }
        Rfa(fhsw, "objects.rfa"); Rfa(fhsw, "texture.rfa"); Rfa(fhsw, "sound.rfa");
        Rfa(fhsw, "bf1942/levels/SomeMap.rfa");                 // a level archive -> excluded
        Rfa(fh, "objects.rfa"); Rfa(fh, "standardmesh.rfa");

        var (mesh, tex) = ModChain.CollectArchives(ModChain.Resolve(_root, fhsw));

        Assert.DoesNotContain(mesh, p => p.Contains("levels", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mesh, p => p.EndsWith("sound.rfa", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tex, p => p.EndsWith("texture.rfa", StringComparison.OrdinalIgnoreCase));
        // FHSW's objects.rfa must come before FH's — the libraries are FIRST-WINS, so precedence is the order.
        int iFhsw = Array.FindIndex(mesh, p => p.Contains("FHSW", StringComparison.OrdinalIgnoreCase) && p.EndsWith("objects.rfa", StringComparison.OrdinalIgnoreCase));
        int iFh = Array.FindIndex(mesh, p => p.Contains($"Mods{Path.DirectorySeparatorChar}FH{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) && p.EndsWith("objects.rfa", StringComparison.OrdinalIgnoreCase));
        Assert.True(iFhsw >= 0 && iFh >= 0 && iFhsw < iFh, $"FHSW objects.rfa ({iFhsw}) must outrank FH's ({iFh})");
    }

    [Fact]
    public void FindGameRoot_walks_up_to_the_install_dir()
    {
        var fhsw = Mod("FHSW");
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(ModChain.FindGameRoot(Path.Combine(fhsw, "Archives", "objects.rfa"))!));
        Assert.Null(ModChain.FindGameRoot(Path.GetTempPath()));
    }
}
