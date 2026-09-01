namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Weathering for terrain that was sculpted by hand: thermal erosion knocks steep faces down into scree, and a
/// particle-based hydraulic pass cuts gullies where water would run. Together they turn a lump into a hill.
///
/// Works on a float copy of a rectangle and hands back the result; the editor writes it through a terrain stroke
/// so it is one undo step and one collaboration broadcast like any other sculpt.
/// </summary>
public static class Erosion
{
    public sealed class Params
    {
        public int Iterations { get; init; } = 40;
        /// <summary>Thermal: slopes steeper than this (rise per cell, metres) shed material.</summary>
        public float TalusMeters { get; init; } = 1.2f;
        /// <summary>Thermal: fraction of the excess moved per iteration.</summary>
        public float ThermalRate { get; init; } = 0.25f;
        /// <summary>Hydraulic: droplets per cell of the rectangle.</summary>
        public float DropsPerCell { get; init; } = 0.6f;
        public int DropLife { get; init; } = 40;
        public float Erode { get; init; } = 0.06f;      // capacity scale
        public float Deposit { get; init; } = 0.04f;
        public float Inertia { get; init; } = 0.3f;
        public int Seed { get; init; } = 1;
        public bool Thermal { get; init; } = true;
        public bool Hydraulic { get; init; } = true;
    }

    /// <summary>
    /// Erode the rectangle [x0, x0+w) x [y0, y0+h) of the heightmap. Returns the new heights in METRES, row-major,
    /// w*h long. The border ring of the rectangle is pinned so the edit blends into the untouched ground.
    /// </summary>
    public static float[] Run(Heightmap hm, TerrainConfig cfg, int x0, int y0, int w, int h, Params p)
    {
        x0 = Math.Clamp(x0, 0, hm.Width - 1); y0 = Math.Clamp(y0, 0, hm.Height - 1);
        w = Math.Clamp(w, 3, hm.Width - x0); h = Math.Clamp(h, 3, hm.Height - y0);

        var height = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                height[y * w + x] = cfg.HeightToMeters(hm[x0 + x, y0 + y]);
        var pinned = (float[])height.Clone();

        if (p.Thermal) ThermalPass(height, w, h, p);
        if (p.Hydraulic) HydraulicPass(height, w, h, p);

        // Pin the ring so the patch meets its surroundings without a step.
        for (int x = 0; x < w; x++) { height[x] = pinned[x]; height[(h - 1) * w + x] = pinned[(h - 1) * w + x]; }
        for (int y = 0; y < h; y++) { height[y * w] = pinned[y * w]; height[y * w + w - 1] = pinned[y * w + w - 1]; }
        return height;
    }

    private static void ThermalPass(float[] hgt, int w, int h, Params p)
    {
        var delta = new float[w * h];
        for (int it = 0; it < p.Iterations; it++)
        {
            Array.Clear(delta);
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    float here = hgt[i];
                    // Material moves to whichever neighbours are far enough below, proportionally.
                    float total = 0f;
                    Span<float> d = stackalloc float[4];
                    Span<int> n = stackalloc int[4] { i - 1, i + 1, i - w, i + w };
                    for (int k = 0; k < 4; k++)
                    {
                        float diff = here - hgt[n[k]] - p.TalusMeters;
                        d[k] = diff > 0f ? diff : 0f;
                        total += d[k];
                    }
                    if (total <= 0f) continue;
                    float move = total * p.ThermalRate * 0.5f;
                    delta[i] -= move;
                    for (int k = 0; k < 4; k++) if (d[k] > 0f) delta[n[k]] += move * (d[k] / total);
                }
            for (int i = 0; i < hgt.Length; i++) hgt[i] += delta[i];
        }
    }

    /// <summary>
    /// Droplets: each starts somewhere random, follows the gradient, picks up sediment while it accelerates and
    /// drops it as it slows. Gullies appear where many droplets share a path. The classic scheme; parameters are
    /// modest so a pass reads as weathering rather than as a canyon generator.
    /// </summary>
    private static void HydraulicPass(float[] hgt, int w, int h, Params p)
    {
        var rng = new Random(p.Seed);
        int drops = (int)(w * h * p.DropsPerCell);
        for (int d = 0; d < drops; d++)
        {
            float px = 1f + (float)rng.NextDouble() * (w - 3);
            float py = 1f + (float)rng.NextDouble() * (h - 3);
            float dx = 0f, dy = 0f, speed = 1f, water = 1f, sediment = 0f;

            for (int life = 0; life < p.DropLife; life++)
            {
                int ix = (int)px, iy = (int)py;
                if (ix < 1 || iy < 1 || ix >= w - 2 || iy >= h - 2) break;
                float fx = px - ix, fy = py - iy;

                // Bilinear height and gradient at the droplet.
                int i = iy * w + ix;
                float h00 = hgt[i], h10 = hgt[i + 1], h01 = hgt[i + w], h11 = hgt[i + w + 1];
                float gx = (h10 - h00) * (1 - fy) + (h11 - h01) * fy;
                float gy = (h01 - h00) * (1 - fx) + (h11 - h10) * fx;
                float hOld = h00 * (1 - fx) * (1 - fy) + h10 * fx * (1 - fy) + h01 * (1 - fx) * fy + h11 * fx * fy;

                dx = dx * p.Inertia - gx * (1 - p.Inertia);
                dy = dy * p.Inertia - gy * (1 - p.Inertia);
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-5f) { dx = (float)rng.NextDouble() - 0.5f; dy = (float)rng.NextDouble() - 0.5f; len = MathF.Sqrt(dx * dx + dy * dy); }
                dx /= len; dy /= len;
                float nx = px + dx, ny = py + dy;
                if (nx < 1 || ny < 1 || nx >= w - 2 || ny >= h - 2) break;

                int jx = (int)nx, jy = (int)ny;
                float gfx = nx - jx, gfy = ny - jy;
                int j = jy * w + jx;
                float hNew = hgt[j] * (1 - gfx) * (1 - gfy) + hgt[j + 1] * gfx * (1 - gfy) + hgt[j + w] * (1 - gfx) * gfy + hgt[j + w + 1] * gfx * gfy;
                float dh = hNew - hOld;

                float capacity = MathF.Max(-dh, 0.01f) * speed * water * p.Erode * 4f;
                if (sediment > capacity || dh > 0f)
                {
                    // Slowing, or ran uphill: drop sediment where we are.
                    float amt = dh > 0f ? MathF.Min(dh, sediment) : (sediment - capacity) * p.Deposit;
                    sediment -= amt;
                    Splat(hgt, w, ix, iy, fx, fy, amt);
                }
                else
                {
                    float amt = MathF.Min((capacity - sediment) * p.Erode, -dh);
                    sediment += amt;
                    Splat(hgt, w, ix, iy, fx, fy, -amt);
                }

                speed = MathF.Sqrt(MathF.Max(speed * speed + dh * -9.81f * 0.02f, 0f));
                water *= 0.985f;
                px = nx; py = ny;
                if (water < 0.05f) break;
            }
        }
    }

    private static void Splat(float[] hgt, int w, int ix, int iy, float fx, float fy, float amt)
    {
        int i = iy * w + ix;
        hgt[i] += amt * (1 - fx) * (1 - fy);
        hgt[i + 1] += amt * fx * (1 - fy);
        hgt[i + w] += amt * (1 - fx) * fy;
        hgt[i + w + 1] += amt * fx * fy;
    }
}
