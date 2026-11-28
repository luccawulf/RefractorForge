using System;
using System.Collections.Generic;
using System.Linq;

namespace RefractorForge.Render;

/// <summary>
/// Keeps a level's 24-bit object lightmaps within what the game can load.
///
/// <para>BfVietnam is a 32-bit program and a 24-bit lightmap costs it four bytes a texel plus mips, where an 8-bit
/// one costs one. Measured the hard way (2026-11-28): Operation_Flaming_Dart baked with 288 colour maps at 1024 px -
/// 864 MB of files beside 330 MB that had loaded fine as 8-bit - ran the client out of memory while the level loaded;
/// it died on a null effect creating <c>effects/particles.fx</c> with two gigabytes in use.</para>
///
/// <para>So a map that carries no colour to speak of goes back to 8 bits (through the level palette, where it costs a
/// third and reads the same), and the largest remaining maps are halved one at a time until the 24-bit files fit the
/// budget. Largest first, because a 1024 px map is on a hangar or a long wall, where a texel is already small.</para>
/// </summary>
public static class LightmapBudget
{
    /// <param name="Colour">Write it 24-bit; otherwise 8-bit grey.</param>
    /// <param name="Halvings">How many times to halve it first.</param>
    public readonly record struct Choice(bool Colour, int Halvings);

    /// <summary>The smallest side a map is reduced to.</summary>
    public const int MinSide = 128;

    /// <summary>
    /// True when the map's texels differ from grey enough to see: more than <paramref name="share"/> of them have a
    /// channel over <paramref name="threshold"/> (of 255) away from their own mean.
    /// </summary>
    public static bool HasColour(Texture2D t, int threshold = 5, float share = 0.02f)
    {
        var px = t.Rgba;
        int n = px.Length / 4, over = 0, limit = (int)(n * share);
        for (int i = 0; i < n; i++)
        {
            int r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            int mean = (r + g + b + 1) / 3;
            if (Math.Abs(r - mean) > threshold || Math.Abs(g - mean) > threshold || Math.Abs(b - mean) > threshold)
                if (++over > limit) return true;
        }
        return false;
    }

    /// <summary>What to do with each map so that the 24-bit ones total at most <paramref name="budgetBytes"/> of
    /// file (three bytes a texel).</summary>
    public static Choice[] Fit(IReadOnlyList<Texture2D> maps, long budgetBytes)
    {
        var res = new Choice[maps.Count];
        var side = new int[maps.Count];
        long total = 0;
        for (int i = 0; i < maps.Count; i++)
        {
            bool colour = HasColour(maps[i]);
            res[i] = new Choice(colour, 0);
            side[i] = Math.Max(maps[i].Width, maps[i].Height);
            if (colour) total += (long)maps[i].Width * maps[i].Height * 3;
        }
        // Halve the largest, one at a time. Ties go to the earlier map, so the same bake gives the same files.
        var order = Enumerable.Range(0, maps.Count).Where(i => res[i].Colour).ToList();
        while (total > budgetBytes)
        {
            int pick = -1;
            foreach (int i in order)
                if (side[i] / 2 >= MinSide && (pick < 0 || side[i] > side[pick])) pick = i;
            if (pick < 0) break;                                   // everything is at the floor: nothing more to give
            int h = res[pick].Halvings;
            long now = ((long)maps[pick].Width >> h) * (maps[pick].Height >> h) * 3;
            total -= now - now / 4;
            side[pick] /= 2;
            res[pick] = new Choice(true, h + 1);
        }
        return res;
    }

    /// <summary>The map at half its size each way, each texel the mean of four.</summary>
    public static Texture2D Halve(Texture2D t)
    {
        int w = Math.Max(1, t.Width / 2), h = Math.Max(1, t.Height / 2);
        var src = t.Rgba; var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x1 = Math.Min(x * 2 + 1, t.Width - 1), y1 = Math.Min(y * 2 + 1, t.Height - 1);
                int a = (y * 2 * t.Width + x * 2) * 4, b = (y * 2 * t.Width + x1) * 4, c = (y1 * t.Width + x * 2) * 4, d = (y1 * t.Width + x1) * 4;
                int o = (y * w + x) * 4;
                for (int k = 0; k < 4; k++) dst[o + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) / 4);
            }
        return new Texture2D(w, h, dst);
    }

    /// <summary>The map with every texel at its own brightness, for the 8-bit form.</summary>
    public static Texture2D Grey(Texture2D t)
    {
        var src = t.Rgba; var dst = new byte[src.Length];
        for (int i = 0; i + 3 < src.Length; i += 4)
        {
            byte v = (byte)Math.Clamp((int)MathF.Round(0.2126f * src[i] + 0.7152f * src[i + 1] + 0.0722f * src[i + 2]), 0, 255);
            dst[i] = v; dst[i + 1] = v; dst[i + 2] = v; dst[i + 3] = 255;
        }
        return new Texture2D(t.Width, t.Height, dst);
    }
}
