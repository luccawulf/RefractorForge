using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Connecting to an empty relay wiped the level: the joiner adopted the relay's (empty) document, was then asked to
/// seed the relay, and uploaded its now-empty list. The settings it also uploaded made the session count as "seeded",
/// so after a restart the relay never asked again and every later join was wiped too. The editor side now holds its
/// objects until the sync proves there is a document to adopt; this covers the relay side - when it asks for a seed.
/// </summary>
public class RelaySeedingTests
{
    private sealed class Inbox : IClientEndpoint
    {
        public string ClientId { get; }
        public List<string> Lines { get; } = new();
        public Inbox(string id) => ClientId = id;
        public void Deliver(string line) => Lines.Add(line);
        public bool AskedToSeed => Lines.Any(l => Message.Decode(l).Type == MsgType.SeedRequest);
    }

    [Fact]
    public void An_empty_relay_asks_its_first_client_to_seed_it()
    {
        var relay = new RelayServer(null, new CollabWorldState());
        var a = new Inbox("a"); relay.Register(a);
        Assert.True(a.AskedToSeed);
        // only the first one: two seeders would upload the same level twice
        var b = new Inbox("b"); relay.Register(b);
        Assert.False(b.AskedToSeed);
    }

    [Fact]
    public void A_session_holding_only_settings_is_not_seeded_and_still_asks()
    {
        // What a wiped client left behind: water and light ops, no objects, no terrain.
        var world = new CollabWorldState();
        world.ApplyOp("WATER 30 c=0.1/0.2/0.3");
        world.ApplyOp("LIGHT 45 30 1 0.2 0.2 0.2 0.3 0.3 0.3 1 1 1 0.5 0.5 0.5");
        Assert.True(world.Any);
        Assert.False(world.HasLevelContent);

        var relay = new RelayServer(new StaticObjectsFile(), world);
        var a = new Inbox("a"); relay.Register(a);
        Assert.True(a.AskedToSeed);
    }

    [Fact]
    public void A_relay_holding_objects_does_not_ask()
    {
        var doc = new StaticObjectsFile();
        new RefractorForge.Formats.Editing.AddObject("o1", "O_HueHouse_B", new Vec3(1, 2, 3), Vec3.Zero).Apply(doc);
        var relay = new RelayServer(doc, new CollabWorldState());
        var a = new Inbox("a"); relay.Register(a);
        Assert.False(a.AskedToSeed);
        // and the joiner is handed that document to adopt
        Assert.Contains(a.Lines, l => { var m = Message.Decode(l); return m.Type == MsgType.SyncObj && m.Payload.StartsWith("ADD o1 "); });
    }

    [Fact]
    public void A_relay_holding_terrain_but_no_objects_does_not_ask()
    {
        // An objectless map is still a map: its terrain came from somewhere and must not be overwritten by a seed.
        var world = new CollabWorldState { Height = new Heightmap(4, 4) };
        var relay = new RelayServer(null, world);
        var a = new Inbox("a"); relay.Register(a);
        Assert.False(a.AskedToSeed);
    }
}
