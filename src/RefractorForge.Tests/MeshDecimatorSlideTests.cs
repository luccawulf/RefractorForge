using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A model from the internet is seams and loose shells - a car's body is hundreds of separate open panels - and a
/// decimator that pins every seam and border vertex stalls far above a vehicle's triangle budget. Once the interior is
/// spent, seam and border vertices slide along their own straight lines; corners, material boundaries and anything on
/// both a seam and a border stay put.
/// </summary>
public class MeshDecimatorSlideTests
{
    /// <summary>Open, gently curved panels, 12 m apart, each <paramref name="n"/> x <paramref name="n"/> cells with a UV
    /// seam down column <paramref name="seamAt"/>: the vertices there are split, the left half mapped to u 0..0.5, the right
    /// half to u 0.5..1 of another island.</summary>
    private static ObjMesh Panels(int count, int n = 10, int seamAt = 5)
    {
        var s = new ObjSubMesh { Material = "paint" };
        for (int k = 0; k < count; k++)
        {
            var index = new Dictionary<(int, int, bool), int>();
            int V(int x, int y, bool right)
            {
                bool split = x == seamAt;
                var key = (x, y, split && right);
                if (index.TryGetValue(key, out int i)) return i;
                i = s.Positions.Count;
                s.Positions.Add(new Vec3(k * 12f + x, MathF.Sin(x * 0.3f) * 0.5f, y));
                s.Normals.Add(new Vec3(0, 1, 0));
                float u = split && right ? 0.6f + 0.4f * (x - seamAt) / (n - seamAt) : 0.5f * x / seamAt;
                if (!split && x > seamAt) u = 0.6f + 0.4f * (x - seamAt) / (n - seamAt);
                s.Uvs.Add((u, (float)y / n));
                index[key] = i;
                return i;
            }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool right = x >= seamAt;
                    int a = V(x, y, right), b = V(x + 1, y, right), c = V(x + 1, y + 1, right), d = V(x, y + 1, right);
                    s.Faces.Add((a, b, c));
                    s.Faces.Add((a, c, d));
                }
        }
        var m = ObjMesh.FromSubMeshes(new[] { s });
        m.RecomputeBounds();
        return m;
    }

    [Fact]
    public void Seams_and_borders_slide_when_the_interior_cannot_meet_the_budget()
    {
        var src = Panels(40);                                          // 8,000 triangles
        var strict = MeshDecimator.Decimate(src, 800, out var rs, slideSeams: false);
        var slid = MeshDecimator.Decimate(src, 800, out var r);
        Assert.True(rs.Triangles > 1600, $"pinned seams and borders alone stall at {rs.Triangles}");
        Assert.True(r.Triangles <= 900, $"asked for 800, got {r.Triangles} (pinned: {rs.Triangles})");

        // Nothing invented: every output corner is a source vertex, position AND uv - so each side of the seam keeps its
        // own mapping exactly.
        var source = src.SubMeshes[0];
        var pairs = Enumerable.Range(0, source.Positions.Count)
                              .Select(i => (source.Positions[i].X, source.Positions[i].Y, source.Positions[i].Z, source.Uvs[i].U, source.Uvs[i].V)).ToHashSet();
        var o = slid.SubMeshes[0];
        for (int i = 0; i < o.Positions.Count; i++)
            Assert.Contains((o.Positions[i].X, o.Positions[i].Y, o.Positions[i].Z, o.Uvs[i].U, o.Uvs[i].V), pairs);

        // No triangle mixes the two islands: a face's u values are all left of the seam or all right of it.
        foreach (var (a, b, c) in o.Faces)
        {
            var us = new[] { o.Uvs[a].U, o.Uvs[b].U, o.Uvs[c].U };
            Assert.True(us.All(u => u <= 0.5f) || us.All(u => u >= 0.6f), "a triangle spans the seam");
        }

        // Every panel keeps its four corners, so its footprint - and the whole model's box - is unchanged.
        for (int k = 0; k < 40; k++)
            foreach (var (x, z) in new[] { (0, 0), (10, 0), (0, 10), (10, 10) })
                Assert.Contains(o.Positions, p => MathF.Abs(p.X - (k * 12 + x)) < 1e-4f && MathF.Abs(p.Z - z) < 1e-4f);
        for (int i = 0; i < 6; i++) Assert.Equal(src.BoundingBox[i], slid.BoundingBox[i], 3);
    }

    [Fact]
    public void A_target_the_interior_can_meet_is_met_without_sliding()
    {
        // The first phase alone reaches it, so both settings give the same mesh: what already worked is unchanged.
        var src = Panels(4, n: 20, seamAt: 10);
        var a = MeshDecimator.Decimate(src, 2000, out var ra, slideSeams: false);
        var b = MeshDecimator.Decimate(src, 2000, out var rb);
        Assert.Equal(ra.Triangles, rb.Triangles);
        Assert.Equal(a.SubMeshes[0].Faces, b.SubMeshes[0].Faces);
    }
}
