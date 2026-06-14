namespace RefractorForge.Formats.Terrain;

/// <summary>
/// The terrain material map: one 8-bit material index per cell, row-major. This is the file BFV
/// references as <c>GeometryTemplate.materialMap</c> (e.g. <c>MaterialMap.raw</c>) and what the
/// texture brush paints. Confirmed against Operation_Irving: 512×512 bytes for materialSize 512,
/// indices selecting which detail surface (path, riverbed, grass, …) applies at each cell.
/// </summary>
public sealed class MaterialMap
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Row-major material indices, length = Width * Height.</summary>
    public byte[] Samples { get; }

    public MaterialMap(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        Samples = new byte[(long)width * height <= int.MaxValue ? width * height
            : throw new ArgumentOutOfRangeException(nameof(width), "Grid too large for a single array.")];
    }

    public byte this[int x, int y]
    {
        get => Samples[y * Width + x];
        set => Samples[y * Width + x] = value;
    }

    public static MaterialMap LoadRaw(string path, int width, int height)
    {
        var bytes = File.ReadAllBytes(path);
        long expected = (long)width * height;
        if (bytes.Length < expected)
            throw new InvalidDataException($"File '{path}' is {bytes.Length} bytes; {width}x{height} 8-bit needs {expected}.");
        var mm = new MaterialMap(width, height);
        Array.Copy(bytes, mm.Samples, mm.Samples.Length);
        return mm;
    }

    /// <summary>BFV material maps are materialSize × materialSize, 1 byte/cell (grid side == materialSize).</summary>
    public static MaterialMap LoadForMaterialSize(string path, int materialSize) => LoadRaw(path, materialSize, materialSize);

    /// <summary>Load straight from in-memory bytes (e.g. read out of a packed .rfa) — same layout as the file.</summary>
    public static MaterialMap FromBytes(byte[] bytes, int width, int height)
    {
        long expected = (long)width * height;
        if (bytes.Length < expected)
            throw new InvalidDataException($"material map is {bytes.Length} bytes; {width}x{height} 8-bit needs {expected}.");
        var mm = new MaterialMap(width, height);
        Array.Copy(bytes, mm.Samples, mm.Samples.Length);
        return mm;
    }

    public void SaveRaw(string path) => File.WriteAllBytes(path, Samples);

    public MaterialMap Clone()
    {
        var m = new MaterialMap(Width, Height);
        Array.Copy(Samples, m.Samples, Samples.Length);
        return m;
    }
}

/// <summary>
/// A texture brush: paints cells under the brush to <see cref="Material"/>. Because material is an
/// index (not a blendable value), <see cref="Hardness"/> controls the soft edge — a cell is painted
/// when its falloff weight ≥ (1 − Hardness), so Hardness 1 paints the full radius and lower values
/// paint only the core.
/// </summary>
public readonly record struct MaterialBrush(
    byte Material,
    float RadiusMeters,
    float Hardness = 1f,
    BrushFalloff Falloff = BrushFalloff.Smooth,
    BrushMask? Shape = null,        // optional bitmap brush shape (Battlecraft brushes\*.bmp); null => radial/square
    bool Square = false);           // procedural square footprint (Chebyshev) instead of a disc, when Shape is null

/// <summary>An undoable rectangular material edit (before/after indices of the affected region).</summary>
public sealed class MaterialEdit
{
    public int X0 { get; }
    public int Y0 { get; }
    public int W { get; }
    public int H { get; }
    private readonly byte[] _before;
    private readonly byte[] _after;

    public MaterialEdit(int x0, int y0, int w, int h, byte[] before, byte[] after)
    { X0 = x0; Y0 = y0; W = w; H = h; _before = before; _after = after; }

    public int CellCount => W * H;
    public void Undo(MaterialMap m) => Blit(m, _before);
    public void Redo(MaterialMap m) => Blit(m, _after);

    private void Blit(MaterialMap m, byte[] src)
    {
        for (int yy = 0; yy < H; yy++)
            for (int xx = 0; xx < W; xx++)
                m[X0 + xx, Y0 + yy] = src[yy * W + xx];
    }
}

/// <summary>Undo/redo stack for material-map edits.</summary>
public sealed class MaterialEditHistory
{
    private readonly MaterialMap _m;
    private readonly Stack<MaterialEdit> _undo = new();
    private readonly Stack<MaterialEdit> _redo = new();
    public MaterialEditHistory(MaterialMap m) => _m = m;

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;
    public void Push(MaterialEdit e) { _undo.Push(e); _redo.Clear(); }
    public bool Undo() { if (_undo.Count == 0) return false; var e = _undo.Pop(); e.Undo(_m); _redo.Push(e); return true; }
    public bool Redo() { if (_redo.Count == 0) return false; var e = _redo.Pop(); e.Redo(_m); _undo.Push(e); return true; }
}

/// <summary>
/// Paints a <see cref="MaterialMap"/> with radial brushes (world-metre radius via the level's
/// <see cref="TerrainConfig"/>). Pure/engine-agnostic — unit-tested headlessly, reused by the GUI.
/// </summary>
public sealed class MaterialPainter
{
    public MaterialMap Map { get; }
    public TerrainConfig Config { get; }

    public MaterialPainter(MaterialMap map, TerrainConfig cfg) { Map = map; Config = cfg; }

    public MaterialStroke BeginStroke() => new(this);

    public MaterialEdit? Stamp(float worldX, float worldZ, in MaterialBrush brush)
    {
        var s = BeginStroke();
        s.Dab(worldX, worldZ, brush);
        return s.Finish();
    }

    internal MaterialMap MapRef => Map;
    internal TerrainConfig Cfg => Config;
}

/// <summary>One continuous paint stroke; coalesces the whole drag into a single undoable edit.</summary>
public sealed class MaterialStroke
{
    private readonly MaterialMap _m;
    private readonly TerrainConfig _cfg;
    private readonly Dictionary<int, byte> _orig = new();
    private int _minX = int.MaxValue, _minY = int.MaxValue, _maxX = int.MinValue, _maxY = int.MinValue;

    internal MaterialStroke(MaterialPainter p) { _m = p.MapRef; _cfg = p.Cfg; }

    public bool Empty => _orig.Count == 0;

    public void Dab(float worldX, float worldZ, in MaterialBrush brush)
    {
        float sp = _cfg.HorizontalSpacing; if (sp <= 0f) sp = 1f;
        float cgx = worldX / sp, cgy = worldZ / sp;
        float rg = MathF.Max(brush.RadiusMeters / sp, 1e-4f);
        int x0 = Math.Max(0, (int)MathF.Floor(cgx - rg));
        int x1 = Math.Min(_m.Width - 1, (int)MathF.Ceiling(cgx + rg));
        int y0 = Math.Max(0, (int)MathF.Floor(cgy - rg));
        int y1 = Math.Min(_m.Height - 1, (int)MathF.Ceiling(cgy + rg));
        if (x0 > x1 || y0 > y1) return;

        float threshold = 1f - Math.Clamp(brush.Hardness, 0f, 1f);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cgx, dy = y - cgy;
                float w;
                if (brush.Shape is BrushMask shape)
                {
                    // Bitmap brush: sample the shape over the [centre ± radius] box (the bitmap is its own falloff).
                    w = shape.Sample((x - (cgx - rg)) / (2f * rg), (y - (cgy - rg)) / (2f * rg));
                }
                else
                {
                    // Square brush => Chebyshev (box) distance; else radial (disc).
                    float t = brush.Square ? MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) / rg
                                           : MathF.Sqrt(dx * dx + dy * dy) / rg;
                    if (t > 1f) continue;
                    w = TerrainEditor.Weight(brush.Falloff, t);
                }
                if (w < threshold) continue;            // outside the (soft) edge
                int idx = y * _m.Width + x;
                if (_m.Samples[idx] == brush.Material && _orig.ContainsKey(idx)) continue;
                if (!_orig.ContainsKey(idx)) { _orig[idx] = _m.Samples[idx]; Grow(x, y); }
                _m.Samples[idx] = brush.Material;
            }
    }

    public MaterialEdit? Finish()
    {
        if (_orig.Count == 0) return null;
        int w = _maxX - _minX + 1, h = _maxY - _minY + 1;
        var before = new byte[w * h];
        var after = new byte[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int gx = _minX + xx, gy = _minY + yy, idx = gy * _m.Width + gx;
                after[yy * w + xx] = _m.Samples[idx];
                before[yy * w + xx] = _orig.TryGetValue(idx, out var o) ? o : _m.Samples[idx];
            }
        return new MaterialEdit(_minX, _minY, w, h, before, after);
    }

    private void Grow(int x, int y)
    {
        if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
    }
}
