using RefractorForge.Formats.Rfa;

namespace RefractorForge.Render;

/// <summary>
/// Resolves object texture names from <c>.rs</c> shaders (e.g. <c>texture/O_fishinghut_bamboo</c>) to
/// decoded <see cref="Texture2D"/> bitmaps, pulled from the game's <c>texture*.rfa</c> archives and
/// cached. Shaders reference textures by a path-like name without extension; the real file is that name
/// plus <c>.dds</c>. Tall sheets (e.g. 512×2048) are texture atlases that the mesh UVs index into.
/// </summary>
public sealed class TextureLibrary
{
    private readonly List<RfaArchive> _archives = new();
    private readonly Dictionary<string, RfaEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    // Texture/AltTex/<leaf> entries: a map's explicit alternate-texture OVERRIDES. Resolved BEFORE _byName so they win
    // even when an object's .rs references the texture by a FULL base path (the engine's AltTex behaviour). First-wins,
    // and level archives are opened first, so the level's AltTex beats any base AltTex.
    private readonly Dictionary<string, RfaEntry> _override = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RfaEntry, RfaArchive> _owner = new();   // entry -> its archive, O(1) (was an O(n) scan)
    private readonly Dictionary<string, Texture2D?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static TextureLibrary Open(params string[] archivePaths)
    {
        var lib = new TextureLibrary();
        foreach (var path in archivePaths)
        {
            if (!File.Exists(path)) continue;
            if (Path.GetFileName(path).StartsWith("~")) continue;   // ~$… temp/lock leftovers
            RfaArchive arc;
            try { arc = RfaArchive.Open(path); }
            catch (Exception ex) { System.Console.WriteLine($"TextureLibrary: skipping unreadable archive '{Path.GetFileName(path)}' ({ex.GetType().Name})"); continue; }
            lib._archives.Add(arc);
            foreach (var e in arc.Entries)
            {
                // index .dds AND .tga — water textures (water07, normalMap02) and some object textures are .tga.
                if (!e.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                    && !e.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) continue;
                // index by full normalized path and by bare basename, so either form of a shader
                // reference resolves.
                var norm = e.Name.Replace('\\', '/');
                if (lib._byName.TryAdd(norm, e)) lib._owner[e] = arc;
                var bn = norm[(norm.LastIndexOf('/') + 1)..];
                if (lib._byName.TryAdd(bn, e)) lib._owner.TryAdd(e, arc);
                // A level's Texture/AltTex/<name> is an alternate-texture override — index it by leaf so it wins over a
                // base full-path reference (e.g. desertcombat/humvee/Humvee_Hull) the same way the engine swaps it in.
                if (norm.Contains("/alttex/", StringComparison.OrdinalIgnoreCase))
                    if (lib._override.TryAdd(bn, e)) lib._owner.TryAdd(e, arc);
            }
        }
        return lib;
    }

    public int Count => _archives.Sum(a => a.Entries.Count);

    /// <summary>Resolve a shader texture reference (with or without <c>texture/</c> prefix / extension)
    /// to a decoded texture, or null if not present. Results are cached (including misses).</summary>
    public Texture2D? Resolve(string? shaderTextureName)
    {
        if (string.IsNullOrWhiteSpace(shaderTextureName)) return null;
        if (_cache.TryGetValue(shaderTextureName, out var cached)) return cached;

        var entry = Find(shaderTextureName);
        Texture2D? tex = null;
        if (entry is not null)
        {
            try
            {
                var bytes = Owner(entry).Read(entry);
                tex = entry.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                    ? TgaTexture.Decode(bytes) : DdsTexture.Decode(bytes);
            }
            catch { tex = null; }
        }
        _cache[shaderTextureName] = tex;
        return tex;
    }

    private RfaEntry? Find(string name)
    {
        string n = name.Replace('\\', '/').Trim();
        // strip an existing extension so we can try BOTH .dds and .tga (a shader ref like "texture/water07" has none).
        string baseN = (n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) ? n[..^4] : n;
        var bn = baseN[(baseN.LastIndexOf('/') + 1)..];
        // A map's Texture/AltTex/<leaf> override wins first (by bare leaf, either extension) — beats a base full-path ref.
        foreach (var ext in new[] { ".dds", ".tga" })
            if (_override.TryGetValue(bn + ext, out var ov)) return ov;
        foreach (var ext in new[] { ".dds", ".tga" })
            foreach (var cand in new[] { baseN + ext, bn + ext, "texture/" + bn + ext })
                if (_byName.TryGetValue(cand, out var e)) return e;
        return null;
    }

    private RfaArchive Owner(RfaEntry e) => _owner.TryGetValue(e, out var a) ? a : _archives[0];
}
