using System.IO;
using System.Text;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The Battlefield toolkit's model conventions, read out of its own tools (the Mod Development Toolkit's MAXScript
/// exporter and <c>3dsToSm.exe</c>) and checked against the meshes it produced:
///
///  - object NAMES carry the roles (LOD01.., COL01, COL02, shadow, bbox) - there was never a file format for it;
///  - collision is a "SimpleBSP": one node per face, verified byte for byte against Saigon68's props;
///  - the shadow mesh is one exploded, unlit material section after the LODs;
///  - the engine reads a triangle CLOCKWISE from outside (every retail mesh does), so an OBJ or a Blender export
///    is turned over on import and a Max scene's Y/Z swap does the turning by itself.
///
/// The last point is why an imported model was invisible in game while the editor, which draws both sides, showed
/// it perfectly.
/// </summary>
public class ToolkitConventionTests
{
    // ---- a .3ds writer, so the reader is tested against the format rather than against itself ----------------

    static byte[] Chunk(ushort id, params byte[][] payload)
    {
        using var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        int len = 6; foreach (var p in payload) len += p.Length;
        w.Write(id); w.Write((uint)len); foreach (var p in payload) w.Write(p);
        return ms.ToArray();
    }
    static byte[] Str(string s) => Encoding.ASCII.GetBytes(s + "\0");
    static byte[] Bytes(params byte[] b) => b;
    static byte[] F(params float[] f) { using var ms = new MemoryStream(); var w = new BinaryWriter(ms); foreach (var v in f) w.Write(v); return ms.ToArray(); }
    static byte[] U16(params int[] u) { using var ms = new MemoryStream(); var w = new BinaryWriter(ms); foreach (var v in u) w.Write((ushort)v); return ms.ToArray(); }

    /// <summary>A Z-up cube standing on z=0, 2 m on a side: the 3ds exporter's output for a Max box.</summary>
    static (float[] v, int[] f) CubeZUp(float size = 2f)
    {
        float s = size / 2f;
        float[] v = { -s,-s,0, s,-s,0, s,s,0, -s,s,0, -s,-s,size, s,-s,size, s,s,size, -s,s,size };
        // Counter-clockwise seen from OUTSIDE, in Max's right-handed frame.
        int[] f = { 0,2,1, 0,3,2,  4,5,6, 4,6,7,  0,1,5, 0,5,4,  1,2,6, 1,6,5,  2,3,7, 2,7,6,  3,0,4, 3,4,7 };
        return (v, f);
    }

    static byte[] Object3ds(string name, float[] v, int[] f, string material)
    {
        int nv = v.Length / 3, nf = f.Length / 3;
        var verts = new MemoryStream(); var vw = new BinaryWriter(verts); vw.Write((ushort)nv); foreach (var x in v) vw.Write(x);
        var uvs = new MemoryStream(); var uw = new BinaryWriter(uvs); uw.Write((ushort)nv); for (int i = 0; i < nv; i++) { uw.Write(v[i * 3] * 0.25f + 0.5f); uw.Write(v[i * 3 + 1] * 0.25f + 0.5f); }
        var faces = new MemoryStream(); var fw = new BinaryWriter(faces); fw.Write((ushort)nf);
        for (int i = 0; i < nf; i++) { fw.Write((ushort)f[i * 3]); fw.Write((ushort)f[i * 3 + 1]); fw.Write((ushort)f[i * 3 + 2]); fw.Write((ushort)7); }
        var fm = new MemoryStream(); var mw = new BinaryWriter(fm); mw.Write(Str(material)); mw.Write((ushort)nf); for (int i = 0; i < nf; i++) mw.Write((ushort)i);
        var trimesh = Chunk(0x4100, Chunk(0x4110, verts.ToArray()), Chunk(0x4140, uvs.ToArray()), Chunk(0x4120, faces.ToArray(), Chunk(0x4130, fm.ToArray())));
        return Chunk(0x4000, Str(name), trimesh);
    }

    static byte[] Material3ds(string name, string texture) =>
        Chunk(0xAFFF, Chunk(0xA000, Str(name)), Chunk(0xA020, Chunk(0x0011, Bytes(200, 100, 50))), Chunk(0xA200, Chunk(0xA300, Str(texture))));

    static byte[] Scene3ds(float masterScale, params byte[][] objects)
    {
        var editor = new System.Collections.Generic.List<byte[]> { Chunk(0x3D3E, F(3f)), Chunk(0x0100, F(masterScale)), Material3ds("crate", "crate.tga") };
        editor.AddRange(objects);
        return Chunk(0x4D4D, Chunk(0x0002, F(3f)), Chunk(0x3D3D, editor.ToArray()));
    }

    static bool Near(float a, float b, float eps = 1e-4f) => System.MathF.Abs(a - b) < eps;

    /// <summary>The geometric normal of the PARSED triangle order against the stored normals: negative means the
    /// order is clockwise from outside, which is what every retail mesh measures as.</summary>
    static (int agree, int disagree) Winding(StandardMesh sm)
    {
        int agree = 0, disagree = 0;
        foreach (var m in sm.Lods[0])
            foreach (var (a, b, c) in m.Faces)
            {
                var pa = m.Vertices[a]; var pb = m.Vertices[b]; var pc = m.Vertices[c];
                float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z, wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
                float gx = uy * wz - uz * wy, gy = uz * wx - ux * wz, gz = ux * wy - uy * wx;
                var n = m.Normals[a];
                float d = gx * n.X + gy * n.Y + gz * n.Z;
                if (d > 0) agree++; else if (d < 0) disagree++;
            }
        return (agree, disagree);
    }

    // ---- .3ds -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_3ds_file_reads_back_with_its_objects_materials_and_scale()
    {
        var (v, f) = CubeZUp();
        var data = Scene3ds(0.5f, Object3ds("LOD01", v, f, "crate"), Object3ds("COL01", v, f, "crate"));
        var r = ThreeDsMesh.Parse(data);

        Assert.Equal(0.5f, r.MasterScale);
        Assert.Equal(2, r.Mesh.SubMeshes.Count);
        Assert.Equal("LOD01", r.Mesh.SubMeshes[0].Object);
        Assert.Equal("COL01", r.Mesh.SubMeshes[1].Object);
        Assert.Equal(12, r.Mesh.SubMeshes[0].Faces.Count);
        Assert.Equal(8, r.Mesh.SubMeshes[0].Positions.Count);
        // Master scale applied; still Z-up, the way the file has it.
        Assert.True(Near(r.Mesh.BoundingBox[5], 1f) && Near(r.Mesh.BoundingBox[2], 0f), "2 m cube at scale 0.5 stands 1 m tall in Z");
        Assert.True(r.Materials.TryGetValue("crate", out var mat) && mat.TextureFile == "crate.tga", "the material and its map came through");
        Assert.True(Near(mat!.Diffuse.X, 200 / 255f) && Near(mat.Diffuse.Z, 50 / 255f), "rgb8 diffuse");
        Assert.Equal(8, r.Mesh.SubMeshes[0].Uvs.Count);
        Assert.Throws<InvalidDataException>(() => ThreeDsMesh.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void A_3ds_cube_through_the_toolkits_axis_swap_winds_clockwise_in_the_engine()
    {
        var (v, f) = CubeZUp();
        var r = ThreeDsMesh.Parse(Scene3ds(1f, Object3ds("LOD01", v, f, "crate")));
        var parts = MeshParts.Split(r.Mesh);
        var mesh = parts.Lods[0];
        MeshFit.Apply(mesh, new MeshFitOptions { Up = UpAxis.ZSwap, Origin = OriginMode.Keep });
        Assert.True(Near(mesh.BoundingBox[4], 2f) && Near(mesh.BoundingBox[1], 0f), "Z became Y: the cube stands on the ground");

        var sm = StandardMesh.Parse(StandardMeshWriter.Write(mesh));
        var (agree, disagree) = Winding(sm);
        Assert.True(agree == 0 && disagree == 12, $"a Max cube must land clockwise like every retail mesh (agree {agree}, disagree {disagree})");
    }

    // ---- the naming convention ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("LOD01", true, "lod", 1)]
    [InlineData("lod2_body", true, "lod", 2)]
    [InlineData("Lod", true, "lod", 1)]
    [InlineData("COL02", true, "col", 2)]
    [InlineData("col", true, "col", 1)]
    [InlineData("collar", false, "", 0)]
    [InlineData("shadow", true, "shadow", 1)]
    [InlineData("Shadow_mesh", true, "shadow", 1)]
    [InlineData("shaft", false, "", 0)]
    [InlineData("bbox", true, "bounds", 1)]
    [InlineData("bounds", true, "bounds", 1)]
    [InlineData("Cube.001", false, "", 0)]
    [InlineData("", false, "", 0)]
    public void Object_names_declare_their_role_the_way_the_exporter_reads_them(string name, bool matched, string role, int number)
    {
        var (ok, r, n) = MeshParts.Classify(name);
        Assert.Equal(matched, ok);
        if (ok) { Assert.Equal(role, r); Assert.Equal(number, n); }
    }

    const string Tri = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\n";

    [Fact]
    public void A_scene_is_taken_apart_by_its_object_names()
    {
        string obj = Tri +
            "o LOD01\nusemtl wood\nf 1/1 2/2 3/3\n" +
            "o LOD01_extra\nusemtl wood\nf 1/1 2/2 3/3\n" +      // same role and number: merged
            "o LOD02\nusemtl wood\nf 1/1 2/2 3/3\n" +
            "o COL01\nf 1 2 3\n" +
            "o COL02\nf 1 2 3\nf 1 2 3\n" +
            "o shadow\nf 1 2 3\n" +
            "o bbox\nf 1 2 3\n" +
            "o Cube.003\nusemtl wood\nf 1/1 2/2 3/3\n";           // not in the convention: folded into LOD01, reported
        var parts = MeshParts.Split(ObjMesh.Parse(obj));

        Assert.True(parts.UsedConvention);
        Assert.Equal(2, parts.Lods.Count);
        Assert.Equal(3, parts.Lods[0].TotalFaces);                  // LOD01 + LOD01_extra + Cube.003
        Assert.Equal(1, parts.Lods[1].TotalFaces);
        Assert.Single(parts.Lods[0].SubMeshes);                     // merged back to one piece per material
        Assert.NotNull(parts.CollisionSimple); Assert.Equal(1, parts.CollisionSimple!.TotalFaces);
        Assert.NotNull(parts.CollisionComplex); Assert.Equal(2, parts.CollisionComplex!.TotalFaces);
        Assert.NotNull(parts.Shadow);
        Assert.NotNull(parts.Bounds);
        Assert.Equal(new[] { "Cube.003" }, parts.Unclassified);
        Assert.Contains("COL01 1", parts.Describe());
    }

    [Fact]
    public void A_scene_without_the_convention_is_one_visible_model_as_before()
    {
        string obj = Tri + "o Cube\nusemtl a\nf 1/1 2/2 3/3\no Sphere\nusemtl b\nf 1/1 2/2 3/3\no Cube2\nusemtl a\nf 1/1 3/3 2/2\n";
        var parts = MeshParts.Split(ObjMesh.Parse(obj));
        Assert.False(parts.UsedConvention);
        Assert.Single(parts.Lods);
        Assert.Equal(3, parts.Lods[0].TotalFaces);
        Assert.Equal(2, parts.Lods[0].SubMeshes.Count);              // one piece per material, objects folded
        Assert.Null(parts.CollisionSimple); Assert.Null(parts.Shadow);
        Assert.Empty(parts.Unclassified);
    }

    // ---- winding -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_obj_import_is_turned_over_to_the_engines_clockwise_order()
    {
        // OBJ: counter-clockwise from outside. Written raw, it would be back-facing in game.
        string cube = "v -1 -1 -1\nv 1 -1 -1\nv 1 1 -1\nv -1 1 -1\nv -1 -1 1\nv 1 -1 1\nv 1 1 1\nv -1 1 1\n" +
                      "f 1 3 2\nf 1 4 3\nf 5 6 7\nf 5 7 8\nf 1 2 6\nf 1 6 5\nf 2 3 7\nf 2 7 6\nf 3 4 8\nf 3 8 7\nf 4 1 5\nf 4 5 8\n";
        var raw = ObjMesh.Parse(cube);
        var rawSm = StandardMesh.Parse(StandardMeshWriter.Write(raw));
        Assert.Equal((12, 0), Winding(rawSm));                       // the bug: every face counter-clockwise

        var fitted = ObjMesh.Parse(cube);
        MeshFit.Apply(fitted, new MeshFitOptions { Up = UpAxis.Y, Origin = OriginMode.Keep });
        var sm = StandardMesh.Parse(StandardMeshWriter.Write(fitted));
        Assert.Equal((0, 12), Winding(sm));                          // fitted: clockwise, like every retail mesh

        // A Z-up (Blender) source: the rotation keeps handedness, so it is turned over too.
        var zup = ObjMesh.Parse(cube);
        MeshFit.Apply(zup, new MeshFitOptions { Up = UpAxis.Z, Origin = OriginMode.Keep });
        Assert.Equal((0, 12), Winding(StandardMesh.Parse(StandardMeshWriter.Write(zup))));

        // A source that already winds the engine's way (a mesh exported from a retail .sm) is left alone.
        var cw = ObjMesh.Parse(cube);
        MeshFit.Apply(cw, new MeshFitOptions { Up = UpAxis.Y, Origin = OriginMode.Keep, FrontFaces = FaceWinding.Clockwise });
        Assert.Equal((12, 0), Winding(StandardMesh.Parse(StandardMeshWriter.Write(cw))));
    }

    // ---- SimpleBSP collision -------------------------------------------------------------------------------------

    [Fact]
    public void The_collision_section_is_the_exporters_simplebsp_byte_for_byte()
    {
        // A floor quad in the engine's clockwise order, like DC_roadbarrier1's (0,2,3): its node normal is (0,-1,0).
        var v = new[] { new Vec3(-0.5f, 0, -3), new Vec3(0.5f, 0, -3), new Vec3(-0.5f, 0, 3), new Vec3(0.5f, 0, 3) };
        var t = new[] { (0, 2, 3), (3, 1, 0) };
        var sec = StandardMeshWriter.BuildCollisionSection(v, t, 92);

        Assert.Equal(58 + 16 * 4 + 44 * 2, sec.Length);              // the exporter's own size formula
        using var r = new BinaryReader(new MemoryStream(sec));
        Assert.Equal(0xEB97C2FAu, r.ReadUInt32()); Assert.Equal(5u, r.ReadUInt32()); Assert.Equal(4u, r.ReadUInt32());
        for (int i = 0; i < 4; i++)
        {
            float x = r.ReadSingle(); r.ReadSingle(); r.ReadSingle();
            uint w = r.ReadUInt32();
            Assert.Equal(92, (int)(w & 0xFFFF));                     // the material rides in x's low half
            Assert.Equal((uint)System.BitConverter.SingleToInt32Bits(x) & 0xFFFF0000u, w & 0xFFFF0000u);
        }
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal((0, 2, 3, 92), (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16()));
        Assert.Equal((3, 1, 0, 92), (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16()));
        Assert.Equal(2u, r.ReadUInt32()); Assert.Equal(0u, r.ReadUInt32()); Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal((0f, -1f, 0f), (r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));   // left-hand normal: outward
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal((0u, 2u, 3u, 92u), (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
        r.ReadBytes(32);                                              // the second node
        Assert.Equal("SimpleBSP tree method  \0", Encoding.ASCII.GetString(r.ReadBytes(24)));
        Assert.Equal(2u, r.ReadUInt32()); Assert.Equal(0u, r.ReadUInt32()); Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal(sec.Length, r.BaseStream.Position);

        // It still reads back through both parsers, and the tail is exactly the BSP.
        Assert.True(StandardMesh.TryParseCollision(sec, out var pv, out var pi) && pv.Length == 4 && pi.Length == 6);
        Assert.True(StandardMesh.TryParseCollisionFull(sec, out var full) && full.Tail.Length == 12 + 32 * 2 + 24 + 4 + 4 * 2 + 2);
    }

    // ---- the shadow block ----------------------------------------------------------------------------------------

    [Fact]
    public void A_shadow_mesh_is_written_after_the_lods_and_reads_back()
    {
        var vis = ObjMesh.Parse(Tri + "usemtl wood\nf 1/1 2/2 3/3\n");
        var shadow = ObjMesh.Parse("v 0 0 0\nv 2 0 0\nv 0 2 0\nv 2 2 0\nf 1 2 3\nf 2 4 3\n");
        var bytes = StandardMeshWriter.Write(new[] { vis }, System.Array.Empty<byte[]>(), shadow, "thing_", null);
        var sm = StandardMesh.Parse(bytes);

        Assert.NotNull(sm.Shadow);
        Assert.Equal("thing_", sm.Shadow!.Name);
        Assert.Equal(6, sm.Shadow.Vertices.Length);                  // exploded: three per triangle, like the exporter
        Assert.Equal(2, sm.Shadow.Faces.Length);
        Assert.Equal((0, 1, 2), sm.Shadow.Faces[0]);                 // the shadow's faces are NOT reversed
        Assert.True(Near(sm.Shadow.Vertices[3].X, 2f) && Near(sm.Shadow.Vertices[5].Y, 2f), "positions came through");
        Assert.Equal(0, sm.TrailerLength);

        // A mesh with no shadow still ends in the eight bytes every gate counts on.
        var plain = StandardMesh.Parse(StandardMeshWriter.Write(vis));
        Assert.Null(plain.Shadow);
        Assert.Equal(8, plain.Total - plain.Consumed);
    }

    // ---- the whole object -----------------------------------------------------------------------------------------

    [Fact]
    public void A_model_with_the_toolkits_parts_becomes_an_object_with_all_of_them()
    {
        string obj = Tri +
            "o LOD01\nusemtl wood\nf 1/1 2/2 3/3\nf 1/1 3/3 2/2\n" +
            "o LOD02\nusemtl wood\nf 1/1 2/2 3/3\n" +
            "o COL01\nf 1 2 3\n" +
            "o COL02\nf 1 2 3\nf 1 3 2\n" +
            "o shadow\nf 1 2 3\n";
        var parts = MeshParts.Split(ObjMesh.Parse(obj));
        var b = ModelObject.Build("Test_Map", "hut", parts.Lods[0],
            new[] { new ModelObject.Material("wood", null, new Vec3(1, 1, 1)) }, null,
            collision: false, extraLods: parts.Lods.Skip(1).ToList(),
            collisionMeshes: parts.CollisionMeshes.ToList(), collisionMaterial: 92, shadow: parts.Shadow);

        Assert.Equal(2, b.LodCount);
        Assert.True(b.HasCollision); Assert.Equal(2, b.CollisionCount); Assert.True(b.HasShadow);
        var sm = StandardMesh.Parse(b.Files.First(f => f.RelPath == "StandardMesh/hut.sm").Bytes);
        Assert.Equal(2, sm.NumLods);
        Assert.Equal(2, sm.NumCollisionMeshes);
        Assert.True(StandardMesh.TryParseCollision(sm.CollisionSections[1], out _, out var ci) && ci.Length == 6, "COL02 is the second section");
        Assert.Contains("SimpleBSP", Encoding.ASCII.GetString(sm.CollisionSections[0]));
        Assert.NotNull(sm.Shadow); Assert.Equal("hut_", sm.Shadow!.Name);
        var oc = Encoding.UTF8.GetString(b.Files.First(f => f.RelPath == "Objects/hut/Objects.con").Bytes);
        Assert.Contains("HasCollisionPhysics 1", oc);
    }
}
