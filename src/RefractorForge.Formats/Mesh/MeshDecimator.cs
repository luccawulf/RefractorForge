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
public static partial class MeshDecimator
{
    /// <summary>What a run did, so a caller can report it honestly — asking for 500 triangles on a mesh whose seams
    /// pin most of its vertices gives you what the constraints allow, not the number you asked for.</summary>
    public readonly record struct Result(int SourceTriangles, int Triangles, int Vertices, int Collapses);

    /// <summary>
    /// Simplify to roughly <paramref name="targetTriangles"/> triangles, returning a NEW mesh (the source is not
    /// touched). Submeshes keep their own vertex arrays and shaders, but the collapses of all of them are taken in ONE
    /// order, cheapest first: the budget goes where it costs the shape least, whichever material that is. Splitting it
    /// in proportion to each material's triangle count cut a downloaded car's smooth body panels fourteenfold (they were
    /// what could still give) while its bolts and badges, pinned by their borders, kept the total high anyway - and the
    /// panels came out as shards.
    /// </summary>
    /// <param name="weldEpsilon">Positions closer than this are one vertex for the purpose of collapsing. An
    /// exported OBJ splits a vertex per UV and per normal, so without welding a "closed" mesh looks like a pile of
    /// disconnected triangles and nothing can collapse at all.</param>
    /// <param name="slideSeams">Once the interior is spent, let seam and border vertices slide along their lines
    /// (see the class notes). Off: they stay pinned, and a seamed model stops where its seams stop it.</param>
    /// <param name="importance">How much each submesh's shape counts, 0 to 1 (1 when null): its collapses cost that
    /// share of what they would. A part mostly hidden inside the model (<see cref="MeshVisibility.Exposure"/>) gives its
    /// triangles up before the ones anyone sees.</param>
    public static ObjMesh Decimate(ObjMesh source, int targetTriangles, float weldEpsilon = 1e-5f, bool slideSeams = true,
                                  Func<ObjSubMesh, float>? importance = null)
        => Decimate(source, targetTriangles, out _, weldEpsilon, slideSeams, importance);

    public static ObjMesh Decimate(ObjMesh source, int targetTriangles, out Result result, float weldEpsilon = 1e-5f, bool slideSeams = true,
                                   Func<ObjSubMesh, float>? importance = null)
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
        var runs = source.SubMeshes.Select(s => new SubRun(s, weldEpsilon, shared, importance?.Invoke(s) ?? 1f)).ToList();

        // A lazy heap over every submesh: an entry is re-checked against the live quadrics when it comes out, and
        // dropped if either end has since moved. Cheaper than keeping a decrease-key structure honest. Dropping one
        // whose far end merged loses nothing: every collapse re-offers each edge of the survivor, so the pair it now
        // stands for is already queued at its fresh cost.
        var heap = new PriorityQueue<(int Sub, int From, int To), float>();
        for (int i = 0; i < runs.Count; i++)
        {
            int si = i;
            runs[i].Offered = (a, b, cost) => heap.Enqueue((si, a, b), cost);
            runs[i].OfferAll();
        }
        int live = runs.Sum(r => r.Live);
        int collapses = 0;
        bool sliding = false;
        while (live > targetTriangles)
        {
            if (heap.Count == 0)
            {
                // The interior is spent everywhere. Seams and borders may slide from here on.
                if (sliding || !slideSeams) break;
                sliding = true;
                foreach (var r in runs) { r.Sliding = true; r.OfferAll(); }
                if (heap.Count == 0) break;
                continue;
            }
            var (sub, from, to) = heap.Dequeue();
            var run = runs[sub];
            int before = run.Live;
            if (!run.TryCollapse(from, to)) continue;
            collapses++;
            live -= before - run.Live;
        }

        foreach (var r in runs) outMesh.SubMeshes.Add(r.Rebuild());
        RecomputeBounds(outMesh);
        result = new Result(total, outMesh.TotalFaces, outMesh.TotalVertices, collapses);
        return outMesh;
    }

    // ---- one submesh -------------------------------------------------------------------------------------------

    /// <summary>One submesh being simplified: its welded vertices (clusters), its triangles in cluster space, what is
    /// pinned, the quadrics - and the collapses the shared queue hands it.</summary>
    private sealed class SubRun
    {
        /// <summary>Fewer triangles than this a material is never taken to: a material reduced to nothing would leave a
        /// shader bound to no geometry.</summary>
        private const int Floor = 4;

        private readonly ObjSubMesh s;
        private readonly int[] clusterOf;
        private readonly List<List<int>> clusters = new();
        private readonly List<(int A, int B, int C, int OA, int OB, int OC)> tri = new();
        private readonly bool[] pinned, locked, dead;
        private readonly Quadric[] q;
        private readonly List<int>[] triList;
        private readonly Vec3[] pos;
        private readonly int[] parent;
        private readonly Dictionary<(int, int), int> edgeUse = new();
        private readonly bool passThrough;

        /// <summary>Where this run's candidate collapses go (the shared queue).</summary>
        public Action<int, int, float> Offered { get; set; } = (_, _, _) => { };
        /// <summary>The second phase: seams and borders may slide.</summary>
        public bool Sliding { get; set; }
        public int Live { get; private set; }

        public SubRun(ObjSubMesh sub, float eps, HashSet<PositionKey> shared, float importance = 1f)
        {
            s = sub;
            int nv = s.Positions.Count;
            clusterOf = new int[nv];
            var byKey = new Dictionary<PositionKey, int>();
            for (int i = 0; i < nv; i++)
            {
                var key = Key(s.Positions[i], eps);
                if (!byKey.TryGetValue(key, out int c)) { c = clusters.Count; byKey[key] = c; clusters.Add(new List<int>()); }
                clusterOf[i] = c;
                clusters[c].Add(i);
            }
            int nc = clusters.Count;

            // Triangles in cluster space; a triangle whose corners weld together was already degenerate.
            foreach (var (a, b, c) in s.Faces)
            {
                int ca = clusterOf[a], cb = clusterOf[b], cc = clusterOf[c];
                if (ca == cb || cb == cc || ca == cc) continue;
                tri.Add((ca, cb, cc, a, b, c));
            }
            dead = new bool[tri.Count];
            Live = tri.Count;
            pinned = new bool[nc];
            locked = new bool[nc];
            q = new Quadric[nc];
            triList = new List<int>[nc];
            pos = new Vec3[nc];
            parent = new int[nc];
            for (int i = 0; i < nc; i++) parent[i] = i;
            // A submesh already at the floor is written back as it was, untouched.
            passThrough = tri.Count <= Floor;
            if (passThrough) { Live = s.Faces.Count; return; }

            // Pin the places a collapse would damage: a UV split, an open border, a material boundary. `locked` is the
            // subset that never moves, even when seams are allowed to slide: a material boundary, a non-manifold edge.
            for (int c = 0; c < nc; c++)
            {
                var members = clusters[c];
                if (shared.Contains(Key(s.Positions[members[0]], eps))) { pinned[c] = locked[c] = true; continue; }
                var uv0 = s.Uvs[members[0]];
                foreach (var m in members)
                    if (MathF.Abs(s.Uvs[m].U - uv0.U) > 1e-5f || MathF.Abs(s.Uvs[m].V - uv0.V) > 1e-5f) { pinned[c] = true; break; }
            }
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
            // out-voting the one big plane they sit on - and what makes costs in one material comparable with another's.
            for (int c = 0; c < nc; c++) triList[c] = new List<int>();
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
            // A part that counts for less costs less to change: its collapses come out of the queue sooner.
            double weight = Math.Clamp(float.IsFinite(importance) ? importance : 1f, 1e-4f, 1f);
            if (weight < 1.0)
                for (int c = 0; c < nc; c++) q[c] = q[c].Scale(weight);
        }

        private int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        private bool Movable(int v) => Sliding ? !locked[v] : !pinned[v];

        private void Offer(int a, int b)
        {
            if (a == b) return;
            if (Movable(a)) Offered(a, b, q[a].Add(q[b]).Evaluate(pos[b]));
            if (Movable(b)) Offered(b, a, q[a].Add(q[b]).Evaluate(pos[a]));
        }

        /// <summary>Every live edge, at its cost now: the start, and the start of the sliding phase.</summary>
        public void OfferAll()
        {
            if (passThrough) return;
            if (!Sliding)
            {
                foreach (var kv in edgeUse) Offer(kv.Key.Item1, kv.Key.Item2);
                return;
            }
            for (int i = 0; i < tri.Count; i++)
            {
                if (dead[i]) continue;
                var t = tri[i];
                int a = Find(t.A), b = Find(t.B), c = Find(t.C);
                Offer(a, b); Offer(b, c); Offer(c, a);
            }
        }

        // The split vertex of a cluster whose UV is closest to a corner's own: how a corner that moved keeps to its
        // side of a seam, both while deciding what may slide and when the mesh is rebuilt.
        private int Pick(int origCorner)
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

        private int CornerAt(int ti, int v)
        {
            var t = tri[ti];
            return Find(t.A) == v ? t.OA : Find(t.B) == v ? t.OB : t.OC;
        }

        // May seam/border vertex `from` slide onto `to`? Only along a line of its own kind that runs straight on
        // through it: exactly two border edges (and no seam), or exactly two seam edges (and no border), `to` at the
        // far end of one of them.
        private bool MaySlide(int from, int to)
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

        // Refuse a collapse that folds a triangle over on itself. A LOD that turns inside out reads as a hole.
        private bool WouldFlip(int from, int to)
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
                // Not over, and not far off it either: a face swung past MaxTurn folds a curved panel into a shard -
                // the smooth car body a download is mostly made of was cut into facets that way.
                float dot = before.X * after.X + before.Y * after.Y + before.Z * after.Z;
                if (dot < 0f) return true;
                float lb = before.X * before.X + before.Y * before.Y + before.Z * before.Z, la = after.X * after.X + after.Y * after.Y + after.Z * after.Z;
                if (dot * dot < MaxTurnCos * MaxTurnCos * lb * la) return true;
            }
            return false;
        }

        /// <summary>Merge <paramref name="from"/> onto <paramref name="to"/> if it is still a live, allowed collapse that
        /// folds nothing over and leaves the material above its floor; false when it was dropped.</summary>
        public bool TryCollapse(int from, int to)
        {
            if (passThrough || Live <= Floor) return false;
            if (Find(from) != from || Find(to) != to || from == to || !Movable(from)) return false;
            if (Sliding && !MaySlide(from, to)) return false;
            if (WouldFlip(from, to)) return false;

            parent[from] = to;
            q[to] = q[to].Add(q[from]);
            foreach (int ti in triList[from])
            {
                if (dead[ti]) continue;
                var t = tri[ti];
                int a = Find(t.A), b = Find(t.B), c = Find(t.C);
                if (a == b || b == c || a == c) { dead[ti] = true; Live--; }
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
            return true;
        }

        /// <summary>The submesh as it stands. Each surviving corner picks, from its target cluster, the split vertex whose
        /// UV is closest to the one it had (Pick) — which is the identity for anything that never moved, and the right
        /// side of the seam for anything that collapsed onto a seam vertex or slid along a seam.</summary>
        public ObjSubMesh Rebuild()
        {
            if (passThrough || Live == tri.Count) return Clone(s);
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
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static IEnumerable<(int A, int B)> Edges((int A, int B, int C, int OA, int OB, int OC) t)
    {
        yield return (t.A, t.B);
        yield return (t.B, t.C);
        yield return (t.C, t.A);
    }

    private static HashSet<PositionKey> SharedPositions(ObjMesh mesh, float eps)
    {
        var seenIn = new Dictionary<PositionKey, int>();
        var shared = new HashSet<PositionKey>();
        for (int si = 0; si < mesh.SubMeshes.Count; si++)
            foreach (var p in mesh.SubMeshes[si].Positions)
            {
                var k = Key(p, eps);
                if (seenIn.TryGetValue(k, out int owner)) { if (owner != si) shared.Add(k); }
                else seenIn[k] = si;
            }
        return shared;
    }

    /// <summary>A position snapped to the weld grid: the three cell indices themselves, compared exactly. It used to be
    /// the three folded into one long by an XOR hash and that hash taken as the identity - and on a symmetric model,
    /// or any centred on the origin, different corners folded to the same long (16-18% of distinct positions on a tyre
    /// or a torus in its own frame), which welded far-apart vertices into one and stalled the whole run.</summary>
    private readonly record struct PositionKey(long X, long Y, long Z);

    private static PositionKey Key(Vec3 p, float eps)
    {
        float q = eps > 0f ? eps : 1e-5f;
        return new PositionKey((long)MathF.Round(p.X / q), (long)MathF.Round(p.Y / q), (long)MathF.Round(p.Z / q));
    }

    /// <summary>How far a collapse may swing a face that stays: the cosine of the largest turn (about 60 degrees).</summary>
    internal static float MaxTurnCos = 0.5f;

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

        public Quadric Scale(double k) => new(A * k, B * k, C * k, D * k, E * k, F * k, G * k, H * k, I * k, J * k);

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
