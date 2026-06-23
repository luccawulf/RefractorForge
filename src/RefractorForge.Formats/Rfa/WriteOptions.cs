namespace RefractorForge.Formats.Rfa;

/// <summary>Options controlling how a <see cref="RefractorFlatArchive"/> is written.</summary>
public sealed record WriteOptions
{
    /// <summary>LZO1X-compress entry blocks. Set to <c>false</c> for uncompressed archives
    /// (valid; larger; useful for debugging or when targeting a tool that cannot decompress).</summary>
    public bool Compress { get; init; } = true;

    /// <summary>Expansion-pack binding written into the archive header.</summary>
    public XPackId XPackId { get; init; } = XPackId.Default;

    public static readonly WriteOptions Default      = new();
    public static readonly WriteOptions Uncompressed = new() { Compress = false };
}
