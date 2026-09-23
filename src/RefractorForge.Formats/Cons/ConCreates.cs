using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Con;

/// <summary>One definition a script makes: <c>ObjectTemplate.create Bundle WillyComplex</c>,
/// <c>NetworkableInfo.createNewInfo WillyBodyInfo</c>, <c>aiTemplate.create Willy</c>. <see cref="Type"/> is null for
/// the one-argument forms.</summary>
public sealed record ConCreate(string Family, string? Type, string Name, string Source, int Line);

/// <summary>
/// Every name a set of scripts DEFINES, in every form the engine has - which is more than it looks. A vehicle folder
/// creates seven kinds of thing:
/// <list type="bullet">
/// <item><c>ObjectTemplate.create Type Name</c>, <c>GeometryTemplate.create Type Name</c>,
///   <c>LodSelectorTemplate.create Type Name</c>, <c>aiTemplatePlugIn.create Type Name</c> (two arguments);</item>
/// <item><c>aiTemplate.create Name</c>, <c>weaponTemplate.create Name</c> (one argument);</item>
/// <item><c>NetworkableInfo.createNewInfo Name</c>.</item>
/// </list>
/// The engine keeps the FIRST definition of a name and rejects a second one, deactivating the template so the rest
/// of its block silently does nothing. A clone that renames only its ObjectTemplates therefore still re-creates the
/// donor's network infos, AI plug-ins and LOD selectors - retail vehicle folders hold hundreds of those
/// (BF1942: 197 createNewInfo, 361 aiTemplatePlugIn, 113 LodSelectorTemplate). This extractor is what both the
/// cloner and a duplicate-name check stand on.
///
/// Setters that merely start with "create" (<c>createInvisible</c>, <c>createNotInGrid</c>,
/// <c>createSkeleton</c>) are not definitions and are ignored, as is anything inside <c>rem</c> or
/// <c>beginrem</c>...<c>endrem</c>.
/// </summary>
public static class ConCreates
{
    private static readonly Regex Line = new(@"^\s*(?<fam>[A-Za-z_][A-Za-z0-9_]*)\.(?<verb>create|createNewInfo)\s+(?<args>.+?)\s*$",
                                             RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ConCreate> Extract(string source, string text)
    {
        var list = new List<ConCreate>();
        bool inBlockComment = false;
        int n = 0;
        foreach (var raw in ConLines.Split(text ?? ""))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (inBlockComment)
            {
                if (line.StartsWith("endrem", StringComparison.OrdinalIgnoreCase)) inBlockComment = false;
                continue;
            }
            if (line.StartsWith("beginrem", StringComparison.OrdinalIgnoreCase)) { inBlockComment = true; continue; }
            if (line.StartsWith("rem", StringComparison.OrdinalIgnoreCase) && (line.Length == 3 || char.IsWhiteSpace(line[3]))) continue;

            var m = Line.Match(line);
            if (!m.Success) continue;
            var args = StripTrailingComment(m.Groups["args"].Value)
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) continue;
            string family = m.Groups["fam"].Value;
            bool newInfo = m.Groups["verb"].Value.Equals("createNewInfo", StringComparison.OrdinalIgnoreCase);
            if (newInfo || args.Length == 1) list.Add(new ConCreate(family, null, args[0], source, n));
            else list.Add(new ConCreate(family, args[0], args[1], source, n));
        }
        return list;
    }

    public static List<ConCreate> Extract(IEnumerable<(string Source, string Text)> scripts)
        => scripts.SelectMany(s => Extract(s.Source, s.Text)).ToList();

    /// <summary>Names defined more than once within one family, with every place each was defined. The engine keeps
    /// the first (in its load order) and breaks the rest.</summary>
    public static List<(string Family, string Name, List<ConCreate> Sites)> Duplicates(IEnumerable<ConCreate> creates)
        => creates.GroupBy(c => (Family: c.Family.ToLowerInvariant(), Name: c.Name.ToLowerInvariant()))
                  .Where(g => g.Count() > 1)
                  .Select(g => (g.First().Family, g.First().Name, g.ToList()))
                  .ToList();

    private static string StripTrailingComment(string s)
    {
        int cut = s.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? s[..cut] : s;
    }
}
