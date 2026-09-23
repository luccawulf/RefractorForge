using System.Text;
using RefractorBridge.Con;
using RefractorBridge.Mesh;
using RefractorBridge.Oracle;
using RefractorForge.Formats.Rfa;

namespace RefractorBridge.Level;

public sealed record EmbedResult(
    IReadOnlyList<PortedFile> Files,
    IReadOnlyList<string> EmbeddedTemplates,
    IReadOnlyList<string> ConvertedTreeMeshes,
    IReadOnlyList<string> SkippedStockNames,
    IReadOnlyList<string> Missing,
    int MeshCount,
    int TextureCount);

public sealed record EmbedOptions
{
    public StockCensus? Census { get; init; }
    public ConDialectOptions Dialect { get; init; } = new();

    /// <summary>Templates never to pull in, even when something references them - the bound on closure.</summary>
    public IReadOnlySet<string> Exclude { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Convert TreeMesh geometry to StandardMesh rather than dropping the object.</summary>
    public bool ConvertTreeMeshes { get; init; } = true;

    /// <summary>
    /// Cap embedded textures at this many pixels a side by dropping top mip levels. Not cosmetic: an install
    /// with an upscaled texture pack hands out 4096-square props, and Berlin's 101 textures came to 692 MB
    /// before capping. Zero disables it.
    /// </summary>
    public int MaxTextureSide { get; init; } = Mesh.DdsCap.DefaultMaxSide;

    /// <summary>
    /// Meshes to ship that no embedded object asks for - the ones the LEVEL's own scripts name. The skybox is
    /// the one that always matters: Init/SkyAndSun.con declares it, no placement mentions it, and the retail
    /// executable has no missing-mesh guard, so a level referencing a .sm that never shipped can take the
    /// client down on load with nothing in any log.
    /// </summary>
    public IReadOnlySet<string> ExtraMeshes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Lifts the objects a level places out of a mod's shared archives and embeds them in the level itself.
///
/// This is the difference between a ported level that shows terrain and one that shows the map. A BF1942 level
/// places objects by name and the engine finds them in the mod's shared library; a ported level has no such
/// library, so everything it places has to come with it.
///
/// Three things here are not obvious, and each one cost the hand port real time:
/// <list type="bullet">
/// <item><b>The layout matters.</b> A flattened file of all templates does NOT load. What works is the shape
/// the game's own object archives use - one folder per object, <c>Objects/&lt;Name&gt;/{&lt;Name&gt;.con,
/// objects.con, geometries.con}</c>, with an <c>Objects/Objects.con</c> chaining them and Init.con running
/// that. Proven in game.</item>
/// <item><b>Never redefine a name the target game owns.</b> A level's scripts load AFTER stock, so defining a
/// name BFV already uses does not make a private copy - it REPLACES stock's version for the whole game. Those
/// names are skipped and the placement left to resolve against stock.</item>
/// <item><b>Closure has to be bounded.</b> Following every reference transitively is correct in principle and
/// ruinous in practice: on the hand port it dragged in an AC-130 and 27 MB of content the map never spawns.</item>
/// </list>
/// </summary>
public static class ObjectEmbedder
{
    public static EmbedResult Embed(
        IEnumerable<string> wantedTemplates,
        AssetPool pool,
        string targetLevel,
        EmbedOptions? options = null)
    {
        options ??= new EmbedOptions();
        string root = $"BfVietnam/levels/{targetLevel}/";

        // The census has to reach the embedded scripts too. It did not, and the engine said so out loud:
        // "Don't use 'set' on properties any longer, instead use: ObjectTemplate.hasCollisionPhysics", 59 times.
        options = options with
        {
            Dialect = options.Dialect with { Census = options.Dialect.Census ?? options.Census },
        };

        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // folder -> object name
        var embedded = new List<string>();
        var skippedStock = new List<string>();
        var missing = new List<string>();
        var seenTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Work out which object FOLDERS are needed, following template references transitively.
        var queue = new Queue<string>(wantedTemplates.Distinct(StringComparer.OrdinalIgnoreCase));
        while (queue.Count > 0)
        {
            string template = queue.Dequeue();
            if (!seenTemplates.Add(template)) continue;
            if (options.Exclude.Contains(template)) continue;

            // Let stock's own definition stand rather than replacing it game-wide.
            if (options.Census?.DefinesTemplate(template) == true) { skippedStock.Add(template); continue; }

            string? folder = pool.FolderFor(template);
            if (folder is null) { missing.Add(template); continue; }

            if (folders.ContainsKey(folder)) continue;
            folders[folder] = Path.GetFileName(folder);

            foreach (string referenced in ReferencedTemplates(pool, folder))
                if (!seenTemplates.Contains(referenced)) queue.Enqueue(referenced);
        }

        // 2. Emit each folder, collecting the geometry it needs as we go.
        var files = new List<PortedFile>();
        // The same object folder can be indexed from several archives (a mod's Objects.rfa and its _001 patch),
        // and both would land on the same destination. First wins - archives are in priority order.
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Emit(PortedFile f) { if (emitted.Add(f.Name)) files.Add(f); }

        var neededMeshes = new HashSet<string>(options.ExtraMeshes, StringComparer.OrdinalIgnoreCase);
        // A geometry may be declared by several object folders - Ammobox_m1 by three, "soldier" by forty.
        // In one level that is a duplicate create, and BFV rejects it AND leaves no active template, so every
        // following line in that file fails too ("GeometryTemplate does not return an object", 102 times).
        var declaredGeometries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The same trap for OBJECT templates: several soldier folders declare GerSoldierComplexHead1, and BFV
        // reports "already created ... Deactivates active template!" - after which every following line in
        // that file has no template to act on (117 errors on one map).
        var declaredObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var neededSkins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var neededSkeletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objectNames = new List<string>();
        var convertedTrees = new List<string>();

        foreach (var (folder, rawName) in folders.OrderBy(f => f.Value, StringComparer.OrdinalIgnoreCase))
        {
            string objectName = Unique(rawName, objectNames);
            objectNames.Add(objectName);

            var scripts = pool.ScriptsIn(folder);
            bool wroteGeometries = false, wroteObjects = false;

            foreach (var script in scripts)
            {
                string file = Path.GetFileName(script.Name).ToLowerInvariant();

                // The AI subfolder is carried but is not part of the two-file chain.
                bool isGeometries = file == "geometries.con";
                bool isObjects = file == "objects.con" && !script.Name.Contains("/AI/", StringComparison.OrdinalIgnoreCase);

                // EVERY script, not the two obvious ones. Selecting this work by filename is a documented
                // way to lose: Weapons.con and Physics.con declare object templates, parts.con carries
                // geometry, and a geometry path left unrewritten resolves to nothing - which parses fine and
                // null-derefs only when the object is constructed.
                byte[] body = script.Read();
                body = RetargetGeometry(body, pool, neededMeshes, neededSkins, convertedTrees, options);
                HarvestSkeletons(body, neededSkeletons);
                body = DropAlreadyDeclared(body, "GeometryTemplate", 1, declaredGeometries);
                body = DropAlreadyDeclared(body, "ObjectTemplate", 1, declaredObjects);

                var rewritten = ConDialect.Rewrite(body, script.Name, options.Dialect);
                string destination = isGeometries ? "geometries.con"
                    : isObjects ? "objects.con"
                    : RelativeInsideFolder(script.Name, folder);

                Emit(new PortedFile($"{root}Objects/{objectName}/{destination}", rewritten.Bytes,
                    PortOrigin.Rewritten));

                wroteGeometries |= isGeometries;
                wroteObjects |= isObjects;

                if (!isGeometries && !isObjects) continue;
                foreach (string t in TemplatesDefinedIn(script.Name, body)) embedded.Add(t);
            }

            // <Name>.con is the entry point the chain runs.
            var chain = new StringBuilder();
            if (wroteGeometries) chain.Append("run geometries\r\n");
            if (wroteObjects) chain.Append("run objects\r\n");
            Emit(new PortedFile($"{root}Objects/{objectName}/{objectName}.con",
                Encoding.Latin1.GetBytes(chain.ToString()), PortOrigin.Generated,
                "the per-object entry point the engine's own archives use"));
        }

        // 3. Objects/Objects.con chains every object. A flattened template file does not load; this shape does.
        var index = new StringBuilder();
        foreach (string n in objectNames) index.Append($"run {n}/{n}\r\n");
        Emit(new PortedFile($"{root}Objects/Objects.con", Encoding.Latin1.GetBytes(index.ToString()),
            PortOrigin.Generated, $"chains {objectNames.Count} embedded object(s); Init.con must run it"));

        // 4. The meshes those geometries name, their materials, and the textures those name.
        var (assetFiles, textureCount) = EmbedGeometryAssets(neededMeshes, pool, root, convertedTrees, options, missing);
        foreach (var f in assetFiles) Emit(f);

        // Animation assets stay INSIDE the level folder, like everything else in a level archive.
        //
        // A level .rfa is scoped to its own path: the engine asserts "Error loading file list" with
        // "extractFilePath animations/  m_archivePath BfVietnam/Levels/<Level>/" the moment an entry sits
        // outside it, and then the ARCHIVE DOES NOT LOAD AT ALL - the game will not start. An archive-root
        // copy is therefore impossible here, whatever the path in createSkeleton looks like.
        foreach (string ske in neededSkeletons)
        {
            if (pool.Skeleton(ske) is { } k)
                Emit(new PortedFile($"{root}animations/{ske}.ske", k.Read(), PortOrigin.Copied));
            else missing.Add($"{ske}.ske (skeleton not found)");
        }

        foreach (string skin in neededSkins)
        {
            if (pool.Skin(skin) is { } s)
                Emit(new PortedFile($"{root}animations/{skin}.skn", s.Read(), PortOrigin.Copied));
            else missing.Add($"{skin}.skn (skin not found)");

            if (pool.Skeleton(skin) is { } k)
                Emit(new PortedFile($"{root}animations/{skin}.ske", k.Read(), PortOrigin.Copied));
        }

        return new EmbedResult(files, embedded, convertedTrees, skippedStock, missing,
            neededMeshes.Count, textureCount);
    }

    // --- closure -----------------------------------------------------------------------------------------------

    /// <summary>Templates a folder's scripts name - children, projectiles, spawned things.</summary>
    private static IEnumerable<string> ReferencedTemplates(AssetPool pool, string folder)
    {
        string[] referencing =
        {
            "ObjectTemplate.addTemplate", "ObjectTemplate.setObjectTemplate", "ObjectTemplate.projectileTemplate",
        };

        foreach (var script in pool.ScriptsIn(folder))
            foreach (var cmd in ConDialect.Commands(script.Name, script.Read()))
            {
                if (!referencing.Any(r => cmd.Command.Equals(r, StringComparison.OrdinalIgnoreCase))) continue;
                var args = ConDialect.Args(cmd.Rest);
                if (args.Length == 0) continue;

                // The last argument is not always a name: setPosition-style commands end in a vector, and a
                // slot-indexed one ends in a number. Neither is a template to chase.
                string referenced = args[^1];
                if (referenced.Length > 0 && !referenced.Contains('/') && !double.TryParse(referenced, out _))
                    yield return referenced;
            }
    }

    private static IEnumerable<string> TemplatesDefinedIn(string name, byte[] body)
    {
        foreach (var cmd in ConDialect.Commands(name, body))
        {
            if (!cmd.Command.Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) continue;
            var args = ConDialect.Args(cmd.Rest);
            if (args.Length >= 2) yield return args[1];
        }
    }

    // --- geometry ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Rewrite a Geometries.con for a level-local object: every <c>GeometryTemplate.file</c> becomes the stock
    /// <c>../standardMesh/&lt;basename&gt;</c> form, and a TreeMesh becomes a StandardMesh.
    ///
    /// Source mods do not all write bare names - Interstate qualifies some paths by the owning object folder,
    /// and BF1942 objects sometimes use no prefix at all. Both forms fail to resolve in a flat level, and the
    /// failure is nasty: the create still parses, so the level loads clean and every reference audit passes,
    /// and the null geometry is only dereferenced when the object is CONSTRUCTED.
    /// </summary>
    /// <summary>
    /// Remove <c>GeometryTemplate.create</c> blocks for a geometry another folder has already declared.
    ///
    /// The engine's own rule, and the porting notes': a shared geometry must be declared in exactly ONE place.
    /// The whole block goes, not just the create line - the properties that follow belong to it, and left
    /// behind they apply to whatever template happens to be active.
    /// </summary>
    private static byte[] DropAlreadyDeclared(byte[] body, string prefix, int nameArg, HashSet<string> alreadyDeclared)
    {
        var output = new List<ConLine>();
        string createCommand = prefix + ".create";
        bool skipping = false;

        foreach (var line in ConText.Split(body))
        {
            string trimmed = line.Content.TrimStart();

            if (IsCreate(trimmed, createCommand, out string name, nameArg))
            {
                skipping = name.Length > 0 && !alreadyDeclared.Add(name);
                if (skipping) continue;
            }
            else if (skipping && trimmed.Length > 0
                     && !trimmed.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
                     && !trimmed.StartsWith("rem", StringComparison.OrdinalIgnoreCase))
            {
                // The block ends at the first line belonging to something else - a NetworkableInfo, say,
                // which must survive even though the template around it is a duplicate.
                skipping = false;
            }

            if (!skipping) output.Add(line);
        }

        return ConText.JoinBytes(output);
    }

    /// <summary>
    /// Exactly <c>&lt;Prefix&gt;.create</c> - never <c>createSkeleton</c>, <c>createNewInfo</c> or any other
    /// command that merely starts with "create".
    /// </summary>
    private static bool IsCreate(string trimmed, string createCommand, out string name, int nameArg)
    {
        name = "";
        if (!trimmed.StartsWith(createCommand, StringComparison.OrdinalIgnoreCase)) return false;

        string rest = trimmed[createCommand.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) return false;      // createSkeleton, createNewInfo...

        var args = ConDialect.Args(rest);
        if (args.Length > nameArg) name = args[nameArg];
        return true;
    }

    /// <summary>
    /// Skeletons are named OUTRIGHT: <c>ObjectTemplate.createSkeleton animations/UsFace.ske</c>. Keying them
    /// off the mesh name misses every one of them.
    /// </summary>
    private static void HarvestSkeletons(byte[] body, HashSet<string> into)
    {
        foreach (var cmd in ConDialect.Commands("objects.con", body))
        {
            if (!cmd.Command.Equals("ObjectTemplate.createSkeleton", StringComparison.OrdinalIgnoreCase)) continue;
            var args = ConDialect.Args(cmd.Rest);
            if (args.Length >= 1)
                into.Add(Path.GetFileNameWithoutExtension(args[0].Trim('"').Replace('\\', '/')));
        }
    }

    private static byte[] RetargetGeometry(
        byte[] body, AssetPool pool, HashSet<string> neededMeshes, HashSet<string> neededSkins,
        List<string> convertedTrees, EmbedOptions options)
    {
        var lines = ConText.Split(body);
        var output = new List<ConLine>(lines.Count);
        string? currentGeometry = null;

        foreach (var line in lines)
        {
            string trimmed = line.Content.TrimStart();

            if (trimmed.StartsWith("GeometryTemplate.create", StringComparison.OrdinalIgnoreCase))
            {
                var args = ConDialect.Args(trimmed["GeometryTemplate.create".Length..]);
                if (args.Length >= 2)
                {
                    currentGeometry = args[1];
                    if (options.ConvertTreeMeshes && args[0].Equals("TreeMesh", StringComparison.OrdinalIgnoreCase))
                    {
                        string indent = line.Content[..(line.Content.Length - trimmed.Length)];
                        output.Add(line with { Content = $"{indent}GeometryTemplate.create StandardMesh {args[1]}" });
                        continue;
                    }
                }
            }
            else if (trimmed.StartsWith("GeometryTemplate.setSkin", StringComparison.OrdinalIgnoreCase))
            {
                // "setSkin animations/1PGerBody.skn" - the skin ships beside the mesh, or the soldier is bald.
                var args = ConDialect.Args(trimmed["GeometryTemplate.setSkin".Length..]);
                if (args.Length >= 1) neededSkins.Add(Path.GetFileNameWithoutExtension(args[0].Replace('\\', '/')));
            }
            else if (trimmed.StartsWith("GeometryTemplate.file", StringComparison.OrdinalIgnoreCase))
            {
                var args = ConDialect.Args(trimmed["GeometryTemplate.file".Length..]);
                if (args.Length >= 1)
                {
                    string bare = Path.GetFileNameWithoutExtension(args[0].Replace('\\', '/'));
                    neededMeshes.Add(bare);
                    if (currentGeometry is not null &&
                        pool.GeometryType(currentGeometry)?.Equals("TreeMesh", StringComparison.OrdinalIgnoreCase) == true)
                        convertedTrees.Add(bare);

                    string indent = line.Content[..(line.Content.Length - trimmed.Length)];
                    output.Add(line with { Content = $"{indent}GeometryTemplate.file ../standardMesh/{bare}" });
                    continue;
                }
            }

            output.Add(line);
        }

        return ConText.JoinBytes(output);
    }

    /// <summary>Ship the meshes, their materials and the textures those materials name.</summary>
    private static (List<PortedFile> Files, int TextureCount) EmbedGeometryAssets(
        HashSet<string> meshes, AssetPool pool, string root, List<string> convertedTrees,
        EmbedOptions options, List<string> missing)
    {
        var files = new List<PortedFile>();
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var treeSet = new HashSet<string>(convertedTrees, StringComparer.OrdinalIgnoreCase);

        foreach (string bare in meshes.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            byte[]? meshBytes = null;
            string? rsText = null;

            if (pool.Mesh(bare) is { } sm)
            {
                meshBytes = sm.Read();
                if (pool.Material(bare) is { } rs) rsText = Encoding.Latin1.GetString(rs.Read());
            }
            else if (options.ConvertTreeMeshes && pool.TreeMesh(bare) is { } tm)
            {
                byte[] raw = tm.Read();
                if (TmToSm.IsBillboardOnly(raw))
                {
                    missing.Add($"{bare} (.tm is billboard-only - BFV has no imposter shader; substitute or drop)");
                    continue;
                }
                var converted = TmToSm.Convert(raw, bare);
                meshBytes = converted.StandardMesh;
                rsText = converted.RsText;
                treeSet.Add(bare);
            }

            if (meshBytes is null) { missing.Add($"{bare} (no .sm or .tm in the search path)"); continue; }

            files.Add(new PortedFile($"{root}StandardMesh/{bare}.sm", meshBytes, PortOrigin.Copied));

            // An animated mesh looks for animations/<name>.ske beside itself; without it the engine reports
            // "BoneAnimation: Skeleton (.SKE) file not found" and the object does not animate.
            if (pool.Skeleton(bare) is { } ske)
                files.Add(new PortedFile($"{root}animations/{bare}.ske", ske.Read(), PortOrigin.Copied));
            if (rsText is null) continue;

            files.Add(new PortedFile($"{root}StandardMesh/{bare}.rs", Encoding.Latin1.GetBytes(rsText),
                treeSet.Contains(bare) ? PortOrigin.Generated : PortOrigin.Copied));

            foreach (string tex in TexturesNamedBy(rsText)) textures.Add(tex);
        }

        foreach (string tex in textures.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            if (pool.Texture(tex) is not { } entry) { missing.Add($"{tex} (texture not found)"); continue; }

            byte[] bytes = entry.Read();
            var origin = PortOrigin.Copied;
            if (options.MaxTextureSide > 0 && entry.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var capped = Mesh.DdsCap.Cap(bytes, options.MaxTextureSide);
                if (capped.Changed) { bytes = capped.Data; origin = PortOrigin.Rewritten; }
            }

            files.Add(new PortedFile($"{root}Texture/{tex}{Path.GetExtension(entry.Name)}", bytes, origin));
        }

        return (files, textures.Count);
    }

    /// <summary>Texture names a material script references. Both <c>texture</c> and <c>cubemap</c> lines count.</summary>
    private static IEnumerable<string> TexturesNamedBy(string rs)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(rs,
                     "(?:texture|normalmap|detailmap|cubemap)\\s+\"([^\"]+)\"",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            string raw = m.Groups[1].Value.Replace('\\', '/');
            string bare = Path.GetFileNameWithoutExtension(raw);
            if (bare.Length > 0) yield return bare;
        }
    }

    // --- naming ------------------------------------------------------------------------------------------------

    private static string RelativeInsideFolder(string entryName, string folder)
    {
        string normalised = entryName.Replace('\\', '/');
        int at = normalised.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
        string rel = at >= 0 ? normalised[(at + folder.Length)..].TrimStart('/') : Path.GetFileName(normalised);
        return rel.Length == 0 ? Path.GetFileName(normalised) : rel;
    }

    /// <summary>Two mods can both ship "crate1"; flattening them into one folder would silently swap content.</summary>
    private static string Unique(string name, List<string> taken)
    {
        if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
        for (int i = 2; ; i++)
        {
            string candidate = $"{name}_{i}";
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
    }
}
