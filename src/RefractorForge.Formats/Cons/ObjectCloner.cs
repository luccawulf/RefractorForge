using System.Text;
using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Duplicate an object's <c>.con</c> set under a new name - a jeep becomes the start of a new vehicle, a rifle
/// the start of a new weapon - with every template it declares renamed to match and every reference to those
/// templates rewritten. The MDT's Object Generator did this and left the mesh alone: geometry templates keep
/// pointing at the original <c>.sm</c>, which is what a modder wants as a starting point.
///
/// Renaming is by whole word, case-insensitive, over a rename map built from the object's own
/// <c>ObjectTemplate.create</c> lines: anything named with the old name in it (<c>WillyEngine</c>,
/// <c>WillySeat</c>) becomes the same with the new name, and a template named exactly the old name becomes
/// exactly the new one. Names that merely CONTAIN the old name as part of another word are also caught, since
/// that is how DICE named parts; a name that is a substring of another object's name is the one risk, and the
/// preview exists so that can be seen before anything is written.
/// </summary>
public static class ObjectCloner
{
    public sealed record Renamed(string OldPath, string NewPath, string Text);

    public sealed class Plan
    {
        public string OldName { get; init; } = "";
        public string NewName { get; init; } = "";
        /// <summary>Every template rename that will be applied, old -> new.</summary>
        public SortedDictionary<string, string> Templates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Renamed> Files { get; } = new();
    }

    private static readonly Regex Create = new(@"^\s*(ObjectTemplate|GeometryTemplate|NetworkableInfo)\.create\s+(\S+)\s+(\S+)",
                                               RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Build the rename plan for a set of files. <paramref name="files"/> are (archive path, text) pairs -
    /// typically everything under one object's folder. Geometry templates are left as they are so the clone
    /// keeps drawing with the original's mesh.
    /// </summary>
    public static Plan Build(string oldName, string newName, IEnumerable<(string Path, string Text)> files, bool renameGeometry = false)
    {
        var plan = new Plan { OldName = oldName, NewName = newName };
        var list = files.ToList();

        foreach (var (_, text) in list)
            foreach (Match m in Create.Matches(text))
            {
                var kind = m.Groups[1].Value;
                var name = m.Groups[3].Value;
                if (kind.Equals("GeometryTemplate", StringComparison.OrdinalIgnoreCase) && !renameGeometry) continue;
                if (name.Contains(oldName, StringComparison.OrdinalIgnoreCase) && !plan.Templates.ContainsKey(name))
                    plan.Templates[name] = Regex.Replace(name, Regex.Escape(oldName), newName, RegexOptions.IgnoreCase);
            }

        // Longest names first, so "WillyEngine" is rewritten before "Willy" could eat its prefix.
        var ordered = plan.Templates.OrderByDescending(kv => kv.Key.Length).ToList();
        foreach (var (path, text) in list)
        {
            var outp = text;
            foreach (var kv in ordered)
                outp = Regex.Replace(outp, @"(?<![A-Za-z0-9_])" + Regex.Escape(kv.Key) + @"(?![A-Za-z0-9_])", kv.Value, RegexOptions.IgnoreCase);
            var newPath = Regex.Replace(path, Regex.Escape(oldName), newName, RegexOptions.IgnoreCase);
            plan.Files.Add(new Renamed(path, newPath, outp));
        }
        return plan;
    }

    /// <summary>The <c>run</c> line an <c>objects.con</c> would need to pick the clone up, if the original had one.</summary>
    public static string? RunLine(Plan plan)
    {
        var con = plan.Files.FirstOrDefault(f => f.NewPath.EndsWith($"/{plan.NewName}.con", StringComparison.OrdinalIgnoreCase)
                                              || f.NewPath.EndsWith($"{plan.NewName}.con", StringComparison.OrdinalIgnoreCase));
        if (con is null) return null;
        var p = con.NewPath.Replace('\\', '/');
        // The path is relative to the objects folder, wherever that sits: "objects/Vehicles/..." at the root of
        // objects.rfa, or "bf1942/levels/X/Objects/..." inside a level.
        int i = p.IndexOf("objects/", StringComparison.OrdinalIgnoreCase);
        while (i > 0 && p[i - 1] != '/') i = p.IndexOf("objects/", i + 1, StringComparison.OrdinalIgnoreCase);
        var rel = i >= 0 ? p[(i + "objects/".Length)..] : p;
        return "run " + rel[..^4];
    }
}
