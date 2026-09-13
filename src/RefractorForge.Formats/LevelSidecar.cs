namespace RefractorForge.Formats;

/// <summary>
/// The editor's own files for a level - placed lights, object groups, review notes. They are authoring data the
/// engine knows nothing about, so they never go inside a level: a folder level keeps them in the folder, and a
/// PACKED level keeps them beside the archive as <c>&lt;level&gt;.&lt;name&gt;.json</c>.
///
/// Why this exists: every one of them used to be written with <c>Path.Combine(levelDir, name)</c> and
/// <c>Directory.CreateDirectory(levelDir)</c>. For a packed level `levelDir` is the .rfa FILE, so the moment a
/// level had anything to remember - a placed light, or a night preset, which sets the night amount - the save threw
/// "cannot create ... a file with the same name already exists" and the whole save was abandoned.
/// </summary>
public static class LevelSidecar
{
    /// <summary>Where a sidecar lives for a level that is either a folder or a packed archive.</summary>
    public static string PathFor(string levelPathOrDir, string fileName)
    {
        if (LooksLikeArchive(levelPathOrDir))
        {
            var full = Path.GetFullPath(levelPathOrDir);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                return Path.Combine(dir, Path.GetFileNameWithoutExtension(full) + "." + fileName);
        }
        return Path.Combine(levelPathOrDir, fileName);
    }

    /// <summary>True when the level is one packed file rather than a folder.</summary>
    public static bool LooksLikeArchive(string levelPathOrDir)
    {
        if (string.IsNullOrEmpty(levelPathOrDir) || Directory.Exists(levelPathOrDir)) return false;
        return File.Exists(levelPathOrDir) || Path.GetExtension(levelPathOrDir).Length > 0;
    }

    /// <summary>Write a sidecar, making sure the folder it goes in exists - never the level itself.</summary>
    public static void Write(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text);
    }

    /// <summary>Whether a file name is this sidecar - under its plain name, or a packed level's
    /// <c>&lt;level&gt;.&lt;name&gt;</c> form. Used to keep them out of a packed archive.</summary>
    public static bool IsNamed(string pathOrLeaf, string fileName)
    {
        var leaf = pathOrLeaf.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        return leaf.Equals(fileName, StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase);
    }
}
