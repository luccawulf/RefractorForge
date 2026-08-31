using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Message = RefractorForge.Collab.Message;   // disambiguate from System.Windows.Forms.Message

namespace RefractorForge.Viewer;

/// <summary>
/// Viewer-side collaboration session — either the HOST (owns the relay) or a JOINER (one TCP client).
/// Inbound protocol lines arrive on background socket/relay threads and are parked in <see cref="Inbound"/>;
/// the editor DRAINS them on the GL thread, so the document and GL state are only ever touched by the render
/// thread. Outbound (<see cref="SendOp"/>/<see cref="SendPresence"/>) is thread-safe. Reuses the tested
/// RefractorForge.Collab relay/protocol; only the GL-thread marshalling lives here.
/// </summary>
sealed class CollabSession
{
    public string ClientId { get; }
    public string Name { get; }
    public bool IsHost { get; }
    public int Port { get; private set; }
    public string Status { get; set; } = "";
    /// <summary>This machine's LAN IPv4 address(es), for sharing on a local network (host only).</summary>
    public string LocalIp { get; private set; } = "";
    /// <summary>This machine's public IP (fetched best-effort), for internet play with port-forwarding (host only).</summary>
    public string PublicIp { get; set; } = "";

    /// <summary>Protocol lines awaiting application on the GL thread.</summary>
    public readonly ConcurrentQueue<string> Inbound = new();
    /// <summary>Other participants (touched only on the GL thread, during the drain).</summary>
    public readonly Dictionary<string, Peer> Peers = new();

    private long _opId;
    private volatile bool _running;
    private readonly object _wlock = new();

    // host side
    private RelayServer? _relay;
    private TcpRelayHost? _tcpHost;
    // join side
    private TcpClient? _sock;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private string _joinHost = ""; private int _joinPort; private string _joinPass = "";

    private CollabSession(string clientId, string name, bool isHost)
    { ClientId = clientId; Name = name; IsHost = isHost; }

    /// <summary>Start hosting: stand up the relay seeded with the current document, listen on <paramref name="port"/>,
    /// and join our own session through a GL-thread-queued endpoint. <paramref name="bind"/> defaults to every
    /// interface, which is what collaborating over a network needs; the AI bridge passes loopback instead so that
    /// turning it on does not also publish the map to the LAN.</summary>
    public static CollabSession StartHost(StaticObjectsFile doc, int port, string name, string? password = null,
                                          CollabWorldState? world = null, IPAddress? bind = null)
    {
        var s = new CollabSession(NewId(), name, true);
        s._relay = new RelayServer(doc, world, password);
        s._tcpHost = new TcpRelayHost(s._relay, bind ?? IPAddress.Any, port);
        s._tcpHost.Start();
        s.Port = s._tcpHost.Port;
        s._running = true;
        s._relay.Register(new QueuedEndpoint(s.ClientId, s.Inbound));   // relay -> our queue (initial sync + ops)
        s.SendLine(Message.Join(s.ClientId, name).Encode());
        s.Status = $"Hosting on port {s.Port}";

        // Surface connection info: LAN IPv4 now, public IP best-effort (for internet play with port-forwarding).
        try
        {
            s.LocalIp = string.Join(", ", Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString()));
        }
        catch { }
        s.PublicIp = "(fetching...)";
        _ = Task.Run(async () =>
        {
            try { using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(6) }; s.PublicIp = (await hc.GetStringAsync("https://api.ipify.org")).Trim(); }
            catch { s.PublicIp = "(unavailable)"; }
        });
        return s;
    }

    /// <summary>Join an existing host at <paramref name="host"/>:<paramref name="port"/>. Survives transient
    /// drops: the read loop transparently reconnects (with backoff) and re-syncs, only tearing the session down
    /// if it can't get back after many attempts. <paramref name="password"/> is sent (as AUTH) when set.</summary>
    public static CollabSession StartJoin(string host, int port, string name, string? password = null)
    {
        var s = new CollabSession(NewId(), name, false);
        s._joinHost = host; s._joinPort = port; s._joinPass = password ?? "";
        s._running = true;
        s.Connect();                            // initial connect; throws on failure -> surfaced as a join error
        s.Port = port;
        var t = new Thread(s.ReadSupervised) { IsBackground = true, Name = "collab-read" };
        t.Start();
        s.Status = $"Connected to {host}:{port}";
        return s;
    }

    /// <summary>(Re)open the socket, rebind reader/writer, and send AUTH (if any) + JOIN. Throws on connect failure.</summary>
    private void Connect()
    {
        var sock = new TcpClient();
        sock.Connect(_joinHost, _joinPort);
        var stream = sock.GetStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new StreamReader(stream, Encoding.UTF8);
        lock (_wlock) { _sock = sock; _writer = writer; _reader = reader; }
        if (!string.IsNullOrEmpty(_joinPass)) WriteRaw(Message.Auth(_joinPass).Encode());
        WriteRaw(Message.Join(ClientId, Name).Encode());
    }

    private void WriteRaw(string line) { lock (_wlock) { try { _writer?.WriteLine(line); } catch { } } }

    /// <summary>Read inbound lines; on a drop, reconnect with exponential backoff (re-Register re-syncs the full
    /// canonical state) instead of ending the session. Gives up — and signals the GL thread to tear down — only
    /// after many failed attempts.</summary>
    private void ReadSupervised()
    {
        int backoff = 1000;
        while (_running)
        {
            StreamReader? reader; lock (_wlock) reader = _reader;
            bool serverClosed = false;   // an ERROR line is a deliberate server close (kick / auth fail) -> don't reconnect
            try
            {
                string? l;
                while (_running && reader is not null && (l = reader.ReadLine()) != null)
                {
                    Inbound.Enqueue(l); backoff = 1000;
                    if (l.StartsWith("ERROR ", StringComparison.Ordinal)) { serverClosed = true; break; }
                }
            }
            catch { }
            if (!_running || serverClosed) break;   // give up: the GL thread tears down on the ERROR we enqueued
            Status = $"Reconnecting to {_joinHost}:{_joinPort}...";
            try { lock (_wlock) _sock?.Close(); } catch { }
            bool ok = false;
            for (int attempt = 0; _running && attempt < 30; attempt++)
            {
                Thread.Sleep(backoff);
                backoff = Math.Min(backoff * 2, 15000);
                try { Connect(); ok = true; Status = $"Reconnected to {_joinHost}:{_joinPort}"; break; } catch { }
            }
            if (!ok) { Inbound.Enqueue(Message.Error("disconnected").Encode()); break; }
        }
    }

    /// <summary>Run a headless CENTRAL relay (no editor/GL window): a <see cref="RelayServer"/> + TCP host on
    /// <paramref name="port"/>, optionally seeded from a level (folder / StaticObjects.con / .rfa). Everyone JOINs
    /// this one always-on server, so no participant's local document is force-pushed as canonical by "hosting" —
    /// which is what causes accidental overrides. When <paramref name="savePath"/> (a STATE FOLDER) is given, the
    /// full canonical state — objects AND terrain / material / gameplay (vehicles) — is PERSISTED there (debounced +
    /// on Ctrl+C) and RESUMED from it on the next start, so edits survive a server restart. Blocks until stopped.</summary>
    public static void RunRelay(int port, string? seedPath, string? savePath = null, string? password = null)
    {
        // Resume from the persistence folder if it has state; otherwise load the full seed level.
        StaticObjectsFile? objects = null;
        CollabWorldState? world = null;
        bool resumed = false;
        if (!string.IsNullOrEmpty(savePath) && Directory.Exists(savePath))
        {
            world = CollabWorldState.Load(savePath);
            var soc = Path.Combine(savePath, "StaticObjects.con");
            if (File.Exists(soc)) { try { objects = StaticObjectsFile.Load(soc); } catch { } }
            resumed = world is not null || objects is not null;
        }
        if (!resumed) (objects, world) = LoadFullLevel(seedPath);
        world ??= new CollabWorldState();   // always present so gameplay is stored even on an un-seeded relay

        var relay = new RelayServer(objects, world, password);
        var host = new TcpRelayHost(relay, IPAddress.Any, port);
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
        Console.WriteLine($"RefractorForge central relay listening on port {host.Port}.");
        if (lan.Length > 0) Console.WriteLine($"  LAN clients Join: {lan.Split(',')[0].Trim()}:{host.Port}");
        Console.WriteLine($"  Internet: forward TCP {host.Port} on the router (or use a VPN), then Join the public IP.");
        Console.WriteLine(resumed ? $"  Resumed from {savePath} ({layers})."
            : !world.Any && nObj == 0 ? "  Started EMPTY — the first client to connect seeds the canonical document."
            : $"  Seeded ({layers}); all joiners adopt this state.");
        if (!string.IsNullOrEmpty(savePath)) Console.WriteLine($"  Persisting FULL state (objects + terrain + material + gameplay) to {savePath}/  (+ rolling backups under _backups/).");
        if (relay.RequiresAuth) Console.WriteLine("  Password-protected: clients must supply the password to join.");
        Console.WriteLine("  Ctrl+C to stop.  Admin commands: list | kick <name|id> | quit");
        StartRelayConsole(relay, savePath);

        // Establish the state folder up front (captures the seed) + an initial recovery backup, final-flush on
        // Ctrl+C, debounced saves in the loop, and a timestamped backup every few minutes of activity.
        DateTime nextBackup = DateTime.Now.AddMinutes(5);
        if (!string.IsNullOrEmpty(savePath))
        {
            SaveState(relay, savePath);
            BackupState(savePath);
            Console.CancelKeyPress += (_, _) => { try { SaveState(relay, savePath); } catch { } };
        }

        long last = -1, savedSeq = relay.Sequence, backupSeq = relay.Sequence;
        while (true)
        {
            System.Threading.Thread.Sleep(2000);
            long seq = relay.Sequence;
            if (seq != last) { Console.WriteLine($"  {relay.ClientCount} client(s), {seq} edits relayed, {relay.SnapshotDoc().Objects.Count} objects."); last = seq; }
            if (!string.IsNullOrEmpty(savePath) && seq != savedSeq) { SaveState(relay, savePath); savedSeq = seq; }
            if (!string.IsNullOrEmpty(savePath) && seq != backupSeq && DateTime.Now >= nextBackup)
            { BackupState(savePath); backupSeq = seq; nextBackup = DateTime.Now.AddMinutes(5); }
        }
    }

    /// <summary>Headless-relay admin console: reads stdin commands (list / kick / quit) on a background thread.
    /// If no console is attached (stdin is closed/redirected), ReadLine returns null and the thread just exits.</summary>
    private static void StartRelayConsole(RelayServer relay, string? savePath)
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
                        case "help": Console.WriteLine("  list | kick <name|id> | quit"); break;
                        case "quit": case "exit":
                            if (!string.IsNullOrEmpty(savePath)) { try { relay.SaveState(savePath); } catch { } }
                            Environment.Exit(0);
                            break;
                        default: Console.WriteLine($"  unknown command '{parts[0]}' (try: list | kick | quit)"); break;
                    }
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "relay-console" };
        t.Start();
    }

    /// <summary>Copy the current state files to a timestamped <c>_backups/&lt;stamp&gt;/</c> snapshot and prune to the
    /// last 12 — so a bad edit (or a corrupt save) is recoverable by copying a backup over the state folder.</summary>
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

    public void SendOp(string wire) => SendLine(Message.Op(0, ClientId, ++_opId, wire).Encode());

    public void SendPresence(string selectionId, Vec3 cursor, float heading = 0f)
        => SendLine(Message.Presence(ClientId, Name, string.IsNullOrEmpty(selectionId) ? "-" : selectionId, cursor, heading).Encode());

    private void SendLine(string line)
    {
        if (!_running) return;
        try
        {
            if (IsHost) _relay!.OnLine(ClientId, line);   // relay is internally locked; echoes into our queue
            else WriteRaw(line);                          // join side; null-safe across a reconnect swap
        }
        catch { }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { SendLine(Message.Leave(ClientId).Encode()); } catch { }
        try { _tcpHost?.Stop(); } catch { }
        try { _sock?.Close(); } catch { }
    }

    private static string NewId() => "u" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Relay -> host's local client: park delivered lines for the GL thread instead of pushing a socket.</summary>
    private sealed class QueuedEndpoint : IClientEndpoint
    {
        public string ClientId { get; }
        private readonly ConcurrentQueue<string> _q;
        public QueuedEndpoint(string id, ConcurrentQueue<string> q) { ClientId = id; _q = q; }
        public void Deliver(string line) => _q.Enqueue(line);
    }
}
