using System;
using System.Collections.Generic;
using System.Numerics;
using RefractorForge.Formats.Rfa;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// Generates the second UV set - the lightmap unwrap - for a StandardMesh that has none.
///
/// <para>Two thirds of BfVietnam's meshes carry no lightmap channel at all and another fifth carry the slot with
/// (0,0) on every vertex, so only about one mesh in nine can be baked today. The engine cannot help: RE of
/// <c>BfVietnam_DEBUG.exe</c> established that its lightmap generator loads a per-texel sample table from
/// <c>StandardMesh/&lt;mesh&gt;.samples</c>, a file DICE's exporter produced and which ships in no archive, and
/// that there is no runtime UV generation anywhere. If those objects are to take light, the unwrap has to be
/// made here.</para>
///
/// <para><b>Fold-freedom is arithmetic, not a test.</b> Every triangle is assigned to one of six buckets by the
/// dominant axis of its normal, so every member of a bucket satisfies
/// <c>dot(n, axis) &gt;= 1/sqrt(3) = 0.577</c> BY CONSTRUCTION. A chart's signed projected area onto that axis is
/// therefore <c>area * dot(n, axis) &gt; 0</c> for every triangle: they all project the same way round, and an
/// inverted triangle is impossible rather than merely unobserved. That is what buys the right to use plain
/// orthographic projection and skip a least-squares conformal solver entirely - hand-written sparse linear
/// algebra whose failure mode is a fold the verifier has to catch afterwards.</para>
///
/// <para><b>The atlas is per MESH, not per material.</b> <c>MeshLibrary</c> concatenates every LOD0 material into
/// one vertex array and the baker rasterises all of it into a single <c>owner[]</c> grid, so charts must be
/// packed across all materials together. Packing each material into its own square would overlap them and each
/// would take the others' light.</para>
/// </summary>
public static class LightmapUnwrapper
{
    public enum UnwrapStatus
    {
        Ok,
        /// <summary>The mesh already carries a real unwrap; leave DICE's work alone.</summary>
        AlreadyUnwrapped,
        /// <summary>A triangle STRIP cannot be renumbered per chart: one index slot is shared by three
        /// consecutive triangles, so it cannot hold two different chart UVs. Measured cost of refusing: 6
        /// BfVietnam meshes and 52 BF1942 ones, every one a soldier body or head - no static prop anywhere.</summary>
        UnsupportedTopology,
        /// <summary>Nothing survived the degenerate filter.</summary>
        NoUsableGeometry,
        /// <summary>Splitting would cross the 65,535 u16 index ceiling.</summary>
        IndexCeiling,
        /// <summary>Even at the 1024 ceiling the texel density would be useless. Ship no map rather than a bad one.</summary>
        AtlasTooSmall,
        /// <summary>More charts than the budget allows.</summary>
        ChartBudget,
    }

    public sealed record UnwrapOptions
    {
        /// <summary>Uncovered cells around each chart. DERIVED, not chosen: the baker's Dilate is three passes of
        /// a 3x3 average writing only into uncovered texels, so a chart's colour travels exactly 3 texels. Two
        /// charts need 3 + 3 between their covered sets before either can average the other in, plus one for the
        /// in-game bilinear tap. Padding each chart by 4 guarantees 8.</summary>
        public int GutterCells { get; init; } = 4;
        public int MinBakeSize { get; init; } = 64;
        /// <summary>The size the unwrap should try to fit inside - the draft tier's own cap. Exceeding it is
        /// allowed but reported, because it forces that mesh to bake larger than the tier asked for.</summary>
        public int PreferredMaxBakeSize { get; init; } = 256;
        /// <summary>1024 is a hard engine ceiling. 2048 crashes the game; this is not a preference.</summary>
        public int MaxBakeSize { get; init; } = 1024;
        public float DesiredTexelsPerMetre { get; init; } = 16f;
        public float MinTexelsPerMetre { get; init; } = 4f;
        public float WeldEpsilonRelative { get; init; } = 1e-5f;
        /// <summary>1/sqrt(3). Lowering this breaks the fold-freedom proof - do not.</summary>
        public float ProjectMinDot { get; init; } = 0.577f;
        /// <summary>
        /// The cone a MERGED chart must still fit inside. It is not an independent taste knob: it must be no
        /// wider than <see cref="ProjectMinDot"/> allows, or the merged chart fails the mean-normal test, falls
        /// back to an arbitrary member's cardinal axis, and FOLDS - projecting onto an axis it is nearly
        /// perpendicular to. Measured consequence of getting this wrong at 70 degrees: charts with kilometre-wide
        /// projected extents, which drove the scale search to nothing and failed 527 meshes as "atlas too small".
        /// Left at 0 it is derived from ProjectMinDot, which is the only safe answer.
        /// </summary>
        public float MergeConeDegrees { get; init; } = 0f;
        public bool MergeCharts { get; init; } = true;
        public int MaxCharts { get; init; } = 16384;
    }

    public sealed record UnwrapDiagnostics(
        int Charts, int ChartsMerged, int VerticesIn, int VerticesOut, int ParkedTriangles,
        int MinBakeSize, bool ExceedsPreferredSize, float UvScalePerMetre, float AtlasCoverage,
        float WorldAreaSqM, float WorstNormalDot, string Reason)
    {
        public float AddedVertexPercent => VerticesIn == 0 ? 0f : 100f * (VerticesOut - VerticesIn) / VerticesIn;
        public float MeanTexelsPerMetreAt(int size) => UvScalePerMetre * size;
    }

    public sealed record UnwrapResult(UnwrapStatus Status, UnwrapDiagnostics Diagnostics,
                                      IReadOnlyList<StandardMeshRewriter.MaterialPlan?>? Lod0Plan);

    /// <summary>
    /// One triangle, addressed the way the file stores it. <paramref name="Slot"/> is where its triple begins in
    /// the material's RawIndices, so corner k lives at <c>Slot + (2 - k)</c> - the reader reverses each triple.
    /// Carrying it is not an optimisation: recovering it afterwards by matching vertex triples is ambiguous
    /// whenever a mesh repeats one, and degenerate triangles are dropped so a running count does not line up.
    /// </summary>
    private readonly record struct Tri(int Mat, int Slot, int A, int B, int C);

    private static UnwrapResult Fail(UnwrapStatus s, string why)
        => new(s, new UnwrapDiagnostics(0, 0, 0, 0, 0, 0, false, 0f, 0f, 0f, 0f, why), null);

    /// <summary>
    /// Build an unwrap for the mesh's LOD0. Pure and deterministic: the same bytes give the same plan. Never
    /// throws on malformed input - a mesh that cannot be unwrapped comes back with a status saying why.
    /// </summary>
    public static UnwrapResult Unwrap(StandardMesh mesh, UnwrapOptions? options = null)
    {
        var o = options ?? new UnwrapOptions();
        if (mesh is null || mesh.Lods.Count == 0 || mesh.Lods[0].Count == 0)
            return Fail(UnwrapStatus.NoUsableGeometry, "no LOD0");
        var lod = mesh.Lods[0];

        // A strip's index slot belongs to three consecutive triangles, so renumbering it per chart is impossible;
        // and StandardMesh's strip expansion does not flip alternate winding, so half the normals would be
        // inverted and would bucket backwards. Refuse the whole mesh rather than get it subtly wrong.
        foreach (var m in lod)
            if (m.RenderType != 4)
                return Fail(UnwrapStatus.UnsupportedTopology, $"material '{m.Name}' is renderType {m.RenderType}");

        // ---- 1. triangles, in SLOT order and in the baker's winding ---------------------------------
        // Faces is a lossy view: it reverses a list, expands strips naively and drops nothing. The plan must
        // renumber RawIndices, so triangles are taken from there - reversed the same way StandardMesh reverses
        // them, so the geometric normals agree with the file's own vertex normals.
        var tris = new List<Tri>();
        var triNormal = new List<Vector3>();
        var triArea = new List<float>();
        int parked = 0, verticesIn = 0;

        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int mi = 0; mi < lod.Count; mi++)
        {
            var m = lod[mi];
            verticesIn += m.NumVertices;
            foreach (var v in m.Vertices)
            {
                var p = new Vector3(v.X, v.Y, v.Z);
                if (!IsFinite(p)) continue;
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
        }
        if (lo.X > hi.X) return Fail(UnwrapStatus.NoUsableGeometry, "no finite vertices");
        float diag = MathF.Max((hi - lo).Length(), 1e-4f);

        for (int mi = 0; mi < lod.Count; mi++)
        {
            var m = lod[mi];
            var raw = m.RawIndices;
            for (int k = 0; k + 2 < raw.Length; k += 3)
            {
                int a = raw[k + 2], b = raw[k + 1], c = raw[k];      // the reversal StandardMesh applies
                if ((uint)a >= (uint)m.NumVertices || (uint)b >= (uint)m.NumVertices || (uint)c >= (uint)m.NumVertices
                    || a == b || b == c || a == c) { parked++; continue; }
                var pa = V(m.Vertices[a]); var pb = V(m.Vertices[b]); var pc = V(m.Vertices[c]);
                if (!IsFinite(pa) || !IsFinite(pb) || !IsFinite(pc)) { parked++; continue; }
                var cr = Vector3.Cross(pb - pa, pc - pa);
                float len = cr.Length();
                if (len <= 1e-9f * diag * diag) { parked++; continue; }
                tris.Add(new Tri(mi, k, a, b, c));
                triNormal.Add(cr / len);
                triArea.Add(len * 0.5f);
            }
        }
        if (tris.Count == 0) return Fail(UnwrapStatus.NoUsableGeometry, "every triangle was degenerate");

        // ---- 2. weld: topology only, never output indexing -------------------------------------------
        var weld = BuildWeld(lod, lo, o.WeldEpsilonRelative * diag);

        // ---- 3+4. bucket by dominant axis, then connected components within a bucket ------------------
        // Carrying the BUCKET in the edge key is what makes a roof not merge with its wall without any dihedral
        // heuristic, and what keeps a double-sided card (front and back coincident) as two charts rather than
        // shattering: the two faces are in opposite buckets and never share an edge key.
        int n = tris.Count;
        var bucket = new int[n];
        for (int i = 0; i < n; i++) bucket[i] = BucketOf(triNormal[i]);

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var vbase = VertexBases(lod);
        var edge = new Dictionary<(int bucket, int a, int b), int>();
        for (int i = 0; i < n; i++)
        {
            var t = tris[i];
            int wa = weld[vbase[t.Mat] + t.A], wb = weld[vbase[t.Mat] + t.B], wc = weld[vbase[t.Mat] + t.C];
            foreach (var (u, v) in new[] { (wa, wb), (wb, wc), (wc, wa) })
            {
                var key = (bucket[i], Math.Min(u, v), Math.Max(u, v));
                if (edge.TryGetValue(key, out int other)) Union(i, other);
                else edge[key] = i;
            }
        }

        var chartOf = new int[n];
        var rootToChart = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!rootToChart.TryGetValue(r, out int id)) rootToChart[r] = id = rootToChart.Count;
            chartOf[i] = id;
        }
        int charts = rootToChart.Count;
        if (charts > o.MaxCharts) return Fail(UnwrapStatus.ChartBudget, $"{charts} charts");

        // ---- 5. merge coplanar neighbours across buckets ---------------------------------------------
        int merged = 0;
        if (o.MergeCharts && charts > 1)
        {
            // Never wider than the projection can take: the merge must preserve the same guarantee.
            float coneCos = o.MergeConeDegrees > 0f
                ? MathF.Max(MathF.Cos(o.MergeConeDegrees * MathF.PI / 180f), o.ProjectMinDot)
                : o.ProjectMinDot;
            (chartOf, charts, merged) = MergeCoplanar(tris, vbase, weld, chartOf, charts, triNormal, triArea, coneCos);
        }

        // ---- 6. project each chart onto a verified axis ----------------------------------------------
        var chartTris = new List<int>[charts];
        for (int i = 0; i < charts; i++) chartTris[i] = new List<int>();
        for (int i = 0; i < n; i++) chartTris[chartOf[i]].Add(i);

        var frames = new (Vector3 U, Vector3 V)[charts];
        float worstDot = 1f;
        for (int c = 0; c < charts; c++)
        {
            var axis = ChartAxis(chartTris[c], triNormal, triArea, bucket, o.ProjectMinDot, out float minDot);
            worstDot = MathF.Min(worstDot, minDot);
            Basis(axis, out var u, out var v);
            frames[c] = (u, v);
        }

        // Project every triangle CORNER independently: a vertex shared by two charts gets a different UV in each,
        // which is exactly the seam the clone bookkeeping below pays for.
        var cornerUv = new Vector2[n * 3];
        var chartMin = new Vector2[charts];
        var chartMax = new Vector2[charts];
        for (int c = 0; c < charts; c++) { chartMin[c] = new Vector2(float.MaxValue); chartMax[c] = new Vector2(float.MinValue); }
        for (int i = 0; i < n; i++)
        {
            var t = tris[i];
            int c = chartOf[i];
            var (u, v) = frames[c];
            for (int k = 0; k < 3; k++)
            {
                var p = V(lod[t.Mat].Vertices[k == 0 ? t.A : k == 1 ? t.B : t.C]);
                var uv = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
                cornerUv[i * 3 + k] = uv;
                chartMin[c] = Vector2.Min(chartMin[c], uv);
                chartMax[c] = Vector2.Max(chartMax[c], uv);
            }
        }

        // ---- 7. pack: ladder over atlas sizes, binary search the scale at each -----------------------
        float worldArea = 0f;
        foreach (var a in triArea) worldArea += a;

        var extent = new Vector2[charts];
        for (int c = 0; c < charts; c++) extent[c] = chartMax[c] - chartMin[c];

        // Climb the ladder and stop at the SMALLEST size that both packs and resolves the surface well enough.
        // Every larger size is also safe - the gutter is a UV fraction, so baking bigger only widens it - so the
        // smallest adequate one is the right floor to publish, and the tier picks up from there.
        int chosen = 0; float chosenScale = 0f; float coverage = 0f;
        (int X, int Y)[]? origins = null;
        bool packedAnywhere = false;
        foreach (int R in new[] { 64, 128, 256, 512, 1024 })
        {
            if (R < o.MinBakeSize || R > o.MaxBakeSize) continue;
            if (!TryBestScale(extent, R, o.GutterCells, out float s, out var org, out float cov)) continue;
            packedAnywhere = true;
            chosen = R; chosenScale = s; origins = org; coverage = cov;
            if (s * R >= o.DesiredTexelsPerMetre) break;      // good enough; a larger atlas buys nothing needed
        }
        if (origins is null || chosen == 0)
            return Fail(UnwrapStatus.AtlasTooSmall,
                        $"{charts} charts would not shelf-pack at any size up to {o.MaxBakeSize}px");
        if (chosenScale * chosen < o.MinTexelsPerMetre)
            return Fail(UnwrapStatus.AtlasTooSmall,
                        $"{chosenScale * chosen:0.00} texels/m at {chosen}px is below the {o.MinTexelsPerMetre:0} floor"
                        + $" ({charts} charts, {worldArea:0.#} m2)");
        _ = packedAnywhere;

        // ---- 8. emit UVs on the 1/R lattice, and build the per-material plans -------------------------
        var plans = BuildPlans(lod, tris, chartOf, cornerUv, chartMin, origins, frames.Length,
                               chosen, chosenScale, o.GutterCells, out int verticesOut, out bool overflow);
        if (overflow) return Fail(UnwrapStatus.IndexCeiling, "splitting crosses the 65,535 vertex ceiling");

        var diag2 = new UnwrapDiagnostics(charts, merged, verticesIn, verticesOut, parked, chosen,
                                          chosen > o.PreferredMaxBakeSize, chosenScale, coverage,
                                          worldArea, worstDot, "ok");
        return new UnwrapResult(UnwrapStatus.Ok, diag2, plans);
    }

    /// <summary>
    /// The atlas size an unwrap from this class was packed for, read back out of the UVs themselves - or null for an
    /// unwrap it did not make.
    ///
    /// <para>Charts are packed with a gutter of <see cref="UnwrapOptions.GutterCells"/> texels AT THAT SIZE. Bake the map
    /// smaller and the gutter shrinks with it; at half size it is two texels, the baker's three-texel dilation reaches
    /// across it, and one chart's light bleeds into the next. So a patched mesh must never bake below this. Every UV
    /// is written as a whole number of texels over the chosen size (<c>round(px) / R</c>), which makes the smallest R
    /// that turns them all into integers that size. DICE's own unwraps land on no such grid and return null, and
    /// keep the sizing they always had.</para>
    /// </summary>
    public static int? RecoverMinBakeSize(IReadOnlyList<Vector2>? uvs)
    {
        if (uvs is null || uvs.Count == 0) return null;
        bool any = false;
        foreach (int r in new[] { 64, 128, 256, 512, 1024 })
        {
            bool fits = true;
            foreach (var uv in uvs)
            {
                if (uv.X == 0f && uv.Y == 0f) continue;       // a section with no unwrap of its own
                any = true;
                float x = uv.X * r, y = uv.Y * r;
                if (MathF.Abs(x - MathF.Round(x)) > 1e-3f || MathF.Abs(y - MathF.Round(y)) > 1e-3f) { fits = false; break; }
            }
            if (!any) return null;
            if (fits) return r;
        }
        return null;
    }

    private static Vector3 V(Geometry.Vec3 v) => new(v.X, v.Y, v.Z);
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Per-material offsets into the concatenated vertex numbering the weld table uses. Computed once:
    /// summing them inside the edge loop made the decomposition quadratic in material count.</summary>
    private static int[] VertexBases(IReadOnlyList<SmMaterial> lod)
    {
        var b = new int[lod.Count + 1];
        for (int i = 0; i < lod.Count; i++) b[i + 1] = b[i] + lod[i].NumVertices;
        return b;
    }

    /// <summary>
    /// Positions quantised into a spatial hash, probing all 27 neighbouring cells. The 27-cell probe is the point:
    /// a single-cell lookup misses two coordinates that fall either side of a cell boundary, which leaves seams
    /// unwelded and inflates the chart count - and chart count is what sets the atlas size.
    /// </summary>
    private static int[] BuildWeld(IReadOnlyList<SmMaterial> lod, Vector3 origin, float eps)
    {
        int total = 0;
        foreach (var m in lod) total += m.NumVertices;
        var weld = new int[total];
        var cells = new Dictionary<(int, int, int), List<int>>();
        var pos = new Vector3[total];
        float inv = 1f / MathF.Max(eps, 1e-9f);

        int at = 0;
        foreach (var m in lod)
            foreach (var v in m.Vertices) pos[at++] = new Vector3(v.X, v.Y, v.Z);

        for (int i = 0; i < total; i++)
        {
            var p = pos[i];
            if (!IsFinite(p)) { weld[i] = i; continue; }
            var q = (p - origin) * inv;
            int cx = (int)MathF.Floor(q.X), cy = (int)MathF.Floor(q.Y), cz = (int)MathF.Floor(q.Z);
            int hit = -1;
            for (int dz = -1; dz <= 1 && hit < 0; dz++)
                for (int dy = -1; dy <= 1 && hit < 0; dy++)
                    for (int dx = -1; dx <= 1 && hit < 0; dx++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var lst))
                            foreach (int j in lst)
                                if ((pos[j] - p).LengthSquared() <= eps * eps) { hit = weld[j]; break; }
            weld[i] = hit >= 0 ? hit : i;
            if (!cells.TryGetValue((cx, cy, cz), out var own)) cells[(cx, cy, cz)] = own = new List<int>();
            own.Add(i);
        }
        return weld;
    }

    /// <summary>The six cube buckets. Every member satisfies dot(n, signedAxis) &gt;= 1/sqrt(3) by construction,
    /// which is the whole fold-freedom argument.</summary>
    private static int BucketOf(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        int axis = ax >= ay ? (ax >= az ? 0 : 2) : (ay >= az ? 1 : 2);
        float c = axis == 0 ? n.X : axis == 1 ? n.Y : n.Z;
        return axis * 2 + (c < 0f ? 1 : 0);
    }

    private static Vector3 AxisOf(int bucket)
    {
        var a = bucket / 2 == 0 ? Vector3.UnitX : bucket / 2 == 1 ? Vector3.UnitY : Vector3.UnitZ;
        return (bucket & 1) == 1 ? -a : a;
    }

    /// <summary>The area-weighted mean normal when every triangle stays inside the cone around it, otherwise the
    /// bucket's cardinal axis - which passes by construction, so there is always an answer.</summary>
    private static Vector3 ChartAxis(List<int> members, List<Vector3> normals, List<float> areas, int[] bucket,
                                     float minDot, out float worst)
    {
        var sum = Vector3.Zero;
        foreach (int i in members) sum += normals[i] * areas[i];
        worst = 1f;
        if (sum.LengthSquared() > 1e-12f)
        {
            var mean = Vector3.Normalize(sum);
            float w = 1f;
            foreach (int i in members) w = MathF.Min(w, Vector3.Dot(normals[i], mean));
            if (w >= minDot) { worst = w; return mean; }
        }
        // The bucket's cardinal axis passes by construction for an UNMERGED chart. A merged one is only ever
        // admitted when it still satisfies the same bound, so this stays true - the assert is here because if it
        // ever stops being true the symptom is a folded chart with an enormous projected extent, which reads as
        // "the atlas is too small" and sends the investigation to entirely the wrong place.
        var axis = AxisOf(bucket[members[0]]);
        float w2 = 1f;
        foreach (int i in members) w2 = MathF.Min(w2, Vector3.Dot(normals[i], axis));
        worst = w2;
        return axis;
    }

    private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var pick = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(n, pick));
        v = Vector3.Cross(n, u);
    }

    /// <summary>
    /// Merge charts that share an edge and stay within a cone of their joint mean normal. Merging is free in
    /// clones - it removes a seam rather than adding one - and every chart removed is a gutter perimeter saved,
    /// which is the dominant cost in the atlas.
    /// </summary>
    private static (int[] chartOf, int charts, int merged) MergeCoplanar(
        List<Tri> tris, int[] vbase, int[] weld, int[] chartOf, int charts,
        List<Vector3> normals, List<float> areas, float coneCos)
    {
        var sum = new Vector3[charts];
        for (int i = 0; i < tris.Count; i++) sum[chartOf[i]] += normals[i] * areas[i];

        // Adjacency: two charts are neighbours when they share a welded position.
        var owners = new Dictionary<int, List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            foreach (int lv in new[] { t.A, t.B, t.C })
            {
                int w = weld[vbase[t.Mat] + lv];
                if (!owners.TryGetValue(w, out var l)) owners[w] = l = new List<int>();
                if (!l.Contains(chartOf[i])) l.Add(chartOf[i]);
            }
        }
        var pairs = new HashSet<(int, int)>();
        foreach (var l in owners.Values)
            for (int i = 0; i < l.Count; i++)
                for (int j = i + 1; j < l.Count; j++)
                    pairs.Add((Math.Min(l[i], l[j]), Math.Max(l[i], l[j])));

        var parent = new int[charts];
        for (int i = 0; i < charts; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        var members = new List<int>[charts];
        for (int i = 0; i < charts; i++) members[i] = new List<int>();
        for (int i = 0; i < tris.Count; i++) members[chartOf[i]].Add(i);

        int merged = 0;
        // Deterministic order: ascending pair, no heap, no randomness.
        var ordered = new List<(int, int)>(pairs);
        ordered.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.Item2.CompareTo(y.Item2));
        foreach (var (a0, b0) in ordered)
        {
            int a = Find(a0), b = Find(b0);
            if (a == b) continue;
            var joint = sum[a] + sum[b];
            if (joint.LengthSquared() < 1e-12f) continue;
            var mean = Vector3.Normalize(joint);
            bool ok = true;
            foreach (int i in members[a]) if (Vector3.Dot(normals[i], mean) < coneCos) { ok = false; break; }
            if (ok) foreach (int i in members[b]) if (Vector3.Dot(normals[i], mean) < coneCos) { ok = false; break; }
            if (!ok) continue;
            parent[b] = a;
            sum[a] = joint;
            members[a].AddRange(members[b]);
            members[b].Clear();
            merged++;
        }

        var remap = new Dictionary<int, int>();
        var outChart = new int[chartOf.Length];
        for (int i = 0; i < chartOf.Length; i++)
        {
            int r = Find(chartOf[i]);
            if (!remap.TryGetValue(r, out int id)) remap[r] = id = remap.Count;
            outChart[i] = id;
        }
        return (outChart, remap.Count, merged);
    }

    /// <summary>
    /// Binary-search the largest uniform UV-per-metre scale whose padded chart boxes shelf-pack at R.
    ///
    /// <para>The lower bound is DERIVED from the mesh, not fixed. A constant floor has to be small enough for the
    /// worst input, and this corpus contains meshes whose coordinates run to the millions - at a fixed 1e-5 the
    /// search began outside its own feasible region and reported "cannot pack" for a mesh that packs fine. Start
    /// where the largest chart is guaranteed to be a single cell, which is feasible whenever anything is.</para>
    /// </summary>
    private static bool TryBestScale(Vector2[] extent, int R, int gutter,
                                     out float scale, out (int X, int Y)[] origins, out float coverage)
    {
        scale = 0f; origins = Array.Empty<(int, int)>(); coverage = 0f;
        float maxExt = 0f;
        foreach (var e in extent) maxExt = MathF.Max(maxExt, MathF.Max(e.X, e.Y));
        if (maxExt <= 0f) return false;
        // BOTH bounds are derived from the mesh. The upper one is the scale at which the largest chart alone
        // fills the usable atlas - nothing above it can ever pack, so starting there is both correct and tight.
        // An arbitrary large ceiling instead (4096 was the first attempt) is not merely wasteful: a fixed
        // iteration count then spends every step descending orders of magnitude that were never feasible, ends
        // far above the true answer, and the caller reads the result as "this mesh needs more texels than exist".
        // That single mistake failed 548 meshes, including an ordinary 9.9 m bunker.
        float loS = 0.5f / (maxExt * R);
        float hiS = (R - 2 * gutter) / (maxExt * R);
        if (hiS <= loS) return false;
        if (!TryPack(extent, R, gutter, loS, out _, out _)) return false;
        for (int it = 0; it < 24; it++)
        {
            float mid = 0.5f * (loS + hiS);
            if (TryPack(extent, R, gutter, mid, out _, out _)) loS = mid; else hiS = mid;
        }
        if (!TryPack(extent, R, gutter, loS, out origins, out coverage)) return false;
        scale = loS;
        return true;
    }

    /// <summary>
    /// Shelf next-fit-decreasing-height. Chosen over a bitmap skyline deliberately: with a four-cell gutter the
    /// padded box of a small chart is mostly gutter, so the L-shaped fit a skyline buys largely evaporates -
    /// while rectangle disjointness gives the non-overlap guarantee in integer arithmetic, with no epsilon.
    /// </summary>
    private static bool TryPack(Vector2[] extent, int R, int gutter, float scale,
                                out (int X, int Y)[] origins, out float coverage)
    {
        int c = extent.Length;
        origins = new (int, int)[c];
        coverage = 0f;
        var boxes = new (int W, int H, int Id)[c];
        long covered = 0;
        for (int i = 0; i < c; i++)
        {
            int w = (int)MathF.Ceiling(extent[i].X * scale * R) + 2 * gutter;
            int h = (int)MathF.Ceiling(extent[i].Y * scale * R) + 2 * gutter;
            if (w > R - 2 * gutter || h > R - 2 * gutter) return false;
            boxes[i] = (w, h, i);
            covered += (long)(w - 2 * gutter) * (h - 2 * gutter);
        }
        Array.Sort(boxes, (a, b) => a.H != b.H ? b.H.CompareTo(a.H) : a.W != b.W ? b.W.CompareTo(a.W) : a.Id.CompareTo(b.Id));

        int x = gutter, y = gutter, shelfH = 0;
        foreach (var (w, h, id) in boxes)
        {
            if (x + w > R - gutter) { y += shelfH; x = gutter; shelfH = 0; }
            if (y + h > R - gutter) return false;
            origins[id] = (x, y);
            x += w;
            shelfH = Math.Max(shelfH, h);
        }
        coverage = covered / (float)((long)R * R);
        return true;
    }

    /// <summary>
    /// Turn the packed charts into per-material plans. A vertex referenced by two charts needs a copy in each, so
    /// the first chart to claim it keeps the original index and every later claim appends a clone.
    /// </summary>
    private static StandardMeshRewriter.MaterialPlan?[] BuildPlans(
        IReadOnlyList<SmMaterial> lod, List<Tri> tris, int[] chartOf, Vector2[] cornerUv,
        Vector2[] chartMin, (int X, int Y)[] origins, int charts, int R, float scale, int gutter,
        out int verticesOut, out bool overflow)
    {
        int mats = lod.Count;
        var uvOf = new Dictionary<(int mat, int local, int chart), int>();
        var clones = new List<int>[mats];
        var uvs = new List<(float U, float V)>[mats];
        var idx = new ushort[mats][];
        for (int m = 0; m < mats; m++)
        {
            clones[m] = new List<int>();
            uvs[m] = new List<(float, float)>(new (float, float)[lod[m].NumVertices]);
            idx[m] = (ushort[])lod[m].RawIndices.Clone();
        }
        var claimed = new HashSet<(int mat, int local)>();

        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            int c = chartOf[i];
            for (int k = 0; k < 3; k++)
            {
                int local = k == 0 ? t.A : k == 1 ? t.B : t.C;
                var uv = cornerUv[i * 3 + k] - chartMin[c];
                // On the 1/R lattice, so RecoverMinBakeSize can read the gutter floor back out of the file later.
                float px = origins[c].X + gutter + uv.X * scale * R;
                float py = origins[c].Y + gutter + uv.Y * scale * R;
                var final = (MathF.Round(px) / R, MathF.Round(py) / R);

                int target;
                if (uvOf.TryGetValue((t.Mat, local, c), out int existing)) target = existing;
                else if (claimed.Add((t.Mat, local)))
                {
                    target = local;                                    // the first chart keeps the original slot
                    uvs[t.Mat][local] = final;
                    uvOf[(t.Mat, local, c)] = target;
                }
                else
                {
                    target = lod[t.Mat].NumVertices + clones[t.Mat].Count;
                    clones[t.Mat].Add(local);
                    uvs[t.Mat].Add(final);
                    uvOf[(t.Mat, local, c)] = target;
                }
                idx[t.Mat][t.Slot + (2 - k)] = (ushort)target;   // the reader reverses each triple
            }
        }

        verticesOut = 0;
        overflow = false;
        var plans = new StandardMeshRewriter.MaterialPlan?[mats];
        for (int m = 0; m < mats; m++)
        {
            int outCount = lod[m].NumVertices + clones[m].Count;
            verticesOut += outCount;
            if (outCount > 65535) { overflow = true; return plans; }
            var promote = lod[m].VertexByteSize == 32 ? StandardMeshRewriter.StridePromotion.To40 : null;
            plans[m] = new StandardMeshRewriter.MaterialPlan(uvs[m].ToArray(), clones[m].ToArray(), idx[m], promote);
        }
        return plans;
    }

}
