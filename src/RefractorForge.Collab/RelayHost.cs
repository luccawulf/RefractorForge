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
        "  --pass <pw>   require this password to join.\n" +
        "  --bind <ip>   listen on one address only (default: all).\n" +
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

        var relay = new RelayServer(objects, world, o.Password);
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
        if (!string.IsNullOrEmpty(o.SavePath)) Console.WriteLine($"  Persisting the full session to {o.SavePath}/  (+ rolling backups under _backups/).");
        else Console.WriteLine("  NOT persisted: the session is lost when this process stops. Pass --save <folder> to keep it.");
        if (relay.RequiresAuth) Console.WriteLine("  Password-protected: editors must supply the password to join.");
        Console.WriteLine("  Ctrl+C to stop.  Console commands: status | list | kick <name|id> | save | quit");
        StartConsole(relay, o.SavePath);

        // Establish the state folder up front (captures the seed) + an initial recovery backup, final-flush on
        // Ctrl+C / SIGTERM, debounced saves in the loop, and a timestamped backup every few minutes of activity.
        DateTime nextBackup = DateTime.Now.AddMinutes(5);
        if (!string.IsNullOrEmpty(o.SavePath))
        {
            SaveState(relay, o.SavePath);
            BackupState(o.SavePath);
            Console.CancelKeyPress += (_, _) => { try { SaveState(relay, o.SavePath); } catch { } };
            // systemd stops a service with SIGTERM, which is not Ctrl+C: flush there too, or the last edits before
            // a restart are the ones that go missing.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { relay.SaveState(o.SavePath); } catch { } };
        }

        long last = -1, savedSeq = relay.Sequence, backupSeq = relay.Sequence;
        while (true)
        {
            Thread.Sleep(2000);
            long seq = relay.Sequence;
            if (seq != last) { Console.WriteLine($"  {relay.ClientCount} client(s), {seq} edits relayed, {relay.SnapshotDoc().Objects.Count} objects."); last = seq; }
            if (!string.IsNullOrEmpty(o.SavePath) && seq != savedSeq) { SaveState(relay, o.SavePath); savedSeq = seq; }
            if (!string.IsNullOrEmpty(o.SavePath) && seq != backupSeq && DateTime.Now >= nextBackup)
            { BackupState(o.SavePath); backupSeq = seq; nextBackup = DateTime.Now.AddMinutes(5); }
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
    /// last 12 - so a bad edit (or a corrupt save) is recoverable by copying a backup over the state folder.</summary>
    private static void BackupState(string dir)
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
            for (int i = 0; i < all.Count - 12; i++) try { Directory.Delete(all[i], true); } catch { }
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
    private static (StaticObjectsFile?, CollabWorldState?) LoadFullLevel(string? path)
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
}
