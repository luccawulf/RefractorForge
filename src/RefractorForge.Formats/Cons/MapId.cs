using System;
using System.Linq;

namespace RefractorForge.Formats.Con;

/// <summary>
/// <c>game.setMapId</c> in a Battlefield 1942 level's <c>Menu/init.con</c>: the line that says which mod the level
/// belongs to.
///
/// <para>It arrived with patch 1.4, and a level made before that (or by a tool that never wrote it) has none. The
/// 1.61 game then loads NONE of the mod's objects for that level. Measured on the dedicated server (2026-11-28,
/// Snow_El_Alamein, a 2019 custom map): its memory stays at 56 MB instead of 90, every stock template is missing, and
/// the first object that needs one stops the load - "SouthOpenBase addBundleChilds() template not found:AnimatedFlag".
/// The client reports only what follows from it ("The game type is not defined...") and drops out. One appended line,
/// <c>game.setMapId "BF1942"</c>, and the same archive loads. Nothing else about the level is at fault, which is
/// what makes it expensive to find: the errors name whatever object happens to be instantiated first.</para>
/// </summary>
public static class MapId
{
    /// <summary>Does this <c>Menu/init.con</c> set a map id? A commented-out line does not count.</summary>
    public static bool IsSet(string? menuInit)
    {
        if (string.IsNullOrEmpty(menuInit)) return false;
        return menuInit.Split('\n').Select(l => l.Trim())
            .Any(l => l.StartsWith("game.setMapId", StringComparison.OrdinalIgnoreCase)
                      && l.Length > "game.setMapId".Length && char.IsWhiteSpace(l["game.setMapId".Length])
                      && l["game.setMapId".Length..].Trim().Trim('"').Length > 0);
    }

    /// <summary>The same text with <c>game.setMapId "&lt;mod&gt;"</c> appended, CR LF line ends; unchanged when one
    /// is already set. Retail's own levels write the id in capitals ("BF1942").</summary>
    public static string With(string menuInit, string mod)
    {
        if (IsSet(menuInit)) return menuInit;
        string body = menuInit.TrimEnd('\r', '\n', ' ', '\t');
        return (body.Length > 0 ? body + "\r\n" : "") + $"game.setMapId \"{mod}\"\r\n";
    }

    /// <summary>The id retail uses for a mod folder: the stock game's is "BF1942"; a mod's is its folder name.</summary>
    public static string ForMod(string? modFolder)
        => string.IsNullOrWhiteSpace(modFolder) || modFolder.Equals("bf1942", StringComparison.OrdinalIgnoreCase) ? "BF1942" : modFolder;
}
