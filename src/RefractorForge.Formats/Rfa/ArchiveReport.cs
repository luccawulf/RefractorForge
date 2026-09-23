namespace RefractorForge.Formats.Rfa;

/// <summary>What <see cref="RefractorFlatArchive.Inspect"/> found in one archive: the container fields and a count of
/// every entry form the engine cannot read. <see cref="Errors"/> are problems the game will choke on;
/// <see cref="Warnings"/> are departures from every retail archive that have not been seen to break a load.</summary>
public sealed record ArchiveReport(
    string Path,
    bool IsCompressed,
    bool IsV11Format,
    XPackId XPackId,
    byte[]? Descriptor,
    bool HasTableTail,
    int EntryCount,
    int RawEntriesInCompressed,
    int VerbatimBlocks,
    int WrappedEntriesInUncompressed,
    int PrefixViolations,
    int ZeroByteEntries,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>First bytes of the descriptor in hex - enough to tell the common packers apart (retail <c>ai.rfa</c>'s
    /// header, copied by community packers, starts <c>63 EC 95 BF</c>; the MDT-era one <c>64 A2 A7 CC</c>).</summary>
    public string DescriptorFingerprint(int bytes = 8)
        => Descriptor is null ? "-" : Convert.ToHexString(Descriptor, 0, Math.Min(bytes, Descriptor.Length));

    /// <summary>Whether the descriptor carries the RefractorForge stamp.</summary>
    public bool IsRefractorForgeStamp
        => Descriptor is { Length: >= 14 } d && d.AsSpan(0, 14).SequenceEqual("RefractorForge"u8);
}
