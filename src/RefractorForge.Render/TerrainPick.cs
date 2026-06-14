using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Ray-vs-terrain "ground pick" used when placing objects: marches a screen ray against the heightmap
/// — bilinearly sampled, in the exact world mapping <see cref="TerrainMesh"/> uses (x = col*spacing,
/// y = HeightToMeters(sample), z = row*spacing) — and returns the surface point the ray first crosses.
/// Pure and GPU-free, so it can be unit-tested against a real level.
/// </summary>
public sealed class TerrainPick
{
    private readonly Heightmap _hm;
    private readonly TerrainConfig _cfg;
    private readonly float _sp;

    public TerrainPick(Heightmap hm, TerrainConfig cfg) { _hm = hm; _cfg = cfg; _sp = cfg.HorizontalSpacing; }

    public float MaxX => (_hm.Width - 1) * _sp;
    public float MaxZ => (_hm.Height - 1) * _sp;

    /// <summary>Bilinearly-interpolated terrain height (metres) at world (wx,wz); clamps to map bounds.</summary>
    public float HeightAt(float wx, float wz)
    {
        float fx = Math.Clamp(wx / _sp, 0f, _hm.Width - 1.001f);
        float fz = Math.Clamp(wz / _sp, 0f, _hm.Height - 1.001f);
        int x0 = (int)fx, z0 = (int)fz;
        int x1 = Math.Min(x0 + 1, _hm.Width - 1), z1 = Math.Min(z0 + 1, _hm.Height - 1);
        float tx = fx - x0, tz = fz - z0;
        float h00 = _cfg.HeightToMeters(_hm[x0, z0]), h10 = _cfg.HeightToMeters(_hm[x1, z0]);
        float h01 = _cfg.HeightToMeters(_hm[x0, z1]), h11 = _cfg.HeightToMeters(_hm[x1, z1]);
        float a = h00 + (h10 - h00) * tx, b = h01 + (h11 - h01) * tx;
        return a + (b - a) * tz;
    }

    private bool InBounds(float x, float z) => x >= 0f && z >= 0f && x <= MaxX && z <= MaxZ;

    /// <summary>March the ray and return the first terrain-surface crossing inside the map, else false.</summary>
    public bool Raycast(Ray ray, out Vector3 hit)
    {
        hit = default;
        var dir = Vector3.Normalize(ray.Dir);
        var o = ray.Origin;
        float step = _sp * 0.5f;
        float maxDist = (MaxX + MaxZ) * 1.5f + 2000f;
        float prev = o.Y - HeightAt(o.X, o.Z);          // > 0 = above ground
        for (float t = step; t <= maxDist; t += step)
        {
            var p = o + dir * t;
            float diff = p.Y - HeightAt(p.X, p.Z);
            if (diff <= 0f && prev > 0f)                 // crossed from above to below the surface
            {
                float lo = t - step, hi = t;            // binary-refine the crossing
                for (int b = 0; b < 24; b++)
                {
                    float mid = 0.5f * (lo + hi);
                    var pm = o + dir * mid;
                    if (pm.Y - HeightAt(pm.X, pm.Z) <= 0f) hi = mid; else lo = mid;
                }
                var ph = o + dir * hi;
                if (!InBounds(ph.X, ph.Z)) return false; // crossing fell outside the map (clamped edge)
                hit = ph;
                return true;
            }
            prev = diff;
            if (p.Y < -2000f) break;                     // ray heading down below everything
        }
        return false;
    }
}
