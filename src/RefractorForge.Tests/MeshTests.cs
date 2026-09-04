using System.Numerics;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

public class MeshTests
{
    static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

    [Fact]
    public void ObjMesh_to_StandardMesh_roundtrip_quad_and_multi_material()
    {
        string quad = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\nf 1/1/1 3/3/1 4/4/1\n";
        var om = ObjMesh.Parse(quad);
        Assert.True(om.SubMeshes.Count == 1 && om.TotalVertices == 4 && om.TotalFaces == 2, $"parsed quad (1 mat, {om.TotalVertices} verts, {om.TotalFaces} tris)");

        var sm = StandardMesh.Parse(StandardMeshWriter.Write(om));
        Assert.True(sm.Version == 10 && sm.NumLods == 1 && sm.Lods.Count == 1, "wrote v10 single-LOD mesh");
        Assert.True(sm.Total - sm.Consumed == 8, $"reader consumed everything but the 8-byte trailing section ({sm.Consumed}/{sm.Total})");
        var mat = sm.Lods[0][0];
        Assert.True(sm.Lods[0].Count == 1 && mat.NumVertices == 4 && mat.Faces.Length == 2, "round-trips 1 section, 4 verts, 2 tris");
        Assert.True(mat.Faces[0] == (0, 1, 2) && mat.Faces[1] == (0, 2, 3), "triangle winding preserved");
        Assert.True(Near(mat.Vertices[1].X, 1f) && Near(mat.Vertices[2].Y, 1f), "positions round-trip");
        Assert.True(Near(mat.Uvs[2].U, 1f) && Near(mat.Uvs[2].V, 1f), "uvs round-trip");
        Assert.True(Near(mat.Normals[0].Z, 1f), "normals round-trip");
        Assert.True(Near(sm.BoundingBox[0], 0f) && Near(sm.BoundingBox[3], 1f) && Near(sm.BoundingBox[4], 1f), "bbox round-trips");

        var noN = ObjMesh.Parse("v 0 0 0\nv 1 0 0\nv 1 1 0\nf 1 2 3\n");
        Assert.True(noN.TotalFaces == 1 && Near(MathF.Abs(noN.SubMeshes[0].Normals[0].Z), 1f), "missing normals computed from faces");

        var multi = ObjMesh.Parse("v 0 0 0\nv 1 0 0\nv 0 1 0\nv 2 0 0\nv 3 0 0\nv 2 1 0\nusemtl red\nf 1 2 3\nusemtl blue\nf 4 5 6\n");
        Assert.True(multi.SubMeshes.Count == 2, $"two usemtl -> two submeshes ({multi.SubMeshes.Count})");
        var sm2 = StandardMesh.Parse(StandardMeshWriter.Write(multi));
        Assert.True(sm2.Lods[0].Count == 2 && sm2.Lods[0][0].Name == "red" && sm2.Lods[0][1].Name == "blue", "material names round-trip");

        var rmesh = MeshLibrary.MeshFromObj(multi);
        Assert.True(rmesh.Positions.Length == multi.TotalVertices && rmesh.Triangles == multi.TotalFaces && rmesh.Parts.Length == 2, "MeshFromObj keeps counts");
    }

    [Fact]
    public void ObjMtl_and_RsShaderSet_roundtrip()
    {
        var mtl = ObjMtl.Parse("newmtl wood\nKd 0.6 0.4 0.2\nmap_Kd textures/oak.png\nnewmtl glass\nKd 0.1 0.2 0.9\n");
        Assert.True(mtl.Count == 2 && Near(mtl["wood"].Diffuse.X, 0.6f) && mtl["wood"].TextureName == "oak" && mtl["glass"].TextureFile is null, ".mtl parses");
        var rsText = RsShaderSet.Write(new (string, string?, Vector3)[] {
            ("wood", "oak", new Vector3(0.6f, 0.4f, 0.2f)), ("glass", null, new Vector3(0.1f, 0.2f, 0.9f)) });
        var rs = RsShaderSet.Parse(rsText);
        Assert.True(rs.Materials.Count == 2 && rs.Materials["wood"].Texture == "oak" && Near(rs.Materials["wood"].Diffuse.X, 0.6f) && rs.Materials["glass"].Texture is null, ".rs round-trips");
        var withMtl = ObjMesh.Parse("mtllib scene.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl wood\nf 1 2 3\n");
        Assert.True(withMtl.MtlLibs.Count == 1 && withMtl.MtlLibs[0] == "scene.mtl" && withMtl.SubMeshes[0].Material == "wood", "mtllib + usemtl captured");
    }

    [Fact]
    public void Collision_section_capture_parse_generate_roundtrip()
    {
        // Capture
        {
            var cms = new MemoryStream(); var cw = new BinaryWriter(cms);
            cw.Write((uint)10); cw.Write(new byte[4]); for (int i = 0; i < 6; i++) cw.Write(0f); cw.Write((byte)0);
            cw.Write((uint)1);
            cw.Write((uint)8); cw.Write(0xEB97C2FAu); cw.Write((uint)0);
            cw.Write((uint)0);
            var smc = StandardMesh.Parse(cms.ToArray());
            Assert.True(smc.NumCollisionMeshes == 1 && smc.CollisionSections.Count == 1 && smc.CollisionSections[0].Length == 8, "reader captures collision section");
            Assert.True(BitConverter.ToUInt32(smc.CollisionSections[0], 0) == 0xEB97C2FA, "captured section has magic");
        }
        // Parse verts + tris
        {
            var pms = new MemoryStream(); var pw = new BinaryWriter(pms);
            pw.Write(0xEB97C2FAu); pw.Write((uint)5); pw.Write((uint)4);
            float[,] vv = { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 0, 1 }, { 0, 0, 1 } };
            for (int i = 0; i < 4; i++) { pw.Write(vv[i, 0]); pw.Write(vv[i, 1]); pw.Write(vv[i, 2]); pw.Write(0f); }
            pw.Write((uint)2);
            pw.Write((ushort)0); pw.Write((ushort)1); pw.Write((ushort)2); pw.Write((ushort)99);
            pw.Write((ushort)0); pw.Write((ushort)2); pw.Write((ushort)3); pw.Write((ushort)99);
            bool ok = StandardMesh.TryParseCollision(pms.ToArray(), out var cverts, out var cidx);
            Assert.True(ok && cverts.Length == 4 && cidx.Length == 6, "collision section parses to 4 verts / 2 tris");
            Assert.True(ok && cidx[0] == 0 && cidx[1] == 1 && cidx[2] == 2 && cidx[5] == 3, "collision triangle indices decode");
            Assert.True(!StandardMesh.TryParseCollision(new byte[] { 1, 2, 3, 4 }, out _, out _), "garbage collision section rejected");
        }
        // Full parse + writer round-trip
        {
            var oms = new MemoryStream(); var ow = new BinaryWriter(oms);
            ow.Write(0xEB97C2FAu); ow.Write((uint)5); ow.Write((uint)3);
            for (int i = 0; i < 3; i++) { ow.Write((float)i); ow.Write(0.5f); ow.Write(-(float)i); ow.Write(9f); }
            ow.Write((uint)1); ow.Write((ushort)0); ow.Write((ushort)1); ow.Write((ushort)2); ow.Write((ushort)42);
            ow.Write(new byte[] { 7, 0, 0, 0, 1, 2, 3 });
            var orig = oms.ToArray();
            bool fok = StandardMesh.TryParseCollisionFull(orig, out var cd);
            Assert.True(fok && cd.VertexCount == 3 && cd.TriangleCount == 1 && cd.Tail.Length == 7, "full collision parse");
            Assert.True(fok && StandardMeshWriter.WriteCollisionSection(cd).AsSpan().SequenceEqual((ReadOnlySpan<byte>)orig), "collision section round-trips byte-exact");
        }
        // Generate and embed
        {
            var gv = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
            var gt = new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) };
            var gsec = StandardMeshWriter.BuildCollisionSection(gv, gt);
            Assert.True(StandardMesh.TryParseCollision(gsec, out var gpv, out var gpi) && gpv.Length == 4 && gpi.Length == 6, "generated collision section parses back");
            string quad = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\nf 1/1/1 3/3/1 4/4/1\n";
            var om = ObjMesh.Parse(quad);
            var withCol = StandardMesh.Parse(StandardMeshWriter.Write(om, gsec));
            Assert.True(withCol.NumCollisionMeshes == 1 && withCol.Total - withCol.Consumed == 8 && StandardMesh.TryParseCollision(withCol.CollisionSections[0], out _, out var ei) && ei.Length == 6, ".sm embeds + re-reads collision");
        }
    }
}
