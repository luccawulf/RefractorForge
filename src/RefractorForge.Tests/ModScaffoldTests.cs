using RefractorBridge.Oracle;
using RefractorForge.Formats;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>A new mod's init.con must be in its own game's dialect: the lines are compared against the shipped
/// init.con files, and (with an install) every verb against that game's own console-property table.</summary>
public class ModScaffoldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modscaffold_" + Guid.NewGuid().ToString("N"));
    public ModScaffoldTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ModScaffold.Spec Spec(bool vietnam, params string[] bases)
        => new("MyMod", "My Mod", "0.1", "https://example.org", vietnam, bases);

    [Fact]
    public void Bf1942_init_con_uses_the_retail_set_forms_and_music()
    {
        var text = ModScaffold.InitCon(Spec(false));
        Assert.Contains("game.CustomGameName My Mod\r\n", text);
        Assert.Contains("game.addModPath Mods/MyMod/\r\ngame.addModPath Mods/bf1942/\r\n", text);
        Assert.Contains("game.setCustomGameVersion 0.1\r\n", text);
        Assert.Contains("game.setCustomGameUrl \"https://example.org\"\r\n", text);
        Assert.Contains("Game.setMenuMusicFilename \"music/slaughter4.bik\"", text);
        Assert.DoesNotContain("menumusic", text);
    }

    [Fact]
    public void Vietnam_init_con_drops_set_on_custom_game_verbs_and_uses_its_music()
    {
        var text = ModScaffold.InitCon(Spec(true) with { Info = "Hello" });
        Assert.Contains("game.addModPath Mods/MyMod/\r\ngame.addModPath Mods/BfVietnam/\r\n", text);
        Assert.Contains("game.CustomGameVersion 0.1\r\n", text);
        Assert.Contains("game.CustomGameUrl \"https://example.org\"\r\n", text);
        Assert.Contains("game.CustomGameInfo \"Hello\"\r\n", text);
        Assert.DoesNotContain("setCustomGame", text);
        Assert.Contains("Game.setMenuMusicFilename \"music/menumusic\"\r\n", text);
        Assert.Contains("rem Game.setLoadMusicFilename", text);
    }

    [Fact]
    public void Base_mods_are_listed_in_order()
    {
        var text = ModScaffold.InitCon(Spec(false, "XPack1", "bf1942"));
        int x = text.IndexOf("Mods/XPack1/", StringComparison.Ordinal), b = text.IndexOf("Mods/bf1942/", StringComparison.Ordinal);
        Assert.True(x > 0 && b > x);
        Assert.DoesNotContain("customGameFlushArchives", text);   // registered by neither executable
    }

    [Fact]
    public void Created_mod_round_trips_through_ModChain()
    {
        var root = Path.Combine(_dir, "game");
        Directory.CreateDirectory(Path.Combine(root, "Mods", "BfVietnam"));
        var made = ModScaffold.Create(root, Spec(true));
        var modDir = Path.Combine(root, "Mods", "MyMod");
        Assert.Contains(Path.Combine(modDir, "Archives", "BfVietnam", "levels"), made);
        var mounts = ModChain.ReadModPaths(root, modDir);
        Assert.Equal(2, mounts.Count);
        Assert.EndsWith("MyMod", mounts[0]);
        Assert.EndsWith("BfVietnam", mounts[1], StringComparison.OrdinalIgnoreCase);
    }

    [InstallFact(Installs.Bf1942Clean + @"\BF1942.exe", Installs.BfvOriginal + @"\BfVietnam.exe")]
    public void Every_custom_game_verb_exists_in_its_games_executable()
    {
        foreach (var (exe, vietnam) in new[] { (Installs.Bf1942Clean + @"\BF1942.exe", false), (Installs.BfvOriginal + @"\BfVietnam.exe", true) })
        {
            if (!File.Exists(exe)) continue;
            var table = ExeSymbolTable.Load(exe);
            var text = ModScaffold.InitCon(Spec(vietnam) with { Info = "x" });
            foreach (var line in text.Split("\r\n"))
            {
                if (line.Length == 0 || line.StartsWith("rem ")) continue;
                var leaf = ExeSymbolTable.Leaf(line.Split(' ')[0]);  // e.g. CustomGameVersion
                // Vietnam must name the registered property itself; BF1942 also takes its set-twin.
                bool ok = table.HasProperty(leaf)
                          || (!vietnam && leaf.StartsWith("set") && table.HasProperty(ExeSymbolTable.SetTwinLeaf(leaf)));
                Assert.True(ok, $"{Path.GetFileName(exe)} registers no '{leaf}'");
            }
        }
    }
}
