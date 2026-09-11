namespace RefractorForge.Formats;

/// <summary>
/// Getting a file onto the DISK, not just into Windows' write cache.
///
/// A save writes a whole new archive to a temp file and renames it over the level. The rename is a metadata change
/// NTFS journals at once; the 440 MB of data behind it sits in the cache until the lazy writer gets round to it. On
/// 2026-09-11 the user's PC went down 40 seconds after an al_vietnas save (the crash came from elsewhere): the rename
/// had landed, the data had not, and the level came back with its table of contents - the last thing written - all
/// zeros, opening as an empty archive. The auto-backup copied just before it was broken the same way. Only because
/// a backup from three minutes earlier had reached the disk could the save be rebuilt at all.
///
/// So anything that replaces or backs up a level is flushed first: the rename can only ever expose a complete file.
/// </summary>
public static class DurableFile
{
    /// <summary>Block until everything written to <paramref name="path"/> is on the disk (FlushFileBuffers).</summary>
    public static void FlushToDisk(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Flush(flushToDisk: true);
    }

    /// <summary><see cref="File.Copy(string, string, bool)"/>, then flushed - a backup that is only in the cache is
    /// not a backup.</summary>
    public static void Copy(string source, string destination, bool overwrite = false)
    {
        File.Copy(source, destination, overwrite);
        FlushToDisk(destination);
    }
}
