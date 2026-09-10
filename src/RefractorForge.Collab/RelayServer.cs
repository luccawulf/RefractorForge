using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Rfa;

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

    // The base level archive the session is pinned to. Ops are meaningless without agreement on the ground they
    // land on, so the first client to arrive with a level sets the pin and everyone after is measured against it.
    private readonly BaseArchiveStore? _baseStore;
    private LevelBase.Id? _basePin;
    private string? _uploader;   // the one client currently allowed to write into the store

    public RelayServer(StaticObjectsFile? initial = null, CollabWorldState? world = null, string? password = null,
                       BaseArchiveStore? baseStore = null, LevelBase.Id? basePin = null)
    {
        _baseStore = baseStore;
        _basePin = basePin;
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

    /// <summary>The canonical NON-object state - terrain, materials, growth, gameplay, water, lights. Handed out
    /// by reference: it is only ever read by the export, and cloning a pair of full-size terrain maps to answer a
    /// question about them would cost more than the export itself.</summary>
    public CollabWorldState? SnapshotWorld()
    {
        lock (_gate) return _world;
    }

    public long Sequence { get { lock (_gate) return _seq; } }
    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    /// <summary>The archive this session is pinned to, or null while nobody has arrived with a level yet.</summary>
    public LevelBase.Id? BasePin { get { lock (_gate) return _basePin; } }

    /// <summary>Whether the relay holds the pinned archive's bytes and can therefore serve them to a joiner.</summary>
    public bool CanServeBase
    {
        get { lock (_gate) return _basePin is not null && (_baseStore?.Has(_basePin.Fingerprint) ?? false); }
    }

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
        Attach(ep);
        StreamStateTo(ep.ClientId);
    }

    /// <summary>Take the connection without sending it anything. Split from <see cref="StreamStateTo"/> because
    /// a client has to be reachable before it is trusted with the document: on a multi-map relay it needs to be
    /// able to download an archive it does not have, and that transfer runs over this same connection, while
    /// the document itself must NOT be handed over until we know it is standing on the right ground.</summary>
    public void Attach(IClientEndpoint ep)
    {
        lock (_gate) _clients[ep.ClientId] = ep;
    }

    /// <summary>Whether this client's archive is the one the session is built on. Called before the document is
    /// streamed, which is the whole point: learning about a mismatch afterwards is learning too late, because
    /// by then another map's objects are already sitting in the editor's open level.</summary>
    public bool BaseMatches(LevelBase.Id? mine)
    {
        lock (_gate)
        {
            if (_basePin is null) return true;                 // nothing pinned yet: anyone may proceed
            return mine is not null && _basePin.Matches(mine);
        }
    }

    /// <summary>Send the canonical document to a client that has been attached and cleared.</summary>
    public void StreamStateTo(string clientId)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(clientId, out var ep)) return;
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
                    if (_uploader == clientId) { _uploader = null; if (_basePin is not null) _baseStore?.Abort(_basePin.Fingerprint); }
                    BroadcastLocked(Message.Leave(clientId).Encode(), except: clientId);
                }
                break;

            // ---- the base level archive -------------------------------------------------------------------
            case MsgType.Base:
                lock (_gate) HandleBaseAnnounceLocked(clientId, m);
                break;

            case MsgType.Export:
            {
                // Repacking even a 383 MB archive measures at about a second, but a second is far too long to
                // hold the lock every edit in the session needs, so it happens on a worker.
                var builder = BuildExport;
                if (builder is null)
                {
                    lock (_gate)
                        if (_clients.TryGetValue(clientId, out var noEp))
                            noEp.Deliver(Message.ExportFailed("this relay cannot build an archive").Encode());
                    break;
                }
                var t = new Thread(() =>
                {
                    (LevelBase.Id Id, string Path)? built = null;
                    string? why = null;
                    try { built = builder(); if (built is null) why = "this map has no base archive to build from yet"; }
                    catch (Exception ex) { why = ex.Message; }
                    lock (_gate)
                    {
                        if (!_clients.TryGetValue(clientId, out var ep2)) return;
                        if (built is null) { ep2.Deliver(Message.ExportFailed(why ?? "export failed").Encode()); return; }
                        ep2.Deliver(Message.ExportReady(built.Value.Id.Fingerprint, built.Value.Id.Bytes, built.Value.Id.LevelName).Encode());
                        _pendingSends[clientId] = new Send { Fingerprint = built.Value.Id.Fingerprint, Path = built.Value.Path };
                    }
                }) { IsBackground = true, Name = "relay-export" };
                t.Start();
                break;
            }

            case MsgType.BaseGet:
                lock (_gate)
                {
                    if (_basePin is null || _baseStore is null || !_clients.TryGetValue(clientId, out var to)) break;
                    if (!m.Args[0].Equals(_basePin.Fingerprint, StringComparison.OrdinalIgnoreCase)) break;
                    _pendingSends[clientId] = new Send
                    {
                        Fingerprint = _basePin.Fingerprint,
                        Path = Path.Combine(_baseStore.Directory_, _basePin.Fingerprint.ToLowerInvariant() + ".rfa"),
                    };
                    _ = to;
                }
                break;

            case MsgType.BasePut:
                lock (_gate)
                {
                    // Only ever accept the archive the session is already pinned to. Without that a client could
                    // push any file it liked into the store and have the next joiner install it.
                    if (_baseStore is null || _basePin is null) break;
                    if (!m.Args[0].Equals(_basePin.Fingerprint, StringComparison.OrdinalIgnoreCase)) break;
                    if (_baseStore.Has(_basePin.Fingerprint)) break;       // already have it; ignore the offer
                    if (_uploader is not null && _uploader != clientId) break;   // one uploader at a time
                    _uploader = clientId;
                    _baseStore.BeginReceive(_basePin.Fingerprint);
                }
                break;

            case MsgType.BaseData:
                lock (_gate)
                {
                    if (_baseStore is null || _basePin is null || _uploader != clientId) break;
                    if (!m.Args[0].Equals(_basePin.Fingerprint, StringComparison.OrdinalIgnoreCase)) break;
                    byte[] data;
                    try { data = Convert.FromBase64String(m.Payload); }
                    catch { _baseStore.Abort(_basePin.Fingerprint); _uploader = null; break; }
                    if (!_baseStore.ReceiveChunk(_basePin.Fingerprint, int.Parse(m.Args[1]), data)) _uploader = null;
                }
                break;

            case MsgType.BaseDone:
                lock (_gate)
                {
                    if (_baseStore is null || _basePin is null || _uploader != clientId) break;
                    bool ok = _baseStore.CompleteReceive(_basePin.Fingerprint);
                    _uploader = null;
                    if (ok && _clients.TryGetValue(clientId, out var ep)) ep.Deliver(Message.BaseOk(_basePin.Fingerprint).Encode());
                }
                break;
        }
    }

    // A client asking for the archive is served from a pump rather than inside the lock: a 300 MB file is
    // thousands of chunks and holding the relay's only lock across them would stall every edit in the session.
    private readonly Dictionary<string, Send> _pendingSends = new();

    /// <summary>One transfer in flight to one client. The path is carried because a client may be pulling an
    /// exported archive rather than the pinned base, and those are different files.</summary>
    private sealed class Send
    {
        public required string Fingerprint { get; init; }
        public required string Path { get; init; }
        public int Index;
    }

    /// <summary>Build the map as it stands now into one archive. Set by whatever owns this relay's map folder,
    /// because the relay itself knows the edits but not where the base archive lives or what the map is called.
    /// Null on a relay that cannot export - the client is told so rather than left waiting.</summary>
    public Func<(LevelBase.Id Id, string Path)?>? BuildExport;

    /// <summary>Start streaming a file to a client. Used by the export, which builds its archive outside the
    /// relay and then hands it over here.</summary>
    public void SendFileTo(string clientId, string fingerprint, string path)
    {
        lock (_gate)
            if (_clients.ContainsKey(clientId))
                _pendingSends[clientId] = new Send { Fingerprint = fingerprint, Path = path };
    }

    /// <summary>Push one chunk to each client currently downloading the base archive. Called on the host's
    /// service loop; returns the number of clients still receiving, so an idle relay does no work.</summary>
    public int PumpBaseSends()
    {
        List<(IClientEndpoint Ep, string Fp, string Path, int Index)> work = new();
        lock (_gate)
        {
            if (_pendingSends.Count == 0) return 0;
            foreach (var kv in _pendingSends.ToList())
                if (_clients.TryGetValue(kv.Key, out var ep)) work.Add((ep, kv.Value.Fingerprint, kv.Value.Path, kv.Value.Index));
                else _pendingSends.Remove(kv.Key);
        }
        foreach (var (ep, fp, path, index) in work)
        {
            var chunk = BaseArchiveStore.ReadChunkOf(path, index);
            try
            {
                if (chunk.Length == 0)
                {
                    ep.Deliver(Message.BaseDone(fp).Encode());
                    lock (_gate) _pendingSends.Remove(ep.ClientId);
                }
                else
                {
                    ep.Deliver(Message.BaseData(fp, index, Convert.ToBase64String(chunk)).Encode());
                    lock (_gate) if (_pendingSends.TryGetValue(ep.ClientId, out var sd)) sd.Index = index + 1;
                }
            }
            catch { lock (_gate) _pendingSends.Remove(ep.ClientId); }
        }
        lock (_gate) return _pendingSends.Count;
    }

    /// <summary>Decide what to tell a client that has just said what archive it is standing on.</summary>
    private void HandleBaseAnnounceLocked(string clientId, Message m)
    {
        if (!_clients.TryGetValue(clientId, out var ep)) return;
        var mine = LevelBase.Id.TryDecode($"{m.Args[0]} {m.Args[1]} {m.Args[2]}");
        bool hasLevel = mine is not null && mine.Fingerprint != "-" && mine.Bytes > 0;

        // Nobody has pinned the session yet: the first arrival with a real level defines it. A client with
        // nothing open cannot pin anything, and is simply told there is nothing to match yet.
        if (_basePin is null)
        {
            if (!hasLevel) { ep.Deliver(Message.BaseOk("-").Encode()); return; }
            _basePin = mine;
            ep.Deliver(Message.BaseOk(mine!.Fingerprint).Encode());
            if (_baseStore is not null && !_baseStore.Has(mine.Fingerprint)) ep.Deliver(Message.BaseNeed(mine.Fingerprint).Encode());
            return;
        }

        if (hasLevel && _basePin.Matches(mine))
        {
            ep.Deliver(Message.BaseOk(_basePin.Fingerprint).Encode());
            // They are on the right archive; if the store is empty they are the one who can fill it.
            if (_baseStore is not null && !_baseStore.Has(_basePin.Fingerprint) && _uploader is null)
                ep.Deliver(Message.BaseNeed(_basePin.Fingerprint).Encode());
            return;
        }

        ep.Deliver(Message.BaseDiff(_basePin.Fingerprint, _basePin.Bytes, _basePin.LevelName,
                                    _baseStore?.Has(_basePin.Fingerprint) ?? false).Encode());
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
