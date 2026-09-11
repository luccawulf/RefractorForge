using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Collab;

/// <summary>
/// The maps a central relay hosts, one folder each under a maps directory.
///
/// A <see cref="RelayServer"/> already is a room: it owns one canonical document, one sequence counter and one
/// base-archive pin. So hosting several maps is not a rewrite of it but a directory of them, all behind the one
/// port, with a client choosing which to enter before it is registered anywhere. That keeps every rule the
/// single-map relay already enforces - ordering, seeding, the base pin - true per map, for free.
///
/// Layout, so it is readable and repairable by hand:
/// <code>
///   maps/
///     Saigon68/            <- a map. Its name is its folder name.
///       StaticObjects.con  <- the session, exactly as a single-map relay saves it
///       gameplay.sync
///       _backups/&lt;stamp&gt;/
///     Hue/
/// </code>
/// The base archives stay in one content-addressed store shared by every map, because two maps built on the
/// same archive should not cost two copies of it.
/// </summary>
public sealed class MapLibrary
{
    private readonly string _dir;
    private readonly string? _password;
    private readonly BaseArchiveStore? _store;
    private readonly int _keepBackups, _backupMaxMb;
    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public sealed class Room
    {
        public required string Name { get; init; }
        public required string Folder { get; init; }
        public required RelayServer Relay { get; init; }
        public long SavedSeq { get; set; } = -1;
        public long BackupSeq { get; set; } = -1;
        public DateTime NextBackup { get; set; } = DateTime.Now;
    }

    public MapLibrary(string dir, string? password, BaseArchiveStore? store, int keepBackups = 12, int backupMaxMb = 512)
    {
        _dir = dir;
        _password = password;
        _store = store;
        _keepBackups = keepBackups;
        _backupMaxMb = backupMaxMb;
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    /// <summary>The password guards the relay, not a map: it is checked before a client is told what maps
    /// exist, because the list of what a group is working on is itself worth not handing out.</summary>
    public bool RequiresAuth => _password is not null;
    public bool CheckAuth(string? supplied) => _password is null || supplied == _password;

    /// <summary>A map name has to be safe as a folder name and as a single wire token, because it is both.</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
        && name != "." && name != ".."
        && !name.StartsWith('.');

    /// <summary>What the maps directory holds right now, freshly from disk so a folder dropped in by hand shows
    /// up without restarting the relay.</summary>
    public List<Entry> List()
    {
        var seen = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_dir))
            {
                var name = Path.GetFileName(d);
                if (!IsValidName(name)) continue;
                seen[name] = Describe(name, d);
            }
            // A level archive dropped straight into the maps folder is a map too. Copying one file in is the
            // obvious thing to do with a map, so it has to be the thing that works; the session folder beside it
            // is built the first time someone enters it.
            foreach (var f in Directory.EnumerateFiles(_dir, "*.rfa"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!IsValidName(name) || seen.ContainsKey(name)) continue;
                seen[name] = new Entry(name, 0, true, Updated(f), 0);
            }
        }
        catch { }
        // A room created this run but not yet written to disk still belongs in the list.
        lock (_gate)
            foreach (var r in _rooms.Values)
                if (!seen.ContainsKey(r.Name)) seen[r.Name] = Describe(r.Name, r.Folder);
        return seen.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One line of the map list: enough for a person to choose without opening anything.</summary>
    public readonly record struct Entry(string Name, int Objects, bool HasBase, long UpdatedUnix, int Clients);

    private Entry Describe(string name, string folder)
    {
        int objects = 0;
        bool hasBase = false;
        long updated = 0;
        lock (_gate)
        {
            if (_rooms.TryGetValue(name, out var live))
            {
                objects = live.Relay.SnapshotDoc().Objects.Count;
                hasBase = live.Relay.BasePin is not null;
                return new Entry(name, objects, hasBase, Updated(folder), live.Relay.ClientCount);
            }
        }
        // Not loaded: read what the folder says without standing a whole relay up for a listing.
        try
        {
            var soc = Path.Combine(folder, "StaticObjects.con");
            if (File.Exists(soc))
            {
                foreach (var line in File.ReadLines(soc))
                    if (line.StartsWith("object.create", StringComparison.OrdinalIgnoreCase)) objects++;
            }
            hasBase = File.Exists(Path.Combine(folder, "base.txt"));
            updated = Updated(folder);
        }
        catch { }
        return new Entry(name, objects, hasBase, updated, 0);
    }

    private static long Updated(string folder)
    {
        try
        {
            var t = Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder)
                  : File.Exists(folder) ? File.GetLastWriteTimeUtc(folder)
                  : DateTime.UnixEpoch;
            return (long)(t - DateTime.UnixEpoch).TotalSeconds;
        }
        catch { return 0; }
    }

    /// <summary>Enter a map, standing its relay up from disk on first use. Returns null when the name is not a
    /// legal map name; an unknown but legal name creates the map, which is how a new one gets started.</summary>
    public Room? Open(string name)
    {
        if (!IsValidName(name)) return null;
        lock (_gate)
        {
            if (_rooms.TryGetValue(name, out var existing)) return existing;

            string folder = Path.Combine(_dir, name);
            Directory.CreateDirectory(folder);

            StaticObjectsFile? objects = null;
            CollabWorldState? world = null;
            LevelBase.Id? pin = null;
            try
            {
                world = CollabWorldState.Load(folder);
                var soc = Path.Combine(folder, "StaticObjects.con");
                if (File.Exists(soc)) objects = RelayHost.LoadObjects(soc);
                var basetxt = Path.Combine(folder, "base.txt");
                if (File.Exists(basetxt)) pin = LevelBase.Id.TryDecode(File.ReadAllText(basetxt).Trim());
            }
            catch { }

            // No session yet, but an archive of that name is sitting in the maps folder: that archive IS the map.
            // Read it for the starting objects, terrain, materials, growth and gameplay, and pin it as the base so
            // the first person to join is given the file rather than asked to go and find it. One dropped-in .rfa
            // becomes a whole working map, which is the only sane thing for it to mean.
            var dropped = FindDroppedArchive(name);
            if (objects is null && dropped is not null)
            {
                try
                {
                    var (o, w) = RelayHost.LoadFullLevel(dropped);
                    if (o is not null) objects = o;
                    if (w is not null) world = w;
                    if (_store is not null)
                    {
                        string fp = _store.Ingest(dropped);
                        pin = new LevelBase.Id(fp, new FileInfo(dropped).Length, name);
                        File.WriteAllText(Path.Combine(folder, "base.txt"), pin.Encode());
                    }
                    Console.WriteLine($"  [{name}] seeded from {Path.GetFileName(dropped)}: {objects?.Objects.Count ?? 0} objects"
                                      + (pin is null ? " (no base store, so it cannot be handed to joiners)" : $", archive pinned {pin.Short}"));
                }
                catch (Exception ex) { Console.WriteLine($"  [{name}] could not read {Path.GetFileName(dropped)}: {ex.Message}"); }
            }

            var room = new Room
            {
                Name = name,
                Folder = folder,
                Relay = new RelayServer(objects, world ?? new CollabWorldState(), _password, _store, pin, new MapStore(folder)),
            };
            // The relay knows the edits; only the library knows where the base lives and what the map is called.
            room.Relay.BuildExport = () =>
            {
                if (_store is null) return null;
                var id = MapExport.Build(room, _store, Path.Combine(room.Folder, "current.rfa"));
                return id is null ? null : (id, Path.Combine(room.Folder, "current.rfa"));
            };
            // What a never-synced editor compares itself against: the pinned archive, as it shipped.
            room.Relay.BuildBaseline = () => _store is null ? null : MapExport.Baseline(room, _store);
            _rooms[name] = room;
            return room;
        }
    }

    /// <summary>An archive dropped into the maps folder for this map, if there is one. Accepted both as
    /// <c>maps/&lt;Name&gt;.rfa</c> and as <c>maps/&lt;Name&gt;/&lt;anything&gt;.rfa</c>, because both are things a person
    /// plausibly does with a map file and neither should be a silent no-op.</summary>
    private string? FindDroppedArchive(string name)
    {
        try
        {
            var flat = Path.Combine(_dir, name + ".rfa");
            if (File.Exists(flat)) return flat;
            var inside = Path.Combine(_dir, name);
            if (Directory.Exists(inside))
                return Directory.EnumerateFiles(inside, "*.rfa")
                                .FirstOrDefault(f => !Path.GetFileName(f).Equals("current.rfa", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return null;
    }

    /// <summary>Persist every map whose state has moved, and take backups on their own cadence. Called from the
    /// host's service loop; returns how many clients are mid-transfer so the loop can spin faster for them.</summary>
    public int Tick(int backupMinutes)
    {
        List<Room> rooms;
        lock (_gate) rooms = _rooms.Values.ToList();
        int sending = 0;
        foreach (var r in rooms)
        {
            sending += r.Relay.PumpBaseSends();
            long seq = r.Relay.Sequence;
            if (seq == r.SavedSeq) continue;
            Save(r);
            r.SavedSeq = seq;
            if (seq != r.BackupSeq && DateTime.Now >= r.NextBackup)
            {
                RelayHost.BackupState(r.Folder, _keepBackups, _backupMaxMb);
                r.BackupSeq = seq;
                r.NextBackup = DateTime.Now.AddMinutes(backupMinutes);
            }
        }
        return sending;
    }

    /// <summary>Write one map's session out. The base pin is stored beside it so the map still knows which
    /// archive it belongs to after a restart, even before anyone connects.</summary>
    public void Save(Room r)
    {
        try
        {
            r.Relay.SaveState(r.Folder);
            var pin = r.Relay.BasePin;
            if (pin is not null) File.WriteAllText(Path.Combine(r.Folder, "base.txt"), pin.Encode());
        }
        catch { }
    }

    /// <summary>Flush everything, for shutdown.</summary>
    public void SaveAll()
    {
        List<Room> rooms;
        lock (_gate) rooms = _rooms.Values.ToList();
        foreach (var r in rooms) Save(r);
    }

    public int RoomCount { get { lock (_gate) return _rooms.Count; } }

    public IReadOnlyList<Room> LoadedRooms { get { lock (_gate) return _rooms.Values.ToList(); } }
}
