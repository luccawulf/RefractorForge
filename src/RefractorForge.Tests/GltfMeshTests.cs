using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RefractorForge.Formats.Mesh;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The native glTF reader: a downloaded model (Poly Haven, Poly Pizza, Sketchfab all serve glTF) must come in without
/// Blender, and come in exactly as the Blender bridge would have delivered it - same coordinates, same winding,
/// V flipped to OBJ's convention - because everything after the reader was built and checked against that.
/// </summary>
public class GltfMeshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_gltf_" + Guid.NewGuid().ToString("N")[..8]);
    public GltfMeshTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>A 1x1 PNG (opaque white), as a model would carry its texture.</summary>
    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    /// <summary>Lays out binary data for a test glTF and writes the JSON around it.</summary>
    private sealed class Fixture
    {
        private readonly List<byte> _bin = new();
        private readonly List<string> _views = new(), _accessors = new();

        private int View(byte[] data)
        {
            while (_bin.Count % 4 != 0) _bin.Add(0);
            int off = _bin.Count;
            _bin.AddRange(data);
            _views.Add($"{{\"buffer\":0,\"byteOffset\":{off},\"byteLength\":{data.Length}}}");
            return _views.Count - 1;
        }

        public int Floats(string type, params float[] values)
        {
            var b = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]);
            int comps = type switch { "VEC2" => 2, "VEC3" => 3, _ => 1 };
            _accessors.Add($"{{\"bufferView\":{View(b)},\"componentType\":5126,\"count\":{values.Length / comps},\"type\":\"{type}\"}}");
            return _accessors.Count - 1;
        }

        public int UShorts(params ushort[] values)
        {
            var b = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), values[i]);
            _accessors.Add($"{{\"bufferView\":{View(b)},\"componentType\":5123,\"count\":{values.Length},\"type\":\"SCALAR\"}}");
            return _accessors.Count - 1;
        }

        public int Image(byte[] data) => View(data);

        public string Json(string body, string? bufferUri = null) =>
            "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{" + (bufferUri is null ? "" : $"\"uri\":\"{bufferUri}\",") +
            $"\"byteLength\":{_bin.Count}}}],\"bufferViews\":[{string.Join(",", _views)}],\"accessors\":[{string.Join(",", _accessors)}]," + body + "}";

        public byte[] Bin => _bin.ToArray();

        public byte[] Glb(string body)
        {
            var json = Encoding.UTF8.GetBytes(Json(body));
            int jsonLen = (json.Length + 3) & ~3, binLen = (_bin.Count + 3) & ~3;
            var outp = new byte[12 + 8 + jsonLen + 8 + binLen];
            BinaryPrimitives.WriteUInt32LittleEndian(outp, 0x46546C67);
            BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(8), (uint)outp.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(12), (uint)jsonLen);
            BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(16), 0x4E4F534A);
            for (int i = 0; i < jsonLen; i++) outp[20 + i] = i < json.Length ? json[i] : (byte)' ';
            int b = 20 + jsonLen;
            BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(b), (uint)binLen);
            BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(b + 4), 0x004E4942);
            _bin.CopyTo(outp, b + 8);
            return outp;
        }
    }

    /// <summary>A unit quad in the XY plane, counter-clockwise seen from +Z, with UVs at its corners.</summary>
    private static (int Pos, int Uv, int Idx) Quad(Fixture f) => (
        f.Floats("VEC3", 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0),
        f.Floats("VEC2", 0, 0, 1, 0, 1, 1, 0, 1),
        f.UShorts(0, 1, 2, 0, 2, 3));

    private string Write(string name, byte[] data) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, data); return p; }

    [Fact]
    public void A_glb_comes_in_placed_by_its_node_with_its_texture_and_material()
    {
        var f = new Fixture();
        var (pos, uv, idx) = Quad(f);
        int img = f.Image(Png1x1);
        string body =
            "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
            "\"nodes\":[{\"name\":\"Sign Board\",\"mesh\":0,\"translation\":[10,0,0],\"scale\":[2,2,2]}]," +
            $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos},\"TEXCOORD_0\":{uv}}},\"indices\":{idx},\"material\":0}}]}}]," +
            "\"materials\":[{\"name\":\"Painted Sign\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.3,\"doubleSided\":true," +
            "\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.5,0.5,0.5,1],\"baseColorTexture\":{\"index\":0}}}]," +
            $"\"textures\":[{{\"source\":0}}],\"images\":[{{\"name\":\"sign\",\"bufferView\":{img},\"mimeType\":\"image/png\"}}]";
        var path = Write("sign.glb", f.Glb(body));
        var imgDir = Path.Combine(_dir, "images");

        var loaded = GltfMesh.Load(path, imgDir);

        var s = Assert.Single(loaded.Mesh.SubMeshes);
        Assert.Equal("Sign_Board", s.Object);
        Assert.Equal(2, s.Faces.Count);
        Assert.Equal((0, 1, 2), s.Faces[0]);                                   // winding as authored
        Assert.Equal(new[] { 10f, 12f, 12f, 10f }, s.Positions.Select(p => p.X));   // scale 2, then +10
        Assert.Equal(new[] { 0f, 0f, 2f, 2f }, s.Positions.Select(p => p.Y));
        Assert.Equal((0f, 1f), s.Uvs[0]);                                     // V flipped to OBJ's convention
        Assert.Equal((1f, 0f), s.Uvs[2]);
        Assert.All(s.Normals, n => Assert.True(n.Z > 0.99f));                  // none in the file: derived, facing +Z

        var mat = loaded.Materials![s.Material];
        Assert.Equal("Painted_Sign", mat.Name);
        Assert.Equal(0.7354f, mat.Diffuse.X, 3);                               // linear 0.5 written as sRGB
        Assert.True(mat.Cutout);
        Assert.Equal(0.3f, mat.AlphaCutoff, 3);
        Assert.True(mat.TwoSided);
        Assert.NotNull(mat.TextureFile);
        Assert.StartsWith(Path.GetFullPath(imgDir), Path.GetFullPath(mat.TextureFile!));
        Assert.Equal(Png1x1, File.ReadAllBytes(mat.TextureFile!));
        Assert.EndsWith(".png", mat.TextureFile);
        Assert.False(loaded.ZUpMaxConvention);
    }

    [Fact]
    public void A_mirroring_node_turns_its_triangles_over_and_a_child_inherits_its_parent()
    {
        var f = new Fixture();
        var (pos, _, idx) = Quad(f);
        string body = "\"scenes\":[{\"nodes\":[0]}]," +
                      "\"nodes\":[{\"translation\":[0,5,0],\"children\":[1]},{\"mesh\":0,\"scale\":[-1,1,1]}]," +
                      $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos}}},\"indices\":{idx}}}]}}]";
        var s = Assert.Single(GltfMesh.Load(Write("mirror.glb", f.Glb(body)), Path.Combine(_dir, "i")).Mesh.SubMeshes);
        Assert.Equal((0, 2, 1), s.Faces[0]);                                    // still faces out after the mirror
        Assert.Equal(new[] { 0f, -1f, -1f, 0f }, s.Positions.Select(p => p.X));
        Assert.All(s.Positions, p => Assert.True(p.Y >= 5f));                   // the parent's translation applies
        Assert.Equal("default", s.Material);                                    // no material: white default
    }

    [Fact]
    public void Strips_and_fans_become_triangles_with_their_winding_kept()
    {
        var f = new Fixture();
        int pos = f.Floats("VEC3", 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 0);
        int strip = f.UShorts(0, 2, 1, 3);
        int fan = f.UShorts(0, 2, 3, 1);
        string body = "\"scenes\":[{\"nodes\":[0,1]}],\"nodes\":[{\"mesh\":0,\"name\":\"strip\"},{\"mesh\":1,\"name\":\"fan\"}]," +
                      $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos}}},\"indices\":{strip},\"mode\":5}}]}}," +
                      $"{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos}}},\"indices\":{fan},\"mode\":6}}]}}]";
        var mesh = GltfMesh.Load(Write("strips.glb", f.Glb(body)), Path.Combine(_dir, "i")).Mesh;
        var st = mesh.SubMeshes.Single(m => m.Object == "strip");
        Assert.Equal(new[] { (0, 2, 1), (1, 2, 3) }, st.Faces);                   // the odd triangle swapped back
        var fn = mesh.SubMeshes.Single(m => m.Object == "fan");
        Assert.Equal(new[] { (0, 2, 3), (0, 3, 1) }, fn.Faces);
        Assert.All(st.Normals.Concat(fn.Normals), n => Assert.True(n.Z > 0.99f));    // all wound the same way
    }

    [Fact]
    public void A_gltf_reads_its_bin_and_images_beside_it_and_names_only_those_a_download_needs()
    {
        var f = new Fixture();
        var (pos, uv, idx) = Quad(f);
        string body = "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                      $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos},\"TEXCOORD_0\":{uv}}},\"indices\":{idx},\"material\":0}}]}}]," +
                      "\"materials\":[{\"name\":\"wood\",\"normalTexture\":{\"index\":1},\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," +
                      "\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.5,0],\"scale\":[2,2]}}},\"metallicRoughnessTexture\":{\"index\":2}}}]," +
                      "\"textures\":[{\"source\":0},{\"source\":1},{\"source\":2}]," +
                      "\"images\":[{\"uri\":\"textures/wood%20diff.png\"},{\"uri\":\"textures/wood_nor.png\"},{\"uri\":\"textures/wood_arm.png\"}]";
        string json = f.Json(body, "model.bin");
        Directory.CreateDirectory(Path.Combine(_dir, "textures"));
        File.WriteAllBytes(Path.Combine(_dir, "model.bin"), f.Bin);
        File.WriteAllBytes(Path.Combine(_dir, "textures", "wood diff.png"), Png1x1);
        var path = Path.Combine(_dir, "model.gltf");
        File.WriteAllText(path, json);

        Assert.Equal(new[] { "model.bin", "textures/wood diff.png" }, GltfMesh.RequiredFiles(json));

        var loaded = ModelFile.Load(path);                                       // routed to the glTF reader
        var s = Assert.Single(loaded.Mesh.SubMeshes);
        Assert.Equal(Path.Combine(_dir, "textures", "wood diff.png"), loaded.Materials![s.Material].TextureFile);
        // offset 0.5 and scale 2: u' = 2u + 0.5, v' = 2v, then V flipped
        Assert.Equal((0.5f, 1f), s.Uvs[0]);
        Assert.Equal((2.5f, -1f), s.Uvs[2]);
        Assert.False(BlenderBridge.NeedsBlender(path));
    }

    [Fact]
    public void Compressed_geometry_is_handed_back_for_Blender()
    {
        var f = new Fixture();
        var (pos, _, idx) = Quad(f);
        string body = "\"extensionsUsed\":[\"KHR_draco_mesh_compression\"],\"extensionsRequired\":[\"KHR_draco_mesh_compression\"]," +
                      "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                      $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos}}},\"indices\":{idx}}}]}}]";
        var path = Write("draco.glb", f.Glb(body));
        var ex = Assert.Throws<GltfMesh.UnsupportedException>(() => GltfMesh.Load(path, Path.Combine(_dir, "i")));
        Assert.Contains("Draco", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- against Blender -------------------------------------------------------------------------------------

    private const string BlenderFixture = """
        import bpy, sys
        out = sys.argv[sys.argv.index("--") + 1]
        bpy.ops.wm.read_factory_settings(use_empty=True)
        img = bpy.data.images.new("grid", 32, 32)
        img.generated_type = 'UV_GRID'
        mat = bpy.data.materials.new("Painted Mat")
        mat.use_nodes = True
        bsdf = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
        tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
        tex.image = img
        mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
        bpy.ops.mesh.primitive_uv_sphere_add(segments=12, ring_count=6, radius=1.0, location=(1.5, 0.5, 2.0))
        ball = bpy.context.active_object
        ball.name = "Ball"
        ball.rotation_euler = (0.3, 0.2, 0.9)
        ball.scale = (1.0, 2.0, 0.5)
        ball.data.materials.append(mat)
        bpy.ops.mesh.primitive_cube_add(size=1.0, location=(-2.0, 1.0, 0.5))
        box = bpy.context.active_object
        box.name = "Box"
        box.rotation_euler = (0.0, 0.0, 0.4)
        box.data.materials.append(mat)
        bpy.ops.object.empty_add(location=(0.0, 0.0, 3.0))
        root = bpy.context.active_object
        root.rotation_euler = (0.0, 0.5, 0.0)
        box.parent = root
        bpy.ops.export_scene.gltf(filepath=out, export_format='GLB')
        """;

    /// <summary>Every triangle as its three corners, each (x, y, z, u, v), in winding order.</summary>
    private static List<float[][]> Triangles(ObjMesh m)
    {
        var outp = new List<float[][]>();
        foreach (var s in m.SubMeshes)
            foreach (var (a, b, c) in s.Faces)
            {
                float[] K(int i) => new[] { s.Positions[i].X, s.Positions[i].Y, s.Positions[i].Z, s.Uvs[i].U, s.Uvs[i].V };
                outp.Add(new[] { K(a), K(b), K(c) });
            }
        return outp;
    }

    private static string Show(float[][] t) =>
        string.Join(" | ", t.Select(c => string.Join(",", c.Select(x => x.ToString("0.0000", CultureInfo.InvariantCulture)))));

    /// <summary>The same triangle: the same corners, within the OBJ's printed precision, in the same cyclic order -
    /// so winding counts as well as position and UV.</summary>
    private static bool SameTriangle(float[][] t, float[][] u)
    {
        static bool Same(float[] p, float[] q) { for (int i = 0; i < p.Length; i++) if (MathF.Abs(p[i] - q[i]) > 2e-3f) return false; return true; }
        for (int r = 0; r < 3; r++)
            if (Same(t[0], u[r]) && Same(t[1], u[(r + 1) % 3]) && Same(t[2], u[(r + 2) % 3])) return true;
        return false;
    }

    [Fact]
    public void The_reader_agrees_with_Blender_triangle_for_triangle()
    {
        var exe = BlenderBridge.FindBlender();
        if (exe is null) return;   // no Blender here: nothing to compare against

        string fixture = Path.Combine(_dir, "make.py"), glb = Path.Combine(_dir, "src", "scene.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(glb)!);
        File.WriteAllText(fixture, BlenderFixture.Replace("\r\n", "\n"), new UTF8Encoding(false));
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--background", "--python-exit-code", "3", "--python", fixture, "--", glb }) psi.ArgumentList.Add(a);
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0 && File.Exists(glb), "Blender could not write the fixture:\n" + BlenderBridge.Tail(o + "\n" + e));
        }

        var native = GltfMesh.Load(glb, Path.Combine(_dir, "native_images"));
        var r = BlenderBridge.ConvertToObj(glb, Path.Combine(_dir, "bridge"), exe);
        var viaBlender = ObjMesh.Load(r.ObjPath);

        Assert.Equal(viaBlender.TotalFaces, native.Mesh.TotalFaces);
        var mine = Triangles(native.Mesh);
        var theirs = Triangles(viaBlender);
        var used = new bool[theirs.Count];
        var unmatched = new List<float[][]>();
        foreach (var t in mine)
        {
            int hit = -1;
            for (int j = 0; j < theirs.Count && hit < 0; j++) if (!used[j] && SameTriangle(t, theirs[j])) hit = j;
            if (hit >= 0) used[hit] = true; else unmatched.Add(t);
        }
        Assert.True(unmatched.Count == 0,
            $"{unmatched.Count} of {mine.Count} triangles have no twin in Blender's OBJ, e.g.:\n" +
            string.Join("\n", unmatched.Take(3).Select(Show)) + "\nBlender has, e.g.:\n" +
            string.Join("\n", theirs.Where((_, j) => !used[j]).Take(3).Select(Show)));
        Assert.All(native.Materials!.Values, m => Assert.NotNull(m.TextureFile));
    }
}
