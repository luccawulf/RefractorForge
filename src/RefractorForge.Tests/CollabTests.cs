using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

public class CollabTests
{
    static string DocText(StaticObjectsFile f) => string.Join("\n", f.Write());

    static CollabClient Connect(RelayServer relay, string id, string name)
    {
        var link = new LoopbackLink(relay, id);
        var client = new CollabClient(id, name, link);
        link.Attach(client);
        client.Join();
        return client;
    }

    [Fact]
    public void Convergence_concurrent_edits_late_join_presence_stress()
    {
        var seed = new StaticObjectsFile();
        seed.Objects.Add(new StaticObject("hut")    { Id = "base-1", Position = new Vec3(10, 0, 10) });
        seed.Objects.Add(new StaticObject("tower")  { Id = "base-2", Position = new Vec3(20, 0, 5)  });
        seed.Objects.Add(new StaticObject("bunker") { Id = "base-3", Position = new Vec3(0, 0, 30)  });
        var relay = new RelayServer(seed);

        var a = Connect(relay, "A", "Ann");
        var b = Connect(relay, "B", "Bo");
        var c = Connect(relay, "C", "Cy");
        Assert.True(a.Doc.Objects.Count == 3 && b.Doc.Objects.Count == 3 && c.Doc.Objects.Count == 3, "all three synced to the seed");

        a.Move("base-1", new Vec3(11, 0, 10));
        b.Move("base-1", new Vec3(99, 0, 99));
        string addedByA = a.Add("sandbag", new Vec3(5, 0, 5), Vec3.Zero);
        string addedByB = b.Add("sandbag", new Vec3(6, 0, 6), Vec3.Zero);
        c.Rotate("base-2", new Vec3(0, 45, 0));
        a.Scale("base-3", 2.0f);
        b.Delete("base-3");

        Assert.True(addedByA != addedByB, "namespaced add ids differ");

        string sv = DocText(relay.SnapshotDoc());
        Assert.True(DocText(a.Doc) == sv, "client A == server");
        Assert.True(DocText(b.Doc) == sv, "client B == server");
        Assert.True(DocText(c.Doc) == sv, "client C == server");
        Assert.True(relay.SnapshotDoc().FindById("base-1")?.Position == new Vec3(99, 0, 99), "conflict resolved by relay order");
        Assert.True(relay.SnapshotDoc().FindById("base-3") is null, "delete beat scale");
        Assert.True(relay.SnapshotDoc().Objects.Count == 4, "two sandbags added");

        // Scenario 2: late joiner
        var d = Connect(relay, "D", "Di");
        Assert.True(DocText(d.Doc) == DocText(relay.SnapshotDoc()), "late joiner D == server");
        d.Move("base-2", new Vec3(7, 7, 7));
        sv = DocText(relay.SnapshotDoc());
        Assert.True(DocText(a.Doc) == sv, "after D's edit: A==server");
        Assert.True(DocText(b.Doc) == sv, "after D's edit: B==server");
        Assert.True(DocText(c.Doc) == sv, "after D's edit: C==server");
        Assert.True(DocText(d.Doc) == sv, "after D's edit: D==server");

        // Scenario 3: presence
        string docBefore = DocText(relay.SnapshotDoc());
        a.UpdatePresence("base-1", new Vec3(50, 0, 60));
        Assert.True(b.Peers.TryGetValue("A", out var pa) && pa.Cursor == new Vec3(50, 0, 60), "B sees A's cursor");
        Assert.True(c.Peers.TryGetValue("A", out var pc) && pc.SelectionId == "base-1", "C sees A's selection");
        Assert.True(DocText(relay.SnapshotDoc()) == docBefore, "presence did not alter the document");

        // Scenario 4: stress 200 ops
        var rng = new Random(1234);
        var clients = new[] { a, b, c };
        for (int i = 0; i < 200; i++)
        {
            var cl = clients[rng.Next(clients.Length)];
            int pick = rng.Next(5);
            var live = relay.SnapshotDoc().Objects.Select(o => o.Id).ToList();
            string tid = live.Count > 0 ? live[rng.Next(live.Count)] : "base-1";
            switch (pick)
            {
                case 0: cl.Move(tid, new Vec3(rng.Next(100), 0, rng.Next(100))); break;
                case 1: cl.Rotate(tid, new Vec3(0, rng.Next(360), 0)); break;
                case 2: cl.Scale(tid, 0.5f + (float)rng.NextDouble() * 3f); break;
                case 3: cl.Add("crate", new Vec3(rng.Next(100), 0, rng.Next(100)), Vec3.Zero); break;
                case 4: if (live.Count > 4) cl.Delete(tid); break;
            }
        }
        sv = DocText(relay.SnapshotDoc());
        Assert.True(DocText(a.Doc) == sv, "stress: A==server");
        Assert.True(DocText(b.Doc) == sv, "stress: B==server");
        Assert.True(DocText(c.Doc) == sv, "stress: C==server");
        Assert.True(DocText(d.Doc) == sv, "stress: D==server");
    }

    [Fact]
    public void Real_tcp_socket_convergence()
    {
        var tcpSeed = new StaticObjectsFile();
        tcpSeed.Objects.Add(new StaticObject("hut") { Id = "t-1", Position = new Vec3(1, 0, 1) });
        var tcpRelay = new RelayServer(tcpSeed);
        var host = new TcpRelayHost(tcpRelay, System.Net.IPAddress.Loopback, 0);
        host.Start();
        int port = host.Port;

        var connA = new TcpClientConnection("127.0.0.1", port);
        var ta = new CollabClient("TA", "TcpAnn", connA); connA.Attach(ta);
        var connB = new TcpClientConnection("127.0.0.1", port);
        var tb = new CollabClient("TB", "TcpBo", connB); connB.Attach(tb);

        bool Wait(Func<bool> cond, int ms = 2000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
            return cond();
        }

        Assert.True(Wait(() => ta.Doc.Objects.Count == 1 && tb.Doc.Objects.Count == 1), "TCP: both clients synced seed");
        ta.Move("t-1", new Vec3(5, 0, 5));
        string tadd = tb.Add("crate", new Vec3(9, 0, 9), Vec3.Zero);
        Assert.True(Wait(() => tb.Doc.FindById("t-1")?.Position == new Vec3(5, 0, 5)), "TCP: A's move reached B");
        Assert.True(Wait(() => ta.Doc.FindById(tadd) is not null), "TCP: B's add reached A");
        Assert.True(Wait(() => DocText(ta.Doc) == DocText(tcpRelay.SnapshotDoc())), "TCP: A == server");
        Assert.True(Wait(() => DocText(tb.Doc) == DocText(tcpRelay.SnapshotDoc())), "TCP: B == server");

        connA.Dispose(); connB.Dispose(); host.Stop();
    }

    [Fact]
    public void Optimistic_prediction_and_reconciliation()
    {
        var seed6 = new StaticObjectsFile();
        seed6.Objects.Add(new StaticObject("hut") { Id = "p-1", Position = new Vec3(0, 0, 0) });
        var relay6 = new RelayServer(seed6);

        var plink = new QueuingLink(relay6, "P");
        var p = new CollabClient("P", "Pat", plink);
        plink.Attach(p); p.Join(); plink.FlushAll();
        var r = Connect(relay6, "R", "Rex");
        plink.FlushAll();
        var w = Connect(relay6, "W", "Wendy");
        plink.FlushAll();

        Assert.True(p.Doc.Objects.Count == 1 && p.PendingCount == 0, "P synced seed, nothing pending");
        Assert.True(plink.Pending == 0, "P inbound queue empty");

        p.Move("p-1", new Vec3(42, 0, 0));
        Assert.True(p.Doc.FindById("p-1")?.Position == new Vec3(42, 0, 0), "prediction visible instantly");
        Assert.True(p.PendingCount == 1, "local op is pending");
        Assert.True(plink.Pending == 1, "predicting client holds 1 queued inbound");

        r.Move("p-1", new Vec3(0, 0, 7));
        Assert.True(w.PendingCount == 0, "watcher stayed on fast path");
        Assert.True(DocText(w.Doc) == DocText(relay6.SnapshotDoc()), "watcher already reflects canonical");
        Assert.True(p.Doc.FindById("p-1")?.Position == new Vec3(42, 0, 0), "P still optimistically shows its own move");

        plink.FlushAll();
        Assert.True(DocText(p.Doc) == DocText(relay6.SnapshotDoc()), "after reconcile: P == server");
        Assert.True(p.PendingCount == 0, "pending drained to zero");
        Assert.True(p.Doc.FindById("p-1")?.Position == new Vec3(0, 0, 7), "canonical winner applied on P");

        string addId = p.Add("crate", new Vec3(1, 0, 1), Vec3.Zero);
        p.Move("p-1", new Vec3(5, 0, 5));
        Assert.True(p.PendingCount == 2, "two local ops pending");
        Assert.True(p.Doc.FindById(addId) is not null && p.Doc.FindById("p-1")?.Position == new Vec3(5, 0, 5), "both predictions visible");
        r.Rotate("p-1", new Vec3(0, 90, 0));
        plink.FlushAll();
        Assert.True(DocText(p.Doc) == DocText(relay6.SnapshotDoc()), "after full reconcile: P == server");
        Assert.True(DocText(p.Doc) == DocText(r.Doc) && DocText(r.Doc) == DocText(w.Doc), "all clients converged");
        Assert.True(p.PendingCount == 0, "P pending fully drained");
    }

    [Fact]
    public void Central_server_seeding()
    {
        var emptyRelay = new RelayServer();
        var s1 = new CapturingEndpoint("S1");
        emptyRelay.Register(s1);
        Assert.True(s1.Lines.Contains("SEEDREQ"), "first client of an EMPTY relay receives SEEDREQ");

        emptyRelay.OnLine("S1", Message.Op(0, "S1", 1, new AddObject("seed-1", "hut", new Vec3(3, 0, 4), Vec3.Zero).ToWire()).Encode());
        Assert.True(emptyRelay.SnapshotDoc().FindById("seed-1") is not null, "seed op becomes canonical");

        var s2 = new CapturingEndpoint("S2");
        emptyRelay.Register(s2);
        Assert.True(!s2.Lines.Contains("SEEDREQ"), "second client is NOT asked to seed");
        Assert.True(s2.Lines.Any(l => l.StartsWith("SYNCOBJ") && l.Contains("seed-1")), "second client adopts the seeded object");

        var seed = new StaticObjectsFile();
        seed.Objects.Add(new StaticObject("hut") { Id = "base-1" });
        seed.Objects.Add(new StaticObject("tower") { Id = "base-2" });
        seed.Objects.Add(new StaticObject("bunker") { Id = "base-3" });
        var seededRelay = new RelayServer(seed);
        var s3 = new CapturingEndpoint("S3");
        seededRelay.Register(s3);
        Assert.True(!s3.Lines.Contains("SEEDREQ"), "first client of a SEEDED relay is NOT asked to seed");
        Assert.True(s3.Lines.Count(l => l.StartsWith("SYNCOBJ")) >= 3, "first client of a SEEDED relay adopts its objects");
    }

    [Fact]
    public void Server_side_persistence()
    {
        var live = new RelayServer();
        var pep = new CapturingEndpoint("P1");
        live.Register(pep);
        live.OnLine("P1", Message.Op(0, "P1", 1, new AddObject("persist-1", "tower", new Vec3(7, 0, 8), Vec3.Zero).ToWire()).Encode());
        live.OnLine("P1", Message.Op(0, "P1", 2, new AddObject("persist-2", "hut", new Vec3(1, 0, 2), Vec3.Zero).ToWire()).Encode());
        live.OnLine("P1", Message.Op(0, "P1", 3, new ScaleObject("persist-1", 1.5f).ToWire()).Encode());

        var stateFile = Path.Combine(Path.GetTempPath(), "rf_relay_state.con");
        live.SnapshotDoc().Save(stateFile);
        var restarted = new RelayServer(StaticObjectsFile.Load(stateFile));

        Assert.True(DocText(restarted.SnapshotDoc()) == DocText(live.SnapshotDoc()), "relay state survives restart");
        var rdoc = restarted.SnapshotDoc();
        Assert.True(rdoc.Objects.Count == 2 && rdoc.Objects.FirstOrDefault(o => o.Template == "tower")?.Scale == 1.5f, "reloaded keeps objects + scale");
        var cc = new CapturingEndpoint("P2"); restarted.Register(cc);
        Assert.True(!cc.Lines.Contains("SEEDREQ"), "reloaded relay does not re-ask for seed");
        File.Delete(stateFile);
    }

    [Fact]
    public void Terrain_material_gameplay_relay_and_persistence()
    {
        var world = new CollabWorldState
        {
            Height = new Heightmap(8, 8),
            Material = new MaterialMap(8, 8),
            Gameplay = "VS tank1 10/0/20 0/0/0 Sheridan 0\n",
        };
        var wrelay = new RelayServer(new StaticObjectsFile(), world);

        wrelay.OnLine("C", Message.Op(0, "C", 1, "TERRAIN 1 1 2 2 " + Convert.ToBase64String(new byte[] { 0x10, 0x27, 0x10, 0x27, 0x10, 0x27, 0x10, 0x27 })).Encode());
        wrelay.OnLine("C", Message.Op(0, "C", 2, "MATERIAL 0 0 0 8 8 " + Convert.ToBase64String(Enumerable.Repeat((byte)5, 64).ToArray())).Encode());
        wrelay.OnLine("C", Message.Op(0, "C", 3, "GAMEPLAY " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "VS tank1 10/0/20 0/0/0 Sheridan 0\nVS tank2 30/0/40 0/0/0 PBR 1\n"))).Encode());

        Assert.True(world.Height![1, 1] == 0x2710, "relay applied terrain edit");
        Assert.True(world.Material![3, 3] == 5, "relay applied material edit");
        Assert.True(world.Gameplay!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "relay applied gameplay edit");

        var joiner = new CapturingEndpoint("J");
        wrelay.Register(joiner);
        Assert.True(joiner.Lines.Any(l => l.StartsWith("SYNCOBJ TERRAIN")), "late joiner replayed TERRAIN");
        Assert.True(joiner.Lines.Any(l => l.StartsWith("SYNCOBJ MATERIAL 0")), "late joiner replayed MATERIAL");
        Assert.True(joiner.Lines.Any(l => l.StartsWith("SYNCOBJ GAMEPLAY")), "late joiner replayed GAMEPLAY");

        var stateDir = Path.Combine(Path.GetTempPath(), "rf_world_state");
        if (Directory.Exists(stateDir)) Directory.Delete(stateDir, true);
        world.Save(stateDir);
        var reloaded = CollabWorldState.Load(stateDir);
        Assert.True(reloaded?.Height?[1, 1] == 0x2710, "terrain persists across restart");
        Assert.True(reloaded?.Material?[3, 3] == 5, "material persists across restart");
        Assert.True((reloaded?.Gameplay?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length ?? 0) == 2, "gameplay persists across restart");
        Directory.Delete(stateDir, true);
    }

    sealed class CapturingEndpoint : IClientEndpoint
    {
        public string ClientId { get; }
        public List<string> Lines { get; } = new();
        public CapturingEndpoint(string id) => ClientId = id;
        public void Deliver(string line) => Lines.Add(line);
    }

    sealed class QueuingLink : IClientEndpoint, IServerEndpoint
    {
        public string ClientId { get; }
        private readonly RelayServer _server;
        private CollabClient? _client;
        private readonly Queue<string> _inbound = new();
        public QueuingLink(RelayServer server, string clientId) { _server = server; ClientId = clientId; }
        public void Attach(CollabClient client) { _client = client; _server.Register(this); }
        public int Pending => _inbound.Count;
        public bool FlushOne() { if (_inbound.Count == 0) return false; _client?.OnLine(_inbound.Dequeue()); return true; }
        public void FlushAll() { while (_inbound.Count > 0) _client?.OnLine(_inbound.Dequeue()); }
        public void Deliver(string line) => _inbound.Enqueue(line);
        public void Receive(string clientId, string line) => _server.OnLine(clientId, line);
    }
}
