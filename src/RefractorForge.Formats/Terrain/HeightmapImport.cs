using System;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// A heightmap from an external terrain tool (Blender, Gaea, World Machine, L3DT) into the engine's grid.
///
/// Two things differ between such an image and a level's Heightmap.raw, and both have to be got right or the terrain
/// comes in mirrored or at the wrong height:
/// <list type="bullet">
///   <item>ORIENTATION. An image's top row is NORTH - what every terrain tool shows, and what this editor's own
///   minimap writes. The engine's first row is SOUTH (world Z = 0). So the rows are turned over.</item>
///   <item>HEIGHT. The engine reads a sample as <c>raw x yScale / 256</c> metres, so 16 bits cover
///   0 .. 256 x yScale m. An exported image's 0 .. 65535 instead means "lowest .. highest point of the terrain", and
///   the tool knows how many metres that is while the file does not - so the caller says how high the highest point
///   is, and the samples are mapped onto 0 .. that many metres at the level's yScale.</item>
/// </list>
/// A raw file is NOT passed through here: it is already in the engine's order and units.
/// </summary>
public static class HeightmapImport
{
    /// <summary>
    /// The yScale that makes an image's full 16-bit range span exactly <paramref name="topMetres"/>: 65535 lands on
    /// the highest point and nothing is clamped or wasted. What a NEW map made from an imported heightmap should use.
    /// </summary>
    public static float YScaleFor(float topMetres) => MathF.Max(topMetres, 0.01f) * 256f / 65535f;

    /// <summary>The highest terrain a level can hold at this yScale.</summary>
    public static float MaxMetres(float yScale) => 65535f * yScale / 256f;

    /// <summary>
    /// Turn a north-up image into a <paramref name="side"/>-square engine heightmap: rows flipped so the image's top is
    /// the map's north edge, resampled bilinearly (corner-aligned, so a same-size image maps sample for sample), and
    /// the image's 0 .. 65535 mapped onto 0 .. <paramref name="topMetres"/> at <paramref name="yScale"/>.
    /// </summary>
    /// <param name="clamped">How many samples were above what <paramref name="yScale"/> can hold and were cut to it.</param>
    public static Heightmap FromNorthUpImage(ushort[] samples, int width, int height, int side, float topMetres, float yScale,
                                             out int clamped)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (width <= 0 || height <= 0 || samples.LongLength < (long)width * height)
            throw new ArgumentException("The sample array does not match the image size.");
        if (side < 2) throw new ArgumentOutOfRangeException(nameof(side));

        var hm = new Heightmap(side, side);
        // metres = s / 65535 * top;  raw = metres * 256 / yScale
        double toRaw = MathF.Max(topMetres, 0f) * 256.0 / (65535.0 * MathF.Max(yScale, 1e-6f));
        int over = 0;
        for (int gz = 0; gz < side; gz++)
        {
            // Engine row gz (south to north) comes from image row (height-1) - v: the image's top is north.
            double v = (height - 1) - (double)gz * (height - 1) / (side - 1);
            int y0 = Math.Clamp((int)Math.Floor(v), 0, height - 1), y1 = Math.Min(y0 + 1, height - 1);
            double fy = v - y0;
            for (int gx = 0; gx < side; gx++)
            {
                double u = (double)gx * (width - 1) / (side - 1);
                int x0 = Math.Clamp((int)Math.Floor(u), 0, width - 1), x1 = Math.Min(x0 + 1, width - 1);
                double fx = u - x0;
                double s = (samples[(long)y0 * width + x0] * (1 - fx) + samples[(long)y0 * width + x1] * fx) * (1 - fy)
                         + (samples[(long)y1 * width + x0] * (1 - fx) + samples[(long)y1 * width + x1] * fx) * fy;
                double raw = Math.Round(s * toRaw);
                if (raw > ushort.MaxValue) { raw = ushort.MaxValue; over++; }
                hm[gx, gz] = (ushort)Math.Max(raw, 0);
            }
        }
        clamped = over;
        return hm;
    }
}
