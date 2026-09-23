using System;
using System.Collections.Generic;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// Reduces a mesh to a triangle budget by quadric-error edge collapse — Garland &amp; Heckbert's measure, which
/// scores a collapse by how far it moves the surface off the planes that met at that vertex, so flat regions
/// simplify first and creases survive.
///
/// Two deliberate choices make this safe for game assets rather than merely small:
///
/// * **Half-edge collapses only.** A vertex is merged ONTO its neighbour, never onto a computed midpoint. No
///   position, UV or normal is ever invented — every vertex in the result is one the author placed. It gives up a
///   little quality against optimal-placement QEM and gives back exact attributes, which matters far more when the
///   texture is a hand-painted sheet.
/// * **Seams, borders and material edges are pinned first.** A vertex where the UV splits, where the mesh has an
///   open edge, or where two materials meet is not removed while the interior can still give triangles up - those
///   are the places where moving a vertex tears the texture or opens a hole.
/// * **Then they slide, when the budget demands it.** Models from the internet are all seams and loose shells: a
///   car's thousand separate panels are each an open border, and pinning every border and seam vertex stalls far
///   above a vehicle's triangle budget. So once the interior is spent, a seam vertex may merge onto its neighbour ALONG
///   its seam (each side keeps its own UVs), and a border vertex onto its neighbour along its border (the hole stays
///   closed), where the line runs straight on - a corner of a border or of a UV island stays, and so does every
///   vertex two materials share, and every vertex on both a seam and a border. Border edges carry extra planes in
///   their error measure, so what slides first is what changes the silhouette least.
///
/// The budget worth aiming at, measured across 719 shipped BF1942 meshes: a hero mesh is 1,100-2,200 triangles and
/// a whole multi-part vehicle 2,000-6,000. Ten times that will load; it just costs frames on the hardware these
/// games were built for.
/// </summary>
public static class MeshDecimator
{
    /// <summary>What a run did, so a caller can report it honestly — asking for 500 triangles on a mesh whose seams
    /// pin most of its vertices gives you what the constraints allow, not the number you asked for.</summary>
    public readonly record struct Result(int SourceTriangles, int Triangles, int Vertices, int Collapses);

    /// <summary>
    /// Simplify to roughly <paramref name="targetTriangles"/> triangles, returning a NEW mesh (the source is not
    /// touched). Submeshes are decimated independently — they are separate vertex arrays with separate shaders —
    /// with the budget split in proportion to their triangle counts, so one dense material does not eat a small
    /// one's triangles.
    /// </summary>
    /// <param name="weldEpsilon">Positions closer than this are one vertex for the purpose of collapsing. An
    /// exported OBJ splits a vertex per UV and per normal, so without welding a "closed" mesh looks like a pile of
    /// disconnected triangles and nothing can collapse at all.</param>
    /// <param name="slideSeams">Once the interior is spent, let seam and border vertices slide along their lines
    /// (see the class notes). Off: they stay pinned, and a seamed model stops where its seams stop it.</param>
    public static ObjMesh Decimate(ObjMesh source, int targetTriangles, float weldEpsilon = 1e-5f, bool slideSeams = true)
        => Decimate(source, targetTriangles, out _, weldEpsilon, slideSeams);

    public static ObjMesh Decimate(ObjMesh source, int targetTriangles, out Result result, float weldEpsilon = 1e-5f, bool slideSeams = true)
    {
        int total = source.TotalFaces;
        var outMesh = new ObjMesh();
        outMesh.MtlLibs.AddRange(source.MtlLibs);
        if (total == 0 || targetTriangles >= total)
        {
            foreach (var s in source.SubMeshes) outMesh.SubMeshes.Add(Clone(s));
            RecomputeBounds(outMesh);
            result = new Result(total, outMesh.TotalFaces, outMesh.TotalVertices, 0);
            return outMesh;
        }

        // A position used by more than one submesh is where two materials meet. Moving it opens a visible gap
        // between them, so it is pinned in both.
        var shared = SharedPositions(source, weldEpsilon);

        int collapses = 0;
        foreach (var s in source.SubMeshes)
        {
            // Proportional budget, but never below a tetrahedron's worth: a material reduced to nothing would
            // leave a shader bound to no geometry.
            int want = s.Faces.Count == 0 ? 0
                     : Math.Max(4, (int)Math.Round(targetTriangles * (double)s.Faces.Count / total));
            outMesh.SubMeshes.Add(DecimateSub(s, want, weldEpsilon, shared, slideSeams, ref collapses));
        }
        RecomputeBounds(outMesh);
        result = new Result(total, outMesh.TotalFaces, outMesh.TotalVertices, collapses);
        return outMesh;
    }

    // ---- one submesh -------------------------------------------------------------------------------------------

    private static ObjSubMesh DecimateSub(ObjSubMesh s, int target, float eps, HashSet<long> shared, bool slideSeams, ref int collapses)
    {
        if (s.Faces.Count <= target) return Clone(s);

        int nv = s.Positions.Count;
        var clusterOf = new int[nv];
        var clusters = new List<List<int>>();
        var byKey = new Dictionary<long, int>();
        for (int i = 0; i < nv; i++)
        {
            long key = Key(s.Positions[i], eps);
            if (!byKey.TryGetValue(key, out int c)) { c = clusters.Count; byKey[key] = c; clusters.Add(new List<int>()); }
            clusterOf[i] = c;
            clusters[c].Add(i);
        }
        int nc = clusters.Count;

        // Triangles in cluster space; a triangle whose corners weld together was already degenerate.
        var tri = new List<(int A, int B, int C, int OA, int OB, int OC)>(s.Faces.Count);
        foreach (var (a, b, c) in s.Faces)
        {
            int ca = clusterOf[a], cb = clusterOf[b], cc = clusterOf[c];
            if (ca == cb || cb == cc || ca == cc) continue;
            tri.Add((ca, cb, cc, a, b, c));
        }
        if (tri.Count <= target) return Clone(s);

        // Pin the places a collapse would damage: a UV split, an open border, a material boundary. `locked` is the
        // subset that never moves, even when seams are allowed to slide: a material boundary, a non-manifold edge.
        var pinned = new bool[nc];
        var locked = new bool[nc];
        for (int c = 0; c < nc; c++)
        {
            var members = clusters[c];
            if (shared.Contains(Key(s.Positions[members[0]], eps))) { pinned[c] = locked[c] = true; continue; }
            var uv0 = s.Uvs[members[0]];
            foreach (var m in members)
                if (MathF.Abs(s.Uvs[m].U - uv0.U) > 1e-5f || MathF.Abs(s.Uvs[m].V - uv0.V) > 1e-5f) { pinned[c] = true; break; }
        }
        var edgeUse = new Dictionary<(int, int), int>();
        var edgeTri = new Dictionary<(int, int), int>();
        for (int i = 0; i < tri.Count; i++)
            foreach (var e in Edges(tri[i]))
            {
                var k = e.A < e.B ? (e.A, e.B) : (e.B, e.A);
                edgeUse[k] = edgeUse.GetValueOrDefault(k) + 1;
                edgeTri.TryAdd(k, i);
            }
        foreach (var kv in edgeUse)
        {
            if (kv.Value == 1) { pinned[kv.Key.Item1] = true; pinned[kv.Key.Item2] = true; }   // open border
            if (kv.Value > 2) { pinned[kv.Key.Item1] = locked[kv.Key.Item1] = true; pinned[kv.Key.Item2] = locked[kv.Key.Item2] = true; }
        }

        // Face quadrics summed onto their corners. Weighting by area is what stops a swarm of tiny triangles from
        // out-voting the one big plane they sit on.
        var q = new Quadric[nc];
        var triList = new List<int>[nc];
        for (int c = 0; c < nc; c++) triList[c] = new List<int>();
        var pos = new Vec3[nc];
        for (int c = 0; c < nc; c++) pos[c] = s.Positions[clusters[c][0]];
        for (int i = 0; i < tri.Count; i++)
        {
            var t = tri[i];
            var qq = Quadric.FromTriangle(pos[t.A], pos[t.B], pos[t.C]);
            q[t.A] = q[t.A].Add(qq); q[t.B] = q[t.B].Add(qq); q[t.C] = q[t.C].Add(qq);
            triList[t.A].Add(i); triList[t.B].Add(i); triList[t.C].Add(i);
        }
        // An open border also gets the plane that stands on it, square to its face: sliding a border vertex along a
        // straight border costs nothing, pulling it off the line costs as much as the face planes would.
        foreach (var kv in edgeUse)
        {
            if (kv.Value != 1) continue;
            var (a0, b0) = kv.Key;
            var t = tri[edgeTri[kv.Key]];
            var qb = Quadric.BorderPlane(pos[a0], pos[b0], Normal(pos[t.A], pos[t.B], pos[t.C]), BorderWeight);
            q[a0] = q[a0].Add(qb); q[b0] = q[b0].Add(qb);
        }

        var parent = new int[nc];
        for (int i = 0; i < nc; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        // A lazy heap: an entry is re-checked against the live quadrics when it comes out, and dropped if either
        // end has since moved. Cheaper than keeping a decrease-key structure honest.
        var heap = new PriorityQueue<(int From, int To), float>();
        bool sliding = false;                         // the second phase: seams and borders may slide
        bool Movable(int v) => sliding ? !locked[v] : !pinned[v];
        void Offer(int a, int b)
        {
            if (a == b) return;
            if (Movable(a)) heap.Enqueue((a, b), q[a].Add(q[b]).Evaluate(pos[b]));
            if (Movable(b)) heap.Enqueue((b, a), q[a].Add(q[b]).Evaluate(pos[a]));
        }
        foreach (var kv in edgeUse) Offer(kv.Key.Item1, kv.Key.Item2);

        var dead = new bool[tri.Count];
        int live = tri.Count;
        var vmap = new int[nv];                       // original vertex -> surviving cluster
        for (int i = 0; i < nv; i++) vmap[i] = clusterOf[i];

        // The split vertex of a cluster whose UV is closest to a corner's own: how a corner that moved keeps to its
        // side of a seam, both while deciding what may slide and when the mesh is rebuilt.
        int Pick(int origCorner)
        {
            int target2 = Find(clusterOf[origCorner]);
            var members = clusters[target2];
            if (members.Count == 1) return members[0];
            var uv = s.Uvs[origCorner];
            int best = members[0]; float bestD = float.MaxValue;
            foreach (int m in members)
            {
                float du = s.Uvs[m].U - uv.U, dv = s.Uvs[m].V - uv.V;
                float d = du * du + dv * dv;
                if (d < bestD) { bestD = d; best = m; }
            }
            return best;
        }
        int CornerAt(int ti, int v)
        {
            var t = tri[ti];
            return Find(t.A) == v ? t.OA : Find(t.B) == v ? t.OB : t.OC;
        }

        // May seam/border vertex `from` slide onto `to`? Only along a line of its own kind that runs straight on
        // through it: exactly two border edges (and no seam), or exactly two seam edges (and no border), `to` at the
        // far end of one of them.
        bool MaySlide(int from, int to)
        {
            if (!pinned[from]) return true;
            if (locked[from]) return false;
            var around = new Dictionary<int, List<int>>();          // neighbour -> live triangles on that edge
            foreach (int ti in triList[from])
            {
                if (dead[ti]) continue;
                var t = tri[ti];
                foreach (int n in new[] { Find(t.A), Find(t.B), Find(t.C) })
                    if (n != from)
                    {
                        if (!around.TryGetValue(n, out var l)) around[n] = l = new List<int>();
                        if (!l.Contains(ti)) l.Add(ti);
                    }
            }
            var border = new List<int>();
            var seam = new List<int>();
            foreach (var (n, ts) in around)
            {
                if (ts.Count == 1) border.Add(n);
                else if (ts.Count == 2 && (Pick(CornerAt(ts[0], from)) != Pick(CornerAt(ts[1], from))
                                          || Pick(CornerAt(ts[0], n)) != Pick(CornerAt(ts[1], n)))) seam.Add(n);
                else if (ts.Count > 2) return false;
            }
            List<int> line;
            if (border.Count == 2 && seam.Count == 0) line = border;
            else if (seam.Count == 2 && border.Count == 0) line = seam;
            else return false;
            if (!line.Contains(to)) return false;
            // Straight on: the two edges leave in opposite directions (within about 25 degrees).
            var d1 = Direction(pos[from], pos[line[0]]);
            var d2 = Direction(pos[line[1]], pos[from]);
            return d1.X * d2.X + d1.Y * d2.Y + d1.Z * d2.Z > 0.9f;
        }

        while (live > target)
        {
            if (heap.Count == 0)
            {
                // The interior is spent. Seams and borders may slide from here on.
                if (sliding || !slideSeams) break;
                sliding = true;
                for (int i = 0; i < tri.Count; i++)
                {
                    if (dead[i]) continue;
                    var t = tri[i];
                    int a = Find(t.A), b = Find(t.B), c = Find(t.C);
                    Offer(a, b); Offer(b, c); Offer(c, a);
                }
                if (heap.Count == 0) break;
                continue;
            }
            var (from, to) = heap.Dequeue();
            if (Find(from) != from || Find(to) != to || from == to || !Movable(from)) continue;
            if (sliding && !MaySlide(from, to)) continue;

            // Refuse a collapse that folds a triangle over on itself. A LOD that turns inside out reads as a hole.
            if (WouldFlip(from, to)) continue;

            parent[from] = to;
            q[to] = q[to].Add(q[from]);
            collapses++;

            foreach (int ti in triList[from])
            {
                if (dead[ti]) continue;
                var t = tri[ti];
                int a = Find(t.A), b = Find(t.B), c = Find(t.C);
                if (a == b || b == c || a == c) { dead[ti] = true; live--; }
                else triList[to].Add(ti);
            }
            triList[from].Clear();

            // The survivor's new neighbours need fresh costs.
            foreach (int ti in triList[to])
            {
                if (dead[ti]) continue;
                var t = tri[ti];
                int a = Find(t.A), b = Find(t.B), c = Find(t.C);
                if (a == to || b == to || c == to)
                {
                    if (a != to) Offer(to, a);
                    if (b != to) Offer(to, b);
                    if (c != to) Offer(to, c);
                }
            }
        }

        bool WouldFlip(int from, int to)
        {
            foreach (int ti in triList[from])
            {
                if (dead[ti]) continue;
                var t = tri[ti];
                int a = Find(t.A), b = Find(t.B), c = Find(t.C);
                if (a == to || b == to || c == to) continue;              // this one disappears in the collapse
                var before = Normal(pos[a], pos[b], pos[c]);
                int na = a == from ? to : a, nb = b == from ? to : b, ncc = c == from ? to : c;
                if (na == nb || nb == ncc || na == ncc) continue;
                var after = Normal(pos[na], pos[nb], pos[ncc]);
                if (before.X * after.X + before.Y * after.Y + before.Z * after.Z < 0f) return true;
            }
            return false;
        }

        // Rebuild. Each surviving corner picks, from its target cluster, the split vertex whose UV is closest to
        // the one it had (Pick) — which is the identity for anything that never moved, and the right side of the seam
        // for anything that collapsed onto a seam vertex or slid along a seam.
        var outSub = new ObjSubMesh { Material = s.Material };
        var emitted = new Dictionary<int, int>();
        int Emit(int orig)
        {
            if (emitted.TryGetValue(orig, out int idx)) return idx;
            idx = outSub.Positions.Count;
            outSub.Positions.Add(s.Positions[orig]);
            outSub.Normals.Add(s.Normals[orig]);
            outSub.Uvs.Add(s.Uvs[orig]);
            emitted[orig] = idx;
            return idx;
        }
        for (int i = 0; i < tri.Count; i++)
        {
            if (dead[i]) continue;
            var t = tri[i];
            int a = Find(t.A), b = Find(t.B), c = Find(t.C);
            if (a == b || b == c || a == c) continue;
            outSub.Faces.Add((Emit(Pick(t.OA)), Emit(Pick(t.OB)), Emit(Pick(t.OC))));
        }
        return outSub.Faces.Count > 0 ? outSub : Clone(s);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static IEnumerable<(int A, int B)> Edges((int A, int B, int C, int OA, int OB, int OC) t)
    {
        yield return (t.A, t.B);
        yield return (t.B, t.C);
        yield return (t.C, t.A);
    }

    private static HashSet<long> SharedPositions(ObjMesh mesh, float eps)
    {
        var seenIn = new Dictionary<long, int>();
        var shared = new HashSet<long>();
        for (int si = 0; si < mesh.SubMeshes.Count; si++)
            foreach (var p in mesh.SubMeshes[si].Positions)
            {
                long k = Key(p, eps);
                if (seenIn.TryGetValue(k, out int owner)) { if (owner != si) shared.Add(k); }
                else seenIn[k] = si;
            }
        return shared;
    }

    private static long Key(Vec3 p, float eps)
    {
        float q = eps > 0f ? eps : 1e-5f;
        long x = (long)MathF.Round(p.X / q), y = (long)MathF.Round(p.Y / q), z = (long)MathF.Round(p.Z / q);
        return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
    }

    /// <summary>How much a border's own planes weigh against the face planes: ten times, as in meshoptimizer.</summary>
    private const double BorderWeight = 10.0;

    private static Vec3 Direction(Vec3 from, Vec3 to)
    {
        float x = to.X - from.X, y = to.Y - from.Y, z = to.Z - from.Z;
        float len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-12f ? new Vec3(x / len, y / len, z / len) : new Vec3(0, 0, 0);
    }

    private static Vec3 Normal(Vec3 a, Vec3 b, Vec3 c)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        float wx = c.X - a.X, wy = c.Y - a.Y, wz = c.Z - a.Z;
        return new Vec3(uy * wz - uz * wy, uz * wx - ux * wz, ux * wy - uy * wx);
    }

    private static ObjSubMesh Clone(ObjSubMesh s) => new()
    {
        Material = s.Material,
        Positions = new List<Vec3>(s.Positions),
        Normals = new List<Vec3>(s.Normals),
        Uvs = new List<(float, float)>(s.Uvs),
        Faces = new List<(int, int, int)>(s.Faces),
    };

    private static void RecomputeBounds(ObjMesh m)
    {
        if (m.TotalVertices == 0) { Array.Clear(m.BoundingBox, 0, 6); return; }
        float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
        float maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
        foreach (var s in m.SubMeshes)
            foreach (var p in s.Positions)
            {
                minx = MathF.Min(minx, p.X); miny = MathF.Min(miny, p.Y); minz = MathF.Min(minz, p.Z);
                maxx = MathF.Max(maxx, p.X); maxy = MathF.Max(maxy, p.Y); maxz = MathF.Max(maxz, p.Z);
            }
        m.BoundingBox[0] = minx; m.BoundingBox[1] = miny; m.BoundingBox[2] = minz;
        m.BoundingBox[3] = maxx; m.BoundingBox[4] = maxy; m.BoundingBox[5] = maxz;
    }

    /// <summary>A symmetric 4x4 error quadric, stored as its 10 distinct entries. Evaluating it at a point gives
    /// the summed squared distance to the planes it was built from — the whole of Garland &amp; Heckbert's
    /// measure.</summary>
    private readonly record struct Quadric(double A, double B, double C, double D,
                                           double E, double F, double G, double H, double I, double J)
    {
        public static Quadric FromTriangle(Vec3 p0, Vec3 p1, Vec3 p2)
        {
            var n = Normal(p0, p1, p2);
            double len = Math.Sqrt((double)n.X * n.X + (double)n.Y * n.Y + (double)n.Z * n.Z);
            if (len < 1e-20) return default;
            // Area-weight the plane so a fan of slivers cannot out-vote the surface they lie on. The cross
            // product's length is twice the area, which is exactly the weight we want before normalising.
            double w = len * 0.5;
            double a = n.X / len, b = n.Y / len, c = n.Z / len;
            double d = -(a * p0.X + b * p0.Y + c * p0.Z);
            return new Quadric(a * a * w, a * b * w, a * c * w, a * d * w,
                               b * b * w, b * c * w, b * d * w,
                               c * c * w, c * d * w, d * d * w);
        }

        /// <summary>The plane through the border edge a-b that stands square to its face (normal <paramref name="face"/>),
        /// weighted by the edge's length squared - the size of the face planes it is measured against.</summary>
        public static Quadric BorderPlane(Vec3 a, Vec3 b, Vec3 face, double weight)
        {
            double ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
            double nx = ey * face.Z - ez * face.Y, ny = ez * face.X - ex * face.Z, nz = ex * face.Y - ey * face.X;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-20) return default;
            nx /= len; ny /= len; nz /= len;
            double d = -(nx * a.X + ny * a.Y + nz * a.Z);
            double w = weight * (ex * ex + ey * ey + ez * ez);
            return new Quadric(nx * nx * w, nx * ny * w, nx * nz * w, nx * d * w,
                               ny * ny * w, ny * nz * w, ny * d * w,
                               nz * nz * w, nz * d * w, d * d * w);
        }

        public Quadric Add(Quadric o) => new(A + o.A, B + o.B, C + o.C, D + o.D, E + o.E, F + o.F,
                                             G + o.G, H + o.H, I + o.I, J + o.J);

        public float Evaluate(Vec3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            double v = A * x * x + 2 * B * x * y + 2 * C * x * z + 2 * D * x
                     + E * y * y + 2 * F * y * z + 2 * G * y
                     + H * z * z + 2 * I * z + J;
            return v > 0 ? (float)v : 0f;
        }
    }
}
