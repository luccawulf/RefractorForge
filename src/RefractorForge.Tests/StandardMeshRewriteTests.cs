using System.Buffers.Binary;
using System.Text;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// <see cref="StandardMeshRewriter"/> exists to put a lightmap unwrap into a mesh that has none, which means
/// rewriting files whose collision BSP tails, shadow block and portal chunk are only partly decoded. The only
/// defensible way to do that is to prove a no-op rewrite is BYTE-IDENTICAL, and then to prove a real edit touches
/// nothing but the eight bytes it is supposed to.
///
/// <para>These build .sm files by hand rather than through <see cref="StandardMeshWriter"/>, because that writer
/// only emits 32-byte vertices and the layouts that matter here are the 40-byte interleaved one (BF1942) and the
/// 64-byte PLANAR one (BfVietnam) - and the planar layout is the one that has historically been got wrong.</para>
///
/// <para>The whole retail corpus is gated the same way outside the suite: 3,447 meshes across both games re-emit
/// byte-identical.</para>
/// </summary>
public class StandardMeshRewriteTests
{
    // A minimal but structurally real .sm: version 10, a bbox, one collision section with a recognisable tail,
    // one LOD, one material, `nv` vertices at the requested stride, a triangle-list index buffer, no shadow,
    // an empty portal chunk.
    private static byte[] BuildMesh(uint vbs, int nv, int tris, byte qflag = 0, bool withCollision = true)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)10);
        w.Write(new byte[] { 0, 0, 0, 0 });
        for (int i = 0; i < 6; i++) w.Write(i < 3 ? -1.5f : 2.25f);       // bbox
        w.Write(qflag);

        if (withCollision)
        {
            // Not a real BSP - the point is that the bytes come back untouched whatever they are.
            var sec = new byte[64];
            for (int i = 0; i < sec.Length; i++) sec[i] = (byte)(i * 7 + 3);
            w.Write((uint)1); w.Write((uint)sec.Length); w.Write(sec);
        }
        else w.Write((uint)0);

        w.Write((uint)1);                                                  // numLods
        w.Write((uint)1);                                                  // numMaterials
        var name = Encoding.Latin1.GetBytes("probe_Material0");
        w.Write((uint)name.Length); w.Write(name);
        w.Write(new byte[12]);
        w.Write((uint)4);                                                  // renderType: triangle list
        w.Write(vbs == 40 ? 9233u : 1041u);                                // vertexFormat
        w.Write(vbs);
        w.Write((uint)nv);
        w.Write((uint)(tris * 3));
        w.Write((uint)0);                                                  // materialSettings

        int extra = (int)vbs - 32;
        if (vbs == 64)
        {
            for (int v = 0; v < nv; v++) WriteCore(w, v);
            for (int v = 0; v < nv; v++)
            {
                w.Write(0.125f * v); w.Write(0.5f - 0.125f * v);            // the lightmap UV
                for (int k = 0; k < (extra - 8) / 4; k++) w.Write(9.0f + v + k);  // tangent frame
            }
        }
        else
        {
            for (int v = 0; v < nv; v++)
            {
                WriteCore(w, v);
                if (extra >= 8) { w.Write(0.125f * v); w.Write(0.5f - 0.125f * v); }
                for (int k = 0; k < (extra - (extra >= 8 ? 8 : 0)) / 4; k++) w.Write(9.0f + v + k);
            }
        }

        for (int t = 0; t < tris; t++)
        {
            w.Write((ushort)(t % nv)); w.Write((ushort)((t + 1) % nv)); w.Write((ushort)((t + 2) % nv));
        }

        w.Write((uint)0);                                                  // no shadow
        w.Write((uint)0);                                                  // empty portal chunk
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteCore(BinaryWriter w, int v)
    {
        w.Write(1f * v); w.Write(2f * v); w.Write(3f * v);                 // position
        w.Write(0f); w.Write(1f); w.Write(0f);                             // normal
        w.Write(0.25f * v); w.Write(0.75f * v);                            // diffuse uv
    }

    private static (float U, float V) ReadLmUv(byte[] file, SmMaterial m, int v)
    {
        int at = m.VertexByteSize == 64
            ? m.VertexDataOffset + m.NumVertices * 32 + v * ((int)m.VertexByteSize - 32)
            : m.VertexDataOffset + v * (int)m.VertexByteSize + 32;
        return (BinaryPrimitives.ReadSingleLittleEndian(file.AsSpan(at, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(file.AsSpan(at + 4, 4)));
    }

    /// <summary>THE GATE. A rewrite with no plan must reproduce the file exactly - including a collision section
    /// whose contents nothing here understands.</summary>
    [Theory]
    [InlineData(32u)]
    [InlineData(40u)]
    [InlineData(64u)]
    public void A_no_op_rewrite_is_byte_identical(uint vbs)
    {
        var original = BuildMesh(vbs, nv: 6, tris: 4);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var rewritten = StandardMeshRewriter.Write(original, sm!);
        Assert.Equal(original, rewritten);
    }

    /// <summary>Writing lightmap UVs changes the eight bytes that hold them and nothing else - so the collision
    /// section, the indices, the positions, the normals, the diffuse UVs and the tangent frame all survive.</summary>
    [Theory]
    [InlineData(40u)]
    [InlineData(64u)]
    public void Writing_lightmap_uvs_touches_only_those_bytes(uint vbs)
    {
        var original = BuildMesh(vbs, nv: 5, tris: 3);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var mat = sm!.Lods[0][0];

        var uv = new (float U, float V)[mat.NumVertices];
        for (int v = 0; v < uv.Length; v++) uv[v] = (0.1f * v, 0.9f - 0.1f * v);
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(uv, System.Array.Empty<int>()) } };

        var after = StandardMeshRewriter.Write(original, sm, plan);
        Assert.Equal(original.Length, after.Length);

        // Every differing byte must fall inside one of the eight-byte lightmap windows.
        var allowed = new HashSet<int>();
        for (int v = 0; v < mat.NumVertices; v++)
        {
            int at = vbs == 64
                ? mat.VertexDataOffset + mat.NumVertices * 32 + v * ((int)vbs - 32)
                : mat.VertexDataOffset + v * (int)vbs + 32;
            for (int k = 0; k < 8; k++) allowed.Add(at + k);
        }
        for (int i = 0; i < original.Length; i++)
            if (original[i] != after[i])
                Assert.True(allowed.Contains(i), $"byte {i} changed and is not part of a lightmap UV");

        // And the values really landed.
        Assert.True(StandardMesh.TryParse(after, out var re));
        var reMat = re!.Lods[0][0];
        for (int v = 0; v < uv.Length; v++)
        {
            var got = ReadLmUv(after, reMat, v);
            Assert.Equal(uv[v].U, got.U, 5);
            Assert.Equal(uv[v].V, got.V, 5);
        }
        Assert.Equal(mat.Vertices, reMat.Vertices);
        Assert.Equal(mat.Normals, reMat.Normals);
        Assert.Equal(mat.Uvs, reMat.Uvs);
        Assert.Equal(sm.CollisionSections[0], re.CollisionSections[0]);
    }

    /// <summary>A seam duplicates a vertex. The clone must copy its source verbatim - position, normal, diffuse UV
    /// and, on the planar layout, the tangent frame - and differ only in the lightmap UV.</summary>
    [Theory]
    [InlineData(40u)]
    [InlineData(64u)]
    public void A_cloned_vertex_copies_its_source_except_the_lightmap_uv(uint vbs)
    {
        var original = BuildMesh(vbs, nv: 4, tris: 2);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var mat = sm!.Lods[0][0];

        // Clone vertex 1 and vertex 3, and point the first index at the first clone.
        var clones = new[] { 1, 3 };
        var uv = new (float U, float V)[mat.NumVertices + clones.Length];
        for (int v = 0; v < uv.Length; v++) uv[v] = (0.02f * v, 0.03f * v);
        var idx = (ushort[])mat.RawIndices.Clone();
        idx[0] = (ushort)mat.NumVertices;                                   // the clone of vertex 1

        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(uv, clones, idx) } };
        var after = StandardMeshRewriter.Write(original, sm, plan);

        Assert.True(StandardMesh.TryParse(after, out var re));
        var reMat = re!.Lods[0][0];
        Assert.Equal(mat.NumVertices + clones.Length, reMat.NumVertices);
        Assert.Equal(mat.NumFaceValues, reMat.NumFaceValues);              // a renumbering, not a retopology

        for (int c = 0; c < clones.Length; c++)
        {
            int dst = mat.NumVertices + c, srcV = clones[c];
            Assert.Equal(reMat.Vertices[srcV], reMat.Vertices[dst]);
            Assert.Equal(reMat.Normals[srcV], reMat.Normals[dst]);
            Assert.Equal(reMat.Uvs[srcV], reMat.Uvs[dst]);
            if (vbs == 64)                                                  // the tangent frame rides along
            {
                int fpv = ((int)vbs - 32) / 4;
                for (int k = 2; k < fpv; k++)
                    Assert.Equal(reMat.PlanarExtra![srcV * fpv + k], reMat.PlanarExtra[dst * fpv + k]);
            }
        }
        Assert.Equal(uv[mat.NumVertices].U, ReadLmUv(after, reMat, mat.NumVertices).U, 5);
        Assert.Equal(sm.CollisionSections[0], re.CollisionSections[0]);
    }

    /// <summary>A triangle STRIP survives, because the plan renumbers vertices and never rebuilds the index
    /// buffer. <see cref="SmMaterial.Faces"/> expands a strip naively and reverses a list, so it is a lossy view -
    /// re-deriving indices from it would quietly corrupt every strip mesh.</summary>
    [Fact]
    public void A_triangle_strip_keeps_its_index_buffer()
    {
        var original = BuildMesh(64u, nv: 6, tris: 4);
        // Turn the material into a strip: renderType 5, same index values.
        Assert.True(StandardMesh.TryParse(original, out var probe));
        int rtAt = probe!.Lods[0][0].VertexDataOffset - 24;                 // renderType sits 6 u32 before the data
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(rtAt, 4), 5u);

        Assert.True(StandardMesh.TryParse(original, out var sm));
        Assert.Equal(5u, sm!.Lods[0][0].RenderType);
        var rewritten = StandardMeshRewriter.Write(original, sm);
        Assert.Equal(original, rewritten);

        Assert.True(StandardMesh.TryParse(rewritten, out var re));
        Assert.Equal(sm.Lods[0][0].RawIndices, re!.Lods[0][0].RawIndices);
        Assert.Equal(5u, re.Lods[0][0].RenderType);
    }

    /// <summary>
    /// qflag means "this mesh has a real unwrap", measured across both retail corpora: 227/227 unwrapped
    /// BfVietnam meshes and 144/144 BF1942 ones set it, while 451 of the 452 BfVietnam meshes that own the UV
    /// slot with (0,0) everywhere leave it 0. So filling the channel means setting it.
    /// </summary>
    [Fact]
    public void QFlag_is_captured_and_can_be_set()
    {
        var original = BuildMesh(64u, nv: 4, tris: 2, qflag: 0);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        Assert.Equal(0, sm!.QFlag);

        var lit = StandardMeshRewriter.Write(original, sm, null, new StandardMeshRewriter.Options(SetQFlag: true));
        Assert.True(StandardMesh.TryParse(lit, out var re));
        Assert.Equal(1, re!.QFlag);
        Assert.Equal(original.Length, lit.Length);                          // one byte flipped, nothing moved
        Assert.Equal(sm.CollisionSections[0], re.CollisionSections[0]);
    }

    /// <summary>The u16 index ceiling is a real limit; a split that would cross it must be refused, not truncated.</summary>
    [Fact]
    public void A_split_past_the_u16_ceiling_is_refused()
    {
        var original = BuildMesh(64u, nv: 4, tris: 2);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var clones = new int[65_536];
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(System.Array.Empty<(float, float)>(), clones) } };
        Assert.Throws<System.IO.InvalidDataException>(() => StandardMeshRewriter.Write(original, sm!, plan));
    }

    /// <summary>
    /// Widening a 32-byte vertex to 40 is what reaches the ~1,300 BfVietnam meshes and ALL of BF1942 that have no
    /// lightmap slot at all. The first 32 bytes must survive verbatim; only the new 8 are ours.
    /// </summary>
    [Fact]
    public void Promoting_a_32_byte_material_gives_it_a_lightmap_slot()
    {
        var original = BuildMesh(32u, nv: 5, tris: 3);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var mat = sm!.Lods[0][0];
        Assert.False(mat.HasLightmapUv);

        var uv = new (float U, float V)[mat.NumVertices];
        for (int v = 0; v < uv.Length; v++) uv[v] = (0.1f * v, 0.9f - 0.1f * v);
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(
            uv, System.Array.Empty<int>(), null, StandardMeshRewriter.StridePromotion.To40) } };

        var after = StandardMeshRewriter.Write(original, sm, plan,
                                               new StandardMeshRewriter.Options(SetQFlag: true));
        Assert.True(StandardMesh.TryParse(after, out var re));
        var reMat = re!.Lods[0][0];

        Assert.True(reMat.HasLightmapUv);
        Assert.Equal(40u, reMat.VertexByteSize);
        Assert.Equal(9233u, reMat.VertexFormat);
        Assert.Equal(1, re.QFlag);                                  // qflag 1 = "this mesh has a real unwrap"
        Assert.Equal(mat.Vertices, reMat.Vertices);                 // the first 32 bytes came through untouched
        Assert.Equal(mat.Normals, reMat.Normals);
        Assert.Equal(mat.Uvs, reMat.Uvs);
        Assert.Equal(mat.RawIndices, reMat.RawIndices);
        Assert.Equal(sm.CollisionSections[0], re.CollisionSections[0]);
        for (int v = 0; v < uv.Length; v++)
        {
            var got = ReadLmUv(after, reMat, v);
            Assert.Equal(uv[v].U, got.U, 5);
            Assert.Equal(uv[v].V, got.V, 5);
        }
    }

    /// <summary>
    /// THE SILENT ONE, and the reason these guards exist. A 32-byte vertex has nowhere to put a lightmap UV, so a
    /// plan that supplies them without a promotion would write the clones and the remapped indices and drop every
    /// UV - producing a mesh that is bigger, renumbered, still unlit, and parses perfectly. It must refuse.
    /// </summary>
    [Fact]
    public void Uvs_for_a_material_that_cannot_hold_them_are_refused()
    {
        var original = BuildMesh(32u, nv: 4, tris: 2);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var uv = new (float U, float V)[4];
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(uv, System.Array.Empty<int>()) } };
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => StandardMeshRewriter.Write(original, sm!, plan));
        Assert.Contains("no room for a lightmap UV", ex.Message);
    }

    /// <summary>Half a set of UVs would leave the rest holding whatever their source had - an object lit in
    /// patches. All of them or none.</summary>
    [Fact]
    public void A_partial_set_of_uvs_is_refused()
    {
        var original = BuildMesh(64u, nv: 6, tris: 4);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(
            new (float, float)[3], System.Array.Empty<int>()) } };
        Assert.Throws<System.IO.InvalidDataException>(() => StandardMeshRewriter.Write(original, sm!, plan));
    }

    /// <summary>The 64-byte planar form carries an undecoded tangent frame. Synthesising one is a much bigger
    /// risk than this feature is allowed to take, so promotion to it is refused outright.</summary>
    [Fact]
    public void Promotion_to_the_planar_form_is_refused()
    {
        var original = BuildMesh(32u, nv: 4, tris: 2);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(
            new (float, float)[4], System.Array.Empty<int>(), null,
            new StandardMeshRewriter.StridePromotion(1041u, 64u)) } };
        Assert.Throws<System.IO.InvalidDataException>(() => StandardMeshRewriter.Write(original, sm!, plan));
    }

    /// <summary>A clone that names a vertex which does not exist would read outside the source region.</summary>
    [Fact]
    public void A_clone_naming_a_missing_vertex_is_refused()
    {
        var original = BuildMesh(64u, nv: 4, tris: 2);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(
            System.Array.Empty<(float, float)>(), new[] { 99 }) } };
        Assert.Throws<System.IO.InvalidDataException>(() => StandardMeshRewriter.Write(original, sm!, plan));
    }

    /// <summary>qflag 1 claims the mesh carries a real unwrap. It must not be set on a mesh whose vertices
    /// cannot hold one - that is the same lie one level up.</summary>
    [Fact]
    public void QFlag_cannot_be_set_on_a_mesh_that_cannot_hold_an_unwrap()
    {
        var original = BuildMesh(32u, nv: 4, tris: 2);
        Assert.True(StandardMesh.TryParse(original, out var sm));
        var plan = new[] { new[] { new StandardMeshRewriter.MaterialPlan(
            System.Array.Empty<(float, float)>(), System.Array.Empty<int>()) } };
        Assert.Throws<System.IO.InvalidDataException>(
            () => StandardMeshRewriter.Write(original, sm!, plan, new StandardMeshRewriter.Options(SetQFlag: true)));
    }
}
