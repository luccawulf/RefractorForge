using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Con;

/// <summary>
/// Duplicate an object's <c>.con</c> set under a new name - a jeep becomes the start of a new vehicle, a rifle
/// the start of a new weapon - with every template it declares renamed to match and every reference to those
/// templates rewritten. The MDT's Object Generator did this and left the mesh alone: geometry templates keep
/// pointing at the original <c>.sm</c>, which is what a modder wants as a starting point.
///
/// <para><b>What gets renamed.</b> The names the folder DEFINES, in every create form (<see cref="ConCreates"/>): a name
/// containing the old name gets the new one in its place (<c>WillyEngine</c> -> <c>JeepEngine</c>). With
/// <c>renameAll</c>, a defined name that does NOT contain the old name is renamed too, to <c>&lt;new&gt;_&lt;name&gt;</c> -
/// otherwise the clone would define it a second time, and the engine keeps the first definition of a name and
/// deactivates the second, silently breaking the rest of that block. A clone that is meant to ship next to its donor
/// needs <c>renameAll</c>. Names that are only USED (a shared template from another folder) are never touched.</para>
///
/// <para><b>What never gets renamed: paths and resource names.</b> Only the object's own folder segment in each file
/// path changes (<c>Vehicles/Land/Willy/...</c> -> <c>Vehicles/Land/Jeep/...</c>); file names stay as they are. Path
/// arguments (anything with a slash or a file extension) and resource commands (<c>GeometryTemplate.file</c>,
/// <c>loadSoundScript</c>, <c>setSkin</c>, <c>createSkeleton</c>, icons, <c>include</c>) are left exactly as written.
/// Renaming file names by substring while renaming references by whole word used to disagree whenever a file name held
/// the old name without being a template (<c>Sounds/horn_mutt.ssc</c>), leaving a clone that loaded a script that no
/// longer existed; and a tread's <c>setSkin animations/.../ShermanTrackL.skn</c> must keep naming the file in
/// animations.rfa even though <c>ShermanTrackL</c> is also a template.</para>
/// </summary>
public static class ObjectCloner
{
    public sealed record Renamed(string OldPath, string NewPath, string Text);

    /// <summary>A <c>GeometryTemplate.file</c> line in the cloned set, so a caller can re-point it at a new mesh.</summary>
    public sealed record GeometryFileRef(string Path, string Template, string File);

    public sealed class Plan
    {
        public string OldName { get; init; } = "";
        public string NewName { get; init; } = "";
        /// <summary>Every rename that will be applied, old -> new, across all create families.</summary>
        public SortedDictionary<string, string> Templates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Renamed> Files { get; } = new();
        /// <summary>Every definition in the source set (before renaming).</summary>
        public List<ConCreate> Creates { get; } = new();
        public List<GeometryFileRef> GeometryFiles { get; } = new();
    }

    /// <summary>Commands whose arguments name files or other resources, never templates. Compared with any leading
    /// <c>set</c> removed, so both the BF1942 and the Vietnam spelling match.</summary>
    private static readonly HashSet<string> ResourceCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "loadSoundScript", "skin", "createSkeleton", "vehicleIcon", "minimapIcon", "texture",
        "addSkeletonIK", "soldierIcon", "weaponIcon", "addWeaponIcon", "turretIcon", "include",
    };

    private static readonly string[] FileExtensions =
        { ".ssc", ".inc", ".con", ".skn", ".ske", ".baf", ".tga", ".dds", ".sm", ".rs", ".wav", ".bik", ".tm", ".raw" };

    private static readonly Regex CommandLine = new(@"^\s*#?(?:(?<fam>[A-Za-z_][A-Za-z0-9_]*)\.)?(?<cmd>[A-Za-z_][A-Za-z0-9_]*)\b",
                                                    RegexOptions.Compiled);
    private static readonly Regex GeoCreate = new(@"^\s*GeometryTemplate\.create\s+\S+\s+(?<n>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GeoFile = new(@"^\s*GeometryTemplate\.file\s+(?<f>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Build the rename plan for a set of files. <paramref name="files"/> are (archive path, text) pairs -
    /// typically everything under one object's folder. Geometry templates are left as they are (the clone keeps
    /// drawing with the original's mesh) unless <paramref name="renameGeometry"/>.
    /// </summary>
    public static Plan Build(string oldName, string newName, IEnumerable<(string Path, string Text)> files,
                             bool renameGeometry = false, bool renameAll = false)
    {
        var plan = new Plan { OldName = oldName, NewName = newName };
        var list = files.ToList();

        foreach (var (path, text) in list)
            plan.Creates.AddRange(ConCreates.Extract(path, text));

        // renameAll renames geometry templates too: a clone that re-created the donor's GeometryTemplate names would
        // hit "already exists" on every one. Their .file lines keep naming the donor's meshes, so it still draws the same.
        foreach (var c in plan.Creates)
        {
            if (c.Family.Equals("GeometryTemplate", StringComparison.OrdinalIgnoreCase) && !renameGeometry && !renameAll) continue;
            if (plan.Templates.ContainsKey(c.Name)) continue;
            if (c.Name.Contains(oldName, StringComparison.OrdinalIgnoreCase))
                plan.Templates[c.Name] = Regex.Replace(c.Name, Regex.Escape(oldName), newName.Replace("$", "$$"), RegexOptions.IgnoreCase);
            else if (renameAll)
                plan.Templates[c.Name] = newName + "_" + c.Name;
        }

        // Longest names first, so "WillyEngine" is rewritten before "Willy" could eat its prefix.
        var ordered = plan.Templates.OrderByDescending(kv => kv.Key.Length)
            .Select(kv => (Rx: new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(kv.Key) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase), To: kv.Value))
            .ToList();

        foreach (var (path, text) in list)
        {
            var outText = RewriteText(text, ordered);
            var newPath = RenameFolderSegment(path, oldName, newName);
            plan.Files.Add(new Renamed(path, newPath, outText));

            string? geo = null;
            foreach (var line in ConLines.Split(outText))
            {
                var gc = GeoCreate.Match(line);
                if (gc.Success) { geo = gc.Groups["n"].Value; continue; }
                var gf = GeoFile.Match(line);
                if (gf.Success) plan.GeometryFiles.Add(new GeometryFileRef(newPath, geo ?? "", gf.Groups["f"].Value));
            }
        }
        return plan;
    }

    /// <summary>Rename the path segment equal to the old name - the object's own folder - and nothing else, except a
    /// file whose name IS the old name (<c>Willy/Willy.con</c>, the run-chain file of a level-local object), which the
    /// objects list reaches as <c>run &lt;Name&gt;/&lt;Name&gt;</c>.</summary>
    public static string RenameFolderSegment(string path, string oldName, string newName)
    {
        var parts = path.Replace('\\', '/').Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals(oldName, StringComparison.OrdinalIgnoreCase)) parts[i] = newName;
        // Only the run-chain script sitting directly in the renamed folder: any other file keeps its name, because the
        // references to it (loadSoundScript and friends) are left exactly as written.
        var leaf = parts[^1];
        if (parts.Length >= 2 && parts[^2].Equals(newName, StringComparison.OrdinalIgnoreCase)
            && leaf.Equals(oldName + ".con", StringComparison.OrdinalIgnoreCase))
            parts[^1] = newName + ".con";
        return string.Join('/', parts);
    }

    private static string RewriteText(string text, List<(Regex Rx, string To)> renames)
    {
        if (renames.Count == 0) return text;
        // Line by line, keeping every line ending exactly as it was.
        return Regex.Replace(text, @"[^\r\n]+", m =>
        {
            var line = m.Value;
            var cmd = CommandLine.Match(line);
            if (cmd.Success)
            {
                var c = cmd.Groups["cmd"].Value;
                if (c.StartsWith("set", StringComparison.OrdinalIgnoreCase) && c.Length > 3 && char.IsUpper(c[3])) c = c[3..];
                if (ResourceCommands.Contains(c)) return line;
            }
            return Regex.Replace(line, @"\S+", t => IsPathLike(t.Value) ? t.Value : RenameToken(t.Value, renames));
        });
    }

    private static string RenameToken(string token, List<(Regex Rx, string To)> renames)
    {
        foreach (var (rx, to) in renames)
            token = rx.Replace(token, to.Replace("$", "$$"));
        return token;
    }

    private static bool IsPathLike(string token)
    {
        var t = token.Trim('"');
        if (t.Contains('/') || t.Contains('\\')) return true;
        return FileExtensions.Any(e => t.EndsWith(e, StringComparison.OrdinalIgnoreCase));
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
