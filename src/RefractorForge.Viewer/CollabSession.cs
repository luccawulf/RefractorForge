using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using RefractorForge.Collab;
using RefractorForge.Formats.Rfa;
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

    /// <summary>This session is the AI bridge: a relay of our own, bound to loopback, with an assistant on the
    /// other end acting FOR the mapper. It is the one case where an incoming edit belongs on the local undo stack,
    /// because taking back what the assistant just placed with Ctrl+Z is the whole point of it. A real peer's edit
    /// is not yours to take back.</summary>
    public bool LocalOnly { get; private set; }
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
        s.LocalOnly = bind is not null && IPAddress.IsLoopback(bind);
        s._running = true;
        s._relay.Register(new QueuedEndpoint(s.ClientId, s.Inbound));   // relay -> our queue (initial sync + ops)
        s.SendLine(Message.Join(s.ClientId, name).Encode());
        s.Status = $"Hosting on port {s.Port}";
        // The relay's service loop: it walks requested archives and level files out a chunk at a time. The central
        // server runs this in its main loop; a relay inside the editor had none, so a joiner who asked the host for
        // a file waited forever.
        var relayRef = s._relay;
        new Thread(() =>
        {
            while (s._running)
            {
                int busy = 0;
                try { busy = relayRef.PumpBaseSends(); } catch { }
                Thread.Sleep(busy > 0 ? 1 : 100);
            }
        }) { IsBackground = true, Name = "collab-host-pump" }.Start();

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

    /// <summary>The editor's <c>--relay</c>: the same headless relay the standalone server runs, kept here so a
    /// mapper without the server build can still stand one up. Everything lives in <see cref="RelayHost"/>.</summary>
    public static void RunRelay(RelayOptions options) => RelayHost.Run(options);

    // ---- the base level archive ----------------------------------------------------------------------
    // The session syncs edits. Until now it never synced the ground they land on, so two people whose level
    // archives differed applied the same ordered ops to different worlds and neither was told. BaseSync is the
    // client half of the fix, shared with CollabClient so the two cannot drift apart.

    private BaseSync? _base;
    private string? _pendingBasePath;   // what to re-announce once a map has been entered

    // ---- working at different times -------------------------------------------------------------------
    /// <summary>The server map's replica and the comparison against this copy's baseline. Null on a session that
    /// does not keep one (an editor-hosted session, the AI bridge).</summary>
    public MapSync? Sync { get; private set; }

    /// <summary>Keep a replica of the server's map so offline work can be compared, downloaded and uploaded.</summary>
    public void EnableMapSync() => Sync ??= new MapSync(SendLine);

    /// <summary>Asked once a map has been entered: the archive this copy is derived from, if its record says it is
    /// that map. A copy saved since it was synced no longer hashes to the map's archive; this is what lets it in.</summary>
    public Func<string, LevelBase.Id?>? ClaimFor;

    /// <summary>Say this copy is derived from the session's archive - the mapper's own decision, for a copy saved
    /// before records existed. Its content is then compared against the map, not refused for its bytes.</summary>
    public void ClaimBase(LevelBase.Id pin)
    {
        var b = Base();
        BaseStatus = "Comparing your copy with the server's map...";
        b.AnnounceAs(pin);
    }

    // ---- choosing a map -------------------------------------------------------------------------------
    // A central relay hosts several maps and will not register this client anywhere until it names one, so
    // between connecting and picking there is a real lobby state. It is held here rather than in the editor
    // because the lines arrive on this queue and the editor should only have to ask "what is there?".

    /// <summary>One map the server offers.</summary>
    public readonly record struct MapEntry(string Name, int Objects, bool HasBase, long UpdatedUnix, int Clients);

    private readonly List<MapEntry> _maps = new();
    private bool _mapListOpen;

    /// <summary>The maps this server offers, once it has sent the list. Empty on a single-map relay.</summary>
    public IReadOnlyList<MapEntry> Maps { get { lock (_maps) return _maps.ToList(); } }

    /// <summary>True while the server is waiting for this client to pick a map. Nothing else happens until
    /// <see cref="PickMap"/> is called, so the editor must put the choice in front of the user.</summary>
    public bool AwaitingMapChoice { get; private set; }

    /// <summary>The map this client is in, once the server has confirmed it.</summary>
    public string CurrentMap { get; private set; } = "";

    /// <summary>Enter a map. A name the server has never seen creates it.</summary>
    public void PickMap(string name)
    {
        SendLine(Message.PickMap(name).Encode());
        AwaitingMapChoice = false;
        Status = $"Entering {name}...";
    }

    /// <summary>One line about the base archive, for the Collab panel. Empty when there is nothing to say.</summary>
    public string BaseStatus { get; private set; } = "";

    /// <summary>Set when the session is on a different archive from this editor. Until it is resolved, edits
    /// made here land on different ground from everyone else's.</summary>
    public LevelBase.Id? BaseMismatch { get; private set; }

    /// <summary>Whether the relay holds the pinned archive, so the mismatch can actually be fixed from here.</summary>
    public bool BaseAvailable { get; private set; }

    /// <summary>The map this session is built on, as "Saigon68 (a1b2c3d4e5f6)", or empty before the relay says.
    /// Shown whether or not there is a problem: knowing which map you are all on is the point.</summary>
    public string BaseLevel { get; private set; } = "";

    /// <summary>True once the relay has confirmed this editor is on the session's map.</summary>
    public bool BaseAgreed { get; private set; }

    /// <summary>Whether the relay is holding a copy of the session's map for whoever joins next.</summary>
    public bool BaseStored { get; private set; }

    /// <summary>Where a just-finished download landed, until the editor has dealt with it. Set only when the
    /// file is complete and has verified, so acting on it can never open a half-written archive.</summary>
    public string? DownloadedPath { get; private set; }
    public void ClearDownloaded() => DownloadedPath = null;

    /// <summary>Set when the relay says this editor is on the wrong archive AND can send the right one. Picking
    /// a map from the list is already the decision to work on it, so the fetch should follow from that rather
    /// than from a second trip into a menu. The editor clears this when it starts the download; the transfer
    /// window can still cancel it.</summary>
    public bool AutoDownloadPending { get; private set; }
    public void ClearAutoDownload() => AutoDownloadPending = false;

    /// <summary>Set alongside the download so the editor knows to open the map once it lands, rather than
    /// leaving the archive in a folder and the wrong level on screen.</summary>
    public bool OpenWhenDownloaded { get; set; }

    /// <summary>Bytes moved and expected while a transfer runs, else null.</summary>
    public (long Done, long Total)? BaseProgress { get; private set; }

    /// <summary>True while this editor is uploading, so the UI does not offer to start a second one.</summary>
    public bool BaseBusy { get; private set; }

    /// <summary>Which way the bytes are going, so the progress window can say. None when nothing is running.</summary>
    public enum Transfer { None, Download, Upload }
    public Transfer BaseTransfer { get; private set; } = Transfer.None;

    /// <summary>Seconds since the current transfer started, 0 when none is running.</summary>
    public double BaseElapsed => BaseTransfer == Transfer.None ? 0d : (DateTime.UtcNow - _baseStarted).TotalSeconds;

    /// <summary>Bytes per second, averaged over the last few seconds rather than the whole transfer, so the
    /// number tracks what the line is doing now instead of slowly forgetting a bad start.</summary>
    public double BaseRate { get; private set; }

    /// <summary>Seconds left at the current rate, or null while there is not yet enough to estimate from.</summary>
    public double? BaseEta
    {
        get
        {
            var p = BaseProgress;
            if (p is null || BaseRate <= 1 || p.Value.Total <= 0) return null;
            long left = p.Value.Total - p.Value.Done;
            return left <= 0 ? 0d : left / BaseRate;
        }
    }

    private DateTime _baseStarted = DateTime.UtcNow;
    private DateTime _baseRateAt = DateTime.UtcNow;
    private long _baseRateBytes;

    /// <summary>Abandon the transfer in flight. Nothing half-written is left behind either way.</summary>
    public void CancelBaseTransfer()
    {
        _base?.Cancel();
        BaseTransfer = Transfer.None;
        BaseProgress = null; BaseBusy = false; BaseRate = 0;
        BaseStatus = "Transfer cancelled.";
    }

    private void BeginTransfer(Transfer kind)
    {
        BaseTransfer = kind;
        _baseStarted = DateTime.UtcNow;
        _baseRateAt = _baseStarted;
        _baseRateBytes = 0;
        BaseRate = 0;
        BaseBusy = true;
    }

    private void EndTransfer()
    {
        BaseTransfer = Transfer.None;
        BaseProgress = null; BaseBusy = false; BaseRate = 0;
    }

    private void NoteProgress(long done, long total)
    {
        BaseProgress = (done, total);
        // Sampled rather than measured per chunk: a chunk is ~128 KB and arrives in bursts, so a per-chunk
        // rate reads as noise. Half a second of samples is enough to be steady and still react to a stall.
        var now = DateTime.UtcNow;
        double dt = (now - _baseRateAt).TotalSeconds;
        if (dt >= 0.5)
        {
            double instant = (done - _baseRateBytes) / dt;
            BaseRate = BaseRate <= 0 ? instant : BaseRate * 0.7 + instant * 0.3;
            _baseRateAt = now;
            _baseRateBytes = done;
        }
    }

    private BaseSync Base()
    {
        if (_base is not null) return _base;
        _base = new BaseSync(SendLine);
        _base.OnAgreed += pin =>
        {
            BaseAgreed = true; BaseMismatch = null; BaseStored = true;
            BaseLevel = pin is null ? "" : $"{pin.LevelName} ({pin.Short})";
            BaseStatus = pin is null
                ? "No level open, so there is nothing to keep in step."
                : "You and the session are on the same map.";
        };
        _base.OnMismatch += (pin, avail) =>
        {
            BaseMismatch = pin; BaseAvailable = avail; BaseAgreed = false; BaseStored = avail;
            if (avail && !BaseBusy) { AutoDownloadPending = true; OpenWhenDownloaded = true; }
            BaseLevel = $"{pin.LevelName} ({pin.Short})";
            BaseStatus = avail
                ? $"This session is built on a different {pin.LevelName}.rfa ({pin.Short}). Download it to work on the same map."
                : $"This session is built on a different {pin.LevelName}.rfa ({pin.Short}), and the relay has no copy to send.";
        };
        _base.OnWanted += pin =>
        {
            BaseStored = false;
            BaseLevel = $"{pin.LevelName} ({pin.Short})";
            BaseStatus = $"The relay has no copy of {pin.LevelName}.rfa. Upload yours so others can be given it.";
        };
        _base.OnProgress += NoteProgress;
        _base.OnDownloaded += path =>
        {
            EndTransfer(); BaseMismatch = null; BaseAgreed = true; BaseStored = true;
            DownloadedPath = path;
            BaseStatus = $"Downloaded the session's archive to {path}.";
        };
        _base.OnFailed += why => { EndTransfer(); BaseStatus = "Base archive: " + why; };
        return _base;
    }

    /// <summary>Tell the relay which level archive this editor has open. Hashing a 400 MB archive takes a
    /// moment, so it runs on a worker and the answer arrives through the normal inbound queue.</summary>
    public void AnnounceBase(string? archivePath)
    {
        _pendingBasePath = archivePath;
        var b = Base();
        BaseStatus = "Checking the session is on the same map...";
        var t = new Thread(() => { try { b.Announce(archivePath); } catch { } }) { IsBackground = true };
        t.Start();
    }

    /// <summary>Handle an inbound line that belongs to the session rather than to the document - the base
    /// archive, and the map list. Returns true when it was one, so the editor's own switch can ignore it.</summary>
    public bool HandleBaseMessage(Message m)
    {
        switch (m.Type)
        {
            case MsgType.MapList:
                lock (_maps) _maps.Clear();
                _mapListOpen = true;
                return true;

            case MsgType.MapInfo:
                if (_mapListOpen)
                    lock (_maps)
                        _maps.Add(new MapEntry(m.Args[0], int.Parse(m.Args[1]), m.Args[2] == "1",
                                               long.Parse(m.Args[3]), int.Parse(m.Args[4])));
                return true;

            case MsgType.MapEnd:
                _mapListOpen = false;
                AwaitingMapChoice = true;
                Status = "Choose a map on the server";
                return true;

            case MsgType.MapOk:
                CurrentMap = m.Args[0];
                AwaitingMapChoice = false;
                Status = $"Connected - {CurrentMap}";
                // The BASE line sent on connect went nowhere: a multi-map relay reads only PICKMAP while a
                // client is in the lobby. Now that this client is in a map, say again what it is standing on -
                // and the pin it is measured against belongs to THIS map, not to whatever was picked before.
                // A copy whose record names this map says the archive it grew from, not its own bytes.
                if (ClaimFor?.Invoke(CurrentMap) is { } claim) { BaseStatus = "Checking the session is on the same map..."; Base().AnnounceAs(claim); }
                else if (_pendingBasePath is not null) AnnounceBase(_pendingBasePath);
                return true;
        }
        // The map replica: version, history, baseline and files are its alone; the document and the ops it keeps a
        // copy of and hands on, because the editor decides separately whether to take them.
        if (Sync is not null && Sync.Handle(m)) return true;
        return Base().Handle(m);
    }

    /// <summary>Ask the server to build the map as it stands and send it as one archive - the file you open in
    /// the editor or put on a game server, edits included.</summary>
    public void DownloadCurrentMap(string destinationPath)
    {
        var b = Base();
        BeginTransfer(Transfer.Download);
        BaseProgress = (0, 0);
        BaseStatus = "Building the current map on the server...";
        b.RequestExport(destinationPath);
    }

    /// <summary>Fetch the session's archive to <paramref name="destinationPath"/>.</summary>
    public void DownloadBase(string destinationPath)
    {
        var b = Base();
        BeginTransfer(Transfer.Download);
        BaseProgress = (0, BaseMismatch?.Bytes ?? 0);
        BaseStatus = "Downloading the session's archive...";
        b.Download(destinationPath);
    }

    /// <summary>Send this editor's archive up so the relay can give it to whoever joins next. Runs on a worker:
    /// it walks the whole file, and the render loop must keep running while it does.</summary>
    public void UploadBase(string archivePath)
    {
        if (BaseBusy) return;
        var b = Base();
        BeginTransfer(Transfer.Upload);
        try { BaseProgress = (0, new FileInfo(archivePath).Length); } catch { BaseProgress = (0, 0); }
        BaseStatus = "Uploading the archive so others can be given it...";
        var t = new Thread(() =>
        {
            try { b.Upload(archivePath); BaseStored = true; BaseStatus = "Uploaded. Anyone who joins can now be given this map."; }
            catch (Exception ex) { BaseStatus = "Upload failed: " + ex.Message; }
            finally { EndTransfer(); }
        }) { IsBackground = true };
        t.Start();
    }

    /// <summary>Whether edits may go out live right now. A server map this copy has not yet caught up with keeps
    /// its edits here - they are found by the comparison and go up with Upload - rather than landing on a map
    /// that has moved on underneath them.</summary>
    public Func<bool>? MaySendOps;

    /// <summary>The archive the session's map is built on, once agreed.</summary>
    public LevelBase.Id? SessionBase => _base?.Session;

    public void SendOp(string wire)
    {
        if (MaySendOps is not null && !MaySendOps()) return;
        SendLine(Message.Op(0, ClientId, ++_opId, wire).Encode());
    }

    public void SendPresence(string selectionId, Vec3 cursor, float heading = 0f, float pitch = 0f)
        => SendLine(Message.Presence(ClientId, Name, string.IsNullOrEmpty(selectionId) ? "-" : selectionId, cursor, heading, pitch).Encode());

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
