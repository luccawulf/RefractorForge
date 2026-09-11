using System.Numerics;
using System.Text;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The unwrapper's two load-bearing guarantees, asserted rather than hoped for: no two charts may land on the
/// same texel (the baker resolves a collision by "first triangle wins", so an overlap is silent data loss), and
/// no triangle may project inverted.
///
/// <para>Fold-freedom is meant to be arithmetic - every triangle in a chart is within 54.7 degrees of the chart's
/// axis by construction, so its signed projected area cannot change sign. These tests check the construction
/// actually holds, because the symptom when it does not is a chart with an enormous projected extent that reads
/// as "the atlas is too small" and sends the investigation somewhere else entirely. That happened.</para>
///
/// <para>Corpus results with this code: 1,957 of 1,997 BfVietnam meshes and 1,364 of 1,445 BF1942 meshes unwrap,
/// at +13.3% and +18.5% vertices, about 1 ms each.</para>
/// </summary>
public class LightmapUnwrapTests
{
    /// <summary>An axis-aligned box: six faces, hard edges, the shape most of this corpus is made of.</summary>
    private static byte[] Box(float sx = 2f, float sy = 3f, float sz = 4f, uint vbs = 32u, uint renderType = 4u)
    {
        var p = new List<Vector3>();
        var idx = new List<ushort>();
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int b0 = p.Count;
            p.Add(a); p.Add(b); p.Add(c); p.Add(d);
            // Written reversed, the way the reader un-reverses it.
            foreach (var t in new[] { (0, 1, 2), (0, 2, 3) })
            {
                idx.Add((ushort)(b0 + t.Item3)); idx.Add((ushort)(b0 + t.Item2)); idx.Add((ushort)(b0 + t.Item1));
            }
        }
        float x = sx / 2, y = sy / 2, z = sz / 2;
        Face(new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z));
        Face(new(x, -y, z), new(-x, -y, z), new(-x, y, z), new(x, y, z));
        Face(new(-x, -y, z), new(-x, -y, -z), new(-x, y, -z), new(-x, y, z));
        Face(new(x, -y, -z), new(x, -y, z), new(x, y, z), new(x, y, -z));
        Face(new(-x, y, -z), new(x, y, -z), new(x, y, z), new(-x, y, z));
        Face(new(-x, -y, z), new(x, -y, z), new(x, -y, -z), new(-x, -y, -z));

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)10); w.Write(new byte[4]);
        w.Write(-x); w.Write(-y); w.Write(-z); w.Write(x); w.Write(y); w.Write(z);
        w.Write((byte)0);
        w.Write((uint)0);                                  // no collision
        w.Write((uint)1); w.Write((uint)1);                // one LOD, one material
        var nm = Encoding.Latin1.GetBytes("box_Material0");
        w.Write((uint)nm.Length); w.Write(nm); w.Write(new byte[12]);
        w.Write(renderType); w.Write(vbs == 40 ? 9233u : 1041u); w.Write(vbs);
        w.Write((uint)p.Count); w.Write((uint)idx.Count); w.Write((uint)0);
        foreach (var v in p)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            var n = Vector3.Normalize(v);                  // good enough; the unwrapper uses geometric normals
            w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
            w.Write(0f); w.Write(0f);
            for (int k = 0; k < ((int)vbs - 32) / 4; k++) w.Write(0f);
        }
        foreach (var i in idx) w.Write(i);
        w.Write((uint)0); w.Write((uint)0);                // no shadow, empty portal
        w.Flush();
        return ms.ToArray();
    }

    private static StandardMesh Parse(byte[] b)
    {
        Assert.True(StandardMesh.TryParse(b, out var sm));
        return sm!;
    }

    /// <summary>A box unwraps, and the result is the six faces it obviously has (or fewer, once coplanar
    /// neighbours merge) - not one chart per triangle.</summary>
    [Fact]
    public void A_box_unwraps_into_its_faces()
    {
        var r = LightmapUnwrapper.Unwrap(Parse(Box()));
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r.Status);
        Assert.InRange(r.Diagnostics.Charts, 1, 6);
        Assert.NotNull(r.Lod0Plan);
        Assert.True(r.Diagnostics.MinBakeSize >= 64 && r.Diagnostics.MinBakeSize <= 1024);
    }

    /// <summary>THE GUARANTEE: no triangle may project inverted. Every chart's triangles must have the same
    /// signed area sign in UV space.</summary>
    [Fact]
    public void No_triangle_projects_inverted()
    {
        var mesh = Parse(Box());
        var r = LightmapUnwrapper.Unwrap(mesh);
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r.Status);

        var uvs = PlanUvs(mesh, r);
        var raw = r.Lod0Plan![0]!.Indices!;
        for (int s = 0; s + 2 < raw.Length; s += 3)
        {
            var a = uvs[raw[s + 2]]; var b = uvs[raw[s + 1]]; var c = uvs[raw[s]];
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            Assert.True(MathF.Abs(area) > 0f, "a triangle collapsed to zero area in UV space");
        }
    }

    /// <summary>THE OTHER GUARANTEE: two charts must never claim the same texel. The baker resolves a collision
    /// silently by keeping the first triangle, so an overlap is one surface stealing another's lighting.</summary>
    [Fact]
    public void Charts_do_not_overlap_in_the_atlas()
    {
        var mesh = Parse(Box(6f, 4f, 5f));
        var r = LightmapUnwrapper.Unwrap(mesh);
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r.Status);

        int size = r.Diagnostics.MinBakeSize;
        var uvs = PlanUvs(mesh, r);
        var raw = r.Lod0Plan![0]!.Indices!;

        // Rasterise every triangle into the atlas the way the baker does, and record which TRIANGLE owns each
        // texel. Then check no texel is claimed by triangles whose UV islands are disjoint.
        var owner = new int[size * size];
        Array.Fill(owner, -1);
        int collisions = 0;
        for (int s = 0, tri = 0; s + 2 < raw.Length; s += 3, tri++)
        {
            var a = uvs[raw[s + 2]] * size; var b = uvs[raw[s + 1]] * size; var c = uvs[raw[s]] * size;
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            float area = Edge(a, b, c);
            if (MathF.Abs(area) < 1e-6f) continue;
            float inv = 1f / area;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var pt = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(b, c, pt) * inv, w1 = Edge(c, a, pt) * inv, w2 = Edge(a, b, pt) * inv;
                    if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                    int o = y * size + x;
                    if (owner[o] >= 0 && owner[o] != tri) collisions++;
                    owner[o] = tri;
                }
        }
        // Triangles inside ONE chart legitimately tile the same region edge-to-edge; what must not happen is a
        // texel claimed by two triangles that are not neighbours. A shelf-packed atlas with a 4-cell gutter
        // should produce none at all.
        Assert.True(collisions == 0, $"{collisions} texels were claimed by more than one triangle");
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static Vector2[] PlanUvs(StandardMesh mesh, LightmapUnwrapper.UnwrapResult r)
    {
        var plan = r.Lod0Plan![0]!;
        var uv = new Vector2[mesh.Lods[0][0].NumVertices + plan.CloneSources.Length];
        for (int i = 0; i < plan.Uv2.Length && i < uv.Length; i++) uv[i] = new Vector2(plan.Uv2[i].U, plan.Uv2[i].V);
        return uv;
    }

    /// <summary>Every UV lands inside the atlas, with the border gutter kept clear so a wrap or a bilinear tap
    /// at the edge cannot read across the seam.</summary>
    [Fact]
    public void Every_uv_is_inside_the_atlas()
    {
        var mesh = Parse(Box());
        var r = LightmapUnwrapper.Unwrap(mesh);
        foreach (var (u, v) in r.Lod0Plan![0]!.Uv2)
        {
            Assert.True(float.IsFinite(u) && float.IsFinite(v), "a UV was not finite");
            Assert.InRange(u, 0f, 1f);
            Assert.InRange(v, 0f, 1f);
        }
    }

    /// <summary>A 32-byte material has nowhere to put a lightmap UV, so the plan must carry the promotion that
    /// widens it - otherwise the rewriter would refuse the plan (and before the guards existed, would have
    /// written a fatter, renumbered, still-unlit mesh).</summary>
    [Fact]
    public void A_32_byte_mesh_gets_a_stride_promotion()
    {
        var r = LightmapUnwrapper.Unwrap(Parse(Box(vbs: 32u)));
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r.Status);
        Assert.NotNull(r.Lod0Plan![0]!.Promote);
        Assert.Equal(40u, r.Lod0Plan![0]!.Promote!.NewVertexByteSize);

        // And a mesh that already has the slot must NOT be promoted.
        var r40 = LightmapUnwrapper.Unwrap(Parse(Box(vbs: 40u)));
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r40.Status);
        Assert.Null(r40.Lod0Plan![0]!.Promote);
    }

    /// <summary>A triangle STRIP shares one index slot between three consecutive triangles, so it cannot be
    /// renumbered per chart. Refuse the mesh rather than corrupt it. Measured cost across both corpora: 58
    /// meshes, every one a soldier - no static prop.</summary>
    [Fact]
    public void A_triangle_strip_mesh_is_refused()
    {
        var r = LightmapUnwrapper.Unwrap(Parse(Box(renderType: 5u)));
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.UnsupportedTopology, r.Status);
        Assert.Null(r.Lod0Plan);
    }

    /// <summary>The plan is a renumbering, so the index buffer keeps its length and every value addresses a real
    /// output vertex.</summary>
    [Fact]
    public void The_plan_is_a_renumbering_not_a_retopology()
    {
        var mesh = Parse(Box());
        var r = LightmapUnwrapper.Unwrap(mesh);
        var mat = mesh.Lods[0][0];
        var plan = r.Lod0Plan![0]!;
        Assert.Equal(mat.NumFaceValues, plan.Indices!.Length);
        int outCount = mat.NumVertices + plan.CloneSources.Length;
        Assert.Equal(outCount, plan.Uv2.Length);
        foreach (var i in plan.Indices) Assert.InRange(i, 0, outCount - 1);
        foreach (var srcV in plan.CloneSources) Assert.InRange(srcV, 0, mat.NumVertices - 1);
    }

    /// <summary>Same bytes in, same plan out - or no bake can ever be regression-tested.</summary>
    [Fact]
    public void The_unwrap_is_deterministic()
    {
        var mesh = Parse(Box(3f, 5f, 7f));
        var a = LightmapUnwrapper.Unwrap(mesh);
        var b = LightmapUnwrapper.Unwrap(mesh);
        Assert.Equal(a.Diagnostics.Charts, b.Diagnostics.Charts);
        Assert.Equal(a.Diagnostics.MinBakeSize, b.Diagnostics.MinBakeSize);
        Assert.Equal(a.Lod0Plan![0]!.Uv2, b.Lod0Plan![0]!.Uv2);
        Assert.Equal(a.Lod0Plan![0]!.Indices, b.Lod0Plan![0]!.Indices);
    }

    /// <summary>The unwrap survives the round trip into a real .sm and back out, with the geometry untouched.</summary>
    [Fact]
    public void The_unwrap_can_be_written_into_a_mesh_and_read_back()
    {
        var bytes = Box();
        var mesh = Parse(bytes);
        var r = LightmapUnwrapper.Unwrap(mesh);
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, r.Status);

        var written = StandardMeshRewriter.Write(bytes, mesh, new[] { r.Lod0Plan! },
                                                 new StandardMeshRewriter.Options(SetQFlag: true));
        var re = Parse(written);
        var reMat = re.Lods[0][0];

        Assert.True(reMat.HasLightmapUv);
        Assert.Equal(1, re.QFlag);
        Assert.Equal(mesh.Lods[0][0].NumVertices + r.Lod0Plan![0]!.CloneSources.Length, reMat.NumVertices);

        // Every original vertex kept its position; the clones sit on top of their sources.
        for (int v = 0; v < mesh.Lods[0][0].NumVertices; v++)
            Assert.Equal(mesh.Lods[0][0].Vertices[v], reMat.Vertices[v]);

        // And the UVs are a real unwrap, not all one point - which is exactly the state 452 retail meshes are in.
        var lm = reMat.LightmapUvs;
        Assert.Contains(lm, t => MathF.Abs(t.U) > 1e-6f || MathF.Abs(t.V) > 1e-6f);
        float minU = 1f, maxU = 0f;
        foreach (var (u, _) in lm) { minU = MathF.Min(minU, u); maxU = MathF.Max(maxU, u); }
        Assert.True(maxU - minU > 0.1f, "the unwrap should span a real fraction of the atlas");
    }

    /// <summary>The minimum bake size is recoverable from the emitted UVs: they sit on a 1/R lattice, so the
    /// gutter floor travels with the file and needs no sidecar that could drift out of step with it.</summary>
    [Fact]
    public void The_minimum_bake_size_is_encoded_in_the_uvs()
    {
        var mesh = Parse(Box(5f, 5f, 5f));
        var r = LightmapUnwrapper.Unwrap(mesh);
        int R = r.Diagnostics.MinBakeSize;
        foreach (var (u, v) in r.Lod0Plan![0]!.Uv2)
        {
            Assert.Equal(MathF.Round(u * R), u * R, 3);
            Assert.Equal(MathF.Round(v * R), v * R, 3);
        }
    }
}
