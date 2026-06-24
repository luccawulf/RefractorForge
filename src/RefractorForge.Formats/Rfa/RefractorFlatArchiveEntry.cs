namespace RefractorForge.Formats.Rfa;

/// <summary>One file inside a <see cref="RefractorFlatArchive"/> (a TOC entry).</summary>
public sealed record RefractorFlatArchiveEntry(
    string Name,
    /// <summary>Total on-disk size of this file's data region (block descriptor table + compressed blocks,
    /// or raw bytes for an uncompressed entry).</summary>
    int BlockSize,
    /// <summary>Decompressed size of the whole file.</summary>
    int UncompressedSize,
    /// <summary>Byte offset of this file's data region from the start of the archive.</summary>
    uint Offset)
{
    public override string ToString() => $"{Name} ({UncompressedSize:N0} B)";
}
