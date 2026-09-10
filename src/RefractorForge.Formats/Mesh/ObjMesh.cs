using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>One material group of an imported OBJ: its own vertex arrays (position/normal/uv) and the triangles
/// that index them. Mirrors a Refractor <c>.sm</c> material section, so it maps straight onto the mesh writer.</summary>
public sealed class ObjSubMesh
{
    public string Material = "default";
    /// <summary>The <c>o</c>/<c>g</c> object this piece came from, "" when the file named none. The toolkit's
    /// naming convention (LOD01, COL01, shadow, bbox - see <see cref="MeshParts"/>) is read off this.</summary>
    public string Object = "";
    public List<Vec3> Positions = new();
    public List<Vec3> Normals = new();
    public List<(float U, float V)> Uvs = new();
    public List<(int A, int B, int C)> Faces = new();   // triangles, indices into the lists above
}

/// <summary>
/// A clean-room Wavefront <c>.obj</c> parser. Resolves the separate v/vt/vn index streams into unified
/// per-material vertices (deduped by the v/vt/vn triple), triangulates polygons (fan), fills in normals when
/// the file has none, and records a bounding box. The output is shaped to feed both the editor's renderer and
/// the <c>.sm</c> writer (one vertex array + triangle list per material).
/// <para>
/// Pieces are kept per (object, material), so a file whose objects are named the way the Battlefield toolkit
/// expects - LOD01, COL01, shadow - can be taken apart by <see cref="MeshParts"/>. A caller that does not care
/// which object a triangle came from calls <see cref="MergeByMaterial"/> and gets one piece per material back.
/// </para>
/// </summary>
public sealed class ObjMesh
{
    public List<ObjSubMesh> SubMeshes { get; } = new();
    /// <summary>The <c>mtllib</c> files referenced by the .obj (resolved relative to the .obj's folder).</summary>
    public List<string> MtlLibs { get; } = new();
    /// <summary>minX, minY, minZ, maxX, maxY, maxZ.</summary>
    public float[] BoundingBox { get; } = { 0, 0, 0, 0, 0, 0 };
    public int TotalVertices => SubMeshes.Sum(s => s.Positions.Count);
    public int TotalFaces => SubMeshes.Sum(s => s.Faces.Count);
    /// <summary>The distinct object names, in order of first appearance ("" for geometry outside any object).</summary>
    public IEnumerable<string> Objects => SubMeshes.Select(s => s.Object).Distinct(StringComparer.Ordinal);

    public static ObjMesh Load(string path) => Parse(File.ReadAllText(path));

    public static ObjMesh Parse(string text)
    {
        var v = new List<Vec3>();
        var vt = new List<(float, float)>();
        var vn = new List<Vec3>();
        var subs = new Dictionary<string, ObjSubMesh>(StringComparer.Ordinal);
        var maps = new Dictionary<string, Dictionary<(int, int, int), int>>(StringComparer.Ordinal);
        var order = new List<string>();          // (object, material) order of first appearance
        var sawVn = new HashSet<string>();        // pieces that referenced at least one normal

        var mtlLibs = new List<string>();
        string curObject = "", curMaterial = "default";
        ObjSubMesh cur = null!; Dictionary<(int, int, int), int> curMap = null!;
        void Use()
        {
            string key = curObject + "" + curMaterial;
            if (subs.TryGetValue(key, out var existing)) cur = existing;
            else { cur = new ObjSubMesh { Material = curMaterial, Object = curObject }; subs[key] = cur; maps[key] = new(); order.Add(key); }
            curMap = maps[key];
        }
        Use();

        int Resolve(int idx, int count) => idx > 0 ? idx - 1 : idx < 0 ? count + idx : -1;   // 1-based or negative-relative

        int Local(int vi, int ti, int ni)
        {
            var key = (vi, ti, ni);
            if (curMap.TryGetValue(key, out var li)) return li;
            li = cur.Positions.Count;
            cur.Positions.Add(vi >= 0 && vi < v.Count ? v[vi] : Vec3.Zero);
            cur.Uvs.Add(ti >= 0 && ti < vt.Count ? vt[ti] : (0f, 0f));
            cur.Normals.Add(ni >= 0 && ni < vn.Count ? vn[ni] : Vec3.Zero);
            if (ni >= 0) sawVn.Add(curObject + "" + curMaterial);
            curMap[key] = li;
            return li;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace("\r", "").Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            switch (t[0])
            {
                case "v" when t.Length >= 4: v.Add(new Vec3(F(t[1]), F(t[2]), F(t[3]))); break;
                case "vt" when t.Length >= 3: vt.Add((F(t[1]), F(t[2]))); break;
                case "vn" when t.Length >= 4: vn.Add(new Vec3(F(t[1]), F(t[2]), F(t[3]))); break;
                case "usemtl" when t.Length >= 2: curMaterial = t[1]; Use(); break;
                case "mtllib" when t.Length >= 2: mtlLibs.Add(t[1]); break;
                // `o` names an object; `g` names one or more groups (Blender writes both). Either starts a new
                // piece; "off" is the grammar's own "no group".
                case "o":
                case "g":
                    curObject = t.Length >= 2 && !t[1].Equals("off", StringComparison.OrdinalIgnoreCase) ? t[1] : "";
                    Use();
                    break;
                case "f" when t.Length >= 4:
                {
                    // Resolve each corner to a local vertex index, then fan-triangulate.
                    var idx = new int[t.Length - 1];
                    for (int i = 1; i < t.Length; i++)
                    {
                        var p = t[i].Split('/');
                        int vi = Resolve(int.Parse(p[0], CultureInfo.InvariantCulture), v.Count);
                        int ti = p.Length > 1 && p[1].Length > 0 ? Resolve(int.Parse(p[1], CultureInfo.InvariantCulture), vt.Count) : -1;
                        int ni = p.Length > 2 && p[2].Length > 0 ? Resolve(int.Parse(p[2], CultureInfo.InvariantCulture), vn.Count) : -1;
                        idx[i - 1] = Local(vi, ti, ni);
                    }
                    for (int i = 1; i + 1 < idx.Length; i++) cur.Faces.Add((idx[0], idx[i], idx[i + 1]));
                    break;
                }
            }
        }

        var mesh = new ObjMesh();
        mesh.MtlLibs.AddRange(mtlLibs);
        foreach (var key in order)
        {
            var s = subs[key];
            if (s.Faces.Count == 0) continue;                  // drop empty pieces (e.g. the unused "default")
            if (!sawVn.Contains(key)) ComputeNormals(s);        // no normals in the file -> derive from faces
            mesh.SubMeshes.Add(s);
        }
        mesh.RecomputeBounds();
        return mesh;
    }

    /// <summary>A mesh assembled from existing pieces (they are shared, not copied).</summary>
    public static ObjMesh FromSubMeshes(IEnumerable<ObjSubMesh> pieces, IEnumerable<string>? mtlLibs = null)
    {
        var m = new ObjMesh();
        m.SubMeshes.AddRange(pieces);
        if (mtlLibs is not null) m.MtlLibs.AddRange(mtlLibs);
        m.RecomputeBounds();
        return m;
    }

    /// <summary>Fold the per-object pieces back into one piece per material - what the <c>.sm</c> writer and the
    /// decimator want once the objects have been sorted into their roles. Vertex arrays are concatenated and the
    /// indices rebased; nothing is deduplicated across objects.</summary>
    public void MergeByMaterial()
    {
        if (SubMeshes.Count <= 1) return;
        var merged = new List<ObjSubMesh>();
        var byMat = new Dictionary<string, ObjSubMesh>(StringComparer.Ordinal);
        foreach (var s in SubMeshes)
        {
            if (!byMat.TryGetValue(s.Material, out var into))
            {
                into = new ObjSubMesh { Material = s.Material, Object = s.Object };
                byMat[s.Material] = into; merged.Add(into);
            }
            int b = into.Positions.Count;
            into.Positions.AddRange(s.Positions); into.Normals.AddRange(s.Normals); into.Uvs.AddRange(s.Uvs);
            foreach (var (a, bb, c) in s.Faces) into.Faces.Add((b + a, b + bb, b + c));
            if (!string.Equals(into.Object, s.Object, StringComparison.Ordinal)) into.Object = "";
        }
        SubMeshes.Clear();
        SubMeshes.AddRange(merged);
    }

    /// <summary>Turn every triangle over: (a,b,c) becomes (a,c,b). The normals are left alone - they already point
    /// outward; this is about which way round the engine reads the triangle, see <see cref="MeshFit"/>.</summary>
    public void ReverseWinding()
    {
        foreach (var s in SubMeshes)
            for (int i = 0; i < s.Faces.Count; i++)
            {
                var (a, b, c) = s.Faces[i];
                s.Faces[i] = (a, c, b);
            }
    }

    /// <summary>Uniformly scale + translate every vertex (used to fit an import to a sensible world size).</summary>
    public void Transform(float scale, Vec3 offset)
    {
        foreach (var s in SubMeshes)
            for (int i = 0; i < s.Positions.Count; i++)
            {
                var p = s.Positions[i];
                s.Positions[i] = new Vec3(p.X * scale + offset.X, p.Y * scale + offset.Y, p.Z * scale + offset.Z);
            }
        RecomputeBounds();
    }

    public void RecomputeBounds()
    {
        if (TotalVertices == 0) { Array.Clear(BoundingBox, 0, 6); return; }
        float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
        float maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
        foreach (var s in SubMeshes)
            foreach (var p in s.Positions)
            {
                minx = MathF.Min(minx, p.X); miny = MathF.Min(miny, p.Y); minz = MathF.Min(minz, p.Z);
                maxx = MathF.Max(maxx, p.X); maxy = MathF.Max(maxy, p.Y); maxz = MathF.Max(maxz, p.Z);
            }
        BoundingBox[0] = minx; BoundingBox[1] = miny; BoundingBox[2] = minz;
        BoundingBox[3] = maxx; BoundingBox[4] = maxy; BoundingBox[5] = maxz;
    }

    /// <summary>Area-weighted smooth normals from the triangles, for a piece whose file carried none. The
    /// right-hand rule: for a counter-clockwise triangle these point outward.</summary>
    public static void ComputeNormals(ObjSubMesh s)
    {
        var acc = new Vec3[s.Positions.Count];
        foreach (var (a, b, c) in s.Faces)
        {
            var pa = s.Positions[a]; var pb = s.Positions[b]; var pc = s.Positions[c];
            float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
            float wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
            float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;   // cross(u, w)
            acc[a] = new Vec3(acc[a].X + nx, acc[a].Y + ny, acc[a].Z + nz);
            acc[b] = new Vec3(acc[b].X + nx, acc[b].Y + ny, acc[b].Z + nz);
            acc[c] = new Vec3(acc[c].X + nx, acc[c].Y + ny, acc[c].Z + nz);
        }
        while (s.Normals.Count < s.Positions.Count) s.Normals.Add(Vec3.Zero);
        for (int i = 0; i < acc.Length; i++)
        {
            var n = acc[i];
            float len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            s.Normals[i] = len > 1e-8f ? new Vec3(n.X / len, n.Y / len, n.Z / len) : new Vec3(0, 1, 0);
        }
    }

    private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
