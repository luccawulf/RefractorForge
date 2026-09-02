namespace RefractorForge.Formats;

/// <summary>
/// A new, empty mod the game will list: the folder under <c>Mods/</c>, an <c>init.con</c> in the shape the retail
/// one uses, and the archive folders a level or object archive is expected in. What the MDT's ModWizard made,
/// with the lines taken from the shipped <c>Mods/bf1942/init.con</c> rather than remembered.
/// </summary>
public static class ModScaffold
{
    public sealed record Spec(string Name, string DisplayName, string Version, string Url, bool IsVietnam,
                              IReadOnlyList<string> BaseMods);

    public static string InitCon(Spec s)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("game.CustomGameName ").Append(s.DisplayName.Length > 0 ? s.DisplayName : s.Name).Append("\r\n");
        sb.Append("game.addModPath Mods/").Append(s.Name).Append("/\r\n");
        foreach (var b in s.BaseMods) sb.Append("game.addModPath Mods/").Append(b).Append("/\r\n");
        sb.Append("game.setCustomGameVersion ").Append(s.Version.Length > 0 ? s.Version : "1.0").Append("\r\n");
        if (s.Url.Length > 0) sb.Append("game.setCustomGameUrl \"").Append(s.Url).Append("\"\r\n");
        sb.Append("\r\n");
        // The music lines every retail init.con carries; the files come from the base mod's music.rfa.
        sb.Append("Game.setMenuMusicFilename \"music/slaughter4.bik\"\r\n");
        sb.Append("Game.setLoadMusicFilename \"music/vehicle4.bik\"\r\n");
        sb.Append("Game.setWinMusicFilename \"music/vehicle3.bik\"\r\n");
        sb.Append("Game.setLoseMusicFilename \"music/menu.bik\"\r\n");
        sb.Append("Game.setCampaignLoseMusicFilename \"music/theme2.bik\"\r\n");
        sb.Append("Game.setDebriefingMusicFilename \"music/briefing.bik\"\r\n");
        return sb.ToString();
    }

    /// <summary>Create the mod. Refuses to touch a folder that already exists. Returns every path made.</summary>
    public static List<string> Create(string gameRoot, Spec s)
    {
        var name = Sanitize(s.Name);
        if (name.Length == 0) throw new ArgumentException("A mod needs a name.");
        var modDir = Path.Combine(gameRoot, "Mods", name);
        if (Directory.Exists(modDir)) throw new IOException($"A mod folder already exists: {modDir}");

        var made = new List<string>();
        string sub = s.IsVietnam ? "bfvietnam" : "bf1942";
        foreach (var d in new[]
        {
            modDir,
            Path.Combine(modDir, "Archives"),
            Path.Combine(modDir, "Archives", sub),
            Path.Combine(modDir, "Archives", sub, "levels"),
            Path.Combine(modDir, "Movies"),
            Path.Combine(modDir, "Music"),
        })
        { Directory.CreateDirectory(d); made.Add(d); }

        var init = Path.Combine(modDir, "init.con");
        File.WriteAllText(init, InitCon(s with { Name = name }), System.Text.Encoding.Latin1);
        made.Add(init);
        return made;
    }

    public static string Sanitize(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw.Trim()) sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.ToString().Trim('_');
    }
}
