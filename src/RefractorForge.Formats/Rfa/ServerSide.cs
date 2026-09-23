namespace RefractorForge.Formats.Rfa;

/// <summary>
/// A dedicated server needs a map's scripts and terrain, not its textures, sounds, movies or baked light. The
/// MDT shipped <c>striprfa.exe</c> with a batch file to do this to a whole folder of level archives, keeping the
/// stripped copy only when it came out smaller. This is that, on the archive implementation that round-trips.
/// </summary>
public static class ServerSide
{
    public sealed record Outcome(string Source, string Output, int EntriesBefore, int EntriesAfter, long BytesBefore, long BytesAfter, bool Written);

    /// <summary>Write the server-side copy of one archive. Returns what was kept and how much was saved.
    /// It is a repack of the source with the client-only entries dropped
    /// (<see cref="RefractorFlatArchive.RepackToFile(string, RefractorFlatArchive, IReadOnlyDictionary{string, byte[]}, Func{string, bool})"/>):
    /// the descriptor, XPack ID, table trailers and tail are the source's, and every kept entry is copied as stored,
    /// never re-encoded. It used to write a NEW archive - our stamp in the descriptor, zero trailers, every entry
    /// through our compressor - the kind of rebuilt container that has crashed BF Vietnam on load while validating
    /// cleanly.</summary>
    public static Outcome Strip(string sourcePath, string outputPath)
    {
        var a = new RefractorFlatArchive(sourcePath);
        int kept = a.Entries.Count(e => !RefractorFlatArchive.IsClientOnlyEntry(e.Name));
        long before = new FileInfo(sourcePath).Length;
        RefractorFlatArchive.RepackToFile(outputPath, a, new Dictionary<string, byte[]>(), drop: RefractorFlatArchive.IsClientOnlyEntry);
        long after = new FileInfo(outputPath).Length;
        return new Outcome(sourcePath, outputPath, a.Entries.Count, kept, before, after, true);
    }

    /// <summary>
    /// Strip every level archive in a folder into <paramref name="outputDir"/>. With <paramref name="dryRun"/>
    /// nothing is written; the outcomes still say what would be kept and saved, so the decision can be looked at
    /// before a single file changes.
    /// </summary>
    public static List<Outcome> StripFolder(string levelsDir, string outputDir, bool dryRun, Action<string>? progress = null)
    {
        var results = new List<Outcome>();
        if (!dryRun) Directory.CreateDirectory(outputDir);
        foreach (var src in Directory.EnumerateFiles(levelsDir, "*.rfa").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            progress?.Invoke(Path.GetFileName(src));
            var outPath = Path.Combine(outputDir, Path.GetFileName(src));
            try
            {
                if (dryRun)
                {
                    var a = new RefractorFlatArchive(src);
                    var keep = a.Entries.Where(e => !RefractorFlatArchive.IsClientOnlyEntry(e.Name)).ToList();
                    long kept = keep.Sum(e => (long)e.BlockSize);
                    results.Add(new Outcome(src, outPath, a.Entries.Count, keep.Count, new FileInfo(src).Length, kept, false));
                }
                else results.Add(Strip(src, outPath));
            }
            catch (Exception ex)
            {
                results.Add(new Outcome(src, outPath + "  (" + ex.Message + ")", 0, 0, 0, 0, false));
            }
        }
        return results;
    }
}
