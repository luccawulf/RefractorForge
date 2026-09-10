using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The relay syncs edits; it never synced the ground they land on. Two people whose level archives differ
/// applied the identical ordered op stream to different worlds and neither was told - the one silent
/// corruption the protocol still had. These cover the fix end to end: announcing what you are standing on,
/// being told when it is the wrong thing, and being handed the right one.
/// </summary>
public class BaseArchiveSyncTests : IDisposable
{
    private readonly string _tmp;
    public BaseArchiveSyncTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "rf_base_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    /// <summary>A stand-in "archive": the transfer path only cares about bytes and their hash, and building a
    /// real multi-hundred-MB .rfa per test would make the suite unusable.</summary>
    private string MakeFile(string name, int bytes, byte fill)
    {
        var p = Path.Combine(_tmp, name);
        var data = new byte[bytes];
        for (int i = 0; i < bytes; i++) data[i] = (byte)(fill + (i % 251));
        File.WriteAllBytes(p, data);
        return p;
    }

    private sealed class Inbox : IClientEndpoint
    {
        public string ClientId { get; }
        public List<string> Lines { get; } = new();
        public Inbox(string id) => ClientId = id;
        public void Deliver(string line) => Lines.Add(line);
        public IEnumerable<Message> Msgs => Lines.Select(Message.Decode);
        public Message? First(MsgType t) => Msgs.Cast<Message?>().FirstOrDefault(m => m!.Value.Type == t);
    }

    // ---- identity ---------------------------------------------------------------------------------

    [Fact]
    public void The_same_bytes_fingerprint_the_same_and_one_changed_byte_does_not()
    {
        var a = MakeFile("a.rfa", 40_000, 7);
        var b = MakeFile("b.rfa", 40_000, 7);
        Assert.Equal(LevelBase.Fingerprint(a), LevelBase.Fingerprint(b));

        var bytes = File.ReadAllBytes(b);
        bytes[12_345] ^= 0xFF;                      // a single repainted terrain texel is this small a change
        File.WriteAllBytes(b, bytes);
        Assert.NotEqual(LevelBase.Fingerprint(a), LevelBase.Fingerprint(b));
    }

    [Fact]
    public void An_id_survives_the_wire_and_a_level_name_with_spaces_cannot_break_it()
    {
        var id = new LevelBase.Id(new string('a', 64), 383_475_658, "Al Vietnas Saigon");
        var back = LevelBase.Id.TryDecode(id.Encode());
        Assert.NotNull(back);
        Assert.Equal(id.Fingerprint, back!.Fingerprint);
        Assert.Equal(id.Bytes, back.Bytes);
        Assert.DoesNotContain(' ', back.LevelName);      // sanitised, so the three tokens stay three tokens
        Assert.Equal(12, id.Short.Length);
    }

    // ---- the store --------------------------------------------------------------------------------

    [Fact]
    public void The_store_only_accepts_bytes_that_hash_to_what_they_claimed()
    {
        var src = MakeFile("real.rfa", 300_000, 3);
        string fp = LevelBase.Fingerprint(src);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "store"));
        Assert.False(store.Has(fp));

        // A transfer that goes wrong halfway must leave nothing a later joiner could be handed.
        store.BeginReceive(fp);
        var all = File.ReadAllBytes(src);
        store.ReceiveChunk(fp, 0, all[..1000]);
        Assert.False(store.CompleteReceive(fp));
        Assert.False(store.Has(fp));

        // And the whole thing, in order, is accepted.
        store.BeginReceive(fp);
        int idx = 0;
        for (int off = 0; off < all.Length; off += BaseArchiveStore.ChunkBytes)
        {
            int n = Math.Min(BaseArchiveStore.ChunkBytes, all.Length - off);
            Assert.True(store.ReceiveChunk(fp, idx++, all[off..(off + n)]));
        }
        Assert.True(store.CompleteReceive(fp));
        Assert.True(store.Has(fp));
        Assert.Equal(all.Length, store.SizeOf(fp));
    }

    [Fact]
    public void An_out_of_order_chunk_aborts_rather_than_writing_a_scrambled_file()
    {
        var src = MakeFile("ooo.rfa", 400_000, 9);
        string fp = LevelBase.Fingerprint(src);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "store2"));
        var all = File.ReadAllBytes(src);
        store.BeginReceive(fp);
        Assert.True(store.ReceiveChunk(fp, 0, all[..BaseArchiveStore.ChunkBytes]));
        Assert.False(store.ReceiveChunk(fp, 5, all[..100]));     // skipped ahead
        Assert.False(store.CompleteReceive(fp));
        Assert.False(store.Has(fp));
    }

    // ---- the handshake ----------------------------------------------------------------------------

    [Fact]
    public void The_first_client_with_a_level_pins_the_session_and_is_asked_for_the_bytes()
    {
        var src = MakeFile("pin.rfa", 50_000, 1);
        var id = LevelBase.Identify(src);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "s3"));
        var relay = new RelayServer(new StaticObjectsFile(), null, null, store);

        var a = new Inbox("a"); relay.Register(a);
        relay.OnLine("a", Message.Base(id.Fingerprint, id.Bytes, id.LevelName).Encode());

        Assert.Equal(id.Fingerprint, relay.BasePin?.Fingerprint);
        Assert.NotNull(a.First(MsgType.BaseOk));
        Assert.NotNull(a.First(MsgType.BaseNeed));       // store is empty, so this client is asked to fill it
        Assert.False(relay.CanServeBase);
    }

    [Fact]
    public void A_second_client_on_a_different_archive_is_told_so_and_not_left_to_guess()
    {
        var mine = LevelBase.Identify(MakeFile("mine.rfa", 50_000, 1));
        var theirs = LevelBase.Identify(MakeFile("theirs.rfa", 60_000, 2));
        var relay = new RelayServer(new StaticObjectsFile(), null, null, new BaseArchiveStore(Path.Combine(_tmp, "s4")));

        var a = new Inbox("a"); relay.Register(a);
        relay.OnLine("a", Message.Base(mine.Fingerprint, mine.Bytes, mine.LevelName).Encode());

        var b = new Inbox("b"); relay.Register(b);
        relay.OnLine("b", Message.Base(theirs.Fingerprint, theirs.Bytes, theirs.LevelName).Encode());

        var diff = b.First(MsgType.BaseDiff);
        Assert.NotNull(diff);
        Assert.Equal(mine.Fingerprint, diff!.Value.Args[0]);   // told exactly what the session is on
        Assert.Equal("0", diff.Value.Args[3]);                 // and told the relay cannot serve it yet
        Assert.Null(b.First(MsgType.BaseOk));
    }

    [Fact]
    public void A_client_with_no_level_open_pins_nothing()
    {
        var relay = new RelayServer(new StaticObjectsFile(), null, null, new BaseArchiveStore(Path.Combine(_tmp, "s5")));
        var a = new Inbox("a"); relay.Register(a);
        relay.OnLine("a", Message.Base("-", 0, "-").Encode());
        Assert.Null(relay.BasePin);
        Assert.NotNull(a.First(MsgType.BaseOk));
    }

    [Fact]
    public void The_relay_refuses_an_upload_of_an_archive_it_is_not_pinned_to()
    {
        // Otherwise any client could push a file of its choosing into the store and have the next joiner install it.
        var pinned = LevelBase.Identify(MakeFile("pinned.rfa", 50_000, 1));
        var other = LevelBase.Identify(MakeFile("other.rfa", 50_000, 4));
        var store = new BaseArchiveStore(Path.Combine(_tmp, "s6"));
        var relay = new RelayServer(new StaticObjectsFile(), null, null, store);

        var a = new Inbox("a"); relay.Register(a);
        relay.OnLine("a", Message.Base(pinned.Fingerprint, pinned.Bytes, pinned.LevelName).Encode());

        relay.OnLine("a", Message.BasePut(other.Fingerprint, other.Bytes, other.LevelName).Encode());
        relay.OnLine("a", Message.BaseData(other.Fingerprint, 0, Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_tmp, "other.rfa")))).Encode());
        relay.OnLine("a", Message.BaseDone(other.Fingerprint).Encode());

        Assert.False(store.Has(other.Fingerprint));
        Assert.False(store.Has(pinned.Fingerprint));
    }

    // ---- the whole point: a joiner who has never seen the map ends up with it ----------------------

    [Fact]
    public void A_joiner_without_the_map_is_given_it_byte_for_byte()
    {
        var srcPath = MakeFile("world.rfa", 700_000, 5);       // several chunks, so ordering really is exercised
        var id = LevelBase.Identify(srcPath);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "s7"));
        var relay = new RelayServer(new StaticObjectsFile(), null, null, store);

        // The mapper joins, pins the session, and uploads.
        var linkA = new LoopbackLink(relay, "a");
        var a = new CollabClient("a", "mapper", linkA);
        string? uploadWanted = null;
        a.OnBaseWanted += pin => uploadWanted = pin.Fingerprint;
        linkA.Attach(a);
        a.AnnounceBase(srcPath);
        Assert.Equal(id.Fingerprint, uploadWanted);
        a.UploadBase(srcPath);
        Assert.True(store.Has(id.Fingerprint));
        Assert.True(relay.CanServeBase);

        // The friend joins with nothing at all, is told what the session is on, and asks for it.
        var linkB = new LoopbackLink(relay, "b");
        var b = new CollabClient("b", "friend", linkB);
        LevelBase.Id? told = null; bool available = false; string? landed = null; string? failure = null;
        b.OnBaseMismatch += (pin, avail) => { told = pin; available = avail; };
        b.OnBaseDownloaded += path => landed = path;
        b.OnBaseFailed += why => failure = why;
        linkB.Attach(b);
        b.AnnounceBase(null);

        // Having no level at all is still "not the session's archive", so the relay names what it is on and
        // says it can serve it. That is what makes the download possible without the friend knowing anything.
        Assert.Null(failure);
        Assert.NotNull(told);
        Assert.Equal(id.Fingerprint, told!.Fingerprint);
        Assert.True(available);
        var dest = Path.Combine(_tmp, "downloaded", "world.rfa");
        b.DownloadBase(dest);
        for (int i = 0; i < 200 && landed is null; i++) relay.PumpBaseSends();

        Assert.Null(failure);
        Assert.Equal(dest, landed);
        Assert.True(File.Exists(dest));
        Assert.Equal(File.ReadAllBytes(srcPath), File.ReadAllBytes(dest));
        Assert.Equal(id.Fingerprint, LevelBase.Fingerprint(dest));
        Assert.True(b.BaseAgreed);
        Assert.False(File.Exists(dest + ".part"));      // nothing half-written left behind
    }

    [Fact]
    public void A_joiner_on_the_wrong_archive_is_told_the_relay_can_serve_it()
    {
        var right = MakeFile("right.rfa", 300_000, 5);
        var wrong = MakeFile("wrong.rfa", 300_000, 6);
        var id = LevelBase.Identify(right);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "s8"));
        store.Ingest(right);
        var relay = new RelayServer(new StaticObjectsFile(), null, null, store,
                                    new LevelBase.Id(id.Fingerprint, id.Bytes, id.LevelName));

        var link = new LoopbackLink(relay, "b");
        var b = new CollabClient("b", "friend", link);
        LevelBase.Id? told = null; bool available = false;
        b.OnBaseMismatch += (pin, avail) => { told = pin; available = avail; };
        link.Attach(b);
        b.AnnounceBase(wrong);

        Assert.NotNull(told);
        Assert.Equal(id.Fingerprint, told!.Fingerprint);
        Assert.True(available);                 // the relay has it, so the warning comes with a way out
        Assert.False(b.BaseAgreed);

        string? landed = null;
        b.OnBaseDownloaded += pth => landed = pth;
        var dest = Path.Combine(_tmp, "fixed", "right.rfa");
        b.DownloadBase(dest);
        for (int i = 0; i < 200 && landed is null; i++) relay.PumpBaseSends();
        Assert.Equal(File.ReadAllBytes(right), File.ReadAllBytes(dest));
        Assert.True(b.BaseAgreed);
    }

    // ---- cancelling ---------------------------------------------------------------------------------

    [Fact]
    public void Cancelling_a_download_leaves_nothing_half_written_and_ignores_what_is_still_in_flight()
    {
        var srcPath = MakeFile("cancelme.rfa", 700_000, 5);
        var id = LevelBase.Identify(srcPath);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "s9"));
        store.Ingest(srcPath);
        var relay = new RelayServer(new StaticObjectsFile(), null, null, store,
                                    new LevelBase.Id(id.Fingerprint, id.Bytes, id.LevelName));

        var link = new LoopbackLink(relay, "b");
        var b = new CollabClient("b", "friend", link);
        string? landed = null;
        b.OnBaseDownloaded += pth => landed = pth;
        link.Attach(b);
        b.AnnounceBase(null);

        var dest = Path.Combine(_tmp, "cancelled", "world.rfa");
        b.DownloadBase(dest);
        relay.PumpBaseSends();                     // one chunk in, so there really is a part file to abandon
        Assert.True(File.Exists(dest + ".part"));

        b.CancelBaseTransfer();
        Assert.False(File.Exists(dest + ".part"));

        // The relay does not know yet and keeps sending. Those chunks must be dropped, not resurrect the file.
        for (int i = 0; i < 50; i++) relay.PumpBaseSends();
        Assert.Null(landed);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public void Bytes_moved_are_reported_while_a_download_runs()
    {
        var srcPath = MakeFile("counted.rfa", 500_000, 2);
        var id = LevelBase.Identify(srcPath);
        var store = new BaseArchiveStore(Path.Combine(_tmp, "s10"));
        store.Ingest(srcPath);
        var relay = new RelayServer(new StaticObjectsFile(), null, null, store,
                                    new LevelBase.Id(id.Fingerprint, id.Bytes, id.LevelName));

        var link = new LoopbackLink(relay, "b");
        var b = new CollabClient("b", "friend", link);
        var seen = new List<(long Done, long Total)>();
        b.OnBaseProgress += (d, t) => seen.Add((d, t));
        string? landed = null;
        b.OnBaseDownloaded += pth => landed = pth;
        link.Attach(b);
        b.AnnounceBase(null);
        b.DownloadBase(Path.Combine(_tmp, "counted-out", "world.rfa"));
        for (int i = 0; i < 200 && landed is null; i++) relay.PumpBaseSends();

        Assert.NotEmpty(seen);
        Assert.All(seen, x => Assert.Equal(id.Bytes, x.Total));             // the total is known from the pin
        Assert.Equal(seen.OrderBy(x => x.Done).Select(x => x.Done), seen.Select(x => x.Done));   // monotonic
        Assert.Equal(id.Bytes, seen[^1].Done);                             // and it finishes at 100%
    }

    // ---- what is worth shipping at all ------------------------------------------------------------

    [Fact]
    public void Baked_lightmaps_and_backup_folders_are_not_worth_shipping()
    {
        // On Saigon68 the object lightmaps are 172 MB of a 366 MB archive and the relay already replays a bake
        // as a trigger the peer re-runs, so shipping them is most of the transfer for none of the benefit.
        Assert.True(LevelBase.IsRegenerable("BfVietnam/levels/Saigon68/ObjectLightmaps/o_hut_1-2-3.tga"));
        Assert.True(LevelBase.IsRegenerable("BfVietnam/levels/Saigon68/ObjectLightMaps/x.tga"));
        Assert.True(LevelBase.IsRegenerable("BfVietnam/levels/Saigon68/BACKUP_SAIGON_TERRAIN/tx000x000.dds"));
        Assert.False(LevelBase.IsRegenerable("BfVietnam/levels/Saigon68/Texture/tx000x000.dds"));
        Assert.False(LevelBase.IsRegenerable("BfVietnam/levels/Saigon68/Init.con"));
        Assert.False(LevelBase.IsRegenerable("BfVietnam/levels/Saigon68/StandardMesh/foo.sm"));
    }
}
