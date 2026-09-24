using System;
using System.Collections.Generic;
using System.Linq;
using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Mesh;

/// <summary>
/// What of a model can be seen from outside it. The model is voxelised, the air around it flooded in from every side,
/// and a triangle that touches that air is exposed; one that only touches air the model closes in - an engine bay
/// under a shut hood, the liner behind a panel, the inside of a trunk - is not. See-through parts (glass) let the air
/// through, so a cabin behind its windows is exposed: it can be seen.
///
/// A download's car spends half its triangles where no one will ever look - the Lamborghini Revuelto's engine bay,
/// trunk bay, liners and door jambs are 280,000 of its 548,000 - and a simplifier that cannot tell spends the budget
/// there and folds the body panels instead.
/// </summary>
public static class MeshVisibility
{
    /// <summary>For each submesh, in order, the share of its triangles that touch the air outside the model (0: wholly
    /// hidden inside, 1: all on the outside).</summary>
    public static float[] Exposure(ObjMesh mesh, Func<ObjSubMesh, bool>? seeThrough = null, int resolution = 128, float ground = 0f)
        => ExposedFaces(mesh, seeThrough, resolution, ground)
           .Select(f => f.Length == 0 ? 1f : f.Count(e => e) / (float)f.Length).ToArray();

    /// <summary>
    /// The model without what no one sees from outside: every triangle that touches only air the model closes in is
    /// left out (<paramref name="removed"/> counts them) - the liner under a shut hood, the back of a panel, an engine
    /// bay - while the part of a liner under a vent, or a cabin behind its glass, stays. A submesh left with nothing is
    /// left out; vertices no triangle uses any more go with it.
    /// </summary>
    public static ObjMesh WithoutHidden(ObjMesh mesh, IReadOnlyList<bool[]> exposed, out int removed)
    {
        removed = 0;
        var result = new ObjMesh();
        result.MtlLibs.AddRange(mesh.MtlLibs);
        for (int si = 0; si < mesh.SubMeshes.Count; si++)
        {
            var s = mesh.SubMeshes[si];
            var keep = exposed[si];
            var sub = new ObjSubMesh { Material = s.Material, Object = s.Object };
            var map = new Dictionary<int, int>();
            int Map(int v)
            {
                if (map.TryGetValue(v, out int i)) return i;
                i = sub.Positions.Count;
                sub.Positions.Add(s.Positions[v]);
                if (v < s.Normals.Count) sub.Normals.Add(s.Normals[v]);
                if (v < s.Uvs.Count) sub.Uvs.Add(s.Uvs[v]);
                map[v] = i;
                return i;
            }
            for (int f = 0; f < s.Faces.Count; f++)
            {
                if (f < keep.Length && !keep[f]) { removed++; continue; }
                var (a, b, c) = s.Faces[f];
                sub.Faces.Add((Map(a), Map(b), Map(c)));
            }
            if (sub.Faces.Count > 0) result.SubMeshes.Add(sub);
        }
        result.RecomputeBounds();
        return result;
    }

    /// <summary>For each submesh, in order, which of its triangles touch the air outside the model.</summary>
    /// <param name="seeThrough">Submeshes the air passes through (glass); null for none.</param>
    /// <param name="resolution">Voxels along the model's longest side: a gap narrower than one (a panel over its liner)
    /// is closed.</param>
    /// <param name="ground">The share of the model's height, from its lowest point, that counts as ground: what stands
    /// on it is not looked at from below, and a car modelled without a floor pan (most are) has its engine bay open to
    /// the air underneath - which would make everything in it exposed. 0 for something seen from every side (a plane).</param>
    public static List<bool[]> ExposedFaces(ObjMesh mesh, Func<ObjSubMesh, bool>? seeThrough = null, int resolution = 128, float ground = 0f)
    {
        var result = mesh.SubMeshes.Select(s => new bool[s.Faces.Count]).ToList();
        float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
        foreach (var s in mesh.SubMeshes)
            foreach (var p in s.Positions)
            {
                minx = MathF.Min(minx, p.X); miny = MathF.Min(miny, p.Y); minz = MathF.Min(minz, p.Z);
                maxx = MathF.Max(maxx, p.X); maxy = MathF.Max(maxy, p.Y); maxz = MathF.Max(maxz, p.Z);
            }
        if (minx > maxx) return result;
        float extent = MathF.Max(maxx - minx, MathF.Max(maxy - miny, maxz - minz));
        if (extent <= 0f) { foreach (var r in result) Array.Fill(r, true); return result; }
        float v = extent / Math.Max(8, resolution);
        // One voxel of air all round, so the flood has somewhere to start on every side.
        int nx = (int)MathF.Ceiling((maxx - minx) / v) + 3, ny = (int)MathF.Ceiling((maxy - miny) / v) + 3, nz = (int)MathF.Ceiling((maxz - minz) / v) + 3;
        float ox = minx - v, oy = miny - v, oz = minz - v;
        int Index(int x, int y, int z) => (z * ny + y) * nx + x;
        (int X, int Y, int Z) Cell(Vec3 p) => (Math.Clamp((int)((p.X - ox) / v), 0, nx - 1), Math.Clamp((int)((p.Y - oy) / v), 0, ny - 1), Math.Clamp((int)((p.Z - oz) / v), 0, nz - 1));

        // The voxels a triangle passes through: points over it half a voxel apart (and its corners).
        IEnumerable<int> Touched(Vec3 a, Vec3 b, Vec3 c)
        {
            float lab = Dist(a, b), lac = Dist(a, c), lbc = Dist(b, c);
            int n = Math.Clamp((int)MathF.Ceiling(MathF.Max(lab, MathF.Max(lac, lbc)) / (v * 0.5f)), 1, 512);
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n - i; j++)
                {
                    float u = i / (float)n, w = j / (float)n;
                    var p = new Vec3(a.X + (b.X - a.X) * u + (c.X - a.X) * w, a.Y + (b.Y - a.Y) * u + (c.Y - a.Y) * w, a.Z + (b.Z - a.Z) * u + (c.Z - a.Z) * w);
                    var (x, y, z) = Cell(p);
                    yield return Index(x, y, z);
                }
        }

        var solid = new bool[nx * ny * nz];
        // The ground: every voxel below its top (the padding row under the model included).
        if (ground > 0f)
        {
            int top = Math.Clamp((int)MathF.Ceiling((miny + ground * (maxy - miny) - oy) / v), 0, ny);
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < top; y++)
                    for (int x = 0; x < nx; x++) solid[Index(x, y, z)] = true;
        }
        for (int si = 0; si < mesh.SubMeshes.Count; si++)
        {
            var s = mesh.SubMeshes[si];
            if (seeThrough?.Invoke(s) == true) continue;
            foreach (var (a, b, c) in s.Faces)
                foreach (int i in Touched(s.Positions[a], s.Positions[b], s.Positions[c])) solid[i] = true;
        }

        // Flood the outside air in from the border.
        var air = new bool[solid.Length];
        var queue = new Queue<int>();
        void Seed(int x, int y, int z) { int i = Index(x, y, z); if (!solid[i] && !air[i]) { air[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++) { Seed(x, y, 0); Seed(x, y, nz - 1); }
        for (int x = 0; x < nx; x++)
            for (int z = 0; z < nz; z++) { Seed(x, 0, z); Seed(x, ny - 1, z); }
        for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz; z++) { Seed(0, y, z); Seed(nx - 1, y, z); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
            if (x > 0) Seed(x - 1, y, z);
            if (x < nx - 1) Seed(x + 1, y, z);
            if (y > 0) Seed(x, y - 1, z);
            if (y < ny - 1) Seed(x, y + 1, z);
            if (z > 0) Seed(x, y, z - 1);
            if (z < nz - 1) Seed(x, y, z + 1);
        }

        // A triangle is exposed when one of its voxels is outside air or has outside air beside it.
        bool NextToAir(int i)
        {
            if (air[i]) return true;
            int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
            return (x > 0 && air[i - 1]) || (x < nx - 1 && air[i + 1]) || (y > 0 && air[i - nx]) || (y < ny - 1 && air[i + nx])
                || (z > 0 && air[i - nx * ny]) || (z < nz - 1 && air[i + nx * ny]);
        }
        for (int si = 0; si < mesh.SubMeshes.Count; si++)
        {
            var s = mesh.SubMeshes[si];
            for (int f = 0; f < s.Faces.Count; f++)
            {
                var (a, b, c) = s.Faces[f];
                foreach (int i in Touched(s.Positions[a], s.Positions[b], s.Positions[c]))
                    if (NextToAir(i)) { result[si][f] = true; break; }
            }
        }
        return result;
    }

    private static float Dist(Vec3 a, Vec3 b)
    {
        float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }
}
