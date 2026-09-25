using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A tree laid out as the engine reads it (BF1942's TreeMeshTemplate::load, TreeMesh::realDraw, getAngleIndex and
/// TreeRenderer::createBillboard), checked against every retail <c>.tm</c>: a collision face's plane normal is
/// -(b-a)x(c-a), out of the solid (a built hull stored it inward, so everything walked through it); the two header boxes
/// follow one rule; a leaf or sprite block is its triangles once per view, copy k sorted back to front as seen from view k
/// (a block written once, counted in eighths, drew an eighth of the leaves from each side); a static mesh's collision
/// section is a tree's, read whole. Runs against the clean BF1942 install and does nothing when it is not there.
/// </summary>
public class TreeMeshEngineTests
{
    private const string Archives = @"D:\Games\EA GAMES\Battlefield 1942 Clean\Battlefield 1942\Mods\bf1942\Archives\";

    private static IEnumerable<(string Name, TreeMesh Tree)> Trees()
    {
        if (!File.Exists(Archives + "treeMesh.rfa")) yield break;
        var archive = new RefractorFlatArchive(Archives + "treeMesh.rfa");
        foreach (var e in archive.Entries.Where(e => e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)))
            yield return (e.Name, TreeMesh.Parse(archive.Read(e)));
    }

    private static Vec3 Cross(Vec3 a, Vec3 b, Vec3 c)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return new Vec3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    private static Vec3 Normal(byte[] plane) => new(BitConverter.ToSingle(plane, 0), BitConverter.ToSingle(plane, 4), BitConverter.ToSingle(plane, 8));

    [Fact]
    public void A_built_hull_carries_the_planes_retails_hulls_do()
    {
        int faces = 0;
        foreach (var (name, tm) in Trees())
        {
            if (tm.Collision is not { } col) continue;
            var positions = col.Vertices.Select(v => v.Position).ToList();
            var built = TreeCollision.Build(positions, col.Faces.Select(f => ((int)f.A, (int)f.B, (int)f.C)).ToList());
            for (int i = 0; i < col.Faces.Count; i++)
            {
                var want = Normal(col.Planes[i]);
                var got = Normal(built.Planes[i]);
                Assert.True(MathF.Abs(want.X - got.X) < 1e-4f && MathF.Abs(want.Y - got.Y) < 1e-4f && MathF.Abs(want.Z - got.Z) < 1e-4f,
                            $"{name} face {i}: built ({got.X}, {got.Y}, {got.Z}), retail ({want.X}, {want.Y}, {want.Z})");
                Assert.Equal(col.Planes[i].AsSpan(12, 16).ToArray(), built.Planes[i].AsSpan(12, 16).ToArray());   // 0, then the face's corners
                faces++;
            }
        }
        if (faces == 0) return;                                        // no install here
        Assert.Equal(1306, faces);
    }

    [Fact]
    public void A_built_box_hull_faces_out()
    {
        // Faces wound as a collision face is: (b-a)x(c-a) into the box.
        var p = new List<Vec3>();
        for (int i = 0; i < 8; i++) p.Add(new Vec3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? 0f : 3f, (i & 4) == 0 ? -0.5f : 0.5f));
        var f = new List<(int, int, int)> { (0, 1, 3), (0, 3, 2), (4, 6, 7), (4, 7, 5), (0, 4, 5), (0, 5, 1), (2, 3, 7), (2, 7, 6), (0, 2, 6), (0, 6, 4), (1, 5, 7), (1, 7, 3) };
        var hull = TreeCollision.Build(p, f);
        var centre = new Vec3(0f, 1.5f, 0f);
        for (int i = 0; i < f.Count; i++)
        {
            var (a, b, c) = f[i];
            var n = Normal(hull.Planes[i]);
            var mid = new Vec3((p[a].X + p[b].X + p[c].X) / 3f - centre.X, (p[a].Y + p[b].Y + p[c].Y) / 3f - centre.Y, (p[a].Z + p[b].Z + p[c].Z) / 3f - centre.Z);
            Assert.True(n.X * mid.X + n.Y * mid.Y + n.Z * mid.Z > 0f, $"face {i} faces in");
            var into = Cross(p[a], p[b], p[c]);
            Assert.True(into.X * mid.X + into.Y * mid.Y + into.Z * mid.Z < 0f, $"face {i} is not wound as a collision face");
        }
    }

    [Fact]
    public void Recomputed_boxes_are_the_files_own_and_scale_with_the_sprites()
    {
        int n = 0, withSprites = 0;
        foreach (var (name, tm) in Trees())
        {
            var (min, max, min2, max2) = (tm.Min, tm.Max, tm.Min2, tm.Max2);
            tm.RecomputeBounds();
            Near(min, tm.Min, name); Near(max, tm.Max, name); Near(min2, tm.Min2, name); Near(max2, tm.Max2, name);
            if (tm.Groups[2].Count > 0)
            {
                withSprites++;
                // Scaled, a sprite grows with it: the boxes it gives are the scaled boxes.
                tm.Scale(2f);
                var (smin, smax, smin2, smax2) = (tm.Min, tm.Max, tm.Min2, tm.Max2);
                tm.RecomputeBounds();
                Near(smin, tm.Min, name); Near(smax, tm.Max, name); Near(smin2, tm.Min2, name); Near(smax2, tm.Max2, name);
            }
            else Assert.True(tm.Min2 == default && tm.Max2 == default, $"{name}: a tree without sprites has a second box");
            n++;
        }
        if (n == 0) return;
        Assert.Equal(111, n);
        Assert.True(withSprites > 40, $"{withSprites} trees with sprites");

        static void Near(Vec3 want, Vec3 got, string name)
            => Assert.True(MathF.Abs(want.X - got.X) < 2e-4f && MathF.Abs(want.Y - got.Y) < 2e-4f && MathF.Abs(want.Z - got.Z) < 2e-4f,
                           $"{name}: ({got.X}, {got.Y}, {got.Z}) is not ({want.X}, {want.Y}, {want.Z})");
    }

    [Fact]
    public void A_leaf_block_is_a_copy_per_view_sorted_back_to_front_for_it()
    {
        int blocks = 0;
        double along = 0, against = 0;
        foreach (var (name, tm) in Trees())
            foreach (var (group, m, end) in tm.Blocks())
            {
                if (group is not (0 or 2)) { Assert.Equal(m.Count * 3, end - m.Start); continue; }
                Assert.Equal(8, tm.Views);
                Assert.Equal(m.Count * 3 * tm.Views, end - m.Start);
                var first = Triangles(tm, m.Start, m.Count).OrderBy(t => t).ToList();
                for (int k = 0; k < tm.Views; k++)
                {
                    var copy = Triangles(tm, m.Start + k * m.Count * 3, m.Count);
                    Assert.Equal(first, copy.OrderBy(t => t).ToList());
                    along += InOrder(tm, copy, TreeMesh.ViewDirection(k));
                    against += InOrder(tm, copy, TreeMesh.ViewDirection(k + tm.Views / 2));
                }
                blocks++;
            }
        if (blocks == 0) return;
        Assert.Equal(156, blocks);
        // Retail's order is the middle of each triangle along the view (95 % of neighbours in order); from the other side
        // it is mostly the reverse (a leaf's two triangles often tie).
        Assert.True(along / (blocks * 8) > 0.9, $"{along / (blocks * 8):0.000} in order");
        Assert.True(against / (blocks * 8) < 0.3, $"{against / (blocks * 8):0.000} in order from the other side");

        // Made: every copy is the triangles, each sorted along its view.
        var tris = new List<(int, int, int)> { (0, 1, 2), (3, 4, 5), (6, 7, 8) };
        var at = new[] { new Vec3(-2, 0, 0), new Vec3(-2, 1, 0), new Vec3(-2, 0, 1), new Vec3(0, 0, 3), new Vec3(0, 1, 3), new Vec3(1, 0, 3),
                         new Vec3(2, 0, -2), new Vec3(2, 1, -2), new Vec3(2, 0, -1) };
        var made = TreeMesh.ViewSorted(tris, i => at[i]);
        Assert.Equal(3 * 3 * 8, made.Length);
        var check = new TreeMesh { Vertices = at.Select(v => new TreeMesh.Vertex(v.X, v.Y, v.Z, 0, 1, 0, 0, 0)).ToArray(), Indices = made };
        for (int k = 0; k < 8; k++)
            Assert.Equal(1.0, InOrder(check, Triangles(check, k * 9, 3), TreeMesh.ViewDirection(k)));
    }

    private static List<(int, int, int)> Triangles(TreeMesh tm, int start, int count)
        => Enumerable.Range(0, count).Select(i => ((int)tm.Indices[start + 3 * i], (int)tm.Indices[start + 3 * i + 1], (int)tm.Indices[start + 3 * i + 2])).ToList();

    private static double InOrder(TreeMesh tm, List<(int A, int B, int C)> tris, Vec3 d)
    {
        if (tris.Count < 2) return 1.0;
        var keys = tris.Select(t =>
        {
            float x = (tm.Vertices[t.A].Px + tm.Vertices[t.B].Px + tm.Vertices[t.C].Px) / 3f, z = (tm.Vertices[t.A].Pz + tm.Vertices[t.B].Pz + tm.Vertices[t.C].Pz) / 3f;
            return x * d.X + z * d.Z;
        }).ToList();
        int ok = 0;
        for (int i = 1; i < keys.Count; i++) if (keys[i - 1] <= keys[i] + 1e-6f) ok++;
        return ok / (double)(keys.Count - 1);
    }

    [Fact]
    public void A_view_is_seen_from_half_a_step_east_of_north_and_on_round_by_the_east()
    {
        var v0 = TreeMesh.ViewDirection(0);
        Assert.Equal(MathF.Sin(MathF.PI / 8f), v0.X, 5);               // 22.5 degrees from +Z towards +X
        Assert.Equal(MathF.Cos(MathF.PI / 8f), v0.Z, 5);
        var v2 = TreeMesh.ViewDirection(2);                           // 112.5: east, a little south
        Assert.True(v2.X > 0.9f && v2.Z < 0f);
        var v4 = TreeMesh.ViewDirection(4);
        Assert.Equal(-v0.X, v4.X, 5);
        Assert.Equal(-v0.Z, v4.Z, 5);
    }

    [Fact]
    public void A_static_meshs_collision_section_is_a_tree_hull_read_whole()
    {
        if (!File.Exists(Archives + "standardMesh.rfa")) return;
        var archive = new RefractorFlatArchive(Archives + "standardMesh.rfa");
        int sections = 0, faces = 0;
        foreach (var e in archive.Entries.Where(e => e.Name.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)).Take(400))
        {
            if (!StandardMesh.TryParse(archive.Read(e), out var sm) || sm is null) continue;
            foreach (var section in sm.CollisionSections)
            {
                var hull = TreeCollision.FromMeshSection(section);
                Assert.NotNull(hull);
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, System.Text.Encoding.Latin1, leaveOpen: true))
                {
                    w.Write(TreeMesh.CollisionTag);
                    hull!.Write(w);
                }
                Assert.True(section.AsSpan().SequenceEqual(ms.ToArray()), $"{e.Name}: the section does not write back");
                for (int i = 0; i < hull.Faces.Count; i++)
                {
                    var f = hull.Faces[i];
                    var c = Cross(hull.Vertices[f.A].Position, hull.Vertices[f.B].Position, hull.Vertices[f.C].Position);
                    float len = MathF.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z);
                    var n = Normal(hull.Planes[i]);
                    if (len < 1e-4f || n == default) continue;                  // a sliver: the exporter left its normal 0
                    Assert.True((n.X * c.X + n.Y * c.Y + n.Z * c.Z) / len < -0.99f, $"{e.Name} face {i}: its plane is not -(b-a)x(c-a)");
                    faces++;
                }
                sections++;
            }
        }
        Assert.True(sections > 100, $"{sections} sections");
        Assert.True(faces > 5000, $"{faces} faces");
        Assert.Null(TreeCollision.FromMeshSection(new byte[] { 1, 2, 3, 4, 5, 0, 0, 0 }));
    }

    [Fact]
    public void A_tree_is_drawn_with_each_leaf_once_and_its_sprites_at_their_size()
    {
        var found = Trees().FirstOrDefault(t => t.Tree.Groups[2].Count > 0 && t.Tree.Groups[0].Count > 0);
        if (found.Name is null) return;
        var tm = found.Tree;
        var mesh = MeshLibrary.MeshFromTreeMesh(tm);
        int leaves = tm.Groups[0].Sum(m => m.Count), trunk = tm.Groups[1].Sum(m => m.Count) + tm.Groups[3].Sum(m => m.Count);
        int sprites = tm.Groups[2].Sum(m => m.Count) / 2;
        Assert.Equal(leaves + trunk + sprites * 4, mesh.Triangles);                  // a still cross is two quads

        // Facing a view, a sprite is its own quad, as wide as its uv1 says, square to the view.
        var d = TreeMesh.ViewDirection(1);
        var facing = MeshLibrary.MeshFromTreeMesh(tm, null, new Vector3(d.X, d.Y, d.Z));
        Assert.Equal(leaves + trunk + sprites * 2, facing.Triangles);
        var spriteStart = tm.Groups[2][0].Start;
        var corner = tm.Vertices[tm.Indices[spriteStart]];
        float size = TreeMesh.SpriteSize(corner);
        Assert.True(size > 0.1f);
        var part = facing.Parts.First(p => p.Indices.Any(i => i >= tm.Vertices.Length));
        var quad = part.Indices.Take(6).Select(i => facing.Positions[i]).ToList();
        var toCamera = new Vector3(d.X, 0f, d.Z);
        foreach (var q in quad)
        {
            var off = q - new Vector3(corner.Px, corner.Py, corner.Pz);
            Assert.True(MathF.Abs(Vector3.Dot(off, toCamera)) < 1e-4f);               // in the plane square to the view
            Assert.True(MathF.Abs(MathF.Abs(off.Y) - size) < 1e-3f);
        }
    }
}
