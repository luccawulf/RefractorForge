using RefractorBridge.Con;
using RefractorForge.Formats.Rfa;

namespace RefractorBridge.Level;

/// <summary>A file somewhere in the pool, and the archive it came from.</summary>
public sealed record PoolEntry(string Name, RefractorFlatArchive Archive, RefractorFlatArchiveEntry Entry)
{
    public byte[] Read() => Archive.Read(Entry);
}

/// <summary>
/// Everything a mod's archives can supply, indexed for lookup.
///
/// A BF1942 level does not contain its own objects - it places them by name and the engine finds them in the
/// mod's shared <c>Objects.rfa</c>, <c>standardMesh.rfa</c> and <c>texture.rfa</c> (falling back to the base
/// game's). A ported level has no such shared library to fall back on, so anything it places has to be lifted
/// out of these archives and embedded. This class is the lookup side of that.
///
/// The organising fact: BF1942 keeps ONE FOLDER PER OBJECT -
/// <c>Objects/Vegetation/Common/Afri_bush1_M1/{Geometries.con, Objects.con}</c> - which is also the shape
/// Battlefield Vietnam wants a level-local object in. So embedding is mostly folder copying, not line slicing.
///
/// Archives are searched in the order given and the FIRST hit wins, so a mod's own archives must come before
/// the base game's, and a patch archive (<c>_001</c>) before what it patches. Desert Combat ships a 104-byte
/// silent placeholder that its patch archive overrides with the real clip; resolve the wrong way round and the
/// level ships silence.
/// </summary>
public sealed class AssetPool
{
    private readonly List<RefractorFlatArchive> _archives = new();

    /// <summary>template name -> the folder that defines it (e.g. "objects/vegetation/common/afri_bush1_m1").</summary>
    private readonly Dictionary<string, string> _templateFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>folder -> its .con files, in archive order.</summary>
    private readonly Dictionary<string, List<PoolEntry>> _folderScripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>geometry name -> the mesh file it names (basename, no extension).</summary>
    private readonly Dictionary<string, string> _geometryFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>geometry name -> the geometry TYPE it was created as (StandardMesh, TreeMesh, ...).</summary>
    private readonly Dictionary<string, string> _geometryType = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PoolEntry> _meshes = new(StringComparer.OrdinalIgnoreCase);   // .sm
    private readonly Dictionary<string, PoolEntry> _materials = new(StringComparer.OrdinalIgnoreCase); // .rs
    private readonly Dictionary<string, PoolEntry> _treeMeshes = new(StringComparer.OrdinalIgnoreCase); // .tm
    private readonly Dictionary<string, PoolEntry> _textures = new(StringComparer.OrdinalIgnoreCase);  // .dds/.tga
    private readonly Dictionary<string, PoolEntry> _skeletons = new(StringComparer.OrdinalIgnoreCase); // .ske
    private readonly Dictionary<string, PoolEntry> _skins = new(StringComparer.OrdinalIgnoreCase);      // .skn

    public int TemplateCount => _templateFolder.Count;
    public int MeshCount => _meshes.Count;
    public int TreeMeshCount => _treeMeshes.Count;
    public int TextureCount => _textures.Count;
    public int SkeletonCount => _skeletons.Count;
    public int GeometryCount => _geometryFile.Count;

    /// <summary>Index a set of archives. Earlier archives win, so pass patches and mod archives first.</summary>
    public static AssetPool Build(IEnumerable<string> archivePaths)
    {
        var pool = new AssetPool();
        foreach (string path in archivePaths)
        {
            if (!File.Exists(path)) continue;
            var archive = new RefractorFlatArchive(path);
            pool._archives.Add(archive);
            pool.Index(archive);
        }
        return pool;
    }

    private void Index(RefractorFlatArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            string name = entry.Name.Replace('\\', '/');
            string ext = Path.GetExtension(name);
            string bare = Path.GetFileNameWithoutExtension(name);
            var pooled = new PoolEntry(name, archive, entry);

            switch (ext.ToLowerInvariant())
            {
                case ".sm": _meshes.TryAdd(bare, pooled); break;
                case ".rs": _materials.TryAdd(bare, pooled); break;
                case ".tm": _treeMeshes.TryAdd(bare, pooled); break;
                case ".ske": _skeletons.TryAdd(bare, pooled); break;
                case ".skn": _skins.TryAdd(bare, pooled); break;
                case ".dds":
                case ".tga": _textures.TryAdd(bare, pooled); break;

                case ".con":
                    IndexScript(pooled, name);
                    break;
            }
        }
    }

    private void IndexScript(PoolEntry pooled, string name)
    {
        string folder = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? "";

        // An object's AI lives in a subfolder of the object's own; treat it as part of the object.
        string objectFolder = folder.EndsWith("/AI", StringComparison.OrdinalIgnoreCase)
            ? folder[..^3]
            : folder;

        if (!_folderScripts.TryGetValue(objectFolder, out var scripts))
            _folderScripts[objectFolder] = scripts = new List<PoolEntry>();
        scripts.Add(pooled);

        string? currentGeometry = null;
        foreach (var cmd in ConDialect.Commands(name, pooled.Read()))
        {
            var args = ConDialect.Args(cmd.Rest);

            if (cmd.Command.Equals("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
            {
                _templateFolder.TryAdd(args[1], objectFolder);
            }
            else if (cmd.Command.Equals("GeometryTemplate.create", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
            {
                currentGeometry = args[1];
                _geometryType.TryAdd(currentGeometry, args[0]);
            }
            else if (cmd.Command.Equals("GeometryTemplate.file", StringComparison.OrdinalIgnoreCase)
                     && args.Length >= 1 && currentGeometry is not null)
            {
                _geometryFile.TryAdd(currentGeometry, Path.GetFileNameWithoutExtension(args[0].Replace('\\', '/')));
            }
        }
    }

    // --- lookups -----------------------------------------------------------------------------------------------

    public bool DefinesTemplate(string template) => _templateFolder.ContainsKey(template);

    public string? FolderFor(string template) =>
        _templateFolder.TryGetValue(template, out string? f) ? f : null;

    public IReadOnlyList<PoolEntry> ScriptsIn(string folder) =>
        _folderScripts.TryGetValue(folder, out var s) ? s : Array.Empty<PoolEntry>();

    public string? GeometryFile(string geometry) =>
        _geometryFile.TryGetValue(geometry, out string? f) ? f : null;

    public string? GeometryType(string geometry) =>
        _geometryType.TryGetValue(geometry, out string? t) ? t : null;

    public PoolEntry? Mesh(string bare) => _meshes.GetValueOrDefault(bare);
    public PoolEntry? Material(string bare) => _materials.GetValueOrDefault(bare);
    public PoolEntry? TreeMesh(string bare) => _treeMeshes.GetValueOrDefault(bare);
    public PoolEntry? Texture(string bare) => _textures.GetValueOrDefault(bare);

    /// <summary>The skeleton (.ske) or skin (.skn) an animated mesh of this name needs, if the pool has one.</summary>
    public PoolEntry? Skeleton(string bare) => _skeletons.GetValueOrDefault(bare);

    /// <summary>The skin (.skn) an animated mesh names through GeometryTemplate.setSkin.</summary>
    public PoolEntry? Skin(string bare) => _skins.GetValueOrDefault(bare);

    /// <summary>
    /// The mods a mod inherits from, in the engine's own search order, read from its <c>init.con</c>.
    ///
    /// This is DECLARED, not guessable: every mod lists its chain with <c>game.addModPath</c>, first entry
    /// highest priority. DC_Final says DC_Final → DesertCombat → BF1942; FHSW says FHSW → FH → Bf1942;
    /// Eastern_Front enumerates all four of Eastern_Front → DC_Final → DesertCombat → BF1942, so the chain is
    /// always spelled out in full and never needs resolving transitively.
    ///
    /// Follow it LITERALLY rather than assuming "own mod first, base game last": pr1942 declares
    /// pr1942 → bf1942 → pr1942/XPack1, putting a nested path BELOW the base game. The command's own casing
    /// varies too (FHSW writes <c>addmodPath</c>).
    /// </summary>
    public static IReadOnlyList<string> ModChain(string bf1942Root, string modName)
    {
        string init = Path.Combine(bf1942Root, "Mods", modName, "init.con");
        if (!File.Exists(init)) return new[] { modName, "bf1942" };

        var chain = new List<string>();
        foreach (var cmd in ConDialect.Commands(init, File.ReadAllBytes(init)))
        {
            if (!cmd.Command.Equals("game.addModPath", StringComparison.OrdinalIgnoreCase)) continue;
            var args = ConDialect.Args(cmd.Rest);
            if (args.Length == 0) continue;

            // "Mods/DC_Final/" -> "DC_Final"; "Mods/pr1942/XPack1/" -> "pr1942/XPack1".
            string path = args[0].Trim('"').Replace('\\', '/').Trim('/');
            if (path.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase)) path = path["Mods/".Length..];
            if (path.Length > 0) chain.Add(path);
        }

        return chain.Count > 0 ? chain : new[] { modName, "bf1942" };
    }

    /// <summary>
    /// Every archive a mod's content can come from, in the order the engine searches them: each mod in the
    /// declared inheritance chain, and within a mod its patch archives highest-number-first, then the base.
    /// </summary>
    public static IEnumerable<string> StandardSearchPaths(string bf1942Root, string modName)
    {
        string[] names = { "Objects", "StandardMesh", "treeMesh", "texture", "Animations" };

        foreach (string mod in ModChain(bf1942Root, modName))
        {
            string archives = Path.Combine(bf1942Root, "Mods", mod, "Archives");
            if (!Directory.Exists(archives)) continue;

            // Patch archives override the base one and the suffixes are GAME PATCH VERSIONS, so they are
            // neither contiguous nor bounded: levels ship _000, _003, _006, _009. Enumerate whatever is
            // actually there and search the highest first, because this pool is first-hit-wins.
            foreach (string n in names)
            {
                var patches = new List<(int Number, string Path)>();
                foreach (string candidate in Directory.EnumerateFiles(archives, n + "_*.rfa"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        Path.GetFileNameWithoutExtension(candidate), @"^(.*)_(\d{3})$");
                    if (m.Success && string.Equals(m.Groups[1].Value, n, StringComparison.OrdinalIgnoreCase))
                        patches.Add((int.Parse(m.Groups[2].Value), candidate));
                }

                foreach (var (_, path) in patches.OrderByDescending(x => x.Number)) yield return path;

                string basePath = Path.Combine(archives, n + ".rfa");
                if (File.Exists(basePath)) yield return basePath;
            }
        }
    }
}
