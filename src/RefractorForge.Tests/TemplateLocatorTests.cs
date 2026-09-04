using RefractorForge.Formats.Validation;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A map merged from one mod into another drags its objects' NAMES along, not the objects. The check that says
/// "missing template" now also says where the template actually is and what to do - or that it is nowhere.
/// </summary>
public class TemplateLocatorTests
{
    // A fake Mods folder: three mods, two of which carry object archives, with entries handed in rather than read.
    private static (string Dir, Func<string, IEnumerable<string>> Entries) FakeMods()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf_mods_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "EoD", "archives"));
        Directory.CreateDirectory(Path.Combine(root, "Battlegroup42", "Archives"));
        Directory.CreateDirectory(Path.Combine(root, "echo", "archives"));
        File.WriteAllBytes(Path.Combine(root, "EoD", "archives", "objects.rfa"), new byte[0]);
        File.WriteAllBytes(Path.Combine(root, "EoD", "archives", "objects_001.rfa"), new byte[0]);
        File.WriteAllBytes(Path.Combine(root, "Battlegroup42", "Archives", "Objects.rfa"), new byte[0]);
        File.WriteAllBytes(Path.Combine(root, "echo", "archives", "objects.rfa"), new byte[0]);
        IEnumerable<string> Entries(string arch)
        {
            var name = Path.GetFileName(arch).ToLowerInvariant();
            var mod = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(arch))!);
            if (mod == "EoD" && name == "objects.rfa")
                return new[] { "objects/Statics/stebarrel1_m1/Objects.con", "objects/Statics/stebarrel1_m1/Geometries.con", "objects/Statics/stebarrel1_m1/Ai/Objects.con" };
            if (mod == "EoD" && name == "objects_001.rfa")
                return new[] { @"objects\Statics\planeeng_m1\Objects.con" };        // backslashes, as some packers write them
            if (mod == "Battlegroup42")
                return new[] { "objects/Statics/stebarrel1_m1/Objects.con", "objects/Statics/rubble03_m1/Objects.con" };
            return Array.Empty<string>();                                            // echo has nothing of its own
        }
        return (root, Entries);
    }

    [Fact]
    public void A_template_is_found_in_every_mod_that_carries_it()
    {
        var (dir, entries) = FakeMods();
        try
        {
            var idx = TemplateLocator.IndexMods(dir, entries);
            Assert.True(idx.ContainsKey("stebarrel1_m1"));
            Assert.Equal(new[] { "Battlegroup42", "EoD" }, idx["stebarrel1_m1"].OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "EoD" }, idx["planeeng_m1"].ToArray());                 // from a patch archive, backslash paths
            Assert.Equal(new[] { "Battlegroup42" }, idx["rubble03_m1"].ToArray());
            Assert.False(idx.ContainsKey("Ai"));                                          // an AI sub-folder is not a template
            Assert.False(idx.ContainsKey("milichair_m1"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Lookup_is_case_insensitive_like_the_game()
    {
        var (dir, entries) = FakeMods();
        try
        {
            var idx = TemplateLocator.IndexMods(dir, entries);
            Assert.True(idx.ContainsKey("STEBARREL1_M1"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Both_casings_of_the_archives_folder_are_searched()
    {
        var (dir, _) = FakeMods();
        try
        {
            Assert.Contains(TemplateLocator.ObjectArchives(Path.Combine(dir, "Battlegroup42")), a => a.EndsWith("Objects.rfa"));
            Assert.Equal(2, TemplateLocator.ObjectArchives(Path.Combine(dir, "EoD")).Count());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_advice_names_the_mod_and_the_line_to_add()
    {
        var a = TemplateLocator.Advice("stebarrel1_m1", new[] { "EoD", "Battlegroup42" }, "echo");
        Assert.Contains("game.addModPath Mods/EoD/", a);
        Assert.Contains("Mods/echo/init.con", a);
        Assert.Contains("Battlegroup42", a);
    }

    [Fact]
    public void A_template_nowhere_says_it_has_to_be_ported()
    {
        var a = TemplateLocator.Advice("milichair_m1", Array.Empty<string>(), "echo");
        Assert.Contains("ported", a);
        Assert.DoesNotContain("addModPath", a);
    }

    [Fact]
    public void A_missing_mods_folder_is_simply_empty()
    {
        Assert.Empty(TemplateLocator.IndexMods(Path.Combine(Path.GetTempPath(), "rf_no_such_" + Guid.NewGuid().ToString("N"))));
    }
}
