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

    /// <summary>The editor's <c>--relay</c>: the same headless relay the standalone server runs, kept here so a
    /// mapper without the server build can still stand one up. Everything lives in <see cref="RelayHost"/>.</summary>
    public static void RunRelay(RelayOptions options) => RelayHost.Run(options);

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
