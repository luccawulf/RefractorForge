namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Every archive a mod mounts, seen as the ONE file system the game sees. A Refractor mod is a stack: its own
/// archives over the mods it lists in <c>init.con</c>, over the base game, and inside each of those a
/// <c>_001</c> patch over its base. Open one archive at a time and you see a fragment; a texture reference in a
/// mod's <c>.rs</c> resolves to a file that may live three archives down. This resolves the stack once so a
/// file can be looked up, its provider named, and every archive that also ships a copy of it - the ones it
/// overrides - listed.
///
/// Precedence is by position: <see cref="Layers"/>[0] wins. Callers build that order with
/// <see cref="LayersFor(string, bool)"/>, which knows the engine's literal mount list (<see cref="GameMounts"/>):
/// within a group a mounted patch outranks its base, and an archive the game never reads is not a layer.
/// </summary>
public sealed class ModWorkspace : IDisposable
{
    public sealed record Layer(string Path, string Label, RefractorFlatArchive Archive, string Mod);

    /// <summary>One resolved file: the entry that wins, which layer it came from, and every other layer that
    /// also carries a file of that name (in precedence order) - the copies this one overrides.</summary>
    public sealed record File(string Name, RefractorFlatArchiveEntry Entry, int LayerIndex, IReadOnlyList<int> Overridden);

    public IReadOnlyList<Layer> Layers { get; }
    public IReadOnlyList<File> Files { get; }
    private readonly Dictionary<string, File> _byName;

    private ModWorkspace(List<Layer> layers)
    {
        Layers = layers;
        var byName = new Dictionary<string, (RefractorFlatArchiveEntry E, int L, List<int> Others)>(StringComparer.OrdinalIgnoreCase);
        for (int li = 0; li < layers.Count; li++)
        {
            foreach (var e in layers[li].Archive.Entries)
            {
                var key = e.Name.Replace('\\', '/');
                if (byName.TryGetValue(key, out var have)) have.Others.Add(li);
                else byName[key] = (e, li, new List<int>());
            }
        }
        var files = byName.Select(kv => new File(kv.Key, kv.Value.E, kv.Value.L, kv.Value.Others))
                          .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Files = files;
        _byName = files.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);
    }

    public static ModWorkspace Open(IEnumerable<(string Path, string Mod)> layersInPrecedenceOrder)
    {
        var layers = new List<Layer>();
        foreach (var (p, mod) in layersInPrecedenceOrder)
        {
            try { layers.Add(new Layer(p, System.IO.Path.GetFileName(p), new RefractorFlatArchive(p), mod)); }
            catch { /* an unreadable archive is skipped, the way the engine skips one it cannot mount */ }
        }
        return new ModWorkspace(layers);
    }

    public File? Find(string name) => _byName.TryGetValue(name.Replace('\\', '/'), out var f) ? f : null;

    public byte[] Read(File f) => Layers[f.LayerIndex].Archive.Read(f.Entry);

    /// <summary>Read the copy a specific layer holds - what the winner overrode.</summary>
    public byte[]? ReadFrom(File f, int layerIndex)
    {
        var arch = Layers[layerIndex].Archive;
        var e = arch.Entries.FirstOrDefault(x => string.Equals(x.Name.Replace('\\', '/'), f.Name, StringComparison.OrdinalIgnoreCase));
        return e is null ? null : arch.Read(e);
    }

    /// <summary>
    /// The archives under one mod folder that the engine mounts, in precedence order (<see cref="GameMounts.Scan"/>):
    /// a mounted <c>_001</c> patch ahead of its base, so <c>texture_001.rfa</c> outranks <c>texture.rfa</c> exactly
    /// as the engine layers them, and a level's <c>_NNN</c> patches highest first. Archives the game never reads
    /// (<c>objects_001</c>, a BFV <c>standardMesh_001</c>, any <c>_002</c>) are left out. The game is told from the
    /// folder's install (<see cref="GameMounts.DetectFolder"/>), else <see cref="GameMounts.Union"/> is used. Level
    /// archives are included; pass <paramref name="levelsToo"/> false to leave them out of a global view.
    /// </summary>
    public static List<string> LayersFor(string modDir, bool levelsToo = true)
        => LayersFor(modDir, GameMounts.For(GameMounts.DetectFolder(modDir)), levelsToo);

    /// <summary><see cref="LayersFor(string, bool)"/> for a known game's mount list.</summary>
    public static List<string> LayersFor(string modDir, GameMounts mounts, bool levelsToo = true)
        => mounts.Scan(modDir, levelsToo).Mounted.Select(a => a.Path).ToList();

    /// <summary>The full stack for a mod and everything it mounts, highest precedence first, read with the chain's
    /// own game (<see cref="GameMounts.For(ModChainResult)"/>).</summary>
    public static List<(string Path, string Mod)> LayersForChain(ModChainResult chain, bool levelsToo = true)
    {
        var mounts = GameMounts.For(chain);
        var result = new List<(string, string)>();
        foreach (var m in chain.Mounts)
        {
            var modName = new System.IO.DirectoryInfo(m.Path).Name;
            foreach (var p in LayersFor(m.Path, mounts, levelsToo)) result.Add((p, modName));
        }
        return result;
    }

    public void Dispose() { }
}
