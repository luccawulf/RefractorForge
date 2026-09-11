using System.Globalization;
using System.Text;

namespace RefractorForge.Collab;

/// <summary>
/// What a server map keeps beyond its objects and terrain: its version counter and incarnation, the history of who
/// changed what, and the level files people have sent it. All of it lives in the map's folder so it survives the
/// server restarting - a version number that went back to zero on every restart would make every editor's record
/// of "the version I last synced to" meaningless.
/// </summary>
public sealed class MapStore
{
    private readonly string _dir;
    private readonly object _gate = new();

    public string Folder => _dir;
    public string Epoch { get; }
    public ChangeJournal Journal { get; }
    public FileStore Files { get; }

    public MapStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        var ep = Path.Combine(dir, "epoch.txt");
        string? epoch = null;
        try { if (File.Exists(ep)) epoch = File.ReadAllText(ep).Trim(); } catch { }
        if (string.IsNullOrEmpty(epoch))
        {
            epoch = Guid.NewGuid().ToString("N")[..16];
            try { File.WriteAllText(ep, epoch); } catch { }
        }
        Epoch = epoch;
        Journal = new ChangeJournal(Path.Combine(dir, "journal.log"));
        Files = new FileStore(Path.Combine(dir, "files"));
    }

    /// <summary>The version the map had reached when it was last saved; 0 for a new map.</summary>
    public long LoadSeq()
    {
        try
        {
            var p = Path.Combine(_dir, "seq.txt");
            if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                return Math.Max(s, Journal.LastSeq);
        }
        catch { }
        return Journal.LastSeq;
    }

    public void SaveSeq(long seq)
    {
        lock (_gate)
            try { File.WriteAllText(Path.Combine(_dir, "seq.txt"), seq.ToString(CultureInfo.InvariantCulture)); } catch { }
    }
}

/// <summary>
/// The level files a server map holds: terrain tiles, lighting bakes, sounds, decals, Init.con and anything else an
/// editor wrote into the level that is not rebuilt from the objects and terrain. Kept on disk, one real file per
/// level file under the map's <c>files/</c> folder, with only an index in memory - a map's lightmaps alone run to
/// a hundred MB and more, which has no business sitting in a server's memory.
/// </summary>
public sealed class FileStore
{
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _index = new(StringComparer.OrdinalIgnoreCase);

    public readonly record struct Entry(string Hash, long Size, long Seq);

    public FileStore(string dir)
    {
        _dir = dir;
        _indexPath = Path.Combine(dir, "index.log");
        Directory.CreateDirectory(dir);
        Load();
    }

    public int Count { get { lock (_gate) return _index.Count; } }

    /// <summary>Every file the map holds, with its hash, size and the version it was last written at.</summary>
    public List<(string Path, Entry Entry)> All()
    {
        lock (_gate) return _index.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool TryGet(string relPath, out Entry e)
    {
        lock (_gate) return _index.TryGetValue(SyncKeys.NormPath(relPath), out e);
    }

    /// <summary>Store a file sent by an editor. The path came off the network, so it is checked before it is
    /// allowed anywhere near the disk: a name that climbs out of the folder is refused outright.</summary>
    public bool Put(string relPath, byte[] bytes, long seq)
    {
        var rel = SyncKeys.NormPath(relPath);
        var disk = DiskPath(rel);
        if (disk is null) return false;
        var hash = SyncKeys.Hash(bytes);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                var tmp = disk + ".part";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, disk, overwrite: true);
                _index[rel] = new Entry(hash, bytes.LongLength, seq);
                File.AppendAllText(_indexPath, $"{hash} {bytes.LongLength} {seq} {SyncKeys.EscapePath(rel)}\n");
                return true;
            }
            catch { return false; }
        }
    }

    public byte[]? Read(string relPath)
    {
        var rel = SyncKeys.NormPath(relPath);
        lock (_gate) if (!_index.ContainsKey(rel)) return null;
        var disk = DiskPath(rel);
        try { return disk is not null && File.Exists(disk) ? File.ReadAllBytes(disk) : null; }
        catch { return null; }
    }

    /// <summary>Where a level file lives on disk, or null for a path that is not a plain relative path. Only
    /// letters, digits and a few punctuation marks per segment, no "." or ".." segments, no drive or root.</summary>
    public string? DiskPath(string rel)
    {
        if (!IsSafeRelative(rel)) return null;
        var full = Path.GetFullPath(Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(_dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static bool IsSafeRelative(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Length > 260) return false;
        if (rel.Contains(':') || rel.StartsWith('/') || rel.Contains('\\')) return false;
        foreach (var seg in rel.Split('/'))
        {
            if (seg.Length == 0 || seg == "." || seg == "..") return false;
            if (seg.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var c in seg)
                if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ' or '(' or ')' or '+' or '&' or '#' or '@' or '!' or '\''))
                    return false;
        }
        return !rel.Equals("index.log", StringComparison.OrdinalIgnoreCase);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            foreach (var line in File.ReadLines(_indexPath))
            {
                var p = line.Split(' ', 4);
                if (p.Length < 4) continue;
                if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) continue;
                if (!long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)) continue;
                var rel = SyncKeys.UnescapePath(p[3]);
                if (DiskPath(rel) is { } d && File.Exists(d)) _index[rel] = new Entry(p[0], size, seq);
            }
            // Compact: one line per file, the latest. An index that only ever grows would, over months of saves,
            // be read in full at every start for the sake of the last line of each file.
            var sb = new StringBuilder();
            foreach (var kv in _index) sb.Append($"{kv.Value.Hash} {kv.Value.Size} {kv.Value.Seq} {SyncKeys.EscapePath(kv.Key)}\n");
            File.WriteAllText(_indexPath, sb.ToString());
        }
        catch { }
    }
}

/// <summary>
/// Who changed what, and when: one line per edit, appended as the edits arrive. It is what lets an editor say
/// "34 changes by Bob since you were last here, 2 hours ago" instead of only "the map is different". Only the
/// kind of each change is kept in memory, never the change itself.
/// </summary>
public sealed class ChangeJournal
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly List<Row> _rows = new();
    private readonly List<string> _authors = new();
    private readonly Dictionary<string, int> _authorIndex = new(StringComparer.Ordinal);

    private readonly record struct Row(long Seq, long Unix, int Author, int Kinds);

    /// <summary>The kinds a change can be, as bits - the same words <see cref="SyncKeys.Group"/> uses.</summary>
    public static readonly string[] Kinds =
    {
        "objects", "terrain", "materials", "foliage", "gameplay", "water", "lighting", "overgrowth", "notes",
        "imported objects", "ground texture", "lighting bakes", "level files", "other",
    };

    public ChangeJournal(string? path)
    {
        _path = path;
        Load();
    }

    public long LastSeq { get { lock (_gate) return _rows.Count == 0 ? 0 : _rows[^1].Seq; } }
    public long FirstSeq { get { lock (_gate) return _rows.Count == 0 ? 0 : _rows[0].Seq; } }
    public long LastUnix { get { lock (_gate) return _rows.Count == 0 ? 0 : _rows[^1].Unix; } }
    public string LastAuthor { get { lock (_gate) return _rows.Count == 0 ? "" : _authors[_rows[^1].Author]; } }

    public void Add(long seq, long unix, string author, IEnumerable<string> keys)
    {
        int kinds = 0;
        foreach (var k in keys)
        {
            int i = Array.IndexOf(Kinds, SyncKeys.Group(k));
            kinds |= 1 << (i < 0 ? Kinds.Length - 1 : i);
        }
        if (kinds == 0) return;                       // presence and the like: not a change to the map
        author = string.IsNullOrWhiteSpace(author) ? "someone" : author.Trim();
        lock (_gate)
        {
            _rows.Add(new Row(seq, unix, AuthorId(author), kinds));
            if (_path is null) return;
            try { File.AppendAllText(_path, $"{seq} {unix} {kinds} {Uri.EscapeDataString(author)}\n"); } catch { }
        }
    }

    /// <summary>One person's share of the changes since a version.</summary>
    public readonly record struct Contribution(string Author, int Count, long LastUnix, IReadOnlyList<string> Kinds);

    /// <summary>Who changed what after <paramref name="sinceSeq"/>, most recent first. <paramref name="complete"/>
    /// is false when the history no longer reaches that far back (a map older than its journal).</summary>
    public List<Contribution> Since(long sinceSeq, out bool complete)
    {
        var by = new Dictionary<int, (int N, long Last, int Kinds)>();
        lock (_gate)
        {
            complete = _rows.Count == 0 || _rows[0].Seq <= sinceSeq + 1;
            for (int i = _rows.Count - 1; i >= 0 && _rows[i].Seq > sinceSeq; i--)
            {
                var r = _rows[i];
                by.TryGetValue(r.Author, out var a);
                by[r.Author] = (a.N + 1, Math.Max(a.Last, r.Unix), a.Kinds | r.Kinds);
            }
            return by.Select(kv => new Contribution(_authors[kv.Key], kv.Value.N, kv.Value.Last, KindNames(kv.Value.Kinds)))
                     .OrderByDescending(c => c.LastUnix).ToList();
        }
    }

    public static IReadOnlyList<string> KindNames(int bits)
    {
        var list = new List<string>();
        for (int i = 0; i < Kinds.Length; i++) if ((bits & (1 << i)) != 0) list.Add(Kinds[i]);
        return list;
    }

    /// <summary>The wire payload for a CHANGES answer: "count TAB lastUnix TAB kinds TAB author" per person.</summary>
    public static string Encode(IEnumerable<Contribution> list)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n",
               list.Select(c => $"{c.Count}\t{c.LastUnix}\t{string.Join(",", c.Kinds)}\t{c.Author}"))));

    public static List<Contribution> Decode(string b64)
    {
        var list = new List<Contribution>();
        try
        {
            foreach (var line in Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('\n'))
            {
                var p = line.Split('\t', 4);
                if (p.Length < 4) continue;
                list.Add(new Contribution(p[3], int.Parse(p[0], CultureInfo.InvariantCulture),
                                          long.Parse(p[1], CultureInfo.InvariantCulture),
                                          p[2].Length == 0 ? Array.Empty<string>() : p[2].Split(',')));
            }
        }
        catch { }
        return list;
    }

    private int AuthorId(string a)
    {
        if (_authorIndex.TryGetValue(a, out var i)) return i;
        _authors.Add(a);
        return _authorIndex[a] = _authors.Count - 1;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                var p = line.Split(' ', 4);
                if (p.Length < 4) continue;
                if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)) continue;
                if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) continue;
                if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kinds)) continue;
                _rows.Add(new Row(seq, unix, AuthorId(Uri.UnescapeDataString(p[3])), kinds));
            }
            _rows.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        }
        catch { }
    }
}
