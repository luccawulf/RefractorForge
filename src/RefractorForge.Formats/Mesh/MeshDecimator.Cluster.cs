using System;
using System.Collections.Generic;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

public static partial class MeshDecimator
{
    /// <summary>
    /// Simplify to <paramref name="targetTriangles"/> and get there: the quadric collapse first (<see cref="Decimate(ObjMesh,int,float,bool)"/>),
    /// and when a model's seams and loose shells stop it short - a car downloaded at half a million triangles is a
    /// few hundred separate panels, bolts and badges, every one a border, and the collapse stalls near 100,000
    /// whatever it is asked for - vertex clustering takes the rest of the way (<see cref="Cluster(ObjMesh,int,out Result)"/>).
    /// What the game cannot take (more than 65,535 vertices in one material, a shadow over 21,845 triangles) is a
    /// hard limit, so a caller bound by one asks for this.
    /// </summary>
    public static ObjMesh DecimateTo(ObjMesh source, int targetTriangles, out Result result, float weldEpsilon = 1e-5f)
    {
        var first = Decimate(source, targetTriangles, out result, weldEpsilon);
        if (result.Triangles <= targetTriangles) return first;
        int collapses = result.Collapses;
        var clustered = Cluster(first, targetTriangles, out var r2);
        result = new Result(source.TotalFaces, r2.Triangles, r2.Vertices, collapses);
        return clustered;
    }

    public static ObjMesh DecimateTo(ObjMesh source, int targetTriangles) => DecimateTo(source, targetTriangles, out _);

    /// <summary>
    /// Vertex clustering onto a grid, sized by bisection so the result has at most <paramref name="targetTriangles"/>
    /// triangles (as close under as a grid allows). It never stalls - topology does not constrain it - which is why it is
    /// the second stage and not the first: it moves vertices, the collapse does not.
    /// <list type="bullet">
    /// <item>Every vertex in a cell moves to ONE position, the cell's own (its vertex nearest the cell's mean, so it stays
    ///   on the surface) - shared by every material and every UV island in the cell, so nothing cracks open along a
    ///   seam or a material border.</item>
    /// <item>Attributes are kept per UV island: the vertices of one cell are split by the island they belong to (the
    ///   triangles sharing a vertex index, per material), each island keeping the UV and normal of its own member
    ///   nearest the cell's position - so a texture does not smear across a seam.</item>
    /// <item>A triangle with two corners in one cell is gone, and so is a shell smaller than a cell: the bolts and badges
    ///   that cost a model most of its triangles are what disappears first.</item>
    /// </list>
    /// </summary>
    public static ObjMesh Cluster(ObjMesh source, int targetTriangles, out Result result)
    {
        int total = source.TotalFaces;
        if (total <= targetTriangles || total == 0)
        {
            var copy = new ObjMesh();
            copy.MtlLibs.AddRange(source.MtlLibs);
            foreach (var s in source.SubMeshes) copy.SubMeshes.Add(CloneWithObject(s));
            RecomputeBounds(copy);
            result = new Result(total, copy.TotalFaces, copy.TotalVertices, 0);
            return copy;
        }
        var islands = source.SubMeshes.Select(Islands).ToList();
        float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
        foreach (var s in source.SubMeshes)
            foreach (var p in s.Positions)
            {
                minx = MathF.Min(minx, p.X); miny = MathF.Min(miny, p.Y); minz = MathF.Min(minz, p.Z);
                maxx = MathF.Max(maxx, p.X); maxy = MathF.Max(maxy, p.Y); maxz = MathF.Max(maxz, p.Z);
            }
        var origin = new Vec3(minx, miny, minz);
        float diag = MathF.Sqrt((maxx - minx) * (maxx - minx) + (maxy - miny) * (maxy - miny) + (maxz - minz) * (maxz - minz));
        if (diag <= 0) diag = 1;

        // Bisect the cell size: smaller cells keep more. The count falls (nearly) monotonically as the cells grow.
        float lo = 0, hi = diag;
        ObjMesh? best = null;
        for (int iter = 0; iter < 28; iter++)
        {
            float h = iter == 0 ? diag / MathF.Sqrt(Math.Max(1, targetTriangles)) : (lo + hi) * 0.5f;
            if (h <= 0) h = diag * 1e-6f;
            var m = ClusterAt(source, islands, origin, h);
            if (m.TotalFaces <= targetTriangles)
            {
                best = m;
                hi = h;
                if (m.TotalFaces >= targetTriangles * 0.97) break;       // close enough under the budget
            }
            else lo = h;
            if (hi - lo < diag * 1e-5f) break;
        }
        best ??= ClusterAt(source, islands, origin, diag);                 // one cell: nothing but what no cell can hold
        result = new Result(total, best.TotalFaces, best.TotalVertices, 0);
        return best;
    }

    /// <summary>Each vertex's island: the connected pieces of a submesh, joined through shared vertex indices (a
    /// UV seam splits a vertex into two indices, so it is where two islands meet).</summary>
    private static int[] Islands(ObjSubMesh s)
    {
        int n = s.Positions.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        foreach (var (a, b, c) in s.Faces) { Union(a, b); Union(b, c); }
        var id = new int[n];
        for (int i = 0; i < n; i++) id[i] = Find(i);
        return id;
    }

    private static ObjMesh ClusterAt(ObjMesh source, List<int[]> islands, Vec3 origin, float h)
    {
        (long, long, long) CellOf(Vec3 p) => ((long)MathF.Floor((p.X - origin.X) / h), (long)MathF.Floor((p.Y - origin.Y) / h), (long)MathF.Floor((p.Z - origin.Z) / h));

        // One position per cell for the whole mesh: the vertex nearest the mean of everything in the cell.
        var sum = new Dictionary<(long, long, long), (double X, double Y, double Z, int N)>();
        foreach (var s in source.SubMeshes)
            foreach (var p in s.Positions)
            {
                var k = CellOf(p);
                var a = sum.GetValueOrDefault(k);
                sum[k] = (a.X + p.X, a.Y + p.Y, a.Z + p.Z, a.N + 1);
            }
        var cellPos = new Dictionary<(long, long, long), (Vec3 P, float D)>();
        foreach (var s in source.SubMeshes)
            foreach (var p in s.Positions)
            {
                var k = CellOf(p);
                var a = sum[k];
                var mean = new Vec3((float)(a.X / a.N), (float)(a.Y / a.N), (float)(a.Z / a.N));
                float d = (p.X - mean.X) * (p.X - mean.X) + (p.Y - mean.Y) * (p.Y - mean.Y) + (p.Z - mean.Z) * (p.Z - mean.Z);
                if (!cellPos.TryGetValue(k, out var cur) || d < cur.D) cellPos[k] = (p, d);
            }

        var outMesh = new ObjMesh();
        outMesh.MtlLibs.AddRange(source.MtlLibs);
        for (int si = 0; si < source.SubMeshes.Count; si++)
        {
            var s = source.SubMeshes[si];
            var island = islands[si];
            bool hasNormals = s.Normals.Count == s.Positions.Count, hasUvs = s.Uvs.Count == s.Positions.Count;
            // (cell, island) -> the member whose position is nearest the cell's position, and its output index
            var rep = new Dictionary<((long, long, long) Cell, int Island), (int Vertex, float D)>();
            var cellOf = new (long, long, long)[s.Positions.Count];
            for (int v = 0; v < s.Positions.Count; v++)
            {
                var k = CellOf(s.Positions[v]);
                cellOf[v] = k;
                var cp = cellPos[k].P;
                var p = s.Positions[v];
                float d = (p.X - cp.X) * (p.X - cp.X) + (p.Y - cp.Y) * (p.Y - cp.Y) + (p.Z - cp.Z) * (p.Z - cp.Z);
                var key = (k, island[v]);
                if (!rep.TryGetValue(key, out var cur) || d < cur.D) rep[key] = (v, d);
            }
            var sub = new ObjSubMesh { Material = s.Material, Object = s.Object };
            var index = new Dictionary<((long, long, long), int), int>();
            int Out(int v)
            {
                var key = (cellOf[v], island[v]);
                if (index.TryGetValue(key, out int i)) return i;
                int r = rep[key].Vertex;
                i = sub.Positions.Count;
                sub.Positions.Add(cellPos[cellOf[v]].P);
                if (hasNormals) sub.Normals.Add(s.Normals[r]);
                if (hasUvs) sub.Uvs.Add(s.Uvs[r]);
                index[key] = i;
                return i;
            }
            var seen = new HashSet<(int, int, int)>();
            foreach (var (a, b, c) in s.Faces)
            {
                var ka = cellOf[a]; var kb = cellOf[b]; var kc = cellOf[c];
                if (ka == kb || kb == kc || ka == kc) continue;       // two corners in one cell: collapsed
                // A triangle the move turned over would show its back: it goes (the surface around it closes the gap).
                var n0 = Normal(s.Positions[a], s.Positions[b], s.Positions[c]);
                var n1 = Normal(cellPos[ka].P, cellPos[kb].P, cellPos[kc].P);
                if (n0.X * n1.X + n0.Y * n1.Y + n0.Z * n1.Z <= 0) continue;
                int oa = Out(a), ob = Out(b), oc = Out(c);
                // One triangle per corner set and winding, whichever corner it starts at.
                var key = oa < ob && oa < oc ? (oa, ob, oc) : ob < oc ? (ob, oc, oa) : (oc, oa, ob);
                if (seen.Add(key)) sub.Faces.Add((oa, ob, oc));
            }
            // Normals from the simplified surface (a representative's own normal can face another way than the
            // triangles now around it): area-weighted, per island, so a hard edge between panels stays hard.
            if (hasNormals && sub.Faces.Count > 0)
            {
                var acc = new Vec3[sub.Positions.Count];
                foreach (var (fa, fb, fc) in sub.Faces)
                {
                    var n = Normal(sub.Positions[fa], sub.Positions[fb], sub.Positions[fc]);
                    acc[fa] = Add(acc[fa], n); acc[fb] = Add(acc[fb], n); acc[fc] = Add(acc[fc], n);
                }
                for (int v = 0; v < acc.Length; v++)
                {
                    float len = MathF.Sqrt(acc[v].X * acc[v].X + acc[v].Y * acc[v].Y + acc[v].Z * acc[v].Z);
                    if (len > 1e-12f) sub.Normals[v] = new Vec3(acc[v].X / len, acc[v].Y / len, acc[v].Z / len);
                }
            }
            if (sub.Faces.Count > 0) outMesh.SubMeshes.Add(sub);
        }
        RecomputeBounds(outMesh);
        return outMesh;
    }

    /// <summary>The most triangles a shadow mesh can have: the .sm keeps it exploded, three u16-indexed vertices a
    /// triangle (StandardMeshWriter.WriteShadow).</summary>
    public const int MaxShadowTriangles = 65535 / 3;

    /// <summary>
    /// The shape of <paramref name="source"/> alone, for what keeps nothing else - a shadow mesh (the .sm stores its
    /// positions only) or a collision mesh: every material becomes one, UVs and normals go and coincident positions are
    /// welded, so the collapse has no seam to stop at, and the result is taken down to <paramref name="targetTriangles"/>
    /// whatever the model is made of. A shadow must also stay within <see cref="MaxShadowTriangles"/>.
    /// </summary>
    public static ObjMesh ShapeOf(ObjMesh source, int targetTriangles, float weldEpsilon = 1e-5f)
    {
        targetTriangles = Math.Max(4, targetTriangles);
        var sub = new ObjSubMesh { Material = source.SubMeshes.FirstOrDefault()?.Material ?? "shape" };
        var byKey = new Dictionary<PositionKey, int>();
        foreach (var s in source.SubMeshes)
        {
            var map = new int[s.Positions.Count];
            for (int v = 0; v < s.Positions.Count; v++)
            {
                var key = Key(s.Positions[v], weldEpsilon);
                if (!byKey.TryGetValue(key, out int i))
                {
                    i = sub.Positions.Count;
                    byKey[key] = i;
                    sub.Positions.Add(s.Positions[v]);
                    sub.Normals.Add(new Vec3(0, 1, 0));
                    sub.Uvs.Add((0f, 0f));
                }
                map[v] = i;
            }
            foreach (var (a, b, c) in s.Faces)
            {
                int ma = map[a], mb = map[b], mc = map[c];
                if (ma != mb && mb != mc && ma != mc) sub.Faces.Add((ma, mb, mc));
            }
        }
        var welded = new ObjMesh();
        if (sub.Faces.Count > 0) welded.SubMeshes.Add(sub);
        RecomputeBounds(welded);
        return DecimateTo(welded, targetTriangles, out _, weldEpsilon);
    }

    private static Vec3 Add(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static ObjSubMesh CloneWithObject(ObjSubMesh s)
    {
        var c = Clone(s);
        c.Object = s.Object;
        return c;
    }
}
