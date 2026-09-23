using RefractorForge.Formats.Rfa;

namespace RefractorBridge.Con;

/// <summary>One script, named by its path relative to whatever was opened (or its entry name inside an archive).</summary>
public sealed record ConFile(string Name, byte[] Bytes);

/// <summary>
/// Gets .con scripts out of whatever the user points at: a single file, a level/mod folder, or an .rfa straight
/// off the game install. Reading archives directly matters - BF1942 mod content ships packed, and making someone
/// extract a 300 MB archive before they can ask "will this port?" is how a tool goes unused.
/// </summary>
public static class ConSource
{
    private static readonly string[] DefaultExtensions = { ".con" };

    public static bool LooksLikeArchive(string path) =>
        path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase);

    /// <summary>Load every script under <paramref name="path"/>. Throws only if the path does not exist.</summary>
    public static List<ConFile> Load(string path, IReadOnlyList<string>? extensions = null)
    {
        extensions ??= DefaultExtensions;
        var files = new List<ConFile>();

        if (LooksLikeArchive(path) && File.Exists(path))
        {
            var archive = new RefractorFlatArchive(path);
            foreach (var entry in archive.Entries)
            {
                if (!HasExtension(entry.Name, extensions)) continue;
                files.Add(new ConFile(entry.Name, archive.Read(entry)));
            }
            return files;
        }

        if (File.Exists(path))
        {
            files.Add(new ConFile(Path.GetFileName(path), File.ReadAllBytes(path)));
            return files;
        }

        if (Directory.Exists(path))
        {
            foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (!HasExtension(f, extensions)) continue;
                files.Add(new ConFile(Path.GetRelativePath(path, f), File.ReadAllBytes(f)));
            }
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return files;
        }

        throw new FileNotFoundException($"No such file, folder or archive: {path}");
    }

    private static bool HasExtension(string name, IReadOnlyList<string> extensions)
    {
        foreach (string ext in extensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
