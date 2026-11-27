using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// A clean-room glTF 2.0 reader for static models: a <c>.gltf</c> with its <c>.bin</c> and images, or a binary
/// <c>.glb</c>. glTF is what the free model libraries serve - Poly Haven, Poly Pizza, Sketchfab - so reading it here
/// means a downloaded model needs no Blender to come in.
///
/// It produces exactly what the Blender bridge's OBJ would: Blender's glTF importer maps a file's (x, y, z) to its own
/// (x, -z, y) and the bridge's OBJ export (Y up, -Z forward) maps that straight back, so the OBJ carries the glTF's own
/// coordinates, its counter-clockwise winding and its normals, with only the texture V flipped (glTF puts V = 0 at the
/// top of the image, OBJ at the bottom). The geometry is the scene's: every node's mesh, placed by the node's world
/// transform (a mirroring transform turns its triangles over so they still face out). Each primitive's base colour
/// - texture, factor, cut-out and two-sidedness - becomes its material; the PBR maps the engine has no use for
/// (normal, metal, roughness, occlusion) are ignored.
///
/// What it cannot decode it says so with <see cref="UnsupportedException"/> - geometry compressed with Draco or
/// meshopt - so the caller can hand that file to Blender instead.
/// </summary>
public static class GltfMesh
{
    public static bool Is(string path)
    {
        var e = Path.GetExtension(path);
        return e.Equals(".gltf", StringComparison.OrdinalIgnoreCase) || e.Equals(".glb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A file this reader cannot decode itself (compressed geometry). Blender can.</summary>
    public sealed class UnsupportedException : NotSupportedException
    {
        public UnsupportedException(string message) : base(message) { }
    }

    private const uint GlbMagic = 0x46546C67;      // "glTF"
    private const uint ChunkJson = 0x4E4F534A;     // "JSON"
    private const uint ChunkBin = 0x004E4942;      // "BIN\0"

    private static readonly string[] Unsupported = { "KHR_draco_mesh_compression", "EXT_meshopt_compression", "KHR_meshopt_compression" };

    /// <summary>Read a model. Images packed inside the file (a .glb's buffer, a <c>data:</c> URI) are written to
    /// <paramref name="imageDir"/> - by default a folder under the temp directory keyed to this file - so the importer
    /// finds every texture by path, exactly as it finds an OBJ's.</summary>
    public static ModelFile.Loaded Load(string path, string? imageDir = null)
    {
        var bytes = File.ReadAllBytes(path);
        var (json, bin) = Split(bytes);
        using var doc = JsonDocument.Parse(json);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var reader = new Reader(doc.RootElement, dir, bin, imageDir ?? DefaultImageDir(path, bytes.Length));
        return reader.Read();
    }

    /// <summary>The JSON and the binary chunk of a file: a .glb is split, a .gltf is all JSON.</summary>
    private static (ReadOnlyMemory<byte> Json, byte[]? Bin) Split(byte[] file)
    {
        if (file.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(file) == GlbMagic)
        {
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
            if (version != 2) throw new InvalidDataException($"This is a version {version} .glb; only glTF 2.0 is read.");
            int off = 12;
            ReadOnlyMemory<byte>? json = null; byte[]? bin = null;
            while (off + 8 <= file.Length)
            {
                long len = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off + 4));
                if (off + 8 + len > file.Length) throw new InvalidDataException("A .glb chunk runs past the end of the file.");
                if (type == ChunkJson && json is null) json = new ReadOnlyMemory<byte>(file, off + 8, (int)len);
                else if (type == ChunkBin && bin is null) bin = file.AsSpan(off + 8, (int)len).ToArray();
                off += 8 + (int)len;
            }
            if (json is null) throw new InvalidDataException("The .glb has no JSON chunk.");
            return (json.Value, bin);
        }
        int start = file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF ? 3 : 0;   // UTF-8 BOM
        return (new ReadOnlyMemory<byte>(file, start, file.Length - start), null);
    }

    private static string DefaultImageDir(string path, long length)
    {
        var full = Path.GetFullPath(path);
        var key = full.ToLowerInvariant() + "|" + length + "|" + File.GetLastWriteTimeUtc(full).Ticks;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(Path.GetTempPath(), "RefractorForge", "gltf_images", hash);
    }

    /// <summary>The files a <c>.gltf</c> needs for this importer, as the relative URIs it names them by: its external
    /// buffers and the images its materials take their base colour from. A downloader fetches exactly these and
    /// leaves the normal, roughness and metal maps on the server.</summary>
    public static IReadOnlyList<string> RequiredFiles(string gltfJson)
    {
        using var doc = JsonDocument.Parse(gltfJson);
        var root = doc.RootElement;
        var outp = new List<string>();
        void Add(string? uri)
        {
            if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            var u = Uri.UnescapeDataString(uri);
            if (!outp.Contains(u, StringComparer.OrdinalIgnoreCase)) outp.Add(u);
        }
        if (root.TryGetProperty("buffers", out var bufs))
            foreach (var b in bufs.EnumerateArray()) Add(Str(b, "uri"));
        if (root.TryGetProperty("materials", out var mats) && root.TryGetProperty("textures", out var texs) && root.TryGetProperty("images", out var imgs))
            foreach (var m in mats.EnumerateArray())
                if (BaseColorTexture(m) is { } bct && Int(bct, "index") is int ti && ti >= 0 && ti < texs.GetArrayLength()
                    && ImageOfTexture(texs[ti]) is int ii && ii >= 0 && ii < imgs.GetArrayLength())
                    Add(Str(imgs[ii], "uri"));
        return outp;
    }

    // ---- JSON helpers --------------------------------------------------------------------------------------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static float? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : null;

    private static float[]? Floats(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var a = new float[v.GetArrayLength()];
        int i = 0;
        foreach (var x in v.EnumerateArray()) a[i++] = x.ValueKind == JsonValueKind.Number ? (float)x.GetDouble() : 0f;
        return a;
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>The material's base colour texture: the PBR one, or the older specular-glossiness extension's
    /// diffuse texture (Sketchfab served those for years).</summary>
    private static JsonElement? BaseColorTexture(JsonElement material)
    {
        if (Obj(material, "pbrMetallicRoughness") is { } pbr && Obj(pbr, "baseColorTexture") is { } t) return t;
        if (Obj(material, "extensions") is { } ext && Obj(ext, "KHR_materials_pbrSpecularGlossiness") is { } sg
            && Obj(sg, "diffuseTexture") is { } dt) return dt;
        return null;
    }

    /// <summary>The image a texture draws: its own source, or the source of the first image-format extension that
    /// names one (EXT_texture_webp, KHR_texture_basisu - the plain source is then the fallback).</summary>
    private static int? ImageOfTexture(JsonElement texture)
    {
        if (Int(texture, "source") is int s) return s;
        if (Obj(texture, "extensions") is { } ext)
            foreach (var p in ext.EnumerateObject())
                if (Int(p.Value, "source") is int es) return es;
        return null;
    }

    // ---- the reader ----------------------------------------------------------------------------------------

    private sealed class Reader
    {
        private readonly JsonElement _root;
        private readonly string _dir;
        private readonly byte[]? _glbBin;
        private readonly string _imageDir;
        private readonly Dictionary<int, byte[]> _buffers = new();
        private readonly Dictionary<int, string?> _imageFiles = new();
        private readonly List<(string Name, ObjMaterial Mat, int TexCoord, float[]? UvTransform)> _materials = new();
        private readonly Dictionary<string, ObjSubMesh> _subs = new(StringComparer.Ordinal);
        private readonly List<string> _order = new();
        private int _defaultMaterial = -1;

        public Reader(JsonElement root, string dir, byte[]? glbBin, string imageDir)
        {
            _root = root; _dir = dir; _glbBin = glbBin; _imageDir = imageDir;
        }

        private JsonElement Array(string name) =>
            _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : default;

        private int Count(string name) => Array(name).ValueKind == JsonValueKind.Array ? Array(name).GetArrayLength() : 0;

        public ModelFile.Loaded Read()
        {
            if (_root.TryGetProperty("asset", out var asset) && Str(asset, "version") is { } ver
                && !ver.StartsWith("2", StringComparison.Ordinal))
                throw new InvalidDataException($"This is a glTF {ver} file; only glTF 2.0 is read.");
            if (_root.TryGetProperty("extensionsRequired", out var req) && req.ValueKind == JsonValueKind.Array)
                foreach (var e in req.EnumerateArray())
                    if (e.GetString() is { } name && Unsupported.Contains(name, StringComparer.Ordinal))
                        throw new UnsupportedException($"The model's geometry is compressed ({name}), which this reader does not decode.");

            ReadMaterials();

            // The scene's root nodes; a file with no scene gives every node no other node names as a child.
            var roots = new List<int>();
            int nodeCount = Count("nodes");
            int scene = Int(_root, "scene") ?? 0;
            if (Count("scenes") > 0 && scene < Count("scenes") && Array("scenes")[scene].TryGetProperty("nodes", out var sn))
                foreach (var n in sn.EnumerateArray()) roots.Add(n.GetInt32());
            else
            {
                var child = new bool[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                    if (Array("nodes")[i].TryGetProperty("children", out var ch))
                        foreach (var c in ch.EnumerateArray()) if (c.GetInt32() is int ci && ci >= 0 && ci < nodeCount) child[ci] = true;
                for (int i = 0; i < nodeCount; i++) if (!child[i]) roots.Add(i);
                if (nodeCount == 0)
                    for (int m = 0; m < Count("meshes"); m++) EmitMesh(m, Matrix4x4.Identity, "mesh" + m);   // bare meshes
            }
            var visiting = new HashSet<int>();
            foreach (var r in roots) Walk(r, Matrix4x4.Identity, visiting);

            var mesh = new ObjMesh();
            foreach (var key in _order)
            {
                var s = _subs[key];
                if (s.Faces.Count > 0) mesh.SubMeshes.Add(s);
            }
            mesh.RecomputeBounds();
            var mats = new Dictionary<string, ObjMaterial>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _materials) mats[m.Name] = m.Mat;
            return new ModelFile.Loaded(mesh, mats, ZUpMaxConvention: false);
        }

        private void Walk(int node, Matrix4x4 parent, HashSet<int> visiting)
        {
            if (node < 0 || node >= Count("nodes") || !visiting.Add(node)) return;   // a cycle is a broken file
            var n = Array("nodes")[node];
            var world = LocalMatrix(n) * parent;    // row vectors: local first, then the parent's
            if (Int(n, "mesh") is int mi) EmitMesh(mi, world, Str(n, "name") ?? MeshName(mi) ?? "node" + node);
            if (n.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray()) Walk(c.GetInt32(), world, visiting);
            visiting.Remove(node);
        }

        private string? MeshName(int mesh) => mesh >= 0 && mesh < Count("meshes") ? Str(Array("meshes")[mesh], "name") : null;

        /// <summary>A node's transform as a row-vector matrix (System.Numerics): glTF's column-major array read in
        /// order is exactly that, and TRS composes scale, then rotation, then translation.</summary>
        private static Matrix4x4 LocalMatrix(JsonElement n)
        {
            if (Floats(n, "matrix") is { Length: 16 } m)
                return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                                     m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            var s = Floats(n, "scale") is { Length: 3 } sc ? new Vector3(sc[0], sc[1], sc[2]) : Vector3.One;
            var r = Floats(n, "rotation") is { Length: 4 } q ? Quaternion.Normalize(new Quaternion(q[0], q[1], q[2], q[3])) : Quaternion.Identity;
            var t = Floats(n, "translation") is { Length: 3 } tr ? new Vector3(tr[0], tr[1], tr[2]) : Vector3.Zero;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }

        // ---- materials -------------------------------------------------------------------------------------

        private void ReadMaterials()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Count("materials"); i++)
            {
                var m = Array("materials")[i];
                string name = Unique(Safe(Str(m, "name")) ?? "material" + i, used);
                var mat = new ObjMaterial { Name = name, Diffuse = new Vec3(1f, 1f, 1f) };
                float[]? factor = null;
                if (Obj(m, "pbrMetallicRoughness") is { } pbr) factor = Floats(pbr, "baseColorFactor");
                if (factor is null && Obj(m, "extensions") is { } ext && Obj(ext, "KHR_materials_pbrSpecularGlossiness") is { } sg)
                    factor = Floats(sg, "diffuseFactor");
                if (factor is { Length: >= 3 })
                    mat.Diffuse = new Vec3(LinearToSrgb(factor[0]), LinearToSrgb(factor[1]), LinearToSrgb(factor[2]));

                int texCoord = 0; float[]? uvTransform = null;
                if (BaseColorTexture(m) is { } bct)
                {
                    texCoord = Int(bct, "texCoord") ?? 0;
                    if (Obj(bct, "extensions") is { } bext && Obj(bext, "KHR_texture_transform") is { } tt)
                    {
                        var off = Floats(tt, "offset") ?? new[] { 0f, 0f };
                        var scl = Floats(tt, "scale") ?? new[] { 1f, 1f };
                        float rot = Num(tt, "rotation") ?? 0f;
                        uvTransform = new[] { off.Length > 0 ? off[0] : 0f, off.Length > 1 ? off[1] : 0f, rot,
                                              scl.Length > 0 ? scl[0] : 1f, scl.Length > 1 ? scl[1] : 1f };
                        if (Int(tt, "texCoord") is int tc) texCoord = tc;
                    }
                    if (Int(bct, "index") is int ti) mat.TextureFile = ImageFileOfTexture(ti);
                }
                switch (Str(m, "alphaMode"))
                {
                    case "MASK": mat.Cutout = true; mat.AlphaCutoff = Num(m, "alphaCutoff") ?? 0.5f; break;
                    case "BLEND": mat.Blend = true; break;
                }
                mat.TwoSided = m.TryGetProperty("doubleSided", out var ds) && ds.ValueKind == JsonValueKind.True;
                _materials.Add((name, mat, texCoord, uvTransform));
            }
        }

        private int DefaultMaterial()
        {
            if (_defaultMaterial >= 0) return _defaultMaterial;
            var used = new HashSet<string>(_materials.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            _materials.Add((Unique("default", used), new ObjMaterial { Name = "default", Diffuse = new Vec3(1f, 1f, 1f) }, 0, null));
            _materials[^1].Mat.Name = _materials[^1].Name;
            return _defaultMaterial = _materials.Count - 1;
        }

        /// <summary>glTF colour factors are linear; the engine (and the eye looking at a texture) work in sRGB.</summary>
        private static float LinearToSrgb(float c)
        {
            c = Math.Clamp(c, 0f, 1f);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private static string? Safe(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var sb = new StringBuilder();
            foreach (var ch in s.Trim()) sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string Unique(string name, HashSet<string> used)
        {
            string s = name; int n = 1;
            while (!used.Add(s)) s = name + "_" + (++n);
            return s;
        }

        private string? ImageFileOfTexture(int texture)
        {
            if (texture < 0 || texture >= Count("textures")) return null;
            return ImageOfTexture(Array("textures")[texture]) is int img ? ImageFile(img) : null;
        }

        /// <summary>A path to the image's file: the file itself when the glTF names one beside it, otherwise the
        /// bytes (from a buffer view or a data: URI) written out under the image folder.</summary>
        private string? ImageFile(int image)
        {
            if (_imageFiles.TryGetValue(image, out var known)) return known;
            string? path = null;
            if (image >= 0 && image < Count("images"))
            {
                var im = Array("images")[image];
                var uri = Str(im, "uri");
                string? mime = Str(im, "mimeType");
                byte[]? data = null;
                if (uri is not null && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    data = DataUri(uri, out var dataMime);
                    mime ??= dataMime;
                }
                else if (uri is not null)
                {
                    var p = Path.Combine(_dir, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar));
                    path = File.Exists(p) ? Path.GetFullPath(p) : null;
                }
                else if (Int(im, "bufferView") is int bv)
                    data = BufferViewBytes(bv);

                if (data is not null)
                {
                    string ext = mime switch
                    {
                        "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/ktx2" => ".ktx2",
                        _ => Sniff(data),
                    };
                    var name = (Safe(Str(im, "name")) ?? "image" + image) + ext;
                    Directory.CreateDirectory(_imageDir);
                    path = Path.Combine(_imageDir, image + "_" + name);
                    if (!File.Exists(path) || new FileInfo(path).Length != data.Length) File.WriteAllBytes(path, data);
                }
            }
            _imageFiles[image] = path;
            return path;
        }

        private static string Sniff(byte[] d) =>
            d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 ? ".png"
            : d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 ? ".jpg"
            : d.Length > 12 && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P' ? ".webp"
            : ".bin";

        // ---- buffers and accessors -------------------------------------------------------------------------

        private static byte[] DataUri(string uri, out string? mime)
        {
            int comma = uri.IndexOf(',');
            mime = null;
            if (comma < 0) throw new InvalidDataException("A data: URI with no data.");
            var header = uri[5..comma];   // after "data:"
            int semi = header.IndexOf(';');
            mime = semi >= 0 ? header[..semi] : header;
            var payload = uri[(comma + 1)..];
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }

        private byte[] Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out var b)) return b;
            if (index < 0 || index >= Count("buffers")) throw new InvalidDataException($"Buffer {index} does not exist.");
            var buf = Array("buffers")[index];
            var uri = Str(buf, "uri");
            if (uri is null)
                b = index == 0 && _glbBin is not null ? _glbBin : throw new InvalidDataException("A buffer with no data (a .gltf missing its .bin?).");
            else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                b = DataUri(uri, out _);
            else
            {
                var p = Path.Combine(_dir, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) throw new FileNotFoundException($"The model's buffer {Path.GetFileName(p)} is missing - it must sit beside the .gltf.", p);
                b = File.ReadAllBytes(p);
            }
            _buffers[index] = b;
            return b;
        }

        private (byte[] Data, int Offset, int Length, int Stride) View(int index)
        {
            if (index < 0 || index >= Count("bufferViews")) throw new InvalidDataException($"Buffer view {index} does not exist.");
            var v = Array("bufferViews")[index];
            if (Obj(v, "extensions") is { } ext)
                foreach (var p in ext.EnumerateObject())
                    if (Unsupported.Contains(p.Name, StringComparer.Ordinal))
                        throw new UnsupportedException($"The model's geometry is compressed ({p.Name}), which this reader does not decode.");
            var data = Buffer(Int(v, "buffer") ?? 0);
            int off = Int(v, "byteOffset") ?? 0, len = Int(v, "byteLength") ?? 0, stride = Int(v, "byteStride") ?? 0;
            if (off < 0 || len < 0 || (long)off + len > data.Length) throw new InvalidDataException($"Buffer view {index} runs past its buffer.");
            return (data, off, len, stride);
        }

        private byte[] BufferViewBytes(int index)
        {
            var (d, off, len, _) = View(index);
            return d.AsSpan(off, len).ToArray();
        }

        private static int Components(string? type) => type switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16,
            _ => throw new InvalidDataException($"Unknown accessor type {type}."),
        };

        private static int ComponentSize(int componentType) => componentType switch
        {
            5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4,
            _ => throw new InvalidDataException($"Unknown component type {componentType}."),
        };

        private static float ReadComponent(ReadOnlySpan<byte> s, int componentType, bool normalized) => componentType switch
        {
            5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
            5121 => normalized ? s[0] / 255f : s[0],
            5120 => normalized ? Math.Max((sbyte)s[0] / 127f, -1f) : (sbyte)s[0],
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s),
            5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(s),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
            _ => 0f,
        };

        /// <summary>An accessor's values, <c>count x components</c> floats, normalised integers scaled as the spec
        /// says, with any sparse substitution applied.</summary>
        private float[] ReadFloats(int accessor, out int components)
        {
            if (accessor < 0 || accessor >= Count("accessors")) throw new InvalidDataException($"Accessor {accessor} does not exist.");
            var a = Array("accessors")[accessor];
            int count = Int(a, "count") ?? 0, ct = Int(a, "componentType") ?? 5126;
            bool norm = a.TryGetProperty("normalized", out var nz) && nz.ValueKind == JsonValueKind.True;
            components = Components(Str(a, "type"));
            int cs = ComponentSize(ct), elem = cs * components;
            var outp = new float[(long)count * components];
            if (Int(a, "bufferView") is int bvi)
            {
                var (d, off, len, stride) = View(bvi);
                int baseOff = off + (Int(a, "byteOffset") ?? 0);
                int step = stride > 0 ? stride : elem;
                if (count > 0 && (long)baseOff + (long)(count - 1) * step + elem > (long)off + len)
                    throw new InvalidDataException($"Accessor {accessor} runs past its buffer view.");
                for (int i = 0; i < count; i++)
                {
                    int e = baseOff + i * step;
                    for (int c = 0; c < components; c++)
                        outp[i * components + c] = ReadComponent(d.AsSpan(e + c * cs, cs), ct, norm);
                }
            }
            if (Obj(a, "sparse") is { } sp && Int(sp, "count") is int sc && sc > 0
                && Obj(sp, "indices") is { } si && Obj(sp, "values") is { } sv)
            {
                var (id, ioff, _, _) = View(Int(si, "bufferView") ?? 0);
                int ict = Int(si, "componentType") ?? 5125, ics = ComponentSize(ict);
                int ibase = ioff + (Int(si, "byteOffset") ?? 0);
                var (vd, voff, _, _) = View(Int(sv, "bufferView") ?? 0);
                int vbase = voff + (Int(sv, "byteOffset") ?? 0);
                for (int k = 0; k < sc; k++)
                {
                    int target = (int)ReadComponent(id.AsSpan(ibase + k * ics, ics), ict, false);
                    if (target < 0 || target >= count) continue;
                    for (int c = 0; c < components; c++)
                        outp[target * components + c] = ReadComponent(vd.AsSpan(vbase + (k * components + c) * cs, cs), ct, norm);
                }
            }
            return outp;
        }

        /// <summary>An index accessor, read as integers (a float would lose indices past 2^24).</summary>
        private int[] ReadIndices(int accessor)
        {
            if (accessor < 0 || accessor >= Count("accessors")) throw new InvalidDataException($"Accessor {accessor} does not exist.");
            var a = Array("accessors")[accessor];
            int count = Int(a, "count") ?? 0, ct = Int(a, "componentType") ?? 5125;
            if (ct is not (5121 or 5123 or 5125) || Obj(a, "sparse") is not null || Int(a, "bufferView") is not int bvi)
            {
                var f = ReadFloats(accessor, out _);   // unusual but legal: read it the general way
                var r = new int[f.Length];
                for (int i = 0; i < f.Length; i++) r[i] = (int)f[i];
                return r;
            }
            var (d, off, len, stride) = View(bvi);
            int cs = ComponentSize(ct), step = stride > 0 ? stride : cs;
            int baseOff = off + (Int(a, "byteOffset") ?? 0);
            if (count > 0 && (long)baseOff + (long)(count - 1) * step + cs > (long)off + len)
                throw new InvalidDataException($"Accessor {accessor} runs past its buffer view.");
            var idx = new int[count];
            for (int i = 0; i < count; i++)
            {
                var s = d.AsSpan(baseOff + i * step, cs);
                idx[i] = ct switch
                {
                    5121 => s[0],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                    _ => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(s), int.MaxValue),
                };
            }
            return idx;
        }

        /// <summary>A vertex attribute with exactly <paramref name="components"/> components, or null.</summary>
        private float[]? Attribute(JsonElement attrs, string name, int components)
        {
            if (Int(attrs, name) is not int acc) return null;
            var f = ReadFloats(acc, out int c);
            return c == components ? f : null;
        }

        // ---- geometry --------------------------------------------------------------------------------------

        private void EmitMesh(int meshIndex, Matrix4x4 world, string objectName)
        {
            if (meshIndex < 0 || meshIndex >= Count("meshes")) return;
            var meshEl = Array("meshes")[meshIndex];
            if (!meshEl.TryGetProperty("primitives", out var prims) || prims.ValueKind != JsonValueKind.Array) return;
            string obj = Safe(objectName) ?? "mesh" + meshIndex;
            bool mirrored = world.GetDeterminant() < 0f;
            bool normalsOk = Matrix4x4.Invert(world, out var inv);
            var normalMatrix = Matrix4x4.Transpose(inv);

            foreach (var prim in prims.EnumerateArray())
            {
                int mode = Int(prim, "mode") ?? 4;
                if (mode is not (4 or 5 or 6)) continue;                       // points and lines carry no surface
                if (Obj(prim, "extensions") is { } pext)
                    foreach (var p in pext.EnumerateObject())
                        if (Unsupported.Contains(p.Name, StringComparer.Ordinal))
                            throw new UnsupportedException($"The model's geometry is compressed ({p.Name}), which this reader does not decode.");
                if (Obj(prim, "attributes") is not { } attrs || Int(attrs, "POSITION") is not int posAcc) continue;

                int matIndex = Int(prim, "material") is int mi && mi >= 0 && mi < _materials.Count ? mi : DefaultMaterial();
                var (matName, _, texCoord, uvt) = _materials[matIndex];

                var pos = ReadFloats(posAcc, out int pc);
                if (pc != 3) continue;
                int vcount = pos.Length / 3;
                float[]? nrm = Attribute(attrs, "NORMAL", 3);
                float[]? uv = Attribute(attrs, "TEXCOORD_" + texCoord, 2) ?? (texCoord != 0 ? Attribute(attrs, "TEXCOORD_0", 2) : null);

                int[] idx = Int(prim, "indices") is int ia ? ReadIndices(ia) : Enumerable.Range(0, vcount).ToArray();
                var tris = new List<(int, int, int)>(idx.Length / 3);
                void Tri(int a, int b, int c)
                {
                    if (a == b || b == c || a == c) return;
                    if (a < 0 || b < 0 || c < 0 || a >= vcount || b >= vcount || c >= vcount) return;
                    tris.Add(mirrored ? (a, c, b) : (a, b, c));
                }
                if (mode == 4) for (int i = 0; i + 2 < idx.Length; i += 3) Tri(idx[i], idx[i + 1], idx[i + 2]);
                else if (mode == 5) for (int i = 0; i + 2 < idx.Length; i++) { if ((i & 1) == 0) Tri(idx[i], idx[i + 1], idx[i + 2]); else Tri(idx[i + 1], idx[i], idx[i + 2]); }
                else for (int i = 1; i + 1 < idx.Length; i++) Tri(idx[0], idx[i], idx[i + 1]);
                if (tris.Count == 0) continue;

                // This primitive as a piece of its own, then onto the (object, material) piece it belongs to.
                var piece = new ObjSubMesh { Material = matName, Object = obj };
                for (int v = 0; v < vcount; v++)
                {
                    var p = Vector3.Transform(new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]), world);
                    piece.Positions.Add(new Vec3(p.X, p.Y, p.Z));
                    if (uv is not null && v * 2 + 1 < uv.Length)
                    {
                        float u = uv[v * 2], t = uv[v * 2 + 1];
                        if (uvt is not null) (u, t) = TransformUv(u, t, uvt);
                        piece.Uvs.Add((u, 1f - t));   // glTF V runs down the image, OBJ's up
                    }
                    else piece.Uvs.Add((0f, 0f));
                }
                foreach (var t in tris) piece.Faces.Add(t);
                if (nrm is not null && normalsOk && nrm.Length >= vcount * 3)
                    for (int v = 0; v < vcount; v++)
                    {
                        var n = Vector3.TransformNormal(new Vector3(nrm[v * 3], nrm[v * 3 + 1], nrm[v * 3 + 2]), normalMatrix);
                        float len = n.Length();
                        piece.Normals.Add(len > 1e-8f ? new Vec3(n.X / len, n.Y / len, n.Z / len) : new Vec3(0f, 1f, 0f));
                    }
                else ObjMesh.ComputeNormals(piece);
                Append(piece);
            }
        }

        /// <summary>KHR_texture_transform: uv' = T * R * S * uv, rotation counter-clockwise in radians.</summary>
        private static (float U, float V) TransformUv(float u, float v, float[] t)
        {
            float ox = t[0], oy = t[1], r = t[2], sx = t[3], sy = t[4];
            float c = MathF.Cos(r), s = MathF.Sin(r);
            return (c * sx * u + s * sy * v + ox, -s * sx * u + c * sy * v + oy);
        }

        private void Append(ObjSubMesh piece)
        {
            string key = piece.Object + "\u0001" + piece.Material;
            if (!_subs.TryGetValue(key, out var into))
            {
                into = new ObjSubMesh { Material = piece.Material, Object = piece.Object };
                _subs[key] = into; _order.Add(key);
            }
            int b = into.Positions.Count;
            into.Positions.AddRange(piece.Positions);
            into.Normals.AddRange(piece.Normals);
            into.Uvs.AddRange(piece.Uvs);
            foreach (var (a, bb, c) in piece.Faces) into.Faces.Add((b + a, b + bb, b + c));
        }
    }
}
