using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Terrain;

/// <summary>
/// Raises a mountain into a heightmap. Pure and deterministic for a given seed, like <see cref="Con.CityGenerator"/>.
///
/// A cone with a radial falloff reads as a slag heap, so the shape is built from four things instead: a broad
/// skirt with a narrower summit mass on top (one falloff alone is flattest exactly where the peak should be, which
/// is what makes generated terrain look like a dome), a lean so the summit sits off-centre and one flank is
/// steeper, gullies CARVED into the mass by ridged noise so the spurs between them are what is left standing, and
/// fractal detail over all of it. Every term is multiplied by the profile, so the rise reaches EXACTLY zero at the
/// rim and the mountain melts into whatever terrain is already there instead of ending at a step.
///
/// An earlier version varied the radius with cos(theta * ridges) and produced a tidy starfish — evenly spaced
/// lobes are the one thing that reads as generated at a glance, which is why the spurs are carved rather than
/// extruded and the outline is only gently uneven.
///
/// It is additive: the existing ground is kept and the mountain laid on top, so it inherits the lie of the land
/// underneath instead of flattening it.
/// </summary>
public static class MountainGenerator
{
    /// <summary>Raise a mountain centred on world (<paramref name="cx"/>, <paramref name="cz"/>).</summary>
    /// <param name="radius">Footprint radius in metres (before the ridge warp, which varies it by direction).</param>
    /// <param name="peakMetres">How far the summit rises above the ground it sits on.</param>
    /// <param name="roughness">0 = a smooth hill, 1 = broken and rocky. 0.35 is a good mountain.</param>
    /// <param name="ridges">How many spurs run down from the summit. 0 keeps the footprint round.</param>
    /// <returns>The heightmap rect actually touched, ready to ship as a collab TERRAIN op.</returns>
    public static (int X0, int Y0, int W, int H) Raise(
        Heightmap hm, TerrainConfig cfg, float cx, float cz, float radius, float peakMetres,
        int seed, float roughness = 0.35f, int ridges = 5, float ridgeDepth = 0.30f)
    {
        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        float yScale = cfg.YScale <= 0 ? 1f : cfg.YScale;

        // The warp can push the footprint out past `radius`, so the rect is taken from the widest it can get.
        // Worst case the rim can reach: the bearing warp widens rEff by 16%, and a lean of 0.8 on the gentle flank
        // divides the distance, stretching it by a further 1/0.8. Under-reaching here does not merely crop the
        // rect - it CLIPS the mountain, leaving a step along the edge where the loop stopped.
        float reach = radius * 1.50f + sp * 2f;
        int x0 = Math.Max(0, (int)MathF.Floor((cx - reach) / sp));
        int y0 = Math.Max(0, (int)MathF.Floor((cz - reach) / sp));
        int x1 = Math.Min(hm.Width - 1, (int)MathF.Ceiling((cx + reach) / sp));
        int y1 = Math.Min(hm.Height - 1, (int)MathF.Ceiling((cz + reach) / sp));
        if (x1 < x0 || y1 < y0) return (0, 0, 0, 0);

        float detailScale = 3.5f / MathF.Max(radius, 1f);   // a few features across the mountain, not thousands
        // Which way the mountain leans, picked from the seed so it is stable but not always the same way.
        float leanDir = Fbm(seed * 0.37f, 11f, seed + 91, 2) * MathF.Tau;
        float leanX = MathF.Cos(leanDir), leanZ = MathF.Sin(leanDir);
        float maxMetres = 65535f * yScale / 256f;

        for (int gy = y0; gy <= y1; gy++)
            for (int gx = x0; gx <= x1; gx++)
            {
                float wx = gx * sp, wz = gy * sp;
                float dx = wx - cx, dz = wz - cz;
                float dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist < 1e-4f) dist = 1e-4f;

                // Outline: a MILD irregularity, sampled on the direction vector so it varies with bearing only.
                // An earlier version modulated the radius with cos(theta * ridges) and produced a tidy starfish -
                // regular lobes are the one thing that instantly reads as generated, so the outline is now just
                // gently uneven and the spurs come from carving instead.
                float ux = dx / dist, uz = dz / dist;
                float bearing = (Fbm(ux * 2.3f + 8f, uz * 2.3f + 8f, seed + 31, 3) - 0.5f) * 2f;
                float rEff = radius * (1f + 0.16f * bearing);
                if (rEff < sp) rEff = sp;

                // Lean: one flank steeper than the other. Symmetric relief reads as a spoil heap however much
                // noise is piled on it, and no real summit sits in the middle of its own footprint.
                float lean = 1f + 0.20f * (ux * leanX + uz * leanZ);

                float d = dist / rEff * lean;
                if (d >= 1f) continue;                       // outside: untouched, so the join is seamless

                // Two masses, not one: a broad skirt that eases to zero slope at the rim, plus a narrow summit
                // sitting on it. The skirt alone (a plain cos falloff) is flattest exactly where the peak should
                // be, which is what makes single-falloff terrain look like a dome.
                float skirt = MathF.Pow(MathF.Cos(d * MathF.PI * 0.5f), 1.7f);
                float core = MathF.Exp(-(d * d) / 0.09f);     // ~e^-(d/0.3)^2: negligible by the rim
                float profile = 0.66f * skirt + 0.34f * core;

                // Spurs and gullies. Ridged noise has sharp creases where it approaches 1, so SUBTRACTING it from
                // the cone carves valleys and leaves the untouched ground between them standing as ridges - which
                // is how real mountains are shaped, by erosion cutting into a mass rather than by lobes sticking
                // out of one. Carving is weak at the summit and strong on the flanks, so the peak stays a peak
                // while the skirt breaks up into spurs.
                float gullyScale = MathF.Max(ridges, 1) / MathF.Max(radius, 1f);
                float gully = Ridged(wx * gullyScale, wz * gullyScale, seed + 13, 4);
                float carve = ridgeDepth * (0.30f + 0.70f * d);
                float shape = profile * (1f - carve * (1f - gully));

                float detail = (Fbm(wx * detailScale, wz * detailScale, seed + 7, 5) - 0.5f) * 2f;

                float rise = peakMetres * shape + peakMetres * roughness * 0.30f * profile * detail;
                if (rise <= 0f) continue;                     // never dig into the existing ground

                float baseM = hm[gx, gy] * yScale / 256f;
                float m = Math.Clamp(baseM + rise, 0f, maxMetres);
                hm[gx, gy] = (ushort)Math.Clamp(MathF.Round(m * 256f / yScale), 0f, 65535f);
            }

        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Pack a heightmap rect into the collab wire's base64 u16-LE payload.</summary>
    public static string EncodeRect(Heightmap hm, int x0, int y0, int w, int h)
    {
        var buf = new byte[w * h * 2];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                ushort v = hm[x0 + xx, y0 + yy];
                int o = (yy * w + xx) * 2;
                buf[o] = (byte)(v & 0xFF);
                buf[o + 1] = (byte)(v >> 8);
            }
        return Convert.ToBase64String(buf);
    }

    /// <summary>Highest point of the finished mountain, in metres — handy for reporting and for placing things on it.</summary>
    public static float PeakHeight(Heightmap hm, TerrainConfig cfg, int x0, int y0, int w, int h)
    {
        float yScale = cfg.YScale <= 0 ? 1f : cfg.YScale;
        ushort top = 0;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
                if (hm[x0 + xx, y0 + yy] > top) top = hm[x0 + xx, y0 + yy];
        return top * yScale / 256f;
    }

    // ---- value noise: no dependencies, deterministic, and the same on every machine ----

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

    private static float Noise(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        float u = xf * xf * (3f - 2f * xf), v = yf * yf * (3f - 2f * yf);   // smoothstep, so no lattice creases
        float n00 = Hash(xi, yi, seed), n10 = Hash(xi + 1, yi, seed);
        float n01 = Hash(xi, yi + 1, seed), n11 = Hash(xi + 1, yi + 1, seed);
        return (n00 * (1f - u) + n10 * u) * (1f - v) + (n01 * (1f - u) + n11 * u) * v;
    }

    private static float Fbm(float x, float y, int seed, int octaves)
    {
        float sum = 0f, amp = 0.5f, f = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Noise(x * f, y * f, seed + i * 101);
            norm += amp; amp *= 0.5f; f *= 2f;
        }
        return norm <= 0f ? 0.5f : sum / norm;
    }

    /// <summary>Ridged noise: the creases of 1-|n| are what make rock read as rock rather than as dunes.</summary>
    private static float Ridged(float x, float y, int seed, int octaves)
    {
        float sum = 0f, amp = 0.5f, f = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1f - MathF.Abs(Noise(x * f, y * f, seed + i * 131) * 2f - 1f);
            sum += amp * n * n;
            norm += amp; amp *= 0.5f; f *= 2f;
        }
        return norm <= 0f ? 0.5f : sum / norm;
    }
}
