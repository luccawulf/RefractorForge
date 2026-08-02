namespace RefractorForge.Formats;

/// <summary>One mount point in a resolved mod chain.</summary>
/// <param name="Name">The folder name of the mod (e.g. "FHSW"), or the sub-path leaf for nested mounts.</param>
/// <param name="Path">The absolute directory that is mounted.</param>
/// <param name="Listed">True when the starting mod's own init.con listed this mount explicitly (authoritative,
/// exactly what the game mounts); false when it was INHERITED — discovered by following a dependency's own
/// init.con. Inherited mounts are appended at the LOWEST precedence so they can only fill gaps.</param>
/// <param name="Depth">How many init.con hops from the starting mod (0 = the starting mod itself).</param>
public readonly record struct ModMount(string Name, string Path, bool Listed, int Depth);

/// <summary>A resolved mod mount chain plus what could not be found.</summary>
public sealed class ModChainResult
{
    /// <summary>Mount points in PRECEDENCE ORDER (first wins) — the starting mod first, the base game last.
    /// This is the order the mesh/texture libraries must receive archives in, because both are first-wins
    /// (MeshLibrary.Open / TextureLibrary.Open use Dictionary.TryAdd).</summary>
    public List<ModMount> Mounts { get; } = new();

    /// <summary>Mount paths named by an init.con that do not exist on disk — genuinely missing dependencies.
    /// Surface these to the user: this is the difference between "your objects are missing" and a silent failure.</summary>
    public List<string> Missing { get; } = new();

    public IEnumerable<string> Paths => Mounts.Select(m => m.Path);

    /// <summary>A one-line human summary, e.g. "FHSW -> FH -> bf1942" (inherited entries marked with '+').</summary>
    public string Describe() => string.Join(" -> ", Mounts.Select(m => m.Listed ? m.Name : "+" + m.Name));
}

/// <summary>
/// Resolves a Refractor mod's MOUNT CHAIN — the ordered list of mod folders whose <c>Archives\</c> supply assets.
///
/// A mod declares its chain in <c>init.con</c> as <c>game.addModPath Mods/&lt;X&gt;/</c> lines, in precedence order
/// (itself first, its dependencies next, the base game last). Refractor mounts exactly that flat list, so the
/// starting mod's own list is AUTHORITATIVE and its order is preserved verbatim.
///
/// On top of that this resolver adds TRANSITIVE resolution: it follows each listed dependency's own init.con to
/// discover mounts the author forgot to list (the FHSW case: a mini-mod that lists only itself + FHSW is missing
/// FH, ~3 GB of objects). Those inherited mounts are APPENDED AFTER the explicit list, at the lowest precedence,
/// so they can only fill gaps and can never override what the game would actually mount. That distinction matters:
/// a dependency may name a DIFFERENT version of a mod than the author intends (real example: FHSW0.42's init.con
/// points at <c>Mods/FHSW/</c>, which on this machine is FHSW 0.73), and silently promoting that ahead of the
/// author's own list would show objects the game will not have.
///
/// Grounded in real chains: FHSW -> FH -> Bf1942; FCD (7 deep); HTroop (12 deep, includes the nested mount
/// <c>Mods/HT_Data/EoD</c>); Ballistik_FH (mounts its own sub-folder <c>Mods/Ballistik_FH/X_Flow</c>).
/// </summary>
public static class ModChain
{
    private const int MaxDepth = 24;   // real chains reach 12 (HTroop); this is a runaway guard, not a real limit

    /// <summary>Resolve the mount chain for the mod directory <paramref name="modDir"/>.</summary>
    /// <param name="gameRoot">The game install dir (the parent of <c>Mods\</c>); mount paths are relative to it.</param>
    /// <param name="modDir">The starting mod's folder.</param>
    /// <param name="includeInherited">Follow dependencies' own init.con to add mounts the author didn't list.</param>
    /// <param name="baseGameFallback">Ensure the base game mod (bf1942 / BfVietnam) is mounted last even when no
    /// init.con names it — many small mods only list themselves.</param>
    public static ModChainResult Resolve(string gameRoot, string modDir, bool includeInherited = true, bool baseGameFallback = true)
    {
        var result = new ModChainResult();
        if (string.IsNullOrWhiteSpace(modDir)) return result;
        modDir = Normalize(modDir);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        // 1) The starting mod itself, then its OWN listed mounts in order — authoritative, order preserved.
        Add(result, seen, modDir, listed: true, depth: 0);
        foreach (var dep in ReadModPaths(gameRoot, modDir, missing))
            Add(result, seen, dep, listed: true, depth: 1);

        // 2) Inherited: walk the listed mounts' own init.con breadth-first, collecting anything new. Breadth-first
        //    keeps "closer" dependencies at higher precedence than deeper ones.
        var inherited = new List<ModMount>();
        if (includeInherited)
        {
            var frontier = result.Mounts.Select(m => m.Path).ToList();
            for (int depth = 2; depth <= MaxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in frontier)
                    foreach (var dep in ReadModPaths(gameRoot, dir, missing))
                        if (Make(seen, dep, listed: false, depth) is ModMount m)
                        {
                            inherited.Add(m);
                            next.Add(dep);
                        }
                frontier = next;
            }
        }

        // 3) Splice the inherited mounts in ABOVE the base game but BELOW every explicitly listed mod.
        //    - above the base game, because a dependency like FH overrides vanilla content wholesale; if FH landed
        //      after bf1942 the first-wins lookup would hand back vanilla meshes instead of FH's (a real bug for the
        //      natural mini-mod chain "MyMod, FHSW, Bf1942", which must resolve to MyMod -> FHSW -> FH -> bf1942).
        //    - below every explicit mod, because a dependency's init.con can name a DIFFERENT version of a mod than
        //      the author mounts (FHSW0.42 points at Mods/FHSW/, i.e. 0.73); such a mod must never outrank what the
        //      game itself would load, or the editor shows objects that will not exist in game.
        string? baseDir = baseGameFallback ? BaseGameDir(gameRoot) : null;
        int insertAt = result.Mounts.Count;
        if (baseDir is not null)
        {
            int bi = result.Mounts.FindIndex(m => string.Equals(m.Path, baseDir, StringComparison.OrdinalIgnoreCase));
            if (bi >= 0) insertAt = bi;
        }
        result.Mounts.InsertRange(insertAt, inherited);

        // 4) The base game, last (lowest precedence) — only if nothing above already mounted it.
        if (baseDir is not null && !seen.Contains(baseDir))
            Add(result, seen, baseDir, listed: false, depth: MaxDepth + 1);

        foreach (var m in missing.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!result.Missing.Contains(m, StringComparer.OrdinalIgnoreCase)) result.Missing.Add(m);
        return result;
    }

    /// <summary>Convenience overload: resolve by mod NAME under <c>&lt;gameRoot&gt;\Mods\</c>.</summary>
    public static ModChainResult ResolveByName(string gameRoot, string modName, bool includeInherited = true, bool baseGameFallback = true)
        => Resolve(gameRoot, Path.Combine(gameRoot, "Mods", modName), includeInherited, baseGameFallback);

    private static void Add(ModChainResult r, HashSet<string> seen, string dir, bool listed, int depth)
    {
        if (Make(seen, dir, listed, depth) is ModMount m) r.Mounts.Add(m);
    }

    /// <summary>Claim a directory as a new mount (registering it in <paramref name="seen"/>) and build its
    /// <see cref="ModMount"/>; null when it doesn't exist or was already mounted.</summary>
    private static ModMount? Make(HashSet<string> seen, string dir, bool listed, int depth)
    {
        dir = Normalize(dir);
        if (!Directory.Exists(dir) || !seen.Add(dir)) return null;
        dir = RealCase(dir);   // init.con casing varies ("Mods/Bf1942/"); show the user the REAL folder name
        return new ModMount(new DirectoryInfo(dir).Name, dir, listed, depth);
    }

    /// <summary>Re-case an existing directory path to its true on-disk spelling. init.con paths are written in
    /// whatever case the author used ("Mods/Bf1942/", "Mods/fh", "Mods/DC_realism") and Windows resolves them
    /// case-insensitively, but the resolved chain is shown to the user — so report the real name.</summary>
    private static string RealCase(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if (di.Parent is not { Exists: true } parent) return dir;
            return parent.EnumerateDirectories(di.Name).FirstOrDefault()?.FullName ?? dir;
        }
        catch { return dir; }
    }

    /// <summary>Parse one mod's <c>init.con</c> for <c>game.addModPath</c> mounts, in file order. Tolerates the real
    /// syntactic variety found across ~200 installed mods: the verb's case varies (<c>addmodPath</c>/<c>addModPath</c>),
    /// paths may be quoted, use either slash, have a trailing slash or not, differ in case from the real folder, and
    /// may be NESTED (<c>Mods/HT_Data/EoD</c>). Comment lines (rem / ; / //) are skipped, as is any trailing comment.
    /// Paths that do not exist on disk are recorded in <paramref name="missing"/> rather than silently dropped.</summary>
    public static List<string> ReadModPaths(string gameRoot, string modDir, List<string>? missing = null)
    {
        var found = new List<string>();
        string init = Path.Combine(modDir, "init.con");
        if (!File.Exists(init)) return found;

        string[] lines;
        try { lines = File.ReadAllLines(init); } catch { return found; }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || line.StartsWith("//") || line.StartsWith(';')) continue;

            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0) continue;
            if (!line[..sp].Trim().Equals("game.addModPath", StringComparison.OrdinalIgnoreCase)) continue;

            var rest = line[(sp + 1)..].Trim();
            // strip a trailing comment (rem/;//) that follows the argument
            int cut = rest.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0) rest = rest[..cut].Trim();
            cut = rest.IndexOf(';');
            if (cut >= 0) rest = rest[..cut].Trim();
            rest = rest.Trim('"').Trim();
            if (rest.Length == 0) continue;

            var rel = rest.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
                          .TrimEnd(Path.DirectorySeparatorChar);
            if (rel.Length == 0) continue;

            string abs;
            try { abs = Normalize(Path.Combine(gameRoot, rel)); } catch { continue; }

            if (Directory.Exists(abs)) { if (!found.Contains(abs, StringComparer.OrdinalIgnoreCase)) found.Add(abs); }
            else missing?.Add(rel);
        }
        return found;
    }

    /// <summary>The base-game mod folder under <paramref name="gameRoot"/> (BF1942 or BFV), or null.</summary>
    public static string? BaseGameDir(string gameRoot)
        => new[] { "bf1942", "BfVietnam", "bfvietnam" }
            .Select(b => Path.Combine(gameRoot, "Mods", b))
            .Where(Directory.Exists)
            .Select(Normalize)
            .FirstOrDefault();

    /// <summary>The game install dir for a path inside <c>&lt;gameRoot&gt;\Mods\&lt;Mod&gt;\...</c> (the parent of the
    /// nearest <c>Mods</c> ancestor), or null when the path is not inside a Battlefield Mods folder.</summary>
    public static string? FindGameRoot(string anyPathInsideMods)
    {
        try
        {
            for (var d = new DirectoryInfo(Path.GetFullPath(anyPathInsideMods).TrimEnd('\\', '/')); d is not null; d = d.Parent)
                if (d.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase) && d.Parent is not null)
                    return d.Parent.FullName;
        }
        catch { }
        return null;
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return p; }
    }

    // ---- Archive collection over a resolved chain ----

    /// <summary>Split a resolved chain's archives into the mesh/object list and the texture list, in precedence
    /// order. Level archives are excluded (levels are opened separately). <paramref name="skipNonAsset"/> drops
    /// archives that can never contain editor-visible geometry or textures (sound/music/menu/movies), which on a
    /// full FHSW chain removes a large amount of pointless I/O.</summary>
    public static (string[] mesh, string[] tex) CollectArchives(ModChainResult chain, bool skipNonAsset = true)
    {
        var mesh = new List<string>();
        var tex = new List<string>();
        foreach (var mount in chain.Mounts)
        {
            var archivesDir = Path.Combine(mount.Path, "Archives");
            var root = Directory.Exists(archivesDir) ? archivesDir : mount.Path;
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                var leaf = Path.GetFileName(f);
                if (leaf.StartsWith("~")) continue;                 // ~$ lock/temp leftovers
                if (IsLevelArchive(f)) continue;                    // levels are loaded separately
                if (skipNonAsset && IsNonAssetArchive(leaf)) continue;
                if (IsTextureArchive(leaf)) { if (!tex.Contains(f, StringComparer.OrdinalIgnoreCase)) tex.Add(f); }
                else if (!mesh.Contains(f, StringComparer.OrdinalIgnoreCase)) mesh.Add(f);
            }
        }
        return (mesh.ToArray(), tex.ToArray());
    }

    public static bool IsLevelArchive(string path) => path.Replace('\\', '/').ToLowerInvariant().Contains("/levels/");
    public static bool IsTextureArchive(string leaf) => leaf.StartsWith("texture", StringComparison.OrdinalIgnoreCase);

    /// <summary>Archives that hold no geometry/texture the editor can show (audio, music, menu art, movies, AI
    /// meshes). Skipping these on a 300-archive FHSW chain avoids opening ~gigabytes for nothing.</summary>
    public static bool IsNonAssetArchive(string leaf)
    {
        var n = Path.GetFileNameWithoutExtension(leaf).ToLowerInvariant();
        return n is "sound" or "sounds" or "music" or "menu" or "movies" or "movie" or "font" or "fonts"
            || n.StartsWith("sound_") || n.StartsWith("music_") || n.StartsWith("menu_");
    }
}
