using RefractorForge.Collab;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;
using Xunit;

namespace RefractorForge.Tests;

public class CollabNetTests
{
    [Fact]
    public void CollabAdmin_kick_and_reconnect_preserves_doc()
    {
        var server = new RelayServer();
        var linkA = new LoopbackLink(server, "alice");
        var linkA2 = new LoopbackLink(server, "alice");
        var linkB = new LoopbackLink(server, "bob");

        var ccA = new CollabClient("alice", "Alice", linkA);
        var ccB = new CollabClient("bob", "Bob", linkB);
        linkA.Attach(ccA);
        linkB.Attach(ccB);

        var id = ccA.Add("tank_m1", new Vec3(50, 0, 50), Vec3.Zero);
        Assert.True(ccA.Doc.FindById(id) is not null, "Alice sees her own tank");
        Assert.True(ccB.Doc.FindById(id) is not null, "Bob sees Alice's tank");

        string? kicked = server.Kick("alice");
        Assert.True(kicked is not null, "Kick returned the client's name (not null)");
        Assert.True(server.ClientCount == 1, $"only Bob remains after kick ({server.ClientCount})");

        var snap = server.SnapshotDoc();
        Assert.True(snap.FindById(id)?.Template == "tank_m1",
            "canonical doc on server still has the pre-kick tank");

        var ccA2 = new CollabClient("alice", "Alice2", linkA2);
        linkA2.Attach(ccA2);
        Assert.True(ccA2.Ready, "reconnected client is Ready after sync");
        Assert.True(ccA2.Doc.FindById(id)?.Template == "tank_m1",
            "reconnected Alice gets the existing doc state (tank present)");
        Assert.True(server.ClientCount == 2, $"server has 2 clients again ({server.ClientCount})");

        Assert.True(server.RequiresAuth == false, "open relay: RequiresAuth is false");
        Assert.True(server.CheckAuth(null) && server.CheckAuth("anything"), "open relay: any auth passes");
        var pwdServer = new RelayServer(password: "s3cr3t");
        Assert.True(pwdServer.RequiresAuth, "password relay: RequiresAuth is true");
        Assert.True(pwdServer.CheckAuth("s3cr3t"), "correct password accepted");
        Assert.True(!pwdServer.CheckAuth("wrong"), "wrong password rejected");
        Assert.True(!pwdServer.CheckAuth(null), "null password rejected when password set");
    }

    [Fact]
    public void CollabSync_terrain_material_water_presence_and_persistence()
    {
        var world = new CollabWorldState();
        var hm = new Heightmap(16, 16);
        hm[4, 4] = 30000; hm[5, 4] = 28000; hm[4, 5] = 27000;
        world.Height = hm;
        var mat = new MaterialMap(16, 16);
        mat[4, 4] = 7; mat[5, 5] = 9;
        world.Material = mat;

        var ops = world.SnapshotOps().ToList();
        Assert.True(ops.Any(o => o.StartsWith("TERRAIN ")), "snapshot has TERRAIN op");
        Assert.True(ops.Any(o => o.StartsWith("MATERIAL 0 ")), "snapshot has MATERIAL 0 op");

        var world2 = new CollabWorldState { Height = new Heightmap(16, 16), Material = new MaterialMap(16, 16) };
        foreach (var op in ops) world2.ApplyOp(op);
        Assert.True(world2.Height![4, 4] == 30000, $"TERRAIN ApplyOp restores heights ({world2.Height[4, 4]})");
        Assert.True(world2.Material![4, 4] == 7 && world2.Material[5, 5] == 9,
            $"MATERIAL ApplyOp restores cells ({world2.Material[4, 4]},{world2.Material[5, 5]})");

        var worldW = new CollabWorldState();
        Assert.True(worldW.ApplyOp("WATER 22.5"), "WATER op recognised");
        Assert.True(worldW.Water == "WATER 22.5", $"water op set from op ({worldW.Water})");
        var wops = worldW.SnapshotOps().ToList();
        Assert.True(wops.Count == 1 && wops[0].StartsWith("WATER"), "water snapshots as WATER op");

        world.ApplyOp("WATER 33.0");
        Assert.True(world.Water == "WATER 33.0", $"WATER ApplyOp updates world ({world.Water})");

        string tmpDir = Path.Combine(Path.GetTempPath(), "rf_cns_" + Guid.NewGuid().ToString("N")[..8]);
        var server = new RelayServer(world: world);
        try
        {
            server.SaveState(tmpDir);
            Assert.True(File.Exists(Path.Combine(tmpDir, "Heightmap.raw")), "SaveState wrote Heightmap.raw");
            Assert.True(File.Exists(Path.Combine(tmpDir, "MaterialMap.raw")), "SaveState wrote MaterialMap.raw");
            Assert.True(File.Exists(Path.Combine(tmpDir, "water.txt")), "SaveState wrote water.txt");
            var loaded = CollabWorldState.Load(tmpDir);
            Assert.True(loaded is not null, "CollabWorldState.Load returns non-null");
            Assert.True(loaded!.Height is not null && loaded.Height[4, 4] == 30000, "height persists");
            Assert.True(loaded.Material is not null && loaded.Material[4, 4] == 7, "material persists");
            Assert.True(loaded.Water == "WATER 33.0", $"water persists ({loaded.Water})");
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }
}
