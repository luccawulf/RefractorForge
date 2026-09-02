namespace RefractorForge.Archive;

/// <summary>
/// The archive as ONE expandable list of folders and files, the way BGA presents it, rather than a folder tree
/// beside a separate file pane.
///
/// It is flattened to a row array on every change because the list that draws it is virtual — a stock
/// texture.rfa is tens of thousands of entries, and the control asks for rows by index as it scrolls rather
/// than being handed objects up front.
/// </summary>
public sealed class TreeModel
{
    public sealed class Row
    {
        public required string Path { get; init; }          // full archive path, forward slashes
        public required string Display { get; init; }       // just the leaf name
        public required int Depth { get; init; }
        public required bool IsFolder { get; init; }
        public ArchiveModel.Item? Item { get; init; }       // null for folders

        // Folder aggregates, so a collapsed folder still says how much is inside it.
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public long TotalPacked { get; set; }
    }

    private sealed class Node
    {
        public required string Path;
        public required string Name;
        public readonly SortedDictionary<string, Node> Children =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ArchiveModel.Item> Files = new();
        public int FileCount;
        public long TotalSize, TotalPacked;
    }

    private Node _root = new() { Path = "", Name = "" };
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private List<Row> _rows = new();
    private int _sortColumn = -1;
    private bool _sortDescending;

    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>
    /// Which column orders siblings. The sort deliberately applies WITHIN each folder rather than across the
    /// whole archive: flattening the tree to sort it would throw away the structure the list exists to show,
    /// and "the biggest file in this folder" is the question people actually ask.
    /// </summary>
    public void SetSort(int column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
    }

    private IEnumerable<ArchiveModel.Item> SortFiles(IEnumerable<ArchiveModel.Item> files)
    {
        var ordered = _sortColumn switch
        {
            1 => files.OrderBy(f => f.UncompressedSize),
            2 => files.OrderBy(f => f.BlockSize),
            3 => files.OrderBy(f => f.UncompressedSize > 0 ? (double)f.BlockSize / f.UncompressedSize : 0.0),
            4 => files.OrderBy(f => f.Offset),
            5 => files.OrderBy(f => f.State.ToString(), StringComparer.OrdinalIgnoreCase),
            6 => files.OrderBy(f => f.Source ?? "", StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase),
        };
        return _sortDescending ? ordered.Reverse() : ordered;
    }

    /// <summary>Rebuild from the archive. <paramref name="filter"/> non-empty switches to a flat, filtered
    /// view: while searching, folders are noise — you want the matches.</summary>
    public void Build(IEnumerable<ArchiveModel.Item> items, string filter)
    {
        _root = new Node { Path = "", Name = "" };
        var live = items.Where(i => i.State != ArchiveModel.EntryState.Deleted);

        if (filter.Length > 0)
        {
            var matches = live.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            _rows = (_sortColumn <= 0
                    ? (_sortDescending
                        ? matches.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
                        : matches.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                    : SortFiles(matches))
                .Select(i => new Row
                {
                    Path = i.Name, Display = i.Name, Depth = 0, IsFolder = false, Item = i,
                })
                .ToList();
            return;
        }

        foreach (var item in live)
        {
            var node = _root;
            var parts = item.Folder.Length == 0
                ? Array.Empty<string>()
                : item.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);

            string acc = string.Empty;
            foreach (var part in parts)
            {
                acc = acc.Length == 0 ? part : acc + "/" + part;
                if (!node.Children.TryGetValue(part, out var child))
                {
                    child = new Node { Path = acc, Name = part };
                    node.Children[part] = child;
                }
                node = child;
            }
            node.Files.Add(item);
        }

        Aggregate(_root);
        _rows = new List<Row>();
        Flatten(_root, -1);          // -1 so the root's own children come out at depth 0
    }

    /// <summary>Roll file counts and sizes up the tree so a collapsed folder can still report its contents.</summary>
    private static void Aggregate(Node n)
    {
        n.FileCount = n.Files.Count;
        n.TotalSize = n.Files.Sum(f => (long)f.UncompressedSize);
        n.TotalPacked = n.Files.Sum(f => (long)f.BlockSize);
        foreach (var c in n.Children.Values)
        {
            Aggregate(c);
            n.FileCount += c.FileCount;
            n.TotalSize += c.TotalSize;
            n.TotalPacked += c.TotalPacked;
        }
    }

    private void Flatten(Node n, int depth)
    {
        if (depth >= 0)
        {
            _rows.Add(new Row
            {
                Path = n.Path, Display = n.Name, Depth = depth, IsFolder = true,
                FileCount = n.FileCount, TotalSize = n.TotalSize, TotalPacked = n.TotalPacked,
            });
            if (!IsExpanded(n.Path)) return;      // collapsed: nothing below it is drawn
        }

        foreach (var c in n.Children.Values)
            Flatten(c, depth + 1);

        // Files after subfolders, which is the order every file manager uses.
        foreach (var f in SortFiles(n.Files))
            _rows.Add(new Row
            {
                Path = f.Name, Display = f.FileName, Depth = depth + 1, IsFolder = false, Item = f,
            });
    }

    public bool IsExpanded(string path) => _expanded.Contains(path);

    public void Toggle(string path)
    {
        if (!_expanded.Remove(path)) _expanded.Add(path);
    }

    public void SetExpanded(string path, bool on)
    {
        if (on) _expanded.Add(path); else _expanded.Remove(path);
    }

    public void ExpandAll(IEnumerable<ArchiveModel.Item> items)
    {
        foreach (var i in items)
        {
            string acc = string.Empty;
            foreach (var part in i.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = acc.Length == 0 ? part : acc + "/" + part;
                _expanded.Add(acc);
            }
        }
    }

    public void CollapseAll() => _expanded.Clear();

    /// <summary>
    /// Open just the top level, so a freshly-opened archive shows its shape without a wall of rows.
    ///
    /// Takes the items rather than reading the built tree: the caller naturally sets the expansion it wants and
    /// then builds, and depending on the internal tree here meant doing it in that order silently did nothing.
    /// </summary>
    public void ExpandTopLevel(IEnumerable<ArchiveModel.Item> items)
    {
        _expanded.Clear();
        foreach (var i in items)
        {
            if (i.State == ArchiveModel.EntryState.Deleted) continue;
            var folder = i.Folder;
            if (folder.Length == 0) continue;
            int slash = folder.IndexOf('/');
            _expanded.Add(slash < 0 ? folder : folder[..slash]);
        }
    }

    /// <summary>Expand every ancestor of a path, used to reveal a row that search found.</summary>
    public void RevealPath(string path)
    {
        int i = path.LastIndexOf('/');
        if (i < 0) return;
        string acc = string.Empty;
        foreach (var part in path[..i].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc.Length == 0 ? part : acc + "/" + part;
            _expanded.Add(acc);
        }
    }
}
