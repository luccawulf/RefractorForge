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
    private readonly RelayServer? _server;      // single-map relay: every client lands in this one
    private readonly MapLibrary? _maps;         // multi-map relay: the client picks, then it is registered
    private readonly TcpListener _listener;
    private volatile bool _running;

    public TcpRelayHost(RelayServer server, IPAddress addr, int port)
    {
        _server = server;
        _listener = new TcpListener(addr, port);
    }

    /// <summary>A relay hosting a directory of maps. A connecting client is sent the list and stays in the
    /// lobby - registered nowhere, holding no document - until it picks one.</summary>
    public TcpRelayHost(MapLibrary maps, IPAddress addr, int port)
    {
        _maps = maps;
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

        // Optional auth gate: a password-protected relay requires a valid AUTH line before anything else. The
        // password is the relay's, not a map's, so it is checked before the client learns what maps exist.
        bool requiresAuth = _server?.RequiresAuth ?? _maps?.RequiresAuth ?? false;
        if (requiresAuth)
        {
            string? authLine = reader.ReadLine();
            if (authLine is null) { sock.Close(); return; }
            Message auth;
            try { auth = Message.Decode(authLine); } catch { sock.Close(); return; }
            bool ok = _server?.CheckAuth(auth.Payload) ?? _maps!.CheckAuth(auth.Payload);
            if (auth.Type != MsgType.Auth || !ok)
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

        // THE LOBBY. On a multi-map relay the client is registered nowhere until it names a map: registering
        // first would stream a whole level to somebody about to ask for a different one, and would leave a
        // window in which their edits could land in the wrong map.
        RelayServer server;
        if (_maps is not null)
        {
            ep.Deliver(Message.MapList().Encode());
            foreach (var e in _maps.List())
                ep.Deliver(Message.MapInfo(e.Name, e.Objects, e.HasBase, e.UpdatedUnix, e.Clients).Encode());
            ep.Deliver(Message.MapEnd().Encode());

            MapLibrary.Room? room = null;
            while (room is null)
            {
                string? pick = reader.ReadLine();
                if (pick is null) { sock.Close(); return; }
                Message pm;
                try { pm = Message.Decode(pick); } catch { continue; }
                if (pm.Type != MsgType.PickMap) continue;      // nothing else means anything until a map is chosen
                room = _maps.Open(pm.Args[0]);
                if (room is null)
                    ep.Deliver(Message.Error($"'{pm.Args[0]}' is not a usable map name (letters, digits, - _ . only)").Encode());
            }
            server = room.Relay;
            ep.Deliver(Message.MapOk(room.Name).Encode());

            // THE GATE. The client is attached now, so it can download an archive it does not have, but the
            // DOCUMENT is withheld until it has said what it is standing on and that turns out to be the map's
            // own archive. Streaming first and checking afterwards is how another map's objects ended up in an
            // open level: by the time the mismatch was known, the editor had already adopted them.
            server.Attach(ep);
            server.OnLine(clientId, first);          // presence, so others see them in the lobby

            bool cleared = false;
            while (!cleared)
            {
                string? l = reader.ReadLine();
                if (l is null) { server.DisconnectIf(clientId, ep); sock.Close(); return; }
                Message bm;
                try { bm = Message.Decode(l); } catch { continue; }
                if (bm.Type != MsgType.Base)
                {
                    server.OnLine(clientId, l);      // BASEGET and its chunks have to keep working while we wait
                    continue;
                }
                var mine = RefractorForge.Formats.Rfa.LevelBase.Id.TryDecode($"{bm.Args[0]} {bm.Args[1]} {bm.Args[2]}");
                bool has = mine is not null && mine.Fingerprint != "-" && mine.Bytes > 0;
                if (server.BaseMatches(has ? mine : null))
                {
                    server.OnLine(clientId, l);      // sets the pin when there was none, and answers BASEOK
                    cleared = true;
                }
                else server.OnLine(clientId, l);     // answers BASEDIFF; we keep waiting rather than hand over the map
            }
            server.StreamStateTo(clientId);
        }
        else
        {
            server = _server!;
            server.Register(ep);           // single-map relay: unchanged, streams initial state to this socket
            server.OnLine(clientId, first); // process the JOIN (presence)
        }

        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                server.OnLine(clientId, line);
        }
        catch { /* socket dropped */ }
        finally
        {
            server.DisconnectIf(clientId, ep);   // no-op if this id already reconnected on a fresh socket
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
