using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// A reader for Autodesk <c>.3ds</c> - the 3D Studio format every 3ds Max (and gmax, Blender, Milkshape...) can
/// write, and the one the Battlefield toolkit's own <c>3dsToSm.exe</c> converts. It is a tree of chunks, each
/// <c>u16 id, u32 length</c> (length includes the six-byte header), and the parts a static model needs are few:
/// <code>
///   4D4D  main                       3D3D  editor
///     0100  master scale (f32)
///     AFFF  material: A000 name, A020 diffuse {0011 rgb8 | 0010 rgbf}, A200 texture map {A300 file name}
///     4000  object (ASCIIZ name)     4100  triangle mesh
///       4110 vertices (u16 n; n x f32 xyz)      4140 texture coords (u16 n; n x f32 uv)
///       4120 faces (u16 n; n x u16 a,b,c,flags) then 4130 face material (ASCIIZ name; u16 n; n x u16 face)
///   B000  keyframer - animation and hierarchy, not read
/// </code>
/// Geometry comes out exactly as the file has it: Z-up, right-handed, in the author's units. The fit step
/// (<see cref="MeshFit"/>, <see cref="UpAxis.ZSwap"/>) turns it into the engine's frame the way the Max exporter
/// did - a swap of Y and Z, which is what every retail mesh went through. Object names are kept, so a file that
/// follows the toolkit's LOD01 / COL01 / shadow convention is taken apart by <see cref="MeshParts"/>.
/// <para>
/// Not read: smoothing groups (normals are averaged across each piece instead), the per-object matrix (4160 -
/// vertices are stored already placed, the matrix is the pivot), vertex colours, and everything in the keyframer.
/// </para>
/// </summary>
public static class ThreeDsMesh
{
    public const string Extension = ".3ds";

    public static bool Is(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The model plus its materials, keyed by material name the way an <c>.mtl</c> would be.</summary>
    public sealed record Result(ObjMesh Mesh, Dictionary<string, ObjMaterial> Materials, float MasterScale);

    public static Result Load(string path) => Parse(File.ReadAllBytes(path));

    public static Result Parse(byte[] data)
    {
        if (data.Length < 6 || U16(data, 0) != 0x4D4D) throw new InvalidDataException("Not a .3ds file (no 4D4D main chunk).");
        var st = new State();
        Walk(data, 0, data.Length, st);

        var mesh = new ObjMesh();
        foreach (var o in st.Objects)
        {
            if (o.Verts.Count == 0 || o.Faces.Count == 0) continue;
            // One piece per material used by this object; faces the file gave no material go to "default".
            var byMat = new Dictionary<string, (ObjSubMesh Sub, Dictionary<int, int> Local)>(StringComparer.Ordinal);
            var faceMat = new string[o.Faces.Count];
            foreach (var (mat, faces) in o.FaceMaterials)
                foreach (int fi in faces)
                    if (fi >= 0 && fi < faceMat.Length) faceMat[fi] = mat;
            for (int fi = 0; fi < o.Faces.Count; fi++)
            {
                string mat = faceMat[fi] ?? "default";
                if (!byMat.TryGetValue(mat, out var entry))
                {
                    entry = (new ObjSubMesh { Material = mat, Object = o.Name }, new Dictionary<int, int>());
                    byMat[mat] = entry;
                }
                var (a, b, c) = o.Faces[fi];
                if ((uint)a >= (uint)o.Verts.Count || (uint)b >= (uint)o.Verts.Count || (uint)c >= (uint)o.Verts.Count) continue;
                if (a == b || b == c || a == c) continue;
                entry.Sub.Faces.Add((Local(entry, o, a, st.MasterScale), Local(entry, o, b, st.MasterScale), Local(entry, o, c, st.MasterScale)));
            }
            foreach (var (_, e) in byMat)
            {
                if (e.Sub.Faces.Count == 0) continue;
                ObjMesh.ComputeNormals(e.Sub);
                mesh.SubMeshes.Add(e.Sub);
            }
        }
        mesh.RecomputeBounds();

        var mats = new Dictionary<string, ObjMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in st.Materials) mats[m.Name] = m;
        return new Result(mesh, mats, st.MasterScale);
    }

    static int Local((ObjSubMesh Sub, Dictionary<int, int> Local) e, Obj o, int vi, float scale)
    {
        if (e.Local.TryGetValue(vi, out int li)) return li;
        li = e.Sub.Positions.Count;
        var p = o.Verts[vi];
        e.Sub.Positions.Add(new Vec3(p.X * scale, p.Y * scale, p.Z * scale));
        e.Sub.Uvs.Add(vi < o.Uvs.Count ? o.Uvs[vi] : (0f, 0f));
        e.Sub.Normals.Add(Vec3.Zero);
        e.Local[vi] = li;
        return li;
    }

    sealed class Obj
    {
        public string Name = "";
        public List<Vec3> Verts = new();
        public List<(float, float)> Uvs = new();
        public List<(int, int, int)> Faces = new();
        public List<(string Material, List<int> Faces)> FaceMaterials = new();
    }

    sealed class State
    {
        public List<Obj> Objects = new();
        public List<ObjMaterial> Materials = new();
        public float MasterScale = 1f;
        public Obj? Cur;
        public ObjMaterial? CurMat;
    }

    // Chunks that contain other chunks straight after their header (or after a fixed payload, handled inline).
    static void Walk(byte[] d, int start, int end, State st)
    {
        int p = start;
        while (p + 6 <= end)
        {
            ushort id = U16(d, p);
            int len = (int)Math.Min(U32(d, p + 2), (uint)(end - p));
            if (len < 6) return;                                   // a corrupt length would spin forever
            int body = p + 6, next = p + len;
            switch (id)
            {
                case 0x4D4D: case 0x3D3D: case 0x4100: case 0xA020: case 0xA200:
                    Walk(d, body, next, st); break;
                case 0x0100:
                    if (body + 4 <= next) { float s = F32(d, body); if (s > 0f && float.IsFinite(s)) st.MasterScale = s; }
                    break;
                case 0xAFFF:
                    st.CurMat = new ObjMaterial();
                    Walk(d, body, next, st);
                    if (st.CurMat.Name.Length > 0) st.Materials.Add(st.CurMat);
                    st.CurMat = null;
                    break;
                case 0xA000: if (st.CurMat is not null) st.CurMat.Name = Ascii(d, body, next, out _); break;
                case 0x0011: case 0x0013:
                    if (st.CurMat is not null && body + 3 <= next) st.CurMat.Diffuse = new Vec3(d[body] / 255f, d[body + 1] / 255f, d[body + 2] / 255f);
                    break;
                case 0x0010: case 0x0012:
                    if (st.CurMat is not null && body + 12 <= next) st.CurMat.Diffuse = new Vec3(F32(d, body), F32(d, body + 4), F32(d, body + 8));
                    break;
                case 0xA300: if (st.CurMat is not null) st.CurMat.TextureFile = Ascii(d, body, next, out _); break;
                case 0x4000:
                {
                    var o = new Obj { Name = Ascii(d, body, next, out int after) };
                    st.Cur = o; st.Objects.Add(o);
                    Walk(d, after, next, st);
                    st.Cur = null;
                    break;
                }
                case 0x4110:
                    if (st.Cur is not null && body + 2 <= next)
                    {
                        int n = U16(d, body); int q = body + 2;
                        for (int i = 0; i < n && q + 12 <= next; i++, q += 12) st.Cur.Verts.Add(new Vec3(F32(d, q), F32(d, q + 4), F32(d, q + 8)));
                    }
                    break;
                case 0x4140:
                    if (st.Cur is not null && body + 2 <= next)
                    {
                        int n = U16(d, body); int q = body + 2;
                        for (int i = 0; i < n && q + 8 <= next; i++, q += 8) st.Cur.Uvs.Add((F32(d, q), F32(d, q + 4)));
                    }
                    break;
                case 0x4120:
                    if (st.Cur is not null && body + 2 <= next)
                    {
                        int n = U16(d, body); int q = body + 2;
                        for (int i = 0; i < n && q + 8 <= next; i++, q += 8) st.Cur.Faces.Add((U16(d, q), U16(d, q + 2), U16(d, q + 4)));
                        Walk(d, q, next, st);                      // 4130 face-material and 4150 smoothing follow
                    }
                    break;
                case 0x4130:
                    if (st.Cur is not null)
                    {
                        string mat = Ascii(d, body, next, out int q);
                        if (q + 2 <= next)
                        {
                            int n = U16(d, q); q += 2;
                            var list = new List<int>(n);
                            for (int i = 0; i < n && q + 2 <= next; i++, q += 2) list.Add(U16(d, q));
                            st.Cur.FaceMaterials.Add((mat, list));
                        }
                    }
                    break;
                // 0xB000 keyframer, 0x4150 smoothing, 0x4160 matrix, everything else: skipped by length.
            }
            p = next;
        }
    }

    static string Ascii(byte[] d, int p, int end, out int after)
    {
        int q = p;
        while (q < end && d[q] != 0) q++;
        after = Math.Min(q + 1, end);
        return Encoding.Latin1.GetString(d, p, q - p);
    }

    static ushort U16(byte[] d, int p) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));
    static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
    static float F32(byte[] d, int p) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p, 4));
}
