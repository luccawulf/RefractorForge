using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Formats.Con;

/// <summary>The result of a procedural city: the building placements (reusing <see cref="ScatterPlacement"/> so the
/// caller applies them exactly like a scatter) and the street centerlines as world-space polylines whose Y rides the
/// terrain. The Render/Viewer layer turns each road polyline into an oriented texture sweep (RoadSpline +
/// AtlasPaint.SweepOriented); Formats stays Render-free, so roads are plain <see cref="Vec3"/> control points here.</summary>
public sealed class CityLayout
{
    public List<ScatterPlacement> Buildings { get; } = new();
    public List<List<Vec3>> Roads { get; } = new();
    public float RoadWidth { get; init; }
    public int BlocksX { get; set; }
    public int BlocksZ { get; set; }
}

/// <summary>
/// A from-scratch procedural city generator: lays a rectangular STREET GRID over a map area, then lines each city
/// block with buildings set back from the streets, snapped to the terrain, rejecting water and cliffs. Pure and
/// deterministic for a given seed (only the template/scale pick is randomized — the grid itself is fixed), so it
/// gates headlessly in the Demo harness just like <see cref="ObjectScatter"/>. The caller supplies a building
/// template palette + a terrain height function; the generator is game/mod-agnostic.
/// </summary>
public static class CityGenerator
{
    /// <summary>
    /// Generate a grid city inside the world-space rectangle [<paramref name="minX"/>,<paramref name="maxX"/>] x
    /// [<paramref name="minZ"/>,<paramref name="maxZ"/>] (metres). <paramref name="blockSize"/> is the street-grid
    /// cell; buildings sit <paramref name="setback"/> m inside each block edge (clear of the <paramref name="roadWidth"/>
    /// street) and are spaced <paramref name="lotWidth"/> m along the edge, each yaw-facing its street. Placements that
    /// fall under water (+<paramref name="waterClearance"/>), on slopes steeper than <paramref name="maxSlopeDeg"/>, or
    /// within <paramref name="minSpacing"/> m of an earlier one are dropped.
    /// </summary>
    public static CityLayout Generate(
        float minX, float minZ, float maxX, float maxZ,
        TerrainConfig cfg, Func<float, float, float> heightAt,
        IReadOnlyList<string> buildingTemplates, int seed,
        float blockSize = 64f, float roadWidth = 8f, float setback = 4f,
        float lotWidth = 16f, float minSpacing = 10f,
        float maxSlopeDeg = 18f, bool avoidWater = true, float waterClearance = 0.5f,
        float minScale = 1f, float maxScale = 1f)
    {
        var layout = new CityLayout { RoadWidth = roadWidth };
        if (buildingTemplates.Count == 0) return layout;

        // Clamp the area to the world and normalise.
        float ws = cfg.WorldSize;
        float x0 = Math.Clamp(MathF.Min(minX, maxX), 0f, ws), x1 = Math.Clamp(MathF.Max(minX, maxX), 0f, ws);
        float z0 = Math.Clamp(MathF.Min(minZ, maxZ), 0f, ws), z1 = Math.Clamp(MathF.Max(minZ, maxZ), 0f, ws);
        blockSize = MathF.Max(blockSize, 8f);
        lotWidth = MathF.Max(lotWidth, 1f);
        if (x1 - x0 < blockSize || z1 - z0 < blockSize) return layout;

        var rng = new Random(seed);
        float slopeStep = MathF.Max(cfg.HorizontalSpacing, 1f);
        float minSp2 = minSpacing * minSpacing;
        float waterY = cfg.WaterLevel + waterClearance;

        int nx = Math.Max(1, (int)((x1 - x0) / blockSize));
        int nz = Math.Max(1, (int)((z1 - z0) / blockSize));
        layout.BlocksX = nx; layout.BlocksZ = nz;

        // ---- Street centerlines: the grid lines, sampled so their height follows the terrain. ----
        float roadSampleStep = MathF.Min(blockSize, 16f);
        for (int i = 0; i <= nx; i++)
        {
            float gx = x0 + i * blockSize; if (gx > x1) gx = x1;
            layout.Roads.Add(SampleLine(gx, z0, gx, z1, roadSampleStep, heightAt));
        }
        for (int j = 0; j <= nz; j++)
        {
            float gz = z0 + j * blockSize; if (gz > z1) gz = z1;
            layout.Roads.Add(SampleLine(x0, gz, x1, gz, roadSampleStep, heightAt));
        }

        // ---- Buildings: a ring inside each block, one row per street edge, facing the street. ----
        float inset = roadWidth * 0.5f + setback;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                float bx0 = x0 + i * blockSize, bx1 = MathF.Min(bx0 + blockSize, x1);
                float bz0 = z0 + j * blockSize, bz1 = MathF.Min(bz0 + blockSize, z1);
                float rx0 = bx0 + inset, rx1 = bx1 - inset;
                float rz0 = bz0 + inset, rz1 = bz1 - inset;
                if (rx1 - rx0 < lotWidth || rz1 - rz0 < lotWidth) continue;   // block too small to line

                // South edge (low z, faces -Z) and north edge (high z, faces +Z): walk along X.
                for (float x = rx0 + lotWidth * 0.5f; x <= rx1; x += lotWidth)
                {
                    TryPlace(x, rz0, 0f);     // facing the street to the south
                    TryPlace(x, rz1, 180f);   // facing the street to the north
                }
                // West edge (low x, faces -X) and east edge (high x, faces +X): walk along Z, skipping the
                // corners already covered by the N/S rows.
                for (float z = rz0 + lotWidth * 1.5f; z <= rz1 - lotWidth * 0.5f; z += lotWidth)
                {
                    TryPlace(rx0, z, 90f);
                    TryPlace(rx1, z, 270f);
                }
            }

        return layout;

        void TryPlace(float x, float z, float yaw)
        {
            if (avoidWater && heightAt(x, z) < waterY) return;
            if (ObjectScatter.SlopeDegrees(heightAt, x, z, slopeStep, ws) > maxSlopeDeg) return;
            foreach (var p in layout.Buildings)
            {
                float dx = p.Position.X - x, dz = p.Position.Z - z;
                if (dx * dx + dz * dz < minSp2) return;
            }
            var template = buildingTemplates[rng.Next(buildingTemplates.Count)];
            float scale = maxScale > minScale ? minScale + (float)rng.NextDouble() * (maxScale - minScale) : minScale;
            layout.Buildings.Add(new ScatterPlacement(template, new Vec3(x, heightAt(x, z), z), yaw, scale));
        }
    }

    /// <summary>A straight road centerline from (ax,az) to (bx,bz) sampled every <paramref name="step"/> m, with each
    /// control point's Y read from the terrain so the road grades over hills.</summary>
    private static List<Vec3> SampleLine(float ax, float az, float bx, float bz, float step, Func<float, float, float> heightAt)
    {
        var pts = new List<Vec3>();
        float len = MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        int n = Math.Max(1, (int)MathF.Ceiling(len / MathF.Max(step, 1f)));
        for (int k = 0; k <= n; k++)
        {
            float t = (float)k / n;
            float x = ax + (bx - ax) * t, z = az + (bz - az) * t;
            pts.Add(new Vec3(x, heightAt(x, z), z));
        }
        return pts;
    }
}
