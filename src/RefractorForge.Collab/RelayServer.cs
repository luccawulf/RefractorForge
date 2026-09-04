using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;

namespace RefractorForge.Collab;

/// <summary>
/// The authoritative relay. It owns the canonical document and a single monotonic sequence
/// counter. Every incoming object-edit is stamped with the next sequence number, applied to the
/// canonical document, then rebroadcast to all clients (including the originator). Because the
/// relay is the single serialization point, every client receives the identical ordered op
/// stream and therefore converges to the canonical state.
///
/// Transport-agnostic: it speaks only in lines via <see cref="IClientEndpoint"/>, so the same
/// core drives both the in-process test transport and the real TCP host.
/// </summary>
public sealed class RelayServer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IClientEndpoint> _clients = new();
    private readonly Dictionary<string, string> _names = new();
    private readonly StaticObjectsFile _doc;
    private readonly CollabWorldState? _world;   // canonical terrain / material / gameplay (null = object-only relay)
    private readonly string? _password;          // optional shared secret; clients AUTH before JOIN (null = open)
    private long _seq;
    private bool _seedClaimed;   // an empty relay asks its first client to seed it; only one client is asked

    public RelayServer(StaticObjectsFile? initial = null, CollabWorldState? world = null, string? password = null)
    {
        _doc = initial?.Clone() ?? new StaticObjectsFile();
        _world = world;
        _password = string.IsNullOrEmpty(password) ? null : password;
        // Already holding a LEVEL -> never ask. Judged on objects / terrain / materials / gameplay, not on "any op at
        // all": a session that had only received water and light settings counted as seeded, so after a restart
        // the relay never asked again and handed every joiner an empty document to adopt.
        _seedClaimed = _doc.Objects.Count > 0 || (world?.HasLevelContent ?? false);
    }

    /// <summary>Whether a password must be presented (via an AUTH line) before JOIN.</summary>
    public bool RequiresAuth => _password is not null;
    /// <summary>Validate a supplied password (always true on an open relay).</summary>
    public bool CheckAuth(string? supplied) => _password is null || supplied == _password;

    /// <summary>Snapshot of connected clients (id + display name), for an admin <c>list</c>.</summary>
    public IReadOnlyList<(string Id, string Name)> ClientList()
    {
        lock (_gate) return _clients.Keys.Select(id => (id, _names.TryGetValue(id, out var n) ? n : id)).ToList();
    }

    /// <summary>Admin kick: match a client by exact id or a case-insensitive name prefix, tell it (Error),
    /// force its connection closed, drop it from the canonical roster, and notify the rest. Returns the kicked
    /// client's display name, or null if nothing matched.</summary>
    public string? Kick(string idOrPrefix)
    {
        lock (_gate)
        {
            string? target = _clients.ContainsKey(idOrPrefix) ? idOrPrefix
                : _names.FirstOrDefault(kv => kv.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).Key;
            if (target is null || !_clients.TryGetValue(target, out var ep)) return null;
            string name = _names.TryGetValue(target, out var nm) ? nm : target;
            try { ep.Deliver(Message.Error("kicked by host").Encode()); } catch { }
            try { ep.Close(); } catch { }
            _clients.Remove(target); _names.Remove(target);
            BroadcastLocked(Message.Leave(target).Encode(), except: target);
            return name;
        }
    }

    /// <summary>Current canonical document (a copy; the live one stays private).</summary>
    public StaticObjectsFile SnapshotDoc()
    {
        lock (_gate) return _doc.Clone();
    }

    public long Sequence { get { lock (_gate) return _seq; } }
    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    /// <summary>Persist the canonical objects + world (terrain/material/gameplay) to a state directory, taken
    /// under the relay lock so it's a consistent snapshot. Resume with <see cref="CollabWorldState.Load"/> +
    /// <see cref="StaticObjectsFile.Load"/>.</summary>
    public void SaveState(string dir)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(dir);
            _doc.Save(System.IO.Path.Combine(dir, "StaticObjects.con"));
            _world?.Save(dir);
        }
    }

    /// <summary>
    /// Register a freshly-connected client and immediately stream it the current state so it
    /// starts from the canonical document (late joiners get everything done so far).
    /// </summary>
    public void Register(IClientEndpoint ep)
    {
        lock (_gate)
        {
            _clients[ep.ClientId] = ep;
            ep.Deliver(Message.SyncBegin(_seq).Encode());
            foreach (var line in SnapshotAsWire(_doc))
                ep.Deliver(Message.SyncObj(line).Encode());
            if (_world is not null)                                   // replay terrain / material / gameplay too
                foreach (var op in _world.SnapshotOps())
                    ep.Deliver(Message.SyncObj(op).Encode());
            ep.Deliver(Message.SyncEnd().Encode());

            // A fresh central relay (started empty) has no canonical state, so the FIRST client to connect is
            // asked to upload its document. Subsequent clients just adopt it — nobody clobbers by "hosting".
            if (!_seedClaimed)
            {
                _seedClaimed = true;
                ep.Deliver(Message.SeedRequest().Encode());
            }
        }
    }

    /// <summary>Handle one inbound line from a client.</summary>
    public void OnLine(string clientId, string line)
    {
        Message m;
        try { m = Message.Decode(line); }
        catch { return; }

        switch (m.Type)
        {
            case MsgType.Join:
                lock (_gate)
                {
                    _names[clientId] = m.Args[1];
                    // Tell the newcomer about everyone present, and everyone about the newcomer.
                    foreach (var kv in _names)
                        if (kv.Key != clientId && _clients.TryGetValue(clientId, out var to))
                            to.Deliver(Message.Presence(kv.Key, kv.Value, "-", Formats.Geometry.Vec3.Zero).Encode());
                    BroadcastLocked(Message.Presence(clientId, m.Args[1], "-", Formats.Geometry.Vec3.Zero).Encode(), except: null);
                }
                break;

            case MsgType.Op:
                lock (_gate)
                {
                    long seq = ++_seq;
                    // Route to the canonical state: object edits -> the document; TERRAIN/MATERIAL/GAMEPLAY -> the
                    // world state (kept so late joiners + a restarted server get terrain/material/vehicles too).
                    var payload = m.Payload;
                    int pv = payload.IndexOf(' ');
                    string verb = pv < 0 ? payload : payload[..pv];
                    try
                    {
                        if (verb is "ADD" or "MOVE" or "ROT" or "SCALE" or "DEL") EditWire.Parse(payload).Apply(_doc);
                        else _world?.ApplyOp(payload);
                    }
                    catch { /* malformed op: drop, do not advance state */ }
                    // Rebroadcast in canonical order to everyone, including the sender (acts as ack/ordering).
                    BroadcastLocked(Message.Op(seq, m.Args[1], long.Parse(m.Args[2]), payload).Encode(), except: null);
                }
                break;

            case MsgType.Presence:
                // Ephemeral; relay to others, never touches the document.
                lock (_gate) BroadcastLocked(line, except: clientId);
                break;

            case MsgType.Leave:
                lock (_gate)
                {
                    _clients.Remove(clientId);
                    _names.Remove(clientId);
                    BroadcastLocked(Message.Leave(clientId).Encode(), except: clientId);
                }
                break;
        }
    }

    public void Disconnect(string clientId) => OnLine(clientId, Message.Leave(clientId).Encode());

    /// <summary>Disconnect a client only if <paramref name="ep"/> is still its CURRENT endpoint. A reconnecting
    /// client re-Registers under the same id, so the dying old socket's teardown must NOT evict the fresh one.</summary>
    public void DisconnectIf(string clientId, IClientEndpoint ep)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(clientId, out var cur) && !ReferenceEquals(cur, ep)) return;   // superseded
            _clients.Remove(clientId);
            _names.Remove(clientId);
            BroadcastLocked(Message.Leave(clientId).Encode(), except: clientId);
        }
    }

    private void BroadcastLocked(string line, string? except)
    {
        foreach (var kv in _clients)
            if (kv.Key != except)
                kv.Value.Deliver(line);
    }

    /// <summary>Serialize a document as a list of EditWire commands that recreate it (ADD + SCALE).</summary>
    internal static IEnumerable<string> SnapshotAsWire(StaticObjectsFile doc)
    {
        foreach (var o in doc.Objects)
        {
            yield return new AddObject(o.Id, o.Template, o.Position, o.Rotation).ToWire();
            if (o.Scale is float s)
                yield return new ScaleObject(o.Id, s).ToWire();
        }
    }
}
