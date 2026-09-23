using RefractorForge.Formats;

namespace RefractorForge.Viewer;

/// <summary>
/// The archives the editor opens its mesh and texture libraries from when a level loads, in precedence order - both
/// libraries are first-wins, so this order decides which copy of a mesh or texture the editor draws, and it must be the
/// one the game draws. Every source goes through the engine's literal mount list (<see cref="GameMounts"/>):
///
/// - the level's own mod chain, found from the level archive (<see cref="ModChain.ForLevelArchive"/>) and collected
///   the way the game mounts it (<see cref="ModChain.CollectArchives"/>): a mounted <c>_001</c> ahead of its base,
///   <c>objects_001</c>, a BFV <c>standardMesh_001</c> or any <c>_002</c> left out. Globbing each Archives folder here
///   used to hand the libraries <c>texture.rfa</c> ahead of <c>texture_001.rfa</c> (BF1942 mg42_r at 256 px, not 1024);
/// - archives chosen by hand or saved from File > Open Mod, with the patch each one mounts put ahead of it
///   (<see cref="GameMounts.WithMountedPatches"/>) - never an unmounted sibling, which used to rank above every lower
///   mod, the base game included;
/// - texture archives found beside those, only where the game mounts them (<see cref="GameMounts.IsMountedFile"/>).
///
/// Pure path work (no window, no GL), so it is tested headlessly.
/// </summary>
public static class LibraryArchives
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    public static bool IsTexArc(string p) => Path.GetFileName(p).StartsWith("texture", Ci);

    /// <summary>Archives with nothing a mesh library reads: audio, movies, menu art, fonts, shaders. The animations
    /// archives stay - skinned meshes are rest-posed from their .skn/.ske.</summary>
    public static bool IsNonMeshArc(string p)
    {
        var n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
        return n.StartsWith("sound") || n.StartsWith("movie") || n.StartsWith("music") || n is "menu" or "font" or "shaders";
    }

    /// <summary>The mesh and texture library archives for a level, first wins.</summary>
    /// <param name="meshPicks">Mesh/object archives chosen by hand, saved by File > Open Mod or named by a project.</param>
    /// <param name="texPicks">Texture archives chosen the same ways.</param>
    /// <param name="levelRfas">The level's own archives (base, patches, a borrowed base terrain first), last-wins.</param>
    /// <param name="levelDir">The level as opened: a folder level's own files join both libraries.</param>
    /// <param name="scanDir">The level's folder, searched for texture archives too.</param>
    /// <param name="includeInherited">Follow dependencies' init.con (<see cref="ModChain.Resolve"/>).</param>
    public static (string[] Mesh, string[] Tex) For(IEnumerable<string> meshPicks, IEnumerable<string> texPicks,
        IEnumerable<string> levelRfas, string? levelDir, string? scanDir, bool includeInherited, Action<string>? log = null)
    {
        log ??= _ => { };
        var picksMesh = meshPicks.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        var picksTex = texPicks.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        var levels = levelRfas.Where(File.Exists).ToArray();

        var discovered = Discover(levels, includeInherited, log);
        if (discovered.Count > 0)
            log($"Auto-discovered {discovered.Count} archive(s) from the level's mod + dependency chain.");

        // A FOLDER level goes in too: an extracted map keeps its own objects and textures as loose files, which the
        // libraries read through RefractorFlatArchive.FromFolder.
        var levelFolder = levelDir is not null && Directory.Exists(levelDir) ? new[] { levelDir } : Array.Empty<string>();

        var chosenMesh = picksMesh.Where(a => !IsTexArc(a)).ToArray();
        var withPatches = GameMounts.WithMountedPatches(chosenMesh);
        foreach (var added in withPatches.Except(chosenMesh, StringComparer.OrdinalIgnoreCase))
            log($"Auto-mounted mesh patch archive: {Path.GetFileName(added)}");
        var mesh = withPatches
            .Concat(discovered.Where(a => !IsTexArc(a) && !IsNonMeshArc(a)))
            .Concat(levels)
            .Concat(levelFolder)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Texture archives beside the chosen ones and in the level's folder, where the game mounts them. The level's
        // OWN archives go first: a map retextures objects by shipping same-named .dds, and the engine searches the
        // level's path before the mod and the base game.
        var texDirs = picksMesh.Concat(picksTex)
            .Select(a => Path.GetDirectoryName(Path.GetFullPath(a)))
            .Append(scanDir)
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var beside = texDirs.SelectMany(d => Directory.EnumerateFiles(d, "texture*.rfa", SearchOption.TopDirectoryOnly))
                            .Where(GameMounts.IsMountedFile);
        var tex = levels
            .Concat(levelFolder)
            .Concat(GameMounts.WithMountedPatches(picksTex.Where(File.Exists).Concat(beside)))
            .Concat(discovered.Where(IsTexArc))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (mesh, tex);
    }

    /// <summary>Every archive the level's mod chain mounts, meshes then textures, each list in engine order. The level
    /// opened is the LAST of <paramref name="levels"/> (a borrowed base terrain is prepended to it), so its mod leads.</summary>
    private static List<string> Discover(string[] levels, bool includeInherited, Action<string> log)
    {
        var mesh = new List<string>();
        var tex = new List<string>();
        var seenMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lvl in Enumerable.Reverse(levels))
        {
            var chain = ModChain.ForLevelArchive(lvl, includeInherited);
            if (chain is null || chain.Mounts.Count == 0 || !seenMods.Add(chain.Mounts[0].Path)) continue;
            log($"Mod chain for {chain.Mounts[0].Name}: {chain.Describe()}");
            if (chain.Missing.Count > 0)
                log($"   WARNING - init.con names {chain.Missing.Count} mod(s) that are NOT installed: {string.Join(", ", chain.Missing)}");
            var (m, t) = ModChain.CollectArchives(chain, skipNonAsset: false);   // animations stay (see IsNonMeshArc)
            mesh.AddRange(m);
            tex.AddRange(t);
        }
        return mesh.Concat(tex).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
