using System.Text;
using System.Text.RegularExpressions;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// Who uses what. Refractor names assets loosely - a shader says <c>texture "texture/AmmoBox_H"</c>, a sound
/// script names a <c>.wav</c> without its folder, a mesh carries its texture names as bare strings, and
/// <c>textureManager.alternativePath</c> lets the same name resolve into a subfolder - so references are indexed
/// by the asset's BASE NAME, lower-cased, with no folder and no extension. That is exactly how the engine's
/// own lookup treats them, and it is what the MDT's Mod Optimizer had to model with its alt-path list.
///
/// Built once over a set of archives, it answers two questions: which files mention this asset, and which
/// textures and sounds does nothing mention at all - the dead weight the Mod Optimizer existed to find.
/// </summary>
public sealed class AssetReferences
{
    /// <summary>Files whose text is worth scanning for names.</summary>
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".con", ".rs", ".ssc", ".inc", ".txt", ".tm", ".lst", ".fnt", ".wst", ".sst" };
    /// <summary>Binary files that embed asset names as readable strings (mesh material texture names).</summary>
    private static readonly HashSet<string> StringBearingBinaryExt = new(StringComparer.OrdinalIgnoreCase)
        { ".sm", ".tm", ".ske", ".skn" };

    private static readonly HashSet<string> TextureExt = new(StringComparer.OrdinalIgnoreCase) { ".dds", ".tga", ".bmp", ".rcm" };
    private static readonly HashSet<string> SoundExt = new(StringComparer.OrdinalIgnoreCase) { ".wav" };

    // A name-ish token: letters, digits, _ - . / \ ; captured whole so "texture/Pacific/Kubel1_Z" survives.
    private static readonly Regex Token = new(@"[A-Za-z0-9_\-][A-Za-z0-9_\-\./\\]*", RegexOptions.Compiled);
    // Printable runs inside binaries.
    private static readonly Regex Printable = new(@"[\x20-\x7e]{4,}", RegexOptions.Compiled);

    /// <summary>asset base name -> the files that mention it.</summary>
    private readonly Dictionary<string, HashSet<string>> _refs = new(StringComparer.OrdinalIgnoreCase);
    public int FilesScanned { get; private set; }

    public static string Key(string nameOrPath)
    {
        var s = nameOrPath.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot > 0) s = s[..dot];
        return s.ToLowerInvariant();
    }

    /// <summary>Index every referencing file in the given archives.</summary>
    public static AssetReferences Build(IEnumerable<RefractorFlatArchive> archives, Action<string>? progress = null)
    {
        var r = new AssetReferences();
        foreach (var a in archives)
            foreach (var e in a.Entries)
            {
                var ext = Path.GetExtension(e.Name);
                bool text = TextExt.Contains(ext), bin = StringBearingBinaryExt.Contains(ext);
                if (!text && !bin) continue;
                progress?.Invoke(e.Name);
                byte[] data;
                try { data = a.Read(e); } catch { continue; }
                r.Scan(e.Name.Replace('\\', '/'), data, text);
            }
        return r;
    }

    private void Scan(string fileName, byte[] data, bool asText)
    {
        FilesScanned++;
        var self = Key(fileName);
        if (asText)
        {
            var s = Encoding.Latin1.GetString(data);
            foreach (Match m in Token.Matches(s)) Note(m.Value, fileName, self);
        }
        else
        {
            var s = Encoding.Latin1.GetString(data);
            foreach (Match m in Printable.Matches(s))
                foreach (Match t in Token.Matches(m.Value)) Note(t.Value, fileName, self);
        }
    }

    private void Note(string token, string fileName, string self)
    {
        // Strip a trailing ';' or quote debris the tokeniser may have kept, then key it.
        var k = Key(token.TrimEnd(';', ',', ')'));
        if (k.Length < 2 || k == self) return;
        if (!_refs.TryGetValue(k, out var set)) _refs[k] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(fileName);
    }

    /// <summary>Every file that names this asset.</summary>
    public IReadOnlyCollection<string> ReferencesTo(string assetNameOrPath)
        => _refs.TryGetValue(Key(assetNameOrPath), out var s) ? s : Array.Empty<string>();

    public bool IsReferenced(string assetNameOrPath) => _refs.ContainsKey(Key(assetNameOrPath));

    public sealed record Unused(string Name, int Size, string Archive);

    /// <summary>
    /// Textures and sounds in the given archives that nothing in the index mentions. Anything that shares a base
    /// name with a referenced asset is kept, since the engine resolves by base name and may well be loading it.
    /// </summary>
    public List<Unused> UnusedAssets(IEnumerable<(string Label, RefractorFlatArchive Archive)> assetArchives,
                                     bool textures = true, bool sounds = true)
    {
        var outp = new List<Unused>();
        foreach (var (label, a) in assetArchives)
            foreach (var e in a.Entries)
            {
                var ext = Path.GetExtension(e.Name);
                bool isTex = TextureExt.Contains(ext), isSnd = SoundExt.Contains(ext);
                if (!(textures && isTex) && !(sounds && isSnd)) continue;
                if (IsReferenced(e.Name)) continue;
                // Terrain tiles, minimaps, lightmaps and menu art are loaded by convention, not by reference.
                if (IsLoadedByConvention(e.Name)) continue;
                outp.Add(new Unused(e.Name.Replace('\\', '/'), e.UncompressedSize, label));
            }
        return outp.OrderByDescending(u => u.Size).ToList();
    }

    /// <summary>Files the engine opens without any script naming them.</summary>
    public static bool IsLoadedByConvention(string name)
    {
        var n = name.Replace('\\', '/').ToLowerInvariant();
        var leaf = Path.GetFileName(n);
        if (n.Contains("/levels/") && (n.Contains("/textures/") || n.Contains("/objectlightmaps/") || n.Contains("/menu/")
                                       || n.Contains("/texture/sky") || leaf.StartsWith("tx") || leaf.Contains("lightmap")
                                       || leaf.StartsWith("ingamemap") || leaf.StartsWith("thumbnail")
                                       || leaf.Contains("detail") || leaf.Contains("terrain")))
            return true;
        if (n.StartsWith("menu/") || n.Contains("/menu/")) return true;          // the menu system loads by layout
        if (n.StartsWith("font/")) return true;
        if (leaf.StartsWith("env_") || leaf.Contains("skybox")) return true;
        if (n.Contains("/loading") || n.Contains("/briefing") || n.Contains("serverinfo")) return true;
        return false;
    }
}
