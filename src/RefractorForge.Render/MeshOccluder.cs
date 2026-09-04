using System;
using System.Collections.Generic;
using System.Numerics;

namespace RefractorForge.Render;

/// <summary>
/// Self-shadowing for the object lightmap bake: does a ray from a point on the surface, toward the sun, hit the
/// object's own mesh? The engine's lightmap is a sun-visibility mask (its shader multiplies the sun term by it), and
/// the retail generator ray-traces it, so a ceiling under a roof or the inside of a bunker has to come out dark.
/// A uniform grid over the mesh's world bounds keeps it to a few dozen triangle tests per texel.
/// </summary>
public sealed class MeshOccluder
{
    private readonly Vector3[] _a, _e1, _e2;
    private readonly Vector3 _min, _max, _cellSize;
    private readonly int _nx, _ny, _nz;
    private readonly int[][] _cells;
    private readonly int[] _stamp;
    private int _ray;

    public int TriangleCount => _a.Length;

    public static MeshOccluder? Build(IReadOnlyList<(Vector3 a, Vector3 b, Vector3 c)> tris)
        => tris.Count == 0 ? null : new MeshOccluder(tris);

    private MeshOccluder(IReadOnlyList<(Vector3 a, Vector3 b, Vector3 c)> tris)
    {
        int n = tris.Count;
        _a = new Vector3[n]; _e1 = new Vector3[n]; _e2 = new Vector3[n];
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            var (a, b, c) = tris[i];
            _a[i] = a; _e1[i] = b - a; _e2[i] = c - a;
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        var pad = Vector3.Max((max - min) * 0.01f, new Vector3(0.05f));
        _min = min - pad; _max = max + pad;
        var ext = _max - _min;
        int res = Math.Clamp((int)MathF.Ceiling(MathF.Cbrt(n) * 1.5f), 2, 48);
        float cell = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)) / res;
        _nx = Math.Max(1, (int)MathF.Ceiling(ext.X / cell));
        _ny = Math.Max(1, (int)MathF.Ceiling(ext.Y / cell));
        _nz = Math.Max(1, (int)MathF.Ceiling(ext.Z / cell));
        _cellSize = new Vector3(ext.X / _nx, ext.Y / _ny, ext.Z / _nz);

        var lists = new List<int>?[_nx * _ny * _nz];
        for (int i = 0; i < n; i++)
        {
            var (a, b, c) = tris[i];
            var (x0, y0, z0) = Cell(Vector3.Min(a, Vector3.Min(b, c)));
            var (x1, y1, z1) = Cell(Vector3.Max(a, Vector3.Max(b, c)));
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        (lists[Index(x, y, z)] ??= new List<int>()).Add(i);
        }
        _cells = new int[lists.Length][];
        for (int i = 0; i < lists.Length; i++) _cells[i] = lists[i]?.ToArray() ?? Array.Empty<int>();
        _stamp = new int[n];
    }

    private (int, int, int) Cell(Vector3 p) => (
        Math.Clamp((int)((p.X - _min.X) / _cellSize.X), 0, _nx - 1),
        Math.Clamp((int)((p.Y - _min.Y) / _cellSize.Y), 0, _ny - 1),
        Math.Clamp((int)((p.Z - _min.Z) / _cellSize.Z), 0, _nz - 1));

    private int Index(int x, int y, int z) => (z * _ny + y) * _nx + x;

    private static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>True when the ray from <paramref name="origin"/> along <paramref name="dir"/> (normalised) hits the
    /// mesh. It starts <paramref name="skip"/> along the ray, so the surface the point sits on is not its own
    /// occluder. Not thread-safe: one occluder per bake.</summary>
    public bool Occluded(Vector3 origin, Vector3 dir, float skip = 0.02f)
    {
        origin += dir * skip;
        float tEnter = 0f, tExit = float.MaxValue;
        for (int ax = 0; ax < 3; ax++)
        {
            float o = Comp(origin, ax), d = Comp(dir, ax), lo = Comp(_min, ax), hi = Comp(_max, ax);
            if (MathF.Abs(d) < 1e-9f) { if (o < lo || o > hi) return false; continue; }
            float t0 = (lo - o) / d, t1 = (hi - o) / d;
            if (t0 > t1) (t0, t1) = (t1, t0);
            tEnter = MathF.Max(tEnter, t0); tExit = MathF.Min(tExit, t1);
            if (tEnter > tExit) return false;
        }

        // Amanatides & Woo: walk the cells the ray crosses, testing each cell's triangles once per ray.
        var p = origin + dir * (tEnter + 1e-4f);
        var (x, y, z) = Cell(p);
        Step(dir.X, origin.X, _min.X, _cellSize.X, x, out int sx, out float tMaxX, out float tDeltaX);
        Step(dir.Y, origin.Y, _min.Y, _cellSize.Y, y, out int sy, out float tMaxY, out float tDeltaY);
        Step(dir.Z, origin.Z, _min.Z, _cellSize.Z, z, out int sz, out float tMaxZ, out float tDeltaZ);
        _ray++;
        while (true)
        {
            foreach (int ti in _cells[Index(x, y, z)])
            {
                if (_stamp[ti] == _ray) continue;
                _stamp[ti] = _ray;
                if (Hit(ti, origin, dir)) return true;
            }
            if (tMaxX < tMaxY && tMaxX < tMaxZ) { x += sx; if (x < 0 || x >= _nx) return false; tMaxX += tDeltaX; }
            else if (tMaxY < tMaxZ)             { y += sy; if (y < 0 || y >= _ny) return false; tMaxY += tDeltaY; }
            else                                { z += sz; if (z < 0 || z >= _nz) return false; tMaxZ += tDeltaZ; }
        }
    }

    private static void Step(float d, float o, float lo, float size, int cell, out int step, out float tMax, out float tDelta)
    {
        if (MathF.Abs(d) < 1e-9f) { step = 0; tMax = float.MaxValue; tDelta = float.MaxValue; return; }
        step = d > 0 ? 1 : -1;
        float boundary = lo + (cell + (d > 0 ? 1 : 0)) * size;
        tMax = (boundary - o) / d;
        tDelta = size / MathF.Abs(d);
    }

    // Moller-Trumbore; any hit ahead of the origin counts.
    private bool Hit(int i, Vector3 o, Vector3 d)
    {
        var pvec = Vector3.Cross(d, _e2[i]);
        float det = Vector3.Dot(_e1[i], pvec);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var tvec = o - _a[i];
        float u = Vector3.Dot(tvec, pvec) * inv;
        if (u < 0f || u > 1f) return false;
        var qvec = Vector3.Cross(tvec, _e1[i]);
        float v = Vector3.Dot(d, qvec) * inv;
        if (v < 0f || u + v > 1f) return false;
        return Vector3.Dot(_e2[i], qvec) * inv > 1e-4f;
    }
}
