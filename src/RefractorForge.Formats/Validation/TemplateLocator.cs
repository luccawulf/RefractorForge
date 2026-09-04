using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats.Validation;

/// <summary>
/// Where else an object template lives. A map that references a template no loaded archive declares will not show
/// it - in the editor or in the game - and the map check used to stop at saying so. Usually the object exists a
/// folder away: another installed mod (a map merged from one mod into another drags its objects' NAMES along, not
/// the objects), or the other game entirely. This indexes every installed mod's object archives by template folder,
/// so the check can say which mod has it and what to do.
/// </summary>
public static class TemplateLocator
{
    /// <summary>template name (case-insensitive) -> the mods whose object archives carry a folder of that name.</summary>
    public static Dictionary<string, List<string>> IndexMods(string modsDir, Func<string, IEnumerable<string>>? entriesOf = null)
    {
        var idx = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(modsDir)) return idx;
        foreach (var mod in Directory.EnumerateDirectories(modsDir))
        {
            var modName = Path.GetFileName(mod);
            foreach (var arch in ObjectArchives(mod))
            {
                IEnumerable<string> names;
                try { names = entriesOf?.Invoke(arch) ?? new RefractorFlatArchive(arch).Entries.Select(e => e.Name).ToList(); }
                catch { continue; }                                     // a damaged archive is not this check's problem
                foreach (var n in names)
                {
                    var nn = n.Replace('\\', '/');
                    if (!nn.EndsWith("/objects.con", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = nn.Split('/');
                    if (parts.Length < 2) continue;
                    var tmpl = parts[^2];
                    if (tmpl.Equals("Ai", StringComparison.OrdinalIgnoreCase)) continue;   // an AI sub-folder, not a template
                    if (!idx.TryGetValue(tmpl, out var list)) idx[tmpl] = list = new List<string>();
                    if (!list.Contains(modName, StringComparer.OrdinalIgnoreCase)) list.Add(modName);
                }
            }
        }
        return idx;
    }

    /// <summary>The object archives of one mod: <c>archives/objects*.rfa</c> in either casing of the folder.</summary>
    public static IEnumerable<string> ObjectArchives(string modDir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in new[] { "archives", "Archives" })
        {
            var d = Path.Combine(modDir, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.rfa", SearchOption.TopDirectoryOnly))
                if (Path.GetFileName(f).StartsWith("objects", StringComparison.OrdinalIgnoreCase) && seen.Add(Path.GetFullPath(f)))
                    yield return f;
        }
    }

    /// <summary>One sentence of what to do about a template the loaded archives do not declare.</summary>
    public static string Advice(string template, IReadOnlyList<string> foundIn, string? thisMod)
    {
        if (foundIn.Count == 0)
            return $"'{template}' is not in this mod, its parents, or any other installed mod of this game - it has to be ported (mesh, textures and .con) before it can appear in game or here";
        string mods = string.Join(", ", foundIn);
        string first = foundIn[0];
        return thisMod is null
            ? $"'{template}' is in {mods} - copy that object into this mod's objects archive, or add game.addModPath Mods/{first}/ to this mod's init.con"
            : $"'{template}' is in {mods} - add game.addModPath Mods/{first}/ to Mods/{thisMod}/init.con (above Mods/BfVietnam/), or copy the object into {thisMod}'s objects archive";
    }
}
