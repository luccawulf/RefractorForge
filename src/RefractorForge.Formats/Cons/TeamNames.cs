using System.Text;
using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Who team 1 and team 2 actually are on THIS level.
///
/// The editor used to label them from the game alone - Axis/Allies for BF1942, NVA/US for Vietnam - which is wrong
/// the moment you open anything but a stock WWII map. Teams are a per-LEVEL property, not a per-mod one: stock Wake
/// is Japan vs US Marines while Bocage is Germany vs US, and a mod map can field anything at all (Interstate's
/// Akina_Mountain runs British vs US).
///
/// A level states it in its <c>Init.con</c>:
/// <code>
///   game.setTeamSkin 1 JapaneseSoldier
///   game.setTeamSkin 2 USMarineSoldier
/// </code>
/// The soldier skin IS the nationality, so it needs no lookup table and works for any mod's own skins. Where a level
/// omits it, the control-point flag models (<c>setTeamGeometry 1 flagJp_m1</c>) name the same thing, and failing
/// both we fall back to the old game-based defaults. Team 0 is always Neutral.
/// </summary>
public sealed record TeamNames(string Neutral, string Team1, string Team2)
{
    public static TeamNames DefaultFor(bool vietnam) => vietnam
        ? new TeamNames("Neutral", "Vietcong / NVA", "US Army")
        : new TeamNames("Neutral", "Axis", "Allies");

    /// <summary>The label for a team index (0/1/2); anything else comes back as "Team N".</summary>
    public string this[int team] => team switch { 0 => Neutral, 1 => Team1, 2 => Team2, _ => "Team " + team };

    /// <summary>Label with its index, as the gameplay dialogs show it (e.g. "Japanese (1)").</summary>
    public string Labelled(int team) => $"{this[team]} ({team})";

    /// <summary>Read the teams out of a level's .con files. Pass the bytes of every candidate file - Init.con first,
    /// then ControlPointTemplates.con - and the first thing that names a team wins.</summary>
    public static TeamNames Parse(IEnumerable<byte[]> conFiles, bool vietnam)
    {
        var def = DefaultFor(vietnam);
        string? t1 = null, t2 = null;

        foreach (var bytes in conFiles)
        {
            if (bytes is null || bytes.Length == 0) continue;
            string text;
            try { text = Encoding.Latin1.GetString(bytes); } catch { continue; }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("rem", StringComparison.OrdinalIgnoreCase)) continue;

                // `game.setTeamSkin <team> <SkinName>` - the strongest signal, and what stock levels use.
                var m = Regex.Match(line, @"setTeamSkin\s+([12])\s+(\S+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var name = FriendlyFromSkin(m.Groups[2].Value);
                    if (name.Length > 0) { if (m.Groups[1].Value == "1") t1 ??= name; else t2 ??= name; }
                    continue;
                }
                // `ObjectTemplate.setTeamGeometry <team> flagJp_m1` - the flag model, used only if no skin was named.
                m = Regex.Match(line, @"setTeamGeometry\s+([12])\s+(\S+)", RegexOptions.IgnoreCase);
                if (m.Success && FriendlyFromFlag(m.Groups[2].Value) is { Length: > 0 } flag)
                {
                    if (m.Groups[1].Value == "1") t1 ??= flag; else t2 ??= flag;
                }
            }
            if (t1 is not null && t2 is not null) break;
        }
        return new TeamNames(def.Neutral, t1 ?? def.Team1, t2 ?? def.Team2);
    }

    /// <summary>"JapaneseSoldier" -> "Japanese", "USMarineSoldier" -> "US Marine". Drops the role suffix and splits
    /// the remaining CamelCase, keeping acronyms whole so "US" does not become "U S".</summary>
    public static string FriendlyFromSkin(string skin)
    {
        var s = (skin ?? "").Trim().Trim('"');
        if (s.Length == 0) return "";
        foreach (var suffix in new[] { "Soldier", "Trooper", "Marine_", "Army" })
            if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            { s = s[..^suffix.Length]; break; }
        s = s.Replace('_', ' ');
        s = Regex.Replace(s, "([A-Z]+)([A-Z][a-z])", "$1 $2");   // USMarine -> US Marine
        s = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");      // RedArmy  -> Red Army
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>"flagJp_m1" -> "Japanese". Only the stock flag set is named; an unknown flag returns "" so the
    /// caller keeps its default rather than inventing a nationality.</summary>
    public static string FriendlyFromFlag(string mesh)
    {
        var s = (mesh ?? "").Trim().Trim('"').ToLowerInvariant();
        if (!s.StartsWith("flag")) return "";
        s = s[4..];
        int cut = s.IndexOf("_m", StringComparison.Ordinal);
        if (cut > 0) s = s[..cut];
        return s switch
        {
            "jp" or "ja" => "Japanese",
            "us" => "US",
            "ge" or "de" => "German",
            "uk" or "gb" or "br" => "British",
            "ru" or "so" => "Soviet",
            "it" => "Italian",
            "ca" => "Canadian",
            _ => "",
        };
    }
}
