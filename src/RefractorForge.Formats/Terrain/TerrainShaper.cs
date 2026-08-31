namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Terrain edits that PREPARE ground rather than invent it: level a build pad, take the lumps out of a slope, cut a
/// channel for a road. <see cref="MountainGenerator"/> adds landscape; these make existing landscape usable.
///
/// All of them share the shape that makes an edit blend: a core that is fully changed, a skirt where the change
/// eases back to whatever was there, and nothing at all beyond the radius. Without the skirt a flattened pad is a
/// mesa with vertical sides, which is exactly what makes hand-levelled terrain look hand-levelled.
///
/// Pure and deterministic, and each returns the heightmap rect it touched so the caller can ship precisely that
/// over the wire.
/// </summary>
public static class TerrainShaper
{
    /// <summary>Level a disc to one height, easing back to the original ground across the outer part of the radius.
    /// </summary>
    /// <param name="targetHeight">Metres, or null to use the mean height of the core — which is what "flatten this
    /// spot" almost always means, and avoids the caller having to sample first.</param>
    /// <param name="skirt">Fraction of the radius spent easing back out, 0..1. 0.35 reads as a graded pad.</param>
    public static (int X0, int Y0, int W, int H) Flatten(
        Heightmap hm, TerrainConfig cfg, float cx, float cz, float radius, float? targetHeight = null,
        float skirt = 0.35f)
    {
        if (radius <= 0f) return (0, 0, 0, 0);
        float sp = Spacing(cfg), yScale = YScale(cfg);
        skirt = Math.Clamp(skirt, 0f, 0.95f);
        float inner = radius * (1f - skirt);

        var (x0, y0, x1, y1) = Box(hm, cfg, cx, cz, radius);
        if (x1 < x0 || y1 < y0) return (0, 0, 0, 0);

        // The target: the mean of the core, so the pad settles into the ground rather than sitting on top of it.
        float target;
        if (targetHeight is { } t) target = t;
        else
        {
            double sum = 0; int n = 0;
            for (int gy = y0; gy <= y1; gy++)
                for (int gx = x0; gx <= x1; gx++)
                {
                    float dx = gx * sp - cx, dz = gy * sp - cz;
                    if (dx * dx + dz * dz > inner * inner) continue;
                    sum += hm[gx, gy] * yScale / 256f; n++;
                }
            if (n == 0) return (0, 0, 0, 0);
            target = (float)(sum / n);
        }

        for (int gy = y0; gy <= y1; gy++)
            for (int gx = x0; gx <= x1; gx++)
            {
                float dx = gx * sp - cx, dz = gy * sp - cz;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (d > radius) continue;

                // 1 in the core, easing to 0 at the rim.
                float w = d <= inner ? 1f : 1f - (d - inner) / MathF.Max(radius - inner, 1e-3f);
                w = Math.Clamp(w, 0f, 1f);
                w = w * w * (3f - 2f * w);

                float cur = hm[gx, gy] * yScale / 256f;
                Write(hm, cfg, gx, gy, cur + (target - cur) * w);
            }

        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Average each point with its neighbours, so lumps and stair-stepping settle out. Several light
    /// passes look better than one heavy one, which is why passes is separate from strength.</summary>
    public static (int X0, int Y0, int W, int H) Smooth(
        Heightmap hm, TerrainConfig cfg, float cx, float cz, float radius, int passes = 2, float strength = 1f)
    {
        if (radius <= 0f || passes <= 0) return (0, 0, 0, 0);
        float sp = Spacing(cfg), yScale = YScale(cfg);
        strength = Math.Clamp(strength, 0f, 1f);

        var (x0, y0, x1, y1) = Box(hm, cfg, cx, cz, radius);
        if (x1 < x0 || y1 < y0) return (0, 0, 0, 0);

        for (int pass = 0; pass < passes; pass++)
        {
            // Read from a snapshot so a pass cannot smear its own output across the disc.
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            var src = new ushort[w * h];
            for (int gy = y0; gy <= y1; gy++)
                for (int gx = x0; gx <= x1; gx++)
                    src[(gy - y0) * w + (gx - x0)] = hm[gx, gy];

            ushort At(int gx, int gy)
            {
                int lx = Math.Clamp(gx - x0, 0, w - 1), ly = Math.Clamp(gy - y0, 0, h - 1);
                return src[ly * w + lx];
            }

            for (int gy = y0; gy <= y1; gy++)
                for (int gx = x0; gx <= x1; gx++)
                {
                    float dx = gx * sp - cx, dz = gy * sp - cz;
                    float d = MathF.Sqrt(dx * dx + dz * dz);
                    if (d > radius) continue;

                    float fall = 1f - d / radius;                 // strongest in the middle, nothing at the rim
                    fall = fall * fall * (3f - 2f * fall);

                    int sum = 0, n = 0;
                    for (int oy = -1; oy <= 1; oy++)
                        for (int ox = -1; ox <= 1; ox++) { sum += At(gx + ox, gy + oy); n++; }
                    float avg = (float)sum / n * yScale / 256f;
                    float cur = At(gx, gy) * yScale / 256f;

                    Write(hm, cfg, gx, gy, cur + (avg - cur) * fall * strength);
                }
        }

        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Cut a channel along a path — a pass for a road through a ridge, or a wadi. Depth is measured from
    /// the ground the channel starts under, so it follows the lie of the land rather than digging to a flat plane.
    /// </summary>
    public static (int X0, int Y0, int W, int H) CarveChannel(
        Heightmap hm, TerrainConfig cfg, IReadOnlyList<(float X, float Z)> path, float width, float depth,
        float skirt = 0.5f)
    {
        if (path.Count < 2 || width <= 0f) return (0, 0, 0, 0);
        float sp = Spacing(cfg), yScale = YScale(cfg);
        float half = width * 0.5f;
        skirt = Math.Clamp(skirt, 0f, 0.95f);
        float inner = half * (1f - skirt);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var (px, pz) in path)
        {
            minX = MathF.Min(minX, px); maxX = MathF.Max(maxX, px);
            minZ = MathF.Min(minZ, pz); maxZ = MathF.Max(maxZ, pz);
        }
        int x0 = Clamp((minX - half - sp) / sp, hm.Width), y0 = Clamp((minZ - half - sp) / sp, hm.Height);
        int x1 = Clamp((maxX + half + sp) / sp, hm.Width), y1 = Clamp((maxZ + half + sp) / sp, hm.Height);
        if (x1 < x0 || y1 < y0) return (0, 0, 0, 0);

        for (int gy = y0; gy <= y1; gy++)
            for (int gx = x0; gx <= x1; gx++)
            {
                float x = gx * sp, z = gy * sp;
                float best = float.MaxValue;
                for (int i = 1; i < path.Count; i++)
                {
                    var (ax, az) = path[i - 1]; var (bx, bz) = path[i];
                    float ex = bx - ax, ez = bz - az;
                    float len2 = ex * ex + ez * ez;
                    float t = len2 > 1e-6f ? Math.Clamp(((x - ax) * ex + (z - az) * ez) / len2, 0f, 1f) : 0f;
                    float qx = ax + ex * t, qz = az + ez * t;
                    float d2 = (x - qx) * (x - qx) + (z - qz) * (z - qz);
                    if (d2 < best) best = d2;
                }
                float d = MathF.Sqrt(best);
                if (d > half) continue;

                float w = d <= inner ? 1f : 1f - (d - inner) / MathF.Max(half - inner, 1e-3f);
                w = Math.Clamp(w, 0f, 1f);
                w = w * w * (3f - 2f * w);

                float cur = hm[gx, gy] * yScale / 256f;
                Write(hm, cfg, gx, gy, cur - depth * w);
            }

        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    // ---- shared helpers ----

    private static float Spacing(TerrainConfig c) => c.HorizontalSpacing <= 0 ? 1f : c.HorizontalSpacing;
    private static float YScale(TerrainConfig c) => c.YScale <= 0 ? 1f : c.YScale;
    private static int Clamp(float v, int n) => Math.Clamp((int)MathF.Round(v), 0, n - 1);

    private static (int X0, int Y0, int X1, int Y1) Box(Heightmap hm, TerrainConfig cfg, float cx, float cz, float radius)
    {
        float sp = Spacing(cfg);
        return (Clamp((cx - radius - sp) / sp, hm.Width), Clamp((cz - radius - sp) / sp, hm.Height),
                Clamp((cx + radius + sp) / sp, hm.Width), Clamp((cz + radius + sp) / sp, hm.Height));
    }

    private static void Write(Heightmap hm, TerrainConfig cfg, int gx, int gy, float metres)
    {
        float yScale = YScale(cfg);
        float max = 65535f * yScale / 256f;
        hm[gx, gy] = (ushort)Math.Clamp(MathF.Round(Math.Clamp(metres, 0f, max) * 256f / yScale), 0f, 65535f);
    }
}
