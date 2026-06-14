using System;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Bakes a terrain sun cast-shadow (visibility) map by ray-marching the sun direction against the
/// heightmap: a texel is shadowed when terrain between it and the sun rises above the sun ray. Pure CPU,
/// so it runs headlessly and is testable. The map is UV-aligned the same way as <see cref="TerrainTexture.BakeAtlas"/>
/// (texel (x,y) -> world (x/size*worldSize, y/size*worldSize)), so the terrain shader can sample it with the
/// very same UV it uses for the ground texture and the shadows land in the right place.
///
/// NOTE: this is the editor's own bake/preview/export. It is NOT the engine's packed <c>LightmapShadowBits.lsb</c>
/// (a run-length format that isn't fully reverse-engineered yet); writing that back is a separate step, so a
/// bake here shows + exports shadows but does not change in-game lighting until the .lsb encoder exists.
/// </summary>
public static class TerrainShadow
{
    /// <summary>Visibility map: 255 = fully lit by the sun, 0 = in cast shadow (soft penumbra in between).</summary>
    public static Texture2D Bake(int size, Heightmap hm, TerrainConfig cfg, Vec3 sunDir, int blurRadius = 1)
    {
        if (size < 1) size = 1;
        int hw = hm.Width, hh = hm.Height;
        float ws = cfg.WorldSize;

        // Sun horizontal step + vertical rise per horizontal metre (sunDir points toward the sun).
        float horiz = MathF.Sqrt(sunDir.X * sunDir.X + sunDir.Z * sunDir.Z);
        if (horiz < 1e-4f) horiz = 1e-4f;
        float dirX = sunDir.X / horiz, dirZ = sunDir.Z / horiz;
        float rise = MathF.Max(sunDir.Y, 0.02f) / horiz;

        float minH = float.MaxValue, maxH = float.MinValue;
        for (int i = 0; i < hm.Samples.Length; i++)
        { float m = cfg.HeightToMeters(hm.Samples[i]); if (m < minH) minH = m; if (m > maxH) maxH = m; }

        float step = ws / size;                              // one output texel, in world metres
        float bias = rise * step * 0.5f + 0.05f;             // avoid self-shadowing on the first step
        int maxSteps = Math.Min(size * 2, (int)((maxH - minH) / MathF.Max(rise * step, 1e-3f)) + 4);

        float HeightAtWorld(float wx, float wz)
        {
            float fx = wx / ws * (hw - 1), fz = wz / ws * (hh - 1);
            int x = (int)(fx + 0.5f), y = (int)(fz + 0.5f);
            if (x < 0) x = 0; else if (x > hw - 1) x = hw - 1;
            if (y < 0) y = 0; else if (y > hh - 1) y = hh - 1;
            return cfg.HeightToMeters(hm[x, y]);
        }

        var vis = new byte[size * size];
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float wx = (px + 0.5f) / size * ws;
                float wz = (py + 0.5f) / size * ws;          // UV-aligned: v -> worldZ (matches BakeAtlas)
                float rh = HeightAtWorld(wx, wz) + bias;
                float cx = wx, cz = wz;
                bool blocked = false;
                for (int s = 1; s <= maxSteps; s++)
                {
                    cx += dirX * step; cz += dirZ * step; rh += rise * step;
                    if (rh > maxH) break;                    // ray cleared all terrain -> lit
                    if (cx < 0f || cz < 0f || cx > ws || cz > ws) break;   // marched off-map -> lit
                    if (HeightAtWorld(cx, cz) > rh) { blocked = true; break; }
                }
                vis[py * size + px] = blocked ? (byte)0 : (byte)255;
            }

        if (blurRadius > 0) BoxBlur(vis, size, size, blurRadius);

        var rgba = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        { byte b = vis[i]; rgba[i * 4] = b; rgba[i * 4 + 1] = b; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255; }
        return new Texture2D(size, size, rgba);
    }

    /// <summary>
    /// Bake the sun cast-shadow and pack it as a game-readable <see cref="LightmapShadowBits"/> (the engine's
    /// <c>LightmapShadowBits.lsb</c>). The .lsb is a <paramref name="gridDim"/>² grid of <paramref name="tilePx"/>-square
    /// per-patch lightmaps. Shadow detail is limited by the heightmap, so we ray-march at a modest
    /// <paramref name="bakeSize"/> and nearest-upsample into the full grid (baking 8192² directly would be far too slow).
    /// Visibility is binarised because the .lsb carries a single bit per run.
    ///
    /// Orientation/polarity verified against Operation_Irving's real .lsb (all 8 dihedral transforms ×
    /// both polarities): the layout is IDENTITY (no flip/rotation — every rotation scored worse) and the
    /// stored bit is the OPPOSITE sense of <see cref="Bake"/>'s "lit" — so we invert it here
    /// (<paramref name="invertLit"/>), which lifts pixel agreement from ~14% to ~85%. The residual gap is
    /// the ray-marcher casting more shadow than the engine, not a structural error. Still worth an in-game
    /// look before fully trusting (X mirror is invisible here because the test shadow is near-X-symmetric).
    /// </summary>
    public static LightmapShadowBits BakeToLsb(Heightmap hm, TerrainConfig cfg, Vec3 sunDir,
        int gridDim, int tilePx = 1024, int bakeSize = 0, bool invertLit = true, bool flipX = false, bool flipY = false)
    {
        if (gridDim < 1) throw new ArgumentOutOfRangeException(nameof(gridDim));
        int fullSide = gridDim * tilePx;
        if (bakeSize <= 0) bakeSize = Math.Min(fullSide, 2048);

        var baked = Bake(bakeSize, hm, cfg, sunDir, blurRadius: 0);   // crisp binary shadow (no penumbra)
        var full = new byte[fullSide * fullSide];
        // flipX/flipY mirror the written raster so the user can correct an in-game L/R or top/bottom shadow mirror
        // (the offline polarity test can't see an X-mirror — the test shadow is near-X-symmetric) without a recompile.
        for (int y = 0; y < fullSide; y++)
        {
            int dstRow = y * fullSide;
            int fy = flipY ? (fullSide - 1 - y) : y;
            int sy = fy * bakeSize / fullSide;
            int srcRow = sy * bakeSize;
            for (int x = 0; x < fullSide; x++)
            {
                int fx = flipX ? (fullSide - 1 - x) : x;
                int sx = fx * bakeSize / fullSide;
                bool lit = baked.Rgba[(srcRow + sx) * 4] >= 128;       // Bake: 255 = lit by sun
                if (invertLit) lit = !lit;                             // engine stores the opposite sense
                full[dstRow + x] = lit ? (byte)255 : (byte)0;
            }
        }
        return LightmapShadowBits.FromVisibility(full, fullSide, gridDim, tilePx);
    }

    /// <summary>The terrain height span in metres (min, max) — precompute once and pass to <see cref="PointLit"/>.</summary>
    public static (float Min, float Max) HeightSpan(Heightmap hm, TerrainConfig cfg)
    {
        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < hm.Samples.Length; i++) { float m = cfg.HeightToMeters(hm.Samples[i]); if (m < mn) mn = m; if (m > mx) mx = m; }
        return (mn, mx);
    }

    /// <summary>Is a single world point lit by the sun, or in the terrain's cast shadow? Ray-marches the heightmap from
    /// the point toward the sun (sunDir points TOWARD the sun). Reused by the per-object lightmap baker so baked object
    /// lighting matches the terrain shadow. <paramref name="maxH"/> is the terrain's max height (see <see cref="HeightSpan"/>).</summary>
    public static bool PointLit(float wx, float wy, float wz, Vec3 sunDir, Heightmap hm, TerrainConfig cfg, float maxH)
    {
        float ws = cfg.WorldSize; int hw = hm.Width, hh = hm.Height;
        float horiz = MathF.Sqrt(sunDir.X * sunDir.X + sunDir.Z * sunDir.Z); if (horiz < 1e-4f) horiz = 1e-4f;
        float dirX = sunDir.X / horiz, dirZ = sunDir.Z / horiz;
        float rise = MathF.Max(sunDir.Y, 0.02f) / horiz;
        float step = ws / 1024f;
        float cx = wx, cz = wz, rh = wy + 0.35f;   // small bias off the surface so it doesn't self-shadow
        int maxSteps = 2200;
        for (int s = 1; s <= maxSteps; s++)
        {
            cx += dirX * step; cz += dirZ * step; rh += rise * step;
            if (rh > maxH) return true;                                  // cleared all terrain -> lit
            if (cx < 0f || cz < 0f || cx > ws || cz > ws) return true;   // off-map -> lit
            float fx = cx / ws * (hw - 1), fz = cz / ws * (hh - 1);
            int hx = Math.Clamp((int)(fx + 0.5f), 0, hw - 1), hy = Math.Clamp((int)(fz + 0.5f), 0, hh - 1);
            if (cfg.HeightToMeters(hm[hx, hy]) > rh) return false;       // terrain occludes -> shadow
        }
        return true;
    }

    /// <summary>Separable box blur on a single-channel buffer (softens hard shadow edges into a penumbra).</summary>
    private static void BoxBlur(byte[] a, int w, int h, int r)
    {
        var tmp = new byte[a.Length];
        int win = 2 * r + 1;
        for (int y = 0; y < h; y++)        // horizontal
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) { int xx = Math.Clamp(x + k, 0, w - 1); sum += a[y * w + xx]; }
                tmp[y * w + x] = (byte)(sum / win);
            }
        for (int y = 0; y < h; y++)        // vertical
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) { int yy = Math.Clamp(y + k, 0, h - 1); sum += tmp[yy * w + x]; }
                a[y * w + x] = (byte)(sum / win);
            }
    }
}
