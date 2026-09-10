using RefractorForge.Formats.Rfa;

namespace RefractorForge.Collab;

/// <summary>
/// Where the relay keeps the level archives it can hand out. Content-addressed: a file is named by the
/// fingerprint of its own bytes, so the same archive uploaded twice is stored once, an upload can never
/// overwrite a different archive, and "do you have this?" is a file-exists check rather than a comparison.
///
/// A store is a plain folder. On a server whose system disk is nearly full that folder belongs on the roomy
/// one, which is why the path is a setting rather than a fixed location next to the session.
///
/// Uploads land in a <c>.part</c> file and are only promoted once the received bytes hash to the fingerprint
/// they claimed. A transfer that is truncated, reordered or corrupted therefore leaves nothing behind that a
/// later joiner could be given.
/// </summary>
public sealed class BaseArchiveStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Dictionary<string, Incoming> _incoming = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw bytes per chunk before base64. Base64 inflates by a third, so a chunk is about 170 KB on
    /// the wire: big enough that a 300 MB archive is a couple of thousand lines, small enough that the ops of
    /// a live editing session are not stuck behind one of them for long.</summary>
    public const int ChunkBytes = 128 * 1024;

    public BaseArchiveStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    private string PathFor(string fingerprint) => Path.Combine(_dir, Sanitise(fingerprint) + ".rfa");

    /// <summary>A fingerprint reaches this from the network, so it is never trusted as a path component.</summary>
    private static string Sanitise(string fp)
    {
        var clean = new string(fp.Where(char.IsAsciiLetterOrDigit).Take(64).ToArray());
        return clean.Length == 0 ? "invalid" : clean.ToLowerInvariant();
    }

    public bool Has(string fingerprint) => File.Exists(PathFor(fingerprint));

    public long SizeOf(string fingerprint)
    {
        var p = PathFor(fingerprint);
        return File.Exists(p) ? new FileInfo(p).Length : 0;
    }

    /// <summary>Take a copy of an archive already on this machine (the seed path, say) into the store.</summary>
    public string Ingest(string archivePath)
    {
        string fp = LevelBase.Fingerprint(archivePath);
        var dest = PathFor(fp);
        if (!File.Exists(dest)) File.Copy(archivePath, dest, overwrite: false);
        return fp;
    }

    /// <summary>Read one chunk. Returns an empty array once past the end, which is how a sender knows to stop.</summary>
    public byte[] ReadChunk(string fingerprint, int index) => ReadChunkOf(PathFor(fingerprint), index);

    /// <summary>The same, for a file that is not in the store - an exported archive built for one client.</summary>
    public static byte[] ReadChunkOf(string p, int index)
    {
        if (!File.Exists(p)) return Array.Empty<byte>();
        long offset = (long)index * ChunkBytes;
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset >= fs.Length) return Array.Empty<byte>();
        fs.Position = offset;
        int want = (int)Math.Min(ChunkBytes, fs.Length - offset);
        var buf = new byte[want];
        int got = 0;
        while (got < want)
        {
            int n = fs.Read(buf, got, want - got);
            if (n <= 0) break;
            got += n;
        }
        return got == want ? buf : buf[..got];
    }

    public int ChunkCount(string fingerprint)
    {
        long len = SizeOf(fingerprint);
        return len == 0 ? 0 : (int)((len + ChunkBytes - 1) / ChunkBytes);
    }

    private sealed class Incoming : IDisposable
    {
        public required FileStream File { get; init; }
        public required string PartPath { get; init; }
        public int NextIndex;
        public void Dispose() { try { File.Dispose(); } catch { } }
    }

    /// <summary>Begin receiving. A second Begin for the same fingerprint restarts it, so a client that dropped
    /// mid-transfer and reconnected does not append to its own stale half.</summary>
    public void BeginReceive(string fingerprint)
    {
        lock (_gate)
        {
            if (_incoming.Remove(fingerprint, out var old)) { old.Dispose(); TryDelete(old.PartPath); }
            var part = PathFor(fingerprint) + ".part";
            TryDelete(part);
            _incoming[fingerprint] = new Incoming
            {
                File = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20),
                PartPath = part,
            };
        }
    }

    /// <summary>Accept one chunk. False means the chunk was out of order or unexpected and the transfer has
    /// been abandoned - the caller should stop rather than keep feeding a file that can never verify.</summary>
    public bool ReceiveChunk(string fingerprint, int index, byte[] data)
    {
        lock (_gate)
        {
            if (!_incoming.TryGetValue(fingerprint, out var inc)) return false;
            if (index != inc.NextIndex) { AbortLocked(fingerprint); return false; }
            inc.File.Write(data, 0, data.Length);
            inc.NextIndex++;
            return true;
        }
    }

    /// <summary>Finish. The part file is hashed and only promoted if it really is the archive it claimed to be,
    /// so a truncated or tampered transfer leaves nothing a later joiner could be handed.</summary>
    public bool CompleteReceive(string fingerprint)
    {
        lock (_gate)
        {
            if (!_incoming.TryGetValue(fingerprint, out var inc)) return false;
            inc.File.Flush(); inc.File.Dispose();
            _incoming.Remove(fingerprint);
            try
            {
                string actual = LevelBase.Fingerprint(inc.PartPath);
                if (!string.Equals(actual, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(inc.PartPath);
                    return false;
                }
                var dest = PathFor(fingerprint);
                if (File.Exists(dest)) TryDelete(inc.PartPath);      // someone else finished first; keep theirs
                else File.Move(inc.PartPath, dest);
                return true;
            }
            catch { TryDelete(inc.PartPath); return false; }
        }
    }

    public void Abort(string fingerprint) { lock (_gate) AbortLocked(fingerprint); }

    private void AbortLocked(string fingerprint)
    {
        if (!_incoming.Remove(fingerprint, out var inc)) return;
        inc.Dispose();
        TryDelete(inc.PartPath);
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    /// <summary>Total bytes held, so an operator can see what the store is costing without measuring by hand.</summary>
    public long TotalBytes()
    {
        try { return new DirectoryInfo(_dir).EnumerateFiles("*.rfa").Sum(f => f.Length); }
        catch { return 0; }
    }
}
