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

    public static ObjMesh Load(string path) => Parse(File.ReadAllText(path));

    public static ObjMesh Parse(string text)
    {
        var v = new List<Vec3>();
        var vt = new List<(float, float)>();
        var vn = new List<Vec3>();
        var subs = new Dictionary<string, ObjSubMesh>(StringComparer.Ordinal);
        var maps = new Dictionary<string, Dictionary<(int, int, int), int>>(StringComparer.Ordinal);
        var order = new List<string>();          // material order of first appearance
        var sawVn = new HashSet<string>();        // materials that referenced at least one normal

        var mtlLibs = new List<string>();
        ObjSubMesh cur = null!; Dictionary<(int, int, int), int> curMap = null!;
        void Use(string m)
        {
            if (subs.TryGetValue(m, out var existing)) cur = existing;
            else { cur = new ObjSubMesh { Material = m }; subs[m] = cur; maps[m] = new(); order.Add(m); }
            curMap = maps[m];
        }
        Use("default");

        int Resolve(int idx, int count) => idx > 0 ? idx - 1 : idx < 0 ? count + idx : -1;   // 1-based or negative-relative

        int Local(int vi, int ti, int ni)
        {
            var key = (vi, ti, ni);
            if (curMap.TryGetValue(key, out var li)) return li;
            li = cur.Positions.Count;
            cur.Positions.Add(vi >= 0 && vi < v.Count ? v[vi] : Vec3.Zero);
            cur.Uvs.Add(ti >= 0 && ti < vt.Count ? vt[ti] : (0f, 0f));
            cur.Normals.Add(ni >= 0 && ni < vn.Count ? vn[ni] : Vec3.Zero);
            if (ni >= 0) sawVn.Add(cur.Material);
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
                case "usemtl" when t.Length >= 2: Use(t[1]); break;
                case "mtllib" when t.Length >= 2: mtlLibs.Add(t[1]); break;
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
        foreach (var m in order)
        {
            var s = subs[m];
            if (s.Faces.Count == 0) continue;                  // drop empty groups (e.g. the unused "default")
            if (!sawVn.Contains(m)) ComputeNormals(s);          // no normals in the file -> derive from faces
            mesh.SubMeshes.Add(s);
        }
        mesh.RecomputeBounds();
        return mesh;
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

    private void RecomputeBounds()
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

    private static void ComputeNormals(ObjSubMesh s)
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
        for (int i = 0; i < acc.Length; i++)
        {
            var n = acc[i];
            float len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            s.Normals[i] = len > 1e-8f ? new Vec3(n.X / len, n.Y / len, n.Z / len) : new Vec3(0, 1, 0);
        }
    }

    private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
