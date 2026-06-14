using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

// Headless proof that multiple clients editing the same map converge to an identical document.
// Uses the in-process (synchronous) transport so ordering is deterministic and every step is
// observable. The same RelayServer/CollabClient code runs unchanged over the TCP transport.

int failures = 0;
void Check(string label, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}"); if (!ok) failures++; }

static string DocText(StaticObjectsFile f) => string.Join("\n", f.Write());

// Helper: connect a client to a relay over the loopback transport.
static CollabClient Connect(RelayServer relay, string id, string name)
{
    var link = new LoopbackLink(relay, id);
    var client = new CollabClient(id, name, link);
    link.Attach(client);   // registers with relay -> initial state sync streamed in
    client.Join();         // announce presence
    return client;
}

Console.WriteLine("RefractorForge — collaboration convergence test\n");

// ---- Build an initial shared map (as if loaded from a StaticObjects.con). ----
var seed = new StaticObjectsFile();
seed.Objects.Add(new StaticObject("hut")    { Id = "base-1", Position = new Vec3(10, 0, 10) });
seed.Objects.Add(new StaticObject("tower")  { Id = "base-2", Position = new Vec3(20, 0, 5)  });
seed.Objects.Add(new StaticObject("bunker") { Id = "base-3", Position = new Vec3(0, 0, 30)  });

var relay = new RelayServer(seed);

Console.WriteLine("Scenario 1: three clients join, then make concurrent (interleaved) edits");
var a = Connect(relay, "A", "Ann");
var b = Connect(relay, "B", "Bo");
var c = Connect(relay, "C", "Cy");
Check("all three synced to the seed (3 objects each)",
    a.Doc.Objects.Count == 3 && b.Doc.Objects.Count == 3 && c.Doc.Objects.Count == 3);

// Interleaved edits, including two genuine conflicts on the same object/field.
a.Move("base-1", new Vec3(11, 0, 10));     // A and B both move base-1...
b.Move("base-1", new Vec3(99, 0, 99));     // ...B's op is sequenced later, so B wins everywhere.
string addedByA = a.Add("sandbag", new Vec3(5, 0, 5), Vec3.Zero);   // namespaced id A-1
string addedByB = b.Add("sandbag", new Vec3(6, 0, 6), Vec3.Zero);   // namespaced id B-1 (no collision)
c.Rotate("base-2", new Vec3(0, 45, 0));
a.Scale("base-3", 2.0f);                    // A scales base-3...
b.Delete("base-3");                         // ...B deletes it later; delete wins (object gone).

Check("namespaced add ids differ", addedByA != addedByB);

string sv = DocText(relay.SnapshotDoc());
Check("client A == server", DocText(a.Doc) == sv);
Check("client B == server", DocText(b.Doc) == sv);
Check("client C == server", DocText(c.Doc) == sv);
Check("conflict resolved by relay order (base-1 at 99/0/99)",
    relay.SnapshotDoc().FindById("base-1")?.Position == new Vec3(99, 0, 99));
Check("delete beat scale (base-3 gone)", relay.SnapshotDoc().FindById("base-3") is null);
Check("two sandbags added (object count = 2 original + 2 added)", relay.SnapshotDoc().Objects.Count == 4);

Console.WriteLine($"\n  final object count: {relay.SnapshotDoc().Objects.Count}, relay seq: {relay.Sequence}");

Console.WriteLine("\nScenario 2: a fourth client joins LATE and must catch up exactly");
var d = Connect(relay, "D", "Di");
Check("late joiner D == server immediately after sync", DocText(d.Doc) == DocText(relay.SnapshotDoc()));

// A live edit after D joined must reach everyone, including D.
d.Move("base-2", new Vec3(7, 7, 7));
sv = DocText(relay.SnapshotDoc());
Check("after D's edit: A==server", DocText(a.Doc) == sv);
Check("after D's edit: B==server", DocText(b.Doc) == sv);
Check("after D's edit: C==server", DocText(c.Doc) == sv);
Check("after D's edit: D==server", DocText(d.Doc) == sv);

Console.WriteLine("\nScenario 3: presence (cursors/selection) propagate but never touch the document");
string docBefore = DocText(relay.SnapshotDoc());
a.UpdatePresence("base-1", new Vec3(50, 0, 60));
Check("B sees peer A's cursor", b.Peers.TryGetValue("A", out var pa) && pa.Cursor == new Vec3(50, 0, 60));
Check("C sees peer A's selection = base-1", c.Peers.TryGetValue("A", out var pc) && pc.SelectionId == "base-1");
Check("presence did not alter the document", DocText(relay.SnapshotDoc()) == docBefore);

Console.WriteLine("\nScenario 4: stress — 200 randomized interleaved ops across 3 clients still converge");
var rng = new Random(1234);
var clients = new[] { a, b, c };
var ids = new List<string> { "base-1", "base-2" };
ids.AddRange(relay.SnapshotDoc().Objects.Select(o => o.Id));
ids = ids.Distinct().ToList();
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
Check("stress: A==server", DocText(a.Doc) == sv);
Check("stress: B==server", DocText(b.Doc) == sv);
Check("stress: C==server", DocText(c.Doc) == sv);
Check("stress: D==server", DocText(d.Doc) == sv);
Console.WriteLine($"  after 200 ops: {relay.SnapshotDoc().Objects.Count} objects, relay seq {relay.Sequence}");

Console.WriteLine("\nScenario 5: same code over a REAL TCP socket (localhost)");
{
    var tcpSeed = new StaticObjectsFile();
    tcpSeed.Objects.Add(new StaticObject("hut") { Id = "t-1", Position = new Vec3(1, 0, 1) });
    var tcpRelay = new RelayServer(tcpSeed);
    var host = new TcpRelayHost(tcpRelay, System.Net.IPAddress.Loopback, 0);
    host.Start();
    int port = host.Port;

    var connA = new TcpClientConnection("127.0.0.1", port);
    var ta = new CollabClient("TA", "TcpAnn", connA);
    connA.Attach(ta);
    var connB = new TcpClientConnection("127.0.0.1", port);
    var tb = new CollabClient("TB", "TcpBo", connB);
    connB.Attach(tb);

    bool Wait(Func<bool> cond, int ms = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    Check("TCP: both clients synced seed", Wait(() => ta.Doc.Objects.Count == 1 && tb.Doc.Objects.Count == 1));
    ta.Move("t-1", new Vec3(5, 0, 5));
    string tadd = tb.Add("crate", new Vec3(9, 0, 9), Vec3.Zero);
    Check("TCP: A's move reached B", Wait(() => tb.Doc.FindById("t-1")?.Position == new Vec3(5, 0, 5)));
    Check("TCP: B's add reached A", Wait(() => ta.Doc.FindById(tadd) is not null));
    Check("TCP: A == server", Wait(() => DocText(ta.Doc) == DocText(tcpRelay.SnapshotDoc())));
    Check("TCP: B == server", Wait(() => DocText(tb.Doc) == DocText(tcpRelay.SnapshotDoc())));

    connA.Dispose(); connB.Dispose(); host.Stop();
}

Console.WriteLine("\nScenario 6: optimistic local prediction — instant local feedback that reconciles to canonical");
{
    var seed6 = new StaticObjectsFile();
    seed6.Objects.Add(new StaticObject("hut") { Id = "p-1", Position = new Vec3(0, 0, 0) });
    var relay6 = new RelayServer(seed6);

    // P is on a QUEUING link: we decide when it sees relay traffic (so the prediction window is
    // observable). R is on the normal immediate loopback and acts as the concurrent remote editor.
    var plink = new QueuingLink(relay6, "P");
    var p = new CollabClient("P", "Pat", plink);
    plink.Attach(p); p.Join(); plink.FlushAll();          // catch up to the seed
    var r = Connect(relay6, "R", "Rex");
    plink.FlushAll();                                      // let P observe R's join/presence

    // A pure watcher: never edits, so it must never leave the zero-clone fast path.
    var w = Connect(relay6, "W", "Wendy");
    plink.FlushAll();                                     // drain all join/presence traffic first

    Check("P synced seed, nothing pending", p.Doc.Objects.Count == 1 && p.PendingCount == 0);
    Check("P inbound queue empty before prediction", plink.Pending == 0);

    // (a) A local edit is visible IMMEDIATELY — before the relay has echoed anything back.
    p.Move("p-1", new Vec3(42, 0, 0));
    Check("prediction visible instantly (before echo)", p.Doc.FindById("p-1")?.Position == new Vec3(42, 0, 0));
    Check("local op is pending (unacknowledged)", p.PendingCount == 1);
    Check("predicting client holds 1 queued inbound (its own echo)", plink.Pending == 1);

    // (b) A concurrent REMOTE edit lands. Canonical order is P's move (sequenced first, synchronously)
    //     then R's move — so by last-writer-wins R's value must win once everything reconciles.
    r.Move("p-1", new Vec3(0, 0, 7));
    Check("watcher stayed on fast path (no pending, no clone)", w.PendingCount == 0);
    Check("watcher already reflects canonical", DocText(w.Doc) == DocText(relay6.SnapshotDoc()));
    Check("P still optimistically shows its own move pre-reconcile", p.Doc.FindById("p-1")?.Position == new Vec3(42, 0, 0));

    // (c) Now deliver P's queued traffic (its own echo, then R's op) in canonical order.
    plink.FlushAll();
    Check("after reconcile: P == server", DocText(p.Doc) == DocText(relay6.SnapshotDoc()));
    Check("pending drained to zero", p.PendingCount == 0);
    Check("canonical (relay-order) winner applied on P", p.Doc.FindById("p-1")?.Position == new Vec3(0, 0, 7));

    // (d) Prediction survives MULTIPLE outstanding local ops and a remote op interleaved between them.
    string addId = p.Add("crate", new Vec3(1, 0, 1), Vec3.Zero);   // pending #1
    p.Move("p-1", new Vec3(5, 0, 5));                               // pending #2
    Check("two local ops pending", p.PendingCount == 2);
    Check("both predictions visible", p.Doc.FindById(addId) is not null && p.Doc.FindById("p-1")?.Position == new Vec3(5, 0, 5));
    r.Rotate("p-1", new Vec3(0, 90, 0));                            // remote op while P has pending
    plink.FlushAll();
    Check("after full reconcile: P == server", DocText(p.Doc) == DocText(relay6.SnapshotDoc()));
    Check("all clients converged (P==R==W)", DocText(p.Doc) == DocText(r.Doc) && DocText(r.Doc) == DocText(w.Doc));
    Check("P pending fully drained", p.PendingCount == 0);
}

Console.WriteLine("\nScenario: central-server seeding (empty relay asks its first client; a seeded relay does not)");
{
    // EMPTY relay (a fresh central server) -> the FIRST client is asked to seed; later clients adopt and are not asked.
    var emptyRelay = new RelayServer();
    var s1 = new CapturingEndpoint("S1");
    emptyRelay.Register(s1);
    Check("first client of an EMPTY relay receives SEEDREQ", s1.Lines.Contains("SEEDREQ"));

    // Simulate the first client responding to the seed request by uploading one object.
    emptyRelay.OnLine("S1", Message.Op(0, "S1", 1, new AddObject("seed-1", "hut", new Vec3(3, 0, 4), Vec3.Zero).ToWire()).Encode());
    Check("seed op becomes canonical", emptyRelay.SnapshotDoc().FindById("seed-1") is not null);

    var s2 = new CapturingEndpoint("S2");
    emptyRelay.Register(s2);
    Check("second client is NOT asked to seed", !s2.Lines.Contains("SEEDREQ"));
    Check("second client adopts the seeded object", s2.Lines.Any(l => l.StartsWith("SYNCOBJ") && l.Contains("seed-1")));

    // SEEDED relay (started with a known document) -> nobody is asked to seed; the first client adopts it.
    var seededRelay = new RelayServer(seed);   // 'seed' = the 3-object map from the start of this test
    var s3 = new CapturingEndpoint("S3");
    seededRelay.Register(s3);
    Check("first client of a SEEDED relay is NOT asked to seed", !s3.Lines.Contains("SEEDREQ"));
    Check("first client of a SEEDED relay adopts its objects", s3.Lines.Count(l => l.StartsWith("SYNCOBJ")) >= 3);
}

Console.WriteLine("\nScenario: server-side persistence (edits -> save -> reload into a fresh relay)");
{
    var live = new RelayServer();
    var pep = new CapturingEndpoint("P1");
    live.Register(pep);
    live.OnLine("P1", Message.Op(0, "P1", 1, new AddObject("persist-1", "tower", new Vec3(7, 0, 8), Vec3.Zero).ToWire()).Encode());
    live.OnLine("P1", Message.Op(0, "P1", 2, new AddObject("persist-2", "hut", new Vec3(1, 0, 2), Vec3.Zero).ToWire()).Encode());
    live.OnLine("P1", Message.Op(0, "P1", 3, new ScaleObject("persist-1", 1.5f).ToWire()).Encode());

    var stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_relay_state.con");
    live.SnapshotDoc().Save(stateFile);                                  // what RunRelay's debounced saver writes
    var restarted = new RelayServer(StaticObjectsFile.Load(stateFile));  // what RunRelay loads on restart

    Check("relay state survives a restart (save -> reload matches)", DocText(restarted.SnapshotDoc()) == DocText(live.SnapshotDoc()));
    // NB: the .con format carries no id field (ids are session-local), so reloaded objects get fresh ids — match
    // by template instead. The relay re-syncs its objects (with the new ids) to clients on join, so this is correct.
    var rdoc = restarted.SnapshotDoc();
    Check("reloaded relay keeps both objects + the scale edit",
        rdoc.Objects.Count == 2 && rdoc.Objects.FirstOrDefault(o => o.Template == "tower")?.Scale == 1.5f);
    var cc = new CapturingEndpoint("P2"); restarted.Register(cc);
    Check("a relay reloaded from state is NOT empty -> does not re-ask for a seed", !cc.Lines.Contains("SEEDREQ"));
    System.IO.File.Delete(stateFile);
}

Console.WriteLine("\nScenario: relay holds + replays + persists terrain / material / gameplay (vehicles), not just objects");
{
    var world = new CollabWorldState
    {
        Height = new Heightmap(8, 8),
        Material = new MaterialMap(8, 8),
        Gameplay = "VS tank1 10/0/20 0/0/0 Sheridan 0\n",
    };
    var wrelay = new RelayServer(new StaticObjectsFile(), world);

    // A client edits terrain (a 2x2 rect of height 0x2710), material (fill 5), and gameplay (add a 2nd vehicle).
    wrelay.OnLine("C", Message.Op(0, "C", 1, "TERRAIN 1 1 2 2 " + Convert.ToBase64String(new byte[] { 0x10, 0x27, 0x10, 0x27, 0x10, 0x27, 0x10, 0x27 })).Encode());
    wrelay.OnLine("C", Message.Op(0, "C", 2, "MATERIAL 0 0 0 8 8 " + Convert.ToBase64String(Enumerable.Repeat((byte)5, 64).ToArray())).Encode());
    wrelay.OnLine("C", Message.Op(0, "C", 3, "GAMEPLAY " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
        "VS tank1 10/0/20 0/0/0 Sheridan 0\nVS tank2 30/0/40 0/0/0 PBR 1\n"))).Encode());

    Check("relay applied the terrain edit", world.Height![1, 1] == 0x2710);
    Check("relay applied the material edit", world.Material![3, 3] == 5);
    Check("relay applied the gameplay edit (2 vehicles)", world.Gameplay!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2);

    // A LATE JOINER is replayed all three layers in its initial sync (previously: objects only).
    var joiner = new CapturingEndpoint("J");
    wrelay.Register(joiner);
    Check("late joiner replayed TERRAIN", joiner.Lines.Any(l => l.StartsWith("SYNCOBJ TERRAIN")));
    Check("late joiner replayed MATERIAL", joiner.Lines.Any(l => l.StartsWith("SYNCOBJ MATERIAL 0")));
    Check("late joiner replayed GAMEPLAY (vehicles)", joiner.Lines.Any(l => l.StartsWith("SYNCOBJ GAMEPLAY")));

    // SAVE the world to a state folder, reload it (a restarted server) -> all three layers survive.
    var stateDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_world_state");
    if (System.IO.Directory.Exists(stateDir)) System.IO.Directory.Delete(stateDir, true);
    world.Save(stateDir);
    var reloaded = CollabWorldState.Load(stateDir);
    Check("terrain persists across restart", reloaded?.Height?[1, 1] == 0x2710);
    Check("material persists across restart", reloaded?.Material?[3, 3] == 5);
    Check("gameplay/vehicles persist across restart", (reloaded?.Gameplay?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length ?? 0) == 2);
    System.IO.Directory.Delete(stateDir, true);
}

Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED")}");
return failures == 0 ? 0 : 1;

// ---- Test-only endpoint that just records every line the relay delivers (for asserting SEEDREQ/SYNCOBJ). ----
sealed class CapturingEndpoint : IClientEndpoint
{
    public string ClientId { get; }
    public List<string> Lines { get; } = new();
    public CapturingEndpoint(string id) => ClientId = id;
    public void Deliver(string line) => Lines.Add(line);
}

// ---- Test-only transport: buffers relay->client delivery so the prediction window is observable. ----
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

    public void Deliver(string line) => _inbound.Enqueue(line);        // relay -> client: deferred
    public void Receive(string clientId, string line) => _server.OnLine(clientId, line);  // client -> relay: immediate
}
