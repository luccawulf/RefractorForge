using System;
using System.Numerics;
using System.Threading;
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
    /// <param name="samples">Sub-samples per texel AXIS (2 = a 2x2 grid inside each texel). The sun test is
    /// binary — a point is blocked or it is not — so one sample per texel puts a hard staircase along every shadow
    /// edge. Averaging several turns that into a real gradient, which is most of what "jagged shadows" means.
    /// Leave at 1 for the packed <c>.lsb</c>, which is one bit per texel and cannot carry a gradient anyway.</param>
    /// <param name="objects">Placed static objects as world-space triangles. WITHOUT this the bake only asks whether
    /// the TERRAIN shadows itself, which on a flat city map is almost nothing - Saigon68 came out 5.8% shadowed and
    /// read in game as "the bake did nothing", because the buildings that actually cast the shadows were not in it.
    /// Null keeps the old heightmap-only behaviour.</param>
    /// <param name="onRow">Called once per finished row, for a progress bar. Invoked from worker threads.</param>
    public static Texture2D Bake(int size, Heightmap hm, TerrainConfig cfg, Vec3 sunDir, int blurRadius = 1, int samples = 1,
                                 MeshOccluder? objects = null, Action? onRow = null, CancellationToken cancel = default)
    {
        if (size < 1) size = 1;
        samples = Math.Clamp(samples, 1, 4);
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

        // March at HALF a texel. A ray stepping a whole texel at a time can stride straight over a thin ridge and
        // report the ground behind it lit, which reads as a shadow with holes punched along its length.
        float texel = ws / size;
        float step = texel * 0.5f;
        float bias = rise * texel * 0.5f + 0.05f;            // avoid self-shadowing on the first step
        int maxSteps = Math.Min(size * 4, (int)((maxH - minH) / MathF.Max(rise * step, 1e-3f)) + 8);

        // BILINEAR, not nearest. The heightmap is far coarser than the shadow map (a 256² heightmap under a 2048²
        // shadow is one height per 8x8 block), and sampling it nearest makes every shadow edge follow the
        // heightmap's own grid — a staircase with 8-pixel treads, which is exactly the jaggedness you see.
        float HeightAtWorld(float wx, float wz)
        {
            float fx = wx / ws * (hw - 1), fz = wz / ws * (hh - 1);
            if (fx < 0f) fx = 0f; else if (fx > hw - 1) fx = hw - 1;
            if (fz < 0f) fz = 0f; else if (fz > hh - 1) fz = hh - 1;
            int x0 = (int)fx, y0 = (int)fz;
            int x1 = x0 + 1 > hw - 1 ? hw - 1 : x0 + 1;
            int y1 = y0 + 1 > hh - 1 ? hh - 1 : y0 + 1;
            float tx = fx - x0, tz = fz - y0;
            float h00 = cfg.HeightToMeters(hm[x0, y0]), h10 = cfg.HeightToMeters(hm[x1, y0]);
            float h01 = cfg.HeightToMeters(hm[x0, y1]), h11 = cfg.HeightToMeters(hm[x1, y1]);
            return (h00 * (1f - tx) + h10 * tx) * (1f - tz) + (h01 * (1f - tx) + h11 * tx) * tz;
        }

        bool Blocked(float wx, float wz)
        {
            float rh = HeightAtWorld(wx, wz) + bias;
            float cx = wx, cz = wz;
            for (int s = 1; s <= maxSteps; s++)
            {
                cx += dirX * step; cz += dirZ * step; rh += rise * step;
                if (rh > maxH) return false;                  // ray cleared all terrain -> lit
                if (cx < 0f || cz < 0f || cx > ws || cz > ws) return false;   // marched off-map -> lit
                if (HeightAtWorld(cx, cz) > rh) return true;
            }
            return false;
        }

        var vis = new byte[size * size];
        float sub = 1f / samples;
        var sunV = Vector3.Normalize(new Vector3(sunDir.X, sunDir.Y, sunDir.Z));   // points TOWARD the sun
        // Lift the ray off the ground before testing objects: a building's own floor polygons sit ON the terrain, and
        // starting exactly at ground level makes every texel under a building shadow itself at grazing angles.
        const float groundLift = 0.25f;

        // One cursor per thread, one shared occluder. The stamp array is an int per triangle, so on a city map the
        // cursors are the memory cost - cap the threads when objects are in play rather than letting the scheduler
        // allocate one per core.
        var po = new System.Threading.Tasks.ParallelOptions { CancellationToken = cancel };
        if (objects is not null) po.MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

        System.Threading.Tasks.Parallel.For(0, size, po,
            () => objects?.NewCursor(),
            (py, _, cur) =>
        {
            for (int px = 0; px < size; px++)
            {
                int lit = 0, n = 0;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                    {
                        float wx = (px + (sx + 0.5f) * sub) / size * ws;
                        float wz = (py + (sy + 0.5f) * sub) / size * ws;   // UV-aligned: v -> worldZ (matches BakeAtlas)
                        bool blocked = Blocked(wx, wz);
                        if (!blocked && objects is not null && cur is not null)
                            blocked = objects.Occluded(new Vector3(wx, HeightAtWorld(wx, wz) + groundLift, wz), sunV, cur);
                        if (!blocked) lit++;
                        n++;
                    }
                vis[py * size + px] = (byte)Math.Clamp(lit * 255 / Math.Max(n, 1), 0, 255);
            }
            onRow?.Invoke();
            return cur;
        },
            _ => { });

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
        int gridDim, int tilePx = 1024, int bakeSize = 0, bool invertLit = true, bool flipX = false, bool flipY = false,
        MeshOccluder? objects = null, Action? onRow = null, CancellationToken cancel = default)
    {
        if (gridDim < 1) throw new ArgumentOutOfRangeException(nameof(gridDim));
        int fullSide = gridDim * tilePx;
        if (bakeSize <= 0) bakeSize = Math.Min(fullSide, 2048);

        // crisp binary shadow (no penumbra) - the .lsb is one bit per texel and cannot carry a gradient
        var baked = Bake(bakeSize, hm, cfg, sunDir, blurRadius: 0, samples: 1, objects: objects, onRow: onRow, cancel: cancel);
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

    /// <summary>A shadow map with NOTHING in shadow, for undoing a bake. The engine stores the OPPOSITE sense of
    /// <see cref="Bake"/>'s "lit" (see <see cref="BakeToLsb"/>), so all-zero visibility is a fully sunlit terrain -
    /// which is what a level looks like before anyone bakes it. Writing this beats deleting the file: the level
    /// keeps the entry it shipped, and every save path already knows how to write one.</summary>
    public static LightmapShadowBits UnshadowedLsb(int gridDim, int tilePx = 1024)
    {
        if (gridDim < 1) throw new ArgumentOutOfRangeException(nameof(gridDim));
        int fullSide = gridDim * tilePx;
        return LightmapShadowBits.FromVisibility(new byte[fullSide * fullSide], fullSide, gridDim, tilePx);
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
