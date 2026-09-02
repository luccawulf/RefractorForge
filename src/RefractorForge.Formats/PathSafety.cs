namespace RefractorForge.Formats;

/// <summary>
/// Guards for the one thing the editor does that destroys files it did not create: deleting a project folder.
///
/// A project folder is whatever the user pointed at — "Open Level Folder", or the destination they chose when
/// extracting an .rfa — and nothing stopped that from being the folder RefractorForge itself runs from, a game
/// install, or a whole user profile. Deleting a project then took the application (or the game) with it. This
/// decides what must never be handed to a recursive delete, and what deserves a warning before it becomes a
/// project folder in the first place.
/// </summary>
public static class PathSafety
{
    /// <summary>Why <paramref name="dir"/> must never be recursively deleted, or null when it is ordinary.
    /// <paramref name="appDir"/> defaults to where the running application lives.</summary>
    public static string? ProtectedReason(string? dir, string? appDir = null)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "no folder was given";

        string full;
        try { full = Norm(Path.GetFullPath(dir)); }
        catch { return "that is not a valid folder path"; }

        var root = Norm(Path.GetPathRoot(full) ?? "");
        if (root.Length > 0 && Same(full, root)) return "it is the root of a drive";

        // The application itself. Deleting the folder it runs from is what turned a project delete into
        // "the editor deleted itself" - and an ancestor of it is just as fatal.
        var app = Norm(Path.GetFullPath(string.IsNullOrWhiteSpace(appDir) ? AppContext.BaseDirectory : appDir!));
        if (Same(full, app)) return "it is the folder RefractorForge itself runs from";
        if (Contains(full, app)) return "it contains the RefractorForge installation";

        foreach (var f in new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonProgramFiles,
        })
        {
            string p;
            try { p = Norm(Environment.GetFolderPath(f)); } catch { continue; }
            if (p.Length == 0) continue;
            if (Same(full, p)) return $"it is a system folder ({p})";
            if (Contains(full, p)) return $"it contains a system folder ({p})";
        }

        // A game install is not a project folder. Someone extracting a map "into the game" and later deleting the
        // project would take Battlefield with it.
        try
        {
            foreach (var exe in new[] { "BF1942.exe", "BfVietnam.exe", "BF1942_w32ded.exe", "BfVietnam_w32ded.exe" })
                if (File.Exists(Path.Combine(full, exe))) return "it is a game installation folder";
        }
        catch { }

        return null;
    }

    /// <summary>True when the folder holds nothing at all - the only case where extracting into it is unambiguous.</summary>
    public static bool IsEmpty(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        try
        {
            if (!Directory.Exists(dir)) return true;
            return !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch { return false; }
    }

    /// <summary>How many files a folder holds, and their total size - for a confirmation that states the stakes.</summary>
    public static (int Files, long Bytes) Measure(string dir)
    {
        int files = 0; long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return (files, bytes);
    }

    private static string Norm(string p)
        => p.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool Same(string a, string b)
        => a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="outer"/> is an ancestor of <paramref name="inner"/>.</summary>
    private static bool Contains(string outer, string inner)
        => outer.Length > 0 && inner.Length > outer.Length
        && inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase)
        && (inner[outer.Length] == Path.DirectorySeparatorChar || inner[outer.Length] == Path.AltDirectorySeparatorChar);
}
