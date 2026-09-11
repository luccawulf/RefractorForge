using System.Numerics;
using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Shipping an unwrapped mesh into a level. The patched <c>.sm</c> cannot replace the one in the base archive -
/// base archives are mounted first and win - so it goes in the level's own archive and is referenced BY PATH,
/// the route decals and imported models already take and which is therefore known to load.
/// </summary>
public class LightmapMeshPatchTests
{
    /// <summary>A 32-byte box: six hard-edged faces and no lightmap channel, like most of the corpus.</summary>
    private static byte[] Box(uint renderType = 4u)
    {
        var p = new List<Vector3>();
        var idx = new List<ushort>();
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int b0 = p.Count;
            p.Add(a); p.Add(b); p.Add(c); p.Add(d);
            foreach (var t in new[] { (0, 1, 2), (0, 2, 3) })
            { idx.Add((ushort)(b0 + t.Item3)); idx.Add((ushort)(b0 + t.Item2)); idx.Add((ushort)(b0 + t.Item1)); }
        }
        float x = 1.5f, y = 2f, z = 2.5f;
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
        w.Write((uint)0);
        w.Write((uint)1); w.Write((uint)1);
        var nm = Encoding.Latin1.GetBytes("crate_Material0");
        w.Write((uint)nm.Length); w.Write(nm); w.Write(new byte[12]);
        w.Write(renderType); w.Write((uint)1041); w.Write((uint)32);
        w.Write((uint)p.Count); w.Write((uint)idx.Count); w.Write((uint)0);
        foreach (var v in p)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            var n = Vector3.Normalize(v);
            w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
            w.Write(0f); w.Write(0f);
        }
        foreach (var i in idx) w.Write(i);
        w.Write((uint)0); w.Write((uint)0);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The marker goes BEFORE the LOD tag. A trailing one would key the patched mesh's lightmaps
    /// differently from every other mesh in the level, because the matcher strips <c>_M1</c>/<c>_M2</c>.</summary>
    [Theory]
    [InlineData("o_crate_m1", "o_crate_lm_m1")]
    [InlineData("O_Crate_M2", "O_Crate_lm_M2")]
    [InlineData("o_crate", "o_crate_lm")]
    [InlineData("standardMesh/o_crate_m1.sm", "o_crate_lm_m1")]
    public void The_patched_name_keeps_the_lod_tag_last(string input, string expected)
        => Assert.Equal(expected, LightmapMeshPatch.PatchedName(input));

    /// <summary>The full level-local set, with the mesh referenced by PATH so archive precedence never matters.</summary>
    [Fact]
    public void It_emits_the_level_local_file_set()
    {
        var built = LightmapMeshPatch.Build("Test_Level", "o_crate_m1", Box(), "rem shader\r\n",
                                            "bfvietnam", out var status);
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.Ok, status);
        Assert.NotNull(built);

        var paths = built!.Files.Select(f => f.RelPath).ToList();
        Assert.Contains("StandardMesh/o_crate_lm_m1.sm", paths);
        Assert.Contains("StandardMesh/o_crate_lm_m1.rs", paths);
        Assert.Contains("Objects/o_crate_lm_m1/Geometries.con", paths);
        Assert.Contains("Objects/o_crate_lm_m1/Objects.con", paths);
        Assert.Contains("Objects/o_crate_lm_m1/o_crate_lm_m1.con", paths);
        Assert.Equal("run o_crate_lm_m1/o_crate_lm_m1", built.RunLine);

        var geom = Encoding.Latin1.GetString(built.Files.First(f => f.RelPath.EndsWith("Geometries.con")).Bytes);
        Assert.Contains("GeometryTemplate.file ../bfvietnam/levels/Test_Level/StandardMesh/o_crate_lm_m1", geom);
    }

    /// <summary>The shipped mesh really carries the unwrap - a widened vertex, real UVs, and the qflag that
    /// says so.</summary>
    [Fact]
    public void The_shipped_mesh_carries_a_real_unwrap()
    {
        var built = LightmapMeshPatch.Build("Test_Level", "o_crate_m1", Box(), null, "bfvietnam", out _);
        var sm = built!.Files.First(f => f.RelPath.EndsWith(".sm")).Bytes;
        Assert.True(StandardMesh.TryParse(sm, out var parsed));

        var mat = parsed!.Lods[0][0];
        Assert.True(mat.HasLightmapUv);
        Assert.Equal(40u, mat.VertexByteSize);
        Assert.Equal(9233u, mat.VertexFormat);
        Assert.Equal(1, parsed.QFlag);
        Assert.Contains(mat.LightmapUvs, t => MathF.Abs(t.U) > 1e-6f || MathF.Abs(t.V) > 1e-6f);

        // The original geometry is untouched - only the second UV set and the vertex width changed.
        Assert.True(StandardMesh.TryParse(Box(), out var before));
        for (int v = 0; v < before!.Lods[0][0].NumVertices; v++)
        {
            Assert.Equal(before.Lods[0][0].Vertices[v], mat.Vertices[v]);
            Assert.Equal(before.Lods[0][0].Uvs[v], mat.Uvs[v]);
        }
    }

    /// <summary>The shader is copied verbatim, so the patched mesh keeps exactly the textures and render states
    /// the original had. Without it the object draws untextured.</summary>
    [Fact]
    public void The_shader_is_copied_verbatim()
    {
        const string rs = "texture \"texture/crate\";\r\nmaterialDiffuse 1/1/1;\r\n";
        var built = LightmapMeshPatch.Build("Test_Level", "o_crate_m1", Box(), rs, "bfvietnam", out _);
        var text = Encoding.Latin1.GetString(built!.Files.First(f => f.RelPath.EndsWith(".rs")).Bytes);
        Assert.Contains("texture \"texture/crate\"", text);
        Assert.Contains("materialDiffuse 1/1/1", text);
    }

    /// <summary>A mesh that cannot be unwrapped emits NOTHING. Half a patch is worse than none: a fatter,
    /// renumbered mesh whose qflag claims an unwrap it does not carry would fail only in game.</summary>
    [Fact]
    public void A_mesh_that_cannot_be_unwrapped_emits_nothing()
    {
        var built = LightmapMeshPatch.Build("Test_Level", "o_crate_m1", Box(renderType: 5u), null,
                                            "bfvietnam", out var status);
        Assert.Null(built);
        Assert.Equal(LightmapUnwrapper.UnwrapStatus.UnsupportedTopology, status);
    }

    /// <summary>Running it twice is a no-op, not a way to produce o_crate_lm_lm_m1.</summary>
    [Fact]
    public void Patching_the_same_mesh_twice_is_detected()
    {
        var existing = new[] { "levels/Test_Level/Objects/o_crate_lm_m1/Geometries.con" };
        Assert.True(LightmapMeshPatch.AlreadyPatched("o_crate_m1", existing));
        Assert.False(LightmapMeshPatch.AlreadyPatched("o_barrel_m1", existing));
    }

    /// <summary>The diagnostics come back with the patch, so a caller can report what it cost and what size the
    /// unwrap needs to be baked at.</summary>
    [Fact]
    public void It_reports_what_the_unwrap_cost()
    {
        var built = LightmapMeshPatch.Build("Test_Level", "o_crate_m1", Box(), null, "bfvietnam", out _);
        var d = built!.Diagnostics;
        Assert.InRange(d.Charts, 1, 6);
        Assert.True(d.VerticesOut >= d.VerticesIn);
        Assert.InRange(d.MinBakeSize, 64, 1024);
    }
}
