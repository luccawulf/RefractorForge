using System;
using System.Collections.Generic;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Render;

namespace RefractorForge.Viewer;

/// <summary>
/// One continuous terrain-texture paint stroke. Composites a tiled surface texture into the live terrain
/// atlas (the visible ground colour) under a radius / hardness / intensity brush, capturing each pixel's
/// original colour the first time it is touched so the whole drag coalesces into a single undo step. Pure
/// CPU work on the atlas RGBA — the viewer re-uploads the dirty rectangle to the GPU for a live preview and,
/// on save, splits the painted atlas back into the level's txCxR.dds tiles.
/// </summary>
/// <summary>The surface-painter brushes that adjust the existing terrain image rather than stamping a texture
/// onto it (Battlecraft guide figure 19).</summary>
enum AtlasAdjust { Darken, Lighten, Blur, Color }

sealed class AtlasPaintStroke
{
    private readonly Texture2D _atlas;
    private readonly float _worldSize;
    private readonly Dictionary<int, uint> _orig = new();   // pixel index -> original packed RGBA
    private int _minX = int.MaxValue, _minY = int.MaxValue, _maxX = int.MinValue, _maxY = int.MinValue;

    public AtlasPaintStroke(Texture2D atlas, float worldSize) { _atlas = atlas; _worldSize = worldSize; }

    public bool Empty => _orig.Count == 0;
    // The bounding box of the most recent Dab (clamped to the atlas) -> the viewer uploads just this rect live.
    public int LastX, LastY, LastW, LastH;

    /// <summary>Stamp the brush once, centred at world (x, z) metres, blending <paramref name="tex"/> (tiled
    /// every <paramref name="tileMeters"/>) toward the atlas by falloff(hardness) × intensity.</summary>
    public void Dab(Texture2D tex, float worldX, float worldZ, float radiusMeters, float hardness, float intensity, bool square, float tileMeters, bool useAlpha = false)
    {
        int n = _atlas.Width;
        float ws = _worldSize <= 0f ? 1f : _worldSize;
        float cx = worldX / ws * n, cy = worldZ / ws * n;
        float rp = MathF.Max(radiusMeters / ws * n, 1f);
        int x0 = Math.Max(0, (int)MathF.Floor(cx - rp)), x1 = Math.Min(n - 1, (int)MathF.Ceiling(cx + rp));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - rp)), y1 = Math.Min(n - 1, (int)MathF.Ceiling(cy + rp));
        LastW = 0; LastH = 0;
        if (x0 > x1 || y0 > y1) return;
        LastX = x0; LastY = y0; LastW = x1 - x0 + 1; LastH = y1 - y0 + 1;   // this dab's rect, for live upload

        float tm = MathF.Max(tileMeters, 0.1f);
        float edge = 1f - Math.Clamp(hardness, 0f, 1f);   // hardness 1 = hard disc; <1 = soft edge ramp
        float inten = Math.Clamp(intensity, 0f, 1f);
        var px = _atlas.Rgba;

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) - cx, dy = (y + 0.5f) - cy;
                float t = square ? MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) / rp
                                 : MathF.Sqrt(dx * dx + dy * dy) / rp;
                if (t > 1f) continue;
                float f = edge <= 1e-4f ? 1f : Math.Clamp((1f - t) / edge, 0f, 1f);
                float w = f * inten;
                if (w <= 0f) continue;

                float wx = (x + 0.5f) / n * ws, wz = (y + 0.5f) / n * ws;   // this pixel's world position
                var tc = tex.Sample(wx / tm, wz / tm);                       // tiled surface colour (wraps)
                // Alpha mask (decal/splat): scale this texel's paint weight by the source texture's alpha, so a
                // tileable texture with cut-out transparency paints only where it's opaque (skips clear texels).
                if (useAlpha) { w *= tex.SampleRGBA(wx / tm, wz / tm).W; if (w <= 0f) continue; }

                int pi = y * n + x, idx = pi * 4;
                if (!_orig.ContainsKey(pi))
                {
                    _orig[pi] = (uint)(px[idx] | (px[idx + 1] << 8) | (px[idx + 2] << 16) | (px[idx + 3] << 24));
                    if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
                    if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
                }
                px[idx]     = (byte)(px[idx]     + (tc.X * 255f - px[idx])     * w + 0.5f);
                px[idx + 1] = (byte)(px[idx + 1] + (tc.Y * 255f - px[idx + 1]) * w + 0.5f);
                px[idx + 2] = (byte)(px[idx + 2] + (tc.Z * 255f - px[idx + 2]) * w + 0.5f);
            }
    }

    /// <summary>Battlecraft's surface-painter brushes that do not stamp a texture but ADJUST what is already on the
    /// terrain (guide figure 19): darken, lighten, blend (blur), and paint a flat colour. Shares this stroke's undo
    /// bookkeeping and live-upload rect with <see cref="Dab"/>, so all the surface brushes behave identically.</summary>
    public void DabAdjust(AtlasAdjust mode, System.Numerics.Vector3 color, float worldX, float worldZ,
                          float radiusMeters, float hardness, float intensity, bool square)
    {
        int n = _atlas.Width;
        float ws = _worldSize <= 0f ? 1f : _worldSize;
        float cx = worldX / ws * n, cy = worldZ / ws * n;
        float rp = MathF.Max(radiusMeters / ws * n, 1f);
        int x0 = Math.Max(0, (int)MathF.Floor(cx - rp)), x1 = Math.Min(n - 1, (int)MathF.Ceiling(cx + rp));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - rp)), y1 = Math.Min(n - 1, (int)MathF.Ceiling(cy + rp));
        LastW = 0; LastH = 0;
        if (x0 > x1 || y0 > y1) return;
        LastX = x0; LastY = y0; LastW = x1 - x0 + 1; LastH = y1 - y0 + 1;

        float edge = 1f - Math.Clamp(hardness, 0f, 1f);
        float inten = Math.Clamp(intensity, 0f, 1f);
        var px = _atlas.Rgba;

        // Blur reads its neighbours, so it must sample a SNAPSHOT of the rect - reading half-written pixels would
        // smear the stroke in the direction of the scan rather than blending evenly.
        byte[]? snap = null;
        if (mode == AtlasAdjust.Blur)
        {
            snap = new byte[LastW * LastH * 4];
            for (int y = 0; y < LastH; y++)
                Array.Copy(px, ((y0 + y) * n + x0) * 4, snap, y * LastW * 4, LastW * 4);
        }

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) - cx, dy = (y + 0.5f) - cy;
                float t = square ? MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) / rp
                                 : MathF.Sqrt(dx * dx + dy * dy) / rp;
                if (t > 1f) continue;
                float f = edge <= 1e-4f ? 1f : Math.Clamp((1f - t) / edge, 0f, 1f);
                float w = f * inten;
                if (w <= 0f) continue;

                int pi = y * n + x, idx = pi * 4;
                if (!_orig.ContainsKey(pi))
                {
                    _orig[pi] = (uint)(px[idx] | (px[idx + 1] << 8) | (px[idx + 2] << 16) | (px[idx + 3] << 24));
                    if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
                    if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
                }

                float tr, tg, tb;   // the colour this texel is being pulled toward
                switch (mode)
                {
                    case AtlasAdjust.Darken:  tr = 0f; tg = 0f; tb = 0f; break;
                    case AtlasAdjust.Lighten: tr = 255f; tg = 255f; tb = 255f; break;
                    case AtlasAdjust.Color:   tr = color.X * 255f; tg = color.Y * 255f; tb = color.Z * 255f; break;
                    default:                  // Blur: the 3x3 mean from the snapshot
                    {
                        float sr = 0f, sg = 0f, sb = 0f; int cnt = 0;
                        for (int oy = -1; oy <= 1; oy++)
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                int sx = x + ox - x0, sy = y + oy - y0;
                                if (sx < 0 || sy < 0 || sx >= LastW || sy >= LastH) continue;
                                int si = (sy * LastW + sx) * 4;
                                sr += snap![si]; sg += snap[si + 1]; sb += snap[si + 2]; cnt++;
                            }
                        if (cnt == 0) continue;
                        tr = sr / cnt; tg = sg / cnt; tb = sb / cnt;
                        break;
                    }
                }
                // Darken/Lighten move only PART of the way to black/white per dab, so they build up gradually the
                // way a photo-editor dodge/burn brush does instead of flattening to pure black on one click.
                float k = mode is AtlasAdjust.Darken or AtlasAdjust.Lighten ? w * 0.5f : w;
                px[idx]     = (byte)Math.Clamp(px[idx]     + (tr - px[idx])     * k + 0.5f, 0f, 255f);
                px[idx + 1] = (byte)Math.Clamp(px[idx + 1] + (tg - px[idx + 1]) * k + 0.5f, 0f, 255f);
                px[idx + 2] = (byte)Math.Clamp(px[idx + 2] + (tb - px[idx + 2]) * k + 0.5f, 0f, 255f);
            }
    }

    /// <summary>Paint a smooth ROAD band: blend <paramref name="tex"/> into the atlas along the polyline
    /// <paramref name="pts"/> (world XZ metres), solid out to <paramref name="halfWidth"/> then feathered to zero
    /// over the next <paramref name="feather"/> metres, capped at <paramref name="intensity"/>. Unlike a sweep of
    /// round Dabs, each texel is touched ONCE (its max coverage over all segments), so the edge can't scallop and
    /// the intensity can't over-saturate where dabs would overlap. This is what gives the road clean, soft edges.</summary>
    public void Sweep(Texture2D tex, IReadOnlyList<(float X, float Z)> pts, float halfWidth, float feather, float intensity, float tileMeters)
    {
        if (pts.Count < 2) return;
        int n = _atlas.Width;
        float ws = _worldSize <= 0f ? 1f : _worldSize;
        float tm = MathF.Max(tileMeters, 0.1f);
        float inten = Math.Clamp(intensity, 0f, 1f);
        float fth = MathF.Max(feather, 0.01f);          // metres of soft outer edge (>=0.01 so the edge is at least 1 ramp)
        float reach = halfWidth + fth;                  // texels are considered out to here

        // Per-texel MAX coverage over every segment (0..1). Only band texels land in the dict, so it stays small.
        var cov = new Dictionary<int, float>();
        for (int s = 0; s + 1 < pts.Count; s++)
        {
            float ax = pts[s].X, az = pts[s].Z, bx = pts[s + 1].X, bz = pts[s + 1].Z;
            float ex = bx - ax, ez = bz - az;
            float elen2 = ex * ex + ez * ez; if (elen2 < 1e-6f) elen2 = 1e-6f;
            int x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(ax, bx) - reach) / ws * n));
            int x1 = Math.Min(n - 1, (int)MathF.Ceiling((MathF.Max(ax, bx) + reach) / ws * n));
            int y0 = Math.Max(0, (int)MathF.Floor((MathF.Min(az, bz) - reach) / ws * n));
            int y1 = Math.Min(n - 1, (int)MathF.Ceiling((MathF.Max(az, bz) + reach) / ws * n));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float wx = (x + 0.5f) / n * ws, wz = (y + 0.5f) / n * ws;
                    float tparam = Math.Clamp(((wx - ax) * ex + (wz - az) * ez) / elen2, 0f, 1f);   // nearest point on segment
                    float ddx = wx - (ax + ex * tparam), ddz = wz - (az + ez * tparam);
                    float dist = MathF.Sqrt(ddx * ddx + ddz * ddz);
                    float c = Math.Clamp((reach - dist) / fth, 0f, 1f);   // 1 inside halfWidth, ramps to 0 at halfWidth+feather
                    if (c <= 0f) continue;
                    int pi = y * n + x;
                    if (!cov.TryGetValue(pi, out var prev) || c > prev) cov[pi] = c;
                }
        }

        // Blend the surface texture into the atlas once per covered texel (capturing originals for undo).
        var px = _atlas.Rgba;
        foreach (var kv in cov)
        {
            float w = kv.Value * inten; if (w <= 0f) continue;
            int pi = kv.Key, x = pi % n, y = pi / n, idx = pi * 4;
            float wx = (x + 0.5f) / n * ws, wz = (y + 0.5f) / n * ws;
            var tc = tex.Sample(wx / tm, wz / tm);
            if (!_orig.ContainsKey(pi))
            {
                _orig[pi] = (uint)(px[idx] | (px[idx + 1] << 8) | (px[idx + 2] << 16) | (px[idx + 3] << 24));
                if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
                if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
            }
            px[idx]     = (byte)(px[idx]     + (tc.X * 255f - px[idx])     * w + 0.5f);
            px[idx + 1] = (byte)(px[idx + 1] + (tc.Y * 255f - px[idx + 1]) * w + 0.5f);
            px[idx + 2] = (byte)(px[idx + 2] + (tc.Z * 255f - px[idx + 2]) * w + 0.5f);
        }
    }

    /// <summary>Paint an ORIENTED road band: like <see cref="Sweep"/>, but the texture follows the road —
    /// per texel, u = signed lateral offset across the road width (so the texture's full width spans the road,
    /// lane markings centred) and v = arc length along the centerline / <paramref name="tileAlongMeters"/> (so the
    /// pattern repeats down the road and bends with every curve). <paramref name="pts"/> is the DENSIFIED spline
    /// centerline (RoadSpline.Resample) carrying per-sample half-width + accumulated arc length; each texel takes
    /// its NEAREST segment's frame, which is seam-free when segments are short (~1 m). Feathered + once-per-texel
    /// like Sweep, so edges stay clean and intensity can't over-saturate.</summary>
    public void SweepOriented(Texture2D tex, IReadOnlyList<(float X, float Z, float HalfW, float ArcLen)> pts,
                              float feather, float intensity, float tileAlongMeters, bool alongU = true)
    {
        if (pts.Count < 2) return;
        int n = _atlas.Width;
        float ws = _worldSize <= 0f ? 1f : _worldSize;
        float inten = Math.Clamp(intensity, 0f, 1f);
        float fth = MathF.Max(feather, 0.01f);
        float tileV = MathF.Max(tileAlongMeters, 0.1f);

        // Per-texel NEAREST-segment road frame: lateral distance (signed), half-width and arc length at the foot
        // of the perpendicular. Min-distance wins (vs Sweep's max-coverage) because the UV needs ONE owner segment.
        var best = new Dictionary<int, (float Dist, float Side, float HalfW, float Arc)>();
        for (int s = 0; s + 1 < pts.Count; s++)
        {
            float ax = pts[s].X, az = pts[s].Z, bx = pts[s + 1].X, bz = pts[s + 1].Z;
            float ex = bx - ax, ez = bz - az;
            float elen2 = ex * ex + ez * ez; if (elen2 < 1e-6f) continue;
            float reach = MathF.Max(pts[s].HalfW, pts[s + 1].HalfW) + fth;
            int x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(ax, bx) - reach) / ws * n));
            int x1 = Math.Min(n - 1, (int)MathF.Ceiling((MathF.Max(ax, bx) + reach) / ws * n));
            int y0 = Math.Max(0, (int)MathF.Floor((MathF.Min(az, bz) - reach) / ws * n));
            int y1 = Math.Min(n - 1, (int)MathF.Ceiling((MathF.Max(az, bz) + reach) / ws * n));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float wx = (x + 0.5f) / n * ws, wz = (y + 0.5f) / n * ws;
                    float tparam = Math.Clamp(((wx - ax) * ex + (wz - az) * ez) / elen2, 0f, 1f);
                    float ddx = wx - (ax + ex * tparam), ddz = wz - (az + ez * tparam);
                    float dist = MathF.Sqrt(ddx * ddx + ddz * ddz);
                    float hw = pts[s].HalfW + (pts[s + 1].HalfW - pts[s].HalfW) * tparam;
                    if (dist > hw + fth) continue;
                    int pi = y * n + x;
                    if (best.TryGetValue(pi, out var prev) && prev.Dist <= dist) continue;
                    float side = MathF.Sign(ex * ddz - ez * ddx);                       // which side of the centerline
                    float a = pts[s].ArcLen + (pts[s + 1].ArcLen - pts[s].ArcLen) * tparam;
                    best[pi] = (dist, side, hw, a);
                }
        }

        var px = _atlas.Rgba;
        foreach (var kv in best)
        {
            var (dist, side, hw, a) = kv.Value;
            float w = Math.Clamp((hw + fth - dist) / fth, 0f, 1f) * inten;
            if (w <= 0f) continue;
            int pi = kv.Key, x = pi % n, y = pi / n, idx = pi * 4;
            // across: full texture width spans the road (centerline = 0.5), clamped so the feather zone past the
            // edge extends the texture's border instead of wrapping the opposite edge across. along: arc / tile.
            float across = Math.Clamp(0.5f + side * dist / MathF.Max(hw * 2f, 0.2f), 0.003f, 0.997f);
            float along = a / tileV;
            // BF/Editor42 road strips are drawn with the road running ALONG the image's HORIZONTAL (U) axis
            // (lane lines horizontal), so by default along->U, across->V. alongU=false for a vertical-strip texture.
            var tc = alongU ? tex.Sample(along, across) : tex.Sample(across, along);
            if (!_orig.ContainsKey(pi))
            {
                _orig[pi] = (uint)(px[idx] | (px[idx + 1] << 8) | (px[idx + 2] << 16) | (px[idx + 3] << 24));
                if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
                if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
            }
            px[idx]     = (byte)(px[idx]     + (tc.X * 255f - px[idx])     * w + 0.5f);
            px[idx + 1] = (byte)(px[idx + 1] + (tc.Y * 255f - px[idx + 1]) * w + 0.5f);
            px[idx + 2] = (byte)(px[idx + 2] + (tc.Z * 255f - px[idx + 2]) * w + 0.5f);
        }
    }

    /// <summary>Coalesce the stroke into one undoable command (null if nothing changed). <paramref name="reupload"/>
    /// re-uploads a (x,y,w,h) atlas rectangle to the GPU on apply/undo.</summary>
    public AtlasStrokeCommand? Finish(Action<int, int, int, int> reupload)
    {
        if (_orig.Count == 0) return null;
        int n = _atlas.Width, w = _maxX - _minX + 1, h = _maxY - _minY + 1;
        var before = new byte[w * h * 4];
        var after = new byte[w * h * 4];
        var px = _atlas.Rgba;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int gx = _minX + xx, gy = _minY + yy, pi = gy * n + gx, src = pi * 4, dst = (yy * w + xx) * 4;
                after[dst] = px[src]; after[dst + 1] = px[src + 1]; after[dst + 2] = px[src + 2]; after[dst + 3] = px[src + 3];
                if (_orig.TryGetValue(pi, out var o))
                { before[dst] = (byte)o; before[dst + 1] = (byte)(o >> 8); before[dst + 2] = (byte)(o >> 16); before[dst + 3] = (byte)(o >> 24); }
                else { before[dst] = px[src]; before[dst + 1] = px[src + 1]; before[dst + 2] = px[src + 2]; before[dst + 3] = px[src + 3]; }
            }
        return new AtlasStrokeCommand(_atlas, _minX, _minY, w, h, before, after, reupload);
    }
}

/// <summary>An undoable terrain-texture paint: the before/after RGBA of the affected atlas rectangle, riding
/// the shared object/terrain/material undo stack. Apply/Undo blit the rect into the atlas and re-upload it.</summary>
sealed class AtlasStrokeCommand : IEditCommand
{
    private readonly Texture2D _atlas;
    private readonly int _x0, _y0, _w, _h;
    private readonly byte[] _before, _after;
    private readonly Action<int, int, int, int> _reupload;

    public AtlasStrokeCommand(Texture2D atlas, int x0, int y0, int w, int h, byte[] before, byte[] after, Action<int, int, int, int> reupload)
    { _atlas = atlas; _x0 = x0; _y0 = y0; _w = w; _h = h; _before = before; _after = after; _reupload = reupload; }

    private void Blit(byte[] src)
    {
        int n = _atlas.Width; var px = _atlas.Rgba;
        for (int yy = 0; yy < _h; yy++)
            for (int xx = 0; xx < _w; xx++)
            {
                int dst = ((_y0 + yy) * n + (_x0 + xx)) * 4, s = (yy * _w + xx) * 4;
                px[dst] = src[s]; px[dst + 1] = src[s + 1]; px[dst + 2] = src[s + 2]; px[dst + 3] = src[s + 3];
            }
    }

    public void Apply(StaticObjectsFile _) { Blit(_after); _reupload(_x0, _y0, _w, _h); }
    public void Undo(StaticObjectsFile _) { Blit(_before); _reupload(_x0, _y0, _w, _h); }
    public string ToWire() => $"ATLAS {_x0} {_y0} {_w} {_h}";   // not collab-synced (atlas is editor-side)
}

/// <summary>Accumulates one continuous AI-path paint drag on a vehicle's WORLD-GRID navmap (1 byte/cell,
/// 0x00 = passable / 0xFF = blocked), capturing each cell's original value the first time the brush touches it
/// so the whole drag coalesces into a single undo step — the navmap analogue of <see cref="AtlasPaintStroke"/>.</summary>
sealed class AiNavStroke
{
    private readonly byte[] _buf;
    private readonly int _side, _veh;
    private readonly Dictionary<int, byte> _orig = new();   // cell index -> original value
    private int _minX = int.MaxValue, _minY = int.MaxValue, _maxX = int.MinValue, _maxY = int.MinValue;

    public AiNavStroke(byte[] buf, int side, int veh) { _buf = buf; _side = side; _veh = veh; }

    /// <summary>Record cell (x,y)'s current value before the brush overwrites it (idempotent per cell). Call
    /// from the dab loop just before writing the new value.</summary>
    public void Touch(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _side || y >= _side) return;
        int pi = y * _side + x;
        if (_orig.ContainsKey(pi)) return;
        _orig[pi] = _buf[pi];
        if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
    }

    /// <summary>Coalesce into one undoable command (null if nothing changed). <paramref name="onChanged"/>(veh)
    /// is invoked on apply/undo/redo so the viewer can mark the vehicle dirty + refresh its overlay.</summary>
    public AiNavStrokeCommand? Finish(Action<int> onChanged)
    {
        if (_orig.Count == 0) return null;
        int w = _maxX - _minX + 1, h = _maxY - _minY + 1;
        var before = new byte[w * h];
        var after = new byte[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int gx = _minX + xx, gy = _minY + yy, pi = gy * _side + gx, dst = yy * w + xx;
                after[dst] = _buf[pi];
                before[dst] = _orig.TryGetValue(pi, out var o) ? o : _buf[pi];
            }
        return new AiNavStrokeCommand(_buf, _veh, _side, _minX, _minY, w, h, before, after, onChanged);
    }
}

/// <summary>An undoable AI-path navmap stroke: the before/after of the affected rectangle (1 byte/cell), riding
/// the shared undo stack. Apply/Undo blit the rect into the captured per-vehicle buffer and notify the viewer.
/// The buffer reference is the vehicle's persistent navmap array, so undo works even when another vehicle is the
/// active view. Editor-side only (not collab-synced), like <see cref="AtlasStrokeCommand"/>.</summary>
sealed class AiNavStrokeCommand : IEditCommand
{
    private readonly byte[] _buf;
    private readonly int _veh, _side, _x0, _y0, _w, _h;
    private readonly byte[] _before, _after;
    private readonly Action<int> _onChanged;

    public AiNavStrokeCommand(byte[] buf, int veh, int side, int x0, int y0, int w, int h, byte[] before, byte[] after, Action<int> onChanged)
    { _buf = buf; _veh = veh; _side = side; _x0 = x0; _y0 = y0; _w = w; _h = h; _before = before; _after = after; _onChanged = onChanged; }

    private void Blit(byte[] src)
    {
        for (int yy = 0; yy < _h; yy++)
            for (int xx = 0; xx < _w; xx++)
                _buf[(_y0 + yy) * _side + (_x0 + xx)] = src[yy * _w + xx];
    }

    public void Apply(StaticObjectsFile _) { Blit(_after); _onChanged(_veh); }
    public void Undo(StaticObjectsFile _) { Blit(_before); _onChanged(_veh); }
    public string ToWire() => $"AINAV {_veh} {_x0} {_y0} {_w} {_h}";   // not collab-synced (navmap is editor-side)
}
