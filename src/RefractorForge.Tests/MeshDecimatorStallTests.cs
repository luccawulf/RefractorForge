using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Two ways the decimator used to stop short of its budget on perfectly ordinary models.
///
/// Positions were welded by an XOR hash of their grid cell, and the hash was used as the identity: on anything
/// symmetric about the origin - a tyre in its own frame, which is how a vehicle's wheels are simplified - distinct
/// corners folded to the same hash and were welded together, and the run stalled at the same count whatever it was
/// asked for (1,282 triangles for a 2,304-triangle tyre). And on dense curved meshes the run gave up at about 30% of
/// the source. Both are measured against the same shapes the import survey used.
/// </summary>
public class MeshDecimatorStallTests
{
    /// <summary>A torus about the X axis, centred on <paramref name="centre"/>: <paramref name="around"/> segments round
    /// the wheel, <paramref name="tube"/> round the tube, and a UV seam where each ring closes (the first and last
    /// columns and rows are split vertices, as every exporter writes them). Positions are rounded to six decimals, the
    /// way an OBJ carries them.</summary>
    internal static ObjMesh Torus(int around, int tube, float major, float minor, Vec3 centre)
    {
        var s = new ObjSubMesh { Material = "tyre" };
        static float R6(double v) => (float)Math.Round(v, 6);
        for (int i = 0; i <= tube; i++)
            for (int j = 0; j <= around; j++)
            {
                double phi = 2 * Math.PI * i / tube, theta = 2 * Math.PI * j / around;
                double radial = major + minor * Math.Cos(phi);
                s.Positions.Add(new Vec3(R6(minor * Math.Sin(phi)) + centre.X, R6(radial * Math.Cos(theta)) + centre.Y,
                                         R6(radial * Math.Sin(theta)) + centre.Z));
                s.Normals.Add(new Vec3((float)Math.Sin(phi), (float)(Math.Cos(phi) * Math.Cos(theta)), (float)(Math.Cos(phi) * Math.Sin(theta))));
                s.Uvs.Add(((float)j / around, (float)i / tube));
            }
        int w = around + 1;
        for (int i = 0; i < tube; i++)
            for (int j = 0; j < around; j++)
            {
                int a = i * w + j, b = a + 1, c = a + w + 1, d = a + w;
                s.Faces.Add((a, b, c));
                s.Faces.Add((a, c, d));
            }
        var m = ObjMesh.FromSubMeshes(new[] { s });
        m.RecomputeBounds();
        return m;
    }

    /// <summary>A latitude-longitude sphere of radius 1 standing on the ground (centre 0,1,0): <paramref name="nu"/>
    /// columns, <paramref name="nv"/> rows, the poles and the closing column split per UV - the import survey's
    /// high-poly fixture, at a size a test can afford.</summary>
    internal static ObjMesh UvSphere(int nu, int nv)
    {
        var s = new ObjSubMesh { Material = "ball" };
        static float R5(double v) => (float)Math.Round(v, 5);
        for (int j = 0; j <= nv; j++)
            for (int i = 0; i <= nu; i++)
            {
                double th = 2 * Math.PI * i / nu, ph = Math.PI * j / nv;
                double x = Math.Sin(ph) * Math.Cos(th), y = Math.Cos(ph), z = Math.Sin(ph) * Math.Sin(th);
                s.Positions.Add(new Vec3(R5(x), R5(y + 1), R5(z)));
                s.Normals.Add(new Vec3((float)x, (float)y, (float)z));
                s.Uvs.Add(((float)i / nu, (float)j / nv));
            }
        int w = nu + 1;
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = j * w + i, b = a + 1, c = a + w + 1, e = a + w;
                s.Faces.Add((a, b, c));
                s.Faces.Add((a, c, e));
            }
        var m = ObjMesh.FromSubMeshes(new[] { s });
        m.RecomputeBounds();
        return m;
    }

    [Theory]
    [InlineData(1152)]
    [InlineData(576)]
    [InlineData(200)]
    public void A_tyre_centred_on_the_origin_reaches_its_target(int target)
    {
        var atOrigin = Torus(48, 24, 0.33f, 0.1f, new Vec3(0, 0, 0));
        var offset = Torus(48, 24, 0.33f, 0.1f, new Vec3(0.8f, 0.35f, 1.3f));
        Assert.Equal(2304, atOrigin.TotalFaces);

        var a = MeshDecimator.Decimate(atOrigin, target, out var ra);
        var b = MeshDecimator.Decimate(offset, target, out var rb);
        Assert.True(ra.Triangles <= target, $"at the origin: asked for {target}, got {ra.Triangles}");
        // Where a model sits must not decide how far it simplifies.
        Assert.Equal(rb.Triangles, ra.Triangles);

        // Nothing welded that was not the same point: every output corner is a source vertex, position and UV.
        var src = atOrigin.SubMeshes[0];
        var known = Enumerable.Range(0, src.Positions.Count).Select(i => (src.Positions[i], src.Uvs[i])).ToHashSet();
        var o = a.SubMeshes[0];
        for (int i = 0; i < o.Positions.Count; i++) Assert.Contains((o.Positions[i], o.Uvs[i]), known);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    public void Tori_reach_their_target_wherever_they_sit(float at)
    {
        // 40,000 triangles, a tenth of it asked for; the survey's tori lost 16-17% of their positions to false welds.
        var torus = Torus(200, 100, 1.0f, 0.35f, new Vec3(at, at, at));
        var lod = MeshDecimator.Decimate(torus, 4000, out var r);
        Assert.True(r.Triangles <= 4000 * 1.02, $"asked for 4,000, got {r.Triangles:N0}");
        Assert.True(lod.TotalFaces > 3000, $"overshot to {lod.TotalFaces:N0}");
    }

    [Fact]
    public void A_dense_uv_sphere_reaches_its_target()
    {
        // 400 x 150 cells = 120,000 triangles to 20,000: the old run gave up near 30% of the source.
        var sphere = UvSphere(400, 150);
        var lod = MeshDecimator.Decimate(sphere, 20000, out var r);
        Assert.True(r.Triangles <= 22000, $"asked for 20,000, got {r.Triangles:N0}");

        // Still a sphere: the bounds are the source's (poles and equator survive), and no corner left the surface.
        for (int i = 0; i < 6; i++) Assert.Equal(sphere.BoundingBox[i], lod.BoundingBox[i], 2);
        foreach (var p in lod.SubMeshes[0].Positions)
            Assert.InRange(MathF.Sqrt(p.X * p.X + (p.Y - 1) * (p.Y - 1) + p.Z * p.Z), 0.999f, 1.001f);
    }
}
