using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats.Validation;

/// <summary>
/// Does this level lean on anything its mod chain does not provide?
///
/// A map made with one mod loaded and shipped for another is the classic way to get an invisible building:
/// the template resolved fine on the author's machine, from an archive the player's mod never mounts. This
/// walks what the level references and asks the SAME resolvers the editor loaded the level with, so a "missing"
/// here means missing from the chain the player will have - not merely missing from a hard-coded list.
/// </summary>
public static class DependencyCheck
{
    public sealed class Resolvers
    {
        /// <summary>Whether a template name resolves to a mesh or an assembled object in the loaded chain.</summary>
        public required Func<string, bool> TemplateExists { get; init; }
        /// <summary>Texture names a template's mesh asks for that did NOT resolve (empty when all did).</summary>
        public Func<string, IEnumerable<string>>? UnresolvedTextures { get; init; }
        /// <summary>Which archive (or "level") a template came from, for the report.</summary>
        public Func<string, string?>? SourceOf { get; init; }
    }

    public static LevelReport Run(StaticObjectsFile objects, EditableGameplay? gameplay, Resolvers res)
    {
        var r = new LevelReport("Dependencies");
        var byTemplate = objects.Objects
            .GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count());

        var missingTex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int missingTemplates = 0;
        foreach (var g in byTemplate)
        {
            string t = g.Key;
            if (!res.TemplateExists(t))
            {
                missingTemplates++;
                var first = g.First();
                r.Add(IssueSeverity.Error, "Missing template",
                    $"'{t}' ({g.Count()} placed) does not resolve in the loaded mod chain", first.Position, first.Id);
                continue;
            }
            if (res.UnresolvedTextures is not null)
                foreach (var tex in res.UnresolvedTextures(t))
                {
                    if (!missingTex.TryGetValue(tex, out var list)) missingTex[tex] = list = new List<string>();
                    if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
                }
        }

        foreach (var kv in missingTex.OrderBy(k => k.Key))
            r.Add(IssueSeverity.Warning, "Missing texture",
                $"'{kv.Key}' is not in any loaded texture archive (used by {string.Join(", ", kv.Value.Take(4))}" +
                (kv.Value.Count > 4 ? $" and {kv.Value.Count - 4} more)" : ")"));

        // Vehicles are templates too, and a spawner pointing at a vehicle the mod lacks spawns nothing.
        if (gameplay is not null)
            foreach (var v in gameplay.VehicleSpawns)
                foreach (var veh in new[] { v.Vehicle, v.Vehicle1, v.Vehicle2 }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
                    if (!res.TemplateExists(veh))
                        r.Add(IssueSeverity.Error, "Missing vehicle",
                            $"spawner '{v.Name}' asks for '{veh}', which the loaded mod chain does not have", v.Position);

        // A quick provenance breakdown, so the author can see which archives the level actually depends on.
        if (res.SourceOf is not null)
        {
            var bySource = objects.Objects
                .Select(o => res.SourceOf(o.Template) ?? "?")
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count());
            foreach (var g in bySource.Take(8))
                r.Add(IssueSeverity.Info, "Provenance", $"{g.Count():N0} object(s) come from {g.Key}");
        }

        if (missingTemplates == 0 && missingTex.Count == 0)
            r.Add(IssueSeverity.Info, "Dependencies", "Every template and texture the level uses resolves in the loaded chain");
        return r;
    }
}

/// <summary>
/// What a dedicated server needs versus what only a client draws.
///
/// The same rule the server-side-mod (SSM) patch writer applies, exposed as a listing, so an author can see
/// what a server download will and will not carry before they ship one.
/// </summary>
public static class ServerClientSplit
{
    public sealed record Entry(string Path, long Bytes, bool ClientOnly);

    public static (List<Entry> Entries, long ServerBytes, long ClientOnlyBytes) Classify(IEnumerable<(string Path, long Bytes)> files)
    {
        var list = new List<Entry>();
        long sv = 0, cl = 0;
        foreach (var (p, b) in files)
        {
            bool clientOnly = RefractorFlatArchive.IsClientOnlyEntry(p);
            list.Add(new Entry(p, b, clientOnly));
            if (clientOnly) cl += b; else sv += b;
        }
        return (list.OrderBy(e => e.ClientOnly).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList(), sv, cl);
    }

    public static LevelReport Report(IEnumerable<(string Path, long Bytes)> files)
    {
        var (entries, sv, cl) = Classify(files);
        var r = new LevelReport("Server / client files");
        r.Add(IssueSeverity.Info, "Server", $"{entries.Count(e => !e.ClientOnly):N0} file(s), {sv / (1024.0 * 1024):0.0} MB - what a dedicated server needs");
        r.Add(IssueSeverity.Info, "Client only", $"{entries.Count(e => e.ClientOnly):N0} file(s), {cl / (1024.0 * 1024):0.0} MB - textures, sounds, movies, baked light");
        foreach (var byExt in entries.Where(e => e.ClientOnly).GroupBy(e => System.IO.Path.GetExtension(e.Path).ToLowerInvariant()).OrderByDescending(g => g.Sum(e => e.Bytes)).Take(6))
            r.Add(IssueSeverity.Info, "Client only", $"{byExt.Key}: {byExt.Count()} file(s), {byExt.Sum(e => e.Bytes) / (1024.0 * 1024):0.0} MB");
        return r;
    }
}
