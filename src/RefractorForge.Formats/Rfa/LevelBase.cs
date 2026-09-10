using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RefractorForge.Formats.Rfa;

/// <summary>
/// The identity of the level archive a collaboration session is built on.
///
/// The relay syncs <em>edits</em> - objects, terrain, materials, gameplay, lights. It has never synced the
/// ground those edits sit on: the level's own <c>.rfa</c>, which for a real map is hundreds of MB of terrain
/// tiles, meshes, textures and baked lightmaps. Two people whose archives differ therefore apply the same
/// ordered op stream to different worlds and neither is told. This type is what makes that detectable, and
/// then fixable: a cheap fingerprint everyone can compare on join, and a per-entry manifest that says exactly
/// which files differ when they do.
///
/// The fingerprint is a hash of the archive's raw bytes, not of its contents entry by entry. That is
/// deliberate. It is fast (hundreds of MB in well under a second, hardware accelerated), it needs no
/// decompression, and byte identity is exactly the property that matters here: if your file is not the same
/// file, you should get the session's copy rather than argue about whether the difference is meaningful.
/// The manifest, which does cost a full decompress, is only built when a mismatch has already been found and
/// somebody wants to know what moved.
/// </summary>
public static class LevelBase
{
    /// <summary>Streamed so a 400 MB archive never lands in memory just to be identified.</summary>
    public static string Fingerprint(string archivePath)
    {
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Identity of the level a session is pinned to: what to compare, and what to say about it.</summary>
    public sealed record Id(string Fingerprint, long Bytes, string LevelName)
    {
        /// <summary>The wire form: three tokens, no spaces in any of them (the level name is sanitised).</summary>
        public string Encode() =>
            $"{Fingerprint} {Bytes.ToString(CultureInfo.InvariantCulture)} {Safe(LevelName)}";

        public static Id? TryDecode(string s)
        {
            var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) return null;
            if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)) return null;
            return new Id(p[0], b, p[2]);
        }

        /// <summary>A short form for logs and messages - a full SHA-256 in a toast helps nobody.</summary>
        public string Short => Fingerprint.Length >= 12 ? Fingerprint[..12] : Fingerprint;

        public bool Matches(Id? other) =>
            other is not null && string.Equals(Fingerprint, other.Fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static string Safe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(char.IsWhiteSpace(c) ? '_' : c);
        return sb.Length == 0 ? "level" : sb.ToString();
    }

    /// <summary>Identify a level archive on disk. The level name is the file's own base name, which is what
    /// the engine keys a level by, so two people with Saigon68.rfa agree on the name even when the bytes differ.</summary>
    public static Id Identify(string archivePath)
        => new(Fingerprint(archivePath), new FileInfo(archivePath).Length,
               Path.GetFileNameWithoutExtension(archivePath));

    /// <summary>One entry's identity inside an archive: what it is called, how big it is, and a hash of its
    /// decompressed bytes. Size alone is not enough - a repaint of a terrain tile keeps the size.</summary>
    public sealed record EntryHash(string Name, int Bytes, string Hash);

    /// <summary>Hash every entry. This decompresses the whole archive, so it is the expensive path and exists
    /// for diagnosing a mismatch, not for the join handshake.</summary>
    public static List<EntryHash> Manifest(string archivePath)
    {
        var arch = new RefractorFlatArchive(archivePath);
        var list = new List<EntryHash>(arch.Entries.Count);
        using var sha = SHA256.Create();
        foreach (var e in arch.Entries)
        {
            string hash;
            try { hash = Convert.ToHexString(sha.ComputeHash(arch.Read(e))).ToLowerInvariant()[..16]; }
            catch { hash = "unreadable"; }
            list.Add(new EntryHash(Norm(e.Name), e.UncompressedSize, hash));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private static string Norm(string n) => n.Replace('\\', '/');

    /// <summary>What differs between two archives, named file by file. Ordered biggest-difference first so the
    /// head of the list is the part worth talking about.</summary>
    public sealed record Difference(
        IReadOnlyList<EntryHash> OnlyInMine,
        IReadOnlyList<EntryHash> OnlyInTheirs,
        IReadOnlyList<(EntryHash Mine, EntryHash Theirs)> Changed)
    {
        public int Count => OnlyInMine.Count + OnlyInTheirs.Count + Changed.Count;

        /// <summary>A few lines a human can act on, capped so a wholly different archive does not print 1500 rows.</summary>
        public string Describe(int max = 12)
        {
            if (Count == 0) return "the two archives hold identical files";
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture,
                $"{Count} file(s) differ: {Changed.Count} changed, {OnlyInMine.Count} only yours, {OnlyInTheirs.Count} only the session's");
            int shown = 0;
            foreach (var (m, t) in Changed)
            {
                if (shown++ >= max) break;
                sb.Append(CultureInfo.InvariantCulture, $"\n  changed  {m.Name}  ({m.Bytes:N0} B vs {t.Bytes:N0} B)");
            }
            foreach (var m in OnlyInMine)
            {
                if (shown++ >= max) break;
                sb.Append(CultureInfo.InvariantCulture, $"\n  yours only  {m.Name}  ({m.Bytes:N0} B)");
            }
            foreach (var t in OnlyInTheirs)
            {
                if (shown++ >= max) break;
                sb.Append(CultureInfo.InvariantCulture, $"\n  session only  {t.Name}  ({t.Bytes:N0} B)");
            }
            if (Count > shown) sb.Append(CultureInfo.InvariantCulture, $"\n  ... and {Count - shown} more");
            return sb.ToString();
        }
    }

    public static Difference Compare(IReadOnlyList<EntryHash> mine, IReadOnlyList<EntryHash> theirs)
    {
        var m = mine.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var t = theirs.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var onlyMine = mine.Where(e => !t.ContainsKey(e.Name)).OrderByDescending(e => e.Bytes).ToList();
        var onlyTheirs = theirs.Where(e => !m.ContainsKey(e.Name)).OrderByDescending(e => e.Bytes).ToList();
        var changed = mine.Where(e => t.TryGetValue(e.Name, out var o)
                                      && !string.Equals(e.Hash, o.Hash, StringComparison.OrdinalIgnoreCase))
                          .Select(e => (Mine: e, Theirs: t[e.Name]))
                          .OrderByDescending(p => Math.Max(p.Mine.Bytes, p.Theirs.Bytes))
                          .ToList();
        return new Difference(onlyMine, onlyTheirs, changed);
    }

    /// <summary>Entries a joiner does NOT need shipped to them, because the editor regenerates them or they do
    /// not affect what anyone edits. Baked object lightmaps are the big one: on Saigon68 they are 172 MB of a
    /// 366 MB archive, and the relay already replays a bake as a trigger the peer re-runs for itself. Level
    /// sounds are another 71 MB that no geometry depends on.</summary>
    public static bool IsRegenerable(string entryName)
    {
        var n = Norm(entryName).ToLowerInvariant();
        return n.Contains("/objectlightmaps/")
            || n.Contains("/lightmaps/")
            || n.Contains("_backup")       // BACKUP_SAIGON_TERRAIN and friends: a stale duplicate tile set
            || n.Contains("backup_");
    }

    /// <summary>Bytes in an archive that a joiner actually needs, and the total. Reported so the size of a
    /// transfer is a stated number rather than a surprise.</summary>
    public static (long Needed, long Total) TransferSize(string archivePath)
    {
        var arch = new RefractorFlatArchive(archivePath);
        long needed = 0, total = 0;
        foreach (var e in arch.Entries)
        {
            total += e.UncompressedSize;
            if (!IsRegenerable(e.Name)) needed += e.UncompressedSize;
        }
        return (needed, total);
    }
}
