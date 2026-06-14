namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Procedural heightmap generation. Self-contained (no dependencies) so the editor
/// can synthesize terrain itself rather than requiring an external paint pass.
/// </summary>
public static class HeightmapGenerator
{
    /// <summary>A perfectly flat map at the given height level.</summary>
    public static Heightmap Flat(int size, ushort level = 0)
    {
        var hm = new Heightmap(size, size);
        if (level != 0) Array.Fill(hm.Samples, level);
        return hm;
    }

    /// <summary>
    /// Diamond-square fractal terrain mapped to [<paramref name="min"/>,<paramref name="max"/>].
    /// Deterministic for a given seed. (Kept as the simple entry point; <see cref="Fractal"/> adds shaping.)
    /// </summary>
    public static Heightmap DiamondSquare(int minSize, int seed = 1337, float roughness = 0.5f,
                                          ushort min = 0, ushort max = ushort.MaxValue)
        => MapTo(DiamondSquareNorm(minSize, seed, roughness), minSize, min, max);

    /// <summary>
    /// Fractal terrain with optional shaping, the generator the New Map dialog drives:
    /// <list type="bullet">
    /// <item><paramref name="peak"/> &gt; 1 sharpens ridges/peaks and widens valleys (mountains); 1 = plain fBm (hills).</item>
    /// <item><paramref name="island"/> applies a smooth radial falloff so the map sinks to <paramref name="min"/>
    /// (water) at the edges and rises inland — a sea-surrounded landmass.</item>
    /// </list>
    /// </summary>
    public static Heightmap Fractal(int minSize, int seed, float roughness, ushort min, ushort max,
                                    bool island = false, float peak = 1f)
    {
        var n = DiamondSquareNorm(minSize, seed, roughness);

        if (island)
        {
            // radial falloff: 1 at centre, 0 outside an inscribed disc, smoothstepped — multiply the field down.
            float c = (minSize - 1) * 0.5f, rmax = c;
            for (int y = 0; y < minSize; y++)
                for (int x = 0; x < minSize; x++)
                {
                    float dx = (x - c) / rmax, dy = (y - c) / rmax;
                    float d = MathF.Sqrt(dx * dx + dy * dy);                 // 0 centre .. ~1.41 corner
                    float f = 1f - SmoothStep(0.55f, 1.0f, d);              // full inland, 0 past the disc edge
                    n[y * minSize + x] *= f;
                }
            Normalize(n);   // re-stretch to 0..1 so the tallest inland point still reaches max
        }

        if (MathF.Abs(peak - 1f) > 1e-3f)
            for (int i = 0; i < n.Length; i++) n[i] = MathF.Pow(n[i], peak);

        return MapTo(n, minSize, min, max);
    }

    /// <summary>
    /// Diamond-square fractal on a (2^n+1) grid covering <paramref name="minSize"/>, cropped to
    /// minSize×minSize and normalized to [0,1]. The shared core for <see cref="DiamondSquare"/>/<see cref="Fractal"/>.
    /// </summary>
    public static float[] DiamondSquareNorm(int minSize, int seed = 1337, float roughness = 0.5f)
    {
        if (minSize < 2) throw new ArgumentOutOfRangeException(nameof(minSize));

        int n = 1;
        while (n + 1 < minSize) n <<= 1;
        int grid = n + 1;

        var rng = new Random(seed);
        var h = new float[grid * grid];
        float Get(int x, int y) => h[y * grid + x];
        void Set(int x, int y, float v) => h[y * grid + x] = v;

        Set(0, 0, (float)rng.NextDouble());
        Set(n, 0, (float)rng.NextDouble());
        Set(0, n, (float)rng.NextDouble());
        Set(n, n, (float)rng.NextDouble());

        float scale = 1f;
        for (int step = n; step > 1; step /= 2)
        {
            int half = step / 2;
            for (int y = half; y < grid; y += step)
                for (int x = half; x < grid; x += step)
                {
                    float avg = (Get(x - half, y - half) + Get(x + half, y - half) +
                                 Get(x - half, y + half) + Get(x + half, y + half)) * 0.25f;
                    Set(x, y, avg + Jitter(rng, scale));
                }
            for (int y = 0; y < grid; y += half)
            {
                int xStart = ((y / half) % 2 == 0) ? half : 0;
                for (int x = xStart; x < grid; x += step)
                {
                    float sum = 0f; int count = 0;
                    if (x - half >= 0)   { sum += Get(x - half, y); count++; }
                    if (x + half < grid) { sum += Get(x + half, y); count++; }
                    if (y - half >= 0)   { sum += Get(x, y - half); count++; }
                    if (y + half < grid) { sum += Get(x, y + half); count++; }
                    Set(x, y, sum / count + Jitter(rng, scale));
                }
            }
            scale *= MathF.Pow(2f, -roughness * 2f); // amplitude falloff per octave
        }

        // crop to requested size, normalized 0..1 over the full grid (matches the historical behaviour).
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in h) { if (v < lo) lo = v; if (v > hi) hi = v; }
        float range = MathF.Max(hi - lo, 1e-6f);
        var outN = new float[minSize * minSize];
        for (int y = 0; y < minSize; y++)
            for (int x = 0; x < minSize; x++)
                outN[y * minSize + x] = (Get(x, y) - lo) / range;
        return outN;
    }

    private static Heightmap MapTo(float[] norm, int size, ushort min, ushort max)
    {
        var hm = new Heightmap(size, size);
        int span = max - min;
        for (int i = 0; i < norm.Length; i++)
            hm.Samples[i] = (ushort)Math.Clamp(min + norm[i] * span, 0, ushort.MaxValue);
        return hm;
    }

    private static void Normalize(float[] n)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in n) { if (v < lo) lo = v; if (v > hi) hi = v; }
        float range = MathF.Max(hi - lo, 1e-6f);
        for (int i = 0; i < n.Length; i++) n[i] = (n[i] - lo) / range;
    }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Jitter(Random rng, float scale) => ((float)rng.NextDouble() * 2f - 1f) * scale;
}
