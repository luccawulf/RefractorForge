using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>Which terrain property drives a two-texture blend (Editor42's "Height selector" / "Slope selector").</summary>
public enum LayerSelector { Height, Slope }

/// <summary>
/// One Editor42-style terrain texture LAYER: two tileable source textures ("1st layer" A, "2nd layer" B) blended
/// across a threshold band of a terrain property (height in metres, or slope in degrees), with optional fractal
/// "noise gradation" breaking up the seam so the transition reads natural instead of a hard contour. Pure CPU and
/// engine-agnostic — the viewer bakes the result into the visible terrain atlas and saves it back as the level's
/// txCxR.dds tiles, exactly like a hand-painted stroke.
/// </summary>
public sealed class TextureLayerSpec
{
    public LayerSelector Selector = LayerSelector.Height;
    public float ThresholdLow = 20f;       // below this -> 100% A. Height: metres; Slope: degrees.
    public float ThresholdHigh = 60f;      // above this -> 100% B.
    public bool NoiseOn = true;
    public int Seed = 2300;
    public int FirstOctave = 2;            // coarsest noise octave (2^oct cells across the world)
    public int OctaveCount = 6;            // how many octaves of detail are summed
    public float ThresholdWidth = 0.35f;   // 0..2 — how far the noise can push the A/B boundary (× band width)
    public float TileMetersA = 8f;         // world metres per repeat of texture A
    public float TileMetersB = 8f;         // world metres per repeat of texture B
}

/// <summary>Bakes <see cref="TextureLayerSpec"/> layers into a terrain atlas + the fractal-noise / height / slope
/// math behind them. All static + deterministic (seeded), so it round-trips in the headless Demo harness.</summary>
public static class TerrainTextureLayer
{
    // ---- seeded fractal value-noise (no lattice arrays; hash-based so it works at any world coordinate) ----
    private static float Hash01(int x, int y, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + seed * 982451653;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x7fffffff) / (float)0x7fffffff;   // [0,1)
        }
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy);
        float n00 = Hash01(x0, y0, seed), n10 = Hash01(x0 + 1, y0, seed);
        float n01 = Hash01(x0, y0 + 1, seed), n11 = Hash01(x0 + 1, y0 + 1, seed);
        return (n00 * (1f - sx) + n10 * sx) * (1f - sy) + (n01 * (1f - sx) + n11 * sx) * sy;
    }

    /// <summary>Multi-octave fractal noise at world fraction (u,v) in [0,1]; returns a SIGNED value in [-1,1].</summary>
    public static float Fractal(float u, float v, TextureLayerSpec s)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        int oc = Math.Clamp(s.OctaveCount, 1, 12);
        for (int o = 0; o < oc; o++)
        {
            int oct = Math.Clamp(s.FirstOctave + o, 0, 14);
            float freq = MathF.Pow(2f, oct);
            sum += amp * ValueNoise(u * freq, v * freq, s.Seed + o * 101);
            norm += amp; amp *= 0.5f;
        }
        return (sum / MathF.Max(norm, 1e-4f)) * 2f - 1f;
    }

    // ---- terrain property sampling at a world XZ position ----
    /// <summary>Bilinear terrain height in metres at world (wx,wz).</summary>
    public static float HeightAt(Heightmap hm, TerrainConfig cfg, float wx, float wz)
    {
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
        float gx = wx / ws * (hm.Width - 1), gz = wz / ws * (hm.Height - 1);
        int x0 = Math.Clamp((int)gx, 0, hm.Width - 1), z0 = Math.Clamp((int)gz, 0, hm.Height - 1);
        int x1 = Math.Min(x0 + 1, hm.Width - 1), z1 = Math.Min(z0 + 1, hm.Height - 1);
        float tx = Math.Clamp(gx - x0, 0f, 1f), tz = Math.Clamp(gz - z0, 0f, 1f);
        float h00 = cfg.HeightToMeters(hm[x0, z0]), h10 = cfg.HeightToMeters(hm[x1, z0]);
        float h01 = cfg.HeightToMeters(hm[x0, z1]), h11 = cfg.HeightToMeters(hm[x1, z1]);
        return (h00 * (1f - tx) + h10 * tx) * (1f - tz) + (h01 * (1f - tx) + h11 * tx) * tz;
    }

    /// <summary>Terrain slope in degrees at world (wx,wz) (central difference of the heightfield).</summary>
    public static float SlopeDegrees(Heightmap hm, TerrainConfig cfg, float wx, float wz)
    {
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
        int x = Math.Clamp((int)MathF.Round(wx / ws * (hm.Width - 1)), 0, hm.Width - 1);
        int z = Math.Clamp((int)MathF.Round(wz / ws * (hm.Height - 1)), 0, hm.Height - 1);
        int xm = Math.Max(0, x - 1), xp = Math.Min(hm.Width - 1, x + 1);
        int zm = Math.Max(0, z - 1), zp = Math.Min(hm.Height - 1, z + 1);
        float sp = MathF.Max(cfg.HorizontalSpacing, 1e-3f);
        float dhx = (cfg.HeightToMeters(hm[xp, z]) - cfg.HeightToMeters(hm[xm, z])) / (MathF.Max(xp - xm, 1) * sp);
        float dhz = (cfg.HeightToMeters(hm[x, zp]) - cfg.HeightToMeters(hm[x, zm])) / (MathF.Max(zp - zm, 1) * sp);
        float grad = MathF.Sqrt(dhx * dhx + dhz * dhz);
        return MathF.Atan(grad) * 180f / MathF.PI;
    }

    /// <summary>Blend factor (0 = texture A, 1 = texture B) for a property value, with the noise offset applied.</summary>
    public static float Mask(float value, float noiseSigned, TextureLayerSpec s)
    {
        float lo = MathF.Min(s.ThresholdLow, s.ThresholdHigh), hi = MathF.Max(s.ThresholdLow, s.ThresholdHigh);
        float span = MathF.Max(hi - lo, 1e-3f);
        float v = value;
        if (s.NoiseOn) v += noiseSigned * span * Math.Clamp(s.ThresholdWidth, 0f, 2f);
        float t = Math.Clamp((v - lo) / span, 0f, 1f);
        return t * t * (3f - 2f * t);   // smoothstep
    }

    private static readonly Vector3 FallbackA = new(0.45f, 0.5f, 0.38f);
    private static readonly Vector3 FallbackB = new(0.55f, 0.47f, 0.40f);

    /// <summary>Bake a layer over the WHOLE atlas in place (atlas (x,y) -> world (x/size*ws, y/size*ws), matching
    /// <see cref="TerrainTexture.BakeAtlas"/>): per texel, evaluate the selector property + noise, blend A vs B.</summary>
    public static void BakeLayerToAtlas(Texture2D atlas, Heightmap hm, TerrainConfig cfg, Texture2D? texA, Texture2D? texB, TextureLayerSpec s)
    {
        int size = atlas.Width;
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
        float ta = MathF.Max(s.TileMetersA, 0.1f), tb = MathF.Max(s.TileMetersB, 0.1f);
        var px = atlas.Rgba;
        System.Threading.Tasks.Parallel.For(0, size, y =>
        {
            float wz = (y + 0.5f) / size * ws, fv = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float wx = (x + 0.5f) / size * ws, fu = (x + 0.5f) / size;
                float val = s.Selector == LayerSelector.Slope ? SlopeDegrees(hm, cfg, wx, wz) : HeightAt(hm, cfg, wx, wz);
                float n = s.NoiseOn ? Fractal(fu, fv, s) : 0f;
                float m = Mask(val, n, s);
                var ca = texA is null ? FallbackA : texA.Sample(wx / ta, wz / ta);
                var cb = texB is null ? FallbackB : texB.Sample(wx / tb, wz / tb);
                var c = Vector3.Lerp(ca, cb, m);
                int o = (y * size + x) * 4;
                px[o]     = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                px[o + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                px[o + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                px[o + 3] = 255;
            }
        });
    }

    /// <summary>Fill the WHOLE atlas in place with one tiled texture (Editor42's "fill the entire terrain").</summary>
    public static void FillAtlas(Texture2D atlas, Texture2D tex, float worldSize, float tileMeters)
    {
        int size = atlas.Width;
        float ws = worldSize <= 0 ? 1f : worldSize, tm = MathF.Max(tileMeters, 0.1f);
        var px = atlas.Rgba;
        System.Threading.Tasks.Parallel.For(0, size, y =>
        {
            float wz = (y + 0.5f) / size * ws;
            for (int x = 0; x < size; x++)
            {
                float wx = (x + 0.5f) / size * ws;
                var c = tex.Sample(wx / tm, wz / tm);
                int o = (y * size + x) * 4;
                px[o]     = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                px[o + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                px[o + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                px[o + 3] = 255;
            }
        });
    }

    /// <summary>Render a self-contained preview of the blend (Editor42's "Proof"): the selector value ramps bottom
    /// (low) to top (high) so both source textures show, with the noisy A/B boundary in the middle. No terrain needed.</summary>
    public static Texture2D ProofPreview(int size, Texture2D? texA, Texture2D? texB, TextureLayerSpec s)
    {
        var rgba = new byte[size * size * 4];
        float lo = MathF.Min(s.ThresholdLow, s.ThresholdHigh), hi = MathF.Max(s.ThresholdLow, s.ThresholdHigh);
        float span = MathF.Max(hi - lo, 1e-3f);
        const float tilesAcross = 3f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fy = y / (float)size, fx = x / (float)size;
                float value = (lo - 0.2f * span) + (span * 1.4f) * (1f - fy);   // top of the image -> high property
                float n = s.NoiseOn ? Fractal(fx, fy, s) : 0f;
                float m = Mask(value, n, s);
                var ca = texA is null ? FallbackA : texA.Sample(fx * tilesAcross, fy * tilesAcross);
                var cb = texB is null ? FallbackB : texB.Sample(fx * tilesAcross, fy * tilesAcross);
                var c = Vector3.Lerp(ca, cb, m);
                int o = (y * size + x) * 4;
                rgba[o]     = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                rgba[o + 3] = 255;
            }
        return new Texture2D(size, size, rgba);
    }
}
