using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Collab;

/// <summary>One other participant's live, ephemeral state (cursor + current selection).</summary>
public sealed class Peer
{
    public string ClientId = "";
    public string Name = "";
    public string SelectionId = "-";
    public Vec3 Cursor = Vec3.Zero;
    public float Heading = 0f;   // camera yaw (radians, 0 = +Z) so the diamond can show which way they're looking
}

/// <summary>
/// A single participant's session with OPTIMISTIC LOCAL PREDICTION.
///
/// Two documents are tracked:
///   * <c>_confirmed</c> — the canonical document, mutated ONLY by replaying the relay's
///     totally-ordered op stream. Identical on every client by construction.
///   * <see cref="Doc"/> — the *predicted* view shown to the user: the confirmed document
///     plus this client's not-yet-acknowledged local ops replayed on top. Local edits take
///     effect immediately (zero perceived latency) instead of waiting for the round-trip.
///
/// Reconciliation: every time a canonical op arrives (a remote edit, or the echo of one of our
/// own), we (a) apply it to <c>_confirmed</c>, (b) drop the matching pending op if it was ours,
/// and (c) rebuild <see cref="Doc"/> as <c>_confirmed</c> with the remaining pending ops replayed.
/// Because all object-edits are ABSOLUTE sets (MOVE/ROT/SCALE write a value) or id-addressed
/// structural ops (ADD/DEL), replaying pending-on-top is order-insensitive at the field level and
/// the prediction self-heals: once a client's ops are all acknowledged, its pending list is empty
/// and <see cref="Doc"/> == <c>_confirmed</c> == the canonical document on every client.
///
/// Fast path: when there are no pending local ops, <see cref="Doc"/> aliases <c>_confirmed</c>
/// directly, so a client that is only *watching* never pays a clone — important at 50k objects.
/// </summary>
public sealed class CollabClient
{
    public string ClientId { get; }
    public string Name { get; }

    /// <summary>The predicted view the UI renders (confirmed + pending local ops).</summary>
    public StaticObjectsFile Doc { get; private set; } = new();

    public bool Ready { get; private set; }
    public long LastSeq { get; private set; }

    /// <summary>Number of local ops sent but not yet acknowledged by the relay.</summary>
    public int PendingCount => _pending.Count;

    public IReadOnlyDictionary<string, Peer> Peers => _peers;
    private readonly Dictionary<string, Peer> _peers = new();

    private readonly IServerEndpoint _server;
    private long _localOpId;
    private int _addCounter;

    /// <summary>The canonical document (relay-ordered stream only). Doc is derived from this.</summary>
    private StaticObjectsFile _confirmed = new();

    /// <summary>Local ops applied to the predicted Doc but not yet echoed back by the relay.</summary>
    private readonly List<PendingOp> _pending = new();
    private readonly struct PendingOp
    {
        public readonly long LocalOpId;
        public readonly string Wire;          // canonical wire form (re-parsed fresh on each replay)
        public PendingOp(long id, string wire) { LocalOpId = id; Wire = wire; }
    }

    /// <summary>Fired after each applied op (remote, or local prediction) for UI repaint.</summary>
    public Action<IEditCommand>? OnApplied;

    public CollabClient(string clientId, string name, IServerEndpoint server)
    {
        ClientId = clientId;
        Name = name;
        _server = server;
    }

    /// <summary>Announce presence (sends display name). Call after the transport is attached.</summary>
    public void Join() => _server.Receive(ClientId, Message.Join(ClientId, Name).Encode());

    // ---- Local edit API: predict immediately, then send upstream. ----

    public void Move(string id, Vec3 to)   => Predict(new MoveObject(id, to));
    public void Rotate(string id, Vec3 to) => Predict(new RotateObject(id, to));
    public void Scale(string id, float to) => Predict(new ScaleObject(id, to));
    public void Delete(string id)          => Predict(new DeleteObject(id));

    /// <summary>Add a new object. The id is namespaced to this client so concurrent adds never collide.</summary>
    public string Add(string template, Vec3 pos, Vec3 rot)
    {
        string id = $"{ClientId}-{++_addCounter}";
        Predict(new AddObject(id, template, pos, rot));
        return id;
    }

    /// <summary>Apply a local op to the predicted Doc now; record it pending; send it upstream.</summary>
    private void Predict(IEditCommand cmd)
    {
        long opId = ++_localOpId;
        string wire = cmd.ToWire();

        // Diverge from _confirmed on the first pending op (so prediction never mutates canonical).
        if (_pending.Count == 0)
            Doc = _confirmed.Clone();

        cmd.Apply(Doc);
        _pending.Add(new PendingOp(opId, wire));
        OnApplied?.Invoke(cmd);

        _server.Receive(ClientId, Message.Op(0, ClientId, opId, wire).Encode());
    }

    /// <summary>Publish this client's cursor/selection/heading to peers (ephemeral, not part of the doc).</summary>
    public void UpdatePresence(string selectionId, Vec3 cursor, float heading = 0f)
        => _server.Receive(ClientId, Message.Presence(ClientId, Name, selectionId, cursor, heading).Encode());

    // ---- Inbound from relay ----

    public void OnLine(string line)
    {
        Message m;
        try { m = Message.Decode(line); }
        catch { return; }

        switch (m.Type)
        {
            case MsgType.SyncBegin:
                _confirmed = new StaticObjectsFile();
                _pending.Clear();
                Doc = _confirmed;
                Ready = false;
                LastSeq = long.Parse(m.Args[0]);
                break;

            case MsgType.SyncObj:
                EditWire.Parse(m.Payload).Apply(_confirmed);
                break;

            case MsgType.SyncEnd:
                Ready = true;
                Doc = _confirmed;          // no pending right after sync
                break;

            case MsgType.Op:
            {
                long seq = long.Parse(m.Args[0]);
                string originClient = m.Args[1];
                long originOpId = long.Parse(m.Args[2]);
                LastSeq = seq;

                var cmd = EditWire.Parse(m.Payload);
                cmd.Apply(_confirmed);     // advance canonical state (same order on every client)

                // If this is the echo of one of our own ops, retire the matching prediction.
                if (originClient == ClientId)
                {
                    for (int i = 0; i < _pending.Count; i++)
                        if (_pending[i].LocalOpId == originOpId) { _pending.RemoveAt(i); break; }
                }

                Rebuild();
                OnApplied?.Invoke(cmd);
                break;
            }

            case MsgType.Presence:
            {
                string id = m.Args[0];
                if (id == ClientId) break;
                if (!_peers.TryGetValue(id, out var p)) { p = new Peer { ClientId = id }; _peers[id] = p; }
                p.Name = m.Args[1];
                p.SelectionId = m.Args[2];
                p.Cursor = Vec3.Parse(m.Args[3]);
                if (m.Args.Length > 4 && float.TryParse(m.Args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hd)) p.Heading = hd;
                break;
            }

            case MsgType.Leave:
                _peers.Remove(m.Args[0]);
                break;
        }
    }

    /// <summary>Rebuild the predicted Doc = confirmed + pending. Aliases confirmed when nothing is pending.</summary>
    private void Rebuild()
    {
        if (_pending.Count == 0) { Doc = _confirmed; return; }
        var view = _confirmed.Clone();
        foreach (var p in _pending)
            EditWire.Parse(p.Wire).Apply(view);   // fresh command instance => no stale pre-image state
        Doc = view;
    }
}
