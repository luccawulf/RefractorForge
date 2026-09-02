using RefractorForge.Formats.Rfa;

namespace RefractorForge.Archive;

/// <summary>
/// The open archive plus whatever the user has changed but not yet saved.
///
/// Edits are held here as a pending overlay rather than written into the file as they are made. That is a
/// deliberate departure from how BGA works: BGA edits the archive in place and recovers the wasted space with a
/// separate "defrag" step, so an interrupted edit leaves the real file in whatever state it reached. Here nothing
/// touches the archive on disk until Save, and Save streams a fresh copy through
/// <see cref="RefractorFlatArchive.RepackToFile"/>, which writes a sibling temp file and only then replaces the
/// original — so a crash mid-save costs you the save, not the archive.
///
/// The same model also fronts a whole-mod <see cref="ModWorkspace"/>: every archive the mod mounts, merged the
/// way the game merges them, read-only. Each item then knows which archive it came from and how many lower
/// layers it shadows.
/// </summary>
public sealed class ArchiveModel : IDisposable
{
    /// <summary>What an entry has had done to it, if anything.</summary>
    public enum EntryState { Unchanged, Replaced, Added, Deleted }

    public sealed class Item
    {
        public required string Name { get; init; }              // full archive path, forward slashes
        public int UncompressedSize { get; set; }
        public int BlockSize { get; set; }                      // on-disk region; == uncompressed when stored raw
        public uint Offset { get; set; }                        // where the region starts in the file
        public EntryState State { get; set; } = EntryState.Unchanged;
        public byte[]? Pending { get; set; }                    // replacement/new bytes, held until Save

        // Workspace view only: where this winning copy lives and what it overrides.
        public string? Source { get; init; }                    // archive file name
        public string? SourceMod { get; init; }
        public int LayerIndex { get; init; } = -1;
        public int Overrides { get; init; }                     // lower layers that also ship this file
        public Func<byte[]>? Reader { get; init; }

        public bool IsCompressed => BlockSize != UncompressedSize;
        public string Folder
        {
            get { int i = Name.LastIndexOf('/'); return i < 0 ? string.Empty : Name[..i]; }
        }
        public string FileName
        {
            get { int i = Name.LastIndexOf('/'); return i < 0 ? Name : Name[(i + 1)..]; }
        }
    }

    private RefractorFlatArchive? _archive;
    private ModWorkspace? _workspace;
    private readonly List<Item> _items = new();

    public string? Path { get; private set; }
    /// <summary>For a workspace: the mod folder it was built from.</summary>
    public string? WorkspaceLabel { get; private set; }
    public bool IsOpen => _archive is not null || _workspace is not null;
    public bool IsWorkspace => _workspace is not null;
    public ModWorkspace? Workspace => _workspace;
    public bool IsV11Format => _archive?.IsV11Format ?? false;
    public bool IsCompressed => _archive?.IsCompressed ?? true;
    public XPackId XPackId => _archive?.XPackId ?? XPackId.Default;
    public RefractorFlatArchive? Archive => _archive;

    public IReadOnlyList<Item> Items => _items;

    /// <summary>Workspace layers the user has switched off; their files are hidden from the list.</summary>
    public HashSet<int> HiddenLayers { get; } = new();

    /// <summary>True when there is something worth saving.</summary>
    public bool IsDirty => _items.Any(i => i.State != EntryState.Unchanged);

    public void Open(string path)
    {
        Close();
        var a = new RefractorFlatArchive(path);
        _archive = a;
        Path = path;
        foreach (var e in a.Entries)
            _items.Add(new Item
            {
                Name = e.Name.Replace('\\', '/'),
                UncompressedSize = e.UncompressedSize,
                BlockSize = e.BlockSize,
                Offset = e.Offset,
            });
    }

    /// <summary>Show a whole mod as the one file system the game sees. Read-only.</summary>
    public void OpenWorkspace(ModWorkspace ws, string label)
    {
        Close();
        _workspace = ws;
        WorkspaceLabel = label;
        foreach (var f in ws.Files)
        {
            var layer = ws.Layers[f.LayerIndex];
            var file = f;
            _items.Add(new Item
            {
                Name = f.Name,
                UncompressedSize = f.Entry.UncompressedSize,
                BlockSize = f.Entry.BlockSize,
                Offset = f.Entry.Offset,
                Source = layer.Label,
                SourceMod = layer.Mod,
                LayerIndex = f.LayerIndex,
                Overrides = f.Overridden.Count,
                Reader = () => ws.Read(file),
            });
        }
    }

    public void Close()
    {
        _archive = null;
        _workspace?.Dispose();
        _workspace = null;
        Path = null;
        WorkspaceLabel = null;
        HiddenLayers.Clear();
        _items.Clear();
    }

    /// <summary>The bytes of one entry — the pending replacement if it has one, else decoded from the archive.</summary>
    public byte[] Read(Item item)
    {
        if (item.Pending is not null) return item.Pending;
        if (item.Reader is not null) return item.Reader();
        if (_archive is null) throw new InvalidOperationException("No archive is open.");
        var entry = _archive.Entries.FirstOrDefault(e => e.Name.Replace('\\', '/') == item.Name)
                    ?? throw new InvalidOperationException($"'{item.Name}' is not in the archive.");
        return _archive.Read(entry);
    }

    public Item? Find(string name) =>
        _items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Replace(Item item, byte[] data)
    {
        RequireEditable();
        item.Pending = data;
        item.UncompressedSize = data.Length;
        if (item.State != EntryState.Added) item.State = EntryState.Replaced;
    }

    /// <summary>Add a new entry. Replaces an existing one of the same name rather than creating a duplicate —
    /// the format allows duplicate paths and the engine simply takes one of them, which is not a coin toss worth
    /// shipping into a map.</summary>
    public Item Add(string name, byte[] data)
    {
        RequireEditable();
        name = name.Replace('\\', '/').TrimStart('/');
        if (Find(name) is { } existing)
        {
            if (existing.State == EntryState.Deleted) existing.State = EntryState.Replaced;
            Replace(existing, data);
            return existing;
        }
        var item = new Item
        {
            Name = name,
            UncompressedSize = data.Length,
            BlockSize = data.Length,
            State = EntryState.Added,
            Pending = data,
        };
        _items.Add(item);
        return item;
    }

    public void Delete(Item item)
    {
        RequireEditable();
        if (item.State == EntryState.Added) _items.Remove(item);   // never existed on disk; just forget it
        else item.State = EntryState.Deleted;
    }

    public void Revert(Item item)
    {
        if (item.State == EntryState.Added) { _items.Remove(item); return; }
        item.Pending = null;
        item.State = EntryState.Unchanged;
        if (_archive is not null)
        {
            var e = _archive.Entries.FirstOrDefault(x => x.Name.Replace('\\', '/') == item.Name);
            if (e is not null) { item.UncompressedSize = e.UncompressedSize; item.BlockSize = e.BlockSize; }
        }
    }

    private void RequireEditable()
    {
        if (_workspace is not null)
            throw new InvalidOperationException("A mod view is read-only. Open the file's own archive to edit it.");
    }

    /// <summary>
    /// Write the archive out. Unchanged entries are copied region-for-region by the writer, so a save can only
    /// alter what was actually edited and retail streams stay bit-identical.
    ///
    /// Saving to the SAME path with no additions or deletions takes the repack route, which preserves the source
    /// archive's own container bytes (descriptor, per-entry TOC trailers, table tail). Those are worth keeping:
    /// a rebuilt archive that substitutes its own has been the cause of "the map crashes" reports before.
    /// </summary>
    public void Save(string path)
    {
        if (_archive is null) throw new InvalidOperationException("No archive is open.");

        bool sameShape = _items.All(i => i.State is EntryState.Unchanged or EntryState.Replaced);
        if (sameShape && string.Equals(path, Path, StringComparison.OrdinalIgnoreCase))
        {
            var replacements = _items
                .Where(i => i.State == EntryState.Replaced && i.Pending is not null)
                .ToDictionary(i => i.Name, i => i.Pending!, StringComparer.OrdinalIgnoreCase);
            RefractorFlatArchive.RepackToFile(path, _archive, replacements);
        }
        else
        {
            // Shape changed (entries added or removed) or we are writing somewhere new: build the entry list.
            var list = new List<(string Name, byte[] Data)>();
            foreach (var i in _items)
            {
                if (i.State == EntryState.Deleted) continue;
                list.Add((i.Name, Read(i)));
            }
            RefractorFlatArchive.WriteFile(path, list, _archive.IsCompressed, _archive.XPackId);
        }

        Reopen(path);
    }

    /// <summary>Build a brand-new archive from a folder tree.</summary>
    public static void PackFolder(string folder, string outPath, bool compress, XPackId xPackId,
                                  Action<int, int>? progress = null)
    {
        var root = System.IO.Path.GetFullPath(folder)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        var entries = new List<(string Name, byte[] Data)>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            string rel = System.IO.Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            entries.Add((rel, File.ReadAllBytes(files[i])));
            progress?.Invoke(i + 1, files.Count);
        }
        RefractorFlatArchive.WriteFile(outPath, entries, compress, xPackId);
    }

    private void Reopen(string path)
    {
        Close();
        Open(path);
    }

    public void Dispose() => Close();
}
