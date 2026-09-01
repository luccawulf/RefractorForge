using System;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Bakes a <see cref="LightRig"/> into the two places the engine actually reads lighting from.
///
/// Refractor renders no dynamic point lights, so a placed light only ever reaches the game as baked pixels.
/// There are two destinations, and they carry different things:
///
///   * THE GROUND TEXTURE, in full RGB. This is where a light's COLOUR lives. It is the same trick
///     DC_Basrah_Nights uses — <c>textureManager.alternativePath</c> pointing at level-local art — and it works
///     because the ground texture is modulated by the scene light: with a night ambient near 0.08, a pool
///     painted bright against dark surroundings still reads as a pool, by ratio rather than absolute
///     brightness.
///   * PER-OBJECT LIGHTMAPS, as intensity only. Every one of the 201 shipped lightmaps checked across retail
///     BF1942 levels is a colour-mapped TGA with a GREY palette, so the format carries brightness and the
///     engine is never shown a hue there. Baking colour into one would be inventing behaviour the game has
///     never been asked for, so a coloured lamp brightens an object without tinting it.
/// </summary>
public static class LightBake
{
    /// <summary>
    /// Is <paramref name="light"/> visible from a world point, or does terrain stand between them?
    ///
    /// Marches the segment rather than the sun's parallel ray: a lamp is a finite distance away, so the test
    /// has to stop at the light instead of continuing to the map edge. Stepping is tied to the heightmap's own
    /// spacing — finer wastes time on samples that read the same cell, coarser steps over ridges.
    /// </summary>
    public static bool Visible(float wx, float wy, float wz, PointLight light,
                              Heightmap hm, TerrainConfig cfg)
    {
        float lx = light.Position.X, ly = light.Position.Y, lz = light.Position.Z;
        float dx = lx - wx, dy = ly - wy, dz = lz - wz;
        float horiz = MathF.Sqrt(dx * dx + dz * dz);
        if (horiz < 1e-3f) return true;                 // straight overhead: nothing can be in the way

        float spacing = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        int steps = (int)MathF.Ceiling(horiz / spacing);
        if (steps < 2) return true;
        if (steps > 4096) steps = 4096;                 // a very long reach must not stall the bake

        // Start a little along the segment so the surface the point sits on does not shadow itself.
        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            float sx = wx + dx * t, sy = wy + dy * t, sz = wz + dz * t;
            float g = GroundAt(sx, sz, hm, cfg);
            if (g > sy + 0.15f) return false;
        }
        return true;
    }

    private static float GroundAt(float wx, float wz, Heightmap hm, TerrainConfig cfg)
    {
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
        int x = (int)(wx / ws * (hm.Width - 1) + 0.5f);
        int y = (int)(wz / ws * (hm.Height - 1) + 0.5f);
        if (x < 0) x = 0; else if (x > hm.Width - 1) x = hm.Width - 1;
        if (y < 0) y = 0; else if (y > hm.Height - 1) y = hm.Height - 1;
        return cfg.HeightToMeters(hm[x, y]);
    }

    /// <summary>
    /// The light a rig delivers to the ground, as an RGB map laid out like the terrain atlas: texel (x,y) is
    /// world (x/size·worldSize, z/size·worldSize), the same mapping <c>BakeAtlas</c> and the shadow bake use, so
    /// it lines up with the ground texture without any resampling.
    ///
    /// <paramref name="progress"/> is called per row; a rig over a large map is a lot of ray-marching.
    /// </summary>
    public static Texture2D BakeGround(Heightmap hm, TerrainConfig cfg, LightRig rig, int size,
                                       Action<int, int>? progress = null)
    {
        if (size < 4) size = 4;
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
        var rgba = new byte[size * size * 4];

        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                float wx = (px + 0.5f) / size * ws;
                float wz = (py + 0.5f) / size * ws;
                float wy = GroundAt(wx, wz, hm, cfg);

                var (r, g, b) = rig.Illuminate(wx, wy, wz,
                    l => Visible(wx, wy, wz, l, hm, cfg));

                int o = (py * size + px) * 4;
                rgba[o + 0] = Clamp(r);
                rgba[o + 1] = Clamp(g);
                rgba[o + 2] = Clamp(b);
                rgba[o + 3] = 255;
            }
            progress?.Invoke(py + 1, size);
        }
        return new Texture2D(size, size, rgba);
    }

    /// <summary>
    /// Burn a ground light map into the terrain atlas.
    ///
    /// The pool is ADDED rather than multiplied: light adds to what a surface already reflects, and multiplying
    /// would only ever darken. <paramref name="strength"/> scales the whole rig so it can be dialled in without
    /// re-baking, and the two textures are sampled by normalised position so they need not be the same size.
    ///
    /// This edits the atlas in place, which is the point — the modified ground texture is what ships.
    /// </summary>
    public static void BurnIntoAtlas(Texture2D atlas, Texture2D groundLight, float strength = 1f)
    {
        if (strength <= 0f) return;
        int aw = atlas.Width, ah = atlas.Height;
        int lw = groundLight.Width, lh = groundLight.Height;
        var ap = atlas.Rgba;
        var lp = groundLight.Rgba;

        for (int y = 0; y < ah; y++)
        {
            int sy = lh == ah ? y : (int)((y + 0.5f) / ah * lh);
            if (sy < 0) sy = 0; else if (sy > lh - 1) sy = lh - 1;
            for (int x = 0; x < aw; x++)
            {
                int sx = lw == aw ? x : (int)((x + 0.5f) / aw * lw);
                if (sx < 0) sx = 0; else if (sx > lw - 1) sx = lw - 1;

                int so = (sy * lw + sx) * 4;
                if (lp[so] == 0 && lp[so + 1] == 0 && lp[so + 2] == 0) continue;   // untouched ground

                int ao = (y * aw + x) * 4;
                ap[ao + 0] = AddClamp(ap[ao + 0], lp[so + 0], strength);
                ap[ao + 1] = AddClamp(ap[ao + 1], lp[so + 1], strength);
                ap[ao + 2] = AddClamp(ap[ao + 2], lp[so + 2], strength);
            }
        }
    }

    /// <summary>
    /// The rig's contribution at one world point as a single INTENSITY, for per-object lightmaps.
    ///
    /// Rec. 709 luma rather than a plain average, so a blue lamp and a yellow lamp of the same measured
    /// brightness do not come out at different strengths on an object.
    /// </summary>
    public static float Intensity(float wx, float wy, float wz, LightRig rig, Heightmap hm, TerrainConfig cfg)
    {
        var (r, g, b) = rig.Illuminate(wx, wy, wz, l => Visible(wx, wy, wz, l, hm, cfg));
        return 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }

    private static byte Clamp(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    private static byte AddClamp(byte baseValue, byte add, float strength) =>
        (byte)Math.Clamp(baseValue + (int)(add * strength + 0.5f), 0, 255);
}
