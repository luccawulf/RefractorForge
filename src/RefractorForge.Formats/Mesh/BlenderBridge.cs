using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// Turns any model Blender can read — FBX, glTF, <c>.blend</c>, STL, PLY, USD, Alembic — into the OBJ + MTL +
/// PNG textures the importer already understands, by running Blender itself in the background. Blender is the
/// converter; nothing here parses those formats, which is the point: an FBX reader is a career, and the one in
/// Blender is maintained by people who do nothing else.
///
/// The script Blender runs (<see cref="Script"/>) does four things the importer relies on: it gives every object
/// and material a name the OBJ grammar can carry (no spaces), it saves every texture a material reaches as a PNG
/// beside the OBJ so nothing depends on the source's packing or format, it exports Y-up / -Z-forward with
/// modifiers applied and faces triangulated, and it writes a manifest of the parts (name, parent, position,
/// size) for the day the importer builds multi-part vehicles.
///
/// Blender is found from <c>RF_BLENDER</c>, the usual install folders (newest version wins) or PATH. Without it,
/// OBJ still works with no dependency at all.
/// </summary>
public static class BlenderBridge
{
    /// <summary>Formats handed to Blender. OBJ is not here because it needs no conversion.</summary>
    public static readonly string[] Extensions =
        { ".fbx", ".glb", ".gltf", ".blend", ".stl", ".ply", ".usd", ".usda", ".usdc", ".usdz", ".abc", ".dae" };

    public static bool NeedsBlender(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The picker filter: everything the importer takes, Blender or not.</summary>
    public const string PickerFilter =
        "3D models|*.obj;*.fbx;*.glb;*.gltf;*.blend;*.stl;*.ply;*.usd;*.usda;*.usdc;*.usdz;*.abc;*.dae|" +
        "Wavefront OBJ|*.obj|Autodesk FBX|*.fbx|glTF|*.glb;*.gltf|Blender|*.blend|All files|*.*";

    /// <summary>Where blender.exe is, or null. <c>RF_BLENDER</c> overrides everything, for a portable install.</summary>
    public static string? FindBlender()
    {
        var env = Environment.GetEnvironmentVariable("RF_BLENDER");
        if (env is { Length: > 0 } && File.Exists(env)) return env;

        var candidates = new List<(Version Ver, string Exe)>();
        void Scan(string? root)
        {
            if (root is null || !Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root, "Blender*"))
            {
                var exe = Path.Combine(dir, "blender.exe");
                if (!File.Exists(exe)) continue;
                // "Blender 5.1" -> 5.1; a folder with no number (Steam's plain "Blender") sorts first, and is only
                // chosen when nothing versioned exists.
                var digits = new string(Path.GetFileName(dir).Where(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
                candidates.Add((Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out var v) ? v : new Version(0, 0), exe));
            }
        }
        Scan(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation"));
        Scan(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Blender Foundation"));
        Scan(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common"));
        if (candidates.Count > 0) return candidates.OrderByDescending(c => c.Ver).First().Exe;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var exe = Path.Combine(dir.Trim(), "blender.exe"); if (dir.Length > 0 && File.Exists(exe)) return exe; }
            catch { }
        }
        return null;
    }

    /// <summary>What a conversion produced. <paramref name="Manifest"/> is the parts list the script wrote, as
    /// JSON text — read it when the importer learns to build vehicles.</summary>
    public sealed record Result(string ObjPath, string ManifestPath, string Log);

    /// <summary>
    /// Convert <paramref name="source"/> into <c>model.obj</c> (+ .mtl, PNG textures, manifest.json) inside
    /// <paramref name="workDir"/>, which is created. Throws with the tail of Blender's own output when it fails —
    /// that text is the diagnosis, so it goes to the user rather than a log nobody reads.
    /// </summary>
    public static Result ConvertToObj(string source, string workDir, string? blenderExe = null, int timeoutSeconds = 180)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("No such model.", source);
        blenderExe ??= FindBlender() ?? throw new InvalidOperationException(
            "Blender was not found. Install it from blender.org, or point RF_BLENDER at blender.exe. OBJ files need no Blender.");
        Directory.CreateDirectory(workDir);
        string script = Path.Combine(workDir, "rf_convert.py");
        string obj = Path.Combine(workDir, "model.obj");
        string manifest = Path.Combine(workDir, "manifest.json");
        File.WriteAllText(script, Script, new UTF8Encoding(false));

        var psi = new ProcessStartInfo(blenderExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        // Blender exits 0 even when a --python script throws, unless told otherwise; 3 is ours to recognise.
        foreach (var a in new[] { "--background", "--python-exit-code", "3", "--python", script, "--",
                                  Path.GetFullPath(source), obj, manifest })
            psi.ArgumentList.Add(a);

        var log = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"Blender did not finish within {timeoutSeconds} s.\n" + Tail(log.ToString()));
        }
        p.WaitForExit();   // flushes the async readers
        string text = log.ToString();
        if (p.ExitCode != 0 || !File.Exists(obj))
            throw new InvalidOperationException($"Blender could not convert {Path.GetFileName(source)} (exit code {p.ExitCode}).\n" + Tail(text));
        return new Result(obj, manifest, text);
    }

    /// <summary>The last few meaningful lines of Blender's output — the traceback, when there is one.</summary>
    public static string Tail(string log, int lines = 14)
    {
        var all = log.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        int from = Math.Max(0, all.Count - lines);
        return string.Join("\n", all.Skip(from));
    }

    /// <summary>The Python Blender runs. Kept as text so a test can read it without Blender, and so the whole
    /// conversion is visible in one place.</summary>
    public const string Script = """
import bpy, sys, os, json, re

argv = sys.argv[sys.argv.index("--") + 1:]
src, out_obj, manifest_path = argv[0], argv[1], argv[2]
ext = os.path.splitext(src)[1].lower()

def log(msg):
    print("[rfbridge] " + str(msg))
    sys.stdout.flush()

log("Blender " + bpy.app.version_string + " converting " + src)

# A clean scene, then the import. A .blend IS the scene.
if ext == ".blend":
    bpy.ops.wm.open_mainfile(filepath=src)
else:
    bpy.ops.wm.read_homefile(use_empty=True)
    if ext == ".obj":
        bpy.ops.wm.obj_import(filepath=src)
    elif ext == ".fbx":
        try:
            bpy.ops.wm.fbx_import(filepath=src)          # the native importer (Blender 5)
        except Exception as e:
            log("native FBX import unavailable (" + str(e) + "), using the add-on")
            bpy.ops.import_scene.fbx(filepath=src)
    elif ext in (".gltf", ".glb"):
        bpy.ops.import_scene.gltf(filepath=src)
    elif ext == ".stl":
        bpy.ops.wm.stl_import(filepath=src)
    elif ext == ".ply":
        bpy.ops.wm.ply_import(filepath=src)
    elif ext in (".usd", ".usda", ".usdc", ".usdz"):
        bpy.ops.wm.usd_import(filepath=src)
    elif ext == ".abc":
        bpy.ops.wm.alembic_import(filepath=src)
    elif ext == ".dae":
        bpy.ops.wm.collada_import(filepath=src)        # gone from Blender 5; kept for older versions
    else:
        raise SystemExit("unsupported format: " + ext)

meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
if not meshes:
    raise SystemExit("the file contains no mesh objects")
log(str(len(meshes)) + " mesh object(s)")

# Names the OBJ grammar can carry: no spaces, nothing exotic.
def safe(name):
    s = re.sub("[^A-Za-z0-9_]", "_", name).strip("_")
    return s or "part"

seen = set()
for o in meshes:
    n = safe(o.name)
    while n in seen:
        n = n + "_"
    seen.add(n)
    o.name = n
mseen = set()
for m in bpy.data.materials:
    n = safe(m.name)
    while n in mseen:
        n = n + "_"
    mseen.add(n)
    m.name = n

# A downloaded FBX names its textures by paths from its author's machine ("D:/Jose Bronze/Documents/.../car.jpg")
# while the files sit beside it, usually in a subfolder. Blender keeps the dead path and the image has no data,
# so before anything is saved, a missing image is looked for by name near the source: the source's folder, its
# subfolders (three deep), and the same stem in any format Blender reads.
IMAGE_EXTS = (".png", ".jpg", ".jpeg", ".tga", ".dds", ".bmp", ".tif", ".tiff", ".psd", ".exr", ".hdr")
src_dir = os.path.dirname(os.path.abspath(src))
_index = None

def file_index():
    global _index
    if _index is None:
        _index = {}
        for root, dirs, files in os.walk(src_dir):
            if root[len(src_dir):].count(os.sep) >= 3:
                dirs[:] = []
            for f in files:
                stem, ext = os.path.splitext(f)
                if ext.lower() in IMAGE_EXTS:
                    _index.setdefault(f.lower(), os.path.join(root, f))
                    _index.setdefault(stem.lower(), os.path.join(root, f))
    return _index

def image_on_disk(img):
    p = bpy.path.abspath(img.filepath) if img.filepath else ""
    return p if p and os.path.isfile(p) else None

def find_image(img):
    ref = img.filepath.replace(chr(92), "/") if img.filepath else img.name
    base = os.path.basename(ref) or img.name
    stem = os.path.splitext(base)[0]
    idx = file_index()
    for key in (base.lower(), stem.lower(), img.name.lower(), os.path.splitext(img.name)[0].lower()):
        if key in idx:
            return idx[key]
    return None

missing = {}
for m in bpy.data.materials:
    if not getattr(m, "use_nodes", True) or m.node_tree is None:
        continue
    for node in m.node_tree.nodes:
        if node.type != 'TEX_IMAGE' or node.image is None:
            continue
        img = node.image
        if img.packed_file is not None or img.has_data or image_on_disk(img):
            continue
        found = find_image(img)
        if found:
            log("texture '" + img.name + "' was at '" + str(img.filepath) + "'; using " + found)
            img.filepath = found
            img.source = 'FILE'
            try:
                img.reload()
            except Exception as e:
                log("could not reload " + found + ": " + str(e))
        else:
            missing[img.name] = img.filepath or ""
            log("texture '" + img.name + "' NOT FOUND (referenced as '" + str(img.filepath) + "')")

# Every image a material reaches, saved as a PNG beside the OBJ. save_render works whether the image is packed
# (what the FBX and glTF importers make of embedded textures), generated or on disk. The material -> PNG map is
# what the .mtl will say below; the exporter's own idea of a packed image's path is a bare file name that
# resolves against the drive root, so it is not trusted.
texdir = os.path.dirname(out_obj)
rs = bpy.context.scene.render.image_settings
rs.file_format = 'PNG'
rs.color_mode = 'RGBA'
images = {}
mat_tex = {}

def base_color_image(mat):
    if not getattr(mat, "use_nodes", True) or mat.node_tree is None:
        return None
    first = None
    for node in mat.node_tree.nodes:
        if node.type != 'TEX_IMAGE' or node.image is None:
            continue
        first = first or node.image
        for out in node.outputs:
            for link in out.links:
                if link.to_socket.name == 'Base Color':
                    return node.image
    return first

for m in bpy.data.materials:
    img = base_color_image(m)
    if img is None:
        continue
    if img.name not in images:
        name = safe(os.path.splitext(img.name)[0]) + ".png"
        path = os.path.join(texdir, name)
        try:
            img.save_render(path)
            try:
                if img.packed_file is not None:
                    img.unpack(method='REMOVE')
            except Exception as e:
                log("could not unpack " + img.name + ": " + str(e))
            img.filepath_raw = path
            img.source = 'FILE'
            images[img.name] = name
        except Exception as e:
            log("could not save image " + img.name + ": " + str(e))
            continue
    mat_tex[m.name] = images[img.name]
log(str(len(images)) + " texture(s) saved")

# Y up, -Z forward: the axes the .sm format uses, so the importer's default "Y up" is right for anything that
# came through here. Modifiers applied, faces triangulated, materials and UVs written.
try:
    bpy.ops.wm.obj_export(filepath=out_obj, export_selected_objects=False, apply_modifiers=True,
                          export_triangulated_mesh=True, export_normals=True, export_uv=True,
                          export_materials=True, forward_axis='NEGATIVE_Z', up_axis='Y',
                          path_mode='RELATIVE', global_scale=1.0, export_object_groups=True,
                          export_material_groups=False, export_smooth_groups=False)
except TypeError as e:
    log("obj_export refused an option (" + str(e) + "), exporting with defaults")
    bpy.ops.wm.obj_export(filepath=out_obj, forward_axis='NEGATIVE_Z', up_axis='Y', path_mode='RELATIVE')

# The .mtl's texture lines, written from the map above: one map_Kd per material, naming the PNG beside the OBJ.
mtl_path = os.path.splitext(out_obj)[0] + ".mtl"
if os.path.exists(mtl_path):
    with open(mtl_path, "r", encoding="utf-8", errors="replace") as f:
        src_lines = f.read().splitlines()
    fixed = []
    cur = None
    for line in src_lines:
        s = line.strip()
        if s.startswith("newmtl "):
            cur = s[7:].strip()
            fixed.append(line)
            if cur in mat_tex:
                fixed.append("map_Kd " + mat_tex[cur])
            continue
        # The exporter's own texture lines go: ours replaced them, or they name a file that was never found
        # (a dead absolute path from another machine helps nobody downstream).
        if s.startswith("map_") or s.startswith("bump ") or s.startswith("refl "):
            continue
        fixed.append(line)
    with open(mtl_path, "w", encoding="utf-8") as f:
        f.write("\n".join(fixed) + "\n")
    log(str(len(mat_tex)) + " material(s) bound to a texture")

# The parts, for the day the importer builds multi-part objects; and the textures that could not be found, by
# the path the file gave, so the editor can say exactly what to put where.
man = {"blender": bpy.app.version_string, "source": src, "materials": sorted(mseen), "textures": images,
       "missing_textures": missing, "objects": []}
for o in meshes:
    man["objects"].append({
        "name": o.name,
        "parent": o.parent.name if o.parent is not None else None,
        "location": [float(v) for v in o.matrix_world.translation],
        "dimensions": [float(v) for v in o.dimensions],
        "materials": [s.material.name for s in o.material_slots if s.material is not None],
    })
with open(manifest_path, "w") as f:
    json.dump(man, f, indent=1)
log("done: " + out_obj)
""";
}
