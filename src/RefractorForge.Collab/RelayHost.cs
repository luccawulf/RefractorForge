using System.Net;
using System.Net.Sockets;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Collab;

/// <summary>What a relay is started with. Parsed from a command line by <see cref="Parse"/> - the same parser serves
/// the standalone server and the editor's own <c>--relay</c> mode, so the two never drift apart.</summary>
public sealed class RelayOptions
{
    public int Port { get; set; } = 7777;
    public IPAddress Bind { get; set; } = IPAddress.Any;
    /// <summary>A level to start from: a folder, a .rfa, or a bare StaticObjects.con. Ignored when resuming.</summary>
    public string? SeedPath { get; set; }
    /// <summary>The state folder. Everything is persisted here and resumed from here on the next start.</summary>
    public string? SavePath { get; set; }
    public string? Password { get; set; }
    /// <summary>Minutes between timestamped backups, counted only across intervals in which something changed.
    /// A quiet server makes no backups at all, so the retained snapshots are always N distinct edited states.</summary>
    public int BackupMinutes { get; set; } = 5;
    /// <summary>How many timestamped backups to keep before the oldest is pruned. Together with
    /// <see cref="BackupMinutes"/> this is the whole history a team can roll back through, so on a server
    /// people work on at different times it should span days, not the default hour.</summary>
    public int KeepBackups { get; set; } = 12;
    /// <summary>Total megabytes the <c>_backups/</c> folder may occupy. Whichever bites first, this or
    /// <see cref="KeepBackups"/>, decides how many snapshots survive. It exists because the count alone is not a
    /// bound on disk: a session holding only objects is a couple of hundred KB, but one that has had terrain and
    /// material maps synced into it is megabytes, and the same retention then means gigabytes.</summary>
    public int BackupMaxMb { get; set; } = 512;
    /// <summary>Folder holding the level archives the relay can hand to a joiner who does not have the map.
    /// Content-addressed, so it is safe to share between sessions. Without it the relay can still tell people
    /// their archive is the wrong one, but cannot give them the right one. Put it on the roomy disk: a level
    /// archive is hundreds of MB, and the machine a relay runs on is usually not chosen for its disk.</summary>
    public string? BaseStorePath { get; set; }
    /// <summary>A directory of maps, one folder each. With it the relay hosts many maps behind the one port and
    /// a client picks which to work on after connecting; without it the relay hosts the single session in
    /// <see cref="SavePath"/>, exactly as before.</summary>
    public string? MapsPath { get; set; }
    public bool Help { get; set; }

    public const string Usage =
        "usage: RefractorForge.Server [port] [seed level] [--save <state folder>] [--pass <password>] [--bind <address>]\n" +
        "\n" +
        "  port          TCP port to listen on (default 7777). Open it in the machine's firewall; nobody joining\n" +
        "                needs to forward anything - every editor connects OUT to this server.\n" +
        "  seed level    a level folder, .rfa or StaticObjects.con to start the session from. Ignored when a\n" +
        "                --save folder already holds a session, which is resumed instead. Without either, the\n" +
        "                first editor to connect seeds the session with its own level.\n" +
        "  --save <dir>  persist the whole session (objects, terrain, materials, gameplay, water, lights, bakes,\n" +
        "                level-local files) to this folder, debounced and on shutdown, with rolling backups\n" +
        "                under _backups/. Without it the session lives only in memory.\n" +
        "  --pass <pw>   require this password to join. Prefer the RF_PASS environment variable: an argument\n" +
        "                here is visible to every user on the machine through `ps`. RF_PASS is used when --pass\n" +
        "                is absent.\n" +
        "  --bind <ip>   listen on one address only (default: all).\n" +
        "  --backup-every <minutes>  how often to take a timestamped backup (default 5). A backup is only taken\n" +
        "                when something actually changed in that interval, so an idle server writes none.\n" +
        "  --keep-backups <n>        how many backups to keep (default 12). backup-every x keep-backups is the\n" +
        "                whole window you can roll back through; for a team in different timezones make it days.\n" +
        "  --backup-max-mb <mb>      cap the total size of _backups/ (default 512). Oldest snapshots are dropped\n" +
        "                until it fits, so a session that grows cannot quietly fill the disk.\n" +
        "  --maps <dir>              host MANY maps: one folder per map under here, and a client chooses which\n" +
        "                one to work on when it connects. A name nobody has used yet creates that map. Without\n" +
        "                this the relay hosts one session, the one in --save.\n" +
        "  --base-store <dir>        keep the level archive here so a joiner who does not have the map can be\n" +
        "                given it. Without this the relay can only tell people their archive is the wrong one.\n" +
        "                Level archives are hundreds of MB - put this on the disk that has room.\n" +
        "\n" +
        "Once running, type at the console:  status | list | kick <name|id> | save | quit";

    /// <summary>Parse a command line. Unknown flags and a missing value after a flag are errors, so a typo cannot
    /// silently start an open, unpersisted server.</summary>
    public static RelayOptions Parse(IReadOnlyList<string> args, out string? error)
    {
        var o = new RelayOptions();
        string? err = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Count && err is null; i++)
        {
            var a = args[i];
            string? Next(string flag)
            {
                if (i + 1 >= args.Count) { err = $"{flag} needs a value"; return null; }
                return args[++i];
            }
            switch (a)
            {
                case "--help": case "-h": case "/?": o.Help = true; error = null; return o;
                case "--save": o.SavePath = Next(a); break;
                case "--pass": o.Password = Next(a); break;
                case "--port":
                {
                    var v = Next(a); if (v is null) break;
                    if (!int.TryParse(v, out var p) || p <= 0 || p > 65535) err = $"bad port '{v}'"; else o.Port = p;
                    break;
                }
                case "--bind":
                {
                    var v = Next(a); if (v is null) break;
                    if (!IPAddress.TryParse(v, out var ip)) err = $"bad bind address '{v}'"; else o.Bind = ip;
                    break;
                }
                case "--backup-every":
                {
                    var v = Next(a); if (v is null) break;
                    if (!int.TryParse(v, out var m) || m < 1 || m > 24 * 60) err = $"bad backup interval '{v}' (minutes, 1..1440)";
                    else o.BackupMinutes = m;
                    break;
                }
                case "--keep-backups":
                {
                    var v = Next(a); if (v is null) break;
                    if (!int.TryParse(v, out var k) || k < 1 || k > 10000) err = $"bad backup count '{v}' (1..10000)";
                    else o.KeepBackups = k;
                    break;
                }
                case "--base-store": o.BaseStorePath = Next(a); break;
                case "--maps": o.MapsPath = Next(a); break;
                case "--backup-max-mb":
                {
                    var v = Next(a); if (v is null) break;
                    if (!int.TryParse(v, out var m) || m < 1 || m > 1000000) err = $"bad backup size cap '{v}' (MB, 1..1000000)";
                    else o.BackupMaxMb = m;
                    break;
                }
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) err = $"unknown option '{a}'";
                    else positional.Add(a);
                    break;
            }
        }
        // Positional: [port] [seed]. A first positional that is not a number is the seed.
        if (err is null)
        {
            int pi = 0;
            if (positional.Count > pi && int.TryParse(positional[pi], out var pp))
            {
                if (pp <= 0 || pp > 65535) err = $"bad port '{positional[pi]}'"; else o.Port = pp;
                pi++;
            }
            if (err is null && positional.Count > pi) o.SeedPath = positional[pi++];
            if (err is null && positional.Count > pi) err = $"unexpected argument '{positional[pi]}'";
        }
        // A password given on the command line is readable by every user on the machine, because argv shows up
        // in `ps`. Under systemd that is exactly what happens when the unit writes --pass ${RF_PASS}: systemd
        // expands the variable before exec, so the secret lands in argv anyway. Reading the environment directly
        // is what keeps it out. --pass still wins, so nothing that used to work changes.
        if (err is null && string.IsNullOrEmpty(o.Password))
        {
            var env = Environment.GetEnvironmentVariable("RF_PASS");
            if (!string.IsNullOrEmpty(env)) o.Password = env;
        }
        error = err;
        return o;
    }
}

/// <summary>
/// A headless CENTRAL relay: a <see cref="RelayServer"/> behind a TCP host, with no editor and no window. Everyone
/// JOINs this one always-on server with an outbound connection, so nobody has to forward a port on a home router
/// and no participant's local document is force-pushed as canonical by "hosting" - which is what causes accidental
/// overrides. Runs the same whether started from the standalone server executable or the editor's <c>--relay</c>.
///
/// With a state folder, the full canonical state - objects AND terrain / materials / gameplay / water / lights /
/// bakes / level-local files - is persisted there (debounced, and on shutdown) and resumed from it on the next
/// start, so a session survives the server restarting.
/// </summary>
public static class RelayHost
{
    /// <summary>Blocks until the process is stopped.</summary>
    public static void Run(RelayOptions o)
    {
        // A maps directory is a different shape of server: many relays behind one port, each its own map, with
        // the client choosing after it connects. Nothing below this block applies to it, so it returns.
        if (!string.IsNullOrEmpty(o.MapsPath))
        {
            RunMapServer(o);
            return;
        }

        // Resume from the persistence folder if it has state; otherwise load the full seed level.
        StaticObjectsFile? objects = null;
        CollabWorldState? world = null;
        bool resumed = false;
        if (!string.IsNullOrEmpty(o.SavePath) && Directory.Exists(o.SavePath))
        {
            world = CollabWorldState.Load(o.SavePath);
            var soc = Path.Combine(o.SavePath, "StaticObjects.con");
            if (File.Exists(soc)) { try { objects = StaticObjectsFile.Load(soc); } catch { } }
            resumed = world is not null || objects is not null;
        }
        if (!resumed) (objects, world) = LoadFullLevel(o.SeedPath);
        world ??= new CollabWorldState();   // always present so gameplay is stored even on an un-seeded relay

        BaseArchiveStore? baseStore = null;
        RefractorForge.Formats.Rfa.LevelBase.Id? basePin = null;
        if (!string.IsNullOrEmpty(o.BaseStorePath))
        {
            baseStore = new BaseArchiveStore(o.BaseStorePath);
            // A seed given as a .rfa is exactly the archive the session is about to be built on, so take it into
            // the store now: the mapper who started the server should not also have to upload it.
            if (!string.IsNullOrEmpty(o.SeedPath) && File.Exists(o.SeedPath)
                && o.SeedPath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fp = baseStore.Ingest(o.SeedPath);
                    basePin = new RefractorForge.Formats.Rfa.LevelBase.Id(fp, new FileInfo(o.SeedPath).Length,
                                                                         Path.GetFileNameWithoutExtension(o.SeedPath));
                    Console.WriteLine($"  Base archive pinned from the seed: {Path.GetFileName(o.SeedPath)} ({fp[..12]}).");
                }
                catch (Exception ex) { Console.WriteLine($"  could not take the seed into the base store: {ex.Message}"); }
            }
        }
        var relay = new RelayServer(objects, world, o.Password, baseStore, basePin);
        var host = new TcpRelayHost(relay, o.Bind, o.Port);
        host.Start();

        string lan = "";
        try
        {
            lan = string.Join(", ", Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString()));
        }
        catch { }
        int nObj = objects?.Objects.Count ?? 0;
        string layers = $"{nObj} objects, terrain {(world.Height is not null ? "yes" : "no")}, material {(world.Material is not null ? "yes" : "no")}, gameplay {(string.IsNullOrEmpty(world.Gameplay) ? "no" : "yes")}";
        Console.WriteLine($"RefractorForge relay listening on {(Equals(o.Bind, IPAddress.Any) ? "all addresses" : o.Bind.ToString())}, port {host.Port}.");
        if (lan.Length > 0) Console.WriteLine($"  This machine's address: {lan.Split(',')[0].Trim()}:{host.Port}");
        Console.WriteLine("  Editors connect OUT to this address (Collab > Join) - nobody needs to forward a port at home.");
        Console.WriteLine(resumed ? $"  Resumed from {o.SavePath} ({layers})."
            : !world.Any && nObj == 0 ? "  Started EMPTY - the first editor to connect seeds the session with its level."
            : $"  Seeded ({layers}); all joiners adopt this state.");
        if (!string.IsNullOrEmpty(o.SavePath))
        {
            Console.WriteLine($"  Persisting the full session to {o.SavePath}/");
            double windowH = o.BackupMinutes * (double)o.KeepBackups / 60.0;
            string window = windowH >= 48 ? $"{windowH / 24:0.#} days" : windowH >= 1 ? $"{windowH:0.#} hours" : $"{o.BackupMinutes * o.KeepBackups} minutes";
            Console.WriteLine($"  Backups: every {o.BackupMinutes} min of activity, keeping {o.KeepBackups} (or {o.BackupMaxMb} MB, whichever bites first) -> up to {window} of history in _backups/.");
        }
        else Console.WriteLine("  NOT persisted: the session is lost when this process stops. Pass --save <folder> to keep it.");
        if (relay.RequiresAuth) Console.WriteLine("  Password-protected: editors must supply the password to join.");
        if (!string.IsNullOrEmpty(o.BaseStorePath))
            Console.WriteLine($"  Base archives in {o.BaseStorePath}/ - a joiner without the map is given it."
                              + (relay.CanServeBase ? " Held and ready to serve." : " Empty: the first editor on the pinned archive uploads it."));
        else
            Console.WriteLine("  No --base-store: a joiner whose archive differs is warned but cannot be sent the right one.");
        Console.WriteLine("  Ctrl+C to stop.  Console commands: status | list | kick <name|id> | save | quit");
        StartConsole(relay, o.SavePath);

        // Establish the state folder up front (captures the seed) + an initial recovery backup, final-flush on
        // Ctrl+C / SIGTERM, debounced saves in the loop, and a timestamped backup every few minutes of activity.
        DateTime nextBackup = DateTime.Now.AddMinutes(o.BackupMinutes);
        if (!string.IsNullOrEmpty(o.SavePath))
        {
            SaveState(relay, o.SavePath);
            BackupState(o.SavePath, o.KeepBackups, o.BackupMaxMb);
            Console.CancelKeyPress += (_, _) => { try { SaveState(relay, o.SavePath); } catch { } };
            // systemd stops a service with SIGTERM, which is not Ctrl+C: flush there too, or the last edits before
            // a restart are the ones that go missing.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { relay.SaveState(o.SavePath); } catch { } };
        }

        long last = -1, savedSeq = relay.Sequence, backupSeq = relay.Sequence;
        while (true)
        {
            // A download in flight is walked out here, a chunk per pass, so a 300 MB archive never blocks the
            // relay's lock. While one is running the loop spins faster, because 2 s a chunk is a week.
            int sending = relay.PumpBaseSends();
            Thread.Sleep(sending > 0 ? 1 : 2000);
            if (sending > 0) continue;
            long seq = relay.Sequence;
            if (seq != last) { Console.WriteLine($"  {relay.ClientCount} client(s), {seq} edits relayed, {relay.SnapshotDoc().Objects.Count} objects."); last = seq; }
            if (!string.IsNullOrEmpty(o.SavePath) && seq != savedSeq) { SaveState(relay, o.SavePath); savedSeq = seq; }
            if (!string.IsNullOrEmpty(o.SavePath) && seq != backupSeq && DateTime.Now >= nextBackup)
            { BackupState(o.SavePath, o.KeepBackups, o.BackupMaxMb); backupSeq = seq; nextBackup = DateTime.Now.AddMinutes(o.BackupMinutes); }
        }
    }

    /// <summary>Admin console: reads stdin commands on a background thread. If no console is attached (stdin is
    /// closed or redirected, as under systemd), ReadLine returns null and the thread just exits.</summary>
    private static void StartConsole(RelayServer relay, string? savePath)
    {
        var t = new Thread(() =>
        {
            string? line;
            try
            {
                while ((line = Console.ReadLine()) != null)
                {
                    var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    switch (parts[0].ToLowerInvariant())
                    {
                        case "status":
                            Console.WriteLine($"  {relay.ClientCount} client(s), {relay.Sequence} edits relayed, {relay.SnapshotDoc().Objects.Count} objects{(savePath is null ? ", not persisted" : $", persisting to {savePath}")}.");
                            break;
                        case "list":
                            var cl = relay.ClientList();
                            Console.WriteLine(cl.Count == 0 ? "  (no clients connected)"
                                : "  " + string.Join("\n  ", cl.Select(c => $"{c.Name}  [{c.Id}]")));
                            break;
                        case "kick":
                            if (parts.Length < 2) { Console.WriteLine("  usage: kick <name|id>"); break; }
                            var who = relay.Kick(parts[1].Trim());
                            Console.WriteLine(who is null ? $"  no client matching '{parts[1].Trim()}'" : $"  kicked {who}");
                            break;
                        case "save":
                            if (string.IsNullOrEmpty(savePath)) Console.WriteLine("  no --save folder was given; nothing to save to");
                            else SaveState(relay, savePath);
                            break;
                        case "help": Console.WriteLine("  status | list | kick <name|id> | save | quit"); break;
                        case "quit": case "exit":
                            if (!string.IsNullOrEmpty(savePath)) { try { relay.SaveState(savePath); } catch { } }
                            Environment.Exit(0);
                            break;
                        default: Console.WriteLine($"  unknown command '{parts[0]}' (try: status | list | kick | save | quit)"); break;
                    }
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "relay-console" };
        t.Start();
    }

    /// <summary>Copy the current state files to a timestamped <c>_backups/&lt;stamp&gt;/</c> snapshot and prune to the
    /// newest <paramref name="keep"/> - so a bad edit (or a corrupt save) is recoverable by copying a backup over
    /// the state folder. Stamps sort lexicographically because they are yyyyMMdd_HHmmss, so pruning is a sort.</summary>
    public static void BackupState(string dir, int keep = 12, int maxMb = 512)
    {
        try
        {
            var backupsRoot = Path.Combine(dir, "_backups");
            Directory.CreateDirectory(backupsRoot);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(backupsRoot, stamp);
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.EnumerateFiles(dir))   // top-level state files only (not _backups/)
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
            var all = Directory.EnumerateDirectories(backupsRoot).OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (int i = 0; i < all.Count - Math.Max(1, keep); i++) { try { Directory.Delete(all[i], true); } catch { } all[i] = null!; }
            all.RemoveAll(x => x is null);

            // Then the size bound. The newest snapshot is never dropped - without it there is no backup at all,
            // which is worse than being over budget - so the walk stops with one left standing.
            long budget = (long)Math.Max(1, maxMb) * 1024 * 1024;
            long Size(string d)
            {
                try { return new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
                catch { return 0; }
            }
            var sizes = all.ToDictionary(d => d, Size);
            long total = sizes.Values.Sum();
            int dropped = 0;
            for (int i = 0; i < all.Count - 1 && total > budget; i++)
            {
                try { Directory.Delete(all[i], true); total -= sizes[all[i]]; dropped++; } catch { }
            }
            if (dropped > 0) Console.WriteLine($"  [backups over {maxMb} MB: dropped the {dropped} oldest]");
            Console.WriteLine($"  [backup -> _backups/{stamp}]");
        }
        catch (Exception ex) { Console.WriteLine($"  backup failed: {ex.Message}"); }
    }

    private static void SaveState(RelayServer relay, string dir)
    {
        try { relay.SaveState(dir); Console.WriteLine($"  [persisted full state -> {dir}]"); }
        catch (Exception ex) { Console.WriteLine($"  save failed: {ex.Message}"); }
    }

    /// <summary>Load a level (folder or .rfa) into the relay's canonical objects + world state. A bare
    /// StaticObjects.con loads objects only (no terrain/material/gameplay maps to seed).</summary>
    /// <summary>Public so the map library can seed a map from an archive dropped into the maps folder.</summary>
    public static (StaticObjectsFile? Objects, CollabWorldState? World) LoadFullLevel(string? path)
    {
        if (string.IsNullOrEmpty(path)) return (null, null);
        try
        {
            if (RefractorForge.Render.LevelArchive.IsRfa(path))
            {
                var L = RefractorForge.Render.LevelArchive.FromRfa(path);
                return (L.StaticObjects, new CollabWorldState
                {
                    Height = L.Heightmap, Material = L.Material,
                    Under = L.Growth?.Under, Over = L.Growth?.Over,
                    Gameplay = GameplaySync.Serialize(new EditableGameplay(L.Gameplay)),
                });
            }
            if (Directory.Exists(path))
            {
                string? Find(string n) => Directory.EnumerateFiles(path, n, SearchOption.AllDirectories).FirstOrDefault();
                var terr = Find("Terrain.con"); var hmf = Find("Heightmap.raw");
                if (terr is null || hmf is null)
                {
                    var conOnly = Find("StaticObjects.con");
                    return (conOnly is not null ? StaticObjectsFile.Load(conOnly) : null, null);
                }
                var cfg = TerrainConfig.Load(terr);
                var hm = Heightmap.LoadForMaterialSize(hmf, cfg.MaterialSize);
                MaterialMap? mat = null; var mf = Find("MaterialMap.raw");
                if (mf is not null) mat = MaterialMap.FromBytes(File.ReadAllBytes(mf), cfg.MaterialSize, cfg.MaterialSize);
                var growth = GrowthMaps.LoadFolder(path);
                var gp = GameplayObjects.LoadFolder(path);
                var sof = Find("StaticObjects.con");
                return (sof is not null ? StaticObjectsFile.Load(sof) : new StaticObjectsFile(), new CollabWorldState
                {
                    Height = hm, Material = mat, Under = growth.Under, Over = growth.Over,
                    Gameplay = GameplaySync.Serialize(new EditableGameplay(gp)),
                });
            }
            if (File.Exists(path)) return (StaticObjectsFile.Load(path), null);   // a bare StaticObjects.con
        }
        catch (Exception ex) { Console.WriteLine($"  seed load failed ({path}): {ex.Message}"); }
        return (null, null);
    }

    /// <summary>The multi-map relay: a directory of maps behind one port. Each map is its own RelayServer, so
    /// every rule the single-map relay enforces - op ordering, seeding, the base-archive pin - holds per map
    /// without being reimplemented. Clients land in a lobby and are registered only once they pick one.</summary>
    private static void RunMapServer(RelayOptions o)
    {
        BaseArchiveStore? store = string.IsNullOrEmpty(o.BaseStorePath) ? null : new BaseArchiveStore(o.BaseStorePath);
        var maps = new MapLibrary(o.MapsPath!, o.Password, store, o.KeepBackups, o.BackupMaxMb);

        // A seed .rfa given on the command line is the archive these maps are built on: take it into the store
        // once, so the first person to join a map is not also asked to upload hundreds of MB.
        if (store is not null && !string.IsNullOrEmpty(o.SeedPath) && File.Exists(o.SeedPath)
            && o.SeedPath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
        {
            try { Console.WriteLine($"  Base archive taken into the store: {Path.GetFileName(o.SeedPath)} ({store.Ingest(o.SeedPath)[..12]})."); }
            catch (Exception ex) { Console.WriteLine($"  could not take the seed into the base store: {ex.Message}"); }
        }

        var host = new TcpRelayHost(maps, o.Bind, o.Port);
        host.Start();

        Console.WriteLine($"RefractorForge relay listening on {(Equals(o.Bind, IPAddress.Any) ? "all addresses" : o.Bind.ToString())}, port {host.Port}.");
        Console.WriteLine($"  Hosting the maps in {maps.Directory_}/ - each editor picks one after connecting.");
        var listing = maps.List();
        Console.WriteLine(listing.Count == 0
            ? "  No maps yet. The first editor to connect names one and it is created."
            : $"  {listing.Count} map(s): " + string.Join(", ", listing.Select(e => $"{e.Name} ({e.Objects} objects)")));
        double windowH = o.BackupMinutes * (double)o.KeepBackups / 60.0;
        Console.WriteLine($"  Backups per map: every {o.BackupMinutes} min of activity, keeping {o.KeepBackups} (or {o.BackupMaxMb} MB) -> up to {(windowH >= 48 ? $"{windowH / 24:0.#} days" : $"{windowH:0.#} hours")}.");
        Console.WriteLine(store is not null
            ? $"  Base archives in {o.BaseStorePath}/ - a joiner without the map is given it."
            : "  No --base-store: a joiner whose archive differs is warned but cannot be sent the right one.");
        if (maps.RequiresAuth) Console.WriteLine("  Password-protected: editors must supply the password to join.");
        Console.WriteLine("  Ctrl+C to stop.  Console commands: status | maps | quit");

        Console.CancelKeyPress += (_, _) => { try { maps.SaveAll(); } catch { } };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { maps.SaveAll(); } catch { } };
        StartMapConsole(maps);

        long lastReport = -1;
        while (true)
        {
            int sending = maps.Tick(o.BackupMinutes);
            Thread.Sleep(sending > 0 ? 1 : 2000);
            if (sending > 0) continue;
            long total = maps.LoadedRooms.Sum(r => r.Relay.Sequence);
            if (total != lastReport)
            {
                lastReport = total;
                foreach (var r in maps.LoadedRooms.Where(r => r.Relay.ClientCount > 0 || r.Relay.Sequence > 0))
                    Console.WriteLine($"  [{r.Name}] {r.Relay.ClientCount} client(s), {r.Relay.Sequence} edits, {r.Relay.SnapshotDoc().Objects.Count} objects.");
            }
        }
    }

    private static void StartMapConsole(MapLibrary maps)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                string? line;
                try { line = Console.ReadLine(); } catch { return; }
                if (line is null) return;               // no console attached (systemd): nothing to read
                switch (line.Trim().ToLowerInvariant())
                {
                    case "status":
                    case "maps":
                        foreach (var e in maps.List())
                            Console.WriteLine($"  {e.Name,-28} {e.Objects,6} objects  {(e.HasBase ? "archive pinned" : "no archive")}  {e.Clients} here");
                        break;
                    case "save": maps.SaveAll(); Console.WriteLine("  [all maps persisted]"); break;
                    case "quit": maps.SaveAll(); Environment.Exit(0); break;
                }
            }
        }) { IsBackground = true, Name = "relay-console" };
        t.Start();
    }

}
