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
    /// <param name="importance">How much each submesh's shape counts (see <see cref="Decimate(ObjMesh,int,out Result,float,bool,Func{ObjSubMesh,float})"/>).</param>
    public static ObjMesh DecimateTo(ObjMesh source, int targetTriangles, out Result result, float weldEpsilon = 1e-5f,
                                     Func<ObjSubMesh, float>? importance = null)
    {
        var first = Decimate(source, targetTriangles, out result, weldEpsilon, importance: importance);
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
    /// <item>Every vertex in a cell that faces the same way moves to ONE position - the point nearest the planes of all
    ///   the triangles around them (Lindstrom's quadric clustering), so a flat panel stays flat and a crease or a corner
    ///   stays sharp - shared by every material and every UV island in the cell, so nothing cracks open along a seam or
    ///   a material border. Vertices facing against the rest of their cell (the inner skin of a panel modelled with two,
    ///   a centimetre behind the outer) cluster apart: merging them crumpled the panel.</item>
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

    /// <summary>A cluster: a cell of the grid, and which way its vertices face in it (0: with the cell's surface, 1:
    /// against it - the inner skin of a panel modelled with two).</summary>
    private readonly record struct ClusterKey(long X, long Y, long Z, int Side);

    /// <summary>The planes around a cluster, summed (area-weighted <c>n nT</c>, <c>d n</c>) with the members' mean: where
    /// its vertex goes is the point nearest all of them.</summary>
    private sealed class ClusterQuadric
    {
        public double A11, A12, A13, A22, A23, A33, B1, B2, B3;
        public double Mx, My, Mz;
        public int N;

        public void AddPlane(Vec3 n, float d, float w)
        {
            A11 += w * n.X * n.X; A12 += w * n.X * n.Y; A13 += w * n.X * n.Z;
            A22 += w * n.Y * n.Y; A23 += w * n.Y * n.Z; A33 += w * n.Z * n.Z;
            B1 += w * d * n.X; B2 += w * d * n.Y; B3 += w * d * n.Z;
        }

        /// <summary>The point nearest every plane, drawn toward the members' mean where the planes leave it free (a flat
        /// panel pins one direction, an edge two, a corner all three); the mean when that point is outside the cell's
        /// neighbourhood (nearly parallel planes put it far away).</summary>
        public Vec3 Solve(Vec3 cellMin, float h)
        {
            var m = (X: Mx / N, Y: My / N, Z: Mz / N);
            double trace = A11 + A22 + A33;
            double l = 1e-3 * trace + 1e-12;
            double a11 = A11 + l, a22 = A22 + l, a33 = A33 + l;
            double r1 = l * m.X - B1, r2 = l * m.Y - B2, r3 = l * m.Z - B3;
            double c11 = a22 * a33 - A23 * A23, c12 = A13 * A23 - A12 * a33, c13 = A12 * A23 - A13 * a22;
            double det = a11 * c11 + A12 * c12 + A13 * c13;
            var mean = new Vec3((float)m.X, (float)m.Y, (float)m.Z);
            if (Math.Abs(det) < 1e-30) return mean;
            double c22 = a11 * a33 - A13 * A13, c23 = A12 * A13 - a11 * A23, c33 = a11 * a22 - A12 * A12;
            double x = (c11 * r1 + c12 * r2 + c13 * r3) / det, y = (c12 * r1 + c22 * r2 + c23 * r3) / det, z = (c13 * r1 + c23 * r2 + c33 * r3) / det;
            float slack = 0.5f * h;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                || x < cellMin.X - slack || x > cellMin.X + h + slack || y < cellMin.Y - slack || y > cellMin.Y + h + slack
                || z < cellMin.Z - slack || z > cellMin.Z + h + slack)
                return mean;
            return new Vec3((float)x, (float)y, (float)z);
        }
    }

    private static ObjMesh ClusterAt(ObjMesh source, List<int[]> islands, Vec3 origin, float h)
    {
        (long, long, long) CellOf(Vec3 p) => ((long)MathF.Floor((p.X - origin.X) / h), (long)MathF.Floor((p.Y - origin.Y) / h), (long)MathF.Floor((p.Z - origin.Z) / h));

        // Each vertex's facing: the area-weighted normal of its own triangles (the file's normals may be anything).
        var facing = new List<Vec3[]>(source.SubMeshes.Count);
        foreach (var s in source.SubMeshes)
        {
            var acc = new Vec3[s.Positions.Count];
            foreach (var (a, b, c) in s.Faces)
            {
                var n = Cross(s.Positions[a], s.Positions[b], s.Positions[c]);
                acc[a] = Add(acc[a], n); acc[b] = Add(acc[b], n); acc[c] = Add(acc[c], n);
            }
            facing.Add(acc);
        }
        // Each cell's surface direction: its members' facings summed - or, where they cancel (two skins facing apart),
        // its first member's. A member facing against it belongs to the cell's other side.
        var cellDir = new Dictionary<(long, long, long), (Vec3 Sum, float Mag, Vec3 First)>();
        for (int si = 0; si < source.SubMeshes.Count; si++)
            for (int v = 0; v < source.SubMeshes[si].Positions.Count; v++)
            {
                var k = CellOf(source.SubMeshes[si].Positions[v]);
                var n = facing[si][v];
                float len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
                cellDir[k] = cellDir.TryGetValue(k, out var d) ? (Add(d.Sum, n), d.Mag + len, d.First) : (n, len, n);
            }
        var keyOf = new List<ClusterKey[]>(source.SubMeshes.Count);
        for (int si = 0; si < source.SubMeshes.Count; si++)
        {
            var s = source.SubMeshes[si];
            var keys = new ClusterKey[s.Positions.Count];
            for (int v = 0; v < s.Positions.Count; v++)
            {
                var k = CellOf(s.Positions[v]);
                var d = cellDir[k];
                float sumLen = MathF.Sqrt(d.Sum.X * d.Sum.X + d.Sum.Y * d.Sum.Y + d.Sum.Z * d.Sum.Z);
                var reference = sumLen > 0.3f * d.Mag ? d.Sum : d.First;
                var n = facing[si][v];
                int side = n.X * reference.X + n.Y * reference.Y + n.Z * reference.Z < 0 ? 1 : 0;
                keys[v] = new ClusterKey(k.Item1, k.Item2, k.Item3, side);
            }
            keyOf.Add(keys);
        }

        // Where each cluster's vertex goes: the point nearest the planes of every triangle touching it.
        var quadrics = new Dictionary<ClusterKey, ClusterQuadric>();
        ClusterQuadric Q(ClusterKey k) => quadrics.TryGetValue(k, out var q) ? q : quadrics[k] = new ClusterQuadric();
        for (int si = 0; si < source.SubMeshes.Count; si++)
        {
            var s = source.SubMeshes[si];
            var keys = keyOf[si];
            for (int v = 0; v < s.Positions.Count; v++)
            {
                var q = Q(keys[v]);
                q.Mx += s.Positions[v].X; q.My += s.Positions[v].Y; q.Mz += s.Positions[v].Z; q.N++;
            }
            foreach (var (a, b, c) in s.Faces)
            {
                var cr = Cross(s.Positions[a], s.Positions[b], s.Positions[c]);
                float len = MathF.Sqrt(cr.X * cr.X + cr.Y * cr.Y + cr.Z * cr.Z);
                if (len < 1e-20f) continue;
                var n = new Vec3(cr.X / len, cr.Y / len, cr.Z / len);
                float d = -(n.X * s.Positions[a].X + n.Y * s.Positions[a].Y + n.Z * s.Positions[a].Z);
                float w = len * 0.5f;
                Q(keys[a]).AddPlane(n, d, w);
                if (keys[b] != keys[a]) Q(keys[b]).AddPlane(n, d, w);
                if (keys[c] != keys[a] && keys[c] != keys[b]) Q(keys[c]).AddPlane(n, d, w);
            }
        }
        var cellPos = new Dictionary<ClusterKey, Vec3>(quadrics.Count);
        foreach (var (k, q) in quadrics)
            cellPos[k] = q.N == 0 ? default : q.Solve(new Vec3(origin.X + k.X * h, origin.Y + k.Y * h, origin.Z + k.Z * h), h);

        var outMesh = new ObjMesh();
        outMesh.MtlLibs.AddRange(source.MtlLibs);
        for (int si = 0; si < source.SubMeshes.Count; si++)
        {
            var s = source.SubMeshes[si];
            var island = islands[si];
            var cellOf = keyOf[si];
            bool hasNormals = s.Normals.Count == s.Positions.Count, hasUvs = s.Uvs.Count == s.Positions.Count;
            // (cluster, island) -> the member whose position is nearest the cluster's position, and its output index
            var rep = new Dictionary<(ClusterKey Cell, int Island), (int Vertex, float D)>();
            for (int v = 0; v < s.Positions.Count; v++)
            {
                var k = cellOf[v];
                var cp = cellPos[k];
                var p = s.Positions[v];
                float d = (p.X - cp.X) * (p.X - cp.X) + (p.Y - cp.Y) * (p.Y - cp.Y) + (p.Z - cp.Z) * (p.Z - cp.Z);
                var key = (k, island[v]);
                if (!rep.TryGetValue(key, out var cur) || d < cur.D) rep[key] = (v, d);
            }
            var sub = new ObjSubMesh { Material = s.Material, Object = s.Object };
            var index = new Dictionary<(ClusterKey, int), int>();
            int Out(int v)
            {
                var key = (cellOf[v], island[v]);
                if (index.TryGetValue(key, out int i)) return i;
                int r = rep[key].Vertex;
                i = sub.Positions.Count;
                sub.Positions.Add(cellPos[cellOf[v]]);
                if (hasNormals) sub.Normals.Add(s.Normals[r]);
                if (hasUvs) sub.Uvs.Add(s.Uvs[r]);
                index[key] = i;
                return i;
            }
            var seen = new HashSet<(int, int, int)>();
            foreach (var (a, b, c) in s.Faces)
            {
                var ka = cellOf[a]; var kb = cellOf[b]; var kc = cellOf[c];
                if (ka == kb || kb == kc || ka == kc) continue;       // two corners in one cluster: collapsed
                // A triangle the move turned over would show its back: it goes (the surface around it closes the gap).
                var n0 = Normal(s.Positions[a], s.Positions[b], s.Positions[c]);
                var n1 = Normal(cellPos[ka], cellPos[kb], cellPos[kc]);
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

    /// <summary>A triangle's (b-a)x(c-a): its normal times twice its area.</summary>
    private static Vec3 Cross(Vec3 a, Vec3 b, Vec3 c)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return new Vec3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    private static ObjSubMesh CloneWithObject(ObjSubMesh s)
    {
        var c = Clone(s);
        c.Object = s.Object;
        return c;
    }
}
