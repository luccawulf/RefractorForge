using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Working on one server map at different times: an editor that changed the map while offline uploads exactly its
/// changes, one that was away downloads exactly everyone else's, a change both made differently is a conflict for a
/// person to settle, and the map the server hands out as one .rfa carries everything - ids, files, and a record that
/// it is in step. All of it over a real socket, against the multi-map server the central relay runs.
/// </summary>
public class MapSyncTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rf_mapsync_" + Guid.NewGuid().ToString("N"));
    public MapSyncTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { _stop = true; Thread.Sleep(20); try { Directory.Delete(_tmp, true); } catch { } }

    static bool Wait(Func<bool> cond, int ms = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    // ---- ids -------------------------------------------------------------------------------------------

    private const string Con = "object.create hut_m1\r\nobject.absolutePosition 1/2/3\r\nobject.rotation 0/0/0\r\n"
                             + "object.create wall_m1\r\nobject.absolutePosition 4.5/6/7\r\nobject.rotation 90/0/0\r\n";

    [Fact]
    public void An_ordinary_save_is_unchanged_and_a_synced_one_carries_ids_that_come_back()
    {
        var f = StaticObjectsFile.Parse(Con.Split("\r\n"));
        Assert.DoesNotContain(f.Write(), l => l.StartsWith("rem rfid:"));   // byte-for-byte what it always was

        f.AssignStableIds();
        Assert.Equal(new[] { "b0", "b1" }, f.Objects.Select(o => o.Id));
        f.PersistIds = true;
        var back = StaticObjectsFile.Parse(f.Write(true));
        Assert.Equal(new[] { "b0", "b1" }, back.Objects.Select(o => o.Id));
        Assert.All(back.Objects, o => Assert.True(o.IdFromFile));
        Assert.True(back.PersistIds);
        Assert.Equal(f.Write(true), back.Write(true));                         // no second copy of the id line
        Assert.DoesNotContain(back.Objects, o => o.ExtraLines.Any(l => l.Contains("rfid")));
    }

    [Fact]
    public void Two_machines_opening_the_same_file_agree_on_every_id()
    {
        var a = StaticObjectsFile.Parse(Con.Split("\r\n")); a.AssignStableIds();
        var b = StaticObjectsFile.Parse(Con.Split("\r\n")); b.AssignStableIds();
        Assert.Equal(a.Objects.Select(o => o.Id), b.Objects.Select(o => o.Id));
    }

    // ---- the plan -------------------------------------------------------------------------------------

    [Fact]
    public void The_plan_sorts_changes_by_side_and_leaves_matching_changes_alone()
    {
        var b = new Dictionary<string, string> { ["a"] = "1", ["b"] = "1", ["c"] = "1", ["same"] = "1", ["gone"] = "1" };
        var l = new Dictionary<string, string> { ["a"] = "2", ["b"] = "1", ["c"] = "4", ["same"] = "9", ["gone"] = "1", ["new"] = "1" };
        var s = new Dictionary<string, string> { ["a"] = "1", ["b"] = "3", ["c"] = "5", ["same"] = "9" };
        var p = SyncPlan.Compute(b, l, s);
        Assert.Equal(new[] { "a", "c", "new" }, p.LocalChanged);
        Assert.Equal(new[] { "b", "c", "gone" }, p.ServerChanged);       // "gone" was deleted on the server
        Assert.Equal(new[] { "c" }, p.Conflicts);                          // "same" changed both sides identically
    }

    [Theory]
    [InlineData("Textures/tx00x00.dds", true)]
    [InlineData("ObjectLightMaps/hut_m1_1-2-3.tga", true)]
    [InlineData("../escape.txt", false)]
    [InlineData("Textures/../../escape", false)]
    [InlineData("C:/Windows/x", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("Textures\\tx.dds", false)]
    [InlineData("a/b.part", false)]
    public void A_level_file_path_from_the_network_cannot_leave_its_folder(string rel, bool ok)
        => Assert.Equal(ok, FileStore.IsSafeRelative(rel));

    [Fact]
    public void The_version_the_history_and_the_files_survive_a_restart()
    {
        var dir = Path.Combine(_tmp, "map");
        var s1 = new MapStore(dir);
        s1.Journal.Add(1, 1000, "Bob", new[] { "o:b1" });
        s1.Journal.Add(2, 2000, "Bob", new[] { "h:0,0" });
        s1.Journal.Add(3, 3000, "Alice", new[] { "s:WATER" });
        s1.Files.Put("Textures/tx00x00.dds", new byte[] { 1, 2, 3 }, 2);
        s1.SaveSeq(3);

        var s2 = new MapStore(dir);
        Assert.Equal(s1.Epoch, s2.Epoch);
        Assert.Equal(3, s2.LoadSeq());
        var since1 = s2.Journal.Since(1, out bool complete);
        Assert.True(complete);
        Assert.Equal("Alice", since1[0].Author);                          // most recent first
        Assert.Equal(1, since1.Single(c => c.Author == "Bob").Count);
        Assert.Contains("terrain", since1.Single(c => c.Author == "Bob").Kinds);
        Assert.Equal(new byte[] { 1, 2, 3 }, s2.Files.Read("textures/TX00x00.dds"));   // paths ignore case
        Assert.False(s2.Files.Put("../x", new byte[1], 4));
    }

    // ---- end to end -----------------------------------------------------------------------------------

    /// <summary>A mapper's machine, stripped to what a sync needs: the map it has open and a socket.</summary>
    private sealed class Mapper : IDisposable
    {
        public readonly StaticObjectsFile Objects;
        public readonly CollabWorldState World;
        public readonly Dictionary<string, byte[]> Files;   // level path -> bytes
        public readonly MapSync Sync;
        private readonly TcpClient _sock;
        private readonly StreamWriter _w;
        private readonly ConcurrentQueue<string> _in = new();
        private readonly string _id;
        private long _op;

        public Mapper(string id, int port, string map, LevelBase.Id pin, (StaticObjectsFile, CollabWorldState, Dictionary<string, byte[]>) level)
        {
            _id = id;
            (Objects, World, Files) = level;
            _sock = new TcpClient();
            _sock.Connect("127.0.0.1", port);
            var st = _sock.GetStream();
            _w = new StreamWriter(st, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(st, Encoding.UTF8);
            new Thread(() => { try { string? l; while ((l = r.ReadLine()) != null) _in.Enqueue(l); } catch { } }) { IsBackground = true }.Start();
            Sync = new MapSync(Send);
            Send(Message.Join(id, id).Encode());
            Send(Message.PickMap(map).Encode());
            Send(Message.Base(pin.Fingerprint, pin.Bytes, pin.LevelName).Encode());
        }

        public void Send(string line) { lock (_w) _w.WriteLine(line); }
        public void SendOp(string op) => Send(Message.Op(0, _id, ++_op, op).Encode());

        /// <summary>Feed everything that has arrived through the sync, as the editor's drain does.</summary>
        public void Pump()
        {
            while (_in.TryDequeue(out var line))
            {
                Message m;
                try { m = Message.Decode(line); } catch { continue; }
                Sync.Handle(m);
            }
        }

        public bool PumpUntil(Func<bool> cond) => Wait(() => { Pump(); return cond(); });

        public Dictionary<string, string> FileHashes()
            => Files.ToDictionary(kv => kv.Key, kv => SyncKeys.Hash(kv.Value), StringComparer.OrdinalIgnoreCase);

        public SyncPlan Plan() => Sync.Plan(Objects, World, FileHashes(), out _);

        /// <summary>Carry the server's value of these keys into this copy, the way the editor does.</summary>
        public void Take(IEnumerable<string> keys)
        {
            var list = keys.ToList();
            foreach (var op in Sync.DownloadOps(list))
                if (EditWire.IsObjectOp(op)) EditWire.Parse(op).Apply(Objects); else World.ApplyOp(op);
            var want = Sync.FilesFor(list);
            var got = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            Sync.OnFile = (p, b) => got[p] = b;
            foreach (var p in want) Sync.RequestFile(p);
            Assert.True(PumpUntil(() => want.All(got.ContainsKey)), "requested files never arrived");
            foreach (var p in want) if (got[p] is { } bytes) Files[p] = bytes;
        }

        public void Give(IEnumerable<string> keys)
        {
            foreach (var op in MapSync.UploadOps(keys, Objects, World, FileHashes(), p => Files.TryGetValue(p, out var b) ? b : null))
                SendOp(op);
        }

        public void Dispose() { try { _sock.Close(); } catch { } }
    }

    private string MakeBase(string name)
    {
        var so = "object.create hut_m1\r\nobject.absolutePosition 10/20/30\r\nobject.rotation 0/0/0\r\n"
               + "object.create wall_m1\r\nobject.absolutePosition 40/20/30\r\nobject.rotation 45/0/0\r\n"
               + "object.create tower_m1\r\nobject.absolutePosition 70/20/30\r\nobject.rotation 0/0/0\r\n";
        var tile = new byte[512]; new Random(7).NextBytes(tile);
        var entries = new List<(string, byte[])>
        {
            ($"bfvietnam/levels/{name}/StaticObjects.con", Encoding.Latin1.GetBytes(so)),
            ($"bfvietnam/levels/{name}/Init/Terrain.con", "GeometryTemplate.materialSize 64\r\nGeometryTemplate.worldSize 256"u8.ToArray()),
            ($"bfvietnam/levels/{name}/Init.con", "game.setViewDistance 400\r\n"u8.ToArray()),
            ($"bfvietnam/levels/{name}/Heightmap.raw", new byte[64 * 64 * 2]),
            ($"bfvietnam/levels/{name}/MaterialMap.raw", new byte[64 * 64]),
            ($"bfvietnam/levels/{name}/Textures/tx00x00.dds", tile),
        };
        var p = Path.Combine(_tmp, name + ".rfa");
        RefractorFlatArchive.WriteFile(p, entries, compress: true, xPackId: XPackId.Default);
        return p;
    }

    /// <summary>What an editor has once it opens an archive: objects with stable ids, the world, and its files.</summary>
    private static (StaticObjectsFile, CollabWorldState, Dictionary<string, byte[]>) Open(string rfa)
    {
        var (o, w) = RelayHost.LoadFullLevel(rfa);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var arch = new RefractorFlatArchive(rfa);
        string prefix = RefractorForge.Formats.LevelSaver.ArchivePrefix(arch);
        foreach (var e in arch.Entries)
        {
            var rel = SyncKeys.LevelRelative(e.Name, prefix);
            if (rel is not null && !SyncKeys.IsStructuredEntry(rel)) files[rel] = arch.Read(e);
        }
        // An editor never keeps a record's ids unless the file carried them - exactly what AssignStableIds gives.
        foreach (var x in o!.Objects) x.IdFromFile = x.IdFromFile;
        return (o, w ?? new CollabWorldState(), files);
    }

    private (TcpRelayHost Host, MapLibrary Lib, LevelBase.Id Pin, string Base) Server(string name)
    {
        var basePath = MakeBase(name);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "store"));
        var maps = Path.Combine(_tmp, "maps");
        Directory.CreateDirectory(maps);
        File.Copy(basePath, Path.Combine(maps, name + ".rfa"));          // dropped in: that archive IS the map
        var lib = new MapLibrary(maps, null, store);
        var room = lib.Open(name)!;
        var host = new TcpRelayHost(lib, IPAddress.Loopback, 0);
        host.Start();
        // The central server's service loop: persists, and walks requested files and archives out.
        new Thread(() => { while (!_stop) { try { lib.Tick(5); } catch { } Thread.Sleep(3); } }) { IsBackground = true }.Start();
        return (host, lib, room.Relay.BasePin!, basePath);
    }

    private volatile bool _stop;

    [Fact]
    public void Offline_changes_go_up_exactly_and_come_down_exactly()
    {
        var (host, lib, pin, basePath) = Server("Ops");
        try
        {
            // ---- Ann worked offline on her copy of the map ----
            using var ann = new Mapper("ann", host.Port, "Ops", pin, Open(basePath));
            Assert.True(ann.PumpUntil(() => ann.Sync.Synced), "Ann never received the map");
            ann.Sync.RequestBaseline();
            Assert.True(ann.PumpUntil(() => ann.Sync.Baseline is not null || ann.Sync.BaselineError.Length > 0));
            Assert.Equal("", ann.Sync.BaselineError);

            ann.Objects.FindById("b1")!.Position = new Vec3(41, 20, 30);                    // moved the wall
            ann.Objects.Objects.Add(new StaticObject("crate_m1") { Id = "ann-1", Position = new Vec3(5, 5, 5) });
            ann.World.Height![3, 4] = 0x1234;                                              // raised a cell
            ann.Files["Textures/tx00x00.dds"] = new byte[] { 9, 9, 9 };                     // painted the ground
            ann.Files["Sound/new.ssc"] = "rem new sound"u8.ToArray();                        // added a file

            var plan = ann.Plan();
            Assert.Empty(plan.ServerChanged);
            Assert.Empty(plan.Conflicts);
            Assert.Equal(new[] { "f:sound/new.ssc", "f:textures/tx00x00.dds", "h:0,0", "o:ann-1", "o:b1" },
                         plan.LocalChanged.OrderBy(k => k, StringComparer.Ordinal));
            ann.Give(plan.LocalChanged);

            var room = lib.Open("Ops")!;
            Assert.True(Wait(() => room.Relay.SnapshotDoc().FindById("b1")?.Position.X == 41f), "the move never reached the server");
            Assert.True(Wait(() => room.Relay.FileList().Count == 2), "the files never reached the server");
            Assert.NotNull(room.Relay.SnapshotDoc().FindById("ann-1"));
            Assert.Equal(0x1234, room.Relay.SnapshotWorld()!.Height![3, 4]);

            // ---- Bob was away; his copy is the map as it started ----
            using var bob = new Mapper("bob", host.Port, "Ops", pin, Open(basePath));
            Assert.True(bob.PumpUntil(() => bob.Sync.Synced));
            Assert.True(bob.Sync.ServerSeq > 0, "the server's version should have moved");
            bob.Sync.RequestHistory(0);
            Assert.True(bob.PumpUntil(() => bob.Sync.History is not null));
            Assert.Equal("ann", Assert.Single(bob.Sync.History!).Author);

            bob.Sync.RequestBaseline();
            Assert.True(bob.PumpUntil(() => bob.Sync.Baseline is not null));
            var bp = bob.Plan();
            Assert.Empty(bp.LocalChanged);
            Assert.Equal(plan.LocalChanged.OrderBy(k => k, StringComparer.Ordinal), bp.ServerChanged.OrderBy(k => k, StringComparer.Ordinal));
            bob.Take(bp.ServerChanged);

            // Bob's copy is now the server's, key for key.
            Assert.True(bob.Plan().NothingToDo, "after downloading, nothing should differ");
            Assert.Equal(41f, bob.Objects.FindById("b1")!.Position.X);
            Assert.Equal(new byte[] { 9, 9, 9 }, bob.Files["Textures/tx00x00.dds"]);
            Assert.Equal(0x1234, bob.World.Height![3, 4]);
        }
        finally { host.Stop(); }
    }

    [Fact]
    public void A_thing_changed_differently_on_both_sides_is_a_conflict_not_a_silent_winner()
    {
        var (host, _, pin, basePath) = Server("Clash");
        try
        {
            using var ann = new Mapper("ann", host.Port, "Clash", pin, Open(basePath));
            Assert.True(ann.PumpUntil(() => ann.Sync.Synced));
            ann.Sync.RequestBaseline();
            Assert.True(ann.PumpUntil(() => ann.Sync.Baseline is not null));
            ann.Objects.FindById("b2")!.Position = new Vec3(71, 20, 30);
            ann.Give(ann.Plan().LocalChanged);

            using var bob = new Mapper("bob", host.Port, "Clash", pin, Open(basePath));
            Assert.True(bob.PumpUntil(() => bob.Sync.Synced && bob.Sync.ServerObjects.FindById("b2")?.Position.X == 71f));
            bob.Sync.RequestBaseline();
            Assert.True(bob.PumpUntil(() => bob.Sync.Baseline is not null));
            bob.Objects.FindById("b2")!.Position = new Vec3(69, 20, 30);             // Bob moved the same tower
            bob.Objects.FindById("b0")!.Position = new Vec3(11, 20, 30);             // and something Ann did not touch

            var p = bob.Plan();
            Assert.Equal(new[] { "o:b2" }, p.Conflicts);
            Assert.Contains("o:b0", p.LocalChanged);
            Assert.DoesNotContain("o:b0", p.ServerChanged);
        }
        finally { host.Stop(); }
    }

    /// <summary>The live server's maps were saved before ids existed, so the server numbers their objects by ITS
    /// file order - which is not the archive's once something was deleted. A mapper opening the untouched archive
    /// must still end up with exactly the server's map: no duplicates, nothing resurrected.</summary>
    [Fact]
    public void A_map_saved_before_ids_existed_still_lines_up_and_comes_down_exact()
    {
        var basePath = MakeBase("Old");                  // hut b0, wall b1, tower b2 in the archive
        var store = new BaseArchiveStore(Path.Combine(_tmp, "store"));
        var maps = Path.Combine(_tmp, "maps");
        var mapDir = Path.Combine(maps, "Old");
        Directory.CreateDirectory(mapDir);
        // The old server's own state: the wall deleted, the tower moved - no rfid lines, as every server saved it.
        File.WriteAllText(Path.Combine(mapDir, "StaticObjects.con"),
            "object.create hut_m1\r\nobject.absolutePosition 10/20/30\r\nobject.rotation 0/0/0\r\n" +
            "object.create tower_m1\r\nobject.absolutePosition 77/20/30\r\nobject.rotation 0/0/0\r\n");
        var fp = store.Ingest(basePath);
        var pin = new LevelBase.Id(fp, new FileInfo(basePath).Length, "Old");
        File.WriteAllText(Path.Combine(mapDir, "base.txt"), pin.Encode());
        var lib = new MapLibrary(maps, null, store);
        lib.Open("Old");
        var host = new TcpRelayHost(lib, IPAddress.Loopback, 0);
        host.Start();
        new Thread(() => { while (!_stop) { try { lib.Tick(5); } catch { } Thread.Sleep(3); } }) { IsBackground = true }.Start();
        try
        {
            using var dee = new Mapper("dee", host.Port, "Old", pin, Open(basePath));
            Assert.True(dee.PumpUntil(() => dee.Sync.Synced));
            dee.Sync.RequestBaseline();
            Assert.True(dee.PumpUntil(() => dee.Sync.Baseline is not null));
            var p = dee.Plan();
            Assert.Empty(p.LocalChanged);
            Assert.Empty(p.Conflicts);
            dee.Take(p.ServerChanged);

            var got = dee.Objects.Objects.Select(o => (o.Template, o.Position.X)).OrderBy(t => t.Template).ToList();
            Assert.Equal(new[] { ("hut_m1", 10f), ("tower_m1", 77f) }, got);
            Assert.True(dee.Plan().NothingToDo);
        }
        finally { host.Stop(); }
    }

    [Fact]
    public void A_file_sent_to_the_server_is_receipted_so_it_is_not_sent_again()
    {
        var (host, _, pin, basePath) = Server("Receipt");
        try
        {
            using var ann = new Mapper("ann", host.Port, "Receipt", pin, Open(basePath));
            Assert.True(ann.PumpUntil(() => ann.Sync.Synced));
            ann.SendOp(SyncKeys.FileOp("Sound/new.ssc", "rem hi"u8));
            Assert.True(ann.PumpUntil(() => ann.Sync.ServerFiles.ContainsKey("Sound/new.ssc")),
                        "the sender never learned the server holds its file");
            Assert.Equal(SyncKeys.Hash("rem hi"u8), ann.Sync.ServerFiles["Sound/new.ssc"].Hash);
            // A path that climbs out of the map's folder is not stored and not receipted.
            ann.SendOp(SyncKeys.FileOp("../../escape.txt", "x"u8));
            Thread.Sleep(300); ann.Pump();
            Assert.DoesNotContain(ann.Sync.ServerFiles.Keys, k => k.Contains("escape"));
        }
        finally { host.Stop(); }
    }

    [Fact]
    public void The_map_the_server_hands_out_carries_ids_files_and_a_record_that_it_is_in_step()
    {
        var (host, lib, pin, basePath) = Server("Hand");
        try
        {
            using var ann = new Mapper("ann", host.Port, "Hand", pin, Open(basePath));
            Assert.True(ann.PumpUntil(() => ann.Sync.Synced));
            ann.Sync.RequestBaseline();
            Assert.True(ann.PumpUntil(() => ann.Sync.Baseline is not null));
            ann.Objects.FindById("b0")!.Position = new Vec3(12, 20, 30);
            ann.Files["Textures/tx00x00.dds"] = new byte[] { 7, 7 };
            ann.Give(ann.Plan().LocalChanged);

            var room = lib.Open("Hand")!;
            Assert.True(Wait(() => room.Relay.FileList().Count == 1 && room.Relay.SnapshotDoc().FindById("b0")!.Position.X == 12f));
            var out1 = Path.Combine(_tmp, "Hand.current.rfa");
            var id = MapExport.Build(room, new BaseArchiveStore(Path.Combine(_tmp, "store")), out1);
            Assert.NotNull(id);

            // Opened by a mapper who has never been on this server, the file is already in step.
            var arch = new RefractorFlatArchive(out1);
            var rec = SyncRecord.Parse(arch.Read(arch.Entries.First(e => e.Name.EndsWith(SyncRecord.EntryLeaf))));
            Assert.NotNull(rec);
            Assert.True(rec!.IsFor("Hand", room.Relay.Epoch));
            Assert.Equal(room.Relay.Sequence, rec.Seq);
            Assert.Equal(pin.Fingerprint, rec.Base!.Fingerprint);

            var soText = Encoding.Latin1.GetString(arch.Read(arch.Entries.First(e => e.Name.EndsWith("StaticObjects.con"))));
            Assert.Contains("rem rfid:b0", soText);

            using var cam = new Mapper("cam", host.Port, "Hand", pin, Open(out1));
            Assert.True(cam.PumpUntil(() => cam.Sync.Synced));
            cam.Sync.UseRecord(rec);
            Assert.True(cam.Plan().NothingToDo, "a freshly downloaded map should need nothing either way");
            Assert.Equal(new byte[] { 7, 7 }, cam.Files["Textures/tx00x00.dds"]);
        }
        finally { host.Stop(); }
    }
}
