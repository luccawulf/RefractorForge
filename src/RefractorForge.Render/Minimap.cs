using System;
using System.Numerics;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Render;

/// <summary>
/// Renders a level's <c>ingamemap.dds</c> / menu thumbnail: a top-down image of the terrain shaded with
/// the real texture atlas (or material/height fallback), relief hill-shading from the heightmap, and a
/// water tint below the water level. Pure CPU (no GL) so it runs headlessly and is unit-testable; the
/// viewer calls the same code for its "Generate minimap" action.
/// </summary>
public static class Minimap
{
    // 16-colour material palette mirroring the terrain shader's matColor(), for the no-atlas fallback.
    private static readonly Vector3[] MatPalette =
    {
        new(0.85f,0.75f,0.45f), new(0.30f,0.70f,0.28f), new(0.50f,0.36f,0.22f), new(0.55f,0.55f,0.58f),
        new(0.20f,0.45f,0.68f), new(0.78f,0.58f,0.28f), new(0.42f,0.58f,0.30f), new(0.78f,0.30f,0.30f),
        new(0.28f,0.55f,0.58f), new(0.62f,0.62f,0.32f), new(0.52f,0.40f,0.62f), new(0.80f,0.68f,0.52f),
        new(0.32f,0.64f,0.48f), new(0.58f,0.46f,0.34f), new(0.72f,0.72f,0.74f), new(0.85f,0.40f,0.62f),
    };

    /// <param name="flipNorthUp">Put +Z (north) at the top of the image, matching the in-game map.</param>
    /// <param name="area">
    /// The world rectangle the image covers. The engine stretches ingamemap.dds across the level's
    /// <c>game.setActiveCombatArea</c> rectangle, NOT across the whole terrain - so rendering the whole world into
    /// it puts every icon in the wrong place on any level whose combat area is a sub-rectangle. Null means the
    /// whole world, which is what 48 of the 71 BFV levels that set one ask for anyway (0 0 1024 1024).
    /// </param>
    /// <param name="supersample">
    /// Render at this multiple of <paramref name="size"/> and box-average down. A map window is a heavy DOWNSAMPLE
    /// of the terrain atlas - a 616 m window into a 4096 px atlas at 512 output throws away five texels in six - and
    /// taking ONE point sample per output pixel is most of why the result looks soft and mushy next to a retail map.
    /// 3 is a good default; the cost is quadratic.
    /// </param>
    /// <param name="objects">
    /// Placed objects as world-space triangles (see <see cref="LevelScene.ObjectTriangles"/>). Retail in-game maps
    /// read as MAPS because you can see the buildings; a terrain-only render of a city is a brown smear. These are
    /// rasterised top-down into a height mask and shaded over the ground with a dark edge, which is what puts the
    /// streets and blocks back.
    /// </param>
    /// <param name="gridDivisions">Draw a lettered reference grid this many cells across (retail uses 8). 0 = none;
    /// leave it off for the menu thumbnail, which is far too small to read one.</param>
    public static Texture2D Render(int size, Heightmap hm, TerrainConfig cfg,
                                   TerrainTexture? tex, MaterialMap? material = null, bool flipNorthUp = true,
                                   RefractorForge.Formats.Validation.CombatArea? area = null,
                                   int supersample = 1,
                                   System.Collections.Generic.IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)>? objects = null,
                                   int gridDivisions = 0)
    {
        if (size < 1) size = 1;
        supersample = Math.Clamp(supersample, 1, 4);
        int ss = size * supersample;
        // World-space window -> the 0..1 terrain coordinates everything below samples with.
        float wsz = cfg.WorldSize > 0f ? cfg.WorldSize : 1f;
        float u0 = 0f, v0 = 0f, uScale = 1f, vScale = 1f;
        if (area is { } ar && ar.Width > 0f && ar.Height > 0f)
        {
            u0 = ar.X / wsz; uScale = ar.Width / wsz;
            v0 = ar.Z / wsz; vScale = ar.Height / wsz;
        }
        var big = new Vector3[ss * ss];
        var light = Vector3.Normalize(new Vector3(-0.6f, 1.0f, -0.5f));
        var water = new Vector3(0.20f, 0.40f, 0.60f);
        int hw = hm.Width, hh = hm.Height;
        float sp = cfg.HorizontalSpacing; if (sp <= 0f) sp = 1f;

        // Bilinear height in metres, in heightmap-texel coordinates, clamped at the edges.
        float HeightAt(float fx, float fy)
        {
            fx = Math.Clamp(fx, 0f, hw - 1f); fy = Math.Clamp(fy, 0f, hh - 1f);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, hw - 1), y1 = Math.Min(y0 + 1, hh - 1);
            float tx = fx - x0, ty = fy - y0;
            float a = cfg.HeightToMeters(hm[x0, y0]), b = cfg.HeightToMeters(hm[x1, y0]);
            float c = cfg.HeightToMeters(hm[x0, y1]), d = cfg.HeightToMeters(hm[x1, y1]);
            return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
        }

        for (int py = 0; py < ss; py++)
            for (int px = 0; px < ss; px++)
            {
                float u = (px + 0.5f) / ss;
                float v = (py + 0.5f) / ss;
                if (flipNorthUp) v = 1f - v;   // image top -> max world Z
                // Clamped, not wrapped: a combat area may start negative (Faid_Pass uses -65) or run past the
                // terrain, and SampleUv wraps, which would fold the far edge of the map into the near one.
                u = Math.Clamp(u0 + u * uScale, 0f, 1f);
                v = Math.Clamp(v0 + v * vScale, 0f, 1f);

                // Base colour: real terrain atlas if available, else material palette, else flat.
                Vector3 col;
                if (tex is not null) col = tex.SampleUv(u, v);
                else if (material is not null)
                {
                    int mx = Math.Clamp((int)(u * material.Width), 0, material.Width - 1);
                    int my = Math.Clamp((int)(v * material.Height), 0, material.Height - 1);
                    col = MatPalette[material[mx, my] & 15];
                }
                else col = new Vector3(0.45f, 0.50f, 0.40f);

                // Relief hill-shade from the heightmap (central differences -> surface normal). Sampled BILINEARLY
                // and at a sub-texel step: point-sampling a 256-cell heightmap under a 512+ image quantises the
                // relief into 4 m blocks, which is the staircase look on the old renders.
                float fx = u * (hw - 1), fy = v * (hh - 1);
                float hL = HeightAt(fx - 0.5f, fy), hR = HeightAt(fx + 0.5f, fy);
                float hDn = HeightAt(fx, fy - 0.5f), hUp = HeightAt(fx, fy + 0.5f);
                var n = Vector3.Normalize(new Vector3(hL - hR, sp, hDn - hUp));
                col *= 0.5f + 0.5f * MathF.Max(0f, Vector3.Dot(n, light));

                // Water: tint below the water level, deeper = bluer.
                float hC = HeightAt(fx, fy);
                if (hC < cfg.WaterLevel)
                {
                    float depth = Math.Clamp((cfg.WaterLevel - hC) / 12f, 0f, 1f);
                    col = Vector3.Lerp(col, water, 0.45f + 0.4f * depth);
                }

                big[py * ss + px] = col;
            }

        if (objects is { Count: > 0 })
            DrawObjects(big, ss, HeightAt, hw, hh, wsz, u0, v0, uScale, vScale, flipNorthUp, objects);

        // Box-average back down to the requested size.
        var rgba = new byte[size * size * 4];
        float inv = 1f / (supersample * supersample);
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                var acc = Vector3.Zero;
                for (int sy = 0; sy < supersample; sy++)
                    for (int sx = 0; sx < supersample; sx++)
                        acc += big[(py * supersample + sy) * ss + (px * supersample + sx)];
                acc *= inv;
                acc = MapCurve(acc);
                int i = (py * size + px) * 4;
                rgba[i] = Byte(acc.X); rgba[i + 1] = Byte(acc.Y); rgba[i + 2] = Byte(acc.Z); rgba[i + 3] = 255;
            }
        if (gridDivisions > 1) DrawGrid(rgba, size, gridDivisions);
        return new Texture2D(size, size, rgba);
    }

    /// <summary>
    /// Rasterise the placed objects top-down and shade them over the ground.
    /// <para>
    /// Keeps the HIGHEST world Y per pixel, so a roof wins over the wall below it and what comes out is a real
    /// footprint rather than a soup of overlapping triangles. Anything standing less than a metre above the terrain
    /// is then dropped - that is ground clutter and flat decals, which would otherwise paint big meaningless slabs.
    /// Buildings are lightened by how tall they stand (so a hut and a tower read differently) and the mask's edge is
    /// darkened, which is what draws the streets between the blocks.
    /// </para>
    /// </summary>
    private static void DrawObjects(Vector3[] big, int ss, Func<float, float, float> heightAt, int hw, int hh,
                                    float wsz, float u0, float v0, float uScale, float vScale, bool flipNorthUp,
                                    System.Collections.Generic.IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris)
    {
        var top = new float[ss * ss];
        var has = new bool[ss * ss];

        // World -> pixel. The window maps [u0, u0+uScale] across the image; v is flipped when north is up.
        float PX(float wx) => (wx / wsz - u0) / uScale * ss;
        float PY(float wz)
        {
            float v = (wz / wsz - v0) / vScale;
            if (flipNorthUp) v = 1f - v;
            return v * ss;
        }

        foreach (var (a, b, c) in tris)
        {
            float ax = PX(a.X), ay = PY(a.Z), bx = PX(b.X), by = PY(b.Z), cx = PX(c.X), cy = PY(c.Z);
            int minX = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)));
            int maxX = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
            int minY = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)));
            int maxY = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
            if (maxX < 0 || maxY < 0 || minX >= ss || minY >= ss) continue;
            minX = Math.Max(minX, 0); minY = Math.Max(minY, 0);
            maxX = Math.Min(maxX, ss - 1); maxY = Math.Min(maxY, ss - 1);
            float area2 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (MathF.Abs(area2) < 1e-6f) continue;      // edge-on from above: contributes no footprint
            float invArea = 1f / area2;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float pxc = x + 0.5f, pyc = y + 0.5f;
                    float w1 = ((pxc - ax) * (cy - ay) - (pyc - ay) * (cx - ax)) * invArea;
                    float w2 = ((bx - ax) * (pyc - ay) - (by - ay) * (pxc - ax)) * invArea;
                    if (w1 < 0f || w2 < 0f || w1 + w2 > 1f) continue;
                    float yWorld = a.Y + (b.Y - a.Y) * w1 + (c.Y - a.Y) * w2;
                    int i = y * ss + x;
                    if (!has[i] || yWorld > top[i]) { top[i] = yWorld; has[i] = true; }
                }
        }

        var above = new float[ss * ss];
        for (int y = 0; y < ss; y++)
            for (int x = 0; x < ss; x++)
            {
                int i = y * ss + x;
                if (!has[i]) continue;
                float u = Math.Clamp(u0 + (x + 0.5f) / ss * uScale, 0f, 1f);
                float vv = (y + 0.5f) / ss; if (flipNorthUp) vv = 1f - vv;
                float v = Math.Clamp(v0 + vv * vScale, 0f, 1f);
                above[i] = top[i] - heightAt(u * (hw - 1), v * (hh - 1));
                if (above[i] < 1f) has[i] = false;   // clutter and flat decals, not buildings
            }

        // Drop anything whose footprint is too small to be a building. Trees keep their TRUNK after the foliage
        // cards are skipped, and a forest of one-pixel trunks is just speckle over the water and the jungle. Four
        // square metres separates them cleanly: a trunk is well under one, the smallest hut is many times it.
        float pxPerM = ss / MathF.Max(uScale * wsz, 1e-3f);
        int minArea = Math.Max(4, (int)(4f * pxPerM * pxPerM));
        var stack = new System.Collections.Generic.List<int>();
        var blob = new System.Collections.Generic.List<int>();
        var seen = new bool[ss * ss];
        for (int start = 0; start < has.Length; start++)
        {
            if (!has[start] || seen[start]) continue;
            blob.Clear(); stack.Clear(); stack.Add(start); seen[start] = true;
            while (stack.Count > 0)
            {
                int i = stack[^1]; stack.RemoveAt(stack.Count - 1);
                blob.Add(i);
                int x = i % ss, y = i / ss;
                if (x > 0 && has[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Add(i - 1); }
                if (x < ss - 1 && has[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Add(i + 1); }
                if (y > 0 && has[i - ss] && !seen[i - ss]) { seen[i - ss] = true; stack.Add(i - ss); }
                if (y < ss - 1 && has[i + ss] && !seen[i + ss]) { seen[i + ss] = true; stack.Add(i + ss); }
            }
            if (blob.Count < minArea) foreach (var i in blob) has[i] = false;
        }

        for (int i = 0; i < has.Length; i++)
        {
            if (!has[i]) continue;
            // Pale roof, lighter the taller it stands, flattening out by ~15 m so a tower does not blow out.
            float t = Math.Clamp(0.35f + 0.45f * (above[i] / 15f), 0.35f, 0.80f);
            big[i] = Vector3.Lerp(big[i], new Vector3(0.72f, 0.70f, 0.66f), t);
        }

        // Edge pass, on the post-cull mask: darken any covered pixel that borders an uncovered one.
        var edge = new bool[ss * ss];
        for (int y = 0; y < ss; y++)
            for (int x = 0; x < ss; x++)
            {
                int i = y * ss + x;
                if (!has[i]) continue;
                edge[i] = (x == 0 || !has[i - 1]) || (x == ss - 1 || !has[i + 1])
                       || (y == 0 || !has[i - ss]) || (y == ss - 1 || !has[i + ss]);
            }
        for (int i = 0; i < edge.Length; i++)
            if (edge[i]) big[i] *= 0.45f;
    }

    /// <summary>
    /// Re-cut an existing in-game map image from the world rectangle it covers to a different one.
    /// <para>
    /// The engine stretches <c>ingamemap.dds</c> over the level's combat area, so moving that area moves every
    /// icon relative to the art underneath. Re-rendering from the terrain would fix the alignment and throw away
    /// the map, which on most levels is hand-drawn - grid letters, unit icons, a painted out-of-bounds boundary.
    /// This keeps the drawing and moves it instead. Bilinear, because the crop is not a whole number of pixels;
    /// clamped at the edges, so an area reaching past the source just repeats its border rather than wrapping.
    /// </para>
    /// Always re-cut from the level's ORIGINAL art rather than from the last result - resampling a resample
    /// softens the image a little more every save.
    /// </summary>
    public static Texture2D Refit(Texture2D src,
                                  RefractorForge.Formats.Validation.CombatArea srcArea,
                                  RefractorForge.Formats.Validation.CombatArea dstArea,
                                  int size)
    {
        if (size < 1) size = 1;
        if (srcArea.Width <= 0f || srcArea.Height <= 0f) return src;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Destination pixel -> world, north-up (image top is max Z) -> the source image's own UV.
                float wx = dstArea.X + (x + 0.5f) / size * dstArea.Width;
                float wz = dstArea.Z + (1f - (y + 0.5f) / size) * dstArea.Height;
                float su = (wx - srcArea.X) / srcArea.Width * src.Width - 0.5f;
                float sv = (1f - (wz - srcArea.Z) / srcArea.Height) * src.Height - 0.5f;
                int x0 = (int)MathF.Floor(su), y0 = (int)MathF.Floor(sv);
                float fx = su - x0, fy = sv - y0;
                int i = (y * size + x) * 4;
                for (int c = 0; c < 4; c++)
                    rgba[i + c] = (byte)Math.Clamp(
                        Lerp(Lerp(At(src, x0, y0, c), At(src, x0 + 1, y0, c), fx),
                             Lerp(At(src, x0, y0 + 1, c), At(src, x0 + 1, y0 + 1, c), fx), fy) + 0.5f, 0f, 255f);
            }
        return new Texture2D(size, size, rgba);
    }

    /// <summary>
    /// Lift a straight terrain render toward the brightness a retail in-game map is drawn at. Measured on
    /// Operation_Irving's shipped <c>InGameMap.dds</c>: mean channel ~107 against ~72 for the raw render, and a
    /// wider spread (5th-95th percentile 24-190 vs 13-141). Gamma 0.72 with a touch of contrast about mid-grey
    /// lands on both. Ground textures are lit for standing IN the level, not for reading from above, so without
    /// this a jungle map comes out as a dark green-brown smear no matter how much detail is in it.
    /// </summary>
    // A 5x7 bitmap font, five bits per row, for the grid labels. Small enough to inline and it removes any
    // dependency on a system font being present in a headless render.
    private const string GlyphChars = "ABCDEFGHIJKLMNOP0123456789";
    private static readonly byte[][] Glyphs =
    {
        new byte[]{0x0E,0x11,0x11,0x1F,0x11,0x11,0x11}, // A
        new byte[]{0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E}, // B
        new byte[]{0x0E,0x11,0x10,0x10,0x10,0x11,0x0E}, // C
        new byte[]{0x1E,0x11,0x11,0x11,0x11,0x11,0x1E}, // D
        new byte[]{0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F}, // E
        new byte[]{0x1F,0x10,0x10,0x1E,0x10,0x10,0x10}, // F
        new byte[]{0x0E,0x11,0x10,0x17,0x11,0x11,0x0F}, // G
        new byte[]{0x11,0x11,0x11,0x1F,0x11,0x11,0x11}, // H
        new byte[]{0x1F,0x04,0x04,0x04,0x04,0x04,0x1F}, // I
        new byte[]{0x07,0x02,0x02,0x02,0x02,0x12,0x0C}, // J
        new byte[]{0x11,0x12,0x14,0x18,0x14,0x12,0x11}, // K
        new byte[]{0x10,0x10,0x10,0x10,0x10,0x10,0x1F}, // L
        new byte[]{0x11,0x1B,0x15,0x15,0x11,0x11,0x11}, // M
        new byte[]{0x11,0x19,0x15,0x13,0x11,0x11,0x11}, // N
        new byte[]{0x0E,0x11,0x11,0x11,0x11,0x11,0x0E}, // O
        new byte[]{0x1E,0x11,0x11,0x1E,0x10,0x10,0x10}, // P
        new byte[]{0x0E,0x11,0x13,0x15,0x19,0x11,0x0E}, // 0
        new byte[]{0x04,0x0C,0x04,0x04,0x04,0x04,0x0E}, // 1
        new byte[]{0x0E,0x11,0x01,0x02,0x04,0x08,0x1F}, // 2
        new byte[]{0x1F,0x02,0x04,0x02,0x01,0x11,0x0E}, // 3
        new byte[]{0x02,0x06,0x0A,0x12,0x1F,0x02,0x02}, // 4
        new byte[]{0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E}, // 5
        new byte[]{0x06,0x08,0x10,0x1E,0x11,0x11,0x0E}, // 6
        new byte[]{0x1F,0x01,0x02,0x04,0x08,0x08,0x08}, // 7
        new byte[]{0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E}, // 8
        new byte[]{0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C}, // 9
    };

    /// <summary>
    /// Overlay the lettered reference grid every retail in-game map carries - columns A.. across the top, rows 1..
    /// down the left. It is not decoration: it is how players call positions out to each other, and its absence is
    /// most of why a plain terrain render reads as "less detailed" than a shipped map even when it shows more.
    /// Drawn at OUTPUT resolution, after the downsample, so the lines stay one pixel wide and crisp.
    /// Labels get a light halo so they survive over both dark water and pale rooftops.
    /// </summary>
    private static void DrawGrid(byte[] rgba, int size, int divisions)
    {
        if (divisions < 2 || divisions > 26) return;
        void Blend(int x, int y, Vector3 c, float a)
        {
            if ((uint)x >= (uint)size || (uint)y >= (uint)size) return;
            int i = (y * size + x) * 4;
            for (int k = 0; k < 3; k++)
            {
                float o = rgba[i + k] / 255f, n = k == 0 ? c.X : k == 1 ? c.Y : c.Z;
                rgba[i + k] = Byte(o + (n - o) * a);
            }
        }

        var line = new Vector3(0.10f, 0.10f, 0.12f);
        for (int d = 1; d < divisions; d++)
        {
            int p = (int)((float)d / divisions * size + 0.5f);
            for (int q = 0; q < size; q++) { Blend(p, q, line, 0.40f); Blend(q, p, line, 0.40f); }
        }

        // Labels: one glyph per column/row, in the leading corner of its cell.
        int scale = Math.Max(1, size / 256);            // 2 px per font pixel at 512
        float cell = (float)size / divisions;
        void Glyph(char ch, int ox, int oy)
        {
            int gi = GlyphChars.IndexOf(ch);
            if (gi < 0) return;
            var g = Glyphs[gi];
            for (int ry = 0; ry < 7; ry++)
                for (int rx = 0; rx < 5; rx++)
                {
                    if ((g[ry] & (1 << (4 - rx))) == 0) continue;
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int px = ox + rx * scale + sx, py = oy + ry * scale + sy;
                            // Halo first, then the glyph on top of it.
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                    Blend(px + dx, py + dy, new Vector3(0.92f), 0.55f);
                        }
                }
            for (int ry = 0; ry < 7; ry++)
                for (int rx = 0; rx < 5; rx++)
                {
                    if ((g[ry] & (1 << (4 - rx))) == 0) continue;
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                            Blend(ox + rx * scale + sx, oy + ry * scale + sy, new Vector3(0.08f), 0.95f);
                }
        }
        for (int d = 0; d < divisions; d++)
        {
            Glyph((char)('A' + d), (int)(d * cell + cell * 0.5f) - 2 * scale, 2 * scale);
            var num = (d + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            int ny = (int)(d * cell + cell * 0.5f) - 3 * scale;
            for (int c = 0; c < num.Length; c++) Glyph(num[c], 2 * scale + c * 6 * scale, ny);
        }
    }

    private static Vector3 MapCurve(Vector3 c)
    {
        float F(float v)
        {
            v = MathF.Pow(Math.Clamp(v, 0f, 1f), 0.72f);
            return Math.Clamp(0.5f + (v - 0.5f) * 1.12f, 0f, 1f);
        }
        return new Vector3(F(c.X), F(c.Y), F(c.Z));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float At(Texture2D t, int x, int y, int c)
        => t.Rgba[(Math.Clamp(y, 0, t.Height - 1) * t.Width + Math.Clamp(x, 0, t.Width - 1)) * 4 + c];

    private static byte Byte(float c) => (byte)(Math.Clamp(c, 0f, 1f) * 255f + 0.5f);
}
