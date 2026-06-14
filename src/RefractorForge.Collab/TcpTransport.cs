using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RefractorForge.Collab;

/// <summary>
/// A real, runnable relay over TCP using newline-delimited UTF-8 lines. Pure BCL — no packages.
/// Each accepted socket becomes an <see cref="IClientEndpoint"/> the <see cref="RelayServer"/>
/// pushes to; a per-connection read loop feeds inbound lines back into the relay.
/// </summary>
public sealed class TcpRelayHost
{
    private readonly RelayServer _server;
    private readonly TcpListener _listener;
    private volatile bool _running;

    public TcpRelayHost(RelayServer server, IPAddress addr, int port)
    {
        _server = server;
        _listener = new TcpListener(addr, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _running = true;
        var t = new Thread(AcceptLoop) { IsBackground = true, Name = "relay-accept" };
        t.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient sock;
            try { sock = _listener.AcceptTcpClient(); }
            catch { break; }
            var t = new Thread(() => Serve(sock)) { IsBackground = true };
            t.Start();
        }
    }

    private void Serve(TcpClient sock)
    {
        using var stream = sock.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        // Optional auth gate: a password-protected relay requires a valid AUTH line before anything else.
        if (_server.RequiresAuth)
        {
            string? authLine = reader.ReadLine();
            if (authLine is null) { sock.Close(); return; }
            Message auth;
            try { auth = Message.Decode(authLine); } catch { sock.Close(); return; }
            if (auth.Type != MsgType.Auth || !_server.CheckAuth(auth.Payload))
            {
                try { writer.WriteLine(Message.Error("authentication failed").Encode()); } catch { }
                sock.Close(); return;
            }
        }

        // First line after auth must be JOIN <clientId> <name>; the clientId names this endpoint.
        string? first = reader.ReadLine();
        if (first is null) { sock.Close(); return; }
        Message join;
        try { join = Message.Decode(first); } catch { sock.Close(); return; }
        if (join.Type != MsgType.Join) { sock.Close(); return; }

        string clientId = join.Args[0];
        var ep = new SocketEndpoint(clientId, writer, sock);
        _server.Register(ep);          // streams initial state to this socket
        _server.OnLine(clientId, first); // process the JOIN (presence)

        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                _server.OnLine(clientId, line);
        }
        catch { /* socket dropped */ }
        finally
        {
            _server.DisconnectIf(clientId, ep);   // no-op if this id already reconnected on a fresh socket
            try { sock.Close(); } catch { }
        }
    }

    private sealed class SocketEndpoint : IClientEndpoint
    {
        public string ClientId { get; }
        private readonly StreamWriter _w;
        private readonly TcpClient _sock;
        private readonly object _lock = new();
        public SocketEndpoint(string id, StreamWriter w, TcpClient sock) { ClientId = id; _w = w; _sock = sock; }
        public void Deliver(string line)
        {
            lock (_lock) { try { _w.WriteLine(line); } catch { } }
        }
        public void Close() { try { _sock.Close(); } catch { } }   // admin kick: drop the socket; the read loop ends
    }
}

/// <summary>Client side of the TCP transport: connects, pumps inbound lines to a CollabClient.</summary>
public sealed class TcpClientConnection : IServerEndpoint, IDisposable
{
    private readonly TcpClient _sock;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private CollabClient? _client;
    private readonly object _lock = new();
    private volatile bool _running;

    public TcpClientConnection(string host, int port)
    {
        _sock = new TcpClient();
        _sock.Connect(host, port);
        var s = _sock.GetStream();
        _reader = new StreamReader(s, Encoding.UTF8);
        _writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>Bind a client and start the read loop. The client must send JOIN first.</summary>
    public void Attach(CollabClient client)
    {
        _client = client;
        _running = true;
        var t = new Thread(ReadLoop) { IsBackground = true };
        t.Start();
        client.Join();
    }

    private void ReadLoop()
    {
        try
        {
            string? line;
            while (_running && (line = _reader.ReadLine()) != null)
                _client?.OnLine(line);
        }
        catch { }
    }

    // CollabClient -> server
    public void Receive(string clientId, string line)
    {
        lock (_lock) { try { _writer.WriteLine(line); } catch { } }
    }

    public void Dispose()
    {
        _running = false;
        try { _sock.Close(); } catch { }
    }
}
