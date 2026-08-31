namespace RefractorForge.Formats.Terrain;

/// <summary>How a brush's influence falls off from centre (1) to edge (0).</summary>
public enum BrushFalloff { Smooth, Linear, Constant, Gaussian }

/// <summary>Heightmap sculpt operations, mirroring the staples of Battlecraft's terrain tools.</summary>
public enum BrushMode { Raise, Lower, Smooth, Flatten, Set }

/// <summary>
/// A terrain sculpt brush. <see cref="Strength"/> means metres-at-centre per dab for
/// Raise/Lower, and a 0..1 blend amount at centre for Smooth/Flatten/Set.
/// </summary>
public readonly record struct TerrainBrush(
    BrushMode Mode,
    float RadiusMeters,
    float Strength,
    BrushFalloff Falloff = BrushFalloff.Smooth,
    float? TargetMeters = null,     // Flatten/Set target; null => locked to the height under the brush centre
    BrushMask? Shape = null,        // optional bitmap brush shape (Battlecraft brushes\*.bmp); null => radial falloff
    bool Square = false);           // procedural square footprint (Chebyshev) instead of a disc, when Shape is null

/// <summary>
/// An undoable rectangular heightmap edit: the before/after samples of the affected region.
/// Strokes are coalesced into one of these so a whole drag is a single undo step.
/// </summary>
public sealed class TerrainEdit
{
    public int X0 { get; }
    public int Y0 { get; }
    public int W { get; }
    public int H { get; }
    private readonly ushort[] _before;
    private readonly ushort[] _after;

    public TerrainEdit(int x0, int y0, int w, int h, ushort[] before, ushort[] after)
    { X0 = x0; Y0 = y0; W = w; H = h; _before = before; _after = after; }

    public int CellCount => W * H;
    public void Undo(Heightmap hm) => Blit(hm, _before);
    public void Redo(Heightmap hm) => Blit(hm, _after);

    private void Blit(Heightmap hm, ushort[] src)
    {
        for (int yy = 0; yy < H; yy++)
            for (int xx = 0; xx < W; xx++)
                hm[X0 + xx, Y0 + yy] = src[yy * W + xx];
    }
}

/// <summary>Undo/redo stack for terrain edits (separate from the object-edit history).</summary>
public sealed class TerrainEditHistory
{
    private readonly Heightmap _hm;
    private readonly Stack<TerrainEdit> _undo = new();
    private readonly Stack<TerrainEdit> _redo = new();
    public TerrainEditHistory(Heightmap hm) => _hm = hm;

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    public void Push(TerrainEdit e) { _undo.Push(e); _redo.Clear(); }
    public bool Undo() { if (_undo.Count == 0) return false; var e = _undo.Pop(); e.Undo(_hm); _redo.Push(e); return true; }
    public bool Redo() { if (_redo.Count == 0) return false; var e = _redo.Pop(); e.Redo(_hm); _undo.Push(e); return true; }
}

/// <summary>
/// Sculpts a <see cref="Heightmap"/> with radial brushes, using the level's
/// <see cref="TerrainConfig"/> to convert between world metres and 16-bit samples. Pure and
/// engine-agnostic, so it is unit-tested headlessly and reused by the GUI unchanged.
/// </summary>
public sealed class TerrainEditor
{
    public Heightmap Heightmap { get; }
    public TerrainConfig Config { get; }

    public TerrainEditor(Heightmap hm, TerrainConfig cfg) { Heightmap = hm; Config = cfg; }

    /// <summary>Begin a multi-dab stroke (a mouse drag). Apply <see cref="TerrainStroke.Dab"/> repeatedly, then <see cref="TerrainStroke.Finish"/>.</summary>
    public TerrainStroke BeginStroke() => new(this);

    /// <summary>One-shot stamp: a single brush dab, returned as an undoable edit (null if nothing changed).</summary>
    public TerrainEdit? Stamp(float worldX, float worldZ, in TerrainBrush brush)
    {
        var s = BeginStroke();
        s.Dab(worldX, worldZ, brush);
        return s.Finish();
    }

    /// <summary>Weight in [0,1] for a normalized distance t in [0,1] (0 = centre, 1 = edge).</summary>
    internal static float Weight(BrushFalloff f, float t)
    {
        float u = 1f - Math.Clamp(t, 0f, 1f);   // 1 at centre, 0 at edge
        return f switch
        {
            BrushFalloff.Constant => 1f,
            BrushFalloff.Linear => u,
            BrushFalloff.Gaussian => MathF.Exp(-(t * t) * 6f),
            _ => u * u * (3f - 2f * u),          // Smooth (smoothstep)
        };
    }
}

/// <summary>
/// One continuous sculpt stroke. Captures each cell's original value the first time it is touched,
/// so <see cref="Finish"/> can emit a single coalesced <see cref="TerrainEdit"/> for the whole drag.
/// </summary>
public sealed class TerrainStroke
{
    private readonly Heightmap _hm;
    private readonly TerrainConfig _cfg;
    private readonly Dictionary<int, ushort> _orig = new();
    private int _minX = int.MaxValue, _minY = int.MaxValue, _maxX = int.MinValue, _maxY = int.MinValue;
    private float? _lockedTargetMeters;

    internal TerrainStroke(TerrainEditor ed) { _hm = ed.Heightmap; _cfg = ed.Config; }

    public bool Empty => _orig.Count == 0;

    /// <summary>Stamp the brush once, centred at world (x, z) metres.</summary>
    public void Dab(float worldX, float worldZ, in TerrainBrush brush)
    {
        float sp = _cfg.HorizontalSpacing; if (sp <= 0f) sp = 1f;
        float cgx = worldX / sp, cgy = worldZ / sp;                 // brush centre in grid coords
        float rg = MathF.Max(brush.RadiusMeters / sp, 1e-4f);       // radius in grid cells
        int x0 = Math.Max(0, (int)MathF.Floor(cgx - rg));
        int x1 = Math.Min(_hm.Width - 1, (int)MathF.Ceiling(cgx + rg));
        int y0 = Math.Max(0, (int)MathF.Floor(cgy - rg));
        int y1 = Math.Min(_hm.Height - 1, (int)MathF.Ceiling(cgy + rg));
        if (x0 > x1 || y0 > y1) return;

        float rawPerMetre = 256f / (_cfg.YScale <= 0f ? 1f : _cfg.YScale);

        // Resolve a target height for Flatten/Set (locked for the whole stroke).
        float targetRaw = 0f;
        if (brush.Mode is BrushMode.Flatten or BrushMode.Set)
        {
            float tm = brush.TargetMeters ?? (_lockedTargetMeters ??= _cfg.HeightToMeters(SampleNearest(cgx, cgy)));
            targetRaw = _cfg.MetersToRaw(tm);
        }

        // Smooth reads neighbours from a pre-dab snapshot so the result is order-independent.
        ushort[]? pre = null; int pw = 0;
        if (brush.Mode == BrushMode.Smooth)
        {
            pw = x1 - x0 + 1; int ph = y1 - y0 + 1;
            pre = new ushort[pw * ph];
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    pre[(y - y0) * pw + (x - x0)] = _hm[x, y];
        }

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float w;
                if (brush.Shape is BrushMask shape)
                {
                    // Bitmap brush: sample the shape over the [centre ± radius] footprint; the bitmap is the
                    // falloff, so the whole square box contributes (not just the inscribed circle).
                    w = shape.Sample((x - (cgx - rg)) / (2f * rg), (y - (cgy - rg)) / (2f * rg));
                }
                else
                {
                    float dx = x - cgx, dy = y - cgy;
                    if (brush.Square)
                    {
                        // Square brush: a FLAT-TOPPED axis-aligned square so the raise actually looks square. A
                        // radial-style falloff on the Chebyshev distance gives square contours but tapers the
                        // corners/edges to ~0, which reads as round (especially at small radii). Instead the inner
                        // square is full strength and the chosen falloff shapes only the outer border: Constant =>
                        // a hard square, Smooth/Gaussian/Linear => a square with a soft edge.
                        float tc = MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) / rg;   // Chebyshev 0..>1
                        if (tc > 1f) continue;
                        const float border = 0.30f;                                // outer fraction that tapers
                        w = tc <= 1f - border ? 1f : TerrainEditor.Weight(brush.Falloff, (tc - (1f - border)) / border);
                    }
                    else
                    {
                        float t = MathF.Sqrt(dx * dx + dy * dy) / rg;              // radial disc, 0..>1
                        if (t > 1f) continue;
                        w = TerrainEditor.Weight(brush.Falloff, t);
                    }
                }
                if (w <= 0f) continue;

                int idx = y * _hm.Width + x;
                if (!_orig.ContainsKey(idx)) { _orig[idx] = _hm.Samples[idx]; Grow(x, y); }

                float cur = _hm.Samples[idx];
                float nv = cur;
                switch (brush.Mode)
                {
                    case BrushMode.Raise: nv = cur + brush.Strength * rawPerMetre * w; break;
                    case BrushMode.Lower: nv = cur - brush.Strength * rawPerMetre * w; break;
                    case BrushMode.Flatten: nv = Lerp(cur, targetRaw, Math.Clamp(brush.Strength, 0f, 1f) * w); break;
                    case BrushMode.Set: nv = Lerp(cur, targetRaw, w); break;
                    case BrushMode.Smooth:
                        float avg = NeighbourAverage(pre!, pw, x0, y0, x1, y1, x, y);
                        nv = Lerp(cur, avg, Math.Clamp(brush.Strength, 0f, 1f) * w);
                        break;
                }
                _hm.Samples[idx] = (ushort)Math.Clamp((int)MathF.Round(nv), 0, ushort.MaxValue);
            }
    }

    // ---- Per-vertex editing (Battlecraft's point manipulation) ----
    //
    // These go through the SAME stroke bookkeeping as a brush dab - record each cell's original once, grow the
    // bbox - so they coalesce into the same TerrainEdit and inherit undo and, more importantly, the collab path:
    // the viewer broadcasts a stroke by RECT, re-reading the live heightmap, so a 1x1 rect needs no protocol
    // change at all. Writing the heightmap directly instead would look right locally and be invisible to peers.

    /// <summary>Set one heightmap vertex to an exact height in metres.</summary>
    public void SetVertex(int gx, int gy, float metres)
    {
        if (gx < 0 || gy < 0 || gx >= _hm.Width || gy >= _hm.Height) return;
        int idx = gy * _hm.Width + gx;
        if (!_orig.ContainsKey(idx)) { _orig[idx] = _hm.Samples[idx]; Grow(gx, gy); }
        _hm.Samples[idx] = _cfg.MetersToRaw(metres);
    }

    /// <summary>Raise (or lower, with a negative value) one vertex by a number of metres.</summary>
    public void NudgeVertex(int gx, int gy, float deltaMetres)
    {
        if (gx < 0 || gy < 0 || gx >= _hm.Width || gy >= _hm.Height) return;
        SetVertex(gx, gy, _cfg.HeightToMeters(_hm[gx, gy]) + deltaMetres);
    }

    /// <summary>Height of one vertex, in metres.</summary>
    public float VertexHeight(int gx, int gy) =>
        gx < 0 || gy < 0 || gx >= _hm.Width || gy >= _hm.Height ? 0f : _cfg.HeightToMeters(_hm[gx, gy]);

    /// <summary>Blend the ring around a vertex toward it, so a pulled point leaves a slope instead of a spike.
    /// This is what Battlecraft's auto-smooth radius does, and without it per-vertex editing produces terrain no
    /// amount of later smoothing quite repairs.</summary>
    public void SmoothAround(int gx, int gy, int radiusCells, float strength = 1f)
    {
        if (radiusCells <= 0) return;
        strength = Math.Clamp(strength, 0f, 1f);

        // Read from a snapshot so the result does not depend on which cell is visited first.
        int x0 = Math.Max(0, gx - radiusCells), x1 = Math.Min(_hm.Width - 1, gx + radiusCells);
        int y0 = Math.Max(0, gy - radiusCells), y1 = Math.Min(_hm.Height - 1, gy + radiusCells);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var pre = new ushort[w * h];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                pre[(y - y0) * w + (x - x0)] = _hm[x, y];

        ushort Pre(int x, int y) => pre[(Math.Clamp(y, y0, y1) - y0) * w + (Math.Clamp(x, x0, x1) - x0)];

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (x == gx && y == gy) continue;                 // the moved vertex keeps exactly what was asked for
                float dx = x - gx, dy = y - gy;
                float t = MathF.Sqrt(dx * dx + dy * dy) / radiusCells;
                if (t > 1f) continue;
                float fall = 1f - t;
                fall = fall * fall * (3f - 2f * fall);

                int sum = 0, n = 0;
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++) { sum += Pre(x + ox, y + oy); n++; }
                float avg = (float)sum / n;

                int idx = y * _hm.Width + x;
                if (!_orig.ContainsKey(idx)) { _orig[idx] = _hm.Samples[idx]; Grow(x, y); }
                _hm.Samples[idx] = (ushort)Math.Clamp((int)MathF.Round(Lerp(_hm.Samples[idx], avg, fall * strength)), 0, ushort.MaxValue);
            }
    }

    /// <summary>Coalesce the stroke into one undoable edit (null if nothing changed).</summary>
    public TerrainEdit? Finish()
    {
        if (_orig.Count == 0) return null;
        int w = _maxX - _minX + 1, h = _maxY - _minY + 1;
        var before = new ushort[w * h];
        var after = new ushort[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int gx = _minX + xx, gy = _minY + yy, idx = gy * _hm.Width + gx;
                after[yy * w + xx] = _hm.Samples[idx];
                before[yy * w + xx] = _orig.TryGetValue(idx, out var o) ? o : _hm.Samples[idx];
            }
        return new TerrainEdit(_minX, _minY, w, h, before, after);
    }

    private void Grow(int x, int y)
    {
        if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
    }

    private ushort SampleNearest(float gx, float gy)
    {
        int x = Math.Clamp((int)MathF.Round(gx), 0, _hm.Width - 1);
        int y = Math.Clamp((int)MathF.Round(gy), 0, _hm.Height - 1);
        return _hm[x, y];
    }

    private static float NeighbourAverage(ushort[] pre, int pw, int x0, int y0, int x1, int y1, int x, int y)
    {
        float sum = 0f; int n = 0;
        for (int yy = Math.Max(y0, y - 1); yy <= Math.Min(y1, y + 1); yy++)
            for (int xx = Math.Max(x0, x - 1); xx <= Math.Min(x1, x + 1); xx++)
            { sum += pre[(yy - y0) * pw + (xx - x0)]; n++; }
        return n > 0 ? sum / n : pre[(y - y0) * pw + (x - x0)];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
