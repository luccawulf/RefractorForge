using System.Net;
using System.Net.Sockets;
using System.Text;
using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using RefractorForge.Mcp;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// End-to-end collaboration: every kind of edit the editor and the AI bridge can make, driven through a REAL relay
/// over a real socket, and checked for three things that are cheap to assume and expensive to get wrong —
///
///   1. it reaches the relay's canonical world state, so a LATE JOINER and a restarted relay get it;
///   2. it reaches a second peer that is already connected;
///   3. it survives the trip intact.
///
/// The reason this file exists: an edit that writes local state and forgets to broadcast looks perfect on the
/// machine that made it, and a wire that carries only some of a record's fields quietly erases the rest on
/// everyone. Both of those shipped here, and neither was visible from a test that only drove one side.
/// </summary>
public class CollabIntegrationTests : IDisposable
{
    private readonly CollabWorldState _world = new()
    {
        Height = new Heightmap(64, 64),
        Material = new MaterialMap(64, 64),
    };
    private readonly StaticObjectsFile _doc = new();
    private readonly RelayServer _relay;
    private readonly TcpRelayHost _host;

    public CollabIntegrationTests()
    {
        _doc.Objects.Add(new StaticObject("hut") { Id = "seed-1", Position = new Vec3(10, 0, 10) });
        _relay = new RelayServer(_doc, _world);
        _host = new TcpRelayHost(_relay, IPAddress.Loopback, 0);
        _host.Start();
    }

    public void Dispose() => _host.Stop();

    static bool Wait(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    LiveBridge Bridge()
    {
        var b = new LiveBridge("127.0.0.1", _host.Port, null, "Claude");
        Assert.True(b.WaitSynced(TimeSpan.FromSeconds(10)), "the AI bridge never finished syncing");
        return b;
    }

    /// <summary>A bare peer that records every line the relay sends it — stands in for a second mapper.</summary>
    sealed class Watcher : IDisposable
    {
        private readonly TcpClient _sock;
        private readonly List<string> _lines = new();
        private readonly object _gate = new();

        public Watcher(int port, string id)
        {
            _sock = new TcpClient();
            _sock.Connect("127.0.0.1", port);
            var st = _sock.GetStream();
            var w = new StreamWriter(st, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(st, Encoding.UTF8);
            new Thread(() => { try { string? l; while ((l = r.ReadLine()) != null) lock (_gate) _lines.Add(l); } catch { } })
            { IsBackground = true }.Start();
            w.WriteLine(Message.Join(id, id).Encode());
        }

        public bool Saw(string needle) { lock (_gate) return _lines.Any(l => l.Contains(needle, StringComparison.Ordinal)); }
        public void Dispose() { try { _sock.Close(); } catch { } }
    }

    Watcher SyncedWatcher(string id)
    {
        var w = new Watcher(_host.Port, id);
        Assert.True(Wait(() => w.Saw("SYNCEND")), $"watcher '{id}' never finished syncing");
        return w;
    }

    [Fact]
    public void Terrain_sculpting_reaches_the_canonical_state_and_a_second_peer()
    {
        using var watcher = SyncedWatcher("mapper");
        using var b = Bridge();

        // One raised cell, sent exactly the way raise_mountain / flatten_area / carve_channel send theirs.
        b.SendWorldOp($"TERRAIN 5 7 1 1 {Convert.ToBase64String(new byte[] { 0x34, 0x12 })}");

        Assert.True(Wait(() => _world.Height![5, 7] == 0x1234), "the relay's canonical heightmap was not updated");
        Assert.True(Wait(() => watcher.Saw("TERRAIN 5 7 1 1")), "the other peer never saw the terrain edit");
    }

    [Fact]
    public void A_late_joiner_is_given_the_terrain_that_was_edited_before_it_arrived()
    {
        using (var b = Bridge())
        {
            b.SendWorldOp($"TERRAIN 9 9 1 1 {Convert.ToBase64String(new byte[] { 0x00, 0x20 })}");
            Assert.True(Wait(() => _world.Height![9, 9] == 0x2000));
        }

        // Someone connecting AFTERWARDS must be caught up from the world state, not left on the original ground.
        using var late = SyncedWatcher("late");
        Assert.True(late.Saw("TERRAIN"), "the late joiner was not sent the edited terrain");
    }

    [Fact]
    public void The_gameplay_layer_reaches_the_canonical_state_and_comes_back_whole()
    {
        using var watcher = SyncedWatcher("mapper");
        using var b = Bridge();

        var gp = new EditableGameplay(GameplayObjects.Empty);
        gp.Add(GpKind.ControlPoint, new ControlPointDef("base", new Vec3(1, 2, 3), 30f, SpawnGroupId: 4,
            Team: 1, ObjectSpawnerId: 9, TimeToGetControl: 33, OnlyTakableByTeam: 2));
        gp.Add(GpKind.Vehicle, new VehicleSpawnDef("sp", new Vec3(4, 5, 6), new Vec3(90, 0, 0), "Sherman", OsId: 9,
            Vehicle1: "Willy", Vehicle2: "Sherman", Team: 2, TimeToLive: 300, MaxNrOfObjectSpawned: 4));
        gp.Add(GpKind.Soldier, new SoldierSpawnDef("s1", new Vec3(7, 8, 9), Vec3.Zero, Group: 4, SpawnId: 11));

        b.SendWorldOp("GAMEPLAY " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(GameplaySync.Serialize(gp))));

        Assert.True(Wait(() => !string.IsNullOrEmpty(_world.Gameplay)), "the relay kept no gameplay state");
        Assert.True(Wait(() => watcher.Saw("GAMEPLAY ")), "the other peer never saw the gameplay edit");

        // The ids that tie a spawn or a spawner to its flag, and the per-team templates. These are exactly what the
        // wire used to drop — and because gameplay syncs as FULL STATE, a dropped field is erased for everyone.
        var back = new EditableGameplay(GameplayObjects.Empty);
        GameplaySync.Apply(back, _world.Gameplay!);
        Assert.Equal(9, back.ControlPoints[0].ObjectSpawnerId);
        Assert.Equal(4, back.ControlPoints[0].SpawnGroupId);
        Assert.Equal(33, back.ControlPoints[0].TimeToGetControl);
        Assert.Equal(2, back.ControlPoints[0].OnlyTakableByTeam);
        Assert.Equal("Willy", back.VehicleSpawns[0].Vehicle1);
        Assert.Equal(2, back.VehicleSpawns[0].Team);
        Assert.Equal(300, back.VehicleSpawns[0].TimeToLive);
        Assert.Equal(4, back.VehicleSpawns[0].MaxNrOfObjectSpawned);
        Assert.Equal(4, back.SoldierSpawns[0].Group);
        Assert.Equal(11, back.SoldierSpawns[0].SpawnId);
    }

    [Fact]
    public void Road_paint_reaches_other_peers_even_though_the_relay_does_not_store_it()
    {
        using var watcher = SyncedWatcher("mapper");
        using var b = Bridge();

        var samples = new List<RoadSample> { new(100, 0, 100, 4f, 0f), new(140, 0, 100, 4f, 40f) };
        b.SendWorldOp(RoadRaster.ToWire(RoadRaster.Paint(samples, (200, 180, 140))));

        // Connected peers get it — that is what makes a painted road appear live on the other machine.
        Assert.True(Wait(() => watcher.Saw("ATLAS ")), "the other peer never saw the road");

        // And the documented limit holds: ATLAS is NOT kept for replay, because the ground atlas is far too big to
        // carry in world state. A late joiner sees a painted road only after the level is saved and reloaded.
        Assert.DoesNotContain("ATLAS", string.Join('\n', _world.SnapshotOps()));
    }

    [Fact]
    public void Object_edits_reach_the_document_and_a_second_peer()
    {
        using var watcher = SyncedWatcher("mapper");
        using var b = Bridge();

        string id = b.Add("church", new Vec3(50, 1, 60), new Vec3(45, 0, 0));
        Assert.True(Wait(() => _relay.SnapshotDoc().FindById(id) is not null), "the relay document did not take it");
        Assert.True(Wait(() => watcher.Saw("ADD " + id)), "the other peer never saw the add");

        b.Move(id, new Vec3(70, 1, 80));
        Assert.True(Wait(() => _relay.SnapshotDoc().FindById(id)!.Position == new Vec3(70, 1, 80)), "move did not land");
        Assert.True(Wait(() => watcher.Saw("MOVE " + id)), "the other peer never saw the move");

        b.Delete(id);
        Assert.True(Wait(() => _relay.SnapshotDoc().FindById(id) is null), "delete did not land");
        Assert.True(Wait(() => watcher.Saw("DEL " + id)), "the other peer never saw the delete");
    }

    [Fact]
    public void Water_and_material_edits_reach_the_canonical_state()
    {
        using var b = Bridge();

        b.SendWorldOp("WATER 42.5");
        Assert.True(Wait(() => _world.Water == "WATER 42.5"), "water op did not stick");

        b.SendWorldOp($"MATERIAL 0 2 3 1 1 {Convert.ToBase64String(new byte[] { 7 })}");
        Assert.True(Wait(() => _world.Material![2, 3] == 7), "material paint did not stick");
    }

    [Fact]
    public void Light_settings_reach_the_canonical_state_and_a_late_joiner()
    {
        using var watcher = SyncedWatcher("mapper");
        using (var b = Bridge())
        {
            // The four renderer.* colours are patched into Init.con on save, so they are map data: without this,
            // two people lighting the same map means whoever saves last silently discards the other's work.
            b.SendWorldOp("LIGHT 135 40 1 0.16 0.15 0.17 0.12 0.1 0.08 0.975 1 0.95 0.9 0.9 0.7");
            Assert.True(Wait(() => _world.Light is not null), "the relay kept no light settings");
            Assert.True(Wait(() => watcher.Saw("LIGHT ")), "the other peer never saw the light edit");
        }

        Assert.Contains(_world.SnapshotOps(), o => o.StartsWith("LIGHT"));
        using var late = SyncedWatcher("late");
        Assert.True(late.Saw("LIGHT "), "the late joiner was not sent the light settings");
    }

    [Fact]
    public void Two_peers_and_the_relay_agree_after_a_burst_of_mixed_edits()
    {
        using var b1 = Bridge();
        using var conn = new TcpClientConnection("127.0.0.1", _host.Port);
        var b2 = new CollabClient("mapper", "Mapper", conn);
        var worldSeen = new List<string>();
        b2.OnWorldOp = op => { lock (worldSeen) worldSeen.Add(op); };
        conn.Attach(b2);
        Assert.True(Wait(() => b2.Ready && b2.Doc.Objects.Count == 1), "the second peer never synced");

        // Both edit at once, mixing object and world ops.
        for (int i = 0; i < 10; i++)
        {
            b1.Add($"a{i}", new Vec3(i * 5, 0, 0), Vec3.Zero);
            b2.Add($"b{i}", new Vec3(0, 0, i * 5), Vec3.Zero);
            b1.SendWorldOp($"TERRAIN {i} 0 1 1 {Convert.ToBase64String(new byte[] { (byte)i, 0 })}");
        }

        // 1 seed + 10 + 10, and every peer must land on the same document.
        Assert.True(Wait(() => _relay.SnapshotDoc().Objects.Count == 21),
            $"the relay did not take every edit ({_relay.SnapshotDoc().Objects.Count}/21)");
        Assert.True(Wait(() => b1.Doc.Objects.Count == 21), $"peer 1 diverged ({b1.Doc.Objects.Count}/21)");
        Assert.True(Wait(() => b2.Doc.Objects.Count == 21), $"peer 2 diverged ({b2.Doc.Objects.Count}/21)");
        for (int i = 0; i < 10; i++) Assert.Equal((ushort)i, _world.Height![i, 0]);

        // The terrain ops interleaved with the object edits must have been HANDED to peer 2, not merely survived by
        // it. Until this was fixed, the first world op threw out of the socket read loop and froze the peer for good
        // — every later object edit silently stopped arriving, which reads on-screen as "the other person's edits
        // stopped showing up" rather than as an error.
        Assert.True(Wait(() => { lock (worldSeen) return worldSeen.Count(o => o.StartsWith("TERRAIN")) >= 10; }),
            "peer 2 was not handed the terrain ops");
    }

    [Fact]
    public void A_peer_joining_a_session_that_already_has_terrain_still_syncs()
    {
        // The relay replays the world state to a joiner as part of SYNC, so this is the very first thing a peer
        // meets when it joins a session where anyone has touched the ground.
        using (var seed = Bridge())
        {
            seed.SendWorldOp($"TERRAIN 1 1 1 1 {Convert.ToBase64String(new byte[] { 0x22, 0x22 })}");
            Assert.True(Wait(() => _world.Height![1, 1] == 0x2222));
        }

        using var conn = new TcpClientConnection("127.0.0.1", _host.Port);
        var peer = new CollabClient("joiner", "Joiner", conn);
        conn.Attach(peer);

        Assert.True(Wait(() => peer.Ready), "the joining peer never became Ready");
        Assert.True(peer.Doc.FindById("seed-1") is not null, "the joining peer did not get the object document");
    }

    [Fact]
    public void A_relay_restart_keeps_terrain_and_gameplay()
    {
        // The world state is what a restarted relay reloads from, so anything missing from it is lost on a restart.
        using (var b = Bridge())
        {
            b.SendWorldOp($"TERRAIN 3 4 1 1 {Convert.ToBase64String(new byte[] { 0x11, 0x11 })}");
            var gp = new EditableGameplay(GameplayObjects.Empty);
            gp.Add(GpKind.ControlPoint, new ControlPointDef("cp", new Vec3(1, 1, 1), 20f, SpawnGroupId: 3, ObjectSpawnerId: 5));
            b.SendWorldOp("GAMEPLAY " + Convert.ToBase64String(Encoding.UTF8.GetBytes(GameplaySync.Serialize(gp))));
            Assert.True(Wait(() => _world.Height![3, 4] == 0x1111 && !string.IsNullOrEmpty(_world.Gameplay)));
        }

        var dir = Path.Combine(Path.GetTempPath(), "rf_collab_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            _world.Save(dir);
            var reloaded = CollabWorldState.Load(dir);
            Assert.NotNull(reloaded);
            Assert.Equal(0x1111, reloaded!.Height![3, 4]);

            var back = new EditableGameplay(GameplayObjects.Empty);
            GameplaySync.Apply(back, reloaded.Gameplay!);
            Assert.Equal(5, back.ControlPoints[0].ObjectSpawnerId);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
