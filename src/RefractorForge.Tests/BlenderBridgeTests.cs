using System.Text;
using RefractorForge.Formats.Mesh;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The Blender bridge: the script it hands Blender, how it finds Blender, and — when Blender is installed on the
/// machine running the tests — a real conversion of a textured model authored by Blender itself. That last test
/// is the one that matters: the script talks to Blender's Python API, which renames things between versions,
/// and only Blender can say whether today's script still works.
/// </summary>
public class BlenderBridgeTests
{
    [Fact]
    public void The_script_covers_every_format_and_exports_the_axes_the_sm_format_uses()
    {
        var s = BlenderBridge.Script;
        // Y up, -Z forward: what .sm expects, so the importer's default orientation is right for anything that
        // came through Blender.
        Assert.Contains("up_axis='Y'", s);
        Assert.Contains("forward_axis='NEGATIVE_Z'", s);
        Assert.Contains("export_triangulated_mesh=True", s);
        Assert.Contains("apply_modifiers=True", s);
        Assert.Contains("path_mode='RELATIVE'", s);           // textures named by the copies beside the OBJ
        foreach (var ext in BlenderBridge.Extensions)
            Assert.True(s.Contains("\"" + ext + "\""), $"the script has no branch for {ext}");
        Assert.Contains("save_render", s);                     // textures survive packing and format
        Assert.Contains("manifest", s);
        Assert.False(BlenderBridge.NeedsBlender("model.obj"));
        Assert.True(BlenderBridge.NeedsBlender("model.FBX"));
        Assert.True(BlenderBridge.NeedsBlender(@"C:\x\scene.blend"));
    }

    [Fact]
    public void RF_BLENDER_overrides_every_search_path()
    {
        string fake = Path.Combine(Path.GetTempPath(), "rf_fake_blender_" + Guid.NewGuid().ToString("N")[..6] + ".exe");
        File.WriteAllBytes(fake, new byte[] { 1 });
        var prev = Environment.GetEnvironmentVariable("RF_BLENDER");
        try
        {
            Environment.SetEnvironmentVariable("RF_BLENDER", fake);
            Assert.Equal(fake, BlenderBridge.FindBlender());
            Environment.SetEnvironmentVariable("RF_BLENDER", @"C:\no\such\blender.exe");
            Assert.NotEqual(@"C:\no\such\blender.exe", BlenderBridge.FindBlender());   // a bad override is ignored
        }
        finally
        {
            Environment.SetEnvironmentVariable("RF_BLENDER", prev);
            File.Delete(fake);
        }
    }

    [Fact]
    public void Tail_keeps_the_traceback_and_drops_the_noise()
    {
        var log = string.Join("\n", Enumerable.Range(0, 40).Select(i => i % 3 == 0 ? "" : "line " + i));
        var tail = BlenderBridge.Tail(log, 5);
        Assert.Equal(5, tail.Split('\n').Length);
        Assert.EndsWith("line 38", tail);
    }

    /// <summary>
    /// Blender authors the fixture too: a cube with a material named with a space, textured by a generated
    /// 64x64 image, saved as .blend and exported as FBX (textures embedded) and GLB. Each goes through the bridge
    /// and comes out as an OBJ the importer can read, with the picture beside it as a PNG. Skipped, not failed,
    /// on a machine without Blender — the bridge is optional and so is this proof.
    /// </summary>
    [Fact]
    public void Blender_converts_a_textured_model_into_obj_mtl_and_png()
    {
        var exe = BlenderBridge.FindBlender();
        if (exe is null) return;   // no Blender here: nothing to prove

        string dir = Path.Combine(Path.GetTempPath(), "rfbridge_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string fixture = Path.Combine(dir, "make_fixture.py");
            File.WriteAllText(fixture, FixtureScript, new UTF8Encoding(false));
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--background", "--python-exit-code", "3", "--python", fixture, "--", dir }) psi.ArgumentList.Add(a);
            using (var p = System.Diagnostics.Process.Start(psi)!)
            {
                string outp = p.StandardOutput.ReadToEnd(); string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, "the fixture could not be made:\n" + BlenderBridge.Tail(outp + "\n" + err));
            }

            foreach (var src in new[] { "fixture.blend", "fixture.fbx", "fixture.glb" })
            {
                string path = Path.Combine(dir, src);
                Assert.True(File.Exists(path), src + " was not written by the fixture script");
                var r = BlenderBridge.ConvertToObj(path, Path.Combine(dir, "out_" + Path.GetExtension(src).TrimStart('.')), exe);
                try { CheckConverted(src, r); }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Blender's own words are the diagnosis; without them a failure here is a guessing game.
                    throw new Xunit.Sdk.XunitException(ex.Message + "\n--- Blender said ---\n" + BlenderBridge.Tail(r.Log, 25));
                }
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void CheckConverted(string src, BlenderBridge.Result r)
    {
        {
            {
                Assert.True(File.Exists(r.ObjPath), src + ": no OBJ");
                Assert.True(File.Exists(r.ManifestPath), src + ": no manifest");

                var mesh = ObjMesh.Load(r.ObjPath);
                Assert.True(mesh.TotalFaces == 12, $"{src}: a cube is 12 triangles, got {mesh.TotalFaces}");
                Assert.True(mesh.SubMeshes.Count == 1, $"{src}: one material, got {mesh.SubMeshes.Count}");
                Assert.DoesNotContain(" ", mesh.SubMeshes[0].Material);        // "Crate Wood" became OBJ-safe
                Assert.True(mesh.MtlLibs.Count == 1, src + ": the OBJ names its .mtl");

                var mtl = ObjMtl.Load(Path.Combine(Path.GetDirectoryName(r.ObjPath)!, mesh.MtlLibs[0]));
                Assert.True(mtl.TryGetValue(mesh.SubMeshes[0].Material, out var mat), src + ": the .mtl has the mesh's material");
                Assert.True(mat!.TextureFile is { Length: > 0 }, src + ": the material names its texture");
                var tex = Path.Combine(Path.GetDirectoryName(r.ObjPath)!, mat.TextureFile!);
                Assert.True(File.Exists(tex), $"{src}: texture {mat.TextureFile} is not beside the OBJ");
                Assert.Equal(".png", Path.GetExtension(tex).ToLowerInvariant());

                // Exported Y-up: the cube's authored 2 m height lands on Y.
                Assert.True(MathF.Abs((mesh.BoundingBox[4] - mesh.BoundingBox[1]) - 2f) < 1e-3f, src + ": 2 m tall on Y");

                var manifest = File.ReadAllText(r.ManifestPath);
                Assert.Contains("\"objects\"", manifest);
                Assert.Contains("\"materials\"", manifest);
            }
        }
    }

    /// <summary>Makes the fixture. Runs inside Blender; `--` then the output folder.</summary>
    private const string FixtureScript = """
import bpy, sys, os
out = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.read_homefile(use_empty=True)
bpy.ops.mesh.primitive_cube_add(size=2)
cube = bpy.context.active_object
cube.name = "Crate Body"
mat = bpy.data.materials.new("Crate Wood")
mat.use_nodes = True
bsdf = mat.node_tree.nodes.get("Principled BSDF")
img = bpy.data.images.new("crate diffuse", 64, 64)
img.generated_type = 'COLOR_GRID'
png = os.path.join(out, "crate_diffuse.png")
img.save_render(png)
img.filepath = png
img.source = 'FILE'
img.reload()
tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
tex.image = img
mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
cube.data.materials.append(mat)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "fixture.blend"))
bpy.ops.export_scene.fbx(filepath=os.path.join(out, "fixture.fbx"), path_mode='COPY', embed_textures=True)
bpy.ops.export_scene.gltf(filepath=os.path.join(out, "fixture.glb"), export_format='GLB')
print("fixture ok")
""";
}
