using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Formats.Con;

/// <summary>One randomly-placed object: which template, where (world, Y on the terrain), its yaw, and a
/// per-object scale (1 = no variation; the caller applies it as a ScaleObject when it differs from 1).</summary>
public readonly record struct ScatterPlacement(string Template, Vec3 Position, float Yaw, float Scale = 1f);

/// <summary>
/// Randomly scatters objects (vegetation, buildings, props…) across a map's terrain. Pure + deterministic for a
/// given seed; the caller supplies the candidate template names and a terrain height function, and applies the
/// returned placements as edit commands. Placement is rejection-sampled against per-call constraints: stays above
/// water (with clearance), within a slope band (so nothing lands on a cliff or, optionally, dead-flat ground), and
/// keeps a minimum spacing from already-placed objects.
/// </summary>
public static class ObjectScatter
{
    public static List<ScatterPlacement> Scatter(
        IReadOnlyList<string> candidates, TerrainConfig cfg, Func<float, float, float> heightAt,
        int count, float minSlopeDeg, float maxSlopeDeg, bool avoidWater, float waterClearance,
        float minSpacing, int seed, float edgeMargin = 0f, float minScale = 1f, float maxScale = 1f)
    {
        var result = new List<ScatterPlacement>();
        if (candidates.Count == 0 || count <= 0) return result;

        var rng = new Random(seed);
        float ws = cfg.WorldSize;
        float water = cfg.WaterLevel;
        float step = MathF.Max(cfg.HorizontalSpacing, 1f);     // slope sample distance
        float lo = Math.Clamp(edgeMargin, 0f, ws * 0.49f), hi = ws - lo;
        float minSp2 = minSpacing * minSpacing;
        int maxAttempts = Math.Max(count * 40, 2000);

        for (int attempt = 0; attempt < maxAttempts && result.Count < count; attempt++)
        {
            float x = lo + (float)rng.NextDouble() * (hi - lo);
            float z = lo + (float)rng.NextDouble() * (hi - lo);
            float h = heightAt(x, z);

            if (avoidWater && h < water + waterClearance) continue;

            // slope (degrees) from a central finite difference of the terrain height.
            float gx = (heightAt(MathF.Min(x + step, ws), z) - heightAt(MathF.Max(x - step, 0f), z)) / (2f * step);
            float gz = (heightAt(x, MathF.Min(z + step, ws)) - heightAt(x, MathF.Max(z - step, 0f))) / (2f * step);
            float slopeDeg = MathF.Atan(MathF.Sqrt(gx * gx + gz * gz)) * 180f / MathF.PI;
            if (slopeDeg < minSlopeDeg || slopeDeg > maxSlopeDeg) continue;

            if (minSpacing > 0f)
            {
                bool tooClose = false;
                foreach (var p in result)
                {
                    float dx = p.Position.X - x, dz = p.Position.Z - z;
                    if (dx * dx + dz * dz < minSp2) { tooClose = true; break; }
                }
                if (tooClose) continue;
            }

            var template = candidates[rng.Next(candidates.Count)];
            float yaw = (float)(rng.NextDouble() * 360.0);     // BFV rotation Euler: X = yaw
            float scale = maxScale > minScale ? minScale + (float)rng.NextDouble() * (maxScale - minScale) : minScale;
            result.Add(new ScatterPlacement(template, new Vec3(x, h, z), yaw, scale));
        }
        return result;
    }
}
