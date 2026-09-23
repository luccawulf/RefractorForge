using System.Text;
using System.Text.RegularExpressions;
using RefractorBridge.Con;
using RefractorBridge.Oracle;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;

namespace RefractorBridge.Level;

/// <summary>One file as it will appear in the ported archive, and where it came from.</summary>
public sealed record PortedFile(string Name, byte[] Data, PortOrigin Origin, string? Note = null);

public enum PortOrigin
{
    /// <summary>Carried across byte for byte - terrain, textures, binary payloads.</summary>
    Copied,
    /// <summary>A script the dialect engine rewrote.</summary>
    Rewritten,
    /// <summary>Synthesised because Battlefield Vietnam requires it and BF1942 has no equivalent.</summary>
    Generated,
}

/// <summary>What happened to the level's static object placements.</summary>
public sealed record StaticsReport(int Total, int Kept, int Dropped, IReadOnlyList<(string Template, int Count)> Unresolved);

public sealed record LevelPortResult(
    string SourceLevel,
    string TargetLevel,
    IReadOnlyList<PortedFile> Files,
    IReadOnlyList<ConChange> Changes,
    IReadOnlyList<ConReview> Reviews,
    IReadOnlyList<string> DroppedEntries,
    StaticsReport Statics,
    /// <summary>Level-local templates that parse but could never draw - declared geometry that is never
    /// declared, or a mesh the level does not ship. These null-deref on CONSTRUCT, not on load.</summary>
    IReadOnlyList<string> BrokenGeometry);

public sealed record PortOptions
{
    public StockCensus? Census { get; init; }
    public ConDialectOptions Dialect { get; init; } = new();

    /// <summary>
    /// Template names that resolve even though retail does not define them - level-local templates the source
    /// declares, or content you have already embedded. Placements of anything else are dropped.
    /// </summary>
    public IReadOnlySet<string> ExtraTemplates { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop static placements whose template cannot be resolved. Off leaves them in.</summary>
    public bool DropUnresolvedStatics { get; init; } = true;

    /// <summary>
    /// Files produced elsewhere to ship with the level - the embedded objects, their meshes and their textures
    /// (see <see cref="ObjectEmbedder"/>). When an <c>Objects/Objects.con</c> is among them, Init.con is given
    /// the <c>run</c> that loads it.
    /// </summary>
    public IReadOnlyList<PortedFile> AdditionalFiles { get; init; } = Array.Empty<PortedFile>();

    /// <summary>
    /// The level's patch archives, LOWEST NUMBER FIRST. A BF1942 level ships as a base archive plus
    /// <c>&lt;Level&gt;_NNN.rfa</c> patches, one per game patch, and each later one REPLACES entries in the
    /// earlier ones. Berlin's Menu/init.con exists three times and grows each step: the base ends at
    /// setLoadPicture, _003 adds setMapId, _006 repoints setLoadPicture at the menu art that _006 itself
    /// ships. Porting the base alone silently uses stale scripts - 18 of Berlin's files, including Init.con,
    /// every AI file and every game-mode file.
    /// </summary>
    public IReadOnlyList<RefractorFlatArchive> PatchArchives { get; init; } = Array.Empty<RefractorFlatArchive>();

    /// <summary>
    /// Raise <c>renderer.diffuseColor</c> into retail's band when the level's BF1942 ambient command was
    /// dropped. Not a second-guess of the artist: it compensates for light the converter itself removed.
    /// </summary>
    public bool CompensateLighting { get; init; } = true;
}

/// <summary>
/// Turns a BF1942 level archive into a Battlefield Vietnam one.
///
/// The layout both games use is nearly the same, so most of this is carrying bytes across unchanged - the
/// heightmap, the material map, the terrain texture grid and the palette are all identical formats. What
/// actually has to change is narrow and specific:
/// <list type="bullet">
/// <item><b>Paths.</b> Every archive entry moves from <c>bf1942/levels/&lt;Old&gt;/</c> to
/// <c>BfVietnam/levels/&lt;New&gt;/</c>, and the paths written INSIDE scripts have to follow - the terrain
/// script names its own heightmap, material map and texture base by absolute archive path.</item>
/// <item><b>Dialect.</b> Every script goes through <see cref="ConDialect"/>.</item>
/// <item><b>Growth.</b> BFV requires the foliage pair; BF1942 has no equivalent, so empty ones are generated
/// (see <see cref="GrowthStubs"/>).</item>
/// <item><b>Files BFV never reads</b> - PreCache, texturePreCache, WaterShader.rs, cullRadius.con and the
/// editor's .bak files - are dropped rather than carried as dead weight.</item>
/// <item><b>Statics</b> whose template cannot resolve are dropped and reported, because a placement naming a
/// template that does not exist is exactly the kind of fault that only shows up as a mode-specific crash.</item>
/// </list>
/// </summary>
public static class LevelPorter
{
    private const string TargetPrefix = "BfVietnam/levels/";

    /// <summary>Entries BFV never reads. Carried across they are dead weight at best.</summary>
    private static readonly string[] DropFileNames =
    {
        "precache.con", "texturepreCache.dat", "texturepreCache.con", "watershader.rs", "cullradius.con",
        "thumbs.db",
    };

    public static LevelPortResult Port(RefractorFlatArchive source, string targetLevel, PortOptions? options = null)
    {
        options ??= new PortOptions();

        string sourceLevel = DetectLevelName(source)
                             ?? throw new InvalidDataException("could not work out the level name from the archive's entries");
        // Either game's prefix - porting a BFV level under a new name is the control that proves the
        // pipeline itself, separately from anything BF1942-specific.
        string sourcePrefix = SourcePrefix(source, sourceLevel);

        var files = new List<PortedFile>();
        var changes = new List<ConChange>();
        var reviews = new List<ConReview>();
        var dropped = new List<string>();

        // Templates the ported level can legitimately place: whatever retail BFV owns, whatever the source
        // declares locally, plus anything the caller says it has already embedded.
        var resolvable = new HashSet<string>(options.ExtraTemplates, StringComparer.OrdinalIgnoreCase);
        var (localTemplates, brokenGeometry) = ResolveLocalTemplates(source);
        foreach (string local in localTemplates) resolvable.Add(local);

        var dialect = options.Dialect with { Census = options.Dialect.Census ?? options.Census };
        StaticsReport? statics = null;

        // Resolve every entry across base + patches, later archives winning. Names differ in case between
        // archives ("Bf1942/Levels/berlin/AI.con" vs "bf1942/levels/Berlin/AI.con"), so the map is
        // case-insensitive - as the engine is.
        var resolved = new Dictionary<string, (RefractorFlatArchive Archive, RefractorFlatArchiveEntry Entry)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var archive in new[] { source }.Concat(options.PatchArchives))
            foreach (var e in archive.Entries)
                resolved[e.Name] = (archive, e);

        foreach (var (archive, entry) in resolved.Values.OrderBy(v => v.Entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            string relative = Relative(entry.Name, sourcePrefix);
            if (relative.Length == 0) { dropped.Add(entry.Name); continue; }

            if (ShouldDrop(relative)) { dropped.Add(entry.Name); continue; }

            byte[] data = archive.Read(entry);
            string target = TargetPrefix + targetLevel + "/" + RenameForBfv(relative);

            if (relative.EndsWith(".con", StringComparison.OrdinalIgnoreCase))
            {
                bool isStatics = Path.GetFileName(relative).Equals("StaticObjects.con", StringComparison.OrdinalIgnoreCase);
                if (isStatics)
                {
                    var (filtered, report) = FilterStatics(data, resolvable, options);
                    statics = report;
                    data = filtered;
                }

                var rewritten = ConDialect.Rewrite(data, relative, dialect);
                changes.AddRange(rewritten.Changes);
                reviews.AddRange(rewritten.Reviews);

                byte[] final = RepathScript(rewritten.Bytes, sourceLevel, targetLevel);
                final = ApplyScriptSpecials(relative, final, rewritten.Changes, options, changes);

                files.Add(new PortedFile(target, final, PortOrigin.Rewritten));
                continue;
            }

            if (relative.EndsWith(".wst", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new PortedFile(target, RepathScript(data, sourceLevel, targetLevel), PortOrigin.Rewritten));
                continue;
            }

            files.Add(new PortedFile(target, data, PortOrigin.Copied));
        }

        MergeAdditionalFiles(files, options.AdditionalFiles, targetLevel);
        AddGeneratedFiles(files, targetLevel);

        return new LevelPortResult(sourceLevel, targetLevel, files, changes, reviews, dropped,
            statics ?? new StaticsReport(0, 0, 0, Array.Empty<(string, int)>()), brokenGeometry);
    }

    // --- naming ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A level's patch archives beside it, LOWEST NUMBER FIRST so a caller can let later ones win.
    /// The suffixes are game patch versions and are not contiguous - Berlin ships _000, _003 and _006.
    /// </summary>
    public static IReadOnlyList<string> PatchArchivePaths(string levelArchivePath)
    {
        string dir = Path.GetDirectoryName(levelArchivePath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(levelArchivePath);

        var found = new List<(int Number, string Path)>();
        foreach (string candidate in Directory.EnumerateFiles(dir, "*.rfa"))
        {
            string name = Path.GetFileNameWithoutExtension(candidate);
            var m = Regex.Match(name, @"^(.*)_(\d{3})$");
            if (!m.Success) continue;
            if (!string.Equals(m.Groups[1].Value, stem, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add((int.Parse(m.Groups[2].Value), candidate));
        }

        return found.OrderBy(f => f.Number).Select(f => f.Path).ToList();
    }

    /// <summary>Work out the level's own folder name from the entry paths.</summary>
    public static string? DetectLevelName(RefractorFlatArchive archive)
    {
        foreach (var e in archive.Entries)
        {
            var m = Regex.Match(e.Name, @"^(?:bf1942|BfVietnam)[\\/]levels[\\/]([^\\/]+)[\\/]", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>The archive's own "&lt;game&gt;/levels/&lt;name&gt;/" prefix, whichever game wrote it.</summary>
    private static string SourcePrefix(RefractorFlatArchive archive, string level)
    {
        foreach (var e in archive.Entries)
        {
            string n = e.Name.Replace('\\', '/');
            var m = Regex.Match(n, $@"^(bf1942|BfVietnam)/levels/{Regex.Escape(level)}/", RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
        }
        return $"bf1942/levels/{level}/";
    }

    private static string Relative(string entryName, string sourcePrefix)
    {
        string normalised = entryName.Replace('\\', '/');
        return normalised.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? normalised[sourcePrefix.Length..]
            : "";
    }

    private static bool ShouldDrop(string relative)
    {
        string file = Path.GetFileName(relative);

        if (relative.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return true;
        if (relative.EndsWith(".rcm", StringComparison.OrdinalIgnoreCase)) return true;   // BFV uses env_default
        if (relative.StartsWith("ObjectLightmaps/", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (string n in DropFileNames)
            if (file.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>Case differences are cosmetic to the engine, but matching retail keeps things diffable.</summary>
    private static string RenameForBfv(string relative) => relative switch
    {
        _ when relative.Equals("materialmap.raw", StringComparison.OrdinalIgnoreCase) => "MaterialMap.raw",
        _ when relative.Equals("Menu/thumbnail.dds", StringComparison.OrdinalIgnoreCase) => "Menu/Thumbnail.dds",
        _ when relative.Equals("Textures/ingamemap.dds", StringComparison.OrdinalIgnoreCase) => "Textures/InGameMap.dds",
        _ => relative,
    };

    // --- script surgery ----------------------------------------------------------------------------------------

    /// <summary>
    /// Scripts name their own level by absolute archive path - the terrain script points at its heightmap,
    /// material map and texture base that way, and a <c>.wst</c> names its growth map. Every one of those has
    /// to follow the level to its new game and new name.
    /// </summary>
    private static byte[] RepathScript(byte[] data, string sourceLevel, string targetLevel)
    {
        string text = Encoding.Latin1.GetString(data);

        // Both slash styles appear in the wild, sometimes in the same file.
        text = Regex.Replace(text, $@"bf1942([\\/])levels\1{Regex.Escape(sourceLevel)}\b",
            m => $"BfVietnam{m.Groups[1].Value}levels{m.Groups[1].Value}{targetLevel}", RegexOptions.IgnoreCase);

        // A level that already says BfVietnam but under the old name (a re-port) still needs the rename.
        text = Regex.Replace(text, $@"BfVietnam([\\/])levels\1{Regex.Escape(sourceLevel)}\b",
            m => $"BfVietnam{m.Groups[1].Value}levels{m.Groups[1].Value}{targetLevel}", RegexOptions.IgnoreCase);

        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>Per-file additions BFV expects and a BF1942 level does not carry.</summary>
    private static byte[] ApplyScriptSpecials(
        string relative, byte[] data, IReadOnlyList<ConChange> fileChanges, PortOptions options, List<ConChange> log)
    {
        string file = relative.Replace('\\', '/');
        string text = Encoding.Latin1.GetString(data);

        if (file.Equals("Init/Terrain.con", StringComparison.OrdinalIgnoreCase))
        {
            // 11/11 retail levels set exactly this, and BF1942 levels never do.
            if (!Regex.IsMatch(text, @"GeometryTemplate\.waveHeight", RegexOptions.IgnoreCase))
            {
                text = AppendLine(text, "GeometryTemplate.waveHeight 1.0");
                log.Add(new ConChange(relative, 0, CommandVerdict.Rename, "(absent)",
                    "GeometryTemplate.waveHeight 1.0", "every retail BFV level sets it; BF1942 levels never do"));
            }
        }

        if (file.Equals("Init.con", StringComparison.OrdinalIgnoreCase))
        {
            // The growth pair is not optional - all 83 retail levels run both, and the porter generates them.
            foreach (string run in new[] { "run growth/overGrowth", "run growth/underGrowth" })
            {
                if (Regex.IsMatch(text, Regex.Escape(run).Replace("/", @"[\\/]"), RegexOptions.IgnoreCase)) continue;
                text = AppendLine(text, run);
                log.Add(new ConChange(relative, 0, CommandVerdict.Rename, "(absent)", run,
                    "BFV requires the growth pair; empty maps and palettes were generated for this level"));
            }

            // A flattened template file does not load; the chained Objects/ tree does, and Init.con is what
            // starts the chain.
            bool hasEmbeddedObjects = options.AdditionalFiles.Any(f =>
                f.Name.EndsWith("Objects/Objects.con", StringComparison.OrdinalIgnoreCase));
            if (hasEmbeddedObjects && !Regex.IsMatch(text, @"run\s+objects[\/]Objects", RegexOptions.IgnoreCase))
            {
                text = AppendLine(text, "run objects/Objects");
                log.Add(new ConChange(relative, 0, CommandVerdict.Rename, "(absent)", "run objects/Objects",
                    "loads the objects embedded into this level"));
            }

            if (options.CompensateLighting)
                text = CompensateDiffuse(relative, text, fileChanges, log);
        }

        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>
    /// BF1942 added <c>renderer.globalAmbientColor</c> ON TOP of the diffuse term. BFV has no such command, so
    /// a level that inherits BF1942's tuned-down diffuse renders at a fraction of normal light - the single
    /// commonest complaint about a ported map. All 83 retail BFV levels sit at 0.85-1.0.
    ///
    /// This is applied only when the ambient command was actually dropped from THIS file, so it is repairing
    /// light the converter itself removed rather than overruling the artist.
    /// </summary>
    private static string CompensateDiffuse(string file, string text, IReadOnlyList<ConChange> fileChanges, List<ConChange> log)
    {
        bool ambientDropped = fileChanges.Any(c =>
            c.Before.Contains("globalAmbientColor", StringComparison.OrdinalIgnoreCase));
        if (!ambientDropped) return text;

        var m = Regex.Match(text, @"^(?<indent>[ \t]*)(?<cmd>renderer\.diffuseColor)(?<args>[^\r\n]*)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!m.Success) return text;

        var numbers = Regex.Matches(m.Groups["args"].Value, @"[0-9]*\.?[0-9]+")
            .Select(x => double.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (numbers.Count == 0 || numbers.Max() >= 0.85) return text;

        string replacement = $"{m.Groups["indent"].Value}renderer.diffuseColor .9/.9/.9";
        log.Add(new ConChange(file, 0, CommandVerdict.ValueFix, m.Value.Trim(), replacement.Trim(),
            $"renderer.globalAmbientColor was dropped (BFV has no such command), so this level would render at " +
            $"a fraction of its light; all 83 retail BFV levels sit at 0.85-1.0"));

        return text[..m.Index] + replacement + text[(m.Index + m.Length)..];
    }

    private static string AppendLine(string text, string line)
    {
        string eol = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        if (text.Length > 0 && !text.EndsWith('\n') && !text.EndsWith('\r')) text += eol;
        return text + line + eol;
    }

    // --- statics ---------------------------------------------------------------------------------------------

    /// <summary>Every template name the level's StaticObjects.con places, with how many times.</summary>
    public static IReadOnlyDictionary<string, int> PlacedTemplates(RefractorFlatArchive archive)
    {
        var placed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in archive.Entries)
        {
            if (!Path.GetFileName(e.Name).Equals("StaticObjects.con", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var cmd in ConDialect.Commands(e.Name, archive.Read(e)))
            {
                if (!cmd.Command.Equals("Object.create", StringComparison.OrdinalIgnoreCase)) continue;
                var args = ConDialect.Args(cmd.Rest);
                if (args.Length >= 1) placed[args[0]] = placed.GetValueOrDefault(args[0]) + 1;
            }
        }
        return placed;
    }

    /// <summary>
    /// Mesh files the level's own scripts name but the level does not ship - the skybox above all. Only
    /// mesh-backed geometry types count: patchTerrain names the heightmap, which is not a .sm.
    /// </summary>
    public static IReadOnlySet<string> UnshippedMeshReferences(RefractorFlatArchive archive)
    {
        var meshBacked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "StandardMesh", "AnimatedMesh", "TreeMesh", "SkeletonCollisionMesh" };

        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
            if (e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase))
                shipped.Add(Path.GetFileNameWithoutExtension(e.Name));

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            if (!e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;

            string? type = null;
            foreach (var cmd in ConDialect.Commands(e.Name, archive.Read(e)))
            {
                var args = ConDialect.Args(cmd.Rest);
                if (cmd.Command.Equals("GeometryTemplate.create", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
                    type = args[0];
                else if (cmd.Command.Equals("GeometryTemplate.file", StringComparison.OrdinalIgnoreCase)
                         && args.Length >= 1 && type is not null && meshBacked.Contains(type))
                {
                    string bare = Path.GetFileNameWithoutExtension(args[0].Replace('\\', '/'));
                    if (bare.Length > 0 && !shipped.Contains(bare)) wanted.Add(bare);
                }
            }
        }
        return wanted;
    }

    /// <summary>
    /// Terrain tiles the level's own grid implies but does not ship.
    ///
    /// BFV ASSERTS on a terrain patch texture it cannot load (GeomPatchTerrain/Patch.cpp) where BF1942 simply
    /// tolerates the gap - which is why BF1942's Berlin, shipping only the last 2x2 of an 8x8 grid, loads in
    /// its own game and takes Battlefield Vietnam down on load. The expected grid is derived from the highest
    /// indices actually present rather than from a guessed formula.
    /// </summary>
    public static IReadOnlyList<string> MissingTerrainTiles(RefractorFlatArchive archive)
    {
        var present = new HashSet<(int X, int Y)>();
        int maxX = -1, maxY = -1;

        foreach (var e in archive.Entries)
        {
            var m = Regex.Match(Path.GetFileName(e.Name), @"^tx(\d\d)x(\d\d)\.dds$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int x = int.Parse(m.Groups[1].Value), y = int.Parse(m.Groups[2].Value);
            present.Add((x, y));
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var missing = new List<string>();
        for (int x = 0; x <= maxX; x++)
            for (int y = 0; y <= maxY; y++)
                if (!present.Contains((x, y)))
                    missing.Add($"tx{x:00}x{y:00}.dds");
        return missing;
    }

    /// <summary>
    /// Every template name the level NAMES, from anywhere - not just the placements.
    ///
    /// A level's vehicles are never "placed": the game-mode files declare spawner templates that name them
    /// through <c>setObjectTemplate</c>, and the engine constructs them when the mode starts. An embedder that
    /// only walks StaticObjects.con ships the scenery and none of the vehicles, which the engine reports as
    /// <c>WorldObjTemplBF: Cannt find "Willy"</c> - eleven of those on one map.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ReferencedTemplates(RefractorFlatArchive archive)
    {
        string[] naming =
        {
            "Object.create", "ObjectTemplate.setObjectTemplate", "ObjectTemplate.addTemplate",
            "ObjectTemplate.projectileTemplate",
        };

        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            if (!e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var cmd in ConDialect.Commands(e.Name, archive.Read(e)))
            {
                if (!naming.Any(n => cmd.Command.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                var args = ConDialect.Args(cmd.Rest);
                if (args.Length == 0) continue;

                // setObjectTemplate is "<slot> <Name>"; the others name it last too.
                // Some of these commands end in a value, not a name: a slot index, or a vector like
                // "0/0.6/2.11". Neither is a template.
                string name = args[^1];
                if (name.Length > 0 && !name.Contains('/') && !double.TryParse(name, out _))
                    wanted[name] = wanted.GetValueOrDefault(name) + 1;
            }
        }
        return wanted;
    }

    /// <summary>The level's own resolvable templates - what needs no embedding.</summary>
    public static IReadOnlySet<string> LocallyResolvable(RefractorFlatArchive archive) =>
        ResolveLocalTemplates(archive).Resolvable;

    /// <summary>
    /// The templates a level declares for itself AND can actually draw - name resolution alone is not enough.
    ///
    /// <c>ObjectTemplate.geometry X</c> resolves against GeometryTemplate NAMES, not files, and a
    /// GeometryTemplate whose <c>.file</c> is not shipped still parses perfectly. Either way the level loads
    /// clean and every name-only audit passes, because the null geometry is dereferenced only when the object
    /// is CONSTRUCTED - which surfaces as a mode-specific crash with no bad-file symptom anywhere. That bug
    /// cost several test rounds on the hand port, so both directions are resolved here.
    /// </summary>
    private static (HashSet<string> Resolvable, List<string> BrokenGeometry) ResolveLocalTemplates(
        RefractorFlatArchive archive)
    {
        var templateGeometry = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var geometryFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shippedMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in archive.Entries)
            if (e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase))
                shippedMeshes.Add(Path.GetFileNameWithoutExtension(e.Name));

        foreach (var e in archive.Entries)
        {
            if (!e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Name.Contains("StaticObjects", StringComparison.OrdinalIgnoreCase)) continue;

            string? currentTemplate = null, currentGeometry = null;

            foreach (var cmd in ConDialect.Commands(e.Name, archive.Read(e)))
            {
                var args = ConDialect.Args(cmd.Rest);

                if (cmd.Command.Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
                {
                    currentTemplate = args[1];
                    templateGeometry.TryAdd(currentTemplate, null);
                }
                else if (cmd.Command.Equals("ObjectTemplate.geometry", StringComparison.OrdinalIgnoreCase)
                         && args.Length >= 1 && currentTemplate is not null)
                {
                    templateGeometry[currentTemplate] = args[0];
                }
                else if (cmd.Command.Equals("GeometryTemplate.create", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
                {
                    currentGeometry = args[1];
                }
                else if (cmd.Command.Equals("GeometryTemplate.file", StringComparison.OrdinalIgnoreCase)
                         && args.Length >= 1 && currentGeometry is not null)
                {
                    // Paths take several shapes: a bare name, ../standardMesh/<name>, or a folder-qualified one.
                    geometryFile[currentGeometry] = Path.GetFileNameWithoutExtension(args[0].Replace('\\', '/'));
                }
            }
        }

        var resolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var broken = new List<string>();

        foreach (var (template, geometry) in templateGeometry)
        {
            // A template with no geometry of its own is a logical one (a spawner, a control point) and draws
            // nothing, so there is nothing to fail to ship.
            if (geometry is null) { resolvable.Add(template); continue; }

            if (!geometryFile.TryGetValue(geometry, out string? meshName))
            {
                broken.Add($"{template}: geometry '{geometry}' is never declared");
                continue;
            }
            if (!shippedMeshes.Contains(meshName))
            {
                broken.Add($"{template}: geometry '{geometry}' names mesh '{meshName}', which this level does not ship");
                continue;
            }
            resolvable.Add(template);
        }

        return (resolvable, broken);
    }

    /// <summary>
    /// Keep only placements whose template will exist in the ported level. A placement naming a template
    /// nothing defines does not fail loudly - it is the shape of fault that surfaces later as a crash when
    /// something finally tries to construct it.
    /// </summary>
    private static (byte[] Filtered, StaticsReport Report) FilterStatics(
        byte[] data, IReadOnlySet<string> resolvable, PortOptions options)
    {
        var lines = ConText.Split(data);
        var kept = new List<ConLine>(lines.Count);
        var unresolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int total = 0, keptCount = 0;
        bool droppingBlock = false;

        foreach (var line in lines)
        {
            string trimmed = line.Content.TrimStart();

            if (trimmed.StartsWith("Object.create", StringComparison.OrdinalIgnoreCase))
            {
                total++;
                var args = ConDialect.Args(trimmed["Object.create".Length..]);
                string template = args.Length > 0 ? args[0] : "";

                bool ok = template.Length > 0 &&
                          (resolvable.Contains(template) || options.Census?.DefinesTemplate(template) == true);

                droppingBlock = !ok && options.DropUnresolvedStatics;
                if (ok) keptCount++;
                else unresolved[template] = unresolved.GetValueOrDefault(template) + 1;

                if (!droppingBlock) kept.Add(line);
                continue;
            }

            // Everything up to the next create belongs to the placement just seen.
            if (!droppingBlock) kept.Add(line);
        }

        var report = new StaticsReport(total, keptCount, total - keptCount,
            unresolved.OrderByDescending(k => k.Value).Select(k => (k.Key, k.Value)).ToList());

        return (ConText.JoinBytes(kept), report);
    }

    /// <summary>
    /// Fold embedded objects in WITHOUT letting them overwrite the level's own files.
    ///
    /// A level that already ships an <c>Objects/</c> or <c>texture/</c> tree of its own - DC_Al_Nas does both -
    /// shares a namespace with whatever is embedded beside it, and the engine is case-insensitive, so
    /// <c>Texture/x.dds</c> and <c>texture/x.dds</c> are one entry. Letting the embedder win there would be a
    /// silent content swap: nothing is missing, nothing dangles, every audit passes, and the level draws the
    /// wrong texture. The level's own copy is the authoritative one, so it wins and the embedded one is dropped.
    ///
    /// The one file that must MERGE rather than lose is <c>Objects/Objects.con</c>: it is the chain that loads
    /// the objects, and keeping only one side would strand every object the other side lists.
    /// </summary>
    private static void MergeAdditionalFiles(List<PortedFile> files, IReadOnlyList<PortedFile> additional, string targetLevel)
    {
        string indexName = $"{TargetPrefix}{targetLevel}/Objects/Objects.con";
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++) byName[files[i].Name] = i;

        foreach (var extra in additional)
        {
            if (!byName.TryGetValue(extra.Name, out int existing))
            {
                byName[extra.Name] = files.Count;
                files.Add(extra);
                continue;
            }

            if (!extra.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase)) continue;   // the level's own wins

            var merged = MergeRunLines(files[existing].Data, extra.Data);
            files[existing] = files[existing] with
            {
                Data = merged,
                Origin = PortOrigin.Generated,
                Note = "the level's own object index, with the embedded objects appended",
            };
        }
    }

    /// <summary>Union of two run-chains, the existing file's order first, no line repeated.</summary>
    private static byte[] MergeRunLines(byte[] existing, byte[] added)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<ConLine>();

        foreach (byte[] source in new[] { existing, added })
            foreach (var line in ConText.Split(source))
            {
                string key = line.Content.Trim();
                if (key.Length > 0 && !seen.Add(key)) continue;
                output.Add(line.Terminator.Length == 0 ? line with { Terminator = ConLines.NewLine } : line);
            }

        return ConText.JoinBytes(output);
    }

    // --- generated ---------------------------------------------------------------------------------------------

    private static void AddGeneratedFiles(List<PortedFile> files, string targetLevel)
    {
        string root = TargetPrefix + targetLevel + "/";
        var present = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        void Add(string name, byte[] data, string note)
        {
            if (present.Contains(root + name)) return;
            files.Add(new PortedFile(root + name, data, PortOrigin.Generated, note));
        }

        Add("growth/overGrowth.wst", Encoding.Latin1.GetBytes(GrowthStubs.OverGrowthWst(targetLevel)),
            "BFV requires the growth pair; BF1942 has no equivalent");
        Add("growth/overGrowthMap.raw", GrowthStubs.OverGrowthMap(), "empty 256x256 index map");
        Add("growth/underGrowth.wst", Encoding.Latin1.GetBytes(GrowthStubs.UnderGrowthWst(targetLevel)),
            "BFV requires the growth pair; BF1942 has no equivalent");
        Add("growth/underGrowthMap.raw", GrowthStubs.UnderGrowthMap(), "empty 1024x1024 index map");

        // Retail levels all ship one; its contents are not read as anything meaningful.
        Add("loadcounter.dat", Encoding.Latin1.GetBytes("0\r\n"), "retail levels all ship one");
    }
}
