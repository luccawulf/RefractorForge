using System.Net.Sockets;
using System.Text;
using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Mcp;

/// <summary>
/// A live attachment to a RUNNING RefractorForge editor. The editor already hosts the collaboration relay, and its
/// wire protocol is exactly the edit-command vocabulary this server speaks (ADD / MOVE / ROT / SCALE / DEL), so an
/// AI client is just another peer: objects it places appear in the editor's viewport the same frame the relay
/// echoes them back, and the human's own edits stream the other way into <see cref="Doc"/>.
///
/// Three things about the relay make this less trivial than "open a socket", and each is handled here rather than
/// left to the caller:
///
///  * <see cref="CollabClient"/> only understands the five OBJECT verbs, and every world op (TERRAIN, MATERIAL,
///    GAMEPLAY, WATER, OVERGROWTH, OBJMESH) makes it throw. It gets a steady supply of them: the relay broadcasts
///    everything to everyone, AND replays the whole world as SYNCOBJ lines on connect, so attaching to an editor
///    where anyone has painted terrain means throwing before the first object even arrives. They are filtered out
///    before the client sees them; the read loop also catches, so no single bad line can drop the link either way.
///  * <see cref="CollabClient"/> has no locking at all, and raises its callbacks on the socket read thread while an
///    MCP request may be calling Add/Move on another. Every touch of the client goes through <see cref="Gate"/>.
///  * Sending AUTH to a relay that has no password makes the relay treat it as the JOIN line and hang up without
///    saying why, so AUTH goes out only when a password was actually supplied.
///
/// Undo is kept here rather than in the relay because the protocol has no undo: each edit records its own inverse
/// (captured from the document BEFORE the edit) and undo replays inverses as ordinary ops. That is honest about
/// what it is — a compensating edit that other peers see, not a rewind of shared history.
/// </summary>
public sealed class LiveBridge : IServerEndpoint, IDisposable
{
    public string Host { get; }
    public int Port { get; }
    public string ClientId { get; }
    public string DisplayName { get; }

    /// <summary>Held across every use of <see cref="_client"/>. Public so the session can group a read-modify-write
    /// (look up an object, then edit it) into one atomic step against the inbound stream.</summary>
    public object Gate { get; } = new();

    /// <summary>The editor's live object document, kept current by the relay stream. Read under <see cref="Gate"/>.</summary>
    public StaticObjectsFile Doc => _client.Doc;

    /// <summary>The editor's gameplay layer as last received, or null if none has arrived. Gameplay syncs as FULL
    /// STATE, so anything sent REPLACES the whole layer - which makes knowing the current contents the difference
    /// between adding a control point and deleting everyone else's. The relay replays it on connect, and every
    /// later edit by anyone updates it.</summary>
    public string? GameplayText { get; private set; }

    public bool Connected => _running && _sock.Connected;
    public bool Synced => _synced.IsSet;
    /// <summary>Why the read loop stopped, when it stopped for a reason worth reporting (ERROR from the relay, a
    /// dropped socket). Null while healthy.</summary>
    public string? Disconnected { get; private set; }

    private readonly TcpClient _sock;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly CollabClient _client;
    private readonly ManualResetEventSlim _synced = new(false);
    private volatile bool _running;

    // The six verbs CollabClient cannot parse. They belong to the relay's CollabWorldState, not to the object
    // document, and reach us only because the relay broadcasts everything to everyone.
    private static readonly string[] WorldVerbs = { "TERRAIN", "MATERIAL", "GAMEPLAY", "WATER", "OVERGROWTH", "OBJMESH" };

    // One undo entry: the ops that reverse a single tool call, newest-first. A generated city is hundreds of adds
    // and exactly one entry, matching the editor's own grouping.
    private readonly List<List<IEditCommand>> _undo = new();
    private readonly List<List<IEditCommand>> _redo = new();

    public LiveBridge(string host, int port, string? password, string displayName)
    {
        Host = host;
        Port = port;
        // A fresh id per connection. CollabClient derives object ids as "{ClientId}-{n}" from a counter that
        // restarts at zero, so a fixed id would remint ids the editor already holds after a reconnect — and an ADD
        // whose id already exists is silently discarded by the relay, which would look like the AI going deaf.
        ClientId = "mcp-" + Guid.NewGuid().ToString("N")[..8];
        DisplayName = Sanitize(displayName, "Claude");

        _sock = new TcpClient();
        _sock.Connect(host, port);
        var stream = _sock.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        _client = new CollabClient(ClientId, DisplayName, this);
        _running = true;
        new Thread(ReadLoop) { IsBackground = true, Name = "mcp-bridge-read" }.Start();

        if (!string.IsNullOrEmpty(password)) _writer.WriteLine(Message.Auth(password).Encode());
        _client.Join();
    }

    /// <summary>Block until the relay has finished streaming the editor's current document, so the first tool call
    /// after attaching sees real objects rather than an empty document.</summary>
    public bool WaitSynced(TimeSpan timeout) => _synced.Wait(timeout);

    // ---- IServerEndpoint: client -> relay ----

    public void Receive(string clientId, string line)
    {
        try { lock (_writer) _writer.WriteLine(line); }
        catch (Exception ex) { Stop(ex.Message); }
    }

    private void ReadLoop()
    {
        try
        {
            string? line;
            while (_running && (line = _reader.ReadLine()) != null)
            {
                if (IsWorldOp(line)) { CaptureWorld(line); continue; }   // not ours, and fatal to CollabClient

                if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                {
                    Stop("relay refused the connection: " + line[6..]);
                    return;
                }

                lock (Gate)
                {
                    // A single malformed op must not take the connection down with it.
                    try { _client.OnLine(line); } catch { }
                }

                if (line.StartsWith("SYNCEND", StringComparison.Ordinal)) _synced.Set();
            }
            Stop("the editor closed the connection");
        }
        catch (Exception ex) { Stop(ex.Message); }
        finally { _synced.Set(); }                              // never leave WaitSynced hanging on a dead socket
    }

    /// <summary>Keep what we can use out of a world op before dropping it. Gameplay is the one we must track: it
    /// is full-state, so sending an edit without knowing the current layer would wipe everyone else's work.</summary>
    private void CaptureWorld(string line)
    {
        try
        {
            int i = line.IndexOf("GAMEPLAY ", StringComparison.Ordinal);
            if (i < 0) return;
            var b64 = line[(i + "GAMEPLAY ".Length)..].Trim();
            if (b64.Length == 0) return;
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            lock (Gate) GameplayText = text;
        }
        catch { }
    }

    /// <summary>True for a relay line whose payload is a world op rather than an object op.</summary>
    private static bool IsWorldOp(string line)
    {
        // OP <seq> <clientId> <localOpId> <payload...>   |   SYNCOBJ <payload...>
        var p = line.Split(' ');
        string verb = p[0] switch
        {
            "OP" => p.Length > 4 ? p[4] : "",
            "SYNCOBJ" => p.Length > 1 ? p[1] : "",
            _ => "",
        };
        return Array.IndexOf(WorldVerbs, verb) >= 0;
    }

    private void Stop(string why)
    {
        if (!_running) return;
        _running = false;
        Disconnected = why;
    }

    // ---- Edits. Every one records its inverse first, then sends. ----

    public string Add(string template, Vec3 pos, Vec3 rot, float scale = 1f)
    {
        template = RequireToken(template, "template");
        lock (Gate)
        {
            string id = _client.Add(template, pos, rot);
            if (MathF.Abs(scale - 1f) > 1e-4f) _client.Scale(id, scale);
            _undo.Add(new List<IEditCommand> { new DeleteObject(id) });
            _redo.Clear();
            return id;
        }
    }

    /// <summary>Place many objects as ONE undo entry — a generated city is a single thing the user asked for.</summary>
    public List<string> AddMany(IEnumerable<(string Template, Vec3 Pos, Vec3 Rot, float Scale)> items)
    {
        var ids = new List<string>();
        lock (Gate)
        {
            var inverse = new List<IEditCommand>();
            foreach (var it in items)
            {
                string id = _client.Add(RequireToken(it.Template, "template"), it.Pos, it.Rot);
                if (MathF.Abs(it.Scale - 1f) > 1e-4f) _client.Scale(id, it.Scale);
                inverse.Add(new DeleteObject(id));
                ids.Add(id);
            }
            if (inverse.Count > 0)
            {
                inverse.Reverse();
                _undo.Add(inverse);
                _redo.Clear();
            }
        }
        return ids;
    }

    public bool Move(string id, Vec3 to) => Transform(id, o => new MoveObject(id, o.Position), () => _client.Move(id, to));
    public bool Rotate(string id, Vec3 to) => Transform(id, o => new RotateObject(id, o.Rotation), () => _client.Rotate(id, to));
    public bool Scale(string id, float to) => Transform(id, o => new ScaleObject(id, o.Scale ?? 1f), () => _client.Scale(id, to));

    public bool Delete(string id)
    {
        lock (Gate)
        {
            var o = _client.Doc.FindById(id);
            if (o is null) return false;
            // Re-creating a deleted object needs its template and transform back, so capture them now.
            var restore = new List<IEditCommand> { new AddObject(id, o.Template, o.Position, o.Rotation) };
            if (o.Scale is { } sc && MathF.Abs(sc - 1f) > 1e-4f) restore.Add(new ScaleObject(id, sc));
            _client.Delete(id);
            _undo.Add(restore);
            _redo.Clear();
            return true;
        }
    }

    private bool Transform(string id, Func<StaticObject, IEditCommand> inverse, Action apply)
    {
        lock (Gate)
        {
            var o = _client.Doc.FindById(id);
            if (o is null) return false;
            var back = inverse(o);       // capture the CURRENT value before overwriting it
            apply();
            _undo.Add(new List<IEditCommand> { back });
            _redo.Clear();
            return true;
        }
    }

    /// <summary>Reverse the last tool call by sending its compensating ops. Returns how many ops went out, or -1
    /// when there is nothing left to undo. Note this is a compensating EDIT, not a rewind — the other people in the
    /// session see it happen, which is the only honest thing a shared document can do.</summary>
    public int Undo() => Step(_undo, _redo);

    /// <summary>Re-apply what <see cref="Undo"/> reversed, by the same compensating mechanism.</summary>
    public int Redo() => Step(_redo, _undo);

    private int Step(List<List<IEditCommand>> from, List<List<IEditCommand>> to)
    {
        lock (Gate)
        {
            if (from.Count == 0) return -1;
            var ops = from[^1];
            from.RemoveAt(from.Count - 1);
            // Capture the reverse of what we are about to do BEFORE doing it, so the other stack can undo this undo.
            var back = ops.Select(Capture).Where(c => c is not null).Select(c => c!).ToList();
            back.Reverse();
            foreach (var op in ops) Send(op);
            if (back.Count > 0) to.Add(back);
            return ops.Count;
        }
    }

    /// <summary>The command that would reverse <paramref name="cmd"/>, read from the document's CURRENT state.</summary>
    private IEditCommand? Capture(IEditCommand cmd) => cmd switch
    {
        AddObject a => new DeleteObject(a.Id),
        DeleteObject d => _client.Doc.FindById(d.Id) is { } o ? new AddObject(d.Id, o.Template, o.Position, o.Rotation) : null,
        MoveObject m => _client.Doc.FindById(m.Id) is { } o ? new MoveObject(m.Id, o.Position) : null,
        RotateObject r => _client.Doc.FindById(r.Id) is { } o ? new RotateObject(r.Id, o.Rotation) : null,
        ScaleObject s => _client.Doc.FindById(s.Id) is { } o ? new ScaleObject(s.Id, o.Scale ?? 1f) : null,
        _ => null,
    };

    public int UndoDepth { get { lock (Gate) return _undo.Count; } }
    public int RedoDepth { get { lock (Gate) return _redo.Count; } }

    /// <summary>Send a WORLD op — the relay verbs CollabClient has no API for (WATER, TERRAIN, …). The editor
    /// applies these from its own inbound drain, so a water-level change lands live like an object edit does.</summary>
    public void SendWorldOp(string wire) => Receive(ClientId, Message.Op(0, ClientId, 0, wire).Encode());

    /// <summary>Push one command through the client's own predict-and-send path, whatever its concrete type.</summary>
    private void Send(IEditCommand cmd)
    {
        switch (cmd)
        {
            case DeleteObject d: _client.Delete(d.Id); break;
            case MoveObject m: _client.Move(m.Id, m.To); break;
            case RotateObject r: _client.Rotate(r.Id, r.To); break;
            case ScaleObject s: _client.Scale(s.Id, s.To); break;
            case AddObject a:
                // Re-adding under the ORIGINAL id, which CollabClient.Add cannot do (it mints its own).
                _client.Doc.Objects.Add(new StaticObject(a.Template) { Id = a.Id, Position = a.Pos, Rotation = a.Rot });
                Receive(ClientId, Message.Op(0, ClientId, 0, a.ToWire()).Encode());
                break;
        }
    }

    /// <summary>A snapshot of the live document, safe to read outside the lock.</summary>
    public List<StaticObject> Snapshot()
    {
        lock (Gate) return _client.Doc.Objects.ToList();
    }

    /// <summary>Names of the peers currently in the session (the human editors this AI is sharing the map with).</summary>
    public List<string> PeerNames()
    {
        lock (Gate) return _client.Peers.Values.Select(p => p.Name).ToList();
    }

    // The wire is space-delimited with fixed field positions, so a token containing a space silently corrupts every
    // field after it. Reject it at the boundary instead of shipping a broken op.
    private static string RequireToken(string s, string what)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) throw new ArgumentException($"{what} is required");
        if (s.Any(char.IsWhiteSpace)) throw new ArgumentException($"{what} '{s}' contains whitespace, which the collab wire cannot carry");
        return s;
    }

    private static string Sanitize(string? s, string fallback)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return fallback;
        // Presence lines are parsed by fixed position, so a spaced display name breaks presence for every peer.
        return string.Concat(s.Select(c => char.IsWhiteSpace(c) ? '_' : c));
    }

    public void Dispose()
    {
        if (_running)
        {
            _running = false;
            try { Receive(ClientId, Message.Leave(ClientId).Encode()); } catch { }
        }
        try { _sock.Close(); } catch { }
        _synced.Dispose();
    }
}
