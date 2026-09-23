using System.Text;
using RefractorBridge.Con;
using RefractorBridge.Oracle;

// RefractorBridge - BF1942 -> Battlefield Vietnam converter.
//
// Milestone M0+M1: the executables-as-oracle layer and the .con dialect engine. Everything later (the level
// porter, the object/vehicle porter, whole-mod conversion) imports this, because "which commands does BFV
// actually implement" is the question every one of those has to answer first.
//
// See docs/BF1942_to_BFV_Converter_Plan.md for the full roadmap.

const string DefaultBf42Exe = @"D:\Games\EA GAMES\Battlefield 1942\BF1942.exe";
const string DefaultBfvExe = @"D:\Games\EA GAMES\Battlefield Vietnam\BfVietnam.exe";

var argv = args.ToList();
string bf42Path = TakeOption(argv, "--bf42") ?? DefaultBf42Exe;
string bfvPath = TakeOption(argv, "--bfv") ?? DefaultBfvExe;
bool mapResponsePhysics = TakeFlag(argv, "--map-response-physics");
bool strict = TakeFlag(argv, "--strict");
bool summary = TakeFlag(argv, "--summary");
bool reverseWinding = TakeFlag(argv, "--reverse-winding");
string? censusFile = TakeOption(argv, "--census");
bool useCensus = TakeFlag(argv, "--use-census");
string? embedMod = TakeOption(argv, "--embed");
string? donorPath = TakeOption(argv, "--donor");
string? dropWhat = TakeOption(argv, "--drop");
string bf1942Root = TakeOption(argv, "--bf1942-root") ?? @"D:\Games\EA GAMES\Battlefield 1942";
string bfvModRoot = TakeOption(argv, "--bfv-mod")
    ?? @"D:\Games\EA GAMES\Battlefield Vietnam\Mods\BfVietnam\Archives";
float alphaTestRef = float.TryParse(TakeOption(argv, "--alpha-test-ref"), System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out float atr) ? atr : RefractorBridge.Mesh.TmToSm.DefaultAlphaTestRef;

string command = argv.Count > 0 ? argv[0].ToLowerInvariant() : "help";
var rest = argv.Skip(1).ToList();

try
{
    return command switch
    {
        "oracle" => rest.Count > 0 ? RunOracleQuery(rest) : RunOracle(),
        "condelta" => RunConDelta(rest),
        "conconvert" => RunConConvert(rest),
        "rfacheck" => RunRfaCheck(rest),
        "ls" => RunLs(rest),
        "cat" => RunCat(rest),
        "tmconvert" => RunTmConvert(rest),
        "tminfo" => RunTmInfo(rest),
        "census" => RunCensus(rest),
        "portlevel" => RunPortLevel(rest),
        "patch" => RunPatch(rest),
        "get" => RunGet(rest),
        _ => Usage(),
    };
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Usage()
{
    Console.WriteLine("""
        rbridge - BF1942 -> Battlefield Vietnam converter (M0+M1: oracles + .con dialect)

          rbridge oracle
              Load both executables' command tables and spot-check the documented delta.

          rbridge condelta <file|folder|archive.rfa> [more...] [--strict] [--summary]
              Classify every .con command used, against both executables.
              --strict  exits non-zero if anything BF1942-only survives.
              --summary one compact line per target, for censusing a whole mod corpus.

          rbridge conconvert <file|folder|archive.rfa> <outputFolder>
                             [--map-response-physics] [--use-census]
              Apply the high-confidence dialect rewrite and write the result out.
              --use-census also applies setX->X renames that retail BFV content proves.

          rbridge tmconvert <archive.rfa|file.tm|folder> <outputFolder>
                            [--alpha-test-ref 0.5] [--reverse-winding]
              BF1942 TreeMesh -> BFV StandardMesh (.sm + .rs). BFV registers no TreeMesh type,
              so every .tm in a mod is an object that cannot load. Each result is verified by
              re-parsing it before it is written.

          rbridge portlevel <bf1942 level.rfa> <out.rfa> [LevelName] [--embed <mod>]
              Port a BF1942 level to a BFV level archive. --embed lifts the objects the level
              places out of that mod's shared archives and ships them inside the level.

          rbridge census build [archives...]        census RETAIL BFV content per class
          rbridge census judge <Class> <setProperty> settle a setX-vs-X spelling
          rbridge census class <Class>              what retail sets on a class
          rbridge census name  <TemplateName>       does retail already own this name?

          rbridge ls  <archive.rfa> [substring]      list entries
          rbridge cat <archive.rfa> <substring>      print a text entry

          rbridge rfacheck <archive.rfa> [more...]
              Prove the repacker is byte-faithful: repacking with ZERO changes must reproduce
              the input exactly. Run this before trusting any archive the converter writes.

        Options:
          --bf42 <BF1942.exe>      default: D:\Games\EA GAMES\Battlefield 1942\BF1942.exe
          --bfv  <BfVietnam.exe>   default: D:\Games\EA GAMES\Battlefield Vietnam\BfVietnam.exe
        """);
    return 1;
}

(ExeSymbolTable Bfv, ExeSymbolTable Bf42) LoadOracles()
{
    foreach (string p in new[] { bf42Path, bfvPath })
        if (!File.Exists(p))
            throw new FileNotFoundException($"executable not found: {p} (point at it with --bf42 / --bfv)");

    return (ExeSymbolTable.Load(bfvPath), ExeSymbolTable.Load(bf42Path));
}

int RunOracle()
{
    var (bfv, bf42) = LoadOracles();
    Console.WriteLine($"BF1942.exe    {bf42.IdentifierCount,7} identifiers  {bf42.DottedCount,6} dotted   {bf42.Path}");
    Console.WriteLine($"BfVietnam.exe {bfv.IdentifierCount,7} identifiers  {bfv.DottedCount,6} dotted   {bfv.Path}");
    Console.WriteLine();
    Console.WriteLine("Spot-checks against the documented delta (property names, not dotted forms):");

    // Each of these is a claim the porting notes made; if the oracle disagrees, the oracle is wrong.
    (string Property, bool InBf42, bool InBfv, string What)[] checks =
    {
        ("setTextureParam",     true, false, "BF1942-only (BFV has no texture-param setter)"),
        ("globalAmbientColor",  true, false, "BF1942-only (the 'everything is too dark' command)"),
        ("texLayer1",           true, false, "BF1942-only (layered water)"),
        ("hasResponsePhysics",  true, false, "BF1942-only (62 uses in one ported level)"),
        ("setActiveCombatArea", true, true,  "portable unchanged"),
        ("setTorque",           true, true,  "the trap in the middle: BFV registers it, yet retail uses 'torque' per class"),
    };

    int bad = 0;
    foreach (var (prop, wantBf42, wantBfv, what) in checks)
    {
        bool in42 = bf42.HasProperty(prop), inBfv = bfv.HasProperty(prop);
        bool ok = in42 == wantBf42 && inBfv == wantBfv;
        if (!ok) bad++;
        Console.WriteLine($"  [{(ok ? "ok" : "??")}] {prop,-20} BF1942={YesNo(in42)} BFV={YesNo(inBfv)}   {what}");
    }

    Console.WriteLine();
    Console.WriteLine(bad == 0 ? "ORACLE CHECKS PASSED." : $"{bad} spot-check(s) disagreed with the documented delta.");
    return bad == 0 ? 0 : 2;

    static string YesNo(bool b) => b ? "yes" : "no ";
}

int RunGet(List<string> a)
{
    // Binary extraction - the primitive every bisect needs.
    if (a.Count < 3) { Console.Error.WriteLine("get <archive.rfa> <entrySubstring> <outFile>"); return 1; }
    var archive = new RefractorForge.Formats.Rfa.RefractorFlatArchive(a[0]);
    var hits = archive.Entries.Where(e => e.Name.Contains(a[1], StringComparison.OrdinalIgnoreCase)).ToList();
    if (hits.Count == 0) { Console.Error.WriteLine($"no entry matching '{a[1]}'"); return 1; }
    if (hits.Count > 1)
    {
        // A suffix lookup that is not unique silently grabs the wrong file - qualify it instead.
        Console.Error.WriteLine($"'{a[1]}' matches {hits.Count} entries; be more specific:");
        foreach (var h in hits.Take(5)) Console.Error.WriteLine($"  {h.Name}");
        return 1;
    }
    File.WriteAllBytes(a[2], archive.Read(hits[0]));
    Console.WriteLine($"{hits[0].Name} -> {a[2]} ({hits[0].UncompressedSize:N0} B)");
    return 0;
}

int RunPatch(List<string> a)
{
    // One entry, replaced in place. The point is bisection: change ONE thing, run it, learn something.
    if (a.Count < 3) { Console.Error.WriteLine("patch <archive.rfa> <entrySubstring> <newContentFile>"); return 1; }

    var archive = new RefractorForge.Formats.Rfa.RefractorFlatArchive(a[0]);
    var hit = archive.Entries.FirstOrDefault(e => e.Name.Contains(a[1], StringComparison.OrdinalIgnoreCase));
    if (hit is null) { Console.Error.WriteLine($"no entry matching '{a[1]}'"); return 1; }

    byte[] replacement = File.ReadAllBytes(a[2]);
    RefractorForge.Formats.Rfa.RefractorFlatArchive.RepackToFile(a[0], archive,
        new Dictionary<string, byte[]> { [hit.Name] = replacement });

    Console.WriteLine($"patched {hit.Name}: {hit.UncompressedSize:N0} -> {replacement.Length:N0} B");
    return 0;
}

int RunPortLevel(List<string> a)
{
    if (a.Count < 2) return Usage();
    string sourcePath = a[0], outPath = a[1];
    string targetName = a.Count > 2 ? a[2] : Path.GetFileNameWithoutExtension(outPath);

    StockCensus? census = null;
    string censusPath = censusFile ?? Path.Combine(AppContext.BaseDirectory, "bfv_stock_census.json");
    if (File.Exists(censusPath))
    {
        census = StockCensus.Load(censusPath);
        Console.WriteLine($"retail census: {census.Classes.Count} classes, {census.TemplateNames.Count:N0} template names");
    }
    else Console.WriteLine("no retail census found - statics cannot be resolved against stock BFV (run 'rbridge census build')");

    var source = new RefractorForge.Formats.Rfa.RefractorFlatArchive(sourcePath);

    // A BF1942 level is a base archive PLUS numbered patch archives, later ones overriding earlier.
    var patchPaths = RefractorBridge.Level.LevelPorter.PatchArchivePaths(sourcePath);
    var patches = patchPaths
        .Select(p => new RefractorForge.Formats.Rfa.RefractorFlatArchive(p))
        .ToList();
    if (patches.Count > 0)
        Console.WriteLine($"patch archives (applied in order): " +
                          string.Join(" -> ", patchPaths.Select(Path.GetFileNameWithoutExtension)));
    var dialect = new ConDialectOptions
    {
        MapResponsePhysics = mapResponsePhysics,
        Census = useCensus || censusFile is not null ? census : null,
        Bfv = File.Exists(bfvPath) ? ExeSymbolTable.Load(bfvPath) : null,
    };

    // BFV asserts on a terrain patch texture it cannot load; BF1942 tolerates the gap.
    var missingTiles = RefractorBridge.Level.LevelPorter.MissingTerrainTiles(source);
    if (missingTiles.Count > 0)
    {
        Console.WriteLine($"WARNING: the source ships an INCOMPLETE terrain tile grid - {missingTiles.Count} tile(s) missing");
        Console.WriteLine($"         e.g. {string.Join(", ", missingTiles.Take(6))}");
        Console.WriteLine("         BF1942 tolerates this; Battlefield Vietnam ASSERTS on load. This level will not load as-is.");
    }

    var embedded = Array.Empty<RefractorBridge.Level.PortedFile>() as IReadOnlyList<RefractorBridge.Level.PortedFile>;
    var extraTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (embedMod is not null)
    {
        // A BF1942 level places objects by name and lets the engine find them in the mod's shared library.
        // A ported level has no such library, so whatever it places has to come with it.
        // Mods inherit: DC_Final's levels place Desert Combat's objects. Comma-separate them, highest
        // priority first; the base game is always appended last.
        // The chain is DECLARED in the mod's init.con (game.addModPath, first = highest priority), so it is
        // read rather than guessed. A comma-separated list still overrides it if you need to.
        var mods = embedMod.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mods.Length == 1)
        {
            var chain = RefractorBridge.Level.AssetPool.ModChain(bf1942Root, mods[0]);
            Console.WriteLine($"mod inheritance (from {mods[0]}/init.con): {string.Join(" -> ", chain)}");
        }
        var pool = RefractorBridge.Level.AssetPool.Build(
            mods.SelectMany(m => RefractorBridge.Level.AssetPool.StandardSearchPaths(bf1942Root, m)).Distinct());
        Console.WriteLine($"asset pool ({embedMod}): {pool.TemplateCount:N0} templates, {pool.MeshCount:N0} meshes, " +
                          $"{pool.TreeMeshCount:N0} treemeshes, {pool.TextureCount:N0} textures, " +
                          $"{pool.SkeletonCount:N0} skeletons");

        var placed = RefractorBridge.Level.LevelPorter.ReferencedTemplates(source);
        var local = RefractorBridge.Level.LevelPorter.LocallyResolvable(source);
        var wanted = placed.Keys
            .Where(t => !local.Contains(t) && census?.DefinesTemplate(t) != true)
            .ToList();

        var extraMeshes = RefractorBridge.Level.LevelPorter.UnshippedMeshReferences(source);
        if (extraMeshes.Count > 0)
            Console.WriteLine($"  {extraMeshes.Count} mesh(es) the level's own scripts name but do not ship " +
                              $"(e.g. the skybox): {string.Join(", ", extraMeshes.Take(4))}");

        var er = RefractorBridge.Level.ObjectEmbedder.Embed(wanted, pool, targetName,
            new RefractorBridge.Level.EmbedOptions { Census = census, Dialect = dialect, ExtraMeshes = extraMeshes });

        embedded = er.Files;
        foreach (string t in er.EmbeddedTemplates) extraTemplates.Add(t);

        Console.WriteLine($"embedding: {wanted.Count} template(s) wanted -> {er.EmbeddedTemplates.Count} embedded, " +
                          $"{er.MeshCount} mesh(es), {er.TextureCount} texture(s)");
        if (er.ConvertedTreeMeshes.Count > 0)
            Console.WriteLine($"  {er.ConvertedTreeMeshes.Distinct().Count()} TreeMesh converted to StandardMesh");
        if (er.SkippedStockNames.Count > 0)
            Console.WriteLine($"  {er.SkippedStockNames.Count} name(s) left to retail BFV (redefining them would replace stock game-wide)");
        foreach (string m in er.Missing.Take(8)) Console.WriteLine($"  ! missing: {m}");
        if (er.Missing.Count > 8) Console.WriteLine($"  ... and {er.Missing.Count - 8} more missing");
    }

    var result = RefractorBridge.Level.LevelPorter.Port(source, targetName, new RefractorBridge.Level.PortOptions
    {
        Census = census,
        Dialect = dialect,
        AdditionalFiles = embedded,
        ExtraTemplates = extraTemplates,
        PatchArchives = patches,
    });

    Console.WriteLine($"{result.SourceLevel}  ->  {result.TargetLevel}");
    Console.WriteLine($"  {result.Files.Count(f => f.Origin == RefractorBridge.Level.PortOrigin.Copied),5} copied verbatim");
    Console.WriteLine($"  {result.Files.Count(f => f.Origin == RefractorBridge.Level.PortOrigin.Rewritten),5} scripts rewritten ({result.Changes.Count} change(s))");
    Console.WriteLine($"  {result.Files.Count(f => f.Origin == RefractorBridge.Level.PortOrigin.Generated),5} generated for BFV");
    foreach (var g in result.Files.Where(f => f.Origin == RefractorBridge.Level.PortOrigin.Generated))
        Console.WriteLine($"          + {g.Name.Split('/').Last(),-22} {g.Note}");
    Console.WriteLine($"  {result.DroppedEntries.Count,5} entries dropped (BFV never reads them)");

    var s = result.Statics;
    Console.WriteLine($"  statics: {s.Kept}/{s.Total} placements kept, {s.Dropped} dropped");
    foreach (var (template, count) in s.Unresolved.Take(12))
        Console.WriteLine($"          - {template,-34} x{count}  no such template in BFV or in this level");
    if (s.Unresolved.Count > 12) Console.WriteLine($"          ... and {s.Unresolved.Count - 12} more template(s)");

    if (result.BrokenGeometry.Count > 0)
    {
        // These parse fine and load fine, then null-deref when something finally constructs them.
        Console.WriteLine($"  {result.BrokenGeometry.Count} level-local template(s) could never draw:");
        foreach (string b in result.BrokenGeometry.Take(8)) Console.WriteLine($"          ! {b}");
        if (result.BrokenGeometry.Count > 8) Console.WriteLine($"          ... and {result.BrokenGeometry.Count - 8} more");
    }
    if (result.Reviews.Count > 0) Console.WriteLine($"  {result.Reviews.Count} line(s) need a human");

    // Build the archive by REPACKING THE SOURCE, never WriteFile.
    //
    // WriteFile is the new-archive API: it takes no donor, so it stamps its own 143-byte descriptor, zeroes
    // every 12-byte entry trailer, drops the 4-byte TOC tail and forces the compressed flag. An archive built
    // that way passes Validate(), decodes every entry, re-reads perfectly - and gives Battlefield Vietnam a
    // Runtime Error on load, because the XPack id is a CHECKSUM OVER THE DESCRIPTOR and a self-stamped
    // descriptor makes the header internally inconsistent. Repacking the level we are porting carries the
    // descriptor, the XPack id, the compressed flag and the TOC tail across.
    // A level .rfa is scoped to its own folder. One entry outside it and the engine asserts
    // "Error loading file list", the archive does not load AT ALL, and the game will not start.
    string requiredPrefix = $"BfVietnam/levels/{targetName}/";
    var strays = result.Files
        .Where(f => !f.Name.Replace('\\', '/').StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (strays.Count > 0)
    {
        Console.Error.WriteLine($"REFUSING TO WRITE: {strays.Count} entrie(s) sit outside {requiredPrefix}.");
        foreach (var f in strays.Take(6)) Console.Error.WriteLine($"  {f.Name}");
        Console.Error.WriteLine("  The engine rejects the whole archive for this, and the game will not start.");
        return 2;
    }

    var kept = dropWhat is null
        ? result.Files
        : result.Files.Where(f => !dropWhat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Any(d => f.Name.Contains(d, StringComparison.OrdinalIgnoreCase))).ToList();
    if (dropWhat is not null)
        Console.WriteLine($"--drop '{dropWhat}': {result.Files.Count - kept.Count} entrie(s) left out");

    var replacements = kept.ToDictionary(f => f.Name, f => f.Data, StringComparer.OrdinalIgnoreCase);

    // The donor supplies the container: descriptor, XPack id, compressed flag, TOC tail. It defaults to the
    // level being ported, but a RETAIL BATTLEFIELD VIETNAM level is the safer donor - the XPack id is a
    // checksum over the descriptor, so the header has to be one the target game accepts.
    var donor = source;
    if (donorPath is not null)
    {
        donor = new RefractorForge.Formats.Rfa.RefractorFlatArchive(donorPath);
        Console.WriteLine($"container donor: {Path.GetFileName(donorPath)} (XPackId {donor.XPackId}, compressed {donor.IsCompressed})");
    }
    else Console.WriteLine($"container donor: the source level (XPackId {source.XPackId}, compressed {source.IsCompressed})");

    RefractorForge.Formats.Rfa.RefractorFlatArchive.RepackToFile(outPath, donor, replacements, drop: _ => true);

    // Prove the archive we just wrote reads back the way we wrote it, before anyone tries it in the game.
    var written = new RefractorForge.Formats.Rfa.RefractorFlatArchive(outPath);
    int mismatched = 0;
    foreach (var f in kept)
    {
        var e = written.Entries.FirstOrDefault(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase));
        if (e is not null && written.Read(e).AsSpan().SequenceEqual(f.Data)) continue;
        mismatched++;
        if (mismatched <= 5)
            Console.WriteLine($"  MISMATCH {f.Name} ({(e is null ? "absent from the archive" : $"{e.UncompressedSize:N0} B written vs {f.Data.Length:N0} B expected")})");
    }

    // A case-only collision writes one entry and loses the other, which is how content silently swaps.
    var collisions = kept.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
    foreach (var c in collisions.Take(5))
        Console.WriteLine($"  COLLISION {c.Key} - {c.Count()} files want this name");

    Console.WriteLine();
    Console.WriteLine($"wrote {kept.Count} entries -> {Path.GetFullPath(outPath)} ({new FileInfo(outPath).Length:N0} B)");
    Console.WriteLine(mismatched == 0
        ? "  re-read check: every entry matches what was written."
        : $"  re-read check: {mismatched} entrie(s) DID NOT match - do not ship this archive.");
    return mismatched == 0 ? 0 : 2;
}

int RunCensus(List<string> a)
{
    string sub = a.Count > 0 ? a[0].ToLowerInvariant() : "";
    string censusPath = censusFile ?? Path.Combine(AppContext.BaseDirectory, "bfv_stock_census.json");

    if (sub == "build")
    {
        // Default to everything retail Battlefield Vietnam ships that carries .con: the object library, the
        // effects and AI trees, and every stock level.
        var sources = a.Skip(1).ToList();
        if (sources.Count == 0)
        {
            string root = bfvModRoot;
            foreach (string n in new[] { "objects.rfa", "effects.rfa", "ai.rfa" })
            {
                string p = Path.Combine(root, n);
                if (File.Exists(p)) sources.Add(p);
            }
            string levels = Path.Combine(root, "bfvietnam", "levels");
            if (Directory.Exists(levels)) sources.AddRange(Directory.EnumerateFiles(levels, "*.rfa"));
        }
        if (sources.Count == 0) { Console.Error.WriteLine($"no retail BFV archives found under {bfvModRoot}"); return 1; }

        var files = new List<ConFile>();
        foreach (string s in sources)
        {
            try { files.AddRange(ConSource.Load(s)); }
            catch (Exception ex) { Console.Error.WriteLine($"  skipped {Path.GetFileName(s)}: {ex.Message}"); }
        }

        var census = StockCensus.Build(files);
        census.Save(censusPath);

        Console.WriteLine($"censused {sources.Count} archive(s): {census.FilesScanned:N0} .con files, " +
                          $"{census.CommandsScanned:N0} commands");
        Console.WriteLine($"  {census.Classes.Count:N0} classes, {census.TemplateNames.Count:N0} template names, " +
                          $"{census.Ranges.Count:N0} value ranges");
        Console.WriteLine($"  written to {censusPath}");
        return 0;
    }

    if (!File.Exists(censusPath))
    {
        Console.Error.WriteLine($"no census at {censusPath} - run 'rbridge census build' first");
        return 1;
    }
    var loaded = StockCensus.Load(censusPath);

    if (sub == "judge" && a.Count >= 3)
    {
        var v = loaded.JudgeSetForm(a[1], a[2]);
        Console.WriteLine($"{a[1]}.{a[2]}");
        Console.WriteLine($"  retail uses '{a[2]}' {v.SetFormUses}x and '{v.ShortForm}' {v.ShortFormUses}x on {a[1]}");
        Console.WriteLine($"  -> {v.Decision}: {v.Reason}");
        return 0;
    }

    if (sub == "class" && a.Count >= 2)
    {
        if (!loaded.Classes.TryGetValue(a[1], out var props))
        {
            Console.WriteLine($"retail defines nothing on class '{a[1]}'");
            return 0;
        }
        Console.WriteLine($"{a[1]} - {props.Count} properties retail sets");
        foreach (var (prop, n) in props.OrderByDescending(p => p.Value).Take(60))
        {
            string range = loaded.Ranges.TryGetValue($"{a[1]}.{prop}", out var r)
                ? $"   [{r.Min:0.###} .. {r.Max:0.###}]" : "";
            Console.WriteLine($"  {prop,-36} x{n,-6}{range}");
        }
        return 0;
    }

    if (sub == "name" && a.Count >= 2)
    {
        bool owned = loaded.DefinesTemplate(a[1]);
        Console.WriteLine(owned
            ? $"'{a[1]}' IS a retail BFV template - do NOT redefine it. A level's scripts load after stock, so " +
              "your definition would REPLACE stock's for the whole game; drop yours and let stock's stand."
            : $"'{a[1]}' is not a retail BFV template name - safe to define.");
        return 0;
    }

    Console.WriteLine($"census: {loaded.FilesScanned:N0} files, {loaded.Classes.Count:N0} classes, " +
                      $"{loaded.TemplateNames.Count:N0} template names  ({censusPath})");
    Console.WriteLine("  rbridge census build [archives...]");
    Console.WriteLine("  rbridge census judge <Class> <setProperty>");
    Console.WriteLine("  rbridge census class <Class>");
    Console.WriteLine("  rbridge census name  <TemplateName>");
    return 0;
}

int RunTmInfo(List<string> a)
{
    if (a.Count < 1) return Usage();
    var found = new List<(string Name, byte[] Bytes)>();

    if (ConSource.LooksLikeArchive(a[0]) && File.Exists(a[0]))
    {
        var archive = new RefractorForge.Formats.Rfa.RefractorFlatArchive(a[0]);
        foreach (var e in archive.Entries)
        {
            if (!e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.Count > 1 && !e.Name.Contains(a[1], StringComparison.OrdinalIgnoreCase)) continue;
            found.Add((Path.GetFileNameWithoutExtension(e.Name), archive.Read(e)));
        }
    }
    else if (File.Exists(a[0])) found.Add((Path.GetFileNameWithoutExtension(a[0]), File.ReadAllBytes(a[0])));

    string[] groupNames = { "leaf", "trunk", "sprite", "extra" };
    foreach (var (name, bytes) in found)
    {
        var tm = RefractorForge.Formats.Rfa.TreeMesh.Parse(bytes);
        Console.WriteLine($"{name}  v{tm.Version}  {tm.Vertices.Length} verts  {tm.Indices.Length / 3} tris  " +
                          $"collision={(tm.HasCollision ? $"{tm.CollisionVertices.Length} verts" : "none")}  " +
                          $"parsed {tm.Consumed}/{bytes.Length} bytes");
        for (int g = 0; g < tm.Groups.Length; g++)
        {
            if (tm.Groups[g].Count == 0) { Console.WriteLine($"    {groupNames[g],-7} -"); continue; }
            foreach (var m in tm.Groups[g])
                Console.WriteLine($"    {groupNames[g],-7} start={m.Start,-6} tris={m.Count,-6} tex='{m.TexName}'");
        }
    }
    if (found.Count == 0) Console.WriteLine("no .tm matched");
    return 0;
}

int RunTmConvert(List<string> a)
{
    if (a.Count < 2) return Usage();
    string source = a[0], outDir = a[1];
    Directory.CreateDirectory(outDir);

    var meshes = new List<(string Name, byte[] Bytes)>();
    if (ConSource.LooksLikeArchive(source) && File.Exists(source))
    {
        var archive = new RefractorForge.Formats.Rfa.RefractorFlatArchive(source);
        foreach (var e in archive.Entries)
            if (e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase))
                meshes.Add((Path.GetFileNameWithoutExtension(e.Name), archive.Read(e)));
    }
    else if (File.Exists(source)) meshes.Add((Path.GetFileNameWithoutExtension(source), File.ReadAllBytes(source)));
    else if (Directory.Exists(source))
        foreach (string f in Directory.EnumerateFiles(source, "*.tm", SearchOption.AllDirectories))
            meshes.Add((Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f)));
    else { Console.Error.WriteLine($"no such .tm source: {source}"); return 1; }

    int ok = 0, failed = 0, billboardOnly = 0, spriteMats = 0, spriteTris = 0, withCollision = 0;
    foreach (var (name, bytes) in meshes)
    {
        try
        {
            // Not a failure, a category: with the billboards dropped there is no geometry left, so this one
            // needs a BFV vegetation substitute or to be dropped. That is a content decision, not a conversion.
            if (RefractorBridge.Mesh.TmToSm.IsBillboardOnly(bytes))
            {
                billboardOnly++;
                Console.WriteLine($"  [skip] {name,-34} billboard-only - substitute BFV vegetation or drop");
                continue;
            }

            var c = RefractorBridge.Mesh.TmToSm.Convert(bytes, name, alphaTestRef, reverseWinding);

            // Verify before writing: the .sm must parse back, and the .rs must define every material the .sm
            // names - a name the .rs omits is not an error, the surface just renders untextured.
            var reparsed = RefractorForge.Formats.Rfa.StandardMesh.Parse(c.StandardMesh);
            var declared = reparsed.Lods[0].Select(m => m.Name).ToList();
            var missing = declared.Where(d => !c.RsText.Contains($"\"{d}\"", StringComparison.Ordinal)).ToList();
            if (missing.Count > 0) throw new InvalidDataException($".rs is missing subshader(s): {string.Join(", ", missing)}");

            File.WriteAllBytes(Path.Combine(outDir, name + ".sm"), c.StandardMesh);
            File.WriteAllText(Path.Combine(outDir, name + ".rs"), c.RsText, Encoding.Latin1);

            ok++;
            spriteMats += c.SpriteMaterialsDropped;
            spriteTris += c.SpriteTrianglesDropped;
            if (c.CollisionCarried) withCollision++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  [FAIL] {name,-34} {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{meshes.Count} .tm found: {ok} converted, {billboardOnly} billboard-only (skipped), {failed} failed.");
    Console.WriteLine($"  {withCollision} carried a collision hull (trees are solid in game).");
    Console.WriteLine($"  {spriteMats} sprite material(s) / {spriteTris} triangle(s) dropped - BFV has no billboard-imposter shader.");
    Console.WriteLine($"  written to {Path.GetFullPath(outDir)}");
    return failed == 0 ? 0 : 2;
}

int RunLs(List<string> a)
{
    // Reading the target game's own content is the highest-value research move there is - "find a mod that
    // already solved it" settled the material question that inference had got backwards.
    if (a.Count < 1) return Usage();
    var archive = new RefractorForge.Formats.Rfa.RefractorFlatArchive(a[0]);
    string? pattern = a.Count > 1 ? a[1] : null;

    int shown = 0;
    foreach (var e in archive.Entries)
    {
        if (pattern is not null && !e.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"{e.UncompressedSize,10:N0}  {e.Name}");
        shown++;
    }
    Console.WriteLine($"-- {shown} of {archive.Entries.Count} entries");
    return 0;
}

int RunCat(List<string> a)
{
    if (a.Count < 2) return Usage();
    var archive = new RefractorForge.Formats.Rfa.RefractorFlatArchive(a[0]);
    var hit = archive.Entries.FirstOrDefault(e => e.Name.Contains(a[1], StringComparison.OrdinalIgnoreCase));
    if (hit is null) { Console.Error.WriteLine($"no entry matching '{a[1]}'"); return 1; }

    Console.WriteLine($"--- {hit.Name} ({hit.UncompressedSize:N0} B) ---");
    Console.WriteLine(Encoding.Latin1.GetString(archive.Read(hit)));
    return 0;
}

int RunOracleQuery(List<string> names)
{
    // Interrogate the oracle directly. This is how a new command found in the wild gets a rule: ask both
    // engines, and let the answer - not a guess - decide whether it is dropped, renamed or left alone.
    var (bfv, bf42) = LoadOracles();
    Console.WriteLine($"{"property",-36} {"BF1942",-8} {"BFV",-8} verdict");

    foreach (string n in names)
    {
        string leaf = ExeSymbolTable.Leaf(n);
        string twin = ExeSymbolTable.SetTwinLeaf(leaf);
        bool a = bf42.HasProperty(leaf), b = bfv.HasProperty(leaf);
        bool at = bf42.HasProperty(twin), bt = bfv.HasProperty(twin);

        string verdict = (a || at, b || bt) switch
        {
            (true, false) => "BF1942-ONLY -> drop or rewrite",
            (false, false) => "dead in BOTH -> drop while cleaning",
            (_, true) => "present in BFV -> ports unchanged",
        };

        Console.WriteLine($"{leaf,-36} {YN(a),-8} {YN(b),-8} {verdict}");
        if (twin.Length > 0 && (at || bt))
            Console.WriteLine($"  via twin '{twin}'{new string(' ', Math.Max(1, 22 - twin.Length))} {YN(at),-8} {YN(bt),-8}");
    }
    return 0;

    static string YN(bool v) => v ? "yes" : "no";
}

int RunConDelta(List<string> targets)
{
    if (targets.Count == 0) return Usage();
    var (bfv, bf42) = LoadOracles();

    var files = new List<ConFile>();
    foreach (string t in targets) files.AddRange(ConSource.Load(t));

    var analysis = ConDialect.Analyze(files, bfv, bf42);

    if (summary)
    {
        // One line per target, for censusing a whole mod corpus.
        int unruled = analysis.Rows.Count(r => r.Verdict == CommandVerdict.MissingEngineLevel);
        int bf42Only = analysis.Rows.Count(r => r.Verdict == CommandVerdict.DropBf1942Only);
        int trees = analysis.Reviews.Count(r => r.Kind == ReviewKind.TreeMesh);
        int perClass = analysis.Reviews.Count(r => r.Kind == ReviewKind.PerClassRename);
        Console.WriteLine($"{analysis.FilesScanned,6} con {analysis.Rows.Count,5} cmds " +
                          $"{bf42Only,4} bf42only {unruled,4} UNRULED {trees,5} treemesh {perClass,6} setX  " +
                          Path.GetFileName(targets[0]));
        if (unruled > 0)
            foreach (var r in analysis.Rows.Where(r => r.Verdict == CommandVerdict.MissingEngineLevel))
                Console.WriteLine($"         UNRULED  {r.Command}  x{r.Count}  {r.ExampleFile}");
        return 0;
    }

    Console.WriteLine($"scanned {analysis.FilesScanned} .con file(s) from {string.Join(", ", targets)}");
    Console.WriteLine($"{analysis.Rows.Count} distinct commands");
    Console.WriteLine();

    foreach (var group in analysis.Rows.GroupBy(r => r.Verdict))
    {
        Console.WriteLine($"--- {Heading(group.Key)} ({group.Count()}) ---");
        foreach (var row in group)
        {
            Console.WriteLine($"  {row.Command,-44} x{row.Count,-5} {row.ExampleFile}");
            if (row.Note.Length > 0) Console.WriteLine($"      {row.Note}");
        }
        Console.WriteLine();
    }

    if (analysis.MissingRequired.Count > 0)
    {
        Console.WriteLine($"--- BFV levels normally set these, and this content does not ({analysis.MissingRequired.Count}) ---");
        foreach (string m in analysis.MissingRequired) Console.WriteLine($"  {m}");
        Console.WriteLine();
    }

    if (analysis.Reviews.Count > 0)
    {
        Console.WriteLine($"--- NEEDS A HUMAN ({analysis.Reviews.Count}) ---");
        foreach (var r in analysis.Reviews.Take(40))
        {
            Console.WriteLine($"  {r.File}:{r.Line}  {r.Text}");
            Console.WriteLine($"      {r.Note}");
        }
        if (analysis.Reviews.Count > 40) Console.WriteLine($"  ... and {analysis.Reviews.Count - 40} more");
        Console.WriteLine();
    }

    int surviving = analysis.Rows.Count(r => r.Verdict is CommandVerdict.DropBf1942Only or CommandVerdict.MissingEngineLevel);
    Console.WriteLine(surviving == 0
        ? "No BF1942-only engine commands found."
        : $"{surviving} command(s) will not work in BFV as written.");

    return strict && surviving > 0 ? 3 : 0;

    static string Heading(CommandVerdict v) => v switch
    {
        CommandVerdict.PresentInBfv => "PORTS UNCHANGED",
        CommandVerdict.DropBf1942Only => "BF1942-ONLY - dropped",
        CommandVerdict.DropDeadInBoth => "DEAD IN BOTH ENGINES - dropped while cleaning",
        CommandVerdict.Rename => "RENAMED for BFV",
        CommandVerdict.ValueFix => "ARGUMENT SHAPE CHANGED",
        CommandVerdict.MissingEngineLevel => "MISSING from BfVietnam.exe - no rule yet, must be handled",
        CommandVerdict.SoftTableProperty => "per-class property (exe absence is inconclusive)",
        _ => v.ToString(),
    };
}

int RunRfaCheck(List<string> targets)
{
    if (targets.Count == 0) return Usage();

    int failed = 0;
    foreach (string t in targets)
    {
        var p = RefractorBridge.Rfa.RepackSelfTest.Run(t);
        string name = Path.GetFileName(p.Archive);

        if (p.Failure is not null)
        {
            Console.WriteLine($"  [FAIL] {name,-34} {p.Failure}");
            failed++;
            continue;
        }

        string kind = p.Compressed ? "LZO" : "raw";
        if (p.Faithful)
        {
            string note = p.ByteIdentical
                ? "byte-identical"
                : "faithful (data regions relaid in TOC order; the source stored them in another order)";
            Console.WriteLine($"  [ok]   {name,-34} {p.Entries,6} entries  {p.OriginalBytes,13:N0} B  {kind}  {note}");
        }
        else
        {
            failed++;
            string why = !p.HeaderPreserved ? "HEADER/flag changed"
                : !p.TableOfContentsPreserved ? "table of contents changed"
                : !p.RawRegionsPreserved ? "a data region changed (re-compressed?)"
                : "an entry no longer decodes to the same bytes";
            Console.WriteLine($"  [FAIL] {name,-34} {p.Entries,6} entries  {kind}  {why}");
            if (p.KeptOutput is not null) Console.WriteLine($"         output kept at {p.KeptOutput}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(failed == 0
        ? $"REPACK SELF-TEST PASSED ({targets.Count} archive(s) carried across unchanged)."
        : $"{failed} of {targets.Count} archive(s) FAILED - do not write archives with this build.");
    return failed == 0 ? 0 : 2;
}

int RunConConvert(List<string> a)
{
    if (a.Count < 2) return Usage();
    string source = a[0], outDir = a[1];

    // The census is opt-in: it turns the setX family into rewrites, which is the biggest single change the
    // converter can make to a mod, so it is never applied by accident.
    StockCensus? census = null;
    if (useCensus || censusFile is not null)
    {
        string path = censusFile ?? Path.Combine(AppContext.BaseDirectory, "bfv_stock_census.json");
        if (!File.Exists(path)) { Console.Error.WriteLine($"no census at {path} - run 'rbridge census build'"); return 1; }
        census = StockCensus.Load(path);
        Console.WriteLine($"using retail census: {census.Classes.Count} classes from {census.FilesScanned:N0} files");
    }

    var options = new ConDialectOptions { MapResponsePhysics = mapResponsePhysics, Census = census };

    var files = ConSource.Load(source);
    Directory.CreateDirectory(outDir);

    int changedFiles = 0, totalChanges = 0, untouched = 0;
    var reviews = new List<ConReview>();

    foreach (var file in files)
    {
        var result = ConDialect.Rewrite(file.Bytes, file.Name, options);
        reviews.AddRange(result.Reviews);

        string dest = Path.Combine(outDir, SafeRelative(file.Name));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, result.Bytes);

        if (result.Changed)
        {
            changedFiles++;
            totalChanges += result.Changes.Count;
            Console.WriteLine($"{file.Name}  ({result.Changes.Count} change(s))");
            foreach (var c in result.Changes)
            {
                Console.WriteLine($"  {c.Line,5}: - {c.Before.Trim()}");
                if (c.After is not null) Console.WriteLine($"         + {c.After.Trim()}");
                Console.WriteLine($"         {c.Reason}");
            }
        }
        else untouched++;
    }

    Console.WriteLine();
    Console.WriteLine($"{files.Count} file(s): {changedFiles} rewritten ({totalChanges} change(s)), {untouched} byte-identical.");
    if (reviews.Count > 0) Console.WriteLine($"{reviews.Count} line(s) need a human - run 'condelta' to see them.");
    Console.WriteLine($"written to {Path.GetFullPath(outDir)}");
    return 0;

    // Archive entry names are engine paths; keep the tree but never let one escape the output folder.
    static string SafeRelative(string name)
    {
        string rel = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        rel = rel.TrimStart(Path.DirectorySeparatorChar);
        var parts = rel.Split(Path.DirectorySeparatorChar)
            .Where(p => p.Length > 0 && p != "." && p != "..")
            .ToArray();
        return parts.Length == 0 ? "unnamed.con" : Path.Combine(parts);
    }
}

static string? TakeOption(List<string> argv, string name)
{
    int i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    string value = argv[i + 1];
    argv.RemoveRange(i, 2);
    return value;
}

static bool TakeFlag(List<string> argv, string name)
{
    int i = argv.IndexOf(name);
    if (i < 0) return false;
    argv.RemoveAt(i);
    return true;
}
