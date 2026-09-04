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
                var n = NormalAt(wx, wz, hm, cfg);

                // The same shading the viewport applies, so the bake predicts what the preview showed: a Lambert
                // term against the slope, wrapped so a bare lamp in the open still lights the ground around it
                // rather than only the half facing it. Without the wrap a flat surface under a lamp reads as a
                // hard disc, and without the slope term a hillside facing away from the lamp glows as if flat.
                float r = 0f, g = 0f, b = 0f;
                foreach (var l in rig.Lights)
                {
                    float a = l.Attenuation(wx, wy, wz);
                    if (a <= 0f) continue;
                    if (l.CastsShadows && !Visible(wx, wy, wz, l, hm, cfg)) continue;
                    float dx = l.Position.X - wx, dy = l.Position.Y - wy, dz = l.Position.Z - wz;
                    float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    float ndl = len > 1e-4f ? MathF.Max((n.X * dx + n.Y * dy + n.Z * dz) / len, 0f) : 1f;
                    ndl = ndl * 0.85f + 0.15f;
                    r += l.ColorR * a * ndl;
                    g += l.ColorG * a * ndl;
                    b += l.ColorB * a * ndl;
                }

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

    /// <summary>The terrain normal at a world point, from the heightmap's neighbours.</summary>
    private static Vec3 NormalAt(float wx, float wz, Heightmap hm, TerrainConfig cfg)
    {
        float sp = cfg.HorizontalSpacing <= 0 ? 1f : cfg.HorizontalSpacing;
        float hl = GroundAt(wx - sp, wz, hm, cfg), hr = GroundAt(wx + sp, wz, hm, cfg);
        float hd = GroundAt(wx, wz - sp, hm, cfg), hu = GroundAt(wx, wz + sp, hm, cfg);
        float nx = (hl - hr) / (2f * sp), nz = (hd - hu) / (2f * sp);
        float len = MathF.Sqrt(nx * nx + 1f + nz * nz);
        return new Vec3(nx / len, 1f / len, nz / len);
    }

    /// <summary>
    /// What the engine multiplies the ground texture by, texel for texel: the scene ambient plus the sun's
    /// diffuse against the slope. This is the reference a baked pool has to be measured against, because the
    /// game never adds light to the ground - it only ever scales the texture by this.
    ///
    /// Laid out like <see cref="BakeGround"/>: texel (x,y) is world (x/size*worldSize, y/size*worldSize).
    /// Three floats per texel, linear.
    /// </summary>
    public static float[] SceneLight(Heightmap hm, TerrainConfig cfg, int size, Vec3 ambient, Vec3 diffuse, Vec3 sunDir)
    {
        if (size < 4) size = 4;
        float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
        float sl = MathF.Sqrt(sunDir.X * sunDir.X + sunDir.Y * sunDir.Y + sunDir.Z * sunDir.Z);
        var sun = sl > 1e-6f ? new Vec3(sunDir.X / sl, sunDir.Y / sl, sunDir.Z / sl) : new Vec3(0, 1, 0);
        var scene = new float[size * size * 3];
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float wx = (px + 0.5f) / size * ws, wz = (py + 0.5f) / size * ws;
                var n = NormalAt(wx, wz, hm, cfg);
                float ndl = MathF.Max(n.X * sun.X + n.Y * sun.Y + n.Z * sun.Z, 0f);
                int o = (py * size + px) * 3;
                scene[o + 0] = ambient.X + diffuse.X * ndl;
                scene[o + 1] = ambient.Y + diffuse.Y * ndl;
                scene[o + 2] = ambient.Z + diffuse.Z * ndl;
            }
        return scene;
    }

    /// <summary>
    /// Burn a ground light map into the terrain atlas the way the game will actually show it.
    ///
    /// The viewport lights the ground as <c>texture x (scene + pool)</c>. The game can only ever draw
    /// <c>texture2 x scene</c>, so the pool has to go INTO the texture as a ratio: <c>texture2 = texture x (1 +
    /// pool / scene)</c>. That reproduces the viewport texel for texel, up to the point where the texture
    /// saturates - and it keeps the ground's own detail and colour inside the pool, where simply adding the
    /// lamp colour to every texel washed it out to a flat pale disc.
    ///
    /// The consequence to know about: under a dark night ambient the ratio is large and the pool saturates
    /// early, which is right - a lamp against near-black ground IS a bright pool - but it also means a pool baked
    /// against a daylight scene is faint by construction, because in daylight a lamp barely registers.
    /// <paramref name="strength"/> scales the pool; both maps are sampled by normalised position so they need not
    /// share the atlas's size.
    /// </summary>
    public static void MultiplyIntoAtlas(Texture2D atlas, Texture2D groundLight, float[] sceneLight, int sceneSize, float strength = 1f)
    {
        if (strength <= 0f) return;
        int aw = atlas.Width, ah = atlas.Height;
        int lw = groundLight.Width, lh = groundLight.Height;
        var ap = atlas.Rgba;
        var lp = groundLight.Rgba;
        const float floor = 0.03f;                // a scene term below this is treated as this: no infinite ratios

        for (int y = 0; y < ah; y++)
        {
            float v = (y + 0.5f) / ah;
            for (int x = 0; x < aw; x++)
            {
                float u = (x + 0.5f) / aw;
                var (lr, lg, lb) = Bilinear(lp, lw, lh, u, v);
                if (lr <= 0f && lg <= 0f && lb <= 0f) continue;   // untouched ground

                int sx = Math.Clamp((int)(u * sceneSize), 0, sceneSize - 1);
                int sy = Math.Clamp((int)(v * sceneSize), 0, sceneSize - 1);
                int so = (sy * sceneSize + sx) * 3;
                int ao = (y * aw + x) * 4;
                ap[ao + 0] = Scale(ap[ao + 0], 1f + strength * lr / MathF.Max(sceneLight[so + 0], floor));
                ap[ao + 1] = Scale(ap[ao + 1], 1f + strength * lg / MathF.Max(sceneLight[so + 1], floor));
                ap[ao + 2] = Scale(ap[ao + 2], 1f + strength * lb / MathF.Max(sceneLight[so + 2], floor));
            }
        }
    }

    /// <summary>Bilinear sample of an RGBA8 map at normalised (u,v), as linear 0..1 RGB.</summary>
    private static (float R, float G, float B) Bilinear(byte[] px, int w, int h, float u, float v)
    {
        float fx = u * w - 0.5f, fy = v * h - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int x1 = Math.Clamp(x0 + 1, 0, w - 1), y1 = Math.Clamp(y0 + 1, 0, h - 1);
        x0 = Math.Clamp(x0, 0, w - 1); y0 = Math.Clamp(y0, 0, h - 1);
        float R = 0, G = 0, B = 0;
        void Acc(int x, int y, float wgt)
        {
            if (wgt <= 0f) return;
            int o = (y * w + x) * 4;
            R += px[o] * wgt; G += px[o + 1] * wgt; B += px[o + 2] * wgt;
        }
        Acc(x0, y0, (1 - tx) * (1 - ty)); Acc(x1, y0, tx * (1 - ty));
        Acc(x0, y1, (1 - tx) * ty);       Acc(x1, y1, tx * ty);
        return (R / 255f, G / 255f, B / 255f);
    }

    private static byte Scale(byte v, float k) => (byte)Math.Clamp((int)(v * k + 0.5f), 0, 255);

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
