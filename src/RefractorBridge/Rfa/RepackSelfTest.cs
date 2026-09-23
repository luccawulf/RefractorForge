using RefractorForge.Formats.Rfa;

namespace RefractorBridge.Rfa;

/// <summary>What a zero-change repack of an archive proved.</summary>
public sealed record RepackProof(
    string Archive,
    long OriginalBytes,
    int Entries,
    bool Compressed,
    bool HeaderPreserved,
    bool TableOfContentsPreserved,
    bool RawRegionsPreserved,
    bool EveryEntryDecodesIdentically,
    bool ByteIdentical,
    bool DataRegionsReordered,
    string? Failure,
    string? KeptOutput = null)
{
    /// <summary>
    /// The property that actually matters: nothing about the container or its payload changed. Byte-identity is
    /// reported separately because it is NOT required - see <see cref="RepackSelfTest"/>.
    /// </summary>
    public bool Faithful => Failure is null
                            && HeaderPreserved && TableOfContentsPreserved
                            && RawRegionsPreserved && EveryEntryDecodesIdentically;
}

/// <summary>
/// The gate on every archive the converter will ever write: a repack with ZERO changes must not alter the
/// container or any payload.
///
/// This is not ceremony. The most expensive failure in the hand port was container corruption, not content: a
/// general-purpose writer happily marks the archive compressed, zeroes the 148-byte header blob and substitutes
/// its own 12-byte entry trailers, and if the source was uncompressed the engine then hunts for block headers
/// that do not exist and dies on load. Three consecutive "the map crashes" reports were entirely this.
///
/// WHAT IS CHECKED, and why it is not simply "the files are identical":
/// RefractorForge's <see cref="RefractorFlatArchive.RepackToFile"/> carries kept entries across as RAW REGIONS,
/// so nothing is re-compressed - but it lays those regions down in TOC ORDER. Real archives do not always store
/// them that way: berlin_003.rfa lists <c>AI.con</c> first in its table while its data sits 2,505 bytes in, with
/// another entry's data at the front. Repacking such an archive yields the same entries, the same sizes, the
/// same trailers and the same bytes, at different offsets - a physically reordered, functionally identical file.
/// Demanding whole-file identity would fail those archives for a difference the engine cannot observe, so the
/// gate checks FAITHFULNESS (header, table, raw regions, decoded payloads) and reports byte-identity as
/// information. Archives already in TOC order - DC_Al_Nas.rfa's 456 entries, for one - do come back identical.
/// </summary>
public static class RepackSelfTest
{
    /// <summary>Bytes before the first data region: u32 tocOffset, u32 compressedFlag, 148-byte header blob.</summary>
    private const int HeaderBytes = 156;

    public static RepackProof Run(string archivePath, string? workDir = null)
    {
        string tmp = Path.Combine(
            workDir ?? Path.GetTempPath(),
            $"rbridge_repack_{Path.GetFileNameWithoutExtension(archivePath)}_{Environment.ProcessId}.rfa");

        try
        {
            var original = new RefractorFlatArchive(archivePath);
            int entries = original.Entries.Count;
            bool compressed = original.IsCompressed;
            long size = new FileInfo(archivePath).Length;

            RefractorFlatArchive.RepackToFile(tmp, original, new Dictionary<string, byte[]>());
            var repacked = new RefractorFlatArchive(tmp);

            using var fa = File.OpenRead(archivePath);
            using var fb = File.OpenRead(tmp);

            // 1. The flag and the 148-byte blob - the two things a naive writer destroys.
            bool headerOk = RangesEqual(fa, 4, fb, 4, HeaderBytes - 4)
                            && repacked.IsCompressed == compressed;

            // 2. The table: same entries, same order, same sizes.
            bool tocOk = repacked.Entries.Count == entries;
            if (tocOk)
            {
                for (int i = 0; i < entries; i++)
                {
                    var a = original.Entries[i];
                    var b = repacked.Entries[i];
                    if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                        || a.BlockSize != b.BlockSize
                        || a.UncompressedSize != b.UncompressedSize)
                    {
                        tocOk = false;
                        break;
                    }
                }
            }

            // 3. The payloads, as they sit on disk. Comparing RAW regions - not decoded bytes - is what proves
            //    nothing was re-compressed and no block descriptor table was rebuilt.
            bool regionsOk = tocOk, reordered = false;
            if (tocOk)
            {
                for (int i = 0; i < entries; i++)
                {
                    var a = original.Entries[i];
                    var b = repacked.Entries[i];
                    if (a.Offset != b.Offset) reordered = true;
                    if (!RangesEqual(fa, a.Offset, fb, b.Offset, a.BlockSize)) { regionsOk = false; break; }
                }
            }

            // 4. And that the result still READS the same - a container can be intact and still be mis-parsed.
            bool contentOk = tocOk;
            if (tocOk)
            {
                for (int i = 0; i < entries; i++)
                {
                    if (!original.Read(original.Entries[i]).AsSpan().SequenceEqual(repacked.Read(repacked.Entries[i])))
                    {
                        contentOk = false;
                        break;
                    }
                }
            }

            bool identical = size == new FileInfo(tmp).Length && RangesEqual(fa, 0, fb, 0, size);

            var proof = new RepackProof(archivePath, size, entries, compressed,
                headerOk, tocOk, regionsOk, contentOk, identical, reordered, null);

            fa.Dispose();
            fb.Dispose();

            // Keep the output when it disagrees: a container bug is diagnosed by diffing bytes.
            if (proof.Faithful) TryDelete(tmp);
            return proof with { KeptOutput = proof.Faithful ? null : tmp };
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return new RepackProof(archivePath, 0, 0, false, false, false, false, false, false, false, ex.Message);
        }

        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* a leftover temp file is not a failure */ }
        }
    }

    /// <summary>Compare <paramref name="length"/> bytes of two streams at independent offsets.</summary>
    private static bool RangesEqual(FileStream a, long offsetA, FileStream b, long offsetB, long length)
    {
        if (offsetA + length > a.Length || offsetB + length > b.Length) return false;

        a.Position = offsetA;
        b.Position = offsetB;

        byte[] ba = new byte[64 * 1024], bb = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, ba.Length);
            if (ReadFully(a, ba, want) != want || ReadFully(b, bb, want) != want) return false;
            if (!ba.AsSpan(0, want).SequenceEqual(bb.AsSpan(0, want))) return false;
            remaining -= want;
        }
        return true;

        static int ReadFully(Stream s, byte[] buf, int want)
        {
            int total = 0, n;
            while (total < want && (n = s.Read(buf, total, want - total)) > 0) total += n;
            return total;
        }
    }
}
