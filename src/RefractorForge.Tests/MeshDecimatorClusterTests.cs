using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The decimator's second stage (<see cref="MeshDecimator.DecimateTo(ObjMesh,int)"/>, <see cref="MeshDecimator.Cluster"/>)
/// and the shape-only simplification for shadows and collision (<see cref="MeshDecimator.ShapeOf"/>). A model made of
/// hundreds of loose shells - how cars come from the internet - stalls the quadric collapse far above any vehicle budget
/// (a 548,000-triangle car stopped near 100,000 whatever it was asked for, and its shadow broke the .sm's 21,845-triangle
/// limit); these reach the number asked for.
/// </summary>
public class MeshDecimatorClusterTests
{
    /// <summary>A soup of separate little boxes, each face its own UV island: every vertex is a border, a seam and a
    /// corner at once - nothing the collapse may touch.</summary>
    private static ObjMesh Soup(int boxes, float size = 0.02f)
    {
        var s = new ObjSubMesh { Material = "paint" };
        var rng = new Random(7);
        for (int b = 0; b < boxes; b++)
        {
            var o = new Vec3((float)rng.NextDouble() * 4, (float)rng.NextDouble(), (float)rng.NextDouble() * 2);
            // six faces, each with its own four vertices (a UV island per face)
            var faces = new (Vec3 N, Vec3 U, Vec3 V)[]
            {
                (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)), (new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0)),
                (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)), (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),
                (new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)), (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),
            };
            foreach (var (n, u, v) in faces)
            {
                var c = new Vec3(o.X + n.X * size / 2, o.Y + n.Y * size / 2, o.Z + n.Z * size / 2);
                int i0 = s.Positions.Count;
                foreach (var (du, dv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    s.Positions.Add(new Vec3(c.X + (u.X * du + v.X * dv) * size / 2, c.Y + (u.Y * du + v.Y * dv) * size / 2, c.Z + (u.Z * du + v.Z * dv) * size / 2));
                    s.Normals.Add(n);
                    s.Uvs.Add(((du + 1) / 2, (dv + 1) / 2));
                }
                s.Faces.Add((i0, i0 + 1, i0 + 2));
                s.Faces.Add((i0, i0 + 2, i0 + 3));
            }
        }
        var m = new ObjMesh();
        m.SubMeshes.Add(s);
        m.RecomputeBounds();
        return m;
    }

    [Fact]
    public void A_soup_of_loose_shells_stalls_the_collapse_and_reaches_the_budget_with_the_second_stage()
    {
        var soup = Soup(2000);                                                  // 24,000 triangles
        MeshDecimator.Decimate(soup, 2000, out var stalled);
        Assert.True(stalled.Triangles > 4000, $"{stalled.Triangles}: the collapse was expected to stall on shells");
        var done = MeshDecimator.DecimateTo(soup, 2000, out var r);
        Assert.True(r.Triangles <= 2000, $"{r.Triangles} > 2000");
        Assert.Equal(done.TotalFaces, r.Triangles);
        Assert.True(r.Triangles > 0);
        Assert.All(done.SubMeshes, s => Assert.All(s.Faces, f => Assert.True(f.A != f.B && f.B != f.C && f.A != f.C)));
    }

    /// <summary>Two UV islands meeting along a seam keep their own UVs, and the seam does not open: both sides of it
    /// move to the same positions.</summary>
    [Fact]
    public void Clustering_keeps_a_seam_closed_and_each_side_of_it_its_own_uvs()
    {
        var s = new ObjSubMesh { Material = "m" };
        const int n = 40;
        // a grid 2 m x 1 m, split at x = 1 into two islands with duplicated seam vertices
        for (int half = 0; half < 2; half++)
        {
            int start = s.Positions.Count;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float x = half + i / (float)n, z = j / (float)n;
                    s.Positions.Add(new Vec3(x, 0, z));
                    s.Normals.Add(new Vec3(0, 1, 0));
                    s.Uvs.Add((half == 0 ? i / (float)n * 0.5f : 0.5f + i / (float)n * 0.5f, z));
                }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = start + j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                    s.Faces.Add((a, c, b));
                    s.Faces.Add((b, c, d));
                }
        }
        var m = new ObjMesh();
        m.SubMeshes.Add(s);
        m.RecomputeBounds();
        var outMesh = MeshDecimator.Cluster(m, 400, out var r);
        Assert.True(r.Triangles <= 400);
        var sub = Assert.Single(outMesh.SubMeshes);
        // Along the seam each island ends in an open edge: where the left island's open edges end near x = 1 must be
        // exactly where the right island's do, or light shows through between them.
        HashSet<(float, float)> SeamEnds(bool leftSide)
        {
            var use = new Dictionary<(int, int), int>();
            foreach (var (a, b, c) in sub.Faces)
            {
                if ((sub.Uvs[a].U <= 0.5f + 1e-4f) != leftSide) continue;
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    var k = x < y ? (x, y) : (y, x);
                    use[k] = use.GetValueOrDefault(k) + 1;
                }
            }
            var ends = new HashSet<(float, float)>();
            foreach (var ((x, y), count) in use)
                if (count == 1)
                    foreach (var v in new[] { x, y })
                        if (MathF.Abs(sub.Positions[v].X - 1) < 0.2f && sub.Positions[v].Z > 0.05f && sub.Positions[v].Z < 0.95f)
                            ends.Add((sub.Positions[v].X, sub.Positions[v].Z));
            return ends;
        }
        var left = SeamEnds(true);
        var right = SeamEnds(false);
        Assert.NotEmpty(left);
        Assert.True(left.SetEquals(right), "the two sides of the seam moved to different places: a crack");
        // No UV smeared across: every triangle's corners stay within one half of the atlas.
        foreach (var (a, b, c) in sub.Faces)
        {
            var us = new[] { sub.Uvs[a].U, sub.Uvs[b].U, sub.Uvs[c].U };
            Assert.True(us.All(u => u <= 0.5f + 1e-4f) || us.All(u => u >= 0.5f - 1e-4f));
        }
    }

    [Fact]
    public void A_shape_for_a_shadow_or_collision_is_one_material_welded_and_within_the_limit()
    {
        var soup = Soup(3000);                                                  // 36,000 triangles over one material
        var shape = MeshDecimator.ShapeOf(soup, 30_000_000);
        Assert.True(shape.TotalFaces <= 36_000);
        var shadow = MeshDecimator.ShapeOf(soup, MeshDecimator.MaxShadowTriangles);
        Assert.True(shadow.TotalFaces <= MeshDecimator.MaxShadowTriangles);
        var small = MeshDecimator.ShapeOf(soup, 500);
        Assert.True(small.TotalFaces <= 500 && small.TotalFaces > 0, $"{small.TotalFaces}");
        Assert.Single(small.SubMeshes);
        Assert.Equal(21_845, MeshDecimator.MaxShadowTriangles);
    }
}
