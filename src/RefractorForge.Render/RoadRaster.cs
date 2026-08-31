namespace RefractorForge.Render;

/// <summary>A road painted into an image patch, described in WORLD metres rather than atlas texels.</summary>
/// <param name="WorldX">West edge of the patch, metres.</param>
/// <param name="WorldZ">South edge of the patch, metres.</param>
/// <param name="WorldW">Patch width, metres.</param>
/// <param name="WorldH">Patch depth, metres.</param>
/// <param name="Width">Patch width in pixels.</param>
/// <param name="Height">Patch height in pixels.</param>
/// <param name="Rgba">RGB is the road colour; ALPHA is coverage, so the receiver blends rather than stamping a
/// hard rectangle over the ground.</param>
public readonly record struct RoadPatch(float WorldX, float WorldZ, float WorldW, float WorldH,
                                        int Width, int Height, byte[] Rgba);

/// <summary>
/// Rasterises a road centreline into an RGBA patch that can be blended onto a terrain atlas.
///
/// Deliberately expressed in WORLD coordinates rather than atlas texels: the editor's atlas resolution depends on
/// the level's tile set (8192 on a remastered map, far less elsewhere), and a patch authored against the wrong
/// size would land in the wrong place. Sending metres lets the receiver map it onto whatever atlas it happens to
/// have, and lets this run headlessly with no atlas at all.
///
/// The road is drawn with a soft shoulder and a little per-texel variation, because a constant-colour band with
/// hard edges reads as a painted stripe rather than a track worn into the ground.
/// </summary>
public static class RoadRaster
{
    /// <summary>Paint a road along <paramref name="samples"/>.</summary>
    /// <param name="pixelsPerMetre">Patch resolution. 2 gives a 0.5 m texel, which is finer than any BF terrain
    /// atlas and keeps the edge smooth once it is scaled onto one.</param>
    /// <param name="shoulder">How much of the half-width fades out at the edge, 0..1.</param>
    public static RoadPatch Paint(IReadOnlyList<RoadSample> samples, (byte R, byte G, byte B) colour,
                                  float pixelsPerMetre = 2f, float shoulder = 0.35f, int seed = 1,
                                  float worldSize = float.MaxValue)
    {
        if (samples.Count == 0) throw new ArgumentException("a road needs at least one sample");

        float maxHalf = 0f;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var s in samples)
        {
            maxHalf = MathF.Max(maxHalf, s.HalfWidth);
            minX = MathF.Min(minX, s.X); maxX = MathF.Max(maxX, s.X);
            minZ = MathF.Min(minZ, s.Z); maxZ = MathF.Max(maxZ, s.Z);
        }
        // Room for the widest point plus a texel of margin, clamped to the map.
        float pad = maxHalf + 2f;
        float wx = MathF.Max(minX - pad, 0f), wz = MathF.Max(minZ - pad, 0f);
        float wx1 = MathF.Min(maxX + pad, worldSize), wz1 = MathF.Min(maxZ + pad, worldSize);
        float ww = MathF.Max(wx1 - wx, 1f), wh = MathF.Max(wz1 - wz, 1f);

        int pw = Math.Clamp((int)MathF.Ceiling(ww * pixelsPerMetre), 1, 4096);
        int ph = Math.Clamp((int)MathF.Ceiling(wh * pixelsPerMetre), 1, 4096);
        var rgba = new byte[pw * ph * 4];

        shoulder = Math.Clamp(shoulder, 0.01f, 0.99f);

        for (int py = 0; py < ph; py++)
        {
            float z = wz + (py + 0.5f) / ph * wh;
            for (int px = 0; px < pw; px++)
            {
                float x = wx + (px + 0.5f) / pw * ww;

                // Nearest point on the centreline, and the half-width there. Segment-wise so a coarse sample
                // spacing still gives a continuous edge.
                float best = float.MaxValue, halfAt = maxHalf;
                for (int i = 1; i < samples.Count; i++)
                {
                    var a = samples[i - 1]; var b = samples[i];
                    float dx = b.X - a.X, dz = b.Z - a.Z;
                    float len2 = dx * dx + dz * dz;
                    float t = len2 > 1e-6f ? Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / len2, 0f, 1f) : 0f;
                    float cx = a.X + dx * t, cz = a.Z + dz * t;
                    float d2 = (x - cx) * (x - cx) + (z - cz) * (z - cz);
                    if (d2 < best) { best = d2; halfAt = a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t; }
                }
                if (samples.Count == 1)
                {
                    var a = samples[0];
                    best = (x - a.X) * (x - a.X) + (z - a.Z) * (z - a.Z);
                    halfAt = a.HalfWidth;
                }

                float d = MathF.Sqrt(best);
                if (d > halfAt) continue;                       // outside the road: alpha stays 0

                // Full strength through the middle, fading across the shoulder so the edge blends into the ground.
                float inner = halfAt * (1f - shoulder);
                float cov = d <= inner ? 1f : 1f - (d - inner) / MathF.Max(halfAt - inner, 1e-3f);
                cov = Math.Clamp(cov, 0f, 1f);
                cov *= cov * (3f - 2f * cov);                   // smoothstep: no visible banding at the shoulder

                // A little grain so the surface is not a flat colour. Deterministic from the texel.
                float n = Hash(px, py, seed);
                float shade = 0.90f + 0.20f * n;

                int o = (py * pw + px) * 4;
                rgba[o] = (byte)Math.Clamp(colour.R * shade, 0f, 255f);
                rgba[o + 1] = (byte)Math.Clamp(colour.G * shade, 0f, 255f);
                rgba[o + 2] = (byte)Math.Clamp(colour.B * shade, 0f, 255f);
                rgba[o + 3] = (byte)(cov * 255f);
            }
        }

        return new RoadPatch(wx, wz, ww, wh, pw, ph, rgba);
    }

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 0x8DA6B343) ^ (uint)(y * 0xD8163841) ^ (uint)(seed * 0x1B873593);
            h ^= h >> 15; h *= 0x2C1B3C6D;
            h ^= h >> 12; h *= 0x297A2D39;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    /// <summary>The collab wire form: world rect, pixel size, then the base64 RGBA.</summary>
    public static string ToWire(RoadPatch p) =>
        $"ATLAS {F(p.WorldX)} {F(p.WorldZ)} {F(p.WorldW)} {F(p.WorldH)} {p.Width} {p.Height} {Convert.ToBase64String(p.Rgba)}";

    private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
