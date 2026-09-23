using System.Text.RegularExpressions;

namespace RefractorForge.Formats;

/// <summary>The two Refractor games whose executables mount different archive lists.</summary>
public enum RefractorGame { BF1942, BFV }

/// <summary>One mod-level archive the engine mounts from <c>Mods\&lt;mod&gt;\Archives\</c>.</summary>
/// <param name="Stem">Archive name without extension, as the retail file spells it (<c>standardMesh</c>).</param>
/// <param name="RelativePath">Path under <c>Archives\</c>, e.g. <c>Objects.rfa</c> or <c>bf1942\Game.rfa</c>.</param>
/// <param name="MountRoot">The prefix every entry must start with - the archive's own path under <c>Archives\</c>
/// minus <c>.rfa</c> - e.g. <c>objects/</c> or <c>bf1942/game/</c>. One entry outside it makes the engine reject the
/// whole archive ("Error loading file list").</param>
/// <param name="PatchMounted">Whether the engine also mounts <c>&lt;stem&gt;_001.rfa</c>, ahead of the base. The
/// executables carry a LITERAL list: no other number, and no patch at all for most stems (there is no
/// <c>objects_001</c>).</param>
/// <param name="RetailCompressed">The header flag the retail archive of this kind carries.</param>
public sealed record MountedArchive(string Stem, string RelativePath, string MountRoot, bool PatchMounted, bool RetailCompressed)
{
    /// <summary>Path under <c>Archives\</c> of the one patch the engine mounts over this archive, or null when it
    /// mounts none.</summary>
    public string? PatchRelativePath => PatchMounted ? RelativePath[..^4] + "_001.rfa" : null;
}

/// <summary>Where an archive ENTRY belongs: a mod-level archive, or a level (<see cref="Map"/> set).</summary>
public sealed record MountTarget(MountedArchive? Archive, string? Map, string RelativePath, string MountRoot)
{
    public bool IsLevel => Map is not null;
}

/// <summary>One archive FILE of a mod folder that the engine mounts.</summary>
/// <param name="Path">The file on disk.</param>
/// <param name="RelativePath">Its path under <c>Archives\</c>, spelled as on disk (under the scanned folder for a loose
/// one).</param>
/// <param name="Archive">The mod-level archive it is, base or patch; null for a level.</param>
/// <param name="Map">The level it belongs to; null for a mod-level archive. Both are null for a file of a loose folder,
/// which the table cannot place (see <see cref="GameMounts.Scan"/>).</param>
/// <param name="Patch">-1 for a base; 1 for a mod-level <c>_001</c>; N for a level's <c>_NNN</c> (or a loose
/// <c>_NNN</c> beside its base).</param>
public sealed record ModArchive(string Path, string RelativePath, MountedArchive? Archive, string? Map, int Patch)
{
    public bool IsLevel => Map is not null;
    public bool IsPatch => Patch >= 0;
}

/// <summary>A mod folder's archives as the engine sees them: what it mounts, in precedence order (first wins), and
/// what it never reads.</summary>
public sealed record ModArchiveScan(IReadOnlyList<ModArchive> Mounted, IReadOnlyList<string> Unmounted);

/// <summary>
/// The archives each Refractor executable mounts from a mod folder, as data. The list is LITERAL, taken from the
/// string table inside each retail executable (BF1942.exe 0x4D297C-0x4D2A68, BfVietnam.exe 0x744B14-0x744BEC) and
/// checked against the clean installs: a name that is not on it - <c>objects_001.rfa</c>, a BFV
/// <c>standardMesh_001.rfa</c> or <c>menu_001.rfa</c>, any <c>_002</c> - is never read, and where a <c>_001</c> patch
/// IS mounted it outranks its base. Levels mount as <c>&lt;base mod&gt;\levels\&lt;Map&gt;.rfa</c> plus its
/// <c>_NNN</c> patches, highest number first.
///
/// This is the table RefractorDevelopmentKit's GameProfile carried; it lives here so the map editor, Livery, the
/// Archive app and RDK resolve a mod's files the same way. <see cref="Union"/> is the fallback for a folder whose
/// game cannot be told (see <see cref="Detect(string)"/>): every name either game mounts, patches still ahead of
/// their bases.
/// </summary>
public sealed class GameMounts
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;
    private static readonly Regex LevelPatch = new(@"^(.+)_(\d+)$", RegexOptions.Compiled);

    /// <summary>The game, or null for <see cref="Union"/>.</summary>
    public RefractorGame? Game { get; }
    public string DisplayName { get; }
    /// <summary>The retail mod every other mod sits on, which is also the folder under <c>Archives\</c> that holds
    /// its <c>game.rfa</c> and <c>levels\</c>: <c>bf1942</c> / <c>BfVietnam</c>. <see cref="Union"/> has both.</summary>
    public IReadOnlyList<string> BaseMods { get; }
    public string BaseMod => BaseMods[0];
    /// <summary>The mounted mod-level archives, in the order the executable lists them.</summary>
    public IReadOnlyList<MountedArchive> Archives { get; }
    /// <summary>Executables at a game root that say which game it is.</summary>
    public IReadOnlyList<string> Executables { get; }

    private GameMounts(RefractorGame? game, string display, IReadOnlyList<string> baseMods,
                       IReadOnlyList<MountedArchive> archives, IReadOnlyList<string> exes)
    {
        Game = game; DisplayName = display; BaseMods = baseMods; Archives = archives; Executables = exes;
    }

    public static readonly GameMounts Bf1942 = new(
        RefractorGame.BF1942, "Battlefield 1942", new[] { "bf1942" },
        new MountedArchive[]
        {
            // BF1942.exe string table (0x4D297C-0x4D2A68): menu, Bf1942/game, menu_001, aiMeshes, ai, standardMesh,
            // standardMesh_001, sound, sound_001, texture, texture_001, treeMesh, font, animations, objects, shaders.
            new("objects",      "Objects.rfa",       "objects/",      false, true),
            new("standardMesh", "standardMesh.rfa",  "standardMesh/", true,  true),
            new("texture",      "texture.rfa",       "texture/",      true,  true),
            new("treeMesh",     "treeMesh.rfa",      "treeMesh/",     false, true),
            new("animations",   "animations.rfa",    "animations/",   false, true),
            new("sound",        "sound.rfa",         "sound/",        true,  true),
            new("menu",         "menu.rfa",          "menu/",         true,  true),
            new("ai",           "ai.rfa",            "ai/",           false, true),
            new("aiMeshes",     "aiMeshes.rfa",      "aiMeshes/",     false, true),
            new("font",         "Font.rfa",          "font/",         false, true),
            new("shaders",      "shaders.rfa",       "shaders/",      false, true),
            new("game",         @"bf1942\Game.rfa",  "bf1942/game/",  false, true),
        },
        new[] { "BF1942.exe", "BF1942_r.exe", "BF1942_w32ded.exe" });

    public static readonly GameMounts Bfv = new(
        RefractorGame.BFV, "Battlefield Vietnam", new[] { "BfVietnam" },
        new MountedArchive[]
        {
            // BfVietnam.exe string table (0x744B14-0x744BEC): menu, BfVietnam/game, aiMeshes, ai, standardMesh,
            // effects, music, sound, sound_001, texture, texture_001, font, animations, animations_001, objects.
            // No treeMesh or shaders; no standardMesh_001 or menu_001.
            new("objects",      "objects.rfa",         "objects/",         false, true),
            new("standardMesh", "standardMesh.rfa",    "standardMesh/",    false, true),
            new("texture",      "texture.rfa",         "texture/",         true,  true),
            new("animations",   "animations.rfa",      "animations/",      true,  true),
            // Retail BFV sound and music are UNCOMPRESSED, and no compressed music.rfa exists anywhere.
            new("sound",        "sound.rfa",           "sound/",           true,  false),
            new("music",        "music.rfa",           "music/",           false, false),
            new("menu",         "menu.rfa",            "menu/",            false, true),
            new("effects",      "effects.rfa",         "effects/",         false, true),
            new("ai",           "ai.rfa",              "ai/",              false, true),
            new("aiMeshes",     "aiMeshes.rfa",        "aiMeshes/",        false, true),
            new("font",         "font.rfa",            "font/",            false, true),
            new("game",         @"BfVietnam\game.rfa", "BfVietnam/game/",  false, true),
        },
        new[] { "BfVietnam.exe", "BfVietnam_DEBUG.exe", "bfvietnam_w32ded.exe" });

    /// <summary>The fallback for an unidentified game: every archive either executable mounts, a patch mounted when
    /// either game mounts it (so <c>standardMesh_001</c>, <c>menu_001</c> and <c>animations_001</c> all count, but
    /// never <c>objects_001</c> or a <c>_002</c>), and levels under both base-mod folders. It over-includes rather
    /// than drop real content; <see cref="MountedArchive.RetailCompressed"/> means nothing here.</summary>
    public static readonly GameMounts Union = Merge(Bf1942, Bfv);

    /// <summary>The two real games.</summary>
    public static IReadOnlyList<GameMounts> Games { get; } = new[] { Bf1942, Bfv };

    public static GameMounts For(RefractorGame? game) => game switch
    {
        RefractorGame.BF1942 => Bf1942,
        RefractorGame.BFV => Bfv,
        _ => Union,
    };

    /// <summary>The table for a resolved chain: its game when that can be told, else <see cref="Union"/>.</summary>
    public static GameMounts For(ModChainResult chain) => For(Detect(chain));

    private static GameMounts Merge(GameMounts a, GameMounts b)
    {
        var list = a.Archives.ToList();
        foreach (var s in b.Archives)
        {
            int i = list.FindIndex(x => x.RelativePath.Equals(s.RelativePath, Ci));
            if (i < 0) list.Add(s);
            else if (s.PatchMounted && !list[i].PatchMounted) list[i] = list[i] with { PatchMounted = true };
        }
        return new GameMounts(null, "unidentified game", a.BaseMods.Concat(b.BaseMods).ToArray(), list,
                              a.Executables.Concat(b.Executables).ToArray());
    }

    // ---- Which game ----

    /// <summary>Which game an install is: by the executables at its root, else by which base-mod folder sits under
    /// <c>Mods\</c>. Null when neither says one game (nothing there, or both).</summary>
    public static RefractorGame? Detect(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) return null;
        var byExe = Games.Where(g => g.Executables.Any(e => File.Exists(Path.Combine(gameRoot, e)))).ToList();
        if (byExe.Count == 1) return byExe[0].Game;
        var byMod = Games.Where(g => Directory.Exists(Path.Combine(gameRoot, "Mods", g.BaseMod))).ToList();
        return byMod.Count == 1 ? byMod[0].Game : null;
    }

    /// <summary>Which game a resolved chain runs on: <see cref="ModChainResult.Game"/> when set, else its install
    /// (<see cref="Detect(string)"/>), else the one base mod the chain mounts through an init.con - one the starting
    /// mod lists first, then one a dependency's init.con names. The base game the resolver appends on its own
    /// (<see cref="ModMount.IsBaseGameFallback"/>) never decides: where the install cannot be told it was picked
    /// blind. Null when none of those decides.</summary>
    public static RefractorGame? Detect(ModChainResult chain)
    {
        if (chain.Game is { } known) return known;
        var root = chain.GameRoot ?? chain.Mounts.Select(m => ModChain.FindGameRoot(m.Path)).FirstOrDefault(r => r is not null);
        if (Detect(root) is { } g) return g;
        foreach (var listedOnly in new[] { true, false })
        {
            var byBase = Games.Where(x => chain.Mounts.Any(m => (m.Listed || !listedOnly) && !m.IsBaseGameFallback
                                                               && m.Name.Equals(x.BaseMod, Ci))).ToList();
            if (byBase.Count == 1) return byBase[0].Game;
        }
        return null;
    }

    /// <summary>Which game a lone folder belongs to - a mod folder, its <c>Archives\</c>, or a game root - when no
    /// chain was resolved. Null when it cannot be told.</summary>
    public static RefractorGame? DetectFolder(string dir)
    {
        try
        {
            if (Detect(ModChain.FindGameRoot(dir)) is { } g) return g;
            for (var d = new DirectoryInfo(Path.GetFullPath(dir).TrimEnd('\\', '/')); d is not null; d = d.Parent)
                if (d.Parent is { } p && p.Name.Equals("Mods", Ci))
                {
                    var byName = Games.Where(x => x.BaseMod.Equals(d.Name, Ci)).ToList();
                    if (byName.Count == 1) return byName[0].Game;
                    break;
                }
            return Directory.Exists(Path.Combine(dir, "Mods")) ? Detect(dir) : null;
        }
        catch { return null; }
    }

    // ---- The table ----

    public MountedArchive? ArchiveByStem(string stem) => Archives.FirstOrDefault(s => s.Stem.Equals(stem, Ci));

    /// <summary>Whether the engine mounts an archive at this path under <c>Archives\</c> at all. <c>objects_001.rfa</c>,
    /// a BFV <c>standardMesh_001.rfa</c> or any <c>_002</c> are never read; a level (any <c>.rfa</c> directly in
    /// <c>&lt;base mod&gt;\levels\</c>) always is.</summary>
    public bool IsMounted(string relativePath) => Classify(relativePath, _ => false) is not null;

    /// <summary>Which archive an ENTRY belongs to: the archive whose mount root is the longest prefix of the name, or
    /// a level (<c>&lt;base mod&gt;/levels/&lt;Map&gt;/...</c>). Null when the name fits no mounted archive.</summary>
    public MountTarget? TargetOf(string entryName)
    {
        var n = entryName.Replace('\\', '/');
        foreach (var sub in BaseMods)
        {
            var levels = sub + "/levels/";
            if (!n.StartsWith(levels, Ci)) continue;
            var rest = n[levels.Length..];
            int slash = rest.IndexOf('/');
            if (slash <= 0) return null;                    // a file directly in levels/ belongs to no level
            var map = rest[..slash];
            return new MountTarget(null, map, $@"{sub}\levels\{map}.rfa", levels + map + "/");
        }
        MountedArchive? best = null;
        foreach (var s in Archives)
            if (n.StartsWith(s.MountRoot, Ci) && (best is null || s.MountRoot.Length > best.MountRoot.Length))
                best = s;
        return best is null ? null : new MountTarget(best, null, best.RelativePath, best.MountRoot);
    }

    /// <summary>
    /// A mod folder's archives, the way the engine mounts them. Precedence runs mounted <c>_001</c> patch before its
    /// base, and a level's <c>_NNN</c> patches highest first before the level; between different archives the order
    /// is by path (top-level archives, then <c>&lt;base mod&gt;\game.rfa</c>, then levels). Anything the executable
    /// never reads lands in <see cref="ModArchiveScan.Unmounted"/>.
    ///
    /// A folder with an <c>Archives\</c> sub-folder is a mod, and paths are taken under it. Any other folder - an
    /// <c>Archives</c> folder itself, a <c>&lt;base mod&gt;\levels</c> folder inside one, a game root - has each file
    /// placed by its own path under the <c>Archives</c> folder it is mounted from (<see cref="ArchivesFolderOf"/>), so
    /// every view of a file agrees on whether the engine reads it. A LOOSE folder, with no such folder on or under it
    /// (a download of map archives), is nothing the engine mounts as it stands, so the table cannot judge it: every
    /// archive in it is listed, each numbered <c>_NNN</c> patch ahead of the base beside it, highest first
    /// (<see cref="ModArchive.Archive"/> and <see cref="ModArchive.Map"/> both null).
    /// </summary>
    public ModArchiveScan Scan(string modDir, bool levelsToo = true)
    {
        var empty = new ModArchiveScan(Array.Empty<ModArchive>(), Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(modDir)) return empty;
        var archivesDir = Path.Combine(modDir, "Archives");
        bool isMod = Directory.Exists(archivesDir);
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(isMod ? archivesDir : modDir)); }
        catch { return empty; }
        if (!Directory.Exists(root)) return empty;

        List<string> files;
        try { files = Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories).ToList(); }
        catch { return empty; }
        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        var placed = files.Select(f => (File: f, Base: isMod ? root : ArchivesFolderOf(f, root))).ToList();
        if (placed.All(p => p.Base is null)) return ScanLoose(root, files, present, levelsToo);

        var mounted = new List<(ModArchive A, string Base, string Group)>();
        var unmounted = new List<string>();
        foreach (var (f, baseDir) in placed)
        {
            if (baseDir is null) { unmounted.Add(f); continue; }   // in a game tree but in no Archives folder: never read
            var rel = Path.GetRelativePath(baseDir, f);
            var dir = Path.GetDirectoryName(f) ?? "";
            // "Wake_Evenings" and "Kursk_1943" are maps: a numbered suffix is a patch only beside its base level.
            var hit = Classify(rel, map => present.Contains(Path.Combine(dir, map + ".rfa")));
            if (hit is not { } h) { unmounted.Add(f); continue; }
            if (h.Map is not null && !levelsToo) continue;
            mounted.Add((new ModArchive(f, rel, h.Archive, h.Map, h.Patch), baseDir, h.Group));
        }

        var ordered = mounted
            .OrderBy(m => m.Base, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Group.Count(c => c == '\\'))
            .ThenBy(m => m.Group, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(m => m.A.Patch)
            .Select(m => m.A).ToList();
        return new ModArchiveScan(ordered, unmounted);
    }

    /// <summary>A loose folder (see <see cref="Scan"/>): every archive, grouped by folder and base name, a numbered
    /// patch beside its base ahead of it and highest first - how ModWorkspace listed any folder before the mount list
    /// existed. Without levels, any file on a <c>levels</c> path is left out, as it was then.</summary>
    private static ModArchiveScan ScanLoose(string root, List<string> files, HashSet<string> present, bool levelsToo)
    {
        var list = new List<(ModArchive A, string Key)>();
        foreach (var f in files)
        {
            if (!levelsToo && ModChain.IsLevelArchive(f)) continue;
            var dir = Path.GetDirectoryName(f) ?? "";
            var (group, patch) = (Path.GetFileNameWithoutExtension(f), -1);
            if (LevelPatch.Match(group) is { Success: true } m && int.TryParse(m.Groups[2].Value, out int n)
                && present.Contains(Path.Combine(dir, m.Groups[1].Value + ".rfa")))
                (group, patch) = (m.Groups[1].Value, n);
            list.Add((new ModArchive(f, Path.GetRelativePath(root, f), null, null, patch), Path.Combine(dir, group)));
        }
        var ordered = list.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.A.Patch)
                          .Select(x => x.A).ToList();
        return new ModArchiveScan(ordered, Array.Empty<string>());
    }

    /// <summary>
    /// The <c>Archives</c> folder the engine would mount a file from, read off the file's own path. Inside a
    /// <c>Mods</c> tree it is the shallowest <c>Archives</c> below the nearest <c>Mods</c> folder: the mod's own, also
    /// for a nested mod such as <c>Mods\Ballistik_FH\X_Flow</c>, while an <c>Archives\Archives\</c> inside it is only a
    /// sub-folder the engine never looks in (FHR ships its font.rfa there). Outside a <c>Mods</c> tree it is the
    /// shallowest at or below <paramref name="root"/>, else the nearest above it when the path below that folder runs
    /// through a base mod's folder (<c>Archives\bf1942\levels</c> picked on its own) - a folder that merely sits under
    /// some unrelated "Archives" folder is not placed by it. Null when there is none: loose, or never mounted.
    /// </summary>
    internal static string? ArchivesFolderOf(string file, string root)
    {
        string[] seg, rootSeg;
        try
        {
            seg = Path.GetFullPath(file).Split('\\', '/');
            rootSeg = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Split('\\', '/');
        }
        catch { return null; }
        int last = seg.Length - 1;                              // the file's own name
        string Upto(int i) => string.Join(Path.DirectorySeparatorChar, seg[..(i + 1)]);
        bool IsArchives(int i) => seg[i].Equals("Archives", Ci);

        for (int mods = last - 1; mods >= 0; mods--)
            if (seg[mods].Equals("Mods", Ci))
            {
                for (int i = mods + 1; i < last; i++) if (IsArchives(i)) return Upto(i);
                return null;
            }
        int top = Math.Clamp(rootSeg.Length - 1, 0, last);    // the root's own segment
        for (int i = top; i < last; i++) if (IsArchives(i)) return Upto(i);
        for (int i = top - 1; i >= 0; i--)
            if (IsArchives(i))
                return i + 1 < last && Union.BaseMods.Any(b => b.Equals(seg[i + 1], Ci)) ? Upto(i) : null;
        return null;
    }

    // ---- Archive files picked one by one ----

    /// <summary>Whether the engine mounts this archive FILE: placed under the <c>Archives</c> folder it sits in
    /// (<see cref="ArchivesFolderOf"/>) and judged by its own install's table (<see cref="DetectFolder"/>). A file in no
    /// Archives folder cannot be judged and counts as mounted, the way <see cref="Scan"/> lists a loose folder whole.</summary>
    public static bool IsMountedFile(string file)
    {
        try { return Place(file) is not { } p || p.Table.IsMounted(p.Rel); }
        catch { return true; }
    }

    /// <summary>
    /// Archive files chosen one by one (a file dialog, a saved project, a list collected elsewhere) put in the order the
    /// engine layers them: each archive gets the patches it mounts over it placed right ahead of it - its <c>_001</c>
    /// where the table mounts one (<see cref="MountedArchive.PatchRelativePath"/>), a level's <c>_NNN</c> highest first
    /// - added when missing and moved up when chosen after their base. A patch the game never reads (objects_001, a BFV
    /// standardMesh_001, any <c>_002</c>) is never added, but a file chosen by hand is kept. A file in no Archives folder
    /// takes every numbered sibling as a patch, as a loose folder does. Otherwise the order is kept; duplicates go.
    /// </summary>
    public static List<string> WithMountedPatches(IEnumerable<string> archives)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string p) { if (seen.Add(Path.GetFullPath(p))) result.Add(p); }
        foreach (var a in archives)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            try
            {
                foreach (var patch in PatchesOver(Path.GetFullPath(a))) Add(patch);
                Add(a);
            }
            catch { /* an unusable path is skipped, as the libraries skip an archive they cannot open */ }
        }
        return result;
    }

    /// <summary>The patches the engine mounts over an archive file that exist beside it, highest precedence first.</summary>
    private static IEnumerable<string> PatchesOver(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<string>();
        var stem = Path.GetFileNameWithoutExtension(file);
        bool BaseBeside(string map) => File.Exists(Path.Combine(dir, map + ".rfa"));
        if (Place(file) is not { } p)                          // loose: a numbered name beside its base is a patch
            return LevelPatch.Match(stem) is { Success: true } m && BaseBeside(m.Groups[1].Value)
                ? Array.Empty<string>() : NumberedSiblings(dir, stem);
        if (p.Table.Classify(p.Rel, BaseBeside) is not { Patch: < 0 } hit) return Array.Empty<string>();   // a patch, or never read
        if (hit.Map is not null) return NumberedSiblings(dir, stem);
        return hit.Archive?.PatchRelativePath is { } patch
            ? Directory.EnumerateFiles(dir, Path.GetFileName(patch)).Take(1).ToArray()   // as spelled on disk
            : Array.Empty<string>();
    }

    private static string[] NumberedSiblings(string dir, string stem)
    {
        var rx = new Regex("^" + Regex.Escape(stem) + @"_(\d+)$", RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(dir, stem + "_*.rfa")
            .Select(s => (Path: s, M: rx.Match(Path.GetFileNameWithoutExtension(s))))
            .Where(t => t.M.Success && long.TryParse(t.M.Groups[1].Value, out _))
            .OrderByDescending(t => long.Parse(t.M.Groups[1].Value))
            .Select(t => t.Path).ToArray();
    }

    /// <summary>A file's install table and its path under the Archives folder it is mounted from; null when loose.</summary>
    private static (GameMounts Table, string Rel)? Place(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        if (ArchivesFolderOf(file, dir) is not { } arc) return null;
        return (For(DetectFolder(Path.GetDirectoryName(arc) ?? arc)), Path.GetRelativePath(arc, file));
    }

    /// <summary>Place one path under <c>Archives\</c>: the archive (or level) it is, its patch number, and the base's
    /// path that groups a patch with it. Null when the engine never mounts it.</summary>
    private (MountedArchive? Archive, string? Map, int Patch, string Group)? Classify(string relativePath, Func<string, bool> levelBaseExists)
    {
        var r = relativePath.Replace('/', '\\').TrimStart('\\');
        foreach (var a in Archives)
        {
            if (r.Equals(a.RelativePath, Ci)) return (a, null, -1, a.RelativePath);
            if (a.PatchRelativePath is { } p && r.Equals(p, Ci)) return (a, null, 1, a.RelativePath);
        }
        var seg = r.Split('\\');
        if (seg.Length != 3 || !seg[1].Equals("levels", Ci) || !seg[2].EndsWith(".rfa", Ci)
            || !BaseMods.Any(b => b.Equals(seg[0], Ci)))
            return null;
        var stem = seg[2][..^4];
        if (LevelPatch.Match(stem) is { Success: true } m && int.TryParse(m.Groups[2].Value, out int n)
            && levelBaseExists(m.Groups[1].Value))
            return (null, m.Groups[1].Value, n, $@"{seg[0]}\levels\{m.Groups[1].Value}.rfa");
        return (null, stem, -1, $@"{seg[0]}\levels\{stem}.rfa");
    }
}
