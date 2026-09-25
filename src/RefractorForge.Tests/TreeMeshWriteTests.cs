using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// BF1942's tree meshes written as well as read: every retail <c>.tm</c> (111 of them) back to the same bytes - header, both
/// boxes, the material groups, the collision section whole (vertices, faces, the per-face plane records, the BSP), and
/// every vertex's extra word and second UV - and a tree scaled, and a collision hull built, parsing back as made.
/// Runs against the clean BF1942 install's tree archive when it is there and does nothing when it is not.
/// </summary>
public class TreeMeshWriteTests
{
    private const string TreeArchive = @"D:\Games\EA GAMES\Battlefield 1942 Clean\Battlefield 1942\Mods\bf1942\Archives\treeMesh.rfa";

    private static IEnumerable<(string Name, byte[] Bytes)> Trees()
    {
        if (!File.Exists(TreeArchive)) yield break;
        var archive = new RefractorFlatArchive(TreeArchive);
        foreach (var e in archive.Entries.Where(e => e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)))
            yield return (e.Name, archive.Read(e));
    }

    [Fact]
    public void Every_retail_tree_writes_back_to_the_same_bytes()
    {
        int n = 0, withCollision = 0;
        foreach (var (name, bytes) in Trees())
        {
            var tm = TreeMesh.Parse(bytes);
            Assert.Equal(bytes.Length, tm.Consumed);
            Assert.True(bytes.AsSpan().SequenceEqual(tm.Write()), name);
            if (tm.HasCollision) withCollision++;
            n++;
        }
        if (n == 0) return;                                            // no install here
        Assert.Equal(111, n);
        Assert.Equal(78, withCollision);
    }

    [Fact]
    public void A_scaled_tree_keeps_its_collision_with_it()
    {
        var (name, bytes) = Trees().FirstOrDefault(t => TreeMesh.Parse(t.Bytes).HasCollision);
        if (name is null) return;
        var tm = TreeMesh.Parse(bytes);
        var before = tm.CollisionVertices.Select(v => v.Y).Max();
        var top = tm.Max.Y;
        tm.Scale(1.5f);
        var back = TreeMesh.Parse(tm.Write());
        Assert.Equal(top * 1.5f, back.Max.Y, 3);
        Assert.Equal(before * 1.5f, back.CollisionVertices.Select(v => v.Y).Max(), 3);
        Assert.Equal(tm.Collision!.Root.Max.Y, back.Collision!.Root.Max.Y, 3);
    }

    [Fact]
    public void A_collision_hull_is_built_as_a_one_node_bush_has_it()
    {
        // A box: 8 corners, 12 faces.
        var p = new List<Vec3>();
        for (int i = 0; i < 8; i++) p.Add(new Vec3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? 0f : 3f, (i & 4) == 0 ? -0.5f : 0.5f));
        var f = new List<(int, int, int)> { (0, 1, 3), (0, 3, 2), (4, 6, 7), (4, 7, 5), (0, 4, 5), (0, 5, 1), (2, 3, 7), (2, 7, 6), (0, 2, 6), (0, 6, 4), (1, 5, 7), (1, 7, 3) };
        var tm = new TreeMesh
        {
            Vertices = new[] { new TreeMesh.Vertex(0, 0, 0, 0, 1, 0, 0, 0), new TreeMesh.Vertex(1, 0, 0, 0, 1, 0, 1, 0), new TreeMesh.Vertex(0, 1, 0, 0, 1, 0, 0, 1) },
            Indices = new ushort[] { 0, 1, 2 },
            Collision = TreeCollision.Build(p, f),
        };
        tm.Groups[1].Add(new TreeMesh.Material { Start = 0, Count = 1, TexName = "treeMesh/texture/bark" });
        tm.RecomputeBounds();
        var back = TreeMesh.Parse(tm.Write());
        Assert.True(back.HasCollision);
        Assert.Equal(TreeMesh.CollisionTag, back.CollisionFlag);
        Assert.Equal(8, back.CollisionVertices.Length);
        Assert.Equal(36, back.CollisionIndices.Length);
        Assert.Equal(12u, back.Collision!.CountA);
        Assert.Equal(12, back.Collision.Root.Faces.Length);
        Assert.Null(back.Collision.Root.A);
        Assert.Equal(3f, back.Collision.Root.Max.Y);
        Assert.Equal("treeMesh/texture/bark", back.Groups[1][0].TexName);
        Assert.Equal(TreeMesh.SolidExtra, back.Vertices[0].Extra);
        Assert.True(tm.Write().AsSpan().SequenceEqual(back.Write()));
    }
}
