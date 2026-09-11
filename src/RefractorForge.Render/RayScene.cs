using System;
using System.Collections.Generic;
using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// A bounding-volume hierarchy over the level's triangles, for bakes that cast far more than one ray per texel.
///
/// <para><see cref="MeshOccluder"/> - a uniform grid capped at 48 cells on its longest axis, with a per-thread
/// "already tested on this ray" stamp array one int wide per triangle - is right for what it does: one sun ray
/// per texel, and a mesh-sized occluder. It is the wrong shape for ambient occlusion or path-traced bounce, which
/// want hundreds of rays per texel against the whole level: a uniform grid over a 2 km map puts thousands of
/// triangles in the cells that contain a building, and the stamp array bounds how many threads can share one
/// occluder. So this is additive - nothing that already works is moved onto it.</para>
///
/// <para>Two things this gives that the grid cannot. It is STATELESS during traversal, so any number of threads
/// share one instance with no per-thread allocation at all. And it can return the NEAREST hit with the triangle
/// it belongs to, which is what an indirect bounce needs in order to know what colour the surface it bounced off
/// was - visibility alone is not enough once light is allowed to carry colour.</para>
///
/// <para>Built with a binned surface-area-heuristic split, the standard construction: the cost of a split is
/// estimated as the surface area of each child box times the number of triangles in it, and the cheapest
/// candidate across a fixed set of bins wins. It beats a median split badly on this content, where a level is
/// mostly empty air with dense clusters of buildings.</para>
/// </summary>
public sealed class RayScene
{
    // Triangles in Moller-Trumbore form, matching MeshOccluder so the two agree about what a hit is.
    private readonly Vector3[] _a, _e1, _e2;
    private readonly Vector3[]? _albedo;          // per triangle, linear 0..1; null when nothing shades
    private readonly int[] _index;                // triangle ids, permuted so each leaf is contiguous
    private readonly Node[] _nodes;
    private readonly int _root;

    /// <summary>One node: its box, and either a child pair or a run of triangles.</summary>
    private struct Node
    {
        public Vector3 Min, Max;
        /// <summary>Leaf: first triangle in <c>_index</c>. Interior: the left child; the right child is left+1.</summary>
        public int LeftFirst;
        /// <summary>Triangles in this leaf, or 0 for an interior node.</summary>
        public int Count;
    }

    /// <summary>Where a ray met the scene.</summary>
    /// <param name="Triangle">Index into the triangle list this scene was built from.</param>
    /// <param name="Distance">Distance along the (unit) ray direction.</param>
    /// <param name="Point">The hit position.</param>
    /// <param name="Normal">The geometric normal, flipped to face the incoming ray.</param>
    /// <param name="Albedo">The surface's diffuse colour, or 0.5 grey when the scene carries none.</param>
    public readonly record struct Hit(int Triangle, float Distance, Vector3 Point, Vector3 Normal, Vector3 Albedo);

    public int TriangleCount => _a.Length;
    public int NodeCount => _nodes.Length;

    /// <summary>One BVH node as plain data: its box, and either the first child (interior, <c>Count == 0</c>; the
    /// right child is always <c>LeftFirst + 1</c>) or the first entry of its run in <see cref="Flat.Index"/>.</summary>
    public readonly record struct FlatNode(Vector3 Min, Vector3 Max, int LeftFirst, int Count);

    /// <summary>The whole structure as arrays, exactly as traversal reads it - what a GPU buffer is filled from.</summary>
    public sealed record Flat(Vector3[] A, Vector3[] E1, Vector3[] E2, Vector3[]? Albedo, int[] Index,
                              FlatNode[] Nodes, int Root);

    /// <summary>
    /// Export the BVH verbatim. Nothing is rebuilt or reordered: a traversal of these arrays visits the same nodes
    /// in the same order as <see cref="Occluded"/> and <see cref="Trace"/>, which is what lets a GPU kernel written
    /// against them be checked, bit for bit, against this class on the CPU.
    /// </summary>
    public Flat ExportFlat()
    {
        var nodes = new FlatNode[_nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
            nodes[i] = new FlatNode(_nodes[i].Min, _nodes[i].Max, _nodes[i].LeftFirst, _nodes[i].Count);
        return new Flat(_a, _e1, _e2, _albedo, _index, nodes, _root);
    }

    /// <summary>Build over a triangle soup. <paramref name="albedo"/>, when given, is one linear colour per
    /// triangle - what an indirect bounce off it should pick up.</summary>
    public static RayScene? Build(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris,
                                  IReadOnlyList<Vector3>? albedo = null)
        => tris is null || tris.Count == 0 ? null : new RayScene(tris, albedo);

    private RayScene(IReadOnlyList<(Vector3 a, Vector3 b, Vector3 c)> tris, IReadOnlyList<Vector3>? albedo)
    {
        int n = tris.Count;
        _a = new Vector3[n]; _e1 = new Vector3[n]; _e2 = new Vector3[n];
        var bmin = new Vector3[n]; var bmax = new Vector3[n]; var centroid = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var (a, b, c) = tris[i];
            _a[i] = a; _e1[i] = b - a; _e2[i] = c - a;
            bmin[i] = Vector3.Min(a, Vector3.Min(b, c));
            bmax[i] = Vector3.Max(a, Vector3.Max(b, c));
            centroid[i] = (a + b + c) / 3f;
        }
        _albedo = albedo is null ? null : ToArray(albedo, n);

        _index = new int[n];
        for (int i = 0; i < n; i++) _index[i] = i;

        // At most 2n-1 nodes: one root plus a contiguous pair per interior node.
        _nodes = new Node[Math.Max(2, 2 * n)];
        int used = 1;
        _root = 0;
        Build(_root, 0, n, bmin, bmax, centroid, ref used, 0);
        Array.Resize(ref _nodes, Math.Max(1, used));
    }

    private static Vector3[] ToArray(IReadOnlyList<Vector3> src, int n)
    {
        var dst = new Vector3[n];
        for (int i = 0; i < n && i < src.Count; i++) dst[i] = src[i];
        return dst;
    }

    private const int LeafSize = 4;      // below this a split cannot pay for the extra traversal
    private const int Bins = 12;         // SAH candidates per axis; 12 is the usual sweet spot
    private const int MaxDepth = 64;

    /// <summary>
    /// Fill node <paramref name="self"/> (already allocated) from triangles <c>_index[first .. first+count)</c>.
    ///
    /// <para>The two children are reserved as a CONTIGUOUS PAIR before either is built, so the right child is
    /// always <c>left + 1</c> and a node needs only one child index. Allocating each node's index on entry
    /// instead - the obvious way to write this - is wrong: after a left SUBTREE the right child lands far past
    /// left+1, and traversal silently descends into an unrelated node. That bug missed about 6% of rays and was
    /// caught only by differential-testing against <see cref="MeshOccluder"/>.</para>
    /// </summary>
    private void Build(int self, int first, int count, Vector3[] bmin, Vector3[] bmax, Vector3[] centroid, ref int used, int depth)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        var clo = new Vector3(float.MaxValue); var chi = new Vector3(float.MinValue);
        for (int i = first; i < first + count; i++)
        {
            int t = _index[i];
            lo = Vector3.Min(lo, bmin[t]); hi = Vector3.Max(hi, bmax[t]);
            clo = Vector3.Min(clo, centroid[t]); chi = Vector3.Max(chi, centroid[t]);
        }
        _nodes[self].Min = lo; _nodes[self].Max = hi;

        if (count <= LeafSize || depth >= MaxDepth)
        {
            _nodes[self].LeftFirst = first; _nodes[self].Count = count;
            return;
        }

        // Split along the axis whose CENTROIDS spread widest - splitting on the box extent instead lets one
        // long thin triangle dictate the axis for a whole cluster.
        var span = chi - clo;
        int axis = span.X > span.Y ? (span.X > span.Z ? 0 : 2) : (span.Y > span.Z ? 1 : 2);
        float spanA = Comp(span, axis);
        if (spanA < 1e-9f)
        {
            _nodes[self].LeftFirst = first; _nodes[self].Count = count;
            return;
        }

        Span<int> binCount = stackalloc int[Bins];
        var binMin = new Vector3[Bins]; var binMax = new Vector3[Bins];
        for (int i = 0; i < Bins; i++) { binMin[i] = new Vector3(float.MaxValue); binMax[i] = new Vector3(float.MinValue); }
        float scale = Bins / spanA;
        float loA = Comp(clo, axis);

        for (int i = first; i < first + count; i++)
        {
            int t = _index[i];
            int b = Math.Clamp((int)((Comp(centroid[t], axis) - loA) * scale), 0, Bins - 1);
            binCount[b]++;
            binMin[b] = Vector3.Min(binMin[b], bmin[t]);
            binMax[b] = Vector3.Max(binMax[b], bmax[t]);
        }

        // Sweep the bin boundaries, accumulating box area x triangle count from each side.
        Span<float> costLeft = stackalloc float[Bins - 1];
        Span<float> costRight = stackalloc float[Bins - 1];
        Span<int> countLeft = stackalloc int[Bins - 1];
        var acc = new Vector3(float.MaxValue); var accMax = new Vector3(float.MinValue);
        int running = 0;
        for (int i = 0; i < Bins - 1; i++)
        {
            running += binCount[i];
            if (binCount[i] > 0) { acc = Vector3.Min(acc, binMin[i]); accMax = Vector3.Max(accMax, binMax[i]); }
            countLeft[i] = running;
            costLeft[i] = running == 0 ? 0f : running * HalfArea(acc, accMax);
        }
        acc = new Vector3(float.MaxValue); accMax = new Vector3(float.MinValue);
        running = 0;
        for (int i = Bins - 1; i > 0; i--)
        {
            running += binCount[i];
            if (binCount[i] > 0) { acc = Vector3.Min(acc, binMin[i]); accMax = Vector3.Max(accMax, binMax[i]); }
            costRight[i - 1] = running == 0 ? 0f : running * HalfArea(acc, accMax);
        }

        int bestSplit = -1; float bestCost = float.MaxValue;
        for (int i = 0; i < Bins - 1; i++)
        {
            if (countLeft[i] == 0 || countLeft[i] == count) continue;
            float c = costLeft[i] + costRight[i];
            if (c < bestCost) { bestCost = c; bestSplit = i; }
        }

        // A leaf that costs less than every split is the right answer, not a failure.
        float leafCost = count * HalfArea(lo, hi);
        if (bestSplit < 0 || bestCost >= leafCost)
        {
            _nodes[self].LeftFirst = first; _nodes[self].Count = count;
            return;
        }

        // Partition in place around the chosen bin boundary.
        int mid = first, end = first + count - 1;
        while (mid <= end)
        {
            int t = _index[mid];
            int b = Math.Clamp((int)((Comp(centroid[t], axis) - loA) * scale), 0, Bins - 1);
            if (b <= bestSplit) mid++;
            else { (_index[mid], _index[end]) = (_index[end], _index[mid]); end--; }
        }
        int leftCount = mid - first;
        if (leftCount == 0 || leftCount == count)
        {
            _nodes[self].LeftFirst = first; _nodes[self].Count = count;
            return;
        }

        int left = used; used += 2;                    // reserve the pair, so right == left + 1 by construction
        _nodes[self].Count = 0;
        _nodes[self].LeftFirst = left;
        Build(left, first, leftCount, bmin, bmax, centroid, ref used, depth + 1);
        Build(left + 1, mid, count - leftCount, bmin, bmax, centroid, ref used, depth + 1);
    }

    private static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Half the surface area of a box - the SAH only ever compares these, so the factor of two cancels.</summary>
    private static float HalfArea(Vector3 lo, Vector3 hi)
    {
        var d = hi - lo;
        if (d.X < 0 || d.Y < 0 || d.Z < 0) return 0f;
        return d.X * d.Y + d.Y * d.Z + d.Z * d.X;
    }

    /// <summary>
    /// Is anything between <paramref name="origin"/> and <paramref name="maxDist"/> along <paramref name="dir"/>?
    /// Any-hit, so it stops at the first blocker. Thread-safe: nothing here writes to the scene.
    /// </summary>
    /// <param name="skip">How far along the ray to start, so the surface the point sits on is not its own
    /// occluder. The same 0.02 m the sun bake has always used.</param>
    public bool Occluded(Vector3 origin, Vector3 dir, float maxDist = float.MaxValue, float skip = 0.02f)
    {
        origin += dir * skip;
        maxDist -= skip;
        if (maxDist <= 0f) return false;
        var inv = Reciprocal(dir);

        Span<int> stack = stackalloc int[MaxDepth + 2];
        int sp = 0; int node = _root;
        while (true)
        {
            ref readonly var nd = ref _nodes[node];
            if (SlabHit(nd.Min, nd.Max, origin, inv, maxDist))
            {
                if (nd.Count > 0)
                {
                    for (int i = nd.LeftFirst; i < nd.LeftFirst + nd.Count; i++)
                    {
                        float t = HitDistance(_index[i], origin, dir);
                        if (t > 1e-4f && t <= maxDist) return true;
                    }
                }
                else
                {
                    if (sp < stack.Length) stack[sp++] = nd.LeftFirst + 1;
                    node = nd.LeftFirst;
                    continue;
                }
            }
            if (sp == 0) return false;
            node = stack[--sp];
        }
    }

    /// <summary>
    /// The NEAREST surface along the ray, with the triangle and its albedo - what a bounce needs. Thread-safe.
    /// </summary>
    public bool Trace(Vector3 origin, Vector3 dir, out Hit hit, float maxDist = float.MaxValue, float skip = 0.02f)
    {
        hit = default;
        origin += dir * skip;
        maxDist -= skip;
        if (maxDist <= 0f) return false;
        var inv = Reciprocal(dir);

        float best = maxDist; int bestTri = -1;
        Span<int> stack = stackalloc int[MaxDepth + 2];
        int sp = 0; int node = _root;
        while (true)
        {
            ref readonly var nd = ref _nodes[node];
            if (SlabHit(nd.Min, nd.Max, origin, inv, best))
            {
                if (nd.Count > 0)
                {
                    for (int i = nd.LeftFirst; i < nd.LeftFirst + nd.Count; i++)
                    {
                        int t = _index[i];
                        float d = HitDistance(t, origin, dir);
                        if (d > 1e-4f && d < best) { best = d; bestTri = t; }
                    }
                }
                else
                {
                    if (sp < stack.Length) stack[sp++] = nd.LeftFirst + 1;
                    node = nd.LeftFirst;
                    continue;
                }
            }
            if (sp == 0) break;
            node = stack[--sp];
        }
        if (bestTri < 0) return false;

        var nrm = Vector3.Cross(_e1[bestTri], _e2[bestTri]);
        float len = nrm.Length();
        nrm = len > 1e-12f ? nrm / len : Vector3.UnitY;
        if (Vector3.Dot(nrm, dir) > 0f) nrm = -nrm;          // face the ray, whichever way the mesh is wound
        hit = new Hit(bestTri, best + skip, origin + dir * best, nrm,
                      _albedo is null ? new Vector3(0.5f) : _albedo[bestTri]);
        return true;
    }

    /// <summary>Reciprocal of a direction, with exact zeros nudged so the result stays FINITE. An infinite
    /// reciprocal turns <c>(planeCoord - origin) * inv</c> into NaN whenever the origin lies exactly on that slab
    /// plane - an axis-aligned ray grazing an axis-aligned box, which this content is full of - and since every
    /// comparison against NaN is false, the box would be skipped and the hit lost.</summary>
    private static Vector3 Reciprocal(Vector3 d) => new(
        1f / (d.X != 0f ? d.X : 1e-20f),
        1f / (d.Y != 0f ? d.Y : 1e-20f),
        1f / (d.Z != 0f ? d.Z : 1e-20f));

    // Slab test.
    private static bool SlabHit(Vector3 lo, Vector3 hi, Vector3 o, Vector3 inv, float maxDist)
    {
        float t0 = (lo.X - o.X) * inv.X, t1 = (hi.X - o.X) * inv.X;
        float tmin = MathF.Min(t0, t1), tmax = MathF.Max(t0, t1);
        t0 = (lo.Y - o.Y) * inv.Y; t1 = (hi.Y - o.Y) * inv.Y;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1)); tmax = MathF.Min(tmax, MathF.Max(t0, t1));
        t0 = (lo.Z - o.Z) * inv.Z; t1 = (hi.Z - o.Z) * inv.Z;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1)); tmax = MathF.Min(tmax, MathF.Max(t0, t1));
        return tmax >= MathF.Max(tmin, 0f) && tmin <= maxDist;
    }

    // Moller-Trumbore, returning the distance along the ray or -1 for a miss. Double-sided on purpose: Refractor
    // meshes are clockwise-from-outside, and a bake must not see through the back of a wall.
    private float HitDistance(int i, Vector3 o, Vector3 d)
    {
        var pvec = Vector3.Cross(d, _e2[i]);
        float det = Vector3.Dot(_e1[i], pvec);
        if (MathF.Abs(det) < 1e-9f) return -1f;
        float inv = 1f / det;
        var tvec = o - _a[i];
        float u = Vector3.Dot(tvec, pvec) * inv;
        if (u < 0f || u > 1f) return -1f;
        var qvec = Vector3.Cross(tvec, _e1[i]);
        float v = Vector3.Dot(d, qvec) * inv;
        if (v < 0f || u + v > 1f) return -1f;
        return Vector3.Dot(_e2[i], qvec) * inv;
    }
}
