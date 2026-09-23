using System;
using System.Collections.Generic;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// The coarsest collision a model can still have: one box round each of its connected parts. It is the last resort
/// when neither the collision mesh asked for nor any level of detail fits the collision section's 32,767-vertex
/// limit - a box per part is solid where the model is and costs twelve faces a part, where the alternative was no
/// collision at all and players walking through the object.
///
/// Parts are what shares a corner: positions that are bit-for-bit the same are one point, the way an exporter
/// splits a vertex per UV and normal. A box that lies wholly inside another adds nothing and is left out. Past
/// <c>maxBoxes</c> parts, neighbouring parts are merged into one box by a grid over the model that is made coarser
/// until the count fits - so a pile of loose debris becomes a few boxes, not thousands.
///
/// Faces are in the engine's order: clockwise seen from outside, so the LEFT-hand normal -(b-a)x(c-a) that
/// <see cref="Rfa.StandardMeshWriter.BuildCollisionSection(IReadOnlyList{Vec3}, IReadOnlyList{ValueTuple{int, int, int}}, IReadOnlyList{int})"/>
/// writes points out of the box. Each box is one submesh piece named after the source material that has most of
/// its faces, so a per-material collision table still gives wood to the wooden part.
/// </summary>
public static class ComponentBoxes
{
    /// <summary>256 boxes: 2,048 vertices and 3,072 faces, the size of a dense retail COL02.</summary>
    public const int DefaultMaxBoxes = 256;

    /// <summary>No side of a box is thinner than this (metres): a flat part - a floor, a sign - still gets a solid
    /// with depth instead of faces of no area.</summary>
    public const float MinimumSide = 0.05f;

    private struct Box
    {
        public Vec3 Min, Max;
        public Dictionary<string, int> Faces;       // source material -> faces of it inside the box
    }

    /// <summary>Boxes round the connected parts of <paramref name="mesh"/>, at most <paramref name="maxBoxes"/>
    /// of them. Empty when the mesh has no faces.</summary>
    public static ObjMesh Build(ObjMesh mesh, int maxBoxes = DefaultMaxBoxes)
    {
        if (maxBoxes < 1) throw new ArgumentOutOfRangeException(nameof(maxBoxes));
        var boxes = Parts(mesh);
        if (boxes.Count > maxBoxes && boxes.Count <= 4096) boxes = WithoutContained(boxes);
        if (boxes.Count > maxBoxes) boxes = Clustered(boxes, maxBoxes);
        boxes = WithoutContained(boxes);

        var bySource = new Dictionary<string, ObjSubMesh>(StringComparer.Ordinal);
        var pieces = new List<ObjSubMesh>();
        foreach (var b in boxes)
        {
            string material = b.Faces.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            if (!bySource.TryGetValue(material, out var s)) { bySource[material] = s = new ObjSubMesh { Material = material }; pieces.Add(s); }
            AddBox(s, b.Min, b.Max);
        }
        var result = ObjMesh.FromSubMeshes(pieces);
        result.MtlLibs.AddRange(mesh.MtlLibs);
        return result;
    }

    // Union-find over positions shared bit for bit (+0 and -0 are one point), then one box per root.
    private static List<Box> Parts(ObjMesh mesh)
    {
        var idOf = new Dictionary<(int, int, int), int>();
        var at = new List<Vec3>();
        var parent = new List<int>();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var corners = new List<int[]>();
        foreach (var s in mesh.SubMeshes)
        {
            var ids = new int[s.Positions.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                var p = s.Positions[i];
                var key = (Bits(p.X), Bits(p.Y), Bits(p.Z));
                if (!idOf.TryGetValue(key, out int id)) { id = at.Count; idOf[key] = id; at.Add(p); parent.Add(id); }
                ids[i] = id;
            }
            corners.Add(ids);
            foreach (var (a, b, c) in s.Faces)
            {
                int ra = Find(ids[a]), rb = Find(ids[b]), rc = Find(ids[c]);
                if (ra != rb) parent[rb] = ra;
                rc = Find(rc); ra = Find(ra);
                if (ra != rc) parent[rc] = ra;
            }
        }

        var boxOf = new Dictionary<int, int>();
        var boxes = new List<Box>();
        for (int si = 0; si < mesh.SubMeshes.Count; si++)
        {
            var s = mesh.SubMeshes[si];
            var ids = corners[si];
            foreach (var (a, b, c) in s.Faces)
            {
                int root = Find(ids[a]);
                if (!boxOf.TryGetValue(root, out int bi))
                {
                    bi = boxes.Count; boxOf[root] = bi;
                    var p0 = at[ids[a]];
                    boxes.Add(new Box { Min = p0, Max = p0, Faces = new Dictionary<string, int>(StringComparer.Ordinal) });
                }
                var box = boxes[bi];
                foreach (int v in new[] { ids[a], ids[b], ids[c] }) Grow(ref box, at[v], at[v]);
                box.Faces[s.Material] = box.Faces.GetValueOrDefault(s.Material) + 1;
                boxes[bi] = box;
            }
        }
        return boxes;
    }

    private static int Bits(float f) => f == 0f ? 0 : BitConverter.SingleToInt32Bits(f);

    private static void Grow(ref Box b, Vec3 min, Vec3 max)
    {
        b.Min = new Vec3(MathF.Min(b.Min.X, min.X), MathF.Min(b.Min.Y, min.Y), MathF.Min(b.Min.Z, min.Z));
        b.Max = new Vec3(MathF.Max(b.Max.X, max.X), MathF.Max(b.Max.Y, max.Y), MathF.Max(b.Max.Z, max.Z));
    }

    // Largest first, so a box is only ever tested against the ones that could hold it. Its faces count towards the
    // box that holds it, so the material vote still sees them.
    private static List<Box> WithoutContained(List<Box> boxes)
    {
        var bySize = boxes.OrderByDescending(Volume).ToList();
        var kept = new List<Box>();
        foreach (var b in bySize)
        {
            int holder = kept.FindIndex(k => k.Min.X <= b.Min.X && k.Min.Y <= b.Min.Y && k.Min.Z <= b.Min.Z
                                           && k.Max.X >= b.Max.X && k.Max.Y >= b.Max.Y && k.Max.Z >= b.Max.Z);
            if (holder < 0) { kept.Add(b); continue; }
            foreach (var (m, n) in b.Faces) kept[holder].Faces[m] = kept[holder].Faces.GetValueOrDefault(m) + n;
        }
        return kept;
    }

    private static float Volume(Box b)
        => MathF.Max(b.Max.X - b.Min.X, MinimumSide) * MathF.Max(b.Max.Y - b.Min.Y, MinimumSide) * MathF.Max(b.Max.Z - b.Min.Z, MinimumSide);

    // Parts whose centres fall in one cell of a grid over the whole model become one box; the grid halves until the
    // count fits (one cell holds everything, so it always ends).
    private static List<Box> Clustered(List<Box> boxes, int maxBoxes)
    {
        var lo = boxes[0].Min; var hi = boxes[0].Max;
        foreach (var b in boxes)
        {
            lo = new Vec3(MathF.Min(lo.X, b.Min.X), MathF.Min(lo.Y, b.Min.Y), MathF.Min(lo.Z, b.Min.Z));
            hi = new Vec3(MathF.Max(hi.X, b.Max.X), MathF.Max(hi.Y, b.Max.Y), MathF.Max(hi.Z, b.Max.Z));
        }
        float side = MathF.Max(MathF.Max(hi.X - lo.X, hi.Y - lo.Y), MathF.Max(hi.Z - lo.Z, 1e-6f));
        for (int cells = 64; ; cells /= 2)
        {
            float size = side / cells;
            int Cell(float v, float from) => Math.Clamp((int)((v - from) / size), 0, cells - 1);
            var byCell = new Dictionary<(int, int, int), int>();
            var merged = new List<Box>();
            foreach (var b in boxes)
            {
                var key = (Cell((b.Min.X + b.Max.X) / 2, lo.X), Cell((b.Min.Y + b.Max.Y) / 2, lo.Y), Cell((b.Min.Z + b.Max.Z) / 2, lo.Z));
                if (!byCell.TryGetValue(key, out int i))
                {
                    byCell[key] = merged.Count;
                    merged.Add(new Box { Min = b.Min, Max = b.Max, Faces = new Dictionary<string, int>(b.Faces, StringComparer.Ordinal) });
                    continue;
                }
                var m = merged[i];
                Grow(ref m, b.Min, b.Max);
                foreach (var (mat, n) in b.Faces) m.Faces[mat] = m.Faces.GetValueOrDefault(mat) + n;
                merged[i] = m;
            }
            if (merged.Count <= maxBoxes || cells == 1) return merged;
        }
    }

    // Eight corners and twelve faces, each face turned so its left-hand normal points away from the centre.
    private static void AddBox(ObjSubMesh s, Vec3 min, Vec3 max)
    {
        static (float, float) Span(float a, float b)
        {
            if (b - a >= MinimumSide) return (a, b);
            float mid = (a + b) / 2;
            return (mid - MinimumSide / 2, mid + MinimumSide / 2);
        }
        var (x0, x1) = Span(min.X, max.X);
        var (y0, y1) = Span(min.Y, max.Y);
        var (z0, z1) = Span(min.Z, max.Z);
        int o = s.Positions.Count;
        var v = new[]
        {
            new Vec3(x0, y0, z0), new Vec3(x1, y0, z0), new Vec3(x1, y1, z0), new Vec3(x0, y1, z0),
            new Vec3(x0, y0, z1), new Vec3(x1, y0, z1), new Vec3(x1, y1, z1), new Vec3(x0, y1, z1),
        };
        var centre = new Vec3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
        foreach (var p in v)
        {
            s.Positions.Add(p);
            var n = new Vec3(p.X - centre.X, p.Y - centre.Y, p.Z - centre.Z);
            float len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            s.Normals.Add(new Vec3(n.X / len, n.Y / len, n.Z / len));
            s.Uvs.Add((0f, 0f));
        }
        int[] quads = { 0, 1, 2, 3, 4, 7, 6, 5, 0, 4, 5, 1, 3, 2, 6, 7, 1, 5, 6, 2, 0, 3, 7, 4 };
        for (int q = 0; q < quads.Length; q += 4)
        {
            Outward(quads[q], quads[q + 1], quads[q + 2]);
            Outward(quads[q], quads[q + 2], quads[q + 3]);
        }

        void Outward(int a, int b, int c)
        {
            Vec3 pa = v[a], pb = v[b], pc = v[c];
            float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z, wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
            float nx = -(uy * wz - uz * wy), ny = -(uz * wx - ux * wz), nz = -(ux * wy - uy * wx);
            float fx = (pa.X + pb.X + pc.X) / 3 - centre.X, fy = (pa.Y + pb.Y + pc.Y) / 3 - centre.Y, fz = (pa.Z + pb.Z + pc.Z) / 3 - centre.Z;
            if (nx * fx + ny * fy + nz * fz >= 0f) s.Faces.Add((o + a, o + b, o + c));
            else s.Faces.Add((o + a, o + c, o + b));
        }
    }
}
