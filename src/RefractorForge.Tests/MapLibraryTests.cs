using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// One relay, many maps. A RelayServer already is a room - one document, one sequence, one base pin - so
/// hosting several is a directory of them rather than a rewrite. These cover the things that would go wrong
/// if that were sloppy: maps bleeding into each other, a name escaping its folder, and state surviving a
/// restart per map.
/// </summary>
public class MapLibraryTests : IDisposable
{
    private readonly string _tmp;
    public MapLibraryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "rf_maps_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private MapLibrary Lib(string? password = null) => new(Path.Combine(_tmp, "maps"), password, null);

    private sealed class Inbox : IClientEndpoint
    {
        public string ClientId { get; }
        public Inbox(string id) => ClientId = id;
        public void Deliver(string line) { }
    }

    [Theory]
    [InlineData("Saigon68", true)]
    [InlineData("al-vietnas_v2.1", true)]
    [InlineData("../../etc/passwd", false)]     // a name is a folder name; it must not escape the directory
    [InlineData("..", false)]
    [InlineData(".hidden", false)]
    [InlineData("has space", false)]            // it is also a single wire token
    [InlineData("", false)]
    public void A_map_name_has_to_be_safe_as_a_folder_and_as_a_token(string name, bool ok)
        => Assert.Equal(ok, MapLibrary.IsValidName(name));

    [Fact]
    public void Opening_an_unknown_name_creates_that_map_and_a_bad_name_creates_nothing()
    {
        var lib = Lib();
        Assert.Empty(lib.List());

        var room = lib.Open("Saigon68");
        Assert.NotNull(room);
        Assert.Equal("Saigon68", room!.Name);
        Assert.Single(lib.List());

        Assert.Null(lib.Open("../escape"));
        Assert.Single(lib.List());
        Assert.False(Directory.Exists(Path.Combine(_tmp, "escape")));
    }

    [Fact]
    public void The_same_name_is_the_same_room_whatever_its_case()
    {
        var lib = Lib();
        var a = lib.Open("Hue");
        var b = lib.Open("hue");
        Assert.Same(a, b);
        Assert.Equal(1, lib.RoomCount);
    }

    [Fact]
    public void Edits_in_one_map_never_reach_another()
    {
        // The whole point of rooms. Two relays behind one port must not share a document or a sequence.
        var lib = Lib();
        var one = lib.Open("MapOne")!;
        var two = lib.Open("MapTwo")!;

        var link = new LoopbackLink(one.Relay, "a");
        var client = new CollabClient("a", "mapper", link);
        link.Attach(client);
        client.Join();
        client.Add("hut_m1", new Vec3(10, 0, 10), Vec3.Zero);

        Assert.Single(one.Relay.SnapshotDoc().Objects);
        Assert.Empty(two.Relay.SnapshotDoc().Objects);
        Assert.True(one.Relay.Sequence > 0);
        Assert.Equal(0, two.Relay.Sequence);
    }

    [Fact]
    public void A_map_comes_back_after_a_restart_with_its_own_objects()
    {
        var dir = Path.Combine(_tmp, "maps");
        var lib = new MapLibrary(dir, null, null);
        var room = lib.Open("Saigon68")!;
        var link = new LoopbackLink(room.Relay, "a");
        var client = new CollabClient("a", "mapper", link);
        link.Attach(client);
        client.Join();
        client.Add("hut_m1", new Vec3(1, 2, 3), Vec3.Zero);
        client.Add("hut_m1", new Vec3(4, 5, 6), Vec3.Zero);
        lib.SaveAll();

        // A whole new library over the same folder is what a restart looks like.
        var again = new MapLibrary(dir, null, null);
        var listed = again.List();
        Assert.Single(listed);
        Assert.Equal("Saigon68", listed[0].Name);
        Assert.Equal(2, listed[0].Objects);                      // counted from disk, without loading the room
        Assert.Equal(2, again.Open("Saigon68")!.Relay.SnapshotDoc().Objects.Count);
    }

    [Fact]
    public void The_listing_counts_objects_without_standing_the_room_up()
    {
        var dir = Path.Combine(_tmp, "maps");
        Directory.CreateDirectory(Path.Combine(dir, "Hand_Made"));
        File.WriteAllText(Path.Combine(dir, "Hand_Made", "StaticObjects.con"),
            "object.create hut_m1\nobject.absolutePosition 1/2/3\nobject.create hut_m1\nobject.absolutePosition 4/5/6\n");
        var lib = new MapLibrary(dir, null, null);
        var e = Assert.Single(lib.List());
        Assert.Equal("Hand_Made", e.Name);
        Assert.Equal(2, e.Objects);
        Assert.Equal(0, lib.RoomCount);          // still not loaded: a listing must not cost a relay per map
    }

    [Fact]
    public void A_base_pin_is_remembered_per_map_across_a_restart()
    {
        var dir = Path.Combine(_tmp, "maps");
        var store = new BaseArchiveStore(Path.Combine(_tmp, "store"));
        var lib = new MapLibrary(dir, null, store);

        var src = Path.Combine(_tmp, "world.rfa");
        File.WriteAllBytes(src, Enumerable.Range(0, 40_000).Select(i => (byte)i).ToArray());
        var id = LevelBase.Identify(src);

        var room = lib.Open("Pinned")!;
        // The relay only answers a client it knows about, so register an endpoint before announcing.
        room.Relay.Register(new Inbox("a"));
        room.Relay.OnLine("a", Message.Base(id.Fingerprint, id.Bytes, id.LevelName).Encode());
        Assert.Equal(id.Fingerprint, room.Relay.BasePin?.Fingerprint);
        lib.SaveAll();

        var again = new MapLibrary(dir, null, store);
        Assert.Equal(id.Fingerprint, again.Open("Pinned")!.Relay.BasePin?.Fingerprint);
        // And an unpinned map is still unpinned: the pin is the map's, not the library's.
        Assert.Null(again.Open("Unpinned")!.Relay.BasePin);
    }

    [Fact]
    public void The_password_guards_the_library_before_any_map_is_named()
    {
        // The list of what a group is working on is itself worth not handing out, so auth comes first.
        var lib = Lib("hunter2");
        Assert.True(lib.RequiresAuth);
        Assert.True(lib.CheckAuth("hunter2"));
        Assert.False(lib.CheckAuth("wrong"));
        Assert.False(lib.CheckAuth(null));

        Assert.False(Lib().RequiresAuth);
        Assert.True(Lib().CheckAuth(null));
    }

    [Fact]
    public void Exporting_rebuilds_one_archive_that_carries_the_live_edits()
    {
        // The point of the whole split: the relay keeps a map as an immutable base plus a small delta, but a
        // person who just wants the level needs ONE file. This is that file, and it has to contain the edits.
        var baseRfa = FindRealArchive();
        if (baseRfa is null) return;   // no game on this machine: nothing real to repack, so nothing to prove

        var store = new BaseArchiveStore(Path.Combine(_tmp, "store"));
        string fp = store.Ingest(baseRfa!);
        var lib = new MapLibrary(Path.Combine(_tmp, "maps"), null, store);
        var room = lib.Open("Exported")!;
        room.Relay.Register(new Inbox("a"));
        room.Relay.OnLine("a", Message.Base(fp, new FileInfo(baseRfa!).Length, "Exported").Encode());

        var link = new LoopbackLink(room.Relay, "b");
        var client = new CollabClient("b", "mapper", link);
        link.Attach(client);
        client.Join();
        int before = room.Relay.SnapshotDoc().Objects.Count;
        client.Add("hut_m1", new Vec3(123, 45, 678), Vec3.Zero);

        string outPath = Path.Combine(_tmp, "out", "Exported.rfa");
        var id = MapExport.Build(room, store, outPath);

        Assert.NotNull(id);
        Assert.True(File.Exists(outPath));
        Assert.Null(RefractorFlatArchive.Validate(outPath));      // a real archive the engine could mount

        // Re-open it the way the editor would, and the edit is there.
        var arch = new RefractorFlatArchive(outPath);
        var entry = arch.Entries.First(e => e.Name.Replace(Chr92, Slash)
                                             .EndsWith("/StaticObjects.con", StringComparison.OrdinalIgnoreCase));
        var text = System.Text.Encoding.Latin1.GetString(arch.Read(entry));
        int after = text.Split('\n').Count(l => l.TrimStart().StartsWith("object.create", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before + 1, after);
        Assert.Contains("123", text);
    }

    [Fact]
    public void The_document_is_withheld_from_a_client_on_the_wrong_archive()
    {
        // What went wrong in the first real test: the relay streamed the map's objects the moment a client
        // picked it, then checked the archive afterwards. Another map's control points, water and growth landed
        // in the level the user had open. Attaching and streaming are now separate, with this decision between.
        var store = new BaseArchiveStore(Path.Combine(_tmp, "gate"));
        var lib = new MapLibrary(Path.Combine(_tmp, "gatemaps"), null, store);

        var right = Path.Combine(_tmp, "right.rfa");
        var wrong = Path.Combine(_tmp, "wrong.rfa");
        File.WriteAllBytes(right, Enumerable.Range(0, 30_000).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(wrong, Enumerable.Range(0, 30_000).Select(i => (byte)(i + 1)).ToArray());
        var rightId = LevelBase.Identify(right);
        var wrongId = LevelBase.Identify(wrong);

        var room = lib.Open("Gated")!;
        // Nothing pinned yet, so anyone may proceed - otherwise a brand new map could never be entered.
        Assert.True(room.Relay.BaseMatches(null));
        Assert.True(room.Relay.BaseMatches(wrongId));

        room.Relay.Register(new Inbox("a"));
        room.Relay.OnLine("a", Message.Base(rightId.Fingerprint, rightId.Bytes, "Gated").Encode());
        Assert.Equal(rightId.Fingerprint, room.Relay.BasePin?.Fingerprint);

        // Now it is pinned, the answers are what decides whether a client is handed the map.
        Assert.True(room.Relay.BaseMatches(rightId));
        Assert.False(room.Relay.BaseMatches(wrongId));
        Assert.False(room.Relay.BaseMatches(null));      // no level open is also not the session's archive
    }

    [Fact]
    public void Attaching_a_client_sends_it_nothing_until_the_state_is_streamed()
    {
        // The split that makes the gate possible: a client has to be reachable (so it can download an archive
        // it lacks) long before it is trusted with the document.
        var lib = Lib();
        var room = lib.Open("Split")!;
        var box = new Recorder("a");
        room.Relay.Attach(box);
        Assert.Empty(box.Lines);

        room.Relay.StreamStateTo("a");
        Assert.Contains(box.Lines, l => l.StartsWith("SYNCBEGIN"));
        Assert.Contains(box.Lines, l => l.StartsWith("SYNCEND"));
    }

    private sealed class Recorder : IClientEndpoint
    {
        public string ClientId { get; }
        public List<string> Lines { get; } = new();
        public Recorder(string id) => ClientId = id;
        public void Deliver(string line) => Lines.Add(line);
    }

    [Fact]
    public void Export_refuses_when_the_map_has_no_base_archive_to_build_from()
    {
        // A session with no pin is a pile of edits with no ground under them. Handing that over as "the map"
        // would be worse than saying no.
        var store = new BaseArchiveStore(Path.Combine(_tmp, "store2"));
        var lib = new MapLibrary(Path.Combine(_tmp, "maps2"), null, store);
        var room = lib.Open("Groundless")!;
        Assert.Null(MapExport.Build(room, store, Path.Combine(_tmp, "nope.rfa")));
        Assert.False(File.Exists(Path.Combine(_tmp, "nope.rfa")));
    }

    /// <summary>A real level archive to rebuild from, if this machine has the game. The export is a repack of a
    /// genuine archive, so a synthetic file would not exercise it.</summary>
    private const char Chr92 = (char)92;   // a backslash, spelled this way so no escaping question can arise
    private const char Slash = '/';

    private static string? FindRealArchive()
    {
        foreach (var dir in new[]
                 {
                     @"D:\Games\EA GAMES\Battlefield Vietnam\Mods\BfVietnam\Archives\BfVietnam\Levels",
                 })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                // The smallest one: this test repacks it, and a 383 MB repack in a unit test is rude.
                // Smallest first, but it has to be a real level: many small .rfa in there are _001 patches that
                // carry no StaticObjects.con at all, and repacking one proves nothing about an export.
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.rfa")
                                                        .Where(f => f.Length > 50_000 && f.Length < 12_000_000)
                                                        .OrderBy(f => f.Length))
                {
                    try
                    {
                        var a = new RefractorFlatArchive(f.FullName);
                        if (a.Entries.Any(e => e.Name.Replace(Chr92, Slash)
                                                .EndsWith("/StaticObjects.con", StringComparison.OrdinalIgnoreCase)))
                            return f.FullName;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    [Fact]
    public void A_map_gets_its_own_backups_under_its_own_folder()
    {
        var dir = Path.Combine(_tmp, "maps");
        var lib = new MapLibrary(dir, null, null, keepBackups: 4, backupMaxMb: 64);
        var room = lib.Open("Backed")!;
        var link = new LoopbackLink(room.Relay, "a");
        var client = new CollabClient("a", "m", link);
        link.Attach(client);
        client.Join();
        client.Add("hut_m1", Vec3.Zero, Vec3.Zero);

        lib.Tick(backupMinutes: 0);   // due immediately
        Assert.True(Directory.Exists(Path.Combine(dir, "Backed", "_backups")));
        Assert.NotEmpty(Directory.EnumerateDirectories(Path.Combine(dir, "Backed", "_backups")));
        // and nothing was written for a map that has had no edits
        Assert.False(Directory.Exists(Path.Combine(dir, "Untouched")));
    }
}
