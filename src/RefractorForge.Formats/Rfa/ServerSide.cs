namespace RefractorForge.Formats.Rfa;

/// <summary>
/// A dedicated server needs a map's scripts and terrain, not its textures, sounds, movies or baked light. The
/// MDT shipped <c>striprfa.exe</c> with a batch file to do this to a whole folder of level archives, keeping the
/// stripped copy only when it came out smaller. This is that, on the archive implementation that round-trips.
/// </summary>
public static class ServerSide
{
    public sealed record Outcome(string Source, string Output, int EntriesBefore, int EntriesAfter, long BytesBefore, long BytesAfter, bool Written);

    /// <summary>Write the server-side copy of one archive. Returns what was kept and how much was saved.</summary>
    public static Outcome Strip(string sourcePath, string outputPath)
    {
        var a = new RefractorFlatArchive(sourcePath);
        var keep = a.ReadServerEntries();
        RefractorFlatArchive.WriteFile(outputPath, keep, a.IsCompressed, a.XPackId);
        long before = new FileInfo(sourcePath).Length, after = new FileInfo(outputPath).Length;
        return new Outcome(sourcePath, outputPath, a.Entries.Count, keep.Count, before, after, true);
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
