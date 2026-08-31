using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Mcp;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The AI bridge against a REAL relay over a real socket — the same relay the editor stands up for "Collab ▸ AI
/// Bridge". These are the gates that matter: that an AI's placements reach the editor's document, that undo is a
/// real reversal, and that world ops (terrain, material, water, gameplay) pass by harmlessly. That last one is not
/// hypothetical: the relay replays the whole world as SYNCOBJ lines to every joiner, and CollabClient throws on
/// every one of those verbs — strip both the bridge's verb filter and its read-loop catch and four of these tests
/// fail on the sync alone, before a single object arrives.
/// </summary>
public class LiveBridgeTests : IDisposable
{
    private readonly CollabWorldState _world;
    private readonly RelayServer _relay;
    private readonly TcpRelayHost _host;
    private readonly StaticObjectsFile _doc = new();

    public LiveBridgeTests()
    {
        _doc.Objects.Add(new StaticObject("hut") { Id = "seed-1", Position = new Vec3(10, 0, 10) });
        _doc.Objects.Add(new StaticObject("palm") { Id = "seed-2", Position = new Vec3(20, 0, 20) });
        // A POPULATED world state, exactly as the editor's host builds. This matters: RelayServer.Register replays
        // every world op as a SYNCOBJ line to each joiner, so a bridge attaching to a session where anyone has
        // painted terrain meets world ops before it meets its first object.
        _world = new CollabWorldState
        {
            Height = new Heightmap(4, 4),
            Material = new MaterialMap(4, 4),
            Water = 30f,
            Overgrowth = "OVERGROWTH 1 4 0.5",
        };
        _relay = new RelayServer(_doc, _world);
        _host = new TcpRelayHost(_relay, System.Net.IPAddress.Loopback, 0);
        _host.Start();
    }

    public void Dispose() => _host.Stop();

    private LiveBridge Connect(string name = "Claude")
    {
        var b = new LiveBridge("127.0.0.1", _host.Port, null, name);
        Assert.True(b.WaitSynced(TimeSpan.FromSeconds(5)), "bridge synced with the relay");
        return b;
    }

    private static bool Wait(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    private int RelayCount() => _relay.SnapshotDoc().Objects.Count;
    private StaticObject? RelayFind(string id) => _relay.SnapshotDoc().FindById(id);

    [Fact]
    public void Attaching_adopts_the_editors_live_document()
    {
        using var b = Connect();
        Assert.Equal(2, b.Doc.Objects.Count);
        Assert.NotNull(b.Doc.FindById("seed-1"));
        Assert.Equal("palm", b.Doc.FindById("seed-2")!.Template);
        Assert.Null(b.Disconnected);
    }

    [Fact]
    public void A_placed_object_reaches_the_editors_document()
    {
        using var b = Connect();
        string id = b.Add("O_fishinghut02", new Vec3(100, 5, 200), new Vec3(45, 0, 0));

        Assert.True(Wait(() => RelayFind(id) is not null), "the add reached the relay");
        var o = RelayFind(id)!;
        Assert.Equal("O_fishinghut02", o.Template);
        Assert.Equal(new Vec3(100, 5, 200), o.Position);
        Assert.Equal(new Vec3(45, 0, 0), o.Rotation);
    }

    [Fact]
    public void Scale_rides_as_its_own_op_because_ADD_cannot_carry_it()
    {
        using var b = Connect();
        string id = b.Add("crate", new Vec3(1, 2, 3), Vec3.Zero, scale: 2.5f);
        Assert.True(Wait(() => RelayFind(id)?.Scale is { } s && MathF.Abs(s - 2.5f) < 1e-4f), "scale reached the relay");
    }

    [Fact]
    public void Transforms_and_delete_reach_the_editor()
    {
        using var b = Connect();

        Assert.True(b.Move("seed-1", new Vec3(50, 1, 60)));
        Assert.True(Wait(() => RelayFind("seed-1")!.Position == new Vec3(50, 1, 60)), "move");

        Assert.True(b.Rotate("seed-1", new Vec3(90, 0, 0)));
        Assert.True(Wait(() => RelayFind("seed-1")!.Rotation == new Vec3(90, 0, 0)), "rotate");

        Assert.True(b.Delete("seed-2"));
        Assert.True(Wait(() => RelayFind("seed-2") is null), "delete");

        // An id that is not there is a no-op the caller can see, not an exception.
        Assert.False(b.Move("nope", Vec3.Zero));
        Assert.False(b.Delete("nope"));
    }

    [Fact]
    public void Undo_puts_the_object_back_and_redo_replaces_it()
    {
        using var b = Connect();
        string id = b.Add("bunker", new Vec3(7, 8, 9), Vec3.Zero);
        Assert.True(Wait(() => RelayFind(id) is not null));

        Assert.Equal(1, b.Undo());
        Assert.True(Wait(() => RelayFind(id) is null), "undo removed it from the editor");

        Assert.Equal(1, b.Redo());
        Assert.True(Wait(() => RelayFind(id) is not null), "redo put it back");
        Assert.Equal(new Vec3(7, 8, 9), RelayFind(id)!.Position);
    }

    [Fact]
    public void Undoing_a_move_restores_the_position_it_had_before()
    {
        using var b = Connect();
        var before = RelayFind("seed-1")!.Position;

        b.Move("seed-1", new Vec3(500, 0, 500));
        Assert.True(Wait(() => RelayFind("seed-1")!.Position == new Vec3(500, 0, 500)));

        b.Undo();
        Assert.True(Wait(() => RelayFind("seed-1")!.Position == before), "undo restored the original position");
    }

    [Fact]
    public void A_generated_batch_is_one_undo_entry()
    {
        using var b = Connect();
        var items = Enumerable.Range(0, 40)
            .Select(i => ("hut2", new Vec3(i * 10, 0, i * 10), Vec3.Zero, 1f));
        var ids = b.AddMany(items);

        Assert.Equal(40, ids.Count);
        Assert.True(Wait(() => RelayCount() == 42), "all 40 reached the editor");
        Assert.Equal(1, b.UndoDepth);

        Assert.Equal(40, b.Undo());
        Assert.True(Wait(() => RelayCount() == 2), "one undo took the whole batch back");
        Assert.Equal(0, b.UndoDepth);
    }

    [Fact]
    public void A_new_edit_drops_the_redo_stack()
    {
        using var b = Connect();
        string a = b.Add("x1", Vec3.Zero, Vec3.Zero);
        b.Undo();
        Assert.Equal(1, b.RedoDepth);

        b.Add("x2", Vec3.Zero, Vec3.Zero);
        Assert.Equal(0, b.RedoDepth);
        Assert.True(Wait(() => RelayFind(a) is null), "the undone object stayed undone");
    }

    [Fact]
    public void World_ops_are_ignored_and_never_disturb_the_object_document()
    {
        // The relay replayed terrain/material/water/overgrowth as SYNCOBJ during Connect (see the ctor). If those
        // reached CollabClient the sync would throw partway through, so this assertion IS the sync-path check.
        using var b = Connect();
        Assert.Equal(2, b.Doc.Objects.Count);
        Assert.Null(b.Disconnected);

        // Now the live case: a second peer paints while the AI is attached. Every one of these is a verb
        // CollabClient cannot parse, arriving at the rate a brush stroke emits them.
        var painter = new TcpClientConnection("127.0.0.1", _host.Port);
        var pc = new CollabClient("painter", "Mapper", painter);
        painter.Attach(pc);
        Assert.True(Wait(() => pc.Doc.Objects.Count == 2), "painter synced");

        string rect = Convert.ToBase64String(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 });
        foreach (var world in new[]
        {
            $"TERRAIN 0 0 2 2 {rect}",
            $"MATERIAL 0 0 0 2 2 {Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })}",
            "WATER 42.5",
            "OVERGROWTH 1 4 0.5",
        })
        {
            painter.Receive("painter", Message.Op(0, "painter", 1, world).Encode());
        }

        Thread.Sleep(200);
        Assert.Null(b.Disconnected);
        Assert.True(b.Connected, "the AI is still attached after a terrain/material/water/overgrowth storm");
        Assert.Equal(2, b.Doc.Objects.Count);   // and none of it leaked into the object document

        // And it can still work.
        string id = b.Add("still_here", new Vec3(3, 3, 3), Vec3.Zero);
        Assert.True(Wait(() => RelayFind(id) is not null), "the AI can still place objects afterwards");

        painter.Dispose();
    }

    [Fact]
    public void A_human_edit_shows_up_in_the_ais_view_of_the_map()
    {
        using var b = Connect();
        var human = new TcpClientConnection("127.0.0.1", _host.Port);
        var hc = new CollabClient("human", "Mapper", human);
        human.Attach(hc);
        Assert.True(Wait(() => hc.Doc.Objects.Count == 2), "human synced");

        string hid = hc.Add("player_placed_house", new Vec3(64, 0, 64), Vec3.Zero);

        Assert.True(Wait(() => b.Doc.FindById(hid) is not null), "the AI sees what the human placed");
        Assert.Contains(b.Snapshot(), o => o.Template == "player_placed_house");

        human.Dispose();
    }

    [Fact]
    public void A_template_with_a_space_is_refused_instead_of_corrupting_the_wire()
    {
        using var b = Connect();
        // The wire is space-delimited with fixed positions, so "my house" would shift rotation into the template
        // slot and quietly place garbage. Fail loudly at the boundary.
        Assert.Throws<ArgumentException>(() => b.Add("my house", Vec3.Zero, Vec3.Zero));
        Assert.Throws<ArgumentException>(() => b.Add("   ", Vec3.Zero, Vec3.Zero));
        Assert.Null(b.Disconnected);
    }

    [Fact]
    public void Each_connection_mints_its_own_id_space_so_reconnects_do_not_collide()
    {
        var first = Connect();
        string a = first.Add("hut", new Vec3(1, 0, 1), Vec3.Zero);
        Assert.True(Wait(() => RelayFind(a) is not null));
        first.Dispose();

        // A fresh bridge restarts its per-client add counter at 1. If it reused the same client id the second add
        // would mint an id the relay already holds and be discarded in silence.
        using var second = Connect();
        string b2 = second.Add("hut", new Vec3(2, 0, 2), Vec3.Zero);
        Assert.NotEqual(a, b2);
        Assert.True(Wait(() => RelayFind(b2) is not null), "the reconnected bridge's add was not swallowed");
        Assert.True(Wait(() => RelayCount() == 4), "both adds survive");
    }

    [Fact]
    public void Attaching_to_nothing_fails_fast_rather_than_hanging()
    {
        // Port 1 has no relay. The tool surface turns this into "turn on Collab > AI Bridge", so it must throw
        // promptly rather than block an MCP request.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => new LiveBridge("127.0.0.1", 1, null, "Claude"));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"failed in {sw.ElapsedMilliseconds} ms");
    }
}
